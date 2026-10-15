using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;
using CartonPrinting.Infrastructure.Transactions;

namespace CartonPrinting.Orchestration;

/// <summary>Caso de la sección 9.2 al que corresponde un registro con clave nueva.</summary>
public enum SequenceCase
{
    FirstOfEpoch,
    InOrder,
    Gap,
    Late,
    ResetSuspected,
    AmbiguousDecrease,

    /// <summary>Registro incoherente con CycleId menor o igual que la marca alta: sin efecto de secuencia.</summary>
    IncoherentBelowMark,
}

/// <summary>Veredicto de la clasificación de secuencia de un registro Good.</summary>
public abstract record SequenceVerdict
{
    /// <summary>La época es la primera de la estación (INITIAL) o la actual + 1 con AUTO_INCREMENT_BY_ONE.</summary>
    public sealed record AcceptEpoch(long Epoch, string AcceptanceMode) : SequenceVerdict;

    /// <summary>EPOCH_UNEXPECTED: sin detección; el registro queda retenido y la estación se bloquea.</summary>
    public sealed record RetainEpochUnexpected(string Detail) : SequenceVerdict;

    /// <summary>La misma clave con la misma huella ya se registró en este proceso: sin efecto y sin ir a SQL.</summary>
    public sealed record KnownDuplicate : SequenceVerdict;

    /// <summary>
    /// Va a TX-A con la decisión de secuencia. Si la clave ya existe, TX-A la clasifica como duplicado o
    /// colisión por inserción primero y la decisión se descarta.
    /// </summary>
    public sealed record Register(SequenceCase Case, SequenceDecision Decision) : SequenceVerdict;
}

/// <summary>
/// Clasificación de un registro OPC (sección 9.2 y diagrama 08 de la especificación). Estado en
/// memoria de una estación: épocas aceptadas en orden de AcceptedAt con su marca alta y una caché de
/// huellas recientes (barrera 2: descarta repeticiones sin ir a SQL). La unicidad la garantiza TX-A:
/// la caché solo contiene claves que este proceso registró o confirmó como duplicado.
/// Un consumidor por estación: no es seguro para hilos.
/// </summary>
public sealed class SequenceClassifier
{
    private const int FingerprintCacheSize = 4096;

    private readonly List<EpochMark> _epochs;
    private readonly Dictionary<(long Epoch, ulong Cycle), byte[]> _fingerprints = [];
    private readonly Queue<(long Epoch, ulong Cycle)> _fingerprintOrder = new();

    public SequenceClassifier(IEnumerable<EpochMark> epochs) => _epochs = epochs.OrderBy(e => e.AcceptedAt).ToList();

    public IReadOnlyList<EpochMark> Epochs => _epochs;

    public EpochMark? Current => _epochs.Count == 0 ? null : _epochs[^1];

    /// <summary>La última clave nueva clasificada fue AMBIGUOUS_DECREASE (para la regla de ciclos consecutivos).</summary>
    public bool LastWasAmbiguousDecrease { get; private set; }

    public SequenceVerdict Classify(CycleRecord record, StationRevision revision, DateTime edgeNowUtc)
    {
        var verdict = ClassifyEpoch(record, revision, edgeNowUtc);
        if (verdict is SequenceVerdict.AcceptEpoch && !record.Coherent)
        {
            // Un registro incoherente no acepta épocas: su PlcEpoch puede ser de otro ciclo (SI-13, interino de
            // falla cerrada).
            return new SequenceVerdict.RetainEpochUnexpected($"Registro incoherente de la época {record.PlcEpoch}, que no está aceptada.");
        }

        return verdict;
    }

