"""Unit cells for the AIR-1 lane (mission `air1_runway_circuit`, the first plane flight).

Guards the machine's ways of being silently wrong:
  1. staging before the rolled-out craft reads back as the active vessel, or on a dark
     aircraft channel (it would fly the runway blind);
  2. firing the stage click (the auto-record trigger) without brakes and zero throttle;
  3. a takeoff that never leaves the ground reading as anything but a named
     MISSION-FLAKE, with the throttle cut and the brakes on;
  4. a circuit that cannot reach a waypoint, a final with no touchdown, or a plane that
     will not stop, running out the budget instead of giving up by name;
  5. the bank sign: a wrong sign must flip once and steer the new way on the same frame;
  6. a schema / params / shell drift (every param the machine reads must be a key the
     schema declares, derived by an AST walk, not a hand-copied list).
Plus the pure guidance helpers the later collision lanes reuse (owner request
2026-11-16: two airplanes flying into each other), each checked on its own.

NO krpc, NO KSP, NO network.
"""

import ast
import math
import os
import sys
import tomllib
import unittest
from dataclasses import replace

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import mlib  # noqa: E402
import mission_runner  # noqa: E402

SCHEMA = os.path.join(_MISSIONS, "air1_runway_circuit.schema.toml")
SHELL = os.path.join(_MISSIONS, "air1_runway_circuit.py")

# The runway's west end, where a Runway rollout stands (heading 090).
RWY_LAT, RWY_LON = -0.0486, -74.72
METERS_PER_DEG = 2.0 * math.pi * mlib.KERBIN_RADIUS_M / 360.0


def params(**kw):
    base = {"craftName": "Mallard", "recordSettleFrames": 3, "rolloutFrames": 20}
    base.update(kw)
    return mlib.air_params_from_dict(base)


def snap(**kw):
    return mlib.TelemetrySnapshot(**kw)


def air(ut=200.0, lat=0.0, lon=-74.3, hdg=90.0, alt=500.0, spd=90.0, vs=0.0,
        sit="FLYING", **kw):
    return snap(ut=ut, situation=sit, vessel_name="Mallard", latitude=lat, longitude=lon,
                heading=hdg, altitude=alt, surface_speed=spd, vertical_speed=vs,
                roll=0.0, **kw)


def runway(ut=100.0, lat=RWY_LAT, lon=RWY_LON, spd=0.0, sit="PRE_LAUNCH"):
    return snap(ut=ut, situation=sit, vessel_name="Mallard", latitude=lat, longitude=lon,
                heading=90.0, altitude=1.0, surface_speed=spd, surface_pitch=1.5, roll=0.0)


def kinds(actions):
    return [a.kind for a in actions]


def of_kind(actions, kind):
    return [a for a in actions if a.kind == kind]


def run_to_arm(st):
    st, acts = mlib.air_decide(st, snap(vessel_lost=True))
    assert kinds(acts) == [mlib.ACTION_LAUNCH_VESSEL]
    st, _ = mlib.air_decide(st, runway())
    st, _ = mlib.air_decide(st, runway())
    assert st.phase == mlib.AIR_ARM, st.phase
    return st


def run_to_takeoff(st):
    st = run_to_arm(st)
    st, _ = mlib.air_decide(st, runway(ut=101.0))
    assert st.phase == mlib.AIR_RECORD_SETTLE
    for i in range(st.params.record_settle_frames):
        st, acts = mlib.air_decide(st, runway(ut=102.0 + i, sit="PRE_LAUNCH"))
    assert st.phase == mlib.AIR_TAKEOFF, st.phase
    return st


def climb_state(**kw):
    st = mlib.air_initial_state(params(**kw))
    return replace(st, phase=mlib.AIR_CLIMB, runway_heading=90.0, takeoff_ut=200.0,
                   phase_entry_ut=200.0, release_lat=RWY_LAT, release_lon=RWY_LON)


def circuit_state(leg=0, **kw):
    st = mlib.air_initial_state(params(**kw))
    return replace(st, phase=mlib.AIR_CIRCUIT, runway_heading=90.0, takeoff_ut=200.0,
                   leg_index=leg, leg_start_ut=300.0, phase_entry_ut=300.0,
                   phases_reached=(mlib.AIR_ROLLOUT, mlib.AIR_ARM, mlib.AIR_RECORD_SETTLE,
                                   mlib.AIR_TAKEOFF, mlib.AIR_CLIMB, mlib.AIR_CIRCUIT))


def final_state(**kw):
    st = mlib.air_initial_state(params(**kw))
    return replace(st, phase=mlib.AIR_FINAL, phase_entry_ut=600.0, runway_heading=90.0)


# --------------------------------------------------------------------------- geometry


