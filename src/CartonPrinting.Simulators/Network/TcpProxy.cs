using System.Net;
using System.Net.Sockets;

namespace CartonPrinting.Simulators.Network;

/// <summary>
/// Proxy TCP para inyectar fallas de red entre un cliente y su servidor (OPC UA o impresora):
/// congelar el tráfico sin cerrar (el keep-alive falla), cortar todas las conexiones, rechazar
/// conexiones nuevas y, para T36, dejar pasar solo a un dueño autorizado.
/// </summary>
public sealed class TcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly List<Pair> _pairs = [];
    private readonly CancellationTokenSource _cts = new();
    private volatile TaskCompletionSource _flowing = CompletedGate();
    private Task? _acceptLoop;

    public TcpProxy(string targetHost, int targetPort, int listenPort = 0)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Si es false, las conexiones nuevas se cierran al aceptarlas.</summary>
    public bool AcceptNew { get; set; } = true;

    /// <summary>Filtro de origen (T36: solo el dueño activo pasa); null deja pasar a todos.</summary>
    public Func<IPEndPoint, bool>? Admit { get; set; }

    public int ActiveConnections
    {
        get
        {
            lock (_pairs)
            {
                return _pairs.Count;
            }
        }
    }

    public long BytesToServer => Interlocked.Read(ref _bytesToServer);

    private long _bytesToServer;

    public TcpProxy Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return this;
    }

    /// <summary>Detiene el reenvío en ambos sentidos sin cerrar las conexiones.</summary>
    public void Freeze()
    {
        if (_flowing.Task.IsCompleted)
        {
            _flowing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume() => _flowing.TrySetResult();

    /// <summary>Cierra todas las conexiones activas.</summary>
    public void SeverAll()
    {
        List<Pair> pairs;
        lock (_pairs)
        {
            pairs = [.. _pairs];
            _pairs.Clear();
        }

        foreach (var pair in pairs)
        {
            pair.Close();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            if (!AcceptNew || (Admit is not null && !Admit(remote)))
            {
                client.Dispose();
                continue;
            }

            var server = new TcpClient();
            try
            {
                await server.ConnectAsync(_targetHost, _targetPort, _cts.Token);
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
                client.Dispose();
                server.Dispose();
                continue;
            }

            var pair = new Pair(client, server);
            lock (_pairs)
            {
                _pairs.Add(pair);
            }

            _ = PumpAsync(pair, client.GetStream(), server.GetStream(), toServer: true);
            _ = PumpAsync(pair, server.GetStream(), client.GetStream(), toServer: false);
        }
    }

    private async Task PumpAsync(Pair pair, NetworkStream from, NetworkStream to, bool toServer)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, _cts.Token);
                if (read == 0)
                {
                    break;
                }

                await _flowing.Task.WaitAsync(_cts.Token);
                if (pair.Closed)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), _cts.Token);
                if (toServer)
                {
                    Interlocked.Add(ref _bytesToServer, read);
                }
            }
        }
        catch (Exception)
        {
            // Conexión cortada por el proxy o por un extremo.
        }
        finally
        {
            lock (_pairs)
            {
                _pairs.Remove(pair);
            }

            pair.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        SeverAll();
        if (_acceptLoop is not null)
        {
            await _acceptLoop;
        }

        _cts.Dispose();
    }

    private static TaskCompletionSource CompletedGate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private sealed class Pair(TcpClient client, TcpClient server)
    {
        public volatile bool Closed;

        public void Close()
        {
            Closed = true;
            client.Dispose();
            server.Dispose();
        }
    }
}
