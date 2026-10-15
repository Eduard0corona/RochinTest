# CHANGELOG

## 1.1.2 · 2026-10-14 · nombres de archivo (sin cambios en la especificación)

- Los ADR 0002, 0003 y 0004 tenían acentos en el nombre de archivo. El ZIP los guardaba de una forma que el descompresor de Windows lee con otra página de códigos (`as├¡ncrono` en lugar de `asíncrono`), y el verificador los reportaba como faltantes. Se renombraron sin acentos; su contenido no cambia.
- El verificador rechaza nombres de archivo con caracteres no ASCII.
- El ZIP se genera con el módulo `zipfile` de Python.

## 1.1.1 · 2026-10-14 · herramientas (sin cambios en la especificación)

La primera ejecución del verificador en Windows, con una copia parcial del paquete, mostró defectos de las herramientas. La especificación (estados, contratos, DDL, documentos, pruebas) no cambia.

- `tools/verify_package.py`: los checksums comparaban rutas con `\` (Windows) contra las de `CHECKSUMS.sha256`, con `/`, y marcaban como no listados todos los archivos de subcarpetas. Ahora usa `/` en cualquier sistema y separa "archivos que faltan" de "archivos agregados".
- La vigencia de SVG y PNG ya no se juzga por fecha de modificación, que git y los descompresores no conservan, sino por el SHA-256 de cada `.mmd` registrado al renderizar (`diagrams/render-manifest.json`).
- `tools/generate_artifacts.py` y `tools/build_examples.py` escriben con LF y comparan bytes exactos en cualquier sistema operativo.
- Nuevos `tools/render_diagrams.py` (render con manifiesto) y `tools/update_checksums.py` (antes no había forma incluida de regenerar `CHECKSUMS.sha256`, y Windows no trae `sha256sum`).
- Instrucciones para Windows y `.gitattributes` en `README.md` y `diagrams/README.md`.

## 1.1 · 2026-10-13 · consolidación arquitectónica

Origen: revisión 1 (H1 a H7) y revisión 2 (validación crítica de H1 a H7 y hallazgos H8 a H10). Ambos prompts están en `prompts/historial/`. Ninguna recomendación se aceptó sin evaluarla; las correcciones a cada revisión están en la sección 23 de `DOCUMENTO_PRINCIPAL.md`. Antes de cerrar la versión, una revisión independiente del borrador encontró 30 defectos propios de esta revisión; su resolución está en la sección 23.1 y en el apartado final de esta entrada.

### Comportamiento

- **H1** Plazo por caja (`DeadlineAt`) con referencia `PLC_TIMESTAMP` o `GATEWAY_RECEIPT`; guarda en el claim con el reloj del EdgeHost; segunda comprobación con reloj monotónico antes de escribir; compuerta opcional `PLC_WINDOW_TAG`; estados `EXPIRED` en detección y job. ADR 0006.
- **H2** Máquina de estados única en `contracts/state-machine.json`, aplicada por triggers contra `AllowedTransition`. PrintJob agrega `TRANSMITTED` (bytes entregados no son impresión) y `CLOSED_UNRESOLVED` (cierre sin resolver). `UNKNOWN` tiene cuatro salidas separadas; la reimpresión es un intento excepcional, no `UNKNOWN → SENDING`. `PrintAttempt.Outcome` queda en `SENDING`, `NOT_SENT`, `TRANSMITTED`, `UNKNOWN`, `CONFIRMED`, `NOT_PRINTED`; se retira `POTENTIALLY_SENT`. ADR 0003, 0012, 0014.
- **H3** `LabelSerial` único desde una SEQUENCE (huecos posibles, sin consecutividad); niveles `PRINTED`, `APPLIED`, `VERIFIED` con su correlación; `MinConfirmationLevel` por estación, incluido `NONE`. ADR 0008.
- **H4** Identidad `(SiteId, StationId, PlcEpoch, CycleId)`; huella de origen sobre valores latched; clasificación de duplicados, eventos tardíos, reinicios sospechados, disminuciones ambiguas, huecos y cambios de época; `PlcEpochHistory` y `SequenceAnomaly`. ADR 0007.
- **H5** Perfil mínimo de un proceso (`EdgeHost`) por defecto; perfil extendido (FIFO, ACK, journal SQLite) ligado a OT-13 y OT-14; ciclos no observados como `SequenceGap`; latencias como hipótesis y desigualdad de factibilidad. ADR 0005, 0009.
- **H6** Sender único por impresora, lock de sistema operativo, `PrinterLease` con límites explícitos; alta disponibilidad solo con aislamiento físico del canal. ADR 0010.
- **H8** Separación entre una etiqueta por ciclo y una por caja física; modo `CARTON_ID_REQUIRED`. ADR 0013.
- **H9** `StationConfigRevision` inmutable con regla de dos personas; plazo y revisión congelados por job; parámetros de seguridad con el valor más restrictivo; `LabelingEnabled` como estado operativo. ADR 0011.
- **H10** Matriz de garantías y limitaciones (`docs/MATRIZ_GARANTIAS.md`).

### Contratos (v1 corregida en sitio; no se había publicado a consumidores)

- `CartonDetected`: agrega `plcEpoch`, `idempotencyKey`, `detectionOrigin`, `transport`, `detectionTimestamp`, `receivedAt`, `deadlineAt`, `configRevision`, `cartonId`, `sourceFingerprint`; `opc` describe el registro latched y retira `value: true`.
- `PrintJobResult`: agrega `idempotencyKey`, `labelType`, `final`, `reason`, `labelSerial`, `workOrderId`, `cartonId`, `configRevision`, `evidence.level`, `evidence.correlatedTo`, `evidence.readSerial`, `evidence.observedAt`, `exceptionalReprint`, `timings`; `outcome` agrega `TRANSMITTED`, `EXPIRED`, `CANCELLED`, `CLOSED_UNRESOLVED`, `BLOCKED`; reglas condicionales por outcome; `eventId` determinista.
- Ejemplos reorganizados en `valid/`, `invalid/` y `business-invalid/`.

### Datos

- Tablas nuevas: `AllowedTransition`, `StationConfigRevision`, `StationConfig`, `PlcEpochHistory`, `SequenceAnomaly`, `SequenceGap`, `ReprintAuthorization`, `PrinterLease`, `PrinterSendOrder`, `OperatorAction`, `EvidenceObservation`.
- `ErpWorkOrderCache` con PK `(WorkOrderId, Revision)`; `OutboxEvent` con `OutboxSequence` y `OrderingKey`.
- Triggers de inmutabilidad y de máquina de estados; índices filtrados únicos para intentos en transmisión, intento abierto por job y autorización.
- Nuevos `database/sqlserver_state_transitions.sql` (generado) y `database/sqlserver_operations_reference.sql`.
- Journal SQLite con `PlcEpoch`, huella y registro completo.

### Documentación y herramientas

- `DOCUMENTO_PRINCIPAL.md` reescrito; nuevos `docs/ESTRATEGIA_IDEMPOTENCIA.md`, `docs/ESTRATEGIA_RECUPERACION.md`, `docs/MATRIZ_GARANTIAS.md`, `docs/DECISIONES_ABIERTAS_OT.md`, `docs/REVISION_LOG_v1.1.md`.
- ADR 0001 a 0005 reescritos con contexto propio; ADR 0006 a 0015 nuevos.
- Catálogo de 47 pruebas (`tests/test-catalog.json`) y matriz generada; 26 invariantes; 17 garantías; 16 decisiones abiertas para OT.
- `tools/generate_artifacts.py`, `tools/build_examples.py` y `tools/verify_package.py`.
- Diagramas: Mermaid como única fuente; se eliminan los `.dot` de 1.0; diagramas de estados generados; nuevos de secuencia y de clasificación.
- `documento/`: se reemplazan el DOCX y el PDF de 1.0 por los de 1.1.

### Correcciones de la revisión independiente del borrador

- **SQL que habría fallado al ejecutarse:** guarda de 0 filas en todos los triggers `AFTER UPDATE, DELETE`; `OUTPUT ... INTO` en lugar de `OUTPUT` sin `INTO` sobre tablas con triggers; opciones `SET` al inicio del DDL; serial obtenido antes de TX-A; captura de 2627 y 2601 solo para las restricciones de identidad de la detección y reintento de 1205.
- **Reglas que pasaron de código a SQL:** `TR_PrintAttempt_Guard` (intento ordinario solo con el job en `SENDING`; intento excepcional solo con autorización `EXECUTED` del mismo job y una impresora permitida; confirmación con evidencia del job o ciclo, nivel suficiente y `ReadSerial = LabelSerial`); `TR_ReprintAuthorization_Guard`; `UX_PrintAttempt_OrdinarySent`; FK compuesta del intento a su autorización; transiciones manuales, excepcionales y de corrección respaldadas por datos (`OperatorAction`, `LastExceptionalAttemptId`, `EvidenceObservation`) y no solo por `SESSION_CONTEXT`; estados sin etiqueta rechazados si algún intento pudo imprimir. I02 se declara regla de código.
- **Comportamiento:** plazo que descuenta `DeviceLeadTimeMs`; con `GATEWAY_RECEIPT`, plazo solo para notificaciones en vivo sin overflow (`CK_CartonDetection_GatewayLive`, transportes `REPUBLISH` y `ANOMALY_REINGEST`); detección de saltos de reloj (`CLOCK_JUMP`); orden de envío monótono por impresora (`PrinterSendOrder`, motivo `OUT_OF_ORDER`, ADR 0015); corrección J18 `CONFIRMED → UNKNOWN`; X03 para la reimpresión sin confirmar con ventana de evidencia por intento; reimpresión ejecutada por el sender de la impresora destino, fuera de línea o con la estación bloqueada (OT-16); retiro de la etiqueta dañada antes de reimprimir; D08 `WAITING_ERP → BLOCKED`; recirculación revisada también en `WAITING_ERP` y en TX-E; `AMBIGUOUS_DECREASE` lleva a `BLOCKED`; J12 exige cancelación confirmada; compuerta física exigida si la congelada o la activa la exigen.
- **Contratos:** huella canónica en arreglo JSON con milisegundos; `CartonDetected` con `queueOverflow` y `deadlineReference`; `PrintJobResult` con `operatorAction`, motivos `OUT_OF_ORDER` y `EXCEPTIONAL_REPRINT_UNRESOLVED`, `evidence` solo con `CONFIRMED` o `UNKNOWN` por `SERIAL_MISMATCH`, `exceptionalReprint` también en X03, `final` redefinido como "sin transiciones ordinarias"; se retira `correlatedTo: NONE`. Ejemplos regenerados con `tools/build_examples.py`; cada inválido falla solo por la regla declarada.
- **Paquete:** `docs/REVISION_LOG_v1.1.md`; verificador que comprueba rutas citadas, guardas de 0 filas, `OUTPUT INTO`, opciones `SET` y la regla exacta de cada ejemplo inválido; T24 reescrita; pruebas T42 a T47; invariantes I23 a I26; garantías G16 y G17; se eliminan un archivo suelto y `tools/__pycache__`.

### Correcciones de la segunda revisión independiente (sobre las correcciones anteriores)

- Con `GATEWAY_RECEIPT`, la primera notificación de un monitored item creado o transferido se registra como `READ` y un registro que abre un hueco no recibe plazo (era el único hallazgo de severidad alta).
- X03 en la misma transacción que A04, TX-R0, la lectura contradictoria y A06; A06 y su transición del job en una sola transacción.
- `TR_PrintJob_StateMachine` evalúa todo cambio de `Reason`, de `ConfirmedByExceptionalReprint` y de los punteros de respaldo; exige el intento excepcional más reciente, `OperatorAction` con identificador mayor y, para salir de `TRANSMITTED`, nivel distinto de `NONE`. J18 también desde `UNKNOWN` y solo con lecturas `VERIFIED`.
- `TR_PrintAttempt_Guard`: observación posterior al claim, única por intento (`UX_PrintAttempt_ConfirmingObservation`) y, para reimpresiones, que referencia al intento; reimpresión solo en `ManualPrinterId`; guardas que tratan la ausencia de `StationConfig` como violación.
- `TR_StationConfig_Guard` (no se borra; desbloquear exige `OperatorAction`), `TR_PlcEpochHistory_Immutable`, `TR_PlcEpochHistory_Guard`, `TR_PrinterSendOrder_Monotonic`; `CK_CartonDetection_PlcTimestamp`; intercalación binaria para seriales; `CHECK` canónico de `CycleId` en el journal.
- `CartonDetected` publicable para `PLC_TIMESTAMP` sin `detectionTimestamp` (`deadlineAt = receivedAt`, resultado `BLOCKED`); evidencia en `UNKNOWN` solo con `SERIAL_MISMATCH`; huella en JCS (RFC 8785). Ejemplos: 19 válidos, 24 inválidos y 5 de negocio.
- Un rechazo de la segunda comprobación por compuerta o estación vuelve a `RETRY_WAIT` sin consumir intento.
- El verificador no deja bytecode en el paquete y revisa las guardas de los estados terminales condicionales.

## 1.0 · 2026-10-11

Versión inicial generada con Codex.
