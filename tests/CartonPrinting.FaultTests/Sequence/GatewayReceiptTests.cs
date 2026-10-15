using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T45 · Registros sin cota de latencia con GATEWAY_RECEIPT (PLC sin DetectionTimestamp; marca alta en 700).
/// 701 por READ, 702 por REPUBLISH, 703 en vivo con Overflow, un registro retenido reingresado
/// (ANOMALY_REINGEST) y la primera notificación de la suscripción recreada (730, READ): EXPIRED con
/// LATE_ARRIVAL y DeadlineAt = ReceivedAt. Un ciclo en vivo que abre un hueco: también LATE_ARRIVAL.
/// Crear un job para esos registros lo rechaza CK_CartonDetection_GatewayLive. Un ciclo menor que la
/// marca alta, clave nueva y sin timestamp: AMBIGUOUS_DECREASE y BLOCKED con SEQUENCE_ANOMALY.
/// </summary>
[Trait("Spec", "T45")]
public sealed class GatewayReceiptTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t45", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static StationRevision Gateway(StationRevision r) => r with
    {
        DeadlineReference = DeadlineReference.GatewayReceipt, ClockSkewToleranceMs = null, MaxUpstreamLatencyMs = 40,
    };

    /// <summary>Registro del PLC sin DetectionTimestamp.</summary>
    private static CycleRecord NoTs(StationScenario s, ulong cycle, string transport, bool overflow = false) =>
        s.Cycle(cycle, transport, overflow: overflow) with { DetectionTimestamp = null, OpcSourceTimestamp = s.Clock.UtcNow };

    [Fact]
    public async Task Records_without_a_latency_bound_get_no_deadline_and_a_lower_new_key_is_an_ambiguous_decrease()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, Gateway, ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        Assert.Equal(DetectionStatus.JobCreated, (await ingest.ProcessRecordAsync(NoTs(s, 700, Transport.Subscription), ct)).Result!.FinalStatus);

        var late = new List<IngestOutcome>();
        foreach (var record in new[]
        {
            NoTs(s, 701, Transport.Read), NoTs(s, 702, Transport.Republish), NoTs(s, 703, Transport.Subscription, overflow: true),
            NoTs(s, 730, Transport.Read),          // primera notificación de la suscripción recreada: abre 704-729
            NoTs(s, 740, Transport.Subscription),  // en vivo, sin overflow, pero abre 731-739
        })
        {
            s.AdvanceMs(5);
            late.Add(await ingest.ProcessRecordAsync(record, ct));
        }

        foreach (var o in late)
        {
            Assert.Equal((DetectionStatus.Expired, DetectionReason.LateArrival), (o.Result!.FinalStatus, o.Result.FinalReason));
            var d = (await s.DetectionAsync(o.Record!.CycleId, ct))!;
            Assert.Equal(d.ReceivedAt, d.DeadlineAt);
            Assert.Null(o.Result.PrintJobId);
        }

        Assert.Equal([SequenceCase.Gap, SequenceCase.Gap], late.Skip(3).Select(o => o.Case));

        // Un intento de crear el job de un registro recuperado o con overflow lo rechaza CK_CartonDetection_GatewayLive.
        const string createJob = "UPDATE dbo.CartonDetection SET Status = 'JOB_CREATED', Reason = NULL WHERE DetectionId = @id";
        foreach (var cycle in new ulong[] { 701, 702, 703, 730 })
        {
            var ex = await s.FailsAsync(createJob,
                c => c.Parameters.AddWithValue("@id", new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, cycle).DetectionId), ct: ct);
            Assert.Contains("CK_CartonDetection_GatewayLive", ex.Message, StringComparison.Ordinal);
        }

        // El 740 llegó en vivo y sin overflow: la CHECK no lo distingue (el hueco es regla de código en TX-A, SI-05);
        // lo detiene la máquina de estados, porque EXPIRED es terminal.
        var live = await s.FailsAsync(createJob,
            c => c.Parameters.AddWithValue("@id", new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, 740).DetectionId), ct: ct);
        Assert.Equal(51011, live.Number);

        // Ciclo menor que la marca alta (740), clave nueva y sin timestamp.
        var lower = await ingest.ProcessRecordAsync(NoTs(s, 720, Transport.Subscription), ct);
        Assert.Equal(SequenceCase.AmbiguousDecrease, lower.Case);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.SequenceAnomaly), (lower.Result!.FinalStatus, lower.Result.FinalReason));
        Assert.NotNull(lower.Result.AnomalyId);
        Assert.Equal("WARNING", await s.ScalarAsync<string>("SELECT Severity FROM dbo.SequenceAnomaly WHERE AnomalyId = @id",
            c => c.Parameters.AddWithValue("@id", lower.Result.AnomalyId!.Value), ct));

        // Resultados publicados sin job y métricas por transporte.
        var results = await s.ResultsAsync(ct: ct);
        Assert.Equal(6, results.Count);
        Assert.All(results, r => Assert.False(r.TryGetProperty("printJobId", out var p) && p.ValueKind != System.Text.Json.JsonValueKind.Null));
        Assert.Equal(2, s.Metrics.Total(MetricNames.DetectionsExpired, ("reason", DetectionReason.LateArrival), ("transport", Transport.Read)));
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsExpired, ("reason", DetectionReason.LateArrival), ("transport", Transport.Republish)));
        Assert.Equal(2, s.Metrics.Total(MetricNames.DetectionsExpired, ("reason", DetectionReason.LateArrival), ("transport", Transport.Subscription)));
        Assert.Equal(1, s.Metrics.Total(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.AmbiguousDecrease)));
    }

    [Fact]
    public async Task A_retained_record_reingested_has_no_deadline_even_if_it_first_arrived_live()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, r => Gateway(r) with { EpochChangePolicy = "REQUIRE_CONFIRMATION" }, ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        await ingest.ProcessRecordAsync(NoTs(s, 700, Transport.Subscription), ct);

        var retained = await ingest.ProcessRecordAsync(NoTs(s, 1, Transport.Subscription) with { PlcEpoch = 43 }, ct);
        Assert.Equal(IngestKind.Retained, retained.Kind);
        await s.Txm.ConfirmEpochAsync(StationScenario.SiteId, s.StationId, 43, 1, s.Revision.ConfigRevisionId, "supervisor.ramos", "OT confirma", s.Clock.UtcNow, ct);
        await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "Época confirmada", s.Clock.UtcNow, ct);

        var reingest = await ingest.ReingestAsync(retained.AnomalyId!.Value, "supervisor.ramos", "Reingreso", ct);
        Assert.Equal(Transport.AnomalyReingest, reingest.Record!.Transport);
        Assert.Equal((DetectionStatus.Expired, DetectionReason.LateArrival), (reingest.Result!.FinalStatus, reingest.Result.FinalReason));
        var d = (await s.DetectionInEpochAsync(43, 1, ct))!;
        Assert.Equal(d.ReceivedAt, d.DeadlineAt);
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsExpired, ("reason", DetectionReason.LateArrival), ("transport", Transport.AnomalyReingest)));
    }

    [Fact]
    public async Task End_to_end_over_opc_the_first_notification_after_a_server_restart_is_read_and_expires()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, Gateway, ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        await using var h = await OpcHarness.StartAsync(s.StationId, s.Clock);
        var initial = await ingest.PumpAsync(h, l => l.Count >= 1);
        Assert.Equal((IngestKind.Rejected, DecodeResult.Empty), (initial[0].Kind, initial[0].Rejection));   // valor inicial vacío del PLC

        h.Server.Nodes.Publish(new LatchedRecord { PlcEpoch = s.Epoch, CycleId = 700, WorkOrderId = StationScenario.WorkOrder });
        var live = await ingest.PumpAsync(h, l => l.Any(o => o.Kind == IngestKind.Registered));
        Assert.Equal((Transport.Subscription, DetectionStatus.JobCreated), (live[^1].Record!.Transport, live[^1].Result!.FinalStatus));

        await h.RestartServerAsync(nodes => nodes.Publish(new LatchedRecord { PlcEpoch = s.Epoch, CycleId = 730, WorkOrderId = StationScenario.WorkOrder }));
        var after = await ingest.PumpAsync(h, l => l.Any(o => o.Record?.CycleId == 730 && o.Kind == IngestKind.Registered), TimeSpan.FromSeconds(45));
        var read = after.First(o => o.Record?.CycleId == 730 && o.Kind == IngestKind.Registered);
        Assert.Equal(Transport.Read, read.Record!.Transport);
        Assert.Equal((DetectionStatus.Expired, DetectionReason.LateArrival), (read.Result!.FinalStatus, read.Result.FinalReason));
        var d = (await s.DetectionAsync(730, ct))!;
        Assert.Equal((d.ReceivedAt, DetectionOrigin.Recovered), (d.DeadlineAt, d.Origin));
        Assert.Equal(1, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st AND FromCycleId = 701 AND ToCycleId = 729 AND Status = 'OPEN'",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));
    }
}
