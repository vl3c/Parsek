"""Cells for resourceq: the snapshot sidecar decode, the route-window conservation check,
and the RC-1 spec's pinned tank numbers, each derived from the bdock-recorded fixture.

Runnable with the stdlib runner only::

    python -m unittest discover -s harness/lib
"""

import os
import re
import struct
import unittest
import zlib

try:
    import tomllib
except ImportError:  # pragma: no cover - python < 3.11
    tomllib = None

import resourceq as q

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS = os.path.dirname(HERE)
FIXTURE = os.path.join(HARNESS, "fixtures", "saves", "bdock-recorded")
SFS = os.path.join(FIXTURE, "persistent.sfs")
RECORDINGS = os.path.join(FIXTURE, "Parsek", "Recordings")
RC1_SPEC = os.path.join(HARNESS, "scenarios", "RC-1-docked-transfer-rewind-tanks.toml")

STATION_TIP = "37d0dc074351408ba0374230793abb1c"
INTERCEPTOR_TIP = "4af6cfd725d646ccbac9ef2f7749667e"
RC1_RESOURCES = ("LiquidFuel", "Oxidizer")


def _read_sfs():
    with open(SFS, encoding="utf-8") as fh:
        return fh.read()


def _tip(rec_id):
    return q.read_snapshot_file(os.path.join(RECORDINGS, rec_id + "_vessel.craft"))


class SnapshotDecodeTests(unittest.TestCase):
    TEXT = ("PART\n{\n\tname = a\n\tRESOURCE\n\t{\n\t\tname = LiquidFuel\n\t\tamount = 10.5\n"
            "\t\tmaxAmount = 20\n\t}\n\tMODULE\n\t{\n\t\tRESOURCE\n\t\t{\n\t\t\tname = Decoy\n"
            "\t\t\tamount = 99\n\t\t\tmaxAmount = 99\n\t\t}\n\t}\n}\nPART\n{\n\tRESOURCE\n\t{\n"
            "\t\tname = LiquidFuel\n\t\tamount = 1.5\n\t\tmaxAmount = 5\n\t}\n}\n")

    def test_text_sums_part_resources_only(self):
        parts, totals = q.snapshot_totals(self.TEXT)
        self.assertEqual(2, parts)
        self.assertEqual({"LiquidFuel": (12.0, 25.0)}, totals)

    def test_the_binary_layout_round_trips(self):
        payload = self.TEXT.encode("utf-8")
        comp = zlib.compressobj(9, zlib.DEFLATED, -15)
        body = comp.compress(payload) + comp.flush()
        header = q.SNAPSHOT_MAGIC + struct.pack("<iiBiiI", 0, 4, 1, len(payload), len(body), 0)
        self.assertEqual(q.SNAPSHOT_HEADER_BYTES, len(header))
        self.assertEqual(self.TEXT, q.decode_snapshot_sidecar(header + body))
        with self.assertRaises(ValueError):
            q.decode_snapshot_sidecar(header[:12] + b"\x02" + header[13:] + body)

    def test_the_header_size_matches_the_c_sharp_codec(self):
        path = os.path.join(os.path.dirname(HARNESS), "Source", "Parsek", "SnapshotSidecarCodec.cs")
        with open(path, encoding="utf-8-sig") as fh:
            text = fh.read()
        self.assertIn('Encoding.ASCII.GetBytes("PSN0")', text)
        self.assertIn("HeaderByteCount = 4 + sizeof(int) + sizeof(int) + sizeof(byte) + "
                      "sizeof(int) + sizeof(int) + sizeof(uint);", text)

    def test_the_fixture_tips_decode(self):
        for rec in (STATION_TIP, INTERCEPTOR_TIP):
            with self.subTest(rec=rec):
                parts, totals = _tip(rec)
                self.assertEqual(28, parts)
                self.assertIn("LiquidFuel", totals)


class WindowConservationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.windows = q.read_route_windows(_read_sfs())

    def test_the_fixture_holds_one_dock_window(self):
        self.assertEqual(1, len(self.windows))
        w = self.windows[0]
        self.assertEqual("3620499050", w["transferTargetPid"])
        for block in q.WINDOW_BLOCKS:
            self.assertIn(block, w)

    def test_what_the_station_gave_is_what_the_interceptor_got(self):
        rows = {r["resource"]: r for r in q.window_conservation(self.windows[0])}
        self.assertAlmostEqual(15.772, rows["LiquidFuel"]["transportDelta"], places=3)
        self.assertAlmostEqual(-15.772, rows["LiquidFuel"]["endpointDelta"], places=3)
        for name, row in rows.items():
            with self.subTest(resource=name):
                self.assertTrue(row["conserved"], row)
        # RCS held attitude while docked: a few thousandths of MP burned, inside the tolerance.
        self.assertLess(abs(rows["MonoPropellant"]["imbalance"]), 0.01)
        self.assertNotIn("ElectricCharge", rows)

    def test_fuel_that_appears_reds(self):
        w = {b: dict(m) for b, m in self.windows[0].items() if b in q.WINDOW_BLOCKS}
        amount, cap = w["UNDOCK_TRANSPORT_RESOURCES"]["LiquidFuel"]
        w["UNDOCK_TRANSPORT_RESOURCES"]["LiquidFuel"] = (amount + 40.0, cap)
        rows = {r["resource"]: r for r in q.window_conservation(w)}
        self.assertFalse(rows["LiquidFuel"]["conserved"])
        self.assertAlmostEqual(40.0, rows["LiquidFuel"]["imbalance"], places=6)

    def test_the_undock_tips_are_the_undock_manifests(self):
        # The recordings the undock split produced end holding what the window saw at undock.
        w = self.windows[0]
        for rec, block in ((STATION_TIP, "UNDOCK_ENDPOINT_RESOURCES"),
                           (INTERCEPTOR_TIP, "UNDOCK_TRANSPORT_RESOURCES")):
            _, totals = _tip(rec)
            for name in RC1_RESOURCES:
                with self.subTest(rec=rec, resource=name):
                    self.assertAlmostEqual(w[block][name][0], totals[name][0], places=6)


@unittest.skipIf(tomllib is None, "tomllib needs python 3.11")
class Rc1SpecPinsTests(unittest.TestCase):
    """RC-1's numbers come from the fixture, never from a hand: the summed expectation is
    the pair's total at DOCK time (conservation), and each per-vessel pin is that tip's end
    snapshot."""

    @classmethod
    def setUpClass(cls):
        with open(RC1_SPEC, "rb") as fh:
            cls.spec = tomllib.load(fh)
        cls.window = q.read_route_windows(_read_sfs())[0]
        cls.reads = [s for s in cls.spec["driver"]["steps"]
                     if s.get("cmd") == "ReadVesselResources"]
        cls.required = cls.spec["expectations"]["logContracts"]["required"]

    def test_every_read_names_both_tips_and_the_dock_time_total(self):
        dock_total = q.manifest_sum(self.window["DOCK_TRANSPORT_RESOURCES"],
                                    self.window["DOCK_ENDPOINT_RESOURCES"])
        expect = q.format_expect(dock_total, RC1_RESOURCES)
        self.assertEqual(3, len(self.reads))
        for step in self.reads:
            with self.subTest(label=step.get("label")):
                args = step["args"]
                self.assertEqual("%s,%s" % (STATION_TIP, INTERCEPTOR_TIP), args["rec"])
                self.assertEqual(",".join(RC1_RESOURCES), args["resources"])
                self.assertEqual(expect, args["expect"])
                self.assertEqual("true", args["expectSnapshot"])

    def test_the_undock_total_equals_the_dock_total(self):
        dock = q.manifest_sum(self.window["DOCK_TRANSPORT_RESOURCES"],
                              self.window["DOCK_ENDPOINT_RESOURCES"])
        undock = q.manifest_sum(self.window["UNDOCK_TRANSPORT_RESOURCES"],
                                self.window["UNDOCK_ENDPOINT_RESOURCES"])
        self.assertEqual(q.format_expect(dock, RC1_RESOURCES), q.format_expect(undock, RC1_RESOURCES))

    def test_the_per_vessel_and_total_lines_are_the_snapshots(self):
        tips = {0: _tip(STATION_TIP), 1: _tip(INTERCEPTOR_TIP)}
        for index, (parts, totals) in tips.items():
            rec = STATION_TIP if index == 0 else INTERCEPTOR_TIP
            tail = "".join(" %s=%.4f/%.4f" % (n, totals[n][0], totals[n][1]) for n in RC1_RESOURCES)
            line = ("readvesselresources vessel index=%d rec=%s found=true pid=1 name='Kerbal X' "
                    "loaded=true parts=%d%s" % (index, rec, parts, tail))
            with self.subTest(index=index):
                self.assertTrue(any(re.search(p, line) for p in self.required), line)
        both = q.manifest_sum(tips[0][1], tips[1][1])
        total = "readvesselresources total vessels=2" + "".join(
            " %s=%.4f/%.4f" % (n, both[n][0], both[n][1]) for n in RC1_RESOURCES)
        self.assertTrue(any(re.search(p, total) for p in self.required), total)

    def test_each_read_is_pinned_by_its_own_step_id(self):
        import hlib
        steps = self.spec["driver"]["steps"]
        for i, step in enumerate(steps):
            if step.get("cmd") != "ReadVesselResources":
                continue
            token = "exec id=%s cmd=ReadVesselResources start" % hlib.step_id_for_index(i)
            with self.subTest(label=step.get("label")):
                self.assertTrue(any(token in p for p in self.required), token)


if __name__ == "__main__":
    unittest.main()
