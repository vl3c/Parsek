#!/usr/bin/env python3
"""Unit tests for the GUI mirror fidelity instrument
(`harness/tools/gui_mirror_fidelity.py`).

The instrument's claim is that it measures the REAL mirror page - the file a
reader opens, rendered by a browser at a deep link that strips the chrome - and
reports honest numbers about how far that rendering is from the game frame. The
cells that matter are therefore:

  * every metric is a pure function of two decoded images and one capture, so a
    synthetic frame with a known offset produces exactly that offset;
  * the ink measurement is not fooled by a control's own border, which is the one
    mistake that turned a perfect control into a 471 px defect report;
  * the deep link's grammar here and the page's own `parseHash` agree, because a
    disagreement would silently photograph the wrong capture - or none;
  * bare mode is IN the generated page, and every rule it adds is scoped to the
    `bare` class, so a page opened without the hash is the page that shipped;
  * the browser is optional: there is no browser on CI, and every cell below
    passes without one.

No images are committed anywhere for this: the synthetic PNGs are built in a
temp directory with the stdlib encoder, and one cell fails if any tracked file
under `harness/` or `docs/` is an image or carries an inlined image payload.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network, NO
browser)::

    cd harness && python -m unittest discover -s lib -q
"""

import json
import os
import re
import shutil
import subprocess
import tempfile
import threading
import unittest

import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tools"))

import gui_mirror as gmi  # noqa: E402
import gui_mirror_fidelity as fid  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


# --------------------------------------------------------------------------
# synthetic images, built here, never committed
# --------------------------------------------------------------------------

def solid(w, h, rgb=(68, 68, 68)):
    """A flat RGB image in the tool's own (w, h, bpp, px) form."""
    return (w, h, 3, bytes(bytes(rgb) * (w * h)))


def painted(w, h, bg=(68, 68, 68), boxes=()):
    """`boxes` is a list of (x, y, w, h, rgb) painted over a flat background."""
    px = bytearray(bytes(bg) * (w * h))
    for (bx, by, bw, bh, rgb) in boxes:
        for y in range(max(0, by), min(h, by + bh)):
            for x in range(max(0, bx), min(w, bx + bw)):
                o = (y * w + x) * 3
                px[o:o + 3] = bytes(rgb)
    return (w, h, 3, bytes(px))


INK = (214, 214, 214)
FILL = (49, 49, 49)


# --------------------------------------------------------------------------
# geometry
# --------------------------------------------------------------------------

class GeometryTests(unittest.TestCase):
    def test_intersection_of_overlapping_rects(self):
        self.assertEqual(fid.intersect((0, 0, 10, 10), (5, 5, 10, 10)),
                         (5, 5, 5, 5))

    def test_rects_that_do_not_meet_intersect_to_nothing(self):
        self.assertIsNone(fid.intersect((0, 0, 10, 10), (20, 20, 5, 5)))
        self.assertIsNone(fid.intersect((0, 0, 10, 10), (10, 0, 5, 5)))

    def test_the_inset_takes_the_border_off_every_side(self):
        self.assertEqual(fid.inset((10, 20, 100, 30), 2), (12, 22, 96, 26))

    def test_a_rect_too_small_to_inset_yields_nothing(self):
        self.assertIsNone(fid.inset((0, 0, 4, 40), 2))
        self.assertIsNone(fid.inset((0, 0, 40, 3), 2))


class AbsoluteNodeWalkTests(unittest.TestCase):
    """`compact_tree` stores child rects parent-relative for the page's nested
    divs; a measurement against a screen-sized frame needs them back in screen
    coordinates, with the clip the page's own `overflow:hidden` applies."""

    ROOT = {
        "k": "window", "x": 100, "y": 50, "w": 200, "h": 100,
        "c": [
            {"k": "scrollview", "x": 10, "y": 20, "w": 100, "h": 40,
             "c": [
                 {"k": "label", "x": 5, "y": 5, "w": 60, "h": 20, "t": "in"},
                 {"k": "label", "x": 5, "y": 60, "w": 60, "h": 20, "t": "below"},
             ]},
        ],
    }

    def setUp(self):
        self.rect, self.nodes = fid.abs_nodes(self.ROOT)

    def test_the_root_rect_is_returned_as_given(self):
        self.assertEqual(self.rect, (100, 50, 200, 100))

    def test_a_childs_rect_is_re_accumulated_into_screen_coordinates(self):
        sv = [n for n in self.nodes if n["k"] == "scrollview"][0]
        self.assertEqual(sv["rect"], (110, 70, 100, 40))
        inside = [n for n in self.nodes if n["t"] == "in"][0]
        self.assertEqual(inside["rect"], (115, 75, 60, 20))

    def test_a_node_the_page_clips_away_has_no_visible_rect(self):
        below = [n for n in self.nodes if n["t"] == "below"][0]
        self.assertEqual(below["rect"], (115, 130, 60, 20))
        self.assertIsNone(below["vis"],
                          "a control past the scroll view's fold is not drawn")

    def test_a_partially_clipped_node_is_measured_over_its_visible_part(self):
        root = {"k": "window", "x": 0, "y": 0, "w": 100, "h": 100,
                "c": [{"k": "label", "x": 80, "y": 10, "w": 40, "h": 20,
                       "t": "wide"}]}
        _r, nodes = fid.abs_nodes(root)
        wide = [n for n in nodes if n["t"] == "wide"][0]
        self.assertEqual(wide["vis"], (80, 10, 20, 20))

    def test_the_window_itself_is_measurable_and_comes_first(self):
        self.assertEqual(self.nodes[0]["k"], "window")
        self.assertEqual(self.nodes[0]["rect"], (100, 50, 200, 100))

    def test_a_titled_window_yields_a_title_band_derived_from_its_first_child(self):
        root = {"k": "window", "x": 0, "y": 0, "w": 300, "h": 200,
                "title": "Parsek", "c": [{"k": "label", "x": 8, "y": 40,
                                          "w": 50, "h": 20, "t": "a"}]}
        _r, nodes = fid.abs_nodes(root)
        title = [n for n in nodes if n["k"] == "title"][0]
        self.assertEqual(title["t"], "Parsek")
        self.assertEqual(title["rect"], (0, 0, 300, 40),
                         "the band is the gap above the first child, not a "
                         "height typed into the instrument")

    def test_an_untitled_window_yields_no_title_band(self):
        self.assertEqual([n for n in self.nodes if n["k"] == "title"], [])

    def test_the_leaf_flag_and_the_child_rects_come_out_absolute(self):
        sv = [n for n in self.nodes if n["k"] == "scrollview"][0]
        self.assertFalse(sv["leaf"])
        self.assertIn((115, 75, 60, 20), sv["kids"])
        self.assertTrue([n for n in self.nodes if n["t"] == "in"][0]["leaf"])


# --------------------------------------------------------------------------
# pixels
# --------------------------------------------------------------------------

