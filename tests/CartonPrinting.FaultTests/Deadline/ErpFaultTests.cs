using CartonPrinting.Contracts;
using CartonPrinting.ErpSync;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Simulators.Erp;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Deadline;

/// <summary>ERP lento, caído y que vuelve tarde (T03, T04, T17), contra el ERP simulado por HTTP.</summary>
public sealed class ErpFaultTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f3erp", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ErpWorkOrder Order(string id, DateTime now) => new()
    {
        WorkOrderId = id, Revision = "R1", ProductId = "SKU-" + id, LotId = "LOT-" + id, ValidFrom = now.AddHours(-1), ValidUntil = now.AddHours(8),
    };

    private static (ErpSnapshotSync Sync, HttpClient Http) Sync(StationScenario s, ErpSimulatorServer erp, int timeoutMs = 500)
    {
        var http = new HttpClient { BaseAddress = new Uri(erp.BaseUrl) };
        var arrivals = new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics);
        var sync = new ErpSnapshotSync(s.Settings, new HttpErpClient(http, s.Clock), s.Sql, s.Erp, arrivals, s.Clock, s.Metrics, new ErpSyncOptions
        {
            BaseUrl = erp.BaseUrl, TimeoutMs = timeoutMs, PollIntervalMs = 20, MinimumThroughput = 2, SamplingDurationMs = 2_000, BreakDurationMs = 1_000,
        });
        return (sync, http);
    }

    private static StationSweepService Sweep(StationScenario s) => new(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);

    /// <summary>
    /// T03 · ERP lento dentro del plazo (reloj real, PLC simulado por OPC UA, ERP con 300 ms de retraso):
    /// la ingesta deja la detección en WAITING_ERP sin esperar al ERP; la sincronización asíncrona trae el
    /// snapshot y TX-E crea el job dentro del plazo. El callback OPC no se alarga. Para comprobar que la ingesta
    /// no llama al ERP de forma síncrona (revisión de la Fase 3, MEDIA-4), el ERP retiene las peticiones mientras
    /// se procesa el ciclo: la ingesta debe devolver sin ninguna respuesta del ERP para esa orden.
    /// </summary>
    [Fact]
    [Trait("Spec", "T03")]
    public async Task Slow_erp_within_the_deadline_creates_the_job_without_blocking_ingest()
    {
        // TransitBudgetMs mayor que el retraso del ERP (precondición del catálogo) y con margen para el ruido de la
        // máquina de pruebas; el cliente HTTP y el ERP simulado se calientan antes del ciclo medido.
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 3_000 }, Ct, TimeProvider.System);
        await using var erp = await ErpSimulatorServer.StartAsync(Ct);
        erp.DelayMs = 300;
        erp.Set(Order("WO-1", DateTime.UtcNow));
        var (sync, http) = Sync(s, erp);
        using var _ = http;
        await new HttpErpClient(http, s.Clock).GetSnapshotAsync("WO-CALENTAMIENTO", Ct);
        var ingest = new StationIngestService(s.Settings, s.Registration, s.Stations, s.Epochs, new Infrastructure.Data.SequenceStore(s.Sql), s.Txs, s.Txm,
            s.Clock, s.Metrics, erpDemand: sync);
        await ingest.ReloadAsync(Ct);
        await using var h = await OpcHarness.StartAsync(s.StationId, s.Clock);
        await ingest.PumpAsync(h, l => l.Count >= 1);   // valor inicial vacío

        using var running = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = sync.RunAsync(TimeProvider.System, running.Token);
        try
        {
            erp.Hold();
            h.Server.Nodes.Publish(new LatchedRecord { PlcEpoch = s.Epoch, CycleId = 100, WorkOrderId = "WO-1", DetectionTimestamp = DateTime.UtcNow.AddMilliseconds(-10) });
            var pump = ingest.PumpAsync(h, l => l.Any(o => o.Kind == IngestKind.Registered), TimeSpan.FromSeconds(10));
            var finished = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(10), Ct));
            var answeredWhenIngestReturned = erp.Answered("WO-1");
            erp.Release();
            Assert.Same(pump, finished);                                                 // con el ERP retenido, la ingesta terminó
            var registered = (await pump)[^1];
            Assert.Equal(DetectionStatus.WaitingErp, registered.Result!.FinalStatus);   // la ingesta no esperó al ERP
            Assert.Equal(0, answeredWhenIngestReturned);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((await s.DetectionAsync(100, Ct))!.Status == DetectionStatus.WaitingErp && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, Ct);
            }
        }
        finally
        {
            erp.Release();
            await running.CancelAsync();
            await loop.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        var detection = (await s.DetectionAsync(100, Ct))!;
        Assert.Equal(DetectionStatus.JobCreated, detection.Status);
        var job = await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CycleId = 100 AND LotId = 'LOT-WO-1' AND Status = 'READY'",
            c => c.Parameters.AddWithValue("@st", s.StationId), Ct);
        Assert.Equal(1, job);
        Assert.True(erp.Requests >= 1);
        var wait = Assert.Single(s.Metrics.Samples(MetricNames.ErpSnapshotWaitMs));
        Assert.InRange(wait, 300, detection.DeadlineAt.Subtract(detection.ReceivedAt).TotalMilliseconds);
        Assert.All(h.Metrics.Samples(MetricNames.OpcCallbackDurationMs), ms => Assert.True(ms < 50, $"callback OPC de {ms} ms"));
    }

    /// <summary>
    /// T04 · ERP caído (503 y timeouts toda la prueba): las detecciones esperan en WAITING_ERP y el barrido las
    /// vence con ERP_UNAVAILABLE; un PrintJobResult EXPIRED sin job por detección; cero jobs.
    /// </summary>
    [Fact]
    [Trait("Spec", "T04")]
    public async Task Erp_down_leaves_detections_waiting_until_they_expire_with_erp_unavailable()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var erp = await ErpSimulatorServer.StartAsync(Ct);
        erp.Set(Order("WO-2", s.Clock.UtcNow));
        var (sync, http) = Sync(s, erp, timeoutMs: 200);
        using var _ = http;

        foreach (var cycle in new ulong[] { 200, 201, 202 })
        {
            Assert.Equal(DetectionStatus.WaitingErp, (await s.RegisterAsync(s.Cycle(cycle, workOrder: "WO-2"), ct: Ct)).FinalStatus);
        }

        erp.Mode = ErpMode.Unavailable;
        Assert.Equal(0, await sync.SyncOnceAsync(Ct));
        erp.Mode = ErpMode.Hang;
        Assert.Equal(0, await sync.SyncOnceAsync(Ct));
        Assert.Equal(0, await sync.SyncOnceAsync(Ct));   // circuito abierto: sin llamada
        Assert.True(s.Metrics.Total(MetricNames.ErpRequests, ("outcome", "unavailable")) >= 2);
        Assert.True(s.Metrics.Total(MetricNames.ErpRequests, ("outcome", "circuit_open")) >= 1);

        s.AdvanceMs(2_000);
        await sync.SyncOnceAsync(Ct);
        Assert.True(s.Metrics.Gauge(MetricNames.ErpCacheAgeSeconds, ("station", s.StationId)) >= 2);
        var report = await Sweep(s).SweepOnceAsync(Ct);
        Assert.Equal(3, report.WaitingErpExpired.Count);
        Assert.Equal(3, s.Metrics.Total(MetricNames.DetectionsExpired, ("reason", DetectionReason.ErpUnavailable)));

        var results = await s.ResultsAsync(ct: Ct);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(("EXPIRED", "ERP_UNAVAILABLE"), (r.GetProperty("outcome").GetString(), r.GetProperty("reason").GetString())));
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
    }

    /// <summary>
    /// T17 · ERP vuelve después del plazo: la detección espera con el plazo a 800 ms y el snapshot válido llega
    /// a los 2 s. La llegada no crea job; el barrido la vence con ERP_UNAVAILABLE; cero bytes.
    /// </summary>
    [Fact]
    [Trait("Spec", "T17")]
    public async Task Snapshot_arriving_after_the_deadline_does_not_create_a_job()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var erp = await ErpSimulatorServer.StartAsync(Ct);
        erp.Mode = ErpMode.Unavailable;
        var (sync, http) = Sync(s, erp);
        using var _ = http;

        var plazo = s.Revision.TransitBudgetMs - s.Revision.DeviceLeadTimeMs - s.Revision.ClockSkewToleranceMs!.Value;
        var record = s.Cycle(300, workOrder: "WO-3", detectionTimestamp: s.Clock.UtcNow.AddMilliseconds(800 - plazo));
        Assert.Equal(DetectionStatus.WaitingErp, (await s.RegisterAsync(record, ct: Ct)).FinalStatus);
        Assert.Equal(s.Clock.UtcNow.AddMilliseconds(800), (await s.DetectionAsync(300, Ct))!.DeadlineAt);
        await sync.SyncOnceAsync(Ct);

        s.AdvanceMs(2_000);
        erp.Mode = ErpMode.Up;
        erp.Set(Order("WO-3", s.Clock.UtcNow));
        Assert.Equal(0, await sync.SyncOnceAsync(Ct));                                // llegó, pero no crea job
        Assert.NotNull(await s.Erp.FindValidAsync("WO-3", s.Clock.UtcNow, Ct));        // el snapshot sí quedó en caché
        Assert.Equal(DetectionStatus.WaitingErp, (await s.DetectionAsync(300, Ct))!.Status);

        Assert.Single((await Sweep(s).SweepOnceAsync(Ct)).WaitingErpExpired);
        var d = (await s.DetectionAsync(300, Ct))!;
        Assert.Equal((DetectionStatus.Expired, DetectionReason.ErpUnavailable), (d.Status, d.Reason));
        var result = Assert.Single(await s.ResultsAsync(ct: Ct));
        Assert.Equal(("EXPIRED", "ERP_UNAVAILABLE"), (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString()));
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsExpired, ("reason", DetectionReason.ErpUnavailable)));
    }
}
