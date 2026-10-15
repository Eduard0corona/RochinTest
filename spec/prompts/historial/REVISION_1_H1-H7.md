# Prompt para Codex: revisión v1.1 del paquete de arquitectura

Eres un Senior .NET/OT engineer. Vas a corregir el paquete `CartonPrinting_AI_Implementation` (v1.0) a partir de una revisión técnica externa. Es una revisión de diseño: **no implementes código de producción ni empieces la Fase 0**. Solo cambias documentación, DDL, contratos, diagramas, ADR, matriz de pruebas y un script de verificación.

## Antes de empezar

1. Lee en este orden: `README.md`, `AI_CONTEXT.md`, `docs/GUARDRAILS.md`, `DOCUMENTO_PRINCIPAL.md`, `docs/ARQUITECTURA_TECNICA.md`, `database/`, `contracts/`, `diagrams/*.mmd`, `tests/MATRIZ_PRUEBAS.md`, `docs/adr/`.
2. Devuelve primero un plan breve: qué archivos tocas por cada hallazgo (H1 a H7). Después ejecútalo sin hacer preguntas que no bloqueen.
3. Conserva todos los invariantes actuales, salvo los que este prompt cambia de forma explícita. Si encuentras un conflicto que este prompt no resuelve, no improvises: regístralo en la sección "Decisiones abiertas" del documento.

## Reglas

- Texto en español. Nombres de tipos, estados, eventos, columnas y métricas en inglés.
- No inventes datos de hardware. Todo tiempo es configurable por estación y se marca como objetivo de diseño.
- Los contratos v1 no se han publicado a consumidores. Corrige v1 en sitio y documenta el cambio en `CHANGELOG.md`; no crees v2.
- No marques nada como probado si no ejecutaste el comando. Pega comandos y salidas reales en `docs/REVISION_LOG_v1.1.md`.
- Una sola fuente de verdad para la máquina de estados (H2). Ningún otro archivo define estados por su cuenta.

---

## H1. No existe vencimiento por caja (crítico)

**Problema.** Ningún estado, columna ni claim impide imprimir tarde. Un job que sale de `WAITING_ERP` cuando vuelve el ERP, de `RETRY_WAIT` después del backoff, o del replay tras una caída de SQL, se imprime con la caja ya fuera de la estación. La etiqueta cae en otra caja, que es justo el caso de dos etiquetas que el diseño debe evitar.

**Cambios requeridos:**

1. Nueva tabla de configuración por estación:

   ```sql
   CREATE TABLE dbo.StationConfig (
     SiteId                NVARCHAR(64) NOT NULL,
     StationId             NVARCHAR(64) NOT NULL,
     PrinterId             NVARCHAR(64) NOT NULL,
     TransitBudgetMs       INT          NOT NULL,  -- sensor a punto de aplicación, menos el tiempo de impresión; lo mide OT
     SendMarginMs          INT          NOT NULL,  -- tiempo mínimo que debe quedar para enviar
     MinCycleIntervalMs    INT          NOT NULL,  -- tiempo físico mínimo entre dos cajas (H4)
     MaxAutoAttempts       TINYINT      NOT NULL,
     MinConfirmationLevel  VARCHAR(10)  NOT NULL,  -- PRINTED | APPLIED | VERIFIED (H3)
     CurrentPlcEpoch       INT          NOT NULL,  -- H4
     LabelingEnabled       BIT          NOT NULL,  -- H4: estación bloqueada por anomalía
     CONSTRAINT PK_StationConfig PRIMARY KEY (SiteId, StationId)
   );
   ```

2. `CartonDetection.DeadlineAt DATETIME2(3) NOT NULL` = `DetectionTimestamp` del PLC + `TransitBudgetMs`. `PrintJob.DeadlineAt` es una copia inmutable.
3. El claim incluye el plazo. Esta condición es la protección real; no depende de ningún barrido:

   ```sql
   UPDATE dbo.PrintJob
   SET Status = 'SENDING', UpdatedAt = SYSUTCDATETIME()
   OUTPUT inserted.PrintJobId
   WHERE PrintJobId = @id
     AND Status = 'READY'
     AND DeadlineAt > DATEADD(millisecond, @sendMarginMs, SYSUTCDATETIME());
   -- además valida el fencing token del dueño de la impresora (H6)
   ```

