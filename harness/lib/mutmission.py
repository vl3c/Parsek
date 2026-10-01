"""Pure core of the mission-verdict mutation checker (MUTATION-CHECK-PHASE-2, PR 3).

An autopilot lane's flight is gated by its mission's telemetry assertions: the
mission subprocess evaluates them (``mlib.evaluate_<x>_assertions`` through the
shell's ``SPEC.evaluate``), resolves a verdict (``mlib.resolve_flight_verdict``),
writes the result JSON (``mlib.build_mission_result`` +
``serialize_mission_result``), and ``run.py`` reads it back
(``missionverify.parse_mission_result_text``), maps it to the mission step
(``hlib.classify_mission_step``), composes driver validity and the
``missionOutcome`` row (``missionverify.compose_driver_validity``) and classifies
the run (``hlib.classify_verdict``). Nothing re-checked that chain over a real
flight, so an assertion that stopped reading its sensor, or a verdict path that
stopped reddening, would go unnoticed. Given an archived run record
(``<runId>.json``), its mission result (``<runId>_mission.json``) and the mission
shell module, this module replays that REAL chain over perturbed copies:

- each archived assertion row with its sensor reading removed (``value`` None, the
  row unmet, as the evaluators write a missing reading: NaN never passes, design
  edge 11); the kill is the run classifying driver-INVALID(mission) with the mission
  reason naming THAT assertion (``assert``). Orthogonality: an unmet mission is a
  driver-INVALID by design, never PARSEK-FAIL, so INVALID(mission) is the designed
  red and anything else (a PASS, or a PARSEK-FAIL) is a survivor;
- the BLIND replay: the shell's real ``evaluate`` over the machine's initial state
  (``build_state`` on the spec's missionParams) with no telemetry frames, and again
  with frames whose every field reads unread (NaN / "" / -1 / False). Every
  archived assertion must be unmet there: a row met with no telemetry at all
  certifies without reading anything (``blind``). The blind outcomes also go through
  the whole chain and must not classify PASS (``mission:blind-verdict``);
- each post-mission OUTCOME step (``hlib.post_mission_step_gates``) answered
  ``ERROR`` (must classify PARSEK-FAIL(mission-outcome)), ``REJECTED`` (a driver
  refusal: INVALID(driver-verdict-mismatch)) and never answered (INVALID
  (driver-stage)) (``outcome``);
- faults: a torn result file, a bumped ``schema``, no ``verdict`` (all
  INVALID(tooling-mission)), an empty assertion list, a vessel-lost terminal (both
  INVALID(mission)) and a phase-timeout flake (INVALID(autopilot-flake)).

The baseline must replay green: the archived verdict MISSION-OK, the real
``resolve_flight_verdict`` reproducing the archived verdict AND reason from the
archived rows, ``validate_mission_result`` clean, the composed ``mission`` /
``missionOutcome`` rows equal to the archived ones (when the record carries them),
and the classification PASS.

Not reached: a threshold pushed across its bound inside an evaluator. The frames
the evaluators read are not archived (the mission stdout is rate-limited), so the
blind replay proves an assertion depends on telemetry, not which side of its bound
it compares.

Pure apart from calling the mission shell's ``build_state`` / ``evaluate`` (pure by
the mission library's contract): no file I/O, no clock. The shell
(``harness/tools/mutation_check.py``) puts ``harness/missions`` and
``harness/missions/lib`` on ``sys.path`` and imports the shell module.
"""

from __future__ import annotations

import copy
import dataclasses
import json
import math
from dataclasses import dataclass, field
from types import SimpleNamespace
from typing import Callable, Dict, List, Optional, Sequence, Tuple

import hlib
import missionverify
import mlib
from mutsave import PROVEN, UNCHECKED, VACUOUS, GateVerdict

VERIFIER = "mission"
KIND_ASSERT = "mission-assert"
KIND_BLIND = "mission-blind"
KIND_OUTCOME = "mission-outcome"
KIND_FAULT = "mission-fault"

