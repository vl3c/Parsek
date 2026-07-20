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

# B1 phase names (design "Mission B1: pad-hop").
B1_PRELAUNCH = "PRELAUNCH"
B1_ASCENT = "ASCENT"
B1_COAST = "COAST"
B1_DESCENT = "DESCENT"
B1_LANDED = "LANDED"
B1_PHASES: Tuple[str, ...] = (B1_PRELAUNCH, B1_ASCENT, B1_COAST, B1_DESCENT, B1_LANDED)

# B2 phase names (design "Mission B2: LKO-ascent").
B2_PRELAUNCH = "PRELAUNCH"
B2_MJ_ASCENT = "MJ-ASCENT"
B2_CIRCULARIZE = "CIRCULARIZE"
B2_ORBIT = "ORBIT"
B2_PHASES: Tuple[str, ...] = (B2_PRELAUNCH, B2_MJ_ASCENT, B2_CIRCULARIZE, B2_ORBIT)

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
    argument (throttle fraction, target apoapsis) or None for a no-arg action."""
    kind: str
    value: Optional[float] = None


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
    Any timed phase that out-runs its budget yields MISSION-FLAKE naming the stuck
    phase (``state.verdict`` / ``state.flake_phase``), so a wedged autopilot never
    hangs. Once ``done`` the machine is idempotent (returns the state unchanged,
    no actions).
    """
    if state.done:
        return state, []

    peak = _update_peak(state.peak_apoapsis, snapshot.apoapsis)

    # Runner-signaled vessel loss (unreadable active vessel after repeated telemetry
    # failures): a phase-INDEPENDENT terminal (design vessel-destroyed terminal). A
    # destroyed vessel is a deterministic mission failure, not a flake.
    if snapshot.vessel_lost:
        return replace(
            state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason="vessel-lost (unreadable after repeated telemetry failures)"), []

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
            return replace(
                state, peak_apoapsis=peak, frozen_sig=new_sig, frozen_count=new_count,
                done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("vessel-lost (telemetry frozen %d consecutive samples "
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
                           k: int = DEFAULT_DEBOUNCE_K) -> List[AssertionOutcome]:
    """Evaluate the two B1 driver-validity assertions over the flight frames.

    - ``apoapsisWindow``: the PEAK apoapsis must sit within apoapsisWindowMeters
      (a WINDOW, not a golden apoapsis). The gate is the NaN-filtered running MAX
      over the whole flight -- a hop that climbs THROUGH the window but peaks above
      it (e.g. passes through 6-30 km then apogees at 45 km) is UNMET, because the
      peak (45 km) lies outside the window; the transient in-window frames on the
      way up do NOT satisfy it. NaN/Inf apoapsis frames are filtered out of the max
      (they never inflate or pass it); a flight with no finite apoapsis reading is
      UNMET (peak None). The reported value is that peak (evidence).
    - ``landedSituation``: the FINAL situation must be one of landedSituations.
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

    final_situation = frames[-1].situation if frames else None
    sit_met = final_situation in params.landed_situations
    sit = AssertionOutcome("landedSituation", sit_met, final_situation,
                           {"accepted": list(params.landed_situations)})
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
