using System.Collections.Concurrent;
using CartonPrinting.Orchestration;

namespace CartonPrinting.Testing;

/// <summary>
/// Impresora en memoria que cuenta transmisiones por serial (arnés de la Fase 1; el simulador TCP
/// llega en la Fase 4). Cualquier serial recibido más de una vez es una transmisión física duplicada.
/// </summary>
public sealed class CountingPrinter
{
    private readonly ConcurrentDictionary<string, int> _bySerial = new();

    public int Transmissions => _bySerial.Values.Sum();

    public int DuplicatePhysicalTransmissions => _bySerial.Values.Sum(v => Math.Max(0, v - 1));

    public void Write(string payload)
    {
        var serial = LabelRenderer.ExtractSerial(payload) ?? throw new InvalidOperationException("Payload sin serial.");
        _bySerial.AddOrUpdate(serial, 1, (_, v) => v + 1);
    }

    public int CountFor(string serial) => _bySerial.TryGetValue(serial, out var v) ? v : 0;
}
