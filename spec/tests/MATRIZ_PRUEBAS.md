<!-- GENERADO por tools/generate_artifacts.py desde tests/test-catalog.json. No editar. -->
# Matriz de pruebas v1.1

Cada prueba declara sus precondiciones, datos, inyección, estados, eventos, efectos físicos, métricas y criterio de aprobación. Las columnas de transiciones e invariantes enlazan con `contracts/state-machine.json` y `contracts/invariants.json`; `tools/verify_package.py` exige que cada transición e invariante tenga al menos una prueba.

Estado: solo T23 y T28 se ejecutaron en esta revisión documental. Las demás requieren la implementación y quedan como **No ejecutada**.

Niveles: `UNIT` Prueba unitaria sin infraestructura.; `INTEGRATION` SQL Server real (Testcontainers) y simuladores.; `FAULT` Inyección de fallas con simuladores de OPC UA, impresora, ERP, red y procesos.; `LOAD` Carga sostenida con payload realista.; `FAT` Aceptación con PLC, impresora y aplicador reales.; `DOC` Verificación del paquete documental.

## Resumen

| ID | Prueba | Nivel | Perfil | Transiciones | Invariantes | Estado |
| --- | --- | --- | --- | --- | --- | --- |
| T01 | Tag duplicado | INTEGRATION | AMBOS | D01, D02, J01 | I01 | No ejecutada |
| T02 | Concurrencia en el claim | INTEGRATION | AMBOS | J02, A01 | I01, I03 | No ejecutada |
| T03 | ERP lento dentro del plazo | FAULT | AMBOS | D03, D06 | I10 | No ejecutada |
| T04 | ERP caído | FAULT | AMBOS | D03, D07 | I10 | No ejecutada |
| T05 | Impresora no conecta antes de enviar | FAULT | AMBOS | A02, J06, J13, J07 | I02 | No ejecutada |
| T06 | Respuesta perdida después de transmitir | FAULT | AMBOS | A04, J09, A08, J15 | I02, I03, I05 | No ejecutada |
| T07 | Reinicio del servicio en SENDING | FAULT | AMBOS | J09, A04 | I02, I03, I19, I20 | No ejecutada |
| T08 | RabbitMQ caído | FAULT | AMBOS | — | I22 | No ejecutada |
| T09 | SQL caído con journal (perfil extendido) | FAULT | EXTENDIDO | — | I21 | No ejecutada |
| T10 | Cambio de lote en carrera | INTEGRATION | AMBOS | — | I09, I10 | No ejecutada |
| T11 | Reconexión OPC con FIFO (perfil extendido) | FAULT | EXTENDIDO | G01, G02, D04 | I04, I16 | No ejecutada |
| T12 | Carga sostenida y capacidad | LOAD | AMBOS | — | I14 | No ejecutada |
| T13 | Snapshot del PLC incoherente | FAULT | AMBOS | D05 | I10 | No ejecutada |
| T14 | Resultado republicado | INTEGRATION | AMBOS | — | I22 | No ejecutada |
| T15 | Confirmación física por escáner | INTEGRATION | AMBOS | A03, J05, A05, J10 | I05, I06 | No ejecutada |
| T16 | Caja recirculada con CartonId | INTEGRATION | AMBOS | D05, D06, D08 | I07 | No ejecutada |
| T17 | ERP vuelve después del plazo | FAULT | AMBOS | D07 | I04, I10 | No ejecutada |
| T18 | Backoff mayor que el plazo restante | FAULT | AMBOS | J14 | I04 | No ejecutada |
| T19 | SQL caído (perfil mínimo) | FAULT | MINIMO | G01, G03 | I04, I16 | No ejecutada |
| T20 | Replay tardío del journal (perfil extendido) | FAULT | EXTENDIDO | D04 | I04 | No ejecutada |
| T21 | Claim con plazo vencido | INTEGRATION | AMBOS | J03 | I04 | No ejecutada |
| T22 | El escáner lee el serial de otro job | INTEGRATION | AMBOS | J11, A06 | I06 | No ejecutada |
| T23 | Contratos inválidos | DOC | AMBOS | — | I06, I13 | Ejecutada en esta revisión |
| T24 | Contador reiniciado sin cambio de época | FAULT | AMBOS | D05 | I08, I15 | No ejecutada |
| T25 | Contador reiniciado con cambio de época | FAULT | AMBOS | — | I08 | No ejecutada |
| T26 | Rebote del sensor | FAULT | AMBOS | D05 | I08 | No ejecutada |
| T27 | Dos instancias del EdgeHost | FAULT | AMBOS | J09 | I11 | No ejecutada |
| T28 | Coherencia del paquete | DOC | AMBOS | — | I12, I13, I14 | Ejecutada en esta revisión |
| T29 | Suspensión después del claim | FAULT | AMBOS | A02, J08 | I04 | No ejecutada |
| T30 | Evento OPC antiguo fuera de orden | FAULT | AMBOS | D04, J03 | I08, I23 | No ejecutada |
| T31 | Etiqueta retenida en la cola del dispositivo | FAULT | AMBOS | J11, A06, J12, A07 | I05 | No ejecutada |
| T32 | Recirculación sin identidad física | INTEGRATION | AMBOS | D02 | I07 | No ejecutada |
| T33 | Cambio de configuración con job pendiente | INTEGRATION | AMBOS | — | I09, I17 | No ejecutada |
| T34 | Reimpresión con incertidumbre | INTEGRATION | AMBOS | R01, R02, A01, X01 | I01, I03, I18 | No ejecutada |
| T35 | Hueco de secuencia sin timestamps | FAULT | MINIMO | G01, G03 | I07, I16 | No ejecutada |
| T36 | Agente suspendido después de validar su token | FAULT | AMBOS | — | I11, I12 | No ejecutada |
| T37 | Desfase de reloj del PLC | FAULT | AMBOS | D05 | I04 | No ejecutada |
| T38 | Ventana de evidencia vencida y estación sin evidencia | INTEGRATION | AMBOS | J11, A06, J05 | I05 | No ejecutada |
| T39 | Cancelación autorizada | INTEGRATION | AMBOS | J04 | I03, I24 | No ejecutada |
| T40 | Reconciliación negativa y cierre operativo | INTEGRATION | AMBOS | J16, A09, J17 | I18, I24 | No ejecutada |
| T41 | Autorizaciones revocadas, vencidas y etiqueta dañada | INTEGRATION | AMBOS | R03, R04, X02 | I18 | No ejecutada |
| T42 | Envío fuera de orden | FAULT | AMBOS | J06, J14 | I04, I23 | No ejecutada |
| T43 | Reimpresión excepcional sin confirmar | INTEGRATION | AMBOS | X03, A06, A08, X01 | I18, I25 | No ejecutada |
| T44 | Evidencia contradictoria después de CONFIRMED | INTEGRATION | AMBOS | J18 | I06, I25 | No ejecutada |
| T45 | Registros sin cota de latencia con GATEWAY_RECEIPT | FAULT | AMBOS | D04, D05 | I08, I26 | No ejecutada |
| T46 | Estación bloqueada mientras se espera el ERP | INTEGRATION | AMBOS | D08 | I10 | No ejecutada |
| T47 | DDL y sentencias de referencia en SQL Server | INTEGRATION | AMBOS | — | I03, I09, I24 | No ejecutada |

