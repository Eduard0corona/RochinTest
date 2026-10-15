# Registro de implementación

Decisiones, comandos ejecutados con su salida, pendientes y riesgos, por fase. Fechas en hora local de la máquina de desarrollo (Windows 11, zona del autor). Un resultado de prueba solo cuenta si su comando y su salida están aquí.

## Entorno (2026-10-14)

| Herramienta | Versión | Cómo se comprobó |
| --- | --- | --- |
| Sistema operativo | Windows 11 Pro 10.0.26200 | — |
| .NET SDK | 10.0.302 (fijado en `global.json`, `rollForward: latestFeature`) | `dotnet --version` |
| Python | 3.14.2, con `jsonschema` 4.26.0 y `sqlfluff` 4.4.0 | `py --version`; `importlib` |
| Docker | 29.8.0, motor Linux (Docker Desktop, contexto `desktop-linux`) | `docker info` |
| SQL Server (pruebas y compose) | `mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04`, versión 16.0.4295.3 | `docker image inspect` |
| RabbitMQ (pruebas y compose) | `rabbitmq:4.3.5-management` | `docker pull` |
| Git | rama `main` limpia al empezar; primera entrada de `spec/CHANGELOG.md`: 1.1.2 | `git status`; `head spec/CHANGELOG.md` |

Docker Desktop estaba instalado pero detenido al empezar; se inició desde `C:\Users\lalon\AppData\Local\Programs\DockerDesktop\Docker Desktop.exe`. No se instaló nada.

```
> py spec/tools/verify_package.py
...
Resultado: 15 grupos OK, 0 errores
```

### Paquetes NuGet (fuente: nuget.org)

| Paquete | Versión | Uso |
| --- | --- | --- |
| Microsoft.Data.SqlClient | 7.1.1 | Capa de datos |
| Dapper | 2.1.89 | Lecturas fuera de la ruta crítica |
| System.Security.Cryptography.Pkcs | 10.0.13 | Fijado (transitivo de SqlClient): la 9.0.18 tiene GHSA-2fmv-rv2c-59x7 y GHSA-846f-f764-76mx |
| RabbitMQ.Client | 7.2.2 | Outbox Relay con publisher confirms |
| JsonSchema.Net | 9.4.1 | Validación Draft 2020-12 de los contratos |
| OPCFoundation.NetStandard.Opc.Ua.Client / .Server / .Configuration | 1.5.378.182 | Cliente OPC UA real y servidor simulado (línea estable 1.5; la 2.0 es una versión mayor reciente que no se validó) |
| Microsoft.Extensions.Hosting(.Abstractions), Options, Logging.Abstractions, Diagnostics.HealthChecks(.Abstractions) | 10.0.13 | Hosting y health checks |
| OpenTelemetry.Extensions.Hosting, OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.19.1 | Trazas y métricas |
| Polly.Core | 8.8.0 | Circuit breaker del ERP |
| Microsoft.NET.Test.Sdk | 18.11.0 | Pruebas |
| xunit.v3 / xunit.runner.visualstudio | 3.2.2 / 3.1.5 | Pruebas (línea 3.x; la 4.x es reciente y no se validó) |
| Testcontainers.MsSql / Testcontainers.RabbitMq | 4.16.0 | SQL Server y RabbitMQ reales en pruebas |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | Reloj controlado en pruebas |

## Fase 0 · Fundación (T23, T28)

### Plan

- **Entregables:** solución en la raíz con los proyectos de la sección 1 de `spec/docs/ARQUITECTURA_TECNICA.md`, `src/EdgeHost` y `src/CartonPrinting.Simulators`; cuatro proyectos de prueba; `Directory.Build.props` y `Directory.Packages.props`; `docker-compose.yml`; ejecutor de migraciones; contratos con UUIDv5, huella JCS y reglas de negocio; OpenTelemetry base y health checks; compuerta de CI y trazabilidad; documentación.
- **Pruebas que la cierran:** T23 (contratos) y T28 (coherencia del paquete, en CI).
- **Riesgos previstos:** que el DDL no ejecute en SQL Server real (sería la primera ejecución); que la biblioteca JSON Schema de .NET informe los errores con otra granularidad que jsonschema de Python, lo que haría imposible exigir "solo por la palabra clave y la ruta declaradas".

### Lo hecho

