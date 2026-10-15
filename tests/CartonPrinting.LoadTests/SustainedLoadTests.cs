using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Testing;

namespace CartonPrinting.LoadTests;

/// <summary>
/// T12 · Carga sostenida y capacidad, con simuladores y la composición del EdgeHost (<see cref="StationPipeline"/>: PLC por OPC UA
/// con SignAndEncrypt, ingesta real, señal en proceso, sender con sondeo de respaldo de 50 ms, barrido cada 200 ms, Outbox Relay
/// hacia RabbitMQ, impresora, aplicador y escáner simulados), contra SQL Server y RabbitMQ reales.
/// La tasa y el payload no están acordados con OT: se usa la hipótesis declarada (una caja por segundo con el payload del
/// simulador; 30 minutos a esa tasa y 10 al doble). Reporta p50, p95, p99 y máximo de cada tramo, el backlog y la desigualdad
/// de factibilidad del plazo; no valida tiempos físicos (impresión, aplicación y lectura son del simulador).
/// </summary>
public sealed class SustainedLoadTests(SqlServerFixture sql, RabbitMqFixture rabbit) : IAsyncLifetime, IClassFixture<RabbitMqFixture>
{
    private static readonly (string Name, double BoxesPerSecond, TimeSpan Duration)[] Phases =
    [
        ("tasa pactada (hipótesis)", 1.0, TimeSpan.FromMinutes(30)),
        ("doble de la tasa", 2.0, TimeSpan.FromMinutes(10)),
    ];

    private const int WarmUpBoxes = 10;

