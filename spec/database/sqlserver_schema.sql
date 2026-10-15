-- =====================================================================================
-- CartonPrinting v1.1 · SQL Server 2022 · ESQUEMA DE REFERENCIA
-- Estado de verificación: parseado con sqlfluff (dialecto tsql) y revisado por
-- tools/verify_package.py. NO ejecutado contra SQL Server en esta revisión
-- (ver docs/REVISION_LOG_v1.1.md). Se ejecuta por primera vez en la Fase 1 con Testcontainers (T47).
--
-- Convenciones
--   * Fechas UTC en DATETIME2(3). Las decisiones de plazo usan @EdgeNowUtc, el reloj
--     del EdgeHost, nunca SYSUTCDATETIME() del servidor SQL (ADR 0006).
--   * Estados, motivos y transiciones: contracts/state-machine.json es la fuente.
--     Este archivo y database/sqlserver_state_transitions.sql deben coincidir con él.
--   * CHECK solo valida la fila. Las reglas entre tablas las aplican los triggers
--     TR_*_Guard y TR_*_StateMachine, o la transacción indicada en "Regla entre tablas".
--   * En los CHECK con columnas anulables se escribe "X IS NOT NULL AND X IN (...)"
--     porque un CHECK que evalúa UNKNOWN se acepta.
--   * Todo trigger AFTER UPDATE o DELETE empieza con la guarda de 0 filas: un UPDATE que
--     no afecta filas también dispara el trigger, con inserted y deleted vacíos.
--   * Después de un DML sobre una tabla con triggers no se usa @@ROWCOUNT ni OUTPUT sin
--     INTO: se usa OUTPUT ... INTO una variable de tabla (Msg 334).
--   * Las conexiones de la aplicación deben usar ANSI_NULLS y QUOTED_IDENTIFIER en ON
--     (valor por defecto de SqlClient). Con sqlcmd, ejecutar con -I. Los índices
--     filtrados fallan con error 1934 si no.
-- Orden de ejecución: este archivo y después sqlserver_state_transitions.sql.
-- =====================================================================================

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
GO

-- -------------------------------------------------------------------------------------
-- 1. Máquina de estados aplicada por la base de datos (contenido generado aparte)
-- -------------------------------------------------------------------------------------
-- Kind = modo de la transición en contracts/state-machine.json. Los triggers exigen:
--   AUTO       ningún respaldo adicional
--   MANUAL     una OperatorAction nueva y coherente
--   EXCEPTION  SESSION_CONTEXT 'EXCEPTION' y un intento EXCEPTIONAL_REPRINT del job
--   CORRECTION SESSION_CONTEXT 'CORRECTION' y una EvidenceObservation contradictoria
CREATE TABLE dbo.AllowedTransition (
    Machine      VARCHAR(30) NOT NULL,
    FromStatus   VARCHAR(20) NOT NULL,   -- '(create)' para la creación
    ToStatus     VARCHAR(20) NOT NULL,
    Kind         VARCHAR(12) NOT NULL,
    TransitionId VARCHAR(8)  NOT NULL,
    CONSTRAINT PK_AllowedTransition PRIMARY KEY (Machine, FromStatus, ToStatus, Kind),
    CONSTRAINT CK_AllowedTransition_Kind CHECK (Kind IN ('AUTO', 'MANUAL', 'EXCEPTION', 'CORRECTION'))
);
GO

-- -------------------------------------------------------------------------------------
-- 2. Configuración versionada de estación (H9, ADR 0011)
-- -------------------------------------------------------------------------------------
CREATE TABLE dbo.StationConfigRevision (
    ConfigRevisionId     BIGINT IDENTITY (1, 1) NOT NULL,
    SiteId               NVARCHAR(64)  NOT NULL,
    StationId            NVARCHAR(64)  NOT NULL,
    RevisionNumber       INT           NOT NULL,
    PlcEndpointId        NVARCHAR(128) NOT NULL,
    PrinterId            NVARCHAR(64)  NOT NULL,      -- impresora de línea
    ManualPrinterId      NVARCHAR(64)  NULL,          -- impresora fuera de línea para reimpresiones (ADR 0012)
    LabelType            NVARCHAR(64)  NOT NULL,
    TemplateRevision     NVARCHAR(64)  NOT NULL,
    DeadlineReference    VARCHAR(20)   NOT NULL,
    TransitBudgetMs      INT           NOT NULL,      -- sensor a punto de aplicación (OT-03)
    DeviceLeadTimeMs     INT           NOT NULL,      -- p99 inicio de envío a etiqueta aplicada, medido
    SendMarginMs         INT           NOT NULL,      -- pausa del proceso tras la segunda comprobación
    ClockSkewToleranceMs INT           NULL,
    MaxUpstreamLatencyMs INT           NULL,
    ClockJumpToleranceMs INT           NOT NULL,      -- divergencia UtcNow contra reloj monotónico
    MinCycleIntervalMs   INT           NULL,          -- NULL: regla de rebote desactivada
    MaxAutoAttempts      TINYINT       NOT NULL,
    RetryBackoffMs       INT           NOT NULL,
    MinConfirmationLevel VARCHAR(10)   NOT NULL,
    EvidenceWindowMs     INT           NULL,
    EpochChangePolicy    VARCHAR(30)   NOT NULL,
    CartonIdentityMode   VARCHAR(30)   NOT NULL,
    GapGraceMs           INT           NOT NULL,
    PhysicalGateMode     VARCHAR(20)   NOT NULL,      -- NONE | PLC_WINDOW_TAG (ADR 0006)
    GateFreshnessMs      INT           NULL,          -- antigüedad máxima del tag de ventana
    CreatedBy            NVARCHAR(128) NOT NULL,
    ApprovedBy           NVARCHAR(128) NOT NULL,
    CreatedAt            DATETIME2(3)  NOT NULL CONSTRAINT DF_StationConfigRevision_CreatedAt DEFAULT SYSUTCDATETIME(),
    ChangeReason         NVARCHAR(512) NOT NULL,
    CONSTRAINT PK_StationConfigRevision PRIMARY KEY (ConfigRevisionId),
    CONSTRAINT UQ_StationConfigRevision_Number UNIQUE (SiteId, StationId, RevisionNumber),
    CONSTRAINT UQ_StationConfigRevision_Station UNIQUE (ConfigRevisionId, SiteId, StationId),
    CONSTRAINT UQ_StationConfigRevision_Reference UNIQUE (ConfigRevisionId, SiteId, StationId, DeadlineReference),
    CONSTRAINT CK_StationConfigRevision_DeadlineReference CHECK (DeadlineReference IN ('PLC_TIMESTAMP', 'GATEWAY_RECEIPT')),
    CONSTRAINT CK_StationConfigRevision_DeadlineParams CHECK (
           (DeadlineReference = 'PLC_TIMESTAMP' AND ClockSkewToleranceMs IS NOT NULL AND ClockSkewToleranceMs >= 0)
        OR (DeadlineReference = 'GATEWAY_RECEIPT' AND MaxUpstreamLatencyMs IS NOT NULL AND MaxUpstreamLatencyMs >= 0)),
    CONSTRAINT CK_StationConfigRevision_Times CHECK (
            TransitBudgetMs > 0 AND SendMarginMs >= 0 AND DeviceLeadTimeMs >= 0
        AND SendMarginMs + DeviceLeadTimeMs < TransitBudgetMs
        AND ClockJumpToleranceMs > 0 AND RetryBackoffMs >= 0 AND GapGraceMs >= 0
        AND (MinCycleIntervalMs IS NULL OR MinCycleIntervalMs > 0)),
    CONSTRAINT CK_StationConfigRevision_Attempts CHECK (MaxAutoAttempts BETWEEN 1 AND 10),
    CONSTRAINT CK_StationConfigRevision_Level CHECK (MinConfirmationLevel IN ('NONE', 'PRINTED', 'APPLIED', 'VERIFIED')),
    CONSTRAINT CK_StationConfigRevision_EvidenceWindow CHECK (
           (MinConfirmationLevel = 'NONE' AND EvidenceWindowMs IS NULL)
        OR (MinConfirmationLevel <> 'NONE' AND EvidenceWindowMs IS NOT NULL AND EvidenceWindowMs > 0)),
    CONSTRAINT CK_StationConfigRevision_EpochPolicy CHECK (EpochChangePolicy IN ('AUTO_INCREMENT_BY_ONE', 'REQUIRE_CONFIRMATION')),
    CONSTRAINT CK_StationConfigRevision_CartonIdentity CHECK (CartonIdentityMode IN ('CYCLE_ONLY', 'CARTON_ID_REQUIRED')),
    CONSTRAINT CK_StationConfigRevision_Gate CHECK (
           (PhysicalGateMode = 'NONE' AND GateFreshnessMs IS NULL)
        OR (PhysicalGateMode = 'PLC_WINDOW_TAG' AND GateFreshnessMs IS NOT NULL AND GateFreshnessMs > 0)),
    CONSTRAINT CK_StationConfigRevision_ManualPrinter CHECK (ManualPrinterId IS NULL OR ManualPrinterId <> PrinterId),
    CONSTRAINT CK_StationConfigRevision_TwoPersons CHECK (ApprovedBy <> CreatedBy)
);
GO

-- Activar una configuración = insertar una revisión nueva y mover el puntero de
-- StationConfig en la misma transacción. Nunca se edita una revisión.
CREATE TRIGGER dbo.TR_StationConfigRevision_Immutable
ON dbo.StationConfigRevision
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51001, N'StationConfigRevision es inmutable: cree una revisión nueva.', 1;
END;
GO

-- Estado operativo de la estación. No es configuración: un bloqueo no crea revisión.
CREATE TABLE dbo.StationConfig (
    SiteId                 NVARCHAR(64)  NOT NULL,
    StationId              NVARCHAR(64)  NOT NULL,
    ActiveConfigRevisionId BIGINT        NOT NULL,
    LabelingEnabled        BIT           NOT NULL,
    BlockReason            VARCHAR(30)   NULL,
    BlockedAt              DATETIME2(3)  NULL,
    BlockedBy              NVARCHAR(128) NULL,      -- 'SYSTEM' o persona
    BlockedByAnomalyId     BIGINT        NULL,
    LastUnblockedBy        NVARCHAR(128) NULL,
    LastUnblockedAt        DATETIME2(3)  NULL,
    LastOperatorActionId   BIGINT        NULL,      -- respaldo del último desbloqueo (TR_StationConfig_Guard)
    RowVersion             ROWVERSION    NOT NULL,
    CONSTRAINT PK_StationConfig PRIMARY KEY (SiteId, StationId),
    CONSTRAINT FK_StationConfig_Revision FOREIGN KEY (ActiveConfigRevisionId, SiteId, StationId)
        REFERENCES dbo.StationConfigRevision (ConfigRevisionId, SiteId, StationId),
    CONSTRAINT CK_StationConfig_Block CHECK (
           (LabelingEnabled = 1 AND BlockReason IS NULL AND BlockedAt IS NULL AND BlockedBy IS NULL)
        OR (LabelingEnabled = 0 AND BlockReason IS NOT NULL AND BlockedAt IS NOT NULL AND BlockedBy IS NOT NULL)),
    CONSTRAINT CK_StationConfig_BlockReason CHECK (
        BlockReason IS NULL
        OR BlockReason IN ('IDENTITY_COLLISION', 'RESET_SUSPECTED', 'EPOCH_UNEXPECTED', 'AMBIGUOUS_DECREASE',
                           'CLOCK_SKEW', 'CLOCK_JUMP', 'PRINTER_OWNERSHIP', 'OPERATOR'))
);
GO

