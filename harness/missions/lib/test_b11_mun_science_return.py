"""Unit cells for mission `b11_mun_science_return` (CS-6c) and its composed machine.

The mission's claim is "B5's machine verbatim with captureEnabled to its PARK exit,
the high-space readings and an EVA report in the park, two lowering nodes, the
low-space readings and a second EVA report, then mor's departure and mfr's return
tail verbatim". Each class pins one part:

  1. THE DELEGATES ARE VERBATIM. Over B11's own capture fixture the orbit leg emits
     exactly what b5_decide emits until the PARK exit frame, where only the
     mid-mission CommitTree is replaced by the high run. The departure and the tail
     are mor's and mfr's own functions, run on MscState because it carries every
     MorState field. mor_decide and mlr_decide are pinned frame for frame by trace
     digests taken on the base commit (bdcf4bb07), before this machine existed.
  2. THE PARAMS: the modes and windows the science cannot follow are refused.
  3. THE PURE RULES: the gates, the pod / kerbal reads, the lowering node arithmetic.
  4. THE READINGS: each run gated on the OBSERVED count, bounded re-runs, named
     give-ups; each EVA verb gated on its own leg's tag, each refusal by name.
  5. THE LOWERING: plan, burn, skip, each give-up by name.
  6. THE ELECTRIC-CHARGE WATCHDOG and the kept-readings count.
  7. THE WHOLE FLIGHT over the fake seam, the rows, the shell, the schema and the
     spec.

NO krpc, NO KSP, NO network. Import path matches the sibling suites.
"""

import dataclasses
import hashlib
import inspect
import math
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
import b11_mun_orbit_return        # noqa: E402
import b11_mun_science_return      # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402
import test_b11_mun_orbit_return as t11  # noqa: E402
from test_b11_mun_orbit_return import (CAREER_PARAMS, DEPART_PARAMS,  # noqa: E402
                                       PLAIN_PARAMS as MOR_PLAIN_PARAMS,
                                       _orbit_frames, _return_tail_frames)
from test_b13_mun_landing_return import (PLAIN_PARAMS as MLR_PLAIN_PARAMS,  # noqa: E402
                                         _whole_flight as _mlr_whole_flight)

CS6C_SPEC = "CS-6c-career-mun-science"

SCIENCE_PARAMS = {
    "sciHighExperiments": ["mysteryGoo", "temperatureScan"],
    "sciLowExperiments": ["crewReport", "mysteryGoo", "temperatureScan"],
    "sciHighMinPeriapsisMeters": 70000,
    "sciLowMaxApoapsisMeters": 55000,
    "sciLowMinPeriapsisMeters": 10000,
    "sciRunTimeoutSeconds": 120,
    "sciRunRetrySeconds": 15,
    "sciLowPeriapsisMeters": 30000,
    "sciLowApoapsisMeters": 45000,
    # B11's fixture parks at ~1000 km: its lowering legs cost ~100 and ~149 m/s.
    "sciLowerMaxDvMps": 200,
    "sciLowerPlanTimeoutSeconds": 120,
    "sciLowerBurnTimeoutSeconds": 12000,
    "sciNodeLeadSeconds": 120,
    "evaSettleSeconds": 5,
    "evaSettleFrames": 3,
    "evaPhaseTimeoutSeconds": 900,
    "electricChargeFloor": 0.5,
    "electricChargeDebounceFrames": 4,
}
PLAIN_PARAMS = dict(MOR_PLAIN_PARAMS, **SCIENCE_PARAMS)
PARAMS = dict(PLAIN_PARAMS, **CAREER_PARAMS)

# mor_decide / mlr_decide trace digests, taken on the base commit bdcf4bb07 (before
# this machine) over the CS-6 and CS-7 full-flight fixtures and their rows. A change
# to either machine moves it; re-pin only for a deliberate change to that machine.
MOR_TRACE_DIGEST = "e3881eada1b717c44f211535cc307a348b7fe24fb7a0a63cfa47d213daef0cf7"
MLR_TRACE_DIGEST = "a782c07ee15e8975befe7e4b1429c4b763cf96c785f8d054470ccc0d5ac8a503"

_PARK_EXIT_UT = 48_200.0
_PARK = dict(body="Mun", situation="ORBITING", periapsis=989_990.0, apoapsis=1_010_010.0,
             eccentricity=0.01)


def _params(**over):
    return mlib.msc_params_from_dict(dict(PARAMS, **over))


def _kinds(actions):
    return [a.kind for a in actions]


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


