using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.IntegrationTests.Deadline;

/// <summary>Plazo, configuración versionada y ERP con la estación bloqueada (Fase 3, pruebas de integración).</summary>
public sealed class DeadlineIntegrationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f3int", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StationSweepService Sweep(StationScenario s) => new(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);

    /// <summary>
    /// T21 · Claim con plazo vencido: el claim afecta 0 filas, no deja fila en @claimed, no lanza ni crea
    /// PrintAttempt; el barrido pasa el job a EXPIRED y publica el resultado. Por el ClaimService:
    /// claim_rejected_total{cause=deadline}.
    /// </summary>
    [Fact]
    [Trait("Spec", "T21")]
    public async Task Claim_with_a_past_deadline_affects_no_rows_and_the_sweep_expires_the_job()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(10, Ct);
        s.AdvanceMs(2_000);
        var row = await s.JobAsync(job, Ct);
        var now = s.Clock.UtcNow;

        var claim = await s.Txb.ExecuteAsync(new ClaimRequest(job, Guid.NewGuid(), s.OwnerInstanceId, s.Lease.FencingToken, now, row.DeadlineAt,
            s.Revision.SendMarginMs), Ct);
        Assert.False(claim.Claimed);
        Assert.Empty(await s.AttemptsAsync(job, Ct));
        Assert.Equal(JobStatus.Ready, (await s.JobAsync(job, Ct)).Status);

        var report = await Sweep(s).SweepOnceAsync(Ct);
        var expired = Assert.Single(report.Expired);
        Assert.Equal((job, JobStatus.Ready, JobReason.DeadlinePassed), (expired.Id, expired.From, expired.Reason));
        var result = Assert.Single(await s.ResultsAsync(job, Ct));
        Assert.Equal(("EXPIRED", "DEADLINE_PASSED"), (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString()));
        Assert.Empty(await s.AttemptsAsync(job, Ct));

        // Por el ClaimService: el rechazo por plazo se mide y el job vence (J03, "claim rechazado por plazo").
        var other = await s.ReadyJobAsync(11, Ct);
        s.AdvanceMs(2_000);
        var outcome = (await s.Claims.TryClaimAsync(other, s.Lease, Ct))!;
        Assert.Equal((false, ClaimRejection.Deadline), (outcome.Claimed, outcome.Rejection));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ClaimRejected, ("cause", "deadline")));
        Assert.Empty(await s.AttemptsAsync(other, Ct));
        Assert.Equal(JobStatus.Expired, (await s.JobAsync(other, Ct)).Status);
    }

    /// <summary>
    /// T33 · Cambio de configuración con job pendiente: el job conserva la revisión 7 y su DeadlineAt; el
    /// claim usa el plazo efectivo más restrictivo (TransitBudgetMs 1000 y SendMarginMs 150 de la revisión
    /// 8); el UPDATE sobre la revisión 7 falla por TR_StationConfigRevision_Immutable; el resultado lleva
    /// configRevision de la 7; config_revision_active refleja la 8.
    /// </summary>
    [Fact]
    [Trait("Spec", "T33")]
    public async Task Pending_job_keeps_revision_7_and_the_claim_uses_the_most_restrictive_effective_deadline()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(20, Ct);
        var before = await s.JobAsync(job, Ct);
        var rev7 = s.Revision;

        var rev8 = await s.Stations.ActivateRevisionAsync(StationScenario.DefaultRevision(s.StationId) with
        {
            RevisionNumber = 8, TransitBudgetMs = 1_000, SendMarginMs = 150, ChangeReason = "Línea más rápida",
        }, Ct);
        var after = await s.JobAsync(job, Ct);
        Assert.Equal((rev7.ConfigRevisionId, before.DeadlineAt), (after.ConfigRevisionId, after.DeadlineAt));

        var context = (await s.Claims.LoadAsync(job, Ct))!;
        var detection = (await s.DetectionAsync(20, Ct))!;
        var recalculated = detection.DetectionTimestamp!.Value.AddMilliseconds(1_000 - rev8.DeviceLeadTimeMs - rev8.ClockSkewToleranceMs!.Value);
        Assert.Equal(recalculated, context.EffectiveDeadline);
        Assert.True(context.EffectiveDeadline < before.DeadlineAt);
        Assert.Equal(150, context.EffectiveSendMarginMs);

        // Entre el plazo efectivo y el congelado: con la revisión 7 se reclamaría; con la efectiva, no.
        s.Time.SetUtcNow(new DateTimeOffset(context.EffectiveDeadline.AddMilliseconds(-100), TimeSpan.Zero));
        Assert.True(s.Clock.UtcNow < before.DeadlineAt.AddMilliseconds(-rev7.SendMarginMs));
        var outcome = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
        Assert.Equal((false, ClaimRejection.Deadline), (outcome.Claimed, outcome.Rejection));
        Assert.Empty(await s.AttemptsAsync(job, Ct));

        var result = Assert.Single(await s.ResultsAsync(job, Ct));
        Assert.Equal(rev7.ConfigRevisionId, result.GetProperty("configRevision").GetInt64());

        var ex = await s.FailsAsync("UPDATE dbo.StationConfigRevision SET TransitBudgetMs = 900 WHERE ConfigRevisionId = @id",
            c => c.Parameters.AddWithValue("@id", rev7.ConfigRevisionId), ct: Ct);
        Assert.Equal(51001, ex.Number);
        Assert.Equal(1_500, (await s.Stations.GetRevisionAsync(rev7.ConfigRevisionId, Ct)).TransitBudgetMs);

        await Sweep(s).SweepOnceAsync(Ct);
        Assert.Equal(8.0, s.Metrics.Gauge(MetricNames.ConfigRevisionActive, ("station", s.StationId)));
    }

    /// <summary>
    /// T46 · Estación bloqueada mientras se espera el ERP: con el plazo vigente, la estación se bloquea por
    /// una IDENTITY_COLLISION en otro ciclo y después llega el snapshot: BLOCKED con STATION_DISABLED (D08),
    /// no EXPIRED con ERP_UNAVAILABLE; PrintJobResult BLOCKED sin job.
    /// </summary>
    [Fact]
    [Trait("Spec", "T46")]
    public async Task Snapshot_arriving_while_the_station_is_blocked_publishes_the_real_reason()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        const string order = "WO-T46";
        var waiting = await s.RegisterAsync(s.Cycle(30, workOrder: order), ct: Ct);
        Assert.Equal(DetectionStatus.WaitingErp, waiting.FinalStatus);

        await s.RegisterAsync(s.Cycle(31), ct: Ct);
        var collision = await s.RegisterAsync(s.Cycle(31) with { WorkOrderId = "WO-OTRA" }, ct: Ct);
        Assert.Equal(RegistrationOutcome.Collision, collision.Outcome);
        Assert.Equal(BlockReason.IdentityCollision, (await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct))!.Value.State.BlockReason);

        await s.AddSnapshotAsync("R1", "SKU-46", "LOT-46", Ct, order);
        var snapshot = (await s.Erp.FindValidAsync(order, s.Clock.UtcNow, Ct))!;
        var arrivals = new ErpArrivalService(s.Txe, s.Stations, s.Serials, s.Settings, s.Clock, s.Metrics);
        var result = (await arrivals.ProcessAsync((await s.DetectionAsync(30, Ct))!, snapshot, Ct))!;
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.StationDisabled, (Guid?)null), (result.FinalStatus, result.FinalReason, result.PrintJobId));

        var published = (await s.ResultsAsync(ct: Ct)).Single(r => r.GetProperty("cycleId").GetString() == "30");
        Assert.Equal(("BLOCKED", "STATION_DISABLED"), (published.GetProperty("outcome").GetString(), published.GetProperty("reason").GetString()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.DetectionsBlocked, ("reason", DetectionReason.StationDisabled)));

        // El barrido ya no la cambia: el motivo publicado es el real.
        s.AdvanceMs(5_000);
        Assert.Empty((await Sweep(s).SweepOnceAsync(Ct)).WaitingErpExpired);
        Assert.Equal(DetectionReason.StationDisabled, (await s.DetectionAsync(30, Ct))!.Reason);
    }
}
