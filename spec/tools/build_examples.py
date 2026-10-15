#!/usr/bin/env python3
"""Regenera contracts/examples/ (válidos, inválidos y de negocio) con sus campos derivados.

eventId, correlationId, sourceFingerprint y deadlineAt se calculan con las mismas funciones que
usa tools/verify_package.py. Cada inválido se construye mutando un válido y el script exige que
falle por una sola regla de JSON Schema; cada ejemplo de negocio, por una sola regla de negocio.
Los valores de configuración de ejemplo (revisiones 3, 5, 7 y 12) son ficticios.

Uso: python3 tools/build_examples.py   (en Windows: py tools\\build_examples.py)
"""
import copy, datetime as dt, json, shutil, sys, uuid
from pathlib import Path
ROOT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
sys.dont_write_bytecode = True
sys.path.insert(0, str(ROOT / "tools"))
import verify_package as vp

sm = json.loads((ROOT / "contracts/state-machine.json").read_text(encoding="utf-8"))
NS = uuid.UUID(sm["uuidNamespace"])
EX = ROOT / "contracts/examples"

def ts(s): return vp.parse_ts(s)
def iso(d): return vp.canonical_ts(d.isoformat().replace("+00:00", "Z"))
def plus(s, ms): return iso(ts(s) + dt.timedelta(milliseconds=ms))
def key(site, st, ep, cy): return f"{site}:{st}:{ep}:{cy}"
def det_id(k): return str(uuid.uuid5(NS, f"CartonDetected|{k}"))
def res_id(k, lt, v): return str(uuid.uuid5(NS, f"PrintJobResult|{k}|{lt}|{v}"))

# Revisiones de ejemplo (ConfigRevisionId): 7 = LINE-01 VERIFIED, 3 = LINE-02 NONE,
# 12 = LINE-03 GATEWAY_RECEIPT, 5 = LINE-04 APPLIED. TransitBudget 1500, DeviceLead 400,
# ClockSkew 50 (PLC_TIMESTAMP) o MaxUpstream 40 (GATEWAY_RECEIPT).
def plc_deadline(t): return plus(t, 1500 - 400 - 50)
def gw_deadline(r): return plus(r, -40 + 1500 - 400)

def carton(site, st, ep, cy, rev, origin, transport, t, r, wo, ref="PLC_TIMESTAMP", overflow=False, deadline=None):
    k = key(site, st, ep, cy)
    d = {"schemaVersion": 1, "eventType": "CartonDetected", "eventId": det_id(k), "correlationId": det_id(k),
         "occurredAt": t or r, "siteId": site, "stationId": st, "plcEpoch": ep, "cycleId": cy, "idempotencyKey": k,
         "detectionOrigin": origin, "transport": transport, "queueOverflow": overflow}
    if t: d["detectionTimestamp"] = t
    d["receivedAt"] = r
    d["deadlineReference"] = ref
    if deadline is None:
        live = transport == "SUBSCRIPTION" and not overflow
        deadline = plc_deadline(t) if ref == "PLC_TIMESTAMP" else (gw_deadline(r) if live else r)
    d["deadlineAt"] = deadline
    d["configRevision"] = rev
    d["workOrderId"] = wo
    d["sourceFingerprint"] = vp.source_fingerprint(d)
    d["opc"] = {"nodeId": f"ns=2;s={st}.CartonCycleRecord", "statusCode": "Good"}
    if t: d["opc"]["sourceTimestamp"] = t
    return d

L1 = dict(site="PLANT-01", st="LINE-01", ep=42, rev=7, printer="PRN-LINE-01", wo="WO-2026-4815",
          product="SKU-10245", lot="LOT-20261012-A", lt="CARTON_GS1")

def result(base, cy, v, outcome, at, job=None, serial=None, attempt=None, reason=None, **extra):
    k = key(base["site"], base["st"], base["ep"], cy)
    d = {"schemaVersion": 1, "eventType": "PrintJobResult", "eventId": res_id(k, base["lt"], v),
         "correlationId": det_id(k), "occurredAt": at, "siteId": base["site"], "stationId": base["st"],
         "plcEpoch": base["ep"], "cycleId": cy, "idempotencyKey": k, "labelType": base["lt"], "resultVersion": v,
         "outcome": outcome, "final": outcome != "UNKNOWN", "configRevision": base["rev"]}
    if reason: d["reason"] = reason
    if job:
        d.update({"printJobId": job, "labelSerial": serial, "workOrderId": base["wo"], "productId": base["product"],
                  "lotId": base["lot"], "printerId": base["printer"]})
    if attempt: d["attemptId"] = attempt
    d.update(extra)
    return d

