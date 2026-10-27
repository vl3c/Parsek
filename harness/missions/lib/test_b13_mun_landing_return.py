"""Unit cells for mission `b13_mun_landing_return` (CS-7) and its composed machine.

The mission's claim is "B5's machine verbatim with captureEnabled and landingEnabled
to its landed-dwell exit, the seam's EvaExit / PlantFlag / EvaBoard from inside the
flight, a Mun ascent, then mor's departure and mfr's return tail verbatim". Each class
pins one part:

  1. THE DELEGATES ARE VERBATIM. Over B13's own landing fixture the landing leg emits
     exactly what b5_decide emits until the landed-dwell exit frame, where only the
     mid-mission CommitTree is replaced by the EvaExit. The departure and the tail are
     mor's and mfr's own functions, run on MlrState because it carries every MorState
     field.
  2. THE PARAMS: the modes the surface visit cannot follow are refused.
  3. THE SURFACE VISIT: each verb gated on its own tag, each refusal by name.
  4. THE ASCENT: the engage gate, the liftoff window, the orbit read, the
     circularization fallback, each give-up by name.
  5. THE ELECTRIC-CHARGE WATCHDOG: where it reads and where it does not.
  6. THE WHOLE FLIGHT over the fake seam, the rows, the shell, the schema, the spec,
     the runner's rebind action and the seam poll windows.

NO krpc, NO KSP, NO network. Import path matches the sibling suites.
"""

import dataclasses
import inspect
import os
import re
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
import mission_runner              # noqa: E402
import b11_mun_orbit_return        # noqa: E402
import b13_mun_landing             # noqa: E402
import b13_mun_landing_return      # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import (B13_PARAMS, B13LandingShellTests,  # noqa: E402
                         FakeMissionControl, run, snap)
from test_b11_mun_orbit_return import (CAREER_PARAMS, DEPART_PARAMS,  # noqa: E402
                                       _return_tail_frames)
from test_b5_mun_flyby_return import RETURN_PARAMS  # noqa: E402

CS7_SPEC = "CS-7-career-mun-landing"
CS6B_SPEC = "CS-6b-career-astronaut-complex"

SURFACE_PARAMS = {
    "evaSettleSeconds": 5,
    "evaSettleFrames": 3,
    "evaPhaseTimeoutSeconds": 900,
    "munAscentAltitudeMeters": 20000,
    "munAscentMinPeriapsisMeters": 10000,
    "munAscentEngageConfirmFrames": 3,
    "munAscentLiftoffWindowSeconds": 60,
    "munAscentTimeoutSeconds": 900,
    "munCircularizeTimeoutSeconds": 900,
    "electricChargeFloor": 0.5,
    "electricChargeDebounceFrames": 4,
}
PLAIN_PARAMS = dict(B13_PARAMS, **RETURN_PARAMS, **DEPART_PARAMS, **SURFACE_PARAMS)
PARAMS = dict(PLAIN_PARAMS, **CAREER_PARAMS)

_SURFACE_UT = 48_200.0 + 200.0 * 6 + 130.0     # B13's landed-dwell exit frame


def _params(**over):
    return mlib.mlr_params_from_dict(dict(PARAMS, **over))


def _kinds(actions):
    return [a.kind for a in actions]


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


