# Defectos y vacíos de la especificación

Clasificación de `CLAUDE.md`:

- **Clase A:** impide ejecutar algo sin cambiar ninguna regla (sintaxis, orden de objetos, un nombre o tipo inconsistente entre archivos). Se registra con la evidencia y el parche mínimo, se pide la aprobación de la edición en `spec/` y se aplica en un commit aparte `spec: …`.
- **Clase B:** cualquier cambio de comportamiento. Se registra y se detiene el trabajo afectado hasta que la persona decida. Nunca se rodea en el código.

| ID | Clase | Fase | Archivo | Estado |
| --- | --- | --- | --- | --- |
| SI-01 | A | 1 | `spec/tests/test-catalog.json` (T47) y `spec/tests/MATRIZ_PRUEBAS.md` | Resuelto: edición aprobada y aplicada en `82b1e5b` (spec 1.1.3) |
| SI-02 | B (observación) | 0 | `spec/contracts/*.schema.json` y `spec/tools/verify_package.py` | Abierto: ningún trabajo afectado |
| SI-03 | Vacío de herramientas | 0 | `spec/tools/verify_package.py` (requisito "Python 3.10+") | Abierto: CI fijada en Python 3.14 |
| SI-04 | B (vacío) | 1 | `docs/ADR 0013`, sección 12 del documento principal | Abierto: camino detenido en falla cerrada hasta la decisión |
| SI-05 | B (contradicción entre fuentes) | 1 | `spec/tools/verify_package.py` frente a `contracts/state-machine.json` (D04) | Abierto: gana la máquina de estados |
| SI-06 | Observación | 1 | `spec/contracts/state-machine.json` (G02 en TX-A) | Abierto: rama defensiva en el barrido, sin cambio de comportamiento |
| SI-07 | B (vacío de guarda) | 1 | `spec/database/sqlserver_schema.sql` (`TR_PrintJob_StateMachine`, rama CORRECTION) | Resuelto: la persona eligió endurecer el trigger; ADR 0016 en `5eb9695` (spec 1.1.3) |
| SI-08 | B (vacío de guarda) | 1 | `spec/database/sqlserver_schema.sql` (`TR_PlcEpochHistory_Guard`) | Abierto: el código inserta una acción por confirmación |
| SI-09 | B (vacío) | 1 | `docs/ARQUITECTURA_TECNICA.md` (plazo efectivo tras cambiar la revisión) | Abierto: comportamiento interino de falla cerrada |

## SI-01 · Rango de códigos de error en el criterio de T47

- **Evidencia.** `spec/tests/test-catalog.json`, T47, `expectedStates`: "todo intento indebido falla con su error (51010 a 51032)". `spec/database/sqlserver_schema.sql` define `THROW` con los códigos 51001 a 51032 (por ejemplo 51001 en `TR_StationConfigRevision_Immutable`, 51004 y 51005 en `TR_PrintJob_Immutable`), y `spec/database/sqlserver_operations_reference.sql` usa 52001 en TX-X cuando la autorización no es ejecutable.
- **Impacto.** Ninguno sobre el comportamiento: el texto del criterio es más estrecho que el DDL. No impide ejecutar T47.
- **Qué se hace mientras tanto.** Las pruebas de T47 comprueban el código exacto que cada `THROW` del DDL asigna (51001 a 51032) y 52001 en TX-X, sin cambiar ninguna regla.
- **Resolución (2026-10-14).** La persona aprobó la edición; se aplicó en el commit `82b1e5b` (`spec: SI-01 …`) con los artefactos regenerados y el verificador en verde.
- **Parche aplicado.** En `spec/tests/test-catalog.json`, T47, `expectedStates`: "(51010 a 51032)" → "(51001 a 51032, o 52001 en TX-X)". Después, `py spec/tools/generate_artifacts.py` (regenera `tests/MATRIZ_PRUEBAS.md`), `py spec/tools/update_checksums.py` y `py spec/tools/verify_package.py`.

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
- **Trabajo afectado detenido.** Como corresponde a un defecto de clase B, ese camino no se implementa con una salida propia: queda en falla cerrada. Un registro que de otro modo sería candidato a job queda `BLOCKED` con `INCOHERENT_SNAPSHOT` (el registro no trae un dato que la revisión declara obligatorio y sin él no se puede validar la recirculación), no imprime y nunca se inventa un `CartonId` (I07). Elegir `INCOHERENT_SNAPSHOT` es una decisión interina de la implementación (el motivo de bloqueo más cercano a "registro incompleto"), no algo que diga la especificación. El interino no pasa por encima de otras clasificaciones: un registro sin plazo confiable sigue D04 (`EXPIRED` con `LATE_ARRIVAL`) y uno que la secuencia ya bloqueó conserva su motivo (`ReferenceCoverageTests.Review2_b5_…`).
- **Por qué clase B.** Elegir el motivo definitivo (u otra salida, por ejemplo esperar un lector) es una decisión de comportamiento. Pendiente de la persona. El resto de las fases no depende de este caso borde.

