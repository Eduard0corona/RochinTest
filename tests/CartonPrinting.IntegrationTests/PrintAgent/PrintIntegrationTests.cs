using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Testing;

namespace CartonPrinting.IntegrationTests.PrintAgent;

/// <summary>Sender real, impresora simulada por TCP y evidencia simulada contra SQL Server real (Fase 4).</summary>
public sealed class PrintIntegrationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f4int", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StationSweepService Sweep(StationScenario s) => new(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);

    private static JsonElement LastResult(IReadOnlyList<JsonElement> results) => results.MaxBy(r => r.GetProperty("resultVersion").GetInt32());

    /// <summary>
    /// T15 · Confirmación física por escáner (VERIFIED, correlación por el tracking del PLC): intento
    /// SENDING → TRANSMITTED → CONFIRMED; job TRANSMITTED → CONFIRMED; resultado con evidence.level VERIFIED y
    /// correlatedTo CYCLE; readSerial = labelSerial; send_to_verified_ms. TR_PrintAttempt_Guard no acepta
    /// confirmar con una observación de otro ciclo.
    /// </summary>
    [Fact]
    [Trait("Spec", "T15")]
    public async Task Scanner_reading_the_right_serial_confirms_the_job()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        await using var rig = PrintRig.Create(s, sources: new EvidenceSources(PrinterCompletion: true, Scanner: true));
        var job = await s.ReadyJobAsync(10, Ct);

        var sent = await rig.SendAsync(job, Ct);
        Assert.Equal(AttemptOutcome.Transmitted, sent.Write!.AttemptOutcome);
        var row = await s.JobAsync(job, Ct);
        Assert.Equal(JobStatus.Confirmed, row.Status);
        var attempt = Assert.Single(await s.AttemptsAsync(job, Ct));
        Assert.Equal(AttemptOutcome.Confirmed, attempt.Outcome);

        var result = LastResult(await s.ResultsAsync(job, Ct));
        var evidence = result.GetProperty("evidence");
        Assert.Equal(("CONFIRMED", "VERIFIED", "CYCLE", row.LabelSerial, true),
            (result.GetProperty("outcome").GetString(), evidence.GetProperty("level").GetString(), evidence.GetProperty("correlatedTo").GetString(),
             evidence.GetProperty("readSerial").GetString(), evidence.GetProperty("verified").GetBoolean()));
        Assert.Single(s.Metrics.Samples(MetricNames.SendToVerifiedMs));
        Assert.Equal(1, rig.Printer.TransmissionsOf(row.LabelSerial));

        // Otro job transmitido: confirmarlo a mano con la observación del ciclo 10 lo rechaza el trigger.
        var other = await s.ReadyJobAsync(11, Ct);
        rig.EvidenceSim.Sources = new EvidenceSources(PrinterCompletion: false);
        await rig.SendAsync(other, Ct);
        var otherAttempt = Assert.Single(await s.AttemptsAsync(other, Ct));
        var ex = await s.FailsAsync("UPDATE dbo.PrintAttempt SET Outcome = 'CONFIRMED', ConfirmingObservationId = @o WHERE AttemptId = @a",
            c => { c.Parameters.AddWithValue("@o", attempt.ConfirmingObservationId!.Value); c.Parameters.AddWithValue("@a", otherAttempt.AttemptId); }, ct: Ct);
        Assert.InRange(ex.Number, 2601, 51032);
    }

    /// <summary>
    /// T22 · El escáner lee el serial de otro job en la caja de A: observación registrada; intento UNKNOWN;
    /// job A UNKNOWN con SERIAL_MISMATCH, nunca CONFIRMED; resultado con evidence.verified = false;
    /// serial_mismatch_total. El trigger rechaza confirmar con esa observación aunque el código lo intente.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task Scanner_reading_another_jobs_serial_is_a_serial_mismatch_never_a_confirmation()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified }, Ct);
        await using var rig = PrintRig.Create(s, sources: new EvidenceSources(PrinterCompletion: false));
        var a = await s.ReadyJobAsync(20, Ct);
        var b = await s.ReadyJobAsync(21, Ct);
        await rig.SendAsync(a, Ct);
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(a, Ct)).Status);
        var bSerial = (await s.JobAsync(b, Ct)).LabelSerial;
        var aKey = new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, 20).IdempotencyKey;

        await rig.EvidenceSim.ScanAsync(aKey, bSerial, Ct);
        var row = await s.JobAsync(a, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), (row.Status, row.Reason));
        var attempt = Assert.Single(await s.AttemptsAsync(a, Ct));
        Assert.Equal(AttemptOutcome.Unknown, attempt.Outcome);
        var result = LastResult(await s.ResultsAsync(a, Ct));
        Assert.Equal(("UNKNOWN", false, bSerial), (result.GetProperty("outcome").GetString(),
            result.GetProperty("evidence").GetProperty("verified").GetBoolean(), result.GetProperty("evidence").GetProperty("readSerial").GetString()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.SerialMismatch));

        var observation = await s.ScalarAsync<long>("SELECT MAX(ObservationId) FROM dbo.EvidenceObservation WHERE StationId = @st AND ReadSerial = @serial",
            c => { c.Parameters.AddWithValue("@st", s.StationId); c.Parameters.AddWithValue("@serial", bSerial); }, Ct);
        var ex = await s.FailsAsync("UPDATE dbo.PrintAttempt SET Outcome = 'CONFIRMED', ConfirmingObservationId = @o WHERE AttemptId = @a",
            c => { c.Parameters.AddWithValue("@o", observation); c.Parameters.AddWithValue("@a", attempt.AttemptId); }, ct: Ct);
        Assert.InRange(ex.Number, 51001, 51032);
        Assert.NotEqual(JobStatus.Confirmed, (await s.JobAsync(a, Ct)).Status);
    }

    /// <summary>
    /// T38 · Ventana de evidencia vencida y estación sin evidencia. Con PRINTED y sin evidencia:
    /// TRANSMITTED → UNKNOWN con EVIDENCE_WINDOW_ELAPSED. Con NONE: TRANSMITTED terminal, publicado con final
    /// true y sin evidence. TRANSMITTED nunca se publica como CONFIRMED.
    /// </summary>
    [Fact]
    [Trait("Spec", "T38")]
    public async Task Without_evidence_the_window_elapses_to_unknown_and_with_none_transmitted_is_terminal()
    {
        var printed = await StationScenario.CreateAsync(_db, ct: Ct);
        await using (var rig = PrintRig.Create(printed, sources: new EvidenceSources(PrinterCompletion: false)))
        {
            var job = await printed.ReadyJobAsync(30, Ct);
            await rig.SendAsync(job, Ct);
            Assert.Equal(JobStatus.Transmitted, (await printed.JobAsync(job, Ct)).Status);
            printed.AdvanceMs(printed.Revision.EvidenceWindowMs!.Value + 10);
            var report = await Sweep(printed).SweepOnceAsync(Ct);
            Assert.Single(report.EvidenceElapsed);
            var row = await printed.JobAsync(job, Ct);
            Assert.Equal((JobStatus.Unknown, JobReason.EvidenceWindowElapsed), (row.Status, row.Reason));
            Assert.Equal("UNKNOWN", LastResult(await printed.ResultsAsync(job, Ct)).GetProperty("outcome").GetString());
            Assert.Equal(1, printed.Metrics.Total(MetricNames.EvidenceWindowElapsed));
        }

        var none = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.None, EvidenceWindowMs = null }, Ct);
        await using (var rig = PrintRig.Create(none, sources: new EvidenceSources(PrinterCompletion: false)))
        {
            var job = await none.ReadyJobAsync(31, Ct);
            await rig.SendAsync(job, Ct);
            none.AdvanceMs(60_000);
            await Sweep(none).SweepOnceAsync(Ct);
            Assert.Equal(JobStatus.Transmitted, (await none.JobAsync(job, Ct)).Status);
            var result = Assert.Single(await none.ResultsAsync(job, Ct));
            Assert.Equal(("TRANSMITTED", true, false), (result.GetProperty("outcome").GetString(), result.GetProperty("final").GetBoolean(),
                result.TryGetProperty("evidence", out var e) && e.ValueKind != JsonValueKind.Null));
        }
    }

    /// <summary>
    /// T39 · Cancelación autorizada: el READY pasa a CANCELLED con la OperatorAction CANCEL_JOB en
    /// LastOperatorActionId y el resultado la lleva; el job con un intento TRANSMITTED (MayHaveSent = 1) no se
    /// cancela; un UPDATE a CANCELLED sin OperatorAction lo rechaza el trigger.
    /// </summary>
    [Fact]
    [Trait("Spec", "T39")]
    public async Task Only_a_ready_job_is_cancelled_and_always_with_its_operator_action()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var rig = PrintRig.Create(s, sources: new EvidenceSources(PrinterCompletion: false));
        var ready = await s.ReadyJobAsync(40, Ct);
        var sent = await s.ReadyJobAsync(41, Ct);
        await rig.SendAsync(sent, Ct);

        var cancel = await s.Txm.CancelJobAsync(ready, StationScenario.SiteId, s.StationId, "supervisor.ramos", "Caja retirada de la línea", s.Clock.UtcNow, Ct);
        var row = await s.JobAsync(ready, Ct);
        Assert.Equal((JobStatus.Cancelled, (long?)cancel.OperatorActionId), (row.Status, row.LastOperatorActionId));
        Assert.Equal(("CANCEL_JOB", "supervisor.ramos", "Caja retirada de la línea"), await ActionAsync(s, cancel.OperatorActionId));
        var result = LastResult(await s.ResultsAsync(ready, Ct));
        Assert.Equal(("CANCELLED", cancel.OperatorActionId), (result.GetProperty("outcome").GetString(),
            result.GetProperty("operatorAction").GetProperty("operatorActionId").GetInt64()));

        await Assert.ThrowsAsync<ManualActionRejectedException>(() =>
            s.Txm.CancelJobAsync(sent, StationScenario.SiteId, s.StationId, "supervisor.ramos", "Intento", s.Clock.UtcNow, Ct));
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(sent, Ct)).Status);
        var backed = await s.FailsAsync("""
            DECLARE @oa TABLE (Id BIGINT);
            INSERT INTO dbo.OperatorAction (ActionType, SiteId, StationId, PrintJobId, TargetStatus, PerformedBy, ReasonText, PerformedAt)
            OUTPUT inserted.OperatorActionId INTO @oa (Id)
            VALUES ('CANCEL_JOB', @site, @st, @id, 'CANCELLED', N'supervisor.ramos', N'Intento directo', SYSUTCDATETIME());
            UPDATE dbo.PrintJob SET Status = 'CANCELLED', Reason = 'CANCELLED_BY_OPERATOR', LastOperatorActionId = (SELECT Id FROM @oa),
                ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id;
            """, c =>
        {
            c.Parameters.AddWithValue("@site", StationScenario.SiteId);
            c.Parameters.AddWithValue("@st", s.StationId);
            c.Parameters.AddWithValue("@id", sent);
        }, "MANUAL", Ct);
        Assert.InRange(backed.Number, 51001, 51032);

        var other = await s.ReadyJobAsync(42, Ct);
        var unbacked = await s.FailsAsync("UPDATE dbo.PrintJob SET Status = 'CANCELLED', Reason = 'CANCELLED_BY_OPERATOR', ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id",
            c => c.Parameters.AddWithValue("@id", other), "MANUAL", Ct);
        Assert.Equal(51012, unbacked.Number);
        Assert.Equal(0, rig.Printer.TransmissionsOf((await s.JobAsync(ready, Ct)).LabelSerial));
    }

    /// <summary>
    /// T44 · Evidencia contradictoria después de CONFIRMED (APPLIED): el escáner aguas abajo lee en la caja de A
    /// el serial de B: J18, A UNKNOWN con SERIAL_MISMATCH y una versión mayor no final con la lectura. Después
    /// el trigger rechaza volver a CONFIRMED, cambiar solo Reason a WRITE_INTERRUPTED para reintentar J15 y
    /// fijar EXCEPTIONAL_REPRINT_UNRESOLVED para habilitar X01.
    /// </summary>
    [Fact]
    [Trait("Spec", "T44")]
    public async Task Contradicting_read_after_confirmed_publishes_a_higher_version_and_cannot_be_undone()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Applied }, Ct);
        await using var rig = PrintRig.Create(s, sources: new EvidenceSources(PrinterCompletion: true, Applicator: true));
        var a = await s.ReadyJobAsync(50, Ct);
        var b = await s.ReadyJobAsync(51, Ct);
        await rig.SendAsync(a, Ct);
        var confirmed = await s.JobAsync(a, Ct);
        Assert.Equal(JobStatus.Confirmed, confirmed.Status);
        Assert.Equal("APPLIED", LastResult(await s.ResultsAsync(a, Ct)).GetProperty("evidence").GetProperty("level").GetString());

        var bSerial = (await s.JobAsync(b, Ct)).LabelSerial;
        await rig.EvidenceSim.ScanAsync(new CycleIdentity(StationScenario.SiteId, s.StationId, s.Epoch, 50).IdempotencyKey, bSerial, Ct);
        var corrected = await s.JobAsync(a, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), (corrected.Status, corrected.Reason));
        Assert.True(corrected.ResultVersion > confirmed.ResultVersion);
        var result = LastResult(await s.ResultsAsync(a, Ct));
        Assert.Equal(("UNKNOWN", false, bSerial, corrected.ResultVersion), (result.GetProperty("outcome").GetString(), result.GetProperty("final").GetBoolean(),
            result.GetProperty("evidence").GetProperty("readSerial").GetString(), result.GetProperty("resultVersion").GetInt32()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ConfirmedCorrected));

        foreach (var (sql, kind) in new[]
        {
            ("UPDATE dbo.PrintJob SET Status = 'CONFIRMED', Reason = NULL, ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id", (string?)null),
            ("UPDATE dbo.PrintJob SET Reason = 'WRITE_INTERRUPTED', ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id", null),
            ("UPDATE dbo.PrintJob SET Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED', ResultVersion = ResultVersion + 1 WHERE PrintJobId = @id", "EXCEPTION"),
        })
        {
            var ex = await s.FailsAsync(sql, c => c.Parameters.AddWithValue("@id", a), kind, Ct);
            Assert.Equal(51012, ex.Number);
        }

        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), ((await s.JobAsync(a, Ct)).Status, (await s.JobAsync(a, Ct)).Reason));
    }

    private static async Task<(string Type, string By, string Reason)> ActionAsync(StationScenario s, long id)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT ActionType, PerformedBy, ReasonText FROM dbo.OperatorAction WHERE OperatorActionId = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await r.ReadAsync(Ct));
        return (r.GetString(0), r.GetString(1), r.GetString(2));
    }
}