-- -------------------------------------------------------------------------------------
-- 3. Épocas del contador del PLC (H4, ADR 0007)
-- -------------------------------------------------------------------------------------
-- El orden entre épocas lo da AcceptedAt, no el valor numérico: una época administrada
-- por el gateway no tiene por qué crecer.
CREATE TABLE dbo.PlcEpochHistory (
    SiteId           NVARCHAR(64)  NOT NULL,
    StationId        NVARCHAR(64)  NOT NULL,
    PlcEpoch         BIGINT        NOT NULL,      -- UInt32 del PLC o época del gateway
    EpochSource      VARCHAR(10)   NOT NULL,      -- PLC | GATEWAY
    AcceptanceMode   VARCHAR(30)   NOT NULL,
    FirstCycleId     DECIMAL(20, 0) NOT NULL,
    AcceptedAt       DATETIME2(3)  NOT NULL,
    AcceptedBy       NVARCHAR(128) NOT NULL,      -- 'SYSTEM' o persona
    ConfigRevisionId BIGINT        NOT NULL,
    OperatorActionId BIGINT        NULL,          -- CONFIRM_EPOCH cuando la acepta una persona
    CONSTRAINT PK_PlcEpochHistory PRIMARY KEY (SiteId, StationId, PlcEpoch),
    CONSTRAINT UQ_PlcEpochHistory_Order UNIQUE (SiteId, StationId, AcceptedAt),
    CONSTRAINT FK_PlcEpochHistory_Revision FOREIGN KEY (ConfigRevisionId, SiteId, StationId)
        REFERENCES dbo.StationConfigRevision (ConfigRevisionId, SiteId, StationId),
    CONSTRAINT CK_PlcEpochHistory_Range CHECK (PlcEpoch BETWEEN 0 AND 4294967295),
    CONSTRAINT CK_PlcEpochHistory_Source CHECK (EpochSource IN ('PLC', 'GATEWAY')),
    CONSTRAINT CK_PlcEpochHistory_Mode CHECK (AcceptanceMode IN ('INITIAL', 'AUTO_INCREMENT_BY_ONE', 'OPERATOR_CONFIRMED')),
    CONSTRAINT CK_PlcEpochHistory_Operator CHECK (
        AcceptanceMode <> 'OPERATOR_CONFIRMED' OR (AcceptedBy <> N'SYSTEM' AND OperatorActionId IS NOT NULL))
);
GO

-- -------------------------------------------------------------------------------------
-- 4. Snapshots ERP (caché local; nunca se consulta al ERP por caja)
-- -------------------------------------------------------------------------------------
CREATE TABLE dbo.ErpWorkOrderCache (
    WorkOrderId NVARCHAR(128) NOT NULL,
    Revision    NVARCHAR(64)  NOT NULL,
    ProductId   NVARCHAR(128) NOT NULL,
    LotId       NVARCHAR(128) NOT NULL,
    Gtin        CHAR(14)      NULL,
    ValidFrom   DATETIME2(3)  NOT NULL,
    ValidUntil  DATETIME2(3)  NOT NULL,
    ReceivedAt  DATETIME2(3)  NOT NULL,
    SourceHash  BINARY(32)    NOT NULL,
    CONSTRAINT PK_ErpWorkOrderCache PRIMARY KEY (WorkOrderId, Revision),
    CONSTRAINT CK_ErpWorkOrderCache_Validity CHECK (ValidUntil > ValidFrom)
);
GO

-- -------------------------------------------------------------------------------------
-- 5. Detecciones (H4, H5). Solo ciclos observados o recuperados con datos reales.
-- -------------------------------------------------------------------------------------
-- Transport: SUBSCRIPTION (notificación en vivo), REPUBLISH (notificación reenviada tras
-- una interrupción), PLC_FIFO, JOURNAL_REPLAY, READ (lectura tras reconexión) y
-- ANOMALY_REINGEST (registro retenido en SequenceAnomaly que OT liberó).
CREATE TABLE dbo.CartonDetection (
    DetectionId        UNIQUEIDENTIFIER NOT NULL,   -- = eventId de CartonDetected (UUIDv5)
    SiteId             NVARCHAR(64)     NOT NULL,
    StationId          NVARCHAR(64)     NOT NULL,
    PlcEpoch           BIGINT           NOT NULL,
    CycleId            DECIMAL(20, 0)   NOT NULL,
    IdempotencyKey     NVARCHAR(220)    NOT NULL,
    SourceFingerprint  BINARY(32)       NOT NULL,   -- SHA-256 del arreglo canónico (state-machine.json)
    Origin             VARCHAR(12)      NOT NULL,   -- OBSERVED | RECOVERED
    Transport          VARCHAR(16)      NOT NULL,
    QueueOverflow      BIT              NOT NULL,   -- bit Overflow del StatusCode de OPC UA
    DetectionTimestamp DATETIME2(3)     NULL,       -- dato latched por el PLC, no metadato OPC
    OpcSourceTimestamp DATETIME2(3)     NULL,       -- informativo; fuera de identidad y huella
    ReceivedAt         DATETIME2(3)     NOT NULL,   -- recepción original; el replay la conserva
    DeadlineReference  VARCHAR(20)      NOT NULL,   -- copia de la revisión capturada
    DeadlineAt         DATETIME2(3)     NOT NULL,
    ConfigRevisionId   BIGINT           NOT NULL,
    WorkOrderId        NVARCHAR(128)    NULL,
    CartonId           NVARCHAR(128)    NULL,
    Status             VARCHAR(20)      NOT NULL,
    Reason             VARCHAR(40)      NULL,
    UpdatedAt          DATETIME2(3)     NOT NULL CONSTRAINT DF_CartonDetection_UpdatedAt DEFAULT SYSUTCDATETIME(),
    RowVersion         ROWVERSION       NOT NULL,
    CONSTRAINT PK_CartonDetection PRIMARY KEY (DetectionId),
    CONSTRAINT UQ_CartonDetection_Identity UNIQUE (SiteId, StationId, PlcEpoch, CycleId),
    CONSTRAINT UQ_CartonDetection_IdempotencyKey UNIQUE (IdempotencyKey),
    CONSTRAINT UQ_CartonDetection_Job UNIQUE (DetectionId, SiteId, StationId, PlcEpoch, CycleId),
    CONSTRAINT FK_CartonDetection_Epoch FOREIGN KEY (SiteId, StationId, PlcEpoch)
        REFERENCES dbo.PlcEpochHistory (SiteId, StationId, PlcEpoch),
    CONSTRAINT FK_CartonDetection_Revision FOREIGN KEY (ConfigRevisionId, SiteId, StationId, DeadlineReference)
        REFERENCES dbo.StationConfigRevision (ConfigRevisionId, SiteId, StationId, DeadlineReference),
    CONSTRAINT CK_CartonDetection_Cycle CHECK (CycleId BETWEEN 0 AND 18446744073709551615),
    CONSTRAINT CK_CartonDetection_Origin CHECK (
           (Origin = 'OBSERVED' AND Transport = 'SUBSCRIPTION')
        OR (Origin = 'RECOVERED' AND Transport IN ('REPUBLISH', 'PLC_FIFO', 'JOURNAL_REPLAY', 'READ', 'ANOMALY_REINGEST'))),
    -- Con GATEWAY_RECEIPT el plazo solo es válido para una notificación en vivo y sin
    -- overflow: no hay cota de latencia para lo demás (D04 LATE_ARRIVAL).
    -- La primera notificación de un monitored item creado o transferido se registra como READ;
    -- un registro que abre un hueco tampoco recibe plazo (regla de código en TX-A).
    CONSTRAINT CK_CartonDetection_GatewayLive CHECK (
        NOT (DeadlineReference = 'GATEWAY_RECEIPT' AND (Origin = 'RECOVERED' OR QueueOverflow = 1)
             AND (Status IN ('WAITING_ERP', 'JOB_CREATED') OR DeadlineAt <> ReceivedAt))),
    -- Con PLC_TIMESTAMP y sin DetectionTimestamp no hay plazo: DeadlineAt = ReceivedAt y BLOCKED.
    CONSTRAINT CK_CartonDetection_PlcTimestamp CHECK (
        NOT (DeadlineReference = 'PLC_TIMESTAMP' AND DetectionTimestamp IS NULL
             AND (DeadlineAt <> ReceivedAt OR Status NOT IN ('RECEIVED', 'BLOCKED')))),
    CONSTRAINT CK_CartonDetection_Status CHECK (Status IN ('RECEIVED', 'WAITING_ERP', 'JOB_CREATED', 'BLOCKED', 'EXPIRED')),
    CONSTRAINT CK_CartonDetection_Reason CHECK (
           (Status = 'BLOCKED' AND Reason IS NOT NULL AND Reason IN ('INCOHERENT_SNAPSHOT', 'IMPLAUSIBLE_RATE', 'SEQUENCE_ANOMALY', 'STATION_DISABLED', 'RECIRCULATED_CARTON'))
        OR (Status = 'EXPIRED' AND Reason IS NOT NULL AND Reason IN ('LATE_ARRIVAL', 'ERP_UNAVAILABLE'))
        OR (Status NOT IN ('BLOCKED', 'EXPIRED') AND Reason IS NULL)),
    CONSTRAINT CK_CartonDetection_WorkOrder CHECK (WorkOrderId IS NOT NULL OR Status IN ('RECEIVED', 'BLOCKED'))
);
GO

CREATE INDEX IX_CartonDetection_Sweep ON dbo.CartonDetection (Status, DeadlineAt)
    WHERE Status = 'WAITING_ERP';
GO
CREATE INDEX IX_CartonDetection_HighWater ON dbo.CartonDetection (SiteId, StationId, PlcEpoch, CycleId DESC)
    INCLUDE (DetectionTimestamp, SourceFingerprint);
GO
-- Recirculación (H8): detecciones de la misma caja que todavía esperan snapshot.
CREATE INDEX IX_CartonDetection_Carton ON dbo.CartonDetection (SiteId, CartonId)
    INCLUDE (Status)
    WHERE CartonId IS NOT NULL;
GO