def _spec(name):
    with open(os.path.join(_HARNESS, "scenarios", name + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _landing_frames():
    """B13's own ascent, transfer, capture, park, descent and landed dwell, up to and
    including the dwell-exit frame (B13's last frame, the seam's OK answer, belongs
    to a commit this lane drops)."""
    shell = B13LandingShellTests()
    return (shell._transfer_frames() + shell._capture_frames_to_park()
            + shell._landing_frames()[:-1])


def _at(block, phase, params=None, **over):
    """A machine state in ``block`` at ``phase``, entered at UT 50000."""
    p = params or _params()
    st = mlib.mlr_initial_state(p)
    st = dataclasses.replace(st, block=block, phase=phase, phase_entry_ut=50000.0)
    return dataclasses.replace(st, **over)


def _answer(tag, result="OK", payload=(), **kw):
    fields = dict(ut=50001.0, body="Mun", situation="LANDED", liquid_fuel=0.0,
                  electric_charge=float("nan"), seam_command_tag=tag,
                  seam_command_result=result, seam_command_payload=tuple(payload))
    fields.update(kw)
    return snap(**fields)


_EXIT_PAYLOAD = (("kerbal", "Jebediah Kerman"), ("evaPid", "4242"), ("released", "true"),
                 ("standoff", "off"))
_FLAG_PAYLOAD = (("flagSite", "Jebediah Kerman's Flag"), ("body", "Mun"),
                 ("lat", "0.77"), ("lon", "-124.69"))
_BOARD_PAYLOAD = (("kerbal", "Jebediah Kerman"), ("boardedPid", "1700000000"))


def _lander(ut, **kw):
    fields = dict(ut=ut, body="Mun", situation="LANDED", liquid_fuel=300.0,
                  electric_charge=45.0, altitude=3.0)
    fields.update(kw)
    return snap(**fields)


def _surface_frames(t0):
    """The three seam answers and the settle on the lander, from the EvaExit sent at
    ``t0``: EVA-EXIT -> EVA-FLAG -> EVA-BOARD -> EVA-SETTLE -> MUN-ASCENT."""
    return [
        _answer(mlib.MLR_TAG_EVA_EXIT, payload=_EXIT_PAYLOAD, ut=t0 + 20.0),
        _answer(mlib.MLR_TAG_FLAG, payload=_FLAG_PAYLOAD, ut=t0 + 60.0),
        _answer(mlib.MLR_TAG_BOARD, payload=_BOARD_PAYLOAD, ut=t0 + 80.0),
        _lander(t0 + 81.0, seam_command_tag=mlib.MLR_TAG_BOARD, seam_command_result="OK"),
        _lander(t0 + 82.0),
        _lander(t0 + 83.0),
        _lander(t0 + 86.0),                                   # streak 3, 5 s -> ASCENT
    ]


def _ascent_frames(t0):
    """Engage read back, liftoff, climb, MechJeb's release in a 20 km orbit."""
    return [
        _lander(t0 + 1.0, mj_autopilot_enabled=True),           # engaged
        _lander(t0 + 3.0, mj_autopilot_enabled=True, situation="FLYING",
                altitude=50.0, vertical_speed=12.0),            # lifted
        _lander(t0 + 120.0, mj_autopilot_enabled=True, situation="SUB_ORBITAL",
                altitude=15000.0, apoapsis=20000.0, periapsis=-150000.0),
        _lander(t0 + 300.0, mj_autopilot_enabled=False, situation="ORBITING",
                altitude=20000.0, apoapsis=20300.0, periapsis=19500.0,
                eccentricity=0.002),                            # -> DEPART-PLAN
    ]


def _depart_frames(t0):
    """Plan -> burn -> coast warp -> the Kerbin SOI (mor's shape, shifted to t0)."""
    bound = dict(apoapsis=20300.0, periapsis=19500.0, eccentricity=0.002,
                 situation="ORBITING", body="Mun", electric_charge=40.0,
                 liquid_fuel=120.0)
    node_ut = t0 + 800.0
    target = t0 + 810.0 + 9000.0 - 30.0
    return [
        snap(ut=t0 + 10.0, altitude=20000.0, node_count=1, node_dv=280.0,
             node_ut=node_ut, **bound),                                 # -> BURN
        snap(ut=t0 + 700.0, altitude=20010.0, node_count=1, node_dv=280.0,
             node_ut=node_ut, node_executor_enabled=1, **bound),        # holds
        snap(ut=t0 + 810.0, altitude=25000.0, apoapsis=-3_000_000.0, periapsis=19_000.0,
             eccentricity=1.3, situation="ESCAPING", body="Mun", node_count=0,
             next_body="Kerbin", next_pe=31_000.0, time_to_soi=9_000.0,
             electric_charge=39.0),                                     # -> COAST
        snap(ut=t0 + 811.0, altitude=26000.0, apoapsis=-3_000_000.0, eccentricity=1.3,
             situation="ESCAPING", body="Mun", next_body="Kerbin", next_pe=31_000.0,
             time_to_soi=8_999.0, electric_charge=39.0),                # warp
        snap(ut=t0 + 4000.0, altitude=1_800_000.0, apoapsis=-3_000_000.0,
             eccentricity=1.3, situation="ESCAPING", body="Mun", next_body="Kerbin",
             next_pe=31_000.0, time_to_soi=5_810.0, warping_to=target,
             electric_charge=39.0),                                     # warping
        snap(ut=target - 2.0, altitude=2_400_000.0, apoapsis=-3_000_000.0,
             eccentricity=1.3, situation="ESCAPING", body="Mun", next_body="Kerbin",
             next_pe=31_000.0, time_to_soi=32.0, electric_charge=39.0),  # arrived
        snap(ut=target + 30.0, altitude=11_300_000.0, apoapsis=11_900_000.0,
             periapsis=31_000.0, eccentricity=0.9, situation="ORBITING",
             body="Kerbin", electric_charge=39.0),                      # -> TRIM
    ]


def _whole_flight():
    frames = _landing_frames()
    t_surface = frames[-1].ut
    frames += _surface_frames(t_surface)
    t_ascent = frames[-1].ut
    frames += _ascent_frames(t_ascent)
    t_depart = frames[-1].ut
    frames += _depart_frames(t_depart)
    t_home = frames[-1].ut
    # At the SOI frame the trim reads 31 km, inside the window, and skips; the tail's
    # stage-drop and fall frames follow (mor's FullFlightTests shape).
    frames += [snap(ut=t_home + 1.0, altitude=11_290_000.0, periapsis=31_000.0,
                    body="Kerbin")]
    frames += _return_tail_frames(t_home - 14.0, periapsis=31_000.0,
                                  stowed=mlib.CHUTE_STATE_STOWED,
                                  deployed=mlib.CHUTE_STATE_DEPLOYED)[6:]
    return frames


class DelegatesAreVerbatimTests(unittest.TestCase):
    def test_the_landing_leg_is_b5_until_the_landed_dwell_exit_frame(self):
        p = mlib.mlr_params_from_dict(PLAIN_PARAMS)
        st = mlib.mlr_initial_state(p)
        b5 = mlib.b5_initial_state(p.orbit)
        frames = _landing_frames()
        for frame in frames[:-1]:
            st, got = mlib.mlr_decide(st, frame)
            b5, want = mlib.b5_decide(b5, frame)
            self.assertEqual(want, got)
            self.assertEqual(b5, st.flight)
            self.assertEqual(b5.phase, st.phase)
        self.assertEqual(mlib.B5_LANDED_SETTLE, st.phase)
        # The dwell exit frame: B5 asks for the commit; the composer sends the EvaExit
        # in its place and keeps everything else B5 emitted.
        st, got = mlib.mlr_decide(st, frames[-1])
        b5, want = mlib.b5_decide(b5, frames[-1])
        self.assertEqual(mlib.B5_SURFACE_COMMIT, b5.phase)
        self.assertIn(mlib.ACTION_PARSEK_COMMIT_TREE, _kinds(want))
        self.assertEqual([a for a in want if a.kind != mlib.ACTION_PARSEK_COMMIT_TREE]
                         + mlib.mlr_eva_actions("EvaExit", p), got)
        self.assertEqual(b5, st.flight)
        self.assertEqual((mlib.MLR_EVA, mlib.MLR_EVA_EXIT), (st.block, st.phase))
        self.assertNotIn(mlib.B5_SURFACE_COMMIT, st.phases_reached)
        self.assertEqual(frames[-1].ut, st.surface_ut)

    def test_the_departure_and_tail_blocks_run_on_mlr_state(self):
        mor_state = {f.name for f in dataclasses.fields(mlib.MorState)}
        mlr_state = {f.name for f in dataclasses.fields(mlib.MlrState)}
        self.assertEqual(set(), mor_state - mlr_state)
        mor_params = {f.name for f in dataclasses.fields(mlib.MorParams)}
        mlr_params = {f.name for f in dataclasses.fields(mlib.MlrParams)}
        self.assertEqual(set(), mor_params - mlr_params)

    def test_the_departure_is_mors_own(self):
        # The same plan frame from the same DEPART-PLAN entry gives the same actions.
        frame = snap(ut=50002.0, body="Mun", node_count=1, node_dv=280.0, node_ut=51000.0)
        mor = dataclasses.replace(
            mlib.mor_initial_state(mlib.mor_params_from_dict(
                dict(PARAMS, landingEnabled=False))),
            block=mlib.MOR_DEPART, phase=mlib.MOR_DEPART_PLAN, phase_entry_ut=50000.0,
            depart_plan_attempts=1, depart_last_plan_ut=50000.0)
        mlr = _at(mlib.MOR_DEPART, mlib.MOR_DEPART_PLAN, depart_plan_attempts=1,
                  depart_last_plan_ut=50000.0)
        mor, a = mlib.mor_decide(mor, frame)
        mlr, b = mlib.mlr_decide(mlr, frame)
        self.assertEqual(a, b)
        self.assertEqual(mor.phase, mlr.phase)

    def test_no_private_b4_b5_helper_is_called(self):
        src = "".join(inspect.getsource(f) for f in (
            mlib._mlr_land, mlib._mlr_eva, mlib._mlr_ascent, mlib._mlr_enter_depart,
            mlib.mlr_decide))
        self.assertIn("b5_decide(", src)
        self.assertEqual([], re.findall(r"(?<![A-Za-z0-9])_b[45]_\w+", src))

    def test_the_launch_gate_is_the_career_one(self):
        st, actions = mlib.mlr_decide(mlib.mlr_initial_state(_params()),
                                      snap(ut=5.0, situation="PRE_LAUNCH", body="Kerbin"))
        self.assertEqual([mlib.ACTION_MJ_SET_TARGET_APOAPSIS, mlib.ACTION_MJ_ENABLE_AUTOSTAGE,
                          mlib.ACTION_MJ_SET_ASCENT_TYPE, mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT,
                          mlib.ACTION_MJ_ENGAGE_ASCENT], _kinds(actions))
        self.assertTrue(st.stage_held)
        st, actions = mlib.mlr_decide(st, snap(ut=7.0, mj_autopilot_enabled=True))
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], _kinds(actions))


