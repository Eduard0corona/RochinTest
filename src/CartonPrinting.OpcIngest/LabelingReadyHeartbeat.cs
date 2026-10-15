using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.OpcIngest;

/// <summary>Destino del latido LabelingReady (el PLC; en pruebas, un doble).</summary>
public interface ILabelingReadySink
{
    bool Connected { get; }

    Task WriteLabelingReadyAsync(bool ready, CancellationToken ct);
}

/// <summary>
/// Latido LabelingReady (contrato con el PLC, sección 4): el servicio escribe true mientras la estación
/// está habilitada, la ingesta conectada y su canal sin saturar; false si la estación está bloqueada, si se
/// pierde OPC, si el canal está lleno (ARQUITECTURA_TECNICA §2) o si no se puede leer su estado en SQL. Si el
/// latido deja de llegar, el PLC aplica su propia política (OT).
/// </summary>
public sealed class LabelingReadyHeartbeat(
    StationSettings station, StationConfigStore stations, ILabelingReadySink sink, CartonMetrics metrics,
    ILogger<LabelingReadyHeartbeat>? logger = null, Func<bool>? overloaded = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<LabelingReadyHeartbeat>.Instance;

    /// <summary>Un latido: calcula la señal, la escribe y la devuelve.</summary>
    public async Task<bool> BeatAsync(CancellationToken ct)
    {
        bool ready;
        try
        {
            var active = await stations.GetActiveAsync(station.SiteId, station.StationId, ct);
            ready = active is { State.LabelingEnabled: true } && sink.Connected && !(overloaded?.Invoke() ?? false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LabelingReady: no se pudo leer el estado de {Station}; se señala no lista", station.StationId);
            ready = false;
        }

        if (sink.Connected)
        {
            await sink.WriteLabelingReadyAsync(ready, ct);
        }

        metrics.SetGauge(MetricNames.LabelingReady, ready ? 1 : 0, ("station", station.StationId));
        return ready;
    }

    public async Task RunAsync(TimeSpan interval, TimeProvider time, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await BeatAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "LabelingReady: escritura fallida en {Station}", station.StationId);
                }

                await Task.Delay(interval, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Al detener: señal baja, si todavía hay conexión.
            if (sink.Connected)
            {
                try
                {
                    await sink.WriteLabelingReadyAsync(false, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LabelingReady: no se pudo bajar la señal al detener {Station}", station.StationId);
                }
            }
        }
    }
}
