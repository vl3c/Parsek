"""Fixture gates for `k2-held-kerbal-pad` (the KB-5 host).

The fixture is DERIVED by `harness/tools/build_k2_held_kerbal_pad.py` from two committed
fixtures (`career-earned-pad` as the base, `rover-route-recorded`'s `rover fuel 0` as the
cloned second vessel), so a re-forge of either must re-run the builder. These cells turn
that "must" into a red, and pin the properties KB-5 depends on: the crewless rover sits
at VESSEL index 0 and is the active vessel (so the flight-ready crew swap finds no held
kerbal aboard it), the pad craft at index 1 still carries Jebediah Kerman, whose roster
row stays Assigned, and the clone shares no identity with its donor.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

SAVES = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_SFS = os.path.join(SAVES, "k2-held-kerbal-pad", "persistent.sfs")
DONOR_SFS = os.path.join(SAVES, "rover-route-recorded", "persistent.sfs")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_k2_held_kerbal_pad.py")
    spec = importlib.util.spec_from_file_location("build_k2_held_kerbal_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _lines(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    return text.split("\r\n") if "\r\n" in text else text.split("\n")


class K2HeldKerbalPadFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = _lines(FIXTURE_SFS)
        cls.blocks = cls.builder.vessel_blocks(cls.lines)

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_k2_held_kerbal_pad.py")

    def test_rover_is_index_zero_and_active(self):
        self.assertEqual(2, len(self.blocks))
        self.assertEqual("rover fuel 0", self.blocks[0][2])
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        start, end, _ = self.blocks[0]
        self.assertFalse(any(l.strip().startswith("crew = ") for l in self.lines[start:end]),
                         "the active rover must carry no crew")

    def test_pad_craft_keeps_the_held_kerbal_and_he_stays_assigned(self):
        start, end, _ = self.blocks[1]
        self.assertIn("\t\t\t\tcrew = Jebediah Kerman", self.lines[start:end])
        i = self.lines.index("\t\t\tname = Jebediah Kerman")
        window = self.lines[i:i + 20]
        self.assertIn("\t\t\tstate = Assigned", window)

    def test_clone_shares_no_identity_with_its_donor(self):
        donor = _lines(DONOR_SFS)
        donor_block = [b for b in self.builder.vessel_blocks(donor) if b[2] == "rover fuel 0"][0]
        donor_ids = {l.strip() for l in donor[donor_block[0]:donor_block[1]]
                     if l.strip().startswith(("pid = ", "persistentId = "))}
        start, end, _ = self.blocks[0]
        clone_ids = {l.strip() for l in self.lines[start:end]
                     if l.strip().startswith(("pid = ", "persistentId = "))}
        self.assertTrue(clone_ids)
        self.assertFalse(donor_ids & clone_ids)


if __name__ == "__main__":
    unittest.main()
