using System.Text.Json;
using System.Text.Json.Serialization;
using CartonPrinting.Contracts;
using CartonPrinting.Orchestration;

namespace CartonPrinting.OpcIngest;

/// <summary>Resultado de decodificar una notificación: un registro de ciclo o el motivo por el que se descartó.</summary>
public sealed record DecodeResult(CycleRecord? Record, string? Rejection)
{
    public const string StatusNotGood = "STATUS_NOT_GOOD";
    public const string Empty = "EMPTY";
    public const string Malformed = "MALFORMED";
}

/// <summary>
/// Decodifica el registro latched del ciclo. El formato real del registro es una decisión de integración
/// con el PLC (OT-04); el del simulador es un String JSON con los campos latched y un eco del CycleId que
/// el PLC escribe al cerrar el registro (<c>closingCycleId</c>). Si el eco no coincide, los campos no
/// corresponden al mismo ciclo: el registro es incoherente (INCOHERENT_SNAPSHOT, T13). Sin eco, la
/// coherencia no se puede comprobar y el registro se acepta (limitación declarada, SI-10).
/// Solo los StatusCode Good producen un registro; nada sin identidad (época y ciclo) llega a TX-A.
/// </summary>
public sealed class CycleDecoder(StationSettings station)
{
    public DecodeResult Decode(RawNotification raw)
    {
        if (!raw.StatusGood)
        {
            return new DecodeResult(null, DecodeResult.StatusNotGood);
        }

        if (string.IsNullOrWhiteSpace(raw.Value))
        {
            return new DecodeResult(null, DecodeResult.Empty);
        }

        LatchedRecordFormat? latched;
        try
        {
            latched = JsonSerializer.Deserialize<LatchedRecordFormat>(raw.Value, ContractJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            return new DecodeResult(null, DecodeResult.Malformed);
        }

        if (latched?.PlcEpoch is not { } epoch || epoch is < 0 or > uint.MaxValue || latched.CycleId is not { } cycle)
        {
            return new DecodeResult(null, DecodeResult.Malformed);
        }

        return new DecodeResult(new CycleRecord
        {
            SiteId = station.SiteId,
            StationId = station.StationId,
            PlcEpoch = epoch,
            CycleId = cycle,
            DetectionTimestamp = latched.DetectionTimestamp,
            WorkOrderId = latched.WorkOrderId,
            CartonId = latched.CartonId,
            Transport = raw.Transport,
            QueueOverflow = raw.Overflow,
            ReceivedAt = CanonicalTimestamp.Truncate(raw.ReceivedAt),
            ReceivedTimestamp = raw.ReceivedTimestamp,
            OpcNodeId = raw.NodeId,
            OpcSourceTimestamp = raw.SourceTimestamp,
            OpcServerTimestamp = raw.ServerTimestamp,
            Coherent = latched.ClosingCycleId is null || latched.ClosingCycleId == cycle,
        }, null);
    }

    /// <summary>Formato del registro latched del PLC simulado (los nombres coinciden con el simulador).</summary>
    private sealed record LatchedRecordFormat
    {
        [JsonPropertyName("plcEpoch")]
        public long? PlcEpoch { get; init; }

        [JsonPropertyName("cycleId")]
        [JsonConverter(typeof(NullableCycleIdConverter))]
        public ulong? CycleId { get; init; }

        [JsonPropertyName("detectionTimestamp")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? DetectionTimestamp { get; init; }

        [JsonPropertyName("workOrderId")]
        public string? WorkOrderId { get; init; }

        [JsonPropertyName("cartonId")]
        public string? CartonId { get; init; }

        [JsonPropertyName("closingCycleId")]
        [JsonConverter(typeof(NullableCycleIdConverter))]
        public ulong? ClosingCycleId { get; init; }
    }

    /// <summary>CycleId como cadena decimal canónica (como en los contratos); null si falta.</summary>
    private sealed class NullableCycleIdConverter : JsonConverter<ulong?>
    {
        private static readonly CycleIdConverter Inner = new();

        public override ulong? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(ulong), options);

        public override void Write(Utf8JsonWriter writer, ulong? value, JsonSerializerOptions options)
        {
            if (value is { } v)
            {
                Inner.Write(writer, v, options);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
