"""Unit cells for mission `b11_mun_orbit_return` (CS-6) and its composed machine.

The mission's claim is "B5's machine verbatim with captureEnabled to its PARK exit,
a Mun departure, then mfr's return tail verbatim". Each class pins one part:

  1. THE DELEGATES ARE VERBATIM. Over B11's own capture fixture the orbit leg emits
     exactly what b5_decide emits until the PARK exit frame, where only the
     mid-mission CommitTree is replaced by the departure plan. mfr_decide's
     behaviour is pinned frame for frame by a digest taken before its helpers were
     shared, and the tail blocks run on MorState because it carries every MfrState
     field.
  2. THE PARAMS: the modes the departure cannot follow are refused.
  3. THE DEPARTURE: plan, burn and coast, each give-up by name.
  4. THE WHOLE FLIGHT over the fake seam, the rows, the shell, the schema, the spec
     and the runner's moon-return action.

NO krpc, NO KSP, NO network. Import path matches the sibling suites.
"""

import dataclasses
import hashlib
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
import b11_mun_orbit               # noqa: E402
import b11_mun_orbit_return        # noqa: E402
import b5_mun_flyby_return         # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import (B11_PARAMS, B11OrbitShellTests, B5ShellTests,  # noqa: E402
                         FakeMissionControl, run, snap)
from test_b5_mun_flyby_return import PARAMS as MFR_PARAMS  # noqa: E402
from test_b5_mun_flyby_return import PLAIN_PARAMS as MFR_PLAIN_PARAMS  # noqa: E402
from test_b5_mun_flyby_return import RETURN_PARAMS  # noqa: E402

CS6_SPEC = "CS-6-career-mun-orbit"

DEPART_PARAMS = {
    "departPeriapsisMeters": 30000,
    "departMaxDvMps": 600,
    "departPlanTimeoutSeconds": 300,
    "departPlanRetrySeconds": 10,
    "departBurnTimeoutSeconds": 20000,
    "departCoastTimeoutSeconds": 100000,
    "departSoiLeadSeconds": 30,
}
CAREER_PARAMS = {"mjEngageConfirmFrames": 3, "autostageLimit": 2, "ascentType": 0}
PLAIN_PARAMS = dict(B11_PARAMS, **RETURN_PARAMS, **DEPART_PARAMS)
PARAMS = dict(PLAIN_PARAMS, **CAREER_PARAMS)

# The mfr_decide trace digest, taken BEFORE the launch gate, the params builder and
# the tail rows were shared with mor (commit cs6-mun-orbit). A change to mfr's
# behaviour moves it; re-pin only for a deliberate mfr change.
MFR_TRACE_DIGEST = "c12756e93fc71358884c522de5194372e3dd42b18becadcd657d6dbe11e485c8"

_PARK_EXIT_UT = 48_200.0


def _params(**over):
    return mlib.mor_params_from_dict(dict(PARAMS, **over))


def _kinds(actions):
    return [a.kind for a in actions]


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


