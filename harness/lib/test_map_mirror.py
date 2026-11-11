#!/usr/bin/env python3
"""Unit tests for the map mirror (`harness/tools/map_mirror.py`).

The page exists so the owner can SEE whether the map's trajectory lines are
right, so the cells that matter are the ones about not lying: the geometry math
puts a point where the numbers say, the measured table reads the values a hand
calculation gives on the sample dump, a malformed or wrong-schema dump is named
on the page instead of drawing a plausible picture, and the page is a single
self-contained file that fits its budget.

The sample dumps under `testdata/` were built by hand in the shape the C#
writer (`Source/Parsek/MapGeometryJson.cs`) emits - open 64-point body rings,
`counts` over ghost lines AND route legs, `unknownBodyItems`, `icon.source`,
`markerDecision`, an inactive line with empty points, a pid-0 entry - with
these deliberate, hand-checkable features (all metres):

  map_mirror_sample.map.json (a Kerbin -> Mun loop, Kerbin radius 600 000,
  Mun 200 000, Minmus 60 000):
    Mun Shuttle (TracedPath, icon from the Parsek marker):
    line #0              ascent polyline about Kerbin, radii 600 070 .. 679 000
                         -> lowest 70, highest 79 000; its reference path is the
                         same vertices lifted 1 500 along up (out of the drawn
                         plane) -> max distance 1 500.
    line #0 -> #1        the ascent ends at r 679 000 on the parking orbit's own
                         start direction (r 680 000) -> gap 1 000.
    line #1              circular parking orbit polyline r 680 000 -> 80 000.
    line #1 -> #2        the transfer starts exactly on the parking orbit's
                         start point -> gap 0.
    line #2 -> #3        Kerbin-relative end = (Mun - Kerbin) + s + (0, 3000, 0),
                         Mun-relative start = s -> gap 3 000 across bodies.
    line #3 -> #4        the capture hyperbola ends on the Mun orbit's start
                         point -> gap 0.
    line #4              circular Mun orbit polyline r 230 000 -> 30 000.
    line #5              the stock orbit line the traced path hides: inactive,
                         no points, no time range, stock fade opacity 0; conic
                         r 230 000 -> Pe = Ap = 30 000. Listed ("not drawn (stock
                         fade)"), not drawn, not in the gap chain.
    icon                 r 230 250 along a vertex direction of line #4 -> 250.
  Minmus Tanker (StockConic, icon from the proto vessel):
    line #0              orbit r 80 000 tilted 30 deg -> 20 000 both ways; stock
                         drew it whole (`clipped` false) at fade opacity 0.6, so
                         it is drawn faded and fits its circular conic.
    line #1              seam bridge, two points on r 80 000: the ring's end
                         (0 deg) and 5 deg -> 20 000 both ways.
    line #0 -> #1        the bridge starts on the ring's end -> gap 0.
    line #2              landing tail from r 80 000 at 10 deg down to r 60 000
                         -> lowest 0, highest 20 000; its intended reference is
                         the tail moved 50 along the orbit plane's normal -> 50.
    line #1 -> #2        bridge end (5 deg) to tail start (10 deg) on r 80 000 ->
                         chord 2 * 80 000 * sin(2.5 deg) = 6 980.1.
    icon                 on tail vertex 4 -> 0, line #2.
  pid-0 entry            no vessel name, committedIndex -1, DriverOnly, one
                         polyline; icon on its vertex 2 -> 0.
  route leg              starts r 700 000 (-> lowest 100 000), ends 2 000 000
                         short of Minmus' centre (|Minmus| = 47 000 000) ->
                         highest 44 400 000.
  camera                 pitch 30, heading 40, and the forward / up the page's
                         pitch / heading model gives for them -> 0 deg apart.

  map_mirror_minimal.map.json: the always-written fields only.

Lives under `lib/` because CI discovers `lib/` only. Stdlib runner::

    cd harness && python -m unittest discover -s lib -q
"""

import contextlib
import io
import json
import math
import os
import random
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(HERE), "tools"))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import map_mirror as mm  # noqa: E402
from gui_mirror import write_png  # noqa: E402

TESTDATA = os.path.join(HERE, "testdata")
SAMPLE = os.path.join(TESTDATA, "map_mirror_sample.map.json")
MINIMAL = os.path.join(TESTDATA, "map_mirror_minimal.map.json")
REPO_ROOT = os.path.dirname(os.path.dirname(HERE))


def load_json(path):
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def close(tc, a, b, tol=0.01):
    tc.assertIsNotNone(a)
    tc.assertAlmostEqual(a, b, delta=tol)


