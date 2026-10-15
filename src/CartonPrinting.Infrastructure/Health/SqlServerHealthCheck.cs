using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CartonPrinting.Infrastructure.Health;

/// <summary>SQL Server es el árbitro: sin él la estación falla cerrada.</summary>
public sealed class SqlServerHealthCheck(Func<string> connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString());
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.AllowedTransition", conn) { CommandTimeout = 5 };
            var rows = (int)(await cmd.ExecuteScalarAsync(cancellationToken))!;
            return rows > 0
                ? HealthCheckResult.Healthy($"AllowedTransition con {rows} filas")
                : HealthCheckResult.Unhealthy("AllowedTransition vacía: falta la semilla de transiciones");
        }
        catch (SqlException ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server no disponible", ex);
        }
    }
}