class GeometryTests(unittest.TestCase):
    def test_wrap_heading(self):
        self.assertEqual(0.0, mlib.air_wrap_heading(360.0))
        self.assertAlmostEqual(350.0, mlib.air_wrap_heading(-10.0))
        self.assertAlmostEqual(10.0, mlib.air_wrap_heading(730.0))

    def test_heading_error_is_the_short_way_round_and_signed(self):
        self.assertAlmostEqual(20.0, mlib.air_heading_error(80.0, 100.0))
        self.assertAlmostEqual(-20.0, mlib.air_heading_error(100.0, 80.0))
        self.assertAlmostEqual(20.0, mlib.air_heading_error(350.0, 10.0))
        self.assertAlmostEqual(-20.0, mlib.air_heading_error(10.0, 350.0))
        self.assertAlmostEqual(180.0, mlib.air_heading_error(0.0, 180.0))

    def test_ground_distance(self):
        self.assertAlmostEqual(METERS_PER_DEG,
                               mlib.air_ground_distance(0.0, 0.0, 0.0, 1.0), delta=0.01)
        self.assertAlmostEqual(0.0, mlib.air_ground_distance(1.0, 2.0, 1.0, 2.0))
        self.assertTrue(math.isnan(mlib.air_ground_distance(float("nan"), 0, 0, 0)))

    def test_bearing(self):
        self.assertAlmostEqual(90.0, mlib.air_bearing(0.0, 0.0, 0.0, 0.1), delta=1e-6)
        self.assertAlmostEqual(0.0, mlib.air_bearing(0.0, 0.0, 0.1, 0.0), delta=1e-6)
        self.assertAlmostEqual(270.0, mlib.air_bearing(0.0, 0.1, 0.0, 0.0), delta=1e-6)
        self.assertAlmostEqual(180.0, mlib.air_bearing(0.1, 0.0, 0.0, 0.0), delta=1e-6)
        self.assertTrue(math.isnan(mlib.air_bearing(0, 0, 0, float("nan"))))

    def test_parse_point(self):
        self.assertEqual((0.05, -73.85), mlib.air_parse_point(" 0.05 , -73.85 "))
        for bad in ("", "1", "1,2,3", "a,b", "91,0", "0,181", "nan,0"):
            self.assertIsNone(mlib.air_parse_point(bad), bad)

    def test_parse_points_is_all_or_nothing(self):
        self.assertEqual(((0.0, 1.0), (2.0, 3.0)), mlib.air_parse_points("0,1;2,3"))
        self.assertEqual((), mlib.air_parse_points(""))
        self.assertIsNone(mlib.air_parse_points("0,1;oops"))

    def test_default_circuit_stays_near_the_space_center(self):
        # Inside the Space Center's 25 km ghost cull range with room to spare, and
        # every leg longer than a 25-deg turn at cruise (radius ~1.8 km).
        p = params()
        pad = (-0.0972, -74.5577)
        for pt in p.waypoints + (p.touchdown,):
            self.assertLess(mlib.air_ground_distance(pad[0], pad[1], *pt), 12000.0, pt)
        legs = [(RWY_LAT, RWY_LON)] + list(p.waypoints) + [p.touchdown]
        for a, b in zip(legs, legs[1:]):
            self.assertGreater(mlib.air_ground_distance(*a, *b), 3000.0, (a, b))
        # The touchdown is EAST of the last waypoint, so the final is flown eastbound
        # toward open sea and an overshoot lands further out.
        self.assertGreater(p.touchdown[1], p.waypoints[-1][1])


# --------------------------------------------------------------------------- guidance


class GuidanceTests(unittest.TestCase):
    def test_bank_command_is_proportional_clamped_and_signed(self):
        self.assertAlmostEqual(10.0, mlib.air_bank_command(10.0, 25.0, 1.0))
        self.assertAlmostEqual(25.0, mlib.air_bank_command(90.0, 25.0, 1.0))
        self.assertAlmostEqual(-25.0, mlib.air_bank_command(-90.0, 25.0, 1.0))
        self.assertAlmostEqual(-25.0, mlib.air_bank_command(90.0, 25.0, -1.0))
        self.assertEqual(0.0, mlib.air_bank_command(float("nan"), 25.0, 1.0))

    def test_heading_command_steps_at_most_the_lead(self):
        self.assertAlmostEqual(110.0, mlib.air_heading_command(90.0, 180.0))
        self.assertAlmostEqual(70.0, mlib.air_heading_command(90.0, 0.0))
        self.assertAlmostEqual(95.0, mlib.air_heading_command(90.0, 95.0))
        self.assertAlmostEqual(355.0, mlib.air_heading_command(5.0, 300.0, lead=10.0))
        self.assertAlmostEqual(180.0, mlib.air_heading_command(float("nan"), 180.0))
        self.assertAlmostEqual(90.0, mlib.air_heading_command(90.0, float("nan")))

    def test_vs_target(self):
        self.assertAlmostEqual(0.0, mlib.air_vs_target(1000.0, 1000.0))
        self.assertAlmostEqual(mlib.AIR_MAX_CLIMB_RATE, mlib.air_vs_target(1000.0, 0.0))
        self.assertAlmostEqual(-mlib.AIR_MAX_SINK, mlib.air_vs_target(0.0, 1000.0))
        self.assertAlmostEqual(-3.0, mlib.air_vs_target(500.0, 500.0, feed_forward=-3.0))
        self.assertEqual(0.0, mlib.air_vs_target(500.0, float("nan")))

    def test_pitch_command(self):
        # Level, on target: the AoA trim alone.
        self.assertAlmostEqual(mlib.AIR_AOA_TRIM_DEG, mlib.air_pitch_command(0.0, 0.0, 90.0))
        # A climb target raises the nose by the flight-path angle.
        self.assertGreater(mlib.air_pitch_command(5.0, 5.0, 90.0), mlib.AIR_AOA_TRIM_DEG)
        # Clamped both ways.
        self.assertEqual(mlib.AIR_MAX_PITCH, mlib.air_pitch_command(50.0, -50.0, 60.0))
        self.assertEqual(mlib.AIR_MIN_PITCH, mlib.air_pitch_command(-50.0, 50.0, 60.0))
        self.assertAlmostEqual(mlib.AIR_AOA_TRIM_DEG,
                               mlib.air_pitch_command(float("nan"), float("nan"), float("nan")))

    def test_throttle_command(self):
        self.assertAlmostEqual(mlib.AIR_THROTTLE_BASE, mlib.air_throttle_command(90.0, 90.0, 0.1))
        self.assertEqual(1.0, mlib.air_throttle_command(90.0, 40.0, 0.1))
        self.assertEqual(0.1, mlib.air_throttle_command(60.0, 120.0, 0.1))
        self.assertAlmostEqual(mlib.AIR_THROTTLE_BASE,
                               mlib.air_throttle_command(90.0, float("nan"), 0.1))

    def test_glide_altitude(self):
        self.assertAlmostEqual(1000.0 * math.tan(math.radians(6.0)),
                               mlib.air_glide_altitude(1000.0, 6.0, 5000.0))
        self.assertEqual(800.0, mlib.air_glide_altitude(100000.0, 6.0, 800.0))
        self.assertEqual(0.0, mlib.air_glide_altitude(-5.0, 6.0, 800.0))
        self.assertEqual(800.0, mlib.air_glide_altitude(float("nan"), 6.0, 800.0))

    def test_steer_toward_a_point_dead_ahead_flies_wings_level(self):
        s = air(lat=0.0, lon=-74.3, hdg=90.0)
        st = mlib.air_steer_toward(s, 0.0, -74.0, 500.0, 90.0, 25.0, 1.0)
        self.assertAlmostEqual(0.0, st.heading_err, delta=0.01)
        self.assertAlmostEqual(0.0, st.roll, delta=0.01)
        self.assertAlmostEqual(90.0, st.heading, delta=0.01)
        self.assertAlmostEqual(0.3 * METERS_PER_DEG, st.distance, delta=1.0)

    def test_steer_toward_a_point_to_the_left_banks_left(self):
        s = air(lat=0.0, lon=-74.3, hdg=90.0)
        st = mlib.air_steer_toward(s, 0.3, -74.3, 500.0, 90.0, 25.0, 1.0)
        self.assertAlmostEqual(-90.0, st.heading_err, delta=0.01)
        self.assertEqual(-25.0, st.roll)
        self.assertAlmostEqual(70.0, st.heading, delta=0.01)
        # A negative bank sign mirrors the roll, never the heading.
        st2 = mlib.air_steer_toward(s, 0.3, -74.3, 500.0, 90.0, 25.0, -1.0)
        self.assertEqual(25.0, st2.roll)
        self.assertAlmostEqual(st.heading, st2.heading)

    def test_steer_toward_follows_a_moving_point(self):
        # The collision-lane use: the same call with the other vessel's position each
        # frame. Pure, so two frames with two points give two answers.
        s = air(lat=0.0, lon=-74.3, hdg=90.0)
        a = mlib.air_steer_toward(s, 0.05, -74.0, 500.0, 90.0, 25.0, 1.0)
        b = mlib.air_steer_toward(s, -0.05, -74.0, 500.0, 90.0, 25.0, 1.0)
        self.assertLess(a.heading_err, 0.0)
        self.assertGreater(b.heading_err, 0.0)

    def test_low_altitude_limits_the_bank(self):
        s = air(alt=100.0)
        st = mlib.air_steer_toward(s, 0.3, -74.3, 500.0, 90.0, 25.0, 1.0)
        self.assertEqual(-mlib.AIR_LOW_MAX_BANK, st.roll)

    def test_stall_guard_caps_the_pitch(self):
        s = air(alt=500.0, spd=40.0, vs=-5.0)
        st = mlib.air_steer_toward(s, 0.0, -74.0, 1000.0, 90.0, 25.0, 1.0)
        self.assertLessEqual(st.pitch, mlib.AIR_AOA_TRIM_DEG)

    def test_dark_channel_steers_wings_level(self):
        s = snap(ut=1.0, altitude=500.0)
        st = mlib.air_steer_toward(s, 0.0, -74.0, 500.0, 90.0, 25.0, 1.0)
        self.assertEqual(0.0, st.roll)
        self.assertTrue(math.isnan(st.distance))

    def test_waypoint_capture(self):
        self.assertTrue(mlib.air_waypoint_captured(700.0, 5.0, 800.0))
        self.assertFalse(mlib.air_waypoint_captured(1500.0, 5.0, 800.0))
        # Flown abeam: behind the wing inside three radii.
        self.assertTrue(mlib.air_waypoint_captured(2000.0, 120.0, 800.0))
        self.assertFalse(mlib.air_waypoint_captured(3000.0, 120.0, 800.0))
        self.assertFalse(mlib.air_waypoint_captured(2000.0, 90.0, 800.0))
        self.assertFalse(mlib.air_waypoint_captured(float("nan"), 0.0, 800.0))


