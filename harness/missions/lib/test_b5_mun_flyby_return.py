"""Unit cells for mission `b5_mun_flyby_return` (CS-5) and its composed machine.

The mission's claim is "B5's machine verbatim, then a return trim, then B4's machine
verbatim from its REENTRY". Each class pins one part of that claim:

  1. THE DELEGATES ARE VERBATIM. Over the same frames the flyby leg emits exactly
     what b5_decide emits (no setter keys set), and the B5 / B4 suites in
     test_mlib / test_shells / test_b4_reentry_career pass unchanged, because
     neither machine is edited.
  2. THE LAUNCH GATE: the career setters before B5's engage, the stage held until
     the AscentAutopilot reads enabled, the named give-up.
  3. THE PARAMS: B5 modes the tail cannot follow are refused, the window is
     ordered, the B4 delegate reads the return* keys only.
  4. THE TRIM, THE FALL AND THE HAND-OFFS, each with its give-ups.
  5. THE SHELL, THE SCHEMA AND THE CS-5 SPEC agree.

NO krpc, NO KSP, NO network. Import path matches the sibling suites.
"""

import dataclasses
import inspect
import math
import os
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_MISSIONS = os.path.dirname(_HERE)                       # harness/missions
_HARNESS = os.path.dirname(_MISSIONS)                    # harness/
if _MISSIONS not in sys.path:
    sys.path.insert(0, _MISSIONS)
if os.path.join(_HARNESS, "lib") not in sys.path:
    sys.path.insert(0, os.path.join(_HARNESS, "lib"))

import mlib                        # noqa: E402
import b5_mun_flyby                # noqa: E402
import b5_mun_flyby_return         # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import B5_PARAMS, FakeMissionControl, run, snap  # noqa: E402

CS5_SPEC = "CS-5-career-mun-flyby"

RETURN_PARAMS = {
    "returnPeriapsisMeters": 30000,
    "returnPeriapsisMinMeters": 20000,
    "returnPeriapsisMaxMeters": 40000,
    "returnHeadingDeg": 90,
    "returnTrimThrottle": 0.25,
    "returnTrimFineThrottle": 0.03,
    "returnTrimFineBandMeters": 300000,
    "returnTrimSettleSeconds": 10,
    "returnMaxAttitudeErrorDeg": 5,
    "returnTrimIgniteFrames": 3,
    "returnTrimTimeoutSeconds": 900,
    "returnStageCount": 1,
    "returnStageSettleSeconds": 2,
    "returnWarpAboveAltMeters": 70000,
    "returnWarpHopSeconds": 120,
    "returnChuteDeployAltMeters": 12000,
    "returnReentryTimeoutSeconds": 3600,
    "returnDescentTimeoutSeconds": 900,
    "returnLandedSituations": ["LANDED", "SPLASHED"],
    "returnFallLeadSeconds": 600,
    "returnFallMinAltMeters": 200000,
    "returnFallTimeoutSeconds": 200000,
}
CAREER_PARAMS = {"mjEngageConfirmFrames": 3, "autostageLimit": 2, "ascentType": 0}
PARAMS = dict(B5_PARAMS, **RETURN_PARAMS, **CAREER_PARAMS)
PLAIN_PARAMS = dict(B5_PARAMS, **RETURN_PARAMS)

MISSION_KEYS = set(RETURN_PARAMS) | set(CAREER_PARAMS)


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


