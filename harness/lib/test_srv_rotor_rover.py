"""Fixture gates for `srv-rotor-rover` (the SRV-2 host).

DERIVED by `harness/tools/build_srv_rotor_rover.py` from the committed `miner-rover-watched`
(through `build_srv_hinge_rover`'s helpers), so a change to that base must re-run the builder.
These cells pin what SRV-2 depends on: the rover (renamed `SRV2 Rotor Rover`) stays index 0
and active with the watcher at index 1, an EM-16 rotor on its adapter's top node carries a
Z-200 on its MOVING (top) node, so exactly one part rides the rotor on the replay, and the
rotor loads free and motorized with its motor OFF (SRV-2 engages it inside the recording).

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
    path = os.path.join(_HARNESS, "tools", "build_srv_rotor_rover.py")
    spec = importlib.util.spec_from_file_location("build_srv_rotor_rover", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SrvRotorRoverFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.sfs = cls.builder.build_sfs()

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_srv_rotor_rover.py")

    def test_the_built_save_verifies(self):
        self.assertEqual([], self.builder.verify(self.sfs))

    def test_rover_then_watcher_and_the_rover_is_active(self):
        lines = self.sfs.split("\n")
        names = [b[2] for b in self.builder.landed.vessel_blocks(lines)]
        self.assertEqual([self.builder.ROVER_NAME, self.builder.WATCHER_NAME], names)
        self.assertIn("\t\tactiveVessel = 0", lines)
        self.assertIn("\tTitle = srv-rotor-rover (SANDBOX)", lines)

    def test_the_new_parts_do_not_reuse_the_hinge_fixtures_ids(self):
        # A sibling of srv-hinge-rover, seeded separately: no new pid or uid is SRV-1's.
        hinge = self.builder.hinge
        hinge_ids = hinge.rover_part_ids()
        ids = self.builder.rover_part_ids()
        self.assertEqual(hinge_ids["roverPersistentId"], ids["roverPersistentId"])
        self.assertNotEqual(hinge_ids["hingePersistentId"], ids["rotorPersistentId"])
        hinge_lines = hinge.build_sfs().split("\n")
        lines = self.sfs.split("\n")
        new_pids = hinge._ids(lines, "persistentId") - hinge._ids(hinge_lines, "persistentId")
        self.assertEqual(2, len(new_pids))
        self.assertFalse(new_pids & hinge._ids(hinge_lines, "persistentId"))

    def test_verify_catches_the_battery_on_the_base_node(self):
        # The negative control: the rotor's nodes swapped (battery on the base side) must red,
        # or the verify cell above would pass on a save whose replay has no follower.
        b = self.builder
        swapped = self.sfs.replace(
            b.P4 + "attN = top, %d\n%sattN = bottom, %d" % (b.BATTERY_INDEX, b.P4, b.ADAPTER_INDEX),
            b.P4 + "attN = top, %d\n%sattN = bottom, %d" % (b.ADAPTER_INDEX, b.P4, b.BATTERY_INDEX))
        self.assertNotEqual(self.sfs, swapped)
        self.assertTrue(any("moving" in p for p in b.verify(swapped)))

    def test_verify_catches_a_motor_engaged_at_load(self):
        # An engaged motor spins the rotor before StartRecording, so the recorded spin-up the
        # lane pins would never happen inside the recording.
        b = self.builder
        engaged = self.sfs.replace(b.P5 + "servoMotorIsEngaged = False",
                                   b.P5 + "servoMotorIsEngaged = True")
        self.assertNotEqual(self.sfs, engaged)
        self.assertTrue(any("servoMotorIsEngaged" in p for p in b.verify(engaged)))

    def test_verify_catches_a_locked_rotor(self):
        b = self.builder
        locked = self.sfs.replace(b.P5 + "servoIsLocked = False", b.P5 + "servoIsLocked = True")
        self.assertNotEqual(self.sfs, locked)
        self.assertTrue(any("servoIsLocked" in p for p in b.verify(locked)))

    def test_the_spec_names_the_fixture_the_rover_and_the_rotor(self):
        spec = os.path.join(_HARNESS, "scenarios", "SRV-2-rotor-spin-ghost.toml")
        with open(spec, encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn('saveTemplate = "fixtures/saves/srv-rotor-rover"', text)
        self.assertIn('part = "%s"' % self.builder.ROTOR, text)
        self.assertIn("Servo follower plan for '%s'" % self.builder.ROVER_NAME, text)
        # The rotoron press reads back the fixture's own rpmLimit.
        self.assertIn("rpmLimit=%s\\\\.00" % self.builder.ROTOR_RPM, text)


if __name__ == "__main__":
    unittest.main()
