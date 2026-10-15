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
        if (await ClockJumpedAsync(stations, station, clockGuard, active, clock, metrics, _logger, ct))
        {
            return new SweepReport(true, [], [], [], [], []);
        }

        metrics.SetGauge(MetricNames.ConfigRevisionActive, active.RevisionNumber, ("station", station.StationId));
        var now = clock.UtcNow;
        var margin = active.SendMarginMs;

        var expired = new List<SweepTransition>();
        expired.AddRange(await txs.ExpireDeadlinesAsync(station.SiteId, station.StationId, margin, now, ct));
        expired.AddRange(await txs.ExpireOutOfOrderAsync(station.SiteId, station.StationId, now, ct));
        foreach (var t in expired)
        {
            metrics.Increment(MetricNames.JobsExpired, ("reason", t.Reason!), ("from", t.From));
        }

        var released = await txs.ReleaseRetriesAsync(station.SiteId, station.StationId, margin, now, ct);

        var waiting = await txs.ExpireWaitingErpAsync(station.SiteId, station.StationId, margin, now, ct);
        foreach (var t in waiting)
        {
            metrics.Increment(MetricNames.DetectionsExpired, ("reason", DetectionReason.ErpUnavailable));
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
    /// que la tolerancia de la revisión activa, bloquea la estación con CLOCK_JUMP y devuelve true.
    /// </summary>
    public static async Task<bool> ClockJumpedAsync(
        StationConfigStore stations, StationSettings station, ClockGuard guard, StationRevision active, EdgeClock clock, CartonMetrics metrics,
        ILogger logger, CancellationToken ct)
    {
        var divergence = guard.Check();
        if (divergence <= active.ClockJumpToleranceMs)
        {
            return false;
        }

        metrics.Increment(MetricNames.ClockJump, ("station", station.StationId));
        var blocked = await stations.BlockAsync(station.SiteId, station.StationId, BlockReason.ClockJump, "SYSTEM", null, clock.UtcNow, ct);
        logger.LogError("Salto del reloj de pared de {Divergence} ms en {Station} (tolerancia {Tolerance} ms): estación {Action} con CLOCK_JUMP",
            divergence, station.StationId, active.ClockJumpToleranceMs, blocked ? "bloqueada" : "ya bloqueada");
        return true;
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
