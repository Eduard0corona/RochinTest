using System.Net;
using System.Security.Cryptography;
using System.Text;
using CartonPrinting.Contracts;
using CartonPrinting.ErpSync;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Simulators.Erp;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Deadline;

/// <summary>
/// Regresiones de la revisión independiente de la Fase 3 en el lado ERP, contra SQL Server real: revisión del ERP
/// reutilizada con otro contenido (I10), una orden que falla no detiene a las demás, una TX-E sin efecto no se
/// mide y la revisión activa se exige en TX-E y en TX-B.
/// </summary>
public sealed class ErpReviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f3erprev", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ErpSnapshot Snapshot(string workOrder, string product, string lot, DateTime validFrom, DateTime now) =>
        new(workOrder, "R1", product, lot, null, validFrom, now.AddHours(8), now, SHA256.HashData(Encoding.UTF8.GetBytes(workOrder + product + lot)));

    private static ErpSnapshotSync Sync(StationScenario s, IErpClient client) =>
        new(s.Settings, client, s.Sql, s.Erp, new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics), s.Clock, s.Metrics,
            new ErpSyncOptions { TimeoutMs = 2_000 });

    /// <summary>
    /// MEDIA-5: la caché tiene (WO-X, R1, SKU-A, LOT-A), todavía no vigente, y el ERP responde (WO-X, R1, SKU-B, LOT-B).
    /// La respuesta no se usa (revisión reutilizada con otro contenido); cuando la fila de la caché es vigente, el job se
    /// arma con ella: su producto y su lote son los de la fila a la que apunta FK_PrintJob_Erp.
    /// </summary>
    [Fact]
    public async Task A_reused_erp_revision_with_other_content_is_not_used_and_the_job_matches_the_cache_row()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var now = s.Clock.UtcNow;
        await s.Erp.UpsertAsync(Snapshot("WO-X", "SKU-A", "LOT-A", now.AddMilliseconds(50), now), Ct);
        await using var erp = await ErpSimulatorServer.StartAsync(Ct);
        erp.Set(new ErpWorkOrder { WorkOrderId = "WO-X", Revision = "R1", ProductId = "SKU-B", LotId = "LOT-B", ValidFrom = now.AddHours(-1), ValidUntil = now.AddHours(8) });
        using var http = new HttpClient { BaseAddress = new Uri(erp.BaseUrl) };
        var sync = Sync(s, new HttpErpClient(http, s.Clock));

        Assert.Equal(DetectionStatus.WaitingErp, (await s.RegisterAsync(s.Cycle(800, workOrder: "WO-X"))).FinalStatus);
        Assert.Equal(0, await sync.SyncOnceAsync(Ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ErpRequests, ("outcome", "revision_conflict")));
        Assert.Equal(DetectionStatus.WaitingErp, (await s.DetectionAsync(800, Ct))!.Status);

        s.AdvanceMs(60);
        Assert.Equal(1, await sync.SyncOnceAsync(Ct));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CycleId = 800 AND ProductId = 'SKU-A' AND LotId = 'LOT-A'",
            c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
    }

    /// <summary>MEDIA-5: TX-E rechaza un plan cuya etiqueta no tiene el contenido de la fila de la caché.</summary>
    [Fact]
    public async Task Tx_e_does_not_create_a_job_whose_content_differs_from_the_cache_row()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var now = s.Clock.UtcNow;
        var waiting = await s.RegisterAsync(s.Cycle(810, workOrder: "WO-Y"));
        await s.Erp.UpsertAsync(Snapshot("WO-Y", "SKU-C", "LOT-C", now.AddHours(-1), now), Ct);
        var arrivals = new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics);

        var detection = (await s.DetectionAsync(810, Ct))!;
        Assert.Null(await arrivals.ProcessAsync(detection, Snapshot("WO-Y", "SKU-C", "LOT-OTRO", now.AddHours(-1), now), Ct));
        Assert.Equal(DetectionStatus.WaitingErp, (await s.DetectionAsync(810, Ct))!.Status);
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE DetectionId = @d", c => c.Parameters.AddWithValue("@d", waiting.DetectionId), Ct));

        Assert.NotNull(await arrivals.ProcessAsync(detection, (await s.Erp.FindValidAsync("WO-Y", now, Ct))!, Ct));
        Assert.Equal(DetectionStatus.JobCreated, (await s.DetectionAsync(810, Ct))!.Status);
    }

    /// <summary>BAJA-6: una orden que falla no detiene la pasada; las órdenes van en orden de plazo.</summary>
    [Fact]
    public async Task An_order_that_fails_does_not_stop_the_others()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var now = s.Clock.UtcNow;
        await s.RegisterAsync(s.Cycle(820, workOrder: "WO-MALA"));
        s.AdvanceMs(10);
        await s.RegisterAsync(s.Cycle(821, workOrder: "WO-BUENA"));
        var client = new ScriptedErpClient(id => id == "WO-MALA"
            ? throw new InvalidOperationException("falla no prevista")
            : Snapshot(id, "SKU-OK", "LOT-OK", now.AddHours(-1), now));

        Assert.Equal(1, await Sync(s, client).SyncOnceAsync(Ct));
        Assert.Equal(["WO-MALA", "WO-BUENA"], client.Calls);
        Assert.Equal(DetectionStatus.WaitingErp, (await s.DetectionAsync(820, Ct))!.Status);
        Assert.Equal(DetectionStatus.JobCreated, (await s.DetectionAsync(821, Ct))!.Status);
    }

    /// <summary>BAJA-7: dos llegadas para la misma detección con la estación bloqueada: una sola transición medida.</summary>
    [Fact]
    public async Task A_tx_e_without_effect_is_not_counted()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var now = s.Clock.UtcNow;
        await s.RegisterAsync(s.Cycle(830, workOrder: "WO-Z"));
        await s.Erp.UpsertAsync(Snapshot("WO-Z", "SKU-Z", "LOT-Z", now.AddHours(-1), now), Ct);
        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.ClockJump, "SYSTEM", null, now, Ct);
        var arrivals = new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics);
        var snapshot = (await s.Erp.FindValidAsync("WO-Z", now, Ct))!;

        var first = await arrivals.ProcessAsync((await s.DetectionAsync(830, Ct))!, snapshot, Ct);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled), (first!.FinalStatus, first.FinalReason));
        Assert.Null(await arrivals.ProcessAsync((await s.DetectionAsync(830, Ct))!, snapshot, Ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.StationDisabled)));
    }

    /// <summary>
    /// BAJA-8: el plazo efectivo se calcula con la revisión activa leída fuera de la transacción; si se activó otra
    /// entre la lectura y TX-E o TX-B, la transacción no actúa (TX-B informa RevisionChanged) y se reintenta.
    /// </summary>
    [Fact]
    public async Task Tx_e_and_tx_b_require_the_revision_they_were_computed_with()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var now = s.Clock.UtcNow;
        var stale = s.Revision.ConfigRevisionId;
        var ready = await s.ReadyJobAsync(840, Ct);
        await s.RegisterAsync(s.Cycle(841, workOrder: "WO-R"));
        await s.Erp.UpsertAsync(Snapshot("WO-R", "SKU-R", "LOT-R", now.AddHours(-1), now), Ct);
        var snapshot = (await s.Erp.FindValidAsync("WO-R", now, Ct))!;
        var detection = (await s.DetectionAsync(841, Ct))!;
        var serial = LabelSerialSource.Format(s.SerialPrefix, await s.Serials.NextAsync(Ct));
        var label = LabelRenderer.Render(s.Revision, snapshot, detection.Identity, serial, null);
        var plan = new ErpArrivalPlan(detection.DetectionId, new JobInsert(Guid.NewGuid(), s.Revision.LabelType, s.Revision.PrinterId, "WO-R",
                snapshot.Revision, snapshot.ProductId, snapshot.LotId, serial, s.Revision.TemplateRevision, label.Payload, label.PayloadHash,
                detection.DeadlineAt),
            false, detection.DeadlineAt, s.Revision.SendMarginMs, s.Revision.LabelType, now, snapshot.Gtin, stale);
        var job = await s.JobAsync(ready, Ct);

        var active = await s.Stations.ActivateRevisionAsync(StationScenario.DefaultRevision(s.StationId) with
        {
            RevisionNumber = 8, ChangeReason = "Revisión nueva",
        }, Ct);

        var arrival = await s.Txe.ExecuteAsync(plan, Ct);
        Assert.Equal((DetectionStatus.WaitingErp, (Guid?)null), (arrival.FinalStatus, arrival.PrintJobId));

        var request = new ClaimRequest(ready, Guid.NewGuid(), s.Lease.OwnerInstanceId, s.Lease.FencingToken, now, job.DeadlineAt, 100, stale);
        Assert.False((await s.Txb.ExecuteAsync(request, Ct)).Claimed);
        Assert.Equal(ClaimRejection.RevisionChanged, await s.Txb.DiagnoseAsync(request, Ct));
        Assert.True((await s.Txb.ExecuteAsync(request with { AttemptId = Guid.NewGuid(), ActiveConfigRevisionId = active.ConfigRevisionId }, Ct)).Claimed);
    }

    private sealed class ScriptedErpClient(Func<string, ErpSnapshot?> answer) : IErpClient
    {
        public List<string> Calls { get; } = [];

        public Task<ErpSnapshot?> GetSnapshotAsync(string workOrderId, CancellationToken ct)
        {
            Calls.Add(workOrderId);
            return Task.FromResult(answer(workOrderId));
        }
    }
}

