# Rendimiento, capacidad y ciberseguridad (v1.1)

Todos los valores de este documento son hipótesis de diseño sin benchmark. Se reemplazan por mediciones en T12.

## Objetivos configurables

Los objetivos de latencia se configuran por estación después de medir. Lo que el diseño exige es la desigualdad de factibilidad con percentiles medidos:

> p99(`plc_to_opc_receive_ms`) + p99(`opc_receive_to_send_start_ms`) + `SendMarginMs` + `ClockSkewToleranceMs` + `DeviceLeadTimeMs` < `TransitBudgetMs`

Con `GATEWAY_RECEIPT`, `MaxUpstreamLatencyMs` reemplaza al primer término y `ClockSkewToleranceMs` desaparece. `DeviceLeadTimeMs` es el p99 de `send_start_to_applied_ms`: sin él, un envío iniciado a tiempo podría no quedar aplicado dentro del tránsito. Si la desigualdad no se cumple, la estación no es viable con esa configuración.

## Presupuesto inicial (hipótesis)

| Tramo | Hipótesis | Medición |
| --- | --- | --- |
| Flanco a notificación OPC | 10 a 30 ms | `plc_to_opc_receive_ms` (requiere relojes sincronizados) |
| Notificación a job `READY` | 2 a 11 ms | `opc_receive_to_job_ready_ms` |
| Notificación a inicio de escritura | 4 a 21 ms | `opc_receive_to_send_start_ms` |
| Inicio de escritura a escritura completa | 1 a 5 ms | `send_start_to_device_accept_ms` |
| Impresión y aplicación | Cientos de ms o más; su p99 es `DeviceLeadTimeMs` | `send_start_to_applied_ms`, `send_to_printed_ms`, `printed_to_applied_ms` |
| Verificación | Depende del escáner | `applied_to_verified_ms` |

El muestreo de OPC UA (tiempo hasta que el servidor detecta el cambio) se distingue del tiempo posterior a la recepción: el primero lo fija el servidor y su configuración; el segundo es responsabilidad del EdgeHost.

## Tasa y cola

Definir con OT la tasa de llegada (cajas por segundo), la tasa de servicio de impresora y aplicador, los picos y los buffers físicos. Si la llegada supera al servicio de forma sostenida, ningún software lo corrige: se requiere buffer mecánico o regulación de la línea. Se vigilan la edad del job `READY` más antiguo y la ocupación del canal por impresora.

## Reporte de benchmark

Declarar servidor, CPU, RAM, red, impresora o simulador, intervalos OPC negociados, número de cajas, carga por segundo, percentiles p50, p95, p99 y máximo, errores, backlog y escenarios excluidos. Con simuladores, declarar que no valida tiempos físicos.

## Ciberseguridad OT

- Zona OT segmentada; rutas permitidas solo hacia el servidor OPC, SQL Server, RabbitMQ e impresoras.
- OPC UA con SignAndEncrypt y validación estricta de certificados; sin autoaceptación en producción.
- Credenciales desde un almacén de secretos; certificados rotables; roles mínimos en SQL; RabbitMQ con TLS y permisos por vhost.
- La Operations API autentica a cada persona y registra cada acción; ningún endpoint envía bytes arbitrarios a una impresora.
- En alta disponibilidad, el aislamiento de red hacia la impresora es parte del mecanismo de exclusión (ADR 0010).
- El control y la seguridad funcional de la línea permanecen en el PLC; el EdgeHost solo baja `LabelingReady`.