-- -------------------------------------------------------------------------------------
-- 6. Anomalías y huecos de secuencia (H4, H5)
-- -------------------------------------------------------------------------------------
-- Severity BLOCKING bloquea la estación; WARNING solo bloquea la detección afectada.
CREATE TABLE dbo.SequenceAnomaly (
    AnomalyId           BIGINT IDENTITY (1, 1) NOT NULL,
    SiteId              NVARCHAR(64)     NOT NULL,
    StationId           NVARCHAR(64)     NOT NULL,
    PlcEpoch            BIGINT           NOT NULL,   -- época informada por el registro, aceptada o no
    CycleId             DECIMAL(20, 0)   NULL,
    AnomalyType         VARCHAR(30)      NOT NULL,
    Severity            VARCHAR(10)      NOT NULL,
    ExistingDetectionId UNIQUEIDENTIFIER NULL,
    ExistingFingerprint BINARY(32)       NULL,
    IncomingFingerprint BINARY(32)       NOT NULL,
    IncomingRecord      NVARCHAR(MAX)    NOT NULL,   -- registro completo retenido para reingreso
    DetectedAt          DATETIME2(3)     NOT NULL,
    ConfigRevisionId    BIGINT           NOT NULL,
    ResolvedAt          DATETIME2(3)     NULL,
    ResolvedBy          NVARCHAR(128)    NULL,
    Resolution          NVARCHAR(512)    NULL,
    CONSTRAINT PK_SequenceAnomaly PRIMARY KEY (AnomalyId),
    CONSTRAINT FK_SequenceAnomaly_Detection FOREIGN KEY (ExistingDetectionId) REFERENCES dbo.CartonDetection (DetectionId),
    CONSTRAINT FK_SequenceAnomaly_Revision FOREIGN KEY (ConfigRevisionId, SiteId, StationId)
        REFERENCES dbo.StationConfigRevision (ConfigRevisionId, SiteId, StationId),
    CONSTRAINT CK_SequenceAnomaly_Type CHECK (AnomalyType IN ('IDENTITY_COLLISION', 'RESET_SUSPECTED', 'EPOCH_UNEXPECTED', 'AMBIGUOUS_DECREASE')),
    CONSTRAINT CK_SequenceAnomaly_Severity CHECK (Severity IN ('BLOCKING', 'WARNING')),
    CONSTRAINT CK_SequenceAnomaly_SeverityByType CHECK (
           (AnomalyType = 'AMBIGUOUS_DECREASE' AND Severity IN ('WARNING', 'BLOCKING'))
        OR (AnomalyType <> 'AMBIGUOUS_DECREASE' AND Severity = 'BLOCKING')),
    CONSTRAINT CK_SequenceAnomaly_Collision CHECK (
        AnomalyType <> 'IDENTITY_COLLISION'
        OR (ExistingDetectionId IS NOT NULL AND ExistingFingerprint IS NOT NULL AND ExistingFingerprint <> IncomingFingerprint)),
    CONSTRAINT CK_SequenceAnomaly_Json CHECK (ISJSON(IncomingRecord) = 1),
    CONSTRAINT CK_SequenceAnomaly_Resolution CHECK (
           (ResolvedAt IS NULL AND ResolvedBy IS NULL AND Resolution IS NULL)
        OR (ResolvedAt IS NOT NULL AND ResolvedBy IS NOT NULL AND Resolution IS NOT NULL))
);
GO

ALTER TABLE dbo.StationConfig ADD CONSTRAINT FK_StationConfig_Anomaly
    FOREIGN KEY (BlockedByAnomalyId) REFERENCES dbo.SequenceAnomaly (AnomalyId);
GO

CREATE TABLE dbo.SequenceGap (
    GapId            BIGINT IDENTITY (1, 1) NOT NULL,
    SiteId           NVARCHAR(64)   NOT NULL,
    StationId        NVARCHAR(64)   NOT NULL,
    PlcEpoch         BIGINT         NOT NULL,
    FromCycleId      DECIMAL(20, 0) NOT NULL,
    ToCycleId        DECIMAL(20, 0) NOT NULL,
    DetectedAt       DATETIME2(3)   NOT NULL,
    GraceUntil       DATETIME2(3)   NOT NULL,
    ConfigRevisionId BIGINT         NOT NULL,
    Status           VARCHAR(20)    NOT NULL,
    UnrecoveredCount DECIMAL(20, 0) NULL,
    ClosedAt         DATETIME2(3)   NULL,
    CONSTRAINT PK_SequenceGap PRIMARY KEY (GapId),
    CONSTRAINT UQ_SequenceGap_Start UNIQUE (SiteId, StationId, PlcEpoch, FromCycleId),
    CONSTRAINT FK_SequenceGap_Epoch FOREIGN KEY (SiteId, StationId, PlcEpoch)
        REFERENCES dbo.PlcEpochHistory (SiteId, StationId, PlcEpoch),
    CONSTRAINT FK_SequenceGap_Revision FOREIGN KEY (ConfigRevisionId, SiteId, StationId)
        REFERENCES dbo.StationConfigRevision (ConfigRevisionId, SiteId, StationId),
    CONSTRAINT CK_SequenceGap_Range CHECK (FromCycleId <= ToCycleId AND GraceUntil >= DetectedAt),
    CONSTRAINT CK_SequenceGap_Status CHECK (Status IN ('OPEN', 'RECOVERED', 'CLOSED_UNRECOVERED')),
    CONSTRAINT CK_SequenceGap_Close CHECK (
           (Status = 'OPEN' AND ClosedAt IS NULL AND UnrecoveredCount IS NULL)
        OR (Status = 'RECOVERED' AND ClosedAt IS NOT NULL AND UnrecoveredCount IS NOT NULL AND UnrecoveredCount = 0)
        OR (Status = 'CLOSED_UNRECOVERED' AND ClosedAt IS NOT NULL AND UnrecoveredCount IS NOT NULL AND UnrecoveredCount > 0))
);
GO
-- Regla entre tablas: un hueco no se solapa con otro de la misma época. Se valida en
-- TX-A con UPDLOCK, HOLDLOCK sobre UQ_SequenceGap_Start.

-- -------------------------------------------------------------------------------------
-- 7. Trabajos de impresión (H1, H3, H8, H9)
-- -------------------------------------------------------------------------------------
CREATE SEQUENCE dbo.LabelSerialSeq AS BIGINT START WITH 1 INCREMENT BY 1 NO CYCLE CACHE 50;
GO
-- El serial se obtiene ANTES de TX-A (SELECT @n = NEXT VALUE FOR, fuera de la
-- transacción), se arma el payload con él y se calcula PayloadHash; después se inserta.
-- Un rollback o un duplicado consume el valor y el CACHE puede perder valores ante un
-- apagado abrupto: habrá huecos. Se garantiza unicidad (UQ_PrintJob_LabelSerial), no
-- consecutividad.

CREATE TABLE dbo.PrintJob (
    PrintJobId                    UNIQUEIDENTIFIER NOT NULL,
    DetectionId                   UNIQUEIDENTIFIER NOT NULL,
    SiteId                        NVARCHAR(64)     NOT NULL,
    StationId                     NVARCHAR(64)     NOT NULL,
    PlcEpoch                      BIGINT           NOT NULL,
    CycleId                       DECIMAL(20, 0)   NOT NULL,
    LabelType                     NVARCHAR(64)     NOT NULL,
    ConfigRevisionId              BIGINT           NOT NULL,
    PrinterId                     NVARCHAR(64)     NOT NULL,
    WorkOrderId                   NVARCHAR(128)    NOT NULL,
    ErpRevision                   NVARCHAR(64)     NOT NULL,
    ProductId                     NVARCHAR(128)    NOT NULL,
    LotId                         NVARCHAR(128)    NOT NULL,
    CartonId                      NVARCHAR(128)    NULL,
    LabelSerial                   VARCHAR(40)      COLLATE Latin1_General_100_BIN2 NOT NULL,   -- comparación exacta
    TemplateRevision              NVARCHAR(64)     NOT NULL,
    Payload                       NVARCHAR(MAX)    NOT NULL,
    PayloadHash                   BINARY(32)       NOT NULL,
    DeadlineAt                    DATETIME2(3)     NOT NULL,
    Status                        VARCHAR(20)      NOT NULL,
    Reason                        VARCHAR(40)      NULL,
    AutoAttemptCount              TINYINT          NOT NULL CONSTRAINT DF_PrintJob_AutoAttemptCount DEFAULT (0),
    NextAttemptAt                 DATETIME2(3)     NULL,
    ResultVersion                 INT              NOT NULL CONSTRAINT DF_PrintJob_ResultVersion DEFAULT (0),
    ConfirmedByExceptionalReprint BIT              NOT NULL CONSTRAINT DF_PrintJob_Exceptional DEFAULT (0),
    LastOperatorActionId          BIGINT           NULL,   -- respaldo de la última transición manual
    LastExceptionalAttemptId      UNIQUEIDENTIFIER NULL,   -- respaldo de la última transición excepcional
    CreatedAt                     DATETIME2(3)     NOT NULL CONSTRAINT DF_PrintJob_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt                     DATETIME2(3)     NOT NULL CONSTRAINT DF_PrintJob_UpdatedAt DEFAULT SYSUTCDATETIME(),
    RowVersion                    ROWVERSION       NOT NULL,
    CONSTRAINT PK_PrintJob PRIMARY KEY (PrintJobId),
    CONSTRAINT UQ_PrintJob_DetectionLabel UNIQUE (DetectionId, LabelType),
    CONSTRAINT UQ_PrintJob_LabelSerial UNIQUE (LabelSerial),
    CONSTRAINT FK_PrintJob_Detection FOREIGN KEY (DetectionId, SiteId, StationId, PlcEpoch, CycleId)
        REFERENCES dbo.CartonDetection (DetectionId, SiteId, StationId, PlcEpoch, CycleId),
    CONSTRAINT FK_PrintJob_Revision FOREIGN KEY (ConfigRevisionId, SiteId, StationId)
        REFERENCES dbo.StationConfigRevision (ConfigRevisionId, SiteId, StationId),
    CONSTRAINT FK_PrintJob_Erp FOREIGN KEY (WorkOrderId, ErpRevision)
        REFERENCES dbo.ErpWorkOrderCache (WorkOrderId, Revision),
    CONSTRAINT CK_PrintJob_Status CHECK (Status IN ('READY', 'SENDING', 'TRANSMITTED', 'RETRY_WAIT', 'CONFIRMED', 'UNKNOWN', 'FAILED', 'EXPIRED', 'CANCELLED', 'CLOSED_UNRESOLVED')),
    CONSTRAINT CK_PrintJob_Reason CHECK (
           (Status = 'UNKNOWN' AND Reason IS NOT NULL AND Reason IN ('WRITE_INTERRUPTED', 'RESTART_DURING_SEND', 'EVIDENCE_WINDOW_ELAPSED', 'SERIAL_MISMATCH', 'EXCEPTIONAL_REPRINT_UNRESOLVED'))
        OR (Status = 'FAILED' AND Reason IS NOT NULL AND Reason IN ('PRINTER_UNAVAILABLE', 'DEVICE_REPORTED_NOT_PRINTED', 'RECONCILED_NOT_PRINTED'))
        OR (Status = 'EXPIRED' AND Reason IS NOT NULL AND Reason IN ('DEADLINE_PASSED', 'OUT_OF_ORDER'))
        OR (Status = 'CANCELLED' AND Reason IS NOT NULL AND Reason IN ('CANCELLED_BY_OPERATOR'))
        OR (Status = 'CLOSED_UNRESOLVED' AND Reason IS NOT NULL AND Reason IN ('CLOSED_CARTON_REJECTED', 'CLOSED_CARTON_QUARANTINED'))
        OR (Status NOT IN ('UNKNOWN', 'FAILED', 'EXPIRED', 'CANCELLED', 'CLOSED_UNRESOLVED') AND Reason IS NULL)),
    CONSTRAINT CK_PrintJob_RetryWait CHECK (Status <> 'RETRY_WAIT' OR NextAttemptAt IS NOT NULL),
    CONSTRAINT CK_PrintJob_Exceptional CHECK (ConfirmedByExceptionalReprint = 0 OR Status = 'CONFIRMED'),
    CONSTRAINT CK_PrintJob_Counters CHECK (AutoAttemptCount BETWEEN 0 AND 10 AND ResultVersion >= 0),
    CONSTRAINT CK_PrintJob_Serial CHECK (LEN(LabelSerial) >= 6)
);
GO

