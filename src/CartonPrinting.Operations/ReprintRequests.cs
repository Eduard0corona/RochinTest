using System.Collections.Concurrent;

namespace CartonPrinting.Operations;

/// <summary>Solicitud de reimpresión excepcional que espera la aprobación de otra persona.</summary>
public sealed record PendingReprint(
    Guid RequestId,
    Guid PrintJobId,
    string Reason,
    string DuplicateRisk,
    string CartonReference,
    string InspectionEvidence,
    bool ReprintMarker,
    string RequestedBy,
    DateTime RequestedAt);

/// <summary>
/// Solicitudes pendientes de aprobación (R01 exige dos personas: <c>CK_ReprintAuthorization_TwoPersons</c>). El
/// esquema solo guarda autorizaciones aprobadas, así que la solicitud vive en memoria hasta que otra persona la
/// aprueba o vence; si el EdgeHost reinicia, se vuelve a pedir. Nada se ejecuta desde aquí: la reimpresión la
/// ejecuta el sender de la impresora destino (TX-X).
/// </summary>
public sealed class ReprintRequestStore
{
    private readonly ConcurrentDictionary<Guid, PendingReprint> _pending = new();

    public PendingReprint Add(PendingReprint request)
    {
        _pending[request.RequestId] = request;
        return request;
    }

    public PendingReprint? Find(Guid requestId) => _pending.TryGetValue(requestId, out var r) ? r : null;

    /// <summary>La quita si sigue pendiente (una sola aprobación por solicitud).</summary>
    public bool TryTake(Guid requestId, out PendingReprint? request)
    {
        var taken = _pending.TryRemove(requestId, out var r);
        request = r;
        return taken;
    }

    /// <summary>Descarta las solicitudes más viejas que <paramref name="maxAge"/>.</summary>
    public int Expire(DateTime nowUtc, TimeSpan maxAge)
    {
        var removed = 0;
        foreach (var r in _pending.Values.Where(r => nowUtc - r.RequestedAt > maxAge))
        {
            if (_pending.TryRemove(r.RequestId, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