def _cs5_spec():
    with open(os.path.join(_HARNESS, "scenarios", CS5_SPEC + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _params(**over):
    return mlib.mfr_params_from_dict(dict(PARAMS, **over))


def _kinds(actions):
    return [a.kind for a in actions]


def _returned_flight(params):
    """A B5 state already done at RETURN with its own rows met."""
    base = mlib.b5_initial_state(params.flyby)
    return dataclasses.replace(
        base, phase=mlib.B5_RETURN, done=True, min_target_altitude=1500000.0,
        phases_reached=(mlib.B5_PRELAUNCH, mlib.B5_MJ_ASCENT, mlib.B5_ORBIT,
                        mlib.B5_TARGET_FLYBY, mlib.B5_RETURN))


def _at(block, params=None, **over):
    """A machine state in ``block`` (a delegated B4 at REENTRY for HOME / FALL)."""
    p = params or _params()
    st = mlib.mfr_initial_state(p)
    st = dataclasses.replace(st, flight=_returned_flight(p), block=block, phase=block,
                             phase_entry_ut=1000.0)
    if block in (mlib.MFR_HOME, mlib.MFR_FALL):
        home = dataclasses.replace(mlib.b4_initial_state(p.home), phase=mlib.B4_REENTRY,
                                   phase_entry_ut=1000.0,
                                   phases_reached=(mlib.B4_REENTRY,))
        st = dataclasses.replace(st, home=home,
                                 phase=(mlib.MFR_HOME_PREFIX + mlib.B4_REENTRY
                                        if block == mlib.MFR_HOME else block))
    return dataclasses.replace(st, **over)


class DelegatesAreVerbatimTests(unittest.TestCase):
    """With no career key set the flyby leg is b5_decide, frame for frame."""

    FRAMES = [snap(ut=10.0 + i, altitude=70.0, situation="PRE_LAUNCH", body="Kerbin")
              for i in range(3)] + [
        snap(ut=20.0 + i, altitude=5000.0 * (i + 1), vertical_speed=200.0,
             apoapsis=10000.0 * (i + 1), periapsis=-500000.0, situation="FLYING",
             body="Kerbin", mj_autopilot_enabled=True) for i in range(5)]

    def test_the_flyby_leg_emits_exactly_b5s_actions(self):
        p = mlib.mfr_params_from_dict(PLAIN_PARAMS)
        st = mlib.mfr_initial_state(p)
        b5 = mlib.b5_initial_state(p.flyby)
        for frame in self.FRAMES:
            st, got = mlib.mfr_decide(st, frame)
            b5, want = mlib.b5_decide(b5, frame)
            self.assertEqual(want, got)
            self.assertEqual(b5, st.flight)
            self.assertEqual(b5.phase, st.phase)

    def test_the_plain_launch_is_left_untouched(self):
        p = mlib.mfr_params_from_dict(PLAIN_PARAMS)
        kickoff = mlib._b5_ascent_kickoff_actions(p.flyby)
        out, held, setters = mlib.mfr_inject_launch_actions(p, kickoff)
        self.assertEqual(kickoff, out)
        self.assertFalse(held)
        self.assertEqual((), setters)

    def test_neither_delegate_is_edited_by_the_composer(self):
        # The composer reads B5 / B4 state and builds B4's entry state, and it never
        # calls a private B5 / B4 helper that would couple it to their internals.
        src = inspect.getsource(mlib._mfr_flyby) + inspect.getsource(mlib._mfr_home)
        self.assertIn("b5_decide(", src)
        self.assertIn("b4_decide(", src)
        import re
        self.assertEqual([], re.findall(r"(?<![A-Za-z0-9])_b[45]_\w+", src))


class LaunchGateTests(unittest.TestCase):
    def test_the_setters_go_before_the_engage_and_the_stage_is_held(self):
        p = _params()
        st, actions = mlib.mfr_decide(mlib.mfr_initial_state(p),
                                      snap(ut=5.0, situation="PRE_LAUNCH", body="Kerbin"))
        self.assertEqual([mlib.ACTION_MJ_SET_TARGET_APOAPSIS, mlib.ACTION_MJ_ENABLE_AUTOSTAGE,
                          mlib.ACTION_MJ_SET_ASCENT_TYPE, mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT,
                          mlib.ACTION_MJ_ENGAGE_ASCENT], _kinds(actions))
        self.assertEqual(2.0, actions[3].value)
        self.assertEqual(0.0, actions[2].value)
        self.assertTrue(st.stage_held)
        self.assertEqual(mlib.B5_MJ_ASCENT, st.phase)

    def test_the_stage_waits_for_the_enabled_read_then_restamps_b5s_clock(self):
        p = _params()
        st, _ = mlib.mfr_decide(mlib.mfr_initial_state(p), snap(ut=5.0))
        st, actions = mlib.mfr_decide(st, snap(ut=6.0, mj_autopilot_enabled=False))
        self.assertEqual([], actions)
        self.assertTrue(st.stage_held)
        st, actions = mlib.mfr_decide(st, snap(ut=7.0, mj_autopilot_enabled=True))
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], _kinds(actions))
        self.assertFalse(st.stage_held)
        self.assertEqual(7.0, st.flight.phase_entry_ut)
        self.assertEqual(7.0, st.flight.launch_ut)

    def test_a_refused_engage_is_the_named_flake_and_never_stages(self):
        p = _params(mjEngageConfirmFrames=2)
        st, _ = mlib.mfr_decide(mlib.mfr_initial_state(p), snap(ut=5.0))
        st, a1 = mlib.mfr_decide(st, snap(ut=6.0))
        st, a2 = mlib.mfr_decide(st, snap(ut=7.0))
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MFR_MJ_NOT_ENABLED_GIVEUP, st.flake_reason)
        self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, _kinds(a1 + a2))

    def test_a_pad_loss_while_held_is_an_assert_fail(self):
        p = _params()
        st, _ = mlib.mfr_decide(mlib.mfr_initial_state(p), snap(ut=5.0))
        st, _ = mlib.mfr_decide(st, snap(ut=6.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)

    def test_the_fake_run_flakes_on_the_pad_without_staging(self):
        frames = [snap(ut=1.0 + i, altitude=70.0, situation="PRE_LAUNCH", body="Kerbin")
                  for i in range(8)]
        control = FakeMissionControl(frames)
        code, result = run(b5_mun_flyby_return.SPEC, PARAMS, control)
        self.assertEqual("b5_mun_flyby_return", result["mission"])
        self.assertEqual(mlib.MISSION_FLAKE, result["verdict"])
        self.assertIn(mlib.MFR_MJ_NOT_ENABLED_GIVEUP, result["reason"])
        self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, _kinds(control.actions))
        self.assertIn(mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT, _kinds(control.actions))


