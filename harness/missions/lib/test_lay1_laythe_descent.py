"""Unit cells for B4's ORBIT-START opt-in and the LAY-1 lane that flies it.

LAY-1 (todo NEXT-TESTS-TO-BUILD-2026-11-11 item 9) hands `b4_reentry` a crewed pod
already parked in Laythe orbit. The claim is "B4's live-proven walk, entered at the
ORBIT waypoint instead of the pad". Each class pins one part of it:

  1. THE GATE: each refusal by name, the K-frame debounce, the fail-fast give-up.
  2. THE DEFAULT WALK IS UNTOUCHED with the opt-in absent (PRELAUNCH still launches).
  3. THE ROWS: startedInOrbit / landedOnBody appear only with their opt-ins and
     fail closed on missing evidence.
  4. THE SHELL: a fake Laythe flight through the runner ends MISSION-OK with the
     two extra rows met and no ascent action emitted.
  5. THE SPEC: LAY-1's params validate against both B4 schemas and build the mode.

NO krpc, NO KSP, NO network.
"""

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
import b4_reentry                  # noqa: E402
from test_shells import B4_PARAMS, FakeMissionControl, run, snap  # noqa: E402

LAY1_SPEC = "LAY-1-laythe-reentry-splashdown"

ORBIT_START_KEYS = {
    "startInOrbit", "startInOrbitBodyName", "startInOrbitMinPeriapsisMeters",
    "startInOrbitDebounceFrames", "startInOrbitGateMaxFrames", "landingBodyName",
}

# B4_PARAMS (the shell fixture) in orbit-start mode around Laythe. Stage count 2 as
# the lane flies it.
LAYTHE_PARAMS = dict(B4_PARAMS, **{
    "startInOrbit": True,
    "startInOrbitBodyName": "Laythe",
    "startInOrbitMinPeriapsisMeters": 52000,
    "startInOrbitDebounceFrames": 3,
    "startInOrbitGateMaxFrames": 10,
    "landingBodyName": "Laythe",
    "reentryStageCount": 2,
})


def _spec():
    with open(os.path.join(_HARNESS, "scenarios", LAY1_SPEC + ".toml"), "rb") as fh:
        return tomllib.load(fh)


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


def _park(ut, **kw):
    base = dict(ut=ut, body="Laythe", situation="ORBITING", apoapsis=87900.0,
                periapsis=56200.0, altitude=70000.0)
    base.update(kw)
    return snap(**base)


def _state(params=None):
    return mlib.b4_initial_state(mlib.b4_params_from_dict(params or LAYTHE_PARAMS))


def _step(state, frame):
    return mlib.b4_decide(state, frame)


class GateRefusalTests(unittest.TestCase):
    def setUp(self):
        self.p = mlib.b4_params_from_dict(LAYTHE_PARAMS)

    def test_in_gate_park_is_accepted(self):
        self.assertEqual("", mlib.b4_start_gate_refusal(self.p, _park(1.0)))

    def test_each_refusal_names_what_it_read(self):
        cases = (
            (_park(1.0, situation="SUB_ORBITAL"), "situation=SUB_ORBITAL, not ORBITING"),
            (_park(1.0, situation=""), "situation=<unread>, not ORBITING"),
            (_park(1.0, body="Jool"), "body=Jool, not Laythe"),
            (_park(1.0, periapsis=float("nan")), "periapsis unread"),
            (_park(1.0, periapsis=40000.0), "periapsis 40000 m below 52000 m"),
        )
        for frame, reason in cases:
            self.assertEqual(reason, mlib.b4_start_gate_refusal(self.p, frame))

    def test_no_body_named_accepts_any_body(self):
        p = mlib.b4_params_from_dict(dict(LAYTHE_PARAMS, startInOrbitBodyName=""))
        self.assertEqual("", mlib.b4_start_gate_refusal(p, _park(1.0, body="Eve")))


