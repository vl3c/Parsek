#!/usr/bin/env python3
"""Unit tests for the GUI mirror generator (`harness/tools/gui_mirror.py`).

The mirror's whole claim is that it CANNOT drift from the game, because every
window, control, string and colour on the page comes out of a census artifact.
The cells that matter are therefore the ones about that claim holding:

  * the label grammar resolves to the (fixture, window, tab, state, mode) a lane
    actually flew, and the seam log overrides the label where they disagree;
  * a capture is never paired with, or shown in place of, a capture of something
    else - the dataset fallback says so out loud and the before/after key carries
    the scene;
  * an edge in the state graph exists only where the destination capture exists,
    so a click can never invent a screen;
  * a control whose text is markup cannot break out of the page;
  * the page fits its size budget, since a self-contained page that does not is
    not deliverable.

Lives under `lib/` rather than beside the tool because CI runs
`python -m unittest discover -s lib -q` and nothing discovers `tools/` - the same
placement as `lib/test_gui_tree_view.py` for `tools/gui_tree_view.py`.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import json
import os
import shutil
import sys
import tempfile
import unittest
import zlib

sys.path.insert(0, os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "tools"))

import gui_mirror as gmi  # noqa: E402


# --------------------------------------------------------------------------
# synthetic census artifacts (no game, no real run)
# --------------------------------------------------------------------------

def node(kind, rect, text=None, style=None, tooltip=None, children=None,
         enabled=True, value=None, text_value=None):
    n = {"kind": kind, "rect": list(rect), "localRect": list(rect),
         "clipDepth": 1, "style": style if style is not None else kind,
         "enabled": enabled, "text": text, "children": children or []}
    if tooltip is not None:
        n["tooltip"] = tooltip
    if value is not None:
        n["value"] = value
    if text_value is not None:
        n["textValue"] = text_value
    return n


def dump(label, window_title, kids, grid_value=None):
    roots = [node("window", [270, 8, 700, 400], window_title, style="window",
                  children=([node("buttongrid", [280, 43, 680, 21], None,
                                  style="button", text_value=grid_value)]
                            if grid_value else []) + kids)]
    return {"schema": gmi.TREE_SCHEMA, "label": label,
            "capturedUtc": "2026-09-11T05:49:25Z",
            "frame": 1, "screen": {"width": 1280, "height": 720},
            "screenshotHint": label + ".png",
            "guiMatrix": {"identity": True}, "counts": {}, "funnels": [],
            "roots": roots}


def tiny_png(path, w=1280, h=720, rgb=(68, 68, 68)):
    """A real PNG at the census frame size, so the colour sampler runs on it the
    way it does in production rather than on a stub."""
    raw = b"".join(b"\x00" + bytes(rgb) * w for _ in range(h))
    import struct

    def chunk(typ, body):
        c = typ + body
        return struct.pack(">I", len(body)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n"
                 + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 9))
                 + chunk(b"IEND", b""))


LOG_TWO_STATES = """
[LOG 00:00:01] [Parsek][INFO][TestCommands] uiaction complexity mode=advanced already=true
[LOG 00:00:02] [Parsek][INFO][TestCommands] uiaction open window=widgets open=true already=false frames=1
[LOG 00:00:03] [Parsek][INFO][TestCommands] uiaction rect window=widgets rect=270,8,700,400 frames=1
[LOG 00:00:04] [Parsek][INFO][TestCommands] uiaction describe scene=SPACECENTER complexity=advanced windows=2 open=1 openWindows=widgets
[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction tab window=widgets tab=zynthia index=0 already=true
[LOG 00:00:06] [Parsek][INFO][TestCommands] capturescreenshot ok label=syn-widgets-zynthia-advanced
[LOG 00:00:07] [Parsek][INFO][TestCommands] uiaction tab window=widgets tab=qorvex index=1 already=false
[LOG 00:00:08] [Parsek][INFO][TestCommands] capturescreenshot ok label=syn-widgets-qorvex-advanced
""".strip()


def make_shots(root, name="2026-09-11_0548_SYN-1-census-widgets_shots",
               log=LOG_TWO_STATES, with_png=True):
    path = os.path.join(root, name)
    os.makedirs(path)
    with open(os.path.join(path, "KSP.log"), "w", encoding="utf-8") as fh:
        fh.write(log + "\n")
    zynthia = dump("syn-widgets-zynthia-advanced", "Parsek - Widgets", [
        node("label", [284, 76, 190, 23], "Column", style="box"),
        node("button", [284, 115, 190, 21], "Qorvex", tooltip="Go to the Qorvex tab"),
        node("label", [478, 115, 220, 21], "zynthia row"),
    ], grid_value="Zynthia")
    qorvex = dump("syn-widgets-qorvex-advanced", "Parsek - Widgets", [
        node("label", [284, 76, 190, 23], "Column", style="box"),
        node("label", [284, 115, 190, 21], "qorvex row"),
    ], grid_value="Qorvex")
    for d in (zynthia, qorvex):
        with open(os.path.join(path, d["label"] + ".gui.json"), "w",
                  encoding="utf-8") as fh:
            json.dump(d, fh)
        if with_png:
            tiny_png(os.path.join(path, d["label"] + ".png"))
    return path


def make_scenarios(root, spec_id="SYN-1-census-widgets",
                   template="fixtures/saves/syn-fixture"):
    path = os.path.join(root, "scenarios")
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, spec_id + ".toml"), "w", encoding="utf-8") as fh:
        fh.write('[scenario]\nid = "%s"\n\n[fixture]\nsaveTemplate = "%s"\n'
                 % (spec_id, template))
    return path


# --------------------------------------------------------------------------


class LabelGrammarTests(unittest.TestCase):
    """`<host>-<window>[-<tab>][-<state>]-<mode>`, resolved against the
    vocabularies the seam log produced rather than against a typed table."""

    WINDOWS = {"main", "missions", "timeline", "kerbals", "career", "logistics",
               "structure", "settings", "spawncontrol", "gloops", "testrunner"}
    TABS = {"missions": {"missions", "recordings"},
            "kerbals": {"roster", "outcomes"},
            "career": {"contracts", "strategies", "facilities", "milestones"},
            "timeline": {"overview", "details", "rewindff", "refly"}}

    def parse(self, label):
        return gmi.parse_label(label, self.WINDOWS, self.TABS)

    def test_window_and_mode_only(self):
        self.assertEqual(
            self.parse("ksc-main-advanced"),
            {"host": "ksc", "window": "main", "tab": None, "state": "", "mode": "advanced"})

    def test_tab_is_taken_only_from_that_windows_vocabulary(self):
        got = self.parse("ksc-missions-recordings-advanced")
        self.assertEqual((got["window"], got["tab"], got["state"]),
                         ("missions", "recordings", ""))
        # `linkpicker` is not a Logistics tab, so it is a STATE, not a tab.
        got = self.parse("ib-logistics-linkpicker-advanced")
        self.assertEqual((got["window"], got["tab"], got["state"]),
                         ("logistics", None, "linkpicker"))

    def test_tab_then_multi_token_state(self):
        got = self.parse("play-career-contracts-sandbox-flight-advanced")
        self.assertEqual((got["host"], got["window"], got["tab"], got["state"],
                          got["mode"]),
                         ("play", "career", "contracts", "sandbox-flight", "advanced"))

    def test_multi_token_state_with_no_tab(self):
        got = self.parse("b1-main-disabledecho-spawncontrol-advanced")
        self.assertEqual((got["window"], got["tab"], got["state"]),
                         ("main", None, "disabledecho-spawncontrol"))

    def test_basic_mode_is_recognised_and_leaves_no_state(self):
        got = self.parse("ksc-missions-basic")
        self.assertEqual((got["window"], got["tab"], got["state"], got["mode"]),
                         ("missions", None, "", "basic"))

    def test_a_host_whose_second_token_is_no_window_keeps_it_as_state(self):
        # The dialog and map-scope lanes: `dlg-*` and `scope-*` name no window,
        # and guessing one would file a modal under a window it never stood over.
        got = self.parse("dlg-wipemilestones")
        self.assertEqual((got["host"], got["window"], got["state"], got["mode"]),
                         ("dlg", None, "wipemilestones", None))
        got = self.parse("scope-map-all-hidden")
        self.assertEqual((got["host"], got["window"], got["state"]),
                         ("scope", None, "map-all-hidden"))

    def test_no_vocabulary_falls_back_to_position(self):
        got = gmi.parse_label("ksc-newwindow-newtab-advanced")
        self.assertEqual((got["window"], got["tab"], got["state"], got["mode"]),
                         ("newwindow", None, "newtab", "advanced"))

    def test_empty_label_is_survivable(self):
        got = gmi.parse_label("")
        self.assertEqual(got["window"], None)
        self.assertEqual(got["mode"], None)


class LogReplayTests(unittest.TestCase):
    """The seam log, not the label, is the authority for what was on screen."""

    def test_window_tab_mode_and_scene_come_from_the_log(self):
        rep = gmi.parse_ksp_log(LOG_TWO_STATES)
        caps = rep["captures"]
        self.assertEqual(sorted(caps), ["syn-widgets-qorvex-advanced",
                                        "syn-widgets-zynthia-advanced"])
        a = caps["syn-widgets-zynthia-advanced"]
        self.assertEqual((a["window"], a["tab"], a["mode"], a["scene"]),
                         ("widgets", "zynthia", "advanced", "SPACECENTER"))
        self.assertEqual(caps["syn-widgets-qorvex-advanced"]["tab"], "qorvex")

    def test_the_tab_vocabulary_is_collected_with_its_indices(self):
        rep = gmi.parse_ksp_log(LOG_TWO_STATES)
        self.assertEqual(rep["tabs"]["widgets"], [("zynthia", 0), ("qorvex", 1)])

    def test_the_applied_rect_is_remembered_per_window(self):
        rep = gmi.parse_ksp_log(LOG_TWO_STATES)
        self.assertEqual(rep["captures"]["syn-widgets-zynthia-advanced"]["rects"],
                         {"widgets": [270, 8, 700, 400]})

    def test_a_standing_dialog_is_read_with_its_buttons(self):
        # A PopupDialog is stock uGUI and appears in NO control tree, so the
        # seam's own report is the only record of its title and buttons.
        log = ("uiaction dialog open=true count=1 name=ParsekWipeMilestonesConfirm "
               "title=Confirm: Wipe Milestones nbuttons=2 buttons=Wipe All|Cancel\n"
               "capturescreenshot ok label=dlg-wipemilestones\n"
               "uiaction dialog open=false count=0\n"
               "capturescreenshot ok label=dlg-teardown-no-modal\n")
        caps = gmi.parse_ksp_log(log)["captures"]
        dlg = caps["dlg-wipemilestones"]["dialog"]
        self.assertEqual(dlg["name"], "ParsekWipeMilestonesConfirm")
        self.assertEqual(dlg["title"], "Confirm: Wipe Milestones")
        self.assertEqual(dlg["buttons"], ["Wipe All", "Cancel"])
        self.assertIsNone(caps["dlg-teardown-no-modal"]["dialog"])

    def test_a_log_with_nothing_in_it_yields_nothing(self):
        self.assertEqual(gmi.parse_ksp_log("")["captures"], {})
        self.assertEqual(gmi.parse_ksp_log(None)["captures"], {})


class DatasetFallbackTests(unittest.TestCase):
    """The selected fixture's capture when it exists, else the nearest one in the
    declared order - and the caller is always told which happened, because a page
    that silently swaps datasets is a page that lies."""

    ORDER = ["c1-gui", "bdock-recorded", "career-earned-ksc"]

    def test_exact_fixture_wins_and_reports_exact(self):
        cap, exact = gmi.choose_capture(
            {"c1-gui": "A", "bdock-recorded": "B"}, "c1-gui", self.ORDER)
        self.assertEqual((cap, exact), ("A", True))

    def test_missing_fixture_falls_back_in_declared_order_and_says_so(self):
        cap, exact = gmi.choose_capture(
            {"bdock-recorded": "B", "career-earned-ksc": "C"}, "c1-gui", self.ORDER)
        self.assertEqual((cap, exact), ("B", False))

    def test_a_fixture_outside_the_order_is_still_reachable(self):
        cap, exact = gmi.choose_capture({"zz-other": "Z"}, "c1-gui", self.ORDER)
        self.assertEqual((cap, exact), ("Z", False))

    def test_no_capture_at_all_is_not_an_exception(self):
        self.assertEqual(gmi.choose_capture({}, "c1-gui", self.ORDER), (None, False))


class KeyIdentityTests(unittest.TestCase):
    def test_scene_is_part_of_the_before_after_key(self):
        # The same window at the Space Center and in flight is two pictures, not
        # a change; pairing them would report every scene difference as one.
        ksc = gmi.key_of("c1-gui", "main", None, "", "advanced", "SPACECENTER")
        flight = gmi.key_of("c1-gui", "main", None, "", "advanced", "FLIGHT")
        self.assertNotEqual(ksc, flight)

    def test_the_key_is_stable_for_the_same_five_facets(self):
        self.assertEqual(
            gmi.key_of("f", "w", "t", "s", "m", "SC"),
            gmi.key_of("f", "w", "t", "s", "m", "SC"))


class StateGraphTests(unittest.TestCase):
    """An edge exists only where the destination capture exists."""

    def caps(self):
        return [
            {"id": "1", "window": "kerbals", "tab": "roster", "state": "",
             "mode": "advanced", "fixture": "bd"},
            {"id": "2", "window": "kerbals", "tab": "outcomes", "state": "",
             "mode": "advanced", "fixture": "bd"},
            {"id": "3", "window": "kerbals", "tab": "roster", "state": "expanded",
             "mode": "advanced", "fixture": "bd"},
            # a different fixture and a different mode: NOT reachable from 1
            {"id": "4", "window": "kerbals", "tab": "roster", "state": "",
             "mode": "advanced", "fixture": "other"},
            {"id": "5", "window": "kerbals", "tab": "roster", "state": "",
             "mode": "basic", "fixture": "bd"},
        ]

    def test_tab_and_state_edges_exist_within_one_fixture_and_mode(self):
        edges = gmi.state_graph_edges(self.caps())
        outgoing = {(e["to"], e["kind"]) for e in edges if e["from"] == "1"}
        self.assertIn(("2", "tab"), outgoing)
        self.assertIn(("3", "state"), outgoing)

    def test_no_edge_crosses_a_fixture_or_a_mode(self):
        edges = gmi.state_graph_edges(self.caps())
        outgoing = {e["to"] for e in edges if e["from"] == "1"}
        self.assertNotIn("4", outgoing, "an edge crossed the dataset")
        self.assertNotIn("5", outgoing, "an edge crossed the complexity mode")

    def test_a_window_with_one_capture_has_no_edges(self):
        one = [{"id": "1", "window": "settings", "tab": None, "state": "",
                "mode": "advanced", "fixture": "bd"}]
        self.assertEqual(gmi.state_graph_edges(one), [])


class EscapingTests(unittest.TestCase):
    """A control whose text is markup must not be able to break out of the page.
    Every string the mirror shows is a control's own text, so this is the whole
    of the page's safety."""

    NASTY = '</script><img src=x onerror=alert(1)>&"\'</style>'

    def test_esc_covers_text_and_attribute_contexts(self):
        got = gmi.esc(self.NASTY)
        for ch in ("<", ">", '"', "'"):
            self.assertNotIn(ch, got, "raw %r survived esc()" % ch)
        self.assertIn("&amp;", got)
        self.assertEqual(gmi.esc(None), "")

    def test_the_inlined_json_cannot_close_its_own_script_block(self):
        blob = gmi.json_for_script({"t": self.NASTY})
        self.assertNotIn("<", blob)
        self.assertNotIn(">", blob)
        self.assertNotIn("</script", blob.lower())
        # and it is still the same string once parsed
        self.assertEqual(json.loads(blob)["t"], self.NASTY)

    def test_a_nasty_control_text_survives_a_whole_render_unescaped_nowhere(self):
        root = tempfile.mkdtemp()
        try:
            shots = make_shots(root)
            with open(os.path.join(shots, "syn-widgets-zynthia-advanced.gui.json"),
                      encoding="utf-8") as fh:
                d = json.load(fh)
            d["roots"][0]["children"][-1]["text"] = self.NASTY
            d["roots"][0]["children"][-1]["tooltip"] = self.NASTY
            with open(os.path.join(shots, "syn-widgets-zynthia-advanced.gui.json"), "w",
                      encoding="utf-8") as fh:
                json.dump(d, fh)
            model = gmi.build_model([shots], make_scenarios(root), with_photos=False)
            html = gmi.render_html(model)
            self.assertNotIn("<img src=x", html)
            self.assertNotIn("</script><img", html)
            # exactly one closing script tag per opening one, i.e. no breakout
            self.assertEqual(html.count("<script>"), html.count("</script>"))
        finally:
            shutil.rmtree(root, ignore_errors=True)


class TreeFlatteningTests(unittest.TestCase):
    def test_child_rects_become_parent_relative_so_a_scroll_view_clips(self):
        parent = node("scrollview", [280, 68, 980, 556], None, children=[
            node("label", [284, 76, 190, 23], "Kerbal", style="box")])
        out = gmi.compact_tree(parent, [270, 8])
        self.assertEqual((out["x"], out["y"]), (10, 60))
        self.assertEqual((out["c"][0]["x"], out["c"][0]["y"]), (4, 8))

    def test_a_zero_height_window_takes_the_height_the_seam_applied(self):
        # A GUILayout window reports h=0 in the dump; rendering it at 0 would
        # hide the whole window, which is how the main window first vanished.
        root = node("window", [8, 8, 250, 0], "Parsek", style="window", children=[
            node("button", [18, 30, 230, 22], "Timeline")])
        self.assertEqual(gmi.root_height(root, {"main": [8, 8, 250, 300]}), 300)

    def test_without_a_log_rect_the_height_comes_from_the_child_extent(self):
        root = node("window", [8, 8, 250, 0], "Parsek", style="window", children=[
            node("button", [18, 30, 230, 22], "Timeline")])
        self.assertGreaterEqual(gmi.root_height(root, {}), 44)

    def test_a_real_height_is_never_overridden(self):
        root = node("window", [270, 8, 1000, 700], "Parsek - Timeline", style="window")
        self.assertEqual(gmi.root_height(root, {"timeline": [270, 8, 1000, 245]}), 700)

    def test_the_selected_grid_item_is_carried_through(self):
        grid = node("buttongrid", [280, 43, 980, 21], None, style="button",
                    text_value="Roster")
        out = gmi.compact_tree(grid, [270, 8])
        self.assertEqual(out["tv"], "Roster")


class GridLabelMeasurementTests(unittest.TestCase):
    """A selection grid reports one rect and the selected item's text only, so
    where its labels sit is measured off the frame. The product is not uniform
    about it - two-tab bars centre, four-tab bars left-align - and assuming either
    put a whole tab strip tens of pixels out."""

    def frame(self, bands):
        """A 400x30 dark frame with bright bands at the given x ranges."""
        w, h, bpp = 400, 30, 3
        px = bytearray(bytes((0x3c, 0x3c, 0x3c)) * (w * h))
        for (x0, x1) in bands:
            for y in range(10, 20):
                for x in range(x0, x1):
                    o = (y * w + x) * bpp
                    px[o:o + 3] = bytes((0xe0, 0xe0, 0xe0))
        return w, h, bpp, bytes(px)

    def test_each_label_is_found_as_one_run(self):
        w, h, bpp, px = self.frame([(20, 70), (200, 244)])
        runs = gmi.grid_label_runs(w, h, bpp, px, [0, 0, 400, 30])
        self.assertEqual(runs, [[20, 69], [200, 243]])

    def test_gaps_narrower_than_a_word_join(self):
        # Two glyph clusters 6 px apart are one label, not two.
        w, h, bpp, px = self.frame([(20, 50), (56, 90)])
        runs = gmi.grid_label_runs(w, h, bpp, px, [0, 0, 400, 30])
        self.assertEqual(runs, [[20, 89]])

    def test_a_flat_bar_measures_nothing(self):
        w, h, bpp, px = self.frame([])
        self.assertEqual(gmi.grid_label_runs(w, h, bpp, px, [0, 0, 400, 30]), [])

    def test_a_degenerate_rect_measures_nothing(self):
        w, h, bpp, px = self.frame([(20, 70)])
        self.assertEqual(gmi.grid_label_runs(w, h, bpp, px, [0, 0, 0, 30]), [])
        self.assertEqual(gmi.grid_label_runs(w, h, bpp, px, [0, 0, 400, 2]), [])


class HeaderOffsetMeasurementTests(unittest.TestCase):
    """The alignment number the Compare note quotes is measured here, off the
    dump, rather than copied out of the record it cites."""

    def build(self, dx):
        header = node("layoutgroup", [280, 72, 980, 31], None, children=[
            node("label", [284, 76, 190, 23], "A", style="box"),
            node("label", [478, 76, 220, 23], "B", style="box"),
            node("label", [702, 76, 130, 23], "C", style="box")])
        body = node("layoutgroup", [280, 111, 980, 29], None, children=[
            node("label", [284 + dx, 115, 190, 21], "a", style="label"),
            node("label", [478 + dx, 115, 220, 21], "b", style="label"),
            node("label", [702 + dx, 115, 130, 21], "c", style="label")])
        return gmi.compact_tree(
            node("window", [270, 8, 1000, 400], "W", style="window",
                 children=[header, body]), [270, 8])

    def test_an_offset_table_is_measured_per_column(self):
        offs = gmi.header_cell_offsets(self.build(-5))
        self.assertEqual([o["dx"] for o in offs], [-5, -5, -5])

    def test_an_aligned_table_measures_zero(self):
        offs = gmi.header_cell_offsets(self.build(0))
        self.assertEqual([o["dx"] for o in offs], [0, 0, 0])
        self.assertEqual([o["dw"] for o in offs], [0, 0, 0])

    def test_a_window_with_no_header_row_measures_nothing(self):
        plain = gmi.compact_tree(
            node("window", [270, 8, 400, 200], "W", style="window", children=[
                node("label", [280, 40, 100, 21], "just a line")]), [270, 8])
        self.assertEqual(gmi.header_cell_offsets(plain), [])

    def test_the_pair_measurement_reports_both_sides(self):
        before = {"roots": [self.build(-5)]}
        after = {"roots": [self.build(0)]}
        m = gmi.measure_pair(before, after)
        self.assertEqual(m["maxDxBefore"], 5)
        self.assertEqual(m["maxDxAfter"], 0)
        self.assertEqual(m["nodesBefore"], m["nodesAfter"])


class RecordParsingTests(unittest.TestCase):
    """The Compare prose is lifted from the repo's records, so the lift is what
    gets tested - not the prose."""

    CHANGELOG = """# Changelog

## 0.10.5

### Changed

- **The Kerbals window was rebuilt as two column tables.** Before this, the
  Roster tab drew exactly ONE row.

- **The Timeline `R` button now appears on the launch row only.** It walked to
  the tree root.

## 0.10.4

### Changed

- **Something older about the Kerbals window that must not be picked up.**
"""

    TODO = """# Todo

- ~~**GUI-P5-WIPE-ALL-GAME-ACTIONS-CLEARS-ONLY-MILESTONES**~~ The Settings
  button named the ledger. Fix: relabel to `Wipe All Milestones (N)`.
- **GUI-P3-ROUTE-INTERVAL-SNAPS-SILENTLY** The Logistics interval field
  ceil-snaps what you typed. Fix: not decided.
"""

    def test_only_the_current_version_section_is_read(self):
        entries = gmi.parse_changelog(self.CHANGELOG)
        titles = [e["title"] for e in entries]
        self.assertTrue(any("Kerbals window was rebuilt" in t for t in titles))
        self.assertFalse(any("must not be picked up" in t for t in titles),
                         "a previous version's entry leaked into the notes")

    def test_a_bullets_continuation_lines_are_kept_with_it(self):
        entries = gmi.parse_changelog(self.CHANGELOG)
        kerbals = [e for e in entries if "rebuilt" in e["title"]][0]
        self.assertIn("drew exactly ONE row", kerbals["body"])
        self.assertEqual(kerbals["section"], "Changed")

    def test_todo_ids_carry_their_struck_state_and_fix_line(self):
        items = {e["id"]: e for e in gmi.parse_todo(self.TODO)}
        self.assertTrue(items["GUI-P5-WIPE-ALL-GAME-ACTIONS-CLEARS-ONLY-MILESTONES"]["done"])
        self.assertIn("Wipe All Milestones",
                      items["GUI-P5-WIPE-ALL-GAME-ACTIONS-CLEARS-ONLY-MILESTONES"]["fix"])
        self.assertFalse(items["GUI-P3-ROUTE-INTERVAL-SNAPS-SILENTLY"]["done"])

    def test_records_attach_to_the_window_they_name(self):
        vocab = gmi.window_vocabulary(
            ["kerbals", "timeline", "logistics"],
            {"kerbals": ["Parsek - Kerbals"], "timeline": ["Parsek - Timeline"],
             "logistics": ["Parsek - Logistics"]})
        notes = gmi.attach_records(vocab, gmi.parse_changelog(self.CHANGELOG),
                                   gmi.parse_todo(self.TODO), [])
        self.assertTrue(notes["kerbals"]["changelog"])
        self.assertTrue(notes["timeline"]["changelog"])
        self.assertEqual(notes["logistics"]["changelog"], [],
                         "a record was attached to a window it never names")
        self.assertEqual([t["id"] for t in notes["logistics"]["open"]],
                         ["GUI-P3-ROUTE-INTERVAL-SNAPS-SILENTLY"])
        self.assertEqual(notes["logistics"]["todo"], [])


class FixtureResolutionTests(unittest.TestCase):
    def test_the_fixture_is_the_save_templates_leaf(self):
        self.assertEqual(gmi.fixture_of_template("fixtures/saves/bdock-recorded"),
                         "bdock-recorded")
        self.assertEqual(gmi.fixture_of_template("fixtures/local-saves/c1-gui/"),
                         "c1-gui")
        self.assertEqual(gmi.fixture_of_template(""), "")

    def test_the_spec_toml_is_where_it_comes_from(self):
        root = tempfile.mkdtemp()
        try:
            scen = make_scenarios(root, "SYN-1-census-widgets",
                                  "fixtures/saves/syn-fixture")
            self.assertEqual(gmi.spec_fixture(scen, "SYN-1-census-widgets"),
                             "syn-fixture")
            self.assertEqual(gmi.spec_fixture(scen, "NO-SUCH-SPEC"), "")
        finally:
            shutil.rmtree(root, ignore_errors=True)


class EndToEndRenderTests(unittest.TestCase):
    """A synthetic two-capture dump must render BOTH states and the switch
    between them, with no window text typed anywhere in the generator."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.shots = make_shots(self.root)
        self.scen = make_scenarios(self.root)
        self.model = gmi.build_model([self.shots], self.scen, with_photos=True)
        self.html = gmi.render_html(self.model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_both_captures_are_in_the_page(self):
        labels = sorted(c["label"] for c in self.model["captures"])
        self.assertEqual(labels, ["syn-widgets-qorvex-advanced",
                                  "syn-widgets-zynthia-advanced"])
        self.assertIn("syn-widgets-zynthia-advanced", self.html)
        self.assertIn("syn-widgets-qorvex-advanced", self.html)

    def test_the_window_tab_mode_and_fixture_are_resolved(self):
        cap = [c for c in self.model["captures"] if c["tab"] == "zynthia"][0]
        self.assertEqual((cap["window"], cap["mode"], cap["fixture"], cap["scene"]),
                         ("widgets", "advanced", "syn-fixture", "SPACECENTER"))

    def test_the_switch_between_the_two_states_exists_as_an_edge(self):
        ids = {c["tab"]: c["id"] for c in self.model["captures"]}
        edges = {(e["from"], e["to"]) for e in self.model["edges"]}
        self.assertIn((ids["zynthia"], ids["qorvex"]), edges)
        self.assertIn((ids["qorvex"], ids["zynthia"]), edges)

    def test_both_tab_names_are_known_although_no_single_dump_carries_both(self):
        # A selection grid reports only the SELECTED item, so the other tab's
        # name can only come from the capture where IT was selected.
        tabs = {t["token"]: t["name"]
                for w in self.model["windows"] if w["token"] == "widgets"
                for t in w["tabs"]}
        self.assertEqual(tabs, {"zynthia": "Zynthia", "qorvex": "Qorvex"})

    def test_control_text_and_tooltips_reach_the_page_from_the_dump_only(self):
        self.assertIn("Go to the Qorvex tab", self.html)
        self.assertIn("zynthia row", self.html)
        self.assertIn("qorvex row", self.html)

    def test_the_generator_types_no_window_text_of_its_own(self):
        # The guard on the whole claim: the tool's SOURCE must not contain the
        # strings the page shows. A window label that lives in the generator is
        # a window label that can drift from the game.
        with open(gmi.__file__, encoding="utf-8") as fh:
            src = fh.read()
        for typed in ("Parsek - Widgets", "zynthia row", "qorvex row",
                      "Go to the Qorvex tab", "Zynthia", "Qorvex"):
            self.assertNotIn(typed, src,
                             "%r is typed into the generator" % typed)

    def test_the_colours_are_sampled_from_the_frame(self):
        cap = self.model["captures"][0]
        self.assertTrue(cap["roots"][0].get("bg"),
                        "no colour was sampled off the PNG")
        self.assertRegex(cap["roots"][0]["bg"], r"^#[0-9a-f]{6}$")

    def test_a_missing_png_degrades_instead_of_failing(self):
        root2 = tempfile.mkdtemp()
        try:
            shots = make_shots(root2, with_png=False)
            model = gmi.build_model([shots], make_scenarios(root2))
            self.assertEqual(len(model["captures"]), 2)
            self.assertIsNone(model["captures"][0]["photo"])
        finally:
            shutil.rmtree(root2, ignore_errors=True)

    def test_a_malformed_dump_is_skipped_not_fatal(self):
        with open(os.path.join(self.shots, "syn-broken.gui.json"), "w",
                  encoding="utf-8") as fh:
            fh.write("{ not json")
        with open(os.path.join(self.shots, "syn-wrongschema.gui.json"), "w",
                  encoding="utf-8") as fh:
            json.dump({"schema": "something/9", "roots": []}, fh)
        model = gmi.build_model([self.shots], self.scen, with_photos=False)
        self.assertEqual(len(model["captures"]), 2)

    def test_the_index_reports_coverage_per_window(self):
        idx = gmi.build_index(self.model)
        self.assertEqual(idx["captureCount"], 2)
        self.assertEqual(idx["windows"]["widgets"]["captures"], 2)
        self.assertIn("syn-fixture", idx["fixtures"])

    def test_a_key_with_one_capture_is_not_reported_as_changed(self):
        for info in self.model["keys"].values():
            self.assertFalse(info["changed"])
            self.assertIsNone(info["measured"])

    def test_the_page_is_one_self_contained_file(self):
        self.assertNotIn("http://", self.html.replace("http://www.w3.org", ""))
        self.assertNotIn("https://", self.html)
        self.assertNotIn("<link", self.html)
        self.assertNotIn('src="', self.html.replace('src="data:', 'src="DATA'))


class RailDisclosureTests(unittest.TestCase):
    """The rail's window headers are a real disclosure widget, not a list of
    shortcuts.

    Nothing here renders the rail - it is built in the page at runtime - so what
    is pinned is that the page SHIPS the wiring: the header carries the toggle
    role and the expanded state, the capture list carries the collapsible
    attribute and the hidden flag, a header click stops before selecting anything,
    and every localStorage access is guarded. A rail that quietly lost its
    aria-expanded, or a `localStorage` read that throws in a private window and
    takes the whole rail down with it, would otherwise only show up in front of
    the operator.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp()
        model = gmi.build_model([make_shots(self.root)], make_scenarios(self.root),
                                with_photos=False)
        self.html = gmi.render_html(model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_the_header_carries_the_toggle_role_and_its_expanded_state(self):
        self.assertIn("setAttribute('role', 'button')", self.html)
        self.assertIn("setAttribute('aria-expanded'", self.html)
        self.assertIn("setAttribute('aria-controls', listId)", self.html)
        self.assertIn("setAttribute('tabindex', '0')", self.html)

    def test_the_capture_list_is_the_collapsible_element(self):
        self.assertIn("list.dataset.collapsible = '1'", self.html)
        self.assertIn("list.hidden = !open", self.html)
        self.assertIn("list.id = listId", self.html)

    def test_a_header_click_toggles_and_selects_nothing(self):
        # `stopPropagation` plus a handler that only writes S.collapsed: a header
        # that also selected would make the rail unusable as a fold.
        self.assertIn("if (ev) ev.stopPropagation();", self.html)
        self.assertIn("S.collapsed[w.token] = open;", self.html)
        self.assertIn("row.onclick = toggle;", self.html)

    def test_selecting_a_capture_unfolds_its_own_window(self):
        self.assertIn("S.collapsed[cap.window] = false;", self.html)

    def test_the_keyboard_opens_it_too(self):
        self.assertIn("row.onkeydown", self.html)
        self.assertIn("ev.key === 'Enter'", self.html)

    def test_the_count_badge_stays_on_the_header(self):
        self.assertIn("el('span', 'n', String(w.captureCount))", self.html)

    def test_a_caret_marks_the_state(self):
        self.assertIn("'caret' + (open ? ' open' : '')", self.html)
        self.assertIn(".caret.open{transform:rotate(90deg)}", self.html)

    def test_every_localstorage_access_is_guarded(self):
        # Two accessors, two try/catch blocks, and a load that answers null rather
        # than throwing - the page must come up in a private window.
        for i, fn in enumerate(("function loadCollapsed(){", "function saveCollapsed(){")):
            self.assertIn(fn, self.html)
            body = self.html[self.html.index(fn):]
            body = body[:body.index("\n}")]
            self.assertIn("try {", body, "%s has no try block" % fn)
            self.assertIn("catch (e)", body, "%s has no catch" % fn)

    def test_everything_starts_folded_but_the_shown_window(self):
        self.assertIn("M.windows.forEach(function(w){ S.collapsed[w.token] = true; });",
                      self.html)
        # ...and the stored set wins over that default when there is one
        self.assertIn("var stored = loadCollapsed();", self.html)


class SizeBudgetTests(unittest.TestCase):
    """A self-contained page that does not fit its budget is not deliverable, so
    the budget is a gate and not a hope."""

    def test_the_generated_page_fits_the_declared_budget(self):
        root = tempfile.mkdtemp()
        try:
            shots = make_shots(root)
            out = os.path.join(root, "m.html")
            rc = gmi.main(["--shots", shots, "--scenarios", make_scenarios(root),
                           "--out", out, "--index", os.path.join(root, "m.json")])
            self.assertEqual(rc, 0)
            self.assertLessEqual(os.path.getsize(out), gmi.DEFAULT_BUDGET_BYTES)
            self.assertTrue(os.path.getsize(os.path.join(root, "m.json")) > 0)
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_an_impossible_budget_is_reported_rather_than_silently_broken(self):
        root = tempfile.mkdtemp()
        try:
            shots = make_shots(root)
            out = os.path.join(root, "m.html")
            rc = gmi.main(["--shots", shots, "--scenarios", make_scenarios(root),
                           "--out", out, "--budget-mb", "0.001"])
            self.assertEqual(rc, 2, "over-budget page exited 0")
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_photos_can_be_left_out_entirely(self):
        root = tempfile.mkdtemp()
        try:
            shots = make_shots(root)
            model = gmi.build_model([shots], make_scenarios(root), with_photos=False)
            self.assertEqual(model["photoBytes"], 0)
            self.assertNotIn("data:image/png", gmi.render_html(model))
        finally:
            shutil.rmtree(root, ignore_errors=True)


class PngCodecTests(unittest.TestCase):
    """The photo pipeline is stdlib zlib plus struct, so it gets its own cells."""

    def test_round_trip_crop_and_subsample(self):
        root = tempfile.mkdtemp()
        try:
            p = os.path.join(root, "x.png")
            tiny_png(p, 8, 8, (10, 20, 30))
            w, h, bpp, px = gmi.read_png(p)
            self.assertEqual((w, h, bpp), (8, 8, 3))
            self.assertEqual(tuple(px[0:3]), (10, 20, 30))
            cw, ch, cpx = gmi.crop(w, h, bpp, px, 2, 2, 4, 4)
            self.assertEqual((cw, ch, len(cpx)), (4, 4, 48))
            sw, sh, spx = gmi.subsample(cw, ch, bpp, cpx, 2)
            self.assertEqual((sw, sh, len(spx)), (2, 2, 12))
            blob = gmi.write_png(sw, sh, bpp, spx, quant=8)
            self.assertTrue(blob.startswith(b"\x89PNG"))
            p2 = os.path.join(root, "y.png")
            with open(p2, "wb") as fh:
                fh.write(blob)
            self.assertEqual(gmi.read_png(p2)[:3], (2, 2, 3))
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_a_crop_outside_the_image_is_clamped_not_crashed(self):
        root = tempfile.mkdtemp()
        try:
            p = os.path.join(root, "x.png")
            tiny_png(p, 8, 8)
            w, h, bpp, px = gmi.read_png(p)
            cw, ch, _ = gmi.crop(w, h, bpp, px, 6, 6, 40, 40)
            self.assertEqual((cw, ch), (2, 2))
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_the_sampler_reports_a_flat_regions_background_and_no_ink(self):
        root = tempfile.mkdtemp()
        try:
            p = os.path.join(root, "x.png")
            tiny_png(p, 8, 8, (68, 68, 68))
            w, h, bpp, px = gmi.read_png(p)
            bg, fg = gmi.sample_colors(w, h, bpp, px, [0, 0, 8, 8])
            self.assertEqual(bg, "#444444")
            self.assertIsNone(fg, "flat grey reported ink that is not there")
        finally:
            shutil.rmtree(root, ignore_errors=True)

    def test_a_rect_off_the_image_samples_nothing(self):
        root = tempfile.mkdtemp()
        try:
            p = os.path.join(root, "x.png")
            tiny_png(p, 8, 8)
            w, h, bpp, px = gmi.read_png(p)
            self.assertEqual(gmi.sample_colors(w, h, bpp, px, [100, 100, 10, 10]),
                             (None, None))
            self.assertEqual(gmi.sample_colors(w, h, bpp, px, [0, 0, 0, 0]),
                             (None, None))
        finally:
            shutil.rmtree(root, ignore_errors=True)


class ForeignRootTests(unittest.TestCase):
    """Another mod's window was on screen and is in the dump; it is not part of
    any Parsek surface and must not be mistaken for one - nor the other way
    round, which is how the main window once vanished from the page."""

    def cap(self, window, roots, rects):
        return {"dump": {"roots": roots}, "log": {"window": window, "rects": rects}}

    def test_a_window_the_seam_placed_is_never_foreign(self):
        roots = [node("window", [10, 10, 288, 290], "kRPC v0.5.4", style="window"),
                 node("window", [8, 8, 250, 0], "Parsek", style="window")]
        caps = [self.cap(w, roots, ({"main": [8, 8, 250, 300]} if w == "main" else {}))
                for w in ("main", "missions", "timeline", "kerbals", "career")]
        foreign = gmi.classify_foreign(caps)
        self.assertIn(("kRPC v0.5.4", (10, 10, 288, 290)), foreign)
        self.assertNotIn(("Parsek", (8, 8, 250, 0)), foreign,
                         "the main window was demoted to another mod's chrome")

    def test_a_root_seen_under_one_window_only_stays(self):
        # A group picker or the Gloops recorder: Parsek's own child surface.
        roots = [node("window", [0, 8, 280, 300], "Manage Groups", style="window")]
        caps = [self.cap("missions", roots, {})]
        self.assertEqual(gmi.classify_foreign(caps), set())


if __name__ == "__main__":
    unittest.main()
