using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Outbox;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// T08 · RabbitMQ caído. Con carga nominal (una caja por segundo: TX-A, envío a la impresora simulada y evidencia PRINTED),
/// el broker deja de estar disponible para el EdgeHost durante 10 minutos: un proxy TCP entre el Outbox Relay y RabbitMQ
/// corta las conexiones y no acepta nuevas. Detecciones, jobs e impresión siguen igual; las filas del Outbox quedan
/// pendientes y outbox_pending_age_seconds crece. Al volver, todos los eventos se publican con su EventId, en orden de
/// OutboxSequence, y el consumidor (con InboxMessage) los aplica una sola vez: cero perdidos, cero efectos duplicados.
/// RabbitMQ y SQL Server reales; el consumidor se conecta al broker directamente.
/// </summary>
public sealed class BrokerOutageTests(SqlServerFixture sql, RabbitMqFixture rabbit) : IAsyncLifetime, IClassFixture<RabbitMqFixture>
{
    private const string Consumer = "mes-t08";
    private static readonly TimeSpan Before = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Outage = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan After = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BoxInterval = TimeSpan.FromSeconds(1);

    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _db = await sql.CreateDatabaseAsync("t08", ct);

        // Proyección del consumidor de prueba (fuera del modelo de la especificación): una fila por efecto, en orden de llegada.
        await using var conn = new SqlConnection(_db.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            EXEC (N'CREATE SCHEMA testconsumer');
            CREATE TABLE testconsumer.Arrival (
                ArrivalSeq BIGINT IDENTITY (1, 1) NOT NULL PRIMARY KEY, EventId UNIQUEIDENTIFIER NOT NULL, RoutingKey VARCHAR(64) NOT NULL);
            GRANT SELECT, INSERT ON SCHEMA::testconsumer TO [{TestDatabase.AppLogin}];
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task RecordArrivalAsync(SqlConnection conn, SqlTransaction tx, InboundMessage message, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("INSERT INTO testconsumer.Arrival (EventId, RoutingKey) VALUES (@e, @k)", conn, tx);
        cmd.Parameters.AddWithValue("@e", message.EventId);
        cmd.Parameters.AddWithValue("@k", message.RoutingKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    [Trait("Spec", "T08")]
    public async Task Ten_minutes_without_rabbitmq_change_nothing_in_printing_and_lose_no_event()
    {
        // Plazo de 5 s: la prueba corre en paralelo con el resto de la suite y no mide si el plazo nominal alcanza (eso es T12).
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 5_000 }, Ct, TimeProvider.System);
        var broker = new Uri(rabbit.Uri);
        await using var proxy = new TcpProxy(broker.Host, broker.Port).Start();
        var viaProxy = new UriBuilder(broker) { Host = "127.0.0.1", Port = proxy.Port }.Uri.ToString();
        var exchange = "carton.events.t08";

        var consumer = new InboxConsumer(s.Sql, Consumer, RecordArrivalAsync, s.Metrics);
        await using var subscription = await consumer.SubscribeAsync(new RabbitMqOptions { Uri = rabbit.Uri, Exchange = exchange }, "mes.t08",
            [RabbitMqPublisher.CartonDetectedKey, RabbitMqPublisher.PrintResultKey], Ct);
        await using var publisher = new RabbitMqPublisher(new RabbitMqOptions { Uri = viaProxy, Exchange = exchange });
        var relay = new OutboxRelay(s.Sql, publisher, s.Clock, s.Metrics, new OutboxRelayOptions());

        await using var rig = PrintRig.Create(s);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sender = rig.Sender.RunAsync(TimeSpan.FromMilliseconds(20), TimeProvider.System, null, stop.Token);
        var lease = s.KeepLeaseAsync(stop.Token);
        await relay.StartAsync(Ct);

        ulong cycle = 1000;
        long publishedAtCut;
        ulong firstInOutage, lastInOutage;
        try
        {
            // Carga nominal con el broker disponible.
            cycle = await BoxesAsync(s, cycle, Before);
            await WaitAsync(async () => await PendingAsync(s) == 0, "Outbox al día antes del corte");

            // RabbitMQ deja de estar disponible 10 minutos; la carga sigue.
            proxy.AcceptNew = false;
            proxy.SeverAll();
            publishedAtCut = await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxEvent WHERE PublishedAt IS NOT NULL", null, Ct);
            firstInOutage = cycle;
            cycle = await BoxesAsync(s, cycle, Outage);
            lastInOutage = cycle - 1;
            await WaitAsync(async () => await JobsConfirmedAsync(s, firstInOutage, lastInOutage), "los jobs de la caída, CONFIRMED",
                describe: () => DescribeAsync(s, sender));

            // Al final de la caída: nada se publicó, el pendiente tiene unos 10 minutos y crece con la carga.
            Assert.Equal(publishedAtCut, await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxEvent WHERE PublishedAt IS NOT NULL", null, Ct));
            var pending = await PendingAsync(s);
            // Cada caja deja al menos su CartonDetected y su PrintJobResult CONFIRMED.
            Assert.True(pending >= 2 * (int)(lastInOutage - firstInOutage + 1), $"pendientes al final de la caída: {pending}");
            await WaitAsync(() => Task.FromResult(s.Metrics.Gauge(MetricNames.OutboxPendingAgeSeconds) >= Outage.TotalSeconds - 60),
                "outbox_pending_age_seconds cerca de la duración de la caída");

            // RabbitMQ vuelve; la carga sigue.
            proxy.AcceptNew = true;
            cycle = await BoxesAsync(s, cycle, After);
            await WaitAsync(async () => await PendingAsync(s) == 0, "Outbox publicado al volver RabbitMQ", TimeSpan.FromMinutes(3));
            var total = await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxEvent", null, Ct);
            await WaitAsync(async () => await s.ScalarAsync<int>("SELECT COUNT(*) FROM testconsumer.Arrival", null, Ct) >= total,
                "todos los eventos aplicados por el consumidor");
        }
        finally
        {
            await stop.CancelAsync();
            await sender.ContinueWith(_ => { }, TaskScheduler.Default);
            await lease;
            await relay.StopAsync(CancellationToken.None);
        }

        // Detecciones y jobs avanzan igual: cada ciclo tiene su job CONFIRMED y una sola etiqueta; ninguno vence.
        var boxes = (int)(cycle - 1000);
        Assert.Equal(boxes, await s.CountAsync("FROM dbo.PrintJob WHERE Status = 'CONFIRMED'", null, Ct));
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE Status <> 'CONFIRMED'", null, Ct));
        Assert.Equal(boxes, rig.Printer.Received.Count);
        Assert.Equal(boxes, rig.Printer.Received.Select(r => r.Serial).Distinct().Count());

        // Latencia de impresión (recepción del ciclo a inicio de escritura) antes y durante la caída. La primera caja abre la
        // conexión con la impresora y compila el código: queda fuera de la muestra.
        var before = await WriteLatenciesAsync(s, 1001, firstInOutage - 1);
        var during = await WriteLatenciesAsync(s, firstInOutage, lastInOutage);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"T08 latencia recepción→escritura (ms): antes p50={P(before, 0.5):F0} p95={P(before, 0.95):F0} máx={before.Max():F0}; " +
            $"durante p50={P(during, 0.5):F0} p95={P(during, 0.95):F0} máx={during.Max():F0}; cajas={boxes}");
        Assert.True(P(during, 0.95) <= Math.Max(2 * P(before, 0.95), P(before, 0.95) + 250),
            $"p95 durante la caída {P(during, 0.95):F0} ms frente a {P(before, 0.95):F0} ms antes");

        // Cero perdidos, mismo EventId, en orden de OutboxSequence y sin efectos duplicados.
        var arrivals = await s.QueryAsync("""
            SELECT a.ArrivalSeq, o.OutboxSequence FROM testconsumer.Arrival AS a
            LEFT JOIN dbo.OutboxEvent AS o ON o.EventId = a.EventId ORDER BY a.ArrivalSeq
            """, null, r => (Arrival: r.GetInt64(0), Sequence: r.IsDBNull(1) ? (long?)null : r.GetInt64(1)), Ct);
        Assert.Equal(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxEvent", null, Ct), arrivals.Count);
        Assert.DoesNotContain(arrivals, a => a.Sequence is null);
        Assert.Equal(arrivals.Count, arrivals.Select(a => a.Sequence).Distinct().Count());
        Assert.Equal(arrivals.Select(a => a.Sequence).Order(), arrivals.Select(a => a.Sequence));
        Assert.Equal(0, await s.CountAsync("""
            FROM dbo.OutboxEvent AS o WHERE NOT EXISTS (
                SELECT 1 FROM dbo.InboxMessage AS i WHERE i.ConsumerName = @c AND i.EventId = o.EventId)
            """, c => c.Parameters.AddWithValue("@c", Consumer), Ct));
        Assert.True(s.Metrics.Gauge(MetricNames.OutboxPendingAgeSeconds) < 30);
    }

    /// <summary>Una caja por intervalo durante <paramref name="duration"/>: el ciclo llega por suscripción y TX-A lo registra.</summary>
    private static async Task<ulong> BoxesAsync(StationScenario s, ulong cycle, TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        var next = DateTime.UtcNow;
        while (DateTime.UtcNow < end)
        {
            var result = await s.RegisterAsync(s.Cycle(cycle), ct: Ct);
            Assert.Equal(DetectionStatus.JobCreated, result.FinalStatus);
            cycle++;
            next += BoxInterval;
            var wait = next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, Ct);
            }
        }

        return cycle;
    }

