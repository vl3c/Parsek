"""Flight-efficiency analysis: where the WALL time of a harness auto-flight goes.

Pure library behind ``harness/tools/flight_efficiency.py``. It MEASURES only:
nothing here changes a mission, a verifier or a classification. Every decision
is a function of strings / dicts the thin shell read from a results dir, so the
whole estimate is unit-tested with no game (``lib/test_flighteff.py``).

Inputs per run (all optional, a missing one degrades the report, never crashes):
  - ``<runId>_mission.stdout.log``: the ~1 Hz WALL telemetry lines
    (``mission_runner.MissionLogger.verbose_rate_limited``), phase changes,
    ``action`` lines.
  - ``<runId>_mission.json``: ``wallSeconds`` and ``warpUtilisation`` (one row
    per phase VISIT, in visit order). It calibrates the wall clock.
  - ``<runId>.json``: the run.py result (startedUtc, endedUtc, attempt,
    attemptsWallSeconds, missionWallSeconds, driver steps).
  - ``<runId>_shots/KSP.log``: local wall timestamps of boot, scene changes and
    every seam command (recv -> final verdict).
  - ``<stamp>_harness.log``: lock / retry / cost lines (run.py logs no wall
    timestamps, so it contributes facts, never durations).

ESTIMATION CONTRACT (documented limits; every figure is an estimate):
  1. Intervals. Telemetry sample i opens the interval [i, i+1). Its state
     (rate, altitude, body, situation, nodes, apErr) is sample i's; "orbit
     changed" compares sample i with sample i+1 (ap or pe moved more than
     ORBIT_STATIC_TOLERANCE_M). The last sample of a log opens no interval.
  2. Wall. Each interval gets a raw weight: max(1.0 s, game / max rate of its
     two endpoints) -- the time it would take at the FASTER endpoint rate is a
     lower bound on its wall, which credits blocking-RPC gaps (no lines emitted)
     at 1x. Within each phase VISIT the raw weights are then scaled so they sum
     to that visit's ``warpUtilisation.wallSeconds`` (matched in visit order).
     Without a mission.json the raw weights stand (~1.0 s per telemetry line).
  3. Game. ut(i+1) - ut(i); when a log predates the ut= field, game is
     wall x rate (the warp_audit fallback).
  4. Buckets. Every interval lands in EXACTLY ONE bucket, so nothing is counted
     twice: idle, lowWarp, physicsWarp, burn, atmoOrGround, warped,
     unclassified (see ``classify_interval``). Any interval opened at PHYSICS
     warp above 1x is ``physicsWarp`` (never lowWarp, never recoverable): the
     mission warp policy runs PARK / kx COAST at 4x physics on purpose, and
     kRPC's WarpTo drops into PHYSICS x3-4 for its own last game seconds.
  5. Recoverable. For a contiguous run of idle (or lowWarp) intervals inside one
     visit: wall - (game / target rate) - RAMP_SETTLE_OVERHEAD_SECONDS, floored
     at 0. The target rate is the best LEGAL rails rate at the run's MINIMUM
     altitude (stock ``timeWarpAltitudeLimits``), except attitude-align idle,
     which rails warp cannot help (rails freezes rotation): its target is the
     top physics rate, and except the policy-aware targets
     (``POLICY_PHYSICS_TARGETS``: B5 PARK dwell, kx COAST), where the mission
     policy is 4x PHYSICS warp, not rails, so the target is that 4x. The
     ideal assumes a warp-to that lands exactly on the next event; the
     overhead constant stands for the ramp up, the ramp down and the settle
     that a real warp-to always pays.
  6. Long burns (>= LONG_BURN_MIN_SECONDS of 1x with a changing orbit) are
     physics-warp CANDIDATES, reported with an optional saving at
     BURN_PHYSICS_WARP_RATE that is NEVER added to the recoverable total
     (physics warp trades burn precision).
  7. ``thr=`` is never read: MechJeb owns the throttle and kRPC reads 0 during
     its burns, so a changing orbit is the only burn signal.
  8. By design. An idle / lowWarp interval whose (cause, phase) the mission warp
     policy deliberately leaves at 1x (``BY_DESIGN_POLICY``), or a CAPTURE-BURN
     node wait inside the capture lead (``capture_lead_seconds``), forms its own
     runs: the same estimate as item 5 is computed for them but lands in
     ``byDesign``, never in ``recoverable``. The capture lead is the hold's
     release point before the node: NODE_WAIT_ORIENT_LEAD_SECONDS + the
     machine's arrival tolerance + the half burn the machine's own
     ``node-wait:`` action line printed (``halfBurn=``). A visit with no such
     line (a pre-policy run, or a declined hold) uses the constants alone, so
     its lead is short by the half burn (~10 s for a Mun capture) and the
     by-design figure is a lower bound. Resolution is one telemetry line: an
     interval is inside the lead when its OPENING sample is.
     A CIRCULARIZE node wait gets the same lead (``CIRCULARIZE_LEAD_KEY``) only
     in a visit whose own ``node-wait:`` action line printed a half burn: that
     line exists only where the lane opted in (``circularizeNodeWaitWarp``), so
     a CIRCULARIZE wait with no line (every other lane) stays wholly
     recoverable and outcome-sensitive (item 9).
     The BDOCK machines' RENDEZVOUS node wait has the same kind of lead
     (``rendezvous_lead_boundary``): the hold rails-warps to its target T and
     cancels RV_EARLY_CANCEL_SECONDS before it, so a wait on that node is by
     design from T - RV_EARLY_CANCEL_SECONDS on (T comes from the hold's own
     ``warp_to_ut`` action, matched to the node by its ``nodeUt=``; a
     closest-approach clamp is already inside T). A node the hold declined
     uses its ``node-wait:`` text (nodeUt - halfBurn - lead, or the ca-clamp
     UT when earlier, minus the early cancel); a node with no line at all (a
     pre-hold run) uses the constants alone, short by the half burn.
  9. Outcome-sensitive. (cause, phase) rows in ``OUTCOME_SENSITIVE`` stay
     recoverable, but the row carries ``outcomeSensitive`` and its text says a
     warp there may move the flight's outcome.

ASCII only; stdlib only. Imports ``warp_audit`` (harness root) for the shared
telemetry regexes and constants; mirrors two mlib tables and the mission warp
policy constants (each pinned against mlib by ``test_flighteff``) because mlib
is the mission library, not a harness import.
"""

from __future__ import annotations

import json
import math
import os
import re
import sys
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence, Tuple

_HARNESS_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _HARNESS_DIR not in sys.path:
    sys.path.insert(0, _HARNESS_DIR)

import warp_audit  # noqa: E402

SCHEMA_VERSION = 2

# ---------------------------------------------------------------------------
# Tables
# ---------------------------------------------------------------------------

# MIRROR of mlib.RAILS_WARP_RATES / mlib.STOCK_WARP_ALTITUDE_LIMITS (stock KSP
# 1.12.5 ground truth; see mlib for provenance). test_flighteff pins equality.
RAILS_WARP_RATES = (1.0, 5.0, 10.0, 50.0, 100.0, 1000.0, 10000.0, 100000.0)

STOCK_WARP_ALTITUDE_LIMITS = {
    "Sun":    (0.0, 3270000.0, 3270000.0, 6540000.0, 13080000.0, 26160000.0, 52320000.0, 65400000.0),
    "Moho":   (0.0, 10000.0, 10000.0, 30000.0, 50000.0, 100000.0, 200000.0, 300000.0),
    "Eve":    (0.0, 30000.0, 30000.0, 60000.0, 120000.0, 240000.0, 480000.0, 600000.0),
    "Gilly":  (0.0, 8000.0, 8000.0, 8000.0, 20000.0, 40000.0, 80000.0, 100000.0),
    "Kerbin": (0.0, 30000.0, 30000.0, 60000.0, 120000.0, 240000.0, 480000.0, 600000.0),
    "Mun":    (0.0, 5000.0, 5000.0, 10000.0, 25000.0, 50000.0, 100000.0, 200000.0),
    "Minmus": (0.0, 3000.0, 3000.0, 6000.0, 12000.0, 24000.0, 48000.0, 60000.0),
    "Duna":   (0.0, 30000.0, 30000.0, 60000.0, 100000.0, 300000.0, 600000.0, 800000.0),
    "Ike":    (0.0, 5000.0, 5000.0, 10000.0, 25000.0, 50000.0, 100000.0, 200000.0),
    "Dres":   (0.0, 10000.0, 10000.0, 30000.0, 50000.0, 100000.0, 200000.0, 300000.0),
    "Jool":   (0.0, 0.0, 15000.0, 60000.0, 150000.0, 300000.0, 600000.0, 1200000.0),
    "Laythe": (0.0, 30000.0, 30000.0, 60000.0, 120000.0, 240000.0, 480000.0, 600000.0),
    "Vall":   (0.0, 24500.0, 24500.0, 24500.0, 40000.0, 60000.0, 80000.0, 100000.0),
    "Tylo":   (0.0, 30000.0, 30000.0, 60000.0, 120000.0, 240000.0, 480000.0, 600000.0),
    "Bop":    (0.0, 24500.0, 24500.0, 24500.0, 40000.0, 60000.0, 80000.0, 100000.0),
    "Pol":    (0.0, 5000.0, 5000.0, 5000.0, 8000.0, 12000.0, 30000.0, 90000.0),
    "Eeloo":  (0.0, 4000.0, 4000.0, 20000.0, 30000.0, 40000.0, 70000.0, 150000.0),
}

# Stock KSP 1.12.5 atmosphere heights (metres ASL). No such table existed in
# mlib or hlib; these are the stock CelestialBody.atmosphereDepth values for the
# five bodies with an atmosphere. Every other stock body is airless (0).
ATMOSPHERE_HEIGHT_M = {
    "Kerbin": 70000.0,
    "Eve": 90000.0,
    "Duna": 50000.0,
    "Jool": 200000.0,
    "Laythe": 50000.0,
}

# kRPC physics warp rates by index (stock).
PHYSICS_WARP_RATES = (1.0, 2.0, 3.0, 4.0)

# ---------------------------------------------------------------------------
# Thresholds (all documented in the module contract)
# ---------------------------------------------------------------------------

ONE_X_RATE_MAX = warp_audit.ONE_X_RATE_MAX
NOMINAL_LINE_SECONDS = warp_audit.TELEMETRY_INTERVAL_SECONDS
ORBIT_STATIC_TOLERANCE_M = 1.0
RAMP_SETTLE_OVERHEAD_SECONDS = 10.0
LONG_BURN_MIN_SECONDS = 60.0
BURN_PHYSICS_WARP_RATE = 2.0
# An apErr above this (degrees) means the autopilot is still slewing; B4's own
# maxAttitudeErrorDeg default is the same 5 degrees.
ATTITUDE_ALIGN_MIN_DEG = 5.0
SLOW_LADDER_SECONDS = 10.0
OSCILLATION_MIN_RERAISES = 2
COAST_X1_FLAG_SECONDS = 30.0
# KSP logs continuously during a run; a stamp more than this far after (or, for
# the backward bound, before) its predecessor is an out-of-order line, not
# elapsed time.
KSP_MAX_FORWARD_JUMP_SECONDS = 3600.0
KSP_MAX_BACKWARD_JUMP_SECONDS = 60.0

GROUND_SITUATIONS = ("LANDED", "SPLASHED", "PRE_LAUNCH")
ORBITAL_SITUATIONS = ("ORBITING", "SUB_ORBITAL", "ESCAPING")
UNKNOWN_BODY_TOKENS = ("", "?", "-", "none", "None", "nan")

BUCKETS = ("idle", "lowWarp", "physicsWarp", "burn", "atmoOrGround", "warped",
           "unclassified")

IDLE_CAUSES = ("dwell", "attitude-align", "waiting-for-node", "soi-approach",
               "coast-to-apoapsis", "coast-to-entry", "coast-to-periapsis",
               "other-coast")
LOW_WARP_CAUSE = "low-warp"
BURN_CAUSE = "burn-physics-warp"

# Deliberate hold phases: any phase whose name carries one of these tokens,
# plus the explicit extras. Deterministic and pinned by tests.
DWELL_PHASE_TOKENS = ("PARK", "HOLD", "DWELL", "SETTLE", "WAIT", "WATCH")
DWELL_PHASE_EXTRAS = ("POST-DEPLOY", "EVA-WINDOW")

# ---------------------------------------------------------------------------
# Mission warp policy (what the missions leave at 1x or physics warp ON PURPOSE)
# ---------------------------------------------------------------------------

# MIRROR of mlib's mission warp policy constants; test_flighteff pins each one
# against mlib. The b5 machine releases a held capture hand-off once UT is
# within the arrival tolerance of node UT - half burn - the orient lead.
NODE_WAIT_ORIENT_LEAD_SECONDS = 120.0
NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS = 5.0
# kRPC PhysicsWarpFactor index of a physics dwell (3 = the stock 4x).
PHYSICS_DWELL_WARP_INDEX = 3
POLICY_PHYSICS_RATE = PHYSICS_WARP_RATES[PHYSICS_DWELL_WARP_INDEX]

