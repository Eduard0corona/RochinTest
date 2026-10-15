using CartonPrinting.Infrastructure.Observability;
using CartonPrinting.Infrastructure.Time;
using CartonPrinting.Infrastructure.Transactions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CartonPrinting.PrintAgent;

/// <summary>
/// Arranque del sender de una impresora (después del lock y del lease, antes de enviar): TX-R0 deja en UNKNOWN
/// con RESTART_DURING_SEND todo intento que quedó en SENDING (A04, J09; X03 para uno excepcional). Nunca vuelve
/// nada a READY: el proceso pudo morir antes, durante o después de escribir, y desde SQL no se distingue (T07).
/// </summary>
public sealed class SenderRecovery(Txr0Recovery txr0, EdgeClock clock, CartonMetrics metrics, ILogger<SenderRecovery>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<SenderRecovery>.Instance;

    /// <summary>TX-R0 de la impresora; con <paramref name="leaseToken"/>, solo lo reclamado con ese lease (el sender en marcha, SI-20).</summary>
    public async Task<RecoveryResult> RecoverAsync(string printerId, CancellationToken ct, long? leaseToken = null)
    {
        var result = await txr0.ExecuteAsync(printerId, clock.UtcNow, ct, leaseToken);
        var recovered = result.OrdinaryJobs.Count + result.ExceptionalJobs.Count;
        if (recovered > 0)
        {
            metrics.Add(MetricNames.RestartRecoveredSending, recovered, ("printer", printerId));
            _logger.LogWarning("Arranque de {Printer}: {Count} intento(s) en SENDING pasan a UNKNOWN (RESTART_DURING_SEND); ninguno se reenvía",
                printerId, recovered);
        }

        if (result.ExceptionalJobs.Count > 0)
        {
            // X03 en la misma transacción que A04: reimpresión excepcional sin resolver.
            metrics.Add(MetricNames.ExceptionalReprints, result.ExceptionalJobs.Count, ("result", "UNRESOLVED"));
        }

        return result;
    }
}
