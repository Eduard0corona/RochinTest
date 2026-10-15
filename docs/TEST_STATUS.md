# Estado de las pruebas T01 a T47

Estado real de cada prueba del catálogo (`spec/tests/test-catalog.json`). `spec/tests/test-catalog.json` no se edita para registrar resultados: el estado vive aquí. Una prueba solo cuenta como `PASA` si se ejecutó, con su comando, fecha y salida en `docs/IMPLEMENTATION_LOG.md`.

- **Estado:** `PASA`, `FALLA`, `NO_EJECUTADA` o `NO_APLICA` (con motivo).
- **Entorno:** `SQL Server real` (Testcontainers con SQL Server 2022), `simulador` o `proceso`.
- Una prueba cuyo criterio incluye efectos físicos se mide en el simulador, y la nota lo dice: ningún resultado de esta tabla demuestra una garantía física.

| Txx | Fase | Estado | Pruebas xUnit | Entorno | Comando y fecha | Notas |
| --- | --- | --- | --- | --- | --- | --- |
| T01 | 1 | PASA | `IntegrationTests/Idempotency/DuplicateTagTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 20:20 | 100 entregas (SUBSCRIPTION, REPUBLISH, READ) desde 10 consumidores: 1 detección JOB_CREATED, 1 job, 1 intento, 1 CartonDetected, 1 PrintJobResult; duplicate_detection_total = 99 por transporte. **Cobertura parcial:** la transmisión se mide en una impresora en memoria con un arnés de sender; T01 se repite en la Fase 4 con el sender real y el simulador TCP. |
| T02 | 1 | PASA | `IntegrationTests/Idempotency/ClaimConcurrencyTests` (5 rondas y una con bloqueo retenido) | SQL Server real | `build/ci.ps1` · 2026-10-14 20:20 | 10 claims concurrentes: 1 ganador, 9 con @claimed vacío y sin excepción, claim_conflicts_total = 9, 1 intento SENDING, ningún evento adicional. Con un `UPDLOCK` retenido por otra transacción sobre la fila del job, ningún claim termina hasta liberarlo y después gana uno solo. |
| T03 | 3 | NO_EJECUTADA | — | — | — |  |
| T04 | 3 | NO_EJECUTADA | — | — | — |  |
| T05 | 4 | NO_EJECUTADA | — | — | — |  |
| T06 | 4 | NO_EJECUTADA | — | — | — |  |
| T07 | 4 | NO_EJECUTADA | — | — | — |  |
| T08 | 7 | NO_EJECUTADA | — | — | — |  |
| T09 | 6 | NO_EJECUTADA | — | — | — | Perfil extendido (Fase 6). |
| T10 | 1 | PASA | `IntegrationTests/Idempotency/LotChangeRaceTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 20:20 | R2 llega con el job READY; UPDATE de Payload, ErpRevision, LotId y PayloadHash fallan con 51005; el resultado publicado y la etiqueta transmitida (impresora en memoria) llevan el lote de R1. |
| T11 | 2 | NO_EJECUTADA | — | — | — |  |
| T12 | 7 | NO_EJECUTADA | — | — | — |  |
| T13 | 2 | NO_EJECUTADA | — | — | — |  |
| T14 | 1 | PASA | `IntegrationTests/Idempotency/RepublishedResultTests` | SQL Server real y RabbitMQ real | `build/ci.ps1` · 2026-10-14 20:20 | El relay publica 3 veces el mismo EventId (confirm "perdido"); el consumidor con InboxMessage aplica 1 efecto; inbox_duplicates_total = 2; con versiones desordenadas conserva la mayor por (idempotencyKey, labelType). El Inbox y la deduplicación son de `InboxConsumer` (src); el efecto "conserva la versión mayor" es del consumidor de prueba, porque los consumidores reales están fuera del MVP. |
| T15 | 4 | NO_EJECUTADA | — | — | — |  |
| T16 | 7 | NO_EJECUTADA | — | — | — |  |
| T17 | 3 | NO_EJECUTADA | — | — | — |  |
| T18 | 3 | NO_EJECUTADA | — | — | — |  |
| T19 | 7 | NO_EJECUTADA | — | — | — |  |
| T20 | 6 | NO_EJECUTADA | — | — | — | Perfil extendido (Fase 6). |
| T21 | 3 | NO_EJECUTADA | — | — | — |  |
| T22 | 4 | NO_EJECUTADA | — | — | — |  |
| T23 | 0 | PASA | `UnitTests/Contracts/ContractExamplesTests`, `ContractReferenceDifferentialTests`, `IdentityPrimitivesTests` | proceso | `build/ci.ps1` (dotnet test --filter "Category!=Load") · 2026-10-14 19:08 | 19 válidos pasan schema y reglas; 24 inválidos fallan solo por la palabra clave y la ruta de EXPECTED.json; 5 de negocio fallan exactamente su regla; prueba diferencial contra jsonschema y business_errors de Python. |
| T24 | 2 | NO_EJECUTADA | — | — | — |  |
| T25 | 2 | NO_EJECUTADA | — | — | — |  |
| T26 | 2 | NO_EJECUTADA | — | — | — |  |
| T27 | 5 | NO_EJECUTADA | — | — | — |  |
| T28 | 0 | PASA | `UnitTests/Package/SpecPackageTests` | proceso | `py spec/tools/verify_package.py` · 2026-10-14 | 15 grupos OK, 0 errores, pendientes de integración listados; además es un paso propio del workflow de CI. |
| T29 | 4 | NO_EJECUTADA | — | — | — |  |
| T30 | 2 | NO_EJECUTADA | — | — | — |  |
| T31 | 4 | NO_EJECUTADA | — | — | — |  |
| T32 | 7 | NO_EJECUTADA | — | — | — |  |
| T33 | 3 | NO_EJECUTADA | — | — | — |  |
| T34 | 5 | NO_EJECUTADA | — | — | — |  |
| T35 | 2 | NO_EJECUTADA | — | — | — |  |
| T36 | 5 | NO_EJECUTADA | — | — | — |  |
| T37 | 3 | NO_EJECUTADA | — | — | — |  |
| T38 | 4 | NO_EJECUTADA | — | — | — |  |
| T39 | 4 | NO_EJECUTADA | — | — | — |  |
| T40 | 5 | NO_EJECUTADA | — | — | — |  |
| T41 | 5 | NO_EJECUTADA | — | — | — |  |
| T42 | 4 | NO_EJECUTADA | — | — | — |  |
| T43 | 5 | NO_EJECUTADA | — | — | — |  |
| T44 | 4 | NO_EJECUTADA | — | — | — |  |
| T45 | 2 | NO_EJECUTADA | — | — | — |  |
| T46 | 3 | NO_EJECUTADA | — | — | — |  |
| T47 | 1 | FALLA (un subcriterio, SI-07) | `IntegrationTests/Schema/TriggerGuardTests`, `ReferenceStatementTests`, `ReferenceCoverageTests`, `MigrationTests` | SQL Server real | `build/ci.ps1` · 2026-10-14 | Se cumple: DDL y semilla sin error; cada transacción de referencia con 0 y N filas sin excepción ni Msg 334, incluidas las ramas excepcionales (X03 en TX-C, TX-D, TX-S y TX-R0) y la recirculación (TX-A y D08 en TX-E); los 33 THROW (51001 a 51032 y 52001, SI-01) fallan con su código exacto; transiciones prohibidas, respaldos ausentes o anteriores (también un intento excepcional que no es el más reciente), cambio de solo Reason, PrinterSendOrder hacia atrás, PlcEpochHistory editada y desbloqueo sin OperatorAction rechazados; cada regla CHECK y UNIQUE de la sección 6 con una prueba que nombra la restricción. **No se cumple:** una corrección (CORRECTION) respaldada por una lectura registrada antes del claim no falla: el trigger la acepta (SI-07, clase B, decisión pendiente). Tampoco se rechaza una OperatorAction CONFIRM_EPOCH reutilizada (SI-08). |
