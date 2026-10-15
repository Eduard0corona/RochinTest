-- =====================================================================================
-- CartonPrinting v1.1 · Journal local del gateway (SOLO perfil extendido, ADR 0009)
-- Ejecutado en esta revisión con sqlite3 en memoria (ver docs/REVISION_LOG_v1.1.md).
-- Propósito: conservar ciclos con ACK al PLC mientras SQL Server no está disponible.
-- El journal no crea jobs ni decide impresiones; al volver SQL se reproduce con la misma
-- identidad, Transport = JOURNAL_REPLAY, el ReceivedAt original y la revisión con la que se
-- capturó; la clasificación de TX-A decide (normalmente EXPIRED por LATE_ARRIVAL, y siempre
-- así con DeadlineReference = GATEWAY_RECEIPT).
-- Configuración obligatoria por conexión (validar con pruebas de corte de energía):
--   PRAGMA journal_mode = WAL;
--   PRAGMA synchronous = FULL;
-- =====================================================================================

CREATE TABLE IF NOT EXISTS CapturedCycle (
    SiteId              TEXT    NOT NULL,
    StationId           TEXT    NOT NULL,
    PlcEpoch            INTEGER NOT NULL CHECK (PlcEpoch BETWEEN 0 AND 4294967295),
    -- Cadena decimal canónica de un UInt64: solo dígitos, sin ceros a la izquierda y como máximo
    -- 18446744073709551615 (comparación de texto válida entre cadenas de 20 dígitos).
    CycleId             TEXT    NOT NULL CHECK (
                            length(CycleId) BETWEEN 1 AND 20
                            AND CycleId NOT GLOB '*[^0-9]*'
                            AND (CycleId = '0' OR CycleId NOT GLOB '0*')
                            AND (length(CycleId) < 20 OR CycleId <= '18446744073709551615')),
    IdempotencyKey      TEXT    NOT NULL,
    EventId             TEXT    NOT NULL,
    SourceFingerprint   TEXT    NOT NULL CHECK (length(SourceFingerprint) = 64),
    Transport           TEXT    NOT NULL CHECK (Transport IN ('SUBSCRIPTION', 'REPUBLISH', 'PLC_FIFO', 'READ')),
    QueueOverflow       INTEGER NOT NULL CHECK (QueueOverflow IN (0, 1)),
    DetectionTimestamp  TEXT    NULL,
    OpcSourceTimestamp  TEXT    NULL,
    ReceivedAt          TEXT    NOT NULL,          -- recepción original; el replay no la cambia
    ConfigRevisionId    INTEGER NOT NULL,          -- revisión activa al capturar
    WorkOrderId         TEXT    NULL,
    CartonId            TEXT    NULL,
    RawRecord           TEXT    NOT NULL,          -- registro latched completo (JSON)
    CapturedAt          TEXT    NOT NULL,
    PlcAckedAt          TEXT    NULL,              -- se fija después del COMMIT de esta fila
    SqlReplicatedAt     TEXT    NULL,
    PRIMARY KEY (SiteId, StationId, PlcEpoch, CycleId),
    UNIQUE (IdempotencyKey),
    UNIQUE (EventId)
);

CREATE INDEX IF NOT EXISTS IX_CapturedCycle_Pending ON CapturedCycle (SqlReplicatedAt, CapturedAt);

-- Duplicado en el journal: INSERT ... ON CONFLICT (SiteId, StationId, PlcEpoch, CycleId) DO NOTHING
-- y comparación de SourceFingerprint con la fila existente (misma regla que en SQL Server).
-- El ACK al PLC (AckCycleId) significa "ciclo capturado de forma durable", nunca "etiqueta impresa".
