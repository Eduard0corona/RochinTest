using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>
/// Entrada del claim. Plazo y margen efectivos los calcula el EdgeHost con su reloj y con la revisión activa
/// <c>ActiveConfigRevisionId</c>; si se indica, TX-B exige que siga siendo la activa (una activación entre la
/// lectura y el claim invalida el plazo calculado).
/// </summary>
public sealed record ClaimRequest(
    Guid PrintJobId,
    Guid AttemptId,
    string OwnerInstanceId,
    long LeaseToken,
    DateTime EdgeNowUtc,
    DateTime EffectiveDeadlineUtc,
    int EffectiveSendMarginMs,
    long? ActiveConfigRevisionId = null);

public sealed record ClaimResult(bool Claimed, Guid AttemptId, int AttemptNumber, byte AutoAttemptCount);

/// <summary>Por qué un claim no obtuvo fila (el sender relee el job).</summary>
public enum ClaimRejection
{
    None,
    NotReady,
    Deadline,
    OutOfOrder,
    StationDisabled,
    PrinterNotActive,
    LeaseInvalid,
    MissingSendOrder,
    PrinterBusy,

    /// <summary>El reloj de pared saltó: la estación se bloqueó con CLOCK_JUMP sin evaluar el claim.</summary>
    ClockJump,

    /// <summary>Se activó otra revisión después de calcular el plazo efectivo: el claim se repite con la nueva.</summary>
    RevisionChanged,
}