## SI-05 · Registro `GATEWAY_RECEIPT` en vivo que abre un hueco: regla de negocio del verificador

- **Contradicción.** `spec/contracts/state-machine.json` (D04), el invariante I26 y la descripción de `deadlineAt` en `carton-detected.v1.schema.json` dicen que un registro `GATEWAY_RECEIPT` que abre un hueco de secuencia no tiene plazo y se registra con `deadlineAt = receivedAt`, aunque llegue por `SUBSCRIPTION` sin overflow. `business_errors` de `spec/tools/verify_package.py` no puede ver el hueco: para ese registro (en vivo y sin overflow) exige `deadlineAt` posterior a `receivedAt` y lo marca con "deadlineAt no es posterior a su referencia temporal".
- **Resolución aplicada (regla del proyecto).** Gana la máquina de estados: el registro se publica con `deadlineAt = receivedAt` y queda `EXPIRED` con `LATE_ARRIVAL`. La validación del contrato antes de escribir el Outbox tolera solo esa regla y solo para ese caso (`OutboxWriter.InsertCartonDetectedAsync`, `opensGapWithoutDeadline`); todas las demás reglas se siguen exigiendo.
- **Parche propuesto en la especificación (clase B, porque cambia qué documentos acepta el verificador).** Que `business_errors` acepte `deadlineAt == receivedAt` para un registro `GATEWAY_RECEIPT` en vivo (el único caso legítimo es el hueco), o que el contrato declare con un campo que el registro abrió un hueco. Pendiente de la persona.

## SI-06 · G02 también en el barrido (rama defensiva)

- **Evidencia.** `state-machine.json` asigna G02 a `TX-A` ("La última detección faltante del rango se registra").
- **Qué hace la implementación.** TX-A aplica G02. Además, el barrido de huecos (`TxsSweep.CloseGapsAsync`) cierra como `RECOVERED` un hueco cuya gracia venció si todos sus ciclos ya tienen detección: es el único estado válido con 0 ciclos faltantes (`CK_SequenceGap_Close`). La segunda revisión no reprodujo un hueco que quedara `OPEN` con todos sus ciclos (20 rondas concurrentes, y una con superposición forzada): la rama es defensiva y no se atribuye a una carrera demostrada.
- **D07.** La primera versión de este registro también incluía D07 en TX-E. Era una elección de la implementación, no un defecto: TX-E ya no lo aplica (un snapshot tardío deja la detección en `WAITING_ERP` sin job y el barrido aplica D07, como dice la máquina de estados).
- **Impacto.** Ninguno sobre estados, motivos ni contratos. Pendiente de la persona solo si quiere que la especificación nombre la rama defensiva.

## SI-07 · La guarda CORRECTION no exige que la lectura sea posterior al claim

