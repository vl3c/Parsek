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
  5a. the ground roll (first flight 2026-11-16_1955): the AutoPilot stays OFF while the
     nose wheel steers toward the runway heading, a heading drift past the limit gives
     up by name with the throttle cut and the brakes on, and a one-frame bounce never
     reads as airborne;
  5b. the rotation (second flight 2026-11-16_2033 / _2034_a2): the elevator alone raises
     the nose from the rotate speed, the nose wheel keeps steering and the ground guard
     keeps measuring until airborne is sustained; a give-up confirms its throttle cut
     off the telemetry before it is named;
  5c. the flight (third flight 2026-11-16_2126 / _2128_a2: the AutoPilot engaged at
     16.9 m rolled the plane inverted): no AutoPilot action anywhere, the attitude flown
     on the elevator and ailerons from P-D loops, the aileron sign self-checked, the bank
     loop settling over a bracket of plausible aileron authorities, and the flight's own
     hand-over frames replayed;
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


def runway(ut=100.0, lat=RWY_LAT, lon=RWY_LON, spd=0.0, sit="PRE_LAUNCH", hdg=90.0,
           alt=1.0, vs=0.0, pitch=1.5, thr=1.0):
    return snap(ut=ut, situation=sit, vessel_name="Mallard", latitude=lat, longitude=lon,
                heading=hdg, altitude=alt, surface_speed=spd, surface_pitch=pitch, roll=0.0,
                vertical_speed=vs, throttle=thr)


def lift_off(st, ut, lon, frames=mlib.AIR_AIRBORNE_FRAMES, alt=12.0, vs=3.0, spd=62.0,
             pitch=6.0, roll=0.0):
    """Feed ``frames`` consecutive climbing FLYING frames; returns (state, last actions)."""
    acts = []
    for i in range(frames):
        st, acts = mlib.air_decide(st, snap(ut=ut + 0.5 * i, situation="FLYING",
                                            latitude=RWY_LAT, longitude=lon, heading=90.0,
                                            altitude=alt + i, surface_speed=spd,
                                            vertical_speed=vs, surface_pitch=pitch,
                                            roll=roll))
    return st, acts


def kinds(actions):
    return [a.kind for a in actions]


# Every AutoPilot action the runner knows. AIR-1 must never send one (third flight
# 2026-11-16_2126: the AutoPilot engaged at 16.9 m rolled the Mallard inverted).
AP_KINDS = frozenset(getattr(mlib, n) for n in dir(mlib) if n.startswith("ACTION_AP_"))


