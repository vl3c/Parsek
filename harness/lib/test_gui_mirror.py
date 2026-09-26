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
  * a click resolves through one ranking function over the captures that EXIST,
    so it can never invent a screen;
  * a control whose text is markup cannot break out of the page;
  * the page fits its size budget, since a self-contained page that does not is
    not deliverable.

Lives under `lib/` rather than beside the tool because CI runs
`python -m unittest discover -s lib -q` and nothing discovers `tools/` - the same
placement as `lib/test_gui_tree_view.py` for `tools/gui_tree_view.py`.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    cd harness && python -m unittest discover -s lib -q
"""

import ast
import io
import json
import os
import re
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
         enabled=True, value=None, text_value=None, selected_index=None):
    n = {"kind": kind, "rect": list(rect), "localRect": list(rect),
         "clipDepth": 1, "style": style if style is not None else kind,
         "enabled": enabled, "text": text, "children": children or []}
    if tooltip is not None:
        n["tooltip"] = tooltip
    if value is not None:
        n["value"] = value
    if text_value is not None:
        n["textValue"] = text_value
    if selected_index is not None:
        n["selectedIndex"] = selected_index
    return n


def dump(label, window_title, kids, grid_value=None,
         utc="2026-09-11T05:49:25Z", mock=None, screen=(1280, 720)):
    roots = [node("window", [270, 8, 700, 400], window_title, style="window",
                  children=([node("buttongrid", [280, 43, 680, 21], None,
                                  style="button", text_value=grid_value)]
                            if grid_value else []) + kids)]
    out = {"schema": gmi.TREE_SCHEMA, "label": label,
           "capturedUtc": utc,
           "frame": 1, "screen": {"width": screen[0], "height": screen[1]},
           "screenshotHint": label + ".png",
           "guiMatrix": {"identity": True}, "counts": {}, "funnels": [],
           "roots": roots}
    if mock is not None:
        # ADDITIVE, and ABSENT on every real capture - which is the reader's
        # rule, and what every other cell in this file exercises.
        out["mock"] = mock
    return out


def tiny_png(path, w=1280, h=720, rgb=(68, 68, 68), bands=()):
    """A real PNG at the census frame size, so the colour sampler runs on it the
    way it does in production rather than on a stub.

    `bands` paints bright rectangles, which is how a synthetic frame can carry
    the tab labels the grid measurement reads back out of it.
    """
    import struct
    px = bytearray(bytes(rgb) * (w * h))
    for (bx0, by0, bx1, by1) in bands:
        for y in range(by0, min(by1, h)):
            for x in range(bx0, min(bx1, w)):
                o = (y * w + x) * 3
                px[o:o + 3] = bytes((224, 224, 224))
    raw = b"".join(b"\x00" + bytes(px[y * w * 3:(y + 1) * w * 3])
                   for y in range(h))

    def chunk(typ, body):
        c = typ + body
        return struct.pack(">I", len(body)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    with open(path, "wb") as fh:
        fh.write(b"\x89PNG\r\n\x1a\n"
                 + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                 + chunk(b"IDAT", zlib.compress(raw, 9))
                 + chunk(b"IEND", b""))


TESTDATA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "testdata")


def _load_excerpt():
    """A small COMMITTED excerpt of a real census dump (the KSC main window from
    `2026-09-11_0548_GUI-1-census-ksc`), so the no-typed-UI-text guard is checked
    against strings the game actually drew and not only against synthetic ones."""
    with open(os.path.join(TESTDATA, "gui_mirror_excerpt.gui.json"),
              encoding="utf-8") as fh:
        return json.load(fh)


REAL_EXCERPT = _load_excerpt()


def _strings_of(node, acc=None):
    """Every text, selection-grid value and tooltip in a COMPACT node tree."""
    acc = set() if acc is None else acc
    for key in ("t", "tv", "p"):
        if node.get(key):
            acc.add(node[key])
    for ch in node.get("c") or ():
        _strings_of(ch, acc)
    return acc


def _strings_of_dump(dump):
    """The same, over a raw `parsek-gui-tree/1` dump."""
    acc = set()

    def walk(n):
        for key in ("text", "textValue", "tooltip"):
            if n.get(key):
                acc.add(n[key])
        for ch in n.get("children") or ():
            walk(ch)

    for root in dump.get("roots") or ():
        walk(root)
    return acc


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


LOG_WITH_DIALOG = (LOG_TWO_STATES
                   .replace("capturescreenshot ok label=syn-widgets-zynthia-advanced",
                            "uiaction dialog open=true count=1 "
                            "name=ParsekWipeRecordingsConfirm "
                            "title=Confirm: Wipe Recordings nbuttons=2 "
                            "buttons=Wipe All|Cancel\n"
                            "capturescreenshot ok label=dlg-wipe")
                   .replace("capturescreenshot ok label=syn-widgets-qorvex-advanced",
                            "uiaction dialog open=false count=0\n"
                            "capturescreenshot ok label=dlg-clean"))


def make_shots(root, name="2026-09-11_0548_SYN-1-census-widgets_shots",
               log=LOG_TWO_STATES, with_png=True,
               utc="2026-09-11T05:49:25Z", extra_row=None):
    path = os.path.join(root, name)
    os.makedirs(path)
    with open(os.path.join(path, "KSP.log"), "w", encoding="utf-8") as fh:
        fh.write(log + "\n")
    zynthia = dump("syn-widgets-zynthia-advanced", "Parsek - Widgets", [
        node("label", [284, 76, 190, 23], "Column", style="box"),
        node("button", [284, 115, 190, 21], "Qorvex", tooltip="Go to the Qorvex tab"),
        node("label", [478, 115, 220, 21], "zynthia row"),
    ] + ([node("label", [478, 140, 220, 21], extra_row)] if extra_row else []),
        grid_value="Zynthia", utc=utc)
    qorvex = dump("syn-widgets-qorvex-advanced", "Parsek - Widgets", [
        node("label", [284, 76, 190, 23], "Column", style="box"),
        node("label", [284, 115, 190, 21], "qorvex row"),
    ], grid_value="Qorvex", utc=utc)
    for d in (zynthia, qorvex):
        with open(os.path.join(path, d["label"] + ".gui.json"), "w",
                  encoding="utf-8") as fh:
            json.dump(d, fh)
        if with_png:
            # One bright band per tab, inside the grid's own rect
            # [280,43,680,21], so `grid_label_runs` has something real to read.
            tiny_png(os.path.join(path, d["label"] + ".png"),
                     bands=((300, 48, 380, 60), (640, 48, 720, 60)))
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
            "career": {"contracts", "strategies"},
            "timeline": {"overview", "details", "rewindff", "refly",
                         "contracts", "strategies", "facilities", "milestones", "tech"}}

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


class ShotsDirNameTests(unittest.TestCase):
    """run.py's collision guard puts `_run<N>` / `_a<N>` AFTER the scenario id
    (hlib.format_run_id). Read as part of the spec id they named no scenario, so a
    re-flight in the same minute lost its dataset and never paired in Compare."""

    def scan(self, dirname):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        shots = make_shots(root, name=dirname, with_png=False)
        return gmi.scan_shots_dir(shots, make_scenarios(root), want_colors=False)

    def test_a_plain_run_is_unchanged(self):
        out = self.scan("2026-09-11_0548_SYN-1-census-widgets_shots")
        self.assertEqual((out["runId"], out["specId"], out["fixture"]),
                         ("2026-09-11_0548", "SYN-1-census-widgets", "syn-fixture"))

    def test_the_ordinal_and_attempt_suffixes_belong_to_the_run(self):
        for sfx in ("_run2", "_a2", "_run3_a2"):
            out = self.scan("2026-09-22_1841_SYN-1-census-widgets%s_shots" % sfx)
            self.assertEqual(out["specId"], "SYN-1-census-widgets", sfx)
            self.assertEqual(out["fixture"], "syn-fixture", sfx)
            self.assertEqual(out["runId"], "2026-09-22_1841" + sfx)


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

    def test_a_zero_height_window_is_sized_by_its_content_not_the_seam_rect(self):
        # A GUILayout window reports h=0 in the dump because its host re-fits it
        # every frame. The seam's applied rect is the height it had at apply time
        # and goes stale when the content shrinks: the Basic main window drew a
        # 300 px panel over 226 px of content. Content plus the bottom chrome wins.
        root = node("window", [8, 8, 250, 0], "Parsek", style="window", children=[
            node("button", [18, 30, 230, 22], "Timeline")])
        self.assertEqual(gmi.root_height(root, {"main": [8, 8, 250, 300]}),
                         (30 + 22 - 8) + gmi.WINDOW_BOTTOM_PAD)

    def test_a_zero_height_window_with_no_children_falls_back_to_the_seam_rect(self):
        root = node("window", [8, 8, 250, 0], "Parsek", style="window")
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


class FontDeltaTests(unittest.TestCase):
    """The dump's additive `fontSize` / `fontStyle` keys reach the page. The main
    window's version footer is a 10 px `new GUIStyle(label)` copy in a 35 px rect;
    drawn at the page's 13 px label default it read `v0.10.` - the one bug the owner
    reported against the mirror itself."""

    def test_a_custom_label_size_becomes_fs(self):
        n = node("label", [18, 273, 35, 17], "v0.10.5", style="label")
        n["fontSize"] = 10
        out = gmi.compact_tree(n, [8, 8])
        self.assertEqual(out["fs"], 10)
        self.assertNotIn("fw", out)

    def test_a_window_title_font_becomes_fs_and_fw(self):
        n = node("window", [8, 8, 250, 0], "Parsek", style="window")
        n["fontSize"] = 16
        n["fontStyle"] = "bold"
        out = gmi.compact_tree(n, [8, 8])
        self.assertEqual((out["fs"], out["fw"]), (16, "b"))

    def test_every_wire_style_has_a_code(self):
        # The C# side's pinned wire names (GuiTreeAssembler.FontStyleWireName).
        for wire, code in (("normal", "n"), ("bold", "b"), ("italic", "i"),
                           ("boldItalic", "bi")):
            n = node("label", [0, 0, 10, 10], "x")
            n["fontStyle"] = wire
            self.assertEqual(gmi.compact_tree(n, [0, 0])["fw"], code)

    def test_absent_or_unusable_keys_add_nothing(self):
        for size, style in ((None, None), (0, None), (True, None), ("10", "9")):
            n = node("label", [0, 0, 10, 10], "x")
            if size is not None:
                n["fontSize"] = size
            if style is not None:
                n["fontStyle"] = style
            out = gmi.compact_tree(n, [0, 0])
            self.assertNotIn("fs", out)
            self.assertNotIn("fw", out)

    def test_the_model_root_carries_the_title_font_and_the_page_applies_it(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        shots = make_shots(root, with_png=False)
        path = os.path.join(shots, "syn-widgets-zynthia-advanced.gui.json")
        with open(path, encoding="utf-8") as fh:
            d = json.load(fh)
        d["roots"][0]["fontSize"] = 16
        d["roots"][0]["fontStyle"] = "bold"
        d["roots"][0]["children"][-1]["fontSize"] = 10
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(d, fh)
        model = gmi.build_model([shots], make_scenarios(root), with_photos=False)
        cap = [c for c in model["captures"] if c["tab"] == "zynthia"][0]
        self.assertEqual((cap["roots"][0]["fs"], cap["roots"][0]["fw"]), (16, "b"))
        self.assertEqual(cap["roots"][0]["c"][-1]["fs"], 10)
        html = gmi.render_html(model)
        # Both surfaces call the one helper: the title div and every control div.
        self.assertIn("applyFont(t, r);", html)
        self.assertIn("applyFont(d, n);", html)


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


class SliderThumbMeasurementTests(unittest.TestCase):
    """A slider reports its rect and no value, so where its HANDLE sits is
    measured off the frame - the same move as the tab bar's label runs. Before
    this the page drew a bare groove and the design doc listed the missing handle
    as unfixable from the dump; it was never unfixable from the PNG."""

    def bar(self, w, h, rect, thumb, vertical, groove=0x38, face=0xc0):
        px = bytearray(bytes((0x44, 0x44, 0x44)) * (w * h))
        x, y, rw, rh = rect
        for yy in range(y, y + rh):
            for xx in range(x, x + rw):
                o = (yy * w + xx) * 3
                px[o:o + 3] = bytes((groove, groove, groove))
        t0, t1 = thumb
        if vertical:
            for yy in range(y + t0, y + t1):
                for xx in range(x, x + rw):
                    o = (yy * w + xx) * 3
                    px[o:o + 3] = bytes((face, face, face))
        else:
            for yy in range(y, y + rh):
                for xx in range(x + t0, x + t1):
                    o = (yy * w + xx) * 3
                    px[o:o + 3] = bytes((face, face, face))
        return w, h, 3, bytes(px)

    def ksp_bar(self, rect, start, length, groove=45, face=17, bevel=101):
        """A scroll bar shaped like KSP's own: a groove at `groove`, a thumb whose
        FACE is darker than it, and the one-pixel bevel that is the only part
        brighter. Measured on ib-structure-mission-advanced: groove 45, thumb
        columns 14, 0, 101, 85, 68, 50, 17, 17, 17, 17, 34, 50, 50, 0, 14."""
        w, h = 300, 400
        px = bytearray(bytes((0x44, 0x44, 0x44)) * (w * h))
        x, y, rw, rh = rect
        for yy in range(y, y + rh):
            for xx in range(x, x + rw):
                o = (yy * w + xx) * 3
                px[o:o + 3] = bytes((groove,) * 3)
        for yy in range(y + start, y + start + length):
            for xx in range(x, x + rw):
                o = (yy * w + xx) * 3
                px[o:o + 3] = bytes((face,) * 3)
            o = (yy * w + x + 2) * 3
            px[o:o + 3] = bytes((bevel,) * 3)
        return w, h, 3, bytes(px)

    def test_a_horizontal_handle_is_read_at_its_own_offset(self):
        rect = [30, 50, 200, 12]
        w, h, bpp, px = self.bar(300, 100, rect, (140, 152), False)
        self.assertEqual(gmi.slider_thumb_run(w, h, bpp, px, rect),
                         (140, 12, False))

    def test_a_vertical_scrollbar_is_read_on_its_own_axis(self):
        rect = [200, 20, 15, 300]
        w, h, bpp, px = self.bar(300, 400, rect, (10, 120), True)
        self.assertEqual(gmi.slider_thumb_run(w, h, bpp, px, rect),
                         (10, 110, True))

    def test_a_flat_groove_measures_nothing_rather_than_guessing_a_middle(self):
        rect = [30, 50, 200, 12]
        w, h, bpp, px = self.bar(300, 100, rect, (0, 0), False, groove=0x38,
                                 face=0x38)
        self.assertIsNone(gmi.slider_thumb_run(w, h, bpp, px, rect))

    def test_a_degenerate_rect_measures_nothing(self):
        w, h, bpp, px = self.bar(300, 100, [30, 50, 200, 12], (140, 152), False)
        self.assertIsNone(gmi.slider_thumb_run(w, h, bpp, px, [30, 50, 0, 12]))
        self.assertIsNone(gmi.slider_thumb_run(w, h, bpp, px, [30, 50, 4, 4]))

    def test_the_longest_bright_run_wins_over_a_stray_highlight(self):
        rect = [30, 50, 200, 12]
        w, h, bpp, px = self.bar(300, 100, rect, (100, 140), False)
        px = bytearray(px)
        for yy in range(50, 62):           # a two-pixel glint at the far end
            for xx in range(225, 227):
                o = (yy * 300 + xx) * 3
                px[o:o + 3] = bytes((0xc0, 0xc0, 0xc0))
        self.assertEqual(gmi.slider_thumb_run(w, h, bpp, bytes(px), rect),
                         (100, 40, False))

    def test_the_thumbs_own_colours_are_sampled_not_typed(self):
        # The page drew every handle at #b9b9b9 (luminance 185) where KSP's
        # scroll bar thumb has a DARK face under a one-pixel bevel.
        rect = [200, 20, 15, 300]
        w, h, bpp, px = self.ksp_bar(rect, 10, 110)
        out = gmi.compact_tree(
            node("slider", rect, style="verticalscrollbar"), [0, 0],
            sampler=lambda r, ex=(): gmi.sample_colors(w, h, bpp, px, r,
                                                       exclude=ex),
            thumb_runs=lambda r: gmi.slider_thumb_run(w, h, bpp, px, r))
        self.assertEqual(out["th"], [10, 110])
        self.assertEqual(out["tc"], "#111111", "the FACE, luminance 17")
        self.assertEqual(out["te"], "#656565", "the bevel, the extreme tail")

    def test_the_groove_is_sampled_WITHOUT_the_thumb_over_it(self):
        # A scroll bar is more thumb than groove, so the median of the whole rect
        # is the THUMB's colour - the page then painted the groove in it, and
        # drawing the thumb on top changed nothing a measurement could see.
        rect = [200, 20, 15, 300]
        w, h, bpp, px = self.ksp_bar(rect, 10, 260)
        out = gmi.compact_tree(
            node("slider", rect, style="verticalscrollbar"), [0, 0],
            sampler=lambda r, ex=(): gmi.sample_colors(w, h, bpp, px, r,
                                                       exclude=ex),
            thumb_runs=lambda r: gmi.slider_thumb_run(w, h, bpp, px, r))
        self.assertEqual(out["bg"], "#2d2d2d", "45, the groove - not the thumb")

    def test_no_thumb_means_no_thumb_colours(self):
        rect = [30, 50, 200, 12]
        w, h, bpp, px = self.bar(300, 100, rect, (0, 0), False, groove=0x38,
                                 face=0x38)
        out = gmi.compact_tree(
            node("slider", rect, style="horizontalslider"), [0, 0],
            sampler=lambda r, ex=(): gmi.sample_colors(w, h, bpp, px, r,
                                                       exclude=ex),
            thumb_runs=lambda r: gmi.slider_thumb_run(w, h, bpp, px, r))
        self.assertNotIn("tc", out)
        self.assertNotIn("te", out)

    def test_the_page_paints_the_sampled_thumb_and_groove(self):
        self.assertIn("th.style.background = n.tc", gmi.JS)
        self.assertIn("th.style.borderColor = n.te", gmi.JS)
        self.assertIn(".gn.k-slider.sg::before{display:none}", gmi.CSS)
        self.assertNotIn("background:#b9b9b9", gmi.CSS)

    def test_the_measurement_reaches_the_compact_node(self):
        rect = [30, 50, 200, 12]
        w, h, bpp, px = self.bar(300, 100, rect, (140, 152), False)
        out = gmi.compact_tree(
            node("slider", rect, style="horizontalslider"), [0, 0],
            thumb_runs=lambda r: gmi.slider_thumb_run(w, h, bpp, px, r))
        self.assertEqual(out["th"], [140, 12])
        self.assertEqual(out["vt"], 0)

    def test_a_vertical_node_carries_its_orientation(self):
        rect = [200, 20, 15, 300]
        w, h, bpp, px = self.bar(300, 400, rect, (10, 120), True)
        out = gmi.compact_tree(
            node("slider", rect, style="verticalscrollbar"), [0, 0],
            thumb_runs=lambda r: gmi.slider_thumb_run(w, h, bpp, px, r))
        self.assertEqual(out["vt"], 1)
        self.assertEqual(out["th"], [10, 110])

    def test_no_frame_leaves_the_node_with_no_handle(self):
        out = gmi.compact_tree(node("slider", [0, 0, 100, 12]), [0, 0])
        self.assertNotIn("th", out)


class ToggleStyleTests(unittest.TestCase):
    """KSP draws `Toggle(v, text, "button")` as a pushed-in button, not a
    checkbox - 987 of the corpus's 8154 toggles - so the page has to tell the two
    styles apart, and the button-styled one needs its fill sampled like any other
    button's."""

    def frame(self, rect, rgb=(0x31, 0x31, 0x31)):
        w, h, bpp = 300, 100, 3
        px = bytearray(bytes((0x44, 0x44, 0x44)) * (w * h))
        x, y, rw, rh = rect
        for yy in range(y, y + rh):
            for xx in range(x, x + rw):
                o = (yy * w + xx) * 3
                px[o:o + 3] = bytes(rgb)
        return w, h, bpp, bytes(px)

    def test_a_button_styled_toggle_gets_its_fill_sampled(self):
        rect = [20, 20, 120, 21]
        w, h, bpp, px = self.frame(rect)
        out = gmi.compact_tree(
            node("toggle", rect, text="Advanced", style="button"), [0, 0],
            sampler=lambda r, ex=(): gmi.sample_colors(w, h, bpp, px, r,
                                                       exclude=ex))
        self.assertEqual(out["bg"], "#313131")

    def test_a_checkbox_styled_toggle_stores_no_fill(self):
        rect = [20, 20, 120, 21]
        w, h, bpp, px = self.frame(rect)
        out = gmi.compact_tree(
            node("toggle", rect, text="Show ghosts", style="toggle"), [0, 0],
            sampler=lambda r, ex=(): gmi.sample_colors(w, h, bpp, px, r,
                                                       exclude=ex))
        self.assertNotIn("bg", out)

    def test_the_page_draws_the_two_styles_differently(self):
        self.assertIn(".gn.k-toggle.s-button", gmi.CSS)
        self.assertIn(".gn.k-toggle.s-button .cb{display:none}", gmi.CSS)

    def test_the_tick_is_drawn_and_not_typed(self):
        # An ASCII `x` was the right STATE in the wrong shape.
        self.assertIn(".gn.k-toggle .cb.on::after", gmi.CSS)
        self.assertNotIn("n.v ? 'x'", gmi.JS)


def _bare_js():
    """The body of the page's `bootBare`, for the source-level cells below."""
    return gmi.JS.split("function bootBare()", 1)[1].split("\nfunction ", 1)[0]


