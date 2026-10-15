# PROMPT PARA CLAUDE — Revisión arquitectónica y consolidación v1.1

## ROL

Actúa como un **Principal Software Architect especializado en .NET, sistemas industriales OT, OPC UA, integración PLC, sistemas distribuidos, impresión industrial, concurrencia, idempotencia y arquitecturas de baja latencia**.

Vas a realizar una revisión arquitectónica profunda y una corrección integral del paquete:

`CartonPrinting_AI_Implementation`

Versión actual: `1.0`

Versión objetivo: `1.1`

Este proyecto corresponde a una evaluación técnica donde se debe demostrar capacidad para diseñar soluciones industriales confiables y utilizar inteligencia artificial de manera rigurosa durante el desarrollo de software.

Tu responsabilidad no es solamente actualizar documentos. Debes garantizar que el conjunto completo de especificaciones sea técnicamente coherente, implementable y verificable.

**Importante: no implementes código de producción ni inicies la Fase 0. Esta actividad corresponde exclusivamente a la consolidación arquitectónica y documental.**

---

# 1. CONTEXTO DEL PROBLEMA

El caso práctico requiere diseñar un flujo mínimo donde:

1. Un PLC expone tags a través de OPC UA.
2. Se detecta una caja y se genera un evento `CartonDetected`.
3. Se resuelven los datos de producto y lote desde información proveniente del ERP.
4. Se crea un `PrintJob`.
5. Un agente envía el trabajo a una impresora.
6. Se verifica y publica el resultado.
7. El sistema garantiza idempotencia lógica y evita reimpresiones físicas inseguras.

Los escenarios obligatorios son:

- Tag OPC duplicado.
- ERP lento o caído.
- Impresora sin respuesta.
- Reinicio del servicio a mitad del proceso.
- Estado de impresión incierto.
- Caída de RabbitMQ.
- Caída de SQL Server.
- Reconexión OPC UA.
- Colisiones o reinicios del contador del PLC.
- Impresión fuera de la ventana física de una caja.
- Concurrencia entre agentes.
- Reimpresiones manuales autorizadas.

Se requiere comunicación de baja latencia, idealmente en el orden de los milisegundos, sin inventar garantías de rendimiento que no hayan sido medidas.

---

# 2. INSTRUCCIONES FUNDAMENTALES

## 2.1 Pensamiento crítico

Existe una revisión técnica previa con siete hallazgos, H1 a H7.

También existe una segunda revisión arquitectónica que valida algunos hallazgos, cuestiona determinadas soluciones y detecta problemas adicionales.

**No consideres ninguna de las dos revisiones como autoridad absoluta.**

Para cada decisión:

1. Identifica el problema original.
2. Verifica si realmente existe.
3. Evalúa la solución propuesta.
4. Identifica efectos secundarios o nuevas condiciones de carrera.
5. Selecciona la solución técnicamente más sólida.
6. Documenta alternativas rechazadas.
7. Define cómo se comprobará la garantía mediante pruebas.

Si encuentras que alguna recomendación es incorrecta, no la implementes solamente porque aparece en la revisión.

Documenta el motivo del rechazo.

## 2.2 Alcance

Puedes modificar:

- README y documentación técnica.
- AI_CONTEXT y guardrails.
- ADR.
- DDL de SQL Server y SQLite, cuando corresponda.
- Contratos JSON y JSON Schema.
- Máquina de estados.
- Diagramas Mermaid.
- Ejemplos de eventos.
- Matriz de pruebas.
- Runbooks.
- Scripts de validación documental.
- Prompt de implementación para agentes IA.
- DOCX y PDF finales.
- CHANGELOG y checksums.

No debes:

- Implementar servicios .NET.
- Crear lógica productiva de comunicación con PLC.
- Desarrollar el Print Agent real.
- Comenzar la Fase 0.
- Afirmar garantías físicas que dependan de hardware no especificado.
- Dar por ejecutadas pruebas que no ejecutaste.
- Inventar características del PLC o impresora.

## 2.3 Principio rector

