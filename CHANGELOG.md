# CHANGELOG

Cambios de la implementación. Los de la especificación están en `spec/CHANGELOG.md`.

## Fase 0 · Fundación (2026-10-14)

- Solución .NET 10 (`CartonPrinting.slnx`) con los proyectos de `spec/docs/ARQUITECTURA_TECNICA.md`, `src/EdgeHost`, `src/CartonPrinting.Simulators` y cuatro proyectos de prueba; versiones fijas en `Directory.Packages.props`.
- `CartonPrinting.Contracts`: tipos de `CartonDetected` y `PrintJobResult`, UUIDv5, huella JCS, instantes canónicos, validación JSON Schema 2020-12 y reglas de negocio de `verify_package.py` en C#.
- `CartonPrinting.Infrastructure`: ejecutor de migraciones que lee el DDL y la semilla desde `spec/database`, aprovisionamiento de los logins de migraciones y de aplicación, reloj sobre `TimeProvider`, telemetría y health check de SQL Server.
- `src/EdgeHost`: health checks, OpenTelemetry base y comandos `provision` y `migrate`.
- `docker-compose.yml` con SQL Server 2022 y RabbitMQ.
- Compuerta de CI (`build/ci.ps1`, `build/ci.sh`, `.github/workflows/ci.yml`) y `build/check_traceability.py`.
- Pruebas: T23 y T28 en verde; el DDL de la especificación se ejecutó por primera vez en SQL Server 2022.
