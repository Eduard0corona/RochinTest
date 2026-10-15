# Compuerta completa de una fase (PROMPT_IMPLEMENTACION.md, sección 3, paso 5).
# Requiere: .NET SDK 10, Docker (Testcontainers), Python 3.10+ con jsonschema y sqlfluff.
$ErrorActionPreference = 'Stop'
# Los argumentos (por ejemplo --fase N) van al paso de trazabilidad; dentro de un scriptblock $args es otro.
$scriptArgs = $args
Set-Location (Join-Path $PSScriptRoot '..')
$py = if ($env:PYTHON) { $env:PYTHON } else { 'py' }
$env:PYTHONIOENCODING = 'utf-8'

function Step([string] $title, [scriptblock] $action) {
    Write-Host "== $title"
    & $action
    if ($LASTEXITCODE -ne 0) { throw "Falló: $title (código $LASTEXITCODE)" }
}

Step '1/5 Integridad de la especificación' { & $py -B spec/tools/verify_package.py }
Step '2/5 Artefactos derivados al día' { & $py -B spec/tools/generate_artifacts.py --check }
Step '3/5 Compilación sin advertencias' { dotnet build -warnaserror }
Step '4/5 Pruebas (sin carga)' {
    if (Test-Path TestResults) { Remove-Item -Recurse -Force TestResults }
    dotnet test --no-build --filter 'Category!=Load' --logger trx --results-directory TestResults
}
Step '5/5 Trazabilidad Txx' { & $py -B build/check_traceability.py --trx-dir TestResults @scriptArgs }
Write-Host 'Compuerta en verde.'