def _cs6_spec():
    with open(os.path.join(_HARNESS, "scenarios", CS6_SPEC + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _orbit_frames():
    """B11's own transfer + capture fixture, up to and including the dwell-exit frame
    (B11's last frame, the seam's OK answer, belongs to a commit this lane drops)."""
    shell = B11OrbitShellTests()
    return shell._transfer_frames() + shell._capture_frames()[:-1]


def _at(phase, params=None, **over):
    """A machine state in the DEPART block at ``phase``, entered at UT 49000."""
    p = params or _params()
    st = mlib.mor_initial_state(p)
    st = dataclasses.replace(st, block=mlib.MOR_DEPART, phase=phase,
                             phase_entry_ut=49000.0, depart_plan_attempts=1,
                             depart_last_plan_ut=49000.0)
    return dataclasses.replace(st, **over)


def _mfr_trace_digest():
    """sha256 over every (actions, state) mfr_decide returns across a scripted
    CS-5-shaped flight, the params builds and refusals, and the evaluated rows."""
    out = []

    def feed(st, frames):
        for f in frames:
            st, acts = mlib.mfr_decide(st, f)
            out.append(repr(acts))
            out.append(repr(st))
        return st

    for over in ({}, {"returnPeriapsisMinMeters": 35000},
                 {"returnPeriapsisMaxMeters": 80000}, {"returnTrimFineThrottle": 0.5},
                 {"captureEnabled": True}, {"returnBodyName": "Sun"}):
        try:
            out.append(repr(mlib.mfr_params_from_dict(dict(MFR_PARAMS, **over))))
        except ValueError as exc:
            out.append("ValueError: %s" % exc)
    try:
        mlib.mfr_params_from_dict(dict(MFR_PLAIN_PARAMS, autostageLimit=2))
    except ValueError as exc:
        out.append("ValueError: %s" % exc)
    st = mlib.mfr_initial_state(mlib.mfr_params_from_dict(MFR_PARAMS))
    feed(st, [snap(ut=5.0, situation="PRE_LAUNCH", body="Kerbin"),
              snap(ut=6.0, mj_autopilot_enabled=False),
              snap(ut=7.0, mj_autopilot_enabled=True)]
         + [snap(ut=20.0 + i, altitude=5000.0 * (i + 1), vertical_speed=200.0,
                 apoapsis=10000.0 * (i + 1), periapsis=-500000.0, situation="FLYING",
                 body="Kerbin", mj_autopilot_enabled=True) for i in range(5)])
    st = mlib.mfr_initial_state(mlib.mfr_params_from_dict(dict(MFR_PARAMS,
                                                               mjEngageConfirmFrames=2)))
    feed(st, [snap(ut=5.0), snap(ut=6.0), snap(ut=7.0)])
    st = mlib.mfr_initial_state(mlib.mfr_params_from_dict(MFR_PLAIN_PARAMS))
    st = feed(st, B5ShellTests()._happy_frames())
    st = feed(st, _return_tail_frames(80000.0, periapsis=900000.0))
    out.append(repr(mlib.evaluate_mfr_assertions([], st.params, st)))
    return hashlib.sha256("\n".join(out).encode("utf-8")).hexdigest(), st


def _return_tail_frames(t0, periapsis, stowed="STOWED", deployed="DEPLOYED"):
    """mfr's tail from its TRIM entry: a lower trim (when ``periapsis`` is above the
    window), B4's stage drop, the fall warp, the hops, the chute, the splashdown.
    The default chute spellings are the ones the pinned mfr digest was taken over
    (not kRPC's, so the canopy row stays unmet there); the full flight passes the
    real CHUTE_STATE_* readings."""
    return [
        snap(ut=t0 + 1.0, periapsis=periapsis, body="Kerbin", altitude=11e6),
        snap(ut=t0 + 5.0, periapsis=periapsis, ap_error=1.0, body="Kerbin"),
        snap(ut=t0 + 12.0, periapsis=periapsis, ap_error=-2.0, body="Kerbin"),
        snap(ut=t0 + 13.0, periapsis=600000.0, available_thrust=50000.0, body="Kerbin"),
        snap(ut=t0 + 14.0, periapsis=250000.0, available_thrust=50000.0, body="Kerbin"),
        snap(ut=t0 + 15.0, periapsis=29000.0, available_thrust=50000.0, body="Kerbin"),
        snap(ut=t0 + 18.0, altitude=9.0e6, vertical_speed=-50.0, time_to_periapsis=20000.0,
             periapsis=29000.0, body="Kerbin"),
        snap(ut=t0 + 19.0, altitude=8.99e6, vertical_speed=-50.0,
             time_to_periapsis=19999.0, periapsis=29000.0, body="Kerbin"),
        snap(ut=t0 + 10000.0, warping_to=t0 + 19418.0, altitude=5e6, body="Kerbin"),
        snap(ut=t0 + 19416.0, altitude=900000.0, vertical_speed=-1500.0,
             periapsis=29000.0, body="Kerbin", time_to_periapsis=600.0),
        snap(ut=t0 + 19420.0, altitude=890000.0, vertical_speed=-1500.0,
             periapsis=29000.0, body="Kerbin", time_to_periapsis=596.0),
        snap(ut=t0 + 19900.0, altitude=60000.0, vertical_speed=-1500.0,
             periapsis=29000.0, body="Kerbin", situation="FLYING"),
        snap(ut=t0 + 19990.0, altitude=11000.0, vertical_speed=-250.0,
             periapsis=-500000.0, body="Kerbin", situation="FLYING",
             craft_chute_state=stowed),
        snap(ut=t0 + 20000.0, altitude=5000.0, vertical_speed=-80.0, body="Kerbin",
             situation="FLYING", craft_chute_state=deployed),
        snap(ut=t0 + 20010.0, altitude=2000.0, vertical_speed=-7.0, body="Kerbin",
             situation="FLYING", craft_chute_state=deployed),
        snap(ut=t0 + 20011.0, altitude=1990.0, vertical_speed=-7.0, body="Kerbin",
             situation="FLYING", craft_chute_state=deployed),
        snap(ut=t0 + 20012.0, altitude=1980.0, vertical_speed=-7.0, body="Kerbin",
             situation="FLYING", craft_chute_state=deployed),
        snap(ut=t0 + 20300.0, altitude=0.0, vertical_speed=0.0, body="Kerbin",
             situation="SPLASHED", craft_chute_state=deployed),
        snap(ut=t0 + 20301.0, altitude=0.0, vertical_speed=0.0, body="Kerbin",
             situation="SPLASHED", craft_chute_state=deployed),
    ]


def _depart_frames():
    """PARK exit -> plan -> burn -> coast warp -> the Kerbin SOI."""
    bound = dict(apoapsis=1_010_030.0, periapsis=989_970.0, eccentricity=0.01,
                 situation="ORBITING", body="Mun", angular_velocity=0.002)
    return [
        snap(ut=_PARK_EXIT_UT + 10.0, altitude=999_300.0, node_count=1, node_dv=280.0,
             node_ut=49_000.0, **bound),                                   # -> BURN
        snap(ut=48_900.0, altitude=999_200.0, node_count=1, node_dv=280.0,
             node_ut=49_000.0, node_executor_enabled=1, **bound),          # holds
        snap(ut=49_010.0, altitude=1_000_500.0, apoapsis=-3_000_000.0,
             periapsis=990_000.0, eccentricity=1.3, situation="ESCAPING", body="Mun",
             node_count=0, next_body="Kerbin", next_pe=31_000.0,
             time_to_soi=9_000.0),                                          # -> COAST
        snap(ut=49_011.0, altitude=1_001_000.0, apoapsis=-3_000_000.0,
             eccentricity=1.3, situation="ESCAPING", body="Mun", next_body="Kerbin",
             next_pe=31_000.0, time_to_soi=8_999.0),                        # warp
        snap(ut=52_000.0, altitude=1_800_000.0, apoapsis=-3_000_000.0,
             eccentricity=1.3, situation="ESCAPING", body="Mun", next_body="Kerbin",
             next_pe=31_000.0, time_to_soi=6_010.0, warping_to=57_980.0),   # warping
        snap(ut=57_978.0, altitude=2_400_000.0, apoapsis=-3_000_000.0,
             eccentricity=1.3, situation="ESCAPING", body="Mun", next_body="Kerbin",
             next_pe=31_000.0, time_to_soi=32.0),                           # arrived
        snap(ut=58_010.0, altitude=11_300_000.0, apoapsis=11_900_000.0,
             periapsis=31_000.0, eccentricity=0.9, situation="ORBITING",
             body="Kerbin"),                                                # -> TRIM
    ]


class DelegatesAreVerbatimTests(unittest.TestCase):
    def test_the_orbit_leg_is_b5_until_the_park_exit_frame(self):
        p = mlib.mor_params_from_dict(PLAIN_PARAMS)
        st = mlib.mor_initial_state(p)
        b5 = mlib.b5_initial_state(p.orbit)
        frames = _orbit_frames()
        for frame in frames[:-1]:
            st, got = mlib.mor_decide(st, frame)
            b5, want = mlib.b5_decide(b5, frame)
            self.assertEqual(want, got)
            self.assertEqual(b5, st.flight)
            self.assertEqual(b5.phase, st.phase)
        self.assertEqual(mlib.B5_PARK, st.phase)
        # The PARK exit frame: B5 asks for the commit; the composer plans the
        # departure in its place and keeps everything else B5 emitted.
        st, got = mlib.mor_decide(st, frames[-1])
        b5, want = mlib.b5_decide(b5, frames[-1])
        self.assertEqual(mlib.B5_ORBIT_COMMIT, b5.phase)
        self.assertIn(mlib.ACTION_PARSEK_COMMIT_TREE, _kinds(want))
        self.assertEqual([a for a in want if a.kind != mlib.ACTION_PARSEK_COMMIT_TREE]
                         + [Action(mlib.ACTION_MJ_PLAN_MOON_RETURN, 30000.0)], got)
        self.assertEqual(b5, st.flight)
        self.assertEqual((mlib.MOR_DEPART, mlib.MOR_DEPART_PLAN), (st.block, st.phase))
        self.assertNotIn(mlib.B5_ORBIT_COMMIT, st.phases_reached)
        self.assertEqual(frames[-1].ut, st.park_exit_ut)

    def test_mfr_decide_is_unchanged_frame_for_frame(self):
        digest, st = _mfr_trace_digest()
        self.assertEqual(mlib.MFR_TERMINAL_PHASE, st.phase)
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(MFR_TRACE_DIGEST, digest)

    def test_the_tail_blocks_run_on_mor_state(self):
        mfr_state = {f.name for f in dataclasses.fields(mlib.MfrState)}
        mor_state = {f.name for f in dataclasses.fields(mlib.MorState)}
        self.assertEqual(set(), mfr_state - mor_state)
        mfr_params = {f.name for f in dataclasses.fields(mlib.MfrParams)} - {"flyby"}
        mor_params = {f.name for f in dataclasses.fields(mlib.MorParams)}
        self.assertEqual(set(), mfr_params - mor_params)
        self.assertEqual(set(mlib.return_tail_param_kwargs({})), mfr_params)

    def test_the_mor_tail_is_mfrs_tail(self):
        # The same tail frames from the same TRIM entry give the same actions.
        mor = dataclasses.replace(mlib.mor_initial_state(_params()), block=mlib.MFR_TRIM,
                                  phase=mlib.MFR_TRIM, phase_entry_ut=80000.0)
        mfr = dataclasses.replace(
            mlib.mfr_initial_state(mlib.mfr_params_from_dict(MFR_PARAMS)),
            block=mlib.MFR_TRIM, phase=mlib.MFR_TRIM, phase_entry_ut=80000.0)
        for frame in _return_tail_frames(80000.0, periapsis=900000.0):
            mor, a = mlib.mor_decide(mor, frame)
            mfr, b = mlib.mfr_decide(mfr, frame)
            self.assertEqual(b, a)
            self.assertEqual(mfr.phase, mor.phase)
            # repr: B4State holds NaN floats, which never compare equal by value.
            self.assertEqual(repr(mfr.home), repr(mor.home))
        self.assertEqual(mlib.MOR_TERMINAL_PHASE, mor.phase)
        self.assertTrue(mor.done)

    def test_no_private_b4_b5_helper_is_called(self):
        src = "".join(inspect.getsource(f) for f in (
            mlib._mor_orbit, mlib._mor_depart_plan, mlib._mor_depart_burn,
            mlib._mor_depart_coast, mlib.mor_decide))
        self.assertIn("b5_decide(", src)
        self.assertEqual([], re.findall(r"(?<![A-Za-z0-9])_b[45]_\w+", src))


class LaunchGateTests(unittest.TestCase):
    def test_the_career_gate_holds_the_stage_until_enabled(self):
        st, actions = mlib.mor_decide(mlib.mor_initial_state(_params()),
                                      snap(ut=5.0, situation="PRE_LAUNCH", body="Kerbin"))
        self.assertEqual([mlib.ACTION_MJ_SET_TARGET_APOAPSIS, mlib.ACTION_MJ_ENABLE_AUTOSTAGE,
                          mlib.ACTION_MJ_SET_ASCENT_TYPE, mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT,
                          mlib.ACTION_MJ_ENGAGE_ASCENT], _kinds(actions))
        self.assertTrue(st.stage_held)
        st, actions = mlib.mor_decide(st, snap(ut=6.0, mj_autopilot_enabled=False))
        self.assertEqual([], actions)
        st, actions = mlib.mor_decide(st, snap(ut=7.0, mj_autopilot_enabled=True))
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], _kinds(actions))
        self.assertEqual(7.0, st.flight.launch_ut)

    def test_a_refused_engage_is_the_named_flake(self):
        st, _ = mlib.mor_decide(mlib.mor_initial_state(_params(mjEngageConfirmFrames=2)),
                                snap(ut=5.0))
        st, _ = mlib.mor_decide(st, snap(ut=6.0))
        st, _ = mlib.mor_decide(st, snap(ut=7.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MFR_MJ_NOT_ENABLED_GIVEUP, st.flake_reason)


class ParamsTests(unittest.TestCase):
    def test_the_departure_and_tail_keys_are_read(self):
        p = _params(departPeriapsisMeters=25000, departMaxDvMps=450,
                    departSoiLeadSeconds=40)
        self.assertEqual(25000.0, p.depart_periapsis)
        self.assertEqual(450.0, p.depart_max_dv)
        self.assertEqual(40.0, p.depart_soi_lead)
        self.assertTrue(p.orbit.capture_enabled)
        self.assertEqual(30000.0, p.home.deorbit_periapsis)
        self.assertEqual(2, p.autostage_limit)
        src = inspect.getsource(mlib.mor_params_from_dict)
        for key in DEPART_PARAMS:
            self.assertIn('"%s"' % key, src)

    def test_the_aim_defaults_to_the_return_target(self):
        params = dict(PARAMS)
        del params["departPeriapsisMeters"]
        self.assertEqual(30000.0, mlib.mor_params_from_dict(params).depart_periapsis)

    def test_refusals(self):
        bad = ({"captureEnabled": False}, {"landingEnabled": True},
               {"interplanetaryTransfer": True}, {"targetBodyName": "Kerbin"},
               {"departPeriapsisMeters": 60000}, {"departMaxDvMps": 0},
               {"returnPeriapsisMinMeters": 35000}, {"returnTrimFineThrottle": 0.5})
        for over in bad:
            with self.subTest(over=over):
                with self.assertRaises(ValueError):
                    _params(**over)
        with self.assertRaises(ValueError):
            mlib.mor_params_from_dict(dict(PLAIN_PARAMS, autostageLimit=2))


class PureRuleTests(unittest.TestCase):
    def test_dv_verdict(self):
        p = _params()
        self.assertEqual(mlib.MOR_DV_UNREAD, mlib.mor_depart_dv_verdict(float("nan"), p))
        self.assertEqual(mlib.MOR_DV_OVER_CAP, mlib.mor_depart_dv_verdict(600.1, p))
        self.assertEqual(mlib.MOR_DV_FLY, mlib.mor_depart_dv_verdict(600.0, p))

    def test_escaped(self):
        p = _params()
        self.assertTrue(mlib.mor_depart_escaped(snap(body="Kerbin"), p))
        self.assertTrue(mlib.mor_depart_escaped(snap(body="Mun", apoapsis=-1.0), p))
        self.assertTrue(mlib.mor_depart_escaped(snap(body="Mun", apoapsis=float("nan"),
                                                     eccentricity=1.0), p))
        self.assertFalse(mlib.mor_depart_escaped(
            snap(body="Mun", apoapsis=500000.0, eccentricity=0.4), p))
        self.assertFalse(mlib.mor_depart_escaped(
            snap(body="Mun", apoapsis=float("nan"), eccentricity=float("nan")), p))

    def test_coast_target(self):
        p = _params()
        self.assertEqual((1000.0 + 9000.0 - 30.0, ""), mlib.mor_coast_target(1000.0, 9000.0, p))
        self.assertIsNone(mlib.mor_coast_target(1000.0, float("nan"), p)[0])
        self.assertIsNone(mlib.mor_coast_target(1000.0, 80.0, p)[0])


class DepartPlanTests(unittest.TestCase):
    def test_a_flyable_node_goes_to_the_executor(self):
        st, a = mlib.mor_decide(_at(mlib.MOR_DEPART_PLAN),
                                snap(ut=49002.0, body="Mun", node_count=1, node_dv=280.0,
                                     node_ut=50000.0))
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_NODES)], a)
        self.assertEqual(mlib.MOR_DEPART_BURN, st.phase)
        self.assertEqual((50000.0, 280.0), (st.depart_node_ut, st.depart_node_dv))

    def test_an_unread_dv_waits(self):
        st, a = mlib.mor_decide(_at(mlib.MOR_DEPART_PLAN),
                                snap(ut=49002.0, body="Mun", node_count=1))
        self.assertEqual([], a)
        self.assertEqual(mlib.MOR_DEPART_PLAN, st.phase)

    def test_an_over_cap_plan_is_cleared_and_named(self):
        st, a = mlib.mor_decide(_at(mlib.MOR_DEPART_PLAN),
                                snap(ut=49002.0, body="Mun", node_count=1, node_dv=900.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MOR_PLAN_OVER_CAP_GIVEUP, st.flake_reason)
        self.assertEqual([mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES], _kinds(a))

    def test_no_node_re_plans_on_the_cadence_then_gives_up(self):
        st = _at(mlib.MOR_DEPART_PLAN)
        st, a = mlib.mor_decide(st, snap(ut=49005.0, body="Mun"))
        self.assertEqual([], a)
        st, a = mlib.mor_decide(st, snap(ut=49010.0, body="Mun"))
        self.assertEqual([Action(mlib.ACTION_MJ_PLAN_MOON_RETURN, 30000.0)], a)
        self.assertEqual(2, st.depart_plan_attempts)
        st, a = mlib.mor_decide(st, snap(ut=49020.0, body="Mun"))
        self.assertEqual(3, st.depart_plan_attempts)
        st, a = mlib.mor_decide(st, snap(ut=49030.0, body="Mun"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MOR_PLAN_REFUSED_GIVEUP, st.flake_reason)

    def test_the_plan_budget_flakes(self):
        st, a = mlib.mor_decide(_at(mlib.MOR_DEPART_PLAN),
                                snap(ut=49000.0 + 301.0, body="Mun"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MOR_PLAN_REFUSED_GIVEUP, st.flake_reason)

    def test_leaving_the_mun_before_the_burn_fails(self):
        st, _ = mlib.mor_decide(_at(mlib.MOR_DEPART_PLAN), snap(ut=49002.0, body="Kerbin"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)


def _burning(**over):
    fields = dict(depart_node_ut=50000.0, depart_node_dv=280.0)
    fields.update(over)
    return _at(mlib.MOR_DEPART_BURN, **fields)


class DepartBurnTests(unittest.TestCase):
    def test_an_escape_with_the_node_consumed_starts_the_coast(self):
        st, a = mlib.mor_decide(_burning(), snap(
            ut=50010.0, body="Mun", apoapsis=-3e6, eccentricity=1.3, node_count=0,
            next_body="Kerbin", next_pe=31000.0))
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0)], a)
        self.assertEqual(mlib.MOR_DEPART_COAST, st.phase)
        self.assertEqual(("Kerbin", 31000.0), (st.depart_next_body, st.depart_next_pe))
        self.assertEqual(50010.0, st.depart_burn_end_ut)

    def test_a_consumed_node_with_a_bound_orbit_is_the_named_under_burn(self):
        st = _burning()
        for i in range(mlib.MOR_UNDERBURN_FRAMES):
            st, a = mlib.mor_decide(st, snap(ut=50010.0 + i, body="Mun", apoapsis=800000.0,
                                             eccentricity=0.5, node_count=0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn(mlib.MOR_UNDERBURN_GIVEUP, st.loss_reason)

    def test_a_dead_executor_is_re_issued_then_named(self):
        st = _burning()
        issued = 0
        for i in range(mlib.MOR_EXEC_DOWN_FRAMES * (mlib.MOR_EXEC_REISSUE_LIMIT + 1)):
            st, a = mlib.mor_decide(st, snap(ut=49100.0 + i, body="Mun", apoapsis=1e6,
                                             eccentricity=0.01, node_count=1,
                                             node_executor_enabled=0))
            issued += _kinds(a).count(mlib.ACTION_MJ_EXECUTE_NODES)
            if st.done:
                break
        self.assertEqual(mlib.MOR_EXEC_REISSUE_LIMIT, issued)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MOR_EXEC_DOWN_GIVEUP, st.flake_reason)

    def test_an_enabled_executor_holding_before_ignition_is_left_alone(self):
        st, a = mlib.mor_decide(_burning(depart_exec_down_frames=3), snap(
            ut=49500.0, body="Mun", apoapsis=1e6, eccentricity=0.01, node_count=1,
            node_executor_enabled=1))
        self.assertEqual([], a)
        self.assertEqual(0, st.depart_exec_down_frames)
        self.assertFalse(st.done)

    def test_a_missed_window_is_named(self):
        st, a = mlib.mor_decide(_burning(), snap(
            ut=50000.0 + mlib.MOR_DEPART_WINDOW_GRACE_SECONDS + 1.0, body="Mun",
            apoapsis=1e6, eccentricity=0.01, node_count=1, node_executor_enabled=1))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MOR_WINDOW_MISSED_GIVEUP, st.flake_reason)
        self.assertEqual([mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES, mlib.ACTION_CUT_THROTTLE],
                         _kinds(a))

    def test_the_burn_budget_flakes(self):
        st, a = mlib.mor_decide(_burning(depart_node_ut=float("nan")), snap(
            ut=49000.0 + 20001.0, body="Mun", apoapsis=1e6, eccentricity=0.01,
            node_count=1))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("timed out", st.flake_reason)


class DepartCoastTests(unittest.TestCase):
    def _coast(self, **over):
        return _at(mlib.MOR_DEPART_COAST, **over)

    def test_one_warp_to_the_boundary_then_1x_then_the_trim(self):
        st, a = mlib.mor_decide(self._coast(), snap(ut=49010.0, body="Mun",
                                                    next_body="Kerbin", time_to_soi=9000.0))
        self.assertEqual([Action(mlib.ACTION_WARP_TO_UT, 49010.0 + 9000.0 - 30.0)], a)
        self.assertEqual(st.depart_coast_target_ut, st.warp_to_cmd)
        st, a = mlib.mor_decide(st, snap(ut=52000.0, body="Mun", warping_to=57980.0))
        self.assertEqual([], a)
        st, a = mlib.mor_decide(st, snap(ut=57977.0, body="Mun", warping_to=57980.0))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], _kinds(a))
        self.assertTrue(st.depart_coast_warp_done)
        self.assertIsNone(st.warp_to_cmd)
        st, a = mlib.mor_decide(st, snap(ut=57990.0, body="Mun", time_to_soi=20.0))
        self.assertEqual([], a)
        st, a = mlib.mor_decide(st, snap(ut=58010.0, body="Kerbin", periapsis=31000.0))
        self.assertEqual([], a)
        self.assertEqual((mlib.MFR_TRIM, mlib.MFR_TRIM), (st.block, st.phase))
        self.assertEqual(58010.0, st.depart_home_ut)

    def test_crossing_while_warping_cancels_the_warp(self):
        st, a = mlib.mor_decide(self._coast(warp_to_cmd=60000.0,
                                            depart_coast_target_ut=60000.0),
                                snap(ut=58010.0, body="Kerbin", warping_to=60000.0))
        self.assertEqual([mlib.ACTION_CANCEL_WARP], _kinds(a))
        self.assertEqual(mlib.MFR_TRIM, st.block)

    def test_an_unreadable_clock_coasts_at_1x(self):
        st, a = mlib.mor_decide(self._coast(), snap(ut=49010.0, body="Mun"))
        self.assertEqual([], a)
        self.assertIsNone(st.depart_coast_target_ut)

    def test_a_patch_to_another_body_fails_by_name(self):
        st, a = mlib.mor_decide(self._coast(), snap(ut=49010.0, body="Mun",
                                                    next_body="Sun", time_to_soi=9000.0))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn(mlib.MOR_NO_HOME_GIVEUP, st.loss_reason)

    def test_a_stalled_warp_re_issues_then_gives_up(self):
        st = self._coast(depart_coast_target_ut=60000.0, depart_coast_issues=1,
                         warp_to_cmd=60000.0)
        issued = 0
        for i in range(mlib.MOR_COAST_STALL_FRAMES * mlib.MOR_COAST_REISSUE_LIMIT + 5):
            st, a = mlib.mor_decide(st, snap(ut=50000.0 + i, body="Mun"))
            issued += _kinds(a).count(mlib.ACTION_WARP_TO_UT)
            if st.done:
                break
        self.assertEqual(mlib.MOR_COAST_REISSUE_LIMIT - 1, issued)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MOR_COAST_STALLED_GIVEUP, st.flake_reason)

    def test_the_coast_budget_flakes_and_cancels(self):
        st, a = mlib.mor_decide(self._coast(), snap(ut=49000.0 + 100001.0, body="Mun"))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertEqual([mlib.ACTION_CANCEL_WARP], _kinds(a))


