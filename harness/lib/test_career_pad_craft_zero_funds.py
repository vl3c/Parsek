"""Drift and shape gates for the `career-pad-craft-zero-funds` fixture (ZF-1's host).

The fixture is DERIVED from `career-pad-craft` by
`harness/tools/build_career_pad_craft_zero_funds.py`: L2's career pad host with the funds pool
and Starting Funds at zero (KSP-SETTINGS-AUDIT S4). A hand edit to either save, or a change to
the derivation, reds here instead of in a flight that would quietly start at 500000.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_career_pad_craft_zero_funds.py")
FIXTURE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft-zero-funds")
SOURCE_DIR = os.path.join(HARNESS_ROOT, "fixtures", "saves", "career-pad-craft")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("ZF-1-zero-funds-career.toml",)


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_career_pad_craft_zero_funds", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read()


class CareerPadCraftZeroFundsDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_exactly_three_lines(self):
        changed = []
        for name in ("persistent.sfs", "persistent.loadmeta"):
            src = _read(os.path.join(SOURCE_DIR, name)).split("\n")
            out = _read(os.path.join(FIXTURE_DIR, name)).split("\n")
            self.assertEqual(len(src), len(out), name)
            changed += [(a.strip(), b.strip()) for a, b in zip(src, out) if a != b]
        self.assertEqual(sorted([("funds = 500000", "funds = 0"),
                                 ("StartingFunds = 10000", "StartingFunds = 0"),
                                 ("funds = 500000", "funds = 0")]), sorted(changed))

    def test_the_career_starts_at_zero_with_no_parsek_footprint(self):
        text = _read(os.path.join(FIXTURE_DIR, "persistent.sfs"))
        funding = re.search(r"\n\t\tname = Funding\n(.*?)\n\t}\n", text, re.S).group(1)
        self.assertIn("\t\tfunds = 0", funding)
        self.assertRegex(text, r"(?m)^\tMode = CAREER$")
        # A first-ever Parsek load: no ledger exists, so the seed decision is the one S4 fixed.
        self.assertFalse(os.path.exists(os.path.join(FIXTURE_DIR, "Parsek")))
        self.assertEqual(["persistent.loadmeta", "persistent.sfs"], sorted(os.listdir(FIXTURE_DIR)))

    def test_the_hosted_specs_stage_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                self.assertRegex(
                    _read(os.path.join(SCENARIOS, name)),
                    r'(?m)^saveTemplate\s*=\s*"fixtures/saves/career-pad-craft-zero-funds"')


if __name__ == "__main__":
    unittest.main()
