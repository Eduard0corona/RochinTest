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
| SI-10 | B (vacío; capacidad de hardware sin confirmar, regla 6 de `AI_CONTEXT`) | 2 | `DOCUMENTO_PRINCIPAL.md` sección 4, ADR 0001 | Abierto (OT-04): la persona decidió mantener el eco de cierre del simulador detrás de `CycleDecoder` |
| SI-11 | A | 2 | `spec/tests/test-catalog.json` (T11) | Resuelto: edición aprobada y aplicada en `8798b85` (spec 1.1.4) |
| SI-12 | B (vacío) | 2 | `DOCUMENTO_PRINCIPAL.md` sección 9.2 ("marca alta reiniciada") | Abierto (OT-04): la persona decidió mantener el interino de falla cerrada |
| SI-13 | B (vacío) | 2 | `DOCUMENTO_PRINCIPAL.md` secciones 4 y 9.2, ADR 0001 (registro incoherente frente a la secuencia) | Abierto: la persona decidió mantener el interino de falla cerrada |
| SI-14 | B (contradicción entre fuentes) | 3 | `spec/database/sqlserver_operations_reference.sql` (J03, J14 y D07 en TX-S) frente a `contracts/state-machine.json` | Abierto: la persona decidió mantener el plazo efectivo (gana la máquina de estados) |
| SI-15 | B (vacío) | 3 | `DOCUMENTO_PRINCIPAL.md` sección 10.2 (`CLOCK_SKEW`) | Abierto (OT): la persona decidió mantener el interino y declararlo |
| SI-16 | B (vacío) | 3 | `DOCUMENTO_PRINCIPAL.md` secciones 10.2 y 645; `docs/ESTRATEGIA_RECUPERACION.md` (trabajo pendiente tras `CLOCK_JUMP`) | Resuelto por decisión de la persona: lo pendiente vence al confirmar el salto |
| SI-17 | B (contradicción entre fuentes) | 7 | `spec/database/sqlserver_operations_reference.sql` (recirculación en TX-E) frente al catálogo de T16 | Resuelto por decisión de la persona: solo cuentan las detecciones anteriores, con la identidad más restrictiva |
| SI-18 | B (vacío) | 4 | `contracts/state-machine.json` (J06: un error o un apagado entre el claim y la escritura) | Resuelto por decisión de la persona: NOT_SENT sin consumir intento |

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

## SI-10 · Cómo se reconoce un registro latched incoherente

- **Vacío.** La sección 4 y el ADR 0001 dicen que, si los campos del registro no corresponden al mismo ciclo, la detección queda `BLOCKED` con `INCOHERENT_SNAPSHOT` (T13), pero no definen cómo lo reconoce el gateway: un valor atómico no trae, por sí mismo, una prueba de que sus campos son del mismo ciclo.
- **Qué hace la implementación.** Es una capacidad del PLC no confirmada por OT (regla 6 de `spec/AI_CONTEXT.md`): queda detrás de `CycleDecoder` y del simulador. El registro del simulador termina con un eco del `CycleId` que el PLC escribe al cerrar el registro (`closingCycleId`); si no coincide con `cycleId`, el registro es incoherente. Sin eco, la coherencia no se puede comprobar y el registro se acepta.
- **Pendiente de OT (OT-04).** El formato real del registro y su mecanismo de coherencia (eco, contador de escritura, estructura OPC UA atómica).
- **Decisión de la persona (2026-10-14).** Mantener el mecanismo del simulador detrás de `CycleDecoder` hasta que OT defina el formato real.

## SI-11 · T11: el hueco 1001-1020 y la lectura tras reconectar

- **Observación.** T11 espera "hueco 1001-1020 OPEN" con 20 cajas pasadas durante la desconexión. Tras reconectar, OPC UA entrega el valor actual del registro (sección 9.2); si es la caja 1020, el hueco que abre es 1001-1019 y la FIFO entrega 19 cajas nuevas.
- **Interpretación usada.** La prueba reconecta cuando el registro actual ya es la caja 1021: la lectura abre el hueco 1001-1020 y la FIFO entrega las 20 como `RECOVERED`. Ninguna regla cambia; solo se precisa el escenario.
- **Por qué clase A.** El criterio de T11 no se puede ejecutar tal como está escrito (con 20 cajas y la lectura del valor actual, el hueco es 1001-1019) y corregirlo no cambia ninguna regla.
- **Parche mínimo propuesto.** En `spec/tests/test-catalog.json`, T11, `injection`: "Desconectar el gateway mientras pasan 20 cajas; reconectar." → "Desconectar el gateway mientras pasan 20 cajas; reconectar cuando el registro actual ya es la caja siguiente (1021)." Después, `generate_artifacts.py`, `update_checksums.py` y `verify_package.py`.
- **Resolución (2026-10-14).** La persona aprobó la edición; se aplicó en el commit `8798b85` (`spec: SI-11 …`) con `MATRIZ_PRUEBAS.md` regenerada, los checksums actualizados y el verificador en verde (especificación 1.1.4).