**Una transacción SQL correcta no necesariamente implica una operación física correcta.**

El diseño debe distinguir explícitamente:

- Unicidad de eventos.
- Unicidad de trabajos.
- Unicidad de transmisiones autorizadas.
- Impresión física.
- Aplicación física sobre una caja.
- Verificación de que la etiqueta correcta corresponde a la caja correcta.

---

# 3. AUDITORÍA DE LA DOCUMENTACIÓN EXISTENTE

Lee el paquete completo antes de modificarlo.

Orden sugerido:

1. `README.md`
2. `AI_CONTEXT.md`
3. `docs/GUARDRAILS.md`
4. `DOCUMENTO_PRINCIPAL.md`
5. `docs/ARQUITECTURA_TECNICA.md`
6. `database/`
7. `contracts/`
8. `diagrams/`
9. `docs/adr/`
10. `tests/MATRIZ_PRUEBAS.md`
11. Runbooks y documentación operacional.
12. Prompts existentes para Codex o Claude.

No asumas que el contenido de esos archivos es consistente.

Antes de editar, presenta un resumen de los problemas confirmados y un plan de archivos a modificar. Después continúa con las correcciones sin solicitar confirmaciones que no sean indispensables.

Conserva los invariantes válidos y registra cualquier cambio de comportamiento.

---

# 4. H1 — VENCIMIENTO DE IMPRESIÓN Y CORRELACIÓN FÍSICA

## Diagnóstico

El diseño original no impide que un PrintJob se ejecute después de que la caja correspondiente haya abandonado el punto de impresión.

Esto puede ocasionar que una etiqueta termine en una caja equivocada.

La revisión previa propuso:

- `StationConfig`.
- `TransitBudgetMs`.
- `SendMarginMs`.
- `DeadlineAt`.
- Estado `EXPIRED`.
- Claim condicional en SQL Server.

Aceptamos estos conceptos como base, pero la solución requiere ampliación.

## Correcciones requeridas

### 4.1 Configuración operacional

Define una configuración por estación que contemple, cuando aplique:

- `TransitBudgetMs`
- `SendMarginMs`
- `MinCycleIntervalMs`
- `MaxAutoAttempts`
- `MinConfirmationLevel`
- `LabelingEnabled`
- Identificación del PLC.
- Identificación de la impresora.
- Revisión de configuración.

Los valores concretos deben configurarse y validarse con OT.

No inventes tiempos físicos.

### 4.2 Vencimiento

Cada detección real debe conservar su deadline calculado con la referencia temporal acordada.

El PrintJob conserva el deadline efectivo y la revisión de configuración utilizada para crearlo.

Define comportamiento para:

- ERP que responde tarde.
- Reintentos con backoff.
- Replay después de caída de SQL.
- Reinicio del servicio.
- Jobs pendientes.
- Eventos antiguos.
- Cambios de configuración de estación.

Un trabajo vencido no debe enviarse automáticamente.

### 4.3 Condición de carrera después del claim

El claim de SQL debe validar el deadline, pero esto no es suficiente.

Analiza este escenario:

1. El agente reclama el job correctamente.
2. SQL confirma el claim.
3. El proceso se suspende.
4. El deadline vence.
5. El agente vuelve a ejecutarse.
6. Intenta transmitir.

Diseña una segunda comprobación inmediatamente antes del envío.

Explica sus límites frente a pausas del proceso y operaciones físicas no atómicas.

### 4.4 Autorización física

Evalúa qué capacidades OPC/PLC permitirían confirmar que la caja correcta sigue autorizada para recibir la etiqueta.

Considera diferentes opciones:

- Compuerta o ventana de autorización desde PLC.
- Identificación física mediante sensor o escáner.
- Confirmación de presencia en estación.
- Retención o rechazo de caja.
- Control de la cola de impresión y del aplicador.

No conviertas estas opciones en requisitos de hardware asumidos.

Documenta qué garantía es posible con cada alternativa.

### 4.5 Diferenciar tiempos

Define métricas separadas para:

