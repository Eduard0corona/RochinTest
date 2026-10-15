using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;

namespace CartonPrinting.Orchestration;

/// <summary>
/// Plazo de la caja (sección 10.1 del documento principal, ADR 0006). El plazo descuenta
/// DeviceLeadTimeMs: un envío que empieza antes del plazo debe tener tiempo físico de terminar.
/// </summary>
public static class DeadlinePolicy
{
    /// <summary>
    /// Plazo con la referencia de la revisión, o null si no hay referencia confiable: con
    /// PLC_TIMESTAMP sin DetectionTimestamp; con GATEWAY_RECEIPT si el registro no llegó en vivo
    /// (Transport distinto de SUBSCRIPTION, overflow, primera notificación registrada como READ) o abre
    /// un hueco. Sin plazo confiable se registra DeadlineAt = ReceivedAt: nunca se fabrica un plazo.
    /// </summary>
    public static DateTime? Compute(StationRevision rev, DateTime? detectionTimestampUtc, DateTime receivedAtUtc, string transport, bool queueOverflow, bool opensGap)
    {
        if (rev.DeadlineReference == DeadlineReference.PlcTimestamp)
        {
            if (detectionTimestampUtc is null)
            {
                return null;
            }

            var skew = rev.ClockSkewToleranceMs ?? throw new InvalidOperationException("PLC_TIMESTAMP exige ClockSkewToleranceMs.");
            return CanonicalTimestamp.Truncate(detectionTimestampUtc.Value)
                .AddMilliseconds(rev.TransitBudgetMs - rev.DeviceLeadTimeMs - skew);
        }

        var live = transport == Transport.Subscription && !queueOverflow && !opensGap;
        if (!live)
        {
            return null;
        }

        var upstream = rev.MaxUpstreamLatencyMs ?? throw new InvalidOperationException("GATEWAY_RECEIPT exige MaxUpstreamLatencyMs.");
        return CanonicalTimestamp.Truncate(receivedAtUtc).AddMilliseconds(-upstream + rev.TransitBudgetMs - rev.DeviceLeadTimeMs);
    }

    /// <summary>
    /// Plazo efectivo de un job pendiente: el menor entre el congelado y el recalculado con la
    /// revisión activa (nunca más permisivo que el congelado). Si la revisión activa no puede dar un
    /// plazo confiable para el registro, el plazo efectivo es su recepción: el job ya no se envía (SI-09).
    /// </summary>
    public static DateTime Effective(DateTime frozenDeadlineUtc, StationRevision active, DetectionRecord detection, bool opensGap = false)
    {
        var recalculated = Compute(active, detection.DetectionTimestamp, detection.ReceivedAt, detection.Transport, detection.QueueOverflow, opensGap)
            ?? detection.ReceivedAt;
        return recalculated < frozenDeadlineUtc ? recalculated : frozenDeadlineUtc;
    }

    /// <summary>Margen efectivo: el mayor entre el congelado y el activo.</summary>
    public static int EffectiveSendMargin(StationRevision frozen, StationRevision active) => Math.Max(frozen.SendMarginMs, active.SendMarginMs);

    /// <summary>Intentos automáticos efectivos: el menor entre el congelado y el activo.</summary>
    public static byte EffectiveMaxAutoAttempts(StationRevision frozen, StationRevision active) => Math.Min(frozen.MaxAutoAttempts, active.MaxAutoAttempts);

    /// <summary>Compuerta física: exigida si la congelada o la activa la exigen, con la frescura menor.</summary>
    public static int? EffectiveGateFreshness(StationRevision frozen, StationRevision active)
    {
        int?[] values = [frozen.UsesWindowGate ? frozen.GateFreshnessMs : null, active.UsesWindowGate ? active.GateFreshnessMs : null];
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToArray();
        return present.Length == 0 ? null : present.Min();
    }
}