J = lambda n: f"0d7f3c2e-91b4-4a5d-b8e6-2f1a7c9d4e{n:02x}"
A = lambda n: f"ab17ad7b-afc5-4e3d-a38a-bd0dbb286d{n:02x}"
S = lambda n: f"P01L01-000000{n:06d}"

valid = {}
valid["carton-detected-observed.json"] = carton("PLANT-01", "LINE-01", 42, "918273", 7, "OBSERVED", "SUBSCRIPTION",
    "2026-10-12T18:04:11.204Z", "2026-10-12T18:04:11.221Z", "WO-2026-4815")
valid["carton-detected-recovered-fifo.json"] = carton("PLANT-01", "LINE-01", 42, "918260", 7, "RECOVERED", "PLC_FIFO",
    "2026-10-12T18:03:55.010Z", "2026-10-12T18:04:40.502Z", "WO-2026-4815")
valid["carton-detected-gateway-republish.json"] = carton("PLANT-01", "LINE-03", 7, "55120", 12, "RECOVERED", "REPUBLISH",
    None, "2026-10-12T18:06:02.415Z", "WO-2026-4900", ref="GATEWAY_RECEIPT")

valid["carton-detected-plc-without-timestamp.json"] = carton("PLANT-01", "LINE-01", 42, "918283", 7, "OBSERVED", "SUBSCRIPTION",
    None, "2026-10-12T18:05:31.402Z", "WO-2026-4815", deadline="2026-10-12T18:05:31.402Z")
valid["result-blocked-incoherent-snapshot.json"] = result(L1, "918283", 1, "BLOCKED", "2026-10-12T18:05:31.410Z",
    reason="INCOHERENT_SNAPSHOT")
valid["result-confirmed-verified.json"] = result(L1, "918273", 1, "CONFIRMED", "2026-10-12T18:04:11.733Z",
    job=J(1), serial=S(531201), attempt=A(1),
    evidence={"type": "BARCODE_VERIFICATION", "level": "VERIFIED", "correlatedTo": "CYCLE", "verified": True,
              "readSerial": S(531201), "reference": "SCN-LINE-01-77812", "observedAt": "2026-10-12T18:04:11.731Z"},
    timings={"detectionTimestamp": "2026-10-12T18:04:11.204Z", "receivedAt": "2026-10-12T18:04:11.221Z",
             "jobReadyAt": "2026-10-12T18:04:11.226Z", "sendStartAt": "2026-10-12T18:04:11.229Z",
             "writeCompletedAt": "2026-10-12T18:04:11.231Z", "evidenceAt": "2026-10-12T18:04:11.731Z"})
valid["result-unknown-write-interrupted.json"] = result(L1, "918275", 1, "UNKNOWN", "2026-10-12T18:04:14.090Z",
    job=J(3), serial=S(531203), attempt=A(3), reason="WRITE_INTERRUPTED")
valid["result-closed-unresolved.json"] = result(L1, "918275", 2, "CLOSED_UNRESOLVED", "2026-10-12T18:31:02.000Z",
    job=J(3), serial=S(531203), attempt=A(3), reason="CLOSED_CARTON_QUARANTINED",
    operatorAction={"operatorActionId": 88, "type": "CLOSE_UNRESOLVED", "performedBy": "operador.lopez",
                    "performedAt": "2026-10-12T18:31:01.640Z"})
valid["result-confirmed-exceptional-reprint.json"] = result(L1, "918275", 3, "CONFIRMED", "2026-10-12T18:47:30.512Z",
    job=J(3), serial=S(531203), attempt=A(0x1e),
    evidence={"type": "MANUAL_INSPECTION", "level": "VERIFIED", "correlatedTo": "CYCLE", "verified": True,
              "readSerial": S(531203), "reference": "INSP-2026-10-12-0042", "observedAt": "2026-10-12T18:47:30.100Z"},
    exceptionalReprint={"authorizationId": 15, "duplicateRisk": "ACCEPTED", "reason": "CLOSED_CARTON_RECOVERED",
                        "approvedBy": "supervisor.ramos"})
