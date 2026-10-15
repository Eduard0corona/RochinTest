# Implementación completa de CartonPrinting con Claude Code

## 0. Parámetros de la sesión

Edita estos valores antes de empezar. Si la sesión se reinicia, vuelve a dar este archivo: el estado vive en el repositorio.

| Parámetro | Valor | Significado |
| --- | --- | --- |
| `MODO` | `CONTINUO` | `CONTINUO`: al cerrar una fase con su compuerta en verde, sigue con la siguiente sin esperar. `POR_FASE`: al cerrar cada fase, entrega el reporte y espera aprobación. |
| `FASE_INICIAL` | `AUTO` | `AUTO`: continúa desde la primera fase sin la etiqueta `fase-N-ok` en git. Un número fuerza esa fase. |
| `PERFIL_EXTENDIDO` | `NO` | `SI` solo si OT confirmó que exige la historia completa de ciclos (OT-14). Con `NO`, la Fase 6 queda como `NO_APLICA`. |

La persona responsable autoriza, con este archivo, avanzar de fase según `MODO`. Esto sustituye la regla de `spec/prompts/INICIO_CODEX.md` que exige revisión humana antes de la Fase 1. Todo lo demás de `CLAUDE.md` y de `spec/AI_CONTEXT.md` sigue vigente.

## 1. Objetivo y límites

Implementar el MVP especificado en `spec/` por fases (0 a 7 de `spec/docs/PLAN_IMPLEMENTACION.md`), con cada prueba T01 a T47 implementada como prueba automatizada, ejecutada y registrada con evidencia.

Dentro del alcance:

- La solución .NET 10 completa: los módulos de `spec/docs/ARQUITECTURA_TECNICA.md`, los simuladores, las pruebas, `docker-compose.yml`, CI y documentación.
- Ejecutar el DDL de `spec/database/` en SQL Server real (contenedor) por primera vez.

Fuera del alcance:

- Conectar PLC, impresoras, aplicadores, escáneres, ERP o redes de planta reales.
- La aceptación en planta (FAT o SAT) y cualquier afirmación sobre efectos físicos reales.
- Inventar capacidades de hardware. Una capacidad no confirmada se implementa detrás de una interfaz con su simulador; la columna "Qué puede avanzar sin resolverla" de `spec/docs/DECISIONES_ABIERTAS_OT.md` dice qué suponer.
- Hacer `git push`.

## 2. Antes de la primera fase

1. Lee `CLAUDE.md`, `spec/AI_CONTEXT.md` (ya importado), `spec/docs/GUARDRAILS.md`, `spec/docs/PLAN_IMPLEMENTACION.md` y `spec/prompts/INICIO_CODEX.md`.
2. Comprueba el entorno y registra cada versión en `docs/IMPLEMENTATION_LOG.md`:
   - `py spec/tools/verify_package.py` termina con `15 grupos OK, 0 errores`. Si no, detente: la especificación no está íntegra.
   - `git status` está limpio; la primera entrada de `spec/CHANGELOG.md` es 1.1.2.
   - `dotnet --version` es 10.x; `docker info` responde con contenedores Linux; `py --version` es 3.10 o mayor.
   - Lo que falte se informa a la persona con el comando exacto para instalarlo. No lo instales sin pedirlo.
3. Si `FASE_INICIAL` es `AUTO`, lee `docs/IMPLEMENTATION_LOG.md`, `docs/TEST_STATUS.md` y las etiquetas `fase-N-ok` para decidir dónde continuar. Una fase sin etiqueta se considera abierta aunque tenga código.

## 3. Ciclo de cada fase

1. **Plan.** Escribe en `docs/IMPLEMENTATION_LOG.md` un plan breve de la fase: entregables, archivos, pruebas Txx que la cierran (tabla de `spec/docs/PLAN_IMPLEMENTACION.md`), decisiones y riesgos. Mantén una lista de tareas de la fase.
2. **Rama.** Crea la rama de la fase desde `main`, con los nombres del plan: `feat/foundation`, `feat/sql-idempotency`, `feat/opc-ingest`, `feat/erp-deadline`, `feat/print-agent`, `feat/ownership-recovery`, `feat/extended-profile`, `test/hardening`.
3. **Pruebas primero.** Para cada Txx de la fase, escribe primero sus pruebas xUnit con `[Trait("Spec", "Txx")]`, con el criterio de aprobación del catálogo (`spec/tests/test-catalog.json`) y nunca uno más débil. Cada prueba afirma también los estados, eventos publicados y métricas del catálogo, no solo que "no lanzó excepción".
4. **Implementación.** Commits pequeños con mensajes en español.
5. **Compuerta.** Debe pasar todo esto:
   - `dotnet build -warnaserror` desde la raíz.
   - `dotnet test --filter "Category!=Load"`, completo: las pruebas de fases anteriores siguen en verde. Las de carga (`[Trait("Category", "Load")]`) solo corren en la Fase 7.
   - `py spec/tools/verify_package.py` y `py spec/tools/generate_artifacts.py --check`.
   - `py build/check_traceability.py`.
