using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.Sequence;

/// <summary>
/// T35 · Hueco de secuencia sin timestamps (perfil mínimo, PLC sin FIFO). Marca alta en 300; llega el 310
/// sin datos de 301 a 309: SequenceGap 301-309 OPEN y, al vencer la gracia, CLOSED_UNRECOVERED con
/// UnrecoveredCount 9; ningún CartonDetected ni PrintJobResult para el hueco; cycles_unobserved_total = 9.
/// </summary>
[Trait("Spec", "T35")]
public sealed class SequenceGapTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t35", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Missing_cycles_are_counted_and_closed_never_fabricated()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        var ingest = s.Ingest();
        await ingest.ReloadAsync(ct);

        Assert.Equal(SequenceCase.FirstOfEpoch, (await ingest.ProcessRecordAsync(s.Cycle(300), ct)).Case);
        var opening = await ingest.ProcessRecordAsync(s.Cycle(310), ct);
        Assert.Equal(SequenceCase.Gap, opening.Case);
        Assert.Equal(DetectionStatus.JobCreated, opening.Result!.FinalStatus);   // el 310 tiene su propio plazo (PLC_TIMESTAMP)

        var gap = await GapAsync(s, ct);
        Assert.Equal((301m, 309m, "OPEN"), (gap.From, gap.To, gap.Status));
        Assert.Empty(await ingest.SweepGapsAsync(ct));   // antes de la gracia no se cierra
        Assert.Equal(1.0, s.Metrics.Gauge(MetricNames.SequenceGapsOpen, ("station", s.StationId)));

        s.AdvanceMs(s.Revision.GapGraceMs + 1);
        var closed = Assert.Single(await ingest.SweepGapsAsync(ct));
        Assert.Equal(("CLOSED_UNRECOVERED", 9UL), (closed.Status, closed.UnrecoveredCount));
        gap = await GapAsync(s, ct);
        Assert.Equal(("CLOSED_UNRECOVERED", 9m), (gap.Status, gap.Unrecovered));
        Assert.Equal(9, s.Metrics.Total(MetricNames.CyclesUnobserved));
        Assert.Equal(0.0, s.Metrics.Gauge(MetricNames.SequenceGapsOpen, ("station", s.StationId)));

        // Ninguna detección fabricada y ningún evento para el hueco.
        Assert.Equal(0, await s.CountAsync("FROM dbo.CartonDetection WHERE StationId = @st AND CycleId BETWEEN 301 AND 309",
            c => c.Parameters.AddWithValue("@st", s.StationId), ct));
        var cycles = (await s.OutboxAsync(ct)).Where(e => e.Type == "CartonDetected")
            .Select(e => System.Text.Json.JsonDocument.Parse(e.Payload).RootElement.GetProperty("cycleId").GetString())
            .Distinct().Order().ToList();
        Assert.Equal(["300", "310"], cycles);
    }

    private static async Task<(decimal From, decimal To, string Status, decimal? Unrecovered)> GapAsync(StationScenario s, CancellationToken ct)
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT FromCycleId, ToCycleId, Status, UnrecoveredCount FROM dbo.SequenceGap WHERE StationId = @st", conn);
        cmd.Parameters.AddWithValue("@st", s.StationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct));
        var row = (r.GetDecimal(0), r.GetDecimal(1), r.GetString(2), r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3));
        Assert.False(await r.ReadAsync(ct));
        return row;
    }
}
