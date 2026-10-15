using CartonPrinting.Infrastructure.Time;

namespace CartonPrinting.Infrastructure.Signals;

/// <summary>
/// La caída de SQL Server del perfil mínimo (ESTRATEGIA_RECUPERACION §3), una sola para el proceso. La declaran la ingesta (TX-A), el
/// latido (lectura de la estación) o el sender de línea (TX-B); la levanta solo la vigilancia de la ingesta. Mientras está declarada,
/// LabelingReady está en falso y ningún ciclo se registra; un ciclo recibido antes de la última recuperación es parte del hueco.
/// <list type="bullet">
/// <item>Una operación de SQL que se dejó de esperar (sin respuesta en su tiempo máximo) se sigue con
/// <see cref="TrackAbandoned"/>: mientras alguna no termina, la caída no se levanta, porque esa conexión colgada todavía puede
/// responder o tomar otra vez la estación.</item>
/// <item>Una recaída poco después de levantarla (<see cref="RelapseWindow"/>) duplica la espera mínima antes de la próxima
/// recuperación (<see cref="Relapses"/>), hasta 64 veces: una falla intermitente no hace oscilar LabelingReady.</item>
/// </list>
/// Las marcas son del reloj monotónico del EdgeHost, el mismo que sella la recepción de cada ciclo.
/// </summary>
public sealed class SqlOutage(EdgeClock clock)
{
    public static readonly TimeSpan RelapseWindow = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private volatile bool _declared;
    private long _declaredAt;
    private long _recoveredAt = long.MinValue;
    private int _abandoned;
    private int _relapses;

    public bool Declared => _declared;

    /// <summary>Marca monotónica de la última recuperación (long.MinValue si nunca hubo una caída).</summary>
    public long RecoveredAt => Interlocked.Read(ref _recoveredAt);

    /// <summary>Operaciones abandonadas que todavía no terminaron.</summary>
    public int AbandonedOperations => Volatile.Read(ref _abandoned);

    /// <summary>Recaídas seguidas dentro de <see cref="RelapseWindow"/> desde la última recuperación.</summary>
    public int Relapses => Volatile.Read(ref _relapses);

    /// <summary>Cuánto lleva declarada (cero si no lo está).</summary>
    public TimeSpan DeclaredFor => _declared ? clock.GetElapsedTime(Interlocked.Read(ref _declaredAt)) : TimeSpan.Zero;

    /// <summary>Declara la caída. Devuelve true solo para quien la declaró, para registrarla una vez.</summary>
    public bool Declare()
    {
        lock (_gate)
        {
            if (_declared)
            {
                return false;
            }

            var now = clock.GetTimestamp();
            var recovered = RecoveredAt;
            _relapses = recovered != long.MinValue && clock.GetElapsedTime(recovered, now) < RelapseWindow ? Math.Min(_relapses + 1, 6) : 0;
            Interlocked.Exchange(ref _declaredAt, now);
            _declared = true;
            return true;
        }
    }

    /// <summary>
    /// La levanta la vigilancia, después de recargar el estado y de comprobar el camino de TX-A. La marca de recuperación se escribe
    /// antes de bajar la declaración: quien lee la declaración y después la marca nunca ve una caída levantada sin su marca.
    /// </summary>
    public void Recover()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _recoveredAt, clock.GetTimestamp());
            _declared = false;
        }
    }

    /// <summary>Sigue una operación de SQL que se dejó de esperar hasta que termine (y observa su excepción).</summary>
    public void TrackAbandoned(Task operation)
    {
        Interlocked.Increment(ref _abandoned);
        _ = operation.ContinueWith(t =>
        {
            _ = t.Exception;
            Interlocked.Decrement(ref _abandoned);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
