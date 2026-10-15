using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

public sealed record ReprintExecution(long AuthorizationId, Guid PrintJobId, Guid AttemptId, int AttemptNumber, int EvidenceWindowMs, string Payload);

/// <summary>
/// TX-X: reimpresión excepcional. La ejecuta el sender de TargetPrinterId con su lease vigente, por
/// su conexión única; la Operations API solo crea la autorización. R02 y A01 van juntos: si la
/// autorización no pasa a EXECUTED, la transacción se revierte con 52001.
/// </summary>
public sealed class TxxReprint(SqlDb db)
{
    public const int NotExecutable = 52001;

    public Task<ReprintExecution> ExecuteAsync(
        long authorizationId, string printerId, string ownerInstanceId, long leaseToken, Guid attemptId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @executed TABLE (AuthorizationId BIGINT NOT NULL, PrintJobId UNIQUEIDENTIFIER NOT NULL, EvidenceWindowMs INT NOT NULL);
                BEGIN TRY
                    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = 'EXCEPTION';

                    UPDATE ra
                    SET Status = 'EXECUTED', ExecutedAt = @EdgeNowUtc
                    OUTPUT inserted.AuthorizationId, inserted.PrintJobId, inserted.EvidenceWindowMs
                        INTO @executed (AuthorizationId, PrintJobId, EvidenceWindowMs)
                    FROM dbo.ReprintAuthorization AS ra
                    INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = ra.PrintJobId
                    WHERE ra.AuthorizationId = @AuthorizationId
                      AND ra.Status = 'APPROVED'
                      AND ra.ExpiresAt > @EdgeNowUtc
                      AND ra.TargetPrinterId = @PrinterId
                      AND pj.Status = ra.PriorJobStatus
                      AND EXISTS (
                          SELECT 1 FROM dbo.PrinterLease AS pl
                          WHERE pl.PrinterId = @PrinterId AND pl.OwnerInstanceId = @OwnerInstanceId
                            AND pl.FencingToken = @LeaseToken AND pl.LeaseUntil > @EdgeNowUtc);

                    IF NOT EXISTS (SELECT 1 FROM @executed)
                    BEGIN
                        THROW 52001, N'Autorización no ejecutable: vencida, revocada, usada, job en otro estado o lease no vigente.', 1;
                    END;

                    DECLARE @attemptNumber INT = (
                        SELECT ISNULL(MAX(pa.AttemptNumber), 0) + 1 FROM dbo.PrintAttempt AS pa
                        INNER JOIN @executed AS e ON e.PrintJobId = pa.PrintJobId);

                    -- TR_PrintAttempt_Guard exige además que @PrinterId sea la ManualPrinterId activa. El
                    -- intento registra su propio AttemptPayloadHash (I09): el payload congelado del job.
                    INSERT INTO dbo.PrintAttempt (
                        AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId,
                        LeaseToken, Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt)
                    SELECT @AttemptId, e.PrintJobId, @attemptNumber, 'EXCEPTIONAL_REPRINT', e.AuthorizationId, @PrinterId,
                        @OwnerInstanceId, @LeaseToken, 'SENDING', 1, pj.PayloadHash, @EdgeNowUtc
                    FROM @executed AS e INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = e.PrintJobId;

                    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
                    SELECT e.AuthorizationId, e.PrintJobId, @attemptNumber, e.EvidenceWindowMs, pj.Payload
                    FROM @executed AS e INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = e.PrintJobId;
                END TRY
                BEGIN CATCH
                    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
                    THROW;
                END CATCH;
                """);
            cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).BigInt("@AuthorizationId", authorizationId).NVarChar("@PrinterId", printerId, 64)
                .NVarChar("@OwnerInstanceId", ownerInstanceId, 128).BigInt("@LeaseToken", leaseToken).Guid("@AttemptId", attemptId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                throw new InvalidOperationException("TX-X no devolvió la ejecución.");
            }

            return new ReprintExecution(r.GetInt64(0), r.GetGuid(1), attemptId, r.GetInt32(2), r.GetInt32(3), r.GetString(4));
        }, ct);

    /// <summary>Autorizaciones APPROVED dirigidas a la impresora (la cola del sender de TargetPrinterId).</summary>
    public Task<IReadOnlyList<long>> PendingForPrinterAsync(string printerId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<long>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT AuthorizationId FROM dbo.ReprintAuthorization
                WHERE TargetPrinterId = @p AND Status = 'APPROVED' AND ExpiresAt > @now
                ORDER BY ApprovedAt
                """);
            cmd.NVarChar("@p", printerId, 64).DateTime2("@now", edgeNowUtc);
            var ids = new List<long>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetInt64(0));
            }

            return ids;
        }, ct);
}
