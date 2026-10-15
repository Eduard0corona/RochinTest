using System.Globalization;
using System.Text.RegularExpressions;

namespace CartonPrinting.Contracts;

/// <summary>
/// Instantes en la forma canónica del sistema: UTC truncado a milisegundos,
/// <c>YYYY-MM-DDTHH:MM:SS.fffZ</c>. Todo instante se trunca antes de usarse en la huella y antes de
/// enviarse a SQL: <c>DATETIME2(3)</c> redondea, y lo almacenado dejaría de coincidir con la huella.
/// </summary>
public static partial class CanonicalTimestamp
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Convierte a UTC y trunca a milisegundos (nunca redondea).</summary>
    public static DateTime Truncate(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => throw new ArgumentException("El instante debe declarar su zona (Kind = Utc).", nameof(value)),
        };
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    public static DateTime Truncate(DateTimeOffset value) =>
        Truncate(value.UtcDateTime);

    public static string ToCanonicalString(DateTime value) =>
        Truncate(value).ToString(Format, CultureInfo.InvariantCulture);

    public static string ToCanonicalString(DateTimeOffset value) => ToCanonicalString(value.UtcDateTime);

    /// <summary>
    /// Interpreta una fecha RFC 3339 (<c>Z</c> o desplazamiento <c>±HH:MM</c>) con precisión de
    /// microsegundos, como <c>datetime.fromisoformat</c> en tools/verify_package.py.
    /// </summary>
    public static DateTimeOffset ParseRfc3339(string value)
    {
        if (!TryParseRfc3339(value, out var result))
        {
            throw new FormatException($"No es una fecha RFC 3339: '{value}'.");
        }

        return result;
    }

    public static bool TryParseRfc3339(string? value, out DateTimeOffset result)
    {
        result = default;
        if (value is null)
        {
            return false;
        }

        var m = Rfc3339().Match(value);
        if (!m.Success)
        {
            return false;
        }

        int Part(string name) => int.Parse(m.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture);

        var micro = 0;
        if (m.Groups["frac"].Success)
        {
            var frac = m.Groups["frac"].Value;
            frac = frac.Length >= 6 ? frac[..6] : frac.PadRight(6, '0');
            micro = int.Parse(frac, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        var offset = TimeSpan.Zero;
        if (m.Groups["oh"].Success)
        {
            var oh = Part("oh");
            var om = Part("om");
            if (oh > 23 || om > 59)
            {
                return false;
            }

            offset = new TimeSpan(oh, om, 0);
            if (m.Groups["sign"].Value == "-")
            {
                offset = -offset;
            }
        }

        try
        {
            var local = new DateTime(Part("y"), Part("mo"), Part("d"), Part("h"), Part("mi"), Part("s"), DateTimeKind.Unspecified)
                .AddTicks(micro * 10L);
            result = new DateTimeOffset(local, offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?<y>[0-9]{4})-(?<mo>[0-9]{2})-(?<d>[0-9]{2})T(?<h>[0-9]{2}):(?<mi>[0-9]{2}):(?<s>[0-9]{2})(\.(?<frac>[0-9]+))?(Z|(?<sign>[+-])(?<oh>[0-9]{2}):(?<om>[0-9]{2}))$", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339();
}
