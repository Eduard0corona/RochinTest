using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Contracts;

/// <summary>UUIDv5, JCS, huella e instantes canónicos (base de T23 y T25).</summary>
[Trait("Spec", "T23")]
public sealed class IdentityPrimitivesTests
{
    [Fact]
    public void Uuid5_matches_rfc9562_test_vector()
    {
        // RFC 9562, apéndice A.4: namespace DNS y "www.example.com".
        var dns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"), Uuid5.Create(dns, "www.example.com"));
    }

    [Fact]
    public void Namespace_and_names_come_from_the_state_machine()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoPaths.SpecFile("contracts", "state-machine.json")));

        Assert.Equal(Guid.Parse(doc.RootElement.GetProperty("uuidNamespace").GetString()!), StateMachineSpec.Instance.UuidNamespace);
        Assert.Equal("CartonDetected|{idempotencyKey}", StateMachineSpec.Instance.UuidNames["CartonDetected"]);
    }

    [Fact]
    public void Jcs_escapes_only_quote_backslash_and_control_characters()
    {
        object?[] value = ["a+b&c<d>e'f", "ñandú €", "q\"b\\c", "\u0001\n\t", null, 42L, 18446744073709551615UL];

        Assert.Equal("[\"a+b&c<d>e'f\",\"ñandú €\",\"q\\\"b\\\\c\",\"\\u0001\\n\\t\",null,42,18446744073709551615]", Jcs.Serialize(value));
    }

    [Fact]
    public void Jcs_sorts_object_members_by_utf16_code_units()
    {
        var map = new Dictionary<string, object?> { ["b"] = 1L, ["a"] = "x", ["€"] = null, ["\u0080"] = true };

        Assert.Equal("{\"a\":\"x\",\"b\":1,\"\u0080\":true,\"€\":null}", Jcs.Serialize(map));
    }

    [Fact]
    public void Fingerprint_truncates_timestamp_to_milliseconds_and_uses_null_for_missing_values()
    {
        var ts = new DateTime(2026, 10, 12, 18, 4, 11, 204, DateTimeKind.Utc).AddTicks(9999);

        var canonical = SourceFingerprint.CanonicalArray("PLANT-01", "LINE-01", 42, 918273, ts, "WO-2026-4815", null);

        Assert.Equal("[\"PLANT-01\",\"LINE-01\",42,\"918273\",\"2026-10-12T18:04:11.204Z\",\"WO-2026-4815\",null]", canonical);
        Assert.Equal(
            "57d6ff68618411b327c4a5209af69e4f5802bb4a6282ed7946569cd16f893b3c",
            SourceFingerprint.Compute("PLANT-01", "LINE-01", 42, 918273, ts, "WO-2026-4815", null));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("18446744073709551615", true)]
    [InlineData("18446744073709551616", false)]
    [InlineData("007", false)]
    [InlineData("", false)]
    [InlineData("12abc", false)]
    [InlineData("-1", false)]
    public void CycleId_accepts_only_canonical_decimal_text(string text, bool ok)
    {
        Assert.Equal(ok, Identity.TryParseCycleId(text, out _));
    }

    [Fact]
    public void Canonical_timestamp_truncates_never_rounds()
    {
        var value = new DateTime(2026, 1, 1, 0, 0, 0, 999, DateTimeKind.Utc).AddTicks(9999);

        Assert.Equal("2026-01-01T00:00:00.999Z", CanonicalTimestamp.ToCanonicalString(value));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, 999, DateTimeKind.Utc), CanonicalTimestamp.Truncate(value));
    }

    [Fact]
    public void Canonical_timestamp_rejects_unspecified_kind()
    {
        Assert.Throws<ArgumentException>(() => CanonicalTimestamp.Truncate(new DateTime(2026, 1, 1)));
    }

    [Theory]
    [InlineData("2026-10-12T18:04:11.204Z", true)]
    [InlineData("2026-10-12T18:04:11Z", true)]
    [InlineData("2026-10-12T18:04:11.204+02:00", true)]
    [InlineData("2026-02-30T18:04:11Z", false)]
    [InlineData("2026-10-12 18:04:11Z", false)]
    [InlineData("2026-10-12T18:04:11", false)]
    public void Rfc3339_parsing_matches_the_verifier_format_rule(string text, bool ok)
    {
        Assert.Equal(ok, CanonicalTimestamp.TryParseRfc3339(text, out _));
    }
}
