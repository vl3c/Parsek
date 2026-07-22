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
# B7 interplanetary transfer plan (MechJeb OperationInterplanetaryTransfer with
# WaitForPhaseAngle). Same PLAN_* try/except contract as ACTION_MJ_PLAN_TRANSFER.
ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER = "mj_plan_interplanetary_transfer"  # value None
ACTION_MJ_PLAN_COURSE_CORRECT = "mj_plan_course_correct"   # value = periapsis m
ACTION_MJ_EXECUTE_NODES = "mj_execute_nodes"               # value = None (autowarp)
ACTION_MJ_ABORT_AND_CLEAR_NODES = "mj_abort_and_clear_nodes"  # value = None
# B5 DIY correction burner (live finding 8): point kRPC's NATIVE AutoPilot
# along the first maneuver node's burn vector (node.reference_frame, direction
# (0, 1, 0) -- that frame's y-axis IS the burn vector, pinned-source verified).
# MechJeb's NodeExecutor is NOT used for corrections: its close-in-node path
# demands AlignedAndSettled (< 1 deg AND angular velocity < 0.001 rad/s,
# decompiled 2.15.1 StateWarpAlign) which the low-torque Kerbal X never meets,
# parking every close-in correction node forever.
ACTION_AP_POINT_NODE = "ap_point_node"                     # value = None
# Non-blocking rails-warp control (operator design critique 2026-07-22: the
# per-hop warp_to ramp-down/up sawtooth made warp oscillate mid-coast; warp
# should change only when an action is imminent). value = the KSP rails warp
# factor INDEX (0 = 1x .. 7 = 100,000x; the server clamps to the altitude-
# legal maximum). The machine emits this ON CHANGE only and keeps polling --
# no blocking RPC, so telemetry (frozen/ejection detectors) stays continuous
# and the Flight-Results-dialog wedge class (finding 4) is structurally gone
# from the B5 coast.
ACTION_SET_RAILS_WARP = "set_rails_warp"                   # value = factor index
# Physics (LOW mode) warp control for segments that need RUNNING physics --
# today the correction-burn attitude flip (a ~340 s 1x crawl on the low-torque
# Kerbal X). value = the KSP physics warp factor INDEX (0 = 1x .. 3 = 4x; the
# server clamps via kRPC PhysicsWarpFactor.Clamp(0, 3)). Precedent: MechJeb's
# own WarpToUT runs attitude-holding segments at physics warp capped 2.0x
# (decompiled 2.15.1 MechJebModuleWarpController.WarpToUT). Same on-change +
# self-healing emission discipline as set_rails_warp; the machine always drops
# to 0 BEFORE any throttle-up so a burn never integrates at scaled physics dt.
ACTION_SET_PHYSICS_WARP = "set_physics_warp"               # value = factor index
# Native fire-and-forget warp-to-UT (Path A, docs/dev/research/
# native-warp-to-ut.md): the runner's WarpService issues SpaceCenter.WarpTo
# on a DEDICATED second kRPC connection owned by a daemon thread, so the
# primary telemetry connection never blocks (per-connection RPC
# serialization, pinned kRPC Core.cs). The server's own stepper adapts the
# factor both ways against the game's live altitude limits - table-free
# native adaptation, the operator's design principle. value = target UT.
# CONTRACT: while a native warp is active (TelemetrySnapshot.warping_to
# finite) the machine MUST NOT emit set_rails_warp - two writers fight and
# WarpTo wins within 1-2 frames (research doc, scheduler analysis).
ACTION_WARP_TO_UT = "warp_to_ut"                           # value = target UT (s)
# Cancel the native warp: the runner closes the warp connection (the server
# discards the continuation next FixedUpdate) and zeroes both warp factors
# from the primary connection. Idempotent when no warp is active.
ACTION_CANCEL_WARP = "cancel_warp"                         # value = None

# TARGET-FLYBY impact-warp guard: below this altitude with a SUB-SURFACE
# periapsis the machine stops issuing warp hops and polls at 1x, so a crash
# happens under live telemetry (clean vessel-lost terminal in seconds) instead
# of inside a blocking warp_to wedged by the paused Flight Results dialog.
IMPACT_WARP_GUARD_ALT = 400_000.0

# TARGET-FLYBY impact-certain EARLY TERMINAL (twenty-second live flight
# 2026-07-22): once the impact-warp guard condition (sub-surface periapsis
# below the guard altitude) has held for this many CONSECUTIVE frames, the
# mission outcome is decided -- no correction capability exists inside the
# target SOI -- so the machine terminates ASSERT-FAIL immediately instead of
# riding the descent at 1x to physical destruction (589 wall-seconds on the
# certification flight; the audit's only 1x-coast violation). The debounce
# keeps a transient periapsis mis-read from ending a live mission.
IMPACT_TERMINAL_DEBOUNCE_FRAMES = 5

# Flameout staging (twenty-second live flight 2026-07-22): mid-correction the
# Kerbal X CORE stage ran dry (LiquidFuel froze at exactly 720.0 -- the full,
# unreachable X200-16 upper tank) and BOTH correction rounds no-progress-gave-
# up against a flamed-out engine; the under-corrected arrival was an impact
# trajectory. During a COMMANDED burn (throttle readback above the epsilon),
# ZERO available thrust for FLAMEOUT_DEBOUNCE_FRAMES consecutive frames means
# the active stage is dry -> pop ONE stage and keep burning, bounded at
# MAX_FLAMEOUT_STAGES per mission (a mis-read must not cascade the whole
# stack; the flyby floor assertion still judges the outcome).
FLAMEOUT_THROTTLE_EPS = 0.01
FLAMEOUT_DEBOUNCE_FRAMES = 2
MAX_FLAMEOUT_STAGES = 2

# Arrival-quality re-correction (twenty-third live flight 2026-07-22, finding
# 16): both altitude-triggered correction rounds executed to <1 m/s residual
# and the flyby STILL arrived at pe -31.8 km -- the blind altitude triggers
# cannot see arrival quality, and at 6,000 km leverage (~12.8 km of arrival-pe
# shift per m/s) small post-burn effects move the arrival tens of km. Once
# the altitude rounds are exhausted, a PREDICTED arrival periapsis (patched-
# conic next_orbit at the target body) below the flyby floor for
# ARRIVAL_BAD_DEBOUNCE_FRAMES consecutive frames (conic reads flap at 1000x
# rails) grants a bounded extra PLAN-CORRECTION round, only while more than
# ARRIVAL_RECORRECT_MIN_TTS_SECONDS remain to the SOI crossing (a plan + aim
# + burn cannot complete closer in; past that, the impact-certain terminal
# owns a bad arrival). NaN next_pe / wrong next_body never fire the gate.
ARRIVAL_BAD_DEBOUNCE_FRAMES = 3
MAX_ARRIVAL_EXTRA_ROUNDS = 2
ARRIVAL_RECORRECT_MIN_TTS_SECONDS = 600.0
# High-precision window UPPER bound (twenty-fourth flight): an extra round
# fired immediately on detection at tts ~12,700 s moved the prediction only
# -33.7 -> -29.3 km -- at that leverage (~12.8 km of arrival shift per m/s)
# the 2.0 m/s cut residual alone is +/-25 km and MechJeb's long-range plan
# quality adds more, so far-out extras CANNOT converge on the target.
# Precision per m/s improves linearly toward the encounter (~3.6 km per m/s
# at 3,600 s, cut residual +/-7 km), so the extras hold until the coast
# carries the craft inside this bound; the sub-floor prediction is stable
# across the coast (patched conics are deterministic on rails).
ARRIVAL_RECORRECT_MAX_TTS_SECONDS = 3600.0

# No-encounter early correction trigger (finding 18, B7 fourth flight
# 2026-07-22): the phase-angle interplanetary ejection reliably produces NO
# target encounter (design Q5's contrary assumption REFUTED live), so in
# TIME mode over a via body a debounced encounter-less trajectory fires the
# pending correction round early -- the course-correct plan CREATES the
# encounter mid-course. The debounce guards transient NaN tts reads at SOI
# transitions.
NO_ENCOUNTER_DEBOUNCE_FRAMES = 3

# No-start clock countability bound (finding 19b, B7 sixth flight): when the
# flip is commanded from a 100,000x coast, KSP flips TimeWarp.Mode to LOW
# (kRPC reports PHYSICS) IMMEDIATELY while CurrentRate is still DECAYING from
# 100,000 (observed 5.32 = mid-decay), so a mode-label re-anchor still let
# 600 game-s of decay tail consume the whole alignment budget in ~1 wall-s.
# Frames whose OBSERVED rate exceeds this bound are never alignment time,
# whatever the mode label says; genuine 1x-4x flip frames count, keeping the
# give-up bounded (stock physics warp maxes at 4x).
NOSTART_COUNTABLE_RATE_MAX = 4.5

# DIY-burner aligned-gate debounce: the throttle fires only after this many
# CONSECUTIVE in-gate attitude readings. The fourteenth live flight proved a
# single-frame transient error reading (slipping between rate-limited samples)
# opened the gate at a true ~98 deg off-axis and fired a ~200 m/s wild burn;
# one odd frame must never start a burn.
ALIGNED_DEBOUNCE_FRAMES = 2

# KSP rails warp rates by factor index (stock table).
RAILS_WARP_RATES = (1.0, 5.0, 10.0, 50.0, 100.0, 1000.0, 10000.0, 100000.0)

# Worst-case decision latency the stair-down must absorb: two ~0.5 s polls.
_WARP_SAFETY_SECONDS = 1.0

# Native warp-to-UT re-issue threshold (game s): a fresh target computed from
# a shifted SOI estimate re-issues the warp only when it moved more than this
# from the commanded target (kRPC WarpTo cannot retarget; the runner cancels
# and re-issues, so churn must be bounded).
WARP_RETARGET_THRESHOLD_SECONDS = 120.0

# Native warp self-healing bound (game s): if the game reports NO active warp
# (warping_to NaN) while the machine still expects one (target ahead, no
# cancel issued), re-issue at most once per this many game seconds.
WARP_REISSUE_SECONDS = 30.0

# PLAN-* attempt bound (live finding 14): a plan that keeps being produced
# and DISQUALIFIED runner-side (over-cap removal) is indistinguishable from a
# no-encounter failure to the machine (node_count stays 0), and the old
# cadence loop sat at 1x re-planning for the full 300 s planTimeoutSeconds
# (seventeenth-flight round 2: 169-171 m/s quotes vs the old 150 cap, five
# removals). After this many attempts with no node, the next cadence check
# takes the timeout path EARLY (PLAN-TRANSFER: flake; PLAN-CORRECTION: fall
# through + consume the round) -- worst case 1x drops from 300 s to ~90 s.
PLAN_MAX_ATTEMPTS = 3


def rails_factor_for_distance(dist_m: float, speed_mps: float, cap: int) -> int:
    """The highest rails factor index (<= cap) whose warped travel over the
    safety window still fits inside ``dist_m`` -- the operator-reported fix for
    the 1x crawl (sixteenth flight round: the old slow-down band dropped to 1x
    for its ENTIRE 2,000 km, ~40 real minutes at coast speeds; the stair-down
    holds 1000x far out and only reaches 1x in the last moments). A tiny or
    non-finite speed is floored at 10 m/s (conservative: slower closure allows
    MORE warp only when the distance genuinely shrinks slowly)."""
    if not _is_finite(dist_m) or dist_m <= 0.0:
        return 0
    speed = max(abs(speed_mps), 10.0) if _is_finite(speed_mps) else 10.0
    for idx in range(min(cap, len(RAILS_WARP_RATES) - 1), 0, -1):
        if RAILS_WARP_RATES[idx] * speed * _WARP_SAFETY_SECONDS <= dist_m:
            return idx
    return 0


def rails_factor_for_time(dt_s: float, cap: int) -> int:
    """The highest rails factor index (<= cap) whose warped GAME-time advance
    over the safety window still fits inside ``dt_s`` seconds -- the TIME
    sibling of ``rails_factor_for_distance`` (operator directive 2026-07-22:
    "warp to maneuver node" as a non-blocking time-based stair-down, never the
    blocking warp_to RPC). One safety window at factor idx advances
    RAILS_WARP_RATES[idx] * _WARP_SAFETY_SECONDS game seconds, so the stair
    holds 100,000x while days remain, 1000x inside ~3 hours, and 1x only in
    the last seconds. NaN / non-positive remaining time returns 0 (fail
    closed: an unknown wait is never warped over)."""
    if not _is_finite(dt_s) or dt_s <= 0.0:
        return 0
    for idx in range(min(cap, len(RAILS_WARP_RATES) - 1), 0, -1):
        if RAILS_WARP_RATES[idx] * _WARP_SAFETY_SECONDS <= dt_s:
            return idx
    return 0


