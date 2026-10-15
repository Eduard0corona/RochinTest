using CartonPrinting.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace CartonPrinting.Testing;

/// <summary>Imágenes fijas, las mismas de docker-compose.yml.</summary>
public static class TestImages
{
    public const string SqlServer = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";
    public const string RabbitMq = "rabbitmq:4.3.5-management";
}

/// <summary>Cadenas de conexión de una base aprovisionada y migrada desde spec/database.</summary>
public sealed record TestDatabase(string Name, string AdminConnectionString, string MigratorConnectionString, string AppConnectionString)
{
    public const string AppLogin = "cp_app";
    public const string MigratorLogin = "cp_migrator";
}

/// <summary>
/// SQL Server 2022 real en un contenedor (Testcontainers), uno por ensamblado de pruebas. Cada
/// clase de pruebas pide su propia base con <see cref="CreateDatabaseAsync"/>: se aprovisiona con
/// build/sql/provision.sql y se migra con el DDL y la semilla de spec/database.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder(TestImages.SqlServer).Build();
    private readonly SemaphoreSlim _provisionLock = new(1, 1);
    private int _counter;

    public string MigratorPassword { get; } = SqlMigrationRunner.GeneratePassword();

    public string AppPassword { get; } = SqlMigrationRunner.GeneratePassword();

    public string AdminConnectionString => _container.GetConnectionString();

    /// <summary>El contenedor, para detenerlo y volver a arrancarlo (T19 con un servidor real). Al volver cambia su puerto publicado.</summary>
    public MsSqlContainer Container => _container;

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        _provisionLock.Dispose();
    }

    public async Task<TestDatabase> CreateDatabaseAsync(string prefix, CancellationToken ct = default)
    {
        var name = $"{prefix}_{Interlocked.Increment(ref _counter)}";
        var runner = new SqlMigrationRunner(RepoPaths.Spec);

        // El aprovisionamiento toca logins del servidor: uno a la vez.
        await _provisionLock.WaitAsync(ct);
        try
        {
            await runner.ProvisionAsync(
                AdminConnectionString,
                Path.Combine(RepoPaths.Root, "build", "sql", "provision.sql"),
                new ProvisioningRequest(name, TestDatabase.MigratorLogin, MigratorPassword, TestDatabase.AppLogin, AppPassword),
                ct);
        }
        finally
        {
            _provisionLock.Release();
        }

        var db = new TestDatabase(
            name,
            WithDatabase(AdminConnectionString, name, null, null),
            WithDatabase(AdminConnectionString, name, TestDatabase.MigratorLogin, MigratorPassword),
            WithDatabase(AdminConnectionString, name, TestDatabase.AppLogin, AppPassword));
        await runner.MigrateAsync(db.MigratorConnectionString, TestDatabase.AppLogin, ct);
        return db;
    }

    private static string WithDatabase(string admin, string database, string? user, string? password)
    {
        var b = new SqlConnectionStringBuilder(admin) { InitialCatalog = database, Pooling = true, MaxPoolSize = 200 };
        if (user is not null)
        {
            b.UserID = user;
            b.Password = password;
        }

        return b.ConnectionString;
    }
}