class ParamsTests(unittest.TestCase):
    def test_the_b4_delegate_reads_the_return_keys(self):
        p = _params()
        self.assertEqual(30000.0, p.home.deorbit_periapsis)
        self.assertEqual(12000.0, p.home.chute_deploy_alt)
        self.assertEqual(1, p.home.reentry_stage_count)
        self.assertEqual(70000.0, p.home.warp_above_alt)
        self.assertEqual(("LANDED", "SPLASHED"), p.home.landed_situations)
        self.assertEqual(900.0, p.home.descent_timeout)

    def test_b5_keys_of_the_same_name_do_not_reach_the_b4_delegate(self):
        # B5's maxAttitudeErrorDeg (its correction burner) and descentTimeoutSeconds
        # (its landing tail) must not retune B4.
        p = _params(maxAttitudeErrorDeg=30, descentTimeoutSeconds=5)
        self.assertEqual(5.0, p.home.max_attitude_error_deg)
        self.assertEqual(900.0, p.home.descent_timeout)
        self.assertEqual(30.0, p.flyby.max_attitude_error_deg)

    def test_every_return_key_is_read(self):
        # The return keys are read by the shared builder mfr_params_from_dict calls
        # (return_tail_param_kwargs, shared with CS-6's mor machine).
        src = (inspect.getsource(mlib.mfr_params_from_dict)
               + inspect.getsource(mlib.return_tail_param_kwargs)
               + repr(mlib.MFR_HOME_KEY_MAP))
        for key in MISSION_KEYS:
            self.assertIn('"%s"' % key if '"%s"' % key in src else "'%s'" % key, src, key)

    def test_a_b5_mode_the_tail_cannot_follow_is_refused(self):
        for key in ("captureEnabled", "landingEnabled", "interplanetaryTransfer"):
            with self.subTest(key=key):
                with self.assertRaises(ValueError):
                    mlib.mfr_params_from_dict(dict(PARAMS, **{key: True}))
        with self.assertRaises(ValueError):
            mlib.mfr_params_from_dict(dict(PARAMS, returnBodyName="Sun"))

    def test_the_window_must_be_ordered_and_below_the_atmosphere_gate(self):
        for over in ({"returnPeriapsisMinMeters": 35000},
                     {"returnPeriapsisMaxMeters": 25000},
                     {"returnPeriapsisMaxMeters": 80000},
                     {"returnTrimFineThrottle": 0.5}):
            with self.subTest(over=over):
                with self.assertRaises(ValueError):
                    _params(**over)

    def test_a_setter_without_the_engage_gate_is_refused(self):
        for key, value in (("autostageLimit", 2), ("ascentType", 0)):
            with self.subTest(key=key):
                with self.assertRaises(ValueError):
                    mlib.mfr_params_from_dict(dict(PLAIN_PARAMS, **{key: value}))


