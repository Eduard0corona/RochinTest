using System.Text.Json;
using System.Text.Json.Nodes;
using CartonPrinting.Contracts;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Contracts;

/// <summary>
/// T23 (refuerzo) · Pruebas diferenciales: el validador C# y las reglas de negocio C# producen,
/// para documentos mutados a propósito, exactamente lo mismo que la implementación de referencia
/// (jsonschema de Python con el FormatChecker de tools/verify_package.py, y business_errors).
/// </summary>
[Trait("Spec", "T23")]
public sealed class ContractReferenceDifferentialTests
{
    [Fact]
    public void Schema_errors_match_python_jsonschema_for_mutated_documents()
    {
        var cases = MutatedDocuments().ToList();
        var input = new JsonArray(cases.Select(c => (JsonNode)new JsonObject
        {
            ["schema"] = c.Schema,
            ["doc"] = c.Doc.DeepClone(),
        }).ToArray());

        var reference = PythonReference.Run("schema-errors", input);

        Assert.Equal(cases.Count, reference.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            using var doc = JsonDocument.Parse(cases[i].Doc.ToJsonString());
            var mine = ContractSchemaValidator.Instance.Validate(cases[i].Schema, doc.RootElement)
                .Select(e => $"{e.Keyword}|{e.InstancePath}").Distinct().Order(StringComparer.Ordinal).ToArray();
            var theirs = reference[i]!.AsArray()
                .Select(k => $"{k![0]!.GetValue<string>()}|{k[1]!.GetValue<string>()}").Order(StringComparer.Ordinal).ToArray();
            Assert.True(theirs.SequenceEqual(mine), $"{cases[i].Name}: Python [{string.Join(", ", theirs)}] C# [{string.Join(", ", mine)}]");
        }
    }

    [Fact]
    public void Business_errors_match_python_for_mutated_documents()
    {
        var docs = BusinessMutations().ToList();
        var reference = PythonReference.Run("business-errors", new JsonArray(docs.Select(d => d.Doc.DeepClone()).ToArray()));

        for (var i = 0; i < docs.Count; i++)
        {
            using var doc = JsonDocument.Parse(docs[i].Doc.ToJsonString());
            var mine = ContractBusinessRules.Validate(doc.RootElement);
            var theirs = reference[i]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
            Assert.True(theirs.SequenceEqual(mine), $"{docs[i].Name}: Python [{string.Join("; ", theirs)}] C# [{string.Join("; ", mine)}]");
        }
    }

    private sealed record Case(string Name, string Schema, JsonNode Doc);

    private static JsonObject Example(string file) =>
        JsonNode.Parse(File.ReadAllText(RepoPaths.SpecFile("contracts", "examples", "valid", file)))!.AsObject();

    private static IEnumerable<Case> MutatedDocuments()
    {
        const string R = ContractSchemaValidator.PrintJobResultSchemaName;
        const string C = ContractSchemaValidator.CartonDetectedSchemaName;

        Case Mut(string name, string schema, string file, Action<JsonObject> change)
        {
            var d = Example(file);
            change(d);
            return new Case(name, schema, d);
        }

        yield return Mut("propiedad extra en la raíz", R, "result-confirmed-verified.json", d => d["extra"] = 1);
        yield return Mut("propiedad extra en evidence", R, "result-confirmed-verified.json", d => d["evidence"]!["x"] = true);
        yield return Mut("uuid en mayúsculas", R, "result-confirmed-verified.json", d => d["eventId"] = d["eventId"]!.GetValue<string>().ToUpperInvariant());
        yield return Mut("uuid sin guiones", C, "carton-detected-observed.json", d => d["eventId"] = d["eventId"]!.GetValue<string>().Replace("-", "", StringComparison.Ordinal));
        yield return Mut("fecha imposible", C, "carton-detected-observed.json", d => d["receivedAt"] = "2026-13-01T00:00:00Z");
        yield return Mut("fecha sin zona", R, "result-cancelled.json", d => d["occurredAt"] = "2026-10-12T18:05:02.330");
        yield return Mut("época negativa", C, "carton-detected-observed.json", d => d["plcEpoch"] = -1);
        yield return Mut("época mayor que UInt32", C, "carton-detected-observed.json", d => d["plcEpoch"] = 4294967296L);
        yield return Mut("cycleId con ceros a la izquierda", C, "carton-detected-observed.json", d => d["cycleId"] = "007");
        yield return Mut("outcome desconocido", R, "result-cancelled.json", d => d["outcome"] = "FOO");
        yield return Mut("varios errores a la vez", R, "result-confirmed-verified.json", d =>
        {
            d.Remove("evidence");
            d["final"] = false;
            d["resultVersion"] = 0;
        });
        yield return Mut("BLOCKED con intento y serial", R, "result-blocked-sequence-anomaly.json", d =>
        {
            d["attemptId"] = "ab17ad7b-afc5-4e3d-a38a-bd0dbb286d01";
            d["labelSerial"] = "P01L01-000000000001";
        });
        yield return Mut("statusCode Bad y transporte en vivo recuperado", C, "carton-detected-recovered-fifo.json", d =>
        {
            d["opc"]!["statusCode"] = "Bad";
            d["detectionOrigin"] = "OBSERVED";
        });
        yield return Mut("exceptionalReprint con riesgo incoherente", R, "result-confirmed-exceptional-reprint.json",
            d => d["exceptionalReprint"]!["duplicateRisk"] = "NONE_PROVEN");
        yield return Mut("operatorAction en FAILED sin reconciliación", R, "result-failed-printer-unavailable.json",
            d => d["operatorAction"] = Example("result-failed-reconciled-not-printed.json")["operatorAction"]!.DeepClone());
        yield return Mut("evidence vacía", R, "result-confirmed-verified.json", d => d["evidence"] = new JsonObject());
        yield return Mut("tipo equivocado en queueOverflow", C, "carton-detected-observed.json", d => d["queueOverflow"] = "false");
        yield return Mut("documento válido", R, "result-unknown-serial-mismatch-correction.json", _ => { });

        // Hallazgo M2 de la revisión de la Fase 0: un salto de línea final no es un formato válido.
        yield return Mut("fecha con salto de línea final", C, "carton-detected-observed.json", d => d["receivedAt"] = d["receivedAt"]!.GetValue<string>() + "\n");
        yield return Mut("uuid con salto de línea final", R, "result-confirmed-verified.json", d => d["printJobId"] = d["printJobId"]!.GetValue<string>() + "\n");
        yield return Mut("eventId con salto de línea final", C, "carton-detected-observed.json", d => d["eventId"] = d["eventId"]!.GetValue<string>() + "\n");

        // Hallazgo B1: desplazamientos y medianoche que fromisoformat (Python 3.14) acepta.
        foreach (var offset in new[] { "+14:01", "+15:00", "-23:59", "+02:60", "+24:00", "+23:60" })
        {
            yield return Mut($"desplazamiento {offset}", R, "result-cancelled.json", d => d["occurredAt"] = "2026-10-12T18:05:02.330" + offset);
        }

        yield return Mut("hora 24:00:00", R, "result-cancelled.json", d => d["occurredAt"] = "2026-10-12T24:00:00Z");
        yield return Mut("hora 24:00:01", R, "result-cancelled.json", d => d["occurredAt"] = "2026-10-12T24:00:01Z");
        yield return Mut("segundo 60", R, "result-cancelled.json", d => d["occurredAt"] = "2026-10-12T18:00:60Z");
        yield return Mut("fracción de 9 dígitos", R, "result-cancelled.json", d => d["occurredAt"] = "2026-10-12T18:05:02.123456789Z");

        // Propiedad que se llama como una palabra clave aplicadora.
        yield return Mut("propiedad llamada not", R, "result-cancelled.json", d => d["not"] = 1);
    }

