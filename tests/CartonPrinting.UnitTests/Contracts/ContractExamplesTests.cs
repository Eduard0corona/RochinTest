using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Contracts;

/// <summary>
/// T23 · Contratos inválidos. Criterio del catálogo: todos los válidos pasan schema y reglas de
/// negocio; cada inválido falla solo por la palabra clave y la ruta declaradas en EXPECTED.json;
/// cada ejemplo de negocio pasa el schema y falla exactamente su regla.
/// </summary>
[Trait("Spec", "T23")]
public sealed class ContractExamplesTests
{
    private static string ExamplesDir(string sub) => RepoPaths.SpecFile("contracts", "examples", sub);

    public static TheoryData<string> ValidExamples() =>
        new(Directory.GetFiles(ExamplesDir("valid"), "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal)!);

    public static TheoryData<string> InvalidExamples() =>
        new(ReadExpected("invalid").Select(c => c.Name).Order(StringComparer.Ordinal));

    public static TheoryData<string> BusinessInvalidExamples() =>
        new(ReadExpected("business-invalid").Select(c => c.Name).Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Valid_example_passes_schema_and_business_rules(string file)
    {
        using var doc = Load("valid", file);

        var schemaErrors = ContractSchemaValidator.Instance.Validate(doc.RootElement);
        var businessErrors = ContractBusinessRules.Validate(doc.RootElement);

        Assert.Empty(schemaErrors);
        Assert.Empty(businessErrors);
    }

    [Theory]
    [MemberData(nameof(InvalidExamples))]
    public void Invalid_example_fails_only_by_declared_keyword_and_path(string file)
    {
        var expected = ReadExpected("invalid").Single(c => c.Name == file);
        using var doc = Load("invalid", file);

        var errors = ContractSchemaValidator.Instance.Validate(expected.Schema!, doc.RootElement);

        Assert.NotEmpty(errors);
        var kinds = errors.Select(e => (e.Keyword, e.InstancePath)).ToHashSet();
        Assert.Equal([(expected.Keyword!, expected.Path!)], kinds);
    }

    [Theory]
    [MemberData(nameof(BusinessInvalidExamples))]
    public void Business_invalid_example_passes_schema_and_fails_exactly_its_rule(string file)
    {
        var expected = ReadExpected("business-invalid").Single(c => c.Name == file);
        using var doc = Load("business-invalid", file);

        Assert.Empty(ContractSchemaValidator.Instance.Validate(doc.RootElement));
        Assert.Equal([expected.Error!], ContractBusinessRules.Validate(doc.RootElement));
    }

    [Fact]
    public void Expected_files_cover_every_example_in_the_folder()
    {
        foreach (var sub in new[] { "invalid", "business-invalid" })
        {
            var files = Directory.GetFiles(ExamplesDir(sub), "*.json").Select(Path.GetFileName)
                .Where(n => n != "EXPECTED.json").ToHashSet();
            var declared = ReadExpected(sub).Select(c => c.Name).ToHashSet();
            Assert.Equal(declared.Order(), files.Order()!);
        }
    }

    [Fact]
    public void Every_contract_outcome_has_a_valid_example()
    {
        var seen = new HashSet<string>();
        foreach (var file in Directory.GetFiles(ExamplesDir("valid"), "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.GetProperty("eventType").GetString() == "PrintJobResult")
            {
                seen.Add(doc.RootElement.GetProperty("outcome").GetString()!);
            }
        }

        Assert.Equal(StateMachineSpec.Instance.ContractOutcomeFinal.Keys.Order(), seen.Order());
    }

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Fingerprint_and_event_ids_computed_in_csharp_match_the_example(string file)
    {
        using var doc = Load("valid", file);
        var root = doc.RootElement;
        var key = Identity.IdempotencyKey(
            root.GetProperty("siteId").GetString()!,
            root.GetProperty("stationId").GetString()!,
            root.GetProperty("plcEpoch").GetInt64(),
            ulong.Parse(root.GetProperty("cycleId").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(root.GetProperty("idempotencyKey").GetString(), key);

        var detectionId = EventIds.CartonDetected(key);
        if (root.GetProperty("eventType").GetString() == "CartonDetected")
        {
            var evt = ContractJson.Deserialize<CartonDetectedEvent>(root.GetRawText());
            var fingerprint = SourceFingerprint.Compute(evt.SiteId, evt.StationId, evt.PlcEpoch, evt.CycleId,
                evt.DetectionTimestamp, evt.WorkOrderId, evt.CartonId);

            Assert.Equal(root.GetProperty("sourceFingerprint").GetString(), fingerprint);
            Assert.Equal(root.GetProperty("eventId").GetGuid(), detectionId);
        }
        else
        {
            var evt = ContractJson.Deserialize<PrintJobResultEvent>(root.GetRawText());
            Assert.Equal(root.GetProperty("eventId").GetGuid(), EventIds.PrintJobResult(key, evt.LabelType, evt.ResultVersion));
            Assert.Equal(root.GetProperty("correlationId").GetGuid(), detectionId);
        }
    }

    [Theory]
    [MemberData(nameof(ValidExamples))]
    public void Typed_round_trip_still_passes_schema_and_business_rules(string file)
    {
        using var doc = Load("valid", file);
        var json = doc.RootElement.GetProperty("eventType").GetString() == "CartonDetected"
            ? ContractJson.Serialize(ContractJson.Deserialize<CartonDetectedEvent>(doc.RootElement.GetRawText()))
            : ContractJson.Serialize(ContractJson.Deserialize<PrintJobResultEvent>(doc.RootElement.GetRawText()));

        using var roundTrip = JsonDocument.Parse(json);
        Assert.Empty(ContractSchemaValidator.Instance.Validate(roundTrip.RootElement));
        Assert.Empty(ContractBusinessRules.Validate(roundTrip.RootElement));
        Assert.True(JsonElement.DeepEquals(doc.RootElement, roundTrip.RootElement), json);
    }

    private static JsonDocument Load(string sub, string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ExamplesDir(sub), file)));

    private static IReadOnlyList<ExpectedCase> ReadExpected(string sub)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExamplesDir(sub), "EXPECTED.json")));
        return doc.RootElement.GetProperty("cases").EnumerateObject().Select(p => new ExpectedCase(
            p.Name,
            p.Value.TryGetProperty("schema", out var s) ? s.GetString() : null,
            p.Value.TryGetProperty("keyword", out var k) ? k.GetString() : null,
            p.Value.TryGetProperty("path", out var pa) ? pa.GetString() : null,
            p.Value.TryGetProperty("error", out var e) ? e.GetString() : null)).ToArray();
    }

    private sealed record ExpectedCase(string Name, string? Schema, string? Keyword, string? Path, string? Error);
}
