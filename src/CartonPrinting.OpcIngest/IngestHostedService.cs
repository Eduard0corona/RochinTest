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
/// </summary>
public sealed class IngestHostedService(
    IngestHostOptions options, SqlDb db, EdgeClock clock, CartonMetrics metrics, ILoggerFactory loggers, IErpDemand? erpDemand = null) : BackgroundService
{
    /// <summary>La ingesta en ejecución (para recargar épocas o reingresar registros retenidos desde la operación).</summary>
    public StationIngestService? Service { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logger = loggers.CreateLogger<IngestHostedService>();
        var station = new StationSettings(options.SiteId, options.StationId, options.LabelSerialPrefix, options.Opc.RecordNodeId);
        var stations = new StationConfigStore(db);
        var registration = new RegistrationService(new TxaRegisterDetection(db), new ErpCacheStore(db), new LabelSerialSource(db), clock, metrics);
        var ingest = new StationIngestService(station, registration, stations, new PlcEpochStore(db), new SequenceStore(db), new TxsSweep(db),
            new TxmManualActions(db), clock, metrics, loggers.CreateLogger<StationIngestService>(), erpDemand);
        var channel = Channel.CreateBounded<RawNotification>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        Service = ingest;
        await using var source = new OpcUaCycleSource(options.Opc, clock, metrics, loggers.CreateLogger<OpcUaCycleSource>());
        var pressure = new ChannelPressure(channel.Reader, options.ChannelCapacity, () => source.DroppedNotifications);
        var heartbeat = new LabelingReadyHeartbeat(station, stations, source, metrics, loggers.CreateLogger<LabelingReadyHeartbeat>(),
            pressure.Overloaded);

        // Con el PLC inalcanzable al arrancar, se reintenta: el resto del EdgeHost (Outbox, barrido) sigue.
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await source.StartAsync(channel.Writer, stoppingToken);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Ingesta de {Station}: no se pudo conectar a {Endpoint}; reintento en {Backoff}", options.StationId,
                    options.Opc.EndpointUrl, backoff);
                await Task.Delay(backoff, clock.TimeProvider, stoppingToken);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }

        logger.LogInformation("Ingesta de {Site}/{Station} conectada a {Endpoint}", options.SiteId, options.StationId, options.Opc.EndpointUrl);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var tasks = new[]
        {
            ingest.RunAsync(channel.Reader, linked.Token),
            heartbeat.RunAsync(TimeSpan.FromMilliseconds(options.LabelingReadyIntervalMs), clock.TimeProvider, linked.Token),
        };
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
            logger.LogCritical(ex, "La ingesta de {Station} se detuvo: ningún registro se salta; revisar antes de reiniciar", options.StationId);
            throw;
        }

    }
}
