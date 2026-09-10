#!/usr/bin/env python3
"""Unit tests for the offline GUI-tree viewer (`gui_tree_view.py`).

The viewer's whole job is to make a dump readable by someone who cannot see the
game, so the cells that matter are the ones about NOT LYING: a malformed dump must
produce a page that says so, an unknown control kind must still draw, a dump whose
Harmony interceptions were bypassed must be flagged rather than looking complete,
and a control labelled `</script>` must not be able to break out of the page.
"""

import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import gui_tree_view as gtv  # noqa: E402


def dump(roots=None, **extra):
    body = {
        "schema": gtv.SCHEMA_ID,
        "label": "probe",
        "capturedUtc": "2026-09-10T10:11:12Z",
        "frame": 42,
        "screen": {"width": 1600, "height": 900},
        "screenshotHint": "probe.png",
        "counts": {"windows": 1, "nodes": 2, "events": 4, "strayEnds": 0,
                   "autoClosedByClip": 0, "autoClosedByEnd": 0,
                   "autoClosedByRect": 0, "unclosedAtEnd": 0,
                   "recordFaults": 0, "droppedOverCap": 0},
        "funnels": [{"name": "GUI.DoLabel", "patched": True, "hits": 3}],
        "roots": roots if roots is not None else [],
    }
    body.update(extra)
    return body


def node(kind="label", rect=(0, 0, 10, 10), children=None, **extra):
    body = {"kind": kind, "rect": list(rect), "localRect": list(rect),
            "clipDepth": 1, "style": kind, "enabled": True, "text": kind,
            "children": children or []}
    body.update(extra)
    return body


class HtmlEscapeTests(unittest.TestCase):
    def test_the_five_dangerous_characters_are_escaped(self):
        self.assertEqual("&lt;b&gt;", gtv.html_escape("<b>"))
        self.assertEqual("&amp;amp;", gtv.html_escape("&amp;"))
        self.assertEqual("&quot;x&quot;", gtv.html_escape('"x"'))
        self.assertEqual("&#x27;", gtv.html_escape("'"))

    def test_none_becomes_empty_not_the_word_none(self):
        self.assertEqual("", gtv.html_escape(None))

    def test_non_strings_are_stringified(self):
        self.assertEqual("42", gtv.html_escape(42))
        self.assertEqual("True", gtv.html_escape(True))


class KindColorTests(unittest.TestCase):
    def test_every_schema_kind_has_its_own_colour(self):
        kinds = ["window", "group", "scrollview", "layoutgroup", "label", "box",
                 "button", "repeatbutton", "toggle", "textfield", "buttongrid",
                 "slider", "control"]
        colours = [gtv.kind_color(k) for k in kinds]
        self.assertEqual(len(kinds), len(set(colours)),
                         "two kinds share a colour, so the overlay cannot be read")
        for colour in colours:
            self.assertNotEqual(gtv.FALLBACK_COLOR, colour)

    def test_kind_matching_is_case_insensitive(self):
        self.assertEqual(gtv.kind_color("button"), gtv.kind_color("Button"))

    def test_an_unknown_kind_still_gets_a_colour(self):
        # A newer mod build may emit a kind this viewer predates. It must draw in
        # the fallback colour, never be dropped - a missing box reads as a missing
        # control.
        self.assertEqual(gtv.FALLBACK_COLOR, gtv.kind_color("wysiwyg"))
        self.assertEqual(gtv.FALLBACK_COLOR, gtv.kind_color(None))
        self.assertEqual(gtv.FALLBACK_COLOR, gtv.kind_color(7))


class RectTests(unittest.TestCase):
    def test_a_well_formed_rect_becomes_floats(self):
        self.assertEqual((1.0, 2.0, 3.0, 4.0), gtv.rect_of(node(rect=(1, 2, 3, 4))))

    def test_a_degenerate_rect_is_not_drawable(self):
        self.assertIsNone(gtv.rect_of(node(rect=(1, 2, 0, 0))))
        self.assertIsNone(gtv.rect_of(node(rect=(1, 2, 100, 0.25))))

    def test_malformed_rects_are_refused_rather_than_guessed(self):
        for bad in (None, [], [1, 2, 3], [1, 2, 3, 4, 5], "1,2,3,4",
                    [1, 2, 3, "4"], [1, 2, 3, None]):
            self.assertIsNone(gtv.rect_of({"kind": "label", "rect": bad}))
        self.assertIsNone(gtv.rect_of("not a node"))

    def test_a_json_null_number_from_a_nan_rect_is_refused(self):
        # GuiTreeJson writes NaN / infinity as `null`, so this shape is reachable.
        self.assertIsNone(gtv.rect_of({"kind": "label", "rect": [None, 0, 10, 10]}))

    def test_booleans_are_not_numbers(self):
        self.assertIsNone(gtv.rect_of({"kind": "label", "rect": [True, 0, 10, 10]}))

    def test_describe_rect_prints_whole_numbers_without_decimals(self):
        self.assertEqual("[1, 2, 3, 4]", gtv.describe_rect((1.0, 2.0, 3.0, 4.0)))
        self.assertEqual("[1.50, 2, 3, 4]", gtv.describe_rect((1.5, 2.0, 3.0, 4.0)))
        self.assertEqual("-", gtv.describe_rect(None))


