# CHANGELOG

Cambios de la implementación. Los de la especificación están en `spec/CHANGELOG.md`.

## Fase 7 · Hardening y demo (2026-10-15)

- SQL Server caído en la ingesta: falla cerrada, sin detección, con `opc_records_rejected_total{cause=SQL_UNAVAILABLE}`; al volver SQL, el hueco de los ciclos perdidos (T19).
- Recirculación: solo cuentan las detecciones anteriores de la caja, con el modo de identidad más restrictivo (SI-17, decidido por la persona; T16 y T32).
- `outbox_pending_age_seconds` se actualiza también cuando la publicación falla (T08).
- Tramos de latencia de la sección 10.4 que faltaban (`plc_to_opc_receive_ms`, `opc_receive_to_send_start_ms`, `print_deadline_slack_ms`, `send_start_to_applied_ms`, `applied_to_verified_ms`), con muestras acotadas por serie.
- `ReadyJobSignal`: TX-A, TX-E y el barrido despiertan al sender de línea; el sondeo queda como respaldo.
- Carga sostenida (T12) en `CartonPrinting.LoadTests` y `docs/BENCHMARK.md` generado por `build/benchmark_report.py`, como hipótesis declarada.
- Seguridad: RabbitMQ con TLS configurable (`RabbitMq__Tls__*` o `amqps://`), `build/check_secrets.py` en la compuerta y `OpcSecurityTests` (SignAndEncrypt con validación estricta contra el PLC simulado).
- Demo con el EdgeHost real como proceso (`tests/CartonPrinting.DemoHost`, `docs/DEMO.md`) y lista de aceptación en planta (`docs/FAT_CHECKLIST.md`).
- Compuerta: paso de secretos y opción `--carga`.
- Revisión independiente:
  - la falla cerrada con SQL caído se decide por la recepción del ciclo: intentos acotados, una sola caída para la ingesta y el latido, vigilancia de la recuperación y `LabelingReady` en falso hasta recargar el estado (T19 con SQL congelado y con ciclos procesados después de volver);
  - T16 concurrente, con el 1205 forzado y reintentado;
  - T08 con el plazo nominal;
  - el benchmark con barrido, Outbox Relay y SignAndEncrypt, y la sección de errores y escenarios excluidos;
  - prerrequisitos de planta en `FAT_CHECKLIST.md`;
  - la revocación del certificado TLS de RabbitMQ;
  - logs sin el endpoint OPC.
- Segunda revisión independiente:
  - una sola caída de SQL Server por proceso (`SqlOutage`) para la ingesta, el latido y el sender de línea (TX-B);
  - la vigilancia la levanta solo si no quedan operaciones colgadas, si recarga el estado y si toma el rango de TX-A, con histéresis;
  - errores de un servidor real clasificados; pool sin periodo de bloqueo; código de salida 71 si la ingesta se detiene;
  - interino de SI-04 en TX-E; SI-20 registrado;
  - T19 con escrituras bloqueadas, con la recarga y con un SQL Server real detenido y vuelto a arrancar.
- Verificación final por dimensiones (14 agentes, con refutación adversarial):
  - `LabelingReady` exige la impresora de línea tomada con TX-R0 y su sender sin pausa;
  - tres fallos seguidos del dispositivo ponen el sender en pausa, y vuelve cuando la impresora acepta conexiones;
  - TX-R0 antes del claim siguiente a un error de TX-B;
  - TX-A y TX-E leen la estación con lock compartido (frente a la purga de `CLOCK_JUMP`);
  - tramos de evidencia monotónicos;
  - alerta del Outbox medida aparte y publicación acotada a 10 s;
  - T08 con el criterio del catálogo primero y los vencidos comparados por ventana;
  - revisión de secretos ampliada y `.env.example` sin contraseñas;
  - SI-19 corregido (la variante de T16 es alcanzable).