CAPTURE_LEAD_KEY = ("waiting-for-node", "CAPTURE-BURN")
CAPTURE_LEAD_REASON = (
    "capture node-wait lead: the b5 machine rails-warps itself to node UT - half "
    "burn - NODE_WAIT_ORIENT_LEAD_SECONDS and only then hands the node to "
    "MechJeb's executor, which aligns and settles at 1x (mlib "
    "_b5_node_wait_begin)")

# The OPT-IN CIRCULARIZE node-wait hold (mlib _B5_CIRCULARIZE_NODE_WAIT_PHASE,
# spec key circularizeNodeWaitWarp): the same release point as the capture
# hold, applied only to a visit whose node-wait line printed its half burn.
CIRCULARIZE_LEAD_KEY = ("waiting-for-node", "CIRCULARIZE")
CIRCULARIZE_LEAD_REASON = (
    "circularize node-wait lead: with circularizeNodeWaitWarp the b5 machine "
    "rails-warps itself to the park round-out node UT - half burn - "
    "NODE_WAIT_ORIENT_LEAD_SECONDS and only then hands the node to MechJeb's "
    "executor, which aligns and settles at 1x (mlib _b5_node_wait_begin)")

# MIRROR of mlib's RENDEZVOUS NODE WAITS constants (the BDOCK machines);
# test_flighteff pins each one against mlib. The hold cancels its own warp
# RV_WARP_ARRIVAL_TOLERANCE_SECONDS before its target (kRPC WarpTo's last game
# seconds read PHYSICS x3-4), and a node keeps its identity while its UT moves
# less than RV_WARP_NODE_IDENTITY_SECONDS.
RV_EARLY_CANCEL_SECONDS = 15.0
RV_NODE_IDENTITY_SECONDS = 1.0

RENDEZVOUS_LEAD_KEY = ("waiting-for-node", "RENDEZVOUS")
# The machines whose RENDEZVOUS phase is bdock_decide's (sdock / rdock wrap it).
RENDEZVOUS_LEAD_MACHINES = ("bdock_decide", "sdock_decide", "rdock_decide")
RENDEZVOUS_LEAD_REASON = (
    "rendezvous node-wait lead: the bdock machine rails-warps itself to node UT - "
    "half burn - NODE_WAIT_ORIENT_LEAD_SECONDS (earlier when the 5 km "
    "closest-approach guard clamps it), cancels the warp RV_EARLY_CANCEL_SECONDS "
    "early, then MechJeb's executor aligns and settles at 1x (mlib "
    "_bdock_rv_node_wait)")

_ASCENT_REASON = (
    "MechJeb's ascent runs its circularization node through its own executor; "
    "it cancels a second warp writer, and its 1x is the align-and-settle, which "
    "needs physics")
_HOLD_REASON = ("m3 render hold: a deliberate 1x hold of dwellHoldSeconds game "
                "time for render observation; shorten it rather than warp")

# (cause, phase) -> why the mission warp policy leaves that 1x time alone. Its
# estimate goes to byDesign, never to recoverable.
BY_DESIGN_POLICY: Dict[Tuple[str, str], str] = {
    ("attitude-align", "DEORBIT"): (
        "B4's retrograde slew before the deorbit burn: rails warp freezes "
        "rotation, and the warp policy leaves attitude slews at 1x"),
    ("dwell", "HOLD-DEPART"): _HOLD_REASON,
    ("dwell", "HOLD-ARRIVE"): _HOLD_REASON,
    ("dwell", "HOLD-PARK"): _HOLD_REASON,
    ("waiting-for-node", "TRANSFER-BURN"): (
        "transfer node waits are not held (mlib _B5_NODE_WAIT_PHASES): a held "
        "TLI ended 3.7 s earlier on every Mun flight, which re-timed the arrival "
        "and moved B13's landing site"),
    ("waiting-for-node", "MJ-ASCENT"): _ASCENT_REASON,
    ("coast-to-apoapsis", "MJ-ASCENT"): _ASCENT_REASON,
    # The BDOCK machine engages the same MechJeb ascent on both legs (mlib
    # _bdock_ascent_entry_actions: ACTION_MJ_ENGAGE_ASCENT); the phase names
    # are BDOCK's alone.
    ("waiting-for-node", "STATION-ASCENT"): _ASCENT_REASON,
    ("waiting-for-node", "INT-ASCENT"): _ASCENT_REASON,
}

# (cause, phase) -> the mlib machine whose policy runs that stretch at 4x
# PHYSICS warp (the recorded coverage needs per-frame physics, so rails is not
# the policy there). The recoverable target is POLICY_PHYSICS_RATE, and only
# for a run of that machine: PARK and COAST name phases of other machines too.
# kx COAST warps whenever it is above the atmosphere with the throttle at zero,
# climbing or falling, so both coast causes are listed.
POLICY_PHYSICS_TARGETS: Dict[Tuple[str, str], str] = {
    ("dwell", "PARK"): "b5_decide",
    ("coast-to-apoapsis", "COAST"): "kxrw_decide",
    ("coast-to-entry", "COAST"): "kxrw_decide",
}

# (cause, phase) -> why a warp there may change the flight's outcome. The
# seconds stay recoverable; the recommendation is flagged.
OUTCOME_SENSITIVE: Dict[Tuple[str, str], str] = {
    ("waiting-for-node", "CIRCULARIZE"): (
        "pending re-fly (B22): circularizeNodeWaitWarp now holds this wait on B22 "
        "only; the park round-out's resulting orbit may move with the burn timing, "
        "as the held TLI moved B13's landing site, so the held flight must show an "
        "unchanged park and Jool arrival before any other lane opts in"),
}


def by_design_reason(cause: str, phase: str) -> str:
    """The policy reason a (cause, phase) stretch stays at 1x, or ""."""
    return BY_DESIGN_POLICY.get((cause, phase), "")


def capture_lead_seconds(half_burn: Optional[float]) -> Tuple[float, bool]:
    """(lead before node UT inside which a CAPTURE-BURN node wait is 1x by
    design, whether the half burn was known). Without a finite half burn the
    lead is the constants alone (short by the half burn)."""
    base = NODE_WAIT_ORIENT_LEAD_SECONDS + NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS
    if _is_finite(half_burn) and half_burn >= 0.0:
        return base + half_burn, True
    return base, False


def rendezvous_text_boundary(w: "RvNodeWait") -> float:
    """By-design start UT from a hold line's ``node-wait:`` text: node UT -
    half burn - lead (the half burn counts 0 when unread), pulled in to the
    ca-clamp UT when that is earlier, minus RV_EARLY_CANCEL_SECONDS. NaN when
    the node UT is unread."""
    if not _is_finite(w.node_ut):
        return float("nan")
    half = w.half_burn if _is_finite(w.half_burn) and w.half_burn >= 0.0 else 0.0
    lead = w.lead if _is_finite(w.lead) else NODE_WAIT_ORIENT_LEAD_SECONDS
    end = w.node_ut - half - lead
    if _is_finite(w.clamp_ut):
        end = min(end, w.clamp_ut)
    return end - RV_EARLY_CANCEL_SECONDS


def rendezvous_lead_boundary(node_ut: float, waits: Sequence["RvNodeWait"]
                             ) -> Tuple[float, str]:
    """(UT from which a RENDEZVOUS wait on ``node_ut`` is 1x by design, source).

    Source ``warp``: the hold's own warp target T for that node (not
    window-capped), boundary T - RV_EARLY_CANCEL_SECONDS. ``text``: a declined
    or window-capped hold, boundary from its ``node-wait:`` text
    (``rendezvous_text_boundary``). ``constants``: no hold line names the node
    (a pre-hold run, or a decline that printed no numbers), boundary node UT -
    NODE_WAIT_ORIENT_LEAD_SECONDS - RV_EARLY_CANCEL_SECONDS, short by the half
    burn. A window-capped warp is not the lead: the capped hold spends its node
    and the executor waits out the rest itself."""
    mine = [w for w in waits if _is_finite(w.node_ut) and _is_finite(node_ut)
            and abs(w.node_ut - node_ut) <= RV_NODE_IDENTITY_SECONDS]
    for w in mine:
        if w.warp_target is not None and _is_finite(w.warp_target) and not w.window_capped:
            return w.warp_target - RV_EARLY_CANCEL_SECONDS, "warp"
    for w in mine:
        b = rendezvous_text_boundary(w)
        if _is_finite(b):
            return b, "text"
    return node_ut - NODE_WAIT_ORIENT_LEAD_SECONDS - RV_EARLY_CANCEL_SECONDS, "constants"


def policy_target_rate(cause: str, phase: str, machine: Optional[str]) -> Optional[float]:
    """POLICY_PHYSICS_RATE when the mission policy runs (cause, phase) under
    physics warp for ``machine``; None otherwise (an unknown machine never
    matches: a log with no mission name predates the policy)."""
    scope = POLICY_PHYSICS_TARGETS.get((cause, phase))
    if scope is None or machine is None or machine != scope:
        return None
    return POLICY_PHYSICS_RATE


def outcome_sensitive_reason(cause: str, phase: str) -> str:
    return OUTCOME_SENSITIVE.get((cause, phase), "")


def _is_finite(v) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v)


def _f(text) -> float:
    try:
        return float(text)
    except (TypeError, ValueError):
        return float("nan")


def atmosphere_height(body: str) -> Optional[float]:
    """Atmosphere top for a stock body (0 for airless), None when unknown."""
    if body in ATMOSPHERE_HEIGHT_M:
        return ATMOSPHERE_HEIGHT_M[body]
    if body in STOCK_WARP_ALTITUDE_LIMITS:
        return 0.0
    return None


def max_legal_rails_rate(body: str, altitude_m: float) -> Optional[float]:
    """Best legal rails rate at ``altitude_m`` (legality: alt >= limit, the kRPC
    CanRailsWarpAt rule). None for an unknown body or a non-finite altitude:
    unlike mlib's fail-open COMMAND helper, a MEASUREMENT must not invent a
    rate it cannot justify."""
    limits = STOCK_WARP_ALTITUDE_LIMITS.get(body)
    if limits is None or not _is_finite(altitude_m):
        return None
    best = 0
    for idx, lim in enumerate(limits):
        if altitude_m >= lim:
            best = idx
    return RAILS_WARP_RATES[best]


def is_dwell_phase(phase: str) -> bool:
    if phase in DWELL_PHASE_EXTRAS:
        return True
    parts = phase.split("-")
    return any(tok in parts for tok in DWELL_PHASE_TOKENS)


# ---------------------------------------------------------------------------
# Mission log parse
# ---------------------------------------------------------------------------

_KV_RE = re.compile(r"([A-Za-z]+)=(\S*)")
_PHASE_RE = re.compile(r"^phase (?P<src>\S+) -> (?P<dst>\S+)")
_ACTION_RE = re.compile(r"^action (?P<kind>[a-z_]+)(?: value=(?P<value>\S+))?")
_HALF_BURN_RE = re.compile(r"\bhalfBurn=([^\s)]+)")
_RV_NODE_WAIT_RE = re.compile(
    r"node-wait: nodeUt=([^\s)]+) halfBurn=([^\s)]+) lead=([^\s)]+)")
_RV_CA_CLAMP_RE = re.compile(r"\bca-clamp caUt=([^\s)]+) .*?\bguard=([^\s)]+)")
_RV_GATE_PREFIX = "gate rvWarp "


@dataclass
class RvNodeWait:
    """One BDOCK rendezvous hold decision for one node (parsed from the
    hold's ``warp_to_ut`` action or its ``declined:`` gate line)."""
    node_ut: float
    half_burn: float
    lead: float
    warp_target: Optional[float]   # the hold's warp target; None = declined
    clamp_ut: float = float("nan")  # caUt - guard when ca-clamped, else NaN
    window_capped: bool = False


def parse_rv_node_wait(text: str, warp_target: Optional[float]) -> Optional[RvNodeWait]:
    """The node-wait numbers in a rendezvous hold line, or None."""
    m = _RV_NODE_WAIT_RE.search(text)
    if not m:
        return None
    clamp = float("nan")
    c = _RV_CA_CLAMP_RE.search(text)
    if c and _is_finite(_f(c.group(1))) and _is_finite(_f(c.group(2))):
        clamp = _f(c.group(1)) - _f(c.group(2))
    return RvNodeWait(node_ut=_f(m.group(1)), half_burn=_f(m.group(2)),
                      lead=_f(m.group(3)), warp_target=warp_target, clamp_ut=clamp,
                      window_capped="window-capped" in text)


@dataclass
class Sample:
    line_no: int
    visit: int
    phase: str
    mode: str
    rate: float
    ut: float
    ap: float
    pe: float
    alt: float
    vspd: float
    body: str
    next_body: str
    situation: str
    nodes: float
    node_ut: float
    ap_err: float


