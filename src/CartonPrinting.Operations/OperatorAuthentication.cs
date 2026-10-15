using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CartonPrinting.Operations;

/// <summary>
/// Personas autorizadas a usar la Operations API: identificador y SHA-256 (hex) de su clave personal. Las claves
/// las entrega la configuración del sitio (variables de entorno o un almacén de secretos), nunca el repositorio.
/// El proveedor de identidad definitivo es una decisión de OT/TI.
/// </summary>
public sealed class OperatorAuthenticationOptions : AuthenticationSchemeOptions
{
    public Dictionary<string, string> OperatorKeySha256 { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Autenticación por persona: cabeceras <c>X-Operator-Id</c> y <c>X-Operator-Key</c>. La clave se compara por su
/// hash en tiempo constante. La identidad autenticada es la que queda en <c>OperatorAction.PerformedBy</c> y en
/// <c>ReprintAuthorization</c>: el cuerpo de la petición nunca la decide.
/// </summary>
public sealed class OperatorAuthenticationHandler(
    IOptionsMonitor<OperatorAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<OperatorAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "OperatorKey";
    public const string IdHeader = "X-Operator-Id";
    public const string KeyHeader = "X-Operator-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdHeader, out var idValues) || !Request.Headers.TryGetValue(KeyHeader, out var keyValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var id = idValues.ToString();
        var key = keyValues.ToString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrEmpty(key) || !Options.OperatorKeySha256.TryGetValue(id, out var expectedHex)
            || !Matches(key, expectedHex))
        {
            return Task.FromResult(AuthenticateResult.Fail("Persona o clave inválida."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, id)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    /// <summary>SHA-256 en hexadecimal de una clave (para preparar la configuración).</summary>
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static bool Matches(string key, string expectedHex)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), expected);
    }
}
