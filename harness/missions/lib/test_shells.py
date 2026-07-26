"""Fake-telemetry integration tests for the M-B1 mission shells (design Test Plan
"Fake-telemetry integration").

These import the SHELL modules (``mission_runner`` / ``b1_pad_hop`` /
``b2_lko_ascent``) on the BASE interpreter with NO krpc installed -- proving the
lazy-import discipline (``import krpc`` lives inside
``KrpcMissionControl.open``, never at module top). A FAKE telemetry/control seam
replays a scripted flight; the pure ``mlib`` decisions drive it exactly as the
real kRPC seam would, so the shell control flow is exercised with no game.

IMPORT PATH: unittest discovery runs ``discover -s missions/lib`` from ``harness/``,
so ``missions/lib`` is on ``sys.path`` (this is how ``import mlib`` resolves). The
mission shells live one directory UP in ``missions/``; this test prepends that dir
to ``sys.path`` so ``import mission_runner`` / ``b1_pad_hop`` / ``b2_lko_ascent``
resolve. The shells themselves also do this bootstrap when run as subprocesses, so
the path handling is identical in both entry modes.

Each test names the regression it guards. NO krpc, NO KSP, NO network, NO real
filesystem write (an in-memory writer captures the result JSON).
"""

import math
import os
import sys
import threading
import time
import tomllib
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                    # noqa: E402  (missions/lib is the discovery root)
import mission_runner         # noqa: E402
import b1_pad_hop             # noqa: E402
import b2_lko_ascent          # noqa: E402
import b4_reentry             # noqa: E402
import b5_mun_flyby           # noqa: E402
import b6_minmus_flyby        # noqa: E402
import b7_duna_flyby          # noqa: E402
import b11_mun_orbit          # noqa: E402
import b12_minmus_orbit       # noqa: E402
import b13_mun_landing        # noqa: E402
import b14_minmus_landing     # noqa: E402
import b15_eve_flyby         # noqa: E402
import b16_eve_orbit         # noqa: E402
import forge_station          # noqa: E402
import forge_lko              # noqa: E402
import bdock_dock_transfer    # noqa: E402
import eva4_atmo_chute        # noqa: E402
import shutil                 # noqa: E402
import tempfile               # noqa: E402


# ---------------------------------------------------------------------------
# Test doubles: a deterministic clock, a no-op sleep, an in-memory result writer,
# and the fake telemetry/control seam.
# ---------------------------------------------------------------------------


class FakeClock:
    """Monotonic clock that advances a fixed step on every read, so budgets are
    deterministic and a runaway loop cannot wall for real time."""
    def __init__(self, step=0.001):
        self.t = 0.0
        self.step = step

    def __call__(self):
        self.t += self.step
        return self.t


class JumpClock:
    """Clock that jumps a large step each read, used to drive the connect-retry
    budget to expiry in a handful of iterations without real sleeping."""
    def __init__(self, step=5.0):
        self.t = 0.0
        self.step = step

    def __call__(self):
        self.t += self.step
        return self.t


class ResultSink:
    """In-memory mission-result writer (captures the serialized JSON instead of
    touching disk)."""
    def __init__(self):
        self.path = None
        self.text = None

    def __call__(self, path, text):
        self.path = path
        self.text = text


B1_PARAMS = {
    "throttle": 1.0,
    "apoapsisWindowMeters": {"min": 6000, "max": 30000},
    "chuteArmMaxRateMps": 30, "chuteFullDeployAltMeters": 2500,
    "landedSituations": ["LANDED", "SPLASHED"],
    "ascentTimeoutSeconds": 90,
    "coastTimeoutSeconds": 180,
    "descentTimeoutSeconds": 360,
}

# Read from the REAL committed spec rather than restated, so the fixture cannot drift
# from the window EVA-4 actually flies.
_EVA4_SPEC_PATH = os.path.join(_HARNESS, "scenarios", "EVA-4-atmo-chute.toml")
with open(_EVA4_SPEC_PATH, "rb") as _fh:
    EVA4_SHELL_PARAMS = tomllib.load(_fh)["driver"]["missionParams"]

B2_PARAMS = {
    "targetApoapsisMeters": 80000,
    "targetPeriapsisMeters": 80000,
    "apoErrorMeters": 5000,
    "periErrorMeters": 5000,
    "eccentricityMax": 0.02,
    "inclinationErrorDeg": 2.0,
    "ascentTimeoutSeconds": 420,
    "circularizeTimeoutSeconds": 300,
    "launchSiteLatitude": 0.0,
}

B5_PARAMS = {
    "targetApoapsisMeters": 80000,
    "targetPeriapsisMeters": 80000,
    "apoErrorMeters": 5000,
    "periErrorMeters": 5000,
    "ascentTimeoutSeconds": 420,
    "circularizeTimeoutSeconds": 300,
    "targetBodyName": "Mun",
    "homeBodyName": "Kerbin",
    "transferMinApoapsisMeters": 10000000,
    "courseCorrectPeriapsisMeters": 250000,
    "planTimeoutSeconds": 300,
    "planRetrySeconds": 10,
    "transferBurnTimeoutSeconds": 4000,
    "coastTimeoutSeconds": 400000,
    "flybyTimeoutSeconds": 300000,
    "coastWarpFactor": 6,
    "flybyWarpFactor": 5,
    "targetPeriapsisFloorMeters": 10000,
    "correctionTriggerAltsMeters": [0, 6000000],
    "maxCorrectionDvMps": 300,
    "flybyMaxWarpFactor": 6,
    "nodeArrivalMarginSeconds": 15,
    "planWarpFactor": 2,
    "soiLeadSeconds": 30,
    "flipPhysicsWarpFactor": 1,
}

# B7 spec-shaped params (mirrors harness/scenarios/B7-duna-flyby.toml): the
# shared B5 machine with the five interplanetary keys ON. The shell round-trip
# proves b5_params_from_dict parses viaBodyNames / returnBodyName /
# interplanetaryTransfer / ejectionEccFloor / correctionTriggerTimeToSoiSeconds.
B7_PARAMS = {
    "targetApoapsisMeters": 700000,
    "targetPeriapsisMeters": 700000,
    "apoErrorMeters": 150000,
    "periErrorMeters": 150000,
    "ascentTimeoutSeconds": 2400,
    "circularizeTimeoutSeconds": 6000,
    "targetBodyName": "Duna",
    "homeBodyName": "Kerbin",
    "transferMinApoapsisMeters": 0,
    "ejectionEccFloor": 1.05,
    "interplanetaryTransfer": True,
    "viaBodyNames": ["Sun"],
    "returnBodyName": "Sun",
    "courseCorrectPeriapsisMeters": 300000,
    "maxCorrectionDvMps": 200,
    "correctionTriggerAltsMeters": [],
    "correctionTriggerTimeToSoiSeconds": [20000000, 500000],
    "planTimeoutSeconds": 300,
    "planRetrySeconds": 10,
    "transferBurnTimeoutSeconds": 25000000,
    "coastTimeoutSeconds": 12000000,
    "flybyTimeoutSeconds": 500000,
    "coastWarpFactor": 7,
    "flybyWarpFactor": 5,
    "flybyMaxWarpFactor": 6,
    "nodeArrivalMarginSeconds": 15,
    "planWarpFactor": 2,
    "soiLeadSeconds": 30,
    "flipPhysicsWarpFactor": 1,
    "targetPeriapsisFloorMeters": 15000,
}

B4_PARAMS = {
    "targetApoapsisMeters": 80000,
    "targetPeriapsisMeters": 80000,
    "apoErrorMeters": 5000,
    "periErrorMeters": 5000,
    "ascentTimeoutSeconds": 420,
    "circularizeTimeoutSeconds": 300,
    "deorbitPeriapsisMeters": 25000,
    "retroSettleSeconds": 10,
    # DELIBERATE FIXTURE DIVERGENCE from the B4 spec's 70000 (see
    # MissionParamsMatchTheSpecsTests._VALUE_DIVERGENCES): the scripted
    # reentry frames below are written around a 45,000 m warp threshold, and
    # the spec value would leave the warp hop unfired in the fake flight.
    "warpAboveAltMeters": 45000,
    "warpHopSeconds": 120,
    "chuteDeployAltMeters": 3000,
    "deorbitTimeoutSeconds": 600,
    "reentryTimeoutSeconds": 3600,
    "descentTimeoutSeconds": 600,
    "landedSituations": ["LANDED", "SPLASHED"],
}


class FakeMissionControl(mission_runner.MissionControl):
    """Scripted telemetry/control seam. Replays ``snapshots`` in order; once
    exhausted it repeats the last one (the settled terminal frame) so the shell's
    settle-tail sees stable orbit data. Records every performed action and whether
    ``close`` ran. Optional connect refusal and a mid-flight raise cover the
    failure paths."""
    def __init__(self, snapshots, refuse_connect=False, raise_on_read_index=None,
                 raise_exc=None, client_version="0.5.4", server_version="0.5.4",
                 max_last_repeats=256):
        self._snaps = list(snapshots)
        self._i = 0
        # Bounded last-frame repeat: once the scripted frames are exhausted the
        # fake repeats the settled terminal frame for the shell's settle-tail, but
        # only up to ``max_last_repeats`` times. An UNBOUNDED repeat turns an
        # under-fed script (one that never drives the machine to a terminal) into a
        # multi-million-iteration spin -- the FakeClock advances only 0.001 s per
        # read, so a large mission budget would not wall for a very long time. When
        # the bound is exceeded read_snapshot raises EOFError ("end of the frame
        # stream"), which is a transport-drop name the fly loop re-raises, so an
        # under-fed script fails FAST and loudly instead of hanging. The bound is
        # far above any real settle tail (DEFAULT_SETTLE_FRAMES = 4).
        self._last_repeats = 0
        self._max_last_repeats = int(max_last_repeats)
        self._refuse = refuse_connect
        self._raise_at = raise_on_read_index
        # The exception raised at raise_on_read_index; defaults to a plain
        # RuntimeError (an internal non-kRPC bug). A test injects a connection-drop
        # exception to exercise the post-connect FLAKE classification (edge 5).
        self._raise_exc = raise_exc or RuntimeError("fake telemetry blew up mid-flight")
        self.client_version = client_version
        self.server_version = server_version
        self.actions = []
        self.reads = 0
        self.closed = False
        self.opened = False

    def open(self, host, rpc_port, stream_port):
        if self._refuse:
            raise ConnectionRefusedError("fake refuses connect at %s:%s" % (host, rpc_port))
        self.opened = True

    def read_snapshot(self):
        if self._raise_at is not None and self.reads == self._raise_at:
            self.reads += 1
            raise self._raise_exc
        self.reads += 1
        if self._i < len(self._snaps):
            snap = self._snaps[self._i]
            self._i += 1
            return snap
        self._last_repeats += 1
        if self._last_repeats > self._max_last_repeats:
            raise EOFError(
                "FakeMissionControl frame list exhausted: repeated the terminal "
                "frame %d times without the machine reaching a done state; the "
                "scripted frame list is under-fed for the current machine contract"
                % (self._max_last_repeats,))
        return self._snaps[-1]

    def perform(self, action):
        self.actions.append(action)

    def close(self):
        self.closed = True


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def run(spec, params, control, writer=None, budget=600.0, clock=None):
    writer = writer or ResultSink()
    clock = clock or FakeClock()
    log = mission_runner.MissionLogger(sink=lambda _l: None, clock=clock)
    code = mission_runner.run_mission(
        spec, params, "127.0.0.1", 50000, 50001, "unused/result.json", budget,
        control=control, log=log, clock=clock, sleep=lambda _s: None, writer=writer)
    result = mlib.parse_mission_result(writer.text)
    return code, result


class Eva4HandoffDisclosureEmitTests(unittest.TestCase):
    """Guards the OPERATOR-FACING half of the EVA-4 handoff disclosure (2026-07-26).

    The disclaimer has to ride the `[Verdict]` LOG LINE, not just the result JSON:
    `harness/status.py`'s _VERDICT_RE parses exactly that line and renders it live
    while the operator watches a flight, and run.py folds the same line into the
    harness log. An earlier revision applied the disclaimer inside
    `mlib.build_mission_result`, which runs AFTER the emit - so the JSON was honest
    and the line a human actually reads still said `reason=all telemetry assertions
    met`, byte-identical to the 2026-07-25 line over a dead kerbal.

    Fails if the disclosure moves back downstream of the emit, or leaks onto a
    mission that terminates on its own outcome."""

    def _fly(self, spec, params, frames):
        lines = []
        clock = FakeClock()
        log = mission_runner.MissionLogger(sink=lines.append, clock=clock)
        writer = ResultSink()
        mission_runner.run_mission(
            spec, params, "127.0.0.1", 50000, 50001, "unused/result.json", 600.0,
            control=FakeMissionControl(frames), log=log, clock=clock,
            sleep=lambda _s: None, writer=writer)
        verdict_lines = [l for l in lines if "mission verdict=" in l]
        self.assertEqual(1, len(verdict_lines), lines)
        return verdict_lines[0], mlib.parse_mission_result(writer.text)

    EVA4_FRAMES = [
        snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=19746, situation="PRE_LAUNCH"),
        snap(ut=1.0, stage_solid_fuel=0.0, apoapsis=19746, situation="FLYING"),
        snap(ut=2.0, vertical_speed=5.0, apoapsis=19746, situation="FLYING"),
        snap(ut=3.0, vertical_speed=-4.0, altitude=11962, apoapsis=19746,
             situation="FLYING"),
        snap(ut=4.0, vertical_speed=-18.0, altitude=1900, apoapsis=19746,
             situation="FLYING", craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        snap(ut=5.0, vertical_speed=-18.0, altitude=1598, apoapsis=19746,
             situation="FLYING", craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        snap(ut=6.0, vertical_speed=-18.0, altitude=1500, apoapsis=19746,
             situation="FLYING", craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
    ]

    def test_the_verdict_log_line_carries_the_disclaimer(self):
        params = dict(EVA4_SHELL_PARAMS)
        line, result = self._fly(eva4_atmo_chute.SPEC, params, self.EVA4_FRAMES)
        self.assertIn("verdict=MISSION-OK", line)
        self.assertIn("handoff mission", line)
        self.assertIn("kerbalSurvival", line)
        self.assertIn("EvaChuteDeploy", line)
        # The JSON agrees with the line (one source of truth, applied once).
        self.assertIn("handoff mission", result["reason"])
        self.assertEqual(["kerbalSurvival"], result["handoff"]["unverifiedByMission"])
        # ... and applied exactly ONCE, not re-applied by the builder.
        self.assertEqual(1, result["reason"].count("handoff mission"))

    def test_a_non_handoff_mission_log_line_is_untouched(self):
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
            snap(ut=1.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
            snap(ut=2.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=3.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=4.0, altitude=2000, apoapsis=14000, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
            snap(ut=5.0, altitude=100, apoapsis=14000, situation="LANDED",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        ]
        line, result = self._fly(b1_pad_hop.SPEC, B1_PARAMS, frames)
        self.assertIn("reason=all telemetry assertions met", line)
        self.assertNotIn("handoff", line)
        self.assertNotIn("handoff", result)


# ---------------------------------------------------------------------------
# Fake-kRPC happy path (B1 + B2).
# ---------------------------------------------------------------------------


class HappyPathTests(unittest.TestCase):
    def test_b1_happy_path_writes_mission_ok(self):
        """B1 flies pad -> ascent -> coast -> descent -> landed; all assertions met
        -> MISSION-OK, exit 0. Guards the shell mis-wiring the phase machine to the
        (fake) kRPC surface (chute never deployed, throttle never cut, no landing)."""
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
            snap(ut=1.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING"),
            snap(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),  # -> COAST (cut)
            snap(ut=3.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),  # -> DESCENT
            snap(ut=5.0, altitude=5000, apoapsis=14000, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),
            snap(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),                      # canopy open
            snap(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),                       # -> LANDED
        ]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b1_pad_hop")
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_SET_THROTTLE, kinds)
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, kinds)
        self.assertIn(mlib.ACTION_CUT_THROTTLE, kinds)
        self.assertIn(mlib.ACTION_DEPLOY_CHUTE, kinds)
        self.assertTrue(control.closed)
        self.assertEqual(result["phasesReached"][-1], mlib.B1_LANDED)
        names = {a["name"]: a["met"] for a in result["assertions"]}
        self.assertTrue(names["apoapsisWindow"])
        self.assertTrue(names["landedSituation"])

    def test_b2_happy_path_writes_mission_ok(self):
        """B2 flies prelaunch -> MJ-ascent -> circularize -> orbit; the settled
        orbit tail lets the K-consecutive debounce pass -> MISSION-OK. Guards the
        shell mis-wiring MechJeb actions or never settling the orbit assertions."""
        settled = snap(ut=200.0, apoapsis=80000, periapsis=80000, eccentricity=0.005,
                       inclination=0.3, situation="ORBITING")
        frames = [
            snap(ut=0.0, apoapsis=1000, periapsis=0, eccentricity=0.9, inclination=0.3, situation="PRE_LAUNCH"),
            snap(ut=100.0, apoapsis=78000, periapsis=1000, eccentricity=0.8, inclination=0.3,
                 situation="FLYING", mj_ascent_complete=True),  # latched -> CIRCULARIZE
            snap(ut=120.0, apoapsis=80000, periapsis=40000, eccentricity=0.3, inclination=0.3, situation="FLYING"),
            snap(ut=140.0, apoapsis=80000, periapsis=70000, eccentricity=0.1, inclination=0.3, situation="FLYING"),
            settled,  # periapsis 80000 -> ORBIT
        ]
        control = FakeMissionControl(frames)
        code, result = run(b2_lko_ascent.SPEC, B2_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, kinds)
        self.assertIn(mlib.ACTION_MJ_ENABLE_AUTOSTAGE, kinds)
        self.assertIn(mlib.ACTION_MJ_ENGAGE_ASCENT, kinds)
        self.assertIn(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION, kinds)
        self.assertEqual(result["phasesReached"][-1], mlib.B2_ORBIT)
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])


# ---------------------------------------------------------------------------
# Fake-kRPC connect failure -> MISSION-CONNECT-TIMEOUT.
# ---------------------------------------------------------------------------


class ConnectFailureTests(unittest.TestCase):
    def test_connect_refused_times_out_nonzero(self):
        """The fake refuses every connect; the shell exhausts the bounded retry and
        writes MISSION-CONNECT-TIMEOUT + nonzero exit. Guards a hang (no connect
        budget) or a spurious OK."""
        control = FakeMissionControl([snap()], refuse_connect=True)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control, clock=JumpClock(5.0))
        self.assertEqual(result["verdict"], mlib.MISSION_CONNECT_TIMEOUT)
        self.assertNotEqual(code, 0)
        self.assertGreaterEqual(result["connect"]["attempts"], 1)
        self.assertIsNotNone(result["error"])
        self.assertTrue(control.closed)  # close() still runs in the finally
        self.assertEqual(result["phasesReached"], [])  # never flew


# ---------------------------------------------------------------------------
# Fake-kRPC phase stall -> MISSION-FLAKE naming the phase.
# ---------------------------------------------------------------------------


