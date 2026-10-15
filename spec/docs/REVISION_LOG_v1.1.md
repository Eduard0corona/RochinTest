# Registro de la revisión 1.1

Fecha: 13 de octubre de 2026. Alcance: auditoría y consolidación documental del paquete 1.0 hasta la 1.1. No se implementó ningún servicio, no se inició la Fase 0 y no se conectó ningún equipo. Este registro dice qué se ejecutó, con qué herramienta y con qué resultado, y qué no se pudo ejecutar.

## 1. Entorno

| Herramienta | Versión | Uso |
| --- | --- | --- |
| Linux (contenedor de revisión) | 6.18 | Entorno |
| Python | 3.13.16 | Verificador, generador, ejemplos |
| jsonschema | 4.26.0 | Validación Draft 2020-12 de schemas y ejemplos |
| sqlfluff | 4.4.0, dialecto `tsql` | Parseo de los tres archivos T-SQL |
| SQLite (módulo `sqlite3` de Python) | 3.45.1 | Ejecución del journal del perfil extendido |
| Mermaid CLI (`mmdc`) | 11.16.0, con Chromium 141 | Render de los 13 diagramas a SVG y PNG |
| pandoc | 3.1.3 | Markdown a DOCX |
| LibreOffice | 24.2.7.2 | DOCX a PDF |
| SQL Server | No disponible | Ver sección 3 |

## 2. Comandos y resultados

| Paso | Comando | Resultado |
| --- | --- | --- |
| Línea base | Extracción del ZIP 1.0 y `git init` con la versión 1.0 como primer commit | Diferencias de esta revisión trazables con `git diff` (el `.git` no se incluye en el ZIP) |
| Generación | `python3 tools/generate_artifacts.py` | Diagramas 03a a 03f, `database/sqlserver_state_transitions.sql`, `tests/MATRIZ_PRUEBAS.md` y bloques de documentos regenerados desde sus fuentes |
| Ejemplos | `python3 tools/build_examples.py` | 19 válidos, 24 inválidos y 5 de negocio; el script exige que cada inválido falle por una sola regla de JSON Schema y cada ejemplo de negocio por una sola regla de negocio |
| Parseo T-SQL | `python3 -m sqlfluff parse <archivo> --config tools/sqlfluff.cfg --format yaml` | Los tres archivos se parsean sin segmentos `unparsable` |
| Journal SQLite | Ejecución en memoria dentro del verificador | La PK rechaza una segunda fila con la misma identidad; el `CHECK` rechaza `CycleId` `12abc`, vacío, de 21 dígitos, `007` y `18446744073709551616`, y acepta `0`, `7` y `18446744073709551615` |
| Diagramas | Bucle de `diagrams/README.md` con `mmdc` | 13 diagramas renderizados y revisados visualmente. `07_secuencia` falló una vez por un `;` dentro de un mensaje (separador de sentencias en Mermaid) y se corrigió; la vista excepcional de PrintJob se pasó a orientación vertical con etiquetas cortas porque las etiquetas se superponían |
| Documentos | `bash tools/build_documents.sh` | `documento/Arquitectura_Etiquetado_OPC_Idempotente_v1.1.docx` y `.pdf` (50 páginas) |
| Validación DOCX | `validate.py` del skill de DOCX sobre el documento generado | Quedan 9 errores de esquema de las mismas clases que produce `pandoc` sin plantilla sobre un Markdown mínimo (orden de `qFormat` en estilos, longitud de `nsid` en numeración, `jc` y `pStyle` en el documento, `zoom` en ajustes). LibreOffice abre y convierte el archivo. Los 9 avisos de tipo de contenido de las imágenes se eliminaron declarando `png` por extensión en `tools/build_documents.sh` |
| Verificación | `python3 tools/verify_package.py` | Ver sección 6 |

## 3. Lo que no se pudo ejecutar

