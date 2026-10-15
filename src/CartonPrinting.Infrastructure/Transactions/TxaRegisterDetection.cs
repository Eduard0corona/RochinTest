using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Transactions;

/// <summary>Datos de la detección tal como se insertan (Status = RECEIVED).</summary>
public sealed record DetectionInsert
{
    public required string SiteId { get; init; }
    public required string StationId { get; init; }
    public required long PlcEpoch { get; init; }
    public required ulong CycleId { get; init; }
    public required byte[] SourceFingerprint { get; init; }
    public required string Transport { get; init; }
    public required bool QueueOverflow { get; init; }
    public DateTime? DetectionTimestamp { get; init; }
    public DateTime? OpcSourceTimestamp { get; init; }
    public DateTime? OpcServerTimestamp { get; init; }
    public required DateTime ReceivedAt { get; init; }
    public required string DeadlineReference { get; init; }
    public required DateTime DeadlineAt { get; init; }
    public required long ConfigRevisionId { get; init; }
    public string? WorkOrderId { get; init; }
    public string? CartonId { get; init; }
    public required string OpcNodeId { get; init; }

    public CycleIdentity Identity => new(SiteId, StationId, PlcEpoch, CycleId);
    public string IdempotencyKey => Identity.IdempotencyKey;
    public Guid DetectionId => Identity.DetectionId;
    public string Origin => CartonPrinting.Contracts.Transport.OriginOf(Transport);

    public CartonDetectedEvent ToEvent() => new()
    {
        EventId = DetectionId,
        CorrelationId = DetectionId,
        OccurredAt = DetectionTimestamp ?? ReceivedAt,
        SiteId = SiteId,
        StationId = StationId,
        PlcEpoch = PlcEpoch,
        CycleId = CycleId,
        IdempotencyKey = IdempotencyKey,
        DetectionOrigin = Origin,
        Transport = Transport,
        QueueOverflow = QueueOverflow,
        DetectionTimestamp = DetectionTimestamp,
        ReceivedAt = ReceivedAt,
        DeadlineReference = DeadlineReference,
        DeadlineAt = DeadlineAt,
        ConfigRevision = ConfigRevisionId,
        WorkOrderId = WorkOrderId,
        CartonId = CartonId,
        SourceFingerprint = Convert.ToHexStringLower(SourceFingerprint),
        Opc = new CartonDetectedEvent.OpcInfo
        {
            NodeId = OpcNodeId,
            SourceTimestamp = OpcSourceTimestamp,
            ServerTimestamp = OpcServerTimestamp,
        },
    };
}

/// <summary>Job ya renderizado antes de TX-A: el serial se obtuvo fuera de la transacción.</summary>
public sealed record JobInsert(
    Guid PrintJobId,
    string LabelType,
    string PrinterId,
    string WorkOrderId,
    string ErpRevision,
    string ProductId,
    string LotId,
    string LabelSerial,
    string TemplateRevision,
    string Payload,
    byte[] PayloadHash,
    DateTime DeadlineAt);

/// <summary>Anomalía que acompaña a la detección BLOCKED con SEQUENCE_ANOMALY en la misma transacción.</summary>
public sealed record AnomalyInsert(string AnomalyType, string Severity, string IncomingRecordJson, bool BlockStation, string? BlockReason);

/// <summary>Hueco que abre este registro (G01): rango sin detección entre la marca alta y el ciclo.</summary>
public sealed record GapOpen(ulong FromCycleId, ulong ToCycleId, DateTime GraceUntil);

/// <summary>
/// Plan de TX-A preparado fuera de la transacción: identidad, huella, plazo, revisión capturada y,
/// si hay snapshot ERP, el job ya renderizado con su serial.
/// </summary>
public sealed record RegistrationPlan
{
    public required DetectionInsert Detection { get; init; }

    /// <summary>Revisión activa capturada por la detección.</summary>
    public required StationRevision Revision { get; init; }

    /// <summary>Clasificación terminal decidida antes (BLOCKED o EXPIRED con su motivo), o null si es candidata.</summary>
    public string? TerminalStatus { get; init; }

