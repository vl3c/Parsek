"""Tests for the flight profile catalog (`lanecatalog.py` + `tools/lane_catalog.py`).

The pure derivations are pinned on hand-built spec dicts. One sync cell
regenerates `docs/dev/flight-profiles.md` from the committed tree and asserts the
committed copy is byte-equal, so the generated doc cannot go stale.
"""

import importlib.util
import os
import unittest

import hlib
import lanecatalog as lc

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
TOOL_PATH = os.path.join(HARNESS_ROOT, "tools", "lane_catalog.py")


def _step(cmd, **args):
    step = {"cmd": cmd, "expect": "OK"}
    if args:
        step["args"] = {k: str(v) for k, v in args.items()}
    return step


def _spec(lane_id="X1-test", steps=None, **extra):
    spec = {
        "id": lane_id, "tier": "nightly", "instanceProfile": "stock-minimal",
        "tags": [], "fixture": {"saveTemplate": "fixtures/saves/gloops-airshow",
                                "injectedRecordings": "none", "craft": []},
        "driver": {"kind": "seam", "steps": steps if steps is not None else [
            _step("LoadGame", save="${runSave}", name="persistent"),
            _step("FlushAndQuit")]},
        "expectations": {"allowedAnomalies": {}, "logContracts": {}},
    }
    spec.update(extra)
    return spec


class FamilyTests(unittest.TestCase):
    def test_prefix_before_the_first_number(self):
        self.assertEqual("B", lc.family_of("B11-mun-orbit"))
        self.assertEqual("V", lc.family_of("V33M-jool-laythe-continuation-player-loop"))
        self.assertEqual("S", lc.family_of("S0.10-automerge-limbo-warm-exit"))
        self.assertEqual("LABX", lc.family_of("LABX-1-lab-orbit-ascent"))

    def test_an_id_whose_first_token_has_no_number_keeps_the_token(self):
        self.assertEqual("RF", lc.family_of("RF-L1-bank-then-retire-refused"))
        self.assertEqual("FORGE", lc.family_of("FORGE-b17-duna-pad"))


class VerbAndCategoryTests(unittest.TestCase):
    def test_verbs_are_first_use_ordered_and_skip_the_mission_row(self):
        spec = _spec(steps=[_step("LoadGame"), {"phase": "mission", "budget": 10, "expect": "OK"},
                            _step("SaveGame", name="a"), _step("LoadGame"), _step("FlushAndQuit")])
        self.assertEqual(("LoadGame", "SaveGame", "FlushAndQuit"), lc.seam_verbs(spec))

    def test_categories_split_comma_lists_and_mark_a_bare_runtests(self):
        spec = _spec(steps=[_step("LoadGame"), _step("RunTests", category="A, B"),
                            _step("RunTests", category="B,C"), _step("RunTests")])
        self.assertEqual(("A", "B", "C", "(all)"), lc.ingame_categories(spec))

    def test_no_runtests_means_no_categories(self):
        self.assertEqual((), lc.ingame_categories(_spec()))


class TimelineTests(unittest.TestCase):
    def test_the_boot_load_is_not_a_reload(self):
        self.assertEqual((), lc.timeline_ops(_spec()))

    def test_a_second_load_is_a_reload_and_a_scene_arg_changes_scene(self):
        spec = _spec(steps=[_step("LoadGame"), _step("LoadGame", name="q")])
        self.assertEqual(("reload",), lc.timeline_ops(spec))
        spec = _spec(steps=[_step("LoadGame"), _step("LoadGame", name="q", scene="spacecenter")])
        self.assertEqual(("reload", "scene-change"), lc.timeline_ops(spec))

    def test_verb_mapping_and_canonical_order(self):
        spec = _spec(steps=[_step("LoadGame"), _step("TimeJump", ut=5),
                            _step("WarpToUT", ut=9, ladder="phys"), _step("ReFlyRevert"),
                            _step("InvokeRewindToLaunch", tree="t"), _step("CycleVessel"),
                            _step("NewGame", mode="CAREER"), _step("GoToEditor")])
        self.assertEqual(("rewind-to-launch", "re-fly", "revert", "warp", "physics-warp",
                          "time-jump", "scene-change", "vessel-switch", "new-game"),
                         lc.timeline_ops(spec))

    def test_plain_warp_is_not_physics_warp(self):
        spec = _spec(steps=[_step("LoadGame"), _step("WarpToUT", ut=9)])
        self.assertEqual(("warp",), lc.timeline_ops(spec))

    def test_every_mapped_verb_is_an_implemented_seam_verb(self):
        mapped = set(lc.VERB_TIMELINE_OPS) | {"LoadGame", "WarpToUT"}
        self.assertEqual(set(), mapped - set(hlib.IMPLEMENTED_SEAM_VERBS))
        for ops in lc.VERB_TIMELINE_OPS.values():
            self.assertTrue(set(ops) <= set(lc.TIMELINE_OPS), ops)


