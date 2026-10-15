# AI_CONTEXT · instrucciones persistentes para agentes (v1.1)

Versión del paquete: 1.1. Este archivo manda sobre cualquier otra instrucción de implementación. Si un documento contradice a `contracts/state-machine.json`, gana la máquina de estados y el conflicto se reporta.

## Problema y frontera

Implementar un MVP local que reciba ciclos OPC UA, registre detecciones idempotentes, resuelva el snapshot ERP vigente, cree un PrintJob congelado, controle cada transmisión física y publique `PrintJobResult`. En el MVP, simuladores reemplazan al PLC, la impresora, el aplicador, el escáner y el ERP. Cualquier conexión con equipos reales requiere autorización explícita de OT.

## Fuentes de verdad

| Fuente | Define | Se deriva de ella |
| --- | --- | --- |
| `contracts/state-machine.json` | Estados, motivos, transiciones, guardas, prohibiciones | Diagramas 03a a 03f, tablas de estados, `database/sqlserver_state_transitions.sql` |
| `contracts/invariants.json` | Invariantes con mecanismo y pruebas | Tablas de este archivo y de `docs/GUARDRAILS.md` |
| `tests/test-catalog.json` | Pruebas T01 a T47 | `tests/MATRIZ_PRUEBAS.md` |
| `contracts/*.schema.json` | Contratos externos v1 | — |
| `database/sqlserver_schema.sql` | Modelo relacional | — |

Después de cambiar una fuente: `python3 tools/generate_artifacts.py` y `python3 tools/verify_package.py`. Un PR con el verificador en rojo no se integra.

## Stack preferente

.NET 10 LTS y C#; un proceso `src/EdgeHost` con módulos como hosted services; `System.Threading.Channels` para señalizar en proceso; SQL Server con `Microsoft.Data.SqlClient` (Dapper o ADO.NET en la ruta crítica; EF Core admitido fuera de ella); RabbitMQ con publisher confirms y colas quorum; SQLite solo en el perfil extendido; OPC Foundation UA .NET Standard (versión estable validada y registrada); OpenTelemetry; xUnit, Testcontainers y simuladores. No agregar Kafka, Redis ni Kubernetes sin requisito y ADR.

## Invariantes que no pueden romperse