class ParamsTests(unittest.TestCase):
    def test_the_keys_are_read(self):
        p = _params(munAscentAltitudeMeters=25000, evaKerbalName="Bill Kerman",
                    electricChargeFloor=1.5)
        self.assertEqual(25000.0, p.ascent_altitude)
        self.assertEqual("Bill Kerman", p.eva_kerbal)
        self.assertEqual(1.5, p.ec_floor)
        self.assertTrue(p.orbit.landing_enabled)
        self.assertEqual(30000.0, p.depart_periapsis)
        src = inspect.getsource(mlib.mlr_params_from_dict)
        for key in list(SURFACE_PARAMS) + list(DEPART_PARAMS) + ["evaKerbalName"]:
            self.assertIn('"%s"' % key, src)

    def test_refusals(self):
        bad = ({"landingEnabled": False}, {"captureEnabled": False, "landingEnabled": False},
               {"interplanetaryTransfer": True}, {"targetBodyName": "Kerbin"},
               {"departPeriapsisMeters": 60000}, {"departMaxDvMps": 0},
               {"munAscentMinPeriapsisMeters": 20000},
               {"munAscentEngageConfirmFrames": 0}, {"evaSettleFrames": 0},
               {"electricChargeDebounceFrames": 0},
               {"returnPeriapsisMinMeters": 35000}, {"returnTrimFineThrottle": 0.5})
        for over in bad:
            with self.subTest(over=over):
                with self.assertRaises(ValueError):
                    _params(**over)
        with self.assertRaises(ValueError):
            mlib.mlr_params_from_dict(dict(PLAIN_PARAMS, autostageLimit=2))


