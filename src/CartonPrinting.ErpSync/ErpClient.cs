using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Time;

namespace CartonPrinting.ErpSync;

/// <summary>El ERP no respondió o respondió algo que no se puede usar: no hay snapshot.</summary>
public sealed class ErpUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Fuente de snapshots de órdenes de trabajo. Nunca se llama desde el camino de ingesta.</summary>
public interface IErpClient
{
    /// <summary>El snapshot vigente de la orden, o null si el ERP no la conoce. Lanza si el ERP no está disponible.</summary>
    Task<ErpSnapshot?> GetSnapshotAsync(string workOrderId, CancellationToken ct);
}

/// <summary>
/// Cliente HTTP del ERP (contrato del simulador; el del ERP real es una decisión de integración). Un
/// snapshot incompleto o con vigencia inválida se rechaza como no disponible: nunca se usa un snapshot
/// no validado (I10). <c>SourceHash</c> es el SHA-256 del cuerpo recibido.
/// </summary>
public sealed class HttpErpClient(HttpClient http, EdgeClock clock) : IErpClient
{
    public async Task<ErpSnapshot?> GetSnapshotAsync(string workOrderId, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"api/work-orders/{Uri.EscapeDataString(workOrderId)}", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ErpUnavailableException($"ERP inalcanzable: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ErpUnavailableException($"ERP respondió {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsByteArrayAsync(ct);
            WorkOrderDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<WorkOrderDto>(body, ContractJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                throw new ErpUnavailableException("Snapshot ERP mal formado.", ex);
            }

            // La vigencia se trunca a milisegundos antes de validarla (lo que se guarda es DATETIME2(3)), y cada campo
            // se valida contra el largo de su columna: un parámetro NVarChar(n) truncaría en silencio.
            var from = dto?.ValidFrom is { } f ? CanonicalTimestamp.Truncate(f) : (DateTime?)null;
            var until = dto?.ValidUntil is { } u ? CanonicalTimestamp.Truncate(u) : (DateTime?)null;
            if (dto is null || dto.WorkOrderId != workOrderId || !Fits(dto.Revision, 64) || !Fits(dto.ProductId, 128) || !Fits(dto.LotId, 128)
                || from is null || until is null || until <= from
                || (dto.Gtin is not null && (dto.Gtin.Length != 14 || !dto.Gtin.All(char.IsAsciiDigit))))
            {
                throw new ErpUnavailableException($"Snapshot ERP de {workOrderId} incompleto, con campos fuera de formato o con vigencia inválida.");
            }

            return new ErpSnapshot(dto.WorkOrderId, dto.Revision!, dto.ProductId!, dto.LotId!, dto.Gtin, from.Value, until.Value, clock.UtcNow,
                SHA256.HashData(body));
        }
    }

    private static bool Fits(string? value, int maxLength) => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private sealed record WorkOrderDto
    {
        [JsonPropertyName("workOrderId")] public string? WorkOrderId { get; init; }
        [JsonPropertyName("revision")] public string? Revision { get; init; }
        [JsonPropertyName("productId")] public string? ProductId { get; init; }
        [JsonPropertyName("lotId")] public string? LotId { get; init; }
        [JsonPropertyName("gtin")] public string? Gtin { get; init; }
        [JsonPropertyName("validFrom")][JsonConverter(typeof(UtcMillisecondsConverter))] public DateTime? ValidFrom { get; init; }
        [JsonPropertyName("validUntil")][JsonConverter(typeof(UtcMillisecondsConverter))] public DateTime? ValidUntil { get; init; }
    }
}