- Cambio físico hasta recepción OPC.
- Recepción OPC hasta creación del PrintJob.
- Recepción OPC hasta inicio de transmisión.
- Inicio de transmisión hasta aceptación del dispositivo.
- Transmisión hasta impresión física.
- Impresión hasta aplicación y verificación.

No confundas escribir bytes con aplicar físicamente la etiqueta.

---

# 5. H2 — MÁQUINA DE ESTADOS CANÓNICA

## Problema

Los diagramas, DDL, contratos y runbooks actuales contienen transiciones inconsistentes.

Debes eliminar esa divergencia.

## Fuente única de verdad

Crea:

`contracts/state-machine.json`

Este documento define:

- Estados.
- Transiciones.
- Disparadores.
- Guard conditions.
- Acciones transaccionales.
- Carácter automático o manual.
- Estados terminales.
- Transiciones prohibidas.

A partir de él deben generarse o verificarse las representaciones de la máquina de estados en los diagramas y artefactos derivados.

No mantengas dos fuentes independientes de transiciones.

## Estados mínimos de PrintJob

Considera:

- `READY`
- `SENDING`
- `RETRY_WAIT`
- `CONFIRMED`
- `UNKNOWN`
- `FAILED`
- `EXPIRED`
- `CANCELLED`

No los adoptes automáticamente sin validar su semántica.

Cada estado debe tener una definición inequívoca.

## Semántica del envío

Registra durablemente `SENDING` antes de iniciar una operación potencialmente irreversible.

Un intento debe iniciar conservadoramente con `MayHaveSent = true`.

Solo puede marcarse `NOT_SENT` cuando exista evidencia confiable de que no se transmitieron datos capaces de provocar impresión.

Un reinicio durante `SENDING` se trata como incertidumbre hasta que pueda reconciliarse.

## Corrección importante: UNKNOWN y reimpresión manual

No permitas que `UNKNOWN → SENDING` sea una transición ordinaria.

`UNKNOWN` significa que el trabajo pudo haberse impreso.

Una autorización manual no demuestra por sí sola que no ocurrió la impresión.

Diseña por separado:

1. Reconciliación que confirma impresión.
2. Reconciliación que demuestra ausencia de impresión.
3. Cierre operacional sin resolver completamente la incertidumbre.
4. Reimpresión excepcional con autorización, evidencia y riesgo explícito.

Si una reimpresión excepcional puede generar una segunda etiqueta, no debe presentarse como una operación que mantiene la garantía absoluta de no duplicación.

Define cómo conservar:

- Identidad del operador.
- Razón.
- Evidencia utilizada.
- Caja involucrada.
- Autorización.
- Resultado.
- Relación con el trabajo original.

Evita crear trabajos lógicos duplicados accidentalmente.

Actualiza el runbook y las pruebas.

---

# 6. H3 — SERIAL ÚNICO Y EVIDENCIA

## Diagnóstico

Producto, GTIN y lote no identifican necesariamente una caja individual.

Dos cajas pueden transportar datos idénticos.

Por tanto, un escaneo de GTIN y lote no demuestra que se verificó la etiqueta correcta.

## Correcciones

### 6.1 LabelSerial

Incorpora un identificador único por etiqueta.

Debe:

- Generarse al crear el PrintJob.
- Ser persistente.
- Formar parte del payload.
- Estar cubierto por el hash del payload.
- Aparecer en la evidencia de verificación.
- Tener una restricción de unicidad apropiada.

Puede utilizarse SQL Server `SEQUENCE`, pero documenta que los valores de una secuencia pueden presentar huecos incluso ante rollbacks.

No prometas consecutividad sin saltos.

### 6.2 Formatos

Permite un serial interno.

SSCC-18 solamente debe utilizarse si el caso de uso y los requisitos GS1 de la planta lo justifican.

No asumas que cualquier secuencia numérica puede considerarse un SSCC válido.

### 6.3 Evidencia

Define:

- `PRINTED`
- `APPLIED`
- `VERIFIED`

Aclara qué demuestra realmente cada nivel y qué no puede demostrar.

