using System.Net;
using System.Net.Sockets;
using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Outbox;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Simulators.Plc;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using RabbitMQ.Client;

namespace CartonPrinting.Testing;

/// <summary>
/// La estación completa con simuladores, compuesta como en el EdgeHost:
/// <list type="bullet">
/// <item>el PLC simulado publica cada ciclo por OPC UA con SignAndEncrypt, y la ingesta valida su certificado como en planta
/// (está en su almacén de confianza; nada se autoacepta);</item>
/// <item>la ingesta real lo registra con TX-A y avisa al sender con la señal en proceso;</item>
/// <item>el sender real (sondeo de respaldo de 50 ms, como el EdgeHost) envía a la impresora simulada;</item>
/// <item>el barrido de la estación (TX-S) corre cada 200 ms, como el EdgeHost;</item>
/// <item>con un broker, el Outbox Relay publica en RabbitMQ, en una cola enlazada sin consumidor;</item>
/// <item>el simulador de evidencia informa lo que se le pida (PRINTED, APPLIED, VERIFIED) por TX-D.</item>
/// </list>
/// Fuera de la composición: la sincronización con el ERP (el snapshot está en caché durante toda la corrida), la Operations API y
/// el ciclo de propiedad de la impresora, que aquí es una renovación del lease por minuto. Todo comparte las métricas del escenario:
/// los tramos de la sección 10.4 cruzan componentes.
/// </summary>
public sealed class StationPipeline : IAsyncDisposable
{
    public const int SweepIntervalMs = 200;

    private readonly CancellationTokenSource _stop = new();

    private StationPipeline(StationScenario s, PlcSimulatorServer plc, IngestHostedService ingest, PrintRig rig, ReadyJobSignal signal)
    {
        Scenario = s;
        Plc = plc;
        Ingest = ingest;
        Rig = rig;
        Signal = signal;
    }

    public StationScenario Scenario { get; }
    public PlcSimulatorServer Plc { get; }
    public IngestHostedService Ingest { get; }
    public PrintRig Rig { get; }
    public ReadyJobSignal Signal { get; }
    public Task Sender { get; private set; } = Task.CompletedTask;
    public Task Sweep { get; private set; } = Task.CompletedTask;
    public OutboxRelay? Relay { get; private set; }

    private Task Lease { get; set; } = Task.CompletedTask;
    private RabbitMqPublisher? Publisher { get; set; }

