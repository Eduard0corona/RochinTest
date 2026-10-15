using System.Diagnostics;

namespace CartonPrinting.Infrastructure.Observability;

/// <summary>
/// Fuente de trazas de OpenTelemetry. Una traza por ciclo con <c>idempotencyKey</c> como atributo
/// (docs/ARQUITECTURA_TECNICA.md, sección 9); se propagan correlationId, eventId, printJobId,
/// attemptId y labelSerial.
/// </summary>
public static class CartonTelemetry
{
    public const string SourceName = "CartonPrinting";

    public static readonly ActivitySource ActivitySource = new(SourceName, "1.1");

    public static class Tags
    {
        public const string CorrelationId = "correlationId";
        public const string EventId = "eventId";
        public const string IdempotencyKey = "idempotencyKey";
        public const string PrintJobId = "printJobId";
        public const string AttemptId = "attemptId";
        public const string LabelSerial = "labelSerial";
        public const string PrinterId = "printerId";
        public const string StationId = "stationId";
    }
}
