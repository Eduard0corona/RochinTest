using System.Diagnostics;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.PrintAgent;

/// <summary>
/// T07 · Reinicio del servicio en SENDING. El sender corre en un proceso aparte (CartonPrinting.FaultHost) que
/// se mata en tres puntos: tras el commit del claim y antes de escribir; durante la escritura; tras escribir y
/// antes de TX-C. Al arrancar, TX-R0 pasa intento y job a UNKNOWN con RESTART_DURING_SEND, nunca a READY. Cero o
/// una etiqueta según el punto, nunca dos; ninguna transacción SQL abierta durante el I/O. El primer punto da un
/// UNKNOWN falso (no se escribió nada): es el costo aceptado de no poder distinguirlo desde SQL.
/// </summary>
[Trait("Spec", "T07")]
public sealed class RestartDuringSendTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t07", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SenderPoint.AfterClaimCommit)]
    [InlineData(SenderPoint.DuringWrite)]
    [InlineData(SenderPoint.AfterWriteBeforeResult)]
    public async Task Killed_sender_leaves_unknown_restart_during_send_and_never_two_labels(SenderPoint point)
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 120_000 }, Ct, TimeProvider.System);
        await using var printer = PrinterSimulator.Start();
        var job = await s.ReadyJobAsync(10, Ct);
        var row = await s.JobAsync(job, Ct);

        using (var host = StartHost(s, printer.Port, job, point))
        {
            var marker = await WaitForLineAsync(host, $"AT {point}");
            Assert.True(marker, $"El host no llegó a {point}.");
            Assert.Equal(0, await OpenTransactionsAsync(s));   // ninguna transacción abierta durante el I/O (I20)
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync(Ct);
        }

        Assert.Equal(JobStatus.Sending, (await s.JobAsync(job, Ct)).Status);
        Assert.Equal(AttemptOutcome.Sending, Assert.Single(await s.AttemptsAsync(job, Ct)).Outcome);

        // Efecto físico según el punto: cero o una etiqueta, nunca dos.
        switch (point)
        {
            case SenderPoint.AfterClaimCommit:
                Assert.Equal((0L, 0), (printer.BytesReceived, printer.Received.Count));
                break;
            case SenderPoint.DuringWrite:
                await WaitAsync(() => printer.BytesReceived > 0);
                Assert.Empty(printer.Received);
                Assert.True(printer.BytesReceived < System.Text.Encoding.UTF8.GetByteCount(row.Payload));
                break;
            case SenderPoint.AfterWriteBeforeResult:
                Assert.Equal(1, printer.TransmissionsOf(row.LabelSerial));
                break;
        }

        // Arranque: TX-R0.
        var recovery = await new SenderRecovery(s.Txr0, s.Clock, s.Metrics).RecoverAsync(s.PrinterId, Ct);
        Assert.Equal([job], recovery.OrdinaryJobs);
        var after = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.RestartDuringSend), (after.Status, after.Reason));
        Assert.Equal(AttemptOutcome.Unknown, Assert.Single(await s.AttemptsAsync(job, Ct)).Outcome);
        Assert.Equal(1, s.Metrics.Total(MetricNames.RestartRecoveredSending, ("printer", s.PrinterId)));
        var result = Assert.Single(await s.ResultsAsync(job, Ct));
        Assert.Equal(("UNKNOWN", "RESTART_DURING_SEND"), (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString()));

        // Nada vuelve a READY ni se reenvía.
        await using var rig = PrintRig.Create(s);
        Assert.Null(await rig.Sender.SendNextAsync(Ct));
        Assert.True(printer.TransmissionsOf(row.LabelSerial) <= 1);
    }

    private static Process StartHost(StationScenario s, int printerPort, Guid job, SenderPoint point)
    {
        var dll = Path.Combine(AppContext.BaseDirectory.Replace("CartonPrinting.FaultTests", "CartonPrinting.FaultHost", StringComparison.Ordinal),
            "CartonPrinting.FaultHost.dll");
        Assert.True(File.Exists(dll), $"No existe {dll}: compilar la solución.");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     dll, s.Db.AppConnectionString, printerPort.ToString(System.Globalization.CultureInfo.InvariantCulture), s.PrinterId, s.OwnerInstanceId,
                     s.Lease.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture), job.ToString(), point.ToString(),
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar el host de fallas.");
    }

    private static async Task<bool> WaitForLineAsync(Process host, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Sin aviso a tiempo.
        }

        return false;
    }

    /// <summary>Transacciones abiertas por las sesiones del host de fallas (vistas con el login administrador).</summary>
    private static async Task<int> OpenTransactionsAsync(StationScenario s)
    {
        await using var conn = new SqlConnection(s.Db.AdminConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new SqlCommand("""
            SELECT COUNT(*) FROM sys.dm_exec_sessions AS es
            INNER JOIN sys.dm_tran_session_transactions AS st ON st.session_id = es.session_id
            WHERE es.program_name = N'CartonPrinting.FaultHost';
            """, conn);
        return (int)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }
}
