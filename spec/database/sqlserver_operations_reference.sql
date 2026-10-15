-- =====================================================================================
-- CartonPrinting v1.1 · Sentencias de referencia de las transacciones (DISEÑO, no código
-- productivo). Muestran guardas y orden de operaciones; la implementación las envuelve en
-- su capa de datos. Parseadas con sqlfluff; NO ejecutadas contra SQL Server.
-- Variables @... las calcula el EdgeHost. @EdgeNowUtc es el reloj del EdgeHost.
-- Reglas comunes:
--   * Después de un DML sobre una tabla con triggers se usa OUTPUT ... INTO una variable
--     de tabla para saber si afectó filas; nunca OUTPUT sin INTO (Msg 334).
--   * Toda transacción que fija SESSION_CONTEXT lo limpia también en el CATCH.
--   * Todas las tablas de estado tienen triggers que rechazan transiciones no sembradas.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- Serial: antes de TX-A o TX-E, fuera de la transacción (H3)
-- -------------------------------------------------------------------------------------
-- Se obtiene un valor por job (la revisión define un solo LabelType por estación, y
-- PrinterSendOrder supone una etiqueta por ciclo e impresora); la capa de datos arma LabelSerial con el
-- prefijo de estación, renderiza el payload con ese serial y calcula PayloadHash sobre los
-- bytes UTF-8 del payload completo. Si TX-A termina en duplicado o rollback, el valor se
-- pierde (hueco aceptado, ADR 0008). Un reintento de TX-A por 1205 reutiliza el mismo valor.
DECLARE @SerialNumber BIGINT;
SELECT @SerialNumber = NEXT VALUE FOR dbo.LabelSerialSeq;

-- -------------------------------------------------------------------------------------
-- TX-A · Registro de detección y creación del job (una transacción por ciclo)
-- -------------------------------------------------------------------------------------
-- 1. INSERT primero. Solo una violación de UQ_CartonDetection_Identity,
--    UQ_CartonDetection_IdempotencyKey o PK_CartonDetection se trata como clave existente;
--    cualquier otra 2627 o 2601 es un error del diseño y se propaga. Un 1205 (víctima de
--    interbloqueo) se reintenta completo: con inserción primero es seguro.
INSERT INTO dbo.CartonDetection (
    DetectionId, SiteId, StationId, PlcEpoch, CycleId, IdempotencyKey, SourceFingerprint,
    Origin, Transport, QueueOverflow, DetectionTimestamp, OpcSourceTimestamp, ReceivedAt,
    DeadlineReference, DeadlineAt, ConfigRevisionId, WorkOrderId, CartonId, Status, Reason)
VALUES (
    @DetectionId, @SiteId, @StationId, @PlcEpoch, @CycleId, @IdempotencyKey, @SourceFingerprint,
    @Origin, @Transport, @QueueOverflow, @DetectionTimestamp, @OpcSourceTimestamp, @ReceivedAt,
    @DeadlineReference, @DeadlineAt, @ConfigRevisionId, @WorkOrderId, @CartonId, 'RECEIVED', NULL);

-- 2. Solo en CARTON_ID_REQUIRED: bloqueo de rango para detectar recirculación (H8). Mira
--    jobs vivos de la caja y detecciones de la caja que esperan snapshot.
SELECT TOP (1) pj.PrintJobId
FROM dbo.PrintJob AS pj WITH (UPDLOCK, HOLDLOCK)
WHERE pj.SiteId = @SiteId
  AND pj.CartonId = @CartonId
  AND pj.Status IN ('READY', 'SENDING', 'TRANSMITTED', 'RETRY_WAIT', 'CONFIRMED', 'UNKNOWN', 'CLOSED_UNRESOLVED');

SELECT TOP (1) cd.DetectionId
FROM dbo.CartonDetection AS cd WITH (UPDLOCK, HOLDLOCK)
WHERE cd.SiteId = @SiteId
  AND cd.CartonId = @CartonId
  AND cd.Status = 'WAITING_ERP'
  AND cd.DetectionId <> @DetectionId;

