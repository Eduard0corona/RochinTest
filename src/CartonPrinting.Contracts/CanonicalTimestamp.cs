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
    /// Interpreta una fecha RFC 3339 (<c>Z</c> o desplazamiento <c>±HH:MM</c>) con la semántica de
    /// <c>datetime.fromisoformat</c> (Python 3.14) que usa tools/verify_package.py: microsegundos
    /// (las cifras de más se truncan), hora 24 solo como 24:00:00 (medianoche del día siguiente) y
    /// desplazamientos estrictamente menores que 24 horas. El resultado está en UTC.
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

        var offsetMinutes = 0;
        if (m.Groups["oh"].Success)
        {
            offsetMinutes = (Part("oh") * 60) + Part("om");
            if (offsetMinutes >= 24 * 60)
            {
                return false;
            }

            if (m.Groups["sign"].Value == "-")
            {
                offsetMinutes = -offsetMinutes;
            }
        }

        var hour = Part("h");
        var minute = Part("mi");
        var second = Part("s");
        var nextDay = false;
        if (hour == 24)
        {
            if (minute != 0 || second != 0 || micro != 0)
            {
                return false;
            }

            hour = 0;
            nextDay = true;
        }

        try
        {
            var local = new DateTime(Part("y"), Part("mo"), Part("d"), hour, minute, second, DateTimeKind.Unspecified).AddTicks(micro * 10L);
            if (nextDay)
            {
                local = local.AddDays(1);
            }

            var utc = DateTime.SpecifyKind(local.AddMinutes(-offsetMinutes), DateTimeKind.Utc);
            result = new DateTimeOffset(utc, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?<y>[0-9]{4})-(?<mo>[0-9]{2})-(?<d>[0-9]{2})T(?<h>[0-9]{2}):(?<mi>[0-9]{2}):(?<s>[0-9]{2})(\.(?<frac>[0-9]+))?(Z|(?<sign>[+-])(?<oh>[0-9]{2}):(?<om>[0-9]{2}))\z", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339();
}
