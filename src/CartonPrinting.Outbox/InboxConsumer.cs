using System.Text;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using Microsoft.Data.SqlClient;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CartonPrinting.Outbox;

/// <summary>Mensaje recibido por un consumidor.</summary>
public sealed record InboundMessage(Guid EventId, string RoutingKey, string Payload, bool Redelivered);

/// <summary>
/// Consumidor idempotente: inserta (ConsumerName, EventId) en InboxMessage y aplica su efecto en la
/// misma transacción; el ACK manual va después del commit. Un duplicado no tiene efecto y se
/// confirma igual. Cola quorum durable enlazada al exchange.
/// </summary>
public sealed class InboxConsumer(
    SqlDb db,
    string consumerName,
    Func<SqlConnection, SqlTransaction, InboundMessage, CancellationToken, Task> applyEffect,
    CartonMetrics metrics)
{
    public string ConsumerName { get; } = consumerName;

    /// <summary>Procesa un mensaje. Devuelve false si era un duplicado (sin efecto).</summary>
    public async Task<bool> HandleAsync(InboundMessage message, CancellationToken ct)
    {
        var applied = await db.InTransactionAsync(async (conn, tx) =>
        {
            await using (var insert = conn.Command(tx, """
                DECLARE @ins TABLE (EventId UNIQUEIDENTIFIER NOT NULL);
                INSERT INTO dbo.InboxMessage (ConsumerName, EventId)
                OUTPUT inserted.EventId INTO @ins (EventId)
                SELECT @consumer, @eventId
                WHERE NOT EXISTS (
                    SELECT 1 FROM dbo.InboxMessage WITH (UPDLOCK, HOLDLOCK)
                    WHERE ConsumerName = @consumer AND EventId = @eventId);
                SELECT COUNT(*) FROM @ins;
                """))
            {
                insert.NVarChar("@consumer", ConsumerName, 128).Guid("@eventId", message.EventId);
                if ((int)(await insert.ExecuteScalarAsync(ct))! == 0)
                {
                    return false;
                }
            }

            await applyEffect(conn, tx, message, ct);
            return true;
        }, ct);

        if (!applied)
        {
            metrics.Increment(MetricNames.InboxDuplicates, ("consumer", ConsumerName));
        }

        return applied;
    }

    /// <summary>
    /// Se suscribe a una cola quorum durable enlazada a las claves dadas. Cada entrega se confirma con
    /// ACK manual solo después del commit de su Inbox; si falla, NACK con reencolado.
    /// </summary>
    public async Task<IAsyncDisposable> SubscribeAsync(RabbitMqOptions options, string queue, IReadOnlyList<string> routingKeys, CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(options.Uri), ClientProvidedName = $"CartonPrinting.Inbox.{ConsumerName}" };
        var connection = await factory.CreateConnectionAsync(ct);
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: ct);
        foreach (var key in routingKeys)
        {
            await channel.QueueBindAsync(queue, options.Exchange, key, cancellationToken: ct);
        }

        await channel.BasicQosAsync(0, 16, false, ct);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var message = new InboundMessage(Guid.Parse(ea.BasicProperties.MessageId!), ea.RoutingKey,
                    Encoding.UTF8.GetString(ea.Body.Span), ea.Redelivered);
                await HandleAsync(message, CancellationToken.None);
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
            }
            catch (Exception)
            {
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
        return new Subscription(connection, channel);
    }

    private sealed class Subscription(IConnection connection, IChannel channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await channel.CloseAsync();
            await channel.DisposeAsync();
            await connection.CloseAsync();
            await connection.DisposeAsync();
        }
    }
}
