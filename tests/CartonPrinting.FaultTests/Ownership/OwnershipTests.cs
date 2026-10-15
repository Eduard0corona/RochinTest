using System.Diagnostics;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Ownership;

/// <summary>
/// Propiedad de la impresora (I11, ADR 0010): dos instancias del EdgeHost (T27) y un agente suspendido después de
/// validar su token (T36), contra SQL Server real, la impresora simulada por TCP y un proceso real que toma el lock.
/// </summary>
public sealed class OwnershipTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f5own", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewLockDirectory() => Path.Combine(Path.GetTempPath(), "cp-locks-" + Guid.NewGuid().ToString("N"));

    private static SenderRecovery Recovery(StationScenario s) => new(new Txr0Recovery(s.Sql), s.Clock, s.Metrics);

    /// <summary>
    /// T27 · Mismo host: otro proceso real tiene el lock de sistema operativo de la impresora. Esta instancia no lo
    /// obtiene, alerta en cada intento y nunca invoca el sender (cero envíos). Cuando el otro proceso muere, el sistema
    /// operativo libera el lock y esta instancia toma la impresora y envía.
    /// </summary>
    [Fact]
    [Trait("Spec", "T27")]
    public async Task A_second_instance_on_the_same_host_gets_no_os_lock_and_sends_nothing()
    {
        // Plazo largo: los reintentos avanzan el reloj de prueba la duración del lease (600 s) cada vez.
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 7_200_000 }, Ct);
        var directory = NewLockDirectory();
        using var other = StartLockHost(s.PrinterId, directory);
        try
        {
            Assert.True(await WaitForLineAsync(other, "LOCKED"), "el otro proceso no tomó el lock");
            var job = await s.ReadyJobAsync(710, Ct);
            await using var rig = PrintRig.Create(s);
            var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
                lockDirectory: directory);
            var runner = new OwnedPrinterRunner(ownership, Recovery(s), s.Clock);

            Assert.False(await runner.AcquireAsync(Ct));
            Assert.Equal(1, s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "os_lock")));

            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var run = runner.RunAsync(async (_, ct) =>
            {
                await rig.Sender.SendNextAsync(ct);
                sent.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }, stop.Token);

            // Reintentos con el otro proceso vivo: cada uno alerta y ninguno envía.
            for (var i = 0; i < 3; i++)
            {
                await AdvanceUntilAsync(s, () => s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "os_lock")) >= i + 2);
            }

            Assert.Equal(0, runner.SenderRuns);
            Assert.Equal(0L, rig.Printer.BytesReceived);
            Assert.Equal(JobStatus.Ready, (await s.JobAsync(job, Ct)).Status);

            other.Kill(entireProcessTree: true);
            await other.WaitForExitAsync(Ct);
            await AdvanceUntilAsync(s, () => sent.Task.IsCompleted);
            Assert.Equal(1, runner.SenderRuns);
            Assert.Equal(1, rig.Printer.TransmissionsOf((await s.JobAsync(job, Ct)).LabelSerial));

            await stop.CancelAsync();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
        }
        finally
        {
            if (!other.HasExited)
            {
                other.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// T27 · Hosts distintos (cada instancia con su propio lock de sistema operativo): la segunda no obtiene el lease,
    /// alerta y nunca invoca el sender; la impresora recibe solo lo que envía la primera.
    /// </summary>
    [Fact]
    [Trait("Spec", "T27")]
    public async Task A_second_instance_on_another_host_gets_no_lease_and_sends_nothing()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(720, Ct);
        await using var rig = PrintRig.Create(s);
        var a = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: NewLockDirectory());
        var b = new PrinterOwnership(s.PrinterId, "edgehost-b", "host-b", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: NewLockDirectory());
        var runnerB = new OwnedPrinterRunner(b, Recovery(s), s.Clock);

        Assert.NotNull(await a.TryAcquireAsync(Ct));
        Assert.False(await runnerB.AcquireAsync(Ct));
        Assert.False(await runnerB.AcquireAsync(Ct));
        Assert.Equal(2, s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "lease")));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var runB = runnerB.RunAsync((_, _) => throw new InvalidOperationException("B no debe enviar"), stop.Token);
        Assert.Equal(AttemptOutcome.Transmitted, (await rig.SendAsync(job, Ct)).Write!.AttemptOutcome);
        await stop.CancelAsync();
        await runB.ContinueWith(_ => { }, TaskScheduler.Default);

        Assert.Equal(0, runnerB.SenderRuns);
        Assert.Single(rig.Printer.Received);
        a.Dispose();
        b.Dispose();
    }

    /// <summary>
    /// T36 · Agente suspendido después de validar su token. A reclama un job (TX-B valida su lease) y se suspende;
    /// vence su lease; B toma la impresora con el token siguiente, ejecuta TX-R0 (el intento de A queda UNKNOWN) y
    /// transmite otro job; A reanuda.
    /// Corrida 1, solo el lease en SQL: la impresora no valida tokens y registra dos transmisores (configuración no
    /// permitida, ADR 0010).
    /// Corrida 2, con el canal de A aislado al cambiar de dueño (proxy que solo deja pasar al dueño activo): la
    /// escritura de A falla y la impresora solo recibe a B.
    /// En las dos, A ya no obtiene el lease (alerta).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "T36")]
    public async Task A_suspended_agent_that_validated_its_token_transmits_unless_its_channel_is_isolated(bool isolate)
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 120_000 }, Ct);
        await using var printer = PrinterSimulator.Start();
        await using var channelA = new TcpProxy("127.0.0.1", printer.Port).Start();
        await using var channelB = new TcpProxy("127.0.0.1", printer.Port).Start();
        var leaseA = (await s.Leases.AcquireAsync(s.PrinterId, s.OwnerInstanceId, "host-a", 5_000, 1_000, s.Clock.UtcNow, Ct))!;
        var pause = new PauseAt(SenderPoint.AfterClaimCommit);
        await using var transportA = Transport(channelA.Port, printer, s);
        var senderA = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, leaseA, transportA, s.Clock, s.Metrics, pause);

        var job1 = await s.ReadyJobAsync(730, Ct);
        var sendA = senderA.SendAsync(job1, Ct);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(JobStatus.Sending, (await s.JobAsync(job1, Ct)).Status);

        // El lease de A vence; B toma la impresora con el token siguiente y ejecuta TX-R0 antes de enviar.
        s.AdvanceMs(6_100);
        var leaseB = (await s.Leases.AcquireAsync(s.PrinterId, "edgehost-b", "host-b", 5_000, 1_000, s.Clock.UtcNow, Ct))!;
        Assert.Equal(leaseA.FencingToken + 1, leaseB.FencingToken);
        if (isolate)
        {
            channelA.AcceptNew = false;
            channelA.SeverAll();
        }

        await Recovery(s).RecoverAsync(s.PrinterId, Ct);
        Assert.Equal(AttemptOutcome.Unknown, Assert.Single(await s.AttemptsAsync(job1, Ct)).Outcome);
        var job2 = await s.ReadyJobAsync(731, Ct);
        await using var transportB = Transport(channelB.Port, printer, s);
        var senderB = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, leaseB, transportB, s.Clock, s.Metrics);
        Assert.Equal(AttemptOutcome.Transmitted, (await senderB.SendAsync(job2, Ct)).Write!.AttemptOutcome);

        pause.Release();
        var resumed = await sendA.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var serial1 = (await s.JobAsync(job1, Ct)).LabelSerial;
        var serial2 = (await s.JobAsync(job2, Ct)).LabelSerial;
        Assert.Equal(1, printer.TransmissionsOf(serial2));
        if (isolate)
        {
            Assert.Equal(0, printer.TransmissionsOf(serial1));   // un solo transmisor
        }
        else
        {
            Assert.True(resumed.WriteInvoked);
            Assert.Equal(1, printer.TransmissionsOf(serial1));   // dos transmisores: la impresora no valida tokens
        }

        // El intento de A sigue UNKNOWN (TX-R0 de B); A ya no obtiene el lease.
        Assert.Equal(AttemptOutcome.Unknown, Assert.Single(await s.AttemptsAsync(job1, Ct)).Outcome);
        Assert.Equal(JobStatus.Unknown, (await s.JobAsync(job1, Ct)).Status);
        using var ownershipA = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 5_000, 1_000,
            lockDirectory: NewLockDirectory());
        Assert.Null(await ownershipA.TryAcquireAsync(Ct));
        Assert.Equal(1, s.Metrics.Total(MetricNames.PrinterOwnerConflicts, ("printer", s.PrinterId), ("cause", "lease")));
    }

    private static TcpPrinterTransport Transport(int port, PrinterSimulator printer, StationScenario s) => new(new PrinterTransportOptions
    {
        Port = port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = 1_000,
    }, s.Provider);

    /// <summary>Avanza el reloj de a poco hasta que se cumpla la condición (los temporizadores del ciclo de propiedad).</summary>
    private static async Task AdvanceUntilAsync(StationScenario s, Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            s.AdvanceMs(100_000);
            await Task.Delay(50, Ct);
        }

        Assert.True(done(), "la condición no se cumplió");
    }

    private static Process StartLockHost(string printerId, string directory)
    {
        var dll = Path.Combine(AppContext.BaseDirectory.Replace("CartonPrinting.FaultTests", "CartonPrinting.FaultHost", StringComparison.Ordinal),
            "CartonPrinting.FaultHost.dll");
        Assert.True(File.Exists(dll), $"No existe {dll}: compilar la solución.");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { dll, "lock", printerId, directory })
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar el proceso del lock.");
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

    /// <summary>Suspende el sender en un punto hasta que la prueba lo libere.</summary>
    private sealed class PauseAt(SenderPoint point) : ISenderFaults
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task OnPointAsync(SenderPoint at, Guid attemptId, CancellationToken ct)
        {
            if (at == point)
            {
                Reached.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
        }
    }
}
