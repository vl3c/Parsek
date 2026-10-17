"""Fixture gates for `miner-rover-watched` (the mining host with a neighbour).

DERIVED by `harness/tools/build_miner_rover_watched.py` from the committed
`miner-rover-landed` (the miner, verbatim) and `rover-route-recorded`'s `rover fuel 0`
(the neighbour), so a change to either must re-run the builder. These cells pin what the
lanes depend on: the miner is index 0, active and byte-identical to the base's; index 1 is
`Miner Watcher`, a crewless LANDED rover 120 m north with ground positioning left to KSP,
re-derived identities disjoint from the miner's, and every clock at the fixture UT.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import math
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_SFS = os.path.join(SAVES, "miner-rover-watched", "persistent.sfs")
BASE_SFS = os.path.join(SAVES, "miner-rover-landed", "persistent.sfs")


def _load(name):
    path = os.path.join(_HARNESS, "tools", name + ".py")
    sys.path.insert(0, os.path.join(_HARNESS, "tools"))
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def _top(block, key):
    prefix = "\t\t\t" + key + " = "
    return [l[len(prefix):] for l in block if l.startswith(prefix)][0]


class MinerRoverWatchedFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load("build_miner_rover_watched")
        cls.lines = _read(FIXTURE_SFS).split("\n")
        blocks = cls.builder.landed.vessel_blocks(cls.lines)
        cls.names = [b[2] for b in blocks]
        cls.miner = cls.lines[blocks[0][0]:blocks[0][1]]
        cls.watcher = cls.lines[blocks[1][0]:blocks[1][1]]
        base = _read(BASE_SFS).split("\n")
        b0 = cls.builder.landed.vessel_blocks(base)[0]
        cls.base_miner = base[b0[0]:b0[1]]

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_miner_rover_watched.py")

    def test_miner_then_watcher_and_the_miner_is_active(self):
        self.assertEqual(["Mining Rover RC", "Miner Watcher"], self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)

    def test_the_miner_is_the_base_fixtures_miner_byte_for_byte(self):
        self.assertEqual(self.base_miner, self.miner)

    def test_watcher_is_landed_crewless_and_ground_positioned_by_ksp(self):
        self.assertEqual("LANDED", _top(self.watcher, "sit"))
        self.assertEqual("False", _top(self.watcher, "skipGroundPositioning"))
        self.assertFalse([l for l in self.watcher if l.strip().startswith("crew = ")])
        self.assertEqual("30", _top(self.watcher, "lct"))
        self.assertEqual("30", _top(self.watcher, "lastUT"))
        self.assertIn("\t\t\t\tEPH = 30", self.watcher)

    def test_watcher_is_within_real_spawn_controls_250_m_of_the_miner(self):
        r = 600000.0
        lat1, lon1 = (float(_top(self.miner, "lat")), float(_top(self.miner, "lon")))
        lat2, lon2 = (float(_top(self.watcher, "lat")), float(_top(self.watcher, "lon")))
        dlat = math.radians(lat2 - lat1)
        dlon = math.radians(lon2 - lon1) * math.cos(math.radians(lat1))
        dist = r * math.hypot(dlat, dlon)
        self.assertGreater(dist, 100.0)
        self.assertLess(dist, 150.0)

    def test_watcher_identities_are_disjoint_from_the_miner(self):
        def ids(block, key):
            return {l.strip() for l in block if l.strip().startswith(key + " = ")}
        for key in ("persistentId", "uid"):
            self.assertFalse(ids(self.miner, key) & ids(self.watcher, key), key)
        self.assertNotEqual(_top(self.miner, "pid"), _top(self.watcher, "pid"))


if __name__ == "__main__":
    unittest.main()
