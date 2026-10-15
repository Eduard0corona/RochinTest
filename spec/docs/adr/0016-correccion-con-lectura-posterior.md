# ADR 0016: Corrección J18 solo con una lectura posterior al estado que corrige

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-14 · **Versión del paquete:** 1.1.3

## Contexto
J18 corrige un job `CONFIRMED`, o en `UNKNOWN` por otra causa, a `UNKNOWN` con `SERIAL_MISMATCH` cuando llega una lectura `VERIFIED` del ciclo con otro serial. La rama `CORRECTION` de `TR_PrintJob_StateMachine` exigía que existiera una lectura así, pero no que fuera posterior al estado que corrige. La implementación del MVP lo encontró al ejecutar T47 contra SQL Server real (registro SI-07 de la implementación).

## Problema
Una lectura registrada antes de que existiera la etiqueta vigente del job bastaba para corregirlo. Dos casos concretos: una lectura anterior al claim (la etiqueta todavía no existía y lo leído pertenece a otra caja) y una lectura de la etiqueta original aplicada después de que una reimpresión excepcional confirmada la reemplazó (X01). En ambos, el trigger aceptaba la corrección y T47 ("toda corrección con un respaldo anterior falla") no se podía cumplir. Reaplicar observaciones al reanudar tras una caída, entre el registro y la aplicación de TX-D, solo es seguro si la base rechaza esos casos.

## Alternativas
1. Exigirlo solo en el código de TX-D: rechazada; una regla de la máquina de estados que solo vive en el código no es una barrera, y la comparación quedaría fuera de T47.
2. Comparar solo `ReceivedAt` con el primer claim: rechazada; no cubre la lectura de la etiqueta original aplicada después de una reimpresión confirmada.
3. Exigir en el trigger una lectura posterior al último claim del job y a la observación que lo confirmó, si la hay: elegida.

## Decisión
La rama `CORRECTION` exige que la `EvidenceObservation` contradictoria cumpla, además de lo anterior: `ReceivedAt` igual o posterior al `ClaimedAt` del último intento del job (la misma comparación de relojes del EdgeHost que I06 usa para confirmar), y un `ObservationId` mayor que el `ConfirmingObservationId` de cualquier intento del job. Una lectura que no lo cumple queda registrada sin efecto. La guarda de J18 en `contracts/state-machine.json` y el mecanismo de I25 lo declaran.

## Justificación
Una corrección reduce certeza y publica una versión mayor del resultado: tiene que referirse a la etiqueta que el job tiene hoy. El `ObservationId` ordena las observaciones sin depender de relojes; el `ClaimedAt` del último intento marca desde cuándo existe esa etiqueta.

## Consecuencias
Una lectura vieja de la etiqueta reemplazada ya no deshace una confirmación por reimpresión. TX-D puede reaplicar observaciones pendientes al arrancar sin riesgo de corregir con evidencia anterior.

## Riesgos
La comparación con `ClaimedAt` usa el reloj de pared del EdgeHost, como I06: un salto del reloj hacia atrás podría descartar una contradicción real. Lo acota la detección de saltos del reloj (`CLOCK_JUMP`), que bloquea la estación.

## Pruebas
T44, T47.

## Revisar si
La evidencia trae una referencia directa al intento o a la etiqueta física leída, o el reloj del EdgeHost deja de ser la referencia común de `ReceivedAt` y `ClaimedAt`.
