using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>
/// Distingue "SQL Server no está disponible" (red, servidor caído, conexión cortada o tiempo de espera de conexión) de
/// cualquier otro error de SQL. Lo primero es la falla cerrada del perfil mínimo (ESTRATEGIA_RECUPERACION §3); lo segundo
/// es un defecto, y quien lo recibe se detiene.
/// </summary>
public static class SqlAvailability
{
    // -2 tiempo de espera, -1/2/53 servidor no encontrado, 64/233/10053/10054 conexión cortada, 10060/10061 sin respuesta o
    // rechazada, 11001 host desconocido, 40613 base no disponible.
    private static readonly HashSet<int> Unavailable = [-2, -1, 2, 53, 64, 233, 10053, 10054, 10060, 10061, 11001, 40613];

    public static bool IsUnavailable(Exception ex) => ex switch
    {
        SqlException sql => sql.Class >= 20 || sql.Errors.Cast<SqlError>().Any(e => Unavailable.Contains(e.Number)),
        InvalidOperationException { InnerException: { } inner } => IsUnavailable(inner),
        _ => false,
    };
}
