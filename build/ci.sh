#!/usr/bin/env bash
# Compuerta completa de una fase (PROMPT_IMPLEMENTACION.md, sección 3, paso 5).
# Requiere: .NET SDK 10, Docker (Testcontainers), Python 3.10+ con jsonschema y sqlfluff.
set -euo pipefail
cd "$(dirname "$0")/.."
PY="${PYTHON:-python3}"
export PYTHONIOENCODING=utf-8

# --carga corre además las pruebas de carga (Category=Load, T12; Fase 7), que tardan unos 45 minutos.
WITH_LOAD=0
ARGS=()
for arg in "$@"; do
  if [ "$arg" = "--carga" ]; then WITH_LOAD=1; else ARGS+=("$arg"); fi
done

echo "== 1/5 Integridad de la especificación"
"$PY" -B spec/tools/verify_package.py
echo "== 2/5 Artefactos derivados al día"
"$PY" -B spec/tools/generate_artifacts.py --check
echo "== 2b/5 Secretos fuera del repositorio"
"$PY" -B build/check_secrets.py
echo "== 3/5 Compilación sin advertencias"
dotnet build -warnaserror
echo "== 4/5 Pruebas (sin carga)"
rm -rf TestResults
dotnet test --no-build --filter "Category!=Load" --logger "trx" --results-directory TestResults
if [ "$WITH_LOAD" = 1 ]; then
  echo "== 4b/5 Pruebas de carga (T12)"
  dotnet test --no-build --filter "Category=Load" --logger "trx" --results-directory TestResults/carga
fi
echo "== 5/5 Trazabilidad Txx"
"$PY" -B build/check_traceability.py --trx-dir TestResults "${ARGS[@]+"${ARGS[@]}"}"
echo "Compuerta en verde."
