"""Unit cells for mission `psub_dive` (the SUB lanes: the stock submarine PSUB-1 dives).

  1. THE HANDOFF GATE and THE HELPERS, each refusal by name.
  2. THE WALK: handoff -> settle -> measure -> drop -> ... -> sink -> hold -> done, and
     the rise: the floats left aboard first, then one ballast pair per measurement.
  3. THE SELF-CALIBRATION: a sub that sinks as delivered drops nothing; one that needs
     two pairs drops two; the two named "k" failures.
  4. THE NAMED ENDS: the crush guard, leaving the water, a click that drops nothing, a
     lost vessel, the timeouts.
  5. THE READINGS: every measurement and every periodic reading is logged.
  6. THE ROWS fail closed; THE SHELL runs a fake dive MISSION-OK and reads the depth
     channel.

NO krpc, NO KSP, NO network.
"""

import inspect
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
import psub_dive                   # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402

FLOOR = -710.0
PARTS = 28
STAGE = 6

PARAMS = {
    "expectedVesselName": "PSUB-1",
    "handoffDebounceFrames": 3,
    "handoffMaxFrames": 10,
    "floatStages": 3,
    "ballastStages": 3,
    "settleSeconds": 2.0,
    "measureSeconds": 2.0,
    "sinkRateMps": 0.2,
    "riseRateMps": 0.2,
    "dropConfirmFrames": 5,
    "noSinkTimeoutSeconds": 10.0,
    "targetDepthMeters": 40.0,
    "floorClearanceMeters": 5.0,
    "maxDepthMeters": 150.0,
    "sinkTimeoutSeconds": 100.0,
    "holdSeconds": 4.0,
    "logIntervalSeconds": 1.0,
    "surfaceDepthMeters": 2.0,
    "riseTimeoutSeconds": 100.0,
    "offWaterFrames": 3,
}
RISE = dict(PARAMS, riseAfterTarget=True)


def _f(ut, alt=0.0, parts=PARTS, stage=STAGE, situation="SPLASHED", name="PSUB-1",
       floor=FLOOR, vs=0.0):
    return snap(ut=ut, vessel_name=name, situation=situation, mean_altitude=alt,
                sea_floor=floor, part_count=parts, current_stage=stage, vertical_speed=vs)


def _state(params=None):
    return mlib.psub_initial_state(mlib.psub_params_from_dict(params or PARAMS))


def _walk(frames, st=None, params=None):
    st, out = st or _state(params), []
    for frame in frames:
        st, actions = mlib.psub_decide(st, frame)
        out.append(actions)
    return st, out


def _settled(params=None):
    st, acts = _walk([_f(1.0), _f(2.0), _f(3.0)], params=params)
    assert st.phase == mlib.PSUB_SETTLE, st.phase
    assert acts[2] == [Action(mlib.ACTION_SET_SAS)]
    return st


def _measure_once(st, ut0, alt0, alt1, parts=PARTS, stage=STAGE):
    """SETTLE -> MEASURE (window at ut0/alt0) -> close the window at ut0+2/alt1."""
    st, a1 = mlib.psub_decide(st, _f(ut0, alt0, parts, stage))
    assert st.phase == mlib.PSUB_MEASURE and a1 == [], (st.phase, a1)
    return mlib.psub_decide(st, _f(ut0 + 2.0, alt1, parts, stage))


def _drop(st, ut, parts_after, stage_after):
    st, acts = mlib.psub_decide(st, _f(ut, st.window_alt or 0.0, parts_after, stage_after))
    assert st.phase == mlib.PSUB_SETTLE, st.phase
    return st, acts


