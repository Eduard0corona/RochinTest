using System.Threading.Channels;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// Regresiones de la revisión independiente de la Fase 2 contra SQL Server real: época confirmada que llega a
/// la ingesta en ejecución, dos instancias en la misma estación, registros incoherentes con la misma regla en
/// memoria y al recargar, canal saturado, reingresos concurrentes y los bloqueos RESET_SUSPECTED y AMBIGUOUS_DECREASE.
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

        // B6: la época confirmada se cuenta una vez, al aparecer en la recarga.
        Assert.Equal(1, s.Metrics.Total(MetricNames.PlcEpochChanges, ("mode", PlcEpochStore.OperatorConfirmed)));
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

    /// <summary>
    /// B1 y MEDIA-3 (segunda revisión): un registro incoherente queda BLOCKED, abre su hueco y mueve la marca alta sin
    /// aportar su hora, igual en memoria que al recargar el estado desde SQL; no acepta épocas.
    /// </summary>
    [Fact]
    [Trait("Spec", "T13")]
    public async Task An_incoherent_record_feeds_the_mark_the_same_in_memory_and_after_reload()
    {
        var (s, ingest) = await StationAsync(_db);
        await ingest.ProcessRecordAsync(s.Cycle(100), Ct);
        s.AdvanceMs(300);
        var mixed = await ingest.ProcessRecordAsync(s.Cycle(105) with { Coherent = false }, Ct);
        Assert.Equal((SequenceCase.Gap, DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot),
            (mixed.Case, mixed.Result!.FinalStatus, mixed.Result.FinalReason));
        Assert.Equal(1, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st AND FromCycleId = 101 AND ToCycleId = 104",
            c => c.Parameters.AddWithValue("@st", s.StationId), Ct));

        var reloaded = s.Ingest();
        await reloaded.ReloadAsync(Ct);
        Assert.Equal((105UL, (DateTime?)null), (ingest.Classifier.Current!.HighWater, ingest.Classifier.Current.HighWaterTimestamp));
        Assert.Equal(ingest.Classifier.Current, reloaded.Classifier.Current);

        // El 101 legítimo se clasifica igual en las dos: sin hora confiable en la marca, un tardío no se distingue de
        // un reinicio (AMBIGUOUS_DECREASE, WARNING, sin bloquear la estación).
        s.AdvanceMs(300);
        var late = s.Cycle(101);
        var before = Assert.IsType<SequenceVerdict.Register>(ingest.Classifier.Classify(late, s.Revision, s.Clock.UtcNow));
        var after = Assert.IsType<SequenceVerdict.Register>(reloaded.Classifier.Classify(late, s.Revision, s.Clock.UtcNow));
        Assert.Equal((SequenceCase.AmbiguousDecrease, SequenceCase.AmbiguousDecrease), (before.Case, after.Case));

        var unknownEpoch = await ingest.ProcessRecordAsync(s.Cycle(1, epoch: 43) with { Coherent = false }, Ct);
        Assert.Equal(IngestKind.Retained, unknownEpoch.Kind);
        Assert.DoesNotContain(await s.Epochs.ListAsync(StationScenario.SiteId, s.StationId, Ct), e => e.Epoch == 43);
    }

    /// <summary>BAJA-2 (segunda revisión): un incoherente de una época recién confirmada se registra; no vuelve a bloquear la estación.</summary>
    [Fact]
    [Trait("Spec", "T25")]
    public async Task An_incoherent_record_of_a_just_confirmed_epoch_does_not_block_the_station_again()
    {
        var (s, ingest) = await StationAsync(_db, r => r with { EpochChangePolicy = "REQUIRE_CONFIRMATION" });
        await ingest.ProcessRecordAsync(s.Cycle(100), Ct);
        Assert.Equal(IngestKind.Retained, (await ingest.ProcessRecordAsync(s.Cycle(1, epoch: 43), Ct)).Kind);

        // Otra instancia (la API de operación) confirma y desbloquea: esta ingesta no lo sabe todavía.
        await s.Txm.ConfirmEpochAsync(StationScenario.SiteId, s.StationId, 43, 1, s.Revision.ConfigRevisionId, "supervisor.ramos", "OT confirma", s.Clock.UtcNow, Ct);
        await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "Época confirmada", s.Clock.UtcNow, Ct);
        var mixed = await ingest.ProcessRecordAsync(s.Cycle(2, epoch: 43) with { Coherent = false }, Ct);
        Assert.Equal(IngestKind.Registered, mixed.Kind);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot), (mixed.Result!.FinalStatus, mixed.Result.FinalReason));
        Assert.True((await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State.LabelingEnabled);
    }

    /// <summary>
    /// M4 y BAJA-4 (segunda revisión): con el canal acotado real al 90 % o con notificaciones descartadas desde el
    /// latido anterior, LabelingReady baja. Los descartes se cuentan en cada latido, también sin conexión.
    /// </summary>
    [Fact]
    public async Task A_saturated_channel_lowers_labeling_ready()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var channel = Channel.CreateBounded<RawNotification>(10);
        long dropped = 0;
        var pressure = new ChannelPressure(channel.Reader, 10, () => Interlocked.Read(ref dropped));
        var sink = new IngestScenario.RecordingSink();
        var heartbeat = new LabelingReadyHeartbeat(s.Settings, s.Stations, sink, s.Metrics, overloaded: pressure.Overloaded);
        var raw = new RawNotification("x", true, 0, false, Transport.Subscription, s.Clock.UtcNow, 0, null, null, 1, "ns=2;s=Record");

        Assert.True(await heartbeat.BeatAsync(Ct));
        for (var i = 0; i < 8; i++)
        {
            Assert.True(channel.Writer.TryWrite(raw));
        }

        Assert.True(await heartbeat.BeatAsync(Ct));    // 8 de 10
        Assert.True(channel.Writer.TryWrite(raw));
        Assert.False(await heartbeat.BeatAsync(Ct));   // 9 de 10
        while (channel.Reader.TryRead(out _))
        {
        }

        Assert.True(await heartbeat.BeatAsync(Ct));
        Interlocked.Increment(ref dropped);
        Assert.False(await heartbeat.BeatAsync(Ct));   // descartes desde el latido anterior
        Assert.True(await heartbeat.BeatAsync(Ct));

        sink.Connected = false;
        Interlocked.Increment(ref dropped);
        Assert.False(await heartbeat.BeatAsync(Ct));   // sin conexión: no escribe, pero cuenta el descarte
        sink.Connected = true;
        Assert.True(await heartbeat.BeatAsync(Ct));
        Assert.Equal([true, true, false, true, false, true, true], sink.Writes);
    }

    /// <summary>BAJA-6 (segunda revisión): una anomalía ya resuelta no se vuelve a resolver ni deja una OperatorAction sin efecto.</summary>
    [Fact]
    public async Task Resolving_an_already_resolved_anomaly_leaves_no_operator_action()
    {
        var (s, ingest) = await StationAsync(_db, r => r with { EpochChangePolicy = "REQUIRE_CONFIRMATION" });
        await ingest.ProcessRecordAsync(s.Cycle(100), Ct);
        var retained = await ingest.ProcessRecordAsync(s.Cycle(1, epoch: 43), Ct);
        var anomalyId = retained.AnomalyId!.Value;

        var first = await s.Txm.ResolveAnomalyAsync(anomalyId, StationScenario.SiteId, s.StationId, "supervisor.ramos", "Reingresado", s.Clock.UtcNow, Ct);
        var second = await s.Txm.ResolveAnomalyAsync(anomalyId, StationScenario.SiteId, s.StationId, "supervisor.lopez", "Reingresado", s.Clock.UtcNow, Ct);
        Assert.Equal((true, false), (first.Applied, second.Applied));
        Assert.Equal(1, await s.CountAsync("FROM dbo.OperatorAction WHERE ActionType = 'RESOLVE_ANOMALY' AND StationId = @st",
            c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
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
