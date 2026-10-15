using System.Reflection;
using System.Text.RegularExpressions;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.IntegrationTests.Schema;

/// <summary>
/// T47 · Cada sentencia de spec/database/sqlserver_operations_reference.sql, envuelta en su clase de
/// transacción, se ejecuta con 0 filas afectadas y con filas afectadas: ninguna lanza excepción con 0
/// filas (los triggers tienen la guarda de 0 filas) y ninguna falla con Msg 334 (OUTPUT sin INTO).
/// Además, las reglas de fila de la sección 6 del documento principal (CHECK y UNIQUE) tienen su
/// prueba negativa, y cada THROW del DDL tiene una prueba en TriggerGuardTests.
/// </summary>
[Trait("Spec", "T47")]
public sealed class ReferenceStatementTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t47r", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_throw_code_of_the_ddl_has_a_negative_test()
    {
        var ddl = File.ReadAllText(RepoPaths.SpecFile("database", "sqlserver_schema.sql"));
        var ops = File.ReadAllText(RepoPaths.SpecFile("database", "sqlserver_operations_reference.sql"));
        var codes = Regex.Matches(ddl + ops, @"THROW\s+(5\d{4})").Select(m => m.Groups[1].Value).ToHashSet();
        var tested = typeof(TriggerGuardTests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => Regex.Matches(m.Name, @"5\d{4}").Select(x => x.Value)).ToHashSet();

        Assert.Equal(33, codes.Count);                                       // 51001 a 51032 y 52001 (SI-01)
        Assert.Empty(codes.Except(tested).Order());
    }

    [Fact]
    public async Task Tx_a_inserts_classifies_and_handles_the_existing_key_without_exception()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var record = s.Cycle(1);
        var first = await s.RegisterAsync(record);
        Assert.Equal(RegistrationOutcome.Registered, first.Outcome);
        Assert.Equal(DetectionStatus.JobCreated, first.FinalStatus);
        // Clave existente: el INSERT falla con 2627 de UQ_CartonDetection_Identity y se clasifica en otra transacción.
        var again = await s.RegisterAsync(record with { Transport = Transport.Republish });
        Assert.Equal(RegistrationOutcome.Duplicate, again.Outcome);
        Assert.Equal(first.DetectionId, again.DetectionId);
        // Sin snapshot (D03), terminal antes de TX-A (D05) y vencida (D04).
        Assert.Equal(DetectionStatus.WaitingErp, (await s.RegisterAsync(s.Cycle(2, workOrder: "WO-OTRA"))).FinalStatus);
        Assert.Equal(DetectionReason.IncoherentSnapshot, (await s.RegisterAsync(s.Cycle(3, workOrder: null))).FinalReason);
        Assert.Equal(DetectionReason.LateArrival,
            (await s.RegisterAsync(s.Cycle(4, detectionTimestamp: s.Clock.UtcNow.AddSeconds(-5)))).FinalReason);
    }

    [Fact]
    public async Task Tx_b_claim_with_zero_and_one_row_and_j03()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(10);
        var claim = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
        Assert.True(claim.Claimed);
        // Mismo job ya en SENDING: 0 filas, sin excepción.
        var again = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
        Assert.False(again.Claimed);
        Assert.Equal(ClaimRejection.NotReady, again.Rejection);
        // J03 con 0 filas (no está READY) y con 1 fila.
        Assert.Null(await s.Txb.ExpireReadyAsync(job, JobReason.DeadlinePassed, s.Clock.UtcNow, Ct));
        await s.Txc.UnknownAsync(claim.AttemptId, s.Clock.UtcNow, "timeout", s.Clock.UtcNow, Ct);
        var ready = await s.ReadyJobAsync(11);
        Assert.NotNull(await s.Txb.ExpireReadyAsync(ready, JobReason.OutOfOrder, s.Clock.UtcNow, Ct));
        // Un claim con plazo vencido: 0 filas y J03 desde el sender.
        var late = await s.ReadyJobAsync(12);
        s.AdvanceMs(2000);
        var rejected = (await s.Claims.TryClaimAsync(late, s.Lease, Ct))!;
        Assert.Equal(ClaimRejection.Deadline, rejected.Rejection);
        Assert.Equal(JobStatus.Expired, (await s.JobAsync(late)).Status);
    }

    [Fact]
    public async Task Tx_c_each_write_result_with_zero_and_one_row()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        // A03 + J05.
        var (job, attempt) = await s.SendingJobAsync(20);
        var transmitted = await s.TransmitAsync(attempt, Ct);
        Assert.Equal(JobStatus.Transmitted, transmitted.JobStatus);
        // 0 filas: el intento ya no está en SENDING.
        Assert.Null((await s.TransmitAsync(attempt, Ct)).JobStatus);
        Assert.Null((await s.Txc.UnknownAsync(attempt, s.Clock.UtcNow, "x", s.Clock.UtcNow, Ct)).JobStatus);
        Assert.Null((await s.Txc.NotSentAsync(attempt, null, "x", NotSentCause.ConnectionFailed, 100, 3, s.Clock.UtcNow, Ct)).JobStatus);
        await s.Txd.DeviceNotPrintedAsync(attempt, "cancelado", s.Clock.UtcNow, Ct);   // libera la impresora: J12
        _ = job;

        // A02 + J06, J07 y J08.
        var (retry, a1) = await s.SendingJobAsync(21);
        Assert.Equal(JobStatus.RetryWait, (await s.Txc.NotSentAsync(a1, s.Clock.UtcNow, "rechazada", NotSentCause.ConnectionFailed, 100, 3, s.Clock.UtcNow, Ct)).JobStatus);
        var (exhausted, a2) = await s.SendingJobAsync(22);
        Assert.Equal(JobStatus.Failed, (await s.Txc.NotSentAsync(a2, s.Clock.UtcNow, "rechazada", NotSentCause.ConnectionFailed, 100, 1, s.Clock.UtcNow, Ct)).JobStatus);
        var (expired, a3) = await s.SendingJobAsync(23);
        Assert.Equal(JobStatus.Expired, (await s.Txc.NotSentAsync(a3, s.Clock.UtcNow, "plazo", NotSentCause.DeadlinePassed, 100, 3, s.Clock.UtcNow, Ct)).JobStatus);
        // A04 + J09.
        var (unknown, a4) = await s.SendingJobAsync(24);
        Assert.Equal(JobStatus.Unknown, (await s.Txc.UnknownAsync(a4, s.Clock.UtcNow, "timeout", s.Clock.UtcNow, Ct)).JobStatus);

        Assert.Equal(JobStatus.RetryWait, (await s.JobAsync(retry)).Status);
        Assert.Equal(JobReason.PrinterUnavailable, (await s.JobAsync(exhausted)).Reason);
        Assert.Equal(JobReason.DeadlinePassed, (await s.JobAsync(expired)).Reason);
        Assert.Equal(JobReason.WriteInterrupted, (await s.JobAsync(unknown)).Reason);
    }

    [Fact]
    public async Task Tx_d_evidence_recorded_first_and_applied_with_zero_and_n_rows()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        // Observación sin job: se registra y no aplica nada.
        Assert.Equal(EvidenceAction.Recorded, (await s.EvidenceAsync(s.Verified(999, "P01LXX-000000000001"))).Action);

        // J10.
        var (j1, _) = await s.TransmittedJobAsync(30);
        Assert.Equal(EvidenceAction.Confirmed, (await s.EvidenceAsync(s.Verified(30, (await s.JobAsync(j1)).LabelSerial))).Action);
        // J18 desde CONFIRMED.
        Assert.Equal(EvidenceAction.Corrected, (await s.EvidenceAsync(s.Verified(30, "P01LXX-000000000002"))).Action);
        Assert.Equal(JobReason.SerialMismatch, (await s.JobAsync(j1)).Reason);
        // J18 solo aplica a un job confirmado o en UNKNOWN "por otra causa": una segunda contradicción
        // sobre UNKNOWN SERIAL_MISMATCH se registra sin transición ni nueva versión.
        var corrected = (await s.JobAsync(j1)).ResultVersion;
        Assert.Equal(EvidenceAction.Recorded, (await s.EvidenceAsync(s.Verified(30, "P01LXX-000000000003"))).Action);
        Assert.Equal(corrected, (await s.JobAsync(j1)).ResultVersion);

        // J11 por serial distinto.
        var (j2, _) = await s.TransmittedJobAsync(31);
        Assert.Equal(EvidenceAction.SerialMismatch, (await s.EvidenceAsync(s.Verified(31, "P01LXX-000000000004"))).Action);
        // J15 desde WRITE_INTERRUPTED con la lectura correcta.
        var (j3, _) = await s.UnknownJobAsync(32);
        Assert.Equal(EvidenceAction.Confirmed, (await s.EvidenceAsync(s.Verified(32, (await s.JobAsync(j3)).LabelSerial))).Action);
        // Tras SERIAL_MISMATCH no hay reconciliación automática: la lectura correcta se registra sin transición.
        Assert.Equal(EvidenceAction.Recorded, (await s.EvidenceAsync(s.Verified(31, (await s.JobAsync(j2)).LabelSerial))).Action);
        Assert.Equal(JobStatus.Unknown, (await s.JobAsync(j2)).Status);
        // J18 desde UNKNOWN por otra causa (WRITE_INTERRUPTED): cambian el motivo y la versión, no el estado.
        var (j4, _) = await s.UnknownJobAsync(33);
        var before = (await s.JobAsync(j4)).ResultVersion;
        Assert.Equal(EvidenceAction.Corrected, (await s.EvidenceAsync(s.Verified(33, "P01LXX-000000000005"))).Action);
        var after = await s.JobAsync(j4);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch, before + 1), (after.Status, after.Reason, after.ResultVersion));
    }

    [Fact]
    public async Task Tx_e_erp_arrival_d06_d07_d08_and_zero_rows()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        const string order = "WO-TARDE";
        var waiting = await s.RegisterAsync(s.Cycle(40, workOrder: order));
        Assert.Equal(DetectionStatus.WaitingErp, waiting.FinalStatus);
        await s.AddSnapshotAsync("R1", "SKU-1", "LOT-1", Ct, order);
        var snapshot = (await s.Erp.FindValidAsync(order, s.Clock.UtcNow, Ct))!;
        var plan = await ErpPlanAsync(s, waiting.DetectionId, snapshot, 40);
        Assert.Equal(DetectionStatus.JobCreated, (await s.Txe.ExecuteAsync(plan, Ct)).FinalStatus);              // D06
        Assert.Null((await s.Txe.ExecuteAsync(plan with { Job = plan.Job with { PrintJobId = Guid.NewGuid() } }, Ct)).PrintJobId); // 0 filas

        var blocked = await s.RegisterAsync(s.Cycle(41, workOrder: "WO-OTRA-1"));
        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.Operator, "supervisor.ramos", null, s.Clock.UtcNow, Ct);
        await s.AddSnapshotAsync("R1", "SKU-2", "LOT-2", Ct, "WO-OTRA-1");
        var d08 = await s.Txe.ExecuteAsync(await ErpPlanAsync(s, blocked.DetectionId, (await s.Erp.FindValidAsync("WO-OTRA-1", s.Clock.UtcNow, Ct))!, 41), Ct);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled), (d08.FinalStatus, d08.FinalReason));     // D08

        await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "Revisado", s.Clock.UtcNow, Ct);
        var late = await s.RegisterAsync(s.Cycle(42, workOrder: "WO-OTRA-2"));
        s.AdvanceMs(3000);
        await s.AddSnapshotAsync("R1", "SKU-3", "LOT-3", Ct, "WO-OTRA-2");
        var tooLate = await s.Txe.ExecuteAsync(await ErpPlanAsync(s, late.DetectionId, (await s.Erp.FindValidAsync("WO-OTRA-2", s.Clock.UtcNow, Ct))!, 42), Ct);
        Assert.Equal((DetectionStatus.WaitingErp, (Guid?)null), (tooLate.FinalStatus, tooLate.PrintJobId));              // 0 filas: sin job
        Assert.Single(await s.Txs.ExpireWaitingErpAsync(StationScenario.SiteId, s.StationId, 100, s.Clock.UtcNow, Ct));    // D07 en TX-S
        Assert.Equal((DetectionStatus.Expired, DetectionReason.ErpUnavailable), ((await s.DetectionAsync(42))!.Status, (await s.DetectionAsync(42))!.Reason));
    }

    [Fact]
    public async Task Tx_s_each_sweep_with_zero_and_n_rows()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var now = s.Clock.UtcNow;
        // 0 filas en todas las operaciones.
        Assert.Empty(await s.Txs.ExpireDeadlinesAsync(StationScenario.SiteId, s.StationId, 100, now, Ct));
        Assert.Empty(await s.Txs.ExpireOutOfOrderAsync(StationScenario.SiteId, s.StationId, now, Ct));
        Assert.Null(await s.Txs.ReleaseRetryAsync(Guid.NewGuid(), now.AddSeconds(5), 100, s.Revision.ConfigRevisionId, now, Ct));
        Assert.Empty(await s.Txs.ListPendingJobsAsync(StationScenario.SiteId, s.StationId, Ct));
        Assert.Empty(await s.Txs.ExpireWaitingErpAsync(StationScenario.SiteId, s.StationId, 100, now, Ct));
        Assert.Empty(await s.Txs.ElapsedEvidenceWindowsAsync(StationScenario.SiteId, s.StationId, now, Ct));
        Assert.Null(await s.Txs.ElapseEvidenceWindowAsync(Guid.NewGuid(), now, Ct));
        Assert.Empty(await s.Txs.ExpireAuthorizationsAsync(StationScenario.SiteId, s.StationId, now, Ct));
        Assert.Empty(await s.Txs.CloseGapsAsync(StationScenario.SiteId, s.StationId, now, Ct));

        // N filas.
        var ready = await s.ReadyJobAsync(50);
        var (retryJob, retryAttempt) = await s.SendingJobAsync(51);
        await s.Txc.NotSentAsync(retryAttempt, s.Clock.UtcNow, "rechazada", NotSentCause.ConnectionFailed, 10, 3, s.Clock.UtcNow, Ct);
        s.AdvanceMs(20);
        var retryRow = await s.JobAsync(retryJob, Ct);
        Assert.Equal(retryJob, (await s.Txs.ReleaseRetryAsync(retryJob, retryRow.DeadlineAt, 100, s.Revision.ConfigRevisionId, s.Clock.UtcNow, Ct))!.Id); // J13
        // Otro job queda en RETRY_WAIT con un backoff largo: el barrido lo vence desde RETRY_WAIT (J14).
        var claimRetry = (await s.Claims.TryClaimAsync(retryJob, s.Lease, Ct))!;
        await s.Txc.UnknownAsync(claimRetry.AttemptId, s.Clock.UtcNow, "timeout", s.Clock.UtcNow, Ct);
        var (waitingRetry, waitingAttempt) = await s.SendingJobAsync(55);
        await s.Txc.NotSentAsync(waitingAttempt, s.Clock.UtcNow, "rechazada", NotSentCause.ConnectionFailed, 60_000, 3, s.Clock.UtcNow, Ct);
        await s.RegisterAsync(s.Cycle(52, workOrder: "WO-SIN"));
        var (transmitted, transmittedAttempt) = await s.TransmittedJobAsync(56);
        var (unknown, _) = await s.UnknownJobAsync(57);
        await s.ApproveReprintAsync(unknown, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED", expiresIn: TimeSpan.FromSeconds(1));
        s.AdvanceMs(3000);
        var expired = await s.Txs.ExpireDeadlinesAsync(StationScenario.SiteId, s.StationId, 100, s.Clock.UtcNow, Ct);
        Assert.Equal(
            new[] { (ready, JobStatus.Ready), (waitingRetry, JobStatus.RetryWait) }.OrderBy(x => x.Item1),
            expired.Select(t => (t.Id, t.From)).OrderBy(x => x.Id));                                                     // J03 y J14
        Assert.Single(await s.Txs.ExpireWaitingErpAsync(StationScenario.SiteId, s.StationId, 100, s.Clock.UtcNow, Ct));    // D07
        Assert.Equal(transmittedAttempt, Assert.Single(await s.Txs.ElapsedEvidenceWindowsAsync(StationScenario.SiteId, s.StationId, s.Clock.UtcNow, Ct)));
        var elapsed = (await s.Txs.ElapseEvidenceWindowAsync(transmittedAttempt, s.Clock.UtcNow, Ct))!;                       // A06 + J11
        Assert.Equal((transmitted, JobReason.EvidenceWindowElapsed), (elapsed.Id, elapsed.Reason));
        Assert.Single(await s.Txs.ExpireAuthorizationsAsync(StationScenario.SiteId, s.StationId, s.Clock.UtcNow, Ct));      // R04
    }

    [Fact]
    public async Task Tx_s_out_of_order_and_tx_r0_with_zero_and_n_rows()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        Assert.Empty((await s.Txr0.ExecuteAsync(s.PrinterId, s.Clock.UtcNow, Ct)).OrdinaryJobs);                          // 0 filas
        var older = await s.ReadyJobAsync(60);
        var (_, newer) = await s.SendingJobAsync(61);
        var outOfOrder = Assert.Single(await s.Txs.ExpireOutOfOrderAsync(StationScenario.SiteId, s.StationId, s.Clock.UtcNow, Ct));
        Assert.Equal((older, JobReason.OutOfOrder), (outOfOrder.Id, outOfOrder.Reason));
        var recovery = await s.Txr0.ExecuteAsync(s.PrinterId, s.Clock.UtcNow, Ct);                                        // A04 + J09
        Assert.Single(recovery.OrdinaryJobs);
        Assert.Equal(AttemptOutcome.Unknown, (await s.AttemptsAsync(recovery.OrdinaryJobs[0])).Single(a => a.AttemptId == newer).Outcome);
        Assert.Equal(JobReason.RestartDuringSend, (await s.JobAsync(recovery.OrdinaryJobs[0])).Reason);
    }

    [Fact]
    public async Task Tx_m_and_tx_x_with_zero_and_n_rows()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        // Desbloqueo con 0 filas (estación habilitada) y con 1 fila.
        Assert.False((await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "nada", s.Clock.UtcNow, Ct)).Applied);
        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.Operator, "supervisor.ramos", null, s.Clock.UtcNow, Ct);
        Assert.True((await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "revisado", s.Clock.UtcNow, Ct)).Applied);

        // J04, J16 (A09 antes del job) y J17.
        var ready = await s.ReadyJobAsync(70);
        Assert.Equal("CANCELLED", (await s.Txm.CancelJobAsync(ready, StationScenario.SiteId, s.StationId, "supervisor.ramos", "anulada", s.Clock.UtcNow, Ct))
            .Published!.Outcome);
        await Assert.ThrowsAsync<ManualActionRejectedException>(() =>
            s.Txm.CancelJobAsync(ready, StationScenario.SiteId, s.StationId, "supervisor.ramos", "otra vez", s.Clock.UtcNow, Ct));
        var (u1, _) = await s.UnknownJobAsync(71);
        var negative = await s.Txm.ReconcileNegativeAsync(u1, StationScenario.SiteId, s.StationId, "supervisor.ramos", "tres pruebas",
            "Impresora: job cancelado", "Cola vacía", "Caja sin etiqueta", s.Clock.UtcNow, Ct);
        Assert.Equal((ResultOutcome.Failed, JobReason.ReconciledNotPrinted), (negative.Published!.Outcome, negative.Published.Reason));
        Assert.All(await s.AttemptsAsync(u1), a => Assert.Equal(AttemptOutcome.NotPrinted, a.Outcome));
        var (u2, _) = await s.UnknownJobAsync(72);
        Assert.Equal(ResultOutcome.ClosedUnresolved, (await s.Txm.CloseUnresolvedAsync(u2, StationScenario.SiteId, s.StationId, "supervisor.ramos",
            "rechazada", "REJECTED", s.Clock.UtcNow, Ct)).Published!.Outcome);

        // TX-X: ejecución, intento excepcional transmitido y confirmado (X01).
        var auth = await s.ApproveReprintAsync(u2, JobStatus.ClosedUnresolved, "CLOSED_CARTON_RECOVERED", "ACCEPTED");
        var run = await s.ExecuteReprintAsync(auth);
        await s.TransmitAsync(run.AttemptId, Ct);
        var serial = (await s.JobAsync(u2)).LabelSerial;
        var x01 = await s.EvidenceAsync(s.Verified(72, serial, run.AttemptId, EvidenceType.ManualInspection, "inspector.diaz"));
        Assert.Equal(EvidenceAction.ExceptionalConfirmed, x01.Action);
        var job = await s.JobAsync(u2);
        Assert.Equal((JobStatus.Confirmed, true), (job.Status, job.ConfirmedByExceptionalReprint));
        Assert.Equal("ACCEPTED", x01.Published!.ExceptionalReprint!.DuplicateRisk);
    }

    [Fact]
    public async Task Row_rules_of_section_6_reject_with_their_check_or_unique_constraint()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(80);
        var row = await s.JobAsync(job);

        // UQ_PrintJob_DetectionLabel: un segundo job ordinario para la misma detección y tipo.
        var dup = await s.FailsAsync("""
            INSERT INTO dbo.PrintJob (PrintJobId, DetectionId, SiteId, StationId, PlcEpoch, CycleId, LabelType, ConfigRevisionId, PrinterId,
                WorkOrderId, ErpRevision, ProductId, LotId, LabelSerial, TemplateRevision, Payload, PayloadHash, DeadlineAt, Status)
            SELECT NEWID(), DetectionId, SiteId, StationId, PlcEpoch, CycleId, LabelType, ConfigRevisionId, PrinterId, WorkOrderId, ErpRevision,
                ProductId, LotId, 'OTRO-SERIAL-1', TemplateRevision, Payload, PayloadHash, DeadlineAt, 'READY'
            FROM dbo.PrintJob WHERE PrintJobId = @id
            """, c => c.Parameters.AddWithValue("@id", job), ct: Ct);
        Assert.Equal(2627, dup.Number);
        Assert.Contains("UQ_PrintJob_DetectionLabel", dup.Message, StringComparison.Ordinal);

        // CK_OperatorAction_Target: reconciliación negativa sin una de las tres pruebas.
        var ck = await Assert.ThrowsAsync<SqlException>(() => s.Txm.ReconcileNegativeAsync(job, StationScenario.SiteId, s.StationId, "supervisor.ramos",
            "falta la inspección", "Impresora: cancelado", "Cola vacía", null!, s.Clock.UtcNow, Ct));
        Assert.Equal(547, ck.Number);
        Assert.Contains("CK_OperatorAction_Target", ck.Message, StringComparison.Ordinal);

        // CK_StationConfigRevision_TwoPersons.
        var two = await Assert.ThrowsAsync<SqlException>(() => s.Stations.ActivateRevisionAsync(
            StationScenario.DefaultRevision(s.StationId) with { RevisionNumber = 8, ApprovedBy = "ingeniero.ot" }, Ct));
        Assert.Contains("CK_StationConfigRevision_TwoPersons", two.Message, StringComparison.Ordinal);

        // CK_CartonDetection_GatewayLive y CK_CartonDetection_PlcTimestamp: plazo fabricado en un registro sin cota.
        var gateway = await InsertDetectionAsync(s, 81, "RECOVERED", "REPUBLISH", detectionTimestamp: false, deadlineAfterReceipt: true);
        Assert.Matches(@"\bCK_CartonDetection_(GatewayLive|PlcTimestamp)\b", gateway.Message);
        var plc = await InsertDetectionAsync(s, 82, "OBSERVED", "SUBSCRIPTION", detectionTimestamp: false, deadlineAfterReceipt: true);
        Assert.Contains("CK_CartonDetection_PlcTimestamp", plc.Message, StringComparison.Ordinal);

        // UX_PrintAttempt_JobOpen y UX_PrintAttempt_OrdinarySent.
        var (sending, _) = await s.SendingJobAsync(83);
        var open = await s.FailsAsync("""
            INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, PrinterId, OwnerInstanceId, LeaseToken, Outcome, MayHaveSent,
                AttemptPayloadHash, ClaimedAt)
            SELECT NEWID(), PrintJobId, 9, 'ORDINARY', PrinterId, N'x', 1, 'SENDING', 1, PayloadHash, SYSUTCDATETIME()
            FROM dbo.PrintJob WHERE PrintJobId = @id
            """, c => c.Parameters.AddWithValue("@id", sending), ct: Ct);
        // El insert viola a la vez JobOpen, OrdinarySent y PrinterInFlight; SQL Server informa uno de los tres.
        // Cada índice se prueba por separado en ReferenceCoverageTests.
        Assert.Equal(2601, open.Number);
        Assert.Matches(@"\bUX_PrintAttempt_(JobOpen|OrdinarySent|PrinterInFlight)\b", open.Message);
        _ = row;
    }

    private static async Task<SqlException> InsertDetectionAsync(StationScenario s, ulong cycle, string origin, string transport, bool detectionTimestamp,
        bool deadlineAfterReceipt)
    {
        var id = new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, cycle);
        var reference = transport == "SUBSCRIPTION" ? "PLC_TIMESTAMP" : "GATEWAY_RECEIPT";
        long revision = s.Revision.ConfigRevisionId;
        if (reference == "GATEWAY_RECEIPT")
        {
            revision = (await s.Sql.InTransactionAsync((conn, tx) => Infrastructure.Data.StationConfigStore.InsertRevisionAsync(conn, tx,
                StationScenario.DefaultRevision(s.StationId) with
                {
                    RevisionNumber = 50 + (int)cycle, DeadlineReference = "GATEWAY_RECEIPT", ClockSkewToleranceMs = null, MaxUpstreamLatencyMs = 40,
                }, Ct), Ct)).ConfigRevisionId;
        }

        return await s.FailsAsync($"""
            INSERT INTO dbo.CartonDetection (DetectionId, SiteId, StationId, PlcEpoch, CycleId, IdempotencyKey, SourceFingerprint, Origin, Transport,
                QueueOverflow, DetectionTimestamp, ReceivedAt, DeadlineReference, DeadlineAt, ConfigRevisionId, WorkOrderId, Status)
            VALUES (@id, @site, @station, @epoch, @cycle, @key, 0x00, '{origin}', '{transport}', 0, {(detectionTimestamp ? "@now" : "NULL")}, @now,
                '{reference}', {(deadlineAfterReceipt ? "DATEADD(SECOND, 1, @now)" : "@now")}, @rev, N'WO-2026-4815', 'RECEIVED')
            """, c =>
        {
            c.Parameters.AddWithValue("@id", id.DetectionId);
            c.Parameters.AddWithValue("@site", id.SiteId);
            c.Parameters.AddWithValue("@station", id.StationId);
            c.Parameters.AddWithValue("@epoch", id.PlcEpoch);
            c.Parameters.Add(new SqlParameter("@cycle", System.Data.SqlDbType.Decimal) { Precision = 20, Scale = 0, Value = (decimal)cycle });
            c.Parameters.AddWithValue("@key", id.IdempotencyKey);
            c.Parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime2) { Value = s.Clock.UtcNow });
            c.Parameters.AddWithValue("@rev", revision);
        }, ct: Ct);
    }

    private static async Task<ErpArrivalPlan> ErpPlanAsync(StationScenario s, Guid detectionId, Infrastructure.Data.ErpSnapshot snapshot, ulong cycle)
    {
        var serial = Infrastructure.Data.LabelSerialSource.Format(s.SerialPrefix, await s.Serials.NextAsync(Ct));
        var identity = new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, cycle);
        var label = LabelRenderer.Render(s.Revision, snapshot, identity, serial, null);
        var detection = (await s.DetectionAsync(cycle))!;
        return new ErpArrivalPlan(detectionId, new JobInsert(Guid.NewGuid(), s.Revision.LabelType, s.Revision.PrinterId, snapshot.WorkOrderId,
            snapshot.Revision, snapshot.ProductId, snapshot.LotId, serial, s.Revision.TemplateRevision, label.Payload, label.PayloadHash,
            detection.DeadlineAt), false, DeadlinePolicy.Effective(detection.DeadlineAt, s.Revision, detection), s.Revision.SendMarginMs,
            s.Revision.LabelType, s.Clock.UtcNow, snapshot.Gtin, s.Revision.ConfigRevisionId);
    }
}
