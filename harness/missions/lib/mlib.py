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

# EVA-4 phase names (mission eva4_atmo_chute; scenario EVA-4-atmo-chute). The B1 hop
# shape up to DESCENT, but the terminal is EVA-WINDOW, NOT a landing: the mission's whole
# job is to FLY the pad craft into a verified-safe mid-air EVA envelope and then HAND OFF
# to the seam (EvaExit -> EvaChuteDeploy), because kRPC has no EVA API and a scenario may
# declare exactly ONE mission-kind step. So the craft is deliberately still airborne,
# crewed, and under its own canopy when the mission ends.
#
# ================= FLIGHT-1 EVIDENCE (2026-07-24), and what it changed =================
# The first live run ASSERT-FAILed exactly as designed, fast and self-explaining:
#   "eva-window-missed: altitude 702m fell below the window floor 800m (vspeed
#    -295.2m/s, situation FLYING, craftChute armed)".
# Measured profile (mission stdout telemetry, per-frame):
#   peak altitude 11,965 m at ut 60.6 (orbital apoapsis 19,879 m);
#   unchuted descent reaches TERMINAL -301 m/s by ~2,700 m and holds it;
#   the craft's chute was armed at 2,382 m / -301 m/s and 5.1 s later, at 855 m, the
#   rate had changed by 4.7 m/s - the canopy had NOT opened at all.
# The Parsek recording confirms it independently: the pod's .prec carries ZERO
# ParachuteSemiDeployed / ParachuteDeployed part events - only a Decoupled at ut 119.70
# (the breakup). ROOT CAUSE (decompiled ModuleParachute.cs:1255-1290 + the fixture's own
# persisted node): the ACTIVE -> SEMIDEPLOYED gate requires
# `automateSafeDeploy >= (int)deploymentSafeState`, and the fixture's parachuteSingle
# persists `automateSafeDeploy = 0` = deploy ONLY while SAFE. At ~300 m/s in dense air
# `DeploySafe` reads RISKY/UNSAFE, so an armed chute simply WAITS - and a craft at
# terminal velocity never slows on its own, so it waits forever. Arming low is not
# "late", it is INERT.
#   (Same evidence in the live-proven B1 log 2026-07-20: its parachuteSingle also has no
#    Parachute* part event and its recording ends at 65 m. B1 is green because its DOWN
#    terminal only needs the chute-COMMAND latch. Flagged separately; not EVA-4's to fix.)
#
# THREE consequences, all now encoded:
#   (a) ARM WHILE SLOW, not at an altitude. The machine arms on the first DESCENT frame
#       whose |vertical speed| is within craftChuteArmMaxRateMps - i.e. at the apoapsis
#       crossing, where DeploySafe is trivially SAFE and the 0.04 atm pressure gate is
#       already satisfied (Kerbin is ~0.2 atm at 12 km). Measured DESCENT-entry rates
#       were -7.4, -16.9, -26.1, -35.5 m/s, so a 30 m/s bound arms within ~3 frames.
#   (b) RAISE THE FULL-DEPLOY ALTITUDE. Stock full deploy triggers under the module's
#       own deployAltitude (1000 m in the fixture) and its animation is SLOW
#       (parachuteMk1.cfg deploymentSpeed = 0.12, so ~8 s). Leaving it at 1000 m would
#       force the EVA band under 1000 m with an unknown settle distance eating into it.
#       The machine sets deployAltitude (a stock PAW tweakable) to
#       craftChuteFullDeployAltMeters at the same moment it arms, so the craft reaches
#       its FULL-canopy terminal well above the band.
#   (c) GATE ON OBSERVED STATE, NOT ON THE COMMAND. The window now requires the craft's
#       chute to READ Deployed (kRPC ParachuteState), never merely "we called deploy".
#
# EVA-WINDOW opens on FIVE conjuncts (all read from the same frame):
#   1. the craft's chute READS Deployed - full canopy, observed, not commanded;
#   2. the situation is airborne (a landed craft is the EVA-1 ground case);
#   3. altitude <= evaWindowMaxAltMeters   (below the full-deploy altitude, so conjunct 1
#      can only become true inside/above the band, never above it by accident);
#   4. altitude >= evaWindowMinAltMeters   (sky left for the KERBAL's own canopy);
#   5. |vertical speed| <= evaMaxDescentRateMps  (the safety bound the kerbal leaves the
#      hatch into, and a cross-check that the observed canopy is actually doing work).
# Conjuncts 1 and 5 keep the window self-regulating: the handoff altitude is decided by
# where the physics actually settles the craft, not by a golden number.
#
# WINDOW-MISSED is the bounded, NAMED failure: the craft sank past evaWindowMinAltMeters
# without all five conjuncts ever holding. It is an ASSERT-FAIL (a deterministic mission
# failure), never a silent wait-out of the descent budget, and its reason string carries
# the OBSERVED chute state - so a repeat of the flight-1 failure mode reads
# "craftChute=Armed" and names itself.
EVA4_PRELAUNCH = "PRELAUNCH"
EVA4_ASCENT = "ASCENT"
EVA4_COAST = "COAST"
EVA4_DESCENT = "DESCENT"
EVA4_EVA_WINDOW = "EVA-WINDOW"
EVA4_PHASES: Tuple[str, ...] = (EVA4_PRELAUNCH, EVA4_ASCENT, EVA4_COAST, EVA4_DESCENT,
                                EVA4_EVA_WINDOW)

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

# FORGE phase names (mission forge_station: the FIXTURE-FORGE runner). A minimal
# two-phase shell that boots an EXISTING valid save (so LoadGame passes), launches
# the docking-variant craft onto the pad via launch_vessel, waits for the spawned
# vessel to settle PRELAUNCH, then exits MISSION-OK -- the post-mission SaveGame +
# FlushAndQuit seam steps persist the pad state, and the harvest tool normalizes
# it into the committed pre-placed-Station fixture. NOT a flight mission (no ascent,
# no orbit): it exists only to STAMP a pad fixture headlessly, replacing the
# operator fixture flight (2026-07-22 operator-principle override). It is generic
# over the craft (and optional named crew), so the same forge later produces the
# EVA-3 pad fixture (same Kerbal X craft, 3-crew pod) with a different missionParams.
FORGE_PRELAUNCH = "PRELAUNCH"
FORGE_LAUNCH = "LAUNCH"
FORGE_SETTLED = "SETTLED"
FORGE_PHASES: Tuple[str, ...] = (FORGE_PRELAUNCH, FORGE_LAUNCH, FORGE_SETTLED)

# FORGE-LKO phase names (mission forge_lko: the ORBITAL fixture forge that stamps
# the EVA-2 crewed-LKO fixture). The pad forge above ends on the pad; this one
# flies the LIVE-PROVEN B-DOCK Interceptor-leg shape -- launch_vessel WITH NAMED
# CREW from a clear pad, the B2 MechJeb ascent, circularize, the two-step
# separation contract (drop the spent core AND ignite the orbital stage), then a
# stabilized park dwell -- so the SaveGame that follows persists a crewed ORBITAL
# STAGE on a stable, non-tumbling, on-rails-safe orbit. NO rendezvous / dock: the
# forge produces a START state, never a trajectory.
FLKO_PRELAUNCH = "PRELAUNCH"
FLKO_LAUNCH = "LAUNCH"
FLKO_ASCENT = "ASCENT"
FLKO_CIRCULARIZE = "CIRCULARIZE"
FLKO_SEPARATE = "SEPARATE"
FLKO_PARK = "PARK"
FLKO_ORBIT = "ORBIT"
FLKO_PHASES: Tuple[str, ...] = (
    FLKO_PRELAUNCH, FLKO_LAUNCH, FLKO_ASCENT, FLKO_CIRCULARIZE, FLKO_SEPARATE,
    FLKO_PARK, FLKO_ORBIT)

# B-DOCK phase names (mission bdock_dock_transfer: design section 3.3). The FIRST
# two-vessel Parsek autotest: a pre-placed Station flies the B2 ascent to a ~110 km
# park and is COMMITTED as its own tree (mid-mission command-seam CommitTree,
# route 1), then the SAME craft launches again as an Interceptor into a ~90 km
# phasing orbit, MechJeb rendezvous closes, MechJeb docking hard-docks, two kRPC
# ResourceTransfers move fuel both ways, and an undock splits the pair. Survival is
# the contract (any vessel-lost / frozen terminal is an ASSERT-FAIL loss); a
# rendezvous / docking / transfer stall is a bounded give-up FLAKE (section 5.3),
# never a PARSEK-FAIL.
BDOCK_PRELAUNCH = "PRELAUNCH"
BDOCK_STATION_ASCENT = "STATION-ASCENT"
BDOCK_STATION_CIRCULARIZE = "STATION-CIRCULARIZE"
# Post-circularize stage separation (flight-3 lesson, 2026-07-24): the spent
# core never autostages off (MechJeb autostage only fires on EMPTY stages and the
# Kerbal X core keeps residual fuel), so docking a ~20 t full stack on pod RCS is
# broken. Each vehicle must be its ORBITAL STAGE ONLY before rendezvous -- exactly
# one stage activation after circularize, verified by a NEW-vessel (spent core)
# spawn, never a second activation (the OTHER stack decoupler jettisons the pod's
# heat shield). See design section 3.3 (amended) + the mission-profile step list
# in BDOCK-1-station-interceptor.toml.
BDOCK_STATION_SEPARATE = "STATION-SEPARATE"
BDOCK_STATION_ORBIT = "STATION-ORBIT"
BDOCK_STATION_COMMIT = "STATION-COMMIT"
BDOCK_INT_LAUNCH = "INT-LAUNCH"
BDOCK_INT_ASCENT = "INT-ASCENT"
BDOCK_INT_CIRCULARIZE = "INT-CIRCULARIZE"
BDOCK_INT_SEPARATE = "INT-SEPARATE"
BDOCK_INT_PHASING_ORBIT = "INT-PHASING-ORBIT"
BDOCK_SET_TARGET = "SET-TARGET"
BDOCK_RENDEZVOUS = "RENDEZVOUS"
BDOCK_MATCH_VELOCITY = "MATCH-VELOCITY"
BDOCK_DOCK = "DOCK"
BDOCK_TRANSFER = "TRANSFER"
BDOCK_UNDOCK = "UNDOCK"
BDOCK_TERMINAL = "TERMINAL"
BDOCK_PHASES: Tuple[str, ...] = (
    BDOCK_PRELAUNCH, BDOCK_STATION_ASCENT, BDOCK_STATION_CIRCULARIZE,
    BDOCK_STATION_SEPARATE, BDOCK_STATION_ORBIT, BDOCK_STATION_COMMIT,
    BDOCK_INT_LAUNCH, BDOCK_INT_ASCENT, BDOCK_INT_CIRCULARIZE,
    BDOCK_INT_SEPARATE, BDOCK_INT_PHASING_ORBIT, BDOCK_SET_TARGET,
    BDOCK_RENDEZVOUS, BDOCK_MATCH_VELOCITY, BDOCK_DOCK, BDOCK_TRANSFER,
    BDOCK_UNDOCK, BDOCK_TERMINAL)

# Docking-port state tokens (kRPC v0.5.4 DockingPortState.name; the runner
# normalizes to these exact spellings). "Docked" is the DOCK-done evidence; a
# post-undock state that is anything OTHER than "Docked" is the undock evidence
# (with the vessel-count increase; MINOR 10 -- "Ready" alone is only soft
# evidence because the port lingers "Undocking" while the halves are inside
# ReengageDistance).
DOCKING_STATE_DOCKED = "Docked"
DOCKING_STATE_DOCKING = "Docking"
DOCKING_STATE_READY = "Ready"


def pick_ready_port_index(state_names) -> "Optional[int]":
    """Pick the docking port to TARGET from a sequence of live DockingPort state
    names (kRPC lower_snake spellings: 'ready' / 'docked' / 'docking' / ...): the
    FIRST free 'ready' port, else the first port, else None (no ports). Pure -- the
    runner reads the live states off the target vessel and applies this (flight-13:
    the pre-reload captured port handle is a destroyed Part server-side; SetVessel
    Target on it silently clears the target, so the port must be resolved LIVE)."""
    names = list(state_names or [])
    if not names:
        return None
    for i, nm in enumerate(names):
        if str(nm).strip().lower() == "ready":
            return i
    return 0


def normalize_docking_state(name) -> str:
    """Normalize a kRPC DockingPortState.name (lower_snake, e.g. 'docked',
    'pre_attached') to the PascalCase spelling the machine gates on ('Docked'),
    or "" for an empty/None read (fail-closed: matches no gate)."""
    if not name:
        return ""
    return "".join(seg.capitalize() for seg in str(name).split("_"))


# The stock parachute states the EVA-4 machine gates on, spelled as the PascalCase
# normalization of kRPC's ParachuteState enum (decompiled
# KRPC.SpaceCenter.Services.Parts.ParachuteState: Stowed / Armed / SemiDeployed /
# Deployed / Cut). "Armed" is kRPC's name for stock's ACTIVE - commanded but NOT open,
# which is exactly the state EVA-4's first live flight got stuck in.
CHUTE_STATE_STOWED = "Stowed"
CHUTE_STATE_ARMED = "Armed"
CHUTE_STATE_SEMI_DEPLOYED = "SemiDeployed"
CHUTE_STATE_DEPLOYED = "Deployed"
CHUTE_STATE_CUT = "Cut"


def normalize_parachute_state(name) -> str:
    """Normalize a kRPC ParachuteState.name (lower_snake, e.g. 'semi_deployed') to the
    PascalCase spelling the machine gates on ('SemiDeployed'), or "" for an
    empty/None read (fail-closed: matches no gate, so an unreadable chute can never
    satisfy the EVA window)."""
    if not name:
        return ""
    return "".join(seg.capitalize() for seg in str(name).split("_"))

# Resource-transfer direction codes carried in Action.limit (section 5.1: the
# Action dataclass is kind/value/text/limit, so the transfer direction rides
# limit as a float code). 0 = deliver transport -> station (the LiquidFuel leg);
# 1 = pickup station -> transport (the MonoPropellant leg).
TRANSFER_DIR_DELIVER = 0.0   # transport tank -> station tank
TRANSFER_DIR_PICKUP = 1.0    # station tank -> transport tank

# Connect-retry decision tokens (design "Connection lifecycle" step 2).
CONNECT_RETRY = "RETRY"
CONNECT_TIMEOUT = "TIMEOUT"

# Action kinds the phase machines emit for the shell to execute (raw kRPC for
# B1, KRPC.MechJeb for B2). The shell maps a kind to the actual RPC call.
ACTION_SET_THROTTLE = "set_throttle"          # value = throttle fraction
ACTION_CUT_THROTTLE = "cut_throttle"          # value = 0.0
ACTION_ACTIVATE_STAGE = "activate_stage"      # value = None
ACTION_DEPLOY_CHUTE = "deploy_chute"          # value = None
# Set the stock full-deploy altitude (metres) on every parachute of the active vessel
# (kRPC Parachute.DeployAltitude, a stock tweakable a player edits in the PAW). EVA-4
# raises it so the craft reaches its FULL-canopy terminal rate well above the ground,
# which is what gives the mid-air EVA window room to open and the kerbal sky to use.
ACTION_SET_CHUTE_DEPLOY_ALTITUDE = "set_chute_deploy_altitude"   # value = metres
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

