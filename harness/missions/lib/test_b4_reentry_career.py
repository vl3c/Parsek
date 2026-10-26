"""Unit cells for mission `b4_reentry_career` and the B4 machine's career opt-ins.

The shell's claim is "B4, plus one control opt-in, plus spec keys the default walk
never sets". Each class pins one part of that claim, because each drifting costs a
flight to discover:

  1. THE OPT-IN. The career control tolerates the un-upgraded Tracking Station's
     maneuver-node refusal; the sandbox `b4_reentry` control still does not.
  2. THE DEFAULT WALK IS UNTOUCHED. With every career key absent the machine emits
     the PRELAUNCH actions it always did (the B4 suites in test_mlib / test_shells
     pin the rest of that walk; they pass unchanged).
  3. THE OPT-INS DECIDE WHAT THEY CLAIM: the PRELAUNCH engage gate, the three
     MechJeb setters, and the native circularization with RF-13's give-ups.
  4. THE RUNNER'S SETTERS read back, and a refused setting withholds the engage.

NO krpc, NO KSP, NO network. Import path matches the sibling suites.
"""

import dataclasses
import os
import sys
import tomllib
import types
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
import b4_reentry                  # noqa: E402
import b4_reentry_career           # noqa: E402
from mlib import Action            # noqa: E402
from test_shells import B4_PARAMS, FakeMissionControl, run, snap  # noqa: E402

CS4_SPEC = "CS-4-career-orbit-return"

CAREER_KEYS = {
    "circularizeMode", "circTargetPeriapsisMeters", "circPeriapsisDebounceFrames",
    "circStartVerticalSpeedMps", "circPitchGainDegPerMps", "circPitchMinDeg",
    "circPitchMaxDeg", "headingDeg", "circIgniteFrames", "mjEngageConfirmFrames",
    "autostageLimit", "skipCircularization", "ascentType",
}

# The CS-4 shape over the B4 shell fixture: B4_PARAMS plus the career opt-ins.
NATIVE_PARAMS = dict(B4_PARAMS, **{
    "circularizeMode": "native",
    "circTargetPeriapsisMeters": 75000,
    "circStartVerticalSpeedMps": 30,
    "circPitchGainDegPerMps": 0.5,
    "circPitchMinDeg": -10,
    "circPitchMaxDeg": 45,
    "headingDeg": 90,
    "mjEngageConfirmFrames": 3,
    "autostageLimit": 2,
    "skipCircularization": True,
    "ascentType": 0,
    "reentryStageCount": 1,
})


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


