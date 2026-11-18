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


def _drop(st, ut, parts_after, stage_after, alt=None):
    if alt is None:
        alt = st.window_alt or 0.0
    st, acts = mlib.psub_decide(st, _f(ut, alt, parts_after, stage_after))
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
                      {"logIntervalSeconds": 0.0}, {"riseDropFallMeters": 0.0}):
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

    def test_resting_on_the_sea_floor_through_the_hold_ends_done(self):
        # Stock reads a hull on the sea floor LANDED; the sub keeps sinking through
        # the hold, so the hold sits on the floor and must still end PSUB-DONE.
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(20.0, -26.0, floor=-30.0))
        self.assertEqual(mlib.PSUB_HOLD, st.phase)
        for i in range(8):
            st, _ = mlib.psub_decide(st, _f(20.5 + 0.5 * i, -28.0, floor=-30.0,
                                            situation="LANDED"))
        self.assertTrue(st.done, st.loss_reason)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.PSUB_DONE, st.phase)
        self.assertEqual(0, st.off_water_max_streak)
        self.assertTrue(st.floor_reached)

    def test_floor_contact_before_the_sink_phase_ends_the_sink(self):
        # A fast sink after the decisive drop can touch bottom while still measuring.
        st = _settled()
        st, _ = mlib.psub_decide(st, _f(5.0, 0.0))
        self.assertEqual(mlib.PSUB_MEASURE, st.phase)
        st, _ = mlib.psub_decide(st, _f(6.0, -12.0, floor=-14.0, situation="LANDED"))
        self.assertEqual(mlib.PSUB_HOLD, st.phase)
        self.assertTrue(st.floor_reached)
        self.assertTrue(st.readings[-1].startswith("at-floor:"))

    def test_the_rise_starts_from_the_sea_floor(self):
        st = _settled(RISE)
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(10.0, -12.0, floor=-14.0, situation="LANDED"))
        self.assertEqual(mlib.PSUB_HOLD, st.phase)
        st, acts = mlib.psub_decide(st, _f(15.0, -12.5, floor=-14.0, situation="LANDED"))
        self.assertEqual(mlib.PSUB_MODE_RISE, st.mode)
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        self.assertEqual(mlib.PSUB_DROP, st.phase)
        st, _ = mlib.psub_decide(st, _f(15.5, -12.5, 24, 5, floor=-14.0,
                                        situation="LANDED"))
        self.assertEqual(mlib.PSUB_SETTLE, st.phase)
        self.assertEqual(1, st.floats_dropped)
        # The floats still aboard go next, unmeasured, the sub still on the floor.
        st, acts = mlib.psub_decide(st, _f(16.0, -12.5, 24, 5, floor=-14.0,
                                           situation="LANDED"))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = mlib.psub_decide(st, _f(16.5, -12.5, 20, 4, floor=-14.0,
                                        situation="LANDED"))
        self.assertEqual(2, st.floats_dropped)
        self.assertIsNone(st.verdict, st.loss_reason)
        self.assertEqual(0, st.off_water_max_streak)

    def test_landed_at_or_above_sea_level_is_not_floor_contact(self):
        self.assertTrue(mlib.psub_is_floor_contact(_f(1.0, -3.0, situation="LANDED")))
        self.assertFalse(mlib.psub_is_floor_contact(_f(1.0, 0.0, situation="LANDED")))
        self.assertFalse(mlib.psub_is_floor_contact(_f(1.0, 4.0, situation="LANDED")))
        self.assertFalse(mlib.psub_is_floor_contact(_f(1.0, -3.0, situation="SPLASHED")))
        self.assertFalse(mlib.psub_is_floor_contact(
            snap(ut=1.0, situation="LANDED")))

    def test_the_rise_drops_the_floats_aboard_and_one_ballast_pair_then_measures(self):
        st = _settled(RISE)
        st, _ = _measure_once(st, 5.0, 0.0, 0.0)                       # afloat
        st, _ = _drop(st, 8.0, 24, 5)                                  # float 1
        st, _ = _measure_once(st, 11.0, 0.0, -1.0, 24, 5)              # sinking
        st, _ = mlib.psub_decide(st, _f(30.0, -41.0, 24, 5, vs=-2.0))  # target
        self.assertEqual(mlib.PSUB_HOLD, st.phase)
        st, acts = mlib.psub_decide(st, _f(35.0, -50.0, 24, 5, vs=-2.0))  # hold end
        self.assertEqual(mlib.PSUB_MODE_RISE, st.mode)
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        m0 = st.measurements
        # The two floats still aboard AND the first ballast pair go back to back, with
        # no settle and no measurement between them.
        st, _ = _drop(st, 36.0, 20, 4, alt=-51.0)
        st, acts = mlib.psub_decide(st, _f(36.5, -51.5, 20, 4))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = _drop(st, 37.0, 16, 3, alt=-52.0)
        self.assertEqual((3, 0), (st.floats_dropped, st.ballast_dropped))
        st, acts = mlib.psub_decide(st, _f(37.5, -52.5, 16, 3))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = _drop(st, 38.0, 12, 2, alt=-53.0)
        self.assertEqual((3, 1), (st.floats_dropped, st.ballast_dropped))
        self.assertEqual(m0, st.measurements)
        self.assertTrue(st.readings[-1].startswith("drop-ballast:"))
        self.assertEqual(-53.0, st.last_drop_alt)
        # Now settle and measure: still sinking (within the fall budget), so one more.
        st, acts = _measure_once(st, 41.0, -55.0, -56.0, 12, 2)
        self.assertEqual(m0 + 1, st.measurements)
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = _drop(st, 44.0, 8, 1, alt=-57.0)
        self.assertEqual((3, 2), (st.floats_dropped, st.ballast_dropped))
        st, acts = _measure_once(st, 47.0, -58.0, -57.0, 8, 1)        # rising
        self.assertEqual([], acts)
        self.assertEqual(mlib.PSUB_ASCEND, st.phase)
        self.assertAlmostEqual(0.5, st.rise_vs)
        self.assertEqual(0, st.rise_cuts)
        st, _ = mlib.psub_decide(st, _f(80.0, -10.0, 8, 1, vs=1.5))
        self.assertFalse(st.done)
        st, _ = mlib.psub_decide(st, _f(90.0, -1.5, 8, 1, vs=1.0))
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
        st, _ = _drop(st, 26.0, 24, 5, alt=-51.0)      # the one float
        st, acts = mlib.psub_decide(st, _f(26.5, -51.5, 24, 5))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        st, _ = _drop(st, 27.0, 20, 4, alt=-52.0)      # the one ballast pair, at once
        # Every pair gone: no fall budget left to cut on, the measurement just repeats.
        st, acts = _measure_once(st, 30.0, -60.0, -66.0, 20, 4)
        self.assertEqual([], acts)
        self.assertEqual(0, st.rise_cuts)
        st, _ = mlib.psub_decide(st, _f(38.0, -80.0, 20, 4))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("k is too low", st.loss_reason)


