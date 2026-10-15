# ADR 0014: Máquina de estados canónica aplicada por la base de datos

**Estado:** Aceptada para el MVP con simuladores · **Fecha:** 2026-10-13 · **Versión del paquete:** 1.1

## Contexto
En la versión 1.0 los estados y transiciones estaban escritos a mano en el DDL, en cada diagrama, en los contratos y en el runbook, y no coincidían entre sí.

## Problema
Un agente de IA que implemente desde documentos contradictorios elegirá una versión al azar. Una regla de transición que solo vive en el código puede saltarse por error.

## Alternativas
1. Documentar mejor cada artefacto por separado: rechazada; diverge otra vez.
2. Fuente única en JSON, artefactos generados y triggers que consultan una tabla generada: elegida.
3. Solo una marca de sesión para autorizar transiciones excepcionales: rechazada tras la revisión independiente; cualquier sesión puede fijarla, así que la autorización debe estar en los datos.
4. Dejar `CONFIRMED` como terminal absoluto: rechazada; una evidencia posterior contradictoria quedaría sin camino y un mal etiquetado conocido seguiría publicado como confirmado.

## Decisión
`contracts/state-machine.json` define estados, motivos, transiciones, guardas, modos y prohibiciones. `tools/generate_artifacts.py` produce los diagramas de estados, las tablas del documento y `sqlserver_state_transitions.sql`, con `Kind` igual al modo (`AUTO`, `MANUAL`, `EXCEPTION`, `CORRECTION`). Los triggers `TR_*_StateMachine` rechazan cambios de estado que no estén en `AllowedTransition` o que no tengan el respaldo que exige su modo: una `OperatorAction` nueva para las manuales; un intento `EXCEPTIONAL_REPRINT` del job cuyo resultado es el destino para las excepcionales; una `EvidenceObservation` contradictoria para la corrección J18. `SESSION_CONTEXT` declara la intención y se limpia en el `CATCH`, pero no basta por sí solo. El trigger evalúa todo cambio de estado, de motivo, de la marca de reimpresión y de los punteros de respaldo, y exige respaldos recientes: el intento excepcional más reciente, una `OperatorAction` con identificador mayor que la anterior, una observación posterior al claim. Además, ningún estado que afirme que no hay etiqueta se alcanza si algún intento pudo imprimir. `tools/verify_package.py` comprueba DDL, schemas, ejemplos, cobertura de pruebas, que los estados terminales solo salgan por excepción o corrección y que la corrección solo lleve a `UNKNOWN`.

## Justificación
Una transición inválida falla en la base de datos aunque el código tenga un error.

## Consecuencias
Cambiar la máquina exige editar el JSON, regenerar y pasar el verificador.

## Riesgos
Los triggers añaden costo por actualización; se mide en T12. Los triggers no se ejecutaron en esta revisión (sin SQL Server disponible): la revisión independiente encontró en el borrador dos defectos que habrían fallado en el primer uso (error con 0 filas y `OUTPUT` sin `INTO`), así que su ejecución en T47 es la primera validación real.

## Pruebas
T23, T28, T34, T44, T47.

## Revisar si
El costo de los triggers afecta la ruta crítica; la alternativa es validar en procedimientos almacenados.
