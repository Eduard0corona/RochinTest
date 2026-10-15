using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.PrintAgent;

public sealed record ReprintSendResult(long AuthorizationId, ReprintExecution? Execution, WriteOutcome? Write, bool WriteInvoked);

/// <summary>
/// Reimpresión excepcional ejecutada por el sender de la impresora manual (ManualPrinterId), con su lease: TX-X
/// (R02 y el intento EXCEPTIONAL_REPRINT en SENDING, confirmado antes del I/O), escritura y TX-C. Un intento
/// excepcional nunca cambia el orden de envío de la línea (ADR 0012, I23) ni reintenta: si la escritura falla
/// después de invocarse, el intento queda UNKNOWN y el job, con X03, UNKNOWN con EXCEPTIONAL_REPRINT_UNRESOLVED.
/// </summary>
public sealed class ManualReprintSender(
    string printerId, SqlDb db, TxxReprint txx, TxcWriteResult txc, LeaseGrant lease, IPrinterTransport transport, EdgeClock clock, CartonMetrics metrics,
    ISenderFaults? faults = null, EvidenceService? evidence = null, ILogger<ManualReprintSender>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ManualReprintSender>.Instance;

    /// <summary>La siguiente autorización APPROVED vigente dirigida a esta impresora.</summary>
    public Task<long?> NextApprovedAsync(CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT TOP (1) AuthorizationId FROM dbo.ReprintAuthorization
                WHERE TargetPrinterId = @printer AND Status = 'APPROVED' AND ExpiresAt > @now
                ORDER BY ApprovedAt, AuthorizationId;
                """);
            cmd.NVarChar("@printer", printerId, 64).DateTime2("@now", clock.UtcNow);
            return await cmd.ExecuteScalarAsync(ct) is long id ? (long?)id : null;
        }, ct);

    public async Task<ReprintSendResult?> ExecuteNextAsync(CancellationToken ct) =>
        await NextApprovedAsync(ct) is { } id ? await ExecuteAsync(id, ct) : null;

    public async Task<ReprintSendResult> ExecuteAsync(long authorizationId, CancellationToken ct)
    {
        var attemptId = Guid.NewGuid();
        ReprintExecution execution;
        try
        {
            execution = await txx.ExecuteAsync(authorizationId, printerId, lease.OwnerInstanceId, lease.FencingToken, attemptId, clock.UtcNow, ct);
        }
        catch (SqlException ex) when (ex.Number is TxxReprint.NotExecutable or TxxReprint.AttemptRejected)
        {
            // Sin autorización ejecutable (revocada, vencida, de otro job), o el trigger rechaza el intento (por ejemplo,
            // LABEL_DAMAGED sin el retiro de la etiqueta original registrado): nada se envía.
            _logger.LogWarning("Autorización {Authorization} no ejecutable en {Printer}: {Error}", authorizationId, printerId, ex.Message);
            return new ReprintSendResult(authorizationId, null, null, false);
        }

        var risk = await db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, "SELECT DuplicateRisk FROM dbo.ReprintAuthorization WHERE AuthorizationId = @id");
            cmd.BigInt("@id", authorizationId);
            return (string)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);
        metrics.Increment(MetricNames.ExceptionalReprints, ("risk", risk));
        var claimedAt = clock.UtcNow;

        if (faults is not null)
        {
            await faults.OnPointAsync(SenderPoint.AfterClaimCommit, attemptId, ct);
        }

        try
        {
            if (!transport.Connected)
            {
                await transport.ConnectAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Abrir el canal no es escribir: NOT_SENT, también al detener el servicio (sin el token de la operación).
            transport.Reset();
            var notSent = await ResultRecorder.RecordAsync(() => txc.NotSentAsync(attemptId, clock.UtcNow, $"Conexión fallida: {ex.Message}",
                NotSentCause.ConnectionFailed, 0, 0, clock.UtcNow, CancellationToken.None), printerId, attemptId, _logger);
            return new ReprintSendResult(authorizationId, execution, notSent, false);
        }

        var writeStartedAt = clock.UtcNow;
        DateTime? accepted;
        try
        {
            accepted = await transport.SendAsync(execution.Payload,
                faults is null ? null : () => faults.OnPointAsync(SenderPoint.DuringWrite, attemptId, ct), ct);
        }
        catch (Exception ex)
        {
            transport.Reset();
            metrics.Increment(MetricNames.PrinterUnknown, ("printer", printerId));
            metrics.Increment(MetricNames.ExceptionalReprints, ("result", "UNRESOLVED"));
            var unknown = await ResultRecorder.RecordAsync(() => txc.UnknownAsync(attemptId, writeStartedAt, $"Escritura interrumpida: {ex.Message}",
                clock.UtcNow, CancellationToken.None), printerId, attemptId, _logger);
            return new ReprintSendResult(authorizationId, execution, unknown, true);
        }

        if (faults is not null)
        {
            await faults.OnPointAsync(SenderPoint.AfterWriteBeforeResult, attemptId, ct);
        }

        var transmitted = await ResultRecorder.RecordAsync(() => txc.TransmittedAsync(attemptId, writeStartedAt, clock.UtcNow, writeStartedAt,
            accepted is { } a ? CanonicalTimestamp.Truncate(a) : null, clock.UtcNow, CancellationToken.None), printerId, attemptId, _logger);
        if (evidence is not null)
        {
            var job = await db.ReadAsync(conn => JobQueries.GetJobAsync(conn, null, execution.PrintJobId, ct), ct);
            if (job is not null)
            {
                await evidence.ReapplyForJobAsync(job, claimedAt, CancellationToken.None);
            }
        }

        return new ReprintSendResult(authorizationId, execution, transmitted, true);
    }

    public async Task RunAsync(TimeSpan pollInterval, TimeProvider loopTime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Solo una autorización ejecutada sigue de inmediato; una no ejecutable (por ejemplo, LABEL_DAMAGED sin el
                // retiro registrado) espera el intervalo de sondeo.
                if (await ExecuteNextAsync(ct) is { Execution: not null })
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not SenderHaltedException)
            {
                _logger.LogError(ex, "Sender manual de {Printer}: error fuera de la escritura; se reintenta", printerId);
            }

            await Task.Delay(pollInterval, loopTime, ct);
        }
    }
}