# Stock per-body rails-warp minimum-altitude tables (metres ASL), index ==
# rails factor index. GROUND TRUTH extracted 2026-07-22 from the dev install's
# serialized CelestialBody.timeWarpAltitudeLimits arrays (KSP 1.12.5
# sharedassets9.assets PSystem prefab, all 17 bodies mapped by the adjacent
# bodyName string; consistent 1112-byte object stride). kRPC's RailsWarpFactor
# setter clamps a commanded factor via CanRailsWarpAt, which compares
# vessel.mainBody.GetAltitude(CoM) against EXACTLY these raw values (pinned
# kRPC 0.5.4 SpaceCenter.cs CanRailsWarpAt -> TimeWarp.GetAltitudeLimit ->
# body.timeWarpAltitudeLimits[i]; no atmosphere fold-in -- the in-atmosphere
# rails block is a separate stock gate our exo-only warp commands never hit).
# A commanded factor above the legal maximum silently produces RAILS at the
# CLAMPED lower rate, and KSP never auto-escalates as the vessel climbs, so
# the machine must choose factors from this table itself or a whole coast leg
# runs slow (live-observed: factor 6 commanded near the 80 km parking orbit
# ran at 50x). Values are data, not tolerances: do not tune.
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


def max_legal_rails_factor(body: str, altitude_m: float) -> int:
    """The highest rails factor index the stock altitude-limit table permits
    for ``body`` at ``altitude_m`` -- the client-side mirror of the kRPC
    RailsWarpFactor clamp, so the machine only ever COMMANDS achievable
    factors. Two payoffs: (1) commanded == achievable, so the on-change
    emission discipline ESCALATES the factor as the vessel climbs past each
    limit (KSP never auto-raises a clamped rate); (2) the self-healing
    re-emit never fights an unachievable command. Legality is altitude >=
    limit (kRPC CanRailsWarpAt rejects strictly-below). FAIL-OPEN to the top
    factor for an unknown body name or a non-finite altitude: the server
    clamp is the hard backstop, and a one-frame altitude blip must not
    sawtooth an otherwise-held warp down to 1x. NOTE: callers pass the
    machine's SURFACE altitude while the game compares sea-level altitude;
    surface <= ASL everywhere, so the mismatch only ever UNDER-commands (by
    terrain height, a few km) -- never an illegal command."""
    limits = STOCK_WARP_ALTITUDE_LIMITS.get(body)
    if limits is None or not _is_finite(altitude_m):
        return len(RAILS_WARP_RATES) - 1
    best = 0
    for idx in range(len(limits)):
        if altitude_m >= limits[idx]:
            best = idx
    return best

# classify_correction_plan verdicts (review SF-3: the runner's plan
# accept/remove decision extracted into a pure, threshold-testable decider).
PLAN_FLY = "fly"
PLAN_OVER_CAP = "over_cap"
PLAN_NEGLIGIBLE = "negligible"


