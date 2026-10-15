using System.Security.Cryptography;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.Orchestration;
using CartonPrinting.PrintAgent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace CartonPrinting.Testing;

/// <summary>
/// Estación de prueba sobre una base real: revisión activa, época, snapshot ERP, orden de envío y
/// lease de la impresora de línea y de la manual. Cada estado se construye por las transacciones
/// (TX-A a TX-X), nunca escribiendo Status a mano. Cada escenario usa su propia estación e
/// impresoras, así varias pruebas comparten una base sin interferir.
/// </summary>
public sealed class StationScenario
{
    private static int _counter;

    public const string SiteId = "PLANT-01";
    public const string WorkOrder = "WO-2026-4815";
    public static readonly DateTime Start = new(2026, 10, 12, 18, 4, 11, 200, DateTimeKind.Utc);

    private StationScenario(TestDatabase db, string stationId, TimeProvider time)
    {
        Db = db;
        StationId = stationId;
        Provider = time;
        Clock = new EdgeClock(time);
        Sql = new SqlDb(new DatabaseOptions { ConnectionString = db.AppConnectionString });
        Metrics = new CartonMetrics();
        Txa = new TxaRegisterDetection(Sql);
        Txb = new TxbClaim(Sql);
        Txc = new TxcWriteResult(Sql);
        Txd = new TxdEvidence(Sql);
        Txe = new TxeErpArrival(Sql);
        Txs = new TxsSweep(Sql);
        Txm = new TxmManualActions(Sql);
        Txx = new TxxReprint(Sql);
        Txr0 = new Txr0Recovery(Sql);
        Stations = new StationConfigStore(Sql);
        Epochs = new PlcEpochStore(Sql);
        Erp = new ErpCacheStore(Sql);
        SendOrder = new PrinterSendOrderStore(Sql);
        Leases = new PrinterLeaseStore(Sql);
        Serials = new LabelSerialSource(Sql);
        Registration = new RegistrationService(Txa, Erp, Serials, Clock, Metrics);
        Claims = new ClaimService(Sql, Txb, Metrics, Clock, new ClockGuard(Clock));
    }

    public TestDatabase Db { get; }
    public string StationId { get; }
    public string PrinterId => $"PRN-{StationId}";
    public string ManualPrinterId => $"PRN-MAN-{StationId}";
    public string OwnerInstanceId { get; } = "edge-test-" + Guid.NewGuid().ToString("N")[..8];
    /// <summary>Reloj manual del escenario; las pruebas con reloj real (ERP con latencia real) no lo tienen.</summary>
    public FakeTimeProvider Time => Provider as FakeTimeProvider ?? throw new InvalidOperationException("El escenario usa el reloj real.");

    public TimeProvider Provider { get; }
    public EdgeClock Clock { get; }
    public SqlDb Sql { get; }
    public CartonMetrics Metrics { get; }
    public TxaRegisterDetection Txa { get; }
    public TxbClaim Txb { get; }
    public TxcWriteResult Txc { get; }
    public TxdEvidence Txd { get; }
    public TxeErpArrival Txe { get; }
    public TxsSweep Txs { get; }
    public TxmManualActions Txm { get; }
    public TxxReprint Txx { get; }
    public Txr0Recovery Txr0 { get; }
    public StationConfigStore Stations { get; }
    public PlcEpochStore Epochs { get; }
    public ErpCacheStore Erp { get; }
    public PrinterSendOrderStore SendOrder { get; }
    public PrinterLeaseStore Leases { get; }
    public LabelSerialSource Serials { get; }
    public RegistrationService Registration { get; }
    public ClaimService Claims { get; }
    public StationRevision Revision { get; private set; } = null!;
    public StationSettings Settings { get; private set; } = null!;
    public LeaseGrant Lease { get; private set; } = null!;
    public LeaseGrant ManualLease { get; private set; } = null!;
    public long Epoch { get; private set; } = 42;
    public string SerialPrefix { get; private set; } = string.Empty;