# ---------------------------------------------------------------------------
# FORGE + B-DOCK actions (design section 5.1). The runner owns kRPC OBJECT
# HANDLES for target / transfer / undock selection (P9: kRPC v0.5.4 Vessel
# exposes no pid/guid, both vessels are literally named "Kerbal X", and ghost
# ProtoVessels can inject same-named map entries -- so name/pid selection is
# FORBIDDEN in the driver; the machine emits an intent action and the runner
# resolves it against the handle it captured while the object was reachable).
# ---------------------------------------------------------------------------
# Launch a fresh vessel from the save's Ships/VAB onto the pad (kRPC
# SpaceCenter.launch_vessel("VAB", <name>, <launch_site>, crew=<names>)). text =
# the craft name ("Kerbal X"); launch_site = the pad/runway name (None -> the
# runner defaults "LaunchPad"); crew = an explicit tuple of KERBAL NAMES (None /
# empty -> crew=[] = KSP's default manifest). Crew is by NAME, never a count:
# kRPC 0.5.4 has no roster-enumeration API. Used by BOTH the FORGE (piece-1
# stamp) and B-DOCK's Interceptor (piece 2). Exercises D1 auto-record-launch on
# the StartWithNewLaunch path.
ACTION_LAUNCH_VESSEL = "launch_vessel"                     # text = craft name
# Capture the CURRENT active vessel + its top docking port as "the Station"
# handle (STATION-COMMIT, while the Station is the active vessel -- P9 / Q4).
# The runner stores the two handles for the later SET_TARGET_VESSEL /
# SET_TARGET_DOCKING_PORT / UNDOCK actions; name/pid is never used.
ACTION_CAPTURE_STATION = "capture_station"                 # value = None
# Mid-mission Parsek command-seam CommitTree (route 1, section 3.2): the runner
# writes a CommitTree command with the reserved command-id into the seam's
# request channel, then polls the response channel under a BOUNDED wait. The
# outcome is fed back into telemetry (TelemetrySnapshot.seam_commit_result:
# "OK" advances the machine, "ERROR"/"TIMEOUT" flakes it -- driver-INVALID,
# retryable, never PARSEK-FAIL). When the runner has no seam config (any
# non-B-DOCK mission never emits this), the action is a logged no-op.
ACTION_PARSEK_COMMIT_TREE = "parsek_commit_tree"           # value = None
# Set the game target to the captured Station handle (kRPC sc.target_vessel =
# <station handle>). Drives BOTH KSP's own target and MechJeb's rendezvous /
# docking target controller (section 4.1).
ACTION_SET_TARGET_VESSEL = "set_target_vessel"             # value = None
# Set the target to the captured Station Clamp-O-Tron handle (kRPC
# sc.target_docking_port = <station port handle>).
ACTION_SET_TARGET_DOCKING_PORT = "set_target_docking_port" # value = None
# Enable MechJeb's rendezvous autopilot (value = desired approach distance m;
# limit = max phasing orbits). Done evidence is the Enabled LATCH flipping
# False (the AP self-disables when finished, NIT-15) AND target_distance
# <= the desired distance.
ACTION_MJ_ENABLE_RENDEZVOUS = "mj_enable_rendezvous"       # value = distance m
# Kill relative velocity to the target (MechJeb maneuver_planner
# operation_kill_rel_vel, OR rely on the rendezvous AP's own terminal match).
# The runner clears any stale node then retargets the op to XFromNow + ~15 s lead
# (flight-5: the default closest-approach selector landed the node ~an orbit
# ahead, stalling MATCH-VELOCITY until the wall).
ACTION_MJ_KILL_REL_VEL = "mj_kill_rel_vel"                 # value = None
# Enable MechJeb's docking autopilot (value = approach speed_limit m/s -- the
# monoprop-budget knob, P2). Done evidence is docking_state == "Docked" AND the
# Enabled latch flipping False.
ACTION_MJ_ENABLE_DOCKING = "mj_enable_docking"             # value = speed m/s
# Disable the docking autopilot (give-up cleanup: stall / monoprop-out / bounce).
ACTION_MJ_DISABLE_DOCKING = "mj_disable_docking"           # value = None
# Abort any pending MechJeb node execution + clear the maneuver nodes (runner:
# node_executor.abort() + control.remove_nodes()). Emitted FIRST at DOCK entry so
# no pending kill-rel-vel node / autowarp executor survives into terminal approach
# (flight-8 prox-ops rule: a pending node rails-warped at ~92 m, packing cleared
# the docking-port target, and the docking AP NRE'd forever). Best-effort.
ACTION_MJ_ABORT_NODE_EXEC = "mj_abort_node_exec"           # value = None
# Attitude control (flight-10 tumble fix). SET_SAS: control.sas = True, then try
# control.sas_mode = stability_assist (separate try/except). SET_RCS: control.rcs
# = (value != 0). Emitted after each SEPARATE (separation torque with no SAS = the
# tumble the operator watched) and at DOCK entry (hand the AP a stabilized ship).
ACTION_SET_SAS = "set_sas"                                 # value = None
ACTION_SET_RCS = "set_rcs"                                 # value = 1.0 on / 0.0 off
# Start a kRPC ResourceTransfer between the captured transport / station tanks
# (text = resource name, value = amount, limit = direction code
# TRANSFER_DIR_DELIVER / TRANSFER_DIR_PICKUP). The runner resolves the from/to
# part handles by resource + the pre-dock part-set split (Q4) and polls the
# ResourceTransfer.complete flag; the outcome rides
# TelemetrySnapshot.transfer_complete / transfer_amount.
ACTION_START_RESOURCE_TRANSFER = "start_resource_transfer" # text = resource
# Undock the captured Station Clamp-O-Tron (kRPC port.undock()). KSP fires
# onVesselsUndocking -> Parsek authors the Undock split branch + completes the
# RouteConnectionWindow. Done evidence: vessel_count INCREASED by one AND
# docking_state != "Docked" (MINOR 10).
ACTION_UNDOCK = "undock"                                   # value = None

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
    # --- B-DOCK docking / rendezvous / transfer telemetry (design section 5.2).
    # Every field defaults to a FAIL-CLOSED sentinel so a runner that forgets to
    # populate one fails the gate rather than faking a satisfied condition (the
    # same SF-2 discipline as ap_error / body). The B2/B5/B7 machines never read
    # any of these, so the pre-B-DOCK suites are unaffected.
    # MechJeb target_controller.distance (m to target); NaN fails the
    # RENDEZVOUS-done gate closed.
    target_distance: float = float("nan")
    # norm(MechJeb target_controller.relative_velocity) (m/s); NaN fails the
    # MATCH-VELOCITY gate closed.
    target_rel_speed: float = float("nan")
    # The active/target docking port state.name (kRPC DockingPortState); ""
    # matches no gate (fail closed). "Docked" is the DOCK-done evidence.
    docking_state: str = ""
    # MechJeb target_controller.normal_target_exists: a vessel/port target is set.
    target_set: bool = False
    # Carried Enabled-latch evidence (NIT-15): the rendezvous / docking AP
    # self-disables when finished, so DONE = the latch flips False. The runner
    # reports the CURRENT enabled state; the machine tracks the latch.
    mj_rendezvous_enabled: bool = False
    mj_docking_enabled: bool = False
    # len(sc.vessels): the UNDOCK split gate reads its INCREASE (a split raises
    # the count), load-bearing with docking_state != "Docked" (MINOR 10). 0 =
    # unread (fail closed: no increase can be measured against a 0 baseline that
    # never advanced).
    vessel_count: int = 0
    # The active ResourceTransfer poll (the runner owns the handle): complete
    # flag + amount transferred so far. transfer_amount NaN = unread (fail
    # closed for the transfer-stall no-progress detector).
    transfer_complete: bool = False
    transfer_amount: float = float("nan")
    # Vessel-total MonoPropellant (the P2 monoprop-budget channel): the docking
    # give-up flakes monoprop-out when this hits ~0 while not yet Docked. NaN =
    # unread (fail closed: the RCS-out give-up never fires on a missing reading).
    monopropellant: float = float("nan")
    # Mid-mission command-seam CommitTree outcome (route 1, section 3.2), fed
    # back from ACTION_PARSEK_COMMIT_TREE's bounded poll: "" = not issued / still
    # waiting (fail closed -- STATION-COMMIT stays until a terminal token or its
    # phase budget), "OK" advances the machine, "ERROR"/"TIMEOUT" flakes it.
    seam_commit_result: str = ""
    # --- Prox-ops observability (flight-10 operator directive: DOCK was blind).
    # angular_velocity magnitude (rad/s) in the orbital frame: THE tumble signal
    # (a stabilized ship reads ~0, a tumble reads high). NaN = unread (fail closed:
    # never counts as "tumble killed" progress in the DOCK watchdog).
    angular_velocity: float = float("nan")
    # control.sas / control.rcs live readbacks (fail closed False: an unread state
    # is treated as OFF so a diagnostic never claims a stabilized ship it cannot
    # confirm). Diagnosability channels; no gate reads them.
    sas_enabled: bool = False
    rcs_enabled: bool = False
    # MechJeb docking_autopilot.status string (KRPC.MechJeb DockingAutopilot.Status),
    # truncated ~60 chars; "" = unread. What the AP thinks it is doing -- the
    # missing signal the operator called out. Diagnosability only.
    docking_ap_status: str = ""
    # kRPC Vessel.CrewCount: kerbals aboard the ACTIVE vessel. -1 = UNREAD (the
    # fail-closed sentinel, same discipline as ap_error / vessel_count): a runner
    # that does not opt into the crew read, or whose read faults, can never
    # satisfy a "crew aboard" gate with a fabricated 0-or-more. Read only when
    # the control was built with read_crew=True (the FORGE-LKO crewed-fixture
    # forge), so every pre-existing mission's snapshot is byte-identical.
    crew_count: int = -1
    # The active vessel's aggregate stock-parachute state, normalized to PascalCase
    # (mlib.normalize_parachute_state over kRPC ParachuteState.name). "" = UNREAD, the
    # fail-closed sentinel: it matches no gate, so a runner that does not opt into the
    # chute read (every mission but EVA-4) can never satisfy a chute conjunct with a
    # fabricated value. THE lesson of the EVA-4 first flight: "the machine COMMANDED
    # the chute" is not evidence the canopy opened - only this read is.
    craft_chute_state: str = ""


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
    the runner disqualifies an oversized correction plan against).
    ``launch_site`` and ``crew`` are the two ACTION_LAUNCH_VESSEL payloads: the
    kRPC ``launch_site`` name (None -> the runner defaults ``"LaunchPad"``) and an
    explicit tuple of KERBAL NAMES to seed the pod (None / empty -> the runner
    passes ``crew=[]`` = KSP's default crew assignments). kRPC 0.5.4 exposes no
    roster-enumeration API (only ``get_kerbal(name)`` + ``launch_vessel(crew:
    List[str])``), so the crew contract is by NAME, never by count."""
    kind: str
    value: Optional[float] = None
    text: Optional[str] = None
    limit: Optional[float] = None
    launch_site: Optional[str] = None
    crew: Optional[Tuple[str, ...]] = None


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


@dataclass(frozen=True)
class Eva4Params:
    """EVA-4 atmospheric-chute tuning (spec [driver.missionParams] for
    eva4_atmo_chute). Every value is a WINDOW / threshold / budget, never a golden
    trajectory: the mission's terminal is "the craft is inside a verified-safe
    mid-air EVA envelope", and the envelope is expressed as bounds the physics has
    to satisfy, not as an altitude the flight is steered to."""
    throttle: float
    # ARM WHILE SLOW (flight-1 fix a): arm the craft's chute on the first DESCENT frame
    # whose |vertical speed| is within this bound, i.e. at the apoapsis crossing where
    # DeploySafe is trivially SAFE. NOT an altitude - arming at an altitude is what
    # produced the flight-1 inert-armed-chute failure.
    craft_chute_arm_max_rate: float
    # The stock deployAltitude (m) the machine SETS on the craft's chutes when it arms
    # them (flight-1 fix b), so the full canopy exists well above the EVA band.
    craft_chute_full_deploy_alt: float
    eva_window_max_alt: float              # window ceiling: sky left for the kerbal
    eva_window_min_alt: float              # window floor: below this = WINDOW-MISSED
    eva_max_descent_rate: float            # |vertical speed| bound at the hatch
    ascent_timeout: float
    coast_timeout: float
    descent_timeout: float
    apoapsis_window: Tuple[float, float]   # (min, max), inclusive - hop sanity only
    frozen_sample_limit: int = 10
    # The situations that keep the EVA window legitimate. A craft that has already
    # LANDED is the EVA-1 ground case, not this scenario's mid-flight surface, so the
    # window requires an airborne situation.
    airborne_situations: Tuple[str, ...] = ("FLYING", "SUB_ORBITAL")


def eva4_params_from_dict(params: Dict) -> Eva4Params:
    """Build ``Eva4Params`` from a spec ``missionParams`` dict. Tolerant of int/float;
    the apoapsis window is the ``{min, max}`` sub-table like B1's."""
    params = params or {}
    window = params.get("apoapsisWindowMeters", {}) or {}
    return Eva4Params(
        throttle=float(params.get("throttle", 1.0)),
        craft_chute_arm_max_rate=float(params.get("craftChuteArmMaxRateMps", 30)),
        craft_chute_full_deploy_alt=float(params.get("craftChuteFullDeployAltMeters", 2500)),
        eva_window_max_alt=float(params.get("evaWindowMaxAltMeters", 2100)),
        eva_window_min_alt=float(params.get("evaWindowMinAltMeters", 700)),
        eva_max_descent_rate=float(params.get("evaMaxDescentRateMps", 25)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 90)),
        coast_timeout=float(params.get("coastTimeoutSeconds", 180)),
        descent_timeout=float(params.get("descentTimeoutSeconds", 480)),
        apoapsis_window=(float(window.get("min", 0.0)), float(window.get("max", 0.0))),
        frozen_sample_limit=int(params.get("frozenTelemetrySamples", 10)),
        airborne_situations=tuple(params.get("airborneSituations", ("FLYING", "SUB_ORBITAL"))),
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
# EVA-4 phase state machine (mission eva4_atmo_chute). Pure.
#
# Deliberately a SIBLING of the B1 machine rather than a parameterisation of it: B1's
# terminal is the craft on the ground (LANDED / DOWN) and it must stay exactly that (it
# is live-proven and other scenarios depend on its shape). EVA-4 needs the OPPOSITE
# terminal - the craft still ALIVE and AIRBORNE at handoff - so forcing both into one
# machine would make B1's proven contract a special case of an unproven one.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Eva4State:
    """EVA-4 machine state. ``verdict`` is None while running; MISSION-FLAKE on a
    per-phase budget overrun (``flake_phase`` names the stuck phase); MISSION-ASSERT-FAIL
    with a ``loss_reason`` on a vessel loss or a missed EVA window. ``done`` is True at
    EVA-WINDOW (the success terminal) or on any of those."""
    params: Eva4Params
    phase: str = EVA4_PRELAUNCH
    phase_entry_ut: float = 0.0
    peak_apoapsis: Optional[float] = None
    # COMMANDED latch (the arm action was emitted). Deliberately NOT a window conjunct
    # any more: flight-1 proved a commanded chute can sit inert in ARMED forever.
    chute_armed: bool = False
    # The altitude / rate the arm was emitted at, carried as evidence into the result.
    chute_armed_altitude: Optional[float] = None
    chute_armed_rate: Optional[float] = None
    # OBSERVED latch: the craft's chute has READ Deployed at least once.
    craft_chute_full_seen: bool = False
    phases_reached: Tuple[str, ...] = (EVA4_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    done: bool = False
    frozen_sig: Optional[FrozenSignature] = None
    frozen_count: int = 0
    loss_reason: Optional[str] = None
    last_finite_altitude: Optional[float] = None
    # The frame the window opened, carried as EVIDENCE into the mission result so the
    # operator can read WHERE the handoff happened without re-deriving it from frames.
    eva_window_altitude: Optional[float] = None
    eva_window_vertical_speed: Optional[float] = None
    # The mission's terminal hands a LIVE, AIRBORNE, CREWED craft to the seam, and the
    # seam's next command (EvaExit) is time-critical: the craft keeps sinking during the
    # handoff. So the settle tail is skipped - the runner already honours this flag.
    skip_settle_tail: bool = False


def eva4_initial_state(params: Eva4Params) -> Eva4State:
    """Fresh EVA-4 machine at PRELAUNCH."""
    return Eva4State(params=params)


def _eva4_phase_budget(params: Eva4Params, phase: str) -> Optional[float]:
    """The bounded budget for a timed EVA-4 phase, or None for the untimed PRELAUNCH /
    terminal EVA-WINDOW phases (design "Every wait bounded")."""
    if phase == EVA4_ASCENT:
        return params.ascent_timeout
    if phase == EVA4_COAST:
        return params.coast_timeout
    if phase == EVA4_DESCENT:
        return params.descent_timeout
    return None


def _eva4_over_budget(state: Eva4State, snapshot: TelemetrySnapshot) -> bool:
    budget = _eva4_phase_budget(state.params, state.phase)
    if budget is None:
        return False
    if not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > budget


def eva4_window_open(params: Eva4Params, snapshot: TelemetrySnapshot) -> bool:
    """True iff THIS frame satisfies every EVA-window conjunct (see the EVA4_* phase
    comment). Pure and separately testable because it is the single decision the whole
    mission exists to make - the seam's IRREVERSIBLE EvaExit fires on its say-so.

    The first conjunct is the FLIGHT-1 LESSON: it reads the craft's OBSERVED parachute
    state, never the machine's own "we commanded it" latch. Flight 1 armed the chute and
    the canopy never opened (stock refuses ACTIVE -> SEMIDEPLOYED while
    `automateSafeDeploy = 0` and DeploySafe reads unsafe at ~300 m/s), yet the commanded
    latch was true the whole time - so the old conjunct was satisfied by a chute that did
    not exist.

    Fail-closed on every unreadable field: a blank chute state (the "" unread sentinel), a
    non-finite altitude or vertical speed, or a situation outside ``airborne_situations``
    all keep the window SHUT."""
    if snapshot.craft_chute_state != CHUTE_STATE_DEPLOYED:
        return False
    if snapshot.situation not in params.airborne_situations:
        return False
    if not _is_finite(snapshot.altitude) or not _is_finite(snapshot.vertical_speed):
        return False
    if snapshot.altitude > params.eva_window_max_alt:
        return False
    if snapshot.altitude < params.eva_window_min_alt:
        return False
    return abs(snapshot.vertical_speed) <= params.eva_max_descent_rate


def eva4_decide(state: Eva4State, snapshot: TelemetrySnapshot) -> Tuple[Eva4State, List[Action]]:
    """Advance the EVA-4 machine one frame; return (new_state, actions).

    Transitions:
      - PRELAUNCH -> ASCENT: set throttle + activate the next stage (ignite the SRB).
      - ASCENT -> COAST: active-stage solid fuel exhausted; cut throttle.
      - COAST -> DESCENT: past apoapsis (vertical speed negative).
      - DESCENT: on the first frame whose |vertical speed| is within
        craftChuteArmMaxRateMps (the apoapsis crossing), RAISE the craft chutes'
        full-deploy altitude and ARM them. Arming while SLOW is the flight-1 fix -
        arming at an ALTITUDE, once the craft is already at terminal velocity, produces
        a chute that sits inert in ARMED forever (stock refuses ACTIVE -> SEMIDEPLOYED
        while automateSafeDeploy = 0 and DeploySafe reads unsafe). Then
        DESCENT -> EVA-WINDOW the first frame ``eva4_window_open`` holds. That is the
        SUCCESS terminal: the craft is airborne, crewed, and under an OBSERVED full
        canopy, and the seam takes over.
      - DESCENT -> WINDOW-MISSED (ASSERT-FAIL): the craft sank below
        evaWindowMinAltMeters with the window never having opened. Bounded and NAMED,
        and the reason carries the OBSERVED chute state, so a repeat of the flight-1
        inert-armed-chute failure reds as "craftChute=Armed" and names itself instead of
        burning the descent budget and flaking.
    A vessel loss (runner-signalled or frozen telemetry) in ANY phase is an ASSERT-FAIL:
    unlike B1 there is no chute-deployed-impact success terminal here, because the craft
    reaching the ground at all means the EVA never happened.
    """
    if state.done:
        return state, []

    peak = _update_peak(state.peak_apoapsis, snapshot.apoapsis)

    if not snapshot.vessel_lost and _is_finite(snapshot.altitude):
        state = replace(state, last_finite_altitude=snapshot.altitude)

    if snapshot.vessel_lost:
        return replace(
            state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason=_eva4_loss_reason_with_altitude(
                state, "vessel-lost (unreadable after repeated telemetry failures) "
                       "before the EVA window opened")), []

    if state.phase in (EVA4_ASCENT, EVA4_COAST, EVA4_DESCENT):
        limit = state.params.frozen_sample_limit
        new_sig, new_count, tripped = _advance_frozen_count(
            state.frozen_sig, state.frozen_count, snapshot, limit)
        if tripped:
            return replace(
                state, peak_apoapsis=peak, frozen_sig=new_sig, frozen_count=new_count,
                done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=_eva4_loss_reason_with_altitude(
                    state, "vessel-lost (telemetry frozen %d consecutive samples while "
                           "airborne; vessel presumed destroyed)" % limit)), []
        state = replace(state, frozen_sig=new_sig, frozen_count=new_count)

    if state.phase == EVA4_PRELAUNCH:
        actions = [Action(ACTION_SET_THROTTLE, state.params.throttle),
                   Action(ACTION_ACTIVATE_STAGE)]
        return _eva4_enter(state, EVA4_ASCENT, snapshot.ut, peak), actions

    if state.phase == EVA4_ASCENT:
        if _is_finite(snapshot.stage_solid_fuel) and snapshot.stage_solid_fuel <= FUEL_EXHAUSTED_EPS:
            return (_eva4_enter(state, EVA4_COAST, snapshot.ut, peak),
                    [Action(ACTION_CUT_THROTTLE, 0.0)])
        return _eva4_stay_or_flake(state, snapshot, peak), []

    if state.phase == EVA4_COAST:
        if _is_finite(snapshot.vertical_speed) and snapshot.vertical_speed < 0.0:
            # Enter DESCENT and FALL THROUGH into its body on the SAME frame (no early
            # return). The arm decision below is RATE-gated and the rate only ever
            # worsens - Kerbin adds ~10 m/s of fall per ~1 s poll (measured flight-1
            # DESCENT entry: -7.4, -16.9, -26.1, -35.5 m/s) - so deferring the arm by one
            # poll needlessly eats the arming bound, and a few polls of delay would push
            # the craft permanently outside it: the flight-1 failure mode in slow motion.
            state = _eva4_enter(state, EVA4_DESCENT, snapshot.ut, peak)
        else:
            return _eva4_stay_or_flake(state, snapshot, peak), []

    if state.phase == EVA4_DESCENT:
        actions: List[Action] = []
        armed = state.chute_armed
        armed_alt = state.chute_armed_altitude
        armed_rate = state.chute_armed_rate
        # ARM WHILE SLOW (flight-1 fix): raise the full-deploy altitude, then arm, on the
        # first frame inside the rate bound. Both actions ride the SAME frame so the
        # module's very first ACTIVE FixedUpdate already sees the raised altitude.
        if (not armed and _is_finite(snapshot.vertical_speed)
                and abs(snapshot.vertical_speed) <= state.params.craft_chute_arm_max_rate):
            actions.append(Action(ACTION_SET_CHUTE_DEPLOY_ALTITUDE,
                                  state.params.craft_chute_full_deploy_alt))
            actions.append(Action(ACTION_DEPLOY_CHUTE))
            armed = True
            armed_alt = snapshot.altitude if _is_finite(snapshot.altitude) else None
            armed_rate = snapshot.vertical_speed

        full_seen = (state.craft_chute_full_seen
                     or snapshot.craft_chute_state == CHUTE_STATE_DEPLOYED)

        if eva4_window_open(state.params, snapshot):
            opened = _eva4_enter(state, EVA4_EVA_WINDOW, snapshot.ut, peak)
            return replace(opened, chute_armed=armed, chute_armed_altitude=armed_alt,
                           chute_armed_rate=armed_rate, craft_chute_full_seen=full_seen,
                           eva_window_altitude=snapshot.altitude,
                           eva_window_vertical_speed=snapshot.vertical_speed,
                           skip_settle_tail=True), actions

        # Sank past the floor without the window ever opening: bounded, named failure.
        # The reason carries the OBSERVED chute state (flight-1 lesson: "we commanded it"
        # is not evidence), so an inert-armed chute names itself.
        if (_is_finite(snapshot.altitude)
                and snapshot.altitude < state.params.eva_window_min_alt):
            return replace(
                state, peak_apoapsis=peak, chute_armed=armed,
                chute_armed_altitude=armed_alt, chute_armed_rate=armed_rate,
                craft_chute_full_seen=full_seen, done=True,
                verdict=MISSION_ASSERT_FAIL,
                loss_reason=("eva-window-missed: altitude %.0fm fell below the window "
                             "floor %.0fm (vspeed %.1fm/s, situation %s, craftChute=%s, "
                             "armCommanded=%s) without every window conjunct holding"
                             % (snapshot.altitude, state.params.eva_window_min_alt,
                                snapshot.vertical_speed, snapshot.situation or "?",
                                snapshot.craft_chute_state or "UNREAD",
                                "yes" if armed else "no"))), actions

        stayed = _eva4_stay_or_flake(state, snapshot, peak)
        return replace(stayed, chute_armed=armed, chute_armed_altitude=armed_alt,
                       chute_armed_rate=armed_rate,
                       craft_chute_full_seen=full_seen), actions

    # Unknown phase: defensively terminate as an error-shaped flake so the shell never spins.
    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase, done=True,
                   peak_apoapsis=peak), []


def _eva4_enter(state: Eva4State, new_phase: str, ut: float,
                peak: Optional[float]) -> Eva4State:
    """Transition into ``new_phase``, stamping the phase-entry UT for the budget clock
    and appending to ``phases_reached``. EVA-WINDOW is the terminal and sets ``done``."""
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        peak_apoapsis=peak,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == EVA4_EVA_WINDOW),
    )


