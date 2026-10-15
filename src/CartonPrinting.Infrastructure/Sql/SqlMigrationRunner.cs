using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.Infrastructure.Sql;

/// <summary>Resultado de una ejecución de migraciones.</summary>
public sealed record MigrationReport(IReadOnlyList<string> Applied, IReadOnlyList<string> Skipped, int SchemaBatches, int SeedBatches);

/// <summary>Credenciales para aprovisionar los dos logins (se toman de secretos, nunca del repositorio).</summary>
public sealed record ProvisioningRequest(
    string DatabaseName,
    string MigratorLogin,
    string MigratorPassword,
    string AppLogin,
    string AppPassword);

/// <summary>
/// Ejecutor de migraciones. Lee el DDL de la especificación desde spec/database (no lo copia):
/// primero <c>sqlserver_schema.sql</c> y después <c>sqlserver_state_transitions.sql</c>, dividiendo
/// los lotes en las líneas que solo contienen <c>GO</c>. Se conecta con el login de migraciones; la
/// aplicación usa otro login sin DDL ni escritura sobre <c>dbo.AllowedTransition</c>.
/// </summary>
/// <remarks>
/// Cada script se aplica dentro de una transacción y queda registrado con su SHA-256 en
/// <c>migrations.AppliedScript</c> (esquema propio de la infraestructura, fuera del modelo de la
/// especificación). El esquema se aplica una sola vez: si spec/ cambió después de aplicarlo, la
/// ejecución falla en lugar de aplicar un DDL distinto encima. La semilla de transiciones es
/// idempotente (DELETE e INSERT) y se vuelve a aplicar si su hash cambió.
/// </remarks>
public sealed class SqlMigrationRunner(string specRoot, ILogger<SqlMigrationRunner>? logger = null)
{
    public const string SchemaScript = "sqlserver_schema.sql";
    public const string SeedScript = "sqlserver_state_transitions.sql";

    private readonly ILogger _logger = logger ?? NullLogger<SqlMigrationRunner>.Instance;

    public string DatabaseDirectory => Path.Combine(specRoot, "database");