class OrbitLegCarryTests(unittest.TestCase):
    def test_a_b5_loss_or_flake_is_carried(self):
        p = _params()
        base = mlib.mor_initial_state(p)
        lost = dataclasses.replace(base.flight, done=True, verdict=mlib.MISSION_ASSERT_FAIL,
                                   loss_reason="left the target SOI")
        lost = dataclasses.replace(lost, phase=mlib.B5_PARK)
        st, _ = mlib._mor_orbit(dataclasses.replace(base, flight=lost), snap(ut=1.0))
        # A done B5 state is not fed again by b5_decide; the carry reads it.
        self.assertTrue(st.done)
        self.assertIn("orbit leg", st.loss_reason or st.flake_reason)


class FullFlightTests(unittest.TestCase):
    def _frames(self):
        # At the SOI frame (UT 58010) the trim reads 31 km, inside the window, and
        # skips; the tail's stage-drop and fall frames (UT 58014 / 58015) follow.
        return (_orbit_frames() + _depart_frames()
                + [snap(ut=58_011.0, altitude=11_290_000.0, periapsis=31_000.0,
                        body="Kerbin")]
                + _return_tail_frames(57_996.0, periapsis=31_000.0,
                                      stowed=mlib.CHUTE_STATE_STOWED,
                                      deployed=mlib.CHUTE_STATE_DEPLOYED)[6:])

    def test_the_whole_flight_is_mission_ok_with_every_row_met(self):
        control = FakeMissionControl(self._frames())
        code, result = run(b11_mun_orbit_return.SPEC, PLAIN_PARAMS, control, budget=600.0)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        self.assertEqual("b11_mun_orbit_return", result["mission"])
        kinds = _kinds(control.actions)
        self.assertNotIn(mlib.ACTION_PARSEK_COMMIT_TREE, kinds)
        self.assertEqual(1, kinds.count(mlib.ACTION_MJ_PLAN_MOON_RETURN))
        # TLI, capture, departure.
        self.assertEqual(3, kinds.count(mlib.ACTION_MJ_EXECUTE_NODES))
        self.assertIn(mlib.ACTION_ACTIVATE_STAGE, kinds)
        phases = result["phasesReached"]
        for phase in (mlib.B5_PARK, mlib.MOR_DEPART_PLAN, mlib.MOR_DEPART_BURN,
                      mlib.MOR_DEPART_COAST, mlib.MFR_TRIM, mlib.MOR_TERMINAL_PHASE):
            self.assertIn(phase, phases)
        self.assertNotIn(mlib.B5_ORBIT_COMMIT, phases)
        names = [a["name"] for a in result["assertions"]]
        self.assertEqual(["reachedOrbit", "reachedTargetSoi", "flybyPeriapsisFloor",
                          "capturedInTargetOrbit", "parkedStable", "departedTargetOrbit",
                          "returnPeriapsisInWindow", "landedSituation",
                          "craftCanopyObserved"], names)
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])

    def test_rows_fail_closed_on_a_flight_that_never_departed(self):
        p = _params()
        rows = {r.name: r for r in mlib.evaluate_mor_assertions([], p,
                                                                mlib.mor_initial_state(p))}
        for name in ("capturedInTargetOrbit", "parkedStable", "departedTargetOrbit",
                     "returnPeriapsisInWindow", "landedSituation", "craftCanopyObserved"):
            self.assertFalse(rows[name].met, name)
        self.assertNotIn("treeCommitted", rows)