4. `RETRY_WAIT → READY` solo si el plazo sigue vigente con el margen; si no, `EXPIRED`.
5. `WAITING_ERP`: si el snapshot llega antes del plazo se crea el job; si llega después, la detección queda `EXPIRED` con `Reason = 'ERP_UNAVAILABLE'` y nunca se crea job.
6. Replays (journal, cola del PLC, reconexión OPC): se registran siempre, pero lo vencido queda `EXPIRED` y jamás se imprime.
7. Un servicio de barrido cierra lo vencido (`READY`, `RETRY_WAIT`, `WAITING_ERP`) y publica su resultado. Documenta que el barrido sirve para cerrar y publicar, y que la condición del claim es la que impide imprimir tarde.
8. El plazo usa el timestamp de origen del PLC. Agrega la métrica `clock_skew_ms` y una alerta cuando supere `SendMarginMs`. Requisito: NTP o PTP validado.
9. Runbook: un job `EXPIRED` no se reimprime en línea; la caja sigue la política de rechazo o retención de OT.

## H2. Máquina de estados inconsistente entre artefactos

**Problema:**

- El diagrama 03 pone `READY → RETRY_WAIT`, pero el error ocurre después del claim.
- `FAILED` solo se alcanza desde `UNKNOWN`, mientras la tabla dice otra cosa.
- `CANCELLED` está en el DDL pero no en el diagrama.
- El runbook y el diagrama 04 permiten un "nuevo intento autorizado" desde `UNKNOWN` que ninguna transición contempla.
- `GUARDRAILS.md` exige `POTENTIALLY_SENT`, que el `CHECK` de `PrintAttempt` no admite.

**Cambios requeridos.** Crea `contracts/state-machine.json` como fuente única, con estados, bandera terminal y transiciones (`from`, `to`, `trigger`, `guard`, `mode: auto|manual`). Debe contener exactamente esto:

**PrintJob**

| Desde | Hacia | Disparador y condición | Modo |
| --- | --- | --- | --- |
| (creación) | `READY` | Snapshot ERP válido y plazo vigente. En la misma transacción se asigna `LabelSerial` (H3). | auto |
| `READY` | `SENDING` | Claim condicional con plazo y fencing token. Inserta `PrintAttempt(SENDING, MayHaveSent=1)`. | auto |
| `READY` | `EXPIRED` | Plazo vencido antes del claim. | auto |
| `READY` | `CANCELLED` | Anulación autorizada antes de enviar. | manual |
| `SENDING` | `CONFIRMED` | Evidencia válida según H3. | auto |
| `SENDING` | `RETRY_WAIT` | `NOT_SENT` comprobado (ningún byte escrito), intentos < `MaxAutoAttempts`, plazo vigente. | auto |
| `SENDING` | `FAILED` | `NOT_SENT` comprobado e intentos agotados. | auto |
| `SENDING` | `EXPIRED` | `NOT_SENT` comprobado y plazo vencido. | auto |
| `SENDING` | `UNKNOWN` | Timeout o corte después de escribir bytes, o reinicio que encuentra `SENDING`. | auto |
| `RETRY_WAIT` | `READY` | Backoff cumplido y plazo vigente con margen. | auto |
| `RETRY_WAIT` | `EXPIRED` | Plazo vencido. | auto |
| `UNKNOWN` | `CONFIRMED` | Reconciliación con evidencia válida según H3. | auto o manual |
| `UNKNOWN` | `FAILED` | Reconciliación que prueba que no se imprimió, o cierre auditado. | manual |
| `UNKNOWN`, `FAILED` | `SENDING` | Reimpresión manual autorizada, con la caja retenida. `PrintAttempt` con `IsManual=1`, `AuthorizedBy` y `Reason`. | manual |

- Terminales: `CONFIRMED`, `EXPIRED`, `CANCELLED`. `FAILED` es terminal salvo reimpresión manual. `UNKNOWN` no es terminal.
- `PrintAttempt.Outcome`: `SENDING`, `NOT_SENT`, `UNKNOWN`, `CONFIRMED`. Elimina `POTENTIALLY_SENT` de `GUARDRAILS.md`.
- `MayHaveSent` vale 1 desde el claim y pasa a 0 solo con `NOT_SENT` comprobado.

**CartonDetection**

- `RECEIVED → JOB_CREATED | WAITING_ERP | BLOCKED | EXPIRED`
- `WAITING_ERP → JOB_CREATED | EXPIRED`
- Columna `Reason VARCHAR(40) NULL` con valores `ERP_UNAVAILABLE`, `NOT_OBSERVED`, `SEQUENCE_ANOMALY`, `IMPLAUSIBLE_RATE`, `BAD_QUALITY` e `INCOHERENT_SNAPSHOT`.