class VectorAndProjectionTests(unittest.TestCase):

    def test_frame_axes_orthonormalises_a_slanted_right(self):
        r, u, f = mm.frame_axes((0, 2, 0), (1, 1, 0))
        for v in (r, u, f):
            close(self, mm.v_len(v), 1.0, 1e-12)
        close(self, mm.v_dot(r, u), 0.0, 1e-12)
        self.assertEqual(f, mm.v_cross(r, u))

    def test_parallel_axes_are_refused(self):
        with self.assertRaises(ValueError):
            mm.frame_axes((0, 1, 0), (0, 3, 0))

    def test_top_down_puts_right_to_the_right_and_looks_down(self):
        x, y, d = mm.basis_top_down((0, 1, 0), (1, 0, 0))
        self.assertEqual(mm.project((5, 7, 0), (x, y, d)), (5, 0))
        self.assertEqual(mm.project((0, 7, 3), (x, y, d)), (0, 3))
        self.assertEqual(tuple(d), (0, -1, 0))

    def test_a_prograde_orbit_draws_counter_clockwise_top_down(self):
        basis = mm.basis_top_down((0, 1, 0), (1, 0, 0))
        # In Unity's world (y up) a prograde orbit runs from +x towards +z.
        a = mm.project((1, 0, 0), basis)
        b = mm.project((math.cos(0.1), 0, math.sin(0.1)), basis)
        self.assertGreater(a[0] * b[1] - a[1] * b[0], 0)

    def test_camera_at_pitch_90_is_the_top_down_view(self):
        top = mm.basis_top_down((0, 1, 0), (1, 0, 0))
        cam = mm.basis_camera((0, 1, 0), (1, 0, 0), 90.0, 0.0)
        for a, b in zip(top, cam):
            for i in range(3):
                close(self, a[i], b[i], 1e-12)

    def test_the_recorded_camera_frame_looking_down_is_the_top_down_view(self):
        # Unity: right = up x forward; looking down -U with F on top gives R on the right.
        top = mm.basis_top_down((0, 1, 0), (1, 0, 0))
        _r, _u, f = mm.frame_axes((0, 1, 0), (1, 0, 0))
        cam = mm.basis_from_camera((0, -1, 0), f)
        for a, b in zip(top, cam):
            for i in range(3):
                close(self, a[i], b[i], 1e-12)
        with self.assertRaises(ValueError):
            mm.basis_from_camera((0, 0, 1), (0, 0, 2))

    def test_the_recorded_frame_agrees_with_the_pitch_heading_model(self):
        cam = {"pitchDeg": 35.0, "headingDeg": -120.0}
        _x, y, d = mm.basis_camera((0, 1, 0), (1, 0, 0), 35.0, -120.0)
        cam.update(forward=d, up=y)
        check = mm.camera_model_check({"up": (0, 1, 0), "right": (1, 0, 0)}, cam)
        close(self, check["forwardDeg"], 0.0, 1e-5)
        close(self, check["upDeg"], 0.0, 1e-5)
        cam["forward"] = (0, -1, 0)
        close(self, mm.camera_model_check({"up": (0, 1, 0), "right": (1, 0, 0)}, cam)
              ["forwardDeg"], mm.angle_deg(d, (0, -1, 0)), 1e-9)

    def test_an_open_ring_is_closed_once(self):
        ring = [(1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (-1.0, 0.0, 0.0), (0.0, 0.0, -1.0)]
        closed = mm.close_ring(ring)
        self.assertEqual(len(closed), 5)
        self.assertEqual(closed[-1], ring[0])
        self.assertEqual(mm.close_ring(closed), closed)
        self.assertEqual(mm.close_ring([]), [])

    def test_camera_at_pitch_0_is_the_edge_on_view(self):
        edge = mm.basis_edge_on((0, 1, 0), (1, 0, 0), 0.0)
        cam = mm.basis_camera((0, 1, 0), (1, 0, 0), 0.0, 0.0)
        for a, b in zip(edge, cam):
            for i in range(3):
                close(self, a[i], b[i], 1e-12)

    def test_edge_on_azimuth_turns_about_up(self):
        x, y, _d = mm.basis_edge_on((0, 1, 0), (1, 0, 0), 90.0)
        close(self, x[2], 1.0, 1e-12)
        self.assertEqual(tuple(y), (0.0, 1.0, 0.0))


class BodyFrameTests(unittest.TestCase):
    BODIES = {"Sun": {"pos": (0.0, 0.0, 0.0), "parent": None},
              "Kerbin": {"pos": (100.0, 0.0, 0.0), "parent": "Sun"},
              "Mun": {"pos": (110.0, 0.0, 5.0), "parent": "Kerbin"}}

    def test_body_offset_and_to_frame(self):
        self.assertEqual(mm.body_offset(self.BODIES, "Mun", "Kerbin"), (10.0, 0.0, 5.0))
        self.assertEqual(mm.to_frame(self.BODIES, "Mun", (1, 2, 3), "Kerbin"), (11.0, 2.0, 8.0))
        self.assertEqual(mm.to_frame(self.BODIES, "Mun", (1, 2, 3), "Mun"), (1, 2, 3))

    def test_is_descendant(self):
        self.assertTrue(mm.is_descendant(self.BODIES, "Mun", "Sun"))
        self.assertTrue(mm.is_descendant(self.BODIES, "Mun", "Mun"))
        self.assertFalse(mm.is_descendant(self.BODIES, "Kerbin", "Mun"))


class MeasureMathTests(unittest.TestCase):

    def test_point_segment_distance_perpendicular_and_clamped(self):
        close(self, mm.point_segment_distance((5, 3, 0), (0, 0, 0), (10, 0, 0)), 3.0)
        close(self, mm.point_segment_distance((-4, 3, 0), (0, 0, 0), (10, 0, 0)), 5.0)
        close(self, mm.point_segment_distance((1, 1, 1), (0, 0, 0), (0, 0, 0)), math.sqrt(3))

    def test_point_polyline_distance(self):
        pts = [(0, 0, 0), (10, 0, 0), (10, 10, 0)]
        d, i = mm.point_polyline_distance((12, 6, 0), pts)
        close(self, d, 2.0)
        self.assertEqual(i, 1)
        self.assertEqual(mm.point_polyline_distance((1, 0, 0), []), (None, None))
        self.assertEqual(mm.point_polyline_distance((3, 4, 0), [(0, 0, 0)]), (5.0, 0))

    def test_max_deviation_is_one_way_from_the_drawn_points(self):
        drawn = [(0, 0, 0), (10, 0, 0)]
        ref = [(0, 2, 0), (10, 2, 0), (10, 50, 0)]
        close(self, mm.max_deviation(drawn, ref), 2.0)
        self.assertIsNone(mm.max_deviation(drawn, []))

    def test_altitude_range(self):
        self.assertEqual(mm.altitude_range([(110, 0, 0), (0, 0, 130)], 100), (10, 30))
        self.assertEqual(mm.altitude_range([], 100), (None, None))

    def test_conic_apsides(self):
        self.assertEqual(mm.conic_apsides({"sma": 1000.0, "ecc": 0.5}, 100), (400.0, 1400.0))
        pe, ap = mm.conic_apsides({"sma": -500.0, "ecc": 1.4}, 100)
        close(self, pe, 100.0)
        self.assertIsNone(ap)
        self.assertEqual(mm.conic_apsides(None, 100), (None, None))

    def test_format_distance(self):
        cases = [(None, "-"), (0, "0 m"), (0.004, "0.004 m"), (0.42, "0.42 m"),
                 (250, "250 m"), (999.9999, "1.00 km"), (1500, "1.50 km"),
                 (13944.4, "13.9 km"), (3.0e6, "3.00 Mm"), (1.36e10, "13.6 Gm"),
                 (-70, "-70.0 m")]
        for value, text in cases:
            self.assertEqual(mm.format_distance(value), text, value)

    def test_format_ut_uses_the_kerbin_calendar(self):
        self.assertEqual(mm.format_ut(0), "Y1 D001 00:00:00")
        self.assertEqual(mm.format_ut(21600 + 3723), "Y1 D002 01:02:03")
        self.assertEqual(mm.format_ut(426 * 21600), "Y2 D001 00:00:00")
        self.assertEqual(mm.format_ut(None), "-")


class DumpReadingTests(unittest.TestCase):

    def test_the_sample_reads_clean(self):
        dump, notes = mm.load_dump(SAMPLE)
        self.assertEqual(notes, [])
        # 10 ghost lines + 1 route leg; 347 ghost points + 51 leg points.
        self.assertEqual(dump["counts"]["held"], {"ghosts": 3, "lines": 11, "points": 398})
        self.assertEqual([ln["kind"] for ln in dump["ghosts"][1]["lines"]],
                         ["orbit", "bridge", "tail"])
        self.assertEqual(mm.closeup_bodies(dump), ["Kerbin", "Mun", "Minmus"])
        g1, g2, g3 = dump["ghosts"]
        self.assertEqual((g1["icon"]["source"], g2["icon"]["source"]), ("marker", "proto"))
        self.assertEqual(g1["markerDecision"], {"draw": True, "directorTracedPath": True,
                                                "polylineOwning": True, "iconSuppressed": True})
        self.assertEqual((g3["pid"], g3["committedIndex"], g3["vesselName"]),
                         (0, -1, "rec-escape-arc"))
        self.assertIsNone(g3["markerDecision"])
        self.assertEqual(g3["treatment"], "DriverOnly")
        self.assertIn("pid-0", g3["treatmentText"])
        self.assertEqual(dump["counts"]["declared"]["unknownBodyItems"], 0)
        # The writer's rings are open; the page closes them when it draws.
        kerbin = [b for b in dump["bodies"] if b["name"] == "Kerbin"][0]
        self.assertEqual(len(kerbin["orbit"]), 64)
        self.assertNotEqual(kerbin["orbit"][0], kerbin["orbit"][-1])

    def test_the_minimal_dump_reads_with_every_optional_field_absent(self):
        dump, notes = mm.load_dump(MINIMAL)
        self.assertEqual(notes, [])
        g = dump["ghosts"][0]
        self.assertIsNone(g["icon"]["pos"])
        self.assertEqual(g["icon"]["source"], "")
        self.assertIsNone(g["markerDecision"])
        self.assertIsNone(g["sampleUT"])
        self.assertIsNone(g["lines"][0]["ut"])
        self.assertIsNone(g["lines"][0]["conic"])
        self.assertIsNone(dump["bodies"][0]["soi"])
        self.assertEqual(mm.closeup_bodies(dump), ["Kerbin"])
        m = mm.measure_ghost(g, {b["name"]: b for b in dump["bodies"]})
        close(self, m["lines"][0]["lowest"]["m"], 100.0)
        close(self, m["lines"][0]["highest"]["m"],
              math.sqrt(605000.0 ** 2 + 2000.0 ** 2) - 600000.0)
        self.assertIsNone(m["icon"])
        self.assertFalse(m["timeOrdered"])

    def test_a_wrong_schema_is_named(self):
        obj = load_json(MINIMAL)
        obj["schema"] = "parsek-map-geometry/2"
        with self.assertRaises(mm.DumpError) as cm:
            mm.validate_dump(obj)
        self.assertIn("parsek-map-geometry/2", str(cm.exception))
        self.assertIn(mm.SCHEMA_ID, str(cm.exception))

    def test_a_malformed_vector_names_its_path(self):
        obj = load_json(MINIMAL)
        obj["ghosts"][0]["lines"][0]["points"][1] = [1.0, "x", 2.0]
        with self.assertRaises(mm.DumpError) as cm:
            mm.validate_dump(obj)
        self.assertIn("ghosts[0].lines[0].points[1][1]", str(cm.exception))

    def test_missing_bodies_and_ghosts_are_refused(self):
        for key in ("bodies", "ghosts"):
            obj = load_json(MINIMAL)
            del obj[key]
            with self.assertRaises(mm.DumpError) as cm:
                mm.validate_dump(obj)
            self.assertIn(key, str(cm.exception))

    def test_non_json_is_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "x.map.json")
            with open(path, "w") as fh:
                fh.write("{not json")
            with self.assertRaises(mm.DumpError) as cm:
                mm.load_dump(path)
            self.assertIn("not JSON", str(cm.exception))

    def test_recoverable_oddities_become_notes(self):
        obj = load_json(MINIMAL)
        obj["ghosts"][0]["lines"].append(
            {"kind": "wiggle", "body": "Eve", "active": True, "points": [[1.0, 2.0, 3.0]]})
        obj["counts"]["truncated"] = True
        del obj["axes"]
        dump, notes = mm.validate_dump(obj)
        text = "\n".join(notes)
        self.assertIn("'Eve'", text)
        self.assertIn("'wiggle'", text)
        self.assertIn("counts.lines says 1, the file holds 2", text)
        self.assertIn("TRUNCATED", text)
        self.assertIn("no axes block", text)
        m = mm.measure_ghost(dump["ghosts"][0], {b["name"]: b for b in dump["bodies"]})
        self.assertIsNone(m["lines"][1]["lowest"]["m"])

    def test_writer_skips_and_drops_become_notes(self):
        obj = load_json(MINIMAL)
        obj["counts"]["unknownBodyItems"] = 2
        obj["counts"]["truncated"] = True
        obj["counts"]["dropped"] = {"ghosts": 0, "lines": 3, "routes": 0, "legs": 1,
                                    "bodies": 0}
        dump, notes = mm.validate_dump(obj)
        text = "\n".join(notes)
        self.assertIn("skipped 2 items", text)
        self.assertIn("lines 3, legs 1 not written", text)
        self.assertEqual(dump["counts"]["declared"]["dropped"]["lines"], 3)
        self.assertEqual(dump["counts"]["declared"]["unknownBodyItems"], 2)

    def test_route_legs_count_toward_lines_and_points(self):
        obj = load_json(SAMPLE)
        obj["counts"]["lines"] -= 1     # a reader that forgot the leg
        _, notes = mm.validate_dump(obj)
        self.assertEqual(notes, ["counts.lines says 10, the file holds 11"])

    def test_the_camera_frame_is_read_and_a_broken_one_falls_back(self):
        dump, _ = mm.load_dump(SAMPLE)
        cam = dump["camera"]
        self.assertEqual(cam["fovDeg"], 60.0)
        check = mm.camera_model_check(dump["axes"], cam)
        self.assertLess(check["forwardDeg"], 1e-3)
        self.assertLess(check["upDeg"], 1e-3)
        obj = load_json(SAMPLE)
        obj["camera"]["up"] = list(obj["camera"]["forward"])
        obj["camera"]["fovDeg"] = 0.0
        dump, notes = mm.validate_dump(obj)
        self.assertIsNone(dump["camera"]["forward"])
        self.assertIsNone(dump["camera"]["fovDeg"])
        self.assertTrue(any("falls back to pitch and heading" in n for n in notes))
        self.assertTrue(any("is not a field of view" in n for n in notes))
        self.assertIsNone(mm.camera_model_check(dump["axes"], dump["camera"]))
        obj = load_json(SAMPLE)
        del obj["camera"]["forward"], obj["camera"]["up"], obj["camera"]["fovDeg"]
        dump, notes = mm.validate_dump(obj)
        self.assertEqual(notes, [])
        self.assertIsNone(dump["camera"]["forward"])