`PRINTED` no implica necesariamente aplicación.

`APPLIED` no implica necesariamente contenido correcto.

`VERIFIED` no implica automáticamente correspondencia con la caja física si la correlación no existe.

`CONFIRMED` solamente debe asignarse cuando la evidencia cumpla el nivel exigido por la estación y sus condiciones de correlación.

### 6.4 Contratos JSON

Actualiza `PrintJobResult` v1.

Contempla:

- `idempotencyKey`
- `final`
- `labelSerial`
- `reason`
- `evidence.level`
- `evidence.readSerial`
- `evidence.reference`

Incluye reglas condicionales mediante JSON Schema Draft 2020-12.

Un JSON Schema no puede demostrar por sí solo que la evidencia física es verdadera; documenta y prueba las validaciones que necesariamente pertenecen a la lógica de negocio.

Los contratos v1 no han sido publicados a consumidores, por lo que pueden corregirse sin crear v2. Documenta los cambios en `CHANGELOG.md`.

---

# 7. H4 — EPOCH DEL PLC, ORDEN Y COLISIONES

## Problema

La clave basada únicamente en estación y `CycleId` puede colisionar cuando el PLC reinicia su contador.

## Solución base

Usa identidad compuesta por:

`(SiteId, StationId, PlcEpoch, CycleId)`

Define `idempotencyKey` a partir de estos componentes.

Mantén `eventId` determinista con UUIDv5 y un namespace fijo y documentado.

## Precaución: eventos fuera de orden

La revisión anterior proponía bloquear toda disminución de `CycleId`.

No aceptes esa regla sin más análisis.

Durante reconexiones, recuperación de notificaciones o replay pueden recibirse ciclos anteriores legítimos.

Diseña procedimientos diferentes para:

- Duplicado auténtico.
- Evento antiguo.
- Evento fuera de orden.
- Hueco en secuencia.
- Colisión de identidad.
- Reinicio real del contador.
- Cambio legítimo de `PlcEpoch`.
- Cambio no autorizado de época.

Una disminución del contador no implica necesariamente un reinicio.

## Colisiones

Cuando exista una clave igual con datos de origen incompatibles, no reutilices silenciosamente la detección.

Registra la anomalía, bloquea nuevas impresiones según el riesgo operacional y genera una alerta.

Define cuidadosamente qué campos sirven como evidencia de colisión y cuáles pueden variar legítimamente por retransmisiones.

No uses igualdad exacta de timestamps como único criterio universal sin justificarlo.

## PLC sin PlcEpoch

Documenta una alternativa limitada administrada por el gateway.

Declara expresamente las situaciones en las que resulta imposible reconstruir la identidad original de un ciclo.

No prometas garantías que requieren información que nunca se recibió.

---

# 8. H5 — ARQUITECTURA MÍNIMA Y LATENCIA

## Decisión propuesta

Adopta inicialmente una arquitectura modular desplegada en un solo proceso edge:

`src/EdgeHost`

Con componentes internos como:

- OPC Ingest.
- Carton Orchestrator.
- ERP Snapshot Sync.
- Print Agent.
- Outbox Relay.
- Recovery Worker.

Los componentes pueden mantenerse en bibliotecas separadas.

Utiliza `System.Threading.Channels` para señalización rápida dentro del proceso.

SQL Server será la autoridad transaccional.

RabbitMQ puede utilizarse para publicar eventos e integrarse con otros sistemas, sin convertirlo innecesariamente en una dependencia síncrona del camino crítico de impresión.

## Perfiles operacionales

Define dos perfiles.

### Perfil mínimo

- EdgeHost.
- SQL Server.
- OPC UA.
- Printer Adapter.
- Outbox.
- ERP cache.
- Falla cerrada cuando no es posible persistir o verificar condiciones críticas.

### Perfil extendido

Opcionalmente:

- FIFO de ciclos del PLC.
- Confirmación ACK al PLC.
- Journal local SQLite.
- Recuperación durable de eventos durante indisponibilidad de SQL.

