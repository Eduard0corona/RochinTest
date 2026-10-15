using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using CartonPrinting.Simulators.Printer;
using CartonPrinting.Testing;

namespace CartonPrinting.FaultTests.PrintAgent;

/// <summary>
/// Regresiones de la revisión independiente de la Fase 4 contra SQL Server real y la impresora simulada: la comprobación
/// monotónica es lo último antes de escribir (A1), la evidencia se aplica en orden aunque llegue durante SENDING (A2), un
/// claim rechazado no gira en bucle (M1), una conexión ociosa cerrada se reabre (M2), un TX-C imposible detiene el sender
/// (M3) y una estación bloqueada después del claim no se envía (J06).
/// </summary>
public sealed class PrintAgentReviewTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f4rev", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TcpPrinterTransport Transport(PrinterSimulator printer, StationScenario s) => new(new PrinterTransportOptions
    {
        Port = printer.Port, ExpectsAcceptance = printer.Capabilities.ReportsAcceptance, AcceptanceTimeoutMs = 1_000,
    }, s.Provider);

    /// <summary>A1: el canal tarda en abrirse más que lo que queda de plazo; la comprobación monotónica, la última antes de escribir, lo detiene.</summary>
    [Fact]
    [Trait("Spec", "T29")]
    public async Task The_last_check_before_writing_is_the_monotonic_deadline()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var printer = PrinterSimulator.Start();
        await using var transport = new SlowConnect(Transport(printer, s), () => s.AdvanceMs(2_000));
        var sender = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, s.Txc, s.Stations, s.Lease, transport, s.Clock, s.Metrics);
        var job = await s.ReadyJobAsync(900, Ct);

        var result = await sender.SendAsync(job, Ct);
        Assert.False(result.WriteInvoked);
        Assert.Equal((AttemptOutcome.NotSent, JobStatus.Expired, JobReason.DeadlinePassed),
            (result.Write!.AttemptOutcome, result.Write.JobStatus, result.Write.JobReason));
        Assert.Equal(0L, printer.BytesReceived);
        Assert.Equal(1, s.Metrics.Total(MetricNames.PresendCheckRejected, ("cause", "deadline")));
    }

    /// <summary>
    /// A2: una lectura VERIFIED con el serial de otro job llega mientras A está en SENDING y queda registrada sin efecto. Si
    /// después llega APPLIED por el camino normal, antes de que el sender reaplique, se aplica primero la lectura (orden de
    /// ObservationId): UNKNOWN con SERIAL_MISMATCH, nunca CONFIRMED, y la alerta se cuenta. Si nadie reaplica (el proceso
    /// cayó), la reaplicación del arranque produce lo mismo.
    /// </summary>
    [Fact]
    [Trait("Spec", "T22")]
    public async Task A_contradicting_read_during_sending_is_applied_in_order_and_never_ends_confirmed()
    {
        var s = await StationScenario.CreateAsync(_db, r => r with { MinConfirmationLevel = EvidenceLevel.Applied }, Ct);
        var evidence = new EvidenceService(s.Txd, s.Sql, s.Clock, s.Metrics);
        var a = await s.ReadyJobAsync(910, Ct);
        var other = await s.ReadyJobAsync(911, Ct);
        var otherSerial = (await s.JobAsync(other, Ct)).LabelSerial;

        var claim = (await s.Claims.TryClaimAsync(a, s.Lease, Ct))!;
        Assert.Equal(EvidenceAction.Recorded, (await evidence.SubmitAsync(s.Verified(910, otherSerial), Ct)).Action);
        await s.TransmitAsync(claim.AttemptId, Ct);
        await evidence.SubmitAsync(s.Applied(910), Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), ((await s.JobAsync(a, Ct)).Status, (await s.JobAsync(a, Ct)).Reason));
        Assert.Equal(1, s.Metrics.Total(MetricNames.SerialMismatch));

        // Sin reaplicación después de TX-C: la del arranque la aplica.
        var c = await s.ReadyJobAsync(912, Ct);
        var claimC = (await s.Claims.TryClaimAsync(c, s.Lease, Ct))!;
        await evidence.SubmitAsync(s.Verified(912, otherSerial) with { SourceReference = "SCN-912-B" }, Ct);
        await s.TransmitAsync(claimC.AttemptId, Ct);
        Assert.Equal(JobStatus.Transmitted, (await s.JobAsync(c, Ct)).Status);
        await new EvidenceService(s.Txd, s.Sql, s.Clock, s.Metrics).ReapplyOpenAsync(StationScenario.SiteId, s.StationId, Ct);
        Assert.Equal((JobStatus.Unknown, JobReason.SerialMismatch), ((await s.JobAsync(c, Ct)).Status, (await s.JobAsync(c, Ct)).Reason));
    }

    /// <summary>M1: con la estación bloqueada, el claim se rechaza y el sender espera el intervalo de sondeo en lugar de girar.</summary>
    [Fact]
    public async Task A_rejected_claim_waits_for_the_poll_interval()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var rig = PrintRig.Create(s);
        await s.ReadyJobAsync(920, Ct);
        await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.IdentityCollision, "SYSTEM", null, s.Clock.UtcNow, Ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await rig.Sender.RunAsync(TimeSpan.FromMilliseconds(200), TimeProvider.System, null, stop.Token);
        }
        catch (OperationCanceledException)
        {
            // Fin de la ventana de observación.
        }

        Assert.InRange(s.Metrics.Total(MetricNames.ClaimRejected, ("cause", nameof(ClaimRejection.StationDisabled))), 1, 20);
        Assert.Equal(0L, rig.Printer.BytesReceived);
    }

    /// <summary>M2: la impresora cerró la conexión ociosa entre dos etiquetas; el sender la reabre antes de escribir y la segunda se transmite.</summary>
    [Fact]
    [Trait("Spec", "T05")]
    public async Task An_idle_connection_the_printer_closed_is_reopened_before_writing()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var rig = PrintRig.Create(s);
        var first = await s.ReadyJobAsync(930, Ct);
        Assert.Equal(AttemptOutcome.Transmitted, (await rig.SendAsync(first, Ct)).Write!.AttemptOutcome);

        rig.Printer.Mode = PrinterMode.RejectConnections;   // corta la conexión abierta
        rig.Printer.Mode = PrinterMode.Normal;
        await Task.Delay(200, Ct);
        var second = await s.ReadyJobAsync(931, Ct);
        var result = await rig.SendAsync(second, Ct);
        Assert.Equal(AttemptOutcome.Transmitted, result.Write!.AttemptOutcome);
        Assert.Equal(1, rig.Printer.TransmissionsOf((await s.JobAsync(second, Ct)).LabelSerial));
        Assert.Equal(0, s.Metrics.Total(MetricNames.PrinterUnknown, ("printer", s.PrinterId)));
    }

    /// <summary>
    /// M3: la etiqueta se escribe completa, pero SQL no responde a TX-C. Después de dos reintentos el sender se detiene (no
    /// sigue con la impresora ocupada en un bucle); el intento queda en SENDING para TX-R0 del arranque siguiente.
    /// </summary>
    [Fact]
    public async Task A_result_that_cannot_be_recorded_halts_the_sender()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var printer = PrinterSimulator.Start();
        await using var transport = Transport(printer, s);
        var unreachable = new TxcWriteResult(new SqlDb(new DatabaseOptions { ConnectionString = "Server=127.0.0.1,1;Connect Timeout=1;Encrypt=false" }));
        var sender = new PrinterSenderService(s.PrinterId, s.Sql, s.Claims, unreachable, s.Stations, s.Lease, transport, s.Clock, s.Metrics);
        var job = await s.ReadyJobAsync(940, Ct);
        await s.ReadyJobAsync(941, Ct);

        // El bucle del sender no se traga la detención: termina con la excepción y no sigue con la impresora ocupada.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<SenderHaltedException>(() => sender.RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, limit.Token));
        Assert.Equal(JobStatus.Sending, (await s.JobAsync(job, Ct)).Status);
        Assert.Equal(1, printer.TransmissionsOf((await s.JobAsync(job, Ct)).LabelSerial));
        Assert.Single(printer.Received);
    }

    /// <summary>J06: la estación se bloquea después del claim; la relectura antes de escribir lo detecta: NOT_SENT sin consumir intento, cero bytes.</summary>
    [Fact]
    [Trait("Spec", "T29")]
    public async Task A_station_blocked_after_the_claim_is_not_sent_and_does_not_consume_an_attempt()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct);
        await using var rig = PrintRig.Create(s, faults: new BlockAfterClaim(s));
        var job = await s.ReadyJobAsync(950, Ct);

        var result = await rig.SendAsync(job, Ct);
        Assert.False(result.WriteInvoked);
        Assert.Equal((AttemptOutcome.NotSent, JobStatus.RetryWait), (result.Write!.AttemptOutcome, result.Write.JobStatus));
        Assert.Equal(0, (await s.JobAsync(job, Ct)).AutoAttemptCount);
        Assert.Equal(0L, rig.Printer.BytesReceived);
        Assert.Equal(1, s.Metrics.Total(MetricNames.PresendCheckRejected, ("cause", "station")));
    }

    private sealed class BlockAfterClaim(StationScenario s) : ISenderFaults
    {
        public async Task OnPointAsync(SenderPoint point, Guid attemptId, CancellationToken ct)
        {
            if (point == SenderPoint.AfterClaimCommit)
            {
                await s.Stations.BlockAsync(StationScenario.SiteId, s.StationId, BlockReason.IdentityCollision, "SYSTEM", null, s.Clock.UtcNow, ct);
            }
        }
    }

    /// <summary>Transporte cuya conexión "tarda" lo que avanza el reloj de prueba.</summary>
    private sealed class SlowConnect(IPrinterTransport inner, Action elapse) : IPrinterTransport
    {
        public bool Connected => inner.Connected;

        public async Task ConnectAsync(CancellationToken ct)
        {
            elapse();
            await inner.ConnectAsync(ct);
        }

        public Task<DateTime?> SendAsync(string payload, Func<Task>? midWrite, CancellationToken ct) => inner.SendAsync(payload, midWrite, ct);

        public void Reset() => inner.Reset();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