class SurfaceVisitTests(unittest.TestCase):
    def test_exit_flag_board_settle_then_the_ascent(self):
        st = _at(mlib.MLR_EVA, mlib.MLR_EVA_EXIT)
        st, a = mlib.mlr_decide(st, _answer(mlib.MLR_TAG_EVA_EXIT, payload=_EXIT_PAYLOAD))
        self.assertEqual(mlib.mlr_eva_actions("PlantFlag", st.params), a)
        self.assertEqual((mlib.MLR_EVA_FLAG, "OK", "Jebediah Kerman", "4242"),
                         (st.phase, st.eva_exit_result, st.eva_kerbal_name, st.eva_pid))
        st, a = mlib.mlr_decide(st, _answer(mlib.MLR_TAG_FLAG, payload=_FLAG_PAYLOAD,
                                            ut=50040.0))
        self.assertEqual(mlib.mlr_eva_actions("EvaBoard", st.params), a)
        self.assertEqual(("Mun", "OK"), (st.flag_body, st.flag_result))
        st, a = mlib.mlr_decide(st, _answer(mlib.MLR_TAG_BOARD, payload=_BOARD_PAYLOAD,
                                            ut=50060.0))
        self.assertEqual([], a)
        self.assertEqual((mlib.MLR_EVA_SETTLE, "1700000000"), (st.phase, st.boarded_pid))
        for ut in (50061.0, 50062.0, 50063.0):
            st, a = mlib.mlr_decide(st, _lander(ut))
            self.assertEqual([], a)
        st, a = mlib.mlr_decide(st, _lander(50066.0))
        self.assertEqual(mlib.mlr_ascent_actions(st.params), a)
        self.assertEqual((mlib.MLR_ASCENT, mlib.MLR_ASCENT), (st.block, st.phase))
        self.assertEqual(50066.0, st.eva_aboard_ut)

    def test_the_exit_carries_release_and_the_named_kerbal(self):
        a = mlib.mlr_eva_actions("EvaExit", _params(evaKerbalName="Bill Kerman"))[0]
        self.assertEqual(("EvaExit", (("release", "true"), ("kerbal", "Bill Kerman")),
                          mlib.MLR_TAG_EVA_EXIT), (a.seam_verb, a.seam_args, a.seam_tag))
        a = mlib.mlr_eva_actions("EvaExit", _params())[0]
        self.assertEqual((("release", "true"),), a.seam_args)
        self.assertEqual(3, len({mlib.MLR_TAG_EVA_EXIT, mlib.MLR_TAG_FLAG,
                                 mlib.MLR_TAG_BOARD}))

    def test_another_commands_answer_is_never_read(self):
        # The previous verb's OK still rides the snapshot: the phase must wait.
        st, a = mlib.mlr_decide(_at(mlib.MLR_EVA, mlib.MLR_EVA_FLAG),
                                _answer(mlib.MLR_TAG_EVA_EXIT, payload=_EXIT_PAYLOAD))
        self.assertEqual([], a)
        self.assertEqual(mlib.MLR_EVA_FLAG, st.phase)
        self.assertFalse(st.done)

    def test_each_refusal_is_named(self):
        for phase, tag, giveup in ((mlib.MLR_EVA_EXIT, mlib.MLR_TAG_EVA_EXIT,
                                    mlib.MLR_EVA_EXIT_REFUSED),
                                   (mlib.MLR_EVA_FLAG, mlib.MLR_TAG_FLAG,
                                    mlib.MLR_FLAG_REFUSED),
                                   (mlib.MLR_EVA_BOARD, mlib.MLR_TAG_BOARD,
                                    mlib.MLR_BOARD_REFUSED)):
            for result in ("ERROR", "TIMEOUT"):
                with self.subTest(phase=phase, result=result):
                    st, a = mlib.mlr_decide(
                        _at(mlib.MLR_EVA, phase),
                        _answer(tag, result=result, payload=(("msg", "flag-lock-stable"),)))
                    self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
                    self.assertIn(giveup, st.flake_reason)
                    self.assertIn("flag-lock-stable", st.flake_reason)
                    self.assertEqual([], a)

    def test_a_kerbal_never_back_aboard_is_named(self):
        st = _at(mlib.MLR_EVA, mlib.MLR_EVA_SETTLE)
        st, _ = mlib.mlr_decide(st, _lander(50010.0, liquid_fuel=0.0))
        self.assertEqual(0, st.eva_settle_streak)
        st, _ = mlib.mlr_decide(st, _lander(50000.0 + 901.0, liquid_fuel=0.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MLR_BOARD_NOT_ABOARD, st.flake_reason)

    def test_aboard_needs_fuel_the_target_and_a_landed_situation(self):
        p = _params()
        self.assertTrue(mlib.mlr_aboard_lander(_lander(1.0), p))
        self.assertFalse(mlib.mlr_aboard_lander(_lander(1.0, liquid_fuel=0.0), p))
        self.assertFalse(mlib.mlr_aboard_lander(_lander(1.0, liquid_fuel=float("nan")), p))
        self.assertFalse(mlib.mlr_aboard_lander(_lander(1.0, body="Kerbin"), p))
        self.assertFalse(mlib.mlr_aboard_lander(_lander(1.0, situation="FLYING"), p))

    def test_a_lost_kerbal_fails(self):
        st, _ = mlib.mlr_decide(_at(mlib.MLR_EVA, mlib.MLR_EVA_FLAG),
                                snap(ut=50001.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)

    def test_the_phase_budget_flakes(self):
        st, _ = mlib.mlr_decide(_at(mlib.MLR_EVA, mlib.MLR_EVA_FLAG),
                                _answer("other", ut=50000.0 + 901.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("timed out", st.flake_reason)


class AscentTests(unittest.TestCase):
    def _ascent(self, **over):
        return _at(mlib.MLR_ASCENT, mlib.MLR_ASCENT, **over)

    def test_engaged_lifted_released_in_orbit_plans_the_departure(self):
        st = self._ascent()
        for frame in _ascent_frames(50000.0)[:-1]:
            st, a = mlib.mlr_decide(st, frame)
            self.assertEqual([], a)
        self.assertTrue(st.ascent_engaged and st.ascent_lifted)
        st, a = mlib.mlr_decide(st, _ascent_frames(50000.0)[-1])
        self.assertEqual([Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES),
                          Action(mlib.ACTION_MJ_PLAN_MOON_RETURN, 30000.0)], a)
        self.assertEqual((mlib.MOR_DEPART, mlib.MOR_DEPART_PLAN), (st.block, st.phase))
        self.assertEqual(19500.0, st.ascent_periapsis)

    def test_a_dead_engage_is_re_issued_then_named(self):
        st = self._ascent()
        issued = 0
        for i in range(3 * (mlib.MLR_ASCENT_REISSUE_LIMIT + 1)):
            st, a = mlib.mlr_decide(st, _lander(50001.0 + i))
            issued += _kinds(a).count(mlib.ACTION_MJ_ENGAGE_ASCENT)
            if st.done:
                break
        self.assertEqual(mlib.MLR_ASCENT_REISSUE_LIMIT, issued)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MLR_ASCENT_NOT_ENABLED, st.flake_reason)

    def test_a_re_engage_rebinds_first(self):
        self.assertEqual([mlib.ACTION_MJ_REBIND_ASCENT, mlib.ACTION_MJ_SET_TARGET_APOAPSIS,
                          mlib.ACTION_MJ_ENGAGE_ASCENT],
                         _kinds(mlib.mlr_ascent_actions(_params())))
        self.assertEqual(20000.0, mlib.mlr_ascent_actions(_params())[1].value)

    def test_an_engaged_autopilot_that_never_lifts_is_named(self):
        st = self._ascent(ascent_engaged=True, ascent_engaged_ut=50001.0)
        st, a = mlib.mlr_decide(st, _lander(50001.0 + 61.0, mj_autopilot_enabled=True))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MLR_ASCENT_NEVER_LIFTED, st.flake_reason)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE], _kinds(a))

    def test_a_release_before_liftoff_goes_back_through_the_gate(self):
        st = self._ascent(ascent_engaged=True, ascent_engaged_ut=50001.0)
        st, a = mlib.mlr_decide(st, _lander(50002.0))
        self.assertFalse(st.ascent_engaged)
        self.assertFalse(st.done)

    def test_a_release_with_a_node_circularizes_then_departs(self):
        st = self._ascent(ascent_engaged=True, ascent_engaged_ut=50001.0,
                          ascent_lifted=True)
        st, a = mlib.mlr_decide(st, _lander(50200.0, situation="SUB_ORBITAL",
                                            apoapsis=20000.0, periapsis=-50000.0,
                                            node_count=1))
        self.assertEqual([mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION], _kinds(a))
        self.assertEqual(mlib.MLR_ASCENT_CIRCULARIZE, st.phase)
        st, a = mlib.mlr_decide(st, _lander(50300.0, situation="ORBITING",
                                            apoapsis=20100.0, periapsis=19800.0,
                                            node_count=1))
        self.assertEqual([], a)
        st, a = mlib.mlr_decide(st, _lander(50301.0, situation="ORBITING",
                                            apoapsis=20100.0, periapsis=19800.0))
        self.assertEqual(mlib.MOR_DEPART_PLAN, st.phase)

    def test_a_release_short_of_orbit_is_named_after_the_debounce(self):
        st = self._ascent(ascent_engaged=True, ascent_engaged_ut=50001.0,
                          ascent_lifted=True)
        for i in range(mlib.MLR_ASCENT_RELEASE_FRAMES):
            st, a = mlib.mlr_decide(st, _lander(50200.0 + i, situation="SUB_ORBITAL",
                                                apoapsis=20000.0, periapsis=-5000.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MLR_ASCENT_NOT_IN_ORBIT, st.flake_reason)

    def test_an_orbit_under_the_floor_is_not_an_orbit(self):
        p = _params()
        self.assertFalse(mlib.mlr_in_target_orbit(
            _lander(1.0, situation="ORBITING", periapsis=9000.0), p))
        self.assertTrue(mlib.mlr_in_target_orbit(
            _lander(1.0, situation="ORBITING", periapsis=10000.0), p))
        self.assertFalse(mlib.mlr_in_target_orbit(
            _lander(1.0, situation="ORBITING", periapsis=float("nan")), p))

    def test_leaving_the_mun_during_the_ascent_fails(self):
        st, _ = mlib.mlr_decide(self._ascent(), _lander(50001.0, body="Kerbin"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)

    def test_the_ascent_budget_flakes(self):
        st, a = mlib.mlr_decide(self._ascent(ascent_engaged=True),
                                _lander(50000.0 + 901.0, mj_autopilot_enabled=True))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("timed out", st.flake_reason)


class ElectricChargeTests(unittest.TestCase):
    def test_a_sustained_low_reading_ends_the_flight_by_name(self):
        st = _at(mlib.MLR_ASCENT, mlib.MLR_ASCENT, ascent_engaged=True,
                 ascent_engaged_ut=50000.0, ascent_lifted=True)
        for i in range(4):
            st, a = mlib.mlr_decide(st, _lander(50001.0 + i, electric_charge=0.1,
                                                mj_autopilot_enabled=True,
                                                situation="FLYING"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MLR_EC_EXHAUSTED, st.flake_reason)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_CANCEL_WARP], _kinds(a))

    def test_a_recovery_resets_the_streak_and_the_minimum_is_kept(self):
        st = _at(mlib.MOR_DEPART, mlib.MOR_DEPART_PLAN)
        for ec in (0.1, 0.1, 0.1, 5.0):
            st, _ = mlib.mlr_ec_step(st, snap(electric_charge=ec))
        self.assertEqual(0, st.ec_low_frames)
        self.assertEqual(0.1, st.ec_min)

    def test_the_eva_and_home_blocks_are_not_read(self):
        for block in (mlib.MLR_EVA, mlib.MFR_HOME, mlib.MFR_FALL):
            st = _at(block, block)
            for _ in range(10):
                st, tripped = mlib.mlr_ec_step(st, snap(electric_charge=0.0))
                self.assertFalse(tripped)
            self.assertEqual(0, st.ec_low_frames)

    def test_an_unread_value_and_a_zero_floor_never_trip(self):
        st = _at(mlib.MLR_ASCENT, mlib.MLR_ASCENT)
        st, tripped = mlib.mlr_ec_step(st, snap(electric_charge=float("nan")))
        self.assertFalse(tripped)
        st = _at(mlib.MLR_ASCENT, mlib.MLR_ASCENT, params=_params(electricChargeFloor=0))
        for _ in range(10):
            st, tripped = mlib.mlr_ec_step(st, snap(electric_charge=0.0))
            self.assertFalse(tripped)
        self.assertEqual(0.0, st.ec_min)


class FullFlightTests(unittest.TestCase):
    def test_the_whole_flight_is_mission_ok_with_every_row_met(self):
        control = FakeMissionControl(_whole_flight())
        code, result = run(b13_mun_landing_return.SPEC, PLAIN_PARAMS, control,
                           budget=900.0)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        self.assertEqual("b13_mun_landing_return", result["mission"])
        kinds = _kinds(control.actions)
        self.assertNotIn(mlib.ACTION_PARSEK_COMMIT_TREE, kinds)
        seam = [a.seam_verb for a in control.actions
                if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND]
        self.assertEqual(["EvaExit", "PlantFlag", "EvaBoard"], seam)
        self.assertEqual(1, kinds.count(mlib.ACTION_MJ_LAND_UNTARGETED))
        self.assertEqual(1, kinds.count(mlib.ACTION_MJ_REBIND_ASCENT))
        self.assertEqual(1, kinds.count(mlib.ACTION_MJ_PLAN_MOON_RETURN))
        # TLI, capture, departure.
        self.assertEqual(3, kinds.count(mlib.ACTION_MJ_EXECUTE_NODES))
        phases = result["phasesReached"]
        for phase in (mlib.B5_PARK, mlib.B5_DESCENT, mlib.B5_LANDED_SETTLE,
                      mlib.MLR_EVA_EXIT, mlib.MLR_EVA_FLAG, mlib.MLR_EVA_BOARD,
                      mlib.MLR_EVA_SETTLE, mlib.MLR_ASCENT, mlib.MOR_DEPART_PLAN,
                      mlib.MOR_DEPART_BURN, mlib.MOR_DEPART_COAST, mlib.MFR_TRIM,
                      mlib.MLR_TERMINAL_PHASE):
            self.assertIn(phase, phases)
        self.assertNotIn(mlib.B5_SURFACE_COMMIT, phases)
        names = [a["name"] for a in result["assertions"]]
        self.assertEqual(["reachedOrbit", "reachedTargetSoi", "flybyPeriapsisFloor",
                          "capturedInTargetOrbit", "parkedStable", "landedOnTargetBody",
                          "landedStable", "evaExited", "flagPlanted", "kerbalBoarded",
                          "ascendedToTargetOrbit", "departedTargetOrbit",
                          "returnPeriapsisInWindow", "landedSituation",
                          "craftCanopyObserved"], names)
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])

    def test_rows_fail_closed_on_a_flight_that_never_landed(self):
        p = _params()
        rows = {r.name: r for r in mlib.evaluate_mlr_assertions(
            [], p, mlib.mlr_initial_state(p))}
        for name in ("landedOnTargetBody", "landedStable", "evaExited", "flagPlanted",
                     "kerbalBoarded", "ascendedToTargetOrbit", "departedTargetOrbit",
                     "returnPeriapsisInWindow", "landedSituation", "craftCanopyObserved"):
            self.assertFalse(rows[name].met, name)
        self.assertNotIn("treeCommitted", rows)

    def test_a_flag_on_another_body_is_not_the_mun_flag(self):
        p = _params()
        st = dataclasses.replace(mlib.mlr_initial_state(p), flag_result="OK",
                                 flag_body="Kerbin")
        rows = {r.name: r for r in mlib.evaluate_mlr_assertions([], p, st)}
        self.assertFalse(rows["flagPlanted"].met)


