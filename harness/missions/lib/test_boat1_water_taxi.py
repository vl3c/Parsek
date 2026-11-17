"""Unit cells for mission `boat1_water_taxi` (BOAT-1: the Mallard's surface run on water).

  1. THE GOVERNOR and THE HANDOFF GATE, each refusal by name.
  2. THE WALK: handoff -> taxi -> stop -> done, the actions it emits and when.
  3. THE NAMED ENDS: a wrong handoff, leaving the water, an overspeed, a lost vessel,
     the two timeouts.
  4. THE ROWS fail closed.
  5. THE SHELL: a fake run through the runner ends MISSION-OK; the control reads the
     two channels the machine needs.
  6. THE SPEC: BOAT-1's params validate against the schema and build a consistent set.

NO krpc, NO KSP, NO network.
"""

import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib                        # noqa: E402
import boat1_water_taxi            # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

BOAT1_SPEC = "BOAT-1-mallard-water-taxi"
LAT0, LON0 = 0.054549681973474297, -73.697009592794771
# One degree of longitude at the equator on Kerbin, in metres.
M_PER_DEG = 2.0 * 3.141592653589793 * mlib.KERBIN_RADIUS_M / 360.0

PARAMS = {
    "expectedVesselName": "Mallard",
    "handoffDebounceFrames": 3,
    "handoffMaxFrames": 10,
    "targetDistanceMeters": 300.0,
    "cruiseSpeedMps": 12.0,
    "maxSpeedMps": 30.0,
    "throttleBase": 0.3,
    "throttleGainPerMps": 0.05,
    "offWaterFrames": 3,
    "taxiTimeoutSeconds": 180.0,
    "restSpeedMps": 1.0,
    "restDebounceFrames": 3,
    "stopTimeoutSeconds": 120.0,
}


def _f(ut, east_m=0.0, speed=0.0, situation="SPLASHED", name="Mallard", **kw):
    return snap(ut=ut, vessel_name=name, situation=situation,
                latitude=LAT0, longitude=LON0 + east_m / M_PER_DEG,
                surface_speed=speed, heading=90.0, **kw)


def _state(params=None):
    return mlib.boat_initial_state(mlib.boat_params_from_dict(params or PARAMS))


def _walk(frames, params=None):
    st, out = _state(params), []
    for frame in frames:
        st, actions = mlib.boat_decide(st, frame)
        out.append(actions)
    return st, out


def _taxiing():
    st, _ = _walk([_f(1.0), _f(2.0), _f(3.0)])
    assert st.phase == mlib.BOAT_TAXI
    return st


class GovernorAndGateTests(unittest.TestCase):
    def setUp(self):
        self.p = mlib.boat_params_from_dict(PARAMS)

    def test_the_governor_is_proportional_and_clamped(self):
        self.assertAlmostEqual(0.9, mlib.boat_throttle_command(self.p, 0.0))
        self.assertAlmostEqual(0.3, mlib.boat_throttle_command(self.p, 12.0))
        self.assertAlmostEqual(0.0, mlib.boat_throttle_command(self.p, 40.0))
        self.assertAlmostEqual(1.0, mlib.boat_throttle_command(
            mlib.boat_params_from_dict(dict(PARAMS, throttleBase=0.8)), 0.0))
        self.assertAlmostEqual(0.3, mlib.boat_throttle_command(self.p, float("nan")))

    def test_each_handoff_refusal_names_what_it_read(self):
        cases = (
            (_f(1.0, name="Kerbal X"), "vessel=Kerbal X, not Mallard"),
            (_f(1.0, name=""), "vessel=<unread>, not Mallard"),
            (_f(1.0, situation="LANDED"), "situation=LANDED, not SPLASHED"),
            (snap(ut=1.0, vessel_name="Mallard", situation="SPLASHED"),
             "position / speed channel unread"),
        )
        for frame, reason in cases:
            self.assertEqual(reason, mlib.boat_handoff_refusal(self.p, frame))
        self.assertEqual("", mlib.boat_handoff_refusal(self.p, _f(1.0)))

    def test_inconsistent_params_are_refused(self):
        for extra in ({"cruiseSpeedMps": 30.0}, {"restSpeedMps": 12.0},
                      {"targetDistanceMeters": 0.0}, {"throttleBase": 0.9, "throttleMax": 0.5},
                      {"expectedVesselName": ""}):
            with self.assertRaises(ValueError, msg=extra):
                mlib.boat_params_from_dict(dict(PARAMS, **extra))


