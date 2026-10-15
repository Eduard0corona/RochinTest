using System.Diagnostics;
using System.Threading.Channels;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.OpcIngest;

/// <summary>Qué pasó con una notificación o un registro.</summary>
public enum IngestKind
{
    /// <summary>Descartada antes de clasificar (StatusCode no Good, vacía o sin identidad).</summary>
    Rejected,

    /// <summary>Duplicado reconocido por la caché de huellas, sin ir a SQL.</summary>
    KnownDuplicate,

    /// <summary>EPOCH_UNEXPECTED: registro retenido sin detección y estación bloqueada.</summary>
    Retained,

    /// <summary>Pasó por TX-A (registrada, duplicado o colisión según <see cref="IngestOutcome.Result"/>).</summary>
    Registered,
}

public sealed record IngestOutcome(
    IngestKind Kind,
    CycleRecord? Record = null,
    SequenceCase? Case = null,
    RegistrationResult? Result = null,
    long? AnomalyId = null,
    string? Rejection = null);

/// <summary>
/// Ingesta de una estación: un consumidor del canal acotado de notificaciones. Decodifica (solo
/// StatusCode Good), clasifica la secuencia (sección 9.2), acepta épocas, retiene registros de épocas no
/// aceptadas y registra con TX-A. Procesa en orden y de a uno: la marca alta en memoria es de este
/// consumidor; la unicidad la garantiza TX-A con inserción primero.
/// </summary>
public sealed class StationIngestService
{
    private readonly StationSettings _station;
    private readonly RegistrationService _registration;
    private readonly StationConfigStore _stations;
    private readonly PlcEpochStore _epochs;
    private readonly SequenceStore _sequence;
    private readonly TxsSweep _sweep;
    private readonly TxmManualActions _manual;
    private readonly EdgeClock _clock;
    private readonly CartonMetrics _metrics;
    private readonly ILogger _logger;
    private readonly CycleDecoder _decoder;
    private readonly IErpDemand? _erpDemand;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private SequenceClassifier _classifier = new([]);

    public StationIngestService(
        StationSettings station, RegistrationService registration, StationConfigStore stations, PlcEpochStore epochs, SequenceStore sequence,
        TxsSweep sweep, TxmManualActions manual, EdgeClock clock, CartonMetrics metrics, ILogger<StationIngestService>? logger = null,
        IErpDemand? erpDemand = null)
    {
        _erpDemand = erpDemand;
        _station = station;
        _registration = registration;
        _stations = stations;
        _epochs = epochs;
        _sequence = sequence;
        _sweep = sweep;
        _manual = manual;
        _clock = clock;
        _metrics = metrics;
        _logger = logger ?? NullLogger<StationIngestService>.Instance;
        _decoder = new CycleDecoder(station);
    }

    public SequenceClassifier Classifier => _classifier;