class ShellTests(unittest.TestCase):
    def test_the_control_is_cs6s_plus_b13s_reads(self):
        mine = b13_mun_landing_return.make_control()
        cs6 = b11_mun_orbit_return.make_control()
        b13 = b13_mun_landing.make_control()
        for key in ("_read_docking", "_read_node_executor", "_read_periapsis",
                    "_read_chute", "_read_landing", "_use_mechjeb"):
            self.assertTrue(getattr(mine, key), key)
            self.assertTrue(getattr(cs6, key) or getattr(b13, key), key)

    def test_the_spec_shape(self):
        spec = b13_mun_landing_return.SPEC
        self.assertEqual("b13_mun_landing_return", spec.name)
        self.assertTrue(spec.allow_rails_warp)
        self.assertEqual(4.0, spec.max_physics_warp)
        state = spec.build_state(PARAMS)
        self.assertIsInstance(state, mlib.MlrState)
        self.assertIs(state.flight, mlib.frozen_detector_holder(state))

    def test_flighteff_names_the_machine(self):
        import flighteff
        self.assertEqual("mlr_decide",
                         flighteff.MISSION_MACHINES["b13_mun_landing_return"])

    def test_each_eva_verb_polls_at_twice_its_seam_budget(self):
        import hlib
        for verb in ("EvaExit", "PlantFlag", "EvaBoard"):
            with self.subTest(verb=verb):
                self.assertEqual(2.0 * hlib.DISPATCH_DEFERRAL_BUDGET_SECONDS[verb],
                                 mlib.seam_command_poll_seconds(verb))


