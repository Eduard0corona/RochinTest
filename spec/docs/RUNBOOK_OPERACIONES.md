# Runbook de operación y reconciliación (v1.1)

Toda acción manual pasa por la Operations API, con identidad de la persona, motivo y evidencia, y queda en `OperatorAction` (o en `ReprintAuthorization` para reimpresiones). SQL rechaza una transición manual sin ese registro. Las acciones sobre maquinaria (paro, desvío, rechazo) son de OT y del PLC; este runbook no las sustituye.

## UNKNOWN (impresión incierta)

1. No reenviar ni pulsar "reintentar": el sistema lo impide y el intento se registraría como violación.
2. Retener la caja si el PLC lo permite. Localizar `PrintJobId`, `AttemptId`, `LabelSerial` y la caja (`CartonId` o posición).
3. Buscar evidencia del intento original: lectura del serial, señal del aplicador correlacionada, estado por job del dispositivo.
4. Elegir una sola salida:

| Situación | Acción | Transición |
| --- | --- | --- |
| Hay evidencia del nivel exigido y el `UNKNOWN` vino de una escritura interrumpida, un reinicio o una ventana vencida | Registrar la evidencia (`EvidenceObservation`; una inspección manual lleva la identidad de quien inspecciona) | J15 → `CONFIRMED` |
| Se prueba la ausencia: dispositivo confirma no impresión, cola vacía o cancelada y la caja no tiene la etiqueta | `OperatorAction` `RECONCILE_NEGATIVE` con las tres pruebas; sin una de ellas el registro se rechaza | J16 → `FAILED` |
| No se puede resolver, o el `UNKNOWN` vino de un serial distinto (`SERIAL_MISMATCH`) | `OperatorAction` `CLOSE_UNRESOLVED`; rechazar o poner en cuarentena la caja | J17 → `CLOSED_UNRESOLVED` |
| La caja retenida necesita etiqueta | Reimpresión excepcional (abajo) | X01 tras la evidencia, o X03 si no se confirma |

## Reimpresión excepcional

1. Quien solicita registra: job, motivo, caja retenida, evidencia de inspección, impresora destino y plazo para la evidencia (`EvidenceWindowMs`). La impresora destino es la `ManualPrinterId` de la estación, fuera de línea; el sistema no admite la impresora de línea, porque una print-and-apply en marcha aplicaría la etiqueta a la caja que esté bajo el aplicador y bloquear la estación en software no detiene la línea (OT-16). Sin `ManualPrinterId` configurada no hay reimpresión.
2. Otra persona aprueba. El riesgo debe ser coherente con el estado previo: `ACCEPTED` desde `UNKNOWN` o `CLOSED_UNRESOLVED`; `NONE_PROVEN` o `ACCEPTED` desde `FAILED` o `EXPIRED`, y `NONE_PROVEN` solo si ningún intento del job que pudo transmitir quedó sin probar como `NOT_PRINTED`; `REPLACES_EXISTING_LABEL` para etiqueta dañada. Solo puede haber una autorización vigente por job.
3. Etiqueta dañada: retirar la etiqueta original y registrar el retiro (`OriginalLabelRemovedBy`) antes de ejecutar; sin ese registro, el sistema no crea el intento.
4. La ejecuta el sender de la impresora destino antes de `ExpiresAt`, por su conexión única. Cada autorización sirve para un solo intento; si ese intento queda `NOT_SENT`, se pide otra.
5. La etiqueta lleva el mismo `LabelSerial` (y marca de reimpresión si `ReprintMarker`).
6. Se verifica en la caja retenida, normalmente por inspección manual que lee el serial y registra el `AttemptId` de la reimpresión: una lectura anterior del mismo serial no la confirma. Esa evidencia confirma el job (X01 o X02). Si no llega dentro de `EvidenceWindowMs`, el job queda `UNKNOWN` con `EXCEPTIONAL_REPRINT_UNRESOLVED` (X03): una autorización posterior será siempre con riesgo `ACCEPTED`. El resultado publicado declara `exceptionalReprint` y el riesgo en ambos casos.

