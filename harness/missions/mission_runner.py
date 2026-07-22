"""Shared mission-shell runtime for the M-B1 flown missions (design M-B1).

This is the THIN I/O shell the two mission programs (``b1_pad_hop.py`` /
``b2_lko_ascent.py``) share. It owns everything that is NOT a pure decision:

  - the injectable telemetry/control SEAM (``MissionControl``): the real
    ``KrpcMissionControl`` wraps the kRPC client (imported LAZILY inside
    ``open()`` so this module -- and the mission shells -- import clean on the
    BASE interpreter with NO krpc installed, which is what lets the unittest
    discovery + the fake-telemetry tests run without the venv); a test injects a
    fake with the same surface,
  - the bounded connect-retry loop (driven by ``mlib.decide_connect_retry``),
  - the bounded per-frame fly loop (driven by a mission's ``mlib`` phase machine),
  - the mission-result JSON write (via ``mlib.serialize_mission_result``),
  - the ``[Mission][LEVEL][Phase]`` diagnostic logging (design "Diagnostic
    Logging"), and the CLI parsing.

EVERY decision is delegated to ``mlib`` (the pure, kRPC-free, unittest-covered
library): the phase machines, the assertion evaluators, the connect-retry
decision, and the result serialization all live there. This module only performs
the I/O the decisions dictate. It NEVER hangs: connect is bounded by a budget +
attempt cap, every phase is bounded by an ``mlib`` phase budget, and the whole
fly loop is bounded by a wall-clock deadline (the ``--budget`` the harness
passes); every terminal path writes the mission-result JSON and returns an exit
code (0 only on MISSION-OK), and any exception is caught (never a hang), with the
kRPC connection closed in a ``finally``.

Exception taxonomy (design edges 5 / 8 vs 9), classified by WHEN + WHERE it
originates:
  - PRE-connect / setup / an internal non-kRPC bug (a None dereference, a bad
    param) -> MISSION-ERROR (subkind tooling-mission).
  - POST-connect kRPC RPC / connection-drop exception (a mid-flight socket reset,
    a dropped stream, a vessel-invalid RPC error) -> MISSION-FLAKE (subkind
    autopilot-flake), retryable on a fresh boot -- a transient the mission-validity
    gate keeps out of the Parsek-defect bucket.
The pre/post split is a ``connected`` flag; the kRPC-vs-internal origin split is
the pure ``mlib.classify_post_connect_exception`` (mlib never imports krpc).

GPLv3 NOTICE: the mission shells (this file + ``b1_pad_hop.py`` /
``b2_lko_ascent.py``) import the kRPC client (GPLv3) and are therefore themselves
taken as GPLv3 (a derivative of the GPL client). The obligation is confined to
these kRPC-importing mission shells and never reaches Parsek, run.py, hlib,
provlib, or the kRPC-free ``mlib`` (design "The dependency decision").

ASCII only; stdlib only outside the lazily-imported kRPC client; LF line endings.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time
import traceback
from dataclasses import dataclass, replace
from typing import Callable, Dict, List, Optional, Tuple

# The mission shells run as subprocesses (``python missions/<name>.py ...``) with
# their own directory (missions/) as sys.path[0]; the pure decision library lives
# in missions/lib. Put it on the path BEFORE importing mlib so the shell is
# self-sufficient whether launched as a script or imported by the tests (where
# missions/lib is already the discovery root).
_MISSIONS_DIR = os.path.dirname(os.path.abspath(__file__))
_MLIB_DIR = os.path.join(_MISSIONS_DIR, "lib")
if _MLIB_DIR not in sys.path:
    sys.path.insert(0, _MLIB_DIR)

import mlib  # noqa: E402  (deliberately after the sys.path bootstrap above)


# ---------------------------------------------------------------------------
# Bounded connect-retry defaults (design "Connection lifecycle" step 2: "up to
# 10 attempts over 30 s"). These are shell constants, not missionParams: the spec
# tunes the FLIGHT budgets, the connect budget is a fixed server-bind allowance.
# ---------------------------------------------------------------------------

CONNECT_BUDGET_SECONDS = 30.0
CONNECT_MAX_ATTEMPTS = 10
CONNECT_BACKOFF_SECONDS = 3.0

# Consecutive telemetry-read failures that mean the active vessel is GONE (KSP
# destroyed the craft and handed active-vessel to invalid debris) rather than a
# one-off transient. Below this the read re-raises (the existing transient path);
# at or above it read_snapshot emits a vessel_lost snapshot the phase machine
# terminates on (design "First live B1 flown-mission run": vessel-destroyed
# terminal).
READ_FAIL_STREAK_LIMIT = 3

# Per-frame telemetry poll cadence. The fly loop reads a snapshot, decides, and
# executes actions this often; the design's telemetry log line is rate-limited to
# ~1 Hz over this cadence.
POLL_INTERVAL_SECONDS = 0.5

# Bounded settle-tail sampled AFTER the phase machine reaches a real terminal
# (LANDED / ORBIT), before evaluating the assertions. The design requires "K
# consecutive in-tolerance snapshots" once warp has settled to 1x (guardrails:
# MechJeb warp-stutter debounce); the phase machine reaches ORBIT on the FIRST
# frame that crosses the transition threshold, so a few settled samples are needed
# for the K-consecutive periapsis / eccentricity debounce to have data. Sized one
# above the default debounce depth. Not sampled on a flake (we are aborting).
DEFAULT_SETTLE_FRAMES = mlib.DEFAULT_DEBOUNCE_K + 1

# A course-correction plan below this total dv (m/s) is removed instead of
# executed: it is smaller than the NodeExecutor's 1.0 m/s tolerance (which
# never engages on such a node -- sixth live B5 flight 2026-07-22) and the
# residual it would fix is within the flyby floor's margin anyway.
NEGLIGIBLE_CORRECTION_DV = 0.5


# ---------------------------------------------------------------------------
# Injectable telemetry/control seam (design Mental Model "kRPC calls behind an
# injectable interface"). The pure mlib decisions drive this; the real impl wraps
# kRPC, a test injects a fake with the same surface.
# ---------------------------------------------------------------------------


class MissionControl:
    """The telemetry/control seam a mission drives. All kRPC access sits behind
    this interface so the pure ``mlib`` decisions never touch the client and the
    fake-telemetry tests inject a scripted stand-in.

    Contract:
      - ``open(host, rpc_port, stream_port)`` establishes the connection or raises
        (the connect-retry loop catches the raise and decides RETRY/TIMEOUT).
      - ``client_version`` / ``server_version`` are readable after a successful
        ``open`` (the on-connect ABI check reads them).
      - ``read_snapshot()`` returns a frozen ``mlib.TelemetrySnapshot`` of the
        live flight quantities.
      - ``perform(action)`` executes one ``mlib.Action`` (throttle/stage/chute for
        B1, MechJeb calls for B2).
      - ``close()`` tears the connection down (called in a ``finally``; must
        swallow its own teardown errors or raise them for the caller to swallow).
    """

    client_version: str = ""
    server_version: str = ""

    def open(self, host: str, rpc_port: int, stream_port: int) -> None:
        raise NotImplementedError

    def read_snapshot(self) -> "mlib.TelemetrySnapshot":
        raise NotImplementedError

    def perform(self, action: "mlib.Action") -> None:
        raise NotImplementedError

    def close(self) -> None:
        raise NotImplementedError


class KrpcMissionControl(MissionControl):
    """Real telemetry/control seam: wraps the kRPC client. ``import krpc`` is
    LAZY (inside ``open``), so importing this module on the base interpreter (no
    krpc) succeeds -- only an actual flight, in the mission venv, imports the
    client.

    NOTE (PENDING-OPERATOR): the exact kRPC API surface below cannot be verified
    offline in this environment (no venv, no server). It is a best-effort mapping
    of the design's phase actions + telemetry fields to the 0.5.x client API; the
    live B1/B2 runbook (design Test Plan "PENDING-OPERATOR") is where it is
    confirmed. The fake-telemetry tests exercise the SHELL's control flow, not
    this class. Reads are defensive: a missing MechJeb surface leaves the B2
    carried-evidence flags at their benign defaults.
    """

    def __init__(self, use_mechjeb: bool = False, client_name: str = "parsek-mission") -> None:
        self._use_mechjeb = use_mechjeb
        self._client_name = client_name
        self._conn = None
        self._mechjeb = None
        self._ascent = None
        # Latches True the first time the AscentAutopilot reads as enabled, so
        # "complete" is never inferred BEFORE the autopilot has ever been engaged
        # (NIT 8: pre-engage, enabled==False must NOT read as ascent-complete=True).
        self._mj_ever_enabled = False
        # Consecutive telemetry-read failures. A one-off error still re-raises (the
        # existing transient path); only a SUSTAINED streak (>= _READ_FAIL_LIMIT)
        # means the active vessel is gone -- KSP destroyed the craft and handed
        # active-vessel to invalid debris -- so we emit a vessel_lost snapshot the
        # phase machine terminates on rather than let the mission burn its budget.
        self._read_fail_streak = 0
        self.client_version = ""
        self.server_version = ""

    def open(self, host: str, rpc_port: int, stream_port: int) -> None:
        import krpc  # LAZY: the base interpreter must import this shell with no krpc.

        self._conn = krpc.connect(
            name=self._client_name, address=host,
            rpc_port=int(rpc_port), stream_port=int(stream_port))
        status = self._conn.krpc.get_status()
        self.server_version = str(getattr(status, "version", ""))
        self.client_version = str(getattr(krpc, "__version__", ""))
        if self._use_mechjeb:
            # KRPC.MechJeb service; the AscentAutopilot is the B2 driver.
            self._mechjeb = self._conn.mech_jeb
            self._ascent = self._mechjeb.ascent_autopilot

    def read_snapshot(self) -> "mlib.TelemetrySnapshot":
        # Wrapped so a SUSTAINED read-failure streak (the active vessel destroyed +
        # handed to invalid debris) becomes a vessel_lost snapshot the phase machine
        # terminates on, while a one-off transient still re-raises (the existing
        # error path). The streak resets on any successful read.
        try:
            sc = self._conn.space_center
            v = sc.active_vessel
            orbit = v.orbit
            body_frame = orbit.body.reference_frame
            flight_srf = v.flight(body_frame)
            mj_enabled = False
            mj_complete = False
            if self._ascent is not None:
                try:
                    mj_enabled = bool(self._ascent.enabled)
                    if mj_enabled:
                        self._mj_ever_enabled = True
                    # The AscentAutopilot disables itself once the ascent is complete;
                    # "complete" = engaged EARLIER (latched) AND now disabled. Before it
                    # has ever been engaged, disabled is PRELAUNCH, NOT complete (NIT 8).
                    mj_complete = self._mj_ever_enabled and not mj_enabled
                except Exception:
                    mj_enabled = False
                    mj_complete = False
            warp_mode, warp_rate = _read_warp_state(sc)
            # AutoPilot pointing error (degrees). kRPC raises when the AP is not
            # engaged; NaN then, which the B4 attitude AND-gate treats as
            # not-aligned (a wedged AP ends as the bounded deorbit-budget flake,
            # never a throttle-up on an unknown attitude).
            try:
                ap_error = float(v.auto_pilot.error)
            except Exception:
                ap_error = float("nan")
            nodes = v.control.nodes
            snapshot = mlib.TelemetrySnapshot(
                ut=float(sc.ut),
                altitude=float(flight_srf.surface_altitude),
                vertical_speed=float(flight_srf.vertical_speed),
                apoapsis=float(orbit.apoapsis_altitude),
                periapsis=float(orbit.periapsis_altitude),
                eccentricity=float(orbit.eccentricity),
                inclination=math.degrees(float(orbit.inclination)),
                situation=_situation_name(v.situation),
                # PENDING-OPERATOR / v1 LIMITATION: this reads the VESSEL-TOTAL SolidFuel,
                # NOT the active-stage total. It is correct for the v1 B1 fixture (a
                # single-SRB pad-hop, where vessel-total == active-stage SolidFuel), and
                # the B1 "ASCENT -> COAST when solid fuel exhausted" transition relies on
                # that. A MULTI-SRB or asparagus-staged craft would keep vessel-total
                # SolidFuel > 0 while an earlier SRB stage is spent, so this would NOT
                # detect the per-stage burnout -- reading the active decouple stage's own
                # SolidFuel (v.resources_in_decouple_stage / a stage-scoped query) is the
                # deferred fix when a multi-SRB fixture lands.
                stage_solid_fuel=float(v.resources.amount("SolidFuel")),
                mj_autopilot_enabled=mj_enabled,
                mj_ascent_complete=mj_complete,
                ap_error=ap_error,
                warp_mode=warp_mode,
                warp_rate=warp_rate,
                # SOI body + pending-node evidence (B5 cross-SOI gates + the
                # PLAN/BURN transitions) + the first node's remaining dv (the
                # DIY correction burner's cut/overshoot gates). Direct reads: a
                # failure re-raises into the surrounding try/except and counts
                # toward the vessel-lost read-fail streak (the fly loop
                # tolerates non-transport raises). The machine-side guards for
                # an EMPTY body ("" = no reading) and a NaN node_dv fail closed.
                body=str(orbit.body.name),
                node_count=len(nodes),
                node_dv=(float(nodes[0].remaining_delta_v) if nodes else float("nan")),
                liquid_fuel=float(v.resources.amount("LiquidFuel")),
                throttle=float(v.control.throttle),
            )
            self._read_fail_streak = 0
            return snapshot
        except Exception:
            self._read_fail_streak += 1
            if self._read_fail_streak < READ_FAIL_STREAK_LIMIT:
                # A one-off / transient read error: preserve the existing behaviour
                # (the run_mission handler classifies a post-connect kRPC/socket drop
                # as a flake, an internal bug as an error).
                raise
            # Sustained streak: the active vessel is gone. Emit a vessel_lost snapshot
            # (best-effort UT so the phase-entry clock still advances) instead of
            # re-raising, so the phase machine reaches its vessel-lost terminal.
            ut = 0.0
            try:
                ut = float(self._conn.space_center.ut)
            except Exception:
                ut = 0.0
            _stdout_sink(mlib.format_mission_log_line(
                "Warn", "Telemetry",
                "vessel-lost: telemetry read failed %d consecutive samples; "
                "emitting vessel_lost snapshot ut=%s"
                % (self._read_fail_streak, _fmt(ut))))
            return mlib.TelemetrySnapshot(ut=ut, vessel_lost=True)

    def perform(self, action: "mlib.Action") -> None:
        sc = self._conn.space_center
        v = sc.active_vessel
        control = v.control
        kind = action.kind
        if kind == mlib.ACTION_SET_THROTTLE:
            control.throttle = float(action.value if action.value is not None else 0.0)
            # Immediate readback (thirteenth flight diagnosability): a set that
            # does not stick means another controller (MechJeb thrust module)
            # is zeroing the throttle every frame -- name it loudly.
            try:
                back = float(control.throttle)
                if abs(back - float(action.value or 0.0)) > 0.01:
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Throttle",
                        "set_throttle %.2f did not stick (readback %.2f): another "
                        "controller holds the throttle" % (float(action.value or 0.0), back)))
            except Exception:
                pass
        elif kind == mlib.ACTION_CUT_THROTTLE:
            control.throttle = 0.0
        elif kind == mlib.ACTION_ACTIVATE_STAGE:
            control.activate_next_stage()
        elif kind == mlib.ACTION_DEPLOY_CHUTE:
            # Fire every deployable parachute (stock action group not assumed).
            for p in v.parts.parachutes:
                try:
                    p.deploy()
                except Exception:
                    pass
        elif kind == mlib.ACTION_MJ_SET_TARGET_APOAPSIS:
            self._ascent.desired_orbit_altitude = float(action.value)
        elif kind == mlib.ACTION_MJ_ENABLE_AUTOSTAGE:
            self._ascent.autostage = True
        elif kind == mlib.ACTION_MJ_ENGAGE_ASCENT:
            self._ascent.enabled = True
        elif kind == mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION:
            # Execute the circularization node IF the autopilot left one.
            # MechJeb's AscentAutopilot usually performs the circularization
            # burn itself before self-disabling, so an empty node list here is
            # the NORMAL completed case - and ExecuteAllNodes on an empty list
            # is a server-side RPCError (first live B2 run 2026-07-20).
            if len(v.control.nodes) > 0:
                self._mechjeb.node_executor.execute_all_nodes()
        elif kind == mlib.ACTION_AP_POINT_RETROGRADE:
            # kRPC's NATIVE AutoPilot (NOT MechJeb SmartASS): point orbital
            # retrograde. Surface verified against the installed krpc 0.5.4
            # python client source (harness/missions/.venv): Vessel.auto_pilot,
            # AutoPilot.reference_frame / target_direction setters, engage();
            # Vessel.orbital_reference_frame's y-axis is the orbital PROGRADE
            # direction, so retrograde is (0, -1, 0) in that frame. Live
            # behavior proof rides the first B4 operator run.
            # Defensive: if the (abnormal) circularize path left MechJeb's node
            # executor running, abort it first - two autopilots fighting over
            # attitude wedges the flip until the deorbit budget flakes (Fable
            # review of PR #1335, NIT).
            if self._mechjeb is not None:
                try:
                    self._mechjeb.node_executor.abort()
                except Exception:
                    pass
            ap = v.auto_pilot
            ap.reference_frame = v.orbital_reference_frame
            ap.target_direction = (0.0, -1.0, 0.0)
            # Heavy-stack tuning: the AP's default 5s deceleration time is
            # tuned for agile craft and limit-cycles on a low-torque stack
            # like the post-circularization Kerbal X (pod reaction wheel
            # turning the whole orbiter), overshooting the target repeatedly.
            # A longer deceleration window makes the approach critically
            # damped at the cost of a slower (but converging) flip.
            try:
                ap.deceleration_time = (15.0, 15.0, 15.0)
            except Exception:
                pass  # tuning is best-effort; the attitude gate is the safety
            ap.engage()
        elif kind == mlib.ACTION_AP_DISENGAGE:
            v.auto_pilot.disengage()
        elif kind == mlib.ACTION_WARP_TO:
            # Blocking RAILS warp to the machine-chosen target UT (bounded hops;
            # mlib emits now + warpHopSeconds per decision frame). Blocking is
            # acceptable: the fly loop's poll gap simply widens across the call,
            # and the B4 phase budgets are game-time so the warped span is
            # budgeted correctly. The warp guard permits RAILS via the spec's
            # allow_rails_warp.
            sc.warp_to(float(action.value))
        elif kind == mlib.ACTION_SET_TARGET_BODY:
            # B5: target the transfer body (the ManeuverPlanner's OperationTransfer
            # is a Hohmann-to-target and needs it). Surface verified against the
            # pinned kRPC 0.5.4 source (SpaceCenter.TargetBody settable property,
            # SpaceCenter.Bodies name dict).
            sc.target_body = sc.bodies[str(action.text)]
        elif kind == mlib.ACTION_MJ_PLAN_TRANSFER:
            # KRPC.MechJeb 0.8.1: maneuver_planner.operation_transfer.make_nodes()
            # (a Hohmann transfer to the current target). INTERCEPT-ONLY BUT
            # TARGETED -- semantics verified against the DECOMPILED MechJeb
            # 2.15.1 OperationGeneric (2026-07-21):
            #   capture=False      -> the GUI's "intercept only" checkbox
            #                         (Capture = !intercept_only); a single TLI
            #                         node, no arrival/insertion second node
            #                         (which the first live flight showed parks
            #                         the flyby machine).
            #   plan_capture=False -> belt+braces (only read when Capture).
            #   rendezvous=True    -> the TARGETED-INTERCEPT flag: it flows into
            #                         DeltaVAndTimeForHohmannTransfer as the
            #                         "arrive AT the target" mode. False is the
            #                         GUI's phase-blind "Transfer" (reach the
            #                         target's ALTITUDE at arbitrary phase) --
            #                         forcing it False on the third live flight
            #                         produced a deterministic no-encounter
            #                         coast (ap 11.4M, fell back to Kerbin) with
            #                         a persistent ~403 m/s re-aim demand.
            # A plan with no valid window/target throws a server-side
            # OperationException: log + swallow, node_count stays 0, and the
            # machine's bounded re-plan cadence owns the retry (re-issuing is
            # safe ONLY while no node exists).
            try:
                op = self._mechjeb.maneuver_planner.operation_transfer
                op.capture = False
                op.plan_capture = False
                op.rendezvous = True
                op.make_nodes()
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Plan", "operation_transfer.make_nodes failed: %s" % (exc,)))
        elif kind == mlib.ACTION_MJ_PLAN_COURSE_CORRECT:
            # KRPC.MechJeb 0.8.1: course-correct the existing target encounter to
            # the machine-chosen flyby periapsis (metres). Same throw/log/swallow
            # contract as the transfer plan; MechJeb transiently sees no encounter
            # right after a burn and the machine re-plans on its bounded cadence.
            # DV CAP (action.limit, m/s): a genuine course correction is a SMALL
            # tweak. The second live flight (2026-07-21) had MechJeb plan an
            # oversized "correction" (executing it re-shaped the transfer,
            # ap 11.4M -> 16.6M, and wedged the executor until the burn budget
            # flaked). An over-cap plan's nodes are removed here, so the machine
            # sees node_count stay 0 and PLAN-CORRECTION's bounded fall-through
            # coasts on the raw Hohmann intercept instead.
            try:
                op = self._mechjeb.maneuver_planner.operation_course_correction
                op.course_correct_final_pe_a = float(action.value)
                nodes = op.make_nodes()
                if nodes:
                    total_dv = 0.0
                    for n in nodes:
                        total_dv += abs(float(n.delta_v))
                    cap = float(action.limit) if action.limit else 0.0
                    if cap > 0.0 and total_dv > cap:
                        v.control.remove_nodes()
                        _stdout_sink(mlib.format_mission_log_line(
                            "Warn", "Plan",
                            "course-correction dv %.1f m/s exceeds cap %.1f; "
                            "plan removed (correction disqualified, coast will "
                            "fly the raw intercept)" % (total_dv, cap)))
                    elif total_dv < NEGLIGIBLE_CORRECTION_DV:
                        # A node smaller than the executor's own 1.0 m/s
                        # tolerance never engages (sixth live flight: execute
                        # issued, no warp, no burn); a sub-0.5 m/s residual is
                        # also within the flyby floor's margin. Skip the round.
                        v.control.remove_nodes()
                        _stdout_sink(mlib.format_mission_log_line(
                            "Info", "Plan",
                            "course-correction dv %.2f m/s is negligible "
                            "(< %.1f); plan removed (trajectory already good)"
                            % (total_dv, NEGLIGIBLE_CORRECTION_DV)))
            except Exception as exc:
                _stdout_sink(mlib.format_mission_log_line(
                    "Warn", "Plan",
                    "operation_course_correction.make_nodes failed: %s" % (exc,)))
        elif kind == mlib.ACTION_MJ_EXECUTE_NODES:
            # Hand the planned node(s) to MechJeb's NodeExecutor with autowarp:
            # it rails-warps to each node and burns it (the warp guard permits
            # RAILS via allow_rails_warp). Guarded on node count like the B2
            # circularize case: ExecuteAllNodes on an empty list is a server-side
            # RPCError (first live B2 run 2026-07-20).
            # NOTE: no tolerance override -- the KRPC.MechJeb 0.8.1 wrapper
            # never initializes its Tolerance backing object (InitInstance
            # sets only leadTime, verified in the pinned source), so setting
            # it throws server-side and the executor keeps its 0.1 default.
            # TLI-scale nodes execute fine on the defaults (the far-node
            # autowarp branch carries them); corrections use the DIY burner.
            if len(v.control.nodes) > 0:
                ne = self._mechjeb.node_executor
                ne.autowarp = True
                ne.execute_all_nodes()
        elif kind == mlib.ACTION_SET_RAILS_WARP:
            # Non-blocking rails warp (operator design critique: warp changes
            # only when an action is imminent -- no more per-hop ramp sawtooth,
            # no blocking RPC to wedge under a dialog). The server clamps the
            # factor to the altitude-legal maximum; 0 = 1x.
            sc.rails_warp_factor = int(action.value)
        elif kind == mlib.ACTION_AP_POINT_NODE:
            # DIY correction burner (live finding 8): point kRPC's NATIVE
            # AutoPilot along the first node's burn vector. Node.ReferenceFrame's
            # y-axis IS the burn vector (pinned kRPC source); same heavy-stack
            # deceleration tuning as the B4 retro flip.
            nodes = v.control.nodes
            if len(nodes) > 0:
                # Release MechJeb's throttle hold FIRST (eleventh live flight,
                # via the new thr= readback: after the TLI executor runs,
                # set_throttle commands read back 0.000 -- MechJeb's thrust
                # controller keeps zeroing the throttle until the executor's
                # full abort teardown runs). This mirrors B4's proven
                # AP_POINT_RETROGRADE pattern, whose SET_THROTTLE always
                # worked because it aborts the executor before engaging the
                # native AP. Executor re-use poisoning is moot: corrections
                # never touch the executor again.
                if self._mechjeb is not None:
                    # LOUD abort (thirteenth flight: corrections still zero-
                    # thrust + 0.07 deg/s crawl -- something retains vessel
                    # control; a silently-failed abort would explain it, so
                    # log the outcome + the executor's enabled state).
                    try:
                        self._mechjeb.node_executor.abort()
                        _stdout_sink(mlib.format_mission_log_line(
                            "Info", "Point", "executor abort ok; enabled=%s"
                            % (self._mechjeb.node_executor.enabled,)))
                    except Exception as exc:
                        _stdout_sink(mlib.format_mission_log_line(
                            "Warn", "Point", "executor abort FAILED: %s" % (exc,)))
                # SAS/RCS off before engaging the kRPC AP (standard practice;
                # a stock SAS hold left on by MechJeb's teardown would cancel
                # most of the wheel torque = exactly the measured 0.07 deg/s
                # crawl that survived every AP-tuning change).
                try:
                    v.control.sas = False
                    v.control.rcs = False
                except Exception:
                    pass
                # SmartASS OFF: force-release MechJeb's ATTITUDE controller
                # (the crawl + throttle hold survived the executor abort AND
                # sas=False on the fourteenth flight -- MechJeb's attitude
                # module is the remaining candidate holder, and Smart A.S.S.
                # Off is its exposed release lever; pinned-source verified
                # SmartASSAutopilotMode.Off + Update(false)).
                try:
                    sa = self._mechjeb.smart_ass
                    sa.autopilot_mode = self._mechjeb.SmartASSAutopilotMode.off
                    sa.update(False)
                    _stdout_sink(mlib.format_mission_log_line(
                        "Info", "Point", "smart-ass forced OFF"))
                except Exception as exc:
                    _stdout_sink(mlib.format_mission_log_line(
                        "Warn", "Point", "smart-ass OFF failed: %s" % (exc,)))
                ap = v.auto_pilot
                ap.reference_frame = nodes[0].reference_frame
                ap.target_direction = (0.0, 1.0, 0.0)
                # deceleration_time (15,15,15), the B4-PROVEN tuning. Finding
                # 9(c) had this BACKWARDS: on a low-torque craft this window
                # RAISES the angular-velocity cap (the AP permits spin it can
                # stop within the window; omega_max ~ alpha * window). With
                # the override removed the twelfth flight crawled at
                # ~0.05 deg/s under kRPC's default ~0.5 s stopping profile;
                # B4's measured 0.5 deg/s flip runs (15,15,15). Operator
                # observed the 1x multi-hour coast live and flagged it.
                try:
                    ap.deceleration_time = (15.0, 15.0, 15.0)
                except Exception:
                    pass  # best-effort; the give-up bound owns a slow flip
                ap.engage()
        elif kind == mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES:
            # B5 burn-exit cleanup: remove every remaining node, so the coast
            # hops are not suppressed by node_count > 0 and no unwanted burn
            # ever flies. Deliberately NO node_executor.abort() call: MechJeb's
            # executor aborts ITSELF on the next physics frame once the node
            # list is empty (decompiled 2.15.1 OnFixedUpdate: !_hasNodes ->
            # Abort()), and an EXTERNAL Abort() is the prime suspect for the
            # poisoned re-engage observed on flights 6-7 (every fresh
            # ExecuteAllNodes works; every post-external-abort one never
            # starts). Letting MechJeb run its own abort path keeps its
            # attitude/thrust user bookkeeping consistent.
            v.control.remove_nodes()
        else:
            raise ValueError("unknown action kind: %r" % (kind,))

    def close(self) -> None:
        conn = self._conn
        self._conn = None
        if conn is not None:
            conn.close()


def _situation_name(situation) -> str:
    """Normalize a kRPC VesselSituation enum to the design's UPPER token
    (LANDED / SPLASHED / PRE_LAUNCH / ...). kRPC exposes the enum member's
    ``name`` in lower_snake ('landed', 'splashed'); the design's landed set is
    ('LANDED', 'SPLASHED'), so upper-casing the name lines the two up."""
    name = getattr(situation, "name", None)
    if name is None:
        name = str(situation).rsplit(".", 1)[-1]
    return str(name).upper()


def _read_warp_state(sc):
    """Read (warp_mode, warp_rate) derived from ONE warp_rate sample. The rate
    is read exactly once and the mode classified from THAT sample: reading them
    as two separate RPCs let a ramp crossing 1.0 report mode NONE with a rate
    above 1, which the guard treats as an inconsistent state (Fable review of
    the PR #1328 tail, SF-1: a single-sample false MISSION-FLAKE risked on
    every warp-engagement ramp). Defensive: an unknown surface reports
    (NONE, 1.0)."""
    try:
        rate = float(getattr(sc, "warp_rate", 1.0))
        if not (rate > 1.0):
            return "NONE", rate
        mode = getattr(sc, "warp_mode", None)
        name = getattr(mode, "name", None) or str(mode)
        return ("RAILS" if "rail" in str(name).lower() else "PHYSICS"), rate
    except Exception:
        return "NONE", 1.0


# ---------------------------------------------------------------------------
# Pure ABI version check (design "Connection lifecycle" step 3). Testable.
# ---------------------------------------------------------------------------


def major_minor_mismatch(client_version: str, server_version: str) -> bool:
    """True iff client and server MAJOR.MINOR differ (an ABI-incompatible pair) or
    either version is unparseable (a foreign / unknown server, edge 2/3). A patch
    difference (0.5.4 vs 0.5.3) is NOT a mismatch. On True the mission aborts
    MISSION-ERROR rather than flying against a mismatched RPC surface."""
    c = _major_minor(client_version)
    s = _major_minor(server_version)
    if c is None or s is None:
        return True
    return c != s


def _major_minor(version: str) -> Optional[Tuple[int, int]]:
    parts = str(version or "").strip().split(".")
    if len(parts) < 2:
        return None
    try:
        return (int(parts[0]), int(parts[1]))
    except (TypeError, ValueError):
        return None


# ---------------------------------------------------------------------------
# Diagnostic logging (design "Diagnostic Logging"). The sink is injectable so the
# tests capture lines; default is stdout (captured by run.py into the harness log).
# ---------------------------------------------------------------------------


@dataclass
class MissionLogger:
    """Thin ``[Mission][LEVEL][Phase]`` line emitter (format from
    ``mlib.format_mission_log_line``). ``sink`` is a callable taking one line;
    default writes to stdout. ``rate_limit`` throttles the per-frame telemetry
    line to ~1 Hz keyed by a tag (batch-counting convention: one summary line,
    not one per frame)."""
    sink: Callable[[str], None]
    clock: Callable[[], float]
    _last_emit: dict = None

    def __post_init__(self) -> None:
        if self._last_emit is None:
            self._last_emit = {}

    def emit(self, level: str, phase: str, message: str) -> None:
        self.sink(mlib.format_mission_log_line(level, phase, message))

    def info(self, phase: str, message: str) -> None:
        self.emit("Info", phase, message)

    def warn(self, phase: str, message: str) -> None:
        self.emit("Warn", phase, message)

    def error(self, phase: str, message: str) -> None:
        self.emit("Error", phase, message)

    def verbose(self, phase: str, message: str) -> None:
        self.emit("Verbose", phase, message)

    def verbose_rate_limited(self, key: str, phase: str, message: str,
                             interval: float = 1.0) -> None:
        now = self.clock()
        last = self._last_emit.get(key)
        if last is None or (now - last) >= interval:
            self._last_emit[key] = now
            self.emit("VerboseRateLimited", phase, message)


def _stdout_sink(line: str) -> None:
    sys.stdout.write(line + "\n")


# ---------------------------------------------------------------------------
# Bounded connect + fly loops (I/O; every wait bounded, design "A mission never
# hangs"). Every decision is delegated to mlib.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ConnectResult:
    ok: bool
    attempts: int
    connected_seconds: float          # NaN when never connected
    last_error: Optional[str]


def connect_with_retry(
    control: MissionControl, host: str, rpc_port: int, stream_port: int,
    log: MissionLogger,
    budget: float = CONNECT_BUDGET_SECONDS,
    max_attempts: int = CONNECT_MAX_ATTEMPTS,
    backoff: float = CONNECT_BACKOFF_SECONDS,
    clock: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
) -> ConnectResult:
    """Bounded connect loop (design "Connection lifecycle" step 2). Attempts to
    ``open`` the seam; on each failure ``mlib.decide_connect_retry`` decides RETRY
    (sleep the backoff, try again) vs TIMEOUT (give up). Bounded by BOTH the
    attempt cap and the wall budget, so an unreachable server can never retry
    forever. Returns a ``ConnectResult``; ``ok=False`` maps to
    MISSION-CONNECT-TIMEOUT."""
    start = clock()
    attempts = 0
    last_error: Optional[str] = None
    while True:
        attempts += 1
        try:
            control.open(host, rpc_port, stream_port)
            connected = clock() - start
            log.info("Connect", "connected in %ss attempts=%d serverVersion=%s clientVersion=%s"
                     % (_fmt(connected), attempts, control.server_version, control.client_version))
            return ConnectResult(True, attempts, connected, None)
        except Exception as exc:  # noqa: BLE001 -- any connect failure is a retry candidate
            last_error = "%s: %s" % (type(exc).__name__, exc)
            elapsed = clock() - start
            log.verbose_rate_limited(
                "connect", "Connect",
                "connect attempt=%d/%d elapsed=%s/%s" % (attempts, max_attempts, _fmt(elapsed), _fmt(budget)))
            if mlib.decide_connect_retry(elapsed, attempts, budget, max_attempts) == mlib.CONNECT_RETRY:
                sleep(backoff)
                continue
            log.warn("Connect", "connect timeout after %d attempts / %ss -> %s"
                     % (attempts, _fmt(elapsed), mlib.MISSION_CONNECT_TIMEOUT))
            return ConnectResult(False, attempts, float("nan"), last_error)


def fly_loop(
    control: MissionControl, initial_state, decide, log: MissionLogger,
    deadline: float,
    clock: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
    poll_interval: float = POLL_INTERVAL_SECONDS,
    settle_frames: int = DEFAULT_SETTLE_FRAMES,
    allow_rails_warp: bool = False,
    max_physics_warp: float = 0.0,
):
    """Drive a mission's ``mlib`` phase machine to completion, bounded by BOTH the
    machine's per-phase budgets (inside ``decide``) AND a wall-clock ``deadline``
    (the ``--budget`` the harness passes). Returns ``(final_state, frames)``.

    Each frame: read a snapshot, feed it to the pure ``decide`` (which returns the
    next state + the actions to perform), execute those actions on the seam, log a
    transition when the phase changes, and rate-limit a telemetry line. The loop
    NEVER blocks unbounded: if the machine wedges without hitting its own phase
    budget (e.g. frozen UT), the wall deadline forces a MISSION-FLAKE naming the
    stuck phase, so the shell always terminates and writes a result.

    Physics-warp guard (design edge 7): each frame the snapshot's warp state is
    checked via the pure ``mlib.is_unexpected_warp`` (PHYSICS warp is never
    permitted; RAILS warp only when ``allow_rails_warp``). An unexpected warp state
    is a determinism violation -> MISSION-FLAKE naming the phase + the warp state,
    with the WARP log line, so a stray high-warp request never silently distorts
    the recorded trajectory.

    On a REAL terminal (LANDED / ORBIT, ``state.verdict is None``) the loop then
    samples a bounded SETTLE-TAIL of ``settle_frames`` more snapshots before
    returning, so the assertion evaluators' K-consecutive debounce has settled
    orbit data (design guardrails: sample only after warp settles, require K
    consecutive in-tolerance snapshots). A flake terminal skips the settle-tail."""
    state = initial_state
    frames: List = []
    # Seed the exception-state slot immediately: without this, a raise BEFORE
    # the loop body's first publish (e.g. a malformed build_state result) would
    # attach the PREVIOUS mission's final state to THIS mission's error result
    # (Fable review of the PR #1328 tail, NIT-2).
    _FLY_LOOP_LAST_STATE["state"] = state
    try:
        return _fly_loop_body(control, state, decide, log, deadline, clock, sleep,
                              poll_interval, settle_frames, allow_rails_warp, max_physics_warp, frames)
    except Exception as exc:
        # Preserve the ADVANCED machine state for the shell's error result:
        # without this a mid-flight kRPC error reported phasesReached=[] even
        # though the machine had reached CIRCULARIZE (first live B2 run).
        # ``mission_state`` rides the exception; run_mission reads it back.
        if not hasattr(exc, "mission_state"):
            exc.mission_state = _FLY_LOOP_LAST_STATE.get("state", state)  # type: ignore[attr-defined]
        raise


# The loop body publishes its current state here each frame so the fly_loop
# wrapper can attach it to an escaping exception (single-threaded shell; the
# dict is overwritten per call and read only on the unwind path).
_FLY_LOOP_LAST_STATE: Dict = {}


def _fly_loop_body(control, state, decide, log, deadline, clock, sleep,
                   poll_interval, settle_frames, allow_rails_warp, max_physics_warp, frames):
    warp_violations = 0
    while not state.done:
        _FLY_LOOP_LAST_STATE["state"] = state
        if clock() >= deadline:
            log.warn(state.phase, "wall budget elapsed in phase %s -> %s"
                     % (state.phase, mlib.MISSION_FLAKE))
            return replace(state, verdict=mlib.MISSION_FLAKE, flake_phase=state.phase, done=True), frames
        try:
            snapshot = control.read_snapshot()
        except Exception as exc:  # noqa: BLE001
            if mlib.is_transport_drop_exception(type(exc).__name__):
                raise  # dead connection: the retryable-flake path (edges 5/8)
            # A server-ANSWERED read failure (vessel-state RPC error, e.g. the
            # maneuver-nodes read on a just-destroyed vessel -- seventh live B5
            # flight): tolerate and poll on. Bounded inherently: the control
            # seam's read-fail streak escalates to a vessel_lost snapshot
            # within READ_FAIL_STREAK_LIMIT consecutive failures, so at most
            # two of these continues happen before the machine reaches its
            # honest vessel-lost terminal.
            log.warn(state.phase, "telemetry read failed (%s: %s); polling on"
                     % (type(exc).__name__, str(exc)[:160]))
            sleep(poll_interval)
            continue
        frames.append(snapshot)
        # Edge 7: an unexpected physics (or, for B1, any) warp state distorts the
        # flight; flake naming the phase + warp state rather than record a warped
        # run. DEBOUNCED to two CONSECUTIVE violating samples (Fable review of
        # the PR #1328 tail, SF-1/SF-2): TimeWarp.CurrentRate is a jittery,
        # continuously-ramping quantity, so a single sample caught mid-ramp or
        # on a frame-hitch spike must not kill an otherwise clean flight; a
        # REAL unexpected warp state persists across the 0.5s poll gap.
        if mlib.is_unexpected_warp(snapshot.warp_mode, snapshot.warp_rate, allow_rails_warp,
                                   max_physics_warp=max_physics_warp):
            warp_violations += 1
            log.warn(state.phase, "unexpected %s-warp x%s in phase %s (allow_rails=%s) strike %d/2"
                     % (snapshot.warp_mode, _fmt(snapshot.warp_rate), state.phase,
                        allow_rails_warp, warp_violations))
            if warp_violations >= 2:
                log.warn(state.phase, "unexpected warp persisted 2 consecutive samples -> %s"
                         % (mlib.MISSION_FLAKE,))
                return replace(state, verdict=mlib.MISSION_FLAKE, flake_phase=state.phase, done=True), frames
        else:
            warp_violations = 0
        prev_phase = state.phase
        state, actions = decide(state, snapshot)
        # Re-publish post-decide: a perform() failure below must report the
        # phase the machine had ALREADY entered this frame.
        _FLY_LOOP_LAST_STATE["state"] = state
        if state.phase != prev_phase:
            log.info(state.phase, "phase %s -> %s ut=%s alt=%s ap=%s vsurf=%s"
                     % (prev_phase, state.phase, _fmt(snapshot.ut), _fmt(snapshot.altitude),
                        _fmt(snapshot.apoapsis), _fmt(snapshot.vertical_speed)))
        for action in actions:
            control.perform(action)
            log.info(state.phase, "action %s value=%s%s"
                     % (action.kind, _fmt(action.value),
                        (" text=%s" % action.text) if getattr(action, "text", None) else ""))
        # alt/vspeed/body/nodes ride the line too: B4 attempt-1 (2026-07-21)
        # stalled in REENTRY with a line that omitted altitude, leaving
        # frozen-physics vs normal-coast undiagnosable from the log. Log what
        # the machines gate on.
        log.verbose_rate_limited(
            "telemetry", state.phase,
            "telemetry ap=%s pe=%s ecc=%s inc=%s alt=%s vspd=%s body=%s nodes=%d "
            "nodeDv=%s lf=%s thr=%s situation=%s warp=%sx%s apErr=%s"
            % (_fmt(snapshot.apoapsis), _fmt(snapshot.periapsis), _fmt(snapshot.eccentricity),
               _fmt(snapshot.inclination), _fmt(snapshot.altitude),
               _fmt(snapshot.vertical_speed), snapshot.body or "?", snapshot.node_count,
               _fmt(snapshot.node_dv), _fmt(snapshot.liquid_fuel), _fmt(snapshot.throttle),
               snapshot.situation, snapshot.warp_mode,
               _fmt(snapshot.warp_rate), _fmt(snapshot.ap_error)))
        if state.done:
            break
        sleep(poll_interval)

    # Settle-tail (real terminal only): gather K-ish settled samples for debounce.
    # A DOWN-terminal state (mlib.B1State.skip_settle_tail, operator decision
    # 2026-07-20 option A) skips the tail: the vessel is GONE, so the tail would
    # only gather vessel_lost / garbage frames. LANDED / ORBIT / SPLASHDOWN keep it.
    if state.verdict is None and getattr(state, "skip_settle_tail", False):
        log.info(state.phase, "settle tail skipped: terminal marks vessel gone "
                              "(skip_settle_tail set)")
    elif state.verdict is None:
        for _ in range(max(0, settle_frames)):
            if clock() >= deadline:
                break
            snapshot = control.read_snapshot()
            frames.append(snapshot)
            log.verbose_rate_limited(
                "settle", state.phase,
                "settle ap=%s pe=%s ecc=%s inc=%s situation=%s"
                % (_fmt(snapshot.apoapsis), _fmt(snapshot.periapsis), _fmt(snapshot.eccentricity),
                   _fmt(snapshot.inclination), snapshot.situation))
            sleep(poll_interval)
    return state, frames


# ---------------------------------------------------------------------------
# Mission spec (the per-mission wiring b1_pad_hop / b2_lko_ascent supply) + the
# generic runner that flies it. This is the whole mission control flow; the two
# shells just build a MissionSpec and call run_mission.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class MissionSpec:
    """Everything the generic runner needs to fly one mission, supplied by the
    concrete shell (b1/b2):
      - ``name``:        the mission id written into the result.
      - ``build_state``: params-dict -> the mlib initial phase-machine state.
      - ``decide``:      the mlib per-frame decision fn ``(state, snapshot) -> (state, actions)``.
      - ``evaluate``:    ``(frames, params, state) -> list[mlib.AssertionOutcome]``.
        ``state`` is the TERMINATED phase-machine state (carried evidence: B1's
        DOWN terminal, B4's phases_reached / chute_deployed); shells that only
        need frames accept and ignore it (``state=None`` default).
      - ``make_control``: () -> a real MissionControl (KrpcMissionControl); tests
        inject their own control and never call this.
      - ``allow_rails_warp``: whether RAILS warp is a PERMITTED warp state for this
        mission's fly loop (design edge 7). B1 flies 1x throughout (False); B2
        permits RAILS on its exo-atmospheric coast (True). PHYSICS warp is never
        permitted for either. An unexpected warp state flakes the mission.
    """
    name: str
    build_state: Callable[[dict], object]
    decide: Callable
    evaluate: Callable
    make_control: Callable[[], MissionControl]
    allow_rails_warp: bool = False
    # Highest PERMITTED physics-warp rate (0.0 = never, the B1 default). B2
    # sets 2.0: MechJeb's AscentAutopilot engages its own 2x physics warp
    # during ascent and KRPC.MechJeb 0.8.1 exposes no toggle for it (observed
    # live 2026-07-20). Above the bound (plus the ramp allowance) the warp
    # guard still flakes the mission.
    max_physics_warp: float = 0.0


def run_mission(
    spec: MissionSpec, params: dict, host: str, rpc_port: int, stream_port: int,
    result_path: str, budget: float,
    control: Optional[MissionControl] = None,
    log: Optional[MissionLogger] = None,
    clock: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
    writer: Optional[Callable[[str, str], None]] = None,
) -> int:
    """Fly ``spec`` end-to-end and write the mission-result JSON. Returns the
    process exit code (0 only on MISSION-OK; nonzero on every non-OK verdict so
    run.py has a fallback signal if the result file is missing, edge 12).

    The whole body is wrapped so NOTHING escapes as a hang or an unwritten result:
    connect (bounded) -> ABI check -> fly (bounded) -> evaluate assertions ->
    resolve verdict; any exception -> MISSION-ERROR with a traceback; the seam is
    ALWAYS closed in the ``finally`` before the result is written, so the client
    is gone before the harness sends FlushAndQuit (design step 5 / edge 10).

    ``control`` / ``log`` / ``writer`` are injectable for the fake-telemetry
    tests; production passes none and gets the real kRPC seam, stdout logging, and
    a filesystem write."""
    if log is None:
        log = MissionLogger(sink=_stdout_sink, clock=clock)
    if control is None:
        control = spec.make_control()
    if writer is None:
        writer = _write_result_file

    wall_start = clock()
    verdict = mlib.MISSION_ERROR
    reason = "mission did not run"
    phases_reached: List[str] = []
    connect_attempts = 0
    connected_seconds = float("nan")
    assertion_rows: List = []
    error: Optional[str] = None
    # Track whether the kRPC connect (+ ABI check) succeeded, so a post-connect
    # exception is classified by the mission-validity gate (edge 5/8: a connection
    # drop or kRPC RPC error is a MISSION-FLAKE, not a MISSION-ERROR); only a
    # pre-connect / setup / internal non-kRPC exception stays MISSION-ERROR (edge 9).
    connected = False

    try:
        cr = connect_with_retry(control, host, rpc_port, stream_port, log,
                                clock=clock, sleep=sleep)
        connect_attempts = cr.attempts
        if not cr.ok:
            verdict = mlib.MISSION_CONNECT_TIMEOUT
            reason = "server unreachable within connect budget"
            error = cr.last_error
        else:
            connected_seconds = cr.connected_seconds
            if major_minor_mismatch(control.client_version, control.server_version):
                verdict = mlib.MISSION_ERROR
                reason = ("kRPC version mismatch client=%s server=%s"
                          % (control.client_version, control.server_version))
                error = reason
                log.error("Connect", "version mismatch client=%s server=%s -> %s"
                          % (control.client_version, control.server_version, mlib.MISSION_ERROR))
            else:
                connected = True
                state = spec.build_state(params)
                deadline = wall_start + float(budget)
                state, frames = fly_loop(control, state, spec.decide, log, deadline,
                                         clock=clock, sleep=sleep,
                                         allow_rails_warp=spec.allow_rails_warp,
                                         max_physics_warp=spec.max_physics_warp)
                phases_reached = list(state.phases_reached)
                outcomes = spec.evaluate(frames, params, state)
                for o in outcomes:
                    v = o.value
                    non_finite = isinstance(v, float) and not math.isfinite(v)
                    if non_finite:
                        log.warn("Assert", "assert %s non-finite value; unmet-for-now" % (o.name,))
                    log.info("Assert", "assert %s value=%s met=%s"
                             % (o.name, ("null" if non_finite else _fmt(v)), o.met))
                assertion_rows = [o.to_dict() for o in outcomes]
                verdict, reason = mlib.resolve_flight_verdict(state, outcomes)
    except Exception as exc:  # noqa: BLE001 -- caught so nothing escapes as a hang / unwritten result
        error = traceback.format_exc()
        # Recover the advanced machine state fly_loop attached to the exception
        # so the error result reports the phases the flight actually reached.
        lost_state = getattr(exc, "mission_state", None)
        if lost_state is not None and getattr(lost_state, "phases_reached", None):
            phases_reached = list(lost_state.phases_reached)
        if connected:
            # A drop / kRPC RPC error AFTER a successful connect is autopilot-flake
            # (retryable on a fresh boot); a non-kRPC internal error stays ERROR.
            exc_verdict = mlib.classify_post_connect_exception(
                type(exc).__module__, type(exc).__name__)
            if exc_verdict == mlib.MISSION_FLAKE:
                verdict = mlib.MISSION_FLAKE
                # Carry the exception MESSAGE, not just the type: an RPCError's
                # server-side text names the failing procedure/cause, and losing
                # it cost a full diagnose cycle on the first live B2 run.
                reason = ("connection dropped / kRPC error post-connect: %s: %s"
                          % (type(exc).__name__, str(exc)[:300]))
                log.warn("Verdict", "post-connect drop (%s: %s) -> %s"
                         % (type(exc).__name__, str(exc)[:300], mlib.MISSION_FLAKE))
            else:
                verdict = mlib.MISSION_ERROR
                reason = "unexpected exception in mission"
                log.error("Verdict", "unexpected post-connect exception -> %s"
                          % (mlib.MISSION_ERROR,))
        else:
            verdict = mlib.MISSION_ERROR
            reason = "unexpected exception in mission (pre-connect / setup)"
            log.error("Verdict", "unexpected pre-connect exception -> %s" % (mlib.MISSION_ERROR,))
    finally:
        closed = False
        try:
            control.close()
            closed = True
        except Exception as exc:  # noqa: BLE001
            log.warn("Connect", "disconnect error swallowed: %s" % (exc,))
        log.verbose("Connect", "disconnect (finally) closed=%s" % (closed,))

    wall_seconds = clock() - wall_start
    log.info("Verdict", "mission verdict=%s reason=%s phasesReached=%s wall=%ss"
             % (verdict, reason, phases_reached, _fmt(wall_seconds)))

    result = mlib.build_mission_result(
        mission=spec.name,
        verdict=verdict,
        reason=reason,
        phases_reached=phases_reached,
        connect_attempts=connect_attempts,
        connected_seconds=connected_seconds,
        rpc_port=rpc_port,
        assertions=assertion_rows,
        wall_seconds=round(wall_seconds, 3),
        krpc_client_version=str(getattr(control, "client_version", "") or ""),
        krpc_server_version=str(getattr(control, "server_version", "") or ""),
        error=error,
    )
    writer(result_path, mlib.serialize_mission_result(result))
    return 0 if verdict == mlib.MISSION_OK else 1


def _write_result_file(path: str, text: str) -> None:
    """Write the mission-result text via a temp-file + atomic replace, so a reader
    (run.py) never sees a half-written result. Creates the parent dir if needed."""
    parent = os.path.dirname(os.path.abspath(path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent, exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="ascii", newline="\n") as fh:
        fh.write(text)
    os.replace(tmp, path)


def _fmt(value) -> str:
    """Format a value for a log line: floats to 3dp, None as 'none', everything
    else via str. Kept locale-independent (no thousands separators)."""
    if value is None:
        return "none"
    if isinstance(value, float):
        if not math.isfinite(value):
            return "nan"
        return ("%.3f" % value)
    return str(value)


# ---------------------------------------------------------------------------
# CLI (design Mental Model: --params <json> --rpc-host --rpc-port --stream-port
# --result <path> --budget <s>). Shared by both mission shells.
# ---------------------------------------------------------------------------


def build_arg_parser(mission_name: str) -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog=mission_name,
        description="M-B1 flown mission %s (kRPC autopilot driver)." % mission_name)
    p.add_argument("--params", required=True,
                   help="missionParams block as a JSON object string")
    p.add_argument("--rpc-host", default="127.0.0.1", help="kRPC RPC host")
    p.add_argument("--rpc-port", type=int, default=50000, help="kRPC RPC port")
    p.add_argument("--stream-port", type=int, default=50001, help="kRPC stream port")
    p.add_argument("--result", required=True,
                   help="path to write the mission-result JSON")
    p.add_argument("--budget", type=float, required=True,
                   help="mission wall-clock budget in seconds (the mission-step budget)")
    return p


def main_from_spec(spec: MissionSpec, argv: Optional[List[str]] = None) -> int:
    """Parse the CLI, decode ``--params`` JSON, and fly ``spec``. This is the
    entry the two shells call from ``__main__``. A malformed ``--params`` is a
    MISSION-ERROR written to the result path (never an uncaught crash), so run.py
    still reads a verdict."""
    args = build_arg_parser(spec.name).parse_args(argv)
    log = MissionLogger(sink=_stdout_sink, clock=time.monotonic)
    log.info("Spawn", "mission start name=%s rpc=%s:%d stream=%d budget=%ss result=%s"
             % (spec.name, args.rpc_host, args.rpc_port, args.stream_port, _fmt(args.budget), args.result))
    try:
        params = json.loads(args.params)
        if not isinstance(params, dict):
            raise ValueError("--params must be a JSON object, got %s" % type(params).__name__)
    except Exception as exc:  # noqa: BLE001 -- a bad --params is MISSION-ERROR, not a crash
        log.error("Spawn", "bad --params: %s -> %s" % (exc, mlib.MISSION_ERROR))
        result = mlib.build_mission_result(
            mission=spec.name, verdict=mlib.MISSION_ERROR,
            reason="bad --params json", phases_reached=[], connect_attempts=0,
            connected_seconds=float("nan"), rpc_port=args.rpc_port, assertions=[],
            wall_seconds=0.0, krpc_client_version="", krpc_server_version="",
            error=traceback.format_exc())
        _write_result_file(args.result, mlib.serialize_mission_result(result))
        return 1
    return run_mission(spec, params, args.rpc_host, args.rpc_port, args.stream_port,
                       args.result, args.budget, log=log)
