using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Marca alta de una época aceptada: el mayor CycleId registrado y su DetectionTimestamp latched.</summary>
public sealed record EpochMark(long Epoch, DateTime AcceptedAt, ulong? HighWater, DateTime? HighWaterTimestamp);

/// <summary>Anomalía con su registro completo retenido para un reingreso posterior.</summary>
public sealed record RetainedAnomaly(long AnomalyId, string AnomalyType, long PlcEpoch, ulong? CycleId, string IncomingRecord, DateTime DetectedAt);

/// <summary>
/// Estado de secuencia de una estación en SQL: épocas con su marca alta (para reconstruir el estado de la
/// ingesta al arrancar) y anomalías sin detección (EPOCH_UNEXPECTED) con el registro retenido.
/// </summary>
public sealed class SequenceStore(SqlDb db)
{
    /// <summary>Épocas en orden de AcceptedAt con el mayor CycleId registrado de cada una y su DetectionTimestamp.</summary>
    public Task<IReadOnlyList<EpochMark>> LoadEpochsAsync(string siteId, string stationId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<EpochMark>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT e.PlcEpoch, e.AcceptedAt, hw.CycleId, hw.DetectionTimestamp
                FROM dbo.PlcEpochHistory AS e
                OUTER APPLY (
                    SELECT TOP (1) cd.CycleId, cd.DetectionTimestamp
                    FROM dbo.CartonDetection AS cd
                    WHERE cd.SiteId = e.SiteId AND cd.StationId = e.StationId AND cd.PlcEpoch = e.PlcEpoch
                    ORDER BY cd.CycleId DESC) AS hw
                WHERE e.SiteId = @site AND e.StationId = @station
                ORDER BY e.AcceptedAt;
                """);
            cmd.NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64);
            var list = new List<EpochMark>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new EpochMark(r.GetInt64(0), r.GetUtc(1), r.IsDBNull(2) ? null : r.GetCycleId(2), r.IsDBNull(3) ? null : r.GetUtc(3)));
            }

            return list;
        }, ct);

    /// <summary>
    /// EPOCH_UNEXPECTED: anomalía BLOCKING sin detección, con el registro completo retenido, y estación
    /// bloqueada, en una sola transacción. Nunca se crea una detección de una época no aceptada.
    /// </summary>
    public Task<long> RetainAsync(AnomalyRow anomaly, string blockReason, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var id = await AnomalyWriter.InsertAsync(conn, tx, anomaly, ct);
            await StationConfigStore.BlockAsync(conn, tx, anomaly.SiteId, anomaly.StationId, blockReason, "SYSTEM", id, anomaly.DetectedAt, ct);
            return id;
        }, ct);

    /// <summary>Anomalías EPOCH_UNEXPECTED sin resolver (registros retenidos), en orden de llegada.</summary>
    public Task<IReadOnlyList<RetainedAnomaly>> ListRetainedAsync(string siteId, string stationId, CancellationToken ct) =>
        db.ReadAsync<IReadOnlyList<RetainedAnomaly>>(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT AnomalyId, AnomalyType, PlcEpoch, CycleId, IncomingRecord, DetectedAt
                FROM dbo.SequenceAnomaly
                WHERE SiteId = @site AND StationId = @station AND AnomalyType = 'EPOCH_UNEXPECTED' AND ResolvedAt IS NULL
                ORDER BY AnomalyId;
                """);
            cmd.NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64);
            var list = new List<RetainedAnomaly>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new RetainedAnomaly(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetCycleId(3), r.GetString(4), r.GetUtc(5)));
            }

            return list;
        }, ct);

    /// <summary>Huecos OPEN de la estación (indicador sequence_gaps_open).</summary>
    public Task<int> CountOpenGapsAsync(string siteId, string stationId, CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, """
                SELECT COUNT(*) FROM dbo.SequenceGap WHERE SiteId = @site AND StationId = @station AND Status = 'OPEN';
                """);
            cmd.NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64);
            return (int)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);

    /// <summary>true si la violación es de PK_PlcEpochHistory (la época ya la aceptó otro proceso o un reintento).</summary>
    public static bool IsEpochAlreadyAccepted(SqlException ex) =>
        ex.Number is 2627 or 2601 && ex.Message.Contains("PK_PlcEpochHistory", StringComparison.Ordinal);
}
