"""Fixture gates for `miner-rover-relay` (the Ore shuttle host of lane MINE-4).

DERIVED by `harness/tools/build_miner_rover_relay.py` from the committed `miner-rover-depot`
(itself derived from `miner-rover-landed`), so a change to either must re-run the builder.
These cells pin what lane MINE-4 depends on: the miner is index 0 and active, with a second
free port on its rear facing -y; index 2 is the full `Ore Depot` ahead, index 3 the empty
`Ore Silo` behind, its port facing the miner's rear port across `SILO_GAP_METERS`; the miner
outweighs the full depot so it stays the dominant vessel of every dock.

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
FIXTURE_SFS = os.path.join(SAVES, "miner-rover-relay", "persistent.sfs")
BASE_SFS = os.path.join(SAVES, "miner-rover-depot", "persistent.sfs")
LANE = os.path.join(_HARNESS, "scenarios", "MINE-4-rover-docked-origin-route-rewind.toml")

ORE_DENSITY = 0.01
LF_OX_DENSITY = 0.005


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
    return [block[s:e] for s, e in variants.part_blocks(block)]


def _pkey(part, key):
    prefix = "\t\t\t\t" + key + " = "
    return [l[len(prefix):] for l in part if l.startswith(prefix)]


def _amount(part, resource):
    for i, line in enumerate(part):
        if line == "\t\t\t\t\tname = " + resource:
            return float(part[i + 1].split(" = ", 1)[1])
    return 0.0


def _wet_mass(parts):
    total = 0.0
    for p in parts:
        total += float(_pkey(p, "mass")[0])
        total += _amount(p, "Ore") * ORE_DENSITY
        total += (_amount(p, "LiquidFuel") + _amount(p, "Oxidizer")) * LF_OX_DENSITY
        total += _amount(p, "MonoPropellant") * 0.004
    return total


class MinerRoverRelayFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load("build_miner_rover_relay")
        cls.depot_builder = cls.builder.depot
        cls.variants = cls.builder.variants
        cls.lines = _read(FIXTURE_SFS).split("\n")
        blocks = cls.builder.landed.vessel_blocks(cls.lines)
        cls.names = [b[2] for b in blocks]
        cls.miner, cls.probe, cls.depot, cls.silo = (cls.lines[s:e] for s, e, _ in blocks)
        base = _read(BASE_SFS).split("\n")
        b = cls.builder.landed.vessel_blocks(base)
        cls.base_miner, cls.base_probe, cls.base_depot = (base[s:e] for s, e, _ in b)

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_miner_rover_relay.py")

    def test_miner_probe_depot_silo_and_the_miner_is_active(self):
        self.assertEqual(["Mining Rover RC", "Observer Probe", "Ore Depot", "Ore Silo"],
                         self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        self.assertEqual(self.base_probe, self.probe)
        meta = _read(os.path.join(SAVES, "miner-rover-relay", "persistent.loadmeta"))
        self.assertIn("vesselCount = 4\n", meta)

    def test_the_rear_port_is_free_on_the_tanks_bottom_node_facing_back(self):
        parts = _parts(self.variants, self.miner)
        self.assertEqual(29, len(parts))
        port, tank = parts[28], parts[7]
        self.assertEqual(["dockingPort3"], _pkey(port, "name"))
        self.assertEqual(["7"], _pkey(port, "parent"))
        self.assertEqual(["top, -1", "bottom, 7"], _pkey(port, "attN"))
        self.assertIn("bottom, 28", _pkey(tank, "attN"))
        self.assertIn("\t\t\t\t\tstate = Ready", port)
        b = self.depot_builder
        face = b._qrot(b._parse_quat(_pkey(port, "rotation")[0]), (0.0, 1.0, 0.0))
        self.assertAlmostEqual(-1.0, face[1], places=9)
        ty = float(_pkey(tank, "position")[0].split(",")[1])
        py = float(_pkey(port, "position")[0].split(",")[1])
        self.assertAlmostEqual(self.builder.TANK_BOTTOM_NODE, ty - py, places=9)
        # Two Ready ports, the front one the depot builder's.
        self.assertEqual(2, len([p for p in parts if _pkey(p, "name") == ["dockingPort3"]]))
        base_parts = _parts(self.variants, self.base_miner)
        self.assertEqual(base_parts[27], parts[27])

    def test_cargo_and_the_miner_stays_dominant(self):
        miner = _parts(self.variants, self.miner)
        dep = _parts(self.variants, self.depot)
        silo = _parts(self.variants, self.silo)
        self.assertEqual(150.0, sum(_amount(p, "Ore") for p in dep))
        self.assertEqual(0.0, sum(_amount(p, "Ore") for p in silo))
        self.assertEqual(0.0, sum(_amount(p, "Ore") for p in miner))
        # The depot full, the miner with one 75-unit tank of its Ore: the miner is heavier
        # in both docks, so it stays the root side and the active vessel.
        self.assertGreater(_wet_mass(miner), _wet_mass(dep) + 0.25)
        self.assertGreater(_wet_mass(miner), _wet_mass(silo) + 0.25)
        for v in (self.miner, self.depot, self.silo):
            self.assertEqual("Probe", _top(v, "type"))
            self.assertEqual("LANDED", _top(v, "sit"))

    def test_the_silo_is_the_depot_renamed_and_re_identified(self):
        silo = _parts(self.variants, self.silo)
        base = _parts(self.variants, self.base_depot)
        self.assertEqual(len(base), len(silo))
        for a, b in zip(base, silo):
            self.assertEqual(_pkey(a, "name"), _pkey(b, "name"))
            self.assertEqual(_pkey(a, "position"), _pkey(b, "position"))
        self.assertEqual(_top(self.miner, "rot"), _top(self.silo, "rot"))
        self.assertEqual("True", _top(self.silo, "skipGroundPositioning"))

        def ids(block, key):
            return {l.strip() for l in block if l.strip().startswith(key + " = ")}
        for other in (self.miner, self.probe, self.depot):
            for key in ("persistentId", "uid", "cid"):
                self.assertFalse(ids(other, key) & ids(self.silo, key), key)
            self.assertNotEqual(_top(other, "pid"), _top(self.silo, "pid"))
        self.assertEqual({"launchID = 5"}, ids(self.silo, "launchID"))

    def test_the_silo_port_faces_the_rear_port_across_the_gap(self):
        b = self.depot_builder
        qm = b._parse_quat(_top(self.miner, "rot"))
        fm = b._qrot(qm, (0.0, 1.0, 0.0))

        def root(v):
            lat, lon, alt = (float(_top(v, k)) for k in ("lat", "lon", "alt"))
            n = b._surface_normal(lat, lon)
            return tuple(c * (b.KERBIN_RADIUS + alt) for c in n)
        rm, rs = root(self.miner), root(self.silo)
        parts = _parts(self.variants, self.miner)
        ty = float(_pkey(parts[7], "position")[0].split(",")[1])
        ay = float(_pkey(parts[26], "position")[0].split(",")[1])
        rear = self.builder.rear_face_offset(ty)
        front = b.port_face_offset(ay)
        face_m = tuple(r - rear * f for r, f in zip(rm, fm))
        face_s = tuple(r + front * f for r, f in zip(rs, fm))
        gap = math.sqrt(sum((x - y) ** 2 for x, y in zip(face_m, face_s)))
        self.assertAlmostEqual(self.builder.SILO_GAP_METERS, gap, places=3)
        along = sum((x - y) * f for x, y, f in zip(face_m, face_s, fm))
        self.assertAlmostEqual(self.builder.SILO_GAP_METERS, along, places=3)

    def test_the_lane_names_the_ids_the_builder_derives(self):
        ids = self.builder.relay_ids()
        lane = _read(LANE)
        for key in ("minerOreTankPersistentId", "siloOreTankPersistentId",
                    "depotPersistentId", "minerPersistentId", "siloPersistentId"):
            self.assertIn(str(ids[key]), lane, key)


if __name__ == "__main__":
    unittest.main()
