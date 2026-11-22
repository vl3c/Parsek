"""Drift and shape gates for the `kerbin-escape-probe-stack` fixture (ASR-9's host).

The fixture is DERIVED from `gs2-orbital-stack` by
`harness/tools/build_kerbin_escape_probe_stack.py` (read its docstring for why): the same
Kerbal X stack with its probe-cored core, placed on a Kerbin escape hyperbola whose SOI
exit is a fixed 1800 game s after the save's UT, beyond every moon, into a bound solar
orbit. These cells keep the derivation mechanical and the lane's premise honest.
"""
import importlib.util
import math
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_kerbin_escape_probe_stack.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "kerbin-escape-probe-stack",
                       "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs2-orbital-stack", "persistent.sfs")
SPEC = os.path.join(HARNESS_ROOT, "scenarios", "ASR-9-escape-stage-ends-orbiting-sun.toml")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_kerbin_escape_probe_stack", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _orbit(sfs_text: str, builder):
    start, end = builder._vessel_span(sfs_text, builder.VESSEL_NAME)
    block = re.search(r"\n\t\t\tORBIT\n\t\t\t\{\n((?:\t\t\t\t[^\n]*\n)*)\t\t\t\}\n",
                      sfs_text[start:end]).group(1)
    return {k: v for k, v in re.findall(r"\t\t\t\t(\w+) = ([^\n]*)\n", block)}


def _save_ut(sfs_text: str) -> float:
    return float(re.search(r"\n\t\tUT = ([^\n]+)\n", sfs_text).group(1))


class KerbinEscapeProbeStackDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_only_the_stacks_situation_altitude_and_orbit(self):
        source = open(SOURCE, encoding="utf-8").read().split("\n")
        derived = open(FIXTURE, encoding="utf-8").read().split("\n")
        self.assertEqual(len(source), len(derived), "the derivation must not add or drop lines")
        changed = sorted({b.strip().split(" = ")[0] for a, b in zip(source, derived) if a != b})
        self.assertEqual(["ECC", "MNA", "SMA", "alt", "hgt", "sit"], changed)

    def test_the_stack_escapes_kerbin_past_every_moon_into_a_bound_solar_orbit(self):
        builder = _load_builder()
        text = open(FIXTURE, encoding="utf-8").read()
        orbit = _orbit(text, builder)
        self.assertEqual("1", orbit["REF"])
        sma, ecc, mna = float(orbit["SMA"]), float(orbit["ECC"]), float(orbit["MNA"])
        self.assertLess(sma, 0.0)
        self.assertGreater(ecc, 1.0)
        self.assertGreater(mna, 0.0, "outbound leg (past periapsis)")
        self.assertEqual(_save_ut(text), float(orbit["EPH"]))
        self.assertAlmostEqual(1800.0, builder.seconds_to_soi_exit(sma, ecc, mna), places=3)
        radius = builder.radius_at_mean_anomaly(sma, ecc, mna)
        self.assertLess(radius, builder.KERBIN_SOI)
        # Outbound and beyond Minmus's orbit by more than its SOI: no moon encounter
        # between the save and the exit, whatever the asymptote direction.
        self.assertGreater(radius, builder.MINMUS_ORBIT_RADIUS + 2.0 * builder.MINMUS_SOI)
        # Bound around the Sun after the exit whatever the asymptote direction: v_inf
        # plus Kerbin's orbital speed stays under the Sun's escape speed there.
        v_inf = math.sqrt(builder.KERBIN_GM / abs(sma))
        kerbin_speed = math.sqrt(builder.SUN_GM / builder.KERBIN_ORBIT_RADIUS)
        escape = math.sqrt(2.0 * builder.SUN_GM / builder.KERBIN_ORBIT_RADIUS)
        self.assertLess(v_inf + kerbin_speed, escape)

    def test_the_spec_dwell_stays_short_of_the_exit(self):
        builder = _load_builder()
        text = open(FIXTURE, encoding="utf-8").read()
        exit_ut = _save_ut(text) + builder.EXIT_LEAD_SECONDS
        spec = open(SPEC, encoding="utf-8").read()
        dwell = [float(t) for t in re.findall(
            r'^\s*\{ cmd = "WarpToUT",\s*args = \{ ut = "([0-9.]+)", maxRate = "1" \}',
            spec, re.M)]
        self.assertEqual(1, len(dwell), "ASR-9 carries exactly one 1x dwell and no other warp")
        self.assertEqual(1, len(re.findall(r'^\s*\{ cmd = "WarpToUT"', spec, re.M)))
        self.assertGreater(dwell[0], _save_ut(text))
        # The commit must come while the probe is still inside Kerbin's SOI: the finalizer,
        # not stock, carries it across the boundary.
        self.assertLess(dwell[0], exit_ut - 600.0)

    def test_the_stack_still_carries_the_probe_core_and_the_decoupler_the_lane_fires(self):
        text = open(FIXTURE, encoding="utf-8").read()
        builder = _load_builder()
        start, end = builder._vessel_span(text, builder.VESSEL_NAME)
        vessel = text[start:end]
        self.assertIn("name = probeStackLarge", vessel)
        self.assertIn("name = mk1-3pod", vessel)
        self.assertRegex(vessel, r"name = Decoupler\.2\n(?:\t\t\t\t[^\n]*\n)*?"
                                 r"\t\t\t\tpersistentId = 2923926328\n")
        self.assertIn("\n\t\t\tsit = ESCAPING\n", vessel)


if __name__ == "__main__":
    unittest.main()
