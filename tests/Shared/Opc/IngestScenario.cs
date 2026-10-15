using CartonPrinting.Infrastructure.Data;
using CartonPrinting.OpcIngest;

namespace CartonPrinting.Testing;

/// <summary>Ingesta de una estación de prueba (StationScenario) y bombeo desde el PLC simulado.</summary>
public static class IngestScenario
{
    public static StationIngestService Ingest(this StationScenario s) =>
        new(s.Settings, s.Registration, s.Stations, s.Epochs, new SequenceStore(s.Sql), s.Txs, s.Txm, s.Clock, s.Metrics);

    /// <summary>Procesa notificaciones del arnés OPC hasta que el predicado se cumpla o venza el plazo.</summary>
    public static async Task<List<IngestOutcome>> PumpAsync(
        this StationIngestService ingest, OpcHarness h, Func<List<IngestOutcome>, bool> done, TimeSpan? timeout = null)
    {
        var outcomes = new List<IngestOutcome>();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (!done(outcomes))
            {
                var raw = await h.Notifications.Reader.ReadAsync(cts.Token);
                outcomes.Add(await ingest.ProcessAsync(raw, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Devuelve lo procesado: la prueba decide con sus aserciones.
        }

        return outcomes;
    }

    /// <summary>Sumidero de LabelingReady en memoria (sin OPC).</summary>
    public sealed class RecordingSink : ILabelingReadySink
    {
        public List<bool> Writes { get; } = [];

        public bool Connected { get; set; } = true;

        public Task WriteLabelingReadyAsync(bool ready, CancellationToken ct)
        {
            Writes.Add(ready);
            return Task.CompletedTask;
        }
    }
}
