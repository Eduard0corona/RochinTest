# CartonPrinting

Implementación en .NET 10 del MVP de etiquetado industrial idempotente especificado en [`spec/`](spec/README.md) (paquete 1.1.3): ciclo OPC UA → `CartonDetected` → `PrintJob` → `PrintJobResult`, sin imprimir dos etiquetas por un evento duplicado.

En el MVP, simuladores reemplazan al PLC, la impresora, el aplicador, el escáner y el ERP. **Nunca se conecta equipo real**: cualquier conexión con equipos de planta requiere autorización explícita de OT. Ninguna garantía física (etiqueta aplicada, ventana física, etiqueta en la caja correcta) se da por demostrada con simuladores.

## Estado

El estado real de cada prueba T01 a T47, con su evidencia, está en [`docs/TEST_STATUS.md`](docs/TEST_STATUS.md). Las decisiones, comandos ejecutados y riesgos por fase están en [`docs/IMPLEMENTATION_LOG.md`](docs/IMPLEMENTATION_LOG.md); los defectos encontrados en la especificación, en [`docs/SPEC_ISSUES.md`](docs/SPEC_ISSUES.md). Cada fase cerrada tiene su etiqueta git `fase-N-ok`.

## Estructura

| Ruta | Contenido |
| --- | --- |
| `spec/` | Especificación 1.1.3, de solo lectura |
| `src/CartonPrinting.Contracts` | Tipos de `CartonDetected` y `PrintJobResult`, UUIDv5, huella JCS, validación JSON Schema y reglas de negocio |
| `src/CartonPrinting.Infrastructure` | SQL Server, migraciones, reloj, telemetría |
| `src/CartonPrinting.{OpcIngest,Orchestration,ErpSync,PrintAgent,Outbox,Recovery,Operations}` | Módulos de `spec/docs/ARQUITECTURA_TECNICA.md` |
| `src/CartonPrinting.Simulators` | Simuladores de PLC (OPC UA), impresora, ERP y evidencia |
| `src/EdgeHost` | Proceso único por estación; aloja los módulos como hosted services |
| `tests/` | `UnitTests`, `IntegrationTests` (Testcontainers), `FaultTests` y `LoadTests` |
| `build/` | Compuerta de CI, trazabilidad y aprovisionamiento SQL |

## Requisitos

- .NET SDK 10 (fijado en `global.json`).
- Docker con contenedores Linux (Testcontainers levanta SQL Server 2022 y RabbitMQ para las pruebas).
- Python 3.10 o mayor con `jsonschema>=4.18` y `sqlfluff` (verificador de la especificación).

En Windows, Python se invoca con `py`; en Linux y macOS, con `python3`.

## Comandos

Integridad de la especificación (debe terminar con `15 grupos OK, 0 errores`):

```bash
py spec/tools/verify_package.py
```

Compilar y probar (Testcontainers levanta su propio SQL Server y RabbitMQ):

```bash
dotnet build -warnaserror
```

```bash
dotnet test --filter "Category!=Load"
```

Trazabilidad de pruebas Txx:

```bash
py build/check_traceability.py
```

Compuerta completa de una fase (los cinco pasos anteriores):

```bash
powershell -ExecutionPolicy Bypass -File build/ci.ps1
```

```bash
build/ci.sh
```

## Entorno local con Docker Compose

1. Copiar `.env.example` a `.env` y cambiar las contraseñas (`.env` no se versiona).
2. Levantar SQL Server 2022 y RabbitMQ:

```bash
docker compose up -d
```

3. Aprovisionar la base y los dos logins (`cp_migrator` para DDL; `cp_app` para la aplicación, sin DDL y sin escritura sobre `dbo.AllowedTransition`). Las credenciales se pasan por variables de entorno o secretos de usuario:

```bash
dotnet run --project src/EdgeHost -- provision
```

Variables: `ConnectionStrings__SqlAdmin` (login sysadmin, por ejemplo `Server=127.0.0.1,14333;User ID=sa;Password=...;TrustServerCertificate=true`), `Database__MigratorPassword` y `Database__AppPassword`.

4. Aplicar el DDL y la semilla de transiciones de `spec/database/` con el login de migraciones (`ConnectionStrings__CartonPrintingMigrator`):

```bash
dotnet run --project src/EdgeHost -- migrate
```

5. Arrancar el EdgeHost con el login de la aplicación (`ConnectionStrings__CartonPrinting`); expone `/health/live` y `/health/ready` en `http://localhost:5080`:

```bash
dotnet run --project src/EdgeHost
```

## Reglas que gobiernan el trabajo

- `spec/AI_CONTEXT.md` manda sobre cualquier otra instrucción de implementación; ante una contradicción gana `spec/contracts/state-machine.json`.
- `CLAUDE.md` adapta el paquete a este repositorio y `PROMPT_IMPLEMENTACION.md` define el trabajo por fases.
- El DDL, los schemas y los ejemplos se leen desde `spec/`; no se copian a `src/`.