- `CartonPrinting.slnx` (formato que crea el SDK 10) con 11 proyectos en `src/` y 4 en `tests/`. `Directory.Build.props`: net10.0, nullable, advertencias como errores, análisis `latest`. `Directory.Packages.props` con versiones fijas y pinning transitivo.
- `CartonPrinting.Contracts`:
  - `Uuid5` (RFC 9562: SHA-1 del namespace big-endian más el nombre UTF-8, versión 5 y variante RFC; `new Guid(bytes, bigEndian: true)`), comprobado con el vector del apéndice A.4 de RFC 9562.
  - `Jcs`: serialización RFC 8785 para null, booleanos, cadenas, enteros, arreglos y objetos. Solo escapa comillas, barra invertida y controles. Los números no enteros no se admiten (no se usan).
  - `SourceFingerprint`, `Identity` (`idempotencyKey`, `CycleId` canónico), `EventIds`, `CanonicalTimestamp` (truncado a milisegundos, nunca redondeo; RFC 3339 con la semántica de `fromisoformat`).
  - `CartonDetectedEvent` y `PrintJobResultEvent` con `CycleId` como `ulong` serializado en texto decimal y los instantes en la forma canónica.
  - `ContractSchemaValidator` con JsonSchema.Net 9.4.1 y los formatos `uuid` y `date-time` con las reglas del verificador.
  - `ContractBusinessRules`: port de `business_errors` con los mismos mensajes y el mismo orden.
  - `StateMachineSpec`: lectura de `state-machine.json`.
- `CartonPrinting.Infrastructure`: `SqlScript` (lotes divididos en las líneas que solo contienen `GO`; sustitución de variables sqlcmd), `SqlMigrationRunner` (aprovisionamiento con `build/sql/provision.sql`; migración con el DDL y la semilla de `spec/database`), `EdgeClock` sobre `TimeProvider`, `CartonTelemetry` y `SqlServerHealthCheck`.
- `src/EdgeHost`: `WebApplication` con `/health/live` y `/health/ready`, OpenTelemetry (exportador OTLP solo si `OTEL_EXPORTER_OTLP_ENDPOINT` está configurado) y los comandos `provision` y `migrate`. Arranca sin módulos activos.
- `docker-compose.yml` (SQL Server 2022 CU27 y RabbitMQ 4.3.5 con healthchecks; contraseñas desde `.env`, no versionado), `.env.example`.
- `build/ci.ps1`, `build/ci.sh`, `build/check_traceability.py`, `.github/workflows/ci.yml`.

### Decisiones

1. **Esquemas y máquina de estados incrustados por enlace.** `CartonPrinting.Contracts.csproj` incrusta `spec/contracts/*.schema.json` y `state-machine.json` con `EmbeddedResource Link` desde `spec/`. No hay copia en `src/`: el binario se recompila si `spec/` cambia. Así el namespace UUIDv5 y la semántica de `final` salen de la fuente y no de constantes duplicadas.
2. **Errores de JSON Schema con la semántica de Python.** JsonSchema.Net informa también errores de los aplicadores (`allOf`, `then`, `properties`). `ContractSchemaValidator.Validate` devuelve los errores "hoja" como `iter_errors` de jsonschema: los aplicadores de paso (`allOf`, `properties`, `then`, `else`, `$ref`, `items`...) no producen error propio; `not`, `anyOf`, `oneOf`, `contains` y `additionalProperties` producen uno solo con su palabra clave; el subschema `if` nunca es error. Una prueba diferencial compara el resultado con jsonschema de Python para 18 documentos mutados (propiedad extra, formatos, rangos, varios errores a la vez).
3. **Oráculo de referencia en las pruebas.** `tests/Shared/reference_oracle.py` importa `spec/tools/verify_package.py` (con `-B` y `sys.dont_write_bytecode`: no escribe en `spec/`) para comparar huellas, errores de schema y reglas de negocio con la implementación de referencia. Las pruebas requieren Python con jsonschema, el mismo requisito de la compuerta.
4. **Registro de migraciones.** `migrations.AppliedScript` (esquema propio de la infraestructura, fuera del modelo de la especificación) guarda el SHA-256 de cada script aplicado. El esquema se aplica una sola vez dentro de una transacción; si `spec/database/sqlserver_schema.sql` cambia después de aplicado, la migración falla en lugar de aplicar otro DDL encima. La semilla (DELETE e INSERT) se reaplica si su hash cambia.
5. **Dos logins.** `build/sql/provision.sql` (ejecutado con un login sysadmin) crea la base con RCSI, `cp_migrator` (db_owner) y `cp_app` (db_datareader y db_datawriter, `DENY ALTER ON SCHEMA::dbo`). Después del DDL, el ejecutor concede `UPDATE` sobre `dbo.LabelSerialSeq` y aplica `DENY INSERT, UPDATE, DELETE ON dbo.AllowedTransition` a `cp_app`. El mismo script lo usan las pruebas (Testcontainers) y el entorno local.
6. **EdgeHost con SDK web.** Es un proceso de tipo worker que también expone health checks y, desde la Fase 5, la Operations API local; por eso usa `Microsoft.NET.Sdk.Web`.
7. **Globalización.** `InvariantGlobalization` en `false`: Microsoft.Data.SqlClient lanza `NotSupportedException` en modo invariante.
8. **xUnit v3 sobre VSTest.** `dotnet test --filter "Category!=Load"` filtra por el rasgo `Category`; cada prueba de la especificación lleva `[Trait("Spec", "Txx")]`.

