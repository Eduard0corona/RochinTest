using CartonPrinting.Contracts;

namespace CartonPrinting.Orchestration;

/// <summary>
/// Un registro latched del ciclo, tal como lo entrega la ingesta OPC UA: valores fijados por el PLC
/// (identidad, timestamp, orden, caja) y cómo llegó (transporte, overflow, recepción). El callback
/// OPC solo valida el StatusCode Good, sella ReceivedAt (UTC del EdgeHost) y la marca monotónica, y
/// encola; nunca toca SQL.
/// </summary>
public sealed record CycleRecord
{
    public required string SiteId { get; init; }
    public required string StationId { get; init; }
    public required long PlcEpoch { get; init; }
    public required ulong CycleId { get; init; }

    /// <summary>Instante del flanco escrito por el PLC como dato (no el SourceTimestamp de OPC).</summary>
    public DateTime? DetectionTimestamp { get; init; }

    public string? WorkOrderId { get; init; }
    public string? CartonId { get; init; }
    public required string Transport { get; init; }
    public required bool QueueOverflow { get; init; }

    /// <summary>Recepción original en el EdgeHost (UTC truncado a milisegundos); un replay la conserva.</summary>
    public required DateTime ReceivedAt { get; init; }

    /// <summary>Marca monotónica de recepción (null si el registro no se recibió en este proceso).</summary>
    public long? ReceivedTimestamp { get; init; }

    public required string OpcNodeId { get; init; }
    public DateTime? OpcSourceTimestamp { get; init; }
    public DateTime? OpcServerTimestamp { get; init; }

    public CycleIdentity Identity => new(SiteId, StationId, PlcEpoch, CycleId);

    /// <summary>Huella de origen: solo valores latched; nada de lo que cambia en una retransmisión legítima.</summary>
    public byte[] SourceFingerprint() =>
        Contracts.SourceFingerprint.ComputeBytes(SiteId, StationId, PlcEpoch, CycleId, DetectionTimestamp, WorkOrderId, CartonId);
}

/// <summary>Datos fijos de la estación en este EdgeHost (no versionados en SQL).</summary>
public sealed record StationSettings(string SiteId, string StationId, string LabelSerialPrefix, string OpcNodeId);
