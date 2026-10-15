# Contratos públicos v1 (paquete 1.1)

`CartonDetected` y `PrintJobResult` son los dos eventos externos. Ambos usan JSON Schema Draft 2020-12. La versión 1 no se había publicado a consumidores, por lo que la revisión 1.1 la corrigió en sitio; los cambios están en `CHANGELOG.md`. Cualquier cambio incompatible a partir de ahora exige `schemaVersion = 2`, archivo nuevo y ADR.

## Identidad

| Dato | Regla |
| --- | --- |
| `idempotencyKey` | `siteId:stationId:plcEpoch:cycleId` |
| `CartonDetected.eventId` | UUIDv5(`uuidNamespace`, `CartonDetected|` + idempotencyKey). Es también `DetectionId` |
| `PrintJobResult.eventId` | UUIDv5(`uuidNamespace`, `PrintJobResult|` + idempotencyKey + `|` + labelType + `|` + resultVersion) |
| `PrintJobResult.correlationId` | `DetectionId` |
| `sourceFingerprint` | SHA-256 hex de la serialización JCS (RFC 8785) del arreglo `[siteId, stationId, plcEpoch, cycleId, detectionTimestamp, workOrderId, cartonId]`: `plcEpoch` número, `cycleId` cadena decimal, `detectionTimestamp` UTC truncado a milisegundos (`YYYY-MM-DDTHH:MM:SS.fffZ`), ausentes como `null` |
| `cycleId` | UInt64 como texto decimal, para no perder precisión en JavaScript |
| `configRevision` | `ConfigRevisionId` (identificador global de la revisión), no `RevisionNumber` |

`uuidNamespace` y la definición completa de la huella están en `contracts/state-machine.json`. La huella usa un arreglo JSON y no una cadena unida por separadores porque `workOrderId` y `cartonId` no tienen patrón y la unión con `|` sería ambigua. La implementación de referencia es `source_fingerprint` en `tools/verify_package.py`; la del EdgeHost debe producir los mismos bytes (T25). El codificador por defecto de System.Text.Json escapa caracteres como `+`, `&`, `<`, `>` y `'`, y produciría otra huella: un duplicado auténtico se clasificaría como colisión. El SourceTimestamp de OPC es informativo y no participa en la identidad ni en la huella.

## Semántica de `CartonDetected`

- `transport` = `SUBSCRIPTION` es la única entrega en vivo y la única con `detectionOrigin` = `OBSERVED`. La primera notificación de un monitored item creado o transferido es el valor actual, no una entrega en vivo, y se publica como `READ`. `REPUBLISH`, `PLC_FIFO`, `JOURNAL_REPLAY`, `READ` y `ANOMALY_REINGEST` son recuperaciones.
- `queueOverflow` refleja el bit Overflow del StatusCode: la notificación pudo llegar tarde.
- Sin plazo confiable, `deadlineAt` = `receivedAt`: con `GATEWAY_RECEIPT`, un registro recuperado, con overflow o que abre un hueco (resultado `EXPIRED` con `LATE_ARRIVAL`); con `PLC_TIMESTAMP`, un registro sin `detectionTimestamp` (resultado `BLOCKED` con `INCOHERENT_SNAPSHOT`). Nunca se fabrica un plazo.
- `receivedAt` es la recepción original; un replay del journal la conserva.

## Semántica de `PrintJobResult`

- Se publica una versión por cada transición publicable; `resultVersion` crece por `(idempotencyKey, labelType)` y el consumidor aplica siempre la mayor.
- `final = true` significa que no se espera ninguna transición ordinaria. Una versión posterior todavía puede llegar por corrección (J18: evidencia posterior contradictoria) o por procedimiento excepcional (X01, X02, X03). Solo `UNKNOWN` es `final = false`.
- `CONFIRMED` exige la evidencia que lo decidió, verificada y con su correlación: `PRINTED` con `JOB`, `APPLIED` con `CYCLE`, `VERIFIED` con `CYCLE` y `readSerial`.
- `evidence` solo acompaña a `CONFIRMED` o a `UNKNOWN` con `SERIAL_MISMATCH`, que publica la lectura contradictoria `VERIFIED` con `verified = false`.
- `TRANSMITTED` solo se publica en estaciones sin fuente de evidencia (`MinConfirmationLevel = NONE`) y no afirma impresión.
- `BLOCKED` y `EXPIRED` por `LATE_ARRIVAL` o `ERP_UNAVAILABLE` no tienen job. `EXPIRED` por `DEADLINE_PASSED` u `OUT_OF_ORDER` sí lo tienen.
- `exceptionalReprint` aparece cuando una reimpresión autorizada confirmó el job (X01, X02) o terminó sin confirmar (X03, `UNKNOWN` con `EXCEPTIONAL_REPRINT_UNRESOLVED`), e incluye el riesgo de duplicado.
- `operatorAction` es obligatorio en `CANCELLED`, `CLOSED_UNRESOLVED` y `FAILED` con `RECONCILED_NOT_PRINTED`, y no aparece en ningún otro resultado.

## Lo que el schema no puede validar

| Regla | Dónde se valida |
| --- | --- |
| `eventId` y `correlationId` deterministas | Código y `tools/verify_package.py` |
| `sourceFingerprint` correcto | Código y verificador |
| `deadlineAt` = `receivedAt` sin plazo confiable | Código, `CK_CartonDetection_GatewayLive`, `CK_CartonDetection_PlcTimestamp` y verificador |
| `readSerial` = `labelSerial` al confirmar | `TR_PrintAttempt_Guard`, TX-D y verificador |
| Nivel de evidencia ≥ `MinConfirmationLevel` de la revisión | `TR_PrintAttempt_Guard` |
| Que la evidencia física sea verdadera | Integración física y aceptación en planta |

## Ejemplos

| Carpeta | Contenido | Expectativa |
| --- | --- | --- |
| `examples/valid/` | 19: al menos uno por cada `outcome`, los casos de corrección, reimpresión sin confirmar, fuera de orden, registro incoherente y acciones manuales, y cuatro `CartonDetected` | Pasan schema y reglas de negocio |
| `examples/invalid/` | 24 combinaciones prohibidas; regla, palabra clave y ruta en `EXPECTED.json` | Fallan el schema, cada una solo por su regla |
| `examples/business-invalid/` | 5: serial distinto, `eventId` no determinista, huella incorrecta y dos plazos fabricados | Pasan el schema y fallan exactamente una regla de negocio |

`tools/build_examples.py` regenera las tres carpetas con los campos derivados correctos. Los identificadores y las revisiones de los ejemplos son ilustrativos: `ConfigRevisionId` 7 (LINE-01, `VERIFIED`), 3 (LINE-02, `NONE`), 5 (LINE-04, `APPLIED`) y 12 (LINE-03, `GATEWAY_RECEIPT`), con `TransitBudgetMs` 1500, `DeviceLeadTimeMs` 400 y `ClockSkewToleranceMs` 50.