def _cs4_spec():
    with open(os.path.join(_HARNESS, "scenarios", CS4_SPEC + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _native_state(phase, **overrides):
    base = mlib.b4_initial_state(mlib.b4_params_from_dict(NATIVE_PARAMS))
    fields = {**base.__dict__, "phase": phase, "phase_entry_ut": 0.0}
    fields.update(overrides)
    return base.__class__(**fields)


class CareerOptInTests(unittest.TestCase):
    def test_the_career_shell_tolerates_unreadable_maneuver_nodes(self):
        self.assertTrue(b4_reentry_career.make_control()._tolerate_unreadable_nodes)

    def test_the_sandbox_shell_keeps_the_default_off(self):
        self.assertFalse(b4_reentry.make_control()._tolerate_unreadable_nodes)

    def test_the_controls_differ_in_the_opt_in_and_the_client_name_only(self):
        career = dict(vars(b4_reentry_career.make_control()))
        sandbox = dict(vars(b4_reentry.make_control()))
        for key in ("_tolerate_unreadable_nodes", "_client_name"):
            career.pop(key)
            sandbox.pop(key)
        self.assertEqual(sorted(sandbox), sorted(career))
        plain = (bool, int, float, str, type(None))
        config = [k for k, v in sandbox.items() if isinstance(v, plain)]
        self.assertIn("_read_chute", config)
        self.assertIn("_use_mechjeb", config)
        for key in config:
            self.assertEqual(sandbox[key], career[key], key)

    def test_the_spec_is_b4s_apart_from_name_and_control(self):
        career = b4_reentry_career.SPEC
        sandbox = b4_reentry.SPEC
        self.assertEqual("b4_reentry_career", career.name)
        for field in dataclasses.fields(career):
            if field.name in ("name", "build_state", "decide", "evaluate", "make_control"):
                continue
            self.assertEqual(getattr(sandbox, field.name), getattr(career, field.name),
                             field.name)


class SchemaTests(unittest.TestCase):
    def test_every_b4_key_is_declared_identically(self):
        b4 = _schema("b4_reentry")["params"]
        career = _schema("b4_reentry_career")["params"]
        for key, decl in b4.items():
            self.assertEqual(decl, career.get(key), key)

    def test_the_career_keys_are_exactly_the_opt_ins_and_optional(self):
        b4 = _schema("b4_reentry")["params"]
        career = _schema("b4_reentry_career")["params"]
        self.assertEqual(CAREER_KEYS, set(career) - set(b4))
        for key in CAREER_KEYS:
            self.assertFalse(career[key]["required"], key)

    def test_the_sandbox_schema_declares_no_career_key(self):
        # A sandbox lane must not be able to opt in without a schema change.
        self.assertEqual(set(), CAREER_KEYS & set(_schema("b4_reentry")["params"]))

    def test_every_career_key_is_read_by_the_params_builder(self):
        import inspect
        src = inspect.getsource(mlib.b4_params_from_dict)
        for key in CAREER_KEYS:
            self.assertIn('"%s"' % key, src, key)

    def test_the_cs4_params_validate_against_the_career_schema(self):
        import hlib
        spec = _cs4_spec()
        self.assertEqual("b4_reentry_career", spec["driver"]["mission"])
        params = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(
            params, _schema("b4_reentry_career")))
        p = mlib.b4_params_from_dict(params)
        self.assertEqual("native", p.circularize_mode)
        self.assertTrue(p.skip_circularization)
        self.assertGreater(p.mj_engage_confirm_frames, 0)
        # The machine's own stage after the deorbit is the pod's separation; the
        # MechJeb autostage limit must stop above it.
        self.assertGreater(p.autostage_limit, p.reentry_stage_count - 1)

    def test_an_unknown_circularize_mode_is_refused(self):
        with self.assertRaises(ValueError):
            mlib.b4_params_from_dict(dict(B4_PARAMS, circularizeMode="Native"))


class DefaultWalkUnchangedTests(unittest.TestCase):
    def test_the_default_params_carry_every_opt_in_off(self):
        p = mlib.b4_params_from_dict(B4_PARAMS)
        self.assertEqual("mechjeb", p.circularize_mode)
        self.assertEqual(0, p.mj_engage_confirm_frames)
        self.assertIsNone(p.autostage_limit)
        self.assertIsNone(p.skip_circularization)
        self.assertIsNone(p.ascent_type)

    def test_the_default_launch_is_the_same_four_actions_in_one_frame(self):
        state = mlib.b4_initial_state(mlib.b4_params_from_dict(B4_PARAMS))
        state, actions = mlib.b4_decide(state, snap(ut=0.0))
        self.assertEqual(mlib.B4_MJ_ASCENT, state.phase)
        self.assertEqual([Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 80000.0),
                          Action(mlib.ACTION_MJ_ENABLE_AUTOSTAGE),
                          Action(mlib.ACTION_MJ_ENGAGE_ASCENT),
                          Action(mlib.ACTION_ACTIVATE_STAGE)], actions)

    def test_the_default_ascent_still_hands_off_to_mechjeb_circularization(self):
        state = mlib.b4_initial_state(mlib.b4_params_from_dict(B4_PARAMS))
        state = dataclasses.replace(state, phase=mlib.B4_MJ_ASCENT)
        state, actions = mlib.b4_decide(
            state, snap(ut=100.0, apoapsis=78000.0, mj_ascent_complete=True))
        self.assertEqual(mlib.B4_CIRCULARIZE, state.phase)
        self.assertEqual([Action(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION)], actions)
        self.assertIsNone(state.flake_reason)