class WalkTests(unittest.TestCase):
    def test_handoff_stamps_the_start_and_starts_the_run(self):
        st, acts = _walk([_f(1.0), _f(2.0), _f(3.0)])
        self.assertEqual([[], []], acts[:2])
        self.assertEqual(mlib.BOAT_TAXI, st.phase)
        self.assertEqual((LAT0, LON0, "SPLASHED", 3.0),
                         (st.start_lat, st.start_lon, st.start_situation, st.start_ut))
        self.assertEqual([Action(mlib.ACTION_SET_SAS), Action(mlib.ACTION_SET_BRAKES, 0.0)],
                         acts[2][:2])
        self.assertEqual(mlib.ACTION_SET_THROTTLE, acts[2][2].kind)
        self.assertAlmostEqual(0.9, acts[2][2].value)

    def test_the_throttle_is_resent_only_past_the_dead_band(self):
        st = _taxiing()
        st, a1 = mlib.boat_decide(st, _f(4.0, east_m=2.0, speed=0.5))   # 0.875: inside
        self.assertEqual([], a1)
        st, a2 = mlib.boat_decide(st, _f(5.0, east_m=10.0, speed=8.0))  # 0.5: resent
        self.assertEqual([mlib.ACTION_SET_THROTTLE], [a.kind for a in a2])
        self.assertAlmostEqual(0.5, a2[0].value)

    def test_reaching_the_distance_stops_then_rest_ends_the_run(self):
        st = _taxiing()
        st, acts = mlib.boat_decide(st, _f(30.0, east_m=305.0, speed=12.0))
        self.assertEqual(mlib.BOAT_STOP, st.phase)
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                          Action(mlib.ACTION_SET_BRAKES, 1.0)], acts)
        self.assertGreaterEqual(st.reach_distance, 300.0)
        for i, speed in enumerate((3.0, 0.8, 0.6)):
            st, _ = mlib.boat_decide(st, _f(31.0 + i, east_m=310.0, speed=speed))
            self.assertFalse(st.done)
        st, _ = mlib.boat_decide(st, _f(34.0, east_m=311.0, speed=0.4))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.BOAT_DONE, st.phase)
        self.assertEqual(st, mlib.boat_decide(st, _f(35.0))[0])

    def test_a_brief_bounce_is_tolerated(self):
        st = _taxiing()
        for i in range(3):
            st, _ = mlib.boat_decide(st, _f(4.0 + i, east_m=20.0 * i, speed=14.0,
                                            situation="FLYING"))
        self.assertFalse(st.done)
        st, _ = mlib.boat_decide(st, _f(8.0, east_m=80.0, speed=12.0))
        self.assertEqual(0, st.off_water_streak)
        self.assertEqual(3, st.off_water_max_streak)
        self.assertEqual(("FLYING",), st.off_water_situations)


