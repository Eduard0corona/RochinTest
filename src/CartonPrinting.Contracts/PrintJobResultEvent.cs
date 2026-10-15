using System.Text.Json.Serialization;

namespace CartonPrinting.Contracts;

/// <summary>Evento <c>PrintJobResult</c> v1 (spec/contracts/print-job-result.v1.schema.json).</summary>
public sealed record PrintJobResultEvent
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "PrintJobResult";

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

    [JsonPropertyName("labelType")]
    public required string LabelType { get; init; }

    [JsonPropertyName("resultVersion")]
    public required int ResultVersion { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("final")]
    public required bool Final { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("configRevision")]
    public required long ConfigRevision { get; init; }

    [JsonPropertyName("printJobId")]
    public Guid? PrintJobId { get; init; }

    [JsonPropertyName("attemptId")]
    public Guid? AttemptId { get; init; }

    [JsonPropertyName("labelSerial")]
    public string? LabelSerial { get; init; }

    [JsonPropertyName("workOrderId")]
    public string? WorkOrderId { get; init; }

    [JsonPropertyName("productId")]
    public string? ProductId { get; init; }

    [JsonPropertyName("lotId")]
    public string? LotId { get; init; }

    [JsonPropertyName("printerId")]
    public string? PrinterId { get; init; }

    [JsonPropertyName("cartonId")]
    public string? CartonId { get; init; }

    [JsonPropertyName("evidence")]
    public EvidenceInfo? Evidence { get; init; }

    [JsonPropertyName("exceptionalReprint")]
    public ExceptionalReprintInfo? ExceptionalReprint { get; init; }

    [JsonPropertyName("operatorAction")]
    public OperatorActionInfo? OperatorAction { get; init; }

    [JsonPropertyName("timings")]
    public TimingsInfo? Timings { get; init; }

    public sealed record EvidenceInfo
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("level")]
        public required string Level { get; init; }

        [JsonPropertyName("correlatedTo")]
        public required string CorrelatedTo { get; init; }

        [JsonPropertyName("verified")]
        public required bool Verified { get; init; }

        [JsonPropertyName("readSerial")]
        public string? ReadSerial { get; init; }

        [JsonPropertyName("reference")]
        public required string Reference { get; init; }

        [JsonPropertyName("observedAt")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public required DateTime ObservedAt { get; init; }
    }

    public sealed record ExceptionalReprintInfo
    {
        [JsonPropertyName("authorizationId")]
        public required long AuthorizationId { get; init; }

        [JsonPropertyName("duplicateRisk")]
        public required string DuplicateRisk { get; init; }

        [JsonPropertyName("reason")]
        public required string Reason { get; init; }

        [JsonPropertyName("approvedBy")]
        public required string ApprovedBy { get; init; }
    }

    public sealed record OperatorActionInfo
    {
        [JsonPropertyName("operatorActionId")]
        public required long OperatorActionId { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("performedBy")]
        public required string PerformedBy { get; init; }

        [JsonPropertyName("performedAt")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public required DateTime PerformedAt { get; init; }
    }

    public sealed record TimingsInfo
    {
        [JsonPropertyName("detectionTimestamp")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? DetectionTimestamp { get; init; }

        [JsonPropertyName("receivedAt")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? ReceivedAt { get; init; }

        [JsonPropertyName("jobReadyAt")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? JobReadyAt { get; init; }

        [JsonPropertyName("sendStartAt")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? SendStartAt { get; init; }

        [JsonPropertyName("writeCompletedAt")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? WriteCompletedAt { get; init; }

        [JsonPropertyName("evidenceAt")]
        [JsonConverter(typeof(UtcMillisecondsConverter))]
        public DateTime? EvidenceAt { get; init; }
    }
}
