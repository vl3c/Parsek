"""Fixture gates for `seat-rover-pad` (the SEAT-2 / SEAT-3 host).

The fixture is DERIVED by `harness/tools/build_seat_rover_pad.py` from the committed
`eva-construct-pad` (itself derived), so a change to that fixture must re-run the builder.
These cells turn that "must" into a red and pin what SEAT-2 and SEAT-3 depend on: the
rover keeps the persistentId and launch Guid both specs name, carries exactly one stock
external command seat surface-attached to its SAS part with no MODULE node (an empty
seat), Bill Kerman is aboard the pad Kerbal X, and the Kerbal X is VESSEL index 0 and
active (a Space Center save of this world reloads FLIGHT on the pad).

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SCENARIOS = os.path.join(_HARNESS, "scenarios")
SPECS = ("SEAT-2-seat-occupant-survives-spawn.toml", "SEAT-3-foreign-seat-claim-rewind.toml")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_seat_rover_pad.py")
    spec = importlib.util.spec_from_file_location("build_seat_rover_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SeatRoverPadFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.sfs = cls.builder.build_sfs()
        cls.lines = cls.sfs.split("\n")
        cls.blocks = cls.builder.vessel_blocks(cls.lines)

    def _rover(self):
        start, end, name = self.blocks[2]
        self.assertEqual(self.builder.ROVER_NAME, name)
        return start, end

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_seat_rover_pad.py")

    def test_vessel_order_and_focus_are_the_base_ones(self):
        self.assertEqual(["Kerbal X", "Ast. VPA-167", "rover fuel 0"], [b[2] for b in self.blocks])
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        self.assertIn("\tTitle = seat-rover-pad (SANDBOX)", self.lines)

    def test_bill_is_aboard_the_pad_craft(self):
        start, end, _ = self.blocks[0]
        self.assertIn("\t\t\t\tcrew = Bill Kerman", self.lines[start:end])

    def test_the_rover_identity_is_the_one_the_specs_name(self):
        start, end = self._rover()
        rover = self.lines[start:end]
        self.assertIn("\t\t\tpersistentId = %d" % self.builder.ROVER_PERSISTENT_ID, rover)
        self.assertIn("\t\t\tpid = " + self.builder.ROVER_GUID, rover)
        self.assertIn("\t\t\tsit = LANDED", rover)
        self.assertFalse(any(l.strip().startswith("crew = ") for l in rover),
                         "the rover must stay crewless")
        for name in SPECS:
            with open(os.path.join(SCENARIOS, name), "r", encoding="utf-8") as fh:
                spec = fh.read()
            with self.subTest(spec=name):
                self.assertIn('saveTemplate       = "fixtures/saves/seat-rover-pad"', spec)
                self.assertIn(str(self.builder.ROVER_PERSISTENT_ID), spec)

    def test_one_empty_seat_surface_attached_to_the_sas(self):
        start, end = self._rover()
        parts = self.builder.part_blocks(self.lines, start, end)
        self.assertEqual(self.builder.ROVER_BASE_PART_COUNT + 1, len(parts))
        names = [self.lines[s + 2].strip() for s, _ in parts]
        self.assertEqual(1, names.count("name = seatExternalCmd"))
        self.assertEqual("name = seatExternalCmd", names[-1])
        self.assertEqual("name = advSasModule", names[self.builder.SEAT_PARENT_INDEX])
        s, e = parts[-1]
        seat = [l.strip() for l in self.lines[s:e]]
        self.assertIn("parent = %d" % self.builder.SEAT_PARENT_INDEX, seat)
        self.assertIn("attm = 1", seat)
        self.assertIn("srfN = srfAttach, %d" % self.builder.SEAT_PARENT_INDEX, seat)
        self.assertFalse(any(l.startswith("attN = ") for l in seat))
        # No MODULE node: KerbalSeat loads its prefab defaults, occupantID 0 (an empty seat).
        self.assertNotIn("MODULE", seat)
        self.assertNotIn("KerbalSeat", self.sfs)


if __name__ == "__main__":
    unittest.main()
