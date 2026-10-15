using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.Orchestration;

/// <summary>Resultado de una pasada del barrido.</summary>
public sealed record SweepReport(
    bool ClockJump,
    IReadOnlyList<SweepTransition> Expired,
    IReadOnlyList<SweepTransition> Released,
    IReadOnlyList<SweepTransition> WaitingErpExpired,
    IReadOnlyList<SweepTransition> EvidenceElapsed,
    IReadOnlyList<GapClosure> Gaps);

/// <summary>
/// Barrido TX-S de una estación: cierra y publica, no protege (la protección contra envíos tardíos es la
/// guarda del claim). Antes de evaluar cualquier plazo comprueba el reloj: con un salto mayor que
/// ClockJumpToleranceMs bloquea la estación con CLOCK_JUMP y no evalúa nada (T37). Después: J03 y J14
/// por plazo (DEADLINE_PASSED) y por orden (OUT_OF_ORDER), J13 (RETRY_WAIT → READY solo con plazo
/// vigente), D07 (ERP_UNAVAILABLE), ventanas de evidencia vencidas, autorizaciones vencidas y G03.
/// Los plazos son los efectivos (sección 10.1 y SI-09): el menor entre el congelado y el recalculado con la
/// revisión activa, con el mayor de los dos márgenes, como en el claim y en TX-E (SI-14).
/// </summary>
public sealed class StationSweepService(
    StationSettings station, TxsSweep txs, StationConfigStore stations, ClockGuard clockGuard, EdgeClock clock, CartonMetrics metrics,
    ILogger<StationSweepService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<StationSweepService>.Instance;

    public async Task<SweepReport> SweepOnceAsync(CancellationToken ct)
    {
        var (_, active) = await stations.GetActiveAsync(station.SiteId, station.StationId, ct)
            ?? throw new InvalidOperationException($"La estación {station.SiteId}/{station.StationId} no tiene StationConfig.");
        var check = await ClockJumpedAsync(stations, station, clockGuard, active, clock, metrics, _logger, ct);
        if (check.Jumped)
        {
            return new SweepReport(true, [], [], [], [], []);
        }

        metrics.SetGauge(MetricNames.ConfigRevisionActive, active.RevisionNumber, ("station", station.StationId));
        var now = check.VerifiedUtc;
        var revisions = new Dictionary<long, StationRevision> { [active.ConfigRevisionId] = active };

        async Task<(DateTime Deadline, int Margin)> EffectiveAsync(DateTime frozenDeadline, long frozenRevisionId, DetectionRecord detection)
        {
            if (!revisions.TryGetValue(frozenRevisionId, out var frozen))
            {
                revisions[frozenRevisionId] = frozen = await stations.GetRevisionAsync(frozenRevisionId, ct);
            }

            return (DeadlinePolicy.Effective(frozenDeadline, active, detection), DeadlinePolicy.EffectiveSendMargin(frozen, active));
        }

        var pending = await txs.ListPendingJobsAsync(station.SiteId, station.StationId, ct);
        var effective = new Dictionary<Guid, (DateTime Deadline, int Margin)>();
        var expired = new List<SweepTransition>();
        foreach (var job in pending)
        {
            var e = effective[job.PrintJobId] = await EffectiveAsync(job.DeadlineAt, job.ConfigRevisionId, job.Detection);
            if (e.Deadline <= now.AddMilliseconds(e.Margin)
                && await txs.ExpireEffectiveDeadlineAsync(station.SiteId, station.StationId, job.PrintJobId, e.Deadline, e.Margin, active.ConfigRevisionId,
                    now, ct) is { } t)
            {
                expired.Add(t);
            }
        }

        expired.AddRange(await txs.ExpireOutOfOrderAsync(station.SiteId, station.StationId, now, ct));
        foreach (var t in expired)
        {
            metrics.Increment(MetricNames.JobsExpired, ("reason", t.Reason!), ("from", t.From));
        }

        var released = new List<SweepTransition>();
        foreach (var job in pending.Where(j => j.Status == JobStatus.RetryWait && j.NextAttemptAt <= now && expired.All(t => t.Id != j.PrintJobId)))
        {
            var e = effective[job.PrintJobId];
            if (await txs.ReleaseRetryAsync(job.PrintJobId, e.Deadline, e.Margin, active.ConfigRevisionId, now, ct) is { } t)
            {
                released.Add(t);
            }
        }

        var waiting = new List<SweepTransition>();
        foreach (var detection in await txs.ListWaitingErpAsync(station.SiteId, station.StationId, ct))
        {
            var e = await EffectiveAsync(detection.DeadlineAt, detection.ConfigRevisionId, detection);
            if (e.Deadline <= now.AddMilliseconds(e.Margin)
                && await txs.ExpireWaitingErpDetectionAsync(detection.DetectionId, e.Deadline, e.Margin, active.ConfigRevisionId, now, ct) is { } t)
            {
                waiting.Add(t);
                metrics.Increment(MetricNames.DetectionsExpired, ("reason", DetectionReason.ErpUnavailable));
            }
        }

        var evidence = new List<SweepTransition>();
        foreach (var attemptId in await txs.ElapsedEvidenceWindowsAsync(station.SiteId, station.StationId, now, ct))
        {
            if (await txs.ElapseEvidenceWindowAsync(attemptId, now, ct) is { } t)
            {
                evidence.Add(t);
                metrics.Increment(MetricNames.EvidenceWindowElapsed);
            }
        }

        await txs.ExpireAuthorizationsAsync(station.SiteId, station.StationId, now, ct);

        var gaps = await txs.CloseGapsAsync(station.SiteId, station.StationId, now, ct);
        foreach (var gap in gaps.Where(g => g.UnrecoveredCount > 0))
        {
            metrics.Add(MetricNames.CyclesUnobserved, (long)gap.UnrecoveredCount, ("station", station.StationId));
        }

        return new SweepReport(false, expired, released, waiting, evidence, gaps);
    }

    /// <summary>
    /// Comprobación compartida por el barrido y el claim: si el reloj de pared divergió del monotónico más
    /// que la tolerancia de la revisión activa, o hay un salto anterior cuyo bloqueo todavía no se confirmó,
    /// bloquea la estación con CLOCK_JUMP y devuelve <c>Jumped</c>. El salto solo deja de retenerse cuando el
    /// bloqueo quedó confirmado en SQL: si <see cref="StationConfigStore.BlockAsync"/> falla, la excepción sube y
    /// la próxima comprobación lo reintenta. Sin salto, <c>VerifiedUtc</c> es el instante que se verificó, el que
    /// debe usarse como <c>@EdgeNowUtc</c>.
    /// </summary>
    public static async Task<ClockReading> ClockJumpedAsync(
        StationConfigStore stations, StationSettings station, ClockGuard guard, StationRevision active, EdgeClock clock, CartonMetrics metrics,
        ILogger logger, CancellationToken ct)
    {
        var reading = guard.Check(active.ClockJumpToleranceMs);
        if (!reading.Jumped)
        {
            return reading;
        }

        if (reading.Detected)
        {
            metrics.Increment(MetricNames.ClockJump, ("station", station.StationId));
        }

        var blocked = await stations.BlockAsync(station.SiteId, station.StationId, BlockReason.ClockJump, "SYSTEM", null, clock.UtcNow, ct);
        guard.Acknowledge(reading);
        logger.LogError("Salto del reloj de pared de {Divergence} ms en {Station} (tolerancia {Tolerance} ms): estación {Action} con CLOCK_JUMP",
            reading.DivergenceMs, station.StationId, active.ClockJumpToleranceMs, blocked ? "bloqueada" : "ya bloqueada");
        return reading;
    }

    public async Task RunAsync(TimeSpan interval, TimeProvider loopTime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Barrido de {Station} fallido; se reintenta", station.StationId);
            }

            await Task.Delay(interval, loopTime, ct);
        }
    }
}
