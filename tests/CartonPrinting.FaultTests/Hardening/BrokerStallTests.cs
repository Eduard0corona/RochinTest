using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Outbox;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// Un broker que deja de responder sin cerrar la conexión (connection.blocked o una red colgada, verificación final de la Fase 7):
/// la publicación no recibe su confirm. outbox_pending_age_seconds tiene que crecer igual (DOCUMENTO_PRINCIPAL §19: "Más de 30 s:
/// broker caído o relay detenido"), y al volver el broker el Outbox se publica entero.
/// </summary>
[Collection(nameof(BrokerOutageCollection))]
public sealed class BrokerStallTests(SqlServerFixture sql, RabbitMqFixture rabbit) : IAsyncLifetime, IClassFixture<RabbitMqFixture>
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f7stall", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Spec", "T08")]
    public async Task A_broker_that_stops_answering_still_raises_the_pending_age_and_everything_is_published_when_it_answers()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        var broker = new Uri(rabbit.Uri);
        await using var proxy = new TcpProxy(broker.Host, broker.Port).Start();
        var exchange = "carton.events.stall";
        var consumer = new InboxConsumer(s.Sql, "mes-stall", (_, _, _, _) => Task.CompletedTask, s.Metrics);
        await using var subscription = await consumer.SubscribeAsync(new RabbitMqOptions { Uri = rabbit.Uri, Exchange = exchange }, "mes.stall",
            [RabbitMqPublisher.CartonDetectedKey, RabbitMqPublisher.PrintResultKey], Ct);
        await using var publisher = new RabbitMqPublisher(new RabbitMqOptions
        {
            Uri = new UriBuilder(broker) { Host = "127.0.0.1", Port = proxy.Port }.Uri.ToString(), Exchange = exchange,
        });
        var relay = new OutboxRelay(s.Sql, publisher, s.Clock, s.Metrics, new OutboxRelayOptions
        {
            PublishTimeout = TimeSpan.FromSeconds(2), PendingAgeInterval = TimeSpan.FromMilliseconds(200),
        });
        await relay.StartAsync(Ct);
        try
        {
            await s.RegisterAsync(s.Cycle(940), ct: Ct);
            await WaitAsync(async () => await PendingAsync(s) == 0, "Outbox al día");

            proxy.Freeze();
            await s.RegisterAsync(s.Cycle(941), ct: Ct);
            await WaitAsync(() => Task.FromResult(s.Metrics.Gauge(MetricNames.OutboxPendingAgeSeconds) >= 5), "outbox_pending_age_seconds creciendo sin confirm");
            await WaitAsync(async () => await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxEvent WHERE PublishedAt IS NULL AND LastError IS NOT NULL",
                null, Ct) > 0, "la publicación sin confirm registrada como falla");

            proxy.Resume();
            await WaitAsync(async () => await PendingAsync(s) == 0, "Outbox publicado al volver el broker", TimeSpan.FromMinutes(2));
            await WaitAsync(() => Task.FromResult(s.Metrics.Gauge(MetricNames.OutboxPendingAgeSeconds) < 1), "la alerta baja");
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
        }
    }

    private static Task<int> PendingAsync(StationScenario s) =>
        s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxEvent WHERE PublishedAt IS NULL", null, Ct);

    private static async Task WaitAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"No se cumplió a tiempo: {what}.");
            await Task.Delay(100, Ct);
        }
    }
}
