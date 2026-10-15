using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.FaultTests.Opc;

/// <summary>
/// El servicio alojado de la ingesta, como lo arranca el EdgeHost: cliente OPC UA real contra el PLC
/// simulado, registro en SQL Server real y latido LabelingReady escrito en el PLC (true con la estación
/// habilitada, false al bloquearla).
/// </summary>
public sealed class IngestHostTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("ingesthost", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Hosted_ingest_registers_records_and_drives_labeling_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        await using var h = await OpcHarness.StartAsync(s.StationId, new EdgeClock(TimeProvider.System));
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
        using var metrics = new CartonMetrics();
        using var host = new IngestHostedService(options, s.Sql, new EdgeClock(TimeProvider.System), metrics, NullLoggerFactory.Instance);
        await host.StartAsync(ct);
        try
        {
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), ct);

            h.Server.Nodes.Publish(new LatchedRecord { PlcEpoch = s.Epoch, CycleId = 77, WorkOrderId = StationScenario.WorkOrder, DetectionTimestamp = DateTime.UtcNow });
            await WaitAsync(async () => await s.DetectionAsync(77, ct) is not null, ct);

            await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, "OPERATOR", "supervisor.ramos", null, s.Clock.UtcNow, ct);
            await WaitAsync(() => Task.FromResult(!h.Server.Nodes.LabelingReady), ct);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "La condición no se cumplió en 30 s.");
            await Task.Delay(100, ct);
        }
    }
}