@dataclass
class Visit:
    index: int
    phase: str
    start_line: int
    start_ut: float
    samples: List[int] = field(default_factory=list)
    json_wall: Optional[float] = None
    json_game: Optional[float] = None
    warp_to: int = 0
    rails_cmds: int = 0
    physics_cmds: int = 0
    cancels: int = 0
    time_jumps: int = 0
    # halfBurn= of the machine's own node-wait line in this visit (NaN if none).
    half_burn: float = float("nan")
    # BDOCK rendezvous hold decisions in this visit, in log order.
    rv_waits: List["RvNodeWait"] = field(default_factory=list)
    # round(nodeUt, 1) -> (by-design start UT, source) for each RENDEZVOUS node
    # wait the policy pass judged (filled by apply_policy).
    rv_leads: Dict[float, Tuple[float, str]] = field(default_factory=dict)


@dataclass
class WarpCommand:
    visit: int
    after_sample: int  # global index of the last sample before the action (-1 = none)
    kind: str
    value: float


@dataclass
class MissionLog:
    visits: List[Visit]
    samples: List[Sample]
    commands: List[WarpCommand]
    mission_lines: int = 0
    telemetry_lines: int = 0
    malformed: int = 0
    has_ut: bool = False
    mission_name: Optional[str] = None


def parse_telemetry(msg: str) -> Optional[Dict[str, str]]:
    """Key/value map of one telemetry message, or None when it carries no
    ``warp=MODExRATE`` (the one field every telemetry generation has)."""
    if not msg.startswith("telemetry "):
        return None
    w = warp_audit._WARP_RE.search(msg)
    if not w:
        return None
    kv = dict(_KV_RE.findall(msg))
    kv["_mode"] = w.group("mode")
    kv["_rate"] = w.group("rate")
    return kv


def parse_mission_log(lines: Sequence[str]) -> MissionLog:
    """Parse a mission stdout log. Never raises on content: an unparseable
    ``[Mission]`` line is counted as malformed and skipped."""
    visits: List[Visit] = []
    samples: List[Sample] = []
    commands: List[WarpCommand] = []
    out = MissionLog(visits=visits, samples=samples, commands=commands)
    cur: Optional[Visit] = None
    for i, raw in enumerate(lines, start=1):
        line = raw.strip()
        if not line.startswith("[Mission]"):
            continue
        out.mission_lines += 1
        m = warp_audit._LINE_RE.match(line)
        if not m:
            out.malformed += 1
            continue
        tag, msg = m.group("tag"), m.group("msg")
        if out.mission_name is None and msg.startswith("mission start "):
            out.mission_name = dict(_KV_RE.findall(msg)).get("name")
        pm = _PHASE_RE.match(msg)
        if pm:
            if cur is None:
                cur = Visit(index=0, phase=pm.group("src"), start_line=i,
                            start_ut=float("nan"))
                visits.append(cur)
            ut = _f(dict(_KV_RE.findall(msg)).get("ut"))
            cur = Visit(index=len(visits), phase=pm.group("dst"), start_line=i,
                        start_ut=ut)
            visits.append(cur)
            continue
        if msg.startswith("telemetry "):
            out.telemetry_lines += 1
            kv = parse_telemetry(msg)
            if kv is None:
                out.malformed += 1
                continue
            if cur is None:
                cur = Visit(index=0, phase=tag, start_line=i, start_ut=float("nan"))
                visits.append(cur)
            ut = _f(kv.get("ut"))
            if _is_finite(ut):
                out.has_ut = True
            s = Sample(
                line_no=i, visit=cur.index, phase=cur.phase,
                mode=kv["_mode"], rate=_f(kv["_rate"]), ut=ut,
                ap=_f(kv.get("ap")), pe=_f(kv.get("pe")), alt=_f(kv.get("alt")),
                vspd=_f(kv.get("vspd")), body=kv.get("body", ""),
                next_body=kv.get("nextBody", ""), situation=kv.get("situation", ""),
                nodes=_f(kv.get("nodes")), node_ut=_f(kv.get("nodeUt")),
                ap_err=_f(kv.get("apErr")))
            cur.samples.append(len(samples))
            samples.append(s)
            continue
        if cur is None:
            continue
        if "TimeJump" in msg:
            cur.time_jumps += 1
        if msg.startswith(_RV_GATE_PREFIX):
            new = msg[len(_RV_GATE_PREFIX):].split(" | ")[0].partition("->")[2]
            if new.startswith("declined:"):
                rv = parse_rv_node_wait(new, None)
                if rv is not None:
                    cur.rv_waits.append(rv)
        am = _ACTION_RE.match(msg)
        if am and am.group("kind") == "warp_to_ut" and "rendezvous node-wait:" in msg:
            rv = parse_rv_node_wait(msg, _f(am.group("value")))
            if rv is not None:
                cur.rv_waits.append(rv)
        if am and "node-wait" in msg:
            hb = _HALF_BURN_RE.search(msg)
            if hb and _is_finite(_f(hb.group(1))):
                cur.half_burn = _f(hb.group(1))
        if not am:
            continue
        kind = am.group("kind")
        value = _f(am.group("value"))
        if kind in ("warp_to_ut", "warp_to"):
            cur.warp_to += 1
        elif kind == "set_rails_warp":
            cur.rails_cmds += 1
        elif kind == "set_physics_warp":
            cur.physics_cmds += 1
        elif kind == "cancel_warp":
            cur.cancels += 1
        else:
            continue
        commands.append(WarpCommand(visit=cur.index, after_sample=len(samples) - 1,
                                    kind=kind, value=value))
    return out


def match_visits(visits: List[Visit], warp_utilisation) -> int:
    """Attach mission.json ``warpUtilisation`` rows to log visits IN ORDER: each
    log visit takes the next unconsumed row with the same phase name (rows the
    log never reached are skipped). Returns the number matched."""
    rows = [r for r in (warp_utilisation or []) if isinstance(r, dict)]
    j = 0
    matched = 0
    for v in visits:
        k = j
        while k < len(rows) and rows[k].get("phase") != v.phase:
            k += 1
        if k >= len(rows):
            continue
        wall, game = rows[k].get("wallSeconds"), rows[k].get("gameSeconds")
        v.json_wall = float(wall) if _is_finite(wall) else None
        v.json_game = float(game) if _is_finite(game) else None
        matched += 1
        j = k + 1
    return matched


# ---------------------------------------------------------------------------
# Intervals: wall calibration + classification
# ---------------------------------------------------------------------------

@dataclass
class Interval:
    sample: int
    visit: int
    phase: str
    wall: float
    game: float
    rate: float
    mode: str
    alt_min: float
    body: str
    bucket: str = ""
    cause: str = ""
    target_rate: Optional[float] = None
    ut: float = float("nan")
    node_ut: float = float("nan")
    # Policy reason when this 1x / low-warp interval is left alone on purpose.
    by_design: str = ""


def _orbit_changed(a: Sample, b: Sample) -> Optional[bool]:
    """True when ap or pe moved more than the tolerance; None when unknowable
    (a value is NaN or the body changed, e.g. an SOI crossing)."""
    if a.body != b.body:
        return None
    vals = (a.ap, b.ap, a.pe, b.pe)
    if not all(_is_finite(x) for x in vals):
        return None
    return (abs(b.ap - a.ap) > ORBIT_STATIC_TOLERANCE_M
            or abs(b.pe - a.pe) > ORBIT_STATIC_TOLERANCE_M)


def in_vacuum(s: Sample) -> bool:
    """Rails-warp eligibility: never on the ground; above the body's atmosphere
    (airless bodies always qualify) or in an orbital situation."""
    if s.situation in GROUND_SITUATIONS:
        return False
    if s.situation in ORBITAL_SITUATIONS:
        return True
    top = atmosphere_height(s.body)
    if top is None or not _is_finite(s.alt):
        return False
    return s.alt > top


def idle_cause(s: Sample) -> str:
    """Deterministic idle-cause classifier, first match wins:
    dwell phase > attitude-align > waiting-for-node > soi-approach >
    coast-to-apoapsis (vspd > 0) > coast-to-entry (vspd < 0, pe below the
    atmosphere top, or below 0 m on an airless body) > coast-to-periapsis
    (vspd < 0) > other-coast."""
    if is_dwell_phase(s.phase):
        return "dwell"
    if _is_finite(s.ap_err) and abs(s.ap_err) > ATTITUDE_ALIGN_MIN_DEG:
        return "attitude-align"
    if _is_finite(s.nodes) and s.nodes > 0 and _is_finite(s.node_ut) \
            and (not _is_finite(s.ut) or s.node_ut > s.ut):
        return "waiting-for-node"
    if s.next_body not in UNKNOWN_BODY_TOKENS and s.next_body != s.body:
        return "soi-approach"
    if _is_finite(s.vspd) and s.vspd > 0:
        return "coast-to-apoapsis"
    if _is_finite(s.vspd) and s.vspd < 0:
        top = atmosphere_height(s.body)
        floor = top if top else 0.0
        if _is_finite(s.pe) and top is not None and s.pe < floor:
            return "coast-to-entry"
        return "coast-to-periapsis"
    return "other-coast"


def classify_interval(a: Sample, b: Sample) -> Tuple[str, str]:
    """(bucket, cause) for the interval opened by ``a`` and closed by ``b``."""
    rate = a.rate
    if not _is_finite(rate):
        return "unclassified", ""
    vac = in_vacuum(a)
    changed = _orbit_changed(a, b)
    if rate <= ONE_X_RATE_MAX:
        if not vac:
            return "atmoOrGround", ""
        if changed is None:
            return "unclassified", ""
        if changed:
            return "burn", ""
        return "idle", idle_cause(a)
    if a.mode == "PHYSICS":
        # Physics warp is a policy choice (PARK / kx COAST dwells, the DIY
        # flip) or kRPC WarpTo's own tail, never a rails factor left low.
        return "physicsWarp", ""
    if a.mode == "RAILS" and vac:
        best = max_legal_rails_rate(a.body, min(a.alt, b.alt) if _is_finite(b.alt) else a.alt)
        if best is not None and warp_audit.bucket_rate(rate) < best:
            return "lowWarp", LOW_WARP_CAUSE
    return "warped", ""


def build_intervals(log: MissionLog) -> List[Interval]:
    """Intervals with calibrated wall (contract items 1-4).

    A warp-to issued in or just before an interval (``action warp_to`` /
    ``warp_to_ut`` logged after the interval's opening sample or after the one
    before it: the action line is written before the runner dispatches, so one
    more rate-limited sample can precede the blocking call) whose game span
    exceeds twice the nominal line at its endpoint rate marks a warp-to gap: a
    blocking warp emits no
    telemetry, so the interval's game span was covered at warp, not at the rate
    its opening sample shows. A gap is bucketed ``warped`` and takes the wall the
    visit's other intervals leave over (split by game seconds); without a
    mission.json it gets game / best legal rate."""
    out: List[Interval] = []
    ss = log.samples
    gap_samples = {c.after_sample for c in log.commands
                   if c.kind in ("warp_to_ut", "warp_to") and c.after_sample >= 0}
    gaps: Dict[int, bool] = {}
    for i in range(len(ss) - 1):
        a, b = ss[i], ss[i + 1]
        game = b.ut - a.ut if (_is_finite(a.ut) and _is_finite(b.ut)) else float("nan")
        if _is_finite(game) and game < 0:
            game = float("nan")
        r_max = max(x for x in (a.rate, b.rate, 1.0) if _is_finite(x))
        finite_alts = [x for x in (a.alt, b.alt) if _is_finite(x)]
        alt_min = min(finite_alts) if finite_alts else float("nan")
        is_gap = ((i in gap_samples or (i - 1) in gap_samples) and _is_finite(game)
                  and game > 2.0 * NOMINAL_LINE_SECONDS * r_max)
        raw = NOMINAL_LINE_SECONDS
        if is_gap:
            legal = max_legal_rails_rate(a.body, alt_min) or r_max
            raw = max(NOMINAL_LINE_SECONDS, game / max(legal, r_max))
            bucket, cause = "warped", "warp-to-gap"
        else:
            if _is_finite(game) and game > 0:
                raw = max(NOMINAL_LINE_SECONDS, game / r_max)
            bucket, cause = classify_interval(a, b)
        gaps[i] = is_gap
        out.append(Interval(sample=i, visit=a.visit, phase=a.phase, wall=raw,
                            game=game, rate=a.rate, mode=a.mode, alt_min=alt_min,
                            body=a.body, bucket=bucket, cause=cause, ut=a.ut,
                            node_ut=a.node_ut))
    by_visit: Dict[int, List[Interval]] = {}
    for iv in out:
        by_visit.setdefault(iv.visit, []).append(iv)
    for v in log.visits:
        ivs = by_visit.get(v.index, [])
        if v.json_wall is None or not ivs:
            continue
        gap_ivs = [iv for iv in ivs if gaps.get(iv.sample)]
        rest = [iv for iv in ivs if not gaps.get(iv.sample)]
        rest_sum = sum(iv.wall for iv in rest)
        residual = v.json_wall - rest_sum
        gap_game = sum(iv.game for iv in gap_ivs)
        if gap_ivs and residual > 0 and gap_game > 0:
            for iv in gap_ivs:
                iv.wall = residual * iv.game / gap_game
            continue
        total = sum(iv.wall for iv in ivs)
        if total > 0:
            scale = v.json_wall / total
            for iv in ivs:
                iv.wall *= scale
    for iv in out:
        if not _is_finite(iv.game):
            iv.game = iv.wall * (iv.rate if _is_finite(iv.rate) else 1.0)
    return out


