using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Orchestration;

/// <summary>
/// Clasificación de secuencia de la sección 9.2 y del diagrama 08, sin SQL: épocas, marca alta, huecos,
/// tardíos, reinicio sospechado, disminución ambigua y rebote. Las pruebas de fallas (FaultTests)
/// repiten cada caso del catálogo contra SQL Server real.
/// </summary>
public sealed class SequenceClassifierTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static SequenceClassifier WithMark(long epoch, ulong highWater, DateTime? highWaterTimestamp) =>
        new([new EpochMark(epoch, T0.AddHours(-1), highWater, highWaterTimestamp)]);

    private static SequenceVerdict.Register Register(SequenceVerdict v) => Assert.IsType<SequenceVerdict.Register>(v);

    [Fact]
    public void First_record_of_a_station_accepts_its_epoch_as_initial_and_then_registers_without_a_gap()
    {
        var c = new SequenceClassifier([]);
        var record = TestRevisions.Cycle(42, 500, T0, T0);
        var accept = Assert.IsType<SequenceVerdict.AcceptEpoch>(c.Classify(record, TestRevisions.Revision(), T0));
        Assert.Equal((42L, PlcEpochStore.Initial), (accept.Epoch, accept.AcceptanceMode));

        c.AcceptEpoch(42, T0);
        var first = Register(c.Classify(record, TestRevisions.Revision(), T0));
        Assert.Equal(SequenceCase.FirstOfEpoch, first.Case);
        Assert.Null(first.Decision.Gap);
        Assert.Null(first.Decision.TerminalStatus);
    }

    [Fact]
    [Trait("Spec", "T35")]
    public void Next_cycle_is_in_order_and_a_jump_opens_a_gap_with_its_grace()
    {
        var c = WithMark(42, 300, T0);
        Assert.Equal(SequenceCase.InOrder, Register(c.Classify(TestRevisions.Cycle(42, 301, T0.AddSeconds(1), T0), TestRevisions.Revision(), T0)).Case);

        var gap = Register(c.Classify(TestRevisions.Cycle(42, 310, T0.AddSeconds(10), T0), TestRevisions.Revision(), T0));
        Assert.Equal(SequenceCase.Gap, gap.Case);
        Assert.Equal(new GapOpen(301, 309, T0.AddMilliseconds(5000)), gap.Decision.Gap);
        Assert.Null(gap.Decision.TerminalStatus);
    }

    [Fact]
    [Trait("Spec", "T30")]
    public void Lower_cycle_with_an_earlier_timestamp_is_a_legitimate_late_record()
    {
        var c = WithMark(42, 2000, T0);
        var late = Register(c.Classify(TestRevisions.Cycle(42, 1998, T0.AddMilliseconds(-800), T0.AddSeconds(1)), TestRevisions.Revision(), T0));
        Assert.Equal(SequenceCase.Late, late.Case);
        Assert.Null(late.Decision.Anomaly);
        Assert.Null(late.Decision.TerminalStatus);
    }

    [Fact]
    [Trait("Spec", "T24")]
    public void Lower_cycle_with_a_later_timestamp_is_reset_suspected_and_blocks_the_station()
    {
        var c = WithMark(42, 5000, T0);
        var reset = Register(c.Classify(TestRevisions.Cycle(42, 1, T0.AddSeconds(1), T0.AddSeconds(1)), TestRevisions.Revision(), T0));
        Assert.Equal(SequenceCase.ResetSuspected, reset.Case);
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.SequenceAnomaly), (reset.Decision.TerminalStatus, reset.Decision.TerminalReason));
        Assert.Equal(new AnomalyInsert(AnomalyType.ResetSuspected, AnomalySeverity.Blocking, reset.Decision.Anomaly!.IncomingRecordJson, true,
            BlockReason.ResetSuspected), reset.Decision.Anomaly);
    }

    [Fact]
    [Trait("Spec", "T45")]
    public void Lower_cycle_without_timestamps_is_an_ambiguous_decrease_warning_then_blocking_when_repeated()
    {
        var c = WithMark(42, 700, null);
        var first = TestRevisions.Cycle(42, 650, null, T0);
        var warning = Register(c.Classify(first, TestRevisions.Revision(DeadlineReference.GatewayReceipt), T0));
        Assert.Equal(SequenceCase.AmbiguousDecrease, warning.Case);
        Assert.Equal((AnomalySeverity.Warning, false), (warning.Decision.Anomaly!.Severity, warning.Decision.Anomaly.BlockStation));
        Assert.Equal(DetectionReason.SequenceAnomaly, warning.Decision.TerminalReason);

        c.Observe(first, SequenceCase.AmbiguousDecrease, RegistrationOutcome.Registered);
        var repeated = Register(c.Classify(TestRevisions.Cycle(42, 651, null, T0), TestRevisions.Revision(DeadlineReference.GatewayReceipt), T0));
        Assert.Equal((AnomalySeverity.Blocking, true, BlockReason.AmbiguousDecrease),
            (repeated.Decision.Anomaly!.Severity, repeated.Decision.Anomaly.BlockStation, repeated.Decision.Anomaly.BlockReason));

        // Un ciclo en orden entre medio rompe la racha.
        c.Observe(TestRevisions.Cycle(42, 701, null, T0), SequenceCase.InOrder, RegistrationOutcome.Registered);
        var again = Register(c.Classify(TestRevisions.Cycle(42, 652, null, T0), TestRevisions.Revision(DeadlineReference.GatewayReceipt), T0));
        Assert.Equal(AnomalySeverity.Warning, again.Decision.Anomaly!.Severity);
    }

    [Fact]
    [Trait("Spec", "T25")]
    public void Epoch_changes_follow_the_policy_and_unexpected_epochs_are_retained()
    {
        var auto = WithMark(42, 5000, T0);
        var accepted = Assert.IsType<SequenceVerdict.AcceptEpoch>(auto.Classify(TestRevisions.Cycle(43, 1, T0, T0), TestRevisions.Revision(), T0));
        Assert.Equal((43L, PlcEpochStore.AutoIncrementByOne), (accepted.Epoch, accepted.AcceptanceMode));

        var require = TestRevisions.Revision(configure: r => r with { EpochChangePolicy = "REQUIRE_CONFIRMATION" });
        Assert.IsType<SequenceVerdict.RetainEpochUnexpected>(WithMark(42, 5000, T0).Classify(TestRevisions.Cycle(43, 1, T0, T0), require, T0));
        Assert.IsType<SequenceVerdict.RetainEpochUnexpected>(WithMark(42, 5000, T0).Classify(TestRevisions.Cycle(45, 1, T0, T0), TestRevisions.Revision(), T0));
        Assert.IsType<SequenceVerdict.RetainEpochUnexpected>(WithMark(42, 5000, T0).Classify(TestRevisions.Cycle(41, 1, T0, T0), TestRevisions.Revision(), T0));
    }

    [Fact]
    [Trait("Spec", "T25")]
    public void A_previous_accepted_epoch_is_classified_against_its_own_high_water_mark()
    {
        var c = new SequenceClassifier([new EpochMark(42, T0.AddHours(-2), 5000, T0.AddMinutes(-10)), new EpochMark(43, T0.AddHours(-1), 20, T0)]);
        var late = Register(c.Classify(TestRevisions.Cycle(42, 4999, T0.AddMinutes(-11), T0), TestRevisions.Revision(), T0));
        Assert.Equal(SequenceCase.Late, late.Case);
        Assert.IsType<SequenceVerdict.RetainEpochUnexpected>(c.Classify(TestRevisions.Cycle(42, 5001, T0, T0), TestRevisions.Revision(), T0));
    }

    [Fact]
    [Trait("Spec", "T26")]
    public void Advance_faster_than_min_cycle_interval_is_implausible_rate_and_without_timestamp_the_rule_is_off()
    {
        var revision = TestRevisions.Revision(configure: r => r with { MinCycleIntervalMs = 200 });
        var c = WithMark(42, 100, T0);
        var bounce = Register(c.Classify(TestRevisions.Cycle(42, 101, T0.AddMilliseconds(150), T0), revision, T0));
        Assert.Equal((DetectionStatus.Blocked, DetectionReason.ImplausibleRate), (bounce.Decision.TerminalStatus, bounce.Decision.TerminalReason));

        Assert.Null(Register(c.Classify(TestRevisions.Cycle(42, 101, T0.AddMilliseconds(200), T0), revision, T0)).Decision.TerminalStatus);

        // Con un hueco, el avance mínimo es MinCycleIntervalMs por ciclo: 3 ciclos en 500 ms es demasiado rápido.
        Assert.Equal(DetectionReason.ImplausibleRate,
            Register(c.Classify(TestRevisions.Cycle(42, 103, T0.AddMilliseconds(500), T0), revision, T0)).Decision.TerminalReason);

        // Sin DetectionTimestamp (en el registro o en la marca alta) la regla no se aplica.
        Assert.Null(Register(c.Classify(TestRevisions.Cycle(42, 101, null, T0), revision, T0)).Decision.TerminalStatus);
        Assert.Null(Register(WithMark(42, 100, null).Classify(TestRevisions.Cycle(42, 101, T0, T0), revision, T0)).Decision.TerminalStatus);
    }

    [Fact]
    [Trait("Spec", "T01")]
    public void Known_key_with_the_same_fingerprint_is_a_duplicate_without_sql_and_collisions_do_not_move_the_mark()
    {
        var c = WithMark(42, 100, T0);
        var record = TestRevisions.Cycle(42, 101, T0.AddSeconds(1), T0);
        c.Observe(record, SequenceCase.InOrder, RegistrationOutcome.Registered);
        Assert.Equal(101UL, c.Current!.HighWater);
        Assert.IsType<SequenceVerdict.KnownDuplicate>(c.Classify(record with { Transport = Transport.Republish }, TestRevisions.Revision(), T0));

        // Otra huella con la misma clave no es duplicado: va a TX-A, que decide la colisión.
        Assert.IsType<SequenceVerdict.Register>(c.Classify(record with { WorkOrderId = "WO-OTRA" }, TestRevisions.Revision(), T0));
        c.Observe(TestRevisions.Cycle(42, 900, T0, T0), SequenceCase.Gap, RegistrationOutcome.Collision);
        Assert.Equal(101UL, c.Current!.HighWater);
    }
}