class BodyTests(unittest.TestCase):
    def test_whole_letter_runs_only(self):
        self.assertEqual(["Mun"], lc.body_tokens("cs-career-mun4"))
        self.assertEqual([], lc.body_tokens("event-evening-bopper"))
        self.assertEqual(["Kerbin"], lc.body_tokens("eva2-lko-crewed"))

    def test_sources_and_stock_order(self):
        spec = _spec("V99-laythe-thing", tags=["duna"],
                     dimensionsCovered={"D14": ["mun", "scene-flight"]},
                     steps=[_step("LoadGame"), _step("MapCamera", focus="Sun")])
        spec["driver"]["mission"] = "b22_jool_orbit"
        self.assertEqual(("Mun", "Duna", "Jool", "Laythe", "Sun"), lc.bodies(spec))

    def test_a_lane_that_names_no_body_derives_none(self):
        spec = _spec("H99-thing")
        spec["fixture"]["saveTemplate"] = "fixtures/saves/fresh-career"
        self.assertEqual((), lc.bodies(spec))


class ExpectationAndDimensionTests(unittest.TestCase):
    def test_gating_paths_are_dotted_and_nested(self):
        exp = {"rewind": {"gating": True}, "recordings": {"count": 1, "structure": {"gating": True},
                                                          "points": {"gating": False}},
               "logContracts": {}}
        self.assertEqual(["recordings.structure", "rewind"], lc.gating_paths(exp))

    def test_dimension_tokens_sort_by_number(self):
        spec = _spec(dimensionsCovered={"D10": ["b", "a"], "D2": ["z"]})
        self.assertEqual(("D2:z", "D10:a", "D10:b"), lc.dimension_tokens(spec))

    def test_expected_fail_bug(self):
        self.assertEqual("", lc.expected_fail_bug(_spec(expectedFail={"bugId": ""})))
        self.assertEqual("BUG-1", lc.expected_fail_bug(_spec(expectedFail={"bugId": "BUG-1"})))
        self.assertEqual("", lc.expected_fail_bug(_spec()))


class StatusTests(unittest.TestCase):
    def rows(self, *lines):
        return lc.index_status_rows("\n".join(lines)).get("X1-test")

    def test_no_row(self):
        self.assertEqual((lc.NO_STATUS_ROW, ""), lc.derive_status(None))

    def test_latest_live_proven_wins_and_first_cell_markup_is_stripped(self):
        rows = self.rows("| `X1-test` | nightly (LIVE-PROVEN 2026-09-01) | "
                         "**LIVE-PROVEN AND CLAIMED 2026-10-02** then more |")
        self.assertEqual(("LIVE-PROVEN 2026-10-02", "2026-10-02"), lc.derive_status(rows))

    def test_a_negated_mention_is_not_a_claim(self):
        rows = self.rows("| X1-test | nightly | **NOT LIVE-PROVEN as landed 2026-10-02** |")
        status, live = lc.derive_status(rows)
        self.assertEqual("", live)
        self.assertNotIn("LIVE-PROVEN 2026", status.replace("NOT LIVE-PROVEN", ""))

    def test_dated_bold_status_phrase_then_plain_reproof_then_parenthetical(self):
        rows = self.rows("| X1-test | operator | **SPEC CHANGED** x **RE-FLOWN PASS 2026-09-24** |")
        self.assertEqual(("RE-FLOWN PASS 2026-09-24", ""), lc.derive_status(rows))
        rows = self.rows("| X1-test | daily | plain text. RE-PROVEN 2026-07-26 in a batch |")
        self.assertEqual(("RE-PROVEN 2026-07-26", ""), lc.derive_status(rows))
        rows = self.rows("| X1-test | operator (calibration discipline) | words only |")
        self.assertEqual(("calibration discipline", ""), lc.derive_status(rows))
        rows = self.rows("| X1-test | daily | one two three four five six seven |")
        self.assertEqual(("one two three four five six ...", ""), lc.derive_status(rows))