class GateWalkTests(unittest.TestCase):
    def test_k_in_gate_frames_enter_orbit_then_deorbit(self):
        st = _state()
        for i in range(2):
            st, actions = _step(st, _park(10.0 + i))
            self.assertEqual(mlib.B4_PRELAUNCH, st.phase)
            self.assertEqual([], actions)
        st, actions = _step(st, _park(12.0))
        self.assertEqual(mlib.B4_ORBIT, st.phase)
        self.assertEqual([mlib.Action(mlib.ACTION_CUT_THROTTLE, 0.0)], actions)
        self.assertEqual(("Laythe", "ORBITING", 56200.0),
                         (st.start_entry_body, st.start_entry_situation,
                          st.start_entry_periapsis))
        self.assertEqual(12.0, st.phase_entry_ut)
        st, actions = _step(st, _park(13.0))
        self.assertEqual(mlib.B4_DEORBIT, st.phase)
        self.assertEqual([mlib.Action(mlib.ACTION_AP_POINT_RETROGRADE)], actions)

    def test_a_refused_frame_resets_the_streak(self):
        st = _state()
        st, _ = _step(st, _park(1.0))
        st, _ = _step(st, _park(2.0))
        st, _ = _step(st, _park(3.0, periapsis=10000.0))
        self.assertEqual(0, st.start_streak)
        for ut in (4.0, 5.0):
            st, _ = _step(st, _park(ut))
            self.assertEqual(mlib.B4_PRELAUNCH, st.phase)
        st, _ = _step(st, _park(6.0))
        self.assertEqual(mlib.B4_ORBIT, st.phase)

    def test_a_handoff_that_never_passes_fails_fast_naming_the_refusal(self):
        st = _state()
        for i in range(10):
            st, actions = _step(st, _park(float(i), body="Jool"))
            self.assertEqual([], actions)
        self.assertTrue(st.done)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, st.verdict)
        self.assertIn("orbit-start handoff refused on 10 frames", st.loss_reason)
        self.assertIn("body=Jool, not Laythe", st.loss_reason)
        self.assertNotIn(mlib.B4_ORBIT, st.phases_reached)

    def test_the_gate_never_emits_a_launch_action(self):
        st = _state()
        emitted = []
        for i in range(3):
            st, actions = _step(st, _park(float(i)))
            emitted.extend(a.kind for a in actions)
        for kind in (mlib.ACTION_ACTIVATE_STAGE, mlib.ACTION_MJ_ENGAGE_ASCENT,
                     mlib.ACTION_MJ_SET_TARGET_APOAPSIS):
            self.assertNotIn(kind, emitted)

    def test_orbit_start_refuses_the_ascent_only_opt_ins(self):
        for extra in ({"mjEngageConfirmFrames": 3}, {"circularizeMode": "native"}):
            with self.assertRaises(ValueError):
                mlib.b4_params_from_dict(dict(LAYTHE_PARAMS, **extra))


class DefaultWalkTests(unittest.TestCase):
    def test_absent_opt_in_still_launches_from_prelaunch(self):
        p = mlib.b4_params_from_dict(B4_PARAMS)
        self.assertFalse(p.start_in_orbit)
        self.assertEqual("", p.landing_body)
        st, actions = mlib.b4_decide(mlib.b4_initial_state(p),
                                     snap(ut=0.0, situation="PRE_LAUNCH"))
        self.assertEqual(mlib.B4_MJ_ASCENT, st.phase)
        self.assertEqual(mlib.ACTION_ACTIVATE_STAGE, actions[-1].kind)

    def test_absent_opt_ins_add_no_rows(self):
        frames = [snap(ut=1.0, situation="SPLASHED", apoapsis=80000.0)]
        names = [o.name for o in mlib.evaluate_b4_assertions(
            frames, mlib.b4_params_from_dict(B4_PARAMS), phases_reached=(mlib.B4_ORBIT,))]
        self.assertEqual(["reachedOrbit", "apoapsisFloor", "landedSituation",
                          "craftCanopyObserved"], names)


class RowTests(unittest.TestCase):
    def setUp(self):
        self.p = mlib.b4_params_from_dict(LAYTHE_PARAMS)

    def _rows(self, frames, state):
        return {o.name: o for o in mlib.evaluate_b4_assertions(
            frames, self.p, phases_reached=state.phases_reached, state=state)}

    def _accepted_state(self):
        st = _state()
        for i in range(3):
            st, _ = _step(st, _park(float(i)))
        return st

    def test_accepted_handoff_and_laythe_splashdown_meet_both_rows(self):
        rows = self._rows([_park(0.0), snap(ut=9.0, body="Laythe", situation="SPLASHED")],
                          self._accepted_state())
        self.assertTrue(rows["startedInOrbit"].met)
        self.assertEqual("Laythe", rows["startedInOrbit"].detail["body"])
        self.assertTrue(rows["landedOnBody"].met)

    def test_no_accepted_handoff_fails_closed(self):
        rows = self._rows([snap(ut=9.0, body="Laythe", situation="SPLASHED")], _state())
        self.assertFalse(rows["startedInOrbit"].met)
        rows = {o.name: o for o in mlib.evaluate_b4_assertions(
            [snap(ut=9.0, body="Laythe", situation="SPLASHED")], self.p)}
        self.assertFalse(rows["startedInOrbit"].met)

    def test_landing_on_another_body_or_not_landing_reds_landed_on_body(self):
        st = self._accepted_state()
        self.assertFalse(self._rows([snap(ut=9.0, body="Jool", situation="SPLASHED")],
                                    st)["landedOnBody"].met)
        self.assertFalse(self._rows([snap(ut=9.0, body="Laythe", situation="FLYING")],
                                    st)["landedOnBody"].met)
        self.assertFalse(self._rows([], st)["landedOnBody"].met)