class EngageGateTests(unittest.TestCase):
    def _fresh(self, **over):
        return mlib.b4_initial_state(mlib.b4_params_from_dict(dict(NATIVE_PARAMS, **over)))

    def test_the_setup_writes_every_setting_before_the_engage_and_does_not_stage(self):
        state, actions = mlib.b4_decide(self._fresh(), snap(ut=0.0))
        self.assertEqual(mlib.B4_PRELAUNCH, state.phase)
        self.assertTrue(state.mj_engage_sent)
        self.assertEqual([Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 80000.0),
                          Action(mlib.ACTION_MJ_SET_ASCENT_TYPE, 0.0),
                          Action(mlib.ACTION_MJ_ENABLE_AUTOSTAGE),
                          Action(mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT, 2.0),
                          Action(mlib.ACTION_MJ_SET_SKIP_CIRCULARIZATION, 1.0),
                          Action(mlib.ACTION_MJ_ENGAGE_ASCENT)], actions)

    def test_the_launch_is_staged_on_the_first_frame_that_reads_enabled(self):
        state, _ = mlib.b4_decide(self._fresh(), snap(ut=0.0))
        state, actions = mlib.b4_decide(state, snap(ut=0.5))
        self.assertEqual(mlib.B4_PRELAUNCH, state.phase)
        self.assertEqual([], actions)
        state, actions = mlib.b4_decide(state, snap(ut=1.0, mj_autopilot_enabled=True))
        self.assertEqual(mlib.B4_MJ_ASCENT, state.phase)
        self.assertEqual([Action(mlib.ACTION_ACTIVATE_STAGE)], actions)
        self.assertFalse(state.done)

    def test_an_autopilot_that_never_reads_enabled_flakes_by_name_on_the_pad(self):
        state, _ = mlib.b4_decide(self._fresh(), snap(ut=0.0))
        stages = 0
        for i in range(1, 4):
            state, actions = mlib.b4_decide(state, snap(ut=float(i)))
            stages += sum(1 for a in actions if a.kind == mlib.ACTION_ACTIVATE_STAGE)
        self.assertTrue(state.done)
        self.assertEqual(0, stages)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertEqual(mlib.B4_PRELAUNCH, state.flake_phase)
        verdict, reason = mlib.resolve_flight_verdict(state, [])
        self.assertEqual(mlib.MISSION_FLAKE, verdict)
        self.assertTrue(reason.startswith(mlib.B4_MJ_NOT_ENABLED_GIVEUP), reason)

    def test_unset_setters_emit_nothing(self):
        state = mlib.b4_initial_state(mlib.b4_params_from_dict(
            dict(B4_PARAMS, mjEngageConfirmFrames=2)))
        _, actions = mlib.b4_decide(state, snap(ut=0.0))
        self.assertEqual([Action(mlib.ACTION_MJ_SET_TARGET_APOAPSIS, 80000.0),
                          Action(mlib.ACTION_MJ_ENABLE_AUTOSTAGE),
                          Action(mlib.ACTION_MJ_ENGAGE_ASCENT)], actions)