class GateAndHelperTests(unittest.TestCase):
    def setUp(self):
        self.p = mlib.psub_params_from_dict(PARAMS)

    def test_each_handoff_refusal_names_what_it_read(self):
        cases = (
            (_f(1.0, name="Kerbal X"), "vessel=Kerbal X, not PSUB-1"),
            (_f(1.0, name=""), "vessel=<unread>, not PSUB-1"),
            (_f(1.0, situation="PRELAUNCH"), "situation=PRELAUNCH, not SPLASHED"),
            (snap(ut=1.0, vessel_name="PSUB-1", situation="SPLASHED"),
             "mean_altitude unread"),
            (_f(1.0, parts=0), "part_count unread"),
            (_f(1.0, stage=-1), "current_stage unread"),
        )
        for frame, reason in cases:
            self.assertEqual(reason, mlib.psub_handoff_refusal(self.p, frame))
        self.assertEqual("", mlib.psub_handoff_refusal(self.p, _f(1.0)))

    def test_the_vertical_speed_is_up_positive_and_nan_when_unread(self):
        self.assertAlmostEqual(-0.5, mlib.psub_vertical_speed(0.0, 10.0, -5.0, 20.0))
        self.assertAlmostEqual(0.25, mlib.psub_vertical_speed(-10.0, 0.0, -9.0, 4.0))
        nan = mlib.psub_vertical_speed
        self.assertNotEqual(nan(None, 1.0, 0.0, 2.0), nan(None, 1.0, 0.0, 2.0))
        self.assertNotEqual(nan(0.0, 2.0, 0.0, 2.0), nan(0.0, 2.0, 0.0, 2.0))
        self.assertNotEqual(nan(0.0, 1.0, float("nan"), 2.0),
                            nan(0.0, 1.0, float("nan"), 2.0))

    def test_a_reading_is_one_whitespace_free_token(self):
        text = mlib.psub_format_reading("measure1", _state(), _f(12.5, -3.25), -0.5)
        self.assertEqual("measure1:ut=12.5,mode=sink,floats=0,ballast=0,alt=-3.25,"
                         "vs=-0.500,floor=-710.0,parts=28,stage=6,sit=SPLASHED", text)
        self.assertNotIn(" ", mlib.psub_format_reading("x", _state(), snap(ut=1.0)))

    def test_inconsistent_params_are_refused(self):
        for extra in ({"expectedVesselName": ""}, {"floatStages": 0},
                      {"ballastStages": -1}, {"riseAfterTarget": True, "ballastStages": 0},
                      {"measureSeconds": 0.0}, {"settleSeconds": -1.0},
                      {"sinkRateMps": 0.0}, {"riseRateMps": -0.1},
                      {"targetDepthMeters": 200.0}, {"surfaceDepthMeters": 50.0},
                      {"logIntervalSeconds": 0.0}):
            with self.assertRaises(ValueError, msg=extra):
                mlib.psub_params_from_dict(dict(PARAMS, **extra))

    def test_the_defaults_are_the_kit_defaults(self):
        p = mlib.psub_params_from_dict({})
        self.assertEqual(("PSUB-1", 3, 3, 40.0, 150.0, False),
                         (p.expected_vessel_name, p.float_stages, p.ballast_stages,
                          p.target_depth, p.max_depth, p.rise_after_target))


