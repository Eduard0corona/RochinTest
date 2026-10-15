using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Testing;

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
}
