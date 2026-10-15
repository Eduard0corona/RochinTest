# Lista de aceptación en planta (FAT)

El MVP se probó contra simuladores: PLC por OPC UA, impresora, aplicador, escáner y ERP. Usó SQL Server y RabbitMQ reales en contenedores. `docs/TEST_STATUS.md` dice qué pruebas pasaron y en qué entorno.

Lo de esta lista no se puede demostrar sin la línea real: las garantías físicas y de rendimiento de `spec/docs/MATRIZ_GARANTIAS.md` y las decisiones de OT abiertas (`spec/docs/DECISIONES_ABIERTAS_OT.md`). Ninguna fila se da por aceptada hasta que la planta registre la prueba y su evidencia.

## 0. Prerrequisitos que el MVP no implementa

Sin ellos, la planta no puede aceptar las garantías de la sección 1:

| Prerrequisito | Estado en el MVP | Sin él | Afecta a |
| --- | --- | --- | --- |
| Integración de evidencia en el EdgeHost: impresora, aplicador y escáner reales informando por TX-D | **No implementada.** `EvidenceService` (TX-D) existe y está probado, pero el EdgeHost no tiene una entrada de evidencia; en la demo y en las pruebas la aplica un simulador (`docs/DEMO.md`) | Con `MinConfirmationLevel` ≥ PRINTED, todo job termina en `UNKNOWN` al vencer su ventana de evidencia | G06, OT-07, OT-08, OT-15 |
| Compuerta física (`PhysicalGateMode`, `PrintWindowCycleId`) | **No implementada** (Fase 4, decisión 3) | La etiqueta puede salir sin la caja en posición | G05, OT-01, OT-09 |

## 1. Garantías que solo acepta la planta

| Garantía | Qué se acepta en planta | Cómo | Depende de | Pruebas con simulador |
| --- | --- | --- | --- | --- |
| G05 · Ninguna etiqueta fuera de la ventana física de su caja | La etiqueta solo se aplica con la caja correcta en posición | Compuerta del PLC o del aplicador (`PhysicalGateMode`, `PrintWindowCycleId`), probada con cajas reales, retenidas y detenidas. La compuerta no tiene implementación en el MVP (Fase 4, decisión 3) | OT-01, OT-02, OT-09 | Ninguna: no hay compuerta física simulada |
| G06 · La etiqueta correcta en la caja correcta | Un escáner posterior a la aplicación lee `LabelSerial` y su lectura se correlaciona con el ciclo | Nivel VERIFIED con el escáner y el tracking reales, conectados por la integración de evidencia (sección 0). Una etiqueta ajena se lee como `SERIAL_MISMATCH` (J11 y J18) | OT-08, OT-01 | T15, T22, T44 (lecturas simuladas) |
| G07 · Una etiqueta por caja física, aun recirculada | Una caja que vuelve a pasar no recibe otra etiqueta | `CARTON_ID_REQUIRED` con un identificador físico leído antes de etiquetar | OT-05, OT-06 | T16 y T32 (identidad simulada; sin identidad, la garantía es por ciclo) |
| G10 · Un solo transmisor por impresora | Ningún segundo proceso o nodo transmite a la impresora | Un EdgeHost por estación con lock de sistema operativo y lease. Con alta disponibilidad, el aislamiento físico del canal (red o proxy que valide tokens) | OT-10, OT-11 | T27 y T36 (dos procesos en una máquina; aislamiento simulado con un proxy TCP) |
| G11 · Latencia compatible con el plazo | La desigualdad de factibilidad con los tiempos de la línea | `docs/BENCHMARK.md` repetido en el hardware de planta: intervalos OPC negociados con el PLC real, `DeviceLeadTimeMs` medido con el aplicador real (p99 de `send_start_to_applied_ms`) | OT-03 | T12 (hipótesis de una caja por segundo, simuladores) |
| G12 · Capacidad sostenida | La tasa de llegada es menor que la capacidad de impresión y aplicación | Takt time y capacidad medidos; backlog estable a la tasa pactada | OT-02 | T12 |
| G04 · Plazo con el reloj del PLC | Con `DeadlineReference = PLC_TIMESTAMP`, los relojes del PLC y del EdgeHost sincronizados | NTP o PTP validado entre el PLC y el EdgeHost, antes del benchmark de G11: el primer tramo de la desigualdad y el plazo solo valen con relojes sincronizados. Un PLC atrasado no se detecta (SI-15) | OT-03 | T12 y la demo con el PLC simulado y el mismo reloj |
| G16 · Orden de aplicación | El orden de envío es el orden de aplicación | El aplicador real aplica en el orden en que recibe | OT-01 | T30 y T42 (orden de envío; la aplicación es del simulador) |
| G09 · Historia completa durante una caída de SQL | Solo si OT lo exige | Perfil extendido (FIFO del PLC, `AckCycleId`, journal) | OT-14 | T09 y T20: NO_APLICA (perfil mínimo) |

