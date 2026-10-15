using CartonPrinting.EdgeHost;
using CartonPrinting.Infrastructure.Health;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Sql;
using CartonPrinting.Infrastructure.Time;
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

// Fase 0: el EdgeHost arranca sin módulos activos. Los hosted services de cada módulo se
// registran en las fases siguientes.

var app = builder.Build();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
await app.RunAsync();
return 0;
