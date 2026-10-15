# ADR 0006: Vencimiento por caja y segunda comprobación antes de escribir

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
Una caja tarda un tiempo fijo en llegar del sensor al punto de aplicación. La versión 1.0 no tenía ningún concepto de plazo: un job podía imprimirse minutos después.

## Problema
Un job que sale de `WAITING_ERP`, de `RETRY_WAIT` o de un replay después de que su caja pasó, imprime una etiqueta que cae en otra caja. Además, validar el plazo solo en el claim no basta: el proceso puede suspenderse entre el claim y la escritura. La revisión independiente del borrador mostró otros tres huecos: el plazo no descontaba el tiempo que el dispositivo tarda en imprimir y aplicar; con `GATEWAY_RECEIPT` se fabricaba un plazo para registros sin latencia acotada; y un salto del reloj de pared hacía permisivas la guarda del claim y la conversión al dominio monotónico.

## Alternativas
1. Sin plazo, confiando en la velocidad: rechazada.
2. Plazo solo en el claim: insuficiente.
3. Plazo congelado, guarda en el claim, segunda comprobación monotónica y compuerta opcional del PLC: elegida.
4. Redefinir `TransitBudgetMs` como tiempo hasta el inicio del envío en lugar de añadir `DeviceLeadTimeMs`: descartada, porque mezcla en un solo parámetro una medida de la línea (OT-03) y una del dispositivo, que cambian por separado.
5. Usar `ReceivedAt` como referencia para todo registro con `GATEWAY_RECEIPT`: rechazada; fabrica un plazo cuando la latencia no está acotada.

## Decisión
`DeadlineAt` se calcula con la referencia de la revisión y se congela: con `PLC_TIMESTAMP`, `DetectionTimestamp + TransitBudgetMs − DeviceLeadTimeMs − ClockSkewToleranceMs`; con `GATEWAY_RECEIPT`, `ReceivedAt − MaxUpstreamLatencyMs + TransitBudgetMs − DeviceLeadTimeMs`, solo para notificaciones `SUBSCRIPTION` sin overflow que no sean la primera de un monitored item creado o transferido (el valor actual, registrado como `READ`) ni abran un hueco. Con `PLC_TIMESTAMP`, un registro sin `DetectionTimestamp` queda `BLOCKED` sin plazo (`CK_CartonDetection_PlcTimestamp`). Cualquier otro registro con `GATEWAY_RECEIPT` se registra con `DeadlineAt = ReceivedAt` y queda `EXPIRED` con `LATE_ARRIVAL`; `CK_CartonDetection_GatewayLive` lo impone. El claim exige plazo efectivo menos margen en el futuro, con `@EdgeNowUtc` del EdgeHost. Antes de invocar la escritura, el sender vuelve a comprobar en el dominio monotónico y, si la revisión congelada o la activa usan `PLC_WINDOW_TAG`, que el ciclo esté en la zona con la frescura menor. El EdgeHost vigila la divergencia entre `UtcNow` y `Stopwatch` y bloquea la estación con `CLOCK_JUMP` si supera `ClockJumpToleranceMs`. Lo vencido queda `EXPIRED` y nunca se envía como operación ordinaria.

## Justificación
Un envío tardío es peor que no enviar: la falta de etiqueta se detecta, una etiqueta en la caja equivocada no.

## Consecuencias
Más cajas sin etiqueta cuando la línea va al límite; se miden con `jobs_expired_total`.

## Riesgos
Una pausa entre la segunda comprobación y la escritura, o una etiqueta retenida en la cola del dispositivo, no se pueden excluir por software; solo una compuerta física cierra la ventana (G05). Un salto de reloj menor que `ClockJumpToleranceMs` no se detecta y un job recuperado tras un reinicio solo tiene la comprobación derivada del reloj de pared. Con `GATEWAY_RECEIPT`, toda caja que llega por recuperación queda sin etiqueta.

## Pruebas
T17, T18, T19, T21, T29, T31, T37, T45.

## Revisar si
OT confirma un aplicador controlado por el PLC, que permitiría delegar la ventana física.