## Anomalías de secuencia

| Tipo | Significado | Qué hacer |
| --- | --- | --- |
| `IDENTITY_COLLISION` | Misma clave, datos de origen distintos | Confirmar con OT si el contador se reinició; si sí, aceptar una época nueva; desbloquear con motivo |
| `RESET_SUSPECTED` | Contador menor con timestamp más nuevo | Igual que el anterior |
| `EPOCH_UNEXPECTED` | Salto de época no permitido por la política | Confirmar la época (`OperatorAction` `CONFIRM_EPOCH`); los registros retenidos se reingresan con `Transport = ANOMALY_REINGEST` y su propio plazo decide |
| `AMBIGUOUS_DECREASE` | Contador menor sin timestamp | La detección ya quedó `BLOCKED`; revisar si se repite; bloquea la estación solo si es un patrón |

Desbloquear una estación registra una `OperatorAction` `UNBLOCK_STATION`, quién y por qué en `StationConfig`, y resuelve la anomalía con `ResolvedBy` y `Resolution`. Las cajas cuyos ciclos quedaron como anomalía no tienen resultado publicado: tratarlas según la política de OT (OT-09).

## Huecos de secuencia

Un `SequenceGap` `CLOSED_UNRECOVERED` indica cajas que pasaron sin datos. Contar las cajas sin etiqueta en la línea, comparar con `UnrecoveredCount` e informar a calidad. El sistema no publica resultados para esos ciclos.

## RabbitMQ caído

No borrar el Outbox. Verificar conexión y publisher confirms. Al volver, la republicación usa los mismos `EventId`; verificar que los consumidores no duplicaron efectos.

## SQL Server caído

Perfil mínimo: la estación baja `LabelingReady`; aplicar la política de OT para cajas sin etiqueta. Perfil extendido: verificar espacio del journal y que ningún ACK al PLC precedió al commit. Al volver, revisar huecos y vencidos.

## ERP caído o snapshot vencido

No crear jobs con lotes supuestos. Las detecciones esperan en `WAITING_ERP` y vencen. En un cambio de orden con el ERP caído, la orden nueva no se etiqueta hasta tener snapshot válido.

## Desfase y saltos de reloj

Si `clock_skew_ms` supera la tolerancia, la estación se bloquea con `CLOCK_SKEW`. Si el reloj del EdgeHost salta (divergencia entre reloj de pared y monotónico mayor que `ClockJumpToleranceMs`), se bloquea con `CLOCK_JUMP`. Corregir NTP o PTP; desbloquear con motivo. No cambiar la referencia de plazo sin una revisión nueva de configuración.

## Evidencia contradictoria después de confirmar

Un `PrintJobResult` `UNKNOWN` con `SERIAL_MISMATCH` posterior a un `CONFIRMED` (J18) significa que la caja del ciclo lleva otra etiqueta. Es un incidente de calidad: localizar la caja y la del serial leído, retenerlas y cerrar con J17 o reimprimir con autorización. La reconciliación negativa (J16) no procede: la etiqueta del job existe en alguna caja.

## Jobs vencidos por orden

`EXPIRED` con `OUT_OF_ORDER` significa que la impresora ya había recibido un ciclo posterior: imprimir ese job habría cruzado etiquetas. La caja sigue la política de cajas sin etiqueta.

## Cambio de configuración

Crear una revisión nueva con su motivo y otra persona como aprobadora. Los jobs pendientes conservan su revisión; los parámetros de seguridad se evalúan con el valor más restrictivo.

## Conmutación del Print Agent (sin alta disponibilidad automática)

1. Detener el EdgeHost activo y verificar que el proceso o el host están apagados, o aislar su camino de red hacia la impresora.
2. Solo entonces arrancar el respaldo: tomará lock y lease, pasará los `SENDING` heredados a `UNKNOWN` y vaciará la cola si puede.
3. Reconciliar los `UNKNOWN` resultantes.
