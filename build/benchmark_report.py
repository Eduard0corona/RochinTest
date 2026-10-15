#!/usr/bin/env python3
"""Genera docs/BENCHMARK.md desde el reporte JSON de T12 (tests/CartonPrinting.LoadTests).

La prueba de carga escribe el reporte en CARTONPRINTING_BENCHMARK_OUT (o junto a su ensamblado, benchmark-t12.json).
Este script lo convierte en el documento con el hardware, los intervalos OPC negociados, la tasa y la duración, los
percentiles de cada tramo de la sección 10.4 del documento principal, el backlog y la desigualdad de factibilidad.

Uso: py build/benchmark_report.py <benchmark-t12.json> [--fecha "AAAA-MM-DD HH:MM"] [--comando "..."] > docs/BENCHMARK.md
"""
from __future__ import annotations

import argparse
import json
import sys

def cell(value) -> str:
    """Texto de una celda de tabla Markdown: sin '|' ni saltos de línea que partan la fila."""
    return " ".join(str(value).replace("|", "/").split())


SEGMENTS = [
    ("plc_to_opc_receive_ms", "Flanco del PLC → notificación OPC recibida", "Relojes del PLC y del EdgeHost (la misma máquina en el simulador)"),
    ("opc_receive_to_job_ready_ms", "Notificación → commit de TX-A (job READY)", "Monotónico"),
    ("opc_receive_to_send_start_ms", "Notificación → invocación de la escritura", "Monotónico"),
    ("send_start_to_device_accept_ms", "Invocación de la escritura → acuse del dispositivo", "Monotónico"),
    ("presend_check_to_write_ms", "Segunda comprobación → invocación de la escritura", "Monotónico"),
    ("print_deadline_slack_ms", "Holgura: invocación de la escritura → plazo efectivo", "Monotónico"),
    ("send_to_printed_ms", "Escritura → evidencia PRINTED", "Instantes de la evidencia (simulador)"),
    ("printed_to_applied_ms", "PRINTED → APPLIED", "Instantes de la evidencia (simulador)"),
    ("applied_to_verified_ms", "APPLIED → VERIFIED", "Instantes de la evidencia (simulador)"),
    ("send_start_to_applied_ms", "Invocación de la escritura → APPLIED (su p99 es DeviceLeadTimeMs)", "Instantes de la evidencia (simulador)"),
    ("send_to_verified_ms", "Escritura → VERIFIED", "Instantes de la evidencia (simulador)"),
]