# Unread telemetry frames fed to the blind replay (enough for any K-consecutive
# debounce the evaluators use).
BLIND_FRAMES = 12


@dataclass(frozen=True)
class MissionInputs:
    """What the shell read for one archived run. ``evaluator`` is the mission shell's
    ``SPEC`` (``build_state`` / ``evaluate``), or None with ``evaluator_error`` set."""
    run_record: Optional[Dict]
    result_text: Optional[str]
    evaluator: Optional[object] = None
    evaluator_error: str = ""
    # Set when the shell could not read the archive (OSError).
    read_error: str = ""


def is_mission_spec(spec: Dict) -> bool:
    d = spec.get("driver") or {}
    return d.get("kind") == "autopilot" and bool(d.get("mission"))


# ---------------------------------------------------------------------------
# The chain.
# ---------------------------------------------------------------------------


def clean_state() -> SimpleNamespace:
    """A terminated machine state with no flake and no loss (what a MISSION-OK run
    hands ``resolve_flight_verdict``)."""
    return SimpleNamespace(verdict=None, loss_reason=None, flake_phase=None,
                           flake_reason=None)


def outcomes_from_rows(rows: Sequence[Dict]) -> List["mlib.AssertionOutcome"]:
    out = []
    for r in rows:
        detail = {k: v for k, v in r.items() if k not in ("name", "met", "value")}
        out.append(mlib.AssertionOutcome(str(r.get("name")), bool(r.get("met")),
                                         r.get("value"), detail))
    return out


@dataclass
class ChainVerdict:
    verdict: str
    subkind: str
    mission_verdict: Optional[str]
    mission_reason: str
    dv: "missionverify.DriverValidity"


@dataclass
class MissionBaseline:
    spec_id: str
    mission: str
    result_obj: Dict
    result_text: str
    rows: List[Dict]
    seam_rows: List[Dict]
    mission_row: Dict
    reasons: List[str]
    notes: List[str] = field(default_factory=list)


def _response_line(row: Dict, verdict: Optional[str], msg: str = "") -> Optional[str]:
    if verdict is None:
        return None
    line = "id=%s cmd=%s verdict=%s" % (row["id"], row.get("cmd", ""), verdict)
    if msg:
        line += " msg=%s" % msg
    return line


def build_result_text(base: MissionBaseline, outcomes, state) -> Tuple[Optional[str], str, str]:
    """Resolve + build + serialize, exactly as ``mission_runner.run_mission`` does
    after its fly loop. Returns (text, verdict, reason). ``serialize_mission_result``
    refuses a non-finite float (``allow_nan=False``); ``run_mission`` calls it outside
    its try, so the subprocess dies with no result file and the harness reads that
    as tooling-mission. The text is None there, which ``run_chain`` reads the same
    way."""
    verdict, reason = mlib.resolve_flight_verdict(state, outcomes)
    if verdict == mlib.MISSION_OK:
        reason = mlib.handoff_ok_reason(base.mission, reason)
    obj = base.result_obj
    conn = obj.get("connect") or {}
    cs = conn.get("connectedSeconds")
    result = mlib.build_mission_result(
        mission=base.mission, verdict=verdict, reason=reason,
        phases_reached=obj.get("phasesReached") or [],
        connect_attempts=int(conn.get("attempts") or 0),
        connected_seconds=float(cs) if isinstance(cs, (int, float)) else float("nan"),
        rpc_port=int(conn.get("rpcPort") or 0),
        assertions=[o.to_dict() for o in outcomes],
        wall_seconds=obj.get("wallSeconds"),
        krpc_client_version=str(obj.get("krpcClientVersion") or ""),
        krpc_server_version=str(obj.get("krpcServerVersion") or ""),
        error=obj.get("error"),
        warp_utilisation=obj.get("warpUtilisation"))
    try:
        return mlib.serialize_mission_result(result), verdict, reason
    except ValueError as exc:
        return None, verdict, "%s [serialize_mission_result raised: %s]" % (reason, exc)


