using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.Recovery;

/// <summary>Pasos del arranque del EdgeHost (ESTRATEGIA_RECUPERACION.md, sección 1; DOCUMENTO_PRINCIPAL, orden de arranque).</summary>
public enum StartupStage
{
    /// <summary>1. LabelingReady permanece en falso: el PLC aplica su política mientras el servicio no está listo.</summary>
    LabelingReadyLow = 1,

    /// <summary>2. Lock de sistema operativo y PrinterLease por impresora: ninguna otra instancia puede reclamar.</summary>
    Ownership = 2,

    /// <summary>3. TX-R0: SENDING → UNKNOWN (intentos y jobs) de las impresoras propias.</summary>
    SendingRecovered = 3,

    /// <summary>4. Vaciar la cola del dispositivo si la capacidad existe.</summary>
    DeviceQueue = 4,

    /// <summary>5. Revisión activa, épocas, marca alta por época y PrinterSendOrder; vigilancia del reloj.</summary>
    StateLoaded = 5,

    /// <summary>6. Barrido TX-S inmediato por estación.</summary>
    Swept = 6,

    /// <summary>7. Suscripción OPC UA (la primera notificación es READ).</summary>
    Subscribed = 7,

    /// <summary>8. LabelingReady en verdadero: solo ahora la estación etiqueta.</summary>
    LabelingReady = 8,
}

/// <summary>
/// Arranque ordenado del EdgeHost. Cada módulo registra sus pasos en la etapa que le corresponde (al construirse,
/// antes de que arranque ningún servicio) y espera la etapa que necesita con <see cref="WhenReached"/>. Las etapas se
/// ejecutan en orden; dentro de una etapa, los pasos en el orden en que se registraron. Si un paso falla, la etapa
/// se reintenta con espera creciente sin repetir los pasos que ya terminaron, y ninguna etapa posterior empieza:
/// el servicio no queda listo a medias (falla cerrada).
/// </summary>
public sealed class StartupCoordinator(TimeProvider time, ILogger<StartupCoordinator>? logger = null)
{
    private ILogger _logger = logger ?? NullLogger<StartupCoordinator>.Instance;
    private readonly object _gate = new();
    private readonly List<(StartupStage Stage, string Name, Func<CancellationToken, Task> Run)> _steps = [];
    private readonly Dictionary<StartupStage, TaskCompletionSource> _reached =
        Enum.GetValues<StartupStage>().ToDictionary(s => s, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    private readonly List<string> _log = [];
    private bool _started;

    /// <summary>Pasos ejecutados, en orden, como "etapa:nombre".</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Pasos que fallaron (cada reintento cuenta), como "etapa:nombre".</summary>
    public int Failures { get; private set; }

    public void Add(StartupStage stage, string name, Func<CancellationToken, Task> step)
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException($"El arranque ya empezó: el paso {stage}:{name} se registró tarde.");
            }

            _steps.Add((stage, name, step));
        }
    }

    /// <summary>Se completa cuando terminaron todos los pasos de la etapa (y de las anteriores).</summary>
    public Task WhenReached(StartupStage stage) => _reached[stage].Task;

    /// <summary>
    /// Ejecuta las etapas. El EdgeHost crea el coordinador antes de tener sus servicios, así que pasa aquí su logger: sin él,
    /// los fallos del arranque no quedarían en ningún registro (revisión de la Fase 5, M1).
    /// </summary>
    public async Task RunAsync(CancellationToken ct, ILogger? logger = null)
    {
        if (logger is not null)
        {
            _logger = logger;
        }

        List<(StartupStage Stage, string Name, Func<CancellationToken, Task> Run)> steps;
        lock (_gate)
        {
            _started = true;
            steps = [.. _steps];
        }

        foreach (var stage in Enum.GetValues<StartupStage>())
        {
            var pending = new Queue<(StartupStage Stage, string Name, Func<CancellationToken, Task> Run)>(steps.Where(s => s.Stage == stage));
            var backoff = TimeSpan.FromMilliseconds(200);
            while (pending.Count > 0)
            {
                var step = pending.Peek();
                try
                {
                    await step.Run(ct);
                    pending.Dequeue();
                    lock (_gate)
                    {
                        _log.Add($"{stage}:{step.Name}");
                    }

                    backoff = TimeSpan.FromMilliseconds(200);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Failures++;
                    _logger.LogError(ex, "Arranque: falló {Stage}:{Step}; se reintenta en {Backoff} sin pasar a la etapa siguiente", stage, step.Name, backoff);
                    await Task.Delay(backoff, time, ct);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }

            _logger.LogInformation("Arranque: etapa {Stage} completa", stage);
            _reached[stage].TrySetResult();
        }
    }
}
