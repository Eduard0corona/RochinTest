# ADR 0009: Perfil mínimo por defecto, perfil extendido por requisito y huecos como SequenceGap

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
El caso pide un flujo mínimo, pero algunas plantas exigen la historia completa de ciclos aunque SQL caiga. La versión 1.0 imponía siempre cola en el PLC, ACK y journal SQLite.

## Problema
Imponer tres capas durables encarece la integración y suma latencia sin un requisito que lo pida. Eliminar el journal sin más dejaría sin registro los ciclos durante una caída cuando la planta sí lo exige. Registrar ciclos no observados como detecciones inventaría hechos.

## Alternativas
1. Siempre perfil extendido: rechazada como valor por defecto.
2. Siempre perfil mínimo: rechazada; la durabilidad es un requisito legítimo en algunas plantas.
3. Dos perfiles con criterio explícito y `SequenceGap` para lo no observado: elegida.

## Decisión
El perfil mínimo falla cerrado si SQL cae y registra el hueco del contador como `SequenceGap`, sin detecciones ni resultados para esos ciclos. El perfil extendido agrega cola del PLC, `AckCycleId` y journal SQLite cuando OT exige la historia completa (OT-13, OT-14). En ambos, lo reprocesado después del plazo queda `EXPIRED`.

## Justificación
Cada componente adicional queda ligado al requisito que lo justifica.

## Consecuencias
En el perfil mínimo, los ciclos de una caída solo se conocen como cantidad.

## Riesgos
Elegir el perfil sin confirmar OT-14 puede dejar sin historia lo que la planta necesita.

## Pruebas
T09, T11, T19, T20, T35.

## Revisar si
OT responde OT-13 u OT-14.