def _spec(name):
    with open(os.path.join(_HARNESS, "scenarios", name + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _at(block, phase, params=None, **over):
    """A machine state in ``block`` at ``phase``, entered at UT 50000."""
    p = params or _params()
    st = mlib.msc_initial_state(p)
    st = dataclasses.replace(st, block=block, phase=phase, phase_entry_ut=50000.0)
    return dataclasses.replace(st, **over)


def _pod(ut, data=-1, **kw):
    fields = dict(ut=ut, liquid_fuel=200.0, electric_charge=40.0,
                  science_data_count=data, **_PARK)
    fields.update(kw)
    return snap(**fields)


def _kerbal(ut, data=0, **kw):
    fields = dict(ut=ut, body="Mun", situation="ORBITING", liquid_fuel=0.0,
                  electric_charge=0.0, science_data_count=data)
    fields.update(kw)
    return snap(**fields)


def _answer(tag, ut, result="OK", payload=(), **kw):
    fields = dict(ut=ut, body="Mun", situation="ORBITING", liquid_fuel=0.0,
                  seam_command_tag=tag, seam_command_result=result,
                  seam_command_payload=tuple(payload))
    fields.update(kw)
    return snap(**fields)


_EXIT_PAYLOAD = (("kerbal", "Jebediah Kerman"), ("evaPid", "4242"), ("released", "false"),
                 ("standoff", "off"))
_BOARD_PAYLOAD = (("kerbal", "Jebediah Kerman"), ("boardedPid", "1700000000"))


def _leg_frames(leg, t0, want, orbit):
    """A leg from its run frame on: the pod reads its readings twice (-> EvaExit), the
    exit answer (-> evaReport), the kerbal reads its report twice (-> EvaBoard), the
    board answer (-> settle), three settled pod frames 5 s on (-> the next block).
    ``orbit`` is the pod's orbit; the last settle frame carries a periapsis clock."""
    pod = dict(liquid_fuel=200.0, electric_charge=40.0, **orbit)
    return [
        snap(ut=t0 + 1.0, science_data_count=want, **pod),
        snap(ut=t0 + 2.0, science_data_count=want, **pod),
        _answer(leg.exit_tag, t0 + 10.0, payload=_EXIT_PAYLOAD),
        _kerbal(t0 + 11.0, data=1),
        _kerbal(t0 + 12.0, data=1),
        _answer(leg.board_tag, t0 + 20.0, payload=_BOARD_PAYLOAD, science_data_count=1),
        snap(ut=t0 + 21.0, science_data_count=want, **pod),
        snap(ut=t0 + 22.0, science_data_count=want, **pod),
        snap(ut=t0 + 26.0, science_data_count=want, time_to_periapsis=1500.0, **pod),
    ]


_LOW_ORBIT = dict(body="Mun", situation="ORBITING", periapsis=30_100.0, apoapsis=45_200.0,
                  eccentricity=0.03)


def _lower_frames(t0):
    """LOWER-PLAN-PE (entered on the high settle exit frame) -> BURN-PE -> the far
    side down -> LOWER-PLAN-AP (planned on its entry frame) -> BURN-AP -> the low
    orbit (-> SCI-LOW-RUN on the third settled frame)."""
    mid = dict(body="Mun", situation="ORBITING", periapsis=30_500.0,
               apoapsis=989_990.0, eccentricity=0.44, liquid_fuel=180.0,
               electric_charge=38.0)
    low = dict(liquid_fuel=170.0, electric_charge=39.0, **_LOW_ORBIT)
    return [
        _pod(t0 + 1.0, data=2, node_count=1, node_dv=100.0),               # -> BURN-PE
        _pod(t0 + 900.0, data=2, node_count=1, node_dv=100.0, node_executor_enabled=1),
        snap(ut=t0 + 1530.0, science_data_count=2, **mid),
        snap(ut=t0 + 1531.0, science_data_count=2, **mid),
        snap(ut=t0 + 1532.0, science_data_count=2, time_to_periapsis=2000.0,
             **mid),                                                       # -> PLAN-AP
        snap(ut=t0 + 1533.0, science_data_count=2, node_count=1, node_dv=148.0,
             **mid),                                                       # -> BURN-AP
        snap(ut=t0 + 3540.0, science_data_count=2, **low),
        snap(ut=t0 + 3541.0, science_data_count=2, **low),
        snap(ut=t0 + 3542.0, science_data_count=2, **low),                 # -> LOW-RUN
    ]


def _depart_frames(t0):
    """Plan -> burn -> coast warp -> the Kerbin SOI (mor's shape, shifted to t0)."""
    bound = dict(apoapsis=45_200.0, periapsis=30_100.0, eccentricity=0.03,
                 situation="ORBITING", body="Mun", electric_charge=36.0,
                 liquid_fuel=120.0, science_data_count=5)
    node_ut = t0 + 800.0
    target = t0 + 810.0 + 9000.0 - 30.0
    return [
        snap(ut=t0 + 10.0, altitude=30_000.0, node_count=1, node_dv=280.0,
             node_ut=node_ut, **bound),                                 # -> BURN
        snap(ut=t0 + 700.0, altitude=30_010.0, node_count=1, node_dv=280.0,
             node_ut=node_ut, node_executor_enabled=1, **bound),        # holds
        snap(ut=t0 + 810.0, altitude=35_000.0, apoapsis=-3_000_000.0, periapsis=29_000.0,
             eccentricity=1.3, situation="ESCAPING", body="Mun", node_count=0,
             next_body="Kerbin", next_pe=31_000.0, time_to_soi=9_000.0,
             electric_charge=39.0),                                     # -> COAST
        snap(ut=t0 + 811.0, altitude=36_000.0, apoapsis=-3_000_000.0, eccentricity=1.3,
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


def _whole_flight(kept=5):
    frames = _orbit_frames()
    frames += _leg_frames(mlib.MSC_LEG_HIGH, _PARK_EXIT_UT, 2, _PARK)
    t_lower = frames[-1].ut
    frames += _lower_frames(t_lower)
    t_low = frames[-1].ut
    frames += _leg_frames(mlib.MSC_LEG_LOW, t_low, 5, _LOW_ORBIT)
    t_depart = frames[-1].ut
    frames += _depart_frames(t_depart)
    t_home = frames[-1].ut
    frames += [snap(ut=t_home + 1.0, altitude=11_290_000.0, periapsis=31_000.0,
                    body="Kerbin")]
    tail = _return_tail_frames(t_home - 14.0, periapsis=31_000.0,
                               stowed=mlib.CHUTE_STATE_STOWED,
                               deployed=mlib.CHUTE_STATE_DEPLOYED)[6:]
    frames += [dataclasses.replace(f, science_data_count=kept) for f in tail]
    return frames


def _trace_digest(decide, state, frames, evaluate):
    out = []
    for f in frames:
        state, acts = decide(state, f)
        out.append(repr(acts))
        out.append(repr(state))
    out.append(repr(evaluate([], state.params, state)))
    return hashlib.sha256("\n".join(out).encode("utf-8")).hexdigest(), state


def _mor_trace_digest():
    st = mlib.mor_initial_state(mlib.mor_params_from_dict(MOR_PLAIN_PARAMS))
    return _trace_digest(mlib.mor_decide, st, t11.FullFlightTests()._frames(),
                         mlib.evaluate_mor_assertions)


def _mlr_trace_digest():
    st = mlib.mlr_initial_state(mlib.mlr_params_from_dict(MLR_PLAIN_PARAMS))
    return _trace_digest(mlib.mlr_decide, st, _mlr_whole_flight(),
                         mlib.evaluate_mlr_assertions)


class DelegatesAreVerbatimTests(unittest.TestCase):
    def test_the_orbit_leg_is_b5_until_the_park_exit_frame(self):
        p = mlib.msc_params_from_dict(PLAIN_PARAMS)
        st = mlib.msc_initial_state(p)
        b5 = mlib.b5_initial_state(p.orbit)
        frames = _orbit_frames()
        for frame in frames[:-1]:
            st, got = mlib.msc_decide(st, frame)
            b5, want = mlib.b5_decide(b5, frame)
            self.assertEqual(want, got)
            self.assertEqual(b5, st.flight)
            self.assertEqual(b5.phase, st.phase)
        self.assertEqual(mlib.B5_PARK, st.phase)
        # The PARK exit frame: B5 asks for the commit; the composer starts the high
        # run in its place and keeps everything else B5 emitted.
        st, got = mlib.msc_decide(st, frames[-1])
        b5, want = mlib.b5_decide(b5, frames[-1])
        self.assertEqual(mlib.B5_ORBIT_COMMIT, b5.phase)
        self.assertIn(mlib.ACTION_PARSEK_COMMIT_TREE, _kinds(want))
        self.assertEqual([a for a in want if a.kind != mlib.ACTION_PARSEK_COMMIT_TREE]
                         + [Action(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS,
                                   text="mysteryGoo,temperatureScan")], got)
        self.assertEqual(b5, st.flight)
        self.assertEqual((mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.run), (st.block, st.phase))
        self.assertNotIn(mlib.B5_ORBIT_COMMIT, st.phases_reached)
        self.assertEqual(frames[-1].ut, st.science_entry_ut)
        self.assertEqual(989_990.0, st.high_gate_pe)

    def test_mor_and_mlr_are_unchanged_frame_for_frame(self):
        digest, st = _mor_trace_digest()
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(MOR_TRACE_DIGEST, digest)
        digest, st = _mlr_trace_digest()
        self.assertTrue(st.done)
        self.assertIsNone(st.verdict)
        self.assertEqual(MLR_TRACE_DIGEST, digest)

    def test_the_departure_and_tail_blocks_run_on_msc_state(self):
        mor_state = {f.name for f in dataclasses.fields(mlib.MorState)}
        msc_state = {f.name for f in dataclasses.fields(mlib.MscState)}
        self.assertEqual(set(), mor_state - msc_state)
        mor_params = {f.name for f in dataclasses.fields(mlib.MorParams)}
        msc_params = {f.name for f in dataclasses.fields(mlib.MscParams)}
        self.assertEqual(set(), mor_params - msc_params)
        # Every per-leg field exists for both legs.
        for leg in mlib.MSC_LEGS:
            for key in ("gate_pe", "gate_ap", "data", "collected_ut", "exit_result",
                        "kerbal", "eva_pid", "report_data", "report_ut", "board_result",
                        "boarded_pid", "aboard_ut"):
                self.assertIn("%s_%s" % (leg.prefix, key), msc_state)

    def test_the_departure_is_mors_own(self):
        frame = snap(ut=50002.0, body="Mun", node_count=1, node_dv=280.0, node_ut=51000.0)
        mor = dataclasses.replace(
            mlib.mor_initial_state(mlib.mor_params_from_dict(
                dict(MOR_PLAIN_PARAMS, **CAREER_PARAMS))),
            block=mlib.MOR_DEPART, phase=mlib.MOR_DEPART_PLAN, phase_entry_ut=50000.0,
            depart_plan_attempts=1, depart_last_plan_ut=50000.0)
        msc = _at(mlib.MOR_DEPART, mlib.MOR_DEPART_PLAN, depart_plan_attempts=1,
                  depart_last_plan_ut=50000.0)
        mor, a = mlib.mor_decide(mor, frame)
        msc, b = mlib.msc_decide(msc, frame)
        self.assertEqual(a, b)
        self.assertEqual(mor.phase, msc.phase)

    def test_no_private_b4_b5_helper_is_called(self):
        src = "".join(inspect.getsource(f) for f in (
            mlib._msc_orbit, mlib._msc_science, mlib._msc_eva, mlib._msc_run,
            mlib._msc_lower, mlib._msc_lower_plan, mlib._msc_lower_burn, mlib.msc_decide))
        self.assertIn("b5_decide(", src)
        self.assertEqual([], re.findall(r"(?<![A-Za-z0-9])_b[45]_\w+", src))

    def test_the_launch_gate_is_the_career_one(self):
        st, actions = mlib.msc_decide(mlib.msc_initial_state(_params()),
                                      snap(ut=5.0, situation="PRE_LAUNCH", body="Kerbin"))
        self.assertEqual([mlib.ACTION_MJ_SET_TARGET_APOAPSIS, mlib.ACTION_MJ_ENABLE_AUTOSTAGE,
                          mlib.ACTION_MJ_SET_ASCENT_TYPE, mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT,
                          mlib.ACTION_MJ_ENGAGE_ASCENT], _kinds(actions))
        self.assertTrue(st.stage_held)
        st, actions = mlib.msc_decide(st, snap(ut=7.0, mj_autopilot_enabled=True))
        self.assertEqual([mlib.ACTION_ACTIVATE_STAGE], _kinds(actions))


class ParamsTests(unittest.TestCase):
    def test_the_keys_are_read(self):
        p = _params(sciHighMinPeriapsisMeters=80000, sciLowExperiments=["crewReport"],
                    evaKerbalName="Bill Kerman", electricChargeFloor=1.5,
                    sciLowerMaxDvMps=120)
        self.assertEqual(80000.0, p.sci_high_min_periapsis)
        self.assertEqual(("crewReport",), p.sci_low_experiments)
        self.assertEqual("Bill Kerman", p.eva_kerbal)
        self.assertEqual(1.5, p.ec_floor)
        self.assertEqual(120.0, p.sci_lower_max_dv)
        self.assertEqual(30000.0, p.depart_periapsis)
        src = inspect.getsource(mlib.msc_params_from_dict)
        for key in list(SCIENCE_PARAMS) + list(DEPART_PARAMS) + ["evaKerbalName"]:
            self.assertIn('"%s"' % key, src)

    def test_the_defaults_are_the_two_runs_the_craft_carries(self):
        p = mlib.msc_params_from_dict(MOR_PLAIN_PARAMS)
        self.assertEqual(("mysteryGoo", "temperatureScan"), p.sci_high_experiments)
        self.assertEqual(("crewReport", "mysteryGoo", "temperatureScan"),
                         p.sci_low_experiments)
        self.assertEqual(2, mlib.msc_expected_pod_data(p, mlib.MSC_LEG_HIGH))
        self.assertEqual(5, mlib.msc_expected_pod_data(p, mlib.MSC_LEG_LOW))

    def test_refusals(self):
        bad = ({"captureEnabled": False}, {"landingEnabled": True},
               {"interplanetaryTransfer": True}, {"targetBodyName": "Kerbin"},
               {"targetBodyName": "Vall"},
               {"departPeriapsisMeters": 60000}, {"departMaxDvMps": 0},
               {"sciHighExperiments": []}, {"sciLowExperiments": ["evaReport"]},
               {"sciLowMaxApoapsisMeters": 75000}, {"sciLowApoapsisMeters": 25000},
               {"sciLowPeriapsisMeters": 5000}, {"sciLowerMaxDvMps": 0},
               {"sciRunRetrySeconds": 0}, {"evaSettleFrames": 0},
               {"electricChargeDebounceFrames": 0},
               {"returnPeriapsisMinMeters": 35000}, {"returnTrimFineThrottle": 0.5})
        for over in bad:
            with self.subTest(over=over):
                with self.assertRaises(ValueError):
                    _params(**over)
        with self.assertRaises(ValueError):
            mlib.msc_params_from_dict(dict(PLAIN_PARAMS, autostageLimit=2))


class PureRuleTests(unittest.TestCase):
    def test_the_high_gate_is_the_periapsis_over_the_threshold(self):
        p = _params()
        self.assertTrue(mlib.msc_high_gate(_pod(1.0), p))
        self.assertTrue(mlib.msc_high_gate(_pod(1.0, periapsis=70000.0), p))
        self.assertFalse(mlib.msc_high_gate(_pod(1.0, periapsis=69999.0), p))
        self.assertFalse(mlib.msc_high_gate(_pod(1.0, periapsis=float("nan")), p))
        self.assertFalse(mlib.msc_high_gate(_pod(1.0, situation="ESCAPING"), p))
        self.assertFalse(mlib.msc_high_gate(_pod(1.0, body="Kerbin"), p))

    def test_the_low_gate_is_the_whole_orbit_under_the_threshold(self):
        p = _params()
        low = dict(_LOW_ORBIT)
        self.assertTrue(mlib.msc_low_gate(snap(**low), p))
        self.assertTrue(mlib.msc_low_gate(snap(**dict(low, apoapsis=55000.0)), p))
        self.assertFalse(mlib.msc_low_gate(snap(**dict(low, apoapsis=55001.0)), p))
        self.assertFalse(mlib.msc_low_gate(snap(**dict(low, periapsis=9000.0)), p))
        self.assertFalse(mlib.msc_low_gate(snap(**dict(low, apoapsis=-1.0)), p))
        self.assertFalse(mlib.msc_low_gate(snap(**dict(low, situation="SUB_ORBITAL")), p))

    def test_the_pod_and_the_kerbal_are_told_apart_by_fuel(self):
        p = _params()
        self.assertTrue(mlib.msc_pod_active(_pod(1.0), p))
        self.assertFalse(mlib.msc_pod_active(_pod(1.0, liquid_fuel=0.0), p))
        self.assertFalse(mlib.msc_pod_active(_pod(1.0, liquid_fuel=float("nan")), p))
        self.assertTrue(mlib.msc_kerbal_active(_kerbal(1.0), p))
        self.assertFalse(mlib.msc_kerbal_active(_kerbal(1.0, liquid_fuel=float("nan")), p))
        self.assertFalse(mlib.msc_kerbal_active(_pod(1.0), p))

    def test_the_lowering_node_is_vis_viva_at_the_periapsis(self):
        p = _params(sciLowerMaxDvMps=150)
        mu, radius = mlib.STOCK_BODY_GRAVITY["Mun"][:2]
        # A 140 km circular park: the first node drops the far side to 30 km.
        frame = snap(ut=1000.0, periapsis=140_000.0, apoapsis=140_000.0,
                     time_to_periapsis=600.0)
        dv, node_ut, why = mlib.msc_lower_node_plan(frame, p, "PE")
        self.assertEqual("", why)
        r = radius + 140_000.0
        want = (math.sqrt(mu * (2 / r - 1 / ((r + radius + 30_000.0) / 2)))
                - math.sqrt(mu / r))
        self.assertAlmostEqual(want, dv, places=9)
        self.assertAlmostEqual(-44.5, dv, delta=0.5)
        self.assertEqual(1600.0, node_ut)
        # The second, at the new 30 km periapsis, drops the apoapsis to 45 km.
        frame = snap(ut=5000.0, periapsis=30_000.0, apoapsis=140_000.0,
                     time_to_periapsis=1800.0)
        dv, node_ut, why = mlib.msc_lower_node_plan(frame, p, "AP")
        self.assertEqual("", why)
        self.assertAlmostEqual(-40.8, dv, delta=0.5)
        self.assertEqual(6800.0, node_ut)

    def test_a_near_periapsis_is_taken_one_orbit_later(self):
        p = _params()
        mu, radius = mlib.STOCK_BODY_GRAVITY["Mun"][:2]
        frame = snap(ut=1000.0, periapsis=140_000.0, apoapsis=140_000.0,
                     time_to_periapsis=60.0)
        _, node_ut, _ = mlib.msc_lower_node_plan(frame, p, "PE")
        period = 2 * math.pi * math.sqrt((radius + 140_000.0) ** 3 / mu)
        self.assertAlmostEqual(1060.0 + period, node_ut, places=6)

    def test_refused_and_unreadable_plans(self):
        p = _params(sciLowerMaxDvMps=150)
        _, _, why = mlib.msc_lower_node_plan(
            snap(ut=1.0, periapsis=20_000.0, apoapsis=25_000.0, time_to_periapsis=900.0),
            p, "PE")
        self.assertTrue(why.startswith("not-a-lowering"), why)
        _, _, why = mlib.msc_lower_node_plan(
            snap(ut=1.0, periapsis=30_000.0, apoapsis=2_000_000.0,
                 time_to_periapsis=900.0), p, "AP")
        self.assertTrue(why.startswith("over-cap"), why)
        for bad in (dict(periapsis=float("nan")), dict(apoapsis=-5.0),
                    dict(time_to_periapsis=float("nan"))):
            fields = dict(ut=1.0, periapsis=140_000.0, apoapsis=140_000.0,
                          time_to_periapsis=900.0)
            fields.update(bad)
            _, _, why = mlib.msc_lower_node_plan(snap(**fields), p, "PE")
            self.assertTrue(why.startswith("unreadable"), (bad, why))

    def test_a_leg_is_met_inside_its_window(self):
        p = _params()
        self.assertTrue(mlib.msc_lower_leg_met(snap(periapsis=30_000.0), p, "PE"))
        self.assertTrue(mlib.msc_lower_leg_met(snap(periapsis=40_000.0), p, "PE"))
        self.assertFalse(mlib.msc_lower_leg_met(snap(periapsis=40_001.0), p, "PE"))
        self.assertFalse(mlib.msc_lower_leg_met(snap(periapsis=9_000.0), p, "PE"))
        self.assertTrue(mlib.msc_lower_leg_met(snap(**_LOW_ORBIT), p, "AP"))

    def test_the_seam_commands_carry_each_legs_own_tag_and_no_release(self):
        p = _params(evaKerbalName="Bill Kerman")
        tags = set()
        for leg in mlib.MSC_LEGS:
            a = mlib.msc_eva_actions("EvaExit", leg, p)[0]
            self.assertEqual(("EvaExit", (("kerbal", "Bill Kerman"),), leg.exit_tag),
                             (a.seam_verb, a.seam_args, a.seam_tag))
            b = mlib.msc_eva_actions("EvaBoard", leg, p)[0]
            self.assertEqual(("EvaBoard", leg.board_tag), (b.seam_verb, b.seam_tag))
            tags |= {leg.exit_tag, leg.board_tag}
        self.assertEqual(4, len(tags))
        self.assertNotIn(mlib.MLR_TAG_EVA_EXIT, tags)
        self.assertEqual((), mlib.msc_eva_actions("EvaExit", mlib.MSC_LEG_HIGH,
                                                  _params())[0].seam_args)
        for tag in tags:
            self.assertRegex(tag, r"^[a-z0-9]+$")
        with self.assertRaises(ValueError):
            mlib.msc_eva_actions("PlantFlag", mlib.MSC_LEG_HIGH, p)


class ReadingTests(unittest.TestCase):
    def test_a_short_park_is_named_on_the_exit_frame(self):
        p = _params()
        st = dataclasses.replace(mlib.msc_initial_state(p), phase=mlib.B5_PARK)
        st, a = mlib._msc_enter_run(st, _pod(1.0, periapsis=50_000.0),
                                    mlib.MSC_LEG_HIGH)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MSC_HIGH_NOT_HIGH, st.flake_reason)
        self.assertEqual([], a)

    def test_the_run_waits_for_the_observed_count_debounced(self):
        st = _at(mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.run, run_attempts=1,
                 last_run_ut=50000.0)
        st, a = mlib.msc_decide(st, _pod(50001.0, data=1))
        self.assertEqual([], a)
        st, a = mlib.msc_decide(st, _pod(50002.0, data=2))
        self.assertEqual([], a)
        st, a = mlib.msc_decide(st, _pod(50003.0, data=2))
        self.assertEqual(mlib.msc_eva_actions("EvaExit", mlib.MSC_LEG_HIGH, st.params), a)
        self.assertEqual((mlib.MSC_LEG_HIGH.eva_exit, 2, 50003.0),
                         (st.phase, st.high_data, st.high_collected_ut))

    def test_a_kerbal_frame_never_counts_as_the_pods(self):
        st = _at(mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.run, run_attempts=1,
                 last_run_ut=50000.0)
        for ut in (50001.0, 50002.0, 50003.0):
            st, a = mlib.msc_decide(st, _kerbal(ut, data=5))
            self.assertEqual([], a)
        self.assertEqual(mlib.MSC_LEG_HIGH.run, st.phase)

    def test_a_short_run_is_re_run_then_named(self):
        st = _at(mlib.MSC_SCIENCE, mlib.MSC_LEG_LOW.run, run_attempts=1,
                 last_run_ut=50000.0, high_data=2)
        runs = 0
        for ut in range(50001, 50122, 5):
            st, a = mlib.msc_decide(st, _pod(float(ut), data=3, **_LOW_ORBIT))
            runs += _kinds(a).count(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS)
            if a:
                self.assertEqual("crewReport,mysteryGoo,temperatureScan", a[0].text)
            if st.done:
                break
        self.assertEqual(mlib.MSC_RUN_MAX_ATTEMPTS - 1, runs)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MSC_RUN_SHORT, st.flake_reason)
        self.assertIn("read 3 of 5", st.flake_reason)

    def test_the_exit_answer_runs_the_kerbals_report(self):
        leg = mlib.MSC_LEG_HIGH
        st = _at(mlib.MSC_SCIENCE, leg.eva_exit)
        st, a = mlib.msc_decide(st, _answer(leg.exit_tag, 50001.0, payload=_EXIT_PAYLOAD))
        self.assertEqual([Action(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS, text="evaReport")], a)
        self.assertEqual((leg.eva_report, "OK", "Jebediah Kerman", "4242"),
                         (st.phase, st.high_exit_result, st.high_kerbal, st.high_eva_pid))
        st, a = mlib.msc_decide(st, _kerbal(50002.0, data=1))
        self.assertEqual([], a)
        st, a = mlib.msc_decide(st, _kerbal(50003.0, data=1))
        self.assertEqual(mlib.msc_eva_actions("EvaBoard", leg, st.params), a)
        self.assertEqual((leg.eva_board, 1, 50003.0),
                         (st.phase, st.high_report_data, st.high_report_ut))

    def test_a_missing_report_is_named(self):
        leg = mlib.MSC_LEG_LOW
        st = _at(mlib.MSC_SCIENCE, leg.eva_report, run_attempts=1, last_run_ut=50000.0)
        st, _ = mlib.msc_decide(st, _kerbal(50000.0 + 121.0, data=0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MSC_EVA_REPORT_SHORT, st.flake_reason)

    def test_the_other_legs_and_other_verbs_answers_are_never_read(self):
        # The high leg's board OK still rides the snapshot when the low exit waits.
        st, a = mlib.msc_decide(_at(mlib.MSC_SCIENCE, mlib.MSC_LEG_LOW.eva_exit),
                                _answer(mlib.MSC_LEG_HIGH.board_tag, 50001.0))
        self.assertEqual(([], mlib.MSC_LEG_LOW.eva_exit, False), (a, st.phase, st.done))
        st, a = mlib.msc_decide(_at(mlib.MSC_SCIENCE, mlib.MSC_LEG_LOW.eva_board),
                                _answer(mlib.MSC_LEG_LOW.exit_tag, 50001.0))
        self.assertEqual(([], mlib.MSC_LEG_LOW.eva_board), (a, st.phase))

    def test_each_refusal_is_named(self):
        for leg in mlib.MSC_LEGS:
            for phase, tag, giveup in ((leg.eva_exit, leg.exit_tag,
                                        mlib.MSC_EVA_EXIT_REFUSED),
                                       (leg.eva_board, leg.board_tag,
                                        mlib.MSC_BOARD_REFUSED)):
                for result in ("ERROR", "TIMEOUT"):
                    with self.subTest(phase=phase, result=result):
                        st, a = mlib.msc_decide(
                            _at(mlib.MSC_SCIENCE, phase),
                            _answer(tag, 50001.0, result=result,
                                    payload=(("msg", "board-timeout"),)))
                        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
                        self.assertIn(giveup, st.flake_reason)
                        self.assertIn("board-timeout", st.flake_reason)
                        self.assertEqual([], a)

    def test_the_settle_needs_the_pod_and_every_reading(self):
        leg = mlib.MSC_LEG_LOW
        st = _at(mlib.MSC_SCIENCE, leg.settle, **{"high_data": 2})
        for ut in (50001.0, 50002.0, 50006.0):
            st, a = mlib.msc_decide(st, _pod(ut, data=4, **_LOW_ORBIT))
            self.assertEqual([], a)
        self.assertEqual(0, st.settle_streak)
        st, _ = mlib.msc_decide(st, _pod(50000.0 + 901.0, data=4, **_LOW_ORBIT))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MSC_BOARD_NOT_ABOARD, st.flake_reason)

    def test_the_low_settle_departs_with_the_nodes_cleared(self):
        leg = mlib.MSC_LEG_LOW
        st = _at(mlib.MSC_SCIENCE, leg.settle)
        for ut in (50001.0, 50002.0):
            st, a = mlib.msc_decide(st, _pod(ut, data=5, **_LOW_ORBIT))
            self.assertEqual([], a)
        st, a = mlib.msc_decide(st, _pod(50006.0, data=5, **_LOW_ORBIT))
        self.assertEqual([Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES),
                          Action(mlib.ACTION_MJ_PLAN_MOON_RETURN, 30000.0)], a)
        self.assertEqual((mlib.MOR_DEPART, mlib.MOR_DEPART_PLAN), (st.block, st.phase))
        self.assertEqual(50006.0, st.low_aboard_ut)

    def test_leaving_the_mun_or_losing_the_vessel_fails(self):
        st, _ = mlib.msc_decide(_at(mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.run),
                                _pod(50001.0, body="Kerbin"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        st, _ = mlib.msc_decide(_at(mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.eva_report),
                                snap(ut=50001.0, vessel_lost=True))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)

    def test_the_eva_phase_budget_flakes(self):
        st, _ = mlib.msc_decide(_at(mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.eva_board),
                                _answer("other", 50000.0 + 901.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("timed out", st.flake_reason)


class LoweringTests(unittest.TestCase):
    def _plan(self, leg="PE", **over):
        phase = mlib.MSC_LOWER_PLAN_PE if leg == "PE" else mlib.MSC_LOWER_PLAN_AP
        return _at(mlib.MSC_LOWER, phase, params=_params(sciLowerMaxDvMps=150), **over)

    def _park(self, ut, **kw):
        fields = dict(ut=ut, body="Mun", situation="ORBITING", periapsis=140_000.0,
                      apoapsis=141_000.0, time_to_periapsis=600.0, liquid_fuel=200.0,
                      electric_charge=40.0)
        fields.update(kw)
        return snap(**fields)

    def test_plan_add_read_back_execute(self):
        st, a = mlib.msc_decide(self._plan(), self._park(50001.0))
        self.assertEqual([mlib.ACTION_ADD_MANEUVER_NODE], _kinds(a))
        self.assertLess(a[0].value, 0.0)
        self.assertEqual(50601.0, a[0].node_ut)
        self.assertEqual(mlib.MSC_LOWER_NODE_LABEL, a[0].text)
        self.assertEqual(a[0].value, st.lower_pe_dv)
        st, a = mlib.msc_decide(st, self._park(50002.0, node_count=1))
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_NODES)], a)
        self.assertEqual(mlib.MSC_LOWER_BURN_PE, st.phase)

    def test_an_unread_node_is_re_added_then_named(self):
        st = self._plan()
        added = 0
        for i in range(60):
            st, a = mlib.msc_decide(st, self._park(50001.0 + 2.0 * i))
            added += _kinds(a).count(mlib.ACTION_ADD_MANEUVER_NODE)
            if st.done:
                break
        self.assertEqual(mlib.MSC_LOWER_PLAN_MAX_ATTEMPTS, added)
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MSC_LOWER_PLAN_REFUSED, st.flake_reason)

    def test_a_stray_node_is_cleared_before_planning(self):
        st, a = mlib.msc_decide(self._plan(), self._park(50001.0, node_count=1))
        self.assertEqual([Action(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES)], a)
        self.assertEqual(0, st.lower_plan_attempts)

    def test_an_over_cap_plan_is_named(self):
        st, _ = mlib.msc_decide(self._plan(leg="AP"),
                                self._park(50001.0, periapsis=30_000.0,
                                           apoapsis=2_000_000.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn("over-cap", st.flake_reason)

    def test_a_leg_already_met_is_skipped(self):
        st, a = mlib.msc_decide(self._plan(),
                                self._park(50001.0, periapsis=30_000.0,
                                           apoapsis=140_000.0, time_to_periapsis=900.0))
        self.assertEqual(("PE",), st.lower_skipped)
        self.assertEqual(mlib.MSC_LOWER_PLAN_AP, st.phase)
        self.assertEqual([mlib.ACTION_ADD_MANEUVER_NODE], _kinds(a))
        # Both met: straight to the low run.
        st, a = mlib.msc_decide(self._plan(), self._park(50001.0, **_LOW_ORBIT))
        self.assertEqual(("PE", "AP"), st.lower_skipped)
        self.assertEqual(mlib.MSC_LEG_LOW.run, st.phase)
        self.assertEqual([mlib.ACTION_RUN_SCIENCE_EXPERIMENTS], _kinds(a))

    def _burn(self, leg="PE", **over):
        phase = mlib.MSC_LOWER_BURN_PE if leg == "PE" else mlib.MSC_LOWER_BURN_AP
        fields = dict(lower_node_ut=50600.0, lower_plan_attempts=1)
        fields.update(over)
        return _at(mlib.MSC_LOWER, phase, **fields)

    def test_a_burned_leg_is_confirmed_then_the_next_planned(self):
        st = self._burn()
        down = dict(periapsis=31_000.0, apoapsis=140_000.0, time_to_periapsis=1800.0)
        for ut in (50700.0, 50701.0):
            st, a = mlib.msc_decide(st, self._park(ut, **down))
            self.assertEqual([], a)
        st, a = mlib.msc_decide(st, self._park(50702.0, **down))
        self.assertEqual(mlib.MSC_LOWER_PLAN_AP, st.phase)
        self.assertEqual([mlib.ACTION_ADD_MANEUVER_NODE], _kinds(a))
        self.assertAlmostEqual(52502.0, a[0].node_ut)

    def test_the_second_leg_lands_in_the_low_run(self):
        st = self._burn(leg="AP")
        for ut in (52600.0, 52601.0):
            st, _ = mlib.msc_decide(st, self._park(ut, **_LOW_ORBIT))
        st, a = mlib.msc_decide(st, self._park(52602.0, **_LOW_ORBIT))
        self.assertEqual(mlib.MSC_LEG_LOW.run, st.phase)
        self.assertEqual([Action(mlib.ACTION_RUN_SCIENCE_EXPERIMENTS,
                                 text="crewReport,mysteryGoo,temperatureScan")], a)
        self.assertEqual((45_200.0, 30_100.0), (st.low_gate_ap, st.low_gate_pe))

    def test_a_missed_lowering_is_named(self):
        st = self._burn()
        for i in range(mlib.MSC_LOWER_MISS_FRAMES):
            st, a = mlib.msc_decide(st, self._park(50700.0 + i, periapsis=90_000.0))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MSC_LOWER_MISSED, st.flake_reason)

    def test_a_dead_executor_is_re_issued_then_named(self):
        st = self._burn()
        issued = 0
        for i in range(40):
            st, a = mlib.msc_decide(st, self._park(50001.0 + i, node_count=1,
                                                   node_executor_enabled=0))
            issued += _kinds(a).count(mlib.ACTION_MJ_EXECUTE_NODES)
            if st.done:
                break
        self.assertEqual(mlib.MOR_EXEC_REISSUE_LIMIT, issued)
        self.assertIn(mlib.MSC_LOWER_EXEC_DOWN, st.flake_reason)

    def test_a_missed_window_and_the_budget_are_named(self):
        st, a = mlib.msc_decide(self._burn(), self._park(
            50600.0 + mlib.MOR_DEPART_WINDOW_GRACE_SECONDS + 1.0, node_count=1,
            node_executor_enabled=1))
        self.assertIn(mlib.MSC_LOWER_WINDOW_MISSED, st.flake_reason)
        self.assertIn(mlib.ACTION_MJ_ABORT_AND_CLEAR_NODES, _kinds(a))
        st, _ = mlib.msc_decide(self._burn(lower_node_ut=70000.0), self._park(
            50000.0 + 12001.0, node_count=1, node_executor_enabled=1))
        self.assertIn("timed out", st.flake_reason)

    def test_leaving_the_mun_while_lowering_fails(self):
        st, _ = mlib.msc_decide(self._burn(), self._park(50001.0, body="Kerbin"))
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)


