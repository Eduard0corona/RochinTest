using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Orchestration;

namespace CartonPrinting.Testing;

/// <summary>Revisiones y registros de prueba sin SQL (pruebas unitarias de la clasificación y del plazo).</summary>
public static class TestRevisions
{
    public static StationRevision Revision(string reference = DeadlineReference.PlcTimestamp, Func<StationRevision, StationRevision>? configure = null)
    {
        var revision = new StationRevision
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
            EpochChangePolicy = PlcEpochStore.AutoIncrementByOne,
            CartonIdentityMode = "CYCLE_ONLY",
            GapGraceMs = 5000,
            PhysicalGateMode = "NONE",
            GateFreshnessMs = null,
            CreatedBy = "ingeniero.ot",
            ApprovedBy = "supervisor.calidad",
            ChangeReason = "Prueba",
        };
        return configure is null ? revision : configure(revision);
    }

    public static CycleRecord Cycle(long epoch, ulong cycle, DateTime? detectionTimestamp, DateTime receivedAt, string transport = Transport.Subscription,
        string? workOrder = "WO-2026-4815") => new()
        {
            SiteId = "PLANT-01",
            StationId = "LINE-01",
            PlcEpoch = epoch,
            CycleId = cycle,
            DetectionTimestamp = detectionTimestamp,
            WorkOrderId = workOrder,
            Transport = transport,
            QueueOverflow = false,
            ReceivedAt = receivedAt,
            OpcNodeId = "ns=2;s=LINE-01.CartonCycleRecord",
        };
}
