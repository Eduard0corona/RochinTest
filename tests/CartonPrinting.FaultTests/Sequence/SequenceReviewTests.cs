using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// Regresiones de la revisión independiente de la Fase 2 contra SQL Server real: época confirmada que llega a
/// la ingesta en ejecución, dos instancias en la misma estación, registros incoherentes que no alimentan el
/// estado, canal saturado y los bloqueos RESET_SUSPECTED y AMBIGUOUS_DECREASE.
/// </summary>
public sealed class SequenceReviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f2rev", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(StationScenario S, StationIngestService Ingest)> StationAsync(TestDatabase db, Func<StationRevision, StationRevision>? configure = null)
    {
        var s = await StationScenario.CreateAsync(db, configure, Ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(Ct);
        return (s, ingest);
    }

    /// <summary>M1: una época confirmada por una persona llega a la ingesta en ejecución sin reiniciarla ni reingresar.</summary>
    [Fact]
    [Trait("Spec", "T25")]
    public async Task A_confirmed_epoch_reaches_the_running_ingest()
    {
        var (s, ingest) = await StationAsync(_db, r => r with { EpochChangePolicy = "REQUIRE_CONFIRMATION" });
        await ingest.ProcessRecordAsync(s.Cycle(100), Ct);
        Assert.Equal(IngestKind.Retained, (await ingest.ProcessRecordAsync(s.Cycle(1, epoch: 43), Ct)).Kind);

        await s.Txm.ConfirmEpochAsync(StationScenario.SiteId, s.StationId, 43, 1, s.Revision.ConfigRevisionId, "supervisor.ramos", "OT confirma", s.Clock.UtcNow, Ct);
        await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "Época confirmada", s.Clock.UtcNow, Ct);
        var live = await ingest.ProcessRecordAsync(s.Cycle(2, epoch: 43), Ct);
        Assert.Equal(IngestKind.Registered, live.Kind);
        Assert.Equal(DetectionStatus.JobCreated, live.Result!.FinalStatus);
        Assert.True((await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State.LabelingEnabled);
    }

    /// <summary>M2: con dos instancias, los registros de la otra son duplicados que mueven la marca alta; ningún hueco espurio.</summary>
    [Fact]
    public async Task Two_instances_on_one_station_do_not_open_spurious_gaps()
    {
        var (s, a) = await StationAsync(_db);
        var b = s.Ingest();
        await b.ReloadAsync(Ct);
        await a.ProcessRecordAsync(s.Cycle(700), Ct);
        Assert.Equal(RegistrationOutcome.Duplicate, (await b.ProcessRecordAsync(s.Cycle(700), Ct)).Result!.Outcome);

        foreach (var cycle in new ulong[] { 701, 702, 703, 704 })
        {
            var (first, second) = cycle % 2 == 0 ? (a, b) : (b, a);
            s.AdvanceMs(300);
            var record = s.Cycle(cycle);
            Assert.Equal(SequenceCase.InOrder, (await first.ProcessRecordAsync(record, Ct)).Case);
            var dup = await second.ProcessRecordAsync(record, Ct);
            Assert.Equal(RegistrationOutcome.Duplicate, dup.Result!.Outcome);
        }

        Assert.Equal(0, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.Equal((704UL, 704UL), (a.Classifier.Current!.HighWater!.Value, b.Classifier.Current!.HighWater!.Value));
    }

    /// <summary>B1: un registro incoherente no acepta épocas ni mueve la marca alta ni abre huecos.</summary>
    [Fact]
    [Trait("Spec", "T13")]
    public async Task An_incoherent_record_does_not_feed_the_sequence_state()
    {
        var (s, ingest) = await StationAsync(_db);
        await ingest.ProcessRecordAsync(s.Cycle(100), Ct);
        var mixed = await ingest.ProcessRecordAsync(s.Cycle(105) with { Coherent = false }, Ct);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot), (mixed.Result!.FinalStatus, mixed.Result.FinalReason));
        Assert.Equal(100UL, ingest.Classifier.Current!.HighWater);
        Assert.Equal(SequenceCase.InOrder, (await ingest.ProcessRecordAsync(s.Cycle(101), Ct)).Case);
        Assert.Equal(0, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));

        var unknownEpoch = await ingest.ProcessRecordAsync(s.Cycle(1, epoch: 43) with { Coherent = false }, Ct);
        Assert.Equal(IngestKind.Retained, unknownEpoch.Kind);
        Assert.DoesNotContain(await s.Epochs.ListAsync(StationScenario.SiteId, s.StationId, Ct), e => e.Epoch == 43);
    }

    /// <summary>M4: con el canal de la ingesta saturado, LabelingReady baja.</summary>
    [Fact]
    public async Task A_saturated_channel_lowers_labeling_ready()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var saturated = false;
        var sink = new IngestScenario.RecordingSink();
        var heartbeat = new LabelingReadyHeartbeat(s.Settings, s.Stations, sink, s.Metrics, overloaded: () => saturated);
        Assert.True(await heartbeat.BeatAsync(Ct));
        saturated = true;
        Assert.False(await heartbeat.BeatAsync(Ct));
        Assert.Equal([true, false], sink.Writes);
    }

    /// <summary>RESET_SUSPECTED y AMBIGUOUS_DECREASE repetido bloquean la estación y dejan la detección BLOCKED.</summary>
    [Fact]
    [Trait("Spec", "T45")]
    public async Task Reset_suspected_and_repeated_ambiguous_decrease_block_the_station()
    {
        var (s, ingest) = await StationAsync(_db);
        await ingest.ProcessRecordAsync(s.Cycle(2000), Ct);
        s.AdvanceMs(500);
        var reset = await ingest.ProcessRecordAsync(s.Cycle(1500), Ct);   // clave nueva, menor, timestamp posterior
        Assert.Equal((SequenceCase.ResetSuspected, DetectionStatus.Blocked, DetectionReason.SequenceAnomaly),
            (reset.Case, reset.Result!.FinalStatus, reset.Result.FinalReason));
        var state = (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State;
        Assert.Equal((BlockReason.ResetSuspected, reset.Result.AnomalyId), (state.BlockReason, state.BlockedByAnomalyId));

        var (g, gateway) = await StationAsync(_db, r => r with
        {
            DeadlineReference = DeadlineReference.GatewayReceipt, ClockSkewToleranceMs = null, MaxUpstreamLatencyMs = 40,
        });
        await gateway.ProcessRecordAsync(g.Cycle(700) with { DetectionTimestamp = null }, Ct);
        var warning = await gateway.ProcessRecordAsync(g.Cycle(650) with { DetectionTimestamp = null }, Ct);
        Assert.Equal("WARNING", await g.ScalarAsync<string>("SELECT Severity FROM dbo.SequenceAnomaly WHERE AnomalyId = @id",
            c => c.Parameters.AddWithValue("@id", warning.Result!.AnomalyId!.Value), Ct));
        Assert.True((await g.Stations.GetActiveAsync(StationScenario.SiteId, g.StationId, Ct))!.Value.State.LabelingEnabled);
        var blocking = await gateway.ProcessRecordAsync(g.Cycle(651) with { DetectionTimestamp = null }, Ct);
        Assert.Equal("BLOCKING", await g.ScalarAsync<string>("SELECT Severity FROM dbo.SequenceAnomaly WHERE AnomalyId = @id",
            c => c.Parameters.AddWithValue("@id", blocking.Result!.AnomalyId!.Value), Ct));
        Assert.Equal(BlockReason.AmbiguousDecrease, (await g.Stations.GetActiveAsync(StationScenario.SiteId, g.StationId, Ct))!.Value.State.BlockReason);
    }
}
