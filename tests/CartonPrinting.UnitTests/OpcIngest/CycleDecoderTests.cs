using CartonPrinting.Contracts;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;

namespace CartonPrinting.UnitTests.OpcIngest;

/// <summary>
/// Decodificación del registro latched: solo StatusCode Good produce un registro; sin identidad no hay
/// registro; el eco de cierre distinto del CycleId marca el registro como incoherente (T13).
/// </summary>
public sealed class CycleDecoderTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, 123, DateTimeKind.Utc);
    private static readonly CycleDecoder Decoder = new(new StationSettings("PLANT-01", "LINE-01", "P01L01", "ns=2;s=LINE-01.CartonCycleRecord"));

    private static RawNotification Raw(string? value, bool good = true, bool overflow = false, string transport = Transport.Subscription) =>
        new(value, good, good ? 0u : 0x40000000u, overflow, transport, Now.AddTicks(4567), 99, Now.AddMilliseconds(-5), Now.AddMilliseconds(-1), 7,
            "ns=2;s=LINE-01.CartonCycleRecord");

    [Fact]
    public void A_good_latched_record_becomes_a_cycle_record_with_its_transport_and_overflow()
    {
        var result = Decoder.Decode(Raw("""
            {"plcEpoch":42,"cycleId":"918273","detectionTimestamp":"2026-10-12T18:04:11.204Z","workOrderId":"WO-2026-4815","cartonId":null,"closingCycleId":"918273"}
            """, overflow: true, transport: Transport.Republish));
        var record = Assert.IsType<CycleRecord>(result.Record);
        Assert.Equal((42L, 918273UL, "WO-2026-4815"), (record.PlcEpoch, record.CycleId, record.WorkOrderId));
        Assert.Equal(new DateTime(2026, 10, 12, 18, 4, 11, 204, DateTimeKind.Utc), record.DetectionTimestamp);
        Assert.Equal((Transport.Republish, true, true), (record.Transport, record.QueueOverflow, record.Coherent));
        Assert.Equal(Now, record.ReceivedAt);   // truncado a milisegundos
        Assert.Equal(99L, record.ReceivedTimestamp);
    }

    [Fact]
    [Trait("Spec", "T13")]
    public void A_closing_echo_from_another_cycle_marks_the_record_incoherent_and_no_echo_cannot_be_checked()
    {
        var mixed = Decoder.Decode(Raw("""{"plcEpoch":42,"cycleId":"501","workOrderId":"WO-B","closingCycleId":"502"}"""));
        Assert.False(mixed.Record!.Coherent);
        var withoutEcho = Decoder.Decode(Raw("""{"plcEpoch":42,"cycleId":"501","workOrderId":"WO-B"}"""));
        Assert.True(withoutEcho.Record!.Coherent);
    }

    [Theory]
    [InlineData(null, true, DecodeResult.Empty)]
    [InlineData("", true, DecodeResult.Empty)]
    [InlineData("""{"plcEpoch":42,"cycleId":"501"}""", false, DecodeResult.StatusNotGood)]
    [InlineData("no es json", true, DecodeResult.Malformed)]
    [InlineData("""{"cycleId":"501"}""", true, DecodeResult.Malformed)]
    [InlineData("""{"plcEpoch":42}""", true, DecodeResult.Malformed)]
    [InlineData("""{"plcEpoch":4294967296,"cycleId":"1"}""", true, DecodeResult.Malformed)]
    [InlineData("""{"plcEpoch":42,"cycleId":501}""", true, DecodeResult.Malformed)]
    [InlineData("""{"plcEpoch":42,"cycleId":"0501"}""", true, DecodeResult.Malformed)]
    [InlineData("""{"plcEpoch":42,"cycleId":"1","detectionTimestamp":"ayer"}""", true, DecodeResult.Malformed)]
    public void Nothing_without_good_status_and_identity_reaches_registration(string? value, bool good, string rejection)
    {
        var result = Decoder.Decode(Raw(value, good));
        Assert.Null(result.Record);
        Assert.Equal(rejection, result.Rejection);
    }
}