class SampleMeasurementTests(unittest.TestCase):
    """The hand-computed numbers in the module docstring."""

    @classmethod
    def setUpClass(cls):
        cls.dump, _ = mm.load_dump(SAMPLE)
        cls.bodies = {b["name"]: b for b in cls.dump["bodies"]}
        cls.g1 = mm.measure_ghost(cls.dump["ghosts"][0], cls.bodies)
        cls.g2 = mm.measure_ghost(cls.dump["ghosts"][1], cls.bodies)
        cls.g3 = mm.measure_ghost(cls.dump["ghosts"][2], cls.bodies)

    def test_icon_distances(self):
        close(self, self.g1["icon"]["m"], 250.0)
        self.assertEqual(self.g1["icon"]["line"], 4)
        close(self, self.g2["icon"]["m"], 0.0)
        self.assertEqual(self.g2["icon"]["line"], 2)
        close(self, self.g3["icon"]["m"], 0.0)
        self.assertEqual(self.g3["icon"]["line"], 0)

    def test_altitude_ranges(self):
        rows = self.g1["lines"]
        close(self, rows[0]["lowest"]["m"], 70.0)
        close(self, rows[0]["highest"]["m"], 79000.0)
        for i, alt in ((1, 80000.0), (4, 30000.0)):
            close(self, rows[i]["lowest"]["m"], alt)
            close(self, rows[i]["highest"]["m"], alt)
        close(self, self.g2["lines"][0]["lowest"]["m"], 20000.0)
        close(self, self.g2["lines"][0]["highest"]["m"], 20000.0)
        close(self, self.g2["lines"][1]["lowest"]["m"], 20000.0)
        close(self, self.g2["lines"][1]["highest"]["m"], 20000.0)
        close(self, self.g2["lines"][2]["lowest"]["m"], 0.0)
        close(self, self.g2["lines"][2]["highest"]["m"], 20000.0)

    def test_gaps_between_consecutive_lines(self):
        rows = self.g1["lines"]
        self.assertTrue(self.g1["timeOrdered"])
        self.assertEqual(self.g1["order"], [0, 1, 2, 3, 4, 5])
        close(self, rows[0]["gapToNext"]["m"], 1000.0)
        close(self, rows[1]["gapToNext"]["m"], 0.0)
        close(self, rows[2]["gapToNext"]["m"], 3000.0)
        self.assertTrue(rows[2]["gapToNext"]["crossBody"])
        close(self, rows[3]["gapToNext"]["m"], 0.0)
        # line #5 is the inactive stock orbit line: not a link in the chain
        self.assertIsNone(rows[4]["gapToNext"])
        self.assertIsNone(rows[5]["gapToNext"])
        # the bridge is an ordinary link in the chain
        self.assertEqual(self.g2["order"], [0, 1, 2])
        close(self, self.g2["lines"][0]["gapToNext"]["m"], 0.0)
        self.assertEqual(self.g2["lines"][0]["gapToNext"]["to"], 1)
        close(self, self.g2["lines"][1]["gapToNext"]["m"],
              2 * 80000.0 * math.sin(math.radians(2.5)))

    def test_reference_deviation_and_conic_apsides(self):
        rows = self.g1["lines"]
        close(self, rows[0]["reference"]["m"], 1500.0)
        self.assertEqual(rows[0]["referenceKind"], "recorded")
        self.assertIsNone(rows[1]["reference"])
        self.assertIsNone(rows[1]["conicPe"])
        close(self, rows[5]["conicPe"]["m"], 30000.0)
        close(self, rows[5]["conicAp"]["m"], 30000.0)
        close(self, self.g2["lines"][0]["conicPe"]["m"], 20000.0)
        close(self, self.g2["lines"][2]["reference"]["m"], 50.0)
        self.assertEqual(self.g2["lines"][2]["referenceKind"], "intended")

    def test_an_inactive_line_is_listed_not_measured(self):
        row = self.g1["lines"][5]
        self.assertEqual((row["kind"], row["active"], row["drawn"], row["points"]),
                         ("orbit", False, False, 0))
        self.assertIsNone(row["lowest"]["m"])
        self.assertIsNone(row["reference"])
        self.assertTrue(all(r["drawn"] for r in self.g1["lines"][:5]))
        # An inactive line that still carries points (an older writer) is not
        # drawn, measured or chained either.
        ghost = json.loads(json.dumps(self.dump["ghosts"][1]))
        ghost["lines"][0]["active"] = False
        m = mm.measure_ghost(ghost, self.bodies)
        self.assertFalse(m["lines"][0]["drawn"])
        self.assertIsNone(m["lines"][0]["lowest"]["m"])
        self.assertIsNone(m["lines"][0]["gapToNext"])

    def test_opacity_and_the_faded_line(self):
        rows = self.g1["lines"]
        self.assertEqual(rows[5]["opacity"], 0.0)
        self.assertTrue(rows[5]["faded"])
        self.assertIsNone(rows[5]["conicFit"])
        self.assertEqual(self.g1["faded"], 1)
        self.assertEqual(self.dump["ghosts"][0]["lines"][5]["drawMode"], "REDRAW_AND_RECALCULATE")
        tanker = self.g2["lines"][0]
        self.assertEqual(tanker["opacity"], 0.6)
        self.assertFalse(tanker["faded"])
        self.assertTrue(tanker["drawn"])
        self.assertIsNone(self.g1["lines"][0]["opacity"])
        # A line an older writer marked active with points but that the dump names at
        # opacity 0 is not drawn, measured or chained.
        ghost = json.loads(json.dumps(self.dump["ghosts"][1]))
        ghost["lines"][0]["opacity"] = 0.0
        m = mm.measure_ghost(ghost, self.bodies)
        self.assertFalse(m["lines"][0]["drawn"])
        self.assertTrue(m["lines"][0]["faded"])
        self.assertIsNone(m["lines"][0]["gapToNext"])
        self.assertIsNone(m["lines"][0]["conicFit"])

    def test_a_proto_icon_fades_with_its_orbit_line(self):
        # Stock tints the proto icon with the orbit line's fade opacity, so an icon
        # whose only orbit line reads 0 is not on screen whatever `drawn` says.
        line = lambda kind, op: {"kind": kind, "opacity": op}
        self.assertTrue(mm.proto_icon_faded(True, "proto", [line("orbit", 0.0)]))
        self.assertTrue(mm.proto_icon_faded(
            True, "proto", [line("polyline", None), line("orbit", 0.0)]))
        self.assertFalse(mm.proto_icon_faded(True, "proto", [line("orbit", 0.4)]))
        # No opacity key (an older dump), no orbit line, a marker icon or an icon the
        # dump already calls not drawn: the dump's own reading stands.
        self.assertFalse(mm.proto_icon_faded(True, "proto", [line("orbit", None)]))
        self.assertFalse(mm.proto_icon_faded(True, "proto", [line("polyline", None)]))
        self.assertFalse(mm.proto_icon_faded(True, "marker", [line("orbit", 0.0)]))
        self.assertFalse(mm.proto_icon_faded(False, "proto", [line("orbit", 0.0)]))
        # Through the validator: the icon is reported not drawn, and flagged as faded.
        obj = load_json(SAMPLE)
        ghost = next(g for g in obj["ghosts"] if g["icon"].get("source") == "proto")
        ghost["icon"]["drawn"] = True
        ghost["lines"] = [{"kind": "orbit", "body": ghost["body"], "active": False,
                           "opacity": 0, "points": []}]
        dump, _ = mm.validate_dump(obj)
        icon = next(g for g in dump["ghosts"] if g["recordingId"] == ghost["recordingId"])["icon"]
        self.assertEqual((icon["drawn"], icon["faded"]), (False, True))
        untouched = [g["icon"] for g in mm.load_dump(SAMPLE)[0]["ghosts"]]
        self.assertFalse(any(i["faded"] for i in untouched))

    def test_an_opacity_outside_zero_to_one_is_a_note(self):
        obj = load_json(SAMPLE)
        obj["ghosts"][1]["lines"][0]["opacity"] = 1.5
        dump, notes = mm.validate_dump(obj)
        self.assertIsNone(dump["ghosts"][1]["lines"][0]["opacity"])
        self.assertTrue(any("opacity" in n and "outside 0..1" in n for n in notes), notes)

    def test_the_drawn_circle_fits_its_conic(self):
        fit = self.g2["lines"][0]["conicFit"]
        self.assertTrue(fit["fits"])
        close(self, fit["minR"], 80000.0)
        close(self, fit["maxR"], 80000.0)
        close(self, fit["rp"], 80000.0)
        close(self, fit["ra"], 80000.0)
        self.assertEqual(self.g2["offConic"], 0)
        # Only orbit lines with a conic are checked.
        self.assertIsNone(self.g2["lines"][1]["conicFit"])
        self.assertIsNone(self.g1["lines"][1]["conicFit"])

    def test_a_stale_offset_orbit_line_is_off_its_conic(self):
        # The first-flight defect: the vertices of an older frame, the right shape but
        # moved off the body centre.
        ghost = json.loads(json.dumps(self.dump["ghosts"][1]))
        ghost["lines"][0]["points"] = [[x + 50000.0, y, z] for x, y, z in ghost["lines"][0]["points"]]
        m = mm.measure_ghost(ghost, self.bodies)
        self.assertFalse(m["lines"][0]["conicFit"]["fits"])
        self.assertEqual(m["offConic"], 1)

    def test_conic_fit_rules(self):
        ell = {"sma": 1000.0, "ecc": 0.5}       # rp 500, ra 1500
        whole = [[500.0, 0, 0], [0, 1000.0, 0], [-1500.0, 0, 0]]
        self.assertTrue(mm.conic_fit(whole, ell, False)["fits"])
        self.assertTrue(mm.conic_fit(whole, ell, None)["fits"])
        # A whole ellipse that misses periapsis by 3 % is off.
        self.assertFalse(mm.conic_fit([[515.0, 0, 0], [-1500.0, 0, 0]], ell, False)["fits"])
        # A clipped arc need not reach either apsis, but must stay between them.
        arc = [[0, 700.0, 0], [0, 1200.0, 0]]
        self.assertFalse(mm.conic_fit(arc, ell, False)["fits"])
        self.assertTrue(mm.conic_fit(arc, ell, True)["fits"])
        self.assertFalse(mm.conic_fit([[0, 1600.0, 0]], ell, True)["fits"])
        self.assertFalse(mm.conic_fit([[0, 480.0, 0]], ell, True)["fits"])
        # An open orbit stock drew whole passes through periapsis.
        hyp = {"sma": -500.0, "ecc": 2.0}       # rp 500
        self.assertTrue(mm.conic_fit([[0, 492.0, 0], [0, 1e9, 0]], hyp, False)["fits"])
        self.assertFalse(mm.conic_fit([[0, 480.0, 0]], hyp, False)["fits"])
        self.assertIsNone(mm.conic_fit([[0, 1.0, 0]], hyp, False)["ra"])
        # The first-flight stale case: "drawn whole" but the vertices are an arc far
        # out (an earlier frame's clip, or an older position) -> off.
        self.assertFalse(mm.conic_fit([[0, 3.8e7, 0], [0, 3.9e7, 0]],
                                      {"sma": -510000.0, "ecc": 1.666}, False)["fits"])
        # A clipped open arc: not inside periapsis, not past the SOI when one is known.
        self.assertTrue(mm.conic_fit([[0, 700.0, 0], [0, 5000.0, 0]], hyp, True, soi=6000.0)["fits"])
        self.assertFalse(mm.conic_fit([[0, 700.0, 0], [0, 7000.0, 0]], hyp, True, soi=6000.0)["fits"])
        self.assertTrue(mm.conic_fit([[0, 700.0, 0], [0, 7000.0, 0]], hyp, True)["fits"])
        self.assertFalse(mm.conic_fit([[0, 480.0, 0]], hyp, True)["fits"])
        # Nothing to check.
        self.assertIsNone(mm.conic_fit([], ell, False))
        self.assertIsNone(mm.conic_fit(whole, None, False))
        self.assertIsNone(mm.conic_fit(whole, {"sma": 1000.0, "ecc": None}, False))

    def test_an_open_orbit_has_no_apoapsis(self):
        pe, ap = mm.conic_apsides({"sma": -575000.0, "ecc": 1.4}, 200000.0)
        close(self, pe, 30000.0)
        self.assertIsNone(ap)

    def test_route_leg(self):
        m = mm.measure_route(self.dump["routeLines"][0], self.bodies)
        close(self, m["legs"][0]["lowest"]["m"], 100000.0)
        close(self, m["legs"][0]["highest"]["m"], 44400000.0, tol=2.0)

    def test_line_order_is_timed_lines_first_then_the_rest_in_dump_order(self):
        # The writer's own order: ut-known lines sorted, then the others as captured.
        lines = [{"ut": [5.0, 6.0]}, {"ut": None}, {"ut": [1.0, 2.0]}, {"ut": None},
                 {"ut": [5.0, 5.0]}]
        self.assertEqual(mm.line_order(lines), [2, 4, 0, 1, 3])
        self.assertEqual(mm.line_order([{"ut": [5.0, 6.0]}, {"ut": [1.0, 2.0]}]), [1, 0])