class WalkTests(unittest.TestCase):
    def test_the_handoff_stamps_the_start(self):
        st = _settled()
        self.assertEqual(("SPLASHED", 0.0, PARTS, STAGE, FLOOR),
                         (st.start_situation, st.start_alt, st.start_parts,
                          st.start_stage, st.sea_floor))
        self.assertTrue(st.readings[0].startswith("handoff:ut=3.0,"))

    def test_a_sub_that_sinks_as_delivered_drops_nothing(self):
        st = _settled()
        st, acts = _measure_once(st, 5.0, 0.0, -1.0)
        self.assertEqual([], acts)
        self.assertEqual(mlib.PSUB_SINK, st.phase)
        self.assertEqual(0, st.floats_dropped)
        self.assertAlmostEqual(-0.5, st.sink_vs)

    def test_afloat_drops_the_next_float_pair_and_confirms_by_part_count(self):
        st = _settled()
        st, acts = _measure_once(st, 5.0, 0.0, 0.0)
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        self.assertEqual(mlib.PSUB_DROP, st.phase)
        self.assertEqual(PARTS, st.drop_baseline_parts)
        st, acts = mlib.psub_decide(st, _f(7.5, 0.0, PARTS, STAGE))   # not yet
        self.assertEqual(mlib.PSUB_DROP, st.phase)
        st, acts = _drop(st, 8.0, PARTS - 4, STAGE - 1)
        self.assertEqual([], acts)
        self.assertEqual((1, 0), (st.floats_dropped, st.ballast_dropped))
        self.assertEqual(8.0, st.last_drop_ut)
        self.assertTrue(st.readings[-1].startswith("drop-float:ut=8.0,"))

    def test_two_pairs_then_sinking_then_target_hold_and_done(self):
        st = _settled()
        parts = PARTS
        for i, ut in enumerate((5.0, 12.0)):
            st, acts = _measure_once(st, ut, 0.0, 0.1, parts, STAGE - i)
            self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
            parts -= 4
            st, _ = _drop(st, ut + 3.0, parts, STAGE - i - 1)
        self.assertEqual(2, st.floats_dropped)
        st, acts = _measure_once(st, 20.0, 0.0, -1.0, parts, STAGE - 2)
        self.assertEqual(mlib.PSUB_SINK, st.phase)
        self.assertEqual(3, st.measurements)
        st, _ = mlib.psub_decide(st, _f(30.0, -20.0, parts, vs=-2.0))
        self.assertEqual(mlib.PSUB_SINK, st.phase)
        st, _ = mlib.psub_decide(st, _f(40.0, -40.5, parts, vs=-2.0))
        self.assertEqual(mlib.PSUB_HOLD, st.phase)
        self.assertFalse(st.floor_reached)
        self.assertEqual(40.0, st.target_ut)
        st, _ = mlib.psub_decide(st, _f(43.0, -46.0, parts, vs=-2.0))
        self.assertFalse(st.done)
        st, _ = mlib.psub_decide(st, _f(44.0, -48.0, parts, vs=-2.0))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.PSUB_DONE, st.phase)
        self.assertEqual(st, mlib.psub_decide(st, _f(45.0))[0])
        rows = {r.name: r for r in mlib.evaluate_psub_assertions(
            [], mlib.psub_params_from_dict(PARAMS), st)}
        self.assertTrue(all(r.met for r in rows.values()), rows)
        self.assertNotIn("roseToSurface", rows)

    def test_the_sea_floor_ends_the_sink_before_the_target(self):
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(20.0, -26.0, floor=-30.0))
        self.assertEqual(mlib.PSUB_HOLD, st.phase)
        self.assertTrue(st.floor_reached)
        self.assertTrue(st.readings[-1].startswith("at-floor:"))

    def test_the_rise_drops_the_floats_aboard_then_ballast_until_rising(self):
        st = _settled(RISE)
        st, _ = _measure_once(st, 5.0, 0.0, 0.0)                       # afloat
        st, _ = _drop(st, 8.0, 24, 5)                                  # float 1
        st, _ = _measure_once(st, 11.0, 0.0, -1.0, 24, 5)              # sinking
        st, _ = mlib.psub_decide(st, _f(30.0, -41.0, 24, 5, vs=-2.0))  # target
        self.assertEqual(mlib.PSUB_HOLD, st.phase)
        st, acts = mlib.psub_decide(st, _f(35.0, -50.0, 24, 5, vs=-2.0))  # hold end
        self.assertEqual(mlib.PSUB_MODE_RISE, st.mode)
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        # The two floats still aboard go back to back, with no measurement between.
        st, _ = _drop(st, 36.0, 20, 4)
        st, acts = mlib.psub_decide(st, _f(37.0, -52.0, 20, 4))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = _drop(st, 38.0, 16, 3)
        self.assertEqual((3, 0), (st.floats_dropped, st.ballast_dropped))
        m0 = st.measurements
        # Now settle and measure: still sinking, so one ballast pair.
        st, acts = _measure_once(st, 41.0, -56.0, -58.0, 16, 3)
        self.assertEqual(m0 + 1, st.measurements)
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = _drop(st, 44.0, 12, 2)
        self.assertEqual((3, 1), (st.floats_dropped, st.ballast_dropped))
        self.assertTrue(st.readings[-1].startswith("drop-ballast:"))
        st, acts = _measure_once(st, 47.0, -60.0, -59.0, 12, 2)       # rising
        self.assertEqual([], acts)
        self.assertEqual(mlib.PSUB_ASCEND, st.phase)
        self.assertAlmostEqual(0.5, st.rise_vs)
        st, _ = mlib.psub_decide(st, _f(80.0, -10.0, 12, 2, vs=1.5))
        self.assertFalse(st.done)
        st, _ = mlib.psub_decide(st, _f(90.0, -1.5, 12, 2, vs=1.0))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        rows = {r.name: r for r in mlib.evaluate_psub_assertions(
            [], mlib.psub_params_from_dict(RISE), st)}
        self.assertTrue(all(r.met for r in rows.values()), rows)
        self.assertIn("roseToSurface", rows)