def no_autopilot(test, actions):
    test.assertEqual([], [a.kind for a in actions if a.kind in AP_KINDS])


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

    def test_ground_steer_sign_is_normalized_to_one(self):
        self.assertEqual(-1.0,
                         mlib.air_initial_state(params(groundSteerSign=-0.5)).ground_steer_sign)
        self.assertEqual(1.0, mlib.air_initial_state(params()).ground_steer_sign)

    def test_takeoff_defaults_follow_the_first_flight(self):
        # 2026-11-16_1955: lifting at 0.5 deg of pitch by 43-47 m/s, so rotate at 45;
        # the heading had drifted 8 deg by 36 m/s, so give up there.
        p = mlib.air_params_from_dict({"craftName": "Mallard"})
        self.assertEqual(45.0, p.rotate_speed)
        self.assertEqual(8.0, p.ground_heading_abort_deg)
        self.assertEqual(1.0, p.ground_steer_sign)

    def test_ground_heading_abort_limit_is_checked(self):
        for bad in (0.1, 60.0):
            with self.subTest(bad=bad):
                self.assertIn("groundHeadingAbortDeg", mlib.air_params_problem(
                    mlib.air_params_from_dict({"craftName": "M",
                                               "groundHeadingAbortDeg": bad})))


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

    def test_a_craft_that_does_not_read_level_is_never_staged(self):
        st = run_to_arm(mlib.air_initial_state(params()))
        st, acts = mlib.air_decide(st, replace(runway(ut=101.0), roll=-90.0))
        self.assertEqual([], acts)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("roll -90.0", st.flake_reason)

    def test_the_first_flights_level_roll_stages(self):
        # 2026-11-16_1955 read roll 0.051 on the ARM frame.
        st = run_to_arm(mlib.air_initial_state(params()))
        st, acts = mlib.air_decide(st, replace(runway(ut=101.0), roll=0.051))
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, kinds(acts))

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
        # No AutoPilot: it never steered the nose wheel (first flight) and it rolled
        # the plane inverted at the hand-over (third flight).
        self.assertEqual([mlib.ACTION_SET_BRAKES, mlib.ACTION_SET_WHEEL_STEERING,
                          mlib.ACTION_SET_THROTTLE], kinds(acts))
        self.assertEqual([0.0, 0.0, 1.0], [a.value for a in acts])
        self.assertEqual(RWY_LON, st.release_lon)
        self.assertIsNone(st.cmd_pitch)

    def test_rotates_on_the_elevator_at_the_rotate_speed(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=55.0, rotatePitch=8.0)))
        st, acts = mlib.air_decide(st, runway(ut=120.0, spd=40.0, sit="LANDED"))
        self.assertEqual([], acts)
        st, acts = mlib.air_decide(st, runway(ut=121.0, spd=56.0, sit="LANDED"))
        # 0.08 per deg of the 6.5 deg error, no pitch rate: nose up 0.52, sign +1.
        self.assertEqual([mlib.ACTION_SET_PITCH_INPUT], kinds(acts))
        self.assertAlmostEqual(0.52, acts[0].value)
        self.assertTrue(st.rotated)
        self.assertEqual(121.0, st.rotate_ut)
        self.assertEqual(1.5, st.rotate_start_pitch)
        self.assertIsNone(st.cmd_pitch)
        # The same pitch next frame: inside the dead band, nothing re-sent.
        st, acts = mlib.air_decide(st, runway(ut=122.0, spd=58.0, sit="LANDED"))
        self.assertEqual([], acts)

    def test_airborne_enters_the_climb_with_evidence(self):
        st = run_to_takeoff(mlib.air_initial_state(params(climbPitch=10.0)))
        st, acts = lift_off(st, 129.0, RWY_LON + 0.08)
        self.assertEqual(mlib.AIR_CLIMB, st.phase)
        self.assertEqual(130.0, st.takeoff_ut)
        self.assertAlmostEqual(0.08 * METERS_PER_DEG, st.takeoff_distance, delta=5.0)
        # The attitude loop takes over on raw inputs: climbPitch 10 against a steady 6
        # deg pitch is 0.08 * 4 = 0.32 nose up; wings level on the runway heading.
        no_autopilot(self, acts)
        self.assertAlmostEqual(0.32, of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value)
        self.assertEqual(0.0, of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value)
        self.assertEqual(10.0, st.cmd_pitch)
        self.assertEqual(0.0, st.cmd_roll)

    def test_a_hop_below_the_airborne_altitude_is_not_a_takeoff(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, snap(ut=130.0, situation="FLYING", latitude=RWY_LAT,
                                         longitude=RWY_LON + 0.05, heading=90.0,
                                         altitude=3.0, surface_speed=50.0))
        self.assertEqual(mlib.AIR_TAKEOFF, st.phase)

    def test_airborne_is_sustained_and_climbing(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = lift_off(st, 129.0, RWY_LON + 0.05, frames=mlib.AIR_AIRBORNE_FRAMES - 1)
        self.assertEqual(mlib.AIR_TAKEOFF, st.phase)
        # Level or sinking above the altitude is not a climb-out.
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = lift_off(st, 129.0, RWY_LON + 0.05, frames=5, vs=0.0)
        self.assertEqual(mlib.AIR_TAKEOFF, st.phase)
        self.assertEqual(0, st.liftoff_streak)

    def test_the_first_flights_bounce_is_not_a_takeoff(self):
        # 2026-11-16_1955 frames 49.74 .. 55.38 (alt, situation, vertical speed): the
        # one FLYING frame at 10.6 m between LANDED frames moved the old machine to
        # the CLIMB, whose surface check then gave up.
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=60.0)))
        frames = [(51.28, 5.516, "FLYING", 1.0), (51.78, 5.008, "FLYING", -3.493),
                  (52.28, 3.532, "LANDED", -2.0), (52.80, 3.683, "FLYING", 0.778),
                  (53.32, 4.729, "LANDED", 2.0), (53.84, 6.940, "FLYING", 4.610),
                  (54.36, 9.250, "LANDED", 4.0), (54.86, 10.605, "FLYING", 1.132),
                  (55.38, 9.969, "LANDED", -1.0)]
        for ut, alt, sit, vs in frames:
            st, _ = mlib.air_decide(st, runway(ut=ut, lon=RWY_LON + 0.02, spd=45.0,
                                               sit=sit, alt=alt, vs=vs))
            self.assertEqual(mlib.AIR_TAKEOFF, st.phase, ut)
        self.assertIsNone(st.takeoff_ut)

    def test_not_airborne_by_the_distance_limit_aborts_by_name(self):
        st = run_to_takeoff(mlib.air_initial_state(params(takeoffMaxDistance=1500.0)))
        far = RWY_LON + 1600.0 / METERS_PER_DEG
        st, acts = mlib.air_decide(st, runway(ut=130.0, lon=far, spd=45.0, sit="LANDED"))
        self.assertIsNone(st.verdict)
        self.assertIn("not airborne", st.abort_reason)
        self.assertIn("pitch 1.5, highest on the ground 1.5", st.abort_reason)
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_SET_BRAKES,
                          mlib.ACTION_SET_WHEEL_STEERING], kinds(acts))
        self.assertEqual([0.0, 1.0, 0.0], [a.value for a in acts])
        st, acts = mlib.air_decide(st, runway(ut=130.5, lon=far, spd=44.0, sit="LANDED",
                                              thr=0.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.AIR_TAKEOFF, st.flake_phase)
        self.assertIn("not airborne", st.flake_reason)
        self.assertIn("throttle cut read back 0.00 after 1 frame(s)", st.flake_reason)
        self.assertEqual([], acts)

    def test_not_airborne_by_the_time_limit_aborts_by_name(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        t = st.release_ut + mlib.AIR_TAKEOFF_TIMEOUT_SECONDS + 1.0
        st, acts = mlib.air_decide(st, runway(ut=t, lon=RWY_LON + 0.01, spd=5.0, sit="LANDED"))
        self.assertIn("not airborne", st.abort_reason)
        st, _ = mlib.air_decide(st, runway(ut=t + 0.5, lon=RWY_LON + 0.01, spd=4.0,
                                           sit="LANDED", thr=0.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("not airborne", st.flake_reason)


class GroundRollTests(unittest.TestCase):
    def test_steer_command_sign_follows_krpc(self):
        # kRPC: +1 steers LEFT. A positive error wants a right turn: negative value.
        self.assertLess(mlib.air_ground_steer_command(2.0, 0.0, 0.1, 1.0), 0.0)
        self.assertGreater(mlib.air_ground_steer_command(-2.0, 0.0, 0.1, 1.0), 0.0)
        self.assertGreater(mlib.air_ground_steer_command(2.0, 0.0, 0.1, -1.0), 0.0)
        self.assertAlmostEqual(-0.2, mlib.air_ground_steer_command(2.0, 0.0, 0.1, 1.0))

    def test_steer_command_is_damped_and_clamped(self):
        # The error growing at 2 deg/s adds 0.1 more lock the same way.
        self.assertAlmostEqual(-0.3, mlib.air_ground_steer_command(2.0, 2.0, 0.1, 1.0))
        self.assertEqual(-mlib.AIR_GROUND_STEER_MAX,
                         mlib.air_ground_steer_command(30.0, 0.0, 0.1, 1.0))
        self.assertEqual(mlib.AIR_GROUND_STEER_MAX,
                         mlib.air_ground_steer_command(-30.0, -9.0, 0.1, 1.0))
        self.assertEqual(0.0, mlib.air_ground_steer_command(float("nan"), 1.0, 0.1, 1.0))
        self.assertAlmostEqual(-0.2, mlib.air_ground_steer_command(2.0, float("nan"),
                                                                   0.1, 1.0))

    def test_a_left_drift_steers_right_with_the_autopilot_off(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, acts = mlib.air_decide(st, runway(ut=110.0, spd=17.0, sit="LANDED", hdg=88.0))
        self.assertEqual([mlib.ACTION_SET_WHEEL_STEERING], kinds(acts))
        self.assertAlmostEqual(-0.2, acts[0].value)
        st, acts = mlib.air_decide(st, runway(ut=110.5, spd=19.0, sit="LANDED", hdg=91.0))
        self.assertGreater(acts[0].value, 0.0)
        no_autopilot(self, acts)
        self.assertFalse(st.rotated)

    def test_a_steady_heading_sends_nothing(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, acts = mlib.air_decide(st, runway(ut=110.0, spd=10.0, sit="LANDED", hdg=88.0))
        self.assertEqual(1, len(acts))
        st, acts = mlib.air_decide(st, runway(ut=110.5, spd=12.0, sit="LANDED", hdg=88.0))
        self.assertEqual([], acts)

    def test_heading_drift_on_the_ground_gives_up_by_name(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, runway(ut=110.0, spd=30.0, sit="LANDED", hdg=85.0))
        st, acts = mlib.air_decide(st, runway(ut=110.5, spd=32.0, sit="LANDED", hdg=81.5))
        # The give-up is decided: cut, brakes, wheel neutral...
        self.assertFalse(st.done)
        self.assertIn("heading drift on the ground roll", st.abort_reason)
        self.assertIn("+8.5 deg", st.abort_reason)
        self.assertIn("not rotated", st.abort_reason)
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_SET_BRAKES,
                          mlib.ACTION_SET_WHEEL_STEERING], kinds(acts))
        self.assertEqual([0.0, 1.0, 0.0], [a.value for a in acts])
        self.assertAlmostEqual(8.5, st.max_ground_heading_err)
        # ...and named once the cut reads back.
        st, acts = mlib.air_decide(st, runway(ut=111.0, spd=31.0, sit="LANDED", hdg=80.0,
                                              thr=0.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.AIR_TAKEOFF, st.flake_phase)
        self.assertIn("heading drift on the ground roll", st.flake_reason)

    def test_drift_off_the_ground_is_not_a_ground_abort(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, runway(ut=110.0, spd=50.0, sit="FLYING", hdg=70.0,
                                           alt=4.0))
        self.assertIsNone(st.verdict)

    def test_the_first_flights_roll_gives_up_at_the_limit(self):
        # The recorded heading / speed of 2026-11-16_1955 (no steering then): the
        # give-up fires on the first frame past 8 deg, 36 m/s, well before the plane
        # leaves the runway at ~43 m/s.
        st = run_to_takeoff(mlib.air_initial_state(params()))
        rows = [(41.58, 90.384, 10.694), (42.60, 90.251, 13.976), (43.62, 89.713, 17.439),
                (44.64, 88.935, 21.065), (45.66, 87.883, 24.807), (46.68, 86.449, 28.614),
                (47.68, 83.997, 32.364), (48.70, 79.415, 36.167), (49.74, 71.854, 39.891)]
        for ut, hdg, spd in rows:
            st, acts = mlib.air_decide(replace(st, runway_heading=90.382),
                                       runway(ut=ut, spd=spd, sit="LANDED", hdg=hdg))
            if st.abort_reason is not None:
                break
        self.assertEqual(48.70, ut)
        self.assertIn("heading drift", st.abort_reason)

    def test_the_rotate_keeps_the_wheel_steering_and_the_autopilot_off(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotatePitch=8.0)))
        st, acts = mlib.air_decide(st, runway(ut=110.0, spd=40.0, sit="LANDED", hdg=88.0))
        self.assertLess(st.cmd_wheel_steer, 0.0)
        st, acts = mlib.air_decide(st, runway(ut=111.0, spd=45.5, sit="LANDED", hdg=89.0))
        self.assertTrue(st.rotated)
        # Error +1 shrinking at 1 deg/s: 0.1 - 0.05 = 0.05 right (negative), then the
        # elevator. No AutoPilot action at the rotate.
        self.assertEqual([mlib.ACTION_SET_WHEEL_STEERING, mlib.ACTION_SET_PITCH_INPUT],
                         kinds(acts))
        self.assertAlmostEqual(-0.05, acts[0].value)
        self.assertGreater(acts[1].value, 0.0)
        # Rotated and still on the ground: the wheel still steers.
        st, acts = mlib.air_decide(st, runway(ut=111.5, spd=47.0, sit="LANDED", hdg=88.0))
        self.assertLess(of_kind(acts, mlib.ACTION_SET_WHEEL_STEERING)[0].value, 0.0)
        no_autopilot(self, acts)
        self.assertIsNone(st.cmd_pitch)

    def test_the_steering_sign_flips_once_on_a_growing_error(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        hdgs = [88.0, 87.5, 87.0, 86.5]
        for i, h in enumerate(hdgs):
            st, acts = mlib.air_decide(st, runway(ut=110.0 + 0.5 * i, spd=20.0,
                                                  sit="LANDED", hdg=h))
        self.assertTrue(st.ground_steer_flipped)
        self.assertEqual(-1.0, st.ground_steer_sign)
        # Steered the new way on the flip frame: a right turn is now POSITIVE.
        self.assertGreater(st.cmd_wheel_steer, 0.0)
        flipped = st
        st, _ = mlib.air_decide(st, runway(ut=112.5, spd=21.0, sit="LANDED", hdg=86.0))
        self.assertEqual(flipped.ground_steer_sign, st.ground_steer_sign)

    def test_a_shrinking_error_confirms_the_sign(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        for i, h in enumerate([87.0, 87.5]):
            st, _ = mlib.air_decide(st, runway(ut=110.0 + 0.5 * i, spd=20.0, sit="LANDED",
                                               hdg=h))
        self.assertTrue(st.ground_steer_confirmed)
        self.assertFalse(st.ground_steer_flipped)

    def test_the_check_needs_a_real_command(self):
        st = mlib.air_initial_state(params())
        st2 = mlib.air_ground_steer_check(st, 3.0, 0.05)
        self.assertIsNone(st2.ground_check_last_err)
        st2 = mlib.air_ground_steer_check(st, 1.0, 0.4)
        self.assertIsNone(st2.ground_check_last_err)

    def test_liftoff_straightens_a_steered_wheel(self):
        # A lift-off below the rotate speed with the wheel still turned: a lift-off on
        # the runway heading steers it straight, and the climb leaves it straight.
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=70.0)))
        st = replace(st, cmd_wheel_steer=-0.2)
        st, acts = mlib.air_decide(st, snap(ut=129.0, situation="FLYING", latitude=RWY_LAT,
                                            longitude=RWY_LON + 0.08, heading=90.0,
                                            altitude=12.0, surface_speed=62.0,
                                            vertical_speed=3.0))
        self.assertEqual([0.0], [a.value for a in of_kind(acts,
                                                          mlib.ACTION_SET_WHEEL_STEERING)])
        st, acts = lift_off(st, 129.5, RWY_LON + 0.08,
                            frames=mlib.AIR_AIRBORNE_FRAMES - 1, alt=13.0)
        self.assertEqual(mlib.AIR_CLIMB, st.phase)
        self.assertEqual(0.0, st.cmd_wheel_steer)
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=70.0)))
        st = replace(st, cmd_wheel_steer=-0.2)
        st, acts = lift_off(st, 129.0, RWY_LON + 0.08, frames=1)
        st = replace(st, cmd_wheel_steer=-0.2, liftoff_streak=mlib.AIR_AIRBORNE_FRAMES - 1)
        st, acts = lift_off(st, 130.0, RWY_LON + 0.08, frames=1)
        self.assertEqual(mlib.AIR_CLIMB, st.phase)
        self.assertEqual([0.0], [a.value for a in of_kind(acts,
                                                          mlib.ACTION_SET_WHEEL_STEERING)])


