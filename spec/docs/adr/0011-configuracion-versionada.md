# ADR 0011: Configuración de estación versionada e inmutable

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
Los tiempos de la estación los mide y ajusta OT, y pueden cambiar mientras hay jobs pendientes.

## Problema
Si un parámetro cambia en sitio, una decisión pasada deja de poder explicarse y un job pendiente podría volverse más permisivo de lo que era al crearse.

## Alternativas
1. Parámetros editables en una fila: rechazada.
2. Recalcular todo con la configuración activa: rechazada; cambia retroactivamente el contenido y el criterio de éxito.
3. Revisiones inmutables, valores congelados por job y el más restrictivo para la seguridad: elegida.

## Decisión
`StationConfigRevision` es inmutable (trigger) y requiere dos personas. Cada detección y job guarda `ConfigRevisionId`. El plazo efectivo es el mínimo entre el congelado y el recalculado con la revisión activa; el margen efectivo, el máximo; la compuerta física se exige si la congelada o la activa la exigen, con la frescura menor. Contenido, plantilla y criterio de evidencia quedan congelados; la impresora también, pero el claim exige que siga siendo la de la revisión activa. `LabelingEnabled` es estado operativo en `StationConfig`, no configuración.

## Justificación
Toda decisión crítica se explica con la revisión que la produjo, y ningún cambio hace más permisivo un job existente.

## Consecuencias
Activar una configuración es insertar una revisión; volver atrás es insertar otra con los valores anteriores.

## Riesgos
Un cambio que retira la impresora de un job pendiente deja ese job sin enviar hasta vencer. El borrador de esta versión evaluaba la compuerta solo con la revisión activa, lo que habría permitido desactivarla para jobs creados con ella; la revisión independiente lo detectó.

## Pruebas
T33.

## Revisar si
Aparecen parámetros que no encajan en "congelado" ni en "más restrictivo".
