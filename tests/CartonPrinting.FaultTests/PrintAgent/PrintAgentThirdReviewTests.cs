using System.Diagnostics;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.FaultTests.PrintAgent;

/// <summary>
/// Regresiones de la tercera revisión independiente de la Fase 4, contra SQL Server real: la guarda de A-1 con una lectura que
/// nombra otro intento del job (MEDIA-1) y con una lectura anterior al claim (BAJA-3); el lock exclusivo del registro (MEDIA-2);
/// el orden de locks de TX-D frente a TX-C (BAJA-1); y el código de salida del EdgeHost con SQL Server inalcanzable (MEDIA-4).
/// </summary>
public sealed class PrintAgentThirdReviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string WrongSerial = "ZZ-AJENO-0002";
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f4rev3", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// MEDIA-1: el primer intento terminó NOT_SENT; durante el SENDING del segundo llega una lectura VERIFIED ajena que nombra al
    /// primero. Aplicar después la PRINTED del segundo aplica antes esa lectura (J11), igual que en orden: nunca CONFIRMED.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task A_contradiction_naming_an_earlier_attempt_of_the_job_is_applied_before_confirming()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, first) = await s.SendingJobAsync(2600, Ct);
        await s.Txc.NotSentAsync(first, s.Clock.UtcNow, "Conexión rechazada", NotSentCause.ConnectionFailed, 100, 3, s.Clock.UtcNow, Ct);
        s.AdvanceMs(200);
        await new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics).SweepOnceAsync(Ct);
        var second = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
        Assert.True(second.Claimed, second.Rejection.ToString());

        await s.ObserveAsync(s.Verified(2600, WrongSerial, first), Ct);
        await s.TransmitAsync(second.AttemptId, Ct);
        var printed = await s.ObserveAsync(s.Printed(job), Ct);

        Assert.Equal(EvidenceAction.SerialMismatch, (await s.Txd.ApplyAsync(printed, s.Clock.UtcNow, Ct)).Action);
        var row = await s.JobAsync(job, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), (row.Status, row.Reason));
    }

    /// <summary>BAJA-3: una lectura ajena recibida antes del claim no es del intento: no impide la confirmación de la PRINTED.</summary>
    [Fact]
    [Trait("Spec", "T15")]
    public async Task A_foreign_read_received_before_the_claim_does_not_block_the_confirmation()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var job = await s.ReadyJobAsync(2500, Ct);
        await s.ObserveAsync(s.Verified(2500, WrongSerial), Ct);
        s.AdvanceMs(5);
        var claim = (await s.Claims.TryClaimAsync(job, s.Lease, Ct))!;
        await s.TransmitAsync(claim.AttemptId, Ct);
        var printed = await s.ObserveAsync(s.Printed(job), Ct);

        Assert.Equal(EvidenceAction.Confirmed, (await s.Txd.ApplyAsync(printed, s.Clock.UtcNow, Ct)).Action);
        Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(job, Ct)).Status);
    }

    /// <summary>
    /// MEDIA-2: el registro de una observación de ciclo (TxdEvidence.RecordAsync) toma el lock exclusivo del ciclo: mientras una
    /// aplicación tiene el compartido, el registro espera, y termina cuando se libera.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task Recording_a_cycle_observation_waits_for_an_application_in_progress()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, _) = await s.TransmittedJobAsync(2700, Ct);
        await using var conn = new SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(Ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(Ct);
        await using (var hold = new SqlCommand("EXEC sys.sp_getapplock @Resource = @r, @LockMode = 'Shared', @LockOwner = 'Transaction'", conn, tx))
        {
            hold.Parameters.AddWithValue("@r", TxdEvidence.CycleLockResource(StationScenario.SiteId, s.StationId, s.Epoch, 2700));
            await hold.ExecuteNonQueryAsync(Ct);
        }

        var serial = (await s.JobAsync(job, Ct)).LabelSerial;
        var record = Task.Run(() => s.Txd.RecordAsync(s.Verified(2700, serial), s.Clock.UtcNow, Ct), Ct);
        await Task.Delay(500, Ct);
        Assert.False(record.IsCompleted, "El registro de la observación de ciclo no esperó el lock del ciclo.");
        await tx.CommitAsync(Ct);
        Assert.True(await record.WaitAsync(TimeSpan.FromSeconds(30), Ct) > 0);
    }

    /// <summary>
    /// BAJA-1: un registro VERIFIED que nombra al intento (lock exclusivo del ciclo, espera la FK del intento), una transacción
    /// como TX-C (el intento y después el job) y la aplicación de la PRINTED. Con el lock del ciclo antes del UPDLOCK del job
    /// no hay interbloqueo: la aplicación espera el lock del ciclo sin retener el job, la transacción actualiza el job sin esperar
    /// y confirma, y la PRINTED se aplica.
    /// </summary>
    [Fact]
    [Trait("Spec", "T15")]
    public async Task Recording_an_application_and_a_tx_c_like_transaction_do_not_deadlock()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        var (job, attempt) = await s.TransmittedJobAsync(2800, Ct);
        var printed = await s.ObserveAsync(s.Printed(job), Ct);
        var serial = (await s.JobAsync(job, Ct)).LabelSerial;

        await using var conn = new SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(Ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(Ct);
        await ExecAsync(conn, tx, "SET DEADLOCK_PRIORITY LOW; UPDATE dbo.PrintAttempt SET FailureDetail = N'Prueba de orden de locks' WHERE AttemptId = @id",
            attempt);
        var record = Task.Run(() => s.Txd.RecordAsync(s.Verified(2800, serial, attempt), s.Clock.UtcNow, Ct), Ct);
        await Task.Delay(500, Ct);
        Assert.False(record.IsCompleted, "El registro no quedó esperando la FK del intento: la prueba no reproduce el escenario.");
        var apply = Task.Run(() => s.Txd.ApplyAsync(printed, s.Clock.UtcNow, Ct), Ct);
        await WaitForCycleLockWaiterAsync(s);
        // Mientras la aplicación espera el lock del ciclo no retiene el job: la transacción lo actualiza sin esperar (1222 si esperara).
        await ExecAsync(conn, tx, "SET LOCK_TIMEOUT 1000; UPDATE dbo.PrintJob SET UpdatedAt = UpdatedAt WHERE PrintJobId = @id", job);
        await tx.CommitAsync(Ct);

        Assert.True(await record.WaitAsync(TimeSpan.FromSeconds(30), Ct) > 0);
        Assert.Equal(EvidenceAction.Confirmed, (await apply.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Action);
        Assert.Equal(JobStatus.Confirmed, (await s.JobAsync(job, Ct)).Status);
    }

    /// <summary>
    /// MEDIA-4: el EdgeHost real, con la impresora configurada y SQL Server inalcanzable, se detiene con el código 70, no con 0:
    /// un supervisor que reinicia ante falla lo relanza.
    /// </summary>
    [Fact]
    public async Task The_edge_host_exits_with_a_failure_code_when_sql_server_is_unreachable()
    {
        var dll = AppContext.BaseDirectory.Replace(
            Path.Combine("tests", "CartonPrinting.FaultTests"), Path.Combine("src", "EdgeHost"), StringComparison.Ordinal) + "CartonPrinting.EdgeHost.dll";
        Assert.True(File.Exists(dll), $"No existe {dll}: compilar la solución (dotnet build en la raíz compila el EdgeHost).");
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        psi.Environment["ConnectionStrings__CartonPrinting"] = "Server=127.0.0.1,1;Connect Timeout=1;Encrypt=false";
        psi.Environment["Printer__PrinterId"] = "PRN-EXIT";
        psi.Environment["Printer__Transport__Port"] = "9";
        psi.Environment["Urls"] = "http://127.0.0.1:0";
        using var host = Process.Start(psi)!;
        var output = host.StandardOutput.ReadToEndAsync(Ct);
        var errors = host.StandardError.ReadToEndAsync(Ct);
        try
        {
            await host.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(90), Ct);
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }
        }

        Assert.True(host.ExitCode == 70, $"código de salida {host.ExitCode}; salida: {await output} {await errors}");
    }

    /// <summary>Espera a que SQL Server muestre una petición compartida del lock de un ciclo en espera (la aplicación de TX-D).</summary>
    private static async Task WaitForCycleLockWaiterAsync(StationScenario s)
    {
        await using var admin = new SqlConnection(s.Db.AdminConnectionString);
        await admin.OpenAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var cmd = new SqlCommand("""
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION' AND request_mode = 'S' AND request_status = 'WAIT' AND resource_database_id = DB_ID(@db)
                """, admin);
            cmd.Parameters.AddWithValue("@db", s.Db.Name);
            if ((int)(await cmd.ExecuteScalarAsync(Ct))! > 0)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "La aplicación no llegó a esperar el lock del ciclo.");
            await Task.Delay(50, Ct);
        }
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction tx, string sql, Guid id)
    {
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(Ct);
    }
}