class ShotsDirTests(unittest.TestCase):

    def test_run_and_lane(self):
        self.assertEqual(mm.run_and_lane("/r/2026-11-11_1200_MAP-1-mun-loop_shots"),
                         ("2026-11-11_1200_MAP-1-mun-loop", "MAP-1-mun-loop"))
        self.assertEqual(mm.run_and_lane("/r/2026-11-11_1200_MAP-1_run2_a2_shots/"),
                         ("2026-11-11_1200_MAP-1_run2_a2", "MAP-1"))
        self.assertEqual(mm.run_and_lane("/r/somewhere"), ("somewhere", "somewhere"))


def noise_png(path, w, h, seed=7):
    rnd = random.Random(seed)
    px = bytes(rnd.getrandbits(8) for _ in range(w * h * 3))
    with open(path, "wb") as fh:
        fh.write(write_png(w, h, 3, px))


class PageTests(unittest.TestCase):

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="mapmirror-")
        self.run1 = os.path.join(self.tmp, "2026-11-11_1200_MAP-1-mun-loop_shots")
        self.run2 = os.path.join(self.tmp, "2026-11-11_1210_MAP-2-ts-minimal_shots")
        os.makedirs(self.run1)
        os.makedirs(self.run2)
        shutil.copy(SAMPLE, os.path.join(self.run1, "map-mun-loop.map.json"))
        shutil.copy(MINIMAL, os.path.join(self.run2, "ts-minimal.map.json"))
        with open(os.path.join(self.run2, "bad.map.json"), "w") as fh:
            json.dump({"schema": "nope/1"}, fh)
        # Unrelated screenshots in a shots dir are left alone.
        noise_png(os.path.join(self.run2, "other-shot.png"), 4, 4)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_tool(self, *extra):
        out = os.path.join(self.tmp, "page.html")
        idx = os.path.join(self.tmp, "index.json")
        if os.path.exists(out):
            os.remove(out)
        self.stderr = io.StringIO()
        with contextlib.redirect_stderr(self.stderr):
            rc = mm.main(["--shots", self.run1, "--shots", self.run2, "--out", out,
                          "--index", idx, "--stamp", "2026-11-11T12:00:00Z"] + list(extra))
        html = None
        if os.path.exists(out):
            with open(out, encoding="utf-8") as fh:
                html = fh.read()
        index = load_json(idx) if rc == 0 else None
        return rc, html, index

    def page_model(self, html):
        m = re.search(r"window.__MAPMIRROR__=(.*?);</script>", html, re.S)
        return json.loads(m.group(1))

    def test_the_page_is_self_contained(self):
        rc, html, _ = self.run_tool()
        self.assertEqual(rc, 0)
        self.assertNotIn("http://", html)
        self.assertNotIn("https://", html)
        self.assertNotRegex(html, r"<(script|link|img)[^>]+(src|href)=\"(?!data:)")
        self.assertNotIn("@import", html)

    def test_deterministic_with_a_stamp(self):
        _, a, ia = self.run_tool()
        _, b, ib = self.run_tool()
        self.assertEqual(a, b)
        self.assertEqual(ia, ib)
        self.assertIn("2026-11-11T12:00:00Z", a)

    def test_index_lists_lanes_counts_and_the_unreadable_capture(self):
        _, html, index = self.run_tool()
        self.assertEqual(index["schema"], mm.INDEX_SCHEMA)
        self.assertEqual(index["captureCount"], 3)
        self.assertEqual([l["lane"] for l in index["lanes"]], ["MAP-1-mun-loop", "MAP-2-ts-minimal"])
        row = index["lanes"][0]["captures"][0]
        self.assertEqual((row["ghosts"], row["lines"], row["points"]), (3, 11, 398))
        self.assertEqual((row["routeLines"], row["routeLegs"]), (1, 1))
        self.assertFalse(row["screenshot"])
        self.assertEqual(index["unreadableCount"], 1)
        self.assertEqual(index["unreadable"][0]["file"], "bad.map.json")
        self.assertIn("nope/1", index["unreadable"][0]["reason"])
        model = self.page_model(html)
        bad = [c for c in model["captures"] if c["error"]]
        self.assertEqual([c["label"] for c in bad], ["bad"])

    def test_an_off_conic_line_is_counted_on_the_rail_and_in_the_index(self):
        obj = load_json(SAMPLE)
        ln = obj["ghosts"][1]["lines"][0]
        ln["points"] = [[x * 1.2, y * 1.2, z * 1.2] for x, y, z in ln["points"]]
        with open(os.path.join(self.run1, "map-mun-loop.map.json"), "w") as fh:
            json.dump(obj, fh)
        _, html, index = self.run_tool()
        cap = [c for c in self.page_model(html)["captures"] if c["label"] == "map-mun-loop"][0]
        self.assertEqual(cap["offConic"], 1)
        self.assertEqual(cap["faded"], 1)
        row = [r for r in index["lanes"][0]["captures"] if r["label"] == "map-mun-loop"][0]
        self.assertEqual((row["offConicLines"], row["fadedLines"]), (1, 1))
        self.assertIn("OFF ITS CONIC", mm.JS)
        self.assertIn("not drawn (stock fade)", mm.JS)

    def test_a_capture_without_a_png_still_renders(self):
        _, html, _ = self.run_tool()
        model = self.page_model(html)
        cap = [c for c in model["captures"] if c["label"] == "map-mun-loop"][0]
        self.assertFalse(cap["hasPng"])
        self.assertIsNone(cap["photo"])
        self.assertEqual(len(cap["ghosts"]), 3)
        self.assertEqual(cap["closeups"], ["Kerbin", "Mun", "Minmus"])
        self.assertLess(cap["cameraCheck"]["forwardDeg"], 1e-3)
        kerbin = [b for b in cap["bodies"] if b["name"] == "Kerbin"][0]
        self.assertEqual(len(kerbin["orbit"]), 65)
        self.assertEqual(kerbin["orbit"][0], kerbin["orbit"][-1])

    def test_a_png_is_inlined_and_no_photos_leaves_it_out(self):
        noise_png(os.path.join(self.run1, "map-mun-loop.png"), 32, 20)
        _, html, index = self.run_tool()
        cap = [c for c in self.page_model(html)["captures"] if c["label"] == "map-mun-loop"][0]
        self.assertTrue(cap["photo"]["src"].startswith("data:image/png;base64,"))
        self.assertEqual((cap["photo"]["w"], cap["photo"]["h"]), (32, 20))
        self.assertTrue(index["lanes"][0]["captures"][0]["screenshotInlined"])
        _, html, index = self.run_tool("--no-photos")
        cap = [c for c in self.page_model(html)["captures"] if c["label"] == "map-mun-loop"][0]
        self.assertEqual(cap["photo"]["src"], "")
        self.assertIn("--no-photos", cap["photo"]["note"])
        self.assertFalse(index["lanes"][0]["captures"][0]["screenshotInlined"])

    def test_an_oversized_photo_is_re_encoded_to_fit(self):
        noise_png(os.path.join(self.run1, "map-mun-loop.png"), 400, 300)
        raw = os.path.getsize(os.path.join(self.run1, "map-mun-loop.png"))
        rc, html, _ = self.run_tool("--budget-mb", "0.3")
        self.assertEqual(rc, 0)
        self.assertLessEqual(len(html.encode("utf-8")), int(0.3 * 1024 * 1024))
        cap = [c for c in self.page_model(html)["captures"] if c["label"] == "map-mun-loop"][0]
        self.assertGreater(raw * 4 // 3, int(0.3 * 1024 * 1024))
        self.assertIn("re-encoded", cap["photo"]["note"])

    def test_over_budget_is_refused_and_nothing_is_written(self):
        rc, html, _ = self.run_tool("--budget-mb", "0.01")
        self.assertEqual(rc, 2)
        self.assertIsNone(html)
        self.assertIn("REFUSED", self.stderr.getvalue())
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "index.json")))

    def test_dump_text_cannot_break_out_of_the_page(self):
        obj = load_json(MINIMAL)
        obj["ghosts"][0]["vesselName"] = "</script><script>alert(1)</script>"
        with open(os.path.join(self.run2, "ts-minimal.map.json"), "w") as fh:
            json.dump(obj, fh)
        _, html, _ = self.run_tool()
        self.assertNotIn("<script>alert(1)", html)
        self.assertEqual(html.count("</script>"), 2)

    def test_no_shots_directory_is_an_error(self):
        out = os.path.join(self.tmp, "x.html")
        with contextlib.redirect_stderr(io.StringIO()):
            rc = mm.main(["--shots", os.path.join(self.tmp, "nothing-*"), "--out", out])
        self.assertEqual(rc, 2)
        self.assertFalse(os.path.exists(out))

    def test_the_page_script_parses(self):
        node = shutil.which("node")
        if not node:
            self.skipTest("node is not on PATH; the page script's syntax is unchecked here")
        path = os.path.join(self.tmp, "page.js")
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(mm.JS)
        res = subprocess.run([node, "--check", path], capture_output=True, text=True)
        self.assertEqual(res.returncode, 0, res.stderr)