    public string? TerminalReason { get; init; }

    /// <summary>Job renderizado con el snapshot ERP vigente, o null si no hay snapshot (D03).</summary>
    public JobInsert? Job { get; init; }

    public AnomalyInsert? Anomaly { get; init; }

    public GapOpen? Gap { get; init; }

    public required DateTime EdgeNowUtc { get; init; }

    /// <summary>Margen efectivo (el mayor entre el congelado y el activo).</summary>
    public required int EffectiveSendMarginMs { get; init; }
}

public enum RegistrationOutcome
{
    /// <summary>Clave nueva: la detección quedó registrada con su estado final.</summary>
    Registered,

    /// <summary>Clave existente con la misma huella: sin efecto.</summary>
    Duplicate,

    /// <summary>Clave existente con otra huella: IDENTITY_COLLISION y estación bloqueada.</summary>
    Collision,
}

public sealed record RegistrationResult(
    RegistrationOutcome Outcome,
    Guid DetectionId,
    string? FinalStatus,
    string? FinalReason,
    Guid? PrintJobId,
    long? AnomalyId,
    long? GapId,
    IReadOnlyList<long> RecoveredGapIds);

/// <summary>
/// TX-A: registro de la detección y creación del job en una transacción por ciclo (D01 a D05, J01,
/// G01, G02). Inserción primero: solo una violación de UQ_CartonDetection_Identity,
/// UQ_CartonDetection_IdempotencyKey o PK_CartonDetection es "clave existente" y se clasifica en
/// otra transacción (duplicado auténtico o colisión). Un 1205 reintenta TX-A completa.
/// </summary>
public sealed class TxaRegisterDetection(SqlDb db)
{
    private static readonly string[] LiveJobStatuses =
        ["READY", "SENDING", "TRANSMITTED", "RETRY_WAIT", "CONFIRMED", "UNKNOWN", "CLOSED_UNRESOLVED"];

    public async Task<RegistrationResult> ExecuteAsync(RegistrationPlan plan, CancellationToken ct)
    {
        try
        {
            return await db.InTransactionAsync((conn, tx) => RegisterAsync(conn, tx, plan, ct), ct);
        }
        catch (SqlException ex) when (SqlDb.IsDetectionIdentityViolation(ex))
        {
            return await ClassifyExistingKeyAsync(plan, ct);
        }
    }

