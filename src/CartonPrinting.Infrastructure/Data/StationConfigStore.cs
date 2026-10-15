using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Una revisión inmutable de configuración de estación (dbo.StationConfigRevision).</summary>
public sealed record StationRevision
{
    public long ConfigRevisionId { get; init; }
    public required string SiteId { get; init; }
    public required string StationId { get; init; }
    public required int RevisionNumber { get; init; }
    public required string PlcEndpointId { get; init; }
    public required string PrinterId { get; init; }
    public string? ManualPrinterId { get; init; }
    public required string LabelType { get; init; }
    public required string TemplateRevision { get; init; }
    public required string DeadlineReference { get; init; }
    public required int TransitBudgetMs { get; init; }
    public required int DeviceLeadTimeMs { get; init; }
    public required int SendMarginMs { get; init; }
    public int? ClockSkewToleranceMs { get; init; }
    public int? MaxUpstreamLatencyMs { get; init; }
    public required int ClockJumpToleranceMs { get; init; }
    public int? MinCycleIntervalMs { get; init; }
    public required byte MaxAutoAttempts { get; init; }
    public required int RetryBackoffMs { get; init; }
    public required string MinConfirmationLevel { get; init; }
    public int? EvidenceWindowMs { get; init; }
    public required string EpochChangePolicy { get; init; }
    public required string CartonIdentityMode { get; init; }
    public required int GapGraceMs { get; init; }
    public required string PhysicalGateMode { get; init; }
    public int? GateFreshnessMs { get; init; }
    public required string CreatedBy { get; init; }
    public required string ApprovedBy { get; init; }
    public required string ChangeReason { get; init; }

    public bool RequiresCartonId => CartonIdentityMode == "CARTON_ID_REQUIRED";

    public bool UsesWindowGate => PhysicalGateMode == "PLC_WINDOW_TAG";
}

/// <summary>Estado operativo de la estación (dbo.StationConfig): no es configuración.</summary>
public sealed record StationState(
    string SiteId,
    string StationId,
    long ActiveConfigRevisionId,
    bool LabelingEnabled,
    string? BlockReason,
    DateTime? BlockedAt,
    string? BlockedBy,
    long? BlockedByAnomalyId,
    long? LastOperatorActionId);

/// <summary>
/// Revisiones de configuración y estado de la estación. Activar una configuración es insertar una
/// revisión nueva y mover el puntero de StationConfig en la misma transacción; las revisiones no
/// se editan (TR_StationConfigRevision_Immutable) y exigen dos personas (CHECK).
/// </summary>
public sealed class StationConfigStore(SqlDb db)
{
    private const string RevisionColumns = """
        ConfigRevisionId, SiteId, StationId, RevisionNumber, PlcEndpointId, PrinterId, ManualPrinterId, LabelType,
        TemplateRevision, DeadlineReference, TransitBudgetMs, DeviceLeadTimeMs, SendMarginMs, ClockSkewToleranceMs,
        MaxUpstreamLatencyMs, ClockJumpToleranceMs, MinCycleIntervalMs, MaxAutoAttempts, RetryBackoffMs,
        MinConfirmationLevel, EvidenceWindowMs, EpochChangePolicy, CartonIdentityMode, GapGraceMs, PhysicalGateMode,
        GateFreshnessMs, CreatedBy, ApprovedBy, ChangeReason
        """;

    /// <summary>Inserta una revisión y la activa (crea StationConfig habilitada si no existía).</summary>
    public Task<StationRevision> ActivateRevisionAsync(StationRevision revision, CancellationToken ct) =>
        db.InTransactionAsync(async (conn, tx) =>
        {
            var inserted = await InsertRevisionAsync(conn, tx, revision, ct);
            await using var cmd = conn.Command(tx, """
                DECLARE @moved TABLE (SiteId NVARCHAR(64) NOT NULL);
                UPDATE dbo.StationConfig SET ActiveConfigRevisionId = @rev
                OUTPUT inserted.SiteId INTO @moved (SiteId)
                WHERE SiteId = @site AND StationId = @station;
                IF NOT EXISTS (SELECT 1 FROM @moved)
                    INSERT INTO dbo.StationConfig (SiteId, StationId, ActiveConfigRevisionId, LabelingEnabled)
                    VALUES (@site, @station, @rev, 1);
                """);
            cmd.BigInt("@rev", inserted.ConfigRevisionId).NVarChar("@site", revision.SiteId, 64).NVarChar("@station", revision.StationId, 64);
            await cmd.ExecuteNonQueryAsync(ct);
            return inserted;
        }, ct);

