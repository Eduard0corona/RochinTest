using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>
/// Payload de una reimpresión excepcional: el del job congelado (mismo LabelSerial) y, con
/// ReprintMarker, una marca visible de reimpresión antes del cierre de la etiqueta.
/// </summary>
public static class ReprintPayload
{
    // Debajo del código de barras (filas 340 a 420) y de su línea de interpretación: no tapa las barras.
    public const string MarkerLine = "^FO40,480^A0N,28,28^FDREIMPRESION^FS\n";

    public static string Render(string jobPayload, bool marker)
    {
        if (!marker)
        {
            return jobPayload;
        }

        var end = jobPayload.LastIndexOf("^XZ", StringComparison.Ordinal);
        return end < 0 ? jobPayload + MarkerLine : jobPayload[..end] + MarkerLine + jobPayload[end..];
    }
}

public sealed record ReprintExecution(long AuthorizationId, Guid PrintJobId, Guid AttemptId, int AttemptNumber, int EvidenceWindowMs, string Payload);

/// <summary>
/// TX-X: reimpresión excepcional. La ejecuta el sender de TargetPrinterId con su lease vigente, por
/// su conexión única; la Operations API solo crea la autorización. R02 y A01 van juntos: si la
/// autorización no pasa a EXECUTED, la transacción se revierte con 52001.
/// </summary>
public sealed class TxxReprint(SqlDb db)
{
    public const int NotExecutable = 52001;

    public async Task<ReprintExecution> ExecuteAsync(
        long authorizationId, string printerId, string ownerInstanceId, long leaseToken, Guid attemptId, DateTime edgeNowUtc, CancellationToken ct)
    {
        // El payload de la reimpresión se arma antes de TX-X: el del job congelado y, si la autorización
        // lo pide, la marca visible de reimpresión. El intento registra su propio hash (I09).
        var (jobPayload, marker) = await db.ReadAsync(async conn =>
        {
            await using var read = conn.Command(null, """
                SELECT pj.Payload, ra.ReprintMarker FROM dbo.ReprintAuthorization AS ra
                INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = ra.PrintJobId WHERE ra.AuthorizationId = @id
                """);
            read.BigInt("@id", authorizationId);
            await using var r = await read.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? (r.GetString(0), r.GetBoolean(1)) : (string.Empty, false);
        }, ct);
        var reprintPayload = ReprintPayload.Render(jobPayload, marker);
        var reprintHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reprintPayload));

        var execution = await db.InTransactionAsync(async (conn, tx) =>
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
                        @OwnerInstanceId, @LeaseToken, 'SENDING', 1, @ReprintPayloadHash, @EdgeNowUtc
                    FROM @executed AS e INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = e.PrintJobId;

                    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
                    SELECT e.AuthorizationId, e.PrintJobId, @attemptNumber, e.EvidenceWindowMs
                    FROM @executed AS e INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = e.PrintJobId;
                END TRY
                BEGIN CATCH
                    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
                    THROW;
                END CATCH;
                """);
            cmd.Binary32("@ReprintPayloadHash", reprintHash);
            cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).BigInt("@AuthorizationId", authorizationId).NVarChar("@PrinterId", printerId, 64)
                .NVarChar("@OwnerInstanceId", ownerInstanceId, 128).BigInt("@LeaseToken", leaseToken).Guid("@AttemptId", attemptId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                throw new InvalidOperationException("TX-X no devolvió la ejecución.");
            }

            return new ReprintExecution(r.GetInt64(0), r.GetGuid(1), attemptId, r.GetInt32(2), r.GetInt32(3), reprintPayload);
        }, ct);
        return execution;
    }

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
