using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace CartonPrinting.Simulators.Plc;

/// <summary>Opciones del PLC simulado.</summary>
public sealed record PlcSimulatorOptions
{
    public required string StationId { get; init; }

    public int Port { get; init; }

    /// <summary>Raíz del almacén de certificados (PKI) del simulador; se crea si no existe.</summary>
    public required string PkiRoot { get; init; }

    /// <summary>Política de seguridad que exige el servidor (None solo en pruebas locales).</summary>
    public MessageSecurityMode SecurityMode { get; init; } = MessageSecurityMode.None;
}

/// <summary>
/// PLC simulado: servidor OPC UA en proceso (OPC Foundation UA .NET Standard) que expone el registro
/// latched del ciclo, la FIFO de ciclos pendientes (perfil extendido; aquí solo para T11), el latido
/// LabelingReady que escribe el servicio y el tag de ventana PrintWindowCycleId. Nunca se conecta a
/// equipo real.
/// </summary>
public sealed class PlcSimulatorServer : IAsyncDisposable
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });

    private readonly PlcSimulatorOptions _options;
    private ApplicationInstance? _application;
    private PlcServer? _server;

    private PlcSimulatorServer(PlcSimulatorOptions options) => _options = options;

    public string EndpointUrl => $"opc.tcp://localhost:{_options.Port}/CartonPlc";

    public PlcNodeManager Nodes => _server?.NodeManager ?? throw new InvalidOperationException("El servidor no arrancó.");

    public static async Task<PlcSimulatorServer> StartAsync(PlcSimulatorOptions options)
    {
        var simulator = new PlcSimulatorServer(options);
        await simulator.StartCoreAsync();
        return simulator;
    }

    private async Task StartCoreAsync()
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "CartonPrinting PLC Simulator",
            ApplicationUri = $"urn:localhost:CartonPrinting:PlcSimulator:{_options.StationId}",
            ProductUri = "urn:CartonPrinting:PlcSimulator",
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = OpcPki.Security(_options.PkiRoot, "CN=CartonPrinting PLC Simulator", autoAcceptUntrusted: true),
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000, MaxMessageSize = 4 * 1024 * 1024 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [EndpointUrl],
                SecurityPolicies =
                [
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None },
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 },
                ],
                UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
                MinPublishingInterval = 10,
                MinSubscriptionLifetime = 5000,
                MaxSubscriptionCount = 50,
                MaxMessageQueueSize = 100,
                MaxNotificationQueueSize = 100,
                MaxPublishRequestCount = 20,
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 2000,
            },
            CertificateValidator = new CertificateValidator(Telemetry),
        };
        if (_options.SecurityMode == MessageSecurityMode.SignAndEncrypt)
        {
            config.ServerConfiguration.SecurityPolicies.RemoveAt(0);
        }

        await config.ValidateAsync(ApplicationType.Server);
        _application = new ApplicationInstance(config, Telemetry);
        await _application.CheckApplicationInstanceCertificatesAsync(false, null);
        _server = new PlcServer(_options.StationId);
        await _application.StartAsync(_server);
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.StopAsync();
        }

        _server?.Dispose();
    }

    private sealed class PlcServer(string stationId) : StandardServer
    {
        public PlcNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new PlcNodeManager(server, configuration, stationId);
            return new MasterNodeManager(server, configuration, null, NodeManager);
        }
    }
}
