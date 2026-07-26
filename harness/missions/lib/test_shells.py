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
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
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
import forge_station          # noqa: E402
import forge_lko              # noqa: E402
import bdock_dock_transfer    # noqa: E402
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
    "descentTimeoutSeconds": 240,
}

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
    "courseCorrectPeriapsisMeters": 60000,
    "planTimeoutSeconds": 300,
    "planRetrySeconds": 30,
    "transferBurnTimeoutSeconds": 4000,
    "coastTimeoutSeconds": 400000,
    "flybyTimeoutSeconds": 300000,
    "coastWarpFactor": 6,
    "flybyWarpFactor": 5,
    "targetPeriapsisFloorMeters": 10000,
}

# B7 spec-shaped params (mirrors harness/scenarios/B7-duna-flyby.toml): the
# shared B5 machine with the five interplanetary keys ON. The shell round-trip
# proves b5_params_from_dict parses viaBodyNames / returnBodyName /
# interplanetaryTransfer / ejectionEccFloor / correctionTriggerTimeToSoiSeconds.
B7_PARAMS = {
    "targetApoapsisMeters": 700000,
    "targetPeriapsisMeters": 700000,
    "apoErrorMeters": 15000,
    "periErrorMeters": 15000,
    "ascentTimeoutSeconds": 1200,
    "circularizeTimeoutSeconds": 600,
    "targetBodyName": "Duna",
    "homeBodyName": "Kerbin",
    "transferMinApoapsisMeters": 0,
    "ejectionEccFloor": 1.05,
    "interplanetaryTransfer": True,
    "viaBodyNames": ["Sun"],
    "returnBodyName": "Sun",
    "courseCorrectPeriapsisMeters": 50000,
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
    "warpAboveAltMeters": 45000,
    "warpHopSeconds": 120,
    "chuteDeployAltMeters": 3000,
    "deorbitTimeoutSeconds": 300,
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


if __name__ == "__main__":
    unittest.main()