def run_chain(base: MissionBaseline, result_text: Optional[str],
              seam_override: Optional[Dict[str, Tuple[Optional[str], str]]] = None
              ) -> ChainVerdict:
    """The harness side of the chain over one (possibly edited) result text and
    response stream. ``seam_override`` maps a seam step id to (verdict, msg)."""
    obj = missionverify.parse_mission_result_text(result_text)
    mv = missionverify.mission_verdict_of(obj)
    met, subkind = hlib.classify_mission_step(mv)
    mrow = base.mission_row
    mission_step = {"id": mrow["id"], "phase": "mission",
                    "expect": mrow.get("expect", hlib.MISSION_STEP_EXPECT),
                    "missionVerdict": mv, "met": met, "subkind": subkind or ""}
    lines: List[str] = []
    steps: List[Dict] = []
    for r in base.seam_rows:
        verdict, msg = r.get("verdict"), ""
        if seam_override and r["id"] in seam_override:
            verdict, msg = seam_override[r["id"]]
        ln = _response_line(r, verdict, msg)
        if ln:
            lines.append(ln)
        steps.append({"id": r["id"], "cmd": r.get("cmd", ""), "expect": r.get("expect", "OK")})
    ev = hlib.evaluate_response_stream(lines, steps)
    dv = missionverify.compose_driver_validity(ev, mission_step, False, False)
    driver = {"spec_valid": True, "admission_ok": True, "instance_lock_ok": True,
              "instance_busy": False, "boot_crashed": False, "boot_crash_repeated": False,
              "batch_crashed": False, "valid": dv.driver_valid,
              "stage_subkind": dv.stage_subkind or "driver-stage"}
    verifiers = {"killed": False, "batch_expected": False, "batch_present": True,
                 "mission_outcome_unmet": dv.mission_outcome_unmet}
    v = hlib.classify_verdict(driver, verifiers, {}, 1, "once")
    reason = str((obj or {}).get("reason") or "")
    return ChainVerdict(v.verdict, v.subkind, mv, reason, dv)


def _unmet_names(reason: str) -> List[str]:
    prefix = "assertions unmet: "
    if not reason.startswith(prefix):
        return []
    return [n.strip() for n in reason[len(prefix):].split(",") if n.strip()]


# ---------------------------------------------------------------------------
# Baseline.
# ---------------------------------------------------------------------------