class NativeCircularizationTests(unittest.TestCase):
    def test_ascent_completion_cuts_the_engine_and_holds_the_law_pitch(self):
        state = _native_state(mlib.B4_MJ_ASCENT)
        state, actions = mlib.b4_decide(state, snap(ut=200.0, apoapsis=79000.0,
                                                    vertical_speed=300.0,
                                                    mj_ascent_complete=True))
        self.assertEqual(mlib.B4_NATIVE_COAST, state.phase)
        # 300 m/s climbing -> the law's floor, -10 deg.
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                          Action(mlib.ACTION_AP_SET_PITCH_HEADING,
                                 pitch_heading=(-10.0, 90.0))], actions)
        self.assertNotIn(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION, [a.kind for a in actions])

    def test_the_coast_waits_for_the_vertical_speed_then_lights_the_engine(self):
        state = _native_state(mlib.B4_NATIVE_COAST, native_last_pitch=-10.0)
        state, actions = mlib.b4_decide(state, snap(ut=210.0, altitude=74000.0,
                                                    vertical_speed=120.0))
        self.assertEqual(mlib.B4_NATIVE_COAST, state.phase)
        self.assertEqual([], actions)    # pitch unchanged: no re-command
        state, actions = mlib.b4_decide(state, snap(ut=250.0, altitude=79500.0,
                                                    vertical_speed=25.0))
        self.assertEqual(mlib.B4_NATIVE_BURN, state.phase)
        self.assertEqual(79500.0, state.circ_start_altitude)
        self.assertEqual(Action(mlib.ACTION_SET_THROTTLE, 1.0), actions[-1])

    def test_the_burn_steers_and_cuts_on_a_debounced_periapsis(self):
        state = _native_state(mlib.B4_NATIVE_BURN, native_last_pitch=-10.0)
        state, actions = mlib.b4_decide(state, snap(ut=251.0, periapsis=-200000.0,
                                                    vertical_speed=-10.0,
                                                    available_thrust=215.0))
        # -0.5 * -10 = +5 deg: re-commanded.
        self.assertEqual([Action(mlib.ACTION_AP_SET_PITCH_HEADING,
                                 pitch_heading=(5.0, 90.0))], actions)
        state, actions = mlib.b4_decide(state, snap(ut=300.0, periapsis=75500.0,
                                                    vertical_speed=-10.0,
                                                    available_thrust=215.0))
        self.assertEqual(mlib.B4_NATIVE_BURN, state.phase)    # one frame is not two
        state, actions = mlib.b4_decide(state, snap(ut=300.5, periapsis=75600.0,
                                                    vertical_speed=-10.0,
                                                    available_thrust=215.0))
        self.assertEqual(mlib.B4_ORBIT, state.phase)
        self.assertEqual(75600.0, state.circ_cut_periapsis)
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                          Action(mlib.ACTION_AP_DISENGAGE)], actions)
        # ORBIT is the default walk's waypoint from here on.
        state, actions = mlib.b4_decide(state, snap(ut=301.0, periapsis=75600.0))
        self.assertEqual(mlib.B4_DEORBIT, state.phase)
        self.assertEqual([Action(mlib.ACTION_AP_POINT_RETROGRADE)], actions)

    def test_an_engine_that_never_lights_is_a_named_loss_and_never_stages(self):
        state = _native_state(mlib.B4_NATIVE_BURN, native_last_pitch=0.0)
        for i in range(20):
            # Inside the 300 s budget, and moving (the frozen detector stays quiet).
            state, actions = mlib.b4_decide(state, snap(ut=10.0 + i, periapsis=-1e5,
                                                        altitude=78000.0 + i,
                                                        available_thrust=0.0))
            self.assertNotIn(mlib.ACTION_ACTIVATE_STAGE, [a.kind for a in actions])
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("engine never lit", state.loss_reason)

    def test_a_dry_engine_short_of_orbit_is_a_named_loss(self):
        state = _native_state(mlib.B4_NATIVE_BURN, native_last_pitch=0.0)
        state, _ = mlib.b4_decide(state, snap(ut=300.0, periapsis=-1e5,
                                              available_thrust=215.0))
        state, actions = mlib.b4_decide(state, snap(ut=301.0, periapsis=40000.0,
                                                    available_thrust=0.0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, state.verdict)
        self.assertIn("propellant exhausted short of orbit", state.loss_reason)
        self.assertEqual([Action(mlib.ACTION_CUT_THROTTLE, 0.0),
                          Action(mlib.ACTION_AP_DISENGAGE)], actions)

    def test_a_stuck_coast_flakes_on_the_circularize_budget(self):
        state = _native_state(mlib.B4_NATIVE_COAST, native_last_pitch=-10.0)
        state, actions = mlib.b4_decide(state, snap(ut=301.0, vertical_speed=200.0))
        self.assertTrue(state.done)
        self.assertEqual(mlib.MISSION_FLAKE, state.verdict)
        self.assertEqual(mlib.B4_NATIVE_COAST, state.flake_phase)
        self.assertEqual([Action(mlib.ACTION_AP_DISENGAGE)], actions)

    def test_the_pitch_law_is_rf13s(self):
        p = mlib.b4_params_from_dict(NATIVE_PARAMS)
        rfo = mlib.rfo_params_from_dict({"circPitchGainDegPerMps": 0.5,
                                         "circPitchMinDeg": -10, "circPitchMaxDeg": 45})
        for vs in (-200.0, -40.0, -1.0, 0.0, 3.0, 30.0, 500.0, float("nan")):
            self.assertEqual(mlib.rfo_circ_pitch(rfo, vs), mlib._b4_native_pitch(p, vs), vs)


class NativeShellFlightTests(unittest.TestCase):
    def _frames(self):
        return [
            snap(ut=0.0, apoapsis=0, periapsis=0, situation="PRE_LAUNCH"),       # setup + engage
            snap(ut=0.5, apoapsis=0, periapsis=0, situation="PRE_LAUNCH",
                 mj_autopilot_enabled=True),                                     # stage
            snap(ut=200.0, apoapsis=79000, periapsis=-300000, altitude=71000.0,
                 vertical_speed=300.0, situation="SUB_ORBITAL",
                 mj_ascent_complete=True),                                       # -> NATIVE-COAST
            snap(ut=240.0, apoapsis=79100, periapsis=-300000, altitude=79000.0,
                 vertical_speed=20.0, situation="SUB_ORBITAL",
                 available_thrust=215.0),                                        # -> NATIVE-BURN
            snap(ut=280.0, apoapsis=81000, periapsis=76000, altitude=79500.0,
                 vertical_speed=-2.0, situation="ORBITING", available_thrust=215.0),
            snap(ut=280.5, apoapsis=81100, periapsis=76200, altitude=79500.0,
                 vertical_speed=-2.0, situation="ORBITING", available_thrust=215.0),  # -> ORBIT
            snap(ut=281.0, apoapsis=81100, periapsis=76200, altitude=79501.0,
                 situation="ORBITING"),                                          # -> DEORBIT
            snap(ut=295.0, apoapsis=81100, periapsis=76100, altitude=79502.0,
                 situation="ORBITING", ap_error=2.0),                            # throttle up
            snap(ut=320.0, apoapsis=81100, periapsis=24000, altitude=79000.0,
                 situation="ORBITING"),                                          # -> REENTRY
            snap(ut=325.0, apoapsis=81100, periapsis=24000, altitude=78000.0,
                 vertical_speed=-50.0, situation="SUB_ORBITAL"),                 # the one stage
            snap(ut=326.0, apoapsis=81100, periapsis=24000, altitude=60000.0,
                 vertical_speed=-100.0, situation="SUB_ORBITAL"),                # warp hop
            snap(ut=450.0, apoapsis=81100, periapsis=24000, altitude=11000.0,
                 vertical_speed=-250.0, situation="FLYING"),                     # -> SPLASHDOWN
            snap(ut=500.0, altitude=800.0, vertical_speed=-8.0, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
            snap(ut=560.0, altitude=0.0, situation="SPLASHED",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        ]

    def test_the_native_walk_resolves_mission_ok_without_a_maneuver_node(self):
        control = FakeMissionControl(self._frames())
        code, result = run(b4_reentry_career.SPEC, NATIVE_PARAMS, control)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        self.assertEqual("b4_reentry_career", result["mission"])
        self.assertEqual(list(mlib.B4_NATIVE_PHASES), result["phasesReached"])
        kinds = [a.kind for a in control.actions]
        self.assertNotIn(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION, kinds)
        # The launch and the ONE owed reentry stage, nothing else: MechJeb's
        # autostage owns the ascent stages and the pod's own stage is never fired.
        stage_idx = [i for i, k in enumerate(kinds) if k == mlib.ACTION_ACTIVATE_STAGE]
        self.assertEqual(2, len(stage_idx))
        self.assertGreater(stage_idx[0], kinds.index(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertTrue(all(a["met"] for a in result["assertions"]), result["assertions"])


class _FakeStaging(object):
    def __init__(self, sticky=True):
        self._sticky = sticky
        self._v = 0

    @property
    def autostage_limit(self):
        return self._v

    @autostage_limit.setter
    def autostage_limit(self, value):
        if self._sticky:
            self._v = value


class _FakeAscent(object):
    def __init__(self, sticky=True, raises=False):
        self.staging_controller = _FakeStaging(sticky)
        self.skip_circularization = False
        self.ascent_type = 0
        self.enabled = False
        self._raises = raises

    def __setattr__(self, name, value):
        if name == "ascent_type" and getattr(self, "_raises", False):
            raise RuntimeError("no such property")
        object.__setattr__(self, name, value)


class RunnerSetterTests(unittest.TestCase):
    def setUp(self):
        self.lines = []
        self._saved = mission_runner._stdout_sink
        mission_runner._stdout_sink = self.lines.append
        self.control = mission_runner.KrpcMissionControl(use_mechjeb=True, client_name="t")
        vessel = types.SimpleNamespace(control=types.SimpleNamespace())
        self.control._conn = types.SimpleNamespace(
            space_center=types.SimpleNamespace(active_vessel=vessel))

    def tearDown(self):
        mission_runner._stdout_sink = self._saved

    def test_settings_that_read_back_are_confirmed_and_the_engage_runs(self):
        self.control._ascent = _FakeAscent()
        for a in (Action(mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT, 2.0),
                  Action(mlib.ACTION_MJ_SET_SKIP_CIRCULARIZATION, 1.0),
                  Action(mlib.ACTION_MJ_SET_ASCENT_TYPE, 0.0),
                  Action(mlib.ACTION_MJ_ENGAGE_ASCENT)):
            self.control.perform(a)
        self.assertEqual("", self.control._mj_config_refusal)
        self.assertEqual(2, self.control._ascent.staging_controller.autostage_limit)
        self.assertTrue(self.control._ascent.skip_circularization)
        self.assertTrue(self.control._ascent.enabled)
        self.assertTrue(any("autostage_limit=2 (read back confirmed)" in l
                            for l in self.lines), self.lines)

    def test_a_setting_that_does_not_stick_withholds_the_engage(self):
        self.control._ascent = _FakeAscent(sticky=False)
        self.control.perform(Action(mlib.ACTION_MJ_SET_AUTOSTAGE_LIMIT, 2.0))
        self.control.perform(Action(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertIn("READ BACK as 0, not 2", self.control._mj_config_refusal)
        self.assertFalse(self.control._ascent.enabled)
        self.assertTrue(any("REFUSING to engage" in l for l in self.lines), self.lines)

    def test_a_setter_that_raises_withholds_the_engage(self):
        self.control._ascent = _FakeAscent(raises=True)
        self.control.perform(Action(mlib.ACTION_MJ_SET_ASCENT_TYPE, 0.0))
        self.control.perform(Action(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertIn("set FAILED", self.control._mj_config_refusal)
        self.assertFalse(self.control._ascent.enabled)

    def test_without_a_career_setter_the_engage_is_unconditional(self):
        self.control._ascent = _FakeAscent()
        self.control.perform(Action(mlib.ACTION_MJ_ENGAGE_ASCENT))
        self.assertTrue(self.control._ascent.enabled)
        self.assertEqual([], self.lines)


if __name__ == "__main__":
    unittest.main()