**Correspondencia con `PrintJobResult.outcome`:**

- Estados del job: `CONFIRMED`, `UNKNOWN` (con `final: false`), `FAILED`, `EXPIRED` y `CANCELLED`.
- Detección sin job: `EXPIRED` o `BLOCKED`.
- Regla: toda detección termina con un resultado publicado.

Alinea con esta fuente los siguientes artefactos:

- los `CHECK` del DDL;
- los enums de los schemas;
- `diagrams/03_estados.mmd` y los diagramas 02, 04 y 05;
- la sección 7 de `DOCUMENTO_PRINCIPAL.md` y `docs/ARQUITECTURA_TECNICA.md`;
- el runbook y la matriz de pruebas.

## H3. Evidencia sin serial y schema permisivo

**Problema:**

- Se exige que el escáner confirme "el serial correcto", pero no existe un serial por etiqueta. Con GTIN y lote iguales, la caja N y la N+1 se leen igual.
- El schema acepta `CONFIRMED` con evidencia `NONE` y `verified: false`.

**Cambios requeridos:**

1. Columna `PrintJob.LabelSerial VARCHAR(32) NOT NULL` con restricción `UNIQUE`. Se toma de `CREATE SEQUENCE dbo.LabelSerialSeq AS BIGINT START WITH 1 INCREMENT BY 1 CACHE 100;` en la misma transacción que crea el job.
   - Nunca se deriva de `CycleId`, porque el contador puede reiniciarse.
   - El formato es configurable: SSCC-18 con dígito de control GS1 si la planta usa GS1, o un serial interno.
   - El payload lo codifica en el código de barras y `PayloadHash` lo cubre.
2. Columna `PrintAttempt.ReadSerial VARCHAR(32) NULL`, con `CHECK (Outcome <> 'CONFIRMED' OR EvidenceType <> 'BARCODE_VERIFICATION' OR ReadSerial IS NOT NULL)`. La igualdad `ReadSerial = LabelSerial` se valida en código y en la prueba T22; documenta que SQL no puede validarla entre tablas.
3. Niveles de evidencia, campo `evidence.level`:
   - `PRINTED`: confirmación específica del trabajo por parte de la impresora.
   - `APPLIED`: sensor del aplicador correlacionado con el ciclo.
   - `VERIFIED`: escáner que lee el `LabelSerial` del job.

   `StationConfig.MinConfirmationLevel` define qué nivel cuenta como `CONFIRMED`, por defecto `VERIFIED`. Documenta que `PRINTED` es más débil: prueba que salió una etiqueta, no que quedó en la caja correcta.
4. Cambios en `PrintJobResult` v1:
   - Campos nuevos: `idempotencyKey` (requerido), `final` (requerido), `labelSerial`, `reason` y `evidence.level` / `evidence.readSerial`.
   - `outcome` admite `CONFIRMED`, `UNKNOWN`, `FAILED`, `EXPIRED`, `CANCELLED` y `BLOCKED`.
   - `reason`: `ERP_UNAVAILABLE`, `NOT_OBSERVED`, `DEADLINE_PASSED`, `PRINTER_UNAVAILABLE`, `RECONCILED_NOT_PRINTED`, `CANCELLED_BY_OPERATOR`, `SEQUENCE_ANOMALY`, `IMPLAUSIBLE_RATE`, `BAD_QUALITY` e `INCOHERENT_SNAPSHOT`.
5. Reglas del schema con `if`/`then` (Draft 2020-12):
   - `CONFIRMED` exige `printJobId`, `attemptId`, `labelSerial`, `productId` y `lotId`. Exige además `evidence.verified: true`, `evidence.type` distinto de `NONE`, `evidence.level` y `evidence.reference`.
   - `UNKNOWN` exige `printJobId`, `attemptId`, `labelSerial` y `final: false`.
   - `FAILED`, `EXPIRED`, `CANCELLED` y `BLOCKED` exigen `reason`.
   - `evidence` deja de ser obligatorio fuera de `CONFIRMED`.
6. Ejemplos en `contracts/examples/valid/`:
   - `confirmed.json`
   - `unknown.json`
   - `expired-erp-unavailable.json` (sin job)
   - `blocked-sequence-anomaly.json`
   - `carton-detected.json`
