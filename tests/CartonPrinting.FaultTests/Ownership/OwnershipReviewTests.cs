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

        await AdvanceUntilAsync(s, () => sent.Task.IsCompleted, stopAdvancing: () => runnerB.Owned);
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


    /// <summary>
    /// Segunda revisión de la Fase 5, MEDIA 1: SQL Server deja de responder a la renovación sin cortar la conexión (proxy
    /// congelado, conexiones del pool). El sender se detiene cuando vence la cobertura del lease (duración menos gracia), sin
    /// esperar el tiempo de espera del comando (30 s): antes de que el lease venza y otra instancia pueda tomarlo.
    /// </summary>
    [Fact]
    public async Task A_renewal_that_hangs_stops_the_sender_before_the_lease_can_expire()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 600_000 }, Ct, TimeProvider.System);
        var parts = new SqlConnectionStringBuilder(s.Db.AppConnectionString).DataSource.Replace("tcp:", string.Empty, StringComparison.Ordinal).Split(',');
        await using var proxy = new TcpProxy(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)).Start();
        var viaProxy = new SqlConnectionStringBuilder(s.Db.AppConnectionString) { DataSource = $"127.0.0.1,{proxy.Port}" };
        var leases = new PrinterLeaseStore(new SqlDb(new DatabaseOptions { ConnectionString = viaProxy.ConnectionString }));
        using var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", leases, s.Clock, s.Metrics, 1_500, 300,
            lockDirectory: NewLockDirectory());
        var runner = new OwnedPrinterRunner(ownership, Recovery(s), s.Clock);
        Assert.True(await runner.AcquireAsync(Ct));
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync(async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                stopped.TrySetResult();
            }
        }, stop.Token);
        try
        {
            await Task.Delay(1_000, Ct);   // al menos una renovación por el proxy
            proxy.Freeze();
            var frozenAt = DateTime.UtcNow;
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            var elapsed = DateTime.UtcNow - frozenAt;
            Assert.True(elapsed < TimeSpan.FromSeconds(3), $"el sender se detuvo {elapsed.TotalMilliseconds:F0} ms después de congelar SQL");
            Assert.Equal(1, s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "lease_lost")));
        }
        finally
        {
            proxy.Resume();
            await stop.CancelAsync();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Segunda revisión de la Fase 5, MEDIA 1 (efecto inverso): un corte breve de SQL Server durante una renovación no detiene
    /// el sender mientras el lease lo cubre: la renovación se reintenta y el job siguiente se imprime sin alerta.
    /// </summary>
    [Fact]
    public async Task A_transient_renewal_error_does_not_stop_the_sender()
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
        using var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", leases, s.Clock, s.Metrics, 3_000, 500,
            lockDirectory: NewLockDirectory());
        var runner = new OwnedPrinterRunner(ownership, Recovery(s), s.Clock);
        Assert.True(await runner.AcquireAsync(Ct));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync((lease, ct) =>
            new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, lease, transport, s.Clock, s.Metrics)
                .RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, ct), stop.Token);
        try
        {
            await Task.Delay(1_200, Ct);   // la renovación siguiente cae durante el corte
            proxy.AcceptNew = false;
            proxy.SeverAll();
            await Task.Delay(400, Ct);
            proxy.AcceptNew = true;
            await Task.Delay(2_500, Ct);

            var job = await s.ReadyJobAsync(770, Ct);
            var serial = (await s.JobAsync(job, Ct)).LabelSerial;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (printer.TransmissionsOf(serial) == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "el job no se imprimió después del corte breve");
                await Task.Delay(100, Ct);
            }

            Assert.Equal(0, s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "lease_lost")));
            Assert.Equal(1, runner.SenderRuns);
        }
        finally
        {
            await stop.CancelAsync();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Segunda revisión de la Fase 5, MEDIA 2: el sender termina con un error fuera de la escritura (por ejemplo, SQL al
    /// prepararlo) con un intento en SENDING. El ciclo de propiedad no termina: vuelve a tomar la impresora, ejecuta TX-R0 (el
    /// SENDING queda UNKNOWN) y vuelve a correr el sender.
    /// </summary>
    [Fact]
    public async Task An_error_in_the_sender_does_not_end_the_ownership_cycle_and_tx_r0_runs_before_it_runs_again()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 7_200_000 }, Ct);
        using var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: NewLockDirectory());
        var runner = new OwnedPrinterRunner(ownership, Recovery(s), s.Clock);
        Assert.True(await runner.AcquireAsync(Ct));
        var inFlight = await s.ReadyJobAsync(780, Ct);
        string? seenAtSecondRun = null;
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = runner.RunAsync(async (lease, ct) =>
        {
            if (runner.SenderRuns == 1)
            {
                Assert.True((await s.Claims.TryClaimAsync(inFlight, lease, ct))!.Claimed);
                throw new InvalidOperationException("Error simulado al preparar el sender");
            }

            var row = await s.JobAsync(inFlight, ct);
            seenAtSecondRun = $"{row.Status}/{row.Reason}";
            resumed.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, stop.Token);

        await AdvanceUntilAsync(s, () => resumed.Task.IsCompleted || run.IsFaulted);
        Assert.False(run.IsFaulted, run.Exception?.GetBaseException().Message);
        Assert.Equal($"{JobStatus.Unknown}/{JobReason.RestartDuringSend}", seenAtSecondRun);
        Assert.Equal(2, runner.SenderRuns);
        await stop.CancelAsync();
        await run.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// Segunda revisión de la Fase 5, MEDIA 3 y BAJA 1: otra instancia tomó el lease entre las etapas 2 y 3 y tiene un intento en
    /// SENDING. TX-R0 de esta instancia no corre y no toca ese intento; y el reintento de la etapa 3 vuelve a fallar, no la da
    /// por terminada sin TX-R0.
    /// </summary>
    [Fact]
    [Trait("Spec", "T27")]
    public async Task Tx_r0_does_not_run_after_the_lease_was_taken_by_another_instance()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 7_200_000 }, Ct);
        using var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: NewLockDirectory());
        var runner = new OwnedPrinterRunner(ownership, Recovery(s), s.Clock);
        await runner.AcquireOrFailAsync(Ct);

        s.AdvanceMs(603_000);   // el lease de esta instancia venció; otra lo toma y reclama un job
        var intruder = (await s.Leases.AcquireAsync(s.PrinterId, "edgehost-b", "host-b", 600_000, 2_000, s.Clock.UtcNow, Ct))!;
        var job = await s.ReadyJobAsync(790, Ct);
        var claim = (await s.Claims.TryClaimAsync(job, intruder, Ct))!;
        Assert.True(claim.Claimed, claim.Rejection.ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RecoverAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RecoverAsync(Ct));
        Assert.Equal(AttemptOutcome.Sending, Assert.Single(await s.AttemptsAsync(job, Ct)).Outcome);
        Assert.Equal(JobStatus.Sending, (await s.JobAsync(job, Ct)).Status);
    }

    /// <summary>
    /// Segunda revisión de la Fase 5, MEDIA 3: la liberación del lease al detener. Quien no es su dueño (u otro token) no lo
    /// cambia; la del dueño lo deja vencido y otra instancia lo toma en el acto, con el token siguiente.
    /// </summary>
    [Fact]
    [Trait("Spec", "T27")]
    public async Task Only_the_owner_releases_the_lease_and_the_next_instance_takes_it_at_once()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        using var a = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: NewLockDirectory());
        var lease = (await a.TryAcquireAsync(Ct))!;

        Assert.False(await s.Leases.ReleaseAsync(s.PrinterId, "edgehost-b", lease.FencingToken, 2_000, s.Clock.UtcNow, Ct));
        Assert.False(await s.Leases.ReleaseAsync(s.PrinterId, s.OwnerInstanceId, lease.FencingToken + 1, 2_000, s.Clock.UtcNow, Ct));
        Assert.Null(await s.Leases.AcquireAsync(s.PrinterId, "edgehost-b", "host-b", 600_000, 2_000, s.Clock.UtcNow, Ct));

        await a.ReleaseAsync(Ct);
        var next = await s.Leases.AcquireAsync(s.PrinterId, "edgehost-b", "host-b", 600_000, 2_000, s.Clock.UtcNow, Ct);
        Assert.NotNull(next);
        Assert.Equal(lease.FencingToken + 1, next.FencingToken);
    }

    private static TcpPrinterTransport Transport(int port, PrinterSimulator printer, StationScenario s) => new(new PrinterTransportOptions
    {
        Port = port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = 1_000,
    }, s.Provider);

    /// <summary>
    /// Avanza el reloj de prueba de a poco hasta que se cumpla la condición (los temporizadores del ciclo de propiedad). Con
    /// <paramref name="stopAdvancing"/> deja de avanzarlo cuando se cumple, para no vencer el plazo de los jobs mientras el
    /// sender trabaja (segunda revisión de la Fase 5, BAJA 5).
    /// </summary>
    private static async Task AdvanceUntilAsync(StationScenario s, Func<bool> done, Func<bool>? stopAdvancing = null)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            if (stopAdvancing?.Invoke() != true)
            {
                s.AdvanceMs(100_000);
            }

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
