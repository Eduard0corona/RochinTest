using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.OpcIngest;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T13 · Snapshot del PLC incoherente, de punta a punta por OPC UA. El PLC simulado publica el CycleId de un
/// ciclo con la orden de otro (el eco de cierre del registro delata la mezcla): detección BLOCKED con
/// INCOHERENT_SNAPSHOT, CartonDetected y PrintJobResult BLOCKED sin job. Variante: con PLC_TIMESTAMP, un
/// registro sin DetectionTimestamp queda BLOCKED con INCOHERENT_SNAPSHOT y DeadlineAt = ReceivedAt.
/// </summary>
[Trait("Spec", "T13")]
public sealed class IncoherentSnapshotTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t13", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Mixed_fields_and_a_missing_plc_timestamp_are_blocked_as_incoherent_without_a_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        await s.AddSnapshotAsync("R1", "SKU-B", "LOT-B", ct, "WO-B");
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        await using var h = await OpcHarness.StartAsync(s.StationId, s.Clock);
        await ingest.PumpAsync(h, l => l.Count >= 1);   // valor inicial vacío

        LatchedRecord Record(ulong cycle, string workOrder, DateTime? ts) => new() { PlcEpoch = s.Epoch, CycleId = cycle, WorkOrderId = workOrder, DetectionTimestamp = ts };
        var now = s.Clock.UtcNow;
        h.Server.Nodes.Publish(Record(500, StationScenario.WorkOrder, now.AddMilliseconds(-40)));
        var normal = await ingest.PumpAsync(h, l => l.Any(o => o.Kind == IngestKind.Registered));
        Assert.Equal(DetectionStatus.JobCreated, normal[^1].Result!.FinalStatus);

        // El ciclo 501 con la orden y el timestamp del 502: el eco de cierre es 502.
        h.Server.Nodes.Publish(LatchedRecord.Mixed(Record(501, StationScenario.WorkOrder, now.AddMilliseconds(-30)), Record(502, "WO-B", now.AddMilliseconds(-20))));
        var mixed = (await ingest.PumpAsync(h, l => l.Any(o => o.Kind == IngestKind.Registered)))[^1];
        Assert.False(mixed.Record!.Coherent);
        Assert.Equal((501UL, "WO-B"), (mixed.Record.CycleId, mixed.Record.WorkOrderId));
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot), (mixed.Result!.FinalStatus, mixed.Result.FinalReason));
        Assert.Null(mixed.Result.PrintJobId);

        // Variante: PLC_TIMESTAMP y un registro sin DetectionTimestamp.
        h.Server.Nodes.Publish(Record(502, StationScenario.WorkOrder, null));
        var noTs = (await ingest.PumpAsync(h, l => l.Any(o => o.Kind == IngestKind.Registered)))[^1];
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot), (noTs.Result!.FinalStatus, noTs.Result.FinalReason));
        var d = (await s.DetectionAsync(502, ct))!;
        Assert.Equal((d.ReceivedAt, (DateTime?)null), (d.DeadlineAt, d.DetectionTimestamp));

        // Ningún job con datos mezclados; eventos BLOCKED sin job; métrica por motivo.
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        var results = await s.ResultsAsync(ct: ct);
        Assert.Equal(["501", "502"], results.Select(r => r.GetProperty("cycleId").GetString()).Order().ToList());
        Assert.All(results, r => Assert.Equal(("BLOCKED", "INCOHERENT_SNAPSHOT"), (r.GetProperty("outcome").GetString(), r.GetProperty("reason").GetString())));
        Assert.Equal(3, (await s.OutboxAsync(ct)).Count(e => e.Type == "CartonDetected"));
        Assert.Equal(2, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.IncoherentSnapshot)));
    }
}