    private static readonly string[] Segments =
    [
        MetricNames.PlcToOpcReceiveMs, MetricNames.OpcReceiveToJobReadyMs, MetricNames.OpcReceiveToSendStartMs, MetricNames.SendStartToDeviceAcceptMs,
        MetricNames.SendToPrintedMs, MetricNames.PrintedToAppliedMs, MetricNames.AppliedToVerifiedMs, MetricNames.SendStartToAppliedMs,
        MetricNames.SendToVerifiedMs, MetricNames.PresendCheckToWriteMs, MetricNames.PrintDeadlineSlackMs,
    ];

    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t12", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Load")]
    [Trait("Spec", "T12")]
    public async Task Thirty_minutes_at_the_agreed_rate_and_ten_at_double_keep_the_backlog_stable_and_the_deadline_feasible()
    {
        // Valores de laboratorio de la estación (OT-03 abierta): TransitBudgetMs 1500, DeviceLeadTimeMs 400, SendMarginMs 250 (cubre el
        // p99 de presend_check_to_write_ms más ClockJumpToleranceMs 200), ClockSkewToleranceMs 50.
        var s = await StationScenario.CreateAsync(_db, r => r with
        {
            MinConfirmationLevel = EvidenceLevel.Verified, EvidenceWindowMs = 5_000, SendMarginMs = 250,
        }, Ct, TimeProvider.System);
        await using var p = await StationPipeline.StartAsync(s, new EvidenceSources(PrinterCompletion: true, Applicator: true, Scanner: true), Ct,
            rabbitUri: rabbit.Uri);

        // Calentamiento, fuera de la muestra: conexión con la impresora y código y planes de SQL en frío.
        ulong cycle = 1;
        for (; cycle <= WarmUpBoxes; cycle++)
        {
            p.Publish(cycle);
            await Task.Delay(1_000, Ct);
        }

        await DrainAsync(s, cycle - 1);
        var warmUp = Segments.ToDictionary(m => m, m => s.Metrics.Samples(m).Count);

        var report = new Dictionary<string, object?>();
        var phaseReports = new List<Dictionary<string, object?>>();
        foreach (var (name, rate, duration) in Phases)
        {
            var firstCycle = cycle;
            var offsets = Segments.ToDictionary(m => m, m => s.Metrics.Samples(m).Count);
            var interval = TimeSpan.FromSeconds(1 / rate);
            var backlog = new List<int>();
            var oldestReadyMs = new List<double>();
            var end = DateTime.UtcNow + duration;
            var next = DateTime.UtcNow;
            var sampleAt = DateTime.UtcNow;
            while (DateTime.UtcNow < end)
            {
                p.Publish(cycle++);
                if (DateTime.UtcNow >= sampleAt)
                {
                    var (pending, oldest) = await BacklogAsync(s);
                    backlog.Add(pending);
                    oldestReadyMs.Add(oldest);
                    sampleAt = DateTime.UtcNow.AddSeconds(1);
                }

                next += interval;
                var wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, Ct);
                }
            }

            await DrainAsync(s, cycle - 1);
            var segments = Segments.ToDictionary(m => m, m => Summary(s.Metrics.Samples(m).Skip(offsets[m]).ToArray()));
            phaseReports.Add(new Dictionary<string, object?>
            {
                ["phase"] = name, ["boxesPerSecond"] = rate, ["minutes"] = duration.TotalMinutes, ["boxes"] = (long)(cycle - firstCycle),
                ["backlogMax"] = backlog.Max(), ["backlogP95"] = Percentile(backlog.Select(b => (double)b).ToArray(), 0.95),
                ["backlogLastMinuteMax"] = backlog.TakeLast(60).Max(), ["oldestPendingMsMax"] = oldestReadyMs.Max(), ["segments"] = segments,
            });

            // Backlog estable: a la tasa pactada no crece (nunca más de unas pocas cajas en curso).
            Assert.True(backlog.Max() <= 3 * (int)Math.Ceiling(rate), $"{name}: backlog máximo {backlog.Max()}");
            Assert.True(backlog.TakeLast(60).Max() <= 3 * (int)Math.Ceiling(rate), $"{name}: backlog del último minuto {backlog.TakeLast(60).Max()}");
        }

        // El Outbox se publica entero: cada caja deja su CartonDetected y su PrintJobResult.
        var outboxDeadline = DateTime.UtcNow.AddSeconds(60);
        while (await s.CountAsync("FROM dbo.OutboxEvent WHERE PublishedAt IS NULL", null, Ct) > 0 && DateTime.UtcNow < outboxDeadline)
        {
            await Task.Delay(250, Ct);
        }

        // Errores de la corrida (RENDIMIENTO_Y_SEGURIDAD, reporte de benchmark): estados finales, rechazos y fallas de servicios.
        var jobsByStatus = (await s.QueryAsync("SELECT Status, COUNT(*) FROM dbo.PrintJob GROUP BY Status", null, r => (r.GetString(0), r.GetInt32(1)), Ct))
            .ToDictionary(x => x.Item1, x => x.Item2);
        var detectionsByStatus = (await s.QueryAsync("SELECT Status, COUNT(*) FROM dbo.CartonDetection GROUP BY Status", null,
            r => (r.GetString(0), r.GetInt32(1)), Ct)).ToDictionary(x => x.Item1, x => x.Item2);
        var outboxEvents = await s.CountAsync("FROM dbo.OutboxEvent", null, Ct);
        var outboxPending = await s.CountAsync("FROM dbo.OutboxEvent WHERE PublishedAt IS NULL", null, Ct);
        var faults = p.Faults();
        report["errors"] = new Dictionary<string, object?>
        {
            ["jobsByStatus"] = jobsByStatus, ["detectionsByStatus"] = detectionsByStatus,
            ["opcRecordsRejected"] = s.Metrics.Series(MetricNames.OpcRecordsRejected),
            ["opcChannelFull"] = s.Metrics.Total("opc_ingest_channel_full_total"),
            ["presendCheckRejected"] = s.Metrics.Total(MetricNames.PresendCheckRejected),
            ["attemptsNotSent"] = s.Metrics.Total(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.NotSent)),
            ["attemptsUnknown"] = s.Metrics.Total(MetricNames.PrintAttempts, ("outcome", AttemptOutcome.Unknown)),
            ["outboxEvents"] = outboxEvents, ["outboxPublished"] = s.Metrics.Total(MetricNames.OutboxPublished), ["outboxPendingAtEnd"] = outboxPending,
            ["serviceFaults"] = faults,
        };

        // Todas las cajas (también las de calentamiento) tienen su job CONFIRMED con VERIFIED y una sola etiqueta.
        var boxes = (int)(cycle - 1);
        Assert.Equal(boxes, await s.CountAsync("FROM dbo.PrintJob WHERE Status = 'CONFIRMED'", null, Ct));
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE Status <> 'CONFIRMED'", null, Ct));
        Assert.Equal(boxes, p.Rig.Printer.Received.Select(r => r.Serial).Distinct().Count());
        Assert.Equal(boxes, p.Rig.Printer.Received.Count);

        // Desigualdad de factibilidad (PLC_TIMESTAMP, sección 17): con los p99 medidos de toda la corrida y DeviceLeadTimeMs
        // como p99 medido de send_start_to_applied_ms.
        var revision = s.Revision;
        double[] Measured(string metric) => [.. s.Metrics.Samples(metric).Skip(warmUp[metric])];
        var p99Flank = Percentile(Measured(MetricNames.PlcToOpcReceiveMs), 0.99);
        var p99ToSend = Percentile(Measured(MetricNames.OpcReceiveToSendStartMs), 0.99);
        var deviceLead = Percentile(Measured(MetricNames.SendStartToAppliedMs), 0.99);
        var left = p99Flank + p99ToSend + revision.SendMarginMs + revision.ClockSkewToleranceMs + deviceLead;
        var presendP99 = Percentile(Measured(MetricNames.PresendCheckToWriteMs), 0.99);
        report["feasibility"] = new Dictionary<string, object?>
        {
            ["deadlineReference"] = revision.DeadlineReference, ["p99PlcToOpcReceiveMs"] = p99Flank, ["p99OpcReceiveToSendStartMs"] = p99ToSend,
            ["sendMarginMs"] = revision.SendMarginMs, ["clockSkewToleranceMs"] = revision.ClockSkewToleranceMs,
            ["deviceLeadTimeMsMeasuredP99"] = deviceLead, ["deviceLeadTimeMsConfigured"] = revision.DeviceLeadTimeMs, ["leftSideMs"] = left,
            ["transitBudgetMs"] = revision.TransitBudgetMs, ["holds"] = left < revision.TransitBudgetMs,
            ["sendMarginCoversPresendP99PlusClockJump"] = revision.SendMarginMs >= presendP99 + revision.ClockJumpToleranceMs,
            ["p99PresendCheckToWriteMs"] = presendP99, ["clockJumpToleranceMs"] = revision.ClockJumpToleranceMs,
        };
        report["phases"] = phaseReports;
        report["opc"] = new Dictionary<string, object?>
        {
            ["requestedSamplingIntervalMs"] = 10, ["requestedPublishingIntervalMs"] = 20,
            ["revisedSamplingIntervalMs"] = p.Ingest.Source.RevisedSamplingIntervalMs, ["revisedPublishingIntervalMs"] = p.Ingest.Source.RevisedPublishingIntervalMs,
            ["revisedQueueSize"] = p.Ingest.Source.RevisedQueueSize, ["sessionSecurity"] = p.Ingest.Source.SessionSecurity,
        };
        report["composition"] = new Dictionary<string, object?>
        {
            ["sweepIntervalMs"] = StationPipeline.SweepIntervalMs, ["outboxRelay"] = p.Relay is not null,
            ["rabbitMq"] = await RabbitMqVersionAsync(),
        };
        report["environment"] = new Dictionary<string, object?>
        {
            ["machine"] = Environment.MachineName, ["os"] = RuntimeInformation.OSDescription, ["cpu"] = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            ["logicalProcessors"] = Environment.ProcessorCount, ["memoryGiB"] = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0, 1),
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["sqlServer"] = await s.ScalarAsync<string>("SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(64)) + N' ' + CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128))", null, Ct),
            ["payloadBytes"] = p.Rig.Printer.Received[0].Bytes,
        };
        report["boxes"] = boxes;
        report["warmUpBoxes"] = WarmUpBoxes;
        report["finishedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var path = Environment.GetEnvironmentVariable("CARTONPRINTING_BENCHMARK_OUT") ?? Path.Combine(AppContext.BaseDirectory, "benchmark-t12.json");
        await File.WriteAllTextAsync(path, json, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(json);

        Assert.Empty(faults);
        Assert.Equal(0, outboxPending);
        Assert.Equal(2 * boxes, outboxEvents);
        Assert.StartsWith("SignAndEncrypt", p.Ingest.Source.SessionSecurity);
        Assert.True(left < revision.TransitBudgetMs, $"desigualdad de factibilidad: {left:F0} ms ≥ TransitBudgetMs {revision.TransitBudgetMs} ms");
        Assert.True(revision.SendMarginMs >= presendP99 + revision.ClockJumpToleranceMs,
            $"SendMarginMs {revision.SendMarginMs} no cubre p99(presend_check_to_write_ms) {presendP99} + ClockJumpToleranceMs {revision.ClockJumpToleranceMs}");
        Assert.True(revision.DeviceLeadTimeMs >= deviceLead, $"DeviceLeadTimeMs {revision.DeviceLeadTimeMs} menor que el p99 medido {deviceLead}");
    }

    private async Task<string> RabbitMqVersionAsync()
    {
        var result = await rabbit.Container.ExecAsync(["rabbitmqctl", "version"], Ct);
        return $"RabbitMQ {result.Stdout.Trim()}, en un contenedor de Testcontainers en la misma máquina";
    }

    private static async Task<(int Pending, double OldestMs)> BacklogAsync(StationScenario s)
    {
        var rows = await s.QueryAsync("""
            SELECT COUNT(*), ISNULL(DATEDIFF_BIG(MILLISECOND, MIN(cd.ReceivedAt), SYSUTCDATETIME()), 0)
            FROM dbo.PrintJob AS pj INNER JOIN dbo.CartonDetection AS cd ON cd.DetectionId = pj.DetectionId
            WHERE pj.Status IN ('READY', 'RETRY_WAIT', 'SENDING')
            """, null, r => (r.GetInt32(0), (double)r.GetInt64(1)), Ct);
        return rows[0];
    }

    /// <summary>Espera a que todas las cajas publicadas tengan su job terminado (o falla a los 60 s).</summary>
    private static async Task DrainAsync(StationScenario s, ulong lastCycle)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await s.CountAsync("FROM dbo.PrintJob WHERE Status IN ('CONFIRMED', 'EXPIRED', 'UNKNOWN', 'FAILED')", null, Ct) < (int)lastCycle)
        {
            Assert.True(DateTime.UtcNow < deadline, "Las cajas no terminaron 60 s después de la fase.");
            await Task.Delay(250, Ct);
        }
    }

    private static Dictionary<string, object?> Summary(double[] samples) => samples.Length == 0
        ? new Dictionary<string, object?> { ["n"] = 0 }
        : new Dictionary<string, object?>
        {
            ["n"] = samples.Length, ["p50"] = Percentile(samples, 0.50), ["p95"] = Percentile(samples, 0.95), ["p99"] = Percentile(samples, 0.99),
            ["max"] = samples.Max(),
        };

    private static double Percentile(double[] samples, double q)
    {
        if (samples.Length == 0)
        {
            return double.NaN;
        }

        var sorted = samples.Order().ToArray();
        return Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)], 1);
    }
}
