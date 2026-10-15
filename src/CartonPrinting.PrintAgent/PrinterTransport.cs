using System.Net.Sockets;
using System.Text;

namespace CartonPrinting.PrintAgent;

/// <summary>
/// Canal con una impresora. La conexión se abre antes de la escritura: si falla, la escritura nunca se
/// invocó (NOT_SENT). Cualquier error desde que se invoca la escritura deja el intento en UNKNOWN (I02).
/// </summary>
public interface IPrinterTransport : IAsyncDisposable
{
    bool Connected { get; }

    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Escribe el payload. <paramref name="midWrite"/> se invoca después de la primera mitad (punto de falla de
    /// T07). Devuelve el instante del acuse del dispositivo, o null si el dispositivo no acusa recibo. Un acuse
    /// de recibo no prueba impresión ni aplicación (I05).
    /// </summary>
    Task<DateTime?> SendAsync(string payload, Func<Task>? midWrite, CancellationToken ct);

    /// <summary>Descarta la conexión actual (después de un error, el canal no se reutiliza).</summary>
    void Reset();
}

/// <summary>Configuración del canal TCP con la impresora (protocolo del simulador; el real es decisión de OT).</summary>
public sealed class PrinterTransportOptions
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; }

    public int ConnectTimeoutMs { get; set; } = 500;

    /// <summary>Si el dispositivo acusa recibo de cada etiqueta ("ACK serial"), cuánto se espera el acuse.</summary>
    public bool ExpectsAcceptance { get; set; } = true;

    public int AcceptanceTimeoutMs { get; set; } = 1_000;
}

/// <summary>Una sola conexión TCP por impresora, usada por su único sender.</summary>
public sealed class TcpPrinterTransport(PrinterTransportOptions options, TimeProvider time) : IPrinterTransport
{
    private TcpClient? _client;
    private StreamReader? _reader;

    public bool Connected => _client is { Connected: true };

    public async Task ConnectAsync(CancellationToken ct)
    {
        Reset();
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(options.Host, options.Port, timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _reader = new StreamReader(client.GetStream(), Encoding.ASCII, false, 256, leaveOpen: true);
    }

    public async Task<DateTime?> SendAsync(string payload, Func<Task>? midWrite, CancellationToken ct)
    {
        var stream = (_client ?? throw new InvalidOperationException("Sin conexión con la impresora.")).GetStream();
        var bytes = Encoding.UTF8.GetBytes(payload);
        var half = bytes.Length / 2;
        await stream.WriteAsync(bytes.AsMemory(0, half), ct);
        await stream.FlushAsync(ct);
        if (midWrite is not null)
        {
            await midWrite();
        }

        await stream.WriteAsync(bytes.AsMemory(half), ct);
        await stream.FlushAsync(ct);
        if (!options.ExpectsAcceptance)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(options.AcceptanceTimeoutMs));
        var line = await _reader!.ReadLineAsync(timeout.Token)
            ?? throw new IOException("La impresora cerró la conexión sin acusar recibo.");
        if (!line.StartsWith("ACK", StringComparison.Ordinal))
        {
            throw new IOException($"Respuesta inesperada de la impresora: {line}");
        }

        return time.GetUtcNow().UtcDateTime;
    }

    public void Reset()
    {
        _reader?.Dispose();
        _reader = null;
        _client?.Dispose();
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        return ValueTask.CompletedTask;
    }
}
