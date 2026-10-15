using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// Los tramos de la sección 10.4 del documento principal se miden de punta a punta con la composición del EdgeHost y
/// simuladores (PLC por OPC UA, impresora, aplicador y escáner): cada uno tiene una muestra por caja. Con la señal en proceso
/// (G11), el sender empieza a escribir sin esperar su sondeo. Es la base de la medición de T12, que corre aparte (carga).
/// </summary>
public sealed class LatencySegmentsTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const int WarmUp = 3;
    private const int Boxes = 10;
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f7lat", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task ConfirmedAsync(StationScenario s, int jobs)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await s.CountAsync("FROM dbo.PrintJob WHERE Status = 'CONFIRMED'", null, Ct) < jobs)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Los {jobs} jobs no quedaron CONFIRMED en 60 s.");
            await Task.Delay(100, Ct);
        }
    }

    private static readonly string[] Segments =
    [
        MetricNames.PlcToOpcReceiveMs, MetricNames.OpcReceiveToJobReadyMs, MetricNames.OpcReceiveToSendStartMs, MetricNames.SendStartToDeviceAcceptMs,
        MetricNames.SendToPrintedMs, MetricNames.PrintedToAppliedMs, MetricNames.AppliedToVerifiedMs, MetricNames.SendStartToAppliedMs,
        MetricNames.SendToVerifiedMs, MetricNames.PresendCheckToWriteMs, MetricNames.PrintDeadlineSlackMs,
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_segment_gets_a_sample_per_box_and_the_signal_wakes_the_sender(bool signal)
    {
        // Sondeo de respaldo de 2 s: sin la señal, la notificación a inicio de escritura espera el sondeo.
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Verified, TransitBudgetMs = 15_000 }, Ct,
            TimeProvider.System);
        await using var p = await StationPipeline.StartAsync(s, new EvidenceSources(PrinterCompletion: true, Applicator: true, Scanner: true), Ct,
            senderPollMs: 2_000, signal: signal);

        // Calentamiento (conexión con la impresora, código y planes de SQL en frío): tres cajas que no se miden.
        ulong cycle = 1;
        for (; cycle <= WarmUp; cycle++)
        {
            p.Publish(cycle);
            await ConfirmedAsync(s, (int)cycle);
        }

        await p.Rig.EvidenceSim.DrainAsync();
        var offsets = Segments.ToDictionary(m => m, m => s.Metrics.Samples(m).Count);
        for (; cycle <= WarmUp + Boxes; cycle++)
        {
            p.Publish(cycle);
            await Task.Delay(1_000, Ct);
        }

        await ConfirmedAsync(s, WarmUp + Boxes);
        await p.Rig.EvidenceSim.DrainAsync();
        foreach (var segment in Segments)
        {
            var count = s.Metrics.Samples(segment).Count - offsets[segment];
            Assert.True(count == Boxes, $"{segment}: {count} muestras para {Boxes} cajas");
        }

        var toSendStart = s.Metrics.Samples(MetricNames.OpcReceiveToSendStartMs).Skip(offsets[MetricNames.OpcReceiveToSendStartMs]).ToArray();
        var shown = string.Join(", ", toSendStart.Select(v => v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
        if (signal)
        {
            Assert.True(toSendStart.Max() < 500, $"con la señal, notificación a inicio de escritura: {shown} ms");
        }
        else
        {
            Assert.True(toSendStart.Max() > 500, $"sin la señal, el sender esperó su sondeo: {shown} ms");
        }
    }
}