-- 3. Con snapshot válido, plazo efectivo vigente con margen, estación habilitada y sin
--    recirculación: crear el job con todo lo congelado (serial ya incluido en el payload).
INSERT INTO dbo.PrintJob (
    PrintJobId, DetectionId, SiteId, StationId, PlcEpoch, CycleId, LabelType, ConfigRevisionId,
    PrinterId, WorkOrderId, ErpRevision, ProductId, LotId, CartonId, LabelSerial, TemplateRevision,
    Payload, PayloadHash, DeadlineAt, Status)
VALUES (
    @PrintJobId, @DetectionId, @SiteId, @StationId, @PlcEpoch, @CycleId, @LabelType, @ConfigRevisionId,
    @PrinterId, @WorkOrderId, @ErpRevision, @ProductId, @LotId, @CartonId, @LabelSerial, @TemplateRevision,
    @Payload, @PayloadHash, @DeadlineAt, 'READY');

-- 4. Estado final de la detección: JOB_CREATED, WAITING_ERP, EXPIRED o BLOCKED con su motivo.
--    Con GATEWAY_RECEIPT y un registro no recibido en vivo, CK_CartonDetection_GatewayLive
--    rechaza WAITING_ERP y JOB_CREATED: el código ya lo clasificó como EXPIRED LATE_ARRIVAL.
UPDATE dbo.CartonDetection
SET Status = @FinalDetectionStatus, Reason = @FinalDetectionReason, UpdatedAt = @EdgeNowUtc
WHERE DetectionId = @DetectionId AND Status = 'RECEIVED';

INSERT INTO dbo.OutboxEvent (EventId, EventType, OrderingKey, AggregateType, AggregateId, AggregateVersion, Payload)
VALUES (@DetectionId, 'CartonDetected', @IdempotencyKey, 'CartonDetection', @DetectionId, 1, @CartonDetectedJson);

-- -------------------------------------------------------------------------------------
-- TX-E · Llega un snapshot ERP para detecciones en WAITING_ERP (D06, D08)
-- -------------------------------------------------------------------------------------
-- Repite la validación de recirculación (paso 2 de TX-A) y lee la estación. Con estación
-- bloqueada o recirculación: D08. Con todo vigente: INSERT PrintJob (paso 3) y D06.
UPDATE dbo.CartonDetection
SET Status = 'BLOCKED', Reason = @BlockReason, UpdatedAt = @EdgeNowUtc
WHERE DetectionId = @DetectionId AND Status = 'WAITING_ERP';

UPDATE dbo.CartonDetection
SET Status = 'JOB_CREATED', UpdatedAt = @EdgeNowUtc
WHERE DetectionId = @DetectionId AND Status = 'WAITING_ERP';

-- -------------------------------------------------------------------------------------
-- TX-B · Claim y apertura del intento (antes de cualquier I/O)
-- -------------------------------------------------------------------------------------
-- @EffectiveDeadlineUtc es el plazo recalculado con la revisión activa; el plazo efectivo
-- es el menor entre ese y el congelado. @EffectiveSendMarginMs es el mayor margen entre
-- la revisión congelada y la activa. El orden se compara por AcceptedAt de la época.
DECLARE @claimed TABLE (
    PrintJobId UNIQUEIDENTIFIER NOT NULL, AutoAttemptCount TINYINT NOT NULL, PrinterId NVARCHAR(64) NOT NULL,
    PlcEpoch BIGINT NOT NULL, CycleId DECIMAL(20, 0) NOT NULL, EpochAcceptedAt DATETIME2(3) NOT NULL);

UPDATE pj
SET Status = 'SENDING', AutoAttemptCount = pj.AutoAttemptCount + 1, UpdatedAt = @EdgeNowUtc
OUTPUT inserted.PrintJobId, inserted.AutoAttemptCount, inserted.PrinterId, inserted.PlcEpoch, inserted.CycleId, eh.AcceptedAt
    INTO @claimed (PrintJobId, AutoAttemptCount, PrinterId, PlcEpoch, CycleId, EpochAcceptedAt)
FROM dbo.PrintJob AS pj
INNER JOIN dbo.StationConfig AS sc ON sc.SiteId = pj.SiteId AND sc.StationId = pj.StationId
INNER JOIN dbo.StationConfigRevision AS ar ON ar.ConfigRevisionId = sc.ActiveConfigRevisionId
INNER JOIN dbo.PlcEpochHistory AS eh
    ON eh.SiteId = pj.SiteId AND eh.StationId = pj.StationId AND eh.PlcEpoch = pj.PlcEpoch