class BoxTextAlignmentTests(unittest.TestCase):
    """KSP's `box` style is not uniform about alignment and the dump records
    none of it: the Logistics section heading is CENTRED in its 1358 px box while
    the sortable column headers of the same table are LEFT-ALIGNED in theirs. The
    page left-aligned the first (643 px out) and centred the second. So the
    offset is measured off the frame, the same move the tab bar's labels use."""

    def cell(self, w, h, rect, ink_x, ink_w, fill=0x29, ink=0xd6, border=True):
        px = bytearray(bytes((0x44, 0x44, 0x44)) * (w * h))
        x, y, rw, rh = rect
        for yy in range(y, y + rh):
            for xx in range(x, x + rw):
                o = (yy * w + xx) * 3
                px[o:o + 3] = bytes((fill, fill, fill))
        if border:
            for xx in range(x, x + rw):
                for yy in (y, y + rh - 1):
                    o = (yy * w + xx) * 3
                    px[o:o + 3] = bytes((ink, ink, ink))
            for yy in range(y, y + rh):
                for xx in (x, x + rw - 1):
                    o = (yy * w + xx) * 3
                    px[o:o + 3] = bytes((ink, ink, ink))
        for yy in range(y + 7, y + 15):
            for xx in range(x + ink_x, x + ink_x + ink_w):
                o = (yy * w + xx) * 3
                px[o:o + 3] = bytes((ink, ink, ink))
        return w, h, 3, bytes(px)

    def test_a_left_aligned_run_reads_its_own_offset(self):
        rect = [100, 40, 200, 23]
        w, h, bpp, px = self.cell(400, 100, rect, 5, 40)
        self.assertEqual(gmi.text_ink_offset(w, h, bpp, px, rect), 5)

    def test_a_centred_run_reads_its_own_offset(self):
        rect = [100, 40, 200, 23]
        w, h, bpp, px = self.cell(400, 100, rect, 80, 40)
        self.assertEqual(gmi.text_ink_offset(w, h, bpp, px, rect), 80)

    def test_the_boxs_own_border_is_not_its_first_glyph(self):
        # The whole reason the measurement is inset: a box border is as strong as
        # its text, and reading it would put every run at offset 0.
        rect = [100, 40, 200, 23]
        w, h, bpp, px = self.cell(400, 100, rect, 80, 40, border=True)
        self.assertEqual(gmi.text_ink_offset(w, h, bpp, px, rect), 80)

    def test_an_empty_box_measures_nothing_rather_than_zero(self):
        rect = [100, 40, 200, 23]
        px = bytearray(bytes((0x44, 0x44, 0x44)) * (400 * 100))
        for yy in range(40, 63):
            for xx in range(100, 300):
                o = (yy * 400 + xx) * 3
                px[o:o + 3] = bytes((0x29, 0x29, 0x29))
        self.assertIsNone(gmi.text_ink_offset(400, 100, 3, bytes(px), rect))

    def test_a_degenerate_rect_measures_nothing(self):
        w, h, bpp, px = self.cell(400, 100, [100, 40, 200, 23], 5, 40)
        self.assertIsNone(gmi.text_ink_offset(w, h, bpp, px, [100, 40, 4, 23]))
        self.assertIsNone(gmi.text_ink_offset(w, h, bpp, px, [100, 40, 200, 4]))

    def test_the_offset_reaches_the_compact_node_for_a_box_styled_label(self):
        rect = [100, 40, 200, 23]
        w, h, bpp, px = self.cell(400, 100, rect, 80, 40)
        out = gmi.compact_tree(
            node("label", rect, text="Active Routes", style="box"), [0, 0],
            text_offsets=lambda r: gmi.text_ink_offset(w, h, bpp, px, r))
        self.assertEqual(out["tx"], 80)

    def test_a_box_styled_button_gets_one_too(self):
        rect = [100, 40, 200, 23]
        w, h, bpp, px = self.cell(400, 100, rect, 5, 40)
        out = gmi.compact_tree(
            node("button", rect, text="Origin", style="box"), [0, 0],
            text_offsets=lambda r: gmi.text_ink_offset(w, h, bpp, px, r))
        self.assertEqual(out["tx"], 5)

    def test_an_ordinary_label_stores_none(self):
        # The ordinary alignments agree with the frame to a pixel or three, and
        # an offset for all 71 000 labels would be payload for nothing.
        rect = [100, 40, 200, 23]
        w, h, bpp, px = self.cell(400, 100, rect, 5, 40)
        out = gmi.compact_tree(
            node("label", rect, text="Jebediah", style="label"), [0, 0],
            text_offsets=lambda r: gmi.text_ink_offset(w, h, bpp, px, r))
        self.assertNotIn("tx", out)

    def test_no_frame_leaves_the_node_with_no_offset(self):
        out = gmi.compact_tree(node("label", [0, 0, 100, 23], text="x",
                                    style="box"), [0, 0])
        self.assertNotIn("tx", out)

    def test_the_page_places_the_run_at_the_measured_offset(self):
        self.assertIn("n.tx != null", gmi.JS)
        self.assertIn("d.style.paddingLeft = n.tx", gmi.JS)


class ScrollViewTests(unittest.TestCase):
    def test_a_scroll_view_scrolls(self):
        # 52 scroll views in the corpus carry content below their fold, up to
        # 23529 px of it, and `overflow:hidden` made every one of them
        # unreachable.
        self.assertIn(".gn.k-scrollview{overflow:auto;scrollbar-width:none}",
                      gmi.CSS)

    def test_the_page_hides_the_browsers_own_scrollbar(self):
        # Making a scroll view scroll gave every overflowing one a white NATIVE
        # scroll bar over the mirrored KSP bar - a difference from the game that
        # the page itself introduced. The instrument used to launch with
        # `--hide-scrollbars`, which hid it from the measurement too.
        self.assertIn("scrollbar-width:none", gmi.CSS)
        self.assertIn(".gn.k-scrollview::-webkit-scrollbar{display:none}", gmi.CSS)

    def test_the_bare_link_can_scroll_them_so_a_screenshot_can_prove_it(self):
        # "Reachable" is a claim about a browser, so it needs a browser to check:
        # `&scroll=<px>` scrolls every scroll view before the page marks itself
        # ready, which lets one capture be photographed at two offsets.
        bare = _bare_js()
        self.assertIn("q.scroll", bare)
        self.assertIn("'.gn.k-scrollview'", bare)
        self.assertIn("scrollTop = sc", bare)
        self.assertIn("dataset.scrolled", bare)

    def test_the_default_offset_is_the_one_the_frame_was_taken_at(self):
        # No `scroll` in the hash means no scrolling, so an ordinary measurement
        # sees offset zero - which is where the census photographed it.
        bare = _bare_js()
        self.assertIn("if (sc > 0)", bare)


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


def _source_literals(src):
    """Every literal a window string could hide in, normalised.

    Quoted Python strings alone were not enough - a typed 'Active Routes' inside
    a longer expression and a `/^Kerbals$/` regex both slipped past a
    quoted-substring test - and a plain regex over the source was worse, because
    it read the module's own DOCSTRINGS as literals and reported the product's
    name in a sentence about the product.

    So: walk the AST. Every string constant that is not a docstring is code, and
    the big ones are the page's own CSS and JS, which are scanned again for the
    literal forms JavaScript adds - single- and double-quoted strings, template
    literals and REGEX literals. Comments never enter, because the AST does not
    carry them, and a comment quoting a window's text to explain a measurement is
    prose.
    """
    tree = ast.parse(src)
    docstrings = set()
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.FunctionDef, ast.AsyncFunctionDef,
                             ast.ClassDef)):
            body = getattr(node, "body", None) or []
            if (body and isinstance(body[0], ast.Expr)
                    and isinstance(body[0].value, ast.Constant)
                    and isinstance(body[0].value.value, str)):
                docstrings.add(id(body[0].value))

    out = set()
    js_pats = (
        r"'((?:[^'\\\n]|\\.)*)'",                        # single-quoted
        r'"((?:[^"\\\n]|\\.)*)"',                        # double-quoted
        r"`((?:[^`\\]|\\.)*)`",                          # template literal
        r"/((?:[^/\\\n\[]|\\.|\[[^\]]*\])+)/[gimsuy]*",  # regex literal
    )
    for node in ast.walk(tree):
        if not isinstance(node, ast.Constant) or not isinstance(node.value, str):
            continue
        if id(node) in docstrings:
            continue
        text = node.value
        n = gmi.norm(text)
        if n:
            out.add(n)
        if len(text) > 200:
            # The page's own CSS and JS: scan them for the literal forms
            # JavaScript adds, with its comments taken out first.
            body = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
            body = re.sub(r"(?m)//.*$", " ", body)
            for pat in js_pats:
                for m in re.findall(pat, body):
                    n2 = gmi.norm(m)
                    if n2:
                        out.add(n2)
    return out


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

    def test_both_states_are_reachable_from_the_model(self):
        # There is no edge table: a click resolves by ranking the captures that
        # exist, so what has to hold is that BOTH states are in the model under
        # the same window and can be told apart by their tab.
        tabs = sorted(c["tab"] for c in self.model["captures"])
        self.assertEqual(tabs, ["qorvex", "zynthia"])
        self.assertEqual({c["window"] for c in self.model["captures"]}, {"widgets"})

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
        # The guard on the whole claim, driven by the CORPUS rather than by a
        # handful of needles: every string the captures carry - control text,
        # selection-grid value, tooltip - must be absent from the generator's own
        # source as a quoted literal. A window label that lives in the generator
        # is a window label that can drift from the game.
        #
        # The seam's own vocabulary is exempt and has to be: an op name
        # ("close"), a complexity mode ("advanced") and a window or tab token all
        # come out of the logs, and one of them happens to spell the same word as
        # a button's label. Those are automation identifiers the page is entitled
        # to know.
        with open(gmi.__file__, encoding="utf-8") as fh:
            src = fh.read()
        exempt = set(gmi.SEAM_VERBS) | set(gmi.MODE_TOKENS)
        # The layout-epoch table is keyed by seam WINDOW tokens (held to the seam's
        # own vocabulary by LayoutEpochTests), and one of them spells a button.
        exempt |= {gmi.norm(t) for t in gmi.LAYOUT_EPOCHS}
        for w in self.model["windows"]:
            exempt.add(gmi.norm(w["token"]))
            for t in w["tabs"]:
                exempt.add(gmi.norm(t["token"]))
        needles = set()
        for cap in self.model["captures"]:
            for root in cap["roots"]:
                needles |= _strings_of(root)
        needles |= _strings_of_dump(REAL_EXCERPT)
        self.assertGreater(len(needles), 8, "the corpus yielded no strings to check")
        # The literals the generator's SOURCE carries, of every kind a window
        # string could hide in. Quoted strings alone let two through: a typed
        # 'Active Routes' and a `/^Kerbals$/` regex, neither of which is a
        # quoted literal by that test's reckoning.
        literals = _source_literals(src)
        for text in sorted(needles):
            n = gmi.norm(text)
            if len(n) < 4 or n in exempt:
                continue
            # ONE check, over the AST's literals. The raw-substring test this
            # replaced read the module's own docstrings as code, so a sentence
            # explaining a measurement counted as a typed window label.
            self.assertNotIn(gmi.norm(text), literals,
                             "%r is typed into the generator" % text)

    def test_the_guard_catches_the_two_forms_a_quoted_scan_missed(self):
        # The reviewer's finding: a typed 'Active Routes' inside a longer
        # expression and a `/^Kerbals$/` regex both passed a quoted-substring
        # scan. Both are literals in the AST, and both are caught now.
        typed_string = "X = 1" + chr(10) + "Y = ['Active Routes', 'x']" + chr(10)
        self.assertIn(gmi.norm("Active Routes"), _source_literals(typed_string))
        blob = "var re = /^Kerbals$/; " * 12
        typed_regex = 'X = 1' + chr(10) + 'JS = "' + blob + '"' + chr(10)
        self.assertIn(gmi.norm("Kerbals"), _source_literals(typed_regex),
                      "a regex literal is a typed window string too")

    def test_the_guard_does_not_read_prose_as_code(self):
        # And the trap on the other side, which the repo's own guidance names: a
        # docstring that QUOTES a window's text to explain a measurement is
        # documentation, not a typed label.
        doc = chr(34) * 3
        src = (doc + "The heading reads 'Active Routes' in a 1358 px box." + doc
               + chr(10) + "X = 1" + chr(10))
        self.assertNotIn(gmi.norm("Active Routes"), _source_literals(src))

    def test_a_comment_quoting_a_window_string_is_not_a_literal(self):
        src = ("# the heading reads 'Active Routes' here" + chr(10)
               + "X = 1" + chr(10))
        self.assertNotIn(gmi.norm("Active Routes"), _source_literals(src))

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


