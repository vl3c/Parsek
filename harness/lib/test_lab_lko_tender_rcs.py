"""Fixture gates for `lab-lko-tender-rcs` (the LAB-13b host).

DERIVED by `harness/tools/build_lab_lko_tender_rcs.py` from the committed
`lab-lko-seeded-transmit` plus two donor part texts (`bdock-recorded`'s RCSBlock.v2,
`miner-rover-full`'s radialRCSTank), so a change to any of the three must re-run the builder.
These cells pin what LAB-13b depends on: the base vessel unchanged in its first 26 parts (the
lab, its 12.5 science, the two PreAttached Jr ports joining the tender), six parts appended
after them on the tender's OKTO (four RCS blocks and two full monopropellant tanks, each a
radial symmetry group centred on the stack axis), fresh identities, the RCS action group off,
and both part names researched.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)


def _load():
    path = os.path.join(_HARNESS, "tools", "build_lab_lko_tender_rcs.py")
    spec = importlib.util.spec_from_file_location("build_lab_lko_tender_rcs", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


P = "\t\t\t\t"


class LabLkoTenderRcsTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load()
        cls.lines = cls.b._lines(cls.b.NAME)
        blocks = cls.b.vessel_blocks(cls.lines)
        cls.names = [n for _s, _e, n in blocks]
        cls.vessel = cls.lines[blocks[0][0]:blocks[0][1]]
        cls.parts = cls.b.part_ranges(cls.vessel)
        base = cls.b._lines(cls.b.BASE_NAME)
        bb = cls.b.vessel_blocks(base)
        cls.base_vessel = base[bb[0][0]:bb[0][1]]

    def _part(self, index):
        s, e = self.parts[index]
        return self.vessel[s:e]

    def _value(self, block, key):
        return self.b._header_value(block, key)

    def _vec(self, text):
        return [float(x) for x in text.split(",")]

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.b.committed_problems(),
                         "re-run python harness/tools/build_lab_lko_tender_rcs.py")

    def test_one_orbiting_lab_vessel_the_lab_active(self):
        self.assertEqual([self.b.VESSEL_NAME], self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        self.assertIn("\tMode = SCIENCE_SANDBOX", self.lines)
        self.assertIn("\t\t\tsit = ORBITING", self.vessel)
        self.assertIn("\t\t\tpersistentId = 2200110055", self.vessel)

    def test_the_base_vessel_is_kept_but_for_the_rcs_group_and_the_appended_parts(self):
        self.assertEqual(len(self.parts), self.b.BASE_PART_COUNT + len(self.b.SPLICES))
        at = self.parts[self.b.BASE_PART_COUNT][0]
        end = self.parts[-1][1]
        kept = self.vessel[:at] + self.vessel[end:]
        self.assertEqual(len(kept), len(self.base_vessel))
        diff = [(a.strip(), c.strip()) for a, c in zip(kept, self.base_vessel) if a != c]
        self.assertEqual(diff, [("RCS = False, 323.651533203074",
                                 "RCS = True, 323.651533203074")])

    def test_the_tender_is_still_joined_by_two_preattached_jr_ports(self):
        states = [l.strip() for l in self.vessel if l.strip() == "state = PreAttached"]
        self.assertEqual(len(states), 2)
        self.assertEqual(self.b.part_name(self.vessel, self.parts[6][0]), "probeCoreOcto.v2")
        self.assertEqual(self._value(self._part(6), "parent"), "5")
        self.assertEqual(self.b.part_name(self.vessel, self.parts[5][0]), "dockingPort3")

    def test_six_parts_on_the_okto_four_rcs_blocks_and_two_tanks(self):
        names = [self.b.part_name(self.vessel, s) for s, _e in self.parts[26:]]
        self.assertEqual(names, ["RCSBlock.v2"] * 4 + ["radialRCSTank"] * 2)
        for i in range(26, 32):
            part = self._part(i)
            self.assertEqual(self._value(part, "parent"), "6", i)
            self.assertEqual(self._value(part, "attm"), "1", i)
            self.assertEqual(self._value(part, "srfN"), "srfAttach, 6", i)
            self.assertEqual(self._value(part, "sepI"), "1", i)
            self.assertEqual(self._value(part, "mid"), self._value(self._part(6), "mid"), i)
            self.assertEqual(self._value(part, "launchID"),
                             self._value(self._part(6), "launchID"), i)

    def test_identities_are_unique_in_the_save(self):
        for key in ("cid", "uid", "persistentId"):
            values = [self._value(self._part(i), key) for i in range(len(self.parts))]
            self.assertEqual(len(values), len(set(values)), key)
        text = "\n".join(self.lines)
        for spec in self.b.SPLICES:
            for ident in (spec.cid, spec.uid, spec.pid):
                self.assertEqual(text.count(" = " + ident + "\n"), 1, ident)

    def test_each_group_is_a_symmetric_ring_at_the_oktos_height(self):
        okto_y = self._vec(self._value(self._part(6), "position"))[1]
        for group in ((26, 27, 28, 29), (30, 31)):
            xs = zs = 0.0
            radii = set()
            for i in group:
                part = self._part(i)
                x, y, z = self._vec(self._value(part, "position"))
                self.assertEqual(y, okto_y, i)
                xs += x
                zs += z
                radii.add(round((x * x + z * z) ** 0.5, 3))
                syms = sorted(int(l.split(" = ")[1]) for l in part
                              if l.startswith(P + "sym = "))
                self.assertEqual(syms, [j for j in group if j != i], i)
            self.assertAlmostEqual(xs, 0.0, places=6)
            self.assertAlmostEqual(zs, 0.0, places=6)
            self.assertEqual(len(radii), 1, group)

    def test_the_rcs_blocks_fire_and_the_tanks_are_full(self):
        for i in range(26, 30):
            part = self._part(i)
            self.assertIn(P + "\tname = ModuleRCSFX", part)
            self.assertIn(P + "\trcsEnabled = True", part)
        for i in (30, 31):
            part = self._part(i)
            r = part.index(P + "RESOURCE")
            self.assertEqual(part[r + 2:r + 5], [P + "\tname = MonoPropellant",
                                                 P + "\tamount = 20",
                                                 P + "\tmaxAmount = 20"])
            self.assertIn(P + "\tname = KOSNameTag", part)
        for i in range(26, 32):
            kos = [l for l in self._part(i) if l == P + "\tname = KOSNameTag"]
            self.assertEqual(len(kos), 1, i)

    def test_both_parts_are_researched(self):
        parts = self.b.researched_parts(self.lines, self.b.RESEARCH_NODE)
        self.assertIn("RCSBlock.v2", parts)
        self.assertIn("radialRCSTank", parts)

    def test_the_rcs_group_is_off(self):
        self.assertIn(self.b.RCS_GROUP_OFF, self.vessel)
        self.assertNotIn(self.b.RCS_GROUP_ON, self.vessel)


if __name__ == "__main__":
    unittest.main()