def _eva4_loss_reason_with_altitude(state: Eva4State, base: str) -> str:
    """Append the last known altitude to a loss reason so a loss names WHERE it happened."""
    if state.last_finite_altitude is None or not _is_finite(state.last_finite_altitude):
        return base
    return "%s; last altitude %.0fm" % (base, state.last_finite_altitude)


def _eva4_stay_or_flake(state: Eva4State, snapshot: TelemetrySnapshot,
                        peak: Optional[float]) -> Eva4State:
    """Stay in the current phase, or flip to MISSION-FLAKE if it out-ran budget."""
    if _eva4_over_budget(state, snapshot):
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
    # Retarget thresholds are ASYMMETRIC (B7 review MINOR-4): interplanetary
    # SOI estimates jitter PROPORTIONALLY (flight 7 showed 200 s / 4,000 s
    # flip-flops on the Kerbin-exit / Duna legs, each retarget costing a
    # cancel + socket teardown + ramp restart). A fresh target EARLIER than
    # the commanded one always retargets at the absolute 120 s floor -- a
    # stale later target would carry the warp PAST the boundary at speed --
    # while a LATER fresh target tolerates 2% of the remaining span before
    # churning (arriving early is harmless: the machine re-polls and
    # re-warps). Close-in behavior is byte-identical to the proven B5
    # contract (the floor dominates below 6,000 s spans).
    span = (target - ut) if _is_finite(ut) else 0.0
    later_threshold = max(WARP_RETARGET_THRESHOLD_SECONDS, 0.02 * span)
    diff = (target - state.warp_to_cmd) if state.warp_to_cmd is not None else 0.0
    if (state.warp_to_cmd is None
            or diff < -WARP_RETARGET_THRESHOLD_SECONDS
            or diff > later_threshold):
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
# FORGE phase state machine (mission forge_station: the FIXTURE-FORGE runner).
# Pure. A minimal two-transition shell: boot an EXISTING valid save (LoadGame
# passes on its active vessel), launch the docking-variant craft onto the pad,
# wait for the spawned vessel to settle PRELAUNCH, done MISSION-OK. NO ascent /
# orbit -- it exists only to STAMP a pad fixture headlessly (2026-07-22
# operator-principle override). Generic over the craft + crew so the same forge
# later produces the EVA-3 pad fixture.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ForgeParams:
    """FIXTURE-FORGE tuning (spec [driver.missionParams] for forge_station). All
    are budgets / debounce depths, never a golden trajectory."""
    craft_name: str
    launch_site: str = "LaunchPad"
    # Explicit KERBAL NAMES seeded into the pod. None / empty -> KSP's default
    # crew assignments (crew=[]). By NAME, never a count: kRPC 0.5.4 exposes no
    # roster-enumeration API to resolve a count to names, only get_kerbal(name)
    # + launch_vessel(crew: List[str]). The EVA-3 3-crew pad fixture passes the
    # three names its EvaExit steps later reference.
    crew_names: Optional[Tuple[str, ...]] = None
    settle_situations: Tuple[str, ...] = ("PRE_LAUNCH",)
    launch_timeout: float = 300.0              # game-s to see the new craft settle
    settle_debounce: int = 3                   # K consecutive settled frames


def forge_params_from_dict(params: Dict) -> ForgeParams:
    params = params or {}
    crew_names = params.get("crewNames", None)
    return ForgeParams(
        craft_name=str(params.get("craftName", "Kerbal X")),
        launch_site=str(params.get("launchSite", "LaunchPad")),
        crew_names=(tuple(str(n) for n in crew_names)
                    if crew_names else None),
        settle_situations=tuple(params.get("settleSituations", ("PRE_LAUNCH",))),
        launch_timeout=float(params.get("launchTimeoutSeconds", 300)),
        settle_debounce=int(params.get("settleDebounceFrames", 3)),
    )


@dataclass(frozen=True)
class ForgeState:
    params: ForgeParams
    phase: str = FORGE_PRELAUNCH
    phase_entry_ut: float = 0.0
    phases_reached: Tuple[str, ...] = (FORGE_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    done: bool = False
    loss_reason: Optional[str] = None
    settle_streak: int = 0


def forge_initial_state(params: ForgeParams) -> ForgeState:
    return ForgeState(params=params)


def _forge_enter(state: ForgeState, new_phase: str, ut: float) -> ForgeState:
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state, phase=new_phase, phase_entry_ut=entry,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == FORGE_SETTLED))


def _forge_over_budget(state: ForgeState, snapshot: TelemetrySnapshot) -> bool:
    if state.phase != FORGE_LAUNCH:
        return False
    if not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > state.params.launch_timeout


def forge_decide(state: ForgeState,
                 snapshot: TelemetrySnapshot) -> Tuple[ForgeState, List[Action]]:
    """Advance the FORGE machine one frame; return (new_state, actions).

    - PRELAUNCH -> LAUNCH: emit ACTION_LAUNCH_VESSEL (the craft onto the pad).
    - LAUNCH -> SETTLED (done MISSION-OK): the new active vessel reads a
      settle situation (PRE_LAUNCH on the pad) for settleDebounce consecutive
      frames. Bounded by launchTimeoutSeconds -> MISSION-FLAKE.

    vessel_lost during LAUNCH is a scene-reload TRANSIENT (launch_vessel is a
    FLIGHT->FLIGHT reload; the runner's read-fail streak can briefly emit
    vessel_lost before the new craft materializes), so it does NOT terminate --
    the settle debounce + the launch budget own the outcome. A vessel_lost in
    any OTHER phase (there is only PRELAUNCH before the launch) is a real loss.
    """
    if state.done:
        return state, []

    if state.phase == FORGE_PRELAUNCH:
        if snapshot.vessel_lost:
            return replace(
                state, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason="vessel-lost before launch (boot save unreadable)"), []
        launch = Action(ACTION_LAUNCH_VESSEL,
                        text=state.params.craft_name,
                        launch_site=state.params.launch_site,
                        crew=state.params.crew_names)
        return _forge_enter(state, FORGE_LAUNCH, snapshot.ut), [launch]

    if state.phase == FORGE_LAUNCH:
        # vessel_lost is a reload transient here -- keep waiting (bounded).
        settled = (not snapshot.vessel_lost
                   and snapshot.situation in state.params.settle_situations)
        streak = state.settle_streak + 1 if settled else 0
        if streak >= state.params.settle_debounce:
            return _forge_enter(replace(state, settle_streak=streak),
                                FORGE_SETTLED, snapshot.ut), []
        stayed = replace(state, settle_streak=streak)
        if _forge_over_budget(stayed, snapshot):
            return replace(stayed, verdict=MISSION_FLAKE,
                           flake_phase=stayed.phase, done=True), []
        return stayed, []

    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase, done=True), []


def evaluate_forge_assertions(frames, params: ForgeParams,
                              phases_reached=(),
                              k: int = DEFAULT_DEBOUNCE_K) -> List[AssertionOutcome]:
    """Two FORGE driver-validity assertions (phase evidence; the forge produces
    STATE, not a trajectory):

    - ``launched``:        FORGE_LAUNCH appears in phases_reached (launch_vessel
      fired).
    - ``settledOnPad``:    FORGE_SETTLED appears in phases_reached (the new craft
      settled in a settle situation on the pad) AND the final situation is one
      of settleSituations (the settled state the SaveGame will persist).
    """
    del k
    frames = list(frames or [])
    phases = tuple(phases_reached or ())

    launched = AssertionOutcome("launched", FORGE_LAUNCH in phases,
                                (phases[-1] if phases else None),
                                {"required": FORGE_LAUNCH})

    reached = FORGE_SETTLED in phases
    final_situation = frames[-1].situation if frames else None
    settled_met = reached and (final_situation in params.settle_situations)
    settled = AssertionOutcome("settledOnPad", settled_met, final_situation,
                               {"required": FORGE_SETTLED,
                                "accepted": list(params.settle_situations)})
    return [launched, settled]


