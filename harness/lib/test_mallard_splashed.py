"""Fixture gates for `mallard-splashed` (BOAT-1's host: AIR-1's Mallard afloat, Parsek-stripped).

DERIVED by `harness/tools/build_mallard_splashed.py` from the committed
`air-circuit-recorded`, so a re-harvest of that save must re-run the builder. These cells
pin what BOAT-1 depends on: the Mallard is the active vessel, SPLASHED east of the Space
Center with both jets ignited and crewed; the ParsekScenario node is present with no
child and no Parsek sidecar directory ships, so the seam StartRecording opens a fresh tree.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES, "mallard-splashed")

if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import saveparse  # noqa: E402


def _load(name):
    path = os.path.join(_HARNESS, "tools", name + ".py")
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


class MallardSplashedFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = saveparse.parse_sfs(_read(os.path.join(FIXTURE_DIR, "persistent.sfs"))).root
        cls.game = cls.root.first("GAME")
        cls.flight = cls.game.first("FLIGHTSTATE")
        cls.vessels = cls.flight.nodes_named("VESSEL")

    def test_the_builder_reproduces_the_committed_bytes(self):
        self.assertEqual([], _load("build_mallard_splashed").committed_problems())

    def test_the_mallard_is_active_and_afloat_east_of_ksc(self):
        names = [v.value("name") for v in self.vessels]
        active = int(self.flight.value("activeVessel"))
        mallard = self.vessels[active]
        self.assertEqual("Mallard", mallard.value("name"), names)
        self.assertEqual("SPLASHED", mallard.value("sit"))
        lat, lon = float(mallard.value("lat")), float(mallard.value("lon"))
        # East of the pad (lon -74.56) over the sea, near the equator.
        self.assertTrue(-0.2 < lat < 0.2, lat)
        self.assertTrue(-74.2 < lon < -73.4, lon)
        self.assertEqual(1, names.count("Mallard"))

    def test_both_jets_are_lit_and_the_crew_is_aboard(self):
        mallard = self.vessels[int(self.flight.value("activeVessel"))]
        lit = []
        crew = []
        for part in mallard.nodes_named("PART"):
            crew.extend(part.values_named("crew"))
            for module in part.nodes_named("MODULE"):
                if module.value("name") in ("ModuleEngines", "ModuleEnginesFX") \
                        and module.value("EngineIgnited") == "True":
                    lit.append(part.value("name"))
        self.assertEqual(2, len(lit), lit)
        self.assertEqual(["Bill Kerman", "Bob Kerman"], sorted(crew))

    def test_the_parsek_node_is_present_and_empty(self):
        nodes = [s for s in self.game.nodes_named("SCENARIO")
                 if s.value("name") == "ParsekScenario"]
        self.assertEqual(1, len(nodes))
        self.assertEqual([], [n.name for n in nodes[0].nodes])
        self.assertEqual(["name", "scene", "gameStateEventCount"],
                         [k for k, _v in nodes[0].values])
        self.assertFalse(os.path.exists(os.path.join(FIXTURE_DIR, "Parsek")))
        self.assertFalse(os.path.exists(os.path.join(FIXTURE_DIR, "Ships")))


if __name__ == "__main__":
    unittest.main()
