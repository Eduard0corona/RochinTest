# Arquitectura técnica de componentes (v1.1)

Complementa las secciones 3 a 10 de `DOCUMENTO_PRINCIPAL.md` con el detalle que necesita quien implemente. Los estados y transiciones citados están definidos en `contracts/state-machine.json`.

## 1. Proceso y módulos

`src/EdgeHost` es un Worker Service de .NET 10 por estación (o por grupo de estaciones con la misma red). Cada módulo vive en su biblioteca y se registra como hosted service:

| Biblioteca | Hosted service | Hilos | Depende de |
| --- | --- | --- | --- |
| `CartonPrinting.OpcIngest` | `OpcIngestService` | Callback del cliente OPC UA y un consumidor de canal | OPC Foundation UA .NET Standard, `Infrastructure` |
| `CartonPrinting.Orchestration` | (lógica invocada desde la ingesta y TX-E) | — | `Contracts`, `Infrastructure` |
| `CartonPrinting.ErpSync` | `ErpSnapshotSyncService` | Un bucle con backoff y circuit breaker | Adaptador ERP |
| `CartonPrinting.PrintAgent` | `PrinterSenderService` (una instancia por impresora) | Un bucle dedicado por impresora | Adaptador de impresora |
| `CartonPrinting.Outbox` | `OutboxRelayService` | Un bucle | RabbitMQ.Client |
| `CartonPrinting.Recovery` | `StartupRecoveryService`, `SweeperService` | Arranque y temporizador | `Infrastructure` |
| `CartonPrinting.Operations` | Operations API (HTTP local con autenticación) | Pool de ASP.NET Core | `Infrastructure` |
| `CartonPrinting.Contracts` | — | — | Tipos de `CartonDetected` y `PrintJobResult`, validación JSON Schema |
| `CartonPrinting.Infrastructure` | — | — | SQL Server, OpenTelemetry, reloj |

## 2. Ruta crítica

```
OPC notification ──► Channel<CycleRecord> (acotado) ──► TX-A ──► Channel<PrintJobId> por impresora
                                                                  │
                                       PrinterSenderService ◄────┘
                                       TX-B claim (plazo, orden) ─► segunda comprobación ─► escritura ─► TX-C
```

- El callback OPC solo valida el `StatusCode`, sella `ReceivedAt` con el reloj monotónico y UTC, y encola. Nunca toca SQL, ERP ni impresora.
- El canal de entrada es acotado. Si se llena, la estación baja `LabelingReady` y se alerta: es una señal de sobrecarga, no un buffer infinito.
- Cada impresora tiene un único `PrinterSenderService` que consume su canal en orden de ciclo y mantiene una sola conexión TCP. Ese bucle es la serialización física dentro del proceso. El claim exige además que el ciclo sea posterior al último reclamado por la impresora (`PrinterSendOrder`).
- Las reimpresiones excepcionales autorizadas entran por la cola del sender de su impresora destino, que las ejecuta con TX-X por la misma conexión. La Operations API nunca abre una conexión a una impresora.
- Si la señal en proceso se pierde (reinicio, canal lleno), el sender relee los `READY` de su impresora desde `IX_PrintJob_Claimable`; SQL sigue siendo la fuente.

## 3. Transacciones

| ID | Qué hace | Estados que toca | Referencia |
| --- | --- | --- | --- |
| TX-A | Serial antes de la transacción; INSERT de la detección `RECEIVED`, clasificación, job `READY`, Outbox | D01 a D05, J01, G01, G02 | `database/sqlserver_operations_reference.sql` |
| TX-B | Claim con plazo, estación, impresora activa, orden y lease; avance de `PrinterSendOrder`; intento `SENDING` | J02, A01, J03 | idem |
| TX-C | Resultado de la escritura | J05 a J09, A02 a A04 | idem |
| TX-D | `EvidenceObservation`, evidencia, reconciliación positiva y corrección | J10 a J12, J15, J18, A05 a A08, X01, X02 | idem |
| TX-E | Snapshot ERP llega para una detección en espera | D06, D08, J01 | idem |
| TX-S | Barrido por estación: plazos, orden, ventanas de evidencia, huecos, autorizaciones | D07, J03, J11, J13, J14, G03, R04, A06, X03 | idem |
| TX-M | Acciones manuales con `OperatorAction` | J04, J16, J17, A09, R01, R03 | idem |
| TX-X | Reimpresión excepcional, ejecutada por el sender de la impresora destino | R02, A01 | idem |
| TX-R0 | Recuperación al arrancar | J09, A04 | idem |

