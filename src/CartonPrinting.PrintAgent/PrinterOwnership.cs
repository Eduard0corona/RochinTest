using System.Text.RegularExpressions;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.PrintAgent;

/// <summary>
/// Lock de sistema operativo por impresora (I11, ADR 0010): un archivo abierto con <c>FileShare.None</c> en un directorio
/// común a todos los procesos de la máquina. Impide que dos procesos del mismo host sean dueños de la misma impresora;
/// entre hosts lo detecta el lease en SQL. El sistema operativo lo libera cuando el proceso muere, de cualquier forma.
/// No se usa un mutex con nombre: en Windows pertenece al hilo que lo tomó, y con código asíncrono ese hilo puede
/// terminar y dejarlo "abandonado" mientras el proceso sigue vivo, de modo que otro proceso lo tomaría.
/// </summary>
public sealed partial class PrinterOsLock : IDisposable
{
    private readonly FileStream _file;

    private PrinterOsLock(FileStream file) => _file = file;

    /// <summary>Directorio de los locks por defecto: común a todos los usuarios de la máquina.</summary>
    public static string DefaultDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CartonPrinting", "locks")
        : Path.Combine(Path.GetTempPath(), "cartonprinting-locks");

    public static PrinterOsLock? TryAcquire(string printerId, string? lockDirectory = null)
    {
        var directory = lockDirectory ?? DefaultDirectory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"printer-{SafeName().Replace(printerId, "_")}.lock");
        try
        {
            return new PrinterOsLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _file.Dispose();

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
    int leaseDurationMs = 10_000, int leaseGraceMs = 2_000, ILogger<PrinterOwnership>? logger = null, string? lockDirectory = null) : IDisposable
{
    private readonly ILogger _logger = logger ?? NullLogger<PrinterOwnership>.Instance;
    private PrinterOsLock? _lock;

    public LeaseGrant? Lease { get; private set; }

    public string PrinterId => printerId;

    public int LeaseDurationMs => leaseDurationMs;

    /// <summary>Un intento: lock del SO y lease. Devuelve el lease o null (otro dueño en este host o en otro).</summary>
    public async Task<LeaseGrant?> TryAcquireAsync(CancellationToken ct)
    {
        _lock ??= PrinterOsLock.TryAcquire(printerId, lockDirectory);
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

/// <summary>
/// Ciclo de vida de una impresora propia (I11, ADR 0010; ESTRATEGIA_RECUPERACION, sección 1). En el arranque toma la
/// propiedad (<see cref="AcquireAsync"/>) y ejecuta TX-R0 (<see cref="RecoverAsync"/>). Después,
/// <see cref="RunAsync"/> corre el sender solo mientras tiene el lock y el lease, renovándolo. Si lo pierde, detiene el
/// sender y vuelve a intentar tomar la impresora cada duración del lease (con una alerta por intento fallido), y
/// ejecuta TX-R0 otra vez antes de enviar. Sin propiedad, el sender nunca se invoca: cero envíos.
/// </summary>
public sealed class OwnedPrinterRunner(PrinterOwnership ownership, SenderRecovery recovery, EdgeClock clock, ILogger<OwnedPrinterRunner>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<OwnedPrinterRunner>.Instance;
    private volatile bool _owned;

    public bool Owned => _owned;

    /// <summary>Cuántas veces se invocó el sender (para alertas y pruebas).</summary>
    public int SenderRuns { get; private set; }

    public async Task<bool> AcquireAsync(CancellationToken ct)
    {
        _owned = await ownership.TryAcquireAsync(ct) is not null;
        return _owned;
    }

    /// <summary>TX-R0 de la impresora, solo si este proceso es su dueño.</summary>
    public Task RecoverAsync(CancellationToken ct) => _owned ? recovery.RecoverAsync(ownership.PrinterId, ct) : Task.CompletedTask;

    public async Task RunAsync(Func<LeaseGrant, CancellationToken, Task> runSender, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_owned)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ownership.LeaseDurationMs), clock.TimeProvider, ct);
                if (!await AcquireAsync(ct))
                {
                    continue;
                }

                await recovery.RecoverAsync(ownership.PrinterId, ct);
            }

            using var lost = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var renewal = ownership.RenewAsync(lost, ct);
            try
            {
                SenderRuns++;
                await runSender(ownership.Lease!, lost.Token);
            }
            catch (OperationCanceledException) when (lost.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogError("Impresora {Printer}: lease perdido; el sender se detuvo y no envía hasta volver a tomarla", ownership.PrinterId);
            }

            _owned = false;
            await lost.CancelAsync();
            try
            {
                await renewal;
            }
            catch (OperationCanceledException)
            {
                // Detención o lease perdido.
            }
        }
    }
}

