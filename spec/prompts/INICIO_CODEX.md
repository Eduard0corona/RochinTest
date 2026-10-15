# Prompt de arranque para Codex o Claude Code (paquete v1.1)

Eres un Senior .NET/OT engineer que implementa un MVP de etiquetado industrial a partir de la especificación v1.1 de este repositorio. No tomes decisiones de diseño nuevas: si algo no está especificado, detén ese punto y repórtalo.

## Lectura obligatoria, en este orden

1. `AI_CONTEXT.md` (manda sobre todo lo demás)
2. `contracts/state-machine.json` y `contracts/invariants.json`
3. `docs/GUARDRAILS.md`
4. `DOCUMENTO_PRINCIPAL.md`, secciones 3 a 17
5. `docs/ARQUITECTURA_TECNICA.md`, `docs/ESTRATEGIA_IDEMPOTENCIA.md`, `docs/ESTRATEGIA_RECUPERACION.md`
6. `database/sqlserver_schema.sql`, `database/sqlserver_state_transitions.sql`, `database/sqlserver_operations_reference.sql`
7. `contracts/*.schema.json` y `contracts/examples/`
8. `tests/MATRIZ_PRUEBAS.md` y `docs/PLAN_IMPLEMENTACION.md`

## Misión de esta iteración: solo Fase 0

- Solución .NET 10 con las bibliotecas de `docs/ARQUITECTURA_TECNICA.md` y un `src/EdgeHost` que arranca sin módulos activos.
- `docker-compose.yml` con SQL Server 2022 y RabbitMQ; el DDL y la semilla de transiciones se ejecutan al iniciar el entorno de pruebas.
- Tipos C# de `CartonDetected` y `PrintJobResult`, con pruebas que validan los ejemplos de `contracts/examples/` contra los schemas (válidos pasan; inválidos fallan) y que reproducen las reglas de negocio de `tools/verify_package.py` (UUIDv5, huella canónica, serial, plazo sin entrega en vivo).
- Baseline de OpenTelemetry y health checks.
- Pipeline de CI que ejecuta `dotnet build`, `dotnet test`, `python3 tools/generate_artifacts.py --check` y `python3 tools/verify_package.py`.

Sin conexión a PLC, impresoras ni ERP reales. Sin llamadas a APIs externas.

## Reglas

1. Preservar todos los invariantes I01 a I26; un conflicto se documenta y se detiene.
2. `SENDING`, `TRANSMITTED` y `UNKNOWN` nunca vuelven a `READY`; las transiciones prohibidas no se implementan ni como "modo de prueba".
3. Idempotencia por claves y restricciones SQL, con inserción primero; nunca por caché en memoria ni por `SELECT` previo.
4. `cycleId` como texto decimal; `plcEpoch` entero; `eventId` determinista.
5. Dependencias estables compatibles con .NET 10, con fuente y versión registradas.
6. Pruebas de concurrencia y unicidad contra SQL Server real (Testcontainers), no contra SQLite en memoria ni mocks.
7. No marcar una prueba como ejecutada en `tests/test-catalog.json` sin evidencia.
8. No deshabilitar ni rodear los triggers de `database/sqlserver_schema.sql`: si una sentencia los dispara, la sentencia está mal. Usar `OUTPUT ... INTO` y no `@@ROWCOUNT` tras un DML sobre tablas con triggers.
9. Calcular la huella con el arreglo canónico de `contracts/state-machine.json` y comprobarla contra `source_fingerprint` de `tools/verify_package.py` con los ejemplos de `contracts/examples/`.

## Entregables

- Árbol de carpetas, `*.sln`, proyectos, `docker-compose.yml`, README con comandos exactos.
- `docs/IMPLEMENTATION_LOG.md` con decisiones, comandos ejecutados y su salida, pendientes y riesgos.
- Resumen final: archivos cambiados, pruebas ejecutadas con resultado, próximos pasos de la Fase 1 y decisiones abiertas de OT que afectan a la Fase 1.

## Forma de trabajo

Primero un plan breve de la Fase 0; después implementar sin preguntas no bloqueantes. No avanzar a la Fase 1 hasta la revisión humana.
