using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Erp;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Simulators.Printer;
using Microsoft.Data.SqlClient;

// Demo del flujo completo (docs/DEMO.md): el EdgeHost real, en su propio proceso, contra simuladores. Este proceso:
//   1. siembra una estación nueva en la base (revisión, época, PrinterSendOrder);
//   2. arranca el PLC simulado (OPC UA), la impresora simulada (TCP) y el ERP simulado (HTTP);
//   3. hace de integración de evidencia: la impresora, el aplicador y el escáner simulados informan por TX-D (el EdgeHost no
//      tiene una entrada de evidencia propia);
//   4. lanza el EdgeHost configurado con variables de entorno, espera LabelingReady y hace pasar las cajas;
//   5. muestra el resultado y sale con 0 si todas quedaron CONFIRMED con una sola etiqueta.
// Nunca se conecta a equipo real. La base tiene que estar migrada (CartonPrinting.EdgeHost migrate).
//
// Uso: CartonPrinting.DemoHost [--connection <cadena del login de la aplicación>] [--boxes 30] [--interval-ms 1000]
//      [--timeout-s 120] [--edgehost <ruta de CartonPrinting.EdgeHost.dll>]
// Sin --connection se usa ConnectionStrings__CartonPrinting del entorno. RabbitMq__Uri, si está en el entorno, pasa al EdgeHost.
var options = DemoOptions.Parse(args);
const string site = "PLANT-01";
var stationId = $"DEMO-{DateTime.UtcNow:HHmmss}";
var printerId = $"PRN-{stationId}";
const string workOrder = "WO-DEMO-0001";
var db = new SqlDb(new DatabaseOptions { ConnectionString = options.Connection });
var clock = new EdgeClock(TimeProvider.System);
using var metrics = new CartonMetrics();

Console.WriteLine($"Estación {site}/{stationId}: {options.Boxes} cajas, una cada {options.IntervalMs} ms.");
var now = clock.UtcNow;
var revision = await new StationConfigStore(db).ActivateRevisionAsync(new StationRevision
{
    SiteId = site,
    StationId = stationId,
    RevisionNumber = 1,
    PlcEndpointId = "opc.tcp://simulador/" + stationId,
    PrinterId = printerId,
    ManualPrinterId = null,
    LabelType = "CARTON_GS1",
    TemplateRevision = "TPL-DEMO",
    DeadlineReference = DeadlineReference.PlcTimestamp,
    TransitBudgetMs = 5_000,
    DeviceLeadTimeMs = 400,
    SendMarginMs = 100,
    ClockSkewToleranceMs = 50,
    MaxUpstreamLatencyMs = null,
    ClockJumpToleranceMs = 200,
    MinCycleIntervalMs = null,
    MaxAutoAttempts = 3,
    RetryBackoffMs = 100,
    MinConfirmationLevel = EvidenceLevel.Verified,
    EvidenceWindowMs = 5_000,
    EpochChangePolicy = "AUTO_INCREMENT_BY_ONE",
    CartonIdentityMode = "CYCLE_ONLY",
    GapGraceMs = 5_000,
    PhysicalGateMode = "NONE",
    GateFreshnessMs = null,
    CreatedBy = "demo",
    ApprovedBy = "demo.supervisor",
    ChangeReason = "Demo del flujo completo con simuladores",
}, CancellationToken.None);
await new PlcEpochStore(db).InsertAsync(new PlcEpoch(site, stationId, 1, "PLC", PlcEpochStore.Initial, 1, now.AddHours(-1), "SYSTEM",
    revision.ConfigRevisionId, null), CancellationToken.None);
await new PrinterSendOrderStore(db).RegisterAsync(printerId, site, stationId, now, CancellationToken.None);

