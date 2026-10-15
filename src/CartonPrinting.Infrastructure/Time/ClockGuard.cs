namespace CartonPrinting.Infrastructure.Time;

/// <summary>
/// Resultado de una comprobación del reloj: la divergencia medida, el instante de pared que se verificó
/// (el que debe usar como <c>@EdgeNowUtc</c> quien decida un plazo con esta comprobación) y si hay un salto
/// pendiente de bloquear.
/// </summary>
public sealed record ClockReading(double DivergenceMs, DateTime VerifiedUtc, bool Jumped, bool Detected, long JumpId);

/// <summary>
/// Vigila que el reloj de pared del EdgeHost (<c>UtcNow</c>) avance igual que el monotónico
/// (<c>GetTimestamp</c>). Un ajuste de NTP hacia atrás vuelve permisivas la guarda del claim y la
/// conversión del plazo; por eso, si entre dos comprobaciones la divergencia supera
/// <c>ClockJumpToleranceMs</c> de la revisión activa, la estación se bloquea con CLOCK_JUMP y no se evalúa
/// ningún plazo con ese reloj (sección 10.2, T37). Una instancia por estación: el barrido y el claim de la
/// estación la comparten; el bloqueo queda en SQL y lo ven todos.
/// <para>
/// El salto queda retenido hasta que el bloqueo se confirma en SQL (<see cref="Acknowledge"/>): mientras
/// tanto, toda comprobación lo informa, aunque la divergencia nueva sea cero. Así, si el bloqueo falla, se
/// reintenta en la comprobación siguiente, y un claim concurrente no evalúa su plazo mientras el bloqueo
/// todavía no es visible.
/// </para>
/// </summary>
public sealed class ClockGuard
{
    private readonly EdgeClock _clock;
    private readonly object _gate = new();
    private DateTimeOffset _wall;
    private long _mono;
    private double? _pendingJumpMs;
    private long _jumpId;

    public ClockGuard(EdgeClock clock)
    {
        _clock = clock;
        _wall = clock.TimeProvider.GetUtcNow();
        _mono = clock.GetTimestamp();
    }

    /// <summary>
    /// Compara el avance de los dos relojes desde la comprobación anterior y vuelve a anclarlos. Una divergencia
    /// mayor que la tolerancia queda retenida como salto hasta <see cref="Acknowledge"/>.
    /// </summary>
    public ClockReading Check(int toleranceMs)
    {
        lock (_gate)
        {
            var wall = _clock.TimeProvider.GetUtcNow();
            var mono = _clock.GetTimestamp();
            var wallAdvance = (wall - _wall).TotalMilliseconds;
            var monoAdvance = _clock.GetElapsedTime(_mono, mono).TotalMilliseconds;
            _wall = wall;
            _mono = mono;
            var divergence = Math.Abs(wallAdvance - monoAdvance);
            var detected = divergence > toleranceMs;
            if (detected)
            {
                _pendingJumpMs = Math.Max(_pendingJumpMs ?? 0, divergence);
                _jumpId++;
            }

            return new ClockReading(_pendingJumpMs ?? divergence, Contracts.CanonicalTimestamp.Truncate(wall), _pendingJumpMs is not null, detected,
                _jumpId);
        }
    }

    /// <summary>
    /// El bloqueo CLOCK_JUMP de la estación quedó confirmado en SQL: el salto de esa lectura deja de retenerse,
    /// salvo que se haya detectado otro después.
    /// </summary>
    public void Acknowledge(ClockReading reading)
    {
        lock (_gate)
        {
            if (reading.JumpId == _jumpId)
            {
                _pendingJumpMs = null;
            }
        }
    }
}
