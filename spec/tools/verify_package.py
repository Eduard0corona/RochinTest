#!/usr/bin/env python3
"""Verificador del paquete CartonPrinting v1.1 (prueba T28; incluye T23).

Ejecuta comprobaciones estáticas y semánticas sobre contratos, máquina de estados, DDL,
pruebas, invariantes, garantías, diagramas, documentación y checksums. Lo que no puede
comprobarse sin ejecutar el sistema se lista como PENDIENTE DE INTEGRACIÓN.

Uso:
  python3 tools/verify_package.py                    # todo (en Windows: py tools\\verify_package.py)
  python3 tools/verify_package.py --skip-checksums   # durante la edición
  python3 tools/verify_package.py --skip-sqlfluff    # si sqlfluff no está instalado

Requisitos: Python 3.10+, jsonschema>=4.18, sqlfluff (opcional pero recomendado).
Código de salida: 0 si no hay errores; 1 si hay al menos un error.
"""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import re
import sqlite3
import subprocess
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.dont_write_bytecode = True  # no dejar bytecode compilado dentro del paquete
sys.path.insert(0, str(ROOT / "tools"))

ERRORS: list[str] = []
PASSES: list[str] = []
PENDING = [
    "Ejecución del DDL en SQL Server 2022 (triggers, índices filtrados, FK compuestas): Fase 1 con Testcontainers (T47).",
    "Comportamiento concurrente de UX_PrintAttempt_PrinterInFlight y del claim bajo carga real (T02).",
    "Reglas entre tablas de TR_PrintAttempt_Guard (incluida ReadSerial = LabelSerial y nivel mínimo de evidencia), TR_ReprintAuthorization_Guard y TR_PrintJob_StateMachine (OperatorAction, intento excepcional, EvidenceObservation): Fase 1 (T47).",
    "Orden monotónico por impresora con PrinterSendOrder bajo concurrencia y eventos tardíos (T42).",
    "Detección de saltos del reloj de pared contra el reloj monotónico del EdgeHost (T37).",
    "Segunda comprobación monotónica antes de escribir y su límite ante pausas del proceso (T29).",
    "Detección de recirculación con UPDLOCK, HOLDLOCK sobre IX_PrintJob_Carton e IX_CartonDetection_Carton (T16).",
    "Durabilidad del journal SQLite ante corte de energía con synchronous=FULL (T09).",
    "Toda propiedad física: impresión, aplicación, verificación y ventana física (FAT).",
    "Latencias y capacidad: hipótesis de diseño sin benchmark (T12).",
]


def ok(group: str, msg: str):
    PASSES.append(f"[{group}] {msg}")


def err(group: str, msg: str):
    ERRORS.append(f"[{group}] {msg}")


def load(rel: str):
    return json.loads((ROOT / rel).read_text(encoding="utf-8"))


# ===================================================================== formatos JSON
RFC3339 = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")


def build_format_checker():
    from jsonschema import FormatChecker

    fc = FormatChecker(formats=())

    @fc.checks("uuid", raises=ValueError)
    def _uuid(value):
        if not isinstance(value, str):
            return True
        if str(uuid.UUID(value)) != value:
            raise ValueError("uuid no canónico")
        return True

    @fc.checks("date-time", raises=ValueError)
    def _dt(value):
        if not isinstance(value, str):
            return True
        if not RFC3339.match(value):
            raise ValueError("no es RFC 3339")
        dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        return True

    return fc


# ===================================================================== reglas de negocio
def parse_ts(s: str) -> dt.datetime:
    return dt.datetime.fromisoformat(s.replace("Z", "+00:00"))


def canonical_ts(value):
    """UTC truncado a milisegundos, YYYY-MM-DDTHH:MM:SS.fffZ (forma canónica de la huella)."""
    if value is None:
        return None
    d = parse_ts(value).astimezone(dt.timezone.utc)
    return d.strftime("%Y-%m-%dT%H:%M:%S.") + f"{d.microsecond // 1000:03d}Z"


