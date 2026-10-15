using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.PrintAgent;

// Host de fallas para T07: ejecuta el sender real de una impresora para un job y se detiene en el punto de
// falla pedido, avisando por stdout ("AT <punto>"), para que la prueba mate el proceso (equivalente a kill -9:
// TerminateProcess en Windows, SIGKILL en Linux). Solo para pruebas; nunca se conecta a equipo real.
//
// Uso: CartonPrinting.FaultHost <cadena de conexión> <puerto impresora> <PrinterId> <OwnerInstanceId> <LeaseToken> <PrintJobId> <punto>
if (args.Length != 7)
{
    Console.Error.WriteLine("Uso: CartonPrinting.FaultHost <conexión> <puerto> <printerId> <owner> <leaseToken> <printJobId> <punto>");
    return 2;
}

var connection = args[0] + ";Application Name=CartonPrinting.FaultHost";
var port = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var printerId = args[2];
var owner = args[3];
var token = long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
var jobId = Guid.Parse(args[5]);
var killAt = Enum.Parse<SenderPoint>(args[6]);

var clock = new EdgeClock(TimeProvider.System);
using var metrics = new CartonMetrics();
var db = new SqlDb(new DatabaseOptions { ConnectionString = connection });
var claims = new ClaimService(db, new TxbClaim(db), metrics, clock, new ClockGuard(clock));
await using var transport = new TcpPrinterTransport(new PrinterTransportOptions { Port = port, AcceptanceTimeoutMs = 2_000 }, TimeProvider.System);
var sender = new PrinterSenderService(printerId, db, claims, new TxcWriteResult(db), new StationConfigStore(db),
    new LeaseGrant(printerId, owner, token, DateTime.UtcNow.AddMinutes(10)), transport, clock, metrics, new StopAt(killAt));

var result = await sender.SendAsync(jobId, CancellationToken.None);
Console.Out.WriteLine($"DONE {result.Write?.AttemptOutcome ?? result.Claim.Rejection.ToString()}");
Console.Out.Flush();
return 0;

/// <summary>Al llegar al punto pedido avisa y espera a que lo maten.</summary>
internal sealed class StopAt(SenderPoint point) : ISenderFaults
{
    public async Task OnPointAsync(SenderPoint reached, Guid attemptId, CancellationToken ct)
    {
        if (reached != point)
        {
            return;
        }

        await Console.Out.WriteLineAsync($"AT {reached}");
        await Console.Out.FlushAsync(ct);
        await Task.Delay(Timeout.Infinite, ct);
    }
}
