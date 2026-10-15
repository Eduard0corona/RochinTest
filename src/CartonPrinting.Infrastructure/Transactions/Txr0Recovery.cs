using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

public sealed record RecoveryResult(IReadOnlyList<Guid> OrdinaryJobs, IReadOnlyList<Guid> ExceptionalJobs, IReadOnlyList<PrintJobResultEvent> Published);

/// <summary>
/// TX-R0: recuperación al arrancar, después del lock y del lease de la impresora y antes de
/// suscribirse a OPC UA. Los intentos SENDING de la impresora pasan a UNKNOWN (A04) y sus jobs a
/// UNKNOWN con RESTART_DURING_SEND (J09); un intento excepcional abierto aplica X03 en la misma
/// transacción. Nunca vuelve nada a READY.
/// </summary>
public sealed class Txr0Recovery(SqlDb db)
{
    public Task<RecoveryResult> ExecuteAsync(string printerId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var published = new List<PrintJobResultEvent>();

            // Intentos ORDINARY abiertos de la impresora: A04 y J09 con RESTART_DURING_SEND.
            var ordinary = new List<(Guid AttemptId, Guid JobId)>();
            await using (var cmd = conn.Command(tx, """
                DECLARE @reopened TABLE (AttemptId UNIQUEIDENTIFIER NOT NULL, PrintJobId UNIQUEIDENTIFIER NOT NULL);
                UPDATE dbo.PrintAttempt
                SET Outcome = 'UNKNOWN', FailureDetail = N'Arranque encontró el intento abierto'
                OUTPUT inserted.AttemptId, inserted.PrintJobId INTO @reopened (AttemptId, PrintJobId)
                WHERE Outcome = 'SENDING' AND PrinterId = @PrinterId AND Kind = 'ORDINARY';

                DECLARE @jobs TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL);
                UPDATE dbo.PrintJob
                SET Status = 'UNKNOWN', Reason = 'RESTART_DURING_SEND', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @jobs (PrintJobId)
                WHERE Status = 'SENDING' AND PrinterId = @PrinterId;

                SELECT r.AttemptId, r.PrintJobId FROM @reopened AS r;
                SELECT j.PrintJobId FROM @jobs AS j WHERE NOT EXISTS (SELECT 1 FROM @reopened AS r WHERE r.PrintJobId = j.PrintJobId);
                """))
            {
                cmd.NVarChar("@PrinterId", printerId, 64).DateTime2("@EdgeNowUtc", edgeNowUtc);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    ordinary.Add((r.GetGuid(0), r.GetGuid(1)));
                }

                await r.NextResultAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    // Un job SENDING sin intento abierto no puede existir: TX-B los crea juntos.
                    throw new InvalidOperationException($"Job {r.GetGuid(0)} en SENDING sin intento abierto.");
                }
            }

            foreach (var (attemptId, jobId) in ordinary)
            {
                published.Add(await ResultPublisher.PublishJobAsync(conn, tx, jobId, edgeNowUtc, new ResultExtras { AttemptId = attemptId }, ct));
            }

            // Intentos EXCEPTIONAL_REPRINT abiertos de la impresora: A04 y X03 en la misma transacción.
            var exceptional = new List<(Guid AttemptId, Guid JobId)>();
            await using (var cmd = conn.Command(tx, """
                DECLARE @reopened TABLE (AttemptId UNIQUEIDENTIFIER NOT NULL, PrintJobId UNIQUEIDENTIFIER NOT NULL);
                UPDATE dbo.PrintAttempt
                SET Outcome = 'UNKNOWN', FailureDetail = N'Arranque encontró la reimpresión abierta'
                OUTPUT inserted.AttemptId, inserted.PrintJobId INTO @reopened (AttemptId, PrintJobId)
                WHERE Outcome = 'SENDING' AND PrinterId = @PrinterId AND Kind = 'EXCEPTIONAL_REPRINT';
                SELECT AttemptId, PrintJobId FROM @reopened;
                """))
            {
                cmd.NVarChar("@PrinterId", printerId, 64);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    exceptional.Add((r.GetGuid(0), r.GetGuid(1)));
                }
            }

            foreach (var (attemptId, jobId) in exceptional)
            {
                published.Add(await TxcWriteResult.ExceptionalUnresolvedAsync(conn, tx, jobId, attemptId, edgeNowUtc, ct));
            }

            return new RecoveryResult(ordinary.Select(o => o.JobId).ToArray(), exceptional.Select(e => e.JobId).ToArray(), published);
        }, ct);
}
