using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.PrintAgent;

/// <summary>Puntos de la secuencia de envío donde las pruebas inyectan fallas (T07) o pausas (T29).</summary>
public enum SenderPoint
{
    /// <summary>Después del commit de TX-B y antes de abrir el canal.</summary>
    AfterClaimCommit,

    /// <summary>A mitad de la escritura.</summary>
    DuringWrite,

    /// <summary>Después de escribir y antes de TX-C.</summary>
    AfterWriteBeforeResult,
}

/// <summary>Inyección de fallas en el sender (en producción, ninguna).</summary>
public interface ISenderFaults
{
    Task OnPointAsync(SenderPoint point, Guid attemptId, CancellationToken ct);
}

/// <summary>Compuerta física (PLC_WINDOW_TAG): el ciclo está en la zona de impresión con una lectura fresca.</summary>
public interface IPhysicalGate
{
    Task<bool> IsOpenForAsync(CycleIdentity cycle, int freshnessMs, CancellationToken ct);
}

public sealed record SendResult(Guid PrintJobId, ClaimOutcome Claim, WriteOutcome? Write, bool WriteInvoked);

/// <summary>El sender se detuvo porque no pudo registrar el resultado de un envío (TX-C); el arranque siguiente ejecuta TX-R0.</summary>
public sealed class SenderHaltedException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Sender único de una impresora (I11): toma el siguiente job READY en orden de envío (época por
/// AcceptedAt, después CycleId), lo reclama con TX-B (que confirma antes de cualquier I/O, I19), hace la
/// segunda comprobación con el reloj monotónico, la estación y la compuerta justo antes de escribir y
/// cierra con TX-C en una transacción nueva (I20). NOT_SENT solo si la escritura nunca se invocó; cualquier
/// error desde que se invoca es UNKNOWN (I02). Nunca reintenta un envío que pudo ocurrir (I03).
/// </summary>
public sealed class PrinterSenderService(
    string printerId, SqlDb db, ClaimService claims, TxcWriteResult txc, StationConfigStore stations, LeaseGrant lease, IPrinterTransport transport,
    EdgeClock clock, CartonMetrics metrics, ISenderFaults? faults = null, IPhysicalGate? gate = null, ILogger<PrinterSenderService>? logger = null,
    EvidenceService? evidence = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<PrinterSenderService>.Instance;

    public string PrinterId => printerId;

    /// <summary>El siguiente job READY de la impresora en orden de envío, o null.</summary>
    public Task<Guid?> NextReadyAsync(CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT TOP (1) pj.PrintJobId
                FROM dbo.PrintJob AS pj
                INNER JOIN dbo.PlcEpochHistory AS eh ON eh.SiteId = pj.SiteId AND eh.StationId = pj.StationId AND eh.PlcEpoch = pj.PlcEpoch
                WHERE pj.PrinterId = @printer AND pj.Status = 'READY'
                ORDER BY eh.AcceptedAt, pj.CycleId;
                """);
            cmd.NVarChar("@printer", printerId, 64);
            return await cmd.ExecuteScalarAsync(ct) is Guid id ? (Guid?)id : null;
        }, ct);

    /// <summary>Envía el siguiente job READY; null si no hay ninguno.</summary>
    public async Task<SendResult?> SendNextAsync(CancellationToken ct) =>
        await NextReadyAsync(ct) is { } jobId ? await SendAsync(jobId, ct) : null;

    public async Task<SendResult> SendAsync(Guid printJobId, CancellationToken ct)
    {
        using var activity = CartonTelemetry.ActivitySource.StartActivity("carton.send");
        activity?.SetTag(CartonTelemetry.Tags.PrintJobId, printJobId);
        activity?.SetTag(CartonTelemetry.Tags.PrinterId, printerId);

        var claimedTs = clock.GetTimestamp();
        var claim = await claims.TryClaimAsync(printJobId, lease, ct);
        if (claim is not { Claimed: true })
        {
            return new SendResult(printJobId, claim!, null, false);
        }

        var context = claim.Context;
        var attemptId = claim.AttemptId;
        activity?.SetTag(CartonTelemetry.Tags.AttemptId, attemptId);
        activity?.SetTag(CartonTelemetry.Tags.CorrelationId, context.Job.DetectionId);
        activity?.SetTag(CartonTelemetry.Tags.LabelSerial, context.Job.LabelSerial);

        // Plazo monotónico: lo que quedaba al reclamar, medido desde la marca tomada antes del claim.
        var remaining = context.EffectiveDeadline.AddMilliseconds(-context.EffectiveSendMarginMs) - claim.ClaimedAtUtc;
        var deadlineTs = clock.AddToTimestamp(claimedTs, remaining);
        var backoff = context.Frozen.RetryBackoffMs;
        var maxAttempts = context.EffectiveMaxAutoAttempts;

        if (faults is not null)
        {
            await faults.OnPointAsync(SenderPoint.AfterClaimCommit, attemptId, ct);
        }

        // Lo que necesita I/O va antes de la comprobación final (DOCUMENTO_PRINCIPAL §10.2, paso 2): la estación
        // (leída ahora, en memoria al escribir), la compuerta física y el canal.
        var station = await stations.GetActiveAsync(context.Job.SiteId, context.Job.StationId, ct);
        if (station is not { State.LabelingEnabled: true })
        {
            metrics.Increment(MetricNames.PresendCheckRejected, ("cause", "station"));
            return await NotSentAsync(printJobId, claim, attemptId, clock.UtcNow, "Segunda comprobación: estación bloqueada", NotSentCause.GateOrStation,
                backoff, maxAttempts);
        }

        if (context.EffectiveGateFreshnessMs is { } freshness
            && (gate is null || !await gate.IsOpenForAsync(context.Job.Identity, freshness, ct)))
        {
            metrics.Increment(MetricNames.PresendCheckRejected, ("cause", "gate"));
            return await NotSentAsync(printJobId, claim, attemptId, clock.UtcNow, "Segunda comprobación: compuerta física cerrada", NotSentCause.GateOrStation,
                backoff, maxAttempts);
        }

        // Abrir el canal no es escribir: si falla (o se cancela), la escritura nunca se invocó. Una conexión ociosa que
        // la impresora cerró no cuenta como abierta y se reabre. Si el plazo venció mientras tanto, es J08.
        try
        {
            if (!transport.Connected)
            {
                await transport.ConnectAsync(ct);
            }
        }
        catch (Exception ex)
        {
            transport.Reset();
            var expired = clock.GetTimestamp() >= deadlineTs;
            return await NotSentAsync(printJobId, claim, attemptId, clock.UtcNow, $"Conexión fallida: {ex.Message}",
                expired ? NotSentCause.DeadlinePassed : NotSentCause.ConnectionFailed, backoff, maxAttempts);
        }

        // Segunda comprobación: la última instrucción antes de escribir, sin I/O. Plazo con el reloj monotónico.
        var preSendCheckAt = clock.UtcNow;
        var checkTs = clock.GetTimestamp();
        if (checkTs >= deadlineTs)
        {
            metrics.Increment(MetricNames.PresendCheckRejected, ("cause", "deadline"));
            return await NotSentAsync(printJobId, claim, attemptId, preSendCheckAt, "Segunda comprobación: plazo vencido", NotSentCause.DeadlinePassed,
                backoff, maxAttempts);
        }

        // Desde aquí la escritura está invocada: cualquier error es UNKNOWN.
        var writeStartedAt = clock.UtcNow;
        var writeTs = clock.GetTimestamp();
        metrics.Record(MetricNames.PresendCheckToWriteMs, clock.GetElapsedTime(checkTs, writeTs).TotalMilliseconds);
        DateTime? deviceAcceptedAt;
        try
        {
            deviceAcceptedAt = await transport.SendAsync(context.Job.Payload,
                faults is null ? null : () => faults.OnPointAsync(SenderPoint.DuringWrite, attemptId, ct), ct);
        }
        catch (Exception ex)
        {
            transport.Reset();
            metrics.Increment(MetricNames.PrinterUnknown, ("printer", printerId));
            metrics.Increment(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.Unknown));
            _logger.LogWarning("Escritura interrumpida en {Printer} para {Job}: {Error}. Intento UNKNOWN, sin reintento automático.",
                printerId, printJobId, ex.Message);
            var unknown = await RecordResultAsync(attemptId,
                () => txc.UnknownAsync(attemptId, writeStartedAt, $"Escritura interrumpida: {ex.Message}", clock.UtcNow, CancellationToken.None));
            await ReapplyEvidenceAsync(context, claim.ClaimedAtUtc);
            return new SendResult(printJobId, claim, unknown, true);
        }

        var writeCompletedAt = clock.UtcNow;
        if (deviceAcceptedAt is not null)
        {
            metrics.Record(MetricNames.SendStartToDeviceAcceptMs, clock.GetElapsedTime(writeTs).TotalMilliseconds);
        }

        if (faults is not null)
        {
            await faults.OnPointAsync(SenderPoint.AfterWriteBeforeResult, attemptId, ct);
        }

        var transmitted = await RecordResultAsync(attemptId, () => txc.TransmittedAsync(attemptId, writeStartedAt, writeCompletedAt, preSendCheckAt,
            deviceAcceptedAt is { } accepted ? CanonicalTimestamp.Truncate(accepted) : null, clock.UtcNow, CancellationToken.None));
        metrics.Increment(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.Transmitted));
        await ReapplyEvidenceAsync(context, claim.ClaimedAtUtc);
        return new SendResult(printJobId, claim, transmitted, true);
    }

    /// <summary>La evidencia que llegó mientras el intento estaba en SENDING se aplica ahora (después de TX-C).</summary>
    private async Task ReapplyEvidenceAsync(JobContext context, DateTime claimedAt)
    {
        if (evidence is null)
        {
            return;
        }

        try
        {
            await evidence.ReapplyForJobAsync(context.Job, claimedAt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo reaplicar la evidencia del job {Job}; la aplica la próxima observación del job o el arranque",
                context.Job.PrintJobId);
        }
    }

    /// <summary>
    /// NOT_SENT (la escritura nunca se invocó), también al detener el servicio: sin el token de la operación, para que un
    /// apagado no deje el intento en SENDING y lo vuelva UNKNOWN al arrancar.
    /// </summary>
    private async Task<SendResult> NotSentAsync(
        Guid printJobId, ClaimOutcome claim, Guid attemptId, DateTime preSendCheckAt, string detail, NotSentCause cause, int backoffMs, byte maxAttempts)
    {
        metrics.Increment(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.NotSent));
        var outcome = await RecordResultAsync(attemptId,
            () => txc.NotSentAsync(attemptId, preSendCheckAt, detail, cause, backoffMs, maxAttempts, clock.UtcNow, CancellationToken.None));
        return new SendResult(printJobId, claim, outcome, false);
    }

    /// <summary>
    /// TX-C con dos reintentos cortos (es idempotente: solo actúa sobre el intento en SENDING). Si SQL sigue sin responder,
    /// el sender se detiene (<see cref="SenderHaltedException"/>): el intento queda en SENDING, la impresora no se usa y el
    /// arranque siguiente ejecuta TX-R0 (ESTRATEGIA_RECUPERACION §3, SQL Server).
    /// </summary>
    private Task<WriteOutcome> RecordResultAsync(Guid attemptId, Func<Task<WriteOutcome>> txcCall) =>
        ResultRecorder.RecordAsync(txcCall, printerId, attemptId, _logger);

    /// <summary>Bucle del sender: envía mientras haya jobs READY; si no, espera una señal o el intervalo de sondeo.</summary>
    public async Task RunAsync(TimeSpan pollInterval, TimeProvider loopTime, Func<CancellationToken, Task>? waitForSignal, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Solo un claim obtenido sigue de inmediato; un rechazo (estación bloqueada, lease, orden, etc.) espera el
                // intervalo de sondeo en lugar de volver a reclamar el mismo job en bucle.
                if (await SendNextAsync(ct) is { Claim.Claimed: true })
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SenderHaltedException)
            {
                _logger.LogError(ex, "Sender de {Printer}: error fuera de la escritura; se reintenta", printerId);
            }

            using var interval = new CancellationTokenSource(pollInterval, loopTime);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, interval.Token);
            try
            {
                if (waitForSignal is null)
                {
                    await Task.Delay(Timeout.Infinite, wait.Token);
                }
                else
                {
                    await waitForSignal(wait.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Venció el intervalo.
            }
        }
    }
}
