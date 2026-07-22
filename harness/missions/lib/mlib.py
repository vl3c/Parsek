"""Pure mission-side decision library for the M-B1 mission library.

This module is the mission analogue of the harness ``hlib.py`` / provisioner
``provlib.py``: it holds every non-trivial decision a FLOWN mission makes as a
side-effect-free, kRPC-free, filesystem-free function, so the phase state
machines, the telemetry-assertion evaluators, the connect-retry decision, and
the mission-result serialization are all unit-testable with FAKE telemetry and
NO game (design Test Plan "Pure unit tests"). The kRPC-importing mission SHELLS
(``harness/missions/b1_pad_hop.py`` / ``b2_lko_ascent.py`` -- SEPARATE modules,
not built here) do all the I/O: connect the RPC client, read live telemetry into
a ``TelemetrySnapshot``, execute the ``Action``s this library emits, and write
the serialized mission-result JSON. They call into here for every decision.

Covered here (design docs/dev/design-autotest-mission-library.md):
  - B1 pad-hop phase state machine (``b1_initial_state`` / ``b1_decide``):
    PRELAUNCH -> ASCENT -> COAST -> DESCENT -> LANDED (design "Mission B1").
  - B2 LKO-ascent phase state machine (``b2_initial_state`` / ``b2_decide``):
    PRELAUNCH -> MJ-ASCENT -> CIRCULARIZE -> ORBIT (design "Mission B2").
  - B4 reentry+splashdown phase state machine (``b4_initial_state`` /
    ``b4_decide``): the B2 ascent verbatim, then ORBIT -> DEORBIT -> REENTRY ->
    SPLASHDOWN (terminal). Survival REQUIRED: no DOWN success terminal.
  - Telemetry-assertion evaluators (``evaluate_b1_assertions`` /
    ``evaluate_b2_assertions``): inclusive tolerance windows, NaN/Inf never
    passes, K-consecutive debounce over noisy warp-edge frames (design
    "Determinism guardrails" + edge 11).
  - Bounded connect-retry decision (``decide_connect_retry``, design
    "Connection lifecycle" step 2).
  - Mission-result build / serialize / parse / validate
    (``build_mission_result`` / ``serialize_mission_result`` /
    ``parse_mission_result`` / ``validate_mission_result``): the design's
    mission-result schema, deterministic + byte-identical for identical inputs.

Design authority: docs/dev/design-autotest-mission-library.md (Module M-B1). The
five mission verdict strings, the exact phase names, and the mission-result field
names are consumed VERBATIM from that doc; where the doc leaves a detail open the
simplest option is chosen and flagged below.

ASCII only; stdlib only; imports NOTHING from krpc (or any third party); no
filesystem, no network, no game access. Everything here is a pure function of its
arguments.

Resolved design-doc ambiguities (doc is authoritative; these fill the gaps):
  - DEBOUNCE K: the doc says "require K consecutive in-tolerance snapshots" but
    never pins K. Chosen: ``DEFAULT_DEBOUNCE_K = 3`` (smallest run that survives a
    single warp-edge outlier on either side), overridable per evaluator call.
  - B2 launch-site latitude: the inclination assertion is
    ``|inc - launchSiteLatitude| <= tol`` but the missionParams example omits the
    latitude. Chosen: default ``0.0`` (a due-east KSC launch targets ~0 deg,
    design "Mission B2"), overridable via ``B2Params.launch_site_latitude``.
  - decide() signature: the doc writes ``b1_decide(state, snapshot)``, but the
    machine needs the mission params (budgets, thresholds). Chosen: the params are
    carried INSIDE the state (built once by ``b1_initial_state`` /
    ``b2_initial_state``), so the per-frame decide stays ``(state, snapshot)``.
  - Transition signals: the doc lists the MechJeb-autopilot-enabled flag among the
    B2 transition inputs. Chosen: the orbit params (apoapsis / periapsis reaching
    target tolerance) are the deterministic transition signals the machine acts
    on; the MechJeb flags ride in the snapshot as carried evidence for the shell's
    logging. This keeps the transitions testable over pure orbit numbers.
  - Non-finite result values: a NaN/Inf assertion value is scrubbed to JSON
    ``null`` (never emitted as the invalid JSON token ``NaN``), mirroring the
    RewindReadbackGuard NaN semantics (design edge 11).
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass, field, replace
from typing import Dict, List, Optional, Tuple

MISSION_RESULT_SCHEMA = 1

# ---------------------------------------------------------------------------
# Vocabulary (consumed VERBATIM from the design; never re-spelled downstream).
# ---------------------------------------------------------------------------

# Mission verdicts (design Data Model "Mission result" + Terminology).
MISSION_OK = "MISSION-OK"
MISSION_ASSERT_FAIL = "MISSION-ASSERT-FAIL"
MISSION_CONNECT_TIMEOUT = "MISSION-CONNECT-TIMEOUT"
MISSION_FLAKE = "MISSION-FLAKE"
MISSION_ERROR = "MISSION-ERROR"

MISSION_VERDICTS: Tuple[str, ...] = (
    MISSION_OK, MISSION_ASSERT_FAIL, MISSION_CONNECT_TIMEOUT,
    MISSION_FLAKE, MISSION_ERROR,
)

# B1 phase names (design "Mission B1: pad-hop"). DOWN is the chute-deployed-impact
# SUCCESS terminal (operator decision 2026-07-20, option A): the hop flew, the chute
# deployed, and the craft reached the ground -- a breakup at touchdown is a
# successful B1 end (the stock Jumping Flea ALWAYS breaks apart at ~9 m/s touchdown
# vs the booster's 7 m/s tolerance; the craft-survives-intact contract is owned by
# the separate B4 mission).
B1_PRELAUNCH = "PRELAUNCH"
B1_ASCENT = "ASCENT"
B1_COAST = "COAST"
B1_DESCENT = "DESCENT"
B1_LANDED = "LANDED"
B1_DOWN = "DOWN"
B1_PHASES: Tuple[str, ...] = (B1_PRELAUNCH, B1_ASCENT, B1_COAST, B1_DESCENT,
                              B1_LANDED, B1_DOWN)

# B2 phase names (design "Mission B2: LKO-ascent").
B2_PRELAUNCH = "PRELAUNCH"
B2_MJ_ASCENT = "MJ-ASCENT"
B2_CIRCULARIZE = "CIRCULARIZE"
B2_ORBIT = "ORBIT"
B2_PHASES: Tuple[str, ...] = (B2_PRELAUNCH, B2_MJ_ASCENT, B2_CIRCULARIZE, B2_ORBIT)

# B4 phase names (mission b4_reentry: the B2 ascent, then deorbit / reentry /
# splashdown; see docs/dev/todo-and-known-bugs.md "B4 reentry + splashdown").
# ORBIT is NOT terminal in B4; SPLASHDOWN is the chute-descent phase whose landed
# situation is the terminal. B4's contract REQUIRES survival: there is no B1-style
# DOWN success terminal here -- any vessel-lost / frozen terminal in ANY phase is
# an ASSERT-FAIL loss.
B4_PRELAUNCH = "PRELAUNCH"
B4_MJ_ASCENT = "MJ-ASCENT"
B4_CIRCULARIZE = "CIRCULARIZE"
B4_ORBIT = "ORBIT"
B4_DEORBIT = "DEORBIT"
B4_REENTRY = "REENTRY"
B4_SPLASHDOWN = "SPLASHDOWN"
B4_PHASES: Tuple[str, ...] = (B4_PRELAUNCH, B4_MJ_ASCENT, B4_CIRCULARIZE, B4_ORBIT,
                              B4_DEORBIT, B4_REENTRY, B4_SPLASHDOWN)

# B5 phase names (mission b5_mun_flyby: the B2 ascent, then a MechJeb
# ManeuverPlanner Hohmann transfer to the Mun, a NodeExecutor-autowarped TLI
# burn, an optional course-correction refinement, a rails-warp coast across the
# SOI boundary, the flyby itself, and the return into Kerbin SOI; see
# docs/dev/todo-and-known-bugs.md "B5 Mun flyby / free-return"). RETURN is the
# success terminal: it is entered (and ``done`` set) the frame the vessel's SOI
# body is the home body again AFTER the flyby -- the settle tail then runs
# on-rails in Kerbin SOI. Like B4, survival is the contract: any vessel-lost /
# frozen terminal in ANY phase is an ASSERT-FAIL loss.
B5_PRELAUNCH = "PRELAUNCH"
B5_MJ_ASCENT = "MJ-ASCENT"
B5_CIRCULARIZE = "CIRCULARIZE"
B5_ORBIT = "ORBIT"
B5_PLAN_TRANSFER = "PLAN-TRANSFER"
B5_TRANSFER_BURN = "TRANSFER-BURN"
B5_PLAN_CORRECTION = "PLAN-CORRECTION"
B5_CORRECTION_BURN = "CORRECTION-BURN"
B5_COAST_TO_TARGET = "COAST-TO-TARGET"
B5_TARGET_FLYBY = "TARGET-FLYBY"
B5_RETURN = "RETURN"
B5_PHASES: Tuple[str, ...] = (B5_PRELAUNCH, B5_MJ_ASCENT, B5_CIRCULARIZE, B5_ORBIT,
                              B5_PLAN_TRANSFER, B5_TRANSFER_BURN, B5_PLAN_CORRECTION,
                              B5_CORRECTION_BURN, B5_COAST_TO_TARGET, B5_TARGET_FLYBY,
                              B5_RETURN)

# Connect-retry decision tokens (design "Connection lifecycle" step 2).
CONNECT_RETRY = "RETRY"
CONNECT_TIMEOUT = "TIMEOUT"

# Action kinds the phase machines emit for the shell to execute (raw kRPC for
# B1, KRPC.MechJeb for B2). The shell maps a kind to the actual RPC call.
ACTION_SET_THROTTLE = "set_throttle"          # value = throttle fraction
ACTION_CUT_THROTTLE = "cut_throttle"          # value = 0.0
ACTION_ACTIVATE_STAGE = "activate_stage"      # value = None
ACTION_DEPLOY_CHUTE = "deploy_chute"          # value = None
ACTION_MJ_SET_TARGET_APOAPSIS = "mj_set_target_apoapsis"   # value = metres
ACTION_MJ_ENABLE_AUTOSTAGE = "mj_enable_autostage"         # value = None
ACTION_MJ_ENGAGE_ASCENT = "mj_engage_ascent"               # value = None
ACTION_MJ_EXECUTE_CIRCULARIZATION = "mj_execute_circularization"  # value = None
# B4 deorbit/reentry actions. AP_* drive kRPC's NATIVE AutoPilot (vessel.auto_pilot:
# reference_frame = vessel.orbital_reference_frame, target_direction = (0, -1, 0)
# = orbital retrograde, engage() / disengage(); surface verified against the
# installed krpc 0.5.4 python client source in harness/missions/.venv), NOT
# MechJeb SmartASS. WARP_TO carries an ABSOLUTE target UT; the runner implements
# it with sc.warp_to(ut) (blocking RAILS warp -- permitted, the B4 spec sets
# allow_rails_warp).
ACTION_AP_POINT_RETROGRADE = "ap_point_retrograde"         # value = None
ACTION_AP_DISENGAGE = "ap_disengage"                       # value = None
ACTION_WARP_TO = "warp_to"                                 # value = target UT (s)
# B5 transfer-planning actions (KRPC.MechJeb ManeuverPlanner + NodeExecutor;
# surfaces verified against the darchambault KRPC.MechJeb 0.8.1 source at the
# provisioner's pinned commit: ManeuverPlanner.OperationTransfer /
# OperationCourseCorrection.CourseCorrectFinalPeA / Operation.MakeNodes,
# NodeExecutor.Autowarp / ExecuteAllNodes). SET_TARGET_BODY carries the body
# NAME in ``text`` (Action.value stays float-only); the runner resolves it via
# space_center.bodies[name]. The PLAN_* runner cases wrap make_nodes in
# try/except (a no-encounter plan throws server-side OperationException) so a
# failed plan is a logged warn + no node, and the machine's bounded re-plan /
# fall-through logic owns the outcome -- never an unhandled mission error.
ACTION_SET_TARGET_BODY = "set_target_body"                 # text = body name
ACTION_MJ_PLAN_TRANSFER = "mj_plan_transfer"               # value = None
ACTION_MJ_PLAN_COURSE_CORRECT = "mj_plan_course_correct"   # value = periapsis m
ACTION_MJ_EXECUTE_NODES = "mj_execute_nodes"               # value = None (autowarp)
ACTION_MJ_ABORT_AND_CLEAR_NODES = "mj_abort_and_clear_nodes"  # value = None

# TARGET-FLYBY impact-warp guard: below this altitude with a SUB-SURFACE
# periapsis the machine stops issuing warp hops and polls at 1x, so a crash
# happens under live telemetry (clean vessel-lost terminal in seconds) instead
# of inside a blocking warp_to wedged by the paused Flight Results dialog.
IMPACT_WARP_GUARD_ALT = 400_000.0

# K-consecutive debounce depth (see module docstring).
DEFAULT_DEBOUNCE_K = 3

# Below this active-stage solid-fuel remaining the SRB is treated as exhausted
# (design "ASCENT -> COAST: when the active-stage solid fuel is exhausted").
FUEL_EXHAUSTED_EPS = 1e-6


# ---------------------------------------------------------------------------
# Finite / debounce primitives (pure). NaN/Inf never counts as in-tolerance
# (design edge 11: the most dangerous silent pass).
# ---------------------------------------------------------------------------


def _is_finite(x) -> bool:
    """True iff ``x`` is a real, finite number. Excludes bool (a bool is an int
    subclass but never a telemetry reading) and any NaN/Inf. Used everywhere a
    telemetry value gates a decision so a transient NaN frame can never pass a
    comparison as True."""
    if isinstance(x, bool):
        return False
    if not isinstance(x, (int, float)):
        return False
    return math.isfinite(x)


def _has_k_consecutive_true(flags, k: int) -> bool:
    """True iff ``flags`` contains a run of at least ``k`` consecutive True.

    This is the debounce core (design "Determinism guardrails" / edge 11): the
    per-frame in-tolerance booleans are scanned for a settled run of K, so a
    single warp-edge outlier (one False among True) breaks a run but the
    surrounding frames still re-establish K, while a persistent out-of-tolerance
    stretch never reaches K. ``k <= 0`` is treated as 1 (a non-positive debounce
    depth would make every empty sequence trivially pass, which is the opposite of
    the intent).
    """
    if k <= 0:
        k = 1
    run = 0
    for flag in flags:
        if flag:
            run += 1
            if run >= k:
                return True
        else:
            run = 0
    return False


# ---------------------------------------------------------------------------
# Telemetry snapshot + emitted action (kRPC-free structs, design Terminology).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class TelemetrySnapshot:
    """A frozen, kRPC-free snapshot of the flight quantities a phase decision or
    an assertion reads (design "Telemetry snapshot"). The shell fills this from
    live kRPC reads; the pure machine consumes it. Numeric fields default to
    benign values (fuel present, not descending, 1x warp) so a test constructs
    only the fields it exercises.
    """
    ut: float = 0.0
    altitude: float = 0.0
    vertical_speed: float = 0.0
    apoapsis: float = 0.0
    periapsis: float = 0.0
    eccentricity: float = 0.0
    inclination: float = 0.0
    situation: str = ""
    stage_solid_fuel: float = 1.0          # active-stage solid fuel remaining
    mj_autopilot_enabled: bool = False     # carried evidence (B2)
    mj_ascent_complete: bool = False       # carried evidence (B2)
    warp_mode: str = "NONE"                # NONE | RAILS | PHYSICS
    warp_rate: float = 1.0
    vessel_lost: bool = False              # runner-signaled: active vessel unreadable
                                           # (repeated telemetry-read failures); a
                                           # phase-independent terminal loss signal.
    # kRPC AutoPilot pointing error (deg); NaN when unreadable/not engaged.
    # Defaults to NaN, NOT 0.0: the B4 attitude gate treats NaN as not-aligned,
    # so a runner or fake that FORGETS to populate the field fails closed
    # instead of simulating a perfectly aligned ship (Fable review of PR #1335,
    # SF-2 - the exact failure mode this field exists to prevent).
    ap_error: float = float("nan")
    # Current SOI body name (orbit.body.name) and pending maneuver-node count
    # (len(vessel.control.nodes)) -- the B5 cross-SOI / node-execution evidence.
    # ``body`` defaults to "" (unknown), NOT a real body name: the B5 SOI gates
    # compare against the spec's home/target names, so an unpopulated field
    # matches NEITHER and fails closed (same fail-closed rationale as ap_error).
    body: str = ""
    node_count: int = 0


# ---------------------------------------------------------------------------
# Frozen-telemetry detection (design "First live B1 flown-mission run":
# vessel-destroyed terminal). Pure. When KSP destroys the active craft and hands
# active-vessel to a debris fragment, kRPC keeps reporting situation=FLYING with
# BIT-IDENTICAL orbit telemetry forever while UT advances; the phase machine would
# otherwise wait out its whole descent budget. These helpers detect that stall.
# ---------------------------------------------------------------------------

# The five telemetry fields whose bitwise-identical repetition (while UT advances)
# marks a dead/stale vessel object.
FrozenSignature = Tuple[float, float, float, float, float]


def frozen_signature(snapshot: TelemetrySnapshot) -> FrozenSignature:
    """The frozen-telemetry signature of a snapshot:
    ``(ut, altitude, vertical_speed, apoapsis, periapsis)``. ``advances_frozen``
    compares two of these to decide whether a live craft has gone stale."""
    return (snapshot.ut, snapshot.altitude, snapshot.vertical_speed,
            snapshot.apoapsis, snapshot.periapsis)


def advances_frozen(prev: Optional[FrozenSignature],
                    curr: Optional[FrozenSignature]) -> bool:
    """True iff ``curr`` is a FROZEN advance over ``prev``: the mission clock
    strictly advanced (``curr`` UT finite and STRICTLY greater than ``prev`` UT)
    while the OTHER four fields (altitude, vertical_speed, apoapsis, periapsis) are
    BITWISE-EXACTLY equal (``==``) to ``prev``'s.

    Exact equality is safe -- and in fact REQUIRED -- here: a LIVE craft's physics
    jitters the low mantissa bits of altitude / vertical speed / apsides on every
    single frame (integration noise, floating-origin re-centering), so two
    consecutive live frames are essentially never bit-identical across all four.
    Only a DEAD / stale vessel object -- KSP handed active-vessel to a destroyed
    craft's debris and kRPC keeps returning the last cached orbit -- returns the
    SAME floats forever while UT keeps ticking. A FROZEN UT (a paused game) does
    NOT count: UT must strictly advance, so a legitimately paused sim (identical
    full signature) is never mistaken for a dead vessel."""
    if prev is None or curr is None:
        return False
    prev_ut, curr_ut = prev[0], curr[0]
    if not _is_finite(prev_ut) or not _is_finite(curr_ut):
        return False
    if not (curr_ut > prev_ut):
        return False
    return curr[1:] == prev[1:]


def _advance_frozen_count(prev_sig: Optional[FrozenSignature], prev_count: int,
                          snapshot: TelemetrySnapshot,
                          limit: int) -> Tuple[FrozenSignature, int, bool]:
    """Advance the airborne frozen-telemetry counter for one frame (shared by the
    B1 and B2 machines). Returns ``(new_sig, new_count, tripped)``: a FROZEN advance
    over ``prev_sig`` increments the count, ANY non-frozen sample resets it to 0,
    and ``tripped`` is True iff the count reached ``limit`` (a vessel-lost terminal).
    The signature is always updated to the current frame so the next comparison uses
    the latest UT."""
    curr_sig = frozen_signature(snapshot)
    new_count = prev_count + 1 if advances_frozen(prev_sig, curr_sig) else 0
    return curr_sig, new_count, (new_count >= limit)


@dataclass(frozen=True)
class Action:
    """One control action the phase machine asks the shell to perform this frame.
    ``kind`` is one of the ``ACTION_*`` constants; ``value`` carries the numeric
    argument (throttle fraction, target apoapsis) or None for a no-arg action.
    ``text`` carries a string argument (the SET_TARGET_BODY body name) -- a
    separate field so ``value`` stays float-only for every numeric consumer.
    ``limit`` carries a secondary numeric bound (the PLAN_COURSE_CORRECT dv cap
    the runner disqualifies an oversized correction plan against)."""
    kind: str
    value: Optional[float] = None
    text: Optional[str] = None
    limit: Optional[float] = None


# ---------------------------------------------------------------------------
# Mission params (parsed from the spec missionParams block; carried in state).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class B1Params:
    """B1 pad-hop tuning (design [driver.missionParams] for b1_pad_hop). All are
    WINDOWS / thresholds / budgets, never golden trajectories."""
    throttle: float
    chute_deploy_alt: float
    ascent_timeout: float
    coast_timeout: float
    descent_timeout: float
    landed_situations: Tuple[str, ...]
    apoapsis_window: Tuple[float, float]   # (min, max), inclusive
    frozen_sample_limit: int = 10          # airborne frozen-telemetry samples ->
                                           # vessel-lost terminal (spec key
                                           # frozenTelemetrySamples)
    down_max_alt: float = 500.0            # DOWN requires the last finite altitude
                                           # at/below this: option A says "reached
                                           # the ground", so a post-chute loss AT
                                           # ALTITUDE stays an ASSERT-FAIL (spec
                                           # key downMaxAltMeters)


@dataclass(frozen=True)
class B2Params:
    """B2 LKO-ascent tuning (design [driver.missionParams] for b2_lko_ascent). All
    tolerances / budgets, never golden orbits."""
    target_apoapsis: float
    target_periapsis: float
    apo_error: float
    peri_error: float
    eccentricity_max: float
    inclination_error: float
    ascent_timeout: float
    circularize_timeout: float
    launch_site_latitude: float = 0.0      # KSC due-east target ~0 deg (see docstring)
    frozen_sample_limit: int = 10          # airborne frozen-telemetry samples ->
                                           # vessel-lost terminal (spec key
                                           # frozenTelemetrySamples)


@dataclass(frozen=True)
class B4Params:
    """B4 reentry+splashdown tuning (spec [driver.missionParams] for b4_reentry).
    The ascent half reuses B2's ascent params verbatim (target apsides + errors +
    the two ascent budgets); the deorbit/reentry half adds the burn target, the
    attitude-settle wait, the bounded warp-hop shape, the chute altitude, and the
    three descent-side phase budgets. All tolerances / thresholds / budgets, never
    a golden trajectory. B2's eccentricityMax / inclinationErrorDeg /
    launchSiteLatitude are deliberately ABSENT: B4 makes no orbital-precision
    assertions (its orbit is a waypoint, not the terminal), so those would be dead
    params here."""
    target_apoapsis: float
    target_periapsis: float
    apo_error: float
    peri_error: float
    ascent_timeout: float
    circularize_timeout: float
    deorbit_periapsis: float = 25000.0     # burn until periapsis <= this (metres)
    retro_settle_seconds: float = 10.0     # MINIMUM game-time settle before throttle-up
    max_attitude_error_deg: float = 5.0    # AND-gate: AutoPilot error must be at/below
                                           # this before the burn starts. The first live
                                           # B4 flight (2026-07-20) burned mid-flip on
                                           # the fixed 10s wait alone: the Kerbal X needs
                                           # a ~180 deg turn to retrograde, throttle-up
                                           # caught it pointing RADIAL, and the radial
                                           # burn raised apoapsis to 382km while pushing
                                           # periapsis through the exit gate.
    warp_above_alt: float = 70000.0        # bounded warp hops only above this altitude.
                                           # 70km = the atmosphere ceiling: below it KSP
                                           # cannot rails-warp, so a 120s hop runs at
                                           # physics warp with zero mid-call snapshots and
                                           # can blow through the chute gate (Fable review
                                           # of PR #1335, SF-3); hops are EXO-ONLY.
    warp_hop_seconds: float = 120.0        # one WARP_TO hop = now + this many seconds
    chute_deploy_alt: float = 3000.0       # deploy chutes at/below this altitude
    deorbit_timeout: float = 300.0
    reentry_timeout: float = 3600.0        # game-time; rails hops advance it fast
    descent_timeout: float = 600.0
    landed_situations: Tuple[str, ...] = ("LANDED", "SPLASHED")
    frozen_sample_limit: int = 10          # airborne frozen-telemetry samples ->
                                           # vessel-lost terminal (spec key
                                           # frozenTelemetrySamples)


def b1_params_from_dict(params: Dict) -> B1Params:
    """Build ``B1Params`` from a spec ``missionParams`` dict. The apoapsis window
    is the ``{min, max}`` sub-table (a WINDOW, design). Tolerant of int/float."""
    params = params or {}
    window = params.get("apoapsisWindowMeters", {}) or {}
    return B1Params(
        throttle=float(params.get("throttle", 1.0)),
        chute_deploy_alt=float(params.get("chuteDeployAltMeters", 2500)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 90)),
        coast_timeout=float(params.get("coastTimeoutSeconds", 180)),
        descent_timeout=float(params.get("descentTimeoutSeconds", 240)),
        landed_situations=tuple(params.get("landedSituations", ("LANDED", "SPLASHED"))),
        apoapsis_window=(float(window.get("min", 0.0)), float(window.get("max", 0.0))),
        frozen_sample_limit=int(params.get("frozenTelemetrySamples", 10)),
        down_max_alt=float(params.get("downMaxAltMeters", 500)),
    )


def b2_params_from_dict(params: Dict) -> B2Params:
    """Build ``B2Params`` from a spec ``missionParams`` dict."""
    params = params or {}
    return B2Params(
        target_apoapsis=float(params.get("targetApoapsisMeters", 80000)),
        target_periapsis=float(params.get("targetPeriapsisMeters", 80000)),
        apo_error=float(params.get("apoErrorMeters", 5000)),
        peri_error=float(params.get("periErrorMeters", 5000)),
        eccentricity_max=float(params.get("eccentricityMax", 0.02)),
        inclination_error=float(params.get("inclinationErrorDeg", 2.0)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 420)),
        circularize_timeout=float(params.get("circularizeTimeoutSeconds", 300)),
        launch_site_latitude=float(params.get("launchSiteLatitude", 0.0)),
        frozen_sample_limit=int(params.get("frozenTelemetrySamples", 10)),
    )


def b4_params_from_dict(params: Dict) -> B4Params:
    """Build ``B4Params`` from a spec ``missionParams`` dict."""
    params = params or {}
    return B4Params(
        target_apoapsis=float(params.get("targetApoapsisMeters", 80000)),
        target_periapsis=float(params.get("targetPeriapsisMeters", 80000)),
        apo_error=float(params.get("apoErrorMeters", 5000)),
        peri_error=float(params.get("periErrorMeters", 5000)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 420)),
        circularize_timeout=float(params.get("circularizeTimeoutSeconds", 300)),
        deorbit_periapsis=float(params.get("deorbitPeriapsisMeters", 25000)),
        retro_settle_seconds=float(params.get("retroSettleSeconds", 10)),
        max_attitude_error_deg=float(params.get("maxAttitudeErrorDeg", 5.0)),
        warp_above_alt=float(params.get("warpAboveAltMeters", 70000)),
        warp_hop_seconds=float(params.get("warpHopSeconds", 120)),
        chute_deploy_alt=float(params.get("chuteDeployAltMeters", 3000)),
        deorbit_timeout=float(params.get("deorbitTimeoutSeconds", 300)),
        reentry_timeout=float(params.get("reentryTimeoutSeconds", 3600)),
        descent_timeout=float(params.get("descentTimeoutSeconds", 600)),
        landed_situations=tuple(params.get("landedSituations", ("LANDED", "SPLASHED"))),
        frozen_sample_limit=int(params.get("frozenTelemetrySamples", 10)),
    )


@dataclass(frozen=True)
class B5Params:
    """B5 Mun-flyby tuning (spec [driver.missionParams] for b5_mun_flyby). The
    ascent half reuses B2's ascent params verbatim; the transfer half adds the
    target/home body names, the plan/burn/coast/flyby phase budgets, the bounded
    warp-hop shapes, the transfer-apoapsis floor, and the optional
    course-correction periapsis. All tolerances / thresholds / budgets, never a
    golden trajectory. Every *_timeout is GAME seconds (the rails hops and the
    NodeExecutor autowarp advance them fast)."""
    target_apoapsis: float
    target_periapsis: float
    apo_error: float
    peri_error: float
    ascent_timeout: float
    circularize_timeout: float
    target_body: str = "Mun"               # transfer target SOI body name
    home_body: str = "Kerbin"              # departure/return SOI body name
    transfer_min_apoapsis: float = 10_000_000.0
                                           # TRANSFER-BURN exit floor: the TLI burn
                                           # must have raised apoapsis at/above this
                                           # (evidence the executor actually burned,
                                           # not just consumed an empty node list)
    course_correct_periapsis: float = 60000.0
                                           # > 0: plan+execute a MechJeb course
                                           # correction to this target-flyby
                                           # periapsis after the TLI burn (pins the
                                           # flyby geometry, keeps the periapsis
                                           # off the terrain); 0 disables the two
                                           # correction phases entirely
    correction_trigger_alts: Tuple[float, ...] = (0.0, 6_000_000.0)
                                           # correction ROUNDS: COAST-TO-TARGET
                                           # enters PLAN-CORRECTION once per
                                           # entry when altitude crosses each
                                           # trigger (0 = immediately post-TLI).
                                           # Round 2+ exists because a single
                                           # early correction is LIVE-PROVEN
                                           # insufficient (flight 4, 2026-07-21:
                                           # the ~100 m/s post-TLI correction
                                           # flew, but a ~1.5 m/s lateral
                                           # executor residual over the 14,000 s
                                           # coast moved the flyby periapsis
                                           # from the intended 60 km to -29 km =
                                           # impact); a mid-coast refinement
                                           # prices the residual at a few m/s
                                           # (spec key correctionTriggerAltsMeters)
    max_correction_dv: float = 150.0       # dv cap (m/s) an acceptable correction
                                           # plan must fit under: a genuine
                                           # course correction is a small tweak,
                                           # and the second live flight
                                           # (2026-07-21) proved an oversized
                                           # "correction" (ap 11.4M -> 16.6M)
                                           # wedges the executor until the burn
                                           # budget flakes. The runner removes a
                                           # too-big plan's nodes, so PLAN-
                                           # CORRECTION times out and falls
                                           # through to the coast on the raw
                                           # Hohmann intercept (spec key
                                           # maxCorrectionDvMps)
    plan_timeout: float = 300.0            # PLAN-* phase budget (game s)
    plan_retry_seconds: float = 30.0       # re-issue a failed plan every this many
                                           # game seconds while no node appeared
    transfer_burn_timeout: float = 4000.0  # TRANSFER-/CORRECTION-BURN budget: the
                                           # NodeExecutor autowarps to the node (up
                                           # to ~1 orbit ahead) then burns
    coast_timeout: float = 400_000.0       # COAST-TO-TARGET budget (game s; the
                                           # LKO->Mun transfer coast is ~2 days)
    flyby_timeout: float = 300_000.0       # TARGET-FLYBY budget (game s)
    coast_warp_hop_seconds: float = 1800.0 # one COAST-TO-TARGET warp hop
    flyby_warp_hop_seconds: float = 600.0  # one TARGET-FLYBY warp hop (smaller so
                                           # the min-altitude evidence is sampled
                                           # reasonably through periapsis)
    target_periapsis_floor: float = 10000.0
                                           # flyby min-altitude assertion floor
                                           # (metres above the target body; the Mun
                                           # has ~7 km peaks)
    burn_stagnant_seconds: float = 120.0   # BURN-phase watchdog: once the orbit
                                           # has CHANGED since burn entry (a burn
                                           # happened) and then sat static at 1x
                                           # for this many game seconds with the
                                           # node still pending, the executor is
                                           # wedged holding a completed node ->
                                           # abort+clear and move on (spec key
                                           # burnStagnantSeconds). Pre-burn
                                           # attitude alignment (orbit unchanged
                                           # since entry) and RAILS autowarp
                                           # never count.
    burn_nostart_seconds: float = 600.0    # CORRECTION-BURN no-start watchdog:
                                           # orbit UNCHANGED since entry and
                                           # static at 1x for this long = the
                                           # executor never began (sixth live
                                           # flight 2026-07-22: round-2 execute
                                           # issued, no warp, no burn, wall
                                           # budget died) -> abort+clear and
                                           # consume the round. Must exceed the
                                           # worst-case pre-burn flip (~340 s on
                                           # the Kerbal X pod wheel; spec key
                                           # burnNoStartSeconds).
    frozen_sample_limit: int = 10          # airborne frozen-telemetry samples ->
                                           # vessel-lost terminal (spec key
                                           # frozenTelemetrySamples)


def b5_params_from_dict(params: Dict) -> B5Params:
    """Build ``B5Params`` from a spec ``missionParams`` dict."""
    params = params or {}
    return B5Params(
        target_apoapsis=float(params.get("targetApoapsisMeters", 80000)),
        target_periapsis=float(params.get("targetPeriapsisMeters", 80000)),
        apo_error=float(params.get("apoErrorMeters", 5000)),
        peri_error=float(params.get("periErrorMeters", 5000)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 420)),
        circularize_timeout=float(params.get("circularizeTimeoutSeconds", 300)),
        target_body=str(params.get("targetBodyName", "Mun")),
        home_body=str(params.get("homeBodyName", "Kerbin")),
        transfer_min_apoapsis=float(params.get("transferMinApoapsisMeters", 10_000_000)),
        course_correct_periapsis=float(params.get("courseCorrectPeriapsisMeters", 60000)),
        correction_trigger_alts=tuple(
            float(a) for a in params.get("correctionTriggerAltsMeters", (0.0, 6_000_000.0))),
        max_correction_dv=float(params.get("maxCorrectionDvMps", 150.0)),
        plan_timeout=float(params.get("planTimeoutSeconds", 300)),
        plan_retry_seconds=float(params.get("planRetrySeconds", 30)),
        transfer_burn_timeout=float(params.get("transferBurnTimeoutSeconds", 4000)),
        coast_timeout=float(params.get("coastTimeoutSeconds", 400_000)),
        flyby_timeout=float(params.get("flybyTimeoutSeconds", 300_000)),
        coast_warp_hop_seconds=float(params.get("coastWarpHopSeconds", 1800)),
        flyby_warp_hop_seconds=float(params.get("flybyWarpHopSeconds", 600)),
        target_periapsis_floor=float(params.get("targetPeriapsisFloorMeters", 10000)),
        burn_stagnant_seconds=float(params.get("burnStagnantSeconds", 120)),
        burn_nostart_seconds=float(params.get("burnNoStartSeconds", 600)),
        frozen_sample_limit=int(params.get("frozenTelemetrySamples", 10)),
    )


# ---------------------------------------------------------------------------
# B1 phase state machine (design "Mission B1: pad-hop"). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class B1State:
    """B1 pad-hop machine state. ``verdict`` is None while running; it is set to
    MISSION-FLAKE (with ``flake_phase`` naming the stuck phase) on a per-phase
    budget overrun. ``done`` is True at LANDED (flew to completion; assertions
    then decide OK vs ASSERT-FAIL) OR on a flake. ``peak_apoapsis`` tracks the
    max finite apoapsis seen for evidence."""
    params: B1Params
    phase: str = B1_PRELAUNCH
    phase_entry_ut: float = 0.0
    peak_apoapsis: Optional[float] = None
    chute_deployed: bool = False
    phases_reached: Tuple[str, ...] = (B1_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    done: bool = False
    # Frozen-telemetry (vessel-destroyed) detection carried across frames. On a
    # terminal loss ``loss_reason`` names it and ``done`` is set; resolve_flight_verdict
    # maps a non-None loss_reason to MISSION-ASSERT-FAIL (a destroyed vessel is a
    # deterministic mission failure, not a flake).
    frozen_sig: Optional[FrozenSignature] = None
    frozen_count: int = 0
    loss_reason: Optional[str] = None
    # Last FINITE altitude seen on a live (non-vessel_lost) frame. The DOWN
    # terminal requires it at/below downMaxAltMeters: option A's wording is
    # "reached the ground", so a craft lost at altitude after the chute deploys
    # (chute ripped, mid-air breakup) must NOT be awarded DOWN (Fable review of
    # PR #1335, SF-1).
    last_finite_altitude: Optional[float] = None
    # DOWN-terminal marker for the shell (operator decision 2026-07-20, option A):
    # a DOWN terminal means the vessel is GONE, so the fly loop's settle tail would
    # only gather vessel_lost / garbage frames -- the loop checks this via
    # getattr(state, "skip_settle_tail", False) and skips the tail. LANDED keeps
    # its settle tail (a surviving craft has real settled frames to sample).
    skip_settle_tail: bool = False


def b1_initial_state(params: B1Params) -> B1State:
    """Fresh B1 machine at PRELAUNCH (design). Params are carried in the state so
    the per-frame ``b1_decide`` keeps the ``(state, snapshot)`` signature."""
    return B1State(params=params)


def _b1_phase_budget(params: B1Params, phase: str) -> Optional[float]:
    """The bounded budget for a timed B1 phase, or None for the untimed
    PRELAUNCH / terminal LANDED phases (design "Every wait bounded")."""
    if phase == B1_ASCENT:
        return params.ascent_timeout
    if phase == B1_COAST:
        return params.coast_timeout
    if phase == B1_DESCENT:
        return params.descent_timeout
    return None


def _b1_over_budget(state: B1State, snapshot: TelemetrySnapshot) -> bool:
    """True iff the current timed phase has out-run its budget by ``snapshot.ut``
    (a non-finite UT never trips the timeout; the shell's outer watchdog is the
    backstop)."""
    budget = _b1_phase_budget(state.params, state.phase)
    if budget is None:
        return False
    if not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > budget


def _update_peak(peak: Optional[float], value: float) -> Optional[float]:
    if _is_finite(value) and (peak is None or value > peak):
        return value
    return peak


def b1_decide(state: B1State, snapshot: TelemetrySnapshot) -> Tuple[B1State, List[Action]]:
    """Advance the B1 pad-hop machine one frame; return (new_state, actions).

    Transitions (each a pure decision over the snapshot, design "Mission B1"):
      - PRELAUNCH -> ASCENT: on the FIRST decision, set throttle and activate the
        next stage (release clamps / ignite the SRB) -- the first real flight mod.
      - ASCENT -> COAST: when the active-stage solid fuel is exhausted, cut
        throttle. Bounded by ascentTimeoutSeconds.
      - COAST -> DESCENT: when past apoapsis (vertical speed goes negative).
        Bounded by coastTimeoutSeconds.
      - DESCENT: deploy the chute once altitude <= chuteDeployAltMeters, then
        DESCENT -> LANDED when the situation is a landed/splashed one. Bounded by
        descentTimeoutSeconds.
      - DESCENT -> DOWN (operator decision 2026-07-20, option A): when either
        vessel-lost signal fires (a runner vessel_lost snapshot, or the frozen
        counter reaching its limit) AND the chute is already deployed, the hop
        FLEW, the CHUTE DEPLOYED, and the craft REACHED THE GROUND -- a
        chute-deployed breakup at touchdown is a SUCCESSFUL end (the Jumping Flea
        always breaks apart at ~9 m/s vs the booster's 7 m/s tolerance; B4 owns
        the craft-survives-intact contract). DOWN is a real terminal: done, NO
        loss_reason, verdict stays None so the assertions decide. Without the
        chute deployed -- and in every other phase -- the loss stays the
        ASSERT-FAIL loss_reason terminal.
    Any timed phase that out-runs its budget yields MISSION-FLAKE naming the stuck
    phase (``state.verdict`` / ``state.flake_phase``), so a wedged autopilot never
    hangs. Once ``done`` the machine is idempotent (returns the state unchanged,
    no actions).
    """
    if state.done:
        return state, []

    peak = _update_peak(state.peak_apoapsis, snapshot.apoapsis)

    # Track the last FINITE altitude from live frames (a vessel_lost snapshot
    # carries benign defaults and must not contribute). The DOWN eligibility
    # gate reads this: option A's "reached the ground" leg.
    if not snapshot.vessel_lost and _is_finite(snapshot.altitude):
        state = replace(state, last_finite_altitude=snapshot.altitude)

    # Runner-signaled vessel loss (unreadable active vessel after repeated telemetry
    # failures): a phase-INDEPENDENT terminal (design vessel-destroyed terminal).
    # In DESCENT with the chute deployed AND the craft last seen at/below
    # downMaxAltMeters this is the DOWN success terminal (option A: flew + chute
    # + reached the ground); a post-chute loss AT ALTITUDE (chute ripped,
    # mid-air breakup) stays a deterministic mission failure (Fable review of
    # PR #1335, SF-1).
    if snapshot.vessel_lost:
        if _b1_down_eligible(state):
            return _b1_enter_down(state, snapshot.ut, peak), []
        return replace(
            state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason=_b1_loss_reason_with_altitude(
                state, "vessel-lost (unreadable after repeated telemetry failures)")), []

    # Frozen-telemetry (vessel-destroyed) detection, AIRBORNE phases only: PRELAUNCH
    # pad telemetry is legitimately static, so the detector never runs there (nor
    # after done). When KSP hands active-vessel to a destroyed craft's debris, kRPC
    # reports bit-identical orbit telemetry forever while UT ticks; catch that here
    # rather than wait out the whole descent budget.
    if state.phase in (B1_ASCENT, B1_COAST, B1_DESCENT):
        limit = state.params.frozen_sample_limit
        new_sig, new_count, tripped = _advance_frozen_count(
            state.frozen_sig, state.frozen_count, snapshot, limit)
        if tripped:
            if _b1_down_eligible(state):
                down = _b1_enter_down(state, snapshot.ut, peak)
                return replace(down, frozen_sig=new_sig, frozen_count=new_count), []
            return replace(
                state, peak_apoapsis=peak, frozen_sig=new_sig, frozen_count=new_count,
                done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=_b1_loss_reason_with_altitude(
                    state, "vessel-lost (telemetry frozen %d consecutive samples "
                           "while airborne; vessel presumed destroyed)" % limit)), []
        state = replace(state, frozen_sig=new_sig, frozen_count=new_count)

    if state.phase == B1_PRELAUNCH:
        actions = [Action(ACTION_SET_THROTTLE, state.params.throttle),
                   Action(ACTION_ACTIVATE_STAGE)]
        return _b1_enter(state, B1_ASCENT, snapshot.ut, peak), actions

    if state.phase == B1_ASCENT:
        if _is_finite(snapshot.stage_solid_fuel) and snapshot.stage_solid_fuel <= FUEL_EXHAUSTED_EPS:
            return (_b1_enter(state, B1_COAST, snapshot.ut, peak),
                    [Action(ACTION_CUT_THROTTLE, 0.0)])
        return _b1_stay_or_flake(state, snapshot, peak), []

    if state.phase == B1_COAST:
        if _is_finite(snapshot.vertical_speed) and snapshot.vertical_speed < 0.0:
            return _b1_enter(state, B1_DESCENT, snapshot.ut, peak), []
        return _b1_stay_or_flake(state, snapshot, peak), []

    if state.phase == B1_DESCENT:
        actions: List[Action] = []
        chute_deployed = state.chute_deployed
        if (not chute_deployed and _is_finite(snapshot.altitude)
                and snapshot.altitude <= state.params.chute_deploy_alt):
            actions.append(Action(ACTION_DEPLOY_CHUTE))
            chute_deployed = True
        if snapshot.situation in state.params.landed_situations:
            landed = _b1_enter(state, B1_LANDED, snapshot.ut, peak)
            return replace(landed, chute_deployed=chute_deployed), actions
        stayed = _b1_stay_or_flake(state, snapshot, peak)
        return replace(stayed, chute_deployed=chute_deployed), actions

    # Unknown phase: defensively terminate as an error-shaped flake so the shell
    # never spins. (Unreachable given the enum above.)
    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase, done=True,
                   peak_apoapsis=peak), []


def _b1_enter(state: B1State, new_phase: str, ut: float, peak: Optional[float]) -> B1State:
    """Transition into ``new_phase``, stamping the phase-entry UT for the budget
    clock and appending to ``phases_reached``. LANDED sets ``done``."""
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        peak_apoapsis=peak,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == B1_LANDED),
    )


def _b1_down_eligible(state: B1State) -> bool:
    """True iff a vessel loss right now qualifies as the DOWN success terminal:
    DESCENT phase, chute deployed, AND the craft was last seen at/below
    downMaxAltMeters (option A: flew + chute deployed + REACHED THE GROUND).
    A post-chute loss at altitude fails all the way to ASSERT-FAIL (Fable
    review of PR #1335, SF-1: without the altitude leg, a chute-ripped mid-air
    breakup at 1800m was awarded DOWN)."""
    return (state.phase == B1_DESCENT
            and state.chute_deployed
            and state.last_finite_altitude is not None
            and _is_finite(state.last_finite_altitude)
            and state.last_finite_altitude <= state.params.down_max_alt)


def _b1_loss_reason_with_altitude(state: B1State, base: str) -> str:
    """Append the last known altitude to a loss reason so a DOWN-ineligible
    post-chute loss names WHERE the craft was lost."""
    if state.last_finite_altitude is None or not _is_finite(state.last_finite_altitude):
        return base
    return "%s; last altitude %.0fm" % (base, state.last_finite_altitude)


def _b1_enter_down(state: B1State, ut: float, peak: Optional[float]) -> B1State:
    """DOWN success terminal (operator decision 2026-07-20, option A): the craft
    reached the ground under a deployed chute and broke apart / became unreadable
    at touchdown. done=True, appended to phases_reached, NO loss_reason, verdict
    stays None so the assertions decide OK vs ASSERT-FAIL. skip_settle_tail marks
    the vessel as gone so the shell's settle tail (which would only gather
    vessel_lost / garbage frames) is skipped."""
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=B1_DOWN,
        phase_entry_ut=entry,
        peak_apoapsis=peak,
        phases_reached=state.phases_reached + (B1_DOWN,),
        done=True,
        skip_settle_tail=True,
    )


def _b1_stay_or_flake(state: B1State, snapshot: TelemetrySnapshot, peak: Optional[float]) -> B1State:
    """Stay in the current phase, or flip to MISSION-FLAKE if it out-ran budget."""
    if _b1_over_budget(state, snapshot):
        return replace(state, peak_apoapsis=peak, verdict=MISSION_FLAKE,
                       flake_phase=state.phase, done=True)
    return replace(state, peak_apoapsis=peak)


# ---------------------------------------------------------------------------
# B2 phase state machine (design "Mission B2: LKO-ascent"). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class B2State:
    """B2 LKO-ascent machine state (MechJeb AscentAutopilot). ``verdict`` /
    ``flake_phase`` / ``done`` mirror B1: MISSION-FLAKE on a budget overrun, done
    at ORBIT (flew to completion) or on a flake."""
    params: B2Params
    phase: str = B2_PRELAUNCH
    phase_entry_ut: float = 0.0
    phases_reached: Tuple[str, ...] = (B2_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    done: bool = False
    # Frozen-telemetry (vessel-destroyed) detection carried across frames (mirrors
    # B1State); a non-None loss_reason resolves to MISSION-ASSERT-FAIL.
    frozen_sig: Optional[FrozenSignature] = None
    frozen_count: int = 0
    loss_reason: Optional[str] = None


def b2_initial_state(params: B2Params) -> B2State:
    """Fresh B2 machine at PRELAUNCH (design)."""
    return B2State(params=params)


def _b2_phase_budget(params: B2Params, phase: str) -> Optional[float]:
    if phase == B2_MJ_ASCENT:
        return params.ascent_timeout
    if phase == B2_CIRCULARIZE:
        return params.circularize_timeout
    return None


def _b2_over_budget(state: B2State, snapshot: TelemetrySnapshot) -> bool:
    budget = _b2_phase_budget(state.params, state.phase)
    if budget is None:
        return False
    if not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > budget


def b2_decide(state: B2State, snapshot: TelemetrySnapshot) -> Tuple[B2State, List[Action]]:
    """Advance the B2 LKO-ascent machine one frame; return (new_state, actions).

    Transitions (design "Mission B2"):
      - PRELAUNCH -> MJ-ASCENT: on the FIRST decision, set MechJeb's target
        apoapsis, enable autostage, and engage the AscentAutopilot.
      - MJ-ASCENT -> CIRCULARIZE: when apoapsis has climbed to within
        apoErrorMeters of the target (the deterministic ascent-complete signal),
        ask MechJeb to execute the circularization node. Bounded by
        ascentTimeoutSeconds.
      - CIRCULARIZE -> ORBIT: when the node has raised periapsis to within
        periErrorMeters of the target (circular). Bounded by
        circularizeTimeoutSeconds.
    A phase that out-runs its budget yields MISSION-FLAKE naming the stuck phase (a
    MechJeb stall, catalog 5.5). ``done`` at ORBIT.
    """
    if state.done:
        return state, []

    # Runner-signaled vessel loss: phase-independent terminal (mirrors B1).
    if snapshot.vessel_lost:
        return replace(
            state, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason="vessel-lost (unreadable after repeated telemetry failures)"), []

    # Frozen-telemetry (vessel-destroyed) detection, AIRBORNE phases only (never
    # PRELAUNCH, never after done); mirrors B1.
    if state.phase in (B2_MJ_ASCENT, B2_CIRCULARIZE):
        limit = state.params.frozen_sample_limit
        new_sig, new_count, tripped = _advance_frozen_count(
            state.frozen_sig, state.frozen_count, snapshot, limit)
        if tripped:
            return replace(
                state, frozen_sig=new_sig, frozen_count=new_count, done=True,
                verdict=MISSION_ASSERT_FAIL,
                loss_reason=("vessel-lost (telemetry frozen %d consecutive samples "
                             "while airborne; vessel presumed destroyed)" % limit)), []
        state = replace(state, frozen_sig=new_sig, frozen_count=new_count)

    if state.phase == B2_PRELAUNCH:
        actions = [
            Action(ACTION_MJ_SET_TARGET_APOAPSIS, state.params.target_apoapsis),
            Action(ACTION_MJ_ENABLE_AUTOSTAGE),
            Action(ACTION_MJ_ENGAGE_ASCENT),
            # LAUNCH: MechJeb's AscentAutopilot engaged via kRPC does NOT
            # ignite the first stage itself (first live B2 run 2026-07-20 sat
            # in PRE_LAUNCH for the full ascent budget with the autopilot
            # engaged); the mission activates the initial stage exactly like a
            # GUI user pressing space, and MechJeb + autostage fly from there.
            Action(ACTION_ACTIVATE_STAGE),
        ]
        return _b2_enter(state, B2_MJ_ASCENT, snapshot.ut), actions

    if state.phase == B2_MJ_ASCENT:
        # Leave MJ-ASCENT only when the AscentAutopilot LATCHES complete
        # (engaged earlier, now self-disabled). The old apoapsis-window
        # condition fired MID-BURN (first live B2 run 2026-07-20: apoapsis
        # crossed the window at 36 km while MechJeb was still flying) and the
        # circularization action then executed an EMPTY node list, which the
        # server answers with an RPCError. The apoapsis check remains as an
        # AND-guard so a spurious early latch cannot advance a mission that
        # never got near its target.
        target = state.params.target_apoapsis
        apo_reached = (_is_finite(snapshot.apoapsis)
                       and snapshot.apoapsis >= target - state.params.apo_error)
        if snapshot.mj_ascent_complete and apo_reached:
            return (_b2_enter(state, B2_CIRCULARIZE, snapshot.ut),
                    [Action(ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        return _b2_stay_or_flake(state, snapshot), []

    if state.phase == B2_CIRCULARIZE:
        target = state.params.target_periapsis
        if _is_finite(snapshot.periapsis) and snapshot.periapsis >= target - state.params.peri_error:
            return _b2_enter(state, B2_ORBIT, snapshot.ut), []
        return _b2_stay_or_flake(state, snapshot), []

    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase, done=True), []


def _b2_enter(state: B2State, new_phase: str, ut: float) -> B2State:
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == B2_ORBIT),
    )


def _b2_stay_or_flake(state: B2State, snapshot: TelemetrySnapshot) -> B2State:
    if _b2_over_budget(state, snapshot):
        return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase, done=True)
    return state


# ---------------------------------------------------------------------------
# B4 phase state machine (mission b4_reentry). Pure. The ascent half reuses the
# B2 semantics VERBATIM (PRELAUNCH staged launch, the ascent-complete latch AND
# apoapsis window, the guarded circularize); ORBIT is a waypoint, not a terminal.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class B4State:
    """B4 reentry+splashdown machine state. ``verdict`` / ``flake_phase`` / ``done``
    mirror B1/B2. ``done`` fires only in SPLASHDOWN on a landed/splashed situation
    (verdict None; the settle tail RUNS -- evidence for the assertions) or on a
    flake / loss terminal. B4 REQUIRES survival: any vessel-lost / frozen terminal
    in ANY phase is an ASSERT-FAIL ``loss_reason`` (no B1-style DOWN equivalent).
    ``peak_apoapsis`` / ``chute_deployed`` are carried evidence for the evaluator;
    ``burn_started`` latches the one deorbit throttle-up after the attitude
    settle."""
    params: B4Params
    phase: str = B4_PRELAUNCH
    phase_entry_ut: float = 0.0
    peak_apoapsis: Optional[float] = None
    chute_deployed: bool = False
    burn_started: bool = False
    phases_reached: Tuple[str, ...] = (B4_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    done: bool = False
    frozen_sig: Optional[FrozenSignature] = None
    frozen_count: int = 0
    loss_reason: Optional[str] = None


def b4_initial_state(params: B4Params) -> B4State:
    """Fresh B4 machine at PRELAUNCH."""
    return B4State(params=params)


def _b4_phase_budget(params: B4Params, phase: str) -> Optional[float]:
    """The bounded game-time budget for a timed B4 phase, or None for the untimed
    PRELAUNCH / one-frame ORBIT waypoint. SPLASHDOWN's budget is the chute-descent
    wait (descentTimeoutSeconds); its clock stops mattering once ``done``."""
    if phase == B4_MJ_ASCENT:
        return params.ascent_timeout
    if phase == B4_CIRCULARIZE:
        return params.circularize_timeout
    if phase == B4_DEORBIT:
        return params.deorbit_timeout
    if phase == B4_REENTRY:
        return params.reentry_timeout
    if phase == B4_SPLASHDOWN:
        return params.descent_timeout
    return None


def _b4_over_budget(state: B4State, snapshot: TelemetrySnapshot) -> bool:
    budget = _b4_phase_budget(state.params, state.phase)
    if budget is None:
        return False
    if not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > budget


def b4_decide(state: B4State, snapshot: TelemetrySnapshot) -> Tuple[B4State, List[Action]]:
    """Advance the B4 reentry+splashdown machine one frame; return (new_state, actions).

    Transitions:
      - PRELAUNCH -> MJ-ASCENT: VERBATIM the B2 launch (set MechJeb target
        apoapsis, enable autostage, engage the AscentAutopilot, then
        ACTIVATE_STAGE -- MechJeb does not ignite the first stage itself).
      - MJ-ASCENT -> CIRCULARIZE: VERBATIM B2 -- the autopilot's
        engaged-then-self-disabled completion latch (mj_ascent_complete) AND the
        apoapsis window, then the (shell-guarded) circularization action. Bounded
        by ascentTimeoutSeconds.
      - CIRCULARIZE -> ORBIT: VERBATIM B2 -- periapsis within periErrorMeters of
        target. Bounded by circularizeTimeoutSeconds. ORBIT is NOT terminal here.
      - ORBIT -> DEORBIT: on the next frame, point the NATIVE kRPC AutoPilot
        retrograde (ACTION_AP_POINT_RETROGRADE) and enter DEORBIT.
      - DEORBIT: wait retroSettleSeconds of GAME time (a pure wait-in-phase
        condition, never a sleep) for the attitude to settle, throttle up once,
        and burn until periapsis <= deorbitPeriapsisMeters; then cut throttle,
        release attitude control (ACTION_AP_DISENGAGE), stage ONCE (the dropped
        service stage becomes debris Parsek records), and enter REENTRY. Bounded
        by deorbitTimeoutSeconds.
      - REENTRY: coast to the atmosphere in bounded RAILS-warp HOPS: while
        altitude > warpAboveAltMeters AND descending (vertical_speed < 0), emit
        one ACTION_WARP_TO with value = snapshot.ut + warpHopSeconds per decision
        frame -- bounded hops keep the machine in control and avoid computing the
        atmosphere-entry UT. Below the threshold: plain polling; at/below
        chuteDeployAltMeters deploy the chutes and enter SPLASHDOWN (the chute
        descent wait). Bounded by reentryTimeoutSeconds (game time; the hops
        advance it fast). NOTE: a still-ASCENDING exo coast (the burn ended
        before apoapsis) polls at 1x until vertical_speed goes negative, per the
        warp condition -- the wall budget must absorb that stretch.
      - SPLASHDOWN: situation in landedSituations -> terminal (done, verdict
        None; the settle tail RUNS so the assertions have settled evidence).
        Bounded by descentTimeoutSeconds.
    Vessel-lost / frozen telemetry in ANY phase -> ASSERT-FAIL loss_reason (B4's
    contract REQUIRES survival; there is no DOWN success terminal). A timed phase
    out-running its budget yields MISSION-FLAKE naming the stuck phase. Once
    ``done`` the machine is idempotent.
    """
    if state.done:
        return state, []

    peak = _update_peak(state.peak_apoapsis, snapshot.apoapsis)

    # Runner-signaled vessel loss: phase-independent ASSERT-FAIL terminal. B4 has
    # NO chute-deployed DOWN carve-out -- survival is the contract.
    if snapshot.vessel_lost:
        return replace(
            state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason="vessel-lost (unreadable after repeated telemetry failures)"), []

    # Frozen-telemetry (vessel-destroyed) detection, every phase except PRELAUNCH
    # (pad telemetry is legitimately static). Mirrors B1/B2.
    if state.phase != B4_PRELAUNCH:
        limit = state.params.frozen_sample_limit
        new_sig, new_count, tripped = _advance_frozen_count(
            state.frozen_sig, state.frozen_count, snapshot, limit)
        if tripped:
            return replace(
                state, peak_apoapsis=peak, frozen_sig=new_sig, frozen_count=new_count,
                done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("vessel-lost (telemetry frozen %d consecutive samples "
                             "while airborne; vessel presumed destroyed)" % limit)), []
        state = replace(state, frozen_sig=new_sig, frozen_count=new_count)

    if state.phase == B4_PRELAUNCH:
        actions = [
            Action(ACTION_MJ_SET_TARGET_APOAPSIS, state.params.target_apoapsis),
            Action(ACTION_MJ_ENABLE_AUTOSTAGE),
            Action(ACTION_MJ_ENGAGE_ASCENT),
            # LAUNCH: same as B2 -- MechJeb's engaged AscentAutopilot does not
            # ignite the first stage (first live B2 run 2026-07-20).
            Action(ACTION_ACTIVATE_STAGE),
        ]
        return _b4_enter(state, B4_MJ_ASCENT, snapshot.ut, peak), actions

    if state.phase == B4_MJ_ASCENT:
        # VERBATIM the B2 gate: completion latch AND apoapsis window (the window
        # alone fired mid-burn on the first live B2 run).
        target = state.params.target_apoapsis
        apo_reached = (_is_finite(snapshot.apoapsis)
                       and snapshot.apoapsis >= target - state.params.apo_error)
        if snapshot.mj_ascent_complete and apo_reached:
            return (_b4_enter(state, B4_CIRCULARIZE, snapshot.ut, peak),
                    [Action(ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        return _b4_stay_or_flake(state, snapshot, peak), []

    if state.phase == B4_CIRCULARIZE:
        target = state.params.target_periapsis
        if _is_finite(snapshot.periapsis) and snapshot.periapsis >= target - state.params.peri_error:
            return _b4_enter(state, B4_ORBIT, snapshot.ut, peak), []
        return _b4_stay_or_flake(state, snapshot, peak), []

    if state.phase == B4_ORBIT:
        # ORBIT is a one-frame waypoint (phase evidence for the reachedOrbit
        # assertion): immediately point retrograde and enter DEORBIT.
        return (_b4_enter(state, B4_DEORBIT, snapshot.ut, peak),
                [Action(ACTION_AP_POINT_RETROGRADE)])

    if state.phase == B4_DEORBIT:
        if _is_finite(snapshot.periapsis) and snapshot.periapsis <= state.params.deorbit_periapsis:
            # Burn done: cut throttle, release the autopilot, stage once (the
            # service stage becomes recorded debris), coast into REENTRY.
            return (_b4_enter(state, B4_REENTRY, snapshot.ut, peak),
                    [Action(ACTION_CUT_THROTTLE, 0.0),
                     Action(ACTION_AP_DISENGAGE),
                     Action(ACTION_ACTIVATE_STAGE)])
        if not state.burn_started:
            settled = (_is_finite(snapshot.ut)
                       and (snapshot.ut - state.phase_entry_ut) >= state.params.retro_settle_seconds)
            # Attitude AND-gate: throttle up only once the AutoPilot reports the
            # ship actually POINTING retrograde. A time-only wait burned mid-flip
            # on the first live B4 flight (radial burn, apoapsis 84km -> 382km).
            # A NaN error (AP unreadable) never passes, so a wedged autopilot
            # ends as the bounded deorbit-budget flake, never a wild burn.
            aligned = (_is_finite(snapshot.ap_error)
                       and snapshot.ap_error <= state.params.max_attitude_error_deg)
            stayed = _b4_stay_or_flake(state, snapshot, peak)
            if settled and aligned and not stayed.done:
                return replace(stayed, burn_started=True), [Action(ACTION_SET_THROTTLE, 1.0)]
            return stayed, []
        return _b4_stay_or_flake(state, snapshot, peak), []

    if state.phase == B4_REENTRY:
        alt_finite = _is_finite(snapshot.altitude)
        if alt_finite and snapshot.altitude <= state.params.chute_deploy_alt:
            entered = _b4_enter(state, B4_SPLASHDOWN, snapshot.ut, peak)
            return replace(entered, chute_deployed=True), [Action(ACTION_DEPLOY_CHUTE)]
        stayed = _b4_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return stayed, []
        actions: List[Action] = []
        if (alt_finite and snapshot.altitude > state.params.warp_above_alt
                and _is_finite(snapshot.vertical_speed) and snapshot.vertical_speed < 0.0
                and _is_finite(snapshot.ut)):
            # One bounded hop per decision frame; never computes atmosphere-entry UT.
            actions.append(Action(ACTION_WARP_TO, snapshot.ut + state.params.warp_hop_seconds))
        return stayed, actions

    if state.phase == B4_SPLASHDOWN:
        if snapshot.situation in state.params.landed_situations:
            # Terminal: done with verdict None -- the settle tail RUNS and the
            # assertions decide OK vs ASSERT-FAIL.
            return replace(state, peak_apoapsis=peak, done=True), []
        return _b4_stay_or_flake(state, snapshot, peak), []

    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase, done=True,
                   peak_apoapsis=peak), []


def _b4_enter(state: B4State, new_phase: str, ut: float, peak: Optional[float]) -> B4State:
    """Transition into ``new_phase``, stamping the phase-entry UT for the budget
    clock and appending to ``phases_reached``. No phase entry sets ``done`` --
    B4's only success terminal is the landed/splashed situation INSIDE
    SPLASHDOWN."""
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        peak_apoapsis=peak,
        phases_reached=state.phases_reached + (new_phase,),
    )


def _b4_stay_or_flake(state: B4State, snapshot: TelemetrySnapshot, peak: Optional[float]) -> B4State:
    if _b4_over_budget(state, snapshot):
        return replace(state, peak_apoapsis=peak, verdict=MISSION_FLAKE,
                       flake_phase=state.phase, done=True)
    return replace(state, peak_apoapsis=peak)


# ---------------------------------------------------------------------------
# B5 phase state machine (mission b5_mun_flyby). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class B5State:
    """B5 Mun-flyby machine state. ``verdict`` / ``flake_phase`` / ``done``
    mirror B4. ``done`` fires in RETURN (back in home SOI after the flyby;
    verdict None, the settle tail RUNS) or on a flake / loss terminal. Survival
    is the contract (no DOWN-style carve-out). ``min_target_altitude`` is the
    running min finite altitude while inside the target SOI (the flyby-floor
    evidence); ``last_plan_ut`` stamps the most recent PLAN-* action so a failed
    plan re-issues on the bounded ``plan_retry_seconds`` cadence."""
    params: B5Params
    phase: str = B5_PRELAUNCH
    phase_entry_ut: float = 0.0
    peak_apoapsis: Optional[float] = None
    min_target_altitude: Optional[float] = None
    last_plan_ut: float = 0.0
    # Node count at the moment a plan handed off to the executor. The BURN-phase
    # exit is "the executor CONSUMED the first node" (node_count dropped below
    # this), NOT "the node list is empty": MechJeb's OperationTransfer plans a
    # capture/arrival burn as a SECOND node when its capture options are on, and
    # waiting for zero then parks the machine through the whole autowarped
    # transfer coast until the burn budget flakes (first live B5 flight,
    # 2026-07-21 - both attempts). Stray leftover nodes are cleared at the exit.
    planned_node_count: int = 0
    # Correction rounds completed (planned+burned, fell through, or timed out).
    # COAST-TO-TARGET enters PLAN-CORRECTION once per params.correction_trigger_alts
    # entry when the altitude crosses that round's trigger.
    correction_rounds_done: int = 0
    # Burn-stagnation watchdog (fifth live flight 2026-07-22: the executor
    # BURNED the correction node then held it forever -- burn visibly done,
    # node never consumed, no warp, phase budget flaked). ``burn_entry_ap`` /
    # ``burn_entry_pe`` snapshot the orbit at BURN-phase entry (has-a-burn-
    # happened evidence); ``burn_prev_ap`` / ``burn_prev_pe`` the previous
    # frame's orbit; ``burn_static_since`` the UT the orbit went static at 1x
    # (warp NONE) -- static through RAILS autowarp toward the node is the
    # LEGITIMATE wait and never counts. Once the orbit has changed since entry
    # AND been static at 1x for burnStagnantSeconds, the burn is treated as
    # effectively complete: abort+clear the stale node and move on.
    burn_entry_ap: Optional[float] = None
    burn_entry_pe: Optional[float] = None
    burn_prev_ap: Optional[float] = None
    burn_prev_pe: Optional[float] = None
    burn_static_since: Optional[float] = None
    phases_reached: Tuple[str, ...] = (B5_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    done: bool = False
    frozen_sig: Optional[FrozenSignature] = None
    frozen_count: int = 0
    loss_reason: Optional[str] = None


def b5_initial_state(params: B5Params) -> B5State:
    """Fresh B5 machine at PRELAUNCH."""
    return B5State(params=params)


def _b5_phase_budget(params: B5Params, phase: str) -> Optional[float]:
    """The bounded game-time budget for a timed B5 phase, or None for the
    untimed PRELAUNCH / one-frame ORBIT waypoint / terminal RETURN."""
    if phase == B5_MJ_ASCENT:
        return params.ascent_timeout
    if phase == B5_CIRCULARIZE:
        return params.circularize_timeout
    if phase in (B5_PLAN_TRANSFER, B5_PLAN_CORRECTION):
        return params.plan_timeout
    if phase in (B5_TRANSFER_BURN, B5_CORRECTION_BURN):
        return params.transfer_burn_timeout
    if phase == B5_COAST_TO_TARGET:
        return params.coast_timeout
    if phase == B5_TARGET_FLYBY:
        return params.flyby_timeout
    return None


def _b5_over_budget(state: B5State, snapshot: TelemetrySnapshot) -> bool:
    budget = _b5_phase_budget(state.params, state.phase)
    if budget is None:
        return False
    if not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > budget


def _b5_enter(state: B5State, new_phase: str, ut: float,
              peak: Optional[float]) -> B5State:
    """Transition into ``new_phase``, stamping the phase-entry UT and appending
    to ``phases_reached``. RETURN is the only phase whose ENTRY terminates the
    machine (done, verdict None -- the assertions decide)."""
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        peak_apoapsis=peak,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == B5_RETURN),
    )


def _b5_stay_or_flake(state: B5State, snapshot: TelemetrySnapshot,
                      peak: Optional[float]) -> B5State:
    if _b5_over_budget(state, snapshot):
        return replace(state, peak_apoapsis=peak, verdict=MISSION_FLAKE,
                       flake_phase=state.phase, done=True)
    return replace(state, peak_apoapsis=peak)


# Burn-stagnation watchdog thresholds: "the burn happened" = an apsis moved
# more than _BURN_CHANGED_EPS since BURN-phase entry; "static" = frame-to-frame
# apsis movement under _BURN_STATIC_EPS (a coasting conic is rock-stable; any
# thrust moves the apsides at km/s-class rates).
_BURN_CHANGED_EPS = 10_000.0
_BURN_STATIC_EPS = 50.0


def _b5_track_burn_stagnation(
        state: B5State,
        snapshot: TelemetrySnapshot) -> Tuple[B5State, bool, bool]:
    """Advance the BURN-phase stagnation watchdog one frame; return
    (new_state, stuck_after_burn, stuck_no_start).

    ``stuck_after_burn``: the orbit CHANGED since burn entry (a burn
    demonstrably happened) and has now sat static at 1x (warp NONE) for
    burn_stagnant_seconds -- the executor is wedged holding a completed node
    (fifth live flight 2026-07-22).
    ``stuck_no_start``: the orbit is UNCHANGED since entry and has sat static
    at 1x for burn_nostart_seconds -- the executor never began (sixth live
    flight: execute issued, no warp, no burn, wall budget died). The longer
    bound leaves room for the legitimate pre-burn attitude flip (~340 s).
    RAILS autowarp toward the node (static orbit, warp != NONE) never counts
    toward either."""
    ap, pe, ut = snapshot.apoapsis, snapshot.periapsis, snapshot.ut
    if not (_is_finite(ap) and _is_finite(pe) and _is_finite(ut)):
        return replace(state, burn_prev_ap=None, burn_prev_pe=None,
                       burn_static_since=None), False, False
    static = (state.burn_prev_ap is not None and state.burn_prev_pe is not None
              and abs(ap - state.burn_prev_ap) < _BURN_STATIC_EPS
              and abs(pe - state.burn_prev_pe) < _BURN_STATIC_EPS
              and snapshot.warp_mode == WARP_NONE)
    since = state.burn_static_since
    if static:
        if since is None:
            since = ut
    else:
        since = None
    burned = (state.burn_entry_ap is not None and state.burn_entry_pe is not None
              and (abs(ap - state.burn_entry_ap) > _BURN_CHANGED_EPS
                   or abs(pe - state.burn_entry_pe) > _BURN_CHANGED_EPS))
    static_span = (ut - since) if since is not None else 0.0
    stuck_after_burn = burned and since is not None \
        and static_span >= state.params.burn_stagnant_seconds
    stuck_no_start = (not burned) and since is not None \
        and static_span >= state.params.burn_nostart_seconds
    return replace(state, burn_prev_ap=ap, burn_prev_pe=pe,
                   burn_static_since=since), stuck_after_burn, stuck_no_start


def _b5_plan_phase(state: B5State, snapshot: TelemetrySnapshot, peak: Optional[float],
                   plan_action: Action, burn_phase: str,
                   on_timeout_phase: Optional[str]) -> Tuple[B5State, List[Action]]:
    """Shared PLAN-TRANSFER / PLAN-CORRECTION logic: once a maneuver node exists,
    hand it to the autowarping NodeExecutor and enter ``burn_phase``; while no
    node exists, re-issue ``plan_action`` on the bounded ``plan_retry_seconds``
    cadence (a no-encounter / transient planner failure throws server-side and
    leaves node_count at 0 -- the re-plan is safe because it fires ONLY while
    node_count == 0, so a successful plan can never stack a second node).
    ``on_timeout_phase``: PLAN-CORRECTION falls through to the coast on budget
    expiry (the correction is a best-effort refinement, not a mission
    requirement); PLAN-TRANSFER passes None and flakes (no node = no mission)."""
    if snapshot.node_count >= 1:
        entered = _b5_enter(state, burn_phase, snapshot.ut, peak)
        entered = replace(
            entered, planned_node_count=snapshot.node_count,
            # Arm the burn-stagnation watchdog: snapshot the entry orbit and
            # clear the frame-to-frame tracking.
            burn_entry_ap=(snapshot.apoapsis if _is_finite(snapshot.apoapsis) else None),
            burn_entry_pe=(snapshot.periapsis if _is_finite(snapshot.periapsis) else None),
            burn_prev_ap=None, burn_prev_pe=None, burn_static_since=None)
        return entered, [Action(ACTION_MJ_EXECUTE_NODES)]
    if _b5_over_budget(state, snapshot) and on_timeout_phase is not None:
        return _b5_enter(state, on_timeout_phase, snapshot.ut, peak), []
    stayed = _b5_stay_or_flake(state, snapshot, peak)
    if stayed.done:
        return stayed, []
    if (_is_finite(snapshot.ut)
            and (snapshot.ut - state.last_plan_ut) >= state.params.plan_retry_seconds):
        return replace(stayed, last_plan_ut=snapshot.ut), [plan_action]
    return stayed, []


def b5_decide(state: B5State, snapshot: TelemetrySnapshot) -> Tuple[B5State, List[Action]]:
    """Advance the B5 Mun-flyby machine one frame; return (new_state, actions).

    Transitions:
      - PRELAUNCH -> MJ-ASCENT -> CIRCULARIZE -> ORBIT: VERBATIM the B4/B2
        MechJeb ascent (engage + launch, completion latch AND apoapsis window,
        guarded circularize, periapsis gate).
      - ORBIT -> PLAN-TRANSFER: one-frame waypoint; set the target body and ask
        the MechJeb ManeuverPlanner for a Hohmann transfer to it.
      - PLAN-TRANSFER -> TRANSFER-BURN: a maneuver node exists -> hand it to the
        autowarping NodeExecutor. While no node: bounded re-plan every
        planRetrySeconds; budget expiry flakes (no transfer = no mission).
      - TRANSFER-BURN -> PLAN-CORRECTION (courseCorrectPeriapsisMeters > 0) or
        COAST-TO-TARGET: the node list is empty again (the executor consumed
        it) AND apoapsis >= transferMinApoapsisMeters (evidence the TLI burn
        actually raised the orbit; an executor that aborts without burning
        waits out the budget -> flake).
      - PLAN-CORRECTION -> CORRECTION-BURN: same node logic as PLAN-TRANSFER,
        but budget expiry FALLS THROUGH to COAST-TO-TARGET (the correction is a
        best-effort geometry refinement -- MechJeb may transiently see no
        encounter to correct; the flyby-floor assertion still guards the
        outcome).
      - CORRECTION-BURN -> COAST-TO-TARGET: node list empty again (no apoapsis
        gate: a correction is a small vector tweak).
      - COAST-TO-TARGET: bounded rails-warp hops (one WARP_TO = now +
        coastWarpHopSeconds per decision frame) while the SOI body is still the
        home body. body == target -> TARGET-FLYBY. body neither home nor target
        (nor "", the fail-closed unknown) -> ASSERT-FAIL (ejected: the craft
        left the home system without meeting the target).
      - TARGET-FLYBY: track the min finite altitude (the flyby-floor evidence);
        smaller bounded hops (flybyWarpHopSeconds) while inside the target SOI.
        body == home -> RETURN (terminal: done, verdict None, the settle tail
        runs on-rails in home SOI). body neither -> ASSERT-FAIL (slung out of
        the home system).
    Vessel-lost / frozen telemetry in ANY phase -> ASSERT-FAIL loss_reason
    (survival is the contract). A timed phase out-running its budget yields
    MISSION-FLAKE naming the stuck phase (except the PLAN-CORRECTION
    fall-through above). Once ``done`` the machine is idempotent.
    """
    if state.done:
        return state, []

    peak = _update_peak(state.peak_apoapsis, snapshot.apoapsis)

    if snapshot.vessel_lost:
        return replace(
            state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason="vessel-lost (unreadable after repeated telemetry failures)"), []

    if state.phase != B5_PRELAUNCH:
        limit = state.params.frozen_sample_limit
        new_sig, new_count, tripped = _advance_frozen_count(
            state.frozen_sig, state.frozen_count, snapshot, limit)
        if tripped:
            return replace(
                state, peak_apoapsis=peak, frozen_sig=new_sig, frozen_count=new_count,
                done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("vessel-lost (telemetry frozen %d consecutive samples "
                             "while airborne; vessel presumed destroyed)" % limit)), []
        state = replace(state, frozen_sig=new_sig, frozen_count=new_count)

    # Flyby-floor evidence: min finite altitude while inside the target SOI.
    if (state.phase == B5_TARGET_FLYBY and snapshot.body == state.params.target_body
            and _is_finite(snapshot.altitude)):
        prev = state.min_target_altitude
        if prev is None or snapshot.altitude < prev:
            state = replace(state, min_target_altitude=float(snapshot.altitude))

    if state.phase == B5_PRELAUNCH:
        actions = [
            Action(ACTION_MJ_SET_TARGET_APOAPSIS, state.params.target_apoapsis),
            Action(ACTION_MJ_ENABLE_AUTOSTAGE),
            Action(ACTION_MJ_ENGAGE_ASCENT),
            Action(ACTION_ACTIVATE_STAGE),
        ]
        return _b5_enter(state, B5_MJ_ASCENT, snapshot.ut, peak), actions

    if state.phase == B5_MJ_ASCENT:
        target = state.params.target_apoapsis
        apo_reached = (_is_finite(snapshot.apoapsis)
                       and snapshot.apoapsis >= target - state.params.apo_error)
        if snapshot.mj_ascent_complete and apo_reached:
            return (_b5_enter(state, B5_CIRCULARIZE, snapshot.ut, peak),
                    [Action(ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        return _b5_stay_or_flake(state, snapshot, peak), []

    if state.phase == B5_CIRCULARIZE:
        target = state.params.target_periapsis
        if _is_finite(snapshot.periapsis) and snapshot.periapsis >= target - state.params.peri_error:
            return _b5_enter(state, B5_ORBIT, snapshot.ut, peak), []
        return _b5_stay_or_flake(state, snapshot, peak), []

    if state.phase == B5_ORBIT:
        # One-frame waypoint (reachedOrbit evidence): set the transfer target and
        # ask the ManeuverPlanner for the Hohmann transfer, then wait for the node.
        entered = _b5_enter(state, B5_PLAN_TRANSFER, snapshot.ut, peak)
        entered = replace(entered, last_plan_ut=snapshot.ut if _is_finite(snapshot.ut) else 0.0)
        return entered, [
            Action(ACTION_SET_TARGET_BODY, text=state.params.target_body),
            Action(ACTION_MJ_PLAN_TRANSFER),
        ]

    if state.phase == B5_PLAN_TRANSFER:
        return _b5_plan_phase(
            state, snapshot, peak,
            plan_action=Action(ACTION_MJ_PLAN_TRANSFER),
            burn_phase=B5_TRANSFER_BURN,
            on_timeout_phase=None)

    if state.phase == B5_TRANSFER_BURN:
        # Exit = the executor CONSUMED the first (TLI) node -- node_count fell
        # below the count the plan handed off -- AND the apoapsis floor proves a
        # real burn. NOT node_count == 0: OperationTransfer may plan a
        # capture/arrival burn as a second node, and waiting for zero parks the
        # machine through the whole autowarped coast until the budget flakes
        # (first live B5 flight 2026-07-21). Stray leftover nodes (that unwanted
        # capture burn) are aborted+cleared at the exit so the executor never
        # flies them and the coast hops are not suppressed by node_count > 0.
        # TRANSFER-BURN uses only the after-burn wedge signal: a no-start TLI
        # has produced no transfer, and the phase budget owns that outcome
        # (six live flights: the TLI executor always started).
        state, stuck, _nostart = _b5_track_burn_stagnation(state, snapshot)
        consumed = snapshot.node_count < max(state.planned_node_count, 1)
        floor_met = (_is_finite(snapshot.apoapsis)
                     and snapshot.apoapsis >= state.params.transfer_min_apoapsis)
        if (consumed or stuck) and floor_met:
            cleanup = ([Action(ACTION_MJ_ABORT_AND_CLEAR_NODES)]
                       if snapshot.node_count > 0 else [])
            # Always into the coast: the correction rounds are COAST-triggered
            # (per correction_trigger_alts; trigger 0 fires on the first coast
            # frame, reproducing the old immediate post-TLI correction).
            return _b5_enter(state, B5_COAST_TO_TARGET, snapshot.ut, peak), cleanup
        if stuck:
            # A burn happened, the executor wedged, and the apoapsis floor is
            # NOT met: the TLI under-burned -- no transfer exists to coast on.
            # An autopilot failure, so a bounded flake (retry per policy).
            return replace(state, peak_apoapsis=peak, verdict=MISSION_FLAKE,
                           flake_phase=state.phase, done=True), []
        return _b5_stay_or_flake(state, snapshot, peak), []

    if state.phase == B5_PLAN_CORRECTION:
        new_state, actions = _b5_plan_phase(
            state, snapshot, peak,
            plan_action=Action(ACTION_MJ_PLAN_COURSE_CORRECT,
                               state.params.course_correct_periapsis,
                               limit=state.params.max_correction_dv),
            burn_phase=B5_CORRECTION_BURN,
            on_timeout_phase=B5_COAST_TO_TARGET)
        if new_state.phase == B5_COAST_TO_TARGET:
            # Timeout fall-through consumes this round (a disqualified/failed
            # plan never blocks the coast; the NEXT round may still refine).
            new_state = replace(new_state,
                                correction_rounds_done=state.correction_rounds_done + 1)
        return new_state, actions

    if state.phase == B5_CORRECTION_BURN:
        # Same consumed-not-empty exit as TRANSFER-BURN (no apoapsis gate: a
        # correction is a small vector tweak); strays cleared on the way out.
        # A stagnation-watchdog trip also exits: after-burn = the executor is
        # wedged holding the completed node (fifth live flight; the
        # imperfect-but-burned correction stands, the next round refines);
        # no-start = the executor never began (sixth live flight; the round is
        # skipped, the assertion floor still guards the outcome).
        state, stuck, nostart = _b5_track_burn_stagnation(state, snapshot)
        if snapshot.node_count < max(state.planned_node_count, 1) or stuck or nostart:
            cleanup = ([Action(ACTION_MJ_ABORT_AND_CLEAR_NODES)]
                       if snapshot.node_count > 0 else [])
            entered = _b5_enter(state, B5_COAST_TO_TARGET, snapshot.ut, peak)
            entered = replace(entered,
                              correction_rounds_done=state.correction_rounds_done + 1)
            return entered, cleanup
        return _b5_stay_or_flake(state, snapshot, peak), []

    if state.phase == B5_COAST_TO_TARGET:
        if snapshot.body == state.params.target_body:
            return _b5_enter(state, B5_TARGET_FLYBY, snapshot.ut, peak), []
        if snapshot.body not in ("", state.params.home_body):
            # A REAL foreign body name (e.g. "Sun") is the ejected terminal; ""
            # (no reading this frame) is NOT -- it stays in phase with no hop,
            # and a sustained unreadable vessel dies at the vessel-lost terminal.
            return replace(
                state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("left home SOI without reaching the target: body=%r "
                             "(expected %r or %r)"
                             % (snapshot.body, state.params.home_body,
                                state.params.target_body))), []
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return stayed, []
        # Correction rounds: one PLAN-CORRECTION entry per trigger altitude.
        # Round 2+ is LIVE-PROVEN necessary (flight 4: the post-TLI correction
        # flew, but executor residual over the long coast drifted the flyby
        # periapsis from +60 km to -29 km = impact; a mid-coast refinement
        # prices the residual at a few m/s).
        triggers = state.params.correction_trigger_alts
        if (state.params.course_correct_periapsis > 0.0
                and state.correction_rounds_done < len(triggers)
                and snapshot.body == state.params.home_body
                and _is_finite(snapshot.altitude)
                and snapshot.altitude >= triggers[state.correction_rounds_done]):
            entered = _b5_enter(state, B5_PLAN_CORRECTION, snapshot.ut, peak)
            entered = replace(entered,
                              last_plan_ut=snapshot.ut if _is_finite(snapshot.ut) else 0.0)
            return entered, [Action(ACTION_MJ_PLAN_COURSE_CORRECT,
                                    state.params.course_correct_periapsis,
                                    limit=state.params.max_correction_dv)]
        actions: List[Action] = []
        if (snapshot.body == state.params.home_body
                and _is_finite(snapshot.ut) and snapshot.node_count == 0):
            actions.append(Action(ACTION_WARP_TO,
                                  snapshot.ut + state.params.coast_warp_hop_seconds))
        return stayed, actions

    if state.phase == B5_TARGET_FLYBY:
        if snapshot.body == state.params.home_body:
            # The free-return: back in home SOI after the flyby. Terminal (done,
            # verdict None); the settle tail runs on-rails in home SOI.
            return _b5_enter(state, B5_RETURN, snapshot.ut, peak), []
        if snapshot.body not in ("", state.params.target_body):
            return replace(
                state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("flyby ejected the craft from the home system: body=%r "
                             "(expected %r or %r)"
                             % (snapshot.body, state.params.target_body,
                                state.params.home_body))), []
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return stayed, []
        # NEVER warp toward a known impact: on a sub-surface periapsis at low
        # altitude the crash happens INSIDE a blocking warp_to, KSP pops the
        # Flight Results dialog which PAUSES the game clock, and the mission
        # process wedges in that RPC until the mission budget reaps it (flight
        # 4, 2026-07-21: pe -28.7 km, ~17 wasted minutes). Polling at 1x lets
        # the vessel-lost / frozen detectors end the mission cleanly in seconds
        # after the impact instead.
        impact_bound = (_is_finite(snapshot.periapsis) and snapshot.periapsis < 0.0
                        and _is_finite(snapshot.altitude)
                        and snapshot.altitude < IMPACT_WARP_GUARD_ALT)
        actions = []
        if (snapshot.body == state.params.target_body and _is_finite(snapshot.ut)
                and not impact_bound):
            actions.append(Action(ACTION_WARP_TO,
                                  snapshot.ut + state.params.flyby_warp_hop_seconds))
        return stayed, actions

    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase, done=True,
                   peak_apoapsis=peak), []


# ---------------------------------------------------------------------------
# Telemetry-assertion evaluators (design "Telemetry assertions" + guardrails).
# Pure over a list of TelemetrySnapshot frames.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class AssertionOutcome:
    """One telemetry assertion's result (design mission-result "assertions").
    ``value`` is the evidence reading (a float, a situation string, or None when
    no finite reading exists); ``detail`` carries the window / tolerance / accepted
    set for the serialized row. ``to_dict`` scrubs a non-finite value to JSON
    ``null`` so the result JSON is always valid + deterministic."""
    name: str
    met: bool
    value: object
    detail: Dict = field(default_factory=dict)

    def to_dict(self) -> Dict:
        v = self.value
        if isinstance(v, float) and not math.isfinite(v):
            v = None
        row: Dict = {"name": self.name, "met": bool(self.met), "value": v}
        row.update(self.detail)
        return row


def _debounced_window_met(frames, getter, lo: float, hi: float, k: int) -> bool:
    """K-consecutive debounce that a scalar reading sits INCLUSIVELY within
    [lo, hi]. A frame is in-tolerance iff its value is finite AND lo <= v <= hi;
    NaN/Inf is out (never a passing compare, design edge 11)."""
    flags = [(_is_finite(getter(f)) and lo <= getter(f) <= hi) for f in frames]
    return _has_k_consecutive_true(flags, k)


def _debounced_max_met(frames, getter, bound: float, k: int) -> bool:
    """K-consecutive debounce that a scalar reading is INCLUSIVELY <= ``bound``
    (used for eccentricity <= max)."""
    flags = [(_is_finite(getter(f)) and getter(f) <= bound) for f in frames]
    return _has_k_consecutive_true(flags, k)


def _last_finite(frames, getter) -> Optional[float]:
    """The last finite reading of a scalar across the frames (the settled orbit
    reading used as the assertion's evidence value), or None if none is finite."""
    val: Optional[float] = None
    for f in frames:
        v = getter(f)
        if _is_finite(v):
            val = float(v)
    return val


def _peak_finite(frames, getter) -> Optional[float]:
    """The maximum finite reading of a scalar across the frames (the B1 apoapsis
    peak), or None if none is finite."""
    peak: Optional[float] = None
    for f in frames:
        v = getter(f)
        if _is_finite(v) and (peak is None or v > peak):
            peak = float(v)
    return peak


def evaluate_b1_assertions(frames, params: B1Params,
                           k: int = DEFAULT_DEBOUNCE_K,
                           down_terminal: bool = False) -> List[AssertionOutcome]:
    """Evaluate the two B1 driver-validity assertions over the flight frames.

    - ``apoapsisWindow``: the PEAK apoapsis must sit within apoapsisWindowMeters
      (a WINDOW, not a golden apoapsis). The gate is the NaN-filtered running MAX
      over the whole flight -- a hop that climbs THROUGH the window but peaks above
      it (e.g. passes through 6-30 km then apogees at 45 km) is UNMET, because the
      peak (45 km) lies outside the window; the transient in-window frames on the
      way up do NOT satisfy it. NaN/Inf apoapsis frames are filtered out of the max
      (they never inflate or pass it); a flight with no finite apoapsis reading is
      UNMET (peak None). The reported value is that peak (evidence).
    - ``landedSituation``: the FINAL situation must be one of landedSituations,
      OR the machine ended in the DOWN terminal (``down_terminal=True``, operator
      decision 2026-07-20 option A: a chute-deployed impact IS the craft reaching
      the ground; the destroyed craft's final frames carry no landed situation to
      read). The DOWN end is named in the outcome's value
      ("DOWN(chute-deployed impact)" vs the raw situation string) and flagged in
      the detail (``downTerminal``) so the result JSON says which end it was.
      (A situation is a discrete kRPC enum, not a noisy float, so it is read from
      the last frame directly rather than debounced.)

    NOTE: the ``k`` parameter is retained for signature symmetry with
    ``evaluate_b2_assertions`` but is unused here -- a peak is a single settled
    quantity (the max), not a noisy per-frame reading that needs K-consecutive
    debounce; B2's orbit params ARE per-frame terminal-state windows and keep the
    debounce.
    """
    frames = list(frames or [])
    lo, hi = params.apoapsis_window
    peak = _peak_finite(frames, lambda f: f.apoapsis)
    apo_met = peak is not None and lo <= peak <= hi
    apo = AssertionOutcome("apoapsisWindow", apo_met,
                           peak if peak is not None else float("nan"),
                           {"window": [lo, hi]})

    if down_terminal:
        sit = AssertionOutcome("landedSituation", True, "DOWN(chute-deployed impact)",
                               {"accepted": list(params.landed_situations),
                                "downTerminal": True})
    else:
        final_situation = frames[-1].situation if frames else None
        sit_met = final_situation in params.landed_situations
        sit = AssertionOutcome("landedSituation", sit_met, final_situation,
                               {"accepted": list(params.landed_situations),
                                "downTerminal": False})
    return [apo, sit]


def evaluate_b2_assertions(frames, params: B2Params,
                           k: int = DEFAULT_DEBOUNCE_K) -> List[AssertionOutcome]:
    """Evaluate the four B2 driver-validity assertions over the orbit frames, all
    WITHIN TOLERANCE (never golden, design "Mission B2"). Each requires K
    consecutive in-tolerance frames (debounce over MechJeb warp-stutter, catalog
    5.5); the reported value is the last settled (finite) reading.

    - ``apoapsisError``:    |apoapsis - target|   <= apoErrorMeters
    - ``periapsisError``:   |periapsis - target|  <= periErrorMeters
    - ``eccentricity``:     eccentricity          <= eccentricityMax
    - ``inclinationError``: |inclination - launchSiteLatitude| <= inclinationErrorDeg
    """
    frames = list(frames or [])

    ap_lo = params.target_apoapsis - params.apo_error
    ap_hi = params.target_apoapsis + params.apo_error
    apo_met = _debounced_window_met(frames, lambda f: f.apoapsis, ap_lo, ap_hi, k)
    apo = AssertionOutcome(
        "apoapsisError", apo_met,
        _value_or_nan(_last_finite(frames, lambda f: f.apoapsis)),
        {"target": params.target_apoapsis, "tolerance": params.apo_error})

    pe_lo = params.target_periapsis - params.peri_error
    pe_hi = params.target_periapsis + params.peri_error
    peri_met = _debounced_window_met(frames, lambda f: f.periapsis, pe_lo, pe_hi, k)
    peri = AssertionOutcome(
        "periapsisError", peri_met,
        _value_or_nan(_last_finite(frames, lambda f: f.periapsis)),
        {"target": params.target_periapsis, "tolerance": params.peri_error})

    ecc_met = _debounced_max_met(frames, lambda f: f.eccentricity, params.eccentricity_max, k)
    ecc = AssertionOutcome(
        "eccentricity", ecc_met,
        _value_or_nan(_last_finite(frames, lambda f: f.eccentricity)),
        {"max": params.eccentricity_max})

    inc_lo = params.launch_site_latitude - params.inclination_error
    inc_hi = params.launch_site_latitude + params.inclination_error
    inc_met = _debounced_window_met(frames, lambda f: f.inclination, inc_lo, inc_hi, k)
    inc = AssertionOutcome(
        "inclinationError", inc_met,
        _value_or_nan(_last_finite(frames, lambda f: f.inclination)),
        {"target": params.launch_site_latitude, "tolerance": params.inclination_error})

    return [apo, peri, ecc, inc]


def evaluate_b4_assertions(frames, params: B4Params,
                           phases_reached=(), chute_deployed: bool = False,
                           k: int = DEFAULT_DEBOUNCE_K) -> List[AssertionOutcome]:
    """Evaluate the four B4 driver-validity assertions: terminal-focused and
    derivable from the frames + the machine's phase evidence, NEVER
    orbital-precision post-deorbit (the orbit is a waypoint, and the deorbit burn
    deliberately wrecks it).

    - ``reachedOrbit``:    ORBIT appears in ``phases_reached`` (phase evidence);
      the reported value is the deepest phase reached.
    - ``apoapsisFloor``:   the PEAK finite apoapsis over the whole flight is >=
      targetApoapsisMeters - apoErrorMeters (the ascent actually got there; a
      floor, not a window -- the deorbit tail never lowers the recorded peak).
    - ``landedSituation``: the FINAL situation is one of landedSituations (the
      splashdown/landing that B4's survival contract requires).
    - ``chuteDeployed``:   the machine deployed the chutes (carried evidence).

    ``k`` is retained for signature symmetry with the B1/B2 evaluators but unused:
    every B4 assertion is a settled terminal / peak quantity, not a noisy
    per-frame window needing K-consecutive debounce.
    """
    frames = list(frames or [])
    phases = tuple(phases_reached or ())

    orbit_met = B4_ORBIT in phases
    orbit = AssertionOutcome("reachedOrbit", orbit_met,
                             (phases[-1] if phases else None),
                             {"required": B4_ORBIT})

    floor = params.target_apoapsis - params.apo_error
    peak = _peak_finite(frames, lambda f: f.apoapsis)
    apo_met = peak is not None and peak >= floor
    apo = AssertionOutcome("apoapsisFloor", apo_met,
                           peak if peak is not None else float("nan"),
                           {"floor": floor})

    final_situation = frames[-1].situation if frames else None
    sit_met = final_situation in params.landed_situations
    sit = AssertionOutcome("landedSituation", sit_met, final_situation,
                           {"accepted": list(params.landed_situations)})

    chute = AssertionOutcome("chuteDeployed", bool(chute_deployed),
                             bool(chute_deployed), {})

    return [orbit, apo, sit, chute]


def evaluate_b5_assertions(frames, params: B5Params,
                           phases_reached=(),
                           min_target_altitude: Optional[float] = None,
                           k: int = DEFAULT_DEBOUNCE_K) -> List[AssertionOutcome]:
    """Evaluate the four B5 driver-validity assertions: terminal-focused phase +
    flyby evidence, NEVER a golden trajectory (the transfer geometry is
    MechJeb's business; ours is that the flyby actually happened and came back).

    - ``reachedOrbit``:        ORBIT appears in ``phases_reached``.
    - ``reachedTargetSoi``:    TARGET-FLYBY appears in ``phases_reached`` (the
      SOI body actually became the target -- cross-SOI evidence).
    - ``flybyPeriapsisFloor``: the min finite altitude recorded inside the
      target SOI is at/above targetPeriapsisFloorMeters (the flyby cleared the
      terrain; a crashed flyby dies at the vessel-lost terminal first, so this
      guards the SAMPLED closest approach). Evidence is machine-carried
      (min_target_altitude), coarse under warp hops -- a floor, not a window.
    - ``returnedToHome``:      RETURN appears in ``phases_reached`` (the machine
      terminated back in home SOI after the flyby -- the free-return).

    ``k`` is retained for signature symmetry but unused: every B5 assertion is
    phase / min evidence, not a noisy per-frame window."""
    del frames  # phase + machine evidence carry everything; kept for seam symmetry
    phases = tuple(phases_reached or ())

    orbit_met = B5_ORBIT in phases
    orbit = AssertionOutcome("reachedOrbit", orbit_met,
                             (phases[-1] if phases else None),
                             {"required": B5_ORBIT})

    soi_met = B5_TARGET_FLYBY in phases
    soi = AssertionOutcome("reachedTargetSoi", soi_met,
                           (params.target_body if soi_met else None),
                           {"required": B5_TARGET_FLYBY, "target": params.target_body})

    floor = params.target_periapsis_floor
    floor_met = min_target_altitude is not None and min_target_altitude >= floor
    flyby = AssertionOutcome("flybyPeriapsisFloor", floor_met,
                             (min_target_altitude if min_target_altitude is not None
                              else float("nan")),
                             {"floor": floor})

    ret_met = B5_RETURN in phases
    ret = AssertionOutcome("returnedToHome", ret_met,
                           (params.home_body if ret_met else None),
                           {"required": B5_RETURN, "home": params.home_body})

    return [orbit, soi, flyby, ret]


def _value_or_nan(v: Optional[float]) -> float:
    return v if v is not None else float("nan")


def all_assertions_met(outcomes) -> bool:
    """True iff every assertion is met (and there is at least one). An empty list
    is False -- a mission with no assertions never certifies a flight OK."""
    outcomes = list(outcomes or [])
    return bool(outcomes) and all(o.met for o in outcomes)


def resolve_flight_verdict(machine_state, outcomes) -> Tuple[str, str]:
    """Map a terminated phase-machine state + assertion outcomes to a mission
    verdict + reason (design "Mission B1/B2": all met -> OK; any unmet ->
    ASSERT-FAIL; a phase timeout -> FLAKE). Returns (verdict, reason)."""
    if getattr(machine_state, "verdict", None) == MISSION_FLAKE:
        return MISSION_FLAKE, "phase %s timed out" % (machine_state.flake_phase,)
    # A vessel-lost / destroyed terminal is a deterministic mission failure (not a
    # flake): return its reason verbatim BEFORE evaluating assertions, since a
    # destroyed craft's residual telemetry could otherwise spuriously satisfy them.
    loss_reason = getattr(machine_state, "loss_reason", None)
    if loss_reason:
        return MISSION_ASSERT_FAIL, loss_reason
    if all_assertions_met(outcomes):
        return MISSION_OK, "all telemetry assertions met"
    unmet = [o.name for o in outcomes if not o.met]
    return MISSION_ASSERT_FAIL, "assertions unmet: %s" % (", ".join(unmet) or "none",)


# ---------------------------------------------------------------------------
# Connect-retry decision (design "Connection lifecycle" step 2). Pure.
# ---------------------------------------------------------------------------


def decide_connect_retry(elapsed: float, attempts: int, budget: float,
                         max_attempts: int) -> str:
    """Decide RETRY vs TIMEOUT for a bounded connect loop (design edge 1).

    ``attempts`` is the number of connect attempts already made; ``elapsed`` the
    seconds since the connect loop began. RETRY only while BOTH bounds still have
    room -- fewer than ``max_attempts`` attempts AND ``elapsed`` still under
    ``budget``; TIMEOUT past EITHER bound (or on a non-finite elapsed, defensively)
    so an unreachable server can never retry forever (design "A mission never
    hangs"). The boundary is inclusive: at exactly ``max_attempts`` or exactly
    ``budget`` the loop is done -> TIMEOUT.
    """
    if attempts >= max_attempts:
        return CONNECT_TIMEOUT
    if not _is_finite(elapsed):
        return CONNECT_TIMEOUT
    if elapsed >= budget:
        return CONNECT_TIMEOUT
    return CONNECT_RETRY


# ---------------------------------------------------------------------------
# Physics-warp guard (design edge 7). Pure. The mission NEVER requests physics
# warp; an unexpected warp state around powered flight is a determinism violation
# that the shell turns into a MISSION-FLAKE naming the phase + warp state. B1 flies
# 1x THROUGHOUT (a 6-30 km hop never leaves the atmosphere, where rails warp is
# forbidden); B2 permits RAILS warp only on its exo-atmospheric coast.
# ---------------------------------------------------------------------------

WARP_NONE = "NONE"
WARP_RAILS = "RAILS"
WARP_PHYSICS = "PHYSICS"

# TimeWarp.CurrentRate ramps CONTINUOUSLY toward the selected step, so a
# permitted 2x physics warp is routinely sampled at 2.0x-and-a-bit while
# settling; the guard adds this allowance on top of max_physics_warp.
PHYSICS_WARP_RAMP_ALLOWANCE = 0.25


def is_unexpected_warp(warp_mode: str, warp_rate: float, allow_rails: bool,
                       max_physics_warp: float = 0.0) -> bool:
    """True iff the reported warp state is UNEXPECTED for a v1 mission (design
    edge 7). 1x (a non-finite or ``<= 1.0`` rate) is always fine. Above 1x:
    PHYSICS warp is permitted only up to ``max_physics_warp`` (default 0.0 =
    never; the mission spec sets the bound - B2 uses 4.0, the stock physics
    ceiling, because MechJeb's AscentAutopilot engages its own physics warp
    during ascent escalating to 4x and KRPC.MechJeb 0.8.1 exposes no toggle
    for it - observed live 2026-07-20; the comparison carries a small ramp
    allowance since TimeWarp.CurrentRate is continuous while ramping toward
    the step rate). Above that bound it stays a determinism violation. RAILS warp is permitted ONLY when ``allow_rails``
    (B2's exo-atmospheric coast, per its RAILS-or-1x contract), and forbidden
    otherwise (B1's 1x-throughout contract). An unknown warp mode above 1x is
    treated conservatively as unexpected. On True the shell flakes the mission
    naming the phase + the warp state."""
    if not _is_finite(warp_rate) or warp_rate <= 1.0:
        return False
    mode = str(warp_mode or "").upper()
    if mode == WARP_PHYSICS:
        if not _is_finite(max_physics_warp) or max_physics_warp <= 0.0:
            return True
        return warp_rate > max_physics_warp + PHYSICS_WARP_RAMP_ALLOWANCE
    if mode == WARP_RAILS:
        return not allow_rails
    # Above 1x with an unrecognized / NONE mode is an inconsistent, unexpected state.
    return True


# ---------------------------------------------------------------------------
# Post-connect exception origin classification (design edges 5 / 8 vs 9). Pure.
# A connection drop or a kRPC RPC error AFTER a successful connect is a
# MISSION-FLAKE (autopilot-flake bucket, retryable on a fresh boot); only a
# pre-connect / setup / internal (non-kRPC) exception stays MISSION-ERROR. mlib
# never imports krpc, so the shell passes the caught exception's type module +
# name and mlib decides by ORIGIN.
# ---------------------------------------------------------------------------

# The kRPC client's exceptions live under the ``krpc`` package (krpc.error.*:
# RPCError / ConnectionError / StreamError, etc.).
_KRPC_EXCEPTION_MODULE = "krpc"

# stdlib socket / connection exception names that signal a dropped connection
# post-connect (a torn socket, a reset, a broken pipe, a read timeout), even when
# the raise did not come from the krpc package itself.
CONNECTION_DROP_EXCEPTION_NAMES = frozenset({
    "ConnectionError", "ConnectionResetError", "ConnectionAbortedError",
    "ConnectionRefusedError", "BrokenPipeError", "TimeoutError", "socket.timeout",
    "RPCError", "StreamError",
})


def classify_post_connect_exception(exc_module: Optional[str], exc_name: Optional[str]) -> str:
    """Classify an exception raised AFTER a successful connect (design edge 5 / 8
    vs 9). Returns ``MISSION_FLAKE`` when the exception originates in the kRPC
    package OR is a stdlib connection-drop exception (a mid-flight socket reset, a
    dropped stream, a vessel-invalid RPC error) -- a transient the mission-validity
    gate keeps out of the Parsek-defect bucket, retryable on a fresh boot. Returns
    ``MISSION_ERROR`` for any other (internal, non-kRPC) exception (edge 9: a None
    dereference, a bad param, a genuine mission-script bug). Pure by ORIGIN so mlib
    never imports krpc."""
    mod = str(exc_module or "")
    name = str(exc_name or "")
    if mod == _KRPC_EXCEPTION_MODULE or mod.startswith(_KRPC_EXCEPTION_MODULE + "."):
        return MISSION_FLAKE
    if name in CONNECTION_DROP_EXCEPTION_NAMES:
        return MISSION_FLAKE
    return MISSION_ERROR


# ---------------------------------------------------------------------------
# Mission-result build / serialize / parse / validate (design Data Model
# "Mission result"). Deterministic + byte-identical for identical inputs.
# ---------------------------------------------------------------------------


def build_mission_result(
    mission: str,
    verdict: str,
    reason: str,
    phases_reached,
    connect_attempts: int,
    connected_seconds: float,
    rpc_port: int,
    assertions,
    wall_seconds,
    krpc_client_version: str,
    krpc_server_version: str,
    error: Optional[str] = None,
) -> Dict:
    """Assemble the mission-result dict in the design's schema (line ~331).

    ``assertions`` may be ``AssertionOutcome`` objects (converted via ``to_dict``)
    or already-shaped dicts. ``connected_seconds`` non-finite (never connected) is
    scrubbed to None so the JSON stays valid. The dict is what
    ``serialize_mission_result`` renders; the shell writes it to ``--result``.
    """
    conn_s = connected_seconds
    if isinstance(conn_s, float) and not math.isfinite(conn_s):
        conn_s = None
    rows = []
    for a in (assertions or []):
        rows.append(a.to_dict() if isinstance(a, AssertionOutcome) else dict(a))
    return {
        "schema": MISSION_RESULT_SCHEMA,
        "mission": mission,
        "verdict": verdict,
        "reason": reason,
        "phasesReached": list(phases_reached or []),
        "connect": {
            "attempts": int(connect_attempts),
            "connectedSeconds": conn_s,
            "rpcPort": int(rpc_port),
        },
        "assertions": rows,
        "wallSeconds": wall_seconds,
        "krpcClientVersion": krpc_client_version,
        "krpcServerVersion": krpc_server_version,
        "error": error,
    }


def serialize_mission_result(result: Dict) -> str:
    """Serialize a mission-result dict deterministically (design Data Model).

    Stable key order (``sort_keys``), floats via Python's ``repr`` through json,
    ASCII, explicit ``\\n`` line endings, and ``allow_nan=False`` so a stray NaN/Inf
    raises rather than emitting the invalid JSON token ``NaN`` (build/assertion
    codepaths scrub non-finite values to null first). Byte-identical output for
    identical inputs, so per-attempt result files diff cleanly and run.py parses
    them without guessing.
    """
    text = json.dumps(result, sort_keys=True, indent=2, ensure_ascii=True, allow_nan=False)
    return text.replace("\r\n", "\n") + "\n"


def parse_mission_result(text: str) -> Dict:
    """Parse a serialized mission-result back to a dict (round-trip partner of
    ``serialize_mission_result``)."""
    return json.loads(text)


def validate_mission_result(obj: Dict) -> Tuple[bool, Tuple[str, ...]]:
    """Validate a parsed mission-result against the design schema. Returns
    (ok, errors); every failing field is reported (not just the first) so a
    malformed result names its whole problem set. Guards the fields run.py reads
    (verdict / connect / assertions) so a dropped or mistyped field is caught,
    not silently mis-read."""
    errors: List[str] = []
    if not isinstance(obj, dict):
        return False, ("result is not a JSON object",)

    if obj.get("schema") != MISSION_RESULT_SCHEMA:
        errors.append("schema: expected %d got %r" % (MISSION_RESULT_SCHEMA, obj.get("schema")))

    for key in ("mission", "reason", "krpcClientVersion", "krpcServerVersion"):
        if not isinstance(obj.get(key), str):
            errors.append("%s: expected string, got %r" % (key, obj.get(key)))

    verdict = obj.get("verdict")
    if verdict not in MISSION_VERDICTS:
        errors.append("verdict: %r not in %s" % (verdict, list(MISSION_VERDICTS)))

    phases = obj.get("phasesReached")
    if not isinstance(phases, list) or not all(isinstance(p, str) for p in phases):
        errors.append("phasesReached: expected list[str], got %r" % (phases,))

    connect = obj.get("connect")
    if not isinstance(connect, dict):
        errors.append("connect: expected object, got %r" % (connect,))
    else:
        if not isinstance(connect.get("attempts"), int):
            errors.append("connect.attempts: expected int, got %r" % (connect.get("attempts"),))
        if not isinstance(connect.get("rpcPort"), int):
            errors.append("connect.rpcPort: expected int, got %r" % (connect.get("rpcPort"),))
        cs = connect.get("connectedSeconds")
        if cs is not None and not isinstance(cs, (int, float)):
            errors.append("connect.connectedSeconds: expected number or null, got %r" % (cs,))

    assertions = obj.get("assertions")
    if not isinstance(assertions, list):
        errors.append("assertions: expected list, got %r" % (assertions,))
    else:
        for i, row in enumerate(assertions):
            if not isinstance(row, dict):
                errors.append("assertions[%d]: expected object, got %r" % (i, row))
                continue
            if not isinstance(row.get("name"), str):
                errors.append("assertions[%d].name: expected string" % (i,))
            if not isinstance(row.get("met"), bool):
                errors.append("assertions[%d].met: expected bool" % (i,))
            if "value" not in row:
                errors.append("assertions[%d].value: missing" % (i,))

    if "wallSeconds" not in obj:
        errors.append("wallSeconds: missing")
    err = obj.get("error")
    if err is not None and not isinstance(err, str):
        errors.append("error: expected string or null, got %r" % (err,))

    return (len(errors) == 0), tuple(errors)


# ---------------------------------------------------------------------------
# Diagnostic logging format (pure; the shell writes these to stdout, design
# Diagnostic Logging). Mirrors ParsekLog / [Harness] / [Provision].
# ---------------------------------------------------------------------------


def format_mission_log_line(level: str, phase: str, message: str) -> str:
    """Format one mission-log line: ``[Mission][LEVEL][Phase] message`` (design
    Diagnostic Logging). ``level`` / ``phase`` pass through so a caller typo is
    visible, not swallowed."""
    return "[Mission][%s][%s] %s" % (level, phase, message)
