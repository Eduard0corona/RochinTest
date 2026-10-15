using CartonPrinting.EdgeHost;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Health;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Sql;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Operations;
using CartonPrinting.Outbox;
using CartonPrinting.Recovery;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Comandos de mantenimiento: "provision" (crea base y logins con un login sysadmin) y "migrate"
// (aplica el DDL de spec/ con el login de migraciones). Sin argumentos arranca el EdgeHost.
if (args.Length > 0 && args[0] is "provision" or "migrate")
{
    return await MaintenanceCommands.RunAsync(args[0], args[1..]);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EdgeClock>();
builder.Services.AddSingleton<CartonMetrics>();
builder.Services.AddSingleton(new DatabaseOptions { ConnectionString = builder.Configuration.GetConnectionString("CartonPrinting") ?? string.Empty });
builder.Services.AddSingleton<SqlDb>();

// Señal en proceso hacia el sender de línea: un job READY nuevo lo despierta sin esperar su sondeo (G11).
builder.Services.AddSingleton<CartonPrinting.Infrastructure.Signals.ReadyJobSignal>();

// Arranque ordenado (Fase 5): cada módulo registra sus pasos; StartupHostedService los ejecuta en orden.
var startup = new StartupCoordinator(TimeProvider.System);
startup.Add(StartupStage.LabelingReadyLow, "LabelingReady sin escribir hasta la etapa 8", _ => Task.CompletedTask);
builder.Services.AddSingleton(startup);
builder.Services.AddHostedService<StartupHostedService>();

// Outbox Relay (Fase 1): solo con RabbitMQ configurado. Sin broker la impresión sigue y el Outbox acumula.
var rabbit = builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>();
if (!string.IsNullOrWhiteSpace(rabbit?.Uri))
{
    builder.Services.AddSingleton(rabbit);
    builder.Services.AddSingleton(builder.Configuration.GetSection("OutboxRelay").Get<OutboxRelayOptions>() ?? new OutboxRelayOptions());
    builder.Services.AddSingleton<IOutboxPublisher, RabbitMqPublisher>();
    builder.Services.AddHostedService<OutboxRelay>();
}

builder.Services.AddHealthChecks()
    .AddCheck("sqlserver", new SqlServerHealthCheck(() =>
        builder.Configuration.GetConnectionString("CartonPrinting")
        ?? throw new InvalidOperationException("Falta ConnectionStrings:CartonPrinting.")), tags: ["ready"]);

var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CartonPrinting.EdgeHost"))
    .WithTracing(t => t.AddSource(CartonTelemetry.SourceName))
    .WithMetrics(m => m.AddMeter(CartonTelemetry.SourceName));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    otel.UseOtlpExporter();
}

// Ingesta OPC UA de una estación (Fase 2): solo con la sección "Ingest" configurada. Los endpoints y
// certificados de planta no van en el repositorio: se configuran en el entorno.
var ingest = builder.Configuration.GetSection("Ingest").Get<IngestHostOptions>();
if (!string.IsNullOrWhiteSpace(ingest?.Opc.EndpointUrl))
{
    builder.Services.AddSingleton(ingest);
    builder.Services.AddSingleton(sp =>
    {
        var service = new IngestHostedService(ingest, sp.GetRequiredService<SqlDb>(), sp.GetRequiredService<EdgeClock>(),
            sp.GetRequiredService<CartonMetrics>(), sp.GetRequiredService<ILoggerFactory>(), sp.GetService<CartonPrinting.Orchestration.IErpDemand>(),
            coordinated: true, sp.GetRequiredService<CartonPrinting.Infrastructure.Signals.ReadyJobSignal>());
        startup.Add(StartupStage.StateLoaded, $"revisión, épocas y marca alta de {ingest.StationId}", service.LoadStateAsync);
        startup.Add(StartupStage.Subscribed, $"suscripción OPC UA de {ingest.StationId}", service.SubscribeAsync);
        startup.Add(StartupStage.LabelingReady, $"primer LabelingReady de {ingest.StationId}", service.FirstBeatAsync);
        return service;
    });
    builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestHostedService>());
    builder.Services.AddSingleton<IAnomalyReingest>(sp => new IngestReingest(sp.GetRequiredService<IngestHostedService>(), ingest));
}

// Sincronización ERP y barrido TX-S de la estación (Fase 3): con la estación configurada en "Ingest";
// el ERP solo con "ErpSync:BaseUrl".
builder.Services.AddStationServices(builder.Configuration);

// Impresoras (Fases 4 y 5): la de línea ("Printer") y la manual de reimpresiones ("ManualPrinter"), cada una con su
// lock de sistema operativo, su lease y un solo sender; solo con PrinterId y puerto configurados.
foreach (var (section, role) in new[] { ("Printer", PrinterRole.Line), ("ManualPrinter", PrinterRole.Manual) })
{
    var printer = builder.Configuration.GetSection(section).Get<PrinterHostOptions>();
    if (!string.IsNullOrWhiteSpace(printer?.PrinterId) && printer.Transport.Port > 0)
    {
        builder.Services.AddHostedService(sp => new PrinterHostedService(role, printer, sp.GetRequiredService<SqlDb>(), sp.GetRequiredService<EdgeClock>(),
            sp.GetRequiredService<CartonMetrics>(), sp.GetRequiredService<ILoggerFactory>(), startup, sp.GetService<ClockGuard>(),
            sp.GetRequiredService<CartonPrinting.Infrastructure.Signals.ReadyJobSignal>()));
    }
}

// Operations API (Fase 5): solo con personas configuradas ("Operations:OperatorKeySha256"). Las claves de planta no van
// en el repositorio; la configuración guarda solo su SHA-256.
var operations = builder.Configuration.GetSection("Operations").Get<OperationsOptions>();
if (operations is { OperatorKeySha256.Count: > 0 })
{
    builder.Services.AddOperations(operations);
}

var app = builder.Build();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
if (operations is { OperatorKeySha256.Count: > 0 })
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapOperations();
}
await app.RunAsync();

// 0, o el código que fijó un módulo que detuvo el host por una falla (por ejemplo, el sender sin TX-C posible).
return Environment.ExitCode;
