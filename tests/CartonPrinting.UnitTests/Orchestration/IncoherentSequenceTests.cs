using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Orchestration;

/// <summary>
/// Registros incoherentes en la clasificación de secuencia (MEDIA-3 de la segunda revisión de la Fase 2): la misma
/// regla en memoria que al recargar desde SQL. El CycleId mueve la marca alta y abre huecos; la hora no cuenta y
/// el registro no acepta épocas.
/// </summary>
public sealed class IncoherentSequenceTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static SequenceClassifier WithMark(long epoch, ulong highWater, DateTime? highWaterTimestamp) =>
        new([new EpochMark(epoch, T0.AddHours(-1), highWater, highWaterTimestamp)]);

    private static SequenceVerdict.Register Register(SequenceVerdict v) => Assert.IsType<SequenceVerdict.Register>(v);

    private static RegistrationResult Result(string status, string? reason) =>
        new(RegistrationOutcome.Registered, Guid.NewGuid(), status, reason, null, null, null, []);

    [Fact]
    [Trait("Spec", "T13")]
    public void An_incoherent_record_above_the_mark_opens_its_gap_and_moves_the_mark_without_its_timestamp()
    {
        var c = WithMark(42, 100, T0);
        var revision = TestRevisions.Revision(configure: r => r with { MinCycleIntervalMs = 1000 });
        var mixed = TestRevisions.Cycle(42, 105, T0.AddMilliseconds(1), T0) with { Coherent = false };

        var verdict = Register(c.Classify(mixed, revision, T0));
        Assert.Equal(SequenceCase.Gap, verdict.Case);
        Assert.Equal(new GapOpen(101, 104, T0.AddMilliseconds(5000)), verdict.Decision.Gap);
        Assert.Null(verdict.Decision.TerminalStatus);   // su hora no decide un rebote (IMPLAUSIBLE_RATE)

        c.Observe(mixed, verdict.Case, Result(DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot));
        Assert.Equal((105UL, (DateTime?)null), (c.Current!.HighWater, c.Current.HighWaterTimestamp));
    }

    [Fact]
    [Trait("Spec", "T13")]
    public void An_incoherent_record_at_or_below_the_mark_has_no_sequence_effect_and_keeps_the_ambiguous_streak()
    {
        var c = WithMark(42, 700, null);
        var gateway = TestRevisions.Revision(DeadlineReference.GatewayReceipt);
        var first = TestRevisions.Cycle(42, 650, null, T0);
        Assert.Equal(SequenceCase.AmbiguousDecrease, Register(c.Classify(first, gateway, T0)).Case);
        c.Observe(first, SequenceCase.AmbiguousDecrease, RegistrationOutcome.Registered);

        var mixed = TestRevisions.Cycle(42, 600, T0.AddSeconds(5), T0) with { Coherent = false };
        var below = Register(c.Classify(mixed, gateway, T0));
        Assert.Equal(SequenceCase.IncoherentBelowMark, below.Case);
        Assert.Equal(new SequenceDecision(), below.Decision);
        c.Observe(mixed, below.Case, Result(DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot));
        Assert.Equal(700UL, c.Current!.HighWater);

        // Un incoherente entre dos descensos ambiguos no corta la racha: el segundo bloquea la estación.
        var repeated = Register(c.Classify(TestRevisions.Cycle(42, 651, null, T0), gateway, T0));
        Assert.Equal((AnomalySeverity.Blocking, true), (repeated.Decision.Anomaly!.Severity, repeated.Decision.Anomaly.BlockStation));
    }

    [Fact]
    [Trait("Spec", "T25")]
    public void An_incoherent_record_never_accepts_an_epoch()
    {
        var empty = new SequenceClassifier([]);
        Assert.IsType<SequenceVerdict.RetainEpochUnexpected>(
            empty.Classify(TestRevisions.Cycle(42, 1, T0, T0) with { Coherent = false }, TestRevisions.Revision(), T0));

        var auto = WithMark(42, 5000, T0);
        Assert.IsType<SequenceVerdict.AcceptEpoch>(auto.Classify(TestRevisions.Cycle(43, 1, T0, T0), TestRevisions.Revision(), T0));
        Assert.IsType<SequenceVerdict.RetainEpochUnexpected>(
            auto.Classify(TestRevisions.Cycle(43, 1, T0, T0) with { Coherent = false }, TestRevisions.Revision(), T0));
    }

    [Fact]
    public void A_detection_blocked_with_incoherent_snapshot_does_not_give_its_time_to_the_mark()
    {
        // SI-04: un registro coherente sin orden de trabajo queda BLOCKED con INCOHERENT_SNAPSHOT; SQL no los distingue.
        var c = WithMark(42, 100, T0);
        var record = TestRevisions.Cycle(42, 101, T0.AddSeconds(1), T0) with { WorkOrderId = null };
        c.Observe(record, SequenceCase.InOrder, Result(DetectionStatus.Blocked, DetectionReason.IncoherentSnapshot));
        Assert.Equal((101UL, (DateTime?)null), (c.Current!.HighWater, c.Current.HighWaterTimestamp));

        c.Observe(TestRevisions.Cycle(42, 102, T0.AddSeconds(2), T0), SequenceCase.InOrder, Result(DetectionStatus.WaitingErp, null));
        Assert.Equal((102UL, (DateTime?)T0.AddSeconds(2)), (c.Current!.HighWater, c.Current.HighWaterTimestamp));
    }
}
