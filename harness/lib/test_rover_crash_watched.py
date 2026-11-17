"""Fixture gates for `rover-crash-watched` (RVC-1's host: two rovers and a watcher).

DERIVED by `harness/tools/build_rover_crash_watched.py` from the committed
`miner-rover-depot` (the ported miner and the depot, verbatim) and
`build_miner_rover_watched.build_watcher` (the watcher), so a change to either must re-run
the builder. These cells pin what RVC-1 depends on: the miner is index 0, active and
byte-identical to the depot fixture's (its port and its probe core 2300437286, the
control point the lane selects); the depot is index 2, byte-identical, 2-3 m in front of
the miner; index 1 is `Miner Watcher`, byte-identical to `miner-rover-watched`'s, about
120 m north, with every identity disjoint from both rovers.

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
FIXTURE_SFS = os.path.join(SAVES, "rover-crash-watched", "persistent.sfs")
DEPOT_SFS = os.path.join(SAVES, "miner-rover-depot", "persistent.sfs")
WATCHED_SFS = os.path.join(SAVES, "miner-rover-watched", "persistent.sfs")
MINER_CONTROL_PART_PID = "2300437286"


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


def _distance_m(a, b):
    r = 600000.0
    lat1, lon1 = float(_top(a, "lat")), float(_top(a, "lon"))
    lat2, lon2 = float(_top(b, "lat")), float(_top(b, "lon"))
    dlat = math.radians(lat2 - lat1)
    dlon = math.radians(lon2 - lon1) * math.cos(math.radians(lat1))
    return r * math.hypot(dlat, dlon)


class RoverCrashWatchedFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load("build_rover_crash_watched")
        blocks_of = cls.builder.landed.vessel_blocks
        cls.lines = _read(FIXTURE_SFS).split("\n")
        blocks = blocks_of(cls.lines)
        cls.names = [b[2] for b in blocks]
        cls.miner, cls.watcher, cls.depot = (cls.lines[s:e] for s, e, _ in blocks)
        base = _read(DEPOT_SFS).split("\n")
        bb = blocks_of(base)
        cls.base_miner = base[bb[0][0]:bb[0][1]]
        cls.base_depot = base[bb[2][0]:bb[2][1]]
        watched = _read(WATCHED_SFS).split("\n")
        wb = blocks_of(watched)
        cls.watched_watcher = watched[wb[1][0]:wb[1][1]]

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_rover_crash_watched.py")

    def test_miner_watcher_depot_and_the_miner_is_active(self):
        self.assertEqual(["Mining Rover RC", "Miner Watcher", "Ore Depot"], self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)

    def test_both_rovers_are_the_depot_fixtures_byte_for_byte(self):
        self.assertEqual(self.base_miner, self.miner)
        self.assertEqual(self.base_depot, self.depot)
        self.assertEqual("110406740", _top(self.miner, "persistentId"))
        self.assertEqual("3950359723", _top(self.depot, "persistentId"))
        self.assertIn("\t\t\t\tpersistentId = " + MINER_CONTROL_PART_PID, self.miner)

    def test_watcher_is_miner_rover_watcheds_byte_for_byte(self):
        self.assertEqual(self.watched_watcher, self.watcher)
        self.assertEqual("3179797026", _top(self.watcher, "persistentId"))
        self.assertEqual("LANDED", _top(self.watcher, "sit"))

    def test_depot_is_in_front_and_the_watcher_well_clear(self):
        self.assertLess(_distance_m(self.miner, self.depot), 4.0)
        self.assertGreater(_distance_m(self.miner, self.watcher), 100.0)
        self.assertLess(_distance_m(self.miner, self.watcher), 150.0)
        self.assertGreater(_distance_m(self.depot, self.watcher), 100.0)

    def test_watcher_identities_are_disjoint_from_both_rovers(self):
        def ids(block, key):
            return {l.strip() for l in block if l.strip().startswith(key + " = ")}
        for other in (self.miner, self.depot):
            for key in ("persistentId", "uid", "pid"):
                self.assertFalse(ids(other, key) & ids(self.watcher, key), key)

    def test_loadmeta_counts_three_vessels(self):
        meta = _read(os.path.join(SAVES, "rover-crash-watched", "persistent.loadmeta"))
        self.assertIn("vesselCount = 3", meta.splitlines())


if __name__ == "__main__":
    unittest.main()