# --------------------------------------------------------------------------- params


class ParamsTests(unittest.TestCase):
    def test_defaults(self):
        p = mlib.air_params_from_dict({"craftName": "Mallard"})
        self.assertEqual("Mallard", p.expected_vessel_name)
        self.assertEqual("Runway", p.launch_site)
        self.assertEqual(3, len(p.waypoints))
        self.assertEqual((0.05, -73.85), p.touchdown)
        self.assertIsNone(mlib.air_params_problem(p))

    def test_problems_are_named(self):
        cases = [
            ({"craftName": ""}, "craftName"),
            ({"craftName": "M", "waypoints": "0,1;x"}, "waypoints"),
            ({"craftName": "M", "touchdownPoint": "east"}, "touchdownPoint"),
            ({"craftName": "M", "glideSlopeDeg": 30.0}, "glideSlopeDeg"),
            ({"craftName": "M", "flareAltitude": 2000.0}, "flareAltitude"),
            ({"craftName": "M", "turnAltitude": 30.0}, "turnAltitude"),
            ({"craftName": "M", "turnAltitude": 1500.0}, "turnAltitude"),
        ]
        for d, word in cases:
            with self.subTest(d=d):
                self.assertIn(word, mlib.air_params_problem(mlib.air_params_from_dict(d)))

    def test_bank_sign_is_normalized_to_one(self):
        self.assertEqual(-1.0, mlib.air_initial_state(params(bankSign=-0.5)).bank_sign)
        self.assertEqual(1.0, mlib.air_initial_state(params(bankSign=0.0)).bank_sign)


# --------------------------------------------------------------------------- rollout / arm


