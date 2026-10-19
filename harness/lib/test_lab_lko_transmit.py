"""Fixture gates for `lab-lko-seeded-transmit` (the LAB-10 / LAB-10b / LAB-13 host).

DERIVED by `harness/tools/build_lab_lko_transmit.py` from the committed `lab-lko-science`,
so a change to the base must re-run the builder. These cells pin what the lanes depend on:
the vessel ORBITING with the probe tender still joined (two PreAttached Clamp-O-Tron Jr
ports, the Re-Fly separation), the lab holding 12.5 science (a transmit pays at once) with
no data and an empty EXPERIMENT_DATA (every subject still storable), research off, the crew
where the craft seats them, and every other line of the vessel the base's.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
SAVES = os.path.join(_HARNESS, "fixtures", "saves")


def _load():
    path = os.path.join(_HARNESS, "tools", "build_lab_lko_transmit.py")
    spec = importlib.util.spec_from_file_location("build_lab_lko_transmit", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _lines(name):
    with open(os.path.join(SAVES, name, "persistent.sfs"), "r", encoding="utf-8",
              newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


class LabLkoSeededTransmitTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.b = _load()
        cls.lines = _lines(cls.b.NAME)
        blocks = cls.b.vessel_blocks(cls.lines)
        cls.names = [n for _s, _e, n in blocks]
        cls.vessel = cls.lines[blocks[0][0]:blocks[0][1]]
        base = _lines(cls.b.BASE_NAME)
        bb = cls.b.vessel_blocks(base)
        cls.base_vessel = base[bb[0][0]:bb[0][1]]

    def _lab_values(self):
        m_start, m_end, _p, _e = self.b.lab_module_range(self.vessel)
        mod = self.vessel[m_start:m_end]
        vals = {}
        for line in mod:
            if line.startswith("\t\t\t\t\t") and not line.startswith("\t\t\t\t\t\t") \
                    and " = " in line:
                k, v = line.strip().split(" = ", 1)
                vals[k] = v
        i = mod.index("\t\t\t\t\tEXPERIMENT_DATA")
        j = mod.index("\t\t\t\t\t}", i)
        return vals, [l.strip() for l in mod[i + 2:j]]

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.b.committed_problems(),
                         "re-run python harness/tools/build_lab_lko_transmit.py")

    def test_one_orbiting_lab_vessel_with_its_crew(self):
        self.assertEqual([self.b.VESSEL_NAME], self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        self.assertIn("\tMode = SCIENCE_SANDBOX", self.lines)
        self.assertIn("\t\t\tsit = ORBITING", self.vessel)
        self.assertIn("\t\t\tpersistentId = 2200110055", self.vessel)
        crew = [l.strip() for l in self.vessel if l.startswith("\t\t\t\tcrew = ")]
        self.assertEqual(["crew = Valentina Kerman", "crew = Bob Kerman"], crew)

    def test_the_tender_is_joined_by_two_preattached_jr_ports(self):
        v = self.vessel
        ports = [i for i, l in enumerate(v) if l == "\t\t\t\tname = dockingPort3"]
        self.assertEqual(len(ports), 2)
        states = [l.strip() for l in v if l.strip().startswith("state = PreAttached")]
        self.assertEqual(len(states), 2, "the Re-Fly separation is the first Undock "
                         "of an editor-joined (PreAttached) pair")
        names = [l.strip() for l in v if l.startswith("\t\t\t\tname = ")]
        for part in ("probeCoreOcto.v2", "batteryBankMini", "SurfAntenna",
                     "Large.Crewed.Lab"):
            self.assertIn("name = " + part, names)

    def test_the_lab_holds_12_5_science_and_no_data(self):
        vals, entries = self._lab_values()
        self.assertEqual(vals["storedScience"], "12.5")
        self.assertEqual(vals["dataStored"], "0")
        self.assertLess(float(vals["storedScience"]), self.b.SCIENCE_CAP)
        self.assertEqual(entries, [], "every subject stays storable")

    def test_research_is_off(self):
        v = self.vessel
        conv = [i for i, l in enumerate(v) if l == "\t\t\t\tMODULE"
                and v[i + 2] == "\t\t\t\t\tname = ModuleScienceConverter"]
        self.assertEqual(len(conv), 1)
        self.assertIn("\t\t\t\t\tIsActivated = False",
                      v[conv[0]:self.b._block_end(v, conv[0])])

    def test_the_vessel_is_the_base_but_for_the_stored_science(self):
        self.assertEqual(len(self.vessel), len(self.base_vessel))
        diff = [(a, c) for a, c in zip(self.vessel, self.base_vessel) if a != c]
        self.assertEqual([(a.strip(), c.strip()) for a, c in diff],
                         [("storedScience = 12.5", "storedScience = 0")])


if __name__ == "__main__":
    unittest.main()
