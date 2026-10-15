# Defectos y vacíos de la especificación

Clasificación de `CLAUDE.md`:

- **Clase A:** impide ejecutar algo sin cambiar ninguna regla (sintaxis, orden de objetos, un nombre o tipo inconsistente entre archivos). Se registra con la evidencia y el parche mínimo, se pide la aprobación de la edición en `spec/` y se aplica en un commit aparte `spec: …`.
- **Clase B:** cualquier cambio de comportamiento. Se registra y se detiene el trabajo afectado hasta que la persona decida. Nunca se rodea en el código.

| ID | Clase | Fase | Archivo | Estado |
| --- | --- | --- | --- | --- |
| SI-01 | A | 1 | `spec/tests/test-catalog.json` (T47) y `spec/tests/MATRIZ_PRUEBAS.md` | Abierto: pendiente de aprobación de la edición |
| SI-02 | B (observación) | 0 | `spec/contracts/*.schema.json` y `spec/tools/verify_package.py` | Abierto: ningún trabajo afectado |
| SI-03 | Vacío de herramientas | 0 | `spec/tools/verify_package.py` (requisito "Python 3.10+") | Abierto: CI fijada en Python 3.14 |
| SI-04 | B (vacío) | 1 | `docs/ADR 0013`, sección 12 del documento principal | Abierto: comportamiento interino de falla cerrada |
| SI-05 | B (contradicción entre fuentes) | 1 | `spec/tools/verify_package.py` frente a `contracts/state-machine.json` (D04) | Abierto: gana la máquina de estados |

## SI-01 · Rango de códigos de error en el criterio de T47

- **Evidencia.** `spec/tests/test-catalog.json`, T47, `expectedStates`: "todo intento indebido falla con su error (51010 a 51032)". `spec/database/sqlserver_schema.sql` define `THROW` con los códigos 51001 a 51032 (por ejemplo 51001 en `TR_StationConfigRevision_Immutable`, 51004 y 51005 en `TR_PrintJob_Immutable`), y `spec/database/sqlserver_operations_reference.sql` usa 52001 en TX-X cuando la autorización no es ejecutable.
- **Impacto.** Ninguno sobre el comportamiento: el texto del criterio es más estrecho que el DDL. No impide ejecutar T47.
- **Qué se hace mientras tanto.** Las pruebas de T47 comprueban el código exacto que cada `THROW` del DDL asigna (51001 a 51032) y 52001 en TX-X, sin cambiar ninguna regla.
- **Parche mínimo propuesto.** En `spec/tests/test-catalog.json`, T47, `expectedStates`: "(51010 a 51032)" → "(51001 a 51032, o 52001 en TX-X)". Después, `py spec/tools/generate_artifacts.py` (regenera `tests/MATRIZ_PRUEBAS.md`), `py spec/tools/update_checksums.py` y `py spec/tools/verify_package.py`.

## SI-02 · Los `pattern` con `$` aceptan un salto de línea final

- **Evidencia (ejecutada en la revisión de la Fase 0).** `"cycleId": "918273\n"` cumple `^(0|[1-9][0-9]*)$` tanto en jsonschema de Python (usa `re.search`, donde `$` coincide antes de un `\n` final) como en JsonSchema.Net (`Regex` de .NET, misma semántica). JSON Schema prescribe la semántica de ECMA-262, donde `$` sin la bandera `m` solo coincide al final de la cadena.
- **Impacto.** El verificador de referencia y esta implementación aceptan, igual, valores con `\n` final en `cycleId`, `siteId`, `stationId`, `idempotencyKey` y `labelType`. Los productores de este repositorio nunca emiten esos valores (los tipos C# serializan `CycleId` desde un `ulong`).
- **Por qué clase B.** Corregirlo (`\z` en los patrones, o validar con semántica ECMA-262) cambia qué documentos acepta el contrato.
- **Qué se hace.** Nada en el código: se conserva la semántica de la referencia, que es el criterio de T23. Decisión pendiente de la persona.

## SI-03 · El oráculo de referencia depende de la versión de Python

- **Evidencia.** `canonical_ts` y el FormatChecker de `spec/tools/verify_package.py` usan `datetime.fromisoformat`, cuyo comportamiento cambia entre versiones admitidas por "Python 3.10+": la 3.10 solo acepta fracciones de 3 o 6 dígitos; la 3.14 (la del entorno local) acepta cualquier cantidad (trunca a microsegundos) y la hora `24:00:00` como medianoche del día siguiente.
- **Impacto.** Un instante en el borde puede ser válido para el verificador en una versión e inválido en otra. Ningún documento producido por este repositorio usa esos bordes (siempre `.fffZ`).
- **Qué se hace.** `CanonicalTimestamp` reproduce la semántica de Python 3.14 y el workflow de CI fija Python 3.14, la versión con la que se ejecutaron las pruebas diferenciales. Pendiente: fijar en la especificación la versión normativa.

## SI-04 · `CARTON_ID_REQUIRED` sin `CartonId` en el registro

- **Vacío.** Con `CartonIdentityMode = CARTON_ID_REQUIRED`, la especificación exige identidad física leída antes de etiquetar (sección 12, ADR 0013) y valida la recirculación con `CartonId`, pero no dice qué hacer con un registro que llega sin `CartonId`.
- **Comportamiento interino (falla cerrada).** La detección queda `BLOCKED` con `INCOHERENT_SNAPSHOT`: el registro no trae un dato que la revisión declara obligatorio, y sin él no se puede validar la recirculación. Nunca se inventa un `CartonId` (I07). Es el comportamiento más restrictivo; no imprime.
- **Por qué clase B.** Elegir un motivo (u otra salida) es una decisión de comportamiento. Pendiente de la persona; no bloquea ninguna fase, porque solo afecta a ese caso borde.

## SI-05 · Registro `GATEWAY_RECEIPT` en vivo que abre un hueco: regla de negocio del verificador

- **Contradicción.** `spec/contracts/state-machine.json` (D04), el invariante I26 y la descripción de `deadlineAt` en `carton-detected.v1.schema.json` dicen que un registro `GATEWAY_RECEIPT` que abre un hueco de secuencia no tiene plazo y se registra con `deadlineAt = receivedAt`, aunque llegue por `SUBSCRIPTION` sin overflow. `business_errors` de `spec/tools/verify_package.py` no puede ver el hueco: para ese registro (en vivo y sin overflow) exige `deadlineAt` posterior a `receivedAt` y lo marca con "deadlineAt no es posterior a su referencia temporal".
- **Resolución aplicada (regla del proyecto).** Gana la máquina de estados: el registro se publica con `deadlineAt = receivedAt` y queda `EXPIRED` con `LATE_ARRIVAL`. La validación del contrato antes de escribir el Outbox tolera solo esa regla y solo para ese caso (`OutboxWriter.InsertCartonDetectedAsync`, `opensGapWithoutDeadline`); todas las demás reglas se siguen exigiendo.
- **Parche propuesto en la especificación (clase B, porque cambia qué documentos acepta el verificador).** Que `business_errors` acepte `deadlineAt == receivedAt` para un registro `GATEWAY_RECEIPT` en vivo (el único caso legítimo es el hueco), o que el contrato declare con un campo que el registro abrió un hueco. Pendiente de la persona.
