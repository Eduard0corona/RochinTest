# ADR 0012: Reimpresión excepcional como intento autorizado, no como transición

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
Una caja retenida con un job `UNKNOWN`, vencido o con etiqueta dañada necesita una etiqueta. La revisión 1 proponía permitir `UNKNOWN → SENDING` con autorización manual.

## Problema
Una autorización no demuestra que la etiqueta original no exista. Presentar la reimpresión como parte del flujo normal ocultaría que puede producir un duplicado, y crear un job nuevo rompería la unicidad por detección. La revisión independiente del borrador encontró además que una reimpresión que no se confirmaba dejaba el job en un estado que afirma que no hay etiqueta, que SQL no validaba la autorización (se podía usar la de otro job, revocada o vencida) y que no estaba definido quién transmite ni a qué impresora.

## Alternativas
1. `UNKNOWN → SENDING` manual: rechazada.
2. Job nuevo para la reimpresión: rechazada.
3. Intento `EXCEPTIONAL_REPRINT` del mismo job, ligado a una autorización de dos personas con riesgo declarado: elegida.
4. Transmitir desde la Operations API: rechazada; abriría una segunda conexión a la impresora.
6. Reimprimir en la impresora de línea con la estación bloqueada en software: rechazada tras la segunda revisión independiente; la bandera no detiene la línea.
5. Retirar la etiqueta dañada después de verificar la de reemplazo: descartada; durante esa ventana la caja tendría dos etiquetas con el mismo serial y una lectura no distinguiría cuál quedó.

## Decisión
`ReprintAuthorization` registra solicitante y aprobador distintos, motivo ligado al estado previo, evidencia de inspección, caja retenida, impresora destino, vigencia, ventana de evidencia y `DuplicateRisk`. Cada autorización se usa una vez y solo puede haber una vigente por job.

En SQL: FK compuesta `(AuthorizationId, PrintJobId)` desde el intento; `TR_PrintAttempt_Guard` exige la autorización en `EXECUTED`, el job en `PriorJobStatus`, la impresora autorizada y, para etiqueta dañada, el retiro de la original registrado antes de ejecutar; `TR_ReprintAuthorization_Guard` exige que el estado previo sea el real y solo admite `NONE_PROVEN` si ningún intento del job que pudo transmitir quedó fuera de `NOT_PRINTED`.

La ejecuta el sender de la impresora destino con su lease (ADR 0010). La impresora es siempre la `ManualPrinterId` de la estación: una print-and-apply en marcha aplicaría la etiqueta a otra caja, y una bandera de software que bloquea la estación no detiene la línea. Un intento excepcional solo se confirma con una observación que lo referencia, y cada transición excepcional cita el intento más reciente del job.

El job no vuelve al flujo ordinario. El intento excepcional confirmado lo lleva a `CONFIRMED` (X01) o publica una versión nueva (X02); el que termina `UNKNOWN` lo deja en `UNKNOWN` con `EXCEPTIONAL_REPRINT_UNRESOLVED` (X03). En los tres casos el resultado lleva `exceptionalReprint`.

## Justificación
La operación queda posible, trazable y honesta sobre su riesgo.

## Consecuencias
Desde `UNKNOWN` o `CLOSED_UNRESOLVED` la garantía de no duplicación no se conserva y el resultado lo declara (`ACCEPTED`).

## Riesgos
Uso rutinario del procedimiento; se vigila `exceptional_reprints_total`.

## Pruebas
T34, T40, T41, T43.

## Revisar si
OT define otro control, o confirma que no existe impresora fuera de línea (OT-16): habilitar la impresora de línea exigiría una condición física registrada (línea detenida o aplicador deshabilitado por el PLC).
