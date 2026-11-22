"""Drift and shape gates for the `gs1-armed-chute-drop` fixture (ASR-8's host).

It is DERIVED from `gs1-two-stage-pad` by `harness/tools/build_gs1_armed_chute_drop.py`
(read its docstring for why): the GS1 Auto-Chute Booster lifted 62 km straight above the
pad, at the top of a near-vertical arc and at rest relative to the air, so its seven
chutes can be armed where the air is far too thin to open them. These cells keep the
derivation mechanical and the numbers the lane relies on honest.
"""
import importlib.util
import math
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_gs1_armed_chute_drop.py")
SOURCE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs1-two-stage-pad", "persistent.sfs")
FIXTURE = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gs1-armed-chute-drop", "persistent.sfs")
KERBIN_GM = 3.5316e12
# The highest altitude at which Kerbin's static pressure reaches 0.04 atm (the chutes'
# `minAirPressureToOpen`) is about 16-18 km; the apex must sit far above all of it.
OPENING_LINE_CEILING_METERS = 20000.0


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_gs1_armed_chute_drop", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _vessel(text, builder):
    start, end = builder._vessel_span(text, builder.VESSEL_NAME)
    return text[start:end]


def _orbit(vessel_text):
    block = re.search(r"\n\t\t\tORBIT\n\t\t\t\{\n((?:\t\t\t\t[^\n]*\n)*)\t\t\t\}\n",
                      vessel_text).group(1)
    return {k: v for k, v in re.findall(r"\t\t\t\t(\w+) = ([^\n]*)\n", block)}


def _save_ut(sfs_text):
    return float(re.search(r"\n\t\tUT = ([^\n]+)\n", sfs_text).group(1))


class Gs1ArmedChuteDropDriftTests(unittest.TestCase):

    def test_the_committed_fixture_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_derivation_changes_only_the_situation_fields_and_two_elements(self):
        source = open(SOURCE, encoding="utf-8").read().split("\n")
        derived = open(FIXTURE, encoding="utf-8").read().split("\n")
        self.assertEqual(len(source), len(derived))
        changed = sorted({b.strip().split(" =")[0]
                          for a, b in zip(source, derived) if a != b})
        self.assertEqual(["ECC", "SMA", "alt", "displaylandedAt", "hgt", "landed",
                          "landedAt", "sit"], changed)

    def test_the_craft_starts_at_its_apex_over_the_pad_at_rest_relative_to_the_air(self):
        builder = _load_builder()
        text = open(FIXTURE, encoding="utf-8").read()
        src = _orbit(_vessel(open(SOURCE, encoding="utf-8").read(), builder))
        orbit = _orbit(_vessel(text, builder))
        sma, ecc = float(orbit["SMA"]), float(orbit["ECC"])
        self.assertEqual("1", orbit["REF"])
        # At apoapsis at the save's UT, in the pad vessel's own direction.
        self.assertAlmostEqual(math.pi, float(orbit["MNA"]), places=6)
        self.assertEqual(_save_ut(text), float(orbit["EPH"]))
        for key in ("INC", "LPE", "LAN", "MNA", "EPH"):
            self.assertEqual(src[key], orbit[key], key)
        ra = sma * (1.0 + ecc)
        src_ra = float(src["SMA"]) * (1.0 + float(src["ECC"]))
        self.assertAlmostEqual(builder.LIFT_METERS, ra - src_ra, delta=1.0)
        apex_altitude = ra - builder.KERBIN_RADIUS
        self.assertGreater(apex_altitude, 3.0 * OPENING_LINE_CEILING_METERS)
        self.assertLess(apex_altitude, 70000.0, "inside the atmosphere: FLYING")
        # Apex speed (vis-viva) equals Kerbin's co-rotation speed there: no drift.
        v_apex = math.sqrt(KERBIN_GM * (2.0 / ra - 1.0 / sma))
        self.assertAlmostEqual(builder.corotation_speed(ra), v_apex, delta=0.05)
        # The arc dives into the planet: the drag-free fall ends at the ground.
        self.assertLess(sma * (1.0 - ecc), builder.KERBIN_RADIUS - 500000.0)
        # Over a minute of game time to fall to the opening line from rest at the apex.
        g = KERBIN_GM / ra ** 2
        self.assertGreater(math.sqrt(2.0 * (apex_altitude - OPENING_LINE_CEILING_METERS) / g),
                           60.0)

    def test_the_craft_is_flying_with_its_crew_decoupler_and_seven_stowed_chutes(self):
        builder = _load_builder()
        vessel = _vessel(open(FIXTURE, encoding="utf-8").read(), builder)
        self.assertIn("\n\t\t\tsit = FLYING\n", vessel)
        self.assertIn("\n\t\t\tlanded = False\n", vessel)
        self.assertIn("crew = Valentina Kerman", vessel)
        self.assertIn("name = probeCoreOcto2.v2", vessel)
        self.assertRegex(vessel, r"name = Decoupler\.1\n(?:\t\t\t\t[^\n]*\n)*?"
                                 r"\t\t\t\tpersistentId = 1500054019\n")
        self.assertEqual(1, len(re.findall(r"\n\t\t\t\tname = parachuteSingle\n", vessel)))
        self.assertEqual(6, len(re.findall(r"\n\t\t\t\tname = parachuteRadial\n", vessel)))
        chute_states = re.findall(r"name = ModuleParachute\n(?:\t\t\t\t\t[^\n]*\n)*?"
                                  r"\t\t\t\t\tpersistentState = (\w+)\n", vessel)
        self.assertEqual(["STOWED"] * 7, chute_states)
        thresholds = re.findall(r"\n\t\t\t\t\tminAirPressureToOpen = ([^\n]*)\n", vessel)
        self.assertEqual(7, len(thresholds))
        for value in thresholds:
            self.assertAlmostEqual(0.04, float(value), places=6)


if __name__ == "__main__":
    unittest.main()
