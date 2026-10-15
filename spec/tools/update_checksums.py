#!/usr/bin/env python3
"""Regenera CHECKSUMS.sha256 con el SHA-256 de cada archivo del paquete.

Excluye CHECKSUMS.sha256, .git y __pycache__. Las rutas se escriben con "/" en cualquier sistema
operativo y el archivo usa LF, en el formato de sha256sum. Correrlo solo después de un cambio
intencional y revisado de la especificación; después, correr tools/verify_package.py.

Uso: python3 tools/update_checksums.py   (en Windows: py tools\\update_checksums.py)
"""
from __future__ import annotations

import hashlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

if __name__ == "__main__":
    lines = []
    for p in sorted(ROOT.rglob("*"), key=lambda q: q.relative_to(ROOT).as_posix()):
        if p.is_file() and ".git" not in p.parts and "__pycache__" not in p.parts and p.name != "CHECKSUMS.sha256":
            lines.append(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(ROOT).as_posix()}")
    (ROOT / "CHECKSUMS.sha256").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"CHECKSUMS.sha256: {len(lines)} archivos")
