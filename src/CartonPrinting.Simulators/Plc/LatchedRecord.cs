using System.Text.Json;
using System.Text.Json.Serialization;
using CartonPrinting.Contracts;

namespace CartonPrinting.Simulators.Plc;

/// <summary>
/// Registro latched de un ciclo tal como lo publica el PLC simulado: todos los campos del ciclo en un
/// solo valor atómico. El formato real del registro es una decisión de integración con el PLC (OT-04);
/// el simulador usa un String JSON y el cliente lo decodifica detrás de <c>ICycleRecordDecoder</c>.
/// </summary>
public sealed record LatchedRecord
{
    [JsonPropertyName("plcEpoch")]
    public required long PlcEpoch { get; init; }

    [JsonPropertyName("cycleId")]
    [JsonConverter(typeof(CycleIdConverter))]
    public required ulong CycleId { get; init; }

    [JsonPropertyName("detectionTimestamp")]
    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public DateTime? DetectionTimestamp { get; init; }

    [JsonPropertyName("workOrderId")]
    public string? WorkOrderId { get; init; }

    [JsonPropertyName("cartonId")]
    public string? CartonId { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, ContractJson.Options);

    public static LatchedRecord Parse(string json) =>
        JsonSerializer.Deserialize<LatchedRecord>(json, ContractJson.Options) ?? throw new JsonException("Registro vacío.");
}