class WatchdogAndKeptTests(unittest.TestCase):
    def test_a_sustained_low_reading_on_a_pod_phase_ends_the_flight_by_name(self):
        st = _at(mlib.MSC_LOWER, mlib.MSC_LOWER_BURN_PE, lower_node_ut=60000.0)
        for i in range(4):
            st, a = mlib.msc_decide(st, _pod(50001.0 + i, electric_charge=0.1,
                                             node_count=1, node_executor_enabled=1))
        self.assertEqual(mlib.MISSION_FLAKE, st.verdict)
        self.assertIn(mlib.MSC_EC_EXHAUSTED, st.flake_reason)
        self.assertEqual([mlib.ACTION_CUT_THROTTLE, mlib.ACTION_CANCEL_WARP], _kinds(a))

    def test_the_eva_phases_and_the_home_block_are_not_read(self):
        for block, phase in ((mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.eva_exit),
                             (mlib.MSC_SCIENCE, mlib.MSC_LEG_HIGH.eva_report),
                             (mlib.MSC_SCIENCE, mlib.MSC_LEG_LOW.settle),
                             (mlib.MFR_HOME, mlib.MFR_HOME), (mlib.MOR_ORBIT, "PARK")):
            st = _at(block, phase)
            for _ in range(10):
                st, tripped = mlib.msc_observe(st, snap(electric_charge=0.0))
                self.assertFalse(tripped, phase)
            self.assertEqual(0, st.ec_low_frames)

    def test_the_departure_is_read_and_the_minimum_kept(self):
        st = _at(mlib.MOR_DEPART, mlib.MOR_DEPART_PLAN)
        for ec in (0.1, 0.1, 0.1, 5.0):
            st, _ = mlib.msc_observe(st, snap(electric_charge=ec))
        self.assertEqual(0, st.ec_low_frames)
        self.assertEqual(0.1, st.ec_min)

    def test_the_home_block_keeps_the_last_count_read(self):
        st = _at(mlib.MFR_HOME, mlib.MFR_HOME)
        st, _ = mlib.msc_observe(st, snap(science_data_count=5))
        st, _ = mlib.msc_observe(st, snap(science_data_count=-1))
        self.assertEqual(5, st.kept_data)
        st, _ = mlib.msc_observe(st, snap(science_data_count=3))
        self.assertEqual(3, st.kept_data)
        st, _ = mlib.msc_observe(_at(mlib.MFR_TRIM, mlib.MFR_TRIM),
                                 snap(science_data_count=5))
        self.assertEqual(-1, st.kept_data)


