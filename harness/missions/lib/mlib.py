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
from typing import Dict, List, Optional, Sequence, Tuple

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

# B1 phase names (design "Mission B1: pad-hop"). DOWN is the canopy-borne-impact
# SUCCESS terminal (operator decision 2026-07-20 option A, re-gated 2026-07-25): the
# hop flew, the canopy was OBSERVED open, and the craft reached the ground -- a
# breakup at touchdown is a successful B1 end, because the craft-survives-intact
# contract is owned by the separate B4 mission.
#
# This block used to justify that with "the stock Jumping Flea ALWAYS breaks apart at
# ~9 m/s touchdown vs the booster's 7 m/s tolerance". RETRACTED 2026-07-25: that rate
# was never measured. The chute never opened on any run that produced it, so every
# observed breakup was a ~300 m/s terminal-velocity impact. Whether this craft
# survives a real canopy touchdown is still unmeasured; both LANDED and DOWN are
# accepted ends.
B1_PRELAUNCH = "PRELAUNCH"
B1_ASCENT = "ASCENT"
B1_COAST = "COAST"
B1_DESCENT = "DESCENT"
B1_LANDED = "LANDED"
B1_DOWN = "DOWN"
B1_PHASES: Tuple[str, ...] = (B1_PRELAUNCH, B1_ASCENT, B1_COAST, B1_DESCENT,
                              B1_LANDED, B1_DOWN)

# Consecutive DESCENT frames that must read ParachuteState Deployed before the OBSERVED
# canopy latch (B1State.craft_chute_full_seen) is earned. Sibling of
# EVA4_WINDOW_DEBOUNCE_K and for the same reason: stock flips the state to DEPLOYED at
# the START of the ~8 s canopy animation, and this latch decides a success terminal.
B1_CANOPY_DEBOUNCE_K = 2

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
# The five conjuncts are evaluated on ONE frame, but the TRANSITION is debounced over
# EVA4_WINDOW_DEBOUNCE_K consecutive open frames - see that constant.
#
# ================= FLIGHT-2 EVIDENCE (2026-07-24): the re-tune worked =================
# FULL PASS on attempt 1, all four assertions met, so the numbers below are MEASURED, not
# projected. Profile: peak / orbital apoapsis 19,747 m; the chute armed at 11,965 m and
# -0.43 m/s (the apoapsis crossing, exactly where the arm-while-slow rule aims it) and
# READ SemiDeployed one poll later.
#   THE SEMI-DEPLOYED RATE, the one number flight 1 could not supply: the semi-deployed
#   craft does NOT crawl. It accelerates to a peak sink of -236 m/s at ~5.7 km, then the
#   rate DECAYS with air density (-223 m/s by the 2500 m full-deploy trigger). The full
#   canopy then brakes it from -223 to -23 m/s in ~5.6 s.
#   The whole DESCENT phase (ut 60.9 -> 122.5) took 61.6 s, which is what let
#   descentTimeoutSeconds be trimmed from the provisional 480 back to 240 (~3.9x margin).
# Handoff frame: 1,606 m at -23.2 m/s, inside [700, 2100] / 25. The kerbal then descended
# under its own canopy at a steady -4.5 m/s and landed ALIVE.
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

# Consecutive open frames the EVA window must hold before the machine terminates into it
# (the house K-consecutive debounce idiom, DEFAULT_DEBOUNCE_K). Deliberately K=2 rather
# than the library default 3:
#   WHY DEBOUNCE AT ALL. The handoff is IRREVERSIBLE - the seam's next command pushes a
#   kerbal out of a hatch - and two of the five conjuncts are one-sample reads that can
#   flicker. Stock flips ParachuteState to DEPLOYED at the START of the full-deploy
#   ANIMATION (decompiled ModuleParachute.cs:1372-1380), so for the ~8 s the Mk16 canopy
#   takes to bite, "Deployed" is true while the craft is still fast; the only thing
#   separating that from a real full canopy is the |vertical speed| conjunct, read from
#   the SAME kRPC frame. A single glitched frame (a dropped / stale kRPC read) would
#   therefore certify a terminal-velocity EVA as green, and evaWindowDescentRate cannot
#   catch it because it re-reports that same frame. Two INDEPENDENT frames must agree.
#   WHY K=2 AND NOT 3. The cost is altitude: the measured flight-2 handoff frame was
#   -23.2 m/s on a ~0.5 s poll, so one extra frame spends ~10-20 m of the 1400 m band -
#   negligible - but the band is not free, and the failure this guards is a transient
#   read, which a second agreeing frame already excludes.
# The streak resets to 0 on ANY non-open frame (fail-closed: the run of agreement must be
# unbroken), and the floor / WINDOW-MISSED check is UNCHANGED, so a craft that sinks past
# the floor mid-streak still reds by name instead of handing off late.
EVA4_WINDOW_DEBOUNCE_K = 2

# ---------------------------------------------------------------------------
# CL-1 phase names (mission cl1_pod_impact; scenario CL-1-pod-impact). THE ATOM
# of the crew-loss lane: a crewed pod launches, does NOT deploy a chute, and hits
# the ground. The crew dies. That is the entire mission.
#
# THIS MISSION INVERTS THE SUITE'S VESSEL-LOSS RULE, DELIBERATELY.
# Everywhere else in this library a vessel-lost terminal is a FAILURE, and
# `resolve_flight_verdict` returns MISSION-ASSERT-FAIL on any `loss_reason`
# BEFORE the assertions are evaluated - specifically so a destroyed craft's
# residual telemetry can never satisfy them. Here the death is the SUCCESS
# terminal. It has to be, and not for taste:
#   an UNMET mission drives `hlib.plan_unmet_mission_tail`, which runs the
#   CLEANUP steps only. CommitTree is world-mutating, so it would be SKIPPED,
#   nothing would be committed, and there would be no recording and no ledger
#   state left to check. A scenario whose whole subject is "what does Parsek
#   record when a kerbal dies, and what does the career ledger do about it"
#   cannot reach its own subject through the failure path.
# So CL-1 does NOT route through the loss path at all. CL1_CREW_LOST is its own
# terminal: `done` with NO `loss_reason` and `verdict` left None, so the
# assertions decide OK vs ASSERT-FAIL exactly as they do for B1's LANDED.
#
# WHAT MAKES THE SUCCESS TERMINAL HONEST. It is gated on an OBSERVED kRPC read of
# the KERBAL'S OWN ROSTER STATUS (`TelemetrySnapshot.crew_roster_status`, from
# `SpaceCenter.GetKerbal(name).RosterStatus`), never on the machine's own "we
# watched it fall" latch. That is the documented COMMANDED-vs-OBSERVED defect
# class (autotest-status known-gate 7): B1 shipped four months of green nightlies
# on a chute that never opened because its terminal read a commanded latch. The
# roster is also the RIGHT channel rather than merely an available one - it is a
# property of the KERBAL, not of the vessel, so it survives the destruction of
# the craft that killed them, which `Vessel.CrewCount` does not.
#
# The success terminal additionally requires that the machine OBSERVED the kerbal
# ALIVE AND ABOARD first (`Assigned`, debounced). Without that conjunct a fixture
# whose kerbal was already dead at load would pass instantly and green.
CL1_PRELAUNCH = "PRELAUNCH"
CL1_FLIGHT = "FLIGHT"
CL1_CREW_LOST = "CREW-LOST"
CL1_PHASES: Tuple[str, ...] = (CL1_PRELAUNCH, CL1_FLIGHT, CL1_CREW_LOST)

# Consecutive agreeing roster reads before a roster-status run is acted on. K=2,
# the B1_CANOPY_DEBOUNCE_K / EVA4_WINDOW_DEBOUNCE_K value and for the same
# reason: these runs decide TERMINALS, and a terminal that CERTIFIES (crew lost)
# and one that CONDEMNS (crew survived) both deserve two independent frames.
# Cheap here - the poll is ~0.5 s against a ~120 s hop.
CL1_ROSTER_DEBOUNCE_K = 2

# Consecutive frames the roster channel may read UNREAD ("") before the mission
# gives up as a NAMED FLAKE. The roster read is deliberately independent of the
# active-vessel read (see mission_runner), so an unreadable roster means the kRPC
# channel itself is broken, not that the flight went wrong - which is a flake
# (retryable), not an assert-fail. 6 frames is ~3 s at the standard poll: long
# enough to ride out a transient, short enough that a dead channel is named in
# seconds instead of burning the whole flight budget into an unnamed
# "phase FLIGHT timed out" (the B1 chute-arm-window-missed lesson: a
# deterministic outcome must not be filed in the flake bucket unnamed).
CL1_ROSTER_UNREAD_GIVEUP_FRAMES = 6

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
# --- ORBIT-mission tail (missions b11_mun_orbit / b12_minmus_orbit; the roadmap's
# "Mun/Minmus ORBIT missions"). All four phases are reachable ONLY when
# ``captureEnabled`` is set, so with the flyby defaults the machine is
# byte-identical to the LIVE-PROVEN B5/B6/B7 shape. The lane exists for one
# Parsek surface no flyby reaches: a recording that ENDS parked in a FOREIGN
# SOI and is COMMITTED there (the commit / BG-handoff / terminal-classification
# path for a tree whose terminal state is "in orbit around another body").
#   PLAN-CAPTURE     plan the periapsis circularization inside the target SOI
#   CAPTURE-BURN     the NodeExecutor (autowarp EXPLICIT) flies it
#   PARK             hold a stable, non-tumbling bound orbit for a dwell
#   ORBIT-COMMIT     mid-mission command-seam CommitTree (the B-DOCK route-1 bridge)
#   ORBIT-COMMITTED  the success terminal (done; the assertions judge the state)
B5_PLAN_CAPTURE = "PLAN-CAPTURE"
B5_CAPTURE_BURN = "CAPTURE-BURN"
B5_PARK = "PARK"
B5_ORBIT_COMMIT = "ORBIT-COMMIT"
B5_ORBIT_COMMITTED = "ORBIT-COMMITTED"
# --- LANDING-mission tail (missions b13_mun_landing / b14_minmus_landing; the
# roadmap's "Mun/Minmus LANDING missions"). All four phases are reachable ONLY
# when ``landingEnabled`` is set -- which itself REQUIRES ``captureEnabled``,
# because the only door into DESCENT is the ORBIT lane's PARK dwell -- so with
# the flag off the machine is byte-identical to the LIVE-PROVEN ORBIT shape,
# and with BOTH flags off it is byte-identical to the LIVE-PROVEN flyby shape.
#
# The lane exists for ONE Parsek surface no other scenario reaches: a recording
# that ENDS **LANDED ON ANOTHER BODY**. B11/B12 end parked in ORBIT around a
# foreign body; B1/B4 land on KERBIN. New here: the terminal classification
# ``Landed`` for a foreign-body tree, SURFACE-class TrackSections off Kerbin
# (the classifier's airless Approach -> Surface* path, unreachable on a body
# with an atmosphere), the landing-leg part events, and the landed-vessel ghost
# / playback surface the committed recording then carries.
#   DESCENT           MechJeb LandingAutopilot.LandUntargeted flies it down
#   LANDED-SETTLE     throttle cut, AP released, SAS held, 1x, a held dwell
#   SURFACE-COMMIT    the SAME route-1 mid-mission seam CommitTree B11/B12 use,
#                     fired while LANDED
#   SURFACE-COMMITTED the success terminal (done; the assertions judge it)
B5_DESCENT = "DESCENT"
B5_LANDED_SETTLE = "LANDED-SETTLE"
B5_SURFACE_COMMIT = "SURFACE-COMMIT"
B5_SURFACE_COMMITTED = "SURFACE-COMMITTED"
B5_PHASES: Tuple[str, ...] = (B5_PRELAUNCH, B5_MJ_ASCENT, B5_CIRCULARIZE, B5_ORBIT,
                              B5_PLAN_TRANSFER, B5_TRANSFER_BURN, B5_PLAN_CORRECTION,
                              B5_CORRECTION_BURN, B5_COAST_TO_TARGET, B5_TARGET_FLYBY,
                              B5_RETURN, B5_PLAN_CAPTURE, B5_CAPTURE_BURN, B5_PARK,
                              B5_ORBIT_COMMIT, B5_ORBIT_COMMITTED,
                              B5_DESCENT, B5_LANDED_SETTLE, B5_SURFACE_COMMIT,
                              B5_SURFACE_COMMITTED)

# Phases in which the machine is INSIDE the target SOI, so the min-altitude
# (closest-approach) evidence must keep tracking. For a flyby this is only
# TARGET-FLYBY; for a capture mission the whole in-SOI stay counts, which makes
# the ``flybyPeriapsisFloor`` assertion certify the PARK orbit's periapsis too.
#
# THE LANDING TAIL IS DELIBERATELY ABSENT. ``min_target_altitude`` is the
# ``flybyPeriapsisFloor`` assertion's evidence -- "the sampled track inside the
# target SOI never came closer to the surface than targetPeriapsisFloorMeters"
# -- and a LANDING drives the altitude to ~0 ON PURPOSE. Including DESCENT here
# would make the landing lane's own objective fail its own approach assertion.
# So the floor certifies exactly what it always certified (the arrival pass and
# the PARKED orbit) and stops at the moment the mission stops trying to stay up.
_B5_IN_TARGET_SOI_PHASES: Tuple[str, ...] = (
    B5_TARGET_FLYBY, B5_PLAN_CAPTURE, B5_CAPTURE_BURN, B5_PARK, B5_ORBIT_COMMIT)

# The LANDING tail's phases (used by the in-SOI guard, the named vessel-lost
# reason and the frozen-telemetry exemption below). EMPTY in effect for every
# non-landing mission: none of these phases is reachable without landingEnabled.
_B5_LANDING_PHASES: Tuple[str, ...] = (
    B5_DESCENT, B5_LANDED_SETTLE, B5_SURFACE_COMMIT)

# Phases EXEMPT from the airborne frozen-telemetry vessel-lost detector.
#
# WHY (and why it is landing-only): ``_advance_frozen_count`` declares a vessel
# LOST when (ut, altitude, vertical_speed, apoapsis, periapsis) repeats
# BIT-IDENTICALLY across frozen_sample_limit consecutive 1x frames. That is a
# correct AIRBORNE staleness signal -- its own docstring says so -- but a
# LANDED craft is exactly the case that can legitimately produce it: KSP sleeps
# a settled rigidbody, so surface altitude and vertical speed can read the same
# float forever while UT ticks. LANDED-SETTLE then holds a MULTI-MINUTE 1x
# dwell (the recorded coverage the lane exists for), which is orders of
# magnitude more exposure than any existing scenario has ever given the
# detector while stationary: B1's DOWN and B4's SPLASHDOWN terminals both END
# the machine on the landed frame, so neither ever polls a settled craft.
#
# DESCENT is deliberately NOT exempt (a craft on the way down must still be
# watched); at most ONE frozen frame can slip through it, because the
# DESCENT -> LANDED-SETTLE handoff fires on the FIRST observed landed situation
# with no debounce, against a limit of 10.
_B5_FROZEN_EXEMPT_PHASES: Tuple[str, ...] = (B5_LANDED_SETTLE, B5_SURFACE_COMMIT)

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


def normalize_camera_mode(name) -> str:
    """Normalize a kRPC CameraMode.name (lower_snake, e.g. 'map', 'automatic',
    'iva') to the PascalCase spelling the V1 dwell machine gates on ('Map'), or
    "" for an empty/None read (fail-closed: matches no gate, so a runner that
    does not opt into the camera read can never satisfy the staged-map gate
    with a fabricated value)."""
    if not name:
        return ""
    return "".join(seg.capitalize() for seg in str(name).split("_"))


# The camera mode the V1 map-dwell machine requires OBSERVED before the dwell
# starts: kRPC CameraMode.map, normalized.
CAMERA_MODE_MAP = "Map"


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


# Crew roster status, normalized from kRPC's RosterStatus enum
# (KRPC.SpaceCenter.Services.RosterStatus at the pinned v0.5.4: Available /
# Assigned / Dead / Missing, mirroring stock ProtoCrewMember.RosterStatus). The
# python client lowercases the enum names, so the same PascalCase normalizer the
# chute channel uses applies here.
#
# TWO SENTINELS, and the difference between them is load-bearing:
#   ""            = UNREAD. The read RAISED. Fail-closed: matches no gate, so a
#                   blind frame can neither certify a death nor clear one.
#   "NotInRoster" = OBSERVED ABSENT. `SpaceCenter.GetKerbal(name)` returned null,
#                   i.e. no kerbal of that name is in `CrewRoster.Crew`. That is
#                   a real observation, not a failed one, and it means opposite
#                   things at opposite ends of a flight: BEFORE the kerbal was
#                   ever seen aboard it is a misspelled `crewName` in the spec
#                   (the likeliest authoring error, and otherwise a mystery
#                   budget burn); AFTER it, the kerbal left the roster, which is
#                   a crew loss by any reading.
ROSTER_STATUS_UNREAD = ""
ROSTER_STATUS_NOT_IN_ROSTER = "NotInRoster"
ROSTER_STATUS_AVAILABLE = "Available"
ROSTER_STATUS_ASSIGNED = "Assigned"
ROSTER_STATUS_DEAD = "Dead"
ROSTER_STATUS_MISSING = "Missing"

# The statuses that mean "this kerbal is no longer alive in the roster".
#
# BOTH of Dead and Missing are accepted, and that is a FIXTURE-INDEPENDENCE
# decision rather than a hedge. Stock KSP's post-death status depends on the
# save's `MissingCrewsRespawn` DIFFICULTY flag: with it OFF the kerbal settles at
# Dead; with it ON stock walks Assigned -> Dead -> Missing and settles at
# Missing. Both archived dead-kerbal runs were flown on a fixture carrying
# `MissingCrewsRespawn = True` and show the two-step; the career fixture this
# mission flies carries False and shows only the first step. A machine that
# accepted just one of the two would be pinned to one fixture's difficulty flag.
ROSTER_STATUS_NOT_ALIVE: Tuple[str, ...] = (ROSTER_STATUS_DEAD,
                                            ROSTER_STATUS_MISSING,
                                            ROSTER_STATUS_NOT_IN_ROSTER)


def normalize_roster_status(name) -> str:
    """Normalize a kRPC RosterStatus.name ('assigned', 'dead', ...) to the
    PascalCase spelling the machine gates on, or "" for an empty/None read
    (fail-closed: matches no gate, so an unreadable roster can neither certify a
    crew loss nor clear one)."""
    if not name:
        return ROSTER_STATUS_UNREAD
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
# ARM the per-frame crew roster read on ONE named kerbal (CL-1). text = the
# kerbal's name. Performing it costs NO rpc: the runner just latches the name,
# and from the next poll on it reads SpaceCenter.GetKerbal(name).RosterStatus
# into TelemetrySnapshot.crew_roster_status.
#
# An ACTION rather than a KrpcMissionControl constructor flag (the shape
# read_crew / read_chute / read_landing use) because the read needs a VALUE - the
# kerbal's name - and `MissionSpec.make_control` takes no parameters, so a
# constructor flag could not carry one without changing that signature for every
# mission. Unarmed, the channel stays at its "" UNREAD sentinel and no extra RPC
# is issued, so every other mission's snapshot is byte-identical.
ACTION_SET_ROSTER_WATCH = "set_roster_watch"                # text = kerbal name
ACTION_MJ_SET_TARGET_APOAPSIS = "mj_set_target_apoapsis"   # value = metres
ACTION_MJ_ENABLE_AUTOSTAGE = "mj_enable_autostage"         # value = None
ACTION_MJ_ENGAGE_ASCENT = "mj_engage_ascent"               # value = None
ACTION_MJ_EXECUTE_CIRCULARIZATION = "mj_execute_circularization"  # value = None
# Park round-out trim (park_trim_ecc_max > 0): plan a MechJeb circularize node
# at the next APOAPSIS. Apoapsis, not periapsis, on purpose -- circularizing at
# periapsis DROPS the park to the periapsis radius, and the interplanetary
# lanes deliberately park HIGH so the ejection-window wait is rails-warp legal
# (the stock factor-7 altitude limit). Rounding out at apoapsis can only raise
# the park, so the warp-legality argument survives the trim by construction.
# Same set-then-READ-BACK time-reference contract as ACTION_MJ_PLAN_CAPTURE:
# MechJeb's OperationCircularize TimeSelector is SHARED, PERSISTED state.
ACTION_MJ_PLAN_PARK_TRIM = "mj_plan_park_trim"              # value = None
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
# GENERALIZED mid-mission Parsek command-seam verb (the verb-agnostic sibling of
# ACTION_PARSEK_COMMIT_TREE above). Carries {verb, args, tag}:
#   seam_verb = the seam verb name written to the request channel ("CommitTree",
#               "InvokeRewind", "RecordingState", ...) -- opaque to the runner,
#               which never validates it; the C# dispatcher owns the verb table
#               and answers an unknown verb with REJECTED unknown-command.
#   seam_args = an ORDERED tuple of (key, value) string pairs appended to the
#               command line as `k=v` tokens (a tuple, not a dict, so Action
#               stays a frozen/hashable dataclass -- same precedent as `crew`
#               and `landing_config`).
#   seam_tag  = a short per-command tag. The runner derives the wire command-id
#               as "<reservedId>.<tag>" (see seam_command_id below), so every
#               mid-mission command carries a DISTINCT id: the C# seam skips
#               duplicate ids outright, so re-using the single reserved id for a
#               second command would make that command a silent no-op.
# The outcome rides TelemetrySnapshot.seam_command_result / _tag / _payload.
# ACTION_PARSEK_COMMIT_TREE is deliberately NOT re-expressed in terms of this
# action: it is live-proven by five scenarios (B-DOCK, B11/B12 ORBIT-COMMIT,
# B13/B14 SURFACE-COMMIT) and its wire bytes must not move.
ACTION_PARSEK_SEAM_COMMAND = "parsek_seam_command"         # seam_verb/_args/_tag
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
# monoprop-budget knob, P2). Done evidence is docking_state == "Docked"
# CORROBORATED as a dock to the TARGET: either the read APPEARED during the phase
# (it was not already docked at entry) or the target port is within
# BDOCK_DOCKED_TARGET_DIST_EPS. `Docked` alone is not enough - _read_docking_state
# answers "any port on the active vessel reads docked" (MINOR-5).
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
# ORBIT-mission capture burn (B11/B12): plan the periapsis circularization INSIDE
# the target SOI. The runner drives KRPC.MechJeb's
# maneuver_planner.operation_circularize with TimeSelector.TimeReference =
# Periapsis (pinned source mods/KRPC.MechJeb/Maneuver/OperationCircularize.cs +
# TimeSelector.cs: "To match apoapsis to periapsis, set the time to
# TimeReference.Periapsis"), so MechJeb picks the burn UT and the NodeExecutor's
# own autowarp carries the coast down to it. No target-altitude knob: the capture
# circularizes at WHATEVER arrival periapsis the correction rounds produced, and
# the PARK window (not a golden number) judges the result. Same throw/log/swallow
# contract as every other plan action -- a failed plan leaves node_count at 0 and
# the machine's bounded re-plan cadence owns the retry.
ACTION_MJ_PLAN_CAPTURE = "mj_plan_capture"                 # value = None
# LANDING-mission descent (B13/B14): engage MechJeb's LandingAutopilot in its
# UNTARGETED mode. The runner writes the module's configuration FIRST (touchdown
# speed, DeployGears, DeployChutes, RcsAdjustment -- carried on
# ``Action.landing_config``), sets the NodeExecutor autowarp EXPLICITLY (the
# B-DOCK flight-12 lesson: MechJeb's landing states gate their OWN warp on
# ``Core.Node.Autowarp``, which is shared global state), then calls
# ``LandUntargeted()`` and READS BACK ``LandingAutopilot.Enabled``. The read-back
# is the whole point: "we called LandUntargeted" is a COMMAND, and this lane's
# three sibling defects (the capture executor, the B1 chute, the EVA-4 ladder
# release) were ALL commanded-vs-observed, so the machine supervises the module
# from ``TelemetrySnapshot.landing_ap_enabled`` every poll and never from this
# call having been made.
ACTION_MJ_LAND_UNTARGETED = "mj_land_untargeted"           # landing_config = cfg
# Release MechJeb's LandingAutopilot (``StopLanding()``). Idempotent: MechJeb's
# own FinalDescent step calls StopLanding the frame it observes
# ``Vessel.LandedOrSplashed``, so by LANDED-SETTLE entry this is usually a
# no-op -- which is exactly why it is emitted there anyway. The dwell that
# follows IS the recorded landed coverage, and it must not run with a
# still-attached autopilot holding attitude/thrust users.
ACTION_MJ_STOP_LANDING = "mj_stop_landing"                 # value = None
# --- V1 map-dwell camera staging (design-testing-unified section 6, V1). All
# three are COMMANDS against kRPC's SpaceCenter.Camera surface; the machine
# never trusts them -- the OBSERVED channel is ``TelemetrySnapshot.camera_mode``
# (read back each poll when the control opts in via ``read_camera=True``), the
# same commanded-vs-observed discipline as the landing/chute/executor lanes.
# Staging a DETERMINISTIC map camera is what makes a dwell's rendered frames
# comparable across flights (V3/V4 will consume them); the parity oracle itself
# is camera-independent, so a failed pose write degrades observability, never
# geometry.
ACTION_CAMERA_SET_MAP = "camera_set_map"                   # value = None
ACTION_CAMERA_FOCUS_BODY = "camera_focus_body"             # text = body name
ACTION_CAMERA_SET_POSE = "camera_set_pose"                 # camera_pose = tuple

# ORBIT-mission capture arming debounce (B11/B12): consecutive frames inside the
# target SOI with a finite ABOVE-SURFACE periapsis before PLAN-CAPTURE is
# entered. The SOI-entry frame's conic reads settle over a poll or two, and a
# transient sub-surface periapsis must never arm a capture the impact-certain
# terminal should own instead.
CAPTURE_ARM_DEBOUNCE_FRAMES = 3

# ---------------------------------------------------------------------------
# CAPTURE-mode TARGET-FLYBY "never armed" LIVENESS BOUND (reviewer finding,
# 2026-07-25). THE GAP: with the periapsis clock dark, capture mode has NO
# bound short of the wall reaper.
#
#   - the periapsis-bounded warp (capture_flyby_warp_target) correctly refuses
#     to warp on a non-finite clock, so the phase runs at 1x;
#   - _b5_capture_arm_ready fails closed on the same non-finite read, so
#     PLAN-CAPTURE is never entered;
#   - the impact-certain terminal needs a FINITE sub-surface periapsis, so it
#     cannot fire either;
#   - and the only remaining bound is flybyTimeoutSeconds -- 300,000 GAME
#     seconds on B11, 400,000 on B12. At 1x, inside a 3,000-4,200 wall-second
#     mission budget, that is unreachable by three orders of magnitude.
#
# The reviewer replayed 6,000 polls of a descending in-SOI arrival reading
# periapsis=NaN, time_to_periapsis=NaN: NOT terminated, phase=TARGET-FLYBY,
# actions=[]. A dead actor idling to a generic wall kill is precisely what the
# standing liveness rule forbids -- budgets bound SLOW, watchdogs bound BROKEN.
#
# THE BOUND: N CONSECUTIVE in-target-SOI capture frames on which the arming
# verdict is False -> a NAMED fast-fail that says WHICH of the three failure
# shapes it saw (classify_capture_arm_failure). N is frozen_sample_limit-scale
# (the same order as the blank-body dwell that bounds an unreadable SOI) with
# real margin over the healthy path: a healthy arrival arms in
# CAPTURE_ARM_DEBOUNCE_FRAMES = 3 consecutive polls, so 30 is TEN TIMES the
# healthy run and ~10x the "conic reads settle over a poll or two" window the
# arming debounce itself was sized against. At the ~0.5 s poll cadence the
# whole bound costs ~15 wall-seconds before it names the defect, against the
# ~3,000 s it used to burn. A single ready frame RESETS the run, so a merely
# jittery clock cannot trip it (an endlessly FLAPPING clock that never gets 3
# consecutive ready frames is still owned by the flyby budget; that shape has
# never been observed and a non-consecutive counter would risk the live-proven
# lane for it).
CAPTURE_NEVER_ARMED_FRAMES = 30

# ---------------------------------------------------------------------------
# CAPTURE NODE-UT SANITY TOLERANCE (reviewer finding, 2026-07-25). The
# PLAN-CAPTURE -> CAPTURE-BURN handoff used to accept ANY node_count >= 1 with
# no gate on WHEN the node was, while the runner's circularize planner set
# TimeReference.Periapsis on a SHARED, PERSISTED MechJeb TimeSelector whose
# setter THROWS on a disallowed reference (KRPC.MechJeb Maneuver/TimeSelector.cs
# :120-124). A swallowed throw left MechJeb's inherited currentTimeRef in place
# and the plan was issued anyway -- inherited global state granting a capture at
# an arbitrary UT.
#
# THE GATE: the planned node must sit AT the arrival periapsis, i.e.
#     |node_ut - (ut + time_to_periapsis)| <= this.
#
# The tolerance is DEFENSIVE in both directions. Lower bound on what it must
# admit: the node UT and the periapsis clock are read on the SAME frame, and
# both are read some polls AFTER the plan was issued -- but the plan runs under
# the 10x PLAN hold, so `ut + time_to_periapsis` (an absolute UT) drifts only by
# conic re-solve noise, tens of game seconds at the very worst. Upper bound on
# what it must reject: the plausible WRONG references are apoapsis, an
# altitude crossing, or an X-from-now offset, all of which land tens of minutes
# to hours away on a Mun / Minmus capture orbit. 300 s sits an order of
# magnitude above the drift and an order of magnitude below the cheapest wrong
# answer.
#
# A non-finite time_to_periapsis FAILS CLOSED (the node is refused): with no
# periapsis clock there is no evidence the node is where the mission needs it,
# and a capture is the mission. Both live shells opt into the clock
# (read_periapsis=True), so this costs the healthy path nothing.
CAPTURE_NODE_PERIAPSIS_TOLERANCE_SECONDS = 300.0
# Consecutive off-periapsis frames before the gate fires. Debounced for the
# same reason every other gate here is -- a transient read must not end a live
# mission -- and specifically because the stale-node RE-PLAN path re-enters
# PLAN-CAPTURE with a JUST-CLEARED node that can still read node_count >= 1 for
# a poll or two, with its UT in the past. Cheap: the phase polls under the 10x
# plan hold, ~5 game-s per poll.
CAPTURE_NODE_SANITY_DEBOUNCE_FRAMES = 3

# classify_capture_arm_failure verdicts.
CAPTURE_ARM_READY = "ready"                     # every conjunct satisfied
CAPTURE_ARM_BLIND = "clock-unreadable"          # non-finite periapsis clock
CAPTURE_ARM_SUBSURFACE = "periapsis-subsurface"  # impact trajectory
CAPTURE_ARM_PAST_PERIAPSIS = "past-periapsis"   # nothing left to capture

# ---------------------------------------------------------------------------
# TARGET-FLYBY periapsis bound in CAPTURE mode (B12 flight 3, 2026-07-25).
# THE PROVEN ROOT CAUSE of a capture that armed AFTER periapsis:
#
# In capture mode the flyby suppresses the SOI-EXIT native warp (warping
# toward the exit is warping toward the failure) and falls through to the
# RAILS STAIR, whose factor is floored at flybyWarpFactor and whose distance
# term is (altitude - periapsis). Nothing in it is bounded by the PERIAPSIS
# CLOCK, and two things then compound:
#   1. The COAST -> TARGET-FLYBY transition emits NO warp cleanup, so the
#      craft crosses into the target SOI still running the coast's native
#      warp. B12 flight 3 entered at RAILSx10000: the FIRST flyby poll alone
#      advanced 3,907 game seconds (ut 268,934.5 -> 272,841.1, alt 1,902 km
#      -> 977 km) before any decision could be taken.
#   2. The capture-arming debounce needs CAPTURE_ARM_DEBOUNCE_FRAMES
#      CONSECUTIVE polls, so arming alone cost ~8,000 game seconds -- more
#      than the entire SOI-entry-to-periapsis coast. PLAN-CAPTURE was entered
#      at alt 41,609 m with vertical speed +92 m/s: already CLIMBING AWAY.
# The late burn produced a bound but wildly eccentric 325 x 5.3 km orbit that
# grazes Minmus, correctly rejected by the capture window as an under-burn.
#
# THE CONTRACT: inside the target SOI in capture mode the ONLY legitimate
# warp target is periapsis_ut - CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS, computed
# from the ORBIT's own periapsis clock, never inferred from altitude trends.
# Past that bound there is nothing left to capture on this pass, so the
# machine does not warp at all.
#
# The lead covers, in order: our arming debounce + the PLAN-CAPTURE RPC
# (tens of game seconds on the 10x plan hold), MechJeb's ignition lead
# (_ignitionUT = node.UT - halfBurnTime, ~10-60 s for this class of burn) and
# MechJeb's own 600 s pre-ignition WARPALIGN hold (see
# MJ_EXECUTOR_WARPALIGN_HOLD_SECONDS). Stopping EARLIER than needed only
# costs a little low-warp coast that MechJeb's executor autowarp then flies;
# stopping later loses the pass outright, so the asymmetry is deliberate.
CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS = 900.0

# ---------------------------------------------------------------------------
# CAPTURE-BURN executor supervision (B11 flight 1, 2026-07-24). THE PROVEN
# ROOT CAUSE, cited from source, not inferred:
#
#   MechJeb2 2.15.1.0, MuMech.MechJebModuleNodeExecutor.StateWarpAlign()
#   (ilspycmd decompile of the INSTALLED MechJeb2.dll):
#
#       else if (_ignitionUT - VesselState.time > 600.0)
#       {
#           Core.Attitude.SetAxisControl(pitch: false, yaw: false, roll: false);
#           Core.Warp.WarpToUT(_ignitionUT - 600.0);
#       }
#       else
#       {
#           Core.Warp.MinimumWarp();
#           SetAttitude();
#       }
#
#   CITATION SCOPE: the decompile above is of the INSTALLED PIN, MechJeb2
#   2.15.1.0. The umbrella checkout at mods/MechJeb2 is a NEWER upstream
#   refactor in which StateWarpAlign no longer exists (grep it: zero hits), so
#   this verbatim body CANNOT be re-derived from that source tree. Re-derive it
#   from the installed DLL, or not at all.
#
#   with _ignitionUT = node.UT - halfBurnTime (CalculateIgnitionUT), the
#   WARPALIGN -> LEAD flip at _ignitionUT - LeadTime (default 3 s) and
#   LEAD -> BURN at _ignitionUT. The ELSE arm -- MinimumWarp(), i.e. 1x with
#   the orbit untouched -- is taken on the TIME TERM ALONE, for the FULL 600
#   GAME seconds before ignition, on EVERY node this executor flies.
#
#   (An earlier revision of this block blamed AlignedAndSettled(), claiming a
#   craft that never settles is what sits at 1x. The branch's own evidence
#   refutes it: flight 1 read angV=0.003 -- above the 0.001 rad/s settle bound
#   -- CONTINUOUSLY, on both sides of the transition, so the settle predicate
#   did not change state when the warp dropped; and the warp dropped at exactly
#   ut=20939 == _ignitionUT - 600, which is precisely where the time term
#   flips. Upstream, DetermineState orders `if (_timeToBurn >
#   InitialWarpLeadTime) return INITIAL_WARP` BELOW the settled check, so an
#   unsettled craft does not get routed out of WARPALIGN into a high-warp state
#   either. The corrected reading is STRONGER, not weaker: the 600 s 1x park is
#   unconditional, so this collision is waiting on every capture burn, not only
#   on a craft that fails to settle.)
#
# That is byte-for-byte the "orbit unchanged and static at 1x" signature the
# no-start watchdog fires on, and burn_nostart_seconds defaults to the SAME
# 600.0. Flight 1: warp dropped to 1x at ut=20939, node at ut=21549.027,
# _ignitionUT ~= 21539 (277.0 m/s at ~250 kN, i.e. halfBurnTime ~10 s), and the
# give-up fired at ut=21539.4: 9.6 s before the NODE's own UT, and within half a
# second of _ignitionUT itself -- i.e. at the very instant a healthy executor
# lights the engine, not comfortably before it. TRANSFER-BURN survived the same
# mechanism only by luck: in LKO the craft DID settle after ~55 s at 1x
# (ut 1265 -> 1321) and MechJeb re-warped, so the static run never reached the
# bound. (TRANSFER-BURN also deliberately ignores the no-start signal, so the
# flyby family is untouched by everything below.)
#
# DOCUMENTATION CONSTANT: nothing in the code reads this number, and that is
# deliberate. The disambiguation does NOT rest on any numeric relation between
# burn_nostart_seconds and the hold -- classify_capture_nostart uses the NODE's
# OWN clock (node_ut + CAPTURE_BURN_WINDOW_GRACE_SECONDS), which is correct for
# every value of burn_nostart_seconds, including the schema's 60 s floor. An
# earlier plan was to assert `burn_nostart_seconds >= this` at param build; that
# would have been a NEW hard failure path bounding a collision the node-clock
# guard already closes, on scenarios this branch cannot edit. Pinned by
# MjWarpalignHoldConstantTests instead.
MJ_EXECUTOR_WARPALIGN_HOLD_SECONDS = 600.0
# Grace past the node's OWN UT before "nothing has burned" becomes evidence
# of a dead executor. MechJeb ignites at node.UT - halfBurnTime, i.e. never
# LATER than node.UT, so a no-start verdict raised before node.UT is
# unfounded by construction; the grace covers a slow ship whose ignition
# straddles the node and a poll or two of conic settle. Once it expires the
# static clock (already past its bound) fires on the very next frame, so the
# liveness cost of the whole guard is bounded by
# MJ_EXECUTOR_WARPALIGN_HOLD_SECONDS + CAPTURE_BURN_WINDOW_GRACE_SECONDS,
# never the (hours-long) capture-burn budget.
CAPTURE_BURN_WINDOW_GRACE_SECONDS = 90.0
# The OBSERVED-side liveness bound: consecutive frames reading
# NodeExecutor.Enabled == FALSE with a node still pending. Debounced because
# one dropped RPC must not re-issue a burn command.
CAPTURE_EXECUTOR_DISABLED_DEBOUNCE_FRAMES = 3
# Bounded re-issue of mj_execute_nodes when the executor is OBSERVED down,
# then a distinctly named fast-fail. Never unbounded: a server-side surface
# that refuses to arm must end the phase, not loop on it.
MAX_CAPTURE_EXECUTOR_REISSUES = 2
# Bounded re-plan when the node's own burn window passed unburned (the craft
# arrived LATE at periapsis with a stale node): clear the node, go back to
# PLAN-CAPTURE for a fresh circularize-at-periapsis solve. Capped at one --
# a second missed window is a named fast-fail, never a stale burn.
MAX_CAPTURE_REPLANS = 1

# classify_capture_nostart verdicts.
CAPTURE_NOSTART_HOLD = "hold"          # MechJeb's own pre-ignition WARPALIGN
CAPTURE_NOSTART_REPLAN = "replan"      # window passed unburned, re-plan left
CAPTURE_NOSTART_FLAKE = "flake"        # window passed, nothing left to try

# ---------------------------------------------------------------------------
# CORRECTION-BURN budget anchoring (B12 flight 1, 2026-07-25). THE PROVEN
# ROOT CAUSE of "phase CORRECTION-BURN timed out", and it is a SHARED-machine
# defect, not a B12 one:
#
# The no-1x-coast PR (commit 4219832b6, 2026-07-22) changed the DIY correction
# burner from "aim, then burn NOW" to AIM-THEN-WARP: aim, then natively warp
# to node_ut - nodeArrivalMarginSeconds, re-verify the attitude, then throttle.
# That made the phase's completion time depend on WHERE MECHJEB PUT THE NODE,
# while its budget stayed transferBurnTimeoutSeconds -- 4000 GAME seconds in
# B5 / B6 / B11 / B12 alike -- and the wait itself BURNS that budget, because
# the warp advances game time.
#
# Measured, same machine, same params, two bodies:
#   B11 (Mun, flight 2 PASS)    entry ut 1898.5, node ut 4907.7 -> the aim-warp
#                               ate 2,994 of the 4,000 s budget (75%) and the
#                               burn finished at 4914.7 with ~1,000 s to spare.
#   B12 (Minmus, flight 1 FLAKE) entry ut 475.3, node ut 74,208.3 -> the wait
#                               needs 73,733 s of a 4,000 s budget. 18x over.
#                               Impossible by construction, every single time.
#
# So the Mun family passes on 25% margin and the Minmus family cannot pass at
# all. B6-minmus-flyby shares the machine, the params AND the 4000 s budget,
# and its live-proven flights all predate 4219832b6 -- it is exposed, it has
# simply not been re-flown since.
#
# THE FIX: a GAME-TIME budget is the wrong instrument for a ballistic wait. It
# is also structurally incapable of bounding the failure it was reached for (a
# STALLED warp advances no game time, so a game-time bound never fires on one;
# the runner's own warp-stall watchdog and the mission WALL budget own that).
# So CORRECTION-BURN's budget now bounds the BURN: it is suppressed entirely
# while an aim-then-warp is in flight toward a still-future node, and it
# re-anchors at the warp ARRIVAL -- the same "warp time is not alignment time"
# seam that already re-anchors corr_nostart_anchor_ut and the aligned streak.
# Post-anchor the phase needs ~25 game-s (B11 round 1: arrival 4892.8 -> exit
# 4914.7) against a 4,000 s budget, so the bound is 100x+ generous for BOTH
# bodies instead of 1.3x for one and negative for the other.

# ---------------------------------------------------------------------------
# COAST-TO-TARGET native-warp LATCH (B12 flight 2, 2026-07-25). THE PROVEN
# ROOT CAUSE of a Minmus coast that crawled at ~2.7x and ate the whole wall
# budget, and it is a SHARED-machine defect:
#
# The coast derives its native warp target from a DERIVED OBSERVATION every
# single poll --
#     native_target = ut + time_to_soi - soi_lead
# -- and when `time_to_soi` reads NaN the branch falls through to the rails
# fallback, which then CANCELS the armed native warp (never two warp writers
# in one frame). But `Orbit.TimeToSOIChange` is unreadable while KSP is
# re-patching conics under a warp RAMP, so the cancel destroys the very
# observation the command depends on, and the next (unwarped) poll re-reads it
# finite and re-arms. Measured over B12 flight 2's COAST frames:
#
#              warp=NONE   warp=RAILS
#   tts finite     2451            7
#   tts NaN           0         1154
#
# 3,603 warp_to_ut issues, 3,602 cancels, 78,568 frames at exactly 1x, and the
# rails rate never escaped ~2.7x because every ramp was cancelled before it
# could climb. The loop is METASTABLE, which is why it had never been seen:
# B11 flight 2's Mun coast issued the warp ONCE, its first post-issue read
# happened to be finite (30/30 warping frames finite), the warp locked in at
# RAILSx1000 and the coast flew. B12 hit a NaN inside the first ramp and never
# escaped. The B5 no-1x certification cannot see this shape either: the 1x is
# interleaved frame-by-frame with 2.7x rails, so no contiguous 1x window
# exists for the audit's >= 30 wall-s violation rule to catch.
#
# THE FIX: the native warp target is an ABSOLUTE UT. Once armed it does not
# need `time_to_soi` to stay readable -- so a BLIND read while the game is
# warping HOLDS the command instead of revoking it. Only a readable frame may
# retarget (through the existing asymmetric hysteresis), and only a blind read
# with the game NOT warping is evidence that the encounter is actually gone.
#
# ---------------------------------------------------------------------------
# NATIVE-WARP THRASH WATCHDOG (widened by the 2026-07-25 review). The original
# guard counted ONE of the FOUR _b5_native_warp call sites (COAST) and counted
# it PER MISSION. Both were wrong:
#
#   - the metastability proven on B12 flight 2 is a property of the SHARED
#     _b5_native_warp primitive (cancel / re-arm within one poll), not of the
#     coast branch, so the CORRECTION-BURN aim-warp (two call sites) and the
#     TARGET-FLYBY capture warp had NO thrash bound at all. Worse, the B12
#     flight-1 fix suppresses the CORRECTION-BURN phase BUDGET entirely while
#     the aim-warp is in flight, and neither mechanism named as covering that
#     class actually does: the runner's stall watchdog only fires when UT
#     FREEZES for 10 wall-s (a crawling warp advances UT), and the mission wall
#     budget is the un-named generic reaper. A 73,733 game-second aim-warp
#     ramping at ~2.7x had literally nothing bounding it.
#   - a PER-MISSION counter bounds the wrong thing. The failure it describes is
#     a SINGLE episode fighting itself; a coast legitimately re-arms once per
#     re-entry (once per correction round), and B7's heliocentric coast runs an
#     asymmetric retarget hysteresis with an absolute 120 s floor against
#     multi-million-second spans over 2+ rounds whose healthy issue count has
#     never been measured. Resetting the counter on every PHASE ENTRY bounds
#     the episode and removes that false-flake risk.
#
# WHAT THE PER-PHASE RESET IS AND IS NOT (corrected by the 2026-07-26 review;
# the earlier wording claimed the reset made the whole change safe, which does
# not follow). At the COAST site the reset is strictly a RELAXATION of the
# shipped bound -- per-episode >= per-mission -- so that site genuinely cannot
# red where it did not before. At the OTHER three sites (both CORRECTION-BURN
# aim-warps and TARGET-FLYBY) the guard is NEW: those sites had no thrash bound
# at all, so B5/B6/B7 face a bound there they never faced. The relaxation
# argument says nothing about them. What makes THOSE sites safe is the MEASURED
# HEADROOM below, not the reset.
#
# THE MEASUREMENT (the actual safety argument). `action warp_to_ut` counted per
# phase over the four HEAD flyby logs re-flown 2026-07-25 -- B11 (1,271 s),
# B12 (581 s), B5 (469 s), B6 (359 s):
#
#   COAST-TO-TARGET   1 / 1 / 1 / 1        (3 warp_to_ut per MISSION in total)
#   CORRECTION-BURN   1 / 1 / 1 / 1
#   TARGET-FLYBY      1 / 1 / 1 / 1
#
# against a cap of 500. Three orders of magnitude of headroom at every site,
# and the pathological coast that motivated the guard issued 3,603 in ONE
# phase (B12 flight 2). Structural corroboration, per site: the aim-warp
# target is the node's FIXED UT, so it never retargets and can only re-issue
# through the 30-game-second self-heal; the CAPTURE-mode flyby target
# (ut + ttPe - lead) is constant along the approach, though that site barely
# runs at all because arming completes in CAPTURE_ARM_DEBOUNCE_FRAMES = 3
# polls. The TARGET-FLYBY site that actually carries B5/B6/B7 traffic is the
# NON-capture branch (the SOI-EXIT native warp), whose target derives from the
# same jittery `time_to_soi` read that produced B12 flight 2's thrash -- which
# is exactly why it needed a bound, and why the measured 1-per-phase on all
# four flights is the number that matters here.
MAX_PHASE_WARP_ISSUES = 500

# The distinct thrash names, one per call site (the standing rule: every
# give-up says which actor was provably dead, and "which warp" is the first
# question an operator asks).
WARP_THRASH_COAST = "coast-warp-thrash"
WARP_THRASH_CORRECTION_AIM = "correction-aim-warp-thrash"
WARP_THRASH_FLYBY = "flyby-warp-thrash"

# ---------------------------------------------------------------------------
# NATIVE-WARP LIVENESS FLOOR (same review). The thrash counter bounds a warp
# that is BEING RE-ISSUED; it says nothing about a warp that was armed ONCE and
# is simply not moving. `warpUtilisation.gameSecondsPerWallSecond` was already
# computed for exactly this shape and nothing consumed it as a give-up.
#
# WHICH SHAPE THIS ACTUALLY BOUNDS (corrected 2026-07-26 from flight 2's own
# gate lines; the earlier wording here implied this floor would have caught B12
# flight 2, and it would NOT have). Flight 2's coast CANCELLED the command every
# other frame -- 3,603 `warp_to_ut` against 3,602 `cancel_warp`, with the
# `gate warpToCmd <target>->none` / `none-><target>` pair alternating frame by
# frame -- and the fly loop resets the liveness episode the moment
# `warp_to_cmd` clears. That episode never lasted two frames, so this floor
# could not have accumulated a judging window regardless of how it was tuned.
# The THRASH counter is what bounds that shape.
#
# What this floor bounds is the POST-FIX RESIDUAL. `coast_native_warp_hold`
# removed the cancel half of flight 2's cycle (a blind read UNDER WARP now HOLDS
# the armed command), so the command stays armed continuously. Flight 2's OTHER
# half -- a rails rate that never escaped 2.76x while the game reported a live
# warp -- is untouched by that fix. Held command + crawling rate is an episode
# that accumulates without bound while advancing almost no game time, and
# nothing else in the stack can see it: the runner's warp-stall watchdog needs
# UT to FREEZE (a crawl advances it), and a GAME-time phase budget is either
# advanced by the crawl or, at CORRECTION-BURN, suppressed outright during an
# aim-warp. That shape has never been flown; it is reachable, it is what this
# floor is for, and it is covered by driving the REAL b5 machine over flight 2's
# post-fix telemetry through fly_loop (test_shells.WarpLivenessRealMachineTests).
#
# THE RATIO, MEASURED -- AND WHICH RATIO. Read the paragraph above carefully:
# this floor does NOT consume `warpUtilisation.gameSecondsPerWallSecond`, which
# is a PER-PHASE average. It computes its own EPISODE-LOCAL ratio in the fly
# loop, from the frame the command armed. On flight 2 those two numbers differ
# by a factor of 27 and only one of them can name the defect:
#
#   PHASE ratio, COAST-TO-TARGET   151,763 game-s / ~3,890 wall-s = ~39
#   EPISODE ratio, the thrash      1.41
#
# The phase average is ~39 because ONE successful warp burst (ut 74,241.8 ->
# 220,311.6, 146,070 game-seconds in 7 frames) precedes the thrash inside the
# same phase and dominates the mean. At ~39 a 5.0 floor could never fire, which
# is exactly why the phase metric is a MARKER and this is a GIVE-UP: the marker
# cannot separate flight 2's coast from a healthy one, the episode ratio names
# it instantly. The episode number is measured off flight 2's MACHINE-STATE
# lines, emitted on a >= 5.0 wall-second cadence
# (MACHINE_STATE_INTERVAL_SECONDS). RE-MEASURED 2026-07-26 over the 724
# final-coast machine-state lines of
# results/2026-07-25_0103_B12-minmus-orbit_mission.stdout.log (the game clock
# each line carries is lastWarpIssueUt -- the thrash re-issues every frame):
# 723 deltas, MEDIAN 7.105 game-s, mean 7.071, full span 3.813 to 7.381, and
# 275 of the 723 fall in the 7.05-7.13 band the earlier wording quoted as if it
# were the whole range. 1.41 is median/cadence and survives on the mean too;
# the coast itself is 7,204 frames, ut 220,311.6 -> 225,991.7 (5,680 game-s).
# So 5.0 sits ~3.5x above the measured defect and below the cheapest healthy
# warp. Do not "simplify" this to read the warpUtilisation row.
#
# It is armed ONLY while the machine has a NATIVE warp command outstanding
# (state.warp_to_cmd is not None), so a deliberate 1x phase -- B5's PARK dwell,
# every B1/B2/B4 phase, the whole FORGE / B-DOCK family -- can never trip it.
# The minimum window covers the rails ramp and any short warp that completes
# before it can be judged: a warp whose whole episode is under
# WARP_LIVENESS_MIN_WALL_SECONDS is never judged at all.
WARP_LIVENESS_MIN_RATIO = 5.0

# THE WINDOW, MEASURED (this replaces the 2026-07-26 PROVISIONAL note). 180.0
# was picked round; it is now ANCHORED rather than re-tuned, and the VALUE IS
# UNCHANGED, so no frame any flown mission took can move. Over all 118 archived
# per-phase `warpUtilisation` rows that issued a warp command in a
# NATIVE-arming phase, the longest wall-clock window is COAST-TO-TARGET at
# 76.4 s (B7, 2026-07-25), then CORRECTION-BURN 69.6 s, TARGET-FLYBY 30.2 s,
# PLAN-CORRECTION 3.7 s, PLAN-CAPTURE 0.6 s. 180.0 is 2.36x that measured
# healthy maximum: not larger because it does not need to be, not smaller
# because the maximum comes from ONE lane (B7's heliocentric coast) and an
# unflown lane may legitimately warp longer.
#
# WHAT THE WINDOW IS NOT, and must never be sold as: the margin protecting the
# long deliberate 1x holds. MEASURED, 31 archived phase rows across SEVEN phase
# names run PAST 180 wall-seconds at a ratio BELOW the 5.0 floor -- REENTRY
# 428.4 s @ 1.45, DEORBIT 349.8 s @ 1.00, DOCK 247.1 s @ 1.00, MJ-ASCENT
# 198.5-199.3 s @ 1.33 (17 rows), INT-ASCENT 194.6 s @ 1.55, STATION-ASCENT
# 194.3 s @ 1.83, PARK 180.2-180.6 s @ 1.00 (9 rows) -- and CAPTURE-BURN has
# been measured at 138.0 s @ 1.10, only 42 seconds short of being judged. Every
# one of those would FIRE if `warp_to_cmd` were ever left armed across them.
# Nothing but the DISARM keeps them safe: CAPTURE-BURN reads warpCommands=0 on
# all ten archived captures because `_b5_enter_plan_capture` clears the command,
# and the PARK entry clears it again on the way in. Those two clears are
# load-bearing safety, not bookkeeping -- pinned by
# test_mlib.WarpLivenessFloorTests so a later edit cannot quietly re-arm them.
#
# FIELD STATUS: the floor has never fired on an archived flight, and that is the
# CORRECT state, not a coverage debt. Every healthy armed episode we have flown
# is 0.5-76.4 wall-seconds and finishes far inside the window; the shape the
# floor bounds is unhealthy by construction and reaching it in the field would
# mean reintroducing the defect. Read a live `warp-liveness-starved` verdict
# against the episode's actual utilisation in the result JSON before acting on
# it, the same as any other first-in-the-field terminal.
WARP_LIVENESS_MIN_WALL_SECONDS = 180.0
WARP_LIVENESS_GIVEUP = "warp-liveness-starved"

# classify_correction_timeout verdicts: the NAMED CORRECTION-BURN give-ups
# (the standing rule -- an actor-dependent phase may never idle to its budget
# without saying which actor was dead). B12 flight 1 rode the GENERIC
# "phase CORRECTION-BURN timed out" and that is the gap these close.
CORR_TIMEOUT_NO_START = "correction-burner-no-start"
CORR_TIMEOUT_INCOMPLETE = "correction-burn-incomplete"

# corr_giveup values: why a correction ROUND ended. Carried on the machine
# state (and diffed, so every round give-up emits its own loud gate line)
# because a round exit is otherwise indistinguishable from a clean one in the
# log -- B12 flight 1's whole diagnosis hinged on knowing WHICH exit fired.
CORR_GIVEUP_NONE = ""
CORR_GIVEUP_NODE_GONE = "node-gone"        # the node vanished under us
CORR_GIVEUP_CUT = "cut-reached"            # remaining dv hit correctionCutDv
CORR_GIVEUP_OVERSHOOT = "overshoot"        # remaining dv started RISING
CORR_GIVEUP_NO_PROGRESS = "no-progress"    # throttle up, dv frozen
CORR_GIVEUP_ALIGN_NO_START = "align-no-start"   # alignment never converged

# classify_capture_executor verdicts.
CAPTURE_EXEC_RUNNING = "running"       # observed enabled (or nothing to judge)
CAPTURE_EXEC_UNKNOWN = "unknown"       # channel unread: no evidence either way
CAPTURE_EXEC_REISSUE = "reissue"       # observed down, re-issue budget left
CAPTURE_EXEC_DEAD = "dead"             # observed down past the re-issue cap

# ---------------------------------------------------------------------------
# LANDING-mission (B13/B14) DESCENT liveness. The standing rule -- budgets bound
# SLOW, watchdogs bound BROKEN, and every give-up gets a DISTINCT NAME -- applied
# to a phase whose actor is a MechJeb module we do not own.
#
# THE DEFAULT FAILURE MODE HERE IS COMMANDED-VS-OBSERVED, not an edge case. Three
# independent defects in this suite were exactly that shape (the CAPTURE-BURN
# NodeExecutor that was commanded and never verified; B1's chute, whose DOWN
# terminal checked the COMMANDED arm latch on a flight where the canopy never
# opened; EVA-4's ladder release), so the descent supervises
# ``LandingAutopilot.Enabled`` as an OBSERVED channel from the first poll.
#
# SURFACE (verified against the INSTALLED pin, not inferred): the automation
# instance's ``GameData/kRPC/KRPC.MechJeb.json`` exports
# ``LandingAutopilot_get_Enabled`` / ``_set_Enabled``, ``_get_Status``,
# ``LandUntargeted``, ``StopLanding``, ``_get/set_TouchdownSpeed``,
# ``_DeployGears``, ``_DeployChutes``, ``_RcsAdjustment``, ``_LimitGearsStage``.
# ``Enabled`` is the inherited ``MuMech.ComputerModule.Enabled``
# (KRPC.MechJeb ComputerModule.cs: ``AutopilotModule : KRPCComputerModule``
# re-exposes it as a KRPCProperty), so the OBSERVED channel EXISTS and no
# derived proxy is needed for the "did it engage" question. The altitude-trend
# proxy is still carried, but as a SEPARATE watchdog for a DIFFERENT failure
# (an autopilot that is enabled and doing nothing useful) -- see
# ``landing_progress_verdict``.
LANDING_AP_DISABLED_DEBOUNCE_FRAMES = 3
# Bounded re-issue of mj_land_untargeted when the module is OBSERVED down, then
# a distinctly named fast-fail. Never unbounded, for the CAPTURE_EXEC reason: a
# server-side surface that refuses to arm must END the phase, not loop on it.
MAX_LANDING_AP_REISSUES = 2

# classify_landing_autopilot verdicts (mirrors classify_capture_executor).
LANDING_AP_RUNNING = "running"     # observed enabled, or already touched down
LANDING_AP_UNKNOWN = "unknown"     # channel unread: no evidence either way
LANDING_AP_REISSUE = "reissue"     # observed down, re-issue budget left
LANDING_AP_DEAD = "dead"           # observed down past the re-issue cap

# Consecutive DESCENT frames that must PROVE no progress before
# `landing-no-progress` fires. Every other liveness gate in this machine is
# debounced (AP-down 3, capture-executor-down 3, capture-arm 3, park-stable 3,
# landed-stable 3, impact-certain 5) and this one was NOT: it flaked on the
# FIRST frame past the window.
#
# EIGHT, not the house 3, and the floor under it is MEASURED rather than
# picked. B14 flight 1's archived telemetry
# (`harness/results/2026-07-25_1543_B14-minmus-landing_mission.stdout.log`)
# shows MechJeb's Minmus FinalDescent hopping and settling: 23 of the 1,330
# DESCENT frames read a vertical speed >= 0 (the craft genuinely GAINS
# altitude, peak +1.305 m/s), and the LONGEST CONSECUTIVE run of them is FIVE
# (ut 278,489.7 -> 278,493.8, alt climbing 92.711 -> 96.370 m), with six
# further runs of three. A HEALTHY landing therefore produces five consecutive
# non-descending frames, so any depth <= 5 would still name it "provably not
# descending" whenever the window happens to be elapsed with an anchor just
# above the disarm band (min_drop < ref < min_drop + a few hundred metres, the
# one geometry the band disarm does not cover). 8 is 1.6x the measured worst
# healthy run; the cost is 3 extra polls, ~3 game seconds against a 900 s
# window. Pinned by LandingStallDebounceDepthTests, which replays those five
# measured frames.
LANDING_STALL_DEBOUNCE_FRAMES = 8

# landing_progress_verdict verdicts.
LANDING_PROGRESS_PENDING = "pending"      # the window has not elapsed yet
LANDING_PROGRESS_OK = "descending"        # the window's drop was delivered
# The ANCHOR sits below min_drop metres AGL, so the window demands a drop that
# does not exist below the craft. DISARMED: the give-up is withheld outright and
# `descentTimeoutSeconds` owns the phase, because no reading of the altitude
# channel in that band can distinguish a slow lander from a stuck one. See the
# reasoning in ``landing_progress_verdict``.
LANDING_PROGRESS_UNSATISFIABLE = "window-unsatisfiable-agl"
# The window under-delivered, but the craft is MEASURABLY descending on the
# independent vertical-speed channel. HOLD, do not flake and do not re-anchor:
# a craft with a finite NEGATIVE vertical speed is not stalled by any reading of
# the word, so the altitude-drop window has no business calling it one. This is
# a CORROBORATION channel, not the near-ground guard -- see
# ``landing_progress_verdict``, which quotes the flight frames that prove the
# vertical-speed channel is NOT reliably negative near the ground.
LANDING_PROGRESS_VSPEED = "descending-under-drop"
LANDING_STALL_BLIND = "altitude-unreadable"    # cannot prove progress
LANDING_STALL_FLAT = "altitude-not-decreasing"  # proved NO progress

# The NAMED landing give-ups. Every one of them says which actor was provably
# dead (or which observation was missing), so an operator never reads a generic
# "phase DESCENT timed out" for a lane whose whole objective is the descent.
LANDING_GIVEUP_AP_NOT_ENABLED = "landing-autopilot-not-enabled"
LANDING_GIVEUP_NO_PROGRESS = "landing-no-progress"
LANDING_GIVEUP_TOUCHDOWN_TIMEOUT = "landing-touchdown-timeout"
LANDING_GIVEUP_NEVER_STABLE = "landed-never-stable"
LANDING_GIVEUP_VESSEL_LOST = "landing-vessel-lost"

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


# Park round-out trim (park_trim_ecc_max). Bounded attempts: MechJeb's
# DeltaVToCircularize is closed-form, so one node should do it and two is
# already generous. A third would be evidence of something the machine cannot
# fix by asking again, which is what the named flake is for.
PARK_TRIM_MAX_ATTEMPTS = 2

# Verdicts of park_trim_verdict.
PARK_TRIM_OFF = "off"          # the param is not armed -- no trim exists
PARK_TRIM_OK = "ok"            # the park is round enough; proceed
PARK_TRIM_PLAN = "plan"        # ask MechJeb for a circularize-at-apoapsis node
PARK_TRIM_EXECUTE = "execute"  # a node is on the board; hand it to the executor
PARK_TRIM_WAIT = "wait"        # burning / settling -- hold, the budget bounds it
PARK_TRIM_GIVEUP = "giveup"    # attempts exhausted and still out of round


def park_trim_verdict(ecc_max: float, eccentricity: float, node_count: int,
                      attempts: int, execs: int,
                      max_attempts: int = PARK_TRIM_MAX_ATTEMPTS) -> str:
    """Decide the next park round-out step. Pure; primitives in, verdict out.

    WHY A ROUND PARK IS A REQUIREMENT AND NOT A PREFERENCE. MechJeb's
    interplanetary ejection planner sizes the burn for the WRONG RADIUS on an
    eccentric parking orbit. Decompiled from the INSTALLED harness binary --
    `automation/stock-minimal/GameData/MechJeb2/Plugins/MechJeb2.dll`, file
    version 2.15.1.0, the pin in `harness/provision/pins.toml` -- NOT from the
    `mods/MechJeb2` source checkout, which tracks a later refactor that no
    longer contains this method under this name. Anyone re-checking this must
    decompile the DLL.
    `OrbitalManeuverCalculator.DeltaVAndTimeForInterplanetaryTransferEjection`:
    it computes the required post-burn SPEED as

        v_eject = sqrt(2 * (soiExitEnergy + mu / o.semiMajorAxis))

    -- at the parking orbit's SEMI-MAJOR AXIS -- and then applies it at
    `burnUT`, which its own ejection geometry places at whatever true anomaly
    makes the escape asymptote point the right way. On a CIRCULAR park r ==
    sma and the sizing is exact. On an eccentric one it is not, and the
    achieved hyperbolic excess comes out as

        v_inf^2 = v_ideal^2 - 2*mu/SOI + 2*mu/sma - 2*mu/r_burn

    Near escape velocity that third-and-fourth-term error is brutal: it is a
    difference of two large reciprocals scaled by 2*mu, so a park that is only
    slightly out of round can eat most of the C3 budget.

    MEASURED, and ONLY measured numbers here (an earlier draft of this
    paragraph quoted a 769.6 m/s "correct ejection" that is in no archive and
    does not reproduce). Flight 3 planned the Eve ejection from a
    562.354 x 778.184 km park (ecc 0.08495) and MechJeb priced it at
    652.843 m/s. Flight 5 -- same planner, same target, same window class, but
    from a ROUND 778.177 x 778.201 km park -- priced the SAME ejection at
    775.873 m/s. That 123.0 m/s is the whole defect: flight 3's heliocentric
    perihelion came out at 12,389,067,761 m against Eve's
    9,734,357,699 - 9,931,011,389 m orbit, a 2.46e9 m miss, and no encounter
    was ever predicted.

    DERIVED, to show the shortfall really is the sma-vs-r_burn term. State the
    assumption up front: `r_burn` is NOT recorded, so the arithmetic below
    takes the burn at PERIAPSIS, which is where the term is largest -- treat
    the achieved figure as a lower bound on the shortfall, not a measurement.
    A Kerbin -> Eve Hohmann needs 778.997 m/s of SOI-EXIT velocity, i.e. a
    post-burn specific energy of 261,454 J/kg (v_inf at infinity 723.1 m/s).
    From a round park at flight 3's own apoapsis radius the formula above
    prices that at 775.75 m/s -- flight 5's MEASURED 775.873 to within
    0.12 m/s, which is the check that the model is the right one. Flight 3's
    652.843 m/s applied at the periapsis of its ecc-0.085 park leaves
    8,310 J/kg: v_inf 128.9 m/s where 723.1 was wanted (SOI-exit 317.1 m/s
    where 779.0 was wanted). Quote the two v_inf figures as a PAIR or neither
    -- 779 is an SOI-exit number and 129 an at-infinity one, and comparing
    them across conventions is what made the original line unreproducible.

    MechJeb warns about this itself, but only above ecc 0.2
    ("#MechJeb_transfer_errormsg3"), which is far too loose for a low-C3
    transfer.

    Verdicts:
    THE ECCENTRICITY IS ONLY READ WHEN NO NODE IS PENDING, and B15 flight 4
    is why. The first shape of this function tested eccentricity FIRST, and a
    circularize-at-apoapsis burn sweeps the orbit CONTINUOUSLY from its starting
    eccentricity down through zero. The gate therefore fired MID-BURN, on a
    frame where the instantaneous reading happened to be inside the window
    (0.085 -> 0.044 -> under 0.02 across three polls), and CIRCULARIZE exited
    with a live node still on the board. ORBIT then planned the ejection, and
    TRANSFER-BURN's `execute_all_nodes` re-burned the leftover trim node,
    driving the park to 778 x 1,014 km -- WORSE than the eccentric park the
    trim existed to fix. A pending node means the orbit is still being written;
    reading it is reading a value in flight. So: node first, orbit second.

    Verdicts:
      - "off":     ecc_max <= 0 (or non-finite). No trim; the caller proceeds
                   exactly as the pre-trim machine did.
      - "execute": a node is on the board and THIS attempt has not been handed
                   to the executor yet -> hand it over. Gated on execs <
                   attempts precisely so a multi-frame burn re-issues nothing:
                   commanding the executor every frame while it works is the
                   shape that wedged earlier lanes.
      - "wait":    a node is on the board and already executing -> hold. Also
                   the CIRCULARIZE-entry case where MechJeb's own ascent
                   circularization node has not been consumed yet (attempts and
                   execs both 0). Bounded by the phase's game-time budget, with
                   its own named give-up in _b5_park_trim_step.
      - "ok":      nothing pending AND the settled eccentricity is finite and
                   <= ecc_max. Proceed.
      - "plan":    nothing pending, out of round, attempts remain -> plan one.
      - "giveup":  nothing pending, out of round, every attempt spent.

    A NON-FINITE eccentricity is NOT treated as ok: an unread orbit must never
    certify a park as round, so it routes to the same plan/giveup ladder as an
    openly bad one (fail closed, the standing rule)."""
    if not _is_finite(ecc_max) or ecc_max <= 0.0:
        return PARK_TRIM_OFF
    if node_count > 0:
        return PARK_TRIM_EXECUTE if execs < attempts else PARK_TRIM_WAIT
    if _is_finite(eccentricity) and eccentricity <= ecc_max:
        return PARK_TRIM_OK
    if attempts >= max_attempts:
        return PARK_TRIM_GIVEUP
    return PARK_TRIM_PLAN


# Verdicts of classify_transfer_reach (the PLAN-TRANSFER diagnostic).
TRANSFER_REACH_UNKNOWN = "unknown"
TRANSFER_REACH_REACHES = "reaches"
TRANSFER_REACH_SHORT = "short"
TRANSFER_REACH_BEYOND = "beyond"


def classify_transfer_reach(leg_pe: float, leg_ap: float,
                            target_pe: float, target_ap: float,
                            target_soi: float = 0.0) -> Tuple[str, float]:
    """Does a planned transfer leg's conic REACH the target body's orbit?
    Pure; four RADII (metres from the shared parent's centre) in, a
    (verdict, gap_metres) pair out.

    This is the diagnostic B15's first three flights did not have. A transfer
    can be planned, executed, and consumed -- node planned, executor burned it,
    node gone, a genuinely hyperbolic escape -- and still be aimed at nothing,
    because "the ejection burned" and "the ejection was the RIGHT SIZE" are
    different claims. The patched conics then simply never predict an encounter
    and the mission dies much later on a coast budget, which reads like a
    budget problem and is not one.

    The test is an ANNULUS OVERLAP, deliberately DIRECTION-FREE: the leg can
    encounter the target only if the radius band it sweeps, [leg_pe, leg_ap],
    overlaps the band the target sweeps, [target_pe, target_ap], WIDENED BY THE
    TARGET'S SOI RADIUS at both ends. One expression covers an OUTWARD transfer
    (B7: leg_ap must reach up to target_pe) and an INWARD one (B15: leg_pe must
    reach down to target_ap), so nothing here has to know which way the craft
    is going.

    THE SOI TERM IS NOT A REFINEMENT, IT IS THE DIFFERENCE BETWEEN A USEFUL
    VERDICT AND A MISLEADING ONE, and B15 flight 5 is the proof. That flight's
    corrected ejection put the heliocentric perihelion 6.96e6 m above Eve's
    aphelion -- 0.082 of Eve's 85.1e6 m SOI radius, i.e. comfortably an
    intercept. A bare band comparison calls that "beyond" in the same word it
    uses for flight 3's 2.46e9 m (28.9 SOI radii), and a reader who trusted the
    word would have gone hunting for a defect in a transfer that was fine.
    Encounter means "enters the SOI", not "crosses the osculating orbit", so
    the SOI belongs in the test. Pass 0.0 (the default) when it cannot be read
    and the verdict degrades to the strict band comparison.

      - "reaches": the widened bands overlap; gap 0.0. NECESSARY, NOT
                   SUFFICIENT -- reaching the target's orbit is not the same as
                   arriving when the target is there, which is the phase
                   problem the correction rounds own. A "reaches" verdict says
                   only that an encounter is GEOMETRICALLY POSSIBLE.
      - "short":   the leg stays entirely INSIDE the target's reach; gap = how
                   much further OUT it must get to graze the SOI.
      - "beyond":  the leg stays entirely OUTSIDE it; gap = how much further IN
                   it must get. This is the B15 flight-3 reading: 2.46e9 m
                   above Eve's aphelion, 28.9 SOI radii, which no amount of
                   coasting can close.
      - "unknown": any of the four radii non-finite. Fails to UNKNOWN, never to
                   a verdict: an unread conic must not be reported as either a
                   good plan or a bad one. A non-finite SOI is NOT unknown --
                   it degrades to 0.0, the strict test, which can only ever be
                   harsher than the truth.

    The returned gap is what a correction actually has to close, so it is the
    SOI-adjusted number and it is 0.0 whenever the verdict is "reaches"."""
    if not (_is_finite(leg_pe) and _is_finite(leg_ap)
            and _is_finite(target_pe) and _is_finite(target_ap)):
        return TRANSFER_REACH_UNKNOWN, float("nan")
    soi = target_soi if (_is_finite(target_soi) and target_soi > 0.0) else 0.0
    if leg_ap < target_pe - soi:
        return TRANSFER_REACH_SHORT, (target_pe - soi) - leg_ap
    if leg_pe > target_ap + soi:
        return TRANSFER_REACH_BEYOND, leg_pe - (target_ap + soi)
    return TRANSFER_REACH_REACHES, 0.0


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
    # GENERALIZED seam-command outcome (ACTION_PARSEK_SEAM_COMMAND). Three
    # fail-closed UNREAD sentinels, in the same discipline as node_executor_
    # enabled's -1 and crew_count's -1:
    #   seam_command_result  "" = no generalized command has terminated yet
    #                        (a phase gate that reads "" stays put and burns its
    #                        own frame budget); "OK" / "ERROR" / "TIMEOUT" are
    #                        the terminal tokens.
    #   seam_command_tag     the TAG of the command the result belongs to, ""
    #                        when none. A phase gate MUST check the tag as well
    #                        as the token, otherwise the previous command's OK
    #                        satisfies the next phase without that command ever
    #                        having run (the stale-result fail-open).
    #   seam_command_payload the terminal response line's payload fields as an
    #                        ordered tuple of (key, value) pairs -- everything
    #                        after id/cmd/verdict/seq, so a RecordingState reply
    #                        is readable by the machine. () = none read.
    # These are NOT in snapshot_dict / MACHINE_STATE_FIELDS on purpose: adding a
    # key to either moves the status-file block or the machine line of EVERY
    # mission. seam_commit_result set the precedent (it is in neither).
    seam_command_result: str = ""
    seam_command_tag: str = ""
    seam_command_payload: Tuple[Tuple[str, str], ...] = ()
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
    # The WATCHED kerbal's roster status, normalized by normalize_roster_status
    # over kRPC's SpaceCenter.GetKerbal(name).RosterStatus. "" = UNREAD, the
    # fail-closed sentinel (same discipline as craft_chute_state): it matches no
    # gate, so a mission that never armed ACTION_SET_ROSTER_WATCH - which is every
    # mission but CL-1 - can never satisfy a crew gate with a fabricated value,
    # and neither can a frame whose read raised. "NotInRoster" is the distinct
    # OBSERVED-ABSENT reading (GetKerbal returned null); see the
    # ROSTER_STATUS_* constants for why the two are not the same thing.
    #
    # THIS FIELD IS DELIBERATELY POPULATED ON A vessel_lost SNAPSHOT TOO, which
    # no other opt-in channel is. The rule those channels follow ("a vessel_lost
    # snapshot carries benign defaults and must not fabricate a canopy") exists
    # because they are properties OF THE VESSEL, and a destroyed vessel has none.
    # A roster status is a property of the KERBAL and outlives the craft, so on
    # the vessel-lost path it is the one channel that still carries truth - and
    # it is exactly the frame on which a crew-loss mission most needs it.
    crew_roster_status: str = ""
    # OBSERVED MechJeb NodeExecutor.Enabled (KRPC.MechJeb 0.8.1
    # NodeExecutor : ComputerModule -> the inherited MuMech.ComputerModule
    # Enabled property). TRI-STATE, -1 = UNREAD (the fail-closed sentinel,
    # same discipline as crew_count): -1 never proves the executor alive AND
    # never proves it dead, so an unread channel falls back to the node-clock
    # classifier instead of acting on evidence we do not have. 1 = observed
    # ENABLED, 0 = observed DISABLED. Read only when the control was built
    # with read_node_executor=True (B11/B12), so every other mission's
    # snapshot is byte-identical. THE B11 flight-1 lesson, and the same
    # commanded-vs-OBSERVED gap that produced the B-DOCK docking-AP and the
    # EVA-4 ladder-release defects: "we issued mj_execute_nodes" is not
    # evidence the executor engaged -- only this read is.
    # NOTE: KRPC.MechJeb 0.8.1 exposes NO executor status string. MechJeb's
    # own MechJebModuleNodeExecutor.State (WARPALIGN / LEAD / BURN / IDLE) is
    # a public field but is NOT wrapped by the service (pinned source
    # NodeExecutor.cs binds only Autowarp / LeadTime / ExecuteOneNode /
    # ExecuteAllNodes / Abort plus the inherited Enabled), so Enabled is the
    # ONLY observable executor channel available to us.
    node_executor_enabled: int = -1
    # Seconds until the craft reaches PERIAPSIS of its CURRENT orbit (kRPC
    # Orbit.TimeToPeriapsis, surface-verified against the installed 0.5.4
    # client). NaN = UNREAD / unreadable, the fail-closed sentinel: with no
    # periapsis clock the capture-mode flyby warp is DISABLED outright (1x is
    # slow but correct; the phase budget bounds it), because there is no way
    # to prove a warp would not sail past the only capture point on the pass.
    # Read only when the control was built with read_periapsis=True (B11/B12),
    # so every other mission's snapshot is byte-identical.
    time_to_periapsis: float = float("nan")
    # --- LANDING lane (B13/B14), opt-in via read_landing=True. Every field
    # carries the FAIL-CLOSED sentinel of its type, so a runner that does not
    # opt in (or whose read faults) can never satisfy a landing gate with a
    # fabricated value.
    #
    # OBSERVED MechJeb LandingAutopilot.Enabled, the same TRI-STATE discipline
    # as node_executor_enabled: -1 = UNREAD (never proves the module alive AND
    # never proves it dead -- the altitude-trend watchdog and the descent budget
    # own the outcome instead), 1 = observed ENABLED, 0 = observed DISABLED.
    # THE channel this lane's headline liveness gate reads: calling
    # LandUntargeted() is a COMMAND, and this is the only evidence the module
    # took control.
    landing_ap_enabled: int = -1
    # MechJeb LandingAutopilot.Status (the AutopilotModule.Status string:
    # "Doing deorbit burn." / "Warping to start of braking burn." / ...),
    # truncated. "" = UNREAD. DIAGNOSABILITY ONLY -- no gate reads it, exactly
    # like docking_ap_status. It is what the module thinks it is doing, which is
    # the first question asked of a descent that is enabled but not descending.
    landing_ap_status: str = ""
    # kRPC Flight.HorizontalSpeed in the body reference frame (m/s). NaN =
    # UNREAD / read failed, and NaN FAILS the landed-stability gate CLOSED: a
    # touchdown is only "settled" when BOTH speed components are observed under
    # their floors, so an unread horizontal component can never certify a
    # sliding or tumbling craft as stable.
    horizontal_speed: float = float("nan")
    # The live camera mode, normalized by normalize_camera_mode over kRPC's
    # SpaceCenter.Camera.Mode name ("Map" / "Automatic" / ...). "" = UNREAD,
    # the fail-closed sentinel (same discipline as craft_chute_state): it
    # matches no gate, so a mission that does not opt into the camera read
    # (every mission but the V1 map-dwell lane, via ``read_camera=True``) can
    # never satisfy the staged-map-camera gate with a fabricated value. THE
    # V1 rationale: "we set camera.mode = Map" is a COMMAND; only this read
    # is evidence the map camera actually engaged.
    camera_mode: str = ""


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
    # ACTION_MJ_LAND_UNTARGETED payload: the MechJeb LandingAutopilot
    # configuration the runner WRITES (and reads back) before engaging, as
    # ``(touchdownSpeedMps, deployGears, deployChutes, rcsAdjustment)``. A plain
    # TUPLE, not a dict, so Action stays a frozen/hashable dataclass and test
    # equality over emitted actions keeps working; same precedent as the
    # ``crew`` tuple above. None -> the runner uses its own conservative
    # defaults, which is a diagnosable bug rather than a silent one (it logs).
    landing_config: Optional[Tuple[float, bool, bool, bool]] = None
    # ACTION_PARSEK_SEAM_COMMAND payload: the verb, its ordered (key, value)
    # args, and the per-command tag the runner folds into the wire command-id.
    # All three default None so EVERY pre-existing Action is constructed and
    # compared exactly as before; the fly loop logs only kind/value/text, so no
    # existing mission's log line moves either.
    seam_verb: Optional[str] = None
    seam_args: Optional[Tuple[Tuple[str, str], ...]] = None
    seam_tag: Optional[str] = None
    # ACTION_CAMERA_SET_POSE payload: ``(pitchDeg, headingDeg, distanceMeters)``.
    # A plain TUPLE (the landing_config precedent) so Action stays frozen /
    # hashable and emitted-action equality in tests keeps working. None for
    # every non-camera action, so no pre-existing Action moves.
    camera_pose: Optional[Tuple[float, float, float]] = None


def seam_command_id(reserved_id: str, tag: str) -> str:
    """The wire command-id for one generalized mid-mission seam command:
    ``"<reservedId>.<tag>"``.

    The reserved id is the mission STEP's own step id, which ``hlib.step_id_for
    _index`` formats as ``"%04d"`` (pure digits). A sub-id therefore can never
    collide with a runner step id (a digits-only id has no ``.``), and two
    different tags can never collide with each other -- both of which matter
    because the C# seam SKIPS DUPLICATE IDS, so a colliding id makes the second
    command a silent no-op rather than an error.

    Fails CLOSED: an empty reserved id or an empty tag returns "" and the caller
    resolves the command to a terminal ERROR token instead of writing a command
    whose id cannot be distinguished."""
    reserved = str(reserved_id or "")
    label = str(tag or "")
    if not reserved or not label:
        return ""
    return "%s.%s" % (reserved, label)


# Bounded WALL-clock poll window for one generalized seam command, per verb.
# The default matches the live-proven CommitTree bridge (SEAM_COMMIT_POLL_SECONDS
# = 120 s): a one-frame Unity verb answers immediately, and the bound only exists
# so a wedged addon can never hang the fly loop. The overrides are the TWO-PHASE
# verbs, whose completion straddles a KSP scene reload and legitimately takes
# minutes -- polling those for 120 s would manufacture a TIMEOUT out of a healthy
# reload. The values are the seam's own deferral budgets rounded up, not guesses
# about wall speed.
SEAM_COMMAND_POLL_SECONDS_DEFAULT: float = 120.0
SEAM_COMMAND_POLL_SECONDS_BY_VERB: Dict[str, float] = {
    "InvokeRewind": 420.0,       # StartInvoke + the scene reload + ConsumePostLoad
    "AnswerMergeDialog": 240.0,  # answer-applied AND the post-answer scene settle
    "LoadGame": 420.0,           # realize the .sfs + StartAndFocusVessel
    # R12. ExitToSpaceCenter is the third scene-straddling verb, and it is entered here
    # for the SAME reason AnswerMergeDialog is: its terminal waits on a SPACECENTER
    # settle whose bootstrap re-reads persistent.sfs and runs SetProtoModules ->
    # ParsekScenario.OnLoad -> the pending-tree auto-commit. Its C# budget is 120 s and
    # the DEFAULT here is also 120 s, so riding the default would poll with ZERO margin
    # and manufacture a TIMEOUT out of a healthy exit; 240 s doubles it exactly as
    # AnswerMergeDialog does. R12's OTHER verb, SimulateStockSwitchClick, is deliberately
    # ABSENT: it is single-phase (the switch and Parsek's consume both run synchronously
    # inside SetActiveVessel), so it answers within a frame and the default is correct.
    "ExitToSpaceCenter": 240.0,
}


def seam_command_poll_seconds(verb: str) -> float:
    """The bounded poll window (wall seconds) for one generalized seam command.
    Unknown / empty verbs get the default: an unrecognized verb is REJECTED by
    the C# seam within a frame, so it never needs the long window."""
    return SEAM_COMMAND_POLL_SECONDS_BY_VERB.get(
        str(verb or ""), SEAM_COMMAND_POLL_SECONDS_DEFAULT)


def decode_seam_value(value: str) -> str:
    """Percent-DECODE one seam response value (the inverse of the C#
    ``TestCommandProtocol.Encode``, which percent-encodes any byte needing it and
    leaves the rest literal).

    Exists so a REJECTED verb's ``msg`` - which carries PARSEK's OWN refusal
    reason verbatim (``recording-active``, ``refly-gate <reason>``, ``unknown-rp``)
    - can be surfaced in a give-up instead of the harness guessing at a cause. The
    first live R1 flight red on ``reason=recording-active`` while the mission's
    give-up text speculated about the RewindPoint, which sent the operator looking
    in the wrong place.

    Tolerant: a malformed escape (a ``%`` not followed by two hex digits) or a
    non-UTF-8 byte sequence returns the input unchanged rather than raising - a
    diagnostic string must never be able to crash the machine that is already
    reporting a failure."""
    text = str(value or "")
    if "%" not in text:
        return text
    out = bytearray()
    i = 0
    try:
        raw = text.encode("ascii")
    except UnicodeEncodeError:
        return text
    while i < len(raw):
        ch = raw[i]
        if ch == 0x25:  # '%'
            if i + 2 >= len(raw):
                return text
            try:
                out.append(int(raw[i + 1:i + 3].decode("ascii"), 16))
            except ValueError:
                return text
            i += 3
            continue
        out.append(ch)
        i += 1
    try:
        return out.decode("utf-8")
    except UnicodeDecodeError:
        return text


def format_seam_command_line(command_id: str, verb: str,
                             args: Optional[Tuple[Tuple[str, str], ...]] = None) -> str:
    """The request-channel line for one seam command: ``id=<id> cmd=<verb>``
    followed by one ``k=v`` token per arg, in declaration order.

    Mirrors ``_perform_seam_commit``'s ``"id=%s cmd=CommitTree"`` shape exactly,
    so a no-arg CommitTree issued through the generalized path is BYTE-IDENTICAL
    to the one the live-proven path writes. Values are passed through verbatim:
    the seam grammar is ``key=value`` whitespace-separated, and every arg this
    lane emits (rewind-point ids, slot indices, merge choices) is already
    token-safe. Pure, so the wire shape is unit-covered without a channel."""
    parts = ["id=%s" % command_id, "cmd=%s" % verb]
    for key, value in (args or ()):
        parts.append("%s=%s" % (key, value))
    return " ".join(parts)


# ---------------------------------------------------------------------------
# Mission params (parsed from the spec missionParams block; carried in state).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class B1Params:
    """B1 pad-hop tuning (design [driver.missionParams] for b1_pad_hop). All are
    WINDOWS / thresholds / budgets, never golden trajectories."""
    throttle: float
    # ARM THE CHUTE WHILE SLOW, at the apoapsis crossing, not at an altitude. The
    # machine arms on the first DESCENT frame whose |vertical speed| is within this
    # bound (spec key chuteArmMaxRateMps). See the b1_decide docstring for why an
    # ALTITUDE trigger was inert on this craft.
    chute_arm_max_rate: float
    # The stock deployAltitude (metres) the machine writes onto every parachute in the
    # same frame it arms (spec key chuteFullDeployAltMeters). Pinned by the spec rather
    # than inherited from whatever the fixture's PAW happens to persist, so the
    # full-canopy leg is a declared mission input and not a silent fixture property.
    chute_full_deploy_alt: float
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
        chute_arm_max_rate=float(params.get("chuteArmMaxRateMps", 30)),
        chute_full_deploy_alt=float(params.get("chuteFullDeployAltMeters", 1000)),
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
        descent_timeout=float(params.get("descentTimeoutSeconds", 240)),
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
    park_trim_ecc_max: float = 0.0         # > 0 ARMS the park round-out trim:
                                           # CIRCULARIZE, having met the pe
                                           # window, plans + burns a MechJeb
                                           # circularize-at-APOAPSIS until the
                                           # observed eccentricity is at/below
                                           # this, bounded by
                                           # PARK_TRIM_MAX_ATTEMPTS. 0.0 (the
                                           # default) leaves CIRCULARIZE
                                           # byte-identical to the pre-trim
                                           # machine, so every flown lane is
                                           # untouched. Required by the
                                           # INTERPLANETARY lanes only -- see
                                           # park_trim_verdict for why an
                                           # eccentric park breaks MechJeb's
                                           # ejection sizing (spec key
                                           # parkTrimEccMax)
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
    # --- ORBIT-mission tail (B11/B12). Every default is the FLYBY-preserving
    # value: with capture_enabled False none of the four capture phases is
    # reachable and the machine is byte-identical to the proven B5/B6/B7 shape.
    capture_enabled: bool = False          # True: inside the target SOI, plan +
                                           # fly a periapsis circularization,
                                           # hold a stable park, and COMMIT the
                                           # tree there (the terminal is
                                           # ORBIT-COMMITTED, not RETURN). The
                                           # return-body exit then becomes an
                                           # ASSERT-FAIL (leaving the target SOI
                                           # IS the failure). Spec key
                                           # captureEnabled.
    capture_plan_timeout: float = 300.0    # PLAN-CAPTURE budget (game s). Expiry
                                           # FLAKES: no capture node = no orbit
                                           # mission (spec key
                                           # capturePlanTimeoutSeconds).
    capture_burn_timeout: float = 40000.0  # CAPTURE-BURN budget (game s): the
                                           # NodeExecutor autowarps from the SOI
                                           # boundary DOWN to periapsis, which on
                                           # a Mun/Minmus arrival is hours of game
                                           # time (spec key
                                           # captureBurnTimeoutSeconds).
    park_min_periapsis: float = 0.0        # PARK gate: the captured orbit's
                                           # periapsis floor (m above the target
                                           # body). Fails CLOSED on a NaN read
                                           # (spec key parkMinPeriapsisMeters).
    park_max_apoapsis: float = 0.0         # PARK gate: the captured orbit's
                                           # apoapsis CEILING (m). A bound orbit
                                           # reads a POSITIVE apoapsis; a
                                           # hyperbolic (uncaptured) one reads
                                           # negative, so 0 < ap <= this is the
                                           # "we are actually captured" evidence
                                           # (spec key parkMaxApoapsisMeters).
    park_max_eccentricity: float = 0.5     # PARK gate: the captured orbit's
                                           # eccentricity ceiling -- the real
                                           # "circularized, not a grazing
                                           # ellipse" signal (spec key
                                           # parkMaxEccentricity).
    park_max_angular_velocity: float = 0.05
                                           # PARK gate: tumble ceiling (rad/s).
                                           # With SAS stability-assist + RCS held
                                           # a settled stage reads ~0; NaN
                                           # (unread) fails closed (spec key
                                           # parkMaxAngularVelocityRadPerSec).
    park_situations: Tuple[str, ...] = ("ORBITING",)
                                           # PARK gate: accepted kRPC situations
                                           # (spec key parkSituations).
    park_dwell: float = 300.0              # GAME seconds the stable park must be
                                           # HELD before the commit: the recording
                                           # must carry real parked-in-foreign-SOI
                                           # coverage, not one settling frame
                                           # (spec key parkDwellSeconds).
    park_debounce: int = 3                 # consecutive in-gate PARK frames
                                           # (spec key parkDebounceFrames).
    park_timeout: float = 7200.0           # PARK budget (game s); expiry flakes
                                           # with a NAMED reason distinguishing
                                           # "never stabilized" from "stabilized
                                           # but never HELD" (spec key
                                           # parkTimeoutSeconds).
    commit_timeout: float = 300.0          # ORBIT-COMMIT / SURFACE-COMMIT budget
                                           # (game s) for the command-seam
                                           # CommitTree round trip (spec key
                                           # commitTimeoutSeconds).
    # --- LANDING-mission tail (B13/B14). Every default is the ORBIT-preserving
    # value: with landing_enabled False none of the four landing phases is
    # reachable and the machine is byte-identical to the LIVE-PROVEN B11/B12
    # shape (and, with capture_enabled also False, to the B5/B6/B7 flyby shape).
    landing_enabled: bool = False          # True: after the PARK dwell, fly
                                           # MechJeb's UNTARGETED landing
                                           # autopilot down to the surface, hold
                                           # a landed dwell, and COMMIT the tree
                                           # THERE (the terminal is
                                           # SURFACE-COMMITTED, not
                                           # ORBIT-COMMITTED). REQUIRES
                                           # capture_enabled: the only door into
                                           # DESCENT is the PARK dwell. Spec key
                                           # landingEnabled.
    descent_timeout: float = 3000.0        # DESCENT budget (GAME s). The
                                           # deorbit-to-touchdown fall is a
                                           # BALLISTIC duration that warp does
                                           # not shorten, so a GAME-time budget
                                           # is the right instrument here (unlike
                                           # CORRECTION-BURN's aim-then-warp,
                                           # whose game-time budget was spent by
                                           # the wait it was meant to bound).
                                           # Sized to fire BEFORE the mission
                                           # WALL reaper even in the worst case
                                           # where MechJeb never warps and the
                                           # whole descent runs 1:1 -- see the
                                           # spec's budget arithmetic. Spec key
                                           # descentTimeoutSeconds.
    landing_touchdown_speed: float = 0.5   # MechJeb LandingAutopilot
                                           # TouchdownSpeed (m/s); its own
                                           # default. Spec key
                                           # landingTouchdownSpeedMps.
    landing_deploy_gears: bool = True      # MechJeb DeployGears: extend the
                                           # landing legs below 1 km AGL. The
                                           # Kerbal X upper stage carries 3x
                                           # landingLeg1-2, so this is a real
                                           # part-event surface. Spec key
                                           # landingDeployGears.
    landing_deploy_chutes: bool = False    # MechJeb DeployChutes. MUST stay
                                           # False for Mun / Minmus: both are
                                           # AIRLESS, a parachute cannot deploy
                                           # there, and arming one in the config
                                           # would be a lie about the vehicle
                                           # configuration. Spec key
                                           # landingDeployChutes.
    landing_rcs_adjustment: bool = False   # MechJeb RcsAdjustment. False: the
                                           # Kerbal X upper stage has NO RCS
                                           # thruster blocks (mk1-3pod +
                                           # HeatShield2 + parachuteLarge +
                                           # Rockomax16.BW + liquidEngine2-2.v2 +
                                           # 3x landingLeg1-2 + 2 ladders + 2
                                           # solar panels), so asking MechJeb to
                                           # trim with RCS commands a control
                                           # authority that does not exist. Spec
                                           # key landingRcsAdjustment.
    landing_progress_window: float = 900.0
                                           # landing-no-progress window (GAME s):
                                           # the descent must drop
                                           # landing_progress_min_drop metres
                                           # within this span or the watchdog
                                           # NAMES the stall. Sized ABOVE the
                                           # worst-case pre-deorbit attitude flip
                                           # (MechJeb points retrograde-horizontal
                                           # before the deorbit burn, and the
                                           # Kerbal X pod wheel's near-antiparallel
                                           # convergence is the ~340 s the
                                           # correction burner's burnNoStartSeconds
                                           # was sized against), during which the
                                           # altitude of a CIRCULAR park is flat by
                                           # construction. Spec key
                                           # landingProgressWindowSeconds.
    landing_progress_min_drop: float = 500.0
                                           # metres of surface-altitude DROP the
                                           # window must deliver. Deliberately
                                           # tiny against the real profile (the
                                           # first 900 s off a Mun park sheds
                                           # tens of km): this bounds BROKEN, and
                                           # the budget bounds SLOW. Spec key
                                           # landingProgressMinDropMeters.
    landed_situations: Tuple[str, ...] = ("LANDED", "SPLASHED")
                                           # accepted kRPC situations for the
                                           # touchdown. SPLASHED is kept even
                                           # though Mun/Minmus have no ocean:
                                           # hard-coding it away would make the
                                           # machine wrong for any future body
                                           # that does, and it costs nothing.
                                           # Spec key landedSituations.
    landed_max_vertical_speed: float = 1.0
                                           # settled-touchdown gate: |vertical
                                           # speed| ceiling (m/s). NaN fails
                                           # CLOSED. Spec key
                                           # landedMaxVerticalSpeedMps.
    landed_max_horizontal_speed: float = 1.0
                                           # settled-touchdown gate: horizontal
                                           # speed ceiling (m/s) -- the conjunct
                                           # that separates a SETTLED lander from
                                           # one still sliding downhill. NaN
                                           # fails CLOSED. Spec key
                                           # landedMaxHorizontalSpeedMps.
    landed_dwell: float = 120.0            # GAME seconds the landed craft is
                                           # HELD before the commit. This dwell
                                           # IS the recorded landed-on-another-
                                           # body coverage (the machine holds 1x
                                           # through it), so it is also the
                                           # lane's main added wall cost. Spec
                                           # key landedDwellSeconds.
    landed_debounce: int = 3               # consecutive in-gate LANDED-SETTLE
                                           # frames. Spec key
                                           # landedDebounceFrames.
    landed_timeout: float = 600.0          # LANDED-SETTLE budget (GAME s);
                                           # expiry flakes landed-never-stable
                                           # with a reason distinguishing "never
                                           # settled" from "settled but not
                                           # in-gate at the end of the dwell".
                                           # Spec key landedTimeoutSeconds.


def b5_params_from_dict(params: Dict) -> B5Params:
    """Build ``B5Params`` from a spec ``missionParams`` dict.

    Raises ``ValueError`` when ``landingEnabled`` is set without
    ``captureEnabled``. That implication is not a style preference, it is the
    machine's TOPOLOGY: the ONLY edge into DESCENT is the capture lane's PARK
    dwell (``b5_decide``'s PARK branch), so a landing-without-capture spec
    silently degrades to the FLYBY machine -- it would fly a fly-past, evaluate
    the four flyby assertion rows, and report MISSION-OK for a scenario whose
    whole objective is a landing. Failing the spec load is the only place that
    mistake is cheap; a lane whose design rests on an implication must assert
    it rather than document it."""
    params = params or {}
    if bool(params.get("landingEnabled", False)) \
            and not bool(params.get("captureEnabled", False)):
        raise ValueError(
            "landingEnabled requires captureEnabled: the only door into the "
            "DESCENT phase is the capture lane's PARK dwell, so landingEnabled "
            "alone is INERT and the mission would silently degrade to the "
            "flyby machine and its flyby assertion rows")
    return B5Params(
        target_apoapsis=float(params.get("targetApoapsisMeters", 80000)),
        target_periapsis=float(params.get("targetPeriapsisMeters", 80000)),
        apo_error=float(params.get("apoErrorMeters", 5000)),
        peri_error=float(params.get("periErrorMeters", 5000)),
        ascent_timeout=float(params.get("ascentTimeoutSeconds", 420)),
        circularize_timeout=float(params.get("circularizeTimeoutSeconds", 300)),
        park_trim_ecc_max=float(params.get("parkTrimEccMax", 0.0)),
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
        capture_enabled=bool(params.get("captureEnabled", False)),
        capture_plan_timeout=float(params.get("capturePlanTimeoutSeconds", 300.0)),
        capture_burn_timeout=float(params.get("captureBurnTimeoutSeconds", 40000.0)),
        park_min_periapsis=float(params.get("parkMinPeriapsisMeters", 0.0)),
        park_max_apoapsis=float(params.get("parkMaxApoapsisMeters", 0.0)),
        park_max_eccentricity=float(params.get("parkMaxEccentricity", 0.5)),
        park_max_angular_velocity=float(
            params.get("parkMaxAngularVelocityRadPerSec", 0.05)),
        park_situations=tuple(params.get("parkSituations", ("ORBITING",))),
        park_dwell=float(params.get("parkDwellSeconds", 300.0)),
        park_debounce=int(params.get("parkDebounceFrames", 3)),
        park_timeout=float(params.get("parkTimeoutSeconds", 7200.0)),
        commit_timeout=float(params.get("commitTimeoutSeconds", 300.0)),
        landing_enabled=bool(params.get("landingEnabled", False)),
        descent_timeout=float(params.get("descentTimeoutSeconds", 3000.0)),
        landing_touchdown_speed=float(params.get("landingTouchdownSpeedMps", 0.5)),
        landing_deploy_gears=bool(params.get("landingDeployGears", True)),
        landing_deploy_chutes=bool(params.get("landingDeployChutes", False)),
        landing_rcs_adjustment=bool(params.get("landingRcsAdjustment", False)),
        landing_progress_window=float(
            params.get("landingProgressWindowSeconds", 900.0)),
        landing_progress_min_drop=float(
            params.get("landingProgressMinDropMeters", 500.0)),
        landed_situations=tuple(params.get("landedSituations",
                                           ("LANDED", "SPLASHED"))),
        landed_max_vertical_speed=float(
            params.get("landedMaxVerticalSpeedMps", 1.0)),
        landed_max_horizontal_speed=float(
            params.get("landedMaxHorizontalSpeedMps", 1.0)),
        landed_dwell=float(params.get("landedDwellSeconds", 120.0)),
        landed_debounce=int(params.get("landedDebounceFrames", 3)),
        landed_timeout=float(params.get("landedTimeoutSeconds", 600.0)),
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
    # COMMANDED latch: the arm action was emitted. Kept as EVIDENCE (it names where the
    # machine acted) but it is NOT the success signal any more -- flight evidence proved
    # a commanded chute can sit inert in ARMED all the way to the ground.
    chute_deployed: bool = False
    # Where the arm was emitted, carried into the result so an operator can read the
    # arm point without re-deriving it from the frames.
    chute_armed_altitude: Optional[float] = None
    chute_armed_rate: Optional[float] = None
    # OBSERVED latch: the craft's parachute has READ Deployed on B1_CANOPY_DEBOUNCE_K
    # consecutive DESCENT frames (TelemetrySnapshot.craft_chute_state, the live kRPC
    # ParachuteState). This -- never ``chute_deployed`` -- is what the DOWN terminal and
    # the canopy assertion gate on.
    craft_chute_full_seen: bool = False
    # Consecutive Deployed reads so far; reset by any non-Deployed frame (fail-closed).
    canopy_seen_streak: int = 0
    # Consecutive unarmed DESCENT frames read BELOW chuteFullDeployAltMeters; reset by
    # any at-or-above-floor frame. Debounces the chute-arm-window-missed terminal.
    below_floor_streak: int = 0
    # The last non-empty observed chute state, carried so a failure names what the
    # canopy was actually doing ("Armed" = commanded but never opened).
    last_chute_state: str = ""
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
      - COAST -> DESCENT: when past apoapsis (vertical speed goes negative). The
        frame FALLS THROUGH into the DESCENT body rather than returning, because the
        arm decision below is RATE-gated and the rate only ever worsens.
        Bounded by coastTimeoutSeconds.
      - DESCENT: on the FIRST frame whose |vertical speed| is within
        chuteArmMaxRateMps (i.e. the apoapsis crossing), write
        chuteFullDeployAltMeters onto the craft's parachutes and ARM them; both
        actions ride the SAME frame so the module's first ACTIVE FixedUpdate already
        sees the raised altitude. Then DESCENT -> LANDED when the situation is a
        landed/splashed one. Bounded by descentTimeoutSeconds.

        ARM WHILE SLOW, NOT AT AN ALTITUDE (2026-07-25 fix, decompiled
        ModuleParachute + two flights of evidence). Stock's ACTIVE -> SEMIDEPLOYED
        transition requires ``automateSafeDeploy >= (int)deploymentSafeState``, and
        DeploySafe reads SAFE only while ``shockTemp <= chuteMaxTemp * safeMult``.
        The b1-pad-craft fixture persists ``automateSafeDeploy = 0`` (open only while
        SAFE), and a craft already at terminal velocity in dense air never reads SAFE
        and never slows on its own -- so an altitude-triggered arm produced a chute
        that sat INERT in ARMED all the way into the ground. The measured proof: this
        craft's unchuted descent settles at -301 m/s, and 5.1 s after a 2,382 m arm
        the rate had moved 4.7 m/s; its recording carried ZERO Parachute* part events.
        At the apoapsis crossing the airspeed is near zero, DeploySafe is trivially
        SAFE, and Kerbin is already far above the module's 0.04 atm pressure gate, so
        the canopy opens within a frame or two. Proven live on this exact fixture and
        craft by EVA-4 flight 2.
      - DESCENT -> DOWN (operator decision 2026-07-20 option A, re-gated 2026-07-25):
        when either vessel-lost signal fires (a runner vessel_lost snapshot, or the
        frozen counter reaching its limit) AND the craft's canopy has been OBSERVED
        open AND the craft was last seen at/below downMaxAltMeters, the hop FLEW, the
        CANOPY OPENED, and the craft REACHED THE GROUND -- a breakup at a
        chute-borne touchdown is a SUCCESSFUL end (B4 owns the
        craft-survives-intact contract). DOWN is a real terminal: done, NO
        loss_reason, verdict stays None so the assertions decide. The conjunct is the
        OBSERVED ``craft_chute_full_seen`` latch, never the commanded
        ``chute_deployed`` one: gating on "we commanded it" is exactly how a
        300 m/s terminal-velocity impact was awarded a chute-deployed-impact
        terminal for four months. Without the observed canopy -- and in every other
        phase -- the loss stays the ASSERT-FAIL loss_reason terminal, and the reason
        NAMES the observed chute state so an inert chute reds as "craftChute=Armed"
        instead of passing.
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

    # OBSERVED canopy latches, from live frames only (a vessel_lost snapshot carries
    # benign defaults and must not fabricate a canopy).
    #
    # ``last_chute_state`` is DIAGNOSTIC ONLY (it names what the canopy was doing in a
    # failure reason), so it tracks every live frame in every phase.
    #
    # ``craft_chute_full_seen`` is the GATE, so it is far narrower: DESCENT-phase frames
    # only, and only after B1_CANOPY_DEBOUNCE_K consecutive Deployed reads. Both legs
    # matter and both were missing in the first draft of this fix (Opus review panel,
    # 2026-07-25, F1):
    #   - PHASE SCOPE: the latch used to run before the phase dispatch, so ONE Deployed
    #     read on any frame - a stale pad read, a multi-chute craft with a pre-deployed
    #     spare, a read taken during an active-vessel handoff - permanently certified
    #     the canopy. Reproduced: a spurious PRE_LAUNCH Deployed followed by an
    #     Armed-the-whole-way 300 m/s impact resolved MISSION-OK with the reason "all
    #     telemetry assertions met" while last_chute_state was still "Armed". That is
    #     this bug class over again, moved one frame earlier. The chute cannot legally
    #     be open before DESCENT anyway: it is STOWED until the apoapsis-crossing arm.
    #   - DEBOUNCE: stock flips ParachuteState to DEPLOYED at the START of the ~8 s
    #     canopy animation (the same trap EVA4_WINDOW_DEBOUNCE_K exists for), so a lone
    #     glitched frame must not certify a terminal. Cheap here: full deploy triggers
    #     at chuteFullDeployAltMeters with many polls of sky left, so a real canopy
    #     always reads Deployed on consecutive frames.
    # Sticky once earned: a canopy that opened and was then Cut, or destroyed on the
    # last frame, still flew a chuted descent.
    if not snapshot.vessel_lost and snapshot.craft_chute_state:
        state = replace(state, last_chute_state=snapshot.craft_chute_state)
    if (not snapshot.vessel_lost and state.phase == B1_DESCENT
            and not state.craft_chute_full_seen):
        if snapshot.craft_chute_state == CHUTE_STATE_DEPLOYED:
            streak = state.canopy_seen_streak + 1
            state = replace(state, canopy_seen_streak=streak,
                            craft_chute_full_seen=(streak >= B1_CANOPY_DEBOUNCE_K))
        else:
            state = replace(state, canopy_seen_streak=0)

    # Runner-signaled vessel loss (unreadable active vessel after repeated telemetry
    # failures): a phase-INDEPENDENT terminal (design vessel-destroyed terminal).
    # In DESCENT with the canopy OBSERVED open AND the craft last seen at/below
    # downMaxAltMeters this is the DOWN success terminal (option A: flew + canopy
    # + reached the ground); a post-canopy loss AT ALTITUDE (chute ripped,
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
            # Enter DESCENT and FALL THROUGH into its body on the SAME frame (no early
            # return). The arm decision below is RATE-gated and the rate only ever
            # worsens - Kerbin adds ~10 m/s of fall per ~1 s poll - so deferring the arm
            # by one poll needlessly eats the arming bound, and a few polls of delay
            # would push the craft permanently outside it: the inert-chute failure mode
            # in slow motion.
            state = _b1_enter(state, B1_DESCENT, snapshot.ut, peak)
        else:
            return _b1_stay_or_flake(state, snapshot, peak), []

    if state.phase == B1_DESCENT:
        actions: List[Action] = []
        chute_deployed = state.chute_deployed
        armed_alt = state.chute_armed_altitude
        armed_rate = state.chute_armed_rate
        # ARM WHILE SLOW: raise the full-deploy altitude, then arm, on the first frame
        # inside the rate bound. Both actions ride the SAME frame so the module's very
        # first ACTIVE FixedUpdate already sees the raised altitude.
        if (not chute_deployed and _is_finite(snapshot.vertical_speed)
                and abs(snapshot.vertical_speed) <= state.params.chute_arm_max_rate):
            actions.append(Action(ACTION_SET_CHUTE_DEPLOY_ALTITUDE,
                                  state.params.chute_full_deploy_alt))
            actions.append(Action(ACTION_DEPLOY_CHUTE))
            chute_deployed = True
            armed_alt = snapshot.altitude if _is_finite(snapshot.altitude) else None
            armed_rate = snapshot.vertical_speed
        # Below-floor streak, computed BEFORE the terminal checks so every return path
        # below can carry it (it is read by the LANDED return too).
        below_floor = state.below_floor_streak
        if not chute_deployed and _is_finite(snapshot.altitude):
            below_floor = (below_floor + 1
                           if snapshot.altitude < state.params.chute_full_deploy_alt
                           else 0)

        if snapshot.situation in state.params.landed_situations:
            landed = _b1_enter(state, B1_LANDED, snapshot.ut, peak)
            return replace(landed, chute_deployed=chute_deployed,
                           chute_armed_altitude=armed_alt,
                           chute_armed_rate=armed_rate,
                           below_floor_streak=below_floor), actions

        # ARM-WINDOW-MISSED: a NAMED, FAST ASSERT-FAIL (Opus review panel 2026-07-25,
        # F2). The arm gate is one-shot and monotonically unreachable once missed - the
        # fall rate only grows - so a single skipped poll across apoapsis (an RPC
        # latency spike, a KSP hitch) permanently disarms the mission. Without this
        # branch the craft simply kept falling to the descent budget and reported
        # MISSION-FLAKE "phase DESCENT timed out": a DETERMINISTIC failure filed in the
        # flake bucket, polluting the flake ledger and naming nothing about the chute.
        # (Both verdicts are retry-once in hlib, so this buys the NAME and the saved
        # descent budget, not a saved retry.) It also falsified this
        # spec's own claim that a canopy failure is "caught first and fast ... instead
        # of burning the budget" - true on the LANDED / vessel-lost paths, false here.
        # Sibling of EVA-4's eva-window-missed terminal.
        #
        # The floor is chuteFullDeployAltMeters: below it an unarmed chute can no
        # longer produce the full canopy the mission asserts, so the outcome is already
        # decided and waiting only wastes budget.
        # DEBOUNCED by B1_CANOPY_DEBOUNCE_K, for the same reason the canopy latch is
        # (review round 3, F1): a terminal that CONDEMNS deserves the identical
        # treatment as one that CERTIFIES. Undebounced, one glitched surface_altitude
        # sample ended a healthy flight - reproduced: a craft at 11,500 m that had
        # simply not reached its arming window yet returned a single bogus 0 m read and
        # was ASSERT-FAILed on the spot, with a reason confidently asserting the arm
        # frame "was never sampled". At a 0.5 s poll and a ~300 m/s fall, waiting for a
        # second corroborating frame costs ~150 m against a 2500 m floor.
        if below_floor >= B1_CANOPY_DEBOUNCE_K:
            return replace(
                state, peak_apoapsis=peak, below_floor_streak=below_floor, done=True,
                verdict=MISSION_ASSERT_FAIL,
                loss_reason=_b1_loss_reason_with_altitude(
                    state,
                    "chute-arm-window-missed: fell below the %.0fm full-deploy altitude "
                    "at %.1fm/s on %d consecutive frames without ever entering the "
                    "%.0fm/s arming window"
                    % (state.params.chute_full_deploy_alt, snapshot.vertical_speed,
                       below_floor, state.params.chute_arm_max_rate))), actions

        stayed = _b1_stay_or_flake(state, snapshot, peak)
        return replace(stayed, chute_deployed=chute_deployed,
                       chute_armed_altitude=armed_alt,
                       chute_armed_rate=armed_rate,
                       below_floor_streak=below_floor), actions

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
    DESCENT phase, the canopy OBSERVED open, AND the craft was last seen at/below
    downMaxAltMeters (option A: flew + canopy opened + REACHED THE GROUND).

    Two conjuncts each closed a real hole:
      - the ALTITUDE leg (Fable review of PR #1335, SF-1): without it a chute-ripped
        mid-air breakup at 1800 m was awarded DOWN.
      - the OBSERVED-canopy leg (2026-07-25): the conjunct used to be the machine's own
        COMMANDED ``chute_deployed`` latch, which is set the moment the arm action is
        emitted and stays true whether or not the canopy ever opens. The fixture's
        ``automateSafeDeploy = 0`` made that routine rather than exotic -- every B1 run
        armed a chute that never left ARMED and impacted at terminal velocity, and
        every one of them was awarded the "chute-deployed impact" success terminal.
        Only the live ``craft_chute_state`` read can distinguish the two."""
    return (state.phase == B1_DESCENT
            and state.craft_chute_full_seen
            and state.last_finite_altitude is not None
            and _is_finite(state.last_finite_altitude)
            and state.last_finite_altitude <= state.params.down_max_alt)


def _b1_loss_reason_with_altitude(state: B1State, base: str) -> str:
    """Append the last known altitude and the OBSERVED chute state to a loss reason so
    a DOWN-ineligible loss names WHERE the craft was lost and WHAT the canopy was
    doing. ``craftChute=Armed`` is the inert-chute signature: commanded but never
    open. ``UNREAD`` means the runner never opted into the chute read (or every read
    faulted), which fails the DOWN gate closed by design."""
    parts = []
    if state.last_finite_altitude is not None and _is_finite(state.last_finite_altitude):
        parts.append("last altitude %.0fm" % state.last_finite_altitude)
    parts.append("craftChute=%s" % (state.last_chute_state or "UNREAD"))
    parts.append("canopyObserved=%s" % ("yes" if state.craft_chute_full_seen else "no"))
    parts.append("armCommanded=%s" % ("yes" if state.chute_deployed else "no"))
    return "%s; %s" % (base, ", ".join(parts))


def _b1_enter_down(state: B1State, ut: float, peak: Optional[float]) -> B1State:
    """DOWN success terminal (operator decision 2026-07-20, option A): the craft
    reached the ground under an OBSERVED canopy and broke apart / became unreadable
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
# CL-1 phase state machine (mission cl1_pod_impact). Pure. THE CREW-LOSS ATOM.
#
# A crewed pod launches, does not deploy a chute, and hits the ground. The crew
# dies. See the CL1_* constants above for the MISSION-OK inversion and why the
# roster is the channel.
#
# THREE PHASES, and the shape is smaller than B1's on purpose. B1 splits
# ASCENT / COAST / DESCENT because it ACTS at each boundary (cut throttle at
# burnout, arm the chute at the apex). CL-1 acts exactly once, on the first
# frame, and then only WATCHES: there is no second action to schedule, so a
# boundary the machine cannot use is a budget clock nobody reads. What the
# collapse costs is per-leg budget attribution, which B1 already owns for this
# exact craft on this exact fixture.
#
#   PRELAUNCH -> FLIGHT   on the first decision: arm the roster watch, set
#                         throttle, activate the next stage. The fixture's craft
#                         sits at `stg = 2` with the RT-5 booster at `istg = 1`
#                         and the parachute at `istg = 0`, so ONE stage
#                         activation ignites the booster and leaves the chute
#                         stowed - which is the entire flight profile.
#   FLIGHT   -> CREW-LOST when the watched kerbal's OBSERVED roster status reads
#                         not-alive on CL1_ROSTER_DEBOUNCE_K consecutive frames,
#                         having earlier been OBSERVED alive and aboard. SUCCESS.
#
# THREE NAMED NON-SUCCESS ENDS, each of which exists because the outcome it names
# is DETERMINISTIC and would otherwise be reported as an unnamed budget timeout
# (the B1 `chute-arm-window-missed` lesson: a deterministic failure filed in the
# flake bucket names nothing and pollutes the flake ledger):
#   crew-watch-name-unknown - the watched name was OBSERVED absent from the
#       roster before the kerbal was ever seen aboard. Almost always a misspelled
#       `crewName` in the spec. ASSERT-FAIL, within a couple of polls of launch.
#   crew-survived-impact    - the craft came to REST (a landed/splashed
#       situation, debounced) with the kerbal still alive. The direct negation of
#       this mission's subject, so it is a mission failure, not a flake.
#   roster-channel-lost     - the roster read has been UNREAD for
#       CL1_ROSTER_UNREAD_GIVEUP_FRAMES consecutive frames. This one is a FLAKE,
#       not an assert-fail, and the distinction is the point: the roster read
#       does not depend on the vessel, so an unreadable roster means the kRPC
#       channel is broken rather than the flight having gone wrong. FLAKE is
#       retryable in hlib, which is the correct treatment for a broken channel.
#
# NOTE what is NOT a terminal here: `snapshot.vessel_lost`. Everywhere else in
# this library it is one. Here the pod being destroyed is the EXPECTED midpoint
# of the flight, not its end - the machine still needs the roster frame that
# says the kerbal died with it, and the runner keeps supplying one because the
# roster read survives the vessel-lost path. A vessel_lost frame therefore just
# flows through: its roster reading is consumed like any other, and if the roster
# is ALSO unread the `roster-channel-lost` give-up owns the outcome.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Cl1Params:
    """CL-1 tuning (spec [driver.missionParams] for cl1_pod_impact). Windows,
    budgets and identifiers only - never a golden trajectory."""
    throttle: float
    crew_name: str
    flight_timeout: float
    landed_situations: Tuple[str, ...]


def cl1_params_from_dict(params: Dict) -> Cl1Params:
    """Build Cl1Params from the spec's missionParams dict (shape + ranges are
    enforced upstream by hlib against cl1_pod_impact.schema.toml)."""
    params = params or {}
    return Cl1Params(
        throttle=float(params.get("throttle", 1.0)),
        crew_name=str(params.get("crewName", "")),
        flight_timeout=float(params.get("flightTimeoutSeconds", 300.0)),
        landed_situations=tuple(params.get("landedSituations", ("LANDED", "SPLASHED"))),
    )


@dataclass(frozen=True)
class Cl1State:
    """CL-1 machine state. ``verdict`` is None while running and at the CREW-LOST
    success terminal (the assertions decide there, exactly as at B1's LANDED); it
    is MISSION-FLAKE on a budget overrun or on ``roster-channel-lost``, and
    MISSION-ASSERT-FAIL on the three named failure ends (``crew-watch-unnamed``,
    ``crew-watch-never-aboard``, ``crew-survived-impact``), which also set
    ``loss_reason``. ``loss_reason`` is NEVER set by the success terminal, which
    is the whole inversion (see the CL1_* constants)."""
    params: Cl1Params
    phase: str = CL1_PRELAUNCH
    phase_entry_ut: float = 0.0
    # Last FINITE altitude from a live frame. Read by _cl1_evidence, so every
    # failure reason names WHERE the craft was. (There is deliberately no peak:
    # the result JSON's peakAltitude is derived from the frames by
    # evaluate_cl1_assertions, and a second machine-side copy of it was dead
    # weight - Opus review panel 2026-07-28.)
    last_finite_altitude: Optional[float] = None
    # The last non-empty OBSERVED roster reading. DIAGNOSTIC: it names what the
    # channel was actually saying in every failure reason.
    last_roster_status: str = ""
    # STICKY precondition latch: the kerbal was OBSERVED Assigned (alive and
    # aboard a vessel) on CL1_ROSTER_DEBOUNCE_K consecutive frames. The success
    # terminal is gated on it, so a fixture whose kerbal was already dead at load
    # can never pass instantly and green.
    crew_alive_aboard_seen: bool = False
    alive_aboard_streak: int = 0
    # Consecutive not-alive / never-aboard / landed-while-alive / unread runs
    # behind the debounced terminals. Each resets to 0 on any disagreeing frame
    # (fail-closed: the run of agreement must be unbroken).
    not_alive_streak: int = 0
    never_aboard_streak: int = 0
    landed_alive_streak: int = 0
    roster_unread_streak: int = 0
    # Evidence carried out of the success terminal for the result JSON.
    crew_loss_ut: Optional[float] = None
    crew_loss_status: str = ""
    # True once any frame reported the active vessel unreadable. Evidence only -
    # it gates nothing (see the machine note above).
    vessel_lost_seen: bool = False
    phases_reached: Tuple[str, ...] = (CL1_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    flake_reason: Optional[str] = None
    loss_reason: Optional[str] = None
    done: bool = False
    # The subject of this mission is a DESTROYED craft, so the shell's settle
    # tail would only gather vessel_lost / debris frames. Same rationale as B1's
    # DOWN terminal.
    skip_settle_tail: bool = False


def cl1_initial_state(params: Cl1Params) -> Cl1State:
    """Fresh CL-1 machine at PRELAUNCH."""
    return Cl1State(params=params)


def _cl1_over_budget(state: Cl1State, snapshot: TelemetrySnapshot) -> bool:
    """True iff FLIGHT has out-run ``flightTimeoutSeconds`` by ``snapshot.ut``.
    PRELAUNCH and the terminal are untimed; a non-finite UT never trips the
    timeout (the shell's outer wall watchdog is the backstop)."""
    if state.phase != CL1_FLIGHT:
        return False
    if not _is_finite(snapshot.ut):
        return False
    return (snapshot.ut - state.phase_entry_ut) > state.params.flight_timeout


def _cl1_evidence(state: Cl1State) -> str:
    """The evidence tail every CL-1 reason string carries, so a failure names
    WHERE the craft was and WHAT the roster channel was actually saying."""
    parts = []
    if state.last_finite_altitude is not None and _is_finite(state.last_finite_altitude):
        parts.append("last altitude %.0fm" % state.last_finite_altitude)
    parts.append("lastRoster=%s" % (state.last_roster_status or "UNREAD"))
    parts.append("aliveAboardObserved=%s"
                 % ("yes" if state.crew_alive_aboard_seen else "no"))
    parts.append("vesselLostSeen=%s" % ("yes" if state.vessel_lost_seen else "no"))
    return ", ".join(parts)


def cl1_decide(state: Cl1State, snapshot: TelemetrySnapshot) -> Tuple[Cl1State, List[Action]]:
    """Advance the CL-1 crew-loss machine one frame; return (new_state, actions).

    Ordering inside a frame is load-bearing and is stated once here:
      1. carry the evidence (peak / last altitude / last roster reading);
      2. advance the roster streaks from THIS frame's reading;
      3. the UNREAD give-up, so a dead channel is named before anything is
         concluded from its silence;
      4. the name-unknown ASSERT-FAIL, so a misspelled crewName is named before
         its permanent NotInRoster reading can be mistaken for a crew loss;
      5. the CREW-LOST success terminal;
      6. the crew-survived ASSERT-FAIL - AFTER the loss check, so a frame that
         reads both "debris at rest" and "kerbal dead" resolves as the death it
         is rather than as a survival;
      7. the phase budget.
    Once ``done`` the machine is idempotent (state unchanged, no actions).
    """
    if state.done:
        return state, []

    # --- 1. evidence ------------------------------------------------------
    if not snapshot.vessel_lost and _is_finite(snapshot.altitude):
        state = replace(state, last_finite_altitude=snapshot.altitude)
    if snapshot.vessel_lost and not state.vessel_lost_seen:
        state = replace(state, vessel_lost_seen=True)

    status = snapshot.crew_roster_status or ROSTER_STATUS_UNREAD
    if status:
        state = replace(state, last_roster_status=status)

    # --- PRELAUNCH: arm the watch, ignite, and enter FLIGHT ----------------
    # Emitted BEFORE the roster gates are evaluated, because on the very first
    # frame the channel is necessarily unread (the watch is being armed by this
    # same action) and no gate should read that as evidence.
    if state.phase == CL1_PRELAUNCH:
        # crew-watch-unnamed: refuse to fly rather than fly blind. An empty
        # crewName passes the schema (hlib's "string" check is an isinstance),
        # arms a watch the runner treats as UNARMED, and would then read "" on
        # every frame - surfacing minutes later as `roster-channel-lost`, which
        # is a RETRYABLE flake blaming the kRPC channel for a spec typo. Named
        # here, before anything is ignited, for the same reason the other three
        # terminals are named: a deterministic outcome must not land unnamed in
        # the flake bucket.
        if not state.params.crew_name:
            return replace(
                state, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("crew-watch-unnamed: missionParams.crewName is empty, "
                             "so there is no kerbal to watch and no statement about "
                             "a crew could ever be made")), []
        actions = [Action(ACTION_SET_ROSTER_WATCH, text=state.params.crew_name),
                   Action(ACTION_SET_THROTTLE, state.params.throttle),
                   Action(ACTION_ACTIVATE_STAGE)]
        entry = snapshot.ut if _is_finite(snapshot.ut) else state.phase_entry_ut
        return replace(state, phase=CL1_FLIGHT, phase_entry_ut=entry,
                       phases_reached=state.phases_reached + (CL1_FLIGHT,)), actions

    # --- 2. roster streaks -------------------------------------------------
    unread = (status == ROSTER_STATUS_UNREAD)
    not_alive = (status in ROSTER_STATUS_NOT_ALIVE)
    alive_aboard = (status == ROSTER_STATUS_ASSIGNED)
    state = replace(
        state,
        roster_unread_streak=(state.roster_unread_streak + 1) if unread else 0,
        not_alive_streak=(state.not_alive_streak + 1) if not_alive else 0,
        alive_aboard_streak=(state.alive_aboard_streak + 1) if alive_aboard else 0,
        # Any SETTLED non-Assigned reading. Distinct from not_alive_streak because
        # `Available` is neither: the kerbal is alive and simply not aboard.
        # UNREAD breaks it (fail-closed - a blind frame proves nothing either way).
        never_aboard_streak=(0 if (unread or alive_aboard)
                             else state.never_aboard_streak + 1),
    )
    if (not state.crew_alive_aboard_seen
            and state.alive_aboard_streak >= CL1_ROSTER_DEBOUNCE_K):
        state = replace(state, crew_alive_aboard_seen=True)

    # --- 3. roster-channel-lost (NAMED FLAKE, not a failure) ---------------
    if state.roster_unread_streak >= CL1_ROSTER_UNREAD_GIVEUP_FRAMES:
        return replace(
            state, done=True, verdict=MISSION_FLAKE, flake_phase=state.phase,
            flake_reason=("roster-channel-lost: the crew roster read was UNREAD on "
                          "%d consecutive frames, so no statement about the crew can "
                          "be made either way; %s"
                          % (state.roster_unread_streak, _cl1_evidence(state)))), []

    # --- 4. crew-watch-never-aboard (ASSERT-FAIL, fast) --------------------
    # A SETTLED reading that is not Assigned, BEFORE the kerbal was ever observed
    # alive and aboard, means the flight can never make the statement it exists to
    # make. All three causes are deterministic and all three used to burn the whole
    # FLIGHT budget into an unnamed "phase FLIGHT timed out" flake, which is the
    # anti-pattern this module keeps naming:
    #   NotInRoster - a misspelled `crewName`, the likeliest authoring error;
    #   Dead / Missing - the fixture's kerbal was ALREADY dead at load, which is
    #     exactly the fault the alive-aboard precondition exists to catch, so the
    #     machine should SAY it rather than merely refuse to succeed;
    #   Available - the kerbal is in the roster but was never aboard a vessel
    #     (an empty pod, or the spec naming a kerbal who is not the crew).
    # Debounced like every other terminal, and scoped by the alive-aboard latch so
    # the SAME readings later in the flight are counted as a crew loss instead.
    if (not state.crew_alive_aboard_seen and not unread
            and status != ROSTER_STATUS_ASSIGNED
            and state.never_aboard_streak >= CL1_ROSTER_DEBOUNCE_K):
        hint = ("no kerbal of that name is in this save's crew roster"
                if status == ROSTER_STATUS_NOT_IN_ROSTER else
                "the kerbal was already not alive before the flight began"
                if status in ROSTER_STATUS_NOT_ALIVE else
                "the kerbal is in the roster but was never aboard a vessel")
        return replace(
            state, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason=("crew-watch-never-aboard: %r read %s on %d consecutive "
                         "frames and was never observed Assigned - %s; %s"
                         % (state.params.crew_name, status,
                            state.never_aboard_streak, hint,
                            _cl1_evidence(state)))), []

    # --- 5. CREW-LOST: the SUCCESS terminal --------------------------------
    # The `crew_alive_aboard_seen` conjunct is DELIBERATELY REDUNDANT with step 4
    # and is kept anyway. Redundant because every not-alive frame is also a
    # never-aboard frame and the two streaks reset on overlapping sets, so a
    # not-alive run reaching K without the latch always trips step 4 first - which
    # is why the mutation that deletes this conjunct is an EQUIVALENT mutant and
    # survives the suite (`test_the_success_terminal_is_unreachable_without_the_
    # latch` proves the property holds either way). Kept because it is the direct
    # statement of the invariant that matters - a fixture whose kerbal was already
    # dead can never be awarded the success terminal - and a future narrowing of
    # step 4 must not silently make that reachable.
    if state.crew_alive_aboard_seen and state.not_alive_streak >= CL1_ROSTER_DEBOUNCE_K:
        entry = snapshot.ut if _is_finite(snapshot.ut) else state.phase_entry_ut
        return replace(
            state, phase=CL1_CREW_LOST, phase_entry_ut=entry,
            phases_reached=state.phases_reached + (CL1_CREW_LOST,),
            crew_loss_ut=(snapshot.ut if _is_finite(snapshot.ut) else None),
            crew_loss_status=status,
            done=True, skip_settle_tail=True), []

    # --- 6. crew-survived-impact (ASSERT-FAIL) -----------------------------
    # The craft came to rest with the kerbal alive: the direct negation of this
    # mission's subject. Three conjuncts, and the second closed a real hole
    # (Opus review panel 2026-07-28, reviewer 1, finding 1):
    #   - live frames only: a vessel_lost snapshot carries the benign default
    #     situation "" and must not be read as a landing;
    #   - AND the roster must not read not-alive ON THIS FRAME. Step 5 only
    #     pre-empts this check when the death debounce is ALREADY COMPLETE, and
    #     the ordinary shape of the real event is staggered, not simultaneous:
    #     the wreck settles into LANDED and the roster flips one ~0.5 s poll
    #     later. Without this conjunct the sequence (LANDED+Assigned,
    #     LANDED+Dead) completed the SURVIVED streak one frame before the death
    #     streak, and red a successful flight with a reason that contradicted
    #     itself inside one line ("with the crew still alive; ... roster=Dead").
    #     With it, any not-alive frame RESETS the survival streak, so the death
    #     always wins the race it is actually in.
    #   - AND the roster must not be UNREAD on this frame, the same fail-closed
    #     rule the never-aboard streak states ("a blind frame proves nothing
    #     either way"). This terminal CONDEMNS, and its reason asserts the crew
    #     was ALIVE, so every frame it counts must have OBSERVED that. Without
    #     the conjunct a channel that went blind at touchdown completed the
    #     survival streak (K=2) four frames before the unread give-up (6) could
    #     name it a retryable roster-channel-lost flake - and a blind frame
    #     AFTER a single Dead reading re-created the self-contradicting reason
    #     line ("with the crew still alive; ... lastRoster=Dead") the not-alive
    #     conjunct exists to prevent.
    landed = (not snapshot.vessel_lost and not unread and not not_alive
              and snapshot.situation in state.params.landed_situations)
    state = replace(state,
                    landed_alive_streak=(state.landed_alive_streak + 1) if landed else 0)
    if state.landed_alive_streak >= CL1_ROSTER_DEBOUNCE_K:
        return replace(
            state, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason=("crew-survived-impact: the craft reached a %s situation on "
                         "%d consecutive frames with the crew still alive; %s"
                         % (snapshot.situation, state.landed_alive_streak,
                            _cl1_evidence(state)))), []

    # --- 7. the phase budget ----------------------------------------------
    if _cl1_over_budget(state, snapshot):
        return replace(state, done=True, verdict=MISSION_FLAKE,
                       flake_phase=state.phase), []
    return state, []


def evaluate_cl1_assertions(frames, params: Cl1Params,
                            state: Optional[Cl1State] = None) -> List[AssertionOutcome]:
    """Evaluate the CL-1 driver-validity assertions.

    Both read OBSERVED state carried out of the machine, never a commanded latch,
    and both are deliberately about the KERBAL rather than the craft - "did the
    pod break up" is a claim about wreckage, "is this kerbal dead" is the claim
    this scenario exists to make.

    - ``crewAliveAboardObserved``: the watched kerbal READ Assigned on
      CL1_ROSTER_DEBOUNCE_K consecutive frames at some point in the flight. The
      precondition half: without it a fixture whose kerbal was dead before the
      run began would satisfy the second assertion trivially.
    - ``crewLostObserved``: the machine reached CL1_CREW_LOST, i.e. the watched
      kerbal READ a not-alive roster status on CL1_ROSTER_DEBOUNCE_K consecutive
      frames AFTER the precondition was met. The value is the terminal status
      actually read (``Dead`` / ``Missing`` / ``NotInRoster``), so the result JSON
      records WHICH not-alive reading this fixture's difficulty flags produced
      rather than merely that one of them did.

    Machine-carried rather than re-derived from ``frames`` on purpose: the
    alive-aboard latch is STICKY (a kerbal who was aboard and is now dead must
    not have that erased by the frames that follow), and the frames after the
    terminal are debris. ``frames`` is accepted for signature symmetry with every
    other evaluator and is used only for the flight-evidence peak."""
    peak = _peak_finite(list(frames or []), lambda f: f.altitude)
    alive_seen = bool(getattr(state, "crew_alive_aboard_seen", False))
    reached = getattr(state, "phase", None) == CL1_CREW_LOST
    terminal_status = str(getattr(state, "crew_loss_status", "") or "")
    loss_ut = getattr(state, "crew_loss_ut", None)
    return [
        AssertionOutcome("crewAliveAboardObserved", alive_seen,
                         ROSTER_STATUS_ASSIGNED if alive_seen else
                         (getattr(state, "last_roster_status", "") or None),
                         {"debounceK": CL1_ROSTER_DEBOUNCE_K,
                          "crewName": params.crew_name,
                          "peakAltitude": peak}),
        AssertionOutcome("crewLostObserved", bool(reached and alive_seen),
                         terminal_status or None,
                         {"accepted": list(ROSTER_STATUS_NOT_ALIVE),
                          "debounceK": CL1_ROSTER_DEBOUNCE_K,
                          "crewLossUt": loss_ut,
                          "lastRosterStatus":
                              getattr(state, "last_roster_status", "") or None}),
    ]


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
    # Consecutive frames every EVA-window conjunct has held. The transition fires at
    # EVA4_WINDOW_DEBOUNCE_K; any non-open frame resets it to 0 (fail-closed).
    window_open_streak: int = 0
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

    PER-FRAME ONLY. The machine does NOT act on one True: ``eva4_decide`` requires
    EVA4_WINDOW_DEBOUNCE_K consecutive True frames before it terminates into EVA-WINDOW,
    because stock flips ParachuteState to DEPLOYED at the START of the ~8 s canopy
    animation and a single glitched kRPC frame would otherwise certify a terminal-velocity
    EVA as green.

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
        DESCENT -> EVA-WINDOW once ``eva4_window_open`` has held for
        EVA4_WINDOW_DEBOUNCE_K CONSECUTIVE frames (any non-open frame resets the run).
        That is the SUCCESS terminal: the craft is airborne, crewed, and under an
        OBSERVED full canopy, and the seam takes over.
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

        # K-CONSECUTIVE DEBOUNCE on the window (EVA4_WINDOW_DEBOUNCE_K): the handoff is
        # irreversible and two of the five conjuncts are single-sample kRPC reads, so a
        # lone glitched frame must never certify it. Any non-open frame resets the run.
        streak = state.window_open_streak + 1 if eva4_window_open(state.params, snapshot) else 0

        if streak >= EVA4_WINDOW_DEBOUNCE_K:
            opened = _eva4_enter(state, EVA4_EVA_WINDOW, snapshot.ut, peak)
            return replace(opened, chute_armed=armed, chute_armed_altitude=armed_alt,
                           chute_armed_rate=armed_rate, craft_chute_full_seen=full_seen,
                           window_open_streak=streak,
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
                craft_chute_full_seen=full_seen, window_open_streak=streak, done=True,
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
                       chute_armed_rate=armed_rate, window_open_streak=streak,
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
# R1 rewind-loop phase state machine (mission r1_rewind_loop). Pure.
#
# The flight leg is NOT re-implemented: R1 COMPOSES the live-proven B2 machine
# (b2_decide) by carrying a nested B2State and delegating every ASCENT frame to
# it verbatim. When the nested machine reaches its own terminal, R1 takes over
# and drives the rewind cycle through the GENERALIZED seam command path:
#
#     ASCENT (delegated b2_decide)  ->  COMMIT (seam CommitTree)
#          ->  STOP (seam StopRecording)  ->  RECORDER-IDLE (seam RecordingState)
#          ->  REWIND (seam InvokeRewind) ->  VERIFY  ->  REWOUND
#          ->  RELAUNCH  ->  LOOP-POINTS (seam RecordingState)
#          ->  LOOP-CLOSED (terminal)
#
# WHY THE LOOP HAS A SECOND FLIGHT (flight 2, 2026-07-26). Flight 2 rewound
# correctly - MISSION-OK, `clockRewound value=267.832`, the craft back at
# PRE_LAUNCH - and the RUN still classified PARSEK-FAIL, because a rewind that is
# never flown again is only HALF a loop. R1 tore down immediately, so the re-fly
# session's provisional recording accumulated ZERO Points, and the merge refused
# to write supersede rows:
#   [Parsek][WARN][Supersede] AppendRelations invariant violation:
#       provisional=rec_aa59a4... reason=empty Points
#   [Parsek][ERROR][MergeDialog] TryCommitReFlySupersede: orchestrator threw ...
# So REWOUND is now a one-frame WAYPOINT that commands a second flight, and two
# further OBSERVED gates close the loop: RELAUNCH requires the rewound craft to
# have physically CLIMBED (altitude gained over the post-rewind reading - a dead
# engine cannot produce it), and LOOP-POINTS requires a RecordingState reply to
# read `points > 0` - i.e. that the second flight was recorded SOMEWHERE. Flying
# and being recorded are different facts, and it is a RECORDING that has to be
# non-empty for the merge to write anything.
#
# WHAT LOOP-POINTS CANNOT SEE (flight 3, and this is a real limit of the gate, not
# a nit). `RecordingState.points` is `RecorderStateSnapshot.bufferedPoints` - the
# LIVE recorder's count, for whatever recording happens to be live - NOT the
# re-fly provisional's. Flight 3 read `points=24 tree=820de77e` while the merge
# was simultaneously refusing `provisional=rec_5b0697a6... reason=empty Points`:
# the second flight was recorded into a DIFFERENT tree, and this gate passed
# anyway. So the gate proves "the post-rewind flight was recorded somewhere", and
# nothing stronger. The reply's `tree` field is captured as row evidence so that
# divergence is visible in the result JSON instead of only in KSP.log, but
# "the points landed in the PROVISIONAL" is NOT observable through any seam verb
# today - no verb exposes the re-fly marker's provisional id. Treat the
# `recordedTree` row detail as the thing an operator must eyeball.
#
# WHY STOP + RECORDER-IDLE EXIST (flight 1, 2026-07-26, INVALID(autopilot-flake)).
# The first live run went COMMIT -> REWIND and Parsek's own dispatcher answered
# `reject id=<step>.rewind cmd=InvokeRewind reason=recording-active`.
# `TestCommandDispatcher.DecideDispatch` refuses InvokeRewind whenever
# `state.Recording` (= `ParsekFlight.HasLiveRecorderForTagging()`), deliberately:
# a re-fly reloads the scene and would SILENTLY DISCARD a live recording.
#
# The cause is NOT that CommitTree leaves a recorder running - it does the
# opposite. `ParsekFlight.CommitTreeFlight` stops the recorder
# (`recorder?.StopRecording()`) and then NULLS BOTH HANDLES
# (`activeTree = null; recorder = null`, ParsekFlight.cs), so
# HasLiveRecorderForTagging is false the instant it returns. What happens is that
# a NEW recording BEGINS ~14 ms later: CommitTreeFlight leaves the active vessel
# live and marks its recording `VesselSpawned = true`, and on the next frame
# `ParsekFlight.TryRestoreCommittedTreeForSpawnedActiveVessel` re-adopts the
# just-committed tree copy-on-write and starts a fresh `promotion` recording on
# the surviving stage. Flight-1 log, in order:
#   22:12:29.393  Recording stopped. 239 points          (CommitTreeFlight)
#   22:12:29.673  exec id=0003.commit verdict=OK
#   22:12:29.681  Armed committed-tree restore attempt for 'Kerbal X'
#   22:12:29.687  Recording started: parts=28, points=0, promotion
#   22:12:30.402  reject id=0003.rewind reason=recording-active
# That promotion is CORRECT Parsek behaviour (commit-then-keep-flying), so the
# machine must adapt to it, not the other way round.
#
# The fix is NOT merely "insert a StopRecording in the right order". Ordering is
# an ASSUMPTION; the machine now carries the dispatcher's gate as an OBSERVED
# PRECONDITION: after StopRecording it POLLS `RecordingState` and refuses to
# command the rewind until the reply's payload reads `recording=false`. That
# reading fails CLOSED by construction: `RecordingState`'s `recording` is
# `ParsekFlight.Instance != null && recorder.IsRecording`
# (`RecorderStateSnapshot.CaptureFromParts`: `snap.isRecording = recorder.IsRecording`),
# which is a SUPERSET of the dispatcher's `activeTree != null && recorder != null
# && recorder.IsRecording` - so `recording=false` GUARANTEES the dispatcher's
# gate is open, while a spurious `true` only ever costs another probe.
#
# WHY VERIFY EXISTS AT ALL (the single most important thing in this machine).
# "We wrote InvokeRewind to the channel and the seam answered verdict=OK" is a
# COMMANDED reading: it says the actor believes it acted. This codebase has been
# burned five times by gating on exactly that shape (the CAPTURE-BURN
# NodeExecutor, B1's chute, EVA-4's ladder release, the B-DOCK docking AP, and
# the EVA-4 fail-open still open today). So the seam token only opens the door
# to VERIFY; the ADVANCE is an OBSERVATION that a rewind is the only thing that
# could have produced: THE GAME CLOCK RAN BACKWARD. A Rewind-to-Separation loads
# an earlier RewindPoint quicksave, so kRPC's `space_center.ut` must read LOWER
# after the cycle than the value stamped before it. Nothing in normal flight can
# do that (UT is monotonic; no TimeJump is issued on this lane), so the reading
# is not a proxy for the rewind, it IS the rewind.
#
# GAME-TIME BUDGETS ARE UNUSABLE PAST THE REWIND. Every other machine here bounds
# a phase with `snapshot.ut - phase_entry_ut > budget`. After a rewind that
# difference is NEGATIVE and only grows less positive, so a game-time budget in
# REWIND / VERIFY can NEVER expire: the phase would hang until the whole mission
# budget killed it, with no named give-up. R1 therefore bounds every post-ascent
# phase by a FRAME count instead, and every one of those give-ups is distinctly
# named.
# ---------------------------------------------------------------------------

R1_ASCENT = "ASCENT"
R1_COMMIT = "COMMIT"
R1_STOP = "STOP"
R1_RECORDER_IDLE = "RECORDER-IDLE"
R1_REWIND = "REWIND"
R1_VERIFY = "VERIFY"
R1_REWOUND = "REWOUND"
R1_RELAUNCH = "RELAUNCH"
R1_LOOP_POINTS = "LOOP-POINTS"
R1_LOOP_CLOSED = "LOOP-CLOSED"
R1_PHASES: Tuple[str, ...] = (R1_ASCENT, R1_COMMIT, R1_STOP, R1_RECORDER_IDLE,
                              R1_REWIND, R1_VERIFY, R1_REWOUND, R1_RELAUNCH,
                              R1_LOOP_POINTS, R1_LOOP_CLOSED)

# Per-command tags. Distinct by construction, so the wire ids
# ("<reserved>.commit" / ".stop" / ".state0" / ".rewind") can never collide --
# the C# seam skips duplicate ids, which would make the later command a SILENT
# no-op whose poll then expires as a TIMEOUT that looks like a wedged addon.
# LIVE-CONFIRMED on flight 1: KSP.log carried `id=0003.commit` and
# `id=0003.rewind` as separate commands, and REWIND did not advance on COMMIT's
# OK.
R1_TAG_COMMIT = "commit"
R1_TAG_STOP = "stop"
R1_TAG_REWIND = "rewind"


def r1_state_probe_tag(probe: int) -> str:
    """The tag for RecordingState probe ``probe`` (``state0``, ``state1``, ...).

    The recorder-idle poll issues the SAME verb repeatedly, so each probe needs
    its OWN tag: reusing one tag would reuse one wire id, and the C# seam skips
    duplicate ids - every probe after the first would be silently dropped and the
    machine would poll a reply that was never going to come."""
    return "state%d" % int(probe)


def r1_loop_probe_tag(probe: int) -> str:
    """The tag for the post-rewind points probe ``probe`` (``loop0``, ...). A
    SEPARATE family from ``state*`` so a LOOP-POINTS probe can never collide with
    a RECORDER-IDLE probe id even at the same index."""
    return "loop%d" % int(probe)


@dataclass(frozen=True)
class R1Params:
    """R1 rewind-loop tuning. The ascent half is the delegated B2 machine's own
    params verbatim (no new ascent tuning surface); everything added here bounds
    the rewind cycle or expresses the OBSERVED gate.

    ``rewind_point_id`` / ``rewind_slot`` carry fail-closed UNREAD sentinels
    ("" / -1) in the same discipline as ``node_executor_enabled``'s -1: a mission
    launched without a resolvable rewind target must name that on the FIRST frame
    instead of flying a whole ascent and discovering it at the top."""
    b2: B2Params
    # The InvokeRewind target. "" / -1 = UNREAD -> the first frame fails closed
    # with the `rewind-target-unresolved` give-up (never a flown ascent whose
    # rewind leg was never reachable).
    rewind_point_id: str = ""
    rewind_slot: int = -1
    # FRAME budgets (see the section header: game-time budgets cannot bound a
    # phase whose clock runs backward). The seam bridge BLOCKS inside perform()
    # for the whole poll window, so a terminal token normally lands on the very
    # next frame; these bounds exist for the case where the action never
    # executed at all (no seam configured -> an immediate ERROR token) or the
    # result never rides a snapshot.
    commit_frames: int = 40
    stop_frames: int = 40
    # RECORDER-IDLE's bound. Each frame in the phase either issues one
    # RecordingState probe or reads its reply, so this is ~half that many probes.
    idle_frames: int = 40
    rewind_frames: int = 40
    verify_frames: int = 40
    # THE OBSERVED GATE: how many seconds the game clock must have run BACKWARD
    # across the cycle before the rewind counts as having happened. Any positive
    # value proves a load of an earlier state; the floor keeps a float-noise or
    # sub-second read from qualifying.
    min_ut_regression: float = 1.0
    # Corroboration tolerance: how far the post-rewind altitude must differ from
    # the pre-rewind altitude for the world state (not merely the clock) to read
    # as having changed. Metres.
    min_altitude_change: float = 100.0
    # ---- CLOSING THE LOOP (post-rewind re-fly) ----
    # A rewind alone is only half a loop. These drive the SECOND flight: throttle
    # up + stage on the rewound craft, then require that it actually LEFT THE
    # GROUND and that the re-fly's provisional recording actually ACCUMULATED
    # POINTS. Without this leg the provisional stays empty, which is the exact
    # condition that made flight 2 red (see the section header).
    relaunch_throttle: float = 1.0
    # Metres of altitude gained over the post-rewind reading before the relaunch
    # counts as powered flight. Not a target altitude - a floor that a craft
    # sitting on the pad with a dead engine can never cross.
    relaunch_min_altitude_gain: float = 100.0
    relaunch_frames: int = 240
    # Bound on the post-rewind RecordingState poll (points > 0).
    loop_points_frames: int = 40


def r1_params_from_dict(params: Dict) -> R1Params:
    """Build ``R1Params`` from a spec ``missionParams`` dict. The ascent block is
    parsed by ``b2_params_from_dict`` over the SAME dict, so the B2 keys keep
    their exact spellings and defaults."""
    params = params or {}
    return R1Params(
        b2=b2_params_from_dict(params),
        rewind_point_id=str(params.get("rewindPointId", "") or ""),
        rewind_slot=int(params.get("rewindSlot", -1)),
        commit_frames=int(params.get("commitFrames", 40)),
        stop_frames=int(params.get("stopFrames", 40)),
        idle_frames=int(params.get("idleFrames", 40)),
        rewind_frames=int(params.get("rewindFrames", 40)),
        verify_frames=int(params.get("verifyFrames", 40)),
        min_ut_regression=float(params.get("minUtRegressionSeconds", 1.0)),
        min_altitude_change=float(params.get("minAltitudeChangeMeters", 100.0)),
        relaunch_throttle=float(params.get("relaunchThrottle", 1.0)),
        relaunch_min_altitude_gain=float(
            params.get("relaunchMinAltitudeGainMeters", 100.0)),
        relaunch_frames=int(params.get("relaunchFrames", 240)),
        loop_points_frames=int(params.get("loopPointsFrames", 40)),
    )


@dataclass(frozen=True)
class R1State:
    """R1 rewind-loop machine state. ``ascent`` is the nested, delegated B2
    machine; ``done`` fires at R1_REWOUND (verdict None -- the assertions judge
    the evidence) or on a named give-up."""
    params: R1Params
    ascent: B2State
    phase: str = R1_ASCENT
    phase_entry_ut: float = 0.0
    # Frames spent in the CURRENT phase (the post-ascent bound; see header).
    phase_frames: int = 0
    phases_reached: Tuple[str, ...] = (R1_ASCENT,)
    # Terminal seam tokens, per command. Carried evidence for the assertions.
    commit_result: str = ""
    stop_result: str = ""
    rewind_result: str = ""
    # PARSEK's OWN refusal reason for a non-OK rewind, decoded off the response
    # `msg` payload ("recording-active", "refly-gate <reason>", "unknown-rp").
    # "" = none read. Flight 1 red with reason=recording-active while the give-up
    # text speculated about the RewindPoint; this field is what stops that
    # recurring.
    rewind_reject_reason: str = ""
    # RECORDER-IDLE evidence. `state_probe` is the NEXT probe index (each probe
    # gets its own tag / wire id); `recorder_idle_reading` is the last `recording`
    # payload value actually read ("" = never read, the fail-closed sentinel);
    # `recorder_idle_observed` latches only on a read of "false".
    state_probe: int = 0
    recorder_idle_reading: str = ""
    recorder_idle_observed: bool = False
    # ---- LOOP-CLOSING evidence (the SECOND flight) ----
    # Peak altitude gain observed over post_rewind_altitude during RELAUNCH. NaN
    # = never measured (fail-closed: an unread channel grants no flight).
    relaunch_altitude_gain: float = float("nan")
    relaunch_situation: str = ""
    # The `points` value READ back off a post-rewind RecordingState reply. -1 =
    # never read (the fail-closed sentinel, same discipline as crew_count): a
    # mission that could not read the count must never satisfy a points gate with
    # a fabricated 0-or-more.
    loop_probe: int = 0
    loop_points_read: int = -1
    # The tree id the post-rewind RecordingState reply named. "" = never read.
    # Evidence only - the mission cannot compare it to the marker's provisional
    # (no seam verb exposes that id), but carrying it puts a flight-3-shaped
    # divergence in the result JSON instead of only in KSP.log.
    loop_recorded_tree: str = ""
    # OBSERVED pre-rewind stamp, taken on the frame InvokeRewind is emitted.
    pre_rewind_ut: float = float("nan")
    pre_rewind_altitude: float = float("nan")
    pre_rewind_situation: str = ""
    pre_rewind_body: str = ""
    # OBSERVED post-rewind readings, taken on the frame the gate opened.
    post_rewind_ut: float = float("nan")
    post_rewind_altitude: float = float("nan")
    post_rewind_situation: str = ""
    post_rewind_body: str = ""
    # pre_rewind_ut - post_rewind_ut. POSITIVE = the clock ran backward = the
    # rewind is OBSERVED. NaN = never measured.
    ut_regression: float = float("nan")
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    flake_reason: Optional[str] = None
    done: bool = False
    loss_reason: Optional[str] = None


def r1_initial_state(params: R1Params) -> R1State:
    """Fresh R1 machine at ASCENT, carrying a fresh nested B2 machine."""
    return R1State(params=params, ascent=b2_initial_state(params.b2))


def _r1_enter(state: R1State, new_phase: str, ut: float) -> R1State:
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        phase_frames=0,
        phases_reached=state.phases_reached + (new_phase,),
        # LOOP-CLOSED, not REWOUND. A rewind that is never flown again is only
        # half a loop -- and it is the half that leaves the re-fly's provisional
        # recording EMPTY, which is what flight 2 (2026-07-26) exposed.
        done=(new_phase == R1_LOOP_CLOSED),
    )


def _r1_flake(state: R1State, reason: str) -> R1State:
    """Terminate with MISSION-FLAKE and a DISTINCTLY NAMED reason. Every give-up
    in this machine routes through here: a bare 'phase X timed out' would not
    tell an operator which of the four ways the cycle can stall actually fired."""
    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase,
                   flake_reason=reason, done=True)


def _r1_seam_result(snapshot: TelemetrySnapshot, tag: str) -> str:
    """The terminal seam token for ``tag``, or "" when the latest generalized
    seam result belongs to a DIFFERENT command (or none has landed).

    The tag check is load-bearing and fails CLOSED: without it the COMMIT
    command's OK would still be riding the snapshot when REWIND first reads it,
    and REWIND would advance on a result InvokeRewind never produced -- the
    stale-result fail-open."""
    if snapshot.seam_command_tag != tag:
        return ""
    return snapshot.seam_command_result or ""


def _r1_seam_payload(snapshot: TelemetrySnapshot, tag: str, key: str) -> str:
    """A single payload field of the terminal seam response for ``tag``, or "".
    Tag-gated for the same fail-closed reason as ``_r1_seam_result``: a payload
    from the PREVIOUS command must never be read as this one's."""
    if snapshot.seam_command_tag != tag:
        return ""
    for k, v in (snapshot.seam_command_payload or ()):
        if k == key:
            return v
    return ""


def _r1_reject_reason(snapshot: TelemetrySnapshot, tag: str) -> str:
    """Parsek's OWN refusal reason for ``tag``'s response, decoded. "" when the
    response carried no ``msg``."""
    return decode_seam_value(_r1_seam_payload(snapshot, tag, "msg"))


def _r1_because(reason: str) -> str:
    """Render a Parsek-supplied refusal reason for a give-up message, or a
    truthful admission that the seam gave none - never a guess at the cause."""
    return ("Parsek's reason: %s" % reason) if reason else \
        "the response carried no msg= reason"


def _r1_commit_action() -> Action:
    return Action(ACTION_PARSEK_SEAM_COMMAND, seam_verb="CommitTree",
                  seam_args=(), seam_tag=R1_TAG_COMMIT)


def _r1_stop_action() -> Action:
    # StopRecording is idempotent-OK by construction: the executor gates on the
    # SAME `HasLiveRecorderForTagging()` predicate the InvokeRewind dispatch guard
    # reads, and answers OK with `stopped=<wasLive> idle=<!wasLive>` either way
    # (ParsekTestCommandAddon.StopRecordingImpl), so issuing it unconditionally is
    # safe whether or not the post-commit promotion actually re-armed a recorder.
    return Action(ACTION_PARSEK_SEAM_COMMAND, seam_verb="StopRecording",
                  seam_args=(), seam_tag=R1_TAG_STOP)


def _r1_state_action(probe: int) -> Action:
    return Action(ACTION_PARSEK_SEAM_COMMAND, seam_verb="RecordingState",
                  seam_args=(), seam_tag=r1_state_probe_tag(probe))


def _r1_loop_action(probe: int) -> Action:
    return Action(ACTION_PARSEK_SEAM_COMMAND, seam_verb="RecordingState",
                  seam_args=(), seam_tag=r1_loop_probe_tag(probe))


def _r1_parse_int(raw: str) -> Optional[int]:
    """Parse a payload integer, or None when it is absent / unparseable. None is
    the FAIL-CLOSED answer: a points gate must never be satisfied by a value the
    mission could not actually read."""
    text = str(raw or "").strip()
    if not text:
        return None
    try:
        return int(text)
    except ValueError:
        return None


def _r1_rewind_action(params: R1Params) -> Action:
    return Action(
        ACTION_PARSEK_SEAM_COMMAND, seam_verb="InvokeRewind",
        seam_args=(("rp", str(params.rewind_point_id)),
                   ("slot", str(int(params.rewind_slot)))),
        seam_tag=R1_TAG_REWIND)


def r1_decide(state: R1State, snapshot: TelemetrySnapshot) -> Tuple[R1State, List[Action]]:
    """Advance the R1 rewind-loop machine one frame; return (new_state, actions).

    ASCENT delegates every frame to ``b2_decide`` over the nested state. On the
    nested terminal: a nested flake / loss propagates as a NAMED R1 give-up (so
    the operator reads which ascent phase died, not a bare R1 flake), and a clean
    B2_ORBIT enters COMMIT.

    COMMIT / REWIND advance ONLY on their own tagged terminal token; VERIFY
    advances only on the OBSERVED backward clock; RELAUNCH advances only on
    MEASURED altitude gain and LOOP-POINTS only on a non-zero point read. Every
    give-up is distinctly named: ``rewind-target-unresolved`` (pre-flight),
    ``commit-seam-<token>`` / ``commit-seam-silent``, ``stop-seam-<token>`` /
    ``stop-seam-silent``, the recorder-never-idle and unreadable-``recording``
    flakes, ``rewind-seam-<token>`` / ``rewind-seam-silent``,
    ``rewind-not-observed``, the never-climbed flake, and the points-stayed-zero
    and unreadable-``points`` flakes.
    """
    if state.done:
        return state, []

    # PRE-FLIGHT FAIL-CLOSED. A mission whose rewind target cannot resolve must
    # say so on frame 1, not after flying a full ascent: the whole flight would
    # otherwise be spent reaching a leg that was never reachable, and the run
    # would read as an expensive ascent flake instead of a spec fault.
    if state.phase == R1_ASCENT and state.phase_frames == 0:
        if not state.params.rewind_point_id or state.params.rewind_slot < 0:
            return _r1_flake(
                state,
                "phase %s: rewind target unresolved before launch "
                "(rewindPointId=%r rewindSlot=%d); InvokeRewind matches the "
                "RewindPoint id EXACTLY, so an unset target can only ever be "
                "REJECTED unknown-rp / unknown-slot"
                % (R1_ASCENT, state.params.rewind_point_id,
                   state.params.rewind_slot)), []

    state = replace(state, phase_frames=state.phase_frames + 1)

    # Runner-signaled vessel loss. SUPPRESSED in REWIND only: InvokeRewind
    # straddles a KSP scene reload, during which the active vessel legitimately
    # ceases to exist. It stays LIVE in VERIFY and everywhere else, so a craft
    # that really was destroyed still terminates (the suppression is one phase
    # wide, not a blanket fail-open).
    if snapshot.vessel_lost and state.phase != R1_REWIND:
        return replace(
            state, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason=("vessel-lost (unreadable after repeated telemetry "
                         "failures) in phase %s" % state.phase)), []

    if state.phase == R1_ASCENT:
        ascent, actions = b2_decide(state.ascent, snapshot)
        st = replace(state, ascent=ascent)
        if not ascent.done:
            return st, actions
        # The nested machine terminated. A loss terminal is a deterministic
        # failure; a flake is a flake; only a clean B2_ORBIT opens the cycle.
        if ascent.loss_reason:
            return replace(st, done=True, verdict=MISSION_ASSERT_FAIL,
                           loss_reason=("ascent leg: %s" % ascent.loss_reason)), actions
        if ascent.verdict is not None:
            return _r1_flake(
                st,
                "phase %s: the delegated B2 ascent leg terminated %s in its "
                "phase %s (the rewind cycle was never reached)"
                % (R1_ASCENT, ascent.verdict, ascent.flake_phase or ascent.phase)), actions
        if B2_ORBIT not in ascent.phases_reached:
            return _r1_flake(
                st,
                "phase %s: the delegated B2 ascent leg reported done with a "
                "clean verdict but never reached %s (phases=%s)"
                % (R1_ASCENT, B2_ORBIT, list(ascent.phases_reached))), actions
        return (_r1_enter(st, R1_COMMIT, snapshot.ut),
                list(actions) + [_r1_commit_action()])

    if state.phase == R1_COMMIT:
        result = _r1_seam_result(snapshot, R1_TAG_COMMIT)
        if result == "OK":
            # STOP the recorder before going anywhere near InvokeRewind. The
            # commit itself already stopped one, but the commit-then-keep-flying
            # promotion starts a NEW one on the surviving stage ~14 ms later
            # (see the section header), and the dispatcher refuses InvokeRewind
            # while any recorder is live.
            return (_r1_enter(replace(state, commit_result="OK"),
                              R1_STOP, snapshot.ut),
                    [_r1_stop_action()])
        if result in ("ERROR", "TIMEOUT"):
            return _r1_flake(
                replace(state, commit_result=result),
                "phase %s: the tree-commit seam command returned %s (%s), so "
                "there is no committed state to rewind FROM"
                % (R1_COMMIT, result,
                   _r1_because(_r1_reject_reason(snapshot, R1_TAG_COMMIT)))), []
        if state.phase_frames > state.params.commit_frames:
            return _r1_flake(
                state,
                "phase %s: the tree-commit seam command never answered within "
                "%d frames (no terminal token rode a snapshot; the seam bridge "
                "may not be configured for this mission)"
                % (R1_COMMIT, state.params.commit_frames)), []
        return state, []

    if state.phase == R1_STOP:
        result = _r1_seam_result(snapshot, R1_TAG_STOP)
        if result == "OK":
            # StopRecording answering OK is a COMMANDED reading. Do not take it
            # as proof: go and OBSERVE the recorder state.
            return (_r1_enter(replace(state, stop_result="OK", state_probe=0),
                              R1_RECORDER_IDLE, snapshot.ut),
                    [_r1_state_action(0)])
        if result in ("ERROR", "TIMEOUT"):
            return _r1_flake(
                replace(state, stop_result=result),
                "phase %s: the StopRecording seam command returned %s (%s); the "
                "recorder cannot be confirmed stopped, and InvokeRewind is "
                "refused `recording-active` while one is live"
                % (R1_STOP, result,
                   _r1_because(_r1_reject_reason(snapshot, R1_TAG_STOP)))), []
        if state.phase_frames > state.params.stop_frames:
            return _r1_flake(
                state,
                "phase %s: the StopRecording seam command never answered within "
                "%d frames" % (R1_STOP, state.params.stop_frames)), []
        return state, []

    if state.phase == R1_RECORDER_IDLE:
        # THE DISPATCHER GATE, CARRIED AS AN OBSERVED PRECONDITION. Never command
        # InvokeRewind until a RecordingState reply has actually READ
        # `recording=false`. Ordering alone is an assumption; this is a reading.
        tag = r1_state_probe_tag(state.state_probe)
        result = _r1_seam_result(snapshot, tag)
        if result == "OK":
            reading = _r1_seam_payload(snapshot, tag, "recording")
            if reading == "false":
                st = replace(state, recorder_idle_reading=reading,
                             recorder_idle_observed=True,
                             # STAMP THE PRE-REWIND OBSERVATION on the same frame
                             # the rewind is commanded, so VERIFY compares against
                             # the tightest possible "before".
                             pre_rewind_ut=snapshot.ut,
                             pre_rewind_altitude=snapshot.altitude,
                             pre_rewind_situation=snapshot.situation or "",
                             pre_rewind_body=snapshot.body or "")
                return (_r1_enter(st, R1_REWIND, snapshot.ut),
                        [_r1_rewind_action(state.params)])
            if reading == "true":
                # Still recording. Probe again under the frame bound, with a FRESH
                # tag (a reused tag is a reused wire id the seam would skip).
                st = replace(state, recorder_idle_reading=reading,
                             state_probe=state.state_probe + 1)
                if st.phase_frames > st.params.idle_frames:
                    return _r1_flake(
                        st,
                        "phase %s: StopRecording reported OK but RecordingState "
                        "still read recording=true after %d frames (%d probe(s)); "
                        "InvokeRewind would be REJECTED `recording-active`. The "
                        "commit-then-keep-flying promotion re-arms a recorder on "
                        "the surviving stage, so something re-started one after "
                        "the stop"
                        % (R1_RECORDER_IDLE, st.params.idle_frames,
                           st.state_probe)), []
                return st, [_r1_state_action(st.state_probe)]
            # OK with no readable `recording` field: FAIL CLOSED. An unreadable
            # recorder state is not permission to command an irreversible rewind.
            return _r1_flake(
                replace(state, recorder_idle_reading=reading),
                "phase %s: the RecordingState reply carried no readable "
                "`recording` field (read %r), so the recorder cannot be confirmed "
                "idle; refusing to command InvokeRewind on an unverified gate"
                % (R1_RECORDER_IDLE, reading)), []
        if result in ("ERROR", "TIMEOUT"):
            return _r1_flake(
                state,
                "phase %s: the RecordingState seam command returned %s (%s); the "
                "recorder-idle precondition could not be observed"
                % (R1_RECORDER_IDLE, result,
                   _r1_because(_r1_reject_reason(snapshot, tag)))), []
        if state.phase_frames > state.params.idle_frames:
            return _r1_flake(
                state,
                "phase %s: the RecordingState seam command never answered within "
                "%d frames" % (R1_RECORDER_IDLE, state.params.idle_frames)), []
        return state, []

    if state.phase == R1_REWIND:
        result = _r1_seam_result(snapshot, R1_TAG_REWIND)
        if result == "OK":
            return _r1_enter(replace(state, rewind_result="OK"),
                             R1_VERIFY, snapshot.ut), []
        if result in ("ERROR", "TIMEOUT"):
            # Surface PARSEK's OWN reason verbatim. The previous wording guessed
            # ("the re-fly never started, or its post-load marker never landed")
            # and on flight 1 that sent the operator hunting the RewindPoint while
            # the real answer, `recording-active`, was sitting in the response.
            reason = _r1_reject_reason(snapshot, R1_TAG_REWIND)
            return _r1_flake(
                replace(state, rewind_result=result, rewind_reject_reason=reason),
                "phase %s: the InvokeRewind seam command returned %s for "
                "rp=%s slot=%d; %s"
                % (R1_REWIND, result, state.params.rewind_point_id,
                   state.params.rewind_slot, _r1_because(reason))), []
        if state.phase_frames > state.params.rewind_frames:
            return _r1_flake(
                state,
                "phase %s: the InvokeRewind seam command never answered within "
                "%d frames" % (R1_REWIND, state.params.rewind_frames)), []
        return state, []

    if state.phase == R1_VERIFY:
        # THE OBSERVED GATE. Not "did the verb return OK" (it already did, that
        # is how we got here) but "did the game clock actually run backward".
        regression = float("nan")
        if _is_finite(state.pre_rewind_ut) and _is_finite(snapshot.ut):
            regression = state.pre_rewind_ut - snapshot.ut
        if _is_finite(regression) and regression >= state.params.min_ut_regression:
            st = replace(state,
                         post_rewind_ut=snapshot.ut,
                         post_rewind_altitude=snapshot.altitude,
                         post_rewind_situation=snapshot.situation or "",
                         post_rewind_body=snapshot.body or "",
                         ut_regression=regression)
            # REWOUND is now a one-frame WAYPOINT, not the terminal: it commands
            # the second flight. Throttle up + activate the stage, exactly as a
            # player pressing space would (the same pair B1/B2's PRELAUNCH uses),
            # which is also what arms auto-record-on-launch again.
            return (_r1_enter(st, R1_REWOUND, snapshot.ut),
                    [Action(ACTION_SET_THROTTLE, state.params.relaunch_throttle),
                     Action(ACTION_ACTIVATE_STAGE)])
        if state.phase_frames > state.params.verify_frames:
            return _r1_flake(
                replace(state, ut_regression=regression),
                "phase %s: InvokeRewind reported OK but the OBSERVED game clock "
                "never ran backward within %d frames (preUT=%s postUT=%s "
                "regression=%s s, required >= %.1f s). A rewind that did not "
                "move the clock did not happen -- the seam's OK is a COMMANDED "
                "reading and is never on its own evidence that it did"
                % (R1_VERIFY, state.params.verify_frames,
                   _obs_fmt(state.pre_rewind_ut), _obs_fmt(snapshot.ut),
                   _obs_fmt(regression), state.params.min_ut_regression)), []
        return state, []

    if state.phase == R1_REWOUND:
        # One-frame waypoint: the relaunch was commanded on entry. Hand straight
        # over to RELAUNCH, which does the OBSERVING.
        return _r1_enter(state, R1_RELAUNCH, snapshot.ut), []

    if state.phase == R1_RELAUNCH:
        # OBSERVED: the rewound craft actually LEFT THE GROUND under power.
        # "We sent throttle + stage" is a commanded reading; altitude gained over
        # the post-rewind reading is not something a dead engine can produce.
        gain = float("nan")
        if _is_finite(state.post_rewind_altitude) and _is_finite(snapshot.altitude):
            gain = snapshot.altitude - state.post_rewind_altitude
        best = gain
        if _is_finite(state.relaunch_altitude_gain) and _is_finite(gain):
            best = max(state.relaunch_altitude_gain, gain)
        elif not _is_finite(best):
            best = state.relaunch_altitude_gain
        st = replace(state, relaunch_altitude_gain=best,
                     relaunch_situation=snapshot.situation or state.relaunch_situation)
        if _is_finite(best) and best >= state.params.relaunch_min_altitude_gain:
            return (_r1_enter(replace(st, loop_probe=0), R1_LOOP_POINTS, snapshot.ut),
                    [_r1_loop_action(0)])
        if st.phase_frames > st.params.relaunch_frames:
            return _r1_flake(
                st,
                "phase %s: the rewound craft never climbed %.0f m within %d "
                "frames (best gain=%s m, situation=%s). The rewind put the craft "
                "back but the second flight never happened, so the re-fly's "
                "provisional would carry no trajectory - which is exactly the "
                "empty-provisional condition this leg exists to avoid"
                % (R1_RELAUNCH, st.params.relaunch_min_altitude_gain,
                   st.params.relaunch_frames, _obs_fmt(best),
                   st.relaunch_situation or "?")), []
        return st, []

    if state.phase == R1_LOOP_POINTS:
        # OBSERVED: the second flight was actually RECORDED. Flying is not the
        # same as being recorded, and it is the RECORDING that has to be
        # non-empty for the merge to write supersede rows.
        tag = r1_loop_probe_tag(state.loop_probe)
        result = _r1_seam_result(snapshot, tag)
        if result == "OK":
            raw = _r1_seam_payload(snapshot, tag, "points")
            points = _r1_parse_int(raw)
            if points is None:
                return _r1_flake(
                    state,
                    "phase %s: the RecordingState reply carried no readable "
                    "`points` field (read %r), so no post-rewind recording can be "
                    "confirmed non-empty"
                    % (R1_LOOP_POINTS, raw)), []
            st = replace(state, loop_points_read=points,
                         # The reply's OWN tree id. NOT compared to the marker's
                         # provisional (no seam verb exposes that), but carried so
                         # a divergence lands in the result JSON. Flight 3 read
                         # tree=820de77e here while the provisional sat empty in
                         # tree-b9-stack-root.
                         loop_recorded_tree=_r1_seam_payload(snapshot, tag, "tree"))
            if points > 0:
                return _r1_enter(st, R1_LOOP_CLOSED, snapshot.ut), []
            st = replace(st, loop_probe=st.loop_probe + 1)
            if st.phase_frames > st.params.loop_points_frames:
                return _r1_flake(
                    st,
                    "phase %s: the craft flew after the rewind but RecordingState "
                    "still read points=0 after %d frames (%d probe(s)), so NO "
                    "recording received the second flight. NOTE this gate reads "
                    "the LIVE recorder's buffered count, not the re-fly "
                    "provisional's: a non-zero read here proves only that SOME "
                    "recording got the flight, and flight 3 (2026-07-26) showed "
                    "those can differ. An empty provisional is what makes the "
                    "merge refuse supersede rows "
                    "(SupersedeCommit.ValidateSupersedeTarget: `empty Points`)"
                    % (R1_LOOP_POINTS, st.params.loop_points_frames,
                       st.loop_probe)), []
            return st, [_r1_loop_action(st.loop_probe)]
        if result in ("ERROR", "TIMEOUT"):
            return _r1_flake(
                state,
                "phase %s: the RecordingState seam command returned %s (%s); the "
                "post-rewind point count could not be observed"
                % (R1_LOOP_POINTS, result,
                   _r1_because(_r1_reject_reason(snapshot, tag)))), []
        if state.phase_frames > state.params.loop_points_frames:
            return _r1_flake(
                state,
                "phase %s: the RecordingState seam command never answered within "
                "%d frames" % (R1_LOOP_POINTS, state.params.loop_points_frames)), []
        return state, []

    return _r1_flake(state, "phase %s: unreachable machine phase" % state.phase), []


def evaluate_r1_assertions(frames, params: R1Params,
                           state: Optional[R1State] = None) -> List["AssertionOutcome"]:
    """R1's assertion rows. Every row is machine-CARRIED evidence sampled on the
    frame that produced it, so ``frames`` is unused (the B5/B6 pattern).

    Two of the five rows are OBSERVATIONS the rewind cannot fake
    (``clockRewound`` / ``vesselStateChanged``); ``rewindSeamAccepted`` is the
    COMMANDED corroboration and is deliberately listed LAST and never alone --
    ``all_assertions_met`` requires every row, so the commanded row can only ever
    make the mission stricter, never substitute for the observed ones."""
    st = state
    phases = tuple(getattr(st, "phases_reached", ()) or ())
    ascent = getattr(st, "ascent", None)
    ascent_phases = tuple(getattr(ascent, "phases_reached", ()) or ())

    orbit_met = B2_ORBIT in ascent_phases
    orbit = AssertionOutcome(
        "reachedOrbitBeforeRewind", orbit_met,
        (B2_ORBIT if orbit_met else (ascent_phases[-1] if ascent_phases else None)),
        # OBSERVED: the nested B2 machine only reaches B2_ORBIT by reading real
        # apoapsis / periapsis telemetry, never by a commanded ack.
        {"required": B2_ORBIT, "leg": "delegated-b2", "channel": "observed"})

    commit_result = str(getattr(st, "commit_result", "") or "")
    commit_met = commit_result == "OK" and R1_STOP in phases
    committed = AssertionOutcome(
        "treeCommittedBeforeRewind", commit_met, (commit_result or None),
        # COMMANDED: this is the CommitTree verb's own OK. Labelled honestly
        # rather than dressed as evidence; the observed rows carry the weight.
        {"required": R1_STOP, "seamVerb": "CommitTree", "channel": "commanded"})

    # The dispatcher's `recording-active` gate, as an assertion row. OBSERVED: the
    # value is the `recording` field READ off a RecordingState reply, not the
    # StopRecording verb's own OK (which is carried separately in `detail` so a
    # reader can see both). Flight 1 (2026-07-26) red exactly here.
    idle_reading = str(getattr(st, "recorder_idle_reading", "") or "")
    idle_met = (bool(getattr(st, "recorder_idle_observed", False))
                and idle_reading == "false"
                and R1_REWIND in phases)
    recorder_idle = AssertionOutcome(
        "recorderIdleBeforeRewind", idle_met, (idle_reading or None),
        {"required": R1_REWIND, "seamVerb": "RecordingState",
         "stopSeamResult": str(getattr(st, "stop_result", "") or "") or None,
         "probes": int(getattr(st, "state_probe", 0)) + 1,
         "channel": "observed"})

    regression = getattr(st, "ut_regression", float("nan"))
    rewound_met = (_is_finite(regression)
                   and regression >= params.min_ut_regression)
    rewound = AssertionOutcome(
        "clockRewound", rewound_met, regression,
        {"required": R1_REWOUND,
         "minRegressionSeconds": params.min_ut_regression,
         "preUt": _json_safe(getattr(st, "pre_rewind_ut", float("nan"))),
         "postUt": _json_safe(getattr(st, "post_rewind_ut", float("nan"))),
         "channel": "observed"})

    pre_alt = getattr(st, "pre_rewind_altitude", float("nan"))
    post_alt = getattr(st, "post_rewind_altitude", float("nan"))
    pre_sit = str(getattr(st, "pre_rewind_situation", "") or "")
    post_sit = str(getattr(st, "post_rewind_situation", "") or "")
    alt_moved = (_is_finite(pre_alt) and _is_finite(post_alt)
                 and abs(pre_alt - post_alt) >= params.min_altitude_change)
    sit_moved = bool(pre_sit) and bool(post_sit) and pre_sit != post_sit
    state_met = R1_REWOUND in phases and (alt_moved or sit_moved)
    changed = AssertionOutcome(
        "vesselStateChanged", state_met,
        (post_sit or None),
        {"required": R1_REWOUND,
         "preAltitude": _json_safe(pre_alt), "postAltitude": _json_safe(post_alt),
         "preSituation": pre_sit or None, "postSituation": post_sit or None,
         "minAltitudeChangeMeters": params.min_altitude_change,
         "channel": "observed"})

    # ---- THE LOOP HALF. A rewind that is never re-flown is not a loop, and it
    # leaves the re-fly provisional empty (flight 2, 2026-07-26). Both rows are
    # OBSERVED: one that the craft physically climbed after the rewind, one that
    # the climb was actually RECORDED.
    gain = getattr(st, "relaunch_altitude_gain", float("nan"))
    flew_met = (_is_finite(gain)
                and gain >= params.relaunch_min_altitude_gain
                and R1_LOOP_POINTS in phases)
    reflew = AssertionOutcome(
        "postRewindFlightObserved", flew_met, gain,
        {"required": R1_LOOP_POINTS,
         "minAltitudeGainMeters": params.relaunch_min_altitude_gain,
         "situation": str(getattr(st, "relaunch_situation", "") or "") or None,
         "channel": "observed"})

    # HONEST NAME + HONEST SCOPE. This row does NOT observe that the re-fly's
    # PROVISIONAL received the flight - `RecordingState.points` is the LIVE
    # recorder's buffered count for whatever recording is live, and flight 3
    # (2026-07-26) read points=24 in tree 820de77e while the merge refused
    # `provisional=rec_5b0697a6... reason=empty Points`. It observes that the
    # post-rewind flight was recorded SOMEWHERE. `recordedTree` carries the tree
    # the reply named so that divergence is visible in the result JSON; the
    # comparison itself is impossible through the seam today (no verb exposes the
    # marker's provisional id), so it is evidence for a human, not a gate.
    points_read = int(getattr(st, "loop_points_read", -1))
    points_met = points_read > 0 and R1_LOOP_CLOSED in phases
    recorded = AssertionOutcome(
        "postRewindFlightRecordedSomewhere", points_met, points_read,
        {"required": R1_LOOP_CLOSED, "seamVerb": "RecordingState",
         "probes": int(getattr(st, "loop_probe", 0)) + 1,
         "recordedTree": str(getattr(st, "loop_recorded_tree", "") or "") or None,
         # -1 is the UNREAD sentinel, kept raw: the sentinel IS the diagnosis
         # when a run never managed to read the count.
         "channel": "observed",
         "doesNotProve": "that the re-fly provisional received these points"})

    rewind_result = str(getattr(st, "rewind_result", "") or "")
    seam_met = rewind_result == "OK"
    seam = AssertionOutcome(
        "rewindSeamAccepted", seam_met, (rewind_result or None),
        {"required": R1_VERIFY, "seamVerb": "InvokeRewind",
         "rewindPointId": params.rewind_point_id or None,
         "rewindSlot": params.rewind_slot,
         # Parsek's OWN refusal reason when it declined, so the result JSON names
         # the cause instead of leaving it to the log.
         "rejectReason": str(getattr(st, "rewind_reject_reason", "") or "") or None,
         "channel": "commanded"})

    return [orbit, committed, recorder_idle, rewound, changed, reflew, recorded, seam]


# ---------------------------------------------------------------------------
# V1 map-dwell machine (mission v1_map_dwell; design-testing-unified section 6,
# V1). Pure. Aims the SHIPPED render parity oracle (MapRenderProbe +
# RenderParityOracle, gated live by S1.6/S1.7 over synthetic one-frame
# fixtures) at REAL flown-mission geometry ACROSS TIME.
#
# WHY THE REWIND LEG EXISTS (read before "simplifying" it away). A committed
# tree in normal forward play draws NO map ghosts: PlaybackScopeTracker (BUG-B,
# "historical-not-replayed") keeps every committed recording dormant unless the
# live playhead was observed at-or-before its activation start, exactly so a
# forward playthrough never draws a duplicate ghost of a still-live vessel.
# MEASURED, not assumed: BDOCK-1 committed mid-mission with the tracer on and
# logged 674 post-commit `probe frame summary` lines, EVERY one ghosts=0.
# A post-commit "map dwell" without the rewind is therefore structurally
# vacuous -- the exact 552-frames-all-ghosts=0 green that S1.4 once passed on.
# The ONLY unattended route that puts REAL flown recordings into replay scope
# is the R1-proven rewind cycle: InvokeRewind lands the playhead BEFORE the
# flown launch, the flown tree re-enters the forward path, and warping forward
# REPLAYS the real recorded geometry as ghosts in front of the per-frame probe.
#
# THE MACHINE: FLIGHT delegates every frame to the live-proven B11 profile
# (``b5_decide`` with captureEnabled -- ascent, transfer, Kerbin->Mun SOI
# crossing, capture, park, mid-mission CommitTree). On its clean
# ORBIT-COMMITTED terminal the R1-proven rewind tail runs (STOP ->
# RECORDER-IDLE -> REWIND -> VERIFY, all four lifted from ``r1_decide``
# verbatim in semantics), then the NEW dwell tail:
#
#   DWELL-CAMERA     stage a deterministic map camera (mode=Map, focus body,
#                    pitch/heading/distance), OBSERVED via camera_mode readback
#   DWELL-WARP-IN    native warp to flight_start_ut + lead (just past the
#                    flown launch, where the replaying ghost is in early
#                    ascent)
#   DWELL-HOLD-1X    hold 1x for dwellHoldSeconds: per-frame probe sampling at
#                    the densest cadence over the replayed ascent
#   DWELL-WARP-RAMP  a rails-factor stair up and back down (the high-warp
#                    reseed/icon behavior no scenario has ever gated), with an
#                    early-exit guard so the ramp can never overshoot the
#                    recorded SOI crossing
#   DWELL-SOI-WARP   native warp to soi_entry_ut - lead
#   DWELL-SOI-CROSS  held moderate rails across the RECORDED Kerbin->Mun SOI
#                    boundary UT (the bodyChanged / re-frame moment)
#   DWELL-DONE       cancel warp; terminal (verdict None -- assertions judge)
#
# WHAT THE DWELL PHASES ASSERT AND WHAT THEY DO NOT. The dwell drives the
# STAGE; the MEASURING is MapRenderProbe's (per-frame, C#-side, gated by the
# scenario's logContract pins on the probe's own summary lines). Ramp steps
# are COMMANDED rails factors -- the machine never gates on an achieved rate
# (the server may clamp; a clamped step still advances the stair), because a
# warp-rate assertion here would re-derive KSP's own altitude/landed rules.
# The UT milestones (warp-in target reached, SOI boundary UT crossed) are
# OBSERVED off the live clock and are what make an empty dwell impossible to
# green at the mission layer.
#
# THE VESSEL NEVER FLIES AGAIN, ON PURPOSE (contrast R1's RELAUNCH). The
# rewound craft sits PRE_LAUNCH on the pad (landed => every rails factor is
# legal) while the GHOSTS do the flying. The re-fly provisional therefore
# stays EMPTY, which is exactly why the scenario's post-mission step must be
# AnswerMergeDialog choice=DISCARD, never merge: merging an empty provisional
# is the known R1-EMPTY-PROVISIONAL defect ([Parsek][ERROR] AppendRelations
# invariant violation ... reason=empty Points) and would red the forbidden-
# ERROR contract on every flight.
# ---------------------------------------------------------------------------

V1_FLIGHT = "FLIGHT"
V1_STOP = "STOP"
V1_RECORDER_IDLE = "RECORDER-IDLE"
V1_REWIND = "REWIND"
V1_VERIFY = "VERIFY"
V1_CAMERA = "DWELL-CAMERA"
V1_WARP_IN = "DWELL-WARP-IN"
V1_HOLD = "DWELL-HOLD-1X"
V1_RAMP = "DWELL-WARP-RAMP"
V1_SOI_WARP = "DWELL-SOI-WARP"
V1_SOI_CROSS = "DWELL-SOI-CROSS"
V1_DONE = "DWELL-DONE"
V1_PHASES: Tuple[str, ...] = (V1_FLIGHT, V1_STOP, V1_RECORDER_IDLE, V1_REWIND,
                              V1_VERIFY, V1_CAMERA, V1_WARP_IN, V1_HOLD,
                              V1_RAMP, V1_SOI_WARP, V1_SOI_CROSS, V1_DONE)

# Per-command tags (the R1 discipline: distinct by construction so wire ids
# never collide -- the C# seam SKIPS duplicate ids). The RECORDER-IDLE probes
# reuse ``r1_state_probe_tag`` ("state0", "state1", ...), which is already a
# per-probe family.
V1_TAG_STOP = "stop"
V1_TAG_REWIND = "rewind"

# How the DWELL-WARP-RAMP ended; carried evidence for the warpRampDriven row.
# "no-factors-configured" is the honest reading for a spec that legally sets
# dwellRampFactors = [] (the stair is skipped entirely, V1_RAMP is never
# entered, and the row must not claim a stair "completed" that never ran).
V1_RAMP_ENDED_COMPLETED = "completed"
V1_RAMP_ENDED_SOI_GUARD = "soi-guard"
V1_RAMP_ENDED_EMPTY = "no-factors-configured"

# The replay-scope tolerance for the rewoundBeforeFlightStart row, matching
# PlaybackScopeTracker.ActivationToleranceSeconds (the C# latch enters a
# committed recording into replay scope when the playhead is observed at or
# before activationStart + 2.0 s). The row compares the post-rewind UT against
# the mission's FIRST-frame UT, which is a LOWER bound on the flown recording's
# activation start (the recording starts at launch, seconds later), so
# post <= firstFrame + tolerance implies post <= activationStart + tolerance.
# THE FLIGHT-1 LESSON (2026-07-30, run 1917): a strict `post <= firstFrame`
# failed the whole otherwise-green mission on a 0.22 s gap -- the rp_b9_root
# quicksave is authored from the SAME pre-launch pad state the mission's first
# frame reads, so the two UTs are equal to within save/load jitter and the
# strict form asserted against the wrong instant.
V1_REPLAY_SCOPE_TOLERANCE_SECONDS = 2.0


@dataclass(frozen=True)
class V1MapDwellParams:
    """V1 map-dwell tuning. The flight half is the delegated B11 machine's own
    params VERBATIM (``b5`` is built by ``b5_params_from_dict`` over the same
    missionParams dict -- no new flight tuning surface); the rewind half mirrors
    R1Params; everything else stages the dwell. Every value is a lead / hold /
    budget, never a golden trajectory."""
    b5: B5Params
    # The InvokeRewind target (R1 discipline: "" / -1 = UNREAD -> frame-1
    # fail-closed give-up, never a flown mission whose dwell was unreachable).
    rewind_point_id: str = ""
    rewind_slot: int = -1
    # FRAME budgets for the seam-driven phases (game-time budgets cannot bound
    # a phase whose clock runs backward; the seam bridge blocks inside
    # perform() so a terminal token normally lands on the very next frame).
    stop_frames: int = 40
    idle_frames: int = 40
    rewind_frames: int = 40
    verify_frames: int = 40
    # THE OBSERVED rewind gate (R1 verbatim).
    min_ut_regression: float = 1.0
    # --- Camera staging ---
    camera_focus_body: str = "Kerbin"
    camera_pitch_deg: float = 45.0
    camera_heading_deg: float = 0.0
    camera_distance_m: float = 40000000.0
    # Frames allowed for the OBSERVED camera_mode readback to land on "Map".
    camera_frames: int = 40
    # --- Dwell staging ---
    # Warp-in target = flight_start_ut + this lead: just past the flown launch,
    # so the hold watches the replaying ghost's early ascent (atmospheric legs,
    # the polyline surface) rather than an empty pre-launch pad.
    dwell_start_lead: float = 60.0
    warp_in_frames: int = 600
    # 1x hold: the densest per-frame probe sampling window.
    dwell_hold_seconds: float = 45.0
    hold_frames: int = 600
    # The rails stair, up and back down. COMMANDED factor indices; the server
    # clamps to legality (the craft is LANDED, so every factor is legal at
    # stock rules). Each step is held ramp_step_frames poll frames.
    ramp_factors: Tuple[int, ...] = (2, 3, 4, 5, 4, 3, 2)
    ramp_step_frames: int = 10
    ramp_frames: int = 600
    # --- The recorded-SOI-crossing leg ---
    # Native-warp target = soi_entry_ut - this lead; the crossing is then taken
    # at held soi_cross_factor rails until soi_entry_ut + trail.
    soi_dwell_lead: float = 300.0
    soi_warp_frames: int = 1200
    soi_cross_factor: int = 2
    soi_dwell_trail: float = 300.0
    soi_cross_frames: int = 1200


def v1_map_dwell_params_from_dict(params: Dict) -> V1MapDwellParams:
    """Build ``V1MapDwellParams`` from a spec ``missionParams`` dict. The flight
    block is parsed by ``b5_params_from_dict`` over the SAME dict, so the B11
    keys keep their exact spellings and defaults (captureEnabled included)."""
    params = params or {}
    raw_factors = params.get("dwellRampFactors", None)
    if raw_factors is None:
        factors: Tuple[int, ...] = V1MapDwellParams.ramp_factors
    else:
        factors = tuple(int(v) for v in raw_factors)
    return V1MapDwellParams(
        b5=b5_params_from_dict(params),
        rewind_point_id=str(params.get("rewindPointId", "") or ""),
        rewind_slot=int(params.get("rewindSlot", -1)),
        stop_frames=int(params.get("stopFrames", 40)),
        idle_frames=int(params.get("idleFrames", 40)),
        rewind_frames=int(params.get("rewindFrames", 40)),
        verify_frames=int(params.get("verifyFrames", 40)),
        min_ut_regression=float(params.get("minUtRegressionSeconds", 1.0)),
        camera_focus_body=str(params.get("cameraFocusBody", "Kerbin")),
        camera_pitch_deg=float(params.get("cameraPitchDeg", 45.0)),
        camera_heading_deg=float(params.get("cameraHeadingDeg", 0.0)),
        camera_distance_m=float(params.get("cameraDistanceMeters", 40000000.0)),
        camera_frames=int(params.get("cameraFrames", 40)),
        dwell_start_lead=float(params.get("dwellStartLeadSeconds", 60.0)),
        warp_in_frames=int(params.get("dwellWarpInFrames", 600)),
        dwell_hold_seconds=float(params.get("dwellHoldSeconds", 45.0)),
        hold_frames=int(params.get("dwellHoldFrames", 600)),
        ramp_factors=factors,
        ramp_step_frames=int(params.get("dwellRampStepFrames", 10)),
        ramp_frames=int(params.get("dwellRampFrames", 600)),
        soi_dwell_lead=float(params.get("soiDwellLeadSeconds", 300.0)),
        soi_warp_frames=int(params.get("soiDwellWarpFrames", 1200)),
        soi_cross_factor=int(params.get("soiDwellCrossFactor", 2)),
        soi_dwell_trail=float(params.get("soiDwellTrailSeconds", 300.0)),
        soi_cross_frames=int(params.get("soiDwellCrossFrames", 1200)),
    )


@dataclass(frozen=True)
class V1MapDwellState:
    """V1 map-dwell machine state. ``flight`` is the nested, delegated B11
    machine (``B5State`` with captureEnabled params); ``done`` fires at
    V1_DONE (verdict None -- the assertions judge the carried evidence) or on
    a named give-up."""
    params: V1MapDwellParams
    flight: "B5State"
    phase: str = V1_FLIGHT
    phase_entry_ut: float = 0.0
    phase_frames: int = 0
    phases_reached: Tuple[str, ...] = (V1_FLIGHT,)
    # --- flown-geometry UT stamps (OBSERVED during the flight leg; the dwell
    # steers by these, never by ghost telemetry it cannot read) ---
    # The first mission frame's UT: the floor the rewind must land below for
    # the flown tree to re-enter replay scope.
    flight_start_ut: float = float("nan")
    # The first frame the ACTIVE vessel's SOI body read the transfer target:
    # the recorded Kerbin->Mun crossing UT the dwell re-visits.
    soi_entry_ut: float = float("nan")
    # --- rewind-cycle evidence (R1 verbatim) ---
    stop_result: str = ""
    rewind_result: str = ""
    rewind_reject_reason: str = ""
    state_probe: int = 0
    recorder_idle_reading: str = ""
    recorder_idle_observed: bool = False
    pre_rewind_ut: float = float("nan")
    pre_rewind_altitude: float = float("nan")
    pre_rewind_situation: str = ""
    post_rewind_ut: float = float("nan")
    post_rewind_altitude: float = float("nan")
    post_rewind_situation: str = ""
    ut_regression: float = float("nan")
    # --- dwell evidence ---
    # Latched TRUE only on an OBSERVED camera_mode == "Map" readback.
    camera_map_observed: bool = False
    # Game seconds actually spent in DWELL-HOLD-1X (exit ut - entry ut).
    hold_elapsed: float = float("nan")
    # Ramp progress: NEXT stair index, and how the ramp ended ("" = never ran).
    ramp_index: int = 0
    ramp_step_frame: int = 0
    ramp_ended_reason: str = ""
    # UT stamped on DWELL-SOI-CROSS exit: >= soi_entry_ut + trail proves the
    # dwell's live clock actually crossed the recorded boundary UT while the
    # probe sampled.
    soi_cross_exit_ut: float = float("nan")
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    flake_reason: Optional[str] = None
    done: bool = False
    loss_reason: Optional[str] = None


def v1_map_dwell_initial_state(params: V1MapDwellParams) -> V1MapDwellState:
    """Fresh V1 machine at FLIGHT, carrying a fresh nested B11 machine."""
    return V1MapDwellState(params=params, flight=b5_initial_state(params.b5))


def _v1_enter(state: V1MapDwellState, new_phase: str, ut: float) -> V1MapDwellState:
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        phase_frames=0,
        phases_reached=state.phases_reached + (new_phase,),
        done=(new_phase == V1_DONE),
    )


def _v1_flake(state: V1MapDwellState, reason: str) -> V1MapDwellState:
    """Terminate with MISSION-FLAKE and a DISTINCTLY NAMED reason (the R1
    discipline: every give-up names which of the ways the cycle can stall
    actually fired)."""
    return replace(state, verdict=MISSION_FLAKE, flake_phase=state.phase,
                   flake_reason=reason, done=True)


def _v1_stop_action() -> Action:
    # Idempotent-OK whether or not the post-commit promotion re-armed a
    # recorder (see _r1_stop_action).
    return Action(ACTION_PARSEK_SEAM_COMMAND, seam_verb="StopRecording",
                  seam_args=(), seam_tag=V1_TAG_STOP)


def _v1_state_action(probe: int) -> Action:
    return Action(ACTION_PARSEK_SEAM_COMMAND, seam_verb="RecordingState",
                  seam_args=(), seam_tag=r1_state_probe_tag(probe))


def _v1_rewind_action(params: V1MapDwellParams) -> Action:
    return Action(
        ACTION_PARSEK_SEAM_COMMAND, seam_verb="InvokeRewind",
        seam_args=(("rp", str(params.rewind_point_id)),
                   ("slot", str(int(params.rewind_slot)))),
        seam_tag=V1_TAG_REWIND)


def _v1_camera_actions(params: V1MapDwellParams) -> List[Action]:
    return [
        Action(ACTION_CAMERA_SET_MAP),
        Action(ACTION_CAMERA_FOCUS_BODY, text=params.camera_focus_body),
        Action(ACTION_CAMERA_SET_POSE,
               camera_pose=(params.camera_pitch_deg, params.camera_heading_deg,
                            params.camera_distance_m)),
    ]


def _v1_soi_target(state: V1MapDwellState) -> float:
    """The native-warp target for the SOI leg: soi_entry_ut - lead. NaN when
    the crossing UT was never observed (fail closed: no target to warp to)."""
    if not _is_finite(state.soi_entry_ut):
        return float("nan")
    return state.soi_entry_ut - state.params.soi_dwell_lead


def v1_map_dwell_decide(state: V1MapDwellState,
                        snapshot: TelemetrySnapshot
                        ) -> Tuple[V1MapDwellState, List[Action]]:
    """Advance the V1 map-dwell machine one frame; return (new_state, actions).

    FLIGHT delegates every frame to ``b5_decide`` over the nested state (and
    stamps flight_start_ut / soi_entry_ut off the live snapshot as it goes).
    The rewind tail advances ONLY on its own tagged terminal tokens / the
    OBSERVED backward clock (R1 verbatim). The dwell tail advances on OBSERVED
    UT milestones and frame-held stair steps; every give-up is distinctly
    named."""
    if state.done:
        return state, []

    # PRE-FLIGHT FAIL-CLOSED (R1 verbatim): a mission whose rewind target
    # cannot resolve must say so on frame 1, not after a ~20-minute flight.
    if state.phase == V1_FLIGHT and state.phase_frames == 0:
        if not state.params.rewind_point_id or state.params.rewind_slot < 0:
            return _v1_flake(
                state,
                "phase %s: rewind target unresolved before launch "
                "(rewindPointId=%r rewindSlot=%d); InvokeRewind matches the "
                "RewindPoint id EXACTLY, so an unset target can only ever be "
                "REJECTED unknown-rp / unknown-slot"
                % (V1_FLIGHT, state.params.rewind_point_id,
                   state.params.rewind_slot)), []

    state = replace(state, phase_frames=state.phase_frames + 1)

    # OBSERVED flown-geometry stamps, taken while the flight leg lives. Both
    # are stamp-once latches.
    if state.phase == V1_FLIGHT:
        if not _is_finite(state.flight_start_ut) and _is_finite(snapshot.ut):
            state = replace(state, flight_start_ut=snapshot.ut)
        if (not _is_finite(state.soi_entry_ut)
                and snapshot.body == state.params.b5.target_body
                and _is_finite(snapshot.ut)):
            state = replace(state, soi_entry_ut=snapshot.ut)

    # Runner-signaled vessel loss. SUPPRESSED in REWIND only (the scene reload
    # legitimately destroys the active vessel there; the R1 rationale).
    if snapshot.vessel_lost and state.phase != V1_REWIND:
        return replace(
            state, done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason=("vessel-lost (unreadable after repeated telemetry "
                         "failures) in phase %s" % state.phase)), []

    if state.phase == V1_FLIGHT:
        flight, actions = b5_decide(state.flight, snapshot)
        st = replace(state, flight=flight)
        if not flight.done:
            return st, actions
        # The nested machine terminated. A loss terminal is a deterministic
        # failure; a flake is a flake; only a clean ORBIT-COMMITTED opens the
        # rewind cycle.
        if flight.loss_reason:
            return replace(st, done=True, verdict=MISSION_ASSERT_FAIL,
                           loss_reason=("flight leg: %s" % flight.loss_reason)), actions
        if flight.verdict is not None:
            return _v1_flake(
                st,
                "phase %s: the delegated B11 flight leg terminated %s in its "
                "phase %s (the dwell was never reached)"
                % (V1_FLIGHT, flight.verdict,
                   flight.flake_phase or flight.phase)), actions
        if B5_ORBIT_COMMITTED not in flight.phases_reached:
            return _v1_flake(
                st,
                "phase %s: the delegated B11 flight leg reported done with a "
                "clean verdict but never reached %s (phases=%s)"
                % (V1_FLIGHT, B5_ORBIT_COMMITTED,
                   list(flight.phases_reached))), actions
        if not _is_finite(st.soi_entry_ut):
            # Fail CLOSED here rather than discover it after the rewind: with
            # no observed crossing UT the SOI leg has no target, and a dwell
            # that silently skipped its headline moment must not read green.
            return _v1_flake(
                st,
                "phase %s: the flight leg completed but the %s SOI-entry UT "
                "was never observed off the live body channel, so the dwell "
                "has no recorded crossing to re-visit"
                % (V1_FLIGHT, st.params.b5.target_body)), actions
        return (_v1_enter(st, V1_STOP, snapshot.ut),
                list(actions) + [_v1_stop_action()])

    if state.phase == V1_STOP:
        result = _r1_seam_result(snapshot, V1_TAG_STOP)
        if result == "OK":
            return (_v1_enter(replace(state, stop_result="OK", state_probe=0),
                              V1_RECORDER_IDLE, snapshot.ut),
                    [_v1_state_action(0)])
        if result in ("ERROR", "TIMEOUT"):
            return _v1_flake(
                replace(state, stop_result=result),
                "phase %s: the StopRecording seam command returned %s (%s); "
                "the recorder cannot be confirmed stopped, and InvokeRewind "
                "is refused `recording-active` while one is live"
                % (V1_STOP, result,
                   _r1_because(_r1_reject_reason(snapshot, V1_TAG_STOP)))), []
        if state.phase_frames > state.params.stop_frames:
            return _v1_flake(
                state,
                "phase %s: the StopRecording seam command never answered "
                "within %d frames" % (V1_STOP, state.params.stop_frames)), []
        return state, []

    if state.phase == V1_RECORDER_IDLE:
        # The dispatcher's `recording-active` gate carried as an OBSERVED
        # precondition (R1 flight-1 lesson, verbatim semantics).
        tag = r1_state_probe_tag(state.state_probe)
        result = _r1_seam_result(snapshot, tag)
        if result == "OK":
            reading = _r1_seam_payload(snapshot, tag, "recording")
            if reading == "false":
                st = replace(state, recorder_idle_reading=reading,
                             recorder_idle_observed=True,
                             pre_rewind_ut=snapshot.ut,
                             pre_rewind_altitude=snapshot.altitude,
                             pre_rewind_situation=snapshot.situation or "")
                return (_v1_enter(st, V1_REWIND, snapshot.ut),
                        [_v1_rewind_action(state.params)])
            if reading == "true":
                st = replace(state, recorder_idle_reading=reading,
                             state_probe=state.state_probe + 1)
                if st.phase_frames > st.params.idle_frames:
                    return _v1_flake(
                        st,
                        "phase %s: StopRecording reported OK but "
                        "RecordingState still read recording=true after %d "
                        "frames (%d probe(s)); InvokeRewind would be REJECTED "
                        "`recording-active`"
                        % (V1_RECORDER_IDLE, st.params.idle_frames,
                           st.state_probe)), []
                return st, [_v1_state_action(st.state_probe)]
            return _v1_flake(
                replace(state, recorder_idle_reading=reading),
                "phase %s: the RecordingState reply carried no readable "
                "`recording` field (read %r); refusing to command "
                "InvokeRewind on an unverified gate"
                % (V1_RECORDER_IDLE, reading)), []
        if result in ("ERROR", "TIMEOUT"):
            return _v1_flake(
                state,
                "phase %s: the RecordingState seam command returned %s (%s); "
                "the recorder-idle precondition could not be observed"
                % (V1_RECORDER_IDLE, result,
                   _r1_because(_r1_reject_reason(snapshot, tag)))), []
        if state.phase_frames > state.params.idle_frames:
            return _v1_flake(
                state,
                "phase %s: the RecordingState seam command never answered "
                "within %d frames"
                % (V1_RECORDER_IDLE, state.params.idle_frames)), []
        return state, []

    if state.phase == V1_REWIND:
        result = _r1_seam_result(snapshot, V1_TAG_REWIND)
        if result == "OK":
            return _v1_enter(replace(state, rewind_result="OK"),
                             V1_VERIFY, snapshot.ut), []
        if result in ("ERROR", "TIMEOUT"):
            reason = _r1_reject_reason(snapshot, V1_TAG_REWIND)
            return _v1_flake(
                replace(state, rewind_result=result,
                        rewind_reject_reason=reason),
                "phase %s: the InvokeRewind seam command returned %s for "
                "rp=%s slot=%d; %s"
                % (V1_REWIND, result, state.params.rewind_point_id,
                   state.params.rewind_slot, _r1_because(reason))), []
        if state.phase_frames > state.params.rewind_frames:
            return _v1_flake(
                state,
                "phase %s: the InvokeRewind seam command never answered "
                "within %d frames" % (V1_REWIND, state.params.rewind_frames)), []
        return state, []

    if state.phase == V1_VERIFY:
        # THE OBSERVED GATE (R1 verbatim): did the game clock actually run
        # backward.
        regression = float("nan")
        if _is_finite(state.pre_rewind_ut) and _is_finite(snapshot.ut):
            regression = state.pre_rewind_ut - snapshot.ut
        if _is_finite(regression) and regression >= state.params.min_ut_regression:
            st = replace(state,
                         post_rewind_ut=snapshot.ut,
                         post_rewind_altitude=snapshot.altitude,
                         post_rewind_situation=snapshot.situation or "",
                         ut_regression=regression)
            # Stage the camera on the transition frame; DWELL-CAMERA then
            # OBSERVES the mode readback before any warp is commanded.
            return (_v1_enter(st, V1_CAMERA, snapshot.ut),
                    _v1_camera_actions(state.params))
        if state.phase_frames > state.params.verify_frames:
            return _v1_flake(
                replace(state, ut_regression=regression),
                "phase %s: InvokeRewind reported OK but the OBSERVED game "
                "clock never ran backward within %d frames (preUT=%s "
                "postUT=%s regression=%s s, required >= %.1f s)"
                % (V1_VERIFY, state.params.verify_frames,
                   _obs_fmt(state.pre_rewind_ut), _obs_fmt(snapshot.ut),
                   _obs_fmt(regression), state.params.min_ut_regression)), []
        return state, []

    if state.phase == V1_CAMERA:
        if snapshot.camera_mode == CAMERA_MODE_MAP:
            st = replace(state, camera_map_observed=True)
            target = st.flight_start_ut + st.params.dwell_start_lead
            if _is_finite(snapshot.ut) and snapshot.ut >= target - 1.0:
                # Already at/past the warp-in target (a deep rewind is not
                # guaranteed): hold immediately.
                return _v1_enter(st, V1_HOLD, snapshot.ut), []
            return (_v1_enter(st, V1_WARP_IN, snapshot.ut),
                    [Action(ACTION_WARP_TO_UT, target)])
        if state.phase_frames > state.params.camera_frames:
            return _v1_flake(
                state,
                "phase %s: the map camera was COMMANDED but camera_mode "
                "never read %r within %d frames (last read %r). A dwell "
                "without an observed map camera cannot claim the map render "
                "surface it exists to exercise"
                % (V1_CAMERA, CAMERA_MODE_MAP, state.params.camera_frames,
                   snapshot.camera_mode)), []
        return state, []

    if state.phase == V1_WARP_IN:
        target = state.flight_start_ut + state.params.dwell_start_lead
        if _is_finite(snapshot.ut) and snapshot.ut >= target - 1.0:
            return (_v1_enter(state, V1_HOLD, snapshot.ut),
                    [Action(ACTION_CANCEL_WARP)])
        if state.phase_frames > state.params.warp_in_frames:
            return _v1_flake(
                state,
                "phase %s: the native warp toward flight_start+lead "
                "(target UT %s) never arrived within %d frames (ut=%s)"
                % (V1_WARP_IN, _obs_fmt(target), state.params.warp_in_frames,
                   _obs_fmt(snapshot.ut))), []
        return state, []

    if state.phase == V1_HOLD:
        elapsed = float("nan")
        if _is_finite(snapshot.ut) and _is_finite(state.phase_entry_ut):
            elapsed = snapshot.ut - state.phase_entry_ut
        if _is_finite(elapsed) and elapsed >= state.params.dwell_hold_seconds:
            st = replace(state, hold_elapsed=elapsed,
                         ramp_index=0, ramp_step_frame=0)
            factors = st.params.ramp_factors
            if not factors:
                # A legal empty stair: skip V1_RAMP entirely, honestly marked
                # (never "completed" -- no stair ran).
                st = replace(st, ramp_ended_reason=V1_RAMP_ENDED_EMPTY)
                return (_v1_enter(st, V1_SOI_WARP, snapshot.ut),
                        [Action(ACTION_WARP_TO_UT, _v1_soi_target(st))])
            return (_v1_enter(st, V1_RAMP, snapshot.ut),
                    [Action(ACTION_SET_RAILS_WARP, float(factors[0]))])
        if state.phase_frames > state.params.hold_frames:
            return _v1_flake(
                replace(state, hold_elapsed=elapsed),
                "phase %s: the 1x hold never accumulated %.0f game seconds "
                "within %d frames (elapsed=%s); the game clock is not "
                "advancing" % (V1_HOLD, state.params.dwell_hold_seconds,
                               state.params.hold_frames, _obs_fmt(elapsed))), []
        return state, []

    if state.phase == V1_RAMP:
        # EARLY-EXIT GUARD, checked before the stair advances: never let the
        # high-factor steps overshoot the recorded SOI crossing. The guard
        # hands off to the crossing leg with the ramp honestly marked
        # "soi-guard" rather than "completed".
        soi_target = _v1_soi_target(state)
        if (_is_finite(soi_target) and _is_finite(snapshot.ut)
                and snapshot.ut >= soi_target):
            st = replace(state, ramp_ended_reason=V1_RAMP_ENDED_SOI_GUARD)
            return (_v1_enter(st, V1_SOI_CROSS, snapshot.ut),
                    [Action(ACTION_SET_RAILS_WARP,
                            float(st.params.soi_cross_factor))])
        step_frame = state.ramp_step_frame + 1
        if step_frame >= state.params.ramp_step_frames:
            nxt = state.ramp_index + 1
            if nxt >= len(state.params.ramp_factors):
                st = replace(state, ramp_index=nxt, ramp_step_frame=0,
                             ramp_ended_reason=V1_RAMP_ENDED_COMPLETED)
                return (_v1_enter(st, V1_SOI_WARP, snapshot.ut),
                        [Action(ACTION_CANCEL_WARP),
                         Action(ACTION_WARP_TO_UT, soi_target)])
            st = replace(state, ramp_index=nxt, ramp_step_frame=0)
            return st, [Action(ACTION_SET_RAILS_WARP,
                               float(st.params.ramp_factors[nxt]))]
        st = replace(state, ramp_step_frame=step_frame)
        if st.phase_frames > st.params.ramp_frames:
            return _v1_flake(
                st,
                "phase %s: the warp stair never finished within %d frames "
                "(stair index %d of %d)"
                % (V1_RAMP, st.params.ramp_frames, st.ramp_index,
                   len(st.params.ramp_factors))), []
        return st, []

    if state.phase == V1_SOI_WARP:
        target = _v1_soi_target(state)
        if _is_finite(snapshot.ut) and _is_finite(target) \
                and snapshot.ut >= target - 1.0:
            return (_v1_enter(state, V1_SOI_CROSS, snapshot.ut),
                    [Action(ACTION_SET_RAILS_WARP,
                            float(state.params.soi_cross_factor))])
        if state.phase_frames > state.params.soi_warp_frames:
            return _v1_flake(
                state,
                "phase %s: the native warp toward the recorded SOI crossing "
                "(target UT %s) never arrived within %d frames (ut=%s)"
                % (V1_SOI_WARP, _obs_fmt(target),
                   state.params.soi_warp_frames, _obs_fmt(snapshot.ut))), []
        return state, []

    if state.phase == V1_SOI_CROSS:
        end = state.soi_entry_ut + state.params.soi_dwell_trail
        if _is_finite(snapshot.ut) and snapshot.ut >= end:
            st = replace(state, soi_cross_exit_ut=snapshot.ut)
            return (_v1_enter(st, V1_DONE, snapshot.ut),
                    [Action(ACTION_CANCEL_WARP)])
        if state.phase_frames > state.params.soi_cross_frames:
            return _v1_flake(
                state,
                "phase %s: the held rails crossing never reached "
                "soi_entry+trail (target UT %s) within %d frames (ut=%s)"
                % (V1_SOI_CROSS, _obs_fmt(end), state.params.soi_cross_frames,
                   _obs_fmt(snapshot.ut))), []
        return state, []

    if state.phase == V1_DONE:
        return state, []

    return _v1_flake(state, "phase %s: unreachable machine phase" % state.phase), []


def evaluate_v1_map_dwell_assertions(
        frames, params: V1MapDwellParams,
        state: Optional[V1MapDwellState] = None) -> List["AssertionOutcome"]:
    """V1's assertion rows: the FULL delegated B11 row set over the nested
    flight state (the flight leg must hold to the same standard it holds as a
    standalone nightly), then the rewind rows (R1 semantics), then the dwell
    rows. Every dwell row is machine-CARRIED evidence stamped on the frame
    that produced it, so ``frames`` rides the shared evaluate seam unused."""
    st = state
    phases = tuple(getattr(st, "phases_reached", ()) or ())
    flight = getattr(st, "flight", None)
    flight_phases = tuple(getattr(flight, "phases_reached", ()) or ())

    rows: List[AssertionOutcome] = list(evaluate_b5_assertions(
        frames, params.b5,
        phases_reached=flight_phases,
        min_target_altitude=getattr(flight, "min_target_altitude", None),
        state=flight))

    idle_reading = str(getattr(st, "recorder_idle_reading", "") or "")
    idle_met = (bool(getattr(st, "recorder_idle_observed", False))
                and idle_reading == "false"
                and V1_REWIND in phases)
    rows.append(AssertionOutcome(
        "recorderIdleBeforeRewind", idle_met, (idle_reading or None),
        {"required": V1_REWIND, "seamVerb": "RecordingState",
         "stopSeamResult": str(getattr(st, "stop_result", "") or "") or None,
         "probes": int(getattr(st, "state_probe", 0)) + 1,
         "channel": "observed"}))

    regression = getattr(st, "ut_regression", float("nan"))
    rewound_met = (_is_finite(regression)
                   and regression >= params.min_ut_regression)
    rows.append(AssertionOutcome(
        "clockRewound", rewound_met, regression,
        {"required": V1_CAMERA,
         "minRegressionSeconds": params.min_ut_regression,
         "preUt": _json_safe(getattr(st, "pre_rewind_ut", float("nan"))),
         "postUt": _json_safe(getattr(st, "post_rewind_ut", float("nan"))),
         "channel": "observed"}))

    # THE REPLAY-SCOPE PRECONDITION, observed: the rewind landed the playhead
    # BEFORE the flown mission's first frame. PlaybackScopeTracker latches a
    # committed recording into replay scope only when the live playhead is
    # observed at-or-before its activation start, so a rewind that lands
    # AFTER flight_start leaves the flown tree dormant and the whole dwell
    # vacuous -- this row is what names that failure instead of leaving it to
    # the logContract miss.
    post_ut = getattr(st, "post_rewind_ut", float("nan"))
    start_ut = getattr(st, "flight_start_ut", float("nan"))
    scope_met = (_is_finite(post_ut) and _is_finite(start_ut)
                 and post_ut <= start_ut + V1_REPLAY_SCOPE_TOLERANCE_SECONDS)
    rows.append(AssertionOutcome(
        "rewoundBeforeFlightStart", scope_met,
        _json_safe(post_ut),
        {"flightStartUt": _json_safe(start_ut),
         "toleranceSeconds": V1_REPLAY_SCOPE_TOLERANCE_SECONDS,
         "why": "PlaybackScopeTracker enters a committed recording into "
                "replay scope when the playhead is observed at-or-before its "
                "activation start + 2.0 s; the mission's first-frame UT is a "
                "LOWER bound on that activation start (launch is later), so "
                "this comparison is conservative-sound",
         "channel": "observed"}))

    cam_met = (bool(getattr(st, "camera_map_observed", False))
               and (V1_WARP_IN in phases or V1_HOLD in phases))
    rows.append(AssertionOutcome(
        "mapCameraObserved", cam_met,
        (CAMERA_MODE_MAP if getattr(st, "camera_map_observed", False) else None),
        {"required": CAMERA_MODE_MAP,
         "focusBody": params.camera_focus_body,
         "channel": "observed"}))

    hold = getattr(st, "hold_elapsed", float("nan"))
    # V1_SOI_WARP is the empty-stair exit (dwellRampFactors = [] skips V1_RAMP
    # entirely), so either successor phase proves the hold completed.
    hold_met = (_is_finite(hold)
                and hold >= params.dwell_hold_seconds - 1.0
                and (V1_RAMP in phases or V1_SOI_WARP in phases))
    rows.append(AssertionOutcome(
        "dwellHeld1x", hold_met, _json_safe(hold),
        {"requiredSeconds": params.dwell_hold_seconds,
         "channel": "observed"}))

    ramp_reason = str(getattr(st, "ramp_ended_reason", "") or "")
    ramp_met = ramp_reason in (V1_RAMP_ENDED_COMPLETED, V1_RAMP_ENDED_SOI_GUARD,
                               V1_RAMP_ENDED_EMPTY)
    rows.append(AssertionOutcome(
        "warpRampDriven", ramp_met, (ramp_reason or None),
        {"factors": list(params.ramp_factors),
         "stairIndexReached": int(getattr(st, "ramp_index", 0)),
         # COMMANDED, honestly labelled: the stair advances on held frames,
         # never on an achieved warp rate (the server may clamp a factor).
         "channel": "commanded"}))

    soi_ut = getattr(st, "soi_entry_ut", float("nan"))
    rows.append(AssertionOutcome(
        "soiEntryUtObservedInFlight", _is_finite(soi_ut), _json_safe(soi_ut),
        {"targetBody": params.b5.target_body, "channel": "observed"}))

    exit_ut = getattr(st, "soi_cross_exit_ut", float("nan"))
    crossed_met = (_is_finite(exit_ut) and _is_finite(soi_ut)
                   and exit_ut >= soi_ut + params.soi_dwell_trail - 1.0
                   and V1_DONE in phases)
    rows.append(AssertionOutcome(
        "dwellCrossedRecordedSoiUt", crossed_met, _json_safe(exit_ut),
        {"recordedSoiEntryUt": _json_safe(soi_ut),
         "trailSeconds": params.soi_dwell_trail,
         "why": "the dwell's LIVE clock re-crossed the recorded Kerbin->%s "
                "boundary UT while the per-frame probe sampled"
                % params.b5.target_body,
         "channel": "observed"}))

    return rows


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
    # AIM-THEN-WARP BUDGET anchor (B12 flight 1): the same warp-ARRIVAL UT,
    # re-anchoring the CORRECTION-BURN phase BUDGET so it bounds the burn
    # rather than the ballistic wait for the node. None = no arrival yet (the
    # budget counts from phase entry, exactly as it always did for a round
    # with no aim-warp). See correction_budget_expired.
    corr_budget_anchor_ut: Optional[float] = None
    # WHY the last correction round ended (CORR_GIVEUP_*; "" = none yet). A
    # diffed observability latch: a round exit is otherwise indistinguishable
    # from a clean one in the log, and B12 flight 1's diagnosis hinged
    # entirely on knowing which exit fired.
    corr_giveup: str = CORR_GIVEUP_NONE
    # Native warp_to_ut issues emitted THIS PHASE (B12 flight 2; widened by the
    # 2026-07-25 review to every _b5_native_warp call site). RESET on every
    # phase entry, because the failure it bounds is a SINGLE warp episode
    # fighting itself -- see MAX_PHASE_WARP_ISSUES for why per-mission counting
    # bounded the wrong thing and put B7's multi-round heliocentric coast at
    # false-flake risk.
    phase_warp_issues: int = 0
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
    # --- ORBIT-mission tail (B11/B12); all inert with capture_enabled False.
    # Consecutive in-target-SOI frames with a finite ABOVE-SURFACE periapsis
    # still AHEAD of us (the PLAN-CAPTURE arming debounce), and its MIRROR: the
    # consecutive in-target-SOI frames the arming verdict came back FALSE. The
    # mirror is the liveness bound -- with a dark periapsis clock nothing else
    # in capture mode can terminate the phase short of the wall reaper (see
    # CAPTURE_NEVER_ARMED_FRAMES). Exactly one of the two is non-zero.
    capture_arm_streak: int = 0
    capture_unarmed_streak: int = 0
    # Consecutive PLAN-CAPTURE frames whose planned node is NOT at the arrival
    # periapsis (see CAPTURE_NODE_SANITY_DEBOUNCE_FRAMES).
    capture_node_bad_streak: int = 0
    # CAPTURE-BURN executor supervision (B11 flight 1). Consecutive frames
    # OBSERVING NodeExecutor.Enabled == False with a node pending, the number
    # of bounded mj_execute_nodes re-issues spent, and the number of bounded
    # stale-node re-plans spent.
    capture_exec_disabled_streak: int = 0
    capture_exec_reissues: int = 0
    capture_replans_done: int = 0
    # Consecutive in-gate PARK frames, and the latch that the park was EVER
    # in-gate (so the give-up can distinguish "never stabilized" from
    # "stabilized but never HELD through the dwell" -- the forge_lko pattern).
    park_stable_streak: int = 0
    park_ever_stable: bool = False
    # The captured orbit read at PARK ENTRY: the capturedInTargetOrbit
    # assertion's carried evidence (the frames cannot carry it -- evaluate
    # discards them for this machine).
    capture_apoapsis: Optional[float] = None
    capture_periapsis: Optional[float] = None
    capture_eccentricity: Optional[float] = None
    # --- LANDING-mission tail (B13/B14); all inert with landing_enabled False.
    # The COMMANDED latch (mj_land_untargeted was emitted) -- carried ONLY so
    # the log can show commanded-vs-observed side by side. NOTHING gates on it:
    # that is the entire lesson of B1's chute latch, which read True for a whole
    # flight on which the canopy never opened.
    landing_engaged: bool = False
    # Consecutive DESCENT frames OBSERVING LandingAutopilot.Enabled == False
    # before touchdown, and the bounded re-issues of mj_land_untargeted spent.
    landing_ap_down_streak: int = 0
    landing_ap_reissues: int = 0
    # landing-no-progress window anchor: the surface altitude and UT the current
    # window started from. Re-anchored every time the window delivers its drop,
    # so a healthy descent rolls the window forward forever and a stalled one
    # runs it out exactly once.
    landing_alt_ref: Optional[float] = None
    landing_alt_ref_ut: Optional[float] = None
    # Consecutive DESCENT frames whose ELAPSED window PROVED no progress
    # (``LANDING_STALL_FLAT``) or could not be read at all
    # (``LANDING_STALL_BLIND``). ``landing-no-progress`` fires at
    # ``LANDING_STALL_DEBOUNCE_FRAMES``; any PENDING / OK / disarmed / vspeed
    # frame resets it, because the claim the give-up makes is about a SUSTAINED
    # observation. Bounded IN THE MACHINE (the phase ends on the frame that
    # reaches the depth), so it is safe as a DIFF field.
    landing_stall_streak: int = 0
    # Frames on which an ELAPSED window under-delivered its drop from an anchor
    # HIGH enough to deliver it, while the craft's own vertical speed was finite
    # and NEGATIVE, so the give-up was WITHHELD (``LANDING_PROGRESS_VSPEED``).
    # Non-zero means the drop window is running against a genuinely
    # slow-but-descending profile -- the operator signal that
    # ``landingProgressMinDropMeters`` / ``landingProgressWindowSeconds`` are
    # mis-sized for this body, not that anything is broken. Rides the periodic
    # machine-state line and the status file ONLY (deliberately NOT a DIFF field:
    # it can increment on consecutive frames, and an uncapped diffed counter is
    # the ~180-360-extra-Info-lines trap PARK already paid for).
    landing_vspeed_holds: int = 0
    # Frames on which the window was DISARMED because its anchor sat below
    # ``landingProgressMinDropMeters`` AGL (``LANDING_PROGRESS_UNSATISFIABLE``).
    # Non-zero says "the last stretch of this descent was watched by
    # descentTimeoutSeconds, not by the drop window" -- which is the honest
    # answer to "what bounded the final descent", and the number an operator
    # needs before believing the drop window guarded anything near the ground.
    # Same surfaces as landing_vspeed_holds, and NOT a DIFF field for the same
    # reason.
    landing_unsat_holds: int = 0
    # Consecutive in-gate LANDED-SETTLE frames, and the latch that the landing
    # was EVER settled (so the give-up separates "never settled" from "settled
    # but not in-gate at the end of the dwell" -- the PARK / forge_lko pattern).
    landed_stable_streak: int = 0
    landed_ever_stable: bool = False
    # The touchdown read at LANDED-SETTLE ENTRY: the landedOnTargetBody
    # assertion's carried evidence (the frames cannot carry it -- this
    # machine's evaluate discards them).
    landed_body: str = ""
    landed_situation: str = ""
    landed_vertical_speed: Optional[float] = None
    landed_horizontal_speed: Optional[float] = None
    # The mid-mission command-seam CommitTree verdict actually OBSERVED
    # ("OK" / "ERROR" / "TIMEOUT"); "" while never issued or still polling.
    # SHARED by ORBIT-COMMIT and SURFACE-COMMIT (one seam, one verdict).
    commit_result: str = ""
    # Park round-out trim bookkeeping (park_trim_ecc_max > 0 only). Both stay 0
    # for every lane that does not arm the trim, so their presence in the
    # machine-state dump is inert.
    park_trim_attempts: int = 0    # circularize-at-apoapsis plans ISSUED
    park_trim_execs: int = 0       # of those, how many were handed to the
                                   # executor (execs < attempts is the
                                   # "this attempt still needs burning" edge)
    phases_reached: Tuple[str, ...] = (B5_PRELAUNCH,)
    verdict: Optional[str] = None
    flake_phase: Optional[str] = None
    # A SPECIFIC flake reason (resolve_flight_verdict prefers it over the generic
    # "phase X timed out"): every ORBIT-tail give-up names its own failure so an
    # operator reads WHY without post-hoc archaeology.
    flake_reason: Optional[str] = None
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
    if phase == B5_PLAN_CAPTURE:
        return params.capture_plan_timeout
    if phase == B5_CAPTURE_BURN:
        return params.capture_burn_timeout
    if phase == B5_PARK:
        return params.park_timeout
    if phase in (B5_ORBIT_COMMIT, B5_SURFACE_COMMIT):
        return params.commit_timeout
    if phase == B5_DESCENT:
        return params.descent_timeout
    if phase == B5_LANDED_SETTLE:
        return params.landed_timeout
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
    to ``phases_reached``. RETURN (the flyby free-return), ORBIT-COMMITTED (the
    capture mission's committed-in-foreign-SOI terminal) and SURFACE-COMMITTED
    (the landing mission's committed-ON-the-foreign-body terminal) are the only
    phases whose ENTRY terminates the machine (done, verdict None -- the
    assertions decide).

    ``phase_warp_issues`` resets here: the thrash watchdog bounds ONE warp
    episode, and a phase entry is exactly the episode boundary (see
    MAX_PHASE_WARP_ISSUES)."""
    entry = ut if _is_finite(ut) else state.phase_entry_ut
    return replace(
        state,
        phase=new_phase,
        phase_entry_ut=entry,
        peak_apoapsis=peak,
        phases_reached=state.phases_reached + (new_phase,),
        phase_warp_issues=0,
        done=(new_phase in (B5_RETURN, B5_ORBIT_COMMITTED, B5_SURFACE_COMMITTED)),
    )


def _b5_stay_or_flake(state: B5State, snapshot: TelemetrySnapshot,
                      peak: Optional[float]) -> B5State:
    if _b5_over_budget(state, snapshot):
        return replace(state, peak_apoapsis=peak, verdict=MISSION_FLAKE,
                       flake_phase=state.phase, done=True)
    return replace(state, peak_apoapsis=peak)


def _b5_park_trim_step(state: B5State, snapshot: TelemetrySnapshot,
                       peak: Optional[float]) -> Tuple[B5State, List[Action]]:
    """The CIRCULARIZE exit, with the optional park ROUND-OUT trim in front of
    it. Called only once the periapsis window is already met.

    With ``park_trim_ecc_max`` at its 0.0 default this is exactly the pre-trim
    line it replaced -- straight into ORBIT, no actions -- so B2/B4/B5/B6/B7/
    B11-B14 are byte-identical. Armed, it holds CIRCULARIZE while MechJeb
    rounds the park out, because the interplanetary ejection planner sizes its
    burn at the parking orbit's SEMI-MAJOR AXIS and applies it at whatever
    radius its ejection geometry picks; on an eccentric park those differ and
    the hyperbolic excess collapses (the full derivation, with B15 flight 3's
    measured numbers, is in ``park_trim_verdict``).

    Bounded on every path: PARK_TRIM_MAX_ATTEMPTS plans, the executor commanded
    at most once per plan, and the whole thing inside CIRCULARIZE's own
    ``circularizeTimeoutSeconds`` game-time budget. Exhausting the attempts is
    a NAMED flake, never a silent proceed -- a lane that armed the trim did so
    because an out-of-round park breaks its transfer, so quietly continuing on
    one would just relocate the failure to a coast budget 11.8M game seconds
    later, which is exactly the archaeology this trim exists to end."""
    verdict = park_trim_verdict(
        state.params.park_trim_ecc_max, snapshot.eccentricity,
        snapshot.node_count, state.park_trim_attempts, state.park_trim_execs)
    if verdict in (PARK_TRIM_OFF, PARK_TRIM_OK):
        return _b5_enter(state, B5_ORBIT, snapshot.ut, peak), []
    if verdict in (PARK_TRIM_PLAN, PARK_TRIM_EXECUTE):
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            # The phase budget expired on this very frame. Commanding MechJeb
            # on a mission that has already given up is pure noise in the log
            # and one more RPC against a dead flight.
            return stayed, []
        if verdict == PARK_TRIM_PLAN:
            return (replace(stayed,
                            park_trim_attempts=state.park_trim_attempts + 1),
                    [Action(ACTION_MJ_PLAN_PARK_TRIM)])
        return (replace(stayed, park_trim_execs=state.park_trim_execs + 1),
                [Action(ACTION_MJ_EXECUTE_NODES)])
    if verdict == PARK_TRIM_GIVEUP:
        return _b5_named_flake(
            state,
            "park round-out never reached eccentricity <= %.4f after %d "
            "MechJeb circularize-at-apoapsis attempt(s) (last read %s): an "
            "eccentric park mis-sizes the interplanetary ejection, so "
            "proceeding would plan a transfer aimed at nothing"
            % (state.params.park_trim_ecc_max, state.park_trim_attempts,
               _obs_fmt(snapshot.eccentricity)),
            peak), []
    # PARK_TRIM_WAIT: a node is on the board and the executor owns it. Bounded
    # by CIRCULARIZE's own game-time budget -- but a budget expiry HERE means
    # something specific (a node nobody is burning), and the standing rule is
    # that every give-up names itself rather than surfacing as a generic
    # "phase CIRCULARIZE timed out".
    stayed = _b5_stay_or_flake(state, snapshot, peak)
    if stayed.done and stayed.flake_reason is None:
        return _b5_named_flake(
            state,
            "park round-out node never cleared: %d node(s) still pending after "
            "%d plan(s) and %d executor hand-off(s), ecc %s -- the trim burn "
            "was commanded but nothing consumed the node"
            % (snapshot.node_count, state.park_trim_attempts,
               state.park_trim_execs, _obs_fmt(snapshot.eccentricity)),
            peak), []
    return stayed, []


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


def _b5_correction_via_bodies(params: B5Params) -> Tuple[str, ...]:
    """The via SOIs in which a NO-ENCOUNTER mid-course correction is meaningful.

    Not the same list as ``via_bodies``, and B15 flight 3 is why. That flight
    widened ``viaBodyNames`` to ``["Sun", "Mun"]`` because an inward Eve
    ejection legitimately transits the Mun's SOI on the way out -- a COAST
    LEGALITY statement. But the no-encounter early correction trigger reads the
    same list, so widening it silently made the craft eligible for an
    interplanetary course correction WHILE INSIDE THE MUN'S SOI, on a Mun
    flyby hyperbola. Both correction rounds were spent there, and MechJeb
    priced the "correction" at 1464.1 m/s against a 200 m/s cap, so both were
    removed and the two rounds bought nothing.

    A correction toward an interplanetary target only makes sense once the
    craft is in the SOI the TARGET also orbits, which on every interplanetary
    lane in the suite is exactly ``return_body`` (the transfer's parent, and
    the body the flyby exits back into). Narrowing to it is a strict subset,
    so it can only ever REMOVE a firing opportunity that was wrong anyway.

    THE SUBSET CLAIM IS A PRECONDITION, NOT AN INVARIANT, AND THIS FUNCTION DOES
    NOT ENFORCE IT. It holds only while ``return_body`` is itself a member of
    ``via_bodies``, which is true of all three interplanetary specs today. A
    future lane whose ``returnBodyName`` sits OUTSIDE ``viaBodyNames`` would make
    this return a body the coast never declared legal -- ADDING a firing
    opportunity and inverting the whole safety argument. Pinned by
    ``test_the_return_body_is_a_member_of_the_via_bodies_on_every_lane``
    (test_shells.py); if that cell ever fails, re-argue this function rather
    than widening the spec.

    IDENTICAL for every lane flown to date: B7/Duna has return_body "Sun" and
    via_bodies ("Sun",), so this returns ("Sun",) unchanged; B5/B6 are not
    interplanetary and return their (empty) via list, and their no-encounter
    trigger is inert regardless because it requires time-mode correction
    triggers. Only B15/B16 see a difference, and only by losing "Mun"."""
    if params.interplanetary_transfer and params.return_body:
        return (params.return_body,)
    return params.via_bodies


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
    TIME mode (B7): body is a CORRECTION via body AND time_to_soi finite AND <=
    the round's threshold (fires in heliocentric space, never during the
    home-SOI escape, and only while a target encounter exists -- which is also
    OperationCourseCorrection's precondition).
    ALTITUDE mode (B5/B6): body == home AND altitude finite AND >= the round's
    threshold. Both NaN-fail-closed.

    THE BODY DOMAIN IS `_b5_correction_via_bodies`, NOT `via_bodies`, AND B15
    FLIGHT 5 IS WHY. `time_to_soi` is the clock to ANY SOI change, not to the
    TARGET's. B7/Duna never transits a moon on its way out, so during its coast
    that clock IS the Sun -> Duna transition and the distinction never showed.
    B15's inward ejection DOES transit the Mun, `viaBodyNames` was widened to
    ["Sun", "Mun"] to keep that coast legal, and inside the Mun's SOI the clock
    becomes the MUN-EXIT time -- a few thousand seconds, which trivially
    satisfies round 0's 20,000,000 s threshold. Flight 5 therefore spent BOTH
    correction rounds on a Mun flyby hyperbola ~8,400 s after ejection, where
    MechJeb priced the fix at 378.6 m/s against the 200 m/s cap so both were
    discarded, and by the time the craft was on the heliocentric leg with a
    real phase error to fix, `rounds_pending` was already False. The coast then
    ran to its budget with a transfer whose geometry was RIGHT (perihelion 0.046
    Eve SOI radii off) and whose PHASE was never corrected.

    Narrowing to the transfer-parent SOI is a strict subset and is IDENTICAL
    for every lane flown to date (B7: return_body "Sun", via_bodies ("Sun",)) --
    subject to the ``return_body in via_bodies`` precondition documented on
    ``_b5_correction_via_bodies``, which no code enforces and one test pins."""
    p = state.params
    if not _b5_rounds_pending(state):
        return False
    idx = state.correction_rounds_done
    if p.correction_trigger_time_to_soi:
        return (snapshot.body in _b5_correction_via_bodies(p)
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


def correction_budget_expired(ut: float, phase_entry_ut: float,
                              budget_anchor_ut: Optional[float],
                              budget: float,
                              aim_warp_target: Optional[float]) -> bool:
    """CORRECTION-BURN's own budget verdict. Pure; primitives in, bool out.

    The phase budget bounds the BURN, never the ballistic wait for the node
    (see the MJ/aim-then-warp block above for the measured B11-vs-B12 proof):

      - While an aim-then-warp is IN FLIGHT toward a still-future node
        (``aim_warp_target`` set and ``ut`` short of it) the budget is
        SUPPRESSED. The wait is bounded by the node's own UT, and a game-time
        bound cannot bound the only real failure here anyway -- a STALLED warp
        advances no game time, so it would never fire; the runner's warp-stall
        watchdog and the mission WALL budget own that class.
      - Otherwise the clock runs from ``budget_anchor_ut`` when set (stamped
        at the warp ARRIVAL, the same seam that re-anchors the no-start clock
        and the aligned streak -- warp time is not burn time), else from
        ``phase_entry_ut`` (a round with no aim-warp is unchanged).

    A non-finite ``ut`` never expires the budget (fail closed: an unreadable
    clock must not end a live round)."""
    if not _is_finite(ut):
        return False
    if aim_warp_target is not None and ut < aim_warp_target:
        return False
    anchor = budget_anchor_ut if budget_anchor_ut is not None else phase_entry_ut
    return (ut - anchor) > budget


def classify_correction_timeout(corr_burn_started: bool, node_count: int,
                                orbit_changed: bool) -> str:
    """Name a CORRECTION-BURN budget expiry. Pure.

    ``CORR_TIMEOUT_NO_START`` - the burner never throttled up AND the orbit
    never moved: a dead actor (the node is pending, nothing is burning). This
    is the case B12 flight 1 rode out under the GENERIC "phase CORRECTION-BURN
    timed out", which is exactly what the liveness rule forbids.
    ``CORR_TIMEOUT_INCOMPLETE`` - a burn demonstrably ran (throttle commanded
    or the orbit moved) and simply did not finish inside the budget."""
    if corr_burn_started or orbit_changed:
        return CORR_TIMEOUT_INCOMPLETE
    if node_count < 1:
        return CORR_TIMEOUT_INCOMPLETE
    return CORR_TIMEOUT_NO_START


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
            corr_nostart_anchor_ut=None, flameout_streak=0,
            # B12 flight 1: the aim-warp budget anchor and the round-give-up
            # latch are per-ROUND, so a fresh burn phase starts with neither.
            # Inert for TRANSFER-BURN / CAPTURE-BURN (neither reads them).
            corr_budget_anchor_ut=None, corr_giveup=CORR_GIVEUP_NONE)
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


def coast_native_warp_hold(time_to_soi: float, warp_to_cmd: Optional[float],
                           ut: float, warp_mode: str, warp_rate: float,
                           warping_to: float) -> bool:
    """True when COAST-TO-TARGET must HOLD an already-armed native warp this
    frame instead of cancelling it. Pure; primitives in, bool out.

    The native coast target is an ABSOLUTE UT. Once armed it does not need
    ``time_to_soi`` to stay readable, and B12 flight 2 proved that reading it
    under a warp ramp is exactly when it goes blind (1,154 of 1,161 warping
    COAST frames read NaN; 2,451 of 2,451 unwarped frames read finite). So:

      - no command armed, or the target is already reached -> nothing to hold
        (arrival and the ordinary policy own those frames);
      - a READABLE ``time_to_soi`` -> nothing to hold, the normal policy
        decides (including a legitimate retarget or an inside-the-lead
        handover to the rails stair);
      - a BLIND read while the game IS warping (rails mode, a live native
        warp, or any rate above 1x) -> HOLD. The blindness is the warp's own
        artifact, not evidence the encounter is gone.
      - a BLIND read with the game NOT warping -> do NOT hold. That is the
        honest "the encounter really is gone" frame, and the existing
        cancel/no-encounter paths own it.

    A non-finite ``ut`` never holds (fail closed: an unreadable clock cannot
    establish that the target is still ahead)."""
    if warp_to_cmd is None:
        return False
    if not (_is_finite(ut) and ut < warp_to_cmd):
        return False
    if _is_finite(time_to_soi):
        return False
    return (warp_mode == WARP_RAILS
            or _is_finite(warping_to)
            or (_is_finite(warp_rate) and warp_rate > 1.0))


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


def _b5_native_warp_guarded(state: B5State, snapshot: TelemetrySnapshot,
                            target: float, peak: Optional[float],
                            giveup_name: str) -> Tuple[B5State, List[Action]]:
    """``_b5_native_warp`` plus the THRASH WATCHDOG, for every call site.

    A healthy phase issues warp_to_ut once (plus the occasional hysteresis
    retarget or 30-game-second self-heal); B12 flight 2's coast issued it 3,603
    times inside one phase and crawled to the wall budget at ~2.7x. Past
    MAX_PHASE_WARP_ISSUES issues IN THIS PHASE the warp policy is provably
    fighting itself -> NAMED fast-fail carrying ``giveup_name`` (one of the
    WARP_THRASH_* constants, so an operator reads WHICH warp thrashed without
    grepping the phase out of the message).

    The counter is per-PHASE (reset in ``_b5_enter``): the failure is one warp
    episode, not a mission-long total."""
    issued, actions = _b5_native_warp(state, snapshot, target)
    if not any(a.kind == ACTION_WARP_TO_UT for a in actions):
        return issued, actions
    issues = issued.phase_warp_issues + 1
    issued = replace(issued, phase_warp_issues=issues)
    if issues > MAX_PHASE_WARP_ISSUES:
        # The warp this frame just armed is DISCARDED and torn down instead:
        # "leave nothing warped behind" (2026-07-26 review). The shipped
        # version returned actions=[] with warp_to_cmd still set, so the
        # runner drove the CLEANUP tail (StopRecording / FlushAndQuit)
        # against a warping game -- the one thing every other terminal in this
        # file is careful not to do.
        stopped, teardown = _b5_stop_all_warp(issued, snapshot)
        return _b5_named_flake(
            stopped,
            "phase %s: %s (%d native warp_to_ut issues in THIS phase, cap %d "
            "-- the warp policy is cancelling and re-arming its own warp "
            "instead of warping; ut=%s target=%s tts=%s ttPe=%s warp=%sx%s)"
            % (issued.phase, giveup_name, issues, MAX_PHASE_WARP_ISSUES,
               _obs_fmt(snapshot.ut), _obs_fmt(float(target)),
               _obs_fmt(snapshot.time_to_soi),
               _obs_fmt(snapshot.time_to_periapsis),
               snapshot.warp_mode, _obs_fmt(snapshot.warp_rate)),
            peak), teardown
    return issued, actions


def warp_liveness_starved(game_seconds: float, wall_seconds: float,
                          min_ratio: float = WARP_LIVENESS_MIN_RATIO,
                          min_wall: float = WARP_LIVENESS_MIN_WALL_SECONDS
                          ) -> bool:
    """True when an ARMED native warp has been running long enough to judge and
    is not actually warping. Pure; primitives in, bool out (the fly loop owns
    the wall clock, so it feeds the spans in).

    The complement of the thrash watchdog: that one bounds a warp being
    RE-ISSUED, this one bounds a warp armed ONCE that crawls. Neither the
    runner's warp-stall watchdog (which needs UT to FREEZE for 10 wall-s) nor a
    GAME-time phase budget (which a crawling warp advances, and which the B12
    flight-1 fix suppresses outright during an aim-warp) can see this shape; the
    only thing that used to bound it was the generic un-named wall reaper.

    Fails CLOSED in every unreadable case -- a non-finite or non-positive span
    is not evidence of a starved warp -- and never judges an episode shorter
    than ``min_wall`` (a short warp completes before it could be judged, and a
    rails ramp is legitimately slow at the start)."""
    if not (_is_finite(wall_seconds) and _is_finite(game_seconds)):
        return False
    if wall_seconds < min_wall or wall_seconds <= 0.0:
        return False
    return (game_seconds / wall_seconds) < min_ratio


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


def _b5_stop_all_warp(state: B5State,
                      snapshot: TelemetrySnapshot) -> Tuple[B5State, List[Action]]:
    """Leave nothing warped behind: the warp teardown a TERMINAL frame owes the
    runner, which drives the CLEANUP tail (StopRecording / FlushAndQuit) next
    and must not drive it against a warping game.

    The same two-case shape the impact-certain terminal and the RETURN exit
    already spell out inline: CANCEL an active native warp (the runner's cancel
    closes the warp connection and zeroes both factors), ELSE drop a held rails
    factor. Never both -- never two warp writers in one frame.

    Added by the 2026-07-26 review for the two give-ups that shipped WITHOUT a
    teardown (``capture-never-armed`` and the ``*-warp-thrash`` family). The
    two inline copies are deliberately left alone: they sit on the live-proven
    B5/B6/B7 lane and rewriting them to call this would buy nothing.

    DO NOT fold the PARK / LANDED-SETTLE self-heal blocks into this helper.
    Theirs is a STRICTLY STRONGER third shape: their second leg also fires on
    an OBSERVED ``snapshot.warp_mode == WARP_RAILS`` while ``warp_cmd`` is
    already 0, which this helper does not do (it reads only commanded state).
    Those phases are recorded 1x coverage and must pull down a rails factor
    nothing of ours commanded. The two rails cells in ``B5ParkTests`` /
    ``LandedSettleTests`` red if that leg is lost."""
    if state.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
        return (replace(state, warp_to_cmd=None, warp_cmd=0),
                [Action(ACTION_CANCEL_WARP)])
    if state.warp_cmd != 0:
        return (replace(state, warp_cmd=0),
                [Action(ACTION_SET_RAILS_WARP, 0.0)])
    return state, []


# ---------------------------------------------------------------------------
# ORBIT-mission (capture / park / commit-in-foreign-SOI) helpers, missions
# b11_mun_orbit + b12_minmus_orbit. All pure; every one of them is inert when
# ``capture_enabled`` is False, so the flyby machine is unchanged.
# ---------------------------------------------------------------------------


def _b5_named_flake(state: B5State, reason: str,
                    peak: Optional[float] = None) -> B5State:
    """A bounded give-up that NAMES its own failure (resolve_flight_verdict
    prefers ``flake_reason`` over the generic "phase X timed out"). The
    liveness principle: budgets bound SLOW, watchdogs bound BROKEN, and a
    watchdog that fires must say which actor was provably dead."""
    return replace(state,
                   peak_apoapsis=(peak if peak is not None else state.peak_apoapsis),
                   verdict=MISSION_FLAKE, flake_phase=state.phase,
                   flake_reason=reason, done=True)


def capture_flyby_warp_target(time_to_periapsis: float, ut: float,
                              lead: float = CAPTURE_PERIAPSIS_WARP_LEAD_SECONDS
                              ) -> Optional[float]:
    """The ONLY legitimate warp target inside the target SOI in capture mode:
    ``periapsis_ut - lead``. Pure; primitives in, target UT (or None) out.

    Returns None -- meaning DO NOT WARP AT ALL -- in every case where warping
    cannot be proven safe:
      - a non-finite periapsis clock or UT (fail closed: with no clock there
        is no way to show a warp would not sail past the only capture point
        on the pass; 1x is slow but correct and the phase budget bounds it);
      - the bound has already passed (``ut >= target``), including a periapsis
        that is already behind us. After periapsis there is nothing to capture
        on this pass, so the arrived-late re-plan / capture-window-missed
        backstop owns the outcome rather than more warp.

    B12 flight 3 is the case this exists for: the flyby entered the Minmus SOI
    still running the coast's RAILSx10000 warp and blew through periapsis
    before the capture was even armed."""
    if not (_is_finite(time_to_periapsis) and _is_finite(ut)):
        return None
    target = ut + time_to_periapsis - lead
    if ut >= target:
        return None
    return target


def _b5_capture_arm_ready(state: B5State, snapshot: TelemetrySnapshot) -> bool:
    """One TARGET-FLYBY frame's capture-arming verdict: capture is enabled, the
    SOI body IS the target, the arrival periapsis reads FINITE and ABOVE the
    surface, AND periapsis is still AHEAD of us. A sub-surface periapsis is an
    impact trajectory (the impact-certain terminal owns it), and a NaN read
    fails CLOSED -- never arm a capture on evidence we do not have.

    The ``time_to_periapsis > 0`` conjunct is DEFENCE IN DEPTH added after B12
    flight 3, which armed the capture AFTER periapsis (alt 41,609 m, vertical
    speed +92 m/s, already climbing away) and produced a 325 x 5.3 km graze the
    park window correctly rejected. The altitude/periapsis pair alone cannot see
    which SIDE of periapsis the craft is on; the clock can. It costs the healthy
    path nothing -- b11/b12 both build their control with read_periapsis=True,
    so the channel is live on every frame of both lanes -- but note it makes the
    ARMING depend on that opt-in read: a shell that copies b11 WITHOUT
    read_periapsis=True now never arms, which is exactly what the
    capture-never-armed give-up below is there to name in bounded time."""
    return (state.params.capture_enabled
            and snapshot.body == state.params.target_body
            and _is_finite(snapshot.periapsis) and snapshot.periapsis > 0.0
            and _is_finite(snapshot.time_to_periapsis)
            and snapshot.time_to_periapsis > 0.0)


def classify_capture_arm_failure(periapsis: float,
                                 time_to_periapsis: float) -> str:
    """WHY a TARGET-FLYBY capture frame did not arm. Pure; primitives in, one of
    the ``CAPTURE_ARM_*`` verdicts out. Only meaningful on a frame where
    ``_b5_capture_arm_ready`` already returned False.

    The three cases need DIFFERENT operator responses, which is the whole point
    of splitting them:

      - ``CAPTURE_ARM_BLIND``     the periapsis clock is unreadable (the opt-in
        read is off, it faulted, or the kRPC surface drifted). Nothing is wrong
        with the trajectory; the MACHINE is blind. Fix the channel.
      - ``CAPTURE_ARM_SUBSURFACE`` the arrival periapsis is below the surface --
        an impact trajectory. The corrections under-performed.
      - ``CAPTURE_ARM_PAST_PERIAPSIS`` periapsis is BEHIND us. There is nothing
        left to capture on this pass (B12 flight 3's signature).
    """
    if not _is_finite(periapsis) or not _is_finite(time_to_periapsis):
        return CAPTURE_ARM_BLIND
    if periapsis <= 0.0:
        return CAPTURE_ARM_SUBSURFACE
    if time_to_periapsis <= 0.0:
        return CAPTURE_ARM_PAST_PERIAPSIS
    # Every conjunct satisfied: the caller should not have asked.
    return CAPTURE_ARM_READY


# The operator-facing half of each verdict: the three shapes need three
# DIFFERENT responses, and "capture-never-armed" alone does not say which.
_CAPTURE_ARM_FAILURE_HINT = {
    CAPTURE_ARM_BLIND: ("The PERIAPSIS CLOCK IS UNREADABLE, not the "
                        "trajectory: check that the mission control was built "
                        "with read_periapsis=True and that Orbit"
                        ".time_to_periapsis still resolves on this kRPC pin."),
    CAPTURE_ARM_SUBSURFACE: ("The arrival periapsis is BELOW THE SURFACE: the "
                             "course corrections under-performed and this is "
                             "an impact trajectory."),
    CAPTURE_ARM_PAST_PERIAPSIS: ("PERIAPSIS IS ALREADY BEHIND US: the arrival "
                                 "flew through the capture point before the "
                                 "machine could arm (B12 flight 3's shape)."),
    CAPTURE_ARM_READY: ("The arming verdict disagrees with the failure "
                        "classifier -- this is a machine bug, not a flight "
                        "outcome."),
}


def _b5_capture_never_armed_giveup(state: B5State, snapshot: TelemetrySnapshot,
                                   unarmed: int, peak: Optional[float]
                                   ) -> Tuple[B5State, List[Action]]:
    """The ``capture-never-armed`` terminal, SPLIT BY VERDICT CLASS.

    THE ORDERING BUG THIS FIXES (2026-07-26 review). The never-armed gate runs
    BEFORE the IMPACT-CERTAIN EARLY TERMINAL further down the same branch, and
    on a sub-surface arrival it ALWAYS wins the race: ``_b5_capture_arm_ready``
    is False from the FIRST in-target-SOI frame, so this counter starts
    immediately, while the impact terminal cannot arm until
    ``altitude < IMPACT_WARP_GUARD_ALT`` (400 km) plus its own 5-frame
    debounce. Shipping that as a FLAKE was wrong: the SAME branch states the
    policy fifteen lines above the arming gate -- a capture that provably
    cannot happen is a DETERMINISTIC flight outcome, so ASSERT-FAIL, never a
    retryable flake -- and the give-up that pre-empted the impact terminal did
    not honour it.

    WHAT THE FIX DOES NOT BUY (checked, 2026-07-26): it does NOT save the
    retry flight. `hlib.MISSION_VERDICT_SUBKINDS` maps ASSERT-FAIL to `mission`
    and FLAKE to `autopilot-flake`, and BOTH are in
    `RETRYABLE_INVALID_SUBKINDS`, so the harness retries either way (B7's Ike
    red is the live proof: attempt 1 ASSERT-FAIL, attempt 2 flown and passed).
    What it buys is the CLASSIFICATION: `mission` says the FLIGHT failed and
    sends an operator to the trajectory, `autopilot-flake` says the MACHINE
    broke and sends them to the machine. On a sub-surface arrival the
    trajectory is the answer.

    So CAPTURE_ARM_SUBSURFACE now terminates MISSION_ASSERT_FAIL carrying the
    impact language, and the other two shapes keep the FLAKE fast-fail they
    were designed as. CAPTURE_ARM_BLIND above all: a dark periapsis clock is a
    MACHINE fault (the channel, not the trajectory) and it is the one shape
    NOTHING else in capture mode can end, which is why the bound exists.

    This is NOT the impact terminal relocated. It fires on the ARMING counter,
    30 frames deep; the impact terminal keeps its own 5-frame debounce and
    still owns a low-altitude sub-surface arrival that develops AFTER a ready
    frame reset this run (jittery periapsis), where it is by far the faster of
    the two.

    Every path tears the warp down on the way out (``_b5_stop_all_warp``): the
    shipped version returned ``actions=[]`` with ``warp_to_cmd`` still armed,
    so the runner drove the CLEANUP tail (StopRecording / FlushAndQuit)
    against a warping game."""
    why = classify_capture_arm_failure(snapshot.periapsis,
                                       snapshot.time_to_periapsis)
    evidence = ("%d consecutive in-%s-SOI frames could not arm PLAN-CAPTURE "
                "(cap %d; pe=%s ttPe=%s alt=%s vspd=%s ut=%s). %s"
                % (unarmed, state.params.target_body,
                   CAPTURE_NEVER_ARMED_FRAMES,
                   _obs_fmt(snapshot.periapsis),
                   _obs_fmt(snapshot.time_to_periapsis),
                   _obs_fmt(snapshot.altitude),
                   _obs_fmt(snapshot.vertical_speed),
                   _obs_fmt(snapshot.ut),
                   _CAPTURE_ARM_FAILURE_HINT.get(why, "")))
    stopped, teardown = _b5_stop_all_warp(state, snapshot)
    if why == CAPTURE_ARM_SUBSURFACE:
        return replace(
            stopped,
            peak_apoapsis=(peak if peak is not None else stopped.peak_apoapsis),
            done=True, verdict=MISSION_ASSERT_FAIL,
            loss_reason=("phase %s: flyby impact certain, capture-never-armed "
                         "(%s) -- %s"
                         % (B5_TARGET_FLYBY, why, evidence))), teardown
    return _b5_named_flake(
        stopped,
        "phase %s: capture-never-armed (%s) -- %s"
        % (B5_TARGET_FLYBY, why, evidence),
        peak), teardown


def capture_node_at_periapsis(node_ut: float, ut: float,
                              time_to_periapsis: float,
                              tolerance: float =
                              CAPTURE_NODE_PERIAPSIS_TOLERANCE_SECONDS
                              ) -> bool:
    """True when a planned capture node actually sits at the arrival periapsis.
    Pure; primitives in, bool out. See
    CAPTURE_NODE_PERIAPSIS_TOLERANCE_SECONDS for the tolerance rationale.

    FAILS CLOSED on any non-finite input -- a node whose UT cannot be read, or
    a periapsis clock that cannot be read, is not evidence that the node is
    where the capture needs it. That is deliberate asymmetry: refusing a good
    node costs one bounded named flake, flying a node planned against MechJeb's
    inherited time reference costs the mission AND records a garbage orbit."""
    if not (_is_finite(node_ut) and _is_finite(ut)
            and _is_finite(time_to_periapsis)):
        return False
    return abs(node_ut - (ut + time_to_periapsis)) <= tolerance


def _b5_enter_plan_capture(state: B5State, snapshot: TelemetrySnapshot,
                           peak: Optional[float],
                           issue_plan: bool = True
                           ) -> Tuple[B5State, List[Action]]:
    """PLAN-CAPTURE entry. Same warp prelude as the correction entry: bring warp
    under PLAN control before planning -- cancel an active native warp (which
    also zeroes the rails factors runner-side), else step a held rails factor
    straight to the plan hold (never 1x: planning is an RPC, and the 10x hold
    bounds plan-position drift to ~5 game-s per poll).

    ``issue_plan=False`` enters the phase WITHOUT emitting the plan on this
    frame, leaving PLAN-CAPTURE's own plan_retry_seconds cadence to issue it on
    the NEXT poll. That is the stale-node RE-PLAN path (see the CAPTURE-BURN
    caller): the transition frame emits only the node CLEAR, because MechJeb's
    NodeExecutor self-aborts on the next physics frame ONLY once the node list
    is observed EMPTY (decompiled 2.15.1 OnFixedUpdate: !_hasNodes -> Abort()),
    and remove_nodes() + make_nodes() in one fly-loop frame are two RPCs that
    can both land inside a single 20 ms physics frame -- so the precondition
    may never be observed, the executor stays engaged across the re-plan, and
    the next handoff calls execute_all_nodes() on an already-enabled module
    (the poisoned re-engage family from flights 6-7). The attempt budget is
    NOT consumed by the skipped emission: plan_attempts stays 0 and last_plan_ut
    is backdated by the retry interval, so the next poll issues attempt 1."""
    entered = _b5_enter(state, B5_PLAN_CAPTURE, snapshot.ut, peak)
    plan_hold = min(state.params.plan_warp_factor,
                    max_legal_rails_factor(snapshot.body, snapshot.altitude))
    if state.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
        prelude: List[Action] = [Action(ACTION_CANCEL_WARP)]
        entered_warp_cmd = 0
    elif state.warp_cmd != plan_hold:
        prelude = [Action(ACTION_SET_RAILS_WARP, float(plan_hold))]
        entered_warp_cmd = plan_hold
    else:
        prelude = []
        entered_warp_cmd = state.warp_cmd
    if issue_plan:
        last_plan_ut = snapshot.ut if _is_finite(snapshot.ut) else 0.0
        attempts = 1
        plan_actions = [Action(ACTION_MJ_PLAN_CAPTURE)]
    else:
        last_plan_ut = ((snapshot.ut - state.params.plan_retry_seconds)
                        if _is_finite(snapshot.ut) else 0.0)
        attempts = 0
        plan_actions = []
    entered = replace(entered,
                      last_plan_ut=last_plan_ut,
                      warp_cmd=entered_warp_cmd, warp_to_cmd=None,
                      body_blank_count=0, plan_attempts=attempts,
                      capture_arm_streak=0, capture_unarmed_streak=0,
                      capture_node_bad_streak=0)
    return entered, prelude + plan_actions


def classify_capture_nostart(node_ut: float, ut: float, node_count: int,
                             replans_done: int,
                             max_replans: int = MAX_CAPTURE_REPLANS,
                             grace: float = CAPTURE_BURN_WINDOW_GRACE_SECONDS
                             ) -> str:
    """Classify a CAPTURE-BURN frame that TRIPPED the static-at-1x no-start
    watchdog. Pure; primitives in, verdict string out.

    ``CAPTURE_NOSTART_HOLD``  - a node is pending and its UT (plus ``grace``)
    has NOT passed. MechJeb's NodeExecutor ignites at ``node.UT -
    halfBurnTime``, i.e. never later than ``node.UT``, and its own WARPALIGN
    branch deliberately holds at 1x with an UNCHANGED orbit for up to
    MJ_EXECUTOR_WARPALIGN_HOLD_SECONDS before that instant (see the constant's
    decompile citation). A no-start verdict inside that window is a FALSE
    POSITIVE -- exactly the one that killed B11 flight 1 nine seconds early --
    so the machine holds instead of flaking.

    ``CAPTURE_NOSTART_REPLAN`` - a node is pending, its window HAS passed with
    the orbit still unchanged (the craft arrived LATE, e.g. coasted through
    periapsis with the node unburned) and a re-plan is still budgeted: clear
    the stale node and solve a fresh capture rather than fly a node whose
    window is gone.

    ``CAPTURE_NOSTART_FLAKE`` - nothing left to try: no node at all, an
    unreadable node clock, or the re-plan budget is spent. The caller names
    the failure.

    A NON-FINITE ``node_ut`` (or ``ut``) fails CLOSED to the flake, checked
    BEFORE the window arithmetic: an unreadable node clock is neither
    evidence that the burn is still ahead (no hold) nor evidence that its
    window has passed (no re-plan), so the original no-start name owns it."""
    if node_count < 1:
        return CAPTURE_NOSTART_FLAKE
    if not (_is_finite(node_ut) and _is_finite(ut)):
        return CAPTURE_NOSTART_FLAKE
    if ut <= node_ut + grace:
        return CAPTURE_NOSTART_HOLD
    if replans_done < max_replans:
        return CAPTURE_NOSTART_REPLAN
    return CAPTURE_NOSTART_FLAKE


def classify_capture_executor(
        executor_enabled: int, disabled_streak: int, reissues_done: int,
        node_count: int,
        debounce: int = CAPTURE_EXECUTOR_DISABLED_DEBOUNCE_FRAMES,
        max_reissues: int = MAX_CAPTURE_EXECUTOR_REISSUES) -> Tuple[str, int]:
    """One CAPTURE-BURN frame's OBSERVED executor verdict; returns
    ``(verdict, new_disabled_streak)``. Pure.

    This is the commanded-vs-OBSERVED channel the B11 flight-1 forensics
    called for: the machine COMMANDS ``mj_execute_nodes`` and previously never
    verified that MechJeb's NodeExecutor actually ENGAGED (the same gap that
    produced the B-DOCK docking-AP and the EVA-4 ladder-release defects).

      ``CAPTURE_EXEC_UNKNOWN`` - ``executor_enabled`` is the -1 UNREAD
      sentinel. NO action: an unread channel is not evidence of a dead actor,
      and the node-clock classifier owns the outcome (fail closed against
      acting on evidence we do not have). The streak resets.

      ``CAPTURE_EXEC_RUNNING`` - observed ENABLED, or no node is pending (the
      executor legitimately self-disables once it consumes the node; the
      consumed / under-burn paths own that frame). The streak resets.

      ``CAPTURE_EXEC_REISSUE`` - observed DISABLED for ``debounce``
      consecutive frames with a node still pending and re-issue budget left:
      hand the node to the executor again.

      ``CAPTURE_EXEC_DEAD`` - the same debounced observation past
      ``max_reissues``: the executor provably will not arm. Distinctly named
      fast-fail, seconds after the evidence rather than hours later."""
    if node_count < 1:
        return CAPTURE_EXEC_RUNNING, 0
    if executor_enabled < 0:
        return CAPTURE_EXEC_UNKNOWN, 0
    if executor_enabled > 0:
        return CAPTURE_EXEC_RUNNING, 0
    streak = disabled_streak + 1
    if streak < debounce:
        return CAPTURE_EXEC_RUNNING, streak
    if reissues_done < max_reissues:
        return CAPTURE_EXEC_REISSUE, 0
    # Cap the streak at the debounce depth (the delta-review C1 discipline):
    # past the re-issue budget every disabled frame would otherwise increment
    # it forever, and each increment is a gate line + a window dump.
    return CAPTURE_EXEC_DEAD, debounce


def _b5_capture_achieved(params: B5Params, snapshot: TelemetrySnapshot) -> bool:
    """CAPTURE-BURN done evidence: the craft is BOUND to the target body. A
    hyperbolic approach reads a NEGATIVE apoapsis in the target's frame, so a
    POSITIVE apoapsis at/below the ceiling, a periapsis at/above the floor and
    an eccentricity at/below the ceiling together mean the capture burn
    actually closed the orbit. Every conjunct fails CLOSED on a non-finite
    read (the burn budget then owns the outcome)."""
    if not (_is_finite(snapshot.apoapsis) and _is_finite(snapshot.periapsis)
            and _is_finite(snapshot.eccentricity)):
        return False
    if not (0.0 < snapshot.apoapsis <= params.park_max_apoapsis):
        return False
    if snapshot.periapsis < params.park_min_periapsis:
        return False
    return snapshot.eccentricity <= params.park_max_eccentricity


def _b5_park_stable(params: B5Params, snapshot: TelemetrySnapshot) -> bool:
    """One PARK frame's stability verdict (the LIVE-PROVEN forge_lko park gate,
    re-pointed at the FOREIGN body): still in the target SOI, an accepted
    orbital situation, a bound orbit inside the capture window, and the tumble
    under the ceiling. Every conjunct fails closed on a non-finite read."""
    if snapshot.body != params.target_body:
        return False
    if snapshot.situation not in params.park_situations:
        return False
    if not _b5_capture_achieved(params, snapshot):
        return False
    return (_is_finite(snapshot.angular_velocity)
            and snapshot.angular_velocity <= params.park_max_angular_velocity)


def _b5_park_entry_actions() -> List[Action]:
    """The vehicle configuration the PARKED, COMMITTED recording must capture:
    throttle CUT (nothing is burning when the tree closes), every maneuver node
    CLEARED (no pending burn rides into the committed terminal), attitude HELD
    (SAS stability-assist + RCS on so the stage does not tumble through the
    dwell), and rails warp DROPPED to 1x (the park dwell is the recorded
    coverage the whole lane exists for -- warping through it would leave the
    committed recording a handful of on-rails checkpoints)."""
    return ([Action(ACTION_SET_RAILS_WARP, 0.0),
             Action(ACTION_CUT_THROTTLE, 0.0),
             Action(ACTION_MJ_ABORT_AND_CLEAR_NODES)]
            + _bdock_attitude_hold_actions())


def _b5_left_target_soi(state: B5State,
                        snapshot: TelemetrySnapshot) -> Optional[B5State]:
    """Shared in-SOI guard for the capture tail: once the machine is planning /
    burning / parking / committing inside the target SOI, a REAL foreign body
    reading means the craft left without capturing -- the deterministic mission
    failure this lane exists to catch. "" (no reading this frame) is NOT a
    departure (the blank-body dwell and the vessel-lost detectors own it).
    Returns the terminal state, or None when the guard does not fire."""
    if snapshot.body == "" or snapshot.body == state.params.target_body:
        return None
    return replace(
        state, done=True, verdict=MISSION_ASSERT_FAIL,
        loss_reason=("left the target SOI during %s without a committed park: "
                     "body=%r (expected %r)"
                     % (state.phase, snapshot.body, state.params.target_body)))


# ---------------------------------------------------------------------------
# LANDING-mission (descent / touchdown / commit-on-the-surface) helpers,
# missions b13_mun_landing + b14_minmus_landing. All pure; every one of them is
# inert when ``landing_enabled`` is False, so the ORBIT and flyby machines are
# unchanged.
# ---------------------------------------------------------------------------


def _b5_landing_config(params: B5Params) -> Tuple[float, bool, bool, bool]:
    """The MechJeb LandingAutopilot configuration the runner writes before
    engaging: ``(touchdownSpeed, deployGears, deployChutes, rcsAdjustment)``."""
    return (float(params.landing_touchdown_speed),
            bool(params.landing_deploy_gears),
            bool(params.landing_deploy_chutes),
            bool(params.landing_rcs_adjustment))


def _b5_touched_down(params: B5Params, snapshot: TelemetrySnapshot) -> bool:
    """True on an OBSERVED touchdown: the kRPC situation is one of the accepted
    landed situations. Deliberately situation-only (no body / speed conjuncts):
    this is the DESCENT EXIT, and the questions "was it the right body?" and
    "did it settle?" belong to the assertions and to the LANDED-SETTLE dwell
    respectively. Folding them in here would leave a craft that touched down on
    the WRONG body flying a descent autopilot forever instead of terminating
    with its own named failure."""
    return snapshot.situation in params.landed_situations


def classify_landing_autopilot(
        ap_enabled: int, down_streak: int, reissues_done: int,
        touched_down: bool,
        debounce: int = LANDING_AP_DISABLED_DEBOUNCE_FRAMES,
        max_reissues: int = MAX_LANDING_AP_REISSUES) -> Tuple[str, int]:
    """One DESCENT frame's OBSERVED landing-autopilot verdict; returns
    ``(verdict, new_down_streak)``. Pure.

    THE COMMANDED-VS-OBSERVED CHANNEL this lane's headline liveness gate reads.
    ``mj_land_untargeted`` is a COMMAND; ``LandingAutopilot.Enabled`` is the
    only evidence MechJeb's module actually took control. Directly modelled on
    ``classify_capture_executor``, because the failure is literally the same
    shape as the B11 flight-1 NodeExecutor defect.

      ``LANDING_AP_RUNNING``  - observed ENABLED, or the craft has already
      TOUCHED DOWN. The streak resets.

      THE TOUCHDOWN CARVE-OUT IS UNREACHABLE FROM THE LIVE PATH, and saying so
      is the honest version of what this comment used to claim (2026-07-26
      review). The hazard is real: MechJeb's own FinalDescent step calls
      ``StopLanding()`` (which clears the module's user pool, i.e. disables it)
      the frame it observes ``Vessel.LandedOrSplashed``, so an observed FALSE
      after touchdown is the module reporting SUCCESS, and reading it as a dead
      autopilot would fast-fail a PERFECT landing. But the live guarantee is
      not provided HERE: ``b5_decide``'s DESCENT block tests ``_b5_touched_down``
      FIRST and leaves the phase, so this function is only ever called with
      ``touched_down=False`` in flight. The conjunct is therefore a SECOND,
      ORDER-INDEPENDENT guarantee, kept deliberately: it costs one comparison,
      it keeps the pure classifier correct for any caller that does not own
      ``b5_decide``'s ordering, and deleting it would move a load-bearing safety
      property into a call-site ordering constraint documented only in a
      comment. Cells: ``LandingDescentTests`` pins the LIVE guarantee (the
      ordering) in ``test_a_landed_frame_exits_whatever_the_autopilot_reads``;
      ``LandingAutopilotClassifierTests`` pins THIS backstop in
      ``test_touchdown_reads_as_success_for_an_out_of_order_caller``.

      ``LANDING_AP_UNKNOWN``  - ``ap_enabled`` is the -1 UNREAD sentinel. NO
      action: an unread channel is not evidence of a dead actor (fail closed
      against acting on evidence we do not have), and the altitude-trend
      watchdog plus the descent budget own the outcome. The streak resets.

      ``LANDING_AP_REISSUE``  - observed DISABLED for ``debounce`` consecutive
      pre-touchdown frames with re-issue budget left: hand the descent to
      MechJeb again.

      ``LANDING_AP_DEAD``     - the same debounced observation past
      ``max_reissues``: the module provably will not arm. Distinctly named
      fast-fail (``landing-autopilot-not-enabled``) seconds after the evidence
      instead of ~900 game seconds later via the altitude watchdog, or an hour
      later via the budget."""
    if touched_down:
        return LANDING_AP_RUNNING, 0
    if ap_enabled < 0:
        return LANDING_AP_UNKNOWN, 0
    if ap_enabled > 0:
        return LANDING_AP_RUNNING, 0
    streak = down_streak + 1
    if streak < debounce:
        return LANDING_AP_RUNNING, streak
    if reissues_done < max_reissues:
        return LANDING_AP_REISSUE, 0
    # Cap the streak at the debounce depth (the CAPTURE_EXEC delta-review C1
    # discipline): past the re-issue budget every disabled frame would otherwise
    # increment it forever, and each increment is a gate line + a window dump.
    return LANDING_AP_DEAD, debounce


def landing_progress_verdict(altitude: float, ref_altitude: Optional[float],
                             elapsed_seconds: float, window_seconds: float,
                             min_drop: float,
                             vertical_speed: float = float("nan")) -> str:
    """One DESCENT frame's altitude-trend verdict over the running no-progress
    window. Pure; primitives in, one of the ``LANDING_PROGRESS_*`` /
    ``LANDING_STALL_*`` verdicts out.

    This is the SECOND, independent liveness channel: ``Enabled`` answers "did
    the module take control", this answers "is it achieving anything". An
    autopilot that is enabled and holding a useless attitude (no engine, a
    refused throttle, an unreachable target) reads ENABLED forever, so the
    observed-enabled gate alone cannot bound it.

      ``LANDING_PROGRESS_PENDING`` - the window has not elapsed (or its clock is
      unreadable). Nothing is decided; the caller HOLDS.
      ``LANDING_PROGRESS_OK``      - the window delivered at least ``min_drop``
      metres of surface-altitude drop. The caller RE-ANCHORS the window.
      ``LANDING_PROGRESS_UNSATISFIABLE`` - the ANCHOR is below ``min_drop``
      metres AGL, so the window asks for a drop that does not exist below the
      craft. The watchdog is DISARMED for this window; the caller HOLDS and does
      NOT re-anchor.
      ``LANDING_PROGRESS_VSPEED``  - the window under-delivered from an anchor
      that COULD have delivered, but the craft's OWN vertical speed is finite
      and NEGATIVE. The caller HOLDS and does NOT re-anchor, so the accumulated
      drop keeps counting toward the same anchor.
      ``LANDING_STALL_BLIND``      - the altitude (now or at the anchor) is not
      finite. FAIL CLOSED: an unreadable altitude is NOT evidence of descent, so
      it must never buy the descent another window. It gets its OWN name
      because the operator response differs completely from a real stall -- fix
      the channel, not the trajectory.
      ``LANDING_STALL_FLAT``       - a full window elapsed with finite altitudes,
      the anchor was high enough for the drop to be possible, the drop was not
      delivered, and the vertical-speed channel does not show a descent either.
      The descent is provably not descending.

    THE NEAR-GROUND BAND IS DISARMED, NOT RESCUED (review round 2, 2026-07-26).
    Below ``min_drop`` metres AGL the window is UNSATISFIABLE BY CONSTRUCTION:
    there is not that much altitude left to shed, so no outcome in that band
    carries information and the only thing the gate can produce there is a false
    give-up. The first cut tried to cover the band with the vertical-speed
    channel instead ("the craft is descending, so withhold"), and THE FLIGHT
    DATA SAYS THAT DOES NOT HOLD: on B14 flight 1's Minmus final descent
    (`harness/results/2026-07-25_1543_B14-minmus-landing_mission.stdout.log`)
    MechJeb hops and settles, and 23 of the 1,330 DESCENT frames read a vertical
    speed >= 0 -- the craft physically CLIMBS, peak +1.305 m/s, in runs of up to
    FIVE consecutive frames (ut 278,489.7 -> 278,493.8, alt 92.711 -> 96.370 m).
    A rescue that requires a negative vertical speed is absent on exactly those
    frames, so with a below-``min_drop`` anchor and an elapsed window the old
    code would have flaked a HEALTHY landing. The band is therefore disarmed
    outright, and the vertical-speed channel is kept only as what it actually is
    -- a corroboration channel ABOVE the band, where a genuinely descending
    craft that under-delivers means the knobs are mis-sized for the body.

    WHAT ACTUALLY KEPT B13/B14 GREEN was neither guard: it was anchor geometry.
    MEASURED from the machine-state lines of both flights, each descent closed
    exactly ONE window and then touched down with the next one still running:
    B13 re-anchored at alt 64,963.5 m (ut 22,634.365) and landed 453.9 s later
    with 446.1 s of the 900 s window unspent; B14 re-anchored at alt 16,099.0 m
    (ut 278,100.482) and landed 481.2 s later with 418.8 s unspent. Both anchors
    are far ABOVE the 5,000 m band, so neither the disarm nor the vertical-speed
    channel has ever been exercised live -- they are covered by cells only, and
    must not be read as flight-proven.

    SLOW is still bounded, and not by this gate: ``descentTimeoutSeconds`` is the
    instrument for slow (2,200 game s against MEASURED descents of 1,353.9 /
    1,381.3 s). This watchdog bounds BROKEN, and it now takes
    ``LANDING_STALL_DEBOUNCE_FRAMES`` consecutive frames of proof to fire, like
    every other liveness gate in this machine.

    The window is measured from the ANCHOR, not from phase entry, so a healthy
    descent rolls it forward indefinitely and only a genuine stall ever runs one
    out."""
    if not (_is_finite(elapsed_seconds) and _is_finite(window_seconds)):
        return LANDING_PROGRESS_PENDING
    if elapsed_seconds < window_seconds:
        return LANDING_PROGRESS_PENDING
    if ref_altitude is None or not (_is_finite(altitude)
                                    and _is_finite(ref_altitude)):
        # BLIND stays AHEAD of both holds on purpose: an unreadable altitude is
        # a CHANNEL fault, and the operator response is "fix the channel". A
        # descending craft on a dark altitude channel is still a dark altitude
        # channel, and an anchor that cannot be read cannot be tested against
        # the near-ground band either.
        return LANDING_STALL_BLIND
    if (ref_altitude - altitude) >= min_drop:
        return LANDING_PROGRESS_OK
    if ref_altitude < min_drop:
        # DISARMED, and ahead of the vertical-speed channel deliberately: in this
        # band the drop test cannot be satisfied by any craft above the surface,
        # so a VSPEED hold here would count a "the knobs are mis-sized" signal
        # that is really "the gate does not apply".
        return LANDING_PROGRESS_UNSATISFIABLE
    if _is_finite(vertical_speed) and vertical_speed < 0.0:
        return LANDING_PROGRESS_VSPEED
    return LANDING_STALL_FLAT


def landed_stable(params: B5Params, snapshot: TelemetrySnapshot) -> bool:
    """One LANDED-SETTLE frame's settled verdict: still on the TARGET body, an
    accepted landed situation, and BOTH speed components under their floors.
    Every conjunct fails CLOSED on a non-finite read -- the PARK gate's
    discipline, re-pointed at the surface.

    The HORIZONTAL conjunct is the load-bearing one and the reason the lane
    opts into an extra telemetry read: a lander that touched down on a slope and
    is sliding reads ``situation == LANDED`` with a vertical speed of ~0, so
    vertical speed alone would certify a craft that is still moving."""
    if snapshot.body != params.target_body:
        return False
    if snapshot.situation not in params.landed_situations:
        return False
    if not (_is_finite(snapshot.vertical_speed)
            and abs(snapshot.vertical_speed) <= params.landed_max_vertical_speed):
        return False
    return (_is_finite(snapshot.horizontal_speed)
            and abs(snapshot.horizontal_speed)
            <= params.landed_max_horizontal_speed)


def _b5_descent_entry_actions(params: B5Params) -> List[Action]:
    """The vehicle configuration handed to MechJeb at DESCENT entry.

    ``MJ_ABORT_AND_CLEAR_NODES`` first: the NodeExecutor must be released before
    another MechJeb autopilot claims the thrust/attitude users, and MechJeb's
    landing PREDICTOR is explicitly built against a node-free vessel (its own
    targeted entry point calls ``Vessel.RemoveAllManeuverNodes()`` "for the
    benefit of the landing predictions module"). PARK already cleared the nodes,
    so this is normally idempotent -- emitted anyway because the cost is one RPC
    and the failure it prevents is a wedged executor fighting a descent.

    NO SAS / RCS action and NO warp action:
      - MechJeb's attitude controller OWNS the action group while the landing AP
        holds it (decompiled MechJebModuleAttitudeController drives
        ``ActionGroups.SetGroup(KSPActionGroup.SAS, ...)`` in both directions),
        so re-asserting our own SAS state here would be a second writer.
      - MechJeb's landing states own the WARP for the same reason: every one of
        them calls ``Core.Warp.WarpRegularAtRate`` / ``WarpToUT`` /
        ``MinimumWarp`` gated on ``Core.Node.Autowarp``. Two warp writers in one
        phase is precisely the cancel/re-arm thrash class that cost B12 flight 2
        its whole wall budget, so DESCENT is deliberately warp-PASSIVE and the
        runner sets that shared Autowarp flag EXPLICITLY inside the engage
        action (the B-DOCK flight-12 lesson)."""
    return [Action(ACTION_MJ_ABORT_AND_CLEAR_NODES),
            Action(ACTION_MJ_LAND_UNTARGETED,
                   landing_config=_b5_landing_config(params))]


def _b5_landed_settle_entry_actions() -> List[Action]:
    """The vehicle configuration the LANDED, COMMITTED recording must capture:
    rails DROPPED to 1x (the landed dwell IS the recorded coverage this whole
    lane exists for -- warping through it would leave the committed recording a
    handful of on-rails checkpoints), throttle CUT, MechJeb's landing autopilot
    RELEASED (idempotent: its own FinalDescent step stops it on the touchdown
    frame, but the dwell must not run with a module still holding the thrust and
    attitude user pools), and SAS held so the settled lander does not tip.

    No RCS action: the Kerbal X upper stage has no RCS blocks, and MechJeb's
    StopLanding already drops its own RCS user when RcsAdjustment was set."""
    return [Action(ACTION_SET_RAILS_WARP, 0.0),
            Action(ACTION_CUT_THROTTLE, 0.0),
            Action(ACTION_MJ_STOP_LANDING),
            Action(ACTION_SET_SAS)]


def _b5_enter_descent(state: B5State, snapshot: TelemetrySnapshot,
                      peak: Optional[float]) -> Tuple[B5State, List[Action]]:
    """PARK -> DESCENT. Anchors the no-progress window at the park altitude and
    latches the COMMANDED engage (observability only -- nothing gates on it)."""
    entered = _b5_enter(state, B5_DESCENT, snapshot.ut, peak)
    entered = replace(
        entered,
        landing_engaged=True,
        landing_ap_down_streak=0,
        landing_stall_streak=0,
        landing_alt_ref=(float(snapshot.altitude)
                         if _is_finite(snapshot.altitude) else None),
        landing_alt_ref_ut=(float(snapshot.ut) if _is_finite(snapshot.ut)
                            else None),
        warp_cmd=0, warp_to_cmd=None)
    return entered, _b5_descent_entry_actions(state.params)


def _b5_enter_landed_settle(state: B5State, snapshot: TelemetrySnapshot,
                            peak: Optional[float]
                            ) -> Tuple[B5State, List[Action]]:
    """DESCENT -> LANDED-SETTLE on an OBSERVED touchdown. Freezes the touchdown
    reading as the ``landedOnTargetBody`` assertion's carried evidence: this
    machine's evaluator discards the frames, so if the state does not carry it,
    nothing does."""
    entered = _b5_enter(state, B5_LANDED_SETTLE, snapshot.ut, peak)
    entered = replace(
        entered,
        warp_cmd=0, warp_to_cmd=None,
        landed_stable_streak=0,
        landed_body=snapshot.body,
        landed_situation=snapshot.situation,
        landed_vertical_speed=(float(snapshot.vertical_speed)
                               if _is_finite(snapshot.vertical_speed) else None),
        landed_horizontal_speed=(float(snapshot.horizontal_speed)
                                 if _is_finite(snapshot.horizontal_speed)
                                 else None))
    return entered, _b5_landed_settle_entry_actions()


def _b5_landing_loss_reason(state: B5State, snapshot: TelemetrySnapshot,
                            base: str) -> str:
    """Wrap a generic vessel-lost reason in the DISTINCTLY NAMED landing form
    when the loss happened inside the landing tail.

    WHY IT NEEDS ITS OWN NAME (the operator's explicit requirement): a CRASHED
    landing must not read as a generic timeout OR as a success. Before this, a
    lithobraked lander and a craft whose telemetry went stale in orbit produced
    the SAME line. Byte-identical for every non-landing phase -- ``
    _B5_LANDING_PHASES`` is unreachable without landingEnabled."""
    if state.phase not in _B5_LANDING_PHASES:
        return base
    return ("%s: %s (phase %s, body=%s situation=%s alt=%s vspd=%s hspd=%s "
            "ut=%s) -- the craft did not survive the landing; this is a FAILED "
            "landing, not a timeout and not a success"
            % (LANDING_GIVEUP_VESSEL_LOST, base, state.phase,
               snapshot.body or "?", snapshot.situation or "?",
               _obs_fmt(snapshot.altitude), _obs_fmt(snapshot.vertical_speed),
               _obs_fmt(snapshot.horizontal_speed), _obs_fmt(snapshot.ut)))


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

    ORBIT-MISSION TAIL (captureEnabled, missions b11_mun_orbit /
    b12_minmus_orbit). With the flag OFF none of these branches is reachable
    and the machine is byte-identical to the flyby shape above.
      - TARGET-FLYBY -> PLAN-CAPTURE: CAPTURE_ARM_DEBOUNCE_FRAMES consecutive
        in-target-SOI frames with a finite ABOVE-SURFACE periapsis. The
        SOI-EXIT native warp is suppressed in capture mode (warping toward the
        exit is warping toward the failure); the rails stair still floors at
        flybyWarpFactor, so no 1x. body == the return body here is now an
        ASSERT-FAIL ("flew past instead of circularizing").
      - PLAN-CAPTURE -> CAPTURE-BURN: the shared plan phase with MechJeb's
        circularize-at-PERIAPSIS operation. No fall-through (a capture node IS
        the mission); PLAN_MAX_ATTEMPTS or the plan budget flake with a NAMED
        reason.
      - CAPTURE-BURN -> PARK: the executor (autowarp EXPLICIT) consumed the
        node AND the orbit is BOUND inside the capture window (0 < ap <=
        parkMaxApoapsisMeters, pe >= parkMinPeriapsisMeters, ecc <=
        parkMaxEccentricity -- a hyperbolic approach reads a NEGATIVE apoapsis,
        so this is real capture evidence). The phase SUPERVISES its actor from
        an OBSERVED channel (snapshot.node_executor_enabled, B11 flight 1):
        NodeExecutor.Enabled read FALSE for
        CAPTURE_EXECUTOR_DISABLED_DEBOUNCE_FRAMES consecutive frames with a
        node pending re-issues mj_execute_nodes, bounded by
        MAX_CAPTURE_EXECUTOR_REISSUES, then fast-fails
        capture-executor-not-enabled. The static-at-1x no-start signature is
        classified by classify_capture_nostart, because MechJeb's own
        WARPALIGN state holds at 1x with an unchanged orbit for up to
        MJ_EXECUTOR_WARPALIGN_HOLD_SECONDS BEFORE ignition (see that
        constant's decompile citation): pre-node frames HOLD, a passed burn
        window re-plans once (MAX_CAPTURE_REPLANS) rather than fly a stale
        node, and past that it fast-fails capture-window-missed. Named
        fast-fails: capture under-burn (executor wedged, orbit still unbound),
        capture-executor-not-enabled, capture-window-missed and
        capture-executor-no-start (no node / no readable node clock).
      - PARK -> ORBIT-COMMIT: throttle cut, nodes cleared, SAS + RCS held and
        rails dropped to 1x on entry (the park dwell IS the recorded coverage),
        then parkDebounceFrames consecutive in-gate frames HELD across
        parkDwellSeconds of game time. Named give-ups distinguish "never
        stabilized" from "stabilized but never held".
      - ORBIT-COMMIT -> ORBIT-COMMITTED: the mid-mission command-seam
        CommitTree (the B-DOCK route-1 reserved-id bridge) answers OK -> the
        TERMINAL (done, verdict None): the tree is committed while the vessel
        is parked in the foreign SOI. ERROR / TIMEOUT / budget expiry flake
        with a named reason.
      - Every capture-tail phase carries the in-SOI guard: a REAL foreign body
        reading is an ASSERT-FAIL (left the target SOI without a committed
        park); "" holds (the blank-body / vessel-lost detectors own it).

    LANDING-MISSION TAIL (landingEnabled, missions b13_mun_landing /
    b14_minmus_landing). Reachable ONLY from PARK and ONLY with the flag on, so
    with it OFF the machine is byte-identical to the ORBIT shape above.
      - PARK -> DESCENT: the SAME held park dwell the ORBIT lane commits on, but
        instead of the seam it clears the nodes and hands the craft to MechJeb's
        UNTARGETED LandingAutopilot. DESCENT is warp-PASSIVE (MechJeb's landing
        states own the warp) and is bounded by FOUR distinctly named give-ups
        plus its GAME-time budget: landing-autopilot-not-enabled (OBSERVED
        LandingAutopilot.Enabled down past the bounded re-issues -- never
        trusting that the command engaged anything), landing-no-progress
        (altitude not decreasing over a debounced window, with a separate
        altitude-unreadable name), landing-touchdown-timeout (the budget) and
        landing-vessel-lost (a CRASH, which must read as neither a timeout nor a
        success).
      - DESCENT -> LANDED-SETTLE: an OBSERVED landed situation. Throttle cut,
        the autopilot released, SAS held, rails at 1x, then landedDebounceFrames
        consecutive settled frames (target body + landed situation + BOTH speed
        components under their floors) HELD across landedDwellSeconds. The
        give-up is landed-never-stable and, as in PARK, it distinguishes "never
        settled" from "settled but not in-gate at the end of the dwell".
      - LANDED-SETTLE -> SURFACE-COMMIT -> SURFACE-COMMITTED: the SAME route-1
        mid-mission seam CommitTree the ORBIT lane fires, but fired while
        LANDED. OK is the TERMINAL; ERROR / TIMEOUT / budget expiry flake with a
        named reason.
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
            loss_reason=_b5_landing_loss_reason(
                state, snapshot,
                "vessel-lost (unreadable after repeated telemetry failures)")), []

    # FROZEN-TELEMETRY vessel-lost detector. PRELAUNCH is exempt (the pad is
    # legitimately static) and so is the LANDING tail's settled dwell -- see
    # _B5_FROZEN_EXEMPT_PHASES for why a LANDED craft is the one case that can
    # legitimately reproduce the dead-vessel signature. Both exemptions are
    # unreachable for every non-landing mission.
    if (state.phase != B5_PRELAUNCH
            and state.phase not in _B5_FROZEN_EXEMPT_PHASES):
        limit = state.params.frozen_sample_limit
        new_sig, new_count, tripped = _advance_frozen_count(
            state.frozen_sig, state.frozen_count, snapshot, limit)
        if tripped:
            return replace(
                state, peak_apoapsis=peak, frozen_sig=new_sig, frozen_count=new_count,
                done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=_b5_landing_loss_reason(
                    state, snapshot,
                    "vessel-lost (telemetry frozen %d consecutive samples "
                    "while airborne; vessel presumed destroyed)" % limit)), []
        state = replace(state, frozen_sig=new_sig, frozen_count=new_count)

    # Flyby-floor evidence: min finite altitude while inside the target SOI. For
    # a flyby that is TARGET-FLYBY only; for a capture mission it spans the whole
    # in-SOI stay (_B5_IN_TARGET_SOI_PHASES), so the same assertion also certifies
    # that the PARKED orbit's periapsis cleared the terrain.
    if (state.phase in _B5_IN_TARGET_SOI_PHASES
            and snapshot.body == state.params.target_body
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
            return _b5_park_trim_step(state, snapshot, peak)
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
            # A burn happened, the executor wedged, and the burn-done evidence
            # is NOT met: the TLI under-burned -- no transfer exists to coast
            # on. An autopilot failure, so a bounded flake (retry per policy).
            #
            # NAMED, because the generic message actively misleads (B15 flights
            # 1-2, 2026-07-25). This branch surfaced as "phase TRANSFER-BURN
            # timed out" while only ~66% of transferBurnTimeoutSeconds had been
            # spent, which sent the investigation at the BUDGET when the
            # stagnation watchdog was what fired -- and the real defect was an
            # ejection eccentricity floor calibrated on an outward transfer.
            # Same class as the CORRECTION-BURN root cause above; the standing
            # rule is that every give-up names its own actor and shows the
            # evidence that failed.
            return _b5_named_flake(
                state,
                "transfer-burn under-burn: the executor stopped making "
                "progress (burn-stagnation watchdog, NOT the phase budget) "
                "with the burn-done evidence unmet -- %s, ap %s, ecc %s, "
                "nodes %d, lf %s"
                % (("ejection eccentricity floor %.4f not reached"
                    % (state.params.ejection_ecc_floor,))
                   if state.params.ejection_ecc_floor > 0.0 else
                   ("apoapsis floor %.0f m not reached"
                    % (state.params.transfer_min_apoapsis,)),
                   _obs_fmt(snapshot.apoapsis), _obs_fmt(snapshot.eccentricity),
                   snapshot.node_count, _obs_fmt(snapshot.liquid_fuel)),
                peak), []
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
        def _corr_exit(st: B5State, reason: str) -> Tuple[B5State, List[Action]]:
            entered = _b5_enter(st, B5_COAST_TO_TARGET, snapshot.ut, peak)
            entered = replace(entered,
                              correction_rounds_done=st.correction_rounds_done + 1,
                              corr_burn_started=False, min_node_dv=None,
                              phys_warp_cmd=0, warp_to_cmd=None,
                              corr_nostart_anchor_ut=None,
                              corr_budget_anchor_ut=None,
                              # WHY this round ended (B12 flight 1): a diffed
                              # latch, so every give-up emits its own loud gate
                              # line instead of being indistinguishable from a
                              # clean cut in the log.
                              corr_giveup=reason)
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

        def _corr_stay_or_flake(st: B5State) -> B5State:
            """CORRECTION-BURN's OWN stay/flake (B12 flight 1). The phase
            budget bounds the BURN, not the ballistic wait for the node: it is
            suppressed while an aim-then-warp is in flight and re-anchors at
            the warp ARRIVAL (see correction_budget_expired). Expiry is a
            NAMED flake, never the generic "phase X timed out"."""
            if not correction_budget_expired(
                    snapshot.ut, st.phase_entry_ut, st.corr_budget_anchor_ut,
                    state.params.transfer_burn_timeout, st.warp_to_cmd):
                return replace(st, peak_apoapsis=peak)
            orbit_changed = (st.burn_entry_ap is not None
                             and _is_finite(snapshot.apoapsis)
                             and abs(snapshot.apoapsis - st.burn_entry_ap)
                             > _BURN_CHANGED_EPS)
            name = classify_correction_timeout(
                st.corr_burn_started, snapshot.node_count, orbit_changed)
            return _b5_named_flake(
                st,
                "phase %s: %s (budget %.0f game-s measured from %s; nodes=%d "
                "nodeDv=%s apErr=%s thr=%s burnStarted=%s)"
                % (B5_CORRECTION_BURN, name,
                   state.params.transfer_burn_timeout,
                   ("the aim-warp arrival" if st.corr_budget_anchor_ut is not None
                    else "phase entry"),
                   snapshot.node_count, _obs_fmt(snapshot.node_dv),
                   _obs_fmt(snapshot.ap_error), _obs_fmt(snapshot.throttle),
                   st.corr_burn_started), peak)

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
            if snapshot.node_count == 0:
                return _corr_exit(state, CORR_GIVEUP_NODE_GONE)
            if _is_finite(dv) and dv <= state.params.correction_cut_dv:
                return _corr_exit(state, CORR_GIVEUP_CUT)
            if overshoot:
                return _corr_exit(state, CORR_GIVEUP_OVERSHOOT)
            if no_progress:
                return _corr_exit(state, CORR_GIVEUP_NO_PROGRESS)
            stayed = _corr_stay_or_flake(state)
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
            return _corr_exit(state, CORR_GIVEUP_NODE_GONE)
        # AIM-THEN-WARP warp-hold (operator PR gate, no-1x-coast): the aim
        # locked and the native warp toward node_ut - nodeArrivalMarginSeconds
        # is running -- rails warp FREEZES orientation, so the machine just
        # holds (self-healed). Give-up clocks do not count warp time -- and
        # since B12 flight 1 that is TRUE OF THE PHASE BUDGET TOO: the wait is
        # ballistic and MechJeb can put a Minmus-class correction node 73,733
        # game seconds ahead of a 4,000 s budget, so a game-time bound here is
        # both wrong and (against a stalled warp, which advances no game time)
        # useless. correction_budget_expired suppresses it while the warp is in
        # flight; the runner's warp-stall watchdog + the mission WALL budget own
        # a wedged warp.
        if state.warp_to_cmd is not None:
            if not (_is_finite(snapshot.ut) and snapshot.ut >= state.warp_to_cmd):
                # NO budget check here, and none is possible: the enclosing
                # condition (ut non-finite, or ut short of the aim-warp target)
                # is EXACTLY correction_budget_expired's own suppression
                # condition, so calling it here always returned False. The
                # branch that used to sit here -- "budget flake mid-warp,
                # cancel and leave nothing warped behind" -- was UNREACHABLE
                # for every input, and reading as a surviving mid-warp bound is
                # what hid the fact that NOTHING bounded a crawling aim-warp.
                # The two real bounds are now the thrash watchdog below and the
                # runner's native-warp liveness floor (warp_liveness_starved).
                held = replace(state, peak_apoapsis=peak)
                return _b5_native_warp_guarded(held, snapshot,
                                               state.warp_to_cmd, peak,
                                               WARP_THRASH_CORRECTION_AIM)
            # ARRIVAL: the server stepper zeroed the factor on completion.
            # Re-verify the attitude from FRESH readings (the streak re-earns
            # its full debounce -- rails should have held the orientation; a
            # drifted apErr re-enters the 2x-physics flip below, bounded by
            # the re-anchored give-up) and restart the no-start clock (warp
            # time is not alignment time). B12 flight 1: the phase BUDGET
            # re-anchors on the same instant and for the same reason.
            state = replace(state, warp_to_cmd=None, aligned_streak=0,
                            corr_nostart_anchor_ut=float(snapshot.ut),
                            corr_budget_anchor_ut=float(snapshot.ut))
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
            return _corr_exit(state, CORR_GIVEUP_ALIGN_NO_START)
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
        stayed = replace(_corr_stay_or_flake(state), aligned_streak=streak)
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
                return _b5_native_warp_guarded(
                    replace(stayed, aligned_streak=0), snapshot, aim_target,
                    peak, WARP_THRASH_CORRECTION_AIM)
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
            entered = _b5_enter(state, B5_TARGET_FLYBY, snapshot.ut, peak)
            if not state.params.capture_enabled:
                # FLYBY missions are unchanged: passing periapsis IS the
                # point, so the inherited coast warp rides on byte-identically.
                return entered, []
            # CAPTURE mode (B12 flight 3): the craft crosses the SOI boundary
            # still running the COAST's native warp -- flight 3 entered at
            # RAILSx10000 and its FIRST flyby poll advanced 3,907 game seconds
            # before any decision could be taken. STOP the inherited warp on
            # the transition frame; the flyby's own periapsis-bounded warp
            # re-arms next poll from a known-stopped state.
            entered = replace(entered, warp_cmd=0, warp_to_cmd=None)
            if state.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
                return entered, [Action(ACTION_CANCEL_WARP)]
            return entered, ([Action(ACTION_SET_RAILS_WARP, 0.0)]
                             if state.warp_cmd != 0 else [])
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
        # Body domain: the TRANSFER-PARENT SOI, not every via body -- see
        # _b5_correction_via_bodies. Unchanged for every lane flown to date.
        no_encounter = (bool(state.params.correction_trigger_time_to_soi)
                        and rounds_pending
                        and snapshot.body in _b5_correction_via_bodies(state.params)
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
        # B12 flight 2: set only by the SOI-coast fallback branch below, so
        # every other warp mode (pending node, altitude / time correction
        # stairs) keeps its exact pre-existing cancel behaviour.
        blind_soi_hold = False
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
                and snapshot.body in _b5_correction_via_bodies(state.params)
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
            # Confined to the correction body domain: the home-SOI escape leg
            # rides the SOI native-warp branch below instead (warp to the home
            # SOI exit).
            #
            # BODY DOMAIN = `_b5_correction_via_bodies`, THE SAME LIST BOTH
            # CORRECTION TRIGGERS READ (review follow-up; the two triggers were
            # narrowed first and this WARP branch was left reading the raw
            # `via_bodies`, an asymmetry that only bites the Eve lanes). Reading
            # the wide list here means a craft inside a NON-transfer-parent via
            # SOI enters this branch and computes dt against a threshold scaled
            # for the heliocentric leg: in the Mun's SOI, dt = 3,086 - 20,000,000
            # = -19,996,914, which fails `dt > soi_lead`, so
            # rails_factor_for_time returns 0 on the non-positive input and the
            # floor-2 stair pins the whole transit at 10x. MEASURED on B15
            # flight 7: 317 of 318 Mun-SOI frames at RAILSx10, ~3,086 game s
            # spread over ~308 wall s of a 1,236 s mission. With the domain
            # matched, the Mun transit falls through to the SOI native-warp
            # branch below (warp to the boundary minus soi_lead) exactly as the
            # home-SOI escape leg does. Provably a NO-OP off the Eve lanes:
            # B7's via_bodies IS ("Sun",) == its correction domain, and B5/B6
            # and the moon orbit/landing lanes never reach here at all (they
            # have no time-mode triggers, so they take the altitude stair
            # above).
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
            # NATIVE-WARP LATCH (B12 flight 2): this branch is also where a
            # BLIND time_to_soi lands, and a blind read UNDER WARP is the
            # warp's own artifact -- cancelling the armed command on it is
            # what produced 3,602 cancel/re-issue cycles and a 2.7x coast.
            # The target is an absolute UT and stays valid; hold it.
            blind_soi_hold = coast_native_warp_hold(
                snapshot.time_to_soi, stayed.warp_to_cmd, snapshot.ut,
                snapshot.warp_mode, snapshot.warp_rate, snapshot.warping_to)
        if native_target is not None:
            # THRASH WATCHDOG (B12 flight 2 liveness): a healthy coast issues
            # the native warp a handful of times (arm + the occasional
            # hysteresis retarget); flight 2 issued it 3,603 times in one phase
            # and crawled to the wall budget.
            return _b5_native_warp_guarded(stayed, snapshot, native_target,
                                           peak, WARP_THRASH_COAST)
        if blind_soi_hold:
            # HOLD: emit nothing, keep warp_to_cmd armed, let the warp ramp.
            return stayed, []
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
        if state.params.capture_enabled and snapshot.body == return_body:
            # CAPTURE MODE: leaving the target SOI IS the failure -- the whole
            # point of the lane is a recording that ENDS parked in the foreign
            # SOI. A deterministic outcome (the approach was never captured),
            # so ASSERT-FAIL, never a retryable flake.
            return replace(
                state, peak_apoapsis=peak, done=True, verdict=MISSION_ASSERT_FAIL,
                loss_reason=("left the target SOI %r without capturing "
                             "(body=%r); the arrival flew past instead of "
                             "circularizing"
                             % (state.params.target_body, snapshot.body))), []
        if state.params.capture_enabled and snapshot.body == state.params.target_body:
            # CAPTURE ARMING: a debounced run of in-SOI frames with a finite
            # ABOVE-SURFACE periapsis hands off to PLAN-CAPTURE. Planning at SOI
            # entry (rather than chasing periapsis with our own warp stair) is
            # deliberate: MechJeb's circularize-at-periapsis picks the burn UT
            # and the NodeExecutor's own autowarp flies the coast down to it --
            # the same proven plan -> node -> executor pipeline the TLI uses, so
            # there is no way to warp past the burn.
            if _b5_capture_arm_ready(state, snapshot):
                streak = state.capture_arm_streak + 1
                state = replace(state, capture_unarmed_streak=0)
                if streak >= CAPTURE_ARM_DEBOUNCE_FRAMES:
                    return _b5_enter_plan_capture(
                        replace(state, capture_arm_streak=0), snapshot, peak)
                state = replace(state, capture_arm_streak=streak)
            else:
                # NEVER-ARMED LIVENESS BOUND (see CAPTURE_NEVER_ARMED_FRAMES).
                # With the periapsis clock dark NOTHING else in capture mode can
                # end this phase: the warp refuses to arm (correctly), the
                # arming gate fails closed, the impact terminal needs a FINITE
                # sub-surface periapsis, and flybyTimeoutSeconds is 300,000+
                # GAME seconds -- unreachable at 1x inside the wall budget. So
                # a debounced run of unarmable frames is a NAMED fast-fail that
                # says WHICH shape it saw. Capture-mode only by construction:
                # the enclosing branch is gated on capture_enabled.
                #
                # This gate PRE-EMPTS the impact-certain terminal below on a
                # sub-surface arrival (it starts counting on the SOI-entry
                # frame; that terminal cannot arm above 400 km), so the VERDICT
                # CLASS is decided in _b5_capture_never_armed_giveup, not here:
                # sub-surface is a deterministic flight outcome -> ASSERT-FAIL,
                # the dark clock and past-periapsis stay flakes.
                unarmed = state.capture_unarmed_streak + 1
                state = replace(state, capture_arm_streak=0,
                                capture_unarmed_streak=unarmed)
                if unarmed >= CAPTURE_NEVER_ARMED_FRAMES:
                    return _b5_capture_never_armed_giveup(
                        state, snapshot, unarmed, peak)
        if not state.params.capture_enabled and snapshot.body == return_body:
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
        elif state.params.capture_enabled:
            # CAPTURE MODE (B12 flight 3): the ONLY legitimate warp target
            # inside the target SOI is periapsis_ut - the capture lead, read
            # from the ORBIT's own periapsis clock. Past that bound (or with
            # no readable clock) the machine does NOT warp: there is nothing
            # left to capture on this pass, and 1x is slow but correct. This
            # REPLACES the rails distance stair here -- the stair is floored
            # at flybyWarpFactor and knows nothing about the periapsis CLOCK,
            # so at the rates a cross-SOI arrival carries it sailed past the
            # capture point inside the arming debounce.
            desired = 0
            native_target = capture_flyby_warp_target(
                snapshot.time_to_periapsis, snapshot.ut)
        elif (_is_finite(snapshot.time_to_soi) and _is_finite(snapshot.ut)
                and snapshot.time_to_soi > state.params.soi_lead):
            # (c) Outer flyby legs: NATIVE warp to the SOI EXIT minus
            # soi_lead. NEVER in capture mode: warping toward the SOI EXIT is
            # warping toward the failure terminal, and the arming debounce is
            # only ~3 polls away from handing the leg to PLAN-CAPTURE anyway.
            # Capture mode rides the rails stair below (flybyWarpFactor floor,
            # so still never 1x). The game's own altitude limits shape the passage
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
            # Same THRASH WATCHDOG as the coast: the capture warp's target
            # (ut + ttPe - lead) is constant along the approach, so a phase
            # that issues it hundreds of times is cancelling and re-arming.
            return _b5_native_warp_guarded(stayed, snapshot, native_target,
                                           peak, WARP_THRASH_FLYBY)
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

    # ---- ORBIT-mission tail (B11/B12): capture -> park -> commit ------------
    # Reachable only with captureEnabled; every phase carries the in-SOI guard
    # (leaving the target SOI here is the deterministic failure) plus a NAMED
    # fast-fail for its own dead actor.

    if state.phase == B5_PLAN_CAPTURE:
        left = _b5_left_target_soi(state, snapshot)
        if left is not None:
            return replace(left, peak_apoapsis=peak), []
        # NODE-UT SANITY GATE (reviewer finding, 2026-07-25). The handoff below
        # accepts ANY node_count >= 1 with no gate on WHEN the node is, and the
        # runner's circularize planner sets TimeReference.Periapsis on a
        # SHARED, PERSISTED MechJeb TimeSelector. A refused set (the property
        # throws OperationException on a disallowed reference) used to be
        # swallowed and the plan issued anyway, so INHERITED GLOBAL MechJeb
        # STATE could grant a capture at an arbitrary UT -- the same
        # commanded-vs-OBSERVED gap this branch closed for
        # NodeExecutor.Enabled. The runner now refuses to plan on a failed
        # read-back; this is the machine-side half, so a node that arrives at
        # the wrong UT by ANY route is refused rather than flown.
        #
        # DEBOUNCED, like every other gate here. Two reasons: a transient
        # node/clock read must not end a live mission, and the stale-node
        # RE-PLAN path re-enters this phase with a just-cleared node that can
        # still read node_count >= 1 for a poll or two (its UT is in the past,
        # so it would classify off-periapsis). The debounce is cheap: the phase
        # polls under the 10x plan hold, ~5 game-s per poll.
        if snapshot.node_count >= 1 and not capture_node_at_periapsis(
                snapshot.node_ut, snapshot.ut, snapshot.time_to_periapsis):
            bad = state.capture_node_bad_streak + 1
            state = replace(state, capture_node_bad_streak=bad)
            if bad >= CAPTURE_NODE_SANITY_DEBOUNCE_FRAMES:
                return _b5_named_flake(
                    replace(state, peak_apoapsis=peak),
                    "phase %s: capture-node-off-periapsis (the planned node is "
                    "not at the arrival periapsis for %d consecutive frames: "
                    "nodeUt=%s, periapsis is at %s (ut=%s + ttPe=%s), "
                    "tolerance %.0f s). Refusing to fly a capture node planned "
                    "against MechJeb's inherited time reference; nodes=%d"
                    % (B5_PLAN_CAPTURE, bad, _obs_fmt(snapshot.node_ut),
                       _obs_fmt(snapshot.ut + snapshot.time_to_periapsis),
                       _obs_fmt(snapshot.ut),
                       _obs_fmt(snapshot.time_to_periapsis),
                       CAPTURE_NODE_PERIAPSIS_TOLERANCE_SECONDS,
                       snapshot.node_count)), [
                    Action(ACTION_MJ_ABORT_AND_CLEAR_NODES)]
            # Hold this frame: do NOT hand a suspect node to the executor while
            # the debounce is still running.
            return replace(state, peak_apoapsis=peak), []
        if state.capture_node_bad_streak:
            state = replace(state, capture_node_bad_streak=0)
        new_state, actions = _b5_plan_phase(
            state, snapshot, peak,
            plan_action=Action(ACTION_MJ_PLAN_CAPTURE),
            burn_phase=B5_CAPTURE_BURN,
            # No fall-through: unlike a best-effort course correction, a capture
            # node IS the mission. PLAN_MAX_ATTEMPTS with no node (the planner
            # keeps throwing server-side) takes the flake path EARLY instead of
            # idling out the whole plan budget -- the liveness rule.
            on_timeout_phase=None,
            # The MechJeb NodeExecutor, with autowarp set EXPLICITLY runner-side
            # (B-DOCK flight-12: the executor's autowarp is shared global state,
            # so an unset one warps or coasts at 1x by luck). The DIY burner is
            # deliberately NOT used: a capture node sits hours ahead at periapsis,
            # exactly the far-node regime the executor is proven in.
            handoff_action=Action(ACTION_MJ_EXECUTE_NODES))
        if new_state.done and new_state.verdict == MISSION_FLAKE:
            return _b5_named_flake(
                new_state,
                "phase %s: no capture node (planAttempts=%d, budget %.0f "
                "game-s): MechJeb circularize-at-periapsis produced nothing "
                "inside %s SOI"
                % (B5_PLAN_CAPTURE, new_state.plan_attempts,
                   state.params.capture_plan_timeout,
                   state.params.target_body)), actions
        return new_state, actions

    if state.phase == B5_CAPTURE_BURN:
        left = _b5_left_target_soi(state, snapshot)
        if left is not None:
            return replace(left, peak_apoapsis=peak), []
        state, stuck, nostart, burned = _b5_track_burn_stagnation(state, snapshot)
        consumed = snapshot.node_count < max(state.planned_node_count, 1)
        achieved = _b5_capture_achieved(state.params, snapshot)
        if (consumed or stuck) and achieved:
            # CAPTURED. Clear any stray node, then park the stage: rails down to
            # 1x, throttle cut, nodes cleared, attitude held.
            entered = _b5_enter(state, B5_PARK, snapshot.ut, peak)
            entered = replace(entered, warp_cmd=0, warp_to_cmd=None,
                              park_stable_streak=0,
                              capture_apoapsis=float(snapshot.apoapsis),
                              capture_periapsis=float(snapshot.periapsis),
                              capture_eccentricity=float(snapshot.eccentricity))
            return entered, _b5_park_entry_actions()
        if stuck:
            # A burn demonstrably ran, progress then stopped for
            # burnStagnantSeconds, and the orbit is STILL not bound inside the
            # window: the capture under-burned. An autopilot failure ->
            # bounded, NAMED flake.
            #
            # ``stuck`` does NOT imply the node is still pending: it is
            # burn-progress stagnation, which also fires when the executor
            # CONSUMED the node and produced an out-of-window orbit. So report
            # the OBSERVED node count instead of asserting a wedge; nodes=0
            # here means "the burn finished and missed", nodes>0 means "the
            # executor stopped holding an unfinished node".
            return _b5_named_flake(
                state,
                "phase %s: capture under-burn (burn progress stalled with "
                "nodes=%d; ap=%.0f pe=%.0f ecc=%.3f is not a bound orbit "
                "inside [pe>=%.0f, ap<=%.0f, ecc<=%.2f])"
                % (B5_CAPTURE_BURN, snapshot.node_count, snapshot.apoapsis,
                   snapshot.periapsis,
                   snapshot.eccentricity, state.params.park_min_periapsis,
                   state.params.park_max_apoapsis,
                   state.params.park_max_eccentricity), peak), []
        # OBSERVED executor supervision (B11 flight 1). "We issued
        # mj_execute_nodes" is a COMMAND, never evidence; this is the read-back.
        # It runs BEFORE the static-at-1x no-start branch because it is the
        # FASTER signal: a dead executor is named in ~3 polls instead of after
        # the 600-second static window.
        exec_verdict, exec_streak = classify_capture_executor(
            snapshot.node_executor_enabled, state.capture_exec_disabled_streak,
            state.capture_exec_reissues, snapshot.node_count)
        state = replace(state, capture_exec_disabled_streak=exec_streak)
        if exec_verdict == CAPTURE_EXEC_DEAD:
            return _b5_named_flake(
                state,
                "phase %s: capture-executor-not-enabled (MechJeb "
                "NodeExecutor.Enabled read FALSE for %d consecutive frames "
                "with %d node(s) pending, after %d bounded re-issue(s) of "
                "mj_execute_nodes)"
                % (B5_CAPTURE_BURN, exec_streak, snapshot.node_count,
                   state.capture_exec_reissues), peak), []
        if exec_verdict == CAPTURE_EXEC_REISSUE:
            # Re-hand the node to the executor and re-stamp the static clock so
            # the fresh attempt earns a full window (the flameout-stage
            # re-anchor discipline). Bounded by MAX_CAPTURE_EXECUTOR_REISSUES.
            return (replace(state,
                            capture_exec_reissues=state.capture_exec_reissues + 1,
                            burn_static_since=(float(snapshot.ut)
                                               if _is_finite(snapshot.ut)
                                               else state.burn_static_since),
                            peak_apoapsis=peak),
                    [Action(ACTION_MJ_EXECUTE_NODES)])
        if nostart:
            # The orbit never changed and the craft has sat static at 1x past
            # the no-start bound. That signature is AMBIGUOUS: MechJeb's own
            # WARPALIGN state parks at 1x with an unchanged orbit for up to
            # MJ_EXECUTOR_WARPALIGN_HOLD_SECONDS *before* ignition, and
            # burn_nostart_seconds defaults to the SAME 600 s (B11 flight 1
            # died on exactly that collision). The node's own clock
            # disambiguates: MechJeb ignites at node.UT - halfBurnTime, so
            # nothing about a pre-node frame is evidence of a dead actor.
            verdict = classify_capture_nostart(
                snapshot.node_ut, snapshot.ut, snapshot.node_count,
                state.capture_replans_done)
            if verdict == CAPTURE_NOSTART_REPLAN:
                # LATE ARRIVAL: the node's burn window passed with the orbit
                # untouched (the craft coasted through periapsis unburned).
                # Never fly a node whose window is gone -- drop it and solve a
                # fresh capture. Bounded at MAX_CAPTURE_REPLANS.
                # ONLY the clear on this frame (issue_plan=False): the plan
                # follows on the next poll through PLAN-CAPTURE's own cadence,
                # so MechJeb can actually OBSERVE an empty node list and run
                # its own abort before a new node appears. See
                # _b5_enter_plan_capture's docstring for the mechanism.
                entered, plan_actions = _b5_enter_plan_capture(
                    replace(state,
                            capture_replans_done=state.capture_replans_done + 1,
                            capture_exec_disabled_streak=0),
                    snapshot, peak, issue_plan=False)
                return entered, ([Action(ACTION_MJ_ABORT_AND_CLEAR_NODES)]
                                 + plan_actions)
            if verdict == CAPTURE_NOSTART_FLAKE:
                # LIVENESS: nothing left to try. Either the node's window is
                # gone and the re-plan budget is spent, or there is no node /
                # no readable node clock at all. Fast-fail with its own name
                # instead of idling to the (hours-long) burn budget.
                if (snapshot.node_count >= 1 and _is_finite(snapshot.node_ut)
                        and _is_finite(snapshot.ut)):
                    return _b5_named_flake(
                        state,
                        "phase %s: capture-window-missed (node UT %.3f passed "
                        "by %.0f s with the node still pending and the orbit "
                        "unchanged; replans=%d)"
                        % (B5_CAPTURE_BURN, snapshot.node_ut,
                           snapshot.ut - snapshot.node_ut,
                           state.capture_replans_done), peak), []
                return _b5_named_flake(
                    state,
                    "phase %s: capture-executor-no-start (the NodeExecutor "
                    "never began; orbit unchanged and static at 1x for %.0f s, "
                    "nodes=%d nodeUt=%s execEnabled=%d)"
                    % (B5_CAPTURE_BURN, state.params.burn_nostart_seconds,
                       snapshot.node_count, _obs_fmt(snapshot.node_ut),
                       snapshot.node_executor_enabled), peak), []
            # CAPTURE_NOSTART_HOLD: MechJeb's own pre-ignition hold. Fall
            # through to the ordinary stay/budget path -- the phase budget
            # still bounds it, and the grace expiry re-raises this branch.
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return _b5_named_flake(
                stayed,
                "phase %s: the capture burn did not complete inside its %.0f "
                "game-second budget" % (B5_CAPTURE_BURN,
                                        state.params.capture_burn_timeout)), []
        # Flameout staging AFTER the exit/flake checks (same delta-review A1/A3
        # ordering as the other burns): a dry stage under a commanded burn pops
        # ONE stage, bounded by the per-mission cap. mid_burn covers MechJeb's
        # throttle collapse on engine death (finding 17).
        mid_burn = burned and snapshot.node_count >= max(state.planned_node_count, 1)
        return _b5_flameout_stage(stayed, snapshot, mid_burn=mid_burn)

    if state.phase == B5_PARK:
        left = _b5_left_target_soi(state, snapshot)
        if left is not None:
            return replace(left, peak_apoapsis=peak), []
        stable = _b5_park_stable(state.params, snapshot)
        # CAPPED at the debounce depth (the aligned_streak / flameout_streak
        # discipline): every gate below tests only `>= park_debounce`, so the
        # cap is behaviour-identical, but park_stable_streak is a DIFFED field
        # and an uncapped counter emits one loud Info gate line plus a 21-line
        # window dump for EVERY frame of the whole 180 s dwell (~180-360 extra
        # lines per park).
        streak = (min(state.park_stable_streak + 1, state.params.park_debounce)
                  if stable else 0)
        st = replace(state, peak_apoapsis=peak, park_stable_streak=streak,
                     park_ever_stable=(state.park_ever_stable
                                       or streak >= state.params.park_debounce))
        dwelled = (_is_finite(snapshot.ut)
                   and (snapshot.ut - st.phase_entry_ut) >= state.params.park_dwell)
        if streak >= state.params.park_debounce and dwelled:
            # In-gate NOW, and the phase has been running for the whole dwell
            # -> commit the tree HERE, parked in the foreign SOI (the Parsek
            # surface the whole lane exists for).
            #
            # WORDING NOTE (reviewer finding): the dwell is measured from
            # phase_entry_ut, NOT from the first stable frame, so "park_dwell
            # seconds of instability followed by park_debounce stable frames"
            # also satisfies this. That is inherited VERBATIM from the
            # LIVE-PROVEN forge_lko FLKO_PARK gate and is deliberately NOT
            # changed here; the docstrings and the give-up text below say what
            # the code enforces rather than the stronger thing. Tracking a
            # `park_stable_since` stamp and measuring the dwell from IT is the
            # stronger contract if it is ever wanted -- it would have to land
            # on forge_lko at the same time, and be re-flown.
            #
            # LANDING FORK (B13/B14): the park dwell is IDENTICAL either way --
            # the same recorded parked-in-foreign-SOI coverage -- and only the
            # exit differs. With landingEnabled the tree is NOT committed here;
            # the craft descends and the commit happens on the SURFACE instead.
            # This is the ONLY door into the landing tail, which is why
            # landingEnabled without captureEnabled is inert by construction.
            if state.params.landing_enabled:
                return _b5_enter_descent(st, snapshot, peak)
            return (_b5_enter(st, B5_ORBIT_COMMIT, snapshot.ut, peak),
                    [Action(ACTION_PARSEK_COMMIT_TREE)])
        # PARK is the RECORDED in-foreign-SOI coverage this lane exists for, so
        # it deliberately runs at 1x: self-heal any warp the node executor (or a
        # leftover native warp) left running, on-change only -- a settled 1x park
        # emits nothing. This is NOT a coast phase, so the no-1x-coast invariant
        # (COAST-TO-TARGET / TARGET-FLYBY) does not apply.
        warp_actions: List[Action] = []
        if st.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
            st = replace(st, warp_to_cmd=None, warp_cmd=0)
            warp_actions.append(Action(ACTION_CANCEL_WARP))
        elif st.warp_cmd != 0 or snapshot.warp_mode == WARP_RAILS:
            st = replace(st, warp_cmd=0)
            warp_actions.append(Action(ACTION_SET_RAILS_WARP, 0.0))
        if _b5_over_budget(st, snapshot):
            # CARRY the teardown out with the give-up (2026-07-28 review). The
            # self-heal above already mutated `st` to say the warp is down, so
            # returning [] here would ship a state that LIES: a PARK that times
            # out on the same frame a stray warp is detected would flake with
            # the game still warping, and the runner drives the CLEANUP tail
            # (StopRecording / FlushAndQuit -- `hlib.plan_unmet_mission_tail`
            # skips the world-mutating verbs after an unmet mission) next --
            # exactly the "leave nothing warped behind" failure
            # `_b5_stop_all_warp` was added to close at the thrash and
            # never-armed terminals. warp_actions is [] on the settled-1x park
            # that this branch almost always fires on, so the common case is
            # unchanged.
            if st.park_ever_stable:
                return _b5_named_flake(
                    st,
                    "phase %s: the captured orbit reached the park gate at "
                    "least once but was not in-gate at the end of the %.0f s "
                    "dwell (the dwell is measured from phase entry, not from "
                    "first stability)"
                    % (B5_PARK, state.params.park_dwell)), warp_actions
            return _b5_named_flake(
                st,
                "phase %s: never reached a stable park (body=%s situation=%s "
                "ap=%.0f pe=%.0f ecc=%.3f angVel=%.4f)"
                % (B5_PARK, snapshot.body or "?", snapshot.situation or "?",
                   snapshot.apoapsis, snapshot.periapsis, snapshot.eccentricity,
                   snapshot.angular_velocity)), warp_actions
        return st, warp_actions

    if state.phase == B5_ORBIT_COMMIT:
        left = _b5_left_target_soi(state, snapshot)
        if left is not None:
            return replace(left, peak_apoapsis=peak), []
        result = snapshot.seam_commit_result
        if result == "OK":
            # TERMINAL: the tree is committed while the vessel is parked in the
            # foreign SOI. done, verdict None -- the assertions judge the state.
            return _b5_enter(replace(state, commit_result="OK"),
                             B5_ORBIT_COMMITTED, snapshot.ut, peak), []
        if result in ("ERROR", "TIMEOUT"):
            # LIVENESS: the seam answered a terminal token, so the actor is
            # provably done and wrong. Name the outcome instead of the generic
            # phase-timeout wording.
            return _b5_named_flake(
                replace(state, commit_result=result),
                "phase %s: tree-commit seam returned %s (the parked-in-%s-SOI "
                "commit did not happen)"
                % (B5_ORBIT_COMMIT, result, state.params.target_body), peak), []
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return _b5_named_flake(
                stayed,
                "phase %s: the tree-commit seam never answered inside its "
                "%.0f game-second budget"
                % (B5_ORBIT_COMMIT, state.params.commit_timeout)), []
        return stayed, []

    # ---- LANDING-mission tail (B13/B14): descend -> settle -> commit ---------
    # Reachable only with landingEnabled (which needs captureEnabled to reach
    # PARK at all). Every phase carries the in-SOI guard, and DESCENT carries
    # FOUR distinctly named give-ups so a dead actor is never allowed to idle to
    # a budget.

    if state.phase == B5_DESCENT:
        left = _b5_left_target_soi(state, snapshot)
        if left is not None:
            return replace(left, peak_apoapsis=peak), []
        # TOUCHDOWN FIRST, before any watchdog. Two reasons, both load-bearing:
        # MechJeb DISABLES its own landing module on the touchdown frame
        # (FinalDescent -> StopLanding), so an observed-enabled check evaluated
        # first would read a successful landing as a dead autopilot; and a
        # landed craft's altitude stops decreasing, which is the no-progress
        # signature. The exit is OBSERVED (kRPC situation), never inferred from
        # having commanded a landing.
        if _b5_touched_down(state.params, snapshot):
            return _b5_enter_landed_settle(state, snapshot, peak)
        # OBSERVED autopilot supervision. "We issued mj_land_untargeted" is a
        # COMMAND; this is the read-back, and it is the FASTEST signal available
        # (~3 polls, against ~900 game-s for the altitude watchdog and the whole
        # descent budget behind that).
        #
        # touched_down=False is a CONSTANT here, and correctly so: the branch
        # above already returned on every landed frame, so no other value is
        # reachable. That makes the classifier's own touchdown carve-out a
        # backstop for out-of-order callers rather than a live path -- THIS
        # ordering is what stops a perfect landing reading as a dead autopilot.
        # Do not reorder these two blocks; see classify_landing_autopilot.
        ap_verdict, ap_streak = classify_landing_autopilot(
            snapshot.landing_ap_enabled, state.landing_ap_down_streak,
            state.landing_ap_reissues, touched_down=False)
        state = replace(state, landing_ap_down_streak=ap_streak)
        if ap_verdict == LANDING_AP_DEAD:
            return _b5_named_flake(
                state,
                "phase %s: %s (MechJeb LandingAutopilot.Enabled read FALSE for "
                "%d consecutive frames after %d bounded re-issue(s) of "
                "mj_land_untargeted; status=%s alt=%s vspd=%s body=%s ut=%s). "
                "The descent was COMMANDED and never OBSERVED to engage."
                % (B5_DESCENT, LANDING_GIVEUP_AP_NOT_ENABLED, ap_streak,
                   state.landing_ap_reissues,
                   snapshot.landing_ap_status or "?",
                   _obs_fmt(snapshot.altitude),
                   _obs_fmt(snapshot.vertical_speed), snapshot.body or "?",
                   _obs_fmt(snapshot.ut)), peak), []
        if ap_verdict == LANDING_AP_REISSUE:
            # Re-hand the descent to MechJeb and RE-ANCHOR the no-progress
            # window, so the fresh attempt earns a full window rather than
            # inheriting the dead one's clock (the capture-executor re-issue
            # discipline, which re-stamps its own static clock for the same
            # reason).
            return (replace(state,
                            landing_ap_reissues=state.landing_ap_reissues + 1,
                            landing_alt_ref=(float(snapshot.altitude)
                                             if _is_finite(snapshot.altitude)
                                             else state.landing_alt_ref),
                            landing_alt_ref_ut=(float(snapshot.ut)
                                                if _is_finite(snapshot.ut)
                                                else state.landing_alt_ref_ut),
                            # The give-up debounce is re-anchored with the window
                            # for the same reason: the fresh attempt is judged on
                            # ITS OWN frames, not on the dead module's.
                            landing_stall_streak=0,
                            peak_apoapsis=peak),
                    [Action(ACTION_MJ_LAND_UNTARGETED,
                            landing_config=_b5_landing_config(state.params))])
        # NO-PROGRESS window. Lazily anchors on the first frame with a readable
        # clock (a DESCENT entered on a NaN ut would otherwise never arm it).
        if state.landing_alt_ref_ut is None and _is_finite(snapshot.ut):
            state = replace(state,
                            landing_alt_ref_ut=float(snapshot.ut),
                            landing_alt_ref=(float(snapshot.altitude)
                                             if _is_finite(snapshot.altitude)
                                             else None))
        # The ALTITUDE half heals SEPARATELY (reviewer finding, 2026-07-26):
        # gating the anchor on the UT alone left a DESCENT entered on ONE
        # non-finite altitude frame with landing_alt_ref None FOREVER -- and a
        # None ref reads BLIND, so the phase flaked `altitude-unreadable` a full
        # window later on a channel that had recovered on frame two.
        # DELIBERATELY does NOT re-stamp landing_alt_ref_ut: re-stamping the
        # clock every frame the altitude is unreadable would hold `elapsed` at
        # ~0 forever and make the NAMED altitude-unreadable give-up unreachable
        # on a PERMANENTLY dark channel, which is the exact fail-closed property
        # the BLIND verdict exists to provide.
        elif state.landing_alt_ref is None and _is_finite(snapshot.altitude):
            state = replace(state, landing_alt_ref=float(snapshot.altitude))
        elapsed = (snapshot.ut - state.landing_alt_ref_ut
                   if (state.landing_alt_ref_ut is not None
                       and _is_finite(snapshot.ut)) else float("nan"))
        progress = landing_progress_verdict(
            snapshot.altitude, state.landing_alt_ref, elapsed,
            state.params.landing_progress_window,
            state.params.landing_progress_min_drop,
            snapshot.vertical_speed)
        if progress == LANDING_PROGRESS_OK:
            state = replace(state, landing_alt_ref=float(snapshot.altitude),
                            landing_alt_ref_ut=float(snapshot.ut),
                            landing_stall_streak=0)
        elif progress == LANDING_PROGRESS_UNSATISFIABLE:
            # DISARMED: the anchor is below min_drop AGL, so the window asks for
            # a drop that does not exist below the craft. HOLD and DO NOT
            # re-anchor (re-anchoring would restart a clock that decides
            # nothing); descentTimeoutSeconds owns the phase from here.
            state = replace(state,
                            landing_unsat_holds=state.landing_unsat_holds + 1,
                            landing_stall_streak=0)
        elif progress == LANDING_PROGRESS_VSPEED:
            # HOLD, and DO NOT re-anchor: the accumulated drop keeps counting
            # toward the SAME anchor, so the window still resolves OK the moment
            # the craft has actually shed min_drop. Only the counter moves.
            state = replace(state,
                            landing_vspeed_holds=state.landing_vspeed_holds + 1,
                            landing_stall_streak=0)
        elif progress in (LANDING_STALL_FLAT, LANDING_STALL_BLIND):
            # DEBOUNCED like every other liveness gate here. One frame is not a
            # sustained observation: MechJeb's final descent measurably hops (see
            # LANDING_STALL_DEBOUNCE_FRAMES for the flight frames), and a single
            # unreadable altitude sample is a blip, not a dark channel.
            stall = state.landing_stall_streak + 1
            state = replace(state, landing_stall_streak=stall)
            if stall >= LANDING_STALL_DEBOUNCE_FRAMES:
                return _b5_named_flake(
                    state,
                    "phase %s: %s (%s on %d consecutive frames) -- surface "
                    "altitude went %s -> %s over %.0f game seconds, less than "
                    "the %.0f m the window requires from an anchor high enough "
                    "to deliver it, and the independent vertical-speed channel "
                    "did not show a descent either (apEnabled=%d status=%s "
                    "vspd=%s thr=%s ut=%s vspeedHolds=%d unsatHolds=%d)"
                    % (B5_DESCENT, LANDING_GIVEUP_NO_PROGRESS, progress, stall,
                       _obs_fmt(state.landing_alt_ref),
                       _obs_fmt(snapshot.altitude),
                       elapsed, state.params.landing_progress_min_drop,
                       snapshot.landing_ap_enabled,
                       snapshot.landing_ap_status or "?",
                       _obs_fmt(snapshot.vertical_speed),
                       _obs_fmt(snapshot.throttle),
                       _obs_fmt(snapshot.ut), state.landing_vspeed_holds,
                       state.landing_unsat_holds), peak), []
        else:
            # PENDING (window not elapsed, or an unreadable clock). The streak is
            # a CONSECUTIVE-frame claim, so anything that is not proof resets it.
            state = replace(state, landing_stall_streak=0)
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return _b5_named_flake(
                stayed,
                "phase %s: %s (the craft never reached an accepted landed "
                "situation inside the %.0f game-second descent budget; "
                "alt=%s vspd=%s situation=%s apEnabled=%d status=%s)"
                % (B5_DESCENT, LANDING_GIVEUP_TOUCHDOWN_TIMEOUT,
                   state.params.descent_timeout, _obs_fmt(snapshot.altitude),
                   _obs_fmt(snapshot.vertical_speed), snapshot.situation or "?",
                   snapshot.landing_ap_enabled,
                   snapshot.landing_ap_status or "?")), []
        # NO warp actions and NO attitude actions: MechJeb owns both here (see
        # _b5_descent_entry_actions). A second writer on either is the thrash
        # class this suite has already paid for twice.
        return stayed, []

    if state.phase == B5_LANDED_SETTLE:
        left = _b5_left_target_soi(state, snapshot)
        if left is not None:
            return replace(left, peak_apoapsis=peak), []
        stable = landed_stable(state.params, snapshot)
        # CAPPED at the debounce depth, for the PARK reason: the streak is a
        # DIFFED field, and an uncapped counter emits one Info gate line plus a
        # window dump for EVERY frame of the whole dwell.
        streak = (min(state.landed_stable_streak + 1, state.params.landed_debounce)
                  if stable else 0)
        st = replace(state, peak_apoapsis=peak, landed_stable_streak=streak,
                     landed_ever_stable=(state.landed_ever_stable
                                         or streak >= state.params.landed_debounce))
        dwelled = (_is_finite(snapshot.ut)
                   and (snapshot.ut - st.phase_entry_ut) >= state.params.landed_dwell)
        if streak >= state.params.landed_debounce and dwelled:
            # Settled NOW, and the phase has been running for the whole dwell
            # -> commit the tree HERE, landed on the foreign body (the Parsek
            # surface the whole lane exists for). Same WORDING CAVEAT as PARK:
            # the dwell is measured from phase_entry_ut, NOT from the first
            # settled frame, so "landed_dwell seconds of bouncing followed by
            # landed_debounce settled frames" also satisfies it. Inherited
            # verbatim from the LIVE-PROVEN PARK / forge_lko gate and
            # deliberately NOT strengthened here in isolation.
            return (_b5_enter(st, B5_SURFACE_COMMIT, snapshot.ut, peak),
                    [Action(ACTION_PARSEK_COMMIT_TREE)])
        # The landed dwell IS the recorded coverage, so it runs at 1x: self-heal
        # any warp MechJeb's landing states left running, on-change only (a
        # settled 1x dwell emits nothing). NOT a coast phase, so the
        # no-1x-coast invariant does not apply -- exactly as for PARK.
        warp_actions: List[Action] = []
        if st.warp_to_cmd is not None or _is_finite(snapshot.warping_to):
            st = replace(st, warp_to_cmd=None, warp_cmd=0)
            warp_actions.append(Action(ACTION_CANCEL_WARP))
        elif st.warp_cmd != 0 or snapshot.warp_mode == WARP_RAILS:
            st = replace(st, warp_cmd=0)
            warp_actions.append(Action(ACTION_SET_RAILS_WARP, 0.0))
        if _b5_over_budget(st, snapshot):
            # CARRY the teardown out with the give-up, for the same reason PARK
            # does (2026-07-28 review): this branch is a verbatim copy of PARK's
            # and inherited its bug. Returning [] here ships a state that has
            # already recorded warp_to_cmd=None / warp_cmd=0 while no cancel
            # ever reaches the runner, which then drives the CLEANUP tail
            # (StopRecording / FlushAndQuit) against a warping game.
            if st.landed_ever_stable:
                return _b5_named_flake(
                    st,
                    "phase %s: %s -- the lander reached the settled gate at "
                    "least once but was not in-gate at the end of the %.0f s "
                    "dwell (the dwell is measured from phase entry, not from "
                    "first stability; body=%s situation=%s vspd=%s hspd=%s)"
                    % (B5_LANDED_SETTLE, LANDING_GIVEUP_NEVER_STABLE,
                       state.params.landed_dwell, snapshot.body or "?",
                       snapshot.situation or "?",
                       _obs_fmt(snapshot.vertical_speed),
                       _obs_fmt(snapshot.horizontal_speed))), warp_actions
            return _b5_named_flake(
                st,
                "phase %s: %s -- never settled after touchdown (body=%s "
                "situation=%s vspd=%s hspd=%s, floors vspd<=%.2f hspd<=%.2f)"
                % (B5_LANDED_SETTLE, LANDING_GIVEUP_NEVER_STABLE,
                   snapshot.body or "?", snapshot.situation or "?",
                   _obs_fmt(snapshot.vertical_speed),
                   _obs_fmt(snapshot.horizontal_speed),
                   state.params.landed_max_vertical_speed,
                   state.params.landed_max_horizontal_speed)), warp_actions
        return st, warp_actions

    if state.phase == B5_SURFACE_COMMIT:
        left = _b5_left_target_soi(state, snapshot)
        if left is not None:
            return replace(left, peak_apoapsis=peak), []
        result = snapshot.seam_commit_result
        if result == "OK":
            # TERMINAL: the tree is committed while the vessel is LANDED ON
            # ANOTHER BODY. done, verdict None -- the assertions judge the state.
            return _b5_enter(replace(state, commit_result="OK"),
                             B5_SURFACE_COMMITTED, snapshot.ut, peak), []
        if result in ("ERROR", "TIMEOUT"):
            return _b5_named_flake(
                replace(state, commit_result=result),
                "phase %s: tree-commit seam returned %s (the landed-on-%s "
                "commit did not happen)"
                % (B5_SURFACE_COMMIT, result, state.params.target_body),
                peak), []
        stayed = _b5_stay_or_flake(state, snapshot, peak)
        if stayed.done:
            return _b5_named_flake(
                stayed,
                "phase %s: the tree-commit seam never answered inside its "
                "%.0f game-second budget"
                % (B5_SURFACE_COMMIT, state.params.commit_timeout)), []
        return stayed, []

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
# Distance to the TARGET docking port below which a `Docked` read is corroborated
# as "docked to the TARGET" rather than "some pair on this craft reads docked"
# (MINOR-5). _read_docking_state returns Docked when ANY port on the active vessel
# reads docked, so a craft carrying an already-mated internal pair would otherwise
# satisfy the DOCK short-circuit on its first poll without ever meeting the
# Station. 10 m is orders of magnitude below any approach distance (DOCK is entered
# from the match-velocity hold, tens to hundreds of metres out) and comfortably
# above the port-to-port reading of a genuinely mated pair.
BDOCK_DOCKED_TARGET_DIST_EPS = 10.0   # metres
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
    # Was the craft ALREADY reading Docked on the poll that entered BDOCK_DOCK
    # (MINOR-5)? _read_docking_state answers "any port on the active vessel reads
    # docked", so a craft with an already-mated internal pair reads Docked from the
    # first poll and would complete the phase without ever docking to the Station.
    # A docked read that APPEARS during the phase is a transition and is therefore
    # self-corroborating; a docked read that was true at entry needs other evidence
    # (the AP having run, or the target port being ~0 m away).
    dock_entry_docked: bool = False
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
                                     dock_last_progress_ut=snapshot.ut,
                                     dock_entry_docked=(
                                         snapshot.docking_state == DOCKING_STATE_DOCKED)),
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
        #
        # CORROBORATION (MINOR-5). `docked` alone is NOT sufficient evidence that we
        # docked to the TARGET: _read_docking_state answers "ANY port on the active
        # vessel reads docked", so a craft carrying an already-mated internal pair
        # reads Docked on its first DOCK poll and would complete BDOCK_DOCK instantly
        # without ever meeting the Station. (Not reachable with the single-port
        # Kerbal X these missions fly - the dropped `docking_ever_enabled` conjunct
        # was guarding it incidentally - but the phase must not depend on the craft.)
        # Either of these corroborates, so every live-proven completion path is
        # unchanged:
        #   - the craft was NOT docked at phase entry, so this read is a TRANSITION
        #     observed during the approach. This covers the flight-9 race the
        #     short-circuit was written for (the pair mates before the deferred
        #     enable fires, so docking_ever_enabled is still False) and every
        #     ordinary completion;
        #   - the TARGET port is within BDOCK_DOCKED_TARGET_DIST_EPS, which is what
        #     rescues a hard dock that landed on the very poll that entered the
        #     phase (docked at entry, but demonstrably docked to the TARGET).
        # Note `docking_ever_enabled` is deliberately NOT a disjunct here: enabling
        # the AP latches it after one poll whether or not the AP achieved anything,
        # so an entry-docked craft would false-complete two polls later - exactly
        # the hole being closed. Uncorroborated falls through to the normal flow:
        # the AP is enabled and has to actually close on the target, and the
        # progress watchdog / budget flake it with a named reason. A false PASS is
        # the one outcome that must not be possible.
        docked_corroborated = (
            not st.dock_entry_docked
            or (_is_finite(snapshot.target_distance)
                and snapshot.target_distance <= BDOCK_DOCKED_TARGET_DIST_EPS))
        if docked and docked_corroborated:
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
        # watchdog -- we have just (re-)issued the enable. (Not reached on a
        # CORROBORATED docked read: the short-circuit above already completed the
        # phase. An UNCORROBORATED one - already docked at entry, target still far
        # away - deliberately DOES reach here, so the AP has to actually close on
        # the target instead of the phase completing on a stale internal mate.)
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
            # it. (A CORROBORATED docked read already completed above, so this
            # branch runs on a non-docked - or uncorroborated-docked - AP-running
            # poll; either way, running the tanks dry is a real give-up.)
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
        # CAPPED at the debounce depth: park_stable_streak is a DIFFED field
        # and every gate below tests only `>= park_debounce`, so the cap is
        # behaviour-identical while removing one gate line + one 21-line window
        # dump per dwell frame. Same change as the B5 PARK branch.
        streak = (min(state.park_stable_streak + 1, p.park_debounce)
                  if stable else 0)
        st = replace(state, park_stable_streak=streak,
                     park_ever_stable=(state.park_ever_stable
                                       or streak >= p.park_debounce))
        # NOTE (same wording caveat as B5 PARK): the dwell runs from
        # phase_entry_ut, not from the first stable frame, so this gate means
        # "in-gate now, and the phase has been running for the dwell", NOT
        # "in-gate continuously through the dwell". LIVE-PROVEN as written --
        # do not tighten it without a re-fly.
        dwelled = (_is_finite(snapshot.ut)
                   and (snapshot.ut - st.phase_entry_ut) >= p.park_dwell)
        if streak >= p.park_debounce and dwelled:
            return _flko_enter(st, FLKO_ORBIT, snapshot.ut), []
        if _flko_over_budget(st, snapshot):
            if st.park_ever_stable:
                return _flko_flake(st, (
                    "phase %s: the orbit reached the park gate at least once but "
                    "was not in-gate at the end of the %.0f s dwell (the dwell is "
                    "measured from phase entry, not from first stability)"
                    % (FLKO_PARK, p.park_dwell))), []
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
                           down_terminal: bool = False,
                           craft_canopy_observed: bool = False,
                           arm_altitude: Optional[float] = None,
                           arm_rate: Optional[float] = None,
                           arm_commanded: bool = False) -> List[AssertionOutcome]:
    """Evaluate the three B1 driver-validity assertions over the flight frames.

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
      decision 2026-07-20 option A: a canopy-borne impact IS the craft reaching
      the ground; the destroyed craft's final frames carry no landed situation to
      read). The DOWN end is named in the outcome's value
      ("DOWN(canopy-borne impact)" vs the raw situation string) and flagged in
      the detail (``downTerminal``) so the result JSON says which end it was.
      (A situation is a discrete kRPC enum, not a noisy float, so it is read from
      the last frame directly rather than debounced.)
    - ``craftCanopyObserved``: the craft's parachute READ Deployed on
      B1_CANOPY_DEBOUNCE_K consecutive live DESCENT frames (``craft_canopy_observed``,
      the machine's sticky OBSERVED latch over TelemetrySnapshot.craft_chute_state;
      DESCENT-scoped and debounced so no stray read outside the descent, and no lone
      glitched frame, can earn it). ``arm_altitude`` / ``arm_rate`` / ``arm_commanded``
      carry the COMMANDED half into the row's value and detail, so the result JSON
      holds both halves of the distinction. This is the assertion that makes B1's
      claimed Parsek surface -- a chute-borne ground arrival, with the two-phase
      ParachuteSemiDeployed / ParachuteDeployed part events in the recording -- an
      OBSERVED fact rather than an assumed one. It is deliberately independent of the
      terminal: LANDED and DOWN both have to satisfy it, so neither end can be
      reached by a craft whose canopy never opened. The evidence is machine-carried
      rather than re-derived from frames because the latch is sticky (a chute cut or
      destroyed on the final frame must not erase a descent that really happened).

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
        sit = AssertionOutcome("landedSituation", True, "DOWN(canopy-borne impact)",
                               {"accepted": list(params.landed_situations),
                                "downTerminal": True})
    else:
        final_situation = frames[-1].situation if frames else None
        sit_met = final_situation in params.landed_situations
        sit = AssertionOutcome("landedSituation", sit_met, final_situation,
                               {"accepted": list(params.landed_situations),
                                "downTerminal": False})

    # value = the altitude the arm was COMMANDED at, and the detail carries the rate
    # and the two knobs, so the result JSON holds BOTH halves of the
    # commanded-vs-observed distinction: where we acted, and whether KSP complied.
    # (Mirrors evaluate_eva4_assertions. Before the 2026-07-25 review these two fields
    # were written into B1State on every arm and read by nothing, while their comment
    # claimed they were "carried into the result".)
    # ``value`` is the altitude the arm was COMMANDED at, falling back to NaN (never a
    # bool) so the column stays float-or-null across runs, matching
    # evaluate_eva4_assertions. ``armCommanded`` reads the machine's real latch, NOT
    # "did we capture an arm altitude" - an arm on a frame with a non-finite altitude
    # leaves arm_altitude None while the deploy actions genuinely were emitted, and
    # reporting armCommanded=false beside a finite armCommandedRate would be a
    # commanded-vs-observed inversion in the very PR about that distinction.
    canopy = AssertionOutcome(
        "craftCanopyObserved", bool(craft_canopy_observed),
        arm_altitude if arm_altitude is not None else float("nan"),
        {"required": CHUTE_STATE_DEPLOYED,
         "debounceK": B1_CANOPY_DEBOUNCE_K,
         "armCommanded": bool(arm_commanded),
         "armCommandedRate": arm_rate,
         "armMaxRate": params.chute_arm_max_rate,
         "fullDeployAltitude": params.chute_full_deploy_alt})
    return [apo, sit, canopy]


def evaluate_eva4_assertions(frames, params: Eva4Params,
                             state=None) -> List[AssertionOutcome]:
    """Evaluate the four EVA-4 driver-validity assertions. All four are about the
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
                           k: int = DEFAULT_DEBOUNCE_K,
                           state=None) -> List[AssertionOutcome]:
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

    ORBIT MODE (``params.capture_enabled``, missions b11_mun_orbit /
    b12_minmus_orbit): ``returnedToHome`` is REPLACED (the mission must NOT
    return -- leaving the target SOI is the failure) by three carried-evidence
    rows. ``flybyPeriapsisFloor`` is retained and now certifies the whole
    in-SOI stay, PARK orbit included.

    - ``capturedInTargetOrbit``: PARK was entered AND the orbit read at PARK
      entry is BOUND inside the capture window (0 < ap <= parkMaxApoapsisMeters,
      pe >= parkMinPeriapsisMeters, ecc <= parkMaxEccentricity). A hyperbolic
      (uncaptured) approach reads a NEGATIVE apoapsis, so this cannot be
      satisfied by flying past.
    - ``parkedStable``:         ORBIT-COMMIT was entered AND the park was ever
      in-gate. What entering ORBIT-COMMIT actually proves is that the craft was
      IN-GATE (park_debounce consecutive frames) at a moment at least
      park_dwell game-seconds after PARK ENTRY -- NOT that it held the gate
      continuously across the dwell (the dwell clock runs from phase entry).
      The gate is inherited verbatim from the LIVE-PROVEN forge_lko park; see
      the note at the B5_PARK branch for the stronger `park_stable_since`
      contract if it is ever wanted.
    - ``treeCommitted``:        ORBIT-COMMITTED was entered AND the seam
      answered OK -- the tree was committed while the vessel was parked in the
      FOREIGN SOI, the Parsek surface the lane exists for.

    LANDING MODE (``params.landing_enabled``, missions b13_mun_landing /
    b14_minmus_landing; it implies capture mode). ``capturedInTargetOrbit`` is
    unchanged, ``parkedStable`` re-points its required phase from ORBIT-COMMIT
    to DESCENT (the ORBIT terminal is never entered), and TWO rows are ADDED
    before ``treeCommitted``, which now requires SURFACE-COMMITTED:

    - ``landedOnTargetBody``: LANDED-SETTLE was entered AND the OBSERVED
      touchdown situation is one of ``landedSituations`` AND the OBSERVED body
      IS the target. This is the row that makes "landed on ANOTHER BODY" a
      checked claim rather than an inference from the phase list.
    - ``landedStable``:       SURFACE-COMMIT was entered AND the landing was
      EVER in the settled gate (target body + landed situation + BOTH speed
      components under their floors).

    ``state`` is the terminated machine state; the capture rows are carried
    evidence the frames cannot hold (this evaluator discards them). Absent /
    None it degrades to the flyby rows.

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

    if params.capture_enabled and params.landing_enabled:
        # LANDING MODE (b13_mun_landing / b14_minmus_landing). Inherits the four
        # ORBIT rows through PARK, then REPLACES the orbit terminal rows with
        # the three that judge the LANDING. Every one is carried machine
        # evidence: this evaluator discards the frames.
        #
        # THE EVA-4 LESSON, applied: a mission must not be able to report
        # MISSION-OK while its actual objective failed. Walk the failure modes:
        #   crashed          -> the vessel-lost / frozen terminals fire first and
        #                       resolve_flight_verdict returns loss_reason BEFORE
        #                       these rows are even consulted (ASSERT-FAIL).
        #   never descended  -> DESCENT's named give-ups (FLAKE), and if the
        #                       machine somehow ended without LANDED-SETTLE,
        #                       landedOnTargetBody is unmet (ASSERT-FAIL).
        #   landed elsewhere -> landedOnTargetBody's body conjunct is unmet.
        #   landed, tumbled  -> landedStable is unmet (SURFACE-COMMIT is never
        #                       entered, and landed_ever_stable stays False).
        #   commit refused   -> treeCommitted is unmet.
        # There is no path on which all eight rows are met and the craft is not
        # sitting intact, settled, on the target body, with its tree committed.
        cap_ap = getattr(state, "capture_apoapsis", None)
        cap_pe = getattr(state, "capture_periapsis", None)
        cap_ecc = getattr(state, "capture_eccentricity", None)
        cap_met = (B5_PARK in phases
                   and cap_ap is not None and cap_pe is not None
                   and cap_ecc is not None
                   and 0.0 < cap_ap <= params.park_max_apoapsis
                   and cap_pe >= params.park_min_periapsis
                   and cap_ecc <= params.park_max_eccentricity)
        captured = AssertionOutcome(
            "capturedInTargetOrbit", cap_met, cap_ecc,
            {"required": B5_PARK, "body": params.target_body,
             "apoapsis": cap_ap, "periapsis": cap_pe,
             "maxApoapsis": params.park_max_apoapsis,
             "minPeriapsis": params.park_min_periapsis,
             "maxEccentricity": params.park_max_eccentricity})

        # The park dwell completed: in LANDING mode the phase that proves it is
        # DESCENT (the ORBIT lane's ORBIT-COMMIT is never entered).
        parked_met = (B5_DESCENT in phases
                      and bool(getattr(state, "park_ever_stable", False)))
        parked = AssertionOutcome(
            "parkedStable", parked_met,
            (B5_DESCENT if B5_DESCENT in phases
             else (phases[-1] if phases else None)),
            {"required": B5_DESCENT, "dwellSeconds": params.park_dwell,
             "debounceFrames": params.park_debounce,
             "everStable": bool(getattr(state, "park_ever_stable", False))})

        landed_body = str(getattr(state, "landed_body", "") or "")
        landed_situation = str(getattr(state, "landed_situation", "") or "")
        # OBSERVED situation AND OBSERVED body, both read off the touchdown
        # frame. SPLASHED is accepted through params.landed_situations rather
        # than excluded here: Mun and Minmus have no ocean, but hard-coding that
        # away would make the row wrong for any body that does.
        landed_met = (B5_LANDED_SETTLE in phases
                      and landed_situation in params.landed_situations
                      and landed_body == params.target_body)
        on_body = AssertionOutcome(
            "landedOnTargetBody", landed_met, (landed_body or None),
            {"required": B5_LANDED_SETTLE, "body": params.target_body,
             "situation": (landed_situation or None),
             "acceptedSituations": list(params.landed_situations)})

        # SURFACE-COMMIT entered proves the settled gate was met at a moment at
        # least landedDwellSeconds after touchdown; landed_ever_stable proves
        # the gate was met at all. Same phase-entry-clock caveat as parkedStable.
        # The second conjunct IS redundant today (reviewer NIT, 2026-07-26):
        # SURFACE-COMMIT is only reachable through the settled-dwell exit, which
        # sets landed_ever_stable. It is KEPT deliberately -- the redundancy
        # costs one boolean read and it is the only thing standing between this
        # row and a future refactor that adds a second edge into SURFACE-COMMIT
        # (a recovery path, an operator override) without noticing that this
        # assertion silently stopped proving the landing was ever settled.
        stable_met = (B5_SURFACE_COMMIT in phases
                      and bool(getattr(state, "landed_ever_stable", False)))
        landed_v = getattr(state, "landed_vertical_speed", None)
        landed_h = getattr(state, "landed_horizontal_speed", None)
        settled = AssertionOutcome(
            "landedStable", stable_met,
            (B5_SURFACE_COMMIT if B5_SURFACE_COMMIT in phases
             else (phases[-1] if phases else None)),
            {"required": B5_SURFACE_COMMIT, "dwellSeconds": params.landed_dwell,
             "debounceFrames": params.landed_debounce,
             "everStable": bool(getattr(state, "landed_ever_stable", False)),
             "touchdownVerticalSpeed": landed_v,
             "touchdownHorizontalSpeed": landed_h,
             "maxVerticalSpeed": params.landed_max_vertical_speed,
             "maxHorizontalSpeed": params.landed_max_horizontal_speed})

        commit_result = str(getattr(state, "commit_result", "") or "")
        commit_met = (B5_SURFACE_COMMITTED in phases) and commit_result == "OK"
        committed = AssertionOutcome(
            "treeCommitted", commit_met, (commit_result or None),
            {"required": B5_SURFACE_COMMITTED, "body": params.target_body,
             "terminal": "landed"})

        return [orbit, soi, flyby, captured, parked, on_body, settled, committed]

    if params.capture_enabled:
        cap_ap = getattr(state, "capture_apoapsis", None)
        cap_pe = getattr(state, "capture_periapsis", None)
        cap_ecc = getattr(state, "capture_eccentricity", None)
        cap_met = (B5_PARK in phases
                   and cap_ap is not None and cap_pe is not None
                   and cap_ecc is not None
                   and 0.0 < cap_ap <= params.park_max_apoapsis
                   and cap_pe >= params.park_min_periapsis
                   and cap_ecc <= params.park_max_eccentricity)
        # Carried readings ride the row as None (never NaN) when absent:
        # AssertionOutcome.to_dict scrubs a non-finite VALUE but NOT the detail
        # dict, and serialize_mission_result renders with allow_nan=False.
        captured = AssertionOutcome(
            "capturedInTargetOrbit", cap_met, cap_ecc,
            {"required": B5_PARK, "body": params.target_body,
             "apoapsis": cap_ap, "periapsis": cap_pe,
             "maxApoapsis": params.park_max_apoapsis,
             "minPeriapsis": params.park_min_periapsis,
             "maxEccentricity": params.park_max_eccentricity})

        parked_met = (B5_ORBIT_COMMIT in phases
                      and bool(getattr(state, "park_ever_stable", False)))
        parked = AssertionOutcome(
            "parkedStable", parked_met,
            (B5_ORBIT_COMMIT if B5_ORBIT_COMMIT in phases
             else (phases[-1] if phases else None)),
            {"required": B5_ORBIT_COMMIT, "dwellSeconds": params.park_dwell,
             "debounceFrames": params.park_debounce,
             "everStable": bool(getattr(state, "park_ever_stable", False))})

        commit_result = str(getattr(state, "commit_result", "") or "")
        commit_met = (B5_ORBIT_COMMITTED in phases) and commit_result == "OK"
        committed = AssertionOutcome(
            "treeCommitted", commit_met, (commit_result or None),
            {"required": B5_ORBIT_COMMITTED, "body": params.target_body})

        return [orbit, soi, flyby, captured, parked, committed]

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


def is_warp_arming_command(kind: str, value) -> bool:
    """True when a warp action ARMS warp, as opposed to cancelling it.

    The distinction the LOW-throughput marker needs (2026-07-26 review,
    MINOR-5). ``warp_utilisation_row``'s ``warpCommands`` counts every warp
    action the phase issued, which conflates "this phase tried to warp and got
    nothing" (the defect) with "this phase deliberately ran at 1x and said so
    once" (PARK's single ``set_rails_warp value=0.000``, a cancel to 1x).

    MEASURED over the archive: every FALSE POSITIVE of the ratio-only marker
    issued ZERO arming commands (B11 MJ-ASCENT 0, TRANSFER-BURN 0, CAPTURE-BURN
    0; PARK's one command is the 1x cancel), while the B12 flight-2 thrash the
    marker exists for issued thousands.

    ``ACTION_WARP_TO_UT`` always arms. ``ACTION_SET_RAILS_WARP`` arms only for a
    factor index > 0 (index 0 IS 1x, i.e. a cancel). ``ACTION_CANCEL_WARP``
    never arms. Pure.
    """
    if kind == ACTION_WARP_TO_UT:
        return True
    if kind == ACTION_SET_RAILS_WARP:
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return False
        return _is_finite(value) and float(value) > 0.0
    return False


def warp_utilisation_row(phase: str, wall_seconds: float, game_seconds: float,
                         warp_commands: int,
                         armed_warp_commands: int = 0) -> Dict:
    """One per-phase WARP-UTILISATION row for the mission result (B12 flight 2
    follow-up; the queued mission time-accounting task owns the full version).

    ``gameSecondsPerWallSecond`` is THE number: a phase that is genuinely
    warping reads hundreds-to-thousands, a 1x phase reads ~1, and B12 flight
    2's thrashing coast read ~1.3 while issuing 3,603 warp commands. That one
    line would have named the defect without any log archaeology. Pure; a
    non-positive or non-finite wall span yields a None ratio rather than a
    divide-by-zero.

    ``armedWarpCommands`` is the subset that actually ARMED warp (see
    ``is_warp_arming_command``); it is what separates "tried to warp and got
    nothing" from "deliberately ran at 1x".
    """
    ratio: Optional[float] = None
    if (_is_finite(wall_seconds) and wall_seconds > 0.0
            and _is_finite(game_seconds)):
        ratio = round(game_seconds / wall_seconds, 3)
    return {
        "phase": phase,
        "wallSeconds": round(float(wall_seconds), 3) if _is_finite(wall_seconds) else None,
        "gameSeconds": round(float(game_seconds), 3) if _is_finite(game_seconds) else None,
        "gameSecondsPerWallSecond": ratio,
        "warpCommands": int(warp_commands),
        "armedWarpCommands": int(armed_warp_commands),
    }


def gate_flip_novelty_keys(phase: str, gate_changes: Sequence[str]) -> List[str]:
    """The ``phase|field`` novelty keys one frame's gate flips carry.

    A ``diff_machine_state`` entry is ``"<key> <old>-><new>"``, so the FIELD is
    the token before the first space. MEASURED on the 43 MB pathological log:
    7,218 gate-flip dumps but only SIXTEEN distinct ``(phase, field)`` pairs in
    the entire run (7,207 dumps came from ``warpToCmd`` alone). Admitting the
    FIRST occurrence of each pair unconditionally therefore emits 16 windows
    instead of 7,218 -- a bigger reduction than the time rule alone -- while
    guaranteeing that no NOVEL flip ever loses its 20-frame context, which the
    time rule cannot promise (a suppressed flip followed by >10 s of quiet loses
    its window permanently). Pure.
    """
    keys: List[str] = []
    for change in gate_changes or ():
        field = str(change).split(" ", 1)[0]
        if not field:
            continue
        key = "%s|%s" % (phase, field)
        if key not in keys:
            keys.append(key)
    return keys


def should_dump_gate_flip_window(now: float, last_dump: Optional[float],
                                 interval: float, first_seen: bool = False) -> bool:
    """Admit a gate-flip event-window dump: unconditionally on the FIRST
    occurrence of a ``(phase, gate-field)`` pair, then at most one per
    ``interval`` WALL seconds for repeats (measured amplifier, 2026-07-25;
    novelty rule added by the 2026-07-26 review, MINOR-7).

    MEASURED: results/2026-07-25_0103_B12-minmus-orbit_mission.stdout.log is
    43 MB / 181,786 lines, of which 144,561 (79.5%) are ``window[NN/20]``
    payload from 7,218 gate-flip dumps -- 7,207 of them triggered by the single
    field ``gate warpToCmd`` while the coast thrashed its own warp. Even a
    HEALTHY B5 run spends 721 of 1,431 lines (50%) on window payload carrying
    only 209 unique frames (71% duplication), because consecutive dumps re-emit
    an overlapping slice of the same ring.

    ``first_seen`` is the caller's answer to "is any of this frame's flipped
    (phase, field) pairs new?" -- see ``gate_flip_novelty_keys``. It wins over
    the time limit so a brand-new gate can never be silently suppressed by an
    unrelated flip that happened 3 seconds earlier.

    Only ``gate-flip`` is rate-limited. ``phase-transition``, ``terminal-*``,
    ``vessel-lost`` and the give-up dumps stay unconditional: those are sparse
    by construction and are the ones a post-mortem actually needs. The ``gate
    warpToCmd`` line ITSELF is untouched (it was informative in replay); it is
    the 20-line dump behind it that is the amplifier.

    Pure: ``last_dump`` None (no dump yet this flight) always admits.
    """
    if first_seen:
        return True
    if last_dump is None:
        return True
    if not (_is_finite(now) and _is_finite(last_dump) and _is_finite(interval)):
        return True
    return (now - last_dump) >= interval


def wall_budget_block(now: float, deadline: Optional[float],
                      wall_budget: Optional[float]) -> Dict:
    """The live WALL accounting for the status payload (audit finding G1).

    Every phase budget in this system is GAME time; there was no WALL budget
    anywhere in the live surface. A B12 run consequently died on
    ``mission-budget-expired`` after burning 57% of its wall budget in ONE
    phase while every displayed budget read ~7.5% consumed. The fly loop
    already holds both numbers (``deadline`` and the ``--budget`` it was spawned
    with); this just shapes them.

    Returns ``{wallElapsedSeconds, wallRemainingSeconds, wallBudgetSeconds}``
    with None for anything not derivable (no deadline / no budget). Pure.
    """
    remaining: Optional[float] = None
    elapsed: Optional[float] = None
    budget: Optional[float] = None
    if _is_finite(wall_budget):
        budget = round(float(wall_budget), 3)
    if _is_finite(now) and _is_finite(deadline):
        remaining = round(float(deadline) - float(now), 3)
        if budget is not None:
            # wall_start = deadline - budget; elapsed = now - wall_start.
            elapsed = round(float(budget) - (float(deadline) - float(now)), 3)
    return {"wallElapsedSeconds": elapsed,
            "wallRemainingSeconds": remaining,
            "wallBudgetSeconds": budget}


# ---------------------------------------------------------------------------
# Handoff contracts (EVA-4 fail-open closure, 2026-07-25). Pure.
# ---------------------------------------------------------------------------
#
# Most missions terminate ON the outcome they certify: B1 ends with the craft on
# the ground, B2 in the target orbit, B5 past the flyby. A HANDOFF mission does
# not. `eva4_atmo_chute` terminates at EVA-WINDOW with the craft still airborne and
# crewed, hands off to the seam, and the mission SUBPROCESS EXITS - and the thing
# the scenario exists to prove ("land the kerbal alive") happens minutes later, to
# a vessel that did not exist on any frame the mission ever read.
#
# That is why flight 3 (2026-07-25) reported `MISSION-OK reason=all telemetry
# assertions met` over a kerbal who had just been killed by a cut canopy. It is NOT
# fixable by adding a fifth assertion, which is what the bug entry originally
# proposed: there is no frame on which the mission machine could evaluate one. The
# structural gate lives in the harness (hlib.SEAM_VERB_POST_MISSION_ROLE, which
# makes the post-mission outcome steps red the run). What lives HERE is the other
# half - the mission stating, in its own machine-readable result, exactly which
# part of the scenario's contract it did NOT verify and which step owns it, so
# MISSION-OK can never again be read as end-to-end success by a human or a script.
#
# Keyed by mission name. A mission ABSENT from this table declares nothing and its
# result JSON is byte-identical to before.
MISSION_HANDOFF_CONTRACTS: Dict[str, Dict] = {
    "eva4_atmo_chute": {
        "terminal": EVA4_EVA_WINDOW,
        "unverifiedByMission": ["kerbalSurvival"],
        "verifiedBy": ["EvaExit", "EvaChuteDeploy"],
    },
}

# What MISSION-OK means for a handoff mission, spelled out in the reason line the
# result JSON carries and run.py copies into driver.steps. The generic
# "all telemetry assertions met" is TRUE but reads as end-to-end success.
HANDOFF_OK_REASON_SUFFIX = ("; handoff mission - %s not verified here, owned by %s")


def mission_handoff_contract(mission: str) -> Optional[Dict]:
    """The handoff contract for ``mission``, or None when the mission terminates on
    the outcome it certifies (every mission but EVA-4 today).

    Returns a DEEP copy: a shallow one would alias the module constant's nested lists
    into every result dict, so a caller that mutated a returned ``verifiedBy`` would
    rewrite the contract for the rest of the process."""
    contract = MISSION_HANDOFF_CONTRACTS.get(str(mission or ""))
    if not contract:
        return None
    return {k: (list(v) if isinstance(v, list) else v) for k, v in contract.items()}


def handoff_ok_reason(mission: str, reason: str) -> str:
    """Extend a MISSION-OK reason with the handoff mission's own disclaimer. A
    non-handoff mission (or a non-OK reason, handled by the caller) is returned
    unchanged, so no other mission's result string moves."""
    contract = MISSION_HANDOFF_CONTRACTS.get(str(mission or ""))
    if not contract:
        return reason
    return str(reason) + HANDOFF_OK_REASON_SUFFIX % (
        ", ".join(contract["unverifiedByMission"]),
        ", ".join(contract["verifiedBy"]))


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
    warp_utilisation=None,
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
    # Handoff disclosure (EVA-4). The REASON is extended by the caller
    # (mission_runner.run_mission), immediately before the `[Verdict]` log emit, because
    # that log line - not this dict - is what a human and `harness/status.py` read;
    # extending it only here would fix the JSON and leave the operator-facing line
    # unchanged. `handoff_ok_reason` is idempotent-by-construction only in the sense
    # that the caller applies it exactly once, so this builder does NOT re-apply it.
    # What the builder owns is the machine-readable BLOCK, on every verdict: a reader of
    # a FAILED run still needs to know which step owned the rest of the contract.
    # A mission with no contract is untouched, so every other mission's result stays
    # byte-identical.
    handoff = mission_handoff_contract(mission)
    return {
        "schema": MISSION_RESULT_SCHEMA,
        "mission": mission,
        "verdict": verdict,
        "reason": reason,
        **({"handoff": handoff} if handoff is not None else {}),
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
        # Per-phase warp utilisation (B12 flight 2). ADDITIVE ON EVERY RESULT
        # from a flown mission: run_mission passes the accumulator
        # unconditionally and _fly_loop_body closes at least one row for any
        # mission that entered the loop, so the key is present on every real
        # flight. It is omitted only for a result built WITHOUT rows at all --
        # a connect failure, a build_state fault, a hand-built result in a
        # test. (An earlier revision of this comment claimed pre-existing
        # results stayed byte-identical; they do not.)
        **({"warpUtilisation": list(warp_utilisation)} if warp_utilisation else {}),
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
    # Park round-out trim (interplanetary lanes only; 0/0 everywhere else).
    ("park_trim_attempts", "parkTrimPlans"),
    ("park_trim_execs", "parkTrimBurns"),
    ("body_blank_count", "bodyBlank"),
    ("corr_burn_started", "corrBurnStarted"),
    ("aligned_streak", "alignedStreak"),
    ("min_node_dv", "minNodeDv"),
    # CORRECTION-BURN budget anchoring + round-give-up naming (B12 flight 1).
    ("corr_budget_anchor_ut", "corrBudgetAnchorUt"),
    ("corr_giveup", "corrGiveup"),
    # PER-PHASE native-warp issue count (B12 flight 2, widened by the
    # 2026-07-25 review): the ONE number that names a self-fighting warp policy
    # at a glance, now counted at every _b5_native_warp call site and reset on
    # phase entry.
    ("phase_warp_issues", "phaseWarpIssues"),
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
    # Was the craft already reading Docked at DOCK entry (MINOR-5)? True means the
    # docked short-circuit needs the target-distance corroboration, so a phase that
    # looks stuck while reading Docked self-explains in the telemetry line.
    ("dock_entry_docked", "dockEntryDocked"),
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
    # ORBIT-mission tail (B11/B12; getattr-generic, so absent elsewhere).
    # park_stable_streak is shared with the FORGE-LKO state, which gains the
    # same (purely additive) observability field.
    ("capture_arm_streak", "captureArmStreak"),
    # The arming MIRROR (the never-armed liveness run). Without it a live
    # status read cannot tell "still coasting toward the arming point" from
    # "counting down to capture-never-armed".
    ("capture_unarmed_streak", "captureUnarmedStreak"),
    ("capture_node_bad_streak", "captureNodeBadStreak"),
    ("capture_exec_disabled_streak", "captureExecDownStreak"),
    ("capture_exec_reissues", "captureExecReissues"),
    ("capture_replans_done", "captureReplans"),
    ("park_stable_streak", "parkStableStreak"),
    ("park_ever_stable", "parkEverStable"),
    ("capture_apoapsis", "captureAp"),
    ("capture_periapsis", "capturePe"),
    ("capture_eccentricity", "captureEcc"),
    # LANDING tail (B13/B14). The COMMANDED latch sits next to the OBSERVED
    # streak DELIBERATELY: a live status read must be able to see "we asked for
    # a descent and MechJeb never took it" at a glance, which is the single
    # failure this lane most expects.
    ("landing_engaged", "landingEngaged"),
    ("landing_ap_down_streak", "landingApDownStreak"),
    ("landing_ap_reissues", "landingApReissues"),
    ("landing_alt_ref", "landingAltRef"),
    ("landing_alt_ref_ut", "landingAltRefUt"),
    # The two WITHHELD-give-up counters. Here and NOT in MACHINE_DIFF_FIELDS on
    # purpose (see the fields' own comments): non-zero is the operator's signal
    # that the drop window is mis-sized for this body (vspeed) or that it was
    # disarmed near the ground (unsat), which are tuning reads, not per-frame
    # gate events.
    ("landing_vspeed_holds", "landingVspeedHolds"),
    ("landing_unsat_holds", "landingUnsatHolds"),
    # The no-progress DEBOUNCE run. Bounded in the machine at
    # LANDING_STALL_DEBOUNCE_FRAMES (the phase ends on the frame that reaches
    # it), so a live status read can watch the give-up count down instead of
    # only seeing it arrive.
    ("landing_stall_streak", "landingStallStreak"),
    ("landed_stable_streak", "landedStableStreak"),
    ("landed_ever_stable", "landedEverStable"),
    ("landed_body", "landedBody"),
    ("landed_situation", "landedSituation"),
    ("landed_vertical_speed", "landedVspd"),
    ("landed_horizontal_speed", "landedHspd"),
    ("commit_result", "commitResult"),
    # WHY the machine is about to end (the named give-up). It already reaches
    # the mission RESULT via resolve_flight_verdict, but it never reached the
    # periodic machine-state line or the status file, so a live status read
    # could see done/verdict without ever seeing the reason. The VALUE is
    # sparse by construction -- set exactly once, on the terminal frame -- but
    # the KEY is not: listing it here puts `flakeReason=none` on every machine
    # line of every mission, which is the intended cost (a fixed-width line is
    # what makes the terminal frame's value greppable next to its neighbours).
    ("flake_reason", "flakeReason"),
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
    # Park round-out trim: at most PARK_TRIM_MAX_ATTEMPTS plans and the same
    # number of burns, so at most a handful of lines per flight, and every one
    # of them is the sparse decision event "the park was not round enough to
    # plan an interplanetary ejection from". 0/0 on every lane that does not
    # arm the trim, so no other mission's log moves.
    ("park_trim_attempts", "parkTrimPlans"),
    ("park_trim_execs", "parkTrimBurns"),
    ("body_blank_count", "bodyBlank"),
    ("corr_burn_started", "corrBurnStarted"),
    ("aligned_streak", "alignedStreak"),
    # WHY a correction round ended (B12 flight 1). A round exit is otherwise
    # indistinguishable from a clean cut in the log, and the whole B12
    # diagnosis hinged on knowing which give-up fired. One sparse line per
    # round, at most MAX rounds + the arrival re-anchor per flight.
    ("corr_giveup", "corrGiveup"),
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
    # B1 canopy: the observed latch is the one gate this whole scenario turns on, and
    # the commanded latch beside it is what a reader must NOT confuse it with, so both
    # get a loud gate line naming which is which.
    ("craft_chute_full_seen", "canopySeen"),
    ("chute_deployed", "armCommanded"),
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
    # EVA-4: the debounced EVA-window agreement run (EVA4_WINDOW_DEBOUNCE_K). Bounded by
    # K, so at most a couple of flips per flight, and its 1 -> 0 reset is exactly the
    # "a frame disagreed about the handoff envelope" event an operator needs to see.
    # getattr-generic: absent on every other machine, so no other mission's log moves.
    ("window_open_streak", "evaWindowStreak"),
    # CL-1: the OBSERVED roster channel. This mission has exactly one input that
    # decides anything, and without these lines a live run shows nothing about it
    # (Opus review panel 2026-07-28, reviewer 1). `last_roster_status` is THE
    # event - `rosterStatus Assigned->Dead` is the death itself, one loud line at
    # the moment it happens - and the four streaks are bounded by their debounce
    # depths (K=2, and CL1_ROSTER_UNREAD_GIVEUP_FRAMES for the unread run), so
    # they are sparse by construction.
    # DIFF_FIELDS, deliberately NOT MACHINE_STATE_FIELDS: `format_machine_state`
    # renders every listed field for EVERY mission (absent -> '-'), so registering
    # them there would widen the ~5 s machine line of all 20 missions, which is
    # the exact cost the seam_command_* fields are kept out for. `diff_machine_state`
    # contributes nothing when a field is absent on both sides, so this is free.
    ("last_roster_status", "rosterStatus"),
    ("crew_alive_aboard_seen", "crewAliveAboard"),
    ("not_alive_streak", "crewNotAliveStreak"),
    ("never_aboard_streak", "crewNeverAboardStreak"),
    ("landed_alive_streak", "landedAliveStreak"),
    ("roster_unread_streak", "rosterUnreadStreak"),
    # ORBIT-mission tail (B11/B12): the capture-arming and park-hold debounce
    # runs, the ever-stable latch and the observed seam commit verdict --
    # exactly the sparse decision events an operator needs when a capture or a
    # parked-in-foreign-SOI commit misses.
    #
    # Both streaks are bounded IN THE MACHINE, at their debounce depths. That
    # was true of capture_arm_streak from the start (it is consumed and reset
    # on the arming frame) but NOT of park_stable_streak, which incremented for
    # every frame of the whole 180 s dwell and emitted one gate line plus a
    # 21-line window dump for each -- ~180-360 extra Info lines per park. The
    # B5 and FORGE-LKO PARK branches now cap it at park_debounce (a
    # behaviour-identical cap: every gate tests only `>= park_debounce`), so
    # the claim this comment makes is now actually enforced.
    ("capture_arm_streak", "captureArmStreak"),
    # The two capture-side liveness countdowns, both bounded by their debounce
    # depths: "the machine cannot arm the capture" and "the planned node is not
    # at periapsis" are exactly the sparse decision events the gate lines exist
    # for, and both used to be invisible until the give-up fired.
    ("capture_unarmed_streak", "captureUnarmedStreak"),
    ("capture_node_bad_streak", "captureNodeBadStreak"),
    # CAPTURE-BURN executor supervision (B11 flight 1): the observed-down
    # debounce run (bounded by CAPTURE_EXECUTOR_DISABLED_DEBOUNCE_FRAMES), and
    # the two hard-capped recovery counters. A re-issue or a re-plan is
    # EXACTLY the sparse decision event the gate lines exist for.
    ("capture_exec_disabled_streak", "captureExecDownStreak"),
    ("capture_exec_reissues", "captureExecReissues"),
    ("capture_replans_done", "captureReplans"),
    ("park_stable_streak", "parkStableStreak"),
    ("park_ever_stable", "parkEverStable"),
    ("commit_result", "commitResult"),
    # LANDING tail (B13/B14). All sparse by construction and all bounded IN THE
    # MACHINE: landing_engaged flips exactly once, the AP-down streak is capped
    # at its debounce depth, the two recovery counters are hard-capped, and
    # landed_stable_streak is capped at landed_debounce (the PARK lesson -- an
    # uncapped dwell counter emitted ~180-360 extra Info lines per park). The
    # touchdown readings flip once, on the LANDED-SETTLE entry frame, and are
    # exactly what an operator wants loudly logged the moment the craft lands.
    # landing_alt_ref / landing_alt_ref_ut are DELIBERATELY excluded: they
    # re-anchor on every healthy no-progress window and would be per-window
    # noise (they still ride the machine-state line + the status file).
    ("landing_engaged", "landingEngaged"),
    ("landing_ap_down_streak", "landingApDownStreak"),
    ("landing_ap_reissues", "landingApReissues"),
    # The no-progress debounce run, bounded at LANDING_STALL_DEBOUNCE_FRAMES by
    # the machine (the phase ends on the frame that reaches it), exactly like
    # landing_ap_down_streak above. A give-up that used to arrive with no
    # warning now counts down in the log.
    ("landing_stall_streak", "landingStallStreak"),
    ("landed_stable_streak", "landedStableStreak"),
    ("landed_ever_stable", "landedEverStable"),
    ("landed_body", "landedBody"),
    ("landed_situation", "landedSituation"),
    # B1 canopy gates. The observed latch (craft_chute_full_seen, registered above
    # next to armCommanded) decides a success terminal and the two streaks are its
    # debounce state, so a reader must be able to tell "one Deployed read then a
    # reset" from "never Deployed", and "one sub-floor sample" from "genuinely below
    # the floor". Sibling counters (aligned_streak, impact_certain_streak) are all here.
    ("canopy_seen_streak", "canopyStreak"),
    ("below_floor_streak", "belowFloorStreak"),
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


# Free-text state fields that must be squeezed into ONE `key=value` token for
# the machine-state LINE. The give-up reasons are whole sentences containing
# spaces AND '=' characters (they quote the deciding telemetry), and
# status.py's parse_kv_tokens splits on whitespace and partitions on '=', so an
# unsanitized reason would inject bogus keys -- including collisions with real
# ones like ut= / nodes=. The status FILE (machine_state_dict -> JSON) carries
# the full untruncated string; only the line is squeezed.
_MACHINE_TOKEN_FIELDS = ("flake_reason",)
_MACHINE_TOKEN_LIMIT = 120


def _obs_fmt_token(value) -> str:
    """One-token rendering of a free-text state value: whitespace and '='
    collapsed away, truncated with a '~' marker so a reader knows the line form
    is lossy and the status file has the rest."""
    if value is _MACHINE_FIELD_ABSENT:
        return "-"
    if value is None or value == "":
        return "none"
    text = "_".join(str(value).split()).replace("=", ":")
    if len(text) > _MACHINE_TOKEN_LIMIT:
        text = text[:_MACHINE_TOKEN_LIMIT] + "~"
    return text


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
        raw = getattr(state, attr, _MACHINE_FIELD_ABSENT)
        render = _obs_fmt_token if attr in _MACHINE_TOKEN_FIELDS else _obs_fmt
        parts.append("%s=%s" % (key, render(raw)))
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
        # OBSERVED MechJeb NodeExecutor.Enabled tri-state (-1 unread / 0 down /
        # 1 armed). Raw int for the same reason as crewCount: the -1 sentinel
        # IS the diagnosis when a capture watchdog fires on a run that never
        # opted into the read. NOTE: emitted UNCONDITIONALLY, so the status
        # file's snapshot block carries nodeExecutorEnabled=-1 on every mission
        # that does not opt in. That is deliberate (an absent key and an unread
        # channel are not the same thing) and it is NOT byte-identical to the
        # pre-B11 status file -- the byte-identical claim on the
        # TelemetrySnapshot field is about the MACHINE DECISIONS, which the -1
        # default does keep unchanged, not about this block.
        "nodeExecutorEnabled": snapshot.node_executor_enabled,
        # Seconds to periapsis (NaN -> None when unread). THE field the
        # capture-mode flyby warp GATES ON: without it a status read cannot
        # explain why the periapsis-bounded warp did or did not arm.
        "timeToPeriapsis": _json_safe(snapshot.time_to_periapsis),
        # LANDING lane (B13/B14). OBSERVED LandingAutopilot.Enabled as the RAW
        # tri-state (-1 unread / 0 down / 1 armed), for the nodeExecutorEnabled
        # reason: the -1 sentinel IS the diagnosis when a landing watchdog fires
        # on a run that never opted into the read. Emitted unconditionally, so
        # every non-landing mission's status snapshot carries -1 / "" / null.
        "landingApEnabled": snapshot.landing_ap_enabled,
        "landingApStatus": snapshot.landing_ap_status,
        # Horizontal surface speed (NaN -> None). One of the two conjuncts the
        # landed-stability gate reads, so without it a status read cannot
        # explain a settle that never converges.
        "horizontalSpeed": _json_safe(snapshot.horizontal_speed),
    }


def format_snapshot_compact(snapshot: TelemetrySnapshot) -> str:
    """One-line-per-frame compact snapshot form for the event-window ring
    buffer (design 2c): the fields the machines gate on, ~100 chars.

    ``crew=N`` is appended ONLY when the crew count was actually read (the
    opt-in FORGE-LKO channel), ``nodeExec=N`` ONLY when the MechJeb
    NodeExecutor.Enabled channel was actually read and ``ttPe=`` ONLY when the
    periapsis clock was actually read (both opt-in B11/B12 channels), so every
    other mission's line is unchanged.

    ``ttPe=`` is here because the capture-mode flyby warp GATES ON it: without
    it the event-window ring dump -- the ONE artifact that survives a
    periapsis-bounded warp misbehaving between rate-limited telemetry samples
    -- could not show why the warp armed, held or refused."""
    crew = "" if snapshot.crew_count < 0 else (" crew=%d" % snapshot.crew_count)
    node_exec = ("" if snapshot.node_executor_enabled < 0
                 else (" nodeExec=%d" % snapshot.node_executor_enabled))
    tt_pe = ("" if not _is_finite(snapshot.time_to_periapsis)
             else (" ttPe=%s" % _obs_fmt(snapshot.time_to_periapsis)))
    # ``landAP=`` / ``hspd=`` ride the ring ONLY when the opt-in landing channel
    # was actually read (B13/B14), so every other mission's line is unchanged.
    # They are here for the same reason ttPe is: the DESCENT watchdogs GATE on
    # them, and the ring dump is the ONE artifact that survives a descent
    # misbehaving between rate-limited telemetry samples.
    land_ap = ("" if snapshot.landing_ap_enabled < 0
               else (" landAP=%d" % snapshot.landing_ap_enabled))
    hspd = ("" if not _is_finite(snapshot.horizontal_speed)
            else (" hspd=%s" % _obs_fmt(snapshot.horizontal_speed)))
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
    return line + crew + node_exec + tt_pe + land_ap + hspd