# SUB-3's first flight, run 2026-11-18_0115 (hand-over a000551b), as the machine saw it:
# (ut, Flight.MeanAltitude) off the mission stdout's window dumps, 0.5 s apart except
# across each PSUB-DROP. The part count and the stage follow the logged drops: 28 / 6
# until the first confirm at 63.02, then 24 / 5, 20 / 4 from 64.62 and 16 / 3 from 66.24
# (build_psub_craft.py: stages 5..3 drop the float pairs, four parts each with their
# TT-70s). Sink rates: 1.603 m/s steady with every pair aboard; over the 0.5 s after
# each float confirm 2.53, 3.61 and 5.02 m/s; with every float gone 6.15 m/s on average
# over the 10.3 s settle and 7.06 m/s, still accelerating, at the crush guard.
SUB3_FLIGHT_0115 = (
    (28.30, -0.813), (28.82, -1.649), (29.34, -2.485), (29.86, -3.321), (30.36, -4.124),
    (30.88, -4.985), (31.40, -5.926), (31.90, -6.859), (32.40, -7.811), (32.92, -8.809),
    (33.44, -9.804), (33.94, -10.755), (34.44, -11.698), (34.96, -12.671), (35.46, -13.597),
    (35.96, -14.513), (36.48, -15.455), (36.98, -16.349), (37.48, -17.231), (38.00, -18.133),
    (38.50, -18.986), (39.00, -19.823), (39.52, -20.674), (40.02, -21.476), (40.52, -22.279),
    (41.04, -23.124), (41.56, -23.968), (42.08, -24.804), (42.58, -25.608), (43.08, -26.415),
    (43.60, -27.250), (44.10, -28.054), (44.60, -28.857), (45.12, -29.691), (45.64, -30.525),
    (46.14, -31.327), (46.66, -32.160), (47.18, -32.994), (47.68, -33.796), (48.18, -34.597),
    (48.70, -35.430), (49.20, -36.232), (49.70, -37.033), (50.22, -37.867), (50.74, -38.700),
    (51.24, -39.502), (51.74, -40.303), (52.26, -41.137), (52.76, -41.938), (53.26, -42.739),
    (53.78, -43.573), (54.28, -44.374), (54.78, -45.176), (55.30, -46.009), (55.80, -46.810),
    (56.32, -47.644), (56.84, -48.477), (57.36, -49.311), (57.88, -50.145), (58.38, -50.946),
    (58.88, -51.747), (59.40, -52.581), (59.90, -53.382), (60.40, -54.184), (60.92, -55.017),
    (61.42, -55.818), (61.92, -56.620), (63.02, -58.695), (63.52, -59.961), (64.62, -63.416),
    (65.14, -65.294), (66.24, -69.716), (66.74, -72.228), (67.24, -74.911), (67.76, -77.786),
    (68.28, -80.718), (68.80, -83.700), (69.32, -86.731), (69.84, -89.811), (70.36, -92.938),
    (70.88, -96.109), (71.40, -99.323), (71.92, -102.579), (72.44, -105.876), (72.96, -109.213),
    (73.46, -112.459), (73.98, -115.868), (74.48, -119.178), (75.00, -122.652), (75.52, -126.158),
    (76.02, -129.555), (76.54, -133.114), (77.06, -136.699), (77.58, -140.307), (78.08, -143.796),
    (78.58, -147.304), (79.08, -150.830),
)
SUB3_FLOWN_PARAMS = dict(PARAMS, settleSeconds=10.0, measureSeconds=10.0,
                         handoffMaxFrames=60, dropConfirmFrames=40,
                         noSinkTimeoutSeconds=60.0, sinkTimeoutSeconds=300.0,
                         holdSeconds=10.0, logIntervalSeconds=5.0, riseAfterTarget=True,
                         riseTimeoutSeconds=300.0, offWaterFrames=20)