class InkBoxTests(unittest.TestCase):
    def test_the_ink_box_is_the_extent_of_the_contrasting_pixels(self):
        img = painted(100, 50, (68, 68, 68), [(20, 10, 30, 12, INK)])
        box = fid.ink_bbox(img, (0, 0, 100, 50))
        self.assertEqual(box[:4], (20, 10, 50, 22))
        self.assertEqual(box[4], 30 * 12)

    def test_a_flat_rect_has_no_ink(self):
        self.assertIsNone(fid.ink_bbox(solid(40, 20), (0, 0, 40, 20)))

    def test_the_background_is_the_rects_own_median_not_a_constant(self):
        # The same label style is drawn on #444444, #292929 and #313131 across
        # the corpus. A fixed reference would read one of the three as ink.
        for bg in ((68, 68, 68), (41, 41, 41), (49, 49, 49)):
            img = painted(60, 20, bg, [(10, 5, 8, 10, INK)])
            box = fid.ink_bbox(img, (0, 0, 60, 20))
            self.assertEqual(box[:4], (10, 5, 18, 15), "bg %r" % (bg,))

    def test_a_rect_outside_the_image_measures_nothing(self):
        self.assertIsNone(fid.ink_bbox(solid(20, 20), (50, 50, 10, 10)))

    def test_a_supplied_background_overrides_the_median(self):
        img = painted(30, 10, (68, 68, 68), [(0, 0, 30, 10, INK)])
        self.assertIsNone(fid.ink_bbox(img, (0, 0, 30, 10)),
                          "a rect that is all one colour has no ink in it")
        box = fid.ink_bbox(img, (0, 0, 30, 10), background=68.0)
        self.assertEqual(box[:4], (0, 0, 30, 10))

    def test_the_border_of_a_control_is_what_the_inset_is_for(self):
        # One 2 px border in the ink colour plus a short label. Measured whole,
        # the box is the border; measured inset, it is the label. This is the
        # instrument's own worst bug, pinned.
        img = painted(100, 21, FILL, [(0, 0, 100, 2, INK), (0, 19, 100, 2, INK),
                                      (0, 0, 2, 21, INK), (98, 0, 2, 21, INK),
                                      (40, 7, 20, 8, INK)])
        whole = fid.ink_bbox(img, (0, 0, 100, 21))
        self.assertEqual(whole[:4], (0, 0, 100, 21))
        tight = fid.ink_bbox(img, fid.inset((0, 0, 100, 21)))
        self.assertEqual(tight[:4], (40, 7, 60, 15))


class FillTests(unittest.TestCase):
    def test_the_median_is_the_controls_own_surface_not_its_childs(self):
        img = painted(100, 40, (68, 68, 68), [(10, 10, 80, 20, (200, 0, 0))])
        self.assertEqual(fid.median_rgb(img, (0, 0, 100, 40),
                                        [(10, 10, 80, 20)]), (68, 68, 68))

    def test_a_fully_covered_control_falls_back_to_the_whole_rect(self):
        img = painted(40, 40, (68, 68, 68), [(0, 0, 40, 40, (10, 20, 30))])
        self.assertEqual(fid.median_rgb(img, (0, 0, 40, 40), [(0, 0, 40, 40)]),
                         (10, 20, 30))

    def test_the_delta_is_the_worst_single_channel(self):
        self.assertEqual(fid.rgb_delta((10, 10, 10), (10, 10, 50)), 40)
        self.assertEqual(fid.rgb_delta((10, 10, 10), (10, 10, 10)), 0)

    def test_a_missing_side_has_no_delta(self):
        self.assertIsNone(fid.rgb_delta(None, (1, 2, 3)))
        self.assertIsNone(fid.rgb_delta((1, 2, 3), None))

    def test_a_rect_off_the_image_has_no_median(self):
        self.assertIsNone(fid.median_rgb(solid(10, 10), (40, 40, 5, 5)))


class LuminanceScoreTests(unittest.TestCase):
    def test_two_identical_images_score_zero(self):
        img = painted(40, 20, (68, 68, 68), [(5, 5, 10, 10, INK)])
        self.assertEqual(fid.mean_abs_lum_diff(img, img, (0, 0, 40, 20)), 0.0)

    def test_the_score_is_the_mean_over_the_rect(self):
        a = solid(10, 10, (0, 0, 0))
        b = solid(10, 10, (255, 255, 255))
        self.assertAlmostEqual(fid.mean_abs_lum_diff(a, b, (0, 0, 10, 10)),
                               255.0, places=3)


# --------------------------------------------------------------------------
# the metrics
# --------------------------------------------------------------------------

class TextMetricTests(unittest.TestCase):
    RECT = (10, 10, 100, 20)

    def test_a_matching_run_reads_zero_offset_and_ratio_one(self):
        m = fid.text_metric((20, 14, 60, 24, 99), (20, 14, 60, 24, 99), self.RECT)
        self.assertEqual((m["dx"], m["dy"]), (0, 0))
        self.assertEqual(m["wr"], 1.0)
        self.assertFalse(m["clipped"])
        self.assertEqual(m["status"], "both")

    def test_the_offset_is_signed_the_mirrors_way(self):
        m = fid.text_metric((20, 14, 60, 24, 9), (23, 12, 63, 22, 9), self.RECT)
        self.assertEqual((m["dx"], m["dy"]), (3, -2))

    def test_a_wide_font_reads_a_ratio_above_one(self):
        m = fid.text_metric((20, 14, 60, 24, 9), (20, 14, 66, 24, 9), self.RECT)
        self.assertAlmostEqual(m["wr"], 46.0 / 40.0)

    def test_a_run_that_ends_at_the_rect_edge_where_the_frames_did_not_is_clipped(self):
        m = fid.text_metric((20, 14, 80, 24, 9), (20, 14, 110, 24, 9), self.RECT)
        self.assertTrue(m["clipped"], "the mirror ran into the edge")

    def test_a_run_that_merely_reaches_the_edge_on_both_sides_is_not_clipped(self):
        m = fid.text_metric((20, 14, 110, 24, 9), (20, 14, 110, 24, 9), self.RECT)
        self.assertFalse(m["clipped"],
                         "the game drew to the edge too, so nothing was cut")

    def test_a_mirror_run_that_ends_inside_the_rect_is_not_clipped(self):
        m = fid.text_metric((20, 14, 60, 24, 9), (20, 14, 70, 24, 9), self.RECT)
        self.assertFalse(m["clipped"])
        self.assertGreater(m["wr"], 1.0, "it is a font-metric finding instead")

    def test_clipping_is_not_decided_by_which_run_has_more_ink(self):
        # A font a few per cent wide runs INTO the edge and is cut there with
        # more ink than the frame's, not less. A width comparison would miss
        # exactly the case that loses characters, which is the case this flag is
        # for.
        m = fid.text_metric((20, 14, 90, 24, 9), (20, 14, 110, 24, 9), self.RECT)
        self.assertTrue(m["clipped"])
        self.assertGreater(m["wr"], 1.0)

    def test_one_sided_ink_is_reported_as_such_with_no_numbers(self):
        f = fid.text_metric((1, 1, 2, 2, 9), None, self.RECT)
        self.assertEqual(f["status"], "frame-only")
        self.assertIsNone(f["dx"])
        m = fid.text_metric(None, (1, 1, 2, 2, 9), self.RECT)
        self.assertEqual(m["status"], "mirror-only")
        self.assertEqual(fid.text_metric(None, None, self.RECT)["status"],
                         "both-empty")


class PresenceGroupingTests(unittest.TestCase):
    def test_the_class_is_kind_style_and_whether_there_is_text(self):
        self.assertEqual(
            fid.presence_key({"k": "button", "s": "label", "t": "Go"}),
            "button|label|text")
        self.assertEqual(
            fid.presence_key({"k": "slider", "s": "", "t": ""}),
            "slider|-|notext")


