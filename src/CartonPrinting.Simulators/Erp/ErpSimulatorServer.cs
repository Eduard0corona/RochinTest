using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using CartonPrinting.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CartonPrinting.Simulators.Erp;

/// <summary>Cómo responde el ERP simulado.</summary>
public enum ErpMode
{
    /// <summary>Responde el snapshot (o 404 si la orden no existe), después del retraso configurado.</summary>
    Up,

    /// <summary>503 Service Unavailable.</summary>
    Unavailable,

    /// <summary>Acepta la conexión y no responde: el cliente agota su timeout.</summary>
    Hang,
}

/// <summary>Snapshot de una orden de trabajo tal como lo publica el ERP simulado.</summary>
public sealed record ErpWorkOrder
{
    [JsonPropertyName("workOrderId")] public required string WorkOrderId { get; init; }
    [JsonPropertyName("revision")] public required string Revision { get; init; }
    [JsonPropertyName("productId")] public required string ProductId { get; init; }
    [JsonPropertyName("lotId")] public required string LotId { get; init; }
    [JsonPropertyName("gtin")] public string? Gtin { get; init; }
    [JsonPropertyName("validFrom")][JsonConverter(typeof(UtcMillisecondsConverter))] public required DateTime ValidFrom { get; init; }
    [JsonPropertyName("validUntil")][JsonConverter(typeof(UtcMillisecondsConverter))] public required DateTime ValidUntil { get; init; }
}

/// <summary>
/// ERP simulado: servidor HTTP en proceso (solo 127.0.0.1) con latencia y caídas configurables
/// (<c>GET /api/work-orders/{id}</c>). El contrato real con el ERP es una decisión de integración; el
/// cliente lo consume detrás de <c>IErpClient</c>. Nunca se conecta a un ERP real.
/// </summary>
public sealed class ErpSimulatorServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ErpWorkOrder> _orders = new(StringComparer.Ordinal);
    private WebApplication? _app;
    private int _requests;

    public ErpMode Mode { get; set; } = ErpMode.Up;

    public int DelayMs { get; set; }

    public int Requests => Volatile.Read(ref _requests);

    public string BaseUrl { get; private set; } = string.Empty;

    public static async Task<ErpSimulatorServer> StartAsync(CancellationToken ct = default)
    {
        var simulator = new ErpSimulatorServer();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/api/work-orders/{id}", simulator.HandleAsync);
        await app.StartAsync(ct);
        simulator._app = app;
        simulator.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
        return simulator;
    }

    public void Set(ErpWorkOrder order) => _orders[order.WorkOrderId] = order;

    private async Task<IResult> HandleAsync(string id, HttpContext http)
    {
        Interlocked.Increment(ref _requests);
        if (DelayMs > 0)
        {
            await Task.Delay(DelayMs, http.RequestAborted);
        }

        switch (Mode)
        {
            case ErpMode.Unavailable:
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            case ErpMode.Hang:
                await Task.Delay(Timeout.Infinite, http.RequestAborted);
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
        }

        return _orders.TryGetValue(id, out var order)
            ? Results.Text(ContractJson.Serialize(order), "application/json")
            : Results.NotFound();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
