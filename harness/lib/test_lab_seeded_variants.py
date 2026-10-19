"""Fixture gates for `lab-pad-seeded`, `lab-lko-seeded` and `lab-lko-relay` (the seeded science-lab hosts).

Both are DERIVED by `harness/tools/build_lab_seeded_variants.py` from the committed
`lab-lko-science`, so a change to it must re-run the builder. These cells pin what the lab
lanes depend on: the lab's seeded dataStored / storedScience strictly under the stock caps,
an EXPERIMENT_DATA list of distinct stock subject ids, research off, the crew where the
craft seats them, and every other line of the vessel the base's. Pad: the vessel is
PRELAUNCH on the LaunchPad with every pad-dependent key the template's (Revert to Launch
needs PRELAUNCH at load). LKO: the vessel is the base's, ORBITING, with the four InSpaceLow
subjects left storable. Relay: the base's orbiting vessel with the pad's lab seed, and the
one save-level edit `enableGroundStations = False` inside CommNetParams.

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


def _load(name):
    path = os.path.join(_HARNESS, "tools", name + ".py")
    sys.path.insert(0, os.path.join(_HARNESS, "tools"))
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _lines(name):
    with open(os.path.join(SAVES, name, "persistent.sfs"), "r", encoding="utf-8",
              newline="") as fh:
        return fh.read().replace("\r\n", "\n").split("\n")


class _Base(unittest.TestCase):
    VARIANT = None

    @classmethod
    def setUpClass(cls):
        cls.b = _load("build_lab_seeded_variants")
        cls.lines = _lines(cls.VARIANT)
        blocks = cls.b.vessel_blocks(cls.lines)
        cls.names = [n for _s, _e, n in blocks]
        cls.vessel = cls.lines[blocks[0][0]:blocks[0][1]]
        base = _lines(cls.b.BASE_NAME)
        bb = cls.b.vessel_blocks(base)
        cls.base_vessel = base[bb[0][0]:bb[0][1]]

    def _lab_module(self):
        v = self.vessel
        lab = [i for i, l in enumerate(v)
               if l == "\t\t\tPART" and v[i + 2] == "\t\t\t\tname = Large.Crewed.Lab"]
        self.assertEqual(len(lab), 1)
        end = self.b._block_end(v, lab[0])
        mods = [i for i in range(lab[0], end) if v[i] == "\t\t\t\tMODULE"
                and v[i + 2] == "\t\t\t\t\tname = ModuleScienceLab"]
        self.assertEqual(len(mods), 1)
        return v[mods[0]:self.b._block_end(v, mods[0])]

    def _lab_values(self):
        mod = self._lab_module()
        vals = {}
        for line in mod:
            if line.startswith("\t\t\t\t\t") and not line.startswith("\t\t\t\t\t\t") \
                    and " = " in line:
                k, v = line.strip().split(" = ", 1)
                vals[k] = v
        i = mod.index("\t\t\t\t\tEXPERIMENT_DATA")
        j = mod.index("\t\t\t\t\t}", i)
        entries = [l.strip() for l in mod[i + 2:j]]
        return vals, entries

    def _shared(self):
        self.assertEqual([], self.b.committed_problems(self.VARIANT),
                         "re-run python harness/tools/build_lab_seeded_variants.py")
        self.assertEqual([self.b.VESSEL_NAME], self.names)
        self.assertIn("\t\tactiveVessel = 0", self.lines)
        self.assertIn("\tMode = SCIENCE_SANDBOX", self.lines)
        crew = [l.strip() for l in self.vessel if l.startswith("\t\t\t\tcrew = ")]
        self.assertEqual(["crew = Valentina Kerman", "crew = Bob Kerman"], crew)

    def _seed_holds(self, data, science, n_entries):
        vals, entries = self._lab_values()
        self.assertEqual(vals["dataStored"], data)
        self.assertEqual(vals["storedScience"], science)
        self.assertLess(float(vals["dataStored"]), self.b.DATA_STORAGE)
        self.assertLess(float(vals["storedScience"]), self.b.SCIENCE_CAP)
        self.assertEqual(len(entries), n_entries)
        self.assertEqual(len(set(entries)), n_entries, "a lab never stores a subject twice")
        for e in entries:
            key, sid = e.split(" = ", 1)
            self.assertEqual(key, "experiment")
            exp, _, sit = sid.partition("@Kerbin")
            self.assertIn(exp, [x for x, _d in self.b.EXPERIMENTS])
            self.assertIn(sit, [s for s, _v in self.b.SITUATIONS])
        return entries

    def _research_off(self):
        v = self.vessel
        conv = [i for i, l in enumerate(v) if l == "\t\t\t\tMODULE"
                and v[i + 2] == "\t\t\t\t\tname = ModuleScienceConverter"]
        self.assertEqual(len(conv), 1)
        self.assertIn("\t\t\t\t\tIsActivated = False",
                      v[conv[0]:self.b._block_end(v, conv[0])])


class StockLabArithmeticTests(unittest.TestCase):

    def test_single_precision_lab_values(self):
        b = _load("build_lab_seeded_variants")
        # FlyingHigh Goo is 13.4999990 in float and rounds DOWN; InSpaceLow PresMat 7.5
        # and Materials Bay 22.5 round half to even.
        self.assertEqual(b.lab_value(12.0, 0.9), 13)
        self.assertEqual(b.lab_value(6.0, 1.0), 8)
        self.assertEqual(b.lab_value(18.0, 1.0), 22)
        self.assertEqual(sum(v for _s, v in b.PAD_SUBJECTS), 204)
        self.assertEqual(sum(v for _s, v in b.LKO_SUBJECTS), 154)


class LabPadSeededTests(_Base):
    VARIANT = "lab-pad-seeded"

    def test_shared(self):
        self._shared()
        self._research_off()

    def test_lab_seed_is_the_stock_sum_of_its_sixteen_subjects(self):
        entries = self._seed_holds("204", "12.5", 16)
        self.assertEqual([e.split(" = ")[1] for e in entries],
                         [s for s, _v in self.b.PAD_SUBJECTS])

    def test_prelaunch_on_the_pad_with_the_templates_placement(self):
        b = self.b
        tmpl = _lines(b.TEMPLATE_NAME)
        tb = [x for x in b.vessel_blocks(tmpl) if x[2] == b.TEMPLATE_VESSEL][0]
        tv = tmpl[tb[0]:tb[1]]
        for key in b.PAD_COPIED_KEYS:
            self.assertEqual(b._value(self.vessel, key), b._value(tv, key), key)
        self.assertEqual(b._value(self.vessel, "sit"), "PRELAUNCH")
        self.assertEqual(b._value(self.vessel, "landed"), "True")
        self.assertEqual(b._value(self.vessel, "landedAt"), "LaunchPad")
        self.assertEqual(b._value(self.vessel, "met"), "0")
        pad = float(b._value(tv, "alt")) - float(b._value(tv, "hgt"))
        self.assertAlmostEqual(float(b._value(self.vessel, "alt"))
                               - float(b._value(self.vessel, "hgt")), pad, places=6)
        self.assertEqual(b._value(self.vessel, "skipGroundPositioning"), "False")

    def test_only_the_placement_keys_orbit_and_lab_differ_from_the_base(self):
        changed_keys = set()
        mine = [l for l in self.vessel]
        base = [l for l in self.base_vessel]
        o_m = self.b._orbit(mine)
        o_b = self.b._orbit(base)
        mine = mine[:o_m[0]] + mine[o_m[1]:]
        base = base[:o_b[0]] + base[o_b[1]:]
        stripped = [l for l in mine if not l.startswith("\t\t\t\t\t\texperiment = ")]
        self.assertEqual(len(stripped), len(base))
        for a, c in zip(stripped, base):
            if a != c:
                changed_keys.add(a.strip().split(" = ")[0])
        # lastUT is absent because the base's already equals the save UT.
        self.assertEqual(changed_keys, set(self.b.PAD_COPIED_KEYS) | {
            "alt", "hgt", "met", "lct", "distanceTraveled", "dataStored", "storedScience"})


class LabLkoSeededTests(_Base):
    VARIANT = "lab-lko-seeded"

    def test_shared(self):
        self._shared()
        self._research_off()

    def test_lab_seed_is_just_under_both_caps(self):
        entries = self._seed_holds("740", "499", 12)
        self.assertFalse([e for e in entries if "InSpaceLow" in e],
                         "the InSpaceLow subjects stay storable in this orbit")

    def test_the_vessel_is_the_base_but_for_the_lab(self):
        self.assertEqual(self.b._value(self.vessel, "sit"), "ORBITING")
        mine = [l for l in self.vessel if not l.startswith("\t\t\t\t\t\texperiment = ")]
        diff = [(a, c) for a, c in zip(mine, self.base_vessel) if a != c]
        self.assertEqual(len(mine), len(self.base_vessel))
        self.assertEqual(sorted(a.strip() for a, _c in diff),
                         ["dataStored = 740", "storedScience = 499"])


class LabLkoRelayTests(_Base):
    VARIANT = "lab-lko-relay"

    def test_shared(self):
        self._shared()
        self._research_off()

    def test_lab_holds_the_pads_seed(self):
        entries = self._seed_holds("204", "12.5", 16)
        self.assertEqual([e.split(" = ")[1] for e in entries],
                         [s for s, _v in self.b.PAD_SUBJECTS])

    def test_the_vessel_is_the_base_but_for_the_lab(self):
        self.assertEqual(self.b._value(self.vessel, "sit"), "ORBITING")
        mine = [l for l in self.vessel if not l.startswith("\t\t\t\t\t\texperiment = ")]
        diff = [(a, c) for a, c in zip(mine, self.base_vessel) if a != c]
        self.assertEqual(len(mine), len(self.base_vessel))
        self.assertEqual(sorted(a.strip() for a, _c in diff),
                         ["dataStored = 204", "storedScience = 12.5"])

    def test_ground_stations_off_and_nothing_else_in_commnet_changed(self):
        base = _lines(self.b.BASE_NAME)
        cn = self.lines.index("\t\tCommNetParams")
        end = self.b._block_end(self.lines, cn)
        block = self.lines[cn:end]
        self.assertIn("\t\t\tenableGroundStations = False", block)
        self.assertNotIn("\t\t\tenableGroundStations = True", self.lines)
        # The occluder and range stock reads: Kerbin's full radius, the base's range.
        self.assertIn("\t\t\tocclusionMultiplierAtm = 1", block)
        self.assertIn("\t\t\trangeModifier = 0.65", block)
        self.assertIn("\t\t\tEnableCommNet = True", self.lines)
        # The only home left without ground stations is the KSC unless other launch
        # sites are allowed (decompiled CommNetHome.CreateNode).
        self.assertIn("\t\t\tAllowOtherLaunchSites = False", self.lines)
        b_cn = base.index("\t\tCommNetParams")
        b_block = base[b_cn:self.b._block_end(base, b_cn)]
        changed = [a for a, c in zip(block, b_block) if a != c]
        self.assertEqual(changed, ["\t\t\tenableGroundStations = False"])

    def test_only_one_vessel_so_no_live_relay_exists(self):
        self.assertEqual([self.b.VESSEL_NAME], self.names)


if __name__ == "__main__":
    unittest.main()