var pki = Path.Combine(Path.GetTempPath(), "cartonprinting-demo", stationId);
await using var plc = await PlcSimulatorServer.StartAsync(new PlcSimulatorOptions { StationId = stationId, Port = FreePort(), PkiRoot = Path.Combine(pki, "plc") });
await using var printer = PrinterSimulator.Start();
await using var erp = await ErpSimulatorServer.StartAsync();
erp.Set(new ErpWorkOrder
{
    WorkOrderId = workOrder, Revision = "R1", ProductId = "SKU-DEMO", LotId = "LOT-DEMO-1", ValidFrom = now.AddHours(-1), ValidUntil = now.AddHours(8),
});
var evidence = new EvidenceService(new TxdEvidence(db), db, clock, metrics);
using var evidenceSim = new EvidenceSimulator(printer, site, stationId, new EvidenceSources(PrinterCompletion: true, Applicator: true, Scanner: true),
    (serial, ct) => JobBySerialAsync(db, serial, ct), (o, ct) => evidence.SubmitAsync(o, ct), () => clock.UtcNow);
Console.WriteLine($"PLC simulado en {plc.EndpointUrl}; impresora en el puerto {printer.Port}; ERP en {erp.BaseUrl}");

var host = StartEdgeHost(options, site, stationId, printerId, plc, printer, erp, pki);
var exitCode = 1;
try
{
    var ready = DateTime.UtcNow.AddSeconds(90);
    while (!plc.Nodes.LabelingReady)
    {
        if (host.HasExited || DateTime.UtcNow > ready)
        {
            throw new InvalidOperationException(host.HasExited ? $"El EdgeHost terminó con {host.ExitCode}." : "El EdgeHost no levantó LabelingReady en 90 s.");
        }

        await Task.Delay(200);
    }

    Console.WriteLine("LabelingReady: el EdgeHost está listo. Pasan las cajas.");
    for (ulong cycle = 1; cycle <= (ulong)options.Boxes; cycle++)
    {
        plc.Nodes.Publish(new LatchedRecord { PlcEpoch = 1, CycleId = cycle, WorkOrderId = workOrder, DetectionTimestamp = DateTime.UtcNow });
        await Task.Delay(options.IntervalMs);
    }

    var deadline = DateTime.UtcNow.AddSeconds(options.TimeoutSeconds);
    IReadOnlyList<(string Status, int Count)> statuses;
    while (true)
    {
        statuses = await StatusesAsync(db, stationId);
        var finished = statuses.Where(s => s.Status is JobStatus.Confirmed or JobStatus.Expired or JobStatus.Failed or JobStatus.Unknown).Sum(s => s.Count);
        if (finished >= options.Boxes || DateTime.UtcNow > deadline)
        {
            break;
        }

        await Task.Delay(500);
    }

    await evidenceSim.DrainAsync();
    var confirmed = statuses.Where(s => s.Status == JobStatus.Confirmed).Sum(s => s.Count);
    var labels = printer.Received.Select(r => r.Serial).ToList();
    Console.WriteLine($"Jobs por estado: {string.Join(", ", statuses.Select(s => $"{s.Status}={s.Count}"))}");
    Console.WriteLine($"Etiquetas recibidas por la impresora: {labels.Count} ({labels.Distinct().Count()} seriales distintos)");
    Console.WriteLine($"Eventos en el Outbox de la estación: {await OutboxAsync(db, stationId)}");
    exitCode = confirmed == options.Boxes && labels.Count == options.Boxes && labels.Distinct().Count() == options.Boxes ? 0 : 1;
    Console.WriteLine(exitCode == 0 ? "DEMO OK: todas las cajas CONFIRMED (VERIFIED), una etiqueta por caja." : "DEMO CON DIFERENCIAS: revisar los estados.");
}
finally
{
    if (!host.HasExited)
    {
        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync();
    }
}

return exitCode;

