namespace CartonPrinting.Contracts;

// Vocabulario de la especificación como constantes: los valores viajan tal cual a SQL y a los
// contratos. Las pruebas comprueban que coinciden con contracts/state-machine.json.

public static class DetectionStatus
{
    public const string Received = "RECEIVED";
    public const string WaitingErp = "WAITING_ERP";
    public const string JobCreated = "JOB_CREATED";
    public const string Blocked = "BLOCKED";
    public const string Expired = "EXPIRED";
}

public static class DetectionReason
{
    public const string IncoherentSnapshot = "INCOHERENT_SNAPSHOT";
    public const string ImplausibleRate = "IMPLAUSIBLE_RATE";
    public const string SequenceAnomaly = "SEQUENCE_ANOMALY";
    public const string StationDisabled = "STATION_DISABLED";
    public const string RecirculatedCarton = "RECIRCULATED_CARTON";
    public const string LateArrival = "LATE_ARRIVAL";
    public const string ErpUnavailable = "ERP_UNAVAILABLE";
}

public static class JobStatus
{
    public const string Ready = "READY";
    public const string Sending = "SENDING";
    public const string Transmitted = "TRANSMITTED";
    public const string RetryWait = "RETRY_WAIT";
    public const string Confirmed = "CONFIRMED";
    public const string Unknown = "UNKNOWN";
    public const string Failed = "FAILED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
    public const string ClosedUnresolved = "CLOSED_UNRESOLVED";
}

public static class JobReason
{
    public const string WriteInterrupted = "WRITE_INTERRUPTED";
    public const string RestartDuringSend = "RESTART_DURING_SEND";
    public const string EvidenceWindowElapsed = "EVIDENCE_WINDOW_ELAPSED";
    public const string SerialMismatch = "SERIAL_MISMATCH";
    public const string ExceptionalReprintUnresolved = "EXCEPTIONAL_REPRINT_UNRESOLVED";
    public const string PrinterUnavailable = "PRINTER_UNAVAILABLE";
    public const string DeviceReportedNotPrinted = "DEVICE_REPORTED_NOT_PRINTED";
    public const string ReconciledNotPrinted = "RECONCILED_NOT_PRINTED";
    public const string DeadlinePassed = "DEADLINE_PASSED";
    public const string OutOfOrder = "OUT_OF_ORDER";
    public const string CancelledByOperator = "CANCELLED_BY_OPERATOR";
    public const string ClosedCartonRejected = "CLOSED_CARTON_REJECTED";
    public const string ClosedCartonQuarantined = "CLOSED_CARTON_QUARANTINED";
}

public static class AttemptOutcome
{
    public const string Sending = "SENDING";
    public const string NotSent = "NOT_SENT";
    public const string Transmitted = "TRANSMITTED";
    public const string Unknown = "UNKNOWN";
    public const string Confirmed = "CONFIRMED";
    public const string NotPrinted = "NOT_PRINTED";
}

public static class AttemptKind
{
    public const string Ordinary = "ORDINARY";
    public const string ExceptionalReprint = "EXCEPTIONAL_REPRINT";
}

public static class GapStatus
{
    public const string Open = "OPEN";
    public const string Recovered = "RECOVERED";
    public const string ClosedUnrecovered = "CLOSED_UNRECOVERED";
}

public static class AuthorizationStatus
{
    public const string Approved = "APPROVED";
    public const string Executed = "EXECUTED";
    public const string Revoked = "REVOKED";
    public const string Expired = "EXPIRED";
}

public static class DetectionOrigin
{
    public const string Observed = "OBSERVED";
    public const string Recovered = "RECOVERED";
}

public static class Transport
{
    public const string Subscription = "SUBSCRIPTION";
    public const string Republish = "REPUBLISH";
    public const string PlcFifo = "PLC_FIFO";
    public const string JournalReplay = "JOURNAL_REPLAY";
    public const string Read = "READ";
    public const string AnomalyReingest = "ANOMALY_REINGEST";

    public static string OriginOf(string transport) =>
        transport == Subscription ? DetectionOrigin.Observed : DetectionOrigin.Recovered;
}

public static class DeadlineReference
{
    public const string PlcTimestamp = "PLC_TIMESTAMP";
    public const string GatewayReceipt = "GATEWAY_RECEIPT";
}

public static class EvidenceLevel
{
    public const string None = "NONE";
    public const string Printed = "PRINTED";
    public const string Applied = "APPLIED";
    public const string Verified = "VERIFIED";

    /// <summary>Orden de los niveles: PRINTED 1, APPLIED 2, VERIFIED 3 (NONE 0).</summary>
    public static int Rank(string level) => level switch
    {
        Printed => 1,
        Applied => 2,
        Verified => 3,
        _ => 0,
    };
}

public static class EvidenceType
{
    public const string PrinterJobCompletion = "PRINTER_JOB_COMPLETION";
    public const string PlcApplicatorConfirmation = "PLC_APPLICATOR_CONFIRMATION";
    public const string BarcodeVerification = "BARCODE_VERIFICATION";
    public const string ManualInspection = "MANUAL_INSPECTION";
}

public static class CorrelatedTo
{
    public const string Job = "JOB";
    public const string Cycle = "CYCLE";
}

public static class AnomalyType
{
    public const string IdentityCollision = "IDENTITY_COLLISION";
    public const string ResetSuspected = "RESET_SUSPECTED";
    public const string EpochUnexpected = "EPOCH_UNEXPECTED";
    public const string AmbiguousDecrease = "AMBIGUOUS_DECREASE";
}

public static class AnomalySeverity
{
    public const string Blocking = "BLOCKING";
    public const string Warning = "WARNING";
}

public static class BlockReason
{
    public const string IdentityCollision = "IDENTITY_COLLISION";
    public const string ResetSuspected = "RESET_SUSPECTED";
    public const string EpochUnexpected = "EPOCH_UNEXPECTED";
    public const string AmbiguousDecrease = "AMBIGUOUS_DECREASE";
    public const string ClockSkew = "CLOCK_SKEW";
    public const string ClockJump = "CLOCK_JUMP";
    public const string PrinterOwnership = "PRINTER_OWNERSHIP";
    public const string Operator = "OPERATOR";
}

public static class OperatorActionType
{
    public const string CancelJob = "CANCEL_JOB";
    public const string ReconcileNegative = "RECONCILE_NEGATIVE";
    public const string CloseUnresolved = "CLOSE_UNRESOLVED";
    public const string UnblockStation = "UNBLOCK_STATION";
    public const string ConfirmEpoch = "CONFIRM_EPOCH";
    public const string ResolveAnomaly = "RESOLVE_ANOMALY";
}

public static class ResultOutcome
{
    public const string Confirmed = "CONFIRMED";
    public const string Transmitted = "TRANSMITTED";
    public const string Unknown = "UNKNOWN";
    public const string Failed = "FAILED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
    public const string ClosedUnresolved = "CLOSED_UNRESOLVED";
    public const string Blocked = "BLOCKED";
}
