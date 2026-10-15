using System.Text.RegularExpressions;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.PrintAgent;

/// <summary>
/// Lock de sistema operativo por impresora (I11, ADR 0010): en Windows, un mutex con nombre global; en Linux,
/// <c>flock</c> sobre un archivo (FileShare.None de .NET). Impide que dos procesos del mismo host sean dueños de
/// la misma impresora; entre hosts lo detecta el lease en SQL. Se mantiene mientras viva el proceso.
/// </summary>
public sealed partial class PrinterOsLock : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly FileStream? _file;

    private PrinterOsLock(Mutex? mutex, FileStream? file)
    {
        _mutex = mutex;
        _file = file;
    }

    public static PrinterOsLock? TryAcquire(string printerId, string? lockDirectory = null)
    {
        var name = SafeName().Replace(printerId, "_");
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(false, $@"Global\CartonPrinting.Printer.{name}");
            try
            {
                if (mutex.WaitOne(0))
                {
                    return new PrinterOsLock(mutex, null);
                }
            }
            catch (AbandonedMutexException)
            {
                // El dueño anterior murió sin liberarlo: ahora es de este proceso.
                return new PrinterOsLock(mutex, null);
            }

            mutex.Dispose();
            return null;
        }

        var path = Path.Combine(lockDirectory ?? Path.GetTempPath(), $"cartonprinting-printer-{name}.lock");
        try
        {
            return new PrinterOsLock(null, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _mutex?.Dispose();   // Sin ReleaseMutex: el dueño queda "abandonado" y el siguiente lo toma.
        _file?.Dispose();
    }

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex SafeName();
}

/// <summary>
/// Propiedad de una impresora (Fase 5): lock de sistema operativo, después lease en SQL con renovación. Sin
/// el lock o sin el lease, este proceso no envía nada y se alerta (printer_owner_conflicts_total). El lease no
/// impide que un proceso que ya validó su token transmita (la impresora no valida tokens): solo el aislamiento
/// físico del canal lo garantiza (T36, ADR 0010).
/// </summary>
public sealed class PrinterOwnership(
    string printerId, string ownerInstanceId, string ownerHost, PrinterLeaseStore leases, EdgeClock clock, CartonMetrics metrics,
    int leaseDurationMs = 10_000, int leaseGraceMs = 2_000, ILogger<PrinterOwnership>? logger = null) : IDisposable
{
    private readonly ILogger _logger = logger ?? NullLogger<PrinterOwnership>.Instance;
    private PrinterOsLock? _lock;

    public LeaseGrant? Lease { get; private set; }

    /// <summary>Un intento: lock del SO y lease. Devuelve el lease o null (otro dueño en este host o en otro).</summary>
    public async Task<LeaseGrant?> TryAcquireAsync(CancellationToken ct)
    {
        _lock ??= PrinterOsLock.TryAcquire(printerId);
        if (_lock is null)
        {
            metrics.Increment(MetricNames.PrinterOwnerConflicts, ("printer", printerId), ("cause", "os_lock"));
            _logger.LogError("Impresora {Printer}: otro proceso de este host tiene el lock; este no envía", printerId);
            return null;
        }

        Lease = await leases.AcquireAsync(printerId, ownerInstanceId, ownerHost, leaseDurationMs, leaseGraceMs, clock.UtcNow, ct);
        if (Lease is null)
        {
            metrics.Increment(MetricNames.PrinterOwnerConflicts, ("printer", printerId), ("cause", "lease"));
            _logger.LogError("Impresora {Printer}: otra instancia tiene el lease vigente; esta no envía", printerId);
        }

        return Lease;
    }

    /// <summary>Renueva el lease; si se pierde, cancela <paramref name="lost"/> para que el sender deje de reclamar.</summary>
    public async Task RenewAsync(CancellationTokenSource lost, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !lost.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(leaseDurationMs / 3.0), clock.TimeProvider, ct);
            var renewed = await leases.AcquireAsync(printerId, ownerInstanceId, ownerHost, leaseDurationMs, leaseGraceMs, clock.UtcNow, ct);
            if (renewed is null || renewed.FencingToken != Lease?.FencingToken)
            {
                metrics.Increment(MetricNames.PrinterOwnerConflicts, ("printer", printerId), ("cause", "lease_lost"));
                _logger.LogError("Impresora {Printer}: lease perdido; el sender se detiene", printerId);
                await lost.CancelAsync();
                return;
            }

            Lease = renewed;
        }
    }

    public void Dispose() => _lock?.Dispose();
}
