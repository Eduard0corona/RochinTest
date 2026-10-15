using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Fila de dbo.CartonDetection.</summary>
public sealed record DetectionRecord
{
    public required Guid DetectionId { get; init; }
    public required string SiteId { get; init; }
    public required string StationId { get; init; }
    public required long PlcEpoch { get; init; }
    public required ulong CycleId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required byte[] SourceFingerprint { get; init; }
    public required string Origin { get; init; }
    public required string Transport { get; init; }
    public required bool QueueOverflow { get; init; }
    public DateTime? DetectionTimestamp { get; init; }
    public DateTime? OpcSourceTimestamp { get; init; }
    public required DateTime ReceivedAt { get; init; }
    public required string DeadlineReference { get; init; }
    public required DateTime DeadlineAt { get; init; }
    public required long ConfigRevisionId { get; init; }
    public string? WorkOrderId { get; init; }
    public string? CartonId { get; init; }
    public required string Status { get; init; }
    public string? Reason { get; init; }

    public CycleIdentity Identity => new(SiteId, StationId, PlcEpoch, CycleId);
}

public static class DetectionQueries
{
    private const string Select = """
        SELECT DetectionId, SiteId, StationId, PlcEpoch, CycleId, IdempotencyKey, SourceFingerprint, Origin, Transport, QueueOverflow,
               DetectionTimestamp, OpcSourceTimestamp, ReceivedAt, DeadlineReference, DeadlineAt, ConfigRevisionId, WorkOrderId, CartonId,
               Status, Reason
        FROM dbo.CartonDetection
        """;

    public static async Task<DetectionRecord?> GetByIdentityAsync(SqlConnection conn, SqlTransaction? tx, CycleIdentity id, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, Select + " WHERE SiteId = @site AND StationId = @station AND PlcEpoch = @epoch AND CycleId = @cycle");
        cmd.NVarChar("@site", id.SiteId, 64).NVarChar("@station", id.StationId, 64).BigInt("@epoch", id.PlcEpoch).Cycle("@cycle", id.CycleId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static async Task<DetectionRecord?> GetAsync(SqlConnection conn, SqlTransaction? tx, Guid detectionId, CancellationToken ct, bool updLock = false)
    {
        var hint = updLock ? " WITH (UPDLOCK, ROWLOCK)" : string.Empty;
        await using var cmd = conn.Command(tx, Select.Replace("FROM dbo.CartonDetection", "FROM dbo.CartonDetection" + hint, StringComparison.Ordinal)
            + " WHERE DetectionId = @id");
        cmd.Guid("@id", detectionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public static async Task<IReadOnlyList<DetectionRecord>> ListAsync(SqlConnection conn, SqlTransaction? tx, string where, Action<SqlCommand> bind, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, Select + " WHERE " + where);
        bind(cmd);
        var list = new List<DetectionRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Read(r));
        }

        return list;
    }

    private static DetectionRecord Read(SqlDataReader r) => new()
    {
        DetectionId = r.GetGuid(0),
        SiteId = r.GetString(1),
        StationId = r.GetString(2),
        PlcEpoch = r.GetInt64(3),
        CycleId = r.GetCycleId(4),
        IdempotencyKey = r.GetString(5),
        SourceFingerprint = r.GetBytes32(6),
        Origin = r.GetString(7),
        Transport = r.GetString(8),
        QueueOverflow = r.GetBoolean(9),
        DetectionTimestamp = r.GetNullableUtc(10),
        OpcSourceTimestamp = r.GetNullableUtc(11),
        ReceivedAt = r.GetUtc(12),
        DeadlineReference = r.GetString(13),
        DeadlineAt = r.GetUtc(14),
        ConfigRevisionId = r.GetInt64(15),
        WorkOrderId = r.GetNullableString(16),
        CartonId = r.GetNullableString(17),
        Status = r.GetString(18),
        Reason = r.GetNullableString(19),
    };
}