INNER JOIN dbo.PrinterSendOrder AS so WITH (UPDLOCK)
    ON so.PrinterId = pj.PrinterId AND so.SiteId = pj.SiteId AND so.StationId = pj.StationId
WHERE pj.PrintJobId = @PrintJobId
  AND pj.Status = 'READY'
  AND pj.DeadlineAt > DATEADD(MILLISECOND, @EffectiveSendMarginMs, @EdgeNowUtc)
  AND @EffectiveDeadlineUtc > DATEADD(MILLISECOND, @EffectiveSendMarginMs, @EdgeNowUtc)
  AND sc.LabelingEnabled = 1
  AND ar.PrinterId = pj.PrinterId
  AND (   so.LastPrintJobId IS NULL
       OR so.LastPrintJobId = pj.PrintJobId
       OR (pj.PlcEpoch <> so.LastPlcEpoch AND eh.AcceptedAt > so.LastEpochAcceptedAt)
       OR (pj.PlcEpoch = so.LastPlcEpoch AND pj.CycleId > so.LastCycleId))
  AND EXISTS (
      SELECT 1 FROM dbo.PrinterLease AS pl
      WHERE pl.PrinterId = pj.PrinterId AND pl.OwnerInstanceId = @OwnerInstanceId
        AND pl.FencingToken = @LeaseToken AND pl.LeaseUntil > @EdgeNowUtc);

-- Sin fila en @claimed: otro proceso lo tomó, venció, la estación está bloqueada, la
-- impresora ya no es la de la revisión activa, un ciclo posterior ya se reclamó o el lease
-- no es vigente. El sender relee el job: si la causa es plazo u orden, aplica J03. Si falta
-- la fila de PrinterSendOrder de la impresora, el sender no arranca (lo comprueba al iniciar).
IF EXISTS (SELECT 1 FROM @claimed)
BEGIN
    -- El orden avanza con los valores de la fila reclamada, no con los que manda el código.
    UPDATE so
    SET LastPlcEpoch = c.PlcEpoch, LastEpochAcceptedAt = c.EpochAcceptedAt, LastCycleId = c.CycleId,
        LastPrintJobId = c.PrintJobId, UpdatedAt = @EdgeNowUtc
    FROM dbo.PrinterSendOrder AS so
    INNER JOIN @claimed AS c ON c.PrinterId = so.PrinterId;

    INSERT INTO dbo.PrintAttempt (
        AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId,
        LeaseToken, Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt)
    VALUES (
        @AttemptId, @PrintJobId, @AttemptNumber, 'ORDINARY', NULL, @PrinterId, @OwnerInstanceId,
        @LeaseToken, 'SENDING', 1, @PayloadHash, @EdgeNowUtc);
END;
-- COMMIT. Después, fuera de toda transacción: segunda comprobación monotónica y escritura.

-- J03 cuando el claim se rechazó por plazo u orden (también lo hace TX-S).
UPDATE dbo.PrintJob
SET Status = 'EXPIRED', Reason = @ExpiryReason, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'READY';

-- -------------------------------------------------------------------------------------
-- TX-C · Resultado de la escritura
-- -------------------------------------------------------------------------------------
-- Escritura completa (A03, J05). EvidenceDeadlineAt = WriteCompletedAt + EvidenceWindowMs
-- congelado (NULL si el nivel es NONE). Con NONE el job es terminal en TRANSMITTED y se
-- publica con ResultVersion + 1.
UPDATE dbo.PrintAttempt
SET Outcome = 'TRANSMITTED', WriteStartedAt = @WriteStartedAt, WriteCompletedAt = @WriteCompletedAt,
    PreSendCheckAt = @PreSendCheckAt, DeviceAcceptedAt = @DeviceAcceptedAt, EvidenceDeadlineAt = @EvidenceDeadlineAt
WHERE AttemptId = @AttemptId AND Outcome = 'SENDING';

