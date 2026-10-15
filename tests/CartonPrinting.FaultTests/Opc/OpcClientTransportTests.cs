using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;
using Opc.Ua;

namespace CartonPrinting.FaultTests.Opc;

/// <summary>
/// Cliente OPC UA real contra el PLC simulado: primera notificación como READ, entregas en vivo como
/// SUBSCRIPTION, calidad no Good, bit Overflow, interrupción con reenvío (REPUBLISH) y reinicio del
/// servidor con lectura del registro actual (READ) y de la FIFO (PLC_FIFO). Base de T11 y T45.
/// </summary>
[Trait("Spec", "T45")]
public sealed class OpcClientTransportTests
{
    private static LatchedRecord Record(ulong cycle) => new()
    {
        PlcEpoch = 42,
        CycleId = cycle,
        DetectionTimestamp = DateTime.UtcNow,
        WorkOrderId = "WO-2026-4815",
    };

    [Fact]
    public async Task First_notification_is_read_and_later_live_changes_are_subscription()
    {
        await using var h = await OpcHarness.StartAsync("OPC-A", new EdgeClock(TimeProvider.System));
        var first = await h.NextAsync();
        Assert.Equal(Transport.Read, first.Transport);

        h.Server.Nodes.Publish(Record(701));
        var live = await h.NextAsync();
        Assert.Equal(Transport.Subscription, live.Transport);
        Assert.True(live.StatusGood);
        Assert.False(live.Overflow);
        Assert.Equal(701UL, LatchedRecord.Parse(live.Value!).CycleId);
        Assert.True(h.Client.RevisedPublishingIntervalMs > 0);
    }

    [Fact]
    public async Task Non_good_quality_is_flagged_and_queue_overflow_sets_the_overflow_bit()
    {
        await using var h = await OpcHarness.StartAsync("OPC-B", new EdgeClock(TimeProvider.System), o =>
        {
            o.PublishingIntervalMs = 1000;
            o.QueueSize = 2;
        });
        await h.NextAsync();   // READ inicial

        h.Server.Nodes.PublishRaw(Record(702).ToJson(), StatusCodes.UncertainLastUsableValue);
        var uncertain = await h.NextAsync();
        Assert.False(uncertain.StatusGood);

        // Cinco cambios en un intervalo de publicación con cola de 2: el servidor descarta y marca Overflow.
        for (ulong c = 710; c < 715; c++)
        {
            h.Server.Nodes.Publish(Record(c));
            await Task.Delay(20);
        }

        var batch = await h.CollectUntilAsync(l => l.Count >= 2);
        Assert.Contains(batch, n => n.Overflow);
        Assert.All(batch, n => Assert.Equal(Transport.Subscription, n.Transport));
    }

    [Fact]
    public async Task Changes_during_a_frozen_link_arrive_as_republish_after_reconnect()
    {
        await using var h = await OpcHarness.StartAsync("OPC-C", new EdgeClock(TimeProvider.System));
        await h.NextAsync();   // READ inicial
        h.Server.Nodes.Publish(Record(720));
        Assert.Equal(Transport.Subscription, (await h.NextAsync()).Transport);

        h.Proxy.Freeze();
        await Task.Delay(300);
        h.Server.Nodes.Publish(Record(721));
        h.Server.Nodes.Publish(Record(722));
        await Task.Delay(2500);                  // el keep-alive (500 ms) falla
        h.Proxy.SeverAll();
        h.Proxy.Resume();

        var got = await h.CollectUntilAsync(l => l.Any(n => n.Value is not null && LatchedRecord.Parse(n.Value).CycleId == 722 && n.Transport == Transport.Read)
            && l.Count(n => n.Transport == Transport.Republish) >= 1, TimeSpan.FromSeconds(30));
        var after = got.Select(Dump).ToList();
        var events = string.Join(" | ", h.Client.ConnectionEvents);
        Assert.True(after.Any(n => n.Transport == Transport.Read && n.Cycle == 722), events);
        Assert.True(after.Any(n => n.Transport == Transport.Republish && n.Cycle == 722), events);
        Assert.DoesNotContain(after, n => n.Transport == Transport.Subscription && n.Cycle is 721 or 722);
        Assert.True(h.Client.ReconnectCount >= 1);
    }

    private sealed record RawNotificationDump(string Transport, ulong? Cycle, uint Seq, bool Good);

    private static RawNotificationDump Dump(CartonPrinting.OpcIngest.RawNotification n) =>
        new(n.Transport, string.IsNullOrEmpty(n.Value) ? null : LatchedRecord.Parse(n.Value).CycleId, n.SequenceNumber, n.StatusGood);

    [Fact]
    public async Task Server_restart_recreates_the_session_reads_the_current_value_and_the_fifo()
    {
        await using var h = await OpcHarness.StartAsync("OPC-D", new EdgeClock(TimeProvider.System), withFifo: true);
        await h.NextAsync();   // READ inicial

        await h.RestartServerAsync(nodes =>
        {
            nodes.SetFifo([Record(731), Record(732)]);
            nodes.Publish(Record(733));
        });

        var after = await h.CollectUntilAsync(l => l.Count(n => n.Transport == Transport.PlcFifo) >= 2, TimeSpan.FromSeconds(45));
        Assert.Contains(after, n => n.Transport == Transport.Read && LatchedRecord.Parse(n.Value!).CycleId == 733);
        Assert.Equal([731UL, 732UL], after.Where(n => n.Transport == Transport.PlcFifo).Select(n => LatchedRecord.Parse(n.Value!).CycleId));
        Assert.DoesNotContain(after, n => n.Transport == Transport.Subscription);
    }
}
