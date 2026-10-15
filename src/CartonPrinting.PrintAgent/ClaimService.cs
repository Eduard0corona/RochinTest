using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;

namespace CartonPrinting.PrintAgent;

/// <summary>Contexto de un job para el claim: lo congelado y la revisión activa.</summary>
public sealed record JobContext(JobRecord Job, DetectionRecord Detection, StationRevision Frozen, StationRevision Active, bool StationEnabled)
{
    public DateTime EffectiveDeadline => DeadlinePolicy.Effective(Job.DeadlineAt, Active, Detection);

    public int EffectiveSendMarginMs => DeadlinePolicy.EffectiveSendMargin(Frozen, Active);

    public byte EffectiveMaxAutoAttempts => DeadlinePolicy.EffectiveMaxAutoAttempts(Frozen, Active);

    public int? EffectiveGateFreshnessMs => DeadlinePolicy.EffectiveGateFreshness(Frozen, Active);
}

public sealed record ClaimOutcome(bool Claimed, Guid AttemptId, int AttemptNumber, ClaimRejection Rejection, JobContext Context, DateTime ClaimedAtUtc);

/// <summary>
/// Claim de un job por el sender de su impresora (TX-B). Si no obtiene fila, relee el job para saber
/// por qué: si la causa es plazo u orden de envío aplica J03; otro sender o un reintento duplicado
/// cuentan como conflicto. Nunca lanza por un claim perdido.
/// </summary>
public sealed class ClaimService(SqlDb db, TxbClaim txb, CartonMetrics metrics, EdgeClock clock)
{
    public async Task<JobContext?> LoadAsync(Guid printJobId, CancellationToken ct) =>
        await db.ReadAsync(async conn =>
        {
            var job = await JobQueries.GetJobAsync(conn, null, printJobId, ct);
            if (job is null)
            {
                return null;
            }

            var detection = (await DetectionQueries.GetAsync(conn, null, job.DetectionId, ct))!;
            var station = (await StationConfigStore.GetActiveAsync(conn, null, job.SiteId, job.StationId, ct))!.Value;
            var frozen = station.Active.ConfigRevisionId == job.ConfigRevisionId
                ? station.Active
                : await new StationConfigStore(db).GetRevisionAsync(job.ConfigRevisionId, ct);
            return new JobContext(job, detection, frozen, station.Active, station.State.LabelingEnabled);
        }, ct);

    public async Task<ClaimOutcome?> TryClaimAsync(Guid printJobId, LeaseGrant lease, CancellationToken ct)
    {
        using var activity = CartonTelemetry.ActivitySource.StartActivity("carton.claim");
        activity?.SetTag(CartonTelemetry.Tags.PrintJobId, printJobId);
        activity?.SetTag(CartonTelemetry.Tags.PrinterId, lease.PrinterId);
        var context = await LoadAsync(printJobId, ct);
        if (context is null)
        {
            return null;
        }

        activity?.SetTag(CartonTelemetry.Tags.CorrelationId, context.Job.DetectionId);
        activity?.SetTag(CartonTelemetry.Tags.IdempotencyKey, context.Job.Identity.IdempotencyKey);
        activity?.SetTag(CartonTelemetry.Tags.LabelSerial, context.Job.LabelSerial);

        var now = clock.UtcNow;
        var request = new ClaimRequest(printJobId, Guid.NewGuid(), lease.OwnerInstanceId, lease.FencingToken, now,
            context.EffectiveDeadline, context.EffectiveSendMarginMs);
        var result = await txb.ExecuteAsync(request, ct);
        if (result.Claimed)
        {
            activity?.SetTag(CartonTelemetry.Tags.AttemptId, result.AttemptId);
            return new ClaimOutcome(true, result.AttemptId, result.AttemptNumber, ClaimRejection.None, context, now);
        }

        var rejection = await txb.DiagnoseAsync(request, ct);
        activity?.SetTag("claimRejection", rejection.ToString());
        switch (rejection)
        {
            case ClaimRejection.NotReady:
                metrics.Increment(MetricNames.ClaimConflicts);
                break;
            case ClaimRejection.Deadline:
                metrics.Increment(MetricNames.ClaimRejected, ("cause", "deadline"));
                await ExpireAsync(printJobId, JobReason.DeadlinePassed, ct);
                break;
            case ClaimRejection.OutOfOrder:
                metrics.Increment(MetricNames.ClaimRejected, ("cause", "order"));
                await ExpireAsync(printJobId, JobReason.OutOfOrder, ct);
                break;
            default:
                metrics.Increment(MetricNames.ClaimRejected, ("cause", rejection.ToString()));
                break;
        }

        return new ClaimOutcome(false, request.AttemptId, 0, rejection, context, now);
    }

    private async Task ExpireAsync(Guid printJobId, string reason, CancellationToken ct)
    {
        if (await txb.ExpireReadyAsync(printJobId, reason, clock.UtcNow, ct) is not null)
        {
            metrics.Increment(MetricNames.JobsExpired, ("reason", reason), ("from", JobStatus.Ready));
        }
    }
}