-- Cola del sender en orden de ciclo (FIFO por impresora).
CREATE INDEX IX_PrintJob_Claimable ON dbo.PrintJob (PrinterId, PlcEpoch, CycleId) WHERE Status = 'READY';
GO
CREATE INDEX IX_PrintJob_Sweep ON dbo.PrintJob (SiteId, StationId, Status, DeadlineAt) WHERE Status IN ('READY', 'RETRY_WAIT');
GO
CREATE INDEX IX_PrintJob_Carton ON dbo.PrintJob (SiteId, CartonId) INCLUDE (Status) WHERE CartonId IS NOT NULL;
GO
-- Regla entre tablas (H8, CARTON_ID_REQUIRED): en TX-A y en TX-E, antes de crear el job,
-- se leen IX_PrintJob_Carton e IX_CartonDetection_Carton con UPDLOCK, HOLDLOCK. Si existe
-- un job de la misma caja en READY, SENDING, TRANSMITTED, RETRY_WAIT, CONFIRMED, UNKNOWN o
-- CLOSED_UNRESOLVED, o una detección de la misma caja en WAITING_ERP, la detección queda
-- BLOCKED con RECIRCULATED_CARTON (D05 o D08). Un índice filtrado no puede expresarlo
-- porque depende del estado de otra fila.

-- Orden de envío por impresora (hallazgo de la revisión independiente: envío fuera de orden).
-- Una impresora de línea pertenece a una sola estación, que tiene un solo LabelType por
-- revisión. Al registrar la impresora se inserta su fila con los campos Last* en NULL. El
-- claim exige que el ciclo del job sea posterior al último reclamado, salvo que sea el
-- mismo job (reintento). La ManualPrinterId no tiene fila: las reimpresiones no siguen orden.
CREATE TABLE dbo.PrinterSendOrder (
    PrinterId           NVARCHAR(64)     NOT NULL,
    SiteId              NVARCHAR(64)     NOT NULL,
    StationId           NVARCHAR(64)     NOT NULL,
    LastPlcEpoch        BIGINT           NULL,
    LastEpochAcceptedAt DATETIME2(3)     NULL,
    LastCycleId         DECIMAL(20, 0)   NULL,
    LastPrintJobId      UNIQUEIDENTIFIER NULL,
    UpdatedAt           DATETIME2(3)     NOT NULL,
    CONSTRAINT PK_PrinterSendOrder PRIMARY KEY (PrinterId),
    CONSTRAINT FK_PrinterSendOrder_Job FOREIGN KEY (LastPrintJobId) REFERENCES dbo.PrintJob (PrintJobId),
    CONSTRAINT CK_PrinterSendOrder_Complete CHECK (
           (LastPlcEpoch IS NULL AND LastEpochAcceptedAt IS NULL AND LastCycleId IS NULL AND LastPrintJobId IS NULL)
        OR (LastPlcEpoch IS NOT NULL AND LastEpochAcceptedAt IS NOT NULL AND LastCycleId IS NOT NULL AND LastPrintJobId IS NOT NULL))
);
GO

-- El orden solo avanza (I23): no se borra la fila ni se retrocede la marca.
CREATE TRIGGER dbo.TR_PrinterSendOrder_Monotonic
ON dbo.PrinterSendOrder
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        THROW 51026, N'PrinterSendOrder no se borra.', 1;
    END;
    IF UPDATE(PrinterId) OR UPDATE(SiteId) OR UPDATE(StationId)
    BEGIN
        THROW 51027, N'La impresora y la estación de PrinterSendOrder son inmutables.', 1;
    END;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN deleted AS d ON d.PrinterId = i.PrinterId
        WHERE d.LastPrintJobId IS NOT NULL
          AND (   i.LastPrintJobId IS NULL
               OR i.LastEpochAcceptedAt < d.LastEpochAcceptedAt
               OR (i.LastEpochAcceptedAt = d.LastEpochAcceptedAt AND i.LastCycleId < d.LastCycleId)))
    BEGIN
        THROW 51028, N'PrinterSendOrder no retrocede.', 1;
    END;
END;
GO

-- -------------------------------------------------------------------------------------
-- 8. Acciones manuales auditadas (TX-M). Registro solo de inserción.
-- -------------------------------------------------------------------------------------
-- Las transiciones manuales de PrintJob y PrintAttempt, el desbloqueo de una estación y la
-- confirmación manual de una época solo se aceptan si apuntan a una OperatorAction nueva y
-- coherente (TR_PrintJob_StateMachine, TR_PrintAttempt_StateMachine, TR_StationConfig_Guard,
-- TR_PlcEpochHistory_Guard). RESOLVE_ANOMALY queda como auditoría junto a ResolvedBy.
CREATE TABLE dbo.OperatorAction (
    OperatorActionId           BIGINT IDENTITY (1, 1) NOT NULL,
    ActionType                 VARCHAR(30)      NOT NULL,
    SiteId                     NVARCHAR(64)     NOT NULL,
    StationId                  NVARCHAR(64)     NOT NULL,
    PrintJobId                 UNIQUEIDENTIFIER NULL,
    TargetStatus               VARCHAR(20)      NULL,
    PerformedBy                NVARCHAR(128)    NOT NULL,
    PerformedAt                DATETIME2(3)     NOT NULL,
    ReasonText                 NVARCHAR(512)    NOT NULL,
    DeviceNotPrintedEvidence   NVARCHAR(256)    NULL,   -- prueba 1: el dispositivo no imprimió el job
    DeviceQueueClearedEvidence NVARCHAR(256)    NULL,   -- prueba 2: su cola quedó vacía o cancelada
    CartonInspectionEvidence   NVARCHAR(256)    NULL,   -- prueba 3: la caja retenida no tiene la etiqueta
    CartonDisposition          VARCHAR(20)      NULL,   -- REJECTED | QUARANTINED
    CONSTRAINT PK_OperatorAction PRIMARY KEY (OperatorActionId),
    CONSTRAINT FK_OperatorAction_Job FOREIGN KEY (PrintJobId) REFERENCES dbo.PrintJob (PrintJobId),
    CONSTRAINT CK_OperatorAction_Type CHECK (ActionType IN ('CANCEL_JOB', 'RECONCILE_NEGATIVE', 'CLOSE_UNRESOLVED',
                                                            'UNBLOCK_STATION', 'CONFIRM_EPOCH', 'RESOLVE_ANOMALY')),
    CONSTRAINT CK_OperatorAction_Target CHECK (
           (ActionType = 'CANCEL_JOB' AND PrintJobId IS NOT NULL AND TargetStatus IS NOT NULL AND TargetStatus = 'CANCELLED')
        OR (ActionType = 'RECONCILE_NEGATIVE' AND PrintJobId IS NOT NULL AND TargetStatus IS NOT NULL AND TargetStatus = 'FAILED'
            AND DeviceNotPrintedEvidence IS NOT NULL AND DeviceQueueClearedEvidence IS NOT NULL AND CartonInspectionEvidence IS NOT NULL)
        OR (ActionType = 'CLOSE_UNRESOLVED' AND PrintJobId IS NOT NULL AND TargetStatus IS NOT NULL AND TargetStatus = 'CLOSED_UNRESOLVED'
            AND CartonDisposition IS NOT NULL AND CartonDisposition IN ('REJECTED', 'QUARANTINED'))
        OR (ActionType IN ('UNBLOCK_STATION', 'CONFIRM_EPOCH', 'RESOLVE_ANOMALY') AND PrintJobId IS NULL AND TargetStatus IS NULL)),
    CONSTRAINT CK_OperatorAction_Person CHECK (PerformedBy <> N'SYSTEM')
);
GO

ALTER TABLE dbo.PrintJob ADD CONSTRAINT FK_PrintJob_OperatorAction
    FOREIGN KEY (LastOperatorActionId) REFERENCES dbo.OperatorAction (OperatorActionId);
GO
ALTER TABLE dbo.StationConfig ADD CONSTRAINT FK_StationConfig_OperatorAction
    FOREIGN KEY (LastOperatorActionId) REFERENCES dbo.OperatorAction (OperatorActionId);
GO
ALTER TABLE dbo.PlcEpochHistory ADD CONSTRAINT FK_PlcEpochHistory_OperatorAction
    FOREIGN KEY (OperatorActionId) REFERENCES dbo.OperatorAction (OperatorActionId);
GO

-- -------------------------------------------------------------------------------------
-- 9. Autorizaciones de reimpresión excepcional (H2, ADR 0012)
-- -------------------------------------------------------------------------------------
CREATE TABLE dbo.ReprintAuthorization (
    AuthorizationId        BIGINT IDENTITY (1, 1) NOT NULL,
    PrintJobId             UNIQUEIDENTIFIER NOT NULL,
    PriorJobStatus         VARCHAR(20)      NOT NULL,
    Reason                 VARCHAR(30)      NOT NULL,
    DuplicateRisk          VARCHAR(30)      NOT NULL,
    CartonReference        NVARCHAR(256)    NOT NULL,   -- CartonId o ubicación de la caja retenida
    CartonDisposition      VARCHAR(10)      NOT NULL,
    InspectionEvidence     NVARCHAR(512)    NOT NULL,
    OriginalLabelRemovedBy NVARCHAR(128)    NULL,       -- se fija una vez, antes de ejecutar (LABEL_DAMAGED)
    OriginalLabelRemovedAt DATETIME2(3)     NULL,
    TargetPrinterId        NVARCHAR(64)     NOT NULL,
    ReprintMarker          BIT              NOT NULL,   -- la etiqueta lleva marca visible de reimpresión
    EvidenceWindowMs       INT              NOT NULL,   -- plazo para la evidencia del intento excepcional
    RequestedBy            NVARCHAR(128)    NOT NULL,
    RequestedAt            DATETIME2(3)     NOT NULL,
    ApprovedBy             NVARCHAR(128)    NOT NULL,
    ApprovedAt             DATETIME2(3)     NOT NULL,
    ExpiresAt              DATETIME2(3)     NOT NULL,
    Status                 VARCHAR(10)      NOT NULL,
    ExecutedAt             DATETIME2(3)     NULL,
    RevokedBy              NVARCHAR(128)    NULL,
    RevokedAt              DATETIME2(3)     NULL,
    CONSTRAINT PK_ReprintAuthorization PRIMARY KEY (AuthorizationId),
    CONSTRAINT UQ_ReprintAuthorization_Job UNIQUE (AuthorizationId, PrintJobId),
    CONSTRAINT FK_ReprintAuthorization_Job FOREIGN KEY (PrintJobId) REFERENCES dbo.PrintJob (PrintJobId),
    CONSTRAINT CK_ReprintAuthorization_Status CHECK (Status IN ('APPROVED', 'EXECUTED', 'REVOKED', 'EXPIRED')),
    CONSTRAINT CK_ReprintAuthorization_PriorStatus CHECK (PriorJobStatus IN ('UNKNOWN', 'FAILED', 'EXPIRED', 'CLOSED_UNRESOLVED', 'CONFIRMED')),
    CONSTRAINT CK_ReprintAuthorization_Reason CHECK (Reason IN ('UNKNOWN_NOT_RESOLVED', 'PROVEN_NOT_PRINTED', 'EXPIRED_CARTON_HELD', 'CLOSED_CARTON_RECOVERED', 'LABEL_DAMAGED')),
    CONSTRAINT CK_ReprintAuthorization_Risk CHECK (
           (PriorJobStatus IN ('UNKNOWN', 'CLOSED_UNRESOLVED') AND DuplicateRisk = 'ACCEPTED')
        OR (PriorJobStatus = 'CONFIRMED' AND DuplicateRisk = 'REPLACES_EXISTING_LABEL')
        OR (PriorJobStatus IN ('FAILED', 'EXPIRED') AND DuplicateRisk IN ('NONE_PROVEN', 'ACCEPTED'))),
    CONSTRAINT CK_ReprintAuthorization_ReasonPrior CHECK (
           (Reason = 'UNKNOWN_NOT_RESOLVED' AND PriorJobStatus = 'UNKNOWN')
        OR (Reason = 'PROVEN_NOT_PRINTED' AND PriorJobStatus = 'FAILED')
        OR (Reason = 'EXPIRED_CARTON_HELD' AND PriorJobStatus = 'EXPIRED')
        OR (Reason = 'CLOSED_CARTON_RECOVERED' AND PriorJobStatus = 'CLOSED_UNRESOLVED')
        OR (Reason = 'LABEL_DAMAGED' AND PriorJobStatus = 'CONFIRMED')),
    CONSTRAINT CK_ReprintAuthorization_Removal CHECK (
           (OriginalLabelRemovedBy IS NULL AND OriginalLabelRemovedAt IS NULL)
        OR (PriorJobStatus = 'CONFIRMED' AND OriginalLabelRemovedBy IS NOT NULL AND OriginalLabelRemovedAt IS NOT NULL)),
    CONSTRAINT CK_ReprintAuthorization_Disposition CHECK (CartonDisposition = 'HELD'),
    CONSTRAINT CK_ReprintAuthorization_TwoPersons CHECK (ApprovedBy <> RequestedBy),
    CONSTRAINT CK_ReprintAuthorization_Times CHECK (ApprovedAt >= RequestedAt AND ExpiresAt > ApprovedAt AND EvidenceWindowMs > 0),
    CONSTRAINT CK_ReprintAuthorization_Executed CHECK (
           (Status = 'EXECUTED' AND ExecutedAt IS NOT NULL)
        OR (Status <> 'EXECUTED' AND ExecutedAt IS NULL)),
    CONSTRAINT CK_ReprintAuthorization_Revoked CHECK (
           (Status = 'REVOKED' AND RevokedBy IS NOT NULL AND RevokedAt IS NOT NULL)
        OR (Status <> 'REVOKED' AND RevokedBy IS NULL AND RevokedAt IS NULL))
);
GO
-- Una sola autorización vigente por job.
CREATE UNIQUE INDEX UX_ReprintAuthorization_OpenPerJob ON dbo.ReprintAuthorization (PrintJobId) WHERE Status = 'APPROVED';
GO