class NodeLabelTests(unittest.TestCase):
    def test_text_wins(self):
        self.assertEqual("Close", gtv.node_label(node(text="Close")))

    def test_a_text_field_falls_back_to_its_value(self):
        self.assertEqual("Munar Lander", gtv.node_label(
            {"kind": "textfield", "text": None, "textValue": "Munar Lander"}))

    def test_an_icon_only_control_falls_back_to_its_kind(self):
        self.assertEqual("button", gtv.node_label({"kind": "button", "text": None}))

    def test_a_whitespace_only_text_is_not_a_label(self):
        self.assertEqual("label", gtv.node_label({"kind": "label", "text": "   "}))


class FlattenTests(unittest.TestCase):
    def test_nesting_becomes_depth_and_parent_links_in_draw_order(self):
        tree = dump([node("window", (0, 0, 100, 100), [
            node("layoutgroup", (2, 2, 96, 96), [
                node("label", (4, 4, 40, 10), text="a"),
                node("button", (4, 16, 40, 10), text="b"),
            ]),
        ])])
        records = gtv.flatten(tree)
        self.assertEqual(4, len(records))
        self.assertEqual([0, 1, 2, 2], [r["depth"] for r in records])
        self.assertEqual([None, 0, 1, 1], [r["parent"] for r in records])
        self.assertEqual(["window", "layoutgroup", "label", "button"],
                         [r["kind"] for r in records])
        self.assertEqual([1], records[0]["children"])
        self.assertEqual([2, 3], records[1]["children"])
        self.assertEqual(["a", "b"], [records[2]["label"], records[3]["label"]])

    def test_two_roots_stay_two_roots(self):
        records = gtv.flatten(dump([node("window"), node("window")]))
        self.assertEqual([None, None], [r["parent"] for r in records])

    def test_a_dump_with_no_roots_flattens_to_nothing(self):
        self.assertEqual([], gtv.flatten(dump([])))
        self.assertEqual([], gtv.flatten({"schema": gtv.SCHEMA_ID}))
        self.assertEqual([], gtv.flatten(None))
        self.assertEqual([], gtv.flatten({"roots": "not a list"}))

    def test_a_non_dict_child_is_skipped_without_losing_its_siblings(self):
        tree = dump([node("window", (0, 0, 50, 50),
                          ["garbage", node("label", (1, 1, 5, 5), text="kept")])])
        records = gtv.flatten(tree)
        self.assertEqual(2, len(records))
        self.assertEqual("kept", records[1]["label"])

    def test_a_disabled_node_is_marked(self):
        records = gtv.flatten(dump([node("button", enabled=False)]))
        self.assertFalse(records[0]["enabled"])
        records = gtv.flatten(dump([node("button")]))
        self.assertTrue(records[0]["enabled"])


class ExtrasTests(unittest.TestCase):
    def test_a_toggle_shows_its_value(self):
        self.assertIn("value=true", gtv.extras_of({"value": True}))
        self.assertIn("value=false", gtv.extras_of({"value": False}))

    def test_a_layout_group_shows_its_orientation(self):
        self.assertIn("horizontal", gtv.extras_of({"horizontal": True}))
        self.assertIn("vertical", gtv.extras_of({"horizontal": False}))

    def test_ids_are_shown_and_booleans_are_not_mistaken_for_them(self):
        self.assertIn("windowId=7", gtv.extras_of({"windowId": 7}))
        self.assertNotIn("windowId", gtv.extras_of({"windowId": True}))

    def test_absent_fields_produce_nothing(self):
        self.assertEqual("", gtv.extras_of({}))
        self.assertEqual("", gtv.extras_of("not a node"))


class ScreenSizeTests(unittest.TestCase):
    def test_the_declared_size_is_used(self):
        self.assertEqual((1600, 900), gtv.screen_size(dump()))

    def test_a_missing_or_nonsense_block_falls_back(self):
        for bad in (None, {}, {"screen": None}, {"screen": {}},
                    {"screen": {"width": 0, "height": 900}},
                    {"screen": {"width": "1600", "height": 900}}):
            self.assertEqual((1920, 1080), gtv.screen_size(bad))


