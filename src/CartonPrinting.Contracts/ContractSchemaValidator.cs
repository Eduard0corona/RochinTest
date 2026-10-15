using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;

namespace CartonPrinting.Contracts;

/// <summary>Error de JSON Schema expresado como lo informa jsonschema de Python: palabra clave y ruta.</summary>
public sealed record SchemaViolation(string Keyword, string InstancePath, string Message)
{
    public override string ToString() => $"{Keyword} en '{InstancePath}': {Message}";
}

/// <summary>
/// Validación de los contratos v1 contra los schemas Draft 2020-12 de spec/contracts (incrustados
/// al compilar), con JsonSchema.Net.
/// </summary>
/// <remarks>
/// Los formatos <c>uuid</c> y <c>date-time</c> se validan como el FormatChecker de
/// tools/verify_package.py: UUID canónico en minúsculas y RFC 3339 con la semántica de
/// <c>datetime.fromisoformat</c> de Python 3.14 (ver <see cref="CanonicalTimestamp"/>).
/// <see cref="Validate"/> devuelve los errores "hoja" con la semántica de <c>iter_errors</c> de
/// jsonschema (Python): <c>allOf</c>, <c>properties</c>, <c>if/then/else</c> y <c>$ref</c> no
/// producen error propio, sino los de sus subschemas; <c>not</c>, <c>anyOf</c>, <c>oneOf</c> y
/// <c>contains</c> producen un solo error con su palabra clave. Así T23 puede exigir que cada
/// ejemplo inválido falle solo por la palabra clave y la ruta declaradas en EXPECTED.json.
/// </remarks>
public sealed partial class ContractSchemaValidator
{
    private static readonly Lazy<ContractSchemaValidator> Embedded = new(() => new ContractSchemaValidator());

    // Aplicadores que en jsonschema (Python) no producen error propio: se informan los de sus subschemas.
    private static readonly HashSet<string> PassThroughKeywords =
        ["allOf", "properties", "patternProperties", "then", "else", "$ref", "$dynamicRef", "items", "prefixItems", "dependentSchemas"];

    // Aplicadores que en jsonschema (Python) producen un solo error con su palabra clave.
    private static readonly HashSet<string> CollapsingKeywords = ["not", "anyOf", "oneOf", "contains", "additionalProperties"];

    private readonly JsonSchema _cartonDetected;
    private readonly JsonSchema _printJobResult;
    private readonly EvaluationOptions _options;