    public static async Task<StationRevision> InsertRevisionAsync(SqlConnection conn, SqlTransaction tx, StationRevision r, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            DECLARE @ids TABLE (ConfigRevisionId BIGINT NOT NULL);
            INSERT INTO dbo.StationConfigRevision (
                SiteId, StationId, RevisionNumber, PlcEndpointId, PrinterId, ManualPrinterId, LabelType, TemplateRevision,
                DeadlineReference, TransitBudgetMs, DeviceLeadTimeMs, SendMarginMs, ClockSkewToleranceMs, MaxUpstreamLatencyMs,
                ClockJumpToleranceMs, MinCycleIntervalMs, MaxAutoAttempts, RetryBackoffMs, MinConfirmationLevel, EvidenceWindowMs,
                EpochChangePolicy, CartonIdentityMode, GapGraceMs, PhysicalGateMode, GateFreshnessMs, CreatedBy, ApprovedBy, ChangeReason)
            OUTPUT inserted.ConfigRevisionId INTO @ids (ConfigRevisionId)
            VALUES (
                @SiteId, @StationId, @RevisionNumber, @PlcEndpointId, @PrinterId, @ManualPrinterId, @LabelType, @TemplateRevision,
                @DeadlineReference, @TransitBudgetMs, @DeviceLeadTimeMs, @SendMarginMs, @ClockSkewToleranceMs, @MaxUpstreamLatencyMs,
                @ClockJumpToleranceMs, @MinCycleIntervalMs, @MaxAutoAttempts, @RetryBackoffMs, @MinConfirmationLevel, @EvidenceWindowMs,
                @EpochChangePolicy, @CartonIdentityMode, @GapGraceMs, @PhysicalGateMode, @GateFreshnessMs, @CreatedBy, @ApprovedBy, @ChangeReason);
            SELECT ConfigRevisionId FROM @ids;
            """);
        cmd.NVarChar("@SiteId", r.SiteId, 64).NVarChar("@StationId", r.StationId, 64).Int("@RevisionNumber", r.RevisionNumber)
            .NVarChar("@PlcEndpointId", r.PlcEndpointId, 128).NVarChar("@PrinterId", r.PrinterId, 64)
            .NVarChar("@ManualPrinterId", r.ManualPrinterId, 64).NVarChar("@LabelType", r.LabelType, 64)
            .NVarChar("@TemplateRevision", r.TemplateRevision, 64).VarChar("@DeadlineReference", r.DeadlineReference, 20)
            .Int("@TransitBudgetMs", r.TransitBudgetMs).Int("@DeviceLeadTimeMs", r.DeviceLeadTimeMs).Int("@SendMarginMs", r.SendMarginMs)
            .Int("@ClockSkewToleranceMs", r.ClockSkewToleranceMs).Int("@MaxUpstreamLatencyMs", r.MaxUpstreamLatencyMs)
            .Int("@ClockJumpToleranceMs", r.ClockJumpToleranceMs).Int("@MinCycleIntervalMs", r.MinCycleIntervalMs)
            .TinyInt("@MaxAutoAttempts", r.MaxAutoAttempts).Int("@RetryBackoffMs", r.RetryBackoffMs)
            .VarChar("@MinConfirmationLevel", r.MinConfirmationLevel, 10).Int("@EvidenceWindowMs", r.EvidenceWindowMs)
            .VarChar("@EpochChangePolicy", r.EpochChangePolicy, 30).VarChar("@CartonIdentityMode", r.CartonIdentityMode, 30)
            .Int("@GapGraceMs", r.GapGraceMs).VarChar("@PhysicalGateMode", r.PhysicalGateMode, 20).Int("@GateFreshnessMs", r.GateFreshnessMs)
            .NVarChar("@CreatedBy", r.CreatedBy, 128).NVarChar("@ApprovedBy", r.ApprovedBy, 128).NVarChar("@ChangeReason", r.ChangeReason, 512);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return r with { ConfigRevisionId = id };
    }

    public Task<StationRevision> GetRevisionAsync(long configRevisionId, CancellationToken ct) =>
        db.ReadAsync(async conn =>
        {
            await using var cmd = conn.Command(null, $"SELECT {RevisionColumns} FROM dbo.StationConfigRevision WHERE ConfigRevisionId = @id");
            cmd.BigInt("@id", configRevisionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? ReadRevision(reader)
                : throw new InvalidOperationException($"StationConfigRevision {configRevisionId} no existe.");
        }, ct);

    /// <summary>Estado operativo y revisión activa de la estación.</summary>
    public Task<(StationState State, StationRevision Active)?> GetActiveAsync(string siteId, string stationId, CancellationToken ct) =>
        db.ReadAsync(conn => GetActiveAsync(conn, null, siteId, stationId, ct), ct);

    public static async Task<(StationState State, StationRevision Active)?> GetActiveAsync(
        SqlConnection conn, SqlTransaction? tx, string siteId, string stationId, CancellationToken ct, bool updLock = false)
    {
        var hint = updLock ? "WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
        await using var cmd = conn.Command(tx, $"""
            SELECT sc.SiteId, sc.StationId, sc.ActiveConfigRevisionId, sc.LabelingEnabled, sc.BlockReason, sc.BlockedAt, sc.BlockedBy,
                   sc.BlockedByAnomalyId, sc.LastOperatorActionId,
                   {string.Join(", ", RevisionColumns.Split(',').Select(c => "r." + c.Trim()))}
            FROM dbo.StationConfig AS sc {hint}
            INNER JOIN dbo.StationConfigRevision AS r ON r.ConfigRevisionId = sc.ActiveConfigRevisionId
            WHERE sc.SiteId = @site AND sc.StationId = @station
            """);
        cmd.NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var state = new StationState(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3), reader.GetNullableString(4),
            reader.GetNullableUtc(5), reader.GetNullableString(6), reader.GetNullableInt64(7), reader.GetNullableInt64(8));
        return (state, ReadRevision(reader, 9));
    }

    /// <summary>
    /// Bloquea la estación (LabelingEnabled = 0). Bloquear no necesita OperatorAction; desbloquear
    /// sí (TX-M, TR_StationConfig_Guard). Si ya estaba bloqueada, conserva el primer motivo.
    /// </summary>
    public static async Task<bool> BlockAsync(
        SqlConnection conn, SqlTransaction tx, string siteId, string stationId, string reason, string blockedBy, long? anomalyId,
        DateTime edgeNowUtc, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            DECLARE @blocked TABLE (SiteId NVARCHAR(64) NOT NULL);
            UPDATE dbo.StationConfig
            SET LabelingEnabled = 0, BlockReason = @reason, BlockedAt = @now, BlockedBy = @by, BlockedByAnomalyId = @anomaly
            OUTPUT inserted.SiteId INTO @blocked (SiteId)
            WHERE SiteId = @site AND StationId = @station AND LabelingEnabled = 1;
            SELECT COUNT(*) FROM @blocked;
            """);
        cmd.VarChar("@reason", reason, 30).DateTime2("@now", edgeNowUtc).NVarChar("@by", blockedBy, 128).BigInt("@anomaly", anomalyId)
            .NVarChar("@site", siteId, 64).NVarChar("@station", stationId, 64);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    public Task<bool> BlockAsync(string siteId, string stationId, string reason, string blockedBy, long? anomalyId, DateTime edgeNowUtc, CancellationToken ct) =>
        db.InTransactionAsync((conn, tx) => BlockAsync(conn, tx, siteId, stationId, reason, blockedBy, anomalyId, edgeNowUtc, ct), ct);

