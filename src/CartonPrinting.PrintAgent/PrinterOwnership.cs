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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Otro proceso lo tiene abierto, o el archivo lo creó otra cuenta sin permitir el acceso a esta: en los dos
            // casos este proceso no es dueño de la impresora.
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

    // Marca monotónica hasta la que el lease vigente cubre al sender: la duración menos la gracia, contada desde que se pidió
    // la toma o la renovación que lo dio (conservador).
    private long _coveredUntil;

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

        var requested = clock.GetTimestamp();
        Lease = await leases.AcquireAsync(printerId, ownerInstanceId, ownerHost, leaseDurationMs, leaseGraceMs, clock.UtcNow, ct);
        if (Lease is not null)
        {
            _coveredUntil = Covered(requested);
        }
        else
        {
            metrics.Increment(MetricNames.PrinterOwnerConflicts, ("printer", printerId), ("cause", "lease"));
            _logger.LogError("Impresora {Printer}: otra instancia tiene el lease vigente; esta no envía", printerId);
        }

        return Lease;
    }

    /// <summary>
    /// Renueva el lease cada tercio de su duración; si se pierde, cancela <paramref name="lost"/> para que el sender deje de
    /// reclamar. El sender se detiene antes de que el lease venza, con un margen igual a <c>LeaseGraceMs</c>
    /// (ARQUITECTURA_TECNICA, propiedad de la impresora): si hasta ese instante no hay una renovación confirmada (SQL Server no
    /// responde, falla o cuelga la llamada), el lease cuenta como perdido sin esperar a que la llamada termine. Los errores
    /// de una renovación se reintentan mientras el lease cubra al sender (revisiones de la Fase 5: A2 y la segunda, MEDIA 1).
    /// El ciclo de propiedad vuelve a tomar la impresora y ejecuta TX-R0 antes de enviar.
    /// </summary>
    public async Task RenewAsync(CancellationTokenSource lost, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !lost.IsCancellationRequested)
        {
            var wait = Min(TimeSpan.FromMilliseconds(leaseDurationMs / 3.0), Remaining());
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock.TimeProvider, ct);
            }

            if (!await RenewOnceAsync(ct))
            {
                metrics.Increment(MetricNames.PrinterOwnerConflicts, ("printer", printerId), ("cause", "lease_lost"));
                _logger.LogError("Impresora {Printer}: lease perdido; el sender se detiene", printerId);
                await lost.CancelAsync();
                return;
            }
        }
    }

    /// <summary>Una renovación, con reintentos mientras el lease cubra al sender. False: perdido (otro dueño o sin cobertura).</summary>
    private async Task<bool> RenewOnceAsync(CancellationToken ct)
    {
        while (true)
        {
            var remaining = Remaining();
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var requested = clock.GetTimestamp();
            // En el pool de hilos: parte de SqlClient es síncrona por debajo (BeginTransactionAsync), y con SQL colgado la llamada
            // no devolvería su tarea hasta el tiempo de espera del comando.
            var renewal = Task.Run(() => leases.AcquireAsync(printerId, ownerInstanceId, ownerHost, leaseDurationMs, leaseGraceMs, clock.UtcNow, ct), ct);
            if (await Task.WhenAny(renewal, Task.Delay(remaining, clock.TimeProvider, ct)) != renewal)
            {
                // La llamada sigue colgada: el sender se detiene igual; su resultado, si llega, ya no cuenta.
                _ = renewal.ContinueWith(t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _logger.LogError("Impresora {Printer}: la renovación del lease no respondió antes de que venza su cobertura", printerId);
                return false;
            }

            try
            {
                var renewed = await renewal;
                if (renewed is null || renewed.FencingToken != Lease?.FencingToken)
                {
                    return false;
                }

                Lease = renewed;
                _coveredUntil = Covered(requested);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Impresora {Printer}: no se pudo renovar el lease; se reintenta mientras cubra al sender", printerId);
                var pause = Min(TimeSpan.FromMilliseconds(200), Remaining());
                if (pause > TimeSpan.Zero)
                {
                    await Task.Delay(pause, clock.TimeProvider, ct);
                }
            }
        }
    }

    private long Covered(long requested) => clock.AddToTimestamp(requested, TimeSpan.FromMilliseconds(leaseDurationMs - leaseGraceMs));

    private TimeSpan Remaining() => clock.GetElapsedTime(clock.GetTimestamp(), _coveredUntil);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Al detener el proceso, libera el lease (lo deja vencido) para que la instancia siguiente no espere a que venza: sin
    /// esto, cada reinicio retrasaría el arranque la duración del lease más la gracia (revisión de la Fase 5, A1).
    /// </summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        if (Lease is { } lease)
        {
            await leases.ReleaseAsync(printerId, ownerInstanceId, lease.FencingToken, leaseGraceMs, clock.UtcNow, ct);
            Lease = null;
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
    private volatile bool _inService;
    private bool _requireOwnership;

    public bool Owned => _owned;

    /// <summary>
    /// Tomada por este proceso y con TX-R0 hecho desde que se tomó: la impresora puede enviar (LabelingReady de la impresora de línea,
    /// <c>LinePrinterReadiness</c>). En falso mientras se vuelve a tomar después de perder el lease, hasta repetir TX-R0.
    /// </summary>
    public bool InService => _inService;

    /// <summary>Cuántas veces se invocó el sender (para alertas y pruebas).</summary>
    public int SenderRuns { get; private set; }

    public async Task<bool> AcquireAsync(CancellationToken ct)
    {
        _owned = await ownership.TryAcquireAsync(ct) is not null;
        return _owned;
    }

    /// <summary>
    /// Etapa 2 del arranque para la impresora de línea: falla mientras otro proceso o instancia sea su dueño, para que el
    /// arranque la reintente (con una alerta por intento) y no siga a la suscripción ni a LabelingReady sin la impresora y
    /// sin TX-R0 (revisión de la Fase 5, A1).
    /// </summary>
    public async Task AcquireOrFailAsync(CancellationToken ct)
    {
        _requireOwnership = true;
        if (!await AcquireAsync(ct))
        {
            throw new InvalidOperationException($"La impresora {ownership.PrinterId} tiene otro dueño: el arranque no sigue sin ella.");
        }
    }

    /// <summary>
    /// TX-R0 de la impresora, solo si este proceso es su dueño. Antes renueva el lease: si la etapa se reintentó después de
    /// que venciera y otra instancia lo tomó, TX-R0 cerraría el intento en curso de esa instancia (revisión de la Fase 5).
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        if (!_owned)
        {
            // La impresora de línea no completa la etapa 3 sin ser dueña: el reintento de la etapa la volvería a dar por
            // terminada sin TX-R0 (segunda revisión de la Fase 5, BAJA 1).
            if (_requireOwnership)
            {
                throw new InvalidOperationException($"La impresora {ownership.PrinterId} no es de este proceso: TX-R0 no se ejecuta.");
            }

            return;
        }

        if (!await AcquireAsync(ct))
        {
            throw new InvalidOperationException($"La impresora {ownership.PrinterId} cambió de dueño antes de TX-R0.");
        }

        await recovery.RecoverAsync(ownership.PrinterId, ct);
        _inService = true;
    }

    /// <summary>Libera el lease al detener el proceso; un error solo se registra (el lease vencerá solo).</summary>
    public async Task ReleaseAsync()
    {
        _owned = false;
        _inService = false;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ownership.ReleaseAsync(limit.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impresora {Printer}: no se pudo liberar el lease; vencerá solo", ownership.PrinterId);
        }
    }

    public async Task RunAsync(Func<LeaseGrant, CancellationToken, Task> runSender, CancellationToken ct)
    {
        var verify = _owned;
        while (!ct.IsCancellationRequested)
        {
            if (verify)
            {
                // Entre la etapa 2 y el primer envío pasan las etapas 3 a 6 sin renovar: si el lease dejó de ser de este
                // proceso, se vuelve a tomar la impresora con TX-R0 (segunda revisión de la Fase 5, BAJA 1).
                verify = false;
                var token = ownership.Lease?.FencingToken;
                try
                {
                    _owned = await AcquireAsync(ct);
                    if (_owned && ownership.Lease?.FencingToken != token)
                    {
                        // El lease venció y se volvió a tomar con otro token: otra instancia pudo reclamar entretanto. TX-R0 otra vez.
                        _inService = false;
                        await recovery.RecoverAsync(ownership.PrinterId, ct);
                        _inService = true;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _owned = false;
                    _logger.LogError(ex, "Impresora {Printer}: no se pudo confirmar el lease antes de enviar", ownership.PrinterId);
                }

                if (!_owned)
                {
                    _inService = false;
                }
            }

            if (!_owned)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ownership.LeaseDurationMs), clock.TimeProvider, ct);
                try
                {
                    if (!await AcquireAsync(ct))
                    {
                        continue;
                    }

                    await recovery.RecoverAsync(ownership.PrinterId, ct);
                    _inService = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Por ejemplo, SQL Server no responde: sin propiedad ni TX-R0 no se envía nada, y se vuelve a intentar.
                    _owned = false;
                    _inService = false;
                    _logger.LogError(ex, "Impresora {Printer}: no se pudo volver a tomar la impresora; se reintenta", ownership.PrinterId);
                    continue;
                }
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
            catch (Exception ex) when (ex is not OperationCanceledException and not SenderHaltedException)
            {
                // Un error fuera de la escritura (por ejemplo, SQL al preparar el sender) no detiene el EdgeHost: se registra y
                // la vuelta siguiente vuelve a tomar la impresora, con TX-R0 (segunda revisión de la Fase 5, MEDIA 2).
                _logger.LogError(ex, "Impresora {Printer}: el sender terminó con un error; se vuelve a tomar la impresora", ownership.PrinterId);
            }
            finally
            {
                // Siempre, también si el sender se detuvo: la renovación no sigue después (segunda revisión, BAJA 2).
                _owned = false;
                _inService = false;
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
}