UPDATE dbo.PrintJob
SET Status = 'TRANSMITTED', ResultVersion = ResultVersion + @PublishIncrement, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';
-- @PublishIncrement = 1 si MinConfirmationLevel congelado = NONE (se escribe Outbox), 0 si no.

-- Nunca se invocó la escritura (A02). Regla de código: el sender sabe que no llamó a write.
UPDATE dbo.PrintAttempt
SET Outcome = 'NOT_SENT', MayHaveSent = 0, PreSendCheckAt = @PreSendCheckAt, FailureDetail = @FailureDetail
WHERE AttemptId = @AttemptId AND Outcome = 'SENDING';

-- Con A02, una de: J06 reintento, J07 sin intentos, J08 plazo vencido. Si la segunda
-- comprobación rechazó por compuerta o estación bloqueada con el plazo vigente, J06 sin
-- consumir intento (@RestoreAttempt = 1): ese rechazo no es una falla de la impresora.
UPDATE dbo.PrintJob
SET Status = 'RETRY_WAIT', AutoAttemptCount = AutoAttemptCount - @RestoreAttempt,
    NextAttemptAt = DATEADD(MILLISECOND, @RetryBackoffMs, @EdgeNowUtc), UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'SENDING'
  AND (@RestoreAttempt = 1 OR AutoAttemptCount < @EffectiveMaxAutoAttempts);

UPDATE dbo.PrintJob
SET Status = 'FAILED', Reason = 'PRINTER_UNAVAILABLE', NextAttemptAt = NULL,
    ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';

UPDATE dbo.PrintJob
SET Status = 'EXPIRED', Reason = 'DEADLINE_PASSED', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';

-- Error después de invocar la escritura (A04 y J09 en un intento ORDINARY).
UPDATE dbo.PrintAttempt
SET Outcome = 'UNKNOWN', WriteStartedAt = @WriteStartedAt, FailureDetail = @FailureDetail
WHERE AttemptId = @AttemptId AND Outcome = 'SENDING';

UPDATE dbo.PrintJob
SET Status = 'UNKNOWN', Reason = 'WRITE_INTERRUPTED', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'SENDING';

-- En un intento EXCEPTIONAL_REPRINT, A04 y X03 van en la misma transacción: el job no puede
-- quedar en un estado que afirme que no hay etiqueta mientras la reimpresión pudo imprimir.
BEGIN TRY
    BEGIN TRANSACTION;
    UPDATE dbo.PrintAttempt
    SET Outcome = 'UNKNOWN', WriteStartedAt = @WriteStartedAt, FailureDetail = @FailureDetail
    WHERE AttemptId = @AttemptId AND Kind = 'EXCEPTIONAL_REPRINT' AND Outcome = 'SENDING';
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = 'EXCEPTION';
    UPDATE dbo.PrintJob
    SET Status = 'UNKNOWN', Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED', ConfirmedByExceptionalReprint = 0,
        LastExceptionalAttemptId = @AttemptId, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
    WHERE PrintJobId = @PrintJobId;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    THROW;
END CATCH;

-- -------------------------------------------------------------------------------------
-- TX-D · Evidencia (A05, A08, J10, J15, J11, J12, J18)
-- -------------------------------------------------------------------------------------
-- 1. Toda evidencia se registra primero, en su propia transacción, confirme o no: un rechazo
--    posterior de un trigger no puede borrar la observación.
DECLARE @observation TABLE (ObservationId BIGINT NOT NULL);
DECLARE @ObservationId BIGINT;

INSERT INTO dbo.EvidenceObservation (
    SiteId, StationId, PlcEpoch, CycleId, PrintJobId, AttemptId, EvidenceType, EvidenceLevel,
    CorrelatedTo, ReadSerial, SourceReference, ObservedAt, ReceivedAt, RecordedBy)
OUTPUT inserted.ObservationId INTO @observation (ObservationId)
VALUES (
    @SiteId, @StationId, @EvPlcEpoch, @EvCycleId, @EvPrintJobId, @EvAttemptId, @EvidenceType, @EvidenceLevel,
    @CorrelatedTo, @ReadSerial, @SourceReference, @ObservedAt, @EdgeNowUtc, @RecordedBy);

