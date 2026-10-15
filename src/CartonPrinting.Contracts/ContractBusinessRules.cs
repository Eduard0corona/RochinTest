using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CartonPrinting.Contracts;

/// <summary>
/// Reglas de los contratos que JSON Schema no puede expresar: port de <c>business_errors</c> de
/// tools/verify_package.py, con los mismos mensajes y en el mismo orden. Se aplican a documentos
/// que ya cumplen su schema.
/// </summary>
public static class ContractBusinessRules
{
    public const string IdempotencyKeyMismatch = "idempotencyKey no es siteId:stationId:plcEpoch:cycleId";
    public const string DetectedEventIdMismatch = "eventId no es UUIDv5 de 'CartonDetected|' + idempotencyKey";
    public const string DetectedCorrelationMismatch = "correlationId distinto de eventId";
    public const string OccurredAtMismatch = "occurredAt debe ser detectionTimestamp (o receivedAt si no existe)";
    public const string FingerprintMismatch = "sourceFingerprint no corresponde al arreglo canónico de valores latched";
    public const string GatewayFabricatedDeadline = "GATEWAY_RECEIPT sin entrega en vivo: deadlineAt debe ser receivedAt (plazo fabricado)";
    public const string PlcFabricatedDeadline = "PLC_TIMESTAMP sin detectionTimestamp: deadlineAt debe ser receivedAt (plazo fabricado)";
    public const string DeadlineNotAfterReference = "deadlineAt no es posterior a su referencia temporal";
    public const string ResultEventIdMismatch = "eventId no es UUIDv5 de su nombre de resultado";
    public const string ResultCorrelationMismatch = "correlationId no es el DetectionId";
    public const string ConfirmedSerialMismatch = "CONFIRMED con readSerial distinto de labelSerial (I06)";
    public const string SerialMismatchSameSerial = "SERIAL_MISMATCH con readSerial igual a labelSerial";
    public const string FinalMismatch = "final no coincide con contractOutcomes";

    public static IReadOnlyList<string> Validate(JsonElement doc) => Validate(doc, StateMachineSpec.Instance);

