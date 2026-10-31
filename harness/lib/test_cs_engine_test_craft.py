"""Drift and shape gates for `CS Engine Test.craft` (RW-17's craft, in `cs-career-mun1`)."""

import importlib.util
import os
import re
import unittest

HARNESS_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir))
BUILDER = os.path.join(HARNESS_ROOT, "tools", "build_cs_engine_test_craft.py")


def _load_builder():
    spec = importlib.util.spec_from_file_location("build_cs_engine_test_craft", BUILDER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CsEngineTestCraftTests(unittest.TestCase):

    def test_the_committed_craft_is_byte_identical_to_a_fresh_derivation(self):
        self.assertEqual([], _load_builder().check())

    def test_the_craft_is_the_flea_plus_one_terrier_linked_from_the_booster(self):
        builder = _load_builder()
        text = builder.build()
        base = builder._read(builder.BASE)
        parts = re.findall(r"(?m)^\tpart = (\S+)$", text)
        base_parts = re.findall(r"(?m)^\tpart = (\S+)$", base)
        self.assertEqual(base_parts, parts[:-1])
        token = parts[-1]
        self.assertTrue(token.startswith("liquidEngine3.v2_"))
        self.assertIn("\tlink = %s\n" % token, text)
        self.assertRegex(text.split("\tpart = %s\n" % token, 1)[1],
                         r"(?m)^\tattN = top,solidBooster\.sm\.v2_\d+_0\|")
        # The contract's Run Test button lives on this module.
        self.assertIn("name = ModuleTestSubject", text.split("\tpart = %s\n" % token, 1)[1])

    def test_the_ship_name_is_the_one_the_lane_builds(self):
        self.assertRegex(_load_builder().build(), r"(?m)^ship = CS Engine Test$")


if __name__ == "__main__":
    unittest.main()
