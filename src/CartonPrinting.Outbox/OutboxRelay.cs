using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.Outbox;

public sealed class OutboxRelayOptions
{
    public int BatchSize { get; set; } = 200;

    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Outbox Relay: lee los eventos pendientes en orden de OutboxSequence (IX_OutboxEvent_Pending),
/// publica cada uno con publisher confirm y marca PublishedAt solo después del confirm. Si una
/// publicación falla se detiene y reintenta con espera exponencial, sin saltarse el orden. Un
/// confirm perdido produce una republicación con el mismo EventId: el consumidor deduplica.
/// </summary>
public sealed class OutboxRelay(
    SqlDb db,
    IOutboxPublisher publisher,
    EdgeClock clock,
    CartonMetrics metrics,
    OutboxRelayOptions options,
    ILogger<OutboxRelay>? logger = null) : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<OutboxRelay>.Instance;

    /// <summary>Publica un lote. Devuelve cuántos quedaron publicados; lanza si una publicación falla.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken ct)
    {
        var pending = await db.ReadAsync<IReadOnlyList<OutboxMessage>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT TOP (@n) OutboxSequence, EventId, EventType, OrderingKey, Payload, CreatedAt
                FROM dbo.OutboxEvent WITH (INDEX (IX_OutboxEvent_Pending))
                WHERE PublishedAt IS NULL
                ORDER BY OutboxSequence;
                """);
            cmd.Int("@n", options.BatchSize);
            var list = new List<OutboxMessage>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new OutboxMessage(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetUtc(5)));
            }

            return list;
        }, ct);

        var published = 0;
        foreach (var message in pending)
        {
            using var activity = CartonTelemetry.ActivitySource.StartActivity("outbox.publish");
            activity?.SetTag(CartonTelemetry.Tags.EventId, message.EventId);
            activity?.SetTag(CartonTelemetry.Tags.IdempotencyKey, message.OrderingKey);
            activity?.SetTag("eventType", message.EventType);
            try
            {
                await publisher.PublishAsync(message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(message.Sequence, ex.Message, ct);
                throw;
            }

            await db.InTransactionAsync(async (conn, tx) =>
            {
                await using var cmd = conn.Command(tx, """
                    UPDATE dbo.OutboxEvent
                    SET PublishedAt = @now, PublishAttempts = PublishAttempts + 1, LastError = NULL
                    WHERE OutboxSequence = @seq AND PublishedAt IS NULL;
                    """);
                cmd.DateTime2("@now", clock.UtcNow).BigInt("@seq", message.Sequence);
                await cmd.ExecuteNonQueryAsync(ct);
            }, ct);
            metrics.Increment(MetricNames.OutboxPublished, ("type", message.EventType));
            published++;
        }

        await UpdatePendingAgeAsync(ct);
        return published;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await PublishPendingAsync(stoppingToken);
                backoff = TimeSpan.FromMilliseconds(250);
                if (count == 0)
                {
                    await Task.Delay(options.IdleDelay, clock.TimeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Outbox Relay: publicación fallida; reintento en {Backoff}", backoff);
                await Task.Delay(backoff, clock.TimeProvider, stoppingToken);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, options.MaxBackoff.Ticks));
            }
        }
    }

    private Task RecordFailureAsync(long sequence, string error, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            await using var cmd = conn.Command(tx, """
                UPDATE dbo.OutboxEvent SET PublishAttempts = PublishAttempts + 1, LastError = @error
                WHERE OutboxSequence = @seq AND PublishedAt IS NULL;
                """);
            cmd.NVarChar("@error", error.Length <= 512 ? error : error[..512], 512).BigInt("@seq", sequence);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);

    private async Task UpdatePendingAgeAsync(CancellationToken ct)
    {
        // Métrica de monitoreo: edad del pendiente más antiguo (CreatedAt es el reloj de auditoría de SQL).
        var age = await db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT ISNULL(DATEDIFF_BIG(MILLISECOND, MIN(CreatedAt), SYSUTCDATETIME()), 0)
                FROM dbo.OutboxEvent WHERE PublishedAt IS NULL;
                """);
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);
        metrics.SetGauge(MetricNames.OutboxPendingAgeSeconds, age / 1000.0);
    }
}
