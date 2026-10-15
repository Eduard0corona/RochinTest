using System.Diagnostics;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Ownership;

/// <summary>
/// T43 (variante) · El sender de la impresora manual muere a mitad de la escritura de una reimpresión excepcional: un
/// proceso real (CartonPrinting.FaultHost) que se mata. Al arrancar, TX-R0 encuentra el intento excepcional abierto y,
/// en la misma transacción, lo pasa a UNKNOWN (A04) y el job a UNKNOWN con EXCEPTIONAL_REPRINT_UNRESOLVED (X03).
/// </summary>
public sealed class ReprintRestartTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f5reprst", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Spec", "T43")]
    public async Task A_manual_sender_killed_during_a_reprint_write_leaves_the_job_unknown_by_x03()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        await using var printer = PrinterSimulator.Start();
        var job = await s.ReadyJobAsync(960, Ct);
        var row = await s.JobAsync(job, Ct);
        var untilExpired = row.DeadlineAt - DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
        if (untilExpired > TimeSpan.Zero)
        {
            await Task.Delay(untilExpired, Ct);
        }
        await new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics).SweepOnceAsync(Ct);
        Assert.Equal((JobStatus.Expired, JobReason.DeadlinePassed), ((await s.JobAsync(job, Ct)).Status, (await s.JobAsync(job, Ct)).Reason));
        var authorization = await s.ApproveReprintAsync(job, JobStatus.Expired, "EXPIRED_CARTON_HELD", "NONE_PROVEN", Ct);

        using (var host = StartHost(s, printer.Port, authorization, SenderPoint.DuringWrite))
        {
            try
            {
                Assert.True(await WaitForLineAsync(host, $"AT {SenderPoint.DuringWrite}"), "el sender manual no llegó a la escritura");
            }
            finally
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync(Ct);
            }
        }

        var open = (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint);
        Assert.Equal(AttemptOutcome.Sending, open.Outcome);
        Assert.Equal(JobStatus.Expired, (await s.JobAsync(job, Ct)).Status);   // la reimpresión no cambia el estado previo del job
        Assert.True(printer.BytesReceived > 0);
        Assert.Empty(printer.Printed);

        var recovery = await new SenderRecovery(new Txr0Recovery(s.Sql), s.Clock, s.Metrics).RecoverAsync(s.ManualPrinterId, Ct);
        Assert.Equal([job], recovery.ExceptionalJobs);
        var unknown = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.ExceptionalReprintUnresolved), (unknown.Status, unknown.Reason));
        Assert.Equal(AttemptOutcome.Unknown, (await s.AttemptsAsync(job, Ct)).Single(a => a.Kind == AttemptKind.ExceptionalReprint).Outcome);
        var result = (await s.ResultsAsync(job, Ct)).MaxBy(r => r.GetProperty("resultVersion").GetInt32());
        Assert.Equal(("UNKNOWN", false, authorization), (result.GetProperty("outcome").GetString(), result.GetProperty("final").GetBoolean(),
            result.GetProperty("exceptionalReprint").GetProperty("authorizationId").GetInt64()));
        Assert.Equal(1, s.Metrics.Total(MetricNames.ExceptionalReprints, ("result", "UNRESOLVED")));
    }

    private static Process StartHost(StationScenario s, int printerPort, long authorization, SenderPoint point)
    {
        var dll = Path.Combine(AppContext.BaseDirectory.Replace("CartonPrinting.FaultTests", "CartonPrinting.FaultHost", StringComparison.Ordinal),
            "CartonPrinting.FaultHost.dll");
        Assert.True(File.Exists(dll), $"No existe {dll}: compilar la solución.");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[]
                 {
                     dll, "reprint", s.Db.AppConnectionString, printerPort.ToString(System.Globalization.CultureInfo.InvariantCulture), s.ManualPrinterId,
                     s.ManualLease.OwnerInstanceId, s.ManualLease.FencingToken.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     authorization.ToString(System.Globalization.CultureInfo.InvariantCulture), point.ToString(),
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
}