class CalibrationEndTests(unittest.TestCase):
    def _all_floats_dropped(self, params=None):
        st = _settled(params)
        parts = PARTS
        ut = 5.0
        for i in range(3):
            st, acts = _measure_once(st, ut, 0.0, 0.0, parts, STAGE - i)
            self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
            parts -= 4
            st, _ = _drop(st, ut + 3.0, parts, STAGE - i - 1)
            ut += 6.0
        return st, ut, parts

    def test_afloat_with_every_float_gone_is_k_too_high(self):
        st, ut, parts = self._all_floats_dropped()
        last = st.last_drop_ut
        st, acts = _measure_once(st, ut, 0.0, 0.0, parts, 3)
        self.assertFalse(st.done)
        self.assertEqual([], acts)                     # re-measure, nothing to drop
        self.assertEqual(mlib.PSUB_MEASURE, st.phase)
        st, _ = mlib.psub_decide(st, _f(last + 12.0, 0.0, parts, 3))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("k is too high", st.loss_reason)
        self.assertIn("every float pair dropped", st.loss_reason)

    def test_sinking_with_every_ballast_pair_gone_is_k_too_low(self):
        p = dict(RISE, ballastStages=1, floatStages=1)
        st = _settled(p)
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(20.0, -41.0, vs=-2.0))
        st, acts = mlib.psub_decide(st, _f(25.0, -50.0, vs=-2.0))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = _drop(st, 26.0, 24, 5)                 # the one float
        st, _ = _measure_once(st, 28.0, -55.0, -57.0, 24, 5)
        st, _ = _drop(st, 31.0, 20, 4)                 # the one ballast pair
        st, acts = _measure_once(st, 33.0, -60.0, -62.0, 20, 4)
        self.assertEqual([], acts)
        st, _ = mlib.psub_decide(st, _f(42.0, -70.0, 20, 4))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("k is too low", st.loss_reason)


