using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Opc;

/// <summary>
/// T11 · Reconexión OPC con FIFO del PLC (simulada; el journal y AckCycleId son de la Fase 6). Marca alta
/// en 1000; el gateway pierde el PLC mientras pasan 20 cajas (1001 a 1020) y reconecta cuando el registro
/// actual ya es la caja 1021. La lectura tras reconectar abre el hueco 1001-1020; la FIFO entrega los 20
/// como RECOVERED, que vencen por LATE_ARRIVAL; el hueco queda RECOVERED. Ningún ciclo duplicado ni impreso
/// fuera de plazo. Interpretación del catálogo (SI-11): el hueco 1001-1020 exige que el valor leído al
/// reconectar sea el de un ciclo posterior a los 20. La desconexión es del gateway (enlace congelado más que la
/// sesión), no un reinicio del PLC.
/// </summary>
[Trait("Spec", "T11")]
public sealed class FifoReconnectTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t11", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Fifo_recovers_the_gap_as_late_arrivals_and_nothing_is_printed_late()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        await using var h = await OpcHarness.StartAsync(s.StationId, s.Clock, o => o.SessionTimeoutMs = 3_000, withFifo: true);
        await ingest.PumpAsync(h, l => l.Count >= 1);   // valor inicial vacío

        LatchedRecord Record(ulong cycle, DateTime ts) => new() { PlcEpoch = s.Epoch, CycleId = cycle, WorkOrderId = StationScenario.WorkOrder, DetectionTimestamp = ts };
        h.Server.Nodes.Publish(Record(1000, s.Clock.UtcNow.AddMilliseconds(-20)));
        var mark = await ingest.PumpAsync(h, l => l.Any(o => o.Kind == IngestKind.Registered));
        Assert.Equal((1000UL, DetectionStatus.JobCreated), (mark[^1].Record!.CycleId, mark[^1].Result!.FinalStatus));

        // La desconexión dura unos 10 s en el reloj del EdgeHost.
        s.AdvanceMs(10_300);
        var now = s.Clock.UtcNow;

        // Desconexión: pasan 1001 a 1020 (300 ms entre cajas, la última 4 s antes de reconectar) y al
        // reconectar el registro actual es 1021.
        var missed = Enumerable.Range(1, 20).Select(i => Record(1000 + (ulong)i, now.AddMilliseconds(-10_000 + (i * 300)))).ToList();
        // Se desconecta el gateway (el enlace queda congelado más que la sesión y la suscripción): el PLC sigue
        // fijando cada caja en su registro y en su FIFO; al reconectar, la sesión es nueva.
        h.Proxy.Freeze();
        foreach (var box in missed)
        {
            h.Server.Nodes.Publish(box);
        }

        h.Server.Nodes.SetFifo(missed);
        h.Server.Nodes.Publish(Record(1021, now.AddMilliseconds(-20)));
        await Task.Delay(8_000);
        h.Proxy.SeverAll();
        h.Proxy.Resume();
        var after = await ingest.PumpAsync(h, l => l.Count(o => o.Record?.Transport == Transport.PlcFifo) >= 20, TimeSpan.FromSeconds(45));

        // La lectura tras reconectar abre el hueco 1001-1020 antes de que llegue la FIFO.
        var read = after.First(o => o.Record?.CycleId == 1021 && o.Kind == IngestKind.Registered);
        Assert.Equal(Transport.Read, read.Record!.Transport);
        Assert.Equal(SequenceCase.Gap, read.Case);
        Assert.NotNull(read.Result!.GapId);
        var fifo = after.Where(o => o.Record?.Transport == Transport.PlcFifo).ToList();
        Assert.True(after.IndexOf(read) < after.IndexOf(fifo[0]));

        // Los 20 de la FIFO: RECOVERED, EXPIRED por LATE_ARRIVAL, sin job.
        Assert.Equal(Enumerable.Range(1001, 20).Select(c => (ulong)c), fifo.Select(o => o.Record!.CycleId));
        Assert.All(fifo, o => Assert.Equal((DetectionStatus.Expired, DetectionReason.LateArrival, (Guid?)null),
            (o.Result!.FinalStatus, o.Result.FinalReason, o.Result.PrintJobId)));
        foreach (var o in fifo)
        {
            Assert.Equal(DetectionOrigin.Recovered, (await s.DetectionAsync(o.Record!.CycleId, ct))!.Origin);
        }

        Assert.Equal(1, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st AND FromCycleId = 1001 AND ToCycleId = 1020 AND Status = 'RECOVERED'",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        await ingest.SweepGapsAsync(ct);
        Assert.Equal(0.0, s.Metrics.Gauge(MetricNames.SequenceGapsOpen, ("station", s.StationId)));

        // Ningún ciclo duplicado ni impreso fuera de plazo: 22 detecciones únicas; jobs solo para 1000 y 1021.
        Assert.Equal(22, await s.CountAsync("FROM dbo.CartonDetection WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        Assert.Equal([1000m, 1021m], (await JobCyclesAsync(s, ct)).ToArray());
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CycleId BETWEEN 1001 AND 1020",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        var results = await s.ResultsAsync(ct: ct);
        Assert.Equal(20, results.Count(r => r.GetProperty("outcome").GetString() == "EXPIRED"));
        Assert.Equal(20, (await s.OutboxAsync(ct)).Count(e => e.Type == "CartonDetected"
            && System.Text.Json.JsonDocument.Parse(e.Payload).RootElement.GetProperty("detectionOrigin").GetString() == "RECOVERED"
            && System.Text.Json.JsonDocument.Parse(e.Payload).RootElement.GetProperty("transport").GetString() == "PLC_FIFO"));
        Assert.True(h.Metrics.Total(MetricNames.OpcReconnect) >= 1);
    }

    private static async Task<List<decimal>> JobCyclesAsync(StationScenario s, CancellationToken ct)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT CycleId FROM dbo.PrintJob WHERE StationId = @st ORDER BY CycleId", conn);
        cmd.Parameters.AddWithValue("@st", s.StationId);
        var list = new List<decimal>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(r.GetDecimal(0));
        }

        return list;
    }
}