class RotationTests(unittest.TestCase):
    def test_nose_up_effort_is_proportional_damped_and_clamped(self):
        self.assertAlmostEqual(0.52, mlib.air_rotate_nose_up(8.0, 1.5, 0.0))
        # The second flight's rotate frame: 7.56 deg short, pitch easing at 0.02 deg/s.
        self.assertEqual(mlib.AIR_ROTATE_MAX_INPUT, mlib.air_rotate_nose_up(8.0, 0.439,
                                                                            -0.0196))
        # A nose rising 5 deg/s two degrees short eases off past neutral.
        self.assertAlmostEqual(-0.04, mlib.air_rotate_nose_up(8.0, 6.0, 5.0))
        self.assertEqual(-mlib.AIR_ROTATE_MAX_INPUT, mlib.air_rotate_nose_up(0.0, 20.0, 0.0))
        self.assertEqual(0.0, mlib.air_rotate_nose_up(8.0, float("nan"), 0.0))
        self.assertAlmostEqual(0.52, mlib.air_rotate_nose_up(8.0, 1.5, float("nan")))

    def test_pitch_input_follows_the_sign(self):
        self.assertEqual(0.3, mlib.air_pitch_input(0.3, 1.0))
        self.assertEqual(-0.3, mlib.air_pitch_input(0.3, -1.0))
        self.assertEqual(0.0, mlib.air_pitch_input(0.0, -1.0))

    def test_a_falling_nose_under_a_nose_up_command_flips_the_sign_once(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=45.0)))
        st, acts = mlib.air_decide(st, runway(ut=120.0, spd=46.0, sit="LANDED", pitch=1.0))
        self.assertGreater(acts[-1].value, 0.0)
        st, _ = mlib.air_decide(st, runway(ut=120.5, spd=47.0, sit="LANDED", pitch=0.8))
        self.assertFalse(st.rotate_pitch_flipped)
        st, acts = mlib.air_decide(st, runway(ut=121.0, spd=48.0, sit="LANDED", pitch=0.6))
        self.assertTrue(st.rotate_pitch_flipped)
        self.assertEqual(-1.0, st.rotate_pitch_sign)
        # Elevated the new way on the flip frame.
        elevator = of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)
        self.assertLess(elevator[0].value, 0.0)
        flipped = st
        st, _ = mlib.air_decide(st, runway(ut=121.5, spd=49.0, sit="LANDED", pitch=0.4))
        self.assertEqual(flipped.rotate_pitch_sign, st.rotate_pitch_sign)

    def test_a_rising_nose_confirms_the_sign(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=45.0)))
        st, _ = mlib.air_decide(st, runway(ut=120.0, spd=46.0, sit="LANDED", pitch=0.5))
        st, _ = mlib.air_decide(st, runway(ut=120.5, spd=47.0, sit="LANDED", pitch=1.6))
        self.assertTrue(st.rotate_pitch_confirmed)
        self.assertEqual(1.0, st.rotate_pitch_sign)

    def test_the_check_needs_a_real_nose_up_command(self):
        st = replace(mlib.air_initial_state(params()), rotate_start_pitch=7.0)
        st2 = mlib.air_rotate_pitch_check(st, 7.5, 0.05)
        self.assertIsNone(st2.rotate_check_last_pitch)
        self.assertFalse(st2.rotate_pitch_confirmed)

    def test_the_wheel_steers_through_a_hop_without_judging_its_sign(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=45.0)))
        st, _ = mlib.air_decide(st, runway(ut=120.0, spd=46.0, sit="LANDED"))
        before = (st.ground_check_frames, st.ground_check_last_err)
        st, acts = mlib.air_decide(st, runway(ut=120.5, spd=47.0, sit="FLYING", alt=5.0,
                                              hdg=88.0))
        self.assertEqual(mlib.AIR_TAKEOFF, st.phase)
        self.assertLess(of_kind(acts, mlib.ACTION_SET_WHEEL_STEERING)[0].value, 0.0)
        self.assertEqual(before, (st.ground_check_frames, st.ground_check_last_err))
        no_autopilot(self, acts)

    def test_sustained_airborne_hands_the_elevator_to_the_flight_loop(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=45.0, climbPitch=10.0)))
        st, _ = mlib.air_decide(st, runway(ut=120.0, spd=46.0, sit="LANDED"))
        self.assertGreater(st.cmd_pitch_input, 0.0)
        for i in range(mlib.AIR_AIRBORNE_FRAMES):
            # Nose at 5 deg and holding: the elevator keeps a nose-up command in the air.
            st, acts = mlib.air_decide(st, runway(ut=121.0 + 0.5 * i, lon=RWY_LON + 0.03,
                                                  spd=55.0, sit="FLYING", alt=12.0 + i,
                                                  vs=3.0, pitch=5.0))
            if i < mlib.AIR_AIRBORNE_FRAMES - 1:
                self.assertEqual(mlib.AIR_TAKEOFF, st.phase)
                self.assertGreater(st.cmd_pitch_input, 0.0)
        self.assertEqual(mlib.AIR_CLIMB, st.phase)
        # No neutral elevator and no AutoPilot at the hand-over: the flight loop keeps
        # the nose-up command toward climbPitch (0.08 * (10 - 5) = 0.4, the flight cap).
        no_autopilot(self, acts)
        self.assertEqual(mlib.AIR_FLIGHT_PITCH_MAX_INPUT, st.cmd_pitch_input)
        self.assertEqual([], [a for a in of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)
                              if a.value == 0.0])

    def test_the_rotation_evidence_is_carried(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=45.0)))
        st, _ = mlib.air_decide(st, runway(ut=120.0, spd=46.0, sit="LANDED", pitch=0.5))
        out = {a.name: a for a in mlib.evaluate_air_assertions([], st.params, st)}
        detail = out["airborne"].detail
        self.assertEqual(120.0, detail["rotateUT"])
        self.assertEqual(0.5, detail["rotateStartPitch"])
        self.assertEqual(1.0, detail["rotatePitchSign"])
        self.assertFalse(detail["rotatePitchFlipped"])
        self.assertIsNone(detail["abortThrottleReadback"])


