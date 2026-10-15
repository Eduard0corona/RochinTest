using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T30 · Evento OPC antiguo fuera de orden. Marca alta en 2000; el 1998 nunca se registró y llega una
/// retransmisión válida con DetectionTimestamp anterior al de 2000. Plazo vencido: EXPIRED con
/// LATE_ARRIVAL. Plazo vigente: job creado que vence con OUT_OF_ORDER (J03) porque la impresora ya
/// reclamó el 2000. Sin anomalía de reinicio; la estación no se bloquea; ningún envío fuera de orden.
/// </summary>
[Trait("Spec", "T30")]
public sealed class LateEventTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t30", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>1997 y 1999 (hueco 1998) y 2000; el 2000 ya se reclamó y transmitió.</summary>
    private static async Task<(StationScenario S, OpcIngest.StationIngestService Ingest, DateTime T)> HighWaterAt2000Async(TestDatabase db, CancellationToken ct)
    {
        var s = await StationScenario.CreateAsync(db, ct: ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);
        var t = s.Clock.UtcNow.AddMilliseconds(-50);
        await ingest.ProcessRecordAsync(s.Cycle(1997, detectionTimestamp: t), ct);
        Assert.Equal(SequenceCase.Gap, (await ingest.ProcessRecordAsync(s.Cycle(1999, detectionTimestamp: t.AddMilliseconds(20)), ct)).Case);
        var last = await ingest.ProcessRecordAsync(s.Cycle(2000, detectionTimestamp: t.AddMilliseconds(30)), ct);
        var claim = (await s.Claims.TryClaimAsync(last.Result!.PrintJobId!.Value, s.Lease, ct))!;
        Assert.True(claim.Claimed);
        await s.TransmitAsync(claim.AttemptId, ct);
        return (s, ingest, t);
    }

    [Fact]
    public async Task Late_record_still_on_time_creates_a_job_that_expires_out_of_order_at_the_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var (s, ingest, t) = await HighWaterAt2000Async(_db, ct);

        var late = await ingest.ProcessRecordAsync(s.Cycle(1998, Transport.Republish, detectionTimestamp: t.AddMilliseconds(10)), ct);
        Assert.Equal(SequenceCase.Late, late.Case);
        Assert.Equal(DetectionStatus.JobCreated, late.Result!.FinalStatus);
        Assert.Null(late.Result.AnomalyId);

        var claim = (await s.Claims.TryClaimAsync(late.Result.PrintJobId!.Value, s.Lease, ct))!;
        Assert.Equal((false, ClaimRejection.OutOfOrder), (claim.Claimed, claim.Rejection));
        var job = await s.JobAsync(late.Result.PrintJobId.Value, ct);
        Assert.Equal((JobStatus.Expired, JobReason.OutOfOrder), (job.Status, job.Reason));
        Assert.Empty(await s.AttemptsAsync(job.PrintJobId, ct));   // ningún byte del 1998 después del 2000

        await AssertNoResetAnomalyAsync(s, ct);
        Assert.Equal(1, s.Metrics.Total(MetricNames.OutOfOrderDetections));
        Assert.Equal(1, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st AND Status = 'RECOVERED'",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));
    }

    [Fact]
    public async Task Late_record_past_its_deadline_expires_with_late_arrival()
    {
        var ct = TestContext.Current.CancellationToken;
        var (s, ingest, t) = await HighWaterAt2000Async(_db, ct);
        s.AdvanceMs(2000);

        var late = await ingest.ProcessRecordAsync(s.Cycle(1998, Transport.Republish, detectionTimestamp: t.AddMilliseconds(10)), ct);
        Assert.Equal(SequenceCase.Late, late.Case);
        Assert.Equal((DetectionStatus.Expired, DetectionReason.LateArrival), (late.Result!.FinalStatus, late.Result.FinalReason));
        Assert.Null(late.Result.PrintJobId);
        var result = (await s.ResultsAsync(ct: ct)).Single(r => r.GetProperty("cycleId").GetString() == "1998");
        Assert.Equal(("EXPIRED", "LATE_ARRIVAL"), (result.GetProperty("outcome").GetString(), result.GetProperty("reason").GetString()));
        await AssertNoResetAnomalyAsync(s, ct);
    }

    private static async Task AssertNoResetAnomalyAsync(StationScenario s, CancellationToken ct)
    {
        Assert.Equal(0, await s.CountAsync("FROM dbo.SequenceAnomaly WHERE StationId = @st", c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        Assert.True((await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, ct))!.Value.State.LabelingEnabled);
    }
}
