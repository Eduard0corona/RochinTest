# Fuentes técnicas (consultadas el 2026-10-11 y el 2026-10-13)

Sustentan mecanismos y herramientas; no validan el PLC, la impresora ni la red de una instalación concreta. Los objetivos de rendimiento son hipótesis pendientes de benchmark.

| Fuente | Qué sustenta en el diseño |
| --- | --- |
| [OPC UA Parte 4, 5.13.1.2: intervalo de muestreo](https://reference.opcfoundation.org/specs/OPC-10000-4/5.13.1.2) | Los intervalos solicitados se negocian con el servidor |
| [OPC UA Parte 4, 5.13.1.5: parámetros de cola](https://reference.opcfoundation.org/specs/OPC-10000-4/5.13.1.5) | Cola mayor que 1 y bit de overflow al descartar valores |
| [OPC UA Parte 4, 5.14.1: modelo de suscripción](https://reference.opcfoundation.org/specs/OPC-10000-4/5.14.1) | Republish y keep-alive tras reconexión |
| [OPC Foundation UA .NET Standard](https://github.com/OPCFoundation/UA-.NETStandard) | Implementación de referencia con soporte para .NET 10 |
| [RabbitMQ: consumer acknowledgements y publisher confirms](https://www.rabbitmq.com/docs/confirms) | Entrega con reentregas posibles; consumidores idempotentes |
| [RabbitMQ: quorum queues](https://www.rabbitmq.com/docs/quorum-queues) | Colas replicadas para consumidores |
| [Política de soporte de .NET](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | .NET 10 es LTS con soporte hasta el 14 de noviembre de 2028 |
| [Ciclo de vida de .NET](https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core) | Fechas de inicio y fin de soporte de .NET 10 |
| [SQL Server: CREATE SEQUENCE](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-sequence-transact-sql) | Valores consumidos aunque haya rollback y perdidos con CACHE: unicidad sin consecutividad |
| [SQL Server: CREATE INDEX, predicado de filtro](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-index-transact-sql) | `IN` admitido en índices filtrados; sin `OR` ni columnas calculadas |
| [SQL Server: restricciones UNIQUE y CHECK](https://learn.microsoft.com/en-us/sql/relational-databases/tables/unique-constraints-and-check-constraints) | UNIQUE admite un solo NULL; CHECK acepta expresiones UNKNOWN y valida solo la fila |
| [SQL Server: SESSION_CONTEXT](https://learn.microsoft.com/en-us/sql/t-sql/functions/session-context-transact-sql) | Marca de transición excepcional leída por los triggers |
| [SQL Server: THROW](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/throw-transact-sql) | Rango de errores y terminación de la sentencia en los triggers |
| [RFC 9562, secciones 5.5 y 6.5](https://www.rfc-editor.org/rfc/rfc9562.html) | UUIDv5 determinista para el mismo namespace y nombre canónico |
| [Kleppmann, "How to do distributed locking"](https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html) | Un lease no protege ante pausas del proceso; el recurso debe validar el fencing token |
| [JSON Schema Draft 2020-12](https://json-schema.org/draft/2020-12/schema) | Dialecto de los contratos y reglas `if`/`then` |
