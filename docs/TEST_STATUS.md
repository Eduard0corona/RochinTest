# Estado de las pruebas T01 a T47

Estado real de cada prueba del catálogo (`spec/tests/test-catalog.json`). `spec/tests/test-catalog.json` no se edita para registrar resultados: el estado vive aquí. Una prueba solo cuenta como `PASA` si se ejecutó, con su comando, fecha y salida en `docs/IMPLEMENTATION_LOG.md`.

- **Estado:** `PASA`, `FALLA`, `NO_EJECUTADA` o `NO_APLICA` (con motivo).
- **Entorno:** `SQL Server real` (Testcontainers con SQL Server 2022), `simulador` o `proceso`.
- Una prueba cuyo criterio incluye efectos físicos se mide en el simulador, y la nota lo dice: ningún resultado de esta tabla demuestra una garantía física.

| Txx | Fase | Estado | Pruebas xUnit | Entorno | Comando y fecha | Notas |
| --- | --- | --- | --- | --- | --- | --- |
| T01 | 1 | PASA | `IntegrationTests/Idempotency/DuplicateTagTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 20:20 | 100 entregas (SUBSCRIPTION, REPUBLISH, READ) desde 10 consumidores: 1 detección JOB_CREATED, 1 job, 1 intento, 1 CartonDetected, 1 PrintJobResult; duplicate_detection_total = 99 por transporte. **Cobertura parcial:** la transmisión se mide en una impresora en memoria con un arnés de sender; T01 se repite en la Fase 4 con el sender real y el simulador TCP. |
| T02 | 1 | PASA | `IntegrationTests/Idempotency/ClaimConcurrencyTests` (5 rondas y una con bloqueo retenido) | SQL Server real | `build/ci.ps1` · 2026-10-14 20:20 | 10 claims concurrentes: 1 ganador, 9 con @claimed vacío y sin excepción, claim_conflicts_total = 9, 1 intento SENDING, ningún evento adicional. Con un `UPDLOCK` retenido por otra transacción sobre la fila del job, ningún claim termina hasta liberarlo y después gana uno solo. |
| T03 | 3 | NO_EJECUTADA | — | — | — |  |
| T04 | 3 | NO_EJECUTADA | — | — | — |  |
| T05 | 4 | NO_EJECUTADA | — | — | — |  |
| T06 | 4 | NO_EJECUTADA | — | — | — |  |
| T07 | 4 | NO_EJECUTADA | — | — | — |  |
| T08 | 7 | NO_EJECUTADA | — | — | — |  |
| T09 | 6 | NO_EJECUTADA | — | — | — | Perfil extendido (Fase 6). |
| T10 | 1 | PASA | `IntegrationTests/Idempotency/LotChangeRaceTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 20:20 | R2 llega con el job READY; UPDATE de Payload, ErpRevision, LotId y PayloadHash fallan con 51005; el resultado publicado y la etiqueta transmitida (impresora en memoria) llevan el lote de R1. |
| T11 | 2 | PASA | `FaultTests/Opc/FifoReconnectTests` | SQL Server real y simulador OPC UA | `build/ci.ps1` · 2026-10-14 23:50 | Cliente OPC UA real contra el PLC simulado; se desconecta el gateway (enlace congelado más que la sesión) mientras el PLC fija cada caja en su registro y su FIFO (FIFO simulada; el journal y AckCycleId son de la Fase 6): la lectura tras reconectar (1021, `READ`) abre el hueco 1001-1020; la FIFO entrega los 20 como `RECOVERED` (`PLC_FIFO`), `EXPIRED` con `LATE_ARRIVAL` y sin job; hueco `RECOVERED`; 22 detecciones únicas; jobs solo para 1000 y 1021; `opc_reconnect_total` ≥ 1; `sequence_gaps_open` = 0. Escenario de la especificación 1.1.4 (SI-11, edición aprobada). |
| T12 | 7 | NO_EJECUTADA | — | — | — |  |
| T13 | 2 | PASA | `FaultTests/Sequence/IncoherentSnapshotTests`, `SequenceReviewTests`, `UnitTests/OpcIngest/CycleDecoderTests`, `UnitTests/Orchestration/IncoherentSequenceTests` | SQL Server real y simulador OPC UA | `build/ci.ps1` · 2026-10-14 23:50 | El PLC simulado publica el CycleId 501 con la orden del 502 (eco de cierre 502): `BLOCKED` con `INCOHERENT_SNAPSHOT`, sin job, `CartonDetected` y `PrintJobResult` BLOCKED. Variante `PLC_TIMESTAMP` sin `DetectionTimestamp`: `BLOCKED` con `INCOHERENT_SNAPSHOT` y `DeadlineAt = ReceivedAt`. `detections_blocked_total{reason=INCOHERENT_SNAPSHOT}` = 2. El mecanismo de coherencia es del simulador (SI-10). El incoherente abre su hueco y mueve la marca alta sin su hora, igual en memoria que al recargar desde SQL; no acepta épocas (SI-13). |
| T14 | 1 | PASA | `IntegrationTests/Idempotency/RepublishedResultTests` | SQL Server real y RabbitMQ real | `build/ci.ps1` · 2026-10-14 20:20 | El relay publica 3 veces el mismo EventId (confirm "perdido"); el consumidor con InboxMessage aplica 1 efecto; inbox_duplicates_total = 2; con versiones desordenadas conserva la mayor por (idempotencyKey, labelType). El Inbox y la deduplicación son de `InboxConsumer` (src); el efecto "conserva la versión mayor" es del consumidor de prueba, porque los consumidores reales están fuera del MVP. |
| T15 | 4 | NO_EJECUTADA | — | — | — |  |
| T16 | 7 | NO_EJECUTADA | — | — | — |  |
| T17 | 3 | NO_EJECUTADA | — | — | — |  |
| T18 | 3 | NO_EJECUTADA | — | — | — |  |
| T19 | 7 | NO_EJECUTADA | — | — | — |  |
| T20 | 6 | NO_EJECUTADA | — | — | — | Perfil extendido (Fase 6). |
| T21 | 3 | NO_EJECUTADA | — | — | — |  |
| T22 | 4 | NO_EJECUTADA | — | — | — |  |
| T23 | 0 | PASA | `UnitTests/Contracts/ContractExamplesTests`, `ContractReferenceDifferentialTests`, `IdentityPrimitivesTests` | proceso | `build/ci.ps1` (dotnet test --filter "Category!=Load") · 2026-10-14 19:08 | 19 válidos pasan schema y reglas; 24 inválidos fallan solo por la palabra clave y la ruta de EXPECTED.json; 5 de negocio fallan exactamente su regla; prueba diferencial contra jsonschema y business_errors de Python. |
| T24 | 2 | PASA | `FaultTests/Sequence/CounterResetTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 23:50 | Época 42 con 1-5000 registrados por TX-A, cada uno con su job; 5200 entregas por la ingesta: 5000 `IDENTITY_COLLISION` BLOCKING, cada una con su registro completo, con la detección y el job existentes intactos; estación bloqueada por la primera colisión (`BlockedByAnomalyId`); 5001-5200 `BLOCKED` con `STATION_DISABLED`; eventos nuevos: solo 200 `CartonDetected` y 200 `PrintJobResult` (ninguno para las colisiones); ningún job nuevo; `LabelingReady` baja; `plc_sequence_anomaly_total{type=IDENTITY_COLLISION}` = 5000. |
| T25 | 2 | PASA | `FaultTests/Sequence/EpochChangeTests`, `SequenceReviewTests`, `UnitTests/Orchestration/FingerprintVectorTests`, `SequenceClassifierTests`, `IncoherentSequenceTests` | SQL Server real y proceso (Python de referencia) | `build/ci.ps1` · 2026-10-14 23:50 | AUTO: época 43 `AUTO_INCREMENT_BY_ONE` por SYSTEM y cajas normales. REQUIRE: 3 registros retenidos (`EPOCH_UNEXPECTED`), estación bloqueada, sin detecciones ni eventos; tras `CONFIRM_EPOCH` y desbloqueo, reingreso `ANOMALY_REINGEST` (`RECOVERED`, `ReceivedAt` original): el vencido queda `EXPIRED`, los vigentes crean job; `PlcEpochHistory` registra `OPERATOR_CONFIRMED`, la persona y su OperatorAction; una aceptación con una acción que no es CONFIRM_EPOCH falla con 51032. Salto a 45: `EPOCH_UNEXPECTED` y bloqueo. Huella idéntica byte a byte a `source_fingerprint` de `verify_package.py` en 6 vectores (+, &, <, >, ', no ASCII, extremos de época y ciclo). |
| T26 | 2 | PASA | `FaultTests/Sequence/SensorBounceTests`, `UnitTests/Orchestration/SequenceClassifierTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 23:50 | Con `MinCycleIntervalMs` = 200, dos ciclos a 150 ms: el segundo `BLOCKED` con `IMPLAUSIBLE_RATE`, `PrintJobResult` BLOCKED, sin job; `detections_blocked_total{reason=IMPLAUSIBLE_RATE}` = 1. Sin `DetectionTimestamp` la regla queda desactivada (decisión 5 de la Fase 2). |
| T27 | 5 | NO_EJECUTADA | — | — | — |  |
| T28 | 0 | PASA | `UnitTests/Package/SpecPackageTests` | proceso | `py spec/tools/verify_package.py` · 2026-10-14 | 15 grupos OK, 0 errores, pendientes de integración listados; además es un paso propio del workflow de CI. |
| T29 | 4 | NO_EJECUTADA | — | — | — |  |
| T30 | 2 | PASA | `FaultTests/Sequence/LateEventTests`, `UnitTests/Orchestration/SequenceClassifierTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 23:50 | Marca alta 2000 (ya reclamado y transmitido); el 1998 llega por `REPUBLISH` con timestamp anterior. Vigente: job creado que vence con `OUT_OF_ORDER` en el claim, sin intento. Vencido: `EXPIRED` con `LATE_ARRIVAL`. Sin anomalía; estación habilitada; `out_of_order_detections_total` = 1; hueco 1998 `RECOVERED`. |
| T31 | 4 | NO_EJECUTADA | — | — | — |  |
| T32 | 7 | NO_EJECUTADA | — | — | — |  |
| T33 | 3 | NO_EJECUTADA | — | — | — |  |
| T34 | 5 | NO_EJECUTADA | — | — | — |  |
| T35 | 2 | PASA | `FaultTests/Sequence/SequenceGapTests`, `UnitTests/Orchestration/SequenceClassifierTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 23:50 | Marca alta 300, llega 310: hueco 301-309 `OPEN`; al vencer la gracia, `CLOSED_UNRECOVERED` con `UnrecoveredCount` 9; `cycles_unobserved_total` = 9; ninguna detección ni evento para 301-309. |
| T36 | 5 | NO_EJECUTADA | — | — | — |  |
| T37 | 3 | NO_EJECUTADA | — | — | — |  |
| T38 | 4 | NO_EJECUTADA | — | — | — |  |
| T39 | 4 | NO_EJECUTADA | — | — | — |  |
| T40 | 5 | NO_EJECUTADA | — | — | — |  |
| T41 | 5 | NO_EJECUTADA | — | — | — |  |
| T42 | 4 | NO_EJECUTADA | — | — | — |  |
| T43 | 5 | NO_EJECUTADA | — | — | — |  |
| T44 | 4 | NO_EJECUTADA | — | — | — |  |
| T45 | 2 | PASA | `FaultTests/Sequence/GatewayReceiptTests`, `FaultTests/Opc/OpcClientTransportTests`, `UnitTests/Orchestration/SequenceClassifierTests` | SQL Server real y simulador OPC UA | `build/ci.ps1` · 2026-10-14 23:50 | `GATEWAY_RECEIPT` sin `DetectionTimestamp`: 701 `READ`, 702 `REPUBLISH`, 703 en vivo con Overflow, el reingreso `ANOMALY_REINGEST` y la primera notificación tras recrear la suscripción (730, `READ`, también de punta a punta por OPC): `EXPIRED` con `LATE_ARRIVAL` y `DeadlineAt = ReceivedAt`; el ciclo en vivo que abre un hueco también; crear el job de un registro recuperado o con Overflow lo rechaza `CK_CartonDetection_GatewayLive` (el ciclo en vivo que abre un hueco lo detiene la máquina de estados, SI-05); el ciclo menor sin timestamp: `AMBIGUOUS_DECREASE` y `BLOCKED` con `SEQUENCE_ANOMALY`. Métricas por transporte y tipo. |
| T46 | 3 | NO_EJECUTADA | — | — | — |  |
| T47 | 1 | PASA | `IntegrationTests/Schema/TriggerGuardTests`, `ReferenceStatementTests`, `ReferenceCoverageTests`, `MigrationTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 | DDL (spec 1.1.3) y semilla sin error; cada transacción de referencia con 0 y N filas sin excepción ni Msg 334, incluidas las ramas excepcionales (X03 en TX-C, TX-D, TX-S y TX-R0) y la recirculación (TX-A y D08 en TX-E); los 33 THROW (51001 a 51032 y 52001) fallan con su código exacto; transiciones prohibidas, respaldos ausentes o anteriores (también un intento excepcional que no es el más reciente y una corrección con una lectura anterior al claim o a la observación que confirmó, ADR 0016), cambio de solo Reason, PrinterSendOrder hacia atrás, PlcEpochHistory editada y desbloqueo sin OperatorAction rechazados; cada regla CHECK y UNIQUE de la sección 6 con una prueba que nombra la restricción. Fuera del criterio de T47 queda un vacío relacionado: una OperatorAction CONFIRM_EPOCH reutilizada no se rechaza (SI-08; el código siempre inserta una nueva). |