- **Evidencia.** En `TR_PrintJob_StateMachine`, la rama `CORRECTION` (J18) exige una `EvidenceObservation` `VERIFIED` del ciclo con `ReadSerial <> LabelSerial`, sin condición sobre su `ReceivedAt`. Para confirmar, `TR_PrintAttempt_Guard` sí exige una observación recibida después del claim (I06).
- **Riesgo.** Una lectura registrada antes de que existiera una etiqueta del job (por ejemplo, la etiqueta de otra caja) bastaría al trigger para pasar a `UNKNOWN` con `SERIAL_MISMATCH` un job ya confirmado. El error es hacia el lado restrictivo (alarma falsa, no confirmación falsa).
- **Efecto en T47.** El criterio de T47 pide que toda corrección "con un respaldo anterior" falle con su error. Una corrección respaldada por una lectura registrada antes del claim no falla: el subcriterio no se cumple y T47 figura como `FALLA` en ese punto (`ReferenceCoverageTests.Si_07_documents_that_the_trigger_accepts_…` fija el comportamiento actual del DDL).
- **Qué hace la implementación.** Aplica J18 tal como lo define la máquina de estados: la observación se aplica en cuanto se registra ("llega una lectura"), con el job `CONFIRMED` o en `UNKNOWN` por otra causa. Una primera versión agregó en el código la condición "lectura posterior al primer claim"; la segunda revisión mostró que no protegía el caso que la motivaba (una lectura vieja aplicada después de una reimpresión confirmada) y que, al comparar relojes de pared, podía descartar una contradicción real tras un salto del reloj hacia atrás. Se retiró: rodear un defecto de clase B en el código no corresponde.
- **Trabajo detenido.** La reaplicación diferida de observaciones (por ejemplo, reaplicar al arrancar las que quedaron registradas sin aplicar porque el proceso cayó entre las dos transacciones de TX-D) depende de esta decisión y no se implementa hasta que la persona decida.
- **Resolución (2026-10-14).** La persona eligió endurecer el trigger. ADR 0016 (`spec/docs/adr/0016-correccion-con-lectura-posterior.md`, commit `5eb9695`): la rama `CORRECTION` exige una lectura recibida en o después del último `ClaimedAt` del job y con `ObservationId` mayor que cualquier `ConfirmingObservationId` del job; cambian la guarda de J18, el mecanismo de I25 y la sección 11. TX-D aplica la misma regla. Pruebas: `ReferenceCoverageTests.Correction_backed_by_a_read_older_than_the_claim_is_rejected` y `…_older_than_the_confirming_observation_is_rejected` (fallan con el DDL anterior). La reaplicación diferida de observaciones deja de estar detenida.

## SI-08 · `TR_PlcEpochHistory_Guard` acepta una `OperatorAction` `CONFIRM_EPOCH` reutilizada

- **Evidencia.** La guarda exige que `OperatorActionId` apunte a una acción `CONFIRM_EPOCH` de la misma estación, pero no que sea nueva ni que no respalde ya otra época. I24 pide que cada confirmación de época quede respaldada por una acción registrada; con la guarda actual, una sola acción podría respaldar varias épocas.
- **Qué hace la implementación.** `TxmManualActions.ConfirmEpochAsync` inserta una acción nueva en la misma transacción que la época; nunca reutiliza una.
- **Parche propuesto (clase B).** Un índice único filtrado `UX_PlcEpochHistory_OperatorAction ON dbo.PlcEpochHistory (OperatorActionId) WHERE OperatorActionId IS NOT NULL`, o la condición de "acción más reciente" que ya usa `TR_PrintJob_StateMachine`. Pendiente de la persona.

## SI-09 · Plazo efectivo cuando la revisión activa no puede calcularlo

- **Vacío.** El plazo efectivo de un job es el mínimo entre el congelado y el recalculado con la revisión activa ("nunca más permisivo que el congelado"). La especificación no dice qué hacer si la revisión activa no puede dar un plazo confiable para ese registro: por ejemplo, cambió a `PLC_TIMESTAMP` y el registro no trae `DetectionTimestamp`, o cambió a `GATEWAY_RECEIPT` y el registro no llegó en vivo.
- **Comportamiento interino (falla cerrada).** El plazo efectivo es la recepción del registro (`DeadlineAt = ReceivedAt`, la misma regla que I26 aplica al registrar): el job ya no se envía como operación ordinaria y vence en el claim (J03). No imprime de más.
- **Por qué clase B.** Es una decisión de comportamiento (otra lectura posible: conservar el plazo congelado). Pendiente de la persona.
