using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T26 · Rebote del sensor. Con MinCycleIntervalMs y DetectionTimestamp, dos ciclos separados por menos
/// del intervalo: la segunda detección queda BLOCKED con IMPLAUSIBLE_RATE, se publica su PrintJobResult
/// BLOCKED y no hay job (cero bytes). Sin DetectionTimestamp la regla queda desactivada.
/// </summary>
[Trait("Spec", "T26")]
public sealed class SensorBounceTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t26", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Second_cycle_faster_than_min_interval_is_blocked_with_implausible_rate()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, r => r with { MinCycleIntervalMs = 200 }, ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        var t = s.Clock.UtcNow.AddMilliseconds(-300);

        var first = await ingest.ProcessRecordAsync(s.Cycle(100, detectionTimestamp: t), ct);
        Assert.Equal(DetectionStatus.JobCreated, first.Result!.FinalStatus);
        var bounce = await ingest.ProcessRecordAsync(s.Cycle(101, detectionTimestamp: t.AddMilliseconds(150)), ct);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.ImplausibleRate), (bounce.Result!.FinalStatus, bounce.Result.FinalReason));
        Assert.Null(bounce.Result.PrintJobId);

        var result = Assert.Single(await s.ResultsAsync(ct: ct));
        Assert.Equal(("BLOCKED", "IMPLAUSIBLE_RATE", "101"),
            (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString(), result.GetProperty("cycleId").GetString()));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.ImplausibleRate)));

        // El ciclo siguiente, a un intervalo plausible del rebote, vuelve a ser normal.
        var next = await ingest.ProcessRecordAsync(s.Cycle(102, detectionTimestamp: t.AddMilliseconds(380)), ct);
        Assert.Equal(DetectionStatus.JobCreated, next.Result!.FinalStatus);
    }

    [Fact]
    public async Task Without_detection_timestamp_the_rule_is_disabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, r => r with
        {
            DeadlineReference = DeadlineReference.GatewayReceipt, ClockSkewToleranceMs = null, MaxUpstreamLatencyMs = 40, MinCycleIntervalMs = 200,
        }, ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);

        foreach (var cycle in new ulong[] { 100, 101 })
        {
            var record = s.Cycle(cycle) with { DetectionTimestamp = null, OpcSourceTimestamp = null };
            Assert.Equal(DetectionStatus.JobCreated, (await ingest.ProcessRecordAsync(record, ct)).Result!.FinalStatus);
        }

        Assert.Equal(0, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.ImplausibleRate)));
    }
}