class SecondFlightReplayTests(unittest.TestCase):
    """2026-11-16_2034_a2 (the retry; _2033 read the same within a few hundredths), its
    telemetry rows joined with the window dumps' pitch, ARM heading ~90.45. Rows up to
    the 50.62 rotate frame are the nose-wheel roll the new machine also flies; the two
    after it are the AutoPilot's swing, replayed only to show that the wheels and the
    ground guard stay live after the rotate (the new machine does not fly them)."""

    # (ut, heading, surface speed, pitch, altitude, lon)
    ROLL = [(42.50, 90.502, 13.861, 0.566, 3.117, -74.721),
            (43.54, 90.506, 17.514, 0.557, 3.120, -74.720),
            (44.54, 90.509, 21.260, 0.545, 3.123, -74.718),
            (45.56, 90.513, 25.276, 0.532, 3.127, -74.716),
            (46.56, 90.517, 29.364, 0.516, 3.131, -74.713),
            (47.58, 90.522, 33.654, 0.499, 3.136, -74.710),
            (48.58, 90.526, 37.946, 0.480, 3.141, -74.707),
            (49.60, 90.530, 42.382, 0.459, 3.146, -74.703),
            (50.62, 90.535, 46.850, 0.439, 3.152, -74.698)]
    SWING = [(51.66, 87.633, 50.907, 0.842, 3.176, -74.694),
             (52.16, 81.900, 52.200, 2.493, 3.229, -74.692)]

    def _start(self):
        st = run_to_takeoff(mlib.air_initial_state(params(
            rotateSpeed=45.0, rotatePitch=8.0, groundHeadingAbortDeg=8.0)))
        return replace(st, runway_heading=90.45, release_ut=35.38, release_lat=-0.049,
                       release_lon=-74.725, last_ground_err=None, last_ground_ut=None)

    def _feed(self, st, row, thr=1.0):
        ut, hdg, spd, pitch, alt, lon = row
        return mlib.air_decide(st, runway(ut=ut, lat=-0.049, lon=lon, spd=spd, sit="LANDED",
                                          hdg=hdg, alt=alt, pitch=pitch, thr=thr))

    def test_the_rotate_frame_raises_the_elevator_and_leaves_the_autopilot_off(self):
        st = self._start()
        sent = []
        for row in self.ROLL:
            st, acts = self._feed(st, row)
            sent.extend(acts)
        self.assertIsNone(st.abort_reason)
        no_autopilot(self, sent)
        self.assertTrue(st.rotated)
        self.assertEqual(50.62, st.rotate_ut)
        self.assertEqual(0.439, st.rotate_start_pitch)
        self.assertEqual([mlib.AIR_ROTATE_MAX_INPUT],
                         [a.value for a in of_kind(sent, mlib.ACTION_SET_PITCH_INPUT)])
        self.assertLess(st.max_ground_heading_err, 0.1)

    def test_the_wheels_and_the_guard_stay_live_after_the_rotate(self):
        st = self._start()
        for row in self.ROLL:
            st, _ = self._feed(st, row)
        st, acts = self._feed(st, self.SWING[0])
        # 2.8 deg left of the runway: the wheel steers right, the elevator eases with
        # the rising nose, and the guard has not fired.
        self.assertIsNone(st.abort_reason)
        self.assertLess(of_kind(acts, mlib.ACTION_SET_WHEEL_STEERING)[0].value, -0.3)
        self.assertLess(of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value,
                        mlib.AIR_ROTATE_MAX_INPUT)
        no_autopilot(self, acts)
        st, acts = self._feed(st, self.SWING[1])
        self.assertIn("heading drift on the ground roll", st.abort_reason)
        self.assertIn("rotating on the elevator", st.abort_reason)
        self.assertIn("at pitch 2.5", st.abort_reason)
        self.assertEqual(0.0, of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value)
        no_autopilot(self, acts)
        # The cut reads back on the next frame and the flake is named.
        st, acts = self._feed(st, self.SWING[1], thr=0.0)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("throttle cut read back 0.00 after 1 frame(s)", st.flake_reason)


