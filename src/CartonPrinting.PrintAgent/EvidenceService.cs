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

    public async Task<EvidenceOutcome> SubmitAsync(ObservationInput observation, CancellationToken ct)
    {
        var id = await txd.RecordAsync(observation, clock.UtcNow, ct);
        var outcome = await txd.ApplyAsync(id, clock.UtcNow, ct);
        await MeasureAsync(observation, outcome, ct);
        return outcome;
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
        var applied = 0;
        foreach (var id in ids)
        {
            if ((await txd.ApplyAsync(id, clock.UtcNow, ct)).Action != EvidenceAction.Recorded)
            {
                applied++;
            }
        }

        await MeasurePendingAsync(job.PrintJobId, ct);
        return applied;
    }

    private async Task MeasureAsync(ObservationInput o, EvidenceOutcome outcome, CancellationToken ct)
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
                if (_printedAt.TryGetValue(jobId, out var printed))
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
