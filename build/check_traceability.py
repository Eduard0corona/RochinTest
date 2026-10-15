#!/usr/bin/env python3
"""Trazabilidad de pruebas: cada Txx de las fases cerradas y de la fase en curso tiene pruebas xUnit.

Lee spec/tests/test-catalog.json (pruebas T01 a T47), spec/docs/PLAN_IMPLEMENTACION.md (qué pruebas
cierra cada fase), docs/TEST_STATUS.md (pruebas NO_APLICA) y las etiquetas git fase-N-ok. Busca en
tests/**/*.cs los atributos [Trait("Spec", "Txx")], sin contar los que están en comentarios o en
bloques #if, y los asocia a la clase o al método que decoran.

Falla (código 1) si una Txx de una fase con etiqueta fase-N-ok, o de la fase en curso, no tiene
ninguna prueba con su Trait. Con --trx-dir exige además que al menos una de esas pruebas aparezca
en los resultados TRX de la corrida con resultado Passed. Excepciones: las marcadas NO_APLICA en
docs/TEST_STATUS.md y T28, que se cumple con el paso de CI que corre spec/tools/verify_package.py.

El script comprueba que cada Txx tiene pruebas, no que esas pruebas cubran todo su criterio: eso lo
revisan el revisor independiente y docs/TEST_STATUS.md.

Uso: py build/check_traceability.py [--fase N] [--trx-dir TestResults]
     La fase en curso es --fase, o la variable CARTONPRINTING_FASE, o la primera fase sin etiqueta.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TRAIT = re.compile(r'\[\s*(?:assembly:\s*)?Trait\s*\(\s*"Spec"\s*,\s*"(T\d{2})"\s*\)\s*\]')
CI_ONLY = {"T28"}
TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def phases_from_plan() -> dict[int, list[str]]:
    plan = (ROOT / "spec/docs/PLAN_IMPLEMENTACION.md").read_text(encoding="utf-8")
    phases: dict[int, list[str]] = {}
    for line in plan.splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) < 3:
            continue
        m = re.match(r"^(\d+)\.", cells[0])
        if m:
            phases[int(m.group(1))] = re.findall(r"\bT\d{2}\b", cells[-1])
    return phases


def closed_phases() -> set[int]:
    try:
        out = subprocess.run(["git", "tag", "--list", "fase-*-ok"], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError) as exc:
        print(f"ERROR no se pudieron leer las etiquetas git: {exc}")
        sys.exit(1)
    return {int(m.group(1)) for m in re.finditer(r"^fase-(\d+)-ok$", out, re.M)}


def not_applicable() -> set[str]:
    path = ROOT / "docs/TEST_STATUS.md"
    if not path.exists():
        return set()
    result = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) >= 3 and re.fullmatch(r"T\d{2}", cells[0]) and cells[2].startswith("NO_APLICA"):
            result.add(cells[0])
    return result


def strip_code(text: str) -> str:
    """Quita comentarios // y /* */ (respetando cadenas) y los bloques #if ... #endif."""
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            seg = text[i:n if j < 0 else j + 2]
            out.append("\n" * seg.count("\n"))
            i = n if j < 0 else j + 2
            continue
        if text.startswith('"""', i):
            q = 3
            while i + q < n and text[i + q] == '"':
                q += 1
            j = text.find('"' * q, i + q)
            j = n if j < 0 else j + q
            out.append(text[i:j])
            i = j
            continue
        if c == "@" and nxt == '"' or (c in "$@" and nxt in "$@" and i + 2 < n and text[i + 2] == '"'):
            start = i
            i = text.index('"', i) + 1
            while i < n:
                if text[i] == '"' and i + 1 < n and text[i + 1] == '"':
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            out.append(text[start:i])
            continue
        if c == '"' or (c == "$" and nxt == '"'):
            start = i
            i = text.index('"', i) + 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
            i += 1
            out.append(text[start:i])
            continue
        if c == "'":
            j = i + 1
            while j < n and text[j] != "'" and j - i < 12:
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
            continue
        out.append(c)
        i += 1
    lines = "".join(out).split("\n")
    kept, depth = [], 0
    for line in lines:
        s = line.strip()
        if s.startswith("#if"):
            depth += 1
            kept.append("")
            continue
        if s.startswith("#endif") and depth > 0:
            depth -= 1
            kept.append("")
            continue
        kept.append("" if depth > 0 else line)
    return "\n".join(kept)