class PhaseStallTests(unittest.TestCase):
    def test_ascent_stall_flakes_naming_ascent(self):
        """The fake never exhausts the SRB; ascent out-runs its phase budget (via
        the advancing telemetry UT) -> MISSION-FLAKE naming ASCENT. Guards a
        stalled autopilot wedging the mission instead of flaking."""
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"),
            snap(ut=30.0, stage_solid_fuel=1.0, situation="FLYING"),
            snap(ut=95.0, stage_solid_fuel=1.0, situation="FLYING"),  # 95 - 0 > 90 -> flake
        ]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE)
        self.assertNotEqual(code, 0)
        self.assertIn(mlib.B1_ASCENT, result["reason"])
        self.assertTrue(control.closed)

    def test_wall_budget_flake_when_telemetry_frozen(self):
        """A frozen telemetry UT never trips the mlib phase budget, but the shell's
        wall-clock deadline forces a MISSION-FLAKE naming the stuck phase. Guards
        the shell hanging on a stream that neither advances nor lands."""
        # Fuel present, UT frozen at 0: the mlib ascent budget never elapses.
        frozen = snap(ut=0.0, stage_solid_fuel=1.0, situation="FLYING")
        control = FakeMissionControl([snap(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"), frozen])
        # A tiny budget + a clock that advances past it within a few reads.
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control, budget=0.05, clock=FakeClock(step=0.02))
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE)
        self.assertNotEqual(code, 0)
        self.assertTrue(control.closed)


# ---------------------------------------------------------------------------
# Fake-kRPC exception mid-flight -> MISSION-ERROR + traceback + close in finally.
# ---------------------------------------------------------------------------


class MidFlightExceptionTests(unittest.TestCase):
    def test_one_off_read_exception_is_tolerated_not_fatal(self):
        """Seventh live B5 flight (2026-07-22): a server-ANSWERED read failure
        (a vessel-state RPC error at impact) must NOT kill the mission on the
        first raise -- the fly loop tolerates non-transport read exceptions so
        the control seam's read-fail streak can escalate to the vessel-lost
        terminal. A ONE-OFF such raise polls on and the flight completes."""
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
            snap(ut=1.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING"),
            snap(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
            snap(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
            snap(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        ]
        control = FakeMissionControl(frames, raise_on_read_index=1)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertTrue(control.closed)

    def test_raise_in_perform_writes_error_with_traceback(self):
        """An internal bug OUTSIDE the tolerated read path (perform raising a
        non-kRPC RuntimeError) still classifies MISSION-ERROR (edge 9), writes
        a traceback string, closes in the finally, exits nonzero. Guards an
        exception leaking as a hang or as no result file, and guards an
        internal bug being mis-filed as a flake."""
        class _PerformBoom(FakeMissionControl):
            def perform(self, action):
                raise RuntimeError("perform blew up mid-flight")

        frames = [snap(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"), snap()]
        control = _PerformBoom(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ERROR)
        self.assertNotEqual(code, 0)
        self.assertIsInstance(result["error"], str)
        self.assertIn("Traceback", result["error"])
        self.assertIn("blew up mid-flight", result["error"])
        self.assertTrue(control.closed)

    def test_connection_drop_mid_flight_is_flake_not_error(self):
        """SHOULD-FIX 4 (design edge 5): a CONNECTION-DROP exception raised AFTER a
        successful connect classifies MISSION-FLAKE (autopilot-flake bucket,
        retryable), NOT MISSION-ERROR. Guards a transient mid-flight socket reset
        poisoning the Parsek-defect bucket."""
        frames = [snap(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"), snap()]
        drop = ConnectionResetError("kRPC socket reset mid-burn")
        control = FakeMissionControl(frames, raise_on_read_index=1, raise_exc=drop)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE)
        self.assertNotEqual(code, 0)
        self.assertIn("ConnectionResetError", result["reason"])
        self.assertTrue(control.closed)


# ---------------------------------------------------------------------------
# Physics-warp guard (design edge 7).
# ---------------------------------------------------------------------------


class WarpGuardShellTests(unittest.TestCase):
    def test_physics_warp_mid_ascent_flakes_b1(self):
        """SHOULD-FIX 5 (design edge 7): a PHYSICS-warp frame mid-ascent (B1 flies 1x
        throughout) flakes the mission naming the phase, rather than record a warped
        (distorted) flight. Guards a stray high-warp request silently corrupting the
        recorded trajectory."""
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"),
            snap(ut=1.0, stage_solid_fuel=0.9, situation="FLYING",
                 warp_mode="PHYSICS", warp_rate=4.0),  # unexpected -> flake in ASCENT
        ]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE)
        self.assertNotEqual(code, 0)
        self.assertIn(mlib.B1_ASCENT, result["reason"])
        self.assertTrue(control.closed)

    def test_single_warp_spike_does_not_flake(self):
        """Fable review of the PR #1328 tail (SF-1/SF-2): the warp guard is
        DEBOUNCED to two CONSECUTIVE violating samples. One spike sample (a
        ramp crossing 1.0 read as mode NONE with rate above 1, or a
        frame-hitch rate blip) followed by a clean sample must NOT flake -- the
        B1 flight continues to LANDED and MISSION-OK."""
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"),
            snap(ut=1.0, stage_solid_fuel=0.9, situation="FLYING",
                 warp_mode="NONE", warp_rate=1.05),  # single ramp-race spike
            snap(ut=2.0, stage_solid_fuel=0.0, situation="FLYING",
                 altitude=3000.0, apoapsis=5000.0),                       # ASCENT->COAST
            snap(ut=10.0, situation="FLYING", altitude=4900.0, apoapsis=5000.0,
                 vertical_speed=-1.0),                                    # COAST->DESCENT
            snap(ut=20.0, situation="FLYING", altitude=1000.0, apoapsis=5000.0,
                 vertical_speed=-50.0,
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),            # canopy open
            snap(ut=40.0, situation="LANDED", altitude=100.0, apoapsis=5000.0,
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        ]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertNotEqual(result["verdict"], mlib.MISSION_FLAKE, result)
        self.assertIn(mlib.B1_LANDED, result["phasesReached"])

    def test_rails_warp_coast_does_not_flake_b2(self):
        """B2 permits RAILS warp on its exo-atmospheric coast (allow_rails_warp), so
        a RAILS-warp frame during the ascent/coast must NOT flake -- it flies through
        to ORBIT and MISSION-OK. Guards the guard over-firing on a legitimate B2
        rails coast (a clean run must be unaffected)."""
        settled = snap(ut=200.0, apoapsis=80000, periapsis=80000, eccentricity=0.005,
                       inclination=0.3, situation="ORBITING")
        frames = [
            snap(ut=0.0, apoapsis=1000, periapsis=0, eccentricity=0.9, inclination=0.3, situation="PRE_LAUNCH"),
            snap(ut=100.0, apoapsis=78000, periapsis=1000, eccentricity=0.8, inclination=0.3,
                 situation="FLYING", warp_mode="RAILS", warp_rate=50.0,
                 mj_ascent_complete=True),  # latched -> CIRCULARIZE, rails OK for B2
            snap(ut=120.0, apoapsis=80000, periapsis=40000, eccentricity=0.3, inclination=0.3, situation="FLYING"),
            snap(ut=140.0, apoapsis=80000, periapsis=70000, eccentricity=0.1, inclination=0.3, situation="FLYING"),
            settled,
        ]
        control = FakeMissionControl(frames)
        code, result = run(b2_lko_ascent.SPEC, B2_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["phasesReached"][-1], mlib.B2_ORBIT)


# ---------------------------------------------------------------------------
# On-connect ABI version check (design "Connection lifecycle" step 3).
# ---------------------------------------------------------------------------


class VersionCheckTests(unittest.TestCase):
    def test_major_minor_mismatch_aborts_error(self):
        """A server whose major/minor differs from the client aborts MISSION-ERROR
        before flying (no assertions attempted). Guards flying against a mismatched
        RPC surface."""
        control = FakeMissionControl([snap()], client_version="0.5.4", server_version="0.4.9")
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ERROR)
        self.assertNotEqual(code, 0)
        self.assertEqual(result["assertions"], [])
        self.assertTrue(control.closed)

    def test_major_minor_mismatch_pure_helper(self):
        self.assertFalse(mission_runner.major_minor_mismatch("0.5.4", "0.5.4"))
        self.assertFalse(mission_runner.major_minor_mismatch("0.5.4", "0.5.3"))  # patch diff ok
        self.assertTrue(mission_runner.major_minor_mismatch("0.5.4", "0.4.9"))
        self.assertTrue(mission_runner.major_minor_mismatch("0.5.4", ""))       # foreign / unknown
        self.assertTrue(mission_runner.major_minor_mismatch("bad", "0.5.4"))


# ---------------------------------------------------------------------------
# CLI / bad-params handling (design: a bad --params is MISSION-ERROR, not a crash).
# ---------------------------------------------------------------------------


class CliTests(unittest.TestCase):
    def test_bad_params_json_writes_error_result(self):
        """A malformed --params writes a MISSION-ERROR result (never an uncaught
        crash) so run.py still reads a verdict. Uses a real temp file to exercise
        the file writer path."""
        import tempfile
        with tempfile.TemporaryDirectory() as d:
            result_path = os.path.join(d, "r.json")
            argv = ["--params", "{not json", "--result", result_path, "--budget", "10"]
            code = b1_pad_hop.main(argv)
            self.assertNotEqual(code, 0)
            with open(result_path, "r", encoding="ascii") as fh:
                result = mlib.parse_mission_result(fh.read())
            self.assertEqual(result["verdict"], mlib.MISSION_ERROR)

    def test_arg_parser_matches_design_cli(self):
        """The CLI carries exactly the design's flags."""
        p = mission_runner.build_arg_parser("b1_pad_hop")
        args = p.parse_args([
            "--params", "{}", "--rpc-host", "127.0.0.1", "--rpc-port", "50000",
            "--stream-port", "50001", "--result", "x.json", "--budget", "600"])
        self.assertEqual(args.rpc_host, "127.0.0.1")
        self.assertEqual(args.rpc_port, 50000)
        self.assertEqual(args.stream_port, 50001)
        self.assertEqual(args.budget, 600.0)

    def test_shells_have_no_module_top_krpc_import(self):
        """Regression for the lazy-import discipline: importing the shells on the
        base interpreter must NOT have imported krpc (it is lazy inside open())."""
        # The shells imported at module load above (including forge_station,
        # forge_lko and bdock_dock_transfer); krpc must not be present.
        self.assertNotIn("krpc", sys.modules)

    def test_arg_parser_accepts_optional_seam_args(self):
        """The seam-bridge CLI args are optional (default None) and do not break
        the pre-B-DOCK missions that never pass them."""
        p = mission_runner.build_arg_parser("bdock_dock_transfer")
        args = p.parse_args([
            "--params", "{}", "--result", "x.json", "--budget", "600"])
        self.assertIsNone(args.seam_commands)
        self.assertIsNone(args.seam_commit_id)
        args2 = p.parse_args([
            "--params", "{}", "--result", "x.json", "--budget", "600",
            "--seam-commands", "c.txt", "--seam-responses", "r.txt",
            "--seam-commit-id", "0003"])
        self.assertEqual(args2.seam_commit_id, "0003")


# ---------------------------------------------------------------------------
# KrpcMissionControl read-fail streak -> vessel_lost snapshot (design "First live
# B1 flown-mission run": vessel-destroyed terminal).
#
# The FakeMissionControl above overrides read_snapshot wholesale, so it does NOT
# exercise the REAL KrpcMissionControl.read_snapshot try/except streak logic. These
# cells drive the real wrapper with a minimal fake kRPC ``_conn`` (a compact stand-in
# for the space_center -> active_vessel -> orbit/flight/resources chain the body
# reads) so the streak progression is covered directly: 2 failures re-raise, the 3rd
# yields a vessel_lost snapshot, and a successful read resets the streak.
# ---------------------------------------------------------------------------


class _FakeFlight:
    surface_altitude = 100.0
    vertical_speed = -1.0


class _FakeBody:
    reference_frame = "body_frame"
    name = "Kerbin"


class _FakeOrbit:
    apoapsis_altitude = 5000.0
    periapsis_altitude = 1000.0
    eccentricity = 0.1
    inclination = 0.0
    body = _FakeBody()
    # No SOI change on the trajectory: kRPC returns NaN (the machine's
    # SOI-approach warp bound skips it, fail open).
    time_to_soi_change = float("nan")


class _FakeSituation:
    name = "flying"


class _FakeResources:
    def amount(self, _name):
        return 1.0


class _FakeNodeControl:
    nodes = ()
    throttle = 0.0


class _FakeParachuteState:
    # kRPC hands back an enum whose .name is lower_snake; mlib normalizes it.
    name = "deployed"


class _FakeParachute:
    state = _FakeParachuteState()


class _FakeParts:
    parachutes = [_FakeParachute()]


class _FakeVessel:
    situation = _FakeSituation()
    orbit = _FakeOrbit()
    resources = _FakeResources()
    control = _FakeNodeControl()
    parts = _FakeParts()
    available_thrust = 215_000.0

    def flight(self, _frame):
        return _FakeFlight()


class _FakeSpaceCenter:
    """A stand-in for the kRPC SpaceCenter. ``ut`` is always readable (so the
    vessel-lost snapshot carries a real UT); ``active_vessel`` consumes the parent
    conn's per-read script and RAISES on a scripted failure (the realistic shape of
    a destroyed craft: sc is fine, the active vessel handle is invalid)."""
    ut = 42.0
    warp_rate = 1.0
    warp_mode = None

    def __init__(self, conn):
        self._conn = conn

    @property
    def active_vessel(self):
        if not self._conn._consume_ok():
            raise RuntimeError("active vessel invalid (handed to debris)")
        return _FakeVessel()


class _FakeConn:
    """Minimal kRPC connection: ``space_center`` is plain (does not consume the
    script), only ``active_vessel`` does, so the vessel-lost UT re-read still works.
    ``results[i]`` True => read i succeeds, False => raises."""
    def __init__(self, results):
        self._results = list(results)
        self._i = 0
        self.space_center = _FakeSpaceCenter(self)

    def _consume_ok(self):
        ok = self._results[self._i] if self._i < len(self._results) else True
        self._i += 1
        return ok


class _FakeWarpSpaceCenter:
    """Stand-in for the warp connection's space_center: warp_to blocks on a
    gate like the real RPC blocks on the server, then raises when the fake
    socket was closed under it (the real cancel path)."""

    def __init__(self, gate, conn):
        self._gate = gate
        self._conn = conn
        self.warped_to = []

    def warp_to(self, ut):
        self.warped_to.append(float(ut))
        self._gate.wait(timeout=5.0)
        if self._conn.closed:
            raise ConnectionAbortedError("warp socket closed")


class _FakeWarpConn:
    def __init__(self, gate):
        self.closed = False
        self._gate = gate
        self.space_center = _FakeWarpSpaceCenter(gate, self)

    def close(self):
        self.closed = True
        self._gate.set()


class _FakePrimarySc:
    """Primary-connection space_center stand-in recording the post-cancel
    factor resets."""
    def __init__(self):
        self.rails_sets = []
        self.physics_sets = []

    @property
    def rails_warp_factor(self):
        return 0

    @rails_warp_factor.setter
    def rails_warp_factor(self, value):
        self.rails_sets.append(int(value))

    @property
    def physics_warp_factor(self):
        return 0

    @physics_warp_factor.setter
    def physics_warp_factor(self, value):
        self.physics_sets.append(int(value))


def _wait_until(pred, timeout=2.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if pred():
            return True
        time.sleep(0.01)
    return pred()


class WarpServiceTests(unittest.TestCase):
    """Headless WarpService contract tests over an injected fake connection:
    the daemon thread owns its own connection, active/target state reads
    correctly, cancel closes the socket + zeroes the primary factors, natural
    completion goes idle, and a thread exception never propagates."""

    def _service(self, gate):
        conn = _FakeWarpConn(gate)
        svc = mission_runner.WarpService(
            "127.0.0.1", 50000, 50001, connect_fn=lambda: conn)
        return svc, conn

    def test_warp_to_ut_is_fire_and_forget_and_exposes_target(self):
        gate = threading.Event()
        svc, conn = self._service(gate)
        try:
            svc.warp_to_ut(12345.0, _FakePrimarySc())
            self.assertTrue(_wait_until(
                lambda: conn.space_center.warped_to == [12345.0]))
            self.assertTrue(svc.active)
            self.assertEqual(svc.target_ut, 12345.0)
        finally:
            gate.set()
            svc.close()

    def test_natural_completion_goes_idle(self):
        gate = threading.Event()
        svc, conn = self._service(gate)
        svc.warp_to_ut(500.0, _FakePrimarySc())
        self.assertTrue(_wait_until(lambda: len(conn.space_center.warped_to) == 1))
        gate.set()  # the RPC returns (arrival)
        self.assertTrue(_wait_until(lambda: not svc.active))
        self.assertTrue(math.isnan(svc.target_ut))

    def test_cancel_closes_socket_and_zeroes_factors(self):
        gate = threading.Event()
        svc, conn = self._service(gate)
        sc = _FakePrimarySc()
        svc.warp_to_ut(9999.0, sc)
        self.assertTrue(_wait_until(lambda: len(conn.space_center.warped_to) == 1))
        svc.cancel(sc)
        self.assertTrue(conn.closed)
        self.assertFalse(svc.active)
        self.assertTrue(math.isnan(svc.target_ut))
        self.assertEqual(sc.rails_sets, [0])
        self.assertEqual(sc.physics_sets, [0])

    def test_connect_failure_never_raises_into_caller(self):
        svc = mission_runner.WarpService(
            "127.0.0.1", 50000, 50001,
            connect_fn=lambda: (_ for _ in ()).throw(OSError("no server")))
        sc = _FakePrimarySc()
        svc.warp_to_ut(777.0, sc)  # must not raise
        self.assertTrue(_wait_until(lambda: not svc.active))
        self.assertTrue(math.isnan(svc.target_ut))
        svc.close()


class WarpStallTrackerTests(unittest.TestCase):
    """Pure watchdog core: UT standstill for the wall deadline = stall; any
    UT advance re-arms; non-finite UT counts as no-advance (fail closed
    toward detection); reset clears history."""

    def test_advancing_ut_never_stalls(self):
        t = mission_runner.WarpStallTracker(stall_seconds=10.0)
        self.assertFalse(t.update(0.0, 100.0))
        self.assertFalse(t.update(5.0, 5100.0))
        self.assertFalse(t.update(20.0, 155100.0))

    def test_frozen_ut_stalls_after_deadline(self):
        t = mission_runner.WarpStallTracker(stall_seconds=10.0)
        self.assertFalse(t.update(0.0, 100.0))
        self.assertFalse(t.update(5.0, 100.0))      # 5 s standstill: not yet
        self.assertTrue(t.update(10.0, 100.0))      # 10 s standstill: stall
        # An advance re-arms.
        self.assertFalse(t.update(11.0, 200.0))
        self.assertFalse(t.update(15.0, 200.0))
        self.assertTrue(t.update(21.5, 200.0))

    def test_nan_ut_counts_as_no_advance(self):
        t = mission_runner.WarpStallTracker(stall_seconds=10.0)
        self.assertFalse(t.update(0.0, 100.0))
        self.assertFalse(t.update(6.0, float("nan")))
        self.assertTrue(t.update(10.0, float("nan")))

    def test_reset_clears_history(self):
        t = mission_runner.WarpStallTracker(stall_seconds=10.0)
        self.assertFalse(t.update(0.0, 100.0))
        t.reset()
        self.assertFalse(t.update(30.0, 100.0))     # fresh baseline, no stall
        self.assertTrue(t.update(40.0, 100.0))


class ReadFailStreakTests(unittest.TestCase):
    def _control(self, results):
        ctrl = mission_runner.KrpcMissionControl()
        ctrl._conn = _FakeConn(results)
        ctrl._ascent = None
        return ctrl

    def test_two_failures_reraise_third_yields_vessel_lost(self):
        """Below the streak limit a read failure re-raises (the existing transient
        path); the 3rd consecutive failure emits a vessel_lost snapshot (UT still
        readable) instead of re-raising, so the phase machine reaches its terminal."""
        ctrl = self._control([False, False, False])
        with self.assertRaises(Exception):
            ctrl.read_snapshot()  # streak 1 -> re-raise
        with self.assertRaises(Exception):
            ctrl.read_snapshot()  # streak 2 -> re-raise
        snap = ctrl.read_snapshot()  # streak 3 -> vessel_lost
        self.assertTrue(snap.vessel_lost)
        self.assertEqual(snap.ut, 42.0)

    def test_successful_read_resets_streak(self):
        """A successful read clears the streak, so a later pair of failures re-raises
        again (not a spurious vessel_lost from an accumulated cross-run count)."""
        ctrl = self._control([False, False, True, False, False, False])
        with self.assertRaises(Exception):
            ctrl.read_snapshot()  # streak 1
        with self.assertRaises(Exception):
            ctrl.read_snapshot()  # streak 2
        good = ctrl.read_snapshot()  # success -> streak resets to 0
        self.assertFalse(good.vessel_lost)
        with self.assertRaises(Exception):
            ctrl.read_snapshot()  # streak 1 again
        with self.assertRaises(Exception):
            ctrl.read_snapshot()  # streak 2 again
        lost = ctrl.read_snapshot()  # streak 3 -> vessel_lost
        self.assertTrue(lost.vessel_lost)


if __name__ == "__main__":
    unittest.main()


class ReadWarpStateTests(unittest.TestCase):
    """Fable review of the PR #1328 tail (SF-1): warp mode and rate derive from
    ONE rate sample, so mode NONE with rate above 1 can no longer be produced
    by a two-RPC race inside the runner itself."""

    class _FakeSc:
        def __init__(self, rate, mode_name):
            self.warp_rate = rate
            self.warp_mode = type("M", (), {"name": mode_name})()

    def test_rate_at_or_below_one_is_none(self):
        mode, rate = mission_runner._read_warp_state(self._FakeSc(0.99, "PHYSICS"))
        self.assertEqual(mode, "NONE")
        self.assertEqual(rate, 0.99)

    def test_rate_above_one_classifies_mode(self):
        mode, rate = mission_runner._read_warp_state(self._FakeSc(4.12, "PHYSICS"))
        self.assertEqual((mode, rate), ("PHYSICS", 4.12))
        mode, rate = mission_runner._read_warp_state(self._FakeSc(50.0, "Rails"))
        self.assertEqual((mode, rate), ("RAILS", 50.0))

    def test_unreadable_surface_reports_none_1x(self):
        class Boom:
            @property
            def warp_rate(self):
                raise RuntimeError("dead connection")
        self.assertEqual(mission_runner._read_warp_state(Boom()), ("NONE", 1.0))


# ---------------------------------------------------------------------------
# B1 DOWN terminal through the shell (operator decision 2026-07-20, option A):
# a chute-deployed touchdown breakup is MISSION-OK, and the settle tail is
# skipped (a DOWN terminal means the vessel is gone -- the tail would only
# gather vessel_lost / garbage frames).
# ---------------------------------------------------------------------------


# The scripted B1 flight up to a DESCENT under an OBSERVED canopy; a vessel_lost
# frame appended to it produces the DOWN terminal, a LANDED frame the classic
# landing. The chute is ARMED on the apoapsis-crossing frame (|vs| 5 <= 30) and the
# canopy READS open on the frames after it -- the observed latch, not the commanded
# one, is what the DOWN gate needs.
_B1_DESCENT_WITH_CHUTE_FRAMES = [
    snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
    snap(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),   # -> COAST
    snap(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),    # -> DESCENT + arm
    snap(ut=6.0, altitude=2000.0, vertical_speed=-30.0, apoapsis=14000,
         situation="FLYING",
         craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),
    # Below downMaxAltMeters (500): the DOWN eligibility gate's "reached the
    # ground" leg (SF-1) needs the last finite altitude near the surface.
    # TWO consecutive Deployed reads: the latch is B1_CANOPY_DEBOUNCE_K debounced, so a
    # single frame must not earn it (stock flips the state at the START of the ~8 s
    # canopy animation).
    snap(ut=7.0, altitude=900.0, vertical_speed=-12.0, apoapsis=14000,
         situation="FLYING",
         craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),                        # canopy open
    snap(ut=7.5, altitude=60.0, vertical_speed=-9.0, apoapsis=14000,
         situation="FLYING",
         craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
]


class DownTerminalShellTests(unittest.TestCase):
    def test_b1_down_terminal_is_mission_ok_and_skips_settle_tail(self):
        """A craft that breaks apart at a canopy-borne touchdown, so the runner
        emits a vessel_lost snapshot -- with the canopy OBSERVED open that is the
        DOWN SUCCESS terminal: MISSION-OK, exit 0, landedSituation met naming the
        DOWN end, and NO settle-tail reads (the vessel is gone). Guards the original
        behavior (every live B1 run ASSERT-FAILed at touchdown) from coming back,
        and pairs with test_b1_commanded_only_chute_is_assert_fail_through_shell,
        which guards the OPPOSITE over-correction."""
        frames = _B1_DESCENT_WITH_CHUTE_FRAMES + [snap(ut=8.0, vessel_lost=True)]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["phasesReached"][-1], mlib.B1_DOWN)
        # Settle tail SKIPPED: exactly one read per scripted frame, none after done.
        self.assertEqual(control.reads, len(frames))
        names = {a["name"]: a for a in result["assertions"]}
        self.assertTrue(names["apoapsisWindow"]["met"])
        sit = names["landedSituation"]
        self.assertTrue(sit["met"])
        self.assertEqual(sit["value"], "DOWN(canopy-borne impact)")
        self.assertTrue(sit["downTerminal"])
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_DEPLOY_CHUTE, kinds)
        self.assertTrue(control.closed)

    def test_b1_landed_terminal_keeps_settle_tail(self):
        """Contrast cell: a SURVIVING craft (classic LANDED terminal) still
        samples the settle tail -- reads = scripted frames + settle frames.
        Guards the skip from over-firing on the healthy landing path."""
        frames = _B1_DESCENT_WITH_CHUTE_FRAMES + [
            snap(ut=8.0, altitude=0.0, apoapsis=14000, situation="LANDED")]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(result["phasesReached"][-1], mlib.B1_LANDED)
        self.assertEqual(control.reads,
                         len(frames) + mission_runner.DEFAULT_SETTLE_FRAMES)
        sit = {a["name"]: a for a in result["assertions"]}["landedSituation"]
        self.assertEqual(sit["value"], "LANDED")
        self.assertFalse(sit["downTerminal"])

    def test_b1_single_deployed_frame_is_not_a_canopy_through_shell(self):
        """F2: the K=2 debounce, asserted END TO END on the DOWN path rather than only
        as a state field. Drop the fixture's first Deployed frame so exactly ONE
        qualifying read reaches the machine before the loss: that must NOT earn the
        canopy, so DOWN is refused and the run reds."""
        frames = _B1_DESCENT_WITH_CHUTE_FRAMES[:-2] + [
            _B1_DESCENT_WITH_CHUTE_FRAMES[-1],          # a single Deployed frame
            snap(ut=8.0, vessel_lost=True)]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotIn(mlib.B1_DOWN, result["phasesReached"])
        self.assertNotEqual(code, 0)

    def test_b1_arm_evidence_reaches_the_result_json(self):
        """F3: the arm altitude / rate are the COMMANDED half of the distinction this
        whole scenario is about, and their predecessor was already dead once - written
        on every arm and read by nothing while its comment claimed otherwise. Deleting
        the detail keys or the shell wiring left the suite green, so pin the surface."""
        frames = list(_B1_DESCENT_WITH_CHUTE_FRAMES) + [
            snap(ut=8.0, altitude=0.0, apoapsis=14000, situation="LANDED",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED)]
        control = FakeMissionControl(frames)
        _, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        row = {a["name"]: a for a in result["assertions"]}["craftCanopyObserved"]
        self.assertTrue(row["met"])
        # The arm rode the ut=4.0 frame (vs -5.0, inside the 30 m/s bound).
        self.assertTrue(row["armCommanded"])
        self.assertEqual(row["armCommandedRate"], -5.0)
        self.assertEqual(row["armMaxRate"], float(B1_PARAMS["chuteArmMaxRateMps"]))
        self.assertEqual(row["fullDeployAltitude"],
                         float(B1_PARAMS["chuteFullDeployAltMeters"]))
        self.assertEqual(row["debounceK"], mlib.B1_CANOPY_DEBOUNCE_K)

    def test_b1_lost_without_chute_is_assert_fail_through_shell(self):
        """A vessel lost in DESCENT BEFORE the chute deployed stays a failed
        mission through the whole shell path (loss_reason short-circuits the
        met assertions)."""
        frames = _B1_DESCENT_WITH_CHUTE_FRAMES[:3] + [  # never reaches chute alt
            snap(ut=6.0, altitude=5000.0, vertical_speed=-40.0, apoapsis=14000,
                 situation="FLYING"),
            snap(ut=8.0, vessel_lost=True)]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertIn("vessel-lost", result["reason"])
        self.assertNotIn(mlib.B1_DOWN, result["phasesReached"])

    def test_b1_commanded_only_chute_is_assert_fail_through_shell(self):
        """END-TO-END regression for the 2026-07-25 fix, and the exact shape of every
        B1 run flown before it: the machine ARMS the chute at the apoapsis crossing,
        stock never opens it (the canopy reads Armed for the whole fall), and the
        craft breaks up on a terminal-velocity ground impact. The old DOWN gate read
        the COMMANDED latch and certified this MISSION-OK. It must now ASSERT-FAIL
        through the whole shell, with a reason that names the inert canopy."""
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
            snap(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
            snap(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=6.0, altitude=2382.0, vertical_speed=-301.0, apoapsis=14000,
                 situation="FLYING", craft_chute_state=mlib.CHUTE_STATE_ARMED),
            snap(ut=7.5, altitude=60.0, vertical_speed=-301.0, apoapsis=14000,
                 situation="FLYING", craft_chute_state=mlib.CHUTE_STATE_ARMED),
            snap(ut=8.0, vessel_lost=True),
        ]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertNotIn(mlib.B1_DOWN, result["phasesReached"])
        self.assertIn("craftChute=Armed", result["reason"])
        # The chute WAS commanded -- that is precisely why the commanded latch is
        # worthless as evidence and the observed one is the gate.
        self.assertIn(mlib.ACTION_DEPLOY_CHUTE, [a.kind for a in control.actions])

    def test_b1_landed_without_canopy_fails_the_canopy_assertion(self):
        """The LANDED counterpart: a craft that survives to a landed situation with a
        chute that never opened still fails, on craftCanopyObserved. Guards the
        assertion being quietly folded into the DOWN path only."""
        frames = [
            snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
            snap(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
            snap(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED",
                 craft_chute_state=mlib.CHUTE_STATE_ARMED),
        ]
        control = FakeMissionControl(frames)
        code, result = run(b1_pad_hop.SPEC, B1_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertEqual(result["phasesReached"][-1], mlib.B1_LANDED)
        names = {a["name"]: a["met"] for a in result["assertions"]}
        self.assertTrue(names["landedSituation"])
        self.assertFalse(names["craftCanopyObserved"])

    def test_read_chute_flag_actually_populates_the_snapshot(self):
        """BEHAVIOURAL companion to the constructor-flag test: asserting the flag is
        set does not prove the read site honours it. Reviewers showed that stubbing
        the read site to `if False:` - the same broken chain one layer down - left the
        whole suite green. This drives the real KrpcMissionControl.read_snapshot."""
        conn = _FakeConn([True])
        on = mission_runner.KrpcMissionControl(read_chute=True)
        on._conn = conn
        self.assertEqual(on.read_snapshot().craft_chute_state,
                         mlib.CHUTE_STATE_DEPLOYED)
        # Opted out: the "" unread sentinel, which fails every chute gate closed.
        off = mission_runner.KrpcMissionControl(read_chute=False)
        off._conn = _FakeConn([True])
        self.assertEqual(off.read_snapshot().craft_chute_state, "")

    def test_b1_control_opts_into_the_chute_read(self):
        """read_chute=True is the single line that makes the observed-canopy chain
        real: without it every frame carries the "" unread sentinel and
        craftCanopyObserved can never be met. Flipping it to False used to leave the
        whole suite green (reviewers found this gap), so it is asserted directly."""
        control = b1_pad_hop.make_control()
        self.assertTrue(control._read_chute)
        self.assertFalse(control._use_mechjeb)   # B1 is raw kRPC, no MechJeb

    def test_b1_arm_carries_the_raised_full_deploy_altitude(self):
        """The arm must ride SET_CHUTE_DEPLOY_ALTITUDE on the same frame, carrying the
        spec's chuteFullDeployAltMeters -- otherwise the module's first ACTIVE
        FixedUpdate sees the fixture's persisted altitude instead of the declared one."""
        control = FakeMissionControl(list(_B1_DESCENT_WITH_CHUTE_FRAMES) + [
            snap(ut=8.0, altitude=0.0, apoapsis=14000, situation="LANDED",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED)])
        run(b1_pad_hop.SPEC, B1_PARAMS, control)
        alt_actions = [a for a in control.actions
                       if a.kind == mlib.ACTION_SET_CHUTE_DEPLOY_ALTITUDE]
        self.assertEqual(len(alt_actions), 1)
        self.assertEqual(alt_actions[0].value,
                         float(B1_PARAMS["chuteFullDeployAltMeters"]))


# ---------------------------------------------------------------------------
# B4 reentry+splashdown through the shell.
# ---------------------------------------------------------------------------


class B4ShellTests(unittest.TestCase):
    def _happy_frames(self):
        return [
            snap(ut=0.0, apoapsis=1000, periapsis=0, situation="PRE_LAUNCH"),
            snap(ut=100.0, apoapsis=78000, periapsis=1000, situation="FLYING",
                 mj_ascent_complete=True),                       # -> CIRCULARIZE
            snap(ut=140.0, apoapsis=80000, periapsis=79000, altitude=79000.0,
                 situation="ORBITING"),                          # -> ORBIT
            snap(ut=141.0, apoapsis=80001, periapsis=79000, altitude=79001.0,
                 situation="ORBITING"),                          # ORBIT -> DEORBIT (retro AP)
            snap(ut=155.0, apoapsis=80002, periapsis=79000.5, altitude=79002.0,
                 situation="ORBITING", ap_error=2.0),            # settled + aligned -> throttle up
            snap(ut=170.0, apoapsis=80002, periapsis=24000, altitude=79000.0,
                 situation="ORBITING"),                          # -> REENTRY (cut+release+stage)
            snap(ut=180.0, apoapsis=80002, periapsis=24000, altitude=70000.0,
                 vertical_speed=-100.0, situation="SUB_ORBITAL"),  # warp hop
            snap(ut=300.0, apoapsis=80002, periapsis=24000, altitude=40000.0,
                 vertical_speed=-400.0, situation="SUB_ORBITAL"),  # below threshold: poll
            snap(ut=400.0, apoapsis=80002, periapsis=24000, altitude=2500.0,
                 vertical_speed=-150.0, situation="FLYING"),     # -> SPLASHDOWN + chute
            snap(ut=500.0, apoapsis=80002, altitude=0.0, situation="SPLASHED"),  # terminal
        ]

    def test_b4_happy_path_writes_mission_ok(self):
        """B4 flies ascent -> orbit -> deorbit -> reentry -> splashdown; the
        settle tail runs on the SPLASHED terminal and all four assertions are
        met -> MISSION-OK. Guards the shell mis-wiring the new AP/warp actions
        or terminating at ORBIT like B2."""
        control = FakeMissionControl(self._happy_frames())
        code, result = run(b4_reentry.SPEC, B4_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b4_reentry")
        self.assertEqual(result["phasesReached"][-1], mlib.B4_SPLASHDOWN)
        self.assertIn(mlib.B4_ORBIT, result["phasesReached"])
        kinds = [a.kind for a in control.actions]
        for kind in (mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_AP_POINT_RETROGRADE,
                     mlib.ACTION_SET_THROTTLE, mlib.ACTION_CUT_THROTTLE,
                     mlib.ACTION_AP_DISENGAGE, mlib.ACTION_WARP_TO,
                     mlib.ACTION_DEPLOY_CHUTE):
            self.assertIn(kind, kinds)
        # The warp hop carried an ABSOLUTE target UT = frame ut + hop seconds.
        warps = [a for a in control.actions if a.kind == mlib.ACTION_WARP_TO]
        self.assertEqual(warps, [mlib.Action(mlib.ACTION_WARP_TO, 180.0 + 120.0)])
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])
        # Settle tail RAN (SPLASHDOWN keeps it): more reads than scripted frames.
        self.assertGreater(control.reads, len(self._happy_frames()))
        self.assertTrue(control.closed)

    def test_b4_vessel_lost_mid_reentry_is_assert_fail(self):
        """B4's survival contract: a vessel_lost snapshot during REENTRY (burned
        up) is MISSION-ASSERT-FAIL even though the ascent went perfectly -- no
        B1-style DOWN success end exists here."""
        frames = self._happy_frames()[:7] + [snap(ut=200.0, vessel_lost=True)]
        control = FakeMissionControl(frames)
        code, result = run(b4_reentry.SPEC, B4_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertIn("vessel-lost", result["reason"])
        self.assertIn(mlib.B4_REENTRY, result["phasesReached"])
        self.assertTrue(control.closed)


class B5ShellTests(unittest.TestCase):
    """B5 shell wiring over the fake seam: ascent -> transfer plan/burn ->
    correction -> cross-SOI coast -> flyby -> RETURN terminal, with the new
    target/plan/execute actions and the body-name SOI gates flowing end to end."""

    def _happy_frames(self):
        return [
            snap(ut=0.0, apoapsis=1000, periapsis=0, situation="PRE_LAUNCH",
                 body="Kerbin"),
            snap(ut=100.0, apoapsis=78000, periapsis=1000, situation="FLYING",
                 mj_ascent_complete=True, body="Kerbin"),        # -> CIRCULARIZE
            snap(ut=140.0, apoapsis=80000, periapsis=79000, altitude=79000.0,
                 situation="ORBITING", body="Kerbin"),           # -> ORBIT
            snap(ut=141.0, apoapsis=80001, periapsis=79000, altitude=79001.0,
                 situation="ORBITING", body="Kerbin"),           # ORBIT -> PLAN (target+plan)
            snap(ut=150.0, apoapsis=80001, periapsis=79000, altitude=79002.0,
                 situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> TRANSFER-BURN (execute)
            snap(ut=2200.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=90000.0, situation="ORBITING", body="Kerbin",
                 node_count=0),                                  # burn done -> COAST
            snap(ut=2210.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=93000.0, situation="ORBITING",
                 body="Kerbin"),                                 # trigger 0 -> PLAN-CORRECTION (round 1)
            snap(ut=2230.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=95000.0, situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> CORRECTION-BURN (AP point)
            snap(ut=2245.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=97000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=100.0, ap_error=1.0),     # streak 1 -> flip physics warp
            snap(ut=2300.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=99000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=100.0, ap_error=1.0,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop physics warp
            snap(ut=2302.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=99500.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=100.0, ap_error=1.0),     # warp NONE -> throttle
            snap(ut=2400.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=200_000.0, situation="ORBITING", body="Kerbin"),  # node gone -> cut pair, COAST
            snap(ut=8000.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=6_500_000.0, situation="ORBITING",
                 body="Kerbin"),                                 # trigger 6M -> PLAN-CORRECTION (round 2)
            snap(ut=8010.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=6_510_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> CORRECTION-BURN (AP point)
            snap(ut=8025.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=6_520_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=4.0, ap_error=0.8),       # streak 1 -> flip physics warp
            snap(ut=8030.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=6_525_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=4.0, ap_error=0.7,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop physics warp
            snap(ut=8035.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=6_530_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=4.0, ap_error=0.7),       # warp NONE -> throttle
            snap(ut=8100.0, apoapsis=11_500_000.0, periapsis=79000,
                 altitude=6_600_000.0, situation="ORBITING", body="Kerbin",
                 node_count=0),                                  # node consumed -> cut pair, COAST
            snap(ut=40_000.0, apoapsis=200_000.0, periapsis=60_000.0,
                 altitude=1_500_000.0, situation="ESCAPING", body="Mun"),   # -> TARGET-FLYBY
            snap(ut=40_600.0, apoapsis=200_000.0, periapsis=60_000.0,
                 altitude=61_000.0, situation="ESCAPING", body="Mun"),      # periapsis + hop
            snap(ut=80_000.0, apoapsis=12_000_000.0, periapsis=35_000.0,
                 altitude=4_000_000.0, situation="ORBITING",
                 body="Kerbin"),                                 # home SOI -> RETURN terminal
        ]

    def test_b5_happy_path_writes_mission_ok(self):
        """B5 flies ascent -> transfer -> flyby -> free-return with NO settle
        tail (spec settle_frames=0, review SF-4) and all four assertions are
        met -> MISSION-OK. Guards the shell mis-wiring the new
        target/plan/execute actions or terminating at ORBIT like B2."""
        control = FakeMissionControl(self._happy_frames())
        code, result = run(b5_mun_flyby.SPEC, B5_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        kinds = [a.kind for a in control.actions]
        for kind in (mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_SET_TARGET_BODY,
                     mlib.ACTION_MJ_PLAN_TRANSFER, mlib.ACTION_MJ_EXECUTE_NODES,
                     mlib.ACTION_MJ_PLAN_COURSE_CORRECT, mlib.ACTION_AP_POINT_NODE,
                     mlib.ACTION_SET_RAILS_WARP, mlib.ACTION_SET_PHYSICS_WARP,
                     mlib.ACTION_SET_THROTTLE):
            self.assertIn(kind, kinds)
        # The flip's physics warp is always DROPPED (a 0 command) before any
        # throttle-up, and each round both raises and drops it.
        phys = [a.value for a in control.actions
                if a.kind == mlib.ACTION_SET_PHYSICS_WARP]
        self.assertEqual(phys, [1.0, 0.0, 1.0, 0.0])
        # The target-body action carried the body NAME in text.
        targets = [a for a in control.actions if a.kind == mlib.ACTION_SET_TARGET_BODY]
        self.assertEqual(targets, [mlib.Action(mlib.ACTION_SET_TARGET_BODY, text="Mun")])
        # Exactly ONE executor handoff (the TLI); both correction rounds fly
        # the DIY burner (AP-point + throttle), never MechJeb's executor.
        executes = [a for a in control.actions if a.kind == mlib.ACTION_MJ_EXECUTE_NODES]
        self.assertEqual(len(executes), 1)
        points = [a for a in control.actions if a.kind == mlib.ACTION_AP_POINT_NODE]
        self.assertEqual(len(points), 2)
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])
        # NO settle tail (review SF-4: B5's assertions are machine-carried and
        # evaluate discards frames, so post-RETURN reads are pure flake
        # surface): reads stop EXACTLY at the terminal frame.
        self.assertEqual(control.reads, len(self._happy_frames()))
        self.assertTrue(control.closed)

    def test_b6_minmus_alias_flies_same_machine(self):
        """b6_minmus_flyby is a thin alias over the shared B5 machine: the same
        happy-path frame script with body=Minmus and Minmus-sized params flies
        to MISSION-OK. Guards the alias shell drifting from the B5 wiring."""
        params = dict(B5_PARAMS, targetBodyName="Minmus",
                      transferMinApoapsisMeters=40_000_000,
                      courseCorrectPeriapsisMeters=20000,
                      targetPeriapsisFloorMeters=6000)
        frames = [
            (snap(**{**f.__dict__, "body": "Minmus"}) if f.body == "Mun" else f)
            for f in self._happy_frames()
        ]
        # The transfer-apoapsis floor is Minmus-sized: raise the burn-done frames.
        frames = [
            (snap(**{**f.__dict__, "apoapsis": 46_000_000.0})
             if f.apoapsis == 11_500_000.0 else f)
            for f in frames
        ]
        control = FakeMissionControl(frames)
        code, result = run(b6_minmus_flyby.SPEC, params, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        targets = [a for a in control.actions if a.kind == mlib.ACTION_SET_TARGET_BODY]
        self.assertEqual(targets, [mlib.Action(mlib.ACTION_SET_TARGET_BODY, text="Minmus")])

    def test_b7_duna_alias_flies_interplanetary_machine(self):
        """b7_duna_flyby is a thin alias over the shared B5 machine with the
        five interplanetary params ON: the shell must emit the
        INTERPLANETARY plan (never the moon Hohmann), exit the ejection burn
        on the hyperbolic ecc gate, coast Kerbin -> Sun with time-to-SOI
        correction rounds, fly the Duna flyby, and terminate RETURN on the
        Sun exit with the returnedToHome assertion reporting Sun. Also the
        B7_PARAMS round-trip proof for the five new spec keys."""
        frames = [
            snap(ut=0.0, situation="PRE_LAUNCH", body="Kerbin"),
            snap(ut=300.0, apoapsis=690_000.0, periapsis=10_000.0,
                 situation="FLYING", mj_ascent_complete=True,
                 body="Kerbin"),                                 # -> CIRCULARIZE
            snap(ut=400.0, apoapsis=700_000.0, periapsis=690_000.0,
                 altitude=699_000.0, situation="ORBITING",
                 body="Kerbin"),                                 # -> ORBIT
            snap(ut=401.0, apoapsis=700_000.0, periapsis=690_000.0,
                 altitude=699_100.0, situation="ORBITING",
                 body="Kerbin"),                                 # ORBIT -> PLAN (interplanetary)
            snap(ut=405.0, apoapsis=700_000.0, periapsis=690_000.0,
                 altitude=699_200.0, situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> TRANSFER-BURN
            snap(ut=10_000_600.0, apoapsis=-40_000_000.0,
                 periapsis=695_000.0, eccentricity=1.4, altitude=800_000.0,
                 situation="ESCAPING", body="Kerbin",
                 node_count=0),                                  # hyperbolic -> COAST
            snap(ut=10_060_000.0, situation="ORBITING", body="Sun",
                 altitude=13_000_000_000.0,
                 time_to_soi=7_000_000.0),                       # helio: round 0 (20M) fires
            snap(ut=10_060_010.0, situation="ORBITING", body="Sun",
                 altitude=13_000_000_100.0, node_count=1),       # node -> CORRECTION-BURN
            snap(ut=10_060_020.0, situation="ORBITING", body="Sun",
                 altitude=13_000_000_200.0, node_count=1,
                 node_dv=80.0, ap_error=1.5),                    # streak 1 -> flip phys warp
            snap(ut=10_060_040.0, situation="ORBITING", body="Sun",
                 altitude=13_000_000_300.0, node_count=1,
                 node_dv=80.0, ap_error=1.2,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop phys warp
            snap(ut=10_060_042.0, situation="ORBITING", body="Sun",
                 altitude=13_000_000_400.0, node_count=1,
                 node_dv=80.0, ap_error=1.1),                    # warp NONE -> throttle
            snap(ut=10_060_070.0, situation="ORBITING", body="Sun",
                 altitude=13_000_000_500.0, node_count=1,
                 node_dv=1.5, ap_error=1.0),                     # dv <= cut -> cut triple, COAST
            snap(ut=16_500_000.0, situation="ORBITING", body="Sun",
                 altitude=13_000_001_000.0,
                 time_to_soi=499_995.0),                         # trigger 500k -> round 1
            snap(ut=16_500_010.0, situation="ORBITING", body="Sun",
                 altitude=13_000_002_000.0, node_count=1),       # node -> CORRECTION-BURN
            snap(ut=16_500_020.0, situation="ORBITING", body="Sun",
                 altitude=13_000_003_000.0, node_count=1,
                 node_dv=10.0, ap_error=1.4),                    # streak 1 -> flip phys warp
            snap(ut=16_500_040.0, situation="ORBITING", body="Sun",
                 altitude=13_000_004_000.0, node_count=1,
                 node_dv=10.0, ap_error=1.2,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop phys warp
            snap(ut=16_500_042.0, situation="ORBITING", body="Sun",
                 altitude=13_000_005_000.0, node_count=1,
                 node_dv=10.0, ap_error=1.1),                    # warp NONE -> throttle
            snap(ut=16_500_060.0, situation="ORBITING", body="Sun",
                 altitude=13_000_006_000.0, node_count=0),       # node consumed -> cut pair, COAST
            snap(ut=17_000_000.0, situation="ESCAPING", body="Duna",
                 altitude=40_000_000.0),                         # Duna SOI -> TARGET-FLYBY
            snap(ut=17_050_000.0, situation="ESCAPING", body="Duna",
                 altitude=60_000.0, periapsis=55_000.0,
                 warp_mode="RAILS", warp_rate=10_000.0),         # periapsis area (min-alt evidence)
            snap(ut=17_200_000.0, situation="ORBITING", body="Sun",
                 altitude=13_000_010_000.0),                     # Sun exit -> RETURN terminal
        ]
        control = FakeMissionControl(frames)
        code, result = run(b7_duna_flyby.SPEC, B7_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER, kinds)
        self.assertNotIn(mlib.ACTION_MJ_PLAN_TRANSFER, kinds)
        targets = [a for a in control.actions if a.kind == mlib.ACTION_SET_TARGET_BODY]
        self.assertEqual(targets, [mlib.Action(mlib.ACTION_SET_TARGET_BODY, text="Duna")])
        # Both time-triggered rounds flew the DIY burner.
        points = [a for a in control.actions if a.kind == mlib.ACTION_AP_POINT_NODE]
        self.assertEqual(len(points), 2)
        # The returned assertion keeps its NAME but reports the Sun exit.
        by_name = {a["name"]: a for a in result["assertions"]}
        self.assertEqual(by_name["returnedToHome"]["value"], "Sun")
        self.assertEqual(by_name["returnedToHome"]["returnBody"], "Sun")
        self.assertTrue(all(a["met"] for a in result["assertions"]),
                        result["assertions"])
        self.assertIn(mlib.B5_TARGET_FLYBY, result["phasesReached"])
        # NO settle tail (same SF-4 contract as B5/B6).
        self.assertEqual(control.reads, len(frames))
        self.assertTrue(control.closed)

    def test_b5_flyby_ejection_is_assert_fail(self):
        """A flyby that slings the craft out of the home system (body=Sun inside
        TARGET-FLYBY) is MISSION-ASSERT-FAIL with the ejected loss reason."""
        frames = self._happy_frames()[:19] + [
            snap(ut=90_000.0, altitude=90_000_000.0, situation="ESCAPING",
                 body="Sun")]
        control = FakeMissionControl(frames)
        code, result = run(b5_mun_flyby.SPEC, B5_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertIn("ejected", result["reason"])
        self.assertIn(mlib.B5_TARGET_FLYBY, result["phasesReached"])
        self.assertTrue(control.closed)


# ---------------------------------------------------------------------------
# FORGE + B-DOCK shell integration (fake telemetry) + the mid-mission seam bridge.
# ---------------------------------------------------------------------------


FORGE_PARAMS = {
    "craftName": "Kerbal X",
    "launchSite": "LaunchPad",
    "launchTimeoutSeconds": 120,
    "settleDebounceFrames": 2,
}

BDOCK_PARAMS = {
    "stationApoapsisMeters": 110000,
    "stationPeriapsisMeters": 110000,
    "interceptorApoapsisMeters": 90000,
    "interceptorPeriapsisMeters": 90000,
    "apoErrorMeters": 5000,
    "periErrorMeters": 5000,
    "ascentTimeoutSeconds": 1200,
    "circularizeTimeoutSeconds": 600,
    "craftName": "Kerbal X",
    "launchSite": "LaunchPad",
    "launchTimeoutSeconds": 300,
    "launchSettleDebounceFrames": 2,
    "approachDistanceMeters": 100,
    "maxPhasingOrbits": 5,
    "matchSpeedMetersPerSec": 1.0,
    "dockSpeedMetersPerSec": 0.5,
    "transferAmountLf": 40,
    "transferAmountMp": 15,
    "stationCommitTimeoutSeconds": 300,
    "rendezvousTimeoutSeconds": 30000,
    "dockTimeoutSeconds": 600,
    "transferTimeoutSeconds": 120,
    "undockTimeoutSeconds": 120,
}


class ForgeShellTests(unittest.TestCase):
    def test_forge_happy_path_writes_mission_ok(self):
        """The forge boots, launches the craft, settles PRELAUNCH -> MISSION-OK.
        Guards the shell mis-wiring launch_vessel or never settling."""
        frames = [
            snap(ut=0.0, situation="FLYING"),               # PRELAUNCH -> LAUNCH
            snap(ut=5.0, situation="PRE_LAUNCH"),           # settle 1
            snap(ut=10.0, situation="PRE_LAUNCH"),          # settle 2 -> SETTLED
        ]
        control = FakeMissionControl(frames)
        code, result = run(forge_station.SPEC, FORGE_PARAMS, control, budget=600.0)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "forge_station")
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_LAUNCH_VESSEL, kinds)
        self.assertTrue(control.closed)


FORGE_LKO_PARAMS = {
    "craftName": "Kerbal X",
    "launchSite": "LaunchPad",
    "crewNames": ["Valentina Kerman", "Bob Kerman"],
    "minCrew": 2,
    "launchTimeoutSeconds": 300,
    "launchSettleDebounceFrames": 2,
    "targetApoapsisMeters": 100000,
    "targetPeriapsisMeters": 100000,
    "apoErrorMeters": 10000,
    "periErrorMeters": 10000,
    "eccentricityMax": 0.02,
    "inclinationErrorDeg": 5.0,
    "ascentTimeoutSeconds": 900,
    "circularizeTimeoutSeconds": 2400,
    "separationTimeoutSeconds": 120,
    "parkSituations": ["ORBITING"],
    "parkDwellSeconds": 60,
    "parkTimeoutSeconds": 600,
    "parkDebounceFrames": 2,
    "maxAngularVelocityRadPerSec": 0.05,
    "minSafePeriapsisMeters": 75000,
}


class ForgeLkoShellTests(unittest.TestCase):
    def _parked(self, ut):
        return snap(ut=ut, situation="ORBITING", apoapsis=100000.0,
                    periapsis=99500.0, eccentricity=0.001, inclination=0.1,
                    angular_velocity=0.001, crew_count=2, vessel_count=2,
                    available_thrust=200000.0)

    def test_forge_lko_happy_path_writes_mission_ok(self):
        """The orbital forge launches with crew, ascends, circularizes, separates
        (two-step) and parks stable -> MISSION-OK with every assertion met.
        Guards the shell mis-wiring the machine to the (fake) kRPC surface: no
        launch, no autowarp-explicit node execution, no attitude hold at the park,
        or a save taken mid-flight."""
        frames = [
            snap(ut=0.0, situation="FLYING"),                            # -> LAUNCH
            snap(ut=5.0, situation="PRE_LAUNCH", crew_count=2),          # settle 1
            snap(ut=10.0, situation="PRE_LAUNCH", crew_count=2),         # settle 2 -> ASCENT
            snap(ut=300.0, apoapsis=99000.0, mj_ascent_complete=True,
                 crew_count=2),                                          # -> CIRCULARIZE
            snap(ut=400.0, periapsis=99000.0, vessel_count=1,
                 crew_count=2),                                          # -> SEPARATE
            snap(ut=401.0, vessel_count=2, available_thrust=0.0, crew_count=2),
            snap(ut=402.0, vessel_count=2, available_thrust=0.0, crew_count=2),
            snap(ut=403.0, vessel_count=2, available_thrust=0.0, crew_count=2),
            snap(ut=404.0, vessel_count=2, available_thrust=200000.0, crew_count=2),
            snap(ut=405.0, vessel_count=2, available_thrust=200000.0, crew_count=2),
            snap(ut=406.0, vessel_count=2, available_thrust=200000.0, crew_count=2),
            self._parked(410.0), self._parked(420.0), self._parked(480.0),
            self._parked(490.0), self._parked(500.0), self._parked(510.0),
        ]
        control = FakeMissionControl(frames)
        code, result = run(forge_lko.SPEC, FORGE_LKO_PARAMS, control, budget=1800.0)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "forge_lko")
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_LAUNCH_VESSEL, kinds)
        self.assertIn(mlib.ACTION_MJ_ENGAGE_ASCENT, kinds)
        # The autowarp-explicit node execution, NOT the bare circularization
        # action (B-DOCK flight-12 lesson).
        self.assertIn(mlib.ACTION_MJ_EXECUTE_NODES, kinds)
        self.assertNotIn(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION, kinds)
        # The park's saved-configuration contract.
        self.assertIn(mlib.ACTION_CUT_THROTTLE, kinds)
        self.assertIn(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES, kinds)
        self.assertIn(mlib.ACTION_SET_SAS, kinds)
        self.assertIn(mlib.ACTION_SET_RCS, kinds)
        # The launch action carries the named crew (the crewed-fixture contract).
        launch = [a for a in control.actions
                  if a.kind == mlib.ACTION_LAUNCH_VESSEL][0]
        self.assertEqual(launch.crew, ("Valentina Kerman", "Bob Kerman"))
        self.assertTrue(all(a["met"] for a in result["assertions"]),
                        result["assertions"])
        self.assertTrue(control.closed)


class _SeamFakeControl(FakeMissionControl):
    """A fake that records configure_seam wiring so a test can prove run_mission
    passes the seam config through."""
    def __init__(self, *a, **k):
        super().__init__(*a, **k)
        self.seam_configured = None

    def configure_seam(self, commands_path, responses_path, commit_id):
        self.seam_configured = (commands_path, responses_path, commit_id)


class BDockShellTests(unittest.TestCase):
    def _bdock_frames(self):
        return [
            snap(ut=0.0),                                              # PRELAUNCH->STATION-ASCENT
            snap(ut=100.0, apoapsis=108000.0, mj_ascent_complete=True),  # ->STATION-CIRCULARIZE
            snap(ut=150.0, periapsis=109000.0, vessel_count=1),       # ->STATION-SEPARATE (baseline=1, drop core)
            snap(ut=151.0, vessel_count=2, available_thrust=0.0),     # split settle 1 (engine unlit)
            snap(ut=152.0, vessel_count=2, available_thrust=0.0),     # split settle 2
            snap(ut=153.0, vessel_count=2, available_thrust=0.0),     # split settle 3 -> ignite
            snap(ut=154.0, vessel_count=2, available_thrust=200000.0),   # thrust settle 1
            snap(ut=155.0, vessel_count=2, available_thrust=200000.0),   # thrust settle 2
            snap(ut=156.0, vessel_count=2, available_thrust=200000.0),   # thrust settle 3 -> STATION-ORBIT
            snap(ut=160.0),                                           # ->STATION-COMMIT
            snap(ut=161.0, seam_commit_result="OK"),                 # ->INT-LAUNCH
            snap(ut=170.0, situation="PRE_LAUNCH"),                  # settle 1
            snap(ut=175.0, situation="PRE_LAUNCH"),                  # settle 2 -> INT-ASCENT
            snap(ut=400.0, apoapsis=88000.0, mj_ascent_complete=True),  # ->INT-CIRCULARIZE
            snap(ut=450.0, periapsis=89000.0, vessel_count=2),       # ->INT-SEPARATE (baseline=2, drop core)
            snap(ut=451.0, vessel_count=3, available_thrust=0.0),    # split settle 1 (engine unlit)
            snap(ut=452.0, vessel_count=3, available_thrust=0.0),    # split settle 2
            snap(ut=453.0, vessel_count=3, available_thrust=0.0),    # split settle 3 -> ignite
            snap(ut=454.0, vessel_count=3, available_thrust=180000.0),   # thrust settle 1
            snap(ut=455.0, vessel_count=3, available_thrust=180000.0),   # thrust settle 2
            snap(ut=456.0, vessel_count=3, available_thrust=180000.0),   # thrust settle 3 -> INT-PHASING-ORBIT
            snap(ut=460.0),                                          # ->SET-TARGET
            snap(ut=470.0, target_set=True),                        # ->RENDEZVOUS
            snap(ut=480.0, mj_rendezvous_enabled=True, target_distance=5000.0),
            snap(ut=490.0, mj_rendezvous_enabled=False, target_distance=80.0),  # ->MATCH-VELOCITY
            snap(ut=500.0, target_rel_speed=0.5),                   # ->DOCK (entry: abort+set-target, enable pending)
            snap(ut=505.0, target_distance=90.0),                   # deferred enable -> MJ_ENABLE_DOCKING
            snap(ut=510.0, mj_docking_enabled=True, docking_state="Docking", target_distance=50.0),
            snap(ut=520.0, mj_docking_enabled=False, docking_state="Docked"),   # ->TRANSFER T1
            snap(ut=525.0, transfer_complete=True, vessel_count=3),  # T1 done -> T2
            snap(ut=530.0, transfer_complete=True, vessel_count=3),  # T2 done -> UNDOCK
            snap(ut=540.0, vessel_count=4, docking_state="Ready"),   # split -> TERMINAL
        ]

    def test_bdock_happy_path_writes_mission_ok(self):
        """The full two-vessel flow drives to TERMINAL with all assertions met.
        Guards the shell mis-wiring any of the docking / transfer / undock actions."""
        control = FakeMissionControl(self._bdock_frames())
        code, result = run(bdock_dock_transfer.SPEC, BDOCK_PARAMS, control, budget=90000.0)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "bdock_dock_transfer")
        kinds = [a.kind for a in control.actions]
        for want in (mlib.ACTION_CAPTURE_STATION, mlib.ACTION_PARSEK_COMMIT_TREE,
                     mlib.ACTION_LAUNCH_VESSEL, mlib.ACTION_SET_TARGET_VESSEL,
                     mlib.ACTION_MJ_ENABLE_RENDEZVOUS, mlib.ACTION_SET_TARGET_DOCKING_PORT,
                     mlib.ACTION_MJ_ENABLE_DOCKING, mlib.ACTION_START_RESOURCE_TRANSFER,
                     mlib.ACTION_UNDOCK):
            self.assertIn(want, kinds)
        # Exactly two transfers, opposite directions.
        transfers = [a for a in control.actions
                     if a.kind == mlib.ACTION_START_RESOURCE_TRANSFER]
        self.assertEqual(len(transfers), 2)
        self.assertEqual(transfers[0].text, "LiquidFuel")
        self.assertEqual(transfers[0].limit, mlib.TRANSFER_DIR_DELIVER)
        self.assertEqual(transfers[1].text, "MonoPropellant")
        self.assertEqual(transfers[1].limit, mlib.TRANSFER_DIR_PICKUP)
        names = {a["name"]: a["met"] for a in result["assertions"]}
        self.assertTrue(all(names.values()), names)
        self.assertTrue(control.closed)

    def test_run_mission_passes_seam_config_to_configure_seam(self):
        """run_mission wires the seam bridge into the control when seam_config is
        supplied (the route-1 plumbing)."""
        control = _SeamFakeControl(self._bdock_frames())
        writer = ResultSink()
        clock = FakeClock()
        log = mission_runner.MissionLogger(sink=lambda _l: None, clock=clock)
        mission_runner.run_mission(
            bdock_dock_transfer.SPEC, BDOCK_PARAMS, "127.0.0.1", 50000, 50001,
            "unused/result.json", 90000.0, control=control, log=log, clock=clock,
            sleep=lambda _s: None, writer=writer,
            seam_config={"commands_path": "cmds.txt", "responses_path": "resps.txt",
                         "commit_id": "0003"})
        self.assertEqual(control.seam_configured, ("cmds.txt", "resps.txt", "0003"))


class SeamCommitBridgeTests(unittest.TestCase):
    """The KrpcMissionControl mid-mission command-seam bridge (route 1): file
    channel write + bounded response poll, WITHOUT any kRPC connection (these
    methods only touch the channel files + time)."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-seam-")
        self.cmds = os.path.join(self.tmp, "parsek-test-commands.txt")
        self.resps = os.path.join(self.tmp, "parsek-test-responses.txt")
        self.saved_poll = mission_runner.SEAM_COMMIT_POLL_SECONDS

    def tearDown(self):
        mission_runner.SEAM_COMMIT_POLL_SECONDS = self.saved_poll
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _ctrl(self):
        c = mission_runner.KrpcMissionControl(use_mechjeb=True, read_docking=True)
        c.configure_seam(self.cmds, self.resps, "0003")
        return c

    def _seed_response(self, line):
        with open(self.resps, "w", encoding="utf-8") as fh:
            fh.write(line + "\n")

    def test_ok_response_writes_command_and_reads_ok(self):
        self._seed_response("id=0003 cmd=CommitTree verdict=OK seq=1 ut=1240.0")
        c = self._ctrl()
        c._perform_seam_commit()
        self.assertEqual(c._seam_commit_result, "OK")
        with open(self.cmds, "r", encoding="utf-8") as fh:
            cmd_text = fh.read()
        self.assertIn("id=0003 cmd=CommitTree", cmd_text)

    def test_error_response_reads_error(self):
        self._seed_response("id=0003 cmd=CommitTree verdict=ERROR seq=1 msg=no-active-tree")
        c = self._ctrl()
        c._perform_seam_commit()
        self.assertEqual(c._seam_commit_result, "ERROR")

    def test_first_terminal_wins_on_rewrite_dupe(self):
        # A crash-recovery rewrite re-emits a byte-equivalent line; first-wins.
        with open(self.resps, "w", encoding="utf-8") as fh:
            fh.write("id=0003 cmd=CommitTree verdict=OK seq=1\n")
            fh.write("id=0003 cmd=CommitTree verdict=OK seq=2\n")
        c = self._ctrl()
        c._perform_seam_commit()
        self.assertEqual(c._seam_commit_result, "OK")

    def test_no_response_times_out(self):
        mission_runner.SEAM_COMMIT_POLL_SECONDS = 0.0  # deadline is now -> immediate TIMEOUT
        c = self._ctrl()
        c._perform_seam_commit()
        self.assertEqual(c._seam_commit_result, "TIMEOUT")

    def test_no_seam_configured_errors(self):
        c = mission_runner.KrpcMissionControl(use_mechjeb=True, read_docking=True)
        c._perform_seam_commit()  # never configured
        self.assertEqual(c._seam_commit_result, "ERROR")

    def test_read_seam_response_ignores_other_ids(self):
        self._seed_response("id=0002 cmd=SetSetting verdict=OK seq=1")
        c = self._ctrl()
        self.assertIsNone(c._read_seam_response("0003"))


# ---------------------------------------------------------------------------
# ORBIT missions (B11 Mun / B12 Minmus): the SAME B5 shell wiring with
# captureEnabled, driven end to end over the fake seam -- ascent, transfer,
# cross-SOI coast, CAPTURE burn, park dwell, mid-mission seam CommitTree, and
# the commit-in-foreign-SOI terminal.
# ---------------------------------------------------------------------------

B11_PARAMS = dict(
    B5_PARAMS,
    courseCorrectPeriapsisMeters=250000,
    captureEnabled=True,
    capturePlanTimeoutSeconds=300,
    captureBurnTimeoutSeconds=60000,
    parkMinPeriapsisMeters=15000,
    parkMaxApoapsisMeters=2000000,
    parkMaxEccentricity=0.25,
    parkMaxAngularVelocityRadPerSec=0.05,
    parkSituations=["ORBITING"],
    parkDwellSeconds=180,
    parkDebounceFrames=3,
    parkTimeoutSeconds=600,
    commitTimeoutSeconds=300,
)

# The UT of the scripted arrival PERIAPSIS: the capture burn completes there
# (the CAPTURE-BURN frame below), the planned capture node sits ON it, and
# every in-SOI frame's time_to_periapsis is derived from it. One constant so
# the clock, the node and the burn cannot drift apart in the fixture.
_ARRIVAL_PERIAPSIS_UT = 48_000.0


class B11OrbitShellTests(unittest.TestCase):
    """B11/B12 shell wiring: the B5 transfer half plus the ORBIT tail
    (PLAN-CAPTURE -> CAPTURE-BURN -> PARK -> ORBIT-COMMIT -> ORBIT-COMMITTED),
    with the new capture-plan and commit-seam actions flowing end to end."""

    def _transfer_frames(self, body="Mun", transfer_ap=11_500_000.0):
        """The B5 ascent + transfer + two correction rounds, verbatim in shape;
        the ORBIT tail replaces B5's flyby/return frames."""
        return [
            snap(ut=0.0, apoapsis=1000, periapsis=0, situation="PRE_LAUNCH",
                 body="Kerbin"),
            snap(ut=100.0, apoapsis=78000, periapsis=1000, situation="FLYING",
                 mj_ascent_complete=True, body="Kerbin"),        # -> CIRCULARIZE
            snap(ut=140.0, apoapsis=80000, periapsis=79000, altitude=79000.0,
                 situation="ORBITING", body="Kerbin"),           # -> ORBIT
            snap(ut=141.0, apoapsis=80001, periapsis=79000, altitude=79001.0,
                 situation="ORBITING", body="Kerbin"),           # ORBIT -> PLAN (target+plan)
            snap(ut=150.0, apoapsis=80001, periapsis=79000, altitude=79002.0,
                 situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> TRANSFER-BURN
            snap(ut=2200.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=90000.0, situation="ORBITING", body="Kerbin",
                 node_count=0),                                  # burn done -> COAST
            snap(ut=2210.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=93000.0, situation="ORBITING",
                 body="Kerbin"),                                 # trigger 0 -> PLAN-CORRECTION
            snap(ut=2230.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=95000.0, situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> CORRECTION-BURN
            snap(ut=2245.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=97000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=100.0, ap_error=1.0),     # streak 1 -> physics warp
            snap(ut=2300.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=99000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=100.0, ap_error=1.0,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop warp
            snap(ut=2302.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=99500.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=100.0, ap_error=1.0),     # warp NONE -> throttle
            snap(ut=2400.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=200_000.0, situation="ORBITING", body="Kerbin"),  # -> COAST
            snap(ut=8000.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=6_500_000.0, situation="ORBITING",
                 body="Kerbin"),                                 # trigger 2 -> PLAN-CORRECTION
            snap(ut=8010.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=6_510_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> CORRECTION-BURN
            snap(ut=8025.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=6_520_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=4.0, ap_error=0.8),       # streak 1
            snap(ut=8030.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=6_525_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=4.0, ap_error=0.7,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop warp
            snap(ut=8035.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=6_530_000.0, situation="ORBITING", body="Kerbin",
                 node_count=1, node_dv=4.0, ap_error=0.7),       # warp NONE -> throttle
            snap(ut=8100.0, apoapsis=transfer_ap, periapsis=79000,
                 altitude=6_600_000.0, situation="ORBITING", body="Kerbin",
                 node_count=0),                                  # consumed -> COAST
            snap(ut=40_000.0, apoapsis=-4_000_000.0, periapsis=1_000_000.0,
                 eccentricity=1.4, altitude=2_100_000.0,
                 situation="ESCAPING", body=body,
                 time_to_periapsis=_ARRIVAL_PERIAPSIS_UT - 40_000.0),  # SOI -> TARGET-FLYBY
        ]

    def _capture_frames(self, body="Mun"):
        """The ORBIT tail: arm the capture, plan it, burn it, park it, commit
        it. Altitudes drift frame to frame so the 1x frozen-telemetry detector
        (which compares bit-identical orbit fields) never trips on the park.

        Every in-SOI frame carries the PERIAPSIS CLOCK, because both live
        shells build their control with read_periapsis=True: the capture arming
        gate needs periapsis still AHEAD of us, the capture warp is bounded by
        that clock, and the PLAN-CAPTURE handoff refuses a node that is not AT
        the arrival periapsis. A fixture without it is a fixture of a mission
        that never flies."""
        arming = [
            snap(ut=40_000.0 + 10.0 * i, apoapsis=-4_000_000.0,
                 periapsis=1_000_000.0, eccentricity=1.4,
                 altitude=2_000_000.0 - 1000.0 * i, situation="ESCAPING",
                 body=body,
                 time_to_periapsis=_ARRIVAL_PERIAPSIS_UT - (40_000.0 + 10.0 * i))
            for i in range(1, mlib.CAPTURE_ARM_DEBOUNCE_FRAMES + 1)
        ]
        tail = [
            # PLAN-CAPTURE: the node appears AT the arrival periapsis -> hand
            # off to the node executor.
            snap(ut=40_100.0, apoapsis=-4_000_000.0, periapsis=1_000_000.0,
                 eccentricity=1.4, altitude=1_900_000.0, situation="ESCAPING",
                 body=body, node_count=1, node_ut=_ARRIVAL_PERIAPSIS_UT,
                 time_to_periapsis=_ARRIVAL_PERIAPSIS_UT - 40_100.0),
            # CAPTURE-BURN: node consumed AND the orbit is now BOUND.
            snap(ut=48_000.0, apoapsis=1_010_000.0, periapsis=990_000.0,
                 eccentricity=0.01, altitude=1_000_000.0, situation="ORBITING",
                 body=body, node_count=0, angular_velocity=0.002),
        ]
        park_entry_ut = 48_000.0
        park = [
            snap(ut=park_entry_ut + 10.0 * i, apoapsis=1_010_000.0 + i,
                 periapsis=990_000.0 - i, eccentricity=0.01,
                 altitude=1_000_000.0 - 100.0 * i, situation="ORBITING",
                 body=body, angular_velocity=0.002)
            for i in range(1, 4)                                  # debounce 3
        ]
        park.append(
            # Dwell elapsed AND still in-gate -> ORBIT-COMMIT (seam fires).
            snap(ut=park_entry_ut + 200.0, apoapsis=1_010_010.0,
                 periapsis=989_990.0, eccentricity=0.01, altitude=999_500.0,
                 situation="ORBITING", body=body, angular_velocity=0.002))
        park.append(
            # The seam answered OK -> ORBIT-COMMITTED terminal.
            snap(ut=park_entry_ut + 210.0, apoapsis=1_010_020.0,
                 periapsis=989_980.0, eccentricity=0.01, altitude=999_400.0,
                 situation="ORBITING", body=body, angular_velocity=0.002,
                 seam_commit_result="OK"))
        return arming + tail + park

    def test_b11_happy_path_commits_the_tree_parked_in_the_mun_soi(self):
        """B11 flies ascent -> transfer -> Mun SOI -> CAPTURE -> PARK ->
        mid-mission seam CommitTree -> ORBIT-COMMITTED, with all SIX assertions
        met. Guards the shell mis-wiring the capture plan / commit-seam actions,
        or terminating on the free-return like B5."""
        frames = self._transfer_frames() + self._capture_frames()
        control = FakeMissionControl(frames)
        code, result = run(b11_mun_orbit.SPEC, B11_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b11_mun_orbit")
        self.assertEqual(result["phasesReached"][-1], mlib.B5_ORBIT_COMMITTED)
        kinds = [a.kind for a in control.actions]
        for kind in (mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_SET_TARGET_BODY,
                     mlib.ACTION_MJ_PLAN_TRANSFER, mlib.ACTION_MJ_EXECUTE_NODES,
                     mlib.ACTION_MJ_PLAN_CAPTURE, mlib.ACTION_CUT_THROTTLE,
                     mlib.ACTION_SET_SAS, mlib.ACTION_SET_RCS,
                     mlib.ACTION_PARSEK_COMMIT_TREE):
            self.assertIn(kind, kinds, kind)
        # EXACTLY ONE mid-mission commit (a second seam CommitTree with no
        # active tree returns ERROR and reds the run).
        commits = [a for a in control.actions
                   if a.kind == mlib.ACTION_PARSEK_COMMIT_TREE]
        self.assertEqual(len(commits), 1)
        # TWO executor handoffs: the TLI and the CAPTURE (both far-node,
        # autowarp regimes); the corrections fly the DIY burner.
        executes = [a for a in control.actions
                    if a.kind == mlib.ACTION_MJ_EXECUTE_NODES]
        self.assertEqual(len(executes), 2)
        by_name = {a["name"]: a["met"] for a in result["assertions"]}
        self.assertEqual(set(by_name),
                         {"reachedOrbit", "reachedTargetSoi",
                          "flybyPeriapsisFloor", "capturedInTargetOrbit",
                          "parkedStable", "treeCommitted"})
        self.assertTrue(all(by_name.values()), result["assertions"])
        # NO settle tail (the SF-4 contract): reads stop at the terminal frame.
        self.assertEqual(control.reads, len(frames))
        self.assertTrue(control.closed)

    def test_b12_minmus_alias_flies_the_same_machine(self):
        """b12_minmus_orbit is a thin alias over the same capture-enabled B5
        machine: the same frame script with body=Minmus and Minmus-sized params
        flies to MISSION-OK. Guards the alias shell drifting from B11."""
        params = dict(B11_PARAMS, targetBodyName="Minmus",
                      transferMinApoapsisMeters=40_000_000,
                      courseCorrectPeriapsisMeters=20000,
                      targetPeriapsisFloorMeters=6000,
                      captureBurnTimeoutSeconds=200000,
                      parkMinPeriapsisMeters=10000,
                      parkMaxApoapsisMeters=1500000)
        frames = (self._transfer_frames(body="Minmus", transfer_ap=46_000_000.0)
                  + self._capture_frames(body="Minmus"))
        control = FakeMissionControl(frames)
        code, result = run(b12_minmus_orbit.SPEC, params, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b12_minmus_orbit")
        self.assertEqual(result["phasesReached"][-1], mlib.B5_ORBIT_COMMITTED)
        targets = [a for a in control.actions
                   if a.kind == mlib.ACTION_SET_TARGET_BODY]
        self.assertEqual(targets,
                         [mlib.Action(mlib.ACTION_SET_TARGET_BODY, text="Minmus")])

    def test_flying_past_without_capturing_is_assert_fail(self):
        """The free-return B5 asserts is the outcome an ORBIT mission must NOT
        have: reading Kerbin again inside the flyby leg is an ASSERT-FAIL with
        a named loss reason, never a RETURN pass."""
        frames = self._transfer_frames() + [
            snap(ut=45_000.0, apoapsis=12_000_000.0, periapsis=35_000.0,
                 altitude=4_000_000.0, situation="ORBITING", body="Kerbin"),
        ]
        control = FakeMissionControl(frames)
        code, result = run(b11_mun_orbit.SPEC, B11_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertIn("without capturing", result["reason"])
        self.assertNotIn(mlib.B5_RETURN, result["phasesReached"])
        self.assertIn(mlib.B5_TARGET_FLYBY, result["phasesReached"])

    def test_commit_seam_error_flakes_naming_the_seam(self):
        """A seam CommitTree that answers ERROR is driver-INVALID (a retryable
        FLAKE naming the seam), never a silent pass and never PARSEK-FAIL."""
        frames = self._transfer_frames() + self._capture_frames()[:-1] + [
            snap(ut=48_400.0, apoapsis=1_010_020.0, periapsis=989_980.0,
                 eccentricity=0.01, altitude=999_400.0, situation="ORBITING",
                 body="Mun", angular_velocity=0.002,
                 seam_commit_result="ERROR"),
        ]
        control = FakeMissionControl(frames)
        code, result = run(b11_mun_orbit.SPEC, B11_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE, result)
        self.assertNotEqual(code, 0)
        self.assertIn("tree-commit seam returned ERROR", result["reason"])
        self.assertNotIn(mlib.B5_ORBIT_COMMITTED, result["phasesReached"])


B13_PARAMS = dict(
    B11_PARAMS,
    landingEnabled=True,
    descentTimeoutSeconds=2200,
    landingTouchdownSpeedMps=0.5,
    landingDeployGears=True,
    landingDeployChutes=False,
    landingRcsAdjustment=False,
    landingProgressWindowSeconds=900,
    landingProgressMinDropMeters=5000,
    landedSituations=["LANDED", "SPLASHED"],
    landedMaxVerticalSpeedMps=1.0,
    landedMaxHorizontalSpeedMps=0.5,
    landedDwellSeconds=120,
    landedDebounceFrames=3,
    landedTimeoutSeconds=600,
)


class B13LandingShellTests(B11OrbitShellTests):
    """B13/B14 shell wiring: the ENTIRE B11 machine (inherited verbatim, hence
    the subclass -- the transfer + capture fixtures are the same frames) plus
    the LANDING tail (DESCENT -> LANDED-SETTLE -> SURFACE-COMMIT ->
    SURFACE-COMMITTED), with the new landing actions and the opt-in landing
    telemetry flowing end to end.

    Inheriting B11OrbitShellTests deliberately RE-RUNS the four B11 cells under
    this class name too: that is free proof that adding the landing lane did not
    move the orbit lane's shell behaviour."""

    # The capture-tail fixture ends by committing the tree; the landing lane
    # instead descends from PARK, so the ORBIT frames are re-scripted below.
    def _capture_frames_to_park(self, body="Mun"):
        """Arm the capture, plan it, burn it, then hold the park until the
        dwell elapses. Identical to B11's fixture up to the PARK exit."""
        arming = [
            snap(ut=40_000.0 + 10.0 * i, apoapsis=-4_000_000.0,
                 periapsis=1_000_000.0, eccentricity=1.4,
                 altitude=2_000_000.0 - 1000.0 * i, situation="ESCAPING",
                 body=body,
                 time_to_periapsis=_ARRIVAL_PERIAPSIS_UT - (40_000.0 + 10.0 * i))
            for i in range(1, mlib.CAPTURE_ARM_DEBOUNCE_FRAMES + 1)
        ]
        tail = [
            snap(ut=40_100.0, apoapsis=-4_000_000.0, periapsis=1_000_000.0,
                 eccentricity=1.4, altitude=1_900_000.0, situation="ESCAPING",
                 body=body, node_count=1, node_ut=_ARRIVAL_PERIAPSIS_UT,
                 time_to_periapsis=_ARRIVAL_PERIAPSIS_UT - 40_100.0),
            snap(ut=48_000.0, apoapsis=1_010_000.0, periapsis=990_000.0,
                 eccentricity=0.01, altitude=1_000_000.0, situation="ORBITING",
                 body=body, node_count=0, angular_velocity=0.002),
        ]
        park = [
            snap(ut=48_000.0 + 10.0 * i, apoapsis=1_010_000.0 + i,
                 periapsis=990_000.0 - i, eccentricity=0.01,
                 altitude=1_000_000.0 - 100.0 * i, situation="ORBITING",
                 body=body, angular_velocity=0.002)
            for i in range(1, 4)                                  # debounce 3
        ]
        park.append(
            # Dwell elapsed AND still in-gate -> DESCENT (not ORBIT-COMMIT).
            snap(ut=48_200.0, apoapsis=1_010_010.0, periapsis=989_990.0,
                 eccentricity=0.01, altitude=141_000.0, situation="ORBITING",
                 body=body, angular_velocity=0.002))
        return arming + tail + park

    def _landing_frames(self, body="Mun"):
        """DESCENT -> LANDED-SETTLE -> SURFACE-COMMIT -> SURFACE-COMMITTED.

        Every frame carries the OPT-IN LANDING CHANNEL, because both live shells
        build their control with read_landing=True: the descent supervisor gates
        on landing_ap_enabled and the settled-touchdown gate needs BOTH speed
        components. A fixture without them is a fixture of a mission that can
        never land."""
        descent = [
            snap(ut=48_200.0 + 200.0 * i, body=body, situation="SUB_ORBITAL",
                 altitude=141_000.0 - 20_000.0 * i, vertical_speed=-100.0,
                 horizontal_speed=400.0 - 50.0 * i, landing_ap_enabled=1,
                 landing_ap_status="Doing deorbit burn.")
            for i in range(1, 6)
        ]
        touchdown_ut = 48_200.0 + 200.0 * 6
        landed = [
            # OBSERVED touchdown. MechJeb has stopped its own module here, so
            # landing_ap_enabled reads 0 -- which the machine must treat as
            # SUCCESS, not as a dead autopilot.
            snap(ut=touchdown_ut, body=body, situation="LANDED", altitude=3.0,
                 vertical_speed=-0.4, horizontal_speed=0.3,
                 landing_ap_enabled=0),
        ]
        settle = [
            snap(ut=touchdown_ut + 1.0 * i, body=body, situation="LANDED",
                 altitude=3.0 - 0.001 * i, vertical_speed=0.0,
                 horizontal_speed=0.01, landing_ap_enabled=0)
            for i in range(1, 4)                                  # debounce 3
        ]
        settle.append(
            # Dwell elapsed AND still settled -> SURFACE-COMMIT (seam fires).
            snap(ut=touchdown_ut + 130.0, body=body, situation="LANDED",
                 altitude=2.99, vertical_speed=0.0, horizontal_speed=0.01,
                 landing_ap_enabled=0))
        settle.append(
            # The seam answered OK -> SURFACE-COMMITTED terminal.
            snap(ut=touchdown_ut + 140.0, body=body, situation="LANDED",
                 altitude=2.98, vertical_speed=0.0, horizontal_speed=0.01,
                 landing_ap_enabled=0, seam_commit_result="OK"))
        return descent + landed + settle

    def test_b13_happy_path_commits_the_tree_landed_on_the_mun(self):
        """B13 flies ascent -> transfer -> Mun SOI -> CAPTURE -> PARK ->
        DESCENT -> LANDED-SETTLE -> mid-mission seam CommitTree ->
        SURFACE-COMMITTED, with all EIGHT assertions met. Guards the shell
        mis-wiring the landing actions, committing in ORBIT like B11, or
        terminating on the free-return like B5."""
        frames = (self._transfer_frames() + self._capture_frames_to_park()
                  + self._landing_frames())
        control = FakeMissionControl(frames)
        code, result = run(b13_mun_landing.SPEC, B13_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b13_mun_landing")
        self.assertEqual(result["phasesReached"][-1], mlib.B5_SURFACE_COMMITTED)
        # The ORBIT terminal is NEVER entered: this lane commits on the surface.
        self.assertNotIn(mlib.B5_ORBIT_COMMIT, result["phasesReached"])
        self.assertNotIn(mlib.B5_ORBIT_COMMITTED, result["phasesReached"])
        kinds = [a.kind for a in control.actions]
        for kind in (mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_SET_TARGET_BODY,
                     mlib.ACTION_MJ_PLAN_TRANSFER, mlib.ACTION_MJ_EXECUTE_NODES,
                     mlib.ACTION_MJ_PLAN_CAPTURE, mlib.ACTION_MJ_LAND_UNTARGETED,
                     mlib.ACTION_MJ_STOP_LANDING, mlib.ACTION_CUT_THROTTLE,
                     mlib.ACTION_SET_SAS, mlib.ACTION_PARSEK_COMMIT_TREE):
            self.assertIn(kind, kinds, kind)
        # EXACTLY ONE mid-mission commit, and EXACTLY ONE landing engage on a
        # healthy descent (the re-issue path is bounded and must not fire here).
        self.assertEqual(kinds.count(mlib.ACTION_PARSEK_COMMIT_TREE), 1)
        self.assertEqual(kinds.count(mlib.ACTION_MJ_LAND_UNTARGETED), 1)
        # The vehicle configuration handed to MechJeb: gears ON, chutes OFF
        # (AIRLESS body), RCS OFF (no thruster blocks on this stage).
        engage = [a for a in control.actions
                  if a.kind == mlib.ACTION_MJ_LAND_UNTARGETED][0]
        self.assertEqual(engage.landing_config, (0.5, True, False, False))
        by_name = {a["name"]: a["met"] for a in result["assertions"]}
        self.assertEqual(set(by_name),
                         {"reachedOrbit", "reachedTargetSoi",
                          "flybyPeriapsisFloor", "capturedInTargetOrbit",
                          "parkedStable", "landedOnTargetBody", "landedStable",
                          "treeCommitted"})
        self.assertTrue(all(by_name.values()), result["assertions"])
        # NO settle tail (the SF-4 contract): reads stop at the terminal frame.
        self.assertEqual(control.reads, len(frames))
        self.assertTrue(control.closed)

    def test_b14_minmus_alias_flies_the_same_machine(self):
        """b14_minmus_landing is a thin alias over the same capture+landing
        machine: the same frame script with body=Minmus and Minmus-sized params
        flies to MISSION-OK. Guards the alias shell drifting from B13."""
        params = dict(B13_PARAMS, targetBodyName="Minmus",
                      transferMinApoapsisMeters=40_000_000,
                      courseCorrectPeriapsisMeters=20000,
                      targetPeriapsisFloorMeters=6000,
                      captureBurnTimeoutSeconds=200000,
                      parkMinPeriapsisMeters=10000,
                      parkMaxApoapsisMeters=1500000)
        frames = (self._transfer_frames(body="Minmus",
                                        transfer_ap=46_000_000.0)
                  + self._capture_frames_to_park(body="Minmus")
                  + self._landing_frames(body="Minmus"))
        control = FakeMissionControl(frames)
        code, result = run(b14_minmus_landing.SPEC, params, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b14_minmus_landing")
        self.assertEqual(result["phasesReached"][-1], mlib.B5_SURFACE_COMMITTED)
        by_name = {a["name"]: a for a in result["assertions"]}
        self.assertEqual(by_name["landedOnTargetBody"]["value"], "Minmus")
        targets = [a for a in control.actions
                   if a.kind == mlib.ACTION_SET_TARGET_BODY]
        self.assertEqual(targets,
                         [mlib.Action(mlib.ACTION_SET_TARGET_BODY, text="Minmus")])

    def test_a_descent_that_never_engages_flakes_naming_the_autopilot(self):
        """COMMANDED-vs-OBSERVED end to end: the shell issues the engage, the
        module reads DOWN every poll, and the run FLAKES with
        landing-autopilot-not-enabled after bounded re-issues -- it does NOT sit
        out the descent budget and it does NOT report OK."""
        dead = [
            snap(ut=48_200.0 + 10.0 * i, body="Mun", situation="ORBITING",
                 altitude=141_000.0, apoapsis=1_010_000.0 + i,
                 periapsis=990_000.0 - i, eccentricity=0.01,
                 vertical_speed=0.0, horizontal_speed=400.0,
                 landing_ap_enabled=0, landing_ap_status="")
            for i in range(1, 30)
        ]
        frames = (self._transfer_frames() + self._capture_frames_to_park()
                  + dead)
        control = FakeMissionControl(frames)
        code, result = run(b13_mun_landing.SPEC, B13_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE, result)
        self.assertNotEqual(code, 0)
        self.assertIn(mlib.LANDING_GIVEUP_AP_NOT_ENABLED, result["reason"])
        self.assertNotIn(mlib.B5_LANDED_SETTLE, result["phasesReached"])
        kinds = [a.kind for a in control.actions]
        # ONE engage on entry + the bounded re-issues, then the fast-fail.
        self.assertEqual(kinds.count(mlib.ACTION_MJ_LAND_UNTARGETED),
                         1 + mlib.MAX_LANDING_AP_REISSUES)

    def test_a_crash_on_descent_is_assert_fail_not_a_timeout(self):
        """The EVA-4 lesson: a failed objective must not read as a generic
        give-up. A lithobraked lander terminates ASSERT-FAIL with the
        landing-vessel-lost name and NO met landing assertions."""
        frames = (self._transfer_frames() + self._capture_frames_to_park()
                  + [snap(ut=48_400.0, body="Mun", situation="SUB_ORBITAL",
                          altitude=140.0, vertical_speed=-190.0,
                          horizontal_speed=30.0, landing_ap_enabled=1),
                     snap(ut=48_410.0, vessel_lost=True)])
        control = FakeMissionControl(frames)
        code, result = run(b13_mun_landing.SPEC, B13_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertIn(mlib.LANDING_GIVEUP_VESSEL_LOST, result["reason"])
        by_name = {a["name"]: a["met"] for a in result["assertions"]}
        self.assertFalse(by_name["landedOnTargetBody"])
        self.assertFalse(by_name["landedStable"])
        self.assertFalse(by_name["treeCommitted"])

    def test_b1_and_eva4_descent_logs_are_untouched_by_the_landing_line(self):
        """"DESCENT" is NOT unique to this lane -- B1's pad hop, B4's suborbital
        lane and EVA-4 all have a phase of that name. The new per-frame descent
        diagnostic is gated on the B5-only `landing_engaged` field, not on the
        phase name, so those three missions' logs stay byte-identical. Guards a
        phase-name collision quietly rewriting a live-proven mission's log."""
        b1_state = mlib.b1_initial_state(mlib.b1_params_from_dict({}))
        self.assertFalse(getattr(b1_state, "landing_engaged", False))
        self.assertFalse(hasattr(b1_state, "landing_engaged"))
        # ... and the landing machine only reports once it has actually engaged.
        b13_state = mlib.b5_initial_state(mlib.b5_params_from_dict(B13_PARAMS))
        self.assertFalse(b13_state.landing_engaged)

    def test_the_landing_shells_opt_into_the_landing_telemetry(self):
        """read_landing=True is LOAD-BEARING: without it landing_ap_enabled
        stays at its -1 UNREAD sentinel (no autopilot verdict at all) and
        horizontal_speed stays NaN (the settled gate fails closed forever, so
        landed-never-stable would fire on a PERFECT landing). Guards a shell
        copied from B11 without the flag."""
        for shell in (b13_mun_landing, b14_minmus_landing):
            control = shell.make_control()
            self.assertTrue(control._read_landing, shell.MISSION_NAME)
            # And the inherited B11 opt-ins must survive the copy.
            self.assertTrue(control._read_docking, shell.MISSION_NAME)
            self.assertTrue(control._read_node_executor, shell.MISSION_NAME)
            self.assertTrue(control._read_periapsis, shell.MISSION_NAME)


# ---------------------------------------------------------------------------
# B15 / B16 EVE lane (2026-07-26). The lane's CENTRAL CLAIM is that Eve needed
# NO new machine code -- it is B7's five interplanetary params and B11/B12's
# capture tail, re-valued. These cells exist to make that claim checkable
# instead of asserted, and to prove the two families it builds on are untouched.
# ---------------------------------------------------------------------------


# B15 spec-shaped params (mirrors harness/scenarios/B15-eve-flyby.toml): B7's
# key set with Eve values. The 100 km flyby floor is NOT a terrain number here
# (Eve's peaks are ~7.5 km) -- it is the VACUUM floor above Eve's 90 km
# atmosphere, which is why it is an order of magnitude above B7's 15 km.
B15_PARAMS = dict(
    B7_PARAMS,
    targetBodyName="Eve",
    apoErrorMeters=150000,
    periErrorMeters=150000,
    ascentTimeoutSeconds=2400,
    # Raised with the park trim below: CIRCULARIZE now also owns the round-out.
    circularizeTimeoutSeconds=12000,
    # The ONE key B7 does not carry. See EveLaneIsAParameterChangeTests.
    parkTrimEccMax=0.02,
    # Re-derived from flight 1: an INWARD ejection needs less C3 than an
    # outward one, so B7/Duna's 1.05 legitimately fails a correct Eve escape
    # (MEASURED 1.004).
    ejectionEccFloor=1.001,
    # Flight 2: an inward ejection transits the Mun's SOI on the way out.
    viaBodyNames=["Sun", "Mun"],
    courseCorrectPeriapsisMeters=1000000,
    transferBurnTimeoutSeconds=18000000,
    coastTimeoutSeconds=7000000,
    flybyTimeoutSeconds=600000,
    targetPeriapsisFloorMeters=100000,
    # MEASURED on flight 6 once the corrections fired on the heliocentric leg:
    # MechJeb prices the Eve phase correction at 378.5 m/s, deterministically.
    maxCorrectionDvMps=450,
)

# B16 spec-shaped params: B15's interplanetary set PLUS B11/B12's capture tail.
# This dict IS the lane's claim -- it is a pure union of two live-proven key
# sets, and test_the_eve_params_are_a_union_of_two_proven_key_sets pins that.
B16_PARAMS = dict(
    B15_PARAMS,
    # parkTrimEccMax / circularizeTimeoutSeconds / ejectionEccFloor /
    # viaBodyNames all ride in from B15_PARAMS: B16's transfer IS B15's, so it
    # inherits every fix the Eve flights bought.
    courseCorrectPeriapsisMeters=5000000,
    captureEnabled=True,
    capturePlanTimeoutSeconds=300,
    captureBurnTimeoutSeconds=400000,
    parkMinPeriapsisMeters=500000,
    parkMaxApoapsisMeters=13000000,
    parkMaxEccentricity=0.25,
    parkMaxAngularVelocityRadPerSec=0.05,
    parkSituations=["ORBITING"],
    parkDwellSeconds=180,
    parkDebounceFrames=3,
    parkTimeoutSeconds=600,
    commitTimeoutSeconds=300,
)

# The UT of the scripted EVE arrival periapsis. Same role as
# _ARRIVAL_PERIAPSIS_UT in the B11 fixture: the capture burn completes there,
# the planned node sits ON it, and every in-SOI frame's time_to_periapsis is
# derived from it, so the clock / node / burn cannot drift apart.
_EVE_PERIAPSIS_UT = 8_100_000.0


class B15EveFlybyShellTests(unittest.TestCase):
    """B15 shell wiring: the SAME interplanetary machine B7 flies, aimed INWARD
    at Eve. Guards the alias shell drifting from B7's wiring and guards the
    inward direction accidentally mattering to mlib (it must not: every gate in
    the coast / ejection path is a name comparison, a scalar clock or an
    eccentricity threshold, none of which has a sign)."""

    def _frames(self, target="Eve", exit_body="Sun"):
        """Kerbin -> Sun -> Eve -> Sun, the B7 frame shape with Eve numbers.
        The in-SOI frame's altitude is 1,050 km: ABOVE the 100 km vacuum floor,
        which on Eve means the pass stayed out of the 90 km atmosphere."""
        return [
            snap(ut=0.0, situation="PRE_LAUNCH", body="Kerbin"),
            snap(ut=300.0, apoapsis=690_000.0, periapsis=10_000.0,
                 situation="FLYING", mj_ascent_complete=True,
                 body="Kerbin"),                                 # -> CIRCULARIZE
            snap(ut=400.0, apoapsis=700_000.0, periapsis=690_000.0,
                 altitude=699_000.0, situation="ORBITING",
                 body="Kerbin"),                                 # -> ORBIT
            snap(ut=401.0, apoapsis=700_000.0, periapsis=690_000.0,
                 altitude=699_100.0, situation="ORBITING",
                 body="Kerbin"),                                 # ORBIT -> PLAN (interplanetary)
            snap(ut=405.0, apoapsis=700_000.0, periapsis=690_000.0,
                 altitude=699_200.0, situation="ORBITING", body="Kerbin",
                 node_count=1),                                  # node -> TRANSFER-BURN
            snap(ut=4_968_000.0, apoapsis=-40_000_000.0,
                 periapsis=695_000.0, eccentricity=1.4, altitude=800_000.0,
                 situation="ESCAPING", body="Kerbin",
                 node_count=0),                                  # hyperbolic -> COAST
            snap(ut=5_066_000.0, situation="ORBITING", body="Sun",
                 altitude=9_500_000_000.0,
                 time_to_soi=3_490_000.0),                       # helio: round 0 (20M) fires
            snap(ut=5_066_010.0, situation="ORBITING", body="Sun",
                 altitude=9_500_000_100.0, node_count=1),        # node -> CORRECTION-BURN
            snap(ut=5_066_020.0, situation="ORBITING", body="Sun",
                 altitude=9_500_000_200.0, node_count=1,
                 node_dv=80.0, ap_error=1.5),                    # streak 1 -> flip phys warp
            snap(ut=5_066_040.0, situation="ORBITING", body="Sun",
                 altitude=9_500_000_300.0, node_count=1,
                 node_dv=80.0, ap_error=1.2,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop phys warp
            snap(ut=5_066_042.0, situation="ORBITING", body="Sun",
                 altitude=9_500_000_400.0, node_count=1,
                 node_dv=80.0, ap_error=1.1),                    # warp NONE -> throttle
            snap(ut=5_066_070.0, situation="ORBITING", body="Sun",
                 altitude=9_500_000_500.0, node_count=1,
                 node_dv=1.5, ap_error=1.0),                     # dv <= cut -> cut triple, COAST
            snap(ut=8_050_000.0, situation="ORBITING", body="Sun",
                 altitude=9_500_001_000.0,
                 time_to_soi=499_995.0),                         # trigger 500k -> round 1
            snap(ut=8_050_010.0, situation="ORBITING", body="Sun",
                 altitude=9_500_002_000.0, node_count=1),        # node -> CORRECTION-BURN
            snap(ut=8_050_020.0, situation="ORBITING", body="Sun",
                 altitude=9_500_003_000.0, node_count=1,
                 node_dv=10.0, ap_error=1.4),                    # streak 1 -> flip phys warp
            snap(ut=8_050_040.0, situation="ORBITING", body="Sun",
                 altitude=9_500_004_000.0, node_count=1,
                 node_dv=10.0, ap_error=1.2,
                 warp_mode="PHYSICS", warp_rate=2.0),            # streak 2 -> drop phys warp
            snap(ut=8_050_042.0, situation="ORBITING", body="Sun",
                 altitude=9_500_005_000.0, node_count=1,
                 node_dv=10.0, ap_error=1.1),                    # warp NONE -> throttle
            snap(ut=8_050_060.0, situation="ORBITING", body="Sun",
                 altitude=9_500_006_000.0, node_count=0),        # node consumed -> cut pair, COAST
            snap(ut=8_060_000.0, situation="ESCAPING", body=target,
                 altitude=80_000_000.0),                         # Eve SOI -> TARGET-FLYBY
            snap(ut=_EVE_PERIAPSIS_UT, situation="ESCAPING", body=target,
                 altitude=1_050_000.0, periapsis=1_000_000.0,
                 warp_mode="RAILS", warp_rate=10_000.0),         # periapsis area (min-alt evidence)
            snap(ut=8_200_000.0, situation="ORBITING", body=exit_body,
                 altitude=9_500_010_000.0),                      # Sun exit -> RETURN terminal
        ]

    def test_b15_eve_alias_flies_the_interplanetary_machine_inward(self):
        """The whole B15 claim in one cell: the SAME shared machine and the SAME
        five interplanetary params, aimed at an INNER planet, reach MISSION-OK.
        The shell must emit the INTERPLANETARY plan (never the moon Hohmann),
        exit the ejection on the hyperbolic ecc gate, coast Kerbin -> Sun with
        time-to-SOI correction rounds, fly the Eve pass and terminate RETURN on
        the Sun exit."""
        control = FakeMissionControl(self._frames())
        code, result = run(b15_eve_flyby.SPEC, B15_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b15_eve_flyby")
        kinds = [a.kind for a in control.actions]
        self.assertIn(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER, kinds)
        self.assertNotIn(mlib.ACTION_MJ_PLAN_TRANSFER, kinds)
        targets = [a for a in control.actions
                   if a.kind == mlib.ACTION_SET_TARGET_BODY]
        self.assertEqual(targets,
                         [mlib.Action(mlib.ACTION_SET_TARGET_BODY, text="Eve")])
        # Both time-triggered rounds flew the DIY burner, exactly as B7's do.
        points = [a for a in control.actions
                  if a.kind == mlib.ACTION_AP_POINT_NODE]
        self.assertEqual(len(points), 2)
        by_name = {a["name"]: a for a in result["assertions"]}
        self.assertEqual(by_name["returnedToHome"]["value"], "Sun")
        self.assertEqual(by_name["reachedTargetSoi"]["value"], "Eve")
        self.assertTrue(all(a["met"] for a in result["assertions"]),
                        result["assertions"])
        self.assertIn(mlib.B5_TARGET_FLYBY, result["phasesReached"])
        # NO settle tail (the SF-4 contract shared with B5/B6/B7).
        self.assertEqual(control.reads, len(self._frames()))
        self.assertTrue(control.closed)

    def test_a_pass_inside_eves_atmosphere_fails_the_vacuum_floor(self):
        """On Eve the flyby floor is a VACUUM floor, not a terrain floor: a pass
        whose min sampled altitude lands inside the 90 km atmosphere must FAIL
        the assertion, because at Eve that is an aerocapture, not a flyby.
        Guards someone harmonising targetPeriapsisFloorMeters back down to a
        Duna-style terrain number."""
        frames = list(self._frames())
        frames[19] = snap(ut=_EVE_PERIAPSIS_UT, situation="ESCAPING",
                          body="Eve", altitude=60_000.0, periapsis=55_000.0,
                          warp_mode="RAILS", warp_rate=10_000.0)
        control = FakeMissionControl(frames)
        code, result = run(b15_eve_flyby.SPEC, B15_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        by_name = {a["name"]: a for a in result["assertions"]}
        self.assertFalse(by_name["flybyPeriapsisFloor"]["met"])
        self.assertEqual(by_name["flybyPeriapsisFloor"]["floor"], 100000)

    def test_gilly_is_not_a_legal_body_and_reads_as_an_ejection(self):
        """GILLY IS DELIBERATELY ABSENT from viaBodyNames. The B7 lane learned
        the hard way that a moon can grab an arrival (Ike ate B7 on 1 of 2
        sweeps); the DESIGNED response is a NAMED ASSERT-FAIL, never a silent
        pass. This cell pins that choice: if someone fixes the Gilly risk by
        adding it to viaBodyNames, this test fails and says why."""
        frames = self._frames()[:19] + [
            snap(ut=8_070_000.0, altitude=90_000.0, situation="ESCAPING",
                 body="Gilly")]
        control = FakeMissionControl(frames)
        code, result = run(b15_eve_flyby.SPEC, B15_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertIn("ejected", result["reason"])
        self.assertIn("Gilly", result["reason"])
        self.assertIn(mlib.B5_TARGET_FLYBY, result["phasesReached"])


class B16EveOrbitShellTests(unittest.TestCase):
    """B16 shell wiring, and the FIRST cell anywhere that flies B7's
    interplanetary param group and B11/B12's capture tail in the SAME mission.
    That union is the whole reason B16 needed no new machine code, so it is
    worth a fixture of its own rather than a parameter tweak on an existing
    one."""

    def _transfer_frames(self):
        """B15's interplanetary ascent + transfer + two heliocentric correction
        rounds, ending on the Eve SOI entry frame with HYPERBOLIC elements and
        the periapsis CLOCK the capture arming gate reads."""
        base = B15EveFlybyShellTests._frames(self)[:18]
        return base + [
            snap(ut=8_060_000.0, apoapsis=-9_000_000.0, periapsis=5_000_000.0,
                 eccentricity=1.9, altitude=80_000_000.0,
                 situation="ESCAPING", body="Eve",
                 time_to_periapsis=_EVE_PERIAPSIS_UT - 8_060_000.0),
        ]

    def _capture_frames(self):
        """The ORBIT tail at Eve: arm the capture, plan it, burn it, park it,
        commit it. Altitudes drift frame to frame so the 1x frozen-telemetry
        detector never trips on the park, and every in-SOI frame carries the
        periapsis clock (the shell builds its control with read_periapsis=True,
        and a fixture without it is a fixture of a mission that never flies).

        The parked orbit is a ~5,000 km circular Eve park: INSIDE the spec's
        13,000 km apoapsis ceiling, which is what keeps a committed recording
        clear of Gilly's 14,175 km periapsis shell."""
        arming = [
            snap(ut=8_060_000.0 + 10.0 * i, apoapsis=-9_000_000.0,
                 periapsis=5_000_000.0, eccentricity=1.9,
                 altitude=79_000_000.0 - 1_000_000.0 * i, situation="ESCAPING",
                 body="Eve",
                 time_to_periapsis=_EVE_PERIAPSIS_UT - (8_060_000.0 + 10.0 * i))
            for i in range(1, mlib.CAPTURE_ARM_DEBOUNCE_FRAMES + 1)
        ]
        tail = [
            # PLAN-CAPTURE: the node appears AT the arrival periapsis -> hand
            # off to the node executor.
            snap(ut=8_060_100.0, apoapsis=-9_000_000.0, periapsis=5_000_000.0,
                 eccentricity=1.9, altitude=70_000_000.0, situation="ESCAPING",
                 body="Eve", node_count=1, node_ut=_EVE_PERIAPSIS_UT,
                 time_to_periapsis=_EVE_PERIAPSIS_UT - 8_060_100.0),
            # CAPTURE-BURN: node consumed AND the orbit is now BOUND.
            snap(ut=_EVE_PERIAPSIS_UT, apoapsis=5_010_000.0,
                 periapsis=4_990_000.0, eccentricity=0.002,
                 altitude=5_000_000.0, situation="ORBITING", body="Eve",
                 node_count=0, angular_velocity=0.002),
        ]
        park = [
            snap(ut=_EVE_PERIAPSIS_UT + 10.0 * i, apoapsis=5_010_000.0 + i,
                 periapsis=4_990_000.0 - i, eccentricity=0.002,
                 altitude=5_000_000.0 - 100.0 * i, situation="ORBITING",
                 body="Eve", angular_velocity=0.002)
            for i in range(1, 4)                                  # debounce 3
        ]
        park.append(
            # Dwell elapsed AND still in-gate -> ORBIT-COMMIT (seam fires).
            snap(ut=_EVE_PERIAPSIS_UT + 200.0, apoapsis=5_010_010.0,
                 periapsis=4_989_990.0, eccentricity=0.002,
                 altitude=4_999_500.0, situation="ORBITING", body="Eve",
                 angular_velocity=0.002))
        park.append(
            # The seam answered OK -> ORBIT-COMMITTED terminal.
            snap(ut=_EVE_PERIAPSIS_UT + 210.0, apoapsis=5_010_020.0,
                 periapsis=4_989_980.0, eccentricity=0.002,
                 altitude=4_999_400.0, situation="ORBITING", body="Eve",
                 angular_velocity=0.002, seam_commit_result="OK"))
        return arming + tail + park

    def test_b16_commits_the_tree_parked_in_the_eve_soi(self):
        """The union claim, end to end: INTERPLANETARY plan + hyperbolic
        ejection + heliocentric coast + CAPTURE + PARK + mid-mission seam
        CommitTree -> ORBIT-COMMITTED, all SIX assertions met. Guards the two
        param groups interfering with each other, which is the ONE risk in a
        spec that adds no machine code."""
        frames = self._transfer_frames() + self._capture_frames()
        control = FakeMissionControl(frames)
        code, result = run(b16_eve_orbit.SPEC, B16_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_OK, result)
        self.assertEqual(code, 0)
        self.assertEqual(result["mission"], "b16_eve_orbit")
        self.assertEqual(result["phasesReached"][-1], mlib.B5_ORBIT_COMMITTED)
        kinds = [a.kind for a in control.actions]
        for kind in (mlib.ACTION_MJ_ENGAGE_ASCENT, mlib.ACTION_SET_TARGET_BODY,
                     mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER,
                     mlib.ACTION_MJ_EXECUTE_NODES, mlib.ACTION_MJ_PLAN_CAPTURE,
                     mlib.ACTION_CUT_THROTTLE, mlib.ACTION_SET_SAS,
                     mlib.ACTION_SET_RCS, mlib.ACTION_PARSEK_COMMIT_TREE):
            self.assertIn(kind, kinds, kind)
        # The INTERPLANETARY plan, never the moon Hohmann: the capture tail must
        # not drag B11's transfer shape in with it.
        self.assertNotIn(mlib.ACTION_MJ_PLAN_TRANSFER, kinds)
        # EXACTLY ONE mid-mission commit (a second seam CommitTree with no
        # active tree returns ERROR and reds the run).
        commits = [a for a in control.actions
                   if a.kind == mlib.ACTION_PARSEK_COMMIT_TREE]
        self.assertEqual(len(commits), 1)
        # TWO executor handoffs: the EJECTION and the CAPTURE; both correction
        # rounds fly the DIY burner.
        executes = [a for a in control.actions
                    if a.kind == mlib.ACTION_MJ_EXECUTE_NODES]
        self.assertEqual(len(executes), 2)
        by_name = {a["name"]: a["met"] for a in result["assertions"]}
        self.assertEqual(set(by_name),
                         {"reachedOrbit", "reachedTargetSoi",
                          "flybyPeriapsisFloor", "capturedInTargetOrbit",
                          "parkedStable", "treeCommitted"})
        self.assertTrue(all(by_name.values()), result["assertions"])
        # returnedToHome must NOT be in the set: in capture mode the return body
        # is the FAILURE body, so awarding an assertion for reaching it would be
        # awarding one for the mission failing.
        self.assertNotIn("returnedToHome", by_name)
        self.assertEqual(control.reads, len(frames))
        self.assertTrue(control.closed)

    def test_flying_past_eve_into_the_sun_is_assert_fail(self):
        """B15's heliocentric exit is exactly the outcome B16 must NOT have.
        With returnBodyName = Sun, reading Sun inside the flyby leg is the NAMED
        left-the-target-SOI-without-capturing ASSERT-FAIL, never a RETURN pass.
        This is the cell that pins why returnBodyName is set at all."""
        frames = self._transfer_frames() + [
            snap(ut=8_070_000.0, situation="ORBITING", body="Sun",
                 altitude=9_500_010_000.0),
        ]
        control = FakeMissionControl(frames)
        code, result = run(b16_eve_orbit.SPEC, B16_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_ASSERT_FAIL, result)
        self.assertNotEqual(code, 0)
        self.assertIn("without capturing", result["reason"])
        self.assertNotIn(mlib.B5_RETURN, result["phasesReached"])
        self.assertIn(mlib.B5_TARGET_FLYBY, result["phasesReached"])

    def test_a_park_above_the_gilly_ceiling_is_not_a_capture(self):
        """The apoapsis ceiling is LOAD-BEARING TWICE on this body: it certifies
        capture (a hyperbolic approach reads a NEGATIVE apoapsis) AND it is the
        Gilly exclusion (13,000 km altitude sits just under Gilly's 14,175 km
        periapsis radius). An orbit whose apoapsis reaches into Gilly's shell
        must NOT be accepted, so a committed recording can never be parked in a
        moon's path.

        WHERE THE REJECTION ACTUALLY HAPPENS, verified here rather than assumed:
        the ceiling is ALSO the CAPTURE-BURN done-evidence bound, so an
        over-ceiling orbit is caught by the NAMED `capture under-burn` give-up
        (a MISSION-FLAKE, the standing verdict for a named give-up) BEFORE PARK
        is ever entered - the run never reaches the assertion row at all. The
        assertion row stays unmet as the backstop. Both facts are pinned, so a
        future change that moves the enforcement point cannot pass silently."""
        frames = self._transfer_frames() + self._capture_frames()
        bad = []
        for f in frames:
            if f.body == "Eve" and f.situation == "ORBITING":
                f = snap(**{**f.__dict__, "apoapsis": 20_000_000.0,
                            "eccentricity": 0.24})
            bad.append(f)
        control = FakeMissionControl(bad)
        code, result = run(b16_eve_orbit.SPEC, B16_PARAMS, control)
        self.assertEqual(result["verdict"], mlib.MISSION_FLAKE, result)
        self.assertNotEqual(code, 0)
        self.assertIn("capture under-burn", result["reason"])
        # The reason QUOTES the ceiling, so an operator reading the red does not
        # have to open the spec to learn why a bound orbit was refused.
        self.assertIn("ap<=13000000", result["reason"])
        self.assertNotIn(mlib.B5_PARK, result["phasesReached"])
        self.assertIn(mlib.B5_CAPTURE_BURN, result["phasesReached"])
        by_name = {a["name"]: a for a in result["assertions"]}
        self.assertFalse(by_name["capturedInTargetOrbit"]["met"])
        self.assertEqual(by_name["capturedInTargetOrbit"]["maxApoapsis"],
                         13000000)

    def test_the_b16_shell_opts_into_the_capture_telemetry(self):
        """The three capture-tail channels are OPT-IN and every one fails CLOSED
        at its unread sentinel, so a shell copied from B15 (which needs none of
        them) would arm no capture and observe no executor. Guards exactly that
        copy."""
        control = b16_eve_orbit.make_control()
        self.assertTrue(control._read_docking)
        self.assertTrue(control._read_node_executor)
        self.assertTrue(control._read_periapsis)
        # ...and B15 is a FLYBY: it must NOT have quietly acquired them.
        flyby = b15_eve_flyby.make_control()
        self.assertFalse(flyby._read_docking)
        self.assertFalse(flyby._read_node_executor)
        self.assertFalse(flyby._read_periapsis)


class EveLaneIsAParameterChangeTests(unittest.TestCase):
    """THE LANE'S CENTRAL CLAIM, machine-checked -- and REVISED once, on
    evidence, which is the whole reason this class is worth keeping.

    B15/B16 were shipped on the argument that Eve is a PARAMETER change, not a
    MACHINE change. Three flights refuted it. Two of the three failures really
    were parameter defects, but the third was not: MechJeb's interplanetary
    ejection planner is only correct from a ROUND parking orbit, and the shared
    machine had no way to produce one (CIRCULARIZE only WAITED on a periapsis
    window; it never acted). So the claim is now the narrower, still-useful
    one: B15 is B7's key set plus EXACTLY ONE argued key, and B16 adds none of
    its own beyond that.

    A test that pins a claim should be REWRITTEN when the claim is disproved,
    not deleted -- deleting it would have quietly retired the guard at the
    exact moment it did its job. These cells still fail the moment a SECOND
    key appears, and they still double as the existing-families-are-unaffected
    proof."""

    # The single machine-surface key B15 adds beyond B7's live-proven set,
    # argued in B15-eve-flyby.toml and b15_eve_flyby.schema.toml.
    EVE_ONLY_KEYS = ["parkTrimEccMax"]

    def test_the_eve_params_add_exactly_one_key_to_two_proven_key_sets(self):
        """B15's key set must be B7's (three green Duna flights) plus exactly
        the argued Eve-only key, and B16's must add nothing beyond B7's plus
        B11's (five green Mun + six green Minmus flights) plus that same key.
        Any OTHER new key means new machine surface, which is a design decision
        to be argued in a spec, not smuggled in via a dict."""
        self.assertEqual(sorted(set(B15_PARAMS) - set(B7_PARAMS)),
                         sorted(self.EVE_ONLY_KEYS))
        self.assertEqual(
            sorted(set(B16_PARAMS) - (set(B7_PARAMS) | set(B11_PARAMS))),
            sorted(self.EVE_ONLY_KEYS))

    def test_the_park_trim_is_armed_on_eve_and_off_on_every_proven_lane(self):
        """The park round-out trim must be ON for both Eve lanes (their
        transfers are wrong without it) and OFF for every lane that has already
        flown green -- that OFF is what makes CIRCULARIZE byte-identical for
        B5/B6/B7/B11-B14 and keeps their proofs valid."""
        for params in (B15_PARAMS, B16_PARAMS):
            self.assertGreater(
                mlib.b5_params_from_dict(params).park_trim_ecc_max, 0.0)
        for params in (B5_PARAMS, B7_PARAMS, B11_PARAMS, B13_PARAMS):
            self.assertEqual(
                mlib.b5_params_from_dict(params).park_trim_ecc_max, 0.0)

    def test_the_eve_lanes_select_the_expected_machine_behaviour(self):
        """The params RESOLVE to what the specs claim: B15 interplanetary with
        the capture and landing tails OFF, B16 interplanetary WITH capture and
        the landing tail still OFF. Guards a copy-paste that switches on a
        family this lane never intended to fly."""
        flyby = mlib.b5_params_from_dict(B15_PARAMS)
        self.assertTrue(flyby.interplanetary_transfer)
        self.assertEqual(flyby.target_body, "Eve")
        # "Mun" is a COAST-legality entry (flight 2: an inward ejection
        # transits it). It is deliberately NOT a correction-trigger body --
        # see test_the_correction_trigger_body_domain_excludes_the_mun.
        self.assertEqual(flyby.via_bodies, ("Sun", "Mun"))
        self.assertEqual(flyby.return_body, "Sun")
        self.assertFalse(flyby.capture_enabled)
        self.assertFalse(flyby.landing_enabled)

        orbit = mlib.b5_params_from_dict(B16_PARAMS)
        self.assertTrue(orbit.interplanetary_transfer)
        self.assertTrue(orbit.capture_enabled)
        self.assertFalse(orbit.landing_enabled)
        self.assertEqual(orbit.correction_trigger_alts, ())
        self.assertEqual(orbit.correction_trigger_time_to_soi,
                         (20000000.0, 500000.0))

    def _coasting(self, params, phase_entry_ut=0.0):
        base = mlib.b5_initial_state(params)
        return base.__class__(**{**base.__dict__,
                                 "phase": mlib.B5_COAST_TO_TARGET,
                                 "phase_entry_ut": phase_entry_ut})

    def test_the_time_mode_round_trigger_ignores_a_moon_soi_clock(self):
        """B15 FLIGHT 5 REGRESSION, and the sharper half of the via-body
        coupling. `time_to_soi` is the clock to ANY SOI change, so inside the
        Mun's SOI it reads the MUN-EXIT time -- a few thousand seconds, which
        trivially satisfies round 0's 20,000,000 s threshold. Flight 5 burned
        BOTH correction rounds there on a Mun flyby hyperbola (MechJeb priced
        the fix at 378.6 m/s against the 200 m/s cap, so both were discarded)
        and had none left for the heliocentric leg, where the real phase error
        was. The transfer's GEOMETRY was right by then -- perihelion 0.046 Eve
        SOI radii off -- and its PHASE was never corrected."""
        params = mlib.b5_params_from_dict(B15_PARAMS)
        state = self._coasting(params)
        in_mun = mlib.TelemetrySnapshot(
            ut=11_838_936.0, body="Mun", altitude=2_215_441.0,
            apoapsis=-2_260_732.0, time_to_soi=3_000.0)
        self.assertFalse(mlib._b5_correction_round_ready(state, in_mun))
        # The SAME clock value on the heliocentric leg DOES fire it.
        self.assertTrue(
            mlib._b5_correction_round_ready(state, replace(in_mun, body="Sun")))

    def test_the_time_mode_round_trigger_is_unchanged_for_b7(self):
        """The narrowing must be a no-op for the three green Duna flights:
        B7's return_body is "Sun" and its via list is ("Sun",), so the domain
        is the same set before and after."""
        params = mlib.b5_params_from_dict(B7_PARAMS)
        state = self._coasting(params)
        frame = mlib.TelemetrySnapshot(ut=5_000_000.0, body="Sun",
                                       time_to_soi=1_000_000.0)
        self.assertTrue(mlib._b5_correction_round_ready(state, frame))
        self.assertEqual(mlib._b5_correction_via_bodies(params),
                         params.via_bodies)

    def test_the_correction_trigger_body_domain_excludes_the_mun(self):
        """Widening viaBodyNames to admit the Mun was a COAST decision, but the
        no-encounter correction trigger read the same list, so flight 3 spent
        BOTH correction rounds inside the Mun's SOI on a flyby hyperbola (where
        MechJeb priced the correction at 1464.1 m/s against a 200 m/s cap and
        both were discarded). The trigger domain is the transfer-parent SOI,
        and this pins that it stayed IDENTICAL for the already-flown lanes."""
        self.assertEqual(
            mlib._b5_correction_via_bodies(mlib.b5_params_from_dict(B15_PARAMS)),
            ("Sun",))
        # B7: return_body "Sun", via_bodies ("Sun",) -- unchanged.
        self.assertEqual(
            mlib._b5_correction_via_bodies(mlib.b5_params_from_dict(B7_PARAMS)),
            mlib.b5_params_from_dict(B7_PARAMS).via_bodies)
        # B5/B6: not interplanetary, so the via list passes straight through.
        self.assertEqual(
            mlib._b5_correction_via_bodies(mlib.b5_params_from_dict(B5_PARAMS)),
            mlib.b5_params_from_dict(B5_PARAMS).via_bodies)

    def test_the_return_body_is_a_member_of_the_via_bodies_on_every_lane(self):
        """THE PRECONDITION `_b5_correction_via_bodies`' SAFETY ARGUMENT RESTS
        ON, pinned because the code does not enforce it. That docstring claims
        the narrowing "is a strict subset, so it can only ever REMOVE a firing
        opportunity" -- true ONLY while `return_body` is itself a member of
        `via_bodies`. A future interplanetary lane whose `returnBodyName` sits
        OUTSIDE `viaBodyNames` would make the narrowing ADD a body, inverting
        the argument from "can only remove" to "can also grant a correction
        round in an SOI the coast never even declared legal". This cell fails
        the moment such a spec is written."""
        for name, params in (("B7", B7_PARAMS), ("B15", B15_PARAMS),
                             ("B16", B16_PARAMS)):
            resolved = mlib.b5_params_from_dict(params)
            self.assertTrue(resolved.interplanetary_transfer, name)
            self.assertIn(
                resolved.return_body, resolved.via_bodies,
                "%s: returnBodyName %r is outside viaBodyNames %r, so the "
                "correction-domain narrowing would ADD a firing opportunity "
                "instead of removing one -- re-argue "
                "_b5_correction_via_bodies before shipping this lane"
                % (name, resolved.return_body, resolved.via_bodies))
            # And therefore the narrowing really is a subset, not just for
            # these three by inspection.
            self.assertTrue(
                set(mlib._b5_correction_via_bodies(resolved))
                <= set(resolved.via_bodies), name)

    def test_the_correction_approach_warp_reads_the_same_narrowed_domain(self):
        """THE ASYMMETRY THE NARROWING CREATED, closed. Both correction
        TRIGGERS were narrowed to the transfer-parent SOI, but the
        correction-approach WARP branch went on reading the raw `via_bodies`.
        For B7 the two lists are equal so nothing moved; for B15/B16 a craft
        inside the Mun's SOI entered that branch and computed
        dt = 3,086 - 20,000,000, which fails `dt > soi_lead`, so
        `rails_factor_for_time` returned 0 on the non-positive input and the
        floor-2 stair pinned the transit at 10x. MEASURED on flight 7: 317 of
        318 Mun frames at RAILSx10, ~3,086 game s over ~308 wall s. Matched to
        the trigger domain, the Mun transit takes the SOI native-warp branch
        instead (warp to the boundary minus soi_lead)."""
        params = mlib.b5_params_from_dict(B15_PARAMS)
        # phase_entry_ut just behind the frame: the flight-7 UTs are 11.8M, and
        # a phase entered at 0 would flake on the coast budget before the warp
        # policy is ever reached.
        state = self._coasting(params, phase_entry_ut=11_838_000.0)
        in_mun = mlib.TelemetrySnapshot(
            ut=11_838_936.0, body="Mun", altitude=2_215_441.0,
            time_to_soi=3_086.0, node_count=0)
        _after, actions = mlib.b5_decide(state, in_mun)
        self.assertIn(
            mlib.Action(mlib.ACTION_WARP_TO_UT,
                        11_838_936.0 + 3_086.0 - params.soi_lead),
            actions,
            "the Mun transit must warp natively to the SOI boundary, not "
            "crawl the floor-2 rails stair: %r" % (actions,))
        self.assertFalse(
            any(a.kind == mlib.ACTION_SET_RAILS_WARP for a in actions),
            actions)

    def test_the_correction_approach_warp_is_unchanged_for_b7(self):
        """The no-op proof for the three green Duna flights: B7's via list IS
        its correction domain, so the same heliocentric frame produces the same
        native trigger-approach warp before and after."""
        params = mlib.b5_params_from_dict(B7_PARAMS)
        state = self._coasting(params, phase_entry_ut=900_000.0)
        frame = mlib.TelemetrySnapshot(
            ut=1_000_000.0, body="Sun", altitude=13_000_000_000.0,
            time_to_soi=25_000_000.0, node_count=0)
        _after, actions = mlib.b5_decide(state, frame)
        # dt = 25e6 - 20e6 = 5e6 > soi_lead, so the trigger approach owns it.
        self.assertIn(
            mlib.Action(mlib.ACTION_WARP_TO_UT, 1_000_000.0 + 5_000_000.0),
            actions, actions)

    def test_gilly_is_absent_from_every_eve_legal_body_list(self):
        """The Gilly decision, pinned at the param level as well as the frame
        level: Gilly must never be a via body or a return body on either lane,
        because either would turn a Gilly capture into a silent PASS."""
        for params in (B15_PARAMS, B16_PARAMS):
            resolved = mlib.b5_params_from_dict(params)
            self.assertNotIn("Gilly", resolved.via_bodies)
            self.assertNotEqual(resolved.return_body, "Gilly")

    def test_the_moon_and_landing_families_are_untouched_by_the_eve_lane(self):
        """The existing-families-are-unaffected cell. B5/B6 (moon flyby),
        B11/B12 (moon orbit) and B13/B14 (moon landing) must still resolve to
        their own shapes with the Eve keys nowhere in sight. mlib gained nothing
        for Eve, so this is the assertion that says so out loud."""
        moon_flyby = mlib.b5_params_from_dict(B5_PARAMS)
        self.assertFalse(moon_flyby.interplanetary_transfer)
        self.assertEqual(moon_flyby.via_bodies, ())
        self.assertEqual(moon_flyby.return_body, "")
        self.assertEqual(moon_flyby.ejection_ecc_floor, 0.0)
        self.assertFalse(moon_flyby.capture_enabled)

        moon_orbit = mlib.b5_params_from_dict(B11_PARAMS)
        self.assertFalse(moon_orbit.interplanetary_transfer)
        self.assertTrue(moon_orbit.capture_enabled)
        self.assertFalse(moon_orbit.landing_enabled)
        # The moon lanes keep ALTITUDE-mode correction triggers; only the
        # interplanetary lanes use the time-to-SOI mode.
        self.assertEqual(moon_orbit.correction_trigger_time_to_soi, ())
        self.assertNotEqual(moon_orbit.correction_trigger_alts, ())

        moon_landing = mlib.b5_params_from_dict(B13_PARAMS)
        self.assertTrue(moon_landing.landing_enabled)
        self.assertFalse(moon_landing.interplanetary_transfer)


class MissionParamsMatchTheSpecsTests(unittest.TestCase):
    """THE `*_PARAMS` DICTS ABOVE ARE ONLY WORTH ANYTHING IF THEY MATCH THE
    SPECS THEY CLAIM TO MIRROR, and until this class existed nothing checked
    that.

    Every one of those dicts says "spec-shaped params (mirrors
    harness/scenarios/<id>.toml)". They are also the DATA the lane-level claims
    are proved over -- `EveLaneIsAParameterChangeTests` computes
    `set(B15_PARAMS) - set(B7_PARAMS)` and concludes the Eve lane adds exactly
    one key of machine surface. That conclusion is about the SHIPPED specs, so
    it is only true if `B7_PARAMS`'s key set is the B7 spec's key set. Nothing
    enforced that, and the drift was real: when this class was written
    `B11_PARAMS` was missing SEVEN keys the B11 spec carries and `B7_PARAMS`
    carried FIVE stale values (an `apoErrorMeters` of 15,000 against the spec's
    150,000, and so on). All of it is fixed above; this cell is what keeps it
    fixed.

    THE DIVERGENCE TABLES BELOW ARE EXACT-MATCH, NOT AN ALLOWLIST CEILING. A
    divergence that DISAPPEARS fails this test just as loudly as a new one, so
    the tables cannot quietly rot into a list of things nobody checks. Each
    entry names why the fixture dict deliberately differs."""

    _PAIRS = (
        ("B1_PARAMS", "B1-pad-hop"),
        ("B2_PARAMS", "B2-lko-ascent"),
        ("B4_PARAMS", "B4-reentry-splashdown"),
        ("B5_PARAMS", "B5-mun-flyby"),
        ("B7_PARAMS", "B7-duna-flyby"),
        ("B11_PARAMS", "B11-mun-orbit"),
        ("B13_PARAMS", "B13-mun-landing"),
        ("B15_PARAMS", "B15-eve-flyby"),
        ("B16_PARAMS", "B16-eve-orbit"),
    )

    # spec id -> (keys the spec has and the dict does not,
    #             keys the dict has and the spec does not)
    _KEY_DIVERGENCES = {
        # `launchSiteLatitude` is OPTIONAL in b2_lko_ascent.schema.toml and the
        # B2 spec omits it (mlib defaults it to 0.0). The fixture sets it
        # EXPLICITLY, to the same 0.0, so the optional-key parse path is
        # exercised by a fake flight instead of only by a schema unit test.
        # Behaviourally identical to the spec; kept deliberately.
        "B2-lko-ascent": ((), ("launchSiteLatitude",)),
    }

    # spec id -> {key: (fixture value, spec value)}
    _VALUE_DIVERGENCES = {
        # The scripted B4 reentry frames are written around a 45,000 m warp
        # threshold; at the spec's 70,000 the fake flight's warp hop never
        # fires and B4ShellTests' happy path stops asserting it. A FIXTURE
        # number, not a stale copy of a spec number.
        "B4-reentry-splashdown": {"warpAboveAltMeters": (45000, 70000)},
    }

    @staticmethod
    def _spec_params(spec_id):
        path = os.path.join(_HARNESS, "scenarios", spec_id + ".toml")
        with open(path, "rb") as handle:
            return tomllib.load(handle)["driver"]["missionParams"]

    def _diff(self, dict_name, spec_id):
        fixture = globals()[dict_name]
        spec = self._spec_params(spec_id)
        missing = tuple(sorted(set(spec) - set(fixture)))
        extra = tuple(sorted(set(fixture) - set(spec)))
        values = {k: (fixture[k], spec[k])
                  for k in sorted(set(fixture) & set(spec))
                  if fixture[k] != spec[k]}
        return missing, extra, values

    def test_every_fixture_dict_carries_exactly_its_specs_keys(self):
        """The load-bearing half: KEY SETS. A key in the spec but not the
        fixture means the fixture proves nothing about that param; a key in the
        fixture but not the spec means the fixture is testing a param the lane
        does not actually ship. Either way the subset arithmetic other cells
        run over these dicts is arithmetic over the wrong data."""
        for dict_name, spec_id in self._PAIRS:
            with self.subTest(spec=spec_id):
                missing, extra, _values = self._diff(dict_name, spec_id)
                expected = self._KEY_DIVERGENCES.get(spec_id, ((), ()))
                self.assertEqual(
                    (missing, extra), expected,
                    "%s key set drifted from %s.toml. Sync the dict, or add "
                    "the divergence to _KEY_DIVERGENCES with the reason."
                    % (dict_name, spec_id))

    def test_every_fixture_dict_carries_exactly_its_specs_values(self):
        """The weaker half, kept honest rather than skipped. A fake flight may
        legitimately need a different NUMBER than the shipped spec (see
        _VALUE_DIVERGENCES), but every such case must be a named decision, not
        a value someone forgot to update when the spec moved."""
        for dict_name, spec_id in self._PAIRS:
            with self.subTest(spec=spec_id):
                _missing, _extra, values = self._diff(dict_name, spec_id)
                expected = self._VALUE_DIVERGENCES.get(spec_id, {})
                self.assertEqual(
                    values, expected,
                    "%s values drifted from %s.toml. Sync the dict, or add "
                    "the divergence to _VALUE_DIVERGENCES with the reason."
                    % (dict_name, spec_id))

    def test_the_two_eve_dicts_are_byte_for_byte_their_specs(self):
        """CALLED OUT SEPARATELY because the Eve lane's central claim is
        computed over B15_PARAMS / B16_PARAMS: they must carry NO divergence of
        either kind, so `set(B15_PARAMS) - set(B7_PARAMS) == {parkTrimEccMax}`
        is a statement about the shipped specs and not about two dicts that
        happen to live in a test file."""
        for dict_name, spec_id in (("B15_PARAMS", "B15-eve-flyby"),
                                   ("B16_PARAMS", "B16-eve-orbit")):
            with self.subTest(spec=spec_id):
                self.assertEqual(self._diff(dict_name, spec_id), ((), (), {}))
                self.assertNotIn(spec_id, self._KEY_DIVERGENCES)
                self.assertNotIn(spec_id, self._VALUE_DIVERGENCES)


# ---------------------------------------------------------------------------
# Fly-loop liveness + accounting wiring (2026-07-25 review). These live in the
# SHELL tests, not mlib's, because they need the WALL clock the pure decision
# library deliberately does not have.
# ---------------------------------------------------------------------------


class _StepClock:
    """A wall clock the TEST advances. FakeClock ticks on every READ, which
    would make a wall-span assertion depend on how many times the loop happens
    to call clock()."""

    def __init__(self):
        self.t = 0.0

    def advance(self, dt):
        self.t += dt

    def __call__(self):
        return self.t


class WarpLivenessFloorWiringTests(unittest.TestCase):
    """The fly-loop half of the native-warp liveness floor: nothing bounded a
    warp that was ARMED ONCE and simply crawled. The runner's warp-stall
    watchdog needs UT to FREEZE (a crawling warp advances UT), and a GAME-time
    phase budget is either advanced by the crawl or -- for CORRECTION-BURN's
    aim-warp -- suppressed outright."""

    def _fly(self, ut_per_frame, wall_per_frame, armed=True, frames=600,
             blind_frames=0):
        clock = _StepClock()
        log = mission_runner.MissionLogger(sink=lambda _l: None, clock=clock)
        state = mlib.b5_initial_state(mlib.b5_params_from_dict(dict(B5_PARAMS)))
        state = replace(state, phase=mlib.B5_COAST_TO_TARGET,
                        phase_entry_ut=0.0,
                        warp_to_cmd=(1e12 if armed else None))

        seen = {"n": 0}

        def decide(st, snapshot):
            clock.advance(wall_per_frame)
            seen["n"] += 1
            # A benign terminal once the scripted frames run out, so a NEGATIVE
            # cell (the floor must NOT fire) ends cleanly instead of spinning.
            if seen["n"] >= frames:
                return replace(st, done=True), []
            return st, []

        snaps = [snap(ut=(float("nan") if i < blind_frames
                          else ut_per_frame * i),
                      body="Kerbin",
                      altitude=8_000_000.0, warp_mode="RAILS", warp_rate=2.68,
                      warping_to=1e12)
                 for i in range(frames)]
        control = FakeMissionControl(snaps, max_last_repeats=4)
        final, _ = mission_runner.fly_loop(
            control, state, decide, log, deadline=1e12, clock=clock,
            sleep=lambda _s: None, poll_interval=0.0, settle_frames=0,
            allow_rails_warp=True)
        return final

    def test_a_crawling_armed_warp_fast_fails_with_its_own_name(self):
        """B12 flight 2's measured shape: ~1.3 game-s per wall-s while a native
        warp is armed. Before this the ONLY bound was the generic un-named wall
        reaper."""
        final = self._fly(ut_per_frame=1.3, wall_per_frame=1.0)
        self.assertTrue(final.done)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason)
        self.assertIn("running but not warping", final.flake_reason)
        verdict, reason = mlib.resolve_flight_verdict(final, [])
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, reason)

    def test_a_genuinely_warping_armed_warp_is_never_touched(self):
        """A real rails warp reads hundreds to thousands of game-s per wall-s;
        the floor must not come near it."""
        final = self._fly(ut_per_frame=1000.0, wall_per_frame=1.0, frames=400)
        self.assertNotEqual(final.verdict, mlib.MISSION_FLAKE)

    def test_an_unarmed_1x_phase_is_never_judged(self):
        """THE guard against killing every deliberate 1x phase (B5's PARK
        dwell, all of B1/B2/B4, the whole FORGE / B-DOCK family): the episode is
        armed by the machine's OWN outstanding native warp command."""
        final = self._fly(ut_per_frame=0.5, wall_per_frame=1.0, armed=False)
        self.assertNotEqual(final.verdict, mlib.MISSION_FLAKE)

    def test_a_blind_ut_on_the_arming_frame_does_not_disarm_the_floor(self):
        """FAIL-OPEN HOLE (2026-07-26 review). If snapshot.ut read non-finite
        on the very frame the episode armed, wl_ut_start was never taken -- and
        no later branch took it, because the judging branch REQUIRES it
        non-None. The floor stayed disarmed for the whole episode. A liveness
        guard that fails open on its own arming frame is worse than none, since
        the roadmap counts it as covering the shape. The re-stamp branch takes
        the baseline on the first frame that has a real UT."""
        final = self._fly(ut_per_frame=1.3, wall_per_frame=1.0, blind_frames=3)
        self.assertTrue(final.done)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason)

    def test_the_re_stamp_does_not_bill_the_blind_frames_against_the_ratio(self):
        """The re-stamp resets the WALL clock with the UT baseline, so the
        judged window starts from a frame that has both. A genuinely fast warp
        that happened to arm on a blind frame must still never be judged
        starved."""
        final = self._fly(ut_per_frame=1000.0, wall_per_frame=1.0, frames=400,
                          blind_frames=3)
        self.assertNotEqual(final.verdict, mlib.MISSION_FLAKE)


class WarpLivenessRealMachineTests(unittest.TestCase):
    """The floor driven by the REAL b5 machine, not a stub decide.

    WHY THIS EXISTS ON TOP OF WarpLivenessFloorWiringTests (2026-07-26). Those
    cells pin the fly-loop WIRING with a decide that returns its state
    unchanged, so ``warp_to_cmd`` stays armed BY CONSTRUCTION. That assumes away
    the load-bearing question: does the real machine actually HOLD an armed
    native warp across a starved episode, or does it clear the command and reset
    the window? For B12 flight 2 the answer was "clears it" -- 3,603
    ``warp_to_ut`` against 3,602 ``cancel_warp``, the episode never lasting two
    frames -- which is why this floor could NOT have caught that flight and the
    thrash counter is what bounds it.

    What the floor DOES bound is the post-fix residual, and that is what these
    cells fly: ``coast_native_warp_hold`` removed the cancel half of the cycle,
    so a blind ``time_to_soi`` under warp now HOLDS the command, while flight
    2's other half (a rails rate that never escaped 2.76x) is untouched by that
    fix. Held command + crawling rate is an unbounded episode that no other
    guard can see. The telemetry below is flight 2's own, post-fix: same body,
    same altitude band, same 2.76x rails rate, same measured 1.41 game-seconds
    per wall-second."""

    THRASH_RAILS_RATE = 2.76      # measured, flight 2's final coast
    STARVED_GAME_PER_WALL = 1.41  # measured, ditto (median 7.105 game-s / 5 s)
    COAST_ALTITUDE = 49_028_969.0

    def _fly_real_machine(self, game_per_wall, frames, wall_per_frame=1.0,
                          rails_rate=None, control_factory=None):
        clock = _StepClock()
        log = mission_runner.MissionLogger(sink=lambda _l: None, clock=clock)
        state = mlib.b5_initial_state(mlib.b5_params_from_dict(dict(B5_PARAMS)))
        # rounds_done = both correction rounds spent, which is exactly where
        # flight 2 sat when its coast went bad (its status.json reads
        # `rounds: 2`). Without it the ALTITUDE trigger for round 0 is 0.0 m and
        # the machine correctly plans a correction on frame 1 instead of
        # coasting.
        state = replace(state, phase=mlib.B5_COAST_TO_TARGET, phase_entry_ut=0.0,
                        correction_rounds_done=len(
                            state.params.correction_trigger_alts))
        rails_rate = self.THRASH_RAILS_RATE if rails_rate is None else rails_rate
        # The native target must stay AHEAD for the whole script, else the
        # machine legitimately disarms on arrival and the cell stops testing the
        # armed episode.
        tts = game_per_wall * wall_per_frame * frames * 4.0 + 40_000.0

        seen = {"n": 0}

        def decide(st, snapshot):
            clock.advance(wall_per_frame)
            seen["n"] += 1
            # A benign terminal once the script runs out, so a NEGATIVE cell
            # ends cleanly instead of spinning (same shape as _fly above).
            if seen["n"] >= frames:
                return replace(st, done=True), []
            return mlib.b5_decide(st, snapshot)

        # Frame 0 ARMS the native warp off a readable time_to_soi. Every later
        # frame is the post-fix blind read under warp that the hold keeps armed.
        snaps = [snap(ut=0.0, body="Kerbin", altitude=self.COAST_ALTITUDE,
                      time_to_soi=tts, warp_mode="NONE", warp_rate=1.0)]
        snaps += [snap(ut=game_per_wall * wall_per_frame * i, body="Kerbin",
                       altitude=self.COAST_ALTITUDE,
                       time_to_soi=float("nan"), warp_mode="RAILS",
                       warp_rate=rails_rate, warping_to=tts - 30.0)
                  for i in range(1, frames)]
        control = (control_factory or FakeMissionControl)(snaps,
                                                          max_last_repeats=4)
        final, _ = mission_runner.fly_loop(
            control, state, decide, log, deadline=1e12, clock=clock,
            sleep=lambda _s: None, poll_interval=0.0, settle_frames=0,
            allow_rails_warp=True)
        return final, control

    # THE EPISODE-RESET SCRIPT (2026-07-26 review round 2, BLOCKER-2). The one
    # line that makes the liveness ratio EPISODE-local rather than cumulative is
    # `if not armed: wl_wall_start = None; wl_ut_start = None` in the fly loop,
    # and replacing it with `pass` survived the whole mission suite. This script
    # is what kills that mutation: the real machine ARMS a native warp, ARRIVES
    # (so the machine itself disarms), holds a long deliberate 1x stretch, then
    # RE-ARMS and warps for real. Without the reset the stale baseline bills the
    # 1x hold against the re-armed episode's ratio and the floor false-fires on
    # a healthy flight -- which is not hypothetical: nine archived PARK rows run
    # 180.2-180.6 wall-seconds at ratio 0.999, i.e. just past the judging window
    # and 5x under the floor.
    HOLD_WALL_SECONDS = 600.0     # the deliberate 1x stretch, ~3.3x the window
    REARMED_GAME_PER_WALL = 300.0  # the re-armed episode, genuinely warping

    def _fly_disarm_rearm(self, rearmed_game_per_wall=None,
                          rearmed_frames=250, hold_frames=600,
                          arm_frames=12, wall_per_frame=1.0):
        """Three scripted stretches through the REAL b5 coast machine:

        1. ARM: a readable ``time_to_soi`` of 40 s arms a native warp to
           ``ut + 10`` (soi_lead 30). The machine holds it while UT crawls to
           the target, then ``_b5_clear_arrived_warp`` DISARMS on arrival -- the
           machine's own disarm, not a test-injected one.
        2. HOLD: ``hold_frames`` of deliberate, unarmed 1x (blank encounter, no
           warp). This is the stretch a stale baseline would bill.
        3. RE-ARM: a fresh readable ``time_to_soi`` re-arms the native warp and
           the game warps at ``rearmed_game_per_wall`` game-s per wall-s for
           ``rearmed_frames`` -- long past WARP_LIVENESS_MIN_WALL_SECONDS, so
           the episode IS judged rather than skipped.
        """
        game_per_wall = (self.REARMED_GAME_PER_WALL
                         if rearmed_game_per_wall is None
                         else rearmed_game_per_wall)
        clock = _StepClock()
        log = mission_runner.MissionLogger(sink=lambda _l: None, clock=clock)
        state = mlib.b5_initial_state(mlib.b5_params_from_dict(dict(B5_PARAMS)))
        state = replace(state, phase=mlib.B5_COAST_TO_TARGET, phase_entry_ut=0.0,
                        correction_rounds_done=len(
                            state.params.correction_trigger_alts))
        total = arm_frames + hold_frames + rearmed_frames
        seen = {"n": 0}

        def decide(st, snapshot):
            clock.advance(wall_per_frame)
            seen["n"] += 1
            if seen["n"] >= total:
                return replace(st, done=True), []
            return mlib.b5_decide(st, snapshot)

        common = dict(body="Kerbin", altitude=self.COAST_ALTITUDE)
        # 1. ARM: target = ut + time_to_soi - soi_lead = 0 + 40 - 30 = 10.
        snaps = [snap(ut=0.0, time_to_soi=40.0, warp_mode="NONE",
                      warp_rate=1.0, **common)]
        snaps += [snap(ut=float(i), time_to_soi=float("nan"), warp_mode="RAILS",
                       warp_rate=2.0, warping_to=10.0, **common)
                  for i in range(1, arm_frames)]
        # 2. HOLD: unarmed, un-warped, 1 game-s per wall-s. The altitude walks
        # (a real 1x coast never repeats a float): these are the only warp_mode
        # NONE frames in the script, and the frozen-telemetry detector advances
        # ONLY at 1x, so a bit-identical altitude here would trip vessel-lost at
        # frame 10 and the script would never reach the re-arm.
        hold_start = float(arm_frames)
        snaps += [snap(ut=hold_start + i, time_to_soi=float("nan"),
                       warp_mode="NONE", warp_rate=1.0, body="Kerbin",
                       altitude=self.COAST_ALTITUDE + i * 0.5)
                  for i in range(hold_frames)]
        # 3. RE-ARM: a readable encounter far enough ahead that the target stays
        # ahead for the whole stretch, then a genuinely fast warp.
        rearm_ut = hold_start + hold_frames
        rearm_tts = game_per_wall * wall_per_frame * rearmed_frames * 4.0 + 40_000.0
        snaps += [snap(ut=rearm_ut, time_to_soi=rearm_tts, warp_mode="NONE",
                       warp_rate=1.0, **common)]
        snaps += [snap(ut=rearm_ut + game_per_wall * wall_per_frame * i,
                       time_to_soi=float("nan"), warp_mode="RAILS",
                       warp_rate=1000.0,
                       warping_to=rearm_ut + rearm_tts - 30.0, **common)
                  for i in range(1, rearmed_frames)]
        control = FakeMissionControl(snaps, max_last_repeats=4)
        final, _ = mission_runner.fly_loop(
            control, state, decide, log, deadline=1e12, clock=clock,
            sleep=lambda _s: None, poll_interval=0.0, settle_frames=0,
            allow_rails_warp=True)
        return final, control

    def test_a_healthy_rearmed_warp_is_judged_on_its_own_episode_not_the_1x_hold(self):
        """KILLS the `if not armed: pass` mutation. The re-armed warp is fast
        (300 game-s per wall-s, 60x the floor) and the deliberate 1x hold before
        it is 600 wall-seconds long. With the reset the episode is judged from
        the re-arming frame and runs clean; without it the judged window starts
        600 wall-seconds and ~612 game-seconds earlier, so the FIRST re-armed
        frame reads ~1.0 game-s/wall-s and the floor kills a healthy flight."""
        final, control = self._fly_disarm_rearm()
        self.assertNotEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertNotIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason or "")
        # NOT VACUOUS: the machine really did disarm and re-arm, so the reset
        # branch was genuinely taken (one arm per armed stretch, and the second
        # can only be issued from a disarmed state).
        arms = [a for a in control.actions if a.kind == mlib.ACTION_WARP_TO_UT]
        self.assertEqual(len(arms), 2, control.actions[:12])
        # And the re-armed episode is long enough to BE judged, so the pass is
        # not "the window never opened".
        self.assertGreater(250 * 1.0, mlib.WARP_LIVENESS_MIN_WALL_SECONDS)

    def test_the_rearmed_episode_is_still_judged_after_the_reset(self):
        """The negative control for the cell above: same script, but the
        re-armed warp crawls at the measured 1.41 game-s/wall-s. The reset must
        restart the window, NOT disarm the guard -- so this one still fires."""
        final, control = self._fly_disarm_rearm(
            rearmed_game_per_wall=self.STARVED_GAME_PER_WALL)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason)
        arms = [a for a in control.actions if a.kind == mlib.ACTION_WARP_TO_UT]
        self.assertEqual(len(arms), 2, control.actions[:12])

    def test_the_real_coast_machine_holds_a_starved_warp_until_the_floor_fires(self):
        """The shape the floor exists for, flown by the real machine: the hold
        keeps ONE armed command outstanding while the rate crawls, and the floor
        is the only guard that can end it."""
        final, control = self._fly_real_machine(
            self.STARVED_GAME_PER_WALL, frames=600)
        self.assertTrue(final.done)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason)
        self.assertIn("running but not warping", final.flake_reason)
        self.assertEqual(final.phase, mlib.B5_COAST_TO_TARGET)
        verdict, reason = mlib.resolve_flight_verdict(final, [])
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, reason)

    def test_the_thrash_counter_cannot_take_the_credit_for_this_shape(self):
        """The two guards are complements, and this cell proves they do not
        overlap: the hold means the machine issues the native warp exactly ONCE
        for the whole episode, so the thrash cap (500) is three orders of
        magnitude away from firing. Whatever ends this flight, it is not the
        thrash counter."""
        final, control = self._fly_real_machine(
            self.STARVED_GAME_PER_WALL, frames=600)
        issues = [a for a in control.actions if a.kind == mlib.ACTION_WARP_TO_UT]
        self.assertEqual(len(issues), 1, control.actions[:8])
        self.assertEqual(final.phase_warp_issues, 1)
        self.assertNotIn(mlib.WARP_THRASH_COAST, final.flake_reason)

    def test_the_floor_terminal_tears_the_warp_down_before_returning(self):
        """LEAVE NOTHING WARPED BEHIND (2026-07-26 review round 2).

        The shipped version returned here with the warp still armed, justified
        by a comment asserting "nothing drives the game afterwards". That was
        FALSE: `hlib.plan_unmet_mission_tail` drives the TAIL_ROLE_CLEANUP verbs
        (StopRecording, FlushAndQuit) after ANY unmet mission, and MISSION-FLAKE
        is unmet exactly like an ASSERT-FAIL -- so the seam was driven against a
        rails-warping game, the one thing every MACHINE terminal
        (`_b5_stop_all_warp`) is careful not to do. This terminal fires ONLY
        while a warp is armed, so it is the fly-loop terminal that owes the
        teardown most. It now performs a cancel inline, best-effort, and the
        returned state stops claiming an armed command."""
        final, control = self._fly_real_machine(
            self.STARVED_GAME_PER_WALL, frames=600)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason)
        self.assertEqual(control.actions[-1].kind, mlib.ACTION_CANCEL_WARP)
        self.assertIsNone(final.warp_to_cmd)
        self.assertEqual(final.warp_cmd, 0)

    def test_a_teardown_that_raises_never_destroys_the_named_give_up(self):
        """The reason the teardown is best-effort rather than plain. A cancel
        that dies on a degraded connection must NOT escape as a post-connect
        drop: the named `warp-liveness-starved` verdict is the entire value of
        this terminal, and a generic transport flake would erase it."""
        class _CancelRaises(FakeMissionControl):
            def perform(self, action):
                FakeMissionControl.perform(self, action)
                if action.kind == mlib.ACTION_CANCEL_WARP:
                    raise ConnectionResetError("fake: warp socket died on cancel")

        final, control = self._fly_real_machine(
            self.STARVED_GAME_PER_WALL, frames=600,
            control_factory=_CancelRaises)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason)
        self.assertEqual(control.actions[-1].kind, mlib.ACTION_CANCEL_WARP)
        # And the state does NOT claim a teardown that did not happen: the
        # command stays armed when the cancel could not be delivered.
        self.assertIsNotNone(final.warp_to_cmd)

    def test_the_real_machine_on_a_genuinely_warping_coast_is_never_judged_starved(self):
        """The negative that matters: the SAME machine, the SAME hold, an armed
        episode judged well past the 180 s window (300 wall-seconds here), but
        warping at a real rate. It must run clean."""
        final, _ = self._fly_real_machine(1000.0, frames=300, rails_rate=1000.0)
        self.assertNotEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertNotIn(mlib.WARP_LIVENESS_GIVEUP, final.flake_reason or "")


class WarpUtilisationUnwindTests(unittest.TestCase):
    """_wu_close ran on every NORMAL fly-loop exit and none of the raising
    ones, so a crashed run silently lost its FINAL phase's warp-utilisation row
    -- exactly the phase whose warp accounting a post-mortem needs."""

    def test_a_mid_flight_raise_still_closes_the_open_row(self):
        clock = FakeClock()
        log = mission_runner.MissionLogger(sink=lambda _l: None, clock=clock)
        state = mlib.b5_initial_state(mlib.b5_params_from_dict(dict(B5_PARAMS)))
        state = replace(state, phase=mlib.B5_COAST_TO_TARGET,
                        phase_entry_ut=0.0)
        control = FakeMissionControl(
            [snap(ut=10.0, body="Kerbin", altitude=8_000_000.0),
             snap(ut=20.0, body="Kerbin", altitude=8_000_000.0)],
            raise_on_read_index=2,
            raise_exc=ConnectionResetError("fake socket died mid-flight"))
        with self.assertRaises(ConnectionResetError):
            mission_runner.fly_loop(
                control, state, lambda st, s: (st, []), log, deadline=1e12,
                clock=clock, sleep=lambda _s: None, poll_interval=0.0,
                settle_frames=0, allow_rails_warp=True)
        rows = list(mission_runner._FLY_LOOP_WARP_UTILISATION)
        self.assertEqual(len(rows), 1, rows)
        self.assertEqual(rows[0]["phase"], mlib.B5_COAST_TO_TARGET)
        # A REAL game span, taken from the last finite UT -- not a null.
        self.assertEqual(rows[0]["gameSeconds"], 10.0)

    def test_the_row_is_not_double_closed(self):
        """The accumulator no-ops when the row is already closed, so a normal
        terminal followed by the wrapper's unwind path cannot emit twice."""
        wu = mission_runner._WarpUtilisation(FakeClock())
        del mission_runner._FLY_LOOP_WARP_UTILISATION[:]
        wu.begin("PHASE", 0.0)
        wu.close(50.0)
        wu.close(50.0)
        self.assertEqual(len(mission_runner._FLY_LOOP_WARP_UTILISATION), 1)


class _FakeTimeSelector:
    def __init__(self, accept=True, initial="APOAPSIS"):
        object.__setattr__(self, "_accept", accept)
        object.__setattr__(self, "time_reference", initial)

    def __setattr__(self, name, value):
        if name == "time_reference" and not object.__getattribute__(self, "_accept"):
            raise RuntimeError("OperationException: reference not allowed")
        object.__setattr__(self, name, value)


class _StickyTimeSelector(_FakeTimeSelector):
    """Accepts the write and keeps its old value -- indistinguishable from a
    throw, downstream, which is exactly why the READ BACK is the point."""

    def __setattr__(self, name, value):
        if name == "time_reference" and hasattr(self, "time_reference"):
            return
        object.__setattr__(self, name, value)


class CapturePlanTimeSelectorTests(unittest.TestCase):
    """ACTION_MJ_PLAN_CAPTURE must not plan on a REFUSED time-reference set.

    TimeSelector's setter throws OperationException on a disallowed reference
    (pinned KRPC.MechJeb Maneuver/TimeSelector.cs:120-124) and its backing
    currentTimeRef is SHARED, PERSISTED MechJeb state, so the old
    swallow-and-plan-anyway shape let INHERITED global state place a valid
    capture node at an arbitrary UT -- the same commanded-vs-OBSERVED gap this
    branch closed for NodeExecutor.Enabled."""

    class _Op:
        def __init__(self, selector):
            self.time_selector = selector
            self.made = 0

        def make_nodes(self):
            self.made += 1

    class _Planner:
        def __init__(self, op):
            self.operation_circularize = op

    class _TimeReference:
        periapsis = "PERIAPSIS"

    class _MechJeb:
        def __init__(self, planner, reference):
            self.maneuver_planner = planner
            self.TimeReference = reference

    class _Control:
        nodes = ()

    class _Vessel:
        def __init__(self):
            self.control = CapturePlanTimeSelectorTests._Control()

    class _Sc:
        def __init__(self, vessel):
            self.active_vessel = vessel

    class _Conn:
        def __init__(self, sc):
            self.space_center = sc

    def _perform_plan_capture(self, selector):
        op = self._Op(selector)
        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True)
        ctrl._mechjeb = self._MechJeb(self._Planner(op), self._TimeReference)
        ctrl._conn = self._Conn(self._Sc(self._Vessel()))
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            ctrl.perform(mlib.Action(mlib.ACTION_MJ_PLAN_CAPTURE))
        finally:
            mission_runner._stdout_sink = orig
        return op, lines

    def test_a_confirmed_periapsis_reference_plans(self):
        op, lines = self._perform_plan_capture(_FakeTimeSelector(accept=True))
        self.assertEqual(op.made, 1)
        self.assertEqual(op.time_selector.time_reference, "PERIAPSIS")
        self.assertTrue(any("READ BACK confirmed" in l for l in lines), lines)

    def test_a_throwing_setter_refuses_to_plan(self):
        op, lines = self._perform_plan_capture(_FakeTimeSelector(accept=False))
        self.assertEqual(op.made, 0,
                         "a refused time-reference set must NOT plan: MechJeb "
                         "would place the node at its inherited reference")
        self.assertTrue(any("REFUSING to plan" in l for l in lines), lines)

    def test_a_silently_ignored_setter_refuses_to_plan(self):
        op, lines = self._perform_plan_capture(_StickyTimeSelector())
        self.assertEqual(op.made, 0)
        self.assertTrue(any("READ BACK" in l for l in lines), lines)


class InterplanetaryPlanDiagnosticIsolationTests(unittest.TestCase):
    """THE FIFTH SHARED CHANGE, and the only one that is NOT param-gated.

    The Eve lane's four machine changes are all gated behind a param B7 does
    not set, so B7/Duna is byte-identical. The plan diagnostic is not: it runs
    on EVERY `ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER`, B7's included. It is
    inert by construction (read-only kRPC reads, and the machine keys on
    `node_count`, which a log line cannot change) -- but it was originally
    written INSIDE the try that wraps `make_nodes()`, so a raise from its own
    unguarded tail (the patches loop, the %-format, the sink) would have been
    reported as "operation_interplanetary_transfer.make_nodes failed", a false
    plan-FAILURE message on a plan that SUCCEEDED. That is precisely the
    misleading-message class this lane exists to eliminate, and it would have
    landed on B7's path.

    These cells pin the split: a raising diagnostic costs the observability
    line and NOTHING else."""

    class _Op:
        def __init__(self, nodes):
            self._nodes = nodes
            self.made = 0
            self.wait_for_phase_angle = False

        def make_nodes(self):
            self.made += 1
            return self._nodes

    class _Planner:
        def __init__(self, op):
            self.operation_interplanetary_transfer = op

    class _MechJeb:
        def __init__(self, planner):
            self.maneuver_planner = planner

    class _Control:
        def __init__(self, nodes):
            self.nodes = nodes

    class _Vessel:
        def __init__(self, nodes):
            self.control = InterplanetaryPlanDiagnosticIsolationTests._Control(nodes)

    class _Sc:
        def __init__(self, vessel):
            self.active_vessel = vessel
            self.target_body = None

    class _Conn:
        def __init__(self, sc):
            self.space_center = sc

    def _perform(self, diagnostic):
        """Run the plan action with `_log_transfer_plan_diagnostic` replaced by
        `diagnostic`. Returns (op, control, log lines)."""
        planned = ["node-0"]
        op = self._Op(planned)
        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True)
        ctrl._mechjeb = self._MechJeb(self._Planner(op))
        vessel = self._Vessel(planned)
        ctrl._conn = self._Conn(self._Sc(vessel))
        ctrl._log_transfer_plan_diagnostic = diagnostic
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            ctrl.perform(
                mlib.Action(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER))
        finally:
            mission_runner._stdout_sink = orig
        return op, vessel.control, lines

    def test_a_raising_diagnostic_does_not_corrupt_the_plan_verdict(self):
        """The node the planner made STAYS on the board and `make_nodes` is
        called exactly once. The machine reads `node_count`; the diagnostic
        cannot touch it, and after the split it cannot make the runner behave
        as though the plan had thrown either."""
        def boom(sc, planned):
            raise RuntimeError("patches walk blew up")

        op, control, _lines = self._perform(boom)
        self.assertEqual(op.made, 1)
        self.assertEqual(len(control.nodes), 1,
                         "a diagnostic fault must never remove the plan")
        self.assertTrue(op.wait_for_phase_angle)

    def test_a_raising_diagnostic_does_not_corrupt_the_plan_MESSAGE(self):
        """The load-bearing half. The log must NOT say the plan failed -- it
        must name the diagnostic and say the plan succeeded, or the next
        investigation is sent at the planner instead of at this function."""
        def boom(sc, planned):
            raise RuntimeError("patches walk blew up")

        _op, _control, lines = self._perform(boom)
        self.assertFalse(
            any("make_nodes failed" in l for l in lines),
            "a diagnostic fault reported as a plan failure is the exact "
            "misleading message this lane exists to remove: %r" % (lines,))
        self.assertTrue(
            any("plan diagnostic raised" in l and "PLAN ITSELF SUCCEEDED" in l
                for l in lines), lines)
        self.assertTrue(any("RuntimeError" in l for l in lines), lines)

    def test_a_genuinely_failing_plan_still_reports_a_plan_failure(self):
        """The other direction: splitting the try must not have made a REAL
        `make_nodes` throw quiet. It still reports as a plan failure, and the
        diagnostic is never reached."""
        class Boom:
            wait_for_phase_angle = False
            made = 0

            def make_nodes(self):
                raise RuntimeError("no transfer window")

        op = Boom()
        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True)
        ctrl._mechjeb = self._MechJeb(self._Planner(op))
        ctrl._conn = self._Conn(self._Sc(self._Vessel([])))
        called = []
        ctrl._log_transfer_plan_diagnostic = lambda sc, planned: called.append(1)
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            ctrl.perform(
                mlib.Action(mlib.ACTION_MJ_PLAN_INTERPLANETARY_TRANSFER))
        finally:
            mission_runner._stdout_sink = orig
        self.assertEqual(called, [], "no plan, nothing to diagnose")
        self.assertTrue(
            any("operation_interplanetary_transfer.make_nodes failed" in l
                for l in lines), lines)

    def test_a_healthy_plan_still_runs_the_diagnostic_once(self):
        """The happy path is unchanged: one plan, one diagnostic call, with the
        nodes `make_nodes` RETURNED (never `control.nodes`, the flight-4
        leftover-node lesson)."""
        seen = []
        _op, _control, lines = self._perform(
            lambda sc, planned: seen.append(planned))
        self.assertEqual(seen, [["node-0"]])
        self.assertFalse(any("failed" in l or "raised" in l for l in lines),
                         lines)


class DarkChannelWarnTests(unittest.TestCase):
    """Both opt-in reads degrade to an UNREAD sentinel on a bare
    `except Exception`, and both sentinels DISABLE machinery downstream --
    silently. Combined with the capture-never-armed liveness gap that was a
    SILENT wall kill with nothing in the log naming the channel."""

    def _capture(self, fn):
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            fn()
        finally:
            mission_runner._stdout_sink = orig
        return lines

    def test_node_executor_read_fault_warns_once_and_fails_closed(self):
        class Boom:
            @property
            def node_executor(self):
                raise RuntimeError("no such attribute on this pin")

        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True,
                                                 read_node_executor=True)
        ctrl._mechjeb = Boom()
        reads = []
        lines = self._capture(
            lambda: [reads.append(ctrl._read_node_executor_enabled())
                     for _ in range(50)])
        warns = [l for l in lines if "NodeExecutor.Enabled UNREADABLE" in l]
        self.assertEqual(len(warns), 1, lines)
        self.assertIn("Warn", warns[0])
        # The -1 UNREAD sentinel is still the value: fail CLOSED, but LOUD once.
        self.assertEqual(set(reads), {-1})

    def test_landing_read_fault_warns_once_and_fails_closed(self):
        """The landing channel's -1 / "" sentinels stand the DESCENT supervisor
        down silently, so a drifted kRPC surface must name itself ONCE instead
        of producing a mute no-progress give-up 900 game seconds later."""
        class Boom:
            @property
            def landing_autopilot(self):
                raise RuntimeError("no such attribute on this pin")

        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True,
                                                 read_landing=True)
        ctrl._mechjeb = Boom()
        reads = []
        lines = self._capture(
            lambda: [reads.append(ctrl._read_landing_autopilot())
                     for _ in range(50)])
        warns = [l for l in lines
                 if "LandingAutopilot.Enabled UNREADABLE" in l]
        self.assertEqual(len(warns), 1, lines)
        self.assertIn("Warn", warns[0])
        self.assertEqual(set(reads), {(-1, "")})


class LandingEngageTests(unittest.TestCase):
    """ACTION_MJ_LAND_UNTARGETED: the settings are WRITTEN then READ BACK, the
    NodeExecutor autowarp (shared global MechJeb state that gates every landing
    state's own warp) is set EXPLICITLY, and the engage is followed by an
    OBSERVED enabled read-back rather than an assumption."""

    class _Landing:
        def __init__(self, enabled_after=True, sticky=(), boom=False):
            self.touchdown_speed = 0.0
            self.deploy_gears = False
            self.deploy_chutes = True     # MechJeb's own default: WRONG for us
            self.rcs_adjustment = True    # MechJeb's own default: WRONG for us
            self.status = "Doing deorbit burn."
            self._enabled_after = enabled_after
            self._sticky = set(sticky)
            self._boom = boom
            self.enabled = False
            self.landed_calls = 0
            self.stop_calls = 0

        def __setattr__(self, name, value):
            if name in getattr(self, "_sticky", ()):
                return          # silently ignored: the read-back is the point
            object.__setattr__(self, name, value)

        def land_untargeted(self):
            if self._boom:
                raise RuntimeError("module not available")
            object.__setattr__(self, "landed_calls", self.landed_calls + 1)
            object.__setattr__(self, "enabled", self._enabled_after)

        def stop_landing(self):
            object.__setattr__(self, "stop_calls", self.stop_calls + 1)
            object.__setattr__(self, "enabled", False)

    class _NodeExecutor:
        def __init__(self):
            self.autowarp = False

    class _MechJeb:
        def __init__(self, landing, node_executor):
            self.landing_autopilot = landing
            self.node_executor = node_executor

    class _Control:
        nodes = ()
        throttle = 0.0

    class _Vessel:
        def __init__(self):
            self.control = LandingEngageTests._Control()

    class _Sc:
        def __init__(self, vessel):
            self.active_vessel = vessel

    class _Conn:
        def __init__(self, sc):
            self.space_center = sc

    def _perform(self, landing, action):
        ne = self._NodeExecutor()
        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True,
                                                 read_landing=True)
        ctrl._mechjeb = self._MechJeb(landing, ne)
        ctrl._conn = self._Conn(self._Sc(self._Vessel()))
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            ctrl.perform(action)
        finally:
            mission_runner._stdout_sink = orig
        return ne, lines

    def _engage(self, cfg=(0.5, True, False, False), **kw):
        landing = self._Landing(**kw)
        ne, lines = self._perform(
            landing, mlib.Action(mlib.ACTION_MJ_LAND_UNTARGETED,
                                 landing_config=cfg))
        return landing, ne, lines

    def test_settings_are_written_and_the_module_engaged(self):
        landing, ne, lines = self._engage()
        self.assertEqual(landing.landed_calls, 1)
        self.assertEqual(landing.touchdown_speed, 0.5)
        self.assertTrue(landing.deploy_gears)
        # MechJeb's OWN default for both of these is True; the airless-body
        # contract requires them off, so the write must actually land.
        self.assertFalse(landing.deploy_chutes)
        self.assertFalse(landing.rcs_adjustment)
        self.assertTrue(any("OBSERVED enabled=1" in l for l in lines), lines)

    def test_node_executor_autowarp_is_set_explicitly(self):
        """MechJeb's landing states gate their OWN warp on Core.Node.Autowarp,
        which is SHARED GLOBAL state (the B-DOCK flight-12 lesson). The machine
        issues no warp during DESCENT, so this flag is the only thing between a
        warped descent and a 1:1 real-time one."""
        _, ne, _ = self._engage()
        self.assertTrue(ne.autowarp)

    def test_a_silently_ignored_chute_setting_is_warned_loudly(self):
        """Arming a chute for an AIRLESS body is a lie in the config, so a
        refused write must be LOUD -- even though it does not block the engage
        (the chute is inert on the Mun by physics, and the machine's OBSERVED
        gates still judge the outcome)."""
        landing, _, lines = self._engage(sticky=("deploy_chutes",))
        self.assertTrue(landing.deploy_chutes)      # the write did not take
        self.assertEqual(landing.landed_calls, 1)   # but the descent still ran
        warns = [l for l in lines
                 if "DeployChutes READ BACK" in l and "Warn" in l]
        self.assertEqual(len(warns), 1, lines)

    def test_an_engage_that_does_not_arm_is_reported_as_a_warn(self):
        """The runner never claims success it did not observe: land_untargeted
        returning is not evidence. The machine's DESCENT supervisor re-reads the
        channel every poll and owns the fast-fail; this line is the record."""
        _, _, lines = self._engage(enabled_after=False)
        engaged = [l for l in lines if "OBSERVED enabled=0" in l]
        self.assertEqual(len(engaged), 1, lines)
        self.assertIn("Warn", engaged[0])

    def test_a_throwing_engage_is_swallowed_and_named(self):
        landing, _, lines = self._engage(boom=True)
        self.assertEqual(landing.landed_calls, 0)
        self.assertTrue(any("land_untargeted() failed" in l for l in lines),
                        lines)

    def test_a_missing_config_falls_back_conservatively_and_says_so(self):
        landing, _, lines = self._engage(cfg=None)
        self.assertFalse(landing.deploy_chutes)
        self.assertFalse(landing.rcs_adjustment)
        self.assertTrue(landing.deploy_gears)
        self.assertTrue(any("carried no landing_config" in l for l in lines),
                        lines)

    def test_stop_landing_releases_the_module(self):
        landing = self._Landing()
        landing.enabled = True
        _, lines = self._perform(landing,
                                 mlib.Action(mlib.ACTION_MJ_STOP_LANDING))
        self.assertEqual(landing.stop_calls, 1)
        self.assertFalse(landing.enabled)
        self.assertTrue(any("landing autopilot released" in l for l in lines),
                        lines)


class _LandingDescentFlightFixture:
    """Shared fly-loop fixtures for the two DESCENT give-up ladders below.

    A MIXIN, not a base test case, deliberately: making one ladder class
    inherit the other would re-run every one of its cells under a second name
    and inflate the suite count with duplicates that measure nothing new."""

    # Both ladders are entered ALREADY ENGAGED: the PARK -> DESCENT entry engage
    # is covered end to end by B13LandingShellTests, and starting past it makes
    # every ACTION_MJ_LAND_UNTARGETED the loop performs a RE-ISSUE, so the bound
    # is asserted against a count that means only one thing. The no-progress
    # window is anchored at 100,000 m / ut 0 by the same call.
    def _descent_state(self, params=None):
        p = mlib.b5_params_from_dict(dict(params or B13_PARAMS))
        return replace(mlib.b5_initial_state(p),
                       phase=mlib.B5_DESCENT, phase_entry_ut=0.0,
                       landing_engaged=True,
                       landing_alt_ref=100_000.0, landing_alt_ref_ut=0.0)

    def _fly(self, frames, params=None, max_last_repeats=4):
        clock = _StepClock()
        lines = []
        log = mission_runner.MissionLogger(sink=lines.append, clock=clock)
        control = FakeMissionControl(frames, max_last_repeats=max_last_repeats)
        final, _ = mission_runner.fly_loop(
            control, self._descent_state(params), mlib.b5_decide, log,
            deadline=1e12, clock=clock, sleep=lambda _s: None,
            poll_interval=0.0, settle_frames=0)
        return final, control, lines

    @staticmethod
    def _descending(i, **kw):
        """One DESCENT frame, 10 game-seconds apart and genuinely falling. The
        spacing is deliberate: the whole ladder must resolve INSIDE one
        no-progress window (900 s) and inside the descent budget, so the
        give-up under test is the only one that can fire."""
        base = dict(ut=10.0 * i, body="Mun", situation="SUB_ORBITAL",
                    altitude=100_000.0 - 1000.0 * i, vertical_speed=-100.0,
                    horizontal_speed=200.0, landing_ap_enabled=1,
                    landing_ap_status="Doing deorbit burn.")
        base.update(kw)
        return snap(**base)

    @staticmethod
    def _gates(lines, key):
        """The ordered `old->new` transitions the loop logged for one gate
        field, with the surrounding telemetry stripped."""
        out = []
        for line in lines:
            marker = "gate %s " % key
            if marker in line:
                out.append(line.split(marker, 1)[1].split(" |", 1)[0])
        return out

    def _landing_tail(self, first_index):
        """A healthy touchdown -> settled dwell -> surface commit tail, so a
        NEGATIVE cell reaches a real terminal instead of running the frame list
        dry (which the fake reports as a transport drop)."""
        touchdown_ut = 10.0 * first_index
        frames = [snap(ut=touchdown_ut, body="Mun", situation="LANDED",
                       altitude=3.0, vertical_speed=-0.3,
                       horizontal_speed=0.05, landing_ap_enabled=0)]
        frames += [snap(ut=touchdown_ut + float(i), body="Mun",
                        situation="LANDED", altitude=3.0, vertical_speed=0.0,
                        horizontal_speed=0.01, landing_ap_enabled=0)
                   for i in range(1, 4)]                       # debounce 3
        frames.append(snap(ut=touchdown_ut + 130.0, body="Mun",
                           situation="LANDED", altitude=2.99,
                           vertical_speed=0.0, horizontal_speed=0.01,
                           landing_ap_enabled=0))              # dwell elapsed
        frames.append(snap(ut=touchdown_ut + 140.0, body="Mun",
                           situation="LANDED", altitude=2.98,
                           vertical_speed=0.0, horizontal_speed=0.01,
                           landing_ap_enabled=0,
                           seam_commit_result="OK"))
        return frames

class LandingAutopilotLadderFlightTests(_LandingDescentFlightFixture,
                                       unittest.TestCase):
    """The DESCENT autopilot supervision ladder, driven through the REAL fly
    loop: debounce -> bounded re-issue -> distinctly named DEAD fast-fail.

    WHY THIS CLASS EXISTS. B13 and B14 both PASSED on their first flight with
    `LandingAutopilot.Enabled` reading 1 on every DESCENT frame THE SUPERVISOR
    EVALUATED -- not on every polled frame, which is a claim the archived
    telemetry contradicts: `landAP=0` appears on B13's PARK -> DESCENT entry
    frame (ut 21,734.345, decided in PARK, before the engage went out) and on
    the TOUCHDOWN frame of BOTH flights (B13 ut 23,088.285, B14 ut 278,581.702),
    which is MechJeb disabling its own module on the landed frame and is exactly
    what the touchdown-before-supervisor ordering exists for. So NEITHER flight
    emitted a single `landingApDownStreak` or
    `landingApReissues` line -- the ladder has never executed against real
    MechJeb, and the machine-diff CHANNEL it reports through has never been
    seen carrying a non-zero value on any surface. These cells do not create a
    live proof and must not be read as one. What they do is bound what a live
    firing could still surprise us with to MechJeb's own behaviour, by pinning
    everything on OUR side of the seam end to end: the debounce DEPTH (the
    existing all-disabled cells cannot tell a 3-frame debounce from a 1-frame
    one), the re-issue ACTION reaching the seam with the spec's vehicle
    configuration, the bound on how many times it can, the give-up NAME, and
    the gate lines an operator would grep for when it fires for real.

    Driven through ``mission_runner.fly_loop`` rather than ``b5_decide``
    directly because the gate lines are emitted by the LOOP (``diff_machine_state``
    -> ``log.info``), not by the machine: an mlib-only cell proves the state
    field moved and says nothing about whether anybody would ever see it.
    """

    # The exact frame count the ladder needs: (debounce) frames per rung, one
    # rung per re-issue plus the final DEAD rung. Derived from the constants
    # rather than hardcoded, so re-tuning either one re-derives the script
    # instead of silently making the cell assert a shorter ladder.
    @property
    def _ladder_frames(self):
        return (mlib.LANDING_AP_DISABLED_DEBOUNCE_FRAMES
                * (mlib.MAX_LANDING_AP_REISSUES + 1))

    def test_the_full_ladder_runs_debounce_reissue_then_names_the_giveup(self):
        """THE headline cell. A module that reads DOWN every poll must climb
        the debounce, be re-handed the descent a BOUNDED number of times, and
        then fast-fail under its own name -- not idle out the 3000 game-second
        descent budget under a generic phase-timeout."""
        frames = [self._descending(i, landing_ap_enabled=0, landing_ap_status="")
                  for i in range(1, self._ladder_frames + 1)]
        final, control, lines = self._fly(frames)
        self.assertTrue(final.done)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.LANDING_GIVEUP_AP_NOT_ENABLED, final.flake_reason)
        self.assertIn("COMMANDED and never OBSERVED", final.flake_reason)
        # NOT the touchdown-timeout / no-progress names: a dead actor must be
        # named as a dead actor, which is the whole point of four give-ups.
        self.assertNotIn(mlib.LANDING_GIVEUP_TOUCHDOWN_TIMEOUT,
                         final.flake_reason)
        self.assertNotIn(mlib.LANDING_GIVEUP_NO_PROGRESS, final.flake_reason)
        # It fast-failed on the ladder's OWN frame, seconds in -- it did not
        # ride the phase budget out (the failure this ladder exists to avoid).
        self.assertEqual(control.reads, self._ladder_frames)
        self.assertLess(final.phase_entry_ut + 10.0 * self._ladder_frames,
                        B13_PARAMS["descentTimeoutSeconds"])
        # ... and the machine never left DESCENT for the landed tail.
        self.assertEqual(final.phase, mlib.B5_DESCENT)
        self.assertNotIn(mlib.B5_LANDED_SETTLE, final.phases_reached)

    def test_the_reissue_is_bounded_and_carries_the_vehicle_configuration(self):
        """BOUNDED: a server-side surface that refuses to arm must END the
        phase, not loop on it. And each re-issue must re-send the SPEC's
        landing configuration -- a re-issue that silently dropped
        DeployGears would land the craft on its engine bell."""
        frames = [self._descending(i, landing_ap_enabled=0)
                  for i in range(1, self._ladder_frames + 1)]
        final, control, _ = self._fly(frames)
        engages = [a for a in control.actions
                   if a.kind == mlib.ACTION_MJ_LAND_UNTARGETED]
        self.assertEqual(len(engages), mlib.MAX_LANDING_AP_REISSUES)
        self.assertEqual(final.landing_ap_reissues, mlib.MAX_LANDING_AP_REISSUES)
        for engage in engages:
            self.assertEqual(engage.landing_config, (0.5, True, False, False))
        # DESCENT stays warp-PASSIVE and attitude-PASSIVE even while recovering:
        # MechJeb owns both, and a second writer is the thrash class this suite
        # has already paid for twice.
        self.assertEqual({a.kind for a in control.actions},
                         {mlib.ACTION_MJ_LAND_UNTARGETED})

    def test_the_ladder_reaches_the_log_as_greppable_gate_lines(self):
        """THE CHANNEL THE TWO FLIGHTS NEVER LIT. `landingApDownStreak` and
        `landingApReissues` are the two fields an operator greps when a descent
        does not engage, and no flight has ever emitted either carrying a
        non-zero value. This asserts the loop emits the WHOLE ladder: one
        transition per debounce step, the reset on each re-issue, and the
        capped final rung."""
        frames = [self._descending(i, landing_ap_enabled=0)
                  for i in range(1, self._ladder_frames + 1)]
        _final, _control, lines = self._fly(frames)
        depth = mlib.LANDING_AP_DISABLED_DEBOUNCE_FRAMES
        expected = []
        for rung in range(mlib.MAX_LANDING_AP_REISSUES):
            expected += ["%d->%d" % (n, n + 1) for n in range(depth - 1)]
            expected.append("%d->0" % (depth - 1))      # consumed by the re-issue
        expected += ["%d->%d" % (n, n + 1) for n in range(depth)]  # DEAD, capped
        self.assertEqual(self._gates(lines, "landingApDownStreak"), expected)
        self.assertEqual(
            self._gates(lines, "landingApReissues"),
            ["%d->%d" % (n, n + 1)
             for n in range(mlib.MAX_LANDING_AP_REISSUES)])

    def test_a_flickering_channel_never_reaches_a_reissue(self):
        """THE DEBOUNCE, measured. Every existing cell scripts the module
        disabled on EVERY frame, which cannot tell a 3-frame debounce from a
        1-frame one. Here the reading flickers -- two down, one up, forever --
        so a single-frame transient (a poll landing between MechJeb's own state
        transitions) must never re-hand it the descent."""
        frames = []
        for i in range(1, 40):
            frames.append(self._descending(
                i, landing_ap_enabled=(1 if i % 3 == 0 else 0)))
        frames += self._landing_tail(40)
        final, control, lines = self._fly(frames)
        # The flight LANDED: the flicker cost it nothing at all.
        self.assertTrue(final.done)
        self.assertIsNone(final.verdict, final.flake_reason)
        self.assertEqual(final.phase, mlib.B5_SURFACE_COMMITTED)
        self.assertEqual(final.landing_ap_reissues, 0)
        self.assertEqual(
            [a.kind for a in control.actions].count(
                mlib.ACTION_MJ_LAND_UNTARGETED), 0)
        # The streak was OBSERVED climbing and resetting, so the cell proves the
        # debounce absorbed the flicker rather than never seeing it.
        streaks = self._gates(lines, "landingApDownStreak")
        self.assertIn("0->1", streaks)
        self.assertIn("2->0", streaks)
        self.assertNotIn("2->3", streaks)
        self.assertEqual(self._gates(lines, "landingApReissues"), [])

    def test_a_healthy_landing_never_enters_the_supervision_path(self):
        """THE SHAPE BOTH LIVE FLIGHTS FLEW. An always-enabled descent must
        leave the ladder's two channels completely silent -- which is why the
        two green flights emitting nothing is evidence of a healthy descent and
        NOT evidence of a dark channel. The other three cells above are what
        turn that reading from an assumption into a measured one."""
        frames = [self._descending(i) for i in range(1, 12)]
        frames += self._landing_tail(12)
        final, control, lines = self._fly(frames)
        self.assertTrue(final.done)
        self.assertIsNone(final.verdict, final.flake_reason)
        self.assertEqual(final.phase, mlib.B5_SURFACE_COMMITTED)
        self.assertEqual(final.landing_ap_down_streak, 0)
        self.assertEqual(final.landing_ap_reissues, 0)
        self.assertEqual(self._gates(lines, "landingApDownStreak"), [])
        self.assertEqual(self._gates(lines, "landingApReissues"), [])
        self.assertEqual(
            [a.kind for a in control.actions].count(
                mlib.ACTION_MJ_LAND_UNTARGETED), 0)

    def test_an_unread_channel_is_not_evidence_of_a_dead_module(self):
        """FAIL CLOSED against acting on evidence we do not have: the -1 UNREAD
        sentinel (a run that forgot read_landing, or a channel that dropped)
        must never climb the ladder. The altitude-trend watchdog and the descent
        budget own that outcome instead."""
        frames = [self._descending(i, landing_ap_enabled=-1)
                  for i in range(1, 30)]
        frames += self._landing_tail(30)
        final, control, lines = self._fly(frames)
        self.assertIsNone(final.verdict, final.flake_reason)
        self.assertEqual(final.landing_ap_reissues, 0)
        self.assertEqual(self._gates(lines, "landingApDownStreak"), [])
        self.assertEqual(
            [a.kind for a in control.actions].count(
                mlib.ACTION_MJ_LAND_UNTARGETED), 0)


class LandingNoProgressDebounceFlightTests(_LandingDescentFlightFixture,
                                          unittest.TestCase):
    """The SECOND DESCENT give-up's debounce, driven through the REAL fly loop
    (review round 2, 2026-07-26).

    `landing-no-progress` used to fire on the FIRST frame past the window, and
    the countdown it now runs is only useful if an operator can SEE it: the
    depth lives in mlib, but the `gate landingStallStreak` lines are emitted by
    the LOOP (`diff_machine_state` -> `log.info`). An mlib-only cell proves the
    field moved and says nothing about whether anybody would ever read it --
    the same reason the autopilot ladder above is flown rather than decided.

    Shares the ladder's fixture mixin deliberately: same `_descent_state`
    (anchored at 100,000 m / ut 0), same `_fly`, same `_gates`, same 10
    game-second frame spacing."""

    def _stalled(self, i, **kw):
        """A frame PAST the no-progress window that proves nothing moved: the
        window opened at ut 0 / 100,000 m, so ut > 900 with a flat altitude is
        an elapsed window that under-delivered, from an anchor high enough that
        the band disarm does not apply."""
        base = dict(ut=B13_PARAMS["landingProgressWindowSeconds"] + 10.0 * i,
                    altitude=99_950.0 + 0.01 * i, vertical_speed=0.0)
        base.update(kw)
        return self._descending(i, **base)

    def test_the_countdown_reaches_the_log_then_names_the_giveup(self):
        depth = mlib.LANDING_STALL_DEBOUNCE_FRAMES
        frames = [self._stalled(i) for i in range(1, depth + 1)]
        final, control, lines = self._fly(frames)
        self.assertTrue(final.done)
        self.assertEqual(final.verdict, mlib.MISSION_FLAKE)
        self.assertIn(mlib.LANDING_GIVEUP_NO_PROGRESS, final.flake_reason)
        # One gate line per step of the countdown, so the give-up is visible
        # coming rather than only on arrival.
        self.assertEqual(self._gates(lines, "landingStallStreak"),
                         ["%d->%d" % (n, n + 1) for n in range(depth)])
        # It fired on the ladder's own frame, not on the descent budget.
        self.assertEqual(control.reads, depth)
        self.assertNotIn(mlib.LANDING_GIVEUP_TOUCHDOWN_TIMEOUT,
                         final.flake_reason)

    def test_a_hop_short_of_the_depth_costs_the_landing_nothing(self):
        """THE MEASURED CASE: B14 flight 1's Minmus final descent produced FIVE
        consecutive non-descending frames on a HEALTHY landing. A run that stops
        short of the depth must leave no trace but the reset, and the flight
        must still land and commit."""
        depth = mlib.LANDING_STALL_DEBOUNCE_FRAMES
        frames = [self._stalled(i) for i in range(1, depth)]
        # ... then one frame that delivers the drop, and a normal tail.
        frames.append(self._stalled(depth, altitude=99_000.0,
                                    vertical_speed=-50.0))
        frames += self._landing_tail(200)
        final, _control, lines = self._fly(frames)
        self.assertTrue(final.done)
        self.assertIsNone(final.verdict, final.flake_reason)
        self.assertEqual(final.phase, mlib.B5_SURFACE_COMMITTED)
        streaks = self._gates(lines, "landingStallStreak")
        self.assertIn("0->1", streaks)
        self.assertIn("%d->0" % (depth - 1), streaks)
        self.assertNotIn("%d->%d" % (depth - 1, depth), streaks)

    def test_a_healthy_descent_leaves_the_countdown_silent(self):
        """The shape both live flights flew: every window delivered its drop, so
        the channel emits nothing at all. That silence is only evidence of a
        healthy descent because the two cells above prove the channel speaks."""
        frames = [self._descending(i) for i in range(1, 12)]
        frames += self._landing_tail(12)
        final, _control, lines = self._fly(frames)
        self.assertIsNone(final.verdict, final.flake_reason)
        self.assertEqual(final.landing_stall_streak, 0)
        self.assertEqual(self._gates(lines, "landingStallStreak"), [])


if __name__ == "__main__":
    unittest.main()
