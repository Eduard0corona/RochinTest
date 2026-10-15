using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Ownership;

/// <summary>
/// Regresiones de la revisión independiente de la Fase 5 sobre el ciclo de propiedad de la impresora, contra SQL Server
/// real y la impresora simulada: la segunda instancia cierra los SENDING heredados antes de enviar (M2, T27), un lease
/// perdido detiene el sender y TX-R0 corre antes de volver a enviar (M2) y una renovación que falla por SQL cuenta como
/// lease perdido (A2).
/// </summary>
public sealed class OwnershipReviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f5own2", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewLockDirectory() => Path.Combine(Path.GetTempPath(), "cp-locks-" + Guid.NewGuid().ToString("N"));

    private static SenderRecovery Recovery(StationScenario s) => new(new Txr0Recovery(s.Sql), s.Clock, s.Metrics);

    /// <summary>
    /// T27 · La primera instancia reclamó un job y murió con el intento en SENDING; su lease sigue vigente. La segunda (otro
    /// dueño) no obtiene el lease y alerta. Cuando vence, lo toma y ejecuta TX-R0: el SENDING heredado ya es UNKNOWN antes de
    /// que su sender envíe nada, y la impresora recibe solo el job siguiente.
    /// </summary>
    [Fact]
    [Trait("Spec", "T27")]
    public async Task A_second_instance_recovers_the_inherited_sending_before_it_sends()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 7_200_000 }, Ct);
        await using var printer = PrinterSimulator.Start();
        await using var transport = Transport(printer.Port, printer, s);
        var (inherited, _) = await s.SendingJobAsync(740, Ct);
        var next = await s.ReadyJobAsync(741, Ct);
        using var b = new PrinterOwnership(s.PrinterId, "edgehost-b", "host-b", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: NewLockDirectory());
        var runnerB = new OwnedPrinterRunner(b, Recovery(s), s.Clock);
        Assert.False(await runnerB.AcquireAsync(Ct));

        string? seenAtFirstRun = null;
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runnerB.RunAsync(async (lease, ct) =>
        {
            var row = await s.JobAsync(inherited, ct);
            seenAtFirstRun ??= $"{row.Status}/{row.Reason}";
            await new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, lease, transport, s.Clock, s.Metrics).SendAsync(next, ct);
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, stop.Token);

        await AdvanceUntilAsync(s, () => sent.Task.IsCompleted);
        Assert.Equal($"{JobStatus.Unknown}/{JobReason.RestartDuringSend}", seenAtFirstRun);
        Assert.Equal(1, printer.TransmissionsOf((await s.JobAsync(next, Ct)).LabelSerial));
        Assert.Equal(0, printer.TransmissionsOf((await s.JobAsync(inherited, Ct)).LabelSerial));
        Assert.True(s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "lease")) >= 1);

        await stop.CancelAsync();
        await run.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// Otra instancia toma el lease mientras el sender de esta tiene un intento en SENDING (por ejemplo, después de una pausa
    /// larga). La renovación lo detecta: alerta, el sender se detiene y no vuelve a correr hasta tomar de nuevo la impresora.
    /// Al tomarla, TX-R0 cierra ese SENDING antes de que el sender vuelva a enviar.
    /// </summary>
    [Fact]
    public async Task A_lost_lease_stops_the_sender_and_tx_r0_runs_before_it_sends_again()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 7_200_000 }, Ct);
        using var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: NewLockDirectory());
        var runner = new OwnedPrinterRunner(ownership, Recovery(s), s.Clock);
        Assert.True(await runner.AcquireAsync(Ct));
        var inFlight = await s.ReadyJobAsync(750, Ct);

        var runs = new List<string>();
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync(async (lease, ct) =>
        {
            if (runs.Count == 0)
            {
                runs.Add("primera");
                if ((await s.Claims.TryClaimAsync(inFlight, lease, ct))?.Claimed == true)
                {
                    claimed.TrySetResult();   // queda en SENDING, como un envío en curso
                }

                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                var row = await s.JobAsync(inFlight, ct);
                runs.Add($"{row.Status}/{row.Reason}");
                resumed.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        }, stop.Token);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await using (var conn = new SqlConnection(s.Db.AppConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var steal = new SqlCommand("""
                UPDATE dbo.PrinterLease
                SET OwnerInstanceId = N'intruso', OwnerHost = N'host-x', FencingToken = FencingToken + 1, AcquiredAt = @now,
                    LeaseUntil = DATEADD(MILLISECOND, 900000, @now)
                WHERE PrinterId = @p;
                """, conn);
            steal.Parameters.Add("@now", System.Data.SqlDbType.DateTime2).Value = s.Clock.UtcNow;
            steal.Parameters.AddWithValue("@p", s.PrinterId);
            await steal.ExecuteNonQueryAsync(Ct);
        }

        await AdvanceUntilAsync(s, () => resumed.Task.IsCompleted);
        Assert.Equal(["primera", $"{JobStatus.Unknown}/{JobReason.RestartDuringSend}"], runs);
        Assert.Equal(1, s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "lease_lost")));
        Assert.Equal(2, runner.SenderRuns);

        await stop.CancelAsync();
        await run.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// A2: SQL Server deja de responder a la renovación del lease (proxy TCP cortado solo para el lease). La renovación fallida
    /// cuenta como lease perdido: alerta y el sender se detiene; mientras tanto no se envía nada. Al volver SQL, el ciclo de
    /// propiedad toma de nuevo la impresora, ejecuta TX-R0 y el job pendiente se imprime.
    /// </summary>
    [Fact]
    public async Task A_renewal_that_fails_counts_as_a_lost_lease_and_printing_resumes_when_sql_returns()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 600_000 }, Ct, TimeProvider.System);
        await using var printer = PrinterSimulator.Start();
        await using var transport = Transport(printer.Port, printer, s);
        var parts = new SqlConnectionStringBuilder(s.Db.AppConnectionString).DataSource.Replace("tcp:", string.Empty, StringComparison.Ordinal).Split(',');
        await using var proxy = new TcpProxy(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)).Start();
        var viaProxy = new SqlConnectionStringBuilder(s.Db.AppConnectionString)
        {
            DataSource = $"127.0.0.1,{proxy.Port}", ConnectTimeout = 2, Pooling = false, ConnectRetryCount = 0,
        };
        var leases = new PrinterLeaseStore(new SqlDb(new DatabaseOptions { ConnectionString = viaProxy.ConnectionString }));
        using var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", leases, s.Clock, s.Metrics, 1_500, 300,
            lockDirectory: NewLockDirectory());
        var runner = new OwnedPrinterRunner(ownership, Recovery(s), s.Clock);
        Assert.True(await runner.AcquireAsync(Ct));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync((lease, ct) =>
            new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, lease, transport, s.Clock, s.Metrics)
                .RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, ct), stop.Token);
        try
        {
            var first = await s.ReadyJobAsync(760, Ct);
            var firstSerial = (await s.JobAsync(first, Ct)).LabelSerial;
            await WaitAsync(() => printer.TransmissionsOf(firstSerial) == 1, "primera etiqueta");

            proxy.AcceptNew = false;
            proxy.SeverAll();
            await WaitAsync(() => s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "lease_lost")) >= 1, "lease perdido");
            var second = await s.ReadyJobAsync(761, Ct);
            var secondSerial = (await s.JobAsync(second, Ct)).LabelSerial;
            await Task.Delay(1_500, Ct);
            Assert.Equal(0, printer.TransmissionsOf(secondSerial));   // sin propiedad no se envía

            proxy.AcceptNew = true;
            await WaitAsync(() => printer.TransmissionsOf(secondSerial) == 1, "segunda etiqueta al volver SQL");
            Assert.Equal(0, s.Metrics.Total(MetricNames.ClaimRejected, ("cause", nameof(ClaimRejection.LeaseInvalid))));
            Assert.True(runner.SenderRuns >= 2);
        }
        finally
        {
            await stop.CancelAsync();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private static TcpPrinterTransport Transport(int port, PrinterSimulator printer, StationScenario s) => new(new PrinterTransportOptions
    {
        Port = port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = 1_000,
    }, s.Provider);

    /// <summary>Avanza el reloj de prueba de a poco hasta que se cumpla la condición (los temporizadores del ciclo de propiedad).</summary>
    private static async Task AdvanceUntilAsync(StationScenario s, Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            s.AdvanceMs(100_000);
            await Task.Delay(50, Ct);
        }

        Assert.True(done(), "la condición no se cumplió");
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"No se cumplió en 60 s: {what}.");
            await Task.Delay(100, Ct);
        }
    }
}