def source_fingerprint(doc: dict) -> str:
    """SHA-256 del arreglo JSON compacto definido en state-machine.json (fingerprint)."""
    arr = [doc["siteId"], doc["stationId"], doc["plcEpoch"], doc["cycleId"],
           canonical_ts(doc.get("detectionTimestamp")), doc.get("workOrderId"), doc.get("cartonId")]
    canon = json.dumps(arr, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(canon.encode("utf-8")).hexdigest()


def business_errors(doc: dict, sm: dict) -> list[str]:
    """Reglas que JSON Schema no puede expresar. Devuelve la lista de violaciones."""
    out = []
    ns = uuid.UUID(sm["uuidNamespace"])
    key = f"{doc['siteId']}:{doc['stationId']}:{doc['plcEpoch']}:{doc['cycleId']}"
    if doc.get("idempotencyKey") != key:
        out.append("idempotencyKey no es siteId:stationId:plcEpoch:cycleId")
    detection_id = str(uuid.uuid5(ns, sm["uuidNames"]["CartonDetected"].format(idempotencyKey=key)))
    if doc["eventType"] == "CartonDetected":
        if doc["eventId"] != detection_id:
            out.append("eventId no es UUIDv5 de 'CartonDetected|' + idempotencyKey")
        if doc["correlationId"] != doc["eventId"]:
            out.append("correlationId distinto de eventId")
        expected_occurred = doc.get("detectionTimestamp", doc["receivedAt"])
        if doc["occurredAt"] != expected_occurred:
            out.append("occurredAt debe ser detectionTimestamp (o receivedAt si no existe)")
        if source_fingerprint(doc) != doc["sourceFingerprint"]:
            out.append("sourceFingerprint no corresponde al arreglo canónico de valores latched")
        live = doc["transport"] == "SUBSCRIPTION" and not doc["queueOverflow"]
        if doc["deadlineReference"] == "GATEWAY_RECEIPT" and not live:
            # sin cota de latencia no hay plazo: no se fabrica uno (D04 LATE_ARRIVAL)
            if doc["deadlineAt"] != doc["receivedAt"]:
                out.append("GATEWAY_RECEIPT sin entrega en vivo: deadlineAt debe ser receivedAt (plazo fabricado)")
        elif doc["deadlineReference"] == "PLC_TIMESTAMP" and "detectionTimestamp" not in doc:
            # registro incoherente (D05 INCOHERENT_SNAPSHOT): tampoco tiene plazo
            if doc["deadlineAt"] != doc["receivedAt"]:
                out.append("PLC_TIMESTAMP sin detectionTimestamp: deadlineAt debe ser receivedAt (plazo fabricado)")
        else:
            ref = doc["detectionTimestamp"] if doc["deadlineReference"] == "PLC_TIMESTAMP" else doc["receivedAt"]
            if parse_ts(doc["deadlineAt"]) <= parse_ts(ref):
                out.append("deadlineAt no es posterior a su referencia temporal")
    else:
        name = sm["uuidNames"]["PrintJobResult"].format(
            idempotencyKey=key, labelType=doc["labelType"], resultVersion=doc["resultVersion"])
        if doc["eventId"] != str(uuid.uuid5(ns, name)):
            out.append("eventId no es UUIDv5 de su nombre de resultado")
        if doc["correlationId"] != detection_id:
            out.append("correlationId no es el DetectionId")
        ev = doc.get("evidence")
        if ev and ev.get("level") == "VERIFIED":
            if doc["outcome"] == "CONFIRMED" and ev.get("readSerial") != doc.get("labelSerial"):
                out.append("CONFIRMED con readSerial distinto de labelSerial (I06)")
            if doc.get("reason") == "SERIAL_MISMATCH" and ev.get("readSerial") == doc.get("labelSerial"):
                out.append("SERIAL_MISMATCH con readSerial igual a labelSerial")
        co = sm["contractOutcomes"][doc["outcome"]]
        if doc["final"] != co["final"]:
            out.append("final no coincide con contractOutcomes")
    return out


def check_contracts(sm: dict):
    g = "contratos"
    try:
        from jsonschema import Draft202012Validator
    except ImportError:
        err(g, "jsonschema no instalado")
        return
    fc = build_format_checker()
    schemas = {}
    for name in ("carton-detected.v1.schema.json", "print-job-result.v1.schema.json"):
        s = load(f"contracts/{name}")
        Draft202012Validator.check_schema(s)
        schemas[name] = Draft202012Validator(s, format_checker=fc)
    ok(g, "2 schemas válidos como Draft 2020-12; formatos uuid y date-time verificados con validadores propios")

    def schema_for(doc):
        return "carton-detected.v1.schema.json" if doc.get("eventType") == "CartonDetected" else "print-job-result.v1.schema.json"

    valid_dir = ROOT / "contracts/examples/valid"
    outcomes_seen = set()
    n = 0
    for p in sorted(valid_dir.glob("*.json")):
        doc = json.loads(p.read_text(encoding="utf-8"))
        errs = [e.message for e in schemas[schema_for(doc)].iter_errors(doc)]
        if errs:
            err(g, f"válido falla el schema: {p.name}: {errs[:2]}")
        b = business_errors(doc, sm)
        if b:
            err(g, f"válido falla reglas de negocio: {p.name}: {b}")
        if doc.get("eventType") == "PrintJobResult":
            outcomes_seen.add(doc["outcome"])
        n += 1
    ok(g, f"{n} ejemplos válidos pasan schema y reglas de negocio")
    contract_outcomes = {k for k in sm["contractOutcomes"] if not k.startswith("$")}
    missing = contract_outcomes - outcomes_seen
    if missing:
        err(g, f"outcomes sin ejemplo válido: {sorted(missing)}")
    else:
        ok(g, "cada outcome del contrato tiene al menos un ejemplo válido")

    inv_dir = ROOT / "contracts/examples/invalid"
    expected = load("contracts/examples/invalid/EXPECTED.json")["cases"]
    files = {p.name for p in inv_dir.glob("*.json") if p.name != "EXPECTED.json"}
    if files != set(expected):
        err(g, f"invalid/ y EXPECTED.json no coinciden: {sorted(files ^ set(expected))}")
    for name, meta in expected.items():
        doc = json.loads((inv_dir / name).read_text(encoding="utf-8"))
        errs = list(schemas[meta["schema"]].iter_errors(doc))
        if not errs:
            err(g, f"inválido NO falla el schema: {name} ({meta['why']})")
            continue
        kinds = {(e.validator, "".join(f"/{x}" for x in e.absolute_path)) for e in errs}
        if kinds != {(meta["keyword"], meta["path"])}:
            err(g, f"inválido falla por otra regla: {name}: esperado {meta['keyword']} en '{meta['path']}', obtenido {sorted(kinds)}")
    ok(g, f"{len(expected)} ejemplos inválidos fallan el schema, cada uno solo por la regla declarada (T23)")

    bdir = ROOT / "contracts/examples/business-invalid"
    bexp = load("contracts/examples/business-invalid/EXPECTED.json")["cases"]
    for name, meta in bexp.items():
        doc = json.loads((bdir / name).read_text(encoding="utf-8"))
        if list(schemas[schema_for(doc)].iter_errors(doc)):
            err(g, f"negocio-inválido debería pasar el schema: {name}")
        be = business_errors(doc, sm)
        if be != [meta["error"]]:
            err(g, f"negocio-inválido no falla solo por su regla: {name}: esperado [{meta['error']}], obtenido {be}")
    ok(g, f"{len(bexp)} ejemplos pasan el schema y fallan exactamente una regla de negocio: el schema no basta (T23)")

    # enums del schema contra la máquina de estados
    rs = load("contracts/print-job-result.v1.schema.json")
    schema_outcomes = set(rs["properties"]["outcome"]["enum"])
    if schema_outcomes != contract_outcomes:
        err(g, f"outcome del schema distinto de contractOutcomes: {sorted(schema_outcomes ^ contract_outcomes)}")
    per_outcome = {}
    for rule in rs["allOf"]:
        cond = rule.get("if", {}).get("properties", {})
        then = rule.get("then", {}).get("properties", {})
        if ("outcome" in cond and "const" in cond["outcome"] and "reason" in then and "reason" not in cond
                and "required" not in rule.get("if", {})):
            r = then["reason"]
            per_outcome[cond["outcome"]["const"]] = set(r.get("enum", [r.get("const")]))
    sm_reasons = {}
    for mname, m in sm["machines"].items():
        for state, reasons in m.get("reasons", {}).items():
            outcome = m["resultOutcome"].get(state)
            if outcome:
                sm_reasons.setdefault(outcome, set()).update(reasons)
    for outcome, reasons in sm_reasons.items():
        if per_outcome.get(outcome) != reasons:
            err(g, f"reasons de {outcome}: schema {sorted(per_outcome.get(outcome, []))} vs máquina {sorted(reasons)}")
    all_reasons = set().union(*sm_reasons.values())
    if set(rs["properties"]["reason"]["enum"]) != all_reasons:
        err(g, "enum global de reason distinto de la unión de motivos de la máquina de estados")
    ok(g, "outcomes y motivos del schema coinciden con contracts/state-machine.json")


# ===================================================================== máquina de estados
def check_state_machine(sm: dict):
    g = "estados"
    ids = []
    for mname, m in sm["machines"].items():
        states = m["states"]
        tr = m["transitions"]
        ids += [t["id"] for t in tr]
        for t in tr:
            for f in t["from"]:
                if f != "(create)" and f not in states:
                    err(g, f"{mname}.{t['id']}: origen desconocido {f}")
            if t["to"] not in states:
                err(g, f"{mname}.{t['id']}: destino desconocido {t['to']}")
            if t["mode"] not in sm["modes"]:
                err(g, f"{mname}.{t['id']}: modo desconocido {t['mode']}")
        creates = [t for t in tr if "(create)" in t["from"]]
        if len(creates) != 1 or creates[0]["to"] != m["initial"]:
            err(g, f"{mname}: debe haber exactamente una creación hacia {m['initial']}")
        # alcanzabilidad
        reach, frontier = {m["initial"]}, [m["initial"]]
        while frontier:
            s = frontier.pop()
            for t in tr:
                if s in t["from"] and t["to"] not in reach:
                    reach.add(t["to"])
                    frontier.append(t["to"])
        # una corrección solo reduce certeza: siempre termina en UNKNOWN
        for t in tr:
            if t["mode"] == "correction" and t["to"] != "UNKNOWN":
                err(g, f"{mname}.{t['id']}: una corrección debe terminar en UNKNOWN")
        if set(states) - reach:
            err(g, f"{mname}: estados inalcanzables {sorted(set(states) - reach)}")
        # terminales: solo salidas excepcionales o de corrección; no terminales: alguna salida
        for s, info in states.items():
            outs = [t for t in tr if s in t["from"] and t["to"] != s]
            if info["terminal"] is True and [t for t in outs if t["mode"] not in ("exception", "correction")]:
                err(g, f"{mname}: estado terminal {s} tiene salidas ordinarias")
            if info["terminal"] is False and not info.get("mayRemain") and not outs:
                err(g, f"{mname}: estado no terminal {s} sin salidas")
            # terminal condicional: cada salida ordinaria debe declarar la condición en su guarda
            if info["terminal"] == "conditional":
                cond_word = info["terminalWhen"].split("=")[-1].split()[0]
                for t in outs:
                    if t["mode"] not in ("exception", "correction") and cond_word not in t["guard"]:
                        err(g, f"{mname}.{t['id']}: sale de {s}, terminal si {info['terminalWhen']}, sin esa condición en la guarda")
        # prohibidas
        allowed = {(f, t["to"]) for t in tr for f in t["from"]}
        for fb in m.get("forbidden", []):
            if (fb["from"], fb["to"]) in allowed:
                err(g, f"{mname}: transición prohibida presente {fb['from']} → {fb['to']}")
        for st in m.get("reasons", {}):
            if st not in states:
                err(g, f"{mname}: motivos para estado inexistente {st}")
        for st, out in m["resultOutcome"].items():
            if st not in states:
                err(g, f"{mname}: resultOutcome de estado inexistente {st}")
            if out and out not in sm["contractOutcomes"]:
                err(g, f"{mname}: outcome {out} no está en contractOutcomes")
    if len(ids) != len(set(ids)):
        err(g, "IDs de transición repetidos")
    # reglas específicas de la revisión 2
    pj = sm["machines"]["PrintJob"]
    ordinary_unknown = [t for t in pj["transitions"] if "UNKNOWN" in t["from"] and t["to"] in ("SENDING", "READY", "RETRY_WAIT")]
    if ordinary_unknown:
        err(g, "existe una salida de UNKNOWN hacia el flujo de envío")
    ok(g, f"{len(sm['machines'])} máquinas coherentes: alcanzables, terminales cerrados, prohibidas ausentes, {len(ids)} transiciones únicas")


# ===================================================================== DDL
CHECK_IN = re.compile(r"CONSTRAINT\s+(CK_\w+)\s+CHECK\s*\(\s*(\w+)\s+IN\s*\(([^)]*)\)\s*\)", re.I)
CHECK_BODY = re.compile(r"CONSTRAINT\s+(CK_\w+)\s+CHECK\s*\((.*?)\)\s*(?:,\s*\n|\n\);)", re.S | re.I)


def sql_list(s: str) -> set[str]:
    return set(re.findall(r"'([^']*)'", s))


def check_sql(sm: dict, skip_sqlfluff: bool):
    g = "sql"
    ddl = (ROOT / "database/sqlserver_schema.sql").read_text(encoding="utf-8")
    checks = {name: (col, sql_list(vals)) for name, col, vals in CHECK_IN.findall(ddl)}
    for mname, m in sm["machines"].items():
        table = m["table"].split(".")[1]
        cname = f"CK_{table}_{m['column']}"
        if cname not in checks:
            err(g, f"falta {cname}")
            continue
        if checks[cname][1] != set(m["states"]):
            err(g, f"{cname} {sorted(checks[cname][1])} distinto de estados {sorted(m['states'])}")
        if m.get("reasons"):
            rname = f"CK_{table}_Reason"
            body_text = ddl[ddl.find(rname):ddl.find("CONSTRAINT", ddl.find(rname) + len(rname))]
            for state, reasons in m["reasons"].items():
                mm = re.search(rf"Status = '{state}' AND Reason IS NOT NULL AND Reason IN \(([^)]*)\)", body_text)
                if not mm or sql_list(mm.group(1)) != set(reasons):
                    err(g, f"{rname}: motivos de {state} no coinciden con la máquina de estados")
    # PriorJobStatus de autorizaciones = orígenes de X01 y X02
    x_from = {f for t in sm["machines"]["PrintJob"]["transitions"] if t["mode"] == "exception" for f in t["from"]}
    if checks.get("CK_ReprintAuthorization_PriorStatus", (None, set()))[1] != x_from:
        err(g, "CK_ReprintAuthorization_PriorStatus distinto de los orígenes de las transiciones excepcionales")
    required = [
        "UQ_CartonDetection_Identity", "UQ_CartonDetection_IdempotencyKey", "UQ_PrintJob_DetectionLabel",
        "UQ_PrintJob_LabelSerial", "UX_PrintAttempt_PrinterInFlight", "UX_PrintAttempt_JobOpen",
        "UX_PrintAttempt_OrdinarySent", "UX_PrintAttempt_Authorization", "UX_ReprintAuthorization_OpenPerJob",
        "UQ_OutboxEvent_EventId", "PK_InboxMessage", "FK_CartonDetection_Epoch", "FK_PrintAttempt_Authorization",
        "TR_StationConfigRevision_Immutable", "TR_PrintJob_Immutable", "TR_PrintAttempt_Immutable",
        "TR_CartonDetection_Immutable", "TR_ReprintAuthorization_Immutable", "TR_OperatorAction_Immutable",
        "TR_EvidenceObservation_Immutable", "TR_PrintAttempt_Guard", "TR_ReprintAuthorization_Guard",
        "CK_ReprintAuthorization_TwoPersons", "CK_PrintAttempt_NotSent", "CK_EvidenceObservation_Level",
        "CK_OperatorAction_Target", "CK_StationConfigRevision_Times", "IX_CartonDetection_Carton",
        "dbo.OperatorAction", "dbo.PrinterSendOrder", "dbo.EvidenceObservation", "LabelSerialSeq",
        "DeviceLeadTimeMs", "ManualPrinterId", "LastOperatorActionId", "LastExceptionalAttemptId",
        "TR_StationConfig_Guard", "TR_PlcEpochHistory_Immutable", "TR_PlcEpochHistory_Guard",
        "TR_PrinterSendOrder_Monotonic", "UX_PrintAttempt_ConfirmingObservation", "CK_CartonDetection_GatewayLive",
        "CK_CartonDetection_PlcTimestamp", "Latin1_General_100_BIN2",
    ]
    for name in required:
        if name not in ddl:
            err(g, f"falta objeto requerido {name}")
    head = "\n".join(ddl.splitlines()[:40])
    for opt in ("SET ANSI_NULLS ON", "SET QUOTED_IDENTIFIER ON", "SET ANSI_PADDING ON", "SET ANSI_WARNINGS ON",
                "SET ARITHABORT ON", "SET CONCAT_NULL_YIELDS_NULL ON", "SET NUMERIC_ROUNDABORT OFF"):
        if opt not in head:
            err(g, f"el DDL no fija {opt} al inicio (índices filtrados, error 1934)")
    # triggers AFTER ... DELETE: un UPDATE de 0 filas también los dispara
    triggered = set()
    for name, table, body in re.findall(r"CREATE TRIGGER dbo\.(\w+)\s+ON dbo\.(\w+)(.*?)\nGO", ddl, re.S):
        triggered.add(table)
        if "DELETE" in body.split("AS", 1)[0]:
            if "IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;" not in body:
                err(g, f"{name}: falta la guarda de 0 filas antes de THROW")
    # sentencias de referencia
    ops = (ROOT / "database/sqlserver_operations_reference.sql").read_text(encoding="utf-8")
    ops_code = "\n".join(l for l in ops.splitlines() if not l.lstrip().startswith("--"))
    for stmt in ops_code.split(";"):
        m = re.match(r"\s*(?:UPDATE|INSERT INTO|DELETE FROM|MERGE)\s+dbo\.(\w+)", stmt)
        if not m:
            continue
        out = re.search(r"\bOUTPUT\b(.*?)(\bWHERE\b|\bVALUES\b|\bFROM\b|\bSELECT\b|$)", stmt, re.S)
        if out and m.group(1) in triggered and "INTO" not in out.group(1).upper():
            err(g, f"OUTPUT sin INTO sobre dbo.{m.group(1)}, que tiene triggers (Msg 334)")
        if stmt.lstrip().upper().startswith("INSERT") and "NEXT VALUE FOR" in stmt.upper():
            err(g, f"NEXT VALUE FOR dentro de un INSERT en dbo.{m.group(1)}: el serial debe existir antes del payload")
    for mname, m in sm["machines"].items():
        table = m["table"].split(".")[1]
        trig = f"TR_{table}_StateMachine"
        start = ddl.find(f"CREATE TRIGGER dbo.{trig}")
        seg = ddl[start:ddl.find("\nGO", start)] if start >= 0 else ""
        if not seg or f"t.Machine = '{mname}'" not in seg:
            err(g, f"falta {trig} ligado a la máquina {mname}")
    # CHECK sin subconsultas (no pueden validar otras tablas)
    for name, body in re.findall(r"CONSTRAINT\s+(CK_\w+)\s+CHECK\s*\((.*?)\)\s*[,\n]", ddl, re.S):
        if re.search(r"\bSELECT\b", body, re.I):
            err(g, f"{name} contiene una subconsulta")
    # Seed generado = transiciones de la máquina
    seed = (ROOT / "database/sqlserver_state_transitions.sql").read_text(encoding="utf-8")
    rows = set(re.findall(r"\('(\w+)', '([^']+)', '(\w+)', '(AUTO|MANUAL|EXCEPTION|CORRECTION)', '(\w+)'\)", seed))
    expected_rows = {(mn, f, t["to"], t["mode"].upper(), t["id"])
                     for mn, m in sm["machines"].items() for t in m["transitions"] for f in t["from"]}
    kinds = {k.upper() for k in sm["modes"]}
    if checks.get("CK_AllowedTransition_Kind", (None, set()))[1] != kinds:
        err(g, "CK_AllowedTransition_Kind no coincide con los modos de la máquina de estados")
    if rows != expected_rows:
        err(g, f"sqlserver_state_transitions.sql no coincide con la máquina: {len(rows ^ expected_rows)} diferencias")
    ok(g, "CHECK de estados y motivos, objetos requeridos, opciones SET, guardas de 0 filas, OUTPUT INTO, triggers por máquina y semilla de transiciones coinciden con la fuente")
    # sqlfluff
    if skip_sqlfluff:
        PENDING.append("Parseo T-SQL con sqlfluff omitido por --skip-sqlfluff.")
    else:
        for rel in ("database/sqlserver_schema.sql", "database/sqlserver_state_transitions.sql",
                    "database/sqlserver_operations_reference.sql"):
            res = subprocess.run([sys.executable, "-m", "sqlfluff", "parse", rel, "--config", "tools/sqlfluff.cfg",
                                  "--format", "yaml"], cwd=ROOT, capture_output=True, text=True)
            if res.returncode not in (0, 1) or "unparsable" in res.stdout or not res.stdout.strip() or res.stdout.strip() == "[]":
                err(g, f"sqlfluff no pudo parsear {rel} por completo")
        ok(g, "sqlfluff (dialecto tsql) parsea los 3 archivos de SQL Server sin segmentos no reconocidos")
    # SQLite: ejecución real
    con = sqlite3.connect(":memory:")
    con.executescript((ROOT / "database/sqlite_journal.sql").read_text(encoding="utf-8"))
    pk = [r[1] for r in con.execute("PRAGMA table_info(CapturedCycle)") if r[5] > 0]
    if pk != ["SiteId", "StationId", "PlcEpoch", "CycleId"]:
        err(g, f"PK del journal {pk}")
    cols = ("SiteId, StationId, PlcEpoch, CycleId, IdempotencyKey, EventId, SourceFingerprint, Transport, "
            "QueueOverflow, ReceivedAt, ConfigRevisionId, RawRecord, CapturedAt")

    def ins(cycle, key, event, fp):
        con.execute(f"INSERT INTO CapturedCycle ({cols}) VALUES ('P','L',1,?,?,?,?,'SUBSCRIPTION',0,'t',7,'{{}}','t')",
                    (cycle, key, event, fp))

    ins("10", "P:L:1:10", "e1", "a" * 64)
    try:
        ins("10", "P:L:1:10b", "e2", "b" * 64)
        err(g, "el journal acepta dos filas con la misma identidad")
    except sqlite3.IntegrityError:
        pass
    for bad in ("12abc", "", "123456789012345678901", "007", "18446744073709551616"):
        try:
            ins(bad, f"k{bad}", f"e{bad}", "c" * 64)
            err(g, f"el journal acepta CycleId no canónico {bad!r}")
        except sqlite3.IntegrityError:
            pass
    ok(g, "sqlite_journal.sql ejecutado en sqlite3: PK (SiteId, StationId, PlcEpoch, CycleId) rechaza duplicados y CycleId no canónico (texto, vacío, ceros a la izquierda, mayor que UInt64)")


# ===================================================================== pruebas, invariantes, garantías
EXECUTED_HERE = {"T23", "T28"}


def check_traceability(sm: dict):
    g = "trazabilidad"
    tc = load("tests/test-catalog.json")["tests"]
    inv = load("contracts/invariants.json")["invariants"]
    gu = load("docs/guarantees.json")["guarantees"]
    od = {d["id"] for d in load("docs/open-decisions.json")["decisions"]}
    test_ids = [t["id"] for t in tc]
    if len(test_ids) != len(set(test_ids)):
        err(g, "IDs de prueba repetidos")
    tids = set(test_ids)
    trans = {t["id"] for m in sm["machines"].values() for t in m["transitions"]}
    covered = {tr for t in tc for tr in t["transitions"]}
    if trans - covered:
        err(g, f"transiciones sin prueba: {sorted(trans - covered)}")
    if covered - trans:
        err(g, f"pruebas citan transiciones inexistentes: {sorted(covered - trans)}")
    inv_ids = {i["id"] for i in inv}
    for i in inv:
        if not i["tests"] or set(i["tests"]) - tids:
            err(g, f"{i['id']} sin pruebas válidas")
    for t in tc:
        if set(t["invariants"]) - inv_ids:
            err(g, f"{t['id']} cita invariantes inexistentes")
        for f in ("preconditions", "initialData", "injection", "expectedStates", "publishedEvents",
                  "physicalEffects", "metrics", "passCriteria"):
            if not t.get(f):
                err(g, f"{t['id']} sin campo {f}")
        if t["status"] == "EJECUTADA_EN_REVISION" and t["id"] not in EXECUTED_HERE:
            err(g, f"{t['id']} marcada como ejecutada sin que este verificador la ejecute (I13)")
    for r in gu:
        if set(r["tests"]) - tids:
            err(g, f"{r['id']} cita pruebas inexistentes")
        if r["class"] == "FISICA" and (not r["hardware"] or r["hardware"].startswith("Ninguna")):
            err(g, f"{r['id']} es física y no declara dependencia de hardware (I12)")
        for ref in re.findall(r"OT-\d{2}", r["hardware"] + r["conditions"]):
            if ref not in od:
                err(g, f"{r['id']} cita {ref} inexistente")
    plan = (ROOT / "docs/PLAN_IMPLEMENTACION.md").read_text(encoding="utf-8")
    in_plan = re.findall(r"\bT\d{2}\b", plan)
    if set(in_plan) != tids or len(in_plan) != len(set(in_plan)):
        err(g, f"el plan por fases no asigna cada prueba exactamente una vez: faltan {sorted(tids - set(in_plan))}")
    ok(g, f"{len(trans)} transiciones y {len(inv_ids)} invariantes cubiertos por {len(tids)} pruebas; cada prueba asignada a una fase; garantías físicas con hardware declarado")


# ===================================================================== artefactos generados
def check_generated():
    g = "generados"
    import generate_artifacts as ga
    stale = ga.run(check=True)
    if stale:
        for s in stale:
            err(g, f"desactualizado: {s}")
    else:
        ok(g, "diagramas de estados, semilla SQL, matriz de pruebas y bloques de documentos al día con sus fuentes")


EDGE = re.compile(r"^\s*(\[\*\]|\w+)\s*-->\s*(\w+)\s*:\s*(\w+)\b", re.M)


def check_diagrams(sm: dict):
    g = "diagramas"
    import generate_artifacts as ga
    for fname, mname, modes in ga.STATE_DIAGRAMS:
        text = (ROOT / "diagrams" / fname).read_text(encoding="utf-8")
        edges = {( "(create)" if a == "[*]" else a, b, i) for a, b, i in EDGE.findall(text)}
        exp = {(f, t["to"], t["id"]) for t in sm["machines"][mname]["transitions"] if t["mode"] in modes for f in t["from"]}
        if edges != exp:
            err(g, f"{fname}: aristas distintas de la máquina ({len(edges ^ exp)} diferencias)")
    # Cada SVG y PNG se generó desde la versión actual de su .mmd: lo prueba el manifiesto que
    # escribe tools/render_diagrams.py. No se usan fechas de archivo: git y los descompresores no
    # conservan el orden de modificación.
    import render_diagrams as rd
    manifest = rd.load_manifest()
    for mmd in sorted((ROOT / "diagrams").glob("*.mmd")):
        for ext in (".svg", ".png"):
            if not mmd.with_suffix(ext).exists():
                err(g, f"falta {mmd.with_suffix(ext).name}")
        if manifest.get(mmd.name) != rd.source_hash(mmd):
            err(g, f"{mmd.name} cambió después de renderizarse: correr tools/render_diagrams.py")
    if set(manifest) - {m.name for m in (ROOT / "diagrams").glob("*.mmd")}:
        err(g, "el manifiesto de render lista diagramas que ya no existen")
    if list((ROOT / "diagrams").glob("*.dot")):
        err(g, "quedan archivos .dot: Mermaid es la única fuente")
    ok(g, "diagramas de estados con las mismas aristas que la máquina; cada .mmd tiene SVG y PNG renderizados desde su versión actual")


# ===================================================================== documentación
DEPRECATED = ["POTENTIALLY_SENT", "PRINTER_JOB_ACK", "NOT_OBSERVED", "03_estados.", ".dot`"]
ALLOW_TOKENS = {
    "UPDLOCK", "HOLDLOCK", "ISJSON", "SEQUENCE", "CACHE", "THROW", "XACT_ABORT", "SESSION_CONTEXT",
    "OUTPUT", "UNIQUE", "CHECK", "NULL", "TRUE", "FALSE", "MUST", "UTC", "JSON", "SQL", "OPC", "ERP", "PLC",
    "ACK", "TCP", "ZPL", "SGD", "UUID", "SHA", "FIFO", "MVP", "FAT", "SAT", "HMI", "UNS", "GS1", "SSCC",
    "GTIN", "NTP", "PTP", "DONE", "ADR", "TX", "OBSERVED", "RECOVERED", "SUBSCRIPTION", "PLC_FIFO",
    "JOURNAL_REPLAY", "READ", "ORDINARY", "EXCEPTIONAL_REPRINT", "EXCEPTION", "SYSTEM", "INITIAL",
    "AUTO_INCREMENT_BY_ONE", "REQUIRE_CONFIRMATION", "OPERATOR_CONFIRMED", "CYCLE_ONLY", "CARTON_ID_REQUIRED",
    "PLC_TIMESTAMP", "GATEWAY_RECEIPT", "NONE", "PRINTED", "APPLIED", "VERIFIED", "JOB", "CYCLE", "HELD",
    "NONE_PROVEN", "ACCEPTED", "REPLACES_EXISTING_LABEL", "UNKNOWN_NOT_RESOLVED", "PROVEN_NOT_PRINTED",
    "EXPIRED_CARTON_HELD", "CLOSED_CARTON_RECOVERED", "LABEL_DAMAGED", "PRINTER_JOB_COMPLETION",
    "PLC_APPLICATOR_CONFIRMATION", "BARCODE_VERIFICATION", "MANUAL_INSPECTION", "IDENTITY_COLLISION",
    "RESET_SUSPECTED", "EPOCH_UNEXPECTED", "AMBIGUOUS_DECREASE", "CLOCK_SKEW", "PRINTER_OWNERSHIP", "OPERATOR",
    "BLOCKING", "WARNING", "GATEWAY", "NO_EJECUTADA", "EJECUTADA_EN_REVISION", "UNIT", "INTEGRATION",
    "FAULT", "LOAD", "DOC", "SOFTWARE", "FISICA", "RENDIMIENTO", "SUPUESTO_OT", "GO", "WAL", "FULL",
    "SQLITE", "UPDATE", "INSERT", "DELETE", "SELECT", "COMMIT", "ROLLBACK", "TOP", "IN", "AND", "OR",
    "NOT", "EXISTS", "CASE", "SET", "TLS", "IDENTITY", "ROWVERSION", "DATETIME2", "NVARCHAR", "BIGINT",
    "BINARY", "AFTER", "XOR", "LR", "API", "EDGE", "CPU", "RAM", "AUTO", "PENDING",
    "TRY", "CATCH", "INTO", "ANSI_NULLS", "QUOTED_IDENTIFIER", "ANSI_PADDING", "ANSI_WARNINGS", "ARITHABORT",
    "CONCAT_NULL_YIELDS_NULL", "NUMERIC_ROUNDABORT", "NOCOUNT", "RETURN",
}


def known_vocabulary(sm: dict) -> set[str]:
    vocab = set(ALLOW_TOKENS)
    for m in sm["machines"].values():
        vocab.update(m["states"])
        for rs in m.get("reasons", {}).values():
            vocab.update(rs)
    vocab.update(sm["contractOutcomes"])
    ddl = (ROOT / "database/sqlserver_schema.sql").read_text(encoding="utf-8")
    vocab.update(re.findall(r"'([A-Z][A-Z0-9_]+)'", ddl))
    return vocab


# Rutas que el agente de implementación crea en la Fase 0; se citan antes de existir.
FUTURE_PATHS = {"docs/IMPLEMENTATION_LOG.md"}
PATH_RE = re.compile(r"(?<![\w/.\-])((?:docs|contracts|database|diagrams|tools|tests|prompts|references|documento)/[^\s`'\")\]<>,;|]+)")

ADR_SECTIONS = ["## Contexto", "## Problema", "## Alternativas", "## Decisión", "## Justificación",
                "## Consecuencias", "## Riesgos", "## Pruebas", "## Revisar si"]


def check_docs(sm: dict):
    g = "documentos"
    vocab = known_vocabulary(sm)
    md_files = [p for p in ROOT.rglob("*.md") if "historial" not in p.parts]
    for p in md_files:
        text = p.read_text(encoding="utf-8")
        rel = p.relative_to(ROOT).as_posix()
        historical = p.name in ("CHANGELOG.md", "REVISION_LOG_v1.1.md")  # pueden citar términos retirados
        if not historical:
            for dep in DEPRECATED:
                if dep in text:
                    err(g, f"{rel}: término retirado {dep!r}")
            for tok in set(re.findall(r"`([A-Z][A-Z0-9_]{2,})`", text)):
                if tok not in vocab:
                    err(g, f"{rel}: token de estado desconocido `{tok}`")
        for link in re.findall(r"\]\((?!https?://|#|mailto:)([^)\s]+)\)", text):
            target = (p.parent / link.split("#")[0]).resolve()
            if not target.exists():
                err(g, f"{rel}: enlace roto {link}")
    # rutas citadas en texto (entre comillas invertidas o en comentarios) que no existen
    names = {q.name for q in ROOT.rglob("*")}
    scan = [q for q in ROOT.rglob("*") if q.suffix in (".md", ".sql", ".json", ".py", ".mmd", ".sh")
            and "historial" not in q.parts and q.name not in ("CHANGELOG.md", "REVISION_LOG_v1.1.md")]
    cited = 0
    for q in scan:
        text = q.read_text(encoding="utf-8")
        for ref in PATH_RE.findall(text):
            ref = ref.rstrip(".:")
            if any(c in ref for c in "*{<") or ref in FUTURE_PATHS:
                continue
            cited += 1
            if not (ROOT / ref).exists():
                err(g, f"{q.relative_to(ROOT).as_posix()}: cita una ruta inexistente {ref}")
        for ref in re.findall(r"`([\w.\-]+\.(?:md|json|sql|py|sh|mmd|sha256|cfg|lua))`", text):
            cited += 1
            if ref not in names:
                err(g, f"{q.relative_to(ROOT).as_posix()}: cita un archivo inexistente {ref}")
    for rel in ("README.md", "DOCUMENTO_PRINCIPAL.md", "AI_CONTEXT.md"):
        if "1.1" not in (ROOT / rel).read_text(encoding="utf-8")[:800]:
            err(g, f"{rel} no declara la versión 1.1 al inicio")
    contexts = {}
    adrs = sorted((ROOT / "docs/adr").glob("*.md"))
    for p in adrs:
        text = p.read_text(encoding="utf-8")
        for sec in ADR_SECTIONS:
            if sec not in text:
                err(g, f"{p.name}: falta sección {sec}")
        m = re.search(r"## Contexto\n+(.*?)\n## ", text, re.S)
        if m:
            ctx = m.group(1).strip()
            if ctx in contexts:
                err(g, f"{p.name}: contexto idéntico a {contexts[ctx]}")
            contexts[ctx] = p.name
    ok(g, f"{len(md_files)} documentos sin términos retirados ni estados desconocidos; {len(adrs)} ADR con secciones completas y contextos distintos; enlaces internos y {cited} rutas citadas existen")


# ===================================================================== checksums
def check_checksums():
    g = "checksums"
    path = ROOT / "CHECKSUMS.sha256"
    if not path.exists():
        err(g, "falta CHECKSUMS.sha256")
        return
    listed = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            digest, name = line.split(None, 1)
            listed[name.strip().lstrip("*")] = digest
    actual = {}
    for p in ROOT.rglob("*"):
        if p.is_file() and ".git" not in p.parts and p.name != "CHECKSUMS.sha256" and "__pycache__" not in p.parts:
            # rutas con "/" en cualquier sistema: en Windows str() daría "contracts\\README.md"
            actual[p.relative_to(ROOT).as_posix()] = hashlib.sha256(p.read_bytes()).hexdigest()
    # Nombres solo ASCII: el descompresor de Windows y otras herramientas interpretan los
    # demás con otra página de códigos y producen nombres distintos (as├¡ncrono en lugar de asíncrono).
    non_ascii = sorted(n for n in actual if not n.isascii())
    if non_ascii:
        err(g, f"nombres de archivo con caracteres no ASCII: {non_ascii[:8]}")
    missing, extra = sorted(set(listed) - set(actual)), sorted(set(actual) - set(listed))
    if missing:
        err(g, f"{len(missing)} archivos listados que faltan (copia incompleta): {missing[:8]}")
    if extra:
        err(g, f"{len(extra)} archivos que no están en CHECKSUMS.sha256 (agregados después; regenerar con tools/update_checksums.py si el cambio es intencional): {extra[:8]}")
    bad = [n for n in listed if n in actual and listed[n] != actual[n]]
    if bad:
        err(g, f"hash distinto: {bad[:8]}")
    if not bad and set(listed) == set(actual):
        ok(g, f"{len(actual)} archivos con hash correcto y nombre ASCII; CHECKSUMS.sha256 excluido de sí mismo")


# ===================================================================== main
def main():
    skip_ck = "--skip-checksums" in sys.argv
    skip_fluff = "--skip-sqlfluff" in sys.argv
    sm = load("contracts/state-machine.json")
    steps = [
        ("estados", lambda: check_state_machine(sm)),
        ("contratos", lambda: check_contracts(sm)),
        ("sql", lambda: check_sql(sm, skip_fluff)),
        ("trazabilidad", lambda: check_traceability(sm)),
        ("generados", check_generated),
        ("diagramas", lambda: check_diagrams(sm)),
        ("documentos", lambda: check_docs(sm)),
    ]
    if not skip_ck:
        steps.append(("checksums", check_checksums))
    for name, fn in steps:
        before_err, before_ok = len(ERRORS), len(PASSES)
        try:
            fn()
        except Exception as exc:  # un fallo del verificador es un error, no un éxito
            err(name, f"excepción: {exc!r}")
        if len(ERRORS) > before_err:
            # un grupo con errores no reporta sus OK
            del PASSES[before_ok:]
    print("CartonPrinting v1.1 · verify_package.py")
    print("=" * 72)
    for p in PASSES:
        print("OK     ", p)
    for e in ERRORS:
        print("ERROR  ", e)
    print("-" * 72)
    print("PENDIENTE DE INTEGRACIÓN (no verificable de forma estática):")
    for p in PENDING:
        print("  -", p)
    print("-" * 72)
    print(f"Resultado: {len(PASSES)} grupos OK, {len(ERRORS)} errores")
    sys.exit(1 if ERRORS else 0)


if __name__ == "__main__":
    main()