7. Ejemplos en `contracts/examples/invalid/`, que deben fallar la validación:
   - `confirmed-without-evidence.json`
   - `confirmed-printer-ack-unverified.json`
   - `unknown-without-attempt.json`
   - `failed-without-reason.json`
   - `carton-detected-without-epoch.json`

## H4. Reinicio del contador del PLC no se detecta

**Problema.** Un duplicado se resuelve reusando la detección existente. Si `CycleId` vuelve a cero por un cambio de CPU, una carga de programa o un reset, cada caja nueva choca con una clave vieja y se queda sin etiqueta, sin ninguna alarma.

**Cambios requeridos:**

1. Tag nuevo en el contrato del PLC (sección 3): `PlcEpoch` (UInt32), que el PLC incrementa cada vez que reinicia el contador. Agrégalo al snapshot latched.
2. Clave de identidad:
   - `UNIQUE (SiteId, StationId, PlcEpoch, CycleId)`.
   - `idempotencyKey = "{siteId}:{stationId}:{plcEpoch}:{cycleId}"`.
   - `eventId = UUIDv5(namespace fijo, idempotencyKey)`; documenta el namespace en `contracts/README.md`.
3. `CartonDetected` v1 agrega `plcEpoch` (entero ≥ 0, requerido), `idempotencyKey` y `deadlineAt`, ambos requeridos.
   - `opc` describe el nodo del registro de ciclo, no el booleano: `nodeId`, `statusCode` y `sourceTimestamp`.
   - Elimina `opc.value: const true`.
4. Ante una violación de unicidad, lee la fila existente en una transacción nueva:
   - **`DetectionTimestamp` igual:** es un duplicado real y se reusa, como hoy.
   - **`DetectionTimestamp` distinto:** es una colisión.
     - No se reusa.
     - Se inserta en `dbo.SequenceAnomaly` (Id, SiteId, StationId, PlcEpoch, CycleId, ExistingDetectionTimestamp, IncomingDetectionTimestamp, DetectedAt) y se incrementa `plc_sequence_anomaly_total`.
     - Se pone `StationConfig.LabelingEnabled = 0`, baja la señal de lista al PLC y se alerta.
     - Se reanuda solo cuando OT confirma la nueva época; la confirmación queda auditada.
5. Un `CycleId` menor que el último de la misma época se trata igual, aunque todavía no choque.
6. Si el PLC no puede exponer `PlcEpoch`, la época la administra el gateway en `StationConfig.CurrentPlcEpoch`, con la misma detección y confirmación manual. Documéntalo como limitación.
7. Plausibilidad: un avance más rápido que `MinCycleIntervalMs` deja la detección `BLOCKED` con `Reason = 'IMPLAUSIBLE_RATE'`, sin imprimir y con alerta.

## H5. El flujo no es mínimo y el SLO no llega a la impresora

**Problema:**

- Hay tres capas durables en serie (cola del PLC, journal SQLite y SQL), cinco procesos más RabbitMQ, y se exige modificar el PLC.
- El objetivo p95 < 100 ms termina en `READY` y no incluye el envío a la impresora.

**Cambios requeridos:**

1. **Perfil mínimo**, que se vuelve el diseño por defecto:
   - El PLC expone `CycleId`, `PlcEpoch` y un snapshot latched (`WorkOrderId` y `DetectionTimestamp`).
   - Un solo proceso edge (`src/EdgeHost`) aloja como hosted services los módulos Ingest, Orchestrator, PrintAgent, OutboxRelay y ErpSync. Los proyectos siguen separados como bibliotecas.
   - SQL Server es la única escritura durable en la ruta crítica.
   - En una sola transacción se registra la detección, se crea el `PrintJob` (si hay snapshot válido) y se escribe el Outbox `CartonDetected`.
   - El PrintAgent recibe aviso en proceso (`System.Threading.Channels`) y hace el claim en SQL.
   - El servicio mantiene una señal de latido o "etiquetadora lista" en el PLC.
   - Si cae SQL, el sistema falla cerrado y el PLC retiene o desvía las cajas.
   - Al volver SQL, los ciclos del hueco del contador se registran como `EXPIRED` con `Reason = 'NOT_OBSERVED'` y `WorkOrderId` NULL, y se publica un resultado por cada uno. Para permitirlo, `CartonDetection.WorkOrderId` pasa a admitir NULL con `CHECK (WorkOrderId IS NOT NULL OR (Status = 'EXPIRED' AND Reason = 'NOT_OBSERVED'))`.