def prepare_baseline(spec: Dict, inputs: MissionInputs) -> MissionBaseline:
    spec_id = str(spec.get("id", "?"))
    mission = str((spec.get("driver") or {}).get("mission") or "")

    def fail(*why: str) -> MissionBaseline:
        return MissionBaseline(spec_id, mission, {}, "", [], [], {}, list(why))

    if inputs.read_error:
        return fail("unreadable archive: %s" % inputs.read_error)
    if inputs.result_text is None:
        return fail("no archived mission result (<runId>_mission.json)")
    obj = missionverify.parse_mission_result_text(inputs.result_text)
    if obj is None:
        return fail("the archived mission result does not parse (or carries another schema)")
    verdict = missionverify.mission_verdict_of(obj)
    if verdict != mlib.MISSION_OK:
        return fail("archived mission verdict %s" % verdict)
    ok, errs = mlib.validate_mission_result(obj)
    if not ok:
        return fail("validate_mission_result: %s" % "; ".join(errs[:3]))
    if str(obj.get("mission")) != mission:
        return fail("archived result names mission %s, the spec %s" % (obj.get("mission"), mission))
    rec = inputs.run_record or {}
    steps = ((rec.get("driver") or {}).get("steps")) or []
    mrows = [s for s in steps if isinstance(s, dict) and s.get("phase") == "mission"]
    if len(mrows) != 1:
        return fail("the archived run record has %d mission step rows" % len(mrows))
    seam = [s for s in steps if isinstance(s, dict) and s.get("phase") != "mission"
            and s.get("id") is not None]
    rows = [dict(r) for r in obj.get("assertions") or []]
    base = MissionBaseline(spec_id, mission, obj, inputs.result_text, rows, seam, mrows[0], [])
    outcomes = outcomes_from_rows(rows)
    _text, v, reason = build_result_text(base, outcomes, clean_state())
    if v != verdict or reason != obj.get("reason"):
        return fail("the real resolve_flight_verdict over the archived rows gives %s (%r), "
                    "the archive says %s (%r)" % (v, reason, verdict, obj.get("reason")))
    chain = run_chain(base, inputs.result_text)
    if chain.verdict != hlib.VERDICT_PASS:
        return fail("the replayed chain classifies %s(%s)" % (chain.verdict, chain.subkind))
    arch = rec.get("verifiers") or {}
    for key, mine in (("mission", chain.dv.detail_mission),
                      ("missionOutcome", chain.dv.detail_mission_outcome)):
        if key not in arch:
            base.notes.append("the archived record has no %s row (older run): not compared" % key)
            continue
        if arch[key] != mine:
            return fail("the replayed %s row %s differs from the archived %s"
                        % (key, json.dumps(mine, sort_keys=True), json.dumps(arch[key], sort_keys=True)))
    return base


# ---------------------------------------------------------------------------
# Blind telemetry.
# ---------------------------------------------------------------------------


def blind_snapshot() -> "mlib.TelemetrySnapshot":
    """A telemetry frame whose every field reads UNREAD: NaN floats, empty strings,
    -1 counts (the library's own unread convention), False flags, empty tuples."""
    kw = {}
    for f in dataclasses.fields(mlib.TelemetrySnapshot):
        t = str(f.type)
        if t == "float":
            kw[f.name] = float("nan")
        elif t == "str":
            kw[f.name] = ""
        elif t == "int":
            kw[f.name] = -1
        elif t == "bool":
            kw[f.name] = False
        elif t.startswith("Tuple"):
            kw[f.name] = ()
        elif t.startswith("Optional"):
            kw[f.name] = None
    return mlib.TelemetrySnapshot(**kw)


# ---------------------------------------------------------------------------
# The edits.
# ---------------------------------------------------------------------------


@dataclass
class MissionCheck:
    mutations: list = field(default_factory=list)       # mutlib.Mutation
    gates: List[GateVerdict] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)


def _fmtv(v) -> str:
    if isinstance(v, float):
        return "%.6g" % v
    return repr(v)