## 2. Decisiones de OT abiertas

Cada una tiene un comportamiento por defecto en el MVP. La planta debe confirmarlo o elegir otro, y lo registra en la revisión de configuración de la estación.

| ID | Pregunta | Por defecto en el MVP | Qué se fija o se prueba al decidir |
| --- | --- | --- | --- |
| OT-01 | ¿Impresora independiente o print-and-apply? | Flujo lógico completo con el simulador | Nivel de evidencia posible (APPLIED) y la compuerta (G05) |
| OT-02 | ¿Las cajas avanzan siempre o pueden detenerse? | Plazo fijo por revisión | Extensión del plazo con una señal del PLC; tasa de llegada (G12) |
| OT-03 | Tiempos sensor → aplicación, envío → etiqueta aplicada, reloj confiable | Valores de laboratorio (`TransitBudgetMs` 1500, `DeviceLeadTimeMs` 400, `SendMarginMs` 250 en T12) | `TransitBudgetMs`, `DeviceLeadTimeMs`, `SendMarginMs` ≥ p99(`presend_check_to_write_ms`) + `ClockJumpToleranceMs`, y `DeadlineReference` |
| OT-04 | ¿PLC con `PlcEpoch` persistente y `DetectionTimestamp`? | Época del registro del PLC; eco de cierre del simulador (SI-10) | Detección de reinicios y rebotes (G01); SI-10, SI-12 y SI-13 |
| OT-05 | ¿Identificador físico de caja? | `CYCLE_ONLY` | `CARTON_ID_REQUIRED` (G07) |
| OT-06 | ¿Recirculación de cajas? | La limitación queda en la matriz | Identidad física o tracking (G07) |
| OT-07 | ¿La impresora informa finalización o cancelación por trabajo? | Capacidades del simulador | Nivel PRINTED, J12 (cancelación confirmada) y la reconciliación negativa (T31, T40) |
| OT-08 | ¿Escáner de verificación correlacionado con el ciclo? | Nivel configurable; el simulador emite lecturas | VERIFIED y G06 |
| OT-09 | ¿El PLC puede retener, detener o rechazar cajas? | El EdgeHost baja `LabelingReady`; la reacción física es del PLC | Qué hace la línea ante UNKNOWN, SQL caído o estación bloqueada |
| OT-10 | ¿Más de un agente activo? | Un solo EdgeHost por estación | Aislamiento físico del canal (G10) |
| OT-11 | ¿Alta disponibilidad del Print Agent? | Conmutación manual | Aislamiento de red, apagado verificado o proxy que valide tokens |
| OT-12 | ¿Qué se hace ante una impresión incierta? | Estados, autorizaciones y auditoría modelados | Retención de cajas UNKNOWN y quién autoriza reimpresiones (procedimiento de G13) |
| OT-13 | ¿Producir con SQL Server caído? | Perfil mínimo: falla cerrada (T19) | Etiquetado manual o perfil extendido |
| OT-14 | ¿Conservar la historia de todos los ciclos en una caída? | Perfil mínimo con `SequenceGap` | Perfil extendido (G09; T09 y T20) |
| OT-15 | ¿Qué nivel de evidencia confirma? | Parámetro de la revisión (`MinConfirmationLevel`) | NONE, PRINTED, APPLIED o VERIFIED por estación |
| OT-16 | ¿Impresora fuera de línea para reimpresiones? | `ManualPrinterId` de la revisión | Impresora de reimpresión o reimpresión en la de línea con la estación detenida |

## 3. Pruebas que en el MVP solo midieron el simulador

Vuelven a hacerse con el equipo real, porque su criterio físico se comprobó en el simulador (`docs/TEST_STATUS.md`, columna Notas):

