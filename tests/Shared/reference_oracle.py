"""Oráculo de referencia para pruebas diferenciales (solo en tiempo de prueba).

Usa las funciones de spec/tools/verify_package.py, que son la implementación de referencia de la
especificación: errores de JSON Schema como los informa jsonschema, huella de origen y reglas de
negocio. Lee un arreglo JSON de stdin y escribe un arreglo JSON en stdout.

Uso: py -B reference_oracle.py <spec_root> (schema-errors | fingerprints | business-errors)
"""
import json
import sys
from pathlib import Path

spec_root = Path(sys.argv[1]).resolve()
command = sys.argv[2]
sys.dont_write_bytecode = True
sys.path.insert(0, str(spec_root / "tools"))
import verify_package as vp  # noqa: E402

sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")
items = json.load(sys.stdin)
sm = json.loads((spec_root / "contracts/state-machine.json").read_text(encoding="utf-8"))

if command == "schema-errors":
    from jsonschema import Draft202012Validator

    fc = vp.build_format_checker()
    validators = {}
    out = []
    for item in items:
        name = item["schema"]
        if name not in validators:
            schema = json.loads((spec_root / "contracts" / name).read_text(encoding="utf-8"))
            validators[name] = Draft202012Validator(schema, format_checker=fc)
        errs = validators[name].iter_errors(item["doc"])
        kinds = sorted({(e.validator, "".join(f"/{x}" for x in e.absolute_path)) for e in errs})
        out.append([list(k) for k in kinds])
elif command == "fingerprints":
    out = [vp.source_fingerprint(doc) for doc in items]
elif command == "business-errors":
    out = [vp.business_errors(doc, sm) for doc in items]
else:
    raise SystemExit(f"comando desconocido: {command}")

json.dump(out, sys.stdout, ensure_ascii=False)