6. **Revisión independiente.** Invoca al subagente `revisor-adversarial` sobre la rama de la fase. Corrige los hallazgos de severidad alta y media; los de severidad baja se corrigen o se registran con su motivo. Si corregiste algo de severidad alta, vuelve a invocarlo.
7. **Registro.** Completa en `docs/IMPLEMENTATION_LOG.md` lo hecho, los comandos con su salida resumida (el número de pruebas que pasaron y las que fallaron), las decisiones y su porqué, los hallazgos del revisor y su resolución, lo que no se verificó y los riesgos. Actualiza `docs/TEST_STATUS.md` y `CHANGELOG.md`.
8. **Cierre.** Merge sin fast-forward a `main` y etiqueta `fase-N-ok`. Con `MODO = POR_FASE`, entrega el reporte de la sección 7 y espera.

Si la compuerta no pasa después de corregir lo que se pueda, la fase no se cierra: aplica la sección 6.

## 4. Fases

### Fase 0 · Fundación (T23, T28)

- Solución en la raíz con los proyectos de la sección 1 de `spec/docs/ARQUITECTURA_TECNICA.md`, `src/EdgeHost` (Worker Service) y `src/CartonPrinting.Simulators`.
- Proyectos de prueba en `tests/`: `CartonPrinting.UnitTests`, `CartonPrinting.IntegrationTests` (Testcontainers con SQL Server 2022 y RabbitMQ), `CartonPrinting.FaultTests` y `CartonPrinting.LoadTests` (vacío hasta la Fase 7).
- `Directory.Build.props` (net10.0, nullable, warnings como errores) y `Directory.Packages.props` con versiones fijas. Cada paquete con su versión, en el registro.
- `docker-compose.yml` con SQL Server 2022 (tag fijo) y RabbitMQ con healthchecks. Dos logins: migraciones (DDL) y aplicación (lectura y escritura, sin DDL y con `DENY INSERT, UPDATE, DELETE` sobre `dbo.AllowedTransition`).
- Ejecutor de migraciones en `CartonPrinting.Infrastructure`. Lee `spec/database/sqlserver_schema.sql` y después `spec/database/sqlserver_state_transitions.sql`, y divide los lotes por las líneas `GO`. No copia el DDL.
- `CartonPrinting.Contracts`:
  - Tipos de `CartonDetected` y `PrintJobResult`.
  - UUIDv5, idempotencyKey y huella JCS.
  - Las reglas de negocio de `spec/tools/verify_package.py` (`business_errors`) en C#.
- Pruebas de contratos (T23):
  - Cada ejemplo de `spec/contracts/examples/valid` pasa el schema (con una biblioteca JSON Schema 2020-12 para .NET) y las reglas en C#.
  - Cada ejemplo de `invalid` falla por la regla que declara `EXPECTED.json`.
  - Cada ejemplo de `business-invalid` pasa el schema y falla exactamente la regla de negocio declarada.
  - La huella y los `eventId` calculados en C# son iguales a los de los ejemplos.
- OpenTelemetry base y health checks.
- `build/ci.ps1` y `build/ci.sh` con la compuerta completa. `build/check_traceability.py` lee `spec/tests/test-catalog.json`, `spec/docs/PLAN_IMPLEMENTACION.md` y `docs/TEST_STATUS.md`. Falla si una Txx de una fase con etiqueta `fase-N-ok`, o de la fase en curso, no tiene ninguna prueba con su `Trait`. Las excepciones son dos: las marcadas `NO_APLICA` en `docs/TEST_STATUS.md` y T28, que se cumple con el paso de CI que corre `spec/tools/verify_package.py`. Workflow de CI para `ubuntu-latest` en `.github/workflows/ci.yml`.
- `README.md` en la raíz, `docs/IMPLEMENTATION_LOG.md`, `docs/TEST_STATUS.md` (T01 a T47, todas `NO_EJECUTADA` salvo las de la fase), `docs/SPEC_ISSUES.md` y `CHANGELOG.md`.

