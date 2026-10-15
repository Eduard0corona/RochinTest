# ADR 0008: Serial único por etiqueta y niveles de evidencia

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
Las cajas de una misma orden llevan el mismo producto, GTIN y lote. La versión 1.0 exigía que un escáner confirmara "el serial correcto", pero no existía un serial por etiqueta, y su schema aceptaba `CONFIRMED` sin evidencia.

## Problema
Sin un dato único en la etiqueta, una lectura no distingue la etiqueta de la caja N de la de la N+1. Sin niveles, "confirmado" significaría cosas distintas en cada estación.

## Alternativas
1. Confirmar con GTIN y lote: rechazada.
2. Serial derivado de `CycleId`: rechazada; el contador puede reiniciarse.
3. Serial de una SEQUENCE de SQL y niveles `PRINTED`, `APPLIED`, `VERIFIED` con su correlación: elegida.

## Decisión
`LabelSerial` se toma de `LabelSerialSeq` antes de crear el job (fuera de la transacción), se incluye en el payload y su hash, y es único. Toda evidencia se registra en `EvidenceObservation`, confirme o no. `CONFIRMED` exige el `MinConfirmationLevel` congelado con la correlación de su nivel; `VERIFIED` exige además `ReadSerial = LabelSerial`. `TR_PrintAttempt_Guard` lo valida en SQL al confirmar un intento. Una estación sin evidencia usa `NONE` y termina en `TRANSMITTED`. Una lectura contradictoria posterior a `CONFIRMED` corrige el job a `UNKNOWN` (J18).

## Justificación
Cada nivel afirma solo lo que su evidencia demuestra; el serial convierte una lectura en una prueba sobre un job concreto.

## Consecuencias
Los seriales tienen huecos (rollback, duplicados, CACHE). SSCC-18 solo con prefijo GS1 asignado y dígito de control. Una confirmación puede revocarse por una evidencia posterior, y el consumidor debe aplicar siempre la `resultVersion` mayor.

## Riesgos
La correlación por ciclo depende del tracking del PLC; sin él, `VERIFIED` solo prueba que la etiqueta existe en la línea.

## Pruebas
T15, T22, T23, T38, T44.

## Revisar si
La planta adopta SSCC o un esquema de serialización regulado.