SELECT @ObservationId = ObservationId FROM @observation;
-- COMMIT. Después, en una transacción nueva, se aplica (2a a 2d).

-- 2a. Evidencia suficiente: el trigger TR_PrintAttempt_Guard valida correlación, nivel y
--     ReadSerial = LabelSerial. Después J10 (desde TRANSMITTED) o J15 (desde UNKNOWN con
--     motivo WRITE_INTERRUPTED, RESTART_DURING_SEND o EVIDENCE_WINDOW_ELAPSED).
UPDATE dbo.PrintAttempt
SET Outcome = 'CONFIRMED', ConfirmingObservationId = @ObservationId
WHERE AttemptId = @AttemptId AND Kind = 'ORDINARY' AND Outcome IN ('TRANSMITTED', 'UNKNOWN');

UPDATE dbo.PrintJob
SET Status = 'CONFIRMED', Reason = NULL, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status IN ('TRANSMITTED', 'UNKNOWN');

-- 2b. Serial distinto con el job en TRANSMITTED: A06 y J11 con SERIAL_MISMATCH. Si el intento
--     es EXCEPTIONAL_REPRINT, A06 y X03 van juntos como en TX-C.
UPDATE dbo.PrintAttempt
SET Outcome = 'UNKNOWN', FailureDetail = N'Evidencia contradictoria'
WHERE AttemptId = @AttemptId AND Outcome = 'TRANSMITTED';

UPDATE dbo.PrintJob
SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'TRANSMITTED';

-- 2c. Serial distinto (lectura VERIFIED) con el job CONFIRMED o UNKNOWN: corrección J18.
BEGIN TRY
    BEGIN TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = 'CORRECTION';
    UPDATE dbo.PrintJob
    SET Status = 'UNKNOWN', Reason = 'SERIAL_MISMATCH', ConfirmedByExceptionalReprint = 0,
        ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
    WHERE PrintJobId = @PrintJobId AND Status IN ('CONFIRMED', 'UNKNOWN');
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    THROW;
END CATCH;

-- 2d. El dispositivo confirma cancelación o descarte del job y cola sin él (A07, J12).
UPDATE dbo.PrintAttempt
SET Outcome = 'NOT_PRINTED', FailureDetail = @DeviceReport
WHERE AttemptId = @AttemptId AND Outcome = 'TRANSMITTED';

UPDATE dbo.PrintJob
SET Status = 'FAILED', Reason = 'DEVICE_REPORTED_NOT_PRINTED', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'TRANSMITTED';

-- -------------------------------------------------------------------------------------
-- TX-R0 · Recuperación al arrancar (después del lock y del lease; antes de suscribirse)
-- -------------------------------------------------------------------------------------
-- Intentos ORDINARY abiertos de la impresora: A04 y J09 con RESTART_DURING_SEND.
UPDATE dbo.PrintAttempt
SET Outcome = 'UNKNOWN', FailureDetail = N'Arranque encontró el intento abierto'
WHERE Outcome = 'SENDING' AND PrinterId = @PrinterId AND Kind = 'ORDINARY';

UPDATE dbo.PrintJob
SET Status = 'UNKNOWN', Reason = 'RESTART_DURING_SEND', ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE Status = 'SENDING' AND PrinterId = @PrinterId;

-- Intentos EXCEPTIONAL_REPRINT abiertos de la impresora: A04 y X03 en la misma transacción.
DECLARE @reopened TABLE (AttemptId UNIQUEIDENTIFIER NOT NULL, PrintJobId UNIQUEIDENTIFIER NOT NULL);
BEGIN TRY
    BEGIN TRANSACTION;
    UPDATE dbo.PrintAttempt
    SET Outcome = 'UNKNOWN', FailureDetail = N'Arranque encontró la reimpresión abierta'
    OUTPUT inserted.AttemptId, inserted.PrintJobId INTO @reopened (AttemptId, PrintJobId)
    WHERE Outcome = 'SENDING' AND PrinterId = @PrinterId AND Kind = 'EXCEPTIONAL_REPRINT';
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = 'EXCEPTION';
    UPDATE pj
    SET Status = 'UNKNOWN', Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED', ConfirmedByExceptionalReprint = 0,
        LastExceptionalAttemptId = r.AttemptId, ResultVersion = pj.ResultVersion + 1, UpdatedAt = @EdgeNowUtc
    FROM dbo.PrintJob AS pj
    INNER JOIN @reopened AS r ON r.PrintJobId = pj.PrintJobId;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    THROW;
