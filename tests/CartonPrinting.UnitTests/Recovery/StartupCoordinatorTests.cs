using CartonPrinting.Recovery;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Recovery;

/// <summary>Arranque ordenado (ESTRATEGIA_RECUPERACION.md, sección 1): etapas en orden, reintento y falla cerrada.</summary>
public sealed class StartupCoordinatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stages_run_in_order_whatever_the_registration_order()
    {
        var startup = new StartupCoordinator(TimeProvider.System);
        startup.Add(StartupStage.LabelingReady, "latido", _ => Task.CompletedTask);
        startup.Add(StartupStage.Subscribed, "opc", _ => Task.CompletedTask);
        startup.Add(StartupStage.Swept, "barrido", _ => Task.CompletedTask);
        startup.Add(StartupStage.SendingRecovered, "tx-r0 A", _ => Task.CompletedTask);
        startup.Add(StartupStage.Ownership, "lease A", _ => Task.CompletedTask);
        startup.Add(StartupStage.SendingRecovered, "tx-r0 B", _ => Task.CompletedTask);
        startup.Add(StartupStage.StateLoaded, "estado", _ => Task.CompletedTask);

        await startup.RunAsync(Ct);
        Assert.Equal(
            ["Ownership:lease A", "SendingRecovered:tx-r0 A", "SendingRecovered:tx-r0 B", "StateLoaded:estado", "Swept:barrido", "Subscribed:opc",
             "LabelingReady:latido"],
            startup.Log);
        Assert.True(startup.WhenReached(StartupStage.LabelingReady).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_failing_step_is_retried_without_repeating_finished_steps_and_later_stages_wait()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        var startup = new StartupCoordinator(time);
        var calls = new Dictionary<string, int> { ["tx-r0 A"] = 0, ["tx-r0 B"] = 0, ["opc"] = 0 };
        var failures = 2;
        startup.Add(StartupStage.SendingRecovered, "tx-r0 A", _ => { calls["tx-r0 A"]++; return Task.CompletedTask; });
        startup.Add(StartupStage.SendingRecovered, "tx-r0 B", _ =>
        {
            calls["tx-r0 B"]++;
            return failures-- > 0 ? throw new InvalidOperationException("SQL caído") : Task.CompletedTask;
        });
        startup.Add(StartupStage.Subscribed, "opc", _ => { calls["opc"]++; return Task.CompletedTask; });

        var run = startup.RunAsync(Ct);
        for (var i = 0; i < 50 && !run.IsCompleted; i++)
        {
            Assert.False(startup.WhenReached(StartupStage.SendingRecovered).IsCompleted || calls["opc"] > 0);
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, Ct);
        }

        await run;
        Assert.Equal((1, 3, 1), (calls["tx-r0 A"], calls["tx-r0 B"], calls["opc"]));
        Assert.Equal(["SendingRecovered:tx-r0 A", "SendingRecovered:tx-r0 B", "Subscribed:opc"], startup.Log);
    }

    [Fact]
    public async Task A_step_registered_after_the_start_is_rejected()
    {
        var startup = new StartupCoordinator(TimeProvider.System);
        await startup.RunAsync(Ct);
        Assert.Throws<InvalidOperationException>(() => startup.Add(StartupStage.Swept, "tarde", _ => Task.CompletedTask));
    }
}
