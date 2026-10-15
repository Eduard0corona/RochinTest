# CHANGELOG

Cambios de la implementación. Los de la especificación están en `spec/CHANGELOG.md`.

## Fase 3 · ERP y plazo (2026-10-14)

- ERP simulado por HTTP (retraso, 503, sin respuesta); `HttpErpClient` con validación del snapshot; `ErpSnapshotSync` con señal de la ingesta, timeout y circuit breaker (Polly) y caché.
- `ErpArrivalService` (D06 y D08 por TX-E con plazo y margen efectivos); D07 en el barrido.
- `StationSweepService` (TX-S por estación) y `ClockGuard` (`CLOCK_JUMP`) en el barrido y en el claim; `CLOCK_SKEW` en la ingesta.
- EdgeHost: sincronización ERP y barrido por estación.
- Pruebas: T03, T04, T17, T18, T21, T33, T37 y T46 en verde.

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
