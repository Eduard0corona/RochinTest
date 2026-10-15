# Benchmark de T12 · carga sostenida y capacidad

> **Hipótesis declarada, no aceptación en planta.** La tasa y el payload no están acordados con OT (OT-02, OT-03). Se usó la
> hipótesis que decidió la persona: una caja por segundo con el payload del simulador, 30 minutos a esa tasa y 10 minutos al
> doble. Impresión, aplicación y lectura son del simulador: **este reporte no valida tiempos físicos.** Se repite en planta con
> el hardware, el PLC y la impresora reales (`docs/FAT_CHECKLIST.md`, G11 y G12).

## Entorno

| Elemento | Valor |
| --- | --- |
| Máquina | EDUARDO (Microsoft Windows 10.0.26200) |
| CPU | AMD64 Family 25 Model 33 Stepping 2, AuthenticAMD, 12 procesadores lógicos |
| Memoria disponible para .NET | 23.9 GiB |
| Runtime | .NET 10.0.13 |
| SQL Server | 16.0.4295.3 Developer Edition (64-bit), en un contenedor de Testcontainers en la misma máquina |
| RabbitMQ | RabbitMQ 4.3.5, en un contenedor de Testcontainers en la misma máquina |
| Red | Local (loopback): PLC simulado, impresora simulada, SQL Server y RabbitMQ en la misma máquina |
| Payload de la etiqueta | 300 bytes (plantilla del simulador) |
| Composición | La del EdgeHost, en un proceso de prueba: ingesta OPC UA real (SignAndEncrypt (Basic256Sha256)); señal en proceso hacia el sender (sondeo de respaldo de 50 ms); socket persistente con la impresora; barrido TX-S cada 200 ms; Outbox Relay con publisher confirms hacia una cola quorum sin consumidor; diagnóstico: las esperas de SQL Server se muestrean cada 200 ms desde el proceso de prueba, con el login de administración |
| Evidencia | Impresora (PRINTED), aplicador (APPLIED) y escáner (VERIFIED) simulados, aplicada por TX-D; nivel mínimo VERIFIED |
| Comando | `build/ci.ps1 --fase 7 --carga (paso 4b: dotnet test --no-build --filter "Category=Load")` |
| Fecha | 2026-10-15 23:47 UTC (40 min 16 s), compuerta de e0812b0 |

## Intervalos OPC UA negociados

| Intervalo | Pedido | Negociado por el servidor |
| --- | --- | --- |
| Muestreo | 10 ms | 10 ms |
| Publicación | 20 ms | 100 ms |
| Cola del monitored item | — | 10 |

El servidor es el PLC simulado: el PLC real negociará sus propios valores, y el primer tramo depende de ellos.

## Carga

Cajas en total: 3010, incluidas 10 de calentamiento fuera de la muestra (conexión con la impresora, código y planes de SQL en frío).

| Fase | Cajas por segundo | Minutos | Cajas | Backlog máximo | Backlog p95 | Backlog máximo en el último minuto | Pendiente más antiguo (máx.) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| tasa pactada (hipótesis) | 1 | 30 | 1800 | 0 | 0 | 0 | 0 ms |
| doble de la tasa | 2 | 10 | 1200 | 0 | 0 | 0 | 0 ms |

El backlog es la cantidad de jobs `READY`, `RETRY_WAIT` o `SENDING`, muestreada cada segundo. Un job pasa por esos estados en
decenas de milisegundos (tramo `opc_receive_to_send_start_ms`), así que una muestra en cero no significa que no hubiera jobs
pendientes, sino que la cola no se acumula de una muestra a la siguiente. Lo confirman el máximo de ese tramo y la holgura del plazo.
Todas las cajas terminaron `CONFIRMED`, ninguna vencida.

## Tramos · tasa pactada (hipótesis)