# ---------------------------------------------------------------------------
# B-DOCK phase state machine (mission bdock_dock_transfer: design sections 3.3 /
# 5). Pure. A NEW machine (NOT a B5 extension): its transitions key on target
# distance, relative speed, docking-port state, transfer completion, and the
# two-vessel launch sequence -- a mostly-disjoint branch set from B5's SOI /
# apsides / time-to-SOI logic (design section 5). The ascent legs emit the same
# B2 ascent ACTIONs; only the phase machine is new. Survival is the contract:
# any vessel-lost / frozen terminal (except the INT-LAUNCH reload transient) is
# an ASSERT-FAIL loss; a rendezvous / docking / transfer stall is a bounded
# give-up FLAKE (section 5.3), never a PARSEK-FAIL.
# ---------------------------------------------------------------------------

# Phases where a FROZEN-telemetry (destroyed-vessel) stall is a real risk and
# the shared 1x frozen detector applies. INT-LAUNCH is excluded (the reload
# transient); PRELAUNCH / STATION-COMMIT / SET-TARGET / TRANSFER / UNDOCK /
# TERMINAL are not continuous-flight phases. The detector self-gates on
# warp_mode == NONE, so the RENDEZVOUS phasing legs (on rails) never false-trip.
_BDOCK_FROZEN_PHASES: Tuple[str, ...] = (
    BDOCK_STATION_ASCENT, BDOCK_STATION_CIRCULARIZE, BDOCK_INT_ASCENT,
    BDOCK_INT_CIRCULARIZE, BDOCK_RENDEZVOUS, BDOCK_MATCH_VELOCITY, BDOCK_DOCK)

# Monoprop-out epsilon (P2): a docking-AP stall that thrashes RCS drains the
# monoprop; at/below this the DOCK give-up flakes monoprop-out (vs approach-stall
# from the monoprop reading). Fail closed on NaN (never fires on a missing read).
BDOCK_MONOPROP_OUT_EPS = 0.5

# STATION-SEPARATE / INT-SEPARATE completion debounce: consecutive frames whose
# vessel_count exceeds the phase-entry baseline (the spent core spawned as a NEW
# vessel) before the SEPARATE phase completes. Reuses the machine's K-consecutive
# settle idiom (DEFAULT_DEBOUNCE_K) so a one-frame count blip never certifies a
# separation. vessel_count defaults to 0 (unread -> fail closed), so an unreadable
# count never advances the streak.
BDOCK_SEPARATION_DEBOUNCE = DEFAULT_DEBOUNCE_K

# DOCK liveness watchdogs (flight-10/11 operator directive: "budgets bound SLOW;
# liveness watchdogs bound BROKEN. A phase may never idle to its budget while its
# actor is provably dead or inert.").
# - Consecutive polls the docking AP may be not-running before the machine acts:
#   the enable-never-took re-emit (E1a) and the died-mid-approach re-enable (E1b).
#   ~10 polls at 0.5 s each ~= 5 s -- MechJeb benches a NRE'd module within a
#   second or two, so this is a fast fail without false-tripping a one-frame blip.
BDOCK_DOCK_LIVENESS_K = 10
# Max port re-acquires in DOCK (flight-9 one-shot was too stingy if KSP clears the
# port target repeatedly): the dropped-target retarget latch is now a bounded
# count, not a single bool.
BDOCK_DOCK_MAX_RETARGETS = 3
# DOCK progress-signature epsilons: a reading must improve by at least this to
# count as observable progress (distance closing / monoprop burning / tumble
# killed). Below the epsilon is "flat" for the no-progress watchdog.
BDOCK_DOCK_DIST_EPS = 1.0        # metres
BDOCK_DOCK_MONO_EPS = 0.01       # monoprop units (RCS actually firing)
BDOCK_DOCK_ANGVEL_EPS = 0.001    # rad/s (tumble actually being reduced)
# TRANSFER liveness: consecutive polls transfer_amount may be flat/unread before
# the machine flakes the stall fast (well inside the transfer budget).
BDOCK_TRANSFER_STALL_FRAMES = 20


@dataclass(frozen=True)
class BDockParams:
    """B-DOCK tuning (spec [driver.missionParams] for bdock_dock_transfer). All
    tolerances / windows / budgets, never a golden trajectory. Every budget is
    ESTIMATED (design section 5.4) and re-timed against the first live run."""
    # Station park (~110 km) + Interceptor phasing park (~90 km, BELOW the
    # Station so it phases faster). Shared apo/peri error + ascent/circularize
    # budgets for both legs (the ascent half is the B2-proven shape).
    station_apoapsis: float = 110000.0
    station_periapsis: float = 110000.0
    interceptor_apoapsis: float = 90000.0
    interceptor_periapsis: float = 90000.0
    apo_error: float = 5000.0
    peri_error: float = 5000.0
    ascent_timeout: float = 1200.0
    circularize_timeout: float = 600.0
    # Post-circularize stage-separation give-up (GAME seconds): the SEPARATE phase
    # flakes if no NEW vessel (the spent core) ever appears within this window
    # after the single ACTION_ACTIVATE_STAGE. Estimated; re-timed against the
    # first live run (the separation itself is instantaneous, so the budget is a
    # generous stuck-decoupler backstop).
    separation_timeout: float = 120.0
    # Interceptor launch (piece 2): the craft + the launch settle.
    craft_name: str = "Kerbal X"
    launch_site: str = "LaunchPad"
    launch_settle_situations: Tuple[str, ...] = ("PRE_LAUNCH",)
    launch_timeout: float = 300.0
    launch_settle_debounce: int = 3
    # Rendezvous / dock / transfer thresholds.
    approach_distance: float = 100.0           # rendezvous desired_distance (m)
    max_phasing_orbits: float = 5.0
    match_speed: float = 1.0                   # MATCH-VELOCITY rel-speed floor (m/s)
    match_timeout: float = 600.0               # MATCH-VELOCITY give-up (GAME s; flight-5)
    dock_speed: float = 0.5                    # docking AP speed_limit (m/s)
    transfer_amount_lf: float = 40.0           # LiquidFuel deliver (transport->station)
    transfer_amount_mp: float = 15.0           # MonoPropellant pickup (station->transport)
    # Give-up budgets (GAME time; section 5.4). Phasing legs advance game time
    # fast under rails warp, so rendezvous_timeout is large.
    station_commit_timeout: float = 300.0      # bounded wait for the seam commit result
    rendezvous_timeout: float = 30000.0
    dock_timeout: float = 600.0                # the 1x approach
    # DOCK progress watchdog (flight-11): while the docking AP is enabled, if NONE
    # of distance / monoprop / angular_velocity shows progress for this many GAME
    # seconds, flake fast instead of idling to dock_timeout (a dead AP).
    dock_no_progress_seconds: float = 120.0
    transfer_timeout: float = 120.0            # each transfer; TRANSFER phase = 2x this
    undock_timeout: float = 120.0
    # RENDEZVOUS no-progress detector: consecutive finite frames whose
    # target_distance never beat the running minimum -> flake (a stuck AP).
    rendezvous_noprogress_frames: int = 40
    frozen_sample_limit: int = 10


def bdock_params_from_dict(params: Dict) -> BDockParams:
    params = params or {}
    return BDockParams(
        station_apoapsis=float(params.get("stationApoapsisMeters", 110000)),
        station_periapsis=float(params.get("stationPeriapsisMeters", 110000)),
        interceptor_apoapsis=float(params.get("interceptorApoapsisMeters", 90000)),
        interceptor_periapsis=float(params.get("interceptorPeriapsisMeters", 90000)),
        apo_error=float(params.get("apoErrorMeters", 5000)),
        peri_error=float(params.get("periErrorMeters", 5000)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 1200)),
        circularize_timeout=float(params.get("circularizeTimeoutSeconds", 600)),
        separation_timeout=float(params.get("separationTimeoutSeconds", 120)),
        craft_name=str(params.get("craftName", "Kerbal X")),
        launch_site=str(params.get("launchSite", "LaunchPad")),
        launch_settle_situations=tuple(params.get("launchSettleSituations", ("PRE_LAUNCH",))),
        launch_timeout=float(params.get("launchTimeoutSeconds", 300)),
        launch_settle_debounce=int(params.get("launchSettleDebounceFrames", 3)),
        approach_distance=float(params.get("approachDistanceMeters", 100)),
        max_phasing_orbits=float(params.get("maxPhasingOrbits", 5)),
        match_speed=float(params.get("matchSpeedMetersPerSec", 1.0)),
        match_timeout=float(params.get("matchTimeoutSeconds", 600)),
        dock_speed=float(params.get("dockSpeedMetersPerSec", 0.5)),
        transfer_amount_lf=float(params.get("transferAmountLf", 40)),
        transfer_amount_mp=float(params.get("transferAmountMp", 15)),
        station_commit_timeout=float(params.get("stationCommitTimeoutSeconds", 300)),
        rendezvous_timeout=float(params.get("rendezvousTimeoutSeconds", 30000)),
        dock_timeout=float(params.get("dockTimeoutSeconds", 600)),
        dock_no_progress_seconds=float(params.get("dockNoProgressSeconds", 120)),
        transfer_timeout=float(params.get("transferTimeoutSeconds", 120)),
        undock_timeout=float(params.get("undockTimeoutSeconds", 120)),
        rendezvous_noprogress_frames=int(params.get("rendezvousNoProgressFrames", 40)),
        frozen_sample_limit=int(params.get("frozenTelemetrySamples", 10)),
    )


@dataclass(frozen=True)
class BDockState:
    """B-DOCK machine state (design section 3.3). ``verdict`` / ``flake_phase`` /
    ``done`` mirror B2/B5: done at TERMINAL (verdict None -> assertions judge) or
    on a flake / loss. Carried evidence rides for the evaluator + the give-ups."""
    params: BDockParams
    phase: str = BDOCK_PRELAUNCH
    phase_entry_ut: float = 0.0
    phases_reached: Tuple[str, ...] = (BDOCK_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    done: bool = False
    loss_reason: Optional[str] = None
    # Custom FLAKE reason (surfaced by resolve_flight_verdict in place of the
    # generic "phase X timed out"); set by the SEPARATE give-up so the operator
    # sees "no separation observed" rather than a bare timeout. None -> generic.
    flake_reason: Optional[str] = None
    # Shared frozen-telemetry detection (mirrors B2/B5).
    frozen_sig: Optional[FrozenSignature] = None
    frozen_count: int = 0
    # Rendezvous / docking Enabled-latch tracking (NIT-15).
    rendezvous_ever_enabled: bool = False
    docking_ever_enabled: bool = False
    # Interceptor launch settle.
    launch_settle_streak: int = 0
    # RENDEZVOUS no-progress detector.
    rendezvous_min_distance: float = float("inf")
    rendezvous_noprogress_count: int = 0
    # MATCH-VELOCITY dropped-target recovery (flight-5): consecutive non-finite
    # target_rel_speed frames (the target likely dropped when the rendezvous AP
    # disabled itself), and the one-shot re-target latch. NaN rel-speed never
    # completes the phase (fail-closed); a dropped target is re-acquired ONCE.
    match_nan_streak: int = 0
    match_retarget_done: bool = False
    # DOCK dropped-target recovery (flight-8/9): consecutive non-finite
    # target_distance frames (the docking-port target went null when a pending
    # kill-rel-vel node rails-warped + packed the ship) and the BOUNDED re-target
    # count (flight-11: one-shot was too stingy if KSP clears the port repeatedly;
    # cap BDOCK_DOCK_MAX_RETARGETS). NaN never completes DOCK (fail-closed).
    dock_nan_streak: int = 0
    dock_retarget_count: int = 0
    # DOCK AP-death liveness (flight-11). E1a "enable never took": polls since the
    # deferred enable was emitted with mj_docking_enabled still False, and the
    # one-shot re-emit latch. E1b "died mid-approach": consecutive polls the AP is
    # disabled after having run (not docked), and the one-shot re-enable latch.
    dock_enable_wait_streak: int = 0
    dock_enable_reissued: bool = False
    dock_died_streak: int = 0
    dock_reenabled_after_death: bool = False
    # DOCK progress watchdog (flight-11). Running minima of the progress signature
    # (distance closing / monoprop burning / tumble killed) and the UT of the last
    # observed progress; if none improves for dock_no_progress_seconds the AP is
    # inert and the phase flakes fast.
    dock_best_distance: float = float("inf")
    dock_best_monoprop: float = float("inf")
    dock_best_angvel: float = float("inf")
    dock_last_progress_ut: float = float("nan")
    # Staggered docking-AP enable (flight 9): MechJeb's core.target syncs from
    # the KSP-level target on its NEXT Update, so enabling the docking AP in
    # the SAME action batch as set_target_docking_port makes the AP's first
    # Drive tick see the OLD vessel target, cast it to a docking node, NRE,
    # and get benched by MechJeb (2 NRE lines then silence, ship sat to the
    # budget). Entry/retarget SET the port target and arm this flag; the
    # enable is emitted on the NEXT poll (~0.5 s, plenty of Unity frames).
    dock_enable_pending: bool = False
    # TRANSFER sequencing (T1 LiquidFuel deliver, T2 MonoPropellant pickup).
    current_transfer_started: bool = False
    transfers_done: int = 0
    # TRANSFER liveness (flight-11): the max transfer_amount seen in the active
    # transfer and consecutive polls without an increase; a flat/unread amount for
    # BDOCK_TRANSFER_STALL_FRAMES flakes fast (dry source / full dest) instead of
    # idling to the transfer budget. Reset when a new transfer starts.
    transfer_best_amount: float = float("-inf")
    transfer_noprogress_streak: int = 0
    # STATION-SEPARATE / INT-SEPARATE evidence (flight-4 two-step contract). The
    # vessel count captured at SEPARATE entry (mirrors the UNDOCK baseline); the
    # K-consecutive streak of frames whose count exceeds it (step 1: the spent
    # core spawned as a NEW vessel); the K-consecutive streak of frames with
    # available_thrust > 0 (step 2: the orbital engine is lit); the latched
    # split-confirmed flag; and the per-phase ACTIVATE_STAGE count (HARD-capped at
    # 2 -- a third would fire the istg=0 heat-shield decoupler). All reset on entry
    # to each SEPARATE phase (the two legs are sequential, never concurrent, so
    # one set of fields serves both).
    separate_baseline_vessel_count: int = 0
    separate_settle_streak: int = 0
    separate_thrust_streak: int = 0
    separate_split_confirmed: bool = False
    separate_activations: int = 0
    # UNDOCK split evidence.
    undock_baseline_vessel_count: int = 0
    # Carried evidence for the evaluator.
    docked_confirmed: bool = False
    undock_confirmed: bool = False


def bdock_initial_state(params: BDockParams) -> BDockState:
    return BDockState(params=params)


def _bdock_enter(state: BDockState, new_phase: str, ut: float,
                 **fields) -> BDockState:
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state, phase=new_phase, phase_entry_ut=entry,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == BDOCK_TERMINAL), **fields)


def _bdock_phase_budget(params: BDockParams, phase: str) -> Optional[float]:
    if phase in (BDOCK_STATION_ASCENT, BDOCK_INT_ASCENT):
        return params.ascent_timeout
    if phase in (BDOCK_STATION_CIRCULARIZE, BDOCK_INT_CIRCULARIZE):
        return params.circularize_timeout
    if phase in (BDOCK_STATION_SEPARATE, BDOCK_INT_SEPARATE):
        return params.separation_timeout
    if phase == BDOCK_STATION_COMMIT:
        return params.station_commit_timeout
    if phase == BDOCK_INT_LAUNCH:
        return params.launch_timeout
    if phase == BDOCK_RENDEZVOUS:
        return params.rendezvous_timeout
    if phase == BDOCK_DOCK:
        return params.dock_timeout
    if phase == BDOCK_TRANSFER:
        return 2.0 * params.transfer_timeout
    if phase == BDOCK_UNDOCK:
        return params.undock_timeout
    if phase == BDOCK_MATCH_VELOCITY:
        # Flight-5 lesson: MATCH-VELOCITY is NOT a fast transition -- the
        # kill-rel-vel node can land far ahead, so an unmet gate silently ate the
        # whole 4800 s wall. It now carries its own bounded give-up.
        return params.match_timeout
    # SET-TARGET: no dedicated budget (a fast transition); the wall deadline in
    # the fly loop is the ultimate backstop.
    return None


def _bdock_over_budget(state: BDockState, snapshot: TelemetrySnapshot) -> bool:
    budget = _bdock_phase_budget(state.params, state.phase)
    if budget is None or not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > budget