class CompareScopeTests(unittest.TestCase):
    """Compare shows ONE window: the one selected in the rail.

    The filter lives in a single pure function in the page, `compareRowsFor`, so
    what is pinned here is that the function the page ships filters on the
    capture's own window and on the seam's window set, and that the section it
    renders is built from that one window rather than from every key in the model.
    A Compare view listing every window's keys is what this replaced.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.model = gmi.build_model([make_shots(self.root)],
                                     make_scenarios(self.root), with_photos=False)
        self.html = gmi.render_html(self.model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_the_row_filter_keys_on_the_captures_own_window(self):
        body = self.html[self.html.index("function compareRowsFor(win){"):]
        body = body[:body.index("function buildCompare(")]
        self.assertIn("if (cap.window !== win) return;", body)
        self.assertIn("M.seamWindows.indexOf(cap.window) < 0", body)

    def test_the_section_is_built_from_the_selected_window_only(self):
        body = self.html[self.html.index("function buildCompare(){"):]
        body = body[:body.index("function sideBlock(")]
        self.assertIn("var win = S.window;", body)
        self.assertIn("rows[win] = compareRowsFor(win);", body)
        self.assertIn("[win].forEach(function(win){", body)
        self.assertNotIn("Object.keys(M.keys).forEach", body,
                         "buildCompare still walks every key in the model")

    def test_a_window_outside_the_seam_set_says_so_instead_of_comparing(self):
        body = self.html[self.html.index("function buildCompare(){"):]
        self.assertIn("is a diagnostic surface", body)

    def test_the_rail_re_filters_compare_without_leaving_it(self):
        self.assertIn("if (S.view === 'compare') buildCompare();", self.html)

    def test_the_global_summary_is_secondary_not_the_default_view(self):
        # It survives as a fold appended AFTER the window's own section.
        self.assertIn("sumSum.textContent = 'every window at a glance';", self.html)
        self.assertIn("host.appendChild(sumSum", self.html.replace("sumHost.appendChild(sumSum);",
                                                                   "host.appendChild(sumSum"))
        self.assertIn("host.appendChild(sumHost);", self.html)

    def test_the_summary_rows_are_built_with_a_valid_tag(self):
        # `el(tag, cls)` takes the TAG first: a row built as el('tr here') threw
        # InvalidCharacterError and took the whole Compare view down.
        self.assertIn("var tr = el('tr');", self.html)
        self.assertNotIn("el('tr' +", self.html)

    def test_an_unchanged_pair_says_how_many_captures_agreed(self):
        self.assertIn("captures, no difference)", self.html)


class LauncherScopeTests(unittest.TestCase):
    """Only the home window's controls launch another window.

    The launcher rule matches a control's text against every captured window's
    token and titles, so a view button in another window's filter row that spells
    a window's name jumped to that window instead of switching the view it names.
    The rule is now scoped to the home window (the seam's first window, the same
    token the close affordance returns to), and a non-home control falls through
    to the tab / state routing, which flashes when nothing was photographed.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.model = gmi.build_model([make_shots(self.root)],
                                     make_scenarios(self.root), with_photos=False)
        self.html = gmi.render_html(self.model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def _route(self):
        body = self.html[self.html.index("function routeClick(ev, cap){"):]
        return body[:body.index("\n}")]

    def test_the_launcher_walks_windows_only_from_the_home_window(self):
        body = self._route()
        self.assertIn("var home = M.seamWindows[0];", body)
        self.assertIn("var wins = cap.window === home\n"
                      "    ? M.windows.filter(function(w){ return w.captureCount > 0; }) : [];",
                      body)
        self.assertEqual(body.count("var home"), 1,
                         "the close affordance and the launcher must share one home")

    def test_the_home_window_is_resolved_before_the_launcher_walk(self):
        body = self._route()
        self.assertLess(body.index("var home = M.seamWindows[0];"),
                        body.index("for (var i=0;i<wins.length;i++){"))

    def test_a_non_home_control_still_reaches_the_tab_and_state_routing(self):
        body = self._route()
        launcher = body.index("for (var i=0;i<wins.length;i++){")
        self.assertLess(launcher, body.index("/* a tab of this window */"))
        self.assertLess(launcher, body.index("var pool = capsFor(cap.window)"))
        self.assertIn("'no capture for this state yet - \"'", body)

    def test_the_model_names_its_home_window_first(self):
        self.assertTrue(self.model["seamWindows"])
        self.assertIn(self.model["seamWindows"][0],
                      [w["token"] for w in self.model["windows"]])


def _cap(window, state="", tab=None, mode="advanced", title="Vexo - Qorvex",
         controls=(), **kw):
    """A capture reduced to what the naming helpers read: its own window's root
    (by title) and that root's controls, as `(kind, text, value)` triples."""
    kids = []
    for kind, text, value in controls:
        node = {"k": kind, "t": text}
        if kind == "toggle":
            node["v"] = value
        kids.append(node)
    cap = {"window": window, "tab": tab, "state": state, "mode": mode,
           "roots": [{"k": "window", "title": title, "t": title, "c": kids}]}
    cap.update(kw)
    return cap


class WindowDisplayNameTests(unittest.TestCase):
    """The rail and the headers name a window by its own title, not its seam
    token: the owner could not find a window whose token is not what the game
    draws. The prefix every product title starts with comes off; the token stays
    the key."""

    def test_the_prefix_is_the_majority_head_so_one_outlier_does_not_erase_it(self):
        titles = ["Vexo", "Vexo - Qorvex", "Vexo - Zynthia Plume", "Glimmer Tock"]
        self.assertEqual(gmi.title_prefix(titles), "",
                         "the all-titles prefix is what the outlier breaks")
        self.assertEqual(gmi.display_title_prefix(titles), "Vexo")

    def test_no_majority_means_no_prefix(self):
        self.assertEqual(gmi.display_title_prefix(["Vexo - Qorvex", "Glimmer Tock"]), "")
        self.assertEqual(gmi.display_title_prefix([]), "")

    def test_a_stable_title_names_its_window(self):
        names = gmi.window_display_names(
            {"qorvex": ["Vexo - Qorvex"], "zyn": ["Vexo - Zynthia Plume"],
             "home": ["Vexo"], "tock": ["Glimmer Tock"]}, "Vexo")
        self.assertEqual(names, {"qorvex": "Qorvex", "zyn": "Zynthia Plume",
                                 "home": "Vexo", "tock": "Glimmer Tock"})

    def test_a_subject_titled_window_reads_by_the_title_that_spells_its_token(self):
        names = gmi.window_display_names(
            {"plinth": ["Vexo - Duna Run 3", "Vexo - Plinth", "Vexo - Route: A to B"]},
            "Vexo")
        self.assertEqual(names, {"plinth": "Plinth"})
        names = gmi.window_display_names(
            {"plinth": ["Vexo - Duna Run 3", "Vexo - Route: A to B"]}, "Vexo")
        self.assertEqual(names, {"plinth": "plinth"},
                         "no stable title and none spelling the token keeps the token")

    def test_two_windows_that_would_read_the_same_carry_their_tokens(self):
        names = gmi.window_display_names(
            {"runner": ["Vexo - Test Bench"], "runnerall": ["Vexo - Test Bench"]}, "Vexo")
        self.assertEqual(names, {"runner": "Test Bench (runner)",
                                 "runnerall": "Test Bench (runnerall)"})

    def test_the_model_names_every_seam_window_and_the_page_draws_the_name(self):
        root = tempfile.mkdtemp()
        try:
            model = gmi.build_model([make_shots(root)], make_scenarios(root),
                                    with_photos=False)
            html = gmi.render_html(model)
        finally:
            shutil.rmtree(root, ignore_errors=True)
        for w in model["windows"]:
            self.assertTrue(w.get("name"), "%s has no display name" % w["token"])
        self.assertIn("stateNames", model)
        self.assertIn("function winName(win){", html)
        rail = html[html.index("function buildRail(){"):]
        rail = rail[:rail.index("\n}")]
        self.assertIn("row.appendChild(el('b', null, winName(w.token)));", rail)
        self.assertNotIn("el('b', null, w.token)", rail)
        # the token stays the key of every note and lookup
        self.assertIn("railNoteKey(w.token, c.tab, c.state, c.mode)", rail)
        self.assertIn("head.appendChild(el('b', null, winName(cap.window)));", html)
        self.assertIn("sec.appendChild(el('h3', null, winName(win)));", html)


class StateDisplayNameTests(unittest.TestCase):
    """A state token is the lane's name for a seam step; where the capture's own
    tree shows the control the step changed, the rail reads by that control."""

    W, T = "qorvex", "Vexo - Qorvex"

    def names(self, caps):
        return gmi.state_display_names(caps, {self.W: [self.T]})

    def base(self, state="", **over):
        ctl = {"Plume": True, "Tock": True, "Glim": False, "Wisp": False}
        ctl.update(over)
        return _cap(self.W, state, title=self.T,
                    controls=[("toggle", k, v) for k, v in ctl.items()]
                    + [("button", "Brisk \u25b6", None)])

    def test_one_toggle_lit_against_its_peers_names_the_state(self):
        caps = [self.base(), self.base("idle"), self.base("glimon", Glim=True)]
        self.assertEqual(self.names(caps), {"qorvex||glimon|advanced": "Glim"})

    def test_one_toggle_unlit_against_its_peers_names_it_off(self):
        caps = [self.base(), self.base("idle"), self.base("noplume", Plume=False)]
        self.assertEqual(self.names(caps), {"qorvex||noplume|advanced": "Plume off"})

    def test_a_button_that_swaps_its_glyph_names_the_state_by_its_words(self):
        swapped = self.base("brisk")
        swapped["roots"][0]["c"][-1]["t"] = "Brisk \u25c0"
        caps = [self.base(), self.base("idle"), swapped]
        self.assertEqual(self.names(caps), {"qorvex||brisk|advanced": "Brisk"})

    def test_a_button_carrying_a_count_never_names_a_state(self):
        caps = [self.base(), self.base("idle"), self.base("more")]
        for c, text in zip(caps, ("Rows (3)", "Rows (3)", "Rows (4)")):
            c["roots"][0]["c"].append({"k": "button", "t": text})
        self.assertEqual(self.names(caps), {})

    def test_the_unanimous_pass_sees_through_a_majority_only_difference(self):
        # `sorted` was photographed with a toggle another state also left lit, so
        # that toggle differs from most peers but not all; its own step's button
        # differs from every peer. A majority-only reading names the toggle.
        caps = [self.base(), self.base("a"), self.base("b", Glim=True),
                self.base("sorted", Glim=True), self.base("c")]
        caps[3]["roots"][0]["c"][-1]["t"] = "Brisk \u25c0"
        self.assertEqual(self.names(caps)["qorvex||sorted|advanced"], "Brisk")

    def test_two_differences_keep_the_token(self):
        caps = [self.base(), self.base("idle"), self.base("x"),
                self.base("both", Glim=True, Wisp=True)]
        self.assertEqual(self.names(caps), {})

    def test_a_name_two_states_would_share_keeps_both_tokens(self):
        caps = [self.base(), self.base("idle"), self.base("x"),
                self.base("glim1", Glim=True), self.base("glim2", Glim=True)]
        self.assertEqual(self.names(caps), {})

    def test_stale_mocked_and_foreign_captures_are_not_read(self):
        stale = self.base("glimon", Glim=True)
        stale["supersededBy"] = "later"
        self.assertEqual(self.names([self.base(), self.base("idle"), stale]), {})
        other = self.base("glimon", Glim=True)
        other["roots"][0]["title"] = "Vexo - Zynthia"
        self.assertEqual(self.names([self.base(), self.base("idle"), other]), {},
                         "another window's root is not this window's control")

    def test_the_page_reads_the_name_and_keeps_the_token_in_the_tooltip(self):
        root = tempfile.mkdtemp()
        try:
            html = gmi.render_html(gmi.build_model(
                [make_shots(root)], make_scenarios(root), with_photos=False))
        finally:
            shutil.rmtree(root, ignore_errors=True)
        fn = html[html.index("function stateWords(win, tab, state, mode){"):]
        fn = fn[:fn.index("\n}")]
        self.assertIn("var named = (M.stateNames || {})[k];", fn)
        self.assertIn("return named || state.replace(/-/g, ' ');", fn)
        self.assertIn("(c.state ? '; seam state ' + c.state : '')", html)


class ToggleTabNameTests(unittest.TestCase):
    """A window whose tabs are a row of toggles, not a selection grid, names each
    tab by the toggle that tab alone lights."""

    W, T = "qorvex", "Vexo - Qorvex"

    def cap(self, tab, lit, state=""):
        texts = ["Plume", "Tock", "Group", "Glim"]
        return _cap(self.W, state, tab=tab, title=self.T,
                    controls=[("toggle", t, t in lit) for t in texts])

    def test_the_toggle_each_tab_alone_lights_names_it(self):
        caps = [self.cap("plume", {"Plume"}), self.cap("tock", {"Tock"}),
                self.cap("glim", {"Glim"}), self.cap("plume", {"Plume"}, "x")]
        self.assertEqual(gmi.toggle_tab_names(caps, {self.W: [self.T]}),
                         {self.W: {"plume": "Plume", "tock": "Tock", "glim": "Glim"}})

    def test_a_grouping_toggle_lit_under_two_tabs_names_neither(self):
        caps = [self.cap("plume", {"Plume"}), self.cap("glim", {"Group", "Glim"}),
                self.cap("tock", {"Group", "Tock"})]
        names = gmi.toggle_tab_names(caps, {self.W: [self.T]})[self.W]
        self.assertEqual(names["glim"], "Glim")
        self.assertEqual(names["tock"], "Tock")

    def test_a_capture_naming_no_tab_is_on_neither_side(self):
        caps = [self.cap("plume", {"Plume"}), self.cap("tock", {"Tock"}),
                self.cap(None, {"Plume"}, "loose")]
        self.assertEqual(gmi.toggle_tab_names(caps, {self.W: [self.T]})[self.W]["plume"],
                         "Plume")

    def test_an_older_capture_settles_a_tab_its_current_captures_leave_ambiguous(self):
        # Every CURRENT capture of `glim` also has the `Wisp` preset lit, so the
        # current pass cannot tell the tab's toggle from the preset; a superseded
        # capture of the same tab with the preset unlit can.
        texts = ["Plume", "Tock", "Glim", "Wisp"]

        def cap(tab, lit, **kw):
            return _cap(self.W, "", tab=tab, title=self.T,
                        controls=[("toggle", t, t in lit) for t in texts], **kw)
        current = [cap("plume", {"Plume"}), cap("glim", {"Glim", "Wisp"})]
        self.assertNotIn("glim", gmi.toggle_tab_names(current, {self.W: [self.T]})[self.W])
        older = cap("glim", {"Glim"}, supersededBy="later")
        names = gmi.toggle_tab_names(current + [older], {self.W: [self.T]})[self.W]
        self.assertEqual(names["glim"], "Glim")
        self.assertEqual(names["plume"], "Plume")


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

    def test_clicking_a_non_shown_windows_title_selects_it(self):
        # Clicking a window's NAME is how a reader asks to see that window; only
        # on the window already shown is the click the fold toggle, which is what
        # keeps the toggle usable at all.
        self.assertIn("var here = (w.token === S.window);", self.html)
        body = self.html[self.html.index("function toggle(ev){"):]
        body = body[:body.index("row.onclick = toggle;")]
        self.assertIn("if (!here){", body)
        self.assertIn("go(w.token, null, null, S.mode);", body)
        self.assertIn("S.collapsed[w.token] = open;", body)
        # the show branch returns before the fold branch, so a title click on
        # another window never doubles as a collapse
        self.assertLess(body.index("go(w.token, null, null, S.mode);"),
                        body.index("S.collapsed[w.token] = open;"))
        self.assertIn("return;", body)

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


class TabNameResolutionTests(unittest.TestCase):
    """A selection grid reports only the SELECTED item's text, so the other tabs'
    names come from the captures where THEY were selected.

    Resolving that once, globally and first-seen, meant a pre-rename heading from
    an older epoch won everywhere: the rebuilt Kerbals window rendered
    "Roster State" / "Mission Outcomes" over a frame reading "Roster" / "Flights",
    and because the selected marker compared NAMES, no cell was marked selected
    either. Both halves are pinned here.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def build(self, extra_runs=()):
        dirs = [make_shots(self.root)]
        for name, renames in extra_runs:
            path = make_shots(self.root, name=name)
            for label, newtv, utc in renames:
                f = os.path.join(path, label + ".gui.json")
                with open(f, encoding="utf-8") as fh:
                    d = json.load(fh)
                d["capturedUtc"] = utc
                grid = d["roots"][0]["children"][0]
                assert grid["kind"] == "buttongrid", grid["kind"]
                grid["textValue"] = newtv
                with open(f, "w", encoding="utf-8") as fh:
                    json.dump(d, fh)
            dirs.append(path)
        return gmi.build_model(dirs, make_scenarios(self.root), with_photos=False)

    def names_of(self, cap):
        return [(t["token"], t["name"]) for t in cap["tabNames"]]

    def test_each_capture_names_both_tabs_from_the_captures_themselves(self):
        model = self.build()
        for cap in model["captures"]:
            self.assertEqual(self.names_of(cap),
                             [("zynthia", "Zynthia"), ("qorvex", "Qorvex")],
                             cap["label"])

    def test_the_selected_tab_is_named_by_this_captures_own_text(self):
        model = self.build()
        for cap in model["captures"]:
            own = dict(self.names_of(cap))[cap["tab"]]
            grid = self.grid_of(cap)
            self.assertEqual(gmi.norm(own), gmi.norm(grid["tv"]), cap["label"])

    def grid_of(self, cap):
        found = []

        def walk(n):
            if n.get("k") == "buttongrid":
                found.append(n)
            for ch in n.get("c") or ():
                walk(ch)

        for r in cap["roots"]:
            walk(r)
        return found[0]

    def test_an_older_datasets_name_never_overrides_this_captures_own_text(self):
        # The regression: an older run of the same lane carried the pre-rename
        # heading, and it won for every capture in every dataset.
        model = self.build(extra_runs=[(
            "2026-09-01_0100_SYN-1-census-widgets_shots",
            [("syn-widgets-zynthia-advanced", "Zynthia Classic",
              "2026-09-01T01:00:00Z"),
             ("syn-widgets-qorvex-advanced", "Qorvex Classic",
              "2026-09-01T01:01:00Z")])])
        newer = [c for c in model["captures"] if c["runId"] == "2026-09-11_0548"]
        self.assertTrue(newer)
        for cap in newer:
            self.assertEqual(self.names_of(cap),
                             [("zynthia", "Zynthia"), ("qorvex", "Qorvex")],
                             "an older dataset's heading won over " + cap["label"])
        older = [c for c in model["captures"] if c["runId"] == "2026-09-01_0100"]
        for cap in older:
            own = dict(self.names_of(cap))[cap["tab"]]
            self.assertTrue(own.endswith("Classic"),
                            "the older capture lost its own text: " + own)

    def test_the_page_marks_the_selected_cell_by_recorded_index_then_token(self):
        # Never by NAME: a stale heading is what lost the marker once. The dump's
        # own selectedIndex wins when it is there, and the seam's tab token stays
        # the fallback for every capture taken before the recorder wrote it.
        html = gmi.render_html(self.build())
        self.assertIn("var on = (typeof n.si === 'number') ? (i === n.si) : "
                      "(t.token === opts.tab);", html)
        self.assertIn("var b = el('div','gi' + (on ? ' on' : ''));", html)
        self.assertNotIn("norm(t.name) === norm(n.tv", html,
                         "the selected cell is still matched on names")

    def test_a_recorded_selected_index_is_carried_into_the_page(self):
        grid = node("buttongrid", [280, 43, 980, 21], None, style="button",
                    text_value="Flights", selected_index=1)
        self.assertEqual(gmi.compact_tree(grid, [270, 8])["si"], 1)

    def test_without_a_recorded_index_the_page_falls_back_to_the_token(self):
        # Every committed capture is in this state, so the absence must leave the
        # compact node exactly as it was.
        grid = node("buttongrid", [280, 43, 980, 21], None, style="button",
                    text_value="Roster")
        self.assertNotIn("si", gmi.compact_tree(grid, [270, 8]))

    def test_a_recorded_index_is_only_read_off_a_grid(self):
        # The recorder emits the key on buttongrid alone; a stray one on another
        # kind is not a selection and must not reach the page.
        other = node("button", [280, 43, 100, 21], "Go", selected_index=3)
        self.assertNotIn("si", gmi.compact_tree(other, [270, 8]))

    def test_the_page_takes_the_names_from_the_capture(self):
        html = gmi.render_html(self.build())
        self.assertIn("var tabs = opts.tabNames || [];", html)
        self.assertIn("tabNames: cap.tabNames", html)

    def test_the_selected_cell_is_the_dark_one_with_no_top_highlight(self):
        html = gmi.render_html(self.build())
        self.assertIn(".gn.k-buttongrid .gi{box-shadow:inset 0 1px 0 "
                      "rgba(255,255,255,.16)}", html)
        self.assertIn(".gn.k-buttongrid .gi.on{box-shadow:none;background:#2c2c2c;",
                      html)
        self.assertNotIn(".gi.on{background:#5c5c5c", html,
                         "the inverted selected look is still shipped")

    def test_the_cell_fills_are_sampled_when_a_frame_is_available(self):
        model = gmi.build_model([make_shots(self.root)],
                                make_scenarios(self.root), with_photos=False)
        grid = self.grid_of(model["captures"][0])
        self.assertEqual(len(grid["gc"]), 2)
        for c in grid["gc"]:
            self.assertRegex(c, r"^#[0-9a-f]{6}$")


class OneSourceForClickableKindsTests(unittest.TestCase):
    """The clickable-kind set is ONE constant, emitted into both the page's JS and
    its CSS. Two copies is how a control ends up looking clickable and not being,
    or the other way round."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.shots = make_shots(self.root)
        self.scen = make_scenarios(self.root)
        self.model = gmi.build_model([self.shots], self.scen, with_photos=False)
        self.html = gmi.render_html(self.model)

    def test_the_js_reads_the_set_from_the_model(self):
        self.assertIn("var CLICK_KINDS = M.clickKinds;", self.html)
        self.assertIn("CLICK_KINDS.indexOf(n.k) >= 0", self.html)
        self.assertNotIn("n.k === 'repeatbutton'", self.html,
                         "the clickable set was re-listed inline in the JS")

    def test_the_css_is_emitted_from_the_same_constant(self):
        for kind in gmi.CLICK_KINDS:
            self.assertIn(".gn.k-%s:hover" % kind, self.html)

    def test_the_model_carries_it(self):
        self.assertEqual(self.model["clickKinds"], list(gmi.CLICK_KINDS))
        self.assertEqual(gmi.build_index(self.model)["seamWindows"],
                         self.model["seamWindows"])


class TodoFixLineTests(unittest.TestCase):
    """The `Fix:` line, in the shapes the file really uses. The first version
    ended an entry at its first bullet, which is BEFORE the Fix line in almost
    every entry - 52 entries parsed, every fix empty."""

    REAL_SHAPE = """# GUI exposure audit

## GUI-P5-WIPE-ALL-GAME-ACTIONS-CLEARS-ONLY-MILESTONES: the button names the ledger

MEASURED 2026-09-11 off the source. The handler is `MilestoneStore.ClearAll`.

- it clears milestones
- it does not touch the ledger

**Fix (shipped).** Relabel, do not widen - a button that really wiped the ledger
would be a new destructive capability.

## ~~GUI-P8-DEFAULTS-SKIPS-TWO-DRAWN-SETTINGS~~: Defaults missed the slider

- one
- two

**Fix:** add `ghostAudioVolume` to `SettingsDefaults`.

## GUI-P3-ROUTE-INTERVAL-SNAPS-SILENTLY: the field ceil-snaps

Fix: not decided.
"""

    def setUp(self):
        self.items = {e["id"]: e for e in gmi.parse_todo(self.REAL_SHAPE)}

    def test_every_entry_is_found(self):
        self.assertEqual(sorted(self.items), [
            "GUI-P3-ROUTE-INTERVAL-SNAPS-SILENTLY",
            "GUI-P5-WIPE-ALL-GAME-ACTIONS-CLEARS-ONLY-MILESTONES",
            "GUI-P8-DEFAULTS-SKIPS-TWO-DRAWN-SETTINGS"])

    def test_a_bullet_list_does_not_end_an_entry_before_its_fix_line(self):
        fix = self.items["GUI-P5-WIPE-ALL-GAME-ACTIONS-CLEARS-ONLY-MILESTONES"]["fix"]
        self.assertTrue(fix.startswith("Relabel, do not widen"), fix)

    def test_the_bolded_marker_is_consumed_not_leaked(self):
        for id_ in self.items:
            self.assertNotIn("**", self.items[id_]["fix"])
            self.assertFalse(self.items[id_]["fix"].startswith("("),
                             "the emphasis run leaked into the fix text")

    def test_the_plain_form_works_too(self):
        self.assertEqual(self.items["GUI-P3-ROUTE-INTERVAL-SNAPS-SILENTLY"]["fix"],
                         "not decided.")

    def test_the_struck_state_is_read_off_the_heading(self):
        self.assertTrue(self.items["GUI-P8-DEFAULTS-SKIPS-TWO-DRAWN-SETTINGS"]["done"])
        self.assertFalse(self.items["GUI-P3-ROUTE-INTERVAL-SNAPS-SILENTLY"]["done"])

    def test_the_real_file_yields_fix_lines(self):
        # The regression this replaced: 52 entries, every fix empty.
        repo = os.path.dirname(os.path.dirname(os.path.dirname(
            os.path.abspath(__file__))))
        path = os.path.join(repo, "docs", "dev", "todo-and-known-bugs.md")
        if not os.path.exists(path):
            self.skipTest("todo file not present")
        with open(path, encoding="utf-8") as fh:
            entries = gmi.parse_todo(fh.read())
        self.assertGreater(len(entries), 20)
        self.assertGreater(sum(1 for e in entries if e["fix"]), 10,
                           "no Fix line was extracted from the real file")


class BackgroundStorageTests(unittest.TestCase):
    """A background is stored only for the kinds the page paints one for. It used
    to be stored for layout groups, bare labels and scroll views as well - 385
    values on a 35-capture corpus that nothing ever read."""

    def test_only_the_painting_kinds_carry_a_background(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        model = gmi.build_model([make_shots(root)], make_scenarios(root))
        seen = []

        def walk(n):
            if "bg" in n:
                seen.append((n["k"], n.get("s")))
            for ch in n.get("c") or ():
                walk(ch)

        for cap in model["captures"]:
            for r in cap["roots"]:
                walk(r)
        self.assertTrue(seen, "nothing was sampled at all")
        for kind, style in seen:
            self.assertTrue(kind in gmi.BG_KINDS or style == "box",
                            "%s/%s stored a background the page never paints"
                            % (kind, style))


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

    def test_an_impossible_budget_refuses_without_writing_anything(self):
        # An over-budget page that has already been written is an over-budget
        # page someone will open anyway, so the size is measured before the file
        # is opened.
        root = tempfile.mkdtemp()
        try:
            shots = make_shots(root)
            out = os.path.join(root, "m.html")
            idx = os.path.join(root, "m.json")
            rc = gmi.main(["--shots", shots, "--scenarios", make_scenarios(root),
                           "--out", out, "--index", idx, "--budget-mb", "0.001"])
            self.assertEqual(rc, 2, "over-budget page exited 0")
            self.assertFalse(os.path.exists(out), "the refused page was written")
            self.assertFalse(os.path.exists(idx), "the refused index was written")
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


class RichTextTests(unittest.TestCase):
    """KSP draws a Unity rich-text subset in labels and the dump carries the raw
    markup, so the page has to translate it - on a whitelist, and never through
    `innerHTML`, because the string belongs to a control."""

    def setUp(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        shots = make_shots(root)
        nasty = ('<b>Jebediah Kerman</b> <color=#ff0000>lost</color>'
                 '<script>alert(1)</script><iframe src=x>')
        path = os.path.join(shots, "syn-widgets-zynthia-advanced.gui.json")
        with open(path, encoding="utf-8") as fh:
            d = json.load(fh)
        d["roots"][0]["children"][-1]["text"] = nasty
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(d, fh)
        self.nasty = nasty
        self.model = gmi.build_model([shots], make_scenarios(root), with_photos=False)
        self.html = gmi.render_html(self.model)

    def test_the_whitelist_is_the_four_tags_unity_supports(self):
        self.assertIn("var RICH_TAG = /<(\\/?)(b|i|color|size)(=([^>]*))?>/gi;",
                      self.html)

    def test_the_translation_never_uses_innerhtml(self):
        body = self.html[self.html.index("function richText("):]
        body = body[:body.index("function renderNode(")]
        self.assertNotIn("innerHTML", body)
        self.assertIn("document.createTextNode", body)
        self.assertIn("document.createElement('span')", body)

    def test_a_non_whitelisted_tag_stays_in_the_payload_as_data(self):
        # It reaches the page escaped inside the JSON and lands as a text node,
        # so it shows as the tag the control really drew rather than executing.
        self.assertIn("\\u003cscript\\u003e", self.html)
        self.assertNotIn("<script>alert(1)</script>", self.html)
        self.assertNotIn("<iframe", self.html)

    def test_the_whole_string_survives_into_the_payload(self):
        cap = [c for c in self.model["captures"] if c["tab"] == "zynthia"][0]
        self.assertIn(self.nasty, _strings_of(cap["roots"][0]))

    def test_the_colour_and_size_arguments_are_validated(self):
        body = self.html[self.html.index("function richText("):]
        body = body[:body.index("function renderNode(")]
        self.assertIn("/^#[0-9a-f]{3,8}$|^[a-z]{3,20}$/i.test(arg)", body)
        self.assertIn("/^[0-9]{1,3}$/.test(arg)", body)


class DialogCaptureTests(unittest.TestCase):
    """A PopupDialog is a centred uGUI canvas with no presence in any control
    tree, so the Parsek windows' bounding box does not contain it. Cropping to
    that box captioned another mod's window as the modal."""

    def build(self, with_photos=True):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        shots = make_shots(root, log=LOG_WITH_DIALOG)
        for old, new in (("syn-widgets-zynthia-advanced", "dlg-wipe"),
                         ("syn-widgets-qorvex-advanced", "dlg-clean")):
            for ext in (".gui.json", ".png"):
                src = os.path.join(shots, old + ext)
                if os.path.exists(src):
                    os.rename(src, os.path.join(shots, new + ext))
        for label in ("dlg-wipe", "dlg-clean"):
            path = os.path.join(shots, label + ".gui.json")
            with open(path, encoding="utf-8") as fh:
                d = json.load(fh)
            d["label"] = label
            d["screenshotHint"] = label + ".png"
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(d, fh)
        return gmi.build_model([shots], make_scenarios(root),
                               with_photos=with_photos)

    def test_a_modal_capture_photographs_the_whole_frame(self):
        model = self.build()
        dlg = [c for c in model["captures"] if c["dialog"]][0]
        self.assertEqual([dlg["photo"]["x"], dlg["photo"]["y"],
                          dlg["photo"]["w"], dlg["photo"]["h"]],
                         [0, 0, 1280, 720])
        self.assertEqual(dlg["photo"]["whole"], 1)

    def test_a_capture_with_no_modal_still_crops_to_the_windows(self):
        model = self.build()
        plain = [c for c in model["captures"] if not c["dialog"]][0]
        self.assertNotEqual(plain["photo"]["w"], 1280)
        self.assertNotIn("whole", plain["photo"])

    def test_the_title_and_buttons_come_through_without_any_photo(self):
        model = self.build(with_photos=False)
        dlg = [c for c in model["captures"] if c["dialog"]][0]
        self.assertIsNone(dlg["photo"])
        self.assertEqual(dlg["dialog"]["title"], "Confirm: Wipe Recordings")
        self.assertEqual(dlg["dialog"]["buttons"], ["Wipe All", "Cancel"])
        html = gmi.render_html(model)
        self.assertIn("Confirm: Wipe Recordings", html)
        self.assertIn("Wipe All", html)

    def test_no_button_is_ever_fabricated(self):
        html = gmi.render_html(self.build())
        self.assertNotIn("['OK']", html)
        self.assertIn("if (cap.dialog.buttons.length){", html)
        self.assertIn("the seam reported no buttons on this modal", html)

    def test_the_modal_block_is_suppressed_in_overlay_mode_too(self):
        html = gmi.render_html(self.build())
        self.assertIn(".stage.overlay .dlg .dt,.stage.overlay .dlg .db,"
                      ".stage.overlay .dlg .dcap{display:none}", html)


class CensusFrameSizeTests(unittest.TestCase):
    """The census lanes capture at 1920x1080 (`[runtime] screenResolution`) while
    older corpora are 1280x720, and one mirror is built over both. Each capture
    must carry the frame its own dump was taken at - never a global 1280x720 -
    and a 1080p PNG that matches its dump must be sampled, not refused as a
    superSize mismatch."""

    def _write(self, path, label, screen):
        d = dump(label, "Parsek - Widgets", [
            node("label", [1500, 900, 190, 21], "far row"),
        ], grid_value="Zynthia", screen=screen)
        with open(os.path.join(path, label + ".gui.json"), "w", encoding="utf-8") as fh:
            json.dump(d, fh)
        tiny_png(os.path.join(path, label + ".png"), screen[0], screen[1],
                 bands=((300, 48, 380, 60),))

    def test_each_capture_keeps_its_own_frame_size(self):
        root = tempfile.mkdtemp()
        try:
            shots = make_shots(root)
            self._write(shots, "syn-widgets-zynthia-basic", (1920, 1080))
            model = gmi.build_model([shots], make_scenarios(root))
            by_label = {c["label"]: c for c in model["captures"]}
            self.assertEqual([1920, 1080], by_label["syn-widgets-zynthia-basic"]["screen"])
            self.assertEqual([1280, 720], by_label["syn-widgets-qorvex-advanced"]["screen"])
            big = by_label["syn-widgets-zynthia-basic"]
            self.assertTrue(big["roots"][0].get("bg"),
                            "a 1920x1080 frame matching its dump was not sampled")
        finally:
            shutil.rmtree(root, ignore_errors=True)


class SuperSizeGuardTests(unittest.TestCase):
    """A frame whose size disagrees with the dump would sample the wrong pixels
    for every control, and scaling it here would be a guess about which way."""

    def test_a_mismatched_frame_is_not_sampled_and_is_reported(self):
        root = tempfile.mkdtemp()
        try:
            shots = make_shots(root)
            # a superSize screenshot: twice the frame the dump was taken at
            tiny_png(os.path.join(shots, "syn-widgets-zynthia-advanced.png"),
                     2560, 1440)
            err = io.StringIO()
            real, sys.stderr = sys.stderr, err
            try:
                model = gmi.build_model([shots], make_scenarios(root), verbose=True)
            finally:
                sys.stderr = real
            bad = [c for c in model["captures"] if c["tab"] == "zynthia"][0]
            good = [c for c in model["captures"] if c["tab"] == "qorvex"][0]
            self.assertIsNone(bad["roots"][0].get("bg"),
                              "a mismatched frame was sampled anyway")
            self.assertTrue(good["roots"][0].get("bg"))
            self.assertIn("does not match the dump", err.getvalue())
        finally:
            shutil.rmtree(root, ignore_errors=True)


class ContainerSamplingTests(unittest.TestCase):
    """A container's colour is the colour of its OWN padding, not of whatever
    opaque child sits under the probe point.

    The defect this pins: the Kerbals window of `ksc-kerbals-outcomes-advanced`
    has a 404 px content box over its centre, so the window sampled that box and
    the whole window painted `#292929` while every other Kerbals capture painted
    `#444444`.
    """

    WIN = (68, 68, 68)      # the window's own grey
    BOX = (41, 41, 41)      # an opaque content box

    def frame(self, child_boxes):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        path = os.path.join(root, "f.png")
        tiny_png(path, 400, 300, self.WIN)
        # repaint the child boxes in the darker colour
        w, h, bpp, px = gmi.read_png(path)
        px = bytearray(px)
        for (x0, y0, x1, y1) in child_boxes:
            for y in range(y0, y1):
                for x in range(x0, x1):
                    o = (y * w + x) * bpp
                    px[o:o + 3] = bytes(self.BOX)
        return w, h, bpp, bytes(px)

    def test_a_full_cover_child_does_not_lend_the_window_its_colour(self):
        # The child covers the window's centre and most of its area; the window's
        # own colour survives only in the margin, which is where it must be read.
        child = (10, 40, 390, 290)
        w, h, bpp, px = self.frame([child])
        rect = [0, 0, 400, 300]
        naive, _ = gmi.sample_colors(w, h, bpp, px, rect)
        self.assertEqual(naive, "#292929",
                         "the fixture does not reproduce the defect")
        own, _ = gmi.sample_colors(w, h, bpp, px, rect,
                                   exclude=[[10, 40, 380, 250]])
        self.assertEqual(own, "#444444",
                         "the window took its child's colour")

    def test_the_child_still_reads_its_own_colour(self):
        child = (10, 40, 390, 290)
        w, h, bpp, px = self.frame([child])
        got, _ = gmi.sample_colors(w, h, bpp, px, [10, 40, 380, 250])
        self.assertEqual(got, "#292929")

    def test_a_totally_covered_container_falls_back_to_its_whole_rect(self):
        # No own surface to read: a consistent wrong colour beats None, and that
        # is what the old behaviour always did.
        w, h, bpp, px = self.frame([(0, 0, 400, 300)])
        got, _ = gmi.sample_colors(w, h, bpp, px, [0, 0, 400, 300],
                                   exclude=[[0, 0, 400, 300]])
        self.assertEqual(got, "#292929")

    def test_a_leaf_with_no_children_is_unaffected(self):
        w, h, bpp, px = self.frame([])
        a, _ = gmi.sample_colors(w, h, bpp, px, [0, 0, 400, 300])
        b, _ = gmi.sample_colors(w, h, bpp, px, [0, 0, 400, 300], exclude=())
        self.assertEqual(a, b)
        self.assertEqual(a, "#444444")

    def test_the_whole_pipeline_reads_the_container_colour(self):
        # End to end through compact_tree, which is what hands the sampler the
        # children: a window with a full-cover dark box must still be its own
        # colour on the page.
        w, h, bpp, px = self.frame([(10, 40, 390, 290)])

        def sampler(rect, exclude=()):
            return gmi.sample_colors(w, h, bpp, px, rect, exclude=exclude)

        root = node("window", [0, 0, 400, 300], "W", style="window", children=[
            node("box", [10, 40, 380, 250], None, style="box", children=[])])
        out = gmi.compact_tree(root, [0, 0], sampler)
        self.assertEqual(out["bg"], "#444444",
                         "the window still took its child's colour")
        self.assertEqual(out["c"][0]["bg"], "#292929")

    def test_the_real_capture_that_found_this(self):
        # The capture itself, if the census dirs are on this machine.
        import glob
        hits = glob.glob(os.path.join(
            os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(
                os.path.abspath(__file__))))),
            "Parsek-gui-census-dump", "harness", "results",
            "2026-09-11_0548_*_shots", "ksc-kerbals-outcomes-advanced.gui.json"))
        if not hits:
            self.skipTest("the census shots directory is not on this machine")
        shots = os.path.dirname(hits[0])
        model = gmi.build_model([shots], None)
        cap = [c for c in model["captures"]
               if c["label"] == "ksc-kerbals-outcomes-advanced"][0]
        win = [r for r in cap["roots"]
               if not r.get("foreign") and "Kerbals" in (r.get("title") or "")][0]
        self.assertEqual(win.get("bg"), "#444444",
                         "the Kerbals window is still painting its box's colour")