def apply_policy(log: MissionLog, intervals: List[Interval],
                 machine: Optional[str] = None) -> Dict[int, Tuple[float, bool]]:
    """Mark the idle / lowWarp intervals the mission warp policy leaves alone on
    purpose (contract item 8). Returns visit -> (capture lead seconds, half
    burn known) for every CAPTURE-BURN visit, and for every CIRCULARIZE visit
    whose node-wait line printed a half burn. The RENDEZVOUS lead applies only
    when ``machine`` is one of RENDEZVOUS_LEAD_MACHINES; its per-node
    boundaries land in each visit's ``rv_leads``."""
    leads: Dict[int, Tuple[float, bool]] = {}
    visits = {v.index: v for v in log.visits}
    for v in log.visits:
        if v.phase == CAPTURE_LEAD_KEY[1]:
            leads[v.index] = capture_lead_seconds(v.half_burn)
        elif v.phase == CIRCULARIZE_LEAD_KEY[1] and _is_finite(v.half_burn) \
                and v.half_burn >= 0.0:
            leads[v.index] = capture_lead_seconds(v.half_burn)
    lead_reasons = {CAPTURE_LEAD_KEY: CAPTURE_LEAD_REASON,
                    CIRCULARIZE_LEAD_KEY: CIRCULARIZE_LEAD_REASON}
    rv_on = machine in RENDEZVOUS_LEAD_MACHINES
    for iv in intervals:
        if iv.bucket not in ("idle", "lowWarp"):
            continue
        reason = by_design_reason(iv.cause, iv.phase)
        if not reason and (iv.cause, iv.phase) in lead_reasons \
                and iv.visit in leads and _is_finite(iv.ut) and _is_finite(iv.node_ut) \
                and iv.node_ut - iv.ut <= leads[iv.visit][0]:
            reason = lead_reasons[(iv.cause, iv.phase)]
        if not reason and rv_on and (iv.cause, iv.phase) == RENDEZVOUS_LEAD_KEY \
                and iv.visit in visits and _is_finite(iv.ut) and _is_finite(iv.node_ut):
            v = visits[iv.visit]
            start, source = rendezvous_lead_boundary(iv.node_ut, v.rv_waits)
            v.rv_leads.setdefault(round(iv.node_ut, 1), (start, source))
            if iv.ut >= start:
                reason = RENDEZVOUS_LEAD_REASON
        iv.by_design = reason
    return leads


# ---------------------------------------------------------------------------
# Runs and recoverable estimates
# ---------------------------------------------------------------------------

@dataclass
class Run:
    kind: str           # "idle" | "lowWarp" | "burn"
    visit: int
    phase: str
    first: int          # index into the interval list
    last: int
    wall: float
    game: float
    alt_min: float
    body: str
    recoverable: float = 0.0
    by_cause: Dict[str, float] = field(default_factory=dict)
    wall_by_cause: Dict[str, float] = field(default_factory=dict)
    optional: float = 0.0
    target_rate: Optional[float] = None
    # A by-design run: its estimate lands in by_design_* and never in
    # recoverable / by_cause.
    by_design: bool = False
    by_design_seconds: float = 0.0
    by_design_by_cause: Dict[str, float] = field(default_factory=dict)
    by_design_reasons: Dict[str, str] = field(default_factory=dict)
    policy_target: bool = False


def _target_rate(cause: str, phase: str, body: str, alt_min: float,
                 machine: Optional[str] = None) -> Optional[float]:
    policy = policy_target_rate(cause, phase, machine)
    if policy is not None:
        return policy
    if cause == "attitude-align":
        return PHYSICS_WARP_RATES[-1]
    return max_legal_rails_rate(body, alt_min)


def build_runs(intervals: List[Interval], machine: Optional[str] = None) -> List[Run]:
    """Contiguous same-bucket runs (idle / lowWarp / burn) inside one visit,
    split where the by-design flag changes, with their recoverable (or
    by-design, or optional for burns) wall seconds. ``machine`` is the mlib
    state machine of the mission (the policy-aware targets need it)."""
    runs: List[Run] = []
    cur: Optional[Run] = None
    for k, iv in enumerate(intervals):
        if iv.bucket not in ("idle", "lowWarp", "burn"):
            cur = None
            continue
        flag = bool(iv.by_design)
        if cur is not None and cur.kind == iv.bucket and cur.visit == iv.visit \
                and cur.last == k - 1 and cur.by_design == flag:
            cur.last = k
        else:
            cur = Run(kind=iv.bucket, visit=iv.visit, phase=iv.phase, first=k,
                      last=k, wall=0.0, game=0.0, alt_min=float("inf"), body=iv.body,
                      by_design=flag)
            runs.append(cur)
        if flag:
            cur.by_design_reasons.setdefault(iv.cause or iv.bucket, iv.by_design)
        cur.wall += iv.wall
        cur.game += iv.game if _is_finite(iv.game) else 0.0
        if _is_finite(iv.alt_min):
            cur.alt_min = min(cur.alt_min, iv.alt_min)
        key = iv.cause or iv.bucket
        cur.wall_by_cause[key] = cur.wall_by_cause.get(key, 0.0) + iv.wall
    for run in runs:
        ivs = intervals[run.first:run.last + 1]
        if run.kind == "burn":
            if run.wall >= LONG_BURN_MIN_SECONDS:
                run.optional = max(0.0, run.wall - run.game / BURN_PHYSICS_WARP_RATE)
            continue
        ideal_total = 0.0
        gains: Dict[str, float] = {}
        unknown = False
        for iv in ivs:
            rate = _target_rate(iv.cause, iv.phase, run.body, run.alt_min, machine)
            if rate is None:
                unknown = True
                break
            if policy_target_rate(iv.cause, iv.phase, machine) is not None:
                run.policy_target = True
            iv.target_rate = rate
            ideal = (iv.game if _is_finite(iv.game) else 0.0) / rate
            ideal_total += ideal
            gains[iv.cause] = gains.get(iv.cause, 0.0) + max(0.0, iv.wall - ideal)
        if unknown:
            continue
        run.target_rate = max_legal_rails_rate(run.body, run.alt_min)
        rec = max(0.0, run.wall - ideal_total - RAMP_SETTLE_OVERHEAD_SECONDS)
        split: Dict[str, float] = {}
        gsum = sum(gains.values())
        if gsum > 0 and rec > 0:
            for cause in sorted(gains):
                split[cause] = rec * gains[cause] / gsum
        if run.by_design:
            run.by_design_seconds = rec
            run.by_design_by_cause = split
        else:
            run.recoverable = rec
            run.by_cause = split
    return runs


# ---------------------------------------------------------------------------
# Other flags: oscillation, ladders, coast-class 1x
# ---------------------------------------------------------------------------

def warp_flags(log: MissionLog, intervals: List[Interval]) -> List[Dict]:
    """Per-visit warp-behaviour flags: oscillation (re-raises after a drop),
    slow rails ladders (wall from a set_rails_warp to reaching its rate), and
    1x wall inside warp_audit's coast-class phases."""
    flags: List[Dict] = []
    iv_by_sample = {iv.sample: iv for iv in intervals}
    for v in log.visits:
        warped = [log.samples[i].rate > ONE_X_RATE_MAX
                  if _is_finite(log.samples[i].rate) else False for i in v.samples]
        reraises = 0
        dropped = False
        for prev, now in zip(warped, warped[1:]):
            if prev and not now:
                dropped = True
            elif not prev and now and dropped:
                reraises += 1
        if reraises >= OSCILLATION_MIN_RERAISES:
            flags.append({"visit": v.index, "phase": v.phase, "kind": "warp-oscillation",
                          "count": reraises})
        if v.phase in warp_audit.VIOLATION_PHASES:
            x1 = sum(iv.wall for iv in intervals
                     if iv.visit == v.index and _is_finite(iv.rate)
                     and iv.rate <= ONE_X_RATE_MAX)
            if x1 >= COAST_X1_FLAG_SECONDS:
                flags.append({"visit": v.index, "phase": v.phase,
                              "kind": "coast-phase-1x", "seconds": round(x1, 1)})
        gaps = [iv for iv in intervals if iv.visit == v.index and iv.cause == "warp-to-gap"]
        if gaps:
            gw = sum(iv.wall for iv in gaps)
            gg = sum(iv.game for iv in gaps)
            legals = [max_legal_rails_rate(iv.body, iv.alt_min) for iv in gaps]
            legal = min(x for x in legals if x is not None) if any(legals) else None
            eff = gg / gw if gw > 0 else float("nan")
            if gw >= SLOW_LADDER_SECONDS and legal and _is_finite(eff) and eff < 0.5 * legal:
                flags.append({"visit": v.index, "phase": v.phase, "kind": "slow-warp-to-gap",
                              "hops": len(gaps), "seconds": round(gw, 1),
                              "gameSeconds": round(gg, 1), "effectiveRate": round(eff, 1),
                              "legalRate": legal})
    for cmd in log.commands:
        if cmd.kind != "set_rails_warp" or not _is_finite(cmd.value) or cmd.value <= 0:
            continue
        idx = int(cmd.value)
        if idx >= len(RAILS_WARP_RATES):
            continue
        target = RAILS_WARP_RATES[idx]
        v = log.visits[cmd.visit]
        later = [i for i in v.samples if i > cmd.after_sample]
        secs = 0.0
        reached = False
        for i in later:
            if warp_audit.bucket_rate(log.samples[i].rate) >= target:
                reached = True
                break
            iv = iv_by_sample.get(i)
            secs += iv.wall if iv else NOMINAL_LINE_SECONDS
        if not later:
            continue
        if secs >= SLOW_LADDER_SECONDS:
            flags.append({"visit": v.index, "phase": v.phase, "kind": "slow-warp-ladder",
                          "targetRate": target, "seconds": round(secs, 1),
                          "reached": reached})
    return flags


# ---------------------------------------------------------------------------
# Code sites
# ---------------------------------------------------------------------------

# Mission shell -> the mlib state machine it drives. Pinned against the shells
# by test_flighteff (each shell must call mlib.<decide>).
MISSION_MACHINES = {
    "b11_mun_orbit": "b5_decide", "b12_minmus_orbit": "b5_decide",
    "b13_mun_landing": "b5_decide", "b14_minmus_landing": "b5_decide",
    "b15_eve_flyby": "b5_decide", "b16_eve_orbit": "b5_decide",
    "b17_duna_direct": "b5_decide", "b19_dres_orbit": "b5_decide",
    "b1_pad_hop": "b1_decide", "b1_pad_hop_career": "b1_decide",
    "b20_moho_orbit": "b5_decide",
    "b21_eeloo_orbit": "b5_decide", "b22_jool_orbit": "b5_decide",
    "b23_ike_orbit": "b5_decide", "b24_gilly_orbit": "b5_decide",
    "b25_laythe_orbit": "b5_decide", "b26_laythe_vall": "b5_decide",
    "b28_laythe_jool": "b5_decide", "b29_jool_kerbin": "b5_decide",
    "b2_lko_ascent": "b2_decide", "b30_mun_minmus": "b5_decide",
    "b4_reentry": "b4_decide", "b5_mun_flyby": "b5_decide",
    "b6_minmus_flyby": "b5_decide", "b7_duna_flyby": "b5_decide",
    "bay1_runway_bays": "bay1_decide", "bdock_dock_transfer": "bdock_decide",
    "bdock_second_dock": "sdock_decide", "cl1_pod_impact": "cl1_decide",
    "cl3_refly_crew_tombstone": "cl3_decide", "d5_redock": "rdock_decide",
    "ci9_tip_dock": "tdock_decide",
    "ci11_krpc_recover": "krec_decide",
    "eva4_atmo_chute": "eva4_decide", "forge_lko": "forge_lko_decide",
    "forge_station": "forge_decide", "gs1_auto_chute_booster": "gs1_decide",
    "gs2_orbital_probe_deploy": "gs2_decide", "kx_rewind_watch": "kxrw_decide",
    "m3_loop_arrival_dwell": "m3_decide", "r1_rewind_loop": "r1_decide",
    "rf12s_refly_orbit_insert": "rfo_decide", "science_bench_recover": "sbr_decide",
    "v1_map_dwell": "v1_map_dwell_decide",
}

