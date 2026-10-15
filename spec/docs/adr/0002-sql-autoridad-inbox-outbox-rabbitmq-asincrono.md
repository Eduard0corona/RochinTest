# ADR 0002: SQL Server como autoridad; Outbox, Inbox y RabbitMQ asíncrono

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
Varios consumidores (MES, ERP, tableros) necesitan los eventos, y la red de planta puede perder el broker sin perder la línea. La versión 1.0 ya proponía Outbox e Inbox, pero no dejaba claro que el broker estuviera fuera de la ruta de impresión.

## Problema
Publicar al broker y escribir en SQL son dos operaciones independientes: si una falla, los consumidores ven un estado distinto del real. Además, RabbitMQ entrega al menos una vez y un consumidor puede recibir un mensaje repetido.

## Alternativas
1. Transacción distribuida (XA o 2PC) entre SQL y el broker: rechazada por complejidad y disponibilidad.
2. El broker como fuente de verdad del flujo: rechazada; su caída detendría la impresión.
3. Outbox en la misma transacción del cambio, relay con publisher confirms e Inbox en cada consumidor: elegida.

## Decisión
SQL Server es la única autoridad transaccional. Cada transición publicable escribe su evento en `OutboxEvent` dentro de la misma transacción. El relay marca `PublishedAt` solo después del publisher confirm y republica con el mismo `EventId` si la confirmación se pierde. Cada consumidor registra `(ConsumerName, EventId)` en `InboxMessage` en la transacción de su efecto. La impresión nunca espera al broker.

## Justificación
El efecto lógico queda exactamente una vez aunque la entrega sea al menos una vez; la caída del broker solo retrasa la publicación.

## Consecuencias
Publicación con retraso posible; el orden entre agregados distintos es de mejor esfuerzo y los consumidores usan `resultVersion`.

## Riesgos
Crecimiento del Outbox durante una caída larga; se vigila `outbox_pending_age_seconds`.

## Pruebas
T08, T14.

## Revisar si
La planta exige otro broker como estándar (MQTT con Sparkplug B o Kafka): cambia el relay, no el patrón.