valid["result-expired-deadline-passed.json"] = result(L1, "918277", 1, "EXPIRED", "2026-10-12T18:04:17.950Z",
    job=J(5), serial=S(531205), reason="DEADLINE_PASSED")
valid["result-unknown-exceptional-unresolved.json"] = result(L1, "918277", 2, "UNKNOWN", "2026-10-12T19:02:41.300Z",
    job=J(5), serial=S(531205), attempt=A(0x2f), reason="EXCEPTIONAL_REPRINT_UNRESOLVED",
    exceptionalReprint={"authorizationId": 16, "duplicateRisk": "NONE_PROVEN", "reason": "EXPIRED_CARTON_HELD",
                        "approvedBy": "supervisor.ramos"})
valid["result-expired-out-of-order.json"] = result(L1, "918281", 1, "EXPIRED", "2026-10-12T18:05:10.050Z",
    job=J(9), serial=S(531209), reason="OUT_OF_ORDER")
valid["result-expired-erp-unavailable.json"] = {k: v for k, v in result(
    dict(L1, wo="WO-2026-4816"), "918276", 1, "EXPIRED", "2026-10-12T18:04:16.120Z", reason="ERP_UNAVAILABLE").items()}
valid["result-expired-erp-unavailable.json"]["workOrderId"] = "WO-2026-4816"
valid["result-failed-printer-unavailable.json"] = result(L1, "918279", 1, "FAILED", "2026-10-12T18:04:20.400Z",
    job=J(7), serial=S(531207), reason="PRINTER_UNAVAILABLE")
valid["result-failed-reconciled-not-printed.json"] = result(L1, "918282", 2, "FAILED", "2026-10-12T18:22:45.000Z",
    job=J(0x0a), serial=S(531210), attempt=A(0x0a), reason="RECONCILED_NOT_PRINTED",
    operatorAction={"operatorActionId": 91, "type": "RECONCILE_NEGATIVE", "performedBy": "operador.lopez",
                    "performedAt": "2026-10-12T18:22:44.120Z"})
valid["result-cancelled.json"] = result(L1, "918280", 1, "CANCELLED", "2026-10-12T18:05:02.330Z",
    job=J(8), serial=S(531208), reason="CANCELLED_BY_OPERATOR",
    operatorAction={"operatorActionId": 87, "type": "CANCEL_JOB", "performedBy": "supervisor.ramos",
                    "performedAt": "2026-10-12T18:05:02.100Z"})
valid["result-blocked-sequence-anomaly.json"] = result(L1, "918278", 1, "BLOCKED", "2026-10-12T18:04:18.010Z",
    reason="SEQUENCE_ANOMALY")
L2 = dict(site="PLANT-01", st="LINE-02", ep=9, rev=3, printer="PRN-LINE-02", wo="WO-2026-4820",
          product="SKU-20011", lot="LOT-20261012-C", lt="CARTON_GS1")
valid["result-transmitted-station-without-evidence.json"] = result(L2, "30411", 1, "TRANSMITTED",
    "2026-10-12T18:04:12.611Z", job="5a0c6e1d-3b7f-4c2a-9e8d-1f2b3c4d5e61", serial="P01L02-000000088120",
    attempt="7c1e2d3f-4a5b-4c6d-8e7f-90a1b2c3d4e5")
L4 = dict(site="PLANT-01", st="LINE-04", ep=3, rev=5, printer="PRN-LINE-04", wo="WO-2026-4830",
          product="SKU-30107", lot="LOT-20261012-D", lt="CARTON_GS1")
valid["result-unknown-serial-mismatch-correction.json"] = result(L4, "7712", 2, "UNKNOWN", "2026-10-12T18:09:03.410Z",
    job="9b2d4f6a-8c0e-4a1b-b3d5-e7f9a1c3e5f7", serial="P01L04-000000120455",
    attempt="1f3e5d7c-9b0a-4c2e-8d4f-6a8b0c2e4f61", reason="SERIAL_MISMATCH",
    evidence={"type": "BARCODE_VERIFICATION", "level": "VERIFIED", "correlatedTo": "CYCLE", "verified": False,
              "readSerial": "P01L04-000000120456", "reference": "SCN-LINE-04-11873", "observedAt": "2026-10-12T18:09:03.380Z"})

