using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;

namespace CartonPrinting.Orchestration;

/// <summary>
/// Llegada de un snapshot ERP válido para una detección en WAITING_ERP (D06, D08): prepara fuera de la
/// transacción el plazo y el margen efectivos, el serial y el payload, y ejecuta TX-E, que repite la
/// validación de TX-A. Con el plazo efectivo ya vencido no reserva serial: TX-E no crea el job y el
/// barrido aplica D07.
/// </summary>
public sealed class ErpArrivalService(
    TxeErpArrival txe, StationConfigStore stations, LabelSerialSource serials, StationSettings station, EdgeClock clock, CartonMetrics metrics)
{
    public async Task<ErpArrivalResult?> ProcessAsync(DetectionRecord detection, ErpSnapshot snapshot, CancellationToken ct)
    {
        if (detection.WorkOrderId != snapshot.WorkOrderId)
        {
            throw new ArgumentException($"El snapshot {snapshot.WorkOrderId} no es de la orden de la detección {detection.IdempotencyKey}.", nameof(snapshot));
        }

        var frozen = await stations.GetRevisionAsync(detection.ConfigRevisionId, ct);
        var active = (await stations.GetActiveAsync(detection.SiteId, detection.StationId, ct))
            ?? throw new InvalidOperationException($"La estación {detection.SiteId}/{detection.StationId} no tiene StationConfig.");
        var effectiveDeadline = DeadlinePolicy.Effective(detection.DeadlineAt, active.Active, detection);
        var margin = DeadlinePolicy.EffectiveSendMargin(frozen, active.Active);
        var now = clock.UtcNow;
        if (now >= effectiveDeadline.AddMilliseconds(-margin))
        {
            return null;
        }

        var serial = LabelSerialSource.Format(station.LabelSerialPrefix, await serials.NextAsync(ct));
        var label = LabelRenderer.Render(frozen, snapshot, detection.Identity, serial, detection.CartonId);
        var plan = new ErpArrivalPlan(detection.DetectionId,
            new JobInsert(Guid.NewGuid(), frozen.LabelType, frozen.PrinterId, snapshot.WorkOrderId, snapshot.Revision, snapshot.ProductId,
                snapshot.LotId, serial, frozen.TemplateRevision, label.Payload, label.PayloadHash, detection.DeadlineAt),
            frozen.RequiresCartonId, effectiveDeadline, margin, frozen.LabelType, clock.UtcNow);
        var result = await txe.ExecuteAsync(plan, ct);

        switch (result.FinalStatus)
        {
            case DetectionStatus.JobCreated:
                metrics.Record(MetricNames.ErpSnapshotWaitMs, (clock.UtcNow - detection.ReceivedAt).TotalMilliseconds);
                break;
            case DetectionStatus.Blocked:
                metrics.Increment(MetricNames.DetectionsBlocked, ("reason", result.FinalReason!));
                break;
        }

        return result;
    }
}
