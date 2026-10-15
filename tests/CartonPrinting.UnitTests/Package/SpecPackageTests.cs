using System.Diagnostics;
using System.Text;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Package;

/// <summary>
/// T28 · Coherencia del paquete. Criterio: <c>tools/verify_package.py</c> termina con código 0 y
/// las propiedades no verificables quedan listadas como pendientes de integración. La compuerta de
/// CI también lo ejecuta como paso propio; esta prueba lo deja trazado en xUnit.
/// </summary>
[Trait("Spec", "T28")]
public sealed class SpecPackageTests
{
    [Fact]
    public void Verify_package_passes_with_15_groups_and_lists_pending_integration()
    {
        var (exitCode, output) = RunPython(Path.Combine(RepoPaths.Spec, "tools", "verify_package.py"));

        Assert.True(exitCode == 0, output);
        Assert.Contains("15 grupos OK, 0 errores", output, StringComparison.Ordinal);
        Assert.Contains("PENDIENTE DE INTEGRACI", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_artifacts_are_up_to_date()
    {
        var (exitCode, output) = RunPython(Path.Combine(RepoPaths.Spec, "tools", "generate_artifacts.py"), "--check");

        Assert.True(exitCode == 0, output);
    }

    private static (int ExitCode, string Output) RunPython(string script, params string[] args)
    {
        var psi = new ProcessStartInfo(PythonReference.Interpreter)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = RepoPaths.Root,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add("-B");
        psi.ArgumentList.Add(script);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(TimeSpan.FromMinutes(5)), "verify_package.py no terminó en 5 minutos");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