class SchemaAndSpecTests(unittest.TestCase):
    OWN_KEYS = set(SURFACE_PARAMS) | {"evaKerbalName"}

    def test_mor_and_b13_keys_are_declared_identically(self):
        mine = _schema("b13_mun_landing_return")["params"]
        mor = _schema("b11_mun_orbit_return")["params"]
        b13 = _schema("b13_mun_landing")["params"]
        for source in (mor, b13):
            for key, decl in source.items():
                self.assertEqual(decl, mine.get(key), key)
        self.assertEqual(self.OWN_KEYS, set(mine) - set(mor) - set(b13))

    def test_the_cs7_params_validate_and_build(self):
        import hlib
        spec = _spec(CS7_SPEC)
        self.assertEqual("b13_mun_landing_return", spec["driver"]["mission"])
        params = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(
            params, _schema("b13_mun_landing_return")))
        p = mlib.mlr_params_from_dict(params)
        self.assertGreater(p.mj_engage_confirm_frames, 0)
        self.assertTrue(p.orbit.capture_enabled and p.orbit.landing_enabled)
        self.assertTrue(p.orbit.landing_deploy_gears)
        self.assertEqual(p.return_periapsis, p.depart_periapsis)
        self.assertLess(p.return_periapsis_max, p.home.warp_above_alt)
        self.assertLess(p.home.chute_deploy_alt, p.return_periapsis_min)
        self.assertGreater(p.ec_floor, 0.0)
        # The park dwell fits its budget with the stabilization slack B11 keeps.
        self.assertGreaterEqual(p.orbit.park_timeout - p.orbit.park_dwell, 420.0)
        self.assertGreater(p.orbit.landed_timeout, p.orbit.landed_dwell)

    def test_the_spec_buys_the_walk_then_flies_once_and_recovers_the_flyer(self):
        spec = _spec(CS7_SPEC)
        self.assertEqual("fixtures/saves/cs-career-mun2", spec["fixture"]["saveTemplate"])
        steps = spec["driver"]["steps"]
        self.assertEqual(1, sum(1 for s in steps if s.get("phase") == "mission"))
        mission = next(i for i, s in enumerate(steps) if s.get("phase") == "mission")
        launch = steps[mission - 1]
        self.assertEqual("LaunchFromEditor", launch["cmd"])
        recover = steps[mission + 1]
        self.assertEqual("Recover", recover["cmd"])
        self.assertEqual("${%s.pid}" % launch["label"], recover["args"]["pid"])
        self.assertEqual("CS Mun Lander", steps[mission - 2]["args"]["craft"])
        ksc = [s["args"] for s in steps[:mission] if s.get("cmd") == "KscAction"]
        self.assertEqual([{"action": "upgrade-facility",
                           "facility": "SpaceCenter/AstronautComplex"},
                          {"action": "research-node", "node": "advRocketry"}], ksc)
        # CS-6b's walk is the same two actions in the same order.
        walk = [s["args"] for s in _spec(CS6B_SPEC)["driver"]["steps"]
                if s.get("cmd") == "KscAction"]
        self.assertEqual(ksc, walk)
        self.assertFalse(any(s.get("cmd") == "CommitTree" and s.get("expect") == "OK"
                             for s in steps))

    def test_the_milestone_pins(self):
        contracts = _spec(CS7_SPEC)["expectations"]["logContracts"]
        required = contracts["required"]
        lines = [r for r in required if r.startswith("Milestone enriched: ")]
        for mid in ("Mun/Landing", "Mun/SurfaceEVA", "Mun/FlagPlant",
                    "Mun/ReturnFromSurface"):
            self.assertTrue(any(l.startswith("Milestone enriched: '%s' " % mid)
                                for l in lines), mid)
        for line in lines:
            self.assertRegex(line, r"^Milestone enriched: '[A-Z][A-Za-z]+/[A-Za-z]+'")
        forbidden = contracts["forbidden"]
        bare = re.compile(forbidden[0])
        self.assertTrue(bare.search("Milestone enriched: 'FlagPlant' funds=1 rep=1 sci=1"))
        self.assertFalse(bare.search("Milestone enriched: 'Mun/FlagPlant' funds=1"))
        repaid = re.compile(forbidden[1])
        for mid in ("Mun/Flyby", "Mun/Orbit", "Mun/ReturnFromOrbit", "Mun/ReturnFromFlyBy"):
            self.assertTrue(repaid.search("Milestone enriched: '%s' funds=0" % mid), mid)
        for mid in ("Mun/Landing", "Mun/SurfaceEVA", "Mun/FlagPlant",
                    "Mun/ReturnFromSurface"):
            self.assertFalse(repaid.search("Milestone enriched: '%s' funds=0" % mid), mid)
        # The rollout regex is the craft's, never the ground-truth Flea's.
        rollout = re.compile(next(r for r in required if "VesselRollout" in r
                                  and "2932" not in r))
        self.assertFalse(rollout.search("Game state: FundsChanged -2932 (VesselRollout)"))
        self.assertTrue(rollout.search("Game state: FundsChanged -13742 (VesselRollout)"))
        # The seam's own completion lines, as the C# writes them.
        exit_re = re.compile(next(r for r in required if r.startswith("evaexit complete")))
        self.assertTrue(exit_re.search("evaexit complete kerbal=Jebediah Kerman "
                                       "evaPid=123 released=true standoff=off"))


