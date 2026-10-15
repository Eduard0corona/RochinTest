using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T25 · Contador reiniciado con cambio de época. AUTO_INCREMENT_BY_ONE: la época 43 se acepta y las cajas
/// siguen normales. REQUIRE_CONFIRMATION: bloqueo hasta la confirmación; los registros retenidos se
/// reingresan con Transport ANOMALY_REINGEST (RECOVERED) y su propio plazo decide. Salto a 45:
/// EPOCH_UNEXPECTED y bloqueo. PlcEpochHistory registra quién y cómo aceptó cada época; una aceptación
/// manual sin OperatorAction CONFIRM_EPOCH la rechaza el trigger. (La huella contra la referencia de
/// Python está en UnitTests/Orchestration/FingerprintVectorTests.)
/// </summary>
[Trait("Spec", "T25")]
public sealed class EpochChangeTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t25", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<(StationScenario S, StationIngestService Ingest)> AtEpoch42Async(TestDatabase db, string policy, CancellationToken ct)
    {
        var s = await StationScenario.CreateAsync(db, r => r with { EpochChangePolicy = policy }, ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        foreach (var cycle in new ulong[] { 4999, 5000 })
        {
            Assert.Equal(DetectionStatus.JobCreated, (await ingest.ProcessRecordAsync(s.Cycle(cycle), ct)).Result!.FinalStatus);
        }

        return (s, ingest);
    }

    [Fact]
    public async Task Auto_increment_accepts_epoch_43_and_cartons_continue_normally()
    {
        var ct = TestContext.Current.CancellationToken;
        var (s, ingest) = await AtEpoch42Async(_db, PlcEpochStore.AutoIncrementByOne, ct);

        foreach (var cycle in new ulong[] { 1, 2 })
        {
            s.AdvanceMs(300);
            var outcome = await ingest.ProcessRecordAsync(s.Cycle(cycle, epoch: 43), ct);
            Assert.Equal(DetectionStatus.JobCreated, outcome.Result!.FinalStatus);
        }

        var epoch = Assert.Single(await s.Epochs.ListAsync(StationScenario.SiteId, s.StationId, ct), e => e.Epoch == 43);
        Assert.Equal((PlcEpochStore.AutoIncrementByOne, "SYSTEM", 1UL, (long?)null), (epoch.AcceptanceMode, epoch.AcceptedBy, epoch.FirstCycleId, epoch.OperatorActionId));
        Assert.Equal(1, s.Metrics.Total(MetricNames.PlcEpochChanges, ("mode", PlcEpochStore.AutoIncrementByOne)));
        Assert.True((await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, ct))!.Value.State.LabelingEnabled);
        Assert.Equal(0, await s.CountAsync("FROM dbo.SequenceAnomaly WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), ct));
    }

    [Fact]
    public async Task Require_confirmation_retains_until_confirmed_and_reingested_records_decide_by_their_own_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var (s, ingest) = await AtEpoch42Async(_db, "REQUIRE_CONFIRMATION", ct);

        // Época 43: el ciclo 1 se detectó hace 3 s (ya vencido al reingresar); el 2 y el 3, recién.
        var old = s.Cycle(1, epoch: 43, detectionTimestamp: s.Clock.UtcNow.AddSeconds(-3));
        var retained = new List<IngestOutcome> { await ingest.ProcessRecordAsync(old, ct) };
        foreach (var cycle in new ulong[] { 2, 3 })
        {
            retained.Add(await ingest.ProcessRecordAsync(s.Cycle(cycle, epoch: 43), ct));
        }

        Assert.All(retained, o => Assert.Equal(IngestKind.Retained, o.Kind));
        Assert.Null(await s.DetectionInEpochAsync(43, 1, ct));
        var station = (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, ct))!.Value.State;
        Assert.Equal((false, BlockReason.EpochUnexpected, retained[0].AnomalyId), (station.LabelingEnabled, station.BlockReason, station.BlockedByAnomalyId));
        Assert.Equal(3, s.Metrics.Total(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.EpochUnexpected)));
        Assert.DoesNotContain(await s.OutboxAsync(ct), e => e.Payload.Contains("\"plcEpoch\":43", StringComparison.Ordinal));

        // Sin la época confirmada no hay reingreso.
        await Assert.ThrowsAsync<InvalidOperationException>(() => ingest.ReingestAsync(retained[0].AnomalyId!.Value, "supervisor.ramos", "Reingreso", ct));

        // Confirmación por una persona (CONFIRM_EPOCH), desbloqueo (UNBLOCK_STATION) y reingreso.
        var confirm = await s.Txm.ConfirmEpochAsync(StationScenario.SiteId, s.StationId, 43, 1, s.Revision.ConfigRevisionId, "supervisor.ramos",
            "OT confirma el reinicio del contador con la época 43", s.Clock.UtcNow, ct);
        await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "Época 43 confirmada", s.Clock.UtcNow, ct);
        var reingested = new List<IngestOutcome>();
        foreach (var r in retained)
        {
            reingested.Add(await ingest.ReingestAsync(r.AnomalyId!.Value, "supervisor.ramos", "Reingreso tras confirmar la época 43", ct));
        }

        Assert.All(reingested, o => Assert.Equal(Transport.AnomalyReingest, o.Record!.Transport));
        Assert.Equal((DetectionStatus.Expired, DetectionReason.LateArrival), (reingested[0].Result!.FinalStatus, reingested[0].Result!.FinalReason));
        Assert.All(reingested.Skip(1), o => Assert.Equal(DetectionStatus.JobCreated, o.Result!.FinalStatus));
        var first = (await s.DetectionInEpochAsync(43, 1, ct))!;
        Assert.Equal((DetectionOrigin.Recovered, Transport.AnomalyReingest, CanonicalTimestamp.Truncate(old.ReceivedAt)),
            (first.Origin, first.Transport, first.ReceivedAt));
        Assert.Equal(0, await s.CountAsync("FROM dbo.SequenceAnomaly WHERE StationId = @st AND ResolvedAt IS NULL",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));

        // Quién y cómo aceptó la época.
        var epoch = Assert.Single(await s.Epochs.ListAsync(StationScenario.SiteId, s.StationId, ct), e => e.Epoch == 43);
        Assert.Equal((PlcEpochStore.OperatorConfirmed, "supervisor.ramos", confirm.OperatorActionId),
            (epoch.AcceptanceMode, epoch.AcceptedBy, epoch.OperatorActionId!.Value));
        Assert.Equal(OperatorActionType.ConfirmEpoch, await s.ScalarAsync<string>("SELECT ActionType FROM dbo.OperatorAction WHERE OperatorActionId = @id",
            c => c.Parameters.AddWithValue("@id", confirm.OperatorActionId), ct));

        // Reingresar otra vez es un duplicado auténtico, no otra detección.
        Assert.Equal(1, await s.CountAsync("FROM dbo.CartonDetection WHERE StationId = @st AND PlcEpoch = 43 AND CycleId = 1",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));
    }

    [Fact]
    public async Task Manual_acceptance_without_a_confirm_epoch_action_is_rejected_by_the_trigger()
    {
        var ct = TestContext.Current.CancellationToken;
        var (s, _) = await AtEpoch42Async(_db, "REQUIRE_CONFIRMATION", ct);
        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.Operator, "supervisor.ramos", null, s.Clock.UtcNow, ct);
        var unblock = await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "Prueba", s.Clock.UtcNow, ct);

        var ex = await s.FailsAsync("""
            INSERT INTO dbo.PlcEpochHistory (SiteId, StationId, PlcEpoch, EpochSource, AcceptanceMode, FirstCycleId, AcceptedAt, AcceptedBy,
                ConfigRevisionId, OperatorActionId)
            VALUES (@site, @st, 44, 'PLC', 'OPERATOR_CONFIRMED', 1, SYSUTCDATETIME(), N'supervisor.ramos', @rev, @oa)
            """, c =>
        {
            c.Parameters.AddWithValue("@site", StationScenario.SiteId);
            c.Parameters.AddWithValue("@st", s.StationId);
            c.Parameters.AddWithValue("@rev", s.Revision.ConfigRevisionId);
            c.Parameters.AddWithValue("@oa", unblock.OperatorActionId);   // una acción que no es CONFIRM_EPOCH
        }, ct: ct);
        Assert.Equal(51032, ex.Number);
    }

    [Fact]
    public async Task Jump_to_epoch_45_is_unexpected_and_blocks_the_station()
    {
        var ct = TestContext.Current.CancellationToken;
        var (s, ingest) = await AtEpoch42Async(_db, PlcEpochStore.AutoIncrementByOne, ct);

        var jump = await ingest.ProcessRecordAsync(s.Cycle(1, epoch: 45), ct);
        Assert.Equal(IngestKind.Retained, jump.Kind);
        await using var conn = new SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT AnomalyType, Severity, PlcEpoch, JSON_VALUE(IncomingRecord, '$.plcEpoch') FROM dbo.SequenceAnomaly WHERE AnomalyId = @id", conn);
        cmd.Parameters.AddWithValue("@id", jump.AnomalyId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct));
        Assert.Equal((AnomalyType.EpochUnexpected, "BLOCKING", 45L, "45"), (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));
        Assert.Equal(BlockReason.EpochUnexpected, (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, ct))!.Value.State.BlockReason);
        Assert.DoesNotContain(await s.Epochs.ListAsync(StationScenario.SiteId, s.StationId, ct), e => e.Epoch == 45);
        Assert.Equal(1, s.Metrics.Total(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.EpochUnexpected)));
    }
}
