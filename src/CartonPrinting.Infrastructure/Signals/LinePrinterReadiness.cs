namespace CartonPrinting.Infrastructure.Signals;

/// <summary>
/// La impresora de línea en servicio, para LabelingReady (ESTRATEGIA_RECUPERACION §1, etapas 2, 3 y 8, y §3 Impresora). La estación
/// solo se anuncia lista al PLC si la impresora de línea está tomada por este proceso (lock y lease), ya pasó TX-R0 y su sender no
/// está en pausa por fallos seguidos del dispositivo. Al recuperar el lease después de perderlo (por ejemplo, durante una caída de
/// SQL Server más larga que su cobertura), LabelingReady vuelve recién con TX-R0 hecho.
/// </summary>
public sealed class LinePrinterReadiness
{
    private Func<bool> _inService = () => false;
    private volatile bool _paused;

    /// <summary>Lo conecta el ciclo de propiedad de la impresora de línea.</summary>
    public void Attach(Func<bool> inService) => _inService = inService;

    /// <summary>El sender de línea entra en pausa (tres fallos seguidos del dispositivo) o sale de ella.</summary>
    public void SetPaused(bool paused) => _paused = paused;

    public bool Paused => _paused;

    public bool InService => _inService();

    public bool Ready => _inService() && !_paused;
}
