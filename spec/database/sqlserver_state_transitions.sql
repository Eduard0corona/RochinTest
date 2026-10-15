-- GENERADO por tools/generate_artifacts.py desde contracts/state-machine.json. No editar.
-- Ejecutar después de database/sqlserver_schema.sql. Los triggers TR_*_StateMachine
-- rechazan cualquier cambio de estado que no esté en esta tabla. Kind = modo de la
-- transición: AUTO, MANUAL (exige OperatorAction), EXCEPTION (exige intento excepcional
-- nuevo) o CORRECTION (exige EvidenceObservation contradictoria).
SET XACT_ABORT ON;
SET NOCOUNT ON;
GO
BEGIN TRANSACTION;
DELETE FROM dbo.AllowedTransition;
INSERT INTO dbo.AllowedTransition (Machine, FromStatus, ToStatus, Kind, TransitionId)
VALUES
    ('CartonDetection', '(create)', 'RECEIVED', 'AUTO', 'D01'),
    ('CartonDetection', 'RECEIVED', 'JOB_CREATED', 'AUTO', 'D02'),
    ('CartonDetection', 'RECEIVED', 'WAITING_ERP', 'AUTO', 'D03'),
    ('CartonDetection', 'RECEIVED', 'EXPIRED', 'AUTO', 'D04'),
    ('CartonDetection', 'RECEIVED', 'BLOCKED', 'AUTO', 'D05'),
    ('CartonDetection', 'WAITING_ERP', 'JOB_CREATED', 'AUTO', 'D06'),
    ('CartonDetection', 'WAITING_ERP', 'EXPIRED', 'AUTO', 'D07'),
    ('CartonDetection', 'WAITING_ERP', 'BLOCKED', 'AUTO', 'D08'),
    ('PrintJob', '(create)', 'READY', 'AUTO', 'J01'),
    ('PrintJob', 'READY', 'SENDING', 'AUTO', 'J02'),
    ('PrintJob', 'READY', 'EXPIRED', 'AUTO', 'J03'),
    ('PrintJob', 'READY', 'CANCELLED', 'MANUAL', 'J04'),
    ('PrintJob', 'SENDING', 'TRANSMITTED', 'AUTO', 'J05'),
    ('PrintJob', 'SENDING', 'RETRY_WAIT', 'AUTO', 'J06'),
    ('PrintJob', 'SENDING', 'FAILED', 'AUTO', 'J07'),
    ('PrintJob', 'SENDING', 'EXPIRED', 'AUTO', 'J08'),
    ('PrintJob', 'SENDING', 'UNKNOWN', 'AUTO', 'J09'),
    ('PrintJob', 'TRANSMITTED', 'CONFIRMED', 'AUTO', 'J10'),
    ('PrintJob', 'TRANSMITTED', 'UNKNOWN', 'AUTO', 'J11'),
    ('PrintJob', 'TRANSMITTED', 'FAILED', 'AUTO', 'J12'),
    ('PrintJob', 'RETRY_WAIT', 'READY', 'AUTO', 'J13'),
    ('PrintJob', 'RETRY_WAIT', 'EXPIRED', 'AUTO', 'J14'),
    ('PrintJob', 'UNKNOWN', 'CONFIRMED', 'AUTO', 'J15'),
    ('PrintJob', 'UNKNOWN', 'FAILED', 'MANUAL', 'J16'),
    ('PrintJob', 'UNKNOWN', 'CLOSED_UNRESOLVED', 'MANUAL', 'J17'),
    ('PrintJob', 'CONFIRMED', 'UNKNOWN', 'CORRECTION', 'J18'),
    ('PrintJob', 'UNKNOWN', 'UNKNOWN', 'CORRECTION', 'J18'),
    ('PrintJob', 'UNKNOWN', 'CONFIRMED', 'EXCEPTION', 'X01'),
    ('PrintJob', 'FAILED', 'CONFIRMED', 'EXCEPTION', 'X01'),
    ('PrintJob', 'EXPIRED', 'CONFIRMED', 'EXCEPTION', 'X01'),
    ('PrintJob', 'CLOSED_UNRESOLVED', 'CONFIRMED', 'EXCEPTION', 'X01'),
    ('PrintJob', 'CONFIRMED', 'CONFIRMED', 'EXCEPTION', 'X02'),
    ('PrintJob', 'UNKNOWN', 'UNKNOWN', 'EXCEPTION', 'X03'),
    ('PrintJob', 'FAILED', 'UNKNOWN', 'EXCEPTION', 'X03'),
    ('PrintJob', 'EXPIRED', 'UNKNOWN', 'EXCEPTION', 'X03'),
    ('PrintJob', 'CLOSED_UNRESOLVED', 'UNKNOWN', 'EXCEPTION', 'X03'),
    ('PrintJob', 'CONFIRMED', 'UNKNOWN', 'EXCEPTION', 'X03'),
    ('PrintAttempt', '(create)', 'SENDING', 'AUTO', 'A01'),
    ('PrintAttempt', 'SENDING', 'NOT_SENT', 'AUTO', 'A02'),
    ('PrintAttempt', 'SENDING', 'TRANSMITTED', 'AUTO', 'A03'),
    ('PrintAttempt', 'SENDING', 'UNKNOWN', 'AUTO', 'A04'),
    ('PrintAttempt', 'TRANSMITTED', 'CONFIRMED', 'AUTO', 'A05'),
    ('PrintAttempt', 'TRANSMITTED', 'UNKNOWN', 'AUTO', 'A06'),
    ('PrintAttempt', 'TRANSMITTED', 'NOT_PRINTED', 'AUTO', 'A07'),
    ('PrintAttempt', 'UNKNOWN', 'CONFIRMED', 'AUTO', 'A08'),
    ('PrintAttempt', 'UNKNOWN', 'NOT_PRINTED', 'MANUAL', 'A09'),
    ('SequenceGap', '(create)', 'OPEN', 'AUTO', 'G01'),
    ('SequenceGap', 'OPEN', 'RECOVERED', 'AUTO', 'G02'),
    ('SequenceGap', 'OPEN', 'CLOSED_UNRECOVERED', 'AUTO', 'G03'),
    ('ReprintAuthorization', '(create)', 'APPROVED', 'MANUAL', 'R01'),
    ('ReprintAuthorization', 'APPROVED', 'EXECUTED', 'EXCEPTION', 'R02'),
    ('ReprintAuthorization', 'APPROVED', 'REVOKED', 'MANUAL', 'R03'),
    ('ReprintAuthorization', 'APPROVED', 'EXPIRED', 'AUTO', 'R04');
COMMIT TRANSACTION;
GO
