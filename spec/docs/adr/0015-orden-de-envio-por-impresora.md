# ADR 0015: Orden de envío monótono por impresora

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
En una línea con print-and-apply la etiqueta se aplica en el orden en que se imprime: la etiqueta que sale del dispositivo cae en la caja que está bajo el aplicador. El borrador de la versión 1.1 controlaba el plazo de cada job, pero no el orden entre jobs de la misma impresora.

## Problema
La revisión independiente mostró dos caminos para enviar fuera de orden dentro del plazo: un job N en `RETRY_WAIT` por un fallo transitorio mientras se reclama y transmite N+1, y un evento tardío pero todavía vigente que llega después de un ciclo posterior. En ambos casos las etiquetas quedan cruzadas entre cajas y, con `MinConfirmationLevel = APPLIED`, los dos jobs llegarían a `CONFIRMED`.

## Alternativas
1. Bloquear la cabeza de la cola mientras un job está en `RETRY_WAIT`: descartada como regla de seguridad, porque no cubre eventos tardíos; queda como posible política del sender.
2. Exceptuar las estaciones con `PLC_WINDOW_TAG`: rechazada; la compuerta confirma qué ciclo está en la zona de impresión, no el orden de aplicación.
3. Orden monótono por impresora en el claim, aplicado en SQL: elegida.

## Decisión
`PrinterSendOrder` guarda, por impresora de línea, el último ciclo reclamado (época, su `AcceptedAt` y `CycleId`). El claim (J02) lo lee con `UPDLOCK` y solo acepta un ciclo posterior o un reintento del mismo job, y lo avanza en la misma transacción con los valores de la fila reclamada. `TR_PrinterSendOrder_Monotonic` impide retrocederlo y `TR_PlcEpochHistory_Immutable` impide reordenar las épocas. Un job atrasado no se envía: vence con `OUT_OF_ORDER` (J03 desde `READY`, J14 desde `RETRY_WAIT`). El orden entre épocas lo da `PlcEpochHistory.AcceptedAt`, no el valor numérico de la época. Una impresora de línea pertenece a una sola estación.

## Justificación
Una caja sin etiqueta se detecta y se trata; una etiqueta en la caja equivocada no. Perder un job atrasado es el costo de no cruzar etiquetas.

## Consecuencias
Un fallo transitorio de la impresora puede costar la caja del job afectado si mientras tanto llega la siguiente. Se mide con `jobs_expired_total{reason=OUT_OF_ORDER}`.

## Riesgos
El orden de envío no garantiza el orden de aplicación si el aplicador reordena o retiene etiquetas (OT-01); eso solo lo cubre una compuerta física o la verificación por escáner. Las reimpresiones excepcionales no participan del orden: por eso solo se permiten en una impresora fuera de línea o con la estación bloqueada (ADR 0012).

## Pruebas
T30, T42.

## Revisar si
Una impresora sirve a más de una estación, o el aplicador informa por ciclo qué etiqueta aplicó.