class RolloutTests(unittest.TestCase):
    def test_first_frame_launches_the_craft_on_the_runway(self):
        st = mlib.air_initial_state(params())
        st, acts = mlib.air_decide(st, snap(vessel_lost=True))
        self.assertEqual([mlib.ACTION_LAUNCH_VESSEL], kinds(acts))
        self.assertEqual("Mallard", acts[0].text)
        self.assertEqual("Runway", acts[0].launch_site)

    def test_bad_params_flake_before_launching(self):
        st = mlib.air_initial_state(params(waypoints="nope"))
        st, acts = mlib.air_decide(st, snap())
        self.assertEqual([], acts)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("waypoints", st.flake_reason)

    def test_settles_on_two_frames_of_the_named_craft_at_prelaunch(self):
        st = mlib.air_initial_state(params())
        st, _ = mlib.air_decide(st, snap(vessel_lost=True))
        for s in (snap(vessel_lost=True), runway(sit="LANDED"),
                  replace(runway(), vessel_name="GS1 Auto-Chute Booster")):
            st, acts = mlib.air_decide(st, s)
            self.assertEqual(mlib.AIR_ROLLOUT, st.phase)
            self.assertEqual([], acts)
        st, _ = mlib.air_decide(st, runway())
        self.assertEqual(mlib.AIR_ROLLOUT, st.phase)
        st, _ = mlib.air_decide(st, runway())
        self.assertEqual(mlib.AIR_ARM, st.phase)
        self.assertTrue(st.rollout_ready_observed)

    def test_rollout_gives_up_by_name(self):
        st = mlib.air_initial_state(params(rolloutFrames=3))
        st, _ = mlib.air_decide(st, snap(vessel_lost=True))
        for _ in range(4):
            st, _ = mlib.air_decide(st, snap(vessel_lost=True))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("never read back", st.flake_reason)