def fmt(value: object) -> str:
    if value is None:
        return "—"
    if isinstance(value, float):
        return f"{value:.1f}".rstrip("0").rstrip(".") if value != int(value) else f"{int(value)}"
    return str(value)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("report")
    parser.add_argument("--fecha", default="")
    parser.add_argument("--comando", default='dotnet test --filter "Category=Load"')
    args = parser.parse_args()
    r = json.load(open(args.report, encoding="utf-8"))
    env, opc, feas = r["environment"], r["opc"], r["feasibility"]
    comp, errors = r.get("composition", {}), r.get("errors")

    out = []
    w = out.append
    w("# Benchmark de T12 · carga sostenida y capacidad")
    w("")
    w("> **Hipótesis declarada, no aceptación en planta.** La tasa y el payload no están acordados con OT (OT-02, OT-03). Se usó la")
    w("> hipótesis que decidió la persona: una caja por segundo con el payload del simulador, 30 minutos a esa tasa y 10 minutos al")
    w("> doble. Impresión, aplicación y lectura son del simulador: **este reporte no valida tiempos físicos.** Se repite en planta con")
    w("> el hardware, el PLC y la impresora reales (`docs/FAT_CHECKLIST.md`, G11 y G12).")
    w("")
    w("## Entorno")
    w("")
    w("| Elemento | Valor |")
    w("| --- | --- |")
    w(f"| Máquina | {env['machine']} ({env['os']}) |")
    w(f"| CPU | {env['cpu']}, {env['logicalProcessors']} procesadores lógicos |")
    w(f"| Memoria disponible para .NET | {fmt(env['memoryGiB'])} GiB |")
    w(f"| Runtime | {env['dotnet']} |")
    w(f"| SQL Server | {env['sqlServer']}, en un contenedor de Testcontainers en la misma máquina |")
    if comp.get("rabbitMq"):
        w(f"| RabbitMQ | {comp['rabbitMq']} |")
    w("| Red | Local (loopback): PLC simulado, impresora simulada, SQL Server y RabbitMQ en la misma máquina |")
    w(f"| Payload de la etiqueta | {env['payloadBytes']} bytes (plantilla del simulador) |")
    parts = [f"ingesta OPC UA real ({opc['sessionSecurity']})" if opc.get("sessionSecurity") else "ingesta OPC UA real",
             "señal en proceso hacia el sender (sondeo de respaldo de 50 ms)", "socket persistente con la impresora"]
    if comp.get("sweepIntervalMs"):
        parts.append(f"barrido TX-S cada {comp['sweepIntervalMs']} ms")
    if comp.get("outboxRelay"):
        parts.append("Outbox Relay con publisher confirms hacia una cola quorum sin consumidor")
    if comp.get("stallWatchIntervalMs"):
        parts.append(f"diagnóstico: las esperas de SQL Server se muestrean cada {comp['stallWatchIntervalMs']} ms desde el proceso de prueba, "
                     "con el login de administración")
    w(f"| Composición | La del EdgeHost, en un proceso de prueba: {'; '.join(parts)} |")
    w("| Evidencia | Impresora (PRINTED), aplicador (APPLIED) y escáner (VERIFIED) simulados, aplicada por TX-D; nivel mínimo VERIFIED |")
    w(f"| Comando | `{args.comando}` |")
    if args.fecha:
        w(f"| Fecha | {args.fecha} |")
    w("")
    w("## Intervalos OPC UA negociados")
    w("")
    w("| Intervalo | Pedido | Negociado por el servidor |")
    w("| --- | --- | --- |")
    w(f"| Muestreo | {fmt(opc['requestedSamplingIntervalMs'])} ms | {fmt(opc['revisedSamplingIntervalMs'])} ms |")
    w(f"| Publicación | {fmt(opc['requestedPublishingIntervalMs'])} ms | {fmt(opc['revisedPublishingIntervalMs'])} ms |")
    w(f"| Cola del monitored item | — | {fmt(opc['revisedQueueSize'])} |")
    w("")
    w("El servidor es el PLC simulado: el PLC real negociará sus propios valores, y el primer tramo depende de ellos.")
    w("")
    w(f"## Carga")
    w("")
    w(f"Cajas en total: {r['boxes']}, incluidas {r.get('warmUpBoxes', 0)} de calentamiento fuera de la muestra (conexión con la impresora, código y planes de SQL en frío).")
    w("")
    w("| Fase | Cajas por segundo | Minutos | Cajas | Backlog máximo | Backlog p95 | Backlog máximo en el último minuto | Pendiente más antiguo (máx.) |")
    w("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for ph in r["phases"]:
        w(f"| {ph['phase']} | {fmt(ph['boxesPerSecond'])} | {fmt(ph['minutes'])} | {ph['boxes']} | {ph['backlogMax']} | {fmt(ph['backlogP95'])} | "
          f"{ph['backlogLastMinuteMax']} | {fmt(ph['oldestPendingMsMax'])} ms |")
    w("")
    w("El backlog es la cantidad de jobs `READY`, `RETRY_WAIT` o `SENDING`, muestreada cada segundo. Un job pasa por esos estados en")
    w("decenas de milisegundos (tramo `opc_receive_to_send_start_ms`), así que una muestra en cero no significa que no hubiera jobs")
    w("pendientes, sino que la cola no se acumula de una muestra a la siguiente. Lo confirman el máximo de ese tramo y la holgura del plazo.")
    statuses = errors.get("jobsByStatus", {}) if errors else {}
    if statuses and set(statuses) == {"CONFIRMED"} and not errors.get("notConfirmed"):
        w("Todas las cajas terminaron `CONFIRMED`, ninguna vencida.")
    elif statuses:
        pending = sum(v for k, v in statuses.items() if k != "CONFIRMED")
        w(f"**{pending} jobs no terminaron `CONFIRMED`** (ver \"Errores y escenarios excluidos\").")
    w("")
    for ph in r["phases"]:
        w(f"## Tramos · {ph['phase']}")
        w("")
        w("| Métrica | Tramo | Reloj | n | p50 (ms) | p95 (ms) | p99 (ms) | máx. (ms) |")
        w("| --- | --- | --- | --- | --- | --- | --- | --- |")
        for name, label, clock in SEGMENTS:
            s = ph["segments"].get(name, {"n": 0})
            w(f"| `{name}` | {label} | {clock} | {s['n']} | {fmt(s.get('p50'))} | {fmt(s.get('p95'))} | {fmt(s.get('p99'))} | {fmt(s.get('max'))} |")
        w("")
    w("## Desigualdad de factibilidad")
    w("")
    w(f"Referencia del plazo: `{feas['deadlineReference']}` (sección 17 del documento principal), con los p99 de toda la corrida sin el calentamiento:")
    w("")
    w("> p99(`plc_to_opc_receive_ms`) + p99(`opc_receive_to_send_start_ms`) + `SendMarginMs` + `ClockSkewToleranceMs` + `DeviceLeadTimeMs` < `TransitBudgetMs`")
    w("")
    w("| Término | Valor (ms) |")
    w("| --- | --- |")
    w(f"| p99(`plc_to_opc_receive_ms`) | {fmt(feas['p99PlcToOpcReceiveMs'])} |")
    w(f"| p99(`opc_receive_to_send_start_ms`) | {fmt(feas['p99OpcReceiveToSendStartMs'])} |")
    w(f"| `SendMarginMs` (revisión) | {fmt(feas['sendMarginMs'])} |")
    w(f"| `ClockSkewToleranceMs` (revisión) | {fmt(feas['clockSkewToleranceMs'])} |")
    w(f"| `DeviceLeadTimeMs`: p99 medido de `send_start_to_applied_ms` | {fmt(feas['deviceLeadTimeMsMeasuredP99'])} |")
    w(f"| **Lado izquierdo** | **{fmt(feas['leftSideMs'])}** |")
    w(f"| `TransitBudgetMs` (revisión) | {fmt(feas['transitBudgetMs'])} |")
    w(f"| **¿Se cumple?** | **{'Sí' if feas['holds'] else 'No'}** |")
    w("")
    w("Condiciones relacionadas:")
    w("")
    w(f"- `SendMarginMs` ({fmt(feas['sendMarginMs'])}) ≥ p99(`presend_check_to_write_ms`) ({fmt(feas['p99PresendCheckToWriteMs'])}) + `ClockJumpToleranceMs` ({fmt(feas['clockJumpToleranceMs'])}): "
      f"**{'se cumple' if feas['sendMarginCoversPresendP99PlusClockJump'] else 'no se cumple'}**.")
    w(f"- `DeviceLeadTimeMs` configurado ({fmt(feas['deviceLeadTimeMsConfigured'])}) ≥ p99 medido ({fmt(feas['deviceLeadTimeMsMeasuredP99'])}): "
      f"**{'se cumple' if (feas['deviceLeadTimeMsConfigured'] or 0) >= feas['deviceLeadTimeMsMeasuredP99'] else 'no se cumple'}**.")
    w("")
    w("Los valores de la revisión son de laboratorio (OT-03 abierta). En planta, `TransitBudgetMs` y `DeviceLeadTimeMs` salen de la línea")
    w("real, y la desigualdad se evalúa con los p99 medidos allí.")
    w("")
    w("## Errores y escenarios excluidos")
    w("")
    if errors:
        jobs = ", ".join(f"{k} {v}" for k, v in sorted(errors["jobsByStatus"].items()))
        detections = ", ".join(f"{k} {v}" for k, v in sorted(errors["detectionsByStatus"].items()))
        faults = "; ".join(errors["serviceFaults"]) or "ninguna"
        w("| Error | Cantidad |")
        w("| --- | --- |")
        w(f"| Jobs por estado final | {cell(jobs)} |")
        w(f"| Detecciones por estado | {cell(detections)} |")
        rejected = errors["opcRecordsRejected"]
        rejected = ", ".join(f"{v} {k}" for k, v in sorted(rejected.items())) if isinstance(rejected, dict) else str(rejected)
        w(f"| Registros OPC rechazados (`opc_records_rejected_total`; el valor inicial vacío del registro es `EMPTY`) | {rejected or '0'} |")
        w(f"| Notificaciones perdidas por canal lleno | {errors['opcChannelFull']} |")
        w(f"| Rechazos de la segunda comprobación | {errors['presendCheckRejected']} |")
        w(f"| Intentos NOT_SENT | {errors['attemptsNotSent']} |")
        w(f"| Intentos UNKNOWN | {errors['attemptsUnknown']} |")
        w(f"| Eventos del Outbox: escritos, publicados, pendientes al final | {errors['outboxEvents']}, {errors['outboxPublished']}, {errors['outboxPendingAtEnd']} |")
        w(f"| Servicios de fondo que fallaron (sender, barrido) | {faults} |")
        if "senderPauses" in errors:
            w(f"| Pausas del sender (tres fallos seguidos del dispositivo) | {errors['senderPauses']} |")
            w(f"| Reintentos de TX-A con SQL Server sin responder | {errors['ingestSqlRetries']} |")
        if "sqlOutageDeclarations" in errors:
            w(f"| Caídas de SQL Server declaradas | {errors['sqlOutageDeclarations']} |")
            w(f"| Transacciones reintentadas por un 1205 | {errors['deadlockRetries']} |")
            w(f"| Errores del sender fuera de la escritura; intentos SENDING cerrados por TX-R0 | {errors['senderErrors']}; {errors['recoveredSending']} |")
        for item in errors.get("notConfirmed", []):
            w(f"| Job sin CONFIRMED | {cell(item)} |")
        if "cyclesWithoutDetection" in errors:
            w(f"| Ciclos publicados sin detección | {errors['cyclesWithoutDetection']} |")
        for item in errors.get("detectionsWithoutJob", []):
            w(f"| Detección sin job | {cell(item)} |")
        for item in errors.get("sequenceGaps", []):
            w(f"| Hueco de secuencia | {cell(item)} |")
        for item in errors.get("deadlocks", []):
            w(f"| Interbloqueo (system_health) | {cell(item)} |")
        for item in errors.get("backlogPeaks", []):
            w(f"| Pico de backlog | {cell(item)} |")
        if errors.get("slowestToWrite"):
            w(f"| Cajas más lentas (notificación a escritura) | {cell('; '.join(errors['slowestToWrite'][:5]))} |")
        stalls = errors.get("stalls", [])
        for item in stalls:
            w(f"| Espera larga o pausa observada | {cell(item)} |")
        w("")
    w("Escenarios excluidos de esta medición:")
    w("")
    w("- **Fallas.** Sin caídas de SQL Server, RabbitMQ, la impresora o el PLC: esas son T08, T19, T05 a T07 y T11, que no miden capacidad.")
    w("- **ERP.** El snapshot de la orden está en caché durante toda la corrida: la sincronización con el ERP no participa y ningún job espera snapshot.")
    w("- **Operación.** Sin acciones de la Operations API ni reimpresiones; una sola estación y una sola impresora de línea.")
    w("- **Propiedad.** El lease de la impresora se renueva una vez por minuto desde la prueba, no con el ciclo de propiedad del EdgeHost.")
    w("- **Consumidor.** La cola recibe los eventos pero nadie los consume: el costo del consumidor (MES) queda fuera.")
    w("- **Hardware.** Impresión, aplicación y lectura son del simulador, en la misma máquina, sin tiempos físicos.")
    w("")
    print("\n".join(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())
