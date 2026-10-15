using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Simulators.Printer;

namespace CartonPrinting.Simulators.Evidence;

/// <summary>Qué fuentes de evidencia tiene la estación simulada y si reaccionan solas a cada etiqueta impresa.</summary>
public sealed record EvidenceSources(bool PrinterCompletion = true, bool Applicator = false, bool Scanner = false);

/// <summary>
/// Evidencia simulada de una estación: la impresora informa el fin de impresión por job (PRINTED,
/// correlacionada al job), el aplicador confirma por ciclo (APPLIED) y el escáner lee el código de barras de la
/// caja del ciclo (VERIFIED, con ReadSerial). La correlación por ciclo sale del tracking del PLC: aquí, de la
/// identidad del ciclo impresa en la etiqueta. Las pruebas pueden inyectar lecturas (otro serial, tarde) o
/// apagar fuentes. Cada observación va a <c>sink</c> (el servicio de evidencia: registro y TX-D).
/// </summary>
public sealed class EvidenceSimulator : IDisposable
{
    private readonly PrinterSimulator _printer;
    private readonly string _siteId;
    private readonly string _stationId;
    private readonly Func<string, CancellationToken, Task<Guid?>> _jobBySerial;
    private readonly Func<ObservationInput, CancellationToken, Task> _sink;
    private readonly Func<DateTime> _now;
    private readonly List<Task> _pending = [];

    public EvidenceSimulator(
        PrinterSimulator printer, string siteId, string stationId, EvidenceSources sources, Func<string, CancellationToken, Task<Guid?>> jobBySerial,
        Func<ObservationInput, CancellationToken, Task> sink, Func<DateTime> now)
    {
        _printer = printer;
        _siteId = siteId;
        _stationId = stationId;
        Sources = sources;
        _jobBySerial = jobBySerial;
        _sink = sink;
        _now = now;
        printer.LabelPrinted += OnPrinted;
    }

    public EvidenceSources Sources { get; set; }

    /// <summary>Espera a que terminen las observaciones disparadas por etiquetas impresas.</summary>
    public async Task DrainAsync()
    {
        Task[] pending;
        lock (_pending)
        {
            pending = [.. _pending];
            _pending.Clear();
        }

        await Task.WhenAll(pending);
    }

    private void OnPrinted(ReceivedLabel label)
    {
        var task = Task.Run(async () =>
        {
            if (Sources.PrinterCompletion)
            {
                await PrinterCompletedAsync(label.Serial, CancellationToken.None);
            }

            if (label.CycleKey is { } key)
            {
                if (Sources.Applicator)
                {
                    await ApplyAsync(key, CancellationToken.None);
                }

                if (Sources.Scanner)
                {
                    await ScanAsync(key, label.Serial, CancellationToken.None);
                }
            }
        });
        lock (_pending)
        {
            _pending.Add(task);
        }
    }

    /// <summary>La impresora informa que terminó el job de ese serial (PRINTED, correlacionada al job).</summary>
    public async Task PrinterCompletedAsync(string serial, CancellationToken ct)
    {
        if (await _jobBySerial(serial, ct) is { } jobId)
        {
            await _sink(new ObservationInput(_siteId, _stationId, null, null, jobId, null, EvidenceType.PrinterJobCompletion, EvidenceLevel.Printed,
                CorrelatedTo.Job, null, $"SIM-PRN-{serial}", _now(), "SYSTEM"), ct);
        }
    }

    /// <summary>El aplicador confirma que aplicó la etiqueta en la caja del ciclo (APPLIED).</summary>
    public Task ApplyAsync(string cycleKey, CancellationToken ct)
    {
        var (epoch, cycle) = Parse(cycleKey);
        return _sink(new ObservationInput(_siteId, _stationId, epoch, cycle, null, null, EvidenceType.PlcApplicatorConfirmation, EvidenceLevel.Applied,
            CorrelatedTo.Cycle, null, $"SIM-APL-{cycleKey}", _now(), "SYSTEM"), ct);
    }

    /// <summary>El escáner lee <paramref name="readSerial"/> en la caja del ciclo (VERIFIED).</summary>
    public Task ScanAsync(string cycleKey, string readSerial, CancellationToken ct)
    {
        var (epoch, cycle) = Parse(cycleKey);
        return _sink(new ObservationInput(_siteId, _stationId, epoch, cycle, null, null, EvidenceType.BarcodeVerification, EvidenceLevel.Verified,
            CorrelatedTo.Cycle, readSerial, $"SIM-SCN-{cycleKey}", _now(), "SYSTEM"), ct);
    }

    /// <summary>Identidad del ciclo "site:station:epoch:cycle" (los dos últimos segmentos).</summary>
    private static (long Epoch, ulong Cycle) Parse(string key)
    {
        var parts = key.Split(':');
        return (long.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture),
            ulong.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose() => _printer.LabelPrinted -= OnPrinted;
}
