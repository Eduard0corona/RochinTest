# Guardrails de diseño e implementación (v1.1)

Cada invariante se vincula con su mecanismo y con las pruebas que lo demuestran. La tabla se genera desde `contracts/invariants.json`.

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

## Prohibiciones (MUST NOT)

- Usar el timestamp de llegada, el flanco de un booleano o el número de secuencia de notificación de OPC como identidad de una caja.
- Leer tags independientes suponiendo una instantánea coherente: el ciclo se lee de un registro latched.
- Comparar timestamps de OPC (SourceTimestamp, ServerTimestamp) para decidir si un registro es duplicado; se compara la huella de origen.
- Tratar un `CycleId` menor como reinicio sin clasificarlo (duplicado, tardío, reinicio sospechado o ambiguo).
- Registrar un ciclo no observado como detección, o calcular un plazo sin timestamp o para un registro que no llegó en vivo con `GATEWAY_RECEIPT`.
- Crear un PrintJob con un snapshot ERP vencido, ausente o de otra orden.
- Llamar a la impresora desde el callback OPC o dentro de una transacción SQL.
- Reintentar automáticamente un intento con `MayHaveSent = 1`, o mover a `READY` un job en `SENDING`, `TRANSMITTED` o `UNKNOWN`.
- Marcar `NOT_SENT` si la escritura llegó a invocarse.
- Enviar a una impresora un ciclo anterior a otro que ya reclamó.
- Abrir una segunda conexión a una impresora para reimprimir: la reimpresión la ejecuta su sender.
- Ejecutar una transición manual, desbloquear una estación o confirmar una época sin `OperatorAction`, o fijar `SESSION_CONTEXT` sin limpiarlo en el `CATCH`.
- Reimprimir en una impresora que no sea la `ManualPrinterId`.
- Serializar la huella con un codificador JSON que no sea JCS (RFC 8785).
- Usar `OUTPUT` sin `INTO` o `@@ROWCOUNT` después de un DML sobre una tabla con triggers.
- Capturar como colisión de identidad una violación de unicidad de otra restricción.
- Confirmar con un ACK TCP, un estado genérico "lista" de la impresora o una escritura completa.
- Confirmar con una lectura cuyo serial no sea el `LabelSerial` del job.
- Enviar un job vencido como operación ordinaria.
- Ejecutar dos senders contra la misma impresora sin un mecanismo físico de exclusión.
- Presentar un fencing token en SQL como garantía física.
- Editar una `StationConfigRevision` o el contenido congelado de un PrintJob.
- Crear un job nuevo para reimprimir.
- Agregar componentes sin requisito ni ADR.

## Obligaciones (MUST)

- Validar `StatusCode` Good, certificados, reconexión y huecos de secuencia; registrar como `READ` la primera notificación de cada monitored item creado o transferido.
- Registrar los intervalos OPC revisados por el servidor; 10 y 20 ms son solicitudes.
- Persistir el intento `SENDING` antes de cualquier I/O que pueda imprimir.
- Hacer la segunda comprobación monotónica inmediatamente antes de escribir y vigilar la divergencia entre reloj de pared y monotónico.
- Registrar toda evidencia en `EvidenceObservation` antes de aplicarla.
- Obtener el serial antes de TX-A y calcular `PayloadHash` sobre el payload con el serial.
- Pasar a `UNKNOWN` todo `SENDING` encontrado al arrancar, antes de suscribirse a OPC UA.
- Publicar un resultado por cada transición publicable con `resultVersion` creciente.
- Exigir dos personas, caja retenida y riesgo declarado para cada reimpresión excepcional.
- Medir p50, p95, p99 y máximo de cada tramo de la sección 10.4 del documento principal.

## Supuestos pendientes con OT

Ver `docs/DECISIONES_ABIERTAS_OT.md` (OT-01 a OT-16). Ninguno se asume resuelto: cada capacidad física dudosa queda detrás de una interfaz y de su simulador.

## Mínimos aceptables del MVP con simuladores

- 100 entregas del mismo registro: 1 detección, 1 job, como máximo 1 transmisión (T01).
- Reinicio en cualquier punto del envío: `UNKNOWN` y cero reenvíos (T07).
- Agente suspendido después del claim: cero envíos tardíos (T29).
- ERP de vuelta después del plazo: cero bytes a la impresora (T17).
- Contador reiniciado sin época: estación bloqueada y ninguna detección reutilizada (T24).
- Broker caído: impresión sin cambio y republicación con el mismo `EventId` (T08).
- Job atrasado respecto de la impresora: vence con `OUT_OF_ORDER`, nunca se envía después (T42).
- DDL y sentencias de referencia ejecutados en SQL Server sin errores con 0 filas ni Msg 334 (T47).
