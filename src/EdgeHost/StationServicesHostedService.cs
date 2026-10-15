using CartonPrinting.ErpSync;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Orchestration;
using CartonPrinting.Recovery;

namespace CartonPrinting.EdgeHost;

/// <summary>
/// Servicios por estación que no son la ingesta: sincronización ERP (sondeo con señal de la ingesta) y
/// barrido TX-S. Corren en paralelo y fuera del camino de ingesta; un error en una pasada se registra y
/// se reintenta en la siguiente. En el arranque ordenado, el primer barrido es la etapa 6 y los bucles empiezan
/// después de ella.
/// </summary>
public sealed class StationServicesHostedService : BackgroundService
{
    private readonly StationSweepService _sweep;
    private readonly ErpSnapshotSync? _erp;
    private readonly TimeSpan _sweepInterval;
    private readonly TimeProvider _time;
    private readonly StartupCoordinator? _startup;

    public StationServicesHostedService(
        StationSweepService sweep, ErpSnapshotSync? erp, TimeSpan sweepInterval, TimeProvider time, StartupCoordinator? startup = null, string? stationId = null)
    {
        _sweep = sweep;
        _erp = erp;
        _sweepInterval = sweepInterval;
        _time = time;
        _startup = startup;
        startup?.Add(StartupStage.Swept, $"barrido de {stationId}", sweep.SweepOnceAsync);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startup is not null)
        {
            await _startup.WhenReached(StartupStage.Swept).WaitAsync(stoppingToken);
        }

        var sweep = _sweep;
        var erp = _erp;
        var sweepInterval = _sweepInterval;
        var time = _time;
        var tasks = new List<Task> { sweep.RunAsync(sweepInterval, time, stoppingToken) };
        if (erp is not null)
        {
            tasks.Add(erp.RunAsync(time, stoppingToken));
        }

        await Task.WhenAll(tasks);
    }
}

/// <summary>Composición de los servicios de una estación a partir de la configuración.</summary>
public static class StationComposition
{
    public static void AddStationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var ingest = configuration.GetSection("Ingest").Get<CartonPrinting.OpcIngest.IngestHostOptions>();
        if (string.IsNullOrWhiteSpace(ingest?.StationId))
        {
            return;
        }

        var station = new StationSettings(ingest.SiteId, ingest.StationId, ingest.LabelSerialPrefix, ingest.Opc.RecordNodeId);
        services.AddSingleton(station);
        services.AddSingleton(sp => new ClockGuard(sp.GetRequiredService<EdgeClock>()));
        services.AddSingleton(sp => new StationSweepService(station, new CartonPrinting.Infrastructure.Transactions.TxsSweep(sp.GetRequiredService<CartonPrinting.Infrastructure.Data.SqlDb>()),
            new CartonPrinting.Infrastructure.Data.StationConfigStore(sp.GetRequiredService<CartonPrinting.Infrastructure.Data.SqlDb>()), sp.GetRequiredService<ClockGuard>(),
            sp.GetRequiredService<EdgeClock>(), sp.GetRequiredService<CartonPrinting.Infrastructure.Observability.CartonMetrics>(),
            sp.GetRequiredService<ILogger<StationSweepService>>(), sp.GetService<CartonPrinting.Infrastructure.Signals.ReadyJobSignal>()));

        var erpOptions = configuration.GetSection("ErpSync").Get<ErpSyncOptions>();
        if (!string.IsNullOrWhiteSpace(erpOptions?.BaseUrl))
        {
            services.AddSingleton(erpOptions);
            services.AddHttpClient<HttpErpClient>(http => http.BaseAddress = new Uri(erpOptions.BaseUrl));
            services.AddSingleton(sp =>
            {
                var db = sp.GetRequiredService<CartonPrinting.Infrastructure.Data.SqlDb>();
                var clock = sp.GetRequiredService<EdgeClock>();
                var metrics = sp.GetRequiredService<CartonPrinting.Infrastructure.Observability.CartonMetrics>();
                var stations = new CartonPrinting.Infrastructure.Data.StationConfigStore(db);
                var arrivals = new ErpArrivalService(new CartonPrinting.Infrastructure.Transactions.TxeErpArrival(db), stations,
                    new CartonPrinting.Infrastructure.Data.LabelSerialSource(db), station, clock, metrics);
                return new ErpSnapshotSync(station, sp.GetRequiredService<HttpErpClient>(), db, new CartonPrinting.Infrastructure.Data.ErpCacheStore(db), arrivals,
                    clock, metrics, erpOptions, sp.GetRequiredService<ILogger<ErpSnapshotSync>>());
            });
            services.AddSingleton<IErpDemand>(sp => sp.GetRequiredService<ErpSnapshotSync>());
        }

        services.AddHostedService(sp => new StationServicesHostedService(sp.GetRequiredService<StationSweepService>(), sp.GetService<ErpSnapshotSync>(),
            TimeSpan.FromMilliseconds(ingest.SweepIntervalMs), sp.GetRequiredService<EdgeClock>().TimeProvider, sp.GetService<StartupCoordinator>(),
            ingest.StationId));
    }
}