class StatisticsTests(unittest.TestCase):
    def test_the_percentile_is_nearest_rank(self):
        vals = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
        self.assertEqual(fid.percentile(vals, 50), 6)
        self.assertEqual(fid.percentile(vals, 95), 10)
        self.assertEqual(fid.percentile(vals, 0), 1)

    def test_a_half_rank_does_not_move_with_the_sample_parity(self):
        # Python's round() sends a half to the even neighbour, which would make
        # the p50 of ten values and of eleven disagree about which way it leans.
        self.assertEqual(fid.percentile([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], 50), 5)
        self.assertEqual(fid.percentile([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 5)

    def test_an_empty_set_has_no_percentile(self):
        self.assertIsNone(fid.percentile([], 50))

    def test_one_value_is_its_own_percentile(self):
        self.assertEqual(fid.percentile([7], 95), 7)

    def test_the_stat_block_works_on_absolute_values(self):
        b = fid._stat_block([-5, 1, 2])
        self.assertEqual(b["n"], 3)
        self.assertEqual(b["worst"], 5)


class AggregationTests(unittest.TestCase):
    RECORDS = [
        {"id": "r/a", "window": "main", "score": 4.0, "clippedCount": 1,
         "texts": [
             {"class": "label|label|text", "status": "both", "dx": 1, "dy": 0,
              "wr": 1.0, "clipped": False},
             {"class": "label|label|text", "status": "both", "dx": 9, "dy": 2,
              "wr": 1.2, "clipped": True},
             {"class": "button|button|text", "status": "frame-only", "dx": None,
              "dy": None, "wr": None, "clipped": False},
         ],
         "fills": [{"class": "box|box|notext", "delta": 3},
                   {"class": "box|box|notext", "delta": None}],
         "presence": [{"class": "slider|-|notext", "side": "frame"},
                      {"class": "toggle|toggle|text", "side": "mirror"}]},
        {"id": "r/b", "window": "kerbals", "score": 2.0, "clippedCount": 0,
         "texts": [{"class": "label|label|text", "status": "both", "dx": -3,
                    "dy": 1, "wr": 0.9, "clipped": False}],
         "fills": [], "presence": []},
    ]

    def setUp(self):
        self.agg = fid.aggregate(self.RECORDS)

    def test_every_window_gets_its_own_row(self):
        self.assertEqual(sorted(self.agg["windows"]), ["kerbals", "main"])
        self.assertEqual(self.agg["windows"]["main"]["captures"], 1)

    def test_a_one_sided_text_counts_as_a_text_but_contributes_no_offset(self):
        main = self.agg["windows"]["main"]
        self.assertEqual(main["texts"], 3)
        self.assertEqual(main["dx"]["n"], 2)

    def test_the_clipped_count_reaches_both_tables_and_the_totals(self):
        self.assertEqual(self.agg["windows"]["main"]["clipped"], 1)
        self.assertEqual(self.agg["classes"]["label|label|text"]["clipped"], 1)
        self.assertEqual(self.agg["totals"]["clipped"], 1)

    def test_a_fill_with_no_delta_is_left_out_of_the_statistics(self):
        self.assertEqual(self.agg["classes"]["box|box|notext"]["fill"]["n"], 1)

    def test_presence_is_counted_on_the_side_it_was_found(self):
        self.assertEqual(self.agg["classes"]["slider|-|notext"]["frameOnly"], 1)
        self.assertEqual(self.agg["classes"]["toggle|toggle|text"]["mirrorOnly"], 1)
        self.assertEqual(self.agg["totals"]["frameOnly"], 1)
        self.assertEqual(self.agg["totals"]["mirrorOnly"], 1)

    def test_the_totals_span_every_window(self):
        self.assertEqual(self.agg["totals"]["captures"], 2)
        self.assertEqual(self.agg["totals"]["dx"]["n"], 3)
        self.assertEqual(self.agg["totals"]["dx"]["worst"], 9)

    def test_the_width_ratio_worst_is_the_one_furthest_from_one(self):
        self.assertAlmostEqual(self.agg["totals"]["wr"]["worst"], 1.2)

    def test_nothing_measured_aggregates_to_an_empty_set_not_an_exception(self):
        agg = fid.aggregate([])
        self.assertEqual(agg["windows"], {})
        self.assertEqual(agg["totals"]["texts"], 0)
        self.assertIsNone(agg["totals"]["dx"]["p50"])


class WorstOrderTests(unittest.TestCase):
    def test_clipping_outranks_an_offset_which_outranks_the_score(self):
        recs = [
            {"id": "low", "clippedCount": 0, "score": 1.0, "texts": []},
            {"id": "offset", "clippedCount": 0, "score": 0.5,
             "texts": [{"status": "both", "dx": 40}]},
            {"id": "clip", "clippedCount": 1, "score": 0.1, "texts": []},
        ]
        self.assertEqual([r["id"] for r in fid.worst_captures(recs, 3)],
                         ["clip", "offset", "low"])

    def test_the_limit_is_honoured(self):
        self.assertEqual(len(fid.worst_captures(
            [{"id": str(i), "clippedCount": 0, "score": 0, "texts": []}
             for i in range(10)], 3)), 3)


# --------------------------------------------------------------------------
# the browser, located and driven with no browser present
# --------------------------------------------------------------------------

class BrowserLocationTests(unittest.TestCase):
    CANDS = (r"C:\edge.exe", r"C:\chrome.exe")

    def test_the_candidates_are_probed_in_order(self):
        self.assertEqual(
            fid.find_browser(candidates=self.CANDS, exists=lambda p: True),
            r"C:\edge.exe")
        self.assertEqual(
            fid.find_browser(candidates=self.CANDS,
                             exists=lambda p: p.endswith("chrome.exe")),
            r"C:\chrome.exe")

    def test_no_browser_at_all_is_reported_as_none_not_guessed(self):
        self.assertIsNone(fid.find_browser(candidates=self.CANDS,
                                           exists=lambda p: False))

    def test_an_override_is_used_when_it_exists_and_refused_when_it_does_not(self):
        self.assertEqual(fid.find_browser("/my/browser", self.CANDS,
                                          exists=lambda p: True), "/my/browser")
        self.assertIsNone(fid.find_browser("/my/browser", self.CANDS,
                                           exists=lambda p: p != "/my/browser"),
                          "an override that is not there must not fall back to a "
                          "candidate the caller did not ask for")

    def test_the_shipped_candidate_list_is_the_two_windows_paths(self):
        self.assertEqual(len(fid.BROWSER_CANDIDATES), 2)
        self.assertTrue(any("msedge" in c for c in fid.BROWSER_CANDIDATES))
        self.assertTrue(any("chrome" in c for c in fid.BROWSER_CANDIDATES))

    def test_the_cli_exits_with_a_code_and_a_message_when_there_is_no_browser(self):
        import io
        import contextlib
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            rc = fid.main(["--shots", REPO, "--out-dir", REPO,
                           "--browser", os.path.join(REPO, "no-such-browser")])
        self.assertEqual(rc, 3)
        self.assertIn("no headless browser", err.getvalue())


class DeepLinkTests(unittest.TestCase):
    def test_the_capture_id_survives_its_slash(self):
        url = fid.bare_url("/tmp/page.html", "2026-09-11_0548/ksc-main-advanced")
        self.assertIn("#cap=2026-09-11_0548%2Fksc-main-advanced&bare=1", url)
        self.assertTrue(url.startswith("file:///"))

    def test_the_grammar_here_and_the_pages_own_agree(self):
        # The page parses the fragment with its own `parseHash`; this function is
        # that grammar in Python. If they drift, the instrument photographs the
        # wrong capture - or a blank - and reports it as a defect in the mirror.
        cid = "2026-09-11_0548/ksc-main-advanced"
        frag = fid.bare_url("/tmp/p.html", cid).split("#", 1)[1]
        q = fid.parse_hash_query(frag)
        self.assertEqual(q["cap"], cid)
        self.assertEqual(q["bare"], "1")

    def test_a_bare_key_with_no_value_reads_as_one(self):
        self.assertEqual(fid.parse_hash_query("#bare")["bare"], "1")

    def test_an_empty_fragment_parses_to_nothing(self):
        self.assertEqual(fid.parse_hash_query(""), {})
        self.assertEqual(fid.parse_hash_query("#"), {})
        self.assertEqual(fid.parse_hash_query("#&&"), {})

    def test_the_foreign_flag_is_opt_in(self):
        self.assertNotIn("foreign", fid.bare_url("/p.html", "a/b"))
        self.assertIn("&foreign=1", fid.bare_url("/p.html", "a/b", foreign=True))

    def test_the_pages_parse_hash_is_the_same_grammar_in_javascript(self):
        # Source-level, because the JS cannot be executed here: the page must
        # split on '&', take the first '=' as the separator, default a bare key
        # to '1' and decode both halves.
        js = gmi.JS
        self.assertIn("function parseHash(h)", js)
        for needle in ("split('&')", "indexOf('=')", "decodeURIComponent"):
            self.assertIn(needle, js, "parseHash lost %r" % needle)


class BrowserArgvTests(unittest.TestCase):
    """The launch is an argument LIST with an absolute profile directory, and
    both halves of that were paid for.

    A relative `--user-data-dir` reaches Edge as a fragment and Edge answers with
    a modal on the owner's desktop - once per launch, which on this corpus is 230
    dialogs. A profile under a deep scratch path silently exceeded Windows' path
    limit, so the launch returned 0 and wrote no screenshot at all.
    """

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-fidelity-test-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_the_argv_is_a_list_of_plain_arguments(self):
        argv = fid.browser_argv("/br", "file:///p.html#bare=1", "/o.png",
                                (1280, 720), self.tmp)
        self.assertIsInstance(argv, list)
        for part in argv:
            self.assertIsInstance(part, str)
            self.assertNotIn(" --", part,
                             "%r looks shell-joined; a list is never joined" % part)

    def test_the_profile_directory_is_absolute_and_present_in_the_argv(self):
        argv = fid.browser_argv("/br", "u", "/o.png", (10, 10), self.tmp)
        prof = [a for a in argv if a.startswith("--user-data-dir=")]
        self.assertEqual(len(prof), 1)
        path = prof[0].split("=", 1)[1]
        self.assertTrue(os.path.isabs(path), path)
        self.assertTrue(os.path.isdir(path), path)

    def test_a_relative_profile_is_refused_before_the_browser_sees_it(self):
        for bad in ("pp", "", None, "work/profile-1"):
            with self.assertRaises(ValueError):
                fid.browser_argv("/br", "u", "/o.png", (10, 10), bad)

    def test_the_quiet_flags_are_all_there(self):
        argv = fid.browser_argv("/br", "u", "/o.png", (10, 10), self.tmp)
        for flag in ("--headless=new", "--no-first-run",
                     "--no-default-browser-check", "--disable-gpu",
                     "--disable-extensions",
                     "--force-device-scale-factor=1"):
            self.assertIn(flag, argv)

    def test_the_browsers_own_scrollbars_are_not_hidden(self):
        # `--hide-scrollbars` made the instrument blind to a regression the page
        # introduced: `overflow:auto` on a scroll view gives it a white NATIVE
        # scroll bar over the mirrored KSP one, on all 52 overflowing views. The
        # page hides its own (`scrollbar-width:none`); the instrument must see
        # what a reader sees.
        argv = fid.browser_argv("/br", "u", "/o.png", (10, 10), self.tmp)
        self.assertNotIn("--hide-scrollbars", argv)

    def test_the_url_is_last_and_the_screenshot_path_is_its_own_argument(self):
        argv = fid.browser_argv("/br", "file:///p#bare=1", "/o.png", (4, 4),
                                self.tmp)
        self.assertEqual(argv[-1], "file:///p#bare=1")
        self.assertIn("--screenshot=/o.png", argv)

    def test_a_path_at_the_windows_limit_is_refused_by_name(self):
        # The worst failure this tool had, and the hardest to read: the
        # screenshots were named after their capture and written beside the
        # report, which lives in a 189-character scratch directory, so the long
        # labels came to 264 characters. The launch returned 0, wrote nothing and
        # said nothing on stderr, and the run then burned 240 s of timeout and
        # retry per capture before halting. It has to be refused where it can
        # still be explained.
        long_png = os.path.join(self.tmp, "x" * fid.MAX_BROWSER_PATH + ".png")
        with self.assertRaises(ValueError) as ctx:
            fid.browser_argv("/br", "u", long_png, (10, 10), self.tmp)
        self.assertIn("--screenshot", str(ctx.exception))
        self.assertIn(str(fid.MAX_BROWSER_PATH), str(ctx.exception))

    def test_a_short_screenshot_path_is_accepted(self):
        argv = fid.browser_argv("/br", "u", os.path.join(self.tmp, "0001.png"),
                                (10, 10), self.tmp)
        self.assertTrue(any(a.startswith("--screenshot=") for a in argv))

    def test_the_limit_leaves_room_under_the_windows_one(self):
        self.assertLess(fid.MAX_BROWSER_PATH, 260)

    def test_an_unusable_profile_is_named_rather_than_launched_into(self):
        self.assertEqual(fid.check_profile_dir(self.tmp), "")
        self.assertIn("not absolute", fid.check_profile_dir("pp"))
        self.assertIn("does not exist",
                      fid.check_profile_dir(os.path.join(self.tmp, "nope")))
        self.assertIn("not writable",
                      fid.check_profile_dir(self.tmp, writable=lambda p: False))

    def test_each_worker_thread_gets_its_own_short_path_profile(self):
        pool = {}
        mine = fid._profile_for_thread(pool)
        self.addCleanup(shutil.rmtree, mine, True)
        self.assertEqual(fid._profile_for_thread(pool), mine,
                         "a thread must reuse its own profile, and stay warm")
        self.assertTrue(os.path.isabs(mine))
        self.assertIn("parsek-fidelity-", os.path.basename(mine))
        other = {}
        seen = []

        def run():
            seen.append(fid._profile_for_thread(other))

        th = threading.Thread(target=run)
        th.start()
        th.join()
        self.addCleanup(shutil.rmtree, seen[0], True)
        self.assertNotEqual(seen[0], mine,
                            "two threads sharing one profile made the second "
                            "Edge hand its URL to the first and write nothing")


class ScreenshotDrivingTests(unittest.TestCase):
    """The shell that drives the browser, driven with no browser: `runner` and
    `sleep` are injected."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _valid_png(self, path):
        with open(path, "wb") as fh:
            fh.write(gmi.write_png(4, 4, 3, bytes(4 * 4 * 3)))

    def test_a_finished_png_is_returned(self):
        out = os.path.join(self.tmp, "s.png")
        seen = {}

        def runner(cmd, **kw):
            seen["cmd"] = cmd
            seen["kw"] = kw
            self._valid_png(out)
            return subprocess.CompletedProcess(cmd, 0, None, None)

        got = fid.screenshot("/br", "file:///p.html#bare=1", out, (1280, 720),
                             profile_dir=self.tmp, runner=runner,
                             sleep=lambda s: None)
        self.assertEqual(got, out)
        cmd = seen["cmd"]
        self.assertIn("--headless=new", cmd)
        self.assertIn("--force-device-scale-factor=1", cmd)
        self.assertNotIn("--hide-scrollbars", cmd,
                         "the flag hid a difference the page itself introduced: "
                         "a native scroll bar over the mirrored KSP one")
        self.assertIn("--window-size=1280,720", cmd)
        self.assertIn("--screenshot=" + out, cmd)
        self.assertEqual(cmd[-1], "file:///p.html#bare=1")
        self.assertNotIn("capture_output", seen["kw"],
                         "a pipe the browser's grandchildren inherit never "
                         "closes, and subprocess.run then waits for ever")
        self.assertEqual(seen["kw"]["stdout"], subprocess.DEVNULL)

    def test_the_profile_directory_reaches_the_launch(self):
        out = os.path.join(self.tmp, "s.png")

        def runner(cmd, **kw):
            self.seen = cmd
            self._valid_png(out)
            return subprocess.CompletedProcess(cmd, 0, None, None)

        fid.screenshot("/br", "u", out, (10, 10), profile_dir=self.tmp,
                       runner=runner, sleep=lambda s: None)
        self.assertIn("--user-data-dir=" + self.tmp, self.seen)

    def test_a_launch_without_a_usable_profile_is_refused(self):
        # Rather than handing Edge a relative path and letting it put a dialog on
        # the owner's desktop once per capture.
        out = os.path.join(self.tmp, "s.png")
        for bad in (None, "pp", os.path.join(self.tmp, "absent")):
            with self.assertRaises(RuntimeError):
                fid.screenshot("/br", "u", out, (10, 10), profile_dir=bad,
                               runner=lambda *a, **k: None,
                               sleep=lambda s: None)

    def test_a_browser_that_writes_nothing_raises_with_its_own_stderr(self):
        out = os.path.join(self.tmp, "s.png")

        def runner(cmd, **kw):
            # The browser's diagnostics reach us through a FILE, never a pipe:
            # msedge.exe is a launcher whose detached grandchildren inherit a
            # pipe and never close it, and `subprocess.run` then waits forever -
            # its own timeout does not help, because after killing the direct
            # child it goes back to reading the same pipe.
            kw["stderr"].write(b"it broke")
            return subprocess.CompletedProcess(cmd, 0, None, None)

        with self.assertRaises(RuntimeError) as ctx:
            fid.screenshot("/br", "u", out, (10, 10), timeout=0.05,
                           profile_dir=self.tmp, runner=runner,
                           sleep=lambda s: None)
        self.assertIn("it broke", str(ctx.exception))

    def test_a_stale_screenshot_is_removed_before_the_run(self):
        out = os.path.join(self.tmp, "s.png")
        self._valid_png(out)
        marker = {}

        def runner(cmd, **kw):
            marker["gone"] = not os.path.exists(out)
            self._valid_png(out)
            return subprocess.CompletedProcess(cmd, 0, None, None)

        fid.screenshot("/br", "u", out, (10, 10), profile_dir=self.tmp,
                       runner=runner, sleep=lambda s: None)
        self.assertTrue(marker["gone"],
                        "a previous run's PNG would have been measured again")

    def test_a_half_written_png_is_not_mistaken_for_a_finished_one(self):
        out = os.path.join(self.tmp, "p.png")
        blob = gmi.write_png(8, 8, 3, bytes(8 * 8 * 3))
        with open(out, "wb") as fh:
            fh.write(blob[:-12])
        self.assertFalse(fid._png_complete(out))
        with open(out, "wb") as fh:
            fh.write(blob)
        self.assertTrue(fid._png_complete(out))

    def test_a_file_that_is_not_a_png_is_not_complete(self):
        out = os.path.join(self.tmp, "x.png")
        with open(out, "wb") as fh:
            fh.write(b"x" * 400)
        self.assertFalse(fid._png_complete(out))
        self.assertFalse(fid._png_complete(os.path.join(self.tmp, "absent.png")))


# --------------------------------------------------------------------------
# one whole capture, measured, with no browser
# --------------------------------------------------------------------------

def _cap(roots, dialog=None, screen=(200, 120)):
    return {"id": "run/lab", "label": "lab", "window": "main", "tab": None,
            "state": "", "mode": "advanced", "runId": "run", "fixture": "fix",
            "screen": list(screen), "dialog": dialog, "roots": roots}


class MeasureCaptureTests(unittest.TestCase):
    ROOT = {"k": "window", "x": 10, "y": 10, "w": 160, "h": 90, "bg": "#444444",
            "c": [
                {"k": "label", "x": 10, "y": 30, "w": 100, "h": 20, "t": "Hello"},
                {"k": "slider", "x": 10, "y": 60, "w": 100, "h": 12},
            ]}

    def _imgs(self, mirror_text_x):
        # window #444444 at (10,10,160,90); a label glyph run in each image
        frame = painted(200, 120, (20, 30, 20),
                        [(10, 10, 160, 90, (68, 68, 68)),
                         (30, 44, 40, 8, INK),               # the label's ink
                         (22, 74, 80, 4, (140, 140, 140))])   # a slider handle
        mirror = painted(200, 120, (0, 0, 0),
                         [(10, 10, 160, 90, (68, 68, 68)),
                          (mirror_text_x, 44, 40, 8, INK)])
        return frame, mirror

    def test_a_matching_render_measures_zero_offset(self):
        frame, mirror = self._imgs(30)
        rec = fid.measure_capture(_cap([self.ROOT]), frame, mirror)
        texts = [t for t in rec["texts"] if t["status"] == "both"]
        self.assertEqual(len(texts), 1)
        self.assertEqual((texts[0]["dx"], texts[0]["dy"]), (0, 0))
        self.assertAlmostEqual(texts[0]["wr"], 1.0)

    def test_a_shifted_render_measures_the_shift(self):
        frame, mirror = self._imgs(35)
        rec = fid.measure_capture(_cap([self.ROOT]), frame, mirror)
        self.assertEqual([t["dx"] for t in rec["texts"]
                          if t["status"] == "both"], [5])

    def test_something_the_frame_draws_and_the_mirror_does_not_is_presence(self):
        frame, mirror = self._imgs(30)
        rec = fid.measure_capture(_cap([self.ROOT]), frame, mirror)
        sliders = [p for p in rec["presence"] if p["class"].startswith("slider")]
        self.assertEqual(len(sliders), 1, "the slider handle went unreported")
        self.assertEqual(sliders[0]["side"], "frame")

    def test_the_stored_fill_is_checked_against_both_images(self):
        frame, mirror = self._imgs(30)
        rec = fid.measure_capture(_cap([self.ROOT]), frame, mirror)
        fills = [f for f in rec["fills"] if f["stored"]]
        self.assertTrue(fills)
        self.assertEqual(fills[0]["delta"], 0)

    def test_a_window_score_is_produced_for_ranking(self):
        frame, mirror = self._imgs(35)
        rec = fid.measure_capture(_cap([self.ROOT]), frame, mirror)
        self.assertIsNotNone(rec["score"])
        self.assertGreater(rec["score"], 0)

    def test_a_foreign_window_is_not_measured(self):
        foreign = dict(self.ROOT)
        foreign["foreign"] = 1
        frame, mirror = self._imgs(30)
        rec = fid.measure_capture(_cap([foreign]), frame, mirror)
        self.assertIn("no Parsek window", rec["skipped"])

    def test_a_modal_capture_is_declined_with_the_reason(self):
        frame, mirror = self._imgs(30)
        rec = fid.measure_capture(
            _cap([self.ROOT], dialog={"name": "d", "title": "t", "buttons": []}),
            frame, mirror)
        self.assertIn("modal", rec["skipped"])
        self.assertEqual(rec["texts"], [])

    def test_a_blank_mirror_frame_is_caught_before_it_is_measured(self):
        frame, _m = self._imgs(30)
        blank = solid(200, 120, (0, 0, 0))
        self.assertIn("ready marker", fid.looks_unpainted(_cap([self.ROOT]), blank))

    def test_one_empty_window_does_not_condemn_the_capture(self):
        # Deciding on the FIRST root declined four captures whose first window is
        # a small chrome strip - the watch-mode overlay is a 300x22 bar that
        # carries no ink of its own - while the windows behind it had drawn
        # perfectly well.
        strip = {"k": "window", "x": 10, "y": 5, "w": 100, "h": 12, "c": []}
        _f, mirror = self._imgs(30)
        self.assertEqual(fid.looks_unpainted(_cap([strip, self.ROOT]), mirror), "")

    def test_a_capture_whose_windows_are_all_too_small_is_not_condemned(self):
        tiny = {"k": "window", "x": 0, "y": 0, "w": 4, "h": 4, "c": []}
        blank = solid(200, 120, (0, 0, 0))
        self.assertEqual(fid.looks_unpainted(_cap([tiny]), blank), "")

    def test_a_painted_mirror_frame_passes_the_ready_check(self):
        _f, mirror = self._imgs(30)
        self.assertEqual(fid.looks_unpainted(_cap([self.ROOT]), mirror), "")


# --------------------------------------------------------------------------
# the report
# --------------------------------------------------------------------------


class SliderMetricTests(unittest.TestCase):
    """PRESENCE cannot see a slider: it asks "are there four ink pixels in this
    rect", and a groove has a border, so the answer is yes whatever the page
    draws inside it. Deleting the handle element moved NO metric in the report
    while the page's handle was 168 luminance away from the game's - the corpus
    said "slider frame-only 41 -> 0" about a change that had moved the page
    AWAY from the frame. Hence a metric of the slider's own."""

    def bar(self, thumb_lum, groove_lum=45, start=20, length=60):
        px = bytearray(bytes((0, 0, 0)) * (40 * 200))
        for y in range(10, 190):
            for x in range(10, 30):
                o = (y * 40 + x) * 3
                px[o:o + 3] = bytes((groove_lum,) * 3)
        for y in range(10 + start, 10 + start + length):
            for x in range(10, 30):
                o = (y * 40 + x) * 3
                px[o:o + 3] = bytes((thumb_lum,) * 3)
        return (40, 200, 3, bytes(px))

    RECT = (10, 10, 20, 180)

    def test_two_identical_sliders_read_zero_error(self):
        img = self.bar(17)
        m = fid.slider_metric(img, img, self.RECT)
        self.assertEqual(m["mae"], 0.0)

    def test_a_wrong_coloured_thumb_is_an_error_the_metric_sees(self):
        # The defect this metric exists for: the page drew the handle at
        # luminance 185 where the game draws 17.
        frame = self.bar(17)
        mirror = self.bar(185)
        m = fid.slider_metric(frame, mirror, self.RECT)
        self.assertGreater(m["mae"], 40)

    def test_the_thumb_run_is_compared_on_both_sides_when_both_resolve(self):
        frame = self.bar(180, groove_lum=45, start=20, length=60)
        mirror = self.bar(180, groove_lum=45, start=26, length=60)
        m = fid.slider_metric(frame, mirror, self.RECT,
                              thumb_run=fid._thumb_run)
        self.assertEqual(m["frameRun"], [20, 60])
        self.assertEqual(m["mirrorRun"], [26, 60])
        self.assertEqual((m["dStart"], m["dLen"]), (6, 0))

    def test_a_missing_thumb_leaves_the_run_unresolved_on_that_side(self):
        # The mutation the reviewer asked for: remove the handle and a metric
        # must move. This is the one that moves decisively.
        frame = self.bar(180, start=20, length=60)
        flat = self.bar(45, start=20, length=60)
        m = fid.slider_metric(frame, flat, self.RECT, thumb_run=fid._thumb_run)
        self.assertEqual(m["frameRun"], [20, 60])
        self.assertIsNone(m["mirrorRun"])
        self.assertIsNone(m["dStart"])

    def test_without_a_reader_only_the_luminance_error_is_reported(self):
        img = self.bar(17)
        m = fid.slider_metric(img, img, self.RECT)
        self.assertIsNone(m["frameRun"])
        self.assertIsNone(m["dStart"])

    def test_the_reading_reaches_the_capture_record_and_the_tables(self):
        root = {"k": "window", "x": 0, "y": 0, "w": 40, "h": 200, "bg": "#2d2d2d",
                "c": [{"k": "slider", "s": "verticalscrollbar",
                       "x": 10, "y": 10, "w": 20, "h": 180}]}
        cap = _cap([root], screen=(40, 200))
        rec = fid.measure_capture(cap, self.bar(17), self.bar(185))
        self.assertEqual(len(rec["sliders"]), 1)
        self.assertGreater(rec["sliders"][0]["mae"], 40)
        agg = fid.aggregate([rec])
        self.assertEqual(agg["totals"]["slider"]["n"], 1)
        self.assertIn("slider|verticalscrollbar|notext", agg["classes"])


class TempDirCleanupTests(unittest.TestCase):
    """43 profile directories (320 MB) accumulated in the owner's %TEMP%:
    `shutil.rmtree(ignore_errors=True)` ran while the browser's children still
    held files, failed silently BECAUSE errors were ignored, and left them."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="parsek-fidelity-test-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_a_directory_that_goes_is_gone_and_reported_as_such(self):
        d = tempfile.mkdtemp(dir=self.tmp)
        self.assertEqual(fid.remove_temp_dirs([d], sleep=lambda s: None), [])
        self.assertFalse(os.path.isdir(d))

    def test_a_locked_directory_is_retried_and_then_NAMED(self):
        d = tempfile.mkdtemp(dir=self.tmp)
        tries = []

        def rmtree(path):
            tries.append(path)
            raise OSError(32, "in use")

        left = fid.remove_temp_dirs([d], attempts=4, sleep=lambda s: None,
                                    rmtree=rmtree)
        self.assertEqual(left, [d])
        self.assertEqual(len(tries), 4, "it has to RETRY, not give up at once")

    def test_it_succeeds_on_a_later_attempt(self):
        d = tempfile.mkdtemp(dir=self.tmp)
        state = {"n": 0}

        def rmtree(path):
            state["n"] += 1
            if state["n"] < 3:
                raise OSError(32, "the browser still holds it")
            shutil.rmtree(path)

        self.assertEqual(fid.remove_temp_dirs([d], sleep=lambda s: None,
                                              rmtree=rmtree), [])
        self.assertEqual(state["n"], 3)

    def test_a_path_that_is_not_there_is_not_an_error(self):
        self.assertEqual(
            fid.remove_temp_dirs([os.path.join(self.tmp, "gone"), None, ""],
                                 sleep=lambda s: None), [])

    def test_the_cleanup_runs_from_a_finally(self):
        # An interrupted run must not leak either.
        with open(fid.__file__, encoding="utf-8") as fh:
            src = fh.read()
        body = src.split("def main(", 1)[1]
        self.assertIn("finally:", body)
        self.assertIn("remove_temp_dirs(list(profiles.values()))", body)


class ExitCodeTests(unittest.TestCase):
    def test_the_codes_are_distinct_and_non_zero(self):
        self.assertNotEqual(fid.EXIT_NO_BROWSER, fid.EXIT_HALTED)
        self.assertTrue(fid.EXIT_NO_BROWSER and fid.EXIT_HALTED)

    def test_a_halted_batch_does_not_exit_zero(self):
        # It has a report, and the report covers only what was measured before
        # the browser gave up; exiting 0 would let a caller read a partial
        # corpus as a whole one.
        with open(fid.__file__, encoding="utf-8") as fh:
            src = fh.read()
        self.assertIn('return EXIT_HALTED if halt["why"] else 0', src)

class ReportAssemblyTests(unittest.TestCase):
    def setUp(self):
        self.rec = {"id": "r/a", "label": "a", "window": "main", "score": 1.0,
                    "clippedCount": 0, "screen": [200, 120],
                    "windowRects": [[10, 10, 160, 90]],
                    "texts": [{"class": "label|label|text", "status": "both",
                               "dx": 1, "dy": 0, "wr": 1.0, "clipped": False}],
                    "fills": [], "presence": []}
        self.report = fid.assemble_report([self.rec], [{"id": "r/b",
                                                        "skipped": "a modal"}],
                                          "page.html", "/br", 2)

    def test_the_report_carries_its_schema_and_its_counts(self):
        self.assertEqual(self.report["schema"], fid.FIDELITY_SCHEMA)
        self.assertEqual(self.report["captures"], 2)
        self.assertEqual(self.report["measured"], 1)
        self.assertEqual(self.report["skippedCount"], 1)

    def test_the_report_is_json_serialisable(self):
        json.loads(json.dumps(self.report))

    def test_the_page_is_one_self_contained_file(self):
        html = fid.render_report_html(self.report, [])
        self.assertTrue(html.startswith("<!doctype html>"))
        self.assertNotIn("<script src=", html)
        self.assertNotIn('<link rel="stylesheet"', html)

    def test_the_skipped_captures_and_their_reasons_are_on_the_page(self):
        html = fid.render_report_html(self.report, [])
        self.assertIn("a modal", html)
        self.assertIn("r/b", html)

    def test_the_per_window_and_per_class_tables_are_both_rendered(self):
        html = fid.render_report_html(self.report, [])
        self.assertIn("per window", html)
        self.assertIn("per control class", html)
        self.assertIn("label|label|text", html)

    def test_a_triple_is_inlined_as_its_own_image_payload(self):
        frame = painted(200, 120, (68, 68, 68), [(20, 20, 40, 10, INK)])
        mirror = painted(200, 120, (68, 68, 68), [(24, 20, 40, 10, INK)])
        box = (10, 10, 160, 90)
        imgs = [fid._img_tag(fid._crop_png(frame, box), "the game frame"),
                fid._img_tag(fid._crop_png(mirror, box), "the mirror"),
                fid._img_tag(fid._heatmap_png(frame, mirror, box), "the diff")]
        html = fid.render_report_html(self.report, [(self.rec, imgs)])
        self.assertEqual(html.count("data:image/png;base64,"), 3)
        self.assertIn("the game frame", html)

    def test_the_heatmap_is_a_decodable_png_of_the_rect(self):
        frame = painted(60, 40, (68, 68, 68), [(10, 10, 20, 8, INK)])
        mirror = solid(60, 40, (68, 68, 68))
        blob = fid._heatmap_png(frame, mirror, (5, 5, 40, 30))
        tmp = tempfile.mkdtemp()
        try:
            p = os.path.join(tmp, "h.png")
            with open(p, "wb") as fh:
                fh.write(blob)
            w, h, bpp, px = gmi.read_png(p)
            self.assertEqual((w, h), (40, 30))
            # ink the frame has and the mirror lacks lands in the RED channel
            o = ((12 - 5) * w + (15 - 5)) * bpp
            self.assertGreater(px[o], 100)
            self.assertEqual(px[o + 1], 0)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    def test_the_heatmap_marks_the_mirrors_own_extra_ink_green(self):
        frame = solid(60, 40, (68, 68, 68))
        mirror = painted(60, 40, (68, 68, 68), [(10, 10, 20, 8, INK)])
        blob = fid._heatmap_png(frame, mirror, (5, 5, 40, 30))
        tmp = tempfile.mkdtemp()
        try:
            p = os.path.join(tmp, "h.png")
            with open(p, "wb") as fh:
                fh.write(blob)
            w, _h, bpp, px = gmi.read_png(p)
            o = ((12 - 5) * w + (15 - 5)) * bpp
            self.assertEqual(px[o], 0)
            self.assertGreater(px[o + 1], 100)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


# --------------------------------------------------------------------------
# bare mode in the generated page
# --------------------------------------------------------------------------

class BareModeTests(unittest.TestCase):
    def test_the_deep_link_is_in_the_generated_page(self):
        self.assertIn("function bootBare()", gmi.JS)
        self.assertIn("q.bare !== '1'", gmi.JS)
        self.assertIn("dataset.ready", gmi.JS)
        self.assertIn("body.bare", gmi.BARE_CSS)

    def test_boot_consults_bare_mode_before_anything_else(self):
        # One added line, and it is the first statement of boot(): everything
        # bare mode does is downstream of a hash the ordinary reader does not
        # have.
        body = gmi.JS.split("function boot(){", 1)[1]
        first = [ln.strip() for ln in body.splitlines() if ln.strip()][0]
        self.assertEqual(first, "if (bootBare()) return;")

    def test_every_bare_rule_is_scoped_to_the_bare_class(self):
        # This is what makes "the page without the hash is unchanged" mechanical.
        # A selector that is not scoped would restyle the page every reader opens.
        css = re.sub(r"/\*.*?\*/", "", gmi.BARE_CSS, flags=re.S)
        self.assertGreater(len(re.findall(r"\{", css)), 4,
                           "no bare rules were found to check")
        self.assertEqual(fid.unscoped_bare_selectors(gmi.BARE_CSS), [],
                         "the bare skin restyles the ordinary page")

    def test_a_selector_that_merely_mentions_bare_does_not_pass(self):
        # The substring test this replaced let both of these through, and both
        # restyle the page a reader opens without the hash.
        self.assertEqual(
            fid.unscoped_bare_selectors("body:not(.bare) .stagewrap{color:red}"),
            ["body:not(.bare) .stagewrap"])
        self.assertEqual(
            fid.unscoped_bare_selectors(".stagewrap:not(.barely){color:red}"),
            [".stagewrap:not(.barely)"])

    def test_a_longer_class_name_does_not_pass_as_the_bare_one(self):
        self.assertEqual(fid.unscoped_bare_selectors("body.barely .x{color:red}"),
                         ["body.barely .x"])

    def test_the_two_scoped_prefixes_pass_and_every_selector_in_a_list_is_checked(self):
        self.assertEqual(fid.unscoped_bare_selectors(
            'body.bare #top,html[data-ready="1"] body.bare .stagewrap{display:none}'),
            [])
        self.assertEqual(fid.unscoped_bare_selectors(
            "body.bare #top,.stagewrap{display:none}"), [".stagewrap"])

    def test_a_comment_mentioning_a_selector_is_not_a_selector(self):
        self.assertEqual(fid.unscoped_bare_selectors(
            "/* .stagewrap is hidden */ body.bare .stagewrap{display:none}"), [])

    def test_the_ordinary_stylesheet_carries_no_bare_mode_rule(self):
        # Comments stripped: the word appears in prose about a groove that stays
        # bare when nothing could be measured, and prose changes nothing.
        css = re.sub(r"/\*.*?\*/", "", gmi.CSS, flags=re.S)
        self.assertNotIn("bare", css)

    def test_the_bare_skin_is_appended_to_the_page_after_the_ordinary_one(self):
        model = _tiny_model()
        html = gmi.render_html(model)
        self.assertIn("body.bare", html)
        self.assertLess(html.index(".stagewrap{position:relative"),
                        html.index("body.bare"))

    def test_the_stage_paints_only_once_the_page_marks_itself_ready(self):
        # The ready gate, which is why the instrument needs no second browser
        # call to read a DOM attribute: an early screenshot is blank, and blank
        # is refused.
        self.assertIn("body.bare .stagewrap{visibility:hidden}", gmi.BARE_CSS)
        self.assertIn('html[data-ready="1"] body.bare .stagewrap', gmi.BARE_CSS)

    def test_bare_mode_leaves_the_modal_photograph_out(self):
        # The modal block inlines a photograph of the whole frame. Measuring the
        # page against the frame while the page is showing the frame would
        # measure nothing at all.
        self.assertIn("opts.dialog !== false", gmi.JS)
        self.assertIn("dialog: false", gmi.JS)

    def test_the_stage_is_pinned_to_the_frame_the_dump_was_taken_at(self):
        bare = gmi.JS.split("function bootBare()", 1)[1].split("\nfunction ", 1)[0]
        self.assertIn("cap.screen[0]", bare)
        self.assertIn("cap.screen[1]", bare)

    def test_a_capture_the_page_does_not_have_is_an_error_not_a_blank(self):
        bare = gmi.JS.split("function bootBare()", 1)[1].split("\nfunction ", 1)[0]
        self.assertIn("dataset.error", bare)
        self.assertIn("dataset.ready = '0'", bare)


def _tiny_model():
    """The smallest page model `render_html` accepts, so the bare-mode cells can
    check the generated file without a census corpus."""
    return {
        "schema": gmi.MIRROR_SCHEMA, "seamWindows": ["main"], "titlePrefix": "",
        "clickKinds": list(gmi.CLICK_KINDS), "seamOps": list(gmi.SEAM_VERBS),
        "closeOp": gmi.VERB_CLOSE, "fixtures": [{"key": "fix", "specIds": ["S"]}],
        "windows": [{"token": "main", "titles": [], "tabs": [],
                     "captureCount": 1}],
        "modes": ["advanced"],
        "captures": [{"id": "r/a", "label": "a", "runId": "r", "specId": "S",
                      "fixture": "fix", "window": "main", "tab": None,
                      "tabNames": [], "tabAlias": None, "scene": "SPACECENTER",
                      "mode": "advanced", "state": "", "capturedUtc": "",
                      "screen": [1280, 720], "openWindows": ["main"],
                      "dialog": None, "roots": [], "photo": None, "counts": {}}],
        "keys": {}, "missing": [], "notes": {}, "photoBytes": 0,
    }


# --------------------------------------------------------------------------
# the standing rules
# --------------------------------------------------------------------------

class NoCommittedImagesTests(unittest.TestCase):
    """The design tooling is code only: no image file and no inlined image
    payload is committed anywhere under `harness/` or `docs/`.

    The instrument produces screenshots, crops and heatmaps by the hundred; every
    one of them belongs in a scratch folder. This cell is the mechanical form of
    that rule, so it cannot be forgotten in a later commit.
    """

    EXTS = (".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".ico",
            ".tif", ".tiff")
    DIRS = ("harness/", "docs/")
    PAYLOAD = re.compile(r"data:image/[a-z.+-]+;base64,[A-Za-z0-9+/=]{200,}")

    def _tracked(self):
        try:
            out = subprocess.run(["git", "ls-files"], cwd=REPO,
                                 capture_output=True, timeout=120)
        except (OSError, subprocess.SubprocessError):
            self.skipTest("git is not available here")
        if out.returncode != 0:
            self.skipTest("git ls-files failed: %r" % out.stderr[-200:])
        names = out.stdout.decode("utf-8", "replace").splitlines()
        if not names:
            self.skipTest("git ls-files listed nothing")
        return [n for n in names if n.startswith(self.DIRS)]

    def test_no_tracked_image_file_lives_under_harness_or_docs(self):
        bad = [n for n in self._tracked() if n.lower().endswith(self.EXTS)]
        self.assertEqual(bad, [], "image files are committed: %s" % bad)

    def test_no_tracked_file_carries_an_inlined_image_payload(self):
        bad = []
        for name in self._tracked():
            path = os.path.join(REPO, name)
            try:
                if os.path.getsize(path) > 4 * 1024 * 1024:
                    continue
                with open(path, "r", encoding="utf-8", errors="ignore") as fh:
                    text = fh.read()
            except OSError:
                continue
            if self.PAYLOAD.search(text):
                bad.append(name)
        self.assertEqual(bad, [],
                         "inlined image payloads are committed: %s" % bad)


class NoTypedWindowTextTests(unittest.TestCase):
    """The mirror's whole claim is that nothing about a window is typed into the
    tooling. The instrument reads the same captures, so the same rule binds it:
    it may know about kinds, styles and metrics, and nothing about what a Parsek
    window says.
    """

    def test_the_instrument_types_no_window_text_of_its_own(self):
        with open(fid.__file__, encoding="utf-8") as fh:
            src = fh.read()
        # The strings the instrument is entitled to: control KINDS and STYLES
        # (the dump's own vocabulary, and what the per-class table is keyed by),
        # the seam's ops and the complexity modes.
        exempt = set(gmi.SEAM_VERBS) | set(gmi.MODE_TOKENS) | set(gmi.CLICK_KINDS)
        exempt |= set(gmi.BG_KINDS) | {"label", "box", "window", "scrollview",
                                       "layoutgroup", "toggle", "slider"}
        # Every string any capture in the repo's own excerpt carries. There is no
        # census corpus in the repository, so the needles come from the one
        # recorded excerpt the mirror's own tests keep.
        import test_gui_mirror as tgm
        needles = set()

        def walk(node):
            for key in ("text", "textValue", "tooltip"):
                if node.get(key):
                    needles.add(node[key])
            for ch in node.get("children") or ():
                walk(ch)

        for root in tgm.REAL_EXCERPT.get("roots") or ():
            walk(root)
        self.assertGreater(len(needles), 8, "no strings to check")
        for text in sorted(needles):
            n = gmi.norm(text)
            if len(n) < 4 or n in exempt:
                continue
            for quoted in ("'%s'" % text, '"%s"' % text):
                self.assertNotIn(quoted, src,
                                 "%r is typed into the instrument" % text)

    def test_the_instrument_hard_codes_no_window_geometry(self):
        # A per-window rect, column width or row height typed here would make the
        # measurement agree with the page for the wrong reason. The only numeric
        # constants allowed are the pixel thresholds, each with the measurement
        # that justifies it beside it.
        with open(fid.__file__, encoding="utf-8") as fh:
            src = fh.read()
        consts = re.findall(r"^([A-Z_]+) = (\d+)$", src, re.M)
        self.assertEqual(sorted(k for k, _v in consts),
                         ["INK_INSET", "INK_THRESHOLD", "MAX_BROWSER_PATH",
                          "MIN_INK_PIXELS", "MIN_NODE_SIDE",
                          "WINDOW_INK_INSET"])


if __name__ == "__main__":
    unittest.main()
