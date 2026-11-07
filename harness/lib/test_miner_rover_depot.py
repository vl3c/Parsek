"""Fixture gates for `miner-rover-depot` (the mining host that can dock an Ore depot).

DERIVED by `harness/tools/build_miner_rover_depot.py` from the committed `miner-rover-landed`
(the miner gains a `dockingPort3` on its front) and `lab-lko-sandbox` (the port's PART node),
so a change to either must re-run the builder. These cells pin what lane MINE-5b depends on:
the miner is index 0 and active, its port is free and stacked on the adapter's top node along
+y; index 2 is `Ore Depot`, the ported miner without its drills, lighter than the miner, its
port facing the miner's across `GAP_METERS`, with identities disjoint from every other vessel.

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
FIXTURE_SFS = os.path.join(SAVES, "miner-rover-depot", "persistent.sfs")
BASE_SFS = os.path.join(SAVES, "miner-rover-landed", "persistent.sfs")
LANE = os.path.join(_HARNESS, "scenarios", "MINE-5b-rover-dock-depot-harvest-route.toml")


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


def _parts(variants, block):
    out = []
    for s, e in variants.part_blocks(block):
        out.append(block[s:e])
    return out


def _pkey(part, key):
    prefix = "\t\t\t\t" + key + " = "
    return [l[len(prefix):] for l in part if l.startswith(prefix)]


def _mass(part):
    return float(_pkey(part, "mass")[0])


class MinerRoverDepotFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load("build_miner_rover_depot")
        cls.variants = cls.builder.variants
        cls.lines = _read(FIXTURE_SFS).split("\n")
        blocks = cls.builder.landed.vessel_blocks(cls.lines)
        cls.names = [b[2] for b in blocks]
        cls.vessels = [cls.lines[s:e] for s, e, _ in blocks]
        cls.miner, cls.probe, cls.depot = cls.vessels
        base = _read(BASE_SFS).split("\n")
        b = cls.builder.landed.vessel_blocks(base)
        cls.base_miner = base[b[0][0]:b[0][1]]
        cls.base_probe = base[b[1][0]:b[1][1]]

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_miner_rover_depot.py")

    def test_miner_probe_depot_and_the_miner_is_active(self):
        self.assertEqual(["Mining Rover RC", "Observer Probe", "Ore Depot"], self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        self.assertEqual(self.base_probe, self.probe)
        meta = _read(os.path.join(SAVES, "miner-rover-depot", "persistent.loadmeta"))
        self.assertIn("vesselCount = 3\n", meta)

    def test_the_miners_port_is_free_on_the_adapters_top_node(self):
        parts = _parts(self.variants, self.miner)
        self.assertEqual(28, len(parts))
        port, adapter = parts[27], parts[26]
        self.assertEqual(["dockingPort3"], _pkey(port, "name"))
        self.assertEqual(["26"], _pkey(port, "parent"))
        self.assertEqual(["top, -1", "bottom, 26"], _pkey(port, "attN"))
        self.assertIn("top, 27", _pkey(adapter, "attN"))
        self.assertIn("\t\t\t\t\tstate = Ready", port)
        self.assertFalse([l for l in port if "KOSNameTag" in l])
        ay = float(_pkey(adapter, "position")[0].split(",")[1])
        py = float(_pkey(port, "position")[0].split(",")[1])
        self.assertAlmostEqual(self.builder.ADAPTER_TOP_NODE, py - ay, places=9)
        # Every part but the port and the adapter's top node is the base's.
        base_parts = _parts(self.variants, self.base_miner)
        for i in range(26):
            self.assertEqual(base_parts[i], parts[i], "miner part %d changed" % i)

    def test_the_depot_has_no_drills_and_is_lighter_than_the_miner(self):
        depot_parts = _parts(self.variants, self.depot)
        miner_parts = _parts(self.variants, self.miner)
        self.assertEqual(24, len(depot_parts))
        self.assertFalse([p for p in depot_parts if _pkey(p, "name") == ["MiniDrill"]])
        self.assertEqual(2, len([p for p in depot_parts if _pkey(p, "name") == ["RadialOreTank"]]))
        self.assertEqual(1, len([p for p in depot_parts if _pkey(p, "name") == ["dockingPort3"]]))
        self.assertLess(sum(_mass(p) for p in depot_parts), sum(_mass(p) for p in miner_parts))
        self.assertEqual("Probe", _top(self.depot, "type"))
        self.assertEqual("Probe", _top(self.miner, "type"))
        self.assertEqual("LANDED", _top(self.depot, "sit"))
        self.assertEqual("True", _top(self.depot, "skipGroundPositioning"))

    def test_the_ports_face_each_other_across_the_gap(self):
        b = self.builder
        qm = b._parse_quat(_top(self.miner, "rot"))
        qd = b._parse_quat(_top(self.depot, "rot"))
        fm = b._qrot(qm, (0.0, 1.0, 0.0))
        fd = b._qrot(qd, (0.0, 1.0, 0.0))
        self.assertLess(sum(x * y for x, y in zip(fm, fd)), -0.9999)
        um = b._qrot(qm, (0.0, 0.0, 1.0))
        ud = b._qrot(qd, (0.0, 0.0, 1.0))
        self.assertGreater(sum(x * y for x, y in zip(um, ud)), 0.9999)

        def root(v):
            lat, lon, alt = (float(_top(v, k)) for k in ("lat", "lon", "alt"))
            n = b._surface_normal(lat, lon)
            return tuple(c * (b.KERBIN_RADIUS + alt) for c in n)
        rm, rd = root(self.miner), root(self.depot)
        adapter = _parts(self.variants, self.miner)[26]
        face = b.port_face_offset(float(_pkey(adapter, "position")[0].split(",")[1]))
        face_m = tuple(r + face * f for r, f in zip(rm, fm))
        face_d = tuple(r + face * f for r, f in zip(rd, fd))
        gap = math.sqrt(sum((x - y) ** 2 for x, y in zip(face_m, face_d)))
        self.assertAlmostEqual(b.GAP_METERS, gap, places=3)
        # Along the miner's forward, with no sideways offset.
        along = sum((y - x) * f for x, y, f in zip(face_m, face_d, fm))
        self.assertAlmostEqual(b.GAP_METERS, along, places=3)

    def test_the_depot_identities_are_disjoint_from_every_other_vessel(self):
        def ids(block, key):
            return {l.strip() for l in block if l.strip().startswith(key + " = ")}
        for other in (self.miner, self.probe):
            for key in ("persistentId", "uid", "cid"):
                self.assertFalse(ids(other, key) & ids(self.depot, key), key)
            self.assertNotEqual(_top(other, "pid"), _top(self.depot, "pid"))
        for key in ("persistentId", "uid"):
            self.assertFalse(ids(self.miner, key) & ids(self.probe, key), key)
        self.assertEqual({"launchID = 4"}, ids(self.depot, "launchID"))

    def test_the_lane_names_the_ids_the_builder_derives(self):
        ids = self.builder.depot_ids()
        lane = _read(LANE)
        self.assertIn("partPid = \"%d\"" % ids["depotOreTankPersistentId"], lane)
        self.assertIn(str(ids["depotPersistentId"]), lane)
        self.assertIn(str(ids["minerPersistentId"]), lane)


if __name__ == "__main__":
    unittest.main()
