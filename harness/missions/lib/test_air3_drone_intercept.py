"""Unit cells for the AIR-3 lane (mission `air3_drone_intercept`: the Mallard flies into
its own released parachute drone; todo NEXT-TESTS-TO-BUILD-2026-11-11 item 3b).

Guards the machine's ways of being silently wrong:
  1. the take-off half is AIR-1's machine, handed over at its turn altitude and never
     re-implemented (a flake or a loss there carries AIR-1's own verdict and reason);
  2. the release: only level, wings level, at altitude, over the sea and not mid-turn,
     with the sibling watch armed on the drone BEFORE the stage click;
  3. a drone that never appears under its expected name gives up by name (the vessel
     name KSP gives a split-off probe is an assumption a flight can falsify);
  4. the pursuit steers at the drone's live position, led by its velocity, and asks
     for the vertical speed that meets the drone's altitude at the intercept
     (curvature included);
  5. a missed pass is counted and re-flown, and every bound gives up by name;
  6. the collision is read off part counts, a drone gone or a chaser lost, ONLY near
     the drone and only after the counts settled (a count read across the split frame
     must not read as a hit at the release);
  7. the aftermath ends only with both vessels down or gone;
  8. the runner's opt-in air-target channel (off by default, reuses the sibling
     sweep's handle, survives the chaser's loss);
  9. the derivative craft (harness/tools/build_air_drone_carrier.py): committed bytes
     equal the derivation and every structural post-condition holds;
 10. a schema / params / shell drift (the params read are derived by an AST walk).

NO krpc, NO KSP, NO network.
"""

import ast
import importlib.util
import math
import os
import sys
import tomllib
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
_HARNESS = os.path.dirname(_MISSIONS)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mlib  # noqa: E402
import mission_runner  # noqa: E402

SCHEMA = os.path.join(_MISSIONS, "air3_drone_intercept.schema.toml")
SHELL = os.path.join(_MISSIONS, "air3_drone_intercept.py")
BUILDER = os.path.join(_HARNESS, "tools", "build_air_drone_carrier.py")

CHASER = "AIR Drone Carrier"
DRONE = "AIR Drone Carrier Probe"
RWY_LAT, RWY_LON = -0.0486, -74.72
REL_LAT, REL_LON = -0.05, -74.10
M_PER_DEG = 2.0 * math.pi * mlib.KERBIN_RADIUS_M / 360.0


def params(**kw):
    base = {"craftName": CHASER, "recordSettleFrames": 2, "rolloutFrames": 20}
    base.update(kw)
    return mlib.ax_params_from_dict(base)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def fly(ut=300.0, lat=REL_LAT, lon=REL_LON, hdg=90.0, alt=1600.0, spd=85.0, vs=0.0,
        roll=0.0, sit="FLYING", name=CHASER, rel=None, tparts=4, oparts=38,
        present=None, tsit="FLYING", **kw):
    """One poll of the chaser. ``rel`` is the drone's (up, north, east); with a ``rel``
    the drone is present unless ``present`` says otherwise."""
    if present is None:
        present = 1 if rel is not None else -1
    return snap(ut=ut, situation=sit, vessel_name=name, latitude=lat, longitude=lon,
                heading=hdg, altitude=alt, surface_speed=spd, vertical_speed=vs, roll=roll,
                surface_pitch=3.0,
                air_target_rel_position=rel if rel is not None else mlib.NAN3,
                air_target_part_count=tparts if present == 1 else -1,
                own_part_count=oparts,
                sibling_present=present,
                sibling_situation=tsit if present == 1 else "", **kw)


def kinds(actions):
    return [a.kind for a in actions]


def of_kind(actions, kind):
    return [a for a in actions if a.kind == kind]


def ascending(p=None, ut=200.0):
    p = p or params()
    st = mlib.ax_initial_state(p)
    return mlib._ax_enter(replace(st, air=replace(st.air, launch_commanded=True,
                                                  phase=mlib.AIR_CIRCUIT)),
                          mlib.AX_ASCEND, ut)


def released(p=None, ut=300.0, hdg=90.0):
    """A state on the release poll's exit: EXTEND, anchored at REL_LAT / REL_LON."""
    p = p or params()
    st = ascending(p, ut=ut - 100.0)
    return mlib._ax_enter(st, mlib.AX_EXTEND, ut, release_ut=ut, release_lat=REL_LAT,
                          release_lon=REL_LON, release_altitude=1600.0,
                          extend_heading=hdg, attempt_start_ut=ut)


def seen(st, ut=300.5, rel=(-2.0, 0.0, -1.0), n=None):
    """Feed the settle polls after the sighting (counts 38 / 4)."""
    n = mlib.AX_REF_SETTLE_FRAMES if n is None else n
    for i in range(n):
        st, _ = mlib.ax_decide(st, fly(ut=ut + 0.5 * i, rel=rel))
    return st


def pursuing(p=None, ut=400.0, tgt_alt=1500.0):
    """PURSUE with the drone tracked at 2 km east of the release point, at rest."""
    p = p or params()
    st = seen(released(p))
    return mlib._ax_enter(st, mlib.AX_PURSUE, ut, tgt_n=0.0, tgt_e=2000.0,
                          tgt_alt=tgt_alt, tgt_ut=ut - 0.5, tgt_vel_known=True,
                          tgt_vn=0.0, tgt_ve=0.0, tgt_vu=-4.5, attempt_min_range=None,
                          attempt_start_ut=ut)


