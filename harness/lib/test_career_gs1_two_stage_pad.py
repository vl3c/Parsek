"""Drift and shape gates for the `career-gs1-two-stage-pad` fixture (QL-2b's host).

The fixture is BUILT by `harness/tools/build_career_gs1_two_stage_pad.py`:
`career-pad-craft`'s recipe (`build_career_pad_craft.build`) with `gs1-two-stage-pad` as
the donor, so `fresh-career`'s career carries GS-1's two-stage craft on the pad. A hand
edit to the fixture, a change to either input save or a change to the shared recipe reds
here instead of in a flight.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_gs1_two_stage_pad.py")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES, "career-gs1-two-stage-pad")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("QL-2b-quickload-booster-career-reward-not-paid.toml",)


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_gs1_two_stage_pad", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _scenario_body(text, name):
    return re.search(r"\n\t\tname = %s\n(.*?)\n\t}\n" % re.escape(name), text, re.S).group(1)


class CareerGs1TwoStagePadDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_build(self):
        self.assertEqual([], _load_builder().check())

    def test_the_vessel_is_the_sandbox_donors_byte_for_byte(self):
        builder = _load_builder()
        fixture = builder._recipe.read_lines(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        donor = builder._recipe.read_lines(
            os.path.join(SAVES, "gs1-two-stage-pad", "persistent.sfs"))
        ours = builder._vessel_named(fixture, "GS1 Auto-Chute Booster")
        theirs = builder._vessel_named(donor, "GS1 Auto-Chute Booster")
        self.assertIsNotNone(ours)
        self.assertEqual(donor[theirs[0]:theirs[1]], fixture[ours[0]:ours[1]])

    def test_the_career_is_fresh_careers_with_one_crewed_pad_vessel(self):
        text = _read(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        self.assertRegex(text, r"(?m)^\tMode = CAREER$")
        self.assertIn("\t\tfunds = 500000", _scenario_body(text, "Funding"))
        self.assertIn("\t\tsci = 100", _scenario_body(text, "ResearchAndDevelopment"))
        self.assertIn("\t\trep = 0", _scenario_body(text, "Reputation"))
        self.assertEqual(1, len(re.findall(r"(?m)^\t\tVESSEL$", text)))
        self.assertRegex(text, r"(?m)^\t\t\t\tcrew = Valentina Kerman$")
        self.assertRegex(text, r"(?m)^\t\t\tsit = PRELAUNCH$")
        # The inert ParsekScenario node (a FLIGHT-route load needs it) and no ledger.
        self.assertIn("\t\tgameStateEventCount = 2", _scenario_body(text, "ParsekScenario"))
        self.assertFalse(os.path.exists(os.path.join(FIXTURE_DIR, "Parsek")))

    def test_no_kerbin_progress_is_earned_so_the_first_eva_pays(self):
        # QL-2b's reward is KSPAchievements.SurfaceEVA on Kerbin: complete-once, so the
        # premise is a progress tree with nothing in it.
        body = _scenario_body(_read(os.path.join(FIXTURE_DIR, "persistent.sfs")),
                              "ProgressTracking")
        self.assertRegex(body, r"\t\tProgress\n\t\t\{\n\t\t\}$")
        self.assertNotIn("SurfaceEVA", body)

    def test_the_hosted_specs_stage_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                with open(os.path.join(SCENARIOS, name), encoding="utf-8") as fh:
                    self.assertRegex(
                        fh.read(),
                        r'(?m)^saveTemplate\s*=\s*"fixtures/saves/career-gs1-two-stage-pad"')


if __name__ == "__main__":
    unittest.main()