## SI-12 · Valor de la marca alta al reiniciarse con una época nueva

- **Vacío.** La sección 9.2 dice que, con un cambio legítimo de época, la marca alta se reinicia, pero no a qué valor. Si se reinicia "vacía", el primer registro de la época nueva nunca abre hueco: con el contador arrancando en 1 y el primer registro en 7, los ciclos 1 a 6 no se cuentan (I16) y, con `GATEWAY_RECEIPT`, el registro 7 recibe plazo aunque la entrega no fue continua (I26).
- **Comportamiento interino (falla cerrada).** En la primera época de la estación (`INITIAL`) no se abre hueco: no se sabe desde dónde contaba el PLC. En una época nueva (`AUTO_INCREMENT_BY_ONE` u `OPERATOR_CONFIRMED`), la marca alta se reinicia como si el contador arrancara en 1: un primer registro mayor que 1 abre el hueco desde 1. Un PLC que arranque en 0 no abre hueco con 0 ni con 1.
- **Por qué clase B.** Elegir el valor de arranque es una decisión de comportamiento que depende del PLC (OT-04). Pendiente de la persona.
- **Costo del interino (segunda revisión de la Fase 2, BAJA-8).** Si la época la administra el gateway, o el PLC no reinicia el contador al cambiar de época, el primer registro (por ejemplo 5000) abre el hueco 1-4999: infla `cycles_unobserved_total`, da una alarma falsa y, con `GATEWAY_RECEIPT`, deja sin plazo ese registro.
- **Alternativa para decidir.** En `OPERATOR_CONFIRMED`, arrancar en `PlcEpochHistory.FirstCycleId`, el ciclo que la persona confirma, en lugar de 1. En `AUTO_INCREMENT_BY_ONE` no hay un valor confirmado y el problema sigue.
- **Decisión de la persona (2026-10-14).** Mantener el interino hasta que OT-04 fije el valor de arranque.

## SI-13 · Registro incoherente frente al estado de secuencia

- **Vacío.** La sección 4 y el ADR 0001 dicen que un registro incoherente queda `BLOCKED` con `INCOHERENT_SNAPSHOT` (D05), pero no qué efecto tiene sobre la secuencia de la sección 9.2. En particular, no dicen si puede aceptar una época, si su `CycleId` mueve la marca alta o abre huecos, ni si su `DetectionTimestamp` sirve para distinguir un tardío de un reinicio. La sección 9.2 sí dice que la marca alta se reconstruye desde SQL, donde la detección incoherente cuenta.
- **Comportamiento interino (falla cerrada).**
  - No acepta épocas. Un incoherente de una época no aceptada se retiene como `EPOCH_UNEXPECTED` y bloquea la estación, aunque la época sea la actual + 1 con `AUTO_INCREMENT_BY_ONE`: su `PlcEpoch` puede ser de otro ciclo.
  - Su `CycleId` mueve la marca alta y abre huecos, igual que su fila al recargar desde SQL.
  - Su hora no cuenta para la marca alta. Esto vale para toda detección `BLOCKED` con `INCOHERENT_SNAPSHOT`, porque SQL no distingue el motivo de SI-04 ni el de `PLC_TIMESTAMP` sin hora.
  - Consecuencia: un tardío legítimo posterior a esa marca es `AMBIGUOUS_DECREASE` (`WARNING`, detección `BLOCKED`, estación habilitada salvo repetición), no `Late`.
  - Por debajo de la marca no tiene efecto de secuencia ni corta la racha de descensos ambiguos.