END CATCH;

-- -------------------------------------------------------------------------------------
-- TX-S · Barrido por estación (cierra y publica; no es la protección contra envíos tardíos)
-- -------------------------------------------------------------------------------------
-- Margen efectivo por job: el mayor entre el congelado y el de la revisión activa.
UPDATE pj
SET Status = 'EXPIRED', Reason = 'DEADLINE_PASSED', NextAttemptAt = NULL,
    ResultVersion = pj.ResultVersion + 1, UpdatedAt = @EdgeNowUtc
FROM dbo.PrintJob AS pj
INNER JOIN dbo.StationConfigRevision AS fr ON fr.ConfigRevisionId = pj.ConfigRevisionId
WHERE pj.SiteId = @SiteId AND pj.StationId = @StationId
  AND pj.Status IN ('READY', 'RETRY_WAIT')
  AND pj.DeadlineAt <= DATEADD(
      MILLISECOND,
      CASE WHEN fr.SendMarginMs > @ActiveSendMarginMs THEN fr.SendMarginMs ELSE @ActiveSendMarginMs END,
      @EdgeNowUtc);

-- Fuera de orden: la impresora ya reclamó un ciclo posterior (J03 y J14 con OUT_OF_ORDER).
UPDATE pj
SET Status = 'EXPIRED', Reason = 'OUT_OF_ORDER', NextAttemptAt = NULL,
    ResultVersion = pj.ResultVersion + 1, UpdatedAt = @EdgeNowUtc
FROM dbo.PrintJob AS pj
INNER JOIN dbo.PrinterSendOrder AS so ON so.PrinterId = pj.PrinterId
INNER JOIN dbo.PlcEpochHistory AS eh
    ON eh.SiteId = pj.SiteId AND eh.StationId = pj.StationId AND eh.PlcEpoch = pj.PlcEpoch
WHERE pj.SiteId = @SiteId AND pj.StationId = @StationId
  AND pj.Status IN ('READY', 'RETRY_WAIT')
  AND so.LastPrintJobId IS NOT NULL AND so.LastPrintJobId <> pj.PrintJobId
  AND (   eh.AcceptedAt < so.LastEpochAcceptedAt
       OR (pj.PlcEpoch = so.LastPlcEpoch AND pj.CycleId < so.LastCycleId));

UPDATE dbo.CartonDetection
SET Status = 'EXPIRED', Reason = 'ERP_UNAVAILABLE', UpdatedAt = @EdgeNowUtc
WHERE SiteId = @SiteId AND StationId = @StationId
  AND Status = 'WAITING_ERP'
  AND DeadlineAt <= DATEADD(MILLISECOND, @ActiveSendMarginMs, @EdgeNowUtc);

-- Ventana de evidencia vencida, por intento (A06), con la guarda de su propio plazo. A06 y la
-- transición del job (J11 u X03) van en una sola transacción.
DECLARE @elapsed TABLE (AttemptId UNIQUEIDENTIFIER NOT NULL, PrintJobId UNIQUEIDENTIFIER NOT NULL, Kind VARCHAR(20) NOT NULL);