static Process StartEdgeHost(
    DemoOptions options, string site, string stationId, string printerId, PlcSimulatorServer plc, PrinterSimulator printer, ErpSimulatorServer erp, string pki)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add(options.EdgeHost);
    var env = psi.Environment;
    env["ConnectionStrings__CartonPrinting"] = options.Connection;
    env["Urls"] = "http://127.0.0.1:0";
    env["Logging__LogLevel__Default"] = "Warning";
    env["Ingest__SiteId"] = site;
    env["Ingest__StationId"] = stationId;
    env["Ingest__LabelSerialPrefix"] = "P01DEMO";
    env["Ingest__Opc__EndpointUrl"] = plc.EndpointUrl;
    env["Ingest__Opc__RecordNodeId"] = plc.Nodes.RecordNodeId;
    env["Ingest__Opc__LabelingReadyNodeId"] = plc.Nodes.LabelingReadyNodeId;
    // Laboratorio: el PLC simulado sin cifrado y sin validar su certificado. En planta, SignAndEncrypt con validación estricta
    // (los valores por defecto de OpcIngestOptions; ver docs/DEMO.md).
    env["Ingest__Opc__SecurityMode"] = "None";
    env["Ingest__Opc__AutoAcceptUntrustedCertificates"] = "true";
    env["Ingest__Opc__PkiRoot"] = Path.Combine(pki, "edgehost");
    env["ErpSync__BaseUrl"] = erp.BaseUrl;
    env["Printer__PrinterId"] = printerId;
    env["Printer__Transport__Port"] = printer.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var host = new Process { StartInfo = psi };
    host.OutputDataReceived += (_, e) => Echo(e.Data);
    host.ErrorDataReceived += (_, e) => Echo(e.Data);
    host.Start();
    host.BeginOutputReadLine();
    host.BeginErrorReadLine();
    Console.WriteLine($"EdgeHost lanzado (proceso {host.Id}).");
    return host;

    static void Echo(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            Console.WriteLine($"  [edgehost] {line}");
        }
    }
}

static async Task<Guid?> JobBySerialAsync(SqlDb db, string serial, CancellationToken ct) =>
    await db.ReadAsync(async conn =>
    {
        await using var cmd = conn.Command(null, "SELECT PrintJobId FROM dbo.PrintJob WHERE LabelSerial = @serial");
        cmd.VarChar("@serial", serial, 40);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }, ct);

static Task<IReadOnlyList<(string Status, int Count)>> StatusesAsync(SqlDb db, string stationId) =>
    db.ReadAsync<IReadOnlyList<(string, int)>>(async conn =>
    {
        await using var cmd = conn.Command(null, "SELECT Status, COUNT(*) FROM dbo.PrintJob WHERE StationId = @station GROUP BY Status ORDER BY Status");
        cmd.NVarChar("@station", stationId, 64);
        var rows = new List<(string, int)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add((r.GetString(0), r.GetInt32(1)));
        }

        return rows;
    }, CancellationToken.None);

static Task<int> OutboxAsync(SqlDb db, string stationId) =>
    db.ReadAsync(async conn =>
    {
        await using var cmd = conn.Command(null, "SELECT COUNT(*) FROM dbo.OutboxEvent WHERE OrderingKey LIKE @key");
        cmd.NVarChar("@key", $"%:{stationId}:%", 256);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }, CancellationToken.None);

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

internal sealed record DemoOptions(string Connection, int Boxes, int IntervalMs, int TimeoutSeconds, string EdgeHost)
{
    public static DemoOptions Parse(string[] args)
    {
        string? Value(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var connection = Value("--connection") ?? Environment.GetEnvironmentVariable("ConnectionStrings__CartonPrinting")
            ?? throw new ArgumentException("Falta --connection o ConnectionStrings__CartonPrinting.");
        var edgeHost = Value("--edgehost") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory.Replace(
            Path.Combine("tests", "CartonPrinting.DemoHost"), Path.Combine("src", "EdgeHost"), StringComparison.Ordinal), "CartonPrinting.EdgeHost.dll"));
        if (!File.Exists(edgeHost))
        {
            throw new FileNotFoundException("No existe el EdgeHost compilado: dotnet build en la raíz, o --edgehost <ruta>.", edgeHost);
        }

        return new DemoOptions(connection, int.Parse(Value("--boxes") ?? "30", System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(Value("--interval-ms") ?? "1000", System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(Value("--timeout-s") ?? "120", System.Globalization.CultureInfo.InvariantCulture), edgeHost);
    }
}