    private SequenceVerdict ClassifyEpoch(CycleRecord record, StationRevision revision, DateTime edgeNowUtc)
    {
        var current = Current;
        if (current is null)
        {
            return new SequenceVerdict.AcceptEpoch(record.PlcEpoch, PlcEpochStore.Initial);
        }

        EpochMark target;
        if (record.PlcEpoch == current.Epoch)
        {
            target = current;
        }
        else if (_epochs.Find(e => e.Epoch == record.PlcEpoch) is { } earlier)
        {
            // Época anterior ya aceptada: solo hasta su marca alta (duplicado, colisión o tardío).
            if (earlier.HighWater is not { } hw || record.CycleId > hw)
            {
                return new SequenceVerdict.RetainEpochUnexpected(
                    $"Época {record.PlcEpoch} anterior a la actual {current.Epoch} con CycleId {record.CycleId} sobre su marca alta {earlier.HighWater?.ToString() ?? "(vacía)"}.");
            }

            target = earlier;
        }
        else if (current.Epoch < uint.MaxValue && record.PlcEpoch == current.Epoch + 1)
        {
            if (revision.EpochChangePolicy == PlcEpochStore.AutoIncrementByOne)
            {
                return new SequenceVerdict.AcceptEpoch(record.PlcEpoch, PlcEpochStore.AutoIncrementByOne);
            }

            return new SequenceVerdict.RetainEpochUnexpected(
                $"Época {record.PlcEpoch} = actual + 1 con EpochChangePolicy {revision.EpochChangePolicy}: requiere confirmación.");
        }
        else
        {
            return new SequenceVerdict.RetainEpochUnexpected($"Época {record.PlcEpoch} inesperada (actual {current.Epoch}).");
        }

        if (_fingerprints.TryGetValue((record.PlcEpoch, record.CycleId), out var known)
            && known.AsSpan().SequenceEqual(record.SourceFingerprint()))
        {
            return new SequenceVerdict.KnownDuplicate();
        }

        return ClassifyAgainst(target, record, revision, edgeNowUtc);
    }

    private SequenceVerdict ClassifyAgainst(EpochMark mark, CycleRecord record, StationRevision revision, DateTime edgeNowUtc)
    {
        var c = record.CycleId;
        var grace = CanonicalTimestamp.Truncate(edgeNowUtc).AddMilliseconds(revision.GapGraceMs);
        if (mark.HighWater is not { } m)
        {
            // Primera clave de la época. En la primera época de la estación no se sabe desde dónde contaba el PLC;
            // en una época nueva (reinicio del contador), mientras OT no fije el valor de arranque, la marca alta se
            // reinicia como si el contador arrancara en 1 y los ciclos anteriores sin datos son un hueco (SI-12,
            // interino de falla cerrada: se cuentan y, con GATEWAY_RECEIPT, el registro no recibe plazo).
            if (mark.AcceptanceMode == PlcEpochStore.Initial || c <= 1)
            {
                return new SequenceVerdict.Register(SequenceCase.FirstOfEpoch, new SequenceDecision());
            }

            return new SequenceVerdict.Register(SequenceCase.Gap, new SequenceDecision { Gap = new GapOpen(1, c - 1, grace) });
        }

        if (m < ulong.MaxValue && c == m + 1)
        {
            return WithRate(SequenceCase.InOrder, new SequenceDecision(), mark, record, revision);
        }

        if (m < ulong.MaxValue && c > m + 1)
        {
            return WithRate(SequenceCase.Gap, new SequenceDecision { Gap = new GapOpen(m + 1, c - 1, grace) }, mark, record, revision);
        }

        if (!record.Coherent)
        {
            // Un registro incoherente (queda BLOCKED con INCOHERENT_SNAPSHOT) solo avanza la marca alta y abre
            // huecos, igual que su fila al recargar el estado desde SQL; su DetectionTimestamp no es confiable, así
            // que no decide tardíos ni reinicios.
            return new SequenceVerdict.Register(SequenceCase.IncoherentBelowMark, new SequenceDecision());
        }

        // Clave nueva (si existiera, TX-A lo resuelve) con CycleId menor o igual que la marca alta.
        var retained = RetainedRecords.ToJson(record);
        if (record.DetectionTimestamp is { } ts && mark.HighWaterTimestamp is { } tsM)
        {
            if (CanonicalTimestamp.Truncate(ts) < tsM)
            {
                // Tardío legítimo: su propio plazo decide y la impresora, el orden (J03 OUT_OF_ORDER).
                return new SequenceVerdict.Register(SequenceCase.Late, new SequenceDecision());
            }

            return new SequenceVerdict.Register(SequenceCase.ResetSuspected, new SequenceDecision
            {
                TerminalStatus = DetectionStatus.Blocked,
                TerminalReason = DetectionReason.SequenceAnomaly,
                Anomaly = new AnomalyInsert(AnomalyType.ResetSuspected, AnomalySeverity.Blocking, retained, BlockStation: true,
                    BlockReason.ResetSuspected),
            });
        }

        // Sin DetectionTimestamp no se distingue un tardío de un reinicio: WARNING; si se repite en
        // ciclos consecutivos, BLOCKING y estación bloqueada.
        var repeated = LastWasAmbiguousDecrease;
        return new SequenceVerdict.Register(SequenceCase.AmbiguousDecrease, new SequenceDecision
        {
            TerminalStatus = DetectionStatus.Blocked,
            TerminalReason = DetectionReason.SequenceAnomaly,
            Anomaly = new AnomalyInsert(AnomalyType.AmbiguousDecrease, repeated ? AnomalySeverity.Blocking : AnomalySeverity.Warning, retained,
                BlockStation: repeated, repeated ? BlockReason.AmbiguousDecrease : null),
        });
    }