    public static IReadOnlyList<string> Validate(JsonElement doc, StateMachineSpec spec)
    {
        var errors = new List<string>();
        var key = string.Join(':', Text(doc, "siteId"), Text(doc, "stationId"), Text(doc, "plcEpoch"), Text(doc, "cycleId"));
        if (OptionalString(doc, "idempotencyKey") != key)
        {
            errors.Add(IdempotencyKeyMismatch);
        }

        var detectionId = EventIds.CartonDetected(key).ToString();
        if (Text(doc, "eventType") == "CartonDetected")
        {
            if (Text(doc, "eventId") != detectionId)
            {
                errors.Add(DetectedEventIdMismatch);
            }

            if (Text(doc, "correlationId") != Text(doc, "eventId"))
            {
                errors.Add(DetectedCorrelationMismatch);
            }

            var detectionTimestamp = OptionalString(doc, "detectionTimestamp");
            var receivedAt = Text(doc, "receivedAt");
            var expectedOccurred = doc.TryGetProperty("detectionTimestamp", out _) ? detectionTimestamp : receivedAt;
            if (Text(doc, "occurredAt") != expectedOccurred)
            {
                errors.Add(OccurredAtMismatch);
            }

            if (DocumentFingerprint(doc) != Text(doc, "sourceFingerprint"))
            {
                errors.Add(FingerprintMismatch);
            }

            var live = Text(doc, "transport") == Transport.Subscription && !doc.GetProperty("queueOverflow").GetBoolean();
            var reference = Text(doc, "deadlineReference");
            var deadlineAt = Text(doc, "deadlineAt");
            if (reference == DeadlineReference.GatewayReceipt && !live)
            {
                // Sin cota de latencia no hay plazo: no se fabrica uno (D04 LATE_ARRIVAL).
                if (deadlineAt != receivedAt)
                {
                    errors.Add(GatewayFabricatedDeadline);
                }
            }
            else if (reference == DeadlineReference.PlcTimestamp && !doc.TryGetProperty("detectionTimestamp", out _))
            {
                // Registro incoherente (D05 INCOHERENT_SNAPSHOT): tampoco tiene plazo.
                if (deadlineAt != receivedAt)
                {
                    errors.Add(PlcFabricatedDeadline);
                }
            }
            else
            {
                var refText = reference == DeadlineReference.PlcTimestamp ? detectionTimestamp! : receivedAt;
                if (CanonicalTimestamp.ParseRfc3339(deadlineAt) <= CanonicalTimestamp.ParseRfc3339(refText))
                {
                    errors.Add(DeadlineNotAfterReference);
                }
            }
        }
        else
        {
            var resultVersion = doc.GetProperty("resultVersion");
            var name = spec.UuidNames["PrintJobResult"]
                .Replace("{idempotencyKey}", key, StringComparison.Ordinal)
                .Replace("{labelType}", Text(doc, "labelType"), StringComparison.Ordinal)
                .Replace("{resultVersion}", NumberText(resultVersion), StringComparison.Ordinal);
            if (Text(doc, "eventId") != Uuid5.Create(spec.UuidNamespace, name).ToString())
            {
                errors.Add(ResultEventIdMismatch);
            }

            if (Text(doc, "correlationId") != detectionId)
            {
                errors.Add(ResultCorrelationMismatch);
            }

            if (doc.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.Object && ev.EnumerateObject().Any()
                && OptionalString(ev, "level") == EvidenceLevel.Verified)
            {
                var readSerial = OptionalString(ev, "readSerial");
                var labelSerial = OptionalString(doc, "labelSerial");
                if (Text(doc, "outcome") == ResultOutcome.Confirmed && readSerial != labelSerial)
                {
                    errors.Add(ConfirmedSerialMismatch);
                }

                if (OptionalString(doc, "reason") == JobReason.SerialMismatch && readSerial == labelSerial)
                {
                    errors.Add(SerialMismatchSameSerial);
                }
            }

            if (doc.GetProperty("final").GetBoolean() != spec.ContractOutcomeFinal[Text(doc, "outcome")])
            {
                errors.Add(FinalMismatch);
            }
        }

        return errors;
    }

    /// <summary>Huella calculada con los valores tal como vienen en el documento (como en Python).</summary>
    public static string DocumentFingerprint(JsonElement doc)
    {
        string? ts = null;
        if (doc.TryGetProperty("detectionTimestamp", out var t) && t.ValueKind == JsonValueKind.String)
        {
            ts = CanonicalTimestamp.ToCanonicalString(CanonicalTimestamp.ParseRfc3339(t.GetString()!));
        }

        object?[] arr =
        [
            JsonValue(doc.GetProperty("siteId")),
            JsonValue(doc.GetProperty("stationId")),
            JsonValue(doc.GetProperty("plcEpoch")),
            JsonValue(doc.GetProperty("cycleId")),
            ts,
            doc.TryGetProperty("workOrderId", out var wo) ? JsonValue(wo) : null,
            doc.TryGetProperty("cartonId", out var ca) ? JsonValue(ca) : null,
        ];
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Jcs.Serialize(arr))));
    }

    private static object? JsonValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number when e.TryGetInt64(out var l) => l,
        JsonValueKind.Number when e.TryGetUInt64(out var u) => u,
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new NotSupportedException($"Valor no admitido en la huella: {e.ValueKind}."),
    };

    private static string Text(JsonElement doc, string name)
    {
        var e = doc.GetProperty(name);
        return e.ValueKind == JsonValueKind.Number ? NumberText(e) : e.GetString() ?? string.Empty;
    }

    private static string NumberText(JsonElement e) =>
        e.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : e.GetRawText();

    private static string? OptionalString(JsonElement doc, string name) =>
        doc.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
