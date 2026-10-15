using System.Text.Json.Nodes;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Orchestration;

/// <summary>
/// T25 (criterio de aprobación): la huella del EdgeHost coincide byte a byte con source_fingerprint de
/// spec/tools/verify_package.py, incluidos valores que los serializadores escapan distinto (+, &amp;, &lt;,
/// &gt;, ', no ASCII), épocas distintas, CycleId en los extremos y campos ausentes.
/// </summary>
[Trait("Spec", "T25")]
public sealed class FingerprintVectorTests
{
    private static readonly DateTime T = new(2026, 10, 12, 18, 4, 11, 204, DateTimeKind.Utc);

    public static TheoryData<long, ulong, string?, string?, bool> Vectors => new()
    {
        { 42, 918273, "WO-2026-4815", null, true },
        { 43, 1, "WO+A&B<C>D'E", "CAJA+&<>'", true },
        { 42, 5000, "ÓRDEN-ñ-€-漢字-😀", "CAJA-é́", true },
        { 0, 0, "WO \"comillas\" \\ barra / \t tab", null, false },
        { 4294967295, ulong.MaxValue, null, "C-\u2028-\u2029-\u007f", true },
        { 45, 18446744073709551614, "<script>alert('x')</script>", "a&amp;b", false },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Edge_host_fingerprint_matches_the_reference_byte_for_byte(long epoch, ulong cycle, string? workOrder, string? carton, bool withTimestamp)
    {
        var record = TestRevisions.Cycle(epoch, cycle, withTimestamp ? T.AddTicks(9999) : null, T) with { CartonId = carton, WorkOrderId = workOrder };
        var doc = new JsonObject
        {
            ["siteId"] = record.SiteId,
            ["stationId"] = record.StationId,
            ["plcEpoch"] = epoch,
            ["cycleId"] = cycle.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["detectionTimestamp"] = withTimestamp ? "2026-10-12T18:04:11.204999Z" : null,
            ["workOrderId"] = workOrder,
            ["cartonId"] = carton,
        };

        var reference = PythonReference.Run("fingerprints", [doc])[0]!.GetValue<string>();
        Assert.Equal(reference, Convert.ToHexStringLower(record.SourceFingerprint()));
    }
}
