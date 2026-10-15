using System.Text.Json.Serialization;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Transactions;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Registro OPC retenido en SequenceAnomaly.IncomingRecord para un reingreso posterior.</summary>
public sealed record RetainedRecord
{
    [JsonPropertyName("siteId")] public required string SiteId { get; init; }
    [JsonPropertyName("stationId")] public required string StationId { get; init; }
    [JsonPropertyName("plcEpoch")] public required long PlcEpoch { get; init; }
    [JsonPropertyName("cycleId")][JsonConverter(typeof(CycleIdConverter))] public required ulong CycleId { get; init; }
    [JsonPropertyName("detectionTimestamp")][JsonConverter(typeof(UtcMillisecondsConverter))] public DateTime? DetectionTimestamp { get; init; }
    [JsonPropertyName("workOrderId")] public string? WorkOrderId { get; init; }
    [JsonPropertyName("cartonId")] public string? CartonId { get; init; }
    [JsonPropertyName("transport")] public required string Transport { get; init; }
    [JsonPropertyName("queueOverflow")] public required bool QueueOverflow { get; init; }
    [JsonPropertyName("receivedAt")][JsonConverter(typeof(UtcMillisecondsConverter))] public required DateTime ReceivedAt { get; init; }
    [JsonPropertyName("opcNodeId")] public required string OpcNodeId { get; init; }
    [JsonPropertyName("opcSourceTimestamp")][JsonConverter(typeof(UtcMillisecondsConverter))] public DateTime? OpcSourceTimestamp { get; init; }
    [JsonPropertyName("sourceFingerprint")] public required string SourceFingerprint { get; init; }
}

public sealed record AnomalyRow(
    string SiteId,
    string StationId,
    long PlcEpoch,
    ulong? CycleId,
    string AnomalyType,
    string Severity,
    Guid? ExistingDetectionId,
    byte[]? ExistingFingerprint,
    byte[] IncomingFingerprint,
    string IncomingRecordJson,
    DateTime DetectedAt,
    long ConfigRevisionId);

/// <summary>dbo.SequenceAnomaly: colisiones, reinicios sospechados, épocas inesperadas, disminuciones ambiguas.</summary>
public static class AnomalyWriter
{
    public static async Task<long> InsertAsync(SqlConnection conn, SqlTransaction tx, AnomalyRow a, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            DECLARE @ids TABLE (AnomalyId BIGINT NOT NULL);
            INSERT INTO dbo.SequenceAnomaly (SiteId, StationId, PlcEpoch, CycleId, AnomalyType, Severity, ExistingDetectionId,
                ExistingFingerprint, IncomingFingerprint, IncomingRecord, DetectedAt, ConfigRevisionId)
            OUTPUT inserted.AnomalyId INTO @ids (AnomalyId)
            VALUES (@site, @station, @epoch, @cycle, @type, @severity, @existing, @existingFp, @incomingFp, @record, @at, @rev);
            SELECT AnomalyId FROM @ids;
            """);
        cmd.NVarChar("@site", a.SiteId, 64).NVarChar("@station", a.StationId, 64).BigInt("@epoch", a.PlcEpoch).Cycle("@cycle", a.CycleId)
            .VarChar("@type", a.AnomalyType, 30).VarChar("@severity", a.Severity, 10).Guid("@existing", a.ExistingDetectionId)
            .Binary32("@existingFp", a.ExistingFingerprint).Binary32("@incomingFp", a.IncomingFingerprint).NVarCharMax("@record", a.IncomingRecordJson)
            .DateTime2("@at", a.DetectedAt).BigInt("@rev", a.ConfigRevisionId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>dbo.SequenceGap: rangos de ciclos sin datos. Se cuentan, nunca se inventan detecciones.</summary>
public static class GapWriter
{
    /// <summary>
    /// G01. Un hueco no se solapa con otro de la misma época: se valida con UPDLOCK, HOLDLOCK. Un
    /// solapamiento o una violación de UQ_SequenceGap_Start es un defecto y se propaga.
    /// </summary>
    public static async Task<long> OpenAsync(
        SqlConnection conn, SqlTransaction tx, string siteId, string stationId, long epoch, GapOpen gap, DateTime edgeNowUtc, long configRevisionId,
        CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            IF EXISTS (
                SELECT 1 FROM dbo.SequenceGap WITH (UPDLOCK, HOLDLOCK)
                WHERE SiteId = @site AND StationId = @station AND PlcEpoch = @epoch
                  AND FromCycleId <= @to AND ToCycleId >= @from)
                THROW 50101, N'SequenceGap solapado con otro de la misma época.', 1;
            DECLARE @ids TABLE (GapId BIGINT NOT NULL);
            INSERT INTO dbo.SequenceGap (SiteId, StationId, PlcEpoch, FromCycleId, ToCycleId, DetectedAt, GraceUntil, ConfigRevisionId, Status)
            OUTPUT inserted.GapId INTO @ids (GapId)
            VALUES (@site, @station, @epoch, @from, @to, @now, @grace, @rev, 'OPEN');
            SELECT GapId FROM @ids;
            """);
        cmd.NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64).BigInt("@epoch", epoch).Cycle("@from", gap.FromCycleId)
            .Cycle("@to", gap.ToCycleId).DateTime2("@now", edgeNowUtc).DateTime2("@grace", gap.GraceUntil).BigInt("@rev", configRevisionId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>G02: los huecos abiertos que contienen el ciclo y ya tienen todas sus detecciones.</summary>
    public static async Task<IReadOnlyList<long>> RecoverCompletedAsync(
        SqlConnection conn, SqlTransaction tx, string siteId, string stationId, long epoch, ulong cycleId, DateTime edgeNowUtc, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            DECLARE @recovered TABLE (GapId BIGINT NOT NULL);
            UPDATE g
            SET Status = 'RECOVERED', ClosedAt = @now, UnrecoveredCount = 0
            OUTPUT inserted.GapId INTO @recovered (GapId)
            FROM dbo.SequenceGap AS g WITH (UPDLOCK, ROWLOCK)
            WHERE g.SiteId = @site AND g.StationId = @station AND g.PlcEpoch = @epoch AND g.Status = 'OPEN'
              AND g.FromCycleId <= @cycle AND g.ToCycleId >= @cycle
              AND (SELECT COUNT_BIG(*) FROM dbo.CartonDetection AS cd
                   WHERE cd.SiteId = g.SiteId AND cd.StationId = g.StationId AND cd.PlcEpoch = g.PlcEpoch
                     AND cd.CycleId BETWEEN g.FromCycleId AND g.ToCycleId) = g.ToCycleId - g.FromCycleId + 1;
            SELECT GapId FROM @recovered;
            """);
        cmd.DateTime2("@now", edgeNowUtc).NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64).BigInt("@epoch", epoch)
            .Cycle("@cycle", cycleId);
        var ids = new List<long>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            ids.Add(r.GetInt64(0));
        }

        return ids;
    }
}