-- -------------------------------------------------------------------------------------
-- 10. Intentos de transmisión (H1, H2, H3, H6)
-- -------------------------------------------------------------------------------------
CREATE TABLE dbo.PrintAttempt (
    AttemptId               UNIQUEIDENTIFIER NOT NULL,
    PrintJobId              UNIQUEIDENTIFIER NOT NULL,
    AttemptNumber           INT              NOT NULL,
    Kind                    VARCHAR(20)      NOT NULL,   -- ORDINARY | EXCEPTIONAL_REPRINT
    AuthorizationId         BIGINT           NULL,
    PrinterId               NVARCHAR(64)     NOT NULL,
    OwnerInstanceId         NVARCHAR(128)    NOT NULL,
    LeaseToken              BIGINT           NOT NULL,
    Outcome                 VARCHAR(20)      NOT NULL,
    MayHaveSent             BIT              NOT NULL,
    AttemptPayloadHash      BINARY(32)       NOT NULL,
    ClaimedAt               DATETIME2(3)     NOT NULL,
    PreSendCheckAt          DATETIME2(3)     NULL,
    WriteStartedAt          DATETIME2(3)     NULL,       -- informativo: se persiste después de escribir
    WriteCompletedAt        DATETIME2(3)     NULL,
    DeviceAcceptedAt        DATETIME2(3)     NULL,
    EvidenceDeadlineAt      DATETIME2(3)     NULL,       -- se fija una vez al pasar a TRANSMITTED
    ConfirmingObservationId BIGINT           NULL,       -- evidencia que confirmó el intento
    OperatorActionId        BIGINT           NULL,       -- respaldo de A09
    FailureDetail           NVARCHAR(512)    NULL,
    CONSTRAINT PK_PrintAttempt PRIMARY KEY (AttemptId),
    CONSTRAINT UQ_PrintAttempt_Number UNIQUE (PrintJobId, AttemptNumber),
    CONSTRAINT FK_PrintAttempt_Job FOREIGN KEY (PrintJobId) REFERENCES dbo.PrintJob (PrintJobId),
    -- FK compuesta: el intento solo puede usar una autorización de su propio job.
    CONSTRAINT FK_PrintAttempt_Authorization FOREIGN KEY (AuthorizationId, PrintJobId)
        REFERENCES dbo.ReprintAuthorization (AuthorizationId, PrintJobId),
    CONSTRAINT FK_PrintAttempt_OperatorAction FOREIGN KEY (OperatorActionId) REFERENCES dbo.OperatorAction (OperatorActionId),
    CONSTRAINT CK_PrintAttempt_Outcome CHECK (Outcome IN ('SENDING', 'NOT_SENT', 'TRANSMITTED', 'UNKNOWN', 'CONFIRMED', 'NOT_PRINTED')),
    CONSTRAINT CK_PrintAttempt_Number CHECK (AttemptNumber > 0),
    CONSTRAINT CK_PrintAttempt_Kind CHECK (
           (Kind = 'ORDINARY' AND AuthorizationId IS NULL)
        OR (Kind = 'EXCEPTIONAL_REPRINT' AND AuthorizationId IS NOT NULL)),
    -- NOT_SENT solo si la escritura nunca se invocó. Es regla de código (I02): SQL no puede
    -- probarlo porque WriteStartedAt se persiste después de escribir.
    CONSTRAINT CK_PrintAttempt_NotSent CHECK (
           (Outcome = 'NOT_SENT' AND MayHaveSent = 0 AND WriteStartedAt IS NULL AND WriteCompletedAt IS NULL)
        OR (Outcome <> 'NOT_SENT' AND MayHaveSent = 1)),
    CONSTRAINT CK_PrintAttempt_Transmitted CHECK (Outcome <> 'TRANSMITTED' OR (WriteStartedAt IS NOT NULL AND WriteCompletedAt IS NOT NULL)),
    CONSTRAINT CK_PrintAttempt_WriteOrder CHECK (WriteCompletedAt IS NULL OR (WriteStartedAt IS NOT NULL AND WriteCompletedAt >= WriteStartedAt)),
    CONSTRAINT CK_PrintAttempt_Confirmed CHECK (
           (Outcome = 'CONFIRMED' AND ConfirmingObservationId IS NOT NULL)
        OR (Outcome <> 'CONFIRMED' AND ConfirmingObservationId IS NULL)),
    CONSTRAINT CK_PrintAttempt_ExceptionalWindow CHECK (
        Kind <> 'EXCEPTIONAL_REPRINT' OR Outcome <> 'TRANSMITTED' OR EvidenceDeadlineAt IS NOT NULL),
    CONSTRAINT CK_PrintAttempt_ManualNegative CHECK (OperatorActionId IS NULL OR Outcome = 'NOT_PRINTED')
);
GO

-- Un solo intento en transmisión por impresora: serializa el canal a nivel lógico.
CREATE UNIQUE INDEX UX_PrintAttempt_PrinterInFlight ON dbo.PrintAttempt (PrinterId) WHERE Outcome = 'SENDING';
GO
-- Un solo intento abierto por job.
CREATE UNIQUE INDEX UX_PrintAttempt_JobOpen ON dbo.PrintAttempt (PrintJobId) WHERE Outcome IN ('SENDING', 'TRANSMITTED');
GO
-- Como máximo un intento ORDINARY que pudo transmitir por job (I03). NOT_SENT tiene MayHaveSent = 0.
CREATE UNIQUE INDEX UX_PrintAttempt_OrdinarySent ON dbo.PrintAttempt (PrintJobId) WHERE Kind = 'ORDINARY' AND MayHaveSent = 1;
GO
-- Cada autorización se usa una sola vez. UNIQUE admitiría un solo NULL; por eso es un índice filtrado.
CREATE UNIQUE INDEX UX_PrintAttempt_Authorization ON dbo.PrintAttempt (AuthorizationId) WHERE AuthorizationId IS NOT NULL;
GO
CREATE INDEX IX_PrintAttempt_EvidenceWindow ON dbo.PrintAttempt (EvidenceDeadlineAt) WHERE Outcome = 'TRANSMITTED';
GO

ALTER TABLE dbo.PrintJob ADD CONSTRAINT FK_PrintJob_ExceptionalAttempt
    FOREIGN KEY (LastExceptionalAttemptId) REFERENCES dbo.PrintAttempt (AttemptId);
GO

-- -------------------------------------------------------------------------------------
-- 11. Evidencia recibida (H3). Registro solo de inserción; toda lectura queda, confirme o no.
-- -------------------------------------------------------------------------------------
CREATE TABLE dbo.EvidenceObservation (
    ObservationId   BIGINT IDENTITY (1, 1) NOT NULL,
    SiteId          NVARCHAR(64)     NOT NULL,
    StationId       NVARCHAR(64)     NOT NULL,
    PlcEpoch        BIGINT           NULL,       -- correlación CYCLE
    CycleId         DECIMAL(20, 0)   NULL,
    PrintJobId      UNIQUEIDENTIFIER NULL,       -- correlación JOB; NULL si no corresponde a ningún job
    AttemptId       UNIQUEIDENTIFIER NULL,
    EvidenceType    VARCHAR(30)      NOT NULL,
    EvidenceLevel   VARCHAR(10)      NOT NULL,
    CorrelatedTo    VARCHAR(10)      NOT NULL,
    ReadSerial      VARCHAR(40)      COLLATE Latin1_General_100_BIN2 NULL,   -- comparación exacta con LabelSerial
    SourceReference NVARCHAR(256)    NOT NULL,
    ObservedAt      DATETIME2(3)     NOT NULL,
    ReceivedAt      DATETIME2(3)     NOT NULL,
    RecordedBy      NVARCHAR(128)    NOT NULL,   -- 'SYSTEM' o la persona que inspeccionó
    CONSTRAINT PK_EvidenceObservation PRIMARY KEY (ObservationId),
    CONSTRAINT FK_EvidenceObservation_Job FOREIGN KEY (PrintJobId) REFERENCES dbo.PrintJob (PrintJobId),
    CONSTRAINT FK_EvidenceObservation_Attempt FOREIGN KEY (AttemptId) REFERENCES dbo.PrintAttempt (AttemptId),
    CONSTRAINT CK_EvidenceObservation_Type CHECK (
           (EvidenceType = 'PRINTER_JOB_COMPLETION' AND EvidenceLevel = 'PRINTED')
        OR (EvidenceType = 'PLC_APPLICATOR_CONFIRMATION' AND EvidenceLevel = 'APPLIED')
        OR (EvidenceType = 'BARCODE_VERIFICATION' AND EvidenceLevel = 'VERIFIED')
        OR (EvidenceType = 'MANUAL_INSPECTION' AND EvidenceLevel IN ('PRINTED', 'APPLIED', 'VERIFIED'))),
    CONSTRAINT CK_EvidenceObservation_Level CHECK (
           (EvidenceLevel = 'PRINTED' AND CorrelatedTo = 'JOB' AND PrintJobId IS NOT NULL)
        OR (EvidenceLevel = 'APPLIED' AND CorrelatedTo = 'CYCLE' AND PlcEpoch IS NOT NULL AND CycleId IS NOT NULL)
        OR (EvidenceLevel = 'VERIFIED' AND CorrelatedTo = 'CYCLE' AND PlcEpoch IS NOT NULL AND CycleId IS NOT NULL
            AND ReadSerial IS NOT NULL)),
    CONSTRAINT CK_EvidenceObservation_Manual CHECK (EvidenceType <> 'MANUAL_INSPECTION' OR RecordedBy <> N'SYSTEM')
);
GO
CREATE INDEX IX_EvidenceObservation_Cycle ON dbo.EvidenceObservation (SiteId, StationId, PlcEpoch, CycleId)
    INCLUDE (ReadSerial, CorrelatedTo)
    WHERE CycleId IS NOT NULL;
