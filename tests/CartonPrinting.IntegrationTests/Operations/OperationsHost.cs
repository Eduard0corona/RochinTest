using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CartonPrinting.Operations;
using CartonPrinting.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CartonPrinting.IntegrationTests.Operations;

/// <summary>
/// La Operations API real (Kestrel en 127.0.0.1) sobre la base del escenario, con claves de prueba generadas en cada
/// corrida (nunca en el repositorio): la configuración recibe solo su SHA-256.
/// </summary>
public sealed class OperationsHost : IAsyncDisposable
{
    public const string Operator = "operador.lopez";
    public const string Supervisor = "supervisor.ramos";
    public const string Auditor = "auditora.diaz";

    private readonly WebApplication _app;
    private readonly Dictionary<string, string> _keys;

    private OperationsHost(WebApplication app, Dictionary<string, string> keys, string baseUrl)
    {
        _app = app;
        _keys = keys;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<OperationsHost> StartAsync(StationScenario s, CancellationToken ct)
    {
        var keys = new[] { Operator, Supervisor, Auditor }.ToDictionary(id => id, _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(s.Sql);
        builder.Services.AddSingleton(s.Clock);
        builder.Services.AddSingleton(s.Metrics);
        builder.Services.AddOperations(new OperationsOptions
        {
            OperatorKeySha256 = keys.ToDictionary(k => k.Key, k => OperatorAuthenticationHandler.Hash(k.Value)),
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOperations();
        await app.StartAsync(ct);
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
        return new OperationsHost(app, keys, url);
    }

    /// <summary>Cliente autenticado como <paramref name="operatorId"/> (o con la clave equivocada).</summary>
    public HttpClient As(string operatorId, bool wrongKey = false)
    {
        var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        http.DefaultRequestHeaders.Add(OperatorAuthenticationHandler.IdHeader, operatorId);
        http.DefaultRequestHeaders.Add(OperatorAuthenticationHandler.KeyHeader, wrongKey ? "clave-equivocada" : _keys[operatorId]);
        return http;
    }

    public HttpClient Anonymous() => new() { BaseAddress = new Uri(BaseUrl) };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public static class OperationsCalls
{
    public static async Task<(int Status, JsonElement Body)> CallAsync(this HttpClient http, string path, object? body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body ?? new { }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        return ((int)response.StatusCode, json);
    }
}