### Comandos y resultados

Compuerta completa, 2026-10-14 18:41 (`powershell -File build/ci.ps1`, salida en el registro de la sesión):

```
== 1/5 Integridad de la especificación
Resultado: 15 grupos OK, 0 errores
== 2/5 Artefactos derivados al día
Artefactos generados al día.
== 3/5 Compilación sin advertencias
    0 Advertencia(s)
    0 Errores
== 4/5 Pruebas (sin carga)
Correctas! - Con error: 0, Superado: 112, Omitido: 0, Total: 112, Duración: 12 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error: 0, Superado: 11, Omitido: 0, Total: 11, Duración: 19 s - CartonPrinting.IntegrationTests.dll (net10.0)
== 5/5 Trazabilidad Txx
  Fase 0: T23=3 archivo(s), T28=CI(verify_package.py)
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Primera ejecución del DDL en SQL Server 2022 real (Testcontainers, `IntegrationTests/Schema/MigrationTests`, 11 pruebas): el esquema completo (58 lotes) y la semilla se crean sin error, incluidos los 14 índices filtrados y todos los triggers (ninguno deshabilitado); la semilla coincide fila por fila con las transiciones de `state-machine.json`; una segunda migración no aplica nada; el login de la aplicación recibe error de permisos al escribir `AllowedTransition`, deshabilitar un trigger o crear una tabla, y sí puede tomar valores de `LabelSerialSeq`.

Entorno local con Docker Compose, 2026-10-14 (contraseñas generadas al azar en un archivo fuera del repositorio):

```
> docker compose --env-file <fuera del repo> up -d        # sqlserver y rabbitmq healthy en ~15 s
> dotnet run --project src/EdgeHost -- provision           # Base CartonPrinting y logins aprovisionados
> dotnet run --project src/EdgeHost -- migrate             # aplicadas sqlserver_schema.sql, sqlserver_state_transitions.sql
> dotnet run --project src/EdgeHost -- migrate             # aplicadas (ninguna); sin cambios: ambos scripts
> dotnet run --project src/EdgeHost                        # /health/live = Healthy, /health/ready = Healthy (HTTP 200)
```

### Pruebas de la fase

- **T23: PASA.** 19 ejemplos válidos pasan schema y reglas de negocio C#; los 24 inválidos fallan solo por la palabra clave y la ruta de `EXPECTED.json` (igualdad exacta de conjuntos); los 5 de negocio pasan el schema y fallan exactamente su regla; la huella y los `eventId` calculados en C# son los de los ejemplos; la ida y vuelta por los tipos C# conserva cada documento; diferencial contra Python.
- **T28: PASA.** `verify_package.py` termina con código 0 y lista los pendientes de integración; `generate_artifacts.py --check` en verde.

### Revisión independiente

Pendiente de registrar (ver más abajo en esta sección cuando termine).

### Lo que no se verificó

- El workflow de GitHub Actions (`.github/workflows/ci.yml`) no se ejecutó: no se hizo `git push`. `build/ci.sh` no se ejecutó en Linux; se ejecutó `build/ci.ps1` en Windows.
- El exportador OTLP no se probó contra un colector.
- Solo se ejecutaron las rutas de código de la Fase 0; los módulos están vacíos.

### Riesgos

- Las pruebas que comparan con la implementación de referencia dependen de Python con jsonschema en el entorno de pruebas.
- `migrations.AppliedScript` impide reaplicar un `sqlserver_schema.sql` distinto: un cambio futuro del DDL necesitará una migración incremental explícita.
