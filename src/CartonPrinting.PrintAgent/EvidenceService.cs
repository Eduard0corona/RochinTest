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
    /// <summary>
    /// Horizonte de la reaplicación del arranque: una observación registrada y sin aplicar (el proceso cayó entre las dos
    /// transacciones de TX-D) se reaplica si se recibió dentro de las 24 h anteriores al arranque.
    /// </summary>
    public static readonly TimeSpan StartupHorizon = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, DateTime> _printedAt = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _appliedAt = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _appliedForVerified = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _verifiedFirst = new();
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
        await MeasureAsync(id, observation, outcome, ct);
        return outcome;
    }

    /// <summary>
    /// Al arrancar el sender, después de TX-R0, reaplica en orden de ObservationId lo que pudo quedar registrado sin aplicar si
    /// el proceso cayó entre las dos transacciones de TX-D: la evidencia de la estación recibida dentro de
    /// <see cref="StartupHorizon"/> cuyo job todavía puede cambiar con ella. Es decir, un job TRANSMITTED o UNKNOWN, o un job
    /// CONFIRMED con una lectura VERIFIED de otro serial, que es J18 (segunda revisión de la Fase 4, M-3; I25 y T44: una
    /// corrección nunca se pierde en silencio). Una sola consulta acotada por la clave primaria, guiada por la evidencia
    /// reciente y no por el histórico de jobs (tercera revisión, MEDIA-3).
    /// </summary>
    public async Task<int> ReapplyOpenAsync(string siteId, string stationId, CancellationToken ct)
    {
        var ids = await db.ReadAsync<IReadOnlyList<long>>(async conn =>
        {
            await using var cmd = conn.Command(null, EvidenceQueries.StartupReapply);
            cmd.DateTime2("@since", clock.UtcNow - StartupHorizon).NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64);
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
            var outcome = await txd.ApplyAsync(id, clock.UtcNow, ct);
            CountAlerts(outcome);
            if (outcome.Action != EvidenceAction.Recorded)
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
            await using var cmd = conn.Command(null, EvidenceQueries.ForJobSince);
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

    private async Task MeasureAsync(long observationId, ObservationInput o, EvidenceOutcome outcome, CancellationToken ct)
    {
        CountAlerts(outcome);

        if (outcome.PrintJobId is not { } jobId)
        {
            return;
        }

        var attempts = await db.ReadAsync(conn => JobQueries.GetAttemptsAsync(conn, null, jobId, ct), ct);
        if (SentAt(attempts) is { } sent)
        {
            // Lo que quedó pendiente durante SENDING se mide antes, en su orden. La confirmación cuenta aunque la haya
            // aplicado la reaplicación del sender antes que este pase (segunda revisión de la Fase 4, B-5).
            await MeasurePendingAsync(jobId, ct);
            var confirmed = outcome.Action is EvidenceAction.Confirmed or EvidenceAction.ExceptionalConfirmed
                || attempts.Any(a => a.ConfirmingObservationId == observationId);
            Measure(jobId, sent, o.EvidenceLevel, o.ObservedAt, confirmed);
        }
        else
        {
            // Llegó antes de TX-C (todavía sin WriteStartedAt): se mide al reaplicar después de TX-C, o ahora mismo
            // si TX-C confirmó entre la lectura y el encolado. Solo para métricas: si se acumulan jobs que nunca se
            // escriben, lo pendiente se descarta.
            if (_pending.Count > 10_000)
            {
                _pending.Clear();
            }

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
        if (_printedAt.Count > 10_000 || _appliedAt.Count > 10_000 || _appliedForVerified.Count > 10_000 || _verifiedFirst.Count > 10_000)
        {
            _printedAt.Clear();
            _appliedAt.Clear();
            _appliedForVerified.Clear();
            _verifiedFirst.Clear();
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
                metrics.Record(MetricNames.SendStartToAppliedMs, (observedAt - sent).TotalMilliseconds);

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

                // APPLIED y VERIFIED (los dos por ciclo) también en cualquier orden.
                if (_verifiedFirst.TryRemove(jobId, out var verifiedEarlier))
                {
                    metrics.Record(MetricNames.AppliedToVerifiedMs, (verifiedEarlier - observedAt).TotalMilliseconds);
                }
                else
                {
                    _appliedForVerified[jobId] = observedAt;
                }

                break;
            case EvidenceLevel.Verified:
                if (confirmed)
                {
                    metrics.Record(MetricNames.SendToVerifiedMs, (observedAt - sent).TotalMilliseconds);
                }

                if (_appliedForVerified.TryRemove(jobId, out var applied))
                {
                    metrics.Record(MetricNames.AppliedToVerifiedMs, (observedAt - applied).TotalMilliseconds);
                }
                else
                {
                    _verifiedFirst[jobId] = observedAt;
                }

                break;
        }
    }

    private async Task<DateTime?> SentAtAsync(Guid jobId, CancellationToken ct) =>
        SentAt(await db.ReadAsync(conn => JobQueries.GetAttemptsAsync(conn, null, jobId, ct), ct));

    private static DateTime? SentAt(IReadOnlyList<AttemptRecord> attempts) =>
        attempts.Where(a => a.WriteStartedAt is not null).MaxBy(a => a.AttemptNumber)?.WriteStartedAt;
}

/// <summary>
/// Consultas de evidencia de la ruta crítica (segunda revisión de la Fase 4, M-2). <c>EvidenceObservation</c> solo crece y no
/// tiene índice por <c>PrintJobId</c> ni por <c>ReceivedAt</c>: estas consultas no la recorren completa. Son públicas para que
/// las pruebas comprueben su plan.
/// </summary>
public static class EvidenceQueries
{
    /// <summary>
    /// Cota inferior de ObservationId para "recibida desde @since": el último ObservationId recibido más de 5 minutos antes de
    /// @since, buscado hacia atrás en la clave primaria, así que solo se leen las filas recientes. ReceivedAt se toma del reloj
    /// del EdgeHost justo antes de insertar: una fila recibida desde @since no puede tener un ObservationId menor salvo que, entre
    /// dos registros, el reloj retroceda más de 5 minutos, o que otro EdgeHost que comparte SQL Server tenga el reloj atrasado
    /// más de 5 minutos (supuesto declarado; tercera revisión de la Fase 4, BAJA-2). Una cota por estación evitaría lo segundo,
    /// pero recorre la tabla completa cuando la estación no tiene evidencia anterior (una estación nueva o con nivel NONE). Si el
    /// supuesto falla, el efecto es conservador: la rama del job (PRINTED) puede no reaplicar una observación y el job termina
    /// UNKNOWN por ventana vencida; las lecturas de ciclo y la guarda de TX-D usan IX_EvidenceObservation_Cycle, sin cota.
    /// Quien la usa fuerza la búsqueda por rango en la clave primaria (FORCESEEK): en las pruebas el plan ya era ese sin la
    /// sugerencia; se deja para que no dependa de las estadísticas.
    /// </summary>
    public const string FromObservation = """
        DECLARE @from BIGINT = ISNULL((SELECT TOP (1) ObservationId FROM dbo.EvidenceObservation
            WHERE ReceivedAt < DATEADD(MINUTE, -5, @since) ORDER BY ObservationId DESC), 0);

        """;

    /// <summary>
    /// La evidencia de un job recibida desde @since, en orden: la de su ciclo (@site, @station, @epoch, @cycle) por
    /// IX_EvidenceObservation_Cycle y la del job (@job, PRINTED) desde la cota de la clave primaria.
    /// </summary>
    public const string ForJobSince = FromObservation + """
        SELECT ObservationId FROM dbo.EvidenceObservation
        WHERE SiteId = @site AND StationId = @station AND PlcEpoch = @epoch AND CycleId = @cycle AND ReceivedAt >= @since
        UNION
        SELECT ObservationId FROM dbo.EvidenceObservation WITH (INDEX (PK_EvidenceObservation), FORCESEEK)
        WHERE ObservationId > @from AND PrintJobId = @job AND ReceivedAt >= @since AND SiteId = @site AND StationId = @station
        ORDER BY ObservationId;
        """;

    /// <summary>
    /// La evidencia de la estación recibida desde @since cuyo job todavía puede cambiar con ella (TRANSMITTED, UNKNOWN, o
    /// CONFIRMED con una lectura VERIFIED de otro serial), en orden. El job sale por PrintJobId (clave primaria) o por el ciclo
    /// (UQ_CartonDetection_Identity y UQ_PrintJob_DetectionLabel): nunca se recorre el histórico de jobs.
    /// </summary>
    public const string StartupReapply = FromObservation + """
        SELECT eo.ObservationId
        FROM dbo.EvidenceObservation AS eo WITH (INDEX (PK_EvidenceObservation), FORCESEEK)
        CROSS APPLY (
            SELECT pj.Status, pj.LabelSerial FROM dbo.PrintJob AS pj WHERE pj.PrintJobId = eo.PrintJobId
            UNION ALL
            SELECT pj.Status, pj.LabelSerial
            FROM dbo.CartonDetection AS cd
            INNER JOIN dbo.PrintJob AS pj ON pj.DetectionId = cd.DetectionId
            WHERE eo.PrintJobId IS NULL AND cd.SiteId = eo.SiteId AND cd.StationId = eo.StationId AND cd.PlcEpoch = eo.PlcEpoch
              AND cd.CycleId = eo.CycleId
        ) AS j
        WHERE eo.ObservationId > @from AND eo.ReceivedAt >= @since AND eo.SiteId = @site AND eo.StationId = @station
          AND (j.Status IN ('TRANSMITTED', 'UNKNOWN') OR (j.Status = 'CONFIRMED' AND eo.EvidenceLevel = 'VERIFIED' AND eo.ReadSerial <> j.LabelSerial))
        ORDER BY eo.ObservationId;
        """;
}