class FullFlightTests(unittest.TestCase):
    def test_the_whole_flight_is_mission_ok_with_every_row_met(self):
        control = FakeMissionControl(_whole_flight())
        code, result = run(b11_mun_science_return.SPEC, PLAIN_PARAMS, control,
                           budget=900.0)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        self.assertEqual("b11_mun_science_return", result["mission"])
        kinds = _kinds(control.actions)
        self.assertNotIn(mlib.ACTION_PARSEK_COMMIT_TREE, kinds)
        seam = [(a.seam_verb, a.seam_tag) for a in control.actions
                if a.kind == mlib.ACTION_PARSEK_SEAM_COMMAND]
        self.assertEqual([("EvaExit", "cs6cevaexithigh"), ("EvaBoard", "cs6cevaboardhigh"),
                          ("EvaExit", "cs6cevaexitlow"), ("EvaBoard", "cs6cevaboardlow")],
                         seam)
        runs = [a.text for a in control.actions
                if a.kind == mlib.ACTION_RUN_SCIENCE_EXPERIMENTS]
        self.assertEqual(["mysteryGoo,temperatureScan", "evaReport",
                          "crewReport,mysteryGoo,temperatureScan", "evaReport"], runs)
        self.assertEqual(2, kinds.count(mlib.ACTION_ADD_MANEUVER_NODE))
        self.assertEqual(1, kinds.count(mlib.ACTION_MJ_PLAN_MOON_RETURN))
        # TLI, capture, the two lowering nodes, departure.
        self.assertEqual(5, kinds.count(mlib.ACTION_MJ_EXECUTE_NODES))
        phases = result["phasesReached"]
        for phase in ((mlib.B5_PARK,) + mlib.MSC_LEG_HIGH.phases
                      + (mlib.MSC_LOWER_PLAN_PE, mlib.MSC_LOWER_BURN_PE,
                         mlib.MSC_LOWER_PLAN_AP, mlib.MSC_LOWER_BURN_AP)
                      + mlib.MSC_LEG_LOW.phases
                      + (mlib.MOR_DEPART_PLAN, mlib.MOR_DEPART_BURN,
                         mlib.MOR_DEPART_COAST, mlib.MFR_TRIM, mlib.MSC_TERMINAL_PHASE)):
            self.assertIn(phase, phases)
        self.assertNotIn(mlib.B5_ORBIT_COMMIT, phases)
        names = [a["name"] for a in result["assertions"]]
        self.assertEqual(["reachedOrbit", "reachedTargetSoi", "flybyPeriapsisFloor",
                          "capturedInTargetOrbit", "parkedStable",
                          "highSpaceScienceCollected", "highSpaceEvaReportBoarded",
                          "lowOrbitReached", "lowSpaceScienceCollected",
                          "lowSpaceEvaReportBoarded", "departedTargetOrbit",
                          "returnPeriapsisInWindow", "landedSituation",
                          "craftCanopyObserved", "scienceKeptToSplashdown"], names)
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])

    def test_a_reading_lost_on_the_way_down_fails_the_kept_row(self):
        control = FakeMissionControl(_whole_flight(kept=3))
        _, result = run(b11_mun_science_return.SPEC, PLAIN_PARAMS, control, budget=900.0)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, result["verdict"])
        unmet = [a["name"] for a in result["assertions"] if not a["met"]]
        self.assertEqual(["scienceKeptToSplashdown"], unmet)

    def test_rows_fail_closed_on_a_flight_that_never_parked(self):
        p = _params()
        rows = {r.name: r for r in mlib.evaluate_msc_assertions(
            [], p, mlib.msc_initial_state(p))}
        for name in ("highSpaceScienceCollected", "highSpaceEvaReportBoarded",
                     "lowOrbitReached", "lowSpaceScienceCollected",
                     "lowSpaceEvaReportBoarded", "departedTargetOrbit",
                     "returnPeriapsisInWindow", "landedSituation", "craftCanopyObserved",
                     "scienceKeptToSplashdown"):
            self.assertFalse(rows[name].met, name)
        self.assertNotIn("treeCommitted", rows)