class GeometryTests(unittest.TestCase):
    def test_local_offset_round_trips(self):
        n, e = mlib.ax_local_offset(REL_LAT, REL_LON, REL_LAT + 0.01, REL_LON - 0.02)
        self.assertAlmostEqual(0.01 * M_PER_DEG, n, places=3)
        self.assertLess(e, 0.0)
        lat, lon = mlib.ax_local_point(REL_LAT, REL_LON, n, e)
        self.assertAlmostEqual(REL_LAT + 0.01, lat, places=9)
        self.assertAlmostEqual(REL_LON - 0.02, lon, places=9)

    def test_target_altitude_corrects_for_the_curvature(self):
        # A point on the chaser's tangent plane 1 km away stands h^2 / 2R above the sea.
        self.assertAlmostEqual(1000.0 + 1e6 / 1.2e6,
                               mlib.ax_target_altitude(1000.0, 0.0, 1000.0), places=6)
        self.assertAlmostEqual(990.0, mlib.ax_target_altitude(1000.0, -10.0, 0.0))
        self.assertTrue(math.isnan(mlib.ax_target_altitude(float("nan"), 0.0, 1.0)))

    def test_range_is_three_dimensional_and_nan_when_unread(self):
        self.assertAlmostEqual(13.0, mlib.ax_range((3.0, 4.0, 12.0)))
        self.assertTrue(math.isnan(mlib.ax_range(mlib.NAN3)))
        self.assertFalse(mlib.ax_rel_ok((1.0, float("nan"), 2.0)))

    def test_time_to_go_floors_the_closing_speed(self):
        self.assertAlmostEqual(10.0, mlib.ax_time_to_go(750.0, 75.0))
        self.assertAlmostEqual(750.0 / mlib.AX_MIN_CLOSING, mlib.ax_time_to_go(750.0, -30.0))
        self.assertAlmostEqual(750.0 / mlib.AX_MIN_CLOSING, mlib.ax_time_to_go(750.0, None))

    def test_lead_is_velocity_times_time_to_go_capped(self):
        self.assertEqual((10.0, -20.0), mlib.ax_lead(1.0, -2.0, 10.0))
        ln, le = mlib.ax_lead(30.0, 40.0, 100.0)
        self.assertAlmostEqual(mlib.AX_MAX_LEAD, math.hypot(ln, le))
        self.assertAlmostEqual(0.6, ln / mlib.AX_MAX_LEAD)

    def test_intercept_vs_meets_the_altitude_at_the_intercept(self):
        # 10 s to go, 20 m low, the drone sinking 4.5 m/s: -4.5 + 20 / 10.
        self.assertAlmostEqual(-2.5, mlib.ax_intercept_vs(-4.5, 20.0, 10.0))
        # Far out the gain floors at 0.1 per second, close in it caps at 0.4.
        self.assertAlmostEqual(-4.5 + 0.1 * 20.0, mlib.ax_intercept_vs(-4.5, 20.0, 60.0))
        self.assertAlmostEqual(-4.5 + 0.4 * 5.0, mlib.ax_intercept_vs(-4.5, 5.0, 0.5))
        self.assertEqual(-mlib.AIR_MAX_SINK, mlib.ax_intercept_vs(-4.5, -500.0, 5.0))
        self.assertEqual(-4.5, mlib.ax_intercept_vs(-4.5, float("nan"), 5.0))

    def test_turn_latch_holds_a_target_behind_on_one_side(self):
        err, latch = mlib.ax_latched_error(170.0, 0.0)
        self.assertEqual((170.0, 1.0), (err, latch))
        # The bearing wraps past 180: the latch keeps the turn going right.
        self.assertEqual((175.0, 1.0), mlib.ax_latched_error(-175.0, latch))
        self.assertEqual((140.0, 1.0), mlib.ax_latched_error(-140.0, latch))
        # Inside the release band the latch lets go.
        self.assertEqual((-100.0, 0.0), mlib.ax_latched_error(-100.0, latch))
        self.assertEqual((140.0, 0.0), mlib.ax_latched_error(140.0, 0.0))


class ParamsTests(unittest.TestCase):
    def test_defaults(self):
        p = params()
        self.assertEqual(DRONE, p.target_vessel_name)
        self.assertEqual(CHASER, p.air.expected_vessel_name)
        self.assertEqual(2, len(p.loiter))
        self.assertIsNone(mlib.ax_params_problem(p))

    def test_default_loiter_and_release_stay_near_the_space_center(self):
        # The Space Center culls KSC ghosts past 25 km of the camera (ParsekConfig
        # CullDistanceMeters): the loiter points stay within 10 km of the pad.
        p = params()
        for lat, lon in p.loiter:
            self.assertLess(mlib.air_ground_distance(-0.0972, -74.5577, lat, lon), 10000.0)
            self.assertGreaterEqual(lon, p.release_min_longitude)

    def test_problems_are_named(self):
        cases = [({"targetVesselName": CHASER}, "must differ"),
                 ({"loiterPoints": "0,-74"}, "two points"),
                 ({"loiterPoints": "x"}, "two points"),
                 ({"releaseAltitude": 320.0}, "releaseAltitude"),
                 ({"collisionRange": 2000.0}, "collisionRange"),
                 ({"maxAttempts": 0}, "maxAttempts"),
                 ({"craftName": ""}, "craftName")]
        for kw, needle in cases:
            with self.subTest(kw=kw):
                problem = mlib.ax_params_problem(params(**kw))
                self.assertIsNotNone(problem)
                self.assertIn(needle, problem)

    def test_bad_params_flake_before_launching(self):
        st, acts = mlib.ax_decide(mlib.ax_initial_state(params(loiterPoints="0,-74")),
                                  snap(vessel_lost=True))
        self.assertEqual([], acts)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("loiterPoints", st.flake_reason)