GO

ALTER TABLE dbo.PrintAttempt ADD CONSTRAINT FK_PrintAttempt_Observation
    FOREIGN KEY (ConfirmingObservationId) REFERENCES dbo.EvidenceObservation (ObservationId);
GO
-- Una observación confirma como máximo un intento.
CREATE UNIQUE INDEX UX_PrintAttempt_ConfirmingObservation ON dbo.PrintAttempt (ConfirmingObservationId)
    WHERE ConfirmingObservationId IS NOT NULL;
GO

-- -------------------------------------------------------------------------------------
-- 12. Propietario de impresora (H6, ADR 0010)
-- -------------------------------------------------------------------------------------
CREATE TABLE dbo.PrinterLease (
    PrinterId       NVARCHAR(64)  NOT NULL,
    OwnerInstanceId NVARCHAR(128) NOT NULL,
    OwnerHost       NVARCHAR(128) NOT NULL,
    FencingToken    BIGINT        NOT NULL,
    AcquiredAt      DATETIME2(3)  NOT NULL,
    LeaseUntil      DATETIME2(3)  NOT NULL,
    CONSTRAINT PK_PrinterLease PRIMARY KEY (PrinterId),
    CONSTRAINT CK_PrinterLease_Token CHECK (FencingToken > 0),
    CONSTRAINT CK_PrinterLease_Times CHECK (LeaseUntil > AcquiredAt)
);
GO
-- Garantía: coordina reclamos lógicos y detecta una segunda instancia. NO impide que un
-- proceso que ya validó su token transmita, porque la impresora no valida tokens.

-- -------------------------------------------------------------------------------------
-- 13. Outbox e Inbox
-- -------------------------------------------------------------------------------------
CREATE TABLE dbo.OutboxEvent (
    OutboxSequence   BIGINT IDENTITY (1, 1) NOT NULL,
    EventId          UNIQUEIDENTIFIER NOT NULL,
    EventType        VARCHAR(30)      NOT NULL,
    OrderingKey      NVARCHAR(220)    NOT NULL,   -- idempotencyKey
    AggregateType    VARCHAR(20)      NOT NULL,   -- CartonDetection | PrintJob
    AggregateId      UNIQUEIDENTIFIER NOT NULL,
    AggregateVersion INT              NOT NULL,
    Payload          NVARCHAR(MAX)    NOT NULL,
    CreatedAt        DATETIME2(3)     NOT NULL CONSTRAINT DF_OutboxEvent_CreatedAt DEFAULT SYSUTCDATETIME(),
    PublishedAt      DATETIME2(3)     NULL,
    PublishAttempts  INT              NOT NULL CONSTRAINT DF_OutboxEvent_Attempts DEFAULT (0),
    LastError        NVARCHAR(512)    NULL,
    CONSTRAINT PK_OutboxEvent PRIMARY KEY (OutboxSequence),
    CONSTRAINT UQ_OutboxEvent_EventId UNIQUE (EventId),
    CONSTRAINT UQ_OutboxEvent_Aggregate UNIQUE (AggregateType, AggregateId, EventType, AggregateVersion),
    CONSTRAINT CK_OutboxEvent_Type CHECK (EventType IN ('CartonDetected', 'PrintJobResult')),
    CONSTRAINT CK_OutboxEvent_Aggregate CHECK (AggregateType IN ('CartonDetection', 'PrintJob')),
    CONSTRAINT CK_OutboxEvent_Json CHECK (ISJSON(Payload) = 1),
    CONSTRAINT CK_OutboxEvent_Counters CHECK (PublishAttempts >= 0 AND AggregateVersion >= 1)
);
GO
CREATE INDEX IX_OutboxEvent_Pending ON dbo.OutboxEvent (OutboxSequence) WHERE PublishedAt IS NULL;
GO

CREATE TABLE dbo.InboxMessage (
    ConsumerName NVARCHAR(128)    NOT NULL,
    EventId      UNIQUEIDENTIFIER NOT NULL,
    ProcessedAt  DATETIME2(3)     NOT NULL CONSTRAINT DF_InboxMessage_ProcessedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_InboxMessage PRIMARY KEY (ConsumerName, EventId)
);
GO

-- -------------------------------------------------------------------------------------
-- 14. Inmutabilidad (I09, I17, I18). Todos empiezan con la guarda de 0 filas.
-- -------------------------------------------------------------------------------------
CREATE TRIGGER dbo.TR_CartonDetection_Immutable
ON dbo.CartonDetection
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        THROW 51002, N'CartonDetection no se borra: es registro auditado.', 1;
    END;
    IF UPDATE(DetectionId) OR UPDATE(SiteId) OR UPDATE(StationId) OR UPDATE(PlcEpoch) OR UPDATE(CycleId)
       OR UPDATE(IdempotencyKey) OR UPDATE(SourceFingerprint) OR UPDATE(Origin) OR UPDATE(Transport)
       OR UPDATE(QueueOverflow) OR UPDATE(DetectionTimestamp) OR UPDATE(ReceivedAt) OR UPDATE(DeadlineReference)
       OR UPDATE(DeadlineAt) OR UPDATE(ConfigRevisionId) OR UPDATE(WorkOrderId) OR UPDATE(CartonId)
    BEGIN
        THROW 51003, N'Campos de identidad y plazo de CartonDetection son inmutables.', 1;
    END;
END;
GO

CREATE TRIGGER dbo.TR_PrintJob_Immutable
ON dbo.PrintJob
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        THROW 51004, N'PrintJob no se borra: es registro auditado.', 1;
    END;
    IF UPDATE(PrintJobId) OR UPDATE(DetectionId) OR UPDATE(SiteId) OR UPDATE(StationId) OR UPDATE(PlcEpoch)
       OR UPDATE(CycleId) OR UPDATE(LabelType) OR UPDATE(ConfigRevisionId) OR UPDATE(PrinterId)
       OR UPDATE(WorkOrderId) OR UPDATE(ErpRevision) OR UPDATE(ProductId) OR UPDATE(LotId) OR UPDATE(CartonId)
       OR UPDATE(LabelSerial) OR UPDATE(TemplateRevision) OR UPDATE(Payload) OR UPDATE(PayloadHash)
       OR UPDATE(DeadlineAt) OR UPDATE(CreatedAt)
    BEGIN
        THROW 51005, N'El contenido congelado de PrintJob es inmutable.', 1;
    END;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN deleted AS d ON d.PrintJobId = i.PrintJobId
        WHERE i.ResultVersion < d.ResultVersion)
    BEGIN
        THROW 51016, N'ResultVersion de PrintJob no retrocede.', 1;
    END;
END;
GO

-- Identidad inmutable; evidencia, ventana y escritura se fijan una sola vez.
CREATE TRIGGER dbo.TR_PrintAttempt_Immutable
ON dbo.PrintAttempt
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        THROW 51006, N'PrintAttempt no se borra: es registro auditado.', 1;
    END;
    IF UPDATE(AttemptId) OR UPDATE(PrintJobId) OR UPDATE(AttemptNumber) OR UPDATE(Kind)
       OR UPDATE(AuthorizationId) OR UPDATE(PrinterId) OR UPDATE(OwnerInstanceId) OR UPDATE(LeaseToken)
       OR UPDATE(AttemptPayloadHash) OR UPDATE(ClaimedAt)
    BEGIN
        THROW 51007, N'La identidad de un PrintAttempt es inmutable.', 1;
    END;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN deleted AS d ON d.AttemptId = i.AttemptId
        WHERE (d.ConfirmingObservationId IS NOT NULL AND (i.ConfirmingObservationId IS NULL OR i.ConfirmingObservationId <> d.ConfirmingObservationId))
           OR (d.EvidenceDeadlineAt IS NOT NULL AND (i.EvidenceDeadlineAt IS NULL OR i.EvidenceDeadlineAt <> d.EvidenceDeadlineAt))
           OR (d.WriteStartedAt IS NOT NULL AND (i.WriteStartedAt IS NULL OR i.WriteStartedAt <> d.WriteStartedAt))
           OR (d.WriteCompletedAt IS NOT NULL AND (i.WriteCompletedAt IS NULL OR i.WriteCompletedAt <> d.WriteCompletedAt))
           OR (d.OperatorActionId IS NOT NULL AND (i.OperatorActionId IS NULL OR i.OperatorActionId <> d.OperatorActionId)))
    BEGIN
        THROW 51017, N'Evidencia, ventana de evidencia y marcas de escritura de un PrintAttempt se fijan una sola vez.', 1;
    END;
END;
GO

CREATE TRIGGER dbo.TR_ReprintAuthorization_Immutable
ON dbo.ReprintAuthorization
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        THROW 51008, N'ReprintAuthorization no se borra: es registro auditado.', 1;
    END;
    IF UPDATE(PrintJobId) OR UPDATE(PriorJobStatus) OR UPDATE(Reason) OR UPDATE(DuplicateRisk)
       OR UPDATE(CartonReference) OR UPDATE(CartonDisposition) OR UPDATE(InspectionEvidence)
       OR UPDATE(TargetPrinterId) OR UPDATE(ReprintMarker) OR UPDATE(EvidenceWindowMs)
       OR UPDATE(RequestedBy) OR UPDATE(RequestedAt) OR UPDATE(ApprovedBy) OR UPDATE(ApprovedAt) OR UPDATE(ExpiresAt)
    BEGIN
        THROW 51009, N'Los datos de aprobación de una reimpresión son inmutables.', 1;
    END;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN deleted AS d ON d.AuthorizationId = i.AuthorizationId
        WHERE (d.OriginalLabelRemovedBy IS NOT NULL AND (i.OriginalLabelRemovedBy IS NULL OR i.OriginalLabelRemovedBy <> d.OriginalLabelRemovedBy))
           OR (d.OriginalLabelRemovedAt IS NOT NULL AND (i.OriginalLabelRemovedAt IS NULL OR i.OriginalLabelRemovedAt <> d.OriginalLabelRemovedAt))
           OR (i.OriginalLabelRemovedBy IS NOT NULL AND d.OriginalLabelRemovedBy IS NULL AND d.Status <> 'APPROVED'))
    BEGIN
        THROW 51010, N'El retiro de la etiqueta original se registra una sola vez y antes de ejecutar.', 1;
    END;
