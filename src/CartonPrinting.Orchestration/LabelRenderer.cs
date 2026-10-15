using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CartonPrinting.Contracts;
using CartonPrinting.Infrastructure.Data;

namespace CartonPrinting.Orchestration;

/// <summary>Payload renderizado y su hash (SHA-256 de sus bytes UTF-8).</summary>
public sealed record RenderedLabel(string Payload, byte[] PayloadHash);

/// <summary>
/// Render del payload de la etiqueta con el serial ya incluido (el serial se obtiene antes de TX-A).
/// Plantilla ZPL de laboratorio para el simulador; la plantilla real de planta es una decisión de
/// integración y queda identificada por TemplateRevision.
/// </summary>
public static class LabelRenderer
{
    public static RenderedLabel Render(StationRevision rev, ErpSnapshot snapshot, CycleIdentity cycle, string labelSerial, string? cartonId)
    {
        var sb = new StringBuilder();
        sb.Append("^XA\n");
        sb.Append(CultureInfo.InvariantCulture, $"^FX CartonPrinting template={rev.TemplateRevision} labelType={rev.LabelType}\n");
        sb.Append(CultureInfo.InvariantCulture, $"^FO40,40^A0N,36,36^FDPRODUCT {snapshot.ProductId}^FS\n");
        sb.Append(CultureInfo.InvariantCulture, $"^FO40,90^A0N,36,36^FDLOT {snapshot.LotId}^FS\n");
        sb.Append(CultureInfo.InvariantCulture, $"^FO40,140^A0N,36,36^FDWO {snapshot.WorkOrderId} REV {snapshot.Revision}^FS\n");
        if (snapshot.Gtin is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"^FO40,190^A0N,36,36^FDGTIN {snapshot.Gtin}^FS\n");
        }

        if (cartonId is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"^FO40,240^A0N,36,36^FDCARTON {cartonId}^FS\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"^FO40,290^A0N,28,28^FDCYCLE {cycle.IdempotencyKey}^FS\n");
        sb.Append(CultureInfo.InvariantCulture, $"^FO40,340^BCN,80,Y,N,N^FD{labelSerial}^FS\n");
        sb.Append("^XZ\n");
        var payload = sb.ToString();
        return new RenderedLabel(payload, SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Extrae el serial del código de barras del payload (lo usan los simuladores de evidencia).</summary>
    public static string? ExtractSerial(string payload)
    {
        const string marker = "^BCN,80,Y,N,N^FD";
        var i = payload.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        var start = i + marker.Length;
        var end = payload.IndexOf("^FS", start, StringComparison.Ordinal);
        return end < 0 ? null : payload[start..end];
    }
}