    private static async Task<RegistrationResult> RegisterAsync(SqlConnection conn, SqlTransaction tx, RegistrationPlan plan, CancellationToken ct)
    {
        var d = plan.Detection;

        // 1. INSERT primero (D01).
        await using (var insert = conn.Command(tx, """
            INSERT INTO dbo.CartonDetection (
                DetectionId, SiteId, StationId, PlcEpoch, CycleId, IdempotencyKey, SourceFingerprint,
                Origin, Transport, QueueOverflow, DetectionTimestamp, OpcSourceTimestamp, ReceivedAt,
                DeadlineReference, DeadlineAt, ConfigRevisionId, WorkOrderId, CartonId, Status, Reason)
            VALUES (
                @DetectionId, @SiteId, @StationId, @PlcEpoch, @CycleId, @IdempotencyKey, @SourceFingerprint,
                @Origin, @Transport, @QueueOverflow, @DetectionTimestamp, @OpcSourceTimestamp, @ReceivedAt,
                @DeadlineReference, @DeadlineAt, @ConfigRevisionId, @WorkOrderId, @CartonId, 'RECEIVED', NULL);
            """))
        {
            insert.Guid("@DetectionId", d.DetectionId).NVarChar("@SiteId", d.SiteId, 64).NVarChar("@StationId", d.StationId, 64)
                .BigInt("@PlcEpoch", d.PlcEpoch).Cycle("@CycleId", d.CycleId).NVarChar("@IdempotencyKey", d.IdempotencyKey, 220)
                .Binary32("@SourceFingerprint", d.SourceFingerprint).VarChar("@Origin", d.Origin, 12).VarChar("@Transport", d.Transport, 16)
                .Bit("@QueueOverflow", d.QueueOverflow).DateTime2("@DetectionTimestamp", d.DetectionTimestamp)
                .DateTime2("@OpcSourceTimestamp", d.OpcSourceTimestamp).DateTime2("@ReceivedAt", d.ReceivedAt)
                .VarChar("@DeadlineReference", d.DeadlineReference, 20).DateTime2("@DeadlineAt", d.DeadlineAt)
                .BigInt("@ConfigRevisionId", d.ConfigRevisionId).NVarChar("@WorkOrderId", d.WorkOrderId, 128).NVarChar("@CartonId", d.CartonId, 128);
            await insert.ExecuteNonQueryAsync(ct);
        }

        // 2. Clasificación dentro de la transacción.
        var (status, reason) = (plan.TerminalStatus, plan.TerminalReason);
        Guid? jobId = null;
        if (status is null)
        {
            (status, reason, jobId) = await ClassifyCandidateAsync(conn, tx, plan, ct);
        }

        // 3. Anomalía de secuencia ligada a la detección (y bloqueo de la estación si corresponde).
        long? anomalyId = null;
        if (plan.Anomaly is { } anomaly)
        {
            anomalyId = await AnomalyWriter.InsertAsync(conn, tx, new AnomalyRow(
                d.SiteId, d.StationId, d.PlcEpoch, d.CycleId, anomaly.AnomalyType, anomaly.Severity, null, null,
                d.SourceFingerprint, anomaly.IncomingRecordJson, plan.EdgeNowUtc, d.ConfigRevisionId), ct);
            if (anomaly.BlockStation)
            {
                await StationConfigStore.BlockAsync(conn, tx, d.SiteId, d.StationId, anomaly.BlockReason ?? anomaly.AnomalyType, "SYSTEM",
                    anomalyId, plan.EdgeNowUtc, ct);
            }
        }

        // 4. Estado final de la detección.
        await using (var final = conn.Command(tx, """
            DECLARE @done TABLE (DetectionId UNIQUEIDENTIFIER NOT NULL);
            UPDATE dbo.CartonDetection
            SET Status = @FinalDetectionStatus, Reason = @FinalDetectionReason, UpdatedAt = @EdgeNowUtc
            OUTPUT inserted.DetectionId INTO @done (DetectionId)
            WHERE DetectionId = @DetectionId AND Status = 'RECEIVED';
            SELECT COUNT(*) FROM @done;
            """))
        {
            final.VarChar("@FinalDetectionStatus", status, 20).VarChar("@FinalDetectionReason", reason, 40)
                .DateTime2("@EdgeNowUtc", plan.EdgeNowUtc).Guid("@DetectionId", d.DetectionId);
            if ((int)(await final.ExecuteScalarAsync(ct))! != 1)
            {
                throw new InvalidOperationException($"La detección {d.IdempotencyKey} no estaba en RECEIVED dentro de su propia TX-A.");
            }
        }

        // 5. Huecos de secuencia (G01 y G02).
        long? gapId = null;
        if (plan.Gap is { } gap)
        {
            gapId = await GapWriter.OpenAsync(conn, tx, d.SiteId, d.StationId, d.PlcEpoch, gap, plan.EdgeNowUtc, d.ConfigRevisionId, ct);
        }

        var recovered = await GapWriter.RecoverCompletedAsync(conn, tx, d.SiteId, d.StationId, d.PlcEpoch, d.CycleId, plan.EdgeNowUtc, ct);

        // 6. Outbox: CartonDetected y, si es terminal sin job, PrintJobResult.
        await OutboxWriter.InsertCartonDetectedAsync(conn, tx, d.ToEvent(), opensGapWithoutDeadline: plan.Gap is not null, ct);
        if (status is DetectionStatus.Blocked or DetectionStatus.Expired)
        {
            var record = await DetectionQueries.GetAsync(conn, tx, d.DetectionId, ct)
                ?? throw new InvalidOperationException("La detección recién insertada no existe.");
            await ResultPublisher.PublishDetectionAsync(conn, tx, record, plan.Revision.LabelType, plan.EdgeNowUtc, ct);
        }

        return new RegistrationResult(RegistrationOutcome.Registered, d.DetectionId, status, reason, jobId, anomalyId, gapId, recovered);
    }