class TrimTests(unittest.TestCase):
    def test_direction_heading_and_cut_are_pure_rules(self):
        p = _params()
        self.assertEqual("", mlib.mfr_trim_direction(float("nan"), p))
        self.assertEqual(mlib.MFR_TRIM_NONE, mlib.mfr_trim_direction(30000.0, p))
        self.assertEqual(mlib.MFR_TRIM_LOWER, mlib.mfr_trim_direction(900000.0, p))
        self.assertEqual(mlib.MFR_TRIM_RAISE, mlib.mfr_trim_direction(-250000.0, p))
        self.assertEqual(270.0, mlib.mfr_trim_heading(mlib.MFR_TRIM_LOWER, p))
        self.assertEqual(90.0, mlib.mfr_trim_heading(mlib.MFR_TRIM_RAISE, p))
        self.assertTrue(mlib.mfr_trim_cut(mlib.MFR_TRIM_LOWER, 29000.0, p))
        self.assertFalse(mlib.mfr_trim_cut(mlib.MFR_TRIM_LOWER, 31000.0, p))
        self.assertTrue(mlib.mfr_trim_cut(mlib.MFR_TRIM_RAISE, 31000.0, p))
        self.assertFalse(mlib.mfr_trim_cut(mlib.MFR_TRIM_RAISE, float("nan"), p))

    def test_a_clean_return_enters_the_trim_on_the_next_frame(self):
        p = _params()
        st = dataclasses.replace(mlib.mfr_initial_state(p), flight=_returned_flight(p))
        st, actions = mlib.mfr_decide(st, snap(ut=500.0, body="Kerbin"))
        self.assertEqual(mlib.MFR_TRIM, st.block)
        self.assertEqual(mlib.MFR_TRIM, st.phase)
        self.assertEqual([], actions)
        self.assertEqual(500.0, st.phase_entry_ut)

    def test_a_return_whose_b5_rows_failed_never_burns(self):
        p = _params()
        flight = dataclasses.replace(_returned_flight(p), min_target_altitude=1000.0)
        st = dataclasses.replace(mlib.mfr_initial_state(p), flight=flight)
        st, _ = mlib.mfr_decide(st, snap(ut=500.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("flybyPeriapsisFloor", st.loss_reason)

    def test_a_b5_flake_or_loss_is_carried(self):
        p = _params()
        flake = dataclasses.replace(_returned_flight(p), verdict=mlib.MISSION_FLAKE,
                                    flake_phase=mlib.B5_COAST_TO_TARGET,
                                    flake_reason="coast budget")
        st, _ = mlib.mfr_decide(dataclasses.replace(mlib.mfr_initial_state(p),
                                                    flight=flake), snap(ut=1.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.B5_COAST_TO_TARGET, st.flake_phase)
        self.assertIn("coast budget", st.flake_reason)
        loss = dataclasses.replace(_returned_flight(p), verdict=mlib.MISSION_ASSERT_FAIL,
                                   loss_reason="vessel-lost")
        st, _ = mlib.mfr_decide(dataclasses.replace(mlib.mfr_initial_state(p),
                                                    flight=loss), snap(ut=1.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("flyby leg: vessel-lost", st.loss_reason)

    def test_a_lower_trim_burns_coarse_then_fine_and_cuts_into_the_home_leg(self):
        st = _at(mlib.MFR_TRIM)
        st, a = mlib.mfr_decide(st, snap(ut=1001.0, periapsis=900000.0))
        self.assertEqual([mlib.ACTION_AP_SET_PITCH_HEADING], _kinds(a))
        self.assertEqual((0.0, 270.0), a[0].pitch_heading)
        # Not settled yet, then settled but not aligned.
        st, a = mlib.mfr_decide(st, snap(ut=1005.0, periapsis=900000.0, ap_error=1.0))
        self.assertEqual([], a)
        st, a = mlib.mfr_decide(st, snap(ut=1011.0, periapsis=900000.0, ap_error=-20.0))
        self.assertEqual([], a)
        st, a = mlib.mfr_decide(st, snap(ut=1012.0, periapsis=900000.0, ap_error=-2.0))
        self.assertEqual([Action(mlib.ACTION_SET_THROTTLE, 0.25)], a)
        st, a = mlib.mfr_decide(st, snap(ut=1013.0, periapsis=600000.0,
                                         available_thrust=50000.0))
        self.assertEqual([], a)
        st, a = mlib.mfr_decide(st, snap(ut=1014.0, periapsis=250000.0,
                                         available_thrust=50000.0))
        self.assertEqual([Action(mlib.ACTION_SET_THROTTLE, 0.03)], a)
        st, a = mlib.mfr_decide(st, snap(ut=1015.0, periapsis=29000.0,
                                         available_thrust=50000.0))
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_DISENGAGE], _kinds(a))
        self.assertEqual(mlib.MFR_HOME, st.block)
        self.assertEqual(mlib.MFR_HOME_PREFIX + mlib.B4_REENTRY, st.phase)
        self.assertEqual(29000.0, st.trim_cut_periapsis)
        self.assertEqual(mlib.B4_REENTRY, st.home.phase)
        self.assertEqual(1, st.home.stages_owed)
        self.assertEqual(1015.0, st.home.last_stage_ut)

    def test_a_raise_trim_burns_prograde(self):
        st = _at(mlib.MFR_TRIM)
        st, a = mlib.mfr_decide(st, snap(ut=1001.0, periapsis=-250000.0))
        self.assertEqual((0.0, 90.0), a[0].pitch_heading)
        self.assertEqual(mlib.MFR_TRIM_RAISE, st.trim_direction)

    def test_a_periapsis_already_in_the_window_skips_the_burn(self):
        st, a = mlib.mfr_decide(_at(mlib.MFR_TRIM), snap(ut=1001.0, periapsis=33000.0))
        self.assertEqual([], a)
        self.assertEqual(mlib.MFR_HOME, st.block)
        self.assertEqual(mlib.MFR_TRIM_NONE, st.trim_direction)
        self.assertEqual(33000.0, st.trim_cut_periapsis)

    def test_an_unreadable_periapsis_waits(self):
        st, a = mlib.mfr_decide(_at(mlib.MFR_TRIM), snap(ut=1001.0, periapsis=float("nan")))
        self.assertEqual([], a)
        self.assertEqual("", st.trim_direction)
        self.assertEqual(mlib.MFR_TRIM, st.block)

    def _burning(self):
        return _at(mlib.MFR_TRIM, trim_direction=mlib.MFR_TRIM_LOWER, trim_heading=270.0,
                   trim_burn_started=True)

    def test_an_engine_that_never_lights_is_the_named_loss(self):
        st = self._burning()
        for i in range(3):
            st, a = mlib.mfr_decide(st, snap(ut=1020.0 + i, periapsis=900000.0,
                                             available_thrust=0.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn(mlib.MFR_TRIM_NOT_LIT_GIVEUP, st.loss_reason)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_DISENGAGE], _kinds(a))

    def test_running_dry_short_of_the_window_is_the_named_loss(self):
        st = self._burning()
        st, _ = mlib.mfr_decide(st, snap(ut=1020.0, periapsis=900000.0,
                                         available_thrust=50000.0))
        st, _ = mlib.mfr_decide(st, snap(ut=1021.0, periapsis=800000.0,
                                         available_thrust=0.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn(mlib.MFR_TRIM_DRY_GIVEUP, st.loss_reason)

    def test_the_trim_budget_flakes_and_stops_the_burn(self):
        st, a = mlib.mfr_decide(self._burning(), snap(ut=1000.0 + 901.0,
                                                      periapsis=900000.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.MFR_TRIM, st.flake_phase)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_AP_DISENGAGE], _kinds(a))


class FallAndHomeTests(unittest.TestCase):
    def test_the_fall_target_rule(self):
        p = _params()
        self.assertEqual((1000.0 + 20000.0 - 600.0, ""),
                         mlib.mfr_fall_target(1000.0, 9.0e6, 20000.0, p))
        self.assertIsNone(mlib.mfr_fall_target(1000.0, 9.0e6, float("nan"), p)[0])
        self.assertIsNone(mlib.mfr_fall_target(1000.0, 150000.0, 20000.0, p)[0])
        self.assertIsNone(mlib.mfr_fall_target(1000.0, 9.0e6, 700.0, p)[0])

    def test_b4_drops_the_stage_then_the_fall_warps_once(self):
        st = _at(mlib.MFR_HOME)
        st = dataclasses.replace(st, home=dataclasses.replace(
            st.home, stages_owed=1, last_stage_ut=1000.0))
        st, a = mlib.mfr_decide(st, snap(ut=1003.0, altitude=9.0e6, vertical_speed=-50.0,
                                         time_to_periapsis=20000.0))
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], _kinds(a))
        self.assertEqual(0, st.home.stages_owed)
        st, a = mlib.mfr_decide(st, snap(ut=1004.0, altitude=8.99e6, vertical_speed=-50.0,
                                         time_to_periapsis=19999.0))
        self.assertEqual([Action(mlib.ACTION_WARP_TO_UT, 1004.0 + 19999.0 - 600.0)], a)
        self.assertEqual(mlib.MFR_FALL, st.block)
        self.assertEqual(st.fall_target_ut, st.warp_to_cmd)

    def test_the_fall_holds_while_warping_then_restamps_b4s_clock(self):
        st = _at(mlib.MFR_FALL, fall_target_ut=20000.0, fall_issues=1, warp_to_cmd=20000.0)
        st, a = mlib.mfr_decide(st, snap(ut=8000.0, warping_to=20000.0))
        self.assertEqual([], a)
        self.assertEqual(mlib.MFR_FALL, st.block)
        st, a = mlib.mfr_decide(st, snap(ut=19998.0, altitude=900000.0))
        self.assertEqual([], a)
        self.assertEqual(mlib.MFR_HOME, st.block)
        self.assertTrue(st.fall_done)
        self.assertIsNone(st.warp_to_cmd)
        self.assertEqual(19998.0, st.home.phase_entry_ut)

    def test_a_stalled_fall_reissues_then_gives_up_by_name(self):
        st = _at(mlib.MFR_FALL, fall_target_ut=20000.0, fall_issues=1, warp_to_cmd=20000.0)
        issued = 0
        for i in range(mlib.MFR_FALL_STALL_FRAMES * mlib.MFR_FALL_REISSUE_LIMIT + 5):
            st, a = mlib.mfr_decide(st, snap(ut=2000.0 + i))
            issued += _kinds(a).count(mlib.ACTION_WARP_TO_UT)
            if st.done:
                break
        self.assertEqual(mlib.MFR_FALL_REISSUE_LIMIT - 1, issued)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MFR_FALL_STALLED_GIVEUP, st.flake_reason)

    def test_an_unreadable_clock_skips_the_fall_and_b4_hops(self):
        st = _at(mlib.MFR_HOME)
        st, a = mlib.mfr_decide(st, snap(ut=1003.0, altitude=9.0e6, vertical_speed=-50.0))
        self.assertTrue(st.fall_done)
        self.assertIn("unreadable", st.fall_skip_reason)
        self.assertEqual([mlib.ACTION_WARP_TO], _kinds(a))

    def test_b4s_splashdown_is_the_clean_terminal(self):
        st = _at(mlib.MFR_HOME, fall_done=True)
        st = dataclasses.replace(st, home=dataclasses.replace(
            st.home, phase=mlib.B4_SPLASHDOWN, chute_deployed=True))
        st, _ = mlib.mfr_decide(st, snap(ut=1100.0, altitude=0.0, situation="SPLASHED"))
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(mlib.MFR_TERMINAL_PHASE, st.phase)

    def test_a_b4_flake_is_carried_behind_the_prefix(self):
        st = _at(mlib.MFR_HOME, fall_done=True)
        st = dataclasses.replace(st, home=dataclasses.replace(
            st.home, phase=mlib.B4_SPLASHDOWN))
        st, _ = mlib.mfr_decide(st, snap(ut=1000.0 + 901.0, altitude=500.0,
                                         situation="FLYING"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual(mlib.MFR_HOME_PREFIX + mlib.B4_SPLASHDOWN, st.flake_phase)


class EvaluateTests(unittest.TestCase):
    def test_the_rows_and_a_clean_return(self):
        p = _params()
        home = dataclasses.replace(mlib.b4_initial_state(p.home), phase=mlib.B4_SPLASHDOWN,
                                   craft_chute_full_seen=True, chute_deployed=True)
        st = _at(mlib.MFR_HOME, home=home, trim_cut_periapsis=29500.0,
                 trim_direction=mlib.MFR_TRIM_LOWER)
        rows = mlib.evaluate_mfr_assertions([snap(situation="SPLASHED")], p, st)
        self.assertEqual(["reachedOrbit", "reachedTargetSoi", "flybyPeriapsisFloor",
                          "returnedToHome", "returnPeriapsisInWindow", "landedSituation",
                          "craftCanopyObserved"], [r.name for r in rows])
        self.assertTrue(all(r.met for r in rows), [r.name for r in rows if not r.met])

    def test_a_return_that_never_reached_the_tail_fails_its_rows(self):
        p = _params()
        st = mlib.mfr_initial_state(p)
        rows = {r.name: r for r in mlib.evaluate_mfr_assertions([], p, st)}
        for name in ("returnPeriapsisInWindow", "landedSituation", "craftCanopyObserved"):
            self.assertFalse(rows[name].met, name)


class ShellTests(unittest.TestCase):
    def test_the_control_is_b5s_plus_the_two_tail_reads(self):
        mine = b5_mun_flyby_return.make_control()
        b5 = b5_mun_flyby.make_control()
        self.assertTrue(mine._read_chute)
        self.assertTrue(mine._read_periapsis)
        self.assertFalse(mine._tolerate_unreadable_nodes)
        self.assertTrue(mine._use_mechjeb)
        a, b = dict(vars(mine)), dict(vars(b5))
        for key in ("_read_chute", "_read_periapsis", "_client_name"):
            a.pop(key)
            b.pop(key)
        plain = (bool, int, float, str, type(None))
        for key, value in b.items():
            if isinstance(value, plain):
                self.assertEqual(value, a[key], key)

    def test_the_spec_shape(self):
        spec = b5_mun_flyby_return.SPEC
        self.assertEqual("b5_mun_flyby_return", spec.name)
        self.assertTrue(spec.allow_rails_warp)
        self.assertEqual(4.0, spec.max_physics_warp)
        state = spec.build_state(PARAMS)
        self.assertIsInstance(state, mlib.MfrState)
        self.assertTrue(hasattr(state, "warp_to_cmd"))
        self.assertIs(state.flight, mlib.frozen_detector_holder(state))

    def test_flighteff_names_the_machine(self):
        import flighteff
        self.assertEqual("mfr_decide", flighteff.MISSION_MACHINES["b5_mun_flyby_return"])


class SchemaAndSpecTests(unittest.TestCase):
    def test_every_b5_key_is_declared_identically(self):
        b5 = _schema("b5_mun_flyby")["params"]
        mine = _schema("b5_mun_flyby_return")["params"]
        for key, decl in b5.items():
            self.assertEqual(decl, mine.get(key), key)

    def test_the_extra_keys_are_exactly_the_missions_own(self):
        b5 = _schema("b5_mun_flyby")["params"]
        mine = _schema("b5_mun_flyby_return")["params"]
        self.assertEqual(MISSION_KEYS, set(mine) - set(b5))

    def test_the_cs5_params_validate_and_build(self):
        import hlib
        spec = _cs5_spec()
        self.assertEqual("b5_mun_flyby_return", spec["driver"]["mission"])
        params = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(
            params, _schema("b5_mun_flyby_return")))
        p = mlib.mfr_params_from_dict(params)
        self.assertGreater(p.mj_engage_confirm_frames, 0)
        self.assertEqual(0, p.ascent_type)
        # The return window sits inside the atmosphere and around the target.
        self.assertLess(p.return_periapsis_max, p.home.warp_above_alt)
        self.assertLess(p.home.chute_deploy_alt, p.return_periapsis_min)
        # A distant flyby, inside the Mun's SOI (2,429,559 m).
        self.assertLess(p.flyby.course_correct_periapsis, 2429559.0)
        self.assertGreater(p.flyby.course_correct_periapsis, p.flyby.target_periapsis_floor)

    def test_the_spec_flies_the_mission_once_and_recovers_the_flyer(self):
        spec = _cs5_spec()
        steps = spec["driver"]["steps"]
        self.assertEqual(1, sum(1 for s in steps if s.get("phase") == "mission"))
        mission = next(i for i, s in enumerate(steps) if s.get("phase") == "mission")
        launch = steps[mission - 1]
        self.assertEqual("LaunchFromEditor", launch["cmd"])
        recover = steps[mission + 1]
        self.assertEqual("Recover", recover["cmd"])
        self.assertEqual("${%s.pid}" % launch["label"], recover["args"]["pid"])
        self.assertEqual("CS Mun Flyby", steps[mission - 2]["args"]["craft"])

    def test_milestone_pins_are_body_qualified(self):
        required = _cs5_spec()["expectations"]["logContracts"]["required"]
        lines = [r for r in required if r.startswith("Milestone enriched: ")]
        self.assertTrue(any(l.startswith("Milestone enriched: 'Mun/Flyby' ") for l in lines),
                        lines)
        for line in lines:
            self.assertRegex(line, r"^Milestone enriched: '[A-Z][A-Za-z]+/[A-Za-z]+'")


if __name__ == "__main__":
    unittest.main()
