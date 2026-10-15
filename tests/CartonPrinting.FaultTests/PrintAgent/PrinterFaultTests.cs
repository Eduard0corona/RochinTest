using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.PrintAgent;

/// <summary>Fallas del canal y del dispositivo contra el sender real y la impresora simulada por TCP (Fase 4).</summary>
public sealed class PrinterFaultTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f4fault", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StationSweepService Sweep(StationScenario s) => new(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);

    /// <summary>
    /// T05 · La impresora no acepta conexiones (MaxAutoAttempts = 3, plazo suficiente): tres intentos NOT_SENT con
    /// SENDING → RETRY_WAIT → READY en ciclo y, al agotarlos, FAILED con PRINTER_UNAVAILABLE. El simulador
    /// recibe 0 bytes; ningún UNKNOWN.
    /// </summary>
    [Fact]
    [Trait("Spec", "T05")]
    public async Task Printer_refusing_connections_exhausts_attempts_with_zero_bytes_and_no_unknown()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 60_000, RetryBackoffMs = 100 }, Ct);
        await using var rig = PrintRig.Create(s);
        rig.Printer.Mode = PrinterMode.RejectConnections;
        var job = await s.ReadyJobAsync(10, Ct);

        var statuses = new List<string>();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var sent = await rig.SendAsync(job, Ct);
            Assert.False(sent.WriteInvoked);
            Assert.Equal(AttemptOutcome.NotSent, sent.Write!.AttemptOutcome);
            statuses.Add(sent.Write.JobStatus!);
            if (sent.Write.JobStatus == JobStatus.RetryWait)
            {
                s.AdvanceMs(150);
                Assert.Single((await Sweep(s).SweepOnceAsync(Ct)).Released);
                Assert.Equal(JobStatus.Ready, (await s.JobAsync(job, Ct)).Status);
            }
        }

        Assert.Equal([JobStatus.RetryWait, JobStatus.RetryWait, JobStatus.Failed], statuses);
        var row = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Failed, JobReason.PrinterUnavailable), (row.Status, row.Reason));
        Assert.All(await s.AttemptsAsync(job, Ct), a => Assert.Equal((AttemptOutcome.NotSent, false), (a.Outcome, a.MayHaveSent)));
        Assert.Equal(3, s.Metrics.Total(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.NotSent)));
        Assert.Equal(0, s.Metrics.Total(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.Unknown)));
        Assert.Equal((0L, 0), (rig.Printer.BytesReceived, rig.Printer.Connections));
        var result = (await s.ResultsAsync(job, Ct)).Single();
        Assert.Equal(("FAILED", "PRINTER_UNAVAILABLE"), (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString()));
    }

    /// <summary>
    /// T06 · Respuesta perdida después de transmitir: el dispositivo recibe e imprime y corta el canal sin acusar
    /// recibo. Intento UNKNOWN y job UNKNOWN con WRITE_INTERRUPTED (resultado no final); minutos después la lectura
    /// tardía del serial correcto lo confirma (A08, J15) con resultVersion mayor, mismo PrintJobId y LabelSerial.
    /// Cero retransmisiones; exactamente una etiqueta.
    /// </summary>
    [Fact]
    [Trait("Spec", "T06")]
    public async Task Lost_response_after_transmitting_is_unknown_and_reconciled_by_a_late_read_without_retransmission()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var rig = PrintRig.Create(s, sources: new EvidenceSources(PrinterCompletion: false));
        rig.Printer.Mode = PrinterMode.PrintThenCut;
        var job = await s.ReadyJobAsync(20, Ct);

        var sent = await rig.SendAsync(job, Ct);
        Assert.True(sent.WriteInvoked);
        var unknown = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.WriteInterrupted), (unknown.Status, unknown.Reason));
        Assert.Equal(AttemptOutcome.Unknown, Assert.Single(await s.AttemptsAsync(job, Ct)).Outcome);
        var first = (await s.ResultsAsync(job, Ct)).Single();
        Assert.Equal(("UNKNOWN", false), (first.GetProperty("outcome").GetString(), first.GetProperty("final").GetBoolean()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.PrinterUnknown, ("printer", s.PrinterId)));

        // Nada lo vuelve a enviar: el sender no encuentra nada READY, ni después del barrido.
        rig.Printer.Mode = PrinterMode.Normal;
        s.AdvanceMs(5 * 60_000);
        await Sweep(s).SweepOnceAsync(Ct);
        Assert.Null(await rig.Sender.SendNextAsync(Ct));

        await rig.EvidenceSim.ScanAsync(new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, 20).IdempotencyKey, unknown.LabelSerial, Ct);
        var confirmed = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Confirmed, unknown.PrintJobId, unknown.LabelSerial), (confirmed.Status, confirmed.PrintJobId, confirmed.LabelSerial));
        Assert.True(confirmed.ResultVersion > unknown.ResultVersion);
        Assert.Equal(AttemptOutcome.Confirmed, Assert.Single(await s.AttemptsAsync(job, Ct)).Outcome);
        Assert.Equal(1, rig.Printer.TransmissionsOf(unknown.LabelSerial));
        Assert.Single(rig.Printer.Printed);
        var final = (await s.ResultsAsync(job, Ct)).MaxBy(r => r.GetProperty("resultVersion").GetInt32());
        Assert.Equal(("CONFIRMED", confirmed.ResultVersion), (final.GetProperty("outcome").GetString(), final.GetProperty("resultVersion").GetInt32()));
        Assert.Equal(0, s.Metrics.Total(MetricNames.DuplicatePhysicalTransmissions));
    }

    /// <summary>
    /// T29 · Suspensión después del claim: con el plazo a 500 ms, el sender se suspende 2 s entre el commit del
    /// claim y la escritura. La segunda comprobación con el reloj monotónico rechaza: intento NOT_SENT, job
    /// EXPIRED (J08), cero bytes. Límite: una pausa entre la comprobación y la llamada de escritura no se puede
    /// excluir por software (SendMarginMs debe cubrirla).
    /// </summary>
    [Fact]
    [Trait("Spec", "T29")]
    public async Task Pause_after_the_claim_is_caught_by_the_monotonic_presend_check()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var pause = new PauseFaults(s);
        await using var rig = PrintRig.Create(s, faults: pause);
        var plazo = s.Revision.TransitBudgetMs - s.Revision.DeviceLeadTimeMs - s.Revision.ClockSkewToleranceMs!.Value;
        var registered = await s.RegisterAsync(s.Cycle(30, detectionTimestamp: s.Clock.UtcNow.AddMilliseconds(500 - plazo)), ct: Ct);
        var job = registered.PrintJobId!.Value;
        Assert.Equal(s.Clock.UtcNow.AddMilliseconds(500), (await s.JobAsync(job, Ct)).DeadlineAt);

        var sent = await rig.SendAsync(job, Ct);
        Assert.True(pause.Paused);
        Assert.False(sent.WriteInvoked);
        Assert.Equal((AttemptOutcome.NotSent, JobStatus.Expired, JobReason.DeadlinePassed),
            (sent.Write!.AttemptOutcome, sent.Write.JobStatus, sent.Write.JobReason));
        Assert.Equal(0L, rig.Printer.BytesReceived);
        Assert.Equal(1, s.Metrics.Total(MetricNames.PresendCheckRejected, ("cause", "deadline")));
        var result = (await s.ResultsAsync(job, Ct)).Single();
        Assert.Equal(("EXPIRED", "DEADLINE_PASSED"), (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString()));

        // Sin pausa, el envío normal mide presend_check_to_write_ms.
        pause.Enabled = false;
        var normal = await s.ReadyJobAsync(31, Ct);
        Assert.Equal(AttemptOutcome.Transmitted, (await rig.SendAsync(normal, Ct)).Write!.AttemptOutcome);
        Assert.NotEmpty(s.Metrics.Samples(MetricNames.PresendCheckToWriteMs));
    }

    /// <summary>
    /// T31 · Etiqueta retenida en la cola del dispositivo (APPLIED): bytes transmitidos a tiempo, el dispositivo
    /// la retiene hasta después de la ventana. Sin APPLIED a tiempo: UNKNOWN con EVIDENCE_WINDOW_ELAPSED.
    /// Variante 1: un "no impreso" sin confirmación de cancelación no cambia nada (sigue UNKNOWN). Variante 2:
    /// cancelación confirmada y cola sin el job: FAILED con DEVICE_REPORTED_NOT_PRINTED. Nunca CONFIRMED sin
    /// evidencia del nivel exigido.
    /// </summary>
    [Fact]
    [Trait("Spec", "T31")]
    public async Task Label_held_in_the_device_queue_is_never_declared_applied()
    {
        // Sin evidencia APPLIED a tiempo, y variante 1.
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Applied }, Ct);
        await using (var rig = PrintRig.Create(s, new PrinterCapabilities(ConfirmsCancellation: false), new EvidenceSources(PrinterCompletion: true)))
        {
            var control = new DeviceCancellation(new SimulatedDeviceControl(rig.Printer), s.Sql, s.Txd, s.Clock);
            var ok = await s.ReadyJobAsync(40, Ct);              // referencia: impresa y aplicada a tiempo
            rig.EvidenceSim.Sources = new EvidenceSources(PrinterCompletion: true, Applicator: true);
            await rig.SendAsync(ok, Ct);
            Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(ok, Ct)).Status);

            rig.EvidenceSim.Sources = new EvidenceSources(PrinterCompletion: true);
            rig.Printer.Mode = PrinterMode.Hold;
            var held = await s.ReadyJobAsync(41, Ct);
            await rig.SendAsync(held, Ct);
            var heldRow = await s.JobAsync(held, Ct);
            Assert.Equal(JobStatus.Transmitted, heldRow.Status);
            Assert.Single(rig.Printer.Queue);

            Assert.False(await control.CancelAsync(held, Ct));   // "no impreso" sin confirmar cancelación
            Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(held, Ct)).Status);

            s.AdvanceMs(s.Revision.EvidenceWindowMs!.Value + 10);
            await Sweep(s).SweepOnceAsync(Ct);
            Assert.Equal((JobStatus.Unknown, JobReason.EvidenceWindowElapsed), ((await s.JobAsync(held, Ct)).Status, (await s.JobAsync(held, Ct)).Reason));
            Assert.Equal(("UNKNOWN", false), ((await s.ResultsAsync(held, Ct)).Last().GetProperty("outcome").GetString(),
                (await s.ResultsAsync(held, Ct)).Last().GetProperty("final").GetBoolean()));
            Assert.False(await control.CancelAsync(held, Ct));   // variante 1 con el job ya UNKNOWN: sigue UNKNOWN
            Assert.Equal(JobStatus.Unknown, (await s.JobAsync(held, Ct)).Status);

            // La etiqueta sale tarde de la cola: PRINTED no alcanza APPLIED; el sistema no la declara aplicada.
            Assert.True(rig.Printer.Release(heldRow.LabelSerial));
            await rig.EvidenceSim.DrainAsync();
            Assert.Equal(JobStatus.Unknown, (await s.JobAsync(held, Ct)).Status);
            Assert.NotEmpty(s.Metrics.Samples(MetricNames.SendToPrintedMs));
            Assert.NotEmpty(s.Metrics.Samples(MetricNames.PrintedToAppliedMs));
        }

        // Variante 2: el dispositivo confirma la cancelación y la cola queda sin el job.
        var v2 = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Applied }, Ct);
        await using (var rig = PrintRig.Create(v2, new PrinterCapabilities(ConfirmsCancellation: true), new EvidenceSources(PrinterCompletion: true)))
        {
            var control = new DeviceCancellation(new SimulatedDeviceControl(rig.Printer), v2.Sql, v2.Txd, v2.Clock);
            rig.Printer.Mode = PrinterMode.Hold;
            var held = await v2.ReadyJobAsync(42, Ct);
            await rig.SendAsync(held, Ct);
            var serial = (await v2.JobAsync(held, Ct)).LabelSerial;
            Assert.True(await control.CancelAsync(held, Ct));
            Assert.Empty(rig.Printer.Queue);
            var row = await v2.JobAsync(held, Ct);
            Assert.Equal((JobStatus.Failed, JobReason.DeviceReportedNotPrinted), (row.Status, row.Reason));
            Assert.Equal(AttemptOutcome.NotPrinted, Assert.Single(await v2.AttemptsAsync(held, Ct)).Outcome);
            Assert.Equal("FAILED", (await v2.ResultsAsync(held, Ct)).Last().GetProperty("outcome").GetString());
            Assert.Empty(rig.Printer.Printed);
        }
    }

    /// <summary>
    /// T42 · Envío fuera de orden: el primer intento de N falla antes de escribir (RETRY_WAIT); N+1 se reclama y
    /// transmite; vence el backoff de N: el claim de N no obtiene fila y N termina EXPIRED con OUT_OF_ORDER (J14).
    /// Ninguna etiqueta de N después de la de N+1. Un reintento del mismo job sigue permitido.
    /// </summary>
    [Fact]
    [Trait("Spec", "T42")]
    public async Task A_retry_behind_a_later_cycle_expires_out_of_order_but_retrying_the_same_job_is_allowed()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 60_000, RetryBackoffMs = 100 }, Ct);
        await using var rig = PrintRig.Create(s, sources: new EvidenceSources(PrinterCompletion: false));
        var n = await s.ReadyJobAsync(50, Ct);
        var n1 = await s.ReadyJobAsync(51, Ct);

        rig.Printer.Mode = PrinterMode.RejectConnections;
        Assert.Equal(JobStatus.RetryWait, (await rig.Sender.SendNextAsync(Ct))!.Write!.JobStatus);
        rig.Printer.Mode = PrinterMode.Normal;
        var next = (await rig.Sender.SendNextAsync(Ct))!;
        Assert.Equal((n1, AttemptOutcome.Transmitted), (next.PrintJobId, next.Write!.AttemptOutcome));
        Assert.Equal(51UL, (await s.SendOrder.GetAsync(s.PrinterId, Ct))!.LastCycleId);

        s.AdvanceMs(150);
        var claim = await s.Txb.ExecuteAsync(new ClaimRequest(n, Guid.NewGuid(), s.OwnerInstanceId, s.Lease.FencingToken, s.Clock.UtcNow,
            (await s.JobAsync(n, Ct)).DeadlineAt, s.Revision.SendMarginMs, s.Revision.ConfigRevisionId), Ct);
        Assert.False(claim.Claimed);
        var report = await Sweep(s).SweepOnceAsync(Ct);
        Assert.Empty(report.Released);
        Assert.Equal((n, JobStatus.RetryWait, JobReason.OutOfOrder), (report.Expired.Single().Id, report.Expired.Single().From, report.Expired.Single().Reason));
        Assert.Equal(1, s.Metrics.Total(MetricNames.JobsExpired, ("from", JobStatus.RetryWait), ("reason", JobReason.OutOfOrder)));
        var nResult = (await s.ResultsAsync(n, Ct)).Last();
        Assert.Equal(("EXPIRED", "OUT_OF_ORDER"), (nResult.GetProperty("outcome").GetString(), nResult.GetProperty("reason").GetString()));
        Assert.Equal((JobStatus.Expired, JobReason.OutOfOrder), ((await s.JobAsync(n, Ct)).Status, (await s.JobAsync(n, Ct)).Reason));
        Assert.Equal([(await s.JobAsync(n1, Ct)).LabelSerial], rig.Printer.Received.Select(l => l.Serial));

        // Reintento del mismo job: después de un NOT_SENT, el siguiente claim del mismo job se permite.
        var m = await s.ReadyJobAsync(52, Ct);
        rig.Printer.Mode = PrinterMode.RejectConnections;
        rig.Transport.Reset();   // el sender detectó la caída del canal y reconecta antes del próximo envío
        Assert.Equal(JobStatus.RetryWait, (await rig.Sender.SendAsync(m, Ct)).Write!.JobStatus);
        rig.Printer.Mode = PrinterMode.Normal;
        s.AdvanceMs(150);
        Assert.Single((await Sweep(s).SweepOnceAsync(Ct)).Released);
        Assert.Equal(AttemptOutcome.Transmitted, (await rig.Sender.SendAsync(m, Ct)).Write!.AttemptOutcome);
    }

    /// <summary>El dispositivo simulado como control de jobs (OT-07): confirma la cancelación solo si lo declara.</summary>
    private sealed class SimulatedDeviceControl(PrinterSimulator printer) : IDeviceJobControl
    {
        public Task<bool> CancelAndConfirmAsync(string labelSerial, CancellationToken ct) => Task.FromResult(printer.Cancel(labelSerial));
    }

    /// <summary>Suspende el sender 2 s (reloj del escenario) entre el commit del claim y la escritura.</summary>
    private sealed class PauseFaults(StationScenario s) : ISenderFaults
    {
        public bool Enabled { get; set; } = true;

        public bool Paused { get; private set; }

        public Task OnPointAsync(SenderPoint point, Guid attemptId, CancellationToken ct)
        {
            if (Enabled && point == SenderPoint.AfterClaimCommit)
            {
                s.AdvanceMs(2_000);
                Paused = true;
            }

            return Task.CompletedTask;
        }
    }
}