    /// <summary>D02, D03, D04 (plazo vencido al registrar) y D05 (estación o recirculación).</summary>
    private static async Task<(string Status, string? Reason, Guid? JobId)> ClassifyCandidateAsync(
        SqlConnection conn, SqlTransaction tx, RegistrationPlan plan, CancellationToken ct)
    {
        var d = plan.Detection;

        // Plazo efectivo vigente con margen, evaluado con el reloj del EdgeHost.
        if (plan.EdgeNowUtc >= d.DeadlineAt.AddMilliseconds(-plan.EffectiveSendMarginMs))
        {
            return (DetectionStatus.Expired, DetectionReason.LateArrival, null);
        }

        var station = await StationConfigStore.GetActiveAsync(conn, tx, d.SiteId, d.StationId, ct)
            ?? throw new InvalidOperationException($"La estación {d.SiteId}/{d.StationId} no tiene StationConfig.");
        if (!station.State.LabelingEnabled)
        {
            return (DetectionStatus.Blocked, DetectionReason.StationDisabled, null);
        }

        if (plan.Revision.RequiresCartonId && d.CartonId is not null
            && await IsRecirculatedAsync(conn, tx, d.SiteId, d.CartonId, d.DetectionId, ct))
        {
            return (DetectionStatus.Blocked, DetectionReason.RecirculatedCarton, null);
        }

        if (plan.Job is { } job
            && await ErpCacheStore.IsRevisionValidAsync(conn, tx, job.WorkOrderId, job.ErpRevision, plan.EdgeNowUtc, ct))
        {
            await InsertJobAsync(conn, tx, d.Identity, d.CartonId, d.ConfigRevisionId, job, ct);
            return (DetectionStatus.JobCreated, null, job.PrintJobId);
        }

        return (DetectionStatus.WaitingErp, null, null);
    }

    /// <summary>
    /// Recirculación (H8, CARTON_ID_REQUIRED): bloqueo de rango sobre IX_PrintJob_Carton e
    /// IX_CartonDetection_Carton con UPDLOCK, HOLDLOCK. La comparten TX-A y TX-E.
    /// </summary>
    public static async Task<bool> IsRecirculatedAsync(SqlConnection conn, SqlTransaction tx, string siteId, string cartonId, Guid detectionId, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, $"""
            SELECT TOP (1) pj.PrintJobId
            FROM dbo.PrintJob AS pj WITH (UPDLOCK, HOLDLOCK)
            WHERE pj.SiteId = @SiteId
              AND pj.CartonId = @CartonId
              AND pj.Status IN ({string.Join(", ", LiveJobStatuses.Select(s => $"'{s}'"))});

            SELECT TOP (1) cd.DetectionId
            FROM dbo.CartonDetection AS cd WITH (UPDLOCK, HOLDLOCK)
            WHERE cd.SiteId = @SiteId
              AND cd.CartonId = @CartonId
              AND cd.Status = 'WAITING_ERP'
              AND cd.DetectionId <> @DetectionId;
            """);
        cmd.NVarChar("@SiteId", siteId, 64).NVarChar("@CartonId", cartonId, 128).Guid("@DetectionId", detectionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liveJob = await r.ReadAsync(ct);
        await r.NextResultAsync(ct);
        var waiting = await r.ReadAsync(ct);
        return liveJob || waiting;
    }