class HandOverTests(unittest.TestCase):
    def _to_climb(self, p):
        st = mlib.ax_initial_state(p)
        st, acts = mlib.ax_decide(st, snap(vessel_lost=True))
        self.assertEqual([mlib.ACTION_LAUNCH_VESSEL], kinds(acts))
        self.assertEqual(CHASER, acts[0].text)
        rw = dict(ut=100.0, situation="PRE_LAUNCH", vessel_name=CHASER, latitude=RWY_LAT,
                  longitude=RWY_LON, heading=90.0, altitude=1.0, surface_speed=0.0,
                  surface_pitch=1.5, roll=0.0, vertical_speed=0.0, throttle=0.0)
        st, _ = mlib.ax_decide(st, snap(**rw))
        st, _ = mlib.ax_decide(st, snap(**rw))
        self.assertEqual(mlib.AIR_ARM, st.air.phase)
        st, acts = mlib.ax_decide(st, snap(**rw))
        self.assertEqual([mlib.ACTION_SET_BRAKES, mlib.ACTION_SET_THROTTLE,
                          mlib.ACTION_ACTIVATE_STAGE], kinds(acts))
        for _ in range(p.air.record_settle_frames):
            st, _ = mlib.ax_decide(st, snap(**rw))
        self.assertEqual(mlib.AIR_TAKEOFF, st.air.phase)
        for i in range(mlib.AIR_AIRBORNE_FRAMES):
            st, _ = mlib.ax_decide(st, fly(ut=130.0 + 0.5 * i, lat=RWY_LAT, lon=-74.65,
                                           alt=15.0 + i, spd=66.0, vs=3.0))
        self.assertEqual(mlib.AIR_CLIMB, st.air.phase)
        self.assertEqual(mlib.AX_AIR, st.phase)
        return st

    def test_the_turn_altitude_hands_over_to_the_ascend(self):
        p = params()
        st = self._to_climb(p)
        st, _ = mlib.ax_decide(st, fly(ut=180.0, lat=RWY_LAT, lon=-74.4, alt=200.0, vs=6.0))
        self.assertEqual(mlib.AX_AIR, st.phase)
        st, _ = mlib.ax_decide(st, fly(ut=190.0, lat=RWY_LAT, lon=-74.4, alt=310.0, vs=6.0))
        self.assertEqual(mlib.AX_ASCEND, st.phase)
        self.assertTrue(st.air.gear_up)
        # The next poll is this machine's: it steers for the first loiter point.
        st, acts = mlib.ax_decide(st, fly(ut=190.5, lat=RWY_LAT, lon=-74.4, alt=312.0,
                                          vs=6.0))
        self.assertEqual(mlib.AX_ASCEND, st.phase)
        self.assertEqual(0, len(of_kind(acts, mlib.ACTION_ACTIVATE_STAGE)))

    def test_an_air_flake_carries_air_1s_reason(self):
        p = params()
        st = self._to_climb(p)
        st, _ = mlib.ax_decide(st, fly(ut=200.0, lat=RWY_LAT, lon=-74.4, alt=0.5,
                                       sit="SPLASHED"))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("touched the surface", st.flake_reason)

    def test_a_loss_in_the_take_off_is_air_1s_assert_fail(self):
        st = self._to_climb(params())
        st, _ = mlib.ax_decide(st, snap(ut=200.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("CLIMB", st.loss_reason)


class AscendTests(unittest.TestCase):
    def test_steers_for_the_loiter_point_and_climbs(self):
        st = ascending()
        lat, lon = st.params.loiter[0]
        st, acts = mlib.ax_decide(st, fly(ut=210.0, lat=lat + 0.02, lon=lon - 0.03,
                                          alt=800.0, hdg=90.0))
        self.assertEqual(mlib.AX_ASCEND, st.phase)
        # The point lies south-east of a plane heading east: a right bank, nose up.
        self.assertGreater(st.air.cmd_roll, 0.0)
        self.assertGreater(st.air.cmd_pitch, mlib.AIR_AOA_TRIM_DEG)
        self.assertEqual([], of_kind(acts, mlib.ACTION_ACTIVATE_STAGE))

    def test_capture_swaps_the_loiter_point(self):
        st = ascending()
        lat, lon = st.params.loiter[0]
        st, _ = mlib.ax_decide(st, fly(ut=210.0, lat=lat, lon=lon, alt=900.0))
        self.assertEqual(1, st.loiter_index)
        self.assertEqual(1, st.loiter_captures)
        lat, lon = st.params.loiter[1]
        st, _ = mlib.ax_decide(st, fly(ut=260.0, lat=lat, lon=lon, alt=1100.0))
        self.assertEqual(0, st.loiter_index)

    def _ready(self, st, ut=300.0, **kw):
        # Level at release altitude, flying west toward the second loiter point.
        lat, lon = st.params.loiter[1]
        base = dict(ut=ut, lat=lat, lon=lon + 0.15, hdg=270.0, alt=1590.0, vs=0.5,
                    roll=2.0)
        base.update(kw)
        return fly(**base)

    def test_release_after_three_ready_polls_arms_the_watch_before_the_click(self):
        st = replace(ascending(), loiter_index=1)
        for i in range(mlib.AX_RELEASE_FRAMES - 1):
            st, acts = mlib.ax_decide(st, self._ready(st, ut=300.0 + 0.5 * i))
            self.assertEqual(mlib.AX_ASCEND, st.phase)
            self.assertEqual([], of_kind(acts, mlib.ACTION_ACTIVATE_STAGE))
        st, acts = mlib.ax_decide(st, self._ready(st, ut=301.0))
        self.assertEqual(mlib.AX_EXTEND, st.phase)
        self.assertEqual([mlib.ACTION_SET_SIBLING_WATCH, mlib.ACTION_ACTIVATE_STAGE],
                         kinds(acts)[:2])
        self.assertEqual(DRONE, acts[0].text)
        self.assertEqual(301.0, st.release_ut)
        self.assertAlmostEqual(270.0, st.extend_heading)
        self.assertEqual(1590.0, st.release_altitude)

    def test_release_waits_for_every_condition(self):
        bad = {"alt": 1500.0, "roll": 15.0, "vs": 6.0, "lon": -74.35, "hdg": 200.0,
               "sit": "SPLASHED"}
        for key, value in bad.items():
            with self.subTest(key=key):
                st = replace(ascending(), loiter_index=1)
                for i in range(mlib.AX_RELEASE_FRAMES + 2):
                    st, acts = mlib.ax_decide(st, self._ready(st, ut=300.0 + 0.5 * i,
                                                              **{key: value}))
                    self.assertEqual([], of_kind(acts, mlib.ACTION_ACTIVATE_STAGE))
                    if st.done:
                        break
                self.assertNotEqual(mlib.AX_EXTEND, st.phase)

    def test_a_broken_ready_streak_starts_again(self):
        st = replace(ascending(), loiter_index=1)
        st, _ = mlib.ax_decide(st, self._ready(st, ut=300.0))
        st, _ = mlib.ax_decide(st, self._ready(st, ut=300.5))
        st, _ = mlib.ax_decide(st, self._ready(st, ut=301.0, roll=12.0))
        self.assertEqual(0, st.release_streak)
        st, _ = mlib.ax_decide(st, self._ready(st, ut=301.5))
        self.assertEqual(mlib.AX_ASCEND, st.phase)

    def test_timeout_and_surface_and_loss(self):
        st, _ = mlib.ax_decide(ascending(ut=0.0), fly(ut=mlib.AX_ASCEND_TIMEOUT_SECONDS + 1,
                                                      alt=900.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("release altitude", st.flake_reason)
        st, _ = mlib.ax_decide(ascending(), fly(ut=210.0, alt=0.0, sit="SPLASHED"))
        self.assertIn("before the release", st.flake_reason)
        st, _ = mlib.ax_decide(ascending(), snap(ut=210.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)


class ExtendAndTrackTests(unittest.TestCase):
    def test_a_drone_that_never_appears_gives_up_by_name(self):
        st = released()
        for i in range(mlib.AX_TARGET_CONFIRM_FRAMES + 1):
            st, _ = mlib.ax_decide(st, fly(ut=300.5 + 0.5 * i, present=0, oparts=42))
            if st.done:
                break
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(repr(DRONE), st.flake_reason)
        self.assertIn("named it otherwise", st.flake_reason)

    def test_the_sighting_settles_the_part_count_references(self):
        st = released()
        # The split frame read the old count; the next polls read the settled one.
        st, _ = mlib.ax_decide(st, fly(ut=300.5, rel=(-1.0, 0.0, 0.0), oparts=42, tparts=0))
        self.assertTrue(st.target_seen)
        self.assertFalse(st.done)
        for i in range(1, mlib.AX_REF_SETTLE_FRAMES):
            st, _ = mlib.ax_decide(st, fly(ut=300.5 + 0.5 * i, rel=(-2.0, 0.0, -5.0 * i)))
            self.assertFalse(st.done, st.flake_reason)
        self.assertEqual(38, st.own_parts_ref)
        self.assertEqual(4, st.target_parts_ref)
        self.assertEqual(mlib.AX_EXTEND, st.phase)

    def test_extend_holds_the_heading_then_turns_back(self):
        p = params(extendSeconds=10.0)
        st = seen(released(p))
        st, acts = mlib.ax_decide(st, fly(ut=305.0, hdg=95.0, rel=(-20.0, 0.0, -400.0)))
        self.assertEqual(mlib.AX_EXTEND, st.phase)
        # Heading 095 against the held 090: a left bank back onto it.
        self.assertLess(st.air.cmd_roll, 0.0)
        st, _ = mlib.ax_decide(st, fly(ut=310.5, rel=(-40.0, 0.0, -800.0)))
        self.assertEqual(mlib.AX_PURSUE, st.phase)
        self.assertIsNone(st.attempt_min_range)

    def test_the_drone_is_tracked_in_the_release_frame(self):
        st = seen(released())
        # The chaser 1 km east of the release, the drone 500 m west of it and 30 m
        # below; a poll later the chaser moved and the drone sank 2.5 m.
        east = 1000.0 / M_PER_DEG
        st, _ = mlib.ax_decide(st, fly(ut=310.0, lon=REL_LON + east, rel=(-30.0, 0.0,
                                                                         -500.0)))
        self.assertAlmostEqual(500.0, st.tgt_e, delta=1.0)
        self.assertAlmostEqual(1600.0 - 30.0 + 500.0 ** 2 / 1.2e6, st.tgt_alt, places=3)
        east2 = 1042.5 / M_PER_DEG
        st, _ = mlib.ax_decide(st, fly(ut=310.5, lon=REL_LON + east2,
                                       rel=(-32.5, 0.0, -542.5)))
        self.assertAlmostEqual(500.0, st.tgt_e, delta=1.0)
        self.assertLess(st.tgt_vu, 0.0)
        self.assertLess(st.closing_speed, 0.0)


class PursueTests(unittest.TestCase):
    def test_banks_toward_a_drone_on_the_left_and_climbs_to_it(self):
        st = pursuing()
        # Heading east, the drone 1500 m ahead and 300 m north, 40 m above.
        st, acts = mlib.ax_decide(st, fly(ut=400.5, lon=REL_LON + 500.0 / M_PER_DEG,
                                          rel=(40.0, 300.0, 1500.0), alt=1460.0))
        self.assertEqual(mlib.AX_PURSUE, st.phase)
        self.assertLess(st.air.cmd_roll, 0.0)
        self.assertGreater(st.air.cmd_pitch, mlib.AIR_AOA_TRIM_DEG)
        self.assertIn(mlib.ACTION_SET_ROLL_INPUT, kinds(acts))

    def test_the_lead_moves_the_aim_point_with_the_drone(self):
        st = replace(pursuing(), tgt_vn=-5.0, closing_speed=75.0)
        rel = (0.0, 0.0, 1500.0)
        s = fly(ut=400.5, lon=REL_LON + 500.0 / M_PER_DEG, rel=rel, alt=1500.0)
        pitch, limit, thr, err, brg, lead, tgo = mlib.ax_pursue_command(st, s)
        self.assertAlmostEqual(20.0, tgo, places=6)
        self.assertAlmostEqual(100.0, lead, places=6)
        # The drone drifts south: the aim point lies right of the nose.
        self.assertGreater(err, 0.0)
        st0 = replace(st, tgt_vn=0.0)
        err0 = mlib.ax_pursue_command(st0, s)[3]
        self.assertAlmostEqual(0.0, err0, delta=0.05)

    def test_a_pass_counts_an_attempt_and_extends_again(self):
        st = pursuing()
        ut = 400.5
        # Closing to 40 m, then opening on two polls.
        # 75 m/s past the drone 40 m abeam: 37.5 m a poll.
        for i in range(9):
            st, _ = mlib.ax_decide(st, fly(ut=ut, rel=(0.0, 40.0, 300.0 - 37.5 * i)))
            ut += 0.5
        self.assertEqual(mlib.AX_PURSUE, st.phase)
        # Opening, but not yet past the closest by the margin.
        st, _ = mlib.ax_decide(st, fly(ut=ut, rel=(0.0, 40.0, -15.0), hdg=90.0))
        self.assertEqual(0, st.open_streak)
        st, _ = mlib.ax_decide(st, fly(ut=ut + 0.5, rel=(0.0, 40.0, -52.5), hdg=90.0))
        self.assertEqual(mlib.AX_PURSUE, st.phase)
        st, acts = mlib.ax_decide(st, fly(ut=ut + 1.0, rel=(0.0, 40.0, -90.0), hdg=90.0))
        self.assertEqual(mlib.AX_EXTEND, st.phase)
        self.assertEqual(1, st.attempts)
        self.assertAlmostEqual(40.0, st.pass_ranges[0], places=6)
        self.assertIsNone(st.attempt_min_range)

    def test_a_stalled_approach_counts_as_a_pass(self):
        st = replace(pursuing(), closing_speed=5.0)
        ut = 400.5
        for i in range(mlib.AX_CLOSING_ABORT_FRAMES):
            st, _ = mlib.ax_decide(st, fly(ut=ut + 0.5 * i, rel=(0.0, 0.0, 1000.0 - i)))
        self.assertEqual(mlib.AX_EXTEND, st.phase)
        self.assertEqual(1, st.attempts)

    def test_max_attempts_give_up_by_name(self):
        st = replace(pursuing(params(maxAttempts=1)), closing_speed=5.0)
        for i in range(mlib.AX_CLOSING_ABORT_FRAMES):
            st, _ = mlib.ax_decide(st, fly(ut=400.5 + 0.5 * i, rel=(0.0, 0.0, 1000.0 - i)))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("no collision after 1 pass", st.flake_reason)

    def test_bounds_give_up_by_name(self):
        st, _ = mlib.ax_decide(pursuing(tgt_alt=100.0), fly(ut=400.5, alt=1000.0,
                                                            rel=(-900.0, 0.0, 1000.0)))
        self.assertIn("sank", st.flake_reason)
        st, _ = mlib.ax_decide(pursuing(), fly(ut=400.5, rel=(0.0, 0.0, 9000.0)))
        self.assertIn("limit 8000", st.flake_reason)
        st, _ = mlib.ax_decide(pursuing(ut=0.0), fly(
            ut=mlib.AX_ATTEMPT_TIMEOUT_SECONDS + 1, rel=(0.0, 0.0, 3000.0)))
        self.assertIn("without a pass", st.flake_reason)
        st, _ = mlib.ax_decide(pursuing(), fly(ut=400.5, alt=0.0, sit="SPLASHED",
                                               rel=(0.0, 0.0, 3000.0)))
        self.assertIn("with no collision", st.flake_reason)

    def test_a_blind_poll_holds_the_commands(self):
        st = pursuing()
        st2, acts = mlib.ax_decide(st, fly(ut=400.5, present=-1))
        self.assertEqual([], acts)
        self.assertEqual(mlib.AX_PURSUE, st2.phase)
        self.assertFalse(st2.done)


def _point_mass_pass(drone_ne=(0.0, 1500.0), drone_alt=1500.0, drone_vs=-4.5,
                     tau=1.0):
    """Fly the machine's EXTEND and first PURSUE against a point-mass plane: 75 m/s,
    the bank and the pitch following their targets with a first-order lag ``tau`` (s),
    the turn rate g tan(bank) / v, the flight path the pitch less the 3 deg trim, and a
    drone hanging at ``drone_ne`` (m from the release point) sinking ``drone_vs``.
    The drone's offset is handed to the machine exactly as the runner reads it (up,
    north, east in the chaser's surface frame, the curvature drop included). Returns
    the true closest 3-D distance flown in the first PURSUE and the machine state."""
    st = seen(released(params()))
    n = e = 0.0
    alt, hdg, v, roll, pitch = 1600.0, 90.0, 75.0, 0.0, 3.0
    dn, de = drone_ne
    dalt = drone_alt
    ut = 302.0
    best = float("inf")
    dt = 0.05
    for _ in range(900):
        lat, lon = mlib.ax_local_point(REL_LAT, REL_LON, n, e)
        rn, re_ = dn - n, de - e
        h = math.hypot(rn, re_)
        up = dalt - alt - h * h / (2.0 * mlib.KERBIN_RADIUS_M)
        s = replace(fly(ut=ut, lat=lat, lon=lon, hdg=hdg % 360.0, alt=alt, spd=v,
                        vs=v * math.sin(math.radians(pitch - 3.0)), roll=roll,
                        rel=(up, rn, re_)), surface_pitch=pitch)
        phase = st.phase
        st, _ = mlib.ax_decide(st, s)
        if st.done or (phase == mlib.AX_PURSUE and st.phase != mlib.AX_PURSUE):
            break
        bank_t = st.air.cmd_roll or 0.0
        pitch_t = st.air.cmd_pitch if st.air.cmd_pitch is not None else 3.0
        for _ in range(10):
            roll += (bank_t - roll) * dt / tau
            pitch += (pitch_t - pitch) * dt / tau
            hdg += math.degrees(9.81 * math.tan(math.radians(roll)) / v) * dt
            gamma = math.radians(pitch - 3.0)
            n += v * math.cos(gamma) * math.cos(math.radians(hdg)) * dt
            e += v * math.cos(gamma) * math.sin(math.radians(hdg)) * dt
            alt += v * math.sin(gamma) * dt
            dalt += drone_vs * dt
            if st.phase == mlib.AX_PURSUE:
                best = min(best, math.sqrt((dn - n) ** 2 + (de - e) ** 2
                                           + (dalt - alt) ** 2))
        ut += 0.5
    return best, st


class PointMassPursuitTests(unittest.TestCase):
    """Not a flight model (no aerodynamics, no roll authority limit): it proves the
    pursuit's GEOMETRY end to end - the (up, north, east) axes, the release-anchored
    frame, the bank direction, the curvature term and the intercept vertical speed -
    by flying the law at a drone and measuring how close it came. A sign error in any
    of them sends the plane kilometres wide."""

    def test_first_pass_meets_a_drone_dead_astern(self):
        for tau in (0.5, 1.0, 2.0):
            with self.subTest(tau=tau):
                best, st = _point_mass_pass(drone_ne=(0.0, -500.0), tau=tau)
                self.assertLess(best, 5.0)
                self.assertEqual(1, st.attempts)

    def test_first_pass_meets_a_drone_off_to_one_side_and_higher(self):
        for ne, alt in (((300.0, 200.0), 1550.0), ((-400.0, -900.0), 1450.0)):
            with self.subTest(ne=ne, alt=alt):
                best, _ = _point_mass_pass(drone_ne=ne, drone_alt=alt)
                self.assertLess(best, 5.0)


class CollisionTests(unittest.TestCase):
    def _near(self, **kw):
        st = pursuing()
        st, _ = mlib.ax_decide(st, fly(ut=400.5, rel=(0.0, 0.0, 60.0)))
        base = dict(ut=401.0, rel=(0.0, 0.0, 25.0))
        base.update(kw)
        return mlib.ax_decide(st, fly(**base))

    def test_a_chaser_part_drop_near_the_drone_is_the_collision(self):
        st, acts = self._near(oparts=31)
        self.assertEqual(mlib.AX_AFTERMATH, st.phase)
        self.assertEqual("chaser-parts-38->31", st.collision_kind)
        self.assertEqual(401.0, st.collision_ut)
        self.assertEqual((38, 31), st.collision_own_parts)
        self.assertAlmostEqual(25.0, st.collision_range)
        thr = of_kind(acts, mlib.ACTION_SET_THROTTLE)
        self.assertEqual([0.0], [a.value for a in thr])
        # Nose down while the Mallard is still the active vessel.
        self.assertEqual(mlib.AX_AFTERMATH_PITCH, st.air.cmd_pitch)

    def test_a_drone_part_drop_or_a_drone_gone_is_the_collision(self):
        st, _ = self._near(tparts=2)
        self.assertEqual("drone-parts-4->2", st.collision_kind)
        st, _ = self._near(rel=None, present=0)
        self.assertEqual("drone-absent", st.collision_kind)

    def test_a_chaser_lost_or_replaced_is_the_collision(self):
        st = pursuing()
        st, _ = mlib.ax_decide(st, fly(ut=400.5, rel=(0.0, 0.0, 60.0)))
        lost = snap(ut=401.0, vessel_lost=True, sibling_present=1, sibling_situation="FLYING",
                    air_target_part_count=4)
        st2, acts = mlib.ax_decide(st, lost)
        self.assertEqual(mlib.AX_AFTERMATH, st2.phase)
        self.assertEqual("chaser-lost", st2.collision_kind)
        self.assertEqual([mlib.ACTION_SET_THROTTLE], kinds(acts))
        st3, acts = self._near(name="AIR Drone Carrier Debris", oparts=5)
        self.assertEqual("chaser-replaced(AIR Drone Carrier Debris)", st3.collision_kind)
        # The active vessel is a fragment: neutral controls, never a dive.
        self.assertTrue(all(a.value == 0.0 for a in of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)))

    def test_a_drop_far_from_the_drone_is_not_the_collision(self):
        st = pursuing()
        st, _ = mlib.ax_decide(st, fly(ut=400.5, rel=(0.0, 0.0, 900.0), oparts=37))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("not the planned collision", st.flake_reason)

    def test_nothing_reads_as_a_hit_before_the_counts_settle(self):
        st = released()
        # The split frame and the next: the chaser count falls 42 -> 38 right next to
        # the drone, which is the separation, not a hit.
        st, _ = mlib.ax_decide(st, fly(ut=300.5, rel=(-1.0, 0.0, 0.0), oparts=42))
        st, _ = mlib.ax_decide(st, fly(ut=301.0, rel=(-1.5, 0.0, -2.0), oparts=38))
        self.assertEqual(mlib.AX_EXTEND, st.phase)
        self.assertFalse(st.done)
        self.assertIsNone(mlib.ax_collision_signal(st, fly(ut=301.0, oparts=1,
                                                           rel=(0.0, 0.0, 1.0))))


class AftermathTests(unittest.TestCase):
    def _after(self, ut=401.0):
        st = pursuing()
        st, _ = mlib.ax_decide(st, fly(ut=400.5, rel=(0.0, 0.0, 60.0)))
        st, _ = mlib.ax_decide(st, fly(ut=ut, rel=(0.0, 0.0, 25.0), oparts=30))
        self.assertEqual(mlib.AX_AFTERMATH, st.phase)
        return st

    def test_done_once_both_are_down_after_the_minimum(self):
        st = self._after()
        st, _ = mlib.ax_decide(st, fly(ut=420.0, sit="SPLASHED", alt=0.0, oparts=12,
                                       rel=(-300.0, 0.0, 100.0)))
        self.assertEqual("SPLASHED", st.own_end)
        self.assertIsNone(st.target_end)
        st, _ = mlib.ax_decide(st, fly(ut=430.0, sit="SPLASHED", alt=0.0, oparts=12,
                                       rel=(-1.0, 0.0, 100.0), tsit="SPLASHED"))
        self.assertEqual("SPLASHED", st.target_end)
        self.assertFalse(st.done)
        st, _ = mlib.ax_decide(st, fly(ut=401.0 + params().aftermath_min_seconds,
                                       sit="SPLASHED", alt=0.0, oparts=12,
                                       rel=(-1.0, 0.0, 100.0), tsit="SPLASHED"))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.AX_DONE, st.phase)

    def test_a_lost_chaser_and_a_gone_drone_are_down(self):
        st = self._after()
        st, _ = mlib.ax_decide(st, snap(ut=430.0, vessel_lost=True, sibling_present=0))
        self.assertEqual(("LOST", "ABSENT"), (st.own_end, st.target_end))
        st, _ = mlib.ax_decide(st, snap(ut=460.0, vessel_lost=True, sibling_present=0))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)

    def test_a_drone_still_hanging_runs_out_the_bound_by_name(self):
        st = self._after()
        st, _ = mlib.ax_decide(st, snap(ut=430.0, vessel_lost=True, sibling_present=1,
                                        sibling_situation="FLYING"))
        st, _ = mlib.ax_decide(st, snap(ut=401.0 + params().aftermath_timeout_seconds + 1,
                                        vessel_lost=True, sibling_present=1,
                                        sibling_situation="FLYING"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("drone is still up", st.flake_reason)

    def test_a_replaced_active_vessel_gets_neutral_controls_not_a_dive(self):
        st = self._after()
        st, acts = mlib.ax_decide(st, fly(ut=405.0, name=DRONE, oparts=4,
                                          rel=(0.0, 0.0, 0.0)))
        self.assertEqual("REPLACED(%s)" % DRONE, st.own_end)
        pitch = of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)
        self.assertTrue(all(a.value == 0.0 for a in pitch))

    def test_a_done_state_is_inert(self):
        st = replace(self._after(), done=True)
        self.assertEqual((st, []), mlib.ax_decide(st, fly(ut=999.0)))


class WholeFlightTests(unittest.TestCase):
    def test_every_assertion_passes_on_a_complete_flight(self):
        p = params()
        st = HandOverTests()._to_climb(p)
        st, _ = mlib.ax_decide(st, fly(ut=190.0, lat=RWY_LAT, lon=-74.4, alt=310.0, vs=6.0))
        self.assertEqual(mlib.AX_ASCEND, st.phase)
        st = replace(st, loiter_index=1)
        lat, lon = p.loiter[1]
        for i in range(mlib.AX_RELEASE_FRAMES):
            st, _ = mlib.ax_decide(st, fly(ut=300.0 + 0.5 * i, lat=lat, lon=lon + 0.15,
                                           hdg=270.0, alt=1590.0, oparts=42))
        self.assertEqual(mlib.AX_EXTEND, st.phase)
        ut = 302.0
        for i in range(mlib.AX_REF_SETTLE_FRAMES):
            st, _ = mlib.ax_decide(st, fly(ut=ut, lat=lat, lon=lon + 0.15, hdg=270.0,
                                           rel=(-2.0 - i, 0.0, 3.0 * i)))
            ut += 0.5
        ut += p.extend_seconds
        st, _ = mlib.ax_decide(st, fly(ut=ut, lat=lat, lon=lon, hdg=270.0,
                                       rel=(-100.0, 0.0, 2000.0)))
        self.assertEqual(mlib.AX_PURSUE, st.phase)
        st, _ = mlib.ax_decide(st, fly(ut=ut + 20, lat=lat, lon=lon, hdg=90.0,
                                       rel=(-1.0, 0.0, 90.0)))
        st, _ = mlib.ax_decide(st, fly(ut=ut + 21, lat=lat, lon=lon, hdg=90.0,
                                       rel=(-1.0, 0.0, 20.0), oparts=33, tparts=1))
        self.assertEqual(mlib.AX_AFTERMATH, st.phase)
        st, _ = mlib.ax_decide(st, snap(ut=ut + 90, vessel_lost=True, sibling_present=0))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        outcomes = mlib.evaluate_air3_assertions([], p, st)
        self.assertEqual(["rolloutObserved", "stagedAtPrelaunch", "airborne",
                          "droneReleased", "midAirCollision", "bothDown"],
                         [o.name for o in outcomes])
        self.assertTrue(all(o.met for o in outcomes),
                        [(o.name, o.value) for o in outcomes if not o.met])

    def test_no_state_fails_every_assertion(self):
        self.assertFalse(any(o.met for o in mlib.evaluate_air3_assertions([], params(),
                                                                          None)))

    def test_a_pass_limit_flake_fails_the_collision_assertion(self):
        st = replace(pursuing(params(maxAttempts=1)), closing_speed=5.0)
        for i in range(mlib.AX_CLOSING_ABORT_FRAMES):
            st, _ = mlib.ax_decide(st, fly(ut=400.5 + 0.5 * i, rel=(0.0, 0.0, 1000.0 - i)))
        met = {o.name: o.met for o in mlib.evaluate_air3_assertions([], params(), st)}
        self.assertTrue(met["droneReleased"])
        self.assertFalse(met["midAirCollision"])
        self.assertFalse(met["bothDown"])


# ---- the runner's air-target channel -------------------------------------------

class _Parts(object):
    def __init__(self, n):
        self.all = [object()] * n


class _Sit(object):
    def __init__(self, name):
        self.name = name


class _Vessel(object):
    def __init__(self, name, parts, sit="flying", pos=(1.0, 2.0, 3.0)):
        self.name = name
        self.parts = _Parts(parts)
        self.situation = _Sit(sit)
        self.surface_reference_frame = "SRF:" + name
        self._pos = pos
        self.frames = []

    def position(self, frame):
        self.frames.append(frame)
        return self._pos


class _LostSpaceCenter(object):
    """The active vessel is gone; the drone is still in the vessel list."""

    def __init__(self, vessels):
        self.ut = 500.0
        self.vessels = vessels

    @property
    def active_vessel(self):
        raise RuntimeError("no active vessel")


class _Conn(object):
    def __init__(self, sc):
        self.space_center = sc


class RunnerAirTargetTests(unittest.TestCase):
    def test_the_channel_is_off_by_default(self):
        self.assertFalse(mission_runner.KrpcMissionControl()._read_air_target)
        self.assertTrue(mission_runner.KrpcMissionControl(
            read_air_target=True)._read_air_target)

    def test_the_read_uses_the_handle_the_sibling_sweep_matched(self):
        c = mission_runner.KrpcMissionControl(read_air_target=True)
        drone = _Vessel(DRONE, 4, pos=(-3.0, 10.0, 250.0))
        other = _Vessel("Other", 7)
        c._sibling_watch_name = DRONE
        self.assertEqual(("FLYING", 1),
                         c._read_sibling_situation(_LostSpaceCenter([other, drone])))
        own = _Vessel(CHASER, 38)
        rel, tparts, oparts = c._read_air_target_channel(own)
        self.assertEqual(((-3.0, 10.0, 250.0), 4, 38), (rel, tparts, oparts))
        self.assertEqual(["SRF:" + CHASER], drone.frames)
        self.assertEqual([], other.frames)

    def test_no_match_no_target_read(self):
        c = mission_runner.KrpcMissionControl(read_air_target=True)
        c._sibling_watch_name = DRONE
        self.assertEqual(("", 0), c._read_sibling_situation(
            _LostSpaceCenter([_Vessel("Other", 7)])))
        rel, tparts, oparts = c._read_air_target_channel(_Vessel(CHASER, 38))
        self.assertTrue(all(math.isnan(x) for x in rel))
        self.assertEqual((-1, 38), (tparts, oparts))

    def test_a_faulting_read_degrades_to_the_sentinels_once_warned(self):
        class _Bad(_Vessel):
            def position(self, frame):
                raise RuntimeError("stale handle")
        c = mission_runner.KrpcMissionControl(read_air_target=True)
        c._sibling_match = _Bad(DRONE, 4)
        lines = []
        old = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            rel, tparts, _ = c._read_air_target_channel(_Vessel(CHASER, 38))
            c._read_air_target_channel(_Vessel(CHASER, 38))
        finally:
            mission_runner._stdout_sink = old
        self.assertTrue(all(math.isnan(x) for x in rel))
        self.assertEqual(4, tparts)
        self.assertEqual(1, len([l for l in lines if "air-target" in l]))

    def _lost_snapshot(self, flag):
        c = mission_runner.KrpcMissionControl(read_air_target=flag)
        c._conn = _Conn(_LostSpaceCenter([_Vessel(DRONE, 4, sit="splashed")]))
        c._sibling_watch_name = DRONE
        old = mission_runner._stdout_sink
        mission_runner._stdout_sink = lambda line: None
        try:
            for _ in range(mission_runner.READ_FAIL_STREAK_LIMIT - 1):
                with self.assertRaises(Exception):
                    c.read_snapshot()
            return c.read_snapshot()
        finally:
            mission_runner._stdout_sink = old

    def test_the_vessel_lost_path_still_reads_the_drone(self):
        s = self._lost_snapshot(True)
        self.assertTrue(s.vessel_lost)
        self.assertEqual(("SPLASHED", 1, 4), (s.sibling_situation, s.sibling_present,
                                              s.air_target_part_count))
        self.assertTrue(all(math.isnan(x) for x in s.air_target_rel_position))

    def test_the_vessel_lost_path_is_unchanged_without_the_flag(self):
        s = self._lost_snapshot(False)
        self.assertTrue(s.vessel_lost)
        self.assertEqual(("", -1, -1), (s.sibling_situation, s.sibling_present,
                                        s.air_target_part_count))


# ---- the derivative craft ---------------------------------------------------------

def _builder():
    spec = importlib.util.spec_from_file_location("build_air_drone_carrier", BUILDER)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _fuel_the_tank(text):
    at = text.index("\tpart = fuelTank_")
    i = text.index("\t\tamount = 0\n", at)
    return text[:i] + "\t\tamount = 200\n" + text[i + len("\t\tamount = 0\n"):]


class CraftDriftTests(unittest.TestCase):
    def test_the_committed_craft_is_the_derivation(self):
        b = _builder()
        text = b.build()
        self.assertEqual([], b.verify(text))
        with open(b.OUT, encoding="utf-8", newline="") as fh:
            self.assertEqual(text, fh.read())

    def test_the_craft_is_launched_by_the_name_the_spec_declares(self):
        b = _builder()
        self.assertEqual(CHASER, b.SHIP_NAME)
        self.assertTrue(b.OUT.endswith(os.path.join("gs1-two-stage-pad", "Ships", "VAB",
                                                    CHASER + ".craft")))

    def test_verify_catches_a_broken_derivation(self):
        b = _builder()
        text = b.build()
        cases = {
            "unlinked decoupler": text.replace("\tlink = radialDecoupler1-2_4200700001\n",
                                               "", 1),
            "engines on stage 0": text.replace("\tistg = 1\n", "\tistg = 0\n"),
            "fuelled tank": _fuel_the_tank(text),
            "stock chute altitude": text.replace("\t\tdeployAltitude = %d\n"
                                                 % b.CHUTE_DEPLOY_ALTITUDE,
                                                 "\t\tdeployAltitude = 1000\n"),
        }
        for label, broken in cases.items():
            with self.subTest(label=label):
                self.assertNotEqual(text, broken)
                self.assertNotEqual([], b.verify(broken))

    def test_the_drone_hangs_below_the_wing_and_above_the_runway(self):
        b = _builder()
        parts = {key: pos for key, _name, pos, _rot in b.layout()}
        # Vertical is the craft frame's Z (the Mallard is stored nose along +Y);
        # the decoupler is under the right wing (x > 0) and the tank lies fore-aft.
        self.assertGreater(parts["decoupler"][0], 2.0)
        self.assertAlmostEqual(parts["tank"][0], parts["decoupler"][0], places=5)
        self.assertGreater(parts["decoupler"][1], parts["probe"][1])
        self.assertGreater(parts["probe"][1], parts["chute"][1])


# ---- schema / params / shell --------------------------------------------------------

def _params_read(fn_name):
    with open(os.path.join(_HERE, "mlib.py"), encoding="utf-8") as fh:
        tree = ast.parse(fh.read())
    fn = next(n for n in ast.walk(tree)
              if isinstance(n, ast.FunctionDef) and n.name == fn_name)
    return {node.args[0].value for node in ast.walk(fn)
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
            and node.func.attr == "get" and node.args
            and isinstance(node.args[0], ast.Constant)}


class SchemaSyncTests(unittest.TestCase):
    def test_every_air_3_param_is_declared_and_every_declared_one_is_read(self):
        with open(SCHEMA, "rb") as fh:
            declared = set(tomllib.load(fh)["params"])
        ax = _params_read("ax_params_from_dict")
        air = _params_read("air_params_from_dict")
        self.assertTrue(ax)
        self.assertEqual(set(), ax - declared)
        self.assertEqual(set(), declared - (ax | air))

    def test_no_air_3_function_names_an_autopilot_action(self):
        with open(os.path.join(_HERE, "mlib.py"), encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        fns = [n for n in ast.walk(tree) if isinstance(n, ast.FunctionDef)
               and (n.name.startswith(("ax_", "_ax_"))
                    or n.name == "evaluate_air3_assertions")]
        self.assertIn("ax_decide", [f.name for f in fns])
        named = {(f.name, node.id) for f in fns for node in ast.walk(f)
                 if isinstance(node, ast.Name) and node.id.startswith("ACTION_AP_")}
        self.assertEqual(set(), named)

    def test_shell_opts_into_the_three_reads(self):
        with open(SHELL, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        kws = {kw.arg: kw.value for node in ast.walk(tree) if isinstance(node, ast.Call)
               and getattr(node.func, "attr", "") == "KrpcMissionControl"
               for kw in node.keywords}
        for key in ("read_vessel_name", "read_aircraft", "read_air_target"):
            self.assertIs(True, kws[key].value, key)

    def test_shell_flies_in_real_time(self):
        import air3_drone_intercept as shell
        self.assertFalse(shell.SPEC.allow_rails_warp)
        self.assertEqual(0.0, shell.SPEC.max_physics_warp)
        self.assertEqual("air3_drone_intercept", shell.SPEC.name)
        st = shell.build_state({"craftName": CHASER})
        self.assertEqual(mlib.AX_AIR, st.phase)


if __name__ == "__main__":
    unittest.main()
