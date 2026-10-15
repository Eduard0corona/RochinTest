using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Signals;
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
/// pierde OPC, si el canal está lleno (ARQUITECTURA_TECNICA §2), si SQL Server está declarado no disponible (<see cref="SqlOutage"/>,
/// hasta que la vigilancia de la ingesta lo levanta, ESTRATEGIA_RECUPERACION §3) o si no se puede leer el estado de la estación en
/// SQL dentro de <c>readTimeout</c>. Una lectura lenta baja la señal en ese latido; si sigue sin respuesta en el latido siguiente, o
/// si el error es SQL no disponible, declara la caída: es una sola para el latido, la ingesta y el sender, y los ciclos que llegan
/// durante ella no se registran. Una sola lectura lenta no la declara: la especificación la ata a TX-A (ESTRATEGIA_RECUPERACION §3,
/// "hasta dos reintentos cortos; después se baja LabelingReady"). Una lectura que no respondió queda como operación abandonada:
/// mientras no termine no se lanza otra y la caída no se levanta. Con impresora de
/// línea (<see cref="LinePrinterReadiness"/>), también en falso mientras no está tomada con TX-R0 hecho o su sender está en pausa
/// (ESTRATEGIA_RECUPERACION §1, etapas 2, 3 y 8; §3 Impresora). Si el latido deja de llegar, el PLC aplica su propia política (OT).
/// </summary>
public sealed class LabelingReadyHeartbeat(
    StationSettings station, StationConfigStore stations, ILabelingReadySink sink, CartonMetrics metrics,
    ILogger<LabelingReadyHeartbeat>? logger = null, Func<bool>? overloaded = null, SqlOutage? outage = null, TimeSpan? readTimeout = null,
    LinePrinterReadiness? linePrinter = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<LabelingReadyHeartbeat>.Instance;
    private readonly TimeSpan _readTimeout = readTimeout ?? TimeSpan.FromSeconds(1);

    // Una lectura que no terminó a tiempo: mientras siga pendiente no se lanza otra, y la señal queda en falso.
    private Task? _pendingRead;

    /// <summary>Un latido: calcula la señal, la escribe y la devuelve.</summary>
    public async Task<bool> BeatAsync(CancellationToken ct)
    {
        // Se evalúa en cada latido, también con la estación bloqueada o sin conexión: los descartes de ese periodo
        // no se atribuyen al primer latido posterior.
        var saturated = overloaded?.Invoke() ?? false;
        bool ready;
        if (_pendingRead is { IsCompleted: false })
        {
            // Dos latidos sin respuesta: SQL no responde de forma sostenida.
            if (outage?.Declare() == true)
            {
                _logger.LogError("SQL Server sigue sin responder a la lectura del latido de {Station}: falla cerrada", station.StationId);
            }

            ready = false;
        }
        else
        {
            var read = Task.Run(() => stations.GetActiveAsync(station.SiteId, station.StationId, ct), CancellationToken.None);
            try
            {
                var active = await read.WaitAsync(_readTimeout, ct);
                ready = active is { State.LabelingEnabled: true } && sink.Connected && !saturated;
            }
            catch (TimeoutException)
            {
                _pendingRead = read.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                outage?.TrackAbandoned(read);
                _logger.LogWarning("LabelingReady: el estado de {Station} no se leyó en {Timeout}; se señala no lista", station.StationId, _readTimeout);
                ready = false;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "LabelingReady: no se pudo leer el estado de {Station}; se señala no lista", station.StationId);
                if (SqlAvailability.IsUnavailable(ex) && outage?.Declare() == true)
                {
                    _logger.LogError("SQL Server no disponible para la lectura del latido de {Station}: falla cerrada", station.StationId);
                }

                ready = false;
            }
        }

        // Se consulta después de la lectura: una declaración de la ingesta durante la lectura no se pierde.
        ready = ready && outage is not { Declared: true } && linePrinter is not { Ready: false };

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