def _sub3_frame(ut, alt):
    parts, stage = ((28, 6) if ut < 63.0 else (24, 5) if ut < 64.6
                    else (20, 4) if ut < 66.2 else (16, 3))
    return _f(ut, alt, parts, stage, floor=-708.6, vs=float("nan"))


class Sub3FlightReplayTests(unittest.TestCase):
    """The rise of SUB-3's first two flights drowned the sub: the old machine dropped the
    three floats back to back, then settled 10 s and measured with every float gone,
    and the sub fell 81 m (69.7 -> 150.8) into the crush guard before any ballast pair
    went (attempt 2: 68.3 -> 151.8)."""

    def _replay(self):
        st = _state(SUB3_FLOWN_PARAMS)
        clicks = []
        for ut, alt in SUB3_FLIGHT_0115:
            st, acts = mlib.psub_decide(st, _sub3_frame(ut, alt))
            if any(a.kind == mlib.ACTION_ACTIVATE_STAGE for a in acts):
                clicks.append((ut, alt, st.floats_dropped, st.ballast_dropped))
            if st.done or len(clicks) == 4:
                break
        return st, clicks

    def test_the_replay_follows_the_flight_up_to_the_third_float(self):
        st, clicks = self._replay()
        self.assertEqual([(61.92, -56.62, 0, 0), (63.52, -59.961, 1, 0),
                          (65.14, -65.294, 2, 0)], clicks[:3])
        self.assertEqual(3, st.floats_dropped)
        self.assertEqual(51.74, st.target_ut)
        self.assertEqual(61.92, st.hold_end_ut)

    def test_the_first_ballast_pair_follows_the_floats_at_once(self):
        # The old machine settled here and the flight fell to the crush guard at 79.08
        # with ballastDropped=0; the fix clicks the first ballast pair on the next frame,
        # 78 m above the guard.
        st, clicks = self._replay()
        self.assertIsNone(st.verdict, st.loss_reason)
        self.assertEqual(4, len(clicks), clicks)
        self.assertEqual((66.74, -72.228, 3, 0), clicks[3])
        self.assertEqual(mlib.PSUB_DROP, st.phase)
        self.assertEqual(-72.228, st.deepest)