def mutate_mission(spec: Dict, base: MissionBaseline, inputs: MissionInputs) -> MissionCheck:
    """Every mission edit for one spec over a green baseline (never modified)."""
    import mutlib  # local: mutlib imports this module inside its lane checks
    check = MissionCheck()
    check.notes.extend(base.notes)

    def record(target: str, kind: str, detail: str, killed: bool, note: str) -> bool:
        check.mutations.append(mutlib.Mutation(
            VERIFIER, target, kind, detail, mutlib.KILLED if killed else mutlib.SURVIVED,
            "" if killed else mutlib.TRIAGE, "" if killed else note))
        return killed

    def gate(label: str, block: str, window: str, measured: str, kills: Sequence[bool],
             survivor_notes: Sequence[str], unchecked: str = "") -> None:
        if not kills:
            check.gates.append(GateVerdict(label, block, window, measured, UNCHECKED,
                                           unchecked or "no edit constructible"))
        elif all(kills):
            check.gates.append(GateVerdict(label, block, window, measured, PROVEN, unchecked))
        else:
            check.gates.append(GateVerdict(label, block, window, measured, VACUOUS,
                                           "; ".join(n for k, n in zip(kills, survivor_notes)
                                                     if not k)))

    outcomes = outcomes_from_rows(base.rows)

    # 1. Each assertion with its sensor reading removed.
    for i, row in enumerate(base.rows):
        name = str(row.get("name"))
        edited = list(outcomes)
        edited[i] = mlib.AssertionOutcome(name, False, None, dict(outcomes[i].detail))
        text, _v, _r = build_result_text(base, edited, clean_state())
        c = run_chain(base, text)
        killed = (c.verdict == hlib.VERDICT_INVALID and c.subkind == "mission"
                  and name in _unmet_names(c.mission_reason))
        note = ("the assertion lost its reading and the run classified %s(%s)"
                % (c.verdict, c.subkind))
        record("assert[%s]" % name, KIND_ASSERT,
               "removed the reading of %s (value %s -> None, met -> False); run %s(%s), "
               "mission %s" % (name, _fmtv(row.get("value")), c.verdict, c.subkind,
                               c.mission_reason), killed, note)
        gate("mission.assert[%s]" % name, "mission.assertions", "met",
             "value=%s" % _fmtv(row.get("value")), [killed], [note])

    # 2. The blind replay through the real evaluator.
    ev_spec = inputs.evaluator
    params = dict(((spec.get("driver") or {}).get("missionParams")) or {})
    blind_runs: List[Tuple[str, Optional[List]]] = []
    state0 = None
    build_error = ""
    if ev_spec is None:
        build_error = inputs.evaluator_error or "the mission shell module did not load"
    else:
        try:
            state0 = ev_spec.build_state(params)
        except Exception as exc:  # noqa: BLE001 - reported, never raised
            build_error = "build_state raised on the spec's missionParams: %s: %s" % (
                type(exc).__name__, exc)
    if state0 is not None:
        for label, frames in (("no frames", []),
                              ("%d unread frames" % BLIND_FRAMES, [blind_snapshot()] * BLIND_FRAMES)):
            try:
                blind_runs.append((label, list(ev_spec.evaluate(frames, params, state0))))
            except Exception as exc:  # noqa: BLE001
                blind_runs.append(("%s (evaluate raised %s)" % (label, type(exc).__name__), None))
    for row in base.rows:
        name = str(row.get("name"))
        label = "mission.blind[%s]" % name
        if state0 is None:
            gate(label, "mission.blind", "unmet", "", [], [], build_error)
            continue
        kills: List[bool] = []
        notes: List[str] = []
        missing = []
        for run_label, outs in blind_runs:
            if outs is None:
                continue
            match = [o for o in outs if o.name == name]
            if not match:
                missing.append(run_label)
                continue
            killed = not match[0].met
            kills.append(killed)
            notes.append("met with %s and the machine's initial state (value %s): it "
                         "certifies without reading telemetry" % (run_label, _fmtv(match[0].value)))
            record("blind[%s]" % name, KIND_BLIND,
                   "%s with %s: met=%s value=%s" % (name, run_label, match[0].met,
                                                     _fmtv(match[0].value)), killed, notes[-1])
        why = ""
        if not kills:
            why = ("the blind evaluator %s" % (
                "raised on every blind input (the runner would write MISSION-ERROR, red as a "
                "whole, but the row is not attributable)" if not missing
                else "produced no %s row" % name))
        gate(label, "mission.blind", "unmet", "", kills, notes, why)
    if state0 is not None and blind_runs and blind_runs[0][1] is not None:
        text, v, _r = build_result_text(base, blind_runs[0][1], state0)
        c = run_chain(base, text)
        killed = c.verdict != hlib.VERDICT_PASS
        note = "the blind outcomes resolved %s and the run classified PASS" % v
        record("blind-verdict", KIND_BLIND, "blind outcomes -> mission %s, run %s(%s)"
               % (v, c.verdict, c.subkind), killed, note)
        gate("mission:blind-verdict", "mission.blind", "not PASS", "", [killed], [note])
    elif state0 is not None:
        gate("mission:blind-verdict", "mission.blind", "not PASS", "", [], [],
             "the evaluator raised on the no-frame input")

    # 3. Post-mission outcome steps.
    mid = str(base.mission_row["id"])
    outcome_steps = [r for r in base.seam_rows
                     if str(r["id"]) > mid and hlib.post_mission_step_gates(r.get("cmd", ""))]
    if not outcome_steps:
        check.notes.append("missionOutcome: no post-mission outcome verb (the row reads "
                           "SKIPPED by design)")
    for r in outcome_steps:
        sid, cmd = str(r["id"]), str(r.get("cmd", ""))
        label = "missionOutcome[%s#%s]" % (cmd, sid)
        kills: List[bool] = []
        notes: List[str] = []
        for name, override, want in (
                ("error", (hlib.SEAM_VERDICT_OUTCOME_TERMINAL, ""),
                 (hlib.VERDICT_PARSEK_FAIL, "mission-outcome")),
                ("refused", ("REJECTED", ""), (hlib.VERDICT_INVALID, "driver-verdict-mismatch")),
                ("unanswered", (None, ""), (hlib.VERDICT_INVALID, "driver-stage"))):
            c = run_chain(base, base.result_text, {sid: override})
            killed = (c.verdict, c.subkind) == want
            note = ("%s answered %s and the run classified %s(%s), not %s(%s)"
                    % (cmd, override[0], c.verdict, c.subkind, want[0], want[1]))
            kills.append(record(label, KIND_OUTCOME, "%s -> %s: run %s(%s)" % (
                cmd, override[0], c.verdict, c.subkind), killed, note))
            notes.append(note)
        gate(label, "missionOutcome", "post-mission outcome", "verdict=%s" % r.get("verdict"),
             kills, notes)

    # 4. Faults.
    obj = base.result_obj
    faults: List[Tuple[str, str, str, Tuple[str, str]]] = []
    faults.append(("torn", "truncated the mission result to half",
                   base.result_text[:len(base.result_text) // 2],
                   (hlib.VERDICT_INVALID, "tooling-mission")))
    bumped = dict(obj, schema=missionverify.MISSION_RESULT_SCHEMA + 1)
    faults.append(("schema", "bumped the result schema", json.dumps(bumped),
                   (hlib.VERDICT_INVALID, "tooling-mission")))
    faults.append(("no-verdict", "removed the verdict key",
                   json.dumps({k: v for k, v in obj.items() if k != "verdict"}),
                   (hlib.VERDICT_INVALID, "tooling-mission")))
    text, _v, _r = build_result_text(base, [], clean_state())
    faults.append(("no-assertions", "emptied the assertion list", text,
                   (hlib.VERDICT_INVALID, "mission")))
    lost = clean_state()
    lost.loss_reason = "mutation-check: vessel lost"
    text, _v, _r = build_result_text(base, outcomes, lost)
    faults.append(("vessel-lost", "ended the machine on a vessel-lost terminal", text,
                   (hlib.VERDICT_INVALID, "mission")))
    flake = clean_state()
    flake.verdict = mlib.MISSION_FLAKE
    flake.flake_phase = (obj.get("phasesReached") or ["?"])[-1]
    text, _v, _r = build_result_text(base, outcomes, flake)
    faults.append(("flake", "ended the machine on a phase timeout", text,
                   (hlib.VERDICT_INVALID, "autopilot-flake")))
    for name, detail, text, want in faults:
        c = run_chain(base, text)
        killed = (c.verdict, c.subkind) == want
        note = "classified %s(%s), not %s(%s)" % (c.verdict, c.subkind, want[0], want[1])
        record("mission:%s" % name, KIND_FAULT, "%s; run %s(%s)" % (detail, c.verdict, c.subkind),
               killed, note)
        gate("mission:%s" % name, "mission", "fault", "", [killed], [note])
    return check
