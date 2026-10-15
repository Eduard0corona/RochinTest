using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>Cadena de conexión del login de la aplicación (sin DDL).</summary>
public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Reintentos de una transacción completa ante 1205 (víctima de interbloqueo).</summary>
    public int MaxDeadlockRetries { get; set; } = 5;
}

/// <summary>
/// Ejecuta transacciones con <c>SET XACT_ABORT ON</c> y READ COMMITTED (RCSI en la base).
/// Un 1205 reintenta la transacción completa. Toda transacción que fija <c>SESSION_CONTEXT</c>
/// lo limpia también al fallar: no se confía en el reseteo del pool de conexiones.
/// </summary>
public sealed partial class SqlDb(DatabaseOptions options, ILogger<SqlDb>? logger = null)
{
    public const int DeadlockVictim = 1205;

    /// <summary>Víctima de interbloqueo en un sp_getapplock (resultado -3): se reintenta como un 1205.</summary>
    public const int AppLockDeadlockVictim = 50205;

    private readonly ILogger _logger = logger ?? NullLogger<SqlDb>.Instance;
    private long _deadlockRetries;

    public DatabaseOptions Options { get; } = options;

    /// <summary>Transacciones reintentadas por un 1205 (o un -3 de sp_getapplock) desde que se creó esta instancia.</summary>
    public long DeadlockRetries => Interlocked.Read(ref _deadlockRetries);

    // Sin el periodo de bloqueo del pool: tras una apertura fallida, el pool devolvería el mismo error durante 5 a 60 s y la
    // vigilancia de la falla cerrada no vería volver a SQL Server hasta entonces (revisión de la Fase 7).
    private string ConnectionString => _connectionString ??= new SqlConnectionStringBuilder(Options.ConnectionString)
    {
        PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock,
    }.ConnectionString;

    private string? _connectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Ejecuta <paramref name="work"/> en una transacción nueva. El delegado no debe producir efectos
    /// fuera de SQL: si hay 1205 se vuelve a ejecutar completo.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(Func<SqlConnection, SqlTransaction, Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var conn = await OpenAsync(ct);
            var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                await using (var xact = conn.Command(tx, "SET XACT_ABORT ON;"))
                {
                    await xact.ExecuteNonQueryAsync(ct);
                }

                var result = await work(conn, tx);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (SqlException ex) when (IsDeadlock(ex) && attempt <= Options.MaxDeadlockRetries)
            {
                Interlocked.Increment(ref _deadlockRetries);
                _logger.LogWarning("1205 en la transacción; reintento {Attempt}", attempt);
                await SafeRollbackAsync(tx);
                await ClearSessionContextAsync(conn);
            }
            catch
            {
                await SafeRollbackAsync(tx);
                await ClearSessionContextAsync(conn);
                throw;
            }
            finally
            {
                await tx.DisposeAsync();
            }
        }
    }

    public Task InTransactionAsync(Func<SqlConnection, SqlTransaction, Task> work, CancellationToken ct) =>
        InTransactionAsync<bool>(async (c, t) =>
        {
            await work(c, t);
            return true;
        }, ct);

    /// <summary>Lectura fuera de transacción explícita.</summary>
    public async Task<T> ReadAsync<T>(Func<SqlConnection, Task<T>> read, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        return await read(conn);
    }

    /// <summary>
    /// Marca de sesión de las transiciones excepcionales y de corrección (TR_PrintJob_StateMachine).
    /// Declara la intención; la guarda real son los datos.
    /// </summary>
    public static async Task SetTransitionKindAsync(SqlConnection conn, SqlTransaction tx, string? kind, CancellationToken ct)
    {
        await using var cmd = conn.Command(tx, "EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = @v;");
        cmd.VarChar("@v", kind, 12);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static bool IsDeadlock(SqlException ex) => ex.Errors.Cast<SqlError>().Any(e => e.Number is DeadlockVictim or AppLockDeadlockVictim);

    /// <summary>
    /// ¿Es "clave existente" de la detección? Solo una violación (2627 o 2601) de
    /// UQ_CartonDetection_Identity, UQ_CartonDetection_IdempotencyKey o PK_CartonDetection. Se
    /// reconoce por el nombre de la restricción en el mensaje, que aparece en cualquier idioma.
    /// </summary>
    public static bool IsDetectionIdentityViolation(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number is 2627 or 2601 && DetectionIdentityConstraint().IsMatch(e.Message));

    public static bool IsUniqueViolation(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number is 2627 or 2601);

    private static async Task SafeRollbackAsync(SqlTransaction tx)
    {
        try
        {
            if (tx.Connection is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
            // La transacción ya terminó (XACT_ABORT la revirtió en el servidor).
        }
        catch (SqlException)
        {
            // Conexión rota: el pool la descarta.
        }
    }

    private static async Task ClearSessionContextAsync(SqlConnection conn)
    {
        if (conn.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var cmd = conn.Command(null, "EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (SqlException)
        {
            // Conexión inutilizable: el pool la descarta y la marca no sobrevive.
            SqlConnection.ClearPool(conn);
        }
    }

    [GeneratedRegex(@"\b(PK_CartonDetection|UQ_CartonDetection_Identity|UQ_CartonDetection_IdempotencyKey)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DetectionIdentityConstraint();
}