class NamedEndTests(unittest.TestCase):
    def test_a_refused_handoff_fails_naming_the_refusal(self):
        st, acts = _walk([_f(float(i), situation="PRELAUNCH") for i in range(10)])
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("handoff refused on 10 frames: situation=PRELAUNCH", st.loss_reason)
        self.assertTrue(all(a == [] for a in acts))

    def test_the_crush_guard_fails_and_logs_the_depth(self):
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(9.0, -151.0, vs=-3.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("past maxDepthMeters 150", st.loss_reason)
        self.assertTrue(st.readings[-1].startswith("crush-guard:"))

    def test_the_sea_floor_reads_landed_and_ends_the_run_past_the_tolerance(self):
        st = _settled()
        for i in range(3):
            st, _ = mlib.psub_decide(st, _f(4.0 + i, situation="LANDED"))
        self.assertFalse(st.done)
        st, _ = mlib.psub_decide(st, _f(7.5, situation="LANDED"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("off the water: situation LANDED on 4", st.loss_reason)

    def test_a_stage_click_that_drops_nothing_fails(self):
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, 0.0)
        for i in range(5):
            st, _ = mlib.psub_decide(st, _f(8.0 + i))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("stage click dropped nothing", st.loss_reason)

    def test_a_lost_vessel_fails(self):
        st, _ = mlib.psub_decide(_settled(), snap(ut=9.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("vessel-lost", st.loss_reason)

    def test_the_sink_and_rise_timeouts_flake_naming_the_phase(self):
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(108.0, -12.0, vs=-0.1))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.PSUB_SINK, st.flake_phase)
        self.assertIn("target 40 m", st.flake_reason)


class ReadingTests(unittest.TestCase):
    def test_every_measurement_and_every_interval_is_logged(self):
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        n = len(st.readings)
        self.assertTrue(st.readings[-1].startswith("measure1:"))
        self.assertEqual(st.readings[-1], st.psub_reading)
        st, _ = mlib.psub_decide(st, _f(7.5, -2.0, vs=-0.8))   # under the interval
        st, _ = mlib.psub_decide(st, _f(8.0, -2.5, vs=-0.8))   # 1 s after the window
        st, _ = mlib.psub_decide(st, _f(9.0, -3.5, vs=-1.0))
        self.assertEqual(n + 2, len(st.readings))
        self.assertTrue(st.readings[-1].startswith("sink:ut=9.0,"))
        self.assertIn("vs=-1.000", st.readings[-1])

    def test_the_reading_is_a_machine_diff_field(self):
        names = [f for f, _ in mlib.MACHINE_DIFF_FIELDS]
        for field in ("psub_reading", "floats_dropped", "ballast_dropped"):
            self.assertIn(field, names)

    def test_the_evidence_is_bounded(self):
        st = _state()
        for i in range(mlib.PSUB_MAX_READINGS + 5):
            st = mlib._psub_record(st, "r%d" % i)
        self.assertEqual(mlib.PSUB_MAX_READINGS, len(st.readings))
        self.assertEqual("r%d" % (mlib.PSUB_MAX_READINGS + 4), st.psub_reading)


class RowTests(unittest.TestCase):
    def test_no_state_fails_every_row(self):
        for params, names in ((PARAMS, ["handoffAfloat", "sankBelowTarget",
                                         "measurementsLogged"]),
                              (RISE, ["handoffAfloat", "sankBelowTarget", "roseToSurface",
                                      "measurementsLogged"])):
            rows = mlib.evaluate_psub_assertions([], mlib.psub_params_from_dict(params), None)
            self.assertEqual(names, [r.name for r in rows])
            self.assertFalse(any(r.met for r in rows))

    def test_a_flaked_sink_fails_the_target_and_the_measurements_rows(self):
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(108.0, -12.0))
        rows = {r.name: r for r in mlib.evaluate_psub_assertions(
            [], mlib.psub_params_from_dict(PARAMS), st)}
        self.assertTrue(rows["handoffAfloat"].met)
        self.assertFalse(rows["sankBelowTarget"].met)
        self.assertFalse(rows["measurementsLogged"].met)


class ShellTests(unittest.TestCase):
    def _dive(self, params):
        """A scripted world: afloat until two float pairs are gone, then sinking at
        1 m/s; with a rise, rising at 1 m/s once one ballast pair is gone."""
        p = mlib.psub_params_from_dict(params)
        st = mlib.psub_initial_state(p)
        world = {"alt": 0.0, "parts": PARTS, "stage": STAGE, "floats": 0, "ballast": 0}
        frames, ut = [], 10.0
        pending = 0
        while not st.done and ut < 2000.0:
            ut += 0.5
            if pending:
                world["parts"] -= 4
                world["stage"] -= 1
                if world["floats"] < p.float_stages:
                    world["floats"] += 1
                else:
                    world["ballast"] += 1
                pending = 0
            if world["ballast"] >= 1:
                vs = 1.0
            elif world["floats"] >= 2:
                vs = -1.0
            else:
                vs = 0.0
            world["alt"] = min(0.0, world["alt"] + vs * 0.5)
            frame = _f(ut, world["alt"], world["parts"], world["stage"], vs=vs)
            frames.append(frame)
            st, acts = mlib.psub_decide(st, frame)
            if any(a.kind == mlib.ACTION_ACTIVATE_STAGE for a in acts):
                pending = 1
        return frames, st

    def test_a_dive_is_mission_ok(self):
        frames, st = self._dive(PARAMS)
        self.assertIsNone(st.verdict)
        self.assertEqual(2, st.floats_dropped)
        control = FakeMissionControl(frames)
        code, result = run(psub_dive.SPEC, PARAMS, control)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])
        self.assertEqual(mlib.PSUB_DONE, result["phasesReached"][-1])
        kinds = [a.kind for a in control.actions]
        self.assertEqual(mlib.ACTION_SET_SAS, kinds[0])
        self.assertEqual(2, kinds.count(mlib.ACTION_ACTIVATE_STAGE))

    def test_a_dive_and_rise_is_mission_ok(self):
        frames, st = self._dive(RISE)
        self.assertIsNone(st.verdict, st.loss_reason or st.flake_reason)
        self.assertEqual((3, 1), (st.floats_dropped, st.ballast_dropped))
        code, result = run(psub_dive.SPEC, RISE, FakeMissionControl(frames))
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertIn("roseToSurface", [a["name"] for a in result["assertions"]])

    def test_the_control_reads_name_and_depth(self):
        control = psub_dive.make_control()
        self.assertTrue(control._read_depth)
        self.assertTrue(control._read_vessel_name)
        self.assertFalse(control._use_mechjeb)
        self.assertFalse(psub_dive.SPEC.allow_rails_warp)
        self.assertEqual(0.0, psub_dive.SPEC.max_physics_warp)


class SchemaTests(unittest.TestCase):
    def test_every_declared_key_is_read_by_the_builder_and_validates(self):
        import hlib
        with open(os.path.join(_MISSIONS, "psub_dive.schema.toml"), "rb") as fh:
            schema = tomllib.load(fh)
        src = inspect.getsource(mlib.psub_params_from_dict)
        for key in schema["params"]:
            self.assertIn('"%s"' % key, src, key)
        self.assertEqual([], hlib._validate_mission_params(RISE, schema))

    def test_every_sub_lane_validates_against_the_schema(self):
        import glob
        import hlib
        with open(os.path.join(_MISSIONS, "psub_dive.schema.toml"), "rb") as fh:
            schema = tomllib.load(fh)
        for path in sorted(glob.glob(os.path.join(_HARNESS, "scenarios", "SUB-*.toml"))):
            with open(path, "rb") as fh:
                spec = tomllib.load(fh)
            self.assertEqual("psub_dive", spec["driver"]["mission"], path)
            params = spec["driver"]["missionParams"]
            self.assertEqual([], hlib._validate_mission_params(params, schema), path)
            mlib.psub_params_from_dict(params)


if __name__ == "__main__":
    unittest.main()
