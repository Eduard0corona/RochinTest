using System.Data;
using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>
/// Parámetros con el tipo exacto de la columna. Los instantes van como <c>DATETIME2(3)</c> ya
/// truncados a milisegundos (SQL Server redondearía); <c>CycleId</c> como <c>DECIMAL(20,0)</c>.
/// </summary>
public static class SqlCommandExtensions
{
    public static SqlCommand Command(this SqlConnection conn, SqlTransaction? tx, string sql, int timeoutSeconds = 30) =>
        new(sql, conn, tx) { CommandTimeout = timeoutSeconds };

    public static SqlCommand NVarChar(this SqlCommand cmd, string name, string? value, int size)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, size) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand NVarCharMax(this SqlCommand cmd, string name, string? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand VarChar(this SqlCommand cmd, string name, string? value, int size)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.VarChar, size) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand Guid(this SqlCommand cmd, string name, Guid? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand BigInt(this SqlCommand cmd, string name, long? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.BigInt) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand Int(this SqlCommand cmd, string name, int? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Int) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand TinyInt(this SqlCommand cmd, string name, byte? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.TinyInt) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand Bit(this SqlCommand cmd, string name, bool? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Bit) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    public static SqlCommand Binary32(this SqlCommand cmd, string name, byte[]? value)
    {
        if (value is not null && value.Length != 32)
        {
            throw new ArgumentException("Se esperaban 32 bytes (SHA-256).", name);
        }

        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Binary, 32) { Value = (object?)value ?? DBNull.Value });
        return cmd;
    }

    /// <summary><c>DATETIME2(3)</c>; el valor se trunca a milisegundos antes de enviarlo.</summary>
    public static SqlCommand DateTime2(this SqlCommand cmd, string name, DateTime? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime2)
        {
            Scale = 3,
            Value = value is null ? DBNull.Value : CanonicalTimestamp.Truncate(value.Value),
        });
        return cmd;
    }

    /// <summary><c>CycleId</c> como <c>DECIMAL(20,0)</c>.</summary>
    public static SqlCommand Cycle(this SqlCommand cmd, string name, ulong? value)
    {
        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = 20,
            Scale = 0,
            Value = value is null ? DBNull.Value : (decimal)value.Value,
        });
        return cmd;
    }

    public static ulong GetCycleId(this SqlDataReader reader, int ordinal) => (ulong)reader.GetDecimal(ordinal);

    public static ulong? GetNullableCycleId(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (ulong)reader.GetDecimal(ordinal);

    public static string? GetNullableString(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTime GetUtc(this SqlDataReader reader, int ordinal) =>
        DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    public static DateTime? GetNullableUtc(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    public static long? GetNullableInt64(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static int? GetNullableInt32(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static Guid? GetNullableGuid(this SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    public static byte[] GetBytes32(this SqlDataReader reader, int ordinal) => (byte[])reader.GetValue(ordinal);
}
