"""Fixture gates for `hitchhiker-pad` (the CX-2 host).

The fixture is DERIVED by `harness/tools/build_hitchhiker_pad.py` from `fresh-sandbox`
and the committed stock `Kerbal X.craft` (the Mk1-3 pod and TD-25 tails), so a change to
either must re-run the builder. These cells turn that "must" into a red and pin what CX-2
depends on: the craft is pod / TD-25 / Hitchhiker, the pod is the only control source,
and the Hitchhiker's authored tail carries no MODULE block.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_hitchhiker_pad.py")
    spec = importlib.util.spec_from_file_location("build_hitchhiker_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class HitchhikerPadFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.builder.check())

    def test_the_craft_verifies(self):
        self.assertEqual([], self.builder.verify(self.builder.build_files()))

    def test_the_save_is_vessel_less_so_the_pad_is_free(self):
        files = self.builder.build_files()
        sfs = files["persistent.sfs"].decode("utf-8")
        self.assertNotIn("\n\t\tVESSEL\n", sfs)
        self.assertIn("\tTitle = hitchhiker-pad (SANDBOX)\n", sfs)


if __name__ == "__main__":
    unittest.main()