| Métrica | Tramo | Reloj | n | p50 (ms) | p95 (ms) | p99 (ms) | máx. (ms) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `plc_to_opc_receive_ms` | Flanco del PLC → notificación OPC recibida | Relojes del PLC y del EdgeHost (la misma máquina en el simulador) | 1800 | 63 | 110 | 115 | 121 |
| `opc_receive_to_job_ready_ms` | Notificación → commit de TX-A (job READY) | Monotónico | 1800 | 12.4 | 17.7 | 24.5 | 109.1 |
| `opc_receive_to_send_start_ms` | Notificación → invocación de la escritura | Monotónico | 1800 | 21.2 | 28 | 67.3 | 166.3 |
| `send_start_to_device_accept_ms` | Invocación de la escritura → acuse del dispositivo | Monotónico | 1800 | 0.2 | 0.2 | 0.2 | 1.4 |
| `presend_check_to_write_ms` | Segunda comprobación → invocación de la escritura | Monotónico | 1800 | 0 | 0 | 0 | 0 |
| `print_deadline_slack_ms` | Holgura: invocación de la escritura → plazo efectivo | Monotónico | 1800 | 963.8 | 1011.7 | 1014.5 | 1027.3 |
| `send_to_printed_ms` | Escritura → evidencia PRINTED | Instantes de la evidencia (simulador) | 1800 | 0.7 | 0.9 | 1 | 4.4 |
| `printed_to_applied_ms` | PRINTED → APPLIED | Instantes de la evidencia (simulador) | 1800 | 14 | 16.6 | 36.5 | 124.5 |
| `applied_to_verified_ms` | APPLIED → VERIFIED | Instantes de la evidencia (simulador) | 1800 | 19.4 | 27.2 | 32.6 | 134.4 |
| `send_start_to_applied_ms` | Invocación de la escritura → APPLIED (su p99 es DeviceLeadTimeMs) | Instantes de la evidencia (simulador) | 1800 | 14.9 | 17.5 | 37.3 | 125.5 |
| `send_to_verified_ms` | Escritura → VERIFIED | Instantes de la evidencia (simulador) | 1800 | 34.3 | 44 | 78.8 | 149.2 |

## Tramos · doble de la tasa

| Métrica | Tramo | Reloj | n | p50 (ms) | p95 (ms) | p99 (ms) | máx. (ms) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `plc_to_opc_receive_ms` | Flanco del PLC → notificación OPC recibida | Relojes del PLC y del EdgeHost (la misma máquina en el simulador) | 1200 | 63 | 110 | 114 | 119 |
| `opc_receive_to_job_ready_ms` | Notificación → commit de TX-A (job READY) | Monotónico | 1200 | 12.2 | 19.5 | 25.9 | 140.7 |
| `opc_receive_to_send_start_ms` | Notificación → invocación de la escritura | Monotónico | 1200 | 20.9 | 29.9 | 70.5 | 161.7 |
| `send_start_to_device_accept_ms` | Invocación de la escritura → acuse del dispositivo | Monotónico | 1200 | 0.2 | 0.2 | 0.2 | 1.2 |
| `presend_check_to_write_ms` | Segunda comprobación → invocación de la escritura | Monotónico | 1200 | 0 | 0 | 0 | 0 |
| `print_deadline_slack_ms` | Holgura: invocación de la escritura → plazo efectivo | Monotónico | 1200 | 962.8 | 1011.8 | 1013.4 | 1025.2 |
| `send_to_printed_ms` | Escritura → evidencia PRINTED | Instantes de la evidencia (simulador) | 1200 | 0.7 | 0.9 | 1.1 | 3.9 |
| `printed_to_applied_ms` | PRINTED → APPLIED | Instantes de la evidencia (simulador) | 1200 | 14 | 16.3 | 31.7 | 119.3 |
| `applied_to_verified_ms` | APPLIED → VERIFIED | Instantes de la evidencia (simulador) | 1200 | 20.9 | 30.1 | 34.1 | 134 |
| `send_start_to_applied_ms` | Invocación de la escritura → APPLIED (su p99 es DeviceLeadTimeMs) | Instantes de la evidencia (simulador) | 1200 | 14.9 | 17.4 | 32.6 | 120.2 |
| `send_to_verified_ms` | Escritura → VERIFIED | Instantes de la evidencia (simulador) | 1200 | 35.7 | 46.4 | 79.3 | 150.8 |