def targets(path: Path) -> list[tuple[str, str, str | None]]:
    """(Txx, clase con espacio de nombres, método o None si el Trait está en la clase)."""
    code = strip_code(path.read_text(encoding="utf-8"))
    ns = re.search(r"\bnamespace\s+([\w.]+)", code)
    namespace = ns.group(1) if ns else ""
    classes = [(m.start(), m.group(1)) for m in re.finditer(r"\b(?:class|record)\s+(\w+)", code)]
    result = []
    for m in TRAIT.finditer(code):
        after = code[m.end():]
        stop = re.search(r"[{;(]", after)
        head = after[: stop.start()] if stop else after
        decl = re.search(r"\b(?:class|record)\s+(\w+)", head)
        if decl:
            result.append((m.group(1), f"{namespace}.{decl.group(1)}".strip("."), None))
            continue
        method = re.search(r"(\w+)\s*$", head)
        enclosing = [name for pos, name in classes if pos < m.start()]
        if method and enclosing:
            result.append((m.group(1), f"{namespace}.{enclosing[-1]}".strip("."), method.group(1)))
    return result


def passed_tests(trx_dir: Path) -> set[tuple[str, str]]:
    passed: set[tuple[str, str]] = set()
    for trx in trx_dir.rglob("*.trx"):
        root = ET.parse(trx).getroot()
        methods = {}
        for ut in root.iter(f"{TRX_NS}UnitTest"):
            tm = ut.find(f"{TRX_NS}TestMethod")
            if tm is not None:
                methods[ut.get("id")] = (tm.get("className"), tm.get("name"))
        for res in root.iter(f"{TRX_NS}UnitTestResult"):
            if res.get("outcome") == "Passed" and res.get("testId") in methods:
                passed.add(methods[res.get("testId")])
    return passed


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--fase", type=int, default=None)
    parser.add_argument("--trx-dir", type=Path, default=None)
    args = parser.parse_args()

    catalog = {t["id"] for t in json.loads((ROOT / "spec/tests/test-catalog.json").read_text(encoding="utf-8"))["tests"]}
    phases = phases_from_plan()
    planned = {tid for tids in phases.values() for tid in tids}
    if planned != catalog:
        print(f"ERROR el plan y el catálogo no coinciden: {sorted(planned ^ catalog)}")
        return 1

    closed = closed_phases()
    env = os.environ.get("CARTONPRINTING_FASE")
    current = args.fase if args.fase is not None else (int(env) if env else next((p for p in sorted(phases) if p not in closed), None))
    required_phases = sorted(closed | ({current} if current is not None else set()))
    na = not_applicable()

    found: dict[str, list[tuple[str, str | None]]] = {}
    for path in sorted((ROOT / "tests").rglob("*.cs")):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        for tid, cls, method in targets(path):
            found.setdefault(tid, []).append((cls, method))

    passed = passed_tests(args.trx_dir) if args.trx_dir else None
    if args.trx_dir and not passed:
        print(f"ERROR --trx-dir {args.trx_dir}: no hay resultados TRX con pruebas superadas")
        return 1

    def executed(tid: str) -> int:
        return sum(1 for cls, name in passed or () for tcls, tmethod in found.get(tid, [])
                   if cls == tcls and (tmethod is None or name == tmethod))

    errors = []
    for phase in required_phases:
        for tid in phases.get(phase, []):
            if tid in na or tid in CI_ONLY:
                continue
            if tid not in found:
                errors.append(f"Fase {phase}: {tid} no tiene ninguna prueba con [Trait(\"Spec\", \"{tid}\")]")
            elif passed is not None and executed(tid) == 0:
                errors.append(f"Fase {phase}: {tid} tiene pruebas con su Trait pero ninguna se ejecutó con resultado Passed")

    for tid in sorted(set(found) - catalog):
        errors.append(f"{tid} citado en {found[tid][0][0]} no existe en spec/tests/test-catalog.json")

    print(f"Fases cerradas (etiqueta fase-N-ok): {sorted(closed) or 'ninguna'}; fase en curso: {current}")
    for phase in required_phases:
        status = []
        for tid in phases.get(phase, []):
            if tid in na:
                status.append(f"{tid}=NO_APLICA")
            elif tid in CI_ONLY:
                status.append(f"{tid}=CI(verify_package.py)")
            else:
                ran = f", {executed(tid)} ejecutadas" if passed is not None else ""
                status.append(f"{tid}={len(found.get(tid, []))} destino(s){ran}")
        print(f"  Fase {phase}: {', '.join(status) if status else '(sin pruebas)'}")
    print(f"Pruebas Txx con Trait en tests/: {len(found)} de {len(catalog)}"
          + ("" if passed is None else f"; resultados TRX con {len(passed)} pruebas superadas"))

    if errors:
        for e in errors:
            print(f"ERROR {e}")
        print(f"Resultado: {len(errors)} errores")
        return 1
    print("Resultado: trazabilidad OK, 0 errores")
    return 0


if __name__ == "__main__":
    sys.exit(main())
