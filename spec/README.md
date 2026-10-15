# Carton Printing · paquete de arquitectura e implementación asistida por IA

**Versión:** 1.1 · **Fecha:** 2026-10-13 · **Estado:** especificación consolidada y verificable; NO es software implementado.

Conecta un ciclo OPC UA con `CartonDetected`, resuelve producto y lote desde un snapshot ERP vigente, crea un único `PrintJob`, controla cada transmisión física y publica `PrintJobResult`, tolerando duplicados, caídas, reinicios, eventos fuera de orden e impresiones inciertas.

## Orden de lectura

1. `documento/Arquitectura_Etiquetado_OPC_Idempotente_v1.1.pdf` (o `DOCUMENTO_PRINCIPAL.md`): documento consolidado.
2. `docs/MATRIZ_GARANTIAS.md`: qué se garantiza por software, qué requiere integración física y qué es solo un objetivo.
3. `contracts/state-machine.json`: la fuente única de estados y transiciones.
4. `AI_CONTEXT.md`, `docs/GUARDRAILS.md`, `prompts/INICIO_CODEX.md`: reglas para el agente que implemente.
5. `docs/ARQUITECTURA_TECNICA.md`, `docs/ESTRATEGIA_IDEMPOTENCIA.md`, `docs/ESTRATEGIA_RECUPERACION.md`: detalle técnico.
6. `docs/adr/`: 15 decisiones con contexto, alternativas, riesgos y pruebas.
7. `tests/MATRIZ_PRUEBAS.md`, `docs/RUNBOOK_OPERACIONES.md`, `docs/PLAN_IMPLEMENTACION.md`, `docs/DECISIONES_ABIERTAS_OT.md`.

## Estructura

| Carpeta | Contenido |
| --- | --- |
| `contracts/` | Máquina de estados, invariantes, schemas v1 y ejemplos válidos, inválidos y de regla de negocio |
| `database/` | DDL de SQL Server, semilla de transiciones generada, sentencias de referencia, journal SQLite |
| `diagrams/` | Fuentes Mermaid con su SVG y PNG |
| `docs/` | Documentos técnicos, ADR, runbook, plan, matriz de garantías, decisiones abiertas, log de revisión |
| `tests/` | Catálogo de pruebas (fuente) y matriz generada |
| `tools/` | Generador de artefactos, generador de ejemplos de contrato, verificador del paquete y construcción de DOCX y PDF |
| `prompts/` | Prompt de arranque y, en `historial/`, los prompts de las revisiones que originaron esta versión |
| `documento/` | DOCX y PDF consolidados v1.1 |

## Verificación

```bash
python3 -m pip install "jsonschema>=4.18" sqlfluff
python3 tools/generate_artifacts.py --check   # artefactos derivados al día
python3 tools/verify_package.py               # contratos, estados, DDL, trazabilidad, diagramas, documentos, checksums
```

En Windows se usa el lanzador `py` (`py -m pip install ...`, `py tools\verify_package.py`). El verificador necesita el paquete completo: una copia parcial falla en los grupos de diagramas, documentos y checksums. Si el paquete vive dentro de un repositorio con git, agregar `spec/** -text` (o la ruta que corresponda) a `.gitattributes` para que git no cambie los fines de línea y con ellos los checksums.

Después de un cambio intencional y revisado: `tools/generate_artifacts.py`, `tools/render_diagrams.py` si cambió un `.mmd`, `tools/build_examples.py` si cambiaron los contratos, `tools/update_checksums.py` y, al final, `tools/verify_package.py`.

El verificador termina en 0 en esta versión (salida registrada en `docs/REVISION_LOG_v1.1.md`). Lo que no puede comprobar de forma estática (ejecución en SQL Server, comportamiento concurrente, propiedades físicas, latencias) lo lista como pendiente de integración. El registro de lo ejecutado está en `docs/REVISION_LOG_v1.1.md`.

## Qué contiene y qué no

- **Sí:** especificación coherente entre estados, DDL, contratos, diagramas y pruebas; validaciones ejecutables; procedimiento de reimpresión excepcional; perfiles mínimo y extendido; decisiones abiertas para OT.
- **No:** servicios .NET, integración con PLC o impresoras reales, pruebas de sistema ejecutadas, benchmarks. El DDL de SQL Server se parseó con sqlfluff pero no se ejecutó en SQL Server en esta revisión: los triggers y reglas entre tablas quedan sin ejecutar hasta T47 (Fase 1).

## Primer trabajo para la IA

Cuando se autorice iniciar la implementación, abrir `prompts/INICIO_CODEX.md` y ejecutar solo la Fase 0. Esta revisión no la inició.
