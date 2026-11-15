"""Fixture gates for the four Klaw / asteroid hosts (AST-1, AST-2, AST-3, AST-4, COM-1).

DERIVED by `harness/tools/build_ast_klaw_fixtures.py` from the committed `lab-lko-sandbox`
plus donor part texts (`bdock-recorded`, `miner-rover-full`) and the donor comet of
`depot-route-recorded`, so a change to any of them must re-run the builder. These cells pin
what the lanes read: the vessel set and the active vessel, the persistentIds the specs name
(`focusPid=`, the depot pid), one rock PART per fixture, every ship part tree well formed
(parents before children, attN / srfN indices in range), the Klaw forward-most on the probe
axis, the rock ahead of the probe on the same orbit, the fully-discovered rock DISCOVERY,
CommNet off and full SAS on, fresh identities, and the Jr port states (Ready, or the AST-4
PreAttached pair).

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)


def _load():
    path = os.path.join(_HARNESS, "tools", "build_ast_klaw_fixtures.py")
    spec = importlib.util.spec_from_file_location("build_ast_klaw_fixtures", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


P = "\t\t\t\t"
VK = "\t\t\t"


class AstKlawFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load()
        cls.saves = {}
        for name in cls.b.FIXTURES:
            lines = cls.b._lines(name)
            blocks = cls.b.vessel_blocks(lines)
            cls.saves[name] = (lines, {n: lines[s:e] for s, e, n in blocks},
                               [n for _s, _e, n in blocks])

    # -- helpers ------------------------------------------------------------

    def _vessel(self, fixture, vessel):
        return self.saves[fixture][1][vessel]

    def _parts(self, vessel):
        return [vessel[s:e] for s, e in self.b.part_ranges(vessel)]

    def _hv(self, part, key):
        return self.b._value(part[2:], P, key)

    def _mna(self, vessel):
        at = vessel.index(VK + "ORBIT")
        orbit = vessel[at:self.b._block_end(vessel, at)]
        return float(self.b._value(orbit, P, "MNA")), float(self.b._value(orbit, P, "SMA"))

    # -- cells --------------------------------------------------------------

    def test_committed_bytes_match_the_builder(self):
        for name in self.b.FIXTURES:
            self.assertEqual([], self.b.committed_problems(name),
                             "re-run python harness/tools/build_ast_klaw_fixtures.py")

    def test_vessel_sets_and_the_active_vessel(self):
        b = self.b
        expected = {
            b.AST_KLAW: [b.ROCK_NAME, b.KLAW_NAME, b.WATCHER_NAME],
            b.COM_KLAW: [b.COMET_NAME, b.KLAW_NAME, b.WATCHER_NAME],
            b.AST_MINER: [b.ROCK_NAME, b.MINER_NAME, b.DEPOT_NAME],
            b.AST_MOTHERSHIP: [b.ROCK_NAME, b.KLAW_NAME],
        }
        for name, vessels in expected.items():
            lines, _by, names = self.saves[name]
            self.assertEqual(vessels, names, name)
            self.assertIn("\t\tactiveVessel = 1", lines, name)
            self.assertIn("\tMode = SANDBOX", lines, name)
            self.assertIn("\tTitle = %s (SANDBOX)" % name, lines, name)

    def test_spec_named_persistent_ids(self):
        b = self.b
        self.assertEqual(2710403000, b.VESSEL_PIDS[b.WATCHER_NAME])
        self.assertIn(VK + "persistentId = 2710403000", self._vessel(b.AST_KLAW, b.WATCHER_NAME))
        self.assertIn(VK + "persistentId = 2710401000", self._vessel(b.AST_KLAW, b.KLAW_NAME))
        self.assertIn(VK + "persistentId = 2710402000", self._vessel(b.AST_MINER, b.MINER_NAME))
        self.assertIn(VK + "persistentId = 2710404000", self._vessel(b.AST_MINER, b.DEPOT_NAME))
        self.assertIn(VK + "persistentId = %d" % b.ROCK_PID, self._vessel(b.AST_KLAW, b.ROCK_NAME))
        self.assertIn(VK + "persistentId = %d" % b.COMET_PID,
                      self._vessel(b.COM_KLAW, b.COMET_NAME))
        # AST-4 decouples at the probe's Jr port; the spec names its part pid.
        probe = self._parts(self._vessel(b.AST_MOTHERSHIP, b.KLAW_NAME))
        self.assertEqual("dockingPort3", self._hv(probe[10], "name"))
        self.assertEqual("2710001010", self._hv(probe[10], "persistentId"))
        # AST-3 transfers into the depot's ore tank; the spec names its part pid.
        depot = self._parts(self._vessel(b.AST_MINER, b.DEPOT_NAME))
        self.assertEqual("RadialOreTank", self._hv(depot[3], "name"))
        self.assertEqual("2710004003", self._hv(depot[3], "persistentId"))

    def test_one_rock_part_per_fixture_and_rocks_are_space_objects(self):
        b = self.b
        for name in b.FIXTURES:
            lines = self.saves[name][0]
            rocks = [l for l in lines if l in (P + "name = PotatoRoid", P + "name = PotatoComet")]
            self.assertEqual(1, len(rocks), name)
        for fixture, rock, part in ((b.AST_KLAW, b.ROCK_NAME, "PotatoRoid"),
                                    (b.COM_KLAW, b.COMET_NAME, "PotatoComet")):
            v = self._vessel(fixture, rock)
            self.assertIn(VK + "type = SpaceObject", v)
            self.assertIn(VK + "sit = ORBITING", v)
            self.assertIn(P + "name = " + part, v)

    def test_rock_discovery_is_fully_discovered_and_permanent(self):
        b = self.b
        for fixture, rock, size in ((b.AST_KLAW, b.ROCK_NAME, "0"), (b.AST_MINER, b.ROCK_NAME, "0"),
                                    (b.AST_MOTHERSHIP, b.ROCK_NAME, "0"),
                                    (b.COM_KLAW, b.COMET_NAME, "5")):
            v = self._vessel(fixture, rock)
            d = v.index(VK + "DISCOVERY")
            disc = v[d:b._block_end(v, d)]
            self.assertEqual("29", b._value(disc, P, "state"), fixture)
            self.assertEqual("Infinity", b._value(disc, P, "lifetime"), fixture)
            self.assertEqual("Infinity", b._value(disc, P, "refTime"), fixture)
            self.assertEqual(size, b._value(disc, P, "size"), fixture)

    def test_every_vessel_shares_the_lab_orbit_but_for_mna(self):
        b = self.b
        base = b._labx(b._lines(b.BASE_NAME))
        base_mna, sma = self._mna(base)
        offsets = {
            b.AST_KLAW: {b.KLAW_NAME: 0.0, b.ROCK_NAME: b.ROCK_AHEAD_M,
                         b.WATCHER_NAME: -b.WATCHER_BEHIND_M},
            b.COM_KLAW: {b.KLAW_NAME: 0.0, b.COMET_NAME: b.COMET_AHEAD_M,
                         b.WATCHER_NAME: -b.WATCHER_BEHIND_M},
            b.AST_MINER: {b.MINER_NAME: 0.0, b.ROCK_NAME: b.ROCK_AHEAD_M,
                          b.DEPOT_NAME: -b.DEPOT_BEHIND_M},
            b.AST_MOTHERSHIP: {b.KLAW_NAME: 0.0, b.ROCK_NAME: b.MOTHERSHIP_ROCK_AHEAD_M},
        }
        for fixture, table in offsets.items():
            for vessel, metres in table.items():
                mna, vsma = self._mna(self._vessel(fixture, vessel))
                self.assertEqual(sma, vsma, (fixture, vessel))
                self.assertAlmostEqual((mna - base_mna) * sma, metres, places=3,
                                       msg=(fixture, vessel))
        # Rocks must not overlap the probe: the comet's nucleus radius is 44.47 m.
        self.assertGreater(b.COMET_AHEAD_M, 44.47 + 20.0)

    def test_part_trees_are_well_formed(self):
        b = self.b
        ships = [(f, v) for f in b.FIXTURES for v in self.saves[f][2]
                 if VK + "type = SpaceObject" not in self._vessel(f, v)]
        self.assertEqual(7, len(ships))
        for fixture, vessel in ships:
            parts = self._parts(self._vessel(fixture, vessel))
            n = len(parts)
            for i, part in enumerate(parts):
                parent = int(self._hv(part, "parent"))
                if i == 0:
                    self.assertEqual(0, parent, (fixture, vessel))
                else:
                    self.assertLess(parent, i, (fixture, vessel, i))
                srf = self._hv(part, "srfN").rsplit(",", 1)[1].strip()
                self.assertTrue(-1 <= int(srf) < n, (fixture, vessel, i, srf))
                if int(srf) >= 0:
                    self.assertEqual(int(srf), parent, (fixture, vessel, i))
                    self.assertEqual("1", self._hv(part, "attm"))
                for att in [l for l in part if l.startswith(P + "attN = ")]:
                    idx = int(att.rsplit(",", 1)[1])
                    self.assertTrue(-1 <= idx < n, (fixture, vessel, i, att))
                for sym in [l for l in part if l.startswith(P + "sym = ")]:
                    j = int(sym.split(" = ")[1])
                    self.assertTrue(0 <= j < n and j != i, (fixture, vessel, i, sym))
                    self.assertIn(P + "sym = %d" % i, parts[j], (fixture, vessel, i, j))
            # Every stack child (non-surface) is named on its parent's attN line.
            for i, part in enumerate(parts[1:], start=1):
                if self._hv(part, "attm") == "0":
                    parent = parts[int(self._hv(part, "parent"))]
                    self.assertTrue(any(l.startswith(P + "attN = ") and l.endswith(", %d" % i)
                                        for l in parent), (fixture, vessel, i))

    def test_the_klaw_is_the_forward_most_part_of_the_probe(self):
        b = self.b
        for fixture, vessel in ((b.AST_KLAW, b.KLAW_NAME), (b.COM_KLAW, b.KLAW_NAME),
                                (b.AST_MINER, b.MINER_NAME), (b.AST_MOTHERSHIP, b.KLAW_NAME)):
            parts = self._parts(self._vessel(fixture, vessel))
            klaws = [p for p in parts if self._hv(p, "name") == b.KLAW]
            self.assertEqual(1, len(klaws), fixture)
            klaw_y = float(self._hv(klaws[0], "position").split(",")[1])
            others = [float(self._hv(p, "position").split(",")[1]) for p in parts
                      if p is not klaws[0]]
            self.assertGreater(klaw_y, max(others) + 0.2, fixture)
            # Exactly one saved module, ModuleGrappleNode, disarmed: its OnLoad is what builds
            # the grapple AttachNode (a module-less Klaw throws every frame and never grabs).
            self.assertEqual(1, klaws[0].count(P + "MODULE"), fixture)
            self.assertIn(P + "\tname = ModuleGrappleNode", klaws[0], fixture)
            self.assertIn(P + "\tstate = Disabled", klaws[0], fixture)

    def test_the_comet_keeps_its_name(self):
        # hasName = False makes stock's CometVessel rename a discovered comet at every load.
        comet = self._vessel(self.b.COM_KLAW, self.b.COMET_NAME)
        self.assertIn(P + "\thasName = True", comet)
        self.assertNotIn(P + "\thasName = False", comet)

    def test_the_klaw_vessel_targets_its_rock(self):
        # kRPC's SpaceCenter.vessels omits SpaceObject vessels, so the mission reaches the
        # rock only through the target stock restores from this node at load.
        b = self.b
        for fixture, vessel, rock in ((b.AST_KLAW, b.KLAW_NAME, b.ROCK_NAME),
                                      (b.COM_KLAW, b.KLAW_NAME, b.COMET_NAME),
                                      (b.AST_MINER, b.MINER_NAME, b.ROCK_NAME),
                                      (b.AST_MOTHERSHIP, b.KLAW_NAME, b.ROCK_NAME)):
            v = self._vessel(fixture, vessel)
            at = v.index(VK + "TARGET")
            self.assertEqual(v[at + 2], P + "type = Vessel", fixture)
            guid = self.b._value(self._vessel(fixture, rock), VK, "pid")
            dashed = v[at + 3][len(P + "vesselID = "):]
            self.assertEqual(guid, dashed.replace("-", ""), fixture)
            self.assertEqual([8, 4, 4, 4, 12], [len(x) for x in dashed.split("-")], fixture)
            self.assertLess(v.index(VK + "FLIGHTPLAN"), at, fixture)
            self.assertEqual(v[at + 5], VK + "CTRLSTATE", fixture)
        for fixture in b.FIXTURES:
            targets = [n for n, v in self.saves[fixture][1].items() if VK + "TARGET" in v]
            self.assertEqual(1, len(targets), fixture)

    def test_jr_port_states(self):
        b = self.b

        def states(fixture, vessel):
            v = self._vessel(fixture, vessel)
            return [l.strip() for l in v if l.startswith(P + "\tstate = ")
                    and l.strip().split(" = ")[1] in ("Ready", "PreAttached", "Docked (docker)",
                                                      "Docked (dockee)", "Disengage", "Acquire")]
        self.assertEqual(["state = Ready"], states(b.AST_KLAW, b.KLAW_NAME))
        self.assertEqual(["state = Ready"], states(b.AST_MINER, b.DEPOT_NAME))
        self.assertEqual(["state = PreAttached", "state = PreAttached"],
                         states(b.AST_MOTHERSHIP, b.KLAW_NAME))

    def test_miner_carries_a_drill_an_empty_ore_tank_and_the_depot_an_empty_tank(self):
        b = self.b
        miner = self._vessel(b.AST_MINER, b.MINER_NAME)
        self.assertIn(P + "name = MiniDrill", miner)
        # Two mono pairs: one pair ran dry on the depot dock (third flight, 2026-11-15).
        self.assertEqual(4, miner.count(P + "name = radialRCSTank"))
        depot = self._vessel(b.AST_MINER, b.DEPOT_NAME)
        for v in (miner, depot):
            at = v.index(P + "\tname = Ore")
            self.assertEqual(P + "\tamount = 0", v[at + 1])

    def test_the_miner_drill_points_its_bit_at_the_held_rock(self):
        # MiniDrill's impact ray runs along the part's -y (MiniDrill.mu); the Klaw holds the
        # rock on +y. The drill hangs on the Z-200 with its +x attach normal toward the axis.
        b = self.b
        miner = self._vessel(b.AST_MINER, b.MINER_NAME)
        at = miner.index(P + "name = MiniDrill")
        part = miner[at:at + 30]
        self.assertIn(P + "parent = 1", part)
        self.assertIn(P + "srfN = srfAttach, 1", part)
        pos = [float(x) for x in next(l for l in part if l.startswith(P + "position = "))
               .split(" = ")[1].split(",")]
        q = [float(x) for x in next(l for l in part if l.startswith(P + "rotation = "))
             .split(" = ")[1].split(",")]

        def rot(v):
            x, y, z, w = q
            ux, uy, uz = v
            # v' = v + 2w (u x v) + 2 u x (u x v), u = (x, y, z)
            cx, cy, cz = y * uz - z * uy, z * ux - x * uz, x * uy - y * ux
            dx, dy, dz = y * cz - z * cy, z * cx - x * cz, x * cy - y * cx
            return (ux + 2 * (w * cx + dx), uy + 2 * (w * cy + dy), uz + 2 * (w * cz + dz))

        bit = rot((0.0, -1.0, 0.0))
        for got, want in zip(bit, (0.0, 1.0, 0.0)):
            self.assertAlmostEqual(want, got, places=5)
        normal = rot((1.0, 0.0, 0.0))
        inward = (-pos[0], 0.0, -pos[2])
        norm = (inward[0] ** 2 + inward[2] ** 2) ** 0.5
        for got, want in zip(normal, (inward[0] / norm, 0.0, inward[2] / norm)):
            self.assertAlmostEqual(want, got, places=5)
        self.assertAlmostEqual(b.OKTO_TO_Z200, pos[1], places=6)

    def test_save_edits_commnet_off_full_sas_crew_free(self):
        b = self.b
        for name in b.FIXTURES:
            lines = self.saves[name][0]
            self.assertIn("\t\t\tEnableCommNet = False", lines, name)
            self.assertIn("\t\t\tEnableFullSASInSandbox = True", lines, name)
            self.assertNotIn("\t\t\tstate = Assigned", lines, name)
            # The LABX survives only in ProgressTracking history, never as a vessel.
            self.assertNotIn(b.BASE_SHIP_NAME, self.saves[name][2], name)

    def test_identities_are_fresh_and_unique_across_fixtures_vessels(self):
        b = self.b
        for name in b.FIXTURES:
            lines = self.saves[name][0]
            pids = [l for l in lines if l.startswith(P + "persistentId = ")]
            self.assertEqual(len(pids), len(set(pids)), name)
            uids = [l for l in lines if l.startswith(P + "uid = ")]
            self.assertEqual(len(uids), len(set(uids)), name)

    def test_loadmeta_counts_the_non_space_object_vessels(self):
        b = self.b
        for name, count in ((b.AST_KLAW, 2), (b.COM_KLAW, 2), (b.AST_MINER, 2),
                            (b.AST_MOTHERSHIP, 1)):
            with open(os.path.join(b.SAVES, name, "persistent.loadmeta"), "rb") as fh:
                self.assertIn(b"vesselCount = %d\r\n" % count, fh.read(), name)

    def test_files_are_ascii_and_lf(self):
        b = self.b
        for name in b.FIXTURES:
            with open(os.path.join(b.SAVES, name, "persistent.sfs"), "rb") as fh:
                data = fh.read()
            data.decode("ascii")
            self.assertNotIn(b"\r\n", data, name)


if __name__ == "__main__":
    unittest.main()
