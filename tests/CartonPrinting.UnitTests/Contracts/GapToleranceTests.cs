using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Testing;

namespace CartonPrinting.UnitTests.Contracts;

/// <summary>
/// SI-05: un registro GATEWAY_RECEIPT en vivo que abre un hueco se registra sin plazo (D04), pero la regla
/// de negocio del verificador exigiría un plazo posterior a ReceivedAt. El OutboxWriter tolera esa única
/// regla en ese único caso; cualquier otra combinación se sigue rechazando.
/// </summary>
[Trait("Spec", "T45")]
public sealed class GapToleranceTests
{
    private static CartonDetectedEvent LiveGatewayWithoutDeadline()
    {
        var path = RepoPaths.SpecFile("contracts", "examples", "valid", "carton-detected-observed.json");
        var observed = ContractJson.Deserialize<CartonDetectedEvent>(File.ReadAllText(path));
        return observed with
        {
            Transport = Transport.Subscription,
            QueueOverflow = false,
            DeadlineReference = DeadlineReference.GatewayReceipt,
            DeadlineAt = observed.ReceivedAt,
        };
    }

    [Fact]
    public void Gap_opening_live_gateway_record_without_deadline_tolerates_only_the_deadline_rule()
    {
        var evt = LiveGatewayWithoutDeadline();
        var payload = ContractJson.Serialize(evt);

        var tolerated = OutboxWriter.ToleratedRule(evt, opensGapWithoutDeadline: true);
        Assert.Equal(ContractBusinessRules.DeadlineNotAfterReference, tolerated);
        OutboxWriter.EnsureContract(payload, tolerated);

        var strict = Assert.Throws<ContractViolationException>(() => OutboxWriter.EnsureContract(payload));
        Assert.Contains(ContractBusinessRules.DeadlineNotAfterReference, strict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_gap_the_same_record_is_rejected()
    {
        var evt = LiveGatewayWithoutDeadline();
        Assert.Null(OutboxWriter.ToleratedRule(evt, opensGapWithoutDeadline: false));
        Assert.Throws<ContractViolationException>(() => OutboxWriter.EnsureContract(ContractJson.Serialize(evt)));
    }

    [Fact]
    public void A_fabricated_deadline_or_another_reference_is_not_tolerated()
    {
        var evt = LiveGatewayWithoutDeadline();
        Assert.Null(OutboxWriter.ToleratedRule(evt with { DeadlineAt = evt.ReceivedAt.AddMilliseconds(-1) }, opensGapWithoutDeadline: true));
        Assert.Null(OutboxWriter.ToleratedRule(evt with { DeadlineReference = DeadlineReference.PlcTimestamp }, opensGapWithoutDeadline: true));
    }

    [Fact]
    public void The_tolerance_does_not_hide_any_other_business_rule()
    {
        // Huella de origen alterada: la tolerancia del plazo no la encubre.
        var evt = LiveGatewayWithoutDeadline() with { WorkOrderId = "WO-OTRA" };
        var tolerated = OutboxWriter.ToleratedRule(evt, opensGapWithoutDeadline: true);
        var ex = Assert.Throws<ContractViolationException>(() => OutboxWriter.EnsureContract(ContractJson.Serialize(evt), tolerated));
        Assert.Contains(ContractBusinessRules.FingerprintMismatch, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ContractBusinessRules.DeadlineNotAfterReference, ex.Message, StringComparison.Ordinal);
    }
}
