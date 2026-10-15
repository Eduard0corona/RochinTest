using System.Globalization;
using System.Security.Cryptography;

namespace CartonPrinting.Contracts;

/// <summary>Identidad de un ciclo del PLC: <c>(SiteId, StationId, PlcEpoch, CycleId)</c>.</summary>
public readonly record struct CycleIdentity(string SiteId, string StationId, long PlcEpoch, ulong CycleId)
{
    /// <summary><c>siteId:stationId:plcEpoch:cycleId</c>, con <c>cycleId</c> en decimal canónico.</summary>
    public string IdempotencyKey => Identity.IdempotencyKey(SiteId, StationId, PlcEpoch, CycleId);

    /// <summary>DetectionId = eventId de <c>CartonDetected</c> (UUIDv5).</summary>
    public Guid DetectionId => EventIds.CartonDetected(IdempotencyKey);

    public override string ToString() => IdempotencyKey;
}

public static class Identity
{
    public const long MaxPlcEpoch = uint.MaxValue;

    public static string IdempotencyKey(string siteId, string stationId, long plcEpoch, ulong cycleId) =>
        string.Create(CultureInfo.InvariantCulture, $"{siteId}:{stationId}:{plcEpoch}:{cycleId}");

    /// <summary>Texto decimal canónico de <c>CycleId</c> (sin ceros a la izquierda).</summary>
    public static string FormatCycleId(ulong cycleId) => cycleId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Acepta solo la forma canónica <c>^(0|[1-9][0-9]*)$</c> dentro del rango de UInt64.</summary>
    public static bool TryParseCycleId(string? text, out ulong cycleId)
    {
        cycleId = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 20)
        {
            return false;
        }

        if (text.Length > 1 && text[0] == '0')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out cycleId);
    }
}

/// <summary>Identificadores deterministas de los eventos (contracts/state-machine.json, uuidNames).</summary>
public static partial class EventIds
{
    public static Guid CartonDetected(string idempotencyKey) => CartonDetected(StateMachineSpec.Instance, idempotencyKey);

    public static Guid CartonDetected(StateMachineSpec spec, string idempotencyKey) =>
        Uuid5.Create(spec.UuidNamespace, FormatName(spec.UuidNames["CartonDetected"], new Dictionary<string, string>
        {
            ["idempotencyKey"] = idempotencyKey,
        }));

    public static Guid PrintJobResult(string idempotencyKey, string labelType, int resultVersion) =>
        PrintJobResult(StateMachineSpec.Instance, idempotencyKey, labelType, resultVersion.ToString(CultureInfo.InvariantCulture));

    public static Guid PrintJobResult(StateMachineSpec spec, string idempotencyKey, string labelType, string resultVersionText) =>
        Uuid5.Create(spec.UuidNamespace, FormatName(spec.UuidNames["PrintJobResult"], new Dictionary<string, string>
        {
            ["idempotencyKey"] = idempotencyKey,
            ["labelType"] = labelType,
            ["resultVersion"] = resultVersionText,
        }));

    /// <summary>
    /// Sustituye los marcadores de la plantilla en una sola pasada, como <c>str.format</c> en
    /// tools/verify_package.py: un valor que contenga <c>{labelType}</c> no se vuelve a sustituir.
    /// </summary>
    public static string FormatName(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v)
            ? v
            : throw new InvalidOperationException($"Marcador sin valor en uuidNames: {m.Value}."));

    [System.Text.RegularExpressions.GeneratedRegex(@"\{(\w+)\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Placeholder();
}

/// <summary>
/// Huella de origen: SHA-256 (hexadecimal en minúsculas) de la serialización JCS del arreglo
/// <c>[siteId, stationId, plcEpoch, cycleId, detectionTimestamp, workOrderId, cartonId]</c>
/// (contracts/state-machine.json, fingerprint). Debe producir los mismos bytes que
/// <c>source_fingerprint</c> de tools/verify_package.py.
/// </summary>
public static class SourceFingerprint
{
    public static byte[] ComputeBytes(
        string siteId,
        string stationId,
        long plcEpoch,
        ulong cycleId,
        DateTime? detectionTimestampUtc,
        string? workOrderId,
        string? cartonId)
    {
        var canonical = CanonicalArray(siteId, stationId, plcEpoch, cycleId, detectionTimestampUtc, workOrderId, cartonId);
        return SHA256.HashData(StrictUtf8.Encoding.GetBytes(canonical));
    }

    public static string Compute(
        string siteId,
        string stationId,
        long plcEpoch,
        ulong cycleId,
        DateTime? detectionTimestampUtc,
        string? workOrderId,
        string? cartonId) =>
        Convert.ToHexStringLower(ComputeBytes(siteId, stationId, plcEpoch, cycleId, detectionTimestampUtc, workOrderId, cartonId));

    /// <summary>El arreglo canónico en JCS, antes del hash.</summary>
    public static string CanonicalArray(
        string siteId,
        string stationId,
        long plcEpoch,
        ulong cycleId,
        DateTime? detectionTimestampUtc,
        string? workOrderId,
        string? cartonId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plcEpoch);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(plcEpoch, Identity.MaxPlcEpoch);
        var ts = detectionTimestampUtc is null ? null : CanonicalTimestamp.ToCanonicalString(detectionTimestampUtc.Value);
        object?[] arr = [siteId, stationId, plcEpoch, Identity.FormatCycleId(cycleId), ts, workOrderId, cartonId];
        return Jcs.Serialize(arr);
    }
}

/// <summary>
/// UTF-8 estricto: un surrogate aislado lanza en lugar de convertirse en U+FFFD, como
/// <c>str.encode("utf-8")</c> en Python. Dos valores distintos nunca comparten huella ni UUID.
/// </summary>
public static class StrictUtf8
{
    public static readonly System.Text.UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