class ShellTests(unittest.TestCase):
    def test_the_control_is_cs6s_plus_the_science_channels(self):
        mine = b11_mun_science_return.make_control()
        cs6 = b11_mun_orbit_return.make_control()
        for key in ("_read_docking", "_read_node_executor", "_read_periapsis",
                    "_read_chute", "_use_mechjeb"):
            self.assertTrue(getattr(mine, key), key)
            self.assertEqual(getattr(cs6, key), getattr(mine, key), key)
        self.assertTrue(mine._read_science)
        self.assertFalse(cs6._read_science)

    def test_the_spec_shape(self):
        spec = b11_mun_science_return.SPEC
        self.assertEqual("b11_mun_science_return", spec.name)
        self.assertTrue(spec.allow_rails_warp)
        self.assertEqual(4.0, spec.max_physics_warp)
        state = spec.build_state(PARAMS)
        self.assertIsInstance(state, mlib.MscState)
        self.assertIs(state.flight, mlib.frozen_detector_holder(state))

    def test_flighteff_names_the_machine(self):
        import flighteff
        self.assertEqual("msc_decide",
                         flighteff.MISSION_MACHINES["b11_mun_science_return"])

    def test_each_eva_verb_polls_at_twice_its_seam_budget(self):
        import hlib
        for verb in ("EvaExit", "EvaBoard"):
            with self.subTest(verb=verb):
                self.assertEqual(2.0 * hlib.DISPATCH_DEFERRAL_BUDGET_SECONDS[verb],
                                 mlib.seam_command_poll_seconds(verb))


