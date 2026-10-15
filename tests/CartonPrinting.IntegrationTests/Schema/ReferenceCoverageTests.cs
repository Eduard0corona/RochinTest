using System.Security.Cryptography;
using System.Text;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.IntegrationTests.Schema;

/// <summary>
/// T47 (complemento tras la revisión independiente de la Fase 1): las sentencias de referencia que
/// faltaban (recirculación, ramas excepcionales de TX-C, TX-D, TX-S y TX-R0, D08 por recirculación,
/// lease sin filas), el respaldo excepcional anterior, las reglas de fila de la sección 6 una por una,
/// y las regresiones de los hallazgos de la revisión.
/// </summary>
[Trait("Spec", "T47")]
public sealed class ReferenceCoverageTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t47c", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Action<SqlCommand> Id(Guid id) => c => c.Parameters.AddWithValue("@id", id);

    /// <summary>Job EXPIRED con una reimpresión ejecutada y su intento excepcional en SENDING.</summary>
    private static async Task<(Guid Job, ReprintExecution Run)> ExceptionalSendingAsync(StationScenario s, ulong cycle)
    {
        var job = await s.ReadyJobAsync(cycle);
        await s.Txb.ExpireReadyAsync(job, JobReason.DeadlinePassed, s.Clock.UtcNow, Ct);
        var auth = await s.ApproveReprintAsync(job, JobStatus.Expired, "EXPIRED_CARTON_HELD", "NONE_PROVEN");
        return (job, await s.ExecuteReprintAsync(auth));
    }

    // ------------------------------------------------------------------ ramas excepcionales (X03 en TX-C, TX-D, TX-S y TX-R0)

    [Fact]
    public async Task Tx_c_a04_and_x03_for_an_exceptional_attempt()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, run) = await ExceptionalSendingAsync(s, 10);
        var outcome = await s.Txc.UnknownAsync(run.AttemptId, s.Clock.UtcNow, "timeout tras escribir", s.Clock.UtcNow, Ct);

        Assert.Equal((JobStatus.Unknown, JobReason.ExceptionalReprintUnresolved), (outcome.JobStatus, outcome.JobReason));
        var row = await s.JobAsync(job);
        Assert.Equal(run.AttemptId, row.LastExceptionalAttemptId);
        Assert.Equal("NONE_PROVEN", outcome.Published!.ExceptionalReprint!.DuplicateRisk);
        Assert.False(outcome.Published.Final);
    }

    [Fact]
    public async Task Tx_r0_reopens_an_exceptional_attempt_with_x03()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, run) = await ExceptionalSendingAsync(s, 20);
        var recovery = await s.Txr0.ExecuteAsync(s.ManualPrinterId, s.Clock.UtcNow, Ct);

        Assert.Equal([job], recovery.ExceptionalJobs);
        Assert.Equal(JobReason.ExceptionalReprintUnresolved, (await s.JobAsync(job)).Reason);
        Assert.Equal(AttemptOutcome.Unknown, (await s.AttemptsAsync(job)).Single(a => a.AttemptId == run.AttemptId).Outcome);
    }

    [Fact]
    public async Task Tx_s_elapsed_window_of_an_exceptional_attempt_applies_a06_and_x03()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, run) = await ExceptionalSendingAsync(s, 30);
        await s.TransmitAsync(run.AttemptId, Ct);
        Assert.NotEqual(s.Revision.EvidenceWindowMs, run.EvidenceWindowMs);
        Assert.Equal(run.EvidenceWindowMs, await s.ScalarAsync<int>(
            "SELECT DATEDIFF(MILLISECOND, WriteCompletedAt, EvidenceDeadlineAt) FROM dbo.PrintAttempt WHERE AttemptId = @a",
            c => c.Parameters.AddWithValue("@a", run.AttemptId), Ct));
        s.AdvanceMs(s.Revision.EvidenceWindowMs!.Value + 5);                                            // vencida la de la revisión, no la suya
        Assert.Null(await s.Txs.ElapseEvidenceWindowAsync(run.AttemptId, s.Clock.UtcNow, Ct));        // ventana vigente: 0 filas
        s.AdvanceMs(run.EvidenceWindowMs);
        var t = (await s.Txs.ElapseEvidenceWindowAsync(run.AttemptId, s.Clock.UtcNow, Ct))!;
        Assert.Equal((job, JobReason.ExceptionalReprintUnresolved), (t.Id, t.Reason));
    }

    [Fact]
    public async Task Tx_d_contradicting_read_of_an_exceptional_attempt_applies_a06_and_x03()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, run) = await ExceptionalSendingAsync(s, 40);
        await s.TransmitAsync(run.AttemptId, Ct);
        var outcome = await s.EvidenceAsync(s.Verified(40, "P01LZZ-000000000099", run.AttemptId, EvidenceType.ManualInspection, "inspector.diaz"));

        Assert.Equal(EvidenceAction.ExceptionalUnresolved, outcome.Action);
        Assert.Equal(JobReason.ExceptionalReprintUnresolved, (await s.JobAsync(job)).Reason);
    }

    [Fact]
    public async Task Exception_with_an_older_exceptional_attempt_as_backing_is_rejected()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, first) = await ExceptionalSendingAsync(s, 50);
        await s.Txc.NotSentAsync(first.AttemptId, s.Clock.UtcNow, "conexión rechazada", NotSentCause.ConnectionFailed, 100, 3, s.Clock.UtcNow, Ct);
        var second = await s.ExecuteReprintAsync(await s.ApproveReprintAsync(job, JobStatus.Expired, "EXPIRED_CARTON_HELD", "NONE_PROVEN"));
        await s.Txc.UnknownAsync(second.AttemptId, s.Clock.UtcNow, "timeout", s.Clock.UtcNow, Ct);   // X03 con el intento más reciente

        var ex = await s.FailsAsync("""
            UPDATE dbo.PrintJob SET LastExceptionalAttemptId = @a, ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id
            """, c => { c.Parameters.AddWithValue("@a", first.AttemptId); c.Parameters.AddWithValue("@id", job); }, "EXCEPTION", Ct);
        Assert.Equal(51012, ex.Number);
    }

    // ------------------------------------------------------------------ recirculación (TX-A y TX-E) y lease

    [Fact]
    public async Task Tx_a_recirculation_blocks_with_a_live_job_or_a_waiting_detection_of_the_same_carton()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { CartonIdentityMode = "CARTON_ID_REQUIRED" }, Ct);
        Assert.Equal(DetectionStatus.JobCreated, (await s.RegisterAsync(s.Cycle(60, cartonId: "C-77"))).FinalStatus);
        var again = await s.RegisterAsync(s.Cycle(61, cartonId: "C-77"));
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.RecirculatedCarton), (again.FinalStatus, again.FinalReason));

        Assert.Equal(DetectionStatus.WaitingErp, (await s.RegisterAsync(s.Cycle(62, workOrder: "WO-SIN", cartonId: "C-88"))).FinalStatus);
        var waiting = await s.RegisterAsync(s.Cycle(63, workOrder: "WO-SIN", cartonId: "C-88"));
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.RecirculatedCarton), (waiting.FinalStatus, waiting.FinalReason));
    }

    [Fact]
    public async Task Tx_e_d08_with_recirculated_carton()
    {
        // Dos detecciones de la misma caja esperan snapshot con CYCLE_ONLY; después se activa
        // CARTON_ID_REQUIRED y llega el snapshot: TX-E repite la validación de recirculación. Para la segunda,
        // la primera (anterior, en WAITING_ERP) es recirculación (SI-17).
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var d1 = await s.RegisterAsync(s.Cycle(70, workOrder: "WO-RC", cartonId: "C-99"));
        var d2 = await s.RegisterAsync(s.Cycle(71, workOrder: "WO-RC", cartonId: "C-99"));
        Assert.Equal(DetectionStatus.WaitingErp, d2.FinalStatus);
        var rev8 = await s.Stations.ActivateRevisionAsync(StationScenario.DefaultRevision(s.StationId) with { RevisionNumber = 8, CartonIdentityMode = "CARTON_ID_REQUIRED" }, Ct);
        await s.AddSnapshotAsync("R1", "SKU-RC", "LOT-RC", Ct, "WO-RC");
        var snapshot = (await s.Erp.FindValidAsync("WO-RC", s.Clock.UtcNow, Ct))!;
        var detection = (await s.DetectionAsync(71))!;
        var serial = LabelSerialSource.Format(s.SerialPrefix, await s.Serials.NextAsync(Ct));
        var label = LabelRenderer.Render(s.Revision, snapshot, detection.Identity, serial, "C-99");
        var plan = new ErpArrivalPlan(d2.DetectionId, new JobInsert(Guid.NewGuid(), s.Revision.LabelType, s.Revision.PrinterId, "WO-RC", snapshot.Revision,
            snapshot.ProductId, snapshot.LotId, serial, s.Revision.TemplateRevision, label.Payload, label.PayloadHash, detection.DeadlineAt),
            RequiresCartonId: true, detection.DeadlineAt, s.Revision.SendMarginMs, s.Revision.LabelType, s.Clock.UtcNow, snapshot.Gtin,
            rev8.ConfigRevisionId);   // el plazo y el margen se calculan con la revisión activa

        var result = await s.Txe.ExecuteAsync(plan, Ct);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.RecirculatedCarton), (result.FinalStatus, result.FinalReason));
    }

    [Fact]
    public async Task Lease_update_with_zero_rows_while_another_instance_holds_it()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        Assert.Null(await s.Leases.AcquireAsync(s.PrinterId, "otra-instancia", "otro-host", 60_000, 2_000, s.Clock.UtcNow, Ct));
        var renewed = await s.Leases.AcquireAsync(s.PrinterId, s.OwnerInstanceId, "host-test", 60_000, 2_000, s.Clock.UtcNow, Ct);
        Assert.Equal(s.Lease.FencingToken, renewed!.FencingToken);
    }

    // ------------------------------------------------------------------ reglas de fila de la sección 6, una por una

    [Fact]
    public async Task Unique_and_check_rules_reject_with_their_own_constraint()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(80);
        var row = await s.JobAsync(job);

        // UQ_PrintJob_LabelSerial: otro job (otra detección) con el mismo serial.
        var other = await s.RegisterAsync(s.Cycle(81, workOrder: "WO-SIN-SNAP"));
        var serial = await s.FailsAsync("""
            INSERT INTO dbo.PrintJob (PrintJobId, DetectionId, SiteId, StationId, PlcEpoch, CycleId, LabelType, ConfigRevisionId, PrinterId,
                WorkOrderId, ErpRevision, ProductId, LotId, LabelSerial, TemplateRevision, Payload, PayloadHash, DeadlineAt, Status)
            SELECT NEWID(), cd.DetectionId, cd.SiteId, cd.StationId, cd.PlcEpoch, cd.CycleId, pj.LabelType, pj.ConfigRevisionId, pj.PrinterId,
                pj.WorkOrderId, pj.ErpRevision, pj.ProductId, pj.LotId, pj.LabelSerial, pj.TemplateRevision, pj.Payload, pj.PayloadHash, pj.DeadlineAt, 'READY'
            FROM dbo.PrintJob AS pj CROSS JOIN dbo.CartonDetection AS cd WHERE pj.PrintJobId = @id AND cd.DetectionId = @d
            """, c => { c.Parameters.AddWithValue("@id", job); c.Parameters.AddWithValue("@d", other.DetectionId); }, ct: Ct);
        Assert.Contains("UQ_PrintJob_LabelSerial", serial.Message, StringComparison.Ordinal);

        // UX_PrintAttempt_OrdinarySent: un segundo intento ordinario que pudo transmitir (el primero ya UNKNOWN).
        var (unknown, _) = await s.UnknownJobAsync(82);
        var sent = await s.FailsAsync(InsertAttempt("ORDINARY", "UNKNOWN", 1), c => c.Parameters.AddWithValue("@job", unknown), ct: Ct);
        Assert.Contains("UX_PrintAttempt_OrdinarySent", sent.Message, StringComparison.Ordinal);

        // UX_PrintAttempt_JobOpen: un segundo intento abierto del mismo job (el excepcional sigue TRANSMITTED).
        // El job nunca tuvo intento ordinario y el nuevo no está en SENDING: solo JobOpen puede rechazarlo.
        var (expiredJob, run) = await ExceptionalSendingAsync(s, 83);
        await s.TransmitAsync(run.AttemptId, Ct);
        var open = await s.FailsAsync(InsertAttempt("ORDINARY", "TRANSMITTED", 1),
            c => c.Parameters.AddWithValue("@job", expiredJob), ct: Ct);
        Assert.Equal(2601, open.Number);
        Assert.Contains("UX_PrintAttempt_JobOpen", open.Message, StringComparison.Ordinal);

        // UX_ReprintAuthorization_OpenPerJob: dos autorizaciones APPROVED del mismo job.
        var (u2, _) = await s.UnknownJobAsync(84);
        await s.ApproveReprintAsync(u2, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");
        var twice = await Assert.ThrowsAsync<SqlException>(() => s.ApproveReprintAsync(u2, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED"));
        Assert.Contains("UX_ReprintAuthorization_OpenPerJob", twice.Message, StringComparison.Ordinal);

        // UX_PrintAttempt_Authorization: una autorización ya usada por otro intento.
        var used = await s.FailsAsync($"""
            INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken,
                Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt)
            SELECT NEWID(), pa.PrintJobId, 99, 'EXCEPTIONAL_REPRINT', pa.AuthorizationId, pa.PrinterId, N'x', 1, 'NOT_SENT', 0, pa.AttemptPayloadHash, SYSUTCDATETIME()
            FROM dbo.PrintAttempt AS pa WHERE pa.AttemptId = @a
            """, c => c.Parameters.AddWithValue("@a", run.AttemptId), "EXCEPTION", Ct);
        Assert.Contains("UX_PrintAttempt_Authorization", used.Message, StringComparison.Ordinal);

        // UX_PrintAttempt_ConfirmingObservation: una observación ya usada para confirmar otro intento.
        var (c1, a1) = await s.TransmittedJobAsync(85);
        var obs = await s.ObserveAsync(s.Printed(c1, a1));
        Assert.Equal(EvidenceAction.Confirmed, (await s.Txd.ApplyAsync(obs, s.Clock.UtcNow, Ct)).Action);
        var (_, a2) = await s.TransmittedJobAsync(86);
        var reuse = await s.FailsAsync("UPDATE dbo.PrintAttempt SET Outcome = 'CONFIRMED', ConfirmingObservationId = @o WHERE AttemptId = @a",
            c => { c.Parameters.AddWithValue("@o", obs); c.Parameters.AddWithValue("@a", a2); }, ct: Ct);
        Assert.Contains("UX_PrintAttempt_ConfirmingObservation", reuse.Message, StringComparison.Ordinal);

        // CK_EvidenceObservation_Type, _Level y _Manual.
        var type = await Assert.ThrowsAsync<SqlException>(() => s.ObserveAsync(s.Printed(c1) with { EvidenceLevel = EvidenceLevel.Verified }));
        Assert.Contains("CK_EvidenceObservation_Type", type.Message, StringComparison.Ordinal);
        var level = await Assert.ThrowsAsync<SqlException>(() => s.ObserveAsync(s.Verified(85, row.LabelSerial) with { ReadSerial = null }));
        Assert.Contains("CK_EvidenceObservation_Level", level.Message, StringComparison.Ordinal);
        var manual = await Assert.ThrowsAsync<SqlException>(() => s.ObserveAsync(s.Verified(85, row.LabelSerial, type: EvidenceType.ManualInspection)));
        Assert.Contains("CK_EvidenceObservation_Manual", manual.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ regresiones de la revisión independiente

    [Fact]
    public async Task Review_alta_1_j18_applies_after_a_confirmed_exceptional_reprint()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, run) = await ExceptionalSendingAsync(s, 90);
        await s.TransmitAsync(run.AttemptId, Ct);
        var serial = (await s.JobAsync(job)).LabelSerial;
        Assert.Equal(EvidenceAction.ExceptionalConfirmed,
            (await s.EvidenceAsync(s.Verified(90, serial, run.AttemptId, EvidenceType.ManualInspection, "inspector.diaz"))).Action);

        // Escáner aguas abajo, sin AttemptId, con otro serial: J18 aunque el job no tenga intento ordinario.
        var corrected = await s.EvidenceAsync(s.Verified(90, "P01LZZ-000000000077"));
        Assert.Equal(EvidenceAction.Corrected, corrected.Action);
        var row = await s.JobAsync(job);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch, false), (row.Status, row.Reason, row.ConfirmedByExceptionalReprint));
        Assert.False(corrected.Published!.Evidence!.Verified);
    }

    [Fact]
    public async Task Review_alta_1_j18_after_x01_also_when_the_read_references_the_confirmed_reprint()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, run) = await ExceptionalSendingAsync(s, 95);
        await s.TransmitAsync(run.AttemptId, Ct);
        var serial = (await s.JobAsync(job)).LabelSerial;
        await s.EvidenceAsync(s.Verified(95, serial, run.AttemptId, EvidenceType.ManualInspection, "inspector.diaz"));

        var corrected = await s.EvidenceAsync(s.Verified(95, "P01LZZ-000000000078", run.AttemptId, EvidenceType.ManualInspection, "inspector.diaz"));
        Assert.Equal(EvidenceAction.Corrected, corrected.Action);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), ((await s.JobAsync(job)).Status, (await s.JobAsync(job)).Reason));
    }

    private const string Correction =
        "UPDATE dbo.PrintJob SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH', ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id";

    /// <summary>
    /// T47 · corrección con un respaldo anterior (SI-07, ADR 0016): una lectura contradictoria registrada antes
    /// del claim no respalda J18, ni en el trigger ni en TX-D.
    /// </summary>
    [Fact]
    public async Task Correction_backed_by_a_read_older_than_the_claim_is_rejected()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        var job = await s.ReadyJobAsync(160);
        var early = await s.ObserveAsync(s.Verified(160, "P01LZZ-000000000066"));   // antes de que existiera una etiqueta del job
        s.AdvanceMs(5);
        var claim = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
        await s.TransmitAsync(claim.AttemptId, Ct);
        Assert.Equal(EvidenceAction.Confirmed, (await s.EvidenceAsync(s.Verified(160, (await s.JobAsync(job)).LabelSerial))).Action);

        var ex = await s.FailsAsync(Correction, c => c.Parameters.AddWithValue("@id", job), "CORRECTION", Ct);
        Assert.Equal(51012, ex.Number);
        Assert.Equal(EvidenceAction.Recorded, (await s.Txd.ApplyAsync(early, s.Clock.UtcNow, Ct)).Action);   // reaplicada tras una caída
        Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(job)).Status);
    }

    /// <summary>
    /// T47 · corrección con un respaldo anterior (ADR 0016, hallazgo M1 de la segunda revisión): una lectura
    /// de la etiqueta original registrada antes de que una reimpresión confirmada la reemplazara no deshace
    /// la confirmación; una lectura posterior sí corrige.
    /// </summary>
    [Fact]
    public async Task Correction_backed_by_a_read_older_than_the_confirming_observation_is_rejected()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        var (job, attempt) = await s.TransmittedJobAsync(165);
        var stale = await s.ObserveAsync(s.Verified(165, "P01LZZ-000000000067"));   // registrada; el proceso cae antes de aplicarla
        s.AdvanceMs(s.Revision.EvidenceWindowMs!.Value + 5);
        await s.Txs.ElapseEvidenceWindowAsync(attempt, s.Clock.UtcNow, Ct);         // A06 + J13: UNKNOWN EVIDENCE_WINDOW_ELAPSED
        var auth = await s.ApproveReprintAsync(job, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");
        var run = await s.ExecuteReprintAsync(auth);
        await s.TransmitAsync(run.AttemptId, Ct);
        var serial = (await s.JobAsync(job)).LabelSerial;
        Assert.Equal(EvidenceAction.ExceptionalConfirmed,
            (await s.EvidenceAsync(s.Verified(165, serial, run.AttemptId, EvidenceType.ManualInspection, "inspector.diaz"))).Action);

        Assert.Equal(EvidenceAction.Recorded, (await s.Txd.ApplyAsync(stale, s.Clock.UtcNow, Ct)).Action);
        var ex = await s.FailsAsync(Correction.Replace("ResultVersion + 1", "ResultVersion + 1, ConfirmedByExceptionalReprint = 0", StringComparison.Ordinal),
            c => c.Parameters.AddWithValue("@id", job), "CORRECTION", Ct);
        Assert.Equal(51012, ex.Number);
        Assert.Equal((JobStatus.Confirmed, true), ((await s.JobAsync(job)).Status, (await s.JobAsync(job)).ConfirmedByExceptionalReprint));

        var fresh = await s.EvidenceAsync(s.Verified(165, "P01LZZ-000000000068"));
        Assert.Equal(EvidenceAction.Corrected, fresh.Action);
    }

    [Fact]
    public async Task Review2_m3_with_frozen_level_none_a_contradiction_or_a_not_printed_report_is_recorded_without_transition()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.None, EvidenceWindowMs = null }, Ct);
        var (job, attempt) = await s.TransmittedJobAsync(170);
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(job)).Status);   // terminal con NONE

        Assert.Equal(EvidenceAction.Recorded, (await s.EvidenceAsync(s.Verified(170, "P01LZZ-000000000170"))).Action);
        Assert.Equal(EvidenceAction.Recorded, (await s.Txd.DeviceNotPrintedAsync(attempt, "Cancelado por el operador", s.Clock.UtcNow, Ct)).Action);
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(job)).Status);
        Assert.Equal(AttemptOutcome.Transmitted, (await s.AttemptsAsync(job)).Single().Outcome);
    }

    [Fact]
    public async Task Review2_b7_a_contradicting_read_that_references_another_jobs_attempt_has_no_effect()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        var (x, _) = await s.TransmittedJobAsync(180);
        var (_, otherAttempt) = await s.TransmittedJobAsync(181);
        var outcome = await s.EvidenceAsync(s.Verified(180, "P01LZZ-000000000180", otherAttempt));
        Assert.Equal(EvidenceAction.Recorded, outcome.Action);
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(x)).Status);
    }

    [Fact]
    public async Task Review2_b5_cart_id_required_interim_does_not_override_late_arrival()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with
        {
            DeadlineReference = DeadlineReference.GatewayReceipt, ClockSkewToleranceMs = null, MaxUpstreamLatencyMs = 40,
            CartonIdentityMode = "CARTON_ID_REQUIRED",
        }, Ct);
        var recovered = await s.RegisterAsync(s.Cycle(190, Transport.Read));      // con orden, sin CartonId, no en vivo
        Assert.Equal((DetectionStatus.Expired, DetectionReason.LateArrival), (recovered.FinalStatus, recovered.FinalReason));
        var live = await s.RegisterAsync(s.Cycle(191));                           // candidato sin CartonId: interino de SI-04
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot), (live.FinalStatus, live.FinalReason));
    }

    [Fact]
    public async Task Review2_b1_racing_claims_of_two_jobs_on_one_printer_never_throw()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        for (ulong round = 0; round < 8; round++)
        {
            var a = await s.ReadyJobAsync(200 + (round * 2));
            var b = await s.ReadyJobAsync(201 + (round * 2));
            var outcomes = await Task.WhenAll(
                Task.Run(() => s.Claims.TryClaimAsync(a, s.Lease, Ct), Ct), Task.Run(() => s.Claims.TryClaimAsync(b, s.Lease, Ct), Ct));
            var won = Assert.Single(outcomes, o => o!.Claimed);
            Assert.Equal(1, await s.CountAsync("FROM dbo.PrintAttempt WHERE PrinterId = @p AND Outcome = 'SENDING'",
                c => c.Parameters.AddWithValue("@p", s.PrinterId), Ct));
            await s.TransmitAsync(won!.AttemptId, Ct);   // libera la impresora para la ronda siguiente
        }
    }

    [Fact]
    public async Task Review_baja_6_a_second_contradiction_does_not_republish_a_serial_mismatch()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        var (job, _) = await s.TransmittedJobAsync(100);
        Assert.Equal(EvidenceAction.SerialMismatch, (await s.EvidenceAsync(s.Verified(100, "P01LZZ-000000000001"))).Action);
        var version = (await s.JobAsync(job)).ResultVersion;
        Assert.Equal(EvidenceAction.Recorded, (await s.EvidenceAsync(s.Verified(100, "P01LZZ-000000000002"))).Action);
        Assert.Equal(version, (await s.JobAsync(job)).ResultVersion);
    }

    [Fact]
    public async Task Review_media_2_a_verified_read_of_another_cycle_tagged_with_this_job_has_no_effect()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        var (x, _) = await s.TransmittedJobAsync(110);
        var (y, _) = await s.TransmittedJobAsync(111);
        var ySerial = (await s.JobAsync(y)).LabelSerial;
        // Lectura VERIFIED del ciclo 111, correcta para Y, pero etiquetada con el PrintJobId de X.
        var outcome = await s.EvidenceAsync(s.Verified(111, ySerial) with { PrintJobId = x });
        Assert.Equal(EvidenceAction.Recorded, outcome.Action);
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(x)).Status);
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(y)).Status);
    }

    [Fact]
    public async Task Review_media_3_gateway_record_not_live_without_work_order_is_blocked_not_an_exception()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with
        {
            DeadlineReference = DeadlineReference.GatewayReceipt, ClockSkewToleranceMs = null, MaxUpstreamLatencyMs = 40,
        }, Ct);
        foreach (var (cycle, transport, overflow) in new[] { (120UL, Transport.Read, false), (121UL, Transport.Republish, false), (122UL, Transport.Subscription, true) })
        {
            var result = await s.RegisterAsync(s.Cycle(cycle, transport, workOrder: null, overflow: overflow));
            Assert.Equal((DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot), (result.FinalStatus, result.FinalReason));
        }

        var expiredByGap = await s.RegisterAsync(s.Cycle(130, workOrder: null),
            new SequenceDecision { TerminalStatus = DetectionStatus.Expired, TerminalReason = DetectionReason.LateArrival });
        Assert.Equal(DetectionStatus.Blocked, expiredByGap.FinalStatus);
    }

    [Fact]
    public async Task Review_media_4_reprint_marker_changes_the_payload_and_the_attempt_hash()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, run) = await ExceptionalSendingAsync(s, 140);
        var row = await s.JobAsync(job);
        var attempt = (await s.AttemptsAsync(job)).Single(a => a.AttemptId == run.AttemptId);
        var hash = await s.ScalarAsync<byte[]>("SELECT AttemptPayloadHash FROM dbo.PrintAttempt WHERE AttemptId = @a",
            c => c.Parameters.AddWithValue("@a", attempt.AttemptId), Ct);

        Assert.Contains("REIMPRESION", run.Payload, StringComparison.Ordinal);
        Assert.Contains(row.LabelSerial, run.Payload, StringComparison.Ordinal);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(run.Payload)), hash);
        Assert.NotEqual(row.PayloadHash, hash);
    }

    [Fact]
    public async Task Review_baja_8_claim_with_another_sending_attempt_on_the_printer_returns_no_row()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await s.SendingJobAsync(150);
        var next = await s.ReadyJobAsync(151);
        var outcome = (await s.Claims.TryClaimAsync(next, s.Lease, Ct))!;
        Assert.False(outcome.Claimed);
        Assert.Equal(ClaimRejection.PrinterBusy, outcome.Rejection);
        Assert.Equal(JobStatus.Ready, (await s.JobAsync(next)).Status);
    }

    private static string InsertAttempt(string kind, string outcome, int mayHaveSent) => $"""
        INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken,
            Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt, WriteStartedAt, WriteCompletedAt, EvidenceDeadlineAt)
        SELECT NEWID(), pj.PrintJobId, 50, '{kind}', NULL,
            pj.PrinterId, N'x', 1, '{outcome}', {mayHaveSent}, pj.PayloadHash, SYSUTCDATETIME(),
            {(outcome == "TRANSMITTED" ? "SYSUTCDATETIME(), SYSUTCDATETIME(), DATEADD(SECOND, 60, SYSUTCDATETIME())" : "NULL, NULL, NULL")}
        FROM dbo.PrintJob AS pj WHERE pj.PrintJobId = @job
        """;
}
