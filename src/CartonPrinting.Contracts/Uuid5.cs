using System.Security.Cryptography;
using System.Text;

namespace CartonPrinting.Contracts;

/// <summary>
/// UUID versión 5 (RFC 9562, sección 5.5): SHA-1 de los 16 bytes del namespace en orden de red
/// seguidos del nombre en UTF-8, con versión 5 y variante RFC. Los bytes son big-endian.
/// </summary>
public static class Uuid5
{
    public static Guid Create(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        Span<byte> namespaceBytes = stackalloc byte[16];
        if (!namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _))
        {
            throw new InvalidOperationException("No se pudo serializar el namespace UUID.");
        }

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input, 16);

        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // SHA-1 es el algoritmo que exige RFC 9562 para UUIDv5; no se usa con fines criptográficos.
        SHA1.HashData(input, hash);
#pragma warning restore CA5350

        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // versión 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variante RFC 9562
        return new Guid(bytes, bigEndian: true);
    }
}
