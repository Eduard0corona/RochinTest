using System.Text.Json;
using System.Text.Json.Serialization;
using CartonPrinting.Contracts;

namespace CartonPrinting.Simulators.Plc;

/// <summary>
/// Registro latched de un ciclo tal como lo publica el PLC simulado: todos los campos del ciclo en un
/// solo valor atómico y, al final, el eco del CycleId que el PLC escribe al cerrar el registro
/// (<c>closingCycleId</c>). El formato real del registro es una decisión de integración con el PLC
/// (OT-04, SI-10); el simulador usa un String JSON y la ingesta lo decodifica con <c>CycleDecoder</c>.
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

    /// <summary>Eco del CycleId escrito al cerrar el registro; por defecto, el mismo CycleId.</summary>
    [JsonPropertyName("closingCycleId")]
    [JsonConverter(typeof(CycleIdConverter))]
    public ulong ClosingCycleId { get => _closing ?? CycleId; init => _closing = value; }

    private readonly ulong? _closing;

    /// <summary>
    /// Registro incoherente (T13): la identidad de un ciclo con los demás campos de otro, como si el PLC
    /// hubiera publicado a mitad de escribir el registro. El eco de cierre es el del otro ciclo.
    /// </summary>
    public static LatchedRecord Mixed(LatchedRecord identity, LatchedRecord fields) => fields with
    {
        PlcEpoch = identity.PlcEpoch,
        CycleId = identity.CycleId,
        ClosingCycleId = fields.CycleId,
    };

    public string ToJson() => JsonSerializer.Serialize(this, ContractJson.Options);

    public static LatchedRecord Parse(string json) =>
        JsonSerializer.Deserialize<LatchedRecord>(json, ContractJson.Options) ?? throw new JsonException("Registro vacío.");
}