# ---------------------------------------------------------------- inválidos ante el schema
V = lambda n: copy.deepcopy(valid[n])
inv = {}
def case(name, doc, schema, why): inv[name] = (doc, schema, why)
R, C = "print-job-result.v1.schema.json", "carton-detected.v1.schema.json"
d = V("result-confirmed-verified.json"); del d["evidence"]; case("confirmed-without-evidence.json", d, R, "CONFIRMED exige evidence")
d = V("result-confirmed-verified.json"); d["evidence"]["verified"] = False; case("confirmed-unverified-evidence.json", d, R, "CONFIRMED exige evidence.verified = true")
d = V("result-confirmed-verified.json"); del d["evidence"]["readSerial"]; case("confirmed-verified-without-readserial.json", d, R, "VERIFIED exige readSerial")
d = V("result-confirmed-verified.json"); d["evidence"].update(type="PLC_APPLICATOR_CONFIRMATION", level="APPLIED", correlatedTo="JOB"); del d["evidence"]["readSerial"]
case("confirmed-applied-correlated-to-job.json", d, R, "APPLIED exige correlación CYCLE")
d = V("result-confirmed-verified.json"); del d["evidence"]["correlatedTo"]; case("confirmed-without-correlation.json", d, R, "evidence exige correlatedTo (JOB o CYCLE; NONE no existe)")
d = V("result-unknown-write-interrupted.json"); d["final"] = True; case("unknown-final-true.json", d, R, "UNKNOWN exige final = false")
d = V("result-unknown-write-interrupted.json"); del d["attemptId"]; case("unknown-without-attempt.json", d, R, "UNKNOWN exige attemptId")
d = V("result-failed-printer-unavailable.json"); del d["reason"]; case("failed-without-reason.json", d, R, "FAILED exige reason")
d = V("result-failed-printer-unavailable.json"); d["reason"] = "DEADLINE_PASSED"; case("failed-with-expired-reason.json", d, R, "reason incompatible con outcome")
d = V("result-blocked-sequence-anomaly.json"); d.update(printJobId=J(6), labelSerial=S(531206)); case("blocked-with-printjob.json", d, R, "BLOCKED no lleva job")
d = V("result-expired-erp-unavailable.json"); d["reason"] = "LATE_ARRIVAL"; d["printJobId"] = J(4); case("expired-late-arrival-with-printjob.json", d, R, "EXPIRED sin job no lleva printJobId")
d = V("result-transmitted-station-without-evidence.json"); d["evidence"] = V("result-confirmed-verified.json")["evidence"]; d["evidence"]["readSerial"] = d["labelSerial"]
case("transmitted-with-evidence.json", d, R, "TRANSMITTED no lleva evidence")
d = V("result-failed-printer-unavailable.json"); d["exceptionalReprint"] = V("result-confirmed-exceptional-reprint.json")["exceptionalReprint"]
case("reprint-on-failed.json", d, R, "exceptionalReprint solo con CONFIRMED (X01, X02) o UNKNOWN (X03)")
d = V("result-cancelled.json"); del d["operatorAction"]; case("cancelled-without-operator-action.json", d, R, "CANCELLED exige operatorAction CANCEL_JOB")
d = V("result-failed-printer-unavailable.json"); d["evidence"] = {"type": "PRINTER_JOB_COMPLETION", "level": "PRINTED", "correlatedTo": "JOB", "verified": True, "reference": "PRN-JOB-1", "observedAt": "2026-10-12T18:04:20.300Z"}
case("evidence-on-failed.json", d, R, "evidence solo con CONFIRMED o UNKNOWN")
d = V("result-unknown-exceptional-unresolved.json"); del d["exceptionalReprint"]; case("unknown-exceptional-without-reprint.json", d, R, "EXCEPTIONAL_REPRINT_UNRESOLVED exige exceptionalReprint")
d = V("result-expired-out-of-order.json")
for f in ("printJobId", "labelSerial", "productId", "lotId", "printerId"): del d[f]
case("expired-out-of-order-without-job.json", d, R, "OUT_OF_ORDER es un job vencido: exige printJobId y labelSerial")
d = V("result-unknown-serial-mismatch-correction.json"); d["evidence"]["verified"] = True; case("serial-mismatch-marked-verified.json", d, R, "SERIAL_MISMATCH publica la lectura con verified = false")
d = V("result-confirmed-verified.json"); d["operatorAction"] = V("result-cancelled.json")["operatorAction"]; case("operator-action-on-confirmed.json", d, R, "operatorAction solo en CANCELLED, CLOSED_UNRESOLVED o FAILED por reconciliación")
d = V("carton-detected-observed.json"); del d["plcEpoch"]; case("carton-detected-without-epoch.json", d, C, "plcEpoch requerido")
d = V("carton-detected-observed.json"); d["cycleId"] = 918273; case("carton-detected-cycleid-number.json", d, C, "cycleId es texto decimal")
d = V("carton-detected-recovered-fifo.json"); d["detectionOrigin"] = "OBSERVED"; case("carton-detected-fifo-as-observed.json", d, C, "transporte de recuperación exige RECOVERED")
d = V("carton-detected-observed.json"); d["opc"]["statusCode"] = "Uncertain"; case("carton-detected-bad-quality.json", d, C, "solo registros Good son detecciones")
d = V("result-unknown-write-interrupted.json"); d["evidence"] = V("result-confirmed-verified.json")["evidence"]
case("unknown-write-interrupted-with-evidence.json", d, R, "con UNKNOWN, evidence solo acompaña a SERIAL_MISMATCH")