- **Por qué clase B.** Usar `EPOCH_UNEXPECTED` para una época que la política aceptaría, y elegir qué hora vale para la marca alta, son decisiones de comportamiento.
- **Decisión de la persona (2026-10-14).** Mantener el interino.

## SI-14 · Las sentencias de referencia del barrido usan el plazo congelado

- **Contradicción.** En `contracts/state-machine.json`, las guardas de J03, J14 y J13 piden el plazo efectivo vigente con margen (el menor entre el congelado y el recalculado con la revisión activa; sección 10.1). D07 pide el plazo efectivo vencido. En cambio, las sentencias de TX-S de `database/sqlserver_operations_reference.sql` comparan `pj.DeadlineAt` y `cd.DeadlineAt`, que son los plazos congelados.
- **Evidencia (revisión de la Fase 3, EXP2).** Con un job en `RETRY_WAIT` y una revisión nueva que acorta el presupuesto de tránsito, el barrido devolvía el job a `READY` con el plazo efectivo vencido. El claim (J02) sí usaba el plazo efectivo, así que el envío seguía protegido.
- **Qué hace la implementación.** Gana la máquina de estados. El barrido calcula en el EdgeHost el plazo y el margen efectivos de cada job y de cada detección pendiente, con `DeadlinePolicy.Effective` y el mayor de los dos márgenes, los mismos que usan el claim y TX-E. Después los aplica por fila con las guardas en SQL: plazo congelado o efectivo vencido, y revisión activa sin cambios.
  - Las sentencias de referencia se conservan para sus pruebas.
  - J13 no tiene sentencia de referencia: su guarda exige vigentes ambos plazos.
- **Por qué clase B.** Corregir la sentencia de referencia cambia el comportamiento de TX-S en la especificación. Pendiente de la persona: que la referencia recalcule el plazo efectivo en SQL, o que se declare que esas sentencias son solo el piso.
- **Decisión de la persona (2026-10-15).** Mantener el plazo efectivo en el barrido. SI-14 sigue abierto para corregir la referencia en una versión futura de la especificación.

## SI-15 · Límites de la detección de `CLOCK_SKEW`

- **Vacío.** La sección 10.2 pide bloquear la estación si el reloj del PLC diverge más que `ClockSkewToleranceMs`, pero el gateway solo observa `DetectionTimestamp − ReceivedAt`. Esa diferencia incluye la latencia de subida, que no se conoce.
  - Un PLC adelantado se detecta solo cuando el adelanto supera la tolerancia más esa latencia. Un adelanto entre la tolerancia y "tolerancia + latencia" no se detecta, y el plazo queda hasta esa latencia más permisivo de lo que absorbe la fórmula.
  - Un PLC atrasado no se distingue de la latencia. Acorta los plazos (es restrictivo) y las cajas se publican como `LATE_ARRIVAL`, que no es el motivo real.
- **Comportamiento interino.** Se bloquea con `CLOCK_SKEW` solo si `DetectionTimestamp − ReceivedAt` supera la tolerancia (adelanto demostrado). Un PLC atrasado no se detecta. Ambos límites están declarados en el registro de la Fase 3.
- **Por qué clase B.** Detectar el atraso (por ejemplo, con una lectura de la hora del PLC por OPC UA, `ServerTimestamp` frente a `SourceTimestamp`) o publicar otro motivo cambia comportamiento y depende de OT. Pendiente de la persona.
- **Decisión de la persona (2026-10-15).** Mantener el interino y declararlo; queda abierto para OT.

## SI-16 · Trabajo pendiente a través de un `CLOCK_JUMP`

- **Vacío.** La sección 10.2 y la fila "Salto del reloj del EdgeHost" (línea 645) piden no evaluar plazos con el reloj saltado. `ESTRATEGIA_RECUPERACION.md` dice que lo pendiente queda "sin enviar hasta el desbloqueo". Ninguna de las dos fuentes dice qué pasa con ese trabajo después, aunque sus plazos se calcularon con un reloj que ya no es confiable.
- **Evidencia (segunda revisión de la Fase 3, EXP1).**
  1. El reloj de pared retrocede 1 h y la estación se bloquea con `CLOCK_JUMP`.
  2. Pasan 5 min reales y una persona desbloquea.
  3. Un claim acepta un job 5 min después de su plazo real (I04).