    /// <summary>
    /// Rebote del sensor: el avance desde la marca alta es más rápido que MinCycleIntervalMs por ciclo,
    /// según DetectionTimestamp. Sin DetectionTimestamp (o sin MinCycleIntervalMs) la regla no se aplica.
    /// </summary>
    private static SequenceVerdict WithRate(SequenceCase kind, SequenceDecision decision, EpochMark mark, CycleRecord record, StationRevision revision)
    {
        if (record.Coherent && revision.MinCycleIntervalMs is { } min && mark.HighWater is { } m && record.DetectionTimestamp is { } ts
            && mark.HighWaterTimestamp is { } tsM)
        {
            var elapsedMs = (CanonicalTimestamp.Truncate(ts) - tsM).TotalMilliseconds;
            var requiredMs = (double)(record.CycleId - m) * min;
            if (elapsedMs < requiredMs)
            {
                decision = decision with { TerminalStatus = DetectionStatus.Blocked, TerminalReason = DetectionReason.ImplausibleRate };
            }
        }

        return new SequenceVerdict.Register(kind, decision);
    }

    /// <summary>Época aceptada (INITIAL, AUTO_INCREMENT_BY_ONE u OPERATOR_CONFIRMED): pasa a ser la actual, con marca alta vacía.</summary>
    public void AcceptEpoch(long epoch, DateTime acceptedAt, string acceptanceMode)
    {
        if (_epochs.Exists(e => e.Epoch == epoch))
        {
            return;
        }

        _epochs.Add(new EpochMark(epoch, acceptedAt, null, null, acceptanceMode));
    }

    /// <summary>
    /// Actualiza la marca alta y la caché con el resultado de TX-A. La misma regla que
    /// <see cref="SequenceStore.LoadEpochsAsync"/>: el DetectionTimestamp de una detección BLOCKED con
    /// INCOHERENT_SNAPSHOT no se toma como hora de la marca alta.
    /// </summary>
    public void Observe(CycleRecord record, SequenceCase kind, RegistrationResult result) =>
        Observe(record, kind, result.Outcome,
            timestampTrusted: !(result.FinalStatus == DetectionStatus.Blocked && result.FinalReason == DetectionReason.IncoherentSnapshot));

    /// <summary>Actualiza la marca alta y la caché con el resultado de TX-A.</summary>
    public void Observe(CycleRecord record, SequenceCase kind, RegistrationOutcome outcome, bool timestampTrusted = true)
    {
        var key = (record.PlcEpoch, record.CycleId);
        if (outcome == RegistrationOutcome.Collision)
        {
            return;
        }

        Remember(key, record.SourceFingerprint());
        if (outcome == RegistrationOutcome.Registered && record.Coherent)
        {
            // Un incoherente entre dos descensos ambiguos no corta la cadena de ciclos consecutivos.
            LastWasAmbiguousDecrease = kind == SequenceCase.AmbiguousDecrease;
        }

        // Un duplicado auténtico también mueve la marca alta: la detección existe (la registró otra instancia o un
        // intento anterior), igual que al reconstruir el estado desde SQL.
        var index = _epochs.FindIndex(e => e.Epoch == record.PlcEpoch);
        if (index < 0)
        {
            return;
        }

        var mark = _epochs[index];
        if (mark.HighWater is not { } m || record.CycleId > m)
        {
            _epochs[index] = mark with
            {
                HighWater = record.CycleId,
                HighWaterTimestamp = timestampTrusted && record.Coherent && record.DetectionTimestamp is { } ts
                    ? CanonicalTimestamp.Truncate(ts)
                    : null,
            };
        }
    }

    private void Remember((long, ulong) key, byte[] fingerprint)
    {
        if (_fingerprints.TryAdd(key, fingerprint))
        {
            _fingerprintOrder.Enqueue(key);
            if (_fingerprintOrder.Count > FingerprintCacheSize)
            {
                _fingerprints.Remove(_fingerprintOrder.Dequeue());
            }
        }
    }
}
