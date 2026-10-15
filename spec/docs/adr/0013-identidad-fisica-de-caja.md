# ADR 0013: Una etiqueta por ciclo frente a una etiqueta por caja física

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
Una caja puede volver a pasar por el sensor (retrabajo, recirculación) y generar un ciclo válido nuevo. La versión 1.0 mencionaba `CartonId` sin definir qué garantía dependía de él.

## Problema
La idempotencia por `CycleId` evita dos etiquetas por ciclo, no por caja. Afirmar lo segundo sin identidad física sería falso.

## Alternativas
1. Asumir que no hay recirculación: rechazada.
2. Deducir la identidad por tiempos o pesos: rechazada; inventa identidad.
3. Dos garantías separadas y un modo `CARTON_ID_REQUIRED` cuando existe identidad física: elegida.

## Decisión
Por defecto, `CYCLE_ONLY`: una etiqueta ordinaria por ciclo. Con `CARTON_ID_REQUIRED`, TX-A bloquea con `RECIRCULATED_CARTON` si la misma caja ya tiene un job con etiqueta posible o pendiente, o una detección que espera snapshot, validado con bloqueo de rango sobre `IX_PrintJob_Carton` e `IX_CartonDetection_Carton`; TX-E repite la validación al llegar el snapshot (D08). El sistema nunca inventa un `CartonId`.

## Justificación
Cada garantía queda atada al dato que la hace posible.

## Consecuencias
Sin identidad física, una caja recirculada puede recibir dos etiquetas; queda en la matriz de garantías (G07).

## Riesgos
Un `CartonId` mal leído bloquea cajas legítimas.

## Pruebas
T16, T32.

## Revisar si
OT confirma código previo, RFID o tracking de recirculación (OT-05, OT-06).