class NamedEndTests(unittest.TestCase):
    def test_a_refused_handoff_fails_naming_the_refusal(self):
        st, acts = _walk([_f(float(i), situation="LANDED") for i in range(10)])
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("handoff refused on 10 frames: situation=LANDED", st.loss_reason)
        self.assertTrue(all(a == [] for a in acts))

    def test_leaving_the_water_cuts_the_throttle_and_fails(self):
        st = _taxiing()
        for i in range(3):
            st, acts = mlib.boat_decide(st, _f(4.0 + i, situation="LANDED"))
        self.assertFalse(st.done)
        st, acts = mlib.boat_decide(st, _f(8.0, situation="LANDED"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("left the water: situation LANDED on 4", st.loss_reason)
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0)], acts)

    def test_an_overspeed_cuts_the_throttle_and_fails(self):
        st, acts = mlib.boat_decide(_taxiing(), _f(9.0, east_m=100.0, speed=31.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("overspeed", st.loss_reason)
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0)], acts)

    def test_a_lost_vessel_fails(self):
        st, _ = mlib.boat_decide(_taxiing(), snap(ut=9.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("vessel-lost", st.loss_reason)

    def test_the_two_timeouts_flake_naming_the_phase(self):
        st, acts = mlib.boat_decide(_taxiing(), _f(184.0, east_m=50.0, speed=1.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.BOAT_TAXI, st.flake_phase)
        self.assertIn("50 m of 300 m", st.flake_reason)
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0)], acts)
        st, _ = mlib.boat_decide(_taxiing(), _f(10.0, east_m=301.0, speed=12.0))
        st, _ = mlib.boat_decide(st, _f(131.0, east_m=400.0, speed=2.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.BOAT_STOP, st.flake_phase)


class RowTests(unittest.TestCase):
    def test_no_state_fails_every_row(self):
        rows = mlib.evaluate_boat_assertions([], mlib.boat_params_from_dict(PARAMS), None)
        self.assertEqual(["handoffAfloat", "travelledOnWater", "stayedOnWater",
                          "cameToRest"], [r.name for r in rows])
        self.assertFalse(any(r.met for r in rows))

    def test_a_run_that_never_reached_the_distance_fails_travelled(self):
        st, _ = mlib.boat_decide(_taxiing(), _f(184.0, east_m=50.0, speed=1.0))
        rows = {r.name: r for r in mlib.evaluate_boat_assertions(
            [], mlib.boat_params_from_dict(PARAMS), st)}
        self.assertTrue(rows["handoffAfloat"].met)
        self.assertFalse(rows["travelledOnWater"].met)
        self.assertFalse(rows["cameToRest"].met)


class ShellTests(unittest.TestCase):
    def _frames(self):
        frames = [_f(10.0), _f(10.5), _f(11.0)]
        ut, east, speed = 11.0, 0.0, 0.0
        while east < 300.0:
            ut += 1.0
            speed = min(12.0, speed + 2.0)
            east += speed
            frames.append(_f(ut, east_m=east, speed=speed))
        for slow in (6.0, 2.0, 0.7, 0.5, 0.3):
            ut += 1.0
            east += slow
            frames.append(_f(ut, east_m=east, speed=slow))
        return frames

    def test_a_water_run_is_mission_ok(self):
        control = FakeMissionControl(self._frames())
        code, result = run(boat1_water_taxi.SPEC, PARAMS, control)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])
        self.assertEqual(mlib.BOAT_DONE, result["phasesReached"][-1])
        kinds = [a.kind for a in control.actions]
        self.assertEqual(mlib.ACTION_SET_SAS, kinds[0])
        self.assertIn(mlib.ACTION_CUT_THROTTLE, kinds)
        self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, kinds)

    def test_the_control_reads_name_and_position(self):
        control = boat1_water_taxi.make_control()
        self.assertTrue(control._read_aircraft)
        self.assertTrue(control._read_vessel_name)
        self.assertFalse(control._use_mechjeb)
        self.assertFalse(boat1_water_taxi.SPEC.allow_rails_warp)


class SpecTests(unittest.TestCase):
    def test_boat1_params_validate_and_build(self):
        import hlib
        with open(os.path.join(_HARNESS, "scenarios", BOAT1_SPEC + ".toml"), "rb") as fh:
            spec = tomllib.load(fh)
        with open(os.path.join(_MISSIONS, "boat1_water_taxi.schema.toml"), "rb") as fh:
            schema = tomllib.load(fh)
        self.assertEqual("boat1_water_taxi", spec["driver"]["mission"])
        self.assertEqual("fixtures/saves/mallard-splashed", spec["fixture"]["saveTemplate"])
        params = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(params, schema))
        p = mlib.boat_params_from_dict(params)
        self.assertEqual("Mallard", p.expected_vessel_name)
        # Every declared key is read by the builder.
        import inspect
        src = inspect.getsource(mlib.boat_params_from_dict)
        for key in schema["params"]:
            self.assertIn('"%s"' % key, src, key)


if __name__ == "__main__":
    unittest.main()
