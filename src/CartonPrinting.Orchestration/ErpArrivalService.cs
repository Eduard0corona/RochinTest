using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;

namespace CartonPrinting.Orchestration;

/// <summary>
/// Llegada de un snapshot ERP válido para una detección en WAITING_ERP (D06, D08): prepara fuera de la
/// transacción el plazo y el margen efectivos, el serial y el payload, y ejecuta TX-E, que repite la
/// validación de TX-A. Con el plazo efectivo ya vencido no reserva serial: TX-E no crea el job y el
/// barrido aplica D07. Devuelve null si TX-E no hizo ninguna transición.
/// </summary>
public sealed class ErpArrivalService(
    TxeErpArrival txe, StationConfigStore stations, LabelSerialSource serials, StationSettings station, EdgeClock clock, CartonMetrics metrics,
    ReadyJobSignal? readySignal = null)
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
            DeadlinePolicy.EffectiveRequiresCartonId(frozen, active.Active), effectiveDeadline, margin, frozen.LabelType, now, snapshot.Gtin,
            active.Active.ConfigRevisionId);
        var result = await txe.ExecuteAsync(plan, ct);

        // Solo una transición se mide: una TX-E sin efecto devuelve el estado que ya tenía la detección.
        if (result.PrintJobId is not null)
        {
            metrics.Record(MetricNames.ErpSnapshotWaitMs, (clock.UtcNow - detection.ReceivedAt).TotalMilliseconds);
            readySignal?.Notify();
        }
        else if (result.Published is not null && result.FinalStatus == DetectionStatus.Blocked)
        {
            metrics.Increment(MetricNames.DetectionsBlocked, ("reason", result.FinalReason!));
        }

        return result.PrintJobId is not null || result.Published is not null ? result : null;
    }
}
