using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Time;

/// <summary>Divergencia entre el reloj de pared y el monotónico (CLOCK_JUMP).</summary>
[Trait("Spec", "T37")]
public sealed class ClockGuardTests
{
    [Fact]
    public void Normal_advance_has_no_divergence_and_a_wall_jump_is_measured_once()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var guard = new ClockGuard(new EdgeClock(time));

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, guard.Check());

        time.Advance(TimeSpan.FromMilliseconds(100));
        time.JumpWall(TimeSpan.FromSeconds(-2));          // NTP retrocede 2 s
        Assert.Equal(2000, guard.Check());
        Assert.Equal(0, guard.Check());                   // vuelve a anclar

        time.JumpWall(TimeSpan.FromMilliseconds(350));    // también hacia adelante
        Assert.Equal(350, guard.Check());
    }
}
