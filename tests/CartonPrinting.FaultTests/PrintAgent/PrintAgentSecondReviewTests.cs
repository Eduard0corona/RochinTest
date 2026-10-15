using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.PrintAgent;

/// <summary>
/// Regresiones de la segunda revisión independiente de la Fase 4, contra SQL Server real y la impresora simulada: una lectura
/// contradictoria anterior nunca queda detrás de una confirmación, tampoco bajo concurrencia (A-1, B-8); un error o un apagado
/// entre el claim y la escritura termina en NOT_SENT (M-1, B-1); el arranque reaplica las correcciones pendientes (M-3); y la
/// reaplicación del sender después de TX-C confirma y cuenta las alertas (M-4, B-6).
/// </summary>
public sealed class PrintAgentSecondReviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string WrongSerial = "ZZ-AJENO-0001";
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f4rev2", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TcpPrinterTransport Transport(PrinterSimulator printer, StationScenario s) => new(new PrinterTransportOptions
    {
        Port = printer.Port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = 1_000,
    }, s.Provider);

    /// <summary>
    /// A-1, determinista: la lectura contradictoria quedó registrada sin aplicar durante SENDING; al aplicar después la evidencia
    /// PRINTED, que bastaría para confirmar, se aplica primero la contradicción (J11) y el job nunca queda CONFIRMED.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task Confirming_evidence_applies_an_earlier_unapplied_contradiction_first()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, attempt) = await s.SendingJobAsync(1000, Ct);
        var contradiction = await s.ObserveAsync(s.Verified(1000, WrongSerial), Ct);
        await s.TransmitAsync(attempt, Ct);
        var printed = await s.ObserveAsync(s.Printed(job), Ct);

        var outcome = await s.Txd.ApplyAsync(printed, s.Clock.UtcNow, Ct);
        Assert.Equal(EvidenceAction.SerialMismatch, outcome.Action);
        var row = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), (row.Status, row.Reason));
        Assert.Null(Assert.Single(await s.AttemptsAsync(job, Ct)).ConfirmingObservationId);
        Assert.Equal(EvidenceAction.Recorded, (await s.Txd.ApplyAsync(contradiction, s.Clock.UtcNow, Ct)).Action);
    }

    /// <summary>
    /// A-1 bajo concurrencia (la sonda Q2 de la revisión): la lectura contradictoria llega durante SENDING y la evidencia
    /// PRINTED compite con TX-C y con la reaplicación del sender. En ninguna de las 100 vueltas el job termina CONFIRMED, y
    /// la alerta serial_mismatch_total se cuenta una vez por job.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task A_contradiction_during_sending_never_ends_confirmed_under_concurrency()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var evidence = new EvidenceService(s.Txd, s.Sql, s.Clock, s.Metrics);
        var random = new Random(7);
        var outcomes = new List<string>();
        for (ulong cycle = 1100; cycle < 1200; cycle++)
        {
            var job = await s.ReadyJobAsync(cycle, Ct);
            var claim = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
            Assert.True(claim.Claimed, claim.Rejection.ToString());
            Assert.Equal(EvidenceAction.Recorded, (await evidence.SubmitAsync(s.Verified(cycle, WrongSerial), Ct)).Action);
            var record = await s.JobAsync(job, Ct);
            var delay = random.Next(0, 12);
            var printed = Task.Run(() => evidence.SubmitAsync(s.Printed(job), Ct), Ct);
            var sender = Task.Run(async () =>
            {
                await Task.Delay(delay, Ct);
                await s.TransmitAsync(claim.AttemptId, Ct);
                await evidence.ReapplyForJobAsync(record, claim.ClaimedAtUtc, Ct);
            }, Ct);
            await Task.WhenAll(printed, sender);
            var row = await s.JobAsync(job, Ct);
            outcomes.Add($"{row.Status}/{row.Reason}");
        }

        Assert.All(outcomes, o => Assert.Equal($"{JobStatus.Unknown}/{JobReason.SerialMismatch}", o));
        Assert.Equal(100, s.Metrics.Total(MetricNames.SerialMismatch));
    }

    /// <summary>
    /// B-8: la lectura contradictoria todavía se está registrando (su transacción, con el ObservationId menor, no confirmó)
    /// cuando la evidencia PRINTED se aplica. La aplicación espera a que el registro termine y aplica la contradicción: el job
    /// no queda CONFIRMED con la lectura detrás.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task A_contradiction_still_being_recorded_is_not_left_behind_a_confirmation()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var evidence = new EvidenceService(s.Txd, s.Sql, s.Clock, s.Metrics);
        var (job, attempt) = await s.TransmittedJobAsync(1300, Ct);

        await using var conn = new SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(Ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(Ct);
        await using (var lockCmd = new SqlCommand(
            "EXEC sys.sp_getapplock @Resource = @r, @LockMode = 'Exclusive', @LockOwner = 'Transaction'", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("@r", TxdEvidence.CycleLockResource(StationScenario.SiteId, s.StationId, s.Epoch, 1300));
            await lockCmd.ExecuteNonQueryAsync(Ct);
        }

        await using (var insert = new SqlCommand("""
            INSERT INTO dbo.EvidenceObservation (SiteId, StationId, PlcEpoch, CycleId, EvidenceType, EvidenceLevel, CorrelatedTo, ReadSerial,
                SourceReference, ObservedAt, ReceivedAt, RecordedBy)
            VALUES (@site, @station, @epoch, 1300, 'BARCODE_VERIFICATION', 'VERIFIED', 'CYCLE', @serial, N'SCN-1300-ENCURSO', @now, @now, N'SYSTEM');
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("@site", StationScenario.SiteId);
            insert.Parameters.AddWithValue("@station", s.StationId);
            insert.Parameters.AddWithValue("@epoch", s.Epoch);
            insert.Parameters.AddWithValue("@serial", WrongSerial);
            insert.Parameters.Add("@now", System.Data.SqlDbType.DateTime2).Value = s.Clock.UtcNow;
            await insert.ExecuteNonQueryAsync(Ct);
        }

        var printed = evidence.SubmitAsync(s.Printed(job, attempt), Ct);
        await Task.Delay(500, Ct);
        Assert.False(printed.IsCompleted, "La evidencia PRINTED se aplicó sin esperar el registro en curso de la lectura del ciclo.");
        await tx.CommitAsync(Ct);

        await printed;
        var row = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), (row.Status, row.Reason));
    }

    /// <summary>M-1: SQL no responde al releer la estación después del claim. NOT_SENT (J06, sin consumir intento), nunca SENDING; la impresora sigue libre.</summary>
    [Fact]
    [Trait("Spec", "T05")]
    public async Task A_sql_error_between_the_claim_and_the_write_is_not_sent_and_frees_the_printer()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var printer = PrinterSimulator.Start();
        await using var transport = Transport(printer, s);
        var unreachable = new StationConfigStore(new SqlDb(new DatabaseOptions { ConnectionString = "Server=127.0.0.1,1;Connect Timeout=1;Encrypt=false" }));
        var failing = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, unreachable, s.Lease, transport, s.Clock, s.Metrics);
        var job = await s.ReadyJobAsync(1400, Ct);

        var result = await failing.SendAsync(job, Ct);
        Assert.False(result.WriteInvoked);
        Assert.Equal((AttemptOutcome.NotSent, JobStatus.RetryWait), (result.Write!.AttemptOutcome, result.Write.JobStatus));
        Assert.Equal(0, (await s.JobAsync(job, Ct)).AutoAttemptCount);
        Assert.Equal(0L, printer.BytesReceived);

        // Sin un intento en SENDING, la impresora no queda ocupada: el job siguiente se transmite.
        var sender = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, s.Lease, transport, s.Clock, s.Metrics);
        var next = await s.ReadyJobAsync(1401, Ct);
        Assert.Equal(AttemptOutcome.Transmitted, (await sender.SendAsync(next, Ct)).Write!.AttemptOutcome);
    }

    /// <summary>M-1: el apagado cancela el token justo después del claim. El intento termina NOT_SENT; el apagado no deja un SENDING.</summary>
    [Fact]
    [Trait("Spec", "T05")]
    public async Task A_shutdown_right_after_the_claim_does_not_leave_sending()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var printer = PrinterSimulator.Start();
        await using var transport = Transport(printer, s);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sender = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, s.Lease, transport, s.Clock, s.Metrics,
            new AtPoint(SenderPoint.AfterClaimCommit, ct =>
            {
                shutdown.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }));
        var job = await s.ReadyJobAsync(1500, Ct);

        var result = await sender.SendAsync(job, shutdown.Token);
        Assert.False(result.WriteInvoked);
        Assert.Equal((AttemptOutcome.NotSent, JobStatus.RetryWait), (result.Write!.AttemptOutcome, result.Write.JobStatus));
        Assert.Equal(AttemptOutcome.NotSent, Assert.Single(await s.AttemptsAsync(job, Ct)).Outcome);
        Assert.Equal(0, (await s.JobAsync(job, Ct)).AutoAttemptCount);   // no consume intento
        Assert.Equal(0L, printer.BytesReceived);
    }

    /// <summary>B-1: la estación se bloquea después del claim, cuando el plazo ya venció: J08 (EXPIRED, DEADLINE_PASSED), no J06.</summary>
    [Fact]
    [Trait("Spec", "T29")]
    public async Task A_station_blocked_after_the_deadline_expires_the_job()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var printer = PrinterSimulator.Start();
        await using var transport = Transport(printer, s);
        var job = await s.ReadyJobAsync(1600, Ct);
        var deadline = (await s.JobAsync(job, Ct)).DeadlineAt;
        var sender = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, s.Lease, transport, s.Clock, s.Metrics,
            new AtPoint(SenderPoint.AfterClaimCommit, async ct =>
            {
                s.AdvanceMs((deadline - s.Clock.UtcNow).TotalMilliseconds + 1_000);
                await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.IdentityCollision, "SYSTEM", null, s.Clock.UtcNow, ct);
            }));

        var result = await sender.SendAsync(job, Ct);
        Assert.Equal((AttemptOutcome.NotSent, JobStatus.Expired, JobReason.DeadlinePassed),
            (result.Write!.AttemptOutcome, result.Write.JobStatus, result.Write.JobReason));
        Assert.Equal(0L, printer.BytesReceived);
    }

    /// <summary>
    /// M-3 (T44, nivel APPLIED): el job está CONFIRMED y una lectura contradictoria posterior quedó registrada sin aplicar (el proceso cayó entre las
    /// dos transacciones de TX-D). La reaplicación del arranque la aplica: J18, UNKNOWN con SERIAL_MISMATCH y la alerta.
    /// </summary>
    [Fact]
    [Trait("Spec", "T44")]
    public async Task Startup_reapplies_a_recorded_correction_of_a_confirmed_job()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Applied }, Ct);
        var (job, _) = await s.TransmittedJobAsync(1700, Ct);
        await s.EvidenceAsync(s.Applied(1700), Ct);
        Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(job, Ct)).Status);
        await s.ObserveAsync(s.Verified(1700, WrongSerial), Ct);

        Assert.Equal(1, await new EvidenceService(s.Txd, s.Sql, s.Clock, s.Metrics).ReapplyOpenAsync(StationScenario.SiteId, s.StationId, Ct));
        var row = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), (row.Status, row.Reason));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ConfirmedCorrected));
        Assert.Equal(1, s.Metrics.Total(MetricNames.SerialMismatch));
    }

    /// <summary>
    /// M-4 y B-6: la evidencia llega mientras el intento sigue en SENDING (después de escribir, antes de TX-C) y queda registrada
    /// sin efecto. La reaplicación del sender después de TX-C la aplica: la PRINTED confirma y la contradicción cuenta su alerta.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task The_sender_applies_evidence_that_arrived_before_its_result()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var printer = PrinterSimulator.Start();
        await using var transport = Transport(printer, s);
        var evidence = new EvidenceService(s.Txd, s.Sql, s.Clock, s.Metrics);
        var submitted = new List<EvidenceAction>();
        Func<CancellationToken, Task>? onResult = null;
        var sender = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, s.Lease, transport, s.Clock, s.Metrics,
            new AtPoint(SenderPoint.AfterWriteBeforeResult, ct => onResult!(ct)), evidence: evidence);

        var confirmed = await s.ReadyJobAsync(1800, Ct);
        onResult = async ct => submitted.Add((await evidence.SubmitAsync(s.Printed(confirmed), ct)).Action);
        Assert.Equal(AttemptOutcome.Transmitted, (await sender.SendAsync(confirmed, Ct)).Write!.AttemptOutcome);
        Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(confirmed, Ct)).Status);

        var contradicted = await s.ReadyJobAsync(1801, Ct);
        onResult = async ct => submitted.Add((await evidence.SubmitAsync(s.Verified(1801, WrongSerial), ct)).Action);
        await sender.SendAsync(contradicted, Ct);
        var row = await s.JobAsync(contradicted, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), (row.Status, row.Reason));
        Assert.Equal([EvidenceAction.Recorded, EvidenceAction.Recorded], submitted);
        Assert.Equal(1, s.Metrics.Total(MetricNames.SerialMismatch));
    }

    /// <summary>
    /// M-2: con 100 000 observaciones de otra estación recibidas hace dos días (fuera del horizonte del arranque), las consultas
    /// de evidencia de la ruta crítica (el pase en orden del job y las correcciones pendientes del arranque) leen solo las filas
    /// recientes. Plan real con SET STATISTICS XML.
    /// </summary>
    [Fact]
    public async Task Evidence_queries_read_only_recent_rows()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var conn = new SqlConnection(s.Db.AdminConnectionString);
        await conn.OpenAsync(Ct);
        await using (var history = conn.Command(null, """
            INSERT INTO dbo.EvidenceObservation (SiteId, StationId, PlcEpoch, CycleId, EvidenceType, EvidenceLevel, CorrelatedTo,
                SourceReference, ObservedAt, ReceivedAt, RecordedBy)
            SELECT TOP (100000) @site, N'HISTORIA', 1, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'PLC_APPLICATOR_CONFIRMATION', 'APPLIED', 'CYCLE',
                N'APL-HISTORIA', DATEADD(DAY, -2, @now), DATEADD(DAY, -2, @now), N'SYSTEM'
            FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b;
            """, timeoutSeconds: 300))
        {
            history.NVarChar("@site", StationScenario.SiteId, 64).DateTime2("@now", s.Clock.UtcNow);
            await history.ExecuteNonQueryAsync(Ct);
        }

        var (job, _) = await s.TransmittedJobAsync(1900, Ct);
        await s.EvidenceAsync(s.Printed(job), Ct);
        await s.ObserveAsync(s.Verified(1900, WrongSerial), Ct);
        var record = await s.JobAsync(job, Ct);

        await using (var on = conn.Command(null, "SET STATISTICS XML ON;"))
        {
            await on.ExecuteNonQueryAsync(Ct);
        }

        await using var pass = conn.Command(null, EvidenceQueries.ForJobSince);
        pass.DateTime2("@since", Assert.Single(await s.AttemptsAsync(job, Ct)).ClaimedAt).Guid("@job", job).NVarChar("@site", record.SiteId, 64)
            .NVarChar("@station", record.StationId, 64).BigInt("@epoch", record.PlcEpoch).Cycle("@cycle", record.CycleId);
        var (passIds, passRead) = await RunWithPlanAsync(pass);
        Assert.Equal(2, passIds.Count);
        Assert.InRange(passRead, 1, 1_000);

        await using var startup = conn.Command(null, EvidenceQueries.StartupReapply);
        startup.DateTime2("@since", s.Clock.UtcNow - EvidenceService.StartupHorizon).NVarChar("@site", StationScenario.SiteId, 64)
            .NVarChar("@station", s.StationId, 64);
        var (startupIds, startupRead) = await RunWithPlanAsync(startup);
        Assert.Single(startupIds);
        Assert.InRange(startupRead, 1, 1_000);
    }

    /// <summary>Ejecuta la consulta y devuelve los ObservationId y la suma de ActualRowsRead de todos sus planes.</summary>
    private static async Task<(List<long> Ids, long RowsRead)> RunWithPlanAsync(SqlCommand cmd)
    {
        var ids = new List<long>();
        long read = 0;
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        do
        {
            var showplan = r.GetName(0).Contains("Showplan", StringComparison.Ordinal);
            while (await r.ReadAsync(Ct))
            {
                if (showplan)
                {
                    read += System.Text.RegularExpressions.Regex.Matches(r.GetString(0), "ActualRowsRead=\"(\\d+)\"")
                        .Sum(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    ids.Add(r.GetInt64(0));
                }
            }
        }
        while (await r.NextResultAsync(Ct));

        return (ids, read);
    }

    private sealed class AtPoint(SenderPoint at, Func<CancellationToken, Task> action) : ISenderFaults
    {
        public Task OnPointAsync(SenderPoint point, Guid attemptId, CancellationToken ct) => point == at ? action(ct) : Task.CompletedTask;
    }
}
