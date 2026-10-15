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
public static class EventIds
{
    public static Guid CartonDetected(string idempotencyKey) =>
        Uuid5.Create(StateMachineSpec.Instance.UuidNamespace, Name("CartonDetected", idempotencyKey, null, null));

    public static Guid PrintJobResult(string idempotencyKey, string labelType, int resultVersion) =>
        Uuid5.Create(StateMachineSpec.Instance.UuidNamespace, Name("PrintJobResult", idempotencyKey, labelType, resultVersion));

    private static string Name(string eventType, string idempotencyKey, string? labelType, int? resultVersion) =>
        StateMachineSpec.Instance.UuidNames[eventType]
            .Replace("{idempotencyKey}", idempotencyKey, StringComparison.Ordinal)
            .Replace("{labelType}", labelType ?? string.Empty, StringComparison.Ordinal)
            .Replace("{resultVersion}", resultVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);
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
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
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
