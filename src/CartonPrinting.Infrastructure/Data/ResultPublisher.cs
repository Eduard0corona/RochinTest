using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Datos opcionales de una versión del resultado.</summary>
public sealed record ResultExtras
{
    public static readonly ResultExtras None = new();

    /// <summary>Intento citado; si falta, el último intento que pudo transmitir.</summary>
    public Guid? AttemptId { get; init; }

    /// <summary>Evidencia que decidió (CONFIRMED) o que contradice (SERIAL_MISMATCH).</summary>
    public long? ObservationId { get; init; }

    public long? AuthorizationId { get; init; }

    public long? OperatorActionId { get; init; }
}

/// <summary>
/// Construye y escribe en el Outbox el <c>PrintJobResult</c> de una transición publicable, en la
/// misma transacción que la transición. La versión es la <c>ResultVersion</c> que la transición
/// acaba de incrementar.
/// </summary>
public static class ResultPublisher
{
    public static async Task<PrintJobResultEvent> PublishJobAsync(
        SqlConnection conn, SqlTransaction tx, Guid printJobId, DateTime occurredAtUtc, ResultExtras extras, CancellationToken ct)
    {
        var job = await JobQueries.GetJobAsync(conn, tx, printJobId, ct)
            ?? throw new InvalidOperationException($"PrintJob {printJobId} no existe.");
        var outcome = StateMachineSpec.Instance.Machines["PrintJob"].ResultOutcome[job.Status]
            ?? throw new InvalidOperationException($"El estado {job.Status} no es publicable.");
        var attempts = await JobQueries.GetAttemptsAsync(conn, tx, printJobId, ct);
        var attempt = extras.AttemptId is { } aid
            ? attempts.Single(a => a.AttemptId == aid)
            : attempts.Where(a => a.MayHaveSent).MaxBy(a => a.AttemptNumber);

        PrintJobResultEvent.EvidenceInfo? evidence = null;
        DateTime? evidenceAt = null;
        if (extras.ObservationId is { } oid)
        {
            var obs = await JobQueries.GetObservationAsync(conn, tx, oid, ct)
                ?? throw new InvalidOperationException($"EvidenceObservation {oid} no existe.");
            evidence = new PrintJobResultEvent.EvidenceInfo
            {
                Type = obs.EvidenceType,
                Level = obs.EvidenceLevel,
                CorrelatedTo = obs.CorrelatedTo,
                Verified = outcome == ResultOutcome.Confirmed,
                ReadSerial = obs.ReadSerial,
                Reference = obs.SourceReference,
                ObservedAt = obs.ObservedAt,
            };
            evidenceAt = obs.ObservedAt;
        }

        var evt = new PrintJobResultEvent
        {
            EventId = EventIds.PrintJobResult(job.IdempotencyKey, job.LabelType, job.ResultVersion),
            CorrelationId = job.DetectionId,
            OccurredAt = occurredAtUtc,
            SiteId = job.SiteId,
            StationId = job.StationId,
            PlcEpoch = job.PlcEpoch,
            CycleId = job.CycleId,
            IdempotencyKey = job.IdempotencyKey,
            LabelType = job.LabelType,
            ResultVersion = job.ResultVersion,
            Outcome = outcome,
            Final = StateMachineSpec.Instance.ContractOutcomeFinal[outcome],
            Reason = job.Reason,
            ConfigRevision = job.ConfigRevisionId,
            PrintJobId = job.PrintJobId,
            AttemptId = attempt?.AttemptId,
            LabelSerial = job.LabelSerial,
            WorkOrderId = job.WorkOrderId,
            ProductId = job.ProductId,
            LotId = job.LotId,
            PrinterId = attempt?.PrinterId ?? job.PrinterId,
            CartonId = job.CartonId,
            Evidence = evidence,
            ExceptionalReprint = extras.AuthorizationId is { } auth ? await ReadAuthorizationAsync(conn, tx, auth, ct) : null,
            OperatorAction = extras.OperatorActionId is { } oa ? await ReadOperatorActionAsync(conn, tx, oa, ct) : null,
            Timings = new PrintJobResultEvent.TimingsInfo
            {
                DetectionTimestamp = job.DetectionTimestamp,
                ReceivedAt = job.ReceivedAt,
                SendStartAt = attempt?.WriteStartedAt,
                WriteCompletedAt = attempt?.WriteCompletedAt,
                EvidenceAt = evidenceAt,
            },
        };

        await OutboxWriter.InsertJobResultAsync(conn, tx, evt, ct);
        return evt;
    }

    /// <summary>Resultado de una detección que terminó sin job (BLOCKED o EXPIRED), versión 1.</summary>
    public static async Task<PrintJobResultEvent> PublishDetectionAsync(
        SqlConnection conn, SqlTransaction tx, DetectionRecord detection, string labelType, DateTime occurredAtUtc, CancellationToken ct)
    {
        var outcome = StateMachineSpec.Instance.Machines["CartonDetection"].ResultOutcome[detection.Status]
            ?? throw new InvalidOperationException($"El estado {detection.Status} de la detección no es publicable.");
        var evt = new PrintJobResultEvent
        {
            EventId = EventIds.PrintJobResult(detection.IdempotencyKey, labelType, 1),
            CorrelationId = detection.DetectionId,
            OccurredAt = occurredAtUtc,
            SiteId = detection.SiteId,
            StationId = detection.StationId,
            PlcEpoch = detection.PlcEpoch,
            CycleId = detection.CycleId,
            IdempotencyKey = detection.IdempotencyKey,
            LabelType = labelType,
            ResultVersion = 1,
            Outcome = outcome,
            Final = StateMachineSpec.Instance.ContractOutcomeFinal[outcome],
            Reason = detection.Reason,
            ConfigRevision = detection.ConfigRevisionId,
            WorkOrderId = detection.WorkOrderId,
            CartonId = detection.CartonId,
            Timings = new PrintJobResultEvent.TimingsInfo
            {
                DetectionTimestamp = detection.DetectionTimestamp,
                ReceivedAt = detection.ReceivedAt,
            },
        };
        await OutboxWriter.InsertDetectionResultAsync(conn, tx, evt, ct);
        return evt;
    }

    private static async Task<PrintJobResultEvent.ExceptionalReprintInfo> ReadAuthorizationAsync(SqlConnection conn, SqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, "SELECT AuthorizationId, DuplicateRisk, Reason, ApprovedBy FROM dbo.ReprintAuthorization WHERE AuthorizationId = @id");
        cmd.BigInt("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            throw new InvalidOperationException($"ReprintAuthorization {id} no existe.");
        }

        return new PrintJobResultEvent.ExceptionalReprintInfo
        {
            AuthorizationId = r.GetInt64(0),
            DuplicateRisk = r.GetString(1),
            Reason = r.GetString(2),
            ApprovedBy = r.GetString(3),
        };
    }

    private static async Task<PrintJobResultEvent.OperatorActionInfo> ReadOperatorActionAsync(SqlConnection conn, SqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, "SELECT OperatorActionId, ActionType, PerformedBy, PerformedAt FROM dbo.OperatorAction WHERE OperatorActionId = @id");
        cmd.BigInt("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            throw new InvalidOperationException($"OperatorAction {id} no existe.");
        }

        return new PrintJobResultEvent.OperatorActionInfo
        {
            OperatorActionId = r.GetInt64(0),
            Type = r.GetString(1),
            PerformedBy = r.GetString(2),
            PerformedAt = r.GetUtc(3),
        };
    }
}
