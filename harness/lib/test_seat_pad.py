"""Fixture gates for `seat-pad` (the SEAT-1 host).

The fixture is DERIVED by `harness/tools/build_seat_pad.py` from `fresh-sandbox` and the
committed `GS1 Auto-Chute Booster.craft` (the Mk1 pod tail), so a change to either must
re-run the builder. These cells turn that "must" into a red and pin what SEAT-1 depends
on: the craft is a Mk1 pod with an external command seat surface-attached to it, and the
seat's authored tail carries no MODULE block.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_seat_pad.py")
    spec = importlib.util.spec_from_file_location("build_seat_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SeatPadFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.builder.check())

    def test_the_craft_verifies(self):
        self.assertEqual([], self.builder.verify(self.builder.build_files()))

    def test_the_save_is_vessel_less_so_the_pad_is_free(self):
        files = self.builder.build_files()
        sfs = files["persistent.sfs"].decode("utf-8")
        self.assertNotIn("\n\t\tVESSEL\n", sfs)
        self.assertIn("\tTitle = seat-pad (SANDBOX)\n", sfs)

    def test_the_seat_is_the_runtime_dot_form_name(self):
        # seatExternalCmd carries no underscore, so its cfg name and runtime name agree;
        # the lane's logContracts pin `seatPart=seatExternalCmd`.
        craft = self.builder.build_craft()
        self.assertIn("\tpart = seatExternalCmd_", craft)


if __name__ == "__main__":
    unittest.main()