class ShellTests(unittest.TestCase):
    def test_the_control_is_b11s_plus_mfrs_reads(self):
        mine = b11_mun_orbit_return.make_control()
        b11 = b11_mun_orbit.make_control()
        mfr = b5_mun_flyby_return.make_control()
        for key in ("_read_docking", "_read_node_executor", "_read_periapsis",
                    "_read_chute", "_use_mechjeb"):
            self.assertTrue(getattr(mine, key), key)
            self.assertTrue(getattr(b11, key) or getattr(mfr, key), key)
        self.assertFalse(mine._tolerate_unreadable_nodes)

    def test_the_spec_shape(self):
        spec = b11_mun_orbit_return.SPEC
        self.assertEqual("b11_mun_orbit_return", spec.name)
        self.assertTrue(spec.allow_rails_warp)
        self.assertEqual(4.0, spec.max_physics_warp)
        state = spec.build_state(PARAMS)
        self.assertIsInstance(state, mlib.MorState)
        self.assertIs(state.flight, mlib.frozen_detector_holder(state))

    def test_flighteff_names_the_machine(self):
        import flighteff
        self.assertEqual("mor_decide", flighteff.MISSION_MACHINES["b11_mun_orbit_return"])


class SchemaAndSpecTests(unittest.TestCase):
    def test_b11_and_mfr_keys_are_declared_identically(self):
        mine = _schema("b11_mun_orbit_return")["params"]
        b11 = _schema("b11_mun_orbit")["params"]
        mfr = _schema("b5_mun_flyby_return")["params"]
        for source in (b11, mfr):
            for key, decl in source.items():
                self.assertEqual(decl, mine.get(key), key)
        self.assertEqual(set(DEPART_PARAMS), set(mine) - set(b11) - set(mfr))

    def test_the_cs6_params_validate_and_build(self):
        import hlib
        spec = _cs6_spec()
        self.assertEqual("b11_mun_orbit_return", spec["driver"]["mission"])
        params = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(
            params, _schema("b11_mun_orbit_return")))
        p = mlib.mor_params_from_dict(params)
        self.assertGreater(p.mj_engage_confirm_frames, 0)
        self.assertTrue(p.orbit.capture_enabled)
        self.assertEqual(p.return_periapsis, p.depart_periapsis)
        self.assertLess(p.return_periapsis_max, p.home.warp_above_alt)
        self.assertLess(p.home.chute_deploy_alt, p.return_periapsis_min)
        # The park dwell fits its budget with the stabilization slack B11 keeps.
        self.assertGreaterEqual(p.orbit.park_timeout - p.orbit.park_dwell, 420.0)

    def test_the_spec_flies_the_mission_once_and_recovers_the_flyer(self):
        spec = _cs6_spec()
        self.assertEqual("fixtures/saves/cs-career-mun1", spec["fixture"]["saveTemplate"])
        steps = spec["driver"]["steps"]
        self.assertEqual(1, sum(1 for s in steps if s.get("phase") == "mission"))
        mission = next(i for i, s in enumerate(steps) if s.get("phase") == "mission")
        launch = steps[mission - 1]
        self.assertEqual("LaunchFromEditor", launch["cmd"])
        recover = steps[mission + 1]
        self.assertEqual("Recover", recover["cmd"])
        self.assertEqual("${%s.pid}" % launch["label"], recover["args"]["pid"])
        self.assertEqual("CS Mun Flyby", steps[mission - 2]["args"]["craft"])
        self.assertFalse(any(s.get("cmd") == "CommitTree" and s.get("expect") == "OK"
                             for s in steps))

    def test_the_milestone_pins(self):
        contracts = _cs6_spec()["expectations"]["logContracts"]
        required = contracts["required"]
        lines = [r for r in required if r.startswith("Milestone enriched: ")]
        self.assertTrue(any(l.startswith("Milestone enriched: 'Mun/Orbit' ") for l in lines))
        self.assertTrue(any(l.startswith("Milestone enriched: 'Mun/ReturnFromOrbit' ")
                            for l in lines))
        for line in lines:
            self.assertRegex(line, r"^Milestone enriched: '[A-Z][A-Za-z]+/[A-Za-z]+'")
        forbidden = contracts["forbidden"]
        self.assertIn("Milestone enriched: 'Orbit' ", forbidden)
        bare = re.compile(forbidden[0])
        self.assertTrue(bare.search("Milestone enriched: 'Orbit' funds=1 rep=1 sci=1"))
        self.assertFalse(bare.search("Milestone enriched: 'Mun/Orbit' funds=1 rep=1 sci=1"))
        repaid = re.compile(forbidden[1])
        self.assertTrue(repaid.search("Milestone enriched: 'Mun/Flyby' funds=0"))
        self.assertFalse(repaid.search("Milestone enriched: 'Mun/Orbit' funds=0"))
        self.assertFalse(repaid.search("Milestone enriched: 'Mun/ReturnFromOrbit' funds=0"))
        # The rollout regex is the craft's, never the ground-truth Flea's.
        rollout = re.compile(next(r for r in required if "VesselRollout" in r
                                  and "2932" not in r))
        self.assertFalse(rollout.search("Game state: FundsChanged -2932 (VesselRollout)"))
        self.assertTrue(rollout.search("Game state: FundsChanged -11612 (VesselRollout)"))


