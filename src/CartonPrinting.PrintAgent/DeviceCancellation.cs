using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;

namespace CartonPrinting.PrintAgent;

/// <summary>
/// Control de un job en el dispositivo (OT-07): pide cancelarlo y devuelve true solo si el dispositivo confirma,
/// para ese job, que lo canceló o descartó y que ya no está en su cola. Un "no impreso" sin esa confirmación es
/// false. La capacidad depende del dispositivo: sin confirmación declarada, siempre false.
/// </summary>
public interface IDeviceJobControl
{
    Task<bool> CancelAndConfirmAsync(string labelSerial, CancellationToken ct);
}

/// <summary>
/// A07 y J12: con la cancelación confirmada por el dispositivo, el intento TRANSMITTED pasa a NOT_PRINTED y el
/// job a FAILED con DEVICE_REPORTED_NOT_PRINTED (o, para una reimpresión, solo el intento). Sin confirmación no
/// cambia nada: el job sigue esperando evidencia y, si la ventana vence, queda UNKNOWN (T31).
/// </summary>
public sealed class DeviceCancellation(IDeviceJobControl device, SqlDb db, TxdEvidence txd, EdgeClock clock)
{
    public async Task<bool> CancelAsync(Guid printJobId, CancellationToken ct)
    {
        var (job, attempt) = await db.ReadAsync(async conn =>
        {
            var j = await JobQueries.GetJobAsync(conn, null, printJobId, ct);
            var attempts = j is null ? [] : await JobQueries.GetAttemptsAsync(conn, null, printJobId, ct);
            return (j, attempts.Where(a => a.Outcome == AttemptOutcome.Transmitted).MaxBy(a => a.AttemptNumber));
        }, ct);
        if (job is null || attempt is null || !await device.CancelAndConfirmAsync(job.LabelSerial, ct))
        {
            return false;
        }

        await txd.DeviceNotPrintedAsync(attempt.AttemptId, "Cancelación confirmada por el dispositivo; cola sin el job", clock.UtcNow, ct);
        return true;
    }
}