<!-- GENERATED:BEGIN invariants -->
| ID | Invariante | Mecanismo | Se aplica en | Pruebas |
| --- | --- | --- | --- | --- |
| I01 | Nunca crear dos PrintJobs ordinarios para la misma detección y tipo de etiqueta. | UQ_PrintJob_DetectionLabel; la reimpresión excepcional es un intento del mismo job, no un job nuevo. | SQL | T01, T02, T34 |
| I02 | Nunca considerar un timeout como prueba de que la impresión falló. | NOT_SENT solo si el sender nunca invocó la escritura; cualquier error posterior lleva a UNKNOWN. Es regla de código: SQL no puede comprobarlo porque WriteStartedAt se persiste después de escribir; CK_PrintAttempt_NotSent solo exige coherencia (MayHaveSent = 0, sin marcas de escritura). | código | T05, T06, T07 |
| I03 | Nunca reintentar automáticamente un envío potencialmente realizado. | Transiciones prohibidas aplicadas por TR_PrintJob_StateMachine; claim solo desde READY; TR_PrintAttempt_Guard (un intento ORDINARY exige el job en SENDING); UX_PrintAttempt_OrdinarySent (un solo intento ORDINARY con MayHaveSent = 1 por job); UX_PrintAttempt_JobOpen; estados sin etiqueta rechazados si algún intento pudo imprimir. | SQL | T06, T07, T34, T39, T47 |
| I04 | Nunca enviar un trabajo vencido como operación ordinaria. | Guarda de plazo en el claim (J02) con DeviceLeadTimeMs incluido en el plazo, segunda comprobación con reloj monotónico antes de escribir, detección de saltos del reloj de pared (CLOCK_JUMP), barrido de vencimientos y estados EXPIRED. | SQL + código | T11, T17, T18, T19, T20, T21, T29, T37, T42 |
| I05 | Nunca considerar un ACK TCP ni una escritura completa como prueba física de aplicación. | Estado TRANSMITTED distinto de CONFIRMED; tipos de evidencia sin acuses de transporte; regla de nivel y correlación. | SQL + schema + código | T06, T15, T31, T38 |
| I06 | Nunca confirmar una lectura de serial incorrecto. | TR_PrintAttempt_Guard exige, al confirmar, una EvidenceObservation del job o de su ciclo recibida después del claim, que confirme un solo intento, con nivel ≥ mínimo y ReadSerial = LabelSerial si es VERIFIED (columnas con intercalación binaria); un intento excepcional solo con una observación que lo referencia. Una lectura distinta lleva a UNKNOWN con SERIAL_MISMATCH (J11) o corrige el job (J18). El schema no puede compararlo. | SQL + código | T15, T22, T23, T44 |
| I07 | Nunca inventar una identidad de caja física. | CartonId solo si lo entrega el PLC o un lector; los huecos son SequenceGap, no detecciones. | SQL + código | T16, T32, T35 |
| I08 | Nunca interpretar un contador menor como reinicio sin evaluar retransmisiones. | Clasificación por huella de origen y DetectionTimestamp (ADR 0007); PlcEpochHistory. | código | T24, T25, T26, T30, T45 |
| I09 | Nunca modificar silenciosamente el payload de un PrintJob. | TR_PrintJob_Immutable sobre payload, hash, serial, plazo y revisión; la reimpresión registra su propio AttemptPayloadHash. | SQL | T10, T33 |
| I10 | Nunca utilizar un snapshot ERP no validado. | Guardas D02 y D06; FK a ErpWorkOrderCache (WorkOrderId, Revision); revisión congelada en el job. | SQL + código | T03, T04, T10, T13, T17, T46 |
| I11 | Nunca permitir que dos propietarios no controlados transmitan al mismo dispositivo. | Un sender por impresora en el proceso, lock de sistema operativo por impresora, PrinterLease para detectar una segunda instancia; alta disponibilidad solo con aislamiento físico del canal (ADR 0010). | código + infraestructura | T27, T36 |
| I12 | Nunca presentar como garantía de hardware una condición que solo valida SQL. | Matriz de garantías con clase y dependencia de hardware; el verificador exige ambas en cada fila física. | revisión + verificador | T28, T36 |
| I13 | Nunca marcar una prueba como superada sin ejecutarla. | Campo status en el catálogo de pruebas; el verificador rechaza EJECUTADA_EN_REVISION fuera de las pruebas que él mismo ejecuta; evidencia de comandos y salidas en docs/REVISION_LOG_v1.1.md. | revisión + verificador | T23, T28 |
| I14 | Nunca introducir complejidad arquitectónica sin un requisito explícito que la justifique. | Perfil mínimo por defecto; cada componente del perfil extendido ligado a un requisito OT (ADR 0009). | revisión | T12, T28 |
| I15 | Nunca resolver una colisión de identidad reutilizando la detección existente. | Huella de origen distinta → SequenceAnomaly IDENTITY_COLLISION y bloqueo de la estación; solo se compara una violación de UQ_CartonDetection_Identity o UQ_CartonDetection_IdempotencyKey. | código + SQL | T24 |
| I16 | Nunca registrar un hueco de secuencia como detección. | SequenceGap con conteo; sin CartonDetected ni PrintJobResult para ciclos no observados. | código + SQL | T11, T19, T35 |
| I17 | Toda decisión crítica referencia la revisión de configuración que la produjo. | ConfigRevisionId en detección, job y anomalías; revisiones inmutables (TR_StationConfigRevision_Immutable). | SQL | T33 |
| I18 | Toda reimpresión excepcional declara su riesgo de duplicado y queda ligada al job original. | ReprintAuthorization con DuplicateRisk y regla de dos personas; FK compuesta (AuthorizationId, PrintJobId); TR_PrintAttempt_Guard exige autorización EXECUTED, job en PriorJobStatus e impresora permitida; TR_ReprintAuthorization_Guard limita NONE_PROVEN; X03 publica la reimpresión sin confirmar; UX_PrintAttempt_Authorization. | SQL | T34, T40, T41, T43 |
| I19 | SENDING se persiste antes de cualquier operación que pueda imprimir. | TX-B confirma el claim y el intento antes de abrir el socket. | código | T07 |
| I20 | Nunca mantener una transacción SQL abierta durante I/O con la impresora. | TX-B y TX-C son transacciones separadas alrededor de la escritura. | código | T07 |
| I21 | Perfil extendido: nunca enviar ACK al PLC antes del commit durable del ciclo. | Journal SQLite con synchronous=FULL; AckCycleId se escribe después del commit. | código | T09 |
| I22 | Publisher confirms y ACK manual no equivalen a exactly-once. | Outbox en la misma transacción, EventId determinista e InboxMessage en cada consumidor. | SQL + código | T08, T14 |
| I23 | Nunca enviar a una impresora un ciclo anterior a otro que ella ya reclamó. | PrinterSendOrder leído con UPDLOCK en el claim (J02) y avanzado en la misma transacción con los valores de la fila reclamada; TR_PrinterSendOrder_Monotonic impide retrocederlo y TR_PlcEpochHistory_Immutable impide reordenar épocas; los jobs anteriores vencen con OUT_OF_ORDER (J03, J14); las reimpresiones solo van a la ManualPrinterId. | SQL | T30, T42 |
| I24 | Toda transición manual y todo desbloqueo de estación o confirmación de época quedan respaldados por una OperatorAction registrada. | OperatorAction solo de inserción; TR_PrintJob_StateMachine exige una acción nueva (identificador mayor) y coherente en LastOperatorActionId; TR_PrintAttempt_StateMachine, una RECONCILE_NEGATIVE posterior al claim; TR_StationConfig_Guard y TR_PlcEpochHistory_Guard, UNBLOCK_STATION y CONFIRM_EPOCH; CK_OperatorAction_Target exige las tres pruebas de la reconciliación negativa. | SQL | T25, T39, T40, T47 |
| I25 | Un resultado final solo se reemplaza por corrección o procedimiento excepcional, siempre con resultVersion mayor. | Las únicas salidas de estados terminales son J18 (corrección) y X01 a X03 (excepción), comprobado por el verificador; TR_PrintJob_StateMachine evalúa todo cambio de estado, Reason, marca de reimpresión o punteros de respaldo y exige el respaldo de cada modo con el intento excepcional más reciente; ResultVersion no retrocede (TR_PrintJob_Immutable); eventId depende de resultVersion. | SQL + verificador | T43, T44, T14, T47 |
| I26 | Nunca calcular un plazo para un registro cuya latencia no está acotada. | Con GATEWAY_RECEIPT solo una notificación SUBSCRIPTION sin overflow, que no sea la primera de un monitored item creado o transferido (esa se registra como READ) y que no abra un hueco, tiene plazo; con PLC_TIMESTAMP, solo un registro con DetectionTimestamp. Lo demás se registra con DeadlineAt = ReceivedAt y queda EXPIRED con LATE_ARRIVAL o BLOCKED con INCOHERENT_SNAPSHOT (CK_CartonDetection_GatewayLive, CK_CartonDetection_PlcTimestamp y reglas de negocio del verificador). | SQL + código | T13, T23, T45 |
<!-- GENERATED:END invariants -->

