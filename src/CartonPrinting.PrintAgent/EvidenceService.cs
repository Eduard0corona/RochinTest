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

        var attempts = await db.ReadAsync(conn => JobQueries.GetAttemptsAsync(conn, null, jobId, ct), ct);
        var sent = attempts.Where(a => a.WriteStartedAt is not null).MaxBy(a => a.AttemptNumber)?.WriteStartedAt;
        if (sent is null)
        {
            return;
        }

        switch (o.EvidenceLevel)
        {
            case EvidenceLevel.Printed:
                _printedAt[jobId] = o.ObservedAt;
                metrics.Record(MetricNames.SendToPrintedMs, (o.ObservedAt - sent.Value).TotalMilliseconds);
                break;
            case EvidenceLevel.Applied when _printedAt.TryGetValue(jobId, out var printed):
                metrics.Record(MetricNames.PrintedToAppliedMs, (o.ObservedAt - printed).TotalMilliseconds);
                break;
            case EvidenceLevel.Verified when outcome.Action == EvidenceAction.Confirmed:
                metrics.Record(MetricNames.SendToVerifiedMs, (o.ObservedAt - sent.Value).TotalMilliseconds);
                break;
        }
    }
}
