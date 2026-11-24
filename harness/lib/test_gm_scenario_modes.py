"""Fixture gates for `gm-scenario-modes` (the GM-1 host).

DERIVED by `harness/tools/build_gm_scenario_modes.py` from the committed `b1-pad-craft`, so
a change to that base must re-run the builder. These cells pin what GM-1 depends on: the
persistent save is a stock SCENARIO game that still carries the donor's four-value
ParsekScenario node (the lane pins `4 value(s)` on each inert load and `2 entries` on each
inert save), the training save is SCENARIO_NON_RESUMABLE with no Parsek node and no Parsek
settings, and both route to FLIGHT on a focusable vessel.

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
    path = os.path.join(_HARNESS, "tools", "build_gm_scenario_modes.py")
    spec = importlib.util.spec_from_file_location("build_gm_scenario_modes", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class GmScenarioModesFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        base = cls.builder.read_lines(
            os.path.join(cls.builder.SAVES, cls.builder.BASE, "persistent.sfs"))
        cls.built = cls.builder.build_all(base)

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_gm_scenario_modes.py")

    def test_the_built_pair_verifies(self):
        self.assertEqual([], self.builder.verify(self.built))

    def test_the_two_modes_are_the_two_inert_scenario_modes(self):
        self.assertIn("\tMode = SCENARIO", self.built["persistent.sfs"])
        self.assertIn("\tMode = SCENARIO_NON_RESUMABLE", self.built["training.sfs"])

    def test_verify_catches_a_training_save_that_keeps_the_parsek_node(self):
        # The negative control: the training save built from the untouched donor (its node
        # kept) must red, or the verify cell above would pass on a save whose module loads.
        b = self.builder
        bad = dict(self.built)
        bad["training.sfs"] = [l for l in b.build_scenario(
            b.read_lines(os.path.join(b.SAVES, b.BASE, "persistent.sfs")))]
        bad["training.sfs"] = [l.replace("Mode = SCENARIO", "Mode = SCENARIO_NON_RESUMABLE")
                               for l in bad["training.sfs"]]
        self.assertTrue(any("no ParsekScenario node" in p for p in b.verify(bad)))

    def test_verify_catches_an_active_game_mode(self):
        b = self.builder
        bad = dict(self.built)
        bad["persistent.sfs"] = [l.replace("\tMode = SCENARIO", "\tMode = SANDBOX")
                                 for l in self.built["persistent.sfs"]]
        self.assertTrue(any("Mode = SCENARIO" in p for p in b.verify(bad)))

    def test_the_spec_names_the_fixture_and_both_saves(self):
        spec = os.path.join(_HARNESS, "scenarios", "GM-1-scenario-saves-inert.toml")
        with open(spec, encoding="utf-8") as fh:
            text = fh.read()
        self.assertIn('saveTemplate = "fixtures/saves/gm-scenario-modes"', text)
        self.assertIn('name = "training"', text)
        self.assertIn('name = "persistent"', text)
        self.assertIn("4 value\\\\(s\\\\), 0 node\\\\(s\\\\)", text)


if __name__ == "__main__":
    unittest.main()