def _bdock_flake(state: BDockState, reason_phase: Optional[str] = None) -> BDockState:
    return replace(state, verdict=MISSION_FLAKE,
                   flake_phase=reason_phase or state.phase, done=True)


def _bdock_stay_or_flake(state: BDockState,
                         snapshot: TelemetrySnapshot) -> BDockState:
    if _bdock_over_budget(state, snapshot):
        return _bdock_flake(state)
    return state


def _bdock_ascent_entry_actions(target_apoapsis: float) -> List[Action]:
    """The B2-proven staged-ascent actions (both legs). MechJeb's ascent AP
    engaged via kRPC does NOT ignite the first stage itself, so the mission
    activates the initial stage exactly like a GUI user pressing space."""
    return [
        Action(ACTION_MJ_SET_TARGET_APOAPSIS, target_apoapsis),
        Action(ACTION_MJ_ENABLE_AUTOSTAGE),
        Action(ACTION_MJ_ENGAGE_ASCENT),
        Action(ACTION_ACTIVATE_STAGE),
    ]


def _bdock_attitude_hold_actions() -> List[Action]:
    """SAS stability-assist + RCS on (flight-10 tumble fix). Emitted when a stage
    separation just dropped mass (separation torque with no SAS = a tumble) and at
    DOCK entry (hand the docking AP a stabilized, RCS-ready ship). SET_RCS value
    1.0 = on."""
    return [Action(ACTION_SET_SAS), Action(ACTION_SET_RCS, value=1.0)]


def _bdock_dock_progress(state: BDockState, snapshot: TelemetrySnapshot,
                         params: BDockParams
                         ) -> Tuple[BDockState, Optional[BDockState]]:
    """DOCK progress watchdog (flight-11 liveness: "budgets bound SLOW; liveness
    bounds BROKEN"). While the docking AP is enabled, ANY of target_distance
    closing / monopropellant burning (RCS actually firing) / angular_velocity
    falling (tumble actually being killed) is observable progress. If NONE improves
    for dock_no_progress_seconds the AP is enabled-but-inert (benched / NRE / stuck)
    -> a named fast flake instead of idling to the dock budget. Returns
    (new_state, flake_state_or_None). Fail closed: an unread (NaN) reading is never
    progress."""
    improved = False
    best_d, best_m, best_a = (state.dock_best_distance, state.dock_best_monoprop,
                              state.dock_best_angvel)
    d = snapshot.target_distance
    m = snapshot.monopropellant
    a = snapshot.angular_velocity
    if _is_finite(d) and d < best_d - BDOCK_DOCK_DIST_EPS:
        best_d = d
        improved = True
    if _is_finite(m) and m < best_m - BDOCK_DOCK_MONO_EPS:
        best_m = m
        improved = True
    if _is_finite(a) and a < best_a - BDOCK_DOCK_ANGVEL_EPS:
        best_a = a
        improved = True
    st = replace(state, dock_best_distance=best_d, dock_best_monoprop=best_m,
                 dock_best_angvel=best_a)
    if improved:
        return replace(st, dock_last_progress_ut=snapshot.ut), None
    if (_is_finite(snapshot.ut) and _is_finite(st.dock_last_progress_ut)
            and (snapshot.ut - st.dock_last_progress_ut)
            > params.dock_no_progress_seconds):
        return st, replace(_bdock_flake(st), flake_reason=(
            "phase %s: docking AP enabled but no observable progress "
            "(dist/monoprop/angvel all flat)" % st.phase))
    return st, None


def separation_evidence(vessel_count: int, available_thrust: float,
                        baseline_vessel_count: int, settle_streak: int,
                        thrust_streak: int, split_confirmed: bool,
                        debounce: int = BDOCK_SEPARATION_DEBOUNCE
                        ) -> Tuple[int, int, bool, bool]:
    """The pure evidence half of the TWO-STEP separation contract (flight-3 /
    flight-4 lessons), shared by every machine that must leave a craft as its
    ORBITAL STAGE ONLY: B-DOCK's STATION-SEPARATE / INT-SEPARATE and the
    FORGE-LKO orbital fixture forge.

    Returns ``(settle_streak, thrust_streak, split_confirmed, ignited)`` for this
    frame:

    - step 1 (drop the spent core): ``vessel_count`` above the phase-entry
      ``baseline_vessel_count`` (the core spawned as a NEW vessel), debounced
      ``debounce`` consecutive frames -> ``split_confirmed`` LATCHES True.
    - step 2 (ignite the orbital engine): ``available_thrust > 0`` debounced
      ``debounce`` consecutive frames -> ``ignited`` True for this frame.

    Fail closed on both: ``vessel_count`` defaults 0 (unread) so an unreadable
    count never bumps past a baseline, and a NaN ``available_thrust`` is never
    treated as ignited. The CALLER owns the phase/budget/flake wrapping and the
    at-most-two stage activations (a third would fire the istg=0 heat-shield
    decoupler) -- this helper only counts evidence."""
    split_bumped = vessel_count > baseline_vessel_count
    settle = settle_streak + 1 if split_bumped else 0
    thrust_up = _is_finite(available_thrust) and available_thrust > 0.0
    thrust = thrust_streak + 1 if thrust_up else 0
    confirmed = bool(split_confirmed or settle >= debounce)
    return settle, thrust, confirmed, bool(thrust >= debounce)


def _bdock_separate_step(state: BDockState, snapshot: TelemetrySnapshot,
                         next_phase: str) -> Tuple[BDockState, List[Action]]:
    """One SEPARATE-phase frame: the evidence-chained TWO-step separation contract
    (flight-4 lesson -- flight 4 dropped the core but reached RENDEZVOUS with
    avThr=0.000, the orbital engine never ignited, because the LV-T45 sits in a
    LATER stage than the separation decoupler).

    Step 1 (drop the spent core). The entry ACTION_ACTIVATE_STAGE (emitted by the
    circularize->SEPARATE transition) drops the core. Step 1 completes when
    vessel_count exceeds the phase-entry baseline (the core spawned as a NEW
    vessel), debounced BDOCK_SEPARATION_DEBOUNCE frames.

    Step 2 (ignite the orbital engine). AFTER the split is confirmed: if
    available_thrust is ALREADY debounced-positive (a craft whose decoupler +
    engine share a stage -- the engine lit on the entry activation), complete with
    NO second activation. Otherwise emit EXACTLY ONE more ACTIVATE_STAGE to light
    the orbital stage, then complete on available_thrust > 0 debounced. HARD CAP:
    at most 2 activations per SEPARATE phase -- a THIRD would fire the istg=0
    heat-shield decoupler, so the ignition activation is emitted at most once.

    Fail closed: vessel_count defaults 0 (unread) and available_thrust defaults
    NaN (unread) -- neither an unread count nor an unread / zero thrust ever
    certifies a step, and NaN is never treated as ignited. Bounded give-up
    (separationTimeoutSeconds spans BOTH steps) with a reason that distinguishes a
    no-split from a split-but-no-ignition stall. The evidence half is the shared
    pure ``separation_evidence`` (FORGE-LKO reuses the SAME counter); this
    function owns only the phase / budget / activation-cap wrapping."""
    settle, thrust_streak, split_confirmed, ignited = separation_evidence(
        snapshot.vessel_count, snapshot.available_thrust,
        state.separate_baseline_vessel_count, state.separate_settle_streak,
        state.separate_thrust_streak, state.separate_split_confirmed)
    st = replace(state, separate_settle_streak=settle,
                 separate_thrust_streak=thrust_streak,
                 separate_split_confirmed=split_confirmed)

    if not split_confirmed:
        # Step 1: still waiting for the spent core to spawn.
        if _bdock_over_budget(st, snapshot):
            return replace(_bdock_flake(st), flake_reason=(
                "phase %s: no separation observed (vessel_count did not increase)"
                % st.phase)), []
        return st, []

    # Step 2: the split is confirmed -> ensure the orbital engine is lit.
    if ignited:
        # Separation dropped the spent core -> hold attitude (SAS + RCS) into the
        # next phase so the orbital stage does not tumble (flight-10).
        return (_bdock_enter(st, next_phase, snapshot.ut,
                             separate_settle_streak=0, separate_thrust_streak=0,
                             separate_split_confirmed=False,
                             separate_activations=0),
                _bdock_attitude_hold_actions())
    if st.separate_activations < 2:
        # Ignition: exactly one more activation (never a third -> heat shield).
        return (replace(st, separate_activations=st.separate_activations + 1),
                [Action(ACTION_ACTIVATE_STAGE)])
    if _bdock_over_budget(st, snapshot):
        return replace(_bdock_flake(st), flake_reason=(
            "phase %s: separated but no ignition (available_thrust stayed 0)"
            % st.phase)), []
    return st, []