    /// <summary>J01: INSERT PrintJob READY con todo lo congelado (lo comparten TX-A y TX-E).</summary>
    public static async Task InsertJobAsync(
        SqlConnection conn, SqlTransaction tx, CycleIdentity cycle, string? cartonId, long configRevisionId, JobInsert job, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, """
            INSERT INTO dbo.PrintJob (
                PrintJobId, DetectionId, SiteId, StationId, PlcEpoch, CycleId, LabelType, ConfigRevisionId,
                PrinterId, WorkOrderId, ErpRevision, ProductId, LotId, CartonId, LabelSerial, TemplateRevision,
                Payload, PayloadHash, DeadlineAt, Status)
            VALUES (
                @PrintJobId, @DetectionId, @SiteId, @StationId, @PlcEpoch, @CycleId, @LabelType, @ConfigRevisionId,
                @PrinterId, @WorkOrderId, @ErpRevision, @ProductId, @LotId, @CartonId, @LabelSerial, @TemplateRevision,
                @Payload, @PayloadHash, @DeadlineAt, 'READY');
            """);
        cmd.Guid("@PrintJobId", job.PrintJobId).Guid("@DetectionId", cycle.DetectionId).NVarChar("@SiteId", cycle.SiteId, 64)
            .NVarChar("@StationId", cycle.StationId, 64).BigInt("@PlcEpoch", cycle.PlcEpoch).Cycle("@CycleId", cycle.CycleId)
            .NVarChar("@LabelType", job.LabelType, 64).BigInt("@ConfigRevisionId", configRevisionId).NVarChar("@PrinterId", job.PrinterId, 64)
            .NVarChar("@WorkOrderId", job.WorkOrderId, 128).NVarChar("@ErpRevision", job.ErpRevision, 64).NVarChar("@ProductId", job.ProductId, 128)
            .NVarChar("@LotId", job.LotId, 128).NVarChar("@CartonId", cartonId, 128).VarChar("@LabelSerial", job.LabelSerial, 40)
            .NVarChar("@TemplateRevision", job.TemplateRevision, 64).NVarCharMax("@Payload", job.Payload).Binary32("@PayloadHash", job.PayloadHash)
            .DateTime2("@DeadlineAt", job.DeadlineAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Clave existente: en una transacción nueva se lee la fila por la clave y se compara la huella.
    /// La detección existente nunca se reutiliza (I15).
    /// </summary>
    private async Task<RegistrationResult> ClassifyExistingKeyAsync(RegistrationPlan plan, CancellationToken ct)
    {
        var d = plan.Detection;
        return await db.InTransactionAsync(async (conn, tx) =>
        {
            var existing = await DetectionQueries.GetByIdentityAsync(conn, tx, d.Identity, ct)
                ?? throw new InvalidOperationException($"Violación de identidad sin fila existente para {d.IdempotencyKey}.");
            if (existing.SourceFingerprint.AsSpan().SequenceEqual(d.SourceFingerprint))
            {
                return new RegistrationResult(RegistrationOutcome.Duplicate, existing.DetectionId, existing.Status, existing.Reason, null, null, null, []);
            }

            var anomalyId = await AnomalyWriter.InsertAsync(conn, tx, new AnomalyRow(
                d.SiteId, d.StationId, d.PlcEpoch, d.CycleId, AnomalyType.IdentityCollision, "BLOCKING", existing.DetectionId,
                existing.SourceFingerprint, d.SourceFingerprint, IncomingRecordJson(d), plan.EdgeNowUtc, d.ConfigRevisionId), ct);
            await StationConfigStore.BlockAsync(conn, tx, d.SiteId, d.StationId, BlockReason.IdentityCollision, "SYSTEM", anomalyId,
                plan.EdgeNowUtc, ct);
            return new RegistrationResult(RegistrationOutcome.Collision, existing.DetectionId, existing.Status, existing.Reason, null, anomalyId, null, []);
        }, ct);
    }

    /// <summary>Registro completo retenido para un reingreso posterior (ANOMALY_REINGEST).</summary>
    public static string IncomingRecordJson(DetectionInsert d) => ContractJson.Serialize(new RetainedRecord
    {
        SiteId = d.SiteId,
        StationId = d.StationId,
        PlcEpoch = d.PlcEpoch,
        CycleId = d.CycleId,
        DetectionTimestamp = d.DetectionTimestamp,
        WorkOrderId = d.WorkOrderId,
        CartonId = d.CartonId,
        Transport = d.Transport,
        QueueOverflow = d.QueueOverflow,
        ReceivedAt = d.ReceivedAt,
        OpcNodeId = d.OpcNodeId,
        OpcSourceTimestamp = d.OpcSourceTimestamp,
        SourceFingerprint = Convert.ToHexStringLower(d.SourceFingerprint),
    });
}