    /// <summary>Arranca la ingesta, el sender, el barrido y, con <paramref name="rabbitUri"/>, el Outbox Relay; vuelve cuando la estación levantó LabelingReady.</summary>
    public static async Task<StationPipeline> StartAsync(StationScenario s, EvidenceSources sources, CancellationToken ct, int senderPollMs = 50,
        bool signal = true, string? rabbitUri = null)
    {
        var pki = Path.Combine(Path.GetTempPath(), "cartonprinting-pki", Guid.NewGuid().ToString("N")[..8]);
        var plc = await PlcSimulatorServer.StartAsync(new PlcSimulatorOptions
        {
            StationId = s.StationId, Port = FreePort(), PkiRoot = Path.Combine(pki, "server"), SecurityMode = MessageSecurityMode.SignAndEncrypt,
        });

        // Lo que hace OT al instalar: el certificado del PLC en el almacén de confianza del EdgeHost.
        var trusted = Directory.CreateDirectory(Path.Combine(pki, "host", "trusted", "certs")).FullName;
        foreach (var certificate in Directory.GetFiles(Path.Combine(pki, "server", "own", "certs"), "*.der"))
        {
            File.Copy(certificate, Path.Combine(trusted, Path.GetFileName(certificate)), overwrite: true);
        }

        var options = new IngestHostOptions
        {
            SiteId = StationScenario.SiteId,
            StationId = s.StationId,
            LabelSerialPrefix = s.SerialPrefix,
            Opc = new OpcIngestOptions
            {
                EndpointUrl = plc.EndpointUrl,
                RecordNodeId = plc.Nodes.RecordNodeId,
                LabelingReadyNodeId = plc.Nodes.LabelingReadyNodeId,
                PkiRoot = Path.Combine(pki, "host"),
            },
        };
        var readySignal = new ReadyJobSignal();
        var ingest = new IngestHostedService(options, s.Sql, s.Clock, s.Metrics, NullLoggerFactory.Instance, readySignal: signal ? readySignal : null);
        // Como en el EdgeHost, el sender de línea comparte la caída de SQL Server de la ingesta.
        var rig = PrintRig.Create(s, sources: sources, sqlOutage: ingest.Service.Outage);
        var pipeline = new StationPipeline(s, plc, ingest, rig, readySignal);
        if (rabbitUri is not null)
        {
            var exchange = $"carton.events.{s.StationId}";
            await DeclareConsumerQueueAsync(rabbitUri, exchange, $"mes.{s.StationId}", ct);
            pipeline.Publisher = new RabbitMqPublisher(new RabbitMqOptions { Uri = rabbitUri, Exchange = exchange });
            pipeline.Relay = new OutboxRelay(s.Sql, pipeline.Publisher, s.Clock, s.Metrics, new OutboxRelayOptions());
            await pipeline.Relay.StartAsync(ct);
        }

        await ingest.StartAsync(ct);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!plc.Nodes.LabelingReady)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("La estación no levantó LabelingReady en 60 s.");
            }

            await Task.Delay(100, ct);
        }

        pipeline.Sender = rig.Sender.RunAsync(TimeSpan.FromMilliseconds(senderPollMs), TimeProvider.System, signal ? readySignal.WaitAsync : null,
            pipeline._stop.Token);
        pipeline.Sweep = new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics,
            readySignal: signal ? readySignal : null).RunAsync(TimeSpan.FromMilliseconds(SweepIntervalMs), TimeProvider.System, pipeline._stop.Token);
        pipeline.Lease = s.KeepLeaseAsync(pipeline._stop.Token);
        return pipeline;
    }

    /// <summary>El PLC latchea el ciclo, con el flanco en el reloj de esta máquina (el mismo que el del EdgeHost).</summary>
    public void Publish(ulong cycle) => Plc.Nodes.Publish(new LatchedRecord
    {
        PlcEpoch = Scenario.Epoch, CycleId = cycle, WorkOrderId = StationScenario.WorkOrder, DetectionTimestamp = DateTime.UtcNow,
    });

    /// <summary>Los servicios de fondo que terminaron antes de tiempo (una falla), para el reporte y las aserciones.</summary>
    public IReadOnlyList<string> Faults() =>
        [.. new[] { ("sender", Sender), ("barrido", Sweep) }.Where(t => t.Item2.IsCompleted && !_stop.IsCancellationRequested)
            .Select(t => $"{t.Item1}: {t.Item2.Status}{(t.Item2.Exception is { } e ? " " + e.GetBaseException().Message : string.Empty)}")];

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Sender.ContinueWith(_ => { }, TaskScheduler.Default);
        await Sweep.ContinueWith(_ => { }, TaskScheduler.Default);
        await Lease;
        if (Relay is not null)
        {
            await Relay.StopAsync(CancellationToken.None);
            Relay.Dispose();
        }

        if (Publisher is not null)
        {
            await Publisher.DisposeAsync();
        }

        await Ingest.StopAsync(CancellationToken.None);
        Ingest.Dispose();
        await Rig.DisposeAsync();
        await Plc.DisposeAsync();
        _stop.Dispose();
    }

    /// <summary>La cola del consumidor (MES) enlazada a los dos tipos de evento; sin ella, el broker devolvería los mensajes obligatorios.</summary>
    private static async Task DeclareConsumerQueueAsync(string rabbitUri, string exchange, string queue, CancellationToken ct)
    {
        await using var connection = await new RabbitMqOptions { Uri = rabbitUri }.CreateConnectionFactory("t12-setup").CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
        foreach (var key in new[] { RabbitMqPublisher.CartonDetectedKey, RabbitMqPublisher.PrintResultKey })
        {
            await channel.QueueBindAsync(queue, exchange, key, cancellationToken: ct);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
