"""Drift and shape gates for the `career-pad-craft-purchase` fixture (RW-23 / RW-24's host).

The fixture is DERIVED from `career-pad-craft` by
`harness/tools/build_career_pad_craft_purchase.py`: L2's career pad host with entry purchases on
and a reputation seed of 120, so Aggressive Negotiations can be activated and a part purchase
costs its entry cost. A hand edit to either save, or a change to the derivation, reds here
instead of in a flight whose part would quietly cost nothing.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_pad_craft_purchase.py")
FIXTURE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft-purchase")
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("RW-23-strategy-part-purchase-rebuy.toml",
                "RW-24-strategy-slot-needed-after-rewind.toml")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_pad_craft_purchase", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read()


class CareerPadCraftPurchaseDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_exactly_three_lines(self):
        changed = []
        for name in ("persistent.sfs", "persistent.loadmeta"):
            src = _read(os.path.join(SOURCE_DIR, name)).split("\n")
            out = _read(os.path.join(FIXTURE_DIR, name)).split("\n")
            self.assertEqual(len(src), len(out), name)
            changed += [(a.strip(), b.strip()) for a, b in zip(src, out) if a != b]
        self.assertEqual(sorted([
            ("BypassEntryPurchaseAfterResearch = True", "BypassEntryPurchaseAfterResearch = False"),
            ("rep = 0", "rep = 120"),
            ("reputationPercent = 0", "reputationPercent = 12"),
        ]), sorted(changed))

    def test_the_seed_clears_the_measured_activation_reputation(self):
        builder = _load_builder()
        self.assertGreaterEqual(builder.REPUTATION_SEED, builder.MEASURED_AN_ACTIVATION_REP)
        text = _read(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        rep = re.search(r"\n\t\tname = Reputation\n(.*?)\n\t}\n", text, re.S).group(1)
        self.assertIn("\t\trep = %d" % builder.REPUTATION_SEED, rep)

    def test_entry_purchases_are_on_and_the_lane_part_is_unbought(self):
        # The lane buys the RT-10 Hammer on basicRocketry: the node is not researched in the
        # save, so its part is neither unlocked nor purchased until the lane researches it.
        text = _read(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        self.assertRegex(text, r"(?m)^\t\t\tBypassEntryPurchaseAfterResearch = False$")
        self.assertRegex(text, r"(?m)^\tMode = CAREER$")
        self.assertNotRegex(text, r"(?m)^\t\t\tid = basicRocketry$")
        self.assertNotRegex(text, r"(?m)^\t\t\tpart = solidBooster\.v2$")
        self.assertEqual(["persistent.loadmeta", "persistent.sfs"], sorted(os.listdir(FIXTURE_DIR)))

    def test_the_hosted_specs_stage_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                self.assertRegex(
                    _read(os.path.join(SCENARIOS, name)),
                    r'(?m)^saveTemplate\s*=\s*"fixtures/saves/career-pad-craft-purchase"')


if __name__ == "__main__":
    unittest.main()