- **T05 y T06:** conexión rechazada y respuesta perdida con la impresora real; una sola etiqueta física.
- **T07:** un corte de energía real durante el envío (`Process.Kill` no prueba el disco ni el dispositivo).
- **T22 y T44:** la alerta inmediata y el incidente físico ante un serial ajeno.
- **T29:** una pausa real del proceso entre el claim y la escritura. Una pausa entre la comprobación y la llamada de escritura no se puede excluir por software.
- **T31:** cola interna y cancelación confirmada por la impresora real (OT-07).
- **T36:** el aislamiento real del canal de la impresora al cambiar de dueño.
- **T42:** orden de envío con la impresora real fuera de línea y de vuelta.
- **T08 y T19:** caída real de RabbitMQ y de SQL Server en la red de planta. T08 simula la caída del broker con un proxy TCP. T19 la simula con un proxy (cortes y congelamiento) y además con el contenedor de SQL Server detenido y vuelto a arrancar, en la misma máquina. En T19, además, lo que hace la línea mientras `LabelingReady` está en falso (OT-09).
- **T05 con la impresora real:** tres fallos seguidos ponen el sender en pausa con `LabelingReady` en falso, y vuelve solo cuando la impresora acepta conexiones.
- **SQL Server caído y reinicio del PLC a la vez:** si el ciclo siguiente a la caída llega con una época nueva, los ciclos descartados durante la caída no quedan como hueco de la época anterior. Solo los cuenta `opc_records_rejected_total{cause=SQL_UNAVAILABLE}`, con su alerta.

## 4. Configuración y seguridad que se verifican al instalar

- **OPC UA.** SignAndEncrypt con validación estricta de certificados: el certificado del PLC en el almacén de confianza del EdgeHost, sin autoaceptación (valores por defecto de `OpcIngestOptions`; `OpcSecurityTests` con el PLC simulado). También se registran los intervalos de muestreo y publicación que el PLC real negocia.
- **RabbitMQ.** `amqps://` con el certificado del broker válido para su nombre y, si la planta lo exige, certificado de cliente (`RabbitMq__Tls__*`). La comprobación de revocación del certificado está activa por defecto. Si la red de planta no llega a la lista de revocación, OT la desactiva de forma explícita (`RabbitMq__Tls__CheckCertificateRevocation=false`) y lo registra aquí. Permisos por vhost.
- **Secretos.** Cadenas de conexión, contraseñas de los logins, claves de las personas de la Operations API (solo su SHA-256 en la configuración) y certificados: desde el almacén de secretos o las variables de entorno del servicio, nunca en el repositorio. `build/check_secrets.py`, en la compuerta, busca valores literales en claves que nombran una contraseña, una frase de paso o un secreto, en todos los archivos de texto salvo `spec/` y `tests/`. Es una revisión por patrones, no una herramienta de detección de secretos.
- **SQL Server.** El login de la aplicación sin DDL ni escritura sobre `AllowedTransition`. Los triggers habilitados.
- **Supervisión del servicio.**
  - Con el código 70 (el sender se detuvo, por ejemplo sin TX-C posible): reiniciar el EdgeHost automáticamente, para que el arranque ejecute TX-R0.
  - Con el código 71 (la ingesta se detuvo por un error de SQL no clasificado como caída, es decir, un defecto): alertar y revisar antes de reiniciar, o reiniciar con un límite de intentos. Cada arranque deja como hueco los ciclos que estaban en el canal.
  - Ninguna prueba automática ejercita los códigos de salida.
- **SQL Server y la falla cerrada.** El EdgeHost conecta sin el periodo de bloqueo del pool (`PoolBlockingPeriod=NeverBlock`), para ver volver a SQL Server enseguida. Un login fallido (18456) cuenta como SQL no disponible: con una contraseña mal configurada, la estación queda en falla cerrada con el error en el log, en lugar de detenerse. Se verifica en planta que el servidor real vuelve después de una detención (T19 con un contenedor detenido y vuelto a arrancar es la prueba con simulador).

## 5. Defectos de la especificación que dependen de OT

En `docs/SPEC_ISSUES.md`: SI-10 y SI-12 (OT-04), SI-13 (registro incoherente frente a la secuencia) y SI-15 (detección de `CLOCK_SKEW`). Siguen abiertos con su comportamiento interino de falla cerrada, decidido por la persona.

## 6. Aceptación

| Punto | Responsable de planta | Fecha | Evidencia | Resultado |
| --- | --- | --- | --- | --- |
| G05 | | | | |
| Sección 0: integración de evidencia y compuerta física | | | | |
| G06 | | | | |
| G07 | | | | |
| G10 | | | | |
| G11 y G12 (benchmark en planta) | | | | |
| OT-01 a OT-16 decididas y registradas en la revisión | | | | |
| Pruebas de la sección 3 con el equipo real | | | | |
| Configuración y seguridad de la sección 4 | | | | |