def bdock_decide(state: BDockState,
                 snapshot: TelemetrySnapshot) -> Tuple[BDockState, List[Action]]:
    """Advance the B-DOCK machine one frame; return (new_state, actions).
    Transitions per design section 3.3. See the phase-name docstrings above."""
    if state.done:
        return state, []

    # Phase-independent vessel-loss terminal (mirrors B2/B5), EXCEPT during the
    # Interceptor launch reload where a vessel_lost is a transient (the new
    # craft has not materialized yet); INT-LAUNCH owns that with its settle
    # debounce + launch budget.
    if snapshot.vessel_lost and state.phase != BDOCK_INT_LAUNCH:
        return replace(
            state, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason="vessel-lost (unreadable after repeated telemetry failures)"), []

    # Frozen-telemetry (vessel-destroyed) detection, flight phases only.
    if state.phase in _BDOCK_FROZEN_PHASES:
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

    p = state.params

    # ---- PIECE 1: STATION (pre-placed on the pad) --------------------------
    if state.phase == BDOCK_PRELAUNCH:
        return (_bdock_enter(state, BDOCK_STATION_ASCENT, snapshot.ut),
                _bdock_ascent_entry_actions(p.station_apoapsis))

    if state.phase == BDOCK_STATION_ASCENT:
        apo_reached = (_is_finite(snapshot.apoapsis)
                       and snapshot.apoapsis >= p.station_apoapsis - p.apo_error)
        if snapshot.mj_ascent_complete and apo_reached:
            return (_bdock_enter(state, BDOCK_STATION_CIRCULARIZE, snapshot.ut),
                    [Action(ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        return _bdock_stay_or_flake(state, snapshot), []

    if state.phase == BDOCK_STATION_CIRCULARIZE:
        if (_is_finite(snapshot.periapsis)
                and snapshot.periapsis >= p.station_periapsis - p.peri_error):
            # Circularized -> drop the spent core AND ignite the orbital engine
            # (the two-step SEPARATE contract). This entry ACTIVATE_STAGE (count 1)
            # drops the core; SEPARATE step 1 confirms on the vessel_count
            # increase, step 2 lights the orbital stage (at most one more
            # activation, cap 2). Baseline the pre-split vessel count.
            return (_bdock_enter(state, BDOCK_STATION_SEPARATE, snapshot.ut,
                                 separate_baseline_vessel_count=snapshot.vessel_count,
                                 separate_settle_streak=0,
                                 separate_thrust_streak=0,
                                 separate_split_confirmed=False,
                                 separate_activations=1),
                    [Action(ACTION_ACTIVATE_STAGE)])
        return _bdock_stay_or_flake(state, snapshot), []

    if state.phase == BDOCK_STATION_SEPARATE:
        return _bdock_separate_step(state, snapshot, BDOCK_STATION_ORBIT)

    if state.phase == BDOCK_STATION_ORBIT:
        # Capture the Station handle (while it is the active vessel, P9/Q4) and
        # commit its tree via the command seam (route 1). Both fire on entry to
        # STATION-COMMIT; the bounded-wait for the seam result follows.
        return (_bdock_enter(state, BDOCK_STATION_COMMIT, snapshot.ut),
                [Action(ACTION_CAPTURE_STATION),
                 Action(ACTION_PARSEK_COMMIT_TREE)])

    if state.phase == BDOCK_STATION_COMMIT:
        result = snapshot.seam_commit_result
        if result == "OK":
            # Launch the Interceptor (same craft, from the now-clear pad).
            return (_bdock_enter(state, BDOCK_INT_LAUNCH, snapshot.ut),
                    [Action(ACTION_LAUNCH_VESSEL, text=p.craft_name)])
        if result in ("ERROR", "TIMEOUT"):
            # Review follow-up 5: name the seam outcome so the operator sees WHY
            # STATION-COMMIT flaked (the tree-commit command seam returned ERROR
            # or TIMEOUT) instead of the generic phase-timeout wording.
            return replace(_bdock_flake(replace(state, loss_reason=None)),
                           flake_reason=(
                               "phase %s: tree-commit seam returned %s"
                               % (BDOCK_STATION_COMMIT, result))), []
        return _bdock_stay_or_flake(state, snapshot), []

    # ---- PIECE 2: INTERCEPTOR (launch_vessel) ------------------------------
    if state.phase == BDOCK_INT_LAUNCH:
        settled = (not snapshot.vessel_lost
                   and snapshot.situation in p.launch_settle_situations)
        streak = state.launch_settle_streak + 1 if settled else 0
        if streak >= p.launch_settle_debounce:
            return (_bdock_enter(replace(state, launch_settle_streak=streak),
                                 BDOCK_INT_ASCENT, snapshot.ut),
                    _bdock_ascent_entry_actions(p.interceptor_apoapsis))
        return _bdock_stay_or_flake(replace(state, launch_settle_streak=streak),
                                    snapshot), []

    if state.phase == BDOCK_INT_ASCENT:
        apo_reached = (_is_finite(snapshot.apoapsis)
                       and snapshot.apoapsis >= p.interceptor_apoapsis - p.apo_error)
        if snapshot.mj_ascent_complete and apo_reached:
            return (_bdock_enter(state, BDOCK_INT_CIRCULARIZE, snapshot.ut),
                    [Action(ACTION_MJ_EXECUTE_CIRCULARIZATION)])
        return _bdock_stay_or_flake(state, snapshot), []

    if state.phase == BDOCK_INT_CIRCULARIZE:
        if (_is_finite(snapshot.periapsis)
                and snapshot.periapsis >= p.interceptor_periapsis - p.peri_error):
            # Same two-step separation as the Station leg: drop the spent
            # Interceptor core AND ignite its orbital engine so it docks as its
            # orbital stage only.
            return (_bdock_enter(state, BDOCK_INT_SEPARATE, snapshot.ut,
                                 separate_baseline_vessel_count=snapshot.vessel_count,
                                 separate_settle_streak=0,
                                 separate_thrust_streak=0,
                                 separate_split_confirmed=False,
                                 separate_activations=1),
                    [Action(ACTION_ACTIVATE_STAGE)])
        return _bdock_stay_or_flake(state, snapshot), []

    if state.phase == BDOCK_INT_SEPARATE:
        return _bdock_separate_step(state, snapshot, BDOCK_INT_PHASING_ORBIT)

    if state.phase == BDOCK_INT_PHASING_ORBIT:
        return (_bdock_enter(state, BDOCK_SET_TARGET, snapshot.ut),
                [Action(ACTION_SET_TARGET_VESSEL)])

    if state.phase == BDOCK_SET_TARGET:
        if snapshot.target_set:
            return (_bdock_enter(state, BDOCK_RENDEZVOUS, snapshot.ut,
                                 rendezvous_min_distance=float("inf"),
                                 rendezvous_noprogress_count=0),
                    [Action(ACTION_MJ_ENABLE_RENDEZVOUS,
                            value=p.approach_distance, limit=p.max_phasing_orbits)])
        return _bdock_stay_or_flake(state, snapshot), []

    if state.phase == BDOCK_RENDEZVOUS:
        # Latch: the AP self-disables when finished (NIT-15).
        st = state
        if snapshot.mj_rendezvous_enabled:
            st = replace(st, rendezvous_ever_enabled=True)
        latched_off = st.rendezvous_ever_enabled and not snapshot.mj_rendezvous_enabled
        close = (_is_finite(snapshot.target_distance)
                 and snapshot.target_distance <= p.approach_distance)
        if latched_off and close:
            return (_bdock_enter(st, BDOCK_MATCH_VELOCITY, snapshot.ut),
                    [Action(ACTION_MJ_KILL_REL_VEL)])
        # No-progress detector: track the running minimum distance. PAUSED
        # (counter reset) while a maneuver node is pending (flight 11): the
        # rendezvous AP legitimately waits minutes for a burn window with the
        # distance flat -- it killed a HEALTHY rendezvous 3.4 m/s from done.
        # Liveness means the actor is DEAD, not "position not improving while
        # a burn is scheduled"; the phase budget bounds slow-but-alive.
        if snapshot.node_count > 0:
            st = replace(st, rendezvous_noprogress_count=0)
        elif _is_finite(snapshot.target_distance):
            if snapshot.target_distance < st.rendezvous_min_distance:
                st = replace(st, rendezvous_min_distance=snapshot.target_distance,
                             rendezvous_noprogress_count=0)
            else:
                st = replace(st,
                             rendezvous_noprogress_count=st.rendezvous_noprogress_count + 1)
                if st.rendezvous_noprogress_count >= p.rendezvous_noprogress_frames:
                    return _bdock_flake(st), []
        return _bdock_stay_or_flake(st, snapshot), []

    if state.phase == BDOCK_MATCH_VELOCITY:
        rel = snapshot.target_rel_speed
        st = state
        if _is_finite(rel):
            # A finite reading resets the dropped-target streak; complete when at
            # or below the rel-speed floor.
            st = replace(st, match_nan_streak=0)
            if rel <= p.match_speed:
                # Abort any pending maneuver execution FIRST (flight-8 prox-ops
                # rule): the kill-rel-vel node can still be pending in the executor
                # with autowarp when MATCH-VELOCITY completes in ~0.5 s, and it
                # rails-warps at ~approach distance, packing the docking-port
                # target null. Then target the port -- but do NOT enable the
                # docking AP in the same batch (flight 9): MechJeb's core.target
                # syncs on its next Update, so a same-batch enable makes the AP's
                # first Drive tick see the OLD vessel target, NRE, and get benched
                # by MechJeb. dock_enable_pending defers the enable to the next
                # poll.
                return (_bdock_enter(replace(st, dock_enable_pending=True),
                                     BDOCK_DOCK, snapshot.ut,
                                     dock_last_progress_ut=snapshot.ut),
                        [Action(ACTION_MJ_ABORT_NODE_EXEC),
                         Action(ACTION_SET_SAS), Action(ACTION_SET_RCS, value=1.0),
                         Action(ACTION_SET_TARGET_DOCKING_PORT)])
        else:
            # Non-finite rel-speed (fail-closed: NaN NEVER completes the phase).
            # The target likely dropped when the rendezvous AP disabled itself;
            # re-acquire it EXACTLY ONCE, debounced K frames so a single transient
            # read miss never re-targets. SET_TARGET is idempotent.
            streak = st.match_nan_streak + 1
            st = replace(st, match_nan_streak=streak)
            if streak >= DEFAULT_DEBOUNCE_K and not st.match_retarget_done:
                return (replace(st, match_retarget_done=True, match_nan_streak=0),
                        [Action(ACTION_SET_TARGET_VESSEL)])
        if _bdock_over_budget(st, snapshot):
            last = ("%.3f" % rel) if _is_finite(rel) else "nan"
            return replace(_bdock_flake(st), flake_reason=(
                "phase %s: match-velocity did not reach rel-speed floor "
                "(target_rel_speed=%s)" % (st.phase, last))), []
        return st, []

    if state.phase == BDOCK_DOCK:
        st = state
        docked = snapshot.docking_state == DOCKING_STATE_DOCKED
        # Docked short-circuit (review follow-up 4). A hard dock can land on the
        # SAME poll that a retarget armed dock_enable_pending. Re-enabling the
        # docking AP on an already-mated pair is at best a no-op and at worst an
        # unguarded runner ENABLE that throws and flakes a WON mission. So the
        # docked test sits AHEAD of the dock_enable_pending branch: once the pair
        # reads docked, discard any pending enable and complete straight to
        # TRANSFER (disable the AP + start T1), exactly as the old latched-off
        # completion did. Completing on `docked` ALONE (not docked AND latched_off)
        # also covers the race where the deferred enable never fired, so
        # docking_ever_enabled is still False and a latched-off gate would
        # otherwise misroute a docked pair into the E1a "enable never took" flake.
        # onPartCouple -> Parsek authors the cross-tree Dock branch + opens the
        # RouteConnectionWindow.
        if docked:
            return (_bdock_enter(replace(st, docked_confirmed=True,
                                         current_transfer_started=True,
                                         transfer_best_amount=float("-inf"),
                                         transfer_noprogress_streak=0,
                                         dock_enable_pending=False),
                                 BDOCK_TRANSFER, snapshot.ut),
                    [Action(ACTION_MJ_DISABLE_DOCKING),
                     Action(ACTION_START_RESOURCE_TRANSFER,
                            value=p.transfer_amount_lf, text="LiquidFuel",
                            limit=TRANSFER_DIR_DELIVER)])

        # Deferred docking-AP enable (flight 9): the port target was set on the
        # previous batch; by this poll MechJeb's core.target has synced to it, so
        # the AP's first Drive tick sees a real docking node. Reset the enable-wait
        # watchdog -- we have just (re-)issued the enable. (Not reached when
        # docked: the short-circuit above already completed the phase.)
        if st.dock_enable_pending:
            return (replace(st, dock_enable_pending=False,
                            dock_enable_wait_streak=0),
                    [Action(ACTION_MJ_ENABLE_DOCKING, value=p.dock_speed)])

        ap_on = snapshot.mj_docking_enabled
        if ap_on:
            # The AP is running: latch docking_ever_enabled and clear both
            # AP-death watchdog streaks (it is alive this frame).
            st = replace(st, docking_ever_enabled=True, dock_enable_wait_streak=0,
                         dock_died_streak=0)

        if ap_on:
            # ---- AP running: dropped-target recovery + progress watchdog + give-ups.
            # Dropped-target recovery (flight-8/9/11): the port target went null
            # (target_distance non-finite) for K debounced frames -> re-acquire it,
            # staggered enable, BOUNDED to BDOCK_DOCK_MAX_RETARGETS re-arms
            # (flight-11: one-shot was too stingy if KSP clears the port
            # repeatedly). NaN never completes anything (the docked gate reads
            # docking_state), so fail-closed is preserved.
            if not _is_finite(snapshot.target_distance):
                streak = st.dock_nan_streak + 1
                st = replace(st, dock_nan_streak=streak)
                if (streak >= DEFAULT_DEBOUNCE_K
                        and st.dock_retarget_count < BDOCK_DOCK_MAX_RETARGETS):
                    return (replace(st,
                                    dock_retarget_count=st.dock_retarget_count + 1,
                                    dock_nan_streak=0, dock_enable_pending=True),
                            [Action(ACTION_SET_TARGET_DOCKING_PORT)])
            else:
                st = replace(st, dock_nan_streak=0)
            # Progress watchdog (flight-11): the AP is enabled -- is it DOING
            # anything? None of dist/monoprop/angvel improving for the window is a
            # dead/inert AP; flake fast with the named reason.
            st, prog_flake = _bdock_dock_progress(st, snapshot, p)
            if prog_flake is not None:
                return prog_flake, [Action(ACTION_MJ_DISABLE_DOCKING)]
            # Monoprop-out give-up (P2): a docking-AP stall thrashing RCS drains
            # it. (docked was already completed by the short-circuit above, so a
            # not-docked test here is redundant -- this branch only runs on a
            # non-docked, AP-still-running poll.)
            if (_is_finite(snapshot.monopropellant)
                    and snapshot.monopropellant <= BDOCK_MONOPROP_OUT_EPS):
                return (replace(_bdock_flake(st, BDOCK_DOCK), flake_reason=(
                            "phase %s: docking aborted, monopropellant exhausted"
                            % BDOCK_DOCK)),
                        [Action(ACTION_MJ_DISABLE_DOCKING)])
            # Budget backstop (slow-but-alive).
            if _bdock_over_budget(st, snapshot):
                return (replace(_bdock_flake(st), flake_reason=(
                            "phase %s: docking did not complete within budget"
                            % st.phase)),
                        [Action(ACTION_MJ_DISABLE_DOCKING)])
            return st, []

        # ---- AP NOT running (not the deferred-enable frame, not docked). ----
        if not st.docking_ever_enabled:
            # E1a: the enable never took (AP refused / NRE'd on enable). Wait a
            # debounced window, re-emit the enable ONCE, then fast-flake.
            streak = st.dock_enable_wait_streak + 1
            st = replace(st, dock_enable_wait_streak=streak)
            if streak >= BDOCK_DOCK_LIVENESS_K:
                if not st.dock_enable_reissued:
                    return (replace(st, dock_enable_reissued=True,
                                    dock_enable_wait_streak=0),
                            [Action(ACTION_MJ_ENABLE_DOCKING, value=p.dock_speed)])
                return (replace(_bdock_flake(st), flake_reason=(
                            "phase %s: docking AP enable did not take" % st.phase)),
                        [Action(ACTION_MJ_DISABLE_DOCKING)])
            if _bdock_over_budget(st, snapshot):
                return (replace(_bdock_flake(st), flake_reason=(
                            "phase %s: docking AP never enabled within budget"
                            % st.phase)),
                        [Action(ACTION_MJ_DISABLE_DOCKING)])
            return st, []

        # E1b: the AP ran then DIED mid-approach (benched / NRE) without docking.
        # Re-target + re-enable ONCE (a dead AP often drops the port target too);
        # if it dies again, fast-flake -- never idle to the budget with a dead AP.
        streak = st.dock_died_streak + 1
        st = replace(st, dock_died_streak=streak)
        if streak >= BDOCK_DOCK_LIVENESS_K:
            if not st.dock_reenabled_after_death:
                return (replace(st, dock_reenabled_after_death=True,
                                dock_died_streak=0, dock_enable_pending=True),
                        [Action(ACTION_SET_TARGET_DOCKING_PORT)])
            return (replace(_bdock_flake(st), flake_reason=(
                        "phase %s: docking AP disabled without docking "
                        "(benched/NRE?)" % st.phase)),
                    [Action(ACTION_MJ_DISABLE_DOCKING)])
        if _bdock_over_budget(st, snapshot):
            return (replace(_bdock_flake(st), flake_reason=(
                        "phase %s: docking AP idle after death within budget"
                        % st.phase)),
                    [Action(ACTION_MJ_DISABLE_DOCKING)])
        return st, []

    if state.phase == BDOCK_TRANSFER:
        # Sequence: T1 (LiquidFuel deliver) then T2 (MonoPropellant pickup).
        if state.current_transfer_started and snapshot.transfer_complete:
            done_n = state.transfers_done + 1
            st = replace(state, transfers_done=done_n, current_transfer_started=False)
            if done_n >= 2:
                # Both transfers done -> undock (baseline the pre-split count).
                base = snapshot.vessel_count
                return (_bdock_enter(replace(st, undock_baseline_vessel_count=base),
                                     BDOCK_UNDOCK, snapshot.ut),
                        [Action(ACTION_UNDOCK)])
            # Start T2 (MonoPropellant pickup, station -> transport); reset the
            # liveness tracker for the new transfer.
            return (replace(st, current_transfer_started=True,
                            transfer_best_amount=float("-inf"),
                            transfer_noprogress_streak=0),
                    [Action(ACTION_START_RESOURCE_TRANSFER,
                            value=p.transfer_amount_mp, text="MonoPropellant",
                            limit=TRANSFER_DIR_PICKUP)])
        # Liveness watchdog (flight-11): a running transfer must move resource --
        # transfer_amount climbs. Flat/unread for BDOCK_TRANSFER_STALL_FRAMES is a
        # stalled transfer (dry source / full dest) -> fast flake, not an idle to
        # the transfer budget. Fail closed: an unread (NaN) amount is no progress.
        st = state
        if st.current_transfer_started:
            amt = snapshot.transfer_amount
            if _is_finite(amt) and amt > st.transfer_best_amount + 1e-6:
                st = replace(st, transfer_best_amount=amt,
                             transfer_noprogress_streak=0)
            else:
                streak = st.transfer_noprogress_streak + 1
                st = replace(st, transfer_noprogress_streak=streak)
                if streak >= BDOCK_TRANSFER_STALL_FRAMES:
                    return replace(_bdock_flake(st), flake_reason=(
                        "phase %s: transfer stalled (transfer_amount not "
                        "increasing)" % st.phase)), []
        return _bdock_stay_or_flake(st, snapshot), []

    if state.phase == BDOCK_UNDOCK:
        # onVesselsUndocking -> Parsek authors the Undock split + completes the
        # RouteConnectionWindow. Done evidence: vessel_count INCREASED by one
        # AND docking_state != Docked (MINOR 10: Ready alone is soft evidence).
        split = (snapshot.vessel_count > state.undock_baseline_vessel_count
                 and snapshot.docking_state != DOCKING_STATE_DOCKED)
        if split:
            return (_bdock_enter(replace(state, undock_confirmed=True),
                                 BDOCK_TERMINAL, snapshot.ut),
                    [Action(ACTION_CANCEL_WARP)])
        return _bdock_stay_or_flake(state, snapshot), []

    return _bdock_flake(state), []


def evaluate_bdock_assertions(frames, params: BDockParams,
                              phases_reached=(), state=None,
                              k: int = DEFAULT_DEBOUNCE_K) -> List[AssertionOutcome]:
    """Five B-DOCK driver-validity assertions -- terminal-focused phase + carried
    evidence, NEVER a golden trajectory (the rendezvous / dock geometry is
    MechJeb's business; the RECORDING-correctness oracle is the offline
    analyzer's, design section 6). ``state`` carries the docked / undock evidence.

    - ``reachedStationOrbit``:      STATION-ORBIT in phases_reached.
    - ``stationSeparated``:         STATION-SEPARATE completed (the spent core
      dropped) -- the phase was entered AND the machine advanced past it to
      STATION-ORBIT (the flight-3 stage-separation contract).
    - ``reachedInterceptorOrbit``:  INT-PHASING-ORBIT in phases_reached.
    - ``interceptorSeparated``:     INT-SEPARATE completed (entered AND advanced
      to INT-PHASING-ORBIT).
    - ``docked``:                   DOCK reached AND docked_confirmed evidence.
    - ``transfersComplete``:        both commanded transfers completed (evidence
      transfers_done >= 2).
    - ``undocked``:                 the authoritative undock split fired
      (UNDOCK/TERMINAL reached AND undock_confirmed evidence).

    A SEPARATE phase is only entered after its circularize completes and only
    LEFT on a confirmed vessel_count increase, so reaching the phase AFTER it
    (STATION-ORBIT / INT-PHASING-ORBIT) is proof the separation was observed;
    requiring the SEPARATE phase itself in ``phases`` too keeps the row honest if
    the flow is ever reordered (a run that entered SEPARATE but flaked before the
    split reads met=False with value=the SEPARATE phase, naming the stall).
    """
    del frames, k
    phases = tuple(phases_reached or ())
    docked_ev = bool(getattr(state, "docked_confirmed", False))
    transfers = int(getattr(state, "transfers_done", 0))
    undock_ev = bool(getattr(state, "undock_confirmed", False))

    station = AssertionOutcome("reachedStationOrbit",
                               BDOCK_STATION_ORBIT in phases,
                               (BDOCK_STATION_ORBIT if BDOCK_STATION_ORBIT in phases
                                else (phases[-1] if phases else None)),
                               {"required": BDOCK_STATION_ORBIT})
    station_sep = AssertionOutcome(
        "stationSeparated",
        (BDOCK_STATION_SEPARATE in phases) and (BDOCK_STATION_ORBIT in phases),
        (BDOCK_STATION_SEPARATE if BDOCK_STATION_SEPARATE in phases
         else (phases[-1] if phases else None)),
        {"required": BDOCK_STATION_SEPARATE})
    interceptor = AssertionOutcome("reachedInterceptorOrbit",
                                   BDOCK_INT_PHASING_ORBIT in phases,
                                   (BDOCK_INT_PHASING_ORBIT if BDOCK_INT_PHASING_ORBIT in phases
                                    else (phases[-1] if phases else None)),
                                   {"required": BDOCK_INT_PHASING_ORBIT})
    interceptor_sep = AssertionOutcome(
        "interceptorSeparated",
        (BDOCK_INT_SEPARATE in phases) and (BDOCK_INT_PHASING_ORBIT in phases),
        (BDOCK_INT_SEPARATE if BDOCK_INT_SEPARATE in phases
         else (phases[-1] if phases else None)),
        {"required": BDOCK_INT_SEPARATE})
    docked = AssertionOutcome("docked",
                              (BDOCK_DOCK in phases) and docked_ev, docked_ev,
                              {"required": BDOCK_DOCK})
    transfers_met = transfers >= 2
    transfer = AssertionOutcome("transfersComplete", transfers_met, transfers,
                                {"required": 2})
    undocked = AssertionOutcome("undocked",
                                (BDOCK_TERMINAL in phases) and undock_ev, undock_ev,
                                {"required": BDOCK_TERMINAL})
    return [station, station_sep, interceptor, interceptor_sep, docked,
            transfer, undocked]


# ---------------------------------------------------------------------------
# FORGE-LKO phase state machine (mission forge_lko: the ORBITAL fixture forge).
# Pure. The B-DOCK Interceptor-leg shape, truncated at the park:
#
#   PRELAUNCH   -> launch_vessel the craft WITH NAMED CREW onto a clear pad
#   LAUNCH      -> settle PRE_LAUNCH with the crew verified aboard
#   ASCENT      -> the B2 MechJeb ascent actions (autostage drops the boosters)
#   CIRCULARIZE -> execute the circularization node with autowarp EXPLICIT
#   SEPARATE    -> two-step: drop the spent core AND ignite the orbital engine
#   PARK        -> cut throttle, clear nodes, hold attitude, dwell stable
#   ORBIT       -> done MISSION-OK; the scenario's SaveGame stamps the fixture
#
# It reuses the SAME ascent / attitude-hold action builders and the SAME pure
# separation-evidence counter as B-DOCK; only the phase wrapper is new. There is
# no rendezvous / dock / transfer half and no mid-mission commit: a forge
# produces a START STATE, never a trajectory.
# ---------------------------------------------------------------------------

# Phases where a FROZEN-telemetry (destroyed-vessel) stall is a real risk and the
# shared 1x frozen detector applies. LAUNCH is excluded (the launch_vessel reload
# transient); PRELAUNCH / SEPARATE / PARK / ORBIT are not continuous-flight
# phases (SEPARATE and PARK own their own bounded evidence gates, and the
# detector self-gates on warp_mode == NONE anyway).
_FLKO_FROZEN_PHASES: Tuple[str, ...] = (FLKO_ASCENT, FLKO_CIRCULARIZE)


@dataclass(frozen=True)
class ForgeLkoParams:
    """FORGE-LKO tuning (spec [driver.missionParams] for forge_lko). All budgets /
    tolerances / debounce depths, never a golden trajectory."""
    # --- launch (the FORGE crew-by-name plumbing, verbatim) ---
    craft_name: str = "Kerbal X"
    launch_site: str = "LaunchPad"
    # Explicit KERBAL NAMES seeded into the pod. None / empty -> KSP's default
    # crew assignments (crew=[]). By NAME, never a count: kRPC 0.5.4 exposes no
    # roster-enumeration API, only get_kerbal(name) + launch_vessel(crew: List[str]).
    crew_names: Optional[Tuple[str, ...]] = None
    # Minimum kerbals that must read aboard before the ascent is allowed to start
    # (and the crewAboard assertion's floor). 0 DISABLES the gate; any positive
    # value fails CLOSED on the -1 unread sentinel, so a forge whose crew seeding
    # silently failed flakes ON THE PAD instead of stamping an UNCREWED fixture
    # that reds its consumer ten minutes later with a confusing "no-crew".
    min_crew: int = 0
    launch_settle_situations: Tuple[str, ...] = ("PRE_LAUNCH",)
    launch_timeout: float = 300.0
    launch_settle_debounce: int = 3
    # --- orbit (the B2 ascent tolerances, verbatim) ---
    target_apoapsis: float = 100000.0
    target_periapsis: float = 100000.0
    apo_error: float = 10000.0
    peri_error: float = 10000.0
    eccentricity_max: float = 0.02
    inclination_error: float = 5.0
    launch_site_latitude: float = 0.0
    ascent_timeout: float = 900.0
    circularize_timeout: float = 2400.0
    # --- separation (the B-DOCK two-step contract, verbatim) ---
    separation_timeout: float = 120.0
    # --- park (new: the fixture is a SAVED STATE, so it must be settled) ---
    park_situations: Tuple[str, ...] = ("ORBITING",)
    # GAME seconds the stabilized orbit must be HELD before the forge declares
    # the park done: the save must not catch a still-settling ship.
    park_dwell: float = 60.0
    park_timeout: float = 600.0
    park_debounce: int = 3
    # Tumble ceiling (rad/s) the park must hold: with SAS stability-assist + RCS
    # on, a stabilized stage reads ~0. NaN (unread) fails closed -- never counted
    # as stable (SF-2 discipline).
    max_angular_velocity: float = 0.05
    # Periapsis floor (m) the park must clear regardless of the target tolerance:
    # Kerbin's atmosphere ends at 70 km, so a park below this is NOT the
    # "on-rails-safe stable orbit" the fixture contract promises.
    min_safe_periapsis: float = 75000.0
    frozen_sample_limit: int = 10


def forge_lko_params_from_dict(params: Dict) -> ForgeLkoParams:
    params = params or {}
    crew_names = params.get("crewNames", None)
    return ForgeLkoParams(
        craft_name=str(params.get("craftName", "Kerbal X")),
        launch_site=str(params.get("launchSite", "LaunchPad")),
        crew_names=(tuple(str(n) for n in crew_names) if crew_names else None),
        min_crew=int(params.get("minCrew", 0)),
        launch_settle_situations=tuple(
            params.get("launchSettleSituations", ("PRE_LAUNCH",))),
        launch_timeout=float(params.get("launchTimeoutSeconds", 300)),
        launch_settle_debounce=int(params.get("launchSettleDebounceFrames", 3)),
        target_apoapsis=float(params.get("targetApoapsisMeters", 100000)),
        target_periapsis=float(params.get("targetPeriapsisMeters", 100000)),
        apo_error=float(params.get("apoErrorMeters", 10000)),
        peri_error=float(params.get("periErrorMeters", 10000)),
        eccentricity_max=float(params.get("eccentricityMax", 0.02)),
        inclination_error=float(params.get("inclinationErrorDeg", 5.0)),
        launch_site_latitude=float(params.get("launchSiteLatitude", 0.0)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 900)),
        circularize_timeout=float(params.get("circularizeTimeoutSeconds", 2400)),
        separation_timeout=float(params.get("separationTimeoutSeconds", 120)),
        park_situations=tuple(params.get("parkSituations", ("ORBITING",))),
        park_dwell=float(params.get("parkDwellSeconds", 60)),
        park_timeout=float(params.get("parkTimeoutSeconds", 600)),
        park_debounce=int(params.get("parkDebounceFrames", 3)),
        max_angular_velocity=float(params.get("maxAngularVelocityRadPerSec", 0.05)),
        min_safe_periapsis=float(params.get("minSafePeriapsisMeters", 75000)),
        frozen_sample_limit=int(params.get("frozenTelemetrySamples", 10)),
    )


def forge_lko_b2_params(params: ForgeLkoParams) -> B2Params:
    """Project the FORGE-LKO orbit tolerances onto ``B2Params`` so the orbital
    quality assertions are the LIVE-PROVEN ``evaluate_b2_assertions`` verbatim
    (apoapsis / periapsis / eccentricity / inclination within tolerance), not a
    second hand-rolled copy that could drift from it."""
    return B2Params(
        target_apoapsis=params.target_apoapsis,
        target_periapsis=params.target_periapsis,
        apo_error=params.apo_error,
        peri_error=params.peri_error,
        eccentricity_max=params.eccentricity_max,
        inclination_error=params.inclination_error,
        ascent_timeout=params.ascent_timeout,
        circularize_timeout=params.circularize_timeout,
        launch_site_latitude=params.launch_site_latitude,
        frozen_sample_limit=params.frozen_sample_limit,
    )


@dataclass(frozen=True)
class ForgeLkoState:
    params: ForgeLkoParams
    phase: str = FLKO_PRELAUNCH
    phase_entry_ut: float = 0.0
    phases_reached: Tuple[str, ...] = (FLKO_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    flake_reason: Optional[str] = None
    done: bool = False
    loss_reason: Optional[str] = None
    # Shared frozen-telemetry detection (mirrors B2/B5/B-DOCK).
    frozen_sig: Optional[FrozenSignature] = None
    frozen_count: int = 0
    # LAUNCH settle + the crew gate's diagnosis latch (a settle-situation frame
    # was seen but the crew count was short -> the flake NAMES the crew).
    launch_settle_streak: int = 0
    launch_crew_short_seen: bool = False
    # SEPARATE evidence (the shared two-step contract).
    separate_baseline_vessel_count: int = 0
    separate_settle_streak: int = 0
    separate_thrust_streak: int = 0
    separate_split_confirmed: bool = False
    separate_activations: int = 0
    # PARK stability debounce.
    park_stable_streak: int = 0
    # Carried evidence for the evaluator (LATCHED, so the per-phase resets above
    # never erase what the run actually proved).
    split_ever_confirmed: bool = False
    ignition_ever_confirmed: bool = False
    park_ever_stable: bool = False


def forge_lko_initial_state(params: ForgeLkoParams) -> ForgeLkoState:
    return ForgeLkoState(params=params)


def _flko_enter(state: ForgeLkoState, new_phase: str, ut: float,
                **fields) -> ForgeLkoState:
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state, phase=new_phase, phase_entry_ut=entry,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == FLKO_ORBIT), **fields)


def _flko_phase_budget(params: ForgeLkoParams, phase: str) -> Optional[float]:
    if phase == FLKO_LAUNCH:
        return params.launch_timeout
    if phase == FLKO_ASCENT:
        return params.ascent_timeout
    if phase == FLKO_CIRCULARIZE:
        return params.circularize_timeout
    if phase == FLKO_SEPARATE:
        return params.separation_timeout
    if phase == FLKO_PARK:
        return params.park_timeout
    return None


def _flko_over_budget(state: ForgeLkoState, snapshot: TelemetrySnapshot) -> bool:
    budget = _flko_phase_budget(state.params, state.phase)
    if budget is None or not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > budget


def _flko_flake(state: ForgeLkoState,
                reason: Optional[str] = None) -> ForgeLkoState:
    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase,
                   flake_reason=reason, done=True)


