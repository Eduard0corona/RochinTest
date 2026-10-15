using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// SI-21: los barridos que recorren los jobs pendientes de la estación (OUT_OF_ORDER y CLOCK_JUMP) bloquean cada job por PK_PrintJob
/// antes que por IX_PrintJob_Sweep, en el mismo orden que el claim de TX-B (J02). Las sentencias de referencia recorrían
/// IX_PrintJob_Sweep con U y buscaban la fila después; contra el claim de esa misma fila se interbloqueaban, y el sender quedaba
/// detenido hasta que SQL Server detectaba el ciclo (hasta 5 s): en T12, una o dos cajas vencidas cada media hora.
/// La prueba fija el orden: una sesión toma el job como el claim (U en la clave primaria), el barrido real queda esperando esa fila
/// y después la sesión pide el job en IX_PrintJob_Sweep, como hace el claim al sacarlo de READY. Con el orden de las sentencias de
/// referencia es un interbloqueo seguro.
/// </summary>
[Collection(nameof(BrokerOutageCollection))]
public sealed class SweepLockOrderTests(SqlServerFixture sql) : IAsyncLifetime
{
    // La tabla con historia antes de compilar el barrido, como en T12: pocos jobs READY entre muchos transmitidos, y el plan busca los
    // pendientes por el índice filtrado.
    private const int History = 300;

    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("sweeplock", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [Trait("Spec", "T42")]
    [InlineData("OUT_OF_ORDER")]
    [InlineData("CLOCK_JUMP")]
    public async Task A_sweep_over_the_pending_jobs_never_deadlocks_with_a_claim_of_one_of_them(string sweep)
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        ulong cycle = 0;
        while (cycle < History)
        {
            var claimed = await s.Claims.TryClaimAsync(await s.ReadyJobAsync(++cycle, Ct), s.Lease, Ct);
            await s.TransmitAsync(claimed!.AttemptId, Ct);
        }

        var job = await s.ReadyJobAsync(++cycle, Ct);

        // El claim: primero la fila en la clave primaria (con prioridad alta, para que la víctima de un interbloqueo sea el barrido y
        // se cuente en DeadlockRetries).
        await using var claim = new SqlConnection(s.Db.AdminConnectionString);
        await claim.OpenAsync(Ct);
        await using var tx = (SqlTransaction)await claim.BeginTransactionAsync(Ct);
        var spid = await ExecuteAsync<short>(claim, tx, """
            SET DEADLOCK_PRIORITY HIGH;
            SELECT 1 FROM dbo.PrintJob WITH (UPDLOCK, ROWLOCK, INDEX (PK_PrintJob)) WHERE PrintJobId = @job;
            SELECT CAST(@@SPID AS SMALLINT);
            """, job, s.StationId);

        var swept = Task.Run(async () => sweep == "OUT_OF_ORDER"
            ? (await s.Txs.ExpireOutOfOrderAsync(StationScenario.SiteId, s.StationId, s.Clock.UtcNow, Ct)).Count
            : (await s.Txs.ClockJumpAsync(StationScenario.SiteId, s.StationId, s.Clock.UtcNow, Ct)).Expired.Count, Ct);
        await WaitBlockedByAsync(s, spid, swept);

        // Después, el job en IX_PrintJob_Sweep, como el claim al sacarlo de READY: el barrido no puede tenerlo.
        try
        {
            await ExecuteAsync<int>(claim, tx, """
                SELECT 1 FROM dbo.PrintJob WITH (UPDLOCK, ROWLOCK, INDEX (IX_PrintJob_Sweep))
                WHERE SiteId = @site AND StationId = @station AND Status IN ('READY', 'RETRY_WAIT') AND PrintJobId = @job;
                """, job, s.StationId);
        }
        catch (SqlException ex) when (SqlDb.IsDeadlock(ex))
        {
            Assert.Fail($"{sweep}: el claim fue la víctima de un interbloqueo con el barrido");
        }

        await tx.CommitAsync(Ct);

        var expired = await swept;
        Assert.True(s.Sql.DeadlockRetries == 0, $"{sweep}: {s.Sql.DeadlockRetries} transacciones reintentadas por un 1205 con el claim");
        Assert.Equal(sweep == "OUT_OF_ORDER" ? 0 : 1, expired);   // en orden no vence nada; el salto del reloj vence lo pendiente
    }

    private static async Task<T> ExecuteAsync<T>(SqlConnection conn, SqlTransaction tx, string sql, Guid job, string station)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@job", System.Data.SqlDbType.UniqueIdentifier).Value = job;
        cmd.Parameters.Add("@site", System.Data.SqlDbType.NVarChar, 64).Value = StationScenario.SiteId;
        cmd.Parameters.Add("@station", System.Data.SqlDbType.NVarChar, 64).Value = station;
        T last = default!;
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        do
        {
            while (await r.ReadAsync(Ct))
            {
                last = (T)Convert.ChangeType(r.GetValue(0), typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        while (await r.NextResultAsync(Ct));
        return last;
    }

    /// <summary>Espera a que el barrido quede bloqueado por la sesión del claim (o falla si termina o no se bloquea en 10 s).</summary>
    private static async Task WaitBlockedByAsync(StationScenario s, short spid, Task sweep)
    {
        await using var admin = new SqlConnection(s.Db.AdminConnectionString);
        await admin.OpenAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            Assert.False(sweep.IsCompleted, "el barrido terminó sin esperar la fila tomada por el claim");
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @spid", admin);
            cmd.Parameters.Add("@spid", System.Data.SqlDbType.SmallInt).Value = spid;
            if ((int)(await cmd.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "el barrido no llegó a esperar la fila tomada por el claim");
            await Task.Delay(20, Ct);
        }
    }
}