BEGIN TRY
    BEGIN TRANSACTION;
    UPDATE dbo.PrintAttempt
    SET Outcome = 'UNKNOWN', FailureDetail = N'Ventana de evidencia vencida'
    OUTPUT inserted.AttemptId, inserted.PrintJobId, inserted.Kind INTO @elapsed (AttemptId, PrintJobId, Kind)
    WHERE AttemptId = @AttemptId AND Outcome = 'TRANSMITTED'
      AND EvidenceDeadlineAt IS NOT NULL AND EvidenceDeadlineAt <= @EdgeNowUtc;

    -- Intento ORDINARY: J11.
    UPDATE pj
    SET Status = 'UNKNOWN', Reason = 'EVIDENCE_WINDOW_ELAPSED', ResultVersion = pj.ResultVersion + 1, UpdatedAt = @EdgeNowUtc
    FROM dbo.PrintJob AS pj
    INNER JOIN @elapsed AS e ON e.PrintJobId = pj.PrintJobId AND e.Kind = 'ORDINARY'
    WHERE pj.Status = 'TRANSMITTED';

    -- Intento EXCEPTIONAL_REPRINT: X03 (desde UNKNOWN solo cambian Reason y ResultVersion).
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = 'EXCEPTION';
    UPDATE pj
    SET Status = 'UNKNOWN', Reason = 'EXCEPTIONAL_REPRINT_UNRESOLVED', ConfirmedByExceptionalReprint = 0,
        LastExceptionalAttemptId = e.AttemptId, ResultVersion = pj.ResultVersion + 1, UpdatedAt = @EdgeNowUtc
    FROM dbo.PrintJob AS pj
    INNER JOIN @elapsed AS e ON e.PrintJobId = pj.PrintJobId AND e.Kind = 'EXCEPTIONAL_REPRINT';
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    THROW;
END CATCH;

UPDATE dbo.ReprintAuthorization
SET Status = 'EXPIRED'
WHERE Status = 'APPROVED' AND ExpiresAt <= @EdgeNowUtc;

-- -------------------------------------------------------------------------------------
-- TX-M · Acción manual (Operations API). Ejemplo: reconciliación negativa (A09, J16)
-- -------------------------------------------------------------------------------------
DECLARE @action TABLE (OperatorActionId BIGINT NOT NULL);
DECLARE @OperatorActionId BIGINT;

INSERT INTO dbo.OperatorAction (
    ActionType, SiteId, StationId, PrintJobId, TargetStatus, PerformedBy, PerformedAt, ReasonText,
    DeviceNotPrintedEvidence, DeviceQueueClearedEvidence, CartonInspectionEvidence)
OUTPUT inserted.OperatorActionId INTO @action (OperatorActionId)
VALUES (
    'RECONCILE_NEGATIVE', @SiteId, @StationId, @PrintJobId, 'FAILED', @PerformedBy, @EdgeNowUtc, @ReasonText,
    @DeviceNotPrintedEvidence, @DeviceQueueClearedEvidence, @CartonInspectionEvidence);

SELECT @OperatorActionId = OperatorActionId FROM @action;

-- Todos los intentos del job que pudieron imprimir pasan a NOT_PRINTED antes que el job.
UPDATE dbo.PrintAttempt
SET Outcome = 'NOT_PRINTED', OperatorActionId = @OperatorActionId
WHERE PrintJobId = @PrintJobId AND Outcome = 'UNKNOWN';

UPDATE dbo.PrintJob
SET Status = 'FAILED', Reason = 'RECONCILED_NOT_PRINTED', LastOperatorActionId = @OperatorActionId,
    ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
WHERE PrintJobId = @PrintJobId AND Status = 'UNKNOWN';

-- TX-M · Desbloqueo de una estación (TR_StationConfig_Guard exige la OperatorAction).
DELETE FROM @action;
INSERT INTO dbo.OperatorAction (ActionType, SiteId, StationId, PerformedBy, PerformedAt, ReasonText)
OUTPUT inserted.OperatorActionId INTO @action (OperatorActionId)
VALUES ('UNBLOCK_STATION', @SiteId, @StationId, @PerformedBy, @EdgeNowUtc, @ReasonText);

SELECT @OperatorActionId = OperatorActionId FROM @action;

UPDATE dbo.StationConfig
SET LabelingEnabled = 1, BlockReason = NULL, BlockedAt = NULL, BlockedBy = NULL, BlockedByAnomalyId = NULL,
    LastUnblockedBy = @PerformedBy, LastUnblockedAt = @EdgeNowUtc, LastOperatorActionId = @OperatorActionId
WHERE SiteId = @SiteId AND StationId = @StationId AND LabelingEnabled = 0;

-- -------------------------------------------------------------------------------------
-- TX-X · Reimpresión excepcional. La ejecuta el sender de TargetPrinterId, con su lease;
-- la Operations API solo crea la autorización y la pone en la cola de ese sender.
-- -------------------------------------------------------------------------------------
DECLARE @executed TABLE (AuthorizationId BIGINT NOT NULL);