class RunnerRebindTests(unittest.TestCase):
    class _MechJeb:
        def __init__(self, raise_on_read=False):
            self._raise = raise_on_read
            self.reads = 0

        @property
        def ascent_autopilot(self):
            self.reads += 1
            if self._raise:
                raise RuntimeError("no MechJeb core on the active vessel")
            return "fresh-handle"

    class _Vessel:
        control = object()

    class _Conn:
        class _Sc:
            pass
        space_center = _Sc()

    def setUp(self):
        self._Conn.space_center.active_vessel = self._Vessel()

    def _perform(self, mj):
        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True)
        ctrl._mechjeb = mj
        ctrl._ascent = "launch-handle"
        ctrl._conn = self._Conn()
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            ctrl.perform(Action(mlib.ACTION_MJ_REBIND_ASCENT))
        finally:
            mission_runner._stdout_sink = orig
        return ctrl, lines

    def test_the_handle_is_re_read(self):
        ctrl, lines = self._perform(self._MechJeb())
        self.assertEqual("fresh-handle", ctrl._ascent)
        self.assertTrue(any("re-bound" in l for l in lines), lines)

    def test_a_failed_read_keeps_the_launch_handle(self):
        ctrl, lines = self._perform(self._MechJeb(raise_on_read=True))
        self.assertEqual("launch-handle", ctrl._ascent)
        self.assertTrue(any("rebind failed" in l for l in lines), lines)


if __name__ == "__main__":
    unittest.main()
