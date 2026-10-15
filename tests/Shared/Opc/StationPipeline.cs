using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Simulators.Plc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.Testing;

/// <summary>
/// La estación completa con simuladores, compuesta como en el EdgeHost:
/// <list type="bullet">
/// <item>el PLC simulado publica cada ciclo por OPC UA;</item>
/// <item>la ingesta real lo registra con TX-A y avisa al sender con la señal en proceso;</item>
/// <item>el sender real (sondeo de respaldo de 50 ms, como el EdgeHost) envía a la impresora simulada;</item>
/// <item>el simulador de evidencia informa lo que se le pida (PRINTED, APPLIED, VERIFIED).</item>
/// </list>
/// Todo comparte las métricas del escenario: los tramos de la sección 10.4 cruzan componentes.
/// </summary>
public sealed class StationPipeline : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();

    private StationPipeline(StationScenario s, OpcHarness plc, IngestHostedService ingest, PrintRig rig, ReadyJobSignal signal)
    {
        Scenario = s;
        Plc = plc;
        Ingest = ingest;
        Rig = rig;
        Signal = signal;
    }

    public StationScenario Scenario { get; }
    public OpcHarness Plc { get; }
    public IngestHostedService Ingest { get; }
    public PrintRig Rig { get; }
    public ReadyJobSignal Signal { get; }
    public Task Sender { get; private set; } = Task.CompletedTask;

    private Task Lease { get; set; } = Task.CompletedTask;

    /// <summary>Arranca la ingesta y el sender; vuelve cuando la estación levantó LabelingReady.</summary>
    public static async Task<StationPipeline> StartAsync(StationScenario s, EvidenceSources sources, CancellationToken ct, int senderPollMs = 50,
        bool signal = true)
    {
        var plc = await OpcHarness.StartAsync(s.StationId, new EdgeClock(TimeProvider.System));
        var options = new IngestHostOptions
        {
            SiteId = StationScenario.SiteId,
            StationId = s.StationId,
            LabelSerialPrefix = s.SerialPrefix,
            Opc = new OpcIngestOptions
            {
                EndpointUrl = $"opc.tcp://localhost:{plc.ServerPort}/CartonPlc",
                RecordNodeId = plc.Server.Nodes.RecordNodeId,
                LabelingReadyNodeId = plc.Server.Nodes.LabelingReadyNodeId,
                SecurityMode = "None",
                PkiRoot = Path.Combine(Path.GetTempPath(), "cartonprinting-pki", Guid.NewGuid().ToString("N")[..8], "host"),
                AutoAcceptUntrustedCertificates = true,
            },
        };
        var readySignal = new ReadyJobSignal();
        var ingest = new IngestHostedService(options, s.Sql, s.Clock, s.Metrics, NullLoggerFactory.Instance, readySignal: signal ? readySignal : null);
        var rig = PrintRig.Create(s, sources: sources);
        var pipeline = new StationPipeline(s, plc, ingest, rig, readySignal);
        await ingest.StartAsync(ct);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!plc.Server.Nodes.LabelingReady)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("La estación no levantó LabelingReady en 60 s.");
            }

            await Task.Delay(100, ct);
        }

        pipeline.Sender = rig.Sender.RunAsync(TimeSpan.FromMilliseconds(senderPollMs), TimeProvider.System, signal ? readySignal.WaitAsync : null,
            pipeline._stop.Token);
        pipeline.Lease = s.KeepLeaseAsync(pipeline._stop.Token);
        return pipeline;
    }

    /// <summary>El PLC latchea el ciclo, con el flanco en el reloj de esta máquina (el mismo que el del EdgeHost).</summary>
    public void Publish(ulong cycle) => Plc.Server.Nodes.Publish(new LatchedRecord
    {
        PlcEpoch = Scenario.Epoch, CycleId = cycle, WorkOrderId = StationScenario.WorkOrder, DetectionTimestamp = DateTime.UtcNow,
    });

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Sender.ContinueWith(_ => { }, TaskScheduler.Default);
        await Lease;
        await Ingest.StopAsync(CancellationToken.None);
        Ingest.Dispose();
        await Rig.DisposeAsync();
        await Plc.DisposeAsync();
        _stop.Dispose();
    }
}
