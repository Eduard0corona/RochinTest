using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Recovery;
using CartonPrinting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.FaultTests.Ownership;

/// <summary>
/// Arranque ordenado del EdgeHost (ESTRATEGIA_RECUPERACION.md, sección 1) con las piezas reales: propiedad de la impresora
/// y TX-R0, estado de la estación, barrido, suscripción OPC UA contra el PLC simulado y primer LabelingReady, contra SQL
/// Server real. Antes de suscribirse, el SENDING que dejó la caída ya es UNKNOWN y el READY vencido ya es EXPIRED;
/// LabelingReady se escribe solo al final.
/// </summary>
public sealed class StartupOrderTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f5start", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Startup_recovers_and_sweeps_before_subscribing_and_raises_labeling_ready_last()
    {
        // Presupuesto largo para que la carga de la máquina no venza el claim del job que queda en SENDING; el job que
        // debe vencer se registra con un DetectionTimestamp que le deja poco más de un segundo.
        var s = await StationScenario.CreateAsync(_db, r => r with { TransitBudgetMs = 60_000 }, Ct, TimeProvider.System);
        var (sending, _) = await s.SendingJobAsync(600, Ct);   // la caída dejó este envío en SENDING
        var registered = await s.RegisterAsync(s.Cycle(601, detectionTimestamp: DateTime.UtcNow.AddMilliseconds(-58_000)), ct: Ct);
        var expired = registered.PrintJobId ?? throw new InvalidOperationException($"El ciclo 601 quedó {registered.FinalStatus}.");
        await Task.Delay((await s.JobAsync(expired, Ct)).DeadlineAt - DateTime.UtcNow + TimeSpan.FromMilliseconds(200), Ct);

        await using var h = await OpcHarness.StartAsync(s.StationId, new EdgeClock(TimeProvider.System));
        using var metrics = new CartonMetrics();
        var options = new IngestHostOptions
        {
            SiteId = StationScenario.SiteId,
            StationId = s.StationId,
            LabelSerialPrefix = s.SerialPrefix,
            LabelingReadyIntervalMs = 100,
            Opc = new OpcIngestOptions
            {
                EndpointUrl = $"opc.tcp://localhost:{h.ServerPort}/CartonPlc",
                RecordNodeId = h.Server.Nodes.RecordNodeId,
                LabelingReadyNodeId = h.Server.Nodes.LabelingReadyNodeId,
                SecurityMode = "None",
                PkiRoot = Path.Combine(Path.GetTempPath(), "cartonprinting-pki", Guid.NewGuid().ToString("N")[..8], "host"),
                AutoAcceptUntrustedCertificates = true,
            },
        };

        var startup = new StartupCoordinator(TimeProvider.System);
        var seen = new List<string>();
        var ownership = new PrinterOwnership(s.PrinterId, s.OwnerInstanceId, "host-a", s.Leases, s.Clock, s.Metrics, 600_000, 2_000,
            lockDirectory: Path.Combine(Path.GetTempPath(), "cp-locks-" + Guid.NewGuid().ToString("N")));
        var runner = new OwnedPrinterRunner(ownership, new SenderRecovery(new Txr0Recovery(s.Sql), s.Clock, s.Metrics), s.Clock);
        using var ingest = new IngestHostedService(options, s.Sql, s.Clock, metrics, NullLoggerFactory.Instance, coordinated: true);
        var sweep = new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics);

        startup.Add(StartupStage.LabelingReadyLow, "LabelingReady sin escribir", _ =>
        {
            seen.Add($"inicio: LabelingReady={h.Server.Nodes.LabelingReady}");
            return Task.CompletedTask;
        });
        startup.Add(StartupStage.Ownership, "lock y lease", runner.AcquireAsync);
        startup.Add(StartupStage.SendingRecovered, "TX-R0", runner.RecoverAsync);
        startup.Add(StartupStage.StateLoaded, "estado", ingest.LoadStateAsync);
        startup.Add(StartupStage.Swept, "barrido", sweep.SweepOnceAsync);
        startup.Add(StartupStage.Subscribed, "suscripción", async ct =>
        {
            seen.Add($"antes de suscribir: {(await s.JobAsync(sending, ct)).Status} {(await s.JobAsync(expired, ct)).Status} LabelingReady={h.Server.Nodes.LabelingReady}");
            await ingest.SubscribeAsync(ct);
        });
        startup.Add(StartupStage.LabelingReady, "primer latido", ingest.FirstBeatAsync);

        await ingest.StartAsync(Ct);
        try
        {
            await startup.RunAsync(Ct);
            Assert.Equal(
                ["Ownership:lock y lease", "SendingRecovered:TX-R0", "StateLoaded:estado", "Swept:barrido", "Subscribed:suscripción", "LabelingReady:primer latido"],
                startup.Log.Skip(1));
            Assert.Equal(["inicio: LabelingReady=False", $"antes de suscribir: {JobStatus.Unknown} {JobStatus.Expired} LabelingReady=False"], seen);
            Assert.Equal(JobReason.RestartDuringSend, (await s.JobAsync(sending, Ct)).Reason);
            Assert.True(h.Server.Nodes.LabelingReady);
            Assert.True(runner.Owned);
        }
        finally
        {
            await ingest.StopAsync(CancellationToken.None);
            ownership.Dispose();
        }
    }
}
