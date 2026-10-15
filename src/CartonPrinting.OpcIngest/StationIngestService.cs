using System.Diagnostics;
using System.Threading.Channels;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Signals;
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

/// <summary>
/// Falla cerrada con SQL Server fuera de servicio, perfil mínimo (ESTRATEGIA_RECUPERACION §3). Los tiempos son de E/S, en tiempo
/// real: no deciden el plazo de ninguna caja.
/// </summary>
public sealed record SqlFailClosedOptions
{
    /// <summary>Tiempo máximo de cada intento de registrar un ciclo, o de recargar el estado al volver SQL.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Cada cuánto se prueba si SQL volvió mientras la ingesta lo tiene declarado caído.</summary>
    public TimeSpan RecoveryProbeInterval { get; init; } = TimeSpan.FromSeconds(1);
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
    private readonly SqlFailClosedOptions _failClosed;
    private readonly SqlOutage _outage;
    private SequenceClassifier _classifier = new([]);
    private volatile StationRevision? _revision;

    public StationIngestService(
        StationSettings station, RegistrationService registration, StationConfigStore stations, PlcEpochStore epochs, SequenceStore sequence,
        TxsSweep sweep, TxmManualActions manual, EdgeClock clock, CartonMetrics metrics, ILogger<StationIngestService>? logger = null,
        IErpDemand? erpDemand = null, SqlFailClosedOptions? failClosed = null, SqlOutage? outage = null)
    {
        _erpDemand = erpDemand;
        _failClosed = failClosed ?? new SqlFailClosedOptions();
        _outage = outage ?? new SqlOutage(clock);
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

    /// <summary>La caída de SQL Server que comparten la ingesta, el latido y el sender de línea.</summary>
    public SqlOutage Outage => _outage;

    /// <summary>
    /// true desde que se declara SQL Server no disponible hasta que la vigilancia recarga el estado y comprueba el camino de TX-A.
    /// Mientras tanto el latido mantiene LabelingReady en falso.
    /// </summary>
    public bool SqlUnavailable => _outage.Declared;

    /// <summary>Declara la caída (la ingesta o, en las pruebas, quien simula a otro componente) y la registra una vez.</summary>
    public void DeclareSqlUnavailable(string source)
    {
        if (_outage.Declare())
        {
            _logger.LogError("SQL Server no disponible en {Station} ({Source}): falla cerrada. LabelingReady baja y los ciclos hasta que vuelva serán un hueco",
                _station.StationId, source);
        }
    }

    /// <summary>Reconstruye el estado de secuencia desde SQL (al arrancar y tras una acción manual sobre épocas).</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            await ReloadCoreAsync(ct);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Recarga con el consumidor ya serializado. Cuenta las épocas confirmadas por una persona que aparecen.</summary>
    private async Task ReloadCoreAsync(CancellationToken ct)
    {
        var known = _classifier.Epochs.Select(e => e.Epoch).ToHashSet();
        var epochs = await _sequence.LoadEpochsAsync(_station.SiteId, _station.StationId, ct);
        foreach (var confirmed in epochs.Where(e => e.AcceptanceMode == PlcEpochStore.OperatorConfirmed && !known.Contains(e.Epoch) && known.Count > 0))
        {
            _metrics.Increment(MetricNames.PlcEpochChanges, ("mode", confirmed.AcceptanceMode));
        }

        _classifier = new SequenceClassifier(epochs);
    }

    /// <summary>
    /// Consume el canal hasta que se cancele, con la vigilancia de la recuperación de SQL Server en paralelo. Con SQL fuera de
    /// servicio, el registro no se registra (será un hueco al volver SQL; ver <see cref="ProcessLiveAsync"/>). Cualquier otra
    /// excepción de SQL detiene el consumidor: no se salta un registro por un defecto.
    /// </summary>
    public async Task RunAsync(ChannelReader<RawNotification> reader, CancellationToken ct)
    {
        // Recarga otra vez antes de consumir; si SQL no responde justo ahora, es una caída como cualquier otra, no un defecto.
        try
        {
            await WithinAttemptAsync(async t =>
            {
                await ReloadAsync(t);
                return true;
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsUnavailable(ex))
        {
            DeclareSqlUnavailable("recarga al empezar a consumir");
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumer = ConsumeAsync(reader, stop.Token);
        var recovery = RecoverWhileUnavailableAsync(stop.Token);
        await Task.WhenAny(consumer, recovery);
        await stop.CancelAsync();
        await IgnoreCancellationAsync(consumer);
        await IgnoreCancellationAsync(recovery);
        ct.ThrowIfCancellationRequested();
    }

    private async Task ConsumeAsync(ChannelReader<RawNotification> reader, CancellationToken ct)
    {
        await foreach (var raw in reader.ReadAllAsync(ct))
        {
            await ProcessAsync(raw, ct);
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Detención: quien llama decide con su propio token.
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

        return await ProcessLiveAsync(record, ct);
    }

    /// <summary>
    /// SQL Server fuera de servicio, perfil mínimo (ESTRATEGIA_RECUPERACION §3, T19): falla cerrada, decidida por la recepción del
    /// ciclo y no por el momento en que se procesa.
    /// <list type="bullet">
    /// <item>Cada intento dura como máximo <see cref="SqlFailClosedOptions.AttemptTimeout"/>. Hay hasta dos reintentos cortos, solo
    /// dentro del plazo de la caja (con la última revisión leída); un registro sin plazo vigente tiene los tres intentos, porque no se
    /// imprime. Si fallan, se declara la caída: el latido baja LabelingReady y el ciclo no se registra.</item>
    /// <item>Mientras está declarada (por la ingesta, el latido o el sender), cada ciclo se rechaza sin ir a SQL.</item>
    /// <item>Al volver, <see cref="RecoverWhileUnavailableAsync"/> recarga el estado, comprueba el camino de TX-A y marca la
    /// recuperación. Un ciclo recibido antes de esa marca se rechaza aunque se procese después (seguía en el canal): es parte del
    /// hueco, nunca una detección.</item>
    /// </list>
    /// </summary>
    private async Task<IngestOutcome> ProcessLiveAsync(CycleRecord record, CancellationToken ct)
    {
        var deadline = LiveDeadline(record);
        for (var attempt = 1; ; attempt++)
        {
            if (ReceivedDuringOutage(record))
            {
                return RejectUnavailable(record);
            }

            try
            {
                return await WithinAttemptAsync(t => ProcessRecordAsync(record, t), ct, record.Identity.IdempotencyKey);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsUnavailable(ex))
            {
                var backoff = TimeSpan.FromMilliseconds(100 * attempt);
                if (attempt < 3 && (deadline is null || _clock.UtcNow + backoff < deadline))
                {
                    _metrics.Increment(MetricNames.IngestSqlRetries, ("station", _station.StationId));
                    await Task.Delay(backoff, ct);
                    continue;
                }

                _logger.LogWarning(ex, "El ciclo {Key} agotó sus intentos con SQL Server sin responder", record.Identity.IdempotencyKey);
                DeclareSqlUnavailable("TX-A");
                return RejectUnavailable(record);
            }
        }
    }

    /// <summary>El plazo de la caja con la última revisión leída, o null si el registro no tiene uno vigente (o no hay revisión).</summary>
    private DateTime? LiveDeadline(CycleRecord record) =>
        _revision is { } revision
        && DeadlinePolicy.Compute(revision, record.DetectionTimestamp, record.ReceivedAt, record.Transport, record.QueueOverflow, opensGap: false)
            is { } deadline && deadline > _clock.UtcNow
            ? deadline
            : null;

    private bool ReceivedDuringOutage(CycleRecord record) =>
        _outage.Declared || (record.ReceivedTimestamp is { } received && received < _outage.RecoveredAt);

    private IngestOutcome RejectUnavailable(CycleRecord record)
    {
        _metrics.Increment(MetricNames.OpcRecordsRejected, ("cause", "SQL_UNAVAILABLE"), ("transport", record.Transport));
        _logger.LogWarning("El ciclo {Key}, recibido con SQL Server no disponible, no se registra (falla cerrada; será un hueco)",
            record.Identity.IdempotencyKey);
        return new IngestOutcome(IngestKind.Rejected, record, Rejection: "SQL_UNAVAILABLE");
    }

    private static bool IsUnavailable(Exception ex) => ex is TimeoutException || SqlAvailability.IsUnavailable(ex);

    /// <summary>
    /// Un intento acotado por <see cref="SqlFailClosedOptions.AttemptTimeout"/>. Al vencer se cancela y se deja de esperar
    /// (TimeoutException): SqlClient no siempre corta enseguida una conexión que dejó de responder, y parte de su API asíncrona
    /// bloquea (BeginTransactionAsync), por eso corre en el pool de hilos. El intento abandonado:
    /// <list type="bullet">
    /// <item>conserva el lock del consumidor hasta terminar, así que nada más toca el estado en memoria mientras tanto;</item>
    /// <item>queda en <see cref="SqlOutage.TrackAbandoned"/>: la caída no se levanta mientras siga pendiente;</item>
    /// <item>si termina registrando su ciclo (un commit que ya estaba en vuelo), queda en el log, porque su rechazo ya se contó.</item>
    /// </list>
    /// </summary>
    private async Task<T> WithinAttemptAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct, string? what = null)
    {
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(() => action(attempt.Token), CancellationToken.None);
        var abandoned = 0;
        _ = task.ContinueWith(t =>
        {
            _ = t.Exception;
            attempt.Dispose();
            if (Volatile.Read(ref abandoned) == 1 && what is not null && t.Status == TaskStatus.RanToCompletion
                && t.Result is IngestOutcome { Kind: IngestKind.Registered, Result: { } result })
            {
                _logger.LogWarning("El intento abandonado de {Key} terminó después y la registró ({Outcome}, {Status}): un commit en vuelo; su rechazo ya se había contado",
                    what, result.Outcome, result.FinalStatus);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await task.WaitAsync(_failClosed.AttemptTimeout, ct);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref abandoned, 1);
            _outage.TrackAbandoned(task);
            try
            {
                await attempt.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // El intento terminó justo al vencer.
            }

            throw;
        }
    }

    /// <summary>
    /// La vigilancia: mientras SQL Server está declarado no disponible, prueba cada <see cref="SqlFailClosedOptions.RecoveryProbeInterval"/>
    /// si volvió, sin esperar a que llegue un ciclo (con LabelingReady en falso, la línea puede no mandar ninguno). La caída se levanta
    /// solo si:
    /// <list type="bullet">
    /// <item>lleva declarada al menos el intervalo de prueba, duplicado por cada recaída reciente (<see cref="SqlOutage.Relapses"/>);</item>
    /// <item>no queda ninguna operación de SQL abandonada pendiente (una lectura del latido o un intento de TX-A colgados);</item>
    /// <item>recarga revisión, épocas y marca alta (arranque, etapa 5);</item>
    /// <item>y toma, sin esperar más que el tiempo de un intento, el rango donde TX-A insertará el ciclo siguiente: una lectura que
    /// funciona no alcanza si las escrituras siguen bloqueadas.</item>
    /// </list>
    /// Cualquier error en la prueba (no solo los clasificados como SQL no disponible: al volver un servidor aparecen también errores
    /// de login o de base en recuperación) significa que SQL todavía no está, y la falla cerrada sigue. Recién con la caída levantada
    /// el latido puede volver a levantar LabelingReady (etapa 8). El barrido de la estación sigue con su propio intervalo (etapa 6).
    /// </summary>
    private async Task RecoverWhileUnavailableAsync(CancellationToken ct)
    {
        string? lastError = null;
        while (true)
        {
            await Task.Delay(_failClosed.RecoveryProbeInterval, ct);
            if (!_outage.Declared || _outage.DeclaredFor < _outage.RecoveryHold(_failClosed.RecoveryProbeInterval) || _outage.AbandonedOperations > 0)
            {
                continue;
            }

            var probeStartedAt = _clock.GetTimestamp();
            try
            {
                await WithinAttemptAsync(async t =>
                {
                    await _serial.WaitAsync(t);
                    try
                    {
                        await ReloadCoreAsync(t);
                        if (_classifier.Current is { } current)
                        {
                            await _sequence.ProbeRegistrationRangeAsync(_station.SiteId, _station.StationId, current.Epoch, current.HighWater ?? 0,
                                (int)_failClosed.AttemptTimeout.TotalMilliseconds, t);
                        }
                    }
                    finally
                    {
                        _serial.Release();
                    }

                    return true;
                }, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Un aviso por cada error distinto, no uno por segundo.
                var error = ex is SqlException sql ? $"SQL {sql.Number}" : ex.GetType().Name;
                if (error != lastError)
                {
                    _logger.LogWarning("Vigilancia de {Station}: SQL Server todavía no está disponible ({Error}: {Message})", _station.StationId, error, ex.Message);
                    lastError = error;
                }

                continue;
            }

            lastError = null;
            if (!_outage.TryRecover(probeStartedAt))
            {
                // Una lectura del latido o un intento se abandonó, o alguien informó una falla, mientras se probaba.
                continue;
            }

            _logger.LogWarning("SQL Server volvió: estado de la estación {Station} recargado y camino de TX-A libre; los ciclos recibidos durante la caída quedan como hueco",
                _station.StationId);
        }
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
        _revision = revision;
        await CheckClockSkewAsync(record, state, revision, ct);

        // Un registro incoherente (campos de ciclos distintos) pasa por la misma clasificación con dos límites: no
        // acepta épocas (SI-13) y su hora no decide nada. Queda BLOCKED con INCOHERENT_SNAPSHOT y su CycleId mueve
        // la marca alta y abre huecos, como su fila al recargar el estado desde SQL.
        var reloaded = false;
        for (var guard = 0; ; guard++)
        {
            var now = _clock.UtcNow;
            var verdict = _classifier.Classify(record, revision, now);
            switch (verdict)
            {
                case SequenceVerdict.AcceptEpoch accept when guard < 2:
                    await AcceptEpochAsync(record, accept, revision, now, ct);
                    continue;

                case SequenceVerdict.AcceptEpoch:
                    throw new UnreachableException("La época aceptada no quedó como actual.");

                case SequenceVerdict.RetainEpochUnexpected when !reloaded:
                    // Antes de retener, una vez: la época pudo confirmarla una persona (CONFIRM_EPOCH) u otra
                    // instancia después de la última carga.
                    await ReloadCoreAsync(ct);
                    reloaded = true;
                    continue;

                case SequenceVerdict.RetainEpochUnexpected retain:
                    var anomalyId = await RetainAsync(record, revision, now, ct);
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
                    _classifier.Observe(record, register.Case, result);
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

    private async Task<long> RetainAsync(CycleRecord record, StationRevision revision, DateTime now, CancellationToken ct)
    {
        var anomalyId = await _sequence.RetainAsync(new AnomalyRow(
            record.SiteId, record.StationId, record.PlcEpoch, record.CycleId, AnomalyType.EpochUnexpected, AnomalySeverity.Blocking,
            null, null, record.SourceFingerprint(), RetainedRecords.ToJson(record), now, revision.ConfigRevisionId),
            BlockReason.EpochUnexpected, ct);
        _metrics.Increment(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.EpochUnexpected));
        return anomalyId;
    }

    /// <summary>Primera época de la estación (INITIAL) o actual + 1 con AUTO_INCREMENT_BY_ONE: nueva fila en PlcEpochHistory.</summary>
    private async Task AcceptEpochAsync(CycleRecord record, SequenceVerdict.AcceptEpoch accept, StationRevision revision, DateTime now, CancellationToken ct)
    {
        // El orden entre épocas lo da AcceptedAt: nunca anterior a la época actual, aunque el reloj de pared retroceda.
        var acceptedAt = CanonicalTimestamp.Truncate(now);
        if (_classifier.Current is { } current && acceptedAt <= current.AcceptedAt)
        {
            acceptedAt = current.AcceptedAt.AddMilliseconds(1);
        }

        try
        {
            await _epochs.InsertAsync(new PlcEpoch(record.SiteId, record.StationId, accept.Epoch, "PLC", accept.AcceptanceMode, record.CycleId,
                acceptedAt, "SYSTEM", revision.ConfigRevisionId, null), ct);
            if (accept.AcceptanceMode != PlcEpochStore.Initial)
            {
                _metrics.Increment(MetricNames.PlcEpochChanges, ("mode", accept.AcceptanceMode));
            }

            _classifier.AcceptEpoch(accept.Epoch, acceptedAt, accept.AcceptanceMode);
            _logger.LogInformation("Época {Epoch} aceptada ({Mode}) en {Station} desde el ciclo {Cycle}",
                accept.Epoch, accept.AcceptanceMode, record.StationId, record.CycleId);
        }
        catch (SqlException ex) when (SequenceStore.IsEpochAlreadyAccepted(ex))
        {
            // Otro proceso o un intento anterior ya la aceptó (o tomó ese AcceptedAt): el orden de épocas sale de SQL.
            await ReloadCoreAsync(ct);
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
        var retained = (await _sequence.ListRetainedAsync(_station.SiteId, _station.StationId, ct)).FirstOrDefault(a => a.AnomalyId == anomalyId)
            ?? throw new InvalidOperationException($"La anomalía {anomalyId} no es un registro retenido sin resolver de la estación {_station.StationId}.");
        var record = RetainedRecords.Reingest(retained.IncomingRecord);
        IngestOutcome outcome;
        await _serial.WaitAsync(ct);
        try
        {
            await ReloadCoreAsync(ct);
            if (!_classifier.Epochs.Any(e => e.Epoch == record.PlcEpoch))
            {
                throw new InvalidOperationException($"La época {record.PlcEpoch} no está aceptada: el registro retenido no se puede reingresar.");
            }

            outcome = await ClassifyAndRegisterAsync(record, ct);
        }
        finally
        {
            _serial.Release();
        }

        var resolved = await _manual.ResolveAnomalyAsync(anomalyId, _station.SiteId, _station.StationId, performedBy, resolution, _clock.UtcNow, ct);
        if (!resolved.Applied)
        {
            // Otro reingreso concurrente la resolvió: el registro de este fue su duplicado auténtico.
            throw new InvalidOperationException($"La anomalía {anomalyId} ya la resolvió otro reingreso (este registro: {outcome.Result?.Outcome}).");
        }

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
