"""Drift and shape gates for the `career-pad-craft-hard` fixture (HC-1's host).

The fixture is DERIVED from `career-pad-craft` by
`harness/tools/build_career_pad_craft_hard.py` (read its docstring for why): the L2
career pad host with KSP 1.12.5's Hard preset values in PARAMETERS. These cells make the
derivation mechanical rather than remembered: a hand edit to either save, or a change to
the derivation, reds here instead of in a flight that would quietly run at x1.
"""
import importlib.util
import os
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_pad_craft_hard.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft-hard", "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft", "persistent.sfs")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("HC-1-hard-career-ledger.toml",)

# KSP 1.12.5 GameParameters.SetDifficultyPresets, Preset.Hard (decompiled), restricted to
# the sections a career save carries and the keys the preset sets.
HARD_PRESET = {
    "FLIGHT": {"CanQuickLoad": "False", "CanRestart": "False", "CanLeaveToEditor": "False"},
    "DIFFICULTY": {"AllowStockVessels": "False", "AllowOtherLaunchSites": "True",
                   "MissingCrewsRespawn": "False", "BypassEntryPurchaseAfterResearch": "False",
                   "ResourceAbundance": "0.5", "ReentryHeatScale": "1", "EnableCommNet": "True",
                   "persistKerbalInventories": "False"},
    "CAREER": {"StartingFunds": "10000", "FundsGainMultiplier": "0.6",
               "RepGainMultiplier": "0.6", "ScienceGainMultiplier": "0.6",
               "FundsLossMultiplier": "2", "RepLossMultiplier": "2", "RepLossDeclined": "3"},
}


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_pad_craft_hard", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read()


class CareerPadCraftHardDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_exactly_the_edited_values(self):
        builder = _load_builder()
        src_lines, out_lines = _read(SOURCE).split("\n"), _read(FIXTURE).split("\n")
        self.assertEqual(len(src_lines), len(out_lines), "the derivation must not add or drop lines")
        changed = [(a.strip(), b.strip()) for a, b in zip(src_lines, out_lines) if a != b]
        self.assertEqual(len(builder.EDITS), len(changed))
        for (section, key, before, after) in builder.EDITS:
            self.assertIn(("%s = %s" % (key, before), "%s = %s" % (key, after)), changed)

    def test_the_fixture_carries_every_hard_preset_value(self):
        builder = _load_builder()
        text = _read(FIXTURE)
        self.assertEqual("Hard", builder.section_values(text, "")["preset"])
        for section, values in HARD_PRESET.items():
            got = builder.section_values(text, section)
            for key, value in values.items():
                with self.subTest(section=section, key=key):
                    self.assertEqual(value, got.get(key))

    def test_the_source_fixture_is_still_a_x1_career(self):
        # The premise the derivation rests on: L2's host runs at x1, so a delta between
        # L2 and HC-1 is attributable to the Hard values alone.
        builder = _load_builder()
        career = builder.section_values(_read(SOURCE), "CAREER")
        self.assertEqual("1", career["FundsGainMultiplier"])
        self.assertEqual("1", career["ScienceGainMultiplier"])
        self.assertEqual("True", builder.section_values(_read(SOURCE), "FLIGHT")["CanQuickLoad"])

    def test_the_hosted_specs_stage_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                self.assertRegex(
                    _read(os.path.join(SCENARIOS, name)),
                    r'(?m)^saveTemplate\s*=\s*"fixtures/saves/career-pad-craft-hard"')


if __name__ == "__main__":
    unittest.main()
