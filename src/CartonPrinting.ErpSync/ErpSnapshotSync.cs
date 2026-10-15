using System.Threading.Channels;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CartonPrinting.ErpSync;

/// <summary>Configuración de la sincronización con el ERP (sección "ErpSync").</summary>
public sealed class ErpSyncOptions
{
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Tiempo máximo de una consulta al ERP; agotarlo cuenta como falla para el circuit breaker.</summary>
    public int TimeoutMs { get; set; } = 800;

    public int PollIntervalMs { get; set; } = 100;

    public double FailureRatio { get; set; } = 0.5;

    public int MinimumThroughput { get; set; } = 4;

    public int SamplingDurationMs { get; set; } = 5_000;

    public int BreakDurationMs { get; set; } = 2_000;
}

/// <summary>
/// Sincronización asíncrona de snapshots ERP de una estación. Busca las órdenes de las detecciones en
/// WAITING_ERP, consulta el ERP fuera del camino de ingesta (timeout y circuit breaker con Polly), guarda
/// el snapshot validado en la caché y ejecuta la llegada (TX-E) de cada detección que lo esperaba. Si el
/// ERP no responde, las detecciones siguen esperando y el barrido las vence con ERP_UNAVAILABLE (D07).
/// La ingesta la señala con <see cref="WorkOrderNeeded"/> (System.Threading.Channels) para no esperar al
/// siguiente ciclo de sondeo.
/// </summary>
public sealed class ErpSnapshotSync : IErpDemand
{
    private readonly StationSettings _station;
    private readonly IErpClient _client;
    private readonly SqlDb _db;
    private readonly ErpCacheStore _cache;
    private readonly ErpArrivalService _arrivals;
    private readonly EdgeClock _clock;
    private readonly CartonMetrics _metrics;
    private readonly ErpSyncOptions _options;
    private readonly ILogger _logger;
    private readonly ResiliencePipeline _pipeline;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly DateTime _startedAt;

    public ErpSnapshotSync(
        StationSettings station, IErpClient client, SqlDb db, ErpCacheStore cache, ErpArrivalService arrivals, EdgeClock clock, CartonMetrics metrics,
        ErpSyncOptions options, ILogger<ErpSnapshotSync>? logger = null)
    {
        _station = station;
        _client = client;
        _db = db;
        _cache = cache;
        _arrivals = arrivals;
        _clock = clock;
        _metrics = metrics;
        _options = options;
        _logger = logger ?? NullLogger<ErpSnapshotSync>.Instance;
        _startedAt = clock.UtcNow;
        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.FailureRatio,
                MinimumThroughput = options.MinimumThroughput,
                SamplingDuration = TimeSpan.FromMilliseconds(options.SamplingDurationMs),
                BreakDuration = TimeSpan.FromMilliseconds(options.BreakDurationMs),
                ShouldHandle = new PredicateBuilder().Handle<ErpUnavailableException>().Handle<TimeoutRejectedException>(),
            })
            .AddTimeout(TimeSpan.FromMilliseconds(options.TimeoutMs))
            .Build();
    }

    /// <summary>Último instante en que el ERP respondió (null si nunca).</summary>
    public DateTime? LastSuccessAt { get; private set; }

    public void WorkOrderNeeded(string workOrderId) => _signal.Writer.TryWrite(true);

    /// <summary>Una pasada: devuelve cuántas detecciones pasaron por TX-E.</summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var waiting = await _db.ReadAsync(conn => DetectionQueries.ListAsync(conn, null,
            "SiteId = @site AND StationId = @station AND Status = 'WAITING_ERP'",
            c => c.NVarChar("@site", _station.SiteId, 64).NVarChar("@station", _station.StationId, 64), ct), ct);
        var processed = 0;
        foreach (var order in waiting.Where(d => d.WorkOrderId is not null).GroupBy(d => d.WorkOrderId!, StringComparer.Ordinal))
        {
            var snapshot = await _cache.FindValidAsync(order.Key, _clock.UtcNow, ct) ?? await FetchAsync(order.Key, ct);
            if (snapshot is null)
            {
                continue;
            }

            foreach (var detection in order)
            {
                if (await _arrivals.ProcessAsync(detection, snapshot, ct) is not null)
                {
                    processed++;
                }
            }
        }

        _metrics.SetGauge(MetricNames.ErpCacheAgeSeconds, (_clock.UtcNow - (LastSuccessAt ?? _startedAt)).TotalSeconds, ("station", _station.StationId));
        return processed;
    }

    private async Task<ErpSnapshot?> FetchAsync(string workOrderId, CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        try
        {
            var snapshot = await _pipeline.ExecuteAsync(async token => await _client.GetSnapshotAsync(workOrderId, token), ct);
            _metrics.Record(MetricNames.ErpRequestMs, _clock.GetElapsedTime(started).TotalMilliseconds);
            _metrics.Increment(MetricNames.ErpRequests, ("outcome", snapshot is null ? "not_found" : "ok"));
            LastSuccessAt = _clock.UtcNow;
            if (snapshot is null)
            {
                return null;
            }

            await _cache.UpsertAsync(snapshot, ct);
            return snapshot.IsValidAt(_clock.UtcNow) ? snapshot : null;
        }
        catch (Exception ex) when (ex is ErpUnavailableException or TimeoutRejectedException or BrokenCircuitException)
        {
            _metrics.Increment(MetricNames.ErpRequests, ("outcome", ex is BrokenCircuitException ? "circuit_open" : "unavailable"));
            _logger.LogDebug("ERP sin respuesta para {WorkOrder}: {Error}", workOrderId, ex.Message);
            return null;
        }
    }

    /// <summary>Sondeo con señal: una pasada por señal de la ingesta o cada PollIntervalMs.</summary>
    public async Task RunAsync(TimeProvider loopTime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Sincronización ERP de {Station} fallida; se reintenta", _station.StationId);
            }

            using var interval = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.PollIntervalMs), loopTime);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, interval.Token);
            try
            {
                await _signal.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Venció el intervalo de sondeo.
            }
        }
    }
}