# (cause, phase) -> (what controls it, spec missionParams keys that exist in
# mlib). Phase "*" is the cause's fallback. test_flighteff pins every key to a
# params.get("<key>") in mlib, so no hint names a knob that does not exist.
# Verified against the shipped MechJeb2 DLL in the automation install.
NE_HINT = ("its NodeExecutor warps only to 600 s before "
           "ignition unless the craft is aligned; inside that it holds 1x until "
           "it is within 1 deg of the burn AND turning slower than 0.001 rad/s "
           "(decompiled MechJeb2 2.15.1 MechJebModuleNodeExecutor.StateWarpAlign "
           "/ AlignedAndSettled), so a slow-settling craft waits at 1x")

SITE_HINTS: Dict[Tuple[str, str], Tuple[str, Tuple[str, ...]]] = {
    ("dwell", "PARK"): (
        "PARK holds parkDwellSeconds of GAME time; the warp policy runs it at 4x "
        "PHYSICS warp while in-gate (rails would freeze the attitude and turn the "
        "recorded park into on-rails checkpoints) and drops to 1x for the last "
        "15 s, so the 1x left is the tumble gate and that tail",
        ("parkDwellSeconds",)),
    ("dwell", "HOLD-DEPART"): (
        "a deliberate 1x hold of dwellHoldSeconds game time for render "
        "observation, so shorten it rather than warp", ("dwellHoldSeconds",)),
    ("dwell", "HOLD-ARRIVE"): (
        "a deliberate 1x hold of dwellHoldSeconds game time for render "
        "observation, so shorten it rather than warp", ("dwellHoldSeconds",)),
    ("dwell", "HOLD-PARK"): (
        "a deliberate 1x hold of dwellHoldSeconds game time for render "
        "observation, so shorten it rather than warp", ("dwellHoldSeconds",)),
    ("dwell", "*"): ("deliberate hold phase run at 1x; check whether it must be "
                     "real time before warping it", ()),
    ("attitude-align", "DEORBIT"): (
        "DEORBIT waits at 1x for the kRPC autopilot to slew retrograde before the "
        "burn; rails warp freezes rotation, so the lever is the autopilot or "
        "physics warp", ("retroSettleSeconds", "maxAttitudeErrorDeg")),
    ("attitude-align", "CORRECTION-BURN"): (
        "the DIY burner's pre-burn flip runs at 1x or low physics warp until the "
        "attitude gate opens; rails warp freezes rotation",
        ("flipPhysicsWarpFactor", "correctionSettleSeconds", "maxAttitudeErrorDeg")),
    ("attitude-align", "*"): (
        "1x while the autopilot slews; rails warp freezes rotation, so the lever "
        "is the autopilot or physics warp", ()),
    ("waiting-for-node", "MJ-ASCENT"): (
        "MechJeb's ascent (mission_runner.py ACTION_MJ_ENGAGE_ASCENT) places a "
        "circularization node and runs it through ExecuteOneNode with autowarp "
        "on by default; " + NE_HINT, ()),
    ("waiting-for-node", "CIRCULARIZE"): (
        "1x coast to the circularization or park round-out node "
        "(mission_runner.py ACTION_MJ_EXECUTE_CIRCULARIZATION / "
        "ACTION_MJ_EXECUTE_NODES); circularizeNodeWaitWarp holds the round-out "
        "hand-off and rails-warps to its orient lead", ("circularizeNodeWaitWarp",)),
    ("waiting-for-node", "CORRECTION-BURN"): (
        "the DIY burner holds 1x before ignition (settle AND attitude gate)",
        ("correctionSettleSeconds",)),
    ("waiting-for-node", "*"): (
        "mission_runner.py ACTION_MJ_EXECUTE_NODES hands the node to MechJeb "
        "with autowarp on; " + NE_HINT, ()),
    ("soi-approach", "*"): (
        "1x around the SOI boundary; the machine stairs down inside soiLeadSeconds "
        "(soiNativeLeadSeconds / triggerNativeLeadSeconds when armed; the 'gate "
        "coastLead' lines name the lead applied)",
        ("soiLeadSeconds", "soiNativeLeadSeconds", "triggerNativeLeadSeconds",
         "coastWarpFactor", "approachMaxWarpFactor")),
    ("coast-to-apoapsis", "MJ-ASCENT"): (
        "MechJeb's ascent (mission_runner.py ACTION_MJ_ENGAGE_ASCENT) coasts to "
        "its circularization node with autowarp on by default; " + NE_HINT, ()),
    ("coast-to-apoapsis", "*"): ("1x coast toward apoapsis with no node", ()),
    ("coast-to-entry", "*"): (
        "1x coast on a trajectory into the atmosphere or the surface; a warp-to "
        "the entry (or B4-style bounded hops) is the lever", ()),
    ("coast-to-periapsis", "*"): ("1x coast toward periapsis with no node", ()),
    ("other-coast", "*"): ("1x coast with no node and no vertical-speed reading", ()),
    ("coast-to-apoapsis", "COAST"): (
        "COAST holds coastSeconds of GAME time for post-separation coverage; the kx "
        "warp policy runs it at 4x PHYSICS warp above the atmosphere with the "
        "throttle at zero and drops to 1x for the last 15 s",
        ("coastSeconds",)),
    ("waiting-for-node", "CAPTURE-BURN"): (
        "the b5 machine holds the hand-off and rails-warps itself to node UT - "
        "half burn - 120 s (mlib _b5_node_wait_begin), then mission_runner.py "
        "ACTION_MJ_EXECUTE_NODES hands the node to MechJeb, whose executor "
        "aligns at 1x; time BEFORE that lead means the hold did not arm (an "
        "unread input, a short window) or did not take; " + NE_HINT, ()),
    ("waiting-for-node", "RENDEZVOUS"): (
        "the bdock machine holds each rendezvous node (mlib _bdock_rv_node_wait): "
        "it rails-warps to node UT - half burn - 120 s (earlier under the 5 km "
        "closest-approach guard) and cancels 15 s early, then MechJeb's executor "
        "aligns at 1x; time BEFORE that lead means the hold did not arm (a decline, "
        "a short window, a node already held) or did not take; " + NE_HINT, ()),
    ("coast-to-apoapsis", "REENTRY"): (
        "an ASCENDING exo coast polls at 1x until vertical speed goes negative "
        "(rails hops only while descending)", ("warpAboveAltMeters", "warpHopSeconds")),
    ("coast-to-entry", "REENTRY"): (
        "rails hops toward the atmosphere only above warpAboveAltMeters",
        ("warpAboveAltMeters", "warpHopSeconds")),
    ("low-warp", "COAST-TO-TARGET"): (
        "coast rails factor below the altitude-legal maximum",
        ("coastWarpFactor", "approachMaxWarpFactor", "soiNativeLeadSeconds",
         "triggerNativeLeadSeconds")),
    ("low-warp", "TARGET-FLYBY"): (
        "flyby rails factor below the altitude-legal maximum",
        ("flybyWarpFactor", "flybyMaxWarpFactor")),
    ("low-warp", "PLAN-CORRECTION"): (
        "planning warp factor below the altitude-legal maximum", ("planWarpFactor",)),
    ("low-warp", "*"): (
        "rails factor below the altitude-legal maximum (MechJeb autowarp or a "
        "machine cap; see mlib.max_legal_rails_factor)", ()),
    (BURN_CAUSE, "*"): (
        "long 1x burn; physics warp x2 roughly halves it at a precision cost", ()),
}


def site_hint(cause: str, phase: str) -> Tuple[str, Tuple[str, ...]]:
    if (cause, phase) in SITE_HINTS:
        return SITE_HINTS[(cause, phase)]
    if (cause, "*") in SITE_HINTS:
        return SITE_HINTS[(cause, "*")]
    return ("1x %s in %s" % (cause, phase), ())


def code_site(mission: Optional[str], cause: str, phase: str) -> str:
    """One-line pointer at the code that controls (mission, phase, cause)."""
    hint, params = site_hint(cause, phase)
    if mission:
        machine = MISSION_MACHINES.get(mission)
        where = "harness/missions/%s.py" % mission
        if machine:
            where += " -> mlib.%s" % machine
    else:
        where = "harness/missions/<mission>.py"
    keys = ", ".join(params) if params else "none"
    return "%s phase %s: %s [spec keys: %s]" % (where, phase, hint, keys)


# ---------------------------------------------------------------------------
# Mission analysis
# ---------------------------------------------------------------------------

def _sum_into(dst: Dict[str, float], src: Dict[str, float]) -> None:
    for k, v in src.items():
        dst[k] = dst.get(k, 0.0) + v


def _rounded(d: Dict[str, float]) -> Dict[str, float]:
    return {k: round(v, 3) for k, v in sorted(d.items())}


def analyze_mission(log_text: str, mission_json: Optional[Dict]) -> Dict:
    """Phase table, buckets, runs, flags and recommendations for one mission."""
    log = parse_mission_log(log_text.splitlines())
    wu = (mission_json or {}).get("warpUtilisation") if isinstance(mission_json, dict) else None
    matched = match_visits(log.visits, wu)
    mission = (mission_json or {}).get("mission") if isinstance(mission_json, dict) else None
    mission = mission or log.mission_name
    machine = MISSION_MACHINES.get(mission) if mission else None
    intervals = build_intervals(log)
    leads = apply_policy(log, intervals, machine)
    runs = build_runs(intervals, machine)
    mission_wall = (mission_json or {}).get("wallSeconds") if isinstance(mission_json, dict) else None

    phases: List[Dict] = []
    for v in log.visits:
        ivs = [iv for iv in intervals if iv.visit == v.index]
        row = {"visit": v.index, "phase": v.phase,
               "wallSeconds": round(v.json_wall if v.json_wall is not None
                                    else sum(iv.wall for iv in ivs), 3),
               "gameSeconds": round(v.json_game if v.json_game is not None
                                    else sum(iv.game for iv in ivs), 3),
               "calibrated": v.json_wall is not None,
               "samples": len(v.samples),
               "warpTo": v.warp_to, "railsCommands": v.rails_cmds,
               "physicsCommands": v.physics_cmds, "cancels": v.cancels,
               "timeJumps": v.time_jumps}
        for b in BUCKETS:
            row[b] = round(sum(iv.wall for iv in ivs if iv.bucket == b), 3)
        rec: Dict[str, float] = {}
        des: Dict[str, float] = {}
        for r in runs:
            if r.visit == v.index:
                _sum_into(rec, r.by_cause)
                _sum_into(des, r.by_design_by_cause)
        row["recoverableByCause"] = _rounded(rec)
        row["recoverable"] = round(sum(rec.values()), 3)
        row["byDesignByCause"] = _rounded(des)
        row["byDesign"] = round(sum(des.values()), 3)
        row["byDesignWall"] = round(sum(iv.wall for iv in ivs if iv.by_design), 3)
        lead = leads.get(v.index)
        row["captureLeadSeconds"] = round(lead[0], 3) if lead else None
        row["captureLeadHalfBurnKnown"] = lead[1] if lead else None
        row["rendezvousLeads"] = [
            {"nodeUt": nu, "byDesignFromUt": round(b, 3), "source": src}
            for nu, (b, src) in sorted(v.rv_leads.items())] or None
        idle_by_cause: Dict[str, float] = {}
        for iv in ivs:
            if iv.bucket == "idle":
                idle_by_cause[iv.cause] = idle_by_cause.get(iv.cause, 0.0) + iv.wall
        row["idleByCause"] = _rounded(idle_by_cause)
        phases.append(row)

    run_rows = []
    for r in runs:
        if r.kind == "burn" and r.wall < LONG_BURN_MIN_SECONDS:
            continue
        if r.kind != "burn" and r.wall < 1.0:
            continue
        run_rows.append({
            "kind": r.kind, "visit": r.visit, "phase": r.phase,
            "wallSeconds": round(r.wall, 3), "gameSeconds": round(r.game, 3),
            "minAltitude": round(r.alt_min, 1) if math.isfinite(r.alt_min) else None,
            "body": r.body,
            "bestLegalRate": r.target_rate,
            "policyTarget": r.policy_target,
            "wallByCause": _rounded(r.wall_by_cause),
            "recoverable": round(r.recoverable, 3),
            "byDesign": r.by_design,
            "byDesignSeconds": round(r.by_design_seconds, 3),
            "optionalPhysicsWarpSaving": round(r.optional, 3),
        })

    # Recommendations: recoverable summed per (phase, cause), by-design per
    # (phase, cause) in rows of their own, burns separate.
    recs: Dict[Tuple[str, str, bool], Dict] = {}
    for r in runs:
        if r.kind == "burn":
            items = [(BURN_CAUSE, r.optional)] if r.optional > 0 else []
        elif r.by_design:
            items = list(r.by_design_by_cause.items())
        else:
            items = list(r.by_cause.items())
        for cause, secs in items:
            key = (r.phase, cause, r.by_design)
            reason = r.by_design_reasons.get(cause, "") if r.by_design else ""
            e = recs.setdefault(key, {"phase": r.phase, "cause": cause,
                                      "recoverable": 0.0, "byDesignSeconds": 0.0,
                                      "optional": cause == BURN_CAUSE,
                                      "byDesign": r.by_design,
                                      "byDesignReason": reason,
                                      "visits": []})
            if r.by_design:
                e["byDesignSeconds"] += secs
            else:
                e["recoverable"] += secs
            if r.visit not in e["visits"]:
                e["visits"].append(r.visit)
    rec_rows = []
    for e in recs.values():
        e["recoverable"] = round(e["recoverable"], 3)
        e["byDesignSeconds"] = round(e["byDesignSeconds"], 3)
        why = "" if e["byDesign"] else outcome_sensitive_reason(e["cause"], e["phase"])
        e["outcomeSensitive"] = bool(why)
        e["site"] = code_site(mission, e["cause"], e["phase"])
        if why:
            e["site"] += " OUTCOME-SENSITIVE: " + why
        rec_rows.append(e)
    rec_rows.sort(key=lambda e: (e["byDesign"], e["optional"],
                                 -(e["recoverable"] + e["byDesignSeconds"]),
                                 e["phase"], e["cause"]))

    totals = {b: round(sum(iv.wall for iv in intervals if iv.bucket == b), 3) for b in BUCKETS}
    sampled = sum(iv.wall for iv in intervals)
    idle_by_cause: Dict[str, float] = {}
    for iv in intervals:
        if iv.bucket == "idle":
            idle_by_cause[iv.cause] = idle_by_cause.get(iv.cause, 0.0) + iv.wall
    rec_by_cause: Dict[str, float] = {}
    des_by_cause: Dict[str, float] = {}
    for r in runs:
        _sum_into(rec_by_cause, r.by_cause)
        _sum_into(des_by_cause, r.by_design_by_cause)
    return {
        "mission": mission,
        "missionWallSeconds": mission_wall if _is_finite(mission_wall) else None,
        "missionLines": log.mission_lines,
        "telemetryLines": log.telemetry_lines,
        "malformedLines": log.malformed,
        "hasUt": log.has_ut,
        "visits": len(log.visits),
        "visitsCalibrated": matched,
        "sampledWallSeconds": round(sampled, 3),
        "buckets": totals,
        "idleByCause": _rounded(idle_by_cause),
        "recoverableByCause": _rounded(rec_by_cause),
        "recoverable": round(sum(rec_by_cause.values()), 3),
        "byDesignByCause": _rounded(des_by_cause),
        "byDesign": round(sum(des_by_cause.values()), 3),
        "byDesignWall": round(sum(iv.wall for iv in intervals if iv.by_design), 3),
        "optionalBurnSaving": round(sum(r.optional for r in runs), 3),
        "phases": phases,
        "runs": run_rows,
        "flags": warp_flags(log, intervals),
        "recommendations": rec_rows,
    }


