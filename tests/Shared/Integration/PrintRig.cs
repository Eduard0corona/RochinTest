using CartonPrinting.Infrastructure.Time;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Evidence;
using CartonPrinting.Simulators.Printer;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.Testing;

/// <summary>
/// Impresora simulada por TCP, el sender real de la impresora de línea de la estación, el servicio de
/// evidencia y el simulador de evidencia (impresora, aplicador y escáner), sobre un <see cref="StationScenario"/>.
/// </summary>
public sealed class PrintRig : IAsyncDisposable
{
    private PrintRig(StationScenario s, PrinterSimulator printer, TcpPrinterTransport transport, PrinterSenderService sender, EvidenceService evidence,
        EvidenceSimulator evidenceSim)
    {
        Scenario = s;
        Printer = printer;
        Transport = transport;
        Sender = sender;
        Evidence = evidence;
        EvidenceSim = evidenceSim;
    }

    public StationScenario Scenario { get; }
    public PrinterSimulator Printer { get; }
    public TcpPrinterTransport Transport { get; }
    public PrinterSenderService Sender { get; }
    public EvidenceService Evidence { get; }
    public EvidenceSimulator EvidenceSim { get; }

    public static PrintRig Create(StationScenario s, PrinterCapabilities? capabilities = null, EvidenceSources? sources = null, ISenderFaults? faults = null,
        int acceptanceTimeoutMs = 500, EdgeClock? clock = null, CartonPrinting.Infrastructure.Signals.SqlOutage? sqlOutage = null)
    {
        var printer = PrinterSimulator.Start(capabilities);
        var transport = new TcpPrinterTransport(new PrinterTransportOptions
        {
            Port = printer.Port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = acceptanceTimeoutMs,
        }, s.Provider);
        var c = clock ?? s.Clock;
        var claims = clock is null ? s.Claims : new ClaimService(s.Sql, s.Txb, s.Metrics, c, new ClockGuard(c));
        var evidence = new EvidenceService(s.Txd, s.Sql, c, s.Metrics);
        var sender = new PrinterSenderService(s.PrinterId, s.Sql, claims, s.Txc, s.Stations, s.Lease, transport, c, s.Metrics, faults, evidence: evidence,
            sqlOutage: sqlOutage);
        var evidenceSim = new EvidenceSimulator(printer, StationScenario.SiteId, s.StationId, sources ?? new EvidenceSources(), (serial, ct) => JobBySerialAsync(s, serial, ct),
            (o, ct) => evidence.SubmitAsync(o, ct), () => c.UtcNow);
        return new PrintRig(s, printer, transport, sender, evidence, evidenceSim);
    }

    public static async Task<Guid?> JobBySerialAsync(StationScenario s, string serial, CancellationToken ct)
    {
        await using var conn = new SqlConnection(s.Db.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT PrintJobId FROM dbo.PrintJob WHERE LabelSerial = @serial", conn);
        cmd.Parameters.AddWithValue("@serial", serial);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>Envía el job y espera la evidencia que dispara la etiqueta impresa.</summary>
    public async Task<SendResult> SendAsync(Guid jobId, CancellationToken ct = default)
    {
        var result = await Sender.SendAsync(jobId, ct);
        await EvidenceSim.DrainAsync();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        EvidenceSim.Dispose();
        await Transport.DisposeAsync();
        await Printer.DisposeAsync();
    }
}