- **SQL Server.** `docker pull mcr.microsoft.com/mssql/server:2022-latest` falló: el registro respondió `Forbidden` (`failed to resolve reference "mcr.microsoft.com/mssql/server:2022-latest": ... Forbidden`). No se intentaron otras rutas para obtener la imagen. Por eso el DDL, la semilla, los triggers, los índices filtrados, las FK compuestas y las sentencias de referencia **no se ejecutaron**: sqlfluff comprueba sintaxis, no semántica (no detecta, por ejemplo, el Msg 334 ni un trigger que falla con 0 filas). Su primera ejecución es T47, en la Fase 1.
- **Pruebas de sistema.** De las 47 pruebas del catálogo solo T23 (contratos) y T28 (coherencia del paquete) se ejecutaron, ambas dentro del verificador. Las demás requieren la implementación y figuran como `NO_EJECUTADA`.
- **Propiedades físicas y de rendimiento.** Nada sobre impresión, aplicación, verificación, ventana física, latencia o capacidad se midió. Los tiempos del documento son hipótesis.
- **Durabilidad del journal ante corte de energía** con `synchronous = FULL`: no ejecutada (T09).

## 4. Verificador: defectos encontrados en el propio verificador

Una ejecución intermedia (13 de octubre, antes de cerrar la revisión) mostró que el verificador tenía dos defectos que podían dar una falsa sensación de éxito:

- Contaba la clave `$comment` de `contractOutcomes` como un outcome del contrato.
- Imprimía las líneas `OK` de un grupo aunque ese grupo tuviera errores.

Ambos se corrigieron: los grupos con errores ya no reportan `OK` y las claves que empiezan con `$` se ignoran. En la misma corrida faltaban los PNG y SVG de los diagramas de estados nuevos, había referencias a un diagrama retirado y a un término retirado, y varios ADR carecían de secciones; todo se corrigió antes de la revisión independiente.

## 5. Revisiones independientes

### 5.1 Primera revisión del borrador

Un agente revisor que no había participado en la redacción auditó el borrador completo con instrucciones adversariales. Corrió el verificador (14 grupos OK con `--skip-checksums`, 2 errores sin esa opción por los checksums de la versión 1.0) y revisó el T-SQL por la semántica documentada del motor, sin SQL Server. Reportó 30 hallazgos: 7 de severidad alta, 14 media y 9 baja. Los más graves eran defectos de ejecución que sqlfluff no detecta:

- Los triggers de inmutabilidad lanzaban error en todo `UPDATE` de 0 filas (claim perdido, TX-R0 en un arranque limpio, barridos vacíos).
- El claim usaba `OUTPUT` sin `INTO` sobre una tabla con triggers (Msg 334).

Los demás eran huecos de diseño: plazo fabricado para registros tardíos con `GATEWAY_RECEIPT`, envío fuera de orden, reimpresión sin confirmar que dejaba el job en un estado falso, autorizaciones no validadas en SQL, acciones manuales sin registro. Ninguno se rechazó; la decisión y la corrección de cada uno están en la sección 23.1 de `DOCUMENTO_PRINCIPAL.md` y en `CHANGELOG.md`.

### 5.2 Segunda revisión, sobre las correcciones

Otro agente independiente revisó el paquete ya corregido, con la lista de los 30 hallazgos y la consigna de no modificar nada. Corrió el verificador (14 grupos OK con `--skip-checksums`; código 1 sin esa opción porque `CHECKSUMS.sha256` aún era el de la versión 1.0), sqlfluff, casos construidos a mano con jsonschema y sqlite3, y recorrió por lectura cada transacción de referencia contra los triggers. Resultado:

- De los 30 hallazgos originales, 25 resueltos y 5 parciales: el 3 (faltaba la primera notificación de una suscripción), el 5 (X03 solo al vencer la ventana), el 19 (estación bloqueada no es línea detenida), el 20 (log, checksums y documentos sin cerrar en ese momento) y un residuo del 16 en la redacción de garantías.
- 14 hallazgos nuevos: 1 de severidad alta, 5 media y 8 baja. El de severidad alta: con `GATEWAY_RECEIPT`, la primera notificación de un monitored item recreado (el valor actual) recibía plazo como si llegara en vivo y podía imprimirse sobre otra caja.

