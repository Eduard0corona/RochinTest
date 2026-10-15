# ADR 0010: Propietario único de impresora y límites del lease

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
La impresora acepta conexiones TCP de cualquier cliente de la red y no valida tokens. La versión 1.0 declaraba un dueño único sin mecanismo, y la revisión 1 proponía un fencing token en SQL.

## Problema
Un proceso que validó su token y se suspende puede reanudar y transmitir después de que otro tomó la impresora. SQL no puede rechazar esa escritura porque no está en su camino.

## Alternativas
1. Lease con fencing token solo en SQL: insuficiente para el canal físico.
2. Alta disponibilidad automática con lease: rechazada.
3. Un dueño efectivo en el MVP y, para alta disponibilidad, aislamiento del camino físico: elegida.

## Decisión
Un sender por impresora dentro del EdgeHost, un lock de sistema operativo por impresora y `PrinterLease` para detectar una segunda instancia y fencear los claims. El mismo sender ejecuta las reimpresiones excepcionales de su impresora (TX-X valida su lease): ningún otro componente, ni la Operations API, abre una conexión a una impresora. El dueño nuevo pasa a `UNKNOWN` los `SENDING` heredados y vacía la cola del dispositivo si puede. La alta disponibilidad solo con aislamiento de red, apagado verificado del nodo anterior o un proxy que valide tokens.

## Justificación
El token protege lo que el recurso valida; aquí el único recurso que lo haría es el camino de red o un componente delante de la impresora.

## Consecuencias
Sin alta disponibilidad automática en el MVP; la conmutación es manual y está en el runbook.

## Riesgos
Una segunda instancia arrancada por error en otro host, sin aislamiento de red, puede transmitir; se detecta con `printer_owner_conflicts_total`.

## Pruebas
T27, T36, T43.

## Revisar si
OT exige alta disponibilidad (OT-11) o la impresora admite una sola conexión con prioridad configurable.