# ---------------------------------------------------------------- negocio
biz = {}
d = V("result-confirmed-verified.json"); d["evidence"]["readSerial"] = S(531299); biz["confirmed-serial-mismatch.json"] = (d, "CONFIRMED con readSerial distinto de labelSerial (I06)")
d = V("result-confirmed-verified.json"); d["eventId"] = str(uuid.uuid4()); biz["result-eventid-not-deterministic.json"] = (d, "eventId no es UUIDv5 de su nombre")
d = V("carton-detected-observed.json"); d["workOrderId"] = "WO-2026-4899"; biz["carton-detected-fingerprint-mismatch.json"] = (d, "sourceFingerprint no corresponde al arreglo canónico")
d = V("carton-detected-gateway-republish.json"); d["deadlineAt"] = gw_deadline(d["receivedAt"])
biz["carton-detected-gateway-fabricated-deadline.json"] = (d, "GATEWAY_RECEIPT recuperado con plazo: el plazo sería fabricado (debe ser receivedAt)")
d = V("carton-detected-plc-without-timestamp.json"); d["deadlineAt"] = plus(d["receivedAt"], 1050)
biz["carton-detected-missing-timestamp-with-deadline.json"] = (d, "PLC_TIMESTAMP sin detectionTimestamp con plazo: el plazo sería fabricado (debe ser receivedAt)")

for sub in ("valid", "invalid", "business-invalid"):
    shutil.rmtree(EX / sub, ignore_errors=True); (EX / sub).mkdir(parents=True)
def dump(path, obj): path.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
for n, d in valid.items(): dump(EX / "valid" / n, d)
for n, (d, sch, why) in inv.items(): dump(EX / "invalid" / n, d)
from jsonschema import Draft202012Validator
fc = vp.build_format_checker()
cases = {}
for n, (d, sch, why) in inv.items():
    errs = list(Draft202012Validator(json.loads((ROOT / "contracts" / sch).read_text(encoding="utf-8")), format_checker=fc).iter_errors(d))
    kinds = {(e.validator, "/".join(str(x) for x in e.absolute_path)) for e in errs}
    assert len(kinds) == 1, (n, kinds)
    kw, path = kinds.pop()
    cases[n] = {"schema": sch, "why": why, "keyword": kw, "path": "/" + path if path else ""}
dump(EX / "invalid/EXPECTED.json", {"$comment": "Cada archivo debe fallar contra su schema y solo por la regla indicada: why la describe; keyword y path identifican el error de JSON Schema que el verificador exige (y ningún otro).",
     "cases": cases})
for n, (d, why) in biz.items(): dump(EX / "business-invalid" / n, d)
bcases = {}
for n, (d, why) in biz.items():
    be = vp.business_errors(d, sm)
    assert len(be) == 1, (n, be)
    bcases[n] = {"why": why, "error": be[0]}
dump(EX / "business-invalid/EXPECTED.json", {"$comment": "Cumplen el schema y deben fallar exactamente una regla de negocio de tools/verify_package.py: error es el mensaje exacto que el verificador exige.",
     "cases": bcases})
print(len(valid), len(inv), len(biz))