def classify_correction_plan(total_dv: float, cap: float,
                             negligible_floor: float) -> str:
    """Classify a planned course-correction's total dv (m/s):

      - "over_cap":   the plan must be REMOVED -- it exceeds the dv cap (a
                      genuine correction is a small tweak; second live flight:
                      an oversized plan wedged the executor), OR the dv is
                      non-finite. NaN FAILS CLOSED to over_cap: a plan whose
                      cost cannot be quantified never flies -- removal is the
                      safe outcome because PLAN-CORRECTION's bounded
                      fall-through simply coasts on the raw intercept and the
                      NEXT trigger round may still refine.
      - "negligible": the plan must be REMOVED -- total dv below the floor is
                      smaller than the executor ever engages on (sixth live
                      flight) and within the flyby floor's margin.
      - "fly":        hand it to the burner.

    Boundaries are INCLUSIVE-fly on the cap (dv == cap flies; only strictly
    greater disqualifies) and EXCLUSIVE-fly on the floor (dv == floor flies;
    only strictly below is negligible), matching the live-proven runner
    comparisons this extracts. cap <= 0 (or non-finite) disables the cap."""
    if not _is_finite(total_dv):
        return PLAN_OVER_CAP
    if _is_finite(cap) and cap > 0.0 and total_dv > cap:
        return PLAN_OVER_CAP
    if total_dv < negligible_floor:
        return PLAN_NEGLIGIBLE
    return PLAN_FLY


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
    # Remaining delta-v (m/s) of the FIRST maneuver node (kRPC
    # Node.RemainingDeltaV); NaN when no node exists or the read failed. NaN
    # fails closed: the DIY correction burner's cut/overshoot gates never fire
    # on it, and its bounded give-up owns the outcome.
    node_dv: float = float("nan")
    # Vessel-total LiquidFuel + the live throttle READBACK (control.throttle).
    # Diagnosability channels (tenth live flight: a zero-thrust "burn" was
    # undiagnosable dry-tanks vs held-throttle vs wrong-pointing without them);
    # no machine gate reads them yet.
    liquid_fuel: float = float("nan")
    throttle: float = float("nan")
    # Vessel-total ElectricCharge (finding-19b diagnosability channel): a
    # solar-panel-less craft on a multi-day interplanetary coast can drain
    # its battery, and dead reaction wheels present EXACTLY like the frozen
    # apErr the B7 heliocentric flips showed. No machine gate reads it yet.
    electric_charge: float = float("nan")
    # kRPC Vessel.AvailableThrust (N): total thrust the ACTIVE engines can
    # produce right now -- 0.0 when the active stage is dry / flamed out /
    # engineless (twenty-second live flight: the core died mid-correction and
    # both rounds burned nothing). NaN when the read failed; NaN fails closed
    # (the flameout staging gate never pops a stage on a missing reading).
    available_thrust: float = float("nan")
    # Patched-conic NEXT orbit (kRPC Orbit.NextOrbit), read only while an SOI
    # change is on the trajectory: the body name the craft will arrive at and
    # the PREDICTED arrival periapsis altitude (m) there -- the arrival-
    # quality evidence the twenty-third flight was blind to (both correction
    # rounds executed to <1 m/s residual, arrival still pe -31.8 km). "" /
    # NaN when absent or unreadable; both fail closed (the arrival
    # re-correction gate never fires without a positive target-body match
    # and a finite sub-floor reading).
    next_body: str = ""
    next_pe: float = float("nan")
    # UT (s) of the FIRST maneuver node (kRPC Node.UT); NaN when no node
    # exists or the read failed. NaN fails closed: the coast's warp-toward-
    # node stair never engages on it (1x, exactly the pre-directive
    # behavior), so an unreadable node is never warped past.
    node_ut: float = float("nan")
    # Seconds until the current orbit changes SOI (kRPC Orbit.TimeToSOIChange);
    # NaN when no SOI change is on the trajectory. NaN fails OPEN for the
    # coast's SOI-approach warp bound (no encounter = nothing to overshoot);
    # a finite value bounds the factor so one 0.5 s poll can never advance
    # past the whole target SOI (the B7 Duna hazard: 100,000x x 0.5 s real =
    # 50,000 game seconds, comparable to an entire Duna SOI transit).
    time_to_soi: float = float("nan")
    # Native warp-to-UT state (runner WarpService): the TARGET UT while a
    # native SpaceCenter.WarpTo is active on the dedicated warp connection,
    # NaN when idle. NaN fails CLOSED for the machine's do-not-touch-rails
    # rule (an unknown warp state is treated as idle, and the bounded
    # self-healing re-issue owns a genuinely lost warp).
    warping_to: float = float("nan")


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
    the latest UT.

    WARP GATE (review N-A4): the detector only advances at 1x (warp_mode
    NONE). Frozen-vessel staleness is a 1x symptom -- kRPC returning the same
    cached floats while the physics runs -- whereas an ON-RAILS craft in a
    (near-)circular orbit can legitimately report bit-identical apsides while
    UT advances (the latent false-trip class). A warped frame HOLDS the
    signature and count unchanged: it is evidence in neither direction, and a
    genuinely dead vessel still trips on the surrounding 1x frames (its
    fields never change across the warp either)."""
    if snapshot.warp_mode != WARP_NONE:
        return prev_sig, prev_count, False
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
    plan_retry_seconds: float = 10.0       # re-issue a failed plan every this many
                                           # game seconds while no node appeared
                                           # (10, was 30: planning is an RPC and
                                           # the plan phases now ride rails warp,
                                           # so the 3-attempt bound costs ~30
                                           # game-s instead of ~90 s at 1x --
                                           # operator PR gate, no-1x-coast)
    plan_warp_factor: int = 2              # PLAN-* rails factor INDEX held
                                           # between plan attempts (2 = 10x,
                                           # altitude-legality-clamped):
                                           # make_nodes needs no 1x, and a 10x
                                           # hold bounds plan-position drift to
                                           # ~5 game-s per poll (spec key
                                           # planWarpFactor; operator PR gate)
    transfer_burn_timeout: float = 4000.0  # TRANSFER-/CORRECTION-BURN budget: the
                                           # NodeExecutor autowarps to the node (up
                                           # to ~1 orbit ahead) then burns
    coast_timeout: float = 400_000.0       # COAST-TO-TARGET budget (game s; the
                                           # LKO->Mun transfer coast is ~2 days)
    flyby_timeout: float = 300_000.0       # TARGET-FLYBY budget (game s)
    coast_warp_factor: int = 6             # COAST-TO-TARGET rails warp factor
                                           # index (6 = 1000x): held via the
                                           # non-blocking set_rails_warp while
                                           # nothing is imminent (spec key
                                           # coastWarpFactor)
    flyby_warp_factor: int = 5             # TARGET-FLYBY rails factor FLOOR
                                           # (5 = 100x: the proven min-altitude
                                           # evidence cadence through periapsis;
                                           # ALSO the SOI-approach floor -- the
                                           # coast's time-to-SOI stair never
                                           # drops below it, so the boundary is
                                           # crossed at ~100x, bounding the
                                           # per-poll overshoot into the SOI to
                                           # ~100 game-s; spec key
                                           # flybyWarpFactor)
    flyby_max_warp_factor: int = 6         # TARGET-FLYBY stair-down CAP: far
                                           # from periapsis the factor rises
                                           # toward this with the remaining
                                           # (altitude - periapsis) distance,
                                           # falling back to the
                                           # flyby_warp_factor floor near
                                           # periapsis (the 100x SOI transit
                                           # took minutes of wall time; the
                                           # outer legs are safe at 1000x+).
                                           # Altitude-legality still clamps
                                           # (spec key flybyMaxWarpFactor)
    node_arrival_margin: float = 15.0      # AIM-THEN-WARP arrival margin (game
                                           # s): after the attitude flip locks
                                           # (aligned debounce) the machine
                                           # warps natively to node_ut minus
                                           # this margin -- rails warp FREEZES
                                           # vessel orientation, so the burn
                                           # vector holds through the warp and
                                           # only this short window plus the
                                           # re-verify frames run at 1x before
                                           # the throttle (spec key
                                           # nodeArrivalMarginSeconds; operator
                                           # PR gate, no-1x-coast; retires
                                           # nodeWarpLeadSeconds)
    soi_lead: float = 30.0                 # native SOI warp lead (game s): the
                                           # post-correction coast and the
                                           # flyby outer legs warp_to_ut to
                                           # now + time_to_soi - this lead, so
                                           # the machine regains poll control
                                           # just before the boundary (the
                                           # inside-lead fallback rides the
                                           # flyby-factor floor, ~100x, never
                                           # 1x) and the body-change frame is
                                           # never inside a high-rate warp.
                                           # 30, was 60: halves the low-rate
                                           # window per crossing (spec key
                                           # soiLeadSeconds; operator PR gate)
    flip_physics_warp: int = 1             # CORRECTION-BURN pre-burn attitude
                                           # flip physics-warp factor INDEX
                                           # (1 = 2x, MechJeb's own WarpToUT
                                           # physics cap -- decompiled 2.15.1;
                                           # the ~340 s 1x flip halves). 0
                                           # reverts to the proven 1x flip.
                                           # Always dropped to 0 BEFORE
                                           # throttle-up (spec key
                                           # flipPhysicsWarpFactor)
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
    burn_nostart_seconds: float = 600.0    # CORRECTION-BURN give-up bound (game
                                           # s): if the DIY burner's attitude
                                           # gate has not opened this long after
                                           # phase entry, the alignment never
                                           # converged -> cut/disengage/clear
                                           # and consume the round. Must exceed
                                           # the worst-case pre-burn flip
                                           # (~340 s on the Kerbal X pod wheel;
                                           # spec key burnNoStartSeconds).
    correction_throttle: float = 0.25      # DIY correction burn throttle (low
                                           # for cut precision; spec key
                                           # correctionThrottle)
    correction_cut_dv: float = 2.0         # cut the DIY burn when the node's
                                           # remaining dv is at/below this m/s
                                           # (spec key correctionCutDvMps)
    correction_settle_seconds: float = 10.0
                                           # MINIMUM game-time settle after
                                           # AP_POINT_NODE before throttle-up
                                           # (AND-gated with the attitude error,
                                           # the B4-proven pattern; spec key
                                           # correctionSettleSeconds)
    max_attitude_error_deg: float = 30.0   # AND-gate: |AutoPilot pointing
                                           # error| must be at/below this
                                           # before the DIY burn starts (NaN
                                           # never passes). 30, not B4's 5: the
                                           # DIY burn CHASES the node's
                                           # remaining vector, so a rough-
                                           # pointed low-throttle start self-
                                           # corrects, and near-anti-parallel
                                           # AP convergence is glacial on this
                                           # craft (tenth live flight: 0.06
                                           # deg/s) -- demanding 5 deg starves
                                           # the round into its give-up (spec
                                           # key maxAttitudeErrorDeg)
    via_bodies: Tuple[str, ...] = ()
                                           # legal INTERMEDIATE coast SOI bodies
                                           # (B7: ("Sun",)); exempt from the coast
                                           # ejection check and legal rails-warp
                                           # bodies. () = B5/B6 (no intermediate).
                                           # Spec key viaBodyNames.
    return_body: str = ""                  # terminal EXIT SOI body after the flyby;
                                           # "" -> home_body (B5/B6 free-return).
                                           # B7: "Sun". Spec key returnBodyName.
    interplanetary_transfer: bool = False  # ORBIT/PLAN-TRANSFER use
                                           # OperationInterplanetaryTransfer instead
                                           # of the moon OperationTransfer. B7: True.
                                           # Spec key interplanetaryTransfer.
    ejection_ecc_floor: float = 0.0        # > 0: TRANSFER-BURN burn-done evidence is
                                           # a hyperbolic home-frame ecc (>= this in
                                           # home SOI) OR already-left-home, NOT the
                                           # apoapsis floor. B7: 1.05. 0 = apoapsis
                                           # floor (B5/B6). Spec key ejectionEccFloor.
    correction_trigger_time_to_soi: Tuple[float, ...] = ()
                                           # DESCENDING time-to-target-SOI thresholds
                                           # (game s) for heliocentric correction
                                           # rounds; non-empty SELECTS time mode and
                                           # supersedes correction_trigger_alts.
                                           # B7: (20_000_000, 500_000). () = altitude
                                           # mode (B5/B6). Spec key
                                           # correctionTriggerTimeToSoiSeconds.
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
        plan_retry_seconds=float(params.get("planRetrySeconds", 10)),
        plan_warp_factor=int(params.get("planWarpFactor", 2)),
        transfer_burn_timeout=float(params.get("transferBurnTimeoutSeconds", 4000)),
        coast_timeout=float(params.get("coastTimeoutSeconds", 400_000)),
        flyby_timeout=float(params.get("flybyTimeoutSeconds", 300_000)),
        coast_warp_factor=int(params.get("coastWarpFactor", 6)),
        flyby_warp_factor=int(params.get("flybyWarpFactor", 5)),
        flyby_max_warp_factor=int(params.get("flybyMaxWarpFactor", 6)),
        node_arrival_margin=float(params.get("nodeArrivalMarginSeconds", 15.0)),
        soi_lead=float(params.get("soiLeadSeconds", 30.0)),
        flip_physics_warp=int(params.get("flipPhysicsWarpFactor", 1)),
        target_periapsis_floor=float(params.get("targetPeriapsisFloorMeters", 10000)),
        burn_stagnant_seconds=float(params.get("burnStagnantSeconds", 120)),
        burn_nostart_seconds=float(params.get("burnNoStartSeconds", 600)),
        correction_throttle=float(params.get("correctionThrottle", 0.25)),
        correction_cut_dv=float(params.get("correctionCutDvMps", 2.0)),
        correction_settle_seconds=float(params.get("correctionSettleSeconds", 10)),
        max_attitude_error_deg=float(params.get("maxAttitudeErrorDeg", 30.0)),
        via_bodies=tuple(str(b) for b in params.get("viaBodyNames", ())),
        return_body=str(params.get("returnBodyName", "")),
        interplanetary_transfer=bool(params.get("interplanetaryTransfer", False)),
        ejection_ecc_floor=float(params.get("ejectionEccFloor", 0.0)),
        correction_trigger_time_to_soi=tuple(
            float(t) for t in params.get("correctionTriggerTimeToSoiSeconds", ())),
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
            # abs(): live B5/B6 flights (2026-07-22) showed kRPC's error
            # reading NEGATIVE (-178 deg mid-flip) -- a signed reading must
            # never satisfy a <=-only gate while pointing the wrong way.
            aligned = (_is_finite(snapshot.ap_error)
                       and abs(snapshot.ap_error) <= state.params.max_attitude_error_deg)
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
    # DIY correction-burner state (live finding 8): ``corr_burn_started``
    # latches the one throttle-up per round; ``min_node_dv`` tracks the lowest
    # finite remaining node dv seen this burn (the overshoot gate compares
    # against it -- a RISING remaining dv means the ship is burning past the
    # node vector).
    corr_burn_started: bool = False
    min_node_dv: Optional[float] = None
    # Consecutive in-gate attitude readings (ALIGNED_DEBOUNCE_FRAMES gate).
    aligned_streak: int = 0
    # Last COMMANDED rails warp factor (the on-change emission discipline for
    # set_rails_warp: warp only ever changes when the machine wants a
    # different speed -- operator design critique 2026-07-22).
    warp_cmd: int = 0
    # Last COMMANDED physics warp factor (the CORRECTION-BURN flip runs at
    # mild physics warp; same on-change + self-healing discipline). Always 0
    # outside CORRECTION-BURN, and always driven back to 0 before throttle-up.
    phys_warp_cmd: int = 0
    # Native warp-to-UT command state (Path A): the target UT the machine
    # last COMMANDED via warp_to_ut, None when no native warp is expected.
    # Cleared on arrival (ut >= target), on cancel, and on every phase exit
    # that cancels. While set, the machine never emits set_rails_warp.
    warp_to_cmd: Optional[float] = None
    # Game-time stamp of the last warp_to_ut emission (initial, retarget, or
    # self-heal re-issue) - bounds the self-healing re-issue to once per
    # WARP_REISSUE_SECONDS.
    last_warp_issue_ut: float = 0.0
    # Consecutive COAST/FLYBY frames with body == "" (no SOI reading). The
    # blank-body hold is fail-closed per frame, but unbounded it would idle
    # at 1x until the GAME-time coast budget expired (~111 wall-hours at 1x;
    # review SF-2) -- at frozen_sample_limit consecutive blanks the vessel is
    # declared lost. Reset by any frame with a real body reading.
    body_blank_count: int = 0
    # Plan emissions this PLAN-* phase (live finding 14): the entry emission
    # counts as attempt 1; each cadence re-plan increments; at
    # PLAN_MAX_ATTEMPTS with node_count still 0 the next cadence check takes
    # the timeout path early. Reset (to 1) on every PLAN-* entry.
    plan_attempts: int = 0
    # AIM-THEN-WARP no-start anchor (operator PR gate): the UT the native
    # warp-to-node ARRIVED in CORRECTION-BURN, re-anchoring the
    # burnNoStartSeconds give-up (time spent inside the rails warp toward the
    # node is not alignment time). None = no arrival yet; the give-up counts
    # from phase entry.
    corr_nostart_anchor_ut: Optional[float] = None
    # Flameout staging (twenty-second flight): consecutive frames a COMMANDED
    # burn read zero available thrust, and stages popped so far (bounded by
    # MAX_FLAMEOUT_STAGES for the whole mission -- staging is irreversible,
    # so the budget never resets between rounds/phases).
    flameout_streak: int = 0
    flameout_stages_done: int = 0
    # Impact-certain early-terminal debounce (TARGET-FLYBY): consecutive
    # frames the impact-warp guard condition held.
    impact_certain_streak: int = 0
    # Arrival-quality re-correction (finding 16): consecutive coast frames
    # the predicted target-body arrival periapsis read below the flyby
    # floor, and extra (non-altitude-triggered) rounds granted so far.
    arrival_bad_streak: int = 0
    extra_rounds_done: int = 0
    # No-encounter early trigger (finding 18): consecutive time-mode via-
    # body coast frames with NO target encounter on the trajectory.
    no_encounter_streak: int = 0
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


# ---------------------------------------------------------------------------
# B7 interplanetary helpers (design docs/dev/design-autotest-b7-duna.md,
# section 5.4). All pure; with the B7 params at their defaults every helper
# reproduces the pre-B7 B5/B6 code path byte-identically.
# ---------------------------------------------------------------------------


def _b5_return_body(params: B5Params) -> str:
    """The terminal exit SOI body: return_body if set, else home_body (B5/B6
    free-return)."""
    return params.return_body or params.home_body


def _b5_coast_bodies(params: B5Params) -> Tuple[str, ...]:
    """Bodies whose presence in COAST-TO-TARGET is NOT an ejection: "" (no
    reading), the home body, and every via body."""
    return ("", params.home_body) + params.via_bodies


def _b5_warp_bodies(params: B5Params) -> Tuple[str, ...]:
    """Bodies over which the coast legitimately operates (home + via).
    Excludes "": an empty reading holds warp state and counts the blank-body
    dwell instead. Also the body domain of the arrival-quality re-correct
    gate (finding 16): with via bodies the heliocentric coast can grant the
    extra round too; with via_bodies=() this is exactly (home_body,), the
    pre-B7 gate."""
    return (params.home_body,) + params.via_bodies


def _b5_transfer_plan_action(params: B5Params) -> Action:
    """The transfer plan action: interplanetary (WaitForPhaseAngle) when
    interplanetary_transfer, else the moon Hohmann transfer."""
    if params.interplanetary_transfer:
        return Action(ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER)
    return Action(ACTION_MJ_PLAN_TRANSFER)


def _b5_transfer_burn_done(params: B5Params, snapshot: TelemetrySnapshot) -> bool:
    """TRANSFER-BURN burn-done evidence. B5/B6: the home-frame apoapsis reached
    transfer_min_apoapsis. B7 (ejection_ecc_floor > 0): a HYPERBOLIC home-frame
    eccentricity (>= floor while still in the home SOI) OR the craft ALREADY
    left the home SOI (body is a via body / the target -- the heliocentric
    frame's ecc is < 1 and would falsely fail the first disjunct). NaN ecc
    fails closed."""
    if params.ejection_ecc_floor > 0.0:
        if snapshot.body in params.via_bodies or snapshot.body == params.target_body:
            return True
        return (snapshot.body == params.home_body
                and _is_finite(snapshot.eccentricity)
                and snapshot.eccentricity >= params.ejection_ecc_floor)
    return (_is_finite(snapshot.apoapsis)
            and snapshot.apoapsis >= params.transfer_min_apoapsis)


def _b5_correction_triggers(params: B5Params) -> Tuple[float, ...]:
    """The active correction-round trigger list: the time-to-SOI list when set
    (B7), else the altitude list (B5/B6)."""
    return params.correction_trigger_time_to_soi or params.correction_trigger_alts


def _b5_rounds_pending(state: B5State) -> bool:
    """True iff more correction rounds may still fire (corrections enabled and
    fewer rounds done than triggers)."""
    return (state.params.course_correct_periapsis > 0.0
            and state.correction_rounds_done < len(_b5_correction_triggers(state.params)))


def _b5_correction_round_ready(state: B5State, snapshot: TelemetrySnapshot) -> bool:
    """True iff the current correction round's trigger has fired this frame.
    TIME mode (B7): body is a via body AND time_to_soi finite AND <= the round's
    threshold (fires in heliocentric space, never during the home-SOI escape,
    and only while a target encounter exists -- which is also
    OperationCourseCorrection's precondition).
    ALTITUDE mode (B5/B6): body == home AND altitude finite AND >= the round's
    threshold. Both NaN-fail-closed."""
    p = state.params
    if not _b5_rounds_pending(state):
        return False
    idx = state.correction_rounds_done
    if p.correction_trigger_time_to_soi:
        return (snapshot.body in p.via_bodies
                and _is_finite(snapshot.time_to_soi)
                and snapshot.time_to_soi <= p.correction_trigger_time_to_soi[idx])
    return (snapshot.body == p.home_body
            and _is_finite(snapshot.altitude)
            and snapshot.altitude >= p.correction_trigger_alts[idx])


# Burn-stagnation watchdog thresholds: "the burn happened" = an apsis moved
# more than _BURN_CHANGED_EPS since BURN-phase entry; "static" = frame-to-frame
# apsis movement under _BURN_STATIC_EPS (a coasting conic is rock-stable; any
# thrust moves the apsides at km/s-class rates).
_BURN_CHANGED_EPS = 10_000.0
_BURN_STATIC_EPS = 50.0


def _b5_track_burn_stagnation(
        state: B5State,
        snapshot: TelemetrySnapshot) -> Tuple[B5State, bool, bool, bool]:
    """Advance the BURN-phase stagnation watchdog one frame; return
    (new_state, stuck_after_burn, stuck_no_start, burned).

    ``burned`` (finding 17): the orbit changed since burn entry -- a burn
    demonstrably ran. TRANSFER-BURN's executor-flameout staging gate reads
    it (the executor collapses the throttle when the engine dies, so
    burn-evidence must come from the orbit, not the throttle readback).

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
                       burn_static_since=None), False, False, False
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
    return (replace(state, burn_prev_ap=ap, burn_prev_pe=pe,
                    burn_static_since=since),
            stuck_after_burn, stuck_no_start, burned)


def _b5_flameout_stage(state: B5State,
                       snapshot: TelemetrySnapshot,
                       mid_burn: bool = False) -> Tuple[B5State, List[Action]]:
    """Flameout-staging watchdog for the BURN phases (twenty-second live
    flight 2026-07-22): a COMMANDED burn -- throttle READBACK above
    FLAMEOUT_THROTTLE_EPS -- reading ZERO available thrust means the active
    stage is dry or flamed out (the Kerbal X core died mid-correction with
    the full X200-16 upper tank unreachable behind its decoupler; both
    correction rounds no-progress-gave-up burning nothing and the
    under-corrected arrival was an impact). After FLAMEOUT_DEBOUNCE_FRAMES
    consecutive such frames, pop ONE stage (ACTION_ACTIVATE_STAGE) and
    re-stamp the no-progress anchor so the fresh stage earns a full progress
    window; bounded at MAX_FLAMEOUT_STAGES per mission. A NaN
    available_thrust or throttle fails closed: a missing reading never pops
    stages (the no-progress give-up still owns that outcome).

    ``mid_burn`` (finding 17, B7 third flight 2026-07-22): the MechJeb
    NodeExecutor COLLAPSES the throttle to zero when the engine dies (the
    B7 ejection flamed out at 476.9 of 797.6 m/s remaining, thr readback
    0.000), so the commanded-throttle evidence never fires under it. The
    TRANSFER-BURN caller passes mid_burn=True when a burn DEMONSTRABLY ran
    (orbit changed since phase entry) and the node is still pending --
    zero available thrust then means the stage died mid-burn regardless of
    the collapsed throttle."""
    flamed = (_is_finite(snapshot.available_thrust)
              and snapshot.available_thrust <= 0.0
              and (mid_burn
                   or (_is_finite(snapshot.throttle)
                       and snapshot.throttle > FLAMEOUT_THROTTLE_EPS)))
    if not flamed:
        if state.flameout_streak:
            return replace(state, flameout_streak=0), []
        return state, []
    streak = state.flameout_streak + 1
    if (streak >= FLAMEOUT_DEBOUNCE_FRAMES
            and state.flameout_stages_done < MAX_FLAMEOUT_STAGES):
        return (replace(state, flameout_streak=0,
                        flameout_stages_done=state.flameout_stages_done + 1,
                        burn_static_since=(float(snapshot.ut)
                                           if _is_finite(snapshot.ut)
                                           else state.burn_static_since)),
                [Action(ACTION_ACTIVATE_STAGE)])
    # Delta-review C1: CAP the streak at the debounce depth -- past the
    # stage budget every flamed frame would otherwise increment it forever,
    # and each increment is a gate line + a 21-line window dump (~5,000
    # noise lines across a 120 s exhausted-budget flameout episode).
    return replace(state, flameout_streak=min(streak,
                                              FLAMEOUT_DEBOUNCE_FRAMES)), []


def _b5_plan_phase(state: B5State, snapshot: TelemetrySnapshot, peak: Optional[float],
                   plan_action: Action, burn_phase: str,
                   on_timeout_phase: Optional[str],
                   handoff_action: Action = Action(ACTION_MJ_EXECUTE_NODES)) -> Tuple[B5State, List[Action]]:
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
            # clear the frame-to-frame tracking. Also reset the DIY-burner
            # latches for a correction round.
            burn_entry_ap=(snapshot.apoapsis if _is_finite(snapshot.apoapsis) else None),
            burn_entry_pe=(snapshot.periapsis if _is_finite(snapshot.periapsis) else None),
            burn_prev_ap=None, burn_prev_pe=None, burn_static_since=None,
            corr_burn_started=False, min_node_dv=None, aligned_streak=0,
            # Delta-review A2: a stale streak of 1 left by a prior burn's
            # exit frame would weaken the next burn's flameout debounce to a
            # single frame -- exactly the transient the debounce exists for.
            corr_nostart_anchor_ut=None, flameout_streak=0)
        return entered, [handoff_action]
    if _b5_over_budget(state, snapshot) and on_timeout_phase is not None:
        return _b5_enter(state, on_timeout_phase, snapshot.ut, peak), []
    stayed = _b5_stay_or_flake(state, snapshot, peak)
    if stayed.done:
        return stayed, []
    # PLAN-phase rails hold (operator PR gate, no-1x-coast): planning is an
    # RPC -- make_nodes needs no 1x -- so between attempts the machine rides
    # planWarpFactor (default 10x, altitude-legality-clamped), bounding plan-
    # position drift to ~5 game-s per poll. The frozen detector is warp-gated
    # (review N-A4), so these frames advance no staleness count.
    actions: List[Action] = []
    desired = min(state.params.plan_warp_factor,
                  max_legal_rails_factor(snapshot.body, snapshot.altitude))
    if _rails_emit_needed(desired, stayed.warp_cmd, snapshot):
        actions.append(Action(ACTION_SET_RAILS_WARP, float(desired)))
        stayed = replace(stayed, warp_cmd=desired)
    if (_is_finite(snapshot.ut)
            and (snapshot.ut - state.last_plan_ut) >= state.params.plan_retry_seconds):
        # Plan-attempt give-up (live finding 14): PLAN_MAX_ATTEMPTS plans in
        # and still no node -- whether the planner keeps failing server-side
        # or the runner keeps DISQUALIFYING the plans (over-cap removal, which
        # the machine cannot distinguish) -- take the timeout path EARLY
        # instead of idling out the full plan budget: PLAN-CORRECTION
        # falls through to the coast (the caller consumes the round),
        # PLAN-TRANSFER flakes (no transfer = no mission).
        if state.plan_attempts >= PLAN_MAX_ATTEMPTS:
            if on_timeout_phase is not None:
                return _b5_enter(stayed, on_timeout_phase, snapshot.ut, peak), actions
            return replace(state, peak_apoapsis=peak, verdict=MISSION_FLAKE,
                           flake_phase=state.phase, done=True), []
        return (replace(stayed, last_plan_ut=snapshot.ut,
                        plan_attempts=state.plan_attempts + 1),
                actions + [plan_action])
    return stayed, actions


