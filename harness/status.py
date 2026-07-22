#!/usr/bin/env python3
"""harness/status.py - "what is the current run doing RIGHT NOW" live panel.

Zero-dependency (pure stdlib) supervisor-side CLI over the EXISTING run
artifacts. It never talks to KSP or kRPC; it reads:

  results/*_harness.log            - the per-invocation harness log (mtime =
                                     liveness signal, tail = current step)
  results/<runId>_mission.stdout.log - the live mission stdout ([Mission] lines,
                                     written unbuffered by the mission process)
  results/<runId>_status.json     - OPTIONAL live status file (Phase 2 of the
                                     live-observability design; preferred when
                                     present and fresh, log parsing otherwise)
  scenarios/<scenarioId>.toml     - phase budgets / caps for the heuristic line
                                     (tomllib; silently skipped on py < 3.11)

Usage:
  python status.py                 one-shot panel for the newest run
  python status.py --watch 5       re-render every 5 s
  python status.py --run 2026-07-22_1210   a specific run (prefix match)
  python status.py --raw 40        dump the last 40 raw mission-stdout lines

Design authority: docs/dev/design-live-observability.md. All parsing helpers
are PURE functions (unit-tested in lib/test_status.py); only main()/render
touch the filesystem. ASCII only; no ANSI required (optional cls on --watch).
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
from datetime import datetime, timezone
from typing import Dict, List, Optional, Tuple

HARNESS_DIR = os.path.dirname(os.path.abspath(__file__))
DEFAULT_RESULTS_DIR = os.path.join(HARNESS_DIR, "results")
DEFAULT_SCENARIOS_DIR = os.path.join(HARNESS_DIR, "scenarios")

# The mission telemetry line is rate-limited to ~1 Hz wall, so the count of
# telemetry lines since a marker approximates elapsed WALL seconds.
TELEMETRY_HZ = 1.0

# A status file (Phase 2) older than this is stale; fall back to log parsing.
STATUS_FILE_FRESH_SECONDS = 15.0

# No write to the mission stdout log for this long = the mission process is
# not producing output (blocked RPC, game dialog/pause, or the run is over).
STALL_WARN_SECONDS = 30.0

MISSION_STDOUT_SUFFIX = "_mission.stdout.log"

# ---------------------------------------------------------------------------
# Pure log-line parsers (unit-tested in lib/test_status.py).
# ---------------------------------------------------------------------------

_LINE_RE = re.compile(
    r"^\[(?P<source>Mission|Harness)\]\[(?P<level>[A-Za-z]+)\]"
    r"\[(?P<tag>[^\]]*)\] (?P<message>.*)$")

_PHASE_RE = re.compile(
    r"^phase (?P<from>\S+) -> (?P<to>\S+) ut=(?P<ut>[-0-9.naninf]+)"
    r"(?: alt=(?P<alt>[-0-9.naninf]+))?(?: ap=(?P<ap>[-0-9.naninf]+))?"
    r"(?: vsurf=(?P<vsurf>[-0-9.naninf]+))?")

_ACTION_RE = re.compile(
    r"^action (?P<kind>\S+) value=(?P<value>\S+)(?: text=(?P<text>\S+))?$")

_WARP_FIELD_RE = re.compile(r"^(?P<mode>[A-Z]+)x(?P<rate>[-0-9.naninf]+)$")

_OVERCAP_RE = re.compile(
    r"course-correction dv (?P<dv>[0-9.]+) m/s exceeds cap (?P<cap>[0-9.]+)")
_NEGLIGIBLE_RE = re.compile(
    r"course-correction dv (?P<dv>[0-9.]+) m/s is negligible")

_RUN_ID_RE = re.compile(
    r"^(?P<ts>\d{4}-\d{2}-\d{2}_\d{4})_(?P<scenario>.+?)(?:_a(?P<attempt>\d+))?$")

_VERDICT_RE = re.compile(
    r"^mission verdict=(?P<verdict>\S+) reason=(?P<reason>.*?)"
    r" phasesReached=(?P<phases>\[[^\]]*\]) wall=(?P<wall>\S+)$")

# Event tags worth surfacing verbatim (sparse, decision-relevant), plus every
# Warn/Error line and every action / phase transition regardless of tag.
EVENT_TAGS = ("Plan", "Point", "Throttle", "Warp", "Telemetry", "Verdict",
              "Assert", "Budget")

TELEMETRY_FIELD_LABELS = (
    ("ap", "apoapsis", "m"),
    ("pe", "periapsis", "m"),
    ("ecc", "eccentricity", ""),
    ("inc", "inclination", "deg"),
    ("alt", "altitude", "m"),
    ("vspd", "vertical speed", "m/s"),
    ("body", "SOI body", ""),
    ("nodes", "maneuver nodes", ""),
    ("nodeDv", "node remaining dv", "m/s"),
    ("nodeUt", "node UT", "s UT"),
    ("tts", "time to SOI change", "s"),
    ("warpTo", "native warp target", "s UT"),
    ("lf", "liquid fuel", "u"),
    ("thr", "throttle", "0..1"),
    ("situation", "situation", ""),
    ("warp", "warp", ""),
    ("apErr", "autopilot error", "deg"),
)

_NUMERIC_TELEMETRY_KEYS = ("ap", "pe", "ecc", "inc", "alt", "vspd", "nodeDv",
                           "nodeUt", "tts", "warpTo", "lf", "thr", "apErr")


def parse_float(text) -> float:
    """Lenient float parse: 'nan' / unparseable / None -> NaN (mission logs
    print non-finite values as 'nan')."""
    if text is None:
        return float("nan")
    try:
        return float(text)
    except (TypeError, ValueError):
        return float("nan")


def is_finite(value) -> bool:
    return isinstance(value, (int, float)) and math.isfinite(value)


def parse_log_line(line: str) -> Optional[Dict]:
    """Parse one '[Mission][LEVEL][Tag] message' / '[Harness][...]' line into
    {source, level, tag, message}; None for anything else (tracebacks, blank
    lines, stray subprocess noise)."""
    m = _LINE_RE.match(line.strip())
    if not m:
        return None
    return {"source": m.group("source"), "level": m.group("level"),
            "tag": m.group("tag"), "message": m.group("message")}


def parse_kv_tokens(message: str) -> Dict[str, str]:
    """Split 'k1=v1 k2=v2 ...' tokens into a dict (raw string values). Tokens
    without '=' are ignored."""
    out: Dict[str, str] = {}
    for token in message.split():
        if "=" in token:
            key, _, value = token.partition("=")
            out[key] = value
    return out


def parse_telemetry_message(message: str) -> Optional[Dict]:
    """Decode a 'telemetry ap=... pe=... ... warp=RAILSx1000.000 apErr=...'
    message into typed fields; None when the message is not a telemetry line.
    Adds warp_mode (str) + warp_rate (float) split out of the warp token."""
    if not message.startswith("telemetry "):
        return None
    raw = parse_kv_tokens(message[len("telemetry "):])
    out: Dict = {}
    for key in _NUMERIC_TELEMETRY_KEYS:
        out[key] = parse_float(raw.get(key))
    out["body"] = raw.get("body", "?")
    out["situation"] = raw.get("situation", "?")
    try:
        out["nodes"] = int(raw.get("nodes", "0"))
    except ValueError:
        out["nodes"] = 0
    out["warp"] = raw.get("warp", "?")
    wm = _WARP_FIELD_RE.match(out["warp"])
    out["warp_mode"] = wm.group("mode") if wm else "?"
    out["warp_rate"] = parse_float(wm.group("rate")) if wm else float("nan")
    return out


def parse_phase_transition(message: str) -> Optional[Dict]:
    """Decode a 'phase A -> B ut=... alt=... ap=... vsurf=...' message into
    {from, to, ut, alt, ap, vsurf}; None otherwise."""
    m = _PHASE_RE.match(message)
    if not m:
        return None
    return {"from": m.group("from"), "to": m.group("to"),
            "ut": parse_float(m.group("ut")),
            "alt": parse_float(m.group("alt")),
            "ap": parse_float(m.group("ap")),
            "vsurf": parse_float(m.group("vsurf"))}


def parse_action_message(message: str) -> Optional[Dict]:
    """Decode an 'action <kind> value=... [text=...]' message."""
    m = _ACTION_RE.match(message)
    if not m:
        return None
    return {"kind": m.group("kind"), "value": m.group("value"),
            "text": m.group("text")}


def is_event_line(parsed: Dict) -> bool:
    """True when a parsed [Mission] line is a sparse decision-relevant EVENT:
    any Warn/Error, any [Plan]/[Point]/[Throttle]/[Warp]/... tagged line, any
    action emission, and any phase transition. Telemetry samples are NOT
    events."""
    if parsed["source"] != "Mission":
        return False
    msg = parsed["message"]
    if msg.startswith("telemetry ") or msg.startswith("settle "):
        return False
    if parsed["level"] in ("Warn", "Error"):
        return True
    if parsed["tag"] in EVENT_TAGS:
        return True
    if msg.startswith("action ") or msg.startswith("phase "):
        return True
    return False


def split_run_id(run_id: str) -> Dict:
    """Split '2026-07-22_1210_B5-mun-flyby[_a2]' into
    {ts, scenario, attempt}; attempt defaults to 1 (no suffix = attempt 1)."""
    m = _RUN_ID_RE.match(run_id)
    if not m:
        return {"ts": "", "scenario": run_id, "attempt": 1}
    return {"ts": m.group("ts"), "scenario": m.group("scenario"),
            "attempt": int(m.group("attempt") or 1)}


def run_start_epoch(run_id: str) -> Optional[float]:
    """UTC epoch seconds of the run start encoded in the run id, or None."""
    parts = split_run_id(run_id)
    if not parts["ts"]:
        return None
    try:
        dt = datetime.strptime(parts["ts"], "%Y-%m-%d_%H%M")
        return dt.replace(tzinfo=timezone.utc).timestamp()
    except ValueError:
        return None


# ---------------------------------------------------------------------------
# Pure run-view builder: one pass over the mission stdout lines.
# ---------------------------------------------------------------------------


def summarize_mission_lines(lines: List[str]) -> Dict:
    """Fold the mission stdout lines into a run view:
      transitions:    [{index, from, to, ut, ...}]
      telemetry:      [(line_index, decoded_dict)]  (ALL samples, ~1/s)
      events:         [{index, level, tag, message}] (sparse decision events)
      spawn:          the '[Spawn] mission start ...' kv dict or {}
      verdict:        the '[Verdict] mission verdict=...' kv dict or None
      asserts:        ['assert <name> ... met=...'] result lines
    Pure: takes lines, returns data; no I/O."""
    transitions: List[Dict] = []
    telemetry: List[Tuple[int, Dict]] = []
    events: List[Dict] = []
    spawn: Dict = {}
    verdict: Optional[Dict] = None
    asserts: List[str] = []
    for index, line in enumerate(lines):
        parsed = parse_log_line(line)
        if parsed is None or parsed["source"] != "Mission":
            continue
        msg = parsed["message"]
        telem = parse_telemetry_message(msg)
        if telem is not None:
            telemetry.append((index, telem))
            continue
        trans = parse_phase_transition(msg)
        if trans is not None:
            trans = dict(trans)
            trans["index"] = index
            transitions.append(trans)
        if parsed["tag"] == "Spawn" and msg.startswith("mission start "):
            spawn = parse_kv_tokens(msg)
        if parsed["tag"] == "Verdict" and msg.startswith("mission verdict="):
            vm = _VERDICT_RE.match(msg)
            if vm:
                verdict = {"verdict": vm.group("verdict"),
                           "reason": vm.group("reason"),
                           "wall": vm.group("wall")}
            else:
                verdict = parse_kv_tokens(msg)
            verdict["_raw"] = msg
        if parsed["tag"] == "Assert" and msg.startswith("assert "):
            asserts.append(msg)
        if is_event_line(parsed):
            events.append({"index": index, "level": parsed["level"],
                           "tag": parsed["tag"], "message": msg})
    return {"transitions": transitions, "telemetry": telemetry,
            "events": events, "spawn": spawn, "verdict": verdict,
            "asserts": asserts, "line_count": len(lines)}


def build_phase_rows(summary: Dict) -> List[Dict]:
    """Phase history with durations. Each row: {phase, entry_ut, entry_index,
    game_s (None for the open current phase), wall_est_s (telemetry-line
    count in the phase, ~1/s)}. The pre-first-transition PRELAUNCH stretch is
    included when a transition exists."""
    transitions = summary["transitions"]
    telemetry = summary["telemetry"]
    rows: List[Dict] = []
    if not transitions:
        if telemetry or summary["spawn"]:
            rows.append({"phase": "PRELAUNCH", "entry_ut": float("nan"),
                         "entry_index": 0, "game_s": None,
                         "wall_est_s": len(telemetry) / TELEMETRY_HZ})
        return rows
    first = transitions[0]
    rows.append({"phase": first["from"], "entry_ut": float("nan"),
                 "entry_index": 0, "game_s": None, "wall_est_s": 0.0})
    for pos, trans in enumerate(transitions):
        nxt = transitions[pos + 1] if pos + 1 < len(transitions) else None
        game_s = None
        if nxt is not None and is_finite(trans["ut"]) and is_finite(nxt["ut"]):
            game_s = max(0.0, nxt["ut"] - trans["ut"])
        end_index = nxt["index"] if nxt is not None else summary["line_count"]
        wall = sum(1 for i, _t in telemetry
                   if trans["index"] < i < end_index) / TELEMETRY_HZ
        rows.append({"phase": trans["to"], "entry_ut": trans["ut"],
                     "entry_index": trans["index"], "game_s": game_s,
                     "wall_est_s": wall})
    return rows


def estimate_phase_elapsed_game(summary: Dict) -> Optional[float]:
    """Estimate GAME seconds spent in the CURRENT phase. The telemetry line
    carries no ut, so use the time-to-SOI-change drift (tts decreases 1:1
    with UT while finite): elapsed ~= tts(first sample in phase) - tts(last).
    None when tts is unusable (non-finite or non-monotonic, e.g. across an
    SOI transition or a re-plan that moved the encounter)."""
    transitions = summary["transitions"]
    telemetry = summary["telemetry"]
    entry_index = transitions[-1]["index"] if transitions else -1
    in_phase = [t for i, t in telemetry if i > entry_index]
    if len(in_phase) < 2:
        return None
    first_tts = next((t["tts"] for t in in_phase if is_finite(t["tts"])), None)
    last_tts = next((t["tts"] for t in reversed(in_phase)
                     if is_finite(t["tts"])), None)
    if first_tts is None or last_tts is None:
        return None
    elapsed = first_tts - last_tts
    return elapsed if elapsed >= 0.0 else None


def wall_est_in_current_phase(summary: Dict) -> float:
    """WALL seconds (estimated) in the current phase = telemetry-line count
    since the last transition (rate-limited to ~1 Hz)."""
    transitions = summary["transitions"]
    entry_index = transitions[-1]["index"] if transitions else -1
    return sum(1 for i, _t in summary["telemetry"] if i > entry_index) \
        / TELEMETRY_HZ


# ---------------------------------------------------------------------------
# Phase budgets from the scenario spec (best-effort; heuristics degrade
# gracefully when the TOML or tomllib is unavailable).
# ---------------------------------------------------------------------------

# Machine phase -> missionParams budget key (GAME seconds). Shared by the
# B5/B6 flyby machine; the ascent half is shared with B2/B4.
PHASE_BUDGET_KEYS = {
    "MJ-ASCENT": "ascentTimeoutSeconds",
    "CIRCULARIZE": "circularizeTimeoutSeconds",
    "PLAN-TRANSFER": "planTimeoutSeconds",
    "PLAN-CORRECTION": "planTimeoutSeconds",
    "TRANSFER-BURN": "transferBurnTimeoutSeconds",
    "CORRECTION-BURN": "transferBurnTimeoutSeconds",
    "COAST-TO-TARGET": "coastTimeoutSeconds",
    "TARGET-FLYBY": "flybyTimeoutSeconds",
}


def phase_budget_seconds(phase: str, params: Dict) -> Optional[float]:
    """The GAME-time budget for ``phase`` from a missionParams dict, or None
    (untimed phase / unknown key)."""
    key = PHASE_BUDGET_KEYS.get(phase)
    if key is None:
        return None
    value = params.get(key)
    return float(value) if isinstance(value, (int, float)) else None


def load_mission_params(scenario_id: str, scenarios_dir: str) -> Dict:
    """Read [driver.missionParams] from scenarios/<id>.toml; {} on any
    failure (missing file, py < 3.11 without tomllib, malformed TOML)."""
    path = os.path.join(scenarios_dir, "%s.toml" % scenario_id)
    try:
        import tomllib
    except ImportError:
        return {}
    try:
        with open(path, "rb") as fh:
            spec = tomllib.load(fh)
    except (OSError, ValueError):
        return {}
    driver = spec.get("driver") or {}
    params = driver.get("missionParams") or {}
    return params if isinstance(params, dict) else {}


# ---------------------------------------------------------------------------
# The heuristic "what is it doing / why might it look stuck" line.
# ---------------------------------------------------------------------------


def _events_since(summary: Dict, entry_index: int) -> List[Dict]:
    return [e for e in summary["events"] if e["index"] > entry_index]


def derive_heuristic(summary: Dict, params: Dict) -> str:
    """One plain-English line mapping the tail state to the machine's most
    likely activity, computed from phase + telemetry + the sparse events.
    Mirrors the diagnostic patterns of the B5/B6 live-flight findings
    (docs/dev/todo-and-known-bugs.md): over-cap plan-removal loops that look
    like a silent 1x hang, executor wedges, warp waits."""
    if summary["verdict"] is not None:
        v = summary["verdict"]
        return ("RUN FINISHED: verdict=%s reason=%s"
                % (v.get("verdict", "?"), v.get("reason", "?")))

    transitions = summary["transitions"]
    telemetry = summary["telemetry"]
    phase = transitions[-1]["to"] if transitions else "PRELAUNCH"
    entry_index = transitions[-1]["index"] if transitions else -1
    last = telemetry[-1][1] if telemetry else None
    events = _events_since(summary, entry_index)
    elapsed_game = estimate_phase_elapsed_game(summary)
    budget = phase_budget_seconds(phase, params)

    def remaining() -> str:
        if budget is None or elapsed_game is None:
            return "budget %s" % (fmt_duration(budget) if budget else "n/a")
        return "fall-through/flake in ~%s" % fmt_duration(budget - elapsed_game)

    if phase in ("PLAN-CORRECTION", "PLAN-TRANSFER"):
        overcap = [_OVERCAP_RE.search(e["message"]) for e in events]
        overcap = [m for m in overcap if m]
        negligible = [e for e in events
                      if _NEGLIGIBLE_RE.search(e["message"])]
        failed = [e for e in events if "make_nodes failed" in e["message"]]
        plans = [e for e in events
                 if e["message"].startswith("action mj_plan")]
        retry = params.get("planRetrySeconds", 30)
        rounds = sum(1 for t in transitions if t["to"] == "PLAN-CORRECTION")
        if overcap:
            m = overcap[-1]
            tail = ("falls through to COAST-TO-TARGET in ~%s"
                    % fmt_duration(budget - elapsed_game)) \
                if (budget is not None and elapsed_game is not None) \
                else "will fall through on the plan budget"
            return ("%s (round %d): re-planning every ~%ss; %d plan(s) removed "
                    "OVER-CAP (last dv=%s m/s > cap %s) so node_count stays 0 "
                    "-- LOOKS like a silent 1x hang but is the bounded "
                    "plan-retry loop; %s, then the coast flies the raw "
                    "intercept." % (phase, rounds, retry, len(overcap),
                                    m.group("dv"), m.group("cap"), tail))
        if negligible:
            return ("%s (round %d): correction dv is NEGLIGIBLE (trajectory "
                    "already good); plan removed, %s."
                    % (phase, rounds, remaining()))
        if failed:
            return ("%s: planner throwing server-side (%d failure(s), "
                    "re-plan every ~%ss) -- usually a transient no-encounter "
                    "right after a burn; %s." % (phase, len(failed), retry,
                                                 remaining()))
        if plans:
            return ("%s: plan issued (%d so far, retry ~%ss), waiting for a "
                    "node to appear; %s." % (phase, len(plans), retry,
                                             remaining()))
        return "%s: waiting to plan; %s." % (phase, remaining())

    if phase in ("TRANSFER-BURN", "CORRECTION-BURN"):
        if last is None:
            return "%s: no telemetry yet." % phase
        stag = params.get("burnStagnantSeconds", 120)
        nostart = params.get("burnNoStartSeconds", 600)
        if last["warp_mode"] == "RAILS":
            eta = ""
            if is_finite(last.get("nodeUt")) and is_finite(last.get("tts")):
                eta = " (autowarp advancing toward the node)"
            return ("%s: NodeExecutor rails-autowarp toward the node, dv=%s "
                    "m/s pending%s." % (phase, fmt_num(last["nodeDv"]), eta))
        if is_finite(last.get("thr")) and last["thr"] > 0.01:
            return ("%s: BURNING throttle=%s nodeDv=%s m/s remaining."
                    % (phase, fmt_num(last["thr"]), fmt_num(last["nodeDv"])))
        static_s = _node_dv_static_seconds(summary, entry_index)
        if static_s is not None and static_s >= 20:
            return ("%s: node dv=%s m/s UNCHANGED for ~%s at 1x with throttle "
                    "0 -- attitude flip / executor idle; stagnation watchdog "
                    "clears a completed node after %ss static (no-start "
                    "give-up %ss)." % (phase, fmt_num(last["nodeDv"]),
                                       fmt_duration(static_s), stag, nostart))
        return ("%s: pre-burn (aligning / settling), nodeDv=%s m/s, apErr=%s "
                "deg." % (phase, fmt_num(last["nodeDv"]),
                          fmt_num(last["apErr"])))

    if phase == "COAST-TO-TARGET":
        if last is None:
            return "%s: no telemetry yet." % phase
        if is_finite(last.get("warpTo")):
            span = ""
            if is_finite(last.get("tts")):
                span = "; SOI change in ~%s game-s" % fmt_duration(last["tts"])
            return ("COAST-TO-TARGET: native warp_to active toward UT %s at "
                    "%sx%s." % (fmt_num(last["warpTo"]),
                                last["warp"], span))
        if last["warp_mode"] == "RAILS":
            return ("COAST-TO-TARGET: rails warp held at %s (no native "
                    "warp_to pending)." % last["warp"])
        return ("COAST-TO-TARGET: at 1x with NO warp commanded -- normal "
                "only briefly (lead window / decision gap); if this "
                "persists across refreshes check the last [Warp] events "
                "for a cancel or a watchdog fire.")

    if phase == "TARGET-FLYBY":
        if last is None:
            return "%s: no telemetry yet." % phase
        return ("TARGET-FLYBY: inside %s SOI, alt=%s, warp=%s -- min-altitude "
                "evidence sampling through periapsis."
                % (last["body"], fmt_meters(last["alt"]), last["warp"]))

    if phase in ("MJ-ASCENT", "CIRCULARIZE", "ORBIT", "PRELAUNCH"):
        if last is None:
            return "%s: waiting for first telemetry." % phase
        return ("%s: alt=%s ap=%s vspd=%s m/s -- MechJeb ascent path; %s."
                % (phase, fmt_meters(last["alt"]), fmt_meters(last["ap"]),
                   fmt_num(last["vspd"]), remaining()))

    if phase == "RETURN":
        return "RETURN: terminal reached; settle tail / assertions next."

    return "%s: %s." % (phase, remaining())


def _node_dv_static_seconds(summary: Dict, entry_index: int) -> Optional[float]:
    """How long (wall-est seconds ~= sample count) the node dv has been
    unchanged at the tail of the current phase; None if unknown."""
    samples = [t for i, t in summary["telemetry"] if i > entry_index]
    if len(samples) < 2:
        return None
    last_dv = samples[-1].get("nodeDv")
    if not is_finite(last_dv):
        return None
    count = 0
    for t in reversed(samples):
        dv = t.get("nodeDv")
        if is_finite(dv) and abs(dv - last_dv) < 0.05:
            count += 1
        else:
            break
    return count / TELEMETRY_HZ


# ---------------------------------------------------------------------------
# Formatting helpers (pure).
# ---------------------------------------------------------------------------


def fmt_num(value) -> str:
    if value is None or (isinstance(value, float) and not math.isfinite(value)):
        return "n/a"
    if isinstance(value, float):
        return ("%.3f" % value).rstrip("0").rstrip(".")
    return str(value)


def fmt_meters(value) -> str:
    """Metres with a km rollover for readability (6207553.4 -> '6207.6 km')."""
    if value is None or not is_finite(value):
        return "n/a"
    if abs(value) >= 10000.0:
        return "%.1f km" % (value / 1000.0)
    return "%.1f m" % value


def fmt_duration(seconds) -> str:
    """Compact h/m/s ('2h03m', '4m30s', '42s'); 'n/a' for None/non-finite."""
    if seconds is None or not is_finite(seconds):
        return "n/a"
    seconds = float(seconds)
    sign = "-" if seconds < 0 else ""
    seconds = abs(seconds)
    if seconds >= 3600:
        return "%s%dh%02dm" % (sign, int(seconds // 3600),
                               int((seconds % 3600) // 60))
    if seconds >= 60:
        return "%s%dm%02ds" % (sign, int(seconds // 60), int(seconds % 60))
    return "%s%ds" % (sign, int(round(seconds)))


def decode_telemetry_fields(telem: Dict) -> List[str]:
    """Render one decoded telemetry sample as labeled 'name: value unit'
    lines, one field per line."""
    out: List[str] = []
    for key, label, unit in TELEMETRY_FIELD_LABELS:
        value = telem.get(key)
        if key in ("ap", "pe", "alt"):
            text = fmt_meters(value)
        elif key == "warp":
            text = "%s x%s" % (telem.get("warp_mode", "?"),
                               fmt_num(telem.get("warp_rate")))
        elif key in ("body", "situation"):
            text = str(value)
        elif key == "nodes":
            text = str(value)
        elif key == "tts":
            text = ("%s (%s)" % (fmt_num(value), fmt_duration(value))
                    if is_finite(value) else "n/a")
        else:
            text = fmt_num(value)
            if unit and text != "n/a":
                text = "%s %s" % (text, unit)
        out.append("  %-22s %s" % (label + ":", text))
    return out


# ---------------------------------------------------------------------------
# Filesystem helpers (thin; everything above is pure).
# ---------------------------------------------------------------------------


def list_runs(results_dir: str) -> List[Tuple[str, float]]:
    """(run_id, mtime) for every *_mission.stdout.log, newest first."""
    runs: List[Tuple[str, float]] = []
    try:
        names = os.listdir(results_dir)
    except OSError:
        return runs
    for name in names:
        if name.endswith(MISSION_STDOUT_SUFFIX):
            path = os.path.join(results_dir, name)
            try:
                mtime = os.path.getmtime(path)
            except OSError:
                continue
            runs.append((name[:-len(MISSION_STDOUT_SUFFIX)], mtime))
    runs.sort(key=lambda pair: pair[1], reverse=True)
    return runs


def newest_harness_log(results_dir: str) -> Optional[Tuple[str, float]]:
    best: Optional[Tuple[str, float]] = None
    try:
        names = os.listdir(results_dir)
    except OSError:
        return None
    for name in names:
        if name.endswith("_harness.log"):
            path = os.path.join(results_dir, name)
            try:
                mtime = os.path.getmtime(path)
            except OSError:
                continue
            if best is None or mtime > best[1]:
                best = (path, mtime)
    return best


def read_lines(path: str) -> List[str]:
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read().splitlines()
    except OSError:
        return []


def read_status_file(results_dir: str, run_id: str,
                     now: Optional[float] = None) -> Optional[Dict]:
    """The Phase-2 live status file, when present AND fresh; None otherwise
    (Phase 1 falls back to log parsing)."""
    path = os.path.join(results_dir, "%s_status.json" % run_id)
    try:
        mtime = os.path.getmtime(path)
    except OSError:
        return None
    if now is None:
        now = time.time()
    if now - mtime > STATUS_FILE_FRESH_SECONDS:
        return None
    try:
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, ValueError):
        return None
    return data if isinstance(data, dict) else None


def last_harness_activity(path: str, limit: int = 6) -> List[str]:
    """The last few [Harness] lines that are NOT forwarded mission stdout
    (those duplicate the mission log)."""
    out: List[str] = []
    for line in read_lines(path):
        parsed = parse_log_line(line)
        if parsed is None or parsed["source"] != "Harness":
            continue
        if "mission-stdout:" in parsed["message"]:
            continue
        out.append(line)
    return out[-limit:]


# ---------------------------------------------------------------------------
# Panel rendering.
# ---------------------------------------------------------------------------


def render_panel(run_id: str, results_dir: str, scenarios_dir: str,
                 event_count: int = 6, now: Optional[float] = None,
                 head: Optional[int] = None) -> str:
    """Build the full status panel text for one run. ``head`` renders the
    panel AS OF the first N log lines only (post-hoc replay: 'what would the
    panel have said mid-run')."""
    if now is None:
        now = time.time()
    stdout_path = os.path.join(results_dir, run_id + MISSION_STDOUT_SUFFIX)
    lines = read_lines(stdout_path)
    if head is not None:
        lines = lines[:max(0, head)]
    summary = summarize_mission_lines(lines)
    parts = split_run_id(run_id)
    params = load_mission_params(parts["scenario"], scenarios_dir)
    rows = build_phase_rows(summary)
    status = None if head is not None \
        else read_status_file(results_dir, run_id, now=now)

    out: List[str] = []
    out.append("=" * 72)
    out.append("PARSEK RUN STATUS  %s  (rendered %s)%s"
               % (run_id, datetime.now().strftime("%H:%M:%S"),
                  ("  [REPLAY: first %d lines]" % head)
                  if head is not None else ""))
    out.append("=" * 72)
    out.append("scenario: %-28s attempt: %d"
               % (parts["scenario"], parts["attempt"]))
    start = run_start_epoch(run_id)
    if start is not None:
        out.append("run age:  %s (started %s UTC)"
                   % (fmt_duration(now - start), parts["ts"]))
    try:
        age = now - os.path.getmtime(stdout_path)
        liveness = "LIVE" if age < STALL_WARN_SECONDS else \
            "STALLED/ENDED (no output for %s)" % fmt_duration(age)
        out.append("mission log: last write %s ago -> %s"
                   % (fmt_duration(age), liveness))
    except OSError:
        out.append("mission log: MISSING (mission step not started yet?)")

    hlog = newest_harness_log(results_dir)
    if hlog is not None:
        out.append("harness log: %s (last write %s ago)"
                   % (os.path.basename(hlog[0]), fmt_duration(now - hlog[1])))

    # Current phase + time in phase.
    transitions = summary["transitions"]
    phase = transitions[-1]["to"] if transitions else "PRELAUNCH"
    entry_ut = transitions[-1]["ut"] if transitions else float("nan")
    elapsed_game = estimate_phase_elapsed_game(summary)
    wall_est = wall_est_in_current_phase(summary)
    budget = phase_budget_seconds(phase, params)
    out.append("")
    out.append("PHASE: %s   (entered ut=%s)" % (phase, fmt_num(entry_ut)))
    out.append("  time in phase: game ~%s%s / wall ~%s (telemetry-line est.)"
               % (fmt_duration(elapsed_game) if elapsed_game is not None
                  else "n/a",
                  (" of %s budget" % fmt_duration(budget)) if budget else "",
                  fmt_duration(wall_est)))
    if summary["verdict"] is not None:
        out.append("  VERDICT: %s" % summary["verdict"].get("_raw", ""))

    # Phase-2 status file (preferred when fresh).
    if status is not None:
        out.append("")
        out.append("LIVE STATUS FILE (fresh %s_status.json):" % run_id)
        machine = status.get("machine", {})
        for key in sorted(machine):
            out.append("  %-22s %s" % (key + ":", machine[key]))

    # Latest decoded telemetry.
    out.append("")
    if summary["telemetry"]:
        out.append("LAST TELEMETRY (decoded):")
        out.extend(decode_telemetry_fields(summary["telemetry"][-1][1]))
    else:
        out.append("LAST TELEMETRY: none yet")

    # Recent events.
    out.append("")
    out.append("LAST %d EVENTS ([Plan]/[Point]/[Throttle]/[Warp]/actions/"
               "warns):" % event_count)
    events = summary["events"][-event_count:]
    if events:
        for e in events:
            out.append("  [%s][%s] %s" % (e["level"], e["tag"], e["message"]))
    else:
        out.append("  none")

    # Phase history.
    out.append("")
    out.append("PHASE HISTORY (game duration / wall est.):")
    for row in rows:
        game = fmt_duration(row["game_s"]) if row["game_s"] is not None \
            else "(open)"
        out.append("  %-18s entry ut=%-12s game %-8s wall ~%s"
                   % (row["phase"], fmt_num(row["entry_ut"]), game,
                      fmt_duration(row["wall_est_s"])))

    # Heuristic line.
    out.append("")
    out.append("WHAT IS IT DOING:")
    out.append("  " + derive_heuristic(summary, params))

    # Harness-side tail (post-mission steps / pre-mission steps visibility).
    if hlog is not None:
        tail = last_harness_activity(hlog[0], limit=4)
        if tail:
            out.append("")
            out.append("HARNESS TAIL:")
            for line in tail:
                out.append("  " + line)
    out.append("=" * 72)
    return "\n".join(out)


def resolve_run_id(requested: Optional[str],
                   results_dir: str) -> Optional[str]:
    """--run prefix match against known runs; newest run when omitted."""
    runs = list_runs(results_dir)
    if not runs:
        return None
    if requested is None:
        return runs[0][0]
    for run_id, _mtime in runs:
        if run_id == requested:
            return run_id
    for run_id, _mtime in runs:
        if run_id.startswith(requested):
            return run_id
    return None


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="status.py",
        description="Live status panel for the current/most-recent harness "
                    "mission run (reads results/*_mission.stdout.log).")
    parser.add_argument("--run", help="run id (prefix ok), default = newest")
    parser.add_argument("--watch", type=float, metavar="N",
                        help="re-render every N seconds until Ctrl+C")
    parser.add_argument("--raw", type=int, metavar="K",
                        help="print the last K raw mission-stdout lines and exit")
    parser.add_argument("--events", type=int, default=6,
                        help="number of recent events to show (default 6)")
    parser.add_argument("--head", type=int, metavar="N",
                        help="REPLAY: render as of the first N log lines "
                             "(post-hoc 'what would it have said')")
    parser.add_argument("--results-dir", default=DEFAULT_RESULTS_DIR)
    parser.add_argument("--scenarios-dir", default=DEFAULT_SCENARIOS_DIR)
    args = parser.parse_args(argv)

    run_id = resolve_run_id(args.run, args.results_dir)
    if run_id is None:
        print("no mission runs found under %s" % args.results_dir)
        return 1

    if args.raw is not None:
        path = os.path.join(args.results_dir, run_id + MISSION_STDOUT_SUFFIX)
        for line in read_lines(path)[-max(1, args.raw):]:
            print(line)
        return 0

    if args.watch is None:
        print(render_panel(run_id, args.results_dir, args.scenarios_dir,
                           event_count=args.events, head=args.head))
        return 0

    interval = max(1.0, args.watch)
    try:
        while True:
            # Re-resolve so a new attempt / new run is picked up mid-watch.
            current = resolve_run_id(args.run, args.results_dir) or run_id
            panel = render_panel(current, args.results_dir,
                                 args.scenarios_dir, event_count=args.events)
            if sys.stdout.isatty() and os.name == "nt":
                os.system("cls")
            elif sys.stdout.isatty():
                os.system("clear")
            print(panel)
            time.sleep(interval)
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main())
