#!/usr/bin/env python3
"""Secretos fuera del repositorio (CLAUDE.md y AI_CONTEXT, regla 9; Fase 7, seguridad).

Revisa todos los archivos de texto versionados (git ls-files) y falla (código 1) si encuentra:
- certificados o claves (.pfx, .p12, .pem, .key, .der, .crt, .cer);
- una clave privada en texto (BEGIN ... PRIVATE KEY);
- un valor literal asignado a una clave que nombra una contraseña, una frase de paso o un secreto (Password=, Pwd=,
  MSSQL_SA_PASSWORD:, RABBITMQ_DEFAULT_PASS:, "Password": "...", ClientCertificatePassphrase, ...), en una cadena de conexión, en
  YAML, en JSON, en un .env o en una línea de comandos;
- credenciales dentro de un Uri amqp/amqps.

No cuentan como valor literal: vacío, una variable (de sqlcmd, de entorno, de plantilla o de configuración), un marcador entre
ángulos (<la del aprovisionamiento>) o puntos suspensivos.

Quedan fuera tests/ (credenciales efímeras de los contenedores de Testcontainers), spec/ (solo lectura) y este script, cuyos
patrones coinciden consigo mismos. Es una revisión por patrones: no reemplaza una herramienta de detección de secretos.
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
# Una clave que nombra un secreto, con = o : (también entre comillas, como en JSON), y su valor.
SECRET_KEY = re.compile(r"""(?i)["']?\b[\w.-]*(password|passwd|pwd|passphrase|secret|_pass)\b["']?\s*[:=]\s*(?P<value>[^\s,;}]*)""")
# Variables de sqlcmd, de entorno, de plantilla o de configuración (en cualquier parte del valor), y marcadores: no son un valor literal.
VARIABLE = re.compile(r"\$\(|\$\{|\{\{|%[A-Za-z_]+%")
PLACEHOLDER = re.compile(r"""^["']?(<|\.\.\.|…)""")
# En el código solo cuenta una cadena literal entre comillas (una expresión como options.Password no es un secreto).
CODE = re.compile(r"\.(cs|py|ps1|sh|js|ts)$")
AMQP_CREDENTIALS = re.compile(r"amqps?://[^:/@\s\"'<]+:[^@/\s\"'<]+@")


def literal(value: str, code: bool) -> bool:
    value = value.strip()
    if not value or PLACEHOLDER.match(value) or VARIABLE.search(value):
        return False
    if code and not re.match(r"""^@?\$?["'][^"']+["']""", value):
        return False
    return bool(value.strip("\"'`)"))


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
        for number, line in enumerate(text.splitlines(), 1):
            for match in SECRET_KEY.finditer(line):
                if literal(match.group("value"), bool(CODE.search(name))):
                    errors.append(f"{name}:{number}: valor literal para {match.group(0).split(match.group('value'))[0].strip()!r}")
            if AMQP_CREDENTIALS.search(line):
                errors.append(f"{name}:{number}: credenciales en un Uri de RabbitMQ")
    for e in errors:
        print(f"ERROR {e}")
    print(f"Resultado: {len(files)} archivos versionados revisados, {len(errors)} errores")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