class Sub3RiseModelTests(unittest.TestCase):
    """A first-order model of PSUB-1 fitted to the 2026-11-18 SUB-3 flights, run through
    the machine with SUB-3's committed params. Terminal speeds (m/s, up positive) per
    (float pairs, ballast pairs) aboard: (3,3) -1.6 and (0,3) -7.5 as flown (1.603 steady;
    7.06 and still rising 12.8 s after the third float); the rest from the craft's
    masses and volumes (build_psub_craft.py) with the displacement factor k = 0.67 and a
    linear water drag of 0.37 t per m/s that fit those two: (2,3) -3.5, (1,3) -5.5,
    (0,2) -3.6, (0,1) +0.19 (under riseRateMps: a measurement reads it as not rising),
    (0,0) +2.1. The approach to each terminal speed has a 7 s time constant (the flown
    -4.76 -> -7.06 m/s over 12.8 s); a stage click lands two frames later. k = 0.67 is the
    pessimistic end of what the flights allow (a quadratic drag fit gives 0.73, where the
    sub already rises with two ballast pairs gone), so this sub needs every pair. The
    model's float drops are gentler than the flight's (65.5 m at the third float against
    69.7 m flown), so it reads a few metres shallow."""

    TERMINAL = {(3, 3): -1.6, (2, 3): -3.5, (1, 3): -5.5, (0, 3): -7.5,
                (0, 2): -3.6, (0, 1): 0.19, (0, 0): 2.1}

    def _spec_params(self):
        with open(os.path.join(_HARNESS, "scenarios",
                               "SUB-3-submarine-dive-and-rise-separations.toml"), "rb") as fh:
            return tomllib.load(fh)["driver"]["missionParams"]

    def _fly(self, params):
        import math
        st = mlib.psub_initial_state(mlib.psub_params_from_dict(params))
        floats, ballast, parts, stage = 3, 3, 28, 6
        alt, vs, ut, pending = -0.8, -1.6, 28.3, []
        deepest = alt
        while not st.done and ut < 1500.0:
            ut += 0.5
            for due in [d for d in pending if d <= ut]:
                pending.remove(due)
                if floats:
                    floats -= 1
                else:
                    ballast -= 1
                parts, stage = parts - 4, stage - 1
            term = self.TERMINAL[(floats, ballast)]
            vs = term + (vs - term) * math.exp(-0.5 / 7.0)
            alt = min(0.0, alt + vs * 0.5)
            deepest = min(deepest, alt)
            st, acts = mlib.psub_decide(st, _f(ut, alt, parts, stage, floor=-708.6, vs=vs))
            if any(a.kind == mlib.ACTION_ACTIVATE_STAGE for a in acts):
                pending.append(ut + 1.0)
        return st, deepest

    def test_the_model_sub_survives_the_flown_params(self):
        # With the params SUB-3 flew (the 150 m guard) the old machine drowned the
        # model sub the way it drowned the real one (settle + measure with every float
        # gone, no ballast pair before the guard); the fixed rise stays above 100 m.
        st, deepest = self._fly(SUB3_FLOWN_PARAMS)
        self.assertIsNone(st.verdict, st.loss_reason or st.flake_reason)
        self.assertEqual(mlib.PSUB_DONE, st.phase)
        self.assertGreater(deepest, -100.0)

    def test_sub3_rises_to_the_surface_well_inside_the_crush_guard(self):
        params = self._spec_params()
        st, deepest = self._fly(params)
        self.assertIsNone(st.verdict, st.loss_reason or st.flake_reason)
        self.assertEqual(mlib.PSUB_DONE, st.phase)
        self.assertEqual((3, 3), (st.floats_dropped, st.ballast_dropped))
        self.assertIsNotNone(st.surface_ut)
        # The flights fell 81 m in 12.8 s with every float gone; the fixed rise turns the
        # model sub at 98.5 m (two rise-cuts), 100 m short of SUB-3's maxDepthMeters.
        self.assertEqual(200.0, params["maxDepthMeters"])
        self.assertGreater(deepest, -100.0, st.readings)
        self.assertGreater(st.rise_cuts, 0)
        self.assertTrue(any(r.startswith("rise-cut:") for r in st.readings))


