using CartonPrinting.Infrastructure.Sql;

namespace CartonPrinting.EdgeHost;

/// <summary>
/// Comandos de aprovisionamiento y migración. Las cadenas de conexión y contraseñas llegan por
/// variables de entorno o secretos de usuario; nunca desde el repositorio.
/// </summary>
internal static class MaintenanceCommands
{
    public static async Task<int> RunAsync(string command, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var config = builder.Configuration;
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILogger<SqlMigrationRunner>>();
        var specRoot = config["Spec:Root"] ?? SpecLocator.Find(AppContext.BaseDirectory);
        var runner = new SqlMigrationRunner(specRoot, logger);
        var database = config["Database:Name"] ?? "CartonPrinting";
        var migratorLogin = config["Database:MigratorLogin"] ?? "cp_migrator";
        var appLogin = config["Database:AppLogin"] ?? "cp_app";

        switch (command)
        {
            case "provision":
                await runner.ProvisionAsync(
                    Required(config, "ConnectionStrings:SqlAdmin"),
                    Path.Combine(Path.GetDirectoryName(specRoot)!, "build", "sql", "provision.sql"),
                    new ProvisioningRequest(database, migratorLogin, Required(config, "Database:MigratorPassword"), appLogin,
                        Required(config, "Database:AppPassword")),
                    CancellationToken.None);
                return 0;
            case "migrate":
                var report = await runner.MigrateAsync(Required(config, "ConnectionStrings:CartonPrintingMigrator"), appLogin, CancellationToken.None);
                logger.LogInformation("Aplicados: {Applied}. Sin cambios: {Skipped}.", report.Applied, report.Skipped);
                return 0;
            default:
                return 2;
        }
    }

    private static string Required(IConfiguration config, string key) =>
        config[key] ?? throw new InvalidOperationException($"Falta la configuración {key} (variable de entorno o secreto de usuario).");
}

internal static class SpecLocator
{
    public static string Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "spec");
            if (File.Exists(Path.Combine(candidate, "contracts", "state-machine.json")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No se encontró spec/: configure Spec:Root.");
    }
}
