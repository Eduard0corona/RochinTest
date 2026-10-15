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
    private readonly SenderRecovery _recovery;
    private readonly ILogger _logger;
    private volatile bool _owned;

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
        _ownership = new PrinterOwnership(options.PrinterId, $"{Environment.MachineName}:{Environment.ProcessId}", Environment.MachineName,
            new PrinterLeaseStore(db), clock, metrics, options.LeaseDurationMs, options.LeaseGraceMs, loggers.CreateLogger<PrinterOwnership>());
        _recovery = new SenderRecovery(new Txr0Recovery(db), clock, metrics, loggers.CreateLogger<SenderRecovery>());

        startup.Add(StartupStage.Ownership, $"lock y lease de {options.PrinterId}", async ct => _owned = await _ownership.TryAcquireAsync(ct) is not null);
        startup.Add(StartupStage.SendingRecovered, $"TX-R0 de {options.PrinterId}", async ct =>
        {
            if (_owned)
            {
                await _recovery.RecoverAsync(options.PrinterId, ct);
            }
        });
        startup.Add(StartupStage.DeviceQueue, $"cola de {options.PrinterId}", _ =>
        {
            _logger.LogInformation("Impresora {Printer}: sin capacidad confirmada de vaciar la cola del dispositivo (OT-07); no se vacía", options.PrinterId);
            return Task.CompletedTask;
        });
    }

    /// <summary>Este proceso es dueño de la impresora (lock y lease).</summary>
    public bool Owned => _owned;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _startup.WhenReached(StartupStage.Swept).WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_owned)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.LeaseDurationMs), _clock.TimeProvider, stoppingToken);
                if (await _ownership.TryAcquireAsync(stoppingToken) is null)
                {
                    continue;
                }

                await _recovery.RecoverAsync(_options.PrinterId, stoppingToken);
                _owned = true;
            }

            using var lost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var renewal = _ownership.RenewAsync(lost, stoppingToken);
            try
            {
                await RunSenderAsync(_ownership.Lease!, lost.Token);
            }
            catch (OperationCanceledException) when (lost.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogError("Impresora {Printer}: lease perdido; el sender se detuvo y no envía hasta volver a tomarla", _options.PrinterId);
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

    private async Task RunSenderAsync(LeaseGrant lease, CancellationToken ct)
    {
        await using var transport = new TcpPrinterTransport(_options.Transport, _clock.TimeProvider);
        var evidence = new EvidenceService(new TxdEvidence(_db), _db, _clock, _metrics);
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