### Fase 1 · Datos e idempotencia lógica (T01, T02, T10, T14, T47)

- Capa de datos con `Microsoft.Data.SqlClient` y Dapper o ADO.NET, una clase por transacción. Las sentencias se toman de `spec/database/sqlserver_operations_reference.sql`; si alguna no ejecuta, aplica la sección 6.
- TX-A con el serial obtenido antes, inserción primero, captura de errores por nombre de restricción, reintento de 1205, Outbox en la misma transacción.
- Outbox Relay con publisher confirms y `message-id` igual a `EventId`; un consumidor de prueba con `InboxMessage`.
- T47 es la primera ejecución real del esquema:
  - Cada sentencia de referencia con 0 filas y con filas afectadas.
  - Cada transición prohibida, y cada manual, excepcional o de corrección sin su respaldo o con un respaldo anterior: deben fallar con el código que les asigna `spec/database/sqlserver_schema.sql` (`THROW` 51001 a 51032) o con 52001 en TX-X. Las pruebas comprueban el código exacto, no solo que hubo un error.
  - Defecto conocido: el criterio de T47 en el catálogo dice "51010 a 51032", pero los códigos van de 51001 a 51032, más 52001. Regístralo en `docs/SPEC_ISSUES.md` como clase A y usa el rango real.
  - Cambiar solo `Reason`, retroceder `PrinterSendOrder`, editar `PlcEpochHistory`, desbloquear una estación sin `OperatorAction`.
  - Lo que falle aquí probablemente es un defecto de la especificación: aplica la sección 6.

### Fase 2 · Ingesta OPC (T11, T13, T24, T25, T26, T30, T35, T45)

- Abstracción de la fuente de ciclos con dos implementaciones:
  - el cliente real con la pila OPC UA .NET Standard de OPC Foundation;
  - un simulador en `CartonPrinting.Simulators`, que expone el registro latched como servidor OPC UA en proceso.
- Las pruebas de integración usan el cliente real contra ese servidor simulado: así se ejercitan StatusCode, Overflow, primera notificación (`READ`), Republish y reconexión.
- Clasificación de la sección 9.2 del documento principal y del diagrama `spec/diagrams/08_clasificacion_secuencia.mmd`: duplicados, colisiones, tardíos, reinicio sospechado, disminución ambigua, huecos, épocas, rebote. Incluye `SequenceAnomaly`, `SequenceGap`, `PlcEpochHistory` y las reglas de plazo de `GATEWAY_RECEIPT`.
- FIFO del PLC simulada para T11. El journal y `AckCycleId` son de la Fase 6.

### Fase 3 · ERP y plazo (T03, T04, T17, T18, T21, T33, T37, T46)

- `ErpSnapshotSync` asíncrono con timeout y circuit breaker; simulador de ERP con latencia y caídas configurables.
- `WAITING_ERP`, TX-E (D06 y D08).
- Plazo y margen efectivos (congelado frente a revisión activa), `DeviceLeadTimeMs`, compuerta física con la regla más restrictiva.
- Detector de saltos de reloj con `TimeProvider`; barrido TX-S por estación.
- Activación de `StationConfigRevision` con dos personas.

### Fase 4 · Print Agent (T05, T06, T07, T15, T22, T29, T31, T38, T39, T42, T44)

- **Simulador de impresora TCP.** Rechaza conexiones; corta a mitad de la escritura; acepta sin responder; retiene en cola; reporta completado o cancelación por job, según las capacidades declaradas; cuenta los bytes recibidos por job, que es el criterio de T05.
- **`PrinterSenderService`** por impresora, con una sola conexión:
  - claim con `PrinterSendOrder` y `OUTPUT INTO`;
  - segunda comprobación monotónica justo antes de escribir;
  - TX-C con la regla de `NOT_SENT`.
- **Simulador de evidencia** (impresora, aplicador y escáner correlacionados por ciclo). TX-D con `EvidenceObservation` en su propia transacción, J10 a J12, J15 y la corrección J18.
- Puntos de falla inyectables para matar el proceso en los tres momentos de T07 y para suspender el sender (T29).

### Fase 5 · Propiedad, recuperación y operación (T27, T34, T36, T40, T41, T43)

