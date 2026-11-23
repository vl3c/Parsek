"""Fixture gates for `pbr-break-rover` (the PBR-1 host).

DERIVED by `harness/tools/build_pbr_break_rover.py` from the committed `miner-rover-watched`,
so a change to that base must re-run the builder. These cells pin what PBR-1 depends on: the
rover (renamed `PBR1 Break Rover`) stays index 0 and active with the watcher at index 1, one
RTG is swapped for a retracted SP-L 1x6 deployable solar panel (the part the lane breaks), and
the stock wheel the lane damages keeps its ModuleWheelDamage undamaged.

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
    path = os.path.join(_HARNESS, "tools", "build_pbr_break_rover.py")
    spec = importlib.util.spec_from_file_location("build_pbr_break_rover", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class PbrBreakRoverFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.sfs = cls.builder.build_sfs()

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_pbr_break_rover.py")

    def test_the_built_save_verifies(self):
        self.assertEqual([], self.builder.verify(self.sfs))

    def test_rover_then_watcher_and_the_rover_is_active(self):
        lines = self.sfs.split("\n")
        names = [b[2] for b in self.builder.landed.vessel_blocks(lines)]
        self.assertEqual([self.builder.ROVER_NAME, self.builder.WATCHER_NAME], names)
        self.assertIn("\t\tactiveVessel = 0", lines)
        self.assertIn("\tTitle = pbr-break-rover (SANDBOX)", lines)

    def test_the_wheel_loads_undamaged(self):
        # The lane's breakwheel press is a candidate only on an undamaged wheel.
        self.assertIn(self.builder.P5 + "isDamaged = False", self.sfs)

    def test_verify_catches_a_panel_that_loads_broken(self):
        # The negative control: a panel that loads BROKEN is never a break candidate, so the
        # lane would read a refusal; verify must red on it.
        b = self.builder
        broken = self.sfs.replace(b.P5 + "deployState = RETRACTED", b.P5 + "deployState = BROKEN", 1)
        self.assertNotEqual(self.sfs, broken)
        self.assertTrue(any("RETRACTED" in p for p in b.verify(broken)))

    def test_verify_catches_the_rtg_left_in_place(self):
        b = self.builder
        unswapped = self.sfs.replace(b.P4 + "name = " + b.PANEL, b.P4 + "name = " + b.RTG, 1)
        self.assertNotEqual(self.sfs, unswapped)
        self.assertNotEqual([], b.verify(unswapped))

    def test_the_spec_names_the_fixture_the_panel_and_the_wheel(self):
        spec = os.path.join(_HARNESS, "scenarios", "PBR-1-panel-wheel-break-ghost.toml")
        with open(spec, encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn('saveTemplate = "fixtures/saves/pbr-break-rover"', text)
        self.assertIn('part = "%s"' % self.builder.PANEL, text)
        self.assertIn('part = "%s"' % self.builder.WHEEL, text)
        self.assertIn(self.builder.ROVER_NAME, text)


if __name__ == "__main__":
    unittest.main()
