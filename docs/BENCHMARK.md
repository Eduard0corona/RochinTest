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
| Red | Local (loopback): PLC simulado, impresora simulada y SQL Server en la misma máquina |
| Payload de la etiqueta | 300 bytes (plantilla del simulador) |
| Composición | La del EdgeHost: ingesta OPC UA real, señal en proceso hacia el sender (sondeo de respaldo de 50 ms), una transacción por caja, socket persistente con la impresora |
| Evidencia | Impresora (PRINTED), aplicador (APPLIED) y escáner (VERIFIED) simulados; nivel mínimo VERIFIED |
| Comando | `dotnet test --filter "Category=Load"` |
| Fecha | 2026-10-15 13:46 UTC (40 min 18 s) |

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
pendientes, sino que la cola no se acumula de una muestra a la siguiente. Lo confirman el máximo de ese tramo, la holgura del plazo
y que todas las cajas terminaron `CONFIRMED`, ninguna vencida.

## Tramos · tasa pactada (hipótesis)

| Métrica | Tramo | Reloj | n | p50 (ms) | p95 (ms) | p99 (ms) | máx. (ms) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `plc_to_opc_receive_ms` | Flanco del PLC → notificación OPC recibida | Relojes del PLC y del EdgeHost (la misma máquina en el simulador) | 1800 | 62 | 110 | 110 | 138 |
| `opc_receive_to_job_ready_ms` | Notificación → commit de TX-A (job READY) | Monotónico | 1800 | 12.3 | 16.7 | 24.8 | 209.3 |
| `opc_receive_to_send_start_ms` | Notificación → invocación de la escritura | Monotónico | 1800 | 20.6 | 26.8 | 59.9 | 267.3 |
| `send_start_to_device_accept_ms` | Invocación de la escritura → acuse del dispositivo | Monotónico | 1800 | 0.2 | 0.2 | 0.2 | 0.5 |
| `presend_check_to_write_ms` | Segunda comprobación → invocación de la escritura | Monotónico | 1800 | 0 | 0 | 0 | 0 |
| `print_deadline_slack_ms` | Holgura: invocación de la escritura → plazo efectivo | Monotónico | 1800 | 965.1 | 1012.5 | 1013.9 | 1028.7 |
| `send_to_printed_ms` | Escritura → evidencia PRINTED | Instantes de la evidencia (simulador) | 1800 | 1 | 1 | 1 | 2 |
| `printed_to_applied_ms` | PRINTED → APPLIED | Instantes de la evidencia (simulador) | 1800 | 13 | 15 | 43 | 156 |
| `applied_to_verified_ms` | APPLIED → VERIFIED | Instantes de la evidencia (simulador) | 1800 | 18 | 24 | 32 | 158 |
| `send_start_to_applied_ms` | Invocación de la escritura → APPLIED (su p99 es DeviceLeadTimeMs) | Instantes de la evidencia (simulador) | 1800 | 14 | 16 | 44 | 157 |
| `send_to_verified_ms` | Escritura → VERIFIED | Instantes de la evidencia (simulador) | 1800 | 32 | 42 | 83 | 177 |

## Tramos · doble de la tasa

| Métrica | Tramo | Reloj | n | p50 (ms) | p95 (ms) | p99 (ms) | máx. (ms) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `plc_to_opc_receive_ms` | Flanco del PLC → notificación OPC recibida | Relojes del PLC y del EdgeHost (la misma máquina en el simulador) | 1200 | 62 | 109 | 110 | 112 |
| `opc_receive_to_job_ready_ms` | Notificación → commit de TX-A (job READY) | Monotónico | 1200 | 12.3 | 17.3 | 23.8 | 122.5 |
| `opc_receive_to_send_start_ms` | Notificación → invocación de la escritura | Monotónico | 1200 | 20.5 | 26.5 | 61.1 | 145.4 |
| `send_start_to_device_accept_ms` | Invocación de la escritura → acuse del dispositivo | Monotónico | 1200 | 0.2 | 0.2 | 0.2 | 1.4 |
| `presend_check_to_write_ms` | Segunda comprobación → invocación de la escritura | Monotónico | 1200 | 0 | 0 | 0 | 0 |
| `print_deadline_slack_ms` | Holgura: invocación de la escritura → plazo efectivo | Monotónico | 1200 | 965.2 | 1012.4 | 1013.8 | 1028.4 |
| `send_to_printed_ms` | Escritura → evidencia PRINTED | Instantes de la evidencia (simulador) | 1200 | 1 | 1 | 1 | 1 |
| `printed_to_applied_ms` | PRINTED → APPLIED | Instantes de la evidencia (simulador) | 1200 | 14 | 16 | 30 | 140 |
| `applied_to_verified_ms` | APPLIED → VERIFIED | Instantes de la evidencia (simulador) | 1200 | 20 | 28 | 33 | 136 |
| `send_start_to_applied_ms` | Invocación de la escritura → APPLIED (su p99 es DeviceLeadTimeMs) | Instantes de la evidencia (simulador) | 1200 | 14 | 16 | 31 | 140 |
| `send_to_verified_ms` | Escritura → VERIFIED | Instantes de la evidencia (simulador) | 1200 | 34 | 44 | 56 | 173 |

## Desigualdad de factibilidad

Referencia del plazo: `PLC_TIMESTAMP` (sección 17 del documento principal), con los p99 de toda la corrida sin el calentamiento:

> p99(`plc_to_opc_receive_ms`) + p99(`opc_receive_to_send_start_ms`) + `SendMarginMs` + `ClockSkewToleranceMs` + `DeviceLeadTimeMs` < `TransitBudgetMs`

| Término | Valor (ms) |
| --- | --- |
| p99(`plc_to_opc_receive_ms`) | 110 |
| p99(`opc_receive_to_send_start_ms`) | 60 |
| `SendMarginMs` (revisión) | 250 |
| `ClockSkewToleranceMs` (revisión) | 50 |
| `DeviceLeadTimeMs`: p99 medido de `send_start_to_applied_ms` | 37 |
| **Lado izquierdo** | **507** |
| `TransitBudgetMs` (revisión) | 1500 |
| **¿Se cumple?** | **Sí** |

Condiciones relacionadas:

- `SendMarginMs` (250) ≥ p99(`presend_check_to_write_ms`) (0) + `ClockJumpToleranceMs` (200): **se cumple**.
- `DeviceLeadTimeMs` configurado (400) ≥ p99 medido (37): **se cumple**.

Los valores de la revisión son de laboratorio (OT-03 abierta). En planta, `TransitBudgetMs` y `DeviceLeadTimeMs` salen de la línea
real, y la desigualdad se evalúa con los p99 medidos allí.

