using System.Text.Json;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Testing;

namespace CartonPrinting.IntegrationTests.Operations;

/// <summary>
/// Operations API (Fase 5) contra SQL Server real: identidad autenticada en cada acción manual, rechazos de TX-M y de
/// las restricciones como conflicto, reconciliación negativa y cierre operativo (T40).
/// </summary>
public sealed class OperationsTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f5ops", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement LastResult(IReadOnlyList<JsonElement> results) => results.MaxBy(r => r.GetProperty("resultVersion").GetInt32());

    [Fact]
    public async Task Without_a_valid_operator_identity_nothing_is_done()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        var job = await s.ReadyJobAsync(800, Ct);
        using var anonymous = ops.Anonymous();
        using var impostor = ops.As(OperationsHost.Supervisor, wrongKey: true);

        Assert.Equal(401, (await anonymous.CallAsync($"api/ops/jobs/{job}/cancel", new { reason = "x" }, Ct)).Status);
        Assert.Equal(401, (await impostor.CallAsync($"api/ops/jobs/{job}/cancel", new { reason = "x" }, Ct)).Status);
        Assert.Equal(JobStatus.Ready, (await s.JobAsync(job, Ct)).Status);
        Assert.Equal(0, await s.CountAsync("FROM dbo.OperatorAction", null, Ct));

        // La identidad que queda en OperatorAction es la autenticada, nunca la del cuerpo.
        using var supervisor = ops.As(OperationsHost.Supervisor);
        var (status, body) = await supervisor.CallAsync($"api/ops/jobs/{job}/cancel", new { reason = "Caja retirada", performedBy = "otra.persona" }, Ct);
        Assert.Equal(200, status);
        Assert.Equal(OperationsHost.Supervisor, await s.ScalarAsync<string>("SELECT PerformedBy FROM dbo.OperatorAction WHERE OperatorActionId = @id",
            c => c.Parameters.AddWithValue("@id", body.GetProperty("operatorActionId").GetInt64()), Ct));
        Assert.Equal(404, (await supervisor.CallAsync($"api/ops/jobs/{Guid.NewGuid()}/cancel", new { reason = "x" }, Ct)).Status);
        Assert.Equal(409, (await supervisor.CallAsync($"api/ops/jobs/{job}/cancel", new { reason = "otra vez" }, Ct)).Status);
    }

    /// <summary>
    /// T40 · Dos jobs UNKNOWN con cajas retenidas. Job 1: sin una de las tres pruebas, CK_OperatorAction_Target rechaza
    /// la acción (409, nada cambia); con las tres, OperatorAction RECONCILE_NEGATIVE, intento NOT_PRINTED (A09) y job
    /// FAILED con RECONCILED_NOT_PRINTED. Job 2, sin pruebas y con la caja rechazada: CLOSE_UNRESOLVED y job
    /// CLOSED_UNRESOLVED con CLOSED_CARTON_REJECTED. Los dos PrintJobResult llevan su operatorAction; ninguna etiqueta nueva.
    /// </summary>
    [Fact]
    [Trait("Spec", "T40")]
    public async Task Negative_reconciliation_needs_its_three_proofs_and_closing_unresolved_records_the_carton()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var ops = await OperationsHost.StartAsync(s, Ct);
        using var supervisor = ops.As(OperationsHost.Supervisor);
        var (job1, attempt1) = await s.UnknownJobAsync(810, Ct);
        var (job2, _) = await s.UnknownJobAsync(811, Ct);

        var missing = await supervisor.CallAsync($"api/ops/jobs/{job1}/reconcile-negative", new
        {
            reason = "La impresora confirma que no imprimió",
            deviceNotPrintedEvidence = "Registro del dispositivo: trabajo 810 cancelado",
            deviceQueueClearedEvidence = "Cola vacía a las 10:42",
            cartonInspectionEvidence = (string?)null,
        }, Ct);
        Assert.Equal(409, missing.Status);
        Assert.Equal(547, missing.Body.GetProperty("sqlError").GetInt32());
        Assert.Equal(JobStatus.Unknown, (await s.JobAsync(job1, Ct)).Status);
        Assert.Equal(0, await s.CountAsync("FROM dbo.OperatorAction", null, Ct));

        var (status, body) = await supervisor.CallAsync($"api/ops/jobs/{job1}/reconcile-negative", new
        {
            reason = "La impresora confirma que no imprimió",
            deviceNotPrintedEvidence = "Registro del dispositivo: trabajo 810 cancelado",
            deviceQueueClearedEvidence = "Cola vacía a las 10:42",
            cartonInspectionEvidence = "Caja CJ-810 inspeccionada: sin etiqueta",
        }, Ct);
        Assert.Equal(200, status);
        var reconcile = body.GetProperty("operatorActionId").GetInt64();
        var row1 = await s.JobAsync(job1, Ct);
        Assert.Equal((JobStatus.Failed, JobReason.ReconciledNotPrinted, (long?)reconcile), (row1.Status, row1.Reason, row1.LastOperatorActionId));
        var attempt = Assert.Single(await s.AttemptsAsync(job1, Ct));
        Assert.Equal((attempt1, AttemptOutcome.NotPrinted), (attempt.AttemptId, attempt.Outcome));
        Assert.Equal(3, await s.ScalarAsync<int>("""
            SELECT IIF(DeviceNotPrintedEvidence IS NULL, 0, 1) + IIF(DeviceQueueClearedEvidence IS NULL, 0, 1) + IIF(CartonInspectionEvidence IS NULL, 0, 1)
            FROM dbo.OperatorAction WHERE OperatorActionId = @id AND ActionType = 'RECONCILE_NEGATIVE' AND PerformedBy = @who
            """, c => { c.Parameters.AddWithValue("@id", reconcile); c.Parameters.AddWithValue("@who", OperationsHost.Supervisor); }, Ct));
        var result1 = LastResult(await s.ResultsAsync(job1, Ct));
        Assert.Equal(("FAILED", reconcile), (result1.GetProperty("outcome").GetString(),
            result1.GetProperty("operatorAction").GetProperty("operatorActionId").GetInt64()));

        var closed = await supervisor.CallAsync($"api/ops/jobs/{job2}/close-unresolved",
            new { reason = "Sin pruebas de no impresión; la caja se rechaza", cartonDisposition = "REJECTED" }, Ct);
        Assert.Equal(200, closed.Status);
        var close = closed.Body.GetProperty("operatorActionId").GetInt64();
        var row2 = await s.JobAsync(job2, Ct);
        Assert.Equal((JobStatus.ClosedUnresolved, JobReason.ClosedCartonRejected, (long?)close), (row2.Status, row2.Reason, row2.LastOperatorActionId));
        Assert.Equal("CLOSE_UNRESOLVED", await s.ScalarAsync<string>("SELECT ActionType FROM dbo.OperatorAction WHERE OperatorActionId = @id",
            c => c.Parameters.AddWithValue("@id", close), Ct));
        var result2 = LastResult(await s.ResultsAsync(job2, Ct));
        Assert.Equal(("CLOSED_UNRESOLVED", close), (result2.GetProperty("outcome").GetString(),
            result2.GetProperty("operatorAction").GetProperty("operatorActionId").GetInt64()));

        Assert.Equal(1, s.Metrics.Total(MetricNames.UnknownResolution, ("result", JobReason.ReconciledNotPrinted)));
        Assert.Equal(1, s.Metrics.Total(MetricNames.UnknownResolution, ("result", JobStatus.ClosedUnresolved)));
        Assert.Single(await s.AttemptsAsync(job2, Ct));   // ninguna etiqueta nueva: solo el intento original de cada job
    }
}