class HealthNoteTests(unittest.TestCase):
    def test_a_clean_dump_has_only_the_empty_tree_note(self):
        # A dump with no nodes is worth saying out loud even when everything else
        # is clean: it is the shape an un-armed or mistimed capture produces.
        self.assertEqual(["the dump contains no nodes at all"],
                         gtv.health_notes(dump([])))

    def test_a_clean_populated_dump_has_no_notes(self):
        self.assertEqual([], gtv.health_notes(dump([node("window")])))

    def test_a_wrong_schema_is_called_out(self):
        notes = gtv.health_notes(dump([node("window")], schema="parsek-gui-tree/99"))
        self.assertTrue(any("schema" in n for n in notes))

    def test_stray_ends_faults_drops_and_unclosed_are_each_reported(self):
        for key, needle in (("strayEnds", "matched no open container"),
                            ("recordFaults", "patch body fault"),
                            ("droppedOverCap", "dropped at the per-frame cap"),
                            ("unclosedAtEnd", "still open when the frame ended")):
            body = dump([node("window")])
            body["counts"][key] = 3
            notes = gtv.health_notes(body)
            self.assertTrue(any(needle in n for n in notes),
                            "%s went unreported: %s" % (key, notes))

    def test_recovery_closures_are_reported_as_one_note(self):
        body = dump([node("window")])
        body["counts"]["autoClosedByClip"] = 2
        body["counts"]["autoClosedByRect"] = 1
        notes = gtv.health_notes(body)
        hits = [n for n in notes if "recovery rules" in n]
        self.assertEqual(1, len(hits))
        self.assertIn("3 container(s)", hits[0])

    def test_an_unpatched_funnel_is_named(self):
        # THE note a supervising agent needs most: the dump parses and renders and
        # is missing a whole control kind.
        body = dump([node("window")])
        body["funnels"] = [{"name": "GUI.EndGroup", "patched": False, "hits": 0}]
        notes = gtv.health_notes(body)
        self.assertTrue(any("NOT PATCHED" in n and "GUI.EndGroup" in n for n in notes))

    def test_a_patched_but_never_hit_funnel_is_named_separately(self):
        body = dump([node("window")])
        body["funnels"] = [{"name": "GUI.DoToggle", "patched": True, "hits": 0}]
        notes = gtv.health_notes(body)
        self.assertTrue(any("never hit" in n and "GUI.DoToggle" in n for n in notes))

    def test_a_patched_and_hit_funnel_is_silent(self):
        self.assertEqual([], gtv.health_notes(dump([node("window")])))

    def test_a_non_object_dump_is_reported_not_raised(self):
        self.assertEqual(["the dump is not a JSON object"], gtv.health_notes([1, 2]))
        self.assertEqual(["the dump is not a JSON object"], gtv.health_notes(None))


class ScriptInliningTests(unittest.TestCase):
    def test_a_control_labelled_like_a_closing_tag_cannot_break_out(self):
        text = gtv.json_for_script([{"label": "</script><script>alert(1)"}])
        self.assertNotIn("</script>", text)
        self.assertNotIn("<", text)
        self.assertNotIn(">", text)
        # And it is still the same data once a JSON parser reads it back.
        self.assertEqual("</script><script>alert(1)",
                         json.loads(text)[0]["label"])

    def test_ampersands_survive_the_round_trip(self):
        self.assertEqual("a&b", json.loads(gtv.json_for_script("a&b")))

    def test_non_ascii_text_survives(self):
        # Escaped rather than literal so this file stays plain ASCII (house style);
        # the value under test is still a real non-ASCII character.
        degrees = "Kerbin \u00b0C"
        self.assertEqual(degrees, json.loads(gtv.json_for_script(degrees)))