class MutationPinningTests(unittest.TestCase):
    """One cell per mutation that survived the first pass. Each pins a decision
    the suite could not previously tell from its opposite."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def two_runs(self, second_differs=True):
        """The same key photographed twice, older run first."""
        early = make_shots(self.root, name="2026-09-11_0548_SYN-1-census-widgets_shots")
        late = make_shots(self.root, name="2026-09-15_1744_SYN-1-census-widgets_shots")
        for path, utc in ((early, "2026-09-11T05:49:25Z"), (late, "2026-09-15T17:45:00Z")):
            for label in ("syn-widgets-zynthia-advanced", "syn-widgets-qorvex-advanced"):
                f = os.path.join(path, label + ".gui.json")
                with open(f, encoding="utf-8") as fh:
                    d = json.load(fh)
                d["capturedUtc"] = utc
                if second_differs and path is late:
                    d["roots"][0]["children"][-1]["text"] = "a genuinely new row"
                with open(f, "w", encoding="utf-8") as fh:
                    json.dump(d, fh)
        return gmi.build_model([early, late], make_scenarios(self.root),
                               with_photos=False)

    def test_m3_the_earliest_capture_is_the_before(self):
        model = self.two_runs()
        info = [v for v in model["keys"].values() if v["changed"]][0]
        self.assertIn("2026-09-11_0548", info["before"])
        self.assertIn("2026-09-15_1744", info["after"])

    def test_m5_identical_trees_with_different_ids_pair_as_unchanged(self):
        model = self.two_runs(second_differs=False)
        self.assertTrue(model["keys"])
        for k, info in model["keys"].items():
            self.assertEqual(len(info["all"]), 2, k)
            self.assertFalse(info["changed"],
                             "two identical trees were reported as a change")
            self.assertIsNone(info["measured"])

    def test_m5_a_real_tree_difference_pairs_as_changed(self):
        model = self.two_runs(second_differs=True)
        changed = [v for v in model["keys"].values() if v["changed"]]
        self.assertTrue(changed, "a real tree difference was not reported")
        self.assertIsNotNone(changed[0]["measured"])

    def test_m11_a_tighter_budget_produces_fewer_photo_bytes(self):
        shots = make_shots(self.root)
        scen = make_scenarios(self.root)
        roomy = gmi.build_model([shots], scen, with_photos=True,
                                budget=16 * 1024 * 1024)
        # Tight enough that the first (quant 8) step does not fit, so the
        # encoder has to walk down its own ladder.
        tight = gmi.build_model([shots], scen, with_photos=True,
                                budget=5 * 1024)
        self.assertGreater(roomy["photoBytes"], 0)
        self.assertLess(tight["photoBytes"], roomy["photoBytes"],
                        "the budget did not change what was encoded")

    def test_m15_grid_label_runs_are_present_only_when_a_frame_is(self):
        with_png = make_shots(self.root, name="2026-09-11_0548_SYN-1-census-widgets_shots")
        without = make_shots(self.root, name="2026-09-12_0548_SYN-1-census-widgets_shots",
                             with_png=False)
        scen = make_scenarios(self.root)

        def grid_of(model):
            cap = model["captures"][0]
            found = []

            def walk(n):
                if n.get("k") == "buttongrid":
                    found.append(n)
                for ch in n.get("c") or ():
                    walk(ch)

            for r in cap["roots"]:
                walk(r)
            return found[0]

        self.assertIn("gi", grid_of(gmi.build_model([with_png], scen)))
        self.assertNotIn("gi", grid_of(gmi.build_model([without], scen)))

    def test_m16_the_seam_log_beats_the_label_on_the_window(self):
        # The label says one window, the log says another: the log wins, because
        # the label is a filename and the log is what was on screen.
        shots = make_shots(self.root, log=LOG_TWO_STATES.replace(
            "window=widgets", "window=gizmos").replace("openWindows=widgets",
                                                       "openWindows=gizmos"))
        model = gmi.build_model([shots], make_scenarios(self.root), with_photos=False)
        self.assertEqual(model["captures"][0]["window"], "gizmos")
        self.assertNotEqual(model["captures"][0]["window"], "widgets")

    def test_m17_overlay_mode_suppresses_every_text_layer(self):
        model = gmi.build_model([make_shots(self.root)],
                                make_scenarios(self.root), with_photos=False)
        html = gmi.render_html(model)
        for rule in (".stage.overlay .gn .tx,.stage.overlay .gn .cb,"
                     ".stage.overlay .gn .gl{display:none}",
                     ".stage.overlay .kwin>.kt{display:none}",
                     ".stage.overlay .dlg .dt,.stage.overlay .dlg .db,"
                     ".stage.overlay .dlg .dcap{display:none}"):
            self.assertIn(rule, html, "overlay mode lost a text-suppression rule")
        self.assertIn(".stage.overlay .gn{background:none !important;"
                      "color:transparent !important;", html)


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


# --------------------------------------------------------------------------
# the gallery half: a capture taken under a mocked view model
# --------------------------------------------------------------------------

MOCK_BLOCK = {"stateId": "widgets.zynthia.lost", "window": "widgets",
              "catalogue": "gui-mock/1", "states": 312,
              "covers": ["RosterStatus.Lost", "KerbalEndState.Dead"]}

LOG_MOCK = """
[LOG 00:00:01] [Parsek][INFO][TestCommands] uiaction complexity mode=advanced already=true
[LOG 00:00:02] [Parsek][INFO][TestCommands] uiaction open window=widgets open=true already=false frames=1
[LOG 00:00:03] [Parsek][INFO][TestCommands] uiaction rect window=widgets rect=270,8,700,400 frames=1
[LOG 00:00:04] [Parsek][INFO][TestCommands] uiaction describe scene=SPACECENTER complexity=advanced windows=2 open=1 openWindows=widgets
[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction tab window=widgets tab=zynthia index=0 already=true
[LOG 00:00:06] [Parsek][INFO][TestCommands] capturescreenshot ok label=mock-widgets-zynthia-lost-advanced
[LOG 00:00:07] [Parsek][INFO][TestCommands] capturescreenshot ok label=mock-widgets-hold-empty-advanced
""".strip()


def make_mock_shots(root, name="2026-09-21_2200_SYN-2-gallery-mock_shots",
                    utc="2026-09-21T22:00:00Z", with_png=True):
    """A gallery shots dir: two mocked captures of one window, one whose state id
    pins a tab and one whose does not."""
    path = os.path.join(root, name)
    os.makedirs(path)
    with open(os.path.join(path, "KSP.log"), "w", encoding="utf-8") as fh:
        fh.write(LOG_MOCK + "\n")
    lost = dump("mock-widgets-zynthia-lost-advanced", "Parsek - Widgets", [
        node("label", [284, 76, 190, 23], "Column", style="box"),
        node("label", [478, 115, 220, 21], "zynthia row"),
    ], grid_value="Zynthia", utc=utc, mock=dict(MOCK_BLOCK))
    hold = dump("mock-widgets-hold-empty-advanced", "Parsek - Widgets", [
        node("label", [284, 76, 190, 23], "Column", style="box"),
    ], grid_value="Zynthia", utc=utc,
        mock=dict(MOCK_BLOCK, stateId="widgets.hold.empty", covers=[]))
    for d in (lost, hold):
        with open(os.path.join(path, d["label"] + ".gui.json"), "w",
                  encoding="utf-8") as fh:
            json.dump(d, fh)
        if with_png:
            tiny_png(os.path.join(path, d["label"] + ".png"))
    return path


class MockProvenanceTests(unittest.TestCase):
    """The dump's `mock` block, and the facets a mocked capture is filed under.

    ABSENT means real, so the whole committed corpus has to read exactly as it
    did; and a block with nothing usable in it must not turn a real capture into
    a synthetic one.
    """

    def test_an_ordinary_dump_carries_no_provenance(self):
        self.assertIsNone(gmi.mock_provenance(dump("x", "Parsek", [])))
        self.assertIsNone(gmi.mock_provenance({}))
        self.assertIsNone(gmi.mock_provenance(None))

    def test_a_block_with_no_state_id_is_not_provenance(self):
        for bad in ({}, {"stateId": ""}, {"stateId": "   "}, {"window": "widgets"},
                    "widgets.zynthia.lost", []):
            self.assertIsNone(gmi.mock_provenance({"mock": bad}), repr(bad))

    def test_the_block_is_read_by_the_key_names_the_writer_emits(self):
        prov = gmi.mock_provenance({"mock": MOCK_BLOCK})
        self.assertEqual(prov["stateId"], "widgets.zynthia.lost")
        self.assertEqual(prov["window"], "widgets")
        self.assertEqual(prov["catalogue"], "gui-mock/1")
        self.assertEqual(prov["states"], 312)
        self.assertEqual(prov["covers"],
                         ["RosterStatus.Lost", "KerbalEndState.Dead"])

    def test_a_non_numeric_state_count_does_not_throw(self):
        prov = gmi.mock_provenance({"mock": dict(MOCK_BLOCK, states="lots",
                                                 covers="not a list")})
        self.assertEqual(prov["states"], 0)
        self.assertEqual(prov["covers"], [])

    def test_the_tab_comes_out_of_the_state_id_when_it_is_a_tab_token(self):
        self.assertEqual(gmi.mock_facets(MOCK_BLOCK, {"zynthia", "qorvex"}),
                         ("widgets", "zynthia", "lost"))

    def test_a_family_that_is_no_tab_stays_in_the_state(self):
        prov = dict(MOCK_BLOCK, stateId="widgets.hold.escrow-short")
        self.assertEqual(gmi.mock_facets(prov, {"zynthia"}),
                         ("widgets", None, "hold-escrow-short"))

    def test_the_blocks_window_field_is_the_authority(self):
        # The id's first segment and the field agree in the catalogue; where they
        # do not, the field wins and the whole id becomes the state, because the
        # field is what the applier actually drove.
        prov = {"stateId": "kerbals.roster.lost", "window": "widgets"}
        self.assertEqual(gmi.mock_facets(prov, ()),
                         ("widgets", None, "kerbals-roster-lost"))

    def test_an_id_with_no_window_field_falls_back_to_its_first_segment(self):
        self.assertEqual(gmi.mock_facets({"stateId": "career.banner.divergent"}, ()),
                         ("career", None, "banner-divergent"))


class MockedCaptureTests(unittest.TestCase):
    """A mocked capture in the page beside real ones: its own dataset, its own
    badge, never paired with a real capture, and never the default view."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        real = make_shots(self.root)
        mocked = make_mock_shots(self.root)
        self.scen = make_scenarios(self.root)
        # The gallery lane HAS a saveTemplate - it needs a loaded game - and it
        # is deliberately the SAME one the real lane flew, which is the case the
        # dump block exists for.
        make_scenarios(self.root, "SYN-2-gallery-mock",
                       "fixtures/saves/syn-fixture")
        self.model = gmi.build_model([real, mocked], self.scen, with_photos=False)
        self.html = gmi.render_html(self.model)
        self.mock = [c for c in self.model["captures"] if c.get("mocked")]
        self.real = [c for c in self.model["captures"] if not c.get("mocked")]

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_a_mocked_capture_is_filed_under_its_own_dataset(self):
        self.assertEqual(len(self.mock), 2)
        self.assertEqual({c["fixture"] for c in self.mock}, {gmi.MOCK_FIXTURE})
        # ...although the SPEC named a real fixture, which is the whole point.
        self.assertEqual({c["fixture"] for c in self.real}, {"syn-fixture"})

    def test_the_facets_come_from_the_state_id_not_from_the_label(self):
        lost = [c for c in self.mock if c["state"] == "lost"][0]
        self.assertEqual((lost["window"], lost["tab"], lost["mode"]),
                         ("widgets", "zynthia", "advanced"))
        hold = [c for c in self.mock if c["state"] == "hold-empty"][0]
        self.assertIsNone(hold["tab"])

    def test_a_mocked_capture_can_never_pair_with_a_real_one(self):
        # Structural, not cosmetic: `fixture` is part of the pair key.
        for k, info in self.model["keys"].items():
            ids = set(info["all"])
            mocked = {c["id"] for c in self.mock} & ids
            self.assertTrue(not mocked or mocked == ids,
                            "key %s mixes mocked and real captures" % k)

    def test_two_mocked_states_of_one_tab_do_not_pair_with_each_other(self):
        # `widgets.zynthia.lost` and a second state of the same tab would agree
        # on every other facet if the state tail were all the key carried.
        keys = {c["key"] for c in self.mock}
        self.assertEqual(len(keys), 2)
        for cap in self.mock:
            self.assertIn(cap["mocked"]["stateId"], cap["key"])

    def test_the_default_dataset_is_never_the_mocked_one(self):
        self.assertEqual(self.model["defaultFixture"], "syn-fixture")
        self.assertNotEqual(self.model["defaultFixture"], gmi.MOCK_FIXTURE)

    def test_the_default_is_pinned_in_the_model_rather_than_derived_on_the_page(self):
        self.assertIn("S.fixture = (FIX_ORDER.indexOf(M.defaultFixture) >= 0)",
                      self.html)
        # and the derivation that used to live in the page is gone
        self.assertNotIn("Object.keys(breadth[b]", self.html)

    def test_a_pinned_default_wins_and_a_bogus_one_refuses(self):
        model = gmi.build_model([make_shots(self.root, "2026-09-11_0549_SYN-1-census-widgets_shots")],
                                self.scen, with_photos=False,
                                pin_fixture="syn-fixture")
        self.assertEqual(model["defaultFixture"], "syn-fixture")
        with self.assertRaises(SystemExit):
            gmi.build_model([make_shots(self.root, "2026-09-11_0550_SYN-1-census-widgets_shots")],
                            self.scen, with_photos=False, pin_fixture="no-such")

    def test_the_badge_is_emitted_for_a_mocked_capture_only(self):
        self.assertIn("MOCKED DATA", self.html)
        self.assertIn("if (cap.mocked){", self.html)
        # the rail's per-window count, the stage header, the status line and the
        # Compare rows all go through the one flag function
        self.assertIn("appendFlags(head, cap)", self.html)
        self.assertIn("appendFlags(kline, after)", self.html)
        self.assertIn("mocked'", self.html)
        self.assertIn("w.mockedCount", self.html)

    def test_the_window_record_counts_the_mocked_captures_separately(self):
        w = [w for w in self.model["windows"] if w["token"] == "widgets"][0]
        self.assertEqual(w["captureCount"], 4)
        self.assertEqual(w["mockedCount"], 2)

    def test_the_index_answers_how_much_of_this_is_real(self):
        idx = gmi.build_index(self.model)
        self.assertEqual(idx["captureCount"], 4)
        self.assertEqual(idx["mockedCaptureCount"], 2)
        self.assertEqual(idx["defaultFixture"], "syn-fixture")
        rows = idx["windows"]["widgets"]["states"]
        self.assertTrue(any(r["mocked"] for r in rows.values()))
        self.assertTrue(any(not r["mocked"] for r in rows.values()))
        self.assertEqual(idx["windows"]["widgets"]["capturesMocked"], 2)

    def test_an_old_dump_with_no_block_behaves_exactly_as_before(self):
        plain = gmi.build_model([make_shots(self.root, "2026-09-11_0551_SYN-1-census-widgets_shots")],
                               self.scen, with_photos=False)
        for cap in plain["captures"]:
            self.assertIsNone(cap["mocked"])
            self.assertEqual(cap["fixture"], "syn-fixture")
            # the key gained no segment
            self.assertEqual(cap["key"].count("|"), 5)


# --------------------------------------------------------------------------
# retiring false coverage, mechanically
# --------------------------------------------------------------------------

class SupersededByKeyTests(unittest.TestCase):
    """A later capture of the same key retires the earlier one.

    Which is what makes the audit's four stale labels stop reading as coverage
    with no label named anywhere: wave 5 re-flew their lanes, so the old
    captures are superseded by construction.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp()
        first = make_shots(self.root, "2026-09-11_0548_SYN-1-census-widgets_shots",
                           utc="2026-09-11T05:49:25Z")
        second = make_shots(self.root, "2026-09-21_2106_SYN-1-census-widgets_shots",
                            utc="2026-09-21T21:06:00Z", extra_row="a new row")
        self.model = gmi.build_model([first, second], make_scenarios(self.root),
                                     with_photos=False)
        self.html = gmi.render_html(self.model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_the_older_capture_of_a_key_is_marked_superseded_by_the_newer(self):
        old = [c for c in self.model["captures"] if c["runId"] == "2026-09-11_0548"]
        new = [c for c in self.model["captures"] if c["runId"] == "2026-09-21_2106"]
        self.assertEqual(len(old), 2)
        for cap in old:
            self.assertTrue(cap["supersededBy"].startswith("2026-09-21_2106/"))
        for cap in new:
            self.assertIsNone(cap.get("supersededBy"))

    def test_the_superseded_side_is_still_the_pairs_before(self):
        for info in self.model["keys"].values():
            self.assertTrue(info["before"].startswith("2026-09-11_0548/"))
            self.assertTrue(info["after"].startswith("2026-09-21_2106/"))
            self.assertEqual(info["superseded"], [info["before"]])

    def test_coverage_counts_distinct_keys_not_files(self):
        idx = gmi.build_index(self.model)
        self.assertEqual(idx["captureCount"], 4)
        self.assertEqual(idx["distinctKeyCount"], 2)
        self.assertEqual(idx["supersededCaptureCount"], 2)
        s = self.model["windowSummaries"]["widgets"]
        self.assertEqual((s["captures"], s["superseded"]), (4, 2))
        self.assertEqual(s["statesReal"], 2)

    def test_the_page_never_lands_a_click_on_a_superseded_capture(self):
        body = self.html[self.html.index("function pick(win, tab, state, mode, fixture){"):]
        body = body[:body.index("\n/* ---- rendering")]
        self.assertIn("var live = pool.filter(function(c){ return !c.supersededBy "
                      "&& !c.retired && !c.outdated; });", body)
        self.assertIn("if (live.length) pool = live;", body)

    def test_the_rail_lists_the_current_capture_of_each_state(self):
        # One row per state, and it is the CURRENT capture of that state.
        rows = self.model["railRows"]["widgets"]
        self.assertEqual(len(rows), 2)
        for r in rows:
            self.assertTrue(r["id"].startswith("2026-09-21_2106/"), r["id"])
        self.assertIn("var ordered = (M.railRows || {})[w.token];", self.html)
        self.assertIn("? ' stale' : ''", self.html)

    def test_the_page_does_not_open_on_a_superseded_capture(self):
        # Found by photographing the page: the first capture in MODEL order is
        # the OLDEST of the main window, so the page opened badged `superseded`,
        # which is the one picture the mirror must not lead with. The opening
        # capture now goes through the same ranking a click does.
        self.assertIn("var r0 = pick(firstWin, null, null, S.mode, S.fixture)",
                      self.html)
        self.assertIn("var first = (r0 && r0.cap) || capsFor('main')[0]", self.html)
        body = self.html[self.html.index("var firstWin = capsFor('main').length"):]
        body = body[:body.index("select(first,")]
        self.assertIn("pick(", body)

    def test_a_state_photographed_once_is_not_superseded(self):
        model = gmi.build_model(
            [make_shots(self.root, "2026-09-11_0549_SYN-1-census-widgets_shots")],
            make_scenarios(self.root), with_photos=False)
        for cap in model["captures"]:
            self.assertIsNone(cap.get("supersededBy"))
        self.assertEqual(gmi.build_index(model)["supersededCaptureCount"], 0)


class StaleCaptureBannerTests(unittest.TestCase):
    def test_a_stale_capture_says_so_and_links_the_current_one(self):
        root = tempfile.mkdtemp()
        html = gmi.render_html(gmi.build_model([make_shots(root)], make_scenarios(root),
                                               with_photos=False))
        sel = html[html.index("function select(cap, exact){"):]
        sel = sel[:sel.index("\nfunction ", 10)]
        self.assertIn("if (isStale(cap)){", sel)
        self.assertIn("var cur = currentOf(cap);", sel)
        self.assertIn("function currentOf(cap){", html)
        self.assertIn("c.key !== cap.key || c.window !== cap.window || isStale(c)", html)


class LabelButtonStyleTests(unittest.TestCase):
    def test_a_label_styled_button_has_no_outline_or_bevel(self):
        # KSP draws a label-styled button (the Career name link) as plain text.
        self.assertIn(".gn.k-button.s-label,.gn.k-button.s-{background:none;border:0;box-shadow:none;",
                      gmi.CSS)


class RemovedTabTests(unittest.TestCase):
    """A tab the product removed stops being drawn on captures taken after it.

    A grid records only its selected item, so a capture's tab bar is assembled from
    every token its window has shown. The Career window lost its Facilities and
    Milestones tabs on 2026-09-24; without this pass the new two-tab captures drew
    all four.
    """

    @staticmethod
    def cap(utc, tab, **kw):
        c = {"window": "career", "tab": tab, "capturedUtc": utc,
             "tabNames": [{"token": t, "index": i, "name": t.title()}
                          for i, t in enumerate(("contracts", "strategies",
                                                 "facilities", "milestones"))]}
        c.update(kw)
        return c

    def test_a_tab_with_no_live_capture_leaves_newer_bars_only(self):
        old_fac = self.cap("2026-09-23T10:00:00Z", "facilities",
                           retired={"spec": "GUI-5", "since": "x"})
        old_mil = self.cap("2026-09-23T10:00:00Z", "milestones", supersededBy="old_mil2")
        old_con = self.cap("2026-09-23T10:00:00Z", "contracts", supersededBy="new")
        new_con = self.cap("2026-09-24T10:00:00Z", "contracts")
        removed = gmi.prune_removed_tabs([old_fac, old_mil, old_con, new_con])
        self.assertEqual(sorted(removed["career"]), ["facilities", "milestones"])
        self.assertEqual([t["token"] for t in new_con["tabNames"]],
                         ["contracts", "strategies"])
        # the old captures were drawn while the tabs existed and keep them
        self.assertEqual(len(old_fac["tabNames"]), 4)
        self.assertEqual(len(old_con["tabNames"]), 4)

    def test_a_capture_drawn_before_the_removal_is_outdated_and_stale(self):
        fac = self.cap("2026-09-23T10:00:00Z", "facilities",
                       retired={"spec": "GUI-5", "since": "x"})
        sandbox = self.cap("2026-09-21T10:00:00Z", "contracts")
        fresh = self.cap("2026-09-24T10:00:00Z", "contracts")
        gmi.prune_removed_tabs([fac, sandbox, fresh])
        self.assertEqual(sandbox["outdated"]["tabs"], ["facilities"])
        self.assertTrue(gmi.is_stale(sandbox))
        self.assertNotIn("outdated", fresh)
        self.assertFalse(gmi.is_stale(fresh))

    def test_a_tab_with_a_live_capture_stays(self):
        fac = self.cap("2026-09-23T10:00:00Z", "facilities")
        new_con = self.cap("2026-09-24T10:00:00Z", "contracts")
        removed = gmi.prune_removed_tabs([fac, new_con])
        self.assertNotIn("facilities", removed.get("career", []))
        self.assertIn("facilities", [t["token"] for t in new_con["tabNames"]])


class LayoutEpochTests(unittest.TestCase):
    """A capture drawn before its window's current layout is `outdated`.

    PR #1792 re-laid the Timeline's filter area without removing a tab, so
    `prune_removed_tabs` had nothing to go on and every Timeline state a lane had
    not re-flown stayed on the rail in the three-row layout beside the new
    two-row captures. The epoch table is the explicit floor.
    """

    EPOCHS = {"career": {"utc": "2026-09-24T15:22:01Z", "pr": 1796}}

    @staticmethod
    def cap(utc, window="career", tab="contracts", **kw):
        c = {"window": window, "tab": tab, "capturedUtc": utc}
        c.update(kw)
        return c

    def test_a_capture_before_the_epoch_is_outdated_and_stale(self):
        old = self.cap("2026-09-24T15:22:00Z")
        marked = gmi.mark_layout_epochs([old], self.EPOCHS)
        self.assertEqual(marked, [old])
        self.assertEqual(old["outdated"]["epoch"],
                         {"utc": "2026-09-24T15:22:01Z", "pr": 1796})
        self.assertEqual(old["outdated"]["tabs"], [])
        self.assertTrue(gmi.is_stale(old))

    def test_a_capture_exactly_at_the_epoch_is_current(self):
        at = self.cap("2026-09-24T15:22:01Z")
        after = self.cap("2026-09-24T15:22:01.5Z")
        self.assertEqual(gmi.mark_layout_epochs([at, after], self.EPOCHS), [])
        self.assertNotIn("outdated", at)
        self.assertFalse(gmi.is_stale(at))

    def test_a_window_with_no_epoch_is_not_judged(self):
        other = self.cap("2020-01-01T00:00:00Z", window="settings")
        self.assertEqual(gmi.mark_layout_epochs([other], self.EPOCHS), [])
        self.assertNotIn("outdated", other)

    def test_a_capture_with_no_readable_time_is_not_judged(self):
        for utc in ("", None, "not a time"):
            c = self.cap(utc)
            self.assertEqual(gmi.mark_layout_epochs([c], self.EPOCHS), [], utc)
            self.assertNotIn("outdated", c)

    def test_an_epoch_joins_a_removed_tab_reason_rather_than_replacing_it(self):
        c = self.cap("2026-09-23T10:00:00Z",
                     outdated={"tabs": ["facilities"], "window": "career"})
        gmi.mark_layout_epochs([c], self.EPOCHS)
        self.assertEqual(c["outdated"]["tabs"], ["facilities"])
        self.assertEqual(c["outdated"]["epoch"]["pr"], 1796)

    def test_superseded_and_retired_captures_are_marked_too(self):
        # Orthogonal flags: an old-layout capture a later run superseded is still
        # an old-layout picture, which is what Compare's BEFORE badge says.
        sup = self.cap("2026-09-23T10:00:00Z", supersededBy="x")
        ret = self.cap("2026-09-23T10:00:00Z", retired={"spec": "S", "since": "r"})
        self.assertEqual(gmi.mark_layout_epochs([sup, ret], self.EPOCHS), [sup, ret])

    def test_the_default_table_is_the_module_table(self):
        c = self.cap("2020-01-01T00:00:00Z", window="timeline")
        gmi.mark_layout_epochs([c])
        self.assertEqual(c["outdated"]["epoch"]["pr"],
                         gmi.LAYOUT_EPOCHS["timeline"]["pr"])
        self.assertEqual(gmi.mark_layout_epochs(
            [self.cap("2020-01-01T00:00:00Z", window="timeline")], {}), [])

    def test_the_table_names_seam_windows_and_readable_instants(self):
        sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
        import hlib
        for win, ep in gmi.LAYOUT_EPOCHS.items():
            self.assertIn(win, hlib.UIACTION_WINDOW_VALUES, win)
            self.assertIsNotNone(gmi.parse_utc(ep["utc"]), win)
            self.assertTrue(ep["utc"].endswith("Z"), win)
            self.assertIsInstance(ep["pr"], int, win)

    def test_parse_utc(self):
        self.assertEqual(gmi.parse_utc("2026-09-23T21:34:53Z"),
                         gmi.parse_utc("2026-09-23T21:34:53+00:00"))
        self.assertLess(gmi.parse_utc("2026-09-23T21:34:53Z"),
                        gmi.parse_utc("2026-09-23T21:34:53.25Z"))
        self.assertIsNone(gmi.parse_utc("2026-13-45"))


class LayoutEpochEndToEndTests(unittest.TestCase):
    """The epoch through `build_model`, the rail and the index.

    SYN-1 photographed both states before the widgets window's epoch; SYN-2, a
    different lane, re-photographed only zynthia at the epoch itself. Qorvex is
    therefore neither superseded (no later capture of its key) nor retired (its
    own lane never flew again): without the epoch it stayed current in the old
    layout, which is the Timeline defect this rule closes.
    """

    EPOCH = "2026-09-22T20:04:00Z"

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.old = make_shots(self.root, "2026-09-15_1744_SYN-1-census-widgets_shots",
                              utc="2026-09-15T17:44:00Z")
        self.new = make_shots(self.root, "2026-09-22_2004_SYN-2-census-widgets_shots",
                              utc=self.EPOCH)
        for ext in (".gui.json", ".png"):
            os.remove(os.path.join(self.new, "syn-widgets-qorvex-advanced" + ext))
        make_scenarios(self.root)
        self.scenarios = make_scenarios(self.root, spec_id="SYN-2-census-widgets")

    def build(self, epochs):
        return gmi.build_model([self.old, self.new], self.scenarios,
                               with_photos=False, layout_epochs=epochs)

    def by(self, model, run, tab):
        (c,) = [c for c in model["captures"]
                if c["runId"].startswith(run) and c["tab"] == tab]
        return c

    def test_without_an_epoch_the_old_layout_state_stays_current(self):
        model = self.build({})
        qorvex = self.by(model, "2026-09-15", "qorvex")
        self.assertFalse(gmi.is_stale(qorvex))
        self.assertIn(qorvex["id"], [r["id"] for r in model["railRows"]["widgets"]])

    def test_the_epoch_takes_the_old_layout_state_off_the_rail(self):
        model = self.build({"widgets": {"utc": self.EPOCH, "pr": 1}})
        qorvex = self.by(model, "2026-09-15", "qorvex")
        old_z = self.by(model, "2026-09-15", "zynthia")
        new_z = self.by(model, "2026-09-22", "zynthia")
        self.assertTrue(gmi.is_stale(qorvex))
        self.assertEqual(qorvex["outdated"]["epoch"]["pr"], 1)
        self.assertNotIn("supersededBy", qorvex)
        self.assertNotIn("retired", qorvex)
        self.assertTrue(old_z.get("supersededBy") and old_z.get("outdated"))
        self.assertNotIn("outdated", new_z, "a capture AT the epoch is current")
        live = [c["id"] for c in model["captures"] if not gmi.is_stale(c)]
        self.assertEqual(live, [new_z["id"]])
        self.assertEqual(model["keys"][qorvex["key"]]["outdated"]["epoch"]["pr"], 1)
        s = model["windowSummaries"]["widgets"]
        self.assertEqual(s["outdated"], 2)
        self.assertEqual(s["statesReal"], 1, "an old-layout state is not coverage")
        idx = gmi.build_index(model)
        self.assertEqual(idx["outdatedCaptureCount"], 2)
        self.assertEqual(idx["outdatedKeyCount"], 1)
        self.assertEqual(idx["layoutEpochs"], {"widgets": {"utc": self.EPOCH, "pr": 1}})

    def test_an_epoch_does_not_make_a_tab_read_as_removed(self):
        # Qorvex's only capture is old-layout, not superseded or retired: the tab
        # still exists, so the new capture's tab bar keeps it.
        model = self.build({"widgets": {"utc": self.EPOCH, "pr": 1}})
        new_z = self.by(model, "2026-09-22", "zynthia")
        self.assertIn("qorvex", [t["token"] for t in new_z["tabNames"]])
        self.assertEqual(self.by(model, "2026-09-15", "qorvex")["outdated"]["tabs"], [])

    def test_the_page_script_parses(self):
        # A quote slip in the page's script leaves an empty page and no Python
        # error, so the emitted script is parsed where node is on PATH.
        node = shutil.which("node")
        if not node:
            self.skipTest("node is not on PATH")
        import subprocess
        html = gmi.render_html(self.build({"widgets": {"utc": self.EPOCH, "pr": 1}}))
        scripts = re.findall(r"<script[^>]*>(.*?)</script>", html, re.S)
        self.assertTrue(scripts)
        for i, js in enumerate(scripts):
            path = os.path.join(self.root, "page%d.js" % i)
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(js)
            r = subprocess.run([node, "--check", path], capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stderr)

    def test_the_page_badges_the_epoch_and_never_opens_on_it(self):
        html = gmi.render_html(self.build({"widgets": {"utc": self.EPOCH, "pr": 1}}))
        self.assertIn("'old layout (before #' + ep.pr + ')'", html)
        body = html[html.index("function pick(win, tab, state, mode, fixture){"):]
        body = body[:body.index("\n/* ---- rendering")]
        self.assertIn("!c.outdated", body)


class RetiredStateTests(unittest.TestCase):
    """A state its own lane no longer produces is retired.

    Superseding needs a later capture of the SAME key, so a state a re-flown lane
    simply stopped photographing (the Kerbals "owner chain" after the round-2
    rebuild: in GUI-11's 2026-09-15_1744 run, absent from its 2026-09-22_2004
    run) stayed the current picture forever. Only a run that can speak for the
    lane retires anything: a PASS result, or - where no result says - a run that
    photographed the same window. A failed run never does.
    """

    @staticmethod
    def cap(run, label, window="widgets", key=None, spec="SYN-1", **kw):
        c = {"id": run + "/" + label, "runId": run, "specId": spec,
             "label": label, "window": window, "key": key or label}
        c.update(kw)
        return c

    def test_a_complete_newer_run_without_the_state_retires_it(self):
        old = self.cap("2026-09-15_1744", "owner-chain")
        keep = self.cap("2026-09-15_1744", "roster")
        new = self.cap("2026-09-22_2004", "roster")
        out = gmi.mark_retired([old, keep, new], [
            {"specId": "SYN-1", "runId": "2026-09-15_1744", "complete": True},
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": True}])
        self.assertEqual(out, [old])
        self.assertEqual(old["retired"], {"spec": "SYN-1", "since": "2026-09-22_2004"})
        self.assertNotIn("retired", keep)
        self.assertNotIn("retired", new)

    def test_a_failed_newer_run_retires_nothing(self):
        old = self.cap("2026-09-15_1744", "owner-chain")
        new = self.cap("2026-09-22_2004", "roster")
        out = gmi.mark_retired([old, new], [
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": False}])
        self.assertEqual(out, [])
        self.assertNotIn("retired", old)

    def test_a_complete_run_with_no_captures_at_all_retires_nothing(self):
        old = self.cap("2026-09-15_1744", "owner-chain")
        out = gmi.mark_retired([old], [
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": True}])
        self.assertEqual(out, [])

    def test_without_a_result_the_newer_run_must_have_photographed_the_window(self):
        old = self.cap("2026-09-15_1744", "owner-chain")
        other = self.cap("2026-09-22_2004", "settings", window="settings")
        out = gmi.mark_retired([old, other], [
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": None}])
        self.assertEqual(out, [], "a run that never opened the window retired it")
        same = self.cap("2026-09-22_2004", "roster")
        out = gmi.mark_retired([old, other, same], [
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": None}])
        self.assertEqual(out, [old])

    def test_a_newer_run_carrying_the_label_or_the_key_keeps_it(self):
        old = self.cap("2026-09-15_1744", "owner-chain", key="k1")
        by_label = self.cap("2026-09-22_2004", "owner-chain", key="k2")
        self.assertEqual(gmi.mark_retired([old, by_label], [
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": True}]), [])
        old = self.cap("2026-09-15_1744", "owner-chain", key="k1")
        by_key = self.cap("2026-09-22_2004", "renamed", key="k1")
        self.assertEqual(gmi.mark_retired([old, by_key], [
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": True}]), [])

    def test_a_state_a_later_run_photographed_again_is_not_retired(self):
        # Dropped at one run, back at the next: the newest word is "produced".
        old = self.cap("2026-09-15_1744", "owner-chain")
        gap = self.cap("2026-09-20_1000", "roster")
        back = self.cap("2026-09-22_2004", "owner-chain", key="other")
        out = gmi.mark_retired([old, gap, back], [
            {"specId": "SYN-1", "runId": r, "complete": True}
            for r in ("2026-09-20_1000", "2026-09-22_2004")])
        self.assertNotIn(old, out)
        # ...while the gap run's own state, which the newest run dropped, is.
        self.assertEqual(out, [gap])

    def test_only_the_same_scenario_and_only_newer_runs_count(self):
        old = self.cap("2026-09-15_1744", "owner-chain")
        foreign = self.cap("2026-09-22_2004", "roster", spec="SYN-2")
        older = self.cap("2026-09-10_0900", "roster")
        out = gmi.mark_retired([old, foreign, older], [
            {"specId": "SYN-2", "runId": "2026-09-22_2004", "complete": True},
            {"specId": "SYN-1", "runId": "2026-09-10_0900", "complete": True}])
        # SYN-2's run says nothing about SYN-1, and an OLDER SYN-1 run lacking
        # the state says nothing either (the reverse, the newer 1744 run lacking
        # the older run's `roster`, is a retirement).
        self.assertNotIn(old, out)
        self.assertEqual(out, [older])

    def test_a_superseded_capture_is_not_also_retired(self):
        old = self.cap("2026-09-15_1744", "owner-chain", supersededBy="x/y")
        new = self.cap("2026-09-22_2004", "roster")
        self.assertEqual(gmi.mark_retired([old, new], [
            {"specId": "SYN-1", "runId": "2026-09-22_2004", "complete": True}]), [])

    def test_the_retirement_is_dated_from_the_first_witness(self):
        old = self.cap("2026-09-15_1744", "owner-chain")
        a = self.cap("2026-09-22_2004", "roster")
        b = self.cap("2026-09-22_2004_run2", "roster")
        gmi.mark_retired([old, b, a], [
            {"specId": "SYN-1", "runId": r, "complete": True}
            for r in ("2026-09-22_2004_run2", "2026-09-22_2004")])
        self.assertEqual(old["retired"]["since"], "2026-09-22_2004")

    def test_run_ids_order_by_minute_then_collision_then_attempt(self):
        ids = ["2026-09-22_1841_run2", "2026-09-22_1841_a2", "2026-09-22_1841",
               "2026-09-21_2359", "2026-09-22_1841_run2_a2"]
        self.assertEqual(sorted(ids, key=gmi.run_sort_key), [
            "2026-09-21_2359", "2026-09-22_1841", "2026-09-22_1841_a2",
            "2026-09-22_1841_run2", "2026-09-22_1841_run2_a2"])

    def test_the_result_json_beside_the_shots_dir_says_whether_it_completed(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        shots = os.path.join(root, "2026-09-22_2004_SYN-1-census-widgets_shots")
        os.makedirs(shots)
        self.assertIsNone(gmi.run_complete(shots))
        res = shots[:-len("_shots")] + ".json"
        for verdict, want in (("PASS", True), ("PARSEK-FAIL", False),
                              ("INVALID", False)):
            with open(res, "w", encoding="utf-8") as fh:
                json.dump({"verdict": verdict}, fh)
            self.assertIs(gmi.run_complete(shots), want, verdict)
        with open(res, "w", encoding="utf-8") as fh:
            fh.write("{not json")
        self.assertIsNone(gmi.run_complete(shots))


class RetiredEndToEndTests(unittest.TestCase):
    """The same rule through `build_model` and the page."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.old = make_shots(self.root, "2026-09-15_1744_SYN-1-census-widgets_shots",
                              utc="2026-09-15T17:44:00Z")
        self.new = make_shots(self.root, "2026-09-22_2004_SYN-1-census-widgets_shots",
                              utc="2026-09-22T20:04:00Z")
        # The newer run no longer photographs the qorvex state at all.
        for ext in (".gui.json", ".png"):
            os.remove(os.path.join(self.new, "syn-widgets-qorvex-advanced" + ext))

    def build(self, verdict):
        if verdict is not None:
            with open(self.new[:-len("_shots")] + ".json", "w",
                      encoding="utf-8") as fh:
                json.dump({"verdict": verdict}, fh)
        return gmi.build_model([self.old, self.new], make_scenarios(self.root),
                               with_photos=False)

    def qorvex(self, model):
        return [c for c in model["captures"] if c["tab"] == "qorvex"]

    def test_a_pass_run_retires_the_state_it_no_longer_captures(self):
        model = self.build("PASS")
        (cap,) = self.qorvex(model)
        self.assertEqual(cap["retired"], {"spec": "SYN-1-census-widgets",
                                          "since": "2026-09-22_2004"})
        self.assertEqual(model["keys"][cap["key"]]["retired"]["since"],
                         "2026-09-22_2004")
        idx = gmi.build_index(model)
        self.assertEqual(idx["retiredCaptureCount"], 1)
        self.assertEqual(idx["retiredKeyCount"], 1)
        s = model["windowSummaries"]["widgets"]
        self.assertEqual(s["retired"], 1)
        self.assertEqual(s["statesReal"], 1, "a retired state still counts")

    def test_a_failed_run_retires_nothing(self):
        model = self.build("PARSEK-FAIL")
        (cap,) = self.qorvex(model)
        self.assertNotIn("retired", cap)
        self.assertEqual(gmi.build_index(model)["retiredCaptureCount"], 0)

    def test_without_a_result_a_run_that_photographed_the_window_retires(self):
        model = self.build(None)
        (cap,) = self.qorvex(model)
        self.assertEqual(cap["retired"]["since"], "2026-09-22_2004")

    def test_the_page_treats_a_retired_capture_as_stale(self):
        html = gmi.render_html(self.build("PASS"))
        body = html[html.index("function pick(win, tab, state, mode, fixture){"):]
        body = body[:body.index("\n/* ---- rendering")]
        self.assertIn("return !c.supersededBy && !c.retired && !c.outdated;", body)
        self.assertIn("function isStale(c){ return !!(c.hoverEmpty || c.supersededBy "
                      "|| c.retired || c.outdated); }", html)
        self.assertIn("text: 'no longer captured by ' + cap.retired.spec", html)
        self.assertIn("+ ' since ' + cap.retired.since, short: 'retired',", html)
        rail = html[html.index("function buildRail(){"):]
        rail = rail[:rail.index("\nfunction showView(){")]
        self.assertIn("if (isStale(c)) return;", rail)


