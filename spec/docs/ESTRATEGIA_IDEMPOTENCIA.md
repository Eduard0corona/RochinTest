# Estrategia de idempotencia (v1.1)

La entrega es al menos una vez en todo el flujo; el efecto lógico es exactamente una vez; la transmisión ordinaria potencial es a lo más una por job. El efecto físico "una etiqueta en la caja correcta" depende de integración física (sección 6).

## 1. Claves por nivel

| Nivel | Clave | Restricción | Generación |
| --- | --- | --- | --- |
| Detección | `(SiteId, StationId, PlcEpoch, CycleId)` | `UQ_CartonDetection_Identity` | Datos latched del PLC |
| Evento de detección | `DetectionId` = UUIDv5(`CartonDetected|` + idempotencyKey) | PK de `CartonDetection`, `UQ_OutboxEvent_EventId` | Determinista |
| Job | `(DetectionId, LabelType)` | `UQ_PrintJob_DetectionLabel` | En TX-A |
| Etiqueta | `LabelSerial` | `UQ_PrintJob_LabelSerial` | `LabelSerialSeq`, antes de TX-A |
| Transmisión | Un intento ordinario que pudo transmitir por job; un intento abierto por job; uno en transmisión por impresora | `UX_PrintAttempt_OrdinarySent`, `UX_PrintAttempt_JobOpen`, `UX_PrintAttempt_PrinterInFlight`, `TR_PrintAttempt_Guard` | TX-B |
| Orden | El ciclo reclamado es posterior al último de la impresora | `PrinterSendOrder` con `UPDLOCK` | TX-B |
| Reimpresión | Una por autorización, de su propio job | `UX_PrintAttempt_Authorization`, FK compuesta `(AuthorizationId, PrintJobId)` | TX-X |
| Resultado | UUIDv5(`PrintJobResult|` + idempotencyKey + labelType + resultVersion) | `UQ_OutboxEvent_EventId` | Determinista |
| Consumo | `(ConsumerName, EventId)` | `PK_InboxMessage` | En cada consumidor |

El namespace UUIDv5 es fijo y está en `contracts/state-machine.json` (`uuidNamespace`). RFC 9562 garantiza que el mismo namespace y nombre canónico producen el mismo UUID.

## 2. Ingesta

1. Validar `StatusCode` Good. Un valor Bad o Uncertain no es una detección: se cuenta, se registra y se espera el siguiente valor bueno; el hueco resultante se trata como hueco.
2. Calcular la huella de origen: SHA-256 del arreglo JSON canónico de los valores latched (`contracts/state-machine.json`, `fingerprint`).
3. Comparar con la marca alta en memoria de la época: igual a la huella conocida de ese `CycleId` reciente, descartar sin ir a SQL (optimización; la autoridad sigue siendo SQL).
4. Verificar la época contra `PlcEpochHistory` y la política de la revisión.
5. Obtener los seriales (`NEXT VALUE FOR`, fuera de la transacción), armar el payload y calcular `PayloadHash`.
6. Ejecutar TX-A.

## 3. TX-A con inserción primero

```
BEGIN TRAN
  INSERT CartonDetection (Status = RECEIVED)            -- falla con 2627/2601 si la clave existe
  [CARTON_ID_REQUIRED] SELECT ... WITH (UPDLOCK, HOLDLOCK) sobre IX_PrintJob_Carton e IX_CartonDetection_Carton
  Clasificar: plazo efectivo, snapshot ERP, estación, plausibilidad, recirculación
  [job] INSERT PrintJob (READY) con LabelSerial, payload y hash
  UPDATE CartonDetection → estado final
  [hueco] INSERT o UPDATE SequenceGap
  INSERT OutboxEvent (CartonDetected y, si es terminal sin job, PrintJobResult)
COMMIT
```

Ante 2627 o 2601 de `UQ_CartonDetection_Identity`, `UQ_CartonDetection_IdempotencyKey` o `PK_CartonDetection` (y solo de esas): ROLLBACK y, en una transacción nueva, leer la fila existente por la clave. Cualquier otra violación de unicidad se propaga como defecto. Un 1205 (víctima de interbloqueo, posible con `HOLDLOCK`) reintenta TX-A completa, lo que es seguro con inserción primero.

| Comparación | Resultado |
| --- | --- |
| Misma `SourceFingerprint` | Duplicado auténtico; ningún efecto |
| Huella distinta | `SequenceAnomaly` `IDENTITY_COLLISION`; `StationConfig.LabelingEnabled = 0` con `BlockReason = IDENTITY_COLLISION`; alerta |

Nunca se reutiliza un DbContext o una conexión con estado fallido como si la operación se hubiera persistido.

## 4. Transmisión

1. TX-B: claim condicional (`Status = READY`, plazo efectivo, estación habilitada, impresora de la revisión activa, lease, ciclo posterior al último reclamado por la impresora), avance de `PrinterSendOrder` e intento `SENDING` con `MayHaveSent = 1`. Commit. El resultado del claim se lee con `OUTPUT ... INTO`.
2. Segunda comprobación monotónica (ADR 0006). Si falla: TX-C con `NOT_SENT`.
3. Escritura. Si el método nunca se invocó: `NOT_SENT`. Si terminó: `TRANSMITTED`. Si falló después de invocarse: `UNKNOWN`. Que nunca se invocó lo sabe el código; SQL no puede comprobarlo (I02).
4. TX-D con la evidencia: se registra en `EvidenceObservation` y `TR_PrintAttempt_Guard` exige nivel, correlación y, si es `VERIFIED`, `ReadSerial = LabelSerial`. Un serial distinto lleva a `UNKNOWN` con `SERIAL_MISMATCH`; si el job ya estaba `CONFIRMED`, J18 lo corrige.

Lo que SQL rechaza aunque el código tenga un error: las transiciones prohibidas (`SENDING → READY`, `UNKNOWN → SENDING`, `TRANSMITTED → RETRY_WAIT`, entre otras); un intento `ORDINARY` sobre un job que no está en `SENDING`; un segundo intento ordinario que pudo transmitir; un estado sin etiqueta con un intento que pudo imprimir; una transición manual sin `OperatorAction`, una excepcional sin intento excepcional y una corrección sin lectura contradictoria.

## 5. Publicación y consumo

- El relay lee `IX_OutboxEvent_Pending` en orden de `OutboxSequence`, publica con `message-id` = `EventId` y marca `PublishedAt` tras el publisher confirm.
- Un confirm perdido produce una republicación con el mismo `EventId`.
- El consumidor inserta en `InboxMessage` y aplica su efecto en la misma transacción; para resultados conserva la `resultVersion` mayor por `(idempotencyKey, labelType)`.

## 6. Límites de la idempotencia

| Lo que garantiza | Lo que no garantiza |
| --- | --- |
| Un job y una transmisión ordinaria potencial por ciclo | Una etiqueta por caja física si la caja recircula sin `CartonId` |
| Ningún envío ordinario iniciado tras el plazo, dentro de los límites de la sección 10.2 del documento principal | Que la etiqueta no salga tarde de la cola del dispositivo |
| Orden de envío por impresora | Que el aplicador respete el orden de impresión |
| Que `CONFIRMED` cumple el nivel configurado | Que un nivel `PRINTED` pruebe aplicación |
| Que una reimpresión excepcional quede trazada | Que no exista una segunda etiqueta cuando el riesgo es `ACCEPTED` |
| Detección de colisiones y reinicios con huella y timestamp | Reconstruir la identidad de ciclos sin datos de origen |
