# Defectos y vacíos de la especificación

Clasificación de `CLAUDE.md`:

- **Clase A:** impide ejecutar algo sin cambiar ninguna regla (sintaxis, orden de objetos, un nombre o tipo inconsistente entre archivos). Se registra con la evidencia y el parche mínimo, se pide la aprobación de la edición en `spec/` y se aplica en un commit aparte `spec: …`.
- **Clase B:** cualquier cambio de comportamiento. Se registra y se detiene el trabajo afectado hasta que la persona decida. Nunca se rodea en el código.

| ID | Clase | Fase | Archivo | Estado |
| --- | --- | --- | --- | --- |
| SI-01 | A | 1 | `spec/tests/test-catalog.json` (T47) y `spec/tests/MATRIZ_PRUEBAS.md` | Abierto: pendiente de aprobación de la edición |

## SI-01 · Rango de códigos de error en el criterio de T47

- **Evidencia.** `spec/tests/test-catalog.json`, T47, `expectedStates`: "todo intento indebido falla con su error (51010 a 51032)". `spec/database/sqlserver_schema.sql` define `THROW` con los códigos 51001 a 51032 (por ejemplo 51001 en `TR_StationConfigRevision_Immutable`, 51004 y 51005 en `TR_PrintJob_Immutable`), y `spec/database/sqlserver_operations_reference.sql` usa 52001 en TX-X cuando la autorización no es ejecutable.
- **Impacto.** Ninguno sobre el comportamiento: el texto del criterio es más estrecho que el DDL. No impide ejecutar T47.
- **Qué se hace mientras tanto.** Las pruebas de T47 comprueban el código exacto que cada `THROW` del DDL asigna (51001 a 51032) y 52001 en TX-X, sin cambiar ninguna regla.
- **Parche mínimo propuesto.** En `spec/tests/test-catalog.json`, T47, `expectedStates`: "(51010 a 51032)" → "(51001 a 51032, o 52001 en TX-X)". Después, `py spec/tools/generate_artifacts.py` (regenera `tests/MATRIZ_PRUEBAS.md`), `py spec/tools/update_checksums.py` y `py spec/tools/verify_package.py`.
