using System.Data;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T24 · Contador reiniciado sin cambio de época. Época 42 con ciclos 1-5000 registrados; el PLC reinicia
/// CycleId a 1 sin incrementar PlcEpoch y continúa hasta 5200. El ciclo 1 choca con la clave existente:
/// IDENTITY_COLLISION BLOCKING y estación bloqueada; 2 a 5000 también chocan y cada uno queda como
/// SequenceAnomaly con su registro completo, no como detección; 5001 a 5200 quedan BLOCKED con
/// STATION_DISABLED. Ningún evento para las colisiones; ninguna etiqueta; señal de lista baja.
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
        var before = s.Clock.UtcNow.AddMinutes(-30);
        await BulkRegisterAsync(s, 1, 5000, before, ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        Assert.Equal(5000UL, ingest.Classifier.Current!.HighWater);
        var sink = new IngestScenario.RecordingSink();
        var heartbeat = new LabelingReadyHeartbeat(s.Settings, s.Stations, sink, s.Metrics);
        Assert.True(await heartbeat.BeatAsync(ct));

        var outcomes = new List<IngestOutcome>();
        for (ulong cycle = 1; cycle <= 5200; cycle++)
        {
            s.AdvanceMs(1);
            outcomes.Add(await ingest.ProcessRecordAsync(s.Cycle(cycle), ct));
        }

        // 1 a 5000: colisiones, cada una con su anomalía y su registro completo.
        var collisions = outcomes.Take(5000).ToList();
        Assert.All(collisions, o => Assert.Equal(RegistrationOutcome.Collision, o.Result!.Outcome));
        await using (var conn = new SqlConnection(s.Db.AppConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("""
                SELECT COUNT(*), COUNT(DISTINCT a.CycleId), MIN(a.CycleId), MAX(a.CycleId),
                       SUM(CASE WHEN a.Severity = 'BLOCKING' AND a.ExistingDetectionId = cd.DetectionId
                                 AND a.ExistingFingerprint = cd.SourceFingerprint AND a.IncomingFingerprint <> cd.SourceFingerprint
                                 AND ISJSON(a.IncomingRecord) = 1 AND JSON_VALUE(a.IncomingRecord, '$.cycleId') = CONVERT(NVARCHAR(20), a.CycleId)
                                 THEN 1 ELSE 0 END)
                FROM dbo.SequenceAnomaly AS a
                INNER JOIN dbo.CartonDetection AS cd ON cd.SiteId = a.SiteId AND cd.StationId = a.StationId AND cd.PlcEpoch = a.PlcEpoch AND cd.CycleId = a.CycleId
                WHERE a.StationId = @st AND a.AnomalyType = 'IDENTITY_COLLISION';
                """, conn);
            cmd.Parameters.AddWithValue("@st", s.StationId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await r.ReadAsync(ct));
            Assert.Equal((5000, 5000, 1m, 5000m, 5000), (r.GetInt32(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4)));
        }

        // La detección existente nunca se reutiliza: las 5000 siguen como estaban.
        Assert.Equal(5000, await s.CountAsync("FROM dbo.CartonDetection WHERE StationId = @st AND CycleId <= 5000 AND Status = 'EXPIRED' AND Transport = 'SUBSCRIPTION'",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));

        // Estación bloqueada por la primera colisión.
        var station = (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, ct))!.Value.State;
        Assert.Equal((false, BlockReason.IdentityCollision), (station.LabelingEnabled, station.BlockReason));

        // 5001 a 5200: claves nuevas durante el bloqueo, BLOCKED con STATION_DISABLED.
        Assert.All(outcomes.Skip(5000), o => Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled), (o.Result!.FinalStatus, o.Result.FinalReason)));

        // Eventos: solo los de 5001 a 5200; ninguno para las colisiones.
        var events = await s.OutboxAsync(ct);
        Assert.Equal(200, events.Count(e => e.Type == "CartonDetected"));
        Assert.Equal(200, events.Count(e => e.Type == "PrintJobResult"));
        Assert.All(events, e => Assert.True(
            ulong.Parse(System.Text.Json.JsonDocument.Parse(e.Payload).RootElement.GetProperty("cycleId").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 5000));

        // Ninguna etiqueta y señal de lista baja.
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        Assert.False(await heartbeat.BeatAsync(ct));
        Assert.Equal([true, false], sink.Writes);
        Assert.Equal(0.0, s.Metrics.Gauge(MetricNames.LabelingReady, ("station", s.StationId)));
        Assert.Equal(5000, s.Metrics.Total(MetricNames.PlcSequenceAnomaly, ("type", AnomalyType.IdentityCollision)));
    }

    /// <summary>
    /// Precondición "época 42 con ciclos 1-5000 registrados", cargada en bloque con SqlBulkCopy (triggers y
    /// CHECK activos: FireTriggers y CheckConstraints) y llevada a EXPIRED por la transición D04.
    /// </summary>
    private static async Task BulkRegisterAsync(StationScenario s, ulong from, ulong to, DateTime detectedAt, CancellationToken ct)
    {
        var table = new DataTable();
        foreach (var (name, type) in new (string, Type)[]
        {
            ("DetectionId", typeof(Guid)), ("SiteId", typeof(string)), ("StationId", typeof(string)), ("PlcEpoch", typeof(long)),
            ("CycleId", typeof(decimal)), ("IdempotencyKey", typeof(string)), ("SourceFingerprint", typeof(byte[])), ("Origin", typeof(string)),
            ("Transport", typeof(string)), ("QueueOverflow", typeof(bool)), ("DetectionTimestamp", typeof(DateTime)), ("ReceivedAt", typeof(DateTime)),
            ("DeadlineReference", typeof(string)), ("DeadlineAt", typeof(DateTime)), ("ConfigRevisionId", typeof(long)), ("WorkOrderId", typeof(string)),
            ("Status", typeof(string)),
        })
        {
            table.Columns.Add(name, type);
        }

        for (var cycle = from; cycle <= to; cycle++)
        {
            var ts = CanonicalTimestamp.Truncate(detectedAt.AddMilliseconds((double)(cycle - from) * 300));
            var record = s.Cycle(cycle, detectionTimestamp: ts) with { ReceivedAt = ts.AddMilliseconds(17) };
            var id = record.Identity;
            table.Rows.Add(id.DetectionId, id.SiteId, id.StationId, id.PlcEpoch, (decimal)cycle, id.IdempotencyKey, record.SourceFingerprint(),
                DetectionOrigin.Observed, Transport.Subscription, false, ts, record.ReceivedAt, DeadlineReference.PlcTimestamp,
                ts.AddMilliseconds(1050), s.Revision.ConfigRevisionId, record.WorkOrderId, DetectionStatus.Received);
        }

        await using var conn = new SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(ct);
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.FireTriggers | SqlBulkCopyOptions.CheckConstraints, null)
               { DestinationTableName = "dbo.CartonDetection", BatchSize = 1000 })
        {
            foreach (DataColumn c in table.Columns)
            {
                bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            }

            await bulk.WriteToServerAsync(table, ct);
        }

        await using var expire = new SqlCommand("""
            UPDATE dbo.CartonDetection SET Status = 'EXPIRED', Reason = 'LATE_ARRIVAL' WHERE StationId = @st AND Status = 'RECEIVED';
            """, conn);
        expire.Parameters.AddWithValue("@st", s.StationId);
        Assert.Equal((int)(to - from + 1), await expire.ExecuteNonQueryAsync(ct));
    }
}
