using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;

namespace CartonPrinting.IntegrationTests.Operations;

/// <summary>
/// Reimpresión excepcional de punta a punta (Fase 5): la solicita una persona y la aprueba otra por la Operations API
/// (R01); la ejecuta el sender de la impresora manual con su lease (TX-X, R02) por su única conexión, contra la
/// impresora simulada; la confirma solo evidencia que referencia al intento. SQL Server real.
/// </summary>
public sealed class ReprintTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f5rep", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement LastResult(IReadOnlyList<JsonElement> results) => results.MaxBy(r => r.GetProperty("resultVersion").GetInt32());

    private static async Task<Guid> RequestAsync(OperationsHost ops, Guid job, string reason, string risk, string requestedBy = OperationsHost.Operator)
    {
        using var http = ops.As(requestedBy);
        var (status, body) = await http.CallAsync("api/ops/reprints/requests", new
        {
            printJobId = job, reason, duplicateRisk = risk, cartonReference = "CJ-" + job.ToString("N")[..6],
            inspectionEvidence = "Caja retenida fuera de la línea e inspeccionada", reprintMarker = true,
        }, Ct);
        Assert.Equal(201, status);
        return body.GetProperty("requestId").GetGuid();
    }

    private static async Task<(int Status, JsonElement Body)> ApproveAsync(OperationsHost ops, Guid requestId, string approvedBy = OperationsHost.Supervisor)
    {
        using var http = ops.As(approvedBy);
        return await http.CallAsync($"api/ops/reprints/requests/{requestId}/approve", null, Ct);
    }

    private static async Task<long> AuthorizeAsync(OperationsHost ops, Guid job, string reason, string risk)
    {
        var (status, body) = await ApproveAsync(ops, await RequestAsync(ops, job, reason, risk));
        Assert.Equal(201, status);
        return body.GetProperty("authorizationId").GetInt64();
    }

    /// <summary>
    /// T34 · Reimpresión con incertidumbre. Job UNKNOWN y caja retenida.
    /// Sin autorización ejecutable, el sender no ejecuta nada, y quien solicita no puede aprobar (403).
    /// Un intento ORDINARY sobre el job UNKNOWN y un paso a CONFIRMED sin intento excepcional los rechaza SQL.
    /// Con la autorización aprobada por otra persona: R01 APPROVED, R02 EXECUTED, el intento EXCEPTIONAL_REPRINT se
    /// confirma con evidencia que lo referencia, y el job queda CONFIRMED por X01. El PrintJobResult lleva
    /// exceptionalReprint con duplicateRisk ACCEPTED.
    /// </summary>
    [Fact]
    [Trait("Spec", "T34")]
    public async Task An_uncertain_reprint_needs_a_second_person_and_is_confirmed_only_by_evidence_of_its_attempt()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        await using var rig = ManualRig.Create(s);
        var (job, _) = await s.UnknownJobAsync(820, Ct);

        Assert.Null((await rig.Sender.ExecuteAsync(987_654, Ct)).Execution);
        var request = await RequestAsync(ops, job, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");
        Assert.Equal(403, (await ApproveAsync(ops, request, approvedBy: OperationsHost.Operator)).Status);
        Assert.Equal(0, await s.CountAsync("FROM dbo.ReprintAuthorization", null, Ct));
        Assert.Null(await rig.Sender.ExecuteNextAsync(Ct));
        Assert.Equal(0L, rig.Printer.BytesReceived);

        var ordinary = await s.FailsAsync("""
            INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken, Outcome,
                MayHaveSent, AttemptPayloadHash, ClaimedAt)
            SELECT NEWID(), PrintJobId, 2, 'ORDINARY', NULL, PrinterId, @owner, @token, 'SENDING', 1, PayloadHash, SYSUTCDATETIME()
            FROM dbo.PrintJob WHERE PrintJobId = @id
            """, c => { c.Parameters.AddWithValue("@id", job); c.Parameters.AddWithValue("@owner", s.OwnerInstanceId); c.Parameters.AddWithValue("@token", s.Lease.FencingToken); },
            ct: Ct);
        Assert.Equal(2601, ordinary.Number);   // UX_PrintAttempt_OrdinarySent: un solo intento ORDINARY que pudo imprimir por job (I03)
        Assert.Contains("UX_PrintAttempt_OrdinarySent", ordinary.Message, StringComparison.Ordinal);
        var guarded = await s.FailsAsync("""
            INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken, Outcome,
                MayHaveSent, AttemptPayloadHash, ClaimedAt)
            SELECT NEWID(), PrintJobId, 2, 'ORDINARY', NULL, PrinterId, @owner, @token, 'NOT_SENT', 0, PayloadHash, SYSUTCDATETIME()
            FROM dbo.PrintJob WHERE PrintJobId = @id
            """, c => { c.Parameters.AddWithValue("@id", job); c.Parameters.AddWithValue("@owner", s.OwnerInstanceId); c.Parameters.AddWithValue("@token", s.Lease.FencingToken); },
            ct: Ct);
        Assert.Equal(51020, guarded.Number);   // TR_PrintAttempt_Guard: un intento ORDINARY exige el job en SENDING
        var unbacked = await s.FailsAsync("UPDATE dbo.PrintJob SET Status = 'CONFIRMED', Reason = NULL, ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id",
            c => c.Parameters.AddWithValue("@id", job), "EXCEPTION", Ct);
        Assert.Equal(51012, unbacked.Number);   // TR_PrintJob_StateMachine: X01 sin intento excepcional que lo respalde
        var resend = await s.FailsAsync("UPDATE dbo.PrintJob SET Status = 'SENDING', Reason = NULL WHERE PrintJobId = @id",
            c => c.Parameters.AddWithValue("@id", job), ct: Ct);
        Assert.Equal(51012, resend.Number);   // TR_PrintJob_StateMachine: UNKNOWN → SENDING está prohibido (I03)

        var (approved, body) = await ApproveAsync(ops, request);
        Assert.Equal(201, approved);
        var authorization = body.GetProperty("authorizationId").GetInt64();
        Assert.Equal(s.ManualPrinterId, body.GetProperty("targetPrinterId").GetString());
        Assert.Equal(("APPROVED", OperationsHost.Operator, OperationsHost.Supervisor), Assert.Single(await s.QueryAsync(
            "SELECT Status, RequestedBy, ApprovedBy FROM dbo.ReprintAuthorization WHERE AuthorizationId = @id",
            c => c.Parameters.AddWithValue("@id", authorization), r => (r.GetString(0), r.GetString(1), r.GetString(2)), Ct)));

        var executed = (await rig.Sender.ExecuteNextAsync(Ct))!;
        Assert.Equal((authorization, true), (executed.AuthorizationId, executed.WriteInvoked));
        Assert.Equal("EXECUTED", await s.ScalarAsync<string>("SELECT Status FROM dbo.ReprintAuthorization WHERE AuthorizationId = @id",
            c => c.Parameters.AddWithValue("@id", authorization), Ct));
        var serial = (await s.JobAsync(job, Ct)).LabelSerial;
        Assert.Equal(1, rig.Printer.TransmissionsOf(serial));
        var reprint = (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint);
        Assert.Equal((AttemptOutcome.Transmitted, s.ManualPrinterId), (reprint.Outcome, reprint.PrinterId));

        await rig.Evidence.SubmitAsync(s.Verified(820, serial, reprint.AttemptId), Ct);
        var row = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Confirmed, true), (row.Status, row.ConfirmedByExceptionalReprint));
        Assert.Equal(AttemptOutcome.Confirmed, (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint).Outcome);
        var result = LastResult(await s.ResultsAsync(job, Ct));
        var exceptional = result.GetProperty("exceptionalReprint");
        Assert.Equal(("CONFIRMED", authorization, "ACCEPTED", OperationsHost.Supervisor), (result.GetProperty("outcome").GetString(),
            exceptional.GetProperty("authorizationId").GetInt64(), exceptional.GetProperty("duplicateRisk").GetString(),
            exceptional.GetProperty("approvedBy").GetString()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ExceptionalReprints, ("risk", "ACCEPTED"), ("result", "EXECUTED")));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE DetectionId = @d", c => c.Parameters.AddWithValue("@d", row.DetectionId), Ct));   // ningún job nuevo
    }

    /// <summary>
    /// T41 · Tres autorizaciones LABEL_DAMAGED aprobadas sobre un job CONFIRMED con la etiqueta dañada. La revocada y la
    /// vencida no permiten intento. La tercera, sin el retiro de la etiqueta original registrado, la rechaza
    /// TR_PrintAttempt_Guard; con el retiro registrado, se ejecuta en la ManualPrinterId. El job sigue CONFIRMED (X02):
    /// la lectura VERIFIED anterior del mismo serial no confirma el reemplazo, solo una observación que referencia al
    /// intento. Además: la autorización de otro job la rechaza la FK compuesta, y dirigir una reimpresión a la impresora
    /// de línea, TR_ReprintAuthorization_Guard.
    /// </summary>
    [Fact]
    [Trait("Spec", "T41")]
    public async Task Revoked_and_expired_authorizations_do_not_print_and_a_damaged_label_is_replaced_only_after_its_removal()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        await using var rig = ManualRig.Create(s);
        var (job, original) = await s.TransmittedJobAsync(830, Ct);
        var serial = (await s.JobAsync(job, Ct)).LabelSerial;
        await s.EvidenceAsync(s.Verified(830, serial), Ct);
        var confirmed = await s.JobAsync(job, Ct);
        Assert.Equal(JobStatus.Confirmed, confirmed.Status);
        var originalObservation = (await s.AttemptsAsync(job, Ct)).Single(a => a.AttemptId == original).ConfirmingObservationId!.Value;

        // Las tres en sucesión: UX_ReprintAuthorization_OpenPerJob admite una sola APPROVED por job a la vez.
        var toRevoke = await AuthorizeAsync(ops, job, "LABEL_DAMAGED", "REPLACES_EXISTING_LABEL");
        using (var supervisor = ops.As(OperationsHost.Supervisor))
        {
            Assert.Equal(200, (await supervisor.CallAsync($"api/ops/reprints/{toRevoke}/revoke", null, Ct)).Status);
        }

        Assert.Equal(52001, (await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => s.Txx.ExecuteAsync(toRevoke, s.ManualPrinterId,
            s.ManualLease.OwnerInstanceId, s.ManualLease.FencingToken, Guid.NewGuid(), s.Clock.UtcNow, Ct))).Number);   // REVOKED: sin fila en @executed
        Assert.Null((await rig.Sender.ExecuteAsync(toRevoke, Ct)).Execution);

        var toExpire = await AuthorizeAsync(ops, job, "LABEL_DAMAGED", "REPLACES_EXISTING_LABEL");
        s.AdvanceMs(31 * 60_000);
        // El sender de la impresora manual renueva su lease (el reloj de prueba avanzó más que su duración).
        await s.Leases.AcquireAsync(s.ManualPrinterId, s.OwnerInstanceId, "host-test", 600_000, 2_000, s.Clock.UtcNow, Ct);
        await s.Txs.ExpireAuthorizationsAsync(StationScenario.SiteId, s.StationId, s.Clock.UtcNow, Ct);
        Assert.Equal(52001, (await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => s.Txx.ExecuteAsync(toExpire, s.ManualPrinterId,
            s.ManualLease.OwnerInstanceId, s.ManualLease.FencingToken, Guid.NewGuid(), s.Clock.UtcNow, Ct))).Number);   // EXPIRED: sin fila en @executed
        Assert.Null((await rig.Sender.ExecuteAsync(toExpire, Ct)).Execution);

        var toExecute = await AuthorizeAsync(ops, job, "LABEL_DAMAGED", "REPLACES_EXISTING_LABEL");
        var earlierRead = await s.ObserveAsync(s.Verified(830, serial) with { SourceReference = "SCN-ANTERIOR-830" }, Ct);   // antes del reemplazo
        Assert.Equal(51021, (await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => s.Txx.ExecuteAsync(toExecute, s.ManualPrinterId,
            s.ManualLease.OwnerInstanceId, s.ManualLease.FencingToken, Guid.NewGuid(), s.Clock.UtcNow, Ct))).Number);   // sin retiro: TR_PrintAttempt_Guard
        Assert.Null((await rig.Sender.ExecuteAsync(toExecute, Ct)).Execution);
        Assert.Equal(0L, rig.Printer.BytesReceived);
        using (var auditor = ops.As(OperationsHost.Auditor))
        {
            Assert.Equal(200, (await auditor.CallAsync($"api/ops/reprints/{toExecute}/original-label-removed", null, Ct)).Status);
        }

        var executed = await rig.Sender.ExecuteAsync(toExecute, Ct);
        Assert.True(executed.WriteInvoked);
        var reprint = (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint);
        Assert.Equal((s.ManualPrinterId, AttemptOutcome.Transmitted), (reprint.PrinterId, reprint.Outcome));
        Assert.Equal(1, rig.Printer.TransmissionsOf(serial));
        var reused = await s.FailsAsync("""
            INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken,
                Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt)
            SELECT NEWID(), pa.PrintJobId, 99, 'EXCEPTIONAL_REPRINT', pa.AuthorizationId, pa.PrinterId, N'x', 1, 'NOT_SENT', 0, pa.AttemptPayloadHash, SYSUTCDATETIME()
            FROM dbo.PrintAttempt AS pa WHERE pa.AttemptId = @a
            """, c => c.Parameters.AddWithValue("@a", reprint.AttemptId), "EXCEPTION", Ct);
        Assert.Equal(2601, reused.Number);   // UX_PrintAttempt_Authorization: ninguna autorización se usa dos veces
        Assert.Contains("UX_PrintAttempt_Authorization", reused.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { ("EXPIRED", toExpire), ("REVOKED", toRevoke), ("EXECUTED", toExecute) }.OrderBy(x => x.Item2),
            (await s.QueryAsync("SELECT Status, AuthorizationId FROM dbo.ReprintAuthorization WHERE PrintJobId = @id ORDER BY AuthorizationId",
                c => c.Parameters.AddWithValue("@id", job), r => (r.GetString(0), r.GetInt64(1)), Ct)));

        var stale = await s.FailsAsync("UPDATE dbo.PrintAttempt SET Outcome = 'CONFIRMED', ConfirmingObservationId = @o WHERE AttemptId = @a",
            c => { c.Parameters.AddWithValue("@o", originalObservation); c.Parameters.AddWithValue("@a", reprint.AttemptId); }, ct: Ct);
        Assert.Equal(2601, stale.Number);   // UX_PrintAttempt_ConfirmingObservation: esa lectura ya confirmó la etiqueta original
        var earlier = await s.FailsAsync("UPDATE dbo.PrintAttempt SET Outcome = 'CONFIRMED', ConfirmingObservationId = @o WHERE AttemptId = @a",
            c => { c.Parameters.AddWithValue("@o", earlierRead); c.Parameters.AddWithValue("@a", reprint.AttemptId); }, ct: Ct);
        Assert.Equal(51022, earlier.Number);   // TR_PrintAttempt_Guard: una lectura anterior al intento no lo confirma

        await rig.Evidence.SubmitAsync(s.Verified(830, serial, reprint.AttemptId) with { SourceReference = "SCN-REEMPLAZO-830" }, Ct);
        var after = await s.JobAsync(job, Ct);
        Assert.Equal(JobStatus.Confirmed, after.Status);
        Assert.True(after.ResultVersion > confirmed.ResultVersion);
        Assert.Equal(AttemptOutcome.Confirmed, (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint).Outcome);
        var result = LastResult(await s.ResultsAsync(job, Ct));
        Assert.Equal(("CONFIRMED", "REPLACES_EXISTING_LABEL"), (result.GetProperty("outcome").GetString(),
            result.GetProperty("exceptionalReprint").GetProperty("duplicateRisk").GetString()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ExceptionalReprints, ("risk", "REPLACES_EXISTING_LABEL"), ("result", "EXECUTED")));

        // La autorización de otro job y la impresora de línea como destino.
        var other = await s.ReadyJobAsync(831, Ct);
        var foreign = await s.FailsAsync("""
            INSERT INTO dbo.PrintAttempt (AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken, Outcome,
                MayHaveSent, AttemptPayloadHash, ClaimedAt)
            SELECT NEWID(), PrintJobId, 1, 'EXCEPTIONAL_REPRINT', @auth, @printer, @owner, @token, 'SENDING', 1, PayloadHash, SYSUTCDATETIME()
            FROM dbo.PrintJob WHERE PrintJobId = @id
            """, c =>
        {
            c.Parameters.AddWithValue("@id", other);
            c.Parameters.AddWithValue("@auth", toRevoke);   // sin intento propio: lo que decide es la FK compuesta
            c.Parameters.AddWithValue("@printer", s.ManualPrinterId);
            c.Parameters.AddWithValue("@owner", s.OwnerInstanceId);
            c.Parameters.AddWithValue("@token", s.ManualLease.FencingToken);
        }, ct: Ct);
        Assert.Equal(547, foreign.Number);   // FK compuesta (AuthorizationId, PrintJobId)
        var line = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => s.Txm.ApproveReprintAsync(new ReprintRequest(job,
            JobStatus.Confirmed, "LABEL_DAMAGED", "REPLACES_EXISTING_LABEL", "CJ-830", "Etiqueta rota", s.PrinterId, true, 5_000,
            OperationsHost.Operator, s.Clock.UtcNow, OperationsHost.Supervisor, s.Clock.UtcNow, s.Clock.UtcNow.AddMinutes(10)), Ct));
        Assert.Equal(51024, line.Number);   // TR_ReprintAuthorization_Guard: impresora no permitida
    }

    /// <summary>
    /// T43 · Reimpresión excepcional sin confirmar. Job EXPIRED con DEADLINE_PASSED, caja retenida, autorización
    /// EXPIRED_CARTON_HELD con NONE_PROVEN. El sender manual ejecuta TX-X y transmite; no llega evidencia en
    /// EvidenceWindowMs: el barrido aplica A06 y X03 en la misma transacción (intento UNKNOWN, job UNKNOWN con
    /// EXCEPTIONAL_REPRINT_UNRESOLVED, PrintJobResult UNKNOWN no final con exceptionalReprint). Otra autorización
    /// NONE_PROVEN la rechaza el CHECK (estado previo UNKNOWN exige ACCEPTED). La inspección tardía que lee el serial
    /// correcto y referencia al intento lo confirma (A08) y confirma el job (X01) con resultVersion mayor.
    /// </summary>
    [Fact]
    [Trait("Spec", "T43")]
    public async Task An_unconfirmed_exceptional_reprint_is_unknown_until_a_late_inspection_references_it()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        await using var rig = ManualRig.Create(s);
        var job = await s.ReadyJobAsync(840, Ct);
        s.AdvanceMs(5_000);
        var sweep = new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);
        await sweep.SweepOnceAsync(Ct);
        Assert.Equal((JobStatus.Expired, JobReason.DeadlinePassed), ((await s.JobAsync(job, Ct)).Status, (await s.JobAsync(job, Ct)).Reason));

        var authorization = await AuthorizeAsync(ops, job, "EXPIRED_CARTON_HELD", "NONE_PROVEN");
        var executed = await rig.Sender.ExecuteAsync(authorization, Ct);
        Assert.True(executed.WriteInvoked);
        var reprint = (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint);
        Assert.Equal(AttemptOutcome.Transmitted, reprint.Outcome);

        var window = await s.ScalarAsync<int>("SELECT EvidenceWindowMs FROM dbo.ReprintAuthorization WHERE AuthorizationId = @id",
            c => c.Parameters.AddWithValue("@id", authorization), Ct);
        s.AdvanceMs(window + 10);
        await sweep.SweepOnceAsync(Ct);
        var unknown = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.ExceptionalReprintUnresolved), (unknown.Status, unknown.Reason));
        Assert.Equal(AttemptOutcome.Unknown, (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint).Outcome);
        var unresolved = LastResult(await s.ResultsAsync(job, Ct));
        Assert.Equal(("UNKNOWN", false, authorization), (unresolved.GetProperty("outcome").GetString(), unresolved.GetProperty("final").GetBoolean(),
            unresolved.GetProperty("exceptionalReprint").GetProperty("authorizationId").GetInt64()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ExceptionalReprints, ("result", "UNRESOLVED")));

        var (status, body) = await ApproveAsync(ops, await RequestAsync(ops, job, "UNKNOWN_NOT_RESOLVED", "NONE_PROVEN"));
        Assert.Equal((409, 547), (status, body.GetProperty("sqlError").GetInt32()));   // CK_ReprintAuthorization_Risk

        var serial = unknown.LabelSerial;
        await rig.Evidence.SubmitAsync(s.Verified(840, serial, reprint.AttemptId, type: EvidenceType.ManualInspection,
            recordedBy: OperationsHost.Supervisor), Ct);
        var confirmed = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Confirmed, true), (confirmed.Status, confirmed.ConfirmedByExceptionalReprint));
        Assert.True(confirmed.ResultVersion > unknown.ResultVersion);
        Assert.Equal(AttemptOutcome.Confirmed, (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint).Outcome);
        Assert.Equal("CONFIRMED", LastResult(await s.ResultsAsync(job, Ct)).GetProperty("outcome").GetString());
        Assert.Equal(1, rig.Printer.TransmissionsOf(serial));   // una etiqueta de reimpresión, en la impresora manual
    }

    /// <summary>
    /// Revisión de la Fase 5, M3: una autorización que TX-X rechaza (LABEL_DAMAGED sin el retiro registrado) no bloquea a la
    /// aprobada después para otro job, que se ejecuta en la vuelta siguiente; la rechazada se vuelve a intentar pasada la
    /// espera y, ya con el retiro registrado, se ejecuta. El riesgo de duplicado sale de TX-X.
    /// </summary>
    [Fact]
    [Trait("Spec", "T41")]
    public async Task A_rejected_authorization_does_not_block_the_ones_approved_after_it()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        await using var rig = ManualRig.Create(s);
        var (damaged, _) = await s.TransmittedJobAsync(850, Ct);
        await s.EvidenceAsync(s.Verified(850, (await s.JobAsync(damaged, Ct)).LabelSerial), Ct);
        var blocked = await AuthorizeAsync(ops, damaged, "LABEL_DAMAGED", "REPLACES_EXISTING_LABEL");
        var (uncertain, _) = await s.UnknownJobAsync(851, Ct);
        var next = await AuthorizeAsync(ops, uncertain, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");

        var first = (await rig.Sender.ExecuteNextAsync(Ct))!;
        Assert.Equal((blocked, (ReprintExecution?)null), (first.AuthorizationId, first.Execution));
        var second = (await rig.Sender.ExecuteNextAsync(Ct))!;
        Assert.Equal((next, true, "ACCEPTED"), (second.AuthorizationId, second.WriteInvoked, second.Execution!.DuplicateRisk));
        Assert.Equal(1, rig.Printer.TransmissionsOf((await s.JobAsync(uncertain, Ct)).LabelSerial));
        Assert.Null(await rig.Sender.ExecuteNextAsync(Ct));   // la rechazada espera

        using (var auditor = ops.As(OperationsHost.Auditor))
        {
            Assert.Equal(200, (await auditor.CallAsync($"api/ops/reprints/{blocked}/original-label-removed", null, Ct)).Status);
        }

        s.AdvanceMs(ManualReprintSender.RejectedRetryDelay.TotalMilliseconds + 1);
        var retried = (await rig.Sender.ExecuteNextAsync(Ct))!;
        Assert.Equal((blocked, true), (retried.AuthorizationId, retried.WriteInvoked));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ExceptionalReprints, ("risk", "REPLACES_EXISTING_LABEL"), ("result", "EXECUTED")));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ExceptionalReprints, ("risk", "ACCEPTED"), ("result", "EXECUTED")));
    }

    /// <summary>
    /// Revisión de la Fase 5, M4 (regla 12, I02, en el sender manual): la impresora manual corta el canal a mitad de la
    /// reimpresión. La escritura se invocó: intento UNKNOWN y, en la misma TX-C, X03 (job UNKNOWN con
    /// EXCEPTIONAL_REPRINT_UNRESOLVED), nunca NOT_SENT; ningún reintento.
    /// </summary>
    [Fact]
    [Trait("Spec", "T43")]
    public async Task A_reprint_cut_mid_write_is_unknown_and_unresolved_never_not_sent()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        await using var rig = ManualRig.Create(s);
        var (job, _) = await s.UnknownJobAsync(870, Ct);
        var authorization = await AuthorizeAsync(ops, job, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");
        rig.Printer.Mode = PrinterMode.CutMidWrite;

        var executed = await rig.Sender.ExecuteAsync(authorization, Ct);
        Assert.True(executed.WriteInvoked);
        Assert.Equal(AttemptOutcome.Unknown, executed.Write!.AttemptOutcome);
        Assert.Equal(AttemptOutcome.Unknown, (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint).Outcome);
        var row = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.ExceptionalReprintUnresolved), (row.Status, row.Reason));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ExceptionalReprints, ("result", "UNRESOLVED")));
        Assert.Empty(rig.Printer.Received);
        Assert.Null(await rig.Sender.ExecuteNextAsync(Ct));   // EXECUTED: no se reintenta
    }

    /// <summary>
    /// Segunda revisión de la Fase 5, MEDIA 3: la impresora manual no acepta la conexión después de TX-X. La escritura nunca se
    /// invocó: el intento queda NOT_SENT (no en SENDING, que ocuparía la impresora manual hasta reiniciar), la autorización
    /// queda EXECUTED y el job sigue en su estado previo; la impresora manual queda libre para la reimpresión siguiente.
    /// </summary>
    [Fact]
    [Trait("Spec", "T34")]
    public async Task A_reprint_whose_connection_fails_after_tx_x_is_not_sent()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        await using var rig = ManualRig.Create(s);
        var (job, _) = await s.UnknownJobAsync(880, Ct);
        var authorization = await AuthorizeAsync(ops, job, "UNKNOWN_NOT_RESOLVED", "ACCEPTED");
        rig.Printer.Mode = PrinterMode.RejectConnections;

        var executed = await rig.Sender.ExecuteAsync(authorization, Ct);
        Assert.False(executed.WriteInvoked);
        Assert.Equal(AttemptOutcome.NotSent, executed.Write!.AttemptOutcome);
        Assert.Equal(AttemptOutcome.NotSent, (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint).Outcome);
        Assert.Equal("EXECUTED", await s.ScalarAsync<string>("SELECT Status FROM dbo.ReprintAuthorization WHERE AuthorizationId = @id",
            c => c.Parameters.AddWithValue("@id", authorization), Ct));
        Assert.Equal(JobStatus.Unknown, (await s.JobAsync(job, Ct)).Status);
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintAttempt WHERE PrinterId = @p AND Outcome = 'SENDING'",
            c => c.Parameters.AddWithValue("@p", s.ManualPrinterId), Ct));
    }

    /// <summary>Sender de la impresora manual con su lease, su impresora simulada y la evidencia.</summary>
    private sealed class ManualRig : IAsyncDisposable
    {
        private ManualRig(PrinterSimulator printer, TcpPrinterTransport transport, ManualReprintSender sender, EvidenceService evidence)
        {
            Printer = printer;
            Transport = transport;
            Sender = sender;
            Evidence = evidence;
        }

        public PrinterSimulator Printer { get; }
        public TcpPrinterTransport Transport { get; }
        public ManualReprintSender Sender { get; }
        public EvidenceService Evidence { get; }

        public static ManualRig Create(StationScenario s)
        {
            var printer = PrinterSimulator.Start();
            var transport = new TcpPrinterTransport(new PrinterTransportOptions
            {
                Port = printer.Port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = 1_000,
            }, s.Provider);
            var evidence = new EvidenceService(s.Txd, s.Sql, s.Clock, s.Metrics);
            return new ManualRig(printer, transport, new ManualReprintSender(s.ManualPrinterId, s.Sql, s.Txx, s.Txc, s.ManualLease, transport, s.Clock,
                s.Metrics, evidence: evidence), evidence);
        }

        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync();
            await Printer.DisposeAsync();
        }
    }
}