/// <summary>
/// TX-B: claim condicional (J02), avance de PrinterSendOrder con los valores de la fila reclamada e
/// intento ORDINARY SENDING con MayHaveSent = 1 (A01). Confirma antes de cualquier I/O (I19). Un
/// claim perdido termina con @claimed vacío y sin excepción: los triggers tienen la guarda de 0 filas.
/// </summary>
public sealed class TxbClaim(SqlDb db)
{
    /// <summary>
    /// Un claim que pierde la carrera contra otro intento SENDING de la misma impresora termina en 2601
    /// de UX_PrintAttempt_PrinterInFlight: XACT_ABORT revierte la transacción entera (también el paso a
    /// SENDING del job) y el resultado es "sin claim", como con 0 filas.
    /// </summary>
    public async Task<ClaimResult> ExecuteAsync(ClaimRequest req, CancellationToken ct)
    {
        try
        {
            return await ClaimAsync(req, ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627 && ex.Message.Contains("UX_PrintAttempt_PrinterInFlight", StringComparison.Ordinal))
        {
            return new ClaimResult(false, req.AttemptId, 0, 0);
        }
    }

    private Task<ClaimResult> ClaimAsync(ClaimRequest req, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @claimed TABLE (
                    PrintJobId UNIQUEIDENTIFIER NOT NULL, AutoAttemptCount TINYINT NOT NULL, PrinterId NVARCHAR(64) NOT NULL,
                    PlcEpoch BIGINT NOT NULL, CycleId DECIMAL(20, 0) NOT NULL, EpochAcceptedAt DATETIME2(3) NOT NULL,
                    PayloadHash BINARY(32) NOT NULL);

                UPDATE pj
                SET Status = 'SENDING', AutoAttemptCount = pj.AutoAttemptCount + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId, inserted.AutoAttemptCount, inserted.PrinterId, inserted.PlcEpoch, inserted.CycleId, eh.AcceptedAt,
                       inserted.PayloadHash
                    INTO @claimed (PrintJobId, AutoAttemptCount, PrinterId, PlcEpoch, CycleId, EpochAcceptedAt, PayloadHash)
                FROM dbo.PrintJob AS pj
                INNER JOIN dbo.StationConfig AS sc ON sc.SiteId = pj.SiteId AND sc.StationId = pj.StationId
                INNER JOIN dbo.StationConfigRevision AS ar ON ar.ConfigRevisionId = sc.ActiveConfigRevisionId
                INNER JOIN dbo.PlcEpochHistory AS eh
                    ON eh.SiteId = pj.SiteId AND eh.StationId = pj.StationId AND eh.PlcEpoch = pj.PlcEpoch
                INNER JOIN dbo.PrinterSendOrder AS so WITH (UPDLOCK)
                    ON so.PrinterId = pj.PrinterId AND so.SiteId = pj.SiteId AND so.StationId = pj.StationId
                WHERE pj.PrintJobId = @PrintJobId
                  AND pj.Status = 'READY'
                  AND pj.DeadlineAt > DATEADD(MILLISECOND, @EffectiveSendMarginMs, @EdgeNowUtc)
                  AND @EffectiveDeadlineUtc > DATEADD(MILLISECOND, @EffectiveSendMarginMs, @EdgeNowUtc)
                  AND sc.LabelingEnabled = 1
                  AND (@ActiveConfigRevisionId IS NULL OR sc.ActiveConfigRevisionId = @ActiveConfigRevisionId)
                  AND ar.PrinterId = pj.PrinterId
                  AND (   so.LastPrintJobId IS NULL
                       OR so.LastPrintJobId = pj.PrintJobId
                       OR (pj.PlcEpoch <> so.LastPlcEpoch AND eh.AcceptedAt > so.LastEpochAcceptedAt)
                       OR (pj.PlcEpoch = so.LastPlcEpoch AND pj.CycleId > so.LastCycleId))
                  AND EXISTS (
                      SELECT 1 FROM dbo.PrinterLease AS pl
                      WHERE pl.PrinterId = pj.PrinterId AND pl.OwnerInstanceId = @OwnerInstanceId
                        AND pl.FencingToken = @LeaseToken AND pl.LeaseUntil > @EdgeNowUtc)
                  -- J02: ningún otro intento SENDING ya confirmado en la impresora. Sin bloqueo de rango: la
                  -- barrera es UX_PrintAttempt_PrinterInFlight y una carrera perdida se trata como 0 filas.
                  AND NOT EXISTS (
                      SELECT 1 FROM dbo.PrintAttempt AS inflight
                      WHERE inflight.PrinterId = pj.PrinterId AND inflight.Outcome = 'SENDING');

                DECLARE @attemptNumber INT = NULL;
                IF EXISTS (SELECT 1 FROM @claimed)
                BEGIN
                    -- El orden avanza con los valores de la fila reclamada, no con los que manda el código.
                    UPDATE so
                    SET LastPlcEpoch = c.PlcEpoch, LastEpochAcceptedAt = c.EpochAcceptedAt, LastCycleId = c.CycleId,
                        LastPrintJobId = c.PrintJobId, UpdatedAt = @EdgeNowUtc
                    FROM dbo.PrinterSendOrder AS so
                    INNER JOIN @claimed AS c ON c.PrinterId = so.PrinterId;

                    SELECT @attemptNumber = ISNULL(MAX(AttemptNumber), 0) + 1 FROM dbo.PrintAttempt WHERE PrintJobId = @PrintJobId;

                    INSERT INTO dbo.PrintAttempt (
                        AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId,
                        LeaseToken, Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt)
                    SELECT @AttemptId, c.PrintJobId, @attemptNumber, 'ORDINARY', NULL, c.PrinterId, @OwnerInstanceId,
                        @LeaseToken, 'SENDING', 1, c.PayloadHash, @EdgeNowUtc
                    FROM @claimed AS c;
                END;

                SELECT c.AutoAttemptCount, @attemptNumber FROM @claimed AS c;
                """);
            cmd.DateTime2("@EdgeNowUtc", req.EdgeNowUtc).Guid("@PrintJobId", req.PrintJobId).Int("@EffectiveSendMarginMs", req.EffectiveSendMarginMs)
                .DateTime2("@EffectiveDeadlineUtc", req.EffectiveDeadlineUtc).NVarChar("@OwnerInstanceId", req.OwnerInstanceId, 128)
                .BigInt("@LeaseToken", req.LeaseToken).Guid("@AttemptId", req.AttemptId);
            cmd.Parameters.Add(new SqlParameter("@ActiveConfigRevisionId", System.Data.SqlDbType.BigInt) { Value = (object?)req.ActiveConfigRevisionId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct)
                ? new ClaimResult(true, req.AttemptId, r.GetInt32(1), r.GetByte(0))
                : new ClaimResult(false, req.AttemptId, 0, 0);
        }, ct);

    /// <summary>Relee el job para saber por qué el claim no obtuvo fila.</summary>
    public Task<ClaimRejection> DiagnoseAsync(ClaimRequest req, CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT pj.Status,
                       CASE WHEN pj.DeadlineAt > DATEADD(MILLISECOND, @margin, @now) AND @effective > DATEADD(MILLISECOND, @margin, @now) THEN 1 ELSE 0 END,
                       sc.LabelingEnabled,
                       CASE WHEN ar.PrinterId = pj.PrinterId THEN 1 ELSE 0 END,
                       CASE WHEN so.PrinterId IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN so.LastPrintJobId IS NULL OR so.LastPrintJobId = pj.PrintJobId
                                 OR (pj.PlcEpoch <> so.LastPlcEpoch AND eh.AcceptedAt > so.LastEpochAcceptedAt)
                                 OR (pj.PlcEpoch = so.LastPlcEpoch AND pj.CycleId > so.LastCycleId) THEN 1 ELSE 0 END,
                       CASE WHEN EXISTS (SELECT 1 FROM dbo.PrinterLease AS pl WHERE pl.PrinterId = pj.PrinterId
                                 AND pl.OwnerInstanceId = @owner AND pl.FencingToken = @token AND pl.LeaseUntil > @now) THEN 1 ELSE 0 END,
                       CASE WHEN EXISTS (SELECT 1 FROM dbo.PrintAttempt AS pa WHERE pa.PrinterId = pj.PrinterId AND pa.Outcome = 'SENDING')
                            THEN 1 ELSE 0 END,
                       CASE WHEN @rev IS NULL OR sc.ActiveConfigRevisionId = @rev THEN 1 ELSE 0 END
                FROM dbo.PrintJob AS pj
                INNER JOIN dbo.StationConfig AS sc ON sc.SiteId = pj.SiteId AND sc.StationId = pj.StationId
                INNER JOIN dbo.StationConfigRevision AS ar ON ar.ConfigRevisionId = sc.ActiveConfigRevisionId
                INNER JOIN dbo.PlcEpochHistory AS eh ON eh.SiteId = pj.SiteId AND eh.StationId = pj.StationId AND eh.PlcEpoch = pj.PlcEpoch
                LEFT JOIN dbo.PrinterSendOrder AS so ON so.PrinterId = pj.PrinterId AND so.SiteId = pj.SiteId AND so.StationId = pj.StationId
                WHERE pj.PrintJobId = @id
                """);
            cmd.Int("@margin", req.EffectiveSendMarginMs).DateTime2("@now", req.EdgeNowUtc).DateTime2("@effective", req.EffectiveDeadlineUtc)
                .NVarChar("@owner", req.OwnerInstanceId, 128).BigInt("@token", req.LeaseToken).Guid("@id", req.PrintJobId);
            cmd.Parameters.Add(new SqlParameter("@rev", System.Data.SqlDbType.BigInt) { Value = (object?)req.ActiveConfigRevisionId ?? DBNull.Value });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
            {
                return ClaimRejection.NotReady;
            }

            if (r.GetString(0) != JobStatus.Ready)
            {
                return ClaimRejection.NotReady;
            }

            if (r.GetInt32(8) == 0)
            {
                return ClaimRejection.RevisionChanged;
            }

            if (r.GetInt32(4) == 0)
            {
                return ClaimRejection.MissingSendOrder;
            }

            if (r.GetInt32(1) == 0)
            {
                return ClaimRejection.Deadline;
            }

            if (r.GetInt32(5) == 0)
            {
                return ClaimRejection.OutOfOrder;
            }

            if (!r.GetBoolean(2))
            {
                return ClaimRejection.StationDisabled;
            }

            if (r.GetInt32(3) == 0)
            {
                return ClaimRejection.PrinterNotActive;
            }

            if (r.GetInt32(6) == 0)
            {
                return ClaimRejection.LeaseInvalid;
            }

            return r.GetInt32(7) == 1 ? ClaimRejection.PrinterBusy : ClaimRejection.None;
        }, ct);

    /// <summary>J03 cuando el claim se rechazó por plazo u orden (también lo hace TX-S).</summary>
    public Task<PrintJobResultEvent?> ExpireReadyAsync(Guid printJobId, string expiryReason, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using (var cmd = conn.Command(tx, """
                DECLARE @expired TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL);
                UPDATE dbo.PrintJob
                SET Status = 'EXPIRED', Reason = @ExpiryReason, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @expired (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'READY';
                SELECT COUNT(*) FROM @expired;
                """))
            {
                cmd.VarChar("@ExpiryReason", expiryReason, 40).DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", printJobId);
                if ((int)(await cmd.ExecuteScalarAsync(ct))! == 0)
                {
                    return null;
                }
            }

            return await ResultPublisher.PublishJobAsync(conn, tx, printJobId, edgeNowUtc, ResultExtras.None, ct);
        }, ct);
}