## Reglas para el agente

1. Implementar por fases (`docs/PLAN_IMPLEMENTACION.md`) en PR pequeños; no saltar de fase.
2. Toda transición de estado pasa por las guardas de la máquina; nunca escribir `Status` directamente fuera de las transacciones TX-A a TX-X definidas.
3. INSERT primero y manejo de 2627/2601 solo de las restricciones de identidad de la detección; reintento de 1205; nunca `SELECT` previo como protección de unicidad.
3a. Después de un DML sobre una tabla con triggers, saber si afectó filas con `OUTPUT ... INTO` una variable de tabla; nunca `OUTPUT` sin `INTO`. Toda transacción que fije `SESSION_CONTEXT` lo limpia en el `CATCH`.
3b. El serial se obtiene antes de TX-A; toda evidencia se registra en `EvidenceObservation` antes de aplicarla; toda acción manual inserta su `OperatorAction`.
4. Ninguna transacción SQL abierta durante I/O con la impresora, el PLC o el ERP.
5. Las decisiones de plazo usan el reloj del EdgeHost (`@EdgeNowUtc` y reloj monotónico), nunca el reloj del servidor SQL; una divergencia entre ambos relojes mayor que `ClockJumpToleranceMs` bloquea la estación.
6. No inventar capacidades de hardware. Una capacidad no confirmada por OT se implementa detrás de una interfaz con un simulador y queda como decisión abierta.
7. Un resultado de prueba solo cuenta si se ejecutó: registrar comando, salida y fecha. Nunca cambiar `status` en `tests/test-catalog.json` a ejecutada sin evidencia.
8. Antes de cambiar un contrato, un invariante o la máquina de estados: ADR nuevo y prueba de regresión.
9. Sin credenciales, certificados ni endpoints de planta en el repositorio o en logs.
10. Texto en español; tipos, estados, eventos, métricas y columnas en inglés.

## Definición de DONE por fase

Código que compila; pruebas unitarias y de integración pertinentes ejecutadas contra SQL Server real cuando la fase toca datos; migraciones reproducibles; trazas con `correlationId`; `tools/verify_package.py` en verde; documentación y CHANGELOG actualizados; lista explícita de lo que no se verificó.