    private static Task<int> PendingAsync(StationScenario s) =>
        s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.OutboxEvent WHERE PublishedAt IS NULL", null, Ct);

    private static async Task<bool> JobsConfirmedAsync(StationScenario s, ulong from, ulong to) =>
        await s.CountAsync("FROM dbo.PrintJob WHERE CycleId BETWEEN @a AND @b AND Status = 'CONFIRMED'",
            c => { c.Parameters.AddWithValue("@a", (decimal)from); c.Parameters.AddWithValue("@b", (decimal)to); }, Ct) == (int)(to - from + 1);

    private static async Task<List<double>> WriteLatenciesAsync(StationScenario s, ulong from, ulong to) =>
        [.. await s.QueryAsync("""
            SELECT DATEDIFF_BIG(MILLISECOND, cd.ReceivedAt, pa.WriteStartedAt)
            FROM dbo.PrintJob AS pj
            INNER JOIN dbo.CartonDetection AS cd ON cd.DetectionId = pj.DetectionId
            INNER JOIN dbo.PrintAttempt AS pa ON pa.PrintJobId = pj.PrintJobId
            WHERE pj.CycleId BETWEEN @a AND @b AND pa.WriteStartedAt IS NOT NULL
            """, c => { c.Parameters.AddWithValue("@a", (decimal)from); c.Parameters.AddWithValue("@b", (decimal)to); },
            r => (double)r.GetInt64(0), Ct)];

    private static double P(IReadOnlyList<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null, Func<Task<string>>? describe = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"No se cumplió a tiempo: {what}. {(describe is null ? string.Empty : await describe())}");
            }

            await Task.Delay(250, Ct);
        }
    }

    /// <summary>Estado de los jobs y del sender, para el mensaje de una espera que no se cumplió.</summary>
    private static async Task<string> DescribeAsync(StationScenario s, Task sender)
    {
        var statuses = await s.QueryAsync("SELECT Status, ISNULL(Reason, '-'), COUNT(*), MIN(CycleId) FROM dbo.PrintJob GROUP BY Status, Reason", null,
            r => $"{r.GetString(0)}/{r.GetString(1)}={r.GetInt32(2)} (desde {r.GetDecimal(3)})", Ct);
        return $"Jobs: {string.Join(", ", statuses)}. Sender: {sender.Status}{(sender.Exception is { } e ? " " + e.GetBaseException().Message : string.Empty)}.";
    }
}
