"""Pins what the committed `bdock-recorded` route-connection window actually holds
(BDOCK-FIXTURE-TRANSFER-INVERTED).

BDOCK-1 asked for 40 LiquidFuel transport -> station and 15 MonoPropellant station ->
transport. The flight that produced this fixture ran both requests reversed (its runner
told the sides apart by the merged vessel's controlling part), and the Station was full,
so the window records a 15.77 LF PICKUP onto the Interceptor and no MP at all. The owner
kept the fixture as it is (2026-10-16): it is a real pickup, and every lane on it reads
it as one. These cells say so in numbers, so a lane or doc that calls it a delivery, or a
re-harvest that changes the shape, reds here first.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import os
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
SFS = os.path.join(os.path.dirname(HERE), "fixtures", "saves", "bdock-recorded",
                   "persistent.sfs")


def read_window_manifests(path):
    """{block name: {resource: (amount, maxAmount)}} for the RESOURCE lists under the
    one ROUTE_CONNECTION_WINDOWS WINDOW of the save. Pure text scan of the ConfigNode
    layout (node name on its own line, then braces)."""
    with open(path, encoding="utf-8") as fh:
        lines = [ln.strip() for ln in fh]
    start = lines.index("ROUTE_CONNECTION_WINDOWS")
    manifests = {}
    block = None
    res = None
    depth = 0
    prev = None
    for ln in lines[start + 1:]:
        if ln == "{":
            depth += 1
            if depth == 3 and prev and prev.endswith("_RESOURCES"):
                block = prev
                manifests[block] = {}
            elif block and prev == "RESOURCE":
                res = {}
        elif ln == "}":
            if res is not None:
                manifests[block][res["name"]] = (float(res["amount"]),
                                                 float(res["maxAmount"]))
                res = None
            elif depth == 3:
                block = None
            depth -= 1
            if depth == 0:
                break
        elif res is not None and " = " in ln:
            key, value = ln.split(" = ", 1)
            res[key] = value
        prev = ln
    return manifests


class BdockRecordedWindowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.m = read_window_manifests(SFS)

    def delta(self, side, resource):
        undock = self.m["UNDOCK_%s_RESOURCES" % side][resource][0]
        dock = self.m["DOCK_%s_RESOURCES" % side][resource][0]
        return undock - dock

    def test_the_window_has_all_four_manifests(self):
        self.assertEqual(sorted(self.m), [
            "DOCK_ENDPOINT_RESOURCES", "DOCK_TRANSPORT_RESOURCES",
            "UNDOCK_ENDPOINT_RESOURCES", "UNDOCK_TRANSPORT_RESOURCES"])

    def test_liquid_fuel_moved_station_to_transport_a_pickup(self):
        self.assertAlmostEqual(self.delta("TRANSPORT", "LiquidFuel"), 15.772, places=2)
        self.assertAlmostEqual(self.delta("ENDPOINT", "LiquidFuel"), -15.772, places=2)

    def test_no_monopropellant_moved(self):
        for side in ("TRANSPORT", "ENDPOINT"):
            with self.subTest(side=side):
                self.assertLess(abs(self.delta(side, "MonoPropellant")), 0.01)

    def test_the_station_docked_full_so_no_delivery_could_move_anything(self):
        for resource in ("LiquidFuel", "Oxidizer", "MonoPropellant"):
            with self.subTest(resource=resource):
                amount, capacity = self.m["DOCK_ENDPOINT_RESOURCES"][resource]
                self.assertEqual(amount, capacity)


if __name__ == "__main__":
    unittest.main()