- Verificación de esas correcciones (10 agentes):
  - pausa heredada por el sender nuevo, con espera mínima y período de prueba;
  - TX-E toma la estación antes que la detección;
  - TX-R0 del sender en marcha solo con su token de lease, y TX-R0 otra vez si el lease cambió antes del primer envío;
  - impresora en servicio solo con el sender corriendo;
  - marcas de escritura de las reimpresiones;
  - ventanas de T08 acotadas;
  - contraseñas en cadenas de conexión dentro de código.
- Los barridos de `OUT_OF_ORDER` y `CLOCK_JUMP` bloquean cada job por su clave primaria, en el mismo orden que el claim: la sentencia de referencia se interbloqueaba con él y la caja vencía (SI-21, `SweepLockOrderTests`).
- Diagnóstico de T12 en el reporte: jobs sin confirmar, picos de backlog, interbloqueos y esperas largas de SQL Server con su momento.
- Revisión de esa corrección:
  - un NOT_SENT con la estación bloqueada por `CLOCK_JUMP` vence el job (J08), extensión de SI-16;
  - la sentencia de referencia de `OUT_OF_ORDER` se conserva para T47;
  - T12 escribe su reporte aunque una fase no termine y aunque falle su diagnóstico;
  - `BENCHMARK.md` escapa las celdas y solo dice "todas `CONFIRMED`" si lo están.
- Pruebas: pendientes de la compuerta final.

## Fase 6 · Perfil extendido (2026-10-15)

- Sin código: `PERFIL_EXTENDIDO=NO` y OT-14 abierta. T09 y T20 quedan `NO_APLICA (OT-14 abierta)`.

## Fase 5 · Propiedad, recuperación y operación (2026-10-15)

- Propiedad por impresora: lock de sistema operativo por archivo, lease con renovación y ciclo de vida (`OwnedPrinterRunner`); sin propiedad, cero envíos y alerta.
- Arranque ordenado del EdgeHost (`StartupCoordinator`, ocho etapas) con propiedad, TX-R0, estado, barrido, suscripción y `LabelingReady` en orden.
- Operations API (`CartonPrinting.Operations`): autenticación por persona con claves de hash en la configuración, acciones manuales por TX-M y reimpresión con regla de dos personas; el sender de la impresora manual ejecuta TX-X.
- Métricas `exceptional_reprints_total{risk,result}` y `unknown_resolution_total{result}`.
- Pruebas: T27, T34, T36, T40, T41 y T43 en verde contra SQL Server real, la impresora simulada, el PLC simulado y procesos reales.
- Revisión independiente: el arranque no sigue sin la impresora de línea (y el lease se libera al detener); una renovación que falla cuenta como lease perdido; TX-R0 antes de volver a enviar, probado con un `SENDING` heredado; los fallos del arranque se registran; una autorización rechazada no bloquea a las demás; validación de la API (400) y solicitud pendiente tras un rechazo.
- Segunda revisión independiente: el sender se detiene al vencer la cobertura local del lease (duración menos gracia), aunque SQL cuelgue, y un error transitorio se reintenta; el ciclo de propiedad sobrevive a un error del sender; la etapa 3 no se completa sin la impresora; desbloqueo decidido en TX-M; cualquier rechazo de TX-X espera; pruebas de la liberación del lease y del NOT_SENT del sender manual.

## Fase 4 · Print Agent (2026-10-15)

