using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Sql;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.IntegrationTests.Schema;

/// <summary>
/// T47 (parte de esquema; el resto del criterio está en las pruebas de la Fase 1) · El DDL y la
/// semilla de spec/database se crean en SQL Server 2022 real
/// sin error, incluidos índices filtrados y triggers; la migración es reproducible; el login de la
/// aplicación no tiene DDL ni escritura sobre AllowedTransition.
/// </summary>
[Trait("Spec", "T47")]
public sealed class MigrationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("migr", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Seed_rows_are_exactly_the_transitions_of_the_state_machine()
    {
        var expected = StateMachineSpec.Instance.AllTransitions
            .SelectMany(t => t.From.Select(f => $"{t.Machine}|{f}|{t.To}|{t.Mode.ToUpperInvariant()}|{t.Id}"))
            .Order(StringComparer.Ordinal).ToArray();

        var actual = new List<string>();
        await using var conn = new SqlConnection(_db.AppConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new SqlCommand("SELECT Machine, FromStatus, ToStatus, Kind, TransitionId FROM dbo.AllowedTransition", conn);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            actual.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}|{reader.GetString(4)}");
        }

        Assert.Equal(expected, actual.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Required_objects_exist_including_filtered_indexes_and_triggers()
    {
        string[] objects =
        [
            "UQ_CartonDetection_Identity", "UQ_CartonDetection_IdempotencyKey", "UQ_PrintJob_DetectionLabel", "UQ_PrintJob_LabelSerial",
            "TR_PrintJob_StateMachine", "TR_PrintAttempt_Guard", "TR_PrintAttempt_StateMachine", "TR_CartonDetection_StateMachine",
            "TR_SequenceGap_StateMachine", "TR_ReprintAuthorization_StateMachine", "TR_ReprintAuthorization_Guard",
            "TR_StationConfig_Guard", "TR_PlcEpochHistory_Guard", "TR_PlcEpochHistory_Immutable", "TR_PrinterSendOrder_Monotonic",
            "CK_CartonDetection_GatewayLive", "CK_CartonDetection_PlcTimestamp", "CK_OperatorAction_Target", "LabelSerialSeq",
        ];
        string[] filteredIndexes =
        [
            "UX_PrintAttempt_PrinterInFlight", "UX_PrintAttempt_JobOpen", "UX_PrintAttempt_OrdinarySent", "UX_PrintAttempt_Authorization",
            "UX_PrintAttempt_ConfirmingObservation", "UX_ReprintAuthorization_OpenPerJob", "IX_PrintJob_Claimable", "IX_PrintJob_Sweep",
            "IX_PrintJob_Carton", "IX_CartonDetection_Sweep", "IX_CartonDetection_Carton", "IX_OutboxEvent_Pending",
            "IX_EvidenceObservation_Cycle", "IX_PrintAttempt_EvidenceWindow",
        ];

        await using var conn = new SqlConnection(_db.MigratorConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var name in objects)
        {
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.objects WHERE name = @n", conn);
            cmd.Parameters.AddWithValue("@n", name);
            Assert.True((int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))! == 1, name);
        }

        foreach (var name in filteredIndexes)
        {
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.indexes WHERE name = @n AND has_filter = 1", conn);
            cmd.Parameters.AddWithValue("@n", name);
            Assert.True((int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))! == 1, name);
        }

        await using var triggers = new SqlCommand("SELECT COUNT(*) FROM sys.triggers WHERE is_disabled = 1", conn);
        Assert.Equal(0, (int)(await triggers.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task Second_migration_is_a_no_op()
    {
        var report = await new SqlMigrationRunner(RepoPaths.Spec)
            .MigrateAsync(_db.MigratorConnectionString, TestDatabase.AppLogin, TestContext.Current.CancellationToken);

        Assert.Empty(report.Applied);
        Assert.Equal([SqlMigrationRunner.SchemaScript, SqlMigrationRunner.SeedScript], report.Skipped);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.AllowedTransition (Machine, FromStatus, ToStatus, Kind, TransitionId) VALUES ('PrintJob', 'UNKNOWN', 'READY', 'AUTO', 'X99')", 229)]
    [InlineData("UPDATE dbo.AllowedTransition SET Kind = 'AUTO' WHERE TransitionId = 'J04'", 229)]
    [InlineData("DELETE FROM dbo.AllowedTransition WHERE TransitionId = 'J02'", 229)]
    [InlineData("ALTER TABLE dbo.PrintJob DISABLE TRIGGER TR_PrintJob_StateMachine", 1088)]
    [InlineData("DISABLE TRIGGER dbo.TR_PrintJob_Immutable ON dbo.PrintJob", 1088)]
    [InlineData("ALTER TABLE dbo.PrintJob NOCHECK CONSTRAINT CK_PrintJob_Reason", 1088)]
    [InlineData("CREATE TABLE dbo.Intruso (Id INT)", 262)]
    [InlineData("DELETE FROM migrations.AppliedScript", 229)]
    public async Task Application_login_cannot_change_the_rules(string sqlText, int expectedError)
    {
        var ct = TestContext.Current.CancellationToken;

        // Control positivo: la misma sentencia funciona con el login de migraciones (y se revierte),
        // así el error del login de la aplicación es un permiso denegado y no un objeto inexistente.
        await using (var migrator = new SqlConnection(_db.MigratorConnectionString))
        {
            await migrator.OpenAsync(ct);
            await using var tx = (SqlTransaction)await migrator.BeginTransactionAsync(ct);
            await using var ok = new SqlCommand(sqlText, migrator, tx);
            await ok.ExecuteNonQueryAsync(ct);
            await tx.RollbackAsync(ct);
        }

        await using var conn = new SqlConnection(_db.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sqlText, conn);

        var ex = await Assert.ThrowsAsync<SqlException>(() => cmd.ExecuteNonQueryAsync(ct));

        Assert.Equal(expectedError, ex.Number);
    }

    [Fact]
    public async Task Application_login_can_take_serials_from_the_sequence()
    {
        await using var conn = new SqlConnection(_db.AppConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new SqlCommand("SELECT NEXT VALUE FOR dbo.LabelSerialSeq", conn);

        Assert.True((long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))! >= 1);
    }

    [Fact]
    public void Batches_split_only_on_lines_that_contain_go()
    {
        var script = SqlScript.Parse("x", "SELECT 1\nGO\n  go  \nSELECT 'GO'\n-- GO comentado\nGOTO_LABEL:\nGO\n");

        Assert.Equal(["SELECT 1\n", "SELECT 'GO'\n-- GO comentado\nGOTO_LABEL:\n"], script.Batches);
    }
}
