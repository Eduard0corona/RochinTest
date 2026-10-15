using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.IntegrationTests.Schema;

/// <summary>
/// T47 · Cada intento indebido falla con el código exacto que le asigna
/// spec/database/sqlserver_schema.sql (THROW 51001 a 51032) o 52001 en TX-X (SI-01: el catálogo dice
/// "51010 a 51032"). Cada estado previo se construye por las transacciones; la sentencia indebida se
/// ejecuta con el login de la aplicación. Cada caso viola una sola regla, para que el código sea el
/// de esa regla y no el del primer trigger que dispare.
/// </summary>
[Trait("Spec", "T47")]
public sealed class TriggerGuardTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t47g", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<StationScenario> StationAsync(Func<StationRevision, StationRevision>? configure = null) =>
        StationScenario.CreateAsync(_db, configure, Ct);

    private static async Task ExpectAsync(int code, StationScenario s, string sqlText, Action<SqlCommand>? bind = null, string? kind = null)
    {
        var ex = await s.FailsAsync(sqlText, bind, kind, Ct);
        Assert.True(ex.Number == code, $"Se esperaba {code} y se obtuvo {ex.Number}: {ex.Message}\n{sqlText}");
    }

    private static Action<SqlCommand> Id(Guid id) => c => c.Parameters.AddWithValue("@id", id);

    // ------------------------------------------------------------------ inmutabilidad (51001 a 51010, 51016 a 51019)

    [Fact]
    public async Task T51001_station_config_revision_is_immutable()
    {
        var s = await StationAsync();
        await ExpectAsync(51001, s, "UPDATE dbo.StationConfigRevision SET ChangeReason = N'editada' WHERE ConfigRevisionId = @r",
            c => c.Parameters.AddWithValue("@r", s.Revision.ConfigRevisionId));

        // Una revisión que nadie referencia tampoco se borra.
        var orphan = await s.Sql.InTransactionAsync((conn, tx) => StationConfigStore.InsertRevisionAsync(conn, tx,
            StationScenario.DefaultRevision(s.StationId) with { RevisionNumber = 99 }, Ct), Ct);
        await ExpectAsync(51001, s, "DELETE FROM dbo.StationConfigRevision WHERE ConfigRevisionId = @r",
            c => c.Parameters.AddWithValue("@r", orphan.ConfigRevisionId));
    }

    [Fact]
    public async Task T51002_51003_carton_detection_is_audited_and_identity_is_immutable()
    {
        var s = await StationAsync();
        var waiting = await s.RegisterAsync(s.Cycle(10, workOrder: "WO-SIN-SNAPSHOT"), ct: Ct);
        Assert.Equal(DetectionStatus.WaitingErp, waiting.FinalStatus);

        await ExpectAsync(51002, s, "DELETE FROM dbo.CartonDetection WHERE DetectionId = @id", Id(waiting.DetectionId));
        await ExpectAsync(51003, s, "UPDATE dbo.CartonDetection SET WorkOrderId = N'WO-OTRA' WHERE DetectionId = @id", Id(waiting.DetectionId));
        await ExpectAsync(51003, s, "UPDATE dbo.CartonDetection SET DeadlineAt = DATEADD(SECOND, 30, DeadlineAt) WHERE DetectionId = @id",
            Id(waiting.DetectionId));
    }

    [Fact]
    public async Task T51004_51005_51016_print_job_frozen_content_and_result_version()
    {
        var s = await StationAsync();
        var job = await s.ReadyJobAsync(20);

        await ExpectAsync(51004, s, "DELETE FROM dbo.PrintJob WHERE PrintJobId = @id", Id(job));
        foreach (var set in new[] { "Payload = N'^XA^XZ'", "LabelSerial = 'OTRO-000001'", "DeadlineAt = DATEADD(SECOND, 5, DeadlineAt)",
                     "ConfigRevisionId = ConfigRevisionId", "PrinterId = N'OTRA'", "TemplateRevision = N'TPL-X'" })
        {
            await ExpectAsync(51005, s, $"UPDATE dbo.PrintJob SET {set} WHERE PrintJobId = @id", Id(job));
        }

        await s.Txb.ExpireReadyAsync(job, JobReason.DeadlinePassed, s.Clock.UtcNow, Ct);   // ResultVersion = 1
        await ExpectAsync(51016, s, "UPDATE dbo.PrintJob SET ResultVersion = ResultVersion - 1 WHERE PrintJobId = @id", Id(job));
    }

    [Fact]
    public async Task T51006_51007_51017_print_attempt_identity_and_fixed_once_fields()
    {
        var s = await StationAsync();
        var (_, sending) = await s.SendingJobAsync(30);
        await ExpectAsync(51007, s, "UPDATE dbo.PrintAttempt SET OwnerInstanceId = N'otra-instancia' WHERE AttemptId = @id", Id(sending));
        await ExpectAsync(51007, s, "UPDATE dbo.PrintAttempt SET LeaseToken = LeaseToken + 1 WHERE AttemptId = @id", Id(sending));
        await ExpectAsync(51006, s, "DELETE FROM dbo.PrintAttempt WHERE AttemptId = @id", Id(sending));

        await s.TransmitAsync(sending, Ct);
        await ExpectAsync(51017, s, "UPDATE dbo.PrintAttempt SET EvidenceDeadlineAt = DATEADD(SECOND, 60, EvidenceDeadlineAt) WHERE AttemptId = @id", Id(sending));
        await ExpectAsync(51017, s, "UPDATE dbo.PrintAttempt SET WriteCompletedAt = DATEADD(MILLISECOND, 5, WriteCompletedAt) WHERE AttemptId = @id", Id(sending));
    }

    [Fact]
    public async Task T51008_51009_51010_reprint_authorization_approval_data_and_label_removal()
    {
        var s = await StationAsync();
        var (job, attempt) = await s.TransmittedJobAsync(40);
        await s.EvidenceAsync(s.Printed(job, attempt));                                  // CONFIRMED (J10)
        var auth = await s.ApproveReprintAsync(job, JobStatus.Confirmed, "LABEL_DAMAGED", "REPLACES_EXISTING_LABEL");

        await ExpectAsync(51009, s, "UPDATE dbo.ReprintAuthorization SET ApprovedBy = N'otra.persona' WHERE AuthorizationId = @a",
            c => c.Parameters.AddWithValue("@a", auth));
        Assert.True(await s.Txm.RecordOriginalLabelRemovalAsync(auth, "operador.lopez", s.Clock.UtcNow, Ct));
        await ExpectAsync(51010, s, "UPDATE dbo.ReprintAuthorization SET OriginalLabelRemovedBy = N'otro.operador' WHERE AuthorizationId = @a",
            c => c.Parameters.AddWithValue("@a", auth));
        await ExpectAsync(51008, s, "DELETE FROM dbo.ReprintAuthorization WHERE AuthorizationId = @a", c => c.Parameters.AddWithValue("@a", auth));
    }

    [Fact]
    public async Task T51018_51019_operator_action_and_evidence_are_insert_only()
    {
        var s = await StationAsync();
        var noop = await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "operador.lopez", "Estación ya habilitada", s.Clock.UtcNow, Ct);
        Assert.False(noop.Applied);
        await ExpectAsync(51018, s, "UPDATE dbo.OperatorAction SET ReasonText = N'otra' WHERE OperatorActionId = @o",
            c => c.Parameters.AddWithValue("@o", noop.OperatorActionId));
        await ExpectAsync(51018, s, "DELETE FROM dbo.OperatorAction WHERE OperatorActionId = @o", c => c.Parameters.AddWithValue("@o", noop.OperatorActionId));

        var obs = await s.ObserveAsync(s.Applied(999));
        await ExpectAsync(51019, s, "UPDATE dbo.EvidenceObservation SET SourceReference = N'otra' WHERE ObservationId = @o", c => c.Parameters.AddWithValue("@o", obs));
        await ExpectAsync(51019, s, "DELETE FROM dbo.EvidenceObservation WHERE ObservationId = @o", c => c.Parameters.AddWithValue("@o", obs));
    }

    // ------------------------------------------------------------------ máquinas de estado (51011 a 51015) y transiciones prohibidas

    [Fact]
    public async Task T51011_carton_detection_forbidden_transitions()
    {
        var s = await StationAsync();
        var expired = await s.RegisterAsync(s.Cycle(50, detectionTimestamp: s.Clock.UtcNow.AddSeconds(-30)), ct: Ct);
        Assert.Equal(DetectionStatus.Expired, expired.FinalStatus);
        await ExpectAsync(51011, s, "UPDATE dbo.CartonDetection SET Status = 'JOB_CREATED', Reason = NULL WHERE DetectionId = @id", Id(expired.DetectionId));

        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.Operator, "operador.lopez", null, s.Clock.UtcNow, Ct);
        var blocked = await s.RegisterAsync(s.Cycle(51), ct: Ct);
        Assert.Equal(DetectionStatus.Blocked, blocked.FinalStatus);
        Assert.Equal(DetectionReason.StationDisabled, blocked.FinalReason);
        await ExpectAsync(51011, s, "UPDATE dbo.CartonDetection SET Status = 'JOB_CREATED', Reason = NULL WHERE DetectionId = @id", Id(blocked.DetectionId));

        // Una detección no nace en JOB_CREATED: (create) → JOB_CREATED no está sembrada.
        var d = new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, 52);
        await ExpectAsync(51011, s, """
            INSERT INTO dbo.CartonDetection (DetectionId, SiteId, StationId, PlcEpoch, CycleId, IdempotencyKey, SourceFingerprint, Origin, Transport,
                QueueOverflow, DetectionTimestamp, ReceivedAt, DeadlineReference, DeadlineAt, ConfigRevisionId, WorkOrderId, Status)
            VALUES (@id, @site, @station, @epoch, 52, @key, 0x00, 'OBSERVED', 'SUBSCRIPTION', 0, @now, @now, 'PLC_TIMESTAMP',
                DATEADD(SECOND, 1, @now), @rev, N'WO-2026-4815', 'JOB_CREATED')
            """, c =>
        {
            c.Parameters.AddWithValue("@id", d.DetectionId);
            c.Parameters.AddWithValue("@site", d.SiteId);
            c.Parameters.AddWithValue("@station", d.StationId);
            c.Parameters.AddWithValue("@epoch", d.PlcEpoch);
            c.Parameters.AddWithValue("@key", d.IdempotencyKey);
            c.Parameters.Add(new SqlParameter("@now", System.Data.SqlDbType.DateTime2) { Value = s.Clock.UtcNow });
            c.Parameters.AddWithValue("@rev", s.Revision.ConfigRevisionId);
        });
    }

    [Fact]
    public async Task T51012_print_job_forbidden_transitions_of_the_state_machine()
    {
        var s = await StationAsync();
        var (sending, sendingAttempt) = await s.SendingJobAsync(60);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'READY' WHERE PrintJobId = @id", Id(sending));               // SENDING → READY
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'CONFIRMED' WHERE PrintJobId = @id", Id(sending));           // SENDING → CONFIRMED
        // Un solo intento en transmisión por impresora (UX_PrintAttempt_PrinterInFlight): se cierra antes del siguiente claim.
        await s.Txc.UnknownAsync(sendingAttempt, s.Clock.UtcNow, "timeout", s.Clock.UtcNow, Ct);

        var (transmitted, _) = await s.TransmittedJobAsync(61);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'READY' WHERE PrintJobId = @id", Id(transmitted));           // TRANSMITTED → READY
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'RETRY_WAIT', NextAttemptAt = SYSUTCDATETIME() WHERE PrintJobId = @id",
            Id(transmitted));                                                                                                      // TRANSMITTED → RETRY_WAIT

        var (unknown, _) = await s.UnknownJobAsync(62);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'READY', Reason = NULL WHERE PrintJobId = @id", Id(unknown));                // UNKNOWN → READY
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'SENDING', Reason = NULL WHERE PrintJobId = @id", Id(unknown));              // UNKNOWN → SENDING
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'RETRY_WAIT', Reason = NULL, NextAttemptAt = SYSUTCDATETIME() WHERE PrintJobId = @id",
            Id(unknown));                                                                                                                          // UNKNOWN → RETRY_WAIT

        var expired = await s.ReadyJobAsync(63);
        await s.Txb.ExpireReadyAsync(expired, JobReason.DeadlinePassed, s.Clock.UtcNow, Ct);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'READY', Reason = NULL WHERE PrintJobId = @id", Id(expired));               // EXPIRED → READY

        var (failed, failedAttempt) = await s.TransmittedJobAsync(64);
        await s.Txd.DeviceNotPrintedAsync(failedAttempt, "Cancelado por el dispositivo y fuera de su cola", s.Clock.UtcNow, Ct);
        Assert.Equal(JobStatus.Failed, (await s.JobAsync(failed)).Status);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'READY', Reason = NULL WHERE PrintJobId = @id", Id(failed));                // FAILED → READY

        var (retry, retryAttempt) = await s.SendingJobAsync(65);
        await s.Txc.NotSentAsync(retryAttempt, s.Clock.UtcNow, "Conexión rechazada", NotSentCause.ConnectionFailed, 100, 3, s.Clock.UtcNow, Ct);
        Assert.Equal(JobStatus.RetryWait, (await s.JobAsync(retry)).Status);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'SENDING', NextAttemptAt = NULL WHERE PrintJobId = @id", Id(retry));       // RETRY_WAIT → SENDING

        // Un terminal no tiene salidas ordinarias.
        var cancelled = await s.ReadyJobAsync(66);
        await s.Txm.CancelJobAsync(cancelled, StationScenario.SiteId, s.StationId, "supervisor.ramos", "Orden anulada", s.Clock.UtcNow, Ct);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'READY', Reason = NULL WHERE PrintJobId = @id", Id(cancelled));
    }

    [Fact]
    public async Task T51012_manual_transitions_without_operator_action_or_with_an_older_or_foreign_one()
    {
        var s = await StationAsync();
        var ready = await s.ReadyJobAsync(70);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'CANCELLED', Reason = 'CANCELLED_BY_OPERATOR' WHERE PrintJobId = @id", Id(ready));

        // OperatorAction de otro job, o con otro TargetStatus.
        var (unknown, _) = await s.UnknownJobAsync(71);
        var other = await s.ReadyJobAsync(72);
        var foreign = await InsertOperatorActionAsync(s, OperatorActionType.CloseUnresolved, other, JobStatus.ClosedUnresolved, cartonDisposition: "REJECTED");
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'CLOSED_UNRESOLVED', Reason = 'CLOSED_CARTON_REJECTED', LastOperatorActionId = @oa WHERE PrintJobId = @id",
            c => { c.Parameters.AddWithValue("@oa", foreign); c.Parameters.AddWithValue("@id", unknown); });
        var wrongTarget = await InsertOperatorActionAsync(s, OperatorActionType.CancelJob, unknown, JobStatus.Cancelled);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'CLOSED_UNRESOLVED', Reason = 'CLOSED_CARTON_REJECTED', LastOperatorActionId = @oa WHERE PrintJobId = @id",
            c => { c.Parameters.AddWithValue("@oa", wrongTarget); c.Parameters.AddWithValue("@id", unknown); });

        // Sin OperatorAction: ningún UNKNOWN → CLOSED_UNRESOLVED o FAILED.
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'CLOSED_UNRESOLVED', Reason = 'CLOSED_CARTON_REJECTED' WHERE PrintJobId = @id", Id(unknown));
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'FAILED', Reason = 'RECONCILED_NOT_PRINTED' WHERE PrintJobId = @id", Id(unknown));

        // Respaldo anterior: un job que ya usó una OperatorAction no avanza con la misma (debe ser mayor).
        var closed = await s.Txm.CloseUnresolvedAsync(unknown, StationScenario.SiteId, s.StationId, "operador.lopez", "Caja rechazada", "REJECTED", s.Clock.UtcNow, Ct);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'UNKNOWN', Reason = 'WRITE_INTERRUPTED', LastOperatorActionId = @oa WHERE PrintJobId = @id",
            c => { c.Parameters.AddWithValue("@oa", closed.OperatorActionId); c.Parameters.AddWithValue("@id", unknown); });

        // Mover solo el puntero de respaldo, sin transición, también se rechaza.
        var unknown2 = (await s.UnknownJobAsync(73)).JobId;
        var fresh = await InsertOperatorActionAsync(s, OperatorActionType.CloseUnresolved, unknown2, JobStatus.ClosedUnresolved, cartonDisposition: "QUARANTINED");
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET LastOperatorActionId = @oa WHERE PrintJobId = @id",
            c => { c.Parameters.AddWithValue("@oa", fresh); c.Parameters.AddWithValue("@id", unknown2); });
    }

    [Fact]
    public async Task T51012_exception_and_correction_without_backing()
    {
        var s = await StationAsync();
        var (unknown, ordinary) = await s.UnknownJobAsync(80);

        // T34: SESSION_CONTEXT EXCEPTION y el job a CONFIRMED sin intento excepcional.
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'CONFIRMED', Reason = NULL, ConfirmedByExceptionalReprint = 1 WHERE PrintJobId = @id",
            Id(unknown), "EXCEPTION");
        // Puntero a un intento que no es EXCEPTIONAL_REPRINT (el ordinario).
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'CONFIRMED', Reason = NULL, ConfirmedByExceptionalReprint = 1, LastExceptionalAttemptId = @a WHERE PrintJobId = @id",
            c => { c.Parameters.AddWithValue("@a", ordinary); c.Parameters.AddWithValue("@id", unknown); }, "EXCEPTION");

        // Corrección sin marca de sesión, y con marca pero sin lectura contradictoria.
        var (confirmed, confirmedAttempt) = await s.TransmittedJobAsync(81);
        await s.EvidenceAsync(s.Printed(confirmed, confirmedAttempt));
        Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(confirmed)).Status);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH' WHERE PrintJobId = @id", Id(confirmed));
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH' WHERE PrintJobId = @id", Id(confirmed), "CORRECTION");
        // Una lectura APPLIED del ciclo no es contradicción (J18 exige VERIFIED con otro serial).
        await s.ObserveAsync(s.Applied(81));
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH' WHERE PrintJobId = @id", Id(confirmed), "CORRECTION");
        // Con la marca equivocada (EXCEPTION) tampoco.
        await s.ObserveAsync(s.Verified(81, "P01LZZ-000000000001"));
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH' WHERE PrintJobId = @id", Id(confirmed), "EXCEPTION");
    }

    [Fact]
    public async Task T51012_changing_only_the_reason_is_rejected()
    {
        var s = await StationAsync();
        var (unknown, _) = await s.UnknownJobAsync(90);
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Reason = 'RESTART_DURING_SEND' WHERE PrintJobId = @id", Id(unknown));
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Reason = 'EVIDENCE_WINDOW_ELAPSED' WHERE PrintJobId = @id", Id(unknown));
        await ExpectAsync(51012, s, "UPDATE dbo.PrintJob SET Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED' WHERE PrintJobId = @id", Id(unknown));
    }

    [Fact]
    public async Task T51013_print_attempt_forbidden_transitions_and_manual_without_backing()
    {
        var s = await StationAsync();

        // UNKNOWN → NOT_SENT: un intento que el arranque dejó UNKNOWN (sin marcas de escritura).
        var (restartJob, restarted) = await s.SendingJobAsync(100);
        await s.Txr0.ExecuteAsync(s.PrinterId, s.Clock.UtcNow, Ct);
        await ExpectAsync(51013, s, "UPDATE dbo.PrintAttempt SET Outcome = 'NOT_SENT', MayHaveSent = 0 WHERE AttemptId = @id", Id(restarted));

        // A09 sin OperatorAction, con una de otro job y con una anterior al claim.
        await ExpectAsync(51013, s, "UPDATE dbo.PrintAttempt SET Outcome = 'NOT_PRINTED' WHERE AttemptId = @id", Id(restarted));
        var otherJob = await s.ReadyJobAsync(101);
        var foreign = await InsertOperatorActionAsync(s, OperatorActionType.ReconcileNegative, otherJob, JobStatus.Failed, proofs: true);
        await ExpectAsync(51013, s, "UPDATE dbo.PrintAttempt SET Outcome = 'NOT_PRINTED', OperatorActionId = @oa WHERE AttemptId = @id",
            c => { c.Parameters.AddWithValue("@oa", foreign); c.Parameters.AddWithValue("@id", restarted); });

        var early = await InsertOperatorActionAsync(s, OperatorActionType.ReconcileNegative, null, null, proofs: true, deferredJob: true);
        s.AdvanceMs(50);
        var (lateJob, late) = await s.SendingJobAsync(102);
        await s.Txc.UnknownAsync(late, s.Clock.UtcNow, "timeout", s.Clock.UtcNow, Ct);
        var olderThanClaim = await InsertOperatorActionAsync(s, OperatorActionType.ReconcileNegative, lateJob, JobStatus.Failed, proofs: true,
            performedAt: s.Clock.UtcNow.AddSeconds(-10));
        await ExpectAsync(51013, s, "UPDATE dbo.PrintAttempt SET Outcome = 'NOT_PRINTED', OperatorActionId = @oa WHERE AttemptId = @id",
            c => { c.Parameters.AddWithValue("@oa", olderThanClaim); c.Parameters.AddWithValue("@id", late); });
        _ = early;

        // NOT_SENT → SENDING: un intento es inmutable; un reintento es un intento nuevo.
        var (retryJob, notSent) = await s.SendingJobAsync(103);
        await s.Txc.NotSentAsync(notSent, s.Clock.UtcNow, "Conexión rechazada", NotSentCause.ConnectionFailed, 100, 3, s.Clock.UtcNow, Ct);
        await ExpectAsync(51013, s, "UPDATE dbo.PrintAttempt SET Outcome = 'SENDING', MayHaveSent = 1 WHERE AttemptId = @id", Id(notSent));
        _ = restartJob;
        _ = retryJob;
    }

    [Fact]
    public async Task Transmitted_to_not_sent_is_stopped_by_ck_print_attempt_not_sent()
    {
        // TRANSMITTED → NOT_SENT: CK_PrintAttempt_NotSent lo detiene antes del trigger, porque un intento
        // TRANSMITTED tiene marcas de escritura y uno NOT_SENT no puede tenerlas.
        var s = await StationAsync();
        var (_, attempt) = await s.TransmittedJobAsync(110);
        var ex = await s.FailsAsync("UPDATE dbo.PrintAttempt SET Outcome = 'NOT_SENT', MayHaveSent = 0 WHERE AttemptId = @id", Id(attempt), ct: Ct);
        Assert.Equal(547, ex.Number);
        Assert.Contains("CK_PrintAttempt_NotSent", ex.Message, StringComparison.Ordinal);
        // Borrar las marcas para burlar el CHECK lo detiene TR_PrintAttempt_Immutable.
        await ExpectAsync(51017, s, "UPDATE dbo.PrintAttempt SET Outcome = 'NOT_SENT', MayHaveSent = 0, WriteStartedAt = NULL, WriteCompletedAt = NULL WHERE AttemptId = @id", Id(attempt));
    }

    [Fact]
    public async Task T51014_closed_unrecovered_gap_never_becomes_recovered()
    {
        var s = await StationAsync();
        await s.RegisterAsync(s.Cycle(300), ct: Ct);
        s.AdvanceMs(5);
        var withGap = await s.RegisterAsync(s.Cycle(310), new Orchestration.SequenceDecision
        {
            Gap = new GapOpen(301, 309, s.Clock.UtcNow.AddMilliseconds(s.Revision.GapGraceMs)),
        }, Ct);
        Assert.NotNull(withGap.GapId);
        s.AdvanceMs(s.Revision.GapGraceMs + 1);
        var closed = Assert.Single(await s.Txs.CloseGapsAsync(StationScenario.SiteId, s.StationId, s.Clock.UtcNow, Ct));
        Assert.Equal(GapStatus.ClosedUnrecovered, closed.Status);
        Assert.Equal(9UL, closed.UnrecoveredCount);

        await ExpectAsync(51014, s, "UPDATE dbo.SequenceGap SET Status = 'RECOVERED', UnrecoveredCount = 0 WHERE GapId = @g",
            c => c.Parameters.AddWithValue("@g", closed.GapId));
    }

    [Fact]
    public async Task T51015_52001_reprint_authorization_state_machine_and_tx_x()
    {
        var s = await StationAsync();
        var (job, _) = await s.UnknownJobAsync(120);
        var auth = await s.ApproveReprintAsync(job, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");

        // APPROVED → EXECUTED sin SESSION_CONTEXT EXCEPTION.
        await ExpectAsync(51015, s, "UPDATE dbo.ReprintAuthorization SET Status = 'EXECUTED', ExecutedAt = SYSUTCDATETIME() WHERE AuthorizationId = @a",
            c => c.Parameters.AddWithValue("@a", auth));

        var executed = await s.ExecuteReprintAsync(auth);
        // EXECUTED → APPROVED: cada reimpresión necesita su propia autorización.
        await ExpectAsync(51015, s, "UPDATE dbo.ReprintAuthorization SET Status = 'APPROVED', ExecutedAt = NULL WHERE AuthorizationId = @a",
            c => c.Parameters.AddWithValue("@a", auth));
        // 52001: una autorización ya usada no se ejecuta otra vez.
        var reuse = await Assert.ThrowsAsync<SqlException>(() => s.ExecuteReprintAsync(auth));
        Assert.Equal(TxxReprint.NotExecutable, reuse.Number);
        _ = executed;

        // 52001 con una autorización revocada.
        var (job2, _) = await s.UnknownJobAsync(121);
        var revoked = await s.ApproveReprintAsync(job2, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");
        Assert.True(await s.Txm.RevokeReprintAsync(revoked, "supervisor.ramos", s.Clock.UtcNow, Ct));
        var ex = await Assert.ThrowsAsync<SqlException>(() => s.ExecuteReprintAsync(revoked));
        Assert.Equal(TxxReprint.NotExecutable, ex.Number);
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintAttempt WHERE AuthorizationId = @a", c => c.Parameters.AddWithValue("@a", revoked)));
    }

    // ------------------------------------------------------------------ guardas entre tablas (51020 a 51025)

    [Fact]
    public async Task T51020_ordinary_attempt_only_with_the_job_in_sending_its_printer_and_payload()
    {
        var s = await StationAsync();
        var ready = await s.ReadyJobAsync(130);
        await ExpectAsync(51020, s, InsertAttempt("ORDINARY", "NOT_SENT", 0), BindAttempt(ready, s.PrinterId, s.Lease.FencingToken, s.OwnerInstanceId, 9, null));

        var (sending, _) = await s.SendingJobAsync(131);
        // Job en SENDING pero otra impresora (NOT_SENT, para no chocar con los índices de intento abierto).
        await ExpectAsync(51020, s, InsertAttempt("ORDINARY", "NOT_SENT", 0), BindAttempt(sending, "PRN-OTRA", s.Lease.FencingToken, s.OwnerInstanceId, 9, null));
        // Job en SENDING, su impresora, pero otro payload.
        await ExpectAsync(51020, s, InsertAttempt("ORDINARY", "NOT_SENT", 0, payloadHash: "0x" + new string('A', 64)),
            BindAttempt(sending, s.PrinterId, s.Lease.FencingToken, s.OwnerInstanceId, 9, null));
    }

    [Fact]
    public async Task T51021_exceptional_attempt_requires_executed_authorization_context_and_manual_printer()
    {
        var s = await StationAsync();
        var (job, _) = await s.UnknownJobAsync(140);
        var auth = await s.ApproveReprintAsync(job, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");
        // Autorización APPROVED (no EXECUTED), con y sin marca de sesión.
        await ExpectAsync(51021, s, InsertAttempt("EXCEPTIONAL_REPRINT", "NOT_SENT", 0), BindAttempt(job, s.ManualPrinterId, s.ManualLease.FencingToken, s.OwnerInstanceId, 9, auth));
        await ExpectAsync(51021, s, InsertAttempt("EXCEPTIONAL_REPRINT", "NOT_SENT", 0), BindAttempt(job, s.ManualPrinterId, s.ManualLease.FencingToken, s.OwnerInstanceId, 9, auth),
            "EXCEPTION");
    }

    [Fact]
    public async Task T51022_confirmation_requires_matching_serial_level_correlation_and_evidence_after_claim()
    {
        var s = await StationAsync(r => r with { MinConfirmationLevel = EvidenceLevel.Verified });
        var (job, attempt) = await s.TransmittedJobAsync(150);
        var serial = (await s.JobAsync(job)).LabelSerial;

        // Mismo serial con otra capitalización: la intercalación binaria los distingue.
        var caseDiffers = await s.ObserveAsync(s.Verified(150, serial.ToLowerInvariant()));
        await ExpectAsync(51022, s, Confirm(), BindConfirm(attempt, caseDiffers));
        // Nivel insuficiente: PRINTED con mínimo VERIFIED.
        var printed = await s.ObserveAsync(s.Printed(job, attempt));
        await ExpectAsync(51022, s, Confirm(), BindConfirm(attempt, printed));
        // Correlación con otro ciclo.
        var otherCycle = await s.ObserveAsync(s.Verified(151, serial));
        await ExpectAsync(51022, s, Confirm(), BindConfirm(attempt, otherCycle));
        // Recibida antes del claim (observación registrada con ReceivedAt anterior).
        var before = await s.Txd.RecordAsync(s.Verified(150, serial), s.Clock.UtcNow.AddMinutes(-5), Ct);
        await ExpectAsync(51022, s, Confirm(), BindConfirm(attempt, before));

        // La misma regla con la evidencia correcta sí confirma.
        var good = await s.ObserveAsync(s.Verified(150, serial));
        Assert.Equal(1, await s.ExecAsync(Confirm(), BindConfirm(attempt, good), ct: Ct));
    }

    [Fact]
    public async Task T51023_transmitted_without_evidence_deadline_when_the_station_requires_evidence()
    {
        var s = await StationAsync();
        var (_, attempt) = await s.SendingJobAsync(160);
        await ExpectAsync(51023, s, "UPDATE dbo.PrintAttempt SET Outcome = 'TRANSMITTED', WriteStartedAt = SYSUTCDATETIME(), WriteCompletedAt = SYSUTCDATETIME() WHERE AttemptId = @id",
            Id(attempt));
    }

    [Fact]
    public async Task T51024_authorization_must_match_the_real_job_state_manual_printer_and_risk()
    {
        var s = await StationAsync();
        var (job, _) = await s.UnknownJobAsync(170);
        // Estado previo distinto del real (el job está UNKNOWN).
        var ex = await Assert.ThrowsAsync<SqlException>(() => s.ApproveReprintAsync(job, JobStatus.Failed, "PROVEN_NOT_PRINTED", "NONE_PROVEN"));
        Assert.Equal(51024, ex.Number);
        // Impresora de línea como destino (OT-16).
        ex = await Assert.ThrowsAsync<SqlException>(() => s.ApproveReprintAsync(job, JobStatus.Unknown, "UNKNOWN_NOT_RESOLVED", "ACCEPTED", targetPrinter: s.PrinterId));
        Assert.Equal(51024, ex.Number);

        // NONE_PROVEN con un intento que pudo imprimir: un job EXPIRED con una reimpresión transmitida.
        var expired = await s.ReadyJobAsync(171);
        await s.Txb.ExpireReadyAsync(expired, JobReason.DeadlinePassed, s.Clock.UtcNow, Ct);
        var first = await s.ApproveReprintAsync(expired, JobStatus.Expired, "EXPIRED_CARTON_HELD", "NONE_PROVEN");
        var run = await s.ExecuteReprintAsync(first);
        await s.TransmitAsync(run.AttemptId, Ct, evidenceWindowMs: 60_000);
        ex = await Assert.ThrowsAsync<SqlException>(() => s.ApproveReprintAsync(expired, JobStatus.Expired, "EXPIRED_CARTON_HELD", "NONE_PROVEN"));
        Assert.Equal(51024, ex.Number);
    }

    [Fact]
    public async Task T51025_state_without_label_rejected_while_an_attempt_may_have_printed()
    {
        var s = await StationAsync();
        var (transmitted, _) = await s.TransmittedJobAsync(180);
        // TRANSMITTED → FAILED es J12 (sembrada), pero el intento TRANSMITTED pudo imprimir.
        await ExpectAsync(51025, s, "UPDATE dbo.PrintJob SET Status = 'FAILED', Reason = 'PRINTER_UNAVAILABLE' WHERE PrintJobId = @id", Id(transmitted));

        // J16 con su OperatorAction pero sin pasar antes los intentos a NOT_PRINTED.
        var (unknown, _) = await s.UnknownJobAsync(181);
        var oa = await InsertOperatorActionAsync(s, OperatorActionType.ReconcileNegative, unknown, JobStatus.Failed, proofs: true);
        await ExpectAsync(51025, s, "UPDATE dbo.PrintJob SET Status = 'FAILED', Reason = 'RECONCILED_NOT_PRINTED', LastOperatorActionId = @oa WHERE PrintJobId = @id",
            c => { c.Parameters.AddWithValue("@oa", oa); c.Parameters.AddWithValue("@id", unknown); });
    }

    // ------------------------------------------------------------------ orden de envío, estación y épocas (51026 a 51032)

    [Fact]
    public async Task T51026_51027_51028_printer_send_order_only_moves_forward()
    {
        var s = await StationAsync();
        await s.SendingJobAsync(200);
        var p = (Action<SqlCommand>)(c => c.Parameters.AddWithValue("@p", s.PrinterId));
        await ExpectAsync(51028, s, "UPDATE dbo.PrinterSendOrder SET LastCycleId = LastCycleId - 1 WHERE PrinterId = @p", p);
        await ExpectAsync(51028, s, "UPDATE dbo.PrinterSendOrder SET LastPlcEpoch = NULL, LastEpochAcceptedAt = NULL, LastCycleId = NULL, LastPrintJobId = NULL WHERE PrinterId = @p", p);
        await ExpectAsync(51027, s, "UPDATE dbo.PrinterSendOrder SET StationId = N'OTRA' WHERE PrinterId = @p", p);
        await ExpectAsync(51026, s, "DELETE FROM dbo.PrinterSendOrder WHERE PrinterId = @p", p);
    }

    [Fact]
    public async Task T51029_51030_station_config_is_not_deleted_and_unblocks_only_with_a_new_unblock_action()
    {
        var s = await StationAsync();
        var key = (Action<SqlCommand>)(c => { c.Parameters.AddWithValue("@site", StationScenario.SiteId); c.Parameters.AddWithValue("@st", s.StationId); });
        await ExpectAsync(51029, s, "DELETE FROM dbo.StationConfig WHERE SiteId = @site AND StationId = @st", key);

        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.IdentityCollision, "SYSTEM", null, s.Clock.UtcNow, Ct);
        const string unblock = "UPDATE dbo.StationConfig SET LabelingEnabled = 1, BlockReason = NULL, BlockedAt = NULL, BlockedBy = NULL{0} WHERE SiteId = @site AND StationId = @st";
        await ExpectAsync(51030, s, string.Format(System.Globalization.CultureInfo.InvariantCulture, unblock, string.Empty), key);
        var wrongType = await InsertOperatorActionAsync(s, OperatorActionType.ConfirmEpoch, null, null);
        await ExpectAsync(51030, s, string.Format(System.Globalization.CultureInfo.InvariantCulture, unblock, ", LastOperatorActionId = @oa"),
            c => { key(c); c.Parameters.AddWithValue("@oa", wrongType); });

        // Con una UNBLOCK_STATION nueva sí; después, reutilizarla (respaldo anterior) para un nuevo desbloqueo no.
        var first = await s.Txm.UnblockStationAsync(StationScenario.SiteId, s.StationId, "supervisor.ramos", "Colisión revisada", s.Clock.UtcNow, Ct);
        Assert.True(first.Applied);
        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.Operator, "supervisor.ramos", null, s.Clock.UtcNow, Ct);
        await ExpectAsync(51030, s, string.Format(System.Globalization.CultureInfo.InvariantCulture, unblock, ", LastOperatorActionId = @oa"),
            c => { key(c); c.Parameters.AddWithValue("@oa", first.OperatorActionId); });
    }

    [Fact]
    public async Task T51031_51032_epochs_are_immutable_and_operator_confirmation_needs_confirm_epoch()
    {
        var s = await StationAsync();
        var key = (Action<SqlCommand>)(c => { c.Parameters.AddWithValue("@st", s.StationId); });
        await ExpectAsync(51031, s, "UPDATE dbo.PlcEpochHistory SET AcceptedAt = DATEADD(DAY, -1, AcceptedAt) WHERE StationId = @st", key);
        // Una época sin detecciones tampoco se borra.
        await s.Epochs.InsertAsync(new PlcEpoch(StationScenario.SiteId, s.StationId, 43, "PLC", PlcEpochStore.AutoIncrementByOne, 1, s.Clock.UtcNow,
            "SYSTEM", s.Revision.ConfigRevisionId, null), Ct);
        await ExpectAsync(51031, s, "DELETE FROM dbo.PlcEpochHistory WHERE StationId = @st AND PlcEpoch = 43", key);

        var unblock = await InsertOperatorActionAsync(s, OperatorActionType.UnblockStation, null, null);
        var ex = await Assert.ThrowsAsync<SqlException>(() => s.Epochs.InsertAsync(new PlcEpoch(StationScenario.SiteId, s.StationId, 44, "PLC",
            PlcEpochStore.OperatorConfirmed, 1, s.Clock.UtcNow.AddSeconds(1), "supervisor.ramos", s.Revision.ConfigRevisionId, unblock), Ct));
        Assert.Equal(51032, ex.Number);

        // Con CONFIRM_EPOCH sí.
        var ok = await s.Txm.ConfirmEpochAsync(StationScenario.SiteId, s.StationId, 44, 1, s.Revision.ConfigRevisionId, "supervisor.ramos",
            "Reinicio confirmado por OT", s.Clock.UtcNow.AddSeconds(2), Ct);
        Assert.True(ok.Applied);
    }

    // ------------------------------------------------------------------ utilidades

    private static string InsertAttempt(string kind, string outcome, int mayHaveSent, string? payloadHash = null) => $"""
        INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken,
            Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt)
        SELECT NEWID(), pj.PrintJobId, @n, '{kind}', @auth, @printer, @owner, @token, '{outcome}', {mayHaveSent},
            {payloadHash ?? "pj.PayloadHash"}, SYSUTCDATETIME()
        FROM dbo.PrintJob AS pj WHERE pj.PrintJobId = @job
        """;

    private static Action<SqlCommand> BindAttempt(Guid job, string printer, long token, string owner, int number, long? authorization) => c =>
    {
        c.Parameters.AddWithValue("@job", job);
        c.Parameters.AddWithValue("@printer", printer);
        c.Parameters.AddWithValue("@token", token);
        c.Parameters.AddWithValue("@owner", owner);
        c.Parameters.AddWithValue("@n", number);
        c.Parameters.Add(new SqlParameter("@auth", System.Data.SqlDbType.BigInt) { Value = (object?)authorization ?? DBNull.Value });
    };

    private static string Confirm() =>
        "UPDATE dbo.PrintAttempt SET Outcome = 'CONFIRMED', ConfirmingObservationId = @obs WHERE AttemptId = @id";

    private static Action<SqlCommand> BindConfirm(Guid attempt, long observation) => c =>
    {
        c.Parameters.AddWithValue("@id", attempt);
        c.Parameters.AddWithValue("@obs", observation);
    };

    private static async Task<long> InsertOperatorActionAsync(StationScenario s, string type, Guid? job, string? target, bool proofs = false,
        string? cartonDisposition = null, DateTime? performedAt = null, bool deferredJob = false)
    {
        if (deferredJob)
        {
            // Acción que no se puede ligar a un job todavía: se registra como auditoría (RESOLVE_ANOMALY).
            type = OperatorActionType.ResolveAnomaly;
        }

        return await s.Sql.InTransactionAsync((conn, tx) => TxmManualActions.InsertOperatorActionAsync(conn, tx, new OperatorActionInput(
            type, StationScenario.SiteId, s.StationId, job, target, "operador.lopez", "Prueba T47",
            proofs ? "Dispositivo: job cancelado" : null, proofs ? "Cola vacía" : null, proofs ? "Caja sin etiqueta" : null, cartonDisposition),
            performedAt ?? s.Clock.UtcNow, Ct), Ct);
    }
}