    private ContractSchemaValidator()
    {
        _cartonDetected = Build(SpecResources.ReadText(SpecResources.CartonDetectedSchema));
        _printJobResult = Build(SpecResources.ReadText(SpecResources.PrintJobResultSchema));

        var formats = new FormatRegistry();
        formats.Register(new PredicateFormat("uuid", e => e.ValueKind != JsonValueKind.String || CanonicalUuid().IsMatch(e.GetString()!)));
        formats.Register(new PredicateFormat("date-time", e => e.ValueKind != JsonValueKind.String || CanonicalTimestamp.TryParseRfc3339(e.GetString(), out _)));
        _options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.Hierarchical,
            RequireFormatValidation = true,
            FormatRegistry = formats,
        };
    }

    public static ContractSchemaValidator Instance => Embedded.Value;

    public const string CartonDetectedSchemaName = "carton-detected.v1.schema.json";
    public const string PrintJobResultSchemaName = "print-job-result.v1.schema.json";

    /// <summary>Schema que corresponde al documento, con la regla de tools/verify_package.py.</summary>
    public static string SchemaNameFor(JsonElement document) =>
        document.ValueKind == JsonValueKind.Object
        && document.TryGetProperty("eventType", out var t)
        && t.ValueKind == JsonValueKind.String
        && t.GetString() == "CartonDetected"
            ? CartonDetectedSchemaName
            : PrintJobResultSchemaName;

    public IReadOnlyList<SchemaViolation> Validate(JsonElement document) => Validate(SchemaNameFor(document), document);

    public IReadOnlyList<SchemaViolation> Validate(string schemaName, JsonElement document)
    {
        var schema = schemaName switch
        {
            CartonDetectedSchemaName => _cartonDetected,
            PrintJobResultSchemaName => _printJobResult,
            _ => throw new ArgumentException($"Schema desconocido: {schemaName}.", nameof(schemaName)),
        };

        var results = schema.Evaluate(document, _options);
        var violations = new List<SchemaViolation>();
        if (!results.IsValid)
        {
            Collect(results, violations);
            if (violations.Count == 0)
            {
                // Red de seguridad: un documento inválido nunca pasa como válido aunque el mapeo no
                // encuentre un error hoja (cambio de la biblioteca o del schema).
                violations.Add(new SchemaViolation("schema", string.Empty, "Documento inválido sin error hoja identificable."));
            }
        }

        return violations;
    }

    public bool IsValid(JsonElement document) => Validate(document).Count == 0;

    /// <summary>Lanza si el evento serializado no cumple su schema: emitir un contrato inválido es un defecto.</summary>
    public void EnsureValid(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var errors = Validate(doc.RootElement);
        if (errors.Count > 0)
        {
            throw new ContractViolationException(
                $"El evento no cumple {SchemaNameFor(doc.RootElement)}: {string.Join("; ", errors)}");
        }
    }

    private static void Collect(EvaluationResults node, List<SchemaViolation> sink)
    {
        if (node.Errors is { Count: > 0 })
        {
            foreach (var (keyword, message) in node.Errors)
            {
                if (!PassThroughKeywords.Contains(keyword))
                {
                    sink.Add(new SchemaViolation(keyword, node.InstanceLocation.ToString(), message));
                }
            }
        }

        if (node.Details is null)
        {
            return;
        }

        foreach (var child in node.Details)
        {
            if (child.IsValid)
            {
                continue;
            }

            // El resultado del subschema "if" nunca es un error; los subschemas de not, anyOf, oneOf,
            // contains y additionalProperties se resumen en el error de la propia palabra clave.
            var keyword = ApplicatorOf(child.EvaluationPath.ToString());
            if (keyword == "if" || CollapsingKeywords.Contains(keyword))
            {
                continue;
            }

            Collect(child, sink);
        }
    }

    // Palabras clave cuyo segmento siguiente es un nombre (de propiedad o de definición), no otra palabra clave.
    private static readonly HashSet<string> NameIntroducers = ["properties", "patternProperties", "dependentSchemas", "$defs", "definitions"];

    private static readonly HashSet<string> ArrayApplicators = ["allOf", "anyOf", "oneOf", "prefixItems"];

    /// <summary>
    /// Palabra clave aplicadora que introdujo el último subschema de la ruta de evaluación:
    /// <c>/allOf/3</c> → allOf, <c>/properties/not</c> → properties (una propiedad llamada "not"),
    /// <c>/allOf/3/then/not</c> → not.
    /// </summary>
    private static string ApplicatorOf(string pointer)
    {
        var parts = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        if (parts.Length >= 2 && NameIntroducers.Contains(parts[^2]) && !IsNameSegment(parts, parts.Length - 2))
        {
            return parts[^2];
        }

        if (parts.Length >= 2 && int.TryParse(parts[^1], out _) && ArrayApplicators.Contains(parts[^2]))
        {
            return parts[^2];
        }

        return parts[^1];
    }

    /// <summary>¿El segmento i es un nombre (precedido por una palabra clave que introduce nombres)?</summary>
    private static bool IsNameSegment(string[] parts, int i) =>
        i > 0 && NameIntroducers.Contains(parts[i - 1]) && !IsNameSegment(parts, i - 1);

    private static JsonSchema Build(string text) =>
        JsonSchema.FromText(text, new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalUuid();
}

public sealed class ContractViolationException(string message) : Exception(message);