2. **Perfil extendido**, opcional y con su ADR: cola del PLC con `AckCycleId` y journal SQLite. Solo aplica si OT exige no perder el registro de ningún ciclo durante caídas de SQL y acepta modificar el PLC. Aun así, lo vencido queda `EXPIRED` (H1).
3. Ajusta el invariante 3 de `AI_CONTEXT.md` (ACK al PLC) para que aplique solo al perfil extendido.
4. **SLO.** Agrega `opc_notification_to_bytes_sent_ms` (notificación OPC recibida a bytes escritos en el socket de la impresora) y conserva `opc_notification_to_job_ready_ms`.
   - Objetivo de diseño para el perfil mínimo: p99 < 50 ms.
   - Regla: el p99 debe quedar por debajo de `TransitBudgetMs - SendMarginMs`.
   - Agrega `print_deadline_slack_ms` (`DeadlineAt` menos el instante de envío) y una alerta cuando su percentil 1 sea menor que `SendMarginMs`.
   - Presupuesto por tramo, como objetivo: OPC 10 a 30 ms; ingesta < 1 ms; transacción SQL 2 a 5 ms; claim y envío 2 a 5 ms.
   - Todo se confirma con benchmark.

## H6. Dueño único de impresora sin mecanismo

**Problema.** El invariante 9 de `AI_CONTEXT.md` exige un solo dueño por impresora, pero nada en el DDL lo implementa.

**Cambios requeridos:**

1. Tabla `dbo.PrinterLease (PrinterId PK, OwnerInstanceId, FencingToken BIGINT, LeaseUntil DATETIME2(3))`.
2. La adquisición usa un `UPDATE` condicional que incrementa `FencingToken`.
3. El claim y cada transición de `PrintAttempt` validan el token vigente.
4. Un dueño nuevo nunca reenvía los `SENDING` heredados: los pasa a `UNKNOWN`.

## H7. Presentación y consistencia

1. **ADR.**
   - Reescribe el contexto de 0001 a 0005: hoy los cinco repiten el mismo párrafo. Cada uno lleva el problema concreto en 2 a 4 oraciones, más alternativas, consecuencias y los IDs de las pruebas que lo sostienen.
   - Crea ADR nuevos con el mismo formato: 0006 (vencimiento por caja), 0007 (época del contador y colisiones), 0008 (serial por etiqueta y niveles de evidencia), 0009 (perfil mínimo y perfil extendido).
2. **Diagramas.**
   - Mermaid es la única fuente. Genera SVG y PNG con `@mermaid-js/mermaid-cli` (`mmdc`) y registra el comando en `diagrams/README.md`. Elimina los `.dot`.
   - En el 02, la decisión de duplicado es el `INSERT` único y va antes de escribir el Outbox. Agrega las ramas de plazo vencido y anomalía de secuencia.
   - El 03 refleja exactamente H2, y el 06 incluye las tablas y columnas nuevas.
   - Usa acentos correctos.
3. **Idioma.** Corrige las mezclas de inglés y español en todo el paquete, por ejemplo "Printer down antes de enviar", "Opc reconnect", "Restart SENDING", "safe retry", "Job result replay" y "Consumers / UI".
4. **Pseudocódigo de la sección 8.2.** Pasa de "consultar y luego insertar" a "insertar primero":
   - Inserta primero y captura los errores 2627 y 2601.
   - Ante esos errores, aplica H4 en una transacción nueva.
   - Valida el plazo antes de crear el job.
5. **Versión.** Pasa a 1.1 en `README.md` y en el front matter. Agrega `CHANGELOG.md` con cada cambio y el hallazgo que lo motivó.
6. **Documentos finales.** Regenera DOCX y PDF desde `DOCUMENTO_PRINCIPAL.md` con la misma cadena de herramientas y registra los comandos. Regenera `CHECKSUMS.sha256` al final.

---

## Pruebas nuevas

Agrégalas a `tests/MATRIZ_PRUEBAS.md` y a la sección 11 del documento.

