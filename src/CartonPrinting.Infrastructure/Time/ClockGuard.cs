namespace CartonPrinting.Infrastructure.Time;

/// <summary>
/// Vigila que el reloj de pared del EdgeHost (<c>UtcNow</c>) avance igual que el monotónico
/// (<c>GetTimestamp</c>). Un ajuste de NTP hacia atrás vuelve permisivas la guarda del claim y la
/// conversión del plazo; por eso, si entre dos comprobaciones la divergencia supera
/// <c>ClockJumpToleranceMs</c> de la revisión activa, la estación se bloquea con CLOCK_JUMP y no se evalúa
/// ningún plazo con ese reloj (sección 10.2, T37). Una instancia por estación: el barrido y el claim de la
/// estación la comparten; el bloqueo queda en SQL y lo ven todos.
/// </summary>
public sealed class ClockGuard
{
    private readonly EdgeClock _clock;
    private readonly object _gate = new();
    private DateTimeOffset _wall;
    private long _mono;

    public ClockGuard(EdgeClock clock)
    {
        _clock = clock;
        _wall = clock.TimeProvider.GetUtcNow();
        _mono = clock.GetTimestamp();
    }

    /// <summary>Divergencia en milisegundos desde la comprobación anterior; vuelve a anclar ambos relojes.</summary>
    public double Check()
    {
        lock (_gate)
        {
            var wall = _clock.TimeProvider.GetUtcNow();
            var mono = _clock.GetTimestamp();
            var wallAdvance = (wall - _wall).TotalMilliseconds;
            var monoAdvance = _clock.GetElapsedTime(_mono, mono).TotalMilliseconds;
            _wall = wall;
            _mono = mono;
            return Math.Abs(wallAdvance - monoAdvance);
        }
    }
}
