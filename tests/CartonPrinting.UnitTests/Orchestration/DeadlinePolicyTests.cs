using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Orchestration;

namespace CartonPrinting.UnitTests.Orchestration;

/// <summary>
/// Plazo efectivo de un job pendiente: el menor entre el congelado y el recalculado con la revisión
/// activa; si la activa no puede calcularlo, la recepción del registro (SI-09, falla cerrada).
/// </summary>
public sealed class DeadlinePolicyTests
{
    private static readonly DateTime Detected = new(2026, 10, 14, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Received = Detected.AddMilliseconds(20);

    private static StationRevision Revision(string reference) => new()
    {
        SiteId = "PLANT-01",
        StationId = "LINE-01",
        RevisionNumber = 7,
        PlcEndpointId = "opc.tcp://sim/LINE-01",
        PrinterId = "PRN-LINE-01",
        ManualPrinterId = "PRN-MAN-LINE-01",
        LabelType = "CARTON_GS1",
        TemplateRevision = "TPL-7",
        DeadlineReference = reference,
        TransitBudgetMs = 1500,
        DeviceLeadTimeMs = 400,
        SendMarginMs = 100,
        ClockSkewToleranceMs = reference == DeadlineReference.PlcTimestamp ? 50 : null,
        MaxUpstreamLatencyMs = reference == DeadlineReference.GatewayReceipt ? 40 : null,
        ClockJumpToleranceMs = 200,
        MinCycleIntervalMs = null,
        MaxAutoAttempts = 3,
        RetryBackoffMs = 100,
        MinConfirmationLevel = EvidenceLevel.Printed,
        EvidenceWindowMs = 2000,
        EpochChangePolicy = "AUTO_INCREMENT_BY_ONE",
        CartonIdentityMode = "CYCLE_ONLY",
        GapGraceMs = 5000,
        PhysicalGateMode = "NONE",
        GateFreshnessMs = null,
        CreatedBy = "ingeniero.ot",
        ApprovedBy = "supervisor.calidad",
        ChangeReason = "Prueba",
    };

    private static DetectionRecord Detection(DateTime? detectionTimestamp, string transport, string reference, DateTime deadline) => new()
    {
        DetectionId = Guid.NewGuid(),
        SiteId = "PLANT-01",
        StationId = "LINE-01",
        PlcEpoch = 1,
        CycleId = 10,
        IdempotencyKey = "PLANT-01:LINE-01:1:10",
        SourceFingerprint = new byte[32],
        Origin = transport == Transport.Subscription ? "OBSERVED" : "RECOVERED",
        Transport = transport,
        QueueOverflow = false,
        DetectionTimestamp = detectionTimestamp,
        ReceivedAt = Received,
        DeadlineReference = reference,
        DeadlineAt = deadline,
        ConfigRevisionId = 1,
        WorkOrderId = "WO-1",
        Status = DetectionStatus.JobCreated,
    };

    [Fact]
    public void Effective_deadline_is_never_later_than_the_frozen_one()
    {
        var frozen = Detected.AddMilliseconds(500);
        var detection = Detection(Detected, Transport.Subscription, DeadlineReference.PlcTimestamp, frozen);
        Assert.Equal(frozen, DeadlinePolicy.Effective(frozen, Revision(DeadlineReference.PlcTimestamp), detection));
    }

    [Fact]
    public void Si_09_active_revision_without_a_reliable_deadline_falls_back_to_reception()
    {
        // Registro GATEWAY_RECEIPT en vivo sin DetectionTimestamp; la revisión activa pasó a PLC_TIMESTAMP.
        var frozen = Received.AddMilliseconds(1000);
        var detection = Detection(null, Transport.Subscription, DeadlineReference.GatewayReceipt, frozen);
        Assert.Equal(Received, DeadlinePolicy.Effective(frozen, Revision(DeadlineReference.PlcTimestamp), detection));

        // Registro recuperado (READ) y revisión activa GATEWAY_RECEIPT: tampoco hay cota de latencia.
        var recovered = Detection(Detected, Transport.Read, DeadlineReference.PlcTimestamp, frozen);
        Assert.Equal(Received, DeadlinePolicy.Effective(frozen, Revision(DeadlineReference.GatewayReceipt), recovered));
    }
}
