using CartonPrinting.Contracts;

namespace CartonPrinting.Infrastructure.Time;

/// <summary>
/// Reloj del EdgeHost. El UTC (<c>@EdgeNowUtc</c>) y el monotónico salen del mismo
/// <see cref="TimeProvider"/> inyectado; nunca se usa <c>SYSUTCDATETIME()</c> para decidir un plazo.
/// Todo instante sale truncado a milisegundos, la precisión de <c>DATETIME2(3)</c>.
/// </summary>
public sealed class EdgeClock(TimeProvider timeProvider)
{
    public TimeProvider TimeProvider { get; } = timeProvider;

    /// <summary>UTC del EdgeHost truncado a milisegundos.</summary>
    public DateTime UtcNow => CanonicalTimestamp.Truncate(TimeProvider.GetUtcNow());

    /// <summary>Marca del reloj monotónico (<see cref="TimeProvider.GetTimestamp"/>).</summary>
    public long GetTimestamp() => TimeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) => TimeProvider.GetElapsedTime(startingTimestamp);

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        TimeProvider.GetElapsedTime(startingTimestamp, endingTimestamp);

    /// <summary>Marca monotónica desplazada por una duración (puede ser negativa).</summary>
    public long AddToTimestamp(long timestamp, TimeSpan delta) =>
        timestamp + (long)(delta.Ticks * (double)TimeProvider.TimestampFrequency / TimeSpan.TicksPerSecond);
}
