using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.IntegrationTests.Hardening;

/// <summary>Recirculación de cajas con identidad física (T16) y sin ella (T32), contra SQL Server real.</summary>
public sealed class RecirculationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f7recirc", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// T16 · CARTON_ID_REQUIRED. La caja C-77 tiene su job CONFIRMED y vuelve a pasar: la segunda detección queda BLOCKED con
    /// RECIRCULATED_CARTON (D05), sin job, con su PrintJobResult BLOCKED. Variante: dos ciclos de C-88 esperan snapshot en
    /// WAITING_ERP (registrados con CYCLE_ONLY, antes de activar CARTON_ID_REQUIRED); al llegar el snapshot, el primero
    /// crea el job (D06) y el segundo queda BLOCKED con RECIRCULATED_CARTON al repetir la validación en TX-E (D08).
    /// </summary>
    [Fact]
    [Trait("Spec", "T16")]
    public async Task A_recirculated_carton_with_its_id_is_blocked_and_never_gets_a_second_label()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { CartonIdentityMode = "CARTON_ID_REQUIRED" }, Ct);
        var first = await s.RegisterAsync(s.Cycle(70, cartonId: "C-77"), ct: Ct);
        Assert.Equal(DetectionStatus.JobCreated, first.FinalStatus);
        var job = first.PrintJobId!.Value;
        var claim = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
        await s.TransmitAsync(claim.AttemptId, Ct);
        await s.EvidenceAsync(s.Verified(70, (await s.JobAsync(job, Ct)).LabelSerial), Ct);
        Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(job, Ct)).Status);

        var again = await s.RegisterAsync(s.Cycle(71, cartonId: "C-77"), ct: Ct);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.RecirculatedCarton, (Guid?)null), (again.FinalStatus, again.FinalReason, again.PrintJobId));
        var blocked = (await s.ResultsAsync(ct: Ct)).Single(r => r.GetProperty("cycleId").GetString() == "71");
        Assert.Equal(("BLOCKED", "RECIRCULATED_CARTON", false), (blocked.GetProperty("outcome").GetString(), blocked.GetProperty("reason").GetString(),
            blocked.TryGetProperty("printJobId", out var p) && p.ValueKind != System.Text.Json.JsonValueKind.Null));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CartonId = 'C-77'", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.RecirculatedCarton)));

        // Variante con WAITING_ERP: los dos ciclos de C-88 se registran con CYCLE_ONLY y esperan snapshot.
        var v = await StationScenario.CreateAsync(_db, ct: Ct);
        var w1 = await v.RegisterAsync(v.Cycle(80, workOrder: "WO-RC", cartonId: "C-88"), ct: Ct);
        var w2 = await v.RegisterAsync(v.Cycle(81, workOrder: "WO-RC", cartonId: "C-88"), ct: Ct);
        Assert.Equal((DetectionStatus.WaitingErp, DetectionStatus.WaitingErp), (w1.FinalStatus, w2.FinalStatus));
        await v.Stations.ActivateRevisionAsync(StationScenario.DefaultRevision(v.StationId) with { RevisionNumber = 8, CartonIdentityMode = "CARTON_ID_REQUIRED" }, Ct);
        await v.AddSnapshotAsync("R1", "SKU-RC", "LOT-RC", Ct, "WO-RC");
        var snapshot = (await v.Erp.FindValidAsync("WO-RC", v.Clock.UtcNow, Ct))!;
        var arrivals = new ErpArrivalService(v.Txe, v.Stations, v.Serials, v.Settings, v.Clock, v.Metrics);
        var a1 = await arrivals.ProcessAsync((await v.DetectionAsync(80, Ct))!, snapshot, Ct);
        var a2 = await arrivals.ProcessAsync((await v.DetectionAsync(81, Ct))!, snapshot, Ct);
        Assert.Equal(DetectionStatus.JobCreated, a1!.FinalStatus);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.RecirculatedCarton, (Guid?)null), (a2!.FinalStatus, a2.FinalReason, a2.PrintJobId));
        Assert.Equal("BLOCKED", (await v.ResultsAsync(ct: Ct)).Single(r => r.GetProperty("cycleId").GetString() == "81").GetProperty("outcome").GetString());
        Assert.Equal(1, await v.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CartonId = 'C-88'", c => c.Parameters.AddWithValue("@st", v.StationId), Ct));
        Assert.Equal(1, v.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.RecirculatedCarton)));
    }

    /// <summary>
    /// T32 · CYCLE_ONLY y sin CartonId: la misma caja, ya etiquetada, genera un segundo ciclo válido. La segunda detección
    /// crea un segundo job y hay dos resultados con idempotencyKey distinta: sin identidad física la recirculación no es
    /// observable (comportamiento documentado de la garantía por ciclo).
    /// </summary>
    [Fact]
    [Trait("Spec", "T32")]
    public async Task Without_a_physical_id_a_recirculated_carton_gets_a_second_job()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var first = await s.RegisterAsync(s.Cycle(90), ct: Ct);
        var second = await s.RegisterAsync(s.Cycle(91), ct: Ct);
        Assert.Equal((DetectionStatus.JobCreated, DetectionStatus.JobCreated), (first.FinalStatus, second.FinalStatus));
        Assert.NotEqual(first.PrintJobId, second.PrintJobId);

        var detected = (await s.OutboxAsync(Ct)).Where(e => e.Type == "CartonDetected")
            .Select(e => System.Text.Json.JsonDocument.Parse(e.Payload).RootElement.GetProperty("idempotencyKey").GetString()).Distinct().ToList();
        Assert.Equal(2, detected.Count);

        // La caja recibe las dos etiquetas: dos resultados CONFIRMED con idempotencyKey distinta.
        foreach (var (job, cycle) in new[] { (first.PrintJobId!.Value, 90UL), (second.PrintJobId!.Value, 91UL) })
        {
            var claim = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
            await s.TransmitAsync(claim.AttemptId, Ct);
            await s.EvidenceAsync(s.Verified(cycle, (await s.JobAsync(job, Ct)).LabelSerial), Ct);
        }

        var confirmed = (await s.ResultsAsync(ct: Ct)).Where(r => r.GetProperty("outcome").GetString() == "CONFIRMED")
            .Select(r => r.GetProperty("idempotencyKey").GetString()).Distinct().ToList();
        Assert.Equal(2, confirmed.Count);
        Assert.Equal(2, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CartonId IS NULL", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.Equal(0, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.RecirculatedCarton)));
    }
}