/// <summary>
/// BAJA-6 de la revisión de la Fase 3: el cliente ERP trunca la vigencia antes de validarla y valida el GTIN y el
/// largo de cada campo contra su columna.
/// </summary>
public sealed class ErpClientValidationTests
{
    private const string Valid = """
        {"workOrderId":"WO-1","revision":"R1","productId":"SKU-1","lotId":"LOT-1","gtin":"07501234567893",
         "validFrom":"2026-10-14T12:00:00.000Z","validUntil":"2026-10-14T20:00:00.000Z"}
        """;

    private static Task<ErpSnapshot?> GetAsync(string body)
    {
        var http = new HttpClient(new FixedHandler(body)) { BaseAddress = new Uri("http://127.0.0.1/") };
        return new HttpErpClient(http, new EdgeClock(TimeProvider.System)).GetSnapshotAsync("WO-1", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_valid_snapshot_is_accepted()
    {
        var snapshot = (await GetAsync(Valid))!;
        Assert.Equal(("SKU-1", "LOT-1", "07501234567893"), (snapshot.ProductId, snapshot.LotId, snapshot.Gtin));
    }

    public static TheoryData<string, string> Invalid => new()
    {
        // Misma vigencia al truncar a milisegundos.
        { "\"validFrom\":\"2026-10-14T12:00:00.000Z\",\"validUntil\":\"2026-10-14T20:00:00.000Z\"",
          "\"validFrom\":\"2026-10-14T12:00:00.0001Z\",\"validUntil\":\"2026-10-14T12:00:00.0009Z\"" },
        { "\"gtin\":\"07501234567893\"", "\"gtin\":\"ABCDEFGHIJKLMN\"" },                       // GTIN sin dígitos
        { "\"lotId\":\"LOT-1\"", $"\"lotId\":\"{new string('L', 129)}\"" },                     // LotId NVARCHAR(128)
        { "\"productId\":\"SKU-1\"", $"\"productId\":\"{new string('P', 129)}\"" },             // ProductId NVARCHAR(128)
        { "\"revision\":\"R1\"", $"\"revision\":\"{new string('R', 65)}\"" },                    // Revision NVARCHAR(64)
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task An_invalid_snapshot_is_rejected_as_unavailable(string original, string replacement)
    {
        Assert.Contains(original, Valid);
        await Assert.ThrowsAsync<ErpUnavailableException>(() => GetAsync(Valid.Replace(original, replacement)));
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