## Desigualdad de factibilidad

Referencia del plazo: `PLC_TIMESTAMP` (sección 17 del documento principal), con los p99 de toda la corrida sin el calentamiento:

> p99(`plc_to_opc_receive_ms`) + p99(`opc_receive_to_send_start_ms`) + `SendMarginMs` + `ClockSkewToleranceMs` + `DeviceLeadTimeMs` < `TransitBudgetMs`

| Término | Valor (ms) |
| --- | --- |
| p99(`plc_to_opc_receive_ms`) | 115 |
| p99(`opc_receive_to_send_start_ms`) | 68 |
| `SendMarginMs` (revisión) | 250 |
| `ClockSkewToleranceMs` (revisión) | 50 |
| `DeviceLeadTimeMs`: p99 medido de `send_start_to_applied_ms` | 33.1 |
| **Lado izquierdo** | **516.1** |
| `TransitBudgetMs` (revisión) | 1500 |
| **¿Se cumple?** | **Sí** |

Condiciones relacionadas:

- `SendMarginMs` (250) ≥ p99(`presend_check_to_write_ms`) (0) + `ClockJumpToleranceMs` (200): **se cumple**.
- `DeviceLeadTimeMs` configurado (400) ≥ p99 medido (33.1): **se cumple**.

Los valores de la revisión son de laboratorio (OT-03 abierta). En planta, `TransitBudgetMs` y `DeviceLeadTimeMs` salen de la línea
real, y la desigualdad se evalúa con los p99 medidos allí.

## Errores y escenarios excluidos

| Error | Cantidad |
| --- | --- |
| Jobs por estado final | CONFIRMED 3010 |
| Detecciones por estado | JOB_CREATED 3010 |
| Registros OPC rechazados (`opc_records_rejected_total`; el valor inicial vacío del registro es `EMPTY`) | 1 {cause=EMPTY,transport=READ} |
| Notificaciones perdidas por canal lleno | 0 |
| Rechazos de la segunda comprobación | 0 |
| Intentos NOT_SENT | 0 |
| Intentos UNKNOWN | 0 |
| Eventos del Outbox: escritos, publicados, pendientes al final | 6020, 6020, 0 |
| Servicios de fondo que fallaron (sender, barrido) | ninguna |
| Pausas del sender (tres fallos seguidos del dispositivo) | 0 |
| Reintentos de TX-A con SQL Server sin responder | 0 |
| Caídas de SQL Server declaradas | 0 |
| Transacciones reintentadas por un 1205 | 0 |
| Errores del sender fuera de la escritura; intentos SENDING cerrados por TX-R0 | 0; 0 |
| Ciclos publicados sin detección | 0 |
| Cajas más lentas (notificación a escritura) | ciclo 1: 587 ms a los -10 s; ciclo 501: 166 ms a los 490 s; ciclo 502: 161 ms a los 491 s; ciclo 2083: 161 ms a los 1936 s; ciclo 2090: 149 ms a los 1940 s |

Escenarios excluidos de esta medición:

- **Fallas.** Sin caídas de SQL Server, RabbitMQ, la impresora o el PLC: esas son T08, T19, T05 a T07 y T11, que no miden capacidad.
- **ERP.** El snapshot de la orden está en caché durante toda la corrida: la sincronización con el ERP no participa y ningún job espera snapshot.
- **Operación.** Sin acciones de la Operations API ni reimpresiones; una sola estación y una sola impresora de línea.
- **Propiedad.** El lease de la impresora se renueva una vez por minuto desde la prueba, no con el ciclo de propiedad del EdgeHost.
- **Consumidor.** La cola recibe los eventos pero nadie los consume: el costo del consumidor (MES) queda fuera.
- **Hardware.** Impresión, aplicación y lectura son del simulador, en la misma máquina, sin tiempos físicos.

