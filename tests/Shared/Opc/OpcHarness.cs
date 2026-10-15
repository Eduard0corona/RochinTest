using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Simulators.Plc;

namespace CartonPrinting.Testing;

/// <summary>
/// PLC simulado (servidor OPC UA en proceso), proxy TCP para inyectar fallas y el cliente OPC UA real
/// de la ingesta conectado a través del proxy.
/// </summary>
public sealed class OpcHarness : IAsyncDisposable
{
    private OpcHarness(string stationId, string pki, int serverPort)
    {
        StationId = stationId;
        Pki = pki;
        ServerPort = serverPort;
    }

    public string StationId { get; }
    public string Pki { get; }
    public int ServerPort { get; }
    public PlcSimulatorServer Server { get; private set; } = null!;
    public TcpProxy Proxy { get; private set; } = null!;
    public OpcUaCycleSource Client { get; private set; } = null!;
    public Channel<RawNotification> Notifications { get; } = Channel.CreateBounded<RawNotification>(10_000);
    public CartonMetrics Metrics { get; } = new();

    public static async Task<OpcHarness> StartAsync(string stationId, EdgeClock clock, Action<OpcIngestOptions>? configure = null,
        bool withFifo = false)
    {
        var pki = Path.Combine(Path.GetTempPath(), "cartonprinting-pki", Guid.NewGuid().ToString("N")[..8]);
        var h = new OpcHarness(stationId, pki, FreePort());
        h.Server = await PlcSimulatorServer.StartAsync(new PlcSimulatorOptions { StationId = stationId, Port = h.ServerPort, PkiRoot = Path.Combine(pki, "server") });
        h.Proxy = new TcpProxy("localhost", h.ServerPort).Start();
        var options = new OpcIngestOptions
        {
            EndpointUrl = $"opc.tcp://localhost:{h.Proxy.Port}/CartonPlc",
            RecordNodeId = h.Server.Nodes.RecordNodeId,
            FifoNodeId = withFifo ? h.Server.Nodes.FifoNodeId : null,
            LabelingReadyNodeId = h.Server.Nodes.LabelingReadyNodeId,
            PrintWindowNodeId = h.Server.Nodes.PrintWindowNodeId,
            SecurityMode = "None",
            PkiRoot = Path.Combine(pki, "client"),
            AutoAcceptUntrustedCertificates = true,
            KeepAliveIntervalMs = 500,
            ReconnectPeriodMs = 300,
        };
        configure?.Invoke(options);
        h.Client = new OpcUaCycleSource(options, clock, h.Metrics);
        await h.Client.StartAsync(h.Notifications.Writer, CancellationToken.None);
        return h;
    }

    /// <summary>Espera la próxima notificación (con plazo).</summary>
    public async Task<RawNotification> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        return await Notifications.Reader.ReadAsync(cts.Token);
    }

    /// <summary>Recoge notificaciones hasta que el predicado se cumpla o venza el plazo.</summary>
    public async Task<List<RawNotification>> CollectUntilAsync(Func<List<RawNotification>, bool> done, TimeSpan? timeout = null)
    {
        var list = new List<RawNotification>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (!done(list))
            {
                list.Add(await Notifications.Reader.ReadAsync(cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
            // Devuelve lo recibido: la prueba decide con sus aserciones.
        }

        return list;
    }

    /// <summary>Reinicia el servidor simulado en el mismo puerto: la sesión se pierde.</summary>
    public async Task RestartServerAsync(Action<PlcNodeManager>? beforeClientsReconnect = null)
    {
        await Server.DisposeAsync();
        Server = await PlcSimulatorServer.StartAsync(new PlcSimulatorOptions { StationId = StationId, Port = ServerPort, PkiRoot = Path.Combine(Pki, "server") });
        beforeClientsReconnect?.Invoke(Server.Nodes);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Proxy.DisposeAsync();
        await Server.DisposeAsync();
        Metrics.Dispose();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
