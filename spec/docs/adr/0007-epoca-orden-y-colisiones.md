# ADR 0007: Épocas, eventos fuera de orden, huecos y colisiones de identidad

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
Durante reconexiones, Republish de OPC UA o la FIFO del PLC llegan ciclos anteriores legítimos. El contador puede reiniciarse por una carga de programa o un cambio de CPU.

## Problema
Tratar todo contador menor como reinicio bloquearía la estación ante un replay normal; tratarlo como duplicado silenciaría un reinicio real. Comparar timestamps de OPC para detectar colisiones falla porque un servidor puede sellar de nuevo un valor releído.

## Alternativas
1. Bloquear toda disminución de `CycleId`: rechazada.
2. Igualdad del SourceTimestamp de OPC: rechazada.
3. Huella de los valores latched más `DetectionTimestamp` del PLC, con épocas aceptadas en `PlcEpochHistory`: elegida.

## Decisión
`SourceFingerprint` = SHA-256 de la serialización JCS (RFC 8785) del arreglo `[siteId, stationId, plcEpoch, cycleId, detectionTimestamp, workOrderId, cartonId]`, con `detectionTimestamp` en milisegundos UTC y ausentes como `null` (una cadena unida con `|` era ambigua porque `workOrderId` y `cartonId` no tienen patrón). Clave existente con la misma huella: duplicado. Con huella distinta: `IDENTITY_COLLISION` y bloqueo; solo una violación de las restricciones de identidad de la detección se trata así. Contador menor con clave nueva: tardío si su `DetectionTimestamp` es anterior al de la marca alta; reinicio sospechado si es posterior; ambiguo sin timestamp, y entonces la detección queda `BLOCKED` con `SEQUENCE_ANOMALY`. Una época anterior (orden por `AcceptedAt`) solo admite claves hasta su marca alta; un ciclo nuevo en ella es `EPOCH_UNEXPECTED`. Época +1 se acepta según `EpochChangePolicy`; otros cambios son `EPOCH_UNEXPECTED`. Los huecos van a `SequenceGap`. Los registros llegados por Republish, lectura, FIFO, replay o reingreso conservan su transporte y entran como `RECOVERED`; la primera notificación de un monitored item creado o transferido es el valor actual y se registra como `READ`.

## Justificación
Cada caso se distingue con datos que el PLC fijó una vez; ninguno depende de cuándo o por qué camino llegó la notificación.

## Consecuencias
Una colisión detiene la estación hasta que OT confirma la causa. Los registros retenidos se reingresan con `Transport = ANOMALY_REINGEST` (`RECOVERED`) cuando la época se acepta. Los ciclos en colisión no son detecciones y no tienen resultado publicado.

## Riesgos
Sin `DetectionTimestamp` ni `PlcEpoch`, algunos reinicios solo se descubren al chocar con una clave existente, y la identidad original de ciertos ciclos no puede reconstruirse.

## Pruebas
T24, T25, T26, T30, T35, T45.

## Revisar si
El PLC expone una marca de reinicio explícita o un identificador global por ciclo.
