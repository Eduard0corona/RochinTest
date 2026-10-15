#!/usr/bin/env python3
"""Trazabilidad de pruebas: cada Txx de las fases cerradas y de la fase en curso tiene pruebas xUnit.

Lee spec/tests/test-catalog.json (pruebas T01 a T47), spec/docs/PLAN_IMPLEMENTACION.md (qué pruebas
cierra cada fase), docs/TEST_STATUS.md (pruebas NO_APLICA) y las etiquetas git fase-N-ok. Busca en
tests/**/*.cs los atributos [Trait("Spec", "Txx")].

Falla (código 1) si una Txx de una fase con etiqueta fase-N-ok, o de la fase en curso, no tiene
ninguna prueba con su Trait. Excepciones: las marcadas NO_APLICA en docs/TEST_STATUS.md y T28, que
se cumple con el paso de CI que corre spec/tools/verify_package.py.

Uso: py build/check_traceability.py [--fase N]
     La fase en curso es --fase, o la variable CARTONPRINTING_FASE, o la primera fase sin etiqueta.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TRAIT = re.compile(r'\[\s*(?:assembly:\s*)?Trait\s*\(\s*"Spec"\s*,\s*"(T\d{2})"\s*\)\s*\]')
CI_ONLY = {"T28"}


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


def traits() -> dict[str, list[str]]:
    found: dict[str, list[str]] = {}
    for path in sorted((ROOT / "tests").rglob("*.cs")):
        if any(part in ("bin", "obj") for part in path.parts):
            continue
        for tid in TRAIT.findall(path.read_text(encoding="utf-8")):
            found.setdefault(tid, []).append(str(path.relative_to(ROOT)).replace("\\", "/"))
    return found


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--fase", type=int, default=None)
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
    found = traits()

    errors = []
    for phase in required_phases:
        for tid in phases.get(phase, []):
            if tid in na or tid in CI_ONLY:
                continue
            if tid not in found:
                errors.append(f"Fase {phase}: {tid} no tiene ninguna prueba con [Trait(\"Spec\", \"{tid}\")]")

    unknown = sorted(set(found) - catalog)
    for tid in unknown:
        errors.append(f"{tid} citado en {found[tid][0]} no existe en spec/tests/test-catalog.json")

    print(f"Fases cerradas (etiqueta fase-N-ok): {sorted(closed) or 'ninguna'}; fase en curso: {current}")
    for phase in required_phases:
        tids = phases.get(phase, [])
        status = []
        for tid in tids:
            if tid in na:
                status.append(f"{tid}=NO_APLICA")
            elif tid in CI_ONLY:
                status.append(f"{tid}=CI(verify_package.py)")
            else:
                status.append(f"{tid}={len(found.get(tid, []))} archivo(s)")
        print(f"  Fase {phase}: {', '.join(status) if status else '(sin pruebas)'}")
    print(f"Pruebas Txx con Trait en tests/: {len(found)} de {len(catalog)}")

    if errors:
        for e in errors:
            print(f"ERROR {e}")
        print(f"Resultado: {len(errors)} errores")
        return 1
    print("Resultado: trazabilidad OK, 0 errores")
    return 0


if __name__ == "__main__":
    sys.exit(main())
