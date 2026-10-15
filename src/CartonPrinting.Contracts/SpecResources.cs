namespace CartonPrinting.Contracts;

/// <summary>Recursos de spec/ incrustados al compilar (ver el .csproj).</summary>
public static class SpecResources
{
    public const string CartonDetectedSchema = "spec/contracts/carton-detected.v1.schema.json";
    public const string PrintJobResultSchema = "spec/contracts/print-job-result.v1.schema.json";
    public const string StateMachine = "spec/contracts/state-machine.json";

    public static Stream Open(string logicalName) =>
        typeof(SpecResources).Assembly.GetManifestResourceStream(logicalName)
        ?? throw new InvalidOperationException($"Recurso incrustado ausente: {logicalName}.");

    public static string ReadText(string logicalName)
    {
        using var reader = new StreamReader(Open(logicalName));
        return reader.ReadToEnd();
    }
}