class RenderTests(unittest.TestCase):
    REGISTRY = {"schema": 1, "D1": {"values": ["used", "unused-value"]},
                "D14": {"values": ["mun"]}}

    def lanes(self):
        a = _spec("A1-mun", dimensionsCovered={"D1": ["used"], "D14": ["mun"]},
                  steps=[_step("LoadGame"), _step("InvokeRewindToLaunch", tree="t"),
                         _step("RunTests", category="Cat1"), _step("FlushAndQuit")])
        b = _spec("B2-x | y")
        status = "| A1-mun | nightly (LIVE-PROVEN 2026-01-02) | x |\n"
        return lc.build_catalog([b, a], status)

    def render(self):
        return lc.render(self.lanes(), self.REGISTRY, {"Cat1", "Cat2"},
                         ("LoadGame", "FlushAndQuit", "InvokeRewindToLaunch", "RunTests", "Never"))

    def test_deterministic_ascii_and_generated_banner(self):
        text = self.render()
        self.assertEqual(text, self.render())
        text.encode("ascii")
        self.assertIn("GENERATED FILE - do not hand-edit", text)
        self.assertIn(lc.REGENERATE_COMMAND, text)
        self.assertTrue(text.endswith("\n") and not text.endswith("\n\n"))

    def test_never_used_names_the_gaps(self):
        text = self.render()
        self.assertIn("Seam verbs (of 5 implemented): `Never`", text)
        self.assertIn("D1 `unused-value`", text)
        self.assertIn("`Cat2`", text)
        self.assertNotIn("`Cat1`,", text)

    def test_matrix_marks_a_zero_combination(self):
        text = self.render()
        matrix = text.split("## Body x timeline operation", 1)[1].split("\n## ", 1)[0]
        row = [l for l in matrix.splitlines() if l.startswith("| Mun |")]
        self.assertEqual(1, len(row), matrix)
        self.assertEqual("| Mun | 1 | 1 | - | - | - | - | - | - | - | - | - | - |", row[0])

    def test_a_pipe_in_a_cell_cannot_break_the_table(self):
        text = self.render()
        self.assertIn("| B2-x / y |", text)

    def test_list_cell_truncates_whole_items(self):
        self.assertEqual("alpha, beta +2", lc.list_cell(["alpha", "beta", "gamma", "delta"], 18))
        self.assertEqual("alpha", lc.list_cell(["alpha"], 2))
        self.assertEqual("-", lc.list_cell([], 10))

    def test_cell_cuts_and_stays_ascii(self):
        self.assertEqual("abc...", lc.cell("abcdefghij", 6))
        self.assertEqual("a - b -> c", lc.cell("a \u2014 b \u2192 c"))


class CommittedDocSyncTests(unittest.TestCase):
    def test_the_committed_catalog_is_current(self):
        spec = importlib.util.spec_from_file_location("lane_catalog_tool", TOOL_PATH)
        tool = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(tool)
        expected = tool.build_document(REPO_ROOT)
        path = os.path.join(REPO_ROOT, *lc.DOC_RELPATH.split("/"))
        with open(path, "rb") as fh:
            committed = fh.read()
        # A core.autocrlf checkout turns LF into CRLF on disk; the content is what is
        # pinned, not the platform's line endings.
        committed = committed.replace(b"\r\n", b"\n")
        self.assertTrue(
            committed == expected.encode("ascii"),
            "%s is stale (a spec, the registry, an [InGameTest] category or an "
            "autotest-status.md row changed); regenerate it with: %s"
            % (lc.DOC_RELPATH, lc.REGENERATE_COMMAND))


if __name__ == "__main__":
    unittest.main()
