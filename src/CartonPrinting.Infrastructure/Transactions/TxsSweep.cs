using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

public sealed record SweepTransition(string Kind, Guid Id, string From, string To, string? Reason, PrintJobResultEvent? Published);

public sealed record GapClosure(long GapId, string Status, ulong UnrecoveredCount);

/// <summary>
/// TX-S: barrido por estación. Cierra y publica (D07, J03, J13, J14, J11/A06, X03, G03, R04); no es
/// la protección contra envíos tardíos, que es la guarda del claim. Cada operación es su propia
/// transacción; las decisiones de plazo usan @EdgeNowUtc, nunca el reloj del servidor SQL.
/// </summary>
public sealed class TxsSweep(SqlDb db)
{
    /// <summary>J03 y J14 con DEADLINE_PASSED; margen efectivo por job: el mayor entre el congelado y el activo.</summary>
    public Task<IReadOnlyList<SweepTransition>> ExpireDeadlinesAsync(string siteId, string stationId, int activeSendMarginMs, DateTime edgeNowUtc, CancellationToken ct) =>
        ExpireJobsAsync("""
            UPDATE pj
            SET Status = 'EXPIRED', Reason = 'DEADLINE_PASSED', NextAttemptAt = NULL,
                ResultVersion = pj.ResultVersion + 1, UpdatedAt = @EdgeNowUtc
            OUTPUT inserted.PrintJobId, deleted.Status INTO @expired (PrintJobId, FromStatus)
            FROM dbo.PrintJob AS pj
            INNER JOIN dbo.StationConfigRevision AS fr ON fr.ConfigRevisionId = pj.ConfigRevisionId
            WHERE pj.SiteId = @SiteId AND pj.StationId = @StationId
              AND pj.Status IN ('READY', 'RETRY_WAIT')
              AND pj.DeadlineAt <= DATEADD(
                  MILLISECOND,
                  CASE WHEN fr.SendMarginMs > @ActiveSendMarginMs THEN fr.SendMarginMs ELSE @ActiveSendMarginMs END,
                  @EdgeNowUtc);
            """, JobReason.DeadlinePassed, siteId, stationId, c => c.Int("@ActiveSendMarginMs", activeSendMarginMs), edgeNowUtc, ct);

    /// <summary>J03 y J14 con OUT_OF_ORDER: la impresora ya reclamó un ciclo posterior.</summary>
    public Task<IReadOnlyList<SweepTransition>> ExpireOutOfOrderAsync(string siteId, string stationId, DateTime edgeNowUtc, CancellationToken ct) =>
        ExpireJobsAsync("""
            UPDATE pj
            SET Status = 'EXPIRED', Reason = 'OUT_OF_ORDER', NextAttemptAt = NULL,
                ResultVersion = pj.ResultVersion + 1, UpdatedAt = @EdgeNowUtc
            OUTPUT inserted.PrintJobId, deleted.Status INTO @expired (PrintJobId, FromStatus)
            FROM dbo.PrintJob AS pj
            INNER JOIN dbo.PrinterSendOrder AS so ON so.PrinterId = pj.PrinterId
            INNER JOIN dbo.PlcEpochHistory AS eh
                ON eh.SiteId = pj.SiteId AND eh.StationId = pj.StationId AND eh.PlcEpoch = pj.PlcEpoch
            WHERE pj.SiteId = @SiteId AND pj.StationId = @StationId
              AND pj.Status IN ('READY', 'RETRY_WAIT')
              AND so.LastPrintJobId IS NOT NULL AND so.LastPrintJobId <> pj.PrintJobId
              AND (   eh.AcceptedAt < so.LastEpochAcceptedAt
                   OR (pj.PlcEpoch = so.LastPlcEpoch AND pj.CycleId < so.LastCycleId));
            """, JobReason.OutOfOrder, siteId, stationId, _ => { }, edgeNowUtc, ct);