    public static StationRevision DefaultRevision(string stationId) => new()
    {
        SiteId = SiteId,
        StationId = stationId,
        RevisionNumber = 7,
        PlcEndpointId = "opc.tcp://sim/" + stationId,
        PrinterId = $"PRN-{stationId}",
        ManualPrinterId = $"PRN-MAN-{stationId}",
        LabelType = "CARTON_GS1",
        TemplateRevision = "TPL-7",
        DeadlineReference = DeadlineReference.PlcTimestamp,
        TransitBudgetMs = 1500,
        DeviceLeadTimeMs = 400,
        SendMarginMs = 100,
        ClockSkewToleranceMs = 50,
        MaxUpstreamLatencyMs = null,
        ClockJumpToleranceMs = 200,
        MinCycleIntervalMs = null,
        MaxAutoAttempts = 3,
        RetryBackoffMs = 100,
        MinConfirmationLevel = EvidenceLevel.Printed,
        EvidenceWindowMs = 2000,
        EpochChangePolicy = "AUTO_INCREMENT_BY_ONE",
        CartonIdentityMode = "CYCLE_ONLY",
        GapGraceMs = 5000,
        PhysicalGateMode = "NONE",
        GateFreshnessMs = null,
        CreatedBy = "ingeniero.ot",
        ApprovedBy = "supervisor.calidad",
        ChangeReason = "Revisión de prueba",
    };

    /// <summary>Crea una estación nueva con su revisión (por defecto PLC_TIMESTAMP y nivel PRINTED).</summary>
    public static async Task<StationScenario> CreateAsync(TestDatabase db, Func<StationRevision, StationRevision>? configure = null, CancellationToken ct = default,
        TimeProvider? time = null)
    {
        var n = Interlocked.Increment(ref _counter);
        var stationId = $"L{n:D3}-{Guid.NewGuid().ToString("N")[..4]}";
        var s = new StationScenario(db, stationId, time ?? new FakeTimeProvider(Start.AddMinutes(n)));
        s.SerialPrefix = $"P01L{n:D3}";
        var revision = DefaultRevision(stationId);
        s.Revision = await s.Stations.ActivateRevisionAsync(configure is null ? revision : configure(revision), ct);
        s.Settings = new StationSettings(SiteId, stationId, s.SerialPrefix, $"ns=2;s={stationId}.CartonCycleRecord");
        var now = s.Clock.UtcNow;
        await s.Epochs.InsertAsync(new PlcEpoch(SiteId, stationId, s.Epoch, "PLC", PlcEpochStore.Initial, 1, now.AddHours(-1), "SYSTEM",
            s.Revision.ConfigRevisionId, null), ct);
        await s.AddSnapshotAsync("R1", "SKU-10245", "LOT-20261012-A", ct);
        await s.SendOrder.RegisterAsync(s.PrinterId, SiteId, stationId, now, ct);
        s.Lease = (await s.Leases.AcquireAsync(s.PrinterId, s.OwnerInstanceId, "host-test", 600_000, 2_000, now, ct))!;
        s.ManualLease = (await s.Leases.AcquireAsync(s.ManualPrinterId, s.OwnerInstanceId, "host-test", 600_000, 2_000, now, ct))!;
        return s;
    }