def _b5_enter_plan_correction(state: B5State, snapshot: TelemetrySnapshot,
                              peak: Optional[float]) -> Tuple[B5State, List[Action]]:
    """Shared PLAN-CORRECTION entry (altitude trigger + finding-16 arrival-
    quality re-correct). Prelude: bring warp under PLAN control before
    planning -- cancel an active native warp (which also zeroes the rails
    factors runner-side; the plan phase re-raises to its own factor next
    frame), else step a held rails factor straight to the plan hold
    (operator PR gate: never 1x -- planning is an RPC and 10x bounds
    plan-position drift to ~5 game-s per poll)."""
    entered = _b5_enter(state, B5_PLAN_CORRECTION, snapshot.ut, peak)
    plan_hold = min(state.params.plan_warp_factor,
                    max_legal_rails_factor(snapshot.body, snapshot.altitude))
    if state.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
        prelude = [Action(ACTION_CANCEL_WARP)]
        entered_warp_cmd = 0
    elif state.warp_cmd != plan_hold:
        prelude = [Action(ACTION_SET_RAILS_WARP, float(plan_hold))]
        entered_warp_cmd = plan_hold
    else:
        prelude = []
        entered_warp_cmd = state.warp_cmd
    entered = replace(entered,
                      last_plan_ut=snapshot.ut if _is_finite(snapshot.ut) else 0.0,
                      warp_cmd=entered_warp_cmd, warp_to_cmd=None,
                      body_blank_count=0, plan_attempts=1)
    return entered, prelude + [Action(ACTION_MJ_PLAN_COURSE_CORRECT,
                                      state.params.course_correct_periapsis,
                                      limit=state.params.max_correction_dv)]


def _rails_emit_needed(desired: int, warp_cmd: int,
                       snapshot: TelemetrySnapshot) -> bool:
    """The rails-factor emission discipline, all three directions:
      - ON CHANGE: the desired factor differs from the last commanded one.
      - UNDER-WARP self-heal (fifteenth flight): the game is NOT rails-warping
        despite a nonzero command (manual changes / KSP's own drops).
      - OVER-WARP pull-down (review SF-1): the game is rails-warping FASTER
        than the desired factor's rate (manual warp-up, or a stale high rate
        left behind) -- including desired == 0, where any sustained rails rate
        above 1x must be pulled back down. The 1% tolerance ignores rate-ramp
        jitter around the commanded rate.
    Callers only reach this when NO native warp is commanded/active (the
    native branches return earlier with hold/cancel), so a WarpService warp
    legitimately running rates the stair never commanded is exempt by
    construction."""
    if desired != warp_cmd:
        return True
    if desired > 0 and snapshot.warp_mode != WARP_RAILS:
        return True
    if (snapshot.warp_mode == WARP_RAILS and _is_finite(snapshot.warp_rate)
            and snapshot.warp_rate > RAILS_WARP_RATES[desired] * 1.01):
        return True
    return False


def _b5_clear_arrived_warp(state: B5State, snapshot: TelemetrySnapshot) -> B5State:
    """Clear the native warp command once the target UT is reached: the
    server-side WarpTo stepper zeroes the factor itself on natural completion
    (pinned kRPC SpaceCenter.cs WarpTo), so arrival needs no cancel action --
    only the machine's expectation flag drops."""
    if (state.warp_to_cmd is not None and _is_finite(snapshot.ut)
            and snapshot.ut >= state.warp_to_cmd):
        return replace(state, warp_to_cmd=None)
    return state