Explica qué requisito justifica cada componente adicional.

No elimines mecanismos de durabilidad únicamente para reducir el número de tecnologías.

## Ciclos no observados

No inventes registros de detección física cuando solamente existe un hueco en el contador.

Considera una entidad como:

`SequenceGap`

que represente ciclos posiblemente omitidos.

Diferencia:

- Detección real observada.
- Detección recuperada con evidencia.
- Hueco inferido sin datos suficientes.

Si el timestamp original no está disponible, no fabriques uno para calcular un deadline.

Define cómo auditar, alertar y publicar resultados de recuperación sin afirmar hechos físicos desconocidos.

## Latencia

No adoptes automáticamente `p99 < 50 ms`.

Debes:

1. Separar latencia de software y tiempo físico.
2. Definir objetivos configurables y medibles.
3. Diferenciar tiempo de muestreo OPC del tiempo posterior a la recepción.
4. Medir p50, p95 y p99.
5. Considerar el takt time y la capacidad de la impresora.
6. Evitar prometer métricas sin benchmark.

La documentación debe incluir un presupuesto inicial de diseño y señalar explícitamente que todavía no está validado con hardware real.

---

# 9. H6 — PROPIETARIO ÚNICO DE IMPRESORA

## Diagnóstico

Un `PrinterLease` con `FencingToken` en SQL no garantiza por sí solo que dos procesos no transmitan al mismo dispositivo.

## Condición de carrera

Considera:

1. Agent A obtiene token 12.
2. Agent A valida propiedad y se suspende.
3. Expira su lease.
4. Agent B obtiene token 13.
5. Agent B transmite.
6. Agent A reanuda su ejecución y transmite.

Si la impresora no valida fencing tokens, SQL no puede rechazar directamente la transmisión del agente antiguo.

## Decisión requerida

Para el MVP, favorece un único propietario efectivo del canal de impresión.

Analiza:

- Un Print Agent activo.
- Exclusión de acceso al dispositivo.
- Serialización de transmisiones.
- Recuperación de conexiones.
- Jobs `SENDING` heredados.
- Comandos pendientes en la impresora.
- Procesos suspendidos.
- Reinicios.

Si propones `PrinterLease`, aclara sus garantías y limitaciones.

Para alta disponibilidad, define qué mecanismo impide físicamente que el propietario anterior continúe transmitiendo.

No presentes un fencing token almacenado únicamente en SQL como una garantía completa cuando el recurso externo no lo valida.

Incluye una prueba de concurrencia con un proceso suspendido después de validar su token.

---

# 10. H7 — CONSISTENCIA DOCUMENTAL

Reestructura los ADR existentes y crea los nuevos que sean necesarios.

Cada ADR debe incluir:

- Contexto específico.
- Problema técnico.
- Alternativas.
- Decisión.
- Justificación.
- Consecuencias.
- Riesgos.
- Pruebas que validan la decisión.
- Condiciones que obligarían a revisarla.

No reutilices el mismo contexto genérico para todas las decisiones.

## Diagramas

Mermaid será la fuente editable de los diagramas arquitectónicos.

La máquina de estados deberá derivarse o validarse contra `contracts/state-machine.json`.

Regenera:

- SVG.
- PNG.
- DOCX.
- PDF.

No mantengas diagramas contradictorios.

Documenta los comandos utilizados.

## Idioma

Explicaciones en español.

Conserva en inglés:

- Identificadores de clases.
- Estados.
- Eventos.
- Métricas.
- Columnas.
- Nombres de propiedades.
- Tipos técnicos cuando corresponda.

---

# 11. HALLAZGOS ADICIONALES

Además de H1-H7, corrige lo siguiente.

## H8 — Identidad de la caja física

Distingue explícitamente:

**Una etiqueta por ciclo detectado** frente a **una etiqueta por caja física**.

Un mismo cartón podría volver a atravesar el sensor y generar otro ciclo válido.

La idempotencia por `CycleId` no resuelve necesariamente ese caso.