BEGIN TRY
    BEGIN TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = 'EXCEPTION';

    UPDATE ra
    SET Status = 'EXECUTED', ExecutedAt = @EdgeNowUtc
    OUTPUT inserted.AuthorizationId INTO @executed (AuthorizationId)
    FROM dbo.ReprintAuthorization AS ra
    INNER JOIN dbo.PrintJob AS pj ON pj.PrintJobId = ra.PrintJobId
    WHERE ra.AuthorizationId = @AuthorizationId
      AND ra.Status = 'APPROVED'
      AND ra.ExpiresAt > @EdgeNowUtc
      AND ra.TargetPrinterId = @PrinterId
      AND pj.Status = ra.PriorJobStatus
      AND EXISTS (
          SELECT 1 FROM dbo.PrinterLease AS pl
          WHERE pl.PrinterId = @PrinterId AND pl.OwnerInstanceId = @OwnerInstanceId
            AND pl.FencingToken = @LeaseToken AND pl.LeaseUntil > @EdgeNowUtc);

    IF NOT EXISTS (SELECT 1 FROM @executed)
    BEGIN
        THROW 52001, N'Autorización no ejecutable: vencida, revocada, usada, job en otro estado o lease no vigente.', 1;
    END;

    -- TR_PrintAttempt_Guard exige además que @PrinterId sea la ManualPrinterId activa.
    INSERT INTO dbo.PrintAttempt (
        AttemptId, PrintJobId, AttemptNumber, Kind, AuthorizationId, PrinterId, OwnerInstanceId,
        LeaseToken, Outcome, MayHaveSent, AttemptPayloadHash, ClaimedAt)
    VALUES (
        @AttemptId, @PrintJobId, @AttemptNumber, 'EXCEPTIONAL_REPRINT', @AuthorizationId, @PrinterId,
        @OwnerInstanceId, @LeaseToken, 'SENDING', 1, @ReprintPayloadHash, @EdgeNowUtc);

    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    THROW;
END CATCH;
-- Después, la misma secuencia de escritura que un intento ordinario (TX-C), con
-- EvidenceDeadlineAt = WriteCompletedAt + EvidenceWindowMs de la autorización.

-- Intento excepcional confirmado (A05 o A08) y X01 o X02, en una transacción. La
-- EvidenceObservation @ObservationId debe referenciar a este intento (AttemptId).
BEGIN TRY
    BEGIN TRANSACTION;
    UPDATE dbo.PrintAttempt
    SET Outcome = 'CONFIRMED', ConfirmingObservationId = @ObservationId
    WHERE AttemptId = @AttemptId AND Kind = 'EXCEPTIONAL_REPRINT' AND Outcome IN ('TRANSMITTED', 'UNKNOWN');

    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = 'EXCEPTION';
    UPDATE dbo.PrintJob
    SET Status = 'CONFIRMED', Reason = NULL, ConfirmedByExceptionalReprint = 1,
        LastExceptionalAttemptId = @AttemptId, ResultVersion = ResultVersion + 1, UpdatedAt = @EdgeNowUtc
    WHERE PrintJobId = @PrintJobId;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    EXEC sys.sp_set_session_context @key = N'TransitionKind', @value = NULL;
    THROW;
END CATCH;

-- -------------------------------------------------------------------------------------
-- Lease de impresora (detección de una segunda instancia; no es fencing físico)
-- -------------------------------------------------------------------------------------
-- PrinterLease no tiene triggers: OUTPUT sin INTO es válido aquí.
UPDATE dbo.PrinterLease
SET FencingToken = CASE WHEN OwnerInstanceId = @OwnerInstanceId THEN FencingToken ELSE FencingToken + 1 END,
    OwnerInstanceId = @OwnerInstanceId, OwnerHost = @OwnerHost,
    AcquiredAt = @EdgeNowUtc, LeaseUntil = DATEADD(MILLISECOND, @LeaseDurationMs, @EdgeNowUtc)
OUTPUT inserted.FencingToken
WHERE PrinterId = @PrinterId
  AND (OwnerInstanceId = @OwnerInstanceId OR LeaseUntil < DATEADD(MILLISECOND, -@LeaseGraceMs, @EdgeNowUtc));
