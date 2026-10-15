namespace CartonPrinting.Testing;

/// <summary>
/// Reloj de prueba con reloj de pared y monotónico independientes: <see cref="Advance"/> mueve los dos y
/// <see cref="JumpWall"/> mueve solo el de pared, como un ajuste de NTP. (FakeTimeProvider deriva el
/// monotónico del de pared y no puede retroceder.)
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _wall = start;
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _wall;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _wall += delta;
            _ticks += delta.Ticks;
        }
    }

    public void JumpWall(TimeSpan delta)
    {
        lock (_gate)
        {
            _wall += delta;
        }
    }
}
