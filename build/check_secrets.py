#!/usr/bin/env python3
"""Secretos fuera del repositorio (CLAUDE.md y AI_CONTEXT, regla 9; Fase 7, seguridad).

Revisa los archivos versionados (git ls-files) y falla (código 1) si encuentra:
- certificados o claves (.pfx, .p12, .pem, .key, .der, .crt, .cer);
- una clave privada en texto (BEGIN ... PRIVATE KEY);
- en src/, build/, la configuración (*.json) y docker-compose: una contraseña literal en una cadena de conexión
  (Password= o Pwd= con un valor que no es una variable) o credenciales dentro de un Uri amqp/amqps.

Quedan fuera tests/ (credenciales efímeras de los contenedores de Testcontainers), spec/ (solo lectura) y este
script, cuyos patrones coinciden consigo mismos.
Uso: py build/check_secrets.py
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SELF = Path(__file__).resolve().relative_to(ROOT).as_posix()
CERT_FILES = re.compile(r"\.(pfx|p12|pem|key|der|crt|cer)$", re.IGNORECASE)
PRIVATE_KEY = re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")
PASSWORD = re.compile(r"(?i)\b(password|pwd)\s*=\s*(.*)")
# Variables de sqlcmd, de entorno, de plantilla o de configuración: no son un valor literal.
VARIABLE = re.compile(r"\$\(|\$\{|\{\{|%[A-Za-z_]+%")
AMQP_CREDENTIALS = re.compile(r"amqps?://[^:/@\s\"']+:[^@/\s\"']+@")
CONFIG_SCOPE = re.compile(r"^(src/|build/|docker-compose[^/]*\.ya?ml$|[^/]+\.json$|.*appsettings[^/]*\.json$)")


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    files = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.splitlines()
    errors = []
    for name in files:
        if name.startswith(("spec/", "tests/")) or name == SELF:
            continue
        if CERT_FILES.search(name):
            errors.append(f"{name}: certificado o clave versionado")
            continue
        path = ROOT / name
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, FileNotFoundError):
            continue
        if PRIVATE_KEY.search(text):
            errors.append(f"{name}: clave privada en texto")
        if CONFIG_SCOPE.match(name):
            for number, line in enumerate(text.splitlines(), 1):
                match = PASSWORD.search(line)
                if match and match.group(2).strip(" ;'\"N") and not VARIABLE.search(match.group(2)):
                    errors.append(f"{name}:{number}: contraseña literal")
                if AMQP_CREDENTIALS.search(line):
                    errors.append(f"{name}:{number}: credenciales en un Uri de RabbitMQ")
    for e in errors:
        print(f"ERROR {e}")
    print(f"Resultado: {len(files)} archivos versionados revisados, {len(errors)} errores")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
