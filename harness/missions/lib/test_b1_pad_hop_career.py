"""Unit cells for mission `b1_pad_hop_career`: B1's pad hop on a CAREER save.

The shell's whole claim is "B1, plus one control opt-in". Each cell pins one half
of that claim, because either half drifting costs a flight to discover:

  1. THE OPT-IN. The career shell's control tolerates the un-upgraded Tracking
     Station's maneuver-node refusal (measured: QL-2 `2026-10-07_2231` / `_2232_a2`
     died vessel-lost at PRELAUNCH without it), and the SANDBOX shell still does
     not - the career variant must never re-globalise the flag.
  2. NOTHING ELSE DIFFERS. The control, the spec, the machine, the evaluator and
     the param schema are B1's; a pad craft cannot reach a terminal through them.

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

import mlib                        # noqa: E402
import b1_pad_hop                  # noqa: E402
import b1_pad_hop_career           # noqa: E402
from test_shells import B1_PARAMS, FakeMissionControl, run, snap  # noqa: E402


def _schema(name):
    with open(os.path.join(_MISSIONS, name + ".schema.toml"), "rb") as fh:
        return tomllib.load(fh)


class CareerOptInTests(unittest.TestCase):
    def test_the_career_shell_tolerates_unreadable_maneuver_nodes(self):
        # THE CELL THE FIRST READING RUN BOUGHT: `QL-2-quickload-career-recovery-
        # not-paid` flew `b1_pad_hop` on `career-pad-craft` and died in 1.2 s at
        # PRELAUNCH (`2026-10-07_2231`, retry `_2232_a2`): kRPC refuses the node
        # read on a level-0 Tracking Station and three raises read as vessel_lost.
        c = b1_pad_hop_career.make_control()
        self.assertTrue(c._tolerate_unreadable_nodes)

    def test_the_sandbox_shell_keeps_the_default_off(self):
        # B1 and every other sandbox lane fly `b1_pad_hop`; the career variant
        # exists precisely so their semantics do not move.
        self.assertFalse(b1_pad_hop.make_control()._tolerate_unreadable_nodes)

    def test_the_controls_differ_in_the_opt_in_and_the_client_name_only(self):
        career = dict(vars(b1_pad_hop_career.make_control()))
        sandbox = dict(vars(b1_pad_hop.make_control()))
        for key in ("_tolerate_unreadable_nodes", "_client_name"):
            career.pop(key)
            sandbox.pop(key)
        self.assertEqual(sorted(sandbox), sorted(career))
        # Every configuration value (the read_* / use_* flags, budgets, ports) is a
        # plain scalar; runtime objects (locks, empty caches) compare by identity
        # and are not configuration, so they are left out of the compare.
        plain = (bool, int, float, str, type(None))
        config = [k for k, v in sandbox.items() if isinstance(v, plain)]
        self.assertIn("_read_chute", config)
        self.assertIn("_use_mechjeb", config)
        for key in config:
            self.assertEqual(sandbox[key], career[key], key)
        self.assertTrue(b1_pad_hop_career.make_control()._read_chute,
                        "read_chute is load-bearing for B1's canopy row and DOWN terminal")


class SameFlightAsB1Tests(unittest.TestCase):
    def test_the_spec_is_b1s_apart_from_name_and_control(self):
        career = b1_pad_hop_career.SPEC
        sandbox = b1_pad_hop.SPEC
        self.assertEqual("b1_pad_hop_career", career.name)
        for field in dataclasses.fields(career):
            if field.name in ("name", "build_state", "decide", "evaluate", "make_control"):
                continue
            self.assertEqual(getattr(sandbox, field.name), getattr(career, field.name),
                             field.name)

    def test_the_param_schema_is_b1s_key_for_key(self):
        # The shell builds B1's machine from mlib.b1_params_from_dict, so the two
        # schemas must accept exactly the same specs. Copy edits across.
        self.assertEqual(_schema("b1_pad_hop")["params"],
                         _schema("b1_pad_hop_career")["params"])

    def test_the_committed_qL2_params_validate_against_the_career_schema(self):
        import hlib
        path = os.path.join(_HARNESS, "scenarios",
                            "QL-2-quickload-career-recovery-not-paid.toml")
        with open(path, "rb") as fh:
            spec = tomllib.load(fh)
        self.assertEqual("b1_pad_hop_career", spec["driver"]["mission"])
        self.assertEqual([], hlib._validate_mission_params(
            spec["driver"]["missionParams"], _schema("b1_pad_hop_career")))

    def _hop(self):
        return [
            snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=14000, situation="PRE_LAUNCH"),
            snap(ut=1.0, stage_solid_fuel=0.5, apoapsis=14000, situation="FLYING"),
            snap(ut=2.0, stage_solid_fuel=0.0, apoapsis=14000, situation="FLYING"),
            snap(ut=3.0, vertical_speed=5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=4.0, vertical_speed=-5.0, apoapsis=14000, situation="FLYING"),
            snap(ut=5.0, altitude=5000, apoapsis=14000, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_SEMI_DEPLOYED),
            snap(ut=6.0, altitude=2000, apoapsis=14000, situation="FLYING",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
            snap(ut=7.0, altitude=100, apoapsis=14000, situation="LANDED",
                 craft_chute_state=mlib.CHUTE_STATE_DEPLOYED),
        ]

    def test_a_hop_resolves_identically_on_both_shells(self):
        _, sandbox = run(b1_pad_hop.SPEC, B1_PARAMS, FakeMissionControl(self._hop()))
        _, career = run(b1_pad_hop_career.SPEC, B1_PARAMS, FakeMissionControl(self._hop()))
        self.assertEqual(mlib.MISSION_OK, career["verdict"], career)
        self.assertEqual("b1_pad_hop_career", career["mission"])
        self.assertEqual(sandbox["phasesReached"], career["phasesReached"])
        self.assertEqual(sandbox["assertions"], career["assertions"])

    def test_a_craft_that_never_leaves_the_pad_never_resolves_ok(self):
        # THE SAFETY ARGUMENT, driven. The tolerance turns frames that used to be
        # blind into complete, trusted pad frames (CL-1's hazard). Here a craft that
        # stays on the pad - fuel never exhausts, apoapsis never rises, the
        # situation stays LANDED - must not reach LANDED / DOWN: it flakes in
        # ASCENT on the phase budget.
        frames = [snap(ut=0.0, stage_solid_fuel=1.0, apoapsis=0.0, altitude=27.0,
                       situation="PRE_LAUNCH")]
        frames += [snap(ut=float(t), stage_solid_fuel=1.0, apoapsis=0.0, altitude=27.0,
                        situation="LANDED") for t in (1, 30, 60, 95)]
        code, result = run(b1_pad_hop_career.SPEC, B1_PARAMS, FakeMissionControl(frames))
        self.assertNotEqual(mlib.MISSION_OK, result["verdict"], result)
        self.assertNotEqual(0, code)
        self.assertNotIn(mlib.B1_LANDED, result["phasesReached"])
        self.assertNotIn(mlib.B1_DOWN, result["phasesReached"])


if __name__ == "__main__":
    unittest.main()
