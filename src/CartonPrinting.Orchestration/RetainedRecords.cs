using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;

namespace CartonPrinting.Orchestration;

/// <summary>
/// Registro completo retenido en SequenceAnomaly.IncomingRecord y su reingreso. El reingreso conserva
/// los valores latched y el ReceivedAt original; solo cambia el transporte (ANOMALY_REINGEST), de modo
/// que su propio plazo decide (sección 9.2).
/// </summary>
public static class RetainedRecords
{
    public static RetainedRecord From(CycleRecord r) => new()
    {
        SiteId = r.SiteId,
        StationId = r.StationId,
        PlcEpoch = r.PlcEpoch,
        CycleId = r.CycleId,
        DetectionTimestamp = r.DetectionTimestamp is { } t ? CanonicalTimestamp.Truncate(t) : null,
        WorkOrderId = r.WorkOrderId,
        CartonId = r.CartonId,
        Transport = r.Transport,
        QueueOverflow = r.QueueOverflow,
        ReceivedAt = CanonicalTimestamp.Truncate(r.ReceivedAt),
        OpcNodeId = r.OpcNodeId,
        OpcSourceTimestamp = r.OpcSourceTimestamp is { } s ? CanonicalTimestamp.Truncate(s) : null,
        SourceFingerprint = Convert.ToHexStringLower(r.SourceFingerprint()),
        Coherent = r.Coherent,
    };

    public static string ToJson(CycleRecord r) => ContractJson.Serialize(From(r));

    /// <summary>Registro retenido liberado por OT: mismo registro latched, Transport = ANOMALY_REINGEST.</summary>
    public static CycleRecord Reingest(string incomingRecordJson)
    {
        var r = JsonSerializer.Deserialize<RetainedRecord>(incomingRecordJson, ContractJson.Options)
            ?? throw new JsonException("Registro retenido vacío.");
        var record = new CycleRecord
        {
            SiteId = r.SiteId,
            StationId = r.StationId,
            PlcEpoch = r.PlcEpoch,
            CycleId = r.CycleId,
            DetectionTimestamp = r.DetectionTimestamp,
            WorkOrderId = r.WorkOrderId,
            CartonId = r.CartonId,
            Transport = Transport.AnomalyReingest,
            QueueOverflow = r.QueueOverflow,
            ReceivedAt = r.ReceivedAt,
            ReceivedTimestamp = null,
            OpcNodeId = r.OpcNodeId,
            OpcSourceTimestamp = r.OpcSourceTimestamp,
            Coherent = r.Coherent,
        };
        if (Convert.ToHexStringLower(record.SourceFingerprint()) != r.SourceFingerprint)
        {
            throw new InvalidOperationException($"El registro retenido {r.PlcEpoch}:{r.CycleId} no reproduce su huella de origen.");
        }

        return record;
    }
}
