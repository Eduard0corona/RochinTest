# Plan de implementación por fases (v1.1)

Incrementos verticales demostrables, sin fechas artificiales. Cada fase termina con un PR revisado por una persona, `tools/verify_package.py` en verde y las pruebas indicadas ejecutadas con evidencia. Una IA propone; una persona aprueba cualquier interacción con sistemas físicos.

| Fase | Entrega | Pruebas que deben pasar |
| --- | --- | --- |
| 0. Fundación | Solución .NET 10 con las bibliotecas de `docs/ARQUITECTURA_TECNICA.md`, `EdgeHost` vacío, Docker Compose con SQL Server y RabbitMQ, tipos de contratos validados contra los schemas, OpenTelemetry base, pipeline de build y pruebas | T23, T28 en CI |
| 1. Datos e idempotencia lógica | DDL v1.1 y semilla de transiciones ejecutados por primera vez en SQL Server real; TX-A con inserción primero y manejo de errores por nombre de restricción; huella canónica; Outbox e Inbox | T01, T02, T10, T14, T47 |
| 2. Ingesta OPC simulada | Simulador OPC con registro latched, `PlcEpoch`, bit de overflow, Republish y opcionalmente FIFO; clasificación de secuencia; épocas; huecos | T11, T13, T24, T25, T26, T30, T35, T45 |
| 3. ERP y plazo | Caché ERP, `WAITING_ERP`, plazo efectivo con `DeviceLeadTimeMs`, detección de saltos de reloj, barrido por estación, configuración versionada | T03, T04, T17, T18, T21, T33, T37, T46 |
| 4. Print Agent | Simulador de impresora con cola, fallos y capacidades declaradas; sender por impresora; claim con `PrinterSendOrder`; segunda comprobación; `TRANSMITTED`; `EvidenceObservation`, evidencia y serial; corrección J18 | T05, T06, T07, T15, T22, T29, T31, T38, T39, T42, T44 |
| 5. Propiedad, recuperación y operación | Lock de sistema operativo, lease, arranque ordenado, Operations API con `OperatorAction`, reconciliación, reimpresión excepcional ejecutada por el sender de la impresora destino | T27, T34, T36, T40, T41, T43 |
| 6. Perfil extendido (si OT-14 lo exige) | FIFO, `AckCycleId`, journal SQLite con pruebas de corte de energía | T09, T20 |
| 7. Hardening y demo | Inyección de fallas completa, carga, seguridad, reporte de benchmark, revisión OT | T08, T12, T16, T19, T32 y aceptación en planta |

Orden de PR sugerido: `feat/foundation` → `feat/sql-idempotency` → `feat/opc-ingest` → `feat/erp-deadline` → `feat/print-agent` → `feat/ownership-recovery` → `feat/extended-profile` → `test/hardening`.

## Revisión en cada PR

- Invariantes intactos y máquina de estados sin cambios no documentados.
- Comandos de prueba y salidas reales adjuntos; estados del catálogo actualizados solo con evidencia.
- Cambios de esquema versionados y semilla regenerada.
- Métricas nuevas registradas.
- Lista de lo no verificado y de los riesgos.