## Detalle

### T01 · Tag duplicado

Origen: v1.0 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Estación habilitada; snapshot ERP válido; impresora simulada disponible. |
| Datos iniciales | Ciclo P01:L03:42:918273 con huella H1. |
| Inyección | 100 entregas del mismo registro (notificaciones repetidas, Republish y relectura) desde 10 consumidores concurrentes. |
| Estados esperados | 1 CartonDetection JOB_CREATED; 1 PrintJob; 1 PrintAttempt. |
| Eventos publicados | 1 CartonDetected; 1 PrintJobResult por transición publicable. |
| Efectos físicos esperados | El simulador recibe como máximo una transmisión. |
| Métricas afectadas | duplicate_detection_total = 99 (por transporte); duplicate_physical_transmissions_total = 0. |
| Criterio de aprobación | Conteos exactos en SQL y en el simulador; ningún error de unicidad sin manejar. |
| Transiciones cubiertas | D01, D02, J01 |
| Invariantes | I01 |

### T02 · Concurrencia en el claim

Origen: v1.0 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Un job READY con plazo vigente. |
| Datos iniciales | Job J con DeadlineAt a 2 s. |
| Inyección | 10 tareas ejecutan el claim del mismo job a la vez. |
| Estados esperados | Exactamente 1 claim afecta 1 fila; 1 PrintAttempt SENDING. |
| Eventos publicados | Ninguno adicional. |
| Efectos físicos esperados | Una transmisión como máximo. |
| Métricas afectadas | claim_conflicts_total = 9. |
| Criterio de aprobación | Un solo ganador; los demás terminan con @claimed vacío y sin excepción: los triggers tienen la guarda de 0 filas. |
| Transiciones cubiertas | J02, A01 |
| Invariantes | I01, I03 |

### T03 · ERP lento dentro del plazo

Origen: v1.0 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Snapshot ausente en caché; TransitBudgetMs mayor que el retraso del ERP. |
| Datos iniciales | Orden WO-1 sin snapshot. |
| Inyección | El simulador ERP responde con 300 ms de retraso. |
| Estados esperados | Detección WAITING_ERP y luego JOB_CREATED; el callback OPC nunca espera al ERP. |
| Eventos publicados | CartonDetected al registrar; resultado del job al terminar. |
| Efectos físicos esperados | Una etiqueta si el plazo lo permite. |
| Métricas afectadas | erp_snapshot_wait_ms; opc_callback_duration_ms sin incremento. |
| Criterio de aprobación | Ninguna llamada síncrona al ERP en el camino de ingesta. |
| Transiciones cubiertas | D03, D06 |
| Invariantes | I10 |

### T04 · ERP caído

Origen: v1.0 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Snapshot ausente o vencido. |
| Datos iniciales | Orden WO-2 sin snapshot válido. |
| Inyección | ERP devuelve 503 y timeouts durante toda la prueba. |
| Estados esperados | Detecciones WAITING_ERP y luego EXPIRED con Reason ERP_UNAVAILABLE. |
| Eventos publicados | PrintJobResult EXPIRED sin job por cada detección. |
| Efectos físicos esperados | Ninguna etiqueta. |
| Métricas afectadas | erp_cache_age_seconds; detections_expired_total{reason=ERP_UNAVAILABLE}. |
| Criterio de aprobación | Cero jobs con lote o producto no validados. |
| Transiciones cubiertas | D03, D07 |
| Invariantes | I10 |

### T05 · Impresora no conecta antes de enviar

Origen: v1.0 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | MaxAutoAttempts = 3; plazo suficiente para los reintentos. |
| Datos iniciales | Job READY. |
| Inyección | El simulador rechaza la conexión TCP en todos los intentos. |
| Estados esperados | Intentos NOT_SENT; job SENDING → RETRY_WAIT → READY en ciclo; al agotar intentos, FAILED con PRINTER_UNAVAILABLE. |
| Eventos publicados | PrintJobResult FAILED. |
| Efectos físicos esperados | Ninguna etiqueta. |
| Métricas afectadas | print_attempts_total{outcome=NOT_SENT} = 3; bytes recibidos por el simulador = 0. |
| Criterio de aprobación | El simulador registra 0 bytes recibidos en cada intento NOT_SENT; ningún UNKNOWN innecesario. El criterio no usa WriteStartedAt: se persiste después de escribir y no prueba nada en SQL (I02). |
| Transiciones cubiertas | A02, J06, J13, J07 |
| Invariantes | I02 |

### T06 · Respuesta perdida después de transmitir

Origen: v1.0 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Simulador configurado para recibir e imprimir y no responder. |
| Datos iniciales | Job READY. |
| Inyección | Corte del canal a mitad de la escritura; minutos después llega la lectura tardía del serial correcto. |
| Estados esperados | Intento UNKNOWN; job UNKNOWN con WRITE_INTERRUPTED; luego CONFIRMED por reconciliación. |
| Eventos publicados | PrintJobResult UNKNOWN (final false) y después CONFIRMED con resultVersion mayor. |
| Efectos físicos esperados | Exactamente una etiqueta. |
| Métricas afectadas | printer_unknown_total; duplicate_physical_transmissions_total = 0. |
| Criterio de aprobación | Cero retransmisiones automáticas; la reconciliación conserva el mismo PrintJobId y LabelSerial. |
| Transiciones cubiertas | A04, J09, A08, J15 |
| Invariantes | I02, I03, I05 |

### T07 · Reinicio del servicio en SENDING

Origen: v1.0 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Puntos de falla inyectables en el sender. |
| Datos iniciales | Job READY. |
| Inyección | kill -9 en tres puntos: tras el commit del claim y antes de escribir; durante la escritura; tras escribir y antes de TX-C. |
| Estados esperados | En los tres casos el arranque pasa intento y job a UNKNOWN con RESTART_DURING_SEND; nunca a READY. |
| Eventos publicados | PrintJobResult UNKNOWN. |
| Efectos físicos esperados | Cero o una etiqueta según el punto; nunca dos. |
| Métricas afectadas | restart_recovered_sending_total. |
| Criterio de aprobación | Se documenta el falso UNKNOWN del primer punto como costo aceptado; ninguna transacción SQL abierta durante el I/O. |
| Transiciones cubiertas | J09, A04 |
| Invariantes | I02, I03, I19, I20 |

### T08 · RabbitMQ caído

Origen: v1.0 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Carga nominal. |
| Datos iniciales | — |
| Inyección | Detener RabbitMQ 10 minutos y reanudarlo. |
| Estados esperados | Detecciones y jobs avanzan igual; filas del Outbox pendientes. |
| Eventos publicados | Al volver, todos los eventos con el mismo EventId, en orden de OutboxSequence. |
| Efectos físicos esperados | Sin cambio en la impresión. |
| Métricas afectadas | outbox_pending_age_seconds; latencias de impresión sin cambio. |
| Criterio de aprobación | Cero eventos perdidos; consumidores sin efectos duplicados. |
| Transiciones cubiertas | — |
| Invariantes | I22 |