class AbortConfirmTests(unittest.TestCase):
    def _aborting(self):
        st = run_to_takeoff(mlib.air_initial_state(params()))
        st, _ = mlib.air_decide(st, runway(ut=110.0, spd=30.0, sit="LANDED", hdg=80.0))
        self.assertIsNotNone(st.abort_reason)
        return st

    def test_the_cut_is_re_sent_until_it_reads_back(self):
        st = self._aborting()
        for i in range(2):
            st, acts = mlib.air_decide(st, runway(ut=111.0 + i, spd=30.0, sit="LANDED",
                                                  thr=1.0))
            self.assertFalse(st.done)
            self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_SET_BRAKES], kinds(acts))
            self.assertEqual([0.0, 1.0], [a.value for a in acts])
        st, acts = mlib.air_decide(st, runway(ut=113.0, spd=28.0, sit="LANDED", thr=0.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("throttle cut read back 0.00 after 3 frame(s)", st.flake_reason)
        self.assertEqual(0.0, st.abort_throttle)

    def test_a_throttle_that_never_reads_back_is_named(self):
        st = self._aborting()
        for i in range(mlib.AIR_ABORT_CONFIRM_FRAMES):
            st, acts = mlib.air_decide(st, runway(ut=111.0 + i, spd=30.0, sit="LANDED",
                                                  thr=1.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("throttle STILL read 1.00 after %d frames"
                      % mlib.AIR_ABORT_CONFIRM_FRAMES, st.flake_reason)
        self.assertEqual([mlib.ACTION_SET_THROTTLE], kinds(acts))
        out = {a.name: a for a in mlib.evaluate_air_assertions([], st.params, st)}
        self.assertEqual(1.0, out["airborne"].detail["abortThrottleReadback"])

    def test_an_unread_throttle_is_not_a_confirmation(self):
        st = self._aborting()
        for i in range(mlib.AIR_ABORT_CONFIRM_FRAMES):
            st, _ = mlib.air_decide(st, runway(ut=111.0 + i, spd=30.0, sit="LANDED",
                                               thr=float("nan")))
        self.assertIn("throttle STILL read UNREAD", st.flake_reason)

    def test_vessel_loss_while_aborting_is_an_assert_fail(self):
        st = self._aborting()
        st, _ = mlib.air_decide(st, snap(vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)


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

    def test_holds_the_climb_pitch_on_the_runway_heading_by_banking(self):
        st = climb_state(climbPitch=10.0)
        st, acts = mlib.air_decide(st, air(ut=210.0, alt=100.0, hdg=93.0, spd=80.0,
                                           surface_pitch=8.0))
        no_autopilot(self, acts)
        # 3 deg right of the runway heading: bank 3 deg left; the pitch target rises
        # 0.1 deg per deg of bank, so the elevator is 0.08 * (10.3 - 8).
        self.assertAlmostEqual(-3.0, st.cmd_roll)
        self.assertAlmostEqual(10.3, st.cmd_pitch)
        self.assertAlmostEqual(0.08 * 2.3,
                               of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value)
        self.assertAlmostEqual(-0.036, of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value)

    def test_the_climb_bank_is_limited(self):
        st = climb_state()
        st, _ = mlib.air_decide(st, air(ut=210.0, alt=100.0, hdg=60.0, spd=80.0))
        self.assertEqual(mlib.AIR_CLIMB_MAX_BANK, st.cmd_roll)

    def test_stall_guard_lowers_the_climb_pitch(self):
        st = climb_state(climbPitch=12.0)
        st, acts = mlib.air_decide(st, air(ut=210.0, alt=100.0, spd=40.0))
        self.assertEqual(mlib.AIR_AOA_TRIM_DEG, st.cmd_pitch)

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
        s = air(ut=310.0, lat=-0.05, lon=-74.4, hdg=60.0, alt=400.0, surface_pitch=4.0)
        st, acts = mlib.air_decide(st, s)
        no_autopilot(self, acts)
        expect = mlib.air_steer_toward(s, wp[0], wp[1], 1000.0, 90.0, 25.0, 1.0)
        self.assertGreater(expect.roll, 0.0)
        self.assertAlmostEqual(expect.roll, st.cmd_roll)
        self.assertAlmostEqual(expect.pitch + mlib.AIR_TURN_PITCH_PER_BANK * expect.roll,
                               st.cmd_pitch)
        self.assertGreater(st.cmd_pitch, mlib.AIR_AOA_TRIM_DEG)
        # A right turn from wings level: right aileron (sign +1), nose up.
        self.assertGreater(of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value, 0.0)
        self.assertGreater(of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value, 0.0)

    def test_dead_band_suppresses_a_repeat_command(self):
        st = circuit_state()
        s = air(ut=310.0, lat=-0.05, lon=-74.4, hdg=90.0, alt=1000.0, surface_pitch=3.0)
        st, acts = mlib.air_decide(st, s)
        self.assertEqual([mlib.ACTION_SET_PITCH_INPUT, mlib.ACTION_SET_ROLL_INPUT,
                          mlib.ACTION_SET_THROTTLE], kinds(acts))
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

    def test_the_sign_check_reads_the_ground_track_not_the_nose(self):
        # The nose yaws TOWARD the waypoint (east) while the plane's track keeps
        # turning AWAY from it: sideslip under a wrong bank. The nose alone would
        # confirm the wrong sign; the track flips it.
        st = circuit_state()
        wp_lat, wp_lon = st.params.waypoints[0]
        lat, lon = wp_lat, wp_lon - 0.2
        track, nose = 150.0, 150.0
        frames = mlib.AIR_TURN_CHECK_FRAMES + 1
        for k in range(frames):
            st, _ = mlib.air_decide(st, air(ut=310.0 + k, lat=lat, lon=lon, hdg=nose,
                                            alt=1000.0))
            if st.bank_sign_flipped:
                break
            track += (mlib.AIR_TURN_CHECK_GROWTH + 4.0) / (frames - 2)
            nose -= 1.0
            step = 45.0 / METERS_PER_DEG
            lat += step * math.cos(math.radians(track))
            lon += step * math.sin(math.radians(track)) / math.cos(math.radians(lat))
        self.assertTrue(st.bank_sign_flipped)
        self.assertFalse(st.bank_sign_confirmed)

    def test_track_error_falls_back_to_the_nose_when_the_plane_barely_moved(self):
        st = replace(circuit_state(), track_lat=0.0, track_lon=-74.3)
        st2, err = mlib.air_track_error(st, air(lat=0.0, lon=-74.3 + 1.0 / METERS_PER_DEG),
                                        90.0, 12.0)
        self.assertEqual(12.0, err)
        self.assertEqual(-74.3 + 1.0 / METERS_PER_DEG, st2.track_lon)
        # Moved 50 m due north toward a target due east: the track is 90 deg off.
        st3, err = mlib.air_track_error(st, air(lat=50.0 / METERS_PER_DEG, lon=-74.3),
                                        90.0, 12.0)
        self.assertAlmostEqual(90.0, err, places=3)


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
        no_autopilot(self, acts)
        self.assertNotEqual(0.0, st.cmd_roll)
        self.assertNotEqual(0.0, of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value)
        self.assertEqual([], of_kind(acts, mlib.ACTION_SET_BRAKES))

    def test_aligned_glides_down_the_slope(self):
        st = final_state()
        s = self._at(3000.0, alt=600.0, spd=60.0, vs=0.0)
        st, acts = mlib.air_decide(st, s)
        self.assertTrue(st.final_aligned)
        # Glide altitude at 3 km on 6 deg is ~315 m: well below 600, so nose down.
        self.assertLess(st.cmd_pitch, mlib.AIR_AOA_TRIM_DEG)
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

    def test_flare_is_wings_level_throttle_zero(self):
        st = replace(final_state(flareAltitude=25.0), final_aligned=True)
        s = replace(self._at(200.0, hdg=95.0, alt=20.0, spd=58.0, vs=-4.0), roll=4.0)
        st, acts = mlib.air_decide(st, s)
        self.assertEqual(620.0, st.flare_ut)
        no_autopilot(self, acts)
        # Wings level whatever the touchdown point's bearing: 4 deg right bank is
        # rolled out with left aileron.
        self.assertEqual(0.0, st.cmd_roll)
        self.assertAlmostEqual(-0.048, of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value)
        self.assertEqual([0.0], [a.value for a in of_kind(acts, mlib.ACTION_SET_THROTTLE)])
        # Still flaring above the flare altitude once started (a float, not a go-around).
        st, acts = mlib.air_decide(st, self._at(150.0, hdg=95.0, alt=30.0, ut=621.0))
        self.assertEqual(620.0, st.flare_ut)

    def test_no_flare_before_alignment(self):
        st = final_state(flareAltitude=25.0)
        st, _ = mlib.air_decide(st, self._at(4000.0, hdg=270.0, alt=20.0))
        self.assertIsNone(st.flare_ut)

    def test_touchdown_cuts_power_brakes_and_neutralises_the_controls(self):
        st = replace(final_state(), final_aligned=True, flare_ut=650.0,
                     cmd_pitch_input=0.2, cmd_roll_input=-0.05)
        s = self._at(300.0, alt=0.5, spd=45.0, vs=-1.2, ut=655.0)
        s = replace(s, situation="SPLASHED")
        st, acts = mlib.air_decide(st, s)
        self.assertEqual(mlib.AIR_STOP, st.phase)
        self.assertEqual([mlib.ACTION_SET_THROTTLE, mlib.ACTION_SET_BRAKES,
                          mlib.ACTION_SET_PITCH_INPUT, mlib.ACTION_SET_ROLL_INPUT],
                         kinds(acts))
        self.assertEqual([0.0, 1.0, 0.0, 0.0], [a.value for a in acts])
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
        for i in range(mlib.AIR_AIRBORNE_FRAMES):
            st, _ = mlib.air_decide(st, air(ut=ut + 0.5 * i, lat=RWY_LAT, lon=RWY_LON + 0.08,
                                            alt=15.0 + i, spd=66.0, vs=3.0))
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


# --------------------------------------------------------------------------- attitude


def flight_state(**kw):
    """A CIRCUIT-phase state, for driving ``_air_attitude`` directly."""
    return replace(circuit_state(**kw), leg_start_ut=0.0, phase_entry_ut=0.0)


def fly(st, ut, roll, pitch=3.0, bank=25.0, pitch_target=3.0):
    return mlib._air_attitude(st, snap(ut=ut, roll=roll, surface_pitch=pitch),
                              pitch_target, bank, None)


def simulate_bank(kr, true_sign, bank=25.0, frames=40, **kw):
    """A roll plant with full-aileron authority ``kr`` deg/s and the given true input
    sign, sampled at the runner's 0.5 s: each frame the bank moves 0.5 * kr * input."""
    st = flight_state(**kw)
    roll, value, hist = 0.0, 0.0, []
    for k in range(frames):
        st, acts = fly(st, 100.0 + 0.5 * k, roll, bank=bank)
        for a in of_kind(acts, mlib.ACTION_SET_ROLL_INPUT):
            value = a.value
        hist.append(roll)
        roll += 0.5 * kr * true_sign * value
    return st, hist


class AttitudeLawTests(unittest.TestCase):
    def test_roll_effort_is_proportional_damped_and_clamped(self):
        # 10 deg short of a right bank: 0.012 * 10 = 0.12 right.
        self.assertAlmostEqual(0.12, mlib.air_roll_effort(10.0, 0.0, 0.0))
        # Rolling right at 10 deg/s eases it by 0.04.
        self.assertAlmostEqual(0.08, mlib.air_roll_effort(10.0, 0.0, 10.0))
        self.assertEqual(mlib.AIR_ROLL_MAX_INPUT, mlib.air_roll_effort(60.0, 0.0, 0.0))
        self.assertEqual(-mlib.AIR_ROLL_MAX_INPUT, mlib.air_roll_effort(-60.0, 0.0, 0.0))
        self.assertEqual(0.0, mlib.air_roll_effort(0.0, float("nan"), 0.0))
        self.assertEqual(0.0, mlib.air_roll_effort(float("nan"), 3.0, 0.0))
        self.assertAlmostEqual(0.12, mlib.air_roll_effort(10.0, 0.0, float("nan")))

    def test_roll_input_follows_the_sign(self):
        self.assertEqual(0.2, mlib.air_roll_input(0.2, 1.0))
        self.assertEqual(-0.2, mlib.air_roll_input(0.2, -1.0))
        self.assertEqual(0.0, mlib.air_roll_input(0.0, -1.0))

    def test_rate_needs_two_samples_a_sane_gap_apart(self):
        self.assertAlmostEqual(-4.0, mlib.air_rate(0.0, 10.0, -2.0, 10.5))
        self.assertTrue(math.isnan(mlib.air_rate(None, 10.0, 1.0, 10.5)))
        self.assertTrue(math.isnan(mlib.air_rate(0.0, None, 1.0, 10.5)))
        self.assertTrue(math.isnan(mlib.air_rate(0.0, 10.0, float("nan"), 10.5)))
        self.assertTrue(math.isnan(mlib.air_rate(0.0, 10.0, 1.0, 10.01)))
        self.assertTrue(math.isnan(mlib.air_rate(0.0, 10.0, 1.0, 13.0)))

    def test_the_bank_raises_the_pitch_target(self):
        st, acts = fly(flight_state(), 100.0, 0.0, pitch=3.0, bank=-20.0, pitch_target=3.0)
        self.assertAlmostEqual(5.0, st.cmd_pitch)
        self.assertAlmostEqual(-20.0, st.cmd_roll)
        self.assertAlmostEqual(0.16, of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value)
        self.assertAlmostEqual(-0.24, of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value)

    def test_the_flight_elevator_is_capped_below_the_rotation(self):
        st, acts = fly(flight_state(), 100.0, 0.0, pitch=-10.0, pitch_target=10.0)
        self.assertEqual(mlib.AIR_FLIGHT_PITCH_MAX_INPUT,
                         of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value)
        self.assertLess(mlib.AIR_FLIGHT_PITCH_MAX_INPUT, mlib.AIR_ROTATE_MAX_INPUT)

    def test_the_elevator_and_ailerons_follow_their_signs(self):
        st = replace(flight_state(), rotate_pitch_sign=-1.0, roll_input_sign=-1.0)
        st, acts = fly(st, 100.0, 0.0, pitch=0.0, bank=10.0, pitch_target=5.0)
        self.assertLess(of_kind(acts, mlib.ACTION_SET_PITCH_INPUT)[0].value, 0.0)
        self.assertLess(of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value, 0.0)

    def test_a_dark_attitude_holds_the_controls_neutral(self):
        st = replace(flight_state(), cmd_pitch_input=0.3, cmd_roll_input=0.2,
                     cmd_roll_effort=0.2)
        st, acts = mlib._air_attitude(st, snap(ut=100.0), 5.0, 10.0, None)
        self.assertEqual([0.0, 0.0], [a.value for a in acts])

    def test_only_the_elevator_and_ailerons_fly_the_attitude(self):
        # The rudder stays neutral (the hands-off frames show no yaw motion to damp) and
        # nothing engages the AutoPilot or SAS.
        st, seen = flight_state(), set()
        for k, roll in enumerate((0.0, 3.0, 9.0, 16.0, 22.0, 24.0, 25.0)):
            st, acts = fly(st, 100.0 + 0.5 * k, roll, pitch=2.0 + k)
            seen.update(kinds(acts))
        self.assertEqual({mlib.ACTION_SET_PITCH_INPUT, mlib.ACTION_SET_ROLL_INPUT}, seen)

    def test_the_bank_loop_settles_over_the_plausible_authority_range(self):
        # Kr 60 is the third flight's AutoPilot roll rate; 30 and 120 bracket it.
        for kr in (30.0, 60.0, 120.0):
            with self.subTest(kr=kr):
                st, hist = simulate_bank(kr, 1.0)
                self.assertTrue(st.roll_sign_confirmed)
                self.assertFalse(st.roll_sign_flipped)
                self.assertLess(abs(hist[-1] - 25.0), 2.5)
                self.assertLess(max(hist), 25.0 + 5.0)
                self.assertGreater(min(hist), -0.1)

    def test_a_wrong_aileron_sign_flips_once_and_then_settles(self):
        for kr in (30.0, 60.0, 120.0):
            with self.subTest(kr=kr):
                st, hist = simulate_bank(kr, -1.0)
                self.assertTrue(st.roll_sign_flipped)
                self.assertEqual(-1.0, st.roll_input_sign)
                # Two frames the wrong way at the 0.25 cap, then back.
                self.assertGreaterEqual(min(hist), -0.5 * kr * mlib.AIR_ROLL_MAX_INPUT * 2
                                        - 1e-6)
                self.assertLess(abs(hist[-1] - 25.0), 2.5)


class RollSignCheckTests(unittest.TestCase):
    def _sent(self, effort, roll=0.0, rate=None):
        st = flight_state()
        return replace(st, cmd_roll_effort=effort, last_roll=roll, last_roll_ut=99.5,
                       last_roll_rate=rate)

    def test_a_response_the_commanded_way_confirms(self):
        st = self._sent(0.2)
        st = mlib.air_roll_sign_check(st, 0.8, 1.6)
        self.assertFalse(st.roll_sign_confirmed)
        st = replace(st, last_roll=0.8, last_roll_rate=1.6)
        st = mlib.air_roll_sign_check(st, 1.6, 1.6)
        self.assertTrue(st.roll_sign_confirmed)
        self.assertEqual(1.0, st.roll_input_sign)
        # Confirmed, the check stops.
        self.assertIs(st, mlib.air_roll_sign_check(st, -50.0, -40.0))

    def test_two_frames_the_other_way_flip_once(self):
        st = self._sent(0.2, rate=0.0)
        st = mlib.air_roll_sign_check(st, -0.6, -1.2)
        self.assertEqual(1, st.roll_check_frames)
        self.assertFalse(st.roll_sign_flipped)
        st = replace(st, last_roll=-0.6, last_roll_rate=-1.2)
        st = mlib.air_roll_sign_check(st, -1.3, -1.4)
        self.assertTrue(st.roll_sign_flipped)
        self.assertEqual(-1.0, st.roll_input_sign)
        self.assertIs(st, mlib.air_roll_sign_check(st, -9.0, -9.0))

    def test_a_flip_needs_the_degree_lost(self):
        st = self._sent(0.2, rate=0.0)
        st = mlib.air_roll_sign_check(st, -0.2, -0.4)
        st = replace(st, last_roll=-0.2, last_roll_rate=-0.4)
        st = mlib.air_roll_sign_check(st, -0.5, -0.6)
        self.assertFalse(st.roll_sign_flipped)
        self.assertEqual(2, st.roll_check_frames)

    def test_arresting_a_roll_already_under_way_is_not_a_wrong_sign(self):
        # Rolling left at 10 deg/s when right aileron goes in: the bank still falls on
        # the next frames, but more slowly each time. That is the right sign working.
        st = self._sent(0.25, roll=-5.0, rate=-10.0)
        st = mlib.air_roll_sign_check(st, -8.0, -6.0)
        self.assertEqual(0, st.roll_check_frames)
        st = replace(st, last_roll=-8.0, last_roll_rate=-6.0)
        st = mlib.air_roll_sign_check(st, -9.5, -3.0)
        self.assertFalse(st.roll_sign_flipped)
        self.assertEqual(0, st.roll_check_frames)

    def test_the_check_needs_a_real_effort(self):
        st = self._sent(mlib.AIR_ROLL_CHECK_MIN_EFFORT / 2.0, rate=0.0)
        st = mlib.air_roll_sign_check(st, -3.0, -6.0)
        self.assertIsNone(st.roll_check_start)
        self.assertEqual(0, st.roll_check_frames)
        st = self._sent(None)
        self.assertIsNone(mlib.air_roll_sign_check(st, -3.0, -6.0).roll_check_start)

    def test_a_reversed_command_reopens_the_window(self):
        st = self._sent(0.2, rate=0.0)
        st = mlib.air_roll_sign_check(st, -0.6, -1.2)
        self.assertEqual(1, st.roll_check_frames)
        st = replace(st, cmd_roll_effort=-0.2, last_roll=-0.6, last_roll_rate=-1.2)
        st = mlib.air_roll_sign_check(st, -1.0, -0.8)
        self.assertEqual(-1.0, st.roll_check_dir)
        self.assertEqual(-0.6, st.roll_check_start)
        self.assertEqual(0, st.roll_check_frames)

    def test_a_flip_re_sends_the_ailerons_the_new_way_on_the_same_frame(self):
        st = flight_state()
        st, acts = fly(st, 100.0, 0.0)
        self.assertGreater(of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value, 0.0)
        st, _ = fly(st, 100.5, -7.5)
        st, acts = fly(st, 101.0, -15.0)
        self.assertTrue(st.roll_sign_flipped)
        self.assertLess(of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value, 0.0)

    def test_the_evidence_is_carried(self):
        st = replace(flight_state(), roll_sign_flipped=True, roll_input_sign=-1.0)
        out = {a.name: a for a in mlib.evaluate_air_assertions([], st.params, st)}
        detail = out["circuitFlown"].detail
        self.assertEqual(-1.0, detail["rollInputSign"])
        self.assertTrue(detail["rollSignFlipped"])
        self.assertFalse(detail["rollSignConfirmed"])

    def test_roll_input_sign_is_normalized_to_one(self):
        self.assertEqual(-1.0,
                         mlib.air_initial_state(params(rollInputSign=-0.5)).roll_input_sign)
        self.assertEqual(1.0, mlib.air_initial_state(params()).roll_input_sign)


class ThirdFlightReplayTests(unittest.TestCase):
    """2026-11-16_2126 (the retry _2128_a2 read the same within a degree): its window
    dump rows (ut, altitude, situation, pitch) joined with the 1 Hz telemetry's heading,
    roll, surface and vertical speed (interpolated between its rows), ARM heading 090.397. The machine then handed over
    to the AutoPilot at 58.78 and the plane rolled left: 59.30 roll -1.97 heading 089.6,
    60.28 roll -37.9 heading 072.6. The new machine never engages it."""

    ARM_HEADING = 90.397
    # (ut, alt, situation, pitch, surface speed, vertical speed, heading, roll)
    TAKEOFF = [(53.66, 3.196, "LANDED", 0.799, 58.7, 0.05, 90.338, 0.050),
               (54.16, 3.226, "LANDED", 1.623, 60.8, 0.012, 90.342, 0.049),
               (54.68, 3.288, "LANDED", 3.079, 62.6, 0.3, 90.346, 0.050),
               (55.20, 3.443, "FLYING", 4.882, 64.4, 0.576, 90.350, 0.051),
               (55.72, 4.196, "FLYING", 5.850, 65.8, 1.9, 90.354, 0.055),
               (56.24, 5.635, "FLYING", 5.798, 67.1, 3.132, 90.358, 0.060),
               (56.76, 7.439, "FLYING", 5.649, 68.7, 3.6, 90.363, 0.065),
               (57.28, 9.443, "FLYING", 5.856, 70.2, 3.978, 90.368, 0.071),
               (57.80, 11.700, "FLYING", 6.248, 71.6, 4.6, 90.374, 0.077),
               (58.28, 14.064, "FLYING", 6.535, 73.0, 5.210, 90.381, 0.083),
               (58.78, 16.863, "FLYING", 6.715, 74.3, 5.816, 90.385, 0.088)]
    HANDOVER = (59.30, 20.061, "FLYING", 7.083, 75.645, 6.295, 89.613, -1.968)
    ROLLED = (60.28, 27.933, "FLYING", 5.296, 75.604, 10.649, 72.554, -37.918)

    def _snap(self, row):
        ut, alt, sit, pitch, spd, vs, hdg, roll = row
        return snap(ut=ut, situation=sit, vessel_name="Mallard", latitude=-0.049,
                    longitude=-74.68, heading=hdg, altitude=alt, surface_speed=spd,
                    vertical_speed=vs, surface_pitch=pitch, roll=roll, throttle=1.0)

    def _airborne(self):
        st = run_to_takeoff(mlib.air_initial_state(params(rotateSpeed=45.0)))
        st = replace(st, runway_heading=self.ARM_HEADING, rotated=True, rotate_ut=52.1,
                     rotate_start_pitch=0.567, cmd_pitch_input=0.6, release_lat=-0.049,
                     release_lon=-74.725, release_ut=35.64)
        sent = []
        for row in self.TAKEOFF:
            st, acts = mlib.air_decide(st, self._snap(row))
            sent.extend(acts)
        return st, sent

    def test_the_climb_starts_where_the_flight_did_with_no_autopilot(self):
        st, sent = self._airborne()
        self.assertEqual(mlib.AIR_CLIMB, st.phase)
        self.assertEqual(58.78, st.takeoff_ut)
        self.assertTrue(st.rotate_pitch_confirmed)
        no_autopilot(self, sent)
        # The elevator is never dropped to neutral at the hand-over: it carries on
        # nose up toward climbPitch 10 from 6.7 deg.
        self.assertGreater(st.cmd_pitch_input, 0.1)

    def test_the_hand_over_frame_rolls_right_against_the_left_roll(self):
        st, _ = self._airborne()
        st, acts = mlib.air_decide(st, self._snap(self.HANDOVER))
        no_autopilot(self, acts)
        # The heading fell 0.8 deg left of the runway: a small right bank target, and
        # the roll reads 2 deg left and falling at ~4 deg/s, so right aileron.
        self.assertGreater(st.cmd_roll, 0.0)
        self.assertLess(st.cmd_roll, 1.0)
        aileron = of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)
        self.assertEqual(1, len(aileron))
        self.assertGreater(aileron[0].value, 0.04)
        # Nose still up toward 10 deg from 7.1.
        self.assertGreater(st.cmd_pitch_input, 0.1)

    def test_the_left_roll_the_autopilot_reached_gets_full_right_aileron(self):
        st, _ = self._airborne()
        st, _ = mlib.air_decide(st, self._snap(self.HANDOVER))
        st, acts = mlib.air_decide(st, self._snap(self.ROLLED))
        self.assertEqual(mlib.AIR_CLIMB_MAX_BANK, st.cmd_roll)
        self.assertEqual(mlib.AIR_ROLL_MAX_INPUT,
                         of_kind(acts, mlib.ACTION_SET_ROLL_INPUT)[0].value)
        # Right aileron in force and the bank fell 36 deg: one frame the wrong way,
        # not yet a flip (the check needs two).
        self.assertFalse(st.roll_sign_flipped)


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


class _FakeControl(object):
    wheel_steering = 0.0
    pitch = 0.0
    roll = 0.0


class _FakeVessel(object):
    def __init__(self, ap):
        self.auto_pilot = ap
        self.control = _FakeControl()
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

    def test_wheel_steering_is_set_and_clamped(self):
        for value, want in ((-0.3, -0.3), (2.0, 1.0), (-5.0, -1.0), (0.0, 0.0)):
            with self.subTest(value=value):
                c = mission_runner.KrpcMissionControl()
                vessel = _FakeVessel(_FakeAutoPilot())
                c._conn = _FakeConn(_FakeSpaceCenter(vessel))
                c.perform(mlib.Action(mlib.ACTION_SET_WHEEL_STEERING, value))
                self.assertEqual(want, vessel.control.wheel_steering)

    def test_pitch_input_is_set_and_clamped(self):
        for value, want in ((0.6, 0.6), (-0.3, -0.3), (3.0, 1.0), (-3.0, -1.0)):
            with self.subTest(value=value):
                c = mission_runner.KrpcMissionControl()
                vessel = _FakeVessel(_FakeAutoPilot())
                c._conn = _FakeConn(_FakeSpaceCenter(vessel))
                c.perform(mlib.Action(mlib.ACTION_SET_PITCH_INPUT, value))
                self.assertEqual(want, vessel.control.pitch)

    def test_roll_input_is_set_and_clamped(self):
        for value, want in ((0.25, 0.25), (-0.1, -0.1), (3.0, 1.0), (-3.0, -1.0)):
            with self.subTest(value=value):
                c = mission_runner.KrpcMissionControl()
                ap = _FakeAutoPilot()
                vessel = _FakeVessel(ap)
                c._conn = _FakeConn(_FakeSpaceCenter(vessel))
                c.perform(mlib.Action(mlib.ACTION_SET_ROLL_INPUT, value))
                self.assertEqual(want, vessel.control.roll)
                self.assertEqual([], ap.calls)

    def test_the_aircraft_channel_is_off_by_default(self):
        self.assertFalse(mission_runner.KrpcMissionControl()._read_aircraft)
        self.assertTrue(mission_runner.KrpcMissionControl(read_aircraft=True)._read_aircraft)


class _TickControl(object):
    """A control whose throttle readback is the value applied at the last physics tick
    (kRPC's), or, with ``holder``, one another controller pins every tick."""

    def __init__(self, applied=0.0, holder=None):
        self.applied = applied
        self.pending = None
        self.holder = holder
        self.sets = []

    @property
    def throttle(self):
        return self.applied

    @throttle.setter
    def throttle(self, value):
        self.sets.append(value)
        self.pending = value

    def tick(self):
        if self.pending is not None:
            self.applied = self.pending
            self.pending = None
        if self.holder is not None:
            self.applied = self.holder


class _TickSpaceCenter(object):
    def __init__(self, control):
        self._control = control
        self._ut = 100.0
        self.active_vessel = type("V", (), {"control": control})()

    @property
    def ut(self):
        # Every read after the first is a later physics tick.
        self._ut += 0.02
        self._control.tick()
        return self._ut


class RunnerThrottleReadbackTests(unittest.TestCase):
    def setUp(self):
        self.lines = []
        self._sink = mission_runner._stdout_sink
        mission_runner._stdout_sink = self.lines.append

    def tearDown(self):
        mission_runner._stdout_sink = self._sink

    def _perform(self, control, value):
        c = mission_runner.KrpcMissionControl()
        c._conn = _FakeConn(_TickSpaceCenter(control))
        c.perform(mlib.Action(mlib.ACTION_SET_THROTTLE, value))
        return c

    def test_a_readback_one_tick_late_is_not_a_holder(self):
        # Every AIR-1 flight: 1.00 set, 0.00 read at once, 1.00 on the next frame.
        control = _TickControl(applied=1.0)
        self._perform(control, 0.0)
        self.assertEqual([0.0], control.sets)
        self.assertFalse([l for l in self.lines if "[Warn]" in l])
        self.assertTrue([l for l in self.lines if "[Verbose][Throttle]" in l
                         and "read back 1.00 at once and 0.00 after a physics tick" in l])

    def test_a_held_throttle_is_re_asserted_and_named(self):
        control = _TickControl(applied=1.0, holder=1.0)
        self._perform(control, 0.0)
        self.assertEqual([0.0, 0.0], control.sets)
        warns = [l for l in self.lines if "[Warn][Throttle]" in l]
        self.assertEqual(1, len(warns))
        self.assertIn("set_throttle 0.00 did not stick (readbacks 1.00 at once, 1.00 after "
                      "a physics tick, 1.00 after a re-assert)", warns[0])

    def test_a_matching_readback_logs_nothing(self):
        control = _TickControl(applied=0.0)
        control.pending = None
        c = mission_runner.KrpcMissionControl()
        sc = _TickSpaceCenter(control)
        c._conn = _FakeConn(sc)
        # The setter only queues; with the value already applied the read matches.
        c.perform(mlib.Action(mlib.ACTION_SET_THROTTLE, 0.0))
        self.assertEqual([], self.lines)


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

    def test_no_air_function_names_an_autopilot_action(self):
        # The static half of ``no_autopilot``: every function of the AIR machine, read
        # off the AST, so a path no unit cell drives cannot engage it either.
        with open(os.path.join(_HERE, "mlib.py"), encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        fns = [n for n in ast.walk(tree) if isinstance(n, ast.FunctionDef)
               and (n.name.startswith(("air_", "_air_")) or n.name == "evaluate_air_assertions")]
        self.assertIn("air_decide", [f.name for f in fns])
        named = {(f.name, node.id) for f in fns for node in ast.walk(f)
                 if isinstance(node, ast.Name) and node.id.startswith("ACTION_AP_")}
        self.assertEqual(set(), named)

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
