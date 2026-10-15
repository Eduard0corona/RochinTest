using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Fila de dbo.PrintJob con los datos de su detección.</summary>
public sealed record JobRecord
{
    public required Guid PrintJobId { get; init; }
    public required Guid DetectionId { get; init; }
    public required string SiteId { get; init; }
    public required string StationId { get; init; }
    public required long PlcEpoch { get; init; }
    public required ulong CycleId { get; init; }
    public required string LabelType { get; init; }
    public required long ConfigRevisionId { get; init; }
    public required string PrinterId { get; init; }
    public required string WorkOrderId { get; init; }
    public required string ErpRevision { get; init; }
    public required string ProductId { get; init; }
    public required string LotId { get; init; }
    public string? CartonId { get; init; }
    public required string LabelSerial { get; init; }
    public required string TemplateRevision { get; init; }
    public required string Payload { get; init; }
    public required byte[] PayloadHash { get; init; }
    public required DateTime DeadlineAt { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public required byte AutoAttemptCount { get; init; }
    public DateTime? NextAttemptAt { get; init; }
    public required int ResultVersion { get; init; }
    public required bool ConfirmedByExceptionalReprint { get; init; }
    public long? LastOperatorActionId { get; init; }
    public Guid? LastExceptionalAttemptId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required DateTime ReceivedAt { get; init; }
    public DateTime? DetectionTimestamp { get; init; }

    public CycleIdentity Identity => new(SiteId, StationId, PlcEpoch, CycleId);
}

/// <summary>Fila de dbo.PrintAttempt.</summary>
public sealed record AttemptRecord(
    Guid AttemptId,
    Guid PrintJobId,
    int AttemptNumber,
    string Kind,
    long? AuthorizationId,
    string PrinterId,
    string OwnerInstanceId,
    long LeaseToken,
    string Outcome,
    bool MayHaveSent,
    DateTime ClaimedAt,
    DateTime? PreSendCheckAt,
    DateTime? WriteStartedAt,
    DateTime? WriteCompletedAt,
    DateTime? EvidenceDeadlineAt,
    long? ConfirmingObservationId,
    long? OperatorActionId,
    string? FailureDetail);

/// <summary>Fila de dbo.EvidenceObservation.</summary>
public sealed record ObservationRecord(
    long ObservationId,
    string SiteId,
    string StationId,
    long? PlcEpoch,
    ulong? CycleId,
    Guid? PrintJobId,
    Guid? AttemptId,
    string EvidenceType,
    string EvidenceLevel,
    string CorrelatedTo,
    string? ReadSerial,
    string SourceReference,
    DateTime ObservedAt,
    DateTime ReceivedAt,
    string RecordedBy);

public static class JobQueries
{
    private const string JobSelect = """
        SELECT pj.PrintJobId, pj.DetectionId, pj.SiteId, pj.StationId, pj.PlcEpoch, pj.CycleId, pj.LabelType, pj.ConfigRevisionId,
               pj.PrinterId, pj.WorkOrderId, pj.ErpRevision, pj.ProductId, pj.LotId, pj.CartonId, pj.LabelSerial, pj.TemplateRevision,
               pj.Payload, pj.PayloadHash, pj.DeadlineAt, pj.Status, pj.Reason, pj.AutoAttemptCount, pj.NextAttemptAt, pj.ResultVersion,
               pj.ConfirmedByExceptionalReprint, pj.LastOperatorActionId, pj.LastExceptionalAttemptId,
               cd.IdempotencyKey, cd.ReceivedAt, cd.DetectionTimestamp
        FROM dbo.PrintJob AS pj
        INNER JOIN dbo.CartonDetection AS cd ON cd.DetectionId = pj.DetectionId
        """;

    private const string AttemptSelect = """
        SELECT AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId, LeaseToken, Outcome, MayHaveSent,
               ClaimedAt, PreSendCheckAt, WriteStartedAt, WriteCompletedAt, EvidenceDeadlineAt, ConfirmingObservationId, OperatorActionId,
               FailureDetail
        FROM dbo.PrintAttempt
        """;

