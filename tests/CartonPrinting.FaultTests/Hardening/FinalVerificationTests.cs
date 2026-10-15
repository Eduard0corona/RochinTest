using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// Hallazgos MEDIA de la verificación final de la Fase 7 (revisión por dimensiones con refutación adversarial), contra SQL Server
/// real y la impresora simulada: LabelingReady con la impresora de línea, la pausa por fallos seguidos del dispositivo, TX-R0 después
/// de un claim sin respuesta, TX-A y TX-E frente a un bloqueo de la estación en curso, y los tramos de evidencia monotónicos.
/// </summary>
[Collection(nameof(CartonPrinting.FaultTests.Hardening.BrokerOutageCollection))]
public sealed class FinalVerificationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f7final", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// LabelingReady sube solo con la impresora de línea tomada y con TX-R0 hecho (ESTRATEGIA_RECUPERACION §1, etapas 2, 3 y 8), baja
    /// con su sender en pausa (§3 Impresora) y vuelve a bajar cuando el sender termina (lease perdido), hasta volver a tomarla.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task LabelingReady_waits_for_the_line_printer_taken_with_tx_r0_and_drops_while_its_sender_is_paused_or_gone()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        using var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: Path.Combine(Path.GetTempPath(), "cp-locks-" + Guid.NewGuid().ToString("N")));
        var runner = new OwnedPrinterRunner(ownership, new SenderRecovery(new Txr0Recovery(s.Sql), s.Clock, s.Metrics), s.Clock);
        var readiness = new LinePrinterReadiness();
        readiness.Attach(() => runner.InService);
        var heartbeat = new LabelingReadyHeartbeat(s.Settings, s.Stations, new IngestScenario.RecordingSink(), s.Metrics, linePrinter: readiness);

        Assert.False(await heartbeat.BeatAsync(Ct));
        Assert.True(await runner.AcquireAsync(Ct));
        Assert.False(await heartbeat.BeatAsync(Ct));   // tomada, todavía sin TX-R0
        await runner.RecoverAsync(Ct);
        Assert.True(await heartbeat.BeatAsync(Ct));

        readiness.SetPaused(true);
        Assert.False(await heartbeat.BeatAsync(Ct));
        readiness.SetPaused(false);
        Assert.True(await heartbeat.BeatAsync(Ct));

        // El sender termina (como al perder el lease): en falso hasta volver a tomarla con TX-R0.
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync((_, _) =>
        {
            ran.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await WaitAsync(() => Task.FromResult(!runner.InService), "la impresora fuera de servicio al terminar el sender");
        Assert.False(await heartbeat.BeatAsync(Ct));
        await stop.CancelAsync();
        await run.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// "Tres fallos seguidos | Sender en pausa, LabelingReady en falso, alerta" (ESTRATEGIA_RECUPERACION §3, Impresora). La impresora
    /// rechaza conexiones: tres jobs fallan seguidos y el sender entra en pausa. No reclama el cuarto mientras dura. Cuando la
    /// impresora vuelve a aceptar conexiones, sigue con la siguiente caja.
    /// </summary>
    [Fact]
    [Trait("Spec", "T05")]
    public async Task Three_consecutive_device_failures_pause_the_sender_until_the_printer_answers_again()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 60_000 }, Ct, TimeProvider.System);
        var readiness = new LinePrinterReadiness();
        readiness.Attach(() => true);
        await using var rig = PrintRig.Create(s, readiness: readiness);
        rig.Printer.Mode = PrinterMode.RejectConnections;
        for (ulong cycle = 900; cycle <= 902; cycle++)
        {
            await s.ReadyJobAsync(cycle, Ct);
        }

        var later = await s.ReadyJobAsync(903, Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sender = rig.Sender.RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, stop.Token);
        try
        {
            await WaitAsync(() => Task.FromResult(rig.Sender.Paused), "el sender en pausa");
            Assert.True(readiness.Paused);
            Assert.Equal(1d, s.Metrics.Gauge(MetricNames.PrinterSenderPaused, ("printer", s.PrinterId)));
            Assert.Equal(1, s.Metrics.Total(MetricNames.PrinterSenderPauses, ("printer", s.PrinterId)));
            await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
            Assert.Equal(JobStatus.Ready, (await s.JobAsync(later, Ct)).Status);

            rig.Printer.Mode = PrinterMode.Normal;
            await WaitAsync(async () => (await s.JobAsync(later, Ct)).Status is not (JobStatus.Ready or JobStatus.Sending), "el job siguiente al volver la impresora");
            Assert.False(rig.Sender.Paused);
            Assert.False(readiness.Paused);
            Assert.Equal(0d, s.Metrics.Gauge(MetricNames.PrinterSenderPaused, ("printer", s.PrinterId)));
        }
        finally
        {
            await stop.CancelAsync();
            await sender.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Un sender nuevo (el ciclo de propiedad lo crea al volver a tomar la impresora, por ejemplo después de perder el lease estando en
    /// pausa) hereda la pausa de la estación: la sigue probando y la levanta cuando la impresora responde. Sin eso, LabelingReady
    /// quedaría en falso para siempre (verificación de las correcciones, MEDIA).
    /// </summary>
    [Fact]
    [Trait("Spec", "T05")]
    public async Task A_new_sender_inherits_the_pause_and_lifts_it_when_the_printer_answers()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 60_000 }, Ct, TimeProvider.System);
        var readiness = new LinePrinterReadiness();
        readiness.Attach(() => true);
        readiness.SetPaused(true);   // la pausa que dejó el sender anterior
        await using var rig = PrintRig.Create(s, readiness: readiness);
        Assert.True(rig.Sender.Paused);
        var job = await s.ReadyJobAsync(950, Ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sender = rig.Sender.RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, stop.Token);
        try
        {
            await WaitAsync(() => Task.FromResult(!readiness.Paused), "la pausa heredada levantada");
            await WaitAsync(async () => (await s.JobAsync(job, Ct)).Status is not (JobStatus.Ready or JobStatus.Sending), "el job después de la pausa");
        }
        finally
        {
            await stop.CancelAsync();
            await sender.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Una impresora que acepta conexiones pero corta la escritura: aceptar una conexión no prueba que imprima. Después de la pausa, el
    /// período de prueba deja un solo intento: el primer fallo vuelve a pausar, con la espera duplicada (verificación de las
    /// correcciones, MEDIA). Las cajas siguientes esperan en READY en lugar de ir perdiéndose de a tres.
    /// </summary>
    [Fact]
    [Trait("Spec", "T06")]
    public async Task A_printer_that_cuts_every_write_costs_one_attempt_per_probation_with_a_growing_pause()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 120_000 }, Ct, TimeProvider.System);
        var readiness = new LinePrinterReadiness();
        readiness.Attach(() => true);
        await using var rig = PrintRig.Create(s, readiness: readiness);
        rig.Printer.Mode = PrinterMode.CutMidWrite;
        var jobs = new List<Guid>();
        for (ulong cycle = 960; cycle <= 964; cycle++)
        {
            jobs.Add(await s.ReadyJobAsync(cycle, Ct));
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sender = rig.Sender.RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, stop.Token);
        try
        {
            await WaitAsync(() => Task.FromResult(s.Metrics.Total(MetricNames.PrinterSenderPauses, ("printer", s.PrinterId)) == 1), "la primera pausa");
            var firstPause = DateTime.UtcNow;
            await WaitAsync(() => Task.FromResult(s.Metrics.Total(MetricNames.PrinterSenderPauses, ("printer", s.PrinterId)) == 2), "la segunda pausa");
            Assert.True(DateTime.UtcNow - firstPause >= PrinterSenderService.MinPause - TimeSpan.FromMilliseconds(200),
                "la pausa se levantó antes de la espera mínima");
            Assert.True(readiness.Paused);

            // Tres fallos, la pausa, un solo intento de prueba y otra pausa: cuatro jobs UNKNOWN y el quinto esperando.
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            var statuses = new List<string>();
            foreach (var job in jobs)
            {
                statuses.Add((await s.JobAsync(job, Ct)).Status);
            }

            Assert.Equal([JobStatus.Unknown, JobStatus.Unknown, JobStatus.Unknown, JobStatus.Unknown, JobStatus.Ready], statuses);
        }
        finally
        {
            await stop.CancelAsync();
            await sender.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Un claim cuyo commit se confirmó y cuya respuesta se perdió deja el intento en SENDING sin escritura, y la impresora ocupada.
    /// Después de un error fuera de la escritura, el sender ejecuta TX-R0 de su impresora antes de reclamar: el SENDING huérfano pasa a
    /// UNKNOWN (RESTART_DURING_SEND, ESTRATEGIA_RECUPERACION §2) y el job siguiente sale, sin reiniciar el proceso.
    /// </summary>
    [Fact]
    [Trait("Spec", "T07")]
    public async Task A_claim_whose_answer_was_lost_is_closed_by_tx_r0_before_the_next_claim()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 60_000 }, Ct, TimeProvider.System);
        var orphan = await s.ReadyJobAsync(910, Ct);
        Assert.True((await s.Claims.TryClaimAsync(orphan, s.Lease, Ct))?.Claimed);
        var next = await s.ReadyJobAsync(911, Ct);

        var direct = new SqlConnectionStringBuilder(s.Db.AppConnectionString);
        var parts = direct.DataSource.Replace("tcp:", string.Empty, StringComparison.Ordinal).Split(',');
        await using var proxy = new TcpProxy(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)).Start();
        proxy.AcceptNew = false;
        var db = new SqlDb(new DatabaseOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(s.Db.AppConnectionString)
            {
                DataSource = $"127.0.0.1,{proxy.Port}", ConnectTimeout = 2, Pooling = false, ConnectRetryCount = 0,
            }.ConnectionString,
        });
        await using var printer = PrinterSimulator.Start();
        await using var transport = new TcpPrinterTransport(new PrinterTransportOptions
        {
            Port = printer.Port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = 500,
        }, s.Provider);
        var sender = new PrinterSenderService(s.PrinterId, db, new ClaimService(db, new TxbClaim(db), s.Metrics, s.Clock, new ClockGuard(s.Clock)),
            new TxcWriteResult(db), new StationConfigStore(db), s.Lease, transport, s.Clock, s.Metrics,
            recovery: new SenderRecovery(new Txr0Recovery(db), s.Clock, s.Metrics));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = sender.RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, stop.Token);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);   // al menos un error fuera de la escritura
            proxy.AcceptNew = true;
            await WaitAsync(async () => (await s.JobAsync(next, Ct)).Status is not (JobStatus.Ready or JobStatus.Sending), "el job siguiente");
        }
        finally
        {
            await stop.CancelAsync();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        var closed = await s.JobAsync(orphan, Ct);
        Assert.Equal((JobStatus.Unknown, "RESTART_DURING_SEND"), (closed.Status, closed.Reason));
        var attempts = await s.QueryAsync("SELECT Outcome, ISNULL(FailureDetail, '-') FROM dbo.PrintAttempt WHERE PrintJobId = @j ORDER BY AttemptNumber",
            c => c.Parameters.AddWithValue("@j", next), r => $"{r.GetString(0)}: {r.GetString(1)}", Ct);
        Assert.True((await s.JobAsync(next, Ct)).Status == JobStatus.Transmitted, $"intentos del job siguiente: {string.Join(" | ", attempts)}");
        Assert.Single(printer.Received);
    }

    /// <summary>
    /// Un bloqueo de la estación en curso (como el de CLOCK_JUMP, que vence lo pendiente en la misma transacción, SI-16) queda en orden
    /// con TX-A y con TX-E: las dos leen la estación con lock compartido hasta el commit, esperan el bloqueo y lo ven (D05 y D08 con
    /// STATION_DISABLED). Con RCSI y sin ese lock verían la estación habilitada y crearían un job que la purga no vence.
    /// </summary>
    [Fact]
    [Trait("Spec", "T37")]
    public async Task A_station_block_in_progress_makes_tx_a_and_tx_e_wait_and_see_it()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        Assert.Equal(DetectionStatus.WaitingErp, (await s.RegisterAsync(s.Cycle(919, workOrder: "WO-CJ"), ct: Ct)).FinalStatus);
        await s.AddSnapshotAsync("R1", "SKU-CJ", "LOT-CJ", Ct, "WO-CJ");
        var snapshot = (await s.Erp.FindValidAsync("WO-CJ", s.Clock.UtcNow, Ct))!;
        var waiting = (await s.DetectionAsync(919, Ct))!;
        var arrivals = new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics);

        await using var holder = new SqlConnection(s.Db.AppConnectionString);
        await holder.OpenAsync(Ct);
        var tx = holder.BeginTransaction();
        Assert.True(await StationConfigStore.BlockAsync(holder, tx, StationScenario.SiteId, s.StationId, BlockReason.ClockJump, "SYSTEM", null,
            s.Clock.UtcNow, Ct));

        var registering = Task.Run(() => s.RegisterAsync(s.Cycle(920), ct: Ct), Ct);
        var arriving = Task.Run(() => arrivals.ProcessAsync(waiting, snapshot, Ct), Ct);
        await WaitForLockWaitersAsync(s, 2);
        Assert.False(registering.IsCompleted || arriving.IsCompleted);
        await tx.CommitAsync(Ct);

        var registered = await registering;
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled, (Guid?)null),
            (registered.FinalStatus, registered.FinalReason, registered.PrintJobId));
        var arrived = await arriving;
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled), (arrived!.FinalStatus, arrived.FinalReason));
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
    }

    /// <summary>
    /// Los tramos de evidencia de la sección 10.4 son monotónicos. Se miden con el reloj del EdgeHost, desde la escritura hasta la
    /// recepción de la observación, nunca con el ObservedAt del dispositivo. Un aplicador con el reloj una hora atrasado y una
    /// impresora una hora adelantada no cambian send_start_to_applied_ms (cuyo p99 es DeviceLeadTimeMs) ni send_to_printed_ms.
    /// </summary>
    [Fact]
    [Trait("Spec", "T12")]
    public async Task Evidence_segments_use_the_edge_host_clock_not_the_device_clock()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 60_000, MinConfirmationLevel = EvidenceLevel.Applied }, Ct,
            TimeProvider.System);
        await using var rig = PrintRig.Create(s, sources: new CartonPrinting.Simulators.Evidence.EvidenceSources(PrinterCompletion: false, Applicator: false, Scanner: false));
        var job = await s.ReadyJobAsync(930, Ct);
        Assert.Equal(JobStatus.Transmitted, (await rig.Sender.SendAsync(job, Ct)).Write?.JobStatus);

        await rig.Evidence.SubmitAsync(s.Printed(job) with { ObservedAt = s.Clock.UtcNow.AddHours(1) }, Ct);
        await rig.Evidence.SubmitAsync(s.Applied(930) with { ObservedAt = s.Clock.UtcNow.AddHours(-1) }, Ct);

        Assert.InRange(Assert.Single(s.Metrics.Samples(MetricNames.SendToPrintedMs)), 0, 5_000);
        Assert.InRange(Assert.Single(s.Metrics.Samples(MetricNames.SendStartToAppliedMs)), 0, 5_000);
        Assert.InRange(Assert.Single(s.Metrics.Samples(MetricNames.PrintedToAppliedMs)), 0, 5_000);
    }

    private static async Task WaitForLockWaitersAsync(StationScenario s, int waiters)
    {
        await using var admin = new SqlConnection(s.Db.AdminConnectionString);
        await admin.OpenAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var cmd = new SqlCommand("""
                SELECT COUNT(DISTINCT request_session_id) FROM sys.dm_tran_locks WHERE request_status = 'WAIT' AND resource_database_id = DB_ID(@db)
                """, admin);
            cmd.Parameters.AddWithValue("@db", s.Db.Name);
            if ((int)(await cmd.ExecuteScalarAsync(Ct))! >= waiters)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "TX-A y TX-E no esperaron el bloqueo de la estación en curso.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"No se cumplió a tiempo: {what}.");
            await Task.Delay(50, Ct);
        }
    }
}
