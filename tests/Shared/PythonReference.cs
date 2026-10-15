using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CartonPrinting.Testing;

/// <summary>
/// Ejecuta tests/Shared/reference_oracle.py, que llama a las funciones de referencia de
/// spec/tools/verify_package.py. Requiere Python 3.10+ con jsonschema (el mismo entorno que
/// exige la compuerta para correr el verificador).
/// </summary>
public static class PythonReference
{
    public static string Interpreter => OperatingSystem.IsWindows() ? "py" : "python3";

    public static JsonArray Run(string command, JsonArray input)
    {
        var script = Path.Combine(RepoPaths.Root, "tests", "Shared", "reference_oracle.py");
        var psi = new ProcessStartInfo(Interpreter)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-B"); // sin bytecode: no se escribe nada en spec/tools
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(RepoPaths.Spec);
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {Interpreter}.");
        process.StandardInput.Write(input.ToJsonString());
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("El oráculo de Python no terminó.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"El oráculo de Python falló ({process.ExitCode}): {stderr.Result}");
        }

        return JsonNode.Parse(stdout.Result)!.AsArray();
    }

    public static string ToJson(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
