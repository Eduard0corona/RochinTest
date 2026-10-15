using CartonPrinting.Infrastructure.Transactions;
using Microsoft.Extensions.Logging;

namespace CartonPrinting.PrintAgent;

/// <summary>
/// Registro del resultado de un envío (TX-C) con dos reintentos cortos: TX-C es idempotente (solo actúa sobre el intento
/// en SENDING). Si SQL sigue sin responder, el sender se detiene (<see cref="SenderHaltedException"/>): el intento queda
/// en SENDING, la impresora no se usa y el arranque siguiente ejecuta TX-R0 (ESTRATEGIA_RECUPERACION §3, SQL Server).
/// </summary>
internal static class ResultRecorder
{
    public static async Task<WriteOutcome> RecordAsync(Func<Task<WriteOutcome>> txc, string printerId, Guid attemptId, ILogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await txc();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= 3)
                {
                    logger.LogCritical(ex, "Sender de {Printer}: TX-C del intento {Attempt} falló tres veces; el sender se detiene", printerId, attemptId);
                    throw new SenderHaltedException($"TX-C del intento {attemptId} no se pudo registrar.", ex);
                }

                logger.LogWarning(ex, "Sender de {Printer}: TX-C del intento {Attempt} falló; reintento {Retry}", printerId, attemptId, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
        }
    }
}
