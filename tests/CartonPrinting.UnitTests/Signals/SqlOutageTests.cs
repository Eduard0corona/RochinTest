using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Time;
using Microsoft.Extensions.Time.Testing;

namespace CartonPrinting.UnitTests.Signals;

/// <summary>La caída de SQL Server compartida (Fase 7, revisiones: M-1, M-2, BAJA-2 y BAJA-11 de la verificación final).</summary>
public sealed class SqlOutageTests
{
    private static readonly TimeSpan Probe = TimeSpan.FromSeconds(1);

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
        Assert.True(outage.TryRecover(time.GetTimestamp()));
        Assert.False(outage.Declared);
        Assert.Equal(time.GetTimestamp(), outage.RecoveredAt);
    }

    [Fact]
    public void A_failure_reported_during_the_probe_or_a_pending_abandoned_operation_keeps_the_outage()
    {
        var time = new FakeTimeProvider();
        var outage = new SqlOutage(new EdgeClock(time));
        outage.Declare();
        time.Advance(TimeSpan.FromSeconds(2));
        var probeStartedAt = time.GetTimestamp();
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(outage.Declare());   // ya declarada, pero la falla queda anotada
        Assert.False(outage.TryRecover(probeStartedAt));
        Assert.True(outage.Declared);

        var hung = new TaskCompletionSource();
        outage.TrackAbandoned(hung.Task);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(outage.TryRecover(time.GetTimestamp()));
        hung.SetResult();
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(outage.TryRecover(time.GetTimestamp()));
    }

    [Fact]
    public void Each_relapse_soon_after_recovering_doubles_the_hold_up_to_64_times_and_a_late_one_resets_it()
    {
        var time = new FakeTimeProvider();
        var outage = new SqlOutage(new EdgeClock(time));
        outage.Declare();
        Assert.Equal(Probe, outage.RecoveryHold(Probe));
        for (var relapse = 1; relapse <= 8; relapse++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.True(outage.TryRecover(time.GetTimestamp()));
            time.Advance(TimeSpan.FromSeconds(5));
            outage.Declare();
            Assert.Equal(Probe * (1 << Math.Min(relapse, 6)), outage.RecoveryHold(Probe));
        }

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(outage.TryRecover(time.GetTimestamp()));
        time.Advance(SqlOutage.RelapseWindow + TimeSpan.FromSeconds(1));
        outage.Declare();
        Assert.Equal(Probe, outage.RecoveryHold(Probe));
    }

    [Fact]
    public void A_relapse_after_a_full_command_timeout_still_counts()
    {
        var time = new FakeTimeProvider();
        var outage = new SqlOutage(new EdgeClock(time));
        outage.Declare();
        time.Advance(TimeSpan.FromSeconds(1));
        outage.TryRecover(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(30.05));   // una operación que esperó todo el CommandTimeout de SqlClient
        outage.Declare();
        Assert.Equal(1, outage.Relapses);
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
