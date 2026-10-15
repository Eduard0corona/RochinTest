using CartonPrinting.EdgeHost;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Health;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Sql;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.OpcIngest;
using CartonPrinting.Outbox;
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
    builder.Services.AddHostedService(sp => new IngestHostedService(ingest, sp.GetRequiredService<SqlDb>(), sp.GetRequiredService<EdgeClock>(),
        sp.GetRequiredService<CartonMetrics>(), sp.GetRequiredService<ILoggerFactory>(), sp.GetService<CartonPrinting.Orchestration.IErpDemand>()));
}

// Sincronización ERP y barrido TX-S de la estación (Fase 3): con la estación configurada en "Ingest";
// el ERP solo con "ErpSync:BaseUrl".
builder.Services.AddStationServices(builder.Configuration);

// Sender de la impresora de línea (Fase 4): solo con "Printer:PrinterId" y su puerto configurados.
var printer = builder.Configuration.GetSection("Printer").Get<CartonPrinting.EdgeHost.PrinterHostOptions>();
if (!string.IsNullOrWhiteSpace(printer?.PrinterId) && printer.Transport.Port > 0)
{
    builder.Services.AddSingleton(printer);
    builder.Services.AddHostedService<CartonPrinting.EdgeHost.PrinterHostedService>();
}

// Los demás módulos (propiedad de la impresora, Operations API) se registran en sus fases.

var app = builder.Build();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
await app.RunAsync();

// 0, o el código que fijó un módulo que detuvo el host por una falla (por ejemplo, el sender sin TX-C posible).
return Environment.ExitCode;