def _b5_native_warp(state: B5State, snapshot: TelemetrySnapshot,
                    target: float) -> Tuple[B5State, List[Action]]:
    """Drive the native warp_to_ut command toward ``target`` one frame.

    Emission discipline (mirrors the rails on-change + self-healing rules):
      - No command yet, or the fresh target moved more than
        WARP_RETARGET_THRESHOLD_SECONDS from the commanded one (an SOI
        estimate shift): (re-)issue warp_to_ut. The runner cancels any
        in-flight warp before re-issuing (kRPC WarpTo cannot retarget).
      - Self-heal: the game reports NO active warp (warping_to NaN) while the
        commanded target is still ahead -- re-issue, bounded to once per
        WARP_REISSUE_SECONDS of game time so a genuinely-completing warp is
        never spammed.
    While a native warp is commanded the rails factor belongs to the server
    stepper, so warp_cmd is pinned to 0 (the runner's cancel path also zeroes
    the real factors)."""
    ut = snapshot.ut
    if (state.warp_to_cmd is None
            or abs(target - state.warp_to_cmd) > WARP_RETARGET_THRESHOLD_SECONDS):
        issued = replace(state, warp_to_cmd=float(target),
                         last_warp_issue_ut=(ut if _is_finite(ut) else 0.0),
                         warp_cmd=0)
        return issued, [Action(ACTION_WARP_TO_UT, float(target))]
    if (not _is_finite(snapshot.warping_to)
            and _is_finite(ut) and ut < state.warp_to_cmd
            and (ut - state.last_warp_issue_ut) >= WARP_REISSUE_SECONDS):
        healed = replace(state, last_warp_issue_ut=ut, warp_cmd=0)
        return healed, [Action(ACTION_WARP_TO_UT, float(state.warp_to_cmd))]
    return state, []


def _b5_hold_blank_body(stayed: B5State) -> Tuple[B5State, List[Action]]:
    """One COAST/FLYBY frame with body == "" (no SOI reading): HOLD all warp
    state (never cancel/re-command on a transient blank), but BOUND the dwell
    (review SF-2) -- at frozen_sample_limit consecutive blanks the vessel is
    treated as lost (the coast budget is GAME time, so an unbounded 1x blank
    hold could idle for ~111 wall-hours before the outer watchdog fired)."""
    count = stayed.body_blank_count + 1
    limit = stayed.params.frozen_sample_limit
    if count >= limit:
        return replace(
            stayed, body_blank_count=count, done=True,
            verdict=MISSION_ASSERT_FAIL,
            loss_reason=("vessel-lost (SOI body unreadable %d consecutive "
                         "samples; vessel presumed destroyed or unreadable)"
                         % count)), []
    return replace(stayed, body_blank_count=count), []


