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
  - `SourceFingerprint`, `Identity` (`idempotencyKey`, `CycleId` canónico), `EventIds` (marcadores de `uuidNames` sustituidos en una sola pasada, como `str.format`), `CanonicalTimestamp` (truncado a milisegundos, nunca redondeo; RFC 3339 con la semántica de `datetime.fromisoformat` de Python 3.14, ver SI-03). UTF-8 estricto: un surrogate aislado lanza, como en Python.
  - `CartonDetectedEvent` y `PrintJobResultEvent` con `CycleId` como `ulong` serializado en texto decimal y los instantes en la forma canónica.
  - `ContractSchemaValidator` con JsonSchema.Net 9.4.1 y los formatos `uuid` y `date-time` como el FormatChecker del verificador (con `\z`: un salto de línea final no es válido).
  - `ContractBusinessRules`: port de `business_errors` con los mismos mensajes y el mismo orden, comprobado contra la referencia con documentos mutados (incluidos números como `42.0`, que `json.loads` lee como `float`).
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

Compuerta después de corregir los hallazgos de la revisión, 2026-10-14 19:08 (`powershell -File build/ci.ps1`, ahora con `--trx-dir`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error: 0, Superado: 122, Omitido: 0, Total: 122 - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error: 0, Superado: 13, Omitido: 0, Total: 13 - CartonPrinting.IntegrationTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
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

Subagente `revisor-adversarial` sobre `feat/foundation` (2026-10-14). Ejecutó la compilación y las pruebas en el árbol y en una copia limpia (`git archive`), el verificador, la trazabilidad, 49 382 mutaciones diferenciales contra jsonschema 4.26 y pruebas de permisos en un contenedor propio. Sin hallazgos ALTA; 3 MEDIA y 9 BAJA. Todos se corrigieron:

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| M1: nombres UUIDv5 con `Replace` encadenados sustituían marcadores dentro de los valores | Media | `EventIds.FormatName` sustituye en una sola pasada; `ContractBusinessRules.Validate` usa el `spec` recibido; casos `{labelType}` y `{resultVersion}` en el diferencial |
| M2: `$` aceptaba un salto de línea final en `uuid` y `date-time` | Media | `\z` en ambas expresiones; tres casos en el diferencial. La misma semántica en los `pattern` del schema es de la referencia: SI-02 |
| M3: la trazabilidad contaba Traits en comentarios y `#if`, y no exigía ejecución | Media | `check_traceability.py` quita comentarios, cadenas y bloques `#if`, asocia cada Trait a su clase o método y, con `--trx-dir`, exige pruebas superadas en el TRX de la corrida; la compuerta borra `TestResults` antes de probar. El comentario de `MigrationTests` declara que es la parte de esquema de T47 |
| B1: desplazamientos y `24:00` que Python acepta | Baja | `CanonicalTimestamp` calcula el UTC sin `DateTimeOffset` (desplazamiento < 24 h, `24:00:00`); casos en el diferencial; CI fijada en Python 3.14 (SI-03) |
| B2: `42.0` hacía lanzar las reglas de negocio | Baja | `PythonFloat` reproduce `repr` y `json.dumps` de `float`; enteros de precisión arbitraria |
| B3: surrogates aislados reemplazados por U+FFFD | Baja | `StrictUtf8` (lanza) en UUIDv5, huella y JCS |
| B4: `cp_app` podía escribir `migrations.AppliedScript` | Baja | `DENY INSERT, UPDATE, DELETE, ALTER ON SCHEMA::migrations` |
| B5: la prueba de permisos aceptaba varios códigos | Baja | Código exacto por sentencia y control positivo con `cp_migrator` dentro de una transacción revertida |
| B6: `ci.ps1` no reenviaba `--fase` | Baja | `$scriptArgs = $args` al inicio |
| B7: sin red de seguridad si el mapeo no encontraba errores; propiedades llamadas como palabras clave | Baja | Violación genérica si el documento es inválido sin errores hoja; la ruta de evaluación distingue nombres de propiedad de palabras clave |
| B8: migraciones concurrentes | Baja | `sp_getapplock` exclusivo de sesión al inicio de `MigrateAsync` |
| B9: afirmaciones excesivas y vacíos sin registrar | Baja | Textos corregidos en este registro y en los comentarios; SI-02 y SI-03 en `docs/SPEC_ISSUES.md` |

El revisor notó archivos sin seguimiento que aparecían en el árbol durante su revisión: eran el trabajo de la Fase 1 que empezó en paralelo en la rama `feat/sql-idempotency`; por eso repitió todo sobre una copia limpia de `feat/foundation`, con el mismo resultado.

### Lo que no se verificó

- El workflow de GitHub Actions (`.github/workflows/ci.yml`) no se ejecutó: no se hizo `git push`. `build/ci.sh` no se ejecutó en Linux; se ejecutó `build/ci.ps1` en Windows.
- El exportador OTLP no se probó contra un colector.
- Solo se ejecutaron las rutas de código de la Fase 0; los módulos están vacíos.

### Riesgos

- Las pruebas que comparan con la implementación de referencia dependen de Python con jsonschema en el entorno de pruebas.
- `migrations.AppliedScript` impide reaplicar un `sqlserver_schema.sql` distinto: un cambio futuro del DDL necesitará una migración incremental explícita.

## Fase 1 · Datos e idempotencia lógica (T01, T02, T10, T14, T47)

### Plan

- **Entregables:** capa de datos con `Microsoft.Data.SqlClient` y una clase por transacción (TX-A, TX-B, TX-C, TX-D, TX-E, TX-S, TX-M, TX-X, TX-R0), con las sentencias de `spec/database/sqlserver_operations_reference.sql`; TX-A con el serial obtenido antes, inserción primero, clave existente reconocida por el nombre de la restricción y reintento de 1205; Outbox en la misma transacción; Outbox Relay con publisher confirms y `message-id` = `EventId`; consumidor de prueba con `InboxMessage`.
- **Pruebas que la cierran:** T01 (tag duplicado), T02 (concurrencia en el claim), T10 (cambio de lote en carrera), T14 (resultado republicado) y T47 (DDL y sentencias de referencia: cada sentencia con 0 y con N filas, cada `THROW` 51001 a 51032 y 52001 con su código exacto).
- **Decisiones previstas:** la capa de datos implementa ya todas las transacciones porque T47 exige ejecutar cada sentencia de referencia; los servicios que las usan (ingesta, sender, barrido, API) llegan en sus fases. En T01 el "sender" es un arnés de prueba que reclama y escribe a un simulador en memoria que cuenta transmisiones; en la Fase 4 se repite con el sender real y el simulador TCP.
- **Riesgos:** que una sentencia de referencia o un trigger no ejecute en SQL Server real (sería un defecto de la especificación: sección 6 del prompt); interbloqueos en el claim concurrente.

### Lo hecho

- **Capa de datos** (`CartonPrinting.Infrastructure`): `SqlDb` (transacciones con `SET XACT_ABORT ON`, READ COMMITTED sobre RCSI, reintento completo ante 1205, limpieza de `SESSION_CONTEXT` también al fallar); parámetros tipados (`DATETIME2(3)` truncado, `DECIMAL(20,0)` para `CycleId`); una clase por transacción: `TxaRegisterDetection`, `TxbClaim`, `TxcWriteResult`, `TxdEvidence`, `TxeErpArrival`, `TxsSweep`, `TxmManualActions`, `TxxReprint`, `Txr0Recovery`. Las sentencias son las de `spec/database/sqlserver_operations_reference.sql`; todo DML sobre una tabla con triggers usa `OUTPUT ... INTO` una variable de tabla. `ResultPublisher` y `OutboxWriter` escriben cada `PrintJobResult` en la misma transacción que su transición y validan el contrato (schema y reglas de negocio) antes de insertarlo.
- **TX-A**: serial antes de la transacción (`LabelSerialSource`), payload y `PayloadHash` (SHA-256 de UTF-8) antes de insertar; inserción primero; clave existente reconocida por el nombre de la restricción (`UQ_CartonDetection_Identity`, `UQ_CartonDetection_IdempotencyKey` o `PK_CartonDetection`) y clasificada en otra transacción (duplicado o `IDENTITY_COLLISION` con bloqueo de estación); recirculación con `UPDLOCK, HOLDLOCK`; huecos (G01, G02) y anomalías en la misma transacción.
- **Orquestación**: `RegistrationService`, `DeadlinePolicy` (plazo por referencia; efectivo = mínimo entre congelado y recalculado; margen efectivo = máximo; intentos = mínimo; compuerta con la frescura menor), `LabelRenderer`.
- **Print Agent**: `ClaimService` (TX-B, diagnóstico del rechazo, J03 por plazo u orden, métricas).
- **Outbox**: `OutboxRelay` (orden de `OutboxSequence`, publisher confirms, `mandatory`, `PublishedAt` solo tras el confirm, espera exponencial), `RabbitMqPublisher` (`message-id` = EventId, cabecera `ordering-key`), `InboxConsumer` (cola quorum, ACK manual tras el commit del Inbox). El EdgeHost aloja el relay si hay `RabbitMq:Uri`.
- **Métricas**: `CartonMetrics` con las de la sección 19 y del catálogo; las pruebas leen los totales en memoria.

### Decisiones

1. **Todas las transacciones en la Fase 1.** T47 exige ejecutar cada sentencia de referencia con 0 y con N filas; por eso la capa de datos completa está aquí. Los servicios que las usan (sender, barrido, API) llegan en sus fases.
2. **Arnés de sender en T01.** El catálogo pide "1 PrintAttempt" y "como máximo una transmisión". En esta fase, cada una de las 100 señales intenta el claim y solo el ganador escribe a una impresora en memoria (`CountingPrinter`); el resultado medido es una transmisión. En la Fase 4 T01 se repite con el sender real y el simulador TCP.
3. **`mandatory` en la publicación.** Un evento sin cola enlazada vuelve como error (`PublishReturnException`) y el relay se detiene sin marcarlo publicado, en lugar de perderlo. Configurable (`RabbitMq:Mandatory`).
4. **Validación de contratos antes del Outbox.** Un evento que no cumple su schema o sus reglas de negocio revierte la transacción: emitir un contrato inválido es un defecto. Única tolerancia: SI-05.
5. **SI-04 y SI-05** (ver `docs/SPEC_ISSUES.md`): `CARTON_ID_REQUIRED` sin `CartonId` queda `BLOCKED` con `INCOHERENT_SNAPSHOT` (falla cerrada, pendiente de la persona); un registro `GATEWAY_RECEIPT` en vivo que abre un hueco se registra sin plazo según la máquina de estados, aunque la regla del verificador no vea el hueco.
6. **Plazo efectivo sin referencia activa.** Si la revisión activa no puede dar un plazo confiable para un registro (por ejemplo, cambió a `PLC_TIMESTAMP` y el registro no trae `DetectionTimestamp`), el plazo efectivo es su recepción: el job ya no se envía. Es la lectura más restrictiva de "nunca más permisivo que el congelado". Registrado como SI-09.
7. **J18 con la regla del ADR 0016.** La condición propia "lectura posterior al primer claim" que agregó la primera corrección se retiró en la segunda revisión (rodeaba un defecto de clase B). La persona decidió endurecer el trigger (SI-07, spec 1.1.3): la lectura que corrige tiene que ser posterior al último claim y a la observación que confirmó; TX-D aplica la misma regla.
8. **El claim no termina en excepción con otro intento en vuelo.** La guarda de TX-B excluye, sin bloqueo de rango, una impresora con otro intento `SENDING` ya confirmado; si dos claims de la misma impresora se cruzan, el 2601 de `UX_PrintAttempt_PrinterInFlight` (la barrera) revierte la transacción y el resultado es "sin claim". Así un claim de una impresora no bloquea el de otra.
9. **TX-C lee el nivel mínimo y la ventana de evidencia congelados dentro de la transacción** (del job para un intento ordinario, de la autorización para uno excepcional); el sender no los pasa.
10. **Payload de la reimpresión.** Es el del job (mismo `LabelSerial`) y, con `ReprintMarker`, una marca visible antes del cierre de la etiqueta; el intento registra el SHA-256 de ese payload en `AttemptPayloadHash` (I09).
11. **D07 solo en TX-S; G02 también en el barrido como rama defensiva** (SI-06): un snapshot tardío deja la detección en `WAITING_ERP` sin job y el barrido aplica D07; el barrido cierra como `RECOVERED` un hueco vencido con todos sus ciclos registrados.
12. **Con nivel congelado NONE, TRANSMITTED es terminal también para TX-D:** una lectura contradictoria o un "no impreso" se registran sin transición (guardas de A06, A07, J11 y J12).
13. **Una observación que referencia un intento de otro job no tiene efecto**, igual que una con el `PrintJobId` de otro job.

### Comandos y resultados

Compuerta completa, 2026-10-14 19:30 (`powershell -File build/ci.ps1`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error: 0, Superado: 122, Omitido: 0, Total: 122, Duración: 10 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error: 0, Superado: 56, Omitido: 0, Total: 56, Duración: 1 m 10 s - CartonPrinting.IntegrationTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 1 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=3 destino(s), 40 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Durante el desarrollo, dos pruebas fallaron por errores de la prueba que el sistema detectó como debía: un segundo claim en la misma impresora con un intento todavía en `SENDING` (`UX_PrintAttempt_PrinterInFlight`, 2601) y un claim de un ciclo anterior al último reclamado (`OUT_OF_ORDER`). Se corrigió la prueba, no el sistema.

### Pruebas de la fase

- **T01, T02, T10, T14 y T47: PASA** (detalle en `docs/TEST_STATUS.md`). T47 pasó a cumplirse por completo después del ADR 0016 (SI-07).
- T47 ejecuta, además del criterio del catálogo, una prueba que lee el DDL y la referencia, extrae los 33 códigos `THROW` y exige que cada uno tenga su prueba negativa.

### Revisión independiente

Subagente `revisor-adversarial` sobre `feat/sql-idempotency` (2026-10-14). 1 ALTA, 4 MEDIA y 9 BAJA. Todos se corrigieron o se registraron:

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| 1: una lectura contradictoria después de X01 (job confirmado por reimpresión) no aplicaba J18 | Alta | TX-D evalúa la contradicción antes de elegir el intento: X03 si referencia la reimpresión en curso, J11 si el job está en `TRANSMITTED`, J18 si está `CONFIRMED` (por cualquier vía) o `UNKNOWN` por otra causa; J18 limpia `ConfirmedByExceptionalReprint`. Prueba con y sin `AttemptId` (la de "con `AttemptId`" se agregó en la segunda revisión) |
| 2: una lectura `VERIFIED` de un ciclo con el `PrintJobId` de otro job podía confirmar a ese otro | Media | La evidencia correlacionada al ciclo resuelve el job por el ciclo; un `PrintJobId` que no coincide deja la observación registrada sin efecto |
| 3: un registro `GATEWAY_RECEIPT` no en vivo sin `WorkOrderId` terminaba en 547 (`CK_CartonDetection_WorkOrder`) | Media | El registro incompleto se clasifica antes que el plazo: `BLOCKED` con `INCOHERENT_SNAPSHOT` (salvo que la secuencia ya lo bloqueara) |
| 4: `ReprintMarker` se ignoraba y el intento excepcional repetía el hash del job | Media | `ReprintPayload.Render` y `AttemptPayloadHash` propio (decisión 10) |
| 5: sentencias de referencia y reglas de fila sin prueba en T47 | Media | `ReferenceCoverageTests`: recirculación en TX-A (job vivo y `WAITING_ERP`), D08 por recirculación en TX-E, A04+X03 en TX-C, X03 en TX-R0, TX-S y TX-D, respaldo excepcional anterior (51012), lease con 0 filas, y una prueba por cada `UNIQUE` y `CHECK` (`UQ_PrintJob_LabelSerial`, `UX_PrintAttempt_JobOpen`, `_OrdinarySent`, `_Authorization`, `_ConfirmingObservation`, `UX_ReprintAuthorization_OpenPerJob`, `CK_EvidenceObservation_Type`, `_Level`, `_Manual`). Las aserciones nombran la restricción exacta. Vacíos de guarda registrados: SI-07 y SI-08 |
| 6: J18 se repetía sobre un `UNKNOWN` que ya era `SERIAL_MISMATCH` | Baja | La segunda contradicción se registra sin transición ni versión nueva (el disparador de J18 dice "por otra causa") |
| 7: T02 no forzaba contención real | Baja | Prueba adicional: otra transacción retiene `UPDLOCK` sobre la fila del job; ningún claim termina mientras tanto; al liberarla, un ganador |
| 8: un claim con otro intento `SENDING` en la impresora lanzaba 2601 | Baja | Decisión 8 y prueba |
| 9: TX-C confiaba en el nivel y la ventana que pasaba el llamador | Baja | Decisión 9 |
| 10: la tolerancia de SI-05 no tenía pruebas | Baja | `OutboxWriter.ToleratedRule` y cuatro pruebas unitarias: tolerada solo con hueco, `GATEWAY_RECEIPT` y `DeadlineAt = ReceivedAt`; nunca encubre otra regla |
| 11: T14 no decía dónde vive el efecto "conserva la versión mayor" | Baja | Nota en `docs/TEST_STATUS.md`: es el efecto del consumidor de prueba |
| 12: faltaban CHANGELOG, lista de lo no verificado y trazas con `correlationId` | Baja | Entrada en `CHANGELOG.md`; secciones siguientes; actividades `carton.register` y `carton.claim` con `idempotencyKey`, `correlationId`, `printJobId` y `attemptId` (`TracingTests`), y `outbox.publish` con `eventId` e `idempotencyKey` (sin prueba: requiere RabbitMQ) |
| 13: D07 en TX-E y G02 en el barrido sin documentar | Baja | Comentarios en el código y SI-06 |
| 14: la decisión 6 no estaba registrada; SI-04 no decía que el camino quedó detenido | Baja | SI-09; SI-04 reformulado (camino en falla cerrada hasta la decisión); riesgo de la Fase 4 abajo |

Al corregir el hallazgo 6 se encontró que una prueba anterior de TX-D esperaba J18 sobre un `UNKNOWN` que ya era `SERIAL_MISMATCH`; la prueba contradecía el disparador de J18 y se corrigió (ahora también cubre J18 desde `UNKNOWN` por `WRITE_INTERRUPTED`).

Compuerta completa después de las correcciones, 2026-10-14 20:20 (`powershell -File build/ci.ps1`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   128, Omitido:     0, Total:   128, Duración: 20 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    74, Omitido:     0, Total:    74, Duración: 1 m 47 s - CartonPrinting.IntegrationTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 56 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Segunda revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `20e3194` (2026-10-14), obligatoria porque se había corregido un hallazgo ALTA. Ejecutó la compuerta (128 y 74 pruebas en verde), mutaciones (al restaurar el código anterior fallan 7 de las 8 pruebas de regresión) y siete sondas propias contra SQL Server real. Sin hallazgos ALTA; 3 MEDIA y 7 BAJA:

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| M1: la guarda propia de SI-07 no protegía el caso que la motivó y comparaba relojes de pared | Media | Retirada (decisión 7): J18 sigue la máquina de estados; la reaplicación diferida de evidencia queda detenida hasta la decisión sobre SI-07 |
| M2: T47 figuraba como PASA con un subcriterio que el DDL no puede cumplir | Media | T47 `FALLA` en ese punto en `docs/TEST_STATUS.md`; `Si_07_documents_that_the_trigger_accepts_…` fija el comportamiento del trigger; SI-07 menciona T47 |
| M3: con nivel congelado NONE, una contradicción o un "no impreso" lanzaban 51012 | Media | Decisión 12 y `Review2_m3_…` |
| B1: el bloqueo de rango de la guarda `PrinterBusy` acoplaba claims de impresoras distintas | Baja | Decisión 8 (sin `HOLDLOCK`; carrera perdida = sin claim) y `Review2_b1_…` (8 rondas de claims cruzados, sin excepción) |
| B2: la marca de reimpresión caía sobre el código de barras | Baja | `^FO40,480`, debajo de las barras y de su interpretación |
| B3: ninguna prueba detectaba que TX-C usara la ventana de la revisión en un intento excepcional | Baja | `Tx_s_elapsed_window_…` comprueba `EvidenceDeadlineAt` con la ventana de la autorización y que no vence con la de la revisión |
| B4: SI-06 mal clasificado (D07 en TX-E era una elección) y la carrera de G02 sin evidencia | Baja | TX-E ya no aplica D07 (decisión 11); SI-06 reformulado como rama defensiva |
| B5: el interino de SI-04 pasaba por encima de D04 | Baja | Solo la orden ausente fuerza BLOCKED antes del plazo; el interino de SI-04 va después (`Review2_b5_…`); SI-04 reformulado |
| B6: afirmaciones excesivas (prueba "con AttemptId", `correlationId` en `outbox.publish`, `GapToleranceTests` como T45) | Baja | Prueba con `AttemptId` agregada; texto de trazas corregido; `GapToleranceTests` sin Trait de T45 |
| B7: una contradicción con el `AttemptId` de otro job aplicaba J11 o J18 | Baja | Decisión 13 y `Review2_b7_…` |

Ninguna corrección fue de severidad ALTA: según el prompt, no hace falta una tercera revisión.

Compuerta completa después de las correcciones, 2026-10-14 21:00 (`powershell -File build/ci.ps1`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   128, Omitido:     0, Total:   128, Duración: 20 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    79, Omitido:     0, Total:    79, Duración: 1 m 42 s - CartonPrinting.IntegrationTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 61 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Decisiones de la persona (2026-10-14)

Con la compuerta en verde, la fase se detuvo (sección 6 del prompt) porque T47 no se cumplía por completo (SI-07, clase B) y había decisiones pendientes. Respuestas:

1. **SI-07:** endurecer el trigger. ADR 0016 y commit `spec: SI-07 …` (`5eb9695`): guarda de J18, mecanismo de I25, `TR_PrintJob_StateMachine` y sección 11; artefactos regenerados y `verify_package.py` en verde. TX-D aplica la misma regla (`e012c23`). Dos pruebas negativas de T47 que fallan con el DDL anterior.
2. **SI-01:** edición aprobada; commit `spec: SI-01 …` (`82b1e5b`).
3. **SI-04, SI-05, SI-08 y SI-09:** se mantienen los interinos de falla cerrada y se sigue con la Fase 2.

`build_examples.py` regenera `result-eventid-not-deterministic.json` con un UUID aleatorio en cada corrida; ese cambio sin contenido se descartó (`git checkout`) antes de recalcular los checksums.

Compuerta de cierre, 2026-10-14 21:48 (`powershell -File build/ci.ps1`), con la especificación 1.1.3:

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   128, Omitido:     0, Total:   128, Duración: 18 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    80, Omitido:     0, Total:    80, Duración: 1 m 29 s - CartonPrinting.IntegrationTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Las correcciones posteriores a la segunda revisión fueron el cambio de especificación decidido por la persona y sus pruebas; ninguna corrigió un hallazgo ALTA, así que no se pidió una tercera revisión. La base de datos local de Docker Compose tiene aplicado el DDL anterior: el registro de migraciones (`migrations.AppliedScript`) rechaza el nuevo `sqlserver_schema.sql` y hay que recrearla (`docker compose down -v`). Las pruebas usan bases nuevas y no se ven afectadas.

### Lo que no se verificó

- T01 mide la transmisión con un arnés de sender y una impresora en memoria; el sender real, el lock de sistema operativo y el simulador TCP llegan en la Fase 4, donde T01 se repite.
- Los servicios que usan TX-D, TX-E, TX-S, TX-M, TX-X y TX-R0 (escáner, ERP, barrido, Operations API, recuperación) no existen todavía: aquí solo se ejecutaron las transacciones, desde las pruebas.
- El consumidor de T14 es de prueba; los consumidores reales de `PrintJobResult` están fuera del MVP.
- Las trazas se comprobaron con un `ActivityListener` en proceso, no contra un colector OTLP.
- La compuerta corrió en Windows con Docker Desktop; no en Linux ni en GitHub Actions.

### Riesgos

- **Evidencia registrada y no aplicada.** TX-D registra la observación en su propia transacción y la aplica en otra. Si el proceso muere entre las dos, la observación queda sin aplicar. La Fase 4 la reaplica al arrancar; con el ADR 0016, una lectura anterior al estado vigente queda sin efecto en el trigger y en TX-D.
- Las pruebas de concurrencia dependen del planificador de SQL Server; T02 se repite en cinco rondas y con un bloqueo retenido explícito.
- SI-04, SI-05, SI-08 y SI-09 quedan con su interino de falla cerrada, por decisión de la persona (2026-10-14).

## Fase 2 · Ingesta OPC simulada (T11, T13, T24, T25, T26, T30, T35, T45)

### Plan

- **Entregables:** abstracción de la fuente de ciclos (`ICycleSource`) con el cliente OPC UA real (OPC Foundation UA .NET Standard) y el PLC simulado como servidor OPC UA en proceso (registro latched, `PlcEpoch`, bit Overflow, Republish, FIFO para T11, `LabelingReady`); decodificación (solo StatusCode Good); clasificación de la sección 9.2 y del diagrama 08 (duplicados, colisiones, tardíos, reinicio sospechado, disminución ambigua, huecos, épocas, rebote); `SequenceAnomaly`, `SequenceGap`, `PlcEpochHistory`; reglas de plazo de `GATEWAY_RECEIPT`; ingesta alojada en el EdgeHost.
- **Pruebas que la cierran:** T11, T13, T24, T25, T26, T30, T35 y T45, con el cliente real contra el servidor simulado donde el transporte importa.
- **Riesgos previstos:** que el SDK no entregue lo perdido en tránsito tras reconectar; que el orden entre la primera notificación y las lecturas de recuperación vuelva no determinista T11; el costo de T24 a escala completa.

### Lo hecho

- **Simuladores** (`CartonPrinting.Simulators`): `PlcSimulatorServer` y `PlcNodeManager` (registro latched como String JSON con eco de cierre `closingCycleId`, FIFO `PendingCycles`, `LabelingReady` escribible, `PrintWindowCycleId`), `TcpProxy` para congelar o cortar el enlace.
- **Ingesta** (`CartonPrinting.OpcIngest`): `OpcUaCycleSource` (clasificación del transporte por mensaje; primera notificación `READ`; `REPUBLISH` tras perder el keep-alive y Republish explícito de los números de secuencia disponibles; lectura del registro actual y de la FIFO al reconectar), `CycleDecoder`, `StationIngestService` (un consumidor por estación), `LabelingReadyHeartbeat`, `IngestHostedService` (sección `Ingest` del EdgeHost).
- **Clasificación** (`CartonPrinting.Orchestration`): `SequenceClassifier` (estado en memoria reconstruido desde SQL, caché de huellas), `RetainedRecords` (registro retenido y reingreso `ANOMALY_REINGEST`).
- **Datos**: `SequenceStore` (épocas con su marca alta, retención `EPOCH_UNEXPECTED` con bloqueo en una transacción, registros retenidos, huecos abiertos).

### Decisiones

1. **TX-A sigue siendo la autoridad.** La marca alta y la caché de huellas son de la ingesta (barrera 2): evitan ir a SQL con repeticiones, pero la unicidad la decide la inserción primero. La caché solo guarda claves que este proceso registró o confirmó como duplicado; una clave con otra huella siempre va a TX-A.
2. **Épocas.** La primera época de una estación se acepta como `INITIAL`; la actual + 1 con `AUTO_INCREMENT_BY_ONE`; lo demás es `EPOCH_UNEXPECTED`. La aceptación es una transacción propia antes de TX-A (la detección tiene FK a la época), con `FirstCycleId` = primer ciclo recibido y un `AcceptedAt` nunca anterior al de la época actual (el orden entre épocas no retrocede con el reloj de pared). En la primera época de la estación el primer registro no abre hueco; en una época nueva, mientras OT no fije el valor de arranque del contador, los ciclos anteriores al primer registro (desde 1) son un hueco (SI-12, interino de falla cerrada). Antes de retener un registro de una época desconocida, la ingesta relee las épocas una vez: una época confirmada por una persona llega sin reiniciar.
3. **`EPOCH_UNEXPECTED`.** Cada registro de una época no aceptada queda como su propia anomalía con el registro completo, y la estación se bloquea una vez. El reingreso exige la época ya aceptada (`CONFIRM_EPOCH`), registra el mismo registro latched con `ANOMALY_REINGEST` y conserva `ReceivedAt`, y después resuelve la anomalía con `RESOLVE_ANOMALY`. Repetirlo da un duplicado auténtico.
4. **Tardío o reinicio.** Con `DetectionTimestamp` en el registro y en la marca alta, anterior es tardío y posterior o igual es `RESET_SUSPECTED` (la igualdad se trata como reinicio: falla cerrada). Sin alguno de los dos timestamps es `AMBIGUOUS_DECREASE`: `WARNING` y, si la clave nueva anterior también fue ambigua, `BLOCKING` con la estación bloqueada.
5. **Rebote.** El avance mínimo plausible desde la marca alta es `(CycleId − M) × MinCycleIntervalMs`; sin `DetectionTimestamp` (o sin `MinCycleIntervalMs`) la regla no se aplica, como pide T26. Un registro con rebote que además salta ciclos abre igual su hueco (diagrama 08).
6. **Registro incoherente.** El formato real del registro latched es una decisión de OT (OT-04). El simulador cierra el registro con un eco del `CycleId`; si el eco no coincide, el registro es incoherente. Queda `BLOCKED` con `INCOHERENT_SNAPSHOT` y pasa por la clasificación con dos límites (SI-13, clase B, interino): no acepta épocas y su hora no cuenta. Su `CycleId` mueve la marca alta y abre huecos, igual que su fila al recargar desde SQL; la hora de una detección `BLOCKED` con `INCOHERENT_SNAPSHOT` tampoco cuenta al recargar. Texto anterior, reemplazado por la segunda revisión: "no alimenta el estado de secuencia (no acepta épocas, no mueve la marca alta, no abre huecos) y queda `BLOCKED` con `INCOHERENT_SNAPSHOT` si su época ya está aceptada, o retenido con la estación bloqueada si no (falla cerrada). Sin eco, la coherencia no se puede comprobar (SI-10).
7. **Decodificación.** Solo StatusCode Good produce un registro; vacío, mal formado o sin identidad se descarta con `opc_records_rejected_total{cause,transport}` y nunca llega a TX-A.
8. **`LabelingReady`** = estación habilitada, OPC conectado con la recuperación terminada y el canal de la ingesta sin saturar (o sin notificaciones descartadas desde el latido anterior); baja si no se puede leer SQL y al detener el servicio. La recuperación tras reconectar termina cuando terminan sus lecturas, no cuando pasa una caja.
9. **G03 en la ingesta.** Mientras no existe el barrido completo de la Fase 3, el servicio alojado cierra los huecos vencidos cada `GapSweepIntervalMs` y mide `cycles_unobserved_total` y `sequence_gaps_open`.
10. **El consumidor no salta registros.** Si TX-A falla con un error que no es de identidad ni 1205, el servicio se detiene: la disponibilidad cede ante la consistencia.
11. **Pruebas de T45 por transporte.** La clasificación del transporte la prueba `OpcClientTransportTests` contra el servidor simulado (primera notificación, Republish tras congelar el enlace, Overflow, reinicio con lectura y FIFO); la regla de plazo por transporte se prueba con registros de cada transporte en la ingesta, más un recorrido completo por OPC (primera notificación tras reiniciar el servidor).
12. **Precondición de T24 por TX-A.** Los 5000 ciclos registrados entran por TX-A en paralelo, cada uno con su job (la versión anterior, con `SqlBulkCopy` y detecciones `EXPIRED` sin job, se reemplazó por B8); la inyección (5200 entregas) pasa completa por la ingesta y TX-A.

13. **Transporte tras reconectar.** Una suscripción recreada (reinicio del PLC, sesión nueva o suscripción vencida) vuelve a numerar sus mensajes: el cliente lleva el número de secuencia por suscripción y la primera notificación de la nueva es `READ`. Durante la recuperación, un cambio que el servidor registró antes de la reconexión (por su `ServerTimestamp` frente a la hora del servidor al reconectar) es `REPUBLISH` aunque viaje en un mensaje nuevo; uno posterior es `SUBSCRIPTION`. La suscripción se reconoce por su instancia y por su Id (segunda revisión, MEDIA-1). La recuperación hace, en pasos independientes: hora del servidor, lectura del registro actual, Republish de lo perdido en tránsito y FIFO. El Republish usa la suscripción viva de la sesión y espera una respuesta de publicación posterior a la reconexión (MEDIA-2). Terminada la recuperación, la marca de la última reconexión se conserva: un cambio anterior a ella que llega tarde sigue siendo `REPUBLISH` (BAJA-1).
14. **Dos instancias.** Un duplicado auténtico también mueve la marca alta (la detección existe), como al reconstruir el estado desde SQL.
15. **Arranque sin PLC.** El servicio alojado reintenta la conexión con espera exponencial en lugar de detener el EdgeHost.

### Comandos y resultados

Compuerta completa, 2026-10-14 22:00 (`powershell -File build/ci.ps1`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   155, Omitido:     0, Total:   155, Duración: 22 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    80, Omitido:     0, Total:    80, Duración: 1 m 38 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    20, Omitido:     0, Total:    20, Duración: 2 m 39 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=2 destino(s), 2 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=2 destino(s), 2 ejecutadas, T24=2 destino(s), 2 ejecutadas, T25=4 destino(s), 7 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=3 destino(s), 8 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Durante el desarrollo:

- `GatewayReceiptTests` falló al esperar `CK_CartonDetection_GatewayLive` también para el ciclo en vivo que abre un hueco: la CHECK no lo distingue (la regla del hueco es de código en TX-A, SI-05) y lo rechaza la máquina de estados (51011). Se corrigió la prueba.
- `FifoReconnectTests` falló con dos cajas "perdidas" cuyo timestamp tenía menos de 1 s: datos de prueba irreales (el reloj del EdgeHost no avanzaba durante la desconexión). La prueba ahora avanza el reloj lo que dura la desconexión.
- `TestRevisions.cs` en la carpeta compartida rompía la compilación de `LoadTests`; pasó al proyecto de pruebas unitarias.

### Pruebas de la fase

- **T11, T13, T24, T25, T26, T30, T35 y T45: PASA** (detalle en `docs/TEST_STATUS.md`).

### Revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `3ab9802` (2026-10-14). Ejecutó la compuerta (155, 80 y 20 pruebas en verde) y diez experimentos propios con el cliente OPC UA real contra el PLC simulado y SQL Server real. 2 ALTA, 4 MEDIA y 9 BAJA:

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| A1: tras reiniciar el PLC, los ciclos en vivo quedaban `REPUBLISH` hasta que la secuencia nueva superaba a la vieja | Alta | Número de secuencia por suscripción y primera notificación `READ` al recrearla (decisión 13). Prueba: ciclos en vivo después de `RestartServerAsync` son `SUBSCRIPTION` |
| A2: tras una reconexión, `Connected` (y `LabelingReady`) seguía en false hasta que pasara una caja | Alta | La recuperación termina al terminar sus pasos; marca por `ServerTimestamp` (decisión 13). Pruebas: recuperación sin cajas tras reiniciar el PLC y tras congelar el enlace; el primer ciclo posterior es `SUBSCRIPTION` |
| M1: una época confirmada no llegaba a la ingesta en ejecución; `ReloadAsync` fuera de la serialización | Media | Recarga bajo el semáforo del consumidor y relectura antes de retener (decisión 2); el servicio alojado expone la ingesta. Prueba `A_confirmed_epoch_reaches_the_running_ingest` |
| M2: con dos EdgeHost, huecos espurios y cajas en vivo vencidas | Media | Decisión 14. Prueba `Two_instances_on_one_station_do_not_open_spurious_gaps` |
| M3: el primer registro de una época nueva nunca abría hueco (interpretación no registrada) | Media | SI-12 (clase B) con interino de falla cerrada; prueba unitaria |
| M4: el canal lleno no bajaba `LabelingReady` | Media | Decisión 8. Prueba `A_saturated_channel_lowers_labeling_ready` |
| B1: un registro incoherente alimentaba el estado de secuencia | Baja | Decisión 6. Prueba `An_incoherent_record_does_not_feed_the_sequence_state` |
| B2: desborde de `ulong` en la marca alta | Baja | Comparaciones sin desborde; prueba unitaria |
| B3: un fallo del Republish saltaba la lectura y la FIFO; la lectura se demoraba 3 s | Baja | Pasos independientes; la lectura va primero |
| B4: `AcceptedAt` del reloj de pared y `UQ_PlcEpochHistory_Order` sin reconocer | Baja | Decisión 2; la violación de `UQ_PlcEpochHistory_Order` recarga las épocas |
| B5: sin PLC al arrancar, el servicio alojado detenía el EdgeHost | Baja | Decisión 15 |
| B6: `plc_epoch_changes_total` contaba `INITIAL` y no `OPERATOR_CONFIRMED` | Baja | Corregido (la confirmada se cuenta al recargar) |
| B7: el monitored item se asignaba después de crearlo | Baja | Se asigna antes |
| B8: pruebas más débiles que su texto (reingreso duplicado no ejercitado, precondición de T24 escrita a mano y sin jobs, Traits de T24 y T01 en pruebas que no lo son, T11 reiniciando el PLC, RESET_SUSPECTED y AMBIGUOUS_DECREASE BLOCKING sin prueba contra SQL) | Baja | T25 reingresa de verdad (duplicado) y prueba el rechazo posterior; T24 registra su precondición por TX-A, con 5000 jobs que no se reutilizan, y comprueba que la estación la bloqueó la primera colisión; Traits corregidos; T11 desconecta el gateway (enlace congelado más que la sesión); prueba de RESET_SUSPECTED y AMBIGUOUS_DECREASE BLOCKING contra SQL. En T45, el `READ` del recorrido por OPC puede salir de la primera notificación o de la lectura de recuperación: las dos son `READ` por diseño |
| B9: clasificación de SI-10 y SI-11; afirmaciones de T24 y T11 sin verificar; lista de lo no verificado incompleta | Baja | SI-10 es clase B (regla 6 de `AI_CONTEXT`); SI-11 es clase A y su edición queda pendiente de aprobación; las afirmaciones ahora se verifican; lista ampliada |

Compuerta completa después de las correcciones, 2026-10-14 22:48 (`powershell -File build/ci.ps1`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   158, Omitido:     0, Total:   158, Duración: 19 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    80, Omitido:     0, Total:    80, Duración: 1 m 57 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    27, Omitido:     0, Total:    27, Duración: 3 m 20 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=3 destino(s), 3 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=6 destino(s), 8 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 11 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Segunda revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `51bcb88` (2026-10-14). Ejecutó la compuerta (158, 80 y 27 pruebas en verde), comprobó que las pruebas nuevas de A1 y A2 fallan sin su corrección e hizo experimentos propios con el cliente OPC UA real: 32 rondas de corte, reinicios del PLC y cuatro escenarios contra SQL Server real. 0 ALTA, 3 MEDIA y 8 BAJA:

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| MEDIA-1: el reinicio de la numeración dependía de que el servidor cambiara el `SubscriptionId` | Media | También se reinicia cuando cambia la instancia de `Subscription`. El simulador elige Ids al azar, así que ninguna prueba cubre un servidor que repita Ids (ver lo no verificado) |
| MEDIA-2: tras el primer reinicio del PLC, la recuperación usaba la suscripción muerta; con una lista de disponibles vieja, el Republish no esperaba | Media | La suscripción y el monitored item vivos se toman de la sesión al reconectar y en cada recuperación. La lista de disponibles se lee después de una respuesta de publicación posterior a la reconexión (`LastNotificationTime`). Prueba nueva: tras un reinicio, lo publicado durante un corte llega como `REPUBLISH` en esa recuperación; falla sin la corrección |
| MEDIA-3: tras recargar, el registro incoherente sí era la marca alta (en memoria no), y ocultaba huecos | Media | Una sola regla en los dos caminos (decisión 6, SI-13). Pruebas: `An_incoherent_record_feeds_the_mark_the_same_in_memory_and_after_reload` contra SQL y cuatro unitarias en `IncoherentSequenceTests` |
| BAJA-1: la marca de hora del servidor se descartaba al terminar la recuperación; dos recuperaciones superpuestas se pisaban | Baja | Se conserva la marca de la última reconexión: un cambio anterior a ella es `REPUBLISH` aunque llegue después. Contador de generación por recuperación |
| BAJA-2: un incoherente de una época recién confirmada volvía a bloquear la estación | Baja | El incoherente pasa por la misma relectura previa a retener. Prueba `An_incoherent_record_of_a_just_confirmed_epoch_does_not_block_the_station_again` |
| BAJA-3: la decisión 6 era un cambio de comportamiento no registrado | Baja | SI-13 (clase B) |
| BAJA-4: la prueba de M4 no ejercitaba el canal; la saturación no se evaluaba con la estación bloqueada o desconectada | Baja | `ChannelPressure` extraída y probada con un canal acotado real; se evalúa en cada latido |
| BAJA-5: un arranque fallido dejaba la sesión abierta y enganchada al keep-alive | Baja | Se desengancha y se cierra en `StartAsync` |
| BAJA-6: un reingreso concurrente informaba éxito y dejaba una `OperatorAction` sin efecto | Baja | `ResolveAnomalyAsync` resuelve primero e inserta la acción solo si resolvió; el reingreso falla si otro ya la resolvió. Prueba `Resolving_an_already_resolved_anomaly_leaves_no_operator_action` |
| BAJA-7: documentación desfasada (decisión 12, fila T24, B6 sin prueba) | Baja | Decisión 12 y fila T24 corregidas; la métrica `OPERATOR_CONFIRMED` se comprueba en `A_confirmed_epoch_reaches_the_running_ingest`; límite de reinicio en lo no verificado |
| BAJA-8: el interino de SI-12 abre huecos enormes si la época cambia sin reiniciar el contador | Baja | Agregado a SI-12 con la alternativa `FirstCycleId`, para que la persona decida |

Compuerta completa después de las correcciones de la segunda revisión, 2026-10-14 23:50 (`powershell -File build/ci.ps1`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   162, Omitido:     0, Total:   162, Duración: 21 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    80, Omitido:     0, Total:    80, Duración: 2 m 14 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    30, Omitido:     0, Total:    30, Duración: 3 m 35 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

La prueba nueva de MEDIA-2 se ejecutó también contra `OpcUaCycleSource.cs` de `51bcb88` (sin la corrección): falla (1 de 1). Con la corrección pasó 3 de 3 veces seguidas, junto con la prueba de corte sin reinicio.

Sin hallazgos ALTA en la segunda revisión: no se pide una tercera.

### Decisiones de la persona (2026-10-14)

- **SI-11 (clase A):** edición aprobada y aplicada en `8798b85` (especificación 1.1.4). Después: `py spec/tools/generate_artifacts.py` (regeneró `tests/MATRIZ_PRUEBAS.md`), `py spec/tools/update_checksums.py` (150 archivos) y `py spec/tools/verify_package.py` (`Resultado: 15 grupos OK, 0 errores`); `generate_artifacts.py --check`: artefactos al día.
- **SI-10, SI-12 y SI-13 (clase B):** se mantienen los interinos de falla cerrada; quedan abiertos para OT-04.

### Lo que no se verificó

- Ningún PLC real: el servidor OPC UA es el simulador. El formato del registro latched, el eco de cierre y la FIFO son del simulador (OT-04, OT-14).
- Un servidor OPC UA que repita `SubscriptionId` al reiniciar (MEDIA-1): el cambio de instancia lo cubre en el cliente, pero el simulador elige Ids al azar y ninguna prueba lo ejercita.
- `plc_epoch_changes_total{mode=OPERATOR_CONFIRMED}` se cuenta al aparecer la época en una recarga del proceso en ejecución; si el EdgeHost reinicia entre la confirmación y la recarga, esa época no se cuenta.
- La clasificación del transporte tras reconectar se probó con el servidor de OPC Foundation (reinicio, enlace congelado, sesión vencida); otros servidores pueden numerar o sellar distinto.
- Dos instancias se probaron con dos consumidores en un proceso, no con dos EdgeHost reales (la Fase 5 agrega la propiedad de la impresora).
- El flujo `REQUIRE_CONFIRMATION` de punta a punta en el EdgeHost necesita la Operations API (Fase 5); aquí se probó con la ingesta y las acciones manuales de TX-M.
- Seguridad OPC UA: las pruebas usan `SecurityMode = None` contra el simulador; `SignAndEncrypt` con certificados de planta no se probó.
- Un solo `IngestHostedService` por proceso (una estación); varias estaciones por EdgeHost no se probaron.
- Los intervalos revisados por el servidor y el bit Overflow se observaron solo con el servidor de OPC Foundation.
- `LabelingReady` se verificó como valor escrito en el simulador; qué hace el PLC cuando cae es una decisión de OT.

### Riesgos

- La marca alta se reconstruye con el mayor `CycleId` registrado de cada época: un registro retenido sin detección no la mueve, a propósito.
- T24 a escala completa tarda unos 2,5 minutos (5200 entregas reales contra SQL).
- La caché de huellas es de 4096 claves: una repetición más vieja va a TX-A, que la resuelve igual.

## Fase 3 · ERP y plazo (T03, T04, T17, T18, T21, T33, T37, T46)

### Plan

- **Entregables:** sincronización ERP asíncrona con timeout y circuit breaker y un ERP simulado con latencia y caídas; `WAITING_ERP` y TX-E (D06, D08); plazo y margen efectivos (congelado frente a revisión activa) con `DeviceLeadTimeMs`; detector de saltos del reloj (`CLOCK_JUMP`) y desfase del PLC (`CLOCK_SKEW`); barrido TX-S por estación; configuración versionada activada por dos personas.
- **Pruebas que la cierran:** T03, T04, T17, T18, T21, T33, T37 y T46.
- **Riesgos previstos:** pruebas sensibles a la latencia real (ERP lento); simular un ajuste de NTP sin un reloj controlable.

### Lo hecho

- **Simulador** (`CartonPrinting.Simulators/Erp`): ERP HTTP en proceso (solo 127.0.0.1) con retraso, 503 y conexión sin respuesta.
- **ERP** (`CartonPrinting.ErpSync`): `HttpErpClient` (valida el snapshot: campos, vigencia, GTIN de 14 dígitos, orden pedida; nunca usa uno no validado, I10), `ErpSnapshotSync` (sondeo con señal de la ingesta por `System.Threading.Channels`, Polly con timeout y circuit breaker, caché, `erp_cache_age_seconds`).
- **Orquestación**: `ErpArrivalService` (plazo y margen efectivos, serial y payload antes de TX-E; sin serial si el plazo ya venció), `StationSweepService` (TX-S por estación con la comprobación del reloj antes de evaluar, `config_revision_active`), `IErpDemand`.
- **Reloj**: `ClockGuard` (divergencia entre `UtcNow` y el monotónico), compartido por el barrido y el claim de la estación; `CLOCK_SKEW` en la ingesta con `PLC_TIMESTAMP`.
- **EdgeHost**: servicios por estación (ERP y barrido) y señal de la ingesta al ERP.

### Decisiones

1. **Contrato con el ERP.** El del simulador (GET JSON por orden); el real es una decisión de integración y queda detrás de `IErpClient`. Un 404 es "sin snapshot"; un error, un timeout o un snapshot inválido son "ERP no disponible".
2. **Polly**: circuit breaker por fuera y timeout por dentro (un timeout cuenta como falla). Los tiempos de E/S usan el reloj real; los instantes del dominio, el del EdgeHost.
3. **Snapshot tardío.** `ErpArrivalService` no reserva serial si el plazo efectivo ya venció; TX-E no crea el job y deja la detección en `WAITING_ERP`; el barrido aplica D07 (decisión 11 de la Fase 1).
4. **`erp_cache_age_seconds`** = segundos desde la última respuesta del ERP (o desde el arranque si nunca respondió).
5. **Orden del barrido**: reloj primero (con un salto no evalúa nada); después J03 y J14 (plazo y orden) antes de J13; D07; ventanas de evidencia; autorizaciones; G03. Los plazos son los efectivos, calculados en el EdgeHost con la revisión activa, y cada transacción exige que esa revisión siga activa (SI-14). J13 exige en SQL vigentes el plazo efectivo y el congelado. Texto anterior, que la revisión encontró incorrecto: "J13, que además exige plazo vigente en SQL" (era el congelado).
6. **`CLOCK_JUMP`**: un `ClockGuard` por estación, compartido por el barrido y el claim; la tolerancia es la de la revisión activa; al detectar un salto se bloquea la estación y el claim se rechaza (`ClockJump`) sin evaluar el plazo. El salto queda retenido hasta que el bloqueo se confirma en SQL. Si `BlockAsync` falla, toda comprobación posterior lo vuelve a informar y reintenta el bloqueo; un claim concurrente tampoco evalúa su plazo mientras el bloqueo no es visible. El claim y el barrido usan como `@EdgeNowUtc` el instante que la guarda verificó. La guarda es obligatoria en `ClaimService`. Un salto menor que la tolerancia no se detecta (límite declarado en la sección 10.2). Segunda revisión y SI-16 (decisión de la persona): la transacción que registra el salto (`TxsSweep.ClockJumpAsync`) bloquea la estación y vence lo pendiente (J03 y J14 con `DEADLINE_PASSED`, D08 con `STATION_DISABLED`). El salto se da por confirmado solo si el bloqueo vigente es `CLOCK_JUMP`; con otro bloqueo vigente se retiene y el barrido no evalúa plazos hasta poder registrarlo.
7. **`CLOCK_SKEW`**: con `PLC_TIMESTAMP`, un `DetectionTimestamp` posterior a la recepción es un límite inferior del adelanto del reloj del PLC; si supera `ClockSkewToleranceMs`, la estación se bloquea antes de registrar el ciclo y el ciclo queda `BLOCKED` con `STATION_DISABLED`. Su `DeadlineAt` se calcula igual (el contrato exige `deadlineAt` posterior a `detectionTimestamp`) pero no decide nada: no se crea job. Un reloj del PLC atrasado acorta los plazos (es restrictivo) y no se distingue de la latencia: no se detecta, y las cajas se publican como `LATE_ARRIVAL`. Ambos límites son SI-15 (clase B).
8. **El escenario de pruebas admite el reloj real** (`TimeProvider.System`) para las pruebas con latencia real (T03); `ManualTimeProvider` separa el reloj de pared del monotónico para simular un ajuste de NTP (T37).
9. **Snapshot ERP: la fila de la caché, no la respuesta (I10).** Después del upsert se relee la fila de `(WorkOrderId, Revision)`. Si su contenido (producto, lote, GTIN, vigencia) no coincide con la respuesta, el ERP reutilizó la revisión: el snapshot no está disponible y se mide `erp_requests_total{outcome=revision_conflict}`. Si coincide, el job se arma con la fila. TX-E además exige en SQL que la fila tenga el producto, el lote y el GTIN de la etiqueta. Se compara el contenido y no `SourceHash`, que depende de los bytes del cuerpo HTTP.
10. **Validación del snapshot.** La vigencia se trunca a milisegundos antes de validarla. El GTIN debe tener 14 dígitos ASCII, y revisión, producto y lote no pueden pasar el largo de su columna (un parámetro `NVarChar(n)` truncaría en silencio). Cada orden va en su propio `try`, y las órdenes se procesan por plazo.
11. **Revisión activa en la transacción.** El claim (TX-B) exige `sc.ActiveConfigRevisionId` igual a la revisión con que calculó el plazo (parámetro obligatorio); si no, el rechazo es `RevisionChanged` y el job sigue `READY`; el sender de la Fase 4 lo vuelve a reclamar en su vuelta siguiente. TX-E no actúa si cambió la revisión; lee la estación sin `UPDLOCK` (con RCSI equivale a ejecutarse antes de la activación concurrente, y el `UPDLOCK` formaba un ciclo de bloqueos con TX-A). El barrido exige la revisión en cada transacción.
12. **Métricas de TX-E.** Solo se mide una transición (job creado o resultado publicado). Una TX-E sin efecto devuelve null y no cuenta en `SyncOnceAsync`.

### Comandos y resultados

Compuerta completa, 2026-10-14 23:10 (`powershell -File build/ci.ps1 --fase 3`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   159, Omitido:     0, Total:   159, Duración: 22 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    83, Omitido:     0, Total:    83, Duración: 2 m 22 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    33, Omitido:     0, Total:    33, Duración: 3 m 19 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=1 destino(s), 1 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=3 destino(s), 3 ejecutadas, T46=1 destino(s), 1 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Durante el desarrollo:

- Al integrar las correcciones de la Fase 2, T03 empezó a fallar a veces: con el presupuesto de tránsito por defecto (950 ms útiles), la latencia de arranque en frío de la primera consulta HTTP (Kestrel, `HttpClient`, JIT) se comía el plazo. Una pasada directa sí creaba el job. La prueba ahora calienta el cliente y usa `TransitBudgetMs` = 3000 (la precondición del catálogo solo exige que sea mayor que el retraso del ERP).
- T26 (Fase 2) falló en esta rama porque un dato de prueba tenía un `DetectionTimestamp` en el futuro, que con `CLOCK_SKEW` bloquea la estación como debe. Se corrigió el dato.

### Pruebas de la fase

- **T03, T04, T17, T18, T21, T33, T37 y T46: PASA** (detalle en `docs/TEST_STATUS.md`).

### Revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `a0eaebb` (2026-10-14). Ejecutó la compuerta (159, 83 y 33 pruebas en verde) y seis experimentos contra SQL Server real (EXP1, EXP2 y EXP4 a EXP7). También hizo dos mutaciones: J13 sin guarda de plazo, y una llamada síncrona al ERP desde la ingesta. Con ambas la suite seguía en verde. Resultado: 1 ALTA, 4 MEDIA y 7 BAJA.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| 1: `ClockGuard` olvidaba el salto antes de que el bloqueo quedara guardado; si `BlockAsync` fallaba, los claims siguientes se evaluaban con el reloj saltado | Alta | El salto queda retenido hasta que el bloqueo se confirma, y el claim usa el instante verificado (decisión 6). Prueba `A_clock_jump_whose_block_failed_is_not_forgotten` (fila de la estación retenida y claim cancelado): falla si la confirmación va antes del bloqueo. Más una prueba unitaria |
| 2: el barrido usaba el `DeadlineAt` congelado, y J13 devolvía a `READY` un job con el plazo efectivo vencido | Media | Plazo efectivo por fila en J03, J14, J13 y D07; conflicto con la referencia registrado como SI-14 (decisión 5). Prueba `The_sweep_expires_by_the_effective_deadline_after_a_revision_change` |
| 3: T18 pasaba sin la guarda de J13 | Media | Prueba `Release_from_retry_wait_has_its_own_deadline_guard`, que llama a J13 sin pasar por la expiración: falla con las guardas de plazo reemplazadas por `1 = 1` |
| 4: T03 no comprobaba que la ingesta no espere al ERP | Media | El ERP retiene las peticiones mientras se procesa el ciclo: la ingesta debe devolver sin respuesta del ERP. Falla con la llamada síncrona de la revisión. Cobertura parcial declarada en `TEST_STATUS` |
| 5: el job copiaba la respuesta HTTP y no la fila validada de la caché (I10) | Media | Decisión 9. Pruebas `A_reused_erp_revision_with_other_content_is_not_used_and_the_job_matches_the_cache_row` y `Tx_e_does_not_create_a_job_whose_content_differs_from_the_cache_row` |
| 6: validación antes de truncar, GTIN sin dígitos aceptado, una orden con error detenía la pasada, órdenes sin orden | Baja | Decisión 10. Pruebas `ErpClientValidationTests` (cinco casos inválidos y uno válido) y `An_order_that_fails_does_not_stop_the_others` |
| 7: una TX-E sin efecto se contaba como transición | Baja | Decisión 12. Prueba `A_tx_e_without_effect_is_not_counted` |
| 8: plazo efectivo calculado con una revisión leída fuera de la transacción | Baja | Decisión 11. Prueba `Tx_e_and_tx_b_require_the_revision_they_were_computed_with` |
| 9: límites de `CLOCK_SKEW` sin declarar | Baja | SI-15 (clase B) y decisión 7 |
| 10: el claim con `ClockGuard` solo existía en las pruebas; `TimeProvider.System` en el servicio de la estación | Baja | Guarda obligatoria en `ClaimService`; el servicio usa el `TimeProvider` inyectado. La composición del EdgeHost no tiene prueba (lo no verificado). El sender del EdgeHost, que compone el claim, es de la Fase 4 |
| 11: T33 no distinguía el margen efectivo | Baja | El claim se prueba a 120 ms del plazo efectivo |
| 12: afirmaciones excesivas ("cero bytes", decisión 5) | Baja | `TEST_STATUS` de T04, T17 y T18 dice de dónde se deduce; decisión 5 corregida |

Compuerta completa después de las correcciones, 2026-10-15 00:29 (`powershell -File build/ci.ps1 --fase 3`, rama con `main` integrado: Fase 2 cerrada y especificación 1.1.4):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   164, Omitido:     0, Total:   164, Duración: 20 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    83, Omitido:     0, Total:    83, Duración: 2 m 17 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    50, Omitido:     0, Total:    50, Duración: 3 m 34 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=3 destino(s), 3 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=4 destino(s), 5 ejecutadas, T46=1 destino(s), 1 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Mutaciones ejecutadas, cada una revertida después:

- **Confirmar el salto antes del bloqueo** (el comportamiento anterior): `A_clock_jump_whose_block_failed_is_not_forgotten` falla.
- **Guardas de plazo de J13 reemplazadas por `1 = 1`**: `Release_from_retry_wait_has_its_own_deadline_guard` falla.
- **`WorkOrderNeeded` consulta al ERP de forma síncrona** (la mutación de la revisión): T03 falla.

En la primera compuerta falló `Tx_e_d08_with_recirculated_carton`. La prueba activaba la revisión 8 y armaba el plan de TX-E con la 7. Con la decisión 11, TX-E no actúa con una revisión vieja, como debe. Se corrigió el dato: el plan usa la revisión activa.


### Segunda revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `b391083` (2026-10-15). Comprobó que los doce hallazgos de la primera revisión quedaron resueltos y que sus pruebas fallan sin la corrección: repitió M1, M8 y M10 y probó M6 y M9, nuevas. Ejecutó la compuerta: 164, 83 y 50. Dos errores de conexión a SQL Server por la carga se repitieron aislados en verde. Hizo dos experimentos (EXP1 y EXP2). Resultado: 0 ALTA, 1 MEDIA y 5 BAJA.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| MEDIA-1: tras confirmar el bloqueo, los plazos calculados antes del salto se evaluaban con el reloj nuevo; un claim se aceptó 5 min después del plazo real (EXP1). Con otro bloqueo vigente, el salto no quedaba en SQL (EXP2) | Media | SI-16 (clase B), decidido por la persona: lo pendiente vence al confirmar el salto. El salto solo se confirma con el bloqueo `CLOCK_JUMP` vigente. Pruebas `Pending_work_from_before_a_clock_jump_is_never_printed_after_the_unblock` y `A_clock_jump_on_a_station_blocked_for_another_reason_is_recorded_once_that_block_is_lifted` |
| BAJA-1: la revisión del claim era opcional y nadie probaba que `ClaimService` la pasara (M2 sobrevivía) | Baja | Parámetro obligatorio y no anulable en `ClaimRequest`. El reintento ante `RevisionChanged` es el del sender de la Fase 4 (decisión 11) |
| BAJA-2: las guardas de revisión del barrido y el `UPDLOCK` de TX-E sin prueba (M3 a M5 sobrevivían) | Baja | Prueba `Sweep_expirations_require_the_revision_they_were_computed_with`; el `UPDLOCK` se quitó (BAJA-3) |
| BAJA-3: el `UPDLOCK, HOLDLOCK` de TX-E formaba un ciclo de bloqueos con TX-A (deducido) | Baja | Quitado (decisión 11) |
| BAJA-4: el salto retenido vive solo en memoria (deducido) | Baja | Declarado en los riesgos |
| BAJA-5: TX-A y TX-E no consultan la guarda (deducido) | Baja | Declarado en `TEST_STATUS` (T37) y en lo no verificado |

Decisiones de la persona (2026-10-15):

- SI-16: lo pendiente vence al confirmar el salto.
- SI-14: se mantiene el plazo efectivo en el barrido.
- SI-15: se mantiene el interino, declarado.

Compuerta completa después de las correcciones de la segunda revisión, 2026-10-15 01:22 (`powershell -File build/ci.ps1 --fase 3`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   164, Omitido:     0, Total:   164, Duración: 20 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    83, Omitido:     0, Total:    83, Duración: 2 m 8 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    53, Omitido:     0, Total:    53, Duración: 3 m 11 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=4 destino(s), 4 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=6 destino(s), 7 ejecutadas, T46=1 destino(s), 1 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Sin hallazgos ALTA en la segunda revisión: no se pide una tercera.

### Lo que no se verificó

- Ningún ERP real: el contrato y el formato son del simulador.
- TX-A y TX-E no consultan el `ClockGuard`: hasta el próximo barrido o claim (200 ms) pueden calcular o evaluar un plazo con el reloj saltado (segunda revisión, BAJA-5).
- La composición de los servicios de la estación en el EdgeHost (`AddStationServices`, `StationServicesHostedService`) no tiene prueba: los servicios se prueban construidos a mano.
- `CLOCK_SKEW` no detecta un adelanto menor que la tolerancia más la latencia de subida ni un PLC atrasado (SI-15).
- El ajuste de NTP se simula con `ManualTimeProvider`; no se cambió el reloj del sistema.
- `CLOCK_SKEW` solo detecta un PLC adelantado.
- Los tiempos del barrido y de la sincronización ERP en el EdgeHost (200 ms y 100 ms) no se ajustaron con carga (Fase 7).

### Riesgos

- Una latencia del ERP cercana al plazo deja la detección en `WAITING_ERP` y la vence: es el comportamiento pedido, pero en la máquina de pruebas depende del ruido.
- El `ClockGuard` compara entre comprobaciones: si nada lo consulta durante un rato, un salto se detecta en la siguiente comprobación (el barrido corre cada 200 ms).
- El salto retenido vive en memoria: si el proceso muere entre detectarlo y registrarlo en SQL (por ejemplo, con SQL caído), la guarda nueva arranca de cero y el salto se olvida (segunda revisión, BAJA-4).

## Fase 4 · Print Agent (T05, T06, T07, T15, T22, T29, T31, T38, T39, T42, T44)

### Plan

- **Entregables:** impresora simulada por TCP (rechaza conexiones, corta a mitad de la escritura, imprime sin responder, acusa recibo, retiene en cola, confirma o no la cancelación según sus capacidades declaradas, cuenta bytes por conexión y por etiqueta); sender por impresora con una sola conexión, claim con `PrinterSendOrder`, segunda comprobación monotónica y TX-C con la regla de `NOT_SENT`; simulador de evidencia (impresora, aplicador y escáner correlacionados por ciclo) y TX-D con J10 a J12, J15 y J18; puntos de falla para matar el proceso en tres momentos (T07) y suspender el sender (T29).
- **Pruebas que la cierran:** T05, T06, T07, T15, T22, T29, T31, T38, T39, T42 y T44.
- **Riesgos previstos:** carreras entre la evidencia y TX-C con una impresora rápida; matar un proceso de verdad desde una prueba.

### Lo hecho

- **Simuladores**: `PrinterSimulator` (TCP en 127.0.0.1, protocolo de laboratorio: ZPL terminado en `^XZ` y "ACK serial" si el dispositivo lo declara) y `EvidenceSimulator` (PRINTED por job, APPLIED y VERIFIED por ciclo; lecturas inyectables).
- **Print Agent**: `IPrinterTransport`/`TcpPrinterTransport` (conectar no es escribir; escritura en dos mitades con punto de falla intermedio; espera del acuse), `PrinterSenderService` (orden de envío, claim, segunda comprobación con reloj monotónico, estación y compuerta, NOT_SENT o UNKNOWN según si la escritura se invocó, TX-C en una transacción nueva, reaplicación de la evidencia que llegó durante `SENDING`), `EvidenceService` (registro y aplicación; tiempos de la sección 19; reaplicación), `DeviceCancellation` (A07 y J12 solo con cancelación confirmada por el dispositivo), `SenderRecovery` (TX-R0 al arrancar el sender).
- **Fallas**: `tests/CartonPrinting.FaultHost`, un proceso que ejecuta el sender real y se detiene en el punto pedido para que la prueba lo mate.
- **EdgeHost**: sender de la impresora de línea (sección `Printer`) con lease y recuperación de arranque.

### Decisiones

1. **Protocolo con el dispositivo.** El del simulador; el real (y qué informa: acuse, fin de impresión, cancelación) es una decisión de integración (OT-01, OT-07) detrás de `IPrinterTransport` y `IDeviceJobControl`. Un acuse de recibo no prueba impresión (I05).
2. **NOT_SENT solo si la escritura nunca se invocó** (fallo al conectar, o la segunda comprobación rechaza); cualquier error desde que se invoca la escritura, incluido el corte del canal o la falta de acuse, es UNKNOWN (I02) y el canal se descarta.
3. **Segunda comprobación monotónica, la última antes de escribir** (revisión de la Fase 4, A1; segunda revisión, M-1, B-1 y B-2). Al reclamar se calcula lo que queda hasta el plazo efectivo menos el margen, medido desde una marca monotónica tomada antes del claim (conservador). Después, en este orden: se abre el canal si hace falta; se relee la estación (bloqueada: NOT_SENT); se consulta la compuerta física si la revisión la exige (sin compuerta configurada no se envía). Solo entonces, como última instrucción y sin I/O, se compara con el reloj monotónico (§10.2, paso 2). Entre el claim y la escritura nada se invocó: un rechazo, cualquier error (SQL, la compuerta) o un apagado terminan en NOT_SENT. La excepción es SQL Server caído: el NOT_SENT de TX-C tampoco se registra, el intento queda en `SENDING` y el sender se detiene (decisión 10). Con el plazo monotónico vencido es J08. Si no, J06: un fallo del canal consume intento (J07 al agotarlos); la estación, la compuerta, cualquier otro error y un apagado no lo consumen (SI-18, decidido por la persona). El catálogo no tiene una prueba de la compuerta; su implementación con `PrintWindowCycleId` queda para la integración con el PLC real.
4. **Evidencia durante SENDING, en orden** (revisión de la Fase 4, A2). Con una impresora rápida, la evidencia llega antes de TX-C, cuando todavía no hay transición que aplicar: se registra sin efecto. Cada observación nueva aplica, en orden de `ObservationId`, todo lo registrado para su job o su ciclo desde el último claim. Así, una lectura contradictoria anterior no se pierde detrás de una confirmación posterior. El sender reaplica después de TX-C, y el arranque del sender reaplica, después de TX-R0, la evidencia de sus jobs `TRANSMITTED` y `UNKNOWN`. Las alertas (`serial_mismatch_total`, `confirmed_corrected_total`) se cuentan también en la reaplicación. La medición de tiempos de esa evidencia se completa después de TX-C.
   - **Bajo concurrencia** (segunda revisión, A-1 y B-8). El orden del pase no basta cuando dos pases se cruzan con TX-C: un pase aplicaba la lectura con el job todavía en `SENDING` y el otro confirmaba después. La lógica de TX-D lo impide dentro de su transacción, bajo el `UPDLOCK` del job (no es un trigger). Antes de confirmar (J10, J15, X01 o X02), busca una lectura VERIFIED del ciclo con otro serial que cumpla tres condiciones: recibida desde el claim del intento, registrada antes que la observación que confirma y después de cualquier confirmación del job. La lectura cuenta con la misma coherencia que al aplicarla: sin intento o con cualquier intento del job (tercera revisión, MEDIA-1). Si existe, aplica esa contradicción (J11, J18 o X03) en lugar de confirmar. Además, el registro de una observación de ciclo toma un lock de aplicación exclusivo por ciclo (`sp_getapplock`), y la aplicación de cualquier evidencia del job lo toma compartido. Así, una lectura que todavía se está registrando, con un `ObservationId` menor, no queda detrás de una confirmación. Las dos mitades tienen prueba: sin el lock compartido falla la del registro en curso, y sin el exclusivo, la que retiene el compartido y espera a `RecordAsync` (tercera revisión, MEDIA-2). La aplicación toma el lock del ciclo antes del `UPDLOCK` del job, en el mismo orden que el registro: al revés, un registro con `AttemptId`, TX-C y la aplicación se interbloqueaban. Si `sp_getapplock` devuelve -3 (víctima de interbloqueo), la transacción se reintenta como un 1205 (tercera revisión, BAJA-1).
   - **Arranque** (segunda revisión, M-3; tercera, MEDIA-3). Después de TX-R0 se reaplica, en orden, la evidencia de la estación recibida en las 24 h anteriores cuyo job todavía puede cambiar con ella: un job `TRANSMITTED` o `UNKNOWN`, o un job `CONFIRMED` con una lectura VERIFIED de otro serial (J18). Es una sola consulta guiada por la evidencia reciente (cota de la clave primaria) que llega al job por `PrintJobId` o por `UQ_CartonDetection_Identity` y `UQ_PrintJob_DetectionLabel`: ya no recorre el histórico de jobs. Corre en paralelo con el sender, sin retrasar el primer envío; aplicar es idempotente y va bajo los locks de TX-D. `ReapplySinceAsync`, que no se usaba, se quitó.
5. **Cancelación (T31).** "No impreso" sin confirmación de cancelación no cambia el job; solo una cancelación confirmada por el dispositivo para ese job aplica A07 y J12.
6. **T07 con un proceso real.** El sender corre en `CartonPrinting.FaultHost`; la prueba lo mata con `Process.Kill` (TerminateProcess en Windows, equivalente a kill -9) en cada punto, comprueba con el login administrador que la sesión del host no tiene transacciones abiertas durante el I/O (I20) y ejecuta la recuperación de arranque. El UNKNOWN del primer punto (no se escribió nada) es falso: es el costo aceptado de no poder distinguirlo desde SQL.
7. **El sender del EdgeHost** toma el lease y ejecuta TX-R0 antes de enviar; el lock de sistema operativo, la renovación del lease y la parada ante su pérdida son de la Fase 5.
8. **Guarda del reloj en el claim.** Desde la corrección de la Fase 3 el `ClockGuard` es obligatorio: el sender del EdgeHost usa el de la estación (el mismo que el barrido), y el FaultHost y las pruebas, uno propio. Un claim rechazado (también `RevisionChanged`) deja el job `READY` y el sender lo reintenta en la vuelta siguiente.
9. **Métricas de evidencia en cualquier orden.** `printed_to_applied_ms` se registra cuando se conocen los dos instantes observados, lleguen PRINTED (por job) y APPLIED (por ciclo) en el orden que lleguen; lo que quedó pendiente durante `SENDING` se mide antes que la observación nueva.
10. **TX-C con reintentos y detención** (revisión, M3). TX-C es idempotente (solo actúa sobre el intento en `SENDING`): se reintenta dos veces. Si SQL sigue sin responder, el sender se detiene (`SenderHaltedException`, que el bucle no absorbe): el intento queda en `SENDING`, la impresora no se usa y el arranque siguiente ejecuta TX-R0. NOT_SENT se registra sin el token de la operación, y un apagado entre el claim y la escritura termina en NOT_SENT (decisión 3), para que no deje un `SENDING` que TX-R0 volvería `UNKNOWN`. Cuando el sender se detiene, o cuando cualquier otra falla detiene el servicio de la impresora (por ejemplo, SQL Server inalcanzable al tomar el lease), el EdgeHost sale con el código 70, no con 0 (segunda revisión, B-3; tercera, MEDIA-4): un supervisor configurado para reiniciar ante falla lo relanza y el arranque ejecuta TX-R0. Lo comprueba una prueba que arranca el EdgeHost real con SQL Server inalcanzable.
11. **Bucle del sender** (revisión, M1). Solo un claim obtenido sigue de inmediato; un rechazo espera el intervalo de sondeo.
12. **Canal** (revisión, M2). Una conexión que la impresora cerró no cuenta como abierta y se reabre antes de escribir. Cada mitad de la escritura tiene un tiempo máximo (`WriteTimeoutMs`): una ventana TCP llena termina en `UNKNOWN`, no en un `SENDING` sin fin.
13. **Arranque del sender del EdgeHost** (revisión, M4). Sin su fila de `PrinterSendOrder`, el sender no arranca y se registra un error crítico.
14. **Consultas de evidencia acotadas** (segunda revisión, M-2). `EvidenceObservation` solo crece y no tiene índice por `PrintJobId` ni por `ReceivedAt` (agregarlo sería cambiar `spec/`). Las consultas de "recibida desde el claim" usan dos ramas. La del ciclo busca en `IX_EvidenceObservation_Cycle`. La del job (PRINTED) parte de una cota de `ObservationId`: el último `ObservationId` recibido más de 5 minutos antes del claim, buscado hacia atrás en la clave primaria, de modo que solo se leen las filas recientes. La cota supone que, entre dos registros, el reloj del EdgeHost no retrocede más de 5 minutos y que otro EdgeHost que comparte SQL Server no tiene el reloj atrasado más de 5 minutos. CLOCK_JUMP solo vigila lo primero: compara el reloj de pared de un host con su propio reloj monotónico (tercera revisión, BAJA-2). Una cota por estación evitaría lo segundo, pero recorre la tabla completa cuando la estación no tiene evidencia anterior (una estación nueva o con nivel NONE, en cada envío). Si el supuesto falla, el efecto es conservador: la rama del job (PRINTED) puede no reaplicar una observación y el job termina `UNKNOWN` por ventana vencida; las lecturas de ciclo y la guarda de TX-D usan `IX_EvidenceObservation_Cycle`, sin cota. Las consultas están en `EvidenceQueries` para que la prueba compruebe su plan, y fuerzan la búsqueda por rango en la clave primaria (`FORCESEEK`). En las pruebas el plan ya era ese sin la sugerencia, así que la sugerencia no tiene prueba que la proteja; se deja para que no dependa de las estadísticas.

### Comandos y resultados

Compuerta completa, 2026-10-15 00:59 (`powershell -File build/ci.ps1 --fase 4`, rama con la Fase 3 corregida y `main` integrados):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   164, Omitido:     0, Total:   164, Duración: 18 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    88, Omitido:     0, Total:    88, Duración: 2 m 56 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    58, Omitido:     0, Total:    58, Duración: 4 m 13 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 4: T05=1 destino(s), 1 ejecutadas, T06=1 destino(s), 1 ejecutadas, T07=1 destino(s), 1 ejecutadas, T15=1 destino(s), 1 ejecutadas, T22=1 destino(s), 1 ejecutadas, T29=1 destino(s), 1 ejecutadas, T31=1 destino(s), 1 ejecutadas, T38=1 destino(s), 1 ejecutadas, T39=1 destino(s), 1 ejecutadas, T42=1 destino(s), 1 ejecutadas, T44=1 destino(s), 1 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Durante el desarrollo:

- T44 falló al principio: la evidencia del aplicador llegaba mientras el intento seguía en `SENDING` y quedaba registrada sin efecto (decisión 4).
- T42 falló porque la impresora "fuera de línea" no cortaba la conexión TCP ya abierta; el simulador ahora la corta y la prueba reconecta.
- T31 falló de forma intermitente por la medición de la evidencia que llega durante `SENDING`; la medición quedó pendiente hasta TX-C.
- Al integrar la Fase 3 corregida, T31 volvió a fallar (2 de 3 corridas sueltas): APPLIED llegaba después de TX-C y se medía en el acto, mientras PRINTED seguía pendiente. Corregido con la decisión 9; después, 5 de 5.
- Una corrida de la compuerta con la revisión de la Fase 3 ejecutando su propia suite a la vez dio cuatro errores de conexión a SQL Server (tiempo de espera del protocolo previo al inicio de sesión) en pruebas de esquema; repetida sin esa carga, en verde.
- Antes de la revisión se reforzaron aserciones:
  - T15 y T22: número exacto del trigger. En T15, la aserción anterior (rango 2601 a 51032) aceptaba la violación del índice `UX_PrintAttempt_ConfirmingObservation`, que no es el trigger; ahora se prueban los dos casos por separado.
  - T39: 51012 y `MayHaveSent`.
  - T06, T31 y T42: el `PrintJobResult` publicado.
  - T31: la variante 1 también con el job ya `UNKNOWN`.
  - T38: el UNKNOWN no es final.
  - T44: `serial_mismatch_total` y la observación registrada.

### Pruebas de la fase

- **T05, T06, T07, T15, T22, T29, T31, T38, T39, T42 y T44: PASA** (detalle en `docs/TEST_STATUS.md`). Tres revisiones independientes; la tercera no encontró hallazgos ALTA.

### Revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `9a3e78a` (2026-10-15). Ejecutó la compuerta (164, 88 y 58) y las pruebas de la fase tres veces, todas en verde. Hizo siete sondas (P1 a P7) y seis mutaciones. Resultado: 2 ALTA, 6 MEDIA y varias BAJA.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| A1: la segunda comprobación no era lo último antes de escribir; un canal lento dejaba escribir con el plazo vencido | Alta | Decisión 3. Prueba `The_last_check_before_writing_is_the_monotonic_deadline`, que falla con el orden anterior |
| A2: una lectura contradictoria que llegó durante SENDING se perdía detrás de una confirmación posterior; sin reaplicación al arrancar | Alta | Decisión 4. Prueba `A_contradicting_read_during_sending_is_applied_in_order_and_never_ends_confirmed` (también la reaplicación del arranque), que falla sin el orden |
| M1: bucle caliente con el claim rechazado | Media | Decisión 11. Prueba `A_rejected_claim_waits_for_the_poll_interval` (antes, unos 72 rechazos por segundo) |
| M2: una conexión ociosa cerrada por la impresora daba UNKNOWN; T42 lo ocultaba con `Reset()` | Media | Decisión 12; `Reset()` quitado de T42. Prueba `An_idle_connection_the_printer_closed_is_reopened_before_writing` |
| M3: un TX-C fallido después de escribir dejaba la impresora ocupada para siempre | Media | Decisión 10. Prueba `A_result_that_cannot_be_recorded_halts_the_sender` |
| M4: el sender arrancaba sin su fila de `PrinterSendOrder` | Media | Decisión 13 |
| M5: `serial_mismatch_total` no se contaba en la reaplicación | Media | Decisión 4. La prueba de A2 solo cubría la rama de `SubmitAsync`; la reaplicación del sender quedó cubierta en la segunda revisión (B-6) |
| M6: `duplicate_physical_transmissions_total` no se emitía y T06 lo daba por cumplido | Media | Aserción quitada; `TEST_STATUS` y lo no verificado lo declaran |
| BAJA: el claim directo de N en T42 era vacío para I23 | Baja | T42 prueba el rechazo por orden en TX-B con un job `READY` de un ciclo anterior |
| BAJA: la relectura de la estación sin prueba | Baja | Prueba `A_station_blocked_after_the_claim_is_not_sent_and_does_not_consume_an_attempt` |
| BAJA: T31, variante 1 con el job ya UNKNOWN, era vacía | Baja | `TEST_STATUS` dice lo que se prueba: el job sigue `UNKNOWN`. Que no se consulte al dispositivo no se comprueba (segunda revisión, B-7) |
| BAJA: T07 dejaba el FaultHost huérfano si una aserción fallaba, y la consulta DMV no filtraba por base | Baja | `finally` que mata el proceso; filtro por `database_id` |
| BAJA: una cancelación durante el apagado dejaba un SENDING evitable; J07 en lugar de J08 tras un connect lento; escritura sin tiempo máximo; diccionarios de métricas sin límite | Baja | Decisiones 3, 10 y 12; los diccionarios se vacían al pasar de 10.000 |
| BAJA: el acuse "ACK" no compara el serial; §10.2 paso 1 (plazo monotónico anclado en la recepción con `GATEWAY_RECEIPT`) no implementado; `SendAsync` público sin comprobar la impresora del job | Baja | Declarados en lo no verificado. Un claim de otra impresora no obtiene fila: TX-B exige el lease de la impresora del job |

Mutaciones ejecutadas después de las correcciones, cada una revertida:

- **Evidencia que aplica solo la observación nueva:** la prueba de A2 falla.
- **Comprobación monotónica antes de abrir el canal:** la prueba de A1 falla.

Compuerta completa después de las correcciones, 2026-10-15 02:16 (`powershell -File build/ci.ps1 --fase 4`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   164, Omitido:     0, Total:   164, Duración: 16 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    88, Omitido:     0, Total:    88, Duración: 2 m 32 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    67, Omitido:     0, Total:    67, Duración: 4 m 10 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=4 destino(s), 4 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=6 destino(s), 7 ejecutadas, T46=1 destino(s), 1 ejecutadas
  Fase 4: T05=2 destino(s), 2 ejecutadas, T06=1 destino(s), 1 ejecutadas, T07=1 destino(s), 1 ejecutadas, T15=1 destino(s), 1 ejecutadas, T22=2 destino(s), 2 ejecutadas, T29=3 destino(s), 3 ejecutadas, T31=1 destino(s), 1 ejecutadas, T38=1 destino(s), 1 ejecutadas, T39=1 destino(s), 1 ejecutadas, T42=1 destino(s), 1 ejecutadas, T44=1 destino(s), 1 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Segunda revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `7b33ce7` (2026-10-15). Ejecutó la compuerta (164, 88 y 67), la trazabilidad de la fase, once sondas (Q1 a Q11) y nueve mutaciones, cada una dos veces. A1, M1, M2 y el resto de M3 de la primera revisión quedaron bien resueltos. Resultado: 1 ALTA, 4 MEDIA y 8 BAJA.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| A-1: A2 resuelta solo en secuencia. Con una impresora rápida, la PRINTED coincide con TX-C; en 200 vueltas de la sonda Q2, entre 28 y 38 jobs terminaron `CONFIRMED` con una lectura ajena posterior al claim | Alta | Decisión 4 (bajo concurrencia). Pruebas `Confirming_evidence_applies_an_earlier_unapplied_contradiction_first` (determinista) y `A_contradiction_during_sending_never_ends_confirmed_under_concurrency` (Q2, 100 vueltas, 0 `CONFIRMED`) |
| M-1: una excepción entre el claim y la escritura, fuera del connect (SQL al releer la estación, un apagado), dejaba el intento en `SENDING` y la impresora ocupada hasta reiniciar | Media | Decisión 3. Pruebas `A_sql_error_between_the_claim_and_the_write_is_not_sent_and_frees_the_printer` y `A_shutdown_right_after_the_claim_does_not_leave_sending` |
| M-2: cada observación y cada TX-C recorrían completa `EvidenceObservation` (`Clustered Index Scan`, 200 000 filas leídas para devolver 0 o 1) | Media | Decisión 14. Prueba `Evidence_queries_read_only_recent_rows` (plan real con 100 000 filas antiguas de otra estación) |
| M-3: el arranque no reaplicaba la evidencia de jobs `CONFIRMED`; una J18 registrada y no aplicada se perdía | Media | Decisión 4 (arranque). Prueba `Startup_reapplies_a_recorded_correction_of_a_confirmed_job` |
| M-4: la reaplicación del sender después de TX-C no la cubría ninguna prueba | Media | Prueba `The_sender_applies_evidence_that_arrived_before_its_result` |
| B-1: J06 con el plazo vencido | Baja | Decisión 3: con el plazo monotónico vencido es J08. Prueba `A_station_blocked_after_the_deadline_expires_the_job` |
| B-2: estación y compuerta se evaluaban antes del connect | Baja | Decisión 3: se releen después de abrir el canal, justo antes de la comprobación monotónica. La compuerta sigue sin implementación (declarado) |
| B-3: `SenderHaltedException` detenía el EdgeHost con código de salida 0 | Baja | Decisión 10: código 70. Sin prueba automática (no hay pruebas de la composición del EdgeHost) |
| B-4: `_pending` sin límite | Baja | Se vacía al pasar de 10 000 jobs, como los otros diccionarios de métricas |
| B-5: `send_to_verified_ms` se perdía si la reaplicación del sender aplicaba la lectura antes que su propio pase | Baja | La muestra cuenta si la observación es la que confirmó el intento, la aplicara quien la aplicara. Sin prueba determinista: el orden depende de la carrera |
| B-6: la mutación que quita `CountAlerts` de la reaplicación sobrevivía | Baja | La prueba de M-4 comprueba `serial_mismatch_total` contado por la reaplicación del sender |
| B-7: afirmaciones excesivas en `docs/` (T22 "nunca CONFIRMED", T31 `DeviceCancellation`, A2, M3, M5 y la decisión 10) | Baja | `TEST_STATUS` y esta sección corregidos |
| B-8: `ObservationId` (IDENTITY) no sigue el orden de commit; una observación antigua que lance aborta el pase | Baja | Lo primero, decisión 4 (lock por ciclo) y prueba `A_contradiction_still_being_recorded_is_not_left_behind_a_confirmation`. Lo segundo se acepta: una excepción al aplicar es un defecto y no se rodea; la observación nueva queda registrada y la aplica el pase siguiente o el arranque |

Mutaciones ejecutadas después de las correcciones, cada una revertida (`PrintAgentSecondReviewTests`):

- **Sin la guarda de contradicción previa en TX-D:** fallan las tres pruebas de A-1 y B-8 (determinista, concurrencia y registro en curso).
- **Sin la reaplicación del sender después de TX-C:** falla la prueba de M-4.
- **Sin `CountAlerts` en la reaplicación:** fallan la prueba de M-4 y la de concurrencia.
- **Arranque sin las lecturas contradictorias de jobs `CONFIRMED`:** falla la prueba de M-3.
- **Un error antes de escribir se propaga en lugar de NOT_SENT:** fallan las dos pruebas de M-1.
- **Estación bloqueada con el plazo vencido como J06:** falla la prueba de B-1.
- **Pase del job sin la cota de `ObservationId`:** falla la prueba de M-2.
- **Aplicación sin el lock compartido del ciclo:** falla la prueba del registro en curso (B-8).

Sonda del plan de las consultas (M-2), contra SQL Server real con 100 000 observaciones antiguas de otra estación:

- **Pase en orden del job:** lee 7 filas, 3 en la búsqueda hacia atrás de la cota y 4 en `IX_EvidenceObservation_Cycle` y en la clave primaria. Una corrida anterior de la prueba falló con 100 005 filas leídas, y se agregó `FORCESEEK`; la tercera revisión no lo reprodujo (3 filas con y sin la sugerencia). Como esa corrida no separó qué consulta falló, lo más probable es que fuera la del arranque, con las filas dentro del horizonte (el punto siguiente).
- **Correcciones pendientes del arranque:** con las 100 000 filas dentro del horizonte de 24 h lee las 100 002, que es el costo previsto del horizonte. Con las filas fuera del horizonte, que es lo que comprueba la prueba, lee solo las recientes.

Compuerta completa después de las correcciones, 2026-10-15 03:30 (`powershell -File build/ci.ps1 --fase 4`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   164, Omitido:     0, Total:   164, Duración: 23 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    88, Omitido:     0, Total:    88, Duración: 2 m 34 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    76, Omitido:     0, Total:    76, Duración: 4 m 21 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=4 destino(s), 4 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=6 destino(s), 7 ejecutadas, T46=1 destino(s), 1 ejecutadas
  Fase 4: T05=4 destino(s), 4 ejecutadas, T06=1 destino(s), 1 ejecutadas, T07=1 destino(s), 1 ejecutadas, T15=2 destino(s), 2 ejecutadas, T22=5 destino(s), 5 ejecutadas, T29=4 destino(s), 4 ejecutadas, T31=1 destino(s), 1 ejecutadas, T38=1 destino(s), 1 ejecutadas, T39=1 destino(s), 1 ejecutadas, T42=1 destino(s), 1 ejecutadas, T44=2 destino(s), 2 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Tercera revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `a2c4205` (2026-10-15). Ejecutó la compuerta (164, 88 y 76), la trazabilidad, una sonda de concurrencia de A-1 (1600 vueltas en ocho variantes, todas `UNKNOWN` con `SERIAL_MISMATCH`), otras sondas y doce mutaciones. Los hallazgos de la segunda revisión quedaron resueltos en el código. Resultado: 0 ALTA, 4 MEDIA y 7 BAJA.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| MEDIA-1: la guarda de A-1 no veía una contradicción que nombra otro intento del mismo job (por ejemplo, un primer intento NOT_SENT) | Media | Decisión 4 (bajo concurrencia). Prueba `A_contradiction_naming_an_earlier_attempt_of_the_job_is_applied_before_confirming` |
| MEDIA-2: ninguna prueba protegía el lock exclusivo del registro (la del registro en curso toma el lock a mano) | Media | Decisión 4. Prueba `Recording_a_cycle_observation_waits_for_an_application_in_progress` (retiene el compartido y espera a `RecordAsync`) |
| MEDIA-3: la reaplicación del arranque crecía con todo el histórico (2,5 ms por job; 200 000 filas leídas por job antiguo) y bloqueaba el primer envío | Media | Decisión 4 (arranque): una consulta guiada por la evidencia reciente, en paralelo con el sender. La prueba del plan cubre la consulta nueva |
| MEDIA-4: el código de salida 70 solo cubría `SenderHaltedException`; con SQL Server inalcanzable al arrancar, el EdgeHost salía con 0 | Media | Decisión 10. Prueba `The_edge_host_exits_with_a_failure_code_when_sql_server_is_unreachable`, con el proceso real |
| BAJA-1: interbloqueo entre un registro con `AttemptId`, TX-C y la aplicación; el 50204 resultante no se reintentaba | Baja | Decisión 4: el lock del ciclo antes del `UPDLOCK` del job, y -3 se reintenta (50205). Prueba `Recording_an_application_and_a_tx_c_like_transaction_do_not_deadlock`: espera a ver en `sys.dm_tran_locks` la aplicación esperando el lock del ciclo y actualiza el job con `LOCK_TIMEOUT` de 1 s |
| BAJA-2: la cota de `ObservationId` falla con varios EdgeHost de relojes distintos; "lo detecta CLOCK_JUMP" era inexacto | Baja | Decisión 14: el supuesto queda declarado y su efecto, conservador |
| BAJA-3: la condición "recibida desde el claim" de la guarda no tenía prueba | Baja | Prueba `A_foreign_read_received_before_the_claim_does_not_block_the_confirmation` |
| BAJA-4: la afirmación sobre `FORCESEEK` no se reproduce | Baja | Decisión 14 y la sonda de la segunda revisión, corregidas |
| BAJA-5: afirmaciones excesivas ("nunca en SENDING", "TX-D lo impide en SQL", J07, la fila T22, la prueba del apagado sin "no consume intento", etiquetas T15 y T44 con nivel PRINTED) | Baja | Decisiones 3 y 4 y `TEST_STATUS` corregidos; la prueba del apagado comprueba `AutoAttemptCount`; la de la reaplicación del sender lleva T22 y la del arranque (T44) confirma con APPLIED |
| BAJA-6: J06 sin consumir intento ante un error o un apagado antes de escribir, sin registrar en `SPEC_ISSUES` | Baja | SI-18 (clase B): la persona decidió mantenerlo (sin consumir intento) |
| BAJA-7: el límite de T29 (una pausa entre la comprobación y la llamada de escritura no se puede excluir por software) solo estaba en un comentario | Baja | `TEST_STATUS` (T29) y lo no verificado |

Mutaciones ejecutadas después de las correcciones, cada una revertida (`PrintAgentThirdReviewTests`):

- **La guarda solo ve lecturas sin intento:** falla la prueba de MEDIA-1.
- **El registro sin el lock exclusivo del ciclo:** falla la prueba de MEDIA-2.
- **El `UPDLOCK` del job antes del lock del ciclo:** falla la prueba de BAJA-1. En sus dos primeras versiones la prueba pasaba también con la mutación: la aplicación todavía no había llegado al lock cuando la transacción actualizaba el job, y el reintento de -3 resolvía el interbloqueo. Ahora espera a ver la aplicación esperando el lock y limita la espera del job a 1 s.
- **La guarda sin "recibida desde el claim":** falla la prueba de BAJA-3.
- **Sin el código de salida ante otra falla:** falla la prueba de MEDIA-4.

Compuerta completa después de las correcciones, 2026-10-15 05:23 (`powershell -File build/ci.ps1 --fase 4`):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   164, Omitido:     0, Total:   164, Duración: 10 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    88, Omitido:     0, Total:    88, Duración: 2 m 49 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    81, Omitido:     0, Total:    81, Duración: 3 m 32 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=4 destino(s), 4 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=6 destino(s), 7 ejecutadas, T46=1 destino(s), 1 ejecutadas
  Fase 4: T05=4 destino(s), 4 ejecutadas, T06=1 destino(s), 1 ejecutadas, T07=1 destino(s), 1 ejecutadas, T15=3 destino(s), 3 ejecutadas, T22=8 destino(s), 8 ejecutadas, T29=4 destino(s), 4 ejecutadas, T31=1 destino(s), 1 ejecutadas, T38=1 destino(s), 1 ejecutadas, T39=1 destino(s), 1 ejecutadas, T42=1 destino(s), 1 ejecutadas, T44=2 destino(s), 2 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Lo que no se verificó

- Ninguna impresora, aplicador ni escáner reales; el protocolo y las capacidades son del simulador.
- La compuerta física (`PLC_WINDOW_TAG`) no tiene implementación ni prueba del catálogo.
- `Process.Kill` no es un corte de energía: no prueba el estado del disco ni del dispositivo tras un apagón.
- T29 simula la suspensión avanzando el reloj manual en el punto posterior al claim; no suspende el proceso. Una pausa entre la comprobación monotónica y la llamada de escritura no se puede excluir por software: la comprobación es la última instrucción antes de escribir, pero el sistema operativo puede suspender el proceso entre las dos.
- La alerta inmediata de T22 y T44 y el incidente físico no se prueban.
- El sender del EdgeHost de esta fase toma el lease y no lo renueva: la renovación y la detención al perderlo son de la Fase 5. La composición del EdgeHost no tiene prueba y el EdgeHost no tiene una entrada de evidencia (la del simulador es de las pruebas).
- `duplicate_physical_transmissions_total` no se emite: necesita que el dispositivo informe impresiones por serial (OT-07).
- El acuse del simulador ("ACK serial") se acepta sin comparar el serial.
- §10.2, paso 1: con `GATEWAY_RECEIPT`, el plazo monotónico no se ancla en la marca de recepción, sino que se convierte desde el reloj de pared (acotado por `ClockJumpToleranceMs`).
- La reaplicación del arranque cubre 24 h: una observación registrada y no aplicada antes de ese horizonte (el proceso cayó entre las dos transacciones de TX-D y tardó más en volver) no se reaplica.
- La cota de `ObservationId` de las consultas de evidencia supone que el reloj de un EdgeHost no retrocede más de 5 minutos entre dos registros y que los EdgeHost que comparten SQL Server no difieren en más de 5 minutos (decisión 14). No se probó con varios EdgeHost.
- El reintento de -3 de `sp_getapplock` no tiene una prueba que lo provoque: la de BAJA-1 comprueba que el orden de locks evita el interbloqueo.
- La prueba del registro en curso (B-8) simula el registro con una transacción escrita en la prueba; la espera de `RecordAsync` por el lock la cubre otra prueba (MEDIA-2).
- El código de salida 70 del EdgeHost (B-3) y `send_to_verified_ms` cuando la reaplicación del sender gana la carrera (B-5) no tienen prueba automática.


## Fase 5 · Propiedad, recuperación y operación (T27, T34, T36, T40, T41, T43)

### Plan

- **Entregables:**
  - **Propiedad por impresora:** lock de sistema operativo y lease con renovación. Sin lock o sin lease no se envía nada y se alerta (`printer_owner_conflicts_total`). Si el lease se pierde, el sender se detiene.
  - **Arranque ordenado del EdgeHost** (sección 1 de `ESTRATEGIA_RECUPERACION.md`): `LabelingReady` bajo → propiedad → TX-R0 → cola del dispositivo → estado → barrido → suscripción → `LabelingReady`.
  - **Sender de la impresora manual:** ejecuta las reimpresiones autorizadas (TX-X) con su lease, por su única conexión.
  - **Operations API** (`CartonPrinting.Operations`, HTTP local con autenticación), con todas las acciones manuales en `OperatorAction` o `ReprintAuthorization`:
    - cancelación, reconciliación negativa y cierre;
    - desbloqueo de estación y confirmación de época;
    - reingreso de anomalías;
    - solicitud y aprobación de reimpresión por dos personas distintas, revocación y retiro de la etiqueta original.
  - **Métricas:** `exceptional_reprints_total{risk,result}` y `unknown_resolution_total{result}`.
- **Pruebas que la cierran:** T27, T34, T36, T40, T41 y T43, más pruebas del orden de arranque y de la autenticación de la API.
- **Riesgos previstos:**
  - Dos procesos reales en el mismo host para el lock (T27).
  - El aislamiento de red de T36 solo se puede simular con el proxy TCP.
  - La identidad de las personas: el proveedor real es una decisión de OT/TI; aquí, claves por persona que la configuración entrega como hash, nunca en el repositorio.

### Lo hecho

- **Propiedad** (`CartonPrinting.PrintAgent`):
  - `PrinterOsLock`: lock de sistema operativo por archivo en un directorio común de la máquina.
  - `PrinterOwnership`: lock más lease, con renovación.
  - `OwnedPrinterRunner`: el ciclo de vida de una impresora propia.
- **Arranque ordenado** (`CartonPrinting.Recovery`): `StartupCoordinator` con las ocho etapas. Cada módulo del EdgeHost registra sus pasos:
  - las impresoras: lock, lease y TX-R0;
  - la ingesta: estado, suscripción y primer latido;
  - la estación: el barrido.
- **Operations API** (`CartonPrinting.Operations`):
  - autenticación por persona;
  - solicitudes de reimpresión con regla de dos personas;
  - acciones manuales por TX-M: cancelación, reconciliación negativa, cierre, desbloqueo, confirmación de época, reingreso, aprobación, revocación y retiro de la etiqueta original.
- **EdgeHost:** impresora de línea (`Printer`) y manual (`ManualPrinter`), cada una con su propiedad y un solo sender. La API se activa solo con personas configuradas.
- **Sender manual:** lleva las correcciones de la revisión de la Fase 4 (TX-C con reintentos y detención, NOT_SENT sin el token de la operación, espera ante una autorización no ejecutable). Mide `exceptional_reprints_total{risk}` al ejecutar y `{result=UNRESOLVED}` en todo X03: escritura fallida, ventana vencida en el barrido y TX-R0.
- **Pruebas:**
  - `OwnershipTests` (T27, T36), `ReprintRestartTests` (T43), `StartupOrderTests`;
  - `OperationsTests` (T40 y autenticación), `ReprintTests` (T34, T41, T43);
  - `StartupCoordinatorTests` (unitarias).
  - El FaultHost tiene dos modos nuevos: `lock` (T27) y `reprint` (T43).

### Decisiones

1. **Lock de sistema operativo por archivo.** Un mutex con nombre pertenece en Windows al hilo que lo toma; con código asíncrono ese hilo puede terminar y dejarlo abandonado mientras el proceso sigue vivo, y otro proceso lo tomaría. El lock es un archivo abierto con `FileShare.None` en `%ProgramData%\CartonPrinting\locks` (en Linux, en el directorio temporal compartido), que el sistema operativo libera cuando el proceso muere.
2. **Sin propiedad, cero envíos.**
   - `OwnedPrinterRunner` no invoca el sender sin el lock y el lease.
   - Reintenta tomar la impresora cada duración del lease, con una alerta por intento (`printer_owner_conflicts_total{cause}`).
   - Si pierde el lease, detiene el sender, y antes de volver a enviar ejecuta TX-R0.
   - El sender se detiene antes de que el lease venza, con un margen igual a `LeaseGraceMs` (`ARQUITECTURA_TECNICA`). Cada toma o renovación fija una cobertura local, con el reloj monotónico: la duración menos la gracia, contada desde que se pidió. Si hasta ese instante no hay una renovación confirmada, porque SQL Server no responde, falla o cuelga la llamada, el lease cuenta como perdido sin esperar a que la llamada termine. Los errores de una renovación se reintentan mientras dure la cobertura: un corte breve no detiene el sender (revisión, A2; segunda revisión, MEDIA 1). La llamada corre en el pool de hilos, porque parte de SqlClient es síncrona por debajo (`BeginTransactionAsync`) y con SQL colgado no devolvería su tarea.
   - Un error al volver a tomar la impresora, o del sender fuera de la escritura (por ejemplo, SQL al prepararlo), se registra y la vuelta siguiente vuelve a tomar la impresora con TX-R0; el ciclo de propiedad no termina (segunda revisión, MEDIA 2). La renovación siempre se detiene y se espera al terminar cada vuelta, también si el sender se detuvo (segunda revisión, BAJA 2).
   - Al detener el proceso, el lease se libera (queda vencido): la instancia siguiente no espera a que venza (revisión, A1). Solo lo libera su dueño, con su token; las dos cosas tienen prueba (segunda revisión, MEDIA 3).
   - `OwnerInstanceId` lleva un GUID por arranque: dos contenedores con el mismo nombre de host y el mismo PID no son el mismo dueño. Un archivo de lock que la cuenta del servicio no puede abrir cuenta como lock ajeno (revisión, BAJA 1 y 3).
   - El lease no impide que un agente que ya validó su token transmita (T36): solo el aislamiento físico del canal lo garantiza (ADR 0010).
3. **Arranque ordenado.**
   - Las etapas se ejecutan en orden; un paso que falla se reintenta con espera creciente, sin repetir los que terminaron y sin pasar a la etapa siguiente (falla cerrada).
   - Los senders arrancan después del barrido inicial; el latido periódico, después del primer `LabelingReady`.
   - La etapa 4 (vaciar la cola del dispositivo) se registra sin hacer nada: no hay una capacidad confirmada (OT-07).
   - La etapa 2 falla mientras otro proceso o instancia sea dueño de la impresora de línea: el arranque la reintenta con alerta y no llega a la suscripción ni a `LabelingReady` sin la impresora ni sin TX-R0 (revisión, A1). La impresora manual no detiene la estación: su ciclo de propiedad la toma, con TX-R0, antes de la primera reimpresión.
   - TX-R0 renueva antes el lease: si la etapa se reintentó después de que venciera y otra instancia lo tomó, no cierra el intento en curso de esa instancia (revisión, BAJA 6). Para la impresora de línea, la etapa 3 sin ser dueña falla también al reintentarse: no se da por terminada sin TX-R0. Y antes del primer envío, que llega después de las etapas 3 a 6 sin renovar, se confirma el lease; si dejó de ser de este proceso, se vuelve a tomar con TX-R0 (segunda revisión, BAJA 1).
   - El coordinador se crea antes que los servicios del host, así que el servicio de arranque le pasa el logger del host al ejecutarlo: cada fallo de un paso queda registrado con su etapa (revisión, M1).
4. **Identidad en la Operations API.**
   - Cabeceras `X-Operator-Id` y `X-Operator-Key`. La configuración guarda solo el SHA-256 de cada clave y se compara en tiempo constante.
   - La identidad autenticada es la que queda en `OperatorAction` y en `ReprintAuthorization`; el cuerpo nunca la decide.
   - El proveedor de identidad definitivo es una decisión de OT/TI.
5. **Reimpresión por la API.**
   - La solicitud espera en memoria (el esquema solo guarda autorizaciones aprobadas) y la aprueba otra persona (403 si es la misma).
   - El destino es la `ManualPrinterId` de la revisión activa y la ventana de evidencia la de la revisión.
   - La API nunca envía bytes: la reimpresión la ejecuta el sender de la impresora manual con su lease.
   - Un rechazo de TX-M, de un trigger o de una restricción es 409 con el número de error. Una solicitud mal formada (un dato obligatorio vacío, un `firstCycleId` que no es la cadena decimal canónica de la regla 9, o un 515 de SQL) es 400.
   - Si TX-M rechaza la aprobación (409 o 400), la solicitud vuelve a quedar pendiente (revisión, BAJA 5). Un error desconocido, por ejemplo la conexión cortada después del commit, no la restaura, porque la autorización pudo quedar creada; TX-M no se cancela con la petición (segunda revisión, BAJA 7).
   - Desbloquear una estación que no está bloqueada es 409 y no deja una `OperatorAction` sin efecto (revisión, BAJA 4). TX-M lo decide con la fila de la estación bloqueada (`UPDLOCK`), así que con desbloqueos concurrentes uno la desbloquea y los demás reciben 409 sin registro. Con 0 filas no lanza excepción (T47) (segunda revisión, BAJA 4).
   - El sender manual deja esperando 5 s, con el reloj monotónico, una autorización que TX-X rechaza por cualquier causa, y las aprobadas después se ejecutan (revisión, M3; segunda revisión, BAJA 3 y 6). Causas: el trigger (LABEL_DAMAGED sin el retiro registrado), un índice (otro intento abierto del job) o SQL sin respuesta. TX-X devuelve `DuplicateRisk` en su `OUTPUT`, y entre TX-X y la escritura cualquier error o apagado es NOT_SENT, como en el sender de línea; lo comprueba una prueba (segunda revisión, MEDIA 3).
   - `exceptional_reprints_total` se cuenta con `{risk, result=EXECUTED}` al ejecutar y con `{result=UNRESOLVED}` en cada X03 (revisión, BAJA 7). En X03 el riesgo no está a mano sin otra consulta: las dos series se leen por separado, no se suman.
6. **`unknown_resolution_total{result}`** se cuenta en las resoluciones manuales (reconciliación negativa y cierre). La resolución por evidencia (J15) no se cuenta.
7. **T41 en sucesión.** `UX_ReprintAuthorization_OpenPerJob` admite una sola autorización `APPROVED` por job: las tres autorizaciones del catálogo se aprueban una después de otra.

### Comandos y resultados

Compuerta completa, 2026-10-15 02:38 (`powershell -File build/ci.ps1 --fase 5`, rama con las Fases 3 y 4 corregidas):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   167, Omitido:     0, Total:   167, Duración: 16 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    93, Omitido:     0, Total:    93, Duración: 2 m 50 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    73, Omitido:     0, Total:    73, Duración: 4 m 16 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=4 destino(s), 4 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=6 destino(s), 7 ejecutadas, T46=1 destino(s), 1 ejecutadas
  Fase 5: T27=2 destino(s), 2 ejecutadas, T34=1 destino(s), 1 ejecutadas, T36=1 destino(s), 1 ejecutadas, T40=1 destino(s), 1 ejecutadas, T41=1 destino(s), 1 ejecutadas, T43=2 destino(s), 2 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

Durante el desarrollo:

- **Tres pruebas que no probaban lo que decían** (corregidas):
  - En T41, TX-X fallaba con 52001 porque el lease de la impresora manual había vencido con el reloj de prueba, no por la regla que se quería probar. Ahora se renueva el lease y cada rechazo se comprueba con su número exacto.
  - En T34, el intento ORDINARY lo rechazaba primero el índice `UX_PrintAttempt_OrdinarySent`, no el trigger. Ahora se prueban los dos.
  - En T41, la lectura anterior la rechazaba el índice de la observación que confirma. Ahora se agrega una lectura nueva anterior al intento, que llega al trigger.
- La prueba de M3 de la revisión de la Fase 4 quedó colgada en la primera versión: después de la detención, la impresora tenía un intento en `SENDING` y el siguiente claim se rechazaba sin fin. Ese comportamiento es el correcto; la prueba ahora ejecuta el bucle desde el principio.

### Pruebas de la fase

- **T27, T34, T36, T40, T41 y T43: PASA** (detalle en `docs/TEST_STATUS.md`). Dos revisiones independientes; la segunda no encontró hallazgos ALTA.

### Revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `8c31acd` (2026-10-15). Ejecutó la compuerta (167, 93 y 73), la trazabilidad de la fase, siete corridas de demostración y seis mutaciones. Resultado: 2 ALTA, 4 MEDIA y 8 BAJA. Ninguno produce un envío duplicado por la vía de los claims: TX-B exige el lease vigente.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| A1: el arranque seguía sin la impresora; en un reinicio normal (otro PID, lease anterior vigente) TX-R0 corría después de la suscripción y de `LabelingReady` | Alta | Decisiones 2 y 3. Prueba `Startup_waits_for_the_previous_owner_before_recovering_and_subscribing`, con un dueño distinto al que dejó el `SENDING` |
| A2: una excepción de SQL en la renovación terminaba la renovación en silencio; el sender seguía y cada claim se rechazaba hasta reiniciar | Alta | Decisión 2. Prueba `A_renewal_that_fails_counts_as_a_lost_lease_and_printing_resumes_when_sql_returns` (proxy TCP delante de SQL solo para el lease) |
| M1: los fallos del arranque no quedaban en ningún log | Media | Decisión 3. Prueba unitaria `A_failing_step_is_logged_with_the_logger_of_the_host`; `StartupCoordinator.Failures` cuenta los fallos. No se agregó una métrica: la especificación no define una para el arranque |
| M2: T27 no probaba los SENDING heredados; las mutaciones "sin TX-R0 al volver a tomar la impresora" y "sin cancelar el sender" sobrevivían | Media | Pruebas `A_second_instance_recovers_the_inherited_sending_before_it_sends` (T27) y `A_lost_lease_stops_the_sender_and_tx_r0_runs_before_it_sends_again`. `TEST_STATUS` y la decisión 2 dicen lo que se prueba. La composición del EdgeHost sigue sin prueba (declarado) |
| M3: una autorización no ejecutable bloqueaba a las demás de la impresora manual; un error entre TX-X y la escritura dejaba el intento en `SENDING` | Media | Decisión 5. Prueba `A_rejected_authorization_does_not_block_the_ones_approved_after_it` |
| M4: la regla 12 (I02) del sender manual sin prueba | Media | Prueba `A_reprint_cut_mid_write_is_unknown_and_unresolved_never_not_sent` |
| BAJA 1: `OwnerInstanceId` no era único entre hosts | Baja | GUID por arranque (decisión 2) |
| BAJA 2: T41 y T34 no comprobaban todo su criterio en su prueba | Baja | T41 comprueba `UX_PrintAttempt_Authorization` (2601); T34, `UNKNOWN → SENDING` (51012) y que no hay un job nuevo |
| BAJA 3: el directorio de locks (otra cuenta de Windows, `TMPDIR` y `PrivateTmp` en Linux, `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`) | Baja | `UnauthorizedAccessException` cuenta como lock ajeno. Lo de Linux queda declarado en lo no verificado |
| BAJA 4: desbloquear una estación no bloqueada dejaba una `OperatorAction` sin efecto | Baja | Decisión 5: 409 sin registro |
| BAJA 5: la aprobación consumía la solicitud antes de TX-M; un 515 salía como 500 | Baja | Decisión 5 |
| BAJA 6: TX-R0 no verificaba el lease | Baja | Decisión 3 |
| BAJA 7: `ResultRecorder` usa `Task.Delay` sin `TimeProvider`; `exceptional_reprints_total` con juegos de etiquetas disjuntos | Baja | La espera entre reintentos de TX-C no decide ningún plazo y con el reloj manual de las pruebas no avanzaría: se mantiene, registrado. Métrica: decisión 5 |
| BAJA 8: `firstCycleId` era un número JSON | Baja | Cadena decimal canónica (decisión 5) |

Mutaciones ejecutadas después de las correcciones, cada una revertida:

- **La etapa 2 no falla sin la impresora:** falla la prueba de A1.
- **La renovación que falla no cuenta como lease perdido:** falla la prueba de A2.
- **El lease perdido no detiene el sender:** fallan la prueba de A2 y la del lease perdido.
- **Sin TX-R0 al volver a tomar la impresora:** fallan la de T27 con el `SENDING` heredado y la del lease perdido.
- **Una autorización rechazada se reintenta de inmediato:** falla la prueba de M3.
- **NOT_SENT después de invocar la escritura de la reimpresión:** falla la prueba de M4.
- **Desbloquear sin comprobar el bloqueo:** falla la prueba de las acciones de estación.
- **La solicitud rechazada no vuelve a quedar pendiente:** falla la misma prueba.
- **El coordinador ignora el logger del host:** falla la prueba unitaria de M1. En esa corrida también falló `A_failing_step_is_retried_without_repeating_finished_steps_and_later_stages_wait`, con la tercera revisión de la Fase 4 corriendo en paralelo; sin la mutación pasó 5 de 5.

Compuerta completa después de las correcciones, 2026-10-15 04:00 (`powershell -File build/ci.ps1 --fase 5`, con la Fase 4 corregida por su segunda revisión integrada):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   168, Omitido:     0, Total:   168, Duración: 26 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    96, Omitido:     0, Total:    96, Duración: 3 m 19 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    86, Omitido:     0, Total:    86, Duración: 5 m 3 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=4 destino(s), 4 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=6 destino(s), 7 ejecutadas, T46=1 destino(s), 1 ejecutadas
  Fase 5: T27=4 destino(s), 4 ejecutadas, T34=1 destino(s), 1 ejecutadas, T36=1 destino(s), 1 ejecutadas, T40=1 destino(s), 1 ejecutadas, T41=2 destino(s), 2 ejecutadas, T43=3 destino(s), 3 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Segunda revisión independiente

Subagente `revisor-adversarial` sobre una copia limpia de `50f789c` (2026-10-15). Ejecutó la compuerta (dos corridas: 168, 96 y 84 de 86, con dos fallos intermitentes que pasan solos; y 168, 96 y 86), la trazabilidad, repitió sus sondas de la primera revisión, hizo seis sondas nuevas (N1 a N6) y doce mutaciones. Los hallazgos A1, A2, M2, M3 (el caso 51021), M4 y las BAJA 5 y 8 quedaron resueltos. Resultado: 0 ALTA, 3 MEDIA y 7 BAJA.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| MEDIA 1: con SQL colgado (no cortado), el sender seguía 20 s después del congelamiento, cuando el lease ya había vencido y otra instancia lo tenía; al revés, un solo error transitorio detenía el sender | Media | Decisión 2: cobertura local del lease con reintentos. Pruebas `A_renewal_that_hangs_stops_the_sender_before_the_lease_can_expire` (proxy congelado: se detiene en menos de 3 s) y `A_transient_renewal_error_does_not_stop_the_sender` (corte de 1,3 s, sin alerta) |
| MEDIA 2: un error de SQL en el preámbulo del sender terminaba el ciclo de propiedad y el EdgeHost salía con 0 | Media | Decisión 2 y el código de salida 70 ante cualquier falla (tercera revisión de la Fase 4, MEDIA-4). Prueba `An_error_in_the_sender_does_not_end_the_ownership_cycle_and_tx_r0_runs_before_it_runs_again` |
| MEDIA 3: tres correcciones sin prueba (NOT_SENT entre TX-X y la escritura, la liberación del lease, TX-R0 con el lease robado) | Media | Pruebas `A_reprint_whose_connection_fails_after_tx_x_is_not_sent`, `Only_the_owner_releases_the_lease_and_the_next_instance_takes_it_at_once` y `Tx_r0_does_not_run_after_the_lease_was_taken_by_another_instance` |
| BAJA 1: el reintento de la etapa 3 la completaba sin TX-R0 y sin la impresora | Baja | Decisión 3. La misma prueba de TX-R0 con el lease robado lo comprueba |
| BAJA 2: una renovación huérfana volvía a tomar el lease liberado | Baja | Decisión 2 (`finally` en el ciclo de propiedad) |
| BAJA 3: la espera de 5 s solo cubría 52001 y 51021 | Baja | Decisión 5: cualquier rechazo de TX-X |
| BAJA 4: desbloqueos concurrentes dejaban `OperatorAction` sin efecto | Baja | Decisión 5: TX-M con la fila bloqueada. La prueba de las acciones de estación hace ocho desbloqueos a la vez |
| BAJA 5: pruebas de T27 y T43 inestables bajo carga (reloj de prueba que seguía avanzando; retardos negativos) | Baja | El reloj de prueba deja de avanzar cuando el runner vuelve a tener la impresora; los retardos se acotan en cero. En las pruebas nuevas con reloj manual pasó lo mismo con la cobertura del lease: se aplicó la misma corrección. Tres corridas seguidas, 15 de 15 |
| BAJA 6: la espera de 5 s usaba el reloj de pared | Baja | Reloj monotónico (decisión 5) |
| BAJA 7: restaurar la solicitud tras un commit ambiguo podía permitir una segunda autorización | Baja | Decisión 5: solo se restaura ante un rechazo definitivo. Sin prueba: no hay una forma determinista de cortar la conexión justo después del commit |

Al integrar la Fase 4 cerrada aparecieron tres efectos de estas correcciones en pruebas existentes, todos corregidos en las pruebas:

- **La prueba de T27 en el mismo host** (`OwnershipTests`) fallaba con dos corridas del sender en lugar de una. El reloj manual seguía avanzando 100 s cada 50 ms después de que el runner tomaba la impresora, y con la cobertura local del lease eso lo vencía mientras SQL respondía en tiempo real. El reloj deja de avanzar cuando el runner es dueño.
- **`T51018_51019`** obtenía su `OperatorAction` desbloqueando una estación habilitada, y TX-M ya no la registra. Ahora usa un desbloqueo real que la estación ya no referencia: de lo contrario, el DELETE lo rechazaría la FK `FK_StationConfig_OperatorAction` y no el trigger.
- **La prueba de proceso del EdgeHost** de la tercera revisión de la Fase 4 esperaba el código de salida 70 con SQL Server inalcanzable. Con el arranque coordinado, el EdgeHost no sale: la etapa 2 falla, se registra y se reintenta hasta que SQL vuelva (ESTRATEGIA_RECUPERACION §3). La prueba ahora comprueba eso (`The_edge_host_keeps_retrying_its_startup_while_sql_server_is_unreachable`). El código 70 queda para una falla que termina el servicio de impresora después del arranque, como un sender detenido.

Mutaciones ejecutadas después de las correcciones, cada una revertida:

- **Sin la cobertura local del lease:** falla la prueba del SQL colgado.
- **El primer error de renovación detiene el sender:** falla la del corte breve. En su primera versión el corte duraba 400 ms y no coincidía con ninguna renovación, así que la prueba pasaba con la mutación; ahora dura 1,3 s.
- **Un error del sender termina el ciclo de propiedad:** falla la prueba de MEDIA 2.
- **Liberación sin comprobar dueño y token:** falla la de la liberación.
- **La etapa 3 sin dueño se da por terminada:** falla la de TX-R0 con el lease robado.
- **Un error antes de escribir en el sender manual relanza la excepción:** falla la de NOT_SENT después de TX-X.
- **Desbloqueo sin comprobar en TX-M:** falla la de las acciones de estación.

Compuerta completa después de las correcciones, 2026-10-15 05:46 (`powershell -File build/ci.ps1 --fase 5`, con la Fase 4 cerrada integrada):

```
Resultado: 15 grupos OK, 0 errores
Artefactos generados al día.
    0 Advertencia(s)
    0 Errores
Correctas! - Con error:     0, Superado:   168, Omitido:     0, Total:   168, Duración: 9 s - CartonPrinting.UnitTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    97, Omitido:     0, Total:    97, Duración: 2 m 31 s - CartonPrinting.IntegrationTests.dll (net10.0)
Correctas! - Con error:     0, Superado:    96, Omitido:     0, Total:    96, Duración: 3 m 13 s - CartonPrinting.FaultTests.dll (net10.0)
  Fase 0: T23=3 destino(s), 20 ejecutadas, T28=CI(verify_package.py)
  Fase 1: T01=1 destino(s), 1 ejecutadas, T02=1 destino(s), 2 ejecutadas, T10=1 destino(s), 1 ejecutadas, T14=1 destino(s), 2 ejecutadas, T47=4 destino(s), 62 ejecutadas
  Fase 2: T11=1 destino(s), 1 ejecutadas, T13=5 destino(s), 5 ejecutadas, T24=1 destino(s), 1 ejecutadas, T25=8 destino(s), 10 ejecutadas, T26=2 destino(s), 3 ejecutadas, T30=2 destino(s), 3 ejecutadas, T35=2 destino(s), 2 ejecutadas, T45=4 destino(s), 12 ejecutadas
  Fase 3: T03=1 destino(s), 1 ejecutadas, T04=1 destino(s), 1 ejecutadas, T17=1 destino(s), 1 ejecutadas, T18=4 destino(s), 4 ejecutadas, T21=1 destino(s), 1 ejecutadas, T33=1 destino(s), 1 ejecutadas, T37=6 destino(s), 7 ejecutadas, T46=1 destino(s), 1 ejecutadas
  Fase 4: T05=4 destino(s), 4 ejecutadas, T06=1 destino(s), 1 ejecutadas, T07=1 destino(s), 1 ejecutadas, T15=3 destino(s), 3 ejecutadas, T22=8 destino(s), 8 ejecutadas, T29=4 destino(s), 4 ejecutadas, T31=1 destino(s), 1 ejecutadas, T38=1 destino(s), 1 ejecutadas, T39=1 destino(s), 1 ejecutadas, T42=1 destino(s), 1 ejecutadas, T44=2 destino(s), 2 ejecutadas
  Fase 5: T27=6 destino(s), 6 ejecutadas, T34=2 destino(s), 2 ejecutadas, T36=1 destino(s), 1 ejecutadas, T40=1 destino(s), 1 ejecutadas, T41=2 destino(s), 2 ejecutadas, T43=3 destino(s), 3 ejecutadas
Resultado: trazabilidad OK, 0 errores
Compuerta en verde.
```

### Lo que no se verificó

- Dos máquinas reales: los "hosts distintos" de T27 y T36 se simulan con directorios de lock separados en una máquina, y el aislamiento del canal de T36 con un proxy TCP.
- El EdgeHost como proceso con su configuración: `StartupOrderTests` compone las piezas reales a mano (propiedad, TX-R0, ingesta con el PLC simulado, barrido, latido).
- El proveedor de identidad de planta, TLS y la exposición de la API fuera de la máquina: la API es HTTP local.
- Las solicitudes de reimpresión pendientes se pierden si el EdgeHost reinicia: hay que volver a pedirlas.
- La etapa 4 del arranque (vaciar la cola del dispositivo) no tiene implementación.
- `unknown_resolution_total` no cuenta las resoluciones por evidencia.
- La composición del EdgeHost (`Program.cs`, `PrinterHostedService`, `StartupHostedService`) solo tiene una prueba de proceso: con SQL Server inalcanzable, el arranque se reintenta y el proceso no sale. El código de salida 70 de una falla posterior al arranque no tiene prueba de proceso. La liberación del lease al detener (el `finally` del servicio), la etapa 2 que falla solo para la impresora de línea y el logger del arranque se comprueban en sus piezas (`PrinterOwnership`, `OwnedPrinterRunner`, `StartupCoordinator`), no en el proceso.
- La restauración de una solicitud tras un commit ambiguo (segunda revisión, BAJA 7) no tiene prueba.
- La cobertura local del lease supone que el reloj monotónico del EdgeHost no se detiene; una suspensión del proceso más larga que la cobertura la cubre el aislamiento físico del canal (T36), no el software.
- El lock de sistema operativo en Linux: depende de `TMPDIR` y de `PrivateTmp` del servicio, y `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` lo anula. No se probó en Linux.
- Un `firstCycleId` de más de 20 dígitos o con signo lo rechaza la validación canónica; no hay prueba de cada forma inválida, solo del número JSON y del cero a la izquierda.

### Riesgos

- Un salto del reloj de prueba (`FakeTimeProvider`) mayor que la duración de un lease lo vence: las pruebas que avanzan el reloj renuevan los leases que siguen usando.
- El directorio de locks debe ser el mismo para todas las instancias de una máquina y escribible por la cuenta del servicio.

## Fase 6 · Perfil extendido (T09, T20): NO_APLICA

### Plan

- **Entregables:** ninguno. `PROMPT_IMPLEMENTACION.md` fija `PERFIL_EXTENDIDO=NO`, y OT-14 ("¿Es obligatorio conservar la historia de todos los ciclos durante una interrupción?") sigue abierta. El perfil extendido (FIFO del PLC, `AckCycleId`, journal SQLite con `synchronous=FULL`) solo se activa si OT lo exige (ADR 0009, I14).
- **Pruebas:** T09 y T20 quedan `NO_APLICA (OT-14 abierta)` en `docs/TEST_STATUS.md`. No se implementaron ni se ejecutaron, y no cuentan como superadas.
- **Etiqueta:** `fase-6-ok` sin código, como indica el prompt.

### Lo que no se verificó

- Todo el perfil extendido: journal SQLite, `AckCycleId`, reproducción tardía del journal, I21 y G09. La FIFO simulada de T11 (Fase 2) es solo la recuperación de ciclos tras reconectar del perfil mínimo.

## Fase 7 · Hardening y demo (T08, T12, T16, T19, T32)

### Plan

- **Entregables:**
  - **Fallas:** RabbitMQ detenido 10 minutos con carga nominal (T08) y SQL Server caído mientras pasan cajas (T19).
  - **Recirculación** con identidad física (T16) y sin ella (T32).
  - **Carga (T12)** con simuladores en `CartonPrinting.LoadTests`, con la duración del catálogo. Se corre aparte (`Category=Load`) y se reporta en `docs/BENCHMARK.md`.
  - **Seguridad:** OPC UA con SignAndEncrypt en la configuración, secretos fuera del repositorio y RabbitMQ con TLS configurable.
  - **Demo y FAT:** `docs/DEMO.md` y `docs/FAT_CHECKLIST.md`.
- **Pruebas que la cierran:** T08, T12, T16, T19 y T32.
- **Riesgos previstos:**
  - La tasa y el payload de T12 no están acordados con OT. La persona decidió usar una hipótesis declarada: una caja por segundo con el payload del simulador, 30 minutos a esa tasa y 10 al doble. `BENCHMARK.md` lo dice como hipótesis, no como aceptación en planta.
  - Las pruebas largas (T08 y T12) comparan latencias y son sensibles a la carga de la máquina.

### Lo hecho

- **SQL Server caído en la ingesta (T19).** `SqlAvailability` distingue "SQL no disponible" de cualquier otro error de SQL.
  - Con SQL caído, la ingesta reintenta dos veces y rechaza el ciclo (`opc_records_rejected_total{cause=SQL_UNAVAILABLE}`): falla cerrada, sin detección.
  - Al volver SQL, recarga el estado de la estación, y el ciclo siguiente abre el hueco de los ciclos de la caída.
  - Cualquier otro error de SQL sigue deteniendo el consumidor.
- **Recirculación (T16, T32; SI-17 decidido por la persona).** TX-A y TX-E cuentan como recirculación solo las detecciones anteriores de la caja, con el modo de identidad más restrictivo entre el congelado y el activo.
- **RabbitMQ caído (T08).** El Outbox Relay actualiza `outbox_pending_age_seconds` también cuando la publicación falla. Antes, con el broker caído, la métrica quedaba congelada en su último valor: justo la alerta de esa caída.
- **Tramos de latencia de la sección 10.4.** De los tramos, solo `opc_receive_to_job_ready_ms` se medía. Ahora también se miden:
  - `plc_to_opc_receive_ms`, en el registro, solo para notificaciones en vivo;
  - `opc_receive_to_send_start_ms`: la marca monotónica de recepción viaja de TX-A al sender en `CartonMetrics`;
  - `print_deadline_slack_ms`, en el sender;
  - `send_start_to_applied_ms` y `applied_to_verified_ms`, en la evidencia.
  
  Las muestras en memoria quedan acotadas a 50 000 por serie, para no crecer sin fin en un proceso de larga duración.
- **Señal en proceso (G11).** `ReadyJobSignal` (`System.Threading.Channels`): TX-A, TX-E y el barrido, cuando liberan un `RETRY_WAIT`, despiertan al sender de línea sin esperar su sondeo de 50 ms, que queda como respaldo. El EdgeHost lo registra como singleton.
- **T12.** `StationPipeline` compone la estación como el EdgeHost:
  - PLC simulado por OPC UA, ingesta real, señal, sender con sondeo de respaldo, impresora, aplicador y escáner simulados;
  - nivel VERIFIED;
  - una caja cada segundo durante 30 minutos y dos por segundo durante 10;
  - 10 cajas de calentamiento fuera de la muestra.
  
  Escribe un reporte JSON que `build/benchmark_report.py` convierte en `docs/BENCHMARK.md`. `LatencySegmentsTests`, en la compuerta, comprueba que cada tramo tiene una muestra por caja y que la señal despierta al sender: con un sondeo de 2 s, sin señal la espera supera 500 ms y con señal no.
- **Seguridad.**
  - `RabbitMqOptions.CreateConnectionFactory`, que usan el publicador y los consumidores: TLS 1.2 o 1.3 con un Uri `amqps://` o con `Tls.Enabled`, el nombre esperado del broker y un certificado de cliente desde los secretos, sin errores de política aceptados.
  - `build/check_secrets.py`, en la compuerta: ningún certificado, clave ni contraseña literal versionados fuera de `tests/` y `spec/`.
  - `OpcSecurityTests`: el PLC simulado solo con SignAndEncrypt; el cliente con los valores por defecto (SignAndEncrypt, sin autoaceptar) rechaza un servidor que no es de confianza y se conecta cifrado cuando lo es.
- **Demo.** `tests/CartonPrinting.DemoHost`:
  - siembra una estación y arranca los simuladores (PLC, impresora, ERP, aplicador y escáner);
  - lanza el EdgeHost real como proceso, configurado solo con variables de entorno, y hace pasar cajas hasta CONFIRMED.
  
  `DemoTests` lo ejecuta en la compuerta: es la primera prueba de composición del EdgeHost de punta a punta. `docs/DEMO.md` es el guion; `docs/FAT_CHECKLIST.md` lista lo que solo acepta la planta.
- **Compuerta.** `build/ci.ps1` y `build/ci.sh`:
  - agregan el paso de secretos;
  - con `--carga`, corren además las pruebas de carga (`Category=Load`), para que la trazabilidad de la Fase 7 vea T12.

### Decisiones

1. **SQL caído: falla cerrada en el perfil mínimo** (ESTRATEGIA_RECUPERACION §3, OT-13 por defecto). Un ciclo que no se puede registrar no se registra: al volver SQL es parte de un hueco (`SequenceGap`), nunca una detección inventada (I07, I16). Cómo se decide qué ciclos son de la caída, después de la revisión: decisión 7.
2. **RabbitMQ caído: simulado con un proxy TCP** entre el Outbox Relay y el broker, que corta las conexiones y no acepta nuevas. Detener el contenedor cambiaría su puerto en Testcontainers.
   - El consumidor se conecta al broker directamente: lo que se prueba es la independencia del EdgeHost.
   - T08 corre sin otras pruebas del ensamblado en paralelo, porque compara la latencia de impresión antes y durante la caída.
3. **La señal en proceso es solo una señal.** El sender siempre lee de SQL qué enviar. Una señal perdida la cubre el sondeo de respaldo; varias seguidas lo despiertan una vez.
4. **T12 con valores de laboratorio** (OT-03 abierta): `TransitBudgetMs` 1500, `DeviceLeadTimeMs` 400, `SendMarginMs` 250 y `ClockSkewToleranceMs` 50.
   - El margen por defecto de las pruebas (100) no cubría p99(`presend_check_to_write_ms`) + `ClockJumpToleranceMs` (200), como pide la sección 17. La corrida corta lo mostró, y T12 lo comprueba junto con la desigualdad y con `DeviceLeadTimeMs` ≥ p99 medido.
   - El PLC simulado negocia la publicación en 100 ms aunque se pidan 20: está en el reporte.
5. **La demo hace de integración de evidencia.** El EdgeHost no tiene una entrada de evidencia propia (Fase 4), así que la demo aplica por TX-D la evidencia que informan la impresora, el aplicador y el escáner simulados. El PLC simulado va sin cifrado en la demo (laboratorio); la seguridad de planta la prueba `OpcSecurityTests`.
6. **Secretos.** La revisión excluye `tests/`, porque las credenciales de los contenedores efímeros no son de planta, y `spec/`. Las variables de sqlcmd, de entorno y de plantilla no cuentan como contraseñas.
7. **Falla cerrada decidida por la recepción** (revisión independiente, A1 y M1).
   - **Una sola caída** para la ingesta y el latido. La declara la ingesta cuando un ciclo agota sus tres intentos, de hasta 1 s cada uno (`SqlAttemptTimeoutMs`). También la declara el latido cuando no puede leer la estación: sin respuesta en ese mismo tiempo, o con SQL no disponible.
   - **Mientras está declarada:** `LabelingReady` en falso, y cada ciclo se rechaza sin ir a SQL.
   - **Vigilancia de la recuperación.** Prueba cada segundo si SQL volvió, sin esperar ciclos: con `LabelingReady` en falso, la línea puede no mandar ninguno. Al volver:
     - recarga el estado (etapa 5 del arranque);
     - marca la recuperación con el reloj monotónico;
     - recién entonces el latido levanta `LabelingReady` (etapa 8). El barrido sigue con su intervalo (etapa 6).
   - **Regla de recepción.** Un ciclo recibido antes de esa marca se rechaza aunque se procese después: es parte del hueco, nunca una detección.
   - **Tiempos.** Los tiempos de los intentos son de E/S, en tiempo real, y no deciden ningún plazo. Un intento que vence se abandona, porque SqlClient no siempre corta enseguida una conexión que dejó de responder. Conserva el lock del consumidor hasta terminar.
   - **Costos aceptados.**
     - Tras volver SQL, los ciclos que llegan antes de que la vigilancia lo note (hasta 1 s) también son hueco, todavía con `LabelingReady` en falso.
     - Un TX-A abandonado que sí alcanzó a confirmar deja registrada una detección real; el claim vuelve a comprobar su plazo.
8. **Benchmark con la composición declarada** (M4). `StationPipeline` agrega:
   - el barrido cada 200 ms;
   - OPC UA con SignAndEncrypt y validación estricta, con el certificado del PLC en el almacén de confianza;
   - en T12, el Outbox Relay con publisher confirms hacia RabbitMQ.

   El reporte declara la composición, los errores de la corrida y los escenarios excluidos: fallas, ERP en caché, operación, propiedad, consumidor y hardware.
9. **T08 con el plazo nominal** (M3): `TransitBudgetMs` 1500, `DeviceLeadTimeMs` 400 y `SendMarginMs` 250, como T12.
   - Una caja demorada durante la caída vence, y la prueba falla.
   - "Latencias de impresión sin cambio" no tiene un número en el catálogo. Los márgenes son de ruido de una sola máquina:
     - p50 durante la caída ≤ p50 antes + 20 ms;
     - p95 ≤ el mayor entre 1,5 veces y + 50 ms;
     - p99 ≤ el mayor entre 2 veces y + 100 ms.
   - El máximo lo acota el plazo.
   - Diez cajas de calentamiento quedan fuera de la medición.

### Comandos y resultados

T08, corrida completa con la máquina en reposo, 2026-10-15 06:00 (`dotnet test tests/CartonPrinting.FaultTests --filter FullyQualifiedName~BrokerOutageTests`):

```
T08 latencia recepción→escritura (ms): antes p50=38 p95=41 máx=80; durante p50=39 p95=108 máx=3941; cajas=690
Correctas CartonPrinting.FaultTests.Hardening.BrokerOutageTests.Ten_minutes_without_rabbitmq_change_nothing_in_printing_and_lose_no_event [11 m 31 s]
```

Durante el desarrollo:

- **Las dos primeras corridas completas de T08 fallaron** con los jobs de los últimos 60 segundos de la caída vencidos (`DEADLINE_PASSED`). La causa estaba en la prueba: el lease de la impresora del escenario dura 10 minutos y nadie lo renovaba, así que TX-B rechazaba cada claim. `StationScenario.KeepLeaseAsync` lo renueva, como el ciclo de propiedad del EdgeHost; `StationPipeline` también lo usa.
- **La tercera corrida pasó** las comprobaciones funcionales (690 cajas, ninguna perdida ni duplicada), pero no la de latencia (p95 de 509 ms durante la caída frente a 46 ms antes). La caída coincidió con las compilaciones y mutaciones de las revisiones de las Fases 4 y 5. Desde entonces T08 corre sola y se repitió con la máquina en reposo.
- **`LatencySegmentsTests`** falló dos veces bajo la carga de esas revisiones. La primera caja en frío tardaba 1,7 s, con conexión y JIT, y el resto arrastraba el backlog. Además, un job venció sin la señal con un plazo de 5 s. Ahora tiene tres cajas de calentamiento, un plazo de 15 s y la comparación sobre diez cajas a una por segundo: tres corridas seguidas en verde.
- **La corrida corta de T12** (1 minuto y 30 segundos) mostró que la desigualdad se cumplía y el margen no. Ver la decisión 4.

Después de la revisión independiente, con la máquina en reposo:

- **T19, las tres pruebas** (`--filter FullyQualifiedName~SqlOutageTests`): correctas en 18, 26 y 44 s.
- **T16 y T32** (`--filter FullyQualifiedName~RecirculationTests`): las cuatro correctas.
- **`OpcSecurityTests`, `DemoTests` y `LatencySegmentsTests`** con la composición nueva (SignAndEncrypt y barrido): correctas.
- **T12, corrida corta** (fases de 1 minuto y 30 s, revertidas después) con el Outbox Relay y SignAndEncrypt: correcta. 130 jobs `CONFIRMED`, 260 eventos publicados, ninguna falla de servicio.
- **T08 con el plazo nominal**, sin calentamiento, 2026-10-15 07:41: falló, con 2 de 750 jobs sin `CONFIRMED` (`Expected: 750, Actual: 748`).
- **T08 con calentamiento y diagnóstico**, 2026-10-15 07:58:

```
  Correctas CartonPrinting.FaultTests.Hardening.BrokerOutageTests.Ten_minutes_without_rabbitmq_change_nothing_in_printing_and_lose_no_event [12 m 41 s]
 T08 latencia recepción→escritura (ms): antes p50=39 p95=41 p99=43 máx=63; durante p50=39 p95=43 p99=64 máx=141; cajas=750
   ciclo 1539 a 419.0 s del corte: recepción→claim 34 ms, claim→escritura 107 ms, intentos 1
```

T12, corrida completa con la máquina en reposo, terminada el 2026-10-15 a las 06:46 hora local (13:46 UTC):

```
CARTONPRINTING_BENCHMARK_OUT=<scratchpad>/benchmark-t12.json dotnet test tests/CartonPrinting.LoadTests --no-build --filter "Category=Load"
  Correctas CartonPrinting.LoadTests.SustainedLoadTests.Thirty_minutes_at_the_agreed_rate_and_ten_at_double_keep_the_backlog_stable_and_the_deadline_feasible [40 m 18 s]
Pruebas totales: 1
     Correcto: 1
 Tiempo total: 40.4510 Minutos
```

- **Carga:** 3010 cajas, 10 de calentamiento, 1800 a una por segundo y 1200 a dos por segundo. Todas `CONFIRMED` con VERIFIED, una etiqueta cada una. El backlog, muestreado cada segundo, quedó en 0 en ambas fases.
- **Notificación → invocación de la escritura:**
  - a la tasa pactada: p50 20,6 ms, p99 59,9 ms y máximo 267 ms;
  - al doble: p99 61,1 ms.
- **Desigualdad de factibilidad (PLC_TIMESTAMP):** 110 + 60 + 250 + 50 + 37 = 507 ms < 1500 ms.
  - `SendMarginMs` 250 ≥ 0 + 200.
  - `DeviceLeadTimeMs` 400 ≥ 37 medido.
- **OPC UA:** publicación pedida en 20 ms y negociada en 100 ms, muestreo en 10 ms.

`docs/BENCHMARK.md` se generó con `py build/benchmark_report.py <scratchpad>/benchmark-t12.json --fecha "2026-10-15 13:46 UTC (40 min 18 s)"`.

### Pruebas de la fase

{PRUEBAS}

### Revisión independiente

Subagente `revisor-adversarial` sobre `4b06425` (2026-10-15). No ejecutó `dotnet build` ni `dotnet test`, porque la compuerta de la fase corría en la misma máquina y sus pruebas comparan latencias. Ejecutó el verificador, la revisión de secretos y la trazabilidad de la fase, y contrastó `docs/BENCHMARK.md` con el JSON de la corrida. Resultado: 1 ALTA, 6 MEDIA y 10 BAJA.

La compuerta de `4b06425` corría a la par y se detuvo para corregir. Antes de detenerla había pasado las pruebas sin carga: 171, 99 y 102, entre ellas T08, T16, T19 y T32. Esa corrida no cuenta como evidencia de la fase, porque el código cambió después.

| Hallazgo | Severidad | Resolución |
| --- | --- | --- |
| A1: la falla cerrada se decidía al procesar el registro, no al recibirlo. Con SQL colgado (sin cortar conexiones), los ciclos encolados durante la caída se registraban al volver SQL, algunos con job e impresión, sin hueco | Alta | Decisión 7. Pruebas `Sql_that_stops_answering_with_pooled_connections_…` (proxy congelado, pool y tiempos por defecto) y `A_cycle_received_during_the_outage_and_processed_after_sql_returns_is_part_of_the_gap` (la prueba entrega al consumidor, después de la recuperación, ciclos recibidos durante la caída) |
| M1: `LabelingReady` no dependía de la ingesta: con TX-A sin respuesta y lecturas que funcionan seguía en verdadero; con SQL colgado el latido quedaba bloqueado; al volver subía con la primera lectura, sin la recarga | Media | Decisión 7. La prueba anterior incluye un latido que lee SQL sin el proxy: en falso mientras la ingesta tiene la caída declarada, en verdadero después de la recarga. T19 comprueba además el gauge `labeling_ready` en cada muestra de la caída |
| M2: T16 no ejercitaba el bloqueo de rango ni el reintento del 1205 | Media | Pruebas `Two_cycles_of_the_same_carton_registered_at_once_end_with_one_job_after_a_deadlock_retry`, con el 1205 forzado (contador `SqlDb.DeadlockRetries`), y `A_snapshot_arrival_and_a_new_cycle_of_the_same_carton_at_once_end_with_one_job` (TX-A contra TX-E) |
| M3: T08 usaba un plazo de 5 s y una tolerancia de p95 amplia; la corrida registrada tuvo un máximo de 3941 ms durante la caída | Media | Decisión 9. La primera corrida con el plazo nominal falló: 2 de 750 jobs sin `CONFIRMED`, fuera de la caída (la espera de los jobs de la caída se había cumplido), y la prueba no decía cuáles. La causa probable es el arranque en frío de las primeras cajas, que con un plazo efectivo de unos 800 ms ya no alcanza. No quedó demostrado. Se agregaron diez cajas de calentamiento fuera de la medición y un diagnóstico antes de las aserciones (las cajas sin `CONFIRMED` y las cinco más lentas de la caída). En la segunda corrida, todas `CONFIRMED`. La pausa de 3,9 s de la corrida anterior no se repitió |
| M4: `docs/BENCHMARK.md` decía "la composición del EdgeHost" sin barrido, sin Outbox Relay y con OPC UA sin cifrado; no tenía la sección de errores y escenarios excluidos | Media | Decisión 8 |
| M5: pruebas dadas por pasadas sin evidencia registrada; la corrida de T08 era anterior al commit que cambió la fábrica de conexiones de RabbitMQ | Media | `TEST_STATUS.md` se llena con la compuerta final de la fase, que corre T08 y T12 sobre el código final. El registro dice qué corrida respalda cada resultado |
| M6: `FAT_CHECKLIST.md` no decía que el EdgeHost no tiene entrada de evidencia | Media | Nueva sección 0, de prerrequisitos que el MVP no implementa: la integración de evidencia y la compuerta física |
| BAJA 1: el EdgeHost no pasaba la señal a TX-E | Baja | `StationServicesHostedService` pasa `ReadyJobSignal` a `ErpArrivalService` |
| BAJA 2: el registro de tres métricas quedaba entre `writeTs` y la escritura | Baja | Se registran después de invocar la escritura (también si falla) |
| BAJA 3: SI-17 afirmaba que en TX-A la detección nueva es siempre la última; resumen duplicado | Baja | SI-17 corregido (el reingreso conserva su `ReceivedAt` y sigue habiendo un solo job) y un solo resumen |
| BAJA 4: con `CARTON_ID_REQUIRED` activa y una detección sin `CartonId`, TX-E crea el job y TX-A la bloquea | Baja | Registrado como extensión de SI-04 (clase B, abierto): D08 no tiene un motivo para ese caso. No se rodea en el código |
| BAJA 5: la variante de T16 del catálogo es inalcanzable | Baja | SI-19 (clase A), con el parche propuesto; pendiente de aprobación. La prueba ejercita el escenario alcanzable |
| BAJA 6: con una época nueva después de la caída, los ciclos descartados no abren hueco | Baja | Registrado en `FAT_CHECKLIST.md` (sección 3) y en los riesgos: solo los cuenta `opc_records_rejected_total{cause=SQL_UNAVAILABLE}` |
| BAJA 7: endpoint OPC en los logs; contraseña en la línea de comandos de la demo; TLS sin comprobar revocación | Baja | Los logs nombran la estación, no el endpoint. La demo toma la cadena de conexión de una variable de entorno. `RabbitMqTlsOptions.CheckCertificateRevocation`, activa por defecto, en `FAT_CHECKLIST.md` |
| BAJA 8: las marcas de recepción se vaciaban todas al llegar a 10 000 | Baja | Se descartan las más antiguas |
| BAJA 9: T19 afirmaba al menos un rechazo, no 25 | Baja | Exactamente 25 (y 20 y 5 en las variantes) |
| BAJA 10: T08 no fuerza una entrega duplicada | Baja | La deduplicación por `InboxMessage` la ejercita T14 (Fase 1). T08 comprueba que cada `EventId` se aplica una vez |

Mutaciones ejecutadas después de las correcciones, cada una revertida:

- **Sin la regla de recepción (solo el estado declarado):** la variante con el proxy congelado sigue pasando, porque el canal se vacía durante la caída. La prueba que entrega los ciclos después de la recuperación falla: 4 detecciones de 502 a 505.
- **Sin `UPDLOCK, HOLDLOCK` sobre `CartonDetection`:** falla la prueba concurrente de T16, porque no hay interbloqueo que reintentar. Con el rango de `PrintJob` solo, el resultado igual es un job.

### Lo que no se verificó

- **Ningún equipo real.** PLC, impresora, aplicador, escáner y ERP son simuladores; las garantías físicas y la aceptación están en `docs/FAT_CHECKLIST.md`.
- **T08 y T19** simulan la caída con un proxy TCP, no deteniendo el broker ni el servidor de SQL. En T08 el consumidor no pierde su conexión.
- **T12** es una hipótesis declarada: una sola máquina, red local, SQL Server en contenedor y simuladores sin tiempos físicos. El PLC simulado negocia la publicación en 100 ms. No mide la capacidad de una impresora o un aplicador reales (G12).
- **TLS de RabbitMQ:** se prueba la configuración de la conexión (`RabbitMqTlsTests`), no una conexión TLS real contra un broker con certificados.
- **OPC UA con SignAndEncrypt:** se prueba contra el PLC simulado con certificados autofirmados en directorios temporales; la PKI de planta (emisor, revocación) no se probó.
- **La revisión de secretos** busca patrones conocidos (certificados, claves privadas, contraseñas literales, credenciales en Uri de RabbitMQ). No reemplaza una herramienta de detección de secretos.
- **La demo** hace de integración de evidencia (el EdgeHost no tiene una propia) y conecta el PLC simulado sin cifrado.
- **SQL Server sin respuesta** se simula con el proxy congelado. No se probó cuánto tarda SqlClient en soltar una conexión que no responde ni una caída de red real. La ingesta no depende de eso, porque abandona el intento a los 1 s.
- **Logs.** Los mensajes del EdgeHost ya no nombran el endpoint OPC, pero el texto de una excepción del cliente OPC UA o de SqlClient puede incluir la dirección del servidor. No se revisaron todas.

### Riesgos

- Las pruebas que comparan latencias (T08 y `LatencySegmentsTests`) dependen de que la máquina no esté cargada. T08 corre sola; las demás tienen calentamiento y márgenes amplios.
- La compuerta completa con `--carga` tarda más de una hora.
- **Falla cerrada.** Una lectura lenta del latido (más de `SqlAttemptTimeoutMs`) declara la caída. Hasta que la vigilancia recarga el estado, en alrededor de 1 s, los ciclos que llegan son hueco: es una falla cerrada más sensible que la de antes.
- **Caída de SQL Server y reinicio del PLC a la vez.** Si el ciclo siguiente a la caída llega con una época nueva, los ciclos descartados no quedan como hueco de la época anterior. Solo los cuenta `opc_records_rejected_total{cause=SQL_UNAVAILABLE}` (revisión, BAJA-6; en `docs/FAT_CHECKLIST.md`).
