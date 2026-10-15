using System.Threading.Channels;

namespace CartonPrinting.OpcIngest;

/// <summary>
/// Saturación de la ingesta para LabelingReady (ARQUITECTURA_TECNICA §2): el canal entre el callback OPC y el
/// consumidor está al 90 % o más, o se descartaron notificaciones desde la consulta anterior.
/// </summary>
public sealed class ChannelPressure(ChannelReader<RawNotification> reader, int capacity, Func<long> droppedNotifications)
{
    private long _droppedSeen;

    public bool Overloaded()
    {
        var dropped = droppedNotifications();
        var lost = dropped > Interlocked.Exchange(ref _droppedSeen, dropped);
        return lost || reader.Count >= (long)capacity * 9 / 10;
    }
}
