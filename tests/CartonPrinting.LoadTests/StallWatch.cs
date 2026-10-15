using System.Diagnostics;
using System.Globalization;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.LoadTests;

/// <summary>
/// Diagnóstico de T12: muestrea cada <see cref="IntervalMs"/> ms, con el login de administración, las solicitudes de la base de la
/// prueba que llevan esperando o ejecutando más de 250 ms (tipo de espera, recurso, sentencia y, si está bloqueada, qué sesión la
/// bloquea, si esa sesión está inactiva con una transacción abierta y su última sentencia), las pausas del propio proceso de prueba
/// (el muestreo que despierta tarde: recolección de basura o pool de hilos) y los momentos en que suben los contadores de fallas.
/// Ubica en el tiempo, con la misma referencia que los picos de backlog, la causa de una pausa del sender.
/// El reporte conserva todos los eventos y, de las solicitudes, las bloqueadas y las de mayor espera, no las primeras en llegar. Un
/// fallo del propio muestreo queda en el reporte y nunca hace fallar la prueba.
/// </summary>
internal sealed class StallWatch : IAsyncDisposable
{
    public const int IntervalMs = 200;
    private const int MaxEvents = 200;
    private const int MaxRequestsKept = 2_000;
    private const int MaxRequestsReported = 60;

    private readonly string _admin;
    private readonly string _database;
    private readonly DateTime _startedAt;
    private readonly Func<IReadOnlyDictionary<string, long>> _counters;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<(short Session, DateTime Start), Entry> _requests = [];
    private readonly List<Entry> _events = [];
    private readonly Task _loop;

    private StallWatch(TestDatabase db, DateTime startedAt, Func<IReadOnlyDictionary<string, long>> counters)
    {
        _admin = db.AdminConnectionString;
        _database = db.Name;
        _startedAt = startedAt;
        _counters = counters;
        _loop = Task.Run(LoopAsync);
    }

    public static StallWatch Start(TestDatabase db, DateTime startedAt, Func<IReadOnlyDictionary<string, long>> counters) => new(db, startedAt, counters);

