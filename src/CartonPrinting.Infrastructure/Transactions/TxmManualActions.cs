using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>Una acción manual auditada. PerformedBy nunca es 'SYSTEM' (CK_OperatorAction_Person).</summary>
public sealed record OperatorActionInput(
    string ActionType,
    string SiteId,
    string StationId,
    Guid? PrintJobId,
    string? TargetStatus,
    string PerformedBy,
    string ReasonText,
    string? DeviceNotPrintedEvidence = null,
    string? DeviceQueueClearedEvidence = null,
    string? CartonInspectionEvidence = null,
    string? CartonDisposition = null);

public sealed record ManualResult(long OperatorActionId, bool Applied, PrintJobResultEvent? Published);

/// <summary>Solicitud de reimpresión excepcional aprobada por dos personas (R01).</summary>
public sealed record ReprintRequest(
    Guid PrintJobId,
    string PriorJobStatus,
    string Reason,
    string DuplicateRisk,
    string CartonReference,
    string InspectionEvidence,
    string TargetPrinterId,
    bool ReprintMarker,
    int EvidenceWindowMs,
    string RequestedBy,
    DateTime RequestedAt,
    string ApprovedBy,
    DateTime ApprovedAt,
    DateTime ExpiresAt);

/// <summary>
/// TX-M: acciones manuales de la Operations API. Cada una inserta su OperatorAction en la misma
/// transacción; los triggers rechazan la transición sin ella (I24). Ninguna envía bytes a una impresora.
/// </summary>
public sealed class TxmManualActions(SqlDb db)
{
    public static async Task<long> InsertOperatorActionAsync(SqlConnection conn, SqlTransaction tx, OperatorActionInput a, DateTime edgeNowUtc, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            DECLARE @action TABLE (OperatorActionId BIGINT NOT NULL);
            INSERT INTO dbo.OperatorAction (
                ActionType, SiteId, StationId, PrintJobId, TargetStatus, PerformedBy, PerformedAt, ReasonText,
                DeviceNotPrintedEvidence, DeviceQueueClearedEvidence, CartonInspectionEvidence, CartonDisposition)
            OUTPUT inserted.OperatorActionId INTO @action (OperatorActionId)
            VALUES (
                @ActionType, @SiteId, @StationId, @PrintJobId, @TargetStatus, @PerformedBy, @EdgeNowUtc, @ReasonText,
                @DeviceNotPrintedEvidence, @DeviceQueueClearedEvidence, @CartonInspectionEvidence, @CartonDisposition);
            SELECT OperatorActionId FROM @action;
            """);
        cmd.VarChar("@ActionType", a.ActionType, 30).NVarChar("@SiteId", a.SiteId, 64).NVarChar("@StationId", a.StationId, 64)
            .Guid("@PrintJobId", a.PrintJobId).VarChar("@TargetStatus", a.TargetStatus, 20).NVarChar("@PerformedBy", a.PerformedBy, 128)
            .DateTime2("@EdgeNowUtc", edgeNowUtc).NVarChar("@ReasonText", a.ReasonText, 512)
            .NVarChar("@DeviceNotPrintedEvidence", a.DeviceNotPrintedEvidence, 256).NVarChar("@DeviceQueueClearedEvidence", a.DeviceQueueClearedEvidence, 256)
            .NVarChar("@CartonInspectionEvidence", a.CartonInspectionEvidence, 256).VarChar("@CartonDisposition", a.CartonDisposition, 20);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>J04: READY → CANCELLED con CANCEL_JOB. El trigger rechaza el estado sin etiqueta si un intento pudo transmitir.</summary>
    public Task<ManualResult> CancelJobAsync(Guid printJobId, string siteId, string stationId, string performedBy, string reasonText, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var oa = await InsertOperatorActionAsync(conn, tx, new OperatorActionInput(OperatorActionType.CancelJob, siteId, stationId, printJobId,
                JobStatus.Cancelled, performedBy, reasonText), edgeNowUtc, ct);
            var moved = await TxcWriteResult.UpdateJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'CANCELLED', Reason = 'CANCELLED_BY_OPERATOR', LastOperatorActionId = @OperatorActionId,
                    ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'READY';
                """, c => c.BigInt("@OperatorActionId", oa).DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", printJobId), ct);
            if (!moved)
            {
                throw new ManualActionRejectedException("Solo un job READY se cancela.");
            }

            return new ManualResult(oa, true, await ResultPublisher.PublishJobAsync(conn, tx, printJobId, edgeNowUtc, new ResultExtras { OperatorActionId = oa }, ct));
        }, ct);

    /// <summary>
    /// A09 y J16: reconciliación negativa con las tres pruebas (CK_OperatorAction_Target). Todos los
    /// intentos UNKNOWN del job pasan a NOT_PRINTED antes que el job.
    /// </summary>
    public Task<ManualResult> ReconcileNegativeAsync(
        Guid printJobId, string siteId, string stationId, string performedBy, string reasonText, string deviceNotPrinted, string queueCleared,
        string cartonInspection, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var oa = await InsertOperatorActionAsync(conn, tx, new OperatorActionInput(OperatorActionType.ReconcileNegative, siteId, stationId,
                printJobId, JobStatus.Failed, performedBy, reasonText, deviceNotPrinted, queueCleared, cartonInspection), edgeNowUtc, ct);
            Guid? attemptId = null;
            await using (var attempts = conn.Command(tx, """
                DECLARE @a TABLE (AttemptId UNIQUEIDENTIFIER NOT NULL, AttemptNumber INT NOT NULL);
                UPDATE dbo.PrintAttempt
                SET Outcome = 'NOT_PRINTED', OperatorActionId = @OperatorActionId
                OUTPUT inserted.AttemptId, inserted.AttemptNumber INTO @a (AttemptId, AttemptNumber)
                WHERE PrintJobId = @PrintJobId AND Outcome = 'UNKNOWN';
                SELECT TOP (1) AttemptId FROM @a ORDER BY AttemptNumber DESC;
                """))
            {
                attempts.BigInt("@OperatorActionId", oa).Guid("@PrintJobId", printJobId);
                attemptId = (Guid?)await attempts.ExecuteScalarAsync(ct);
            }

            var moved = await TxcWriteResult.UpdateJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'FAILED', Reason = 'RECONCILED_NOT_PRINTED', LastOperatorActionId = @OperatorActionId,
                    ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'UNKNOWN';
                """, c => c.BigInt("@OperatorActionId", oa).DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@PrintJobId", printJobId), ct);
            if (!moved)
            {
                throw new ManualActionRejectedException("Solo un job UNKNOWN se reconcilia negativamente.");
            }

            return new ManualResult(oa, true, await ResultPublisher.PublishJobAsync(conn, tx, printJobId, edgeNowUtc,
                new ResultExtras { OperatorActionId = oa, AttemptId = attemptId }, ct));
        }, ct);

    /// <summary>J17: cierre operativo de un UNKNOWN con la caja rechazada o en cuarentena.</summary>
    public Task<ManualResult> CloseUnresolvedAsync(
        Guid printJobId, string siteId, string stationId, string performedBy, string reasonText, string cartonDisposition, DateTime edgeNowUtc,
        CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var oa = await InsertOperatorActionAsync(conn, tx, new OperatorActionInput(OperatorActionType.CloseUnresolved, siteId, stationId,
                printJobId, JobStatus.ClosedUnresolved, performedBy, reasonText, CartonDisposition: cartonDisposition), edgeNowUtc, ct);
            var reason = cartonDisposition == "REJECTED" ? JobReason.ClosedCartonRejected : JobReason.ClosedCartonQuarantined;
            var moved = await TxcWriteResult.UpdateJobAsync(conn, tx, """
                UPDATE dbo.PrintJob
                SET Status = 'CLOSED_UNRESOLVED', Reason = @Reason, LastOperatorActionId = @OperatorActionId,
                    ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
                OUTPUT inserted.PrintJobId INTO @j (PrintJobId)
                WHERE PrintJobId = @PrintJobId AND Status = 'UNKNOWN';
                """, c => c.VarChar("@Reason", reason, 40).BigInt("@OperatorActionId", oa).DateTime2("@EdgeNowUtc", edgeNowUtc)
                    .Guid("@PrintJobId", printJobId), ct);
            if (!moved)
            {
                throw new ManualActionRejectedException("Solo un job UNKNOWN se cierra sin resolver.");
            }

            return new ManualResult(oa, true, await ResultPublisher.PublishJobAsync(conn, tx, printJobId, edgeNowUtc, new ResultExtras { OperatorActionId = oa }, ct));
        }, ct);

    /// <summary>Desbloqueo de estación: TR_StationConfig_Guard exige una OperatorAction UNBLOCK_STATION nueva.</summary>
    public Task<ManualResult> UnblockStationAsync(string siteId, string stationId, string performedBy, string reasonText, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var oa = await InsertOperatorActionAsync(conn, tx, new OperatorActionInput(OperatorActionType.UnblockStation, siteId, stationId, null, null,
                performedBy, reasonText), edgeNowUtc, ct);
            await using var cmd = conn.Command(tx, """
                DECLARE @unblocked TABLE (SiteId NVARCHAR(64) NOT NULL);
                UPDATE dbo.StationConfig
                SET LabelingEnabled = 1, BlockReason = NULL, BlockedAt = NULL, BlockedBy = NULL, BlockedByAnomalyId = NULL,
                    LastUnblockedBy = @PerformedBy, LastUnblockedAt = @EdgeNowUtc, LastOperatorActionId = @OperatorActionId
                OUTPUT inserted.SiteId INTO @unblocked (SiteId)
                WHERE SiteId = @SiteId AND StationId = @StationId AND LabelingEnabled = 0;
                SELECT COUNT(*) FROM @unblocked;
                """);
            cmd.NVarChar("@PerformedBy", performedBy, 128).DateTime2("@EdgeNowUtc", edgeNowUtc).BigInt("@OperatorActionId", oa)
                .NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64);
            return new ManualResult(oa, (int)(await cmd.ExecuteScalarAsync(ct))! == 1, null);
        }, ct);

    /// <summary>Confirmación manual de una época: inserta CONFIRM_EPOCH y la época OPERATOR_CONFIRMED (TR_PlcEpochHistory_Guard).</summary>
    public Task<ManualResult> ConfirmEpochAsync(
        string siteId, string stationId, long epoch, ulong firstCycleId, long configRevisionId, string performedBy, string reasonText,
        DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var oa = await InsertOperatorActionAsync(conn, tx, new OperatorActionInput(OperatorActionType.ConfirmEpoch, siteId, stationId, null, null,
                performedBy, reasonText), edgeNowUtc, ct);
            await PlcEpochStore.InsertAsync(conn, tx, new PlcEpoch(siteId, stationId, epoch, "PLC", PlcEpochStore.OperatorConfirmed, firstCycleId,
                edgeNowUtc, performedBy, configRevisionId, oa), ct);
            return new ManualResult(oa, true, null);
        }, ct);

    /// <summary>Resolución de una anomalía: ResolvedBy y una OperatorAction RESOLVE_ANOMALY de auditoría.</summary>
    public Task<ManualResult> ResolveAnomalyAsync(long anomalyId, string siteId, string stationId, string performedBy, string resolution, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            // La resolución primero: si otra acción ya la resolvió, no queda una OperatorAction que no resolvió nada.
            await using var cmd = conn.Command(tx, """
                DECLARE @resolved TABLE (AnomalyId BIGINT NOT NULL);
                UPDATE dbo.SequenceAnomaly SET ResolvedAt = @EdgeNowUtc, ResolvedBy = @PerformedBy, Resolution = @Resolution
                OUTPUT inserted.AnomalyId INTO @resolved (AnomalyId)
                WHERE AnomalyId = @AnomalyId AND ResolvedAt IS NULL AND SiteId = @SiteId AND StationId = @StationId;
                SELECT COUNT(*) FROM @resolved;
                """);
            cmd.DateTime2("@EdgeNowUtc", edgeNowUtc).NVarChar("@PerformedBy", performedBy, 128).NVarChar("@Resolution", resolution, 512)
                .BigInt("@AnomalyId", anomalyId).NVarChar("@SiteId", siteId, 64).NVarChar("@StationId", stationId, 64);
            if ((int)(await cmd.ExecuteScalarAsync(ct))! != 1)
            {
                return new ManualResult(0, false, null);
            }

            var oa = await InsertOperatorActionAsync(conn, tx, new OperatorActionInput(OperatorActionType.ResolveAnomaly, siteId, stationId, null, null,
                performedBy, resolution), edgeNowUtc, ct);
            return new ManualResult(oa, true, null);
        }, ct);

    /// <summary>R01: solicitud aprobada por dos personas. Los CHECK y TR_ReprintAuthorization_Guard validan coherencia.</summary>
    public Task<long> ApproveReprintAsync(ReprintRequest r, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @ids TABLE (AuthorizationId BIGINT NOT NULL);
                INSERT INTO dbo.ReprintAuthorization (
                    PrintJobId, PriorJobStatus, Reason, DuplicateRisk, CartonReference, CartonDisposition, InspectionEvidence,
                    TargetPrinterId, ReprintMarker, EvidenceWindowMs, RequestedBy, RequestedAt, ApprovedBy, ApprovedAt, ExpiresAt, Status)
                OUTPUT inserted.AuthorizationId INTO @ids (AuthorizationId)
                VALUES (
                    @PrintJobId, @PriorJobStatus, @Reason, @DuplicateRisk, @CartonReference, 'HELD', @InspectionEvidence,
                    @TargetPrinterId, @ReprintMarker, @EvidenceWindowMs, @RequestedBy, @RequestedAt, @ApprovedBy, @ApprovedAt, @ExpiresAt, 'APPROVED');
                SELECT AuthorizationId FROM @ids;
                """);
            cmd.Guid("@PrintJobId", r.PrintJobId).VarChar("@PriorJobStatus", r.PriorJobStatus, 20).VarChar("@Reason", r.Reason, 30)
                .VarChar("@DuplicateRisk", r.DuplicateRisk, 30).NVarChar("@CartonReference", r.CartonReference, 256)
                .NVarChar("@InspectionEvidence", r.InspectionEvidence, 512).NVarChar("@TargetPrinterId", r.TargetPrinterId, 64)
                .Bit("@ReprintMarker", r.ReprintMarker).Int("@EvidenceWindowMs", r.EvidenceWindowMs).NVarChar("@RequestedBy", r.RequestedBy, 128)
                .DateTime2("@RequestedAt", r.RequestedAt).NVarChar("@ApprovedBy", r.ApprovedBy, 128).DateTime2("@ApprovedAt", r.ApprovedAt)
                .DateTime2("@ExpiresAt", r.ExpiresAt);
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);

    /// <summary>R03: revocación de una autorización APPROVED.</summary>
    public Task<bool> RevokeReprintAsync(long authorizationId, string revokedBy, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @revoked TABLE (AuthorizationId BIGINT NOT NULL);
                UPDATE dbo.ReprintAuthorization SET Status = 'REVOKED', RevokedBy = @By, RevokedAt = @EdgeNowUtc
                OUTPUT inserted.AuthorizationId INTO @revoked (AuthorizationId)
                WHERE AuthorizationId = @Id AND Status = 'APPROVED';
                SELECT COUNT(*) FROM @revoked;
                """);
            cmd.NVarChar("@By", revokedBy, 128).DateTime2("@EdgeNowUtc", edgeNowUtc).BigInt("@Id", authorizationId);
            return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
        }, ct);

    /// <summary>LABEL_DAMAGED: el retiro de la etiqueta original se registra una sola vez y antes de ejecutar.</summary>
    public Task<bool> RecordOriginalLabelRemovalAsync(long authorizationId, string removedBy, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                DECLARE @done TABLE (AuthorizationId BIGINT NOT NULL);
                UPDATE dbo.ReprintAuthorization SET OriginalLabelRemovedBy = @By, OriginalLabelRemovedAt = @EdgeNowUtc
                OUTPUT inserted.AuthorizationId INTO @done (AuthorizationId)
                WHERE AuthorizationId = @Id AND Status = 'APPROVED' AND OriginalLabelRemovedBy IS NULL;
                SELECT COUNT(*) FROM @done;
                """);
            cmd.NVarChar("@By", removedBy, 128).DateTime2("@EdgeNowUtc", edgeNowUtc).BigInt("@Id", authorizationId);
            return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
        }, ct);
}

public sealed class ManualActionRejectedException(string message) : Exception(message);
