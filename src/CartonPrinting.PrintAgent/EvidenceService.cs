using System.Collections.Concurrent;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;

namespace CartonPrinting.PrintAgent;

/// <summary>
/// Entrada de evidencia (TX-D): toda observación se registra primero en EvidenceObservation, en su propia
/// transacción, y después se aplica (J10 a J12, J15, J18, A05 a A08, X01 a X03). Mide los tiempos de la
/// sección 19 (envío a impresa, impresa a aplicada, envío a verificada) y las alertas de mal etiquetado.
/// </summary>
public sealed class EvidenceService(TxdEvidence txd, SqlDb db, EdgeClock clock, CartonMetrics metrics)
{
    private readonly ConcurrentDictionary<Guid, DateTime> _printedAt = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _appliedAt = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<(string Level, DateTime ObservedAt)>> _pending = new();

    /// <summary>
    /// Registra la observación y la aplica junto con lo pendiente de su job o de su ciclo desde el último claim, en orden
    /// de ObservationId. Así, una lectura contradictoria que llegó durante SENDING y quedó registrada sin efecto no se
    /// pierde detrás de una confirmación posterior (I06, J11; revisión de la Fase 4, A2). Reaplicar es seguro: una
    /// observación ya aplicada, o anterior al estado vigente del job (ADR 0016), queda sin efecto.
    /// </summary>
    public async Task<EvidenceOutcome> SubmitAsync(ObservationInput observation, CancellationToken ct)
    {
        var id = await txd.RecordAsync(observation, clock.UtcNow, ct);
        EvidenceOutcome? outcome = null;
        if (await JobForAsync(observation, ct) is { } job && await LastClaimAsync(job.PrintJobId, ct) is { } since)
        {
            foreach (var (appliedId, applied) in await ApplyInOrderAsync(job, since, ct))
            {
                if (appliedId == id)
                {
                    outcome = applied;
                }
                else
                {
                    CountAlerts(applied);
                }
            }
        }

        outcome ??= await txd.ApplyAsync(id, clock.UtcNow, ct);
        await MeasureAsync(observation, outcome, ct);
        return outcome;
    }

