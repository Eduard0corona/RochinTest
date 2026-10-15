using System.Collections.Concurrent;
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
    /// <summary>Espera antes de volver a intentar una autorización que TX-X rechazó.</summary>
    public static readonly TimeSpan RejectedRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger = logger ?? NullLogger<ManualReprintSender>.Instance;
    private readonly ConcurrentDictionary<long, DateTime> _rejectedUntil = new();

    /// <summary>
    /// La siguiente autorización APPROVED vigente dirigida a esta impresora. Una que TX-X rechazó (por ejemplo, LABEL_DAMAGED
    /// sin el retiro registrado) espera <see cref="RejectedRetryDelay"/> y no bloquea a las aprobadas después (revisión de
    /// la Fase 5, M3).
    /// </summary>
    public async Task<long?> NextApprovedAsync(CancellationToken ct)
    {
        var approved = await db.ReadAsync<IReadOnlyList<long>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT TOP (50) AuthorizationId FROM dbo.ReprintAuthorization
                WHERE TargetPrinterId = @printer AND Status = 'APPROVED' AND ExpiresAt > @now
                ORDER BY ApprovedAt, AuthorizationId;
                """);
            cmd.NVarChar("@printer", printerId, 64).DateTime2("@now", clock.UtcNow);
            var ids = new List<long>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetInt64(0));
            }

            return ids;
        }, ct);
        var now = clock.UtcNow;
        foreach (var stale in _rejectedUntil.Keys.Except(approved))
        {
            _rejectedUntil.TryRemove(stale, out _);
        }

        return approved.Cast<long?>().FirstOrDefault(id => !_rejectedUntil.TryGetValue(id!.Value, out var until) || now >= until);
    }

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
            // LABEL_DAMAGED sin el retiro de la etiqueta original registrado): nada se envía, y se vuelve a intentar después.
            _rejectedUntil[authorizationId] = clock.UtcNow + RejectedRetryDelay;
            _logger.LogWarning("Autorización {Authorization} no ejecutable en {Printer}: {Error}", authorizationId, printerId, ex.Message);
            return new ReprintSendResult(authorizationId, null, null, false);
        }

        _rejectedUntil.TryRemove(authorizationId, out _);
        metrics.Increment(MetricNames.ExceptionalReprints, ("risk", execution.DuplicateRisk), ("result", "EXECUTED"));
        var claimedAt = clock.UtcNow;

        // Entre TX-X y la escritura nada se invocó: cualquier error o un apagado es NOT_SENT, nunca un intento en SENDING que
        // ocuparía la impresora manual hasta reiniciar (revisión de la Fase 5, M3; la misma regla que el sender de línea).
        try
        {
            if (faults is not null)
            {
                await faults.OnPointAsync(SenderPoint.AfterClaimCommit, attemptId, ct);
            }

            if (!transport.Connected)
            {
                await transport.ConnectAsync(ct);
            }
        }
        catch (Exception ex)
        {
            transport.Reset();
            var notSent = await ResultRecorder.RecordAsync(() => txc.NotSentAsync(attemptId, clock.UtcNow, $"Antes de escribir: {ex.Message}",
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