- Impresora simulada por TCP (rechazo de conexiones, corte a mitad de la escritura, impresión sin acuse, cola retenida, cancelación según capacidades) y simulador de evidencia (impresora, aplicador y escáner por ciclo).
- `PrinterSenderService`: un sender por impresora, claim con `PrinterSendOrder`, segunda comprobación monotónica, `NOT_SENT` solo sin escritura invocada, TX-C en su propia transacción y reaplicación de la evidencia que llega durante `SENDING`; `EvidenceService` (TX-D y métricas de la sección 19), `DeviceCancellation` (A07 y J12) y `SenderRecovery` (TX-R0).
- `CartonPrinting.FaultHost`: el sender real en un proceso aparte para matarlo en tres puntos (T07).
- EdgeHost: sender de la impresora de línea con lease, recuperación de arranque y la guarda del reloj de la estación.
- Pruebas: T05, T06, T07, T15, T22, T29, T31, T38, T39, T42 y T44 en verde contra SQL Server real y la impresora simulada.
- Correcciones de la revisión independiente: la comprobación monotónica es lo último antes de escribir; la evidencia se aplica en orden desde el último claim y se reaplica al arrancar; TX-C con reintentos y detención del sender; sin bucle con el claim rechazado; conexión cerrada reabierta y escritura con tiempo máximo; sender sin `PrinterSendOrder` no arranca.
- Segunda revisión independiente: una lectura contradictoria anterior no queda detrás de una confirmación, tampoco bajo concurrencia (guarda en TX-D y lock de evidencia por ciclo); NOT_SENT ante cualquier error o apagado entre el claim y la escritura; el arranque reaplica las correcciones J18 pendientes; consultas de evidencia acotadas; el EdgeHost sale con código 70 si el sender se detiene.
- Tercera revisión independiente: la guarda de TX-D con la coherencia de la aplicación; el lock del ciclo antes del job y reintento de la víctima de interbloqueo; reaplicación del arranque guiada por la evidencia reciente y en paralelo con el sender; el EdgeHost sale con código 70 ante cualquier falla del servicio de impresora; SI-18 decidido.

## Fase 3 · ERP y plazo (2026-10-14)

- ERP simulado por HTTP (retraso, 503, sin respuesta); `HttpErpClient` con validación del snapshot; `ErpSnapshotSync` con señal de la ingesta, timeout y circuit breaker (Polly) y caché.
- `ErpArrivalService` (D06 y D08 por TX-E con plazo y margen efectivos); D07 en el barrido.
- `StationSweepService` (TX-S por estación) y `ClockGuard` (`CLOCK_JUMP`) en el barrido y en el claim; `CLOCK_SKEW` en la ingesta.
- EdgeHost: sincronización ERP y barrido por estación.
- Pruebas: T03, T04, T17, T18, T21, T33, T37 y T46 en verde.
- Correcciones de la revisión independiente: el salto del reloj se retiene hasta confirmar el bloqueo; el barrido usa el plazo efectivo (SI-14); J13 con su propia guarda; el job se arma con la fila de la caché y TX-E comprueba su contenido; validación del snapshot truncada y con largos; la revisión activa se exige en TX-B, TX-E y el barrido; T03 comprueba que la ingesta no espera al ERP.
- Registrados SI-14 (sentencias de referencia del barrido con el plazo congelado) y SI-15 (límites de `CLOCK_SKEW`).
- Segunda revisión: SI-16, decidido por la persona (al confirmar un `CLOCK_JUMP` vence lo pendiente, y el salto solo se confirma con ese bloqueo vigente); revisión obligatoria en el claim; TX-E sin `UPDLOCK`.

## Fase 2 · Ingesta OPC simulada (2026-10-14)

- PLC simulado como servidor OPC UA en proceso (registro latched con eco de cierre, FIFO, `LabelingReady`, `PrintWindowCycleId`) y proxy TCP para inyectar fallas.
- Cliente OPC UA real (`OpcUaCycleSource`): transporte por mensaje (`READ`, `SUBSCRIPTION`, `REPUBLISH`, `PLC_FIFO`), bit Overflow, Republish explícito y lecturas de recuperación al reconectar.
- `CycleDecoder` (solo StatusCode Good; registros incoherentes), `SequenceClassifier` (sección 9.2), `StationIngestService` (épocas, retención y reingreso, huecos), `LabelingReadyHeartbeat` e `IngestHostedService` en el EdgeHost.
- Pruebas: T11, T13, T24, T25, T26, T30, T35 y T45 en verde contra SQL Server real y el PLC simulado.
- Registrados SI-10 (mecanismo de coherencia del registro, OT-04) y SI-11 (escenario de T11).
- Correcciones de las dos revisiones independientes: numeración por suscripción y `READ` al recrearla (también por instancia), recuperación que termina sin esperar una caja, Republish desde la suscripción viva, marca de la última reconexión conservada, época confirmada visible en la ingesta en ejecución, dos instancias sin huecos espurios, registro incoherente con la misma regla en memoria y desde SQL, `LabelingReady` baja con el canal saturado, sesión cerrada si el arranque falla y anomalía resuelta una sola vez.
- Registrados SI-12 (valor de arranque de la marca alta en una época nueva) y SI-13 (registro incoherente frente a la secuencia); la persona mantuvo los interinos de SI-10, SI-12 y SI-13.
- Especificación 1.1.4 (aprobada por la persona): SI-11, escenario de reconexión de T11.

