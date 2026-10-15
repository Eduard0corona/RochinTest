# ADR 0004: Snapshot ERP local y vínculo de lote

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
El ERP es lento comparado con el paso de las cajas y puede caer. Producto y lote cambian solo con el cambio de orden, no por caja.

## Problema
Consultar al ERP por cada caja pone su latencia en la ruta crítica. Adivinar el lote cuando el ERP no responde imprime etiquetas falsas, y un cambio de lote durante un job pendiente podría reescribir lo que ya se congeló.

## Alternativas
1. Llamada síncrona al ERP por caja: rechazada.
2. Último lote conocido sin validar vigencia: rechazada.
3. Caché local por `(WorkOrderId, Revision)` con vigencia, y job congelado: elegida.

## Decisión
ERP Snapshot Sync mantiene `ErpWorkOrderCache` con `ProductId`, `LotId`, `Revision`, `ValidFrom` y `ValidUntil`. El job copia la revisión usada y su payload queda inmutable (`TR_PrintJob_Immutable`). Sin snapshot válido la detección espera en `WAITING_ERP` mientras dure su plazo; después queda `EXPIRED` con `ERP_UNAVAILABLE`.

## Justificación
El ERP sale de la ruta crítica sin perder la validez de los datos impresos.

## Consecuencias
Un ERP caído durante un cambio de orden detiene el etiquetado de la orden nueva hasta que llegue un snapshot válido.

## Riesgos
La regla exacta de frontera en un cambio de lote depende de OT y del ERP.

## Pruebas
T03, T04, T10, T17.

## Revisar si
El ERP publica eventos de cambio de orden con garantía de entrega, lo que permitiría reducir la vigencia de la caché.
