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
    "chuteDeployAltMeters": 2500,
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
                 raise_exc=None, client_version="0.5.4", server_version="0.5.4"):
        self._snaps = list(snapshots)
        self._i = 0
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
            snap(ut=5.0, altitude=5000, apoapsis=14000, situation="FLYING"),
            snap(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING"),        # deploy chute
            snap(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED"),         # -> LANDED
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
            snap(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING"),
            snap(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED"),
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
                 vertical_speed=-50.0),                                   # chute deploys
            snap(ut=40.0, situation="LANDED", altitude=100.0, apoapsis=5000.0),
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
        # The shells imported at module load above; krpc must not be present.
        self.assertNotIn("krpc", sys.modules)


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


class _FakeVessel:
    situation = _FakeSituation()
    orbit = _FakeOrbit()
    resources = _FakeResources()
    control = _FakeNodeControl()

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


# The scripted B1 flight up to a chute-deployed DESCENT; a vessel_lost frame
# appended to it produces the DOWN terminal, a LANDED frame the classic landing.
_B1_DESCENT_WITH_CHUTE_FRAMES = [
    snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
    snap(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),   # -> COAST
    snap(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),    # -> DESCENT
    snap(ut=6.0, altitude=2000.0, vertical_speed=-30.0, apoapsis=14000,
         situation="FLYING"),                                                 # chute deploys
    # Below downMaxAltMeters (500): the DOWN eligibility gate's "reached the
    # ground" leg (SF-1) needs the last finite altitude near the surface.
    snap(ut=7.5, altitude=60.0, vertical_speed=-9.0, apoapsis=14000,
         situation="FLYING"),
]


class DownTerminalShellTests(unittest.TestCase):
    def test_b1_down_terminal_is_mission_ok_and_skips_settle_tail(self):
        """The live B1 shape: the Jumping Flea breaks apart at touchdown (~9 m/s
        vs the booster's 7 m/s tolerance) and the runner emits a vessel_lost
        snapshot -- with the chute deployed that is the DOWN SUCCESS terminal:
        MISSION-OK, exit 0, landedSituation met naming the DOWN end, and NO
        settle-tail reads (the vessel is gone). Guards the old behavior (every
        live B1 run ASSERT-FAILed at touchdown) from coming back."""
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
        self.assertEqual(sit["value"], "DOWN(chute-deployed impact)")
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
        """B5 flies ascent -> transfer -> flyby -> free-return; the settle tail
        runs on the RETURN terminal and all four assertions are met ->
        MISSION-OK. Guards the shell mis-wiring the new target/plan/execute
        actions or terminating at ORBIT like B2."""
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
        # Settle tail RAN (RETURN keeps it): more reads than scripted frames.
        self.assertGreater(control.reads, len(self._happy_frames()))
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
