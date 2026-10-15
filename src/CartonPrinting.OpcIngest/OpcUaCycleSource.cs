using System.Threading.Channels;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace CartonPrinting.OpcIngest;

/// <summary>Configuración del cliente OPC UA de una estación.</summary>
public sealed class OpcIngestOptions
{
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>Nodo del registro latched del ciclo (no el booleano de presencia).</summary>
    public string RecordNodeId { get; set; } = string.Empty;

    /// <summary>FIFO de ciclos del PLC (perfil extendido); vacío si el PLC no la tiene.</summary>
    public string? FifoNodeId { get; set; }

    public string? LabelingReadyNodeId { get; set; }

    public string? PrintWindowNodeId { get; set; }

    /// <summary>Solicitudes; el servidor revisa los valores y se registran los revisados.</summary>
    public double SamplingIntervalMs { get; set; } = 10;

    public int PublishingIntervalMs { get; set; } = 20;

    /// <summary>Mayor que 1 para no perder cambios entre publicaciones; con cola, el servidor marca Overflow.</summary>
    public uint QueueSize { get; set; } = 10;

    public int KeepAliveIntervalMs { get; set; } = 1000;

    public uint SessionTimeoutMs { get; set; } = 60_000;

    public int ReconnectPeriodMs { get; set; } = 1000;

    /// <summary>SignAndEncrypt con validación estricta en planta; None solo en pruebas locales.</summary>
    public string SecurityMode { get; set; } = "SignAndEncrypt";

    public string PkiRoot { get; set; } = "pki";

    /// <summary>Solo el simulador en pruebas: en producción la validación de certificados es estricta.</summary>
    public bool AutoAcceptUntrustedCertificates { get; set; }
}

/// <summary>
/// Fuente de ciclos de una estación: entrega notificaciones crudas del registro latched en un canal
/// acotado. La implementación de producción es el cliente OPC UA real; en las pruebas habla con el PLC
/// simulado (servidor OPC UA en proceso).
/// </summary>
public interface ICycleSource : IAsyncDisposable
{
    bool Connected { get; }

    Task StartAsync(ChannelWriter<RawNotification> writer, CancellationToken ct);
}

/// <summary>
/// Notificación cruda: el callback OPC solo valida el StatusCode, sella la recepción (UTC del EdgeHost
/// y marca monotónica), clasifica el transporte y encola. No toca SQL, ERP ni impresora.
/// </summary>
public sealed record RawNotification(
    string? Value,
    bool StatusGood,
    uint StatusCode,
    bool Overflow,
    string Transport,
    DateTime ReceivedAt,
    long ReceivedTimestamp,
    DateTime? SourceTimestamp,
    DateTime? ServerTimestamp,
    uint SequenceNumber,
    string NodeId);

