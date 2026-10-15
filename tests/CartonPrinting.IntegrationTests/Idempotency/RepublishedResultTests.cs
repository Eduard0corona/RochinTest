using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Outbox;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.IntegrationTests.Idempotency;

/// <summary>
/// T14 · Resultado republicado. Un consumidor con InboxMessage recibe el mismo PrintJobResult 3
/// veces (el Outbox Relay lo republica porque el confirm "se perdió"): una sola fila en
/// InboxMessage, un solo efecto, inbox_duplicates_total = 2; el consumidor aplica la versión mayor
/// por (idempotencyKey, labelType). RabbitMQ real con publisher confirms y cola quorum.
/// </summary>
[Trait("Spec", "T14")]
public sealed class RepublishedResultTests(SqlServerFixture sql, RabbitMqFixture rabbit) : IAsyncLifetime, IClassFixture<RabbitMqFixture>
{
    private const string Consumer = "mes-test";
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _db = await sql.CreateDatabaseAsync("t14", ct);
        // Proyección del consumidor de prueba (fuera del modelo de la especificación).
        await using var conn = new SqlConnection(_db.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            EXEC (N'CREATE SCHEMA testconsumer');
            CREATE TABLE testconsumer.ResultProjection (
                IdempotencyKey NVARCHAR(220) NOT NULL, LabelType NVARCHAR(64) NOT NULL, ResultVersion INT NOT NULL,
                Outcome VARCHAR(20) NOT NULL, EffectCount INT NOT NULL,
                CONSTRAINT PK_ResultProjection PRIMARY KEY (IdempotencyKey, LabelType));
            GRANT SELECT, INSERT, UPDATE ON SCHEMA::testconsumer TO [{TestDatabase.AppLogin}];
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Efecto del consumidor: conserva la resultVersion mayor por (idempotencyKey, labelType).</summary>
    private static async Task ApplyHighestVersionAsync(SqlConnection conn, SqlTransaction tx, InboundMessage message, CancellationToken ct)
    {
        if (message.RoutingKey != RabbitMqPublisher.PrintResultKey)
        {
            return;
        }

        using var doc = JsonDocument.Parse(message.Payload);
        var root = doc.RootElement;
        await using var cmd = new SqlCommand("""
            MERGE testconsumer.ResultProjection WITH (HOLDLOCK) AS t
            USING (SELECT @key AS IdempotencyKey, @label AS LabelType) AS s
                ON t.IdempotencyKey = s.IdempotencyKey AND t.LabelType = s.LabelType
            WHEN MATCHED THEN UPDATE SET
                ResultVersion = CASE WHEN @version > t.ResultVersion THEN @version ELSE t.ResultVersion END,
                Outcome = CASE WHEN @version > t.ResultVersion THEN @outcome ELSE t.Outcome END,
                EffectCount = t.EffectCount + 1
            WHEN NOT MATCHED THEN INSERT (IdempotencyKey, LabelType, ResultVersion, Outcome, EffectCount)
                VALUES (@key, @label, @version, @outcome, 1);
            """, conn, tx);
        cmd.Parameters.AddWithValue("@key", root.GetProperty("idempotencyKey").GetString());
        cmd.Parameters.AddWithValue("@label", root.GetProperty("labelType").GetString());
        cmd.Parameters.AddWithValue("@version", root.GetProperty("resultVersion").GetInt32());
        cmd.Parameters.AddWithValue("@outcome", root.GetProperty("outcome").GetString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Same_event_published_three_times_has_one_inbox_row_and_one_effect()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        var (job, _) = await s.UnknownJobAsync(7001, ct);   // PrintJobResult UNKNOWN v1 en el Outbox.
        var options = new RabbitMqOptions { Uri = rabbit.Uri, Exchange = "carton.events.t14" };
        var consumer = new InboxConsumer(s.Sql, Consumer, ApplyHighestVersionAsync, s.Metrics);
        await using var subscription = await consumer.SubscribeAsync(options, "mes.t14",
            [RabbitMqPublisher.CartonDetectedKey, RabbitMqPublisher.PrintResultKey], ct);
        await using var publisher = new RabbitMqPublisher(options);
        var relay = new OutboxRelay(s.Sql, publisher, s.Clock, s.Metrics, new OutboxRelayOptions());

        var resultEventId = (await s.ResultsAsync(job, ct)).Single().GetProperty("eventId").GetGuid();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await relay.PublishPendingAsync(ct) >= 1);
            if (i < 2)
            {
                // El confirm "se perdió": el relay vuelve a publicar el mismo EventId.
                await s.ExecAsync("UPDATE dbo.OutboxEvent SET PublishedAt = NULL WHERE EventId = @id",
                    c => c.Parameters.AddWithValue("@id", resultEventId), ct: ct);
            }
        }

        await WaitUntilAsync(() => s.Metrics.Total(MetricNames.InboxDuplicates, ("consumer", Consumer)) >= 2, ct);

        Assert.Equal(1, await s.CountAsync("FROM dbo.InboxMessage WHERE ConsumerName = @c AND EventId = @e",
            c => c.Parameters.AddRange([new SqlParameter("@c", Consumer), new SqlParameter("@e", resultEventId)]), ct));
        Assert.Equal(1, await s.ScalarAsync<int>("SELECT EffectCount FROM testconsumer.ResultProjection WHERE IdempotencyKey = @k",
            c => c.Parameters.AddWithValue("@k", new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, 7001).IdempotencyKey), ct));
        Assert.Equal(2, s.Metrics.Total(MetricNames.InboxDuplicates, ("consumer", Consumer)));
        Assert.Equal(3, await s.ScalarAsync<int>("SELECT PublishAttempts FROM dbo.OutboxEvent WHERE EventId = @id",
            c => c.Parameters.AddWithValue("@id", resultEventId), ct));
    }

    [Fact]
    public async Task Consumer_keeps_the_highest_result_version_even_when_versions_arrive_out_of_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        var (job, _) = await s.UnknownJobAsync(7101, ct);                                         // v1 UNKNOWN
        await s.Txm.CloseUnresolvedAsync(job, StationScenario.SiteId, s.StationId, "operador.lopez", "Caja en cuarentena",
            "QUARANTINED", s.Clock.UtcNow, ct);                                                    // v2 CLOSED_UNRESOLVED
        var results = await s.ResultsAsync(job, ct);
        Assert.Equal([1, 2], results.Select(r => r.GetProperty("resultVersion").GetInt32()));
        var consumer = new InboxConsumer(s.Sql, Consumer + "-order", ApplyHighestVersionAsync, s.Metrics);

        // v2 llega antes que v1, y v1 se repite.
        var v1 = Inbound(results[0]);
        var v2 = Inbound(results[1]);
        Assert.True(await consumer.HandleAsync(v2, ct));
        Assert.True(await consumer.HandleAsync(v1, ct));
        Assert.False(await consumer.HandleAsync(v1, ct));

        var key = new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, 7101).IdempotencyKey;
        Assert.Equal(2, await s.ScalarAsync<int>("SELECT ResultVersion FROM testconsumer.ResultProjection WHERE IdempotencyKey = @k",
            c => c.Parameters.AddWithValue("@k", key), ct));
        Assert.Equal("CLOSED_UNRESOLVED", await s.ScalarAsync<string>("SELECT Outcome FROM testconsumer.ResultProjection WHERE IdempotencyKey = @k",
            c => c.Parameters.AddWithValue("@k", key), ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.InboxDuplicates, ("consumer", Consumer + "-order")));
    }

    private static InboundMessage Inbound(JsonElement result) =>
        new(result.GetProperty("eventId").GetGuid(), RabbitMqPublisher.PrintResultKey, result.GetRawText(), false);

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("El consumidor no recibió las tres entregas en 30 s.");
            }

            await Task.Delay(50, ct);
        }
    }
}
