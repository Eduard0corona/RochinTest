using System.Globalization;

namespace CartonPrinting.Contracts;

/// <summary>
/// Número JSON con parte decimal o exponente. <c>json.loads</c> de Python lo lee como <c>float</c>
/// y <c>str</c>, <c>repr</c> y <c>json.dumps</c> lo escriben con la representación más corta. Las
/// reglas de negocio lo reproducen para dar el mismo resultado que tools/verify_package.py ante un
/// documento que use, por ejemplo, <c>42.0</c> donde se espera un entero.
/// </summary>
public sealed record PythonFloat(double Value)
{
    /// <summary><c>repr(float)</c>: fija si -4 &lt; decpt ≤ 16; si no, exponente con al menos dos dígitos.</summary>
    public string Repr
    {
        get
        {
            if (double.IsNaN(Value))
            {
                return "nan";
            }

            if (double.IsInfinity(Value))
            {
                return Value > 0 ? "inf" : "-inf";
            }

            var negative = Value < 0 || (Value == 0 && double.IsNegative(Value));
            var r = Math.Abs(Value).ToString("R", CultureInfo.InvariantCulture);
            var exponent = 0;
            var e = r.IndexOfAny(['E', 'e']);
            if (e >= 0)
            {
                exponent = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                r = r[..e];
            }

            var dot = r.IndexOf('.', StringComparison.Ordinal);
            var digits = dot < 0 ? r : r.Remove(dot, 1);
            var decpt = (dot < 0 ? r.Length : dot) + exponent;
            var leading = digits.Length - digits.TrimStart('0').Length;
            digits = digits.TrimStart('0');
            decpt -= leading;
            digits = digits.TrimEnd('0');

            string body;
            if (digits.Length == 0)
            {
                body = "0.0";
            }
            else if (decpt > -4 && decpt <= 16)
            {
                body = decpt <= 0
                    ? "0." + new string('0', -decpt) + digits
                    : decpt >= digits.Length
                        ? digits + new string('0', decpt - digits.Length) + ".0"
                        : digits[..decpt] + "." + digits[decpt..];
            }
            else
            {
                var exp = decpt - 1;
                var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
                body = mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
            }

            return negative ? "-" + body : body;
        }
    }

    /// <summary><c>json.dumps(float)</c>: igual a <c>repr</c> salvo infinitos y NaN.</summary>
    public string JsonRepr => double.IsNaN(Value) ? "NaN" : double.IsInfinity(Value) ? (Value > 0 ? "Infinity" : "-Infinity") : Repr;

    /// <summary>¿El texto de un número JSON es un <c>float</c> para Python?</summary>
    public static bool IsFloatText(string raw) => raw.AsSpan().IndexOfAny(".eE") >= 0;

    public static PythonFloat Parse(string raw) => new(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
}