    /// <summary>
    /// Al arrancar el sender, después de TX-R0: reaplica la evidencia de los jobs de la impresora que todavía esperan
    /// evidencia o quedaron sin resolver (TRANSMITTED o UNKNOWN), por si una observación quedó registrada sin aplicar.
    /// </summary>
    public async Task<int> ReapplyOpenAsync(string printerId, CancellationToken ct)
    {
        var jobs = await db.ReadAsync<IReadOnlyList<Guid>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT PrintJobId FROM dbo.PrintJob WHERE PrinterId = @printer AND Status IN ('TRANSMITTED', 'UNKNOWN')
                UNION
                SELECT pa.PrintJobId FROM dbo.PrintAttempt AS pa
                INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = pa.PrintJobId
                WHERE pa.PrinterId = @printer AND pj.Status IN ('TRANSMITTED', 'UNKNOWN');
                """);
            cmd.NVarChar("@printer", printerId, 64);
            var list = new List<Guid>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetGuid(0));
            }

            return list;
        }, ct);
        var applied = 0;
        foreach (var jobId in jobs)
        {
            if (await db.ReadAsync(conn => JobQueries.GetJobAsync(conn, null, jobId, ct), ct) is { } job
                && await LastClaimAsync(jobId, ct) is { } since)
            {
                applied += await ReapplyForJobAsync(job, since, ct);
            }
        }

        return applied;
    }

    /// <summary>
    /// Reaplica, en orden, las observaciones de la estación registradas desde <paramref name="since"/> (por
    /// ejemplo, al arrancar: una observación pudo quedar registrada y sin aplicar si el proceso cayó entre las
    /// dos transacciones de TX-D). Es seguro: una observación ya aplicada, o anterior al estado vigente del job
    /// (ADR 0016), queda sin efecto.
    /// </summary>
    public async Task<int> ReapplySinceAsync(string siteId, string stationId, DateTime since, CancellationToken ct)
    {
        var ids = await db.ReadAsync<IReadOnlyList<long>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT ObservationId FROM dbo.EvidenceObservation
                WHERE SiteId = @site AND StationId = @station AND ReceivedAt >= @since
                ORDER BY ObservationId;
                """);
            cmd.NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64).DateTime2("@since", since);
            var list = new List<long>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetInt64(0));
            }

            return list;
        }, ct);
        var applied = 0;
        foreach (var id in ids)
        {
            if ((await txd.ApplyAsync(id, clock.UtcNow, ct)).Action != EvidenceAction.Recorded)
            {
                applied++;
            }
        }

        return applied;
    }

    /// <summary>
    /// Reaplica la evidencia del job (por job o por su ciclo) recibida desde <paramref name="since"/>. El sender
    /// la llama después de TX-C: con una impresora rápida, la evidencia puede llegar mientras el intento sigue
    /// en SENDING, cuando todavía no hay transición que aplicar.
    /// </summary>
    public async Task<int> ReapplyForJobAsync(JobRecord job, DateTime since, CancellationToken ct)
    {
        var applied = 0;
        foreach (var (_, outcome) in await ApplyInOrderAsync(job, since, ct))
        {
            CountAlerts(outcome);
            if (outcome.Action != EvidenceAction.Recorded)
            {
                applied++;
            }
        }

        await MeasurePendingAsync(job.PrintJobId, ct);
        return applied;
    }

    /// <summary>Aplica, en orden de ObservationId, la evidencia del job (por job o por su ciclo) recibida desde <paramref name="since"/>.</summary>
    private async Task<IReadOnlyList<(long Id, EvidenceOutcome Outcome)>> ApplyInOrderAsync(JobRecord job, DateTime since, CancellationToken ct)
    {
        var ids = await db.ReadAsync<IReadOnlyList<long>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT ObservationId FROM dbo.EvidenceObservation
                WHERE ReceivedAt >= @since
                  AND (PrintJobId = @job
                       OR (SiteId = @site AND StationId = @station AND PlcEpoch = @epoch AND CycleId = @cycle))
                ORDER BY ObservationId;
                """);
            cmd.DateTime2("@since", since).Guid("@job", job.PrintJobId).NVarChar("@site", job.SiteId, 64).NVarChar("@station", job.StationId, 64)
                .BigInt("@epoch", job.PlcEpoch).Cycle("@cycle", job.CycleId);
            var list = new List<long>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(r.GetInt64(0));
            }

            return list;
        }, ct);
        var outcomes = new List<(long, EvidenceOutcome)>();
        foreach (var id in ids)
        {
            outcomes.Add((id, await txd.ApplyAsync(id, clock.UtcNow, ct)));
        }

        return outcomes;
    }

    /// <summary>El job al que corresponde una observación: el que nombra, o el de su ciclo.</summary>
    private Task<JobRecord?> JobForAsync(ObservationInput o, CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            if (o.PrintJobId is { } jobId)
            {
                return await JobQueries.GetJobAsync(conn, null, jobId, ct);
            }

            if (o.PlcEpoch is not { } epoch || o.CycleId is not { } cycle)
            {
                return null;
            }

            await using var cmd = conn.Command(null, """
                SELECT TOP (1) PrintJobId FROM dbo.PrintJob
                WHERE SiteId = @site AND StationId = @station AND PlcEpoch = @epoch AND CycleId = @cycle
                """);
            cmd.NVarChar("@site", o.SiteId, 64).NVarChar("@station", o.StationId, 64).BigInt("@epoch", epoch).Cycle("@cycle", cycle);
            return await cmd.ExecuteScalarAsync(ct) is Guid id ? await JobQueries.GetJobAsync(conn, null, id, ct) : null;
        }, ct);

    /// <summary>El instante del último claim del job (null si nunca se reclamó).</summary>
    private async Task<DateTime?> LastClaimAsync(Guid jobId, CancellationToken ct) =>
        (await db.ReadAsync(conn => JobQueries.GetAttemptsAsync(conn, null, jobId, ct), ct)).Select(a => (DateTime?)a.ClaimedAt).Max();

    /// <summary>Alertas de mal etiquetado (sección 19) de una transición, la aplique quien la aplique.</summary>
    private void CountAlerts(EvidenceOutcome outcome)
    {
        switch (outcome.Action)
        {
            case EvidenceAction.SerialMismatch:
                metrics.Increment(MetricNames.SerialMismatch);
                break;
            case EvidenceAction.Corrected:
                metrics.Increment(MetricNames.SerialMismatch);
                metrics.Increment(MetricNames.ConfirmedCorrected);
                break;
        }
    }

    private async Task MeasureAsync(ObservationInput o, EvidenceOutcome outcome, CancellationToken ct)
    {
        CountAlerts(outcome);

        if (outcome.PrintJobId is not { } jobId)
        {
            return;
        }

        if (await SentAtAsync(jobId, ct) is { } sent)
        {
            // Lo que quedó pendiente durante SENDING se mide antes, en su orden.
            await MeasurePendingAsync(jobId, ct);
            Measure(jobId, sent, o.EvidenceLevel, o.ObservedAt, outcome.Action is EvidenceAction.Confirmed or EvidenceAction.ExceptionalConfirmed);
        }
        else
        {
            // Llegó antes de TX-C (todavía sin WriteStartedAt): se mide al reaplicar después de TX-C, o ahora mismo
            // si TX-C confirmó entre la lectura y el encolado.
            _pending.GetOrAdd(jobId, _ => new ConcurrentQueue<(string, DateTime)>()).Enqueue((o.EvidenceLevel, o.ObservedAt));
            await MeasurePendingAsync(jobId, ct);
        }
    }

    /// <summary>Mide la evidencia que llegó mientras el intento estaba en SENDING, ya con la escritura registrada.</summary>
    private async Task MeasurePendingAsync(Guid jobId, CancellationToken ct)
    {
        if (!_pending.ContainsKey(jobId) || await SentAtAsync(jobId, ct) is not { } sent || !_pending.TryRemove(jobId, out var queue))
        {
            return;
        }

        var confirmed = await db.ReadAsync(async conn => (await JobQueries.GetJobAsync(conn, null, jobId, ct))?.Status == JobStatus.Confirmed, ct);
        while (queue.TryDequeue(out var item))
        {
            Measure(jobId, sent, item.Level, item.ObservedAt, confirmed);
        }
    }

    private void Measure(Guid jobId, DateTime sent, string level, DateTime observedAt, bool confirmed)
    {
        // Solo para métricas: si los pares sin completar se acumulan (jobs que nunca llegan a APPLIED), se descartan.
        if (_printedAt.Count > 10_000 || _appliedAt.Count > 10_000)
        {
            _printedAt.Clear();
            _appliedAt.Clear();
        }

        switch (level)
        {
            case EvidenceLevel.Printed:
                _printedAt[jobId] = observedAt;
                metrics.Record(MetricNames.SendToPrintedMs, (observedAt - sent).TotalMilliseconds);
                if (_appliedAt.TryRemove(jobId, out var appliedFirst))
                {
                    metrics.Record(MetricNames.PrintedToAppliedMs, (appliedFirst - observedAt).TotalMilliseconds);
                }

                break;
            case EvidenceLevel.Applied:
                // PRINTED (por job) y APPLIED (por ciclo) pueden medirse en cualquier orden: el par se registra cuando
                // se conocen los dos instantes observados.
                if (_printedAt.TryRemove(jobId, out var printed))
                {
                    metrics.Record(MetricNames.PrintedToAppliedMs, (observedAt - printed).TotalMilliseconds);
                }
                else
                {
                    _appliedAt[jobId] = observedAt;
                }

                break;
            case EvidenceLevel.Verified when confirmed:
                metrics.Record(MetricNames.SendToVerifiedMs, (observedAt - sent).TotalMilliseconds);
                break;
        }
    }

    private async Task<DateTime?> SentAtAsync(Guid jobId, CancellationToken ct)
    {
        var attempts = await db.ReadAsync(conn => JobQueries.GetAttemptsAsync(conn, null, jobId, ct), ct);
        return attempts.Where(a => a.WriteStartedAt is not null).MaxBy(a => a.AttemptNumber)?.WriteStartedAt;
    }
}