| ID | Caso | Inyección | Resultado esperado |
| --- | --- | --- | --- |
| T17 | ERP vuelve tarde | Snapshot válido llega después de `DeadlineAt` | Detección `EXPIRED` (`ERP_UNAVAILABLE`), 0 bytes a la impresora, resultado `EXPIRED` publicado |
| T18 | Backoff excede el plazo | `RETRY_WAIT` con espera mayor que el plazo restante | `EXPIRED`, 0 bytes |
| T19 | SQL caído, perfil mínimo | SQL detenido 30 s bajo carga | Ciclos del hueco `EXPIRED` (`NOT_OBSERVED`), 0 impresiones tardías |
| T20 | Replay tardío, perfil extendido | Journal reproducido después del plazo | Vencidos `EXPIRED`, 0 bytes |
| T21 | Claim vencido | Job `READY` con `DeadlineAt` pasado | El `UPDATE` afecta 0 filas |
| T22 | Serial equivocado | El escáner lee el serial de otro job | No queda `CONFIRMED`; sube `serial_mismatch_total` |
| T23 | Contratos inválidos | Ejemplos de `invalid/` | Todos fallan la validación |
| T24 | Reinicio sin época | `CycleId` vuelve a 1 con la misma `PlcEpoch` | `SequenceAnomaly`, estación bloqueada, 0 jobs, alerta |
| T25 | Reinicio con época | `CycleId` vuelve a 1 con `PlcEpoch + 1` | Las cajas nuevas se imprimen normal |
| T26 | Rebote de sensor | Dos ciclos separados por menos de `MinCycleIntervalMs` | `BLOCKED` (`IMPLAUSIBLE_RATE`), 0 bytes |
| T27 | Dos PrintAgent | Dos instancias contra la misma impresora | Solo envía el token vigente; los `SENDING` heredados pasan a `UNKNOWN` |
| T28 | Coherencia del paquete | `tools/verify_package.py` | Código de salida 0 |

## Script de verificación

Crea `tools/verify_package.py` (Python 3 + `jsonschema`). Debe terminar con código distinto de 0 si algo falla.

1. Todos los ejemplos de `valid/` pasan y todos los de `invalid/` fallan, con Draft 2020-12 y validación de formatos.
2. Los estados y transiciones de `contracts/state-machine.json` coinciden con:
   - los `CHECK` del DDL (`CartonDetection.Status`, `PrintJob.Status`, `PrintAttempt.Outcome`);
   - el enum `outcome` del schema;
   - las transiciones de `diagrams/03_estados.mmd`.
3. El `eventId` de cada ejemplo de `CartonDetected` es igual a `UUIDv5(namespace, idempotencyKey)`.
4. `CHECKSUMS.sha256` es válido.

Ejecútalo y pega la salida en `docs/REVISION_LOG_v1.1.md`. Si tienes Docker, ejecuta el DDL en `mcr.microsoft.com/mssql/server:2022-latest` y registra la salida; si no, decláralo como no ejecutado.

## Criterios de aceptación

- [ ] Ningún camino permite enviar a la impresora un job con `DeadlineAt` vencido.
- [ ] Una sola máquina de estados y ningún artefacto que la contradiga.
- [ ] `CONFIRMED` solo con evidencia que cumple el nivel mínimo de la estación; si la evidencia es de escáner, el serial leído es el `LabelSerial` del job.
- [ ] Una colisión de clave con `DetectionTimestamp` distinto bloquea y alerta; nunca se reusa en silencio.
- [ ] Perfil mínimo documentado como diseño por defecto, con SLO hasta los bytes enviados.
- [ ] `PrinterLease` con fencing token implementa el invariante 9.
- [ ] ADR sin texto repetido, diagramas regenerados desde Mermaid, DOCX y PDF regenerados, checksums al día.
- [ ] `tools/verify_package.py` termina en 0 y su salida está en el log.
- [ ] `AI_CONTEXT.md` y `GUARDRAILS.md` incluyen los invariantes nuevos:
  - Nunca enviar un job con plazo vencido.
  - Una colisión de clave con timestamp distinto no es un duplicado.
  - `CONFIRMED` exige la evidencia definida en H3.

## Resumen que debes devolver al terminar

1. Archivos creados, modificados y eliminados, agrupados por hallazgo.
2. Comandos ejecutados y su resultado (script, DDL, render de diagramas, DOCX y PDF).
3. Lo que no pudiste ejecutar o verificar.
4. Decisiones abiertas para OT: valores de `TransitBudgetMs`, `SendMarginMs` y `MinCycleIntervalMs`; si el PLC puede exponer `PlcEpoch`; el nivel mínimo de confirmación por estación; y si se requiere el perfil extendido.