def _b5_cancel_native_warp(state: B5State,
                           snapshot: TelemetrySnapshot) -> Tuple[B5State, List[Action]]:
    """Emit cancel_warp when a native warp is commanded OR the game still
    reports one active (warping_to finite); no-op otherwise. The runner's
    cancel closes the warp connection and zeroes both warp factors, so
    warp_cmd resets to 0 with it."""
    if state.warp_to_cmd is None and not _is_finite(snapshot.warping_to):
        return state, []
    return (replace(state, warp_to_cmd=None, warp_cmd=0),
            [Action(ACTION_CANCEL_WARP)])


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
      - CORRECTION-BURN -> COAST-TO-TARGET: AIM-THEN-WARP (operator PR gate):
        point at the node (2x-physics flip + aligned debounce), natively warp
        to node_ut - nodeArrivalMarginSeconds with the orientation frozen by
        rails, re-verify the streak on arrival (drift re-enters the flip;
        the no-start give-up re-anchors at arrival), throttle, then exit on
        cut/overshoot/no-progress/node-consumed (no apoapsis gate: a
        correction is a small vector tweak).
      - COAST-TO-TARGET (Path A native warp + rails stair; operator PR gate
        no-1x-coast): a pending node issues a NATIVE warp_to_ut to
        node_ut - nodeArrivalMarginSeconds (1x only inside the margin / NaN
        node UT, fail closed); the correction-trigger approach keeps the
        LIVE-PROVEN rails distance stair floored at factor 2 (SOI time
        bound + altitude-legality clamp); the
        post-correction coast issues a NATIVE warp_to_ut to
        now + time_to_soi - soiLeadSeconds (re-issued only when the SOI
        estimate shifts > WARP_RETARGET_THRESHOLD_SECONDS; self-healed at
        most once per WARP_REISSUE_SECONDS when the game reports no active
        warp); otherwise the held rails coast factor. While a native warp is
        commanded the machine NEVER emits set_rails_warp (cancel first).
        B7 (correctionTriggerTimeToSoiSeconds non-empty) triggers rounds on
        TIME-TO-TARGET-SOI thresholds over a via body instead, approaching a
        pending trigger with a native warp_to_ut to the trigger UT and a
        factor-2-floored rails time stair inside soiLeadSeconds of it.
        body == target -> TARGET-FLYBY. body not in the coast set (home +
        viaBodyNames; "" HOLDS with no warp change) -> ASSERT-FAIL (ejected:
        the craft left the allowed coast bodies without meeting the target).
      - TARGET-FLYBY: track the min finite altitude (the flyby-floor
        evidence); the outer SOI legs issue a NATIVE warp_to_ut to the SOI
        EXIT minus soiLeadSeconds (the game's own altitude limits shape the
        periapsis passage at the proven ~100x cadence); inside the lead
        window / no estimate the rails stair fallback runs (flybyWarpFactor
        floor, flybyMaxWarpFactor cap, legality clamp). The impact guard is
        AUTHORITATIVE: it cancels any native warp and holds 1x.
        body == the exit body (returnBodyName, defaulting to home) -> RETURN
        (terminal: done, verdict None; cancels any native warp; the settle
        tail runs in the exit SOI). body neither target nor exit ->
        ASSERT-FAIL (slung off-course).
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
        # ask the ManeuverPlanner for the transfer (moon Hohmann, or the B7
        # interplanetary window plan when interplanetary_transfer), then wait
        # for the node.
        entered = _b5_enter(state, B5_PLAN_TRANSFER, snapshot.ut, peak)
        entered = replace(entered,
                          last_plan_ut=snapshot.ut if _is_finite(snapshot.ut) else 0.0,
                          plan_attempts=1)
        return entered, [
            Action(ACTION_SET_TARGET_BODY, text=state.params.target_body),
            _b5_transfer_plan_action(state.params),
        ]

    if state.phase == B5_PLAN_TRANSFER:
        return _b5_plan_phase(
            state, snapshot, peak,
            plan_action=_b5_transfer_plan_action(state.params),
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
        state, stuck, _nostart, burned = _b5_track_burn_stagnation(state, snapshot)
        consumed = snapshot.node_count < max(state.planned_node_count, 1)
        # Burn-done evidence: the B5/B6 apoapsis floor, or the B7 hyperbolic
        # ejection gate when ejection_ecc_floor > 0 (an escape burn drives the
        # home-frame apoapsis NEGATIVE, so the floor cannot be the evidence).
        # For B7 the under-burn flake below means "the ejection did not make
        # the orbit hyperbolic".
        floor_met = _b5_transfer_burn_done(state.params, snapshot)
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
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return stayed, []
        # Flameout staging AFTER the exit/flake checks (delta-review A1/A3:
        # an exit frame must neither consume a stage-budget slot for a
        # dropped action nor stage a vessel on a dead mission). mid_burn
        # evidence (finding 17, B7 third flight): the MechJeb executor
        # COLLAPSES the throttle to zero when the engine dies (the ejection
        # flamed out at 476.9 of 797.6 m/s remaining, thr readback 0.000),
        # so the commanded-throttle gate is blind under it -- a burn that
        # demonstrably ran (orbit changed since entry) with the node still
        # pending substitutes as the burn evidence. Pre-burn autowarp coast
        # frames stay closed: nothing has burned yet, and the engine is
        # alive (avThr > 0) until the moment it dies mid-burn.
        mid_burn = burned and snapshot.node_count >= max(state.planned_node_count, 1)
        return _b5_flameout_stage(stayed, snapshot, mid_burn=mid_burn)

    if state.phase == B5_PLAN_CORRECTION:
        new_state, actions = _b5_plan_phase(
            state, snapshot, peak,
            plan_action=Action(ACTION_MJ_PLAN_COURSE_CORRECT,
                               state.params.course_correct_periapsis,
                               limit=state.params.max_correction_dv),
            burn_phase=B5_CORRECTION_BURN,
            on_timeout_phase=B5_COAST_TO_TARGET,
            # DIY burner handoff (live finding 8): point the native AP at the
            # node instead of engaging MechJeb's executor, whose close-in-node
            # AlignedAndSettled gate the Kerbal X can never satisfy.
            handoff_action=Action(ACTION_AP_POINT_NODE))
        if new_state.phase == B5_COAST_TO_TARGET:
            # Timeout fall-through consumes this round (a disqualified/failed
            # plan never blocks the coast; the NEXT round may still refine).
            new_state = replace(new_state,
                                correction_rounds_done=state.correction_rounds_done + 1)
        return new_state, actions

    if state.phase == B5_CORRECTION_BURN:
        # DIY correction burner (live finding 8): the B4-proven native-AP
        # pattern. Settle + attitude AND-gate, one low-throttle burn, cut when
        # the node's remaining dv reaches the cut threshold or starts RISING
        # (burning past the vector). Every exit consumes the round and cleans
        # up (throttle, AP, leftover nodes); the flyby floor assertion still
        # judges the outcome.
        def _corr_exit(st: B5State) -> Tuple[B5State, List[Action]]:
            entered = _b5_enter(st, B5_COAST_TO_TARGET, snapshot.ut, peak)
            entered = replace(entered,
                              correction_rounds_done=st.correction_rounds_done + 1,
                              corr_burn_started=False, min_node_dv=None,
                              phys_warp_cmd=0, warp_to_cmd=None,
                              corr_nostart_anchor_ut=None)
            cleanup = [Action(ACTION_CUT_THROTTLE, 0.0), Action(ACTION_AP_DISENGAGE)]
            if st.phys_warp_cmd != 0:
                # The flip ran under physics warp and the burn never started
                # (node vanished / alignment give-up): drop it on the way out.
                cleanup.append(Action(ACTION_SET_PHYSICS_WARP, 0.0))
            if st.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
                # An aim-then-warp native warp is still in flight (node
                # vanished mid-warp / give-up): cancel it on the way out.
                cleanup.append(Action(ACTION_CANCEL_WARP))
            if snapshot.node_count > 0:
                cleanup.append(Action(ACTION_MJ_ABORT_AND_CLEAR_NODES))
            return entered, cleanup

        dv = snapshot.node_dv
        # ``improved`` = the remaining dv made real progress this frame (a
        # strict drop below the tracked minimum). While a burn is live, each
        # improvement re-stamps ``burn_static_since`` (the progress anchor);
        # a FROZEN dv leaves the anchor put, so no-progress accrues.
        improved = (_is_finite(dv)
                    and (state.min_node_dv is None or dv < state.min_node_dv - 0.01))
        if _is_finite(dv) and (state.min_node_dv is None or dv < state.min_node_dv):
            state = replace(state, min_node_dv=float(dv))

        if state.corr_burn_started:
            if improved and _is_finite(snapshot.ut):
                state = replace(state, burn_static_since=snapshot.ut)
            overshoot = (_is_finite(dv) and state.min_node_dv is not None
                         and dv > state.min_node_dv + 0.5)
            # NO-PROGRESS give-up (tenth live flight 2026-07-22: a B6 "burn"
            # sat with the remaining dv FROZEN for 2500 frames -- zero thrust
            # despite the throttle command): if the remaining dv has not
            # dropped within burnStagnantSeconds of the throttle-up (or the
            # last progress), nothing is burning; give the round up cleanly.
            no_progress = (_is_finite(snapshot.ut)
                           and state.burn_static_since is not None
                           and (snapshot.ut - state.burn_static_since)
                           >= state.params.burn_stagnant_seconds)
            if (snapshot.node_count == 0
                    or (_is_finite(dv) and dv <= state.params.correction_cut_dv)
                    or overshoot or no_progress):
                return _corr_exit(state)
            stayed = _b5_stay_or_flake(state, snapshot, peak)
            if stayed.done:
                return stayed, []
            # Flameout staging AFTER the exit/flake checks (delta-review
            # A1/A3): a dry stage under a commanded throttle pops the next
            # stage and re-stamps the progress anchor instead of idling out
            # the no-progress window against an engine that cannot burn
            # (twenty-second flight); an exit frame neither consumes a
            # budget slot for a dropped action nor stages a dead mission.
            # The pop lands ~1 s after flameout, ~119 s before the
            # no-progress give-up could co-fire.
            return _b5_flameout_stage(stayed, snapshot)

        # Pre-burn: node vanished (defensive; the plan handoff requires one) ->
        # give the round up cleanly (cancels a mid-flight aim-warp too).
        if snapshot.node_count == 0:
            return _corr_exit(state)
        # AIM-THEN-WARP warp-hold (operator PR gate, no-1x-coast): the aim
        # locked and the native warp toward node_ut - nodeArrivalMarginSeconds
        # is running -- rails warp FREEZES orientation, so the machine just
        # holds (self-healed, bounded by the burn budget). Give-up clocks do
        # not count warp time.
        if state.warp_to_cmd is not None:
            if not (_is_finite(snapshot.ut) and snapshot.ut >= state.warp_to_cmd):
                held = _b5_stay_or_flake(state, snapshot, peak)
                if held.done:
                    # Budget flake mid-warp: leave nothing warped behind.
                    return (replace(held, warp_to_cmd=None),
                            [Action(ACTION_CANCEL_WARP)])
                return _b5_native_warp(held, snapshot, state.warp_to_cmd)
            # ARRIVAL: the server stepper zeroed the factor on completion.
            # Re-verify the attitude from FRESH readings (the streak re-earns
            # its full debounce -- rails should have held the orientation; a
            # drifted apErr re-enters the 2x-physics flip below, bounded by
            # the re-anchored give-up) and restart the no-start clock (warp
            # time is not alignment time).
            state = replace(state, warp_to_cmd=None, aligned_streak=0,
                            corr_nostart_anchor_ut=float(snapshot.ut))
        # HIGH-RATE FRAMES ARE NOT ALIGNMENT TIME (findings 19/19b, B7
        # fifth + sixth flights): a round granted from a 100,000x
        # heliocentric coast enters this phase mid-RAMP-DOWN and the
        # GAME-time no-start budget (600 s) evaporates in ~two polls --
        # both no-encounter rounds were consumed with the full plan
        # unburned and apErr frozen ~110 deg, the ship never having tried
        # to turn. 19b: the mode LABEL cannot gate this -- commanding the
        # physics flip mid-ramp flips TimeWarp.Mode to LOW immediately
        # while CurrentRate is still decaying from 100,000 (kRPC truthfully
        # reports PHYSICS at 5.32x), so the re-anchor keys on the OBSERVED
        # RATE: any frame above the legitimate flip regime re-anchors the
        # clock; genuine 1x-4x flip frames count, keeping the give-up
        # bounded. Same warp-time-is-not-alignment-time principle as the
        # aim-warp arrival re-anchor.
        if (_is_finite(snapshot.ut)
                and (snapshot.warp_mode == WARP_RAILS
                     or (_is_finite(snapshot.warp_rate)
                         and snapshot.warp_rate > NOSTART_COUNTABLE_RATE_MAX))):
            state = replace(state, corr_nostart_anchor_ut=float(snapshot.ut))
        # Alignment never converging is bounded: give the round up after
        # burnNoStartSeconds rather than flake the whole mission. The clock
        # counts from phase entry, the aim-warp ARRIVAL, or the last
        # rails-warped frame.
        nostart_anchor = (state.corr_nostart_anchor_ut
                          if state.corr_nostart_anchor_ut is not None
                          else state.phase_entry_ut)
        if (_is_finite(snapshot.ut)
                and (snapshot.ut - nostart_anchor) >= state.params.burn_nostart_seconds):
            return _corr_exit(state)
        settled = (_is_finite(snapshot.ut)
                   and (snapshot.ut - state.phase_entry_ut) >= state.params.correction_settle_seconds)
        # abs(): kRPC's error reads NEGATIVE in some regimes (-178 deg
        # mid-flip on the tenth live flight) -- a signed reading must never
        # satisfy a <=-only gate while pointing the wrong way. The 30-degree
        # default (vs B4's 5) is deliberate: the DIY burn CHASES the node's
        # remaining vector (the AP tracks node.reference_frame), so a
        # rough-pointed low-throttle start self-corrects, and the overshoot +
        # no-progress guards own the failure modes. K-CONSECUTIVE debounce
        # (ALIGNED_DEBOUNCE_FRAMES): a single-frame transient reading fired a
        # ~200 m/s wild burn at a true ~98 deg off-axis (fourteenth flight).
        aligned = (_is_finite(snapshot.ap_error)
                   and abs(snapshot.ap_error) <= state.params.max_attitude_error_deg)
        # Capped at the debounce depth (delta-review C1): the gate only ever
        # tests >= ALIGNED_DEBOUNCE_FRAMES, and an uncapped streak emits a
        # gate line + 21-line window dump per aligned settle frame.
        streak = (min(state.aligned_streak + 1, ALIGNED_DEBOUNCE_FRAMES)
                  if aligned else 0)
        stayed = replace(_b5_stay_or_flake(state, snapshot, peak),
                         aligned_streak=streak)
        if stayed.done:
            # Budget flake mid-flip: leave nothing warped behind.
            return stayed, ([Action(ACTION_SET_PHYSICS_WARP, 0.0)]
                            if state.phys_warp_cmd != 0 else [])
        if settled and streak >= ALIGNED_DEBOUNCE_FRAMES:
            # BURN ONLY AT 1x: the flip may run under mild physics warp, but a
            # throttle-up at scaled physics dt would coarsen the cut/overshoot
            # gates, so the warp is dropped FIRST and the throttle waits for a
            # frame that reads warp NONE (one extra ~0.5 s poll, the B4-proven
            # settle discipline).
            if state.phys_warp_cmd != 0 or snapshot.warp_mode != WARP_NONE:
                return (replace(stayed, phys_warp_cmd=0),
                        [Action(ACTION_SET_PHYSICS_WARP, 0.0)])
            # AIM DONE -> WARP TO THE NODE (operator PR gate, no-1x-coast):
            # the burn vector is inertially fixed and rails warp FREEZES the
            # vessel orientation, so with the attitude locked the machine
            # warps natively to node_ut - nodeArrivalMarginSeconds instead of
            # 1x-coasting the wait. Fires only while the node is still beyond
            # the margin (post-arrival frames fail this bound, so the warp
            # can never re-issue); the streak resets so arrival re-earns the
            # full aligned debounce.
            if (_is_finite(snapshot.node_ut) and _is_finite(snapshot.ut)
                    and snapshot.ut < snapshot.node_ut - state.params.node_arrival_margin):
                aim_target = snapshot.node_ut - state.params.node_arrival_margin
                return _b5_native_warp(replace(stayed, aligned_streak=0),
                                       snapshot, aim_target)
            started = replace(stayed, corr_burn_started=True,
                              burn_static_since=(snapshot.ut if _is_finite(snapshot.ut)
                                                 else None))
            return started, [Action(ACTION_SET_THROTTLE, state.params.correction_throttle)]
        # Still flipping/settling: run the attitude flip under mild PHYSICS
        # warp (default 2x -- MechJeb's own WarpToUT physics cap; the ~340 s
        # 1x crawl was the single biggest 1x wall-time block in the mission).
        # Same on-change + self-healing emission discipline as the rails
        # factor; flipPhysicsWarpFactor=0 disables (byte-identical old flip).
        desired_phys = state.params.flip_physics_warp
        if (desired_phys != state.phys_warp_cmd
                or (desired_phys > 0 and snapshot.warp_mode != WARP_PHYSICS)):
            return (replace(stayed, phys_warp_cmd=desired_phys),
                    [Action(ACTION_SET_PHYSICS_WARP, float(desired_phys))])
        return stayed, []

    if state.phase == B5_COAST_TO_TARGET:
        if snapshot.body == state.params.target_body:
            return _b5_enter(state, B5_TARGET_FLYBY, snapshot.ut, peak), []
        if snapshot.body not in _b5_coast_bodies(state.params):
            # A REAL foreign body name is the ejected terminal; "" (no reading
            # this frame) is NOT -- it stays in phase with no hop, and a
            # sustained unreadable vessel dies at the vessel-lost terminal. A
            # via body (B7: the Sun) is a legal INTERMEDIATE coast SOI, never
            # an ejection.
            return replace(
                state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("left home SOI without reaching the target: body=%r "
                             "(allowed %r, target %r)"
                             % (snapshot.body, _b5_coast_bodies(state.params),
                                state.params.target_body))), []
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return stayed, []
        # Correction rounds: one PLAN-CORRECTION entry per trigger (altitude
        # thresholds for B5/B6; DESCENDING time-to-target-SOI thresholds for
        # B7's heliocentric coast -- a Kerbin-altitude trigger can never fire
        # in Sun SOI). Round 2+ is LIVE-PROVEN necessary (flight 4: the
        # post-TLI correction flew, but executor residual over the long coast
        # drifted the flyby periapsis from +60 km to -29 km = impact; a
        # mid-coast refinement prices the residual at a few m/s).
        triggers = _b5_correction_triggers(state.params)
        rounds_pending = _b5_rounds_pending(state)
        if _b5_correction_round_ready(state, snapshot):
            return _b5_enter_plan_correction(state, snapshot, peak)
        # NO-ENCOUNTER EARLY TRIGGER (finding 18, B7 fourth flight): the
        # phase-angle interplanetary ejection produced NO target encounter
        # (tts NaN the whole heliocentric coast), the time-to-SOI triggers
        # correctly never fired (fail-closed), and the coast sailed past
        # Duna's orbit to the budget flake. In TIME mode over a via body
        # with NO encounter on the trajectory, fire the pending round EARLY
        # (debounced against transient NaN reads) so the course-correct
        # plan can CREATE the encounter mid-course; a planner that cannot
        # (throws server-side) burns the round through the existing
        # PLAN_MAX_ATTEMPTS fall-through, keeping the failure bounded.
        # Design Q5's "expected reliable encounter" assumption is REFUTED
        # live; this replaces its accepted-flake posture.
        no_encounter = (bool(state.params.correction_trigger_time_to_soi)
                        and rounds_pending
                        and snapshot.body in state.params.via_bodies
                        and snapshot.node_count == 0
                        and not _is_finite(snapshot.time_to_soi))
        if no_encounter:
            streak = stayed.no_encounter_streak + 1
            if streak >= NO_ENCOUNTER_DEBOUNCE_FRAMES:
                granted = replace(stayed, no_encounter_streak=0)
                return _b5_enter_plan_correction(granted, snapshot, peak)
            stayed = replace(stayed, no_encounter_streak=streak)
        elif stayed.no_encounter_streak:
            stayed = replace(stayed, no_encounter_streak=0)
        # ARRIVAL-QUALITY RE-CORRECTION (finding 16, twenty-third flight):
        # both altitude rounds executed to <1 m/s residual and the arrival
        # was STILL pe -31.8 km -- the blind altitude triggers cannot see
        # arrival quality. Once they are exhausted, a debounced sub-floor
        # PREDICTED arrival periapsis at the target body grants a bounded
        # extra round, while enough coast remains to fly it. Every term
        # fails closed: NaN next_pe / blank next_body / NaN tts never fire.
        arrival_bad = (not rounds_pending
                       and state.params.course_correct_periapsis > 0.0
                       and stayed.extra_rounds_done < MAX_ARRIVAL_EXTRA_ROUNDS
                       # Body domain: home for B5/B6 ((home,) == the pre-B7
                       # gate); home OR via for B7, so the heliocentric coast
                       # can grant the extra round too (next_body == target
                       # still gates the encounter).
                       and snapshot.body in _b5_warp_bodies(state.params)
                       and snapshot.node_count == 0
                       and snapshot.next_body == state.params.target_body
                       and _is_finite(snapshot.next_pe)
                       and snapshot.next_pe < state.params.target_periapsis_floor
                       and _is_finite(snapshot.time_to_soi)
                       and snapshot.time_to_soi > ARRIVAL_RECORRECT_MIN_TTS_SECONDS
                       # High-precision window (twenty-fourth flight): far-out
                       # extras moved the arrival only ~2-4 km each; the
                       # extras HOLD until the coast is inside the bound.
                       and snapshot.time_to_soi < ARRIVAL_RECORRECT_MAX_TTS_SECONDS)
        if arrival_bad:
            streak = stayed.arrival_bad_streak + 1
            if streak >= ARRIVAL_BAD_DEBOUNCE_FRAMES:
                granted = replace(stayed, arrival_bad_streak=0,
                                  extra_rounds_done=stayed.extra_rounds_done + 1)
                return _b5_enter_plan_correction(granted, snapshot, peak)
            stayed = replace(stayed, arrival_bad_streak=streak)
        elif stayed.arrival_bad_streak:
            stayed = replace(stayed, arrival_bad_streak=0)
        # Warp policy (Path A, docs/dev/research/native-warp-to-ut.md): the
        # NATIVE fire-and-forget warp_to_ut owns the long time-bound waits
        # (pending node, post-correction coast to the SOI boundary) -- the
        # game adapts the factor against its own live limits, table-free.
        # The rails distance stair stays for the correction-trigger altitude
        # approach (distance-based, live-proven) and as the fallback.
        if snapshot.body == "":
            # No reading this frame: HOLD (never cancel/re-command warp on a
            # transient blank), bounded by the blank-body dwell (SF-2).
            return _b5_hold_blank_body(stayed)
        if stayed.body_blank_count:
            stayed = replace(stayed, body_blank_count=0)
        stayed = _b5_clear_arrived_warp(stayed, snapshot)
        native_target: Optional[float] = None
        desired = 0
        if snapshot.node_count != 0:
            # (a) Pending node: NATIVE warp to node_ut minus the ARRIVAL
            # MARGIN (operator PR gate: nodeWarpLeadSeconds retired -- the
            # burn phase aims BEFORE warping, so no flip window is needed
            # here). 1x is allowed ONLY inside the margin, or on a NaN
            # node_ut (unknown UT = potentially inside the margin, fail
            # closed -- nothing ever warps past a burn on no evidence).
            if _is_finite(snapshot.node_ut) and _is_finite(snapshot.ut):
                tgt = snapshot.node_ut - state.params.node_arrival_margin
                if snapshot.ut < tgt:
                    native_target = tgt
        elif (rounds_pending and not state.params.correction_trigger_time_to_soi
                and _is_finite(snapshot.altitude)):
            # Correction-trigger approach, ALTITUDE mode (B5/B6): the
            # LIVE-PROVEN rails distance stair, FLOORED at factor 2 (operator
            # PR gate: the last metres before a trigger rode 1x; at 10x the
            # trigger overshoot is <= ~5 game-s per poll, and a trigger is a
            # refinement point, not a wall), with the SOI time bound and the
            # legality clamp.
            dist = triggers[state.correction_rounds_done] - snapshot.altitude
            desired = max(rails_factor_for_distance(
                dist, snapshot.vertical_speed, state.params.coast_warp_factor), 2)
            if desired > 0 and _is_finite(snapshot.time_to_soi):
                desired = min(desired, max(
                    rails_factor_for_time(snapshot.time_to_soi,
                                          state.params.coast_warp_factor),
                    state.params.flyby_warp_factor))
            if desired > 0:
                desired = min(desired, max_legal_rails_factor(
                    snapshot.body, snapshot.altitude))
        elif (rounds_pending and state.params.correction_trigger_time_to_soi
                and snapshot.body in state.params.via_bodies
                and _is_finite(snapshot.time_to_soi) and _is_finite(snapshot.ut)):
            # Correction-trigger approach, TIME mode (B7): approach the next
            # round's time-to-SOI threshold on the CURRENT native-first
            # policy. dt = time to the trigger; time_to_soi falls 1:1 with
            # UT, so the trigger UT is now + dt -- warping natively TO it is
            # inherently SOI-safe (the trigger precedes the boundary by
            # threshold > 0 game-s) and never passes the trigger un-polled
            # (arrival is followed by a poll, and the readiness gate fires at
            # tts <= threshold). Inside soi_lead of the trigger the rails
            # time stair takes over, floored at factor 2 (the altitude
            # mode's no-1x floor: a trigger is a refinement point, not a
            # wall; overshoot at 10x is <= ~5 game-s per poll), with the
            # same SOI time bound + legality clamp as the altitude stair.
            # Confined to via bodies: the home-SOI escape leg rides the SOI
            # native-warp branch below instead (warp to the home SOI exit).
            dt = (snapshot.time_to_soi
                  - state.params.correction_trigger_time_to_soi[state.correction_rounds_done])
            if dt > state.params.soi_lead:
                native_target = snapshot.ut + dt
            else:
                desired = max(rails_factor_for_time(
                    dt, state.params.coast_warp_factor), 2)
                desired = min(desired, max(
                    rails_factor_for_time(snapshot.time_to_soi,
                                          state.params.coast_warp_factor),
                    state.params.flyby_warp_factor))
                desired = min(desired, max_legal_rails_factor(
                    snapshot.body, snapshot.altitude))
        elif (_is_finite(snapshot.time_to_soi) and _is_finite(snapshot.ut)
                and snapshot.time_to_soi > state.params.soi_lead):
            # (b) Post-correction coast: NATIVE warp to the SOI boundary
            # minus soi_lead, so the machine regains 1x-poll control just
            # before the body change (never crosses inside a high-rate warp;
            # the old 10,000x poll overshoot class is gone). Re-issued only
            # when the SOI estimate shifts > WARP_RETARGET_THRESHOLD_SECONDS.
            native_target = snapshot.ut + snapshot.time_to_soi - state.params.soi_lead
        else:
            # No encounter (or inside the SOI lead window): held rails coast
            # factor with the legacy SOI time bound + legality clamp -- the
            # documented fallback when the native primitive has no target.
            desired = state.params.coast_warp_factor
            if desired > 0 and _is_finite(snapshot.time_to_soi):
                desired = min(desired, max(
                    rails_factor_for_time(snapshot.time_to_soi,
                                          state.params.coast_warp_factor),
                    state.params.flyby_warp_factor))
            if desired > 0:
                desired = min(desired, max_legal_rails_factor(
                    snapshot.body, snapshot.altitude))
        if native_target is not None:
            return _b5_native_warp(stayed, snapshot, native_target)
        if stayed.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
            # Rails intent while a native warp is (expected) active: CANCEL
            # first -- never two warp writers in the same frame (WarpTo wins
            # the fight within 1-2 frames; research doc scheduler analysis).
            # The rails command follows on the next poll.
            return _b5_cancel_native_warp(stayed, snapshot)
        actions: List[Action] = []
        # Emission discipline (_rails_emit_needed): on change, PLUS the
        # under-warp self-heal (fifteenth flight: manual changes / KSP drops
        # silently overrode the held factor), PLUS the over-warp pull-down
        # (review SF-1: the game rails-warping FASTER than desired -- incl.
        # desired 0 -- must be pulled back). Idempotent re-emission of the
        # same factor is harmless. Native-warp frames never reach here.
        if _rails_emit_needed(desired, state.warp_cmd, snapshot):
            actions.append(Action(ACTION_SET_RAILS_WARP, float(desired)))
            stayed = replace(stayed, warp_cmd=desired)
        return stayed, actions

    if state.phase == B5_TARGET_FLYBY:
        return_body = _b5_return_body(state.params)
        if snapshot.body == return_body:
            # The exit: back in the return body's SOI after the flyby (home
            # for the B5/B6 free-return, Sun for B7 -- a Duna flyby exits
            # heliocentric). Terminal (done, verdict None); the settle tail
            # runs at 1x in the exit SOI. Cancel an active native warp (which
            # zeroes the factors runner-side), else drop a held rails factor.
            entered = _b5_enter(state, B5_RETURN, snapshot.ut, peak)
            entered = replace(entered, warp_cmd=0, warp_to_cmd=None)
            if state.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
                return entered, [Action(ACTION_CANCEL_WARP)]
            return entered, ([Action(ACTION_SET_RAILS_WARP, 0.0)]
                             if state.warp_cmd != 0 else [])
        if snapshot.body not in ("", state.params.target_body, return_body):
            return replace(
                state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("flyby ejected the craft off-course: body=%r "
                             "(expected %r or exit %r)"
                             % (snapshot.body, state.params.target_body,
                                return_body))), []
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return stayed, []
        if snapshot.body == "":
            # No reading this frame: HOLD (never cancel/re-command warp on a
            # transient blank), bounded by the blank-body dwell (SF-2).
            return _b5_hold_blank_body(stayed)
        if stayed.body_blank_count:
            stayed = replace(stayed, body_blank_count=0)
        stayed = _b5_clear_arrived_warp(stayed, snapshot)
        # NEVER warp toward a known impact (finding 4's Flight Results wedge):
        # on a sub-surface periapsis at low altitude, the guard is
        # AUTHORITATIVE -- it CANCELS an active native warp and holds 1x so
        # the crash lands under live telemetry and the vessel-lost detectors
        # end the mission in seconds.
        impact_bound = (_is_finite(snapshot.periapsis) and snapshot.periapsis < 0.0
                        and _is_finite(snapshot.altitude)
                        and snapshot.altitude < IMPACT_WARP_GUARD_ALT)
        # IMPACT-CERTAIN EARLY TERMINAL (twenty-second flight): the guard
        # condition sustained for IMPACT_TERMINAL_DEBOUNCE_FRAMES means the
        # outcome is decided -- a sub-surface periapsis inside the target SOI
        # with no correction capability left ends in destruction regardless
        # -- so terminate ASSERT-FAIL now instead of riding the descent at 1x
        # to the physical crash (589 wall-seconds on the certification
        # flight; the audit's only 1x-coast violation). The first debounce
        # frames keep the guard's warp-cancel/1x-hold behavior, so a
        # transient periapsis mis-read costs five 1x polls, never a mission.
        if impact_bound:
            streak = stayed.impact_certain_streak + 1
            if streak >= IMPACT_TERMINAL_DEBOUNCE_FRAMES:
                terminal = replace(
                    stayed, peak_apoapsis=peak, done=True,
                    verdict=MISSION_ASSERT_FAIL,
                    loss_reason=("flyby impact certain: sub-surface periapsis "
                                 "%.0f m at altitude %.0f m for %d consecutive "
                                 "frames -- early terminal (not waiting for "
                                 "physical destruction)"
                                 % (snapshot.periapsis, snapshot.altitude,
                                    streak)))
                if (stayed.warp_to_cmd is not None
                        or _is_finite(snapshot.warping_to)):
                    return (replace(terminal, warp_to_cmd=None),
                            [Action(ACTION_CANCEL_WARP)])
                return terminal, ([Action(ACTION_SET_RAILS_WARP, 0.0)]
                                  if stayed.warp_cmd != 0 else [])
            stayed = replace(stayed, impact_certain_streak=streak)
        elif stayed.impact_certain_streak:
            stayed = replace(stayed, impact_certain_streak=0)
        native_target = None
        if impact_bound:
            desired = 0
        elif (_is_finite(snapshot.time_to_soi) and _is_finite(snapshot.ut)
                and snapshot.time_to_soi > state.params.soi_lead):
            # (c) Outer flyby legs: NATIVE warp to the SOI EXIT minus
            # soi_lead. The game's own altitude limits shape the passage
            # (e.g. Mun periapsis at 60 km runs at most 100x -- the proven
            # min-altitude evidence cadence -- while the outer legs run
            # 1000x+), table-free.
            native_target = snapshot.ut + snapshot.time_to_soi - state.params.soi_lead
        else:
            # Inside the exit lead window / no SOI estimate: the rails stair
            # fallback -- flyby factor floor near periapsis, stair toward
            # flybyMaxWarpFactor with the (altitude - periapsis) distance,
            # altitude-legality clamped. A NON-FINITE altitude forces 1x
            # (review N-A5, fail-closed symmetry: with no altitude reading
            # neither the stair distance nor the legality clamp is
            # trustworthy, and the impact guard above could not have armed).
            if not _is_finite(snapshot.altitude):
                desired = 0
            else:
                pe_ref = (max(snapshot.periapsis, 0.0)
                          if _is_finite(snapshot.periapsis) else 0.0)
                stair = rails_factor_for_distance(
                    snapshot.altitude - pe_ref, snapshot.vertical_speed,
                    state.params.flyby_max_warp_factor)
                desired = min(max(state.params.flyby_warp_factor, stair),
                              max_legal_rails_factor(snapshot.body,
                                                     snapshot.altitude))
        if native_target is not None:
            return _b5_native_warp(stayed, snapshot, native_target)
        if stayed.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
            # 1x/rails intent while a native warp is (expected) active --
            # including the impact guard's authoritative stop: CANCEL first,
            # rails (if any) follows next poll.
            return _b5_cancel_native_warp(stayed, snapshot)
        actions = []
        # Emission discipline (_rails_emit_needed): on change, PLUS the
        # under-warp self-heal (fifteenth flight: manual changes / KSP drops
        # silently overrode the held factor), PLUS the over-warp pull-down
        # (review SF-1: the game rails-warping FASTER than desired -- incl.
        # desired 0 -- must be pulled back). Idempotent re-emission of the
        # same factor is harmless. Native-warp frames never reach here.
        if _rails_emit_needed(desired, state.warp_cmd, snapshot):
            actions.append(Action(ACTION_SET_RAILS_WARP, float(desired)))
            stayed = replace(stayed, warp_cmd=desired)
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
      SAMPLING BAND CAVEAT (review N-A3): the evidence is polled ~every 50
      game-s at the 100x periapsis cadence, so a true periapsis BELOW the
      floor but ABOVE the terrain can slip between samples and still read as
      met -- the floor certifies the sampled track, not a continuous minimum.
    - ``returnedToHome``:      RETURN appears in ``phases_reached`` (the machine
      terminated back in the EXIT body's SOI after the flyby: the home body for
      the B5/B6 free-return, return_body -- Sun -- for B7; the reported value
      and the returnBody detail name the actual exit body, the assertion NAME
      is kept for result-schema stability).

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

    return_body = _b5_return_body(params)
    ret_met = B5_RETURN in phases
    # Name kept for schema/result-diff stability (design Q1); the value and
    # detail carry the actual EXIT body (home for B5/B6, Sun for B7), so a B7
    # row reads "returned to the exit body".
    ret = AssertionOutcome("returnedToHome", ret_met,
                           (return_body if ret_met else None),
                           {"required": B5_RETURN, "returnBody": return_body})

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

# TRANSPORT-layer drops only (socket dead, stream torn): the fly loop re-raises
# these immediately (a dead connection is the retryable-flake path, edges 5/8).
# Deliberately NARROWER than CONNECTION_DROP_EXCEPTION_NAMES: an RPCError-class
# failure means the server ANSWERED -- a vessel-state problem (e.g. the
# maneuver-nodes read failing on a just-destroyed vessel, seventh live B5
# flight 2026-07-22) -- which the fly loop TOLERATES so the control seam's
# read-fail streak can escalate to the honest vessel-lost terminal instead of
# killing the mission as MISSION-ERROR on the first raise.
TRANSPORT_DROP_EXCEPTION_NAMES = frozenset({
    "ConnectionError", "ConnectionResetError", "ConnectionAbortedError",
    "ConnectionRefusedError", "BrokenPipeError", "TimeoutError", "socket.timeout",
    "StreamError", "EOFError", "OSError",
})


def is_transport_drop_exception(exc_name: Optional[str]) -> bool:
    """True iff the exception NAME is a transport-layer connection drop the fly
    loop must re-raise immediately (vs a server-answered RPC failure it
    tolerates into the read-fail streak). Pure by name so mlib never imports
    krpc."""
    return str(exc_name or "") in TRANSPORT_DROP_EXCEPTION_NAMES


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


# ---------------------------------------------------------------------------
# Live observability helpers (design docs/dev/design-live-observability.md
# Phase 2). Pure: format/diff DECISION state so the fly loop can log it
# verbatim and the supervisor-side status CLI can read it without inference.
# All output is ASCII key=value tokens, decodable by status.py's generic
# parse_kv_tokens.
# ---------------------------------------------------------------------------

# (state attribute, log/JSON key) pairs for the machine-state line + status
# file. getattr-with-default keeps this generic over B1/B2/B4/B5 states:
# absent fields render as "-" (line) / are omitted (dict). burn_static_since
# is deliberately NOT here raw; it is rendered as the derived burnStaticAge
# (the AGE is the diagnostic quantity; the raw UT stamp is meaningless
# without the current UT).
MACHINE_STATE_FIELDS: Tuple[Tuple[str, str], ...] = (
    ("phase", "phase"),
    ("phase_entry_ut", "entryUt"),
    ("correction_rounds_done", "rounds"),
    ("plan_attempts", "planAttempts"),
    ("body_blank_count", "bodyBlank"),
    ("corr_burn_started", "corrBurnStarted"),
    ("aligned_streak", "alignedStreak"),
    ("min_node_dv", "minNodeDv"),
    ("warp_cmd", "warpCmd"),
    ("phys_warp_cmd", "physWarpCmd"),
    ("warp_to_cmd", "warpToCmd"),
    ("last_warp_issue_ut", "lastWarpIssueUt"),
    ("planned_node_count", "plannedNodes"),
    ("last_plan_ut", "lastPlanUt"),
    ("frozen_count", "frozenCount"),
    ("flameout_streak", "flameoutStreak"),
    ("flameout_stages_done", "flameoutStages"),
    ("impact_certain_streak", "impactStreak"),
    ("arrival_bad_streak", "arrivalBadStreak"),
    ("extra_rounds_done", "extraRounds"),
    ("no_encounter_streak", "noEncounterStreak"),
)

# Fields whose CHANGE is a sparse, decision-relevant gate/latch event worth
# one loud Info line (design 2b). Excluded as per-frame-noisy: phase (the
# transition line already logs it), phase_entry_ut / last_plan_ut /
# last_warp_issue_ut (stamps), min_node_dv (tracks every burn frame),
# frozen_count (the vessel-lost terminal is loud on its own). Included
# despite being counters: plan_attempts (one line per ~30 s re-plan cadence),
# aligned_streak (bounded by the debounce depth), body_blank_count (every
# blank-body frame IS an anomaly and the count is capped by
# frozen_sample_limit).
MACHINE_DIFF_FIELDS: Tuple[Tuple[str, str], ...] = (
    ("correction_rounds_done", "rounds"),
    ("plan_attempts", "planAttempts"),
    ("body_blank_count", "bodyBlank"),
    ("corr_burn_started", "corrBurnStarted"),
    ("aligned_streak", "alignedStreak"),
    ("warp_cmd", "warpCmd"),
    ("phys_warp_cmd", "physWarpCmd"),
    ("warp_to_cmd", "warpToCmd"),
    ("planned_node_count", "plannedNodes"),
    # Twenty-second flight additions, both bounded by their debounce depths:
    # a flameout-stage pop and the impact-certain countdown are exactly the
    # sparse decision events the gate lines exist for.
    ("flameout_streak", "flameoutStreak"),
    ("flameout_stages_done", "flameoutStages"),
    ("impact_certain_streak", "impactStreak"),
    # Finding 16 (arrival-quality re-correct): the sub-floor-arrival
    # countdown and the extra-round grant, bounded by the debounce depth
    # and MAX_ARRIVAL_EXTRA_ROUNDS.
    ("arrival_bad_streak", "arrivalBadStreak"),
    ("extra_rounds_done", "extraRounds"),
    ("no_encounter_streak", "noEncounterStreak"),
)

_MACHINE_FIELD_ABSENT = object()


def _obs_fmt(value) -> str:
    """Observability value formatting: None -> 'none', absent -> '-', floats
    3dp / 'nan', bools as True/False, everything else str."""
    if value is _MACHINE_FIELD_ABSENT:
        return "-"
    if value is None:
        return "none"
    if isinstance(value, bool):
        return str(value)
    if isinstance(value, float):
        if not math.isfinite(value):
            return "nan"
        return "%.3f" % value
    return str(value)


def _json_safe(value):
    """JSON-safe scalar: non-finite floats -> None (strict-parser friendly;
    json.dumps would otherwise emit bare NaN)."""
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def machine_state_dict(state, ut: float = float("nan")) -> Dict:
    """The machine's decision state as a JSON-safe {key: value} dict (the
    status-file ``machine`` block, design 2d). Fields the state object lacks
    are omitted; ``burnStaticAge`` is derived from ``burn_static_since`` and
    the current ``ut`` (None while not static or unknown)."""
    out: Dict = {}
    for attr, key in MACHINE_STATE_FIELDS:
        value = getattr(state, attr, _MACHINE_FIELD_ABSENT)
        if value is _MACHINE_FIELD_ABSENT:
            continue
        out[key] = _json_safe(value)
    since = getattr(state, "burn_static_since", None)
    if _is_finite(since) and _is_finite(ut):
        out["burnStaticAge"] = _json_safe(float(ut) - float(since))
    elif hasattr(state, "burn_static_since"):
        out["burnStaticAge"] = None
    return out


def format_machine_state(state, ut: float = float("nan")) -> str:
    """One rate-limited MACHINE-STATE log message (design 2a): the decision
    state verbatim, ``machine phase=... rounds=... planAttempts=...``. Works
    for any B-state via getattr (absent fields render '-'), so the fly loop
    emits it unconditionally."""
    parts = ["machine"]
    for attr, key in MACHINE_STATE_FIELDS:
        parts.append("%s=%s" % (key, _obs_fmt(getattr(state, attr,
                                                      _MACHINE_FIELD_ABSENT))))
    since = getattr(state, "burn_static_since", _MACHINE_FIELD_ABSENT)
    if since is _MACHINE_FIELD_ABSENT:
        age = _MACHINE_FIELD_ABSENT
    elif _is_finite(since) and _is_finite(ut):
        age = float(ut) - float(since)
    else:
        age = None
    parts.append("burnStaticAge=%s" % _obs_fmt(age))
    return " ".join(parts)


def diff_machine_state(prev, new) -> List[str]:
    """Sparse gate/latch flips between two machine states (design 2b): one
    ``key old->new`` string per MACHINE_DIFF_FIELDS change. Pure; the fly
    loop wraps each entry in a loud Info 'gate ...' line with the snapshot
    values that decided it. States lacking a field on BOTH sides contribute
    nothing; a field present on one side only is a change ('-' side)."""
    changes: List[str] = []
    for attr, key in MACHINE_DIFF_FIELDS:
        old = getattr(prev, attr, _MACHINE_FIELD_ABSENT)
        cur = getattr(new, attr, _MACHINE_FIELD_ABSENT)
        if old is _MACHINE_FIELD_ABSENT and cur is _MACHINE_FIELD_ABSENT:
            continue
        if _values_equal(old, cur):
            continue
        changes.append("%s %s->%s" % (key, _obs_fmt(old), _obs_fmt(cur)))
    return changes


def _values_equal(a, b) -> bool:
    """Equality that treats NaN == NaN (a NaN->NaN 'change' would spam)."""
    if isinstance(a, float) and isinstance(b, float):
        if math.isnan(a) and math.isnan(b):
            return True
    return a == b


def snapshot_dict(snapshot: TelemetrySnapshot) -> Dict:
    """The latest telemetry snapshot as a JSON-safe dict (the status-file
    ``snapshot`` block, design 2d). Non-finite floats -> None."""
    return {
        "ut": _json_safe(snapshot.ut),
        "altitude": _json_safe(snapshot.altitude),
        "verticalSpeed": _json_safe(snapshot.vertical_speed),
        "apoapsis": _json_safe(snapshot.apoapsis),
        "periapsis": _json_safe(snapshot.periapsis),
        "eccentricity": _json_safe(snapshot.eccentricity),
        "inclination": _json_safe(snapshot.inclination),
        "situation": snapshot.situation,
        "body": snapshot.body,
        "nodeCount": snapshot.node_count,
        "nodeDv": _json_safe(snapshot.node_dv),
        "nodeUt": _json_safe(snapshot.node_ut),
        "timeToSoi": _json_safe(snapshot.time_to_soi),
        "warpingTo": _json_safe(snapshot.warping_to),
        "liquidFuel": _json_safe(snapshot.liquid_fuel),
        "throttle": _json_safe(snapshot.throttle),
        "warpMode": snapshot.warp_mode,
        "warpRate": _json_safe(snapshot.warp_rate),
        "apError": _json_safe(snapshot.ap_error),
        "vesselLost": snapshot.vessel_lost,
    }


def format_snapshot_compact(snapshot: TelemetrySnapshot) -> str:
    """One-line-per-frame compact snapshot form for the event-window ring
    buffer (design 2c): the fields the machines gate on, ~100 chars."""
    return ("ut=%s alt=%s ap=%s pe=%s body=%s nodes=%d nodeDv=%s thr=%s "
            "apErr=%s warp=%sx%s sit=%s%s"
            % (_obs_fmt(snapshot.ut), _obs_fmt(snapshot.altitude),
               _obs_fmt(snapshot.apoapsis), _obs_fmt(snapshot.periapsis),
               snapshot.body or "?", snapshot.node_count,
               _obs_fmt(snapshot.node_dv), _obs_fmt(snapshot.throttle),
               _obs_fmt(snapshot.ap_error), snapshot.warp_mode,
               _obs_fmt(snapshot.warp_rate), snapshot.situation or "?",
               " LOST" if snapshot.vessel_lost else ""))
