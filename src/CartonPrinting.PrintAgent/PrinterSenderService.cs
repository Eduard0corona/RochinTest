using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Signals;
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
/// AcceptedAt, después CycleId), lo reclama con TX-B (que confirma antes de cualquier I/O, I19), abre el canal,
/// relee la estación y la compuerta, hace la segunda comprobación con el reloj monotónico justo antes de escribir y
/// cierra con TX-C en una transacción nueva (I20). NOT_SENT solo si la escritura nunca se invocó; cualquier
/// error desde que se invoca es UNKNOWN (I02). Nunca reintenta un envío que pudo ocurrir (I03).
/// </summary>
public sealed class PrinterSenderService(
    string printerId, SqlDb db, ClaimService claims, TxcWriteResult txc, StationConfigStore stations, LeaseGrant lease, IPrinterTransport transport,
    EdgeClock clock, CartonMetrics metrics, ISenderFaults? faults = null, IPhysicalGate? gate = null, ILogger<PrinterSenderService>? logger = null,
    EvidenceService? evidence = null, SqlOutage? sqlOutage = null, SenderRecovery? recovery = null, LinePrinterReadiness? readiness = null)
{
    /// <summary>ESTRATEGIA_RECUPERACION §3, Impresora: "Tres fallos seguidos | Sender en pausa, LabelingReady en falso, alerta".</summary>
    public const int MaxConsecutiveDeviceFailures = 3;

    /// <summary>Con el sender en pausa, cada cuánto se prueba si la impresora vuelve a aceptar conexiones.</summary>
    public static readonly TimeSpan PausedProbeInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger = logger ?? NullLogger<PrinterSenderService>.Instance;
    private int _deviceFailures;
    private volatile bool _paused;
    private bool _recoverBeforeClaim;

    /// <summary>En pausa por fallos seguidos del dispositivo: no reclama hasta que la impresora vuelva a aceptar conexiones.</summary>
    public bool Paused => _paused;

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

        // Entre el claim y la escritura nada se invocó todavía: un rechazo, cualquier error (SQL, la compuerta) o un
        // apagado terminan en NOT_SENT y nunca dejan el intento en SENDING (I02; segunda revisión de la Fase 4, M-1). Con
        // el plazo monotónico vencido es J08; si no, J07 cuando falla el canal y J06 en lo demás (no consume intento).
        string? rejection = null;
        var cause = NotSentCause.GateOrStation;
        try
        {
            if (faults is not null)
            {
                await faults.OnPointAsync(SenderPoint.AfterClaimCommit, attemptId, ct);
            }

            // Abrir el canal no es escribir. Una conexión ociosa que la impresora cerró no cuenta como abierta y se reabre.
            try
            {
                if (!transport.Connected)
                {
                    await transport.ConnectAsync(ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                transport.Reset();
                (rejection, cause) = ($"Conexión fallida: {ex.Message}", NotSentCause.ConnectionFailed);
                NoteDeviceFailure("no acepta la conexión");
            }

            // Lo que necesita I/O va justo antes de la comprobación final (DOCUMENTO_PRINCIPAL §10.2, paso 2), ya con el
            // canal abierto: la estación (leída ahora, en memoria al escribir) y la compuerta física.
            if (rejection is null && await stations.GetActiveAsync(context.Job.SiteId, context.Job.StationId, ct) is not { State.LabelingEnabled: true })
            {
                metrics.Increment(MetricNames.PresendCheckRejected, ("cause", "station"));
                rejection = "Segunda comprobación: estación bloqueada";
            }
            else if (rejection is null && context.EffectiveGateFreshnessMs is { } freshness
                && (gate is null || !await gate.IsOpenForAsync(context.Job.Identity, freshness, ct)))
            {
                metrics.Increment(MetricNames.PresendCheckRejected, ("cause", "gate"));
                rejection = "Segunda comprobación: compuerta física cerrada";
            }
        }
        catch (Exception ex)
        {
            transport.Reset();
            _logger.LogWarning("Sender de {Printer}: error antes de escribir el job {Job}: {Error}. NOT_SENT.", printerId, printJobId, ex.Message);
            (rejection, cause) = ($"Error antes de escribir: {ex.Message}", NotSentCause.GateOrStation);
        }

        if (rejection is not null)
        {
            var expired = clock.GetTimestamp() >= deadlineTs;
            return await NotSentAsync(printJobId, claim, attemptId, clock.UtcNow, rejection, expired ? NotSentCause.DeadlinePassed : cause, backoff, maxAttempts);
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

        // Los tramos se registran después de escribir: nada se interpone entre la comprobación y la escritura.
        void RecordWriteStart()
        {
            metrics.Record(MetricNames.PresendCheckToWriteMs, clock.GetElapsedTime(checkTs, writeTs).TotalMilliseconds);
            metrics.Record(MetricNames.PrintDeadlineSlackMs, clock.GetElapsedTime(writeTs, deadlineTs).TotalMilliseconds + context.EffectiveSendMarginMs);
            if (metrics.TakeReceived(context.Job.DetectionId) is { } received)
            {
                metrics.Record(MetricNames.OpcReceiveToSendStartMs, clock.GetElapsedTime(received, writeTs).TotalMilliseconds);
            }
        }

        DateTime? deviceAcceptedAt;
        try
        {
            deviceAcceptedAt = await transport.SendAsync(context.Job.Payload,
                faults is null ? null : () => faults.OnPointAsync(SenderPoint.DuringWrite, attemptId, ct), ct);
        }
        catch (Exception ex)
        {
            RecordWriteStart();
            metrics.MarkWrite(printJobId, writeTs, null);
            transport.Reset();
            NoteDeviceFailure("escritura interrumpida");
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
        metrics.MarkWrite(printJobId, writeTs, clock.GetTimestamp());
        if (deviceAcceptedAt is not null)
        {
            metrics.Record(MetricNames.SendStartToDeviceAcceptMs, clock.GetElapsedTime(writeTs).TotalMilliseconds);
        }

        RecordWriteStart();

        if (faults is not null)
        {
            await faults.OnPointAsync(SenderPoint.AfterWriteBeforeResult, attemptId, ct);
        }

        var transmitted = await RecordResultAsync(attemptId, () => txc.TransmittedAsync(attemptId, writeStartedAt, writeCompletedAt, preSendCheckAt,
            deviceAcceptedAt is { } accepted ? CanonicalTimestamp.Truncate(accepted) : null, clock.UtcNow, CancellationToken.None));
        metrics.Increment(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.Transmitted));
        _deviceFailures = 0;
        await ReapplyEvidenceAsync(context, claim.ClaimedAtUtc);
        return new SendResult(printJobId, claim, transmitted, true);
    }

    /// <summary>
    /// Un fallo del dispositivo (no acepta la conexión o corta la escritura). Al tercero seguido el sender entra en pausa: deja de
    /// reclamar, LabelingReady baja (<see cref="LinePrinterReadiness"/>) y queda la alerta. Un envío transmitido reinicia la cuenta.
    /// </summary>
    private void NoteDeviceFailure(string what)
    {
        if (++_deviceFailures < MaxConsecutiveDeviceFailures || _paused)
        {
            return;
        }

        _paused = true;
        readiness?.SetPaused(true);
        metrics.SetGauge(MetricNames.PrinterSenderPaused, 1, ("printer", printerId));
        metrics.Increment(MetricNames.PrinterSenderPauses, ("printer", printerId));
        _logger.LogError("Impresora {Printer}: {Count} fallos seguidos del dispositivo (el último: {What}). Sender en pausa, LabelingReady en falso; se prueba la conexión hasta que vuelva",
            printerId, _deviceFailures, what);
    }

    /// <summary>
    /// Con el sender en pausa, prueba si la impresora acepta una conexión. Si vuelve (ESTRATEGIA_RECUPERACION §3: "Vaciar cola si se
    /// puede; descartar vencidos; seguir con la siguiente caja"), el sender sigue: los vencidos los descartan el claim (J03) y el barrido.
    /// La cola del dispositivo no se vacía porque la capacidad no está confirmada (OT-07), como en el arranque.
    /// </summary>
    private async Task<bool> ResumeIfPrinterAnswersAsync(CancellationToken ct)
    {
        try
        {
            transport.Reset();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            await transport.ConnectAsync(limit.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            transport.Reset();
            return false;
        }

        _paused = false;
        _deviceFailures = 0;
        readiness?.SetPaused(false);
        metrics.SetGauge(MetricNames.PrinterSenderPaused, 0, ("printer", printerId));
        _logger.LogWarning("Impresora {Printer}: vuelve a aceptar conexiones; el sender sigue con la siguiente caja (la cola del dispositivo no se vacía, OT-07)",
            printerId);
        return true;
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

    /// <summary>
    /// Bucle del sender: envía mientras haya jobs READY; si no, espera una señal o el intervalo de sondeo.
    /// <list type="bullet">
    /// <item>Con la caída de SQL Server declarada (<c>sqlOutage</c> del constructor) no reclama nada: sin árbitro no se imprime
    /// (ESTRATEGIA_RECUPERACION §3). Un error de SQL no disponible fuera de la escritura (TX-B) declara la caída para toda la estación
    /// (DOCUMENTO_PRINCIPAL §15: "Error en TX-A o TX-B"), y el latido baja LabelingReady.</item>
    /// <item>Después de un error fuera de la escritura, antes de reclamar otro job ejecuta TX-R0 de esta impresora: si el commit del
    /// claim se confirmó y la respuesta se perdió, el intento quedó en SENDING sin escritura y la impresora ocupada (PrinterBusy). TX-R0
    /// lo cierra como UNKNOWN (RESTART_DURING_SEND, ESTRATEGIA_RECUPERACION §2), como haría un reinicio. El sender tiene el lease y no
    /// está escribiendo.</item>
    /// <item>En pausa por fallos seguidos del dispositivo, no reclama y prueba la conexión cada <see cref="PausedProbeInterval"/>.</item>
    /// </list>
    /// </summary>
    public async Task RunAsync(TimeSpan pollInterval, TimeProvider loopTime, Func<CancellationToken, Task>? waitForSignal, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_paused && !await ResumeIfPrinterAnswersAsync(ct))
                {
                    await Task.Delay(PausedProbeInterval, loopTime, ct);
                    continue;
                }

                if (sqlOutage is not { Declared: true })
                {
                    if (_recoverBeforeClaim && recovery is not null)
                    {
                        await recovery.RecoverAsync(printerId, ct);
                    }

                    _recoverBeforeClaim = false;

                    // Solo un claim obtenido sigue de inmediato; un rechazo (estación bloqueada, lease, orden, etc.) espera el
                    // intervalo de sondeo en lugar de volver a reclamar el mismo job en bucle.
                    if (await SendNextAsync(ct) is { Claim.Claimed: true })
                    {
                        continue;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SenderHaltedException)
            {
                _recoverBeforeClaim = true;
                _logger.LogError(ex, "Sender de {Printer}: error fuera de la escritura; TX-R0 de la impresora antes del próximo claim", printerId);
                if (SqlAvailability.IsUnavailable(ex) && sqlOutage?.Declare() == true)
                {
                    _logger.LogError("SQL Server no disponible en el claim de {Printer}: falla cerrada para la estación", printerId);
                }
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
