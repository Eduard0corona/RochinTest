#!/usr/bin/env bash
# Compuerta completa de una fase (PROMPT_IMPLEMENTACION.md, sección 3, paso 5).
# Requiere: .NET SDK 10, Docker (Testcontainers), Python 3.10+ con jsonschema y sqlfluff.
set -euo pipefail
cd "$(dirname "$0")/.."
PY="${PYTHON:-python3}"
export PYTHONIOENCODING=utf-8

echo "== 1/5 Integridad de la especificación"
"$PY" -B spec/tools/verify_package.py
echo "== 2/5 Artefactos derivados al día"
"$PY" -B spec/tools/generate_artifacts.py --check
echo "== 3/5 Compilación sin advertencias"
dotnet build -warnaserror
echo "== 4/5 Pruebas (sin carga)"
dotnet test --no-build --filter "Category!=Load" --logger "trx" --results-directory TestResults
echo "== 5/5 Trazabilidad Txx"
"$PY" -B build/check_traceability.py "$@"
echo "Compuerta en verde."
