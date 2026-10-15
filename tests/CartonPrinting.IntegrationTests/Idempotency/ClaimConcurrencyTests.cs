using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.IntegrationTests.Idempotency;

/// <summary>
/// T02 · Concurrencia en el claim. 10 tareas ejecutan el claim del mismo job a la vez: exactamente 1
/// claim afecta 1 fila y deja 1 PrintAttempt SENDING; los demás terminan con @claimed vacío y sin
/// excepción (los triggers tienen la guarda de 0 filas); claim_conflicts_total = 9; ningún evento
/// adicional publicado.
/// </summary>
[Trait("Spec", "T02")]
public sealed class ClaimConcurrencyTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("t02", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Ten_concurrent_claims_have_exactly_one_winner_and_no_exception(int round)
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        var job = await s.ReadyJobAsync((ulong)(1000 + round), ct);
        var outboxBefore = (await s.OutboxAsync(ct)).Count;
        // Plazo a 2 s (initialData de T02): el job se creó con DeadlineAt = detección + 1050 ms; se
        // reclama de inmediato, con el plazo vigente.

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => s.Claims.TryClaimAsync(job, s.Lease, ct), ct)));

        Assert.Equal(1, outcomes.Count(o => o!.Claimed));
        Assert.Equal(9, outcomes.Count(o => !o!.Claimed));
        Assert.All(outcomes.Where(o => !o!.Claimed), o => Assert.Equal(ClaimRejection.NotReady, o!.Rejection));
        Assert.Equal(9, s.Metrics.Total(MetricNames.ClaimConflicts));

        var attempts = await s.AttemptsAsync(job, ct);
        var attempt = Assert.Single(attempts);
        Assert.Equal(AttemptOutcome.Sending, attempt.Outcome);
        Assert.True(attempt.MayHaveSent);
        Assert.Equal(outcomes.Single(o => o!.Claimed)!.AttemptId, attempt.AttemptId);
        Assert.Equal(JobStatus.Sending, (await s.JobAsync(job, ct)).Status);
        Assert.Equal(1, (await s.JobAsync(job, ct)).AutoAttemptCount);

        // El claim no publica: ningún evento adicional.
        Assert.Equal(outboxBefore, (await s.OutboxAsync(ct)).Count);

        // PrinterSendOrder avanzó con los valores de la fila reclamada.
        var order = await s.SendOrder.GetAsync(s.PrinterId, ct);
        Assert.Equal(job, order!.LastPrintJobId);
        Assert.Equal((ulong)(1000 + round), order.LastCycleId);
    }

    /// <summary>
    /// Contención real: otra transacción retiene un UPDLOCK sobre la fila del job mientras se lanzan los
    /// 10 claims. Ninguno termina mientras el bloqueo sigue retenido (no hay lecturas sucias ni atajos por
    /// versión de fila); al liberarlo, exactamente uno reclama y los otros nueve terminan sin excepción.
    /// </summary>
    [Fact]
    public async Task Claims_blocked_by_a_held_update_lock_still_have_exactly_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await StationScenario.CreateAsync(_db, ct: ct);
        var job = await s.ReadyJobAsync(2000, ct);

        await using var holder = new SqlConnection(s.Db.AppConnectionString);
        await holder.OpenAsync(ct);
        await using var tx = (SqlTransaction)await holder.BeginTransactionAsync(ct);
        await using (var hold = new SqlCommand("SELECT Status FROM dbo.PrintJob WITH (UPDLOCK, ROWLOCK) WHERE PrintJobId = @id", holder, tx))
        {
            hold.Parameters.AddWithValue("@id", job);
            Assert.Equal(JobStatus.Ready, (string?)await hold.ExecuteScalarAsync(ct));
        }

        var claims = Enumerable.Range(0, 10).Select(_ => Task.Run(() => s.Claims.TryClaimAsync(job, s.Lease, ct), ct)).ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
        Assert.All(claims, c => Assert.False(c.IsCompleted, "Un claim terminó mientras otra transacción retenía la fila del job."));

        await tx.CommitAsync(ct);
        var outcomes = await Task.WhenAll(claims);

        Assert.Equal(1, outcomes.Count(o => o!.Claimed));
        Assert.All(outcomes.Where(o => !o!.Claimed), o => Assert.Equal(ClaimRejection.NotReady, o!.Rejection));
        Assert.Equal(9, s.Metrics.Total(MetricNames.ClaimConflicts));
        Assert.Equal(AttemptOutcome.Sending, Assert.Single(await s.AttemptsAsync(job, ct)).Outcome);
    }
}
