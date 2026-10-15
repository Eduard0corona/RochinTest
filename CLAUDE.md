# CartonPrinting · instrucciones del proyecto

Implementación en .NET 10 (C#) del MVP de etiquetado industrial idempotente especificado en `spec/` (paquete 1.1.2): ciclo OPC UA → `CartonDetected` → `PrintJob` → `PrintJobResult`, sin imprimir dos etiquetas por un evento duplicado. En el MVP, simuladores reemplazan al PLC, la impresora, el aplicador, el escáner y el ERP. Nunca se conecta equipo real.

## Jerarquía de instrucciones

1. `spec/AI_CONTEXT.md`, importado al final de este archivo, manda sobre cualquier otra instrucción de implementación. Sus rutas son relativas a `spec/`: donde dice `contracts/state-machine.json`, el archivo es `spec/contracts/state-machine.json`.
2. Ante una contradicción entre documentos gana `spec/contracts/state-machine.json`. El conflicto se registra en `docs/SPEC_ISSUES.md`.
3. Este archivo adapta el paquete a este repositorio. `PROMPT_IMPLEMENTACION.md` define el trabajo de cada sesión.
4. Lo que diga este repositorio sobre avanzar de fase sustituye a la regla de `spec/prompts/INICIO_CODEX.md` "no avanzar a la Fase 1 hasta la revisión humana"; las demás reglas de ese archivo siguen vigentes.

## Estructura del repositorio

| Ruta | Contenido |
| --- | --- |
| `spec/` | Especificación 1.1.2, de solo lectura (ver abajo) |
| `CartonPrinting.sln` | Solución en la raíz (o `.slnx`, si el SDK la crea en ese formato) |
| `src/` | Proyectos de la sección 1 de `spec/docs/ARQUITECTURA_TECNICA.md` y `src/EdgeHost` |
| `src/CartonPrinting.Simulators/` | Simuladores de PLC (OPC UA), impresora, ERP y evidencia |
| `tests/` | Proyectos xUnit: unitarias, integración (Testcontainers), fallas y carga |
| `build/` | Scripts de CI y de trazabilidad |
| `docker-compose.yml` | SQL Server 2022 y RabbitMQ para desarrollo y pruebas |
| `docs/IMPLEMENTATION_LOG.md` | Decisiones, comandos ejecutados con su salida, pendientes y riesgos, por fase |
| `docs/TEST_STATUS.md` | Estado real de T01 a T47 con evidencia |
| `docs/SPEC_ISSUES.md` | Defectos o vacíos encontrados en `spec/` |
| `CHANGELOG.md` | Cambios de la implementación (el de `spec/` es de la especificación) |

## La especificación es de solo lectura

- `.claude/settings.json` tiene una regla `ask` sobre `Edit(/spec/**)`: cualquier edición en `spec/` pide aprobación a la persona, en cualquier modo de permisos. Es una barrera para las herramientas de edición, no para scripts: no ejecutes nada que escriba en `spec/` salvo en el procedimiento de defectos de clase A.
- No copies el DDL, los schemas ni los ejemplos dentro de `src/`: se leen desde `spec/` en tiempo de prueba y de migración.
- `spec/tests/test-catalog.json` no se edita para registrar resultados: el estado real va en `docs/TEST_STATUS.md`.

Defectos de la especificación:

- **Clase A**: impide ejecutar algo sin cambiar ninguna regla (sintaxis T-SQL, orden de creación de objetos, un nombre o tipo inconsistente entre dos archivos). Se registra en `docs/SPEC_ISSUES.md` con la evidencia y el parche mínimo; se pide la aprobación de la edición; se aplica en un commit aparte `spec: …` y después se ejecutan `py spec/tools/generate_artifacts.py`, `py spec/tools/build_examples.py` si cambiaron contratos, `py spec/tools/update_checksums.py` y `py spec/tools/verify_package.py`.
- **Clase B**: cualquier cambio de comportamiento (máquina de estados, contratos, invariantes, garantías, transacciones). Se registra y se detiene el trabajo afectado hasta que la persona decida. Nunca se rodea un defecto en el código.

## Comandos

Funcionan en PowerShell y en Git Bash (barras `/` en las rutas). En Windows, Python se invoca con `py`.

```bash
py spec/tools/verify_package.py                 # integridad de la especificación: 15 grupos OK, 0 errores
py spec/tools/generate_artifacts.py --check     # artefactos derivados al día
docker compose up -d                            # SQL Server y RabbitMQ locales
dotnet build -warnaserror                       # desde la raíz
dotnet test --filter "Category!=Load"           # Testcontainers levanta su propio SQL Server y RabbitMQ
py build/check_traceability.py                  # cada prueba Txx de las fases cerradas tiene pruebas xUnit
```

## Reglas técnicas que más fallan

El detalle está en `spec/`; esta lista resume los errores que las dos revisiones independientes encontraron o que el diseño previene:

1. El DDL trae separadores `GO`, que SqlClient no entiende: el ejecutor de migraciones divide los lotes en las líneas que solo contienen `GO`.
2. Después de un DML sobre una tabla con triggers se usa `OUTPUT … INTO` una variable de tabla; nunca `OUTPUT` sin `INTO` (Msg 334) ni `@@ROWCOUNT`.
3. Toda transacción que fija `SESSION_CONTEXT N'TransitionKind'` lo limpia también en el `CATCH`. No se confía en el reseteo del pool de conexiones.
4. El serial se obtiene (`NEXT VALUE FOR`) antes de TX-A; el payload se arma con él y `PayloadHash` es SHA-256 de sus bytes UTF-8.
5. TX-A inserta primero. Solo una violación (2627, 2601) de `UQ_CartonDetection_Identity`, `UQ_CartonDetection_IdempotencyKey` o `PK_CartonDetection` es "clave existente"; se reconoce por el nombre de la restricción en el mensaje, que aparece en cualquier idioma del servidor. Otra violación es un defecto. El error 1205 se reintenta.
6. La huella se serializa con JCS (RFC 8785). `System.Text.Json` por defecto escapa `+ & < > '` y produce otra huella. Se prueba contra `spec/contracts/examples/`.
7. UUIDv5: SHA-1 de namespace y nombre UTF-8, con versión y variante según RFC 9562. Los bytes son big-endian: `new Guid(bytes, bigEndian: true)`. Se prueba contra los `eventId` de los ejemplos.
8. Todo instante se trunca a milisegundos antes de usarlo en la huella y antes de enviarlo a SQL. `DATETIME2(3)` redondea, y lo almacenado dejaría de coincidir con la huella.
9. `CycleId` es `ulong` en C#, `DECIMAL(20,0)` en SQL Server y cadena decimal canónica en JSON.
10. Reloj: `TimeProvider` inyectado para el UTC del EdgeHost y para el tiempo monotónico (`GetTimestamp`). Nunca `SYSUTCDATETIME()` para decidir un plazo. Se vigila la divergencia entre ambos relojes (`CLOCK_JUMP`).
11. OPC UA: solo `StatusCode` Good; la primera notificación de un monitored item creado o transferido se registra como `READ`; el bit Overflow va en `QueueOverflow`; lo reenviado por Republish, como `REPUBLISH`.
12. `NOT_SENT` solo si el sender nunca invocó la escritura; cualquier excepción después de invocarla es `UNKNOWN`. Ninguna transacción SQL queda abierta durante I/O con la impresora, el PLC o el ERP.
13. Cada impresora de línea tiene su fila en `PrinterSendOrder`; si falta, su sender no arranca. Un ciclo atrasado vence con `OUT_OF_ORDER`.
14. Toda evidencia se inserta en `EvidenceObservation` en su propia transacción antes de aplicarse.
15. Las reimpresiones van solo a la `ManualPrinterId` y las ejecuta el sender de esa impresora con su lease. Se confirman solo con evidencia que referencia al intento. X03 va en la misma transacción que deja el intento excepcional en `UNKNOWN`.
16. Toda acción manual inserta su `OperatorAction`, incluidos el desbloqueo de estación y la confirmación de época.
17. Los triggers no se deshabilitan. El usuario de la aplicación no tiene permisos de DDL ni de escritura sobre `AllowedTransition`; las migraciones usan otro login.

## Evidencia y honestidad

- Una prueba solo cuenta como pasada si se ejecutó: comando, salida y fecha en `docs/IMPLEMENTATION_LOG.md`, y su estado en `docs/TEST_STATUS.md`.
- Toda prueba Txx de la especificación tiene al menos una prueba xUnit con `[Trait("Spec", "Txx")]`.
- Lo que depende de hardware se prueba contra simuladores y se declara así. Ninguna garantía física (aplicación, ventana física, etiqueta en la caja correcta) se da por demostrada.
- Texto en español; tipos, estados, eventos, métricas y columnas en inglés, como en `spec/`.
- No se hace `git push` sin que la persona lo pida.

@spec/AI_CONTEXT.md