    /// <summary>
    /// Crea la base y los dos logins con build/sql/provision.sql. Requiere un login sysadmin
    /// (solo en desarrollo, pruebas o la instalación inicial).
    /// </summary>
    public async Task ProvisionAsync(string adminConnectionString, string provisionScriptPath, ProvisioningRequest request, CancellationToken ct)
    {
        var script = SqlScript.Load(provisionScriptPath, new Dictionary<string, string>
        {
            ["DatabaseName"] = Identifier(request.DatabaseName),
            ["MigratorLogin"] = Identifier(request.MigratorLogin),
            ["MigratorPassword"] = Literal(request.MigratorPassword),
            ["AppLogin"] = Identifier(request.AppLogin),
            ["AppPassword"] = Literal(request.AppPassword),
        });

        await using var conn = new SqlConnection(adminConnectionString);
        await conn.OpenAsync(ct);
        foreach (var batch in script.Batches)
        {
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Base {Database} y logins aprovisionados", request.DatabaseName);
    }

    /// <summary>Aplica el esquema y la semilla, y los permisos del login de la aplicación.</summary>
    public async Task<MigrationReport> MigrateAsync(string migratorConnectionString, string appLogin, CancellationToken ct)
    {
        var schema = SqlScript.Load(Path.Combine(DatabaseDirectory, SchemaScript));
        var seed = SqlScript.Load(Path.Combine(DatabaseDirectory, SeedScript));
        var applied = new List<string>();
        var skipped = new List<string>();

        await using var conn = new SqlConnection(migratorConnectionString);
        await conn.OpenAsync(ct);
        await EnsureJournalAsync(conn, ct);

        var schemaHash = await AppliedHashAsync(conn, SchemaScript, ct);
        if (schemaHash is null)
        {
            await ApplyAsync(conn, schema, ct);
            applied.Add(SchemaScript);
        }
        else if (schemaHash != schema.Sha256)
        {
            throw new InvalidOperationException(
                $"{SchemaScript} cambió desde que se aplicó ({schemaHash} → {schema.Sha256}): se necesita una migración explícita.");
        }
        else
        {
            skipped.Add(SchemaScript);
        }

        if (await AppliedHashAsync(conn, SeedScript, ct) == seed.Sha256)
        {
            skipped.Add(SeedScript);
        }
        else
        {
            await ApplyAsync(conn, seed, ct);
            applied.Add(SeedScript);
        }

        await GrantApplicationAsync(conn, appLogin, ct);
        _logger.LogInformation("Migraciones: aplicadas {Applied}; sin cambios {Skipped}", applied, skipped);
        return new MigrationReport(applied, skipped, schema.Batches.Count, seed.Batches.Count);
    }

    private static async Task EnsureJournalAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            IF SCHEMA_ID(N'migrations') IS NULL EXEC (N'CREATE SCHEMA migrations');
            IF OBJECT_ID(N'migrations.AppliedScript', N'U') IS NULL
                CREATE TABLE migrations.AppliedScript (
                    ScriptName NVARCHAR(128) NOT NULL CONSTRAINT PK_AppliedScript PRIMARY KEY,
                    Sha256     CHAR(64)      NOT NULL,
                    AppliedAt  DATETIME2(3)  NOT NULL CONSTRAINT DF_AppliedScript_AppliedAt DEFAULT SYSUTCDATETIME());
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> AppliedHashAsync(SqlConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT Sha256 FROM migrations.AppliedScript WHERE ScriptName = @n", conn);
        cmd.Parameters.Add("@n", System.Data.SqlDbType.NVarChar, 128).Value = name;
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    private async Task ApplyAsync(SqlConnection conn, SqlScript script, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        var index = 0;
        try
        {
            foreach (var batch in script.Batches)
            {
                index++;
                await using var cmd = new SqlCommand(batch, conn, tx) { CommandTimeout = 300 };
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var record = new SqlCommand(
                """
                MERGE migrations.AppliedScript AS t
                USING (SELECT @n AS ScriptName, @h AS Sha256) AS s ON t.ScriptName = s.ScriptName
                WHEN MATCHED THEN UPDATE SET Sha256 = s.Sha256, AppliedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (ScriptName, Sha256) VALUES (s.ScriptName, s.Sha256);
                """, conn, tx))
            {
                record.Parameters.Add("@n", System.Data.SqlDbType.NVarChar, 128).Value = script.Name;
                record.Parameters.Add("@h", System.Data.SqlDbType.Char, 64).Value = script.Sha256;
                await record.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation("{Script} aplicado: {Batches} lotes", script.Name, script.Batches.Count);
        }
        catch (SqlException ex)
        {
            if (tx.Connection is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }

            throw new SqlMigrationException(script.Name, index, script.Batches[index - 1], ex);
        }
    }

    /// <summary>
    /// Permisos del login de la aplicación sobre objetos de la especificación: puede tomar seriales,
    /// no puede escribir en <c>dbo.AllowedTransition</c> (un rol con ese permiso podría saltarse los
    /// triggers, G17).
    /// </summary>
    private static async Task GrantApplicationAsync(SqlConnection conn, string appLogin, CancellationToken ct)
    {
        var user = Identifier(appLogin);
        var sql = $"""
            IF USER_ID(N'{user}') IS NULL THROW 50001, N'El usuario de la aplicación no existe: ejecute el aprovisionamiento.', 1;
            GRANT UPDATE ON OBJECT::dbo.LabelSerialSeq TO [{user}];
            DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.AllowedTransition TO [{user}];
            DENY ALTER ON SCHEMA::dbo TO [{user}];
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Identificador seguro para corchetes: solo letras, dígitos, guion bajo y guion.</summary>
    private static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new ArgumentException($"Identificador SQL no admitido: '{value}'.");
        }

        return value;
    }

    /// <summary>Literal dentro de N'...' anidado en EXEC (N'... N''x'' ...'): se duplican las comillas dos veces.</summary>
    private static string Literal(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return value.Replace("'", "''''", StringComparison.Ordinal);
    }

    /// <summary>Contraseña aleatoria que cumple la política de SQL Server (para pruebas y desarrollo).</summary>
    public static string GeneratePassword()
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return new StringBuilder("Cp1!").Append(Convert.ToBase64String(bytes).Replace('/', 'x').Replace('+', 'y').TrimEnd('=')).ToString();
    }
}

public sealed class SqlMigrationException(string script, int batch, string batchText, SqlException inner)
    : Exception($"{script}: falló el lote {batch} (Msg {inner.Number}, línea {inner.LineNumber}): {inner.Message}\n--- lote ---\n{batchText}", inner)
{
    public string Script { get; } = script;

    public int Batch { get; } = batch;

    public int SqlErrorNumber => ((SqlException)InnerException!).Number;
}
