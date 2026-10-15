using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

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
    /// WAITING_ERP (registrados con CYCLE_ONLY, antes de activar CARTON_ID_REQUIRED: con CARTON_ID_REQUIRED desde el principio, TX-A
    /// bloquea el segundo y la variante del catálogo no se alcanza, SI-19); al llegar el snapshot, el primero crea el job (D06) y el
    /// segundo queda BLOCKED con RECIRCULATED_CARTON al repetir la validación en TX-E (D08).
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
    /// T16 · La validación de TX-A con bloqueo de rango, bajo concurrencia: dos ciclos de C-99 se registran a la vez. Una sesión
    /// retiene el rango de IX_PrintJob_Carton de la caja hasta que las dos TX-A insertaron su detección y esperan ese rango; al
    /// soltarlo, cada una necesita lo que la otra tiene (el rango de PrintJob y la clave de su detección en IX_CartonDetection_Carton):
    /// SQL Server elige una víctima (1205), que se reintenta completa: un solo job y la otra detección BLOCKED con RECIRCULATED_CARTON.
    /// Cuál de las dos lleva el job depende de la víctima: un job vivo bloquea a cualquier otra detección de la caja, anterior o no
    /// (límite registrado en SI-17).
    /// </summary>
    [Fact]
    [Trait("Spec", "T16")]
    public async Task Two_cycles_of_the_same_carton_registered_at_once_end_with_one_job_after_a_deadlock_retry()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { CartonIdentityMode = "CARTON_ID_REQUIRED" }, Ct);
        var (a, b) = await WhileHoldingCartonRangeAsync(s, "C-99", 2,
            () => s.RegisterAsync(s.Cycle(100, cartonId: "C-99"), ct: Ct),
            () => s.RegisterAsync(s.Cycle(101, cartonId: "C-99"), ct: Ct));

        Assert.Equal([DetectionStatus.Blocked, DetectionStatus.JobCreated], new[] { a.FinalStatus, b.FinalStatus }.Order());
        Assert.Equal(DetectionReason.RecirculatedCarton, new[] { a, b }.Single(r => r.FinalStatus == DetectionStatus.Blocked).FinalReason);
        Assert.True(s.Sql.DeadlockRetries >= 1, "Ninguna TX-A fue víctima de un interbloqueo: la prueba no ejercitó el reintento del 1205.");
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CartonId = 'C-99'", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.RecirculatedCarton)));
    }

    /// <summary>
    /// T16 · TX-A y TX-E a la vez sobre la misma caja: C-98 espera snapshot (WAITING_ERP) y, cuando el snapshot llega, pasa un
    /// segundo ciclo de C-98. Las dos transacciones esperan el rango retenido y siguen a la vez al soltarlo. Cualquiera sea el orden
    /// (o la víctima de un 1205), el resultado es el de la serie: la primera detección crea el job (D06) y la segunda queda BLOCKED con
    /// RECIRCULATED_CARTON (D05).
    /// </summary>
    [Fact]
    [Trait("Spec", "T16")]
    public async Task A_snapshot_arrival_and_a_new_cycle_of_the_same_carton_at_once_end_with_one_job()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { CartonIdentityMode = "CARTON_ID_REQUIRED" }, Ct);
        var waiting = await s.RegisterAsync(s.Cycle(110, workOrder: "WO-RX", cartonId: "C-98"), ct: Ct);
        Assert.Equal(DetectionStatus.WaitingErp, waiting.FinalStatus);
        await s.AddSnapshotAsync("R1", "SKU-RX", "LOT-RX", Ct, "WO-RX");
        var snapshot = (await s.Erp.FindValidAsync("WO-RX", s.Clock.UtcNow, Ct))!;
        var arrivals = new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics);
        var detection = (await s.DetectionAsync(110, Ct))!;

        var (arrival, again) = await WhileHoldingCartonRangeAsync(s, "C-98", 2,
            () => arrivals.ProcessAsync(detection, snapshot, Ct),
            () => s.RegisterAsync(s.Cycle(111, workOrder: "WO-RX", cartonId: "C-98"), ct: Ct));

        Assert.Equal(DetectionStatus.JobCreated, arrival!.FinalStatus);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.RecirculatedCarton, (Guid?)null), (again.FinalStatus, again.FinalReason, again.PrintJobId));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CartonId = 'C-98'", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
    }

    /// <summary>
    /// SI-04 en TX-E (interino de falla cerrada, segunda revisión de la Fase 7, BAJA-8): una detección registrada con CYCLE_ONLY y sin
    /// CartonId espera snapshot; se activa CARTON_ID_REQUIRED. Al llegar el snapshot, sin CartonId no se puede validar la
    /// recirculación y D08 no tiene un motivo para eso: TX-E no crea el job, la detección sigue en WAITING_ERP y el barrido la vence
    /// con D07 a su plazo, como en TX-A, que la bloquea.
    /// </summary>
    [Fact]
    public async Task Without_its_carton_id_a_waiting_detection_gets_no_job_once_carton_id_required_is_effective()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        Assert.Equal(DetectionStatus.WaitingErp, (await s.RegisterAsync(s.Cycle(120, workOrder: "WO-NC"), ct: Ct)).FinalStatus);
        await s.Stations.ActivateRevisionAsync(StationScenario.DefaultRevision(s.StationId) with { RevisionNumber = 8, CartonIdentityMode = "CARTON_ID_REQUIRED" }, Ct);
        await s.AddSnapshotAsync("R1", "SKU-NC", "LOT-NC", Ct, "WO-NC");
        var snapshot = (await s.Erp.FindValidAsync("WO-NC", s.Clock.UtcNow, Ct))!;
        var arrivals = new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics);

        Assert.Null(await arrivals.ProcessAsync((await s.DetectionAsync(120, Ct))!, snapshot, Ct));
        Assert.Equal(DetectionStatus.WaitingErp, (await s.DetectionAsync(120, Ct))!.Status);
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));

        s.Advance(TimeSpan.FromSeconds(5));
        await new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics).SweepOnceAsync(Ct);
        Assert.Equal(DetectionStatus.Expired, (await s.DetectionAsync(120, Ct))!.Status);
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
    }

    /// <summary>
    /// Retiene el rango de IX_PrintJob_Carton de la caja (la misma lectura de la validación) hasta que <paramref name="waiters"/>
    /// sesiones esperan un lock en la base; lo suelta y devuelve los dos resultados.
    /// </summary>
    private static async Task<(T1, T2)> WhileHoldingCartonRangeAsync<T1, T2>(StationScenario s, string cartonId, int waiters, Func<Task<T1>> first,
        Func<Task<T2>> second)
    {
        await using var holder = new SqlConnection(s.Db.AppConnectionString);
        await holder.OpenAsync(Ct);
        var tx = holder.BeginTransaction();
        await using (var hold = new SqlCommand(
            "SELECT TOP (1) PrintJobId FROM dbo.PrintJob WITH (UPDLOCK, HOLDLOCK) WHERE SiteId = @site AND CartonId = @carton;", holder, tx))
        {
            hold.Parameters.AddWithValue("@site", StationScenario.SiteId);
            hold.Parameters.AddWithValue("@carton", cartonId);
            await hold.ExecuteScalarAsync(Ct);
        }

        var a = Task.Run(first, Ct);
        var b = Task.Run(second, Ct);
        await using (var admin = new SqlConnection(s.Db.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                await using var cmd = new SqlCommand("""
                    SELECT COUNT(DISTINCT request_session_id) FROM sys.dm_tran_locks
                    WHERE request_status = 'WAIT' AND resource_database_id = DB_ID(@db)
                    """, admin);
                cmd.Parameters.AddWithValue("@db", s.Db.Name);
                if ((int)(await cmd.ExecuteScalarAsync(Ct))! >= waiters)
                {
                    break;
                }

                Assert.True(DateTime.UtcNow < deadline, "Las transacciones no llegaron a esperar el rango retenido.");
                await Task.Delay(20, Ct);
            }
        }

        await tx.RollbackAsync(Ct);
        return (await a, await b);
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