class SchemaAndSpecTests(unittest.TestCase):
    OWN_KEYS = set(SCIENCE_PARAMS) - {"evaSettleSeconds", "evaSettleFrames",
                                      "evaPhaseTimeoutSeconds", "electricChargeFloor",
                                      "electricChargeDebounceFrames"}
    SHARED_EVA_KEYS = {"evaKerbalName", "evaSettleSeconds", "evaSettleFrames",
                       "evaPhaseTimeoutSeconds", "electricChargeFloor",
                       "electricChargeDebounceFrames"}

    def test_mor_and_mlr_keys_are_declared_identically(self):
        mine = _schema("b11_mun_science_return")["params"]
        mor = _schema("b11_mun_orbit_return")["params"]
        mlr = _schema("b13_mun_landing_return")["params"]
        for key, decl in mor.items():
            self.assertEqual(decl, mine.get(key), key)
        for key in self.SHARED_EVA_KEYS:
            self.assertEqual(mlr[key], mine.get(key), key)
        self.assertEqual(self.OWN_KEYS, set(mine) - set(mor) - self.SHARED_EVA_KEYS)

    def test_the_cs6c_params_validate_and_build(self):
        import hlib
        spec = _spec(CS6C_SPEC)
        self.assertEqual("b11_mun_science_return", spec["driver"]["mission"])
        params = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(
            params, _schema("b11_mun_science_return")))
        p = mlib.msc_params_from_dict(params)
        self.assertGreater(p.mj_engage_confirm_frames, 0)
        self.assertTrue(p.orbit.capture_enabled)
        self.assertFalse(p.orbit.landing_enabled)
        self.assertEqual(p.return_periapsis, p.depart_periapsis)
        self.assertLess(p.return_periapsis_max, p.home.warp_above_alt)
        self.assertLess(p.home.chute_deploy_alt, p.return_periapsis_min)
        self.assertGreater(p.ec_floor, 0.0)
        self.assertGreaterEqual(p.orbit.park_timeout - p.orbit.park_dwell, 420.0)
        # The Mun's spaceAltitudeThreshold is 60 km: the high gate sits over it, the
        # low gate under it, the low orbit over the highest terrain (about 7.1 km).
        self.assertGreater(p.sci_high_min_periapsis, 60000.0)
        self.assertLess(p.sci_low_max_apoapsis, 60000.0)
        self.assertGreater(p.sci_low_min_periapsis, 7100.0)
        # B11's arrivals (136-141 km) clear the high gate.
        self.assertLess(p.sci_high_min_periapsis, 136000.0)
        # Both lowering legs from a 141 km circular park fit the cap.
        frame = snap(ut=0.0, periapsis=141_000.0, apoapsis=141_000.0,
                     time_to_periapsis=900.0)
        dv, _, why = mlib.msc_lower_node_plan(frame, p, "PE")
        self.assertEqual("", why)
        frame = snap(ut=0.0, periapsis=p.sci_low_periapsis, apoapsis=141_000.0,
                     time_to_periapsis=900.0)
        dv2, _, why = mlib.msc_lower_node_plan(frame, p, "AP")
        self.assertEqual("", why)
        self.assertLess(-(dv + dv2), 100.0)

    def test_the_spec_flies_once_recovers_then_researches_both_nodes(self):
        spec = _spec(CS6C_SPEC)
        self.assertEqual("fixtures/saves/cs-career-mun3", spec["fixture"]["saveTemplate"])
        steps = spec["driver"]["steps"]
        self.assertEqual(1, sum(1 for s in steps if s.get("phase") == "mission"))
        mission = next(i for i, s in enumerate(steps) if s.get("phase") == "mission")
        launch = steps[mission - 1]
        self.assertEqual("LaunchFromEditor", launch["cmd"])
        recover = steps[mission + 1]
        self.assertEqual("Recover", recover["cmd"])
        self.assertEqual("${%s.pid}" % launch["label"], recover["args"]["pid"])
        self.assertEqual("CS Mun Science", steps[mission - 2]["args"]["craft"])
        self.assertFalse(any(s.get("cmd") == "KscAction" for s in steps[:mission]))
        research = [(i, s["args"]) for i, s in enumerate(steps)
                    if s.get("cmd") == "KscAction"]
        self.assertEqual([{"action": "research-node", "node": "basicScience"},
                          {"action": "research-node", "node": "electrics"}],
                         [a for _, a in research])
        # Both after the recovery (the science is credited there), before the ground
        # truth's Flea, which runs in FLIGHT and closes with them in the pools.
        flea = next(i for i, s in enumerate(steps) if s.get("cmd") == "GoToEditor"
                    and s["args"]["craft"] == "Jumping Flea")
        self.assertTrue(all(mission + 1 < i < flea for i, _ in research))
        self.assertFalse(any(s.get("cmd") == "CommitTree" and s.get("expect") == "OK"
                             for s in steps))

    def test_the_pins(self):
        contracts = _spec(CS6C_SPEC)["expectations"]["logContracts"]
        required = contracts["required"]
        forbidden = contracts["forbidden"]
        # Every subject the two runs and the two EVAs bring home, body-qualified.
        subjects = [r for r in required if r.startswith("Science subject captured: ")]
        for sid in ("mysteryGoo@MunInSpaceHigh", "temperatureScan@MunInSpaceHigh",
                    "evaReport@MunInSpaceHigh", "crewReport@MunInSpaceLow",
                    "mysteryGoo@MunInSpaceLow", "temperatureScan@MunInSpaceLow"):
            self.assertTrue(any(r.startswith("Science subject captured: %s " % sid)
                                for r in subjects), sid)
        biome = re.compile(next(r for r in subjects if "evaReport@MunInSpaceLow" in r))
        self.assertTrue(biome.search("Science subject captured: evaReport@MunInSpaceLow"
                                     "Midlands amount=24.0 increment=24.00 total=24.0 "
                                     "gainMultiplier=1 reason='VesselRecovery'"))
        self.assertFalse(biome.search("Science subject captured: evaReport@MunInSpaceHigh"
                                      " amount=16.0"))
        # The research, each booked.
        for node, cost in (("basicScience", 45), ("electrics", 90)):
            self.assertIn("Game state: TechResearched '%s' \\(cost=%d\\)" % (node, cost),
                          required)
            self.assertTrue(any(r.startswith("kscaction action=research-node target=%s "
                                             "applied=true" % node) for r in required))
        # The interim batch line, and the milestone rules.
        self.assertIn("BATCH_COMPLETE v1 total=3 passed=[1-9][0-9]* failed=0 "
                      "skipped=[0-9]+ category=LedgerGroundTruth scene=FLIGHT", required)
        self.assertTrue(any(r.startswith("Milestone enriched: 'Mun/Spacewalk' ")
                            for r in required))
        bare = re.compile(forbidden[0])
        for mid in ("Spacewalk", "Science", "Orbit"):
            self.assertTrue(bare.search("Milestone enriched: '%s' funds=1" % mid), mid)
        self.assertFalse(bare.search("Milestone enriched: 'Mun/Spacewalk' funds=1"))
        repaid = re.compile(forbidden[1])
        for mid in ("Mun/Flyby", "Mun/Orbit", "Mun/ReturnFromOrbit", "Mun/ReturnFromFlyBy",
                    "Kerbin/Science"):
            self.assertTrue(repaid.search("Milestone enriched: '%s' funds=0" % mid), mid)
        for mid in ("Mun/Spacewalk", "Mun/Science"):
            self.assertFalse(repaid.search("Milestone enriched: '%s' funds=0" % mid), mid)
        rollout = re.compile(next(r for r in required if "VesselRollout" in r
                                  and "2932" not in r))
        self.assertFalse(rollout.search("Game state: FundsChanged -2932 (VesselRollout)"))
        self.assertTrue(rollout.search("Game state: FundsChanged -14912 (VesselRollout)"))


if __name__ == "__main__":
    unittest.main()
