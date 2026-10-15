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