class PathTests(unittest.TestCase):
    def test_the_output_sits_beside_the_dump(self):
        self.assertEqual(os.path.join("d", "probe.gui.html"),
                         gtv.output_path_for(os.path.join("d", "probe.gui.json")))

    def test_an_explicit_output_wins(self):
        self.assertEqual("out.html",
                         gtv.output_path_for("probe.gui.json", "out.html"))

    def test_a_file_not_named_by_the_convention_still_gets_a_page(self):
        self.assertEqual("odd.gui.html", gtv.output_path_for("odd.json"))

    def test_the_screenshot_is_found_by_stem(self):
        with tempfile.TemporaryDirectory() as tmp:
            js = os.path.join(tmp, "probe.gui.json")
            png = os.path.join(tmp, "probe.png")
            open(js, "w").close()
            self.assertIsNone(gtv.find_screenshot(js))
            with open(png, "wb") as fh:
                fh.write(b"\x89PNG")
            self.assertEqual(png, gtv.find_screenshot(js))

    def test_a_jpg_is_accepted_too(self):
        with tempfile.TemporaryDirectory() as tmp:
            js = os.path.join(tmp, "probe.gui.json")
            jpg = os.path.join(tmp, "probe.jpg")
            open(js, "w").close()
            with open(jpg, "wb") as fh:
                fh.write(b"\xff\xd8")
            self.assertEqual(jpg, gtv.find_screenshot(js))


class EmbedTests(unittest.TestCase):
    def test_an_image_becomes_a_data_uri(self):
        with tempfile.TemporaryDirectory() as tmp:
            png = os.path.join(tmp, "a.png")
            with open(png, "wb") as fh:
                fh.write(b"\x89PNG\r\n")
            uri = gtv.embed_image(png)
            self.assertTrue(uri.startswith("data:image/png;base64,"))

    def test_a_missing_empty_or_absent_image_is_none_not_an_error(self):
        self.assertIsNone(gtv.embed_image(None))
        with tempfile.TemporaryDirectory() as tmp:
            self.assertIsNone(gtv.embed_image(os.path.join(tmp, "nope.png")))
            empty = os.path.join(tmp, "empty.png")
            open(empty, "wb").close()
            self.assertIsNone(gtv.embed_image(empty))


class RenderTests(unittest.TestCase):
    def test_the_page_is_self_contained(self):
        page = gtv.render_page(dump([node("window", (10, 20, 100, 50))]))
        self.assertIn("<!doctype html>", page)
        self.assertIn("<style>", page)
        self.assertIn("<script>", page)
        for forbidden in ("http://", "https://", "cdn.", "<link"):
            self.assertNotIn(forbidden, page,
                             "the page reaches outside itself for %r" % forbidden)

    def test_boxes_are_positioned_as_percentages_of_the_capture_frame(self):
        # Percentages rather than pixels so the overlay tracks the screenshot at
        # whatever size the browser renders it.
        page = gtv.render_page(dump([node("window", (160, 90, 320, 180))]))
        self.assertIn("left:10.0000%", page)
        self.assertIn("top:10.0000%", page)
        self.assertIn("width:20.0000%", page)
        self.assertIn("height:20.0000%", page)

    def test_a_degenerate_node_is_in_the_tree_but_not_on_the_overlay(self):
        page = gtv.render_page(dump([
            node("window", (0, 0, 100, 100), [
                node("label", (5, 5, 0, 0), text="zero-size-carrier")])]))
        self.assertIn("zero-size-carrier", page)
        self.assertEqual(1, page.count('class="box container"')
                         + page.count('class="box"'))

    def test_node_text_is_escaped_in_both_the_overlay_and_the_tree(self):
        page = gtv.render_page(dump([node("button", (0, 0, 10, 10),
                                          text='<img src=x onerror=alert(1)>')]))
        self.assertNotIn("<img", page)
        self.assertIn("&lt;img", page)

    def test_a_tooltip_and_a_style_reach_the_tree_row(self):
        page = gtv.render_page(dump([node("toggle", (0, 0, 10, 10),
                                          text="Show ghosts", style="toggle",
                                          tooltip="Draw committed ghosts")]))
        self.assertIn("Draw committed ghosts", page)
        self.assertIn("style=toggle", page)

    def test_a_disabled_node_is_marked_in_the_page(self):
        page = gtv.render_page(dump([node("button", (0, 0, 10, 10), enabled=False)]))
        self.assertIn("DISABLED", page)
        self.assertIn("box disabled", page)

    def test_health_notes_are_rendered_where_they_cannot_be_missed(self):
        body = dump([node("window")])
        body["funnels"] = [{"name": "GUI.EndGroup", "patched": False, "hits": 0}]
        page = gtv.render_page(body)
        self.assertIn('class="notes"', page)
        self.assertIn("GUI.EndGroup", page)
        self.assertLess(page.index("GUI.EndGroup"), page.index('class="split"'))

    def test_a_parse_error_renders_a_page_that_says_so(self):
        page = gtv.render_page(None, error="probe.gui.json is not valid JSON: x")
        self.assertIn("not valid JSON", page)
        self.assertIn("<!doctype html>", page)

    def test_the_no_screenshot_case_says_so_instead_of_a_broken_image(self):
        page = gtv.render_page(dump([node("window")]))
        self.assertIn("no screenshot beside this dump", page)
        self.assertNotIn("<img", page)

    def test_the_screenshot_is_inlined_when_present(self):
        page = gtv.render_page(dump([node("window")]),
                               image_uri="data:image/png;base64,AAA")
        self.assertIn('src="data:image/png;base64,AAA"', page)
        self.assertNotIn("no screenshot beside", page)

    def test_the_header_carries_the_capture_metadata(self):
        page = gtv.render_page(dump([node("window")]))
        self.assertIn("probe", page)
        self.assertIn("screen 1600x900", page)
        self.assertIn("2026-09-10T10:11:12Z", page)

    def test_the_node_payload_is_inlined_for_the_script(self):
        page = gtv.render_page(dump([node("window", (1, 2, 3, 4))]))
        start = page.index('id="nodes"')
        blob = page[page.index(">", start) + 1:page.index("</script>", start)]
        payload = json.loads(blob)
        self.assertEqual(1, len(payload))
        self.assertEqual([1.0, 2.0, 3.0, 4.0], payload[0]["rect"])
        self.assertTrue(payload[0]["container"])


