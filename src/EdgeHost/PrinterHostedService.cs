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
/// del lease y la parada ante su pérdida llegan en la Fase 5 (propiedad de la impresora). El claim comparte el
/// ClockGuard de la estación con el barrido (CLOCK_JUMP).
/// </summary>
public sealed class PrinterHostedService(
    PrinterHostOptions options, SqlDb db, EdgeClock clock, CartonMetrics metrics, ILoggerFactory loggers, IServiceProvider services) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Cualquier falla que detiene el host (el sender sin TX-C posible, SQL Server inalcanzable al arrancar) sale con un
            // código distinto de 0, para que el supervisor lo relance y el arranque siguiente ejecute TX-R0 (revisiones de la
            // Fase 4: B-3 y MEDIA-4).
            Environment.ExitCode = 70;
            throw;
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
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

        // Sin su fila de PrinterSendOrder, el sender no arranca (regla 13; ARQUITECTURA_TECNICA, arranque, paso 5).
        if (await new PrinterSendOrderStore(db).GetAsync(options.PrinterId, stoppingToken) is not { } order)
        {
            logger.LogCritical("La impresora {Printer} no tiene fila en PrinterSendOrder: su sender no arranca", options.PrinterId);
            return;
        }

        // La evidencia que quedó registrada sin aplicar (por ejemplo, durante un SENDING que TX-R0 acaba de cerrar) se aplica
        // en paralelo con el sender: no se espera, para que el primer envío no dependa del volumen de evidencia de las últimas
        // 24 h (tercera revisión de la Fase 4, MEDIA-3). Aplicar es idempotente y va bajo los locks de TX-D.
        var evidence = new EvidenceService(new TxdEvidence(db), db, clock, metrics);
        var reapply = Task.Run(async () =>
        {
            try
            {
                var applied = await evidence.ReapplyOpenAsync(order.SiteId, order.StationId, stoppingToken);
                logger.LogInformation("Arranque de {Printer}: {Applied} observaciones pendientes aplicadas", options.PrinterId, applied);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Arranque de {Printer}: no se pudo reaplicar la evidencia pendiente", options.PrinterId);
            }
        }, stoppingToken);

        await using var transport = new TcpPrinterTransport(options.Transport, clock.TimeProvider);
        var sender = new PrinterSenderService(options.PrinterId, db, new ClaimService(db, new TxbClaim(db), metrics, clock, services.GetService<ClockGuard>() ?? new ClockGuard(clock)), new TxcWriteResult(db),
            new StationConfigStore(db), lease, transport, clock, metrics, logger: loggers.CreateLogger<PrinterSenderService>(), evidence: evidence);
        try
        {
            await sender.RunAsync(TimeSpan.FromMilliseconds(options.PollIntervalMs), clock.TimeProvider, null, stoppingToken);
        }
        finally
        {
            await reapply.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }
}
