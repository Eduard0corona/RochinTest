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
        Assert.Equal(0, guard.Check(200).DivergenceMs);

        time.Advance(TimeSpan.FromMilliseconds(100));
        time.JumpWall(TimeSpan.FromSeconds(-2));          // NTP retrocede 2 s
        var jump = guard.Check(200);
        Assert.Equal((2000.0, true, true), (jump.DivergenceMs, jump.Jumped, jump.Detected));
        guard.Acknowledge(jump);
        var after = guard.Check(200);
        Assert.Equal((0.0, false), (after.DivergenceMs, after.Jumped));   // vuelve a anclar

        time.JumpWall(TimeSpan.FromMilliseconds(350));    // también hacia adelante
        var forward = guard.Check(500);
        Assert.Equal((350.0, false), (forward.DivergenceMs, forward.Jumped));   // por debajo de la tolerancia no se retiene
    }

    /// <summary>
    /// Revisión de la Fase 3, ALTA-1: el salto queda retenido hasta que el bloqueo se confirma. Si el bloqueo falla,
    /// la comprobación siguiente lo vuelve a informar aunque la divergencia nueva sea cero; el instante verificado es
    /// el de la comprobación.
    /// </summary>
    [Fact]
    public void A_jump_stays_pending_until_the_block_is_acknowledged()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var guard = new ClockGuard(new EdgeClock(time));
        time.Advance(TimeSpan.FromSeconds(5));
        time.JumpWall(TimeSpan.FromSeconds(-5));
        var first = guard.Check(200);
        Assert.True(first.Jumped && first.Detected);

        // El bloqueo falló (sin Acknowledge): la siguiente comprobación sigue informando el salto, sin volver a detectarlo.
        time.Advance(TimeSpan.FromMilliseconds(10));
        var retry = guard.Check(200);
        Assert.Equal((true, false, 5000.0), (retry.Jumped, retry.Detected, retry.DivergenceMs));
        Assert.Equal(time.GetUtcNow().UtcDateTime, retry.VerifiedUtc);

        // Un salto nuevo mientras se bloqueaba el anterior: confirmar el anterior no libera el nuevo.
        time.JumpWall(TimeSpan.FromSeconds(3));
        var second = guard.Check(200);
        guard.Acknowledge(first);
        Assert.True(guard.Check(200).Jumped);
        guard.Acknowledge(second);
        Assert.False(guard.Check(200).Jumped);
    }
}
