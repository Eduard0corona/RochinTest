using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CartonPrinting.Simulators.Printer;

/// <summary>
/// Capacidades declaradas del dispositivo (OT-01, OT-07): si acusa recibo de cada etiqueta, si informa
/// el fin de impresión por job y si puede confirmar que canceló un job y lo sacó de su cola.
/// </summary>
public sealed record PrinterCapabilities(bool ReportsAcceptance = true, bool ReportsCompletion = true, bool ConfirmsCancellation = false);

/// <summary>Comportamiento inyectable del dispositivo simulado.</summary>
public enum PrinterMode
{
    /// <summary>Recibe, imprime y, si lo declara, acusa recibo ("ACK serial").</summary>
    Normal,

    /// <summary>No acepta conexiones (el puerto queda cerrado).</summary>
    RejectConnections,

    /// <summary>Corta la conexión a mitad de la etiqueta: no imprime.</summary>
    CutMidWrite,

    /// <summary>Recibe e imprime la etiqueta completa y corta la conexión sin acusar recibo.</summary>
    PrintThenCut,

    /// <summary>Recibe e imprime y nunca responde (el emisor agota su espera).</summary>
    AcceptNoResponse,

    /// <summary>Recibe, acusa recibo y retiene la etiqueta en su cola interna hasta <see cref="PrinterSimulator.Release"/>.</summary>
    Hold,
}

/// <summary>Etiqueta recibida completa por el dispositivo.</summary>
public sealed record ReceivedLabel(string Serial, string? CycleKey, string Payload, int Bytes, DateTime ReceivedAt);

/// <summary>
/// Impresora de laboratorio por TCP (protocolo del simulador; el del dispositivo real es una decisión de
/// integración): recibe ZPL terminado en <c>^XZ</c>, cuenta los bytes recibidos por conexión y por etiqueta,
/// y según el modo rechaza conexiones, corta a mitad de la escritura, imprime sin responder, acusa recibo o
/// retiene en cola. Expone lo impreso para el simulador de evidencia. Nunca se conecta a equipo real.
/// </summary>
public sealed class PrinterSimulator : IAsyncDisposable
{
    private static readonly Regex SerialField = new(@"\^BCN,80,Y,N,N\^FD(?<serial>[^\^]*)\^FS", RegexOptions.Compiled);
    private static readonly Regex CycleField = new(@"\^FDCYCLE (?<key>[^\^]*)\^FS", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly List<ReceivedLabel> _received = [];
    private readonly List<ReceivedLabel> _printed = [];
    private readonly List<ReceivedLabel> _queue = [];
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private PrinterMode _mode;
    private long _bytes;
    private int _connections;

    private PrinterSimulator(PrinterCapabilities capabilities, int port)
    {
        Capabilities = capabilities;
        Port = port;
    }

    public PrinterCapabilities Capabilities { get; }

    public int Port { get; }

    /// <summary>Se imprimió una etiqueta (para el simulador de evidencia).</summary>
    public event Action<ReceivedLabel>? LabelPrinted;

    public PrinterMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }

