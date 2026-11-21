"""Fixture gates for `srv-hinge-rover` (the SRV-1 host).

DERIVED by `harness/tools/build_srv_hinge_rover.py` from the committed `miner-rover-watched`,
so a change to that base must re-run the builder. These cells pin what SRV-1 depends on: the
rover (renamed `SRV1 Hinge Rover`) stays index 0 and active with the watcher at index 1, a
G-11 hinge on its adapter's top node carries a Z-200 on its MOVING (top) node, so exactly one
part rides the hinge on the replay, and the hinge loads free and powered.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)


def _load_builder():
    sys.path.insert(0, os.path.join(_HARNESS, "tools"))
    path = os.path.join(_HARNESS, "tools", "build_srv_hinge_rover.py")
    spec = importlib.util.spec_from_file_location("build_srv_hinge_rover", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SrvHingeRoverFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.sfs = cls.builder.build_sfs()

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_srv_hinge_rover.py")

    def test_the_built_save_verifies(self):
        self.assertEqual([], self.builder.verify(self.sfs))

    def test_rover_then_watcher_and_the_rover_is_active(self):
        lines = self.sfs.split("\n")
        names = [b[2] for b in self.builder.landed.vessel_blocks(lines)]
        self.assertEqual([self.builder.ROVER_NAME, self.builder.WATCHER_NAME], names)
        self.assertIn("\t\tactiveVessel = 0", lines)
        self.assertIn("\tTitle = srv-hinge-rover (SANDBOX)", lines)

    def test_verify_catches_the_battery_on_the_base_node(self):
        # The negative control: the hinge's nodes swapped (battery on the base side) must red,
        # or the verify cell above would pass on a save whose replay has no follower.
        b = self.builder
        swapped = self.sfs.replace(
            b.P4 + "attN = top, %d\n%sattN = bottom, %d" % (b.BATTERY_INDEX, b.P4, b.ADAPTER_INDEX),
            b.P4 + "attN = top, %d\n%sattN = bottom, %d" % (b.ADAPTER_INDEX, b.P4, b.BATTERY_INDEX))
        self.assertNotEqual(self.sfs, swapped)
        self.assertTrue(any("moving" in p for p in b.verify(swapped)))

    def test_verify_catches_a_locked_hinge(self):
        b = self.builder
        locked = self.sfs.replace(b.P5 + "servoIsLocked = False", b.P5 + "servoIsLocked = True")
        self.assertNotEqual(self.sfs, locked)
        self.assertTrue(any("servoIsLocked" in p for p in b.verify(locked)))

    def test_the_spec_names_the_fixture_the_rover_and_the_hinge(self):
        spec = os.path.join(_HARNESS, "scenarios", "SRV-1-hinge-followers-ghost.toml")
        with open(spec, encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn('saveTemplate = "fixtures/saves/srv-hinge-rover"', text)
        self.assertIn('part = "%s"' % self.builder.HINGE, text)
        self.assertIn("Servo follower plan for '%s'" % self.builder.ROVER_NAME, text)


if __name__ == "__main__":
    unittest.main()