## Fase 1 · Datos e idempotencia lógica (2026-10-14)

- Capa de datos (`CartonPrinting.Infrastructure`) con una clase por transacción: TX-A, TX-B, TX-C, TX-D, TX-E, TX-S, TX-M, TX-X y TX-R0, sobre las sentencias de `spec/database/sqlserver_operations_reference.sql`; inserción primero, reintento de 1205, `OUTPUT ... INTO`, `SESSION_CONTEXT` limpiado también al fallar, contrato validado antes de escribir el Outbox.
- `CartonPrinting.Orchestration`: `RegistrationService` (serial y payload antes de TX-A), `DeadlinePolicy`, `LabelRenderer`.
- `CartonPrinting.PrintAgent`: `ClaimService` (TX-B con diagnóstico del rechazo y J03 por plazo u orden).
- `CartonPrinting.Outbox`: `OutboxRelay` con publisher confirms y `mandatory`, `RabbitMqPublisher`, `InboxConsumer` con cola quorum; el EdgeHost aloja el relay.
- Métricas de la sección 19 y trazas `carton.register`, `carton.claim` y `outbox.publish` con `idempotencyKey` y `correlationId`.
- Pruebas: T01, T02, T10, T14 y T47 en verde contra SQL Server 2022 y RabbitMQ reales.
- Especificación 1.1.3 (aprobada por la persona): SI-01 (texto del criterio de T47) y SI-07 (ADR 0016: J18 solo con una lectura posterior al estado que corrige).
- Correcciones de la revisión independiente: J18 después de una reimpresión confirmada, evidencia resuelta por el ciclo, registro sin orden de trabajo bloqueado en lugar de error 547, marca de reimpresión con su propio hash, claim sin 2601 con otro intento en vuelo, nivel y ventana de evidencia leídos dentro de TX-C.
- Defectos de la especificación registrados: SI-04 a SI-09.

## Fase 0 · Fundación (2026-10-14)

- Solución .NET 10 (`CartonPrinting.slnx`) con los proyectos de `spec/docs/ARQUITECTURA_TECNICA.md`, `src/EdgeHost`, `src/CartonPrinting.Simulators` y cuatro proyectos de prueba; versiones fijas en `Directory.Packages.props`.
- `CartonPrinting.Contracts`: tipos de `CartonDetected` y `PrintJobResult`, UUIDv5, huella JCS, instantes canónicos, validación JSON Schema 2020-12 y reglas de negocio de `verify_package.py` en C#.
- `CartonPrinting.Infrastructure`: ejecutor de migraciones que lee el DDL y la semilla desde `spec/database`, aprovisionamiento de los logins de migraciones y de aplicación, reloj sobre `TimeProvider`, telemetría y health check de SQL Server.
- `src/EdgeHost`: health checks, OpenTelemetry base y comandos `provision` y `migrate`.
- `docker-compose.yml` con SQL Server 2022 y RabbitMQ.
- Compuerta de CI (`build/ci.ps1`, `build/ci.sh`, `.github/workflows/ci.yml`) y `build/check_traceability.py`.
- Pruebas: T23 y T28 en verde; el DDL de la especificación se ejecutó por primera vez en SQL Server 2022.
