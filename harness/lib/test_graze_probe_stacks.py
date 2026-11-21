"""Drift and shape gates for the `graze-high-probe-stack` / `graze-low-probe-stack`
fixtures (ASR-3 / ASR-4's hosts).

Both are DERIVED from `gs2-orbital-stack` by `harness/tools/build_graze_probe_stacks.py`
(read its docstring for why): the same Kerbal X stack with its probe-cored core, moved
onto a grazing Kerbin orbit whose apoapsis is the LKO orbit's own and whose periapsis is
40 km (above the 1 kPa line where stock deletes an unloaded vessel) or 15 km (below it),
starting at apoapsis at the save's UT. These cells keep the derivation mechanical and
the two periapses honest.
"""
import importlib.util
import math
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_graze_probe_stacks.py")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs2-orbital-stack", "persistent.sfs")
KERBIN_GM = 3.5316e12
# The altitude band stock's unloaded-vessel deletion (static pressure > 1 kPa) lands in
# on Kerbin; the two fixtures must sit on opposite sides of all of it.
ONE_KPA_BAND = (22000.0, 25000.0)


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_graze_probe_stacks", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _fixture(leaf):
    return os.path.join(HARNESS_ROOT, "fixtures", "saves", leaf, "persistent.sfs")


def _orbit(sfs_text, builder):
    start, end = builder._vessel_span(sfs_text, builder.VESSEL_NAME)
    block = re.search(r"\n\t\t\tORBIT\n\t\t\t\{\n((?:\t\t\t\t[^\n]*\n)*)\t\t\t\}\n",
                      sfs_text[start:end]).group(1)
    return {k: v for k, v in re.findall(r"\t\t\t\t(\w+) = ([^\n]*)\n", block)}


def _save_ut(sfs_text):
    return float(re.search(r"\n\t\tUT = ([^\n]+)\n", sfs_text).group(1))


class GrazeProbeStackDriftTests(unittest.TestCase):

    def test_the_committed_fixtures_are_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_only_situation_altitude_and_three_elements(self):
        source = open(SOURCE, encoding="utf-8").read().split("\n")
        for leaf in _load_builder().PERIAPSIS_ALTITUDES:
            derived = open(_fixture(leaf), encoding="utf-8").read().split("\n")
            self.assertEqual(len(source), len(derived), leaf)
            changed = sorted({b.strip().split(" = ")[0]
                              for a, b in zip(source, derived) if a != b})
            self.assertEqual(["ECC", "MNA", "SMA", "alt", "sit"], changed, leaf)

    def test_the_periapses_straddle_the_one_kpa_line_and_start_at_apoapsis(self):
        builder = _load_builder()
        src_orbit = _orbit(open(SOURCE, encoding="utf-8").read(), builder)
        src_ra = float(src_orbit["SMA"]) * (1.0 + float(src_orbit["ECC"]))
        peri = {}
        for leaf, want in builder.PERIAPSIS_ALTITUDES.items():
            text = open(_fixture(leaf), encoding="utf-8").read()
            orbit = _orbit(text, builder)
            sma, ecc, mna = float(orbit["SMA"]), float(orbit["ECC"]), float(orbit["MNA"])
            self.assertEqual("1", orbit["REF"], leaf)
            self.assertAlmostEqual(math.pi, mna, places=9, msg=leaf)
            self.assertEqual(_save_ut(text), float(orbit["EPH"]), leaf)
            self.assertAlmostEqual(src_ra, sma * (1.0 + ecc), delta=0.01, msg=leaf)
            peri[leaf] = sma * (1.0 - ecc) - builder.KERBIN_RADIUS
            self.assertAlmostEqual(want, peri[leaf], delta=0.01, msg=leaf)
            # Starts above the atmosphere, with the descent to 70 km minutes away.
            self.assertGreater(sma * (1.0 + ecc) - builder.KERBIN_RADIUS, 100000.0, leaf)
            period = 2.0 * math.pi * math.sqrt(sma ** 3 / KERBIN_GM)
            self.assertGreater(period / 2.0, 600.0, leaf)
        self.assertGreater(peri["graze-high-probe-stack"], ONE_KPA_BAND[1] + 10000.0)
        self.assertLess(peri["graze-low-probe-stack"], ONE_KPA_BAND[0] - 5000.0)
        self.assertLess(peri["graze-high-probe-stack"], 70000.0, "still a grazing orbit")

    def test_the_stack_still_carries_the_probe_core_and_the_decoupler(self):
        builder = _load_builder()
        for leaf in builder.PERIAPSIS_ALTITUDES:
            text = open(_fixture(leaf), encoding="utf-8").read()
            start, end = builder._vessel_span(text, builder.VESSEL_NAME)
            vessel = text[start:end]
            self.assertIn("sit = SUB_ORBITAL", vessel, leaf)
            self.assertIn("name = probeStackLarge", vessel, leaf)
            self.assertIn("name = mk1-3pod", vessel, leaf)
            self.assertRegex(vessel, r"name = Decoupler\.2\n(?:\t\t\t\t[^\n]*\n)*?"
                                     r"\t\t\t\tpersistentId = 2923926328\n")


if __name__ == "__main__":
    unittest.main()
