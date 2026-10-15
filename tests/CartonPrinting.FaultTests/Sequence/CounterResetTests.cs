using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T24 · Contador reiniciado sin cambio de época. Época 42 con ciclos 1-5000 registrados (cada uno con su job);
/// el PLC reinicia CycleId a 1 sin incrementar PlcEpoch y continúa hasta 5200. El ciclo 1 choca con la clave
/// existente: IDENTITY_COLLISION BLOCKING y estación bloqueada; 2 a 5000 también chocan y cada uno queda como
/// SequenceAnomaly con su registro completo, no como detección; 5001 a 5200 quedan BLOCKED con
/// STATION_DISABLED. Ningún evento para las colisiones; la detección y el job existentes nunca se reutilizan;
/// ninguna etiqueta; señal de lista baja.
/// </summary>
[Trait("Spec", "T24")]
public sealed class CounterResetTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t24", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Reset_counter_collides_never_reuses_a_detection_and_blocks_the_station()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);

        // Precondición por el camino normal (TX-A con su serial y su job), en paralelo por bloques de ciclos.
        await Parallel.ForEachAsync(Enumerable.Range(1, 5000), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (cycle, token) => Assert.Equal(DetectionStatus.JobCreated, (await s.RegisterAsync(s.Cycle((ulong)cycle), ct: token)).FinalStatus));
        var preconditionEvents = (await s.OutboxAsync(ct)).Count;
        Assert.Equal(5000, preconditionEvents);

        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        Assert.Equal(5000UL, ingest.Classifier.Current!.HighWater);
        var sink = new IngestScenario.RecordingSink();
        var heartbeat = new LabelingReadyHeartbeat(s.Settings, s.Stations, sink, s.Metrics);
        Assert.True(await heartbeat.BeatAsync(ct));

        s.AdvanceMs(30 * 60_000);   // el reinicio ocurre después
        var outcomes = new List<IngestOutcome>();
        for (ulong cycle = 1; cycle <= 5200; cycle++)
        {
            s.AdvanceMs(1);
            outcomes.Add(await ingest.ProcessRecordAsync(s.Cycle(cycle), ct));
        }

        // 1 a 5000: colisiones, cada una con su anomalía y su registro completo.
        var collisions = outcomes.Take(5000).ToList();
        Assert.All(collisions, o => Assert.Equal(RegistrationOutcome.Collision, o.Result!.Outcome));
        long firstAnomaly;
        await using (var conn = new SqlConnection(s.Db.AppConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("""
                SELECT COUNT(*), COUNT(DISTINCT a.CycleId), MIN(a.CycleId), MAX(a.CycleId),
                       SUM(CASE WHEN a.Severity = 'BLOCKING' AND a.ExistingDetectionId = cd.DetectionId
                                 AND a.ExistingFingerprint = cd.SourceFingerprint AND a.IncomingFingerprint <> cd.SourceFingerprint
                                 AND ISJSON(a.IncomingRecord) = 1 AND JSON_VALUE(a.IncomingRecord, '$.cycleId') = CONVERT(NVARCHAR(20), a.CycleId)
                                 THEN 1 ELSE 0 END),
                       MIN(a.AnomalyId)
                FROM dbo.SequenceAnomaly AS a
                INNER JOIN dbo.CartonDetection AS cd ON cd.SiteId = a.SiteId AND cd.StationId = a.StationId AND cd.PlcEpoch = a.PlcEpoch AND cd.CycleId = a.CycleId
                WHERE a.StationId = @st AND a.AnomalyType = 'IDENTITY_COLLISION';
                """, conn);
            cmd.Parameters.AddWithValue("@st", s.StationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await r.ReadAsync(ct));
            Assert.Equal((5000, 5000, 1m, 5000m, 5000), (r.GetInt32(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4)));
            firstAnomaly = r.GetInt64(5);
        }

        // La detección y el job existentes nunca se reutilizan: siguen como estaban.
        Assert.Equal(5000, await s.CountAsync("""
            FROM dbo.CartonDetection AS cd INNER JOIN dbo.PrintJob AS pj ON pj.DetectionId = cd.DetectionId
            WHERE cd.StationId = @st AND cd.CycleId <= 5000 AND cd.Status = 'JOB_CREATED' AND pj.Status = 'READY'
            """, c => c.Parameters.AddWithValue("@st", s.StationId), ct));

        // Estación bloqueada por la primera colisión.
        var station = (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, ct))!.Value.State;
        Assert.Equal((false, BlockReason.IdentityCollision, (long?)firstAnomaly), (station.LabelingEnabled, station.BlockReason, station.BlockedByAnomalyId));

        // 5001 a 5200: claves nuevas durante el bloqueo, BLOCKED con STATION_DISABLED.
        Assert.All(outcomes.Skip(5000), o => Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled), (o.Result!.FinalStatus, o.Result.FinalReason)));

        // Eventos nuevos: solo los de 5001 a 5200; ninguno para las colisiones.
        var events = (await s.OutboxAsync(ct)).Skip(preconditionEvents).ToList();
        Assert.Equal(200, events.Count(e => e.Type == "CartonDetected"));
        Assert.Equal(200, events.Count(e => e.Type == "PrintJobResult"));
        Assert.All(events, e => Assert.True(
            ulong.Parse(System.Text.Json.JsonDocument.Parse(e.Payload).RootElement.GetProperty("cycleId").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 5000));

        // Ninguna etiqueta durante el bloqueo: ningún job nuevo; señal de lista baja.
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CycleId > 5000", c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        Assert.False(await heartbeat.BeatAsync(ct));
        Assert.Equal([true, false], sink.Writes);
        Assert.Equal(0.0, s.Metrics.Gauge(MetricNames.LabelingReady, ("station", s.StationId)));
        Assert.Equal(5000, s.Metrics.Total(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.IdentityCollision)));
    }
}