### T09 · SQL caído con journal (perfil extendido)

Origen: v1.0 · Nivel: FAULT · Perfil: EXTENDIDO · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | PLC con FIFO y AckCycleId simulados; journal SQLite con synchronous=FULL. |
| Datos iniciales | — |
| Inyección | Detener SQL 30 s bajo carga. |
| Estados esperados | Ciclos capturados en el journal y ACK al PLC solo tras commit; ningún job nuevo sin SQL. |
| Eventos publicados | Al volver SQL, CartonDetected de cada ciclo capturado. |
| Efectos físicos esperados | Ninguna etiqueta durante la caída. |
| Métricas afectadas | gateway_journal_depth; labeling_ready = 0 durante la caída. |
| Criterio de aprobación | Ningún ciclo con ACK se pierde; ningún ACK precede al commit del journal. |
| Transiciones cubiertas | — |
| Invariantes | I21 |

### T10 · Cambio de lote en carrera

Origen: v1.0 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Snapshot revisión R1 vigente. |
| Datos iniciales | Job creado con R1. |
| Inyección | Llega R2 del ERP mientras el job está READY; un proceso intenta actualizar Payload. |
| Estados esperados | El job conserva R1; el UPDATE de Payload falla por TR_PrintJob_Immutable. |
| Eventos publicados | Resultados con ErpRevision R1. |
| Efectos físicos esperados | La etiqueta lleva el lote de R1. |
| Métricas afectadas | — |
| Criterio de aprobación | Payload y PayloadHash idénticos al momento de creación. |
| Transiciones cubiertas | — |
| Invariantes | I09, I10 |

### T11 · Reconexión OPC con FIFO (perfil extendido)

Origen: v1.0 · Nivel: FAULT · Perfil: EXTENDIDO · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | PLC con FIFO de ciclos. |
| Datos iniciales | Marca alta en el ciclo 1000. |
| Inyección | Desconectar el gateway mientras pasan 20 cajas; reconectar. |
| Estados esperados | Hueco 1001-1020 OPEN; la FIFO entrega los 20 como RECOVERED y EXPIRED por LATE_ARRIVAL; hueco RECOVERED. |
| Eventos publicados | 20 CartonDetected y 20 PrintJobResult EXPIRED sin job. |
| Efectos físicos esperados | Ninguna impresión tardía. |
| Métricas afectadas | opc_reconnect_total; sequence_gaps_open. |
| Criterio de aprobación | Ningún ciclo duplicado ni impreso fuera de plazo. |
| Transiciones cubiertas | G01, G02, D04 |
| Invariantes | I04, I16 |

### T12 · Carga sostenida y capacidad

Origen: v1.0 · Nivel: LOAD · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Tasa de cajas y tamaño de payload acordados con OT; intervalos OPC negociados registrados. |
| Datos iniciales | — |
| Inyección | 30 minutos a la tasa pactada y 10 minutos al doble. |
| Estados esperados | Backlog estable a tasa pactada. |
| Eventos publicados | — |
| Efectos físicos esperados | Tasa de llegada menor que la capacidad de la impresora. |
| Métricas afectadas | Histogramas de los seis tramos de latencia (ver sección de rendimiento). |
| Criterio de aprobación | Se reportan p50, p95, p99 y máximo con hardware declarado; se comprueba la desigualdad de factibilidad del plazo, incluido el DeviceLeadTimeMs medido (send_start a etiqueta aplicada). |
| Transiciones cubiertas | — |
| Invariantes | I14 |

### T13 · Snapshot del PLC incoherente

Origen: v1.0 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Simulador OPC capaz de publicar campos de ciclos distintos. |
| Datos iniciales | — |
| Inyección | CycleId de un ciclo con WorkOrderId de otro. Variante: con DeadlineReference = PLC_TIMESTAMP, un registro sin DetectionTimestamp. |
| Estados esperados | Detección BLOCKED con INCOHERENT_SNAPSHOT. Variante: también BLOCKED con INCOHERENT_SNAPSHOT y DeadlineAt = ReceivedAt (CK_CartonDetection_PlcTimestamp). |
| Eventos publicados | CartonDetected y PrintJobResult BLOCKED sin job. |
| Efectos físicos esperados | Ninguna etiqueta. |
| Métricas afectadas | detections_blocked_total{reason=INCOHERENT_SNAPSHOT}. |
| Criterio de aprobación | Ningún job con datos mezclados. |
| Transiciones cubiertas | D05 |
| Invariantes | I10 |

### T14 · Resultado republicado

Origen: v1.0 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Consumidor con InboxMessage. |
| Datos iniciales | Un PrintJobResult. |
| Inyección | Publicar el mismo evento 3 veces. |
| Estados esperados | Una sola fila en InboxMessage; un solo efecto. |
| Eventos publicados | — |
| Efectos físicos esperados | — |
| Métricas afectadas | inbox_duplicates_total = 2. |
| Criterio de aprobación | El consumidor aplica la versión mayor por (idempotencyKey, labelType). |
| Transiciones cubiertas | — |
| Invariantes | I22 |

### T15 · Confirmación física por escáner

Origen: v1.0 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | MinConfirmationLevel = VERIFIED; escáner correlacionado por tracking del PLC. |
| Datos iniciales | Job READY. |
| Inyección | El escáner lee el LabelSerial correcto asociado al ciclo. |
| Estados esperados | Intento SENDING → TRANSMITTED → CONFIRMED; job TRANSMITTED → CONFIRMED. |
| Eventos publicados | PrintJobResult CONFIRMED con evidence.level VERIFIED y correlatedTo CYCLE. |
| Efectos físicos esperados | Una etiqueta verificada. |
| Métricas afectadas | send_to_verified_ms. |
| Criterio de aprobación | readSerial = labelSerial; TR_PrintAttempt_Guard acepta la confirmación solo con esa EvidenceObservation. |
| Transiciones cubiertas | A03, J05, A05, J10 |
| Invariantes | I05, I06 |

### T16 · Caja recirculada con CartonId

Origen: v1.0 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | CartonIdentityMode = CARTON_ID_REQUIRED; el PLC entrega CartonId. |
| Datos iniciales | Caja C-77 con job CONFIRMED. |
| Inyección | C-77 vuelve a pasar y genera un ciclo nuevo. Variante: dos ciclos de C-77 llegan sin snapshot y quedan WAITING_ERP; después llega el snapshot. |
| Estados esperados | Segunda detección BLOCKED con RECIRCULATED_CARTON (D05). Variante: el primero JOB_CREATED (D06); el segundo BLOCKED con RECIRCULATED_CARTON al repetir la validación en TX-E (D08). |
| Eventos publicados | PrintJobResult BLOCKED sin job. |
| Efectos físicos esperados | Ninguna segunda etiqueta. |
| Métricas afectadas | detections_blocked_total{reason=RECIRCULATED_CARTON}. |
| Criterio de aprobación | La validación ocurre en TX-A y en TX-E con bloqueo de rango sobre IX_PrintJob_Carton e IX_CartonDetection_Carton; un 1205 se reintenta y termina en el mismo resultado. |
| Transiciones cubiertas | D05, D06, D08 |
| Invariantes | I07 |

