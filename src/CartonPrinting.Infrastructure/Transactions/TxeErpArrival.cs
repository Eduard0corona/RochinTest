using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>
/// Plan de TX-E preparado fuera de la transacción (serial ya obtenido, payload renderizado). El plazo y el margen
/// efectivos se calcularon con la revisión <c>ActiveConfigRevisionId</c>; <c>SnapshotGtin</c> es el GTIN con que se
/// armó la etiqueta.
/// </summary>
public sealed record ErpArrivalPlan(
    Guid DetectionId,
    JobInsert Job,
    bool RequiresCartonId,
    DateTime EffectiveDeadlineUtc,
    int EffectiveSendMarginMs,
    string LabelType,
    DateTime EdgeNowUtc,
    string? SnapshotGtin,
    long ActiveConfigRevisionId);

public sealed record ErpArrivalResult(string? FinalStatus, string? FinalReason, Guid? PrintJobId, PrintJobResultEvent? Published);

/// <summary>
/// TX-E: llega un snapshot ERP válido para una detección en WAITING_ERP. Repite la validación de
/// TX-A (plazo efectivo con margen, estación habilitada, recirculación con bloqueo de rango):
/// D06 crea el job; D08 bloquea con el motivo real (STATION_DISABLED o RECIRCULATED_CARTON). Un
/// snapshot que llega con el plazo vencido no crea job ni cambia la detección: sigue en WAITING_ERP y
/// el barrido (TX-S) aplica D07 con ERP_UNAVAILABLE, la transacción que le asigna la máquina de estados.
/// Tampoco hace nada si la revisión activa ya no es la del plan (el plazo calculado no vale: se reintenta) o si
/// la fila de la caché no tiene el contenido de la etiqueta (I10).
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

            // Con RCSI se lee la versión confirmada de la estación: TX-E equivale a ejecutarse antes de una activación o
            // de un bloqueo concurrentes. Sin UPDLOCK, para no formar un ciclo con TX-A, que inserta su detección y
            // después bloquea la estación.
            var station = await StationConfigStore.GetActiveAsync(conn, tx, d.SiteId, d.StationId, ct)
                ?? throw new InvalidOperationException($"La estación {d.SiteId}/{d.StationId} no tiene StationConfig.");
            if (station.Active.ConfigRevisionId != plan.ActiveConfigRevisionId)
            {
                return new ErpArrivalResult(d.Status, d.Reason, null, null);
            }

            var deadline = d.DeadlineAt < plan.EffectiveDeadlineUtc ? d.DeadlineAt : plan.EffectiveDeadlineUtc;
            if (plan.EdgeNowUtc >= deadline.AddMilliseconds(-plan.EffectiveSendMarginMs))
            {
                // Snapshot tardío: 0 filas. D07 lo aplica TX-S.
                return new ErpArrivalResult(d.Status, d.Reason, null, null);
            }

            if (!station.State.LabelingEnabled)
            {
                return await FinishAsync(conn, tx, d, DetectionStatus.Blocked, DetectionReason.StationDisabled, plan, ct);
            }

            if (plan.RequiresCartonId && d.CartonId is not null
                && await TxaRegisterDetection.IsRecirculatedAsync(conn, tx, d.SiteId, d.CartonId, d.DetectionId, ct))
            {
                return await FinishAsync(conn, tx, d, DetectionStatus.Blocked, DetectionReason.RecirculatedCarton, plan, ct);
            }

            if (!await ErpCacheStore.IsRevisionValidAsync(conn, tx, plan.Job.WorkOrderId, plan.Job.ErpRevision, plan.Job.ProductId, plan.Job.LotId,
                    plan.SnapshotGtin, plan.EdgeNowUtc, ct)
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
