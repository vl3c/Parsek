#!/usr/bin/env python3
"""Unit tests for the map mirror (`harness/tools/map_mirror.py`).

The page exists so the owner can SEE whether the map's trajectory lines are
right, so the cells that matter are the ones about not lying: the geometry math
puts a point where the numbers say, the measured table reads the values a hand
calculation gives on the sample dump, a malformed or wrong-schema dump is named
on the page instead of drawing a plausible picture, and the page is a single
self-contained file that fits its budget.

The sample dumps under `testdata/` were built by hand with these deliberate,
hand-checkable features (all metres):

  map_mirror_sample.map.json (a Kerbin -> Mun loop, Kerbin radius 600 000,
  Mun 200 000, Minmus 60 000):
    Mun Shuttle line #0  ascent polyline about Kerbin, radii 600 070 .. 679 000
                         -> lowest 70, highest 79 000; its reference path is the
                         same vertices lifted 1 500 along up (out of the drawn
                         plane) -> max distance 1 500.
    line #0 -> #1        the ascent ends at r 679 000 on the parking orbit's own
                         start direction (r 680 000) -> gap 1 000.
    line #1              circular parking orbit r 680 000 -> 80 000 both ways;
                         conic Pe = Ap = 80 000.
    line #1 -> #2        the transfer starts exactly on the parking orbit's
                         start point -> gap 0.
    line #2              transfer ellipse rp 680 000, ra 12 600 000 -> conic Pe
                         80 000, Ap 12 000 000.
    line #2 -> #3        Kerbin-relative end = (Mun - Kerbin) + s + (0, 3000, 0),
                         Mun-relative start = s -> gap 3 000 across bodies.
    line #3              Mun capture hyperbola, rp 230 000, e 1.4 -> conic Pe
                         30 000, no Ap (open).
    line #3 -> #4        ends on the Mun orbit's start point -> gap 0.
    line #4              circular Mun orbit r 230 000 -> 30 000 both ways.
    icon                 r 230 250 along a vertex direction of line #4 -> 250.
  Minmus Tanker:
    line #0              orbit r 80 000 tilted 30 deg -> 20 000 both ways.
    line #1              landing tail from r 80 000 at 10 deg down to r 60 000
                         -> lowest 0, highest 20 000.
    line #0 -> #1        ring start (0 deg) to tail start (10 deg) on r 80 000 ->
                         chord 2 * 80 000 * sin(5 deg) = 13 944.4.
    icon                 on tail vertex 4 -> 0, line #1.
  route leg              starts r 700 000 (-> lowest 100 000), ends 2 000 000
                         short of Minmus' centre (|Minmus| = 47 000 000) ->
                         highest 44 400 000.

  map_mirror_minimal.map.json: required fields only.

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
        self.assertEqual(dump["counts"]["held"], {"ghosts": 2, "lines": 7, "points": 339})
        self.assertEqual(mm.closeup_bodies(dump), ["Kerbin", "Mun", "Minmus"])

    def test_the_minimal_dump_reads_with_every_optional_field_absent(self):
        dump, notes = mm.load_dump(MINIMAL)
        self.assertEqual(notes, [])
        g = dump["ghosts"][0]
        self.assertIsNone(g["icon"]["pos"])
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


class SampleMeasurementTests(unittest.TestCase):
    """The hand-computed numbers in the module docstring."""

    @classmethod
    def setUpClass(cls):
        cls.dump, _ = mm.load_dump(SAMPLE)
        cls.bodies = {b["name"]: b for b in cls.dump["bodies"]}
        cls.g1 = mm.measure_ghost(cls.dump["ghosts"][0], cls.bodies)
        cls.g2 = mm.measure_ghost(cls.dump["ghosts"][1], cls.bodies)

    def test_icon_distances(self):
        close(self, self.g1["icon"]["m"], 250.0)
        self.assertEqual(self.g1["icon"]["line"], 4)
        close(self, self.g2["icon"]["m"], 0.0)
        self.assertEqual(self.g2["icon"]["line"], 1)

    def test_altitude_ranges(self):
        rows = self.g1["lines"]
        close(self, rows[0]["lowest"]["m"], 70.0)
        close(self, rows[0]["highest"]["m"], 79000.0)
        for i, alt in ((1, 80000.0), (4, 30000.0)):
            close(self, rows[i]["lowest"]["m"], alt)
            close(self, rows[i]["highest"]["m"], alt)
        close(self, self.g2["lines"][0]["lowest"]["m"], 20000.0)
        close(self, self.g2["lines"][0]["highest"]["m"], 20000.0)
        close(self, self.g2["lines"][1]["lowest"]["m"], 0.0)
        close(self, self.g2["lines"][1]["highest"]["m"], 20000.0)

    def test_gaps_between_consecutive_lines(self):
        rows = self.g1["lines"]
        self.assertTrue(self.g1["timeOrdered"])
        self.assertEqual(self.g1["order"], [0, 1, 2, 3, 4])
        close(self, rows[0]["gapToNext"]["m"], 1000.0)
        close(self, rows[1]["gapToNext"]["m"], 0.0)
        close(self, rows[2]["gapToNext"]["m"], 3000.0)
        self.assertTrue(rows[2]["gapToNext"]["crossBody"])
        close(self, rows[3]["gapToNext"]["m"], 0.0)
        self.assertIsNone(rows[4]["gapToNext"])
        close(self, self.g2["lines"][0]["gapToNext"]["m"],
              2 * 80000.0 * math.sin(math.radians(5.0)))

    def test_reference_deviation_and_conic_apsides(self):
        rows = self.g1["lines"]
        close(self, rows[0]["reference"]["m"], 1500.0)
        self.assertEqual(rows[0]["referenceKind"], "recorded")
        self.assertIsNone(rows[1]["reference"])
        close(self, rows[1]["conicPe"]["m"], 80000.0)
        close(self, rows[1]["conicAp"]["m"], 80000.0)
        close(self, rows[2]["conicPe"]["m"], 80000.0)
        close(self, rows[2]["conicAp"]["m"], 12000000.0)
        close(self, rows[3]["conicPe"]["m"], 30000.0)
        self.assertIsNone(rows[3]["conicAp"]["m"])

    def test_route_leg(self):
        m = mm.measure_route(self.dump["routeLines"][0], self.bodies)
        close(self, m["legs"][0]["lowest"]["m"], 100000.0)
        close(self, m["legs"][0]["highest"]["m"], 44400000.0, tol=2.0)

    def test_gap_order_falls_back_to_dump_order(self):
        lines = [{"ut": [5.0, 6.0]}, {"ut": None}, {"ut": [1.0, 2.0]}]
        self.assertEqual(mm.line_order(lines), [0, 1, 2])
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
        self.assertEqual((row["ghosts"], row["lines"], row["points"]), (2, 7, 339))
        self.assertEqual((row["routeLines"], row["routeLegs"]), (1, 1))
        self.assertFalse(row["screenshot"])
        self.assertEqual(index["unreadableCount"], 1)
        self.assertEqual(index["unreadable"][0]["file"], "bad.map.json")
        self.assertIn("nope/1", index["unreadable"][0]["reason"])
        model = self.page_model(html)
        bad = [c for c in model["captures"] if c["error"]]
        self.assertEqual([c["label"] for c in bad], ["bad"])

    def test_a_capture_without_a_png_still_renders(self):
        _, html, _ = self.run_tool()
        model = self.page_model(html)
        cap = [c for c in model["captures"] if c["label"] == "map-mun-loop"][0]
        self.assertFalse(cap["hasPng"])
        self.assertIsNone(cap["photo"])
        self.assertEqual(len(cap["ghosts"]), 2)
        self.assertEqual(cap["closeups"], ["Kerbin", "Mun", "Minmus"])

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
    quiet about it; fail here instead, naming the key.

    PENDING: the writer lands in a separate change. Until the file exists these
    cells SKIP with a reason; make them strict (drop the skip) once both halves
    are merged."""

    WRITER = os.path.join(REPO_ROOT, "Source", "Parsek", "MapGeometryJson.cs")

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
        ("drawn", "the icon marker style"), ("lines", "every line"),
        ("kind", "line stroke styles"), ("active", "faint inactive lines"),
        ("points", "every line"), ("conic", "the conic Pe / Ap column"),
        ("sma", "the conic Pe / Ap column"), ("ecc", "the conic Pe / Ap column"),
        ("incDeg", "the line hover"), ("lanDeg", "validate_dump"),
        ("argPeDeg", "validate_dump"), ("meanAnomalyAtEpoch", "validate_dump"),
        ("epoch", "validate_dump"), ("reference", "the dashed reference path"),
        ("referenceKind", "the reference column"), ("routeLines", "route legs"),
        ("routeId", "the route table"), ("routeName", "the route table"),
        ("legs", "route legs"), ("counts", "the count cross-check"),
        ("truncated", "the TRUNCATED note"),
    ]

    def _src(self):
        if not os.path.isfile(self.WRITER):
            self.skipTest("PENDING: %s is absent in this checkout - the C# writer has not "
                          "landed, so the key sync is unchecked" % os.path.relpath(
                              self.WRITER, REPO_ROOT))
        with open(self.WRITER, "r", encoding="utf-8", errors="replace") as fh:
            return fh.read()

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

    def test_the_harvest_collects_map_dumps(self):
        import hlib  # noqa: E402
        if mm.DUMP_SUFFIX not in hlib.ARTIFACT_SHOTS_SUFFIXES:
            self.skipTest("PENDING: hlib.ARTIFACT_SHOTS_SUFFIXES does not list %r yet, so "
                          "run.py does not copy map dumps into a shots dir" % mm.DUMP_SUFFIX)
        self.assertIn(mm.DUMP_SUFFIX, hlib.ARTIFACT_SHOTS_SUFFIXES)


if __name__ == "__main__":
    unittest.main()
