# Diagramas (v1.1)

Mermaid es la única fuente editable. Cada `.mmd` tiene su `.svg` y `.png` generados con `@mermaid-js/mermaid-cli`. Los archivos DOT de la versión 1.0 se eliminaron porque divergían de su Mermaid.

| Archivo | Contenido | Origen |
| --- | --- | --- |
| `01_arquitectura` | Perfil mínimo y componentes del perfil extendido | Manual |
| `02_flujo_eventos` | Identidad, plazo, claim, segunda comprobación, evidencia | Manual |
| `03a_estados_printjob` | PrintJob, flujo ordinario y corrección J18 | Generado desde `contracts/state-machine.json` |
| `03b_estados_printjob_excepciones` | PrintJob, procedimiento excepcional (X01 a X03) | Generado |
| `03c_estados_intento` | PrintAttempt | Generado |
| `03d_estados_deteccion` | CartonDetection | Generado |
| `03e_estados_hueco` | SequenceGap | Generado |
| `03f_estados_autorizacion` | ReprintAuthorization | Generado |
| `04_incertidumbre` | Salidas de un PrintJob `UNKNOWN` | Manual |
| `05_recuperacion` | Recuperación por componente | Manual |
| `06_modelo_datos` | Modelo relacional | Manual |
| `07_secuencia` | Secuencia del camino feliz con puntos de persistencia | Manual |
| `08_clasificacion_secuencia` | Duplicados, tardíos, huecos, colisiones, épocas y plazo confiable | Manual |

Los diagramas 03 no se editan a mano: se regeneran con `python3 tools/generate_artifacts.py`, y `tools/verify_package.py` comprueba que sus aristas coinciden con la máquina de estados.

Después de cambiar un `.mmd`, se renderiza con:

```bash
python3 tools/render_diagrams.py          # solo los que cambiaron (Windows: py tools\render_diagrams.py)
python3 tools/render_diagrams.py --all    # todos
```

El script llama a Mermaid CLI (`mmdc`) y escribe `diagrams/render-manifest.json` con el SHA-256 de cada fuente renderizada. El verificador compara ese hash con el `.mmd` actual; no usa fechas de archivo, porque git y los descompresores no conservan el orden de modificación. `tools/puppeteer.json` apunta al Chromium del entorno de revisión y el script solo lo usa si ese ejecutable existe; en otro equipo, `mmdc` usa el Chromium de Puppeteer.
