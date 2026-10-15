# ADR 0001: Identidad de ciclo con época y registro latched

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
El evento de origen es un cambio de tag OPC UA. Un booleano de presencia puede llegar repetido por Republish, reconexión o un servidor redundante, y también puede perder flancos breves. La versión 1.0 usaba `(SiteId, StationId, CycleId)` como identidad.

## Problema
Sin un identificador por caja no se distingue un duplicado de una caja nueva. Sin época, un reinicio del contador hace que cada caja nueva choque con una clave vieja y se descarte como duplicado sin alarma.

## Alternativas
1. Flanco del booleano con antirrebote: rechazada; no identifica cajas ni sobrevive reconexiones.
2. GUID generado al recibir la notificación: rechazada; cada reenvío produce una identidad distinta.
3. `(SiteId, StationId, CycleId)`: insuficiente ante reinicios del contador.
4. `(SiteId, StationId, PlcEpoch, CycleId)` sobre un registro latched: elegida.

## Decisión
La identidad lógica es `(SiteId, StationId, PlcEpoch, CycleId)`. El PLC fija `CycleId`, `PlcEpoch`, `DetectionTimestamp`, `WorkOrderId` y `CartonId` en una estructura latched. `CycleId` viaja como texto decimal; `eventId` es UUIDv5 de la clave. Sin `PlcEpoch`, el gateway administra la época con confirmación de OT (ADR 0007).

## Justificación
La clave depende solo de datos que el PLC escribe una vez por caja, de modo que cualquier repetición en red, broker o reinicio produce la misma clave.

## Consecuencias
Requiere cambios menores en el PLC (contador, época y registro latched). El gateway mantiene una marca alta por época.

## Riesgos
Un PLC que no persista contador o época reduce la garantía (G01). Un registro no latched mezcla datos de ciclos distintos: se bloquea con `INCOHERENT_SNAPSHOT`.

## Pruebas
T01, T13, T24, T25, T30.

## Revisar si
El PLC no puede exponer un contador persistente, o existe un identificador físico de caja que permita otra clave (ADR 0013).
