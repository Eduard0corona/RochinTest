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
        var factory = new ConnectionFactory { Uri = new Uri(options.Uri), ClientProvidedName = "CartonPrinting.OutboxRelay" };
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