Define las condiciones bajo las cuales el sistema puede garantizar cada una de esas propiedades.

Documenta opciones para:

- `CartonId`.
- Código de barras previo.
- Tracking físico.
- Recirculación.
- Sensores adicionales.

No inventes hardware existente.

## H9 — Configuración versionada

Los parámetros de estación pueden cambiar mientras hay trabajos pendientes.

Cada decisión crítica debe poder asociarse a la versión de configuración que se utilizó.

Define:

- `StationConfigRevision`.
- Qué parámetros se capturan por detección.
- Qué parámetros quedan congelados por job.
- Cuáles pueden evaluarse dinámicamente por seguridad.
- Cómo se audita un cambio.
- Qué sucede con trabajos pendientes cuando cambia la configuración.

No permitas modificaciones retroactivas silenciosas.

## H10 — Garantías y limitaciones

Crea una matriz con:

- Garantía deseada.
- Mecanismo implementable.
- Condiciones necesarias.
- Dependencias de hardware.
- Riesgo residual.
- Prueba de aceptación.

Distingue claramente entre:

- Garantías demostrables por software.
- Garantías que requieren integración física.
- Objetivos de rendimiento.
- Supuestos pendientes de confirmación con OT.

---

# 12. MODELO DE DATOS

Actualiza el DDL coherentemente con las decisiones anteriores.

Evalúa, al menos:

- `StationConfig`
- `StationConfigRevision`
- `CartonDetection`
- `PrintJob`
- `PrintAttempt`
- `PrinterLease`, si corresponde
- `SequenceAnomaly`
- `SequenceGap`
- `ErpWorkOrderCache`
- `InboxMessage`
- `OutboxEvent`
- Auditoría de reimpresión excepcional

Evita tablas adicionales que no tengan una responsabilidad concreta.

Define:

- PK y FK.
- Índices únicos.
- CHECK constraints.
- Concurrencia.
- Idempotencia.
- Inmutabilidad.
- Versionado.
- Semántica de estados.
- Transacciones.
- Estrategia de recuperación.

Si una restricción requiere comparar datos de tablas distintas, no afirmes que un CHECK local puede garantizarla.

Especifica dónde se validará.

---

# 13. PRUEBAS OBLIGATORIAS

Conserva las pruebas existentes T01-T16.

Integra las nuevas T17-T28 de la revisión anterior, ajustándolas si el nuevo diseño modifica los contratos o estados.

Añade pruebas adicionales.

### T29 — Suspensión después del claim

Un Print Agent reclama el trabajo antes del deadline, queda suspendido y se reactiva después del plazo.

Esperado: no debe realizarse un envío tardío autorizado. Documentar el límite físico de la garantía.

### T30 — Evento OPC antiguo fuera de orden

Se procesa un contador mayor y posteriormente llega una retransmisión válida de un ciclo anterior.

Esperado: no tratar automáticamente el evento como reinicio del PLC.

### T31 — Cola de impresora

Se transmiten bytes antes del deadline, pero la etiqueta permanece en la cola del dispositivo hasta que la caja ha abandonado su ventana física.

Esperado: el sistema no declara una aplicación correcta sin evidencia adecuada.

### T32 — Recirculación física de una caja

La misma caja genera dos ciclos técnicamente válidos.

Esperado: demostrar el comportamiento de acuerdo con la garantía de identidad disponible, sin confundir unicidad por ciclo con unicidad por caja.

### T33 — Cambio de configuración

El plazo o parámetros de estación cambian mientras un PrintJob está pendiente.

Esperado: configuración versionada y comportamiento determinista.

### T34 — Reimpresión con incertidumbre

Un trabajo queda `UNKNOWN` y un operador solicita reimpresión.

Esperado: no se ejecuta un reenvío ordinario sin reconciliación o procedimiento excepcional explícito, autorizado y auditado.

### T35 — Anomalía y hueco de secuencia

Se detecta un salto de contador sin disponer de todos los timestamps originales.

Esperado: auditoría correcta, sin fabricar detecciones físicas.

Documenta para cada prueba:

- Precondiciones.
- Datos iniciales.
- Inyección.
- Estados esperados.
- Eventos publicados.
- Efectos físicos esperados.
- Métricas afectadas.
- Criterios de aprobación.

---

# 14. VERIFICACIÓN AUTOMÁTICA

Crea o actualiza:

`tools/verify_package.py`

Utiliza Python 3 y bibliotecas adecuadas.

El verificador debe evaluar al menos:

1. JSON Schemas Draft 2020-12.
2. Ejemplos válidos.
3. Ejemplos inválidos.
4. UUIDv5 deterministas.
5. Coherencia de estados.
6. Coherencia de transiciones.
7. Correspondencia entre contratos y máquina de estados.
8. Restricciones SQL que puedan verificarse estáticamente.
9. Existencia de diagramas y artefactos.
10. Checksums.

No limites la coherencia semántica a buscar nombres de estados mediante expresiones regulares si eso permite contradicciones importantes.

Cuando una propiedad no pueda comprobarse estáticamente, regístrala como pendiente de pruebas de integración.

Ejecuta las validaciones disponibles.

Registra comandos, salidas y errores reales.

No declares que SQL Server, Mermaid o cualquier otra herramienta fueron verificados si no se ejecutaron.

El propio archivo de checksums debe quedar excluido de su conjunto de hashes.

---

# 15. GUARDRAILS PARA IMPLEMENTACIÓN CON IA

Actualiza `AI_CONTEXT.md` y `GUARDRAILS.md` con invariantes explícitos.

Como mínimo:

1. Nunca crear dos PrintJobs ordinarios para la misma detección y tipo de etiqueta.
2. Nunca considerar un timeout como prueba de impresión fallida.
3. Nunca reintentar automáticamente un envío potencialmente realizado.
4. Nunca enviar un trabajo vencido como operación ordinaria.
5. Nunca considerar un ACK TCP como prueba física de aplicación.
6. Nunca confirmar una lectura de serial incorrecto.
7. Nunca inventar una identidad de caja física.
8. Nunca interpretar cualquier contador menor como reinicio sin evaluar retransmisiones.
9. Nunca modificar silenciosamente el payload de un PrintJob en curso.
10. Nunca utilizar un snapshot ERP no validado.
11. Nunca permitir que dos propietarios no controlados transmitan al mismo dispositivo.
12. Nunca presentar como garantía de hardware una condición que solamente valida SQL.
13. Nunca marcar una prueba como superada sin ejecutarla.
14. Nunca introducir complejidad arquitectónica sin un requisito explícito que la justifique.

Cada invariante debe vincularse con un mecanismo de implementación y una o más pruebas.

---

# 16. DOCUMENTACIÓN FINAL REQUERIDA

Actualiza o genera:

1. Documento principal de arquitectura v1.1.
2. Documento técnico de componentes.
3. Diagramas de arquitectura, secuencia, flujo de eventos, estados, recuperación y modelo relacional.
4. Contratos JSON actualizados.
5. Máquina de estados canónica.
6. DDL completo.
7. ADR revisados y nuevos.
8. Guardrails para agentes.
9. Matriz de pruebas.
10. Runbook operacional.
11. Estrategia de idempotencia.
12. Estrategia de recuperación.
13. Matriz de garantías y limitaciones.
14. Plan de implementación por fases.
15. Prompt actualizado de arranque para Codex/Claude.
16. `CHANGELOG.md`.
17. `docs/REVISION_LOG_v1.1.md`.
18. `CHECKSUMS.sha256`.
19. PDF y DOCX consolidados.
20. ZIP final del paquete actualizado.

No elimines contenido válido del paquete original sin justificarlo.

Mantén trazabilidad entre los hallazgos, las decisiones y los archivos modificados.

---

# 17. CRITERIOS DE ACEPTACIÓN

La revisión solamente puede considerarse finalizada cuando:

- Los hallazgos H1-H10 están resueltos o documentados como decisiones abiertas justificadas.
- No existen contradicciones conocidas entre estados, DDL, diagramas y contratos.
- El diseño distingue efectos lógicos y físicos.
- Se documentan los límites de la idempotencia.
- El vencimiento por caja y los posibles envíos tardíos están contemplados.
- El manejo de `UNKNOWN` no introduce reenvíos automáticos inseguros.
- Las reimpresiones excepcionales tienen un procedimiento explícito.
- Se reconoce que SQL por sí solo no controla el canal físico de impresión.
- Los eventos fuera de orden no se confunden automáticamente con reinicios.
- Los huecos de secuencia no se presentan como detecciones observadas.
- La arquitectura mantiene un perfil mínimo razonable.
- Todos los objetivos de latencia no medidos están identificados como hipótesis de diseño.
- Los documentos están alineados con la misma especificación.
- Las validaciones ejecutables disponibles finalizan correctamente o sus fallos quedan documentados.
- El paquete final puede entregarse a otro agente IA para comenzar la implementación sin tener que reinterpretar contradicciones arquitectónicas.

---

# 18. DECISIONES ABIERTAS PARA OT

No inventes respuestas para cuestiones que dependen de la planta.

Documenta especialmente:

- ¿La impresora es independiente o forma parte de un sistema print-and-apply?
- ¿Las cajas avanzan continuamente o pueden detenerse?
- ¿Cuál es el tiempo real entre sensor y punto de aplicación?
- ¿Puede el PLC exponer `PlcEpoch` persistente?
- ¿Existe un identificador físico de caja?
- ¿Puede haber recirculación?
- ¿La impresora informa finalización por trabajo?
- ¿Existe escáner de verificación?
- ¿El PLC puede retener, detener o rechazar cajas?
- ¿Puede existir más de un agente activo?
- ¿Se requiere alta disponibilidad del Print Agent?
- ¿Qué ocurre operacionalmente ante una impresión incierta?
- ¿Se permite continuar produciendo con SQL Server fuera de servicio?
- ¿Es obligatorio conservar la historia de todos los ciclos durante una interrupción?
- ¿Qué nivel de evidencia se exige para declarar una etiqueta confirmada?

Para cada decisión abierta, indica su impacto arquitectónico y qué parte de la implementación puede avanzar sin resolverla.

---

# 19. FORMATO DE ENTREGA

Al terminar, entrega:

## A. Veredicto técnico

Resume qué recomendaciones originales aceptaste, cuáles modificaste y cuáles rechazaste.

Explica los motivos.

## B. Arquitectura consolidada

Describe la arquitectura seleccionada, sus garantías, límites y alternativas.

## C. Cambios por hallazgo

Para H1-H10, identifica:

- Archivos modificados.
- Decisiones adoptadas.
- Invariantes incorporados.
- Pruebas asociadas.

## D. Validación

Incluye:

- Comandos ejecutados.
- Resultados obtenidos.
- Herramientas no disponibles.
- Validaciones no ejecutadas.
- Riesgos residuales.

## E. Decisiones abiertas

Relaciona todo lo que requiere confirmación de OT o del hardware real.

## F. Artefacto final

Entrega el ZIP actualizado junto con el PDF y DOCX.

Asegúrate de que los archivos del paquete correspondan realmente a la versión 1.1.

---

# 20. INSTRUCCIÓN FINAL

Este proyecto será utilizado para evaluar mi capacidad real como ingeniero de software senior y arquitecto técnico utilizando inteligencia artificial.

Por tanto, prefiero una solución técnicamente correcta, aunque implique reconocer limitaciones, sobre una solución aparentemente completa que prometa garantías imposibles de demostrar.

**No busques estar de acuerdo conmigo ni con revisiones anteriores. Busca producir el diseño más coherente y defendible técnicamente.**

No implementes producción.

No inicies la Fase 0.

Realiza la auditoría, corrige la documentación, ejecuta las verificaciones disponibles y entrega el paquete consolidado v1.1.

El resultado debe quedar listo para iniciar posteriormente la implementación con un agente IA, mediante fases controladas, criterios de aceptación e invariantes verificables.