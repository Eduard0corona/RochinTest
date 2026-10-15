using System.Globalization;
using System.Text;

namespace CartonPrinting.Contracts;

/// <summary>
/// Serialización canónica JCS (RFC 8785) para los valores que usa el sistema: null, booleanos,
/// cadenas, enteros, arreglos y objetos. Los números no enteros no se admiten: JCS exige el
/// formato de ECMAScript y ningún valor de la huella ni de los contratos lo necesita.
/// </summary>
/// <remarks>
/// System.Text.Json escapa por defecto + &amp; &lt; &gt; ' y los caracteres no ASCII; JCS solo escapa
/// comillas, barra invertida y caracteres de control (RFC 8785, sección 3.2.2.2).
/// </remarks>
public static class Jcs
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    public static byte[] SerializeToUtf8(object? value) => Encoding.UTF8.GetBytes(Serialize(value));

    private static void Write(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case System.Numerics.BigInteger bi:
                sb.Append(bi.ToString(CultureInfo.InvariantCulture));
                break;
            case IReadOnlyDictionary<string, object?> map:
                WriteObject(sb, map);
                break;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                var first = true;
                foreach (var item in list)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    Write(sb, item);
                }

                sb.Append(']');
                break;
            default:
                throw new NotSupportedException($"JCS: tipo no admitido {value.GetType().Name}.");
        }
    }

    private static void WriteObject(StringBuilder sb, IReadOnlyDictionary<string, object?> map)
    {
        // RFC 8785 3.2.3: propiedades ordenadas por sus unidades de código UTF-16.
        var keys = map.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);
        sb.Append('{');
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            WriteString(sb, keys[i]);
            sb.Append(':');
            Write(sb, map[keys[i]]);
        }

        sb.Append('}');
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
