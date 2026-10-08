"""Unit cells for mission `gs1_auto_chute_booster_career`: GS-1 on a CAREER save.

The shell's whole claim is "GS-1, plus one control opt-in". Each cell pins one
half of that claim, because either half drifting costs a flight to discover:

  1. THE OPT-IN. The career shell's control tolerates the level-0 Tracking
     Station's maneuver-node refusal (QL-2 measured the death without it on
     `b1_pad_hop`, `2026-10-07_2231` / `_2232_a2`), and the SANDBOX shell still does
     not - GS-1, QL-4 / QL-4b / QL-4c, RF-1, RF-4 and CA-1 keep the default.
  2. NOTHING ELSE DIFFERS. The control, the spec, the machine, the evaluator and
     the param schema are GS-1's; a pad craft cannot reach a terminal through them.

NO krpc, NO KSP, NO network. Import path matches the sibling suites.
"""

import dataclasses
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

import mlib                              # noqa: E402
import gs1_auto_chute_booster            # noqa: E402
import gs1_auto_chute_booster_career     # noqa: E402
from test_shells import FakeMissionControl, run, snap  # noqa: E402
from test_gs1_auto_chute_booster import GS1_PARAMS, LIT, _nominal_frames  # noqa: E402

QL2B_SPEC_PATH = os.path.join(_HARNESS, "scenarios",
                              "QL-2b-quickload-booster-career-reward-not-paid.toml")


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


class CareerOptInTests(unittest.TestCase):
    def test_the_career_shell_tolerates_unreadable_maneuver_nodes(self):
        c = gs1_auto_chute_booster_career.make_control()
        self.assertTrue(c._tolerate_unreadable_nodes,
                        "without it a level-0 career flight dies vessel-lost in "
                        "PRELAUNCH (QL-2, 2026-10-07_2231 / _2232_a2)")

    def test_the_sandbox_shell_keeps_the_default_off(self):
        self.assertFalse(gs1_auto_chute_booster.make_control()._tolerate_unreadable_nodes)

    def test_the_controls_differ_in_the_opt_in_and_the_client_name_only(self):
        career = dict(vars(gs1_auto_chute_booster_career.make_control()))
        sandbox = dict(vars(gs1_auto_chute_booster.make_control()))
        for key in ("_tolerate_unreadable_nodes", "_client_name"):
            career.pop(key)
            sandbox.pop(key)
        self.assertEqual(sorted(sandbox), sorted(career))
        # Configuration values are plain scalars; runtime objects (locks, empty
        # caches) compare by identity and are left out.
        plain = (bool, int, float, str, type(None))
        config = [k for k, v in sandbox.items() if isinstance(v, plain)]
        for key in ("_read_chute", "_read_vessel_name", "_use_mechjeb"):
            self.assertIn(key, config)
        for key in config:
            self.assertEqual(sandbox[key], career[key], key)


class SameFlightAsGs1Tests(unittest.TestCase):
    def test_the_spec_is_gs1s_apart_from_name_and_control(self):
        career = gs1_auto_chute_booster_career.SPEC
        sandbox = gs1_auto_chute_booster.SPEC
        self.assertEqual("gs1_auto_chute_booster_career", career.name)
        for field in dataclasses.fields(career):
            if field.name in ("name", "build_state", "decide", "evaluate", "make_control"):
                continue
            self.assertEqual(getattr(sandbox, field.name), getattr(career, field.name),
                             field.name)

    def test_the_param_schema_is_gs1s_key_for_key(self):
        self.assertEqual(_schema("gs1_auto_chute_booster")["params"],
                         _schema("gs1_auto_chute_booster_career")["params"])

    def test_the_committed_ql2b_params_validate_against_the_career_schema(self):
        import hlib
        with open(QL2B_SPEC_PATH, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual("gs1_auto_chute_booster_career", spec["driver"]["mission"])
        self.assertEqual([], hlib._validate_mission_params(
            spec["driver"]["missionParams"], _schema("gs1_auto_chute_booster_career")))

    def test_a_nominal_flight_resolves_identically_on_both_shells(self):
        _, sandbox = run(gs1_auto_chute_booster.SPEC, GS1_PARAMS,
                         FakeMissionControl(_nominal_frames()))
        _, career = run(gs1_auto_chute_booster_career.SPEC, GS1_PARAMS,
                        FakeMissionControl(_nominal_frames()))
        self.assertEqual(mlib.MISSION_OK, career["verdict"], career)
        self.assertEqual("gs1_auto_chute_booster_career", career["mission"])
        self.assertEqual(sandbox["phasesReached"], career["phasesReached"])
        self.assertEqual(sandbox["assertions"], career["assertions"])

    def test_a_craft_that_never_leaves_the_pad_never_resolves_ok(self):
        # THE SAFETY ARGUMENT, driven. The tolerance turns frames that used to be
        # blind into complete, trusted pad frames (CL-1's hazard). A craft that
        # stays on the pad - KSP reading LANDED, apoapsis never reaching the staging
        # target, no booster in the vessel list - must not reach any terminal: it
        # flakes in ASCENT on the phase budget, with QL-2b's own params.
        with open(QL2B_SPEC_PATH, "rb") as fh:
            params = tomllib.load(fh)["driver"]["missionParams"]
        frames = [snap(ut=0.0, altitude=70.0, apoapsis=70.0, situation="PRE_LAUNCH",
                       available_thrust=LIT)]
        frames += [snap(ut=float(t), altitude=70.0, apoapsis=70.0, vertical_speed=0.0,
                        situation="LANDED", available_thrust=LIT, sibling_present=0)
                   for t in (1, 30, 60, 95)]
        code, result = run(gs1_auto_chute_booster_career.SPEC, params,
                           FakeMissionControl(frames))
        self.assertNotEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertNotEqual(0, code)
        for phase in (mlib.GS1_STAGE, mlib.GS1_LANDED, mlib.GS1_SIBLING_DOWN,
                      mlib.GS1_IMPACTED):
            self.assertNotIn(phase, result["phasesReached"])


if __name__ == "__main__":
    unittest.main()
