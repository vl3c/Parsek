"""Fixture gates for `eva-construct-pad` (the EVA-11 host).

The fixture is DERIVED by `harness/tools/build_eva_construct_pad.py` from two committed
fixtures (`eva3-pad-3crew` as the base, `rover-route-recorded`'s `rover fuel 0` as the
cloned foreign rover), so a re-forge of either must re-run the builder. These cells turn
that "must" into a red, and pin the properties EVA-11 depends on: the Kerbal X is VESSEL
index 0 and active (a Space Center save of this world reloads FLIGHT on the pad), Bill
Kerman is an Engineer aboard it (only an Engineer opens stock's construction panel), the
rover is crewless, LANDED, carries the free Clamp-O-Tron top node and the six solar
panels the lane attaches to and detaches, shares no identity with its donor, and its
persistentId is the one EVA-11 names as its step anchor and in its log contract.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import re
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

SAVES = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_SFS = os.path.join(SAVES, "eva-construct-pad", "persistent.sfs")
DONOR_SFS = os.path.join(SAVES, "rover-route-recorded", "persistent.sfs")
SPEC = os.path.join(_HARNESS, "scenarios", "EVA-11-construction-foreign-claim.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_eva_construct_pad.py")
    spec = importlib.util.spec_from_file_location("build_eva_construct_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _lines(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    return text.split("\r\n") if "\r\n" in text else text.split("\n")


def _part_names(lines, start, end):
    names = []
    for i in range(start, end):
        if lines[i] == "\t\t\tPART":
            names.append(lines[i + 2][len("\t\t\t\tname = "):])
    return names


class EvaConstructPadFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = _lines(FIXTURE_SFS)
        cls.blocks = cls.builder.vessel_blocks(cls.lines)

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_eva_construct_pad.py")

    def test_pad_craft_is_index_zero_and_active(self):
        self.assertEqual(["Kerbal X", "Ast. VPA-167", "rover fuel 0"], [b[2] for b in self.blocks])
        self.assertIn("\t\tactiveVessel = 0", self.lines)

    def test_bill_is_an_engineer_aboard_the_pad_craft(self):
        start, end, _ = self.blocks[0]
        self.assertIn("\t\t\t\tcrew = Bill Kerman", self.lines[start:end])
        i = self.lines.index("\t\t\tname = Bill Kerman")
        window = self.lines[i:i + 20]
        self.assertIn("\t\t\ttrait = Engineer", window)
        self.assertIn("\t\t\tstate = Assigned", window)

    def test_rover_is_a_landed_crewless_construction_target(self):
        start, end, _ = self.blocks[2]
        block = self.lines[start:end]
        self.assertIn("\t\t\tsit = LANDED", block)
        self.assertIn("\t\t\tlat = " + self.builder.ROVER_LAT, block)
        self.assertIn("\t\t\tlon = " + self.builder.ROVER_LON, block)
        self.assertIn("\t\t\tskipGroundPositioning = False", block)
        self.assertFalse(any(l.strip().startswith("crew = ") for l in block))
        names = _part_names(self.lines, start, end)
        self.assertEqual(16, len(names))
        self.assertEqual(6, names.count("solarPanels5"))
        # The Clamp-O-Tron is the LAST part, so a stack match on it is the one stock's
        # CheckAttach keeps (its loop has no early exit), and its top node is free.
        self.assertEqual("dockingPort2", names[-1])
        last_part = max(i for i in range(start, end) if self.lines[i] == "\t\t\tPART")
        self.assertIn("\t\t\t\tattN = top, -1", self.lines[last_part:end])

    def test_rover_sits_clear_of_the_pad_exclusion(self):
        # 153 m from the pad: outside the KSC exclusion (50 m), so its chain-tip spawn
        # is not retired there, and far from the Kerbal X, so nothing but the lane's
        # kerbal is ever inside EVA construction range (7 m) of it.
        import math
        pad_lat, pad_lon = -0.097207702526701967, -74.557676359248333
        r = 600000.0
        dn = (float(self.builder.ROVER_LAT) - pad_lat) * math.pi / 180.0 * r
        de = (float(self.builder.ROVER_LON) - pad_lon) * math.pi / 180.0 * r
        self.assertTrue(140.0 < math.hypot(dn, de) < 170.0, math.hypot(dn, de))

    def test_clone_shares_no_identity_with_its_donor(self):
        donor = _lines(DONOR_SFS)
        donor_block = [b for b in self.builder.vessel_blocks(donor) if b[2] == "rover fuel 0"][0]
        donor_ids = {l.strip() for l in donor[donor_block[0]:donor_block[1]]
                     if l.strip().startswith(("pid = ", "persistentId = "))}
        start, end, _ = self.blocks[2]
        clone_ids = {l.strip() for l in self.lines[start:end]
                     if l.strip().startswith(("pid = ", "persistentId = "))}
        self.assertTrue(clone_ids)
        self.assertFalse(donor_ids & clone_ids)

    def test_spec_names_the_rover_by_its_derived_persistent_id(self):
        pid = str(self.builder.rover_persistent_id())
        start, end, _ = self.blocks[2]
        self.assertIn("\t\t\tpersistentId = " + pid, self.lines[start:end])
        with open(SPEC, "r", encoding="utf-8") as fh:
            spec = fh.read()
        self.assertIn('saveTemplate       = "fixtures/saves/eva-construct-pad"', spec)
        anchors = re.findall(r'anchor = "([0-9]+)"', spec)
        self.assertTrue(anchors)
        self.assertEqual({pid}, set(anchors))
        # Every literal rover pid in the log contract is this one.
        pids = set(re.findall(r"vesselPid=([0-9]{6,})", spec)) | set(re.findall(r"pid=([0-9]{6,})", spec))
        self.assertEqual({pid}, pids)


if __name__ == "__main__":
    unittest.main()
