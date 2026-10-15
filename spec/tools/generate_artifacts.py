#!/usr/bin/env python3
"""Genera artefactos derivados desde las fuentes canónicas del paquete.

Fuentes:
  contracts/state-machine.json   estados y transiciones
  contracts/invariants.json      invariantes
  tests/test-catalog.json        pruebas
  docs/guarantees.json           matriz de garantías
  docs/open-decisions.json       decisiones abiertas OT

Salidas (archivos completos):
  diagrams/03*_estados_*.mmd
  database/sqlserver_state_transitions.sql
  tests/MATRIZ_PRUEBAS.md
Salidas (bloques dentro de Markdown, entre marcadores GENERATED):
  state-tables, invariants, guarantees, open-decisions, tests-summary

Uso:
  python3 tools/generate_artifacts.py          # escribe
  python3 tools/generate_artifacts.py --check  # no escribe; termina en 1 si algo está desactualizado
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

BLOCK_RE = re.compile(
    r"(<!-- GENERATED:BEGIN (?P<name>[a-z0-9-]+) -->\n)(?P<body>.*?)(<!-- GENERATED:END (?P=name) -->)",
    re.S,
)

# (archivo, máquina, filtro de modos). La máquina PrintJob se dibuja en dos vistas:
# flujo ordinario (auto, manual y corrección) y procedimiento excepcional.
STATE_DIAGRAMS = [
    ("03a_estados_printjob.mmd", "PrintJob", ("auto", "manual", "correction")),
    ("03b_estados_printjob_excepciones.mmd", "PrintJob", ("exception",)),
    ("03c_estados_intento.mmd", "PrintAttempt", ("auto", "manual", "exception", "correction")),
    ("03d_estados_deteccion.mmd", "CartonDetection", ("auto", "manual", "exception", "correction")),
    ("03e_estados_hueco.mmd", "SequenceGap", ("auto", "manual", "exception", "correction")),
    ("03f_estados_autorizacion.mmd", "ReprintAuthorization", ("auto", "manual", "exception", "correction")),
]

# Dirección por vista; la vista excepcional se lee mejor de arriba abajo (autoarcos de X02 y X03).
DIRECTION = {"PrintJob:exception": "TB"}

MODE_ES = {"auto": "automática", "manual": "manual", "exception": "excepcional", "correction": "corrección"}
MODE_PREFIX = {"auto": "", "manual": "[manual] ", "exception": "[excepción] ", "correction": "[corrección] "}
# Valor de AllowedTransition.Kind por modo: los triggers exigen un respaldo distinto en cada uno.
MODE_KIND = {"auto": "AUTO", "manual": "MANUAL", "exception": "EXCEPTION", "correction": "CORRECTION"}


def load(rel: str):
    return json.loads((ROOT / rel).read_text(encoding="utf-8"))


def md_escape(text: str) -> str:
    return str(text).replace("|", "\\|").replace("\n", " ")


# ----------------------------------------------------------------------------- diagramas
def mermaid_state(machine_name: str, machine: dict, modes: tuple) -> str:
    lines = [
        "%% GENERADO por tools/generate_artifacts.py desde contracts/state-machine.json. No editar.",
        f"%% Máquina {machine_name}; modos incluidos: {', '.join(modes)}.",
        "stateDiagram-v2",
        f"    direction {DIRECTION.get(machine_name + ':' + ','.join(modes), 'LR')}",
    ]
    transitions = [t for t in machine["transitions"] if t["mode"] in modes]
    used = set()
    for t in transitions:
        used.update(f for f in t["from"] if f != "(create)")
        used.add(t["to"])
    states = [s for s in machine["states"] if s in used]
    for state in states:
        lines.append(f"    state \"{state}\" as {state}")
    for t in transitions:
        # en una vista de un solo modo el prefijo sobra
        prefix = "" if len(modes) == 1 else MODE_PREFIX[t["mode"]]
        label = f"{t['id']} {prefix}{t['label']}"
        for src in t["from"]:
            src_node = "[*]" if src == "(create)" else src
            lines.append(f"    {src_node} --> {t['to']}: {label}")
    for state in states:
        info = machine["states"][state]
        if info["terminal"] == "conditional":
            lines.append(f"    note right of {state}: Terminal si {info['terminalWhen']}")
    terminal = [s for s in states if machine["states"][s]["terminal"] is True]
    warn = [s for s in states if s == "UNKNOWN"]
    lines.append("    classDef terminal fill:#eef1f4,stroke:#5d636b,color:#1a1c1f")
    lines.append("    classDef warning fill:#fff4dc,stroke:#d98a00,color:#1a1c1f,stroke-width:2px")
    if terminal:
        lines.append(f"    class {','.join(terminal)} terminal")
    if warn:
        lines.append(f"    class {','.join(warn)} warning")
    return "\n".join(lines) + "\n"


# ----------------------------------------------------------------------------- SQL seed
def sql_seed(sm: dict) -> str:
    rows = []
    for mname, m in sm["machines"].items():
        for t in m["transitions"]:
            kind = MODE_KIND[t["mode"]]
            for src in t["from"]:
                rows.append(f"    ('{mname}', '{src}', '{t['to']}', '{kind}', '{t['id']}')")
    head = (
        "-- GENERADO por tools/generate_artifacts.py desde contracts/state-machine.json. No editar.\n"
        "-- Ejecutar después de database/sqlserver_schema.sql. Los triggers TR_*_StateMachine\n"
        "-- rechazan cualquier cambio de estado que no esté en esta tabla. Kind = modo de la\n"
        "-- transición: AUTO, MANUAL (exige OperatorAction), EXCEPTION (exige intento excepcional\n"
        "-- nuevo) o CORRECTION (exige EvidenceObservation contradictoria).\n"
        "SET XACT_ABORT ON;\n"
        "SET NOCOUNT ON;\n"
        "GO\n"
        "BEGIN TRANSACTION;\n"
        "DELETE FROM dbo.AllowedTransition;\n"
        "INSERT INTO dbo.AllowedTransition (Machine, FromStatus, ToStatus, Kind, TransitionId)\n"
        "VALUES\n"
    )
    return head + ",\n".join(rows) + ";\nCOMMIT TRANSACTION;\nGO\n"


# ----------------------------------------------------------------------------- tablas
def state_tables(sm: dict) -> str:
    out = []
    order = ["CartonDetection", "PrintJob", "PrintAttempt", "SequenceGap", "ReprintAuthorization"]
    for mname in order:
        m = sm["machines"][mname]
        out.append(f"**{mname}** (`{m['table']}.{m['column']}`)\n")
        out.append("| Estado | Terminal | Significado |")
        out.append("| --- | --- | --- |")
        for s, info in m["states"].items():
            term = {True: "Sí", False: "No"}.get(info["terminal"], "Condicional")
            if info["terminal"] == "conditional":
                term = f"Si {info['terminalWhen']}"
            elif info.get("mayRemain"):
                term = "No (puede quedar abierto)"
            out.append(f"| `{s}` | {md_escape(term)} | {md_escape(info['meaning'])} |")
        out.append("")
        out.append("| ID | Desde | Hacia | Modo | Disparador | Guarda |")
        out.append("| --- | --- | --- | --- | --- | --- |")
        for t in m["transitions"]:
            frm = ", ".join("creación" if f == "(create)" else f"`{f}`" for f in t["from"])
            out.append(
                f"| {t['id']} | {frm} | `{t['to']}` | {MODE_ES[t['mode']]} | {md_escape(t['trigger'])} | {md_escape(t['guard'])} |"
            )
        if m.get("forbidden"):
            out.append("")
            out.append("Transiciones prohibidas: " + "; ".join(
                f"`{f['from']} → {f['to']}` ({md_escape(f['why'])})" for f in m["forbidden"]) + ".")
        out.append("")
    return "\n".join(out).rstrip() + "\n"


def invariants_table(inv: dict) -> str:
    out = ["| ID | Invariante | Mecanismo | Se aplica en | Pruebas |", "| --- | --- | --- | --- | --- |"]
    for i in inv["invariants"]:
        out.append(
            f"| {i['id']} | {md_escape(i['rule'])} | {md_escape(i['mechanism'])} | {i['enforcedBy']} | {', '.join(i['tests'])} |"
        )
    return "\n".join(out) + "\n"


CLASS_ES = {"SOFTWARE": "Software", "FISICA": "Integración física", "RENDIMIENTO": "Rendimiento", "SUPUESTO_OT": "Supuesto OT"}


def guarantees_table(g: dict) -> str:
    out = [
        "| ID | Clase | Garantía deseada | Mecanismo | Condiciones | Dependencia de hardware | Riesgo residual | Pruebas |",
        "| --- | --- | --- | --- | --- | --- | --- | --- |",
    ]
    for r in g["guarantees"]:
        out.append(
            "| {id} | {cls} | {d} | {m} | {c} | {h} | {r} | {t} |".format(
                id=r["id"], cls=CLASS_ES[r["class"]], d=md_escape(r["desired"]), m=md_escape(r["mechanism"]),
                c=md_escape(r["conditions"]), h=md_escape(r["hardware"]), r=md_escape(r["residualRisk"]),
                t=", ".join(r["tests"]))
        )
    return "\n".join(out) + "\n"


def open_decisions_table(d: dict) -> str:
    out = ["| ID | Pregunta | Impacto arquitectónico | Qué puede avanzar sin resolverla |", "| --- | --- | --- | --- |"]
    for r in d["decisions"]:
        out.append(f"| {r['id']} | {md_escape(r['question'])} | {md_escape(r['impact'])} | {md_escape(r['canProceed'])} |")
    return "\n".join(out) + "\n"


STATUS_ES = {"NO_EJECUTADA": "No ejecutada", "EJECUTADA_EN_REVISION": "Ejecutada en esta revisión"}


def tests_summary(tc: dict) -> str:
    out = ["| ID | Prueba | Nivel | Perfil | Transiciones | Invariantes | Estado |", "| --- | --- | --- | --- | --- | --- | --- |"]
    for t in tc["tests"]:
        out.append(
            f"| {t['id']} | {md_escape(t['title'])} | {t['level']} | {t['profile']} | {', '.join(t['transitions']) or '—'} | {', '.join(t['invariants'])} | {STATUS_ES[t['status']]} |"
        )
    return "\n".join(out) + "\n"


def tests_matrix(tc: dict, sm: dict, inv: dict) -> str:
    out = [
        "<!-- GENERADO por tools/generate_artifacts.py desde tests/test-catalog.json. No editar. -->",
        "# Matriz de pruebas v1.1",
        "",
        "Cada prueba declara sus precondiciones, datos, inyección, estados, eventos, efectos físicos, métricas y criterio de aprobación. "
        "Las columnas de transiciones e invariantes enlazan con `contracts/state-machine.json` y `contracts/invariants.json`; "
        "`tools/verify_package.py` exige que cada transición e invariante tenga al menos una prueba.",
        "",
        "Estado: solo T23 y T28 se ejecutaron en esta revisión documental. Las demás requieren la implementación y quedan como **No ejecutada**.",
        "",
        "Niveles: " + "; ".join(f"`{k}` {v}" for k, v in tc["levels"].items()),
        "",
        "## Resumen",
        "",
        tests_summary(tc),
        "## Detalle",
        "",
    ]
    for t in tc["tests"]:
        out += [
            f"### {t['id']} · {t['title']}",
            "",
            f"Origen: {t['origin']} · Nivel: {t['level']} · Perfil: {t['profile']} · Estado: {STATUS_ES[t['status']]}",
            "",
            "| Campo | Valor |",
            "| --- | --- |",
            f"| Precondiciones | {md_escape(t['preconditions'])} |",
            f"| Datos iniciales | {md_escape(t['initialData'])} |",
            f"| Inyección | {md_escape(t['injection'])} |",
            f"| Estados esperados | {md_escape(t['expectedStates'])} |",
            f"| Eventos publicados | {md_escape(t['publishedEvents'])} |",
            f"| Efectos físicos esperados | {md_escape(t['physicalEffects'])} |",
            f"| Métricas afectadas | {md_escape(t['metrics'])} |",
            f"| Criterio de aprobación | {md_escape(t['passCriteria'])} |",
            f"| Transiciones cubiertas | {', '.join(t['transitions']) or '—'} |",
            f"| Invariantes | {', '.join(t['invariants'])} |",
            "",
        ]
    # cobertura
    cover = {}
    for t in tc["tests"]:
        for tr in t["transitions"]:
            cover.setdefault(tr, []).append(t["id"])
    out += ["## Cobertura de transiciones", "", "| Transición | Máquina | Pruebas |", "| --- | --- | --- |"]
    for mname, m in sm["machines"].items():
        for tr in m["transitions"]:
            out.append(f"| {tr['id']} | {mname} | {', '.join(cover.get(tr['id'], [])) or '**SIN PRUEBA**'} |")
    out += ["", "## Cobertura de invariantes", "", "| Invariante | Pruebas |", "| --- | --- |"]
    for i in inv["invariants"]:
        out.append(f"| {i['id']} | {', '.join(i['tests'])} |")
    return "\n".join(out) + "\n"


# ----------------------------------------------------------------------------- motor
def toc_block() -> str:
    text = (ROOT / "DOCUMENTO_PRINCIPAL.md").read_text(encoding="utf-8")
    heads = re.findall(r"^# (\d+)\. (.+)$", text, re.M)
    half = (len(heads) + 1) // 2
    left, right = heads[:half], heads[half:]
    out = ["| Secciones 1 a %s | Secciones %s a %s |" % (left[-1][0], right[0][0], right[-1][0]), "| --- | --- |"]
    for i in range(half):
        a = f"{left[i][0]}. {left[i][1]}"
        b = f"{right[i][0]}. {right[i][1]}" if i < len(right) else ""
        out.append(f"| {md_escape(a)} | {md_escape(b)} |")
    return "\n".join(out) + "\n"


def build_outputs() -> tuple[dict[str, str], dict[str, str]]:
    sm = load("contracts/state-machine.json")
    inv = load("contracts/invariants.json")
    tc = load("tests/test-catalog.json")
    gu = load("docs/guarantees.json")
    od = load("docs/open-decisions.json")
    files = {}
    for fname, mname, modes in STATE_DIAGRAMS:
        files[f"diagrams/{fname}"] = mermaid_state(mname, sm["machines"][mname], modes)
    files["database/sqlserver_state_transitions.sql"] = sql_seed(sm)
    files["tests/MATRIZ_PRUEBAS.md"] = tests_matrix(tc, sm, inv)
    blocks = {
        "state-tables": state_tables(sm),
        "invariants": invariants_table(inv),
        "guarantees": guarantees_table(gu),
        "open-decisions": open_decisions_table(od),
        "tests-summary": tests_summary(tc),
        "toc": toc_block(),
    }
    return files, blocks


def apply_blocks(text: str, blocks: dict[str, str]) -> tuple[str, list[str]]:
    used = []

    def repl(m):
        name = m.group("name")
        if name not in blocks:
            raise SystemExit(f"Bloque desconocido: {name}")
        used.append(name)
        return m.group(1) + blocks[name] + m.group(4)

    return BLOCK_RE.sub(repl, text), used


def markdown_targets() -> list[Path]:
    return sorted(p for p in ROOT.rglob("*.md") if "historial" not in p.parts)


def run(check: bool) -> list[str]:
    files, blocks = build_outputs()
    stale = []
    for rel, content in files.items():
        path = ROOT / rel
        # comparación exacta de bytes y escritura con LF en cualquier sistema operativo
        current = path.read_bytes().decode("utf-8") if path.exists() else None
        if current != content:
            stale.append(rel)
            if not check:
                path.write_text(content, encoding="utf-8", newline="\n")
    for path in markdown_targets():
        text = path.read_bytes().decode("utf-8")
        new, used = apply_blocks(text, blocks)
        if used and new != text:
            rel = path.relative_to(ROOT).as_posix()
            stale.append(f"{rel} (bloques: {', '.join(sorted(set(used)))})")
            if not check:
                path.write_text(new, encoding="utf-8", newline="\n")
    return stale


if __name__ == "__main__":
    check = "--check" in sys.argv
    stale = run(check)
    if check:
        if stale:
            print("Artefactos desactualizados respecto de sus fuentes:")
            for s in stale:
                print("  -", s)
            sys.exit(1)
        print("Artefactos generados al día.")
    else:
        print("Escritos:" if stale else "Sin cambios.")
        for s in stale:
            print("  -", s)
