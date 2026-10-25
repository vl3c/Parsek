"""Drift and shape gates for `Flea Antenna.craft` (CS-2's craft, in the `cs-science-flea`
fixture).

The craft is DERIVED from the shared `Jumping Flea.craft` by
`harness/tools/build_cs_flea_antenna_craft.py`: the stock Flea verbatim plus a Communotron
16-S and a 2HOT Thermometer linked from the Flea booster. A hand edit to the craft, or a
change to the derivation, reds here instead of in a flight whose transmit silently has no
antenna.
"""
import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_cs_flea_antenna_craft.py")
BASE = os.path.join(HARNESS_ROOT, "fixtures", "ships", "Jumping Flea.craft")
CRAFT = os.path.join(HARNESS_ROOT, "fixtures", "saves", "cs-science-flea",
                     "Ships", "VAB", "Flea Antenna.craft")
SCENARIOS = os.path.join(HARNESS_ROOT, "scenarios")
HOSTED_SPECS = ("CS-2-science-engineering101.toml",)


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_cs_flea_antenna_craft", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _read(path):
    # The shared base craft is CRLF (KSP-written); the builder normalizes it the same way.
    with open(path, encoding="utf-8", newline="") as fh:
        return fh.read().replace("\r\n", "\n")


def _parts(text):
    return re.findall(r"(?m)^\tpart = (\S+)$", text)


class CsFleaAntennaCraftTests(unittest.TestCase):

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_craft_is_the_flea_plus_exactly_the_two_engineering101_parts(self):
        base = _parts(_read(BASE))
        craft = _parts(_read(CRAFT))
        self.assertEqual(base, craft[:len(base)])
        added = sorted(p.rsplit("_", 1)[0] for p in craft[len(base):])
        self.assertEqual(["SurfAntenna", "sensorThermometer"], added)

    def test_every_added_part_is_linked_from_the_booster_it_attaches_to(self):
        # A child its host does not link is dropped at craft load, silently.
        text = _read(CRAFT)
        for token in _parts(text)[len(_parts(_read(BASE))):]:
            with self.subTest(part=token):
                self.assertIn("\tlink = %s\n" % token, text)
                block = text.split("\tpart = %s\n" % token, 1)[1].split("\n}", 1)[0]
                self.assertRegex(block, r"(?m)^\tsrfN = srfAttach,solidBooster\.sm\.v2_\d+$")

    def test_the_pod_stays_the_first_part_so_its_crew_report_is_transmitted_first(self):
        self.assertTrue(_parts(_read(CRAFT))[0].startswith("mk1pod.v2_"))

    def test_the_ship_is_named_for_the_editor_step(self):
        self.assertRegex(_read(CRAFT), r"(?m)^ship = Flea Antenna$")

    def test_the_hosted_specs_launch_this_craft_from_this_fixture(self):
        for name in HOSTED_SPECS:
            with self.subTest(spec=name):
                text = _read(os.path.join(SCENARIOS, name))
                self.assertRegex(text, r'(?m)^saveTemplate\s*=\s*"fixtures/saves/cs-science-flea"')
                self.assertIn('craft = "Flea Antenna"', text)


if __name__ == "__main__":
    unittest.main()