    public Task<bool> AddSnapshotAsync(string revision, string product, string lot, CancellationToken ct = default, string? workOrder = null)
    {
        var now = Clock.UtcNow;
        return Erp.UpsertAsync(new ErpSnapshot(workOrder ?? WorkOrder, revision, product, lot, null, now.AddHours(-1), now.AddHours(8), now,
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(revision + product + lot))), ct);
    }

    public void Advance(TimeSpan delta) => Time.Advance(delta);

    public void AdvanceMs(double ms) => Time.Advance(TimeSpan.FromMilliseconds(ms));

    /// <summary>Registro latched del ciclo, detectado hace 17 ms y recibido ahora.</summary>
    public CycleRecord Cycle(ulong cycleId, string transport = Transport.Subscription, string? workOrder = WorkOrder, DateTime? detectionTimestamp = null,
        string? cartonId = null, bool overflow = false, long? epoch = null)
    {
        var now = Clock.UtcNow;
        return new CycleRecord
        {
            SiteId = SiteId,
            StationId = StationId,
            PlcEpoch = epoch ?? Epoch,
            CycleId = cycleId,
            DetectionTimestamp = detectionTimestamp ?? now.AddMilliseconds(-17),
            WorkOrderId = workOrder,
            CartonId = cartonId,
            Transport = transport,
            QueueOverflow = overflow,
            ReceivedAt = now,
            ReceivedTimestamp = Clock.GetTimestamp(),
            OpcNodeId = Settings.OpcNodeId,
            OpcSourceTimestamp = detectionTimestamp ?? now.AddMilliseconds(-17),
        };
    }

    public Task<RegistrationResult> RegisterAsync(CycleRecord record, SequenceDecision? sequence = null, CancellationToken ct = default) =>
        Registration.RegisterAsync(record, Settings, Revision, sequence ?? SequenceDecision.InOrder, ct);

    /// <summary>Un job READY recién creado por TX-A.</summary>
    public async Task<Guid> ReadyJobAsync(ulong cycleId, CancellationToken ct = default)
    {
        var result = await RegisterAsync(Cycle(cycleId), ct: ct);
        if (result.FinalStatus != DetectionStatus.JobCreated || result.PrintJobId is null)
        {
            throw new InvalidOperationException($"Se esperaba JOB_CREATED y se obtuvo {result.FinalStatus}/{result.FinalReason}.");
        }

        return result.PrintJobId.Value;
    }

    /// <summary>Job en SENDING con su intento abierto (TX-B).</summary>
    public async Task<(Guid JobId, Guid AttemptId)> SendingJobAsync(ulong cycleId, CancellationToken ct = default)
    {
        var job = await ReadyJobAsync(cycleId, ct);
        var claim = await Claims.TryClaimAsync(job, Lease, ct) ?? throw new InvalidOperationException("Job inexistente.");
        if (!claim.Claimed)
        {
            throw new InvalidOperationException($"Claim rechazado: {claim.Rejection}.");
        }

        return (job, claim.AttemptId);
    }

    /// <summary>Job TRANSMITTED (A03, J05) con la ventana de evidencia de la revisión.</summary>
    public async Task<(Guid JobId, Guid AttemptId)> TransmittedJobAsync(ulong cycleId, CancellationToken ct = default)
    {
        var (job, attempt) = await SendingJobAsync(cycleId, ct);
        await TransmitAsync(attempt, ct);
        return (job, attempt);
    }

    public Task<WriteOutcome> TransmitAsync(Guid attemptId, CancellationToken ct = default)
    {
        var now = Clock.UtcNow;
        return Txc.TransmittedAsync(attemptId, now, now.AddMilliseconds(1), now, null, now.AddMilliseconds(1), ct);
    }

    /// <summary>Job UNKNOWN con WRITE_INTERRUPTED (A04, J09).</summary>
    public async Task<(Guid JobId, Guid AttemptId)> UnknownJobAsync(ulong cycleId, CancellationToken ct = default)
    {
        var (job, attempt) = await SendingJobAsync(cycleId, ct);
        await Txc.UnknownAsync(attempt, Clock.UtcNow, "Corte del canal a mitad de la escritura", Clock.UtcNow, ct);
        return (job, attempt);
    }

    public Task<long> ObserveAsync(ObservationInput o, CancellationToken ct = default) => Txd.RecordAsync(o, Clock.UtcNow, ct);

    /// <summary>Evidencia PRINTED del job (finalización reportada por la impresora).</summary>
    public ObservationInput Printed(Guid jobId, Guid? attemptId = null) =>
        new(SiteId, StationId, null, null, jobId, attemptId, EvidenceType.PrinterJobCompletion, EvidenceLevel.Printed, CorrelatedTo.Job, null,
            "PRN-JOB-" + jobId.ToString("N")[..8], Clock.UtcNow, "SYSTEM");

    /// <summary>Lectura VERIFIED del ciclo con el serial dado.</summary>
    public ObservationInput Verified(ulong cycleId, string serial, Guid? attemptId = null, string type = EvidenceType.BarcodeVerification,
        string recordedBy = "SYSTEM") =>
        new(SiteId, StationId, Epoch, cycleId, null, attemptId, type, EvidenceLevel.Verified, CorrelatedTo.Cycle, serial,
            "SCN-" + StationId + "-" + cycleId, Clock.UtcNow, recordedBy);

    public ObservationInput Applied(ulong cycleId) =>
        new(SiteId, StationId, Epoch, cycleId, null, null, EvidenceType.PlcApplicatorConfirmation, EvidenceLevel.Applied, CorrelatedTo.Cycle, null,
            "APL-" + StationId + "-" + cycleId, Clock.UtcNow, "SYSTEM");

    public async Task<EvidenceOutcome> EvidenceAsync(ObservationInput o, CancellationToken ct = default)
    {
        var id = await ObserveAsync(o, ct);
        return await Txd.ApplyAsync(id, Clock.UtcNow, ct);
    }

    public async Task<JobRecord> JobAsync(Guid jobId, CancellationToken ct = default) =>
        (await Sql.ReadAsync(conn => JobQueries.GetJobAsync(conn, null, jobId, ct), ct))!;

    public Task<IReadOnlyList<AttemptRecord>> AttemptsAsync(Guid jobId, CancellationToken ct = default) =>
        Sql.ReadAsync(conn => JobQueries.GetAttemptsAsync(conn, null, jobId, ct), ct);

    public Task<DetectionRecord?> DetectionAsync(ulong cycleId, CancellationToken ct = default) => DetectionInEpochAsync(Epoch, cycleId, ct);

    public Task<DetectionRecord?> DetectionInEpochAsync(long epoch, ulong cycleId, CancellationToken ct = default) =>
        Sql.ReadAsync(conn => DetectionQueries.GetByIdentityAsync(conn, null, new CycleIdentity(SiteId, StationId, epoch, cycleId), ct), ct);

    /// <summary>Autorización de reimpresión aprobada por dos personas hacia la impresora manual.</summary>
    public Task<long> ApproveReprintAsync(Guid jobId, string priorStatus, string reason, string risk, CancellationToken ct = default,
        string? targetPrinter = null, int evidenceWindowMs = 60_000, TimeSpan? expiresIn = null)
    {
        var now = Clock.UtcNow;
        return Txm.ApproveReprintAsync(new ReprintRequest(jobId, priorStatus, reason, risk, "CAJA-RETENIDA-" + jobId.ToString("N")[..6],
            "Inspección visual de la caja retenida", targetPrinter ?? ManualPrinterId, true, evidenceWindowMs, "operador.lopez", now.AddSeconds(-5),
            "supervisor.ramos", now, now.Add(expiresIn ?? TimeSpan.FromMinutes(30))), ct);
    }

    /// <summary>Ejecuta TX-X con el lease de la impresora manual.</summary>
    public Task<ReprintExecution> ExecuteReprintAsync(long authorizationId, CancellationToken ct = default) =>
        Txx.ExecuteAsync(authorizationId, ManualPrinterId, OwnerInstanceId, ManualLease.FencingToken, Guid.NewGuid(), Clock.UtcNow, ct);

    // ----------------------------------------------------------------- SQL directo (como el login de la aplicación)

    public async Task<int> ExecAsync(string sql, Action<SqlCommand>? bind = null, string? transitionKind = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(Db.AppConnectionString);
        await conn.OpenAsync(ct);
        var text = transitionKind is null
            ? sql
            : $"EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = '{transitionKind}';\n{sql}";
        await using var cmd = new SqlCommand(text, conn);
        bind?.Invoke(cmd);
        try
        {
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (transitionKind is not null && conn.State == System.Data.ConnectionState.Open)
            {
                await using var clear = new SqlCommand("EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;", conn);
                await clear.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>Ejecuta y devuelve la SqlException esperada.</summary>
    public async Task<SqlException> FailsAsync(string sql, Action<SqlCommand>? bind = null, string? transitionKind = null, CancellationToken ct = default)
    {
        try
        {
            await ExecAsync(sql, bind, transitionKind, ct);
        }
        catch (SqlException ex)
        {
            return ex;
        }

        throw new Xunit.Sdk.XunitException($"Se esperaba un error de SQL Server y la sentencia se ejecutó:\n{sql}");
    }

    public async Task<T> ScalarAsync<T>(string sql, Action<SqlCommand>? bind = null, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(Db.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        bind?.Invoke(cmd);
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<int> CountAsync(string fromWhere, Action<SqlCommand>? bind = null, CancellationToken ct = default) =>
        ScalarAsync<int>("SELECT COUNT(*) " + fromWhere, bind, ct);

    /// <summary>Eventos del Outbox de esta estación, en orden de OutboxSequence.</summary>
    public async Task<IReadOnlyList<(string Type, string Payload)>> OutboxAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(Db.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT EventType, Payload FROM dbo.OutboxEvent
            WHERE JSON_VALUE(Payload, '$.stationId') = @station ORDER BY OutboxSequence
            """, conn);
        cmd.Parameters.AddWithValue("@station", StationId);
        var list = new List<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add((r.GetString(0), r.GetString(1)));
        }

        return list;
    }

    public async Task<IReadOnlyList<System.Text.Json.JsonElement>> ResultsAsync(Guid? jobId = null, CancellationToken ct = default)
    {
        var all = await OutboxAsync(ct);
        return all.Where(e => e.Type == "PrintJobResult")
            .Select(e => System.Text.Json.JsonDocument.Parse(e.Payload).RootElement)
            .Where(e => jobId is null || (e.TryGetProperty("printJobId", out var p) && p.GetGuid() == jobId))
            .ToList();
    }
}
