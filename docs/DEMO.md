# Demo del flujo completo

Este guion hace pasar cajas por el EdgeHost real, en su propio proceso, contra simuladores. El recorrido es: ciclo OPC UA → `CartonDetected` → snapshot del ERP → `PrintJob` → envío a la impresora → evidencia → `PrintJobResult`. Nunca se conecta equipo real.

`tests/CartonPrinting.DemoHost` monta el escenario y lanza el EdgeHost:

1. Siembra una estación nueva en la base: revisión, época 1 y la fila de `PrinterSendOrder`.
2. Arranca los simuladores:
   - el PLC, como servidor OPC UA;
   - la impresora, por TCP;
   - el ERP, por HTTP, con la orden `WO-DEMO-0001`.
3. Hace de integración de evidencia. La impresora (PRINTED), el aplicador (APPLIED) y el escáner (VERIFIED) simulados informan por TX-D, porque el EdgeHost no tiene una entrada de evidencia propia.
4. Lanza el EdgeHost configurado solo con variables de entorno, espera `LabelingReady` y hace pasar las cajas.
5. Muestra los jobs por estado, las etiquetas recibidas y los eventos del Outbox. Sale con 0 si todas las cajas quedaron `CONFIRMED`, con nivel VERIFIED y una sola etiqueta cada una.

## Requisitos

- .NET SDK 10 y Docker.
- La base aprovisionada y migrada: pasos 1 a 4 de "Entorno local con Docker Compose" en el `README.md`.

## Pasos

1. Compilar la solución desde la raíz. Compila también el EdgeHost, que la demo lanza:

```bash
dotnet build
```

2. Ejecutar la demo con el login de la aplicación (`cp_app`). La contraseña es la de `Database__AppPassword` del aprovisionamiento y no va al repositorio:

```bash
dotnet tests/CartonPrinting.DemoHost/bin/Debug/net10.0/CartonPrinting.DemoHost.dll --connection "Server=127.0.0.1,14333;Database=CartonPrinting;User ID=cp_app;Password=<la del aprovisionamiento>;TrustServerCertificate=true" --boxes 30 --interval-ms 1000
```

Sin `--connection`, la demo usa la variable `ConnectionStrings__CartonPrinting`. Otras opciones:

- `--timeout-s`: espera máxima al final (120 por defecto);
- `--edgehost`: ruta de `CartonPrinting.EdgeHost.dll`, si no está en la de la compilación.

3. Opcional: ver los eventos publicados en RabbitMQ. Se define `RabbitMq__Uri` en el entorno antes de lanzar la demo, que lo pasa al EdgeHost. Por ejemplo, `amqp://<usuario>:<contraseña>@127.0.0.1:5672/`, con las credenciales del `.env`. Los mensajes del exchange `carton.events` se ven en la consola de gestión, `http://127.0.0.1:15672`. Con `amqps://` la conexión usa TLS (`RabbitMq__Tls__*`).

## Qué se ve

Una corrida de diez cajas termina así (la ejecuta `DemoTests` en la compuerta):

```
Estación PLANT-01/DEMO-130300: 10 cajas, una cada 500 ms.
LabelingReady: el EdgeHost está listo. Pasan las cajas.
Jobs por estado: CONFIRMED=10
Etiquetas recibidas por la impresora: 10 (10 seriales distintos)
Eventos en el Outbox de la estación: 20
DEMO OK: todas las cajas CONFIRMED (VERIFIED), una etiqueta por caja.
```

Los 20 eventos son el `CartonDetected` y el `PrintJobResult` CONFIRMED de cada caja. Las líneas `[edgehost]` son el registro del EdgeHost; con `Logging__LogLevel__Default=Warning`, solo avisos y errores.

## Qué no demuestra

- **Tiempos físicos.** Impresión, aplicación y lectura son instantáneas en los simuladores. Los tiempos medidos están en `docs/BENCHMARK.md` y tampoco validan tiempos físicos.
- **La seguridad de planta.** La demo conecta el PLC simulado sin cifrado (`Ingest__Opc__SecurityMode=None`) y acepta su certificado. En planta se usan los valores por defecto de `OpcIngestOptions`: SignAndEncrypt con validación estricta y sin autoaceptación. Eso lo prueba `OpcSecurityTests` contra el PLC simulado.
- **Las garantías físicas.** La etiqueta en la caja correcta, la ventana de aplicación y una etiqueta por caja física solo se aceptan en planta (`docs/FAT_CHECKLIST.md`).
