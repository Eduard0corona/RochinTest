using System.Text.Json.Serialization;

namespace CartonPrinting.Contracts;

/// <summary>Evento <c>CartonDetected</c> v1 (spec/contracts/carton-detected.v1.schema.json).</summary>
public sealed record CartonDetectedEvent
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "CartonDetected";

    [JsonPropertyName("eventId")]
    public required Guid EventId { get; init; }

    [JsonPropertyName("correlationId")]
    public required Guid CorrelationId { get; init; }

    [JsonPropertyName("occurredAt")]
    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public required DateTime OccurredAt { get; init; }

    [JsonPropertyName("siteId")]
    public required string SiteId { get; init; }

    [JsonPropertyName("stationId")]
    public required string StationId { get; init; }

    [JsonPropertyName("plcEpoch")]
    public required long PlcEpoch { get; init; }

    [JsonPropertyName("cycleId")]
    [JsonConverter(typeof(CycleIdConverter))]
    public required ulong CycleId { get; init; }

    [JsonPropertyName("idempotencyKey")]
    public required string IdempotencyKey { get; init; }

    [JsonPropertyName("detectionOrigin")]
    public required string DetectionOrigin { get; init; }

    [JsonPropertyName("transport")]
    public required string Transport { get; init; }

    [JsonPropertyName("queueOverflow")]
    public required bool QueueOverflow { get; init; }

    [JsonPropertyName("detectionTimestamp")]
    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public DateTime? DetectionTimestamp { get; init; }

    [JsonPropertyName("receivedAt")]
    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public required DateTime ReceivedAt { get; init; }

    [JsonPropertyName("deadlineReference")]
    public required string DeadlineReference { get; init; }

    [JsonPropertyName("deadlineAt")]
    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public required DateTime DeadlineAt { get; init; }

    [JsonPropertyName("configRevision")]
    public required long ConfigRevision { get; init; }

    [JsonPropertyName("workOrderId")]
    public string? WorkOrderId { get; init; }

    [JsonPropertyName("cartonId")]
    public string? CartonId { get; init; }

    [JsonPropertyName("sourceFingerprint")]
    public required string SourceFingerprint { get; init; }

    [JsonPropertyName("opc")]
    public required OpcInfo Opc { get; init; }

    public sealed record OpcInfo
    {
        [JsonPropertyName("nodeId")]
        public required string NodeId { get; init; }

        [JsonPropertyName("statusCode")]
        public string StatusCode { get; init; } = "Good";

        [JsonPropertyName("sourceTimestamp")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? SourceTimestamp { get; init; }

        [JsonPropertyName("serverTimestamp")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? ServerTimestamp { get; init; }
    }
}
