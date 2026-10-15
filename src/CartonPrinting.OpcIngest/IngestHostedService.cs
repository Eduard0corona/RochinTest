using System.Threading.Channels;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CartonPrinting.OpcIngest;

/// <summary>Configuración de la ingesta de una estación en el EdgeHost (sección "Ingest").</summary>
public sealed class IngestHostOptions
{
    public string SiteId { get; set; } = string.Empty;

    public string StationId { get; set; } = string.Empty;

    public string LabelSerialPrefix { get; set; } = string.Empty;

    public OpcIngestOptions Opc { get; set; } = new();

    /// <summary>Canal acotado entre el callback OPC y el consumidor: si se llena, se mide y aparece como hueco.</summary>
    public int ChannelCapacity { get; set; } = 10_000;

    public int LabelingReadyIntervalMs { get; set; } = 500;

    /// <summary>Intervalo del barrido TX-S de la estación (cierre de vencidos, huecos y ventanas de evidencia).</summary>
    public int SweepIntervalMs { get; set; } = 200;
}

/// <summary>
/// Ingesta de una estación como servicio alojado: cliente OPC UA, consumidor del canal (clasificación y
/// TX-A) y latido LabelingReady. Los huecos vencidos (G03) los cierra el barrido de la estación. Si el
/// consumidor falla, el servicio se detiene en lugar de saltarse registros.
/// <para>
/// Con <c>coordinated</c>, el arranque ordenado del EdgeHost ejecuta sus pasos: <see cref="LoadStateAsync"/> (etapa 5:
/// revisión, épocas y marca alta), <see cref="SubscribeAsync"/> (etapa 7) y <see cref="FirstBeatAsync"/> (etapa 8:
/// el primer LabelingReady). El consumidor arranca después de cargar el estado y el latido periódico después del
/// primero. Sin coordinar, se conecta solo, con reintentos, y arranca todo a la vez.
/// </para>
/// </summary>
public sealed class IngestHostedService : BackgroundService
{
    private readonly IngestHostOptions _options;
    private readonly EdgeClock _clock;
    private readonly bool _coordinated;
    private readonly ILogger _logger;
    private readonly Channel<RawNotification> _channel;
    private readonly OpcUaCycleSource _source;
    private readonly LabelingReadyHeartbeat _heartbeat;
    private readonly TaskCompletionSource _stateLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IngestHostedService(
        IngestHostOptions options, SqlDb db, EdgeClock clock, CartonMetrics metrics, ILoggerFactory loggers, IErpDemand? erpDemand = null,
        bool coordinated = false)
    {
        _options = options;
        _clock = clock;
        _coordinated = coordinated;
        _logger = loggers.CreateLogger<IngestHostedService>();
        var station = new StationSettings(options.SiteId, options.StationId, options.LabelSerialPrefix, options.Opc.RecordNodeId);
        var stations = new StationConfigStore(db);
        var registration = new RegistrationService(new TxaRegisterDetection(db), new ErpCacheStore(db), new LabelSerialSource(db), clock, metrics);
        Service = new StationIngestService(station, registration, stations, new PlcEpochStore(db), new SequenceStore(db), new TxsSweep(db),
            new TxmManualActions(db), clock, metrics, loggers.CreateLogger<StationIngestService>(), erpDemand);
        _channel = Channel.CreateBounded<RawNotification>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _source = new OpcUaCycleSource(options.Opc, clock, metrics, loggers.CreateLogger<OpcUaCycleSource>());
        var pressure = new ChannelPressure(_channel.Reader, options.ChannelCapacity, () => _source.DroppedNotifications);
        _heartbeat = new LabelingReadyHeartbeat(station, stations, _source, metrics, loggers.CreateLogger<LabelingReadyHeartbeat>(), pressure.Overloaded);
    }

    /// <summary>La ingesta (para recargar épocas o reingresar registros retenidos desde la operación).</summary>
    public StationIngestService Service { get; }

    /// <summary>Etapa 5: revisión activa, épocas y marca alta por época desde SQL.</summary>
    public async Task LoadStateAsync(CancellationToken ct)
    {
        await Service.ReloadAsync(ct);
        _stateLoaded.TrySetResult();
    }

    /// <summary>Etapa 7: sesión y suscripción OPC UA (un intento; el arranque reintenta).</summary>
    public async Task SubscribeAsync(CancellationToken ct)
    {
        await _source.StartAsync(_channel.Writer, ct);
        _logger.LogInformation("Ingesta de {Site}/{Station} conectada a {Endpoint}", _options.SiteId, _options.StationId, _options.Opc.EndpointUrl);
    }

    /// <summary>Etapa 8: el primer latido (LabelingReady según el estado de la estación); después, el periódico.</summary>
    public async Task FirstBeatAsync(CancellationToken ct)
    {
        await _heartbeat.BeatAsync(ct);
        _ready.TrySetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_coordinated)
        {
            await StartAloneAsync(stoppingToken);
        }

        await _stateLoaded.Task.WaitAsync(stoppingToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var consumer = Service.RunAsync(_channel.Reader, linked.Token);
        var heartbeat = HeartbeatAfterReadyAsync(linked.Token);
        var tasks = new[] { consumer, heartbeat };
        await Task.WhenAny(tasks);
        await linked.CancelAsync();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || linked.IsCancellationRequested)
        {
            // Detención ordenada.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "La ingesta de {Station} se detuvo: ningún registro se salta; revisar antes de reiniciar", _options.StationId);
            throw;
        }
    }

    private async Task HeartbeatAfterReadyAsync(CancellationToken ct)
    {
        await _ready.Task.WaitAsync(ct);
        await _heartbeat.RunAsync(TimeSpan.FromMilliseconds(_options.LabelingReadyIntervalMs), _clock.TimeProvider, ct);
    }

    /// <summary>Sin arranque coordinado: conecta con reintentos (el PLC puede no estar al arrancar) y arranca todo.</summary>
    private async Task StartAloneAsync(CancellationToken ct)
    {
        await LoadStateAsync(ct);
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await SubscribeAsync(ct);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Ingesta de {Station}: no se pudo conectar a {Endpoint}; reintento en {Backoff}", _options.StationId,
                    _options.Opc.EndpointUrl, backoff);
                await Task.Delay(backoff, _clock.TimeProvider, ct);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }

        _ready.TrySetResult();
    }

    public override void Dispose()
    {
        _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose();
    }
}