class LaytheShellTests(unittest.TestCase):
    def _frames(self):
        return [
            _park(100.0), _park(101.0),
            _park(102.0),                                               # -> ORBIT
            _park(103.0),                                               # -> DEORBIT
            _park(115.0, ap_error=2.0),                                 # throttle up
            _park(125.0, periapsis=19000.0),                            # -> REENTRY
            _park(128.0, periapsis=19000.0, situation="SUB_ORBITAL",
                  altitude=68000.0, vertical_speed=-20.0),              # stage 1
            _park(130.0, periapsis=19000.0, situation="SUB_ORBITAL",
                  altitude=67000.0, vertical_speed=-25.0),              # stage 0
            _park(131.0, periapsis=19000.0, situation="SUB_ORBITAL",
                  altitude=66000.0, vertical_speed=-30.0),              # warp hop
            _park(300.0, periapsis=19000.0, situation="FLYING",
                  altitude=30000.0, vertical_speed=-600.0),             # poll
            _park(500.0, periapsis=19000.0, situation="FLYING",
                  altitude=7000.0, vertical_speed=-120.0),              # chute
            _park(560.0, situation="FLYING", altitude=600.0, vertical_speed=-7.0,
                  craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
            _park(640.0, situation="SPLASHED", altitude=0.0,
                  craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),         # terminal
        ]

    def test_a_laythe_descent_is_mission_ok_with_both_rows(self):
        control = FakeMissionControl(self._frames())
        code, result = run(b4_reentry.SPEC, LAYTHE_PARAMS, control)
        self.assertEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertEqual(0, code)
        rows = {a["name"]: a for a in result["assertions"]}
        for name in ("reachedOrbit", "apoapsisFloor", "landedSituation",
                     "craftCanopyObserved", "startedInOrbit", "landedOnBody"):
            self.assertTrue(rows[name]["met"], (name, rows[name]))
        kinds = [a.kind for a in control.actions]
        self.assertNotIn(mlib.ACTION_MJ_ENGAGE_ASCENT, kinds)
        self.assertNotIn(mlib.ACTION_MJ_EXECUTE_CIRCULARIZATION, kinds)
        # Exactly the two owed reentry stages, both after the cutoff release.
        stage_idx = [i for i, k in enumerate(kinds) if k == mlib.ACTION_ACTIVATE_STAGE]
        self.assertEqual(2, len(stage_idx))
        self.assertGreater(stage_idx[0], kinds.index(mlib.ACTION_AP_DISENGAGE))
        self.assertEqual([mlib.Action(mlib.ACTION_WARP_TO, 131.0 + 120.0)],
                         [a for a in control.actions if a.kind == mlib.ACTION_WARP_TO])

    def test_a_kerbin_splashdown_reds_landed_on_body(self):
        frames = self._frames()
        frames[-1] = snap(ut=640.0, body="Kerbin", situation="SPLASHED", altitude=0.0,
                          apoapsis=87900.0, craft_chute_state=mlib.CHUTE_STATE_DEPLOYED)
        control = FakeMissionControl(frames)
        _code, result = run(b4_reentry.SPEC, LAYTHE_PARAMS, control)
        self.assertEqual(mlib.MISSION_ASSERT_FAIL, result["verdict"], result)
        rows = {a["name"]: a for a in result["assertions"]}
        self.assertFalse(rows["landedOnBody"]["met"])
        self.assertTrue(rows["landedSituation"]["met"])


class SpecTests(unittest.TestCase):
    def test_lay1_params_validate_and_build_the_mode(self):
        import hlib
        spec = _spec()
        self.assertEqual("b4_reentry", spec["driver"]["mission"])
        params = spec["driver"]["missionParams"]
        self.assertEqual([], hlib._validate_mission_params(params, _schema("b4_reentry")))
        p = mlib.b4_params_from_dict(params)
        self.assertTrue(p.start_in_orbit)
        self.assertEqual("Laythe", p.start_body)
        self.assertEqual("Laythe", p.landing_body)
        # The fixture park reads periapsis ~56.2 km: the gate floor must sit below it
        # and above Laythe's 50 km atmosphere, and the apoapsisFloor row's floor below
        # the park's ~87.9 km apoapsis.
        self.assertTrue(50000 < p.start_min_periapsis < 56200)
        self.assertLess(p.target_apoapsis - p.apo_error, 87900)
        # The deorbit target is inside the atmosphere, and the rails hops stop above it.
        self.assertLess(p.deorbit_periapsis, 50000)
        self.assertGreater(p.warp_above_alt, 50000)

    def test_the_orbit_start_keys_are_optional_in_both_schemas_and_read(self):
        import inspect
        src = inspect.getsource(mlib.b4_params_from_dict)
        for name in ("b4_reentry", "b4_reentry_career"):
            declared = _schema(name)["params"]
            for key in ORBIT_START_KEYS:
                self.assertIn(key, declared, (name, key))
                self.assertFalse(declared[key]["required"], (name, key))
        for key in ORBIT_START_KEYS:
            self.assertIn('"%s"' % key, src, key)


if __name__ == "__main__":
    unittest.main()