class RiseCutTests(unittest.TestCase):
    def _after_first_ballast(self, params=None):
        p = dict(RISE, floatStages=1, ballastStages=3, **(params or {}))
        st = _settled(p)
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(20.0, -41.0, vs=-2.0))
        st, _ = mlib.psub_decide(st, _f(25.0, -50.0, vs=-2.0))     # hold end: float
        st, _ = _drop(st, 26.0, 24, 5, alt=-51.0)
        st, _ = mlib.psub_decide(st, _f(26.5, -52.0, 24, 5))       # ballast 1, at once
        st, _ = _drop(st, 27.0, 20, 4, alt=-53.0)
        self.assertEqual((1, 1), (st.floats_dropped, st.ballast_dropped))
        return st

    def test_a_settle_is_cut_when_the_sub_falls_the_budget(self):
        st = self._after_first_ballast()
        st, acts = mlib.psub_decide(st, _f(27.5, -62.9, 20, 4, vs=-5.0))
        self.assertEqual([], acts)                                 # 9.9 m: not yet
        self.assertEqual(mlib.PSUB_SETTLE, st.phase)
        st, acts = mlib.psub_decide(st, _f(28.0, -63.0, 20, 4, vs=-5.0))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        self.assertEqual(mlib.PSUB_DROP, st.phase)
        self.assertEqual(1, st.rise_cuts)
        self.assertTrue(st.readings[-1].startswith("rise-cut:ut=28.0,mode=rise,"))
        self.assertIn("vs=-5.000", st.readings[-1])

    def test_a_measurement_is_cut_too_and_the_budget_is_a_param(self):
        st = self._after_first_ballast({"riseDropFallMeters": 20.0})
        st, _ = mlib.psub_decide(st, _f(29.5, -60.0, 20, 4))       # settle ends
        self.assertEqual(mlib.PSUB_MEASURE, st.phase)
        st, acts = mlib.psub_decide(st, _f(30.0, -72.0, 20, 4))    # 19 m: not yet
        self.assertEqual([], acts)
        st, acts = mlib.psub_decide(st, _f(30.5, -73.0, 20, 4))
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], acts)
        self.assertEqual(0, st.measurements - 1)                   # only the sink one

    def test_no_cut_with_every_pair_gone(self):
        p = dict(RISE, floatStages=1, ballastStages=1)
        st = _settled(p)
        st, _ = _measure_once(st, 5.0, 0.0, -1.0)
        st, _ = mlib.psub_decide(st, _f(20.0, -41.0, vs=-2.0))
        st, _ = mlib.psub_decide(st, _f(25.0, -50.0, vs=-2.0))
        st, _ = _drop(st, 26.0, 24, 5, alt=-51.0)
        st, _ = mlib.psub_decide(st, _f(26.5, -52.0, 24, 5))
        st, _ = _drop(st, 27.0, 20, 4, alt=-53.0)
        st, acts = mlib.psub_decide(st, _f(28.0, -90.0, 20, 4))
        self.assertEqual([], acts)
        self.assertEqual(mlib.PSUB_SETTLE, st.phase)
        self.assertEqual(0, st.rise_cuts)

    def test_the_sink_never_cuts(self):
        # Sink mode: a fast fall after a float drop is the sink working, not a reason to
        # drop the next float.
        st = _settled()
        st, _ = _measure_once(st, 5.0, 0.0, 0.0)
        st, _ = _drop(st, 8.0, 24, 5, alt=-1.0)
        st, acts = mlib.psub_decide(st, _f(8.5, -30.0, 24, 5))
        self.assertEqual([], acts)
        self.assertEqual(mlib.PSUB_SETTLE, st.phase)
        self.assertEqual(-1.0, st.last_drop_alt)


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
