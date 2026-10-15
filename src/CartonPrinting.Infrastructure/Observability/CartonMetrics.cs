using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace CartonPrinting.Infrastructure.Observability;

/// <summary>
/// Métricas de la sección 19 del documento principal y del catálogo de pruebas. Cada instancia
/// tiene su propio <see cref="Meter"/> (OpenTelemetry lo exporta por nombre) y además conserva los
/// totales en memoria, para que las pruebas afirmen valores exactos sin depender de un exportador.
/// </summary>
public sealed class CartonMetrics : IDisposable
{
    private readonly ConcurrentDictionary<string, Counter<long>> _counters = new();
    private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new();
    private readonly ConcurrentDictionary<string, long> _totals = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _samples = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<Guid, long> _received = new();
    private readonly ConcurrentDictionary<Guid, (long Started, long? Completed)> _writes = new();

    /// <summary>Muestras que se conservan en memoria por serie: las más recientes (el exportador recibe todas).</summary>
    public const int MaxSamplesPerSeries = 50_000;

    /// <summary>Marcas de recepción pendientes de consumir por el sender.</summary>
    public const int MaxReceivedMarks = 10_000;

    public CartonMetrics()
    {
        Meter = new Meter(CartonTelemetry.SourceName, "1.1");
    }

    public Meter Meter { get; }

    public void Increment(string name, params (string Key, string Value)[] tags) => Add(name, 1, tags);

    public void Add(string name, long delta, params (string Key, string Value)[] tags)
    {
        var counter = _counters.GetOrAdd(name, n => Meter.CreateCounter<long>(n));
        counter.Add(delta, ToTagList(tags));
        _totals.AddOrUpdate(Key(name, tags), delta, (_, v) => v + delta);
        if (tags.Length > 0)
        {
            _totals.AddOrUpdate(Key(name, []), delta, (_, v) => v + delta);
        }
    }

    /// <summary>Registra una duración o valor (p50, p95, p99 y máximo se calculan de las muestras).</summary>
    public void Record(string name, double value, params (string Key, string Value)[] tags)
    {
        var histogram = _histograms.GetOrAdd(name, n => Meter.CreateHistogram<double>(n));
        histogram.Record(value, ToTagList(tags));
        Keep(_samples.GetOrAdd(Key(name, tags), _ => new ConcurrentQueue<double>()), value);
        if (tags.Length > 0)
        {
            Keep(_samples.GetOrAdd(Key(name, []), _ => new ConcurrentQueue<double>()), value);
        }
    }

    /// <summary>
    /// Marca monotónica de la recepción OPC de una detección con job, para medir <c>opc_receive_to_send_start_ms</c> en el
    /// sender (otro componente del mismo proceso). Se consume una vez. Si se acumulan sin consumir (jobs que vencieron o nunca se
    /// enviaron), se descartan las más antiguas, no las de los jobs en curso.
    /// </summary>
    public void MarkReceived(Guid detectionId, long receivedTimestamp)
    {
        EvictOldest(_received, ts => ts);
        _received[detectionId] = receivedTimestamp;
    }

    /// <summary>
    /// Marcas monotónicas de la escritura de un job: la invocación y, si terminó sin error, la escritura completa. Los tramos de
    /// evidencia de la sección 10.4 son monotónicos: se miden desde estas marcas hasta la recepción de cada observación en el EdgeHost,
    /// nunca con el ObservedAt de otro dispositivo (verificación final de la Fase 7).
    /// </summary>
    public void MarkWrite(Guid printJobId, long startedTimestamp, long? completedTimestamp)
    {
        EvictOldest(_writes, w => w.Started);
        _writes[printJobId] = (startedTimestamp, completedTimestamp);
    }

    /// <summary>Las marcas de la escritura de un job, si este proceso la hizo.</summary>
    public (long Started, long? Completed)? WriteOf(Guid printJobId) => _writes.TryGetValue(printJobId, out var w) ? w : null;

    /// <summary>Al llegar al límite, se descartan las marcas más antiguas (jobs que nunca se completaron), no las de los jobs en curso.</summary>
    public static void EvictOldest<T>(ConcurrentDictionary<Guid, T> marks, Func<T, long> timestamp)
    {
        if (marks.Count < MaxReceivedMarks)
        {
            return;
        }

        foreach (var stale in marks.OrderBy(e => timestamp(e.Value)).Take(MaxReceivedMarks / 10).Select(e => e.Key).ToList())
        {
            marks.TryRemove(stale, out _);
        }
    }

    /// <summary>La marca de recepción de la detección, si este proceso la recibió y nadie la tomó todavía.</summary>
    public long? TakeReceived(Guid detectionId) => _received.TryRemove(detectionId, out var ts) ? ts : null;

    private static void Keep(ConcurrentQueue<double> samples, double value)
    {
        samples.Enqueue(value);
        while (samples.Count > MaxSamplesPerSeries && samples.TryDequeue(out _))
        {
        }
    }

    public void SetGauge(string name, double value, params (string Key, string Value)[] tags)
    {
        var key = Key(name, tags);
        if (_gauges.TryAdd(key, value))
        {
            var capturedTags = ToTagList(tags);
            Meter.CreateObservableGauge(name, () => new Measurement<double>(_gauges[key], capturedTags));
        }
        else
        {
            _gauges[key] = value;
        }
    }

