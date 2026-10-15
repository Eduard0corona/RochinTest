using CartonPrinting.OpcIngest;
using CartonPrinting.Operations;
using CartonPrinting.Recovery;

namespace CartonPrinting.EdgeHost;

/// <summary>
/// Ejecuta el arranque ordenado (ESTRATEGIA_RECUPERACION.md, sección 1). Los módulos registran sus pasos al
/// construirse; el host construye todos los servicios alojados antes de arrancar el primero, así que cuando este
/// servicio arranca ya están todos.
/// </summary>
public sealed class StartupHostedService(StartupCoordinator startup) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => startup.RunAsync(stoppingToken);
}

/// <summary>Reingreso de registros retenidos por la Operations API, con la ingesta de la estación de este EdgeHost.</summary>
public sealed class IngestReingest(IngestHostedService ingest, IngestHostOptions options) : IAnomalyReingest
{
    public async Task<string> ReingestAsync(string siteId, string stationId, long anomalyId, string performedBy, string resolution, CancellationToken ct)
    {
        if (siteId != options.SiteId || stationId != options.StationId)
        {
            throw new InvalidOperationException($"La estación {siteId}/{stationId} no es la de este EdgeHost.");
        }

        var outcome = await ingest.Service.ReingestAsync(anomalyId, performedBy, resolution, ct);
        return outcome.Result is { } r ? $"{outcome.Kind}:{r.Outcome}:{r.FinalStatus}" : outcome.Kind.ToString();
    }
}
