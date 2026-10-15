using System.Text.Json;
using CartonPrinting.Contracts;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Infrastructure.Data;

/// <summary>
/// Inserta eventos en dbo.OutboxEvent en la misma transacción que el cambio de estado. Antes de
/// insertar valida el JSON contra su schema y las reglas de negocio: emitir un contrato inválido es
/// un defecto y la transacción se revierte.
/// </summary>
public static class OutboxWriter
{
    public const string CartonDetectedType = "CartonDetected";
    public const string PrintJobResultType = "PrintJobResult";
    public const string DetectionAggregate = "CartonDetection";
    public const string JobAggregate = "PrintJob";

    /// <summary>
    /// CartonDetected. <paramref name="opensGapWithoutDeadline"/>: registro GATEWAY_RECEIPT en vivo que
    /// abre un hueco; la máquina de estados (D04) lo registra sin plazo (DeadlineAt = ReceivedAt),
    /// pero la regla de negocio del verificador no ve el hueco y exigiría un plazo posterior (SI-05).
    /// Solo en ese caso se tolera esa regla; todas las demás se exigen.
    /// </summary>
    public static Task InsertCartonDetectedAsync(SqlConnection conn, SqlTransaction tx, CartonDetectedEvent evt, bool opensGapWithoutDeadline, CancellationToken ct)
    {
        var tolerated = opensGapWithoutDeadline
            && evt.DeadlineReference == DeadlineReference.GatewayReceipt
            && evt.DeadlineAt == evt.ReceivedAt
                ? ContractBusinessRules.DeadlineNotAfterReference
                : null;
        return InsertAsync(conn, tx, evt.EventId, CartonDetectedType, evt.IdempotencyKey, DetectionAggregate, evt.EventId, 1,
            ContractJson.Serialize(evt), ct, tolerated);
    }

    /// <summary>Resultado de una detección sin job (BLOCKED, EXPIRED por LATE_ARRIVAL o ERP_UNAVAILABLE).</summary>
    public static Task InsertDetectionResultAsync(SqlConnection conn, SqlTransaction tx, PrintJobResultEvent evt, CancellationToken ct) =>
        InsertAsync(conn, tx, evt.EventId, PrintJobResultType, evt.IdempotencyKey, DetectionAggregate, evt.CorrelationId,
            evt.ResultVersion, ContractJson.Serialize(evt), ct);

    public static Task InsertJobResultAsync(SqlConnection conn, SqlTransaction tx, PrintJobResultEvent evt, CancellationToken ct) =>
        InsertAsync(conn, tx, evt.EventId, PrintJobResultType, evt.IdempotencyKey, JobAggregate,
            evt.PrintJobId ?? throw new InvalidOperationException("Resultado de job sin printJobId."), evt.ResultVersion,
            ContractJson.Serialize(evt), ct);

    private static async Task InsertAsync(
        SqlConnection conn, SqlTransaction tx, Guid eventId, string eventType, string orderingKey, string aggregateType, Guid aggregateId,
        int aggregateVersion, string payload, CancellationToken ct, string? toleratedBusinessRule = null)
    {
        EnsureContract(payload, toleratedBusinessRule);
        await using var cmd = conn.Command(tx, """
            INSERT INTO dbo.OutboxEvent (EventId, EventType, OrderingKey, AggregateType, AggregateId, AggregateVersion, Payload)
            VALUES (@id, @type, @key, @aggType, @aggId, @aggVersion, @payload);
            """);
        cmd.Guid("@id", eventId).VarChar("@type", eventType, 30).NVarChar("@key", orderingKey, 220).VarChar("@aggType", aggregateType, 20)
            .Guid("@aggId", aggregateId).Int("@aggVersion", aggregateVersion).NVarCharMax("@payload", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public static void EnsureContract(string payload, string? toleratedBusinessRule = null)
    {
        ContractSchemaValidator.Instance.EnsureValid(payload);
        using var doc = JsonDocument.Parse(payload);
        var business = ContractBusinessRules.Validate(doc.RootElement).Where(e => e != toleratedBusinessRule).ToList();
        if (business.Count > 0)
        {
            throw new ContractViolationException($"El evento viola reglas de negocio: {string.Join("; ", business)}");
        }
    }
}
