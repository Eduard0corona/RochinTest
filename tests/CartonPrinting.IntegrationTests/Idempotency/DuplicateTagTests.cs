using System.Collections.Concurrent;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Testing;

namespace CartonPrinting.IntegrationTests.Idempotency;

/// <summary>
/// T01 · Tag duplicado. 100 entregas del mismo registro (notificaciones repetidas, Republish y
/// relectura) desde 10 consumidores concurrentes: 1 detección JOB_CREATED, 1 PrintJob, 1 intento,
/// 1 CartonDetected, un PrintJobResult por transición publicable, como máximo una transmisión;
/// duplicate_detection_total = 99 por transporte y ningún error de unicidad sin manejar.
/// En la Fase 1 el "sender" es un arnés que reclama y escribe a una impresora en memoria por cada
/// señal recibida; el sender real y el simulador TCP lo repiten en la Fase 4.
/// </summary>
[Trait("Spec", "T01")]
public sealed class DuplicateTagTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t01", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Hundred_deliveries_from_ten_consumers_produce_one_detection_one_job_and_at_most_one_transmission()
    {
        var ct = TestContext.Current.CancellationToken;
        // Nivel NONE: el job termina en TRANSMITTED y su resultado se publica una vez (J05).
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.None, EvidenceWindowMs = null }, ct);
        var latched = s.Cycle(918273);
        string[] transports = [Transport.Subscription, Transport.Republish, Transport.Read];
        var printer = new CountingPrinter();
        var signals = new ConcurrentQueue<Guid>();
        var results = new ConcurrentBag<RegistrationResult>();

        // 10 consumidores, 10 entregas cada uno. Una retransmisión legítima cambia transporte y
        // recepción, nunca los valores latched: la huella es la misma.
        var consumers = Enumerable.Range(0, 10).Select(c => Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                var transport = transports[((c * 10) + i) % transports.Length];
                var delivery = latched with { Transport = transport, ReceivedAt = latched.ReceivedAt.AddMilliseconds(i) };
                var result = await s.RegisterAsync(delivery, ct: ct);
                results.Add(result);
                // Cada entrega dispara una señal al sender (como un consumidor ingenuo): el claim decide.
                signals.Enqueue(result.PrintJobId ?? result.DetectionId);
            }
        }, ct)).ToArray();
        await Task.WhenAll(consumers);

        // Arnés del sender: por cada señal intenta reclamar el job; solo escribe si obtuvo el claim.
        var job = results.Single(r => r.Outcome == RegistrationOutcome.Registered).PrintJobId!.Value;
        await Task.WhenAll(signals.Select(_ => Task.Run(async () =>
        {
            var claim = await s.Claims.TryClaimAsync(job, s.Lease, ct);
            if (claim is { Claimed: true })
            {
                printer.Write(claim.Context.Job.Payload);
                await s.TransmitAsync(claim.AttemptId, ct);
            }
        }, ct)));

        // Detección y resultados de registro.
        Assert.Equal(100, results.Count);
        Assert.Single(results, r => r.Outcome == RegistrationOutcome.Registered);
        Assert.Equal(99, results.Count(r => r.Outcome == RegistrationOutcome.Duplicate));
        Assert.DoesNotContain(results, r => r.Outcome == RegistrationOutcome.Collision);
        var detection = (await s.DetectionAsync(918273, ct))!;
        Assert.Equal(DetectionStatus.JobCreated, detection.Status);

        // Conteos exactos en SQL.
        Assert.Equal(1, await s.CountAsync("FROM dbo.CartonDetection WHERE StationId = @s", c => c.Parameters.AddWithValue("@s", s.StationId), ct));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @s", c => c.Parameters.AddWithValue("@s", s.StationId), ct));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintAttempt AS pa INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = pa.PrintJobId WHERE pj.StationId = @s",
            c => c.Parameters.AddWithValue("@s", s.StationId), ct));
        var jobRow = await s.JobAsync(job, ct);
        Assert.Equal(JobStatus.Transmitted, jobRow.Status);

        // Eventos: 1 CartonDetected y 1 PrintJobResult (TRANSMITTED final con nivel NONE).
        var outbox = await s.OutboxAsync(ct);
        Assert.Single(outbox, e => e.Type == "CartonDetected");
        var result = Assert.Single(await s.ResultsAsync(ct: ct));
        Assert.Equal("TRANSMITTED", result.GetProperty("outcome").GetString());
        Assert.Equal(1, result.GetProperty("resultVersion").GetInt32());

        // Efecto físico en el simulador: una transmisión, ningún duplicado.
        Assert.Equal(1, printer.Transmissions);
        Assert.Equal(0, printer.DuplicatePhysicalTransmissions);

        // Métricas: 99 duplicados repartidos por transporte (el transporte ganador registró la detección).
        Assert.Equal(99, s.Metrics.Total(MetricNames.DuplicateDetection));
        foreach (var transport in transports)
        {
            var deliveries = Enumerable.Range(0, 100).Count(k => transports[k % transports.Length] == transport);
            var expected = deliveries - (detection.Transport == transport ? 1 : 0);
            Assert.Equal(expected, s.Metrics.Total(MetricNames.DuplicateDetection, ("transport", transport)));
        }

        Assert.Equal(99, s.Metrics.Total(MetricNames.ClaimConflicts));
    }
}
