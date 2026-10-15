using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;

namespace CartonPrinting.Orchestration;

/// <summary>
/// Resultado de la clasificación de secuencia (sección 9.2) que acompaña a una clave nueva: un estado
/// terminal decidido antes de TX-A, la anomalía que se registra con la detección y el hueco que abre.
/// </summary>
public sealed record SequenceDecision
{
    public static readonly SequenceDecision InOrder = new();

    public string? TerminalStatus { get; init; }

    public string? TerminalReason { get; init; }

    public AnomalyInsert? Anomaly { get; init; }

    public GapOpen? Gap { get; init; }
}

/// <summary>
/// Registro de un ciclo: prepara fuera de la transacción la huella, el plazo y, si hay snapshot ERP
/// vigente, el serial (NEXT VALUE FOR antes de TX-A), el payload y su hash; después ejecuta TX-A.
/// Nunca consulta al ERP: solo la caché local.
/// </summary>
public sealed class RegistrationService(
    TxaRegisterDetection txa,
    ErpCacheStore erpCache,
    LabelSerialSource serials,
    EdgeClock clock,
    CartonMetrics metrics,
    ReadyJobSignal? readySignal = null)
{
    public async Task<RegistrationResult> RegisterAsync(
        CycleRecord record, StationSettings station, StationRevision revision, SequenceDecision sequence, CancellationToken ct)
    {
        var identity = record.Identity;
        using var activity = CartonTelemetry.ActivitySource.StartActivity("carton.register");
        activity?.SetTag(CartonTelemetry.Tags.IdempotencyKey, identity.IdempotencyKey);
        activity?.SetTag(CartonTelemetry.Tags.CorrelationId, identity.DetectionId);
        activity?.SetTag(CartonTelemetry.Tags.StationId, record.StationId);

        var plan = await PlanAsync(record, station, revision, sequence, ct);
        var result = await txa.ExecuteAsync(plan, ct);
        Record(result, record, plan);
        activity?.SetTag("outcome", result.Outcome.ToString());
        activity?.SetTag("detectionStatus", result.FinalStatus);
        if (result.PrintJobId is { } jobId)
        {
            activity?.SetTag(CartonTelemetry.Tags.PrintJobId, jobId);
            activity?.SetTag(CartonTelemetry.Tags.LabelSerial, plan.Job?.LabelSerial);
            readySignal?.Notify();
        }

        return result;
    }

    public async Task<RegistrationPlan> PlanAsync(
        CycleRecord record, StationSettings station, StationRevision revision, SequenceDecision sequence, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (terminalStatus, terminalReason) = (sequence.TerminalStatus, sequence.TerminalReason);
        var deadline = DeadlinePolicy.Compute(revision, record.DetectionTimestamp, record.ReceivedAt, record.Transport, record.QueueOverflow,
            opensGap: sequence.Gap is not null);

        if (!record.Coherent || record.WorkOrderId is null)
        {
            // Registro incoherente (campos de ciclos distintos, ADR 0001) o sin orden de trabajo (obligatoria
            // en el contrato con el PLC): D05 con INCOHERENT_SNAPSHOT. Va antes que cualquier otra
            // clasificación terminal porque una detección sin orden solo puede quedar BLOCKED
            // (CK_CartonDetection_WorkOrder) y una con datos mezclados no debe parecer un simple vencimiento;
            // se respeta un bloqueo que la secuencia ya decidió.
            if (terminalStatus != DetectionStatus.Blocked)
            {
                (terminalStatus, terminalReason) = (DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot);
            }
        }
        else if (terminalStatus is null && deadline is null)
        {
            // Sin referencia confiable no se fabrica un plazo (I26): DeadlineAt = ReceivedAt.
            (terminalStatus, terminalReason) = revision.DeadlineReference == DeadlineReference.PlcTimestamp
                ? (DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot)
                : (DetectionStatus.Expired, DetectionReason.LateArrival);
        }
        else if (terminalStatus is null && revision.RequiresCartonId && record.CartonId is null)
        {
            // SI-04 (vacío, clase B): CARTON_ID_REQUIRED sin CartonId. Interino de falla cerrada, solo para
            // un registro que de otro modo sería candidato; no pasa por encima de D04 ni de la secuencia.
            (terminalStatus, terminalReason) = (DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot);
        }

        var detection = new DetectionInsert
        {
            SiteId = record.SiteId,
            StationId = record.StationId,
            PlcEpoch = record.PlcEpoch,
            CycleId = record.CycleId,
            SourceFingerprint = record.SourceFingerprint(),
            Transport = record.Transport,
            QueueOverflow = record.QueueOverflow,
            DetectionTimestamp = record.DetectionTimestamp is { } t ? CanonicalTimestamp.Truncate(t) : null,
            OpcSourceTimestamp = record.OpcSourceTimestamp is { } s ? CanonicalTimestamp.Truncate(s) : null,
            OpcServerTimestamp = record.OpcServerTimestamp is { } v ? CanonicalTimestamp.Truncate(v) : null,
            ReceivedAt = CanonicalTimestamp.Truncate(record.ReceivedAt),
            DeadlineReference = revision.DeadlineReference,
            DeadlineAt = deadline ?? CanonicalTimestamp.Truncate(record.ReceivedAt),
            ConfigRevisionId = revision.ConfigRevisionId,
            WorkOrderId = record.WorkOrderId,
            CartonId = record.CartonId,
            OpcNodeId = record.OpcNodeId,
        };

        JobInsert? job = null;
        if (terminalStatus is null && now < detection.DeadlineAt.AddMilliseconds(-revision.SendMarginMs)
            && await erpCache.FindValidAsync(record.WorkOrderId!, now, ct) is { } snapshot)
        {
            // El serial se obtiene antes de TX-A; si TX-A termina en duplicado o rollback, queda como hueco.
            var serial = LabelSerialSource.Format(station.LabelSerialPrefix, await serials.NextAsync(ct));
            var label = LabelRenderer.Render(revision, snapshot, record.Identity, serial, record.CartonId);
            job = new JobInsert(Guid.NewGuid(), revision.LabelType, revision.PrinterId, snapshot.WorkOrderId, snapshot.Revision,
                snapshot.ProductId, snapshot.LotId, serial, revision.TemplateRevision, label.Payload, label.PayloadHash, detection.DeadlineAt);
        }

        return new RegistrationPlan
        {
            Detection = detection,
            Revision = revision,
            TerminalStatus = terminalStatus,
            TerminalReason = terminalReason,
            Job = job,
            Anomaly = sequence.Anomaly,
            Gap = sequence.Gap,
            EdgeNowUtc = clock.UtcNow,
            EffectiveSendMarginMs = revision.SendMarginMs,
        };
    }

    private void Record(RegistrationResult result, CycleRecord record, RegistrationPlan plan)
    {
        // Flanco a notificación (sección 10.4): solo una notificación en vivo, y válido solo con los relojes del PLC y del
        // EdgeHost sincronizados (lo vigila clock_skew_ms).
        if (record is { Transport: Transport.Subscription, QueueOverflow: false, DetectionTimestamp: { } flank } && result.Outcome != RegistrationOutcome.Duplicate)
        {
            metrics.Record(MetricNames.PlcToOpcReceiveMs, (record.ReceivedAt - flank).TotalMilliseconds);
        }

        switch (result.Outcome)
        {
            case RegistrationOutcome.Duplicate:
                metrics.Increment(MetricNames.DuplicateDetection, ("transport", record.Transport));
                return;
            case RegistrationOutcome.Collision:
                metrics.Increment(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.IdentityCollision));
                return;
        }

        switch (result.FinalStatus)
        {
            case DetectionStatus.Blocked:
                metrics.Increment(MetricNames.DetectionsBlocked, ("reason", result.FinalReason!));
                break;
            case DetectionStatus.Expired:
                metrics.Increment(MetricNames.DetectionsExpired, ("reason", result.FinalReason!), ("transport", record.Transport));
                break;
            case DetectionStatus.JobCreated when record.ReceivedTimestamp is { } received:
                metrics.Record(MetricNames.OpcReceiveToJobReadyMs, clock.GetElapsedTime(received).TotalMilliseconds);
                metrics.MarkReceived(result.DetectionId, received);
                break;
        }

        if (plan.Anomaly is { } anomaly)
        {
            metrics.Increment(MetricNames.PlcSequenceAnomaly, ("type", anomaly.AnomalyType));
        }
    }
}