# ---------------------------------------------------------------------------
# KSP.log anchors
# ---------------------------------------------------------------------------

_KSP_TS_RE = re.compile(r"^\[(?:LOG|WRN|ERR|EXC|AST)\s+(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,6}))?\]")
_SCENE_RE = re.compile(r"Scene Change : From (\S+) to (\S+)")
_RECV_RE = re.compile(r"\[TestCommands\] recv id=(\S+) cmd=(\S+)")
_EXEC_START_RE = re.compile(r"\[TestCommands\] exec id=(\S+) cmd=(\S+) start")
_EXEC_VERDICT_RE = re.compile(r"\[TestCommands\] exec id=(\S+) verdict=([A-Za-z0-9_-]+)")


def parse_ksp_time(line: str) -> Optional[float]:
    """Seconds since local midnight of a ``[LOG HH:MM:SS.mmm]`` line, or None."""
    m = _KSP_TS_RE.match(line)
    if not m:
        return None
    frac = m.group(4) or "0"
    return (int(m.group(1)) * 3600 + int(m.group(2)) * 60 + int(m.group(3))
            + float("0." + frac))


def parse_ksp_anchors(lines: Sequence[str]) -> Dict:
    """Wall anchors from a KSP.log. Times are UNWRAPPED seconds since the local
    midnight of the first line (a drop of more than 12 h is a midnight rollover).
    A line stamped more than KSP_MAX_FORWARD_JUMP_SECONDS after, or more than
    KSP_MAX_BACKWARD_JUMP_SECONDS before, its predecessor (after rollover) is an
    out-of-order stamp (a mod logger writes its own clock: observed
    ``[LOG 20:20:20.484] Log started at ...`` inside a 00:00 boot) and is skipped
    for timing, so it can neither fake a rollover nor move an anchor.
    Every anchor that is not found stays None."""
    out = {"init": None, "firstMainMenu": None, "scenes": [], "commands": {},
           "commandOrder": [], "loadgameComplete": [], "quit": None, "last": None,
           "timestamped": 0, "outOfOrder": 0}
    day = 0.0
    prev = None
    for raw in lines:
        t = parse_ksp_time(raw)
        if t is None:
            continue
        cand = t + day
        rolled = False
        if prev is not None and cand < prev - 43200.0:
            cand += 86400.0
            rolled = True
        if prev is not None and not (-KSP_MAX_BACKWARD_JUMP_SECONDS <= cand - prev
                                     <= KSP_MAX_FORWARD_JUMP_SECONDS):
            out["outOfOrder"] += 1
            continue
        if rolled:
            day += 86400.0
        t = cand
        prev = t
        out["timestamped"] += 1
        out["last"] = t
        if out["timestamped"] == 1 and "Log Initiated" in raw:
            out["init"] = t
        if "Scene Change" in raw:
            m = _SCENE_RE.search(raw)
            if m:
                out["scenes"].append((t, m.group(1), m.group(2)))
                if m.group(2) == "MAINMENU" and out["firstMainMenu"] is None:
                    out["firstMainMenu"] = t
            continue
        if "[TestCommands]" not in raw:
            continue
        m = _RECV_RE.search(raw)
        if m:
            cid = m.group(1)
            c = out["commands"].setdefault(cid, {"verb": m.group(2), "recv": None,
                                                 "start": None, "final": None,
                                                 "verdict": None, "pending": False})
            if c["recv"] is None:
                c["recv"] = t
                out["commandOrder"].append(cid)
            continue
        m = _EXEC_START_RE.search(raw)
        if m:
            c = out["commands"].setdefault(m.group(1), {"verb": m.group(2), "recv": None,
                                                        "start": None, "final": None,
                                                        "verdict": None, "pending": False})
            if c["start"] is None:
                c["start"] = t
            continue
        m = _EXEC_VERDICT_RE.search(raw)
        if m:
            c = out["commands"].get(m.group(1))
            if c is None:
                continue
            if m.group(2) == "PENDING":
                c["pending"] = True
            elif c["final"] is None:
                c["final"] = t
                c["verdict"] = m.group(2)
            continue
        if "loadgame complete" in raw:
            out["loadgameComplete"].append(t)
        elif "flushandquit: Application.Quit" in raw and out["quit"] is None:
            out["quit"] = t
    return out


def parse_utc_seconds_of_day(iso: str) -> Optional[float]:
    m = re.match(r"^\d{4}-\d{2}-\d{2}T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?Z$", iso or "")
    if not m:
        return None
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + int(m.group(3))


def parse_utc_epoch(iso: str) -> Optional[float]:
    """Seconds since 1970 for a ``YYYY-MM-DDTHH:MM:SSZ`` string (stdlib only)."""
    import calendar
    m = re.match(r"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d+)?Z$", iso or "")
    if not m:
        return None
    return float(calendar.timegm(tuple(int(x) for x in m.groups()) + (0, 0, 0)))


def derive_utc_offset(local_tod: float, utc_tod: float) -> float:
    """Local-minus-UTC offset in seconds, rounded to the nearest quarter hour
    (every real zone offset is a multiple of 15 min, and KSP starts seconds after
    run.py stamps startedUtc). Result in [-12 h, +14 h)."""
    delta = (local_tod - utc_tod) % 86400.0
    q = round(delta / 900.0) * 900.0
    if q >= 14 * 3600:
        q -= 86400.0
    return q


# ---------------------------------------------------------------------------
# Harness log facts
# ---------------------------------------------------------------------------

_RESULT_WRITTEN_RE = re.compile(r"\[Result\] result written (.+)$")
_COST_RE = re.compile(r"\[Cost\] scenario cost attempts=(\d+) wallTotal=(\d+)s terminal=(\S+)")
_LOCK_RE = re.compile(r"\[Lock\] run-lock acquired \(([^)]*)\)")
_LOCK_REFUSED_RE = re.compile(r"\[Lock\] run-lock refused \(([^)]*)\)")
_RETRY_RE = re.compile(r"\[Retry\] retry scenario=(\S+) attempt=(\d+) reason=(\S+)")


def run_id_from_result_path(path: str) -> str:
    base = re.split(r"[\\/]", path.strip())[-1]
    return base[:-5] if base.endswith(".json") else base


def parse_harness_log(lines: Sequence[str]) -> Dict[str, Dict]:
    """runId -> {lock, retries, cost} from one run.py log (which may cover a
    multi-scenario selection; each scenario block closes at its [Cost] line)."""
    lock = None
    refused = None
    out: Dict[str, Dict] = {}
    block_ids: List[str] = []
    block_retries: List[Dict] = []
    for raw in lines:
        m = _LOCK_RE.search(raw)
        if m and lock is None:
            lock = m.group(1)
            continue
        m = _LOCK_REFUSED_RE.search(raw)
        if m:
            refused = m.group(1)
            continue
        m = _RESULT_WRITTEN_RE.search(raw)
        if m:
            rid = run_id_from_result_path(m.group(1))
            if rid not in block_ids:
                block_ids.append(rid)
            continue
        m = _RETRY_RE.search(raw)
        if m:
            block_retries.append({"attempt": int(m.group(2)), "reason": m.group(3)})
            continue
        m = _COST_RE.search(raw)
        if m:
            cost = {"attempts": int(m.group(1)), "wallTotal": int(m.group(2)),
                    "terminal": m.group(3)}
            for rid in block_ids:
                out[rid] = {"cost": cost, "retries": list(block_retries)}
            block_ids, block_retries = [], []
    for rid in block_ids:
        out.setdefault(rid, {"cost": None, "retries": list(block_retries)})
    for rid in out:
        out[rid]["lock"] = lock
        out[rid]["lockRefused"] = refused
    return out


# ---------------------------------------------------------------------------
# Harness overhead
# ---------------------------------------------------------------------------

def _row(key: str, seconds: Optional[float], note: str = "") -> Dict:
    return {"key": key, "seconds": round(seconds, 3) if _is_finite(seconds) else None,
            "note": note}