### T17 · ERP vuelve después del plazo

Origen: rev1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Detección WAITING_ERP. |
| Datos iniciales | DeadlineAt en 800 ms. |
| Inyección | El snapshot válido llega a los 2 s. |
| Estados esperados | Detección EXPIRED con ERP_UNAVAILABLE; ningún job. |
| Eventos publicados | PrintJobResult EXPIRED sin job. |
| Efectos físicos esperados | Cero bytes a la impresora. |
| Métricas afectadas | detections_expired_total{reason=ERP_UNAVAILABLE}. |
| Criterio de aprobación | La llegada del snapshot no crea job. |
| Transiciones cubiertas | D07 |
| Invariantes | I04, I10 |

### T18 · Backoff mayor que el plazo restante

Origen: rev1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | RetryBackoffMs mayor que el plazo restante. |
| Datos iniciales | Job en RETRY_WAIT. |
| Inyección | Dejar correr el backoff. |
| Estados esperados | Job EXPIRED con DEADLINE_PASSED. |
| Eventos publicados | PrintJobResult EXPIRED. |
| Efectos físicos esperados | Cero bytes. |
| Métricas afectadas | jobs_expired_total{from=RETRY_WAIT}. |
| Criterio de aprobación | Nunca se pasa a READY con el plazo vencido. |
| Transiciones cubiertas | J14 |
| Invariantes | I04 |

### T19 · SQL caído (perfil mínimo)

Origen: rev1 · Nivel: FAULT · Perfil: MINIMO · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Sin journal; latido de etiquetadora lista al PLC. |
| Datos iniciales | Marca alta en el ciclo 500. |
| Inyección | Detener SQL 30 s mientras pasan 25 cajas. |
| Estados esperados | Falla cerrado; al volver, hueco 501-525 OPEN y luego CLOSED_UNRECOVERED con UnrecoveredCount = 25. |
| Eventos publicados | Ningún PrintJobResult para el hueco; alerta operacional. |
| Efectos físicos esperados | Cero impresiones durante y después de la caída para esos ciclos. |
| Métricas afectadas | cycles_unobserved_total = 25; labeling_ready = 0 durante la caída. |
| Criterio de aprobación | Ninguna detección inventada para el hueco. |
| Transiciones cubiertas | G01, G03 |
| Invariantes | I04, I16 |

### T20 · Replay tardío del journal (perfil extendido)

Origen: rev1 · Nivel: FAULT · Perfil: EXTENDIDO · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Journal con ciclos capturados durante una caída de SQL. |
| Datos iniciales | Ciclos con plazo ya vencido. |
| Inyección | Restaurar SQL y reproducir el journal. |
| Estados esperados | Detecciones RECOVERED y EXPIRED con LATE_ARRIVAL. |
| Eventos publicados | CartonDetected y PrintJobResult EXPIRED sin job. |
| Efectos físicos esperados | Cero bytes. |
| Métricas afectadas | detections_expired_total{reason=LATE_ARRIVAL}. |
| Criterio de aprobación | Ningún job para registros vencidos. |
| Transiciones cubiertas | D04 |
| Invariantes | I04 |

### T21 · Claim con plazo vencido

Origen: rev1 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Job READY con DeadlineAt en el pasado. |
| Datos iniciales | — |
| Inyección | Ejecutar el claim y después el barrido. |
| Estados esperados | El claim afecta 0 filas; el barrido pasa el job a EXPIRED. |
| Eventos publicados | PrintJobResult EXPIRED. |
| Efectos físicos esperados | Cero bytes. |
| Métricas afectadas | claim_rejected_total{cause=deadline}. |
| Criterio de aprobación | Ningún PrintAttempt creado; el claim no deja fila en @claimed y no lanza excepción. |
| Transiciones cubiertas | J03 |
| Invariantes | I04 |

### T22 · El escáner lee el serial de otro job

Origen: rev1 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | MinConfirmationLevel = VERIFIED. |
| Datos iniciales | Job A TRANSMITTED. |
| Inyección | Evidencia correlacionada con el ciclo de A pero con el LabelSerial de B. |
| Estados esperados | EvidenceObservation registrada; intento UNKNOWN; job A UNKNOWN con SERIAL_MISMATCH; nunca CONFIRMED. |
| Eventos publicados | PrintJobResult UNKNOWN con la lectura contradictoria (evidence.verified = false). |
| Efectos físicos esperados | La caja de A lleva una etiqueta ajena: incidente. |
| Métricas afectadas | serial_mismatch_total; alerta inmediata. |
| Criterio de aprobación | TR_PrintAttempt_Guard rechaza confirmar con ese serial aunque el código lo intente; el schema no puede comprobarlo. |
| Transiciones cubiertas | J11, A06 |
| Invariantes | I06 |

### T23 · Contratos inválidos

Origen: rev1 · Nivel: DOC · Perfil: AMBOS · Estado: Ejecutada en esta revisión

| Campo | Valor |
| --- | --- |
| Precondiciones | Schemas Draft 2020-12. |
| Datos iniciales | contracts/examples/valid e invalid. |
| Inyección | Validar todos los ejemplos; aplicar reglas de negocio a los válidos. |
| Estados esperados | — |
| Eventos publicados | — |
| Efectos físicos esperados | — |
| Métricas afectadas | — |
| Criterio de aprobación | Todos los válidos pasan schema y reglas de negocio; cada inválido falla solo por la palabra clave y la ruta declaradas en EXPECTED.json; cada ejemplo de negocio pasa el schema y falla exactamente su regla. |
| Transiciones cubiertas | — |
| Invariantes | I06, I13 |

### T24 · Contador reiniciado sin cambio de época

Origen: rev1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Época 42 con ciclos 1-5000 registrados. |
| Datos iniciales | — |
| Inyección | El PLC reinicia CycleId a 1 sin incrementar PlcEpoch y continúa hasta 5200; los ciclos 1 a 5000 repiten claves existentes. |
| Estados esperados | El ciclo 1 choca con la clave existente: SequenceAnomaly IDENTITY_COLLISION BLOCKING y estación bloqueada. Los ciclos 2 a 5000 también chocan: cada uno queda como SequenceAnomaly con su registro completo, no como detección. Las claves nuevas 5001 a 5200 llegadas durante el bloqueo quedan BLOCKED con STATION_DISABLED. |
| Eventos publicados | Ningún CartonDetected ni PrintJobResult para los ciclos en colisión: no son detecciones. CartonDetected y PrintJobResult BLOCKED para 5001 a 5200. Alerta operacional. |
| Efectos físicos esperados | Ninguna etiqueta; señal de lista baja. Las cajas en colisión no tienen resultado publicado y la planta las trata según OT-09. |
| Métricas afectadas | plc_sequence_anomaly_total{type=IDENTITY_COLLISION}. |
| Criterio de aprobación | La detección existente nunca se reutiliza; cada colisión queda en SequenceAnomaly; ninguna caja recibe etiqueta durante el bloqueo. |
| Transiciones cubiertas | D05 |
| Invariantes | I08, I15 |

