# Estrategia de recuperación (v1.1)

Regla común: ninguna recuperación imprime trabajo vencido. Lo que venció se registra, se publica como no impreso y la caja sigue la política física de OT.

## 1. Arranque del EdgeHost

| Paso | Acción | Por qué en ese orden |
| --- | --- | --- |
| 1 | `LabelingReady` permanece en falso | El PLC aplica su política mientras el servicio no está listo |
| 2 | Lock de sistema operativo y `PrinterLease` por impresora | Ninguna otra instancia puede reclamar |
| 3 | TX-R0: `SENDING` → `UNKNOWN` (intentos y jobs) | Lo que pudo transmitirse no vuelve a la cola |
| 4 | Vaciar la cola del dispositivo si la capacidad existe | Nada atrasado sale después del arranque |
| 5 | Cargar revisión activa, épocas, marca alta por época y `PrinterSendOrder`; iniciar la vigilancia de saltos de reloj | La clasificación y el claim necesitan el estado previo |
| 6 | Barrido TX-S inmediato por estación | Cierra `READY`, `RETRY_WAIT` y `WAITING_ERP` vencidos o atrasados, y ventanas de evidencia vencidas |
| 7 | Suscripción OPC UA; la primera notificación de cada monitored item (el valor actual) se registra como `READ`; comparación con la marca alta | Abre huecos por lo ocurrido durante la caída sin dar plazo a un valor que no llegó en vivo |
| 8 | `LabelingReady` en verdadero | Solo ahora la estación etiqueta |

## 2. Por punto de caída del servicio

| Punto | Estado persistido | Acción al arrancar |
| --- | --- | --- |
| Antes del commit de TX-A | Nada | El ciclo aparece como hueco o llega de nuevo (Republish, FIFO); se clasifica con su propio plazo, o sin plazo con `GATEWAY_RECEIPT` |
| Después de TX-A, antes del claim | Job `READY` | Si el plazo sigue vigente y el orden lo permite se envía; si no, `EXPIRED` |
| Después del claim, antes de escribir | Intento y job `SENDING` | `UNKNOWN` con `RESTART_DURING_SEND` (falso UNKNOWN aceptado) |
| Durante o después de escribir, antes de TX-C | `SENDING` | `UNKNOWN`; la evidencia puede resolverlo (J15) |
| Después de TX-C, esperando evidencia | Job e intento `TRANSMITTED` | Sigue esperando hasta el `EvidenceDeadlineAt` del intento; luego `UNKNOWN` |
| Durante una reimpresión excepcional | Intento excepcional `SENDING` o `TRANSMITTED`; job en su estado previo | TX-R0 pasa el `SENDING` a `UNKNOWN` y aplica X03 en la misma transacción; un `TRANSMITTED` espera su ventana y el barrido aplica A06 y X03 juntos |
| Después de un cambio de estado, antes de publicar | Fila pendiente en el Outbox | El relay publica; un duplicado se descarta por `EventId` |

## 3. Por componente

### RabbitMQ
La impresión no cambia. El Outbox acumula; el relay reintenta con espera exponencial y republica en orden de `OutboxSequence` con el mismo `EventId`. Alerta si `outbox_pending_age_seconds` supera 30 s.

### SQL Server, perfil mínimo
Falla cerrada: sin árbitro no se registra ni se imprime. Hasta dos reintentos cortos dentro del plazo de la caja; después se baja `LabelingReady`. Al volver: arranque completo (sección 1). Los ciclos durante la caída aparecen como `SequenceGap`; nunca como detecciones.

### SQL Server, perfil extendido
El gateway escribe el registro en el journal con `synchronous = FULL` y solo después confirma `AckCycleId` al PLC. No se imprime. Al volver SQL, el journal se reproduce con `Transport = JOURNAL_REPLAY`, `Origin = RECOVERED`, el `ReceivedAt` original y la revisión con la que se capturó; TX-A clasifica cada registro con su propio plazo, normalmente `EXPIRED` con `LATE_ARRIVAL`, y siempre así con `GATEWAY_RECEIPT`. En operación normal, el gateway ejecuta TX-A con el registro vivo y marca la fila del journal como replicada.

### Impresora
| Situación | Acción |
| --- | --- |
| No conecta antes de escribir | `NOT_SENT`; `RETRY_WAIT` con plazo; `FAILED` al agotar intentos |
| Calla después de escribir | `UNKNOWN`; alerta; caja retenida si el PLC puede |
| Tres fallos seguidos | Sender en pausa, `LabelingReady` en falso, alerta |
| Vuelve a estar disponible | Vaciar cola si se puede; descartar vencidos; seguir con la siguiente caja |

### ERP
Lento: la sincronización es asíncrona con timeout y circuit breaker; nunca bloquea la ingesta. Caído: se sigue con snapshots válidos; sin snapshot, `WAITING_ERP` hasta el plazo y después `EXPIRED`. Un cambio de orden con el ERP caído detiene esa orden hasta tener un snapshot válido.

### OPC UA
Reconexión con Republish si la sesión sigue viva; lectura del registro actual; huecos abiertos contra la marca alta; FIFO en el perfil extendido. El valor actual de un tag nunca se toma como historial. Lo recibido por Republish o por lectura entra con ese `Transport` y como `RECOVERED`; con `GATEWAY_RECEIPT` no tiene plazo y queda `EXPIRED` con `LATE_ARRIVAL`.

### Reloj del EdgeHost
Si el avance de `UtcNow` y el de `Stopwatch` divergen más que `ClockJumpToleranceMs`, la estación se bloquea con `CLOCK_JUMP`; los jobs pendientes vencen por el barrido o quedan sin enviar hasta el desbloqueo, que exige una `OperatorAction` `UNBLOCK_STATION` (`TR_StationConfig_Guard`).

## 4. Huecos de secuencia

1. Al registrar un `CycleId` mayor que la marca alta + 1 se crea `SequenceGap` `OPEN` con `GraceUntil = ahora + GapGraceMs`.
2. Cada detección que llega dentro del rango (Republish, FIFO, journal) cuenta como recuperada.
3. Si todo el rango llega: `RECOVERED`. Si vence la gracia: `CLOSED_UNRECOVERED` con `UnrecoveredCount`, alerta y métrica `cycles_unobserved_total`.
4. No se publica `CartonDetected` ni `PrintJobResult` para ciclos sin datos: no hay hechos que afirmar.

## 5. Impresión incierta

Ver la sección 15.1 del documento principal y `docs/RUNBOOK_OPERACIONES.md`. Las cuatro salidas de `UNKNOWN` son excluyentes y cada una queda auditada en `OperatorAction`, en `ReprintAuthorization` o en `EvidenceObservation`.
