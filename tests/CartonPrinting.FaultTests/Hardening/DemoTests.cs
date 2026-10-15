using System.Diagnostics;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// El guion de docs/DEMO.md, ejecutado: CartonPrinting.DemoHost siembra una estación, arranca los simuladores (PLC por OPC UA,
/// impresora, ERP, aplicador y escáner) y lanza el EdgeHost real en su propio proceso, configurado solo por variables de
/// entorno. Las cajas pasan por el arranque ordenado, la ingesta, el ERP, el sender y la evidencia hasta CONFIRMED (VERIFIED),
/// con una etiqueta por caja. Es la prueba de composición del EdgeHost de punta a punta. SQL Server real (Testcontainers).
/// </summary>
public sealed class DemoTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f7demo", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_demo_runs_the_full_flow_through_the_real_edge_host_process()
    {
        var ct = TestContext.Current.CancellationToken;
        var dll = AppContext.BaseDirectory.Replace("CartonPrinting.FaultTests", "CartonPrinting.DemoHost", StringComparison.Ordinal) + "CartonPrinting.DemoHost.dll";
        Assert.True(File.Exists(dll), $"No existe {dll}: compilar la solución.");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { dll, "--connection", _db.AppConnectionString, "--boxes", "10", "--interval-ms", "500", "--timeout-s", "90" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var demo = Process.Start(psi)!;
        var output = demo.StandardOutput.ReadToEndAsync(ct);
        var errors = demo.StandardError.ReadToEndAsync(ct);
        try
        {
            await demo.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(4), ct);
        }
        finally
        {
            if (!demo.HasExited)
            {
                demo.Kill(entireProcessTree: true);
            }
        }

        var text = await output + await errors;
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        Assert.True(demo.ExitCode == 0, $"La demo terminó con {demo.ExitCode}:\n{text}");
        Assert.Contains("DEMO OK", text, StringComparison.Ordinal);
    }
}
