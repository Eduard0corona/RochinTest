# ADR 0003: SENDING antes del I/O, TRANSMITTED distinto de CONFIRMED y UNKNOWN sin reenvío

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
La impresora es un efecto físico externo: no participa en la transacción SQL y su respuesta puede perderse. La versión 1.0 tenía un solo estado `SENDING` que abarcaba desde el claim hasta la evidencia.

## Problema
Un reintento a ciegas tras un timeout es la forma más directa de imprimir dos etiquetas. A la vez, confundir "bytes entregados" con "etiqueta impresa" haría pasar como éxito una etiqueta que nunca se aplicó.

## Alternativas
1. Reintentar ante timeout: rechazada; duplica.
2. Persistir el intento después de escribir: rechazada; un fallo entre escritura y registro deja una impresión sin rastro.
3. Persistir `SENDING` antes del I/O, separar `TRANSMITTED` y tratar toda duda como `UNKNOWN`: elegida.
4. Persistir además una marca justo antes de invocar la escritura para que SQL distinga `NOT_SENT`: descartada; añade un viaje a SQL en la ruta crítica y un corte entre esa marca y la escritura seguiría siendo `UNKNOWN`.

## Decisión
TX-B confirma el claim y un `PrintAttempt` con `MayHaveSent = 1` antes de abrir el socket; SQL admite un solo intento ordinario con `MayHaveSent = 1` por job (`UX_PrintAttempt_OrdinarySent`) y solo con el job en `SENDING`. `NOT_SENT` solo si la escritura nunca se invocó: es una regla de código, porque `WriteStartedAt` se persiste después de escribir y no prueba nada en SQL. La escritura completa lleva a `TRANSMITTED`; solo la evidencia lleva a `CONFIRMED`. Cualquier error después de invocar la escritura, o un `SENDING` encontrado al arrancar, lleva a `UNKNOWN`, que bloquea todo reenvío ordinario.

## Justificación
La única afirmación segura tras un error es "pudo haberse impreso"; el diseño la convierte en un estado explícito con salidas controladas.

## Consecuencias
Un proceso que muere entre el commit del claim y la escritura deja un falso `UNKNOWN`: se acepta como costo para no duplicar. Las cajas `UNKNOWN` se retienen o se inspeccionan según OT.

## Riesgos
Disponibilidad reducida si los `UNKNOWN` son frecuentes; se vigila `printer_unknown_total`.

## Pruebas
T05, T06, T07, T38, T47.

## Revisar si
La impresora ofrece deduplicación por identificador de trabajo, lo que permitiría reenviar con seguridad.
