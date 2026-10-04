"""Drift and shape gates for the `career-science-pad-hard` fixture (HC-2's host).

The fixture is DERIVED from `career-science-pad` by
`harness/tools/build_career_science_pad_hard.py`, which imports the Hard EDITS from
`build_career_pad_craft_hard.py`: L3's science-bench host with KSP 1.12.5's Hard preset values
in PARAMETERS. A hand edit to either save, or a change to either derivation, reds here instead
of in a flight that would quietly run at x1.
"""
import importlib.util
import os
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_science_pad_hard.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-science-pad-hard", "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-science-pad", "persistent.sfs")
PAD_CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft", "persistent.sfs")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("HC-2-hard-career-earn-spend.toml",)


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_science_pad_hard", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read()


def _lf(path):
    return _read(path).replace("\r\n", "\n")


class CareerSciencePadHardDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_exactly_the_hard_edits(self):
        hard = _load_builder().load_hard_builder()
        src_lines, out_lines = _read(SOURCE).split("\n"), _read(FIXTURE).split("\n")
        self.assertEqual(len(src_lines), len(out_lines), "the derivation must not add or drop lines")
        changed = [(a.strip(), b.strip()) for a, b in zip(src_lines, out_lines) if a != b]
        self.assertEqual(len(hard.EDITS), len(changed))
        for (_section, key, before, after) in hard.EDITS:
            self.assertIn(("%s = %s" % (key, before), "%s = %s" % (key, after)), changed)

    def test_the_fixture_keeps_the_source_line_endings(self):
        self.assertEqual(_read(SOURCE).count("\r\n"), _read(FIXTURE).count("\r\n"))

    def test_the_fixture_carries_the_hard_multipliers_and_flags(self):
        hard = _load_builder().load_hard_builder()
        text = _lf(FIXTURE)
        self.assertEqual("Hard", hard.section_values(text, "")["preset"])
        career = hard.section_values(text, "CAREER")
        for key in ("FundsGainMultiplier", "RepGainMultiplier", "ScienceGainMultiplier"):
            self.assertEqual("0.6", career[key])
        for key in ("FundsLossMultiplier", "RepLossMultiplier"):
            self.assertEqual("2", career[key])
        self.assertEqual("False", hard.section_values(text, "FLIGHT")["CanQuickLoad"])

    def test_the_source_parameters_match_the_hc1_source(self):
        # The premise that lets the Hard EDITS table be shared: L3's host carries exactly L2's
        # PARAMETERS node, so both Hard hosts differ from x1 by the same eleven values.
        hard = _load_builder().load_hard_builder()
        a, b = _lf(SOURCE), _read(PAD_CRAFT)
        for section in ("", "FLIGHT", "CAREER", "DIFFICULTY", "AdvancedParams"):
            with self.subTest(section=section):
                self.assertEqual(hard.section_values(b, section), hard.section_values(a, section))

    def test_the_hosted_specs_stage_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                self.assertRegex(
                    _read(os.path.join(SCENARIOS, name)),
                    r'(?m)^saveTemplate\s*=\s*"fixtures/saves/career-science-pad-hard"')


if __name__ == "__main__":
    unittest.main()