class RailOrderTests(unittest.TestCase):
    """The rail reads top to bottom as the window evolves: grouped by tab in the
    tab bar's own order, and within a tab from the least drawn state to the most.
    Mechanical (a node count off the capture), total (Basic before Advanced, then
    the label) and therefore deterministic."""

    @staticmethod
    def cap(label, tab, state, mode, cx, **kw):
        c = {"id": "r/" + label, "label": label, "window": "kerbals", "tab": tab,
             "state": state, "mode": mode, "complexity": cx}
        c.update(kw)
        return c

    def corpus(self):
        return [
            self.cap("flights-unfolded", "outcomes", "unfolded", "advanced", 90),
            self.cap("roster-expanded", "roster", "expanded", "advanced", 70),
            self.cap("flights-folded", "outcomes", "folded", "advanced", 40),
            self.cap("roster-adv", "roster", "", "advanced", 30),
            self.cap("roster-basic", "roster", "", "basic", 30),
            self.cap("roster-collapsed", "roster", "collapsed", "advanced", 30),
            self.cap("dialog", None, "confirm", "advanced", 12),
        ]

    ORDER = {"kerbals": {"roster": 0, "outcomes": 1}}

    def ids(self, caps, order=None):
        return [r["id"][2:] for r in
                gmi.rail_rows(caps, order or self.ORDER)["kerbals"]]

    def test_grouped_by_tab_in_tab_bar_order_then_simple_to_complex(self):
        self.assertEqual(self.ids(self.corpus()), [
            "dialog",
            "roster-basic", "roster-adv", "roster-collapsed", "roster-expanded",
            "flights-folded", "flights-unfolded"])

    def test_the_groups_are_named_by_their_tab_token(self):
        rows = gmi.rail_rows(self.corpus(), self.ORDER)["kerbals"]
        self.assertEqual([r["g"] for r in rows],
                         ["", "roster", "roster", "roster", "roster",
                          "outcomes", "outcomes"])

    def test_the_order_does_not_depend_on_the_input_order(self):
        caps = self.corpus()
        want = self.ids(caps)
        for i in range(len(caps)):
            rotated = caps[i:] + caps[:i]
            self.assertEqual(self.ids(rotated), want)
            self.assertEqual(self.ids(list(reversed(rotated))), want)

    def test_the_tab_order_is_the_tab_bars_not_the_alphabet(self):
        flipped = {"kerbals": {"roster": 1, "outcomes": 0}}
        got = self.ids(self.corpus(), flipped)
        self.assertEqual(got[1:3], ["flights-folded", "flights-unfolded"])

    def test_an_unknown_tab_follows_the_known_ones(self):
        caps = self.corpus() + [self.cap("zz", "archive", "", "advanced", 1)]
        self.assertEqual(self.ids(caps)[-1], "zz")

    def test_a_tabless_capture_is_grouped_by_the_cell_its_grid_shows(self):
        caps = self.corpus() + [self.cap("hover", None, "hover", "advanced", 35,
                                         gridIndex=1)]
        rows = gmi.rail_rows(caps, self.ORDER)["kerbals"]
        self.assertEqual([r["g"] for r in rows if r["id"] == "r/hover"], ["outcomes"])

    def test_one_row_per_state_and_it_is_the_current_capture(self):
        stale = self.cap("roster-expanded-old", "roster", "expanded", "advanced", 5,
                         supersededBy="r/roster-expanded")
        caps = [stale] + self.corpus()
        got = self.ids(caps)
        self.assertIn("roster-expanded", got)
        self.assertNotIn("roster-expanded-old", got)
        retired = self.cap("owner-chain", "roster", "owner-chain", "advanced", 50,
                           retired={"spec": "S", "since": "x"})
        self.assertIn("owner-chain", self.ids(self.corpus() + [retired]),
                      "a state with no current capture must still be listed")

    def test_tab_order_merges_the_seam_index_with_the_grid(self):
        caps = [self.cap("a", "roster", "", "basic", 1, gridIndex=0),
                self.cap("b", "archive", "", "basic", 1, gridIndex=2),
                self.cap("c", "outcomes", "", "basic", 1, gridIndex=7)]
        got = gmi.tab_order({"kerbals": {"roster": 0, "outcomes": 1}}, caps)
        self.assertEqual(dict(got["kerbals"]),
                         {"roster": 0, "outcomes": 1, "archive": 2})

    def test_build_model_records_complexity_and_orders_the_rail(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        model = gmi.build_model([make_shots(root)], make_scenarios(root),
                                with_photos=False)
        by_label = {c["label"]: c for c in model["captures"]}
        z = by_label["syn-widgets-zynthia-advanced"]
        q = by_label["syn-widgets-qorvex-advanced"]
        # window + grid + three rows vs window + grid + two rows
        self.assertEqual((z["complexity"], q["complexity"]), (5, 4))
        self.assertEqual([r["g"] for r in model["railRows"]["widgets"]],
                         ["zynthia", "qorvex"])
        html = gmi.render_html(model)
        self.assertIn('"railRows":', html)
        rail = html[html.index("function buildRail(){"):]
        rail = rail[:rail.index("\nfunction showView(){")]
        self.assertIn("ghead = el('div', 'tg', r.g ? tabName(w.token, r.g) : '(no tab)');",
                      rail)
        self.assertIn("if (ghead && !gshown) ghead.classList.add('hidden');", rail)


class NewestTabNameTests(unittest.TestCase):
    """A tab is named, everywhere the page names it, by the NEWEST capture of it.

    The Kerbals tabs were renamed ("Roster State" -> "Roster", "Mission Outcomes"
    -> "Flights") under unchanged seam tokens, and first-seen kept the old names
    in the rail, the header and Compare for every new capture.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

    def build(self, name, utc, newtv):
        base = make_shots(self.root, utc="2026-09-11T05:49:25Z")
        other = make_shots(self.root, name=name, utc=utc)
        f = os.path.join(other, "syn-widgets-zynthia-advanced.gui.json")
        with open(f, encoding="utf-8") as fh:
            d = json.load(fh)
        d["roots"][0]["children"][0]["textValue"] = newtv
        with open(f, "w", encoding="utf-8") as fh:
            json.dump(d, fh)
        # the older directory is listed FIRST either way: order must not matter
        dirs = sorted([base, other])
        model = gmi.build_model(dirs, make_scenarios(self.root), with_photos=False)
        w = [w for w in model["windows"] if w["token"] == "widgets"][0]
        return {t["token"]: t["name"] for t in w["tabs"]}

    def test_a_newer_run_renames_the_tab(self):
        names = self.build("2026-09-22_2004_SYN-1-census-widgets_shots",
                           "2026-09-22T20:04:00Z", "Zynthia Prime")
        self.assertEqual(names["zynthia"], "Zynthia Prime")
        self.assertEqual(names["qorvex"], "Qorvex")

    def test_an_older_run_does_not(self):
        names = self.build("2026-09-01_0100_SYN-1-census-widgets_shots",
                           "2026-09-01T01:00:00Z", "Zynthia Classic")
        self.assertEqual(names["zynthia"], "Zynthia")


POINTER_EMPTY = ("[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction pointer "
                 "at=133,197 park=false via=active frames=1 focus=true nudge=true "
                 "fgOutcome=attached fg=true tooltip=- tooltipFrame=0")
POINTER_FULL = POINTER_EMPTY.replace(
    "tooltip=- tooltipFrame=0",
    "tooltip=Go to the Qorvex tab tooltipFrame=42")
POINTER_PARKED = POINTER_EMPTY.replace("park=false", "park=true")
POINTER_OLD = ("[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction pointer "
               "at=133,197 park=false via=active frames=1")


class HoverFromTheLogTests(unittest.TestCase):
    """A hover capture that photographed no hover, read off the artifacts.

    The pointer op's own result line is the authority where it carries a
    `tooltip=` key. The older census runs predate that key, and an absent
    statement is not an empty tooltip - so there the fallback is the capture's
    own tree being byte-identical to a sibling of the same run, which is what
    "the hover changed nothing" means in a dump.
    """

    def _model(self, pointer_line, twin=False):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, True)
        log = LOG_TWO_STATES.replace(
            "[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction tab "
            "window=widgets tab=zynthia index=0 already=true",
            pointer_line)
        path = make_shots(root, log=log)
        if twin:
            # A second capture with the SAME tree as the hover one, which is
            # what an unpainted hover leaves behind. It needs its own frame too:
            # the measured offsets are part of the tree, so a capture with no
            # PNG is not byte-identical to one with a PNG.
            with open(os.path.join(path, "syn-widgets-zynthia-advanced.gui.json"),
                      encoding="utf-8") as fh:
                src = json.load(fh)
            src["label"] = "syn-widgets-idle-advanced"
            with open(os.path.join(path, "syn-widgets-idle-advanced.gui.json"),
                      "w", encoding="utf-8") as fh:
                json.dump(src, fh)
            shutil.copyfile(
                os.path.join(path, "syn-widgets-zynthia-advanced.png"),
                os.path.join(path, "syn-widgets-idle-advanced.png"))
        return gmi.build_model([path], make_scenarios(root), with_photos=False)

    def test_the_pointer_lines_own_tooltip_is_read_out_of_the_log(self):
        rep = gmi.parse_ksp_log(LOG_TWO_STATES.replace(
            "[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction tab "
            "window=widgets tab=zynthia index=0 already=true", POINTER_EMPTY))
        ptr = rep["captures"]["syn-widgets-zynthia-advanced"]["pointer"]
        self.assertEqual(ptr["tooltip"], gmi.POINTER_TOOLTIP_EMPTY)
        self.assertEqual(ptr["at"], "133,197")
        self.assertFalse(ptr["park"])

    def test_a_populated_tooltip_carries_its_spaces(self):
        rep = gmi.parse_ksp_log(LOG_TWO_STATES.replace(
            "[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction tab "
            "window=widgets tab=zynthia index=0 already=true", POINTER_FULL))
        ptr = rep["captures"]["syn-widgets-zynthia-advanced"]["pointer"]
        self.assertEqual(ptr["tooltip"], "Go to the Qorvex tab")

    def test_an_older_line_with_no_key_says_nothing_rather_than_empty(self):
        rep = gmi.parse_ksp_log(LOG_TWO_STATES.replace(
            "[LOG 00:00:05] [Parsek][INFO][TestCommands] uiaction tab "
            "window=widgets tab=zynthia index=0 already=true", POINTER_OLD))
        ptr = rep["captures"]["syn-widgets-zynthia-advanced"]["pointer"]
        self.assertIsNone(ptr["tooltip"])

    def test_an_empty_tooltip_flags_the_capture_from_the_log(self):
        model = self._model(POINTER_EMPTY)
        cap = [c for c in model["captures"] if c["label"].endswith("zynthia-advanced")][0]
        self.assertEqual(cap["hoverEmpty"]["why"], "log")
        self.assertEqual(cap["hoverEmpty"]["at"], "133,197")

    def test_a_populated_tooltip_is_not_flagged(self):
        model = self._model(POINTER_FULL, twin=True)
        for cap in model["captures"]:
            self.assertIsNone(cap.get("hoverEmpty"), cap["label"])

    def test_a_parked_pointer_is_not_a_hover_at_all(self):
        model = self._model(POINTER_PARKED, twin=True)
        for cap in model["captures"]:
            self.assertIsNone(cap.get("hoverEmpty"), cap["label"])

    def test_an_identical_sibling_flags_it_where_the_log_cannot_say(self):
        model = self._model(POINTER_OLD, twin=True)
        flagged = [c for c in model["captures"] if c.get("hoverEmpty")]
        self.assertTrue(flagged)
        self.assertEqual(flagged[0]["hoverEmpty"]["why"], "twin")
        self.assertTrue(flagged[0]["hoverEmpty"]["twin"])

    def test_with_no_sibling_and_no_key_nothing_is_claimed(self):
        model = self._model(POINTER_OLD)
        for cap in model["captures"]:
            self.assertIsNone(cap.get("hoverEmpty"), cap["label"])

    def test_a_flagged_capture_is_out_of_coverage_and_greyed(self):
        model = self._model(POINTER_EMPTY)
        idx = gmi.build_index(model)
        self.assertEqual(idx["hoverNotCapturedCount"], 1)
        s = model["windowSummaries"]["widgets"]
        self.assertEqual(s["hoverNotCaptured"], 1)
        html = gmi.render_html(model)
        self.assertIn("hover not captured", html)
        self.assertIn("if (cap.hoverEmpty) return;", html)
        self.assertIn("(isStale(c) ? ' stale' : '')", html)
        self.assertIn("function isStale(c){ return !!(c.hoverEmpty || c.supersededBy "
                      "|| c.retired || c.outdated); }", html)


class LabelVersusLogTests(unittest.TestCase):
    """Where the label and the seam log disagree about what was on screen.

    The log wins, as it always has; this only SAYS so. The quiet form is the one
    worth catching: a label that names NO tab reads as the window's default, so a
    log that selected a later tab contradicts it as plainly as a different token
    would - which is exactly the case `ksc-timeline-basic` (really the Re-Fly
    tab) and `ksc-missions-basic` are in.
    """

    TABS = {"zynthia": 0, "qorvex": 1}

    def test_a_label_naming_another_window_disagrees(self):
        out = gmi.label_log_disagreements(
            {"window": "gloops", "tab": None, "state": ""},
            {"window": "widgets", "tab": None}, {})
        self.assertEqual(out, [{"field": "window", "label": "gloops",
                                "log": "widgets"}])

    def test_a_label_naming_another_tab_disagrees(self):
        out = gmi.label_log_disagreements(
            {"window": "widgets", "tab": "zynthia", "state": ""},
            {"window": "widgets", "tab": "qorvex"}, self.TABS)
        self.assertEqual(out, [{"field": "tab", "label": "zynthia",
                                "log": "qorvex"}])

    def test_a_silent_label_over_a_non_default_tab_disagrees(self):
        out = gmi.label_log_disagreements(
            {"window": "widgets", "tab": None, "state": ""},
            {"window": "widgets", "tab": "qorvex"}, self.TABS)
        self.assertEqual(out, [{"field": "tab", "label": "", "log": "qorvex"}])

    def test_a_silent_label_over_the_default_tab_agrees(self):
        self.assertEqual(gmi.label_log_disagreements(
            {"window": "widgets", "tab": None, "state": ""},
            {"window": "widgets", "tab": "zynthia"}, self.TABS), [])

    def test_a_window_with_no_tabs_is_never_a_tab_disagreement(self):
        self.assertEqual(gmi.label_log_disagreements(
            {"window": "main", "tab": None, "state": "tooltip-timeline"},
            {"window": "main", "tab": None}, {}), [])

    def test_a_state_token_that_is_a_tab_of_that_window_disagrees(self):
        out = gmi.label_log_disagreements(
            {"window": "widgets", "tab": None, "state": "qorvex-live"},
            {"window": "widgets", "tab": "zynthia"}, self.TABS)
        self.assertIn({"field": "state", "label": "qorvex", "log": "zynthia"}, out)

    def test_a_resolved_display_name_is_not_a_disagreement(self):
        # The Kerbals rebuild: the heading was renamed and the seam token was
        # not, so the label's leading token IS the tab, under its display name.
        self.assertEqual(gmi.label_log_disagreements(
            {"window": "widgets", "tab": None, "state": "folded"},
            {"window": "widgets", "tab": "qorvex"}, self.TABS,
            tab_alias="flights"), [])

    def test_the_capture_is_still_filed_under_the_log(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, True)
        # Both tabs are in the vocabulary and the qorvex tab was the one
        # SELECTED when the zynthia-labelled frame was taken.
        shot = ("[LOG 00:00:06] [Parsek][INFO][TestCommands] capturescreenshot ok "
                "label=syn-widgets-zynthia-advanced")
        pick = ("[LOG 00:00:07] [Parsek][INFO][TestCommands] uiaction tab "
                "window=widgets tab=qorvex index=1 already=false")
        log = LOG_TWO_STATES.replace(shot + chr(10) + pick,
                                     pick + chr(10) + shot)
        model = gmi.build_model([make_shots(root, log=log)],
                                make_scenarios(root), with_photos=False)
        cap = [c for c in model["captures"]
               if c["label"] == "syn-widgets-zynthia-advanced"][0]
        self.assertEqual(cap["tab"], "qorvex")
        self.assertEqual([d["field"] for d in cap["disagrees"]], ["tab"])
        idx = gmi.build_index(model)
        self.assertEqual(idx["labelDisagreementCount"], 1)
        html = gmi.render_html(model)
        self.assertIn("label disagrees with the log", html)

    def test_a_mocked_capture_has_nothing_to_disagree_with(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, True)
        make_scenarios(root, "SYN-2-gallery-mock", "fixtures/saves/syn-fixture")
        model = gmi.build_model([make_mock_shots(root)], make_scenarios(root),
                                with_photos=False)
        for cap in model["captures"]:
            self.assertEqual(cap["disagrees"], [])


class WindowSummaryTests(unittest.TestCase):
    """The per-window Compare header: distinct states, and NEW / GONE read off
    spec re-flights rather than off a file count."""

    def _model(self, second_extra="a new row"):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, True)
        first = make_shots(root, "2026-09-11_0548_SYN-1-census-widgets_shots",
                           utc="2026-09-11T05:49:25Z")
        second = make_shots(root, "2026-09-21_2106_SYN-1-census-widgets_shots",
                            utc="2026-09-21T21:06:00Z", extra_row=second_extra)
        # The newest run drops one state and adds another, which is what a
        # re-flown lane that changed its steps does.
        os.remove(os.path.join(second, "syn-widgets-qorvex-advanced.gui.json"))
        os.remove(os.path.join(second, "syn-widgets-qorvex-advanced.png"))
        extra = dump("syn-widgets-zynthia-folded-advanced", "Parsek - Widgets",
                     [node("label", [284, 76, 190, 23], "Column", style="box")],
                     grid_value="Zynthia", utc="2026-09-21T21:06:10Z")
        with open(os.path.join(second, extra["label"] + ".gui.json"), "w",
                  encoding="utf-8") as fh:
            json.dump(extra, fh)
        with open(os.path.join(second, "KSP.log"), encoding="utf-8") as fh:
            log = fh.read()
        log += ("[LOG 00:00:09] [Parsek][INFO][TestCommands] capturescreenshot "
                "ok label=syn-widgets-zynthia-folded-advanced\n")
        with open(os.path.join(second, "KSP.log"), "w", encoding="utf-8") as fh:
            fh.write(log)
        return gmi.build_model([first, second], make_scenarios(root),
                              with_photos=False)

    def test_new_and_gone_come_from_the_specs_own_re_flight(self):
        model = self._model()
        s = model["windowSummaries"]["widgets"]
        self.assertEqual(s["reflownSpecs"], 1)
        self.assertEqual(s["new"], 1)    # the folded state the newest run added
        self.assertEqual(s["gone"], 1)   # the qorvex tab it stopped taking
        self.assertEqual(s["changed"], 1)

    def test_a_spec_that_flew_once_reports_neither(self):
        root = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, root, True)
        model = gmi.build_model([make_shots(root)], make_scenarios(root),
                                with_photos=False)
        s = model["windowSummaries"]["widgets"]
        self.assertEqual((s["reflownSpecs"], s["new"], s["gone"]), (0, 0, 0))
        self.assertEqual((s["statesReal"], s["statesMocked"]), (2, 0))

    def test_the_header_is_rendered_from_the_measured_summary(self):
        html = gmi.render_html(self._model())
        self.assertIn("function summaryHead(win){", html)
        self.assertIn("sec.appendChild(summaryHead(win));", html)
        self.assertIn("M.windowSummaries", html)
        self.assertIn("states ", html)
        self.assertIn("known to the seam, never photographed: ", html)

    def test_the_uncaptured_list_is_the_windows_own(self):
        model = self._model()
        s = model["windowSummaries"]["widgets"]
        for m in s["uncaptured"]:
            self.assertEqual(m["window"], "widgets")


# --------------------------------------------------------------------------
# the owner's notes, and the blob that carries them out and back
# --------------------------------------------------------------------------

class NotesBlobTests(unittest.TestCase):
    """The export payload is a SCHEMA, because it leaves the page and is read
    back by an agent that has nothing else to key on."""

    ROW = {"key": "r1 -> r2|widgets|zynthia|lost|advanced|mock",
           "window": "widgets", "tab": "zynthia", "state": "lost",
           "mode": "advanced", "fixture": "mock", "mocked": True,
           "mockState": "widgets.zynthia.lost",
           "beforeId": "r1/a", "afterId": "r2/b",
           "verdict": "change", "note": "the column is under the wrong heading"}

    def test_the_key_is_the_six_facets_the_design_names(self):
        self.assertEqual(
            gmi.note_key("widgets", "zynthia", "lost", "advanced", "mock",
                         "r1 -> r2"),
            "r1 -> r2|widgets|zynthia|lost|advanced|mock")
        # a missing facet is empty rather than absent, so the key stays aligned
        self.assertEqual(gmi.note_key("widgets", None, "", None, "syn-fixture"),
                         "|widgets||||syn-fixture")

    def test_the_blob_carries_its_schema_and_the_pages_stamp(self):
        blob = gmi.notes_blob([self.ROW], stamp="2026-09-22T10:00:00Z",
                              scope="widgets")
        self.assertEqual(blob["schema"], gmi.NOTES_SCHEMA)
        self.assertEqual(blob["generatedUtc"], "2026-09-22T10:00:00Z")
        self.assertEqual(blob["scope"], "widgets")
        self.assertEqual(blob["count"], 1)
        self.assertEqual(sorted(blob["notes"][0]), sorted(gmi.NOTES_FIELDS))

    def test_every_field_an_agent_needs_to_act_without_the_page_is_in_it(self):
        row = gmi.notes_blob([self.ROW])["notes"][0]
        for field in ("beforeId", "afterId", "window", "tab", "state", "mode",
                      "fixture", "mocked", "verdict", "note"):
            self.assertIn(field, row)
        self.assertIs(row["mocked"], True)
        self.assertEqual(row["afterId"], "r2/b")

    def test_the_round_trip_through_export_and_import_is_lossless(self):
        blob = gmi.notes_blob([self.ROW], stamp="2026-09-22T10:00:00Z")
        back, dropped, schema = gmi.parse_notes_blob(json.dumps(blob))
        self.assertEqual(dropped, 0)
        self.assertEqual(schema, gmi.NOTES_SCHEMA)
        self.assertEqual(list(back), [self.ROW["key"]])
        for field in gmi.NOTES_FIELDS:
            self.assertEqual(back[self.ROW["key"]][field], self.ROW[field], field)

    def test_a_bare_list_and_a_single_row_are_both_accepted(self):
        rows = gmi.notes_blob([self.ROW])["notes"]
        back, dropped, _ = gmi.parse_notes_blob(json.dumps(rows))
        self.assertEqual((len(back), dropped), (1, 0))
        back, dropped, _ = gmi.parse_notes_blob(json.dumps(rows[0]))
        self.assertEqual((len(back), dropped), (1, 0))

    def test_a_row_with_no_key_is_rebuilt_from_its_facets_or_dropped(self):
        row = dict(self.ROW)
        del row["key"]
        back, dropped, _ = gmi.parse_notes_blob(json.dumps([row]))
        self.assertEqual(dropped, 0)
        self.assertEqual(list(back), ["|widgets|zynthia|lost|advanced|mock"])
        back, dropped, _ = gmi.parse_notes_blob(json.dumps([{"note": "hi"}, 7]))
        self.assertEqual((len(back), dropped), (0, 2))

    def test_something_that_is_not_a_blob_says_so(self):
        self.assertEqual(gmi.parse_notes_blob("not json")[2], "not JSON")
        self.assertEqual(gmi.parse_notes_blob('{"x":1}')[2], "no notes in it")

    def test_the_markdown_table_carries_the_same_rows(self):
        md = gmi.notes_markdown(gmi.notes_blob([self.ROW]))
        lines = md.splitlines()
        self.assertTrue(lines[0].startswith("| window | tab | state |"))
        self.assertIn("| --- |", lines[1])
        self.assertIn("the column is under the wrong heading", lines[2])
        self.assertIn("| yes |", lines[2])

    def test_a_pipe_in_a_note_cannot_break_the_table(self):
        md = gmi.notes_markdown(gmi.notes_blob([dict(self.ROW, note="a | b")]))
        self.assertIn("a / b", md)
        self.assertEqual(md.splitlines()[2].count("|"), 9)


class NotesInThePageTests(unittest.TestCase):
    """The page's own half of the notes: one field per row, storage guarded
    everywhere, two formats out, one textarea back, and no download link."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.model = gmi.build_model([make_shots(self.root)],
                                     make_scenarios(self.root),
                                     with_photos=False,
                                     stamp="2026-09-22T10:00:00Z")
        self.html = gmi.render_html(self.model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_the_row_shape_is_the_generators_field_set_not_a_second_list(self):
        # The one thing that keeps the blob the page writes and the blob the
        # Python side parses the same shape.
        self.assertIn("M.notesFields.forEach(function(f){ row[f] = ''; });",
                      self.html)
        self.assertIn("M.notesFields.forEach(function(f){ row[f] = (f in r) ? r[f] : ''; });",
                      self.html)
        self.assertEqual(self.model["notesFields"], list(gmi.NOTES_FIELDS))
        self.assertEqual(self.model["notesSchema"], gmi.NOTES_SCHEMA)
        self.assertEqual(self.model["noteVerdicts"], list(gmi.NOTE_VERDICTS))

    def test_both_a_state_view_and_a_compare_row_get_a_field(self):
        self.assertIn("host.appendChild(notesBox(stateCtx(cap)));", self.html)
        self.assertIn("sec.appendChild(notesBox(pairCtx(r.info)));", self.html)

    def test_the_key_is_the_run_pair_and_the_five_facets(self):
        self.assertIn("function noteKey(runPair, win, tab, state, mode, fixture){",
                      self.html)
        self.assertIn("key: noteKey((b ? b.runId : '') + ' -> ' + a.runId",
                      self.html)

    def test_every_notes_storage_access_is_guarded(self):
        for fn in ("function loadNotes(){", "function saveNotes(){"):
            self.assertIn(fn, self.html)
            body = self.html[self.html.index(fn):]
            body = body[:body.index("\n}")]
            self.assertIn("try {", body, "%s has no try block" % fn)
            self.assertIn("catch (e)", body, "%s has no catch" % fn)
        # every localStorage mention in the page is inside one of the four
        # accessors, which are the only places it appears
        self.assertEqual(self.html.count("window.localStorage"), 4)

    def test_a_refused_store_is_said_out_loud_rather_than_swallowed(self):
        # A fold that does not persist is a nuisance; a VERDICT that does not is
        # lost work, so the page says so beside the field and in the panel.
        self.assertIn("STORE_OK = false", self.html)
        self.assertIn("this browser refused storage - export before you close the tab",
                      self.html)
        self.assertIn("Copy the blob out before you close the tab.", self.html)

    def test_the_export_offers_both_formats_over_every_note(self):
        self.assertIn("function notesBlob(scope){", self.html)
        self.assertIn("function notesMarkdown(blob){", self.html)
        self.assertIn("'JSON blob'", self.html)
        self.assertIn("'markdown table'", self.html)
        # Copy all is the whole store: the panel asks for no scope.
        body = self.html[self.html.index("function paintNotesPanel(){"):]
        body = body[:body.index("\n}")]
        self.assertIn("var blob = notesBlob('');", body)
        self.assertIn("'Copy all'", body)
        self.assertIn("scope: scope || 'all windows'", self.html)

    def test_the_clipboard_has_a_visible_fallback(self):
        self.assertIn("navigator.clipboard.writeText", self.html)
        self.assertIn("selectAllIn(pre)", self.html)
        self.assertIn("user-select:all", self.html)
        self.assertIn("the clipboard was refused", self.html)

    def test_there_is_no_download_link_and_no_server(self):
        for needle in ("createObjectURL", "download=", "'download'", "<a download",
                       "fetch(", "XMLHttpRequest"):
            self.assertNotIn(needle, self.html, needle)

    def test_a_pasted_blob_merges_back(self):
        self.assertIn("function mergeNotes(text){", self.html)
        self.assertIn("aria-label', 'paste a notes blob'", self.html)
        self.assertIn("dropped ' + dropped + ' row(s) with nothing to key on",
                      self.html)

    def test_the_pages_stamp_travels_with_the_blob(self):
        self.assertEqual(self.model["generatedUtc"], "2026-09-22T10:00:00Z")
        self.assertIn("2026-09-22T10:00:00Z", self.html)
        self.assertIn("generatedUtc: M.generatedUtc || ''", self.html)


class FocusDeepLinkTests(unittest.TestCase):
    """One window at a time: `#win=<token>`, optionally `&view=compare`.

    And the older deep link is untouched, because the fidelity instrument
    photographs it: `bootBare()` is still the first statement of `boot()`, so
    nothing here is reachable from a bare page.
    """

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.model = gmi.build_model([make_shots(self.root)],
                                     make_scenarios(self.root), with_photos=False)
        self.html = gmi.render_html(self.model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_the_hash_is_read_with_the_same_parser_bare_mode_uses(self):
        self.assertIn("var q = parseHash(window.location.hash);", self.html)
        self.assertIn("if (q.win){", self.html)
        self.assertIn("if (q.view === 'compare') S.view = 'compare';", self.html)

    def test_the_focus_scopes_the_rail_and_offers_a_way_out(self):
        self.assertIn("shown = shown.filter(function(w){ return w.token === S.focus; });",
                      self.html)
        self.assertIn("'show every window'", self.html)

    def test_the_link_is_printed_so_it_can_be_pasted(self):
        self.assertIn("function focusLink(){", self.html)
        self.assertIn("'#win=' + encodeURIComponent(S.window || '')", self.html)
        self.assertIn("&view=compare", self.html)

    def test_a_token_no_capture_is_of_is_said_rather_than_ignored(self):
        self.assertIn("which no capture in this page is of.", self.html)
        self.assertIn("if (missed) status(missed, true);", self.html)

    def test_bare_mode_still_short_circuits_boot_before_any_of_it(self):
        body = gmi.JS.split("function boot(){", 1)[1]
        first = [ln.strip() for ln in body.splitlines() if ln.strip()][0]
        self.assertEqual(first, "if (bootBare()) return;")
        # the focus link is read AFTER that, so a bare page never reaches it
        self.assertLess(gmi.JS.index("if (bootBare()) return;"),
                        gmi.JS.index("if (q.win){"))

    def test_the_bare_deep_link_grammar_is_unchanged(self):
        bare = gmi.JS.split("function bootBare()", 1)[1].split("\nfunction ", 1)[0]
        self.assertIn("if (q.bare !== '1') return false;", bare)
        self.assertIn("var cap = byId[q.cap];", bare)
        self.assertIn("var sc = parseInt(q.scroll, 10);", bare)

    def test_everything_new_on_the_page_is_hidden_in_bare_mode(self):
        for sel in ("#stagehead", "#statenote", "#notesPanel", "#status", "#top"):
            self.assertIn("body.bare " + sel, gmi.BARE_CSS)

    def test_the_address_bar_is_kept_in_step_with_the_view(self):
        # The printed link field is gone; the address bar IS the link, written
        # on every selection and guarded, since a viewer sandbox may refuse it.
        body = self.html[self.html.index("function syncHash(){"):]
        body = body[:body.index("\n}")]
        self.assertIn("window.history.replaceState(null, '', focusLink());", body)
        self.assertIn("try {", body)
        self.assertIn("catch (e)", body)
        sel = self.html[self.html.index("function select(cap, exact){"):]
        sel = sel[:sel.index("\n}")]
        self.assertIn("syncHash();", sel)
        self.assertNotIn("focusbar", self.html)
        self.assertNotIn("paintFocus", self.html)

    def test_the_link_names_the_exact_capture_and_scopes_only_on_request(self):
        self.assertIn("'&cap=' + encodeURIComponent(S.capture)", self.html)
        self.assertIn("if (q.focus === '1') S.focus = q.win;", self.html)
        self.assertIn("if (byId[q.cap] && byId[q.cap].window === q.win){", self.html)


class SimplifiedChromeTests(unittest.TestCase):
    """The explore -> choose -> note -> export flow: statistics in one place, a
    top bar with only what that flow needs, readable rail rows, and a notes
    panel that lists, deletes, clears and copies.

    The page is built at runtime, so what is pinned is that the page SHIPS the
    handlers, in the same style as the rest of this file."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.model = gmi.build_model([make_shots(self.root)],
                                     make_scenarios(self.root), with_photos=False)
        self.html = gmi.render_html(self.model)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def _fn(self, head):
        body = self.html[self.html.index(head):]
        return body[:body.index("\n}")]

    def _top(self):
        top = self.html[self.html.index('<div id="top">'):]
        return top[:top.index('<div id="wrap">')]

    def test_the_statistics_live_in_the_rail_header_only(self):
        top = self._top()
        for word in ("captures", "fixtures", "distinct"):
            self.assertNotIn(word, top, "the top bar repeats the statistics")
        self.assertIn("' distinct states)'", self.html)

    def test_the_top_bar_keeps_the_flow_and_folds_the_preferences(self):
        top = self._top()
        for bid in ("btnMirror", "btnCompare", "btnPhoto", "btnNotes"):
            self.assertIn('id="%s"' % bid, top)
        opts = top[top.index('<details id="opts">'):]
        for bid in ('id="fixture"', 'id="btnMode"', 'id="btnForeign"'):
            self.assertIn(bid, opts, "%s is not behind the options fold" % bid)
        self.assertNotIn("cmpbtn", self.html)

    def test_the_notes_button_counts_the_saved_notes(self):
        self.assertIn("b.textContent = 'Notes (' + Object.keys(NOTES).length + ')';",
                      self.html)
        self.assertIn("paintNotesCount();", self._fn("function saveNotes(){"))

    def test_one_note_can_be_deleted(self):
        body = self._fn("function deleteNote(key){")
        self.assertIn("delete NOTES[key];", body)
        self.assertIn("saveNotes();", body)
        self.assertIn("afterNotesChange();", body)
        panel = self._fn("function paintNotesPanel(){")
        self.assertIn("x.onclick = function(ev){ ev.stopPropagation(); deleteNote(r.key); };",
                      panel)

    def test_clear_all_asks_first_and_a_refused_prompt_clears_nothing(self):
        body = self._fn("function clearAllNotes(){")
        self.assertIn("window.confirm(", body)
        self.assertIn("catch (e) { yes = false; }", body)
        self.assertLess(body.index("if (!yes) return false;"),
                        body.index("delete NOTES[k];"))
        self.assertIn("saveNotes();", body)
        self.assertIn("clr.onclick = function(){ clearAllNotes(); };",
                      self._fn("function paintNotesPanel(){"))

    def test_a_listed_note_jumps_to_its_own_capture(self):
        body = self._fn("function jumpToNote(r){")
        self.assertIn("var cap = byId[r.afterId] || byId[r.beforeId];", body)
        self.assertIn("select(cap, true);", body)
        self.assertIn("which this page has no capture of.", body)
        self.assertIn("li.onclick = function(){ jumpToNote(r); };",
                      self._fn("function paintNotesPanel(){"))

    def test_import_sits_behind_a_fold(self):
        panel = self._fn("function paintNotesPanel(){")
        self.assertIn("is.textContent = 'import...';", panel)
        self.assertIn("var ta = document.createElement('textarea');", panel)

    def test_the_notes_box_is_a_textarea_that_saves_as_it_is_typed(self):
        body = self.html[self.html.index("function notesBox(ctx){"):]
        body = body[:body.index("function refreshStateNote(")]
        self.assertIn("document.createElement('textarea')", body)
        self.assertIn("timer = setTimeout(commit, 500);", body)
        self.assertIn("inp.onblur = function(){ if (dirty) commit(); };", body)
        # a save repaints the rail's dots in place and never rebuilds the rail,
        # which would detach a row the reader is in the middle of clicking
        self.assertIn("paintNoteDots();", body)
        self.assertNotIn("buildRail()", body)

    def test_rail_rows_read_as_words_not_placeholders(self):
        self.assertIn("function stateLabel(win, tab, state, mode, hideTab){", self.html)
        self.assertIn("return named || state.replace(/-/g, ' ');", self.html)
        self.assertIn("function modeWord(m){", self.html)
        rail = self._fn("function buildRail(){")
        self.assertIn("stateLabel(w.token, c.tab, c.state, c.mode, headed)", rail)
        self.assertNotIn("c.tab || '-'", rail)
        # the dataset moved into the row's tooltip
        self.assertIn("sr.title = 'dataset ' + c.fixture", rail)

    def test_the_rail_lists_current_states_only(self):
        # Owner ruling 2026-09-24: no stale rows, no "show N hidden" link, no
        # never-captured rows - the rail shows the window as it is today.
        rail = self._fn("function buildRail(){")
        self.assertIn("if (isStale(c)) return;", rail)
        self.assertNotIn("' hidden'", rail)
        self.assertNotIn("S.showHidden", rail)
        self.assertNotIn("(no capture)", rail)

    def test_a_noted_state_is_marked_in_the_rail(self):
        rail = self._fn("function buildRail(){")
        self.assertIn("sr.dataset.nk = railNoteKey(w.token, c.tab, c.state, c.mode);",
                      rail)
        self.assertIn("if (noted[sr.dataset.nk]) sr.classList.add('noted');", rail)
        self.assertIn("#rail .noted .dot{background:var(--ok)}", self.html)

    def test_the_status_line_is_empty_unless_something_fell_back(self):
        sel = self._fn("function select(cap, exact){")
        self.assertIn("} else { status(''); }", sel)
        self.assertIn("if (fell.length && !exact){", sel)
        self.assertIn("#status{font-size:11px;color:var(--dim);height:16px", self.html)

    def test_the_hover_strip_keeps_its_fixed_height_and_the_notes_follow_it(self):
        self.assertIn("height:48px;box-sizing:content-box;overflow-y:auto;", self.html)
        after = self.html[self.html.index('<div class="echo" id="echo"></div>'):]
        self.assertTrue(after.split("\n")[1].startswith('<div id="statenote">'))



# --------------------------------------------------------------------------
# stock screens: photographs of stock KSP screens, with Parsek's decorations
# --------------------------------------------------------------------------

def _stock_line(level, body):
    return "[LOG 00:00:00] [Parsek][%s][StockUiOverlay] %s" % (level, body)


LOG_STOCK = "\n".join([
    "[LOG 00:00:01] [Parsek][INFO][TestCommands] uiaction describe "
    "scene=SPACECENTER complexity=advanced windows=0 open=0 openWindows=-",
    # an older Mission Control pass: superseded by the next one
    _stock_line("INFO", "decorate screen=MissionControl tab=Available items=3 "
                        "marked=1 blocked=1"),
    _stock_line("VERBOSE", "decorate screen=MissionControl tab=Available "
                           "item=c-old kind=ContractAccept marked=true "
                           "blocked=true why=\"stale pass\""),
    # the nearest pass before the capture
    _stock_line("INFO", "decorate screen=MissionControl tab=Available items=3 "
                        "marked=2 blocked=2"),
    _stock_line("VERBOSE", "decorate screen=MissionControl tab=Available "
                           "item=c-101 kind=ContractAccept marked=true "
                           "blocked=true why=\"Accepted on Y1 D9 on your "
                           "committed timeline.\""),
    _stock_line("VERBOSE", "decorate screen=MissionControl tab=Available "
                           "item=c-102 kind=ContractAccept marked=true "
                           "blocked=false why=\"a mark with no block\""),
    _stock_line("VERBOSE", "decorate screen=MissionControl kind=ContractSlot "
                           "blocked=4 marked=0 why=\"every slot is needed\""),
    "[LOG 00:00:04] [Parsek][INFO][TestCommands] capturescreenshot ok "
    "label=stk-mc-available-detail bytes=10 superSize=1 overwrote=false elapsed=0.1s",
    # after the capture: not part of what it photographed
    _stock_line("VERBOSE", "decorate screen=MissionControl tab=Available "
                           "item=c-late kind=ContractAccept marked=false "
                           "blocked=true why=\"too late\""),
    # the lane's own per-capture records win over any pass
    _stock_line("INFO", "decorate screen=AstronautComplex tab=Applicants items=2 "
                        "marked=0 blocked=0"),
    _stock_line("INFO", "record label=stk-ac-ksc screen=AstronautComplex "
                        "tab=Applicants kind=KerbalHire id=Valentina Kerman "
                        "marked=true blocked=true why=\"Hired later\""),
    _stock_line("INFO", "record label=stk-ac-ksc screen=AstronautComplex "
                        "tab=Available kind=None id=Bob Kerman marked=false "
                        "blocked=true why=\"\""),
    _stock_line("INFO", "record label=stk-ac-ksc screen=AstronautComplex "
                        "tab=Available kind=KerbalLost id=Bill Kerman "
                        "marked=true blocked=false why=\"Lost on a flight\""),
    "[LOG 00:00:05] [Parsek][INFO][TestCommands] capturescreenshot ok label=stk-ac-ksc",
    # a hover the IMGUI tooltip cannot see: must not read as "hover not captured"
    "[LOG 00:00:06] [Parsek][INFO][TestCommands] uiaction pointer at=400,300 "
    "park=false tooltip=- tooltipFrame=1",
    "[LOG 00:00:07] [Parsek][INFO][TestCommands] capturescreenshot ok "
    "label=stk-rnd-node-hover",
    # a screen no decoration line names
    "[LOG 00:00:08] [Parsek][INFO][TestCommands] capturescreenshot ok "
    "label=stk-admin-strategy",
])


def make_stock_shots(root, name="2026-09-12_0600_SYN-2-census-stock_shots",
                     log=LOG_STOCK, labels=("stk-mc-available-detail",
                                            "stk-ac-ksc", "stk-rnd-node-hover",
                                            "stk-admin-strategy"),
                     rgb=(30, 60, 90), mtime=1757656800, size=(64, 48)):
    """A stock census run: PNGs only (no control tree can see a uGUI screen),
    built in memory at a small size, plus the run's KSP.log."""
    path = os.path.join(root, name)
    os.makedirs(path, exist_ok=True)
    with open(os.path.join(path, "KSP.log"), "a", encoding="utf-8") as fh:
        fh.write(log + "\n")
    for label in labels:
        png = os.path.join(path, label + ".png")
        tiny_png(png, w=size[0], h=size[1], rgb=rgb)
        os.utime(png, (mtime, mtime))
    return path


class StockLabelTests(unittest.TestCase):
    def test_the_stk_host_marks_a_stock_capture(self):
        self.assertTrue(gmi.is_stock_label("stk-rnd-node-hover"))
        self.assertTrue(gmi.is_stock_label("stk-crewdialog"))
        self.assertFalse(gmi.is_stock_label("stk"))
        self.assertFalse(gmi.is_stock_label("ksc-career-contracts-advanced"))

    def test_the_second_token_is_the_screen_and_the_rest_the_state(self):
        self.assertEqual(gmi.stock_facets("stk-mc-available-detail"),
                         ("stk-mc", "mc", "available-detail"))
        self.assertEqual(gmi.stock_facets("stk-launchpicker"),
                         ("stk-launchpicker", "launchpicker", ""))

    def test_a_dump_with_no_parsek_root_is_a_stock_capture(self):
        foreign = {("kRPC", (10, 10, 200, 100))}
        cap = {"label": "odd-frame", "log": {},
               "dump": {"roots": [{"text": "kRPC", "rect": [10, 10, 200, 100]}]}}
        self.assertFalse(gmi.is_stock_capture(cap))           # label alone
        self.assertTrue(gmi.is_stock_capture(cap, foreign))
        cap["dump"]["roots"].append({"text": "Parsek", "rect": [0, 0, 300, 300]})
        self.assertFalse(gmi.is_stock_capture(cap, foreign))
        # the seam naming an open window, or a standing dialog, is Parsek on screen
        empty = {"label": "odd-frame", "dump": {"roots": []},
                 "log": {"openWindows": ["main"]}}
        self.assertFalse(gmi.is_stock_capture(empty, foreign))
        empty["log"] = {"dialog": {"title": "x", "buttons": []}}
        self.assertFalse(gmi.is_stock_capture(empty, foreign))


class StockDecorationParseTests(unittest.TestCase):
    def setUp(self):
        self.parsed = gmi.parse_stock_log(LOG_STOCK)

    def test_an_item_id_keeps_its_spaces_and_the_why_its_words(self):
        f = gmi.parse_stock_line("record label=stk-ac-ksc screen=AstronautComplex "
                                 "tab=Available kind=KerbalHire id=Valentina Kerman "
                                 "marked=true blocked=true why=\"Hired on Y2 D3 = soon\"")
        self.assertEqual(f["id"], "Valentina Kerman")
        self.assertEqual(f["why"], "Hired on Y2 D3 = soon")
        self.assertEqual((f["marked"], f["blocked"]), ("true", "true"))

    def test_the_nearest_pass_before_the_capture_is_the_fallback(self):
        dec = gmi.stock_decoration(self.parsed, "stk-mc-available-detail", "mc")
        self.assertEqual(dec["source"], "pass")
        self.assertEqual(dec["screen"], "MissionControl")
        ids = [r["id"] for r in dec["rows"]]
        self.assertNotIn("c-old", ids, "an earlier pass leaked into the capture")
        self.assertNotIn("c-late", ids, "a line logged after the capture leaked in")
        self.assertEqual(sorted(i for i in ids if i), ["c-101", "c-102"])
        self.assertEqual(dec["summaries"], [{"tab": "Available", "items": 3,
                                             "marked": 2, "blocked": 2}])

    def test_the_slot_aggregate_is_one_row_that_counts_its_items(self):
        dec = gmi.stock_decoration(self.parsed, "stk-mc-available-detail", "mc")
        slot = [r for r in dec["rows"] if r["kind"] == "ContractSlot"][0]
        self.assertEqual(slot["count"], 4)
        self.assertEqual((slot["marked"], slot["blocked"]), (False, True))
        self.assertEqual(slot["pairing"], "block only")
        self.assertFalse(slot["problem"])

    def test_the_captures_own_record_lines_win_over_a_pass(self):
        dec = gmi.stock_decoration(self.parsed, "stk-ac-ksc", "ac")
        self.assertEqual(dec["source"], "record")
        self.assertEqual(dec["screen"], "AstronautComplex")
        self.assertEqual({r["id"] for r in dec["rows"]},
                         {"Valentina Kerman", "Bob Kerman", "Bill Kerman"})
        self.assertEqual(dec["summaries"], [])

    def test_no_line_for_the_screen_says_so_instead_of_guessing(self):
        dec = gmi.stock_decoration(self.parsed, "stk-admin-strategy", "admin")
        self.assertIsNone(dec["source"])
        self.assertEqual(dec["rows"], [])
        self.assertEqual(dec["matched"], [])
        self.assertIn("MissionControl", dec["logScreens"])
        # a screen with no pass before the capture (R&D logged none here)
        dec2 = gmi.stock_decoration(self.parsed, "stk-rnd-node-hover", "rnd")
        self.assertIsNone(dec2["source"])
        # no log at all
        dec3 = gmi.stock_decoration(None, "stk-mc-x", "mc")
        self.assertIsNone(dec3["source"])
        self.assertEqual(dec3["logScreens"], [])

    def test_a_pass_after_the_capture_is_never_used(self):
        log = "\n".join([
            "[LOG] [Parsek][INFO][TestCommands] capturescreenshot ok label=stk-rnd-tree",
            _stock_line("INFO", "decorate screen=RnD tab=RnD items=90 marked=1 blocked=1"),
        ])
        dec = gmi.stock_decoration(gmi.parse_stock_log(log), "stk-rnd-tree", "rnd")
        self.assertIsNone(dec["source"])
        self.assertEqual(dec["matched"], ["RnD"])

    def test_one_pass_prints_one_summary_per_tab_on_adjacent_lines(self):
        log = "\n".join([
            _stock_line("INFO", "decorate screen=AstronautComplex tab=Applicants "
                                "items=4 marked=0 blocked=0"),
            _stock_line("INFO", "decorate screen=AstronautComplex tab=Available "
                                "items=5 marked=1 blocked=1"),
            _stock_line("INFO", "decorate screen=AstronautComplex tab=Assigned "
                                "items=2 marked=0 blocked=0"),
            _stock_line("VERBOSE", "decorate screen=AstronautComplex tab=Available "
                                   "item=Jebediah Kerman kind=KerbalOnFlight "
                                   "marked=true blocked=true why=\"on a flight\""),
            # the next refresh of the same screen is a new pass
            _stock_line("INFO", "decorate screen=AstronautComplex tab=Applicants "
                                "items=4 marked=0 blocked=0"),
            "[LOG] [Parsek][INFO][TestCommands] capturescreenshot ok label=stk-ac-x",
        ])
        parsed = gmi.parse_stock_log(log)
        self.assertEqual(len(parsed["passes"]), 2)
        self.assertEqual([s["tab"] for s in parsed["passes"][0]["summaries"]],
                         ["Applicants", "Available", "Assigned"])
        self.assertEqual(parsed["passes"][0]["rows"][0]["id"], "Jebediah Kerman")
        dec = gmi.stock_decoration(parsed, "stk-ac-x", "ac")
        self.assertEqual(dec["rows"], [], "the capture's pass is the newer one")

    def test_the_facility_menu_lines_make_one_item_with_its_why(self):
        log = "\n".join([
            _stock_line("INFO", "decorate screen=FacilityMenu "
                                "facility=SpaceCenter/LaunchPad marked=true blocked=true"),
            _stock_line("VERBOSE", "decorate screen=FacilityMenu "
                                   "facility=SpaceCenter/LaunchPad (menu open) "
                                   "why=\"Upgraded on Y1 D40\""),
            _stock_line("INFO", "decorate screen=FacilityMenu "
                                "facility=SpaceCenter/VehicleAssemblyBuilding "
                                "marked=false blocked=false"),
            _stock_line("VERBOSE", "decorate screen=FacilityMenu "
                                   "facility=SpaceCenter/VehicleAssemblyBuilding "
                                   "(menu open) unmarked: no committed future "
                                   "upgrade of this facility"),
            "[LOG] [Parsek][INFO][TestCommands] capturescreenshot ok "
            "label=stk-facility-menu",
        ])
        parsed = gmi.parse_stock_log(log)
        self.assertEqual(parsed["passes"][0]["rows"][0]["why"], "Upgraded on Y1 D40")
        dec = gmi.stock_decoration(parsed, "stk-facility-menu", "facility")
        self.assertEqual(dec["source"], "pass")
        self.assertEqual([r["id"] for r in dec["rows"]],
                         ["SpaceCenter/VehicleAssemblyBuilding"])
        self.assertTrue(dec["rows"][0]["why"].startswith("unmarked: no committed"))
        self.assertEqual(dec["rows"][0]["pairing"], "stock")

    def test_the_facility_menu_repair_line_is_the_menus_second_row(self):
        # StockUiDecorationQuery.LogFacilityMenuRepair: a marked Repair logs the pass's
        # item line after the Upgrade lines; the unmarked sentence is no decorate line.
        log = "\n".join([
            _stock_line("INFO", "decorate screen=FacilityMenu "
                                "facility=SpaceCenter/LaunchPad marked=false blocked=false"),
            _stock_line("VERBOSE", "decorate screen=FacilityMenu "
                                   "facility=SpaceCenter/LaunchPad (values modified) "
                                   "unmarked: no committed future upgrade of this facility"),
            _stock_line("VERBOSE", "decorate screen=FacilityMenu tab=Repair "
                                   "item=SpaceCenter/LaunchPad kind=FacilityRepair "
                                   "marked=true blocked=true "
                                   "why=\"Repaired on Y1 D40 on your committed timeline.\""),
            _stock_line("VERBOSE", "FacilityMenu SpaceCenter/VehicleAssemblyBuilding Repair "
                                   "left to stock (values modified): no destroyed building"),
            "[LOG] [Parsek][INFO][TestCommands] capturescreenshot ok "
            "label=stk-facility-menu",
        ])
        parsed = gmi.parse_stock_log(log)
        dec = gmi.stock_decoration(parsed, "stk-facility-menu", "facility")
        self.assertEqual(dec["source"], "pass")
        rows = sorted(dec["rows"], key=lambda r: r["line"])
        self.assertEqual([(r["id"], r["kind"], r["tab"]) for r in rows],
                         [("SpaceCenter/LaunchPad", "", ""),
                          ("SpaceCenter/LaunchPad", "FacilityRepair", "Repair")])
        self.assertEqual(rows[1]["pairing"], "paired")
        self.assertTrue(rows[1]["why"].startswith("Repaired on Y1 D40"))
        self.assertEqual(dec["problems"], 0)

    def test_the_label_token_is_matched_to_the_log_screen_without_a_table(self):
        pairs = {"rnd": "RnD", "mc": "MissionControl", "ac": "AstronautComplex",
                 "crewdialog": "CrewAssignment", "facility": "FacilityMenu"}
        screens = list(pairs.values())
        for tok, want in pairs.items():
            got = [s for s in screens if gmi.stock_screen_matches(tok, s)]
            self.assertEqual(got, [want], tok)
        for tok in ("admin", "part", "launchpicker"):
            self.assertEqual([s for s in screens if gmi.stock_screen_matches(tok, s)],
                             [], tok)


# The GUI-28 lane's own line shapes, lifted from its first run
# (2026-09-25_2055_GUI-28-census-stock-screens, KSP.log), whys shortened.
LOG_GUI28 = chr(10).join([
    "[LOG 23:55:44.100] [Parsek][INFO][TestCommands] capturescreenshot ok label=stk-ksc",
    "[LOG 23:55:44.194] [Parsek][INFO][StockUiOverlay] record label=stk-ksc screens=none",
    "[LOG 23:55:46.600] [Parsek][INFO][TestCommands] capturescreenshot ok "
    "label=stk-part-tooltip-rnd",
    "[LOG 23:55:46.650] [Parsek][INFO][StockUiOverlay] record label=stk-part-tooltip-rnd "
    "screen=RnD tab=Tree items=145 marked=1 blocked=1",
    "[LOG 23:55:46.650] [Parsek][INFO][StockUiOverlay] record label=stk-part-tooltip-rnd "
    "screen=PartTooltip tab=RnD items=1 marked=1 blocked=1",
    "[LOG 23:55:46.650] [Parsek][INFO][StockUiOverlay] record label=stk-part-tooltip-rnd "
    "screen=RnD tab=Tree item=basicRocketry kind=TechResearch marked=true blocked=true "
    "why=\"Researched on Y1, D02, 02:20 on your committed timeline.\"",
    "[LOG 23:55:46.650] [Parsek][INFO][StockUiOverlay] record label=stk-part-tooltip-rnd "
    "screen=PartTooltip tab=RnD item=probeCoreSphere.v2 kind=PartPurchase marked=true "
    "blocked=true why=\"Purchased on Y1, D06, 00:33 on your committed timeline.\"",
    "[LOG 23:55:46.656] [Parsek][INFO][StockUiOverlay] control label=stk-part-tooltip-rnd "
    "screen=RnD name=actionButton:start state=purchase interactable=false visible=true",
    "[LOG 23:55:46.656] [Parsek][INFO][StockUiOverlay] control label=stk-part-tooltip-rnd "
    "screen=PartTooltip name=buttonPurchaseRed state=- interactable=false visible=false",
    "[LOG 23:55:47.400] [Parsek][INFO][TestCommands] capturescreenshot ok label=stk-ac-ksc",
    "[LOG 23:55:47.439] [Parsek][INFO][StockUiOverlay] record label=stk-ac-ksc "
    "screen=AstronautComplex tab=Available items=6 marked=2 blocked=4",
    "[LOG 23:55:47.439] [Parsek][INFO][StockUiOverlay] record label=stk-ac-ksc "
    "screen=AstronautComplex tab=Available item=Debwig Kerman kind=None marked=false "
    "blocked=true why=\"\"",
    "[LOG 23:55:47.442] [Parsek][INFO][StockUiOverlay] control label=stk-ac-ksc "
    "screen=AstronautComplex name=row:Jebediah Kerman state=X/mouseover=off "
    "interactable=false visible=false",
])


class StockLaneLineShapeTests(unittest.TestCase):
    """The per-capture `record` / `control` shapes the GUI-28 lane logs."""

    def setUp(self):
        self.parsed = gmi.parse_stock_log(LOG_GUI28)

    def test_record_summaries_and_items_across_two_screens(self):
        dec = gmi.stock_decoration(self.parsed, "stk-part-tooltip-rnd", "part")
        self.assertEqual(dec["source"], "record")
        self.assertEqual(dec["screen"], "RnD, PartTooltip")
        self.assertEqual([(x["screen"], x["tab"], x["items"]) for x in dec["summaries"]],
                         [("RnD", "Tree", 145), ("PartTooltip", "RnD", 1)])
        self.assertEqual([(r["screen"], r["id"], r["pairing"]) for r in dec["rows"]],
                         [("RnD", "basicRocketry", "paired"),
                          ("PartTooltip", "probeCoreSphere.v2", "paired")])
        self.assertEqual(dec["problems"], 0)

    def test_control_lines_ride_with_the_capture(self):
        dec = gmi.stock_decoration(self.parsed, "stk-part-tooltip-rnd", "part")
        self.assertEqual([(c["screen"], c["name"], c["state"], c["interactable"],
                           c["visible"]) for c in dec["controls"]],
                         [("RnD", "actionButton:start", "purchase", False, True),
                          ("PartTooltip", "buttonPurchaseRed", "-", False, False)])
        ac = gmi.stock_decoration(self.parsed, "stk-ac-ksc", "ac")
        self.assertEqual(ac["controls"][0]["name"], "row:Jebediah Kerman")
        self.assertEqual(ac["controls"][0]["state"], "X/mouseover=off")

    def test_screens_none_is_a_record_with_nothing_decorated(self):
        dec = gmi.stock_decoration(self.parsed, "stk-ksc", "ksc")
        self.assertEqual(dec["source"], "record")
        self.assertTrue(dec["recordNone"])
        self.assertEqual((dec["rows"], dec["summaries"]), ([], []))

    def test_a_block_with_no_mark_on_a_real_line_is_a_problem(self):
        dec = gmi.stock_decoration(self.parsed, "stk-ac-ksc", "ac")
        self.assertEqual(dec["rows"][0]["id"], "Debwig Kerman")
        self.assertEqual(dec["rows"][0]["pairing"], "blocked, not marked")
        self.assertEqual(dec["problems"], 1)

    def test_a_changed_control_changes_the_pair(self):
        a = gmi.stock_decoration(self.parsed, "stk-part-tooltip-rnd", "part")
        b = json.loads(json.dumps(a))
        b["controls"][0]["interactable"] = True
        self.assertNotEqual(gmi._stock_decor_sig(a), gmi._stock_decor_sig(b))


class StockPairingTests(unittest.TestCase):
    def test_the_four_cases(self):
        self.assertEqual(gmi.stock_pairing("TechResearch", True, True), ("paired", False))
        self.assertEqual(gmi.stock_pairing("None", False, False), ("stock", False))
        self.assertEqual(gmi.stock_pairing("ContractAccept", True, False),
                         ("marked, not blocked", True))
        self.assertEqual(gmi.stock_pairing("None", False, True),
                         ("blocked, not marked", True))

    def test_the_exempt_kinds_are_not_problems(self):
        for kind in gmi.STOCK_INFORMATIONAL_KINDS:
            self.assertEqual(gmi.stock_pairing(kind, True, False),
                             ("informational", False))
        for kind in gmi.STOCK_BLOCK_ONLY_KINDS:
            self.assertEqual(gmi.stock_pairing(kind, False, True), ("block only", False))
        # an exemption covers its own direction only
        self.assertTrue(gmi.stock_pairing(gmi.STOCK_BLOCK_ONLY_KINDS[0], True, False)[1])
        self.assertTrue(gmi.stock_pairing(gmi.STOCK_INFORMATIONAL_KINDS[0], False, True)[1])

    def test_problems_are_counted_and_lead_the_table(self):
        parsed = gmi.parse_stock_log(LOG_STOCK)
        dec = gmi.stock_decoration(parsed, "stk-mc-available-detail", "mc")
        self.assertEqual(dec["problems"], 1)
        self.assertEqual(dec["rows"][0]["id"], "c-102")
        self.assertEqual(dec["rows"][0]["pairing"], "marked, not blocked")
        ac = gmi.stock_decoration(parsed, "stk-ac-ksc", "ac")
        by_id = {r["id"]: r for r in ac["rows"]}
        self.assertEqual(by_id["Bob Kerman"]["pairing"], "blocked, not marked")
        self.assertEqual(by_id["Bill Kerman"]["pairing"], "informational")
        self.assertEqual(ac["problems"], 1)

    def test_the_exempt_kinds_are_members_of_the_csharp_enum(self):
        # Source-sync: a renamed or removed StockUiDecorationKind member would
        # otherwise turn its exemption silently into a reported problem.
        src_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "..", "..", "Source", "Parsek",
                                "StockUiDecorationQuery.cs")
        with open(src_path, encoding="utf-8") as fh:
            src = fh.read()
        src = re.sub(r"//[^\n]*", "", src.replace("\r\n", "\n"))
        m = re.search(r"enum\s+StockUiDecorationKind\s*\{([^}]*)\}", src)
        self.assertIsNotNone(m, "StockUiDecorationKind not found")
        members = {t.strip() for t in m.group(1).split(",") if t.strip()}
        for kind in gmi.STOCK_INFORMATIONAL_KINDS + gmi.STOCK_BLOCK_ONLY_KINDS:
            self.assertIn(kind, members)


class StockScreenEndToEndTests(unittest.TestCase):
    """A census corpus with a Parsek lane and a stock-screen lane."""

    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.shots = make_shots(self.root)
        self.scen = make_scenarios(self.root)
        make_scenarios(self.root, spec_id="SYN-2-census-stock",
                       template="fixtures/saves/syn-career")
        self.stock = make_stock_shots(self.root)
        self.model = gmi.build_model([self.shots, self.stock], self.scen)
        self.html = gmi.render_html(self.model)
        self.stk = {c["label"]: c for c in self.model["captures"] if c.get("stock")}

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_every_stk_png_is_a_capture_grouped_by_screen(self):
        self.assertEqual(sorted(self.stk), ["stk-ac-ksc", "stk-admin-strategy",
                                            "stk-mc-available-detail",
                                            "stk-rnd-node-hover"])
        self.assertEqual(sorted(self.model["stockWindows"]),
                         ["stk-ac", "stk-admin", "stk-mc", "stk-rnd"])
        cap = self.stk["stk-mc-available-detail"]
        self.assertEqual((cap["window"], cap["state"], cap["tab"], cap["mode"]),
                         ("stk-mc", "available-detail", None, None))
        self.assertEqual(cap["roots"], [])
        names = {w["token"]: w["name"] for w in self.model["windows"] if w.get("stock")}
        self.assertEqual(names["stk-rnd"], "rnd")
        self.assertIn("stk-rnd", self.model["railRows"])

    def test_the_stage_is_the_whole_png_never_a_redraw(self):
        cap = self.stk["stk-rnd-node-hover"]
        self.assertTrue(cap["photo"]["src"].startswith("data:image/png;base64,"))
        self.assertEqual((cap["photo"]["pw"], cap["photo"]["ph"]), (64, 48))
        self.assertEqual(cap["screen"], [64, 48])
        self.assertIn("if (cap.stock) return renderStockPhoto(cap, host, opts);",
                      self.html)

    def test_a_stock_photo_is_never_subsampled_by_the_budget(self):
        model = gmi.build_model([self.shots, self.stock], self.scen, budget=1)
        stk = [c for c in model["captures"] if c.get("stock")][0]
        own = [c for c in model["captures"] if not c.get("stock")][0]
        self.assertEqual(stk["photo"]["pw"], 64)
        self.assertLess(own["photo"]["pw"], own["photo"]["w"],
                        "the budget ladder did not reach the subsampling step")

    def test_a_uGUI_hover_is_not_read_as_an_empty_imgui_tooltip(self):
        cap = self.stk["stk-rnd-node-hover"]
        self.assertEqual(cap["pointer"]["tooltip"], "-")
        self.assertNotIn("hoverEmpty", cap)
        # and no two tree-less stock frames are each other's "twin"
        self.assertFalse(any(c.get("hoverEmpty") for c in self.stk.values()))

    def test_the_decoration_travels_with_the_capture(self):
        dec = self.stk["stk-mc-available-detail"]["stock"]["decor"]
        self.assertEqual(dec["source"], "pass")
        self.assertEqual(dec["problems"], 1)
        self.assertIsNone(self.stk["stk-admin-strategy"]["stock"]["decor"]["source"])
        self.assertIn("'no decoration lines logged for this screen'", self.html)

    def test_the_parsek_captures_are_untouched_by_a_stock_lane(self):
        alone = gmi.build_model([self.shots], self.scen)
        mine = [c for c in self.model["captures"] if not c.get("stock")]
        self.assertEqual(json.dumps(alone["captures"], sort_keys=True),
                         json.dumps(mine, sort_keys=True))
        self.assertEqual(alone["defaultFixture"], self.model["defaultFixture"])
        self.assertEqual([w for w in alone["windows"]],
                         [w for w in self.model["windows"] if not w.get("stock")])
        self.assertEqual(alone["railRows"],
                         {k: v for k, v in self.model["railRows"].items()
                          if k not in self.model["stockWindows"]})

    def test_a_later_run_supersedes_and_compare_pairs_the_two(self):
        newer = make_stock_shots(
            self.root, name="2026-09-13_0600_SYN-2-census-stock_shots",
            labels=("stk-mc-available-detail",), rgb=(90, 60, 30),
            mtime=1757743200)
        model = gmi.build_model([self.shots, self.stock, newer], self.scen)
        caps = [c for c in model["captures"]
                if c["label"] == "stk-mc-available-detail"]
        self.assertEqual(len(caps), 2)
        old = [c for c in caps if c["runId"].startswith("2026-09-12")][0]
        new = [c for c in caps if c["runId"].startswith("2026-09-13")][0]
        self.assertEqual(old["supersededBy"], new["id"])
        self.assertTrue(gmi.is_stale(old))
        info = model["keys"][new["key"]]
        self.assertEqual((info["before"], info["after"]), (old["id"], new["id"]))
        self.assertTrue(info["changed"], "two different frames read as unchanged")
        self.assertIsNone(info["measured"])
        self.assertFalse(info["stockMeasured"]["photoSame"])

    def test_the_rail_puts_stock_screens_under_their_own_heading(self):
        rail = self.html[self.html.index("function buildRail(){"):]
        rail = rail[:rail.index("\nfunction showView(")]
        self.assertIn("'Stock screens (' + stockCaps + ' captures)'", rail)
        self.assertIn("own.forEach(railWindow);", rail)
        self.assertIn("stk.forEach(railWindow);", rail)
        self.assertLess(rail.index("own.forEach(railWindow);"),
                        rail.index("stk.forEach(railWindow);"))
        self.assertIn("c.stock.decor.problems + ' pairing'", rail)
        # the Parsek heading counts Parsek's captures and states only
        self.assertIn("(M.captures.length - stockCaps) + ' captures'", rail)
        self.assertIn("return !(byId[M.keys[k].after] || {}).stock; }).length;", rail)

    def test_compare_and_the_panel_cover_a_stock_screen(self):
        self.assertIn("if (M.seamWindows.indexOf(win) < 0 && !isStockWin(win)){",
                      self.html)
        self.assertIn("if (r.info.stockMeasured) sec.appendChild(", self.html)
        self.assertIn('<div id="decorwrap" class="hidden"></div>', self.html)
        self.assertIn("['id', 'tab', 'kind', 'marked', 'blocked', 'pairing']", self.html)
        self.assertIn("var wc = el('td', 'w', r.why ? r.why : '(no why logged)');",
                      self.html)
        self.assertIn("['button', 'state', 'interactable', 'visible']", self.html)
        self.assertIn("body.bare #decorwrap{display:none}", self.html)
        self.assertEqual(self.model["stockWindows"],
                         gmi._page_model(self.model)["stockWindows"])

    def test_the_index_counts_the_stock_screens(self):
        idx = gmi.build_index(self.model)
        self.assertEqual(idx["windows"]["stk-mc"]["captures"], 1)
        self.assertEqual(idx["stockCaptureCount"], 4)


if __name__ == "__main__":
    unittest.main()