    /// <summary>
    /// J13: RETRY_WAIT → READY cuando se cumple NextAttemptAt, con plazo vigente con margen y sin un
    /// ciclo posterior ya reclamado por la impresora. Devuelve los jobs a señalar al sender.
    /// </summary>
    public Task<IReadOnlyList<SweepTransition>> ReleaseRetriesAsync(string siteId, string stationId, int activeSendMarginMs, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<SweepTransition>>(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @ready TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL);
                UPDATE pj
                SET Status = 'READY', NextAttemptAt = NULL, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @ready (PrintJobId)
                FROM dbo.PrintJob AS pj
                INNER JOIN dbo.StationConfigRevision AS fr ON fr.ConfigRevisionId = pj.ConfigRevisionId
                INNER JOIN dbo.PlcEpochHistory AS eh
                    ON eh.SiteId = pj.SiteId AND eh.StationId = pj.StationId AND eh.PlcEpoch = pj.PlcEpoch
                LEFT JOIN dbo.PrinterSendOrder AS so ON so.PrinterId = pj.PrinterId
                WHERE pj.SiteId = @SiteId AND pj.StationId = @StationId
                  AND pj.Status = 'RETRY_WAIT' AND pj.NextAttemptAt <= @EdgeNowUtc
                  AND pj.DeadlineAt > DATEADD(
                      MILLISECOND,
                      CASE WHEN fr.SendMarginMs > @ActiveSendMarginMs THEN fr.SendMarginMs ELSE @ActiveSendMarginMs END,
                      @EdgeNowUtc)
                  AND (   so.LastPrintJobId IS NULL OR so.LastPrintJobId = pj.PrintJobId
                       OR (pj.PlcEpoch <> so.LastPlcEpoch AND eh.AcceptedAt > so.LastEpochAcceptedAt)
                       OR (pj.PlcEpoch = so.LastPlcEpoch AND pj.CycleId > so.LastCycleId));
                SELECT PrintJobId FROM @ready;
                """);
            cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64)
                .Int("@ActiveSendMarginMs", activeSendMarginMs);
            var list = new List<SweepTransition>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new SweepTransition("PrintJob", r.GetGuid(0), JobStatus.RetryWait, JobStatus.Ready, null, null));
            }

            return list;
        }, ct);

    /// <summary>D07: detecciones WAITING_ERP con el plazo vencido quedan EXPIRED con ERP_UNAVAILABLE.</summary>
    public Task<IReadOnlyList<SweepTransition>> ExpireWaitingErpAsync(string siteId, string stationId, int activeSendMarginMs, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<SweepTransition>>(async (conn, tx) =>
        {
            var expired = new List<(Guid Id, string LabelType)>();
            await using (var cmd = conn.Command(tx, """
                DECLARE @expired TABLE (DetectionId UNIQUEIDENTIFIER NOT NULL, ConfigRevisionId BIGINT NOT NULL);
                UPDATE dbo.CartonDetection
                SET Status = 'EXPIRED', Reason = 'ERP_UNAVAILABLE', UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.DetectionId, inserted.ConfigRevisionId INTO @expired (DetectionId, ConfigRevisionId)
                WHERE SiteId = @SiteId AND StationId = @StationId
                  AND Status = 'WAITING_ERP'
                  AND DeadlineAt <= DATEADD(MILLISECOND, @ActiveSendMarginMs, @EdgeNowUtc);
                SELECT e.DetectionId, r.LabelType FROM @expired AS e
                INNER JOIN dbo.StationConfigRevision AS r ON r.ConfigRevisionId = e.ConfigRevisionId;
                """))
            {
                cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64)
                    .Int("@ActiveSendMarginMs", activeSendMarginMs);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    expired.Add((r.GetGuid(0), r.GetString(1)));
                }
            }

            var list = new List<SweepTransition>();
            foreach (var (id, labelType) in expired)
            {
                var detection = (await DetectionQueries.GetAsync(conn, tx, id, ct))!;
                var evt = await ResultPublisher.PublishDetectionAsync(conn, tx, detection, labelType, edgeNowUtc, ct);
                list.Add(new SweepTransition("CartonDetection", id, DetectionStatus.WaitingErp, DetectionStatus.Expired, DetectionReason.ErpUnavailable, evt));
            }

            return list;
        }, ct);

    /// <summary>Intentos TRANSMITTED de la estación cuya ventana de evidencia venció.</summary>
    public Task<IReadOnlyList<Guid>> ElapsedEvidenceWindowsAsync(string siteId, string stationId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<Guid>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT pa.AttemptId
                FROM dbo.PrintAttempt AS pa
                INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = pa.PrintJobId
                WHERE pa.Outcome = 'TRANSMITTED' AND pa.EvidenceDeadlineAt IS NOT NULL AND pa.EvidenceDeadlineAt <= @EdgeNowUtc
                  AND pj.SiteId = @SiteId AND pj.StationId = @StationId
                ORDER BY pa.EvidenceDeadlineAt
                """);
            cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64);
            var ids = new List<Guid>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetGuid(0));
            }

            return ids;
        }, ct);

    /// <summary>
    /// Ventana de evidencia vencida, por intento (A06), con la guarda de su propio plazo. A06 y la
    /// transición del job (J11 para un intento ordinario, X03 para uno excepcional) van juntas.
    /// </summary>
    public Task<SweepTransition?> ElapseEvidenceWindowAsync(Guid attemptId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync<SweepTransition?>(async (conn, tx) =>
        {
            var attempt = await TxcWriteResult.UpdateAttemptAsync(conn, tx, """
                UPDATE dbo.PrintAttempt
                SET Outcome = 'UNKNOWN', FailureDetail = N'Ventana de evidencia vencida'
                OUTPUT inserted.PrintJobId, inserted.Kind INTO @a (PrintJobId, Kind)
                WHERE AttemptId = @AttemptId AND Outcome = 'TRANSMITTED'
                  AND EvidenceDeadlineAt IS NOT NULL AND EvidenceDeadlineAt <= @EdgeNowUtc;
                """, c => c.Guid("@AttemptId", attemptId).DateTime2("@EdgeNowUtc", edgeNowUtc), ct);
            if (attempt is null)
            {
                return null;
            }

            if (attempt.Value.Kind == AttemptKind.ExceptionalReprint)
            {
                var x03 = await TxcWriteResult.ExceptionalUnresolvedAsync(conn, tx, attempt.Value.JobId, attemptId, edgeNowUtc, ct);
                return new SweepTransition("PrintJob", attempt.Value.JobId, "(X03)", JobStatus.Unknown, JobReason.ExceptionalReprintUnresolved, x03);
            }

            await TxcWriteResult.RequireJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'UNKNOWN', Reason = 'EVIDENCE_WINDOW_ELAPSED', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'TRANSMITTED';
                """, c => c.DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", attempt.Value.JobId), ct);
            var published = await ResultPublisher.PublishJobAsync(conn, tx, attempt.Value.JobId, edgeNowUtc, new ResultExtras { AttemptId = attemptId }, ct);
            return new SweepTransition("PrintJob", attempt.Value.JobId, JobStatus.Transmitted, JobStatus.Unknown, JobReason.EvidenceWindowElapsed, published);
        }, ct);

    /// <summary>R04: autorizaciones APPROVED vencidas de la estación.</summary>
    public Task<IReadOnlyList<long>> ExpireAuthorizationsAsync(string siteId, string stationId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<long>>(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @expired TABLE (AuthorizationId BIGINT NOT NULL);
                UPDATE ra
                SET Status = 'EXPIRED'
                OUTPUT inserted.AuthorizationId INTO @expired (AuthorizationId)
                FROM dbo.ReprintAuthorization AS ra
                INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = ra.PrintJobId
                WHERE ra.Status = 'APPROVED' AND ra.ExpiresAt <= @EdgeNowUtc
                  AND pj.SiteId = @SiteId AND pj.StationId = @StationId;
                SELECT AuthorizationId FROM @expired;
                """);
            cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64);
            var ids = new List<long>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                ids.Add(r.GetInt64(0));
            }

            return ids;
        }, ct);

    /// <summary>
    /// G03: huecos OPEN cuya gracia venció quedan CLOSED_UNRECOVERED con el conteo de ciclos sin
    /// datos; si entretanto llegaron todos, RECOVERED (G02). Nunca se inventan detecciones.
    /// Rama defensiva: G02 pertenece a TX-A; si un hueco vence con todos sus ciclos registrados y sigue
    /// OPEN (no se reprodujo en pruebas), se cierra como RECOVERED, el único estado válido con 0 ciclos
    /// faltantes (CK_SequenceGap_Close), en lugar de dejarlo abierto (SI-06).
    /// </summary>
    public Task<IReadOnlyList<GapClosure>> CloseGapsAsync(string siteId, string stationId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<GapClosure>>(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @counts TABLE (GapId BIGINT NOT NULL, Missing DECIMAL(20, 0) NOT NULL);
                INSERT INTO @counts (GapId, Missing)
                SELECT g.GapId,
                       (g.ToCycleId - g.FromCycleId + 1) -
                       (SELECT COUNT_BIG(*) FROM dbo.CartonDetection AS cd
                        WHERE cd.SiteId = g.SiteId AND cd.StationId = g.StationId AND cd.PlcEpoch = g.PlcEpoch
                          AND cd.CycleId BETWEEN g.FromCycleId AND g.ToCycleId)
                FROM dbo.SequenceGap AS g WITH (UPDLOCK, HOLDLOCK)
                WHERE g.SiteId = @SiteId AND g.StationId = @StationId AND g.Status = 'OPEN' AND g.GraceUntil <= @EdgeNowUtc;

                DECLARE @closed TABLE (GapId BIGINT NOT NULL, Status VARCHAR(20) NOT NULL, UnrecoveredCount DECIMAL(20, 0) NOT NULL);
                UPDATE g
                SET Status = CASE WHEN c.Missing > 0 THEN 'CLOSED_UNRECOVERED' ELSE 'RECOVERED' END,
                    UnrecoveredCount = c.Missing, ClosedAt = @EdgeNowUtc
                OUTPUT inserted.GapId, inserted.Status, inserted.UnrecoveredCount INTO @closed (GapId, Status, UnrecoveredCount)
                FROM dbo.SequenceGap AS g
                INNER JOIN @counts AS c ON c.GapId = g.GapId;
                SELECT GapId, Status, UnrecoveredCount FROM @closed;
                """);
            cmd.NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64).DateTime2("@EdgeNowUtc", edgeNowUtc);
            var list = new List<GapClosure>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new GapClosure(r.GetInt64(0), r.GetString(1), (ulong)r.GetDecimal(2)));
            }

            return list;
        }, ct);

    private Task<IReadOnlyList<SweepTransition>> ExpireJobsAsync(
        string update, string reason, string siteId, string stationId, Action<SqlCommand> bind, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<SweepTransition>>(async (conn, tx) =>
        {
            var expired = new List<(Guid Id, string From)>();
            await using (var cmd = conn.Command(tx, $"""
                DECLARE @expired TABLE (PrintJobId UNIQUEIDENTIFIER NOT NULL, FromStatus VARCHAR(20) NOT NULL);
                {update}
                SELECT PrintJobId, FromStatus FROM @expired;
                """))
            {
                cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64);
                bind(cmd);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    expired.Add((r.GetGuid(0), r.GetString(1)));
                }
            }

            var list = new List<SweepTransition>();
            foreach (var (id, from) in expired)
            {
                var evt = await ResultPublisher.PublishJobAsync(conn, tx, id, edgeNowUtc, ResultExtras.None, ct);
                list.Add(new SweepTransition("PrintJob", id, from, JobStatus.Expired, reason, evt));
            }

            return list;
        }, ct);
}