        set
        {
            lock (_gate)
            {
                if (value == PrinterMode.RejectConnections && _mode != PrinterMode.RejectConnections)
                {
                    // Fuera de línea: deja de aceptar y corta las conexiones abiertas.
                    _listener?.Stop();
                    _listener = null;
                    foreach (var client in _clients)
                    {
                        try
                        {
                            Abort(client);
                        }
                        catch (ObjectDisposedException)
                        {
                            // Ya cerrada.
                        }
                    }
                }
                else if (value != PrinterMode.RejectConnections && _listener is null)
                {
                    Listen();
                }

                _mode = value;
            }
        }
    }

    /// <summary>Bytes recibidos en total, completos o no.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytes);

    public int Connections => Volatile.Read(ref _connections);

    public IReadOnlyList<ReceivedLabel> Received
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    public IReadOnlyList<ReceivedLabel> Printed
    {
        get
        {
            lock (_gate)
            {
                return [.. _printed];
            }
        }
    }

    public IReadOnlyList<ReceivedLabel> Queue
    {
        get
        {
            lock (_gate)
            {
                return [.. _queue];
            }
        }
    }

    /// <summary>Etiquetas completas recibidas con ese serial (más de una es una transmisión duplicada).</summary>
    public int TransmissionsOf(string serial) => Received.Count(l => l.Serial == serial);

    public static PrinterSimulator Start(PrinterCapabilities? capabilities = null)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var simulator = new PrinterSimulator(capabilities ?? new PrinterCapabilities(), port);
        lock (simulator._gate)
        {
            simulator.Listen();
        }

        return simulator;
    }

    /// <summary>Imprime una etiqueta retenida en la cola.</summary>
    public bool Release(string serial)
    {
        ReceivedLabel? label;
        lock (_gate)
        {
            label = _queue.Find(l => l.Serial == serial);
            if (label is null)
            {
                return false;
            }

            _queue.Remove(label);
        }

        Print(label);
        return true;
    }

    /// <summary>
    /// Cancela un job retenido. Devuelve true solo si el dispositivo declara que confirma cancelaciones y el
    /// job estaba en su cola: entonces sale de la cola y no se imprime.
    /// </summary>
    public bool Cancel(string serial)
    {
        lock (_gate)
        {
            if (!Capabilities.ConfirmsCancellation)
            {
                return false;
            }

            var label = _queue.Find(l => l.Serial == serial);
            return label is not null && _queue.Remove(label);
        }
    }

    private void Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        _listener = listener;
        _ = AcceptLoopAsync(listener);
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }

            Interlocked.Increment(ref _connections);
            _clients.Add(client);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var pending = new StringBuilder();
        var buffer = new byte[4096];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                Interlocked.Add(ref _bytes, read);
                pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (Mode == PrinterMode.CutMidWrite)
                {
                    Abort(client);
                    return;
                }

                int end;
                while ((end = pending.ToString().IndexOf("^XZ", StringComparison.Ordinal)) >= 0)
                {
                    var stop = end + 3;
                    if (stop < pending.Length && pending[stop] == '\n')
                    {
                        stop++;
                    }

                    var payload = pending.ToString(0, stop);
                    pending.Remove(0, stop);
                    if (!await HandleLabelAsync(client, stream, payload))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Conexión cerrada por el emisor o por el simulador.
        }
    }

    /// <summary>Devuelve false si la conexión se cortó.</summary>
    private async Task<bool> HandleLabelAsync(TcpClient client, NetworkStream stream, string payload)
    {
        var serial = SerialField.Match(payload) is { Success: true } m ? m.Groups["serial"].Value : string.Empty;
        var cycle = CycleField.Match(payload) is { Success: true } c ? c.Groups["key"].Value : null;
        var label = new ReceivedLabel(serial, cycle, payload, Encoding.UTF8.GetByteCount(payload), DateTime.UtcNow);
        var mode = Mode;
        lock (_gate)
        {
            _received.Add(label);
            if (mode == PrinterMode.Hold)
            {
                _queue.Add(label);
            }
        }

        switch (mode)
        {
            case PrinterMode.PrintThenCut:
                Print(label);
                Abort(client);
                return false;
            case PrinterMode.AcceptNoResponse:
                Print(label);
                return true;
            case PrinterMode.Hold:
                break;
            default:
                Print(label);
                break;
        }

        if (Capabilities.ReportsAcceptance)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"ACK {serial}\n"), _stop.Token);
        }

        return true;
    }

    private void Print(ReceivedLabel label)
    {
        lock (_gate)
        {
            _printed.Add(label);
        }

        LabelPrinted?.Invoke(label);
    }

    private static void Abort(TcpClient client)
    {
        client.Client.LingerState = new LingerOption(true, 0);
        client.Client.Close();
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_gate)
        {
            _listener?.Stop();
        }

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
