"""Tests for `savepatch`'s `[fixture] trackingStationIntroDone` surface.

The surface sets stock's `ScenarioNewGameIntro.tsComplete = True` in the STAGED
save so the Tracking Station's first-visit intro lock (`intro_TS`, which holds
TIMEWARP) is never set, and a lane can warp above 1x there. Nothing else in the
save may move.

Runnable with the stdlib runner only::

    cd harness && python -m unittest discover -s lib -q
"""

import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
TOOLS_DIR = os.path.join(HARNESS_ROOT, "tools")
for _p in (HARNESS_ROOT, HERE, TOOLS_DIR):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import hlib  # noqa: E402
import savepatch  # noqa: E402
import tomllib  # noqa: E402

SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")

_SAVE = "\n".join([
    "GAME",
    "{",
    "\tSCENARIO",
    "\t{",
    "\t\tname = ScenarioNewGameIntro",
    "\t\tscene = 5, 6, 8",
    "\t\tkscComplete = True",
    "\t\teditorComplete = False",
    "\t\ttsComplete = False",
    "\t}",
    "\tSCENARIO",
    "\t{",
    "\t\tname = ParsekScenario",
    "\t\ttsComplete = False",
    "\t}",
    "}",
    "",
])


def _sfs(leaf):
    return os.path.join(HARNESS_ROOT, "fixtures", "saves", leaf, "persistent.sfs")


class ValidateTests(unittest.TestCase):
    def test_absent_and_true_are_valid(self):
        self.assertEqual([], savepatch.validate_ts_intro_done({}))
        self.assertEqual([], savepatch.validate_ts_intro_done(
            {"trackingStationIntroDone": True}))

    def test_anything_but_true_is_refused(self):
        for bad in (False, "true", 1, None, []):
            with self.subTest(value=bad):
                self.assertTrue(savepatch.validate_ts_intro_done(
                    {"trackingStationIntroDone": bad}))

    def test_hlib_spec_validation_calls_it(self):
        # hlib.validate_spec is the pre-launch gate; a malformed key must be an
        # INVALID-SPEC there, so its source must route through this validator.
        with open(hlib.__file__, "r", encoding="utf-8") as fh:
            self.assertIn("savepatch.validate_ts_intro_done(fixture)", fh.read())


class ApplyTests(unittest.TestCase):
    def test_not_declared_is_byte_identical(self):
        out, notes = savepatch.apply_ts_intro_done(_SAVE, False)
        self.assertEqual(_SAVE, out)
        self.assertEqual([], notes)

    def test_only_the_intro_node_key_moves(self):
        out, notes = savepatch.apply_ts_intro_done(_SAVE, True)
        self.assertEqual(["tsComplete False->True"], notes)
        expected = _SAVE.replace("\t\teditorComplete = False\n\t\ttsComplete = False",
                                 "\t\teditorComplete = False\n\t\ttsComplete = True")
        self.assertEqual(expected, out)
        # The same key in another SCENARIO node is not touched.
        self.assertIn("\t\tname = ParsekScenario\n\t\ttsComplete = False", out)

    def test_crlf_is_preserved(self):
        crlf = _SAVE.replace("\n", "\r\n")
        out, _ = savepatch.apply_ts_intro_done(crlf, True)
        self.assertEqual(crlf.replace("tsComplete = False\r\n\t}\r\n\tSCENARIO",
                                      "tsComplete = True\r\n\t}\r\n\tSCENARIO", 1), out)

    def test_idempotent(self):
        once, _ = savepatch.apply_ts_intro_done(_SAVE, True)
        twice, notes = savepatch.apply_ts_intro_done(once, True)
        self.assertEqual(once, twice)
        self.assertEqual(["tsComplete True->True"], notes)

    def test_all_three_complete_removes_the_node(self):
        save = _SAVE.replace("editorComplete = False", "editorComplete = True")
        out, notes = savepatch.apply_ts_intro_done(save, True)
        self.assertEqual(1, len(notes))
        self.assertIn("removed", notes[0])
        self.assertNotIn("ScenarioNewGameIntro", out)
        # Exactly the intro node went; the sibling scenario is whole.
        self.assertEqual("GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n"
                         "\t\ttsComplete = False\n\t}\n}\n", out)
        self.assertEqual(out.count("{"), out.count("}"))

    def test_no_intro_node_is_a_recorded_no_op(self):
        save = "GAME\n{\n\tSCENARIO\n\t{\n\t\tname = ParsekScenario\n\t}\n}\n"
        out, notes = savepatch.apply_ts_intro_done(save, True)
        self.assertEqual(save, out)
        self.assertEqual(1, len(notes))
        self.assertIn("unchanged", notes[0])

    def test_two_intro_nodes_fail_closed(self):
        save = _SAVE.replace("name = ParsekScenario", "name = ScenarioNewGameIntro")
        with self.assertRaises(savepatch.LiveStatePatchError):
            savepatch.apply_ts_intro_done(save, True)

    def test_an_intro_node_without_the_key_fails_closed(self):
        save = _SAVE.replace("\t\ttsComplete = False\n\t}\n\tSCENARIO",
                             "\t}\n\tSCENARIO", 1)
        with self.assertRaises(savepatch.LiveStatePatchError):
            savepatch.apply_ts_intro_done(save, True)


class CommittedSpecTests(unittest.TestCase):
    """Every spec that declares the key must be able to apply it to its own
    template, and must actually move the key (a template already at True would
    make the declaration a silent no-op)."""

    def test_each_declaring_spec_patches_its_template(self):
        found = 0
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            fixture = spec.get("fixture", {}) or {}
            if not savepatch.declared_ts_intro_done(fixture):
                continue
            found += 1
            with self.subTest(spec=name):
                self.assertEqual([], savepatch.validate_ts_intro_done(fixture))
                leaf = fixture["saveTemplate"].split("/")[-1]
                with open(_sfs(leaf), "rb") as fh:
                    text = fh.read().decode("utf-8")
                out, notes = savepatch.apply_ts_intro_done(text, True)
                self.assertEqual(1, len(notes))
                self.assertTrue(notes[0].startswith("tsComplete False->True"), notes)
                self.assertEqual(out.count("{"), out.count("}"))
                # After the patch no intro node can set intro_TS: either none is
                # left, or the one left reads tsComplete = True.
                lines = out.replace("\r\n", "\n").split("\n")
                nodes = savepatch._intro_scenario_nodes(lines)
                self.assertLessEqual(len(nodes), 1)
                for node in nodes:
                    self.assertEqual("True", savepatch.get_value(lines, node, "tsComplete"))
        self.assertGreater(found, 0, "no committed spec declares the key")


if __name__ == "__main__":
    unittest.main()
