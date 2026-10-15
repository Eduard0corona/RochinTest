using System.Collections.Concurrent;
using System.Diagnostics;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Testing;

namespace CartonPrinting.IntegrationTests.Observability;

/// <summary>
/// Definición de DONE: trazas con correlationId. El registro y el claim emiten una actividad con
/// idempotencyKey, correlationId (= DetectionId) y printJobId.
/// </summary>
public sealed class TracingTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("traces", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Register_and_claim_emit_activities_with_correlation_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CartonTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var s = await StationScenario.CreateAsync(_db, ct: ct);
        var record = s.Cycle(7);
        var registered = await s.RegisterAsync(record, ct: ct);
        var claim = (await s.Claims.TryClaimAsync(registered.PrintJobId!.Value, s.Lease, ct))!;
        Assert.True(claim.Claimed);

        var key = record.Identity.IdempotencyKey;
        var register = Assert.Single(stopped, a => a.OperationName == "carton.register" && (string?)a.GetTagItem(CartonTelemetry.Tags.IdempotencyKey) == key);
        Assert.Equal(registered.DetectionId, register.GetTagItem(CartonTelemetry.Tags.CorrelationId));
        Assert.Equal(registered.PrintJobId, register.GetTagItem(CartonTelemetry.Tags.PrintJobId));

        var claimed = Assert.Single(stopped, a => a.OperationName == "carton.claim" && (string?)a.GetTagItem(CartonTelemetry.Tags.IdempotencyKey) == key);
        Assert.Equal(registered.DetectionId, claimed.GetTagItem(CartonTelemetry.Tags.CorrelationId));
        Assert.Equal(claim.AttemptId, claimed.GetTagItem(CartonTelemetry.Tags.AttemptId));
    }
}
