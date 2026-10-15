using System.Threading.Channels;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.PrintAgent;
using CartonPrinting.Infrastructure.Signals;
using CartonPrinting.Infrastructure.Transactions;
using CartonPrinting.OpcIngest;
using CartonPrinting.Orchestration;
using CartonPrinting.Simulators.Network;
using CartonPrinting.Simulators.Plc;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.FaultTests.Hardening;

/// <summary>
/// T19 · SQL Server caído, perfil mínimo. Un proxy TCP entre la ingesta y SQL Server simula la caída mientras el PLC simulado
/// publica cajas. Falla cerrada: ninguna detección, job ni resultado de esos ciclos, y LabelingReady (y su gauge) en falso desde
/// que la ingesta declara la caída hasta que recarga el estado al volver SQL. El PLC solo manda la caja siguiente cuando vuelve
/// LabelingReady; ese ciclo abre el hueco de la caída y el barrido lo cierra CLOSED_UNRECOVERED.
/// </summary>
[Collection(nameof(CartonPrinting.FaultTests.Hardening.BrokerOutageCollection))]
public sealed class SqlOutageTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await sql.CreateDatabaseAsync("f7sql", TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// El caso del catálogo: el proxy corta todas las conexiones y no acepta nuevas durante 30 s mientras pasan 25 cajas (una cada
    /// 1,2 s). Cada intento falla enseguida.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task Sql_down_while_25_boxes_pass_fails_closed_and_leaves_a_gap_of_25()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        await using var proxy = SqlProxy(s);
        var viaProxy = new SqlConnectionStringBuilder(s.Db.AppConnectionString)
        {
            DataSource = $"127.0.0.1,{proxy.Port}", ConnectTimeout = 2, Pooling = false, ConnectRetryCount = 0,
        };

        await using var h = await OpcHarness.StartAsync(s.StationId, new EdgeClock(TimeProvider.System));
        using var metrics = new CartonMetrics();
        using var host = new IngestHostedService(Options(s, h), new SqlDb(new DatabaseOptions { ConnectionString = viaProxy.ConnectionString }), s.Clock,
            metrics, NullLoggerFactory.Instance);
        await host.StartAsync(Ct);
        try
        {
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al arrancar");
            Publish(h, s, 500);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");

            // SQL cae 30 s mientras pasan 25 cajas (una cada 1,2 s).
            proxy.AcceptNew = false;
            proxy.SeverAll();
            var outage = DateTime.UtcNow;
            var readyDuringOutage = new List<(bool Node, double? Gauge)>();
            for (ulong cycle = 501; cycle <= 525; cycle++)
            {
                Publish(h, s, cycle);
                await Task.Delay(TimeSpan.FromMilliseconds(1_200), Ct);
                readyDuringOutage.Add((h.Server.Nodes.LabelingReady, metrics.Gauge(MetricNames.LabelingReady, ("station", s.StationId))));
            }

            var remaining = outage.AddSeconds(30) - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, Ct);
            }

            Assert.All(readyDuringOutage, r => Assert.Equal((false, 0d), (r.Node, r.Gauge)));
            Assert.True(host.Service.SqlUnavailable);

            // Vuelve SQL: la estación recarga su estado y levanta LabelingReady; recién entonces el PLC manda la caja siguiente.
            proxy.AcceptNew = true;
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al volver SQL");
            Publish(h, s, 526);
            await WaitAsync(async () => await s.DetectionAsync(526, Ct) is not null, "detección 526 al volver SQL");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        await AssertGapAsync(s, metrics, 501, 525);
    }

    /// <summary>
    /// Variante sin respuesta: SQL Server deja de contestar sin cortar las conexiones (el proxy congela el tráfico), con el pool de
    /// conexiones y los tiempos de espera por defecto, como en el EdgeHost. Pasan 20 cajas, una cada 300 ms, más rápido de lo que la
    /// ingesta agota sus intentos: quedan en el canal y algunas se procesan después de que SQL vuelve. Ninguna termina como
    /// detección: la falla cerrada la decide la recepción, no el momento en que se procesa.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task Sql_that_stops_answering_with_pooled_connections_turns_every_cycle_received_during_the_outage_into_the_gap()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        await using var proxy = SqlProxy(s);
        var viaProxy = new SqlConnectionStringBuilder(s.Db.AppConnectionString) { DataSource = $"127.0.0.1,{proxy.Port}" };

        await using var h = await OpcHarness.StartAsync(s.StationId, new EdgeClock(TimeProvider.System));
        using var metrics = new CartonMetrics();
        using var host = new IngestHostedService(Options(s, h), new SqlDb(new DatabaseOptions { ConnectionString = viaProxy.ConnectionString }), s.Clock,
            metrics, NullLoggerFactory.Instance);
        await host.StartAsync(Ct);
        try
        {
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al arrancar");
            Publish(h, s, 500);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");

            proxy.Freeze();
            var frozen = DateTime.UtcNow;
            var samples = new List<(TimeSpan At, bool Node, double? Gauge)>();
            for (ulong cycle = 501; cycle <= 520; cycle++)
            {
                Publish(h, s, cycle);
                await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
                samples.Add((DateTime.UtcNow - frozen, h.Server.Nodes.LabelingReady, metrics.Gauge(MetricNames.LabelingReady, ("station", s.StationId))));
            }

            while (DateTime.UtcNow - frozen < TimeSpan.FromSeconds(12))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
                samples.Add((DateTime.UtcNow - frozen, h.Server.Nodes.LabelingReady, metrics.Gauge(MetricNames.LabelingReady, ("station", s.StationId))));
            }

            // Tres intentos de hasta 1 s y dos esperas cortas: a los 5 s la caída está declarada y LabelingReady en falso.
            Assert.True(host.Service.SqlUnavailable, "La ingesta no declaró SQL no disponible.");
            Assert.All(samples.Where(x => x.At > TimeSpan.FromSeconds(5)), x => Assert.Equal((false, 0d), (x.Node, x.Gauge)));

            proxy.Resume();
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al volver SQL");
            Assert.False(host.Service.SqlUnavailable);
            Publish(h, s, 521);
            await WaitAsync(async () => await s.DetectionAsync(521, Ct) is not null, "detección 521 al volver SQL");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        await AssertGapAsync(s, metrics, 501, 520);
    }

    /// <summary>
    /// Un ciclo recibido durante la caída que el consumidor procesa recién después de que SQL vuelve (seguía en el canal) es parte
    /// del hueco: la falla cerrada la decide la recepción. La prueba controla el canal: las notificaciones de los ciclos 502 a 505,
    /// recibidas con SQL congelado, llegan al consumidor después de la recuperación. Un latido que lee la estación sin pasar por el
    /// proxy (las lecturas funcionan) mantiene LabelingReady en falso mientras la ingesta tiene la caída declarada.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task A_cycle_received_during_the_outage_and_processed_after_sql_returns_is_part_of_the_gap()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        await using var proxy = SqlProxy(s);
        var db = new SqlDb(new DatabaseOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(s.Db.AppConnectionString) { DataSource = $"127.0.0.1,{proxy.Port}" }.ConnectionString,
        });
        using var metrics = new CartonMetrics();
        var ingest = new StationIngestService(s.Settings,
            new RegistrationService(new TxaRegisterDetection(db), new ErpCacheStore(db), new LabelSerialSource(db), s.Clock, metrics),
            new StationConfigStore(db), new PlcEpochStore(db), new SequenceStore(db), new TxsSweep(db), new TxmManualActions(db), s.Clock, metrics,
            failClosed: new SqlFailClosedOptions { AttemptTimeout = TimeSpan.FromSeconds(1), RecoveryProbeInterval = TimeSpan.FromMilliseconds(200) });
        var sink = new IngestScenario.RecordingSink();
        var heartbeat = new LabelingReadyHeartbeat(s.Settings, s.Stations, sink, metrics, outage: ingest.Outage);
        await using var h = await OpcHarness.StartAsync(s.StationId, s.Clock);
        var channel = Channel.CreateUnbounded<RawNotification>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var consumer = ingest.RunAsync(channel.Reader, stop.Token);
        try
        {
            // La primera notificación del monitored item es el valor inicial (vacío): antes de ella, un ciclo publicado llegaría como READ.
            await h.NextAsync(TimeSpan.FromSeconds(60));
            await channel.Writer.WriteAsync(await ReceiveAsync(h, s, 500), Ct);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");
            Assert.True(await heartbeat.BeatAsync(Ct));

            proxy.Freeze();
            var duringOutage = new List<RawNotification>();
            for (ulong cycle = 501; cycle <= 505; cycle++)
            {
                duringOutage.Add(await ReceiveAsync(h, s, cycle));
            }

            // El 501 agota sus intentos: la ingesta declara la caída.
            await channel.Writer.WriteAsync(duringOutage[0], Ct);
            await WaitAsync(() => Task.FromResult(ingest.SqlUnavailable), "la ingesta declara la caída");
            Assert.False(await heartbeat.BeatAsync(Ct));
            proxy.Resume();
            await WaitAsync(() => Task.FromResult(!ingest.SqlUnavailable), "la ingesta recarga el estado al volver SQL");
            Assert.True(await heartbeat.BeatAsync(Ct));

            // SQL ya responde, pero 502 a 505 se recibieron durante la caída.
            foreach (var raw in duringOutage.Skip(1))
            {
                await channel.Writer.WriteAsync(raw, Ct);
            }

            await channel.Writer.WriteAsync(await ReceiveAsync(h, s, 506), Ct);
            await WaitAsync(async () => await s.DetectionAsync(506, Ct) is not null, "detección 506");
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        }

        await AssertGapAsync(s, metrics, 501, 505);
    }

    /// <summary>
    /// El PLC publica el ciclo y la prueba toma su notificación del cliente OPC UA del arnés. Con la máquina cargada el cliente puede
    /// reconectar y entregarla como READ o REPUBLISH: la regla de recepción no depende del transporte.
    /// </summary>
    private static async Task<RawNotification> ReceiveAsync(OpcHarness h, StationScenario s, ulong cycle)
    {
        Publish(h, s, cycle);
        var decoder = new CycleDecoder(s.Settings);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var raw = await h.Notifications.Reader.ReadAsync(wait.Token);
            if (decoder.Decode(raw).Record?.CycleId == cycle)
            {
                return raw;
            }
        }
    }

    /// <summary>
    /// Hasta dos reintentos cortos dentro del plazo de la caja (ESTRATEGIA_RECUPERACION §3; verificación final, BAJA-10): un corte de
    /// unos 150 ms hace fallar los dos primeros intentos de TX-A y el tercero registra el ciclo. No hay caída declarada ni hueco.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task A_short_cut_is_absorbed_by_the_retries_within_the_deadline()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        await using var proxy = SqlProxy(s);
        var db = new SqlDb(new DatabaseOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(s.Db.AppConnectionString)
            {
                DataSource = $"127.0.0.1,{proxy.Port}", ConnectTimeout = 2, Pooling = false, ConnectRetryCount = 0,
            }.ConnectionString,
        });
        using var metrics = new CartonMetrics();
        var ingest = new StationIngestService(s.Settings,
            new RegistrationService(new TxaRegisterDetection(db), new ErpCacheStore(db), new LabelSerialSource(db), s.Clock, metrics),
            new StationConfigStore(db), new PlcEpochStore(db), new SequenceStore(db), new TxsSweep(db), new TxmManualActions(db), s.Clock, metrics);
        await using var h = await OpcHarness.StartAsync(s.StationId, s.Clock);
        var channel = Channel.CreateUnbounded<RawNotification>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var consumer = ingest.RunAsync(channel.Reader, stop.Token);
        try
        {
            await h.NextAsync(TimeSpan.FromSeconds(60));
            await channel.Writer.WriteAsync(await ReceiveAsync(h, s, 500), Ct);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");

            var raw = await ReceiveAsync(h, s, 501);
            proxy.AcceptNew = false;
            await channel.Writer.WriteAsync(raw, Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);   // reintentos a los 100 ms y a los 300 ms
            proxy.AcceptNew = true;
            await WaitAsync(async () => await s.DetectionAsync(501, Ct) is not null, "detección 501 en el tercer intento");
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        }

        Assert.Equal(DetectionStatus.JobCreated, (await s.DetectionAsync(501, Ct))!.Status);
        Assert.True(metrics.Total(MetricNames.IngestSqlRetries, ("station", s.StationId)) >= 1, "el ciclo se registró sin ningún reintento");
        Assert.False(ingest.SqlUnavailable);
        Assert.DoesNotContain(metrics.Series(MetricNames.OpcRecordsRejected), e => e.Key.Contains("cause=SQL_UNAVAILABLE", StringComparison.Ordinal));
    }

    /// <summary>
    /// Escrituras bloqueadas y lecturas que responden (segunda revisión de la Fase 7, M-1). Otra sesión retiene el rango de
    /// UQ_CartonDetection_Identity donde TX-A insertaría los ciclos siguientes al 500. TX-A no termina, la ingesta declara la caída y
    /// LabelingReady baja. Las lecturas siguen respondiendo (RCSI), pero la vigilancia no levanta la caída mientras el rango siga
    /// retenido, porque prueba ese rango. Al soltarlo, la caída se levanta y el ciclo siguiente abre el hueco.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task Blocked_writes_keep_the_outage_declared_although_reads_answer()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        await using var h = await OpcHarness.StartAsync(s.StationId, new EdgeClock(TimeProvider.System));
        using var metrics = new CartonMetrics();
        using var host = new IngestHostedService(Options(s, h), s.Sql, s.Clock, metrics, NullLoggerFactory.Instance);
        await host.StartAsync(Ct);
        try
        {
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al arrancar");
            Publish(h, s, 500);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");

            await using var holder = new SqlConnection(s.Db.AppConnectionString);
            await holder.OpenAsync(Ct);
            var tx = holder.BeginTransaction();
            await using (var hold = new SqlCommand("""
                SELECT TOP (1) CycleId FROM dbo.CartonDetection WITH (UPDLOCK, HOLDLOCK, INDEX (UQ_CartonDetection_Identity))
                WHERE SiteId = @site AND StationId = @st AND PlcEpoch = @epoch AND CycleId > 500 ORDER BY CycleId;
                """, holder, tx))
            {
                hold.Parameters.AddWithValue("@site", StationScenario.SiteId);
                hold.Parameters.AddWithValue("@st", s.StationId);
                hold.Parameters.AddWithValue("@epoch", s.Epoch);
                await hold.ExecuteScalarAsync(Ct);
            }

            var held = DateTime.UtcNow;
            var samples = new List<(TimeSpan At, bool Node, bool Declared)>();
            for (ulong cycle = 501; cycle <= 508; cycle++)
            {
                Publish(h, s, cycle);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
                samples.Add((DateTime.UtcNow - held, h.Server.Nodes.LabelingReady, host.Service.SqlUnavailable));
            }

            // Las lecturas responden todo el tiempo; la caída, una vez declarada, se mantiene.
            Assert.NotNull(await s.Stations.GetActiveAsync(StationScenario.SiteId, s.StationId, Ct));
            Assert.All(samples.Where(x => x.At > TimeSpan.FromSeconds(4)), x => Assert.Equal((false, true), (x.Node, x.Declared)));

            await tx.RollbackAsync(Ct);
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al soltar el rango");
            Publish(h, s, 509);
            await WaitAsync(async () => await s.DetectionAsync(509, Ct) is not null, "detección 509");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        await AssertGapAsync(s, metrics, 501, 508);
    }

    /// <summary>
    /// La vigilancia recarga el estado antes de levantar la caída (segunda revisión, BAJA-3). Durante la caída, otro escritor
    /// registra el ciclo 510 directamente en SQL; al volver, la marca alta de la ingesta es 510, no la que tenía en memoria (500).
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task Recovery_reloads_the_station_state_before_lifting_the_outage()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        await using var proxy = SqlProxy(s);
        var db = new SqlDb(new DatabaseOptions
        {
            ConnectionString = new SqlConnectionStringBuilder(s.Db.AppConnectionString) { DataSource = $"127.0.0.1,{proxy.Port}" }.ConnectionString,
        });
        using var metrics = new CartonMetrics();
        var ingest = new StationIngestService(s.Settings,
            new RegistrationService(new TxaRegisterDetection(db), new ErpCacheStore(db), new LabelSerialSource(db), s.Clock, metrics),
            new StationConfigStore(db), new PlcEpochStore(db), new SequenceStore(db), new TxsSweep(db), new TxmManualActions(db), s.Clock, metrics,
            failClosed: new SqlFailClosedOptions { AttemptTimeout = TimeSpan.FromSeconds(1), RecoveryProbeInterval = TimeSpan.FromMilliseconds(200) });
        await using var h = await OpcHarness.StartAsync(s.StationId, s.Clock);
        var channel = Channel.CreateUnbounded<RawNotification>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var consumer = ingest.RunAsync(channel.Reader, stop.Token);
        try
        {
            await h.NextAsync(TimeSpan.FromSeconds(60));
            await channel.Writer.WriteAsync(await ReceiveAsync(h, s, 500), Ct);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");

            proxy.Freeze();
            await channel.Writer.WriteAsync(await ReceiveAsync(h, s, 501), Ct);
            await WaitAsync(() => Task.FromResult(ingest.SqlUnavailable), "la ingesta declara la caída");
            Assert.Equal(DetectionStatus.JobCreated, (await s.RegisterAsync(s.Cycle(510), ct: Ct)).FinalStatus);
            proxy.Resume();
            await WaitAsync(() => Task.FromResult(!ingest.SqlUnavailable), "la ingesta recarga el estado al volver SQL");
            Assert.Equal(510UL, ingest.Classifier.Current!.HighWater);
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
        }
    }

    /// <summary>
    /// La inyección del catálogo con un servidor real (segunda revisión, M-3): el contenedor de SQL Server, propio de esta prueba, se
    /// detiene mientras pasan diez cajas y vuelve a arrancar. Al detenerse y al volver aparecen errores que un proxy no produce
    /// (apagado en curso, base en recuperación, login fallido): la ingesta no se detiene por ellos, mantiene la falla cerrada y, cuando
    /// la base está lista, recarga el estado y levanta LabelingReady. Al volver, el contenedor publica otro puerto: el proxy de la
    /// prueba solo redirige a él, como lo haría un nombre de red estable en planta.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task Stopping_the_real_sql_server_fails_closed_and_the_ingest_survives_its_restart()
    {
        await using var server = new SqlServerFixture();
        await server.InitializeAsync();
        var database = await server.CreateDatabaseAsync("t19stop", Ct);
        var direct = new SqlConnectionStringBuilder(database.AppConnectionString);
        var hostPort = direct.DataSource.Replace("tcp:", string.Empty, StringComparison.Ordinal).Split(',');
        await using var proxy = new TcpProxy(hostPort[0], int.Parse(hostPort[1], System.Globalization.CultureInfo.InvariantCulture)).Start();

        // Todo, también lo que la prueba lee y el barrido, pasa por el proxy: así sigue al servidor cuando cambia de puerto.
        string Via(string connectionString) => new SqlConnectionStringBuilder(connectionString) { DataSource = $"127.0.0.1,{proxy.Port}" }.ConnectionString;
        var s = await StationScenario.CreateAsync(database with
        {
            AdminConnectionString = Via(database.AdminConnectionString),
            MigratorConnectionString = Via(database.MigratorConnectionString),
            AppConnectionString = Via(database.AppConnectionString),
        }, ct: Ct, time: TimeProvider.System);
        var viaProxy = new SqlConnectionStringBuilder(s.Db.AppConnectionString);

        await using var h = await OpcHarness.StartAsync(s.StationId, new EdgeClock(TimeProvider.System));
        using var metrics = new CartonMetrics();
        using var host = new IngestHostedService(Options(s, h), new SqlDb(new DatabaseOptions { ConnectionString = viaProxy.ConnectionString }), s.Clock,
            metrics, NullLoggerFactory.Instance);
        await host.StartAsync(Ct);
        try
        {
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady al arrancar");
            Publish(h, s, 500);
            await WaitAsync(async () => await s.DetectionAsync(500, Ct) is not null, "detección 500");

            await server.Container.StopAsync(Ct);
            var readyWhileStopped = new List<bool>();
            for (ulong cycle = 501; cycle <= 510; cycle++)
            {
                Publish(h, s, cycle);
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
                readyWhileStopped.Add(h.Server.Nodes.LabelingReady);
            }

            Assert.True(host.Service.SqlUnavailable);
            Assert.All(readyWhileStopped.Skip(2), Assert.False);

            await server.Container.StartAsync(Ct);
            proxy.Retarget(server.Container.Hostname, server.Container.GetMappedPublicPort(1433));
            await WaitAsync(() => Task.FromResult(h.Server.Nodes.LabelingReady), "LabelingReady con el servidor de vuelta", TimeSpan.FromMinutes(3));
            Assert.False(host.ExecuteTask?.IsCompleted ?? false, "la ingesta se detuvo con el reinicio del servidor");
            Publish(h, s, 511);
            await WaitAsync(async () => await s.DetectionAsync(511, Ct) is not null, "detección 511");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        await AssertGapAsync(s, metrics, 501, 510);
    }

    /// <summary>
    /// TX-B en la falla cerrada (DOCUMENTO_PRINCIPAL §15, "Error en TX-A o TX-B"; segunda revisión, BAJA-1). Un sender cuyo SQL no
    /// está disponible declara la caída de la estación en el claim. Mientras está declarada, el sender de línea no reclama nada aunque
    /// su SQL responda; al levantarse, el job READY sale.
    /// </summary>
    [Fact]
    [Trait("Spec", "T19")]
    public async Task A_claim_without_sql_declares_the_outage_and_the_line_sender_claims_nothing_while_it_lasts()
    {
        var s = await StationScenario.CreateAsync(_db, ct: Ct, time: TimeProvider.System);
        var job = await s.ReadyJobAsync(700, Ct);
        var outage = new SqlOutage(s.Clock);

        await using (var proxy = SqlProxy(s))
        {
            proxy.AcceptNew = false;
            var cut = new SqlDb(new DatabaseOptions
            {
                ConnectionString = new SqlConnectionStringBuilder(s.Db.AppConnectionString)
                {
                    DataSource = $"127.0.0.1,{proxy.Port}", ConnectTimeout = 2, Pooling = false, ConnectRetryCount = 0,
                }.ConnectionString,
            });
            var withoutSql = new PrinterSenderService(s.PrinterId, cut, new ClaimService(cut, new TxbClaim(cut), s.Metrics, s.Clock, new ClockGuard(s.Clock)),
                new TxcWriteResult(cut), new StationConfigStore(cut), s.Lease, new TcpPrinterTransport(new PrinterTransportOptions { Port = 1 }, s.Provider),
                s.Clock, s.Metrics, sqlOutage: outage);
            using var stopCut = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var run = withoutSql.RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, stopCut.Token);
            await WaitAsync(() => Task.FromResult(outage.Declared), "la caída declarada por el claim");
            await stopCut.CancelAsync();
            await run.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        await using var rig = PrintRig.Create(s, sqlOutage: outage);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var sender = rig.Sender.RunAsync(TimeSpan.FromMilliseconds(50), TimeProvider.System, null, stop.Token);
        var lease = s.KeepLeaseAsync(stop.Token);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
            Assert.Equal(JobStatus.Ready, (await s.JobAsync(job, Ct)).Status);
            Assert.Empty(rig.Printer.Received);

            Assert.True(outage.TryRecover(s.Clock.GetTimestamp()));
            await WaitAsync(async () => (await s.JobAsync(job, Ct)).Status != JobStatus.Ready, "el claim al levantarse la caída");
        }
        finally
        {
            await stop.CancelAsync();
            await sender.ContinueWith(_ => { }, TaskScheduler.Default);
            await lease;
        }
    }

    /// <summary>Ningún rastro de los ciclos de la caída fuera del hueco, que el barrido cierra CLOSED_UNRECOVERED.</summary>
    private static async Task AssertGapAsync(StationScenario s, CartonMetrics metrics, ulong from, ulong to)
    {
        var count = (int)(to - from + 1);
        Assert.Equal(0, await s.CountAsync("FROM dbo.CartonDetection WHERE StationId = @st AND CycleId BETWEEN @from AND @to",
            c => Bind(c, s, from, to), Ct));
        Assert.Equal(1, await s.CountAsync("FROM dbo.SequenceGap WHERE StationId = @st AND FromCycleId = @from AND ToCycleId = @to AND Status = 'OPEN'",
            c => Bind(c, s, from, to), Ct));
        Assert.Equal(count, metrics.Series(MetricNames.OpcRecordsRejected).Where(e => e.Key.Contains("cause=SQL_UNAVAILABLE", StringComparison.Ordinal))
            .Sum(e => e.Value));

        await Task.Delay(TimeSpan.FromMilliseconds(s.Revision.GapGraceMs + 200), Ct);
        await new StationSweepService(s.Settings, s.Txs, s.Stations, new ClockGuard(s.Clock), s.Clock, s.Metrics).SweepOnceAsync(Ct);
        Assert.Equal(("CLOSED_UNRECOVERED", (decimal)count), Assert.Single(await s.QueryAsync(
            "SELECT Status, UnrecoveredCount FROM dbo.SequenceGap WHERE StationId = @st AND FromCycleId = @from",
            c => Bind(c, s, from, to), r => (r.GetString(0), r.GetDecimal(1)), Ct)));
        Assert.Equal(count, s.Metrics.Total(MetricNames.CyclesUnobserved, ("station", s.StationId)));
        Assert.Equal(0, await s.CountAsync("FROM dbo.PrintJob WHERE StationId = @st AND CycleId BETWEEN @from AND @to", c => Bind(c, s, from, to), Ct));
        Assert.DoesNotContain(await s.ResultsAsync(ct: Ct), r => r.GetProperty("cycleId").GetString() is { } c && ulong.Parse(c) >= from && ulong.Parse(c) <= to);
    }

    private static void Bind(SqlCommand c, StationScenario s, ulong from, ulong to)
    {
        c.Parameters.AddWithValue("@st", s.StationId);
        c.Parameters.AddWithValue("@from", (decimal)from);
        c.Parameters.AddWithValue("@to", (decimal)to);
    }

    private static TcpProxy SqlProxy(StationScenario s)
    {
        var direct = new SqlConnectionStringBuilder(s.Db.AppConnectionString);
        var parts = direct.DataSource.Replace("tcp:", string.Empty, StringComparison.Ordinal).Split(',');
        return new TcpProxy(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)).Start();
    }

    private static IngestHostOptions Options(StationScenario s, OpcHarness h) => new()
    {
        SiteId = StationScenario.SiteId,
        StationId = s.StationId,
        LabelSerialPrefix = s.SerialPrefix,
        LabelingReadyIntervalMs = 100,
        Opc = new OpcIngestOptions
        {
            EndpointUrl = $"opc.tcp://localhost:{h.ServerPort}/CartonPlc",
            RecordNodeId = h.Server.Nodes.RecordNodeId,
            LabelingReadyNodeId = h.Server.Nodes.LabelingReadyNodeId,
            SecurityMode = "None",
            PkiRoot = Path.Combine(Path.GetTempPath(), "cartonprinting-pki", Guid.NewGuid().ToString("N")[..8], "host"),
            AutoAcceptUntrustedCertificates = true,
        },
    };

    private static void Publish(OpcHarness h, StationScenario s, ulong cycle) => h.Server.Nodes.Publish(new LatchedRecord
    {
        PlcEpoch = s.Epoch, CycleId = cycle, WorkOrderId = StationScenario.WorkOrder, DetectionTimestamp = DateTime.UtcNow,
    });

    private static async Task WaitAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"No se cumplió a tiempo: {what}.");
            await Task.Delay(100, Ct);
        }
    }
}
