using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Deadline;

/// <summary>Backoff mayor que el plazo (T18) y relojes fuera de tolerancia (T37).</summary>
public sealed class ClockAndRetryTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f3clock", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// T18 · Backoff mayor que el plazo restante: el job en RETRY_WAIT vence con DEADLINE_PASSED y nunca
    /// vuelve a READY, ni antes ni después de NextAttemptAt; PrintJobResult EXPIRED; cero bytes.
    /// </summary>
    [Fact]
    [Trait("Spec", "T18")]
    public async Task Retry_backoff_longer_than_the_remaining_deadline_expires_and_never_returns_to_ready()
    {
        foreach (var catchUp in new[] { false, true })
        {
            var s = await StationScenario.CreateAsync(_db, ct: Ct);
            var sweep = new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);
            var (job, attempt) = await s.SendingJobAsync(400, Ct);
            var notSent = await s.Txc.NotSentAsync(attempt, s.Clock.UtcNow, "conexión rechazada", NotSentCause.ConnectionFailed, 5_000, 3, s.Clock.UtcNow, Ct);
            Assert.Equal(JobStatus.RetryWait, notSent.JobStatus);
            var row = await s.JobAsync(job, Ct);
            Assert.True(row.NextAttemptAt > row.DeadlineAt);

            if (catchUp)
            {
                s.AdvanceMs(6_000);   // el barrido llega después de NextAttemptAt y del plazo a la vez
            }
            else
            {
                s.AdvanceMs(1_100);   // vence el plazo antes de NextAttemptAt
            }

            var report = await sweep.SweepOnceAsync(Ct);
            Assert.Empty(report.Released);
            var expired = Assert.Single(report.Expired);
            Assert.Equal((JobStatus.RetryWait, JobReason.DeadlinePassed), (expired.From, expired.Reason));
            s.AdvanceMs(6_000);
            Assert.Empty((await sweep.SweepOnceAsync(Ct)).Released);

            Assert.Equal((JobStatus.Expired, JobReason.DeadlinePassed), ((await s.JobAsync(job, Ct)).Status, (await s.JobAsync(job, Ct)).Reason));
            Assert.Single(await s.AttemptsAsync(job, Ct));   // solo el intento NOT_SENT original
            Assert.Equal(("EXPIRED", "DEADLINE_PASSED"), ((await s.ResultsAsync(job, Ct)).Last().GetProperty("outcome").GetString(),
                (await s.ResultsAsync(job, Ct)).Last().GetProperty("reason").GetString()));
            Assert.Equal(1, s.Metrics.Total(MetricNames.JobsExpired, ("from", JobStatus.RetryWait), ("reason", JobReason.DeadlinePassed)));
        }
    }

    /// <summary>
    /// T37 · Desfase del reloj del PLC (PLC_TIMESTAMP, tolerancia 50 ms): con el PLC adelantado 300 ms la
    /// estación se bloquea con CLOCK_SKEW y las detecciones (la que lo reveló y las siguientes) quedan BLOCKED
    /// con STATION_DISABLED, sin job.
    /// </summary>
    [Fact]
    [Trait("Spec", "T37")]
    public async Task Plc_clock_ahead_beyond_tolerance_blocks_the_station_with_clock_skew()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(Ct);
        Assert.Equal(DetectionStatus.JobCreated, (await ingest.ProcessRecordAsync(s.Cycle(500), Ct)).Result!.FinalStatus);

        var ahead = await ingest.ProcessRecordAsync(s.Cycle(501, detectionTimestamp: s.Clock.UtcNow.AddMilliseconds(300)), Ct);
        var station = (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State;
        Assert.Equal((false, BlockReason.ClockSkew), (station.LabelingEnabled, station.BlockReason));
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled, (Guid?)null), (ahead.Result!.FinalStatus, ahead.Result.FinalReason, ahead.Result.PrintJobId));

        s.AdvanceMs(400);
        var next = await ingest.ProcessRecordAsync(s.Cycle(502), Ct);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled), (next.Result!.FinalStatus, next.Result.FinalReason));
        Assert.Equal(1, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.Equal(2, (await s.ResultsAsync(ct: Ct)).Count(r => r.GetProperty("outcome").GetString() == "BLOCKED"));
        Assert.Contains(s.Metrics.Samples(MetricNames.ClockSkewMs, ("station", s.StationId)), ms => ms >= 300);
    }

    /// <summary>
    /// T37 (variante) · El reloj del EdgeHost retrocede 2 s (ajuste de NTP) con jobs READY: la divergencia
    /// entre UtcNow y el monotónico supera ClockJumpToleranceMs; la estación se bloquea con CLOCK_JUMP y ningún
    /// claim se evalúa con el reloj saltado (lo detecte el claim o el barrido).
    /// </summary>
    [Fact]
    [Trait("Spec", "T37")]
    public async Task Wall_clock_jump_blocks_the_station_and_no_claim_is_evaluated_with_it()
    {
        foreach (var detectedBy in new[] { "claim", "sweep" })
        {
            var s = await StationScenario.CreateAsync(_db, ct: Ct);
            var jobs = new[] { await s.ReadyJobAsync(600, Ct), await s.ReadyJobAsync(601, Ct) };
            var manual = new ManualTimeProvider(new DateTimeOffset(s.Clock.UtcNow, TimeSpan.Zero));
            var clock = new EdgeClock(manual);
            var guard = new ClockGuard(clock);
            var claims = new ClaimService(s.Sql, s.Txb, s.Metrics, clock, guard);
            var sweep = new StationSweepService(s.Settings, s.Txs, s.Stations, guard, clock, s.Metrics);

            manual.Advance(TimeSpan.FromMilliseconds(100));
            Assert.False((await sweep.SweepOnceAsync(Ct)).ClockJump);

            manual.JumpWall(TimeSpan.FromSeconds(-2));
            if (detectedBy == "sweep")
            {
                Assert.True((await sweep.SweepOnceAsync(Ct)).ClockJump);
            }

            var outcome = (await claims.TryClaimAsync(jobs[0], s.Lease, Ct))!;
            Assert.False(outcome.Claimed);
            Assert.Equal(detectedBy == "claim" ? ClaimRejection.ClockJump : ClaimRejection.StationDisabled, outcome.Rejection);

            var station = (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State;
            Assert.Equal((false, BlockReason.ClockJump), (station.LabelingEnabled, station.BlockReason));
            Assert.Equal(1, s.Metrics.Total(MetricNames.ClockJump, ("station", s.StationId)));
            foreach (var job in jobs)
            {
                Assert.Empty(await s.AttemptsAsync(job, Ct));
                Assert.Equal(JobStatus.Ready, (await s.JobAsync(job, Ct)).Status);
            }
        }
    }

    /// <summary>
    /// Revisión de la Fase 3, ALTA-1: el reloj de pared retrocede 5 s y el bloqueo CLOCK_JUMP no se completa (otra
    /// transacción retiene la fila de StationConfig y el claim se cancela). El salto no se olvida: el claim siguiente
    /// lo vuelve a informar, bloquea la estación y no reclama el job, aunque la divergencia nueva sea cero.
    /// </summary>
    [Fact]
    [Trait("Spec", "T37")]
    public async Task A_clock_jump_whose_block_failed_is_not_forgotten()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(610, Ct);
        var manual = new ManualTimeProvider(new DateTimeOffset(s.Clock.UtcNow, TimeSpan.Zero));
        var clock = new EdgeClock(manual);
        var claims = new ClaimService(s.Sql, s.Txb, s.Metrics, clock, new ClockGuard(clock));
        manual.Advance(TimeSpan.FromMilliseconds(100));
        manual.JumpWall(TimeSpan.FromSeconds(-5));

        await using (var holder = new SqlConnection(_db.AdminConnectionString))
        {
            await holder.OpenAsync(Ct);
            await using var tx = (SqlTransaction)await holder.BeginTransactionAsync(Ct);
            await using (var hold = new SqlCommand(
                "SELECT LabelingEnabled FROM dbo.StationConfig WITH (UPDLOCK, ROWLOCK) WHERE SiteId = @site AND StationId = @st", holder, tx))
            {
                hold.Parameters.AddWithValue("@site", StationScenario.SiteId);
                hold.Parameters.AddWithValue("@st", s.StationId);
                await hold.ExecuteScalarAsync(Ct);
            }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancel.CancelAfter(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<Exception>(() => claims.TryClaimAsync(job, s.Lease, cancel.Token));
            Assert.True((await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State.LabelingEnabled);
            await tx.RollbackAsync(Ct);
        }

        manual.Advance(TimeSpan.FromMilliseconds(10));
        var outcome = (await claims.TryClaimAsync(job, s.Lease, Ct))!;
        Assert.Equal((false, ClaimRejection.ClockJump), (outcome.Claimed, outcome.Rejection));
        var station = (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State;
        Assert.Equal((false, BlockReason.ClockJump), (station.LabelingEnabled, station.BlockReason));
        Assert.Empty(await s.AttemptsAsync(job, Ct));
        Assert.Equal(JobStatus.Ready, (await s.JobAsync(job, Ct)).Status);
        Assert.Equal(1, s.Metrics.Total(MetricNames.ClockJump, ("station", s.StationId)));

        // Con el bloqueo confirmado, el salto deja de retenerse; la estación sigue bloqueada en SQL.
        manual.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(ClaimRejection.StationDisabled, (await claims.TryClaimAsync(job, s.Lease, Ct))!.Rejection);
    }

    /// <summary>
    /// Revisión de la Fase 3, MEDIA-3: J13 tiene su propia guarda de plazo. Con NextAttemptAt cumplido y sin pasar
    /// antes por la expiración, el job no vuelve a READY si su plazo efectivo venció (aunque el congelado siga
    /// vigente), si la revisión con que se calculó ya no es la activa, o si venció el congelado.
    /// </summary>
    [Fact]
    [Trait("Spec", "T18")]
    public async Task Release_from_retry_wait_has_its_own_deadline_guard()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, attempt) = await s.SendingJobAsync(420, Ct);
        await s.Txc.NotSentAsync(attempt, s.Clock.UtcNow, "conexión rechazada", NotSentCause.ConnectionFailed, 10, 3, s.Clock.UtcNow, Ct);
        var row = await s.JobAsync(job, Ct);
        Assert.Equal(JobStatus.RetryWait, row.Status);
        s.AdvanceMs(20);
        var rev = s.Revision.ConfigRevisionId;

        Assert.Null(await s.Txs.ReleaseRetryAsync(job, s.Clock.UtcNow.AddMilliseconds(50), 100, rev, s.Clock.UtcNow, Ct));    // efectivo vencido
        Assert.Null(await s.Txs.ReleaseRetryAsync(job, row.DeadlineAt, 100, rev + 1_000, s.Clock.UtcNow, Ct));                 // otra revisión
        s.AdvanceMs((row.DeadlineAt - s.Clock.UtcNow).TotalMilliseconds);
        Assert.Null(await s.Txs.ReleaseRetryAsync(job, row.DeadlineAt.AddSeconds(10), 100, rev, s.Clock.UtcNow, Ct));         // congelado vencido
        Assert.Equal(JobStatus.RetryWait, (await s.JobAsync(job, Ct)).Status);
        Assert.Single(await s.AttemptsAsync(job, Ct));
    }

    /// <summary>
    /// Revisión de la Fase 3, MEDIA-2: el barrido usa el plazo efectivo. Una revisión nueva acorta el presupuesto de
    /// tránsito; con el plazo congelado todavía vigente, el job en RETRY_WAIT (con NextAttemptAt cumplido) y uno READY
    /// vencen con DEADLINE_PASSED (J14 y J03), y la detección en WAITING_ERP con ERP_UNAVAILABLE (D07). Ninguno vuelve
    /// a READY.
    /// </summary>
    [Fact]
    [Trait("Spec", "T18")]
    public async Task The_sweep_expires_by_the_effective_deadline_after_a_revision_change()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var sweep = new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);
        var ready = await s.ReadyJobAsync(430, Ct);
        var (retry, attempt) = await s.SendingJobAsync(431, Ct);
        await s.Txc.NotSentAsync(attempt, s.Clock.UtcNow, "conexión rechazada", NotSentCause.ConnectionFailed, 200, 3, s.Clock.UtcNow, Ct);
        var waiting = await s.RegisterAsync(s.Cycle(432, workOrder: "WO-SIN"));
        Assert.Equal(DetectionStatus.WaitingErp, waiting.FinalStatus);

        await s.Stations.ActivateRevisionAsync(StationScenario.DefaultRevision(s.StationId) with
        {
            RevisionNumber = 8, TransitBudgetMs = 600, ChangeReason = "Línea más rápida",
        }, Ct);
        s.AdvanceMs(300);
        foreach (var job in new[] { ready, retry })
        {
            var context = (await s.Claims.LoadAsync(job, Ct))!;
            Assert.True(context.EffectiveDeadline <= s.Clock.UtcNow.AddMilliseconds(context.EffectiveSendMarginMs));
            Assert.True(context.Job.DeadlineAt > s.Clock.UtcNow.AddMilliseconds(context.EffectiveSendMarginMs));
        }

        var report = await sweep.SweepOnceAsync(Ct);
        Assert.Empty(report.Released);
        Assert.Equal(new[] { (ready, JobStatus.Ready), (retry, JobStatus.RetryWait) }.OrderBy(x => x.Item1),
            report.Expired.Select(t => (t.Id, t.From)).OrderBy(x => x.Item1));
        Assert.All(report.Expired, t => Assert.Equal(JobReason.DeadlinePassed, t.Reason));
        Assert.Equal(waiting.DetectionId, Assert.Single(report.WaitingErpExpired).Id);
        Assert.Equal((DetectionStatus.Expired, DetectionReason.ErpUnavailable),
            ((await s.DetectionAsync(432, Ct))!.Status, (await s.DetectionAsync(432, Ct))!.Reason));
    }
}