    /// <summary>Reconstruye el estado de secuencia desde SQL (al arrancar y tras una acción manual sobre épocas).</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        var epochs = await _sequence.LoadEpochsAsync(_station.SiteId, _station.StationId, ct);
        _classifier = new SequenceClassifier(epochs);
    }

    /// <summary>Consume el canal hasta que se cancele. Una excepción de SQL detiene el consumidor: no se salta un registro.</summary>
    public async Task RunAsync(ChannelReader<RawNotification> reader, CancellationToken ct)
    {
        await ReloadAsync(ct);
        await foreach (var raw in reader.ReadAllAsync(ct))
        {
            await ProcessAsync(raw, ct);
        }
    }

    public async Task<IngestOutcome> ProcessAsync(RawNotification raw, CancellationToken ct)
    {
        var decoded = _decoder.Decode(raw);
        if (decoded.Record is not { } record)
        {
            _metrics.Increment(MetricNames.OpcRecordsRejected, ("cause", decoded.Rejection!), ("transport", raw.Transport));
            if (decoded.Rejection != DecodeResult.Empty)
            {
                _logger.LogWarning("Notificación OPC descartada ({Cause}, StatusCode 0x{Status:X8}, transporte {Transport})",
                    decoded.Rejection, raw.StatusCode, raw.Transport);
            }

            return new IngestOutcome(IngestKind.Rejected, Rejection: decoded.Rejection);
        }

        return await ProcessRecordAsync(record, ct);
    }

    public async Task<IngestOutcome> ProcessRecordAsync(CycleRecord record, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            return await ClassifyAndRegisterAsync(record, ct);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<IngestOutcome> ClassifyAndRegisterAsync(CycleRecord record, CancellationToken ct)
    {
        using var activity = CartonTelemetry.ActivitySource.StartActivity("carton.ingest");
        activity?.SetTag(CartonTelemetry.Tags.IdempotencyKey, record.Identity.IdempotencyKey);
        activity?.SetTag(CartonTelemetry.Tags.StationId, record.StationId);
        activity?.SetTag("transport", record.Transport);

        var (state, revision) = await _stations.GetActiveAsync(_station.SiteId, _station.StationId, ct)
            ?? throw new InvalidOperationException($"La estación {_station.SiteId}/{_station.StationId} no tiene StationConfig.");
        await CheckClockSkewAsync(record, state, revision, ct);

        for (var guard = 0; ; guard++)
        {
            var now = _clock.UtcNow;
            var verdict = _classifier.Classify(record, revision, now);
            switch (verdict)
            {
                case SequenceVerdict.AcceptEpoch accept when guard == 0:
                    await AcceptEpochAsync(record, accept, revision, now, ct);
                    continue;

                case SequenceVerdict.AcceptEpoch:
                    throw new UnreachableException("La época aceptada no quedó como actual.");

                case SequenceVerdict.RetainEpochUnexpected retain:
                    var anomalyId = await _sequence.RetainAsync(new AnomalyRow(
                        record.SiteId, record.StationId, record.PlcEpoch, record.CycleId, AnomalyType.EpochUnexpected, AnomalySeverity.Blocking,
                        null, null, record.SourceFingerprint(), RetainedRecords.ToJson(record), now, revision.ConfigRevisionId),
                        BlockReason.EpochUnexpected, ct);
                    _metrics.Increment(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.EpochUnexpected));
                    _logger.LogWarning("EPOCH_UNEXPECTED en {Key}: {Detail}. Registro retenido (anomalía {AnomalyId}); estación bloqueada.",
                        record.Identity.IdempotencyKey, retain.Detail, anomalyId);
                    activity?.SetTag("sequence", "EPOCH_UNEXPECTED");
                    return new IngestOutcome(IngestKind.Retained, record, AnomalyId: anomalyId);

                case SequenceVerdict.KnownDuplicate:
                    _metrics.Increment(MetricNames.DuplicateDetection, ("transport", record.Transport));
                    activity?.SetTag("sequence", "KNOWN_DUPLICATE");
                    return new IngestOutcome(IngestKind.KnownDuplicate, record);

                case SequenceVerdict.Register register:
                    var result = await _registration.RegisterAsync(record, _station, revision, register.Decision, ct);
                    _classifier.Observe(record, register.Case, result.Outcome);
                    if (result.FinalStatus == DetectionStatus.WaitingErp && record.WorkOrderId is { } workOrder)
                    {
                        _erpDemand?.WorkOrderNeeded(workOrder);
                    }

                    if (register.Case == SequenceCase.Late && result.Outcome == RegistrationOutcome.Registered)
                    {
                        _metrics.Increment(MetricNames.OutOfOrderDetections, ("transport", record.Transport));
                    }

                    activity?.SetTag("sequence", register.Case.ToString());
                    return new IngestOutcome(IngestKind.Registered, record, register.Case, result, result.AnomalyId);

                default:
                    throw new UnreachableException(verdict.GetType().Name);
            }
        }
    }

    /// <summary>
    /// CLOCK_SKEW (sección 10.1): con PLC_TIMESTAMP, un DetectionTimestamp posterior a la recepción en el
    /// EdgeHost es un límite inferior del adelanto del reloj del PLC (la latencia solo lo achica). Si supera
    /// ClockSkewToleranceMs, la estación se bloquea antes de registrar el ciclo, de modo que ningún job se
    /// crea con un plazo calculado con ese reloj; el ciclo queda BLOCKED con STATION_DISABLED. Un reloj del
    /// PLC atrasado acorta los plazos (es restrictivo) y no se puede separar de la latencia: no se detecta.
    /// </summary>
    private async Task CheckClockSkewAsync(CycleRecord record, StationState state, StationRevision revision, CancellationToken ct)
    {
        if (revision.DeadlineReference != DeadlineReference.PlcTimestamp || record.DetectionTimestamp is not { } ts)
        {
            return;
        }

        var aheadMs = (CanonicalTimestamp.Truncate(ts) - CanonicalTimestamp.Truncate(record.ReceivedAt)).TotalMilliseconds;
        _metrics.Record(MetricNames.ClockSkewMs, aheadMs, ("station", record.StationId));
        if (revision.ClockSkewToleranceMs is { } tolerance && aheadMs > tolerance && state.LabelingEnabled)
        {
            await _stations.BlockAsync(record.SiteId, record.StationId, BlockReason.ClockSkew, "SYSTEM", null, _clock.UtcNow, ct);
            _logger.LogError("Reloj del PLC adelantado al menos {Ahead} ms en {Station} (tolerancia {Tolerance} ms): estación bloqueada con CLOCK_SKEW",
                aheadMs, record.StationId, tolerance);
        }
    }

    /// <summary>Primera época de la estación (INITIAL) o actual + 1 con AUTO_INCREMENT_BY_ONE: nueva fila en PlcEpochHistory.</summary>
    private async Task AcceptEpochAsync(CycleRecord record, SequenceVerdict.AcceptEpoch accept, StationRevision revision, DateTime now, CancellationToken ct)
    {
        try
        {
            await _epochs.InsertAsync(new PlcEpoch(record.SiteId, record.StationId, accept.Epoch, "PLC", accept.AcceptanceMode, record.CycleId,
                CanonicalTimestamp.Truncate(now), "SYSTEM", revision.ConfigRevisionId, null), ct);
            _metrics.Increment(MetricNames.PlcEpochChanges, ("mode", accept.AcceptanceMode));
            _classifier.AcceptEpoch(accept.Epoch, CanonicalTimestamp.Truncate(now));
            _logger.LogInformation("Época {Epoch} aceptada ({Mode}) en {Station} desde el ciclo {Cycle}",
                accept.Epoch, accept.AcceptanceMode, record.StationId, record.CycleId);
        }
        catch (SqlException ex) when (SequenceStore.IsEpochAlreadyAccepted(ex))
        {
            // Otro proceso o un intento anterior ya la aceptó: el orden de épocas sale de SQL.
            await ReloadAsync(ct);
        }
    }

    /// <summary>
    /// Reingreso de un registro retenido (EPOCH_UNEXPECTED) que OT liberó: exige su época ya aceptada
    /// (confirmada con CONFIRM_EPOCH), registra el mismo registro latched con Transport = ANOMALY_REINGEST
    /// y conserva su ReceivedAt, de modo que su propio plazo decide; después resuelve la anomalía con su
    /// OperatorAction RESOLVE_ANOMALY. Repetirlo tras una caída da un duplicado auténtico, no otra detección.
    /// </summary>
    public async Task<IngestOutcome> ReingestAsync(long anomalyId, string performedBy, string resolution, CancellationToken ct)
    {
        await ReloadAsync(ct);
        var retained = (await _sequence.ListRetainedAsync(_station.SiteId, _station.StationId, ct)).FirstOrDefault(a => a.AnomalyId == anomalyId)
            ?? throw new InvalidOperationException($"La anomalía {anomalyId} no es un registro retenido sin resolver de la estación {_station.StationId}.");
        var record = RetainedRecords.Reingest(retained.IncomingRecord);
        if (!_classifier.Epochs.Any(e => e.Epoch == record.PlcEpoch))
        {
            throw new InvalidOperationException($"La época {record.PlcEpoch} no está aceptada: el registro retenido no se puede reingresar.");
        }

        var outcome = await ProcessRecordAsync(record, ct);
        await _manual.ResolveAnomalyAsync(anomalyId, _station.SiteId, _station.StationId, performedBy, resolution, _clock.UtcNow, ct);
        return outcome;
    }

    /// <summary>G03: cierra los huecos cuya gracia venció y cuenta los ciclos no observados.</summary>
    public async Task<IReadOnlyList<GapClosure>> SweepGapsAsync(CancellationToken ct)
    {
        var closed = await _sweep.CloseGapsAsync(_station.SiteId, _station.StationId, _clock.UtcNow, ct);
        foreach (var gap in closed.Where(g => g.UnrecoveredCount > 0))
        {
            _metrics.Add(MetricNames.CyclesUnobserved, (long)gap.UnrecoveredCount, ("station", _station.StationId));
        }

        _metrics.SetGauge(MetricNames.SequenceGapsOpen, await _sequence.CountOpenGapsAsync(_station.SiteId, _station.StationId, ct),
            ("station", _station.StationId));
        return closed;
    }
}