### T25 · Contador reiniciado con cambio de época

Origen: rev1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Dos corridas: EpochChangePolicy AUTO_INCREMENT_BY_ONE y REQUIRE_CONFIRMATION. |
| Datos iniciales | Época 42. |
| Inyección | El PLC pasa a época 43 y reinicia CycleId; en una tercera corrida salta a época 45. |
| Estados esperados | AUTO: época 43 aceptada y cajas normales. REQUIRE: bloqueo hasta confirmación; los registros retenidos se reingresan con Transport ANOMALY_REINGEST (RECOVERED) y su propio plazo decide. Salto a 45: EPOCH_UNEXPECTED y bloqueo. |
| Eventos publicados | Según el caso. |
| Efectos físicos esperados | Etiquetas solo en el caso aceptado. |
| Métricas afectadas | plc_epoch_changes_total; plc_sequence_anomaly_total{type=EPOCH_UNEXPECTED}. |
| Criterio de aprobación | PlcEpochHistory registra quién y cómo aceptó cada época; una aceptación manual sin OperatorAction CONFIRM_EPOCH la rechaza el trigger. La huella del EdgeHost coincide byte a byte con source_fingerprint de tools/verify_package.py, incluido un vector con caracteres que los serializadores escapan distinto (+, &, <, >, ', no ASCII). |
| Transiciones cubiertas | — |
| Invariantes | I08 |

### T26 · Rebote del sensor

Origen: rev1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | MinCycleIntervalMs configurado; DetectionTimestamp disponible. |
| Datos iniciales | — |
| Inyección | Dos ciclos separados por menos de MinCycleIntervalMs. |
| Estados esperados | Segunda detección BLOCKED con IMPLAUSIBLE_RATE. |
| Eventos publicados | PrintJobResult BLOCKED. |
| Efectos físicos esperados | Cero bytes para el segundo ciclo. |
| Métricas afectadas | detections_blocked_total{reason=IMPLAUSIBLE_RATE}. |
| Criterio de aprobación | Sin DetectionTimestamp la regla queda desactivada y se documenta. |
| Transiciones cubiertas | D05 |
| Invariantes | I08 |

### T27 · Dos instancias del EdgeHost

Origen: rev1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Dos procesos con la misma configuración, en el mismo host y en hosts distintos. |
| Datos iniciales | Jobs READY. |
| Inyección | Arrancar la segunda instancia con la primera activa. |
| Estados esperados | Mismo host: la segunda no obtiene el lock de SO por impresora. Hosts distintos: no obtiene el lease. En ambos casos 0 envíos y alerta. |
| Eventos publicados | — |
| Efectos físicos esperados | Un solo transmisor. |
| Métricas afectadas | printer_owner_conflicts_total. |
| Criterio de aprobación | Si la primera muere, los SENDING heredados pasan a UNKNOWN antes de que la segunda envíe. |
| Transiciones cubiertas | J09 |
| Invariantes | I11 |

### T28 · Coherencia del paquete

Origen: rev1 · Nivel: DOC · Perfil: AMBOS · Estado: Ejecutada en esta revisión

| Campo | Valor |
| --- | --- |
| Precondiciones | Python 3 con jsonschema y sqlfluff. |
| Datos iniciales | Paquete completo. |
| Inyección | python3 tools/verify_package.py |
| Estados esperados | — |
| Eventos publicados | — |
| Efectos físicos esperados | — |
| Métricas afectadas | — |
| Criterio de aprobación | Código de salida 0; las propiedades no verificables quedan listadas como pendientes de integración. |
| Transiciones cubiertas | — |
| Invariantes | I12, I13, I14 |

### T29 · Suspensión después del claim

Origen: rev2 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Punto de pausa inyectable entre el commit del claim y la escritura. |
| Datos iniciales | Job con plazo a 500 ms. |
| Inyección | Suspender el sender 2 s tras el claim y reanudarlo. |
| Estados esperados | La segunda comprobación con reloj monotónico rechaza: intento NOT_SENT, job EXPIRED. |
| Eventos publicados | PrintJobResult EXPIRED. |
| Efectos físicos esperados | Cero bytes. |
| Métricas afectadas | presend_check_rejected_total; presend_check_to_write_ms. |
| Criterio de aprobación | Se documenta el límite: una pausa entre la comprobación y la llamada de escritura no se puede excluir por software. |
| Transiciones cubiertas | A02, J08 |
| Invariantes | I04 |

### T30 · Evento OPC antiguo fuera de orden

Origen: rev2 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Marca alta en el ciclo 2000. |
| Datos iniciales | El ciclo 1998 nunca se registró. |
| Inyección | Llega una retransmisión válida del ciclo 1998 con DetectionTimestamp anterior al de 2000. |
| Estados esperados | Si su plazo venció: detección EXPIRED con LATE_ARRIVAL. Si sigue vigente: job creado y, como la impresora ya reclamó el ciclo 2000, el job vence con OUT_OF_ORDER (J03). Sin anomalía de reinicio. |
| Eventos publicados | CartonDetected y su resultado. |
| Efectos físicos esperados | Ninguna impresión de 1998 después de 2000: en print-and-apply cruzaría etiquetas entre cajas. |
| Métricas afectadas | out_of_order_detections_total. |
| Criterio de aprobación | La estación no se bloquea; ningún envío fuera de orden. |
| Transiciones cubiertas | D04, J03 |
| Invariantes | I08, I23 |

### T31 · Etiqueta retenida en la cola del dispositivo

Origen: rev2 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Simulador con cola interna; MinConfirmationLevel = APPLIED. |
| Datos iniciales | Job READY. |
| Inyección | Bytes transmitidos antes del plazo; el simulador retiene la etiqueta hasta después de la ventana física. Variante 1: el simulador informa 'no impreso' sin confirmar cancelación. Variante 2: confirma cancelación y cola sin el job. |
| Estados esperados | Sin evidencia APPLIED a tiempo: job UNKNOWN con EVIDENCE_WINDOW_ELAPSED. Variante 1: sigue UNKNOWN (un 'no impreso' sin cancelación no basta). Variante 2: FAILED con DEVICE_REPORTED_NOT_PRINTED. |
| Eventos publicados | PrintJobResult UNKNOWN o FAILED. |
| Efectos físicos esperados | Posible etiqueta fuera de ventana: el sistema no la declara aplicada. |
| Métricas afectadas | send_to_printed_ms; printed_to_applied_ms. |
| Criterio de aprobación | Nunca CONFIRMED sin evidencia del nivel exigido. |
| Transiciones cubiertas | J11, A06, J12, A07 |
| Invariantes | I05 |

### T32 · Recirculación sin identidad física

Origen: rev2 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | CartonIdentityMode = CYCLE_ONLY; sin CartonId. |
| Datos iniciales | Una caja ya etiquetada. |
| Inyección | La misma caja genera un segundo ciclo válido. |
| Estados esperados | Segunda detección JOB_CREATED y segundo job. |
| Eventos publicados | Dos resultados con idempotencyKey distinta. |
| Efectos físicos esperados | La caja puede recibir dos etiquetas: comportamiento documentado de la garantía por ciclo. |
| Métricas afectadas | Sin métrica posible: la recirculación no es observable sin identidad física. |
| Criterio de aprobación | La documentación y la matriz de garantías declaran la limitación. |
| Transiciones cubiertas | D02 |
| Invariantes | I07 |

### T33 · Cambio de configuración con job pendiente

Origen: rev2 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Job READY creado con la revisión 7. |
| Datos iniciales | Revisión 7: TransitBudgetMs 1500, SendMarginMs 100. |
| Inyección | Se activa la revisión 8 con TransitBudgetMs 1000 y SendMarginMs 150; se intenta UPDATE sobre la revisión 7. |
| Estados esperados | El job conserva ConfigRevisionId 7 y DeadlineAt; el claim usa el plazo efectivo más restrictivo; el UPDATE falla por TR_StationConfigRevision_Immutable. |
| Eventos publicados | Resultados con configRevision 7. |
| Efectos físicos esperados | Comportamiento determinista. |
| Métricas afectadas | config_revision_active. |
| Criterio de aprobación | Ninguna modificación retroactiva silenciosa. |
| Transiciones cubiertas | — |
| Invariantes | I09, I17 |

### T34 · Reimpresión con incertidumbre

Origen: rev2 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Job UNKNOWN; caja retenida. |
| Datos iniciales | Intento original UNKNOWN. |
| Inyección | Un operador pide reimprimir: primero sin autorización, después con autorización aprobada por otra persona. Además: insertar un intento ORDINARY sobre el job UNKNOWN; fijar SESSION_CONTEXT EXCEPTION y pasar el job a CONFIRMED sin intento excepcional. |
| Estados esperados | Sin autorización: rechazo. Con autorización: R01 APPROVED, R02 EXECUTED, intento EXCEPTIONAL_REPRINT CONFIRMED y job CONFIRMED por X01. El intento ORDINARY y el UPDATE sin intento los rechazan TR_PrintAttempt_Guard y TR_PrintJob_StateMachine. |
| Eventos publicados | PrintJobResult CONFIRMED con exceptionalReprint y duplicateRisk ACCEPTED. |
| Efectos físicos esperados | Puede existir una segunda etiqueta del job original: riesgo declarado. |
| Métricas afectadas | exceptional_reprints_total{risk=ACCEPTED}. |
| Criterio de aprobación | Ningún job nuevo; mismo LabelSerial; ningún UPDATE UNKNOWN → SENDING aceptado por el trigger. |
| Transiciones cubiertas | R01, R02, A01, X01 |
| Invariantes | I01, I03, I18 |

### T35 · Hueco de secuencia sin timestamps

Origen: rev2 · Nivel: FAULT · Perfil: MINIMO · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | PLC sin FIFO. |
| Datos iniciales | Marca alta en el ciclo 300. |
| Inyección | Llega el ciclo 310 sin datos de los ciclos 301-309. |
| Estados esperados | SequenceGap 301-309 OPEN y después CLOSED_UNRECOVERED con UnrecoveredCount 9. |
| Eventos publicados | Ningún CartonDetected ni PrintJobResult para el hueco. |
| Efectos físicos esperados | Desconocidos: el sistema no afirma nada sobre esas cajas. |
| Métricas afectadas | cycles_unobserved_total = 9. |
| Criterio de aprobación | Ninguna detección fabricada; ningún plazo calculado sin timestamp. |
| Transiciones cubiertas | G01, G03 |
| Invariantes | I07, I16 |

### T36 · Agente suspendido después de validar su token

Origen: v1.1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Dos agentes en hosts distintos; impresora simulada. |
| Datos iniciales | Agente A con token 12. |
| Inyección | A valida su token y se suspende; vence el lease; B obtiene el token 13 y transmite; A reanuda y transmite. Corrida 1: solo lease en SQL. Corrida 2: aislamiento de red que solo deja pasar al dueño activo. |
| Estados esperados | Corrida 1: el simulador registra dos transmisiones (la configuración se declara no permitida). Corrida 2: la escritura de A falla; su intento queda NOT_SENT o UNKNOWN según el punto. |
| Eventos publicados | — |
| Efectos físicos esperados | Corrida 1: duplicado posible. Corrida 2: un solo transmisor. |
| Métricas afectadas | printer_owner_conflicts_total. |
| Criterio de aprobación | Demuestra que el fencing token en SQL no protege el canal físico. |
| Transiciones cubiertas | — |
| Invariantes | I11, I12 |

### T37 · Desfase de reloj del PLC

Origen: v1.1 · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | DeadlineReference = PLC_TIMESTAMP; ClockSkewToleranceMs = 50. |
| Datos iniciales | — |
| Inyección | Adelantar el reloj del PLC 300 ms. Variante: retroceder 2 s el reloj del EdgeHost (ajuste de NTP) con jobs READY. |
| Estados esperados | Estación bloqueada con CLOCK_SKEW; detecciones siguientes BLOCKED con STATION_DISABLED. Variante: la divergencia entre UtcNow y Stopwatch supera ClockJumpToleranceMs; estación bloqueada con CLOCK_JUMP; ningún claim se evalúa con el reloj saltado. |
| Eventos publicados | PrintJobResult BLOCKED. |
| Efectos físicos esperados | Ninguna etiqueta mientras dure el desfase. |
| Métricas afectadas | clock_skew_ms. |
| Criterio de aprobación | Nunca se calcula un plazo ni se evalúa un claim con un reloj fuera de tolerancia. |
| Transiciones cubiertas | D05 |
| Invariantes | I04 |

### T38 · Ventana de evidencia vencida y estación sin evidencia

Origen: v1.1 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Corrida 1: MinConfirmationLevel = PRINTED. Corrida 2: NONE. |
| Datos iniciales | Job READY. |
| Inyección | Transmitir sin que llegue evidencia. |
| Estados esperados | Corrida 1: TRANSMITTED → UNKNOWN con EVIDENCE_WINDOW_ELAPSED. Corrida 2: TRANSMITTED terminal. |
| Eventos publicados | Corrida 1: UNKNOWN. Corrida 2: TRANSMITTED con final true y sin evidence. |
| Efectos físicos esperados | Desconocidos en ambos casos; el resultado no afirma impresión. |
| Métricas afectadas | evidence_window_elapsed_total. |
| Criterio de aprobación | TRANSMITTED nunca se publica como CONFIRMED. |
| Transiciones cubiertas | J11, A06, J05 |
| Invariantes | I05 |

### T39 · Cancelación autorizada

Origen: v1.1 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Job READY; otro job con un intento TRANSMITTED. |
| Datos iniciales | — |
| Inyección | Cancelar ambos. |
| Estados esperados | El READY pasa a CANCELLED con OperatorAction CANCEL_JOB en LastOperatorActionId; el segundo se rechaza porque tiene MayHaveSent = 1. Un UPDATE a CANCELLED sin OperatorAction lo rechaza el trigger. |
| Eventos publicados | PrintJobResult CANCELLED con operatorAction. |
| Efectos físicos esperados | Ninguna etiqueta del cancelado. |
| Métricas afectadas | — |
| Criterio de aprobación | La cancelación queda registrada en OperatorAction con identidad y motivo. |
| Transiciones cubiertas | J04 |
| Invariantes | I03, I24 |

### T40 · Reconciliación negativa y cierre operativo

Origen: v1.1 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Dos jobs UNKNOWN con cajas retenidas. |
| Datos iniciales | — |
| Inyección | Job 1: el dispositivo confirma no impresión, cola vacía, inspección sin etiqueta. Job 2: sin pruebas; la caja se rechaza. |
| Estados esperados | Job 1: OperatorAction RECONCILE_NEGATIVE con las tres pruebas, intento NOT_PRINTED (A09) y job FAILED con RECONCILED_NOT_PRINTED. Sin una de las pruebas, CK_OperatorAction_Target rechaza el INSERT. Job 2: OperatorAction CLOSE_UNRESOLVED y job CLOSED_UNRESOLVED con CLOSED_CARTON_REJECTED. |
| Eventos publicados | PrintJobResult FAILED y CLOSED_UNRESOLVED, ambos con operatorAction. |
| Efectos físicos esperados | Ninguna etiqueta nueva. |
| Métricas afectadas | unknown_resolution_total{result}. |
| Criterio de aprobación | Cada cierre registra persona, motivo y evidencia en OperatorAction; ningún cierre sin ella. |
| Transiciones cubiertas | J16, A09, J17 |
| Invariantes | I18, I24 |

### T41 · Autorizaciones revocadas, vencidas y etiqueta dañada

Origen: v1.1 · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Tres autorizaciones aprobadas. |
| Datos iniciales | Un job CONFIRMED con etiqueta dañada. |
| Inyección | Revocar una; dejar vencer otra; ejecutar la tercera (LABEL_DAMAGED) primero sin retiro registrado y después con retiro registrado. Además: usar la autorización de otro job; dirigir una reimpresión a la impresora de línea; confirmar la reimpresión con la lectura VERIFIED anterior del mismo serial. |
| Estados esperados | REVOKED y EXPIRED no permiten intento (TX-X no obtiene fila en @executed). LABEL_DAMAGED sin retiro: TR_PrintAttempt_Guard rechaza. Con retiro: EXECUTED en la ManualPrinterId y el job sigue CONFIRMED (X02) solo con una EvidenceObservation que referencia al intento de reemplazo. La autorización de otro job la rechaza la FK compuesta; la impresora de línea, TR_ReprintAuthorization_Guard; la lectura anterior, TR_PrintAttempt_Guard. |
| Eventos publicados | PrintJobResult CONFIRMED con resultVersion mayor y exceptionalReprint. |
| Efectos físicos esperados | Una etiqueta de reemplazo impresa fuera de la línea; la original retirada antes de reimprimir. |
| Métricas afectadas | exceptional_reprints_total{risk=REPLACES_EXISTING_LABEL}. |
| Criterio de aprobación | Ninguna autorización se usa dos veces (UX_PrintAttempt_Authorization). |
| Transiciones cubiertas | R03, R04, X02 |
| Invariantes | I18 |

### T42 · Envío fuera de orden

Origen: revisión independiente · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Print-and-apply simulado que aplica en orden de impresión; MaxAutoAttempts = 3. |
| Datos iniciales | Jobs N y N+1 READY en la misma impresora. |
| Inyección | El primer intento de N falla antes de escribir (RETRY_WAIT); N+1 se reclama y transmite; vence el backoff de N. |
| Estados esperados | N+1 TRANSMITTED; PrinterSendOrder en N+1; el claim de N no obtiene fila y N termina EXPIRED con OUT_OF_ORDER (J14). |
| Eventos publicados | PrintJobResult EXPIRED con OUT_OF_ORDER para N. |
| Efectos físicos esperados | Ninguna etiqueta de N después de la de N+1. |
| Métricas afectadas | jobs_expired_total{reason=OUT_OF_ORDER}. |
| Criterio de aprobación | Ningún envío con un ciclo anterior al último reclamado por la impresora; un reintento del mismo job sigue permitido. |
| Transiciones cubiertas | J06, J14 |
| Invariantes | I04, I23 |

### T43 · Reimpresión excepcional sin confirmar

Origen: revisión independiente · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Job EXPIRED con DEADLINE_PASSED; caja retenida; ManualPrinterId configurada. |
| Datos iniciales | Autorización EXPIRED_CARTON_HELD con DuplicateRisk NONE_PROVEN. |
| Inyección | El sender de la impresora manual ejecuta TX-X y transmite; no llega evidencia en EvidenceWindowMs. Variante: el sender muere durante la escritura y al arrancar TX-R0 encuentra el intento abierto. Después se intenta otra autorización NONE_PROVEN. Más tarde llega la inspección que lee el serial correcto y referencia al intento. |
| Estados esperados | Intento excepcional UNKNOWN (A06, o A04 en la variante) y, en la misma transacción, job UNKNOWN con EXCEPTIONAL_REPRINT_UNRESOLVED (X03). La segunda autorización con NONE_PROVEN la rechaza el CHECK (estado previo UNKNOWN exige ACCEPTED). La inspección tardía confirma el intento (A08) y el job (X01). |
| Eventos publicados | PrintJobResult UNKNOWN final false con exceptionalReprint; después CONFIRMED con resultVersion mayor. |
| Efectos físicos esperados | Una etiqueta de reimpresión en la caja retenida; ninguna en la línea. |
| Métricas afectadas | exceptional_reprints_total{result=UNRESOLVED}. |
| Criterio de aprobación | El job nunca queda en un estado que afirme que no hay etiqueta mientras una reimpresión pudo imprimir. |
| Transiciones cubiertas | X03, A06, A08, X01 |
| Invariantes | I18, I25 |

### T44 · Evidencia contradictoria después de CONFIRMED

Origen: revisión independiente · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | MinConfirmationLevel = APPLIED; escáner aguas abajo correlacionado por ciclo. |
| Datos iniciales | Job A CONFIRMED con evidencia APPLIED. |
| Inyección | El escáner lee en la caja del ciclo de A el LabelSerial de B. Después: devolver A a CONFIRMED con el intento ordinario ya confirmado; cambiar solo Reason a WRITE_INTERRUPTED y reintentar J15; fijar Reason EXCEPTIONAL_REPRINT_UNRESOLVED para habilitar X01. |
| Estados esperados | EvidenceObservation registrada; job A UNKNOWN con SERIAL_MISMATCH (J18). Los tres intentos posteriores los rechaza TR_PrintJob_StateMachine: tras SERIAL_MISMATCH no hay reconciliación automática y Reason no cambia fuera de una transición sembrada. |
| Eventos publicados | PrintJobResult UNKNOWN final false con resultVersion mayor y la lectura contradictoria. |
| Efectos físicos esperados | Mal etiquetado detectado: incidente. |
| Métricas afectadas | serial_mismatch_total; alerta inmediata. |
| Criterio de aprobación | Un resultado CONFIRMED final se reemplaza por una versión mayor, nunca en silencio. |
| Transiciones cubiertas | J18 |
| Invariantes | I06, I25 |

### T45 · Registros sin cota de latencia con GATEWAY_RECEIPT

Origen: revisión independiente · Nivel: FAULT · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | DeadlineReference = GATEWAY_RECEIPT; PLC sin DetectionTimestamp. |
| Datos iniciales | Marca alta en el ciclo 700. |
| Inyección | Tras una desconexión llegan el ciclo 701 por READ, el 702 por REPUBLISH, el 703 en vivo con Overflow y un registro retenido reingresado (ANOMALY_REINGEST). Al recrear la suscripción, su primera notificación trae el ciclo 730 (valor actual). Después llega en vivo un ciclo que abre un hueco, y uno con CycleId menor que la marca alta, clave nueva y sin timestamp. |
| Estados esperados | 701, 702, 703, el reingreso y la primera notificación (registrada como READ): EXPIRED con LATE_ARRIVAL y DeadlineAt = ReceivedAt. El ciclo que abre un hueco: también LATE_ARRIVAL. Un intento de crear job lo rechaza CK_CartonDetection_GatewayLive. El ciclo menor: AMBIGUOUS_DECREASE y detección BLOCKED con SEQUENCE_ANOMALY. |
| Eventos publicados | CartonDetected y PrintJobResult EXPIRED o BLOCKED sin job. |
| Efectos físicos esperados | Cero bytes para esos ciclos. |
| Métricas afectadas | detections_expired_total{reason=LATE_ARRIVAL,transport}; plc_sequence_anomaly_total{type=AMBIGUOUS_DECREASE}. |
| Criterio de aprobación | Ningún plazo fabricado para un registro sin cota de latencia. |
| Transiciones cubiertas | D04, D05 |
| Invariantes | I08, I26 |

### T46 · Estación bloqueada mientras se espera el ERP

Origen: revisión independiente · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | Detección WAITING_ERP con plazo vigente. |
| Datos iniciales | — |
| Inyección | Bloquear la estación (por ejemplo IDENTITY_COLLISION en otro ciclo) y después entregar el snapshot. |
| Estados esperados | Detección BLOCKED con STATION_DISABLED (D08), no EXPIRED con ERP_UNAVAILABLE. |
| Eventos publicados | PrintJobResult BLOCKED sin job. |
| Efectos físicos esperados | Ninguna etiqueta. |
| Métricas afectadas | detections_blocked_total{reason=STATION_DISABLED}. |
| Criterio de aprobación | El motivo publicado es el real. |
| Transiciones cubiertas | D08 |
| Invariantes | I10 |

### T47 · DDL y sentencias de referencia en SQL Server

Origen: revisión independiente · Nivel: INTEGRATION · Perfil: AMBOS · Estado: No ejecutada

| Campo | Valor |
| --- | --- |
| Precondiciones | SQL Server 2022 en Testcontainers; conexión con ANSI_NULLS y QUOTED_IDENTIFIER en ON. |
| Datos iniciales | Base vacía; después datos de un escenario completo. |
| Inyección | Ejecutar el DDL y la semilla; ejecutar cada sentencia de database/sqlserver_operations_reference.sql con 0 filas afectadas y con filas afectadas; intentar cada transición prohibida y cada transición manual, excepcional o de corrección sin su respaldo o con un respaldo anterior; cambiar solo Reason; retroceder PrinterSendOrder; editar PlcEpochHistory; desbloquear una estación sin OperatorAction. |
| Estados esperados | El DDL se crea sin error (incluidos los índices filtrados); ninguna sentencia de 0 filas lanza excepción; ninguna sentencia falla con Msg 334; todo intento indebido falla con su error (51010 a 51032). |
| Eventos publicados | — |
| Efectos físicos esperados | — |
| Métricas afectadas | — |
| Criterio de aprobación | Salida de la ejecución adjunta al PR; ninguna regla de la sección 6 del documento principal queda sin prueba negativa. |
| Transiciones cubiertas | — |
| Invariantes | I03, I09, I24 |

## Cobertura de transiciones

| Transición | Máquina | Pruebas |
| --- | --- | --- |
| D01 | CartonDetection | T01 |
| D02 | CartonDetection | T01, T32 |
| D03 | CartonDetection | T03, T04 |
| D04 | CartonDetection | T11, T20, T30, T45 |
| D05 | CartonDetection | T13, T16, T24, T26, T37, T45 |
| D06 | CartonDetection | T03, T16 |
| D07 | CartonDetection | T04, T17 |
| D08 | CartonDetection | T16, T46 |
| J01 | PrintJob | T01 |
| J02 | PrintJob | T02 |
| J03 | PrintJob | T21, T30 |
| J04 | PrintJob | T39 |
| J05 | PrintJob | T15, T38 |
| J06 | PrintJob | T05, T42 |
| J07 | PrintJob | T05 |
| J08 | PrintJob | T29 |
| J09 | PrintJob | T06, T07, T27 |
| J10 | PrintJob | T15 |
| J11 | PrintJob | T22, T31, T38 |
| J12 | PrintJob | T31 |
| J13 | PrintJob | T05 |
| J14 | PrintJob | T18, T42 |
| J15 | PrintJob | T06 |
| J16 | PrintJob | T40 |
| J17 | PrintJob | T40 |
| J18 | PrintJob | T44 |
| X01 | PrintJob | T34, T43 |
| X02 | PrintJob | T41 |
| X03 | PrintJob | T43 |
| A01 | PrintAttempt | T02, T34 |
| A02 | PrintAttempt | T05, T29 |
| A03 | PrintAttempt | T15 |
| A04 | PrintAttempt | T06, T07 |
| A05 | PrintAttempt | T15 |
| A06 | PrintAttempt | T22, T31, T38, T43 |
| A07 | PrintAttempt | T31 |
| A08 | PrintAttempt | T06, T43 |
| A09 | PrintAttempt | T40 |
| G01 | SequenceGap | T11, T19, T35 |
| G02 | SequenceGap | T11 |
| G03 | SequenceGap | T19, T35 |
| R01 | ReprintAuthorization | T34 |
| R02 | ReprintAuthorization | T34 |
| R03 | ReprintAuthorization | T41 |
| R04 | ReprintAuthorization | T41 |

## Cobertura de invariantes

| Invariante | Pruebas |
| --- | --- |
| I01 | T01, T02, T34 |
| I02 | T05, T06, T07 |
| I03 | T06, T07, T34, T39, T47 |
| I04 | T11, T17, T18, T19, T20, T21, T29, T37, T42 |
| I05 | T06, T15, T31, T38 |
| I06 | T15, T22, T23, T44 |
| I07 | T16, T32, T35 |
| I08 | T24, T25, T26, T30, T45 |
| I09 | T10, T33 |
| I10 | T03, T04, T10, T13, T17, T46 |
| I11 | T27, T36 |
| I12 | T28, T36 |
| I13 | T23, T28 |
| I14 | T12, T28 |
| I15 | T24 |
| I16 | T11, T19, T35 |
| I17 | T33 |
| I18 | T34, T40, T41, T43 |
| I19 | T07 |
| I20 | T07 |
| I21 | T09 |
| I22 | T08, T14 |
| I23 | T30, T42 |
| I24 | T25, T39, T40, T47 |
| I25 | T43, T44, T14, T47 |
| I26 | T13, T23, T45 |
