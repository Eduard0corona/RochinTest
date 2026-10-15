#!/usr/bin/env bash
# Genera el DOCX y el PDF consolidados desde DOCUMENTO_PRINCIPAL.md.
# Requiere: pandoc 3.x y LibreOffice (soffice). Ejecutar desde la raíz del paquete.
set -euo pipefail
OUT=documento
NAME=Arquitectura_Etiquetado_OPC_Idempotente_v1.1
mkdir -p "$OUT"
pandoc DOCUMENTO_PRINCIPAL.md \
  --from markdown+pipe_tables+yaml_metadata_block \
  --to docx \
  --reference-doc tools/pandoc/reference.docx \
  --lua-filter tools/pandoc/table_widths.lua \
  --resource-path . \
  --output "$OUT/$NAME.docx"
# pandoc declara cada imagen con un Override; se agrega también la declaración Default por
# extensión, que algunos validadores de OOXML exigen. No cambia el contenido del documento.
python3 - "$OUT/$NAME.docx" <<'PY'
import shutil, sys, tempfile, zipfile
path = sys.argv[1]
with zipfile.ZipFile(path) as z:
    items = [(i, z.read(i.filename)) for i in z.infolist()]
ct = dict((i.filename, data) for i, data in items)["[Content_Types].xml"].decode("utf-8")
if 'Extension="png"' not in ct:
    ct = ct.replace('<Default Extension="xml"', '<Default Extension="png" ContentType="image/png" /><Default Extension="xml"', 1)
    tmp = tempfile.NamedTemporaryFile(delete=False, suffix=".docx").name
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as out:
        for info, data in items:
            out.writestr(info, ct.encode("utf-8") if info.filename == "[Content_Types].xml" else data)
    shutil.move(tmp, path)
PY
soffice --headless --convert-to pdf --outdir "$OUT" "$OUT/$NAME.docx" >/dev/null
echo "Generados: $OUT/$NAME.docx y $OUT/$NAME.pdf"