def _flko_stay_or_flake(state: ForgeLkoState,
                        snapshot: TelemetrySnapshot) -> ForgeLkoState:
    if _flko_over_budget(state, snapshot):
        return _flko_flake(state)
    return state


def _flko_crew_ok(params: ForgeLkoParams, snapshot: TelemetrySnapshot) -> bool:
    """The crew gate: no floor -> always satisfied; otherwise the read must be a
    real count at/above the floor. The -1 unread sentinel fails CLOSED."""
    if params.min_crew <= 0:
        return True
    return snapshot.crew_count >= params.min_crew


def _flko_park_stable(params: ForgeLkoParams, snapshot: TelemetrySnapshot) -> bool:
    """One PARK frame's stability verdict: an accepted orbital situation, BOTH
    apsides inside their tolerance, a periapsis clear of the atmosphere, and the
    tumble below the ceiling. Every conjunct fails closed on a non-finite read."""
    if snapshot.situation not in params.park_situations:
        return False
    if not (_is_finite(snapshot.apoapsis) and _is_finite(snapshot.periapsis)):
        return False
    if abs(snapshot.apoapsis - params.target_apoapsis) > params.apo_error:
        return False
    if abs(snapshot.periapsis - params.target_periapsis) > params.peri_error:
        return False
    if snapshot.periapsis < params.min_safe_periapsis:
        return False
    return (_is_finite(snapshot.angular_velocity)
            and snapshot.angular_velocity <= params.max_angular_velocity)


def _flko_park_entry_actions() -> List[Action]:
    """The vehicle-configuration contract the SAVE must capture: throttle CUT (the
    fixture never starts mid-burn), every maneuver node CLEARED (no pending burn
    rides into the fixture), and attitude HELD (SAS stability-assist + RCS on) so
    the orbital stage does not tumble after the separation dropped mass."""
    return ([Action(ACTION_CUT_THROTTLE, 0.0),
             Action(ACTION_MJ_ABORT_AND_CLEAR_NODES)]
            + _bdock_attitude_hold_actions())


def forge_lko_decide(state: ForgeLkoState, snapshot: TelemetrySnapshot
                     ) -> Tuple[ForgeLkoState, List[Action]]:
    """Advance the FORGE-LKO machine one frame; return (new_state, actions).

    Terminals: MISSION-OK at FLKO_ORBIT (the assertions then judge the stamped
    state); ASSERT-FAIL on a vessel loss (outside the launch reload) or a frozen-
    telemetry stall; a bounded FLAKE with a NAMED reason on every phase give-up
    (a forge that flakes costs one operator re-run -- a forge that stamps a BAD
    fixture costs every consumer of that fixture)."""
    if state.done:
        return state, []

    # Phase-independent vessel-loss terminal, EXCEPT during the launch_vessel
    # FLIGHT->FLIGHT reload where a vessel_lost read is a transient (the new
    # craft has not materialized yet); LAUNCH owns that with its settle debounce
    # + launch budget.
    if snapshot.vessel_lost and state.phase != FLKO_LAUNCH:
        return replace(
            state, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason="vessel-lost (unreadable after repeated telemetry failures)"), []

    if state.phase in _FLKO_FROZEN_PHASES:
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

    p = state.params

    if state.phase == FLKO_PRELAUNCH:
        return (_flko_enter(state, FLKO_LAUNCH, snapshot.ut),
                [Action(ACTION_LAUNCH_VESSEL, text=p.craft_name,
                        launch_site=p.launch_site, crew=p.crew_names)])

    if state.phase == FLKO_LAUNCH:
        on_pad = (not snapshot.vessel_lost
                  and snapshot.situation in p.launch_settle_situations)
        crew_ok = _flko_crew_ok(p, snapshot)
        streak = state.launch_settle_streak + 1 if (on_pad and crew_ok) else 0
        st = replace(state, launch_settle_streak=streak,
                     launch_crew_short_seen=(state.launch_crew_short_seen
                                             or (on_pad and not crew_ok)))
        if streak >= p.launch_settle_debounce:
            return (_flko_enter(st, FLKO_ASCENT, snapshot.ut),
                    _bdock_ascent_entry_actions(p.target_apoapsis))
        if _flko_over_budget(st, snapshot):
            # Blame the CREW only when the craft was seen settled-but-short AND is
            # STILL short at the give-up: a craft that reached the pad and then
            # stopped reading PRE_LAUNCH is a settle failure, not a crew failure.
            if st.launch_crew_short_seen and not crew_ok:
                return _flko_flake(st, (
                    "phase %s: craft settled on the pad but crew_count=%d is below "
                    "minCrew=%d (launch_vessel crew seeding failed; the fixture "
                    "would be UNCREWED)" % (FLKO_LAUNCH, snapshot.crew_count,
                                            p.min_crew))), []
            return _flko_flake(st, (
                "phase %s: the launched craft never settled in %s"
                % (FLKO_LAUNCH, list(p.launch_settle_situations)))), []
        return st, []

    if state.phase == FLKO_ASCENT:
        apo_reached = (_is_finite(snapshot.apoapsis)
                       and snapshot.apoapsis >= p.target_apoapsis - p.apo_error)
        if snapshot.mj_ascent_complete and apo_reached:
            # ACTION_MJ_EXECUTE_NODES (not the bare circularization action): it is
            # the SAME guarded execute_all_nodes, but it sets node_executor
            # autowarp EXPLICITLY. B-DOCK flight 12 proved the executor's autowarp
            # is shared global state -- an identical machine warped on one flight
            # and coasted the whole leg at 1x on the next. A forge must not depend
            # on that luck.
            return (_flko_enter(state, FLKO_CIRCULARIZE, snapshot.ut),
                    [Action(ACTION_MJ_EXECUTE_NODES)])
        return _flko_stay_or_flake(state, snapshot), []

    if state.phase == FLKO_CIRCULARIZE:
        if (_is_finite(snapshot.periapsis)
                and snapshot.periapsis >= p.target_periapsis - p.peri_error):
            # Circularized -> the two-step SEPARATE contract. This entry
            # ACTIVATE_STAGE (activation count 1) drops the spent core; step 1
            # confirms on the vessel_count increase, step 2 lights the orbital
            # stage with at most ONE more activation (cap 2 -- a third would fire
            # the istg=0 heat-shield decoupler).
            return (_flko_enter(state, FLKO_SEPARATE, snapshot.ut,
                                separate_baseline_vessel_count=snapshot.vessel_count,
                                separate_settle_streak=0,
                                separate_thrust_streak=0,
                                separate_split_confirmed=False,
                                separate_activations=1),
                    [Action(ACTION_ACTIVATE_STAGE)])
        return _flko_stay_or_flake(state, snapshot), []

    if state.phase == FLKO_SEPARATE:
        settle, thrust, split_confirmed, ignited = separation_evidence(
            snapshot.vessel_count, snapshot.available_thrust,
            state.separate_baseline_vessel_count, state.separate_settle_streak,
            state.separate_thrust_streak, state.separate_split_confirmed)
        st = replace(state, separate_settle_streak=settle,
                     separate_thrust_streak=thrust,
                     separate_split_confirmed=split_confirmed,
                     split_ever_confirmed=(state.split_ever_confirmed
                                           or split_confirmed),
                     ignition_ever_confirmed=(state.ignition_ever_confirmed
                                              or (split_confirmed and ignited)))
        if not split_confirmed:
            if _flko_over_budget(st, snapshot):
                return _flko_flake(st, (
                    "phase %s: no separation observed (vessel_count did not "
                    "increase)" % FLKO_SEPARATE)), []
            return st, []
        if ignited:
            # ORBITAL STAGE, engine LIT -> park it: cut throttle, clear nodes,
            # hold attitude.
            return (_flko_enter(st, FLKO_PARK, snapshot.ut,
                                separate_settle_streak=0,
                                separate_thrust_streak=0,
                                separate_split_confirmed=False,
                                separate_activations=0,
                                park_stable_streak=0),
                    _flko_park_entry_actions())
        if st.separate_activations < 2:
            return (replace(st, separate_activations=st.separate_activations + 1),
                    [Action(ACTION_ACTIVATE_STAGE)])
        if _flko_over_budget(st, snapshot):
            return _flko_flake(st, (
                "phase %s: separated but no ignition (available_thrust stayed 0)"
                % FLKO_SEPARATE)), []
        return st, []

    if state.phase == FLKO_PARK:
        stable = _flko_park_stable(p, snapshot)
        streak = state.park_stable_streak + 1 if stable else 0
        st = replace(state, park_stable_streak=streak,
                     park_ever_stable=(state.park_ever_stable
                                       or streak >= p.park_debounce))
        dwelled = (_is_finite(snapshot.ut)
                   and (snapshot.ut - st.phase_entry_ut) >= p.park_dwell)
        if streak >= p.park_debounce and dwelled:
            return _flko_enter(st, FLKO_ORBIT, snapshot.ut), []
        if _flko_over_budget(st, snapshot):
            if st.park_ever_stable:
                return _flko_flake(st, (
                    "phase %s: the orbit stabilized but never HELD stable through "
                    "the %.0f s park dwell" % (FLKO_PARK, p.park_dwell))), []
            return _flko_flake(st, (
                "phase %s: never reached a stable park (situation=%s apo=%.0f "
                "pe=%.0f angVel=%.4f)"
                % (FLKO_PARK, snapshot.situation or "?", snapshot.apoapsis,
                   snapshot.periapsis, snapshot.angular_velocity))), []
        return st, []

    return _flko_flake(state, "phase %s: unreachable state" % state.phase), []


