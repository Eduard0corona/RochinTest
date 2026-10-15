using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CartonPrinting.Contracts;

/// <summary>Opciones de serialización de los contratos v1.</summary>
public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Los payloads se publican a MES, ERP y tableros, no se incrustan en HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Documento vacío.");
}

/// <summary>Instante UTC en la forma canónica <c>YYYY-MM-DDTHH:MM:SS.fffZ</c>.</summary>
public sealed class UtcMillisecondsConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        CanonicalTimestamp.ParseRfc3339(reader.GetString()!).UtcDateTime;

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(CanonicalTimestamp.ToCanonicalString(value));
}

/// <summary><c>CycleId</c> (UInt64) como cadena decimal canónica.</summary>
public sealed class CycleIdConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Identity.TryParseCycleId(reader.GetString(), out var value))
        {
            throw new JsonException("cycleId debe ser texto decimal canónico.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Identity.FormatCycleId(value));
}
