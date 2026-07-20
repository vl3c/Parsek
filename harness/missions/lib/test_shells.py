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

import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)

import mlib                    # noqa: E402  (missions/lib is the discovery root)
import mission_runner         # noqa: E402
import b1_pad_hop             # noqa: E402
import b2_lko_ascent          # noqa: E402


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
    def test_raise_mid_flight_writes_error_with_traceback(self):
        """The fake raises a non-kRPC RuntimeError on the 2nd telemetry read; the
        shell catches it, classifies MISSION-ERROR (edge 9 internal bug), writes a
        traceback string, closes in the finally, exits nonzero. Guards an exception
        leaking as a hang or as no result file, and guards an internal bug being
        mis-filed as a flake."""
        frames = [snap(ut=0.0, stage_solid_fuel=1.0, situation="PRE_LAUNCH"), snap()]
        control = FakeMissionControl(frames, raise_on_read_index=1)
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


class _FakeOrbit:
    apoapsis_altitude = 5000.0
    periapsis_altitude = 1000.0
    eccentricity = 0.1
    inclination = 0.0
    body = _FakeBody()


class _FakeSituation:
    name = "flying"


class _FakeResources:
    def amount(self, _name):
        return 1.0


class _FakeVessel:
    situation = _FakeSituation()
    orbit = _FakeOrbit()
    resources = _FakeResources()

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