    private static IEnumerable<Case> BusinessMutations()
    {
        Case Mut(string name, string file, Action<JsonObject> change)
        {
            var d = Example(file);
            change(d);
            return new Case(name, string.Empty, d);
        }

        yield return Mut("clave distinta", "carton-detected-observed.json", d => d["idempotencyKey"] = "PLANT-01:LINE-01:42:1");
        yield return Mut("correlationId distinto", "carton-detected-observed.json", d => d["correlationId"] = "6ca03007-d7b1-5163-8595-e5d8349a78e9");
        yield return Mut("occurredAt distinto", "carton-detected-observed.json", d => d["occurredAt"] = d["receivedAt"]!.GetValue<string>());
        yield return Mut("plazo anterior a la referencia", "carton-detected-observed.json", d => d["deadlineAt"] = "2026-10-12T18:04:11.204Z");
        yield return Mut("timestamp con microsegundos", "carton-detected-observed.json", d =>
        {
            d["detectionTimestamp"] = "2026-10-12T18:04:11.204999Z";
            d["occurredAt"] = "2026-10-12T18:04:11.204999Z";
        });
        yield return Mut("timestamp con desplazamiento", "carton-detected-observed.json", d =>
        {
            d["detectionTimestamp"] = "2026-10-12T20:04:11.204+02:00";
            d["occurredAt"] = "2026-10-12T20:04:11.204+02:00";
        });
        yield return Mut("caracteres que los serializadores escapan distinto", "carton-detected-observed.json", d =>
        {
            d["workOrderId"] = "WO+<&>'\"\\ñ€\u0001";
            d["cartonId"] = "C/77 ü";
        });
        yield return Mut("SERIAL_MISMATCH con el mismo serial", "result-unknown-serial-mismatch-correction.json",
            d => d["evidence"]!["readSerial"] = d["labelSerial"]!.GetValue<string>());
        yield return Mut("final incoherente", "result-cancelled.json", d => d["final"] = false);
        yield return Mut("eventId de otra versión", "result-closed-unresolved.json", d => d["resultVersion"] = 3);
        yield return Mut("válido", "result-expired-out-of-order.json", _ => { });

        // Hallazgo M1: marcadores de uuidNames dentro de los valores no se vuelven a sustituir.
        yield return Mut("siteId con {labelType}", "carton-detected-observed.json", d =>
        {
            d["siteId"] = "{labelType}";
            d["idempotencyKey"] = "{labelType}:LINE-01:42:918273";
        });
        yield return Mut("siteId con {resultVersion} en un resultado", "result-cancelled.json", d =>
        {
            d["siteId"] = "{resultVersion}";
            d["idempotencyKey"] = "{resultVersion}:LINE-01:42:918280";
        });
        yield return Mut("labelType con {resultVersion}", "result-cancelled.json", d => d["labelType"] = "CARTON {resultVersion}");

        // Hallazgo B2: números con parte decimal o exponente, que json.loads lee como float.
        yield return Mut("plcEpoch 42.0", "carton-detected-observed.json", d => d["plcEpoch"] = JsonNode.Parse("42.0"));
        yield return Mut("resultVersion 1e0", "result-cancelled.json", d => d["resultVersion"] = JsonNode.Parse("1e0"));
        yield return Mut("plcEpoch 4.2e1", "carton-detected-observed.json", d => d["plcEpoch"] = JsonNode.Parse("4.2e1"));
    }
}
