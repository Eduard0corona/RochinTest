using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using RabbitMQ.Client;

namespace CartonPrinting.Outbox;

/// <summary>Conexión a RabbitMQ. Las credenciales llegan por configuración o secretos, nunca del repositorio.</summary>
public sealed class RabbitMqOptions
{
    public string Uri { get; set; } = string.Empty;

    public string Exchange { get; set; } = "carton.events";

    /// <summary>Con mandatory, un mensaje sin cola enlazada vuelve como error y no se marca publicado.</summary>
    public bool Mandatory { get; set; } = true;

    /// <summary>TLS hacia el broker. Un Uri <c>amqps://</c> lo activa; aquí se fijan el nombre del servidor y el certificado de cliente.</summary>
    public RabbitMqTlsOptions Tls { get; set; } = new();

    /// <summary>
    /// La fábrica de conexiones del publicador y de los consumidores. Con TLS (Uri <c>amqps://</c> o <see cref="RabbitMqTlsOptions.Enabled"/>)
    /// exige TLS 1.2 o 1.3 y un certificado del broker válido para el nombre esperado, sin errores de política aceptados.
    /// </summary>
    public ConnectionFactory CreateConnectionFactory(string clientName)
    {
        var uri = new Uri(Uri);
        var factory = new ConnectionFactory { Uri = uri, ClientProvidedName = clientName };
        if (Tls.Enabled || uri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase))
        {
            factory.Ssl = new SslOption
            {
                Enabled = true,
                ServerName = string.IsNullOrWhiteSpace(Tls.ServerName) ? uri.Host : Tls.ServerName,
                CertPath = Tls.ClientCertificatePath ?? string.Empty,
                CertPassphrase = Tls.ClientCertificatePassphrase ?? string.Empty,
                Version = SslProtocols.Tls12 | SslProtocols.Tls13,
                AcceptablePolicyErrors = SslPolicyErrors.None,
            };
        }

        return factory;
    }
}

/// <summary>
/// TLS hacia RabbitMQ (RENDIMIENTO_Y_SEGURIDAD: "RabbitMQ con TLS y permisos por vhost"). La ruta y la frase del certificado de
/// cliente llegan del almacén de secretos (variables de entorno del servicio), nunca del repositorio.
/// </summary>
public sealed class RabbitMqTlsOptions
{
    /// <summary>Activa TLS aunque el Uri sea <c>amqp://</c> (con <c>amqps://</c> ya está activo).</summary>
    public bool Enabled { get; set; }

    /// <summary>Nombre esperado en el certificado del broker; por defecto, el host del Uri.</summary>
    public string? ServerName { get; set; }

    /// <summary>Certificado de cliente (PFX) para TLS mutuo.</summary>
    public string? ClientCertificatePath { get; set; }

    public string? ClientCertificatePassphrase { get; set; }
}

/// <summary>Evento leído del Outbox para publicar.</summary>
public sealed record OutboxMessage(long Sequence, Guid EventId, string EventType, string OrderingKey, string Payload, DateTime CreatedAt);

public interface IOutboxPublisher : IAsyncDisposable
{
    /// <summary>Publica y espera el publisher confirm; lanza si el broker no confirma o devuelve el mensaje.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>
/// Publicación en el exchange <c>carton.events</c> (topic) con publisher confirms: claves
/// <c>carton.detected.v1</c> y <c>print.result.v1</c>, <c>message-id</c> = EventId y cabecera
/// <c>ordering-key</c> = idempotencyKey. Publisher confirms no equivale a exactly-once (I22): los
/// consumidores deduplican con InboxMessage.
/// </summary>
public sealed class RabbitMqPublisher(RabbitMqOptions options) : IOutboxPublisher
{
    public const string CartonDetectedKey = "carton.detected.v1";
    public const string PrintResultKey = "print.result.v1";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public static string RoutingKeyFor(string eventType) => eventType switch
    {
        "CartonDetected" => CartonDetectedKey,
        "PrintJobResult" => PrintResultKey,
        _ => throw new ArgumentException($"Tipo de evento desconocido: {eventType}.", nameof(eventType)),
    };

    public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var channel = await EnsureChannelAsync(ct);
            var props = new BasicProperties
            {
                MessageId = message.EventId.ToString(),
                ContentType = "application/json",
                ContentEncoding = "utf-8",
                DeliveryMode = DeliveryModes.Persistent,
                Type = message.EventType,
                Headers = new Dictionary<string, object?> { ["ordering-key"] = message.OrderingKey },
            };
            try
            {
                await channel.BasicPublishAsync(options.Exchange, RoutingKeyFor(message.EventType), options.Mandatory, props,
                    Encoding.UTF8.GetBytes(message.Payload), ct);
            }
            catch
            {
                await ResetAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await ResetAsync();
        var factory = options.CreateConnectionFactory("CartonPrinting.OutboxRelay");
        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await _channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        return _channel;
    }

    private async Task ResetAsync()
    {
        if (_channel is not null)
        {
            try
            {
                await _channel.DisposeAsync();
            }
            catch (Exception)
            {
                // Canal ya cerrado por el broker.
            }

            _channel = null;
        }

        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync();
            }
            catch (Exception)
            {
                // Conexión ya cerrada.
            }

            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}