Todos se aceptaron. La tabla con cada hallazgo y su corrección está en la sección 23.1 de `DOCUMENTO_PRINCIPAL.md`, y el detalle de los cambios en `CHANGELOG.md`. No se hizo una tercera revisión independiente: las correcciones de esta segunda ronda solo se comprobaron con el verificador, sqlfluff, sqlite3 y lectura propia de las transacciones.

## 6. Verificación final

Comandos, en este orden, desde la raíz del paquete:

```bash
python3 tools/generate_artifacts.py --check
python3 tools/render_diagrams.py --check
python3 tools/update_checksums.py
python3 tools/verify_package.py
sha256sum -c CHECKSUMS.sha256 --quiet
```

Salida de la corrida completa, con checksums:

```text
CartonPrinting v1.1 · verify_package.py
========================================================================
OK      [estados] 5 máquinas coherentes: alcanzables, terminales cerrados, prohibidas ausentes, 45 transiciones únicas
OK      [contratos] 2 schemas válidos como Draft 2020-12; formatos uuid y date-time verificados con validadores propios
OK      [contratos] 19 ejemplos válidos pasan schema y reglas de negocio
OK      [contratos] cada outcome del contrato tiene al menos un ejemplo válido
OK      [contratos] 24 ejemplos inválidos fallan el schema, cada uno solo por la regla declarada (T23)
OK      [contratos] 5 ejemplos pasan el schema y fallan exactamente una regla de negocio: el schema no basta (T23)
OK      [contratos] outcomes y motivos del schema coinciden con contracts/state-machine.json
OK      [sql] CHECK de estados y motivos, objetos requeridos, opciones SET, guardas de 0 filas, OUTPUT INTO, triggers por máquina y semilla de transiciones coinciden con la fuente
OK      [sql] sqlfluff (dialecto tsql) parsea los 3 archivos de SQL Server sin segmentos no reconocidos
OK      [sql] sqlite_journal.sql ejecutado en sqlite3: PK (SiteId, StationId, PlcEpoch, CycleId) rechaza duplicados y CycleId no canónico (texto, vacío, ceros a la izquierda, mayor que UInt64)
OK      [trazabilidad] 45 transiciones y 26 invariantes cubiertos por 47 pruebas; cada prueba asignada a una fase; garantías físicas con hardware declarado
OK      [generados] diagramas de estados, semilla SQL, matriz de pruebas y bloques de documentos al día con sus fuentes
OK      [diagramas] diagramas de estados con las mismas aristas que la máquina; cada .mmd tiene SVG y PNG renderizados desde su versión actual
OK      [documentos] 34 documentos sin términos retirados ni estados desconocidos; 15 ADR con secciones completas y contextos distintos; enlaces internos y 263 rutas citadas existen
OK      [checksums] 149 archivos con hash correcto y nombre ASCII; CHECKSUMS.sha256 excluido de sí mismo
------------------------------------------------------------------------
PENDIENTE DE INTEGRACIÓN (no verificable de forma estática):
  - Ejecución del DDL en SQL Server 2022 (triggers, índices filtrados, FK compuestas): Fase 1 con Testcontainers (T47).
  - Comportamiento concurrente de UX_PrintAttempt_PrinterInFlight y del claim bajo carga real (T02).
  - Reglas entre tablas de TR_PrintAttempt_Guard (incluida ReadSerial = LabelSerial y nivel mínimo de evidencia), TR_ReprintAuthorization_Guard y TR_PrintJob_StateMachine (OperatorAction, intento excepcional, EvidenceObservation): Fase 1 (T47).
  - Orden monotónico por impresora con PrinterSendOrder bajo concurrencia y eventos tardíos (T42).
  - Detección de saltos del reloj de pared contra el reloj monotónico del EdgeHost (T37).
  - Segunda comprobación monotónica antes de escribir y su límite ante pausas del proceso (T29).
  - Detección de recirculación con UPDLOCK, HOLDLOCK sobre IX_PrintJob_Carton e IX_CartonDetection_Carton (T16).
  - Durabilidad del journal SQLite ante corte de energía con synchronous=FULL (T09).
  - Toda propiedad física: impresión, aplicación, verificación y ventana física (FAT).
  - Latencias y capacidad: hipótesis de diseño sin benchmark (T12).
------------------------------------------------------------------------
Resultado: 15 grupos OK, 0 errores
```