class _FakeMoonReturnOp:
    def __init__(self, sticky=False, raise_on_set=False, nodes=("n",)):
        self._alt = 100000.0
        self._sticky = sticky
        self._raise = raise_on_set
        self._nodes = list(nodes)
        self.made = 0

    @property
    def moon_return_altitude(self):
        return self._alt

    @moon_return_altitude.setter
    def moon_return_altitude(self, value):
        if self._raise:
            raise RuntimeError("OperationException")
        if not self._sticky:
            self._alt = value

    def make_nodes(self):
        self.made += 1
        return list(self._nodes)


class RunnerMoonReturnTests(unittest.TestCase):
    class _Planner:
        def __init__(self, op):
            self.operation_moon_return = op

    class _MechJeb:
        def __init__(self, op):
            self.maneuver_planner = RunnerMoonReturnTests._Planner(op)

    class _Control:
        def __init__(self, nodes):
            self.nodes = nodes

    class _Vessel:
        def __init__(self, nodes):
            self.control = RunnerMoonReturnTests._Control(nodes)

    class _Sc:
        def __init__(self, vessel):
            self.active_vessel = vessel

    class _Conn:
        def __init__(self, sc):
            self.space_center = sc

    def _perform(self, op, pending=()):
        ctrl = mission_runner.KrpcMissionControl(use_mechjeb=True)
        ctrl._mechjeb = self._MechJeb(op)
        ctrl._conn = self._Conn(self._Sc(self._Vessel(list(pending))))
        lines = []
        orig = mission_runner._stdout_sink
        mission_runner._stdout_sink = lines.append
        try:
            ctrl.perform(Action(mlib.ACTION_MJ_PLAN_MOON_RETURN, 30000.0))
        finally:
            mission_runner._stdout_sink = orig
        return lines

    def test_a_confirmed_altitude_plans(self):
        op = _FakeMoonReturnOp()
        lines = self._perform(op)
        self.assertEqual(1, op.made)
        self.assertEqual(30000.0, op.moon_return_altitude)
        self.assertTrue(any("moon-return plan issued" in l and "READ BACK confirmed" in l
                            for l in lines), lines)

    def test_a_pending_node_skips_the_plan(self):
        op = _FakeMoonReturnOp()
        lines = self._perform(op, pending=("stale",))
        self.assertEqual(0, op.made)
        self.assertTrue(any("SKIPPED" in l for l in lines), lines)

    def test_a_sticky_altitude_refuses_to_plan(self):
        op = _FakeMoonReturnOp(sticky=True)
        lines = self._perform(op)
        self.assertEqual(0, op.made)
        self.assertTrue(any("READ BACK" in l and "REFUSING" in l for l in lines), lines)

    def test_a_throwing_setter_refuses_to_plan(self):
        op = _FakeMoonReturnOp(raise_on_set=True)
        lines = self._perform(op)
        self.assertEqual(0, op.made)
        self.assertTrue(any("REFUSING" in l for l in lines), lines)


if __name__ == "__main__":
    unittest.main()