def evaluate_forge_lko_assertions(frames, params: ForgeLkoParams,
                                  phases_reached=(), state=None,
                                  k: int = DEFAULT_DEBOUNCE_K
                                  ) -> List[AssertionOutcome]:
    """FORGE-LKO driver-validity assertions: FOUR forge-specific rows plus the
    four LIVE-PROVEN B2 orbit rows verbatim (apoapsisError / periapsisError /
    eccentricity / inclinationError). The forge produces a STATE, so every
    forge-specific row is phase/carried evidence about that state, never a golden
    trajectory.

    - ``launched``:     FLKO_LAUNCH in phases_reached (launch_vessel fired).
    - ``crewAboard``:   the last finite crew_count is at/above minCrew. Auto-met
      when minCrew is 0 (the gate is off); otherwise the -1 unread sentinel is
      UNMET, so an uncrewed stamp can never read green.
    - ``separated``:    SEPARATE entered AND both steps confirmed (the spent core
      dropped AND the orbital engine lit) AND the machine advanced to PARK.
    - ``parkedStable``: FLKO_ORBIT reached AND the final situation is an accepted
      park situation (the state the SaveGame persists).
    """
    frames = list(frames or [])
    phases = tuple(phases_reached or ())

    launched = AssertionOutcome("launched", FLKO_LAUNCH in phases,
                                (phases[-1] if phases else None),
                                {"required": FLKO_LAUNCH})

    crew_last = None
    for f in frames:
        if int(getattr(f, "crew_count", -1)) >= 0:
            crew_last = int(f.crew_count)
    crew_met = (params.min_crew <= 0
                or (crew_last is not None and crew_last >= params.min_crew))
    crew = AssertionOutcome("crewAboard", crew_met, crew_last,
                            {"minCrew": params.min_crew})

    split_ev = bool(getattr(state, "split_ever_confirmed", False))
    ignition_ev = bool(getattr(state, "ignition_ever_confirmed", False))
    sep_met = ((FLKO_SEPARATE in phases) and (FLKO_PARK in phases)
               and split_ev and ignition_ev)
    separated = AssertionOutcome(
        "separated", sep_met,
        (FLKO_SEPARATE if FLKO_SEPARATE in phases
         else (phases[-1] if phases else None)),
        {"required": FLKO_SEPARATE, "splitConfirmed": split_ev,
         "ignitionConfirmed": ignition_ev})

    final_situation = frames[-1].situation if frames else None
    parked_met = (FLKO_ORBIT in phases) and (final_situation in params.park_situations)
    parked = AssertionOutcome("parkedStable", parked_met, final_situation,
                              {"required": FLKO_ORBIT,
                               "accepted": list(params.park_situations)})

    return ([launched, crew, separated, parked]
            + evaluate_b2_assertions(frames, forge_lko_b2_params(params), k=k))


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


def evaluate_eva4_assertions(frames, params: Eva4Params,
                             state=None) -> List[AssertionOutcome]:
    """Evaluate the three EVA-4 driver-validity assertions. All three are about the
    HANDOFF STATE, not about a trajectory: this mission's product is a craft parked in a
    verified-safe mid-air EVA envelope for the seam to act on.

    - ``apoapsisWindow``: the PEAK apoapsis sits inside apoapsisWindowMeters. Hop sanity
      only (same semantics as B1's): it proves the SRB flew a suborbital hop rather than
      fizzling on the pad or over-shooting into a regime the window was never sized for.
    - ``evaWindowReached``: the machine terminated in EVA-WINDOW. This is the mission's
      actual contract; ``value`` reports the handoff altitude (evidence).
    - ``evaWindowDescentRate``: the |vertical speed| AT the handoff frame is within
      evaMaxDescentRateMps. Redundant with the machine gate by construction, and that is
      the point - it re-states the safety bound in the RESULT JSON so a future window
      re-tune cannot quietly move the exit envelope without the assertion row moving too.
    - ``craftCanopyObserved``: the craft's parachute was OBSERVED at full canopy
      (kRPC ParachuteState Deployed) at least once. Added after flight 1, where the
      chute was commanded, the machine believed it, and the canopy never opened - this
      row is the one that would have said so on its own. ``value`` reports the altitude
      / rate the arm was COMMANDED at, so the result JSON carries both halves of the
      commanded-vs-observed distinction.

    Every assertion reads machine-carried evidence rather than the frame tail, because
    the EVA-WINDOW terminal skips the settle tail (the craft is still descending and the
    seam is waiting).
    """
    frames = list(frames or [])
    lo, hi = params.apoapsis_window
    peak = _peak_finite(frames, lambda f: f.apoapsis)
    apo_met = peak is not None and lo <= peak <= hi
    apo = AssertionOutcome("apoapsisWindow", apo_met,
                           peak if peak is not None else float("nan"),
                           {"window": [lo, hi]})

    reached = getattr(state, "phase", None) == EVA4_EVA_WINDOW
    window_alt = getattr(state, "eva_window_altitude", None)
    window_vs = getattr(state, "eva_window_vertical_speed", None)

    win = AssertionOutcome(
        "evaWindowReached", reached,
        window_alt if window_alt is not None else float("nan"),
        {"altitudeWindow": [params.eva_window_min_alt, params.eva_window_max_alt],
         "terminalPhase": getattr(state, "phase", None)})

    rate_met = (reached and window_vs is not None and _is_finite(window_vs)
                and abs(window_vs) <= params.eva_max_descent_rate)
    rate = AssertionOutcome(
        "evaWindowDescentRate", rate_met,
        window_vs if window_vs is not None else float("nan"),
        {"maxAbsDescentRate": params.eva_max_descent_rate})

    full_seen = bool(getattr(state, "craft_chute_full_seen", False))
    armed_alt = getattr(state, "chute_armed_altitude", None)
    armed_rate = getattr(state, "chute_armed_rate", None)
    canopy = AssertionOutcome(
        "craftCanopyObserved", full_seen,
        armed_alt if armed_alt is not None else float("nan"),
        {"armCommanded": bool(getattr(state, "chute_armed", False)),
         "armCommandedRate": (armed_rate if armed_rate is not None
                              and _is_finite(armed_rate) else None),
         "armMaxRate": params.craft_chute_arm_max_rate,
         "fullDeployAltitude": params.craft_chute_full_deploy_alt})

    return [apo, win, rate, canopy]


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
        # A machine may attach a specific FLAKE reason (e.g. the B-DOCK SEPARATE
        # give-up naming the missing split); otherwise the generic timeout line.
        flake_reason = getattr(machine_state, "flake_reason", None)
        return MISSION_FLAKE, flake_reason or (
            "phase %s timed out" % (machine_state.flake_phase,))
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

# Stock KSP physics (LOW) warp cannot exceed 4x: a PHYSICS-labeled rate above
# this ceiling is by construction the rails-ramp-decay artifact finding 19b
# characterized (TimeWarp.Mode flips to LOW immediately on command while
# CurrentRate still decays from the rails rate).
STOCK_PHYSICS_WARP_CEILING = 4.0


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
        if _is_finite(max_physics_warp) and max_physics_warp > 0.0 \
                and warp_rate <= max_physics_warp + PHYSICS_WARP_RAMP_ALLOWANCE:
            return False
        # PHYSICS-labeled ABOVE the stock physics ceiling (review of the B7
        # branch, MAJOR-1 = the finding-19b insight applied to this guard):
        # commanding a physics flip mid-rails-ramp flips TimeWarp.Mode to LOW
        # immediately while CurrentRate still DECAYS from the rails rate, so
        # kRPC truthfully reports PHYSICS at 4.4-5.3x (flight 7 logged SIX
        # near-flake strikes from exactly this). Stock physics warp cannot
        # exceed 4x, so a rails-allowed mission treats the over-ceiling
        # PHYSICS label as the rails-decay artifact it is; a rails-forbidden
        # mission (B1) still flakes it.
        if allow_rails and warp_rate > STOCK_PHYSICS_WARP_CEILING:
            return False
        return True
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
    # FORGE + B-DOCK carried state (getattr-generic: absent on B1..B7 states, so
    # their machine-state dict/line is unchanged; present only for those runs).
    ("settle_streak", "settleStreak"),
    ("launch_settle_streak", "launchSettleStreak"),
    ("rendezvous_ever_enabled", "rvEnabled"),
    ("docking_ever_enabled", "dockEnabled"),
    ("rendezvous_min_distance", "rvMinDist"),
    ("rendezvous_noprogress_count", "rvNoProgress"),
    ("match_nan_streak", "matchNanStreak"),
    ("match_retarget_done", "matchRetarget"),
    ("dock_nan_streak", "dockNanStreak"),
    ("dock_retarget_count", "dockRetargetCount"),
    ("dock_enable_pending", "dockEnablePending"),
    ("dock_enable_wait_streak", "dockEnableWait"),
    ("dock_enable_reissued", "dockEnableReissued"),
    ("dock_died_streak", "dockDiedStreak"),
    ("dock_reenabled_after_death", "dockReenabled"),
    ("dock_last_progress_ut", "dockLastProgressUt"),
    ("transfers_done", "transfersDone"),
    ("current_transfer_started", "transferStarted"),
    ("transfer_noprogress_streak", "transferNoProgress"),
    ("docked_confirmed", "docked"),
    ("undock_confirmed", "undocked"),
    ("undock_baseline_vessel_count", "undockBaseVessels"),
    ("separate_baseline_vessel_count", "sepBaseVessels"),
    ("separate_settle_streak", "sepSettleStreak"),
    ("separate_thrust_streak", "sepThrustStreak"),
    ("separate_split_confirmed", "sepSplitOk"),
    ("separate_activations", "sepActivations"),
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
    # FORGE + B-DOCK sparse latch/gate flips (getattr-generic; absent elsewhere).
    ("launch_settle_streak", "launchSettleStreak"),
    ("rendezvous_ever_enabled", "rvEnabled"),
    ("docking_ever_enabled", "dockEnabled"),
    ("rendezvous_noprogress_count", "rvNoProgress"),
    # MATCH-VELOCITY / DOCK dropped-target re-acquire + DOCK AP-death liveness
    # latches (sparse flips; the per-frame nan/wait/died streaks stay out of the
    # diff to avoid noise). The re-target count + the two AP-death re-issue latches
    # are the flight-11 fast-fail evidence, loud on change.
    ("match_retarget_done", "matchRetarget"),
    ("dock_retarget_count", "dockRetargetCount"),
    ("dock_enable_pending", "dockEnablePending"),
    ("dock_enable_reissued", "dockEnableReissued"),
    ("dock_reenabled_after_death", "dockReenabled"),
    ("transfers_done", "transfersDone"),
    ("current_transfer_started", "transferStarted"),
    ("docked_confirmed", "docked"),
    ("undock_confirmed", "undocked"),
    # Separation settle / thrust streaks + the split-confirmed latch + the
    # activation count: sparse gate flips bounded by the debounce depth / the
    # hard cap of 2 (mirrors launch_settle_streak above).
    ("separate_settle_streak", "sepSettleStreak"),
    ("separate_thrust_streak", "sepThrustStreak"),
    ("separate_split_confirmed", "sepSplitOk"),
    ("separate_activations", "sepActivations"),
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
        # B-DOCK rendezvous / match diagnosability (flight-5: a MATCH-VELOCITY
        # stall was invisible in the status file without these). NaN -> None.
        "targetDistance": _json_safe(snapshot.target_distance),
        "targetRelSpeed": _json_safe(snapshot.target_rel_speed),
        # Prox-ops observability (flight-10: DOCK was blind to tumble / control /
        # AP-status). NaN -> None; booleans + status string pass through.
        "angularVelocity": _json_safe(snapshot.angular_velocity),
        "sasEnabled": snapshot.sas_enabled,
        "rcsEnabled": snapshot.rcs_enabled,
        "dockingApStatus": snapshot.docking_ap_status,
        # FORGE-LKO crew gate (-1 = not read / read failed). Kept as the raw int
        # (not None-scrubbed): the sentinel IS the diagnosis when a crew gate
        # never opens.
        "crewCount": snapshot.crew_count,
    }


def format_snapshot_compact(snapshot: TelemetrySnapshot) -> str:
    """One-line-per-frame compact snapshot form for the event-window ring
    buffer (design 2c): the fields the machines gate on, ~100 chars.

    ``crew=N`` is appended ONLY when the crew count was actually read (the
    opt-in FORGE-LKO channel), so every other mission's line is unchanged."""
    crew = "" if snapshot.crew_count < 0 else (" crew=%d" % snapshot.crew_count)
    line = ("ut=%s alt=%s ap=%s pe=%s body=%s nodes=%d nodeDv=%s thr=%s "
            "apErr=%s tgtD=%s tgtV=%s angV=%s sas=%d rcs=%d apSt=%s warp=%sx%s "
            "sit=%s%s"
            % (_obs_fmt(snapshot.ut), _obs_fmt(snapshot.altitude),
               _obs_fmt(snapshot.apoapsis), _obs_fmt(snapshot.periapsis),
               snapshot.body or "?", snapshot.node_count,
               _obs_fmt(snapshot.node_dv), _obs_fmt(snapshot.throttle),
               _obs_fmt(snapshot.ap_error), _obs_fmt(snapshot.target_distance),
               _obs_fmt(snapshot.target_rel_speed),
               _obs_fmt(snapshot.angular_velocity),
               1 if snapshot.sas_enabled else 0, 1 if snapshot.rcs_enabled else 0,
               snapshot.docking_ap_status or "?", snapshot.warp_mode,
               _obs_fmt(snapshot.warp_rate), snapshot.situation or "?",
               " LOST" if snapshot.vessel_lost else ""))
    return line + crew
