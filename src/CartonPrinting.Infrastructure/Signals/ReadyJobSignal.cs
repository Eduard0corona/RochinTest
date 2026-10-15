using System.Threading.Channels;

namespace CartonPrinting.Infrastructure.Signals;

/// <summary>
/// Señal en proceso (G11; ARQUITECTURA_TECNICA: System.Threading.Channels para señalizar en proceso): quien deja un job
/// READY (TX-A, TX-E o el barrido al liberar un RETRY_WAIT) despierta al sender de la impresora sin esperar su sondeo. Es
/// solo una señal: el sender siempre lee de SQL qué enviar, y el sondeo sigue como respaldo si una señal se pierde.
/// </summary>
public sealed class ReadyJobSignal
{
    // Una señal pendiente basta: varias seguidas despiertan al sender una vez, y él toma todo lo READY en orden.
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public void Notify() => _channel.Writer.TryWrite(true);

    /// <summary>Espera la próxima señal (o la cancelación, que el sender usa como intervalo de sondeo).</summary>
    public async Task WaitAsync(CancellationToken ct) => await _channel.Reader.ReadAsync(ct);
}