def build_overhead(result: Optional[Dict], anchors: Optional[Dict],
                   mission_wall: Optional[float], harness: Optional[Dict]) -> Dict:
    """Wall seconds outside the mission. Every row whose anchor is missing is
    reported with seconds=None and the reason, never guessed."""
    rows: List[Dict] = []
    res = result if isinstance(result, dict) else {}
    started = parse_utc_seconds_of_day(res.get("startedUtc", ""))
    t0 = parse_utc_epoch(res.get("startedUtc", ""))
    t1 = parse_utc_epoch(res.get("endedUtc", ""))
    total = res.get("wallSeconds") if _is_finite(res.get("wallSeconds")) else (
        (t1 - t0) if (t0 is not None and t1 is not None) else None)
    a = anchors or {}
    init = a.get("init")
    offset = None
    rel = None
    if init is not None and started is not None:
        offset = derive_utc_offset(init, started)
        d0 = ((init - offset - started + 43200.0) % 86400.0) - 43200.0

        def rel(t):  # noqa: E306
            return None if t is None else d0 + (t - init)
    why_no_clock = ("no KSP.log" if not anchors else
                    "KSP.log has no 'Log Initiated' first line" if init is None else
                    "result has no startedUtc" if started is None else "")

    def r(t):
        return rel(t) if rel else None

    rows.append(_row("preLaunch", r(init), "startedUtc -> KSP 'Log Initiated' (admit, stage, launch)"
                     if rel else why_no_clock))
    boot = (a.get("firstMainMenu") - init) if (init is not None and a.get("firstMainMenu") is not None) else None
    rows.append(_row("kspBoot", boot, "'Log Initiated' -> first scene change to MAINMENU"
                     if boot is not None else (why_no_clock or "no MAINMENU scene change")))

    steps = [s for s in ((res.get("driver") or {}).get("steps") or []) if isinstance(s, dict)]
    cmds = a.get("commands") or {}
    step_rows: List[Dict] = []
    prev_end = a.get("firstMainMenu")
    gaps = 0.0
    gaps_known = prev_end is not None
    mission_env = None
    pending_mission = False
    for s in steps:
        if s.get("phase") == "mission" and not s.get("cmd"):
            pending_mission = True
            continue
        c = cmds.get(str(s.get("id")))
        if c is None or c.get("recv") is None:
            step_rows.append({"id": s.get("id"), "verb": s.get("cmd"), "seconds": None,
                              "note": "not in KSP.log"})
            gaps_known = False
            prev_end = None
            pending_mission = False
            continue
        dur = (c["final"] - c["recv"]) if c.get("final") is not None else None
        step_rows.append({"id": s.get("id"), "verb": c.get("verb"),
                          "seconds": round(dur, 3) if dur is not None else None,
                          "twoPhase": bool(c.get("pending")),
                          "note": "" if dur is not None else "no final verdict"})
        if prev_end is not None:
            if pending_mission:
                mission_env = c["recv"] - prev_end
            else:
                gaps += max(0.0, c["recv"] - prev_end)
        else:
            gaps_known = False
        pending_mission = False
        prev_end = c.get("final")
    rows.append(_row("seamSteps", sum(x["seconds"] for x in step_rows if x["seconds"] is not None)
                     if step_rows else None,
                     "sum of driver seam steps recv -> final verdict" if step_rows else "no driver steps"))
    rows.append(_row("seamGaps", gaps if (gaps_known and step_rows) else None,
                     "harness poll / write gaps between steps (incl. MAINMENU -> first recv)"
                     if (gaps_known and step_rows) else "anchor missing"))
    if mission_env is not None:
        mw = mission_wall if _is_finite(mission_wall) else None
        rows.append(_row("missionEnvelope", mission_env,
                         "previous step verdict -> next step recv (spans the mission)"))
        rows.append(_row("missionSpawnConnect",
                         (mission_env - mw) if mw is not None else None,
                         "envelope - mission wallSeconds (spawn, connect, teardown)"
                         if mw is not None else "no mission wallSeconds"))
    quit_t = a.get("quit")
    last = a.get("last")
    rows.append(_row("quit", (last - quit_t) if (quit_t is not None and last is not None) else None,
                     "'Application.Quit' -> last KSP.log line" if quit_t is not None
                     else "no Application.Quit line"))
    tail = None
    if rel and last is not None and t0 is not None and t1 is not None:
        tail = (t1 - t0) - rel(last)
    rows.append(_row("postQuitTail", tail,
                     "last KSP.log line -> endedUtc (verifiers, analyzer, collect-logs, snapshot)"
                     if tail is not None else (why_no_clock or "no endedUtc")))
    driver_ids = {str(s.get("id")) for s in steps if s.get("cmd")}
    in_mission = [{"id": cid, "verb": c.get("verb"),
                   "seconds": round(c["final"] - c["recv"], 3)
                   if (c.get("final") is not None and c.get("recv") is not None) else None}
                  for cid, c in sorted(cmds.items()) if str(cid) not in driver_ids]
    # The partition rows tile [startedUtc, endedUtc]; unaccounted is reported
    # only when every tile is known (a missing anchor would land in it).
    tiles = ["preLaunch", "kspBoot", "seamSteps", "seamGaps", "quit", "postQuitTail"]
    if any(s.get("phase") == "mission" and not s.get("cmd") for s in steps):
        tiles.append("missionEnvelope")
    by_key = {x["key"]: x["seconds"] for x in rows}
    tiles_known = all(by_key.get(k) is not None for k in tiles)
    known = sum(by_key[k] for k in tiles) if tiles_known else None
    attempts = res.get("attempt")
    aw = res.get("attemptsWallSeconds")
    h = harness or {}
    return {
        "utcOffsetSeconds": offset,
        "totalWallSeconds": total,
        "rows": rows,
        "steps": step_rows,
        "inMissionSeamCommands": in_mission,
        "unaccounted": round(total - known, 3) if (_is_finite(total) and known is not None) else None,
        "attempt": attempts,
        "attemptsWallSeconds": aw,
        "priorAttemptsWallSeconds": (aw - total) if (_is_finite(aw) and _is_finite(total)) else None,
        "lock": h.get("lock"),
        "lockWait": ("none: run.py never waits on the machine lock (a held lock is "
                     "refused as INVALID(instance-locked))"),
        "retries": h.get("retries") or [],
        "cost": h.get("cost"),
    }


# ---------------------------------------------------------------------------
# Run assembly, aggregate
# ---------------------------------------------------------------------------

RUN_ID_RE = re.compile(r"^(\d{4}-\d{2}-\d{2})_(\d{4})_(.+)$")


def scenario_from_run_id(run_id: str) -> str:
    m = RUN_ID_RE.match(run_id)
    if not m:
        return run_id
    sc = m.group(3)
    sc = re.sub(r"_a\d+$", "", sc)
    sc = re.sub(r"_run\d+$", "", sc)
    return sc


def analyze_run(run_id: str, result: Optional[Dict], mission_json: Optional[Dict],
                mission_log_text: Optional[str], ksp_log_lines: Optional[Sequence[str]],
                harness: Optional[Dict], notes: Optional[List[str]] = None) -> Dict:
    """Assemble one run's analysis from whatever artifacts exist."""
    notes = list(notes or [])
    res = result if isinstance(result, dict) else None
    mission = None
    if mission_log_text:
        mission = analyze_mission(mission_log_text, mission_json)
        if mission_json is None:
            notes.append("no mission.json: wall = raw line weights (uncalibrated)")
        if not mission["hasUt"]:
            notes.append("mission log predates ut=: game = wall x rate")
    anchors = parse_ksp_anchors(ksp_log_lines) if ksp_log_lines else None
    if anchors is None:
        notes.append("no KSP.log: overhead rows unknown")
    mwall = None
    if isinstance(mission_json, dict) and _is_finite(mission_json.get("wallSeconds")):
        mwall = float(mission_json["wallSeconds"])
    elif res and _is_finite(res.get("missionWallSeconds")):
        mwall = float(res["missionWallSeconds"])
    overhead = build_overhead(res, anchors, mwall, harness)
    total = overhead.get("totalWallSeconds")
    return {
        "runId": run_id,
        "scenario": (res or {}).get("scenarioId") or scenario_from_run_id(run_id),
        "verdict": (res or {}).get("verdict"),
        "subkind": (res or {}).get("subkind"),
        "totalWallSeconds": total,
        "missionWallSeconds": mwall,
        "harnessResidueSeconds": (round(total - mwall, 3)
                                  if (_is_finite(total) and _is_finite(mwall)) else None),
        "mission": mission,
        "overhead": overhead,
        "notes": notes,
    }


def dedupe_candidates(cands: List[Dict]) -> Tuple[List[Dict], int]:
    """One candidate per runId: the one with the most artifacts present; ties go
    to the lexicographically first results dir. Returns (kept, dropped_count)."""
    best: Dict[str, Tuple[int, Dict]] = {}
    for c in sorted(cands, key=lambda c: (c["runId"], c.get("dir", ""))):
        score = sum(1 for k in ("result", "missionJson", "missionLog", "kspLog") if c.get(k))
        cur = best.get(c["runId"])
        if cur is None or score > cur[0]:
            best[c["runId"]] = (score, c)
    kept = [best[k][1] for k in sorted(best)]
    return kept, len(cands) - len(kept)


def steps_by_verb(steps: Sequence[Dict]) -> List[Dict]:
    """Seam steps grouped by verb (count, total, max seconds; steps with no
    final verdict counted as unknown), ranked by total."""
    groups: Dict[str, Dict] = {}
    for st in steps:
        verb = str(st.get("verb"))
        g = groups.setdefault(verb, {"verb": verb, "count": 0, "total": 0.0,
                                     "max": 0.0, "unknown": 0})
        g["count"] += 1
        sec = st.get("seconds")
        if _is_finite(sec):
            g["total"] += sec
            g["max"] = max(g["max"], sec)
        else:
            g["unknown"] += 1
    out = [dict(g, total=round(g["total"], 3), max=round(g["max"], 3))
           for g in groups.values()]
    out.sort(key=lambda g: (-g["total"], g["verb"]))
    return out


def aggregate(runs: List[Dict]) -> Dict:
    """Totals per lane and per cause, ranked by recoverable wall seconds; the
    by-design estimate is totalled beside it, never inside it."""
    lanes: Dict[str, Dict] = {}
    causes: Dict[str, Dict] = {}
    lane_cause: Dict[Tuple[str, str, str, bool], Dict] = {}
    cause_run_ids: Dict[str, set] = {}
    cause_design_run_ids: Dict[str, set] = {}
    lane_cause_run_ids: Dict[Tuple[str, str, str, bool], set] = {}
    mission_wall = 0.0
    idle = 0.0
    physics = 0.0
    by_design = 0.0
    by_design_wall = 0.0
    sampled = 0.0
    overhead_keys: Dict[str, List[float]] = {}
    all_steps: List[Dict] = []
    for r in runs:
        all_steps.extend((r.get("overhead") or {}).get("steps", []))
        lane = lanes.setdefault(r["scenario"], {"scenario": r["scenario"], "runs": 0,
                                                "missionRuns": 0, "missionWall": 0.0,
                                                "idle": 0.0, "physicsWarp": 0.0,
                                                "recoverable": 0.0, "byDesign": 0.0,
                                                "byDesignWall": 0.0,
                                                "optionalBurnSaving": 0.0, "totalWall": 0.0})
        lane["runs"] += 1
        if _is_finite(r.get("totalWallSeconds")):
            lane["totalWall"] += r["totalWallSeconds"]
        for row in (r.get("overhead") or {}).get("rows", []):
            if row["seconds"] is not None:
                overhead_keys.setdefault(row["key"], []).append(row["seconds"])
        m = r.get("mission")
        if not m:
            continue
        lane["missionRuns"] += 1
        mw = r.get("missionWallSeconds") or m.get("sampledWallSeconds") or 0.0
        lane["missionWall"] += mw
        mission_wall += mw
        sampled += m.get("sampledWallSeconds", 0.0)
        lane["idle"] += m["buckets"]["idle"]
        idle += m["buckets"]["idle"]
        lane["physicsWarp"] += m["buckets"].get("physicsWarp", 0.0)
        physics += m["buckets"].get("physicsWarp", 0.0)
        lane["recoverable"] += m["recoverable"]
        lane["byDesign"] += m.get("byDesign", 0.0)
        by_design += m.get("byDesign", 0.0)
        lane["byDesignWall"] += m.get("byDesignWall", 0.0)
        by_design_wall += m.get("byDesignWall", 0.0)
        lane["optionalBurnSaving"] += m["optionalBurnSaving"]
        for rec in m["recommendations"]:
            cause = rec["cause"]
            design = bool(rec.get("byDesign"))
            if not rec["optional"]:
                c = causes.setdefault(cause, {"cause": cause, "recoverable": 0.0, "runs": 0,
                                              "byDesign": 0.0, "byDesignRuns": 0})
                if design:
                    c["byDesign"] += rec.get("byDesignSeconds", 0.0)
                    cause_design_run_ids.setdefault(cause, set()).add(r["runId"])
                else:
                    c["recoverable"] += rec["recoverable"]
                    cause_run_ids.setdefault(cause, set()).add(r["runId"])
            key = (r["scenario"], rec["phase"], cause, design)
            e = lane_cause.setdefault(key, {"scenario": r["scenario"], "phase": rec["phase"],
                                            "cause": cause, "recoverable": 0.0,
                                            "byDesignSeconds": 0.0, "runs": 0,
                                            "optional": rec["optional"], "byDesign": design,
                                            "byDesignReason": rec.get("byDesignReason", ""),
                                            "outcomeSensitive": bool(rec.get("outcomeSensitive")),
                                            "site": rec["site"]})
            e["recoverable"] += rec["recoverable"]
            e["byDesignSeconds"] += rec.get("byDesignSeconds", 0.0)
            lane_cause_run_ids.setdefault(key, set()).add(r["runId"])
    # A run with the same cause in several phases counts once per cause.
    for cause, ids in cause_run_ids.items():
        causes[cause]["runs"] = len(ids)
    for cause, ids in cause_design_run_ids.items():
        causes[cause]["byDesignRuns"] = len(ids)
    for key, ids in lane_cause_run_ids.items():
        lane_cause[key]["runs"] = len(ids)

    def rnd(d):
        return {k: (round(v, 3) if isinstance(v, float) else v) for k, v in d.items()}

    lane_rows = sorted((rnd(v) for v in lanes.values()),
                       key=lambda e: (-e["recoverable"], e["scenario"]))
    cause_rows = sorted((rnd(v) for v in causes.values()),
                        key=lambda e: (-e["recoverable"], -e["byDesign"], e["cause"]))
    lc_rows = sorted((rnd(v) for v in lane_cause.values()),
                     key=lambda e: (e["byDesign"], e["optional"],
                                    -(e["recoverable"] + e["byDesignSeconds"]),
                                    e["scenario"], e["phase"], e["cause"]))
    ov = {k: {"runs": len(v), "total": round(sum(v), 3),
              "mean": round(sum(v) / len(v), 3)} for k, v in sorted(overhead_keys.items())}
    return {
        "runs": len(runs),
        "missionRuns": sum(1 for r in runs if r.get("mission")),
        "missionWallSeconds": round(mission_wall, 3),
        "idleSeconds": round(idle, 3),
        "idleShareOfMissionWall": round(idle / mission_wall, 4) if mission_wall > 0 else None,
        "physicsWarpSeconds": round(physics, 3),
        "recoverableSeconds": round(sum(e["recoverable"] for e in cause_rows), 3),
        "byDesignSeconds": round(by_design, 3),
        "byDesignWallSeconds": round(by_design_wall, 3),
        "lanes": lane_rows,
        "causes": cause_rows,
        "laneCauses": lc_rows,
        "overhead": ov,
        "stepsByVerb": steps_by_verb(all_steps),
    }


