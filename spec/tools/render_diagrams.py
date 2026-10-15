#!/usr/bin/env python3
"""Renderiza los diagramas Mermaid a SVG y PNG y registra desde qué versión de cada fuente.

Escribe diagrams/render-manifest.json con el SHA-256 de cada .mmd renderizado. El verificador
compara ese hash con el .mmd actual: así sabe si una imagen está desactualizada sin depender de
fechas de archivo, que git y los descompresores no conservan.

Requiere Mermaid CLI (npm install -g @mermaid-js/mermaid-cli). tools/puppeteer.json se usa solo si
el Chromium que declara existe en este equipo; si no, mmdc usa el de Puppeteer.

Uso:
  python3 tools/render_diagrams.py           # renderiza los .mmd que cambiaron (Windows: py tools\\render_diagrams.py)
  python3 tools/render_diagrams.py --all     # renderiza todos
  python3 tools/render_diagrams.py --check   # no renderiza; termina en 1 si alguno está desactualizado
"""
from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIAGRAMS = ROOT / "diagrams"
MANIFEST = DIAGRAMS / "render-manifest.json"
PUPPETEER = ROOT / "tools" / "puppeteer.json"


def source_hash(mmd: Path) -> str:
    return hashlib.sha256(mmd.read_bytes()).hexdigest()


def load_manifest() -> dict[str, str]:
    if not MANIFEST.exists():
        return {}
    return json.loads(MANIFEST.read_bytes().decode("utf-8"))["sources"]


def stale() -> list[Path]:
    manifest = load_manifest()
    return [m for m in sorted(DIAGRAMS.glob("*.mmd"))
            if manifest.get(m.name) != source_hash(m)
            or not m.with_suffix(".svg").exists() or not m.with_suffix(".png").exists()]


def render(mmds: list[Path]) -> None:
    mmdc = shutil.which("mmdc")
    if not mmdc:
        raise SystemExit("No se encontró mmdc. Instalar con: npm install -g @mermaid-js/mermaid-cli")
    extra = []
    if PUPPETEER.exists():
        exe = json.loads(PUPPETEER.read_text(encoding="utf-8")).get("executablePath")
        if exe and Path(exe).exists():
            extra = ["-p", str(PUPPETEER)]
    manifest = load_manifest()
    for mmd in mmds:
        digest = source_hash(mmd)
        for ext, scale in ((".svg", []), (".png", ["-s", "2"])):
            subprocess.run([mmdc, "-i", str(mmd), "-o", str(mmd.with_suffix(ext)), "-b", "white", "-q", *extra, *scale],
                           check=True)
        manifest[mmd.name] = digest
        print("  renderizado:", mmd.name)
    names = {m.name for m in DIAGRAMS.glob("*.mmd")}
    data = {"$comment": "Generado por tools/render_diagrams.py: SHA-256 de cada .mmd con el que se renderizaron su SVG y su PNG.",
            "sources": {k: manifest[k] for k in sorted(manifest) if k in names}}
    MANIFEST.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    if "--check" in sys.argv:
        pending = stale()
        for m in pending:
            print("  desactualizado:", m.name)
        sys.exit(1 if pending else 0)
    targets = sorted(DIAGRAMS.glob("*.mmd")) if "--all" in sys.argv else stale()
    if not targets:
        print("Diagramas al día.")
    else:
        render(targets)
