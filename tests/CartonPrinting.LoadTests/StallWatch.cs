using System.Diagnostics;
using CartonPrinting.Testing;
using Microsoft.Data.SqlClient;

namespace CartonPrinting.LoadTests;

/// <summary>
/// Diagnóstico de T12: muestrea cada 200 ms, con el login de administración, las solicitudes de la base de la prueba que llevan
/// esperando o ejecutando más de 250 ms (tipo de espera, recurso, sentencia y, si está bloqueada, qué sesión la bloquea, si esa
/// sesión está inactiva con una transacción abierta y su última sentencia), las pausas del propio proceso de prueba (el muestreo que
/// despierta tarde: recolección de basura o pool de hilos) y los momentos en que suben los contadores de fallas. Ubica en el tiempo,
/// con la misma referencia que los picos de backlog, la causa de una pausa del sender.
/// </summary>
internal sealed class StallWatch : IAsyncDisposable
{
    private const int MaxEntries = 120;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly string _admin;
    private readonly string _database;
    private readonly DateTime _startedAt;
    private readonly Func<IReadOnlyDictionary<string, long>> _counters;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<(short Session, DateTime Start), string> _requests = [];
    private readonly List<string> _events = [];
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
            return [.. _events.Concat(_requests.Values).OrderBy(e => double.Parse(e[..e.IndexOf(' ')], System.Globalization.CultureInfo.InvariantCulture)).Take(MaxEntries)];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private string At() => (DateTime.UtcNow - _startedAt).TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private void Add(string entry)
    {
        lock (_events)
        {
            if (_events.Count < MaxEntries)
            {
                _events.Add(entry);
            }
        }
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        var last = new Dictionary<string, long>(_counters());
        var gcPause = GC.GetTotalPauseDuration();
        await using var conn = new SqlConnection(_admin);
        await conn.OpenAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            var slept = Stopwatch.StartNew();
            await Task.Delay(Interval, ct);
            var late = slept.Elapsed - Interval;
            var pause = GC.GetTotalPauseDuration();
            if (late > TimeSpan.FromMilliseconds(300))
            {
                Add($"{At()} s: el proceso de prueba despertó {late.TotalMilliseconds:F0} ms tarde (pausa de GC {(pause - gcPause).TotalMilliseconds:F0} ms, " +
                    $"trabajo pendiente del pool {ThreadPool.PendingWorkItemCount}, hilos {ThreadPool.ThreadCount})");
            }

            gcPause = pause;
            foreach (var (name, value) in _counters())
            {
                if (value != last.GetValueOrDefault(name))
                {
                    Add($"{At()} s: {name} {last.GetValueOrDefault(name)} → {value}");
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
                // Un diagnóstico que falla no hace fallar la prueba: queda en el reporte.
                Add($"{At()} s: el muestreo falló: {ex.GetType().Name}: {ex.Message}");
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    return;
                }
            }

            if (query.ElapsedMilliseconds > 500)
            {
                Add($"{At()} s: la consulta de muestreo tardó {query.ElapsedMilliseconds} ms (SQL Server o el contenedor sin responder)");
            }
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
            var blocker = r.GetInt16(7);
            var entry = $"{At()} s: sesión {key.Item1} {r.GetString(2)} {r.GetInt32(5)} ms" +
                (r.IsDBNull(3) ? string.Empty : $", espera {r.GetString(3)} {r.GetInt32(4)} ms en {(r.IsDBNull(6) ? "-" : r.GetString(6))}") +
                $": {Compact(r.IsDBNull(8) ? "-" : r.GetString(8))}" +
                (blocker == 0 ? string.Empty
                    : $" | bloqueada por la sesión {blocker} ({(r.IsDBNull(9) ? "?" : r.GetString(9))}, transacciones abiertas {(r.IsDBNull(10) ? 0 : r.GetInt32(10))}, " +
                      $"inactiva {(r.IsDBNull(11) ? "-" : r.GetInt32(11).ToString(System.Globalization.CultureInfo.InvariantCulture))} ms): " +
                      Compact(r.IsDBNull(12) ? "-" : r.GetString(12)));
            lock (_events)
            {
                // De cada solicitud queda la última observación (la de mayor espera); la marca de tiempo es la de la primera.
                if (_requests.TryGetValue(key, out var previous))
                {
                    _requests[key] = previous[..previous.IndexOf(' ')] + entry[entry.IndexOf(' ')..];
                }
                else if (_requests.Count < MaxEntries)
                {
                    _requests[key] = entry;
                }
            }
        }
    }

    private static string Compact(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