def build_document(runs: List[Dict], skipped: List[Dict], duplicates: int) -> Dict:
    return {
        "schema": "flight-efficiency",
        "schemaVersion": SCHEMA_VERSION,
        "constants": {
            "rampSettleOverheadSeconds": RAMP_SETTLE_OVERHEAD_SECONDS,
            "orbitStaticToleranceMeters": ORBIT_STATIC_TOLERANCE_M,
            "longBurnMinSeconds": LONG_BURN_MIN_SECONDS,
            "burnPhysicsWarpRate": BURN_PHYSICS_WARP_RATE,
            "attitudeAlignMinDeg": ATTITUDE_ALIGN_MIN_DEG,
            "nodeWaitOrientLeadSeconds": NODE_WAIT_ORIENT_LEAD_SECONDS,
            "nodeWaitArrivalToleranceSeconds": NODE_WAIT_ARRIVAL_TOLERANCE_SECONDS,
            "rendezvousEarlyCancelSeconds": RV_EARLY_CANCEL_SECONDS,
            "policyPhysicsRate": POLICY_PHYSICS_RATE,
        },
        "byDesignPolicy": [{"cause": c, "phase": p, "reason": why}
                           for (c, p), why in sorted(BY_DESIGN_POLICY.items())]
        + [{"cause": CAPTURE_LEAD_KEY[0], "phase": CAPTURE_LEAD_KEY[1],
            "reason": CAPTURE_LEAD_REASON + " (only inside the capture lead)"},
           {"cause": CIRCULARIZE_LEAD_KEY[0], "phase": CIRCULARIZE_LEAD_KEY[1],
            "reason": CIRCULARIZE_LEAD_REASON + " (only inside the lead, and only "
            "in a visit whose node-wait line printed its half burn)"},
           {"cause": RENDEZVOUS_LEAD_KEY[0], "phase": RENDEZVOUS_LEAD_KEY[1],
            "reason": RENDEZVOUS_LEAD_REASON + " (only inside the rendezvous lead, "
            "BDOCK machines only)"}],
        "runs": runs,
        "skipped": skipped,
        "duplicatesDropped": duplicates,
        "aggregate": aggregate(runs),
    }


# ---------------------------------------------------------------------------
# Text rendering
# ---------------------------------------------------------------------------

def _s(v, fmt="%.0f") -> str:
    return (fmt % v) if _is_finite(v) else "?"


def _design_lines(rows: List[Dict], top: int, with_lane: bool) -> List[str]:
    L: List[str] = []
    for e in rows[:top]:
        lane = ("runs=%-3d %-34s " % (e["runs"], e["scenario"][:34])) if with_lane else ""
        L.append("    %7ss %s%s/%s -> %s" % (_s(e["byDesignSeconds"]), lane, e["phase"],
                                              e["cause"], e["byDesignReason"]))
    return L


RV_LEAD_SOURCE_TEXT = {
    "warp": "hold warp target - early cancel",
    "text": "declined or capped hold: node-wait text",
    "constants": "no hold line: constants only, short by the half burn",
}


def render_run(run: Dict, top: int = 8) -> str:
    L: List[str] = []
    L.append("FLIGHT EFFICIENCY  %s" % run["runId"])
    L.append("  scenario=%s verdict=%s total=%ss mission=%ss harnessResidue=%ss"
             % (run["scenario"], run.get("verdict"), _s(run.get("totalWallSeconds")),
                _s(run.get("missionWallSeconds")), _s(run.get("harnessResidueSeconds"))))
    for n in run.get("notes") or []:
        L.append("  note: %s" % n)
    m = run.get("mission")
    if m:
        b = m["buckets"]
        mw = m.get("missionWallSeconds") or m["sampledWallSeconds"]
        L.append("  mission=%s visits=%d (calibrated %d) telemetry=%d malformed=%d"
                 % (m.get("mission"), m["visits"], m["visitsCalibrated"],
                    m["telemetryLines"], m["malformedLines"]))
        L.append("  idle-x1 %ss (%s of mission wall)  lowWarp %ss  physicsWarp %ss  burn %ss  "
                 "atmo/ground %ss  warped %ss"
                 % (_s(b["idle"]), ("%.0f%%" % (100.0 * b["idle"] / mw)) if mw else "?",
                    _s(b["lowWarp"]), _s(b["physicsWarp"]), _s(b["burn"]),
                    _s(b["atmoOrGround"]), _s(b["warped"])))
        L.append("  recoverable %ss  byDesign %ss (of %ss 1x wall, not counted)  "
                 "(+optional burn %ss)"
                 % (_s(m["recoverable"]), _s(m["byDesign"]), _s(m["byDesignWall"]),
                    _s(m["optionalBurnSaving"])))
        L.append("")
        L.append("  %-3s %-18s %8s %10s %7s %7s %8s %7s %7s %8s %8s  %s"
                 % ("#", "PHASE", "WALL(s)", "GAME(s)", "IDLE", "LOWWARP", "PHYSWARP", "BURN",
                    "ATMO", "RECOVER", "BYDESIGN", "RECOVERABLE BY CAUSE"))
        for p in m["phases"]:
            causes = ", ".join("%s %s" % (c, _s(s)) for c, s in p["recoverableByCause"].items())
            L.append("  %-3d %-18s %8s %10s %7s %7s %8s %7s %7s %8s %8s  %s"
                     % (p["visit"], p["phase"][:18], _s(p["wallSeconds"], "%.1f"),
                        _s(p["gameSeconds"]), _s(p["idle"]), _s(p["lowWarp"]),
                        _s(p["physicsWarp"]), _s(p["burn"]), _s(p["atmoOrGround"]),
                        _s(p["recoverable"]), _s(p["byDesign"]), causes))
        for p in m["phases"]:
            if p.get("captureLeadSeconds") is not None:
                L.append("  %s lead: visit %d %s %ss before node UT (%s)"
                         % ("circularize" if p["phase"] == CIRCULARIZE_LEAD_KEY[1]
                            else "capture",
                            p["visit"], p["phase"], _s(p["captureLeadSeconds"], "%.1f"),
                            "half burn from the node-wait line"
                            if p["captureLeadHalfBurnKnown"]
                            else "no node-wait line: constants only, short by the half burn"))
            for e in p.get("rendezvousLeads") or []:
                L.append("  rendezvous lead: visit %d %s nodeUt=%s by design from ut %s (%s)"
                         % (p["visit"], p["phase"], _s(e["nodeUt"], "%.1f"),
                            _s(e["byDesignFromUt"], "%.1f"),
                            RV_LEAD_SOURCE_TEXT.get(e["source"], e["source"])))
        for f in m["flags"]:
            L.append("  flag: visit %d %s %s" % (f["visit"], f["phase"],
                                                  " ".join("%s=%s" % (k, f[k]) for k in sorted(f)
                                                           if k not in ("visit", "phase"))))
    o = run.get("overhead") or {}
    L.append("")
    L.append("  OVERHEAD (utcOffset=%s)" % (_s(o.get("utcOffsetSeconds")) if
                                            o.get("utcOffsetSeconds") is not None else "?"))
    for row in o.get("rows", []):
        L.append("    %-20s %8s  %s" % (row["key"], _s(row["seconds"], "%.1f"), row["note"]))
    for label, key in (("step", "steps"), ("in-mission", "inMissionSeamCommands")):
        for g in steps_by_verb(o.get(key, [])):
            L.append("    %-10s %-22s n=%-4d total %8s  max %8s%s"
                     % (label, g["verb"], g["count"], _s(g["total"], "%.2f"),
                        _s(g["max"], "%.2f"),
                        ("  unknown=%d" % g["unknown"]) if g["unknown"] else ""))
    L.append("    unaccounted %ss; attempt=%s attemptsWall=%s priorAttempts=%s lock=%s retries=%d"
             % (_s(o.get("unaccounted"), "%.1f"), o.get("attempt"), o.get("attemptsWallSeconds"),
                _s(o.get("priorAttemptsWallSeconds")), o.get("lock"), len(o.get("retries") or [])))
    recs = (m or {}).get("recommendations") or []
    counted = [e for e in recs if not e["byDesign"]]
    design = [e for e in recs if e["byDesign"]]
    if counted:
        L.append("")
        L.append("  RECOMMENDATIONS (ranked by recoverable wall seconds)")
        for e in counted[:top]:
            L.append("    %7ss %s%s/%s -> %s"
                     % (_s(e["recoverable"]), "(optional) " if e["optional"] else "",
                        e["phase"], e["cause"], e["site"]))
    if design:
        L.append("")
        L.append("  BY DESIGN (not counted; the warp policy leaves these at 1x)")
        L.extend(_design_lines(design, top, False))
    return "\n".join(L)


def render_aggregate(doc: Dict, top: int = 10) -> str:
    a = doc["aggregate"]
    L: List[str] = []
    L.append("FLIGHT EFFICIENCY AGGREGATE  runs=%d missionRuns=%d skipped=%d duplicates=%d"
             % (a["runs"], a["missionRuns"], len(doc["skipped"]), doc["duplicatesDropped"]))
    share = a["idleShareOfMissionWall"]
    L.append("  mission wall %ss, idle-x1 %ss (%s), physicsWarp %ss, recoverable %ss, "
             "byDesign %ss (of %ss 1x wall, not counted)"
             % (_s(a["missionWallSeconds"]), _s(a["idleSeconds"]),
                ("%.0f%%" % (100 * share)) if share is not None else "?",
                _s(a["physicsWarpSeconds"]), _s(a["recoverableSeconds"]),
                _s(a["byDesignSeconds"]), _s(a["byDesignWallSeconds"])))
    L.append("")
    L.append("  TOP LANE / PHASE / CAUSE")
    for e in [x for x in a["laneCauses"] if not x["optional"] and not x["byDesign"]][:top]:
        L.append("    %7ss runs=%-3d %-34s %-16s %-18s %s"
                 % (_s(e["recoverable"]), e["runs"], e["scenario"][:34], e["phase"][:16],
                    e["cause"], e["site"]))
    L.append("")
    L.append("  BY CAUSE (recoverable; byDesign beside it, not counted)")
    for e in a["causes"]:
        L.append("    %7ss runs=%-3d byDesign %7ss runs=%-3d %s"
                 % (_s(e["recoverable"]), e["runs"], _s(e["byDesign"]), e["byDesignRuns"],
                    e["cause"]))
    L.append("")
    L.append("  BY LANE")
    for e in a["lanes"][:max(top, 20)]:
        L.append("    %7ss recoverable  byDesign %7ss  idle %7ss  physWarp %6ss  "
                 "missionWall %7ss  runs=%d  %s"
                 % (_s(e["recoverable"]), _s(e["byDesign"]), _s(e["idle"]),
                    _s(e["physicsWarp"]), _s(e["missionWall"]), e["runs"], e["scenario"]))
    design = [x for x in a["laneCauses"] if x["byDesign"]]
    if design:
        L.append("")
        L.append("  BY DESIGN (not counted; the warp policy leaves these at 1x)")
        L.extend(_design_lines(design, top, True))
    L.append("")
    L.append("  OVERHEAD (mean seconds over runs where the anchor exists)")
    for k, v in a["overhead"].items():
        L.append("    %-20s mean %7s  total %8s  runs=%d" % (k, _s(v["mean"], "%.1f"),
                                                            _s(v["total"]), v["runs"]))
    L.append("")
    L.append("  SEAM STEPS BY VERB (driver steps, recv -> final verdict)")
    for g in a["stepsByVerb"][:top]:
        L.append("    %-24s n=%-5d total %9s  max %8s" % (g["verb"], g["count"],
                                                         _s(g["total"]), _s(g["max"], "%.1f")))
    opt = [x for x in a["laneCauses"] if x["optional"]][:5]
    if opt:
        L.append("")
        L.append("  OPTIONAL (long burns, physics warp x2; never in the totals)")
        for e in opt:
            L.append("    %7ss runs=%-3d %-34s %s" % (_s(e["recoverable"]), e["runs"],
                                                     e["scenario"][:34], e["phase"]))
    if doc["skipped"]:
        reasons: Dict[str, int] = {}
        for s in doc["skipped"]:
            reasons[s["reason"]] = reasons.get(s["reason"], 0) + 1
        L.append("")
        L.append("  SKIPPED: " + ", ".join("%s x%d" % (k, v) for k, v in sorted(reasons.items())))
    return "\n".join(L)


def sanitize(obj):
    """JSON-safe copy: non-finite floats become None, tuples become lists."""
    if isinstance(obj, float):
        return obj if math.isfinite(obj) else None
    if isinstance(obj, dict):
        return {str(k): sanitize(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [sanitize(v) for v in obj]
    return obj


def to_json(doc: Dict) -> str:
    return json.dumps(sanitize(doc), indent=1, sort_keys=True, allow_nan=False)