Todas usan `SET XACT_ABORT ON` y el nivel READ COMMITTED (con RCSI recomendado en la base). Las validaciones entre tablas usan `UPDLOCK` y, cuando protegen contra inserciones concurrentes, `HOLDLOCK`; un 1205 se reintenta. Las reglas entre tablas que no dependen de una carrera (quién abre un intento, con qué evidencia se confirma, qué respaldo tiene una transición) las aplican los triggers de `database/sqlserver_schema.sql`.

## 4. Relojes

| Uso | Reloj |
| --- | --- |
| Identidad y huella | Ninguno: solo datos latched del PLC |
| `DetectionTimestamp` | Reloj del PLC (dato) |
| `ReceivedAt`, `DeadlineAt`, `@EdgeNowUtc` | UTC del EdgeHost, sincronizado por NTP o PTP |
| Segunda comprobación y duraciones | `Stopwatch.GetTimestamp()` (monotónico) |
| Vigilancia de saltos | Comparación periódica del avance de `UtcNow` y de `Stopwatch`; divergencia mayor que `ClockJumpToleranceMs` bloquea con `CLOCK_JUMP` |
| `CreatedAt`, `UpdatedAt` por defecto | `SYSUTCDATETIME()` de SQL, solo para auditoría |

El servidor SQL nunca decide un plazo: así un desfase del servidor SQL no afecta la seguridad del envío.

## 5. OPC UA

- Suscripción al nodo del registro latched del ciclo (no al booleano), con `QueueSize` mayor que 1 para no perder cambios entre publicaciones. Con cola mayor que 1, el servidor marca el bit de overflow en el StatusCode cuando descarta valores; un overflow se trata como posible hueco y se registra en `QueueOverflow`. Con `GATEWAY_RECEIPT`, una notificación con overflow no tiene plazo.
- Intervalos solicitados: muestreo 10 ms, publicación 20 ms. Se registran los valores revisados que devuelve el servidor.
- SignAndEncrypt con validación estricta de certificados.
- Tras reconectar: Republish de la sesión si es posible; lectura del registro actual; comparación con la marca alta para abrir huecos; FIFO del PLC en el perfil extendido. Cada registro conserva su transporte (`REPUBLISH`, `READ`, `PLC_FIFO`) y entra como `RECOVERED`. La primera notificación de un monitored item creado o transferido (al arrancar, al recrear la sesión o con TransferSubscriptions con valores iniciales) es el valor actual y se registra como `READ`, no como `SUBSCRIPTION`.
- El servicio escribe `LabelingReady` como latido con el periodo que acuerde OT.

## 6. Adaptador de impresora

Interfaz `IPrinterChannel` con capacidades declaradas, no supuestas:

| Capacidad | Usada por | Si no existe |
| --- | --- | --- |
| `SupportsJobCompletion` | Evidencia `PRINTED` | Nivel mínimo `APPLIED`, `VERIFIED` o `NONE` |
| `SupportsJobCancellationReport` | J12: confirmación por job de que se canceló o descartó y ya no está en la cola | J12 no se usa; un "no impreso" no basta para `FAILED` |
| `SupportsQueueInspection` y `SupportsCancelAll` | Arranque, toma de impresora, reconciliación negativa | J16 exige inspección física completa |
| `SupportsSingleConnection` | Exclusión física | Sin alta disponibilidad automática |

La escritura usa un socket persistente con timeout explícito. `NOT_SENT` se asigna solo si el método de escritura no llegó a invocarse; cualquier excepción posterior lleva a `UNKNOWN`.

## 7. Propiedad de la impresora

Al arrancar, para cada impresora (de línea y `ManualPrinterId`): (1) lock de sistema operativo con nombre `CartonPrinting.Printer.<PrinterId>`; (2) adquisición del `PrinterLease`; (3) TX-R0 para sus `SENDING` (con X03 para los excepcionales); (4) vaciado de cola si la capacidad existe; (5) lectura de `PrinterSendOrder` de cada impresora de línea: si falta su fila, el sender no arranca y alerta. El token del lease viaja en cada claim. Un fallo al renovar detiene el sender antes de que venza el lease, con un margen igual a `LeaseGraceMs`. Estos pasos detectan y evitan una segunda instancia; no impiden que un proceso suspendido transmita (ADR 0010).

## 8. Publicación

- Exchange `carton.events` de tipo topic; claves `carton.detected.v1` y `print.result.v1`.
- `message-id` = `EventId`; cabecera `ordering-key` = `idempotencyKey`.
- Publisher confirms; `PublishedAt` se fija solo tras el confirm.
- Consumidores con colas quorum, ACK manual después del commit de su Inbox.

## 9. Observabilidad

OpenTelemetry con una traza por ciclo (`idempotencyKey` como atributo) que abarca TX-A, la espera en canal, TX-B, la escritura y la evidencia. Métricas de la sección 19 del documento principal. Logs estructurados sin secretos.