class IndexTests(unittest.TestCase):
    def test_an_index_row_summarises_a_good_dump(self):
        entry = gtv.index_entry("a/probe.gui.json", "a/probe.gui.html",
                                dump([node("window")]))
        self.assertEqual("probe.gui.html", entry["href"])
        self.assertEqual("probe", entry["label"])
        self.assertIn("notes=0", entry["detail"])

    def test_an_index_row_carries_the_parse_error(self):
        entry = gtv.index_entry("a/bad.gui.json", "a/bad.gui.html", None,
                                error="not valid JSON")
        self.assertEqual("bad.gui.json", entry["label"])
        self.assertEqual("not valid JSON", entry["detail"])

    def test_the_index_page_is_self_contained_and_escapes_labels(self):
        page = gtv.render_index([
            {"href": "a.gui.html", "label": "<b>x</b>", "detail": "d"}])
        self.assertNotIn("<b>x</b>", page)
        self.assertIn("&lt;b&gt;", page)
        self.assertNotIn("https://", page)


class ShellTests(unittest.TestCase):
    def test_writing_a_page_end_to_end(self):
        with tempfile.TemporaryDirectory() as tmp:
            js = os.path.join(tmp, "probe.gui.json")
            with open(js, "w", encoding="utf-8") as fh:
                json.dump(dump([node("window", (0, 0, 100, 100), [
                    node("label", (2, 2, 40, 10), text="hello")])]), fh)
            out, parsed, error = gtv.write_page(js)
            self.assertIsNone(error)
            self.assertTrue(os.path.isfile(out))
            with open(out, encoding="utf-8") as fh:
                page = fh.read()
            self.assertIn("hello", page)

    def test_a_truncated_dump_still_produces_a_page(self):
        with tempfile.TemporaryDirectory() as tmp:
            js = os.path.join(tmp, "trunc.gui.json")
            with open(js, "w", encoding="utf-8") as fh:
                fh.write('{"schema": "parsek-gui-tree/1", "roots": [')
            out, parsed, error = gtv.write_page(js)
            self.assertIsNotNone(error)
            with open(out, encoding="utf-8") as fh:
                self.assertIn("not valid JSON", fh.read())

    def test_batch_mode_writes_one_page_per_dump_plus_an_index(self):
        with tempfile.TemporaryDirectory() as tmp:
            for name in ("a", "b"):
                with open(os.path.join(tmp, name + ".gui.json"), "w",
                          encoding="utf-8") as fh:
                    json.dump(dump([node("window")], label=name), fh)
            rc = gtv.main(["--batch", tmp])
            self.assertEqual(0, rc)
            for name in ("a.gui.html", "b.gui.html", "index.html"):
                self.assertTrue(os.path.isfile(os.path.join(tmp, name)), name)
            with open(os.path.join(tmp, "index.html"), encoding="utf-8") as fh:
                index = fh.read()
            self.assertIn("a.gui.html", index)
            self.assertIn("b.gui.html", index)

    def test_batch_mode_on_an_empty_directory_is_not_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertEqual(0, gtv.main(["--batch", tmp]))
            self.assertFalse(os.path.isfile(os.path.join(tmp, "index.html")))

    def test_no_argument_at_all_is_a_usage_error(self):
        with self.assertRaises(SystemExit):
            gtv.main([])

    def test_output_with_batch_is_refused(self):
        with self.assertRaises(SystemExit):
            gtv.main(["--batch", ".", "-o", "x.html"])


if __name__ == "__main__":
    unittest.main()
