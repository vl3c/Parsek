"""Fixture gates for `srv-hinge-pad` (the SRV-1 host).

The fixture is DERIVED by `harness/tools/build_srv_hinge_pad.py` from `fresh-sandbox`, so a
change to the base must re-run the builder. These cells turn that "must" into a red and pin
what SRV-1 depends on: the rig is an OKTO2 on an FL-T100 with a G-11 Hinge whose MOVING
(top) node carries a Z-200, so exactly one part rides the hinge on the replay, and the
watcher shares no baked persistentId with it.

No committed spec flies this fixture since 2026-11-21: SRV-1 moved to `srv-hinge-rover`
(a pad rig's ghost is hidden by the launch-site rule). The fixture and these cells stay
until their removal is approved.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_srv_hinge_pad.py")
    spec = importlib.util.spec_from_file_location("build_srv_hinge_pad", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class SrvHingePadFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()

    def test_committed_bytes_match_the_builder(self):
        self.assertEqual([], self.builder.check())

    def test_the_craft_verify(self):
        self.assertEqual([], self.builder.verify(self.builder.build_files()))

    def test_the_save_is_vessel_less_so_the_pad_is_free(self):
        files = self.builder.build_files()
        sfs = files["persistent.sfs"].decode("utf-8")
        self.assertNotIn("\n\t\tVESSEL\n", sfs)
        self.assertIn("\tTitle = srv-hinge-pad (SANDBOX)\n", sfs)

    def test_verify_catches_the_battery_on_the_base_node(self):
        # The negative control: the battery moved to the hinge's bottom node must red, or
        # the verify cell above would pass on a craft whose replay has no follower.
        files = self.builder.build_files()
        rig = files[self.builder.RIG_REL].decode("utf-8")
        battery_ref = "%s_%d" % (self.builder.BATTERY, self.builder.RIG_UID["battery"])
        swapped = rig.replace("\tattN = top," + battery_ref + "_",
                              "\tattN = side," + battery_ref + "_")
        self.assertNotEqual(rig, swapped)
        files[self.builder.RIG_REL] = swapped.encode("utf-8")
        self.assertTrue(any("moving" in p for p in self.builder.verify(files)))


if __name__ == "__main__":
    unittest.main()
