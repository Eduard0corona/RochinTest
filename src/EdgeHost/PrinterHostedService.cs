using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.PrintAgent;

namespace CartonPrinting.EdgeHost;

/// <summary>Configuración del sender de la impresora de línea (sección "Printer").</summary>
public sealed class PrinterHostOptions
{
    public string PrinterId { get; set; } = string.Empty;

    public PrinterTransportOptions Transport { get; set; } = new();

    public int LeaseDurationMs { get; set; } = 10_000;

    public int LeaseGraceMs { get; set; } = 2_000;

    public int PollIntervalMs { get; set; } = 50;
}

/// <summary>
/// Sender único de la impresora de línea en el EdgeHost (Fase 4): toma el lease, ejecuta la recuperación de
/// arranque (TX-R0) antes de enviar nada y después envía en orden. El lock de sistema operativo, la renovación
/// del lease y la parada ante su pérdida llegan en la Fase 5 (propiedad de la impresora).
/// </summary>
public sealed class PrinterHostedService(
    PrinterHostOptions options, SqlDb db, EdgeClock clock, CartonMetrics metrics, ILoggerFactory loggers) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var logger = loggers.CreateLogger<PrinterHostedService>();
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        var leases = new PrinterLeaseStore(db);
        LeaseGrant? lease;
        while ((lease = await leases.AcquireAsync(options.PrinterId, owner, Environment.MachineName, options.LeaseDurationMs, options.LeaseGraceMs,
                   clock.UtcNow, stoppingToken)) is null)
        {
            logger.LogWarning("La impresora {Printer} tiene otro propietario vigente; este EdgeHost no envía", options.PrinterId);
            await Task.Delay(TimeSpan.FromMilliseconds(options.LeaseDurationMs), clock.TimeProvider, stoppingToken);
        }

        await new SenderRecovery(new Txr0Recovery(db), clock, metrics, loggers.CreateLogger<SenderRecovery>()).RecoverAsync(options.PrinterId, stoppingToken);

        await using var transport = new TcpPrinterTransport(options.Transport, clock.TimeProvider);
        var evidence = new EvidenceService(new TxdEvidence(db), db, clock, metrics);
        var sender = new PrinterSenderService(options.PrinterId, db, new ClaimService(db, new TxbClaim(db), metrics, clock), new TxcWriteResult(db),
            new StationConfigStore(db), lease, transport, clock, metrics, logger: loggers.CreateLogger<PrinterSenderService>(), evidence: evidence);
        await sender.RunAsync(TimeSpan.FromMilliseconds(options.PollIntervalMs), clock.TimeProvider, null, stoppingToken);
    }
}
