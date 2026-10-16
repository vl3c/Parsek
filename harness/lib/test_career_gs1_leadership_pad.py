"""Drift and shape gates for the `career-gs1-leadership-pad` fixture (QL-2c's host).

The fixture is DERIVED from `career-gs1-two-stage-pad` (QL-2b's host) by
`harness/tools/build_career_gs1_leadership_pad.py`: the same career and craft with stock's
Leadership Initiative already active in `StrategySystem`. A hand edit to either save or a
change to the derivation reds here instead of in a flight that would quietly fly with no
strategy (and so prove nothing about the strategy's share of the reward).
"""
import difflib
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_gs1_leadership_pad.py")
SAVES = os.path.join(HARNESS_ROOT, "fixtures", "saves")
FIXTURE = os.path.join(SAVES, "career-gs1-leadership-pad", "persistent.sfs")
SOURCE = os.path.join(SAVES, "career-gs1-two-stage-pad", "persistent.sfs")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("QL-2c-quickload-strategy-share-not-kept.toml",)


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_gs1_leadership_pad", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _scenario_body(text, name):
    return re.search(r"\n\t\tname = %s\n(.*?)\n\t}\n" % re.escape(name), text, re.S).group(1)


class CareerGs1LeadershipPadDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_adds_the_strategy_and_restamps_the_title_only(self):
        src, out = _read(SOURCE).split("\n"), _read(FIXTURE).split("\n")
        self.assertEqual(len(src) + 6, len(out), "the derivation adds exactly the six STRATEGY lines")
        added = [line[2:] for line in difflib.ndiff(src, out) if line.startswith("+ ")]
        removed = [line[2:] for line in difflib.ndiff(src, out) if line.startswith("- ")]
        self.assertEqual(["\tTitle = career-gs1-two-stage-pad (CAREER)"], removed)
        self.assertEqual(
            ["\tTitle = career-gs1-leadership-pad (CAREER)",
             "\t\t\tSTRATEGY", "\t\t\t{",
             "\t\t\t\tname = LeadershipInitiative", "\t\t\t\tdate = 0",
             "\t\t\t\tfactor = 0.0500000007", "\t\t\t}"],
            added)

    def test_leadership_initiative_is_the_one_active_strategy(self):
        body = _scenario_body(_read(FIXTURE), "StrategySystem")
        self.assertEqual(["LeadershipInitiative"],
                         re.findall(r"(?m)^\t\t\t\tname = (\S+)$", body))

    def test_the_source_has_no_active_strategy(self):
        # The premise QL-2b vs QL-2c rests on: the only difference is the strategy.
        body = _scenario_body(_read(SOURCE), "StrategySystem")
        self.assertNotIn("STRATEGY\n", body)

    def test_the_surface_eva_milestone_is_still_open(self):
        # The lane's reward after the F5 is Kerbin/SurfaceEVA, inherited from the host.
        body = _scenario_body(_read(FIXTURE), "ProgressTracking")
        self.assertNotIn("SurfaceEVA", body)

    def test_the_hosted_specs_stage_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                with open(os.path.join(SCENARIOS, name), encoding="utf-8") as fh:
                    self.assertRegex(
                        fh.read(),
                        r'(?m)^saveTemplate\s*=\s*"fixtures/saves/career-gs1-leadership-pad"')


if __name__ == "__main__":
    unittest.main()
