using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// T19 · SQL Server caído, perfil mínimo. Un proxy TCP entre el EdgeHost y SQL Server corta todas las conexiones y no
/// acepta nuevas durante 30 s, mientras el PLC simulado publica 25 cajas. Falla cerrada: ninguna detección ni etiqueta de
/// esos ciclos y LabelingReady en falso durante la caída. Al volver, el ciclo siguiente abre el hueco 501-525, y el
/// barrido lo cierra CLOSED_UNRECOVERED con 25 ciclos sin datos.
/// </summary>
public sealed class SqlOutageTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f7sql", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Spec", "T19")]
    public async Task Sql_down_while_25_boxes_pass_fails_closed_and_leaves_a_gap_of_25()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        var direct = new SqlConnectionStringBuilder(s.Db.AppConnectionString);
        var parts = direct.DataSource.Replace("tcp:", string.Empty, StringComparison.Ordinal).Split(',');
        await using var proxy = new TcpProxy(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)).Start();
        var viaProxy = new SqlConnectionStringBuilder(s.Db.AppConnectionString)
        {
            DataSource = $"127.0.0.1,{proxy.Port}", ConnectTimeout = 2, Pooling = false, ConnectRetryCount = 0,
        };

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
        using var host = new IngestHostedService(options, new SqlDb(new DatabaseOptions { ConnectionString = viaProxy.ConnectionString }), s.Clock,
            metrics, NullLoggerFactory.Instance);
        await host.StartAsync(Ct);
        try
        {
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al arrancar");
            Publish(h, s, 500);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");

            // SQL cae 30 s mientras pasan 25 cajas (una cada 1,2 s).
            proxy.AcceptNew = false;
            proxy.SeverAll();
            var outage = DateTime.UtcNow;
            var labelingReadyDuringOutage = new List<bool>();
            for (ulong cycle = 501; cycle <= 525; cycle++)
            {
                Publish(h, s, cycle);
                await Task.Delay(TimeSpan.FromMilliseconds(1_200), Ct);
                labelingReadyDuringOutage.Add(h.Server.Nodes.LabelingReady);
            }

            var remaining = outage.AddSeconds(30) - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, Ct);
            }

            Assert.False(labelingReadyDuringOutage.Skip(3).Any(ready => ready), "LabelingReady siguió en verdadero con SQL caído");
            proxy.AcceptNew = true;

            Publish(h, s, 526);
            await WaitAsync(async () => await s.DetectionAsync(526, Ct) is not null, "detección 526 al volver SQL");
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al volver SQL");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, await s.CountAsync("FROM dbo.CartonDetection WHERE StationId = @st AND CycleId BETWEEN 501 AND 525",
            c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.Equal(1, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st AND FromCycleId = 501 AND ToCycleId = 525 AND Status = 'OPEN'",
            c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.True(metrics.Total(MetricNames.OpcRecordsRejected, ("cause", "SQL_UNAVAILABLE"), ("transport", Transport.Subscription)) >= 1);

        await Task.Delay(TimeSpan.FromMilliseconds(s.Revision.GapGraceMs + 200), Ct);
        await new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics).SweepOnceAsync(Ct);
        Assert.Equal(("CLOSED_UNRECOVERED", 25m), Assert.Single(await s.QueryAsync(
            "SELECT Status, UnrecoveredCount FROM dbo.SequenceGap WHERE StationId = @st AND FromCycleId = 501",
            c => c.Parameters.AddWithValue("@st", s.StationId), r => (r.GetString(0), r.GetDecimal(1)), Ct)));
        Assert.Equal(25, s.Metrics.Total(MetricNames.CyclesUnobserved, ("station", s.StationId)));
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CycleId BETWEEN 501 AND 525",
            c => c.Parameters.AddWithValue("@st", s.StationId), Ct));
        Assert.DoesNotContain(await s.ResultsAsync(ct: Ct), r => r.GetProperty("cycleId").GetString() is { } c && ulong.Parse(c) is >= 501 and <= 525);
    }

    private static void Publish(OpcHarness h, StationScenario s, ulong cycle) => h.Server.Nodes.Publish(new LatchedRecord
    {
        PlcEpoch = s.Epoch, CycleId = cycle, WorkOrderId = StationScenario.WorkOrder, DetectionTimestamp = DateTime.UtcNow,
    });

    private static async Task WaitAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"No se cumplió en 60 s: {what}.");
            await Task.Delay(100, Ct);
        }
    }
}
