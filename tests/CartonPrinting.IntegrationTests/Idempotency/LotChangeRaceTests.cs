using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.IntegrationTests.Idempotency;

/// <summary>
/// T10 · Cambio de lote en carrera. Con el snapshot R1 vigente se crea el job; llega R2 del ERP
/// mientras el job está READY y un proceso intenta actualizar Payload: TR_PrintJob_Immutable lo
/// rechaza (51005). El job conserva R1, Payload y PayloadHash idénticos a su creación, y el
/// resultado publicado lleva el lote de R1. En el simulador la etiqueta lleva el lote de R1.
/// </summary>
[Trait("Spec", "T10")]
public sealed class LotChangeRaceTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t10", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Job_keeps_revision_r1_payload_and_hash_when_r2_arrives_while_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.None, EvidenceWindowMs = null }, ct);
        var job = await s.ReadyJobAsync(5001, ct);
        var created = await s.JobAsync(job, ct);
        Assert.Equal("R1", created.ErpRevision);
        Assert.Equal("LOT-20261012-A", created.LotId);

        // Llega R2 del ERP mientras el job está READY.
        s.AdvanceMs(5);
        Assert.True(await s.AddSnapshotAsync("R2", "SKU-10245", "LOT-20261012-B", ct));

        // Un proceso intenta reescribir el contenido congelado: el trigger lo rechaza.
        var r2Payload = created.Payload.Replace("LOT-20261012-A", "LOT-20261012-B", StringComparison.Ordinal);
        var ex = await s.FailsAsync("UPDATE dbo.PrintJob SET Payload = @p WHERE PrintJobId = @id",
            c => c.Parameters.AddRange([new SqlParameter("@p", r2Payload), new SqlParameter("@id", job)]), ct: ct);
        Assert.Equal(51005, ex.Number);
        foreach (var column in new[] { "ErpRevision = N'R2'", "LotId = N'LOT-20261012-B'", "PayloadHash = 0x" + new string('0', 64) })
        {
            var e = await s.FailsAsync($"UPDATE dbo.PrintJob SET {column} WHERE PrintJobId = @id",
                c => c.Parameters.AddWithValue("@id", job), ct: ct);
            Assert.Equal(51005, e.Number);
        }

        var after = await s.JobAsync(job, ct);
        Assert.Equal(created.Payload, after.Payload);
        Assert.Equal(created.PayloadHash, after.PayloadHash);
        Assert.Equal("R1", after.ErpRevision);
        Assert.Equal(JobStatus.Ready, after.Status);

        // El job sigue su camino con R1: la etiqueta transmitida lleva el lote de R1.
        var printer = new CountingPrinter();
        var claim = (await s.Claims.TryClaimAsync(job, s.Lease, ct))!;
        Assert.True(claim.Claimed);
        Assert.Contains("LOT LOT-20261012-A", claim.Context.Job.Payload, StringComparison.Ordinal);
        printer.Write(claim.Context.Job.Payload);
        await s.TransmitAsync(claim.AttemptId, ct);

        var result = Assert.Single(await s.ResultsAsync(job, ct));
        Assert.Equal("LOT-20261012-A", result.GetProperty("lotId").GetString());
        Assert.Equal(created.LabelSerial, result.GetProperty("labelSerial").GetString());

        // Una detección nueva, posterior a R2, sí usa R2: el snapshot no se mezcla entre jobs.
        var next = await s.ReadyJobAsync(5002, ct);
        var nextRow = await s.JobAsync(next, ct);
        Assert.Equal("R2", nextRow.ErpRevision);
        Assert.Equal("LOT-20261012-B", nextRow.LotId);
    }
}
