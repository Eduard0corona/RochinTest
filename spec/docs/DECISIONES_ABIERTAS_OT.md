# Decisiones abiertas para OT (v1.1)

Ninguna de estas respuestas se inventa en el diseño. Cada una indica qué cambia en la arquitectura y qué puede implementarse sin esperarla. La tabla se genera desde `docs/open-decisions.json`.

<!-- GENERATED:BEGIN open-decisions -->
| ID | Pregunta | Impacto arquitectónico | Qué puede avanzar sin resolverla |
| --- | --- | --- | --- |
| OT-01 | ¿La impresora es independiente o forma parte de un sistema print-and-apply? | Define si existe evidencia APPLIED y si una compuerta física puede garantizar la ventana (G05). | Todo el flujo lógico con el simulador; el nivel de evidencia queda configurable. |
| OT-02 | ¿Las cajas avanzan de forma continua o pueden detenerse? | Si pueden detenerse, el plazo puede extenderse con una señal del PLC y la retención es viable. | Plazo fijo por revisión; la extensión queda como mejora. |
| OT-03 | ¿Cuál es el tiempo real entre el sensor y el punto de aplicación, cuánto tarda el dispositivo desde el envío hasta dejar la etiqueta aplicada, y qué reloj es confiable? | Fija TransitBudgetMs, DeviceLeadTimeMs, SendMarginMs y DeadlineReference; determina la factibilidad (G11). | Valores de laboratorio en el simulador; la desigualdad de factibilidad se evalúa al medir. |
| OT-04 | ¿Puede el PLC exponer PlcEpoch persistente y un registro latched con DetectionTimestamp? | Sin ellos, la detección de reinicios y rebotes es limitada (G01). | Época administrada por el gateway, con su limitación documentada. |
| OT-05 | ¿Existe un identificador físico de caja? | Habilita CARTON_ID_REQUIRED y la garantía por caja (G07). | Modo CYCLE_ONLY. |
| OT-06 | ¿Puede haber recirculación de cajas? | Si la hay y no existe identidad física, puede haber dos etiquetas por caja. | Todo; la limitación queda en la matriz de garantías. |
| OT-07 | ¿La impresora informa finalización o cancelación por trabajo? | Habilita el nivel PRINTED, J12 (que exige cancelación confirmada por job, no solo 'no impreso') y la prueba 1 y 2 de la reconciliación negativa. | Adaptador de impresora con capacidad declarada; sin ella, J12 no se usa. |
| OT-08 | ¿Existe escáner de verificación y está correlacionado con el ciclo? | Habilita VERIFIED y la garantía de etiqueta correcta en caja correcta (G06). | Nivel configurable; el simulador emite lecturas correlacionadas. |
| OT-09 | ¿El PLC puede retener, detener o rechazar cajas? | Define qué hace la línea ante UNKNOWN, SQL caído o estación bloqueada. | El sistema baja la señal de etiquetadora lista; la reacción física queda en el PLC. |
| OT-10 | ¿Puede existir más de un agente activo? | Si sí, se requiere aislamiento físico del canal (G10). | Un solo EdgeHost por estación. |
| OT-11 | ¿Se requiere alta disponibilidad del Print Agent? | Exige aislamiento de red, apagado verificado del nodo anterior o proxy que valide tokens. | Conmutación manual con procedimiento en el runbook. |
| OT-12 | ¿Qué ocurre operacionalmente ante una impresión incierta? | Define la retención de cajas UNKNOWN y quién autoriza reimpresiones. | Estados, autorizaciones y auditoría ya modelados. |
| OT-13 | ¿Se permite producir con SQL Server fuera de servicio? | Si sí, solo con etiquetado manual o perfil extendido; el perfil mínimo falla cerrado. | Perfil mínimo con falla cerrada. |
| OT-14 | ¿Es obligatorio conservar la historia de todos los ciclos durante una interrupción? | Si sí, activa el perfil extendido (G09) y requiere cambios en el PLC. | Perfil mínimo con SequenceGap. |
| OT-15 | ¿Qué nivel de evidencia se exige para declarar una etiqueta confirmada? | Fija MinConfirmationLevel por estación (NONE, PRINTED, APPLIED, VERIFIED). | Todo; el nivel es parámetro de revisión. |
| OT-16 | ¿Existe una impresora fuera de línea para reimpresiones excepcionales, o la reimpresión debe hacerse en la impresora de línea con la estación detenida? | Define ManualPrinterId. Mientras siga abierta, una reimpresión solo se admite en la impresora fuera de línea: en una print-and-apply en marcha caería en la caja que esté bajo el aplicador, y bloquear la estación en software no detiene la línea. | Reimpresión con ManualPrinterId en el simulador. La variante en la impresora de línea requeriría una condición física confirmada (línea detenida o aplicador deshabilitado por el PLC) y no está habilitada. |
<!-- GENERATED:END open-decisions -->

Orden sugerido para resolverlas: OT-03 y OT-04 primero (fijan plazo e identidad), después OT-01, OT-07 y OT-08 (fijan el nivel de evidencia posible) y por último OT-11, OT-13 y OT-14 (fijan el perfil y la disponibilidad).