class CSharpWriterSourceSyncTests(unittest.TestCase):
    """SOURCE-SYNC GATE: reads OUTSIDE `harness/` on purpose (precedent:
    `test_gui_tree_view.CSharpWriterSourceSyncTests`). The tool indexes the dump
    by key name and every name is written by one C# writer,
    `Source/Parsek/MapGeometryJson.cs`. Rename a key there and the page would go
    quiet about it; fail here instead, naming the key. The treatment tokens the
    page explains come from the same file plus the Director's `Treatment` enum."""

    WRITER = os.path.join(REPO_ROOT, "Source", "Parsek", "MapGeometryJson.cs")
    TREATMENT_ENUM = os.path.join(REPO_ROOT, "Source", "Parsek", "MapRender", "RenderSegment.cs")

    # Every key map_mirror.py reads, and what goes quiet without it.
    KEYS = [
        ("schema", "validate_dump's schema check"), ("label", "the capture header"),
        ("scene", "the rail and header"), ("ut", "time order and the header"),
        ("frame", "the header"), ("warpRate", "the header"),
        ("camera", "the camera view"), ("target", "the camera view's centre"),
        ("pitchDeg", "the camera view's basis"), ("headingDeg", "the camera view's basis"),
        ("distance", "the camera view's zoom"), ("axes", "every projection"),
        ("up", "every projection"), ("right", "every projection"),
        ("root", "the whole-system frame"), ("bodies", "every body"),
        ("name", "every body"), ("parent", "orbit rings and close-ups"),
        ("radius", "discs, occlusion and altitudes"), ("soi", "the SOI circles"),
        ("pos", "every frame conversion"), ("orbit", "the orbit rings"),
        ("ghosts", "every ghost"), ("recordingId", "colours and the tables"),
        ("vesselName", "legend, hover and tables"), ("pid", "the tables"),
        ("committedIndex", "the tables"), ("sampleUT", "the tables"),
        ("loopCycle", "the tables"), ("treatment", "the tables"),
        ("body", "every line and icon frame"), ("icon", "the icon marker"),
        ("drawn", "the icon marker style"), ("source", "the ghost header's icon source"),
        ("markerDecision", "the ghost header"), ("draw", "the ghost header"),
        ("directorTracedPath", "the ghost header"), ("polylineOwning", "the ghost header"),
        ("iconSuppressed", "the ghost header"), ("lines", "every line"),
        ("kind", "line stroke styles"), ("active", "which lines are drawn"),
        ("points", "every line"), ("conic", "the conic Pe / Ap column"),
        ("sma", "the conic Pe / Ap column"), ("ecc", "the conic Pe / Ap column"),
        ("incDeg", "the line hover"), ("lanDeg", "validate_dump"),
        ("argPeDeg", "validate_dump"), ("meanAnomalyAtEpoch", "validate_dump"),
        ("epoch", "validate_dump"), ("reference", "the dashed reference path"),
        ("referenceKind", "the reference column"), ("routeLines", "route legs"),
        ("routeId", "the route table"), ("routeName", "the route table"),
        ("legs", "route legs"), ("counts", "the count cross-check"),
        ("truncated", "the TRUNCATED note"),
        ("unknownBodyItems", "the capture header and notes"),
        ("dropped", "the capture header and the TRUNCATED note"),
        ("forward", "the camera view's recorded frame"),
        ("fovDeg", "the camera view's zoom"),
        ("clipped", "the conic fit's whole-ellipse vs clipped-arc rule"),
        ("opacity", "the stock fade: which orbit lines were drawn, and how faded"),
        ("drawMode", "validate_dump (the stock draw mode an orbit line was drawn in)"),
    ]

    def _read(self, path):
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()

    def _src(self):
        return self._read(self.WRITER)

    def test_the_schema_id_matches(self):
        src = self._src()
        self.assertIn('SchemaId = "%s"' % mm.SCHEMA_ID, src)

    def test_every_key_the_tool_reads_is_still_written(self):
        src = self._src()
        for key, why in self.KEYS:
            # Either a plain literal ("key") or a JSON-escaped one (\"key\").
            self.assertTrue('"%s"' % key in src or '\\"%s\\"' % key in src,
                            "%s no longer writes '%s', which the map mirror reads for: %s"
                            % (os.path.basename(self.WRITER), key, why))

    def test_the_dropped_block_names_every_kind_the_page_shows(self):
        src = self._src()
        m = re.search(r'\\"dropped\\": \{(.*?)\}', src, re.S)
        self.assertIsNotNone(m, "the dropped block is not where this gate looks")
        block = src[m.start():m.start() + 800]
        for kind in ("ghosts", "lines", "routes", "legs", "bodies"):
            self.assertIn('\\"%s\\"' % kind, block, "dropped.%s is not written" % kind)

    def test_the_line_kinds_are_the_writer_kinds(self):
        # A kind the writer adds must get a stroke style here, and back.
        kinds = set(re.findall(r'const string Kind\w+ = "(\w+)"', self._src()))
        self.assertEqual(kinds, set(mm.LINE_KINDS),
                         "MapGeometryJson.cs line kinds %s, map_mirror.LINE_KINDS %s"
                         % (sorted(kinds), sorted(mm.LINE_KINDS)))
        for kind in mm.LINE_KINDS:
            self.assertIn("svg .k-%s{" % kind, mm.CSS, "no stroke style for kind %r" % kind)
            self.assertIn("'%s'" % kind, mm.JS, "the page script does not know kind %r" % kind)

    def test_every_treatment_token_is_explained(self):
        # The writer's own constants plus the Director's visible treatments.
        consts = set(re.findall(r'const string \w+Treatment = "(\w+)"', self._src()))
        enum = re.search(r"enum Treatment\s*\{(.*?)\}", self._read(self.TREATMENT_ENUM), re.S)
        self.assertIsNotNone(enum, "enum Treatment not found")
        body = re.sub(r"//[^\n]*", "", enum.group(1))
        members = set(re.findall(r"^\s*(\w+)\s*=", body, re.M))
        self.assertEqual(consts, {"NoIntent", "DriverOnly", "SuppressedMarker", "None"})
        self.assertTrue({"StockConic", "TracedPath"} <= members)
        self.assertEqual(set(mm.TREATMENTS), consts | members)

    def test_the_icon_sources_are_the_recorder_literals(self):
        rec = self._read(os.path.join(REPO_ROOT, "Source", "Parsek", "MapGeometryRecorder.cs"))
        for src in ("proto", "marker"):
            self.assertRegex(rec, r'Source = [^;,}]*"%s"' % src,
                             "the recorder no longer writes icon source %r" % src)

    def test_the_harvest_collects_map_dumps(self):
        import hlib  # noqa: E402
        self.assertIn(mm.DUMP_SUFFIX, hlib.ARTIFACT_SHOTS_SUFFIXES)
        self.assertEqual(mm.DUMP_SUFFIX, hlib.DUMPMAPGEOMETRY_SUFFIX)


if __name__ == "__main__":
    unittest.main()
