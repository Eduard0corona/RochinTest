# ADR 0005: Un proceso edge y presupuesto de latencia como hipótesis

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
El caso pide comunicación en el orden de milisegundos. La versión 1.0 repartía la ruta en cinco procesos más un journal y proponía un objetivo hasta `READY` que no incluía el envío a la impresora.

## Problema
Cada salto entre procesos o capas durables suma latencia y modos de falla. Un objetivo que no llega a la impresora no dice si la caja alcanza a recibir su etiqueta.

## Alternativas
1. Microservicios comunicados por el broker: rechazada para la ruta crítica.
2. Un proceso edge con módulos separados y señal en proceso: elegida.
3. Prometer un p99 fijo: rechazada sin benchmark.

## Decisión
`src/EdgeHost` aloja los módulos como hosted services; `System.Threading.Channels` señaliza en proceso; SQL Server local es la única escritura durable de la ruta. Las métricas separan cada tramo (sección 10.4) y la condición exigible es la desigualdad de factibilidad con percentiles medidos.

## Justificación
Menos saltos y una sola transacción por caja; los objetivos quedan atados a la física de la línea, no a cifras nominales.

## Consecuencias
Un fallo del proceso detiene todos los módulos de la estación; el PLC lo detecta por el latido.

## Riesgos
Pausas del recolector de .NET en la ruta crítica; se miden y se cubren con `SendMarginMs`.

## Pruebas
T12, T29.

## Revisar si
La medición muestra que la desigualdad no se cumple, o que un módulo necesita escalar por separado.