    /// <summary>Lo observado, en orden de tiempo (segundos desde el comienzo de la muestra).</summary>
    public List<string> Report()
    {
        lock (_events)
        {
            var worst = _requests.Values.OrderByDescending(e => e.Weight).Take(MaxRequestsReported);
            return [.. _events.Concat(worst).OrderBy(e => e.At).Select(e => e.Text)];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (Exception ex)
        {
            Add($"el muestreo terminó con un error: {ex.GetType().Name}: {ex.Message}");
        }

        _stop.Dispose();
    }

    private double At() => (DateTime.UtcNow - _startedAt).TotalSeconds;

    private void Add(string text)
    {
        lock (_events)
        {
            if (_events.Count < MaxEvents)
            {
                var at = At();
                _events.Add(new Entry(at, int.MaxValue, $"{Seconds(at)} s: {text}"));
            }
        }
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            var last = new Dictionary<string, long>(_counters());
            var gcPause = GC.GetTotalPauseDuration();
            await using var conn = new SqlConnection(_admin);
            await conn.OpenAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var slept = Stopwatch.StartNew();
                await Task.Delay(IntervalMs, ct);
                var late = slept.Elapsed.TotalMilliseconds - IntervalMs;
                var pause = GC.GetTotalPauseDuration();
                if (late > 300)
                {
                    Add($"el proceso de prueba despertó {late:F0} ms tarde (pausa de GC {(pause - gcPause).TotalMilliseconds:F0} ms, " +
                        $"trabajo pendiente del pool {ThreadPool.PendingWorkItemCount}, hilos {ThreadPool.ThreadCount})");
                }

                gcPause = pause;
                foreach (var (name, value) in _counters())
                {
                    if (value != last.GetValueOrDefault(name))
                    {
                        Add($"{name} {last.GetValueOrDefault(name)} → {value}");
                        last[name] = value;
                    }
                }

                var query = Stopwatch.StartNew();
                try
                {
                    await SampleAsync(conn, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Add($"el muestreo falló: {ex.GetType().Name}: {ex.Message}");
                    if (conn.State != System.Data.ConnectionState.Open)
                    {
                        return;
                    }
                }

                if (query.ElapsedMilliseconds > 500)
                {
                    Add($"la consulta de muestreo tardó {query.ElapsedMilliseconds} ms (SQL Server o el contenedor sin responder)");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Add($"el muestreo se detuvo: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task SampleAsync(SqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            SELECT r.session_id, r.start_time, r.status, r.wait_type, r.wait_time, r.total_elapsed_time, r.wait_resource, r.blocking_session_id,
                SUBSTRING(t.text, r.statement_start_offset / 2 + 1, 240),
                bs.status, bs.open_transaction_count, DATEDIFF(MILLISECOND, bs.last_request_end_time, SYSDATETIME()),
                (SELECT LEFT(bt.text, 240) FROM sys.dm_exec_connections AS bc CROSS APPLY sys.dm_exec_sql_text(bc.most_recent_sql_handle) AS bt
                 WHERE bc.session_id = r.blocking_session_id)
            FROM sys.dm_exec_requests AS r
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t
            LEFT JOIN sys.dm_exec_sessions AS bs ON bs.session_id = r.blocking_session_id
            WHERE r.session_id <> @@SPID AND r.database_id = DB_ID(@db) AND (r.wait_time >= 250 OR r.total_elapsed_time >= 250);
            """, conn);
        cmd.Parameters.AddWithValue("@db", _database);
        cmd.CommandTimeout = 5;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = (r.GetInt16(0), r.GetDateTime(1));
            var blocker = r.IsDBNull(7) ? (short)0 : r.GetInt16(7);
            var waitMs = r.IsDBNull(4) ? 0 : r.GetInt32(4);
            var elapsedMs = r.GetInt32(5);
            var text = $"sesión {key.Item1} {r.GetString(2)} {elapsedMs} ms" +
                (r.IsDBNull(3) ? string.Empty : $", espera {r.GetString(3)} {waitMs} ms en {(r.IsDBNull(6) ? "-" : r.GetString(6))}") +
                $": {Compact(r.IsDBNull(8) ? "-" : r.GetString(8))}" +
                (blocker == 0 ? string.Empty
                    : $" | bloqueada por la sesión {blocker} ({(r.IsDBNull(9) ? "?" : r.GetString(9))}, transacciones abiertas {(r.IsDBNull(10) ? 0 : r.GetInt32(10))}, " +
                      $"inactiva {(r.IsDBNull(11) ? "-" : r.GetInt32(11).ToString(CultureInfo.InvariantCulture))} ms): " +
                      Compact(r.IsDBNull(12) ? "-" : r.GetString(12)));
            // Las bloqueadas primero; después, la mayor espera o duración.
            var weight = (blocker == 0 ? 0 : 1_000_000) + Math.Min(Math.Max(waitMs, elapsedMs), 999_999);
            lock (_events)
            {
                // De cada solicitud queda la última observación (la de mayor espera), con el momento de la primera.
                if (_requests.TryGetValue(key, out var previous))
                {
                    _requests[key] = new Entry(previous.At, Math.Max(previous.Weight, weight), $"{Seconds(previous.At)} s: {text}");
                }
                else
                {
                    if (_requests.Count >= MaxRequestsKept)
                    {
                        var lightest = _requests.MinBy(e => e.Value.Weight);
                        if (lightest.Value.Weight >= weight)
                        {
                            continue;
                        }

                        _requests.Remove(lightest.Key);
                    }

                    var at = At();
                    _requests[key] = new Entry(at, weight, $"{Seconds(at)} s: {text}");
                }
            }
        }
    }

    private static string Seconds(double at) => at.ToString("F1", CultureInfo.InvariantCulture);

    private static string Compact(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record Entry(double At, int Weight, string Text);
}