La salida se pegó aquí, se regeneró `CHECKSUMS.sha256` (este archivo cambió) y se volvió a correr el verificador: la segunda salida fue idéntica, con código 0, y `sha256sum -c` no reportó diferencias. `CHECKSUMS.sha256` no se incluye a sí mismo. El ZIP excluye `.git`.

## 7. Ajustes posteriores a la entrega (1.1.1, 14 de octubre de 2026)

La primera ejecución del verificador en Windows, sobre una copia parcial del paquete dentro de un repositorio, reportó 59 errores. Tenían dos causas:

- **Copia incompleta.** Faltaban, entre otros, las imágenes de `diagrams/`, `documento/`, `CHANGELOG.md`, este registro y `references/`. Es el comportamiento esperado: el verificador exige el paquete completo.
- **Defecto del verificador en Windows.** Los checksums se calculaban con `str(ruta relativa)`, que en Windows usa `\`, y se comparaban con las rutas de `CHECKSUMS.sha256`, que usan `/`: todos los archivos de subcarpetas aparecían como no listados.

Se corrigieron las herramientas (ver `CHANGELOG.md`, versión 1.1.1) sin tocar la especificación. Se renderizaron de nuevo los 13 diagramas con `tools/render_diagrams.py --all` para crear el manifiesto y se reconstruyeron el DOCX y el PDF. El verificador corrido en Linux termina con 15 grupos OK y 0 errores (salida de la sección 6, actualizada). Las correcciones para Windows no se probaron en Windows en esta revisión: el entorno es Linux.

En la segunda corrida en Windows, con el paquete 1.1.1 completo, quedaron 2 errores: los ADR 0002, 0003 y 0004 tenían acentos en el nombre y el descompresor de Windows los extrajo con otro nombre (`as├¡ncrono`). En la versión 1.1.2 se renombraron sin acentos y el verificador rechaza nombres no ASCII.

## 8. Riesgos residuales

- **T-SQL no ejecutado.** Las reglas que esta versión movió del código a SQL (`TR_PrintAttempt_Guard`, `TR_ReprintAuthorization_Guard`, `TR_StationConfig_Guard`, `TR_PlcEpochHistory_Guard`, `TR_PrinterSendOrder_Monotonic` y el respaldo por modo en `TR_PrintJob_StateMachine`) solo se revisaron por lectura. La primera revisión independiente encontró dos defectos de ejecución en el borrador y la segunda, catorce defectos de lógica en las correcciones; las correcciones de esa segunda ronda no tuvieron revisión independiente. Puede haber otros hasta que T47 corra en SQL Server.
- **Costo de los triggers.** Las guardas consultan otras tablas en cada cambio de estado; su efecto en la latencia se mide en T12.
- **Huella en el EdgeHost.** Debe serializarse con JCS; un serializador JSON con otro escape convertiría duplicados auténticos en colisiones de identidad (T25).
- **Pausa tras la segunda comprobación y saltos de reloj pequeños.** No se pueden excluir por software; `SendMarginMs` debe cubrirlos con valores medidos.
- **Un solo transmisor por impresora.** En varios hosts depende de control físico del canal; sin él, el fencing token no protege.
- **Ventana física.** Sin compuerta del PLC o del aplicador, una etiqueta retenida en la cola del dispositivo puede salir tarde; el sistema solo la reporta como `UNKNOWN`.
- **Orden de aplicación.** El orden de envío por impresora no garantiza el orden de aplicación si el aplicador reordena (OT-01).
- **Reimpresión.** Sin `ManualPrinterId` configurada, una estación no admite reimpresiones excepcionales (OT-16).
- **Decisiones de OT abiertas** (OT-01 a OT-16): ningún parámetro físico se dio por resuelto.
