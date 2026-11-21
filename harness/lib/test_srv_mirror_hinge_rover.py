"""Fixture gates for `srv-mirror-hinge-rover` (the SRV-3 host).

DERIVED by `harness/tools/build_srv_mirror_hinge_rover.py` from the committed
`miner-rover-watched` (through `build_srv_hinge_rover`'s helpers), so a change to that base
must re-run the builder. These cells pin what SRV-3 depends on: the rover (renamed `SRV3
Mirror Hinge Rover`) stays index 0 and active with the watcher at index 1; a micronode on its
front carries a symmetry pair of G-11 hinges (the LEFT half `mirrorRotation = True`, both
commanded to the same non-zero `targetAngle`) and a locked Alligator offset hinge, each with a
Z-200 on its MOVING (top) node; the spec names the pids the builder derives.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SPEC = os.path.join(_HARNESS, "scenarios", "SRV-3-mirrored-hinge-pair-ghost.toml")


def _load_builder():
    sys.path.insert(0, os.path.join(_HARNESS, "tools"))
    path = os.path.join(_HARNESS, "tools", "build_srv_mirror_hinge_rover.py")
    spec = importlib.util.spec_from_file_location("build_srv_mirror_hinge_rover", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _part_block(builder, sfs, index):
    lines = sfs.split("\n")
    blocks = builder.landed.vessel_blocks(lines)
    rover = lines[blocks[0][0]:blocks[0][1]]
    parts = builder.variants.part_blocks(rover)
    return "\n".join(rover[parts[index][0]:parts[index][1]])


class SrvMirrorHingeRoverFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.sfs = cls.builder.build_sfs()
        with open(SPEC, encoding="utf-8") as fh:
            cls.spec_text = fh.read()

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_srv_mirror_hinge_rover.py")

    def test_the_built_save_verifies(self):
        self.assertEqual([], self.builder.verify(self.sfs))

    def test_rover_then_watcher_and_the_rover_is_active(self):
        lines = self.sfs.split("\n")
        names = [b[2] for b in self.builder.landed.vessel_blocks(lines)]
        self.assertEqual([self.builder.ROVER_NAME, self.builder.WATCHER_NAME], names)
        self.assertIn("\t\tactiveVessel = 0", lines)
        self.assertIn("\tTitle = srv-mirror-hinge-rover (SANDBOX)", lines)

    def test_the_new_parts_do_not_reuse_the_sibling_fixtures_ids(self):
        # A sibling of srv-hinge-rover / srv-rotor-rover, seeded separately.
        hinge = self.builder.hinge
        hinge_lines = hinge.build_sfs().split("\n")
        lines = self.sfs.split("\n")
        new_pids = hinge._ids(lines, "persistentId") - hinge._ids(hinge_lines, "persistentId")
        self.assertEqual(self.builder.ADDED_PARTS, len(new_pids))
        ids = self.builder.rover_part_ids()
        self.assertEqual(hinge.rover_part_ids()["roverPersistentId"], ids["roverPersistentId"])
        self.assertEqual(3, len({ids["rightHingePersistentId"], ids["leftHingePersistentId"],
                                 ids["offsetHingePersistentId"]}))
        self.assertNotIn(hinge.rover_part_ids()["hingePersistentId"],
                         (ids["rightHingePersistentId"], ids["leftHingePersistentId"]))

    def test_only_the_left_half_is_mirrored(self):
        b = self.builder
        right = _part_block(b, self.sfs, b.RIGHT_HINGE_INDEX)
        left = _part_block(b, self.sfs, b.LEFT_HINGE_INDEX)
        self.assertIn(b.P5 + "mirrorRotation = False", right)
        self.assertIn(b.P5 + "mirrorRotation = True", left)
        self.assertIn(b.P5 + "targetAngle = " + b.PAIR_TARGET, right)
        self.assertIn(b.P5 + "targetAngle = " + b.PAIR_TARGET, left)
        self.assertNotEqual("0", b.PAIR_TARGET, "a zero target makes the mirror baseline moot")

    def test_verify_catches_the_mirror_flag_on_the_wrong_half(self):
        # The negative control for the mirror claim: swapping the two flags must red.
        b = self.builder
        right = _part_block(b, self.sfs, b.RIGHT_HINGE_INDEX)
        left = _part_block(b, self.sfs, b.LEFT_HINGE_INDEX)
        swapped = self.sfs.replace(right, right.replace("mirrorRotation = False",
                                                        "mirrorRotation = True"))
        swapped = swapped.replace(left, left.replace("mirrorRotation = True",
                                                     "mirrorRotation = False"))
        self.assertNotEqual(self.sfs, swapped)
        self.assertTrue(any("mirrorRotation" in p for p in b.verify(swapped)))

    def test_verify_catches_a_pair_at_rest(self):
        b = self.builder
        left = _part_block(b, self.sfs, b.LEFT_HINGE_INDEX)
        rest = self.sfs.replace(left, left.replace("targetAngle = " + b.PAIR_TARGET,
                                                   "targetAngle = 0"))
        self.assertNotEqual(self.sfs, rest)
        self.assertTrue(any("targetAngle" in p for p in b.verify(rest)))

    def test_verify_catches_asymmetric_soft_limits_on_the_pair(self):
        # Stock's joint limits ignore mirrorRotation, so an asymmetric range pinches one half.
        b = self.builder
        right = _part_block(b, self.sfs, b.RIGHT_HINGE_INDEX)
        pinched = self.sfs.replace(right, right.replace("softMinMaxAngles = (-90, 90)",
                                                        "softMinMaxAngles = (0, 90)"))
        self.assertNotEqual(self.sfs, pinched)
        self.assertTrue(any("softMinMaxAngles" in p for p in b.verify(pinched)))

    def test_verify_catches_a_battery_on_the_base_node(self):
        b = self.builder
        left = _part_block(b, self.sfs, b.LEFT_HINGE_INDEX)
        old = "attN = top, %d\n%sattN = bottom, %d" % (b.LEFT_BATTERY_INDEX, b.P4, b.NODE_INDEX)
        new = "attN = top, %d\n%sattN = bottom, %d" % (b.NODE_INDEX, b.P4, b.LEFT_BATTERY_INDEX)
        self.assertIn(old, left)
        swapped = self.sfs.replace(left, left.replace(old, new))
        self.assertTrue(any("moving" in p for p in b.verify(swapped)))

    def test_verify_catches_a_wrong_rotation(self):
        b = self.builder
        saved = dict(b.BASIS_MAPS)
        try:
            b.BASIS_MAPS[b.ROT_LEFT] = b.BASIS_MAPS[b.ROT_RIGHT]
            self.assertTrue(any("maps" in p for p in b.verify(self.sfs)))
        finally:
            b.BASIS_MAPS.clear()
            b.BASIS_MAPS.update(saved)

    def test_the_offset_hinge_is_locked_at_its_rest(self):
        b = self.builder
        alligator = _part_block(b, self.sfs, b.OFFSET_HINGE_INDEX)
        self.assertIn(b.P4 + "name = hinge.03", alligator)
        self.assertIn(b.P5 + "targetAngle = 90", alligator)
        self.assertIn(b.P5 + "servoIsLocked = True", alligator)

    def test_the_spec_names_the_fixture_the_rover_and_the_pids(self):
        b = self.builder
        ids = b.rover_part_ids()
        self.assertIn('saveTemplate = "fixtures/saves/srv-mirror-hinge-rover"', self.spec_text)
        self.assertIn("Servo follower plan for '%s'" % b.ROVER_NAME, self.spec_text)
        # The mirrored line names the LEFT half's pid with the fixture's target, negated.
        self.assertIn("Servo snapshot pose mirrored for 'hinge\\\\.01' pid=%d ordinal=0 "
                      "module=ModuleRoboticServoHinge: targetAngle=%s\\\\.0 mirrorRotation=True "
                      "inverted=False displayed=-%s\\\\.0"
                      % (ids["leftHingePersistentId"], b.PAIR_TARGET, b.PAIR_TARGET),
                      self.spec_text)
        # The unmirrored half must never log the line.
        self.assertIn("Servo snapshot pose mirrored for 'hinge\\\\.01' pid=%d "
                      % ids["rightHingePersistentId"], self.spec_text)
        self.assertIn("Servo pose offset for 'hinge\\\\.03' pid=%d midx=0"
                      % ids["offsetHingePersistentId"], self.spec_text)


if __name__ == "__main__":
    unittest.main()
