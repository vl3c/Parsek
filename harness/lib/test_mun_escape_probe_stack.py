"""Drift and shape gates for the `mun-escape-probe-stack` fixture (SOI-3's host).

The fixture is DERIVED from `gs2-orbital-stack` by
`harness/tools/build_mun_escape_probe_stack.py` (read its docstring for why): the same
Kerbal X stack with its probe-cored core, placed on a Mun escape hyperbola whose SOI exit
is a fixed 900 game s after the save's UT, so SOI-3 can release the probe and rails-warp
both across the boundary with an explicit-UT WarpToUT and no burn. These cells keep the
derivation mechanical and the spec's warp target honest.
"""
import importlib.util
import math
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_mun_escape_probe_stack.py")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "mun-escape-probe-stack",
                       "persistent.sfs")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs2-orbital-stack", "persistent.sfs")
SPEC = os.path.join(HARNESS_ROOT, "scenarios", "SOI-3-bg-probe-mun-exit-on-rails.toml")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_mun_escape_probe_stack", BUILDER)
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


class MunEscapeProbeStackDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_only_the_stacks_situation_altitude_and_orbit(self):
        source = open(SOURCE, encoding="utf-8").read().split("\n")
        derived = open(FIXTURE, encoding="utf-8").read().split("\n")
        self.assertEqual(len(source), len(derived), "the derivation must not add or drop lines")
        changed = sorted({b.strip().split(" = ")[0] for a, b in zip(source, derived) if a != b})
        self.assertEqual(["ECC", "MNA", "REF", "SMA", "alt", "hgt", "sit"], changed)

    def test_the_stack_escapes_the_mun_and_exits_its_soi_900_s_after_the_save_ut(self):
        builder = _load_builder()
        text = open(FIXTURE, encoding="utf-8").read()
        orbit = _orbit(text, builder)
        self.assertEqual("2", orbit["REF"])
        sma, ecc, mna = float(orbit["SMA"]), float(orbit["ECC"]), float(orbit["MNA"])
        self.assertLess(sma, 0.0)
        self.assertGreater(ecc, 1.0)
        self.assertGreater(mna, 0.0, "outbound leg (past periapsis)")
        self.assertEqual(_save_ut(text), float(orbit["EPH"]))
        to_exit = builder.seconds_to_soi_exit(sma, ecc, mna)
        self.assertAlmostEqual(900.0, to_exit, places=3)
        radius = builder.radius_at_mean_anomaly(sma, ecc, mna)
        self.assertLess(radius, builder.MUN_SOI)
        self.assertGreater(radius - builder.MUN_RADIUS, 1000000.0)
        # Bound around Kerbin after the exit whatever the asymptote direction:
        # v_inf + the Mun's orbital speed stays under Kerbin's escape speed there.
        v_inf = math.sqrt(builder.MUN_GM / abs(sma))
        kerbin_gm, mun_orbit_radius = 3.5316e12, 12000000.0
        mun_speed = math.sqrt(kerbin_gm / mun_orbit_radius)
        escape = math.sqrt(2.0 * kerbin_gm / mun_orbit_radius)
        self.assertLess(v_inf + mun_speed, escape)

    def test_the_spec_warps_past_the_exit_with_margin(self):
        builder = _load_builder()
        text = open(FIXTURE, encoding="utf-8").read()
        exit_ut = _save_ut(text) + builder.EXIT_LEAD_SECONDS
        spec = open(SPEC, encoding="utf-8").read()
        rails = [float(t) for t in re.findall(
            r'^\s*\{ cmd = "WarpToUT",\s*args = \{ ut = "([0-9.]+)" \}', spec, re.M)]
        dwell = [float(t) for t in re.findall(
            r'^\s*\{ cmd = "WarpToUT",\s*args = \{ ut = "([0-9.]+)", maxRate = "1" \}',
            spec, re.M)]
        self.assertEqual(1, len(rails), "SOI-3 carries exactly one uncapped WarpToUT")
        # Past the exit by more than a decoupler kick could move it, short of a long tail.
        self.assertGreater(rails[0], exit_ut + 60.0)
        self.assertLess(rails[0], exit_ut + 600.0)
        # The 1x dwell after the decouple stays short of the exit by a wide margin.
        self.assertEqual(1, len(dwell), "SOI-3 carries exactly one 1x dwell")
        self.assertGreater(dwell[0], _save_ut(text))
        self.assertLess(dwell[0], exit_ut - 600.0)

    def test_the_stack_still_carries_the_probe_core_and_the_decoupler_soi3_fires(self):
        text = open(FIXTURE, encoding="utf-8").read()
        builder = _load_builder()
        start, end = builder._vessel_span(text, builder.VESSEL_NAME)
        vessel = text[start:end]
        self.assertIn("name = probeStackLarge", vessel)
        self.assertIn("name = mk1-3pod", vessel)
        self.assertRegex(vessel, r"name = Decoupler\.2\n(?:\t\t\t\t[^\n]*\n)*?"
                                 r"\t\t\t\tpersistentId = 2923926328\n")


if __name__ == "__main__":
    unittest.main()