    public static async Task<JobRecord?> GetJobAsync(SqlConnection conn, SqlTransaction? tx, Guid printJobId, CancellationToken ct, bool updLock = false)
    {
        var sql = updLock
            ? JobSelect.Replace("FROM dbo.PrintJob AS pj", "FROM dbo.PrintJob AS pj WITH (UPDLOCK, ROWLOCK)", StringComparison.Ordinal)
            : JobSelect;
        await using var cmd = conn.Command(tx, sql + " WHERE pj.PrintJobId = @id");
        cmd.Guid("@id", printJobId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadJob(r) : null;
    }

    public static async Task<JobRecord?> GetJobByCycleAsync(SqlConnection conn, SqlTransaction? tx, CycleIdentity cycle, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, JobSelect + """
             WHERE pj.SiteId = @site AND pj.StationId = @station AND pj.PlcEpoch = @epoch AND pj.CycleId = @cycle
            """);
        cmd.NVarChar("@site", cycle.SiteId, 64).NVarChar("@station", cycle.StationId, 64).BigInt("@epoch", cycle.PlcEpoch).Cycle("@cycle", cycle.CycleId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadJob(r) : null;
    }

    public static async Task<IReadOnlyList<AttemptRecord>> GetAttemptsAsync(SqlConnection conn, SqlTransaction? tx, Guid printJobId, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, AttemptSelect + " WHERE PrintJobId = @id ORDER BY AttemptNumber");
        cmd.Guid("@id", printJobId);
        var list = new List<AttemptRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(ReadAttempt(r));
        }

        return list;
    }

    public static async Task<AttemptRecord?> GetAttemptAsync(SqlConnection conn, SqlTransaction? tx, Guid attemptId, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, AttemptSelect + " WHERE AttemptId = @id");
        cmd.Guid("@id", attemptId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAttempt(r) : null;
    }

    public static async Task<ObservationRecord?> GetObservationAsync(SqlConnection conn, SqlTransaction? tx, long observationId, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            SELECT ObservationId, SiteId, StationId, PlcEpoch, CycleId, PrintJobId, AttemptId, EvidenceType, EvidenceLevel, CorrelatedTo,
                   ReadSerial, SourceReference, ObservedAt, ReceivedAt, RecordedBy
            FROM dbo.EvidenceObservation WHERE ObservationId = @id
            """);
        cmd.BigInt("@id", observationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ObservationRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetNullableInt64(3), r.GetNullableCycleId(4),
                r.GetNullableGuid(5), r.GetNullableGuid(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetNullableString(10),
                r.GetString(11), r.GetUtc(12), r.GetUtc(13), r.GetString(14))
            : null;
    }

    private static JobRecord ReadJob(SqlDataReader r) => new()
    {
        PrintJobId = r.GetGuid(0),
        DetectionId = r.GetGuid(1),
        SiteId = r.GetString(2),
        StationId = r.GetString(3),
        PlcEpoch = r.GetInt64(4),
        CycleId = r.GetCycleId(5),
        LabelType = r.GetString(6),
        ConfigRevisionId = r.GetInt64(7),
        PrinterId = r.GetString(8),
        WorkOrderId = r.GetString(9),
        ErpRevision = r.GetString(10),
        ProductId = r.GetString(11),
        LotId = r.GetString(12),
        CartonId = r.GetNullableString(13),
        LabelSerial = r.GetString(14),
        TemplateRevision = r.GetString(15),
        Payload = r.GetString(16),
        PayloadHash = r.GetBytes32(17),
        DeadlineAt = r.GetUtc(18),
        Status = r.GetString(19),
        Reason = r.GetNullableString(20),
        AutoAttemptCount = r.GetByte(21),
        NextAttemptAt = r.GetNullableUtc(22),
        ResultVersion = r.GetInt32(23),
        ConfirmedByExceptionalReprint = r.GetBoolean(24),
        LastOperatorActionId = r.GetNullableInt64(25),
        LastExceptionalAttemptId = r.GetNullableGuid(26),
        IdempotencyKey = r.GetString(27),
        ReceivedAt = r.GetUtc(28),
        DetectionTimestamp = r.GetNullableUtc(29),
    };

    private static AttemptRecord ReadAttempt(SqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetNullableInt64(4), r.GetString(5), r.GetString(6), r.GetInt64(7),
        r.GetString(8), r.GetBoolean(9), r.GetUtc(10), r.GetNullableUtc(11), r.GetNullableUtc(12), r.GetNullableUtc(13),
        r.GetNullableUtc(14), r.GetNullableInt64(15), r.GetNullableInt64(16), r.GetNullableString(17));
}