/// <summary>
/// Cliente OPC UA real (OPC Foundation UA .NET Standard) de la ingesta. Clasificación del transporte:
/// <list type="bullet">
/// <item>La primera notificación de un monitored item creado o transferido es el valor actual: <c>READ</c>.</item>
/// <item>Una notificación de un mensaje con número de secuencia menor que el último procesado llegó
/// reenviada (Republish): <c>REPUBLISH</c>.</item>
/// <item>Tras una interrupción (keep-alive perdido y reconexión), lo que el servidor tenía pendiente al
/// reconectar se entrega tarde: <c>REPUBLISH</c> hasta el primer mensaje posterior a ese pendiente.</item>
/// <item>Después de reconectar se lee el registro actual (<c>READ</c>) y, si existe, la FIFO (<c>PLC_FIFO</c>).</item>
/// <item>Lo demás es <c>SUBSCRIPTION</c>, la única entrega en vivo; el bit Overflow va aparte.</item>
/// </list>
/// La clasificación es conservadora: ante la duda un registro se trata como recuperado, nunca como vivo.
/// </summary>
public sealed class OpcUaCycleSource : ICycleSource, ILabelingReadySink
{
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });

    private readonly OpcIngestOptions _options;
    private readonly EdgeClock _clock;
    private readonly CartonMetrics _metrics;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private ChannelWriter<RawNotification>? _writer;
    private ApplicationConfiguration? _config;
    private ISession? _session;
    private Subscription? _subscription;
    private MonitoredItem? _item;
    private SessionReconnectHandler? _reconnect;
    private bool _firstPending = true;
    private uint _highestSequence;
    private uint _subscriptionId;
    private bool _recovering;
    private DateTime? _republishedBefore;

    public OpcUaCycleSource(OpcIngestOptions options, EdgeClock clock, CartonMetrics metrics, ILogger<OpcUaCycleSource>? logger = null)
    {
        _options = options;
        _clock = clock;
        _metrics = metrics;
        _logger = logger ?? NullLogger<OpcUaCycleSource>.Instance;
    }

    /// <summary>Intervalos revisados por el servidor (10 y 20 ms son solicitudes).</summary>
    public double RevisedSamplingIntervalMs { get; private set; }

    public double RevisedPublishingIntervalMs { get; private set; }

    public uint RevisedQueueSize { get; private set; }

    public int ReconnectCount { get; private set; }

    /// <summary>Eventos de conexión (keep-alive, reconexión, recuperación) con el reloj del EdgeHost.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> ConnectionEvents { get; } = new();

    private void Note(string text)
    {
        ConnectionEvents.Enqueue($"{_clock.UtcNow:HH:mm:ss.fff} {text}");
        _logger.LogInformation("OPC UA: {Event}", text);
    }

    /// <summary>
    /// Sesión conectada y recuperación terminada. La recuperación termina cuando terminan sus lecturas
    /// (registro actual, Republish y FIFO), no cuando llega una caja: con LabelingReady bajo, la línea
    /// puede estar detenida y no pasar ninguna.
    /// </summary>
    public bool Connected => _session is { Connected: true } && !_recovering;

    /// <summary>Notificaciones descartadas porque el canal acotado estaba lleno.</summary>
    public long DroppedNotifications => Interlocked.Read(ref _dropped);

    private long _dropped;

    public async Task StartAsync(ChannelWriter<RawNotification> writer, CancellationToken ct)
    {
        _writer = writer;
        _config = await BuildConfigurationAsync(ct);
        var endpoint = await CoreClientUtils.SelectEndpointAsync(_config, _options.EndpointUrl,
            _options.SecurityMode != "None", 15_000, Telemetry, ct);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (_options.SecurityMode == "SignAndEncrypt" && endpoint.SecurityMode != MessageSecurityMode.SignAndEncrypt)
        {
            throw new InvalidOperationException("El servidor no ofrece SignAndEncrypt.");
        }

        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(_config));
        var session = await new DefaultSessionFactory(Telemetry).CreateAsync(_config, configured, false, "CartonPrinting.OpcIngest",
            _options.SessionTimeoutMs, new UserIdentity(new AnonymousIdentityToken()), null, ct);
        session.KeepAliveInterval = _options.KeepAliveIntervalMs;
        session.KeepAlive += OnKeepAlive;
        _session = session;
        await CreateSubscriptionAsync(session, ct);
        _reconnect = new SessionReconnectHandler(Telemetry, true, _options.ReconnectPeriodMs * 10);
    }

    /// <summary>Escribe el latido LabelingReady al PLC (el PLC aplica su política si cae).</summary>
    public async Task WriteLabelingReadyAsync(bool ready, CancellationToken ct)
    {
        if (_options.LabelingReadyNodeId is null || _session is not { Connected: true } session)
        {
            return;
        }

        var write = new WriteValue
        {
            NodeId = NodeId.Parse(_options.LabelingReadyNodeId),
            AttributeId = Attributes.Value,
            Value = new DataValue(new Variant(ready)),
        };
        var response = await session.WriteAsync(null, [write], ct);
        var status = response.Results is { Count: > 0 } results ? results[0] : Opc.Ua.StatusCodes.BadUnexpectedError;
        if (StatusCode.IsBad(status))
        {
            throw new ServiceResultException(status);
        }
    }

    /// <summary>Lee el tag de ventana del PLC (compuerta PLC_WINDOW_TAG).</summary>
    public async Task<(ulong CycleId, DateTime SourceTimestamp)?> ReadPrintWindowAsync(CancellationToken ct)
    {
        if (_options.PrintWindowNodeId is null || _session is not { Connected: true } session)
        {
            return null;
        }

        var value = await session.ReadValueAsync(NodeId.Parse(_options.PrintWindowNodeId), ct);
        return StatusCode.IsGood(value.StatusCode) && value.Value is ulong cycle
            ? (cycle, DateTime.SpecifyKind(value.SourceTimestamp, DateTimeKind.Utc))
            : null;
    }

    private async Task CreateSubscriptionAsync(ISession session, CancellationToken ct)
    {
        var subscription = new Subscription(Telemetry, new SubscriptionOptions
        {
            DisplayName = "CartonCycleRecord",
            PublishingInterval = _options.PublishingIntervalMs,
            KeepAliveCount = 10,
            LifetimeCount = 100,
            PublishingEnabled = true,
            TimestampsToReturn = TimestampsToReturn.Both,
            RepublishAfterTransfer = true,
            SequentialPublishing = false,
        });
        var item = new MonitoredItem(Telemetry, new MonitoredItemOptions
        {
            DisplayName = "CartonCycleRecord",
            StartNodeId = NodeId.Parse(_options.RecordNodeId),
            AttributeId = Attributes.Value,
            MonitoringMode = MonitoringMode.Reporting,
            SamplingInterval = (int)_options.SamplingIntervalMs,
            QueueSize = _options.QueueSize,
            DiscardOldest = true,
        });
        subscription.FastDataChangeCallback = OnDataChange;
        subscription.AddItem(item);
        lock (_gate)
        {
            _firstPending = true;
            _highestSequence = 0;
        }

        _subscription = subscription;
        _item = item;
        session.AddSubscription(subscription);
        await subscription.CreateAsync(ct);
        RevisedPublishingIntervalMs = subscription.CurrentPublishingInterval;
        RevisedSamplingIntervalMs = item.Status.SamplingInterval;
        RevisedQueueSize = item.Status.QueueSize;
        _logger.LogInformation("Suscripción OPC creada: muestreo {Sampling} ms y publicación {Publishing} ms revisados (solicitados {RS} y {RP})",
            RevisedSamplingIntervalMs, RevisedPublishingIntervalMs, _options.SamplingIntervalMs, _options.PublishingIntervalMs);
    }

    /// <summary>Callback de OPC: valida, sella, clasifica y encola. Nunca bloquea ni toca SQL.</summary>
    private void OnDataChange(Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        var started = _clock.GetTimestamp();
        var seq = notification.SequenceNumber;

        // El transporte se decide por mensaje (todas sus notificaciones comparten número de secuencia);
        // solo la primera notificación del monitored item tras crearlo o transferirlo es READ.
        string messageTransport;
        bool recovering;
        DateTime? watermark;
        lock (_gate)
        {
            // Una suscripción recreada (reinicio del servidor, sesión nueva o suscripción vencida) vuelve a
            // numerar sus mensajes desde 1 y su primera notificación es el valor actual: READ.
            if (subscription.Id != _subscriptionId)
            {
                _subscriptionId = subscription.Id;
                _highestSequence = 0;
                _firstPending = true;
            }

            recovering = _recovering;
            watermark = _republishedBefore;
            messageTransport = seq != 0 && seq <= _highestSequence ? Transport.Republish : Transport.Subscription;

            _highestSequence = Math.Max(_highestSequence, seq);
        }

        foreach (var change in notification.MonitoredItems)
        {
            if (_item is null || change.ClientHandle != _item.ClientHandle)
            {
                continue;
            }

            var transport = messageTransport;
            if (recovering)
            {
                // Durante la recuperación, un cambio que el servidor registró antes de la reconexión llegó tarde
                // (REPUBLISH), aunque viaje en un mensaje nuevo; solo uno posterior es en vivo. Sin la hora del
                // servidor o sin ServerTimestamp, se trata como recuperado (ante la duda, nunca en vivo).
                transport = watermark is { } w && change.Value.ServerTimestamp != DateTime.MinValue
                    && DateTime.SpecifyKind(change.Value.ServerTimestamp, DateTimeKind.Utc) > w
                        ? Transport.Subscription
                        : Transport.Republish;
            }

            lock (_gate)
            {
                if (_firstPending)
                {
                    _firstPending = false;
                    transport = Transport.Read;
                }
            }

            Enqueue(change.Value, transport, seq);
        }

        _metrics.Record(MetricNames.OpcCallbackDurationMs, _clock.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Notificaciones de un mensaje obtenido con Republish: llegan tarde, son REPUBLISH.</summary>
    private void ProcessRepublished(NotificationMessage message)
    {
        foreach (var change in message.GetDataChanges(false))
        {
            if (_item is not null && change.ClientHandle == _item.ClientHandle)
            {
                Enqueue(change.Value, Transport.Republish, message.SequenceNumber);
            }
        }

        lock (_gate)
        {
            _highestSequence = Math.Max(_highestSequence, message.SequenceNumber);
        }
    }

    private void Enqueue(DataValue value, string transport, uint sequence)
    {
        var good = StatusCode.IsGood(value.StatusCode);
        var raw = new RawNotification(
            value.Value as string,
            good,
            value.StatusCode.Code,
            value.StatusCode.Overflow,
            transport,
            _clock.UtcNow,
            _clock.GetTimestamp(),
            value.SourceTimestamp == DateTime.MinValue ? null : DateTime.SpecifyKind(value.SourceTimestamp, DateTimeKind.Utc),
            value.ServerTimestamp == DateTime.MinValue ? null : DateTime.SpecifyKind(value.ServerTimestamp, DateTimeKind.Utc),
            sequence,
            _options.RecordNodeId);
        if (!_writer!.TryWrite(raw))
        {
            Interlocked.Increment(ref _dropped);
            // Canal acotado lleno: señal de sobrecarga, no un buffer infinito. El registro se pierde
            // como notificación y aparecerá como hueco de secuencia.
            _metrics.Increment("opc_ingest_channel_full_total");
        }
    }

    private void OnKeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status) || _reconnect is null)
        {
            return;
        }

        lock (_gate)
        {
            _recovering = true;
            _republishedBefore = null;
        }

        Note($"keep-alive perdido ({e.Status}); estado del reconector {_reconnect.State}");
        _reconnect.BeginReconnect(session, _options.ReconnectPeriodMs, (sender, args) => OnReconnectComplete(sender, args));
    }

    private void OnReconnectComplete(object? sender, EventArgs e)
    {
        var handler = (SessionReconnectHandler)sender!;
        Note($"reconexión terminada: sesión {(handler.Session is null ? "nula" : ReferenceEquals(handler.Session, _session) ? "la misma" : "nueva")}, conectada={handler.Session?.Connected}");
        if (handler.Session is null)
        {
            return;
        }

        ReconnectCount++;
        _metrics.Increment(MetricNames.OpcReconnect);
        if (!ReferenceEquals(handler.Session, _session))
        {
            // Sesión nueva con suscripciones transferidas: la primera notificación vuelve a ser READ.
            _session = handler.Session;
            _session.KeepAlive += OnKeepAlive;
            lock (_gate)
            {
                _firstPending = true;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RecoverAfterReconnectAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Note($"recuperación tras reconectar fallida: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Tras reconectar, en pasos independientes (el fallo de uno no salta los demás): hora del servidor como marca
    /// de lo recuperado; lectura del registro actual (READ), que va primero para no demorarla; Republish de los
    /// mensajes que se perdieron en tránsito; lectura de la FIFO (PLC_FIFO). Al terminar, la recuperación termina,
    /// llegue o no una caja. El valor actual nunca se toma como historial: la clasificación abre huecos.
    /// </summary>
    public async Task RecoverAfterReconnectAsync(CancellationToken ct)
    {
        try
        {
            if (_session is not { Connected: true } session)
            {
                Note("recuperación omitida: sesión no conectada");
                return;
            }

            try
            {
                var serverNow = await session.ReadValueAsync(VariableIds.Server_ServerStatus_CurrentTime, ct);
                if (StatusCode.IsGood(serverNow.StatusCode) && serverNow.Value is DateTime t)
                {
                    lock (_gate)
                    {
                        _republishedBefore = DateTime.SpecifyKind(t, DateTimeKind.Utc);
                    }
                }
            }
            catch (ServiceResultException ex)
            {
                Note($"hora del servidor no disponible ({ex.StatusCode}): lo que llegue durante la recuperación se trata como recuperado");
            }

            try
            {
                var current = await session.ReadValueAsync(NodeId.Parse(_options.RecordNodeId), ct);
                Note($"lectura tras reconectar: {current.StatusCode}");
                Enqueue(current, Transport.Read, 0);
            }
            catch (ServiceResultException ex)
            {
                Note($"lectura del registro tras reconectar fallida: {ex.StatusCode}");
            }

            try
            {
                // Republish: los mensajes que el servidor envió y no llegaron siguen en su cola de retransmisión
                // hasta el acuse. La lista llega con la primera respuesta de publicación tras reconectar.
                if (_subscription is { } subscription)
                {
                    var waited = 0;
                    while (!(subscription.AvailableSequenceNumbers?.Any() ?? false) && waited < 1000)
                    {
                        await Task.Delay(50, ct);
                        waited += 50;
                    }

                    uint highest;
                    lock (_gate)
                    {
                        highest = _highestSequence;
                    }

                    var available = subscription.AvailableSequenceNumbers?.Where(seq => seq > highest).Order().ToArray() ?? [];
                    Note($"Republish: disponibles sin procesar [{string.Join(",", available)}] (último procesado {highest})");
                    foreach (var seq in available)
                    {
                        var response = await ((SessionClient)session).RepublishAsync(null, subscription.Id, seq, ct);
                        ProcessRepublished(response.NotificationMessage);
                    }
                }
            }
            catch (ServiceResultException ex)
            {
                Note($"Republish fallido: {ex.StatusCode}");
            }

            if (_options.FifoNodeId is not null)
            {
                try
                {
                    var fifo = await session.ReadValueAsync(NodeId.Parse(_options.FifoNodeId), ct);
                    if (StatusCode.IsGood(fifo.StatusCode) && fifo.Value is string[] pending)
                    {
                        foreach (var record in pending)
                        {
                            Enqueue(new DataValue(new Variant(record), fifo.StatusCode, fifo.SourceTimestamp, fifo.ServerTimestamp), Transport.PlcFifo, 0);
                        }
                    }
                }
                catch (ServiceResultException ex)
                {
                    Note($"lectura de la FIFO fallida: {ex.StatusCode}");
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _recovering = false;
                _republishedBefore = null;
            }

            Note("recuperación terminada");
        }
    }

    private async Task<ApplicationConfiguration> BuildConfigurationAsync(CancellationToken ct)
    {
        var config = new ApplicationConfiguration
        {
            ApplicationName = "CartonPrinting EdgeHost",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:CartonPrinting:EdgeHost",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(_options.PkiRoot, "own"),
                    SubjectName = "CN=CartonPrinting EdgeHost",
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(_options.PkiRoot, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(_options.PkiRoot, "trusted") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(_options.PkiRoot, "rejected") },
                AutoAcceptUntrustedCertificates = _options.AutoAcceptUntrustedCertificates,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = (int)_options.SessionTimeoutMs },
            CertificateValidator = new CertificateValidator(Telemetry),
        };
        await config.ValidateAsync(ApplicationType.Client, ct);
        if (_options.AutoAcceptUntrustedCertificates)
        {
            config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = e.Error.StatusCode == Opc.Ua.StatusCodes.BadCertificateUntrusted;
        }

        var application = new ApplicationInstance(config, Telemetry);
        await application.CheckApplicationInstanceCertificatesAsync(false, null, ct);
        return config;
    }

    public async ValueTask DisposeAsync()
    {
        _reconnect?.Dispose();
        if (_session is not null)
        {
            try
            {
                await _session.CloseAsync();
            }
            catch (ServiceResultException)
            {
                // Sesión ya cerrada por el servidor.
            }

            _session.Dispose();
        }
    }
}