- **EXP2.** Con la estación ya bloqueada por otro motivo, el salto no quedaba registrado en SQL y se daba por confirmado.
- **Decisión de la persona (2026-10-15): lo pendiente vence al confirmar el salto.** La transacción que registra el salto (`TxsSweep.ClockJumpAsync`) hace, en una sola transacción:
  - bloquea la estación con `CLOCK_JUMP`;
  - vence los jobs `READY` y `RETRY_WAIT` con `DEADLINE_PASSED` (J03, J14);
  - pasa las detecciones `WAITING_ERP` a `BLOCKED` con `STATION_DISABLED` (D08);
  - publica sus resultados.

  Nada anterior al salto se imprime. El salto solo se da por confirmado cuando el bloqueo vigente es `CLOCK_JUMP`. Con otro bloqueo vigente se vence lo pendiente y el salto se retiene; al levantarse ese bloqueo, la estación queda bloqueada con `CLOCK_JUMP`.
- **Costo aceptado.** Un job vence con `DEADLINE_PASSED` aunque su plazo real no haya pasado: falla cerrada, como el `UNKNOWN` del reinicio.

## SI-17 · Recirculación al llegar el snapshot (TX-E)

- **Contradicción.** TX-E repite la validación de recirculación de TX-A (D08) cuando llega el snapshot de una detección en `WAITING_ERP`. La sentencia de referencia, aplicada al pie de la letra, cuenta como recirculación cualquier otra detección de la misma caja en `WAITING_ERP`, también las posteriores. Con dos ciclos de la caja C-77 esperando snapshot, el primero queda `BLOCKED` con `RECIRCULATED_CARTON` y el segundo crea el job. El catálogo de T16 espera lo contrario: el primer paso de la caja se etiqueta y el segundo es la recirculación.
- **Segundo vacío.** Si la revisión cambió entre TX-A y TX-E, la especificación no dice qué `CartonIdentityMode` aplica: el congelado en la detección o el de la revisión activa.
- **Decisión de la persona (2026-10-15).** Solo cuentan las detecciones anteriores de la caja: por `ReceivedAt` y, si empatan, por época y ciclo, que es el orden en que pasó la caja. En TX-A la detección nueva es siempre la última, así que nada cambia ahí. El modo de identidad es el más restrictivo entre el congelado y el activo (`DeadlinePolicy.EffectiveRequiresCartonId`), como el plazo efectivo (SI-09, SI-14).
- **Pruebas.** T16, con la variante de dos ciclos de la misma caja en `WAITING_ERP` (`RecirculationTests`), y la cobertura de D08 en TX-E (`ReferenceCoverageTests`).

## SI-18 · Un error o un apagado entre el claim y la escritura

- **Vacío.** J06 nombra dos disparadores de NOT_SENT con el plazo vigente: el fallo de conexión antes de escribir, que consume intento, y el rechazo de la segunda comprobación por compuerta o estación bloqueada, que no lo consume. La especificación no dice qué transición aplica cuando, antes de invocar la escritura, falla otra cosa (SQL Server no responde al releer la estación, la compuerta lanza una excepción) o el servicio se detiene. I02 permite NOT_SENT, porque la escritura nunca se invocó, pero no dice si consume intento.
- **Evidencia.** Segunda revisión de la Fase 4, M-1: sin una regla, esos casos dejaban el intento en `SENDING` y la impresora ocupada hasta reiniciar, y TX-R0 los volvía un `UNKNOWN` falso. Tercera revisión, BAJA-6: el comportamiento elegido no estaba registrado aquí.
- **Interino (en el código desde `a2c4205`).** NOT_SENT por J06 sin consumir intento, como un rechazo de la segunda comprobación (la comprobación no pudo completarse). Con el plazo monotónico vencido, J08. Si el error persiste, el job vence por su plazo efectivo. Si SQL Server está caído, el NOT_SENT de TX-C tampoco se registra: el intento queda en `SENDING` y el sender se detiene (decisión 10 de la Fase 4).
- **Alternativas.**
  - J06 consumiendo intento, como un fallo de conexión: después de `MaxAutoAttempts` errores, J07 (`FAILED` con `PRINTER_UNAVAILABLE`).
  - Dejar el intento en `SENDING` hasta TX-R0: un `UNKNOWN` falso y la impresora ocupada hasta reiniciar.
- **Clase B. Decisión de la persona (2026-10-15): sin consumir intento**, el interino. Queda como está en el código.

