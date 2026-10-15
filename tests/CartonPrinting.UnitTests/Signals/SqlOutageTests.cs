using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Time;
using Microsoft.Extensions.Time.Testing;

namespace CartonPrinting.UnitTests.Signals;

/// <summary>La caída de SQL Server compartida (Fase 7, segunda revisión: M-1 y M-2).</summary>
public sealed class SqlOutageTests
{
    [Fact]
    public void Only_the_first_declaration_reports_the_transition_and_recovery_marks_the_time()
    {
        var time = new FakeTimeProvider();
        var outage = new SqlOutage(new EdgeClock(time));
        Assert.Equal(long.MinValue, outage.RecoveredAt);
        Assert.True(outage.Declare());
        Assert.False(outage.Declare());
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), outage.DeclaredFor);
        outage.Recover();
        Assert.False(outage.Declared);
        Assert.Equal(time.GetTimestamp(), outage.RecoveredAt);
    }

    [Fact]
    public void A_relapse_soon_after_recovering_doubles_the_wait_and_a_late_one_resets_it()
    {
        var time = new FakeTimeProvider();
        var outage = new SqlOutage(new EdgeClock(time));
        outage.Declare();
        outage.Recover();
        time.Advance(TimeSpan.FromSeconds(5));
        outage.Declare();
        Assert.Equal(1, outage.Relapses);
        outage.Recover();
        time.Advance(TimeSpan.FromSeconds(5));
        outage.Declare();
        Assert.Equal(2, outage.Relapses);
        outage.Recover();
        time.Advance(SqlOutage.RelapseWindow + TimeSpan.FromSeconds(1));
        outage.Declare();
        Assert.Equal(0, outage.Relapses);
    }

    [Fact]
    public async Task An_abandoned_operation_counts_until_it_finishes_even_if_it_fails()
    {
        var outage = new SqlOutage(new EdgeClock(new FakeTimeProvider()));
        var hung = new TaskCompletionSource();
        outage.TrackAbandoned(hung.Task);
        outage.TrackAbandoned(Task.FromException(new InvalidOperationException("ya falló")));
        Assert.Equal(1, outage.AbandonedOperations);
        hung.SetException(new TimeoutException("respondió tarde"));
        await Task.Yield();
        Assert.Equal(0, outage.AbandonedOperations);
    }
}