END;
GO

CREATE TRIGGER dbo.TR_OperatorAction_Immutable
ON dbo.OperatorAction
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51018, N'OperatorAction es un registro de auditoría: solo se inserta.', 1;
END;
GO

CREATE TRIGGER dbo.TR_EvidenceObservation_Immutable
ON dbo.EvidenceObservation
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51019, N'EvidenceObservation es un registro de auditoría: solo se inserta.', 1;
END;
GO

-- -------------------------------------------------------------------------------------
-- 15. Reglas entre tablas aplicadas por trigger
-- -------------------------------------------------------------------------------------
-- Intentos: quién puede abrir uno y con qué evidencia puede confirmarse (I03, I06, I18).
CREATE TRIGGER dbo.TR_PrintAttempt_Guard
ON dbo.PrintAttempt
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    DECLARE @kind VARCHAR(12) = CONVERT(VARCHAR(12), SESSION_CONTEXT(N'TransitionKind'));

    -- Un intento ORDINARY solo nace del claim: job en SENDING, misma impresora y mismo payload.
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.AttemptId = i.AttemptId
        INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = i.PrintJobId
        WHERE d.AttemptId IS NULL AND i.Kind = 'ORDINARY'
          AND (pj.Status <> 'SENDING' OR i.PrinterId <> pj.PrinterId OR i.AttemptPayloadHash <> pj.PayloadHash))
    BEGIN
        THROW 51020, N'Un intento ORDINARY exige el job en SENDING, su impresora y su payload.', 1;
    END;

    -- Un intento EXCEPTIONAL_REPRINT exige su autorización EXECUTED, el job en el estado
    -- aprobado y la impresora fuera de línea de la revisión activa (ManualPrinterId). La
    -- impresora de línea no se admite: una bandera de software no detiene la línea (OT-16).
    -- La ausencia de StationConfig o de la revisión cuenta como violación.
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.AttemptId = i.AttemptId
        INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = i.PrintJobId
        LEFT JOIN dbo.StationConfig AS sc ON sc.SiteId = pj.SiteId AND sc.StationId = pj.StationId
        LEFT JOIN dbo.StationConfigRevision AS ar ON ar.ConfigRevisionId = sc.ActiveConfigRevisionId
        LEFT JOIN dbo.ReprintAuthorization AS ra ON ra.AuthorizationId = i.AuthorizationId AND ra.PrintJobId = i.PrintJobId
        WHERE d.AttemptId IS NULL AND i.Kind = 'EXCEPTIONAL_REPRINT'
          AND (   @kind IS NULL OR @kind <> 'EXCEPTION'
               OR ra.AuthorizationId IS NULL
               OR ra.Status <> 'EXECUTED'
               OR pj.Status <> ra.PriorJobStatus
               OR i.PrinterId <> ra.TargetPrinterId
               OR (ra.PriorJobStatus = 'CONFIRMED' AND ra.OriginalLabelRemovedBy IS NULL)
               OR ar.ConfigRevisionId IS NULL
               OR ar.ManualPrinterId IS NULL
               OR i.PrinterId <> ar.ManualPrinterId))
    BEGIN
        THROW 51021, N'Reimpresión excepcional sin autorización ejecutable, en otro estado o hacia una impresora no permitida.', 1;
    END;

    -- Confirmar exige evidencia del propio job o ciclo, recibida después del claim, nivel
    -- suficiente y, si es VERIFIED, ReadSerial = LabelSerial (I06). Un intento excepcional
    -- solo se confirma con una observación que lo referencia: una lectura anterior del mismo
    -- serial no prueba la etiqueta de reemplazo. Mínimo: nivel congelado (ordinario) o el
    -- mayor entre PRINTED y el congelado (excepcional). Con NONE un intento ordinario no se confirma.
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.AttemptId = i.AttemptId
        INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = i.PrintJobId
        INNER JOIN dbo.StationConfigRevision AS fr ON fr.ConfigRevisionId = pj.ConfigRevisionId
        LEFT JOIN dbo.EvidenceObservation AS eo ON eo.ObservationId = i.ConfirmingObservationId
        WHERE i.Outcome = 'CONFIRMED' AND d.Outcome <> 'CONFIRMED'
          AND (   eo.ObservationId IS NULL
               OR eo.ReceivedAt < i.ClaimedAt
               OR (i.Kind = 'EXCEPTIONAL_REPRINT' AND (eo.AttemptId IS NULL OR eo.AttemptId <> i.AttemptId))
               OR NOT (   (eo.CorrelatedTo = 'JOB' AND eo.PrintJobId = i.PrintJobId
                           AND (eo.AttemptId IS NULL OR eo.AttemptId = i.AttemptId))
                       OR (eo.CorrelatedTo = 'CYCLE' AND eo.SiteId = pj.SiteId AND eo.StationId = pj.StationId
                           AND eo.PlcEpoch = pj.PlcEpoch AND eo.CycleId = pj.CycleId))
               OR (eo.EvidenceLevel = 'VERIFIED' AND eo.ReadSerial <> pj.LabelSerial)
               OR CASE eo.EvidenceLevel WHEN 'PRINTED' THEN 1 WHEN 'APPLIED' THEN 2 ELSE 3 END
                  < CASE
                        WHEN i.Kind = 'EXCEPTIONAL_REPRINT' THEN
                            CASE fr.MinConfirmationLevel WHEN 'APPLIED' THEN 2 WHEN 'VERIFIED' THEN 3 ELSE 1 END
                        ELSE
                            CASE fr.MinConfirmationLevel WHEN 'PRINTED' THEN 1 WHEN 'APPLIED' THEN 2 WHEN 'VERIFIED' THEN 3 ELSE 99 END
                    END))
    BEGIN
        THROW 51022, N'Confirmación sin evidencia del job o ciclo, con nivel insuficiente o con serial distinto.', 1;
    END;

    -- Un intento ordinario TRANSMITTED con nivel distinto de NONE necesita su ventana.
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = i.PrintJobId
        INNER JOIN dbo.StationConfigRevision AS fr ON fr.ConfigRevisionId = pj.ConfigRevisionId
        WHERE i.Outcome = 'TRANSMITTED' AND i.EvidenceDeadlineAt IS NULL AND fr.MinConfirmationLevel <> 'NONE')
    BEGIN
        THROW 51023, N'Intento TRANSMITTED sin EvidenceDeadlineAt en una estación que exige evidencia.', 1;
    END;
END;
GO

-- Autorizaciones: el estado previo es el real y NONE_PROVEN solo si nada pudo imprimirse.
CREATE TRIGGER dbo.TR_ReprintAuthorization_Guard
ON dbo.ReprintAuthorization
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = i.PrintJobId
        LEFT JOIN dbo.StationConfig AS sc ON sc.SiteId = pj.SiteId AND sc.StationId = pj.StationId
        LEFT JOIN dbo.StationConfigRevision AS ar ON ar.ConfigRevisionId = sc.ActiveConfigRevisionId
        WHERE pj.Status <> i.PriorJobStatus
           OR ar.ConfigRevisionId IS NULL
           OR ar.ManualPrinterId IS NULL
           OR i.TargetPrinterId <> ar.ManualPrinterId
           OR (i.DuplicateRisk = 'NONE_PROVEN' AND EXISTS (
                   SELECT 1 FROM dbo.PrintAttempt AS pa
                   WHERE pa.PrintJobId = i.PrintJobId AND pa.MayHaveSent = 1 AND pa.Outcome <> 'NOT_PRINTED')))
    BEGIN
        THROW 51024, N'Autorización incoherente: estado previo distinto, impresora no permitida o NONE_PROVEN con un intento que pudo imprimir.', 1;
    END;
END;
GO

-- Estación: no se borra y solo se desbloquea con una OperatorAction UNBLOCK_STATION nueva.
CREATE TRIGGER dbo.TR_StationConfig_Guard
ON dbo.StationConfig
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)
    BEGIN
        THROW 51029, N'StationConfig no se borra: las guardas de reimpresión dependen de ella.', 1;
    END;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        INNER JOIN deleted AS d ON d.SiteId = i.SiteId AND d.StationId = i.StationId
        WHERE d.LabelingEnabled = 0 AND i.LabelingEnabled = 1
          AND NOT EXISTS (
              SELECT 1 FROM dbo.OperatorAction AS oa
              WHERE oa.OperatorActionId = i.LastOperatorActionId
                AND (d.LastOperatorActionId IS NULL OR i.LastOperatorActionId > d.LastOperatorActionId)
                AND oa.ActionType = 'UNBLOCK_STATION'
                AND oa.SiteId = i.SiteId AND oa.StationId = i.StationId))
    BEGIN
        THROW 51030, N'Desbloquear una estación exige una OperatorAction UNBLOCK_STATION nueva.', 1;
    END;
END;
GO

-- Épocas: inmutables; una aceptación manual exige una OperatorAction CONFIRM_EPOCH de la estación.
CREATE TRIGGER dbo.TR_PlcEpochHistory_Immutable
ON dbo.PlcEpochHistory
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;
    THROW 51031, N'PlcEpochHistory es inmutable: el orden de envío depende de AcceptedAt.', 1;
END;
GO

CREATE TRIGGER dbo.TR_PlcEpochHistory_Guard
ON dbo.PlcEpochHistory
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF EXISTS (
        SELECT 1 FROM inserted AS i
        WHERE i.AcceptanceMode = 'OPERATOR_CONFIRMED'
          AND NOT EXISTS (
              SELECT 1 FROM dbo.OperatorAction AS oa
              WHERE oa.OperatorActionId = i.OperatorActionId AND oa.ActionType = 'CONFIRM_EPOCH'
                AND oa.SiteId = i.SiteId AND oa.StationId = i.StationId))
    BEGIN
        THROW 51032, N'Una época confirmada por una persona exige su OperatorAction CONFIRM_EPOCH.', 1;
    END;
END;
GO

-- -------------------------------------------------------------------------------------
-- 16. Máquinas de estado aplicadas por trigger (H2). Cada cambio de estado debe estar en
--     AllowedTransition y contar con el respaldo que exige su Kind.
-- -------------------------------------------------------------------------------------
CREATE TRIGGER dbo.TR_CartonDetection_StateMachine
ON dbo.CartonDetection
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.DetectionId = i.DetectionId
        WHERE (d.DetectionId IS NULL OR d.Status <> i.Status)
          AND NOT EXISTS (
              SELECT 1 FROM dbo.AllowedTransition AS t
              WHERE t.Machine = 'CartonDetection'
                AND t.FromStatus = COALESCE(d.Status, '(create)')
                AND t.ToStatus = i.Status
                AND t.Kind = 'AUTO'))
    BEGIN
        THROW 51011, N'Transición de CartonDetection no permitida por contracts/state-machine.json.', 1;
    END;
END;
GO