- **Propiedad de la impresora.** Lock de sistema operativo por impresora (mutex con nombre en Windows, `flock` en Linux) y `PrinterLease`.
- **Arranque.** En el orden de `spec/docs/ESTRATEGIA_RECUPERACION.md`: TX-R0 con X03 y vaciado de cola simulado.
- **Operations API.** ASP.NET Core con autenticación (en desarrollo, un esquema simple con la identidad en claims; ningún proveedor real). Cada acción inserta su `OperatorAction`. Ningún endpoint envía bytes a una impresora.
- **Reimpresión excepcional.** La autoriza la API y la ejecuta el sender de la `ManualPrinterId`.
- **T36.** Dos procesos y un proxy TCP que solo deja pasar al dueño activo. La corrida sin proxy debe mostrar las dos transmisiones: así se demuestra el límite del lease.

### Fase 6 · Perfil extendido (T09, T20)

Solo con `PERFIL_EXTENDIDO = SI`:

- FIFO y `AckCycleId` en el simulador del PLC.
- Journal con `Microsoft.Data.Sqlite`, WAL y `synchronous = FULL`, creado desde `spec/database/sqlite_journal.sql`. El replay conserva el `ReceivedAt` original.
- Matar el proceso no es un corte de energía: regístralo como límite de T09.

Con `NO`, marca T09 y T20 como `NO_APLICA (OT-14 abierta)` y crea la etiqueta `fase-6-ok` sin código.

### Fase 7 · Hardening y demo (T08, T12, T16, T19, T32)

- **Fallas.** Suite completa de inyección de fallas, con RabbitMQ y SQL detenidos a mitad de carga.
- **T12.** Carga con simuladores en `CartonPrinting.LoadTests`, con la duración y la tasa del catálogo. Se corre aparte: `dotnet test --filter "Category=Load"`. Reporte en `docs/BENCHMARK.md` con:
  - hardware, intervalos OPC negociados, tasa y duración;
  - p50, p95, p99 y máximo de cada tramo de la sección 10.4 del documento principal;
  - la desigualdad de factibilidad evaluada.
  - El reporte declara que no valida tiempos físicos.
- **Seguridad.** OPC UA con SignAndEncrypt en la configuración, secretos fuera del repositorio y RabbitMQ con TLS configurable.
- **Demo y FAT.** `docs/DEMO.md` con el guion para correr el flujo completo. `docs/FAT_CHECKLIST.md` con lo que solo la planta puede aceptar: las filas físicas de `spec/docs/MATRIZ_GARANTIAS.md` y las decisiones OT abiertas.

## 5. Formato de `docs/TEST_STATUS.md`

Una fila por prueba de T01 a T47, con estas columnas:

| Txx | Fase | Estado | Pruebas xUnit | Entorno | Comando y fecha | Notas |
| --- | --- | --- | --- | --- | --- | --- |

- **Estado:** `PASA`, `FALLA`, `NO_EJECUTADA` o `NO_APLICA` (con motivo).
- **Entorno:** `SQL Server real`, `simulador` o `proceso`.

Una prueba cuyo criterio incluye efectos físicos se mide en el simulador, y la nota lo dice.

## 6. Defectos de la especificación y condiciones de paro

Defectos de la especificación: sigue la clasificación A y B de `CLAUDE.md`. Para la clase A, pide la aprobación de la edición en `spec/`. Para la clase B, detén solo el trabajo afectado y sigue con lo que no dependa de él.

Detente y pregunta a la persona cuando:

- haya un defecto de clase B;
- una compuerta siga en rojo tras corregir lo que se puede corregir sin cambiar la especificación;
- una prueba solo pase debilitando su criterio, o rodeando un trigger o una restricción;
- algo requiera equipo real, red de planta, credenciales o una decisión de OT sin valor por defecto documentado;
- falte una herramienta del entorno.

Al detenerte, deja el repositorio compilando, con el registro al día y una pregunta concreta con las opciones.

## 7. Reporte de cierre de fase, y final

Al cerrar cada fase (y al final, para todo el proyecto), entrega:

- Qué quedó hecho y en qué commit o etiqueta.
- Las pruebas Txx con su estado y la salida resumida de la compuerta.
- Los hallazgos del revisor y cómo se resolvió cada uno.
- Los defectos de la especificación registrados, con su clase y su estado.
- Lo que no se verificó y los riesgos que quedan.
- La siguiente fase, y qué necesita de la persona o de OT.

El reporte final incluye además la tabla completa de `docs/TEST_STATUS.md` y la lista de lo que solo la aceptación en planta puede demostrar.
