using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.PrintAgent;
using CartonPrinting.Recovery;

namespace CartonPrinting.EdgeHost;

/// <summary>Configuración de una impresora (secciones "Printer" y "ManualPrinter").</summary>
public sealed class PrinterHostOptions
{
    public string PrinterId { get; set; } = string.Empty;

    public PrinterTransportOptions Transport { get; set; } = new();

    public int LeaseDurationMs { get; set; } = 10_000;

    public int LeaseGraceMs { get; set; } = 2_000;

    public int PollIntervalMs { get; set; } = 50;
}

/// <summary>Impresora de línea (envíos ordinarios) o manual (reimpresiones excepcionales autorizadas).</summary>
public enum PrinterRole
{
    Line,
    Manual,
}

/// <summary>
/// Dueño y sender único de una impresora en el EdgeHost (I11, ADR 0010). En el arranque ordenado toma el lock de
/// sistema operativo y el lease (etapa 2), ejecuta TX-R0 (etapa 3) y, sin una capacidad confirmada de vaciar la
/// cola del dispositivo (OT-07), lo registra (etapa 4). Envía solo después del barrido inicial (etapa 6) y solo
/// mientras tiene el lock y el lease: sin ellos, cero envíos y una alerta por intento
/// (<c>printer_owner_conflicts_total</c>); si el lease se pierde, el sender se detiene y vuelve a intentar tomar la
/// impresora, con TX-R0 otra vez antes de enviar. La impresora de línea comparte el ClockGuard de la estación con
/// el barrido; la manual ejecuta TX-X por la misma conexión única.
/// </summary>
public sealed class PrinterHostedService : BackgroundService
{
    private readonly PrinterRole _role;
    private readonly PrinterHostOptions _options;
    private readonly SqlDb _db;
    private readonly EdgeClock _clock;
    private readonly CartonMetrics _metrics;
    private readonly ILoggerFactory _loggers;
    private readonly StartupCoordinator _startup;
    private readonly ClockGuard _clockGuard;
    private readonly PrinterOwnership _ownership;
    private readonly OwnedPrinterRunner _runner;
    private readonly ILogger _logger;

    public PrinterHostedService(
        PrinterRole role, PrinterHostOptions options, SqlDb db, EdgeClock clock, CartonMetrics metrics, ILoggerFactory loggers, StartupCoordinator startup,
        ClockGuard? stationClockGuard = null)
    {
        _role = role;
        _options = options;
        _db = db;
        _clock = clock;
        _metrics = metrics;
        _loggers = loggers;
        _startup = startup;
        _clockGuard = stationClockGuard ?? new ClockGuard(clock);
        _logger = loggers.CreateLogger<PrinterHostedService>();
        // Único por arranque: dos contenedores con el mismo nombre de host y el mismo PID no son el mismo dueño.
        _ownership = new PrinterOwnership(options.PrinterId, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}", Environment.MachineName,
            new PrinterLeaseStore(db), clock, metrics, options.LeaseDurationMs, options.LeaseGraceMs, loggers.CreateLogger<PrinterOwnership>());
        _runner = new OwnedPrinterRunner(_ownership, new SenderRecovery(new Txr0Recovery(db), clock, metrics, loggers.CreateLogger<SenderRecovery>()),
            clock, loggers.CreateLogger<OwnedPrinterRunner>());

        // Sin la impresora de línea el arranque no sigue (se reintenta); la manual no detiene la estación: la toma el ciclo
        // de propiedad, con TX-R0, antes de su primera reimpresión.
        startup.Add(StartupStage.Ownership, $"lock y lease de {options.PrinterId}",
            role == PrinterRole.Line ? _runner.AcquireOrFailAsync : _runner.AcquireAsync);
        startup.Add(StartupStage.SendingRecovered, $"TX-R0 de {options.PrinterId}", _runner.RecoverAsync);
        startup.Add(StartupStage.DeviceQueue, $"cola de {options.PrinterId}", _ =>
        {
            _logger.LogInformation("Impresora {Printer}: sin capacidad confirmada de vaciar la cola del dispositivo (OT-07); no se vacía", options.PrinterId);
            return Task.CompletedTask;
        });
    }

    /// <summary>Este proceso es dueño de la impresora (lock y lease).</summary>
    public bool Owned => _runner.Owned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _startup.WhenReached(StartupStage.Swept).WaitAsync(stoppingToken);
            await _runner.RunAsync(RunSenderAsync, stoppingToken);
        }
        catch (SenderHaltedException)
        {
            // El host se detiene con un código distinto de 0, para que el supervisor lo relance y el arranque siguiente
            // ejecute TX-R0 (segunda revisión de la Fase 4, B-3).
            Environment.ExitCode = 70;
            throw;
        }
        finally
        {
            // Ya no hay envío en curso: el lease queda libre para la instancia siguiente.
            await _runner.ReleaseAsync();
        }
    }

    private async Task RunSenderAsync(LeaseGrant lease, CancellationToken ct)
    {
        // Se ejecuta después de tomar la impresora y de TX-R0. Sin su fila de PrinterSendOrder, el sender de línea no
        // arranca (regla 13): se alerta y el ciclo de propiedad lo vuelve a intentar después de la duración del lease.
        if (_role == PrinterRole.Line && await new PrinterSendOrderStore(_db).GetAsync(_options.PrinterId, ct) is null)
        {
            _logger.LogCritical("La impresora {Printer} no tiene fila en PrinterSendOrder: su sender no arranca", _options.PrinterId);
            return;
        }

        // La evidencia que quedó registrada sin aplicar (por ejemplo, durante un SENDING que TX-R0 acaba de cerrar) se
        // aplica ahora, en orden.
        var evidence = new EvidenceService(new TxdEvidence(_db), _db, _clock, _metrics);
        await evidence.ReapplyOpenAsync(_options.PrinterId, ct);
        await using var transport = new TcpPrinterTransport(_options.Transport, _clock.TimeProvider);
        var poll = TimeSpan.FromMilliseconds(_options.PollIntervalMs);
        if (_role == PrinterRole.Line)
        {
            var sender = new PrinterSenderService(_options.PrinterId, _db, new ClaimService(_db, new TxbClaim(_db), _metrics, _clock, _clockGuard),
                new TxcWriteResult(_db), new StationConfigStore(_db), lease, transport, _clock, _metrics,
                logger: _loggers.CreateLogger<PrinterSenderService>(), evidence: evidence);
            await sender.RunAsync(poll, _clock.TimeProvider, null, ct);
        }
        else
        {
            var sender = new ManualReprintSender(_options.PrinterId, _db, new TxxReprint(_db), new TxcWriteResult(_db), lease, transport, _clock, _metrics,
                evidence: evidence, logger: _loggers.CreateLogger<ManualReprintSender>());
            await sender.RunAsync(poll, _clock.TimeProvider, ct);
        }
    }

    public override void Dispose()
    {
        _ownership.Dispose();
        base.Dispose();
    }
}