CREATE TRIGGER dbo.TR_PrintJob_StateMachine
ON dbo.PrintJob
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    DECLARE @kind VARCHAR(12) = CONVERT(VARCHAR(12), SESSION_CONTEXT(N'TransitionKind'));

    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.PrintJobId = i.PrintJobId
        CROSS APPLY (
            SELECT
                CASE WHEN i.LastExceptionalAttemptId IS NOT NULL
                      AND (d.PrintJobId IS NULL OR d.LastExceptionalAttemptId IS NULL
                           OR d.LastExceptionalAttemptId <> i.LastExceptionalAttemptId)
                     THEN 1 ELSE 0 END AS NewExceptional,
                -- Las OperatorAction son IDENTITY: "nueva" es mayor que la anterior, no solo distinta.
                CASE WHEN i.LastOperatorActionId IS NOT NULL
                      AND (d.PrintJobId IS NULL OR d.LastOperatorActionId IS NULL
                           OR i.LastOperatorActionId > d.LastOperatorActionId)
                     THEN 1 ELSE 0 END AS NewOperatorAction,
                CASE WHEN d.PrintJobId IS NOT NULL
                      AND (   ISNULL(d.Reason, '') <> ISNULL(i.Reason, '')
                           OR d.ConfirmedByExceptionalReprint <> i.ConfirmedByExceptionalReprint)
                     THEN 1 ELSE 0 END AS OtherChange,
                -- Cualquier cambio de un puntero de respaldo, incluso hacia NULL o hacia un valor anterior.
                CASE WHEN ISNULL(CONVERT(VARCHAR(36), d.LastOperatorActionId), '') <> ISNULL(CONVERT(VARCHAR(36), i.LastOperatorActionId), '')
                     THEN 1 ELSE 0 END AS OperatorPointerChanged,
                CASE WHEN ISNULL(CONVERT(VARCHAR(36), d.LastExceptionalAttemptId), '') <> ISNULL(CONVERT(VARCHAR(36), i.LastExceptionalAttemptId), '')
                     THEN 1 ELSE 0 END AS ExceptionalPointerChanged
        ) AS c
        -- Se evalúa todo cambio de estado, de motivo, de la marca de reimpresión o de los punteros
        -- de respaldo: ninguno puede cambiar fuera de una transición sembrada.
        WHERE (d.PrintJobId IS NULL OR d.Status <> i.Status OR c.OtherChange = 1
               OR c.OperatorPointerChanged = 1 OR c.ExceptionalPointerChanged = 1)
          AND NOT EXISTS (
              SELECT 1 FROM dbo.AllowedTransition AS t
              WHERE t.Machine = 'PrintJob'
                AND t.FromStatus = COALESCE(d.Status, '(create)')
                AND t.ToStatus = i.Status
                AND (
                    -- AUTO: sin respaldo manual ni excepcional. Confirmar exige un intento
                    -- ORDINARY confirmado y, desde UNKNOWN, solo si la incertidumbre no vino de
                    -- una evidencia contradictoria ni de una reimpresión excepcional.
                       (t.Kind = 'AUTO' AND c.ExceptionalPointerChanged = 0 AND c.OperatorPointerChanged = 0
                        AND i.ConfirmedByExceptionalReprint = 0
                        -- TRANSMITTED es terminal con MinConfirmationLevel NONE (J10, J11, J12)
                        AND (d.PrintJobId IS NULL OR d.Status <> 'TRANSMITTED' OR EXISTS (
                                SELECT 1 FROM dbo.StationConfigRevision AS fr
                                WHERE fr.ConfigRevisionId = i.ConfigRevisionId AND fr.MinConfirmationLevel <> 'NONE'))
                        AND (i.Status <> 'CONFIRMED' OR (
                                (d.Status = 'TRANSMITTED'
                                 OR (d.Reason IS NOT NULL AND d.Reason IN ('WRITE_INTERRUPTED', 'RESTART_DURING_SEND', 'EVIDENCE_WINDOW_ELAPSED')))
                                AND EXISTS (
                                    SELECT 1 FROM dbo.PrintAttempt AS pa
                                    WHERE pa.PrintJobId = i.PrintJobId AND pa.Kind = 'ORDINARY' AND pa.Outcome = 'CONFIRMED'))))
                    -- MANUAL: una OperatorAction nueva del mismo job hacia este estado.
                    OR (t.Kind = 'MANUAL' AND c.ExceptionalPointerChanged = 0 AND c.NewOperatorAction = 1
                        AND i.ConfirmedByExceptionalReprint = 0
                        AND EXISTS (
                            SELECT 1 FROM dbo.OperatorAction AS oa
                            WHERE oa.OperatorActionId = i.LastOperatorActionId
                              AND oa.PrintJobId = i.PrintJobId AND oa.TargetStatus = i.Status))
                    -- EXCEPTION: el intento EXCEPTIONAL_REPRINT más reciente del job, cuyo Outcome es
                    -- el destino; nuevo, o el mismo que dejó el job en EXCEPTIONAL_REPRINT_UNRESOLVED.
                    OR (t.Kind = 'EXCEPTION' AND @kind IS NOT NULL AND @kind = 'EXCEPTION' AND c.OperatorPointerChanged = 0
                        AND (c.NewExceptional = 1
                             OR (d.Reason IS NOT NULL AND d.Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED'
                                 AND d.LastExceptionalAttemptId = i.LastExceptionalAttemptId))
                        AND (   (i.Status = 'CONFIRMED' AND i.ConfirmedByExceptionalReprint = 1)
                             OR (i.Status = 'UNKNOWN' AND i.Reason IS NOT NULL AND i.Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED'))
                        AND EXISTS (
                            SELECT 1 FROM dbo.PrintAttempt AS pa
                            WHERE pa.AttemptId = i.LastExceptionalAttemptId AND pa.PrintJobId = i.PrintJobId
                              AND pa.Kind = 'EXCEPTIONAL_REPRINT' AND pa.Outcome = i.Status
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.PrintAttempt AS p2
                                  WHERE p2.PrintJobId = pa.PrintJobId AND p2.Kind = 'EXCEPTIONAL_REPRINT'
                                    AND p2.AttemptNumber > pa.AttemptNumber)))
                    -- CORRECTION: una lectura VERIFIED del ciclo del job con otro serial, posterior al
                    -- último claim del job y a la observación que lo confirmó, si la hay (ADR 0016).
                    OR (t.Kind = 'CORRECTION' AND @kind IS NOT NULL AND @kind = 'CORRECTION'
                        AND c.ExceptionalPointerChanged = 0 AND c.OperatorPointerChanged = 0
                        AND i.ConfirmedByExceptionalReprint = 0
                        AND i.Reason IS NOT NULL AND i.Reason = 'SERIAL_MISMATCH'
                        AND EXISTS (
                            SELECT 1 FROM dbo.EvidenceObservation AS eo
                            WHERE eo.SiteId = i.SiteId AND eo.StationId = i.StationId
                              AND eo.PlcEpoch = i.PlcEpoch AND eo.CycleId = i.CycleId
                              AND eo.CorrelatedTo = 'CYCLE' AND eo.EvidenceLevel = 'VERIFIED'
                              AND eo.ReadSerial IS NOT NULL AND eo.ReadSerial <> i.LabelSerial
                              AND eo.ReceivedAt >= (SELECT MAX(lc.ClaimedAt) FROM dbo.PrintAttempt AS lc
                                                    WHERE lc.PrintJobId = i.PrintJobId)
                              AND NOT EXISTS (
                                  SELECT 1 FROM dbo.PrintAttempt AS cf
                                  WHERE cf.PrintJobId = i.PrintJobId AND cf.ConfirmingObservationId IS NOT NULL
                                    AND cf.ConfirmingObservationId >= eo.ObservationId)))
                )))
    BEGIN
        THROW 51012, N'Transición de PrintJob no permitida o sin el respaldo que exige su modo (contracts/state-machine.json).', 1;
    END;

    -- Estados que afirman que no existe etiqueta: ningún intento pudo imprimir sin prueba de lo contrario.
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.PrintJobId = i.PrintJobId
        WHERE (d.PrintJobId IS NULL OR d.Status <> i.Status)
          AND i.Status IN ('READY', 'RETRY_WAIT', 'FAILED', 'EXPIRED', 'CANCELLED')
          AND EXISTS (
              SELECT 1 FROM dbo.PrintAttempt AS pa
              WHERE pa.PrintJobId = i.PrintJobId AND pa.MayHaveSent = 1 AND pa.Outcome <> 'NOT_PRINTED'))
    BEGIN
        THROW 51025, N'El job tiene un intento que pudo imprimir: no puede pasar a un estado sin etiqueta.', 1;
    END;
END;
GO

CREATE TRIGGER dbo.TR_PrintAttempt_StateMachine
ON dbo.PrintAttempt
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.AttemptId = i.AttemptId
        WHERE (d.AttemptId IS NULL OR d.Outcome <> i.Outcome)
          AND NOT EXISTS (
              SELECT 1 FROM dbo.AllowedTransition AS t
              WHERE t.Machine = 'PrintAttempt'
                AND t.FromStatus = COALESCE(d.Outcome, '(create)')
                AND t.ToStatus = i.Outcome
                AND (   (t.Kind = 'AUTO' AND (i.OperatorActionId IS NULL OR d.OperatorActionId IS NOT NULL))
                     OR (t.Kind = 'MANUAL' AND d.OperatorActionId IS NULL AND EXISTS (
                             SELECT 1 FROM dbo.OperatorAction AS oa
                             WHERE oa.OperatorActionId = i.OperatorActionId
                               AND oa.PrintJobId = i.PrintJobId AND oa.ActionType = 'RECONCILE_NEGATIVE'
                               AND oa.PerformedAt >= i.ClaimedAt)))))
    BEGIN
        THROW 51013, N'Transición de PrintAttempt no permitida o sin la OperatorAction que exige (contracts/state-machine.json).', 1;
    END;
END;
GO

CREATE TRIGGER dbo.TR_SequenceGap_StateMachine
ON dbo.SequenceGap
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.GapId = i.GapId
        WHERE (d.GapId IS NULL OR d.Status <> i.Status)
          AND NOT EXISTS (
              SELECT 1 FROM dbo.AllowedTransition AS t
              WHERE t.Machine = 'SequenceGap'
                AND t.FromStatus = COALESCE(d.Status, '(create)')
                AND t.ToStatus = i.Status
                AND t.Kind = 'AUTO'))
    BEGIN
        THROW 51014, N'Transición de SequenceGap no permitida por contracts/state-machine.json.', 1;
    END;
END;
GO

CREATE TRIGGER dbo.TR_ReprintAuthorization_StateMachine
ON dbo.ReprintAuthorization
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    DECLARE @kind VARCHAR(12) = CONVERT(VARCHAR(12), SESSION_CONTEXT(N'TransitionKind'));
    IF EXISTS (
        SELECT 1
        FROM inserted AS i
        LEFT JOIN deleted AS d ON d.AuthorizationId = i.AuthorizationId
        WHERE (d.AuthorizationId IS NULL OR d.Status <> i.Status)
          AND NOT EXISTS (
              SELECT 1 FROM dbo.AllowedTransition AS t
              WHERE t.Machine = 'ReprintAuthorization'
                AND t.FromStatus = COALESCE(d.Status, '(create)')
                AND t.ToStatus = i.Status
                AND (t.Kind IN ('AUTO', 'MANUAL') OR (t.Kind = 'EXCEPTION' AND @kind IS NOT NULL AND @kind = 'EXCEPTION'))))
    BEGIN
        THROW 51015, N'Transición de ReprintAuthorization no permitida por contracts/state-machine.json.', 1;
    END;
END;
GO