    private static StationRevision ReadRevision(SqlDataReader r, int o = 0) => new()
    {
        ConfigRevisionId = r.GetInt64(o),
        SiteId = r.GetString(o + 1),
        StationId = r.GetString(o + 2),
        RevisionNumber = r.GetInt32(o + 3),
        PlcEndpointId = r.GetString(o + 4),
        PrinterId = r.GetString(o + 5),
        ManualPrinterId = r.GetNullableString(o + 6),
        LabelType = r.GetString(o + 7),
        TemplateRevision = r.GetString(o + 8),
        DeadlineReference = r.GetString(o + 9),
        TransitBudgetMs = r.GetInt32(o + 10),
        DeviceLeadTimeMs = r.GetInt32(o + 11),
        SendMarginMs = r.GetInt32(o + 12),
        ClockSkewToleranceMs = r.GetNullableInt32(o + 13),
        MaxUpstreamLatencyMs = r.GetNullableInt32(o + 14),
        ClockJumpToleranceMs = r.GetInt32(o + 15),
        MinCycleIntervalMs = r.GetNullableInt32(o + 16),
        MaxAutoAttempts = r.GetByte(o + 17),
        RetryBackoffMs = r.GetInt32(o + 18),
        MinConfirmationLevel = r.GetString(o + 19),
        EvidenceWindowMs = r.GetNullableInt32(o + 20),
        EpochChangePolicy = r.GetString(o + 21),
        CartonIdentityMode = r.GetString(o + 22),
        GapGraceMs = r.GetInt32(o + 23),
        PhysicalGateMode = r.GetString(o + 24),
        GateFreshnessMs = r.GetNullableInt32(o + 25),
        CreatedBy = r.GetString(o + 26),
        ApprovedBy = r.GetString(o + 27),
        ChangeReason = r.GetString(o + 28),
    };
}
