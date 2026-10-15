using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>Plan de TX-E preparado fuera de la transacción (serial ya obtenido, payload renderizado).</summary>
public sealed record ErpArrivalPlan(
    Guid DetectionId,
    JobInsert Job,
    bool RequiresCartonId,
    DateTime EffectiveDeadlineUtc,
    int EffectiveSendMarginMs,
    string LabelType,
    DateTime EdgeNowUtc);

public sealed record ErpArrivalResult(string? FinalStatus, string? FinalReason, Guid? PrintJobId, PrintJobResultEvent? Published);

/// <summary>
/// TX-E: llega un snapshot ERP válido para una detección en WAITING_ERP. Repite la validación de
/// TX-A (plazo efectivo con margen, estación habilitada, recirculación con bloqueo de rango):
/// D06 crea el job; D08 bloquea con el motivo real (STATION_DISABLED o RECIRCULATED_CARTON); un
/// snapshot que llega con el plazo vencido no crea job y la detección vence con ERP_UNAVAILABLE (D07).
/// </summary>
public sealed class TxeErpArrival(SqlDb db)
{
    public Task<ErpArrivalResult> ExecuteAsync(ErpArrivalPlan plan, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var d = await DetectionQueries.GetAsync(conn, tx, plan.DetectionId, ct, updLock: true);
            if (d is null || d.Status != DetectionStatus.WaitingErp)
            {
                return new ErpArrivalResult(d?.Status, d?.Reason, null, null);
            }

            var deadline = d.DeadlineAt < plan.EffectiveDeadlineUtc ? d.DeadlineAt : plan.EffectiveDeadlineUtc;
            if (plan.EdgeNowUtc >= deadline.AddMilliseconds(-plan.EffectiveSendMarginMs))
            {
                // D07 ("snapshot que llega tarde") dentro de TX-E: state-machine.json asigna D07 a TX-S, pero
                // su disparador incluye el snapshot tardío. Aplicarlo aquí evita crear un job ya vencido y
                // deja la detección en el mismo estado que dejaría el barrido (SI-06).
                return await FinishAsync(conn, tx, d, DetectionStatus.Expired, DetectionReason.ErpUnavailable, plan, ct);
            }

            var station = await StationConfigStore.GetActiveAsync(conn, tx, d.SiteId, d.StationId, ct)
                ?? throw new InvalidOperationException($"La estación {d.SiteId}/{d.StationId} no tiene StationConfig.");
            if (!station.State.LabelingEnabled)
            {
                return await FinishAsync(conn, tx, d, DetectionStatus.Blocked, DetectionReason.StationDisabled, plan, ct);
            }

            if (plan.RequiresCartonId && d.CartonId is not null
                && await TxaRegisterDetection.IsRecirculatedAsync(conn, tx, d.SiteId, d.CartonId, d.DetectionId, ct))
            {
                return await FinishAsync(conn, tx, d, DetectionStatus.Blocked, DetectionReason.RecirculatedCarton, plan, ct);
            }

            if (!await ErpCacheStore.IsRevisionValidAsync(conn, tx, plan.Job.WorkOrderId, plan.Job.ErpRevision, plan.EdgeNowUtc, ct)
                || plan.Job.WorkOrderId != d.WorkOrderId)
            {
                return new ErpArrivalResult(d.Status, d.Reason, null, null);
            }

            await TxaRegisterDetection.InsertJobAsync(conn, tx, d.Identity, d.CartonId, d.ConfigRevisionId, plan.Job, ct);
            await MoveAsync(conn, tx, d.DetectionId, DetectionStatus.JobCreated, null, plan.EdgeNowUtc, ct);
            return new ErpArrivalResult(DetectionStatus.JobCreated, null, plan.Job.PrintJobId, null);
        }, ct);

    private static async Task<ErpArrivalResult> FinishAsync(
        SqlConnection conn, SqlTransaction tx, DetectionRecord d, string status, string reason, ErpArrivalPlan plan, CancellationToken ct)
    {
        await MoveAsync(conn, tx, d.DetectionId, status, reason, plan.EdgeNowUtc, ct);
        var updated = (await DetectionQueries.GetAsync(conn, tx, d.DetectionId, ct))!;
        var evt = await ResultPublisher.PublishDetectionAsync(conn, tx, updated, plan.LabelType, plan.EdgeNowUtc, ct);
        return new ErpArrivalResult(status, reason, null, evt);
    }

    private static async Task MoveAsync(SqlConnection conn, SqlTransaction tx, Guid detectionId, string status, string? reason, DateTime edgeNowUtc, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            DECLARE @moved TABLE (DetectionId UNIQUEIDENTIFIER NOT NULL);
            UPDATE dbo.CartonDetection
            SET Status = @Status, Reason = @Reason, UpdatedAt = @EdgeNowUtc
            OUTPUT inserted.DetectionId INTO @moved (DetectionId)
            WHERE DetectionId = @DetectionId AND Status = 'WAITING_ERP';
            SELECT COUNT(*) FROM @moved;
            """);
        cmd.VarChar("@Status", status, 20).VarChar("@Reason", reason, 40).DateTime2("@EdgeNowUtc", edgeNowUtc).Guid("@DetectionId", detectionId);
        if ((int)(await cmd.ExecuteScalarAsync(ct))! != 1)
        {
            throw new InvalidOperationException($"La detección {detectionId} dejó de estar en WAITING_ERP dentro de TX-E.");
        }
    }
}