class ArmTests(unittest.TestCase):
    def test_a_dark_aircraft_channel_never_stages_and_gives_up_on_the_ground(self):
        st = run_to_arm(mlib.air_initial_state(params()))
        dark = snap(ut=101.0, situation="PRE_LAUNCH", vessel_name="Mallard")
        for _ in range(mlib.AIR_CHANNEL_FRAMES):
            st, acts = mlib.air_decide(st, dark)
            self.assertEqual([], acts)
            self.assertIsNone(st.verdict)
        st, acts = mlib.air_decide(st, dark)
        self.assertEqual([], acts)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("read_aircraft", st.flake_reason)

    def test_the_stage_click_comes_after_brakes_and_zero_throttle(self):
        st = run_to_arm(mlib.air_initial_state(params()))
        st, acts = mlib.air_decide(st, runway(ut=101.0))
        self.assertEqual([mlib.ACTION_SET_BRAKES, mlib.ACTION_SET_THROTTLE,
                          mlib.ACTION_ACTIVATE_STAGE], kinds(acts))
        self.assertEqual(1.0, acts[0].value)
        self.assertEqual(0.0, acts[1].value)
        self.assertEqual(mlib.AIR_RECORD_SETTLE, st.phase)
        self.assertEqual("PRE_LAUNCH", st.stage_situation)
        self.assertEqual(90.0, st.runway_heading)
        self.assertEqual(1.5, st.ground_pitch)

    def test_vessel_loss_after_rollout_is_an_assert_fail(self):
        st = run_to_arm(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, snap(vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("ARM", st.loss_reason)


# --------------------------------------------------------------------------- takeoff


class TakeoffTests(unittest.TestCase):
    def test_brake_release_at_full_throttle_on_the_runway_heading(self):
        st = run_to_arm(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, runway(ut=101.0))
        acts = []
        for i in range(st.params.record_settle_frames):
            self.assertEqual(mlib.AIR_RECORD_SETTLE, st.phase)
            st, acts = mlib.air_decide(st, runway(ut=102.0 + i))
        self.assertEqual(mlib.AIR_TAKEOFF, st.phase)
        self.assertEqual(mlib.ACTION_SET_BRAKES, acts[0].kind)
        self.assertEqual(0.0, acts[0].value)
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)
        self.assertEqual(1, len(ap))
        self.assertEqual((1.5, 90.0), ap[0].pitch_heading)
        self.assertEqual(0.0, ap[0].value)
        self.assertEqual([1.0], [a.value for a in of_kind(acts, mlib.ACTION_SET_THROTTLE)])
        self.assertEqual(RWY_LON, st.release_lon)

    def test_rotates_once_at_the_rotate_speed(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=55.0, rotatePitch=8.0)))
        st, acts = mlib.air_decide(st, runway(ut=120.0, spd=40.0, sit="LANDED"))
        self.assertEqual([], acts)
        st, acts = mlib.air_decide(st, runway(ut=121.0, spd=56.0, sit="LANDED"))
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)
        self.assertEqual((8.0, 90.0), ap[0].pitch_heading)
        self.assertTrue(st.rotated)
        st, acts = mlib.air_decide(st, runway(ut=122.0, spd=58.0, sit="LANDED"))
        self.assertEqual([], acts)

    def test_airborne_enters_the_climb_with_evidence(self):
        st = run_to_takeoff(mlib.air_initial_state(params(climbPitch=10.0)))
        st, acts = mlib.air_decide(st, snap(ut=130.0, situation="FLYING", latitude=RWY_LAT,
                                            longitude=RWY_LON + 0.08, heading=90.0,
                                            altitude=12.0, surface_speed=62.0))
        self.assertEqual(mlib.AIR_CLIMB, st.phase)
        self.assertEqual(130.0, st.takeoff_ut)
        self.assertAlmostEqual(0.08 * METERS_PER_DEG, st.takeoff_distance, delta=5.0)
        self.assertEqual((10.0, 90.0),
                         of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)[0].pitch_heading)

    def test_a_hop_below_the_airborne_altitude_is_not_a_takeoff(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, snap(ut=130.0, situation="FLYING", latitude=RWY_LAT,
                                         longitude=RWY_LON + 0.05, heading=90.0,
                                         altitude=3.0, surface_speed=50.0))
        self.assertEqual(mlib.AIR_TAKEOFF, st.phase)

    def test_not_airborne_by_the_distance_limit_aborts_by_name(self):
        st = run_to_takeoff(mlib.air_initial_state(params(takeoffMaxDistance=1500.0)))
        far = RWY_LON + 1600.0 / METERS_PER_DEG
        st, acts = mlib.air_decide(st, runway(ut=130.0, lon=far, spd=45.0, sit="LANDED"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.AIR_TAKEOFF, st.flake_phase)
        self.assertIn("not airborne", st.flake_reason)
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_SET_BRAKES], kinds(acts))
        self.assertEqual([0.0, 1.0], [a.value for a in acts])

    def test_not_airborne_by_the_time_limit_aborts_by_name(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        t = st.release_ut + mlib.AIR_TAKEOFF_TIMEOUT_SECONDS + 1.0
        st, acts = mlib.air_decide(st, runway(ut=t, lon=RWY_LON + 0.01, spd=5.0, sit="LANDED"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("not airborne", st.flake_reason)


# --------------------------------------------------------------------------- climb


class ClimbTests(unittest.TestCase):
    def test_gear_comes_up_once(self):
        st = climb_state()
        st, acts = mlib.air_decide(st, air(ut=210.0, alt=40.0, lon=-74.6))
        self.assertEqual([], of_kind(acts, mlib.ACTION_SET_GEAR))
        st, acts = mlib.air_decide(st, air(ut=211.0, alt=70.0, lon=-74.6))
        self.assertEqual([0.0], [a.value for a in of_kind(acts, mlib.ACTION_SET_GEAR)])
        st, acts = mlib.air_decide(st, air(ut=212.0, alt=80.0, lon=-74.6))
        self.assertEqual([], of_kind(acts, mlib.ACTION_SET_GEAR))

    def test_holds_the_climb_pitch_on_the_runway_heading(self):
        st = climb_state(climbPitch=10.0)
        st, acts = mlib.air_decide(st, air(ut=210.0, alt=100.0, hdg=93.0, spd=80.0))
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)[0]
        self.assertEqual((10.0, 90.0), ap.pitch_heading)
        self.assertEqual(0.0, ap.value)

    def test_stall_guard_lowers_the_climb_pitch(self):
        st = climb_state(climbPitch=12.0)
        st, acts = mlib.air_decide(st, air(ut=210.0, alt=100.0, spd=40.0))
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)[0]
        self.assertEqual(mlib.AIR_AOA_TRIM_DEG, ap.pitch_heading[0])

    def test_turn_altitude_starts_the_circuit(self):
        st = climb_state(turnAltitude=300.0)
        st, _ = mlib.air_decide(st, air(ut=240.0, alt=305.0))
        self.assertEqual(mlib.AIR_CIRCUIT, st.phase)
        self.assertEqual(0, st.leg_index)
        self.assertEqual(240.0, st.leg_start_ut)

    def test_no_waypoints_goes_straight_to_the_final(self):
        st = climb_state(waypoints="")
        st, _ = mlib.air_decide(st, air(ut=240.0, alt=305.0))
        self.assertEqual(mlib.AIR_FINAL, st.phase)

    def test_surface_contact_in_the_climb_is_a_flake(self):
        st = climb_state()
        st, _ = mlib.air_decide(st, air(ut=220.0, alt=0.5, sit="SPLASHED"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("touched the surface", st.flake_reason)

    def test_climb_timeout(self):
        st = climb_state()
        st, _ = mlib.air_decide(st, air(ut=200.0 + mlib.AIR_CLIMB_TIMEOUT_SECONDS + 1,
                                        alt=150.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("after takeoff", st.flake_reason)

    def test_max_altitude_is_tracked(self):
        st = climb_state()
        st, _ = mlib.air_decide(st, air(ut=210.0, alt=150.0))
        st, _ = mlib.air_decide(st, air(ut=211.0, alt=120.0))
        self.assertEqual(150.0, st.max_altitude)


# --------------------------------------------------------------------------- circuit


class CircuitTests(unittest.TestCase):
    def test_steers_toward_the_current_waypoint_and_climbs_to_cruise(self):
        st = circuit_state()
        wp = st.params.waypoints[0]
        s = air(ut=310.0, lat=-0.05, lon=-74.4, hdg=90.0, alt=400.0)
        st, acts = mlib.air_decide(st, s)
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)[0]
        expect = mlib.air_steer_toward(s, wp[0], wp[1], 1000.0, 90.0, 25.0, 1.0)
        self.assertAlmostEqual(expect.pitch, ap.pitch_heading[0])
        self.assertAlmostEqual(expect.heading, ap.pitch_heading[1])
        self.assertAlmostEqual(expect.roll, ap.value)
        self.assertGreater(ap.pitch_heading[0], mlib.AIR_AOA_TRIM_DEG)

    def test_dead_band_suppresses_a_repeat_command(self):
        st = circuit_state()
        s = air(ut=310.0, lat=-0.05, lon=-74.4, hdg=90.0, alt=1000.0)
        st, acts = mlib.air_decide(st, s)
        self.assertTrue(of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL))
        st, acts = mlib.air_decide(st, replace(s, ut=310.5))
        self.assertEqual([], acts)

    def test_capture_advances_the_leg_and_the_last_enters_the_final(self):
        st = circuit_state()
        wps = st.params.waypoints
        for i, (lat, lon) in enumerate(wps):
            st, acts = mlib.air_decide(st, air(ut=400.0 + i, lat=lat, lon=lon + 0.001))
            self.assertEqual([], acts)
        self.assertEqual(mlib.AIR_FINAL, st.phase)
        self.assertEqual(len(wps), len(st.leg_capture_uts))
        self.assertEqual(402.0, st.phase_entry_ut)

    def test_leg_timeout_gives_up_by_name(self):
        st = circuit_state(legTimeoutSeconds=120.0)
        st, acts = mlib.air_decide(st, air(ut=300.0 + 121.0, lat=0.5, lon=-75.5))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.AIR_CIRCUIT, st.flake_phase)
        self.assertIn("waypoint 0", st.flake_reason)

    def test_surface_contact_in_the_circuit_is_a_flake(self):
        st = circuit_state()
        st, _ = mlib.air_decide(st, air(ut=320.0, alt=0.0, sit="LANDED"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)

    def test_a_flip_steers_the_new_way_on_the_same_frame(self):
        # Waypoint 0 due east of a plane heading 150: a 60 deg left turn, inside the
        # check window.
        st = circuit_state()
        wp_lat, wp_lon = st.params.waypoints[0]
        here_lat, here_lon = wp_lat, wp_lon - 0.2
        s0 = air(ut=310.0, lat=here_lat, lon=here_lon, hdg=150.0, alt=1000.0)
        st, _ = mlib.air_decide(st, s0)
        self.assertIsNotNone(st.turn_check_start_err)
        first = st.turn_check_start_err
        # The error GROWS for the whole window (the plane turns the wrong way).
        frames = mlib.AIR_TURN_CHECK_FRAMES
        acts = []
        for k in range(1, frames):
            grow = (mlib.AIR_TURN_CHECK_GROWTH + 2.0) * k / (frames - 1)
            st, acts = mlib.air_decide(st, replace(s0, ut=310.0 + k,
                                                   heading=150.0 + grow))
        self.assertTrue(st.bank_sign_flipped)
        self.assertEqual(-1.0, st.bank_sign)
        self.assertGreater(first, 0.0)
        # On the flip frame the commanded roll is already the mirrored one.
        last = replace(s0, ut=310.0 + frames - 1,
                       heading=150.0 + mlib.AIR_TURN_CHECK_GROWTH + 2.0)
        expect = mlib.air_steer_toward(last, wp_lat, wp_lon, 1000.0, 90.0, 25.0, -1.0)
        self.assertGreater(expect.roll, 0.0)
        self.assertAlmostEqual(expect.roll, st.cmd_roll)


class TurnCheckTests(unittest.TestCase):
    def setUp(self):
        self.st = mlib.air_initial_state(params())

    def _window(self, st, errs, bank=20.0):
        for e in errs:
            st = mlib.air_turn_check(st, e, bank)
        return st

    def test_no_window_for_a_small_error_or_no_bank(self):
        st = mlib.air_turn_check(self.st, 10.0, 20.0)
        self.assertIsNone(st.turn_check_start_err)
        st = mlib.air_turn_check(self.st, 60.0, 2.0)
        self.assertIsNone(st.turn_check_start_err)

    def test_no_window_opens_near_the_wrap(self):
        st = mlib.air_turn_check(self.st, 170.0, 20.0)
        self.assertIsNone(st.turn_check_start_err)

    def test_growth_flips_once(self):
        n = mlib.AIR_TURN_CHECK_FRAMES
        st = self._window(self.st, [60.0 + 15.0 * i / (n - 1) for i in range(n)])
        self.assertTrue(st.bank_sign_flipped)
        self.assertEqual(-1.0, st.bank_sign)
        st2 = self._window(st, [60.0 + 2 * i for i in range(3 * n)])
        self.assertEqual(-1.0, st2.bank_sign)

    def test_shrink_confirms(self):
        n = mlib.AIR_TURN_CHECK_FRAMES
        st = self._window(self.st, [80.0 - 20.0 * i / (n - 1) for i in range(n)])
        self.assertTrue(st.bank_sign_confirmed)
        self.assertFalse(st.bank_sign_flipped)
        self.assertEqual(1.0, st.bank_sign)

    def test_neither_reopens_the_window(self):
        n = mlib.AIR_TURN_CHECK_FRAMES
        st = self._window(self.st, [60.0] * n)
        self.assertFalse(st.bank_sign_flipped or st.bank_sign_confirmed)
        self.assertEqual(1, st.turn_check_frames)
        self.assertEqual(60.0, st.turn_check_start_err)

    def test_a_reopen_past_the_wrap_limit_closes_the_window(self):
        n = mlib.AIR_TURN_CHECK_FRAMES
        errs = [115.0] + [118.0] * (n - 2) + [121.0]
        st = self._window(self.st, errs)
        self.assertFalse(st.bank_sign_flipped or st.bank_sign_confirmed)
        self.assertIsNone(st.turn_check_start_err)


# --------------------------------------------------------------------------- final


class FinalTests(unittest.TestCase):
    TD = (0.05, -73.85)

    def _at(self, dist_m, hdg=90.0, alt=600.0, spd=70.0, ut=620.0, vs=-5.0):
        # A point dist_m west of the touchdown on its latitude.
        lon = self.TD[1] - dist_m / METERS_PER_DEG
        return air(ut=ut, lat=self.TD[0], lon=lon, hdg=hdg, alt=alt, spd=spd, vs=vs)

    def test_unaligned_holds_the_cruise_altitude_and_speed(self):
        st = final_state()
        s = self._at(6000.0, hdg=270.0, alt=1000.0, spd=90.0, vs=0.0)
        st, acts = mlib.air_decide(st, s)
        self.assertFalse(st.final_aligned)
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)[0]
        self.assertNotEqual(0.0, ap.value)
        self.assertEqual([], of_kind(acts, mlib.ACTION_SET_BRAKES))

    def test_aligned_glides_down_the_slope(self):
        st = final_state()
        s = self._at(3000.0, alt=600.0, spd=60.0, vs=0.0)
        st, acts = mlib.air_decide(st, s)
        self.assertTrue(st.final_aligned)
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)[0]
        # Glide altitude at 3 km on 6 deg is ~315 m: well below 600, so nose down.
        self.assertLess(ap.pitch_heading[0], mlib.AIR_AOA_TRIM_DEG)
        # Once aligned it stays aligned even if the nose wanders.
        st, _ = mlib.air_decide(st, self._at(2900.0, hdg=140.0, ut=621.0))
        self.assertTrue(st.final_aligned)

    def test_airbrakes_on_when_fast_and_off_when_slowed(self):
        st = final_state(approachSpeed=60.0)
        st, acts = mlib.air_decide(st, self._at(3000.0, spd=90.0))
        self.assertEqual([1.0], [a.value for a in of_kind(acts, mlib.ACTION_SET_BRAKES)])
        st, acts = mlib.air_decide(st, self._at(2800.0, spd=88.0, ut=621.0))
        self.assertEqual([], of_kind(acts, mlib.ACTION_SET_BRAKES))
        st, acts = mlib.air_decide(st, self._at(2000.0, spd=62.0, ut=630.0))
        self.assertEqual([0.0], [a.value for a in of_kind(acts, mlib.ACTION_SET_BRAKES)])

    def test_flare_is_wings_level_throttle_zero_on_the_current_heading(self):
        st = replace(final_state(flareAltitude=25.0), final_aligned=True)
        st, acts = mlib.air_decide(st, self._at(200.0, hdg=95.0, alt=20.0, spd=58.0, vs=-4.0))
        self.assertEqual(620.0, st.flare_ut)
        ap = of_kind(acts, mlib.ACTION_AP_SET_PITCH_HEADING_ROLL)[0]
        self.assertEqual(0.0, ap.value)
        self.assertAlmostEqual(95.0, ap.pitch_heading[1])
        self.assertEqual([0.0], [a.value for a in of_kind(acts, mlib.ACTION_SET_THROTTLE)])
        # Still flaring above the flare altitude once started (a float, not a go-around).
        st, acts = mlib.air_decide(st, self._at(150.0, hdg=95.0, alt=30.0, ut=621.0))
        self.assertEqual(620.0, st.flare_ut)

    def test_no_flare_before_alignment(self):
        st = final_state(flareAltitude=25.0)
        st, _ = mlib.air_decide(st, self._at(4000.0, hdg=270.0, alt=20.0))
        self.assertIsNone(st.flare_ut)

    def test_touchdown_cuts_power_brakes_and_releases_the_autopilot(self):
        st = replace(final_state(), final_aligned=True, flare_ut=650.0)
        s = self._at(300.0, alt=0.5, spd=45.0, vs=-1.2, ut=655.0)
        s = replace(s, situation="SPLASHED")
        st, acts = mlib.air_decide(st, s)
        self.assertEqual(mlib.AIR_STOP, st.phase)
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_SET_BRAKES,
                          mlib.ACTION_AP_DISENGAGE], kinds(acts))
        self.assertEqual("SPLASHED", st.touchdown_situation)
        self.assertAlmostEqual(300.0, st.touchdown_miss, delta=1.0)
        self.assertEqual(-1.2, st.touchdown_vs)

    def test_a_landed_touchdown_is_accepted_too(self):
        st = final_state()
        st, _ = mlib.air_decide(st, replace(self._at(100.0, alt=0.2), situation="LANDED"))
        self.assertEqual("LANDED", st.touchdown_situation)

    def test_final_timeout(self):
        st = final_state()
        st, _ = mlib.air_decide(st, self._at(5000.0, ut=600.0 + mlib.AIR_FINAL_TIMEOUT_SECONDS
                                              + 1.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("no touchdown", st.flake_reason)


# --------------------------------------------------------------------------- stop


class StopTests(unittest.TestCase):
    def _stop(self, **kw):
        st = mlib.air_initial_state(params(**kw))
        return replace(st, phase=mlib.AIR_STOP, touchdown_ut=700.0, phase_entry_ut=700.0,
                       touchdown_situation="SPLASHED")

    def test_rest_after_the_debounce(self):
        st = self._stop()
        for i in range(mlib.AIR_STOP_DEBOUNCE_FRAMES - 1):
            st, _ = mlib.air_decide(st, air(ut=710.0 + i, sit="SPLASHED", spd=0.4, alt=0.0))
            self.assertFalse(st.done)
        st, _ = mlib.air_decide(st, air(ut=720.0, sit="SPLASHED", spd=0.4, alt=0.0))
        self.assertTrue(st.done)
        self.assertEqual(mlib.AIR_DONE, st.phase)
        self.assertIsNone(st.verdict)
        self.assertEqual(720.0, st.rest_ut)

    def test_movement_resets_the_streak(self):
        st = self._stop()
        for i in range(mlib.AIR_STOP_DEBOUNCE_FRAMES - 1):
            st, _ = mlib.air_decide(st, air(ut=710.0 + i, sit="SPLASHED", spd=0.4))
        st, _ = mlib.air_decide(st, air(ut=716.0, sit="SPLASHED", spd=3.0))
        self.assertEqual(0, st.stop_streak)
        self.assertFalse(st.done)

    def test_a_dark_speed_never_reads_as_rest(self):
        st = self._stop()
        for i in range(2 * mlib.AIR_STOP_DEBOUNCE_FRAMES):
            st, _ = mlib.air_decide(st, snap(ut=710.0 + i, situation="SPLASHED"))
        self.assertFalse(st.done)

    def test_a_bounce_back_into_the_air_gives_up(self):
        st = self._stop()
        for i in range(mlib.AIR_BOUNCE_FRAMES):
            st, _ = mlib.air_decide(st, air(ut=710.0 + i, sit="FLYING", spd=50.0, alt=15.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("airborne again", st.flake_reason)

    def test_stop_timeout(self):
        st = self._stop(stopTimeoutSeconds=60.0)
        st, _ = mlib.air_decide(st, air(ut=761.0, sit="SPLASHED", spd=4.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("still moving", st.flake_reason)

    def test_a_done_state_is_inert(self):
        st = replace(self._stop(), done=True)
        st2, acts = mlib.air_decide(st, air())
        self.assertIs(st, st2)
        self.assertEqual([], acts)


# --------------------------------------------------------------------------- whole flight


class WholeFlightTests(unittest.TestCase):
    """A scripted flight through every phase. The script is not a flight model: it
    places the plane where each gate looks, so the cell proves the phases chain and
    the assertions read what the machine carried."""

    def test_every_assertion_passes_on_a_complete_flight(self):
        p = params(recordSettleFrames=2)
        st = run_to_takeoff(mlib.air_initial_state(p))
        ut = 120.0
        st, _ = mlib.air_decide(st, runway(ut=ut, lon=RWY_LON + 0.03, spd=65.0, sit="LANDED"))
        ut += 1
        st, _ = mlib.air_decide(st, air(ut=ut, lat=RWY_LAT, lon=RWY_LON + 0.08, alt=15.0,
                                        spd=66.0))
        self.assertEqual(mlib.AIR_CLIMB, st.phase)
        ut += 30
        st, _ = mlib.air_decide(st, air(ut=ut, lat=RWY_LAT, lon=-74.3, alt=310.0))
        self.assertEqual(mlib.AIR_CIRCUIT, st.phase)
        for lat, lon in p.waypoints:
            ut += 60
            st, _ = mlib.air_decide(st, air(ut=ut, lat=lat, lon=lon, alt=1000.0))
        self.assertEqual(mlib.AIR_FINAL, st.phase)
        td = p.touchdown
        ut += 60
        st, _ = mlib.air_decide(st, air(ut=ut, lat=td[0], lon=td[1] - 0.03, alt=300.0,
                                        spd=62.0))
        ut += 30
        st, _ = mlib.air_decide(st, replace(air(ut=ut, lat=td[0], lon=td[1] - 0.002,
                                                alt=0.3, spd=40.0, vs=-1.0),
                                            situation="SPLASHED"))
        self.assertEqual(mlib.AIR_STOP, st.phase)
        for i in range(mlib.AIR_STOP_DEBOUNCE_FRAMES):
            ut += 1
            st, _ = mlib.air_decide(st, air(ut=ut, lat=td[0], lon=td[1], sit="SPLASHED",
                                            spd=0.3, alt=0.0))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        outcomes = mlib.evaluate_air_assertions([], p, st)
        self.assertEqual(
            ["rolloutObserved", "stagedAtPrelaunch", "airborne", "cruiseAltitudeReached",
             "circuitFlown", "touchedDown", "cameToRest"], [o.name for o in outcomes])
        self.assertTrue(all(o.met for o in outcomes),
                        [(o.name, o.value) for o in outcomes if not o.met])

    def test_no_state_fails_every_assertion(self):
        outcomes = mlib.evaluate_air_assertions([], params(), None)
        self.assertFalse(any(o.met for o in outcomes))

    def test_a_flake_in_the_takeoff_fails_the_flight_assertions(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, runway(ut=300.0, lon=RWY_LON + 0.01, spd=5.0,
                                           sit="LANDED"))
        outcomes = {o.name: o.met for o in mlib.evaluate_air_assertions([], params(), st)}
        self.assertTrue(outcomes["stagedAtPrelaunch"])
        self.assertFalse(outcomes["airborne"])
        self.assertFalse(outcomes["cameToRest"])


# --------------------------------------------------------------------------- runner seam


class _FakeAutoPilot(object):
    def __init__(self, threshold_raises=False):
        self.calls = []
        self.reference_frame = None
        self.target_roll = None
        self._threshold_raises = threshold_raises
        self._roll_threshold = 5.0

    def target_pitch_and_heading(self, pitch, heading):
        self.calls.append(("pitch_heading", pitch, heading))

    def engage(self):
        self.calls.append(("engage",))

    @property
    def roll_threshold(self):
        return self._roll_threshold

    @roll_threshold.setter
    def roll_threshold(self, value):
        if self._threshold_raises:
            raise AttributeError("no roll_threshold")
        self._roll_threshold = value


class _FakeVessel(object):
    def __init__(self, ap):
        self.auto_pilot = ap
        self.control = object()
        self.surface_reference_frame = "SRF"


class _FakeSpaceCenter(object):
    def __init__(self, vessel):
        self.active_vessel = vessel


class _FakeConn(object):
    def __init__(self, sc):
        self.space_center = sc


class RunnerRollActionTests(unittest.TestCase):
    def _perform(self, action, **kw):
        ap = _FakeAutoPilot(**kw)
        c = mission_runner.KrpcMissionControl()
        c._conn = _FakeConn(_FakeSpaceCenter(_FakeVessel(ap)))
        c.perform(action)
        return ap

    def test_pitch_heading_and_roll_in_the_surface_frame(self):
        ap = self._perform(mlib.Action(mlib.ACTION_AP_SET_PITCH_HEADING_ROLL, value=-20.0,
                                       pitch_heading=(5.0, 135.0)))
        self.assertEqual("SRF", ap.reference_frame)
        self.assertEqual(("pitch_heading", 5.0, 135.0), ap.calls[0])
        self.assertEqual(-20.0, ap.target_roll)
        self.assertEqual(180.0, ap.roll_threshold)
        self.assertEqual(("engage",), ap.calls[-1])

    def test_a_missing_roll_threshold_still_engages(self):
        ap = self._perform(mlib.Action(mlib.ACTION_AP_SET_PITCH_HEADING_ROLL, value=0.0,
                                       pitch_heading=(3.0, 90.0)), threshold_raises=True)
        self.assertEqual(("engage",), ap.calls[-1])
        self.assertEqual(5.0, ap.roll_threshold)

    def test_the_aircraft_channel_is_off_by_default(self):
        self.assertFalse(mission_runner.KrpcMissionControl()._read_aircraft)
        self.assertTrue(mission_runner.KrpcMissionControl(read_aircraft=True)._read_aircraft)


# --------------------------------------------------------------------------- schema / shell


class SchemaSyncTests(unittest.TestCase):
    def test_every_param_the_machine_reads_is_declared(self):
        with open(SCHEMA, "rb") as fh:
            declared = set(tomllib.load(fh)["params"])
        with open(os.path.join(_HERE, "mlib.py"), encoding="utf-8") as fh:
            src = fh.read()
        tree = ast.parse(src)
        fn = next(n for n in ast.walk(tree)
                  if isinstance(n, ast.FunctionDef) and n.name == "air_params_from_dict")
        read = set()
        for node in ast.walk(fn):
            if (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "get" and node.args
                    and isinstance(node.args[0], ast.Constant)):
                read.add(node.args[0].value)
        self.assertTrue(read)
        self.assertEqual(set(), read - declared)
        self.assertEqual(set(), declared - read)

    def test_shell_opts_into_the_vessel_name_and_aircraft_reads(self):
        with open(SHELL, encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        kws = {kw.arg: kw.value for node in ast.walk(tree) if isinstance(node, ast.Call)
               and getattr(node.func, "attr", "") == "KrpcMissionControl"
               for kw in node.keywords}
        self.assertIs(True, kws["read_vessel_name"].value)
        self.assertIs(True, kws["read_aircraft"].value)

    def test_shell_flies_in_real_time(self):
        import air1_runway_circuit as shell
        self.assertFalse(shell.SPEC.allow_rails_warp)
        self.assertEqual(0.0, shell.SPEC.max_physics_warp)
        self.assertEqual("air1_runway_circuit", shell.SPEC.name)


if __name__ == "__main__":
    unittest.main()