    /// <summary>Total acumulado; sin etiquetas devuelve la suma de todas las series.</summary>
    public long Total(string name, params (string Key, string Value)[] tags) =>
        _totals.TryGetValue(Key(name, tags), out var v) ? v : 0;

    /// <summary>Total de cada serie etiquetada de un contador (para reportes), con las etiquetas como texto.</summary>
    public IReadOnlyDictionary<string, long> Series(string name) =>
        _totals.Where(kv => kv.Key.StartsWith(name + "{", StringComparison.Ordinal)).ToDictionary(kv => kv.Key[name.Length..], kv => kv.Value);

    public IReadOnlyList<double> Samples(string name, params (string Key, string Value)[] tags) =>
        _samples.TryGetValue(Key(name, tags), out var q) ? q.ToArray() : [];

    public double? Gauge(string name, params (string Key, string Value)[] tags) =>
        _gauges.TryGetValue(Key(name, tags), out var v) ? v : null;

    public void Dispose() => Meter.Dispose();

    private static string Key(string name, (string Key, string Value)[] tags) =>
        tags.Length == 0
            ? name
            : name + "{" + string.Join(',', tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}")) + "}";

    private static KeyValuePair<string, object?>[] ToTagList((string Key, string Value)[] tags) =>
        tags.Select(t => new KeyValuePair<string, object?>(t.Key, t.Value)).ToArray();

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Nombres de métricas (sección 19 y catálogo de pruebas).</summary>
public static class MetricNames
{
    public const string DuplicateDetection = "duplicate_detection_total";
    public const string DuplicatePhysicalTransmissions = "duplicate_physical_transmissions_total";
    public const string ClaimConflicts = "claim_conflicts_total";
    public const string ClaimRejected = "claim_rejected_total";
    public const string PrintAttempts = "print_attempts_total";
    public const string PrinterUnknown = "printer_unknown_total";

    /// <summary>1 mientras el sender de la impresora está en pausa por tres fallos seguidos del dispositivo (ESTRATEGIA_RECUPERACION §3).</summary>
    public const string PrinterSenderPaused = "printer_sender_paused";

    public const string PrinterSenderPauses = "printer_sender_pauses_total";
    public const string RestartRecoveredSending = "restart_recovered_sending_total";
    public const string InboxDuplicates = "inbox_duplicates_total";
    public const string DetectionsExpired = "detections_expired_total";
    public const string DetectionsBlocked = "detections_blocked_total";
    public const string JobsExpired = "jobs_expired_total";
    public const string SerialMismatch = "serial_mismatch_total";
    public const string PlcSequenceAnomaly = "plc_sequence_anomaly_total";
    public const string CyclesUnobserved = "cycles_unobserved_total";
    public const string OpcReconnect = "opc_reconnect_total";
    public const string OpcRecordsRejected = "opc_records_rejected_total";
    public const string ErpRequests = "erp_requests_total";
    public const string OutOfOrderDetections = "out_of_order_detections_total";
    public const string PresendCheckRejected = "presend_check_rejected_total";
    public const string EvidenceWindowElapsed = "evidence_window_elapsed_total";
    public const string UnknownResolution = "unknown_resolution_total";
    public const string ExceptionalReprints = "exceptional_reprints_total";
    public const string PrinterOwnerConflicts = "printer_owner_conflicts_total";
    public const string PlcEpochChanges = "plc_epoch_changes_total";
    public const string ClockJump = "clock_jump_total";
    public const string ConfirmedCorrected = "confirmed_corrected_total";
    public const string OutboxPublished = "outbox_published_total";

    public const string OpcCallbackDurationMs = "opc_callback_duration_ms";
    public const string ErpSnapshotWaitMs = "erp_snapshot_wait_ms";
    public const string ErpRequestMs = "erp_request_ms";
    public const string SendToVerifiedMs = "send_to_verified_ms";
    public const string PresendCheckToWriteMs = "presend_check_to_write_ms";
    public const string SendToPrintedMs = "send_to_printed_ms";
    public const string PrintedToAppliedMs = "printed_to_applied_ms";
    public const string AppliedToVerifiedMs = "applied_to_verified_ms";
    public const string PlcToOpcReceiveMs = "plc_to_opc_receive_ms";
    public const string OpcReceiveToJobReadyMs = "opc_receive_to_job_ready_ms";
    public const string OpcReceiveToSendStartMs = "opc_receive_to_send_start_ms";
    public const string SendStartToDeviceAcceptMs = "send_start_to_device_accept_ms";
    public const string PrintDeadlineSlackMs = "print_deadline_slack_ms";
    public const string SendStartToAppliedMs = "send_start_to_applied_ms";
    public const string ClockSkewMs = "clock_skew_ms";

    public const string OutboxPendingAgeSeconds = "outbox_pending_age_seconds";
    public const string ErpCacheAgeSeconds = "erp_cache_age_seconds";
    public const string SequenceGapsOpen = "sequence_gaps_open";
    public const string LabelingReady = "labeling_ready";
    public const string ConfigRevisionActive = "config_revision_active";
}
