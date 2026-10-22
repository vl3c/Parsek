"""Fixture and spec gates for `career-earned-pad-oldgen` (the GC-3 host).

The fixture is DERIVED by `harness/tools/build_career_earned_pad_oldgen.py` from the
committed `career-earned-pad`, so a re-harvest of that base must re-run the builder.
These cells turn that "must" into a red, pin the shape the inert-tree path depends on
(one committed tree whose TREE-level generation is another generation while its
recordings stay current, a ledger naming that other generation, rows tagged to the
tree's recordings), and keep `GC-3-other-generation-tree-kept-inert`'s pinned counts
equal to what the fixture bytes say.

Stdlib only; ASCII only; no em dashes.
"""

from __future__ import annotations

import importlib.util
import os
import re
import sys
import tomllib
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_REPO = os.path.dirname(_HARNESS)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

SAVES = os.path.join(_HARNESS, "fixtures", "saves")
FIXTURE_DIR = os.path.join(SAVES, "career-earned-pad-oldgen")
FIXTURE_SFS = os.path.join(FIXTURE_DIR, "persistent.sfs")
FIXTURE_LEDGER = os.path.join(FIXTURE_DIR, "Parsek", "GameState", "ledger.pgld")
SPEC_PATH = os.path.join(_HARNESS, "scenarios", "GC-3-other-generation-tree-kept-inert.toml")
SCHEMA_CS = os.path.join(_REPO, "Source", "Parsek", "RecordingSchema.cs")


def _load_builder():
    path = os.path.join(_HARNESS, "tools", "build_career_earned_pad_oldgen.py")
    spec = importlib.util.spec_from_file_location("build_career_earned_pad_oldgen", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _lines(path):
    with open(path, "r", encoding="utf-8", newline="") as fh:
        text = fh.read()
    return text.split("\r\n") if "\r\n" in text else text.split("\n")


def _current_generation():
    with open(SCHEMA_CS, encoding="utf-8") as fh:
        m = re.search(r"CurrentRecordingSchemaGeneration\s*=\s*(\d+)", fh.read())
    assert m, "schema-generation constant moved out of RecordingSchema.cs"
    return int(m.group(1))


def _ledger_rows(lines):
    """Each top-level GAME_ACTION block as a {key: value} dict."""
    rows = []
    i = 0
    while i < len(lines):
        if lines[i] == "GAME_ACTION":
            assert lines[i + 1] == "{"
            row = {}
            j = i + 2
            while lines[j] != "}":
                line = lines[j]
                if line.startswith("\t") and not line.startswith("\t\t") and " = " in line:
                    key, value = line[1:].split(" = ", 1)
                    row.setdefault(key, value)
                j += 1
            rows.append(row)
            i = j
        i += 1
    return rows


class CareerEarnedPadOldGenFixtureTests(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        cls.builder = _load_builder()
        cls.lines = _lines(FIXTURE_SFS)
        cls.trees = cls.builder.tree_blocks(cls.lines)
        cls.ledger = _lines(FIXTURE_LEDGER)
        cls.rows = _ledger_rows(cls.ledger)
        cls.current = _current_generation()

    def _tree(self):
        start, end, _ = self.trees[0]
        return self.lines[start:end]

    def _tree_recording_ids(self):
        return [l[len("\t\t\t\trecordingId = "):] for l in self._tree()
                if l.startswith("\t\t\t\trecordingId = ")]

    def test_committed_fixture_is_the_builder_output(self):
        self.assertEqual([], self.builder.committed_problems(),
                         "re-run python harness/tools/build_career_earned_pad_oldgen.py")

    def test_one_committed_tree(self):
        self.assertEqual([self.builder.TREE_ID], [t[2] for t in self.trees])
        self.assertFalse(any(l.startswith(("\t\t\tisActive = ", "\t\t\tisPending = "))
                             for l in self._tree()),
                         "only a COMMITTED tree is kept inert; an active / pending one is dropped")

    def test_the_whole_tree_is_rejected_at_tree_level_and_only_there(self):
        # InertRecordingTrees.ClassifySchemaRejection: a tree-level generation other than
        # the current one -> `tree-generation-older` (older) before any recording is read.
        tree_gen = [int(l.split(" = ")[1]) for l in self._tree()
                    if l.startswith("\t\t\trecordingSchemaGeneration = ")]
        rec_gen = [int(l.split(" = ")[1]) for l in self._tree()
                   if l.startswith("\t\t\t\trecordingSchemaGeneration = ")]
        self.assertEqual([self.builder.OTHER_GENERATION], tree_gen)
        self.assertLess(self.builder.OTHER_GENERATION, self.current,
                        "the stamp must be an OLDER generation (reason tree-generation-older)")
        # The recordings stay current: the analyzer reads their stamps, the product never does.
        self.assertEqual([self.current, self.current], rec_gen)
        self.assertEqual(self.current, self.builder.BASE_GENERATION)

    def test_the_ledger_names_the_other_generation_and_the_current_version(self):
        self.assertEqual("version = 0", self.ledger[0])
        self.assertEqual("recordingSchemaGeneration = %d" % self.builder.OTHER_GENERATION,
                         self.ledger[1])

    def test_the_ledger_carries_earned_rows_tagged_to_the_tree(self):
        ids = set(self._tree_recording_ids())
        self.assertEqual(2, len(ids))
        tagged = [r for r in self.rows if r.get("recordingId") in ids]
        stray = [r for r in self.rows if "recordingId" in r and r["recordingId"] not in ids]
        self.assertEqual([], stray, "a ledger row names a recording the fixture does not hold")
        self.assertEqual(16, len(self.rows))
        self.assertEqual(11, len(tagged))
        self.assertEqual(ids, {r["recordingId"] for r in tagged})
        by_type = {}
        for r in tagged:
            by_type[r["type"]] = by_type.get(r["type"], 0) + 1
        # 4 MilestoneAchievement, 3 ScienceEarning, 1 FundsEarning (GameActionType).
        self.assertEqual(4, by_type.get("4"))
        self.assertEqual(3, by_type.get("0"))
        self.assertEqual(1, by_type.get("2"))

    def test_the_sidecars_of_both_recordings_are_present(self):
        rec_dir = os.path.join(FIXTURE_DIR, "Parsek", "Recordings")
        for rid in self._tree_recording_ids():
            self.assertTrue(os.path.isfile(os.path.join(rec_dir, rid + ".prec")), rid)


class GC3SpecFixtureSyncTests(unittest.TestCase):
    """The lane pins counts the fixture determines; nothing else keeps them equal."""

    @classmethod
    def setUpClass(cls):
        with open(SPEC_PATH, "rb") as fh:
            cls.spec = tomllib.load(fh)
        cls.builder = _load_builder()
        cls.lines = _lines(FIXTURE_SFS)
        cls.rows = _ledger_rows(_lines(FIXTURE_LEDGER))
        cls.required = cls.spec["expectations"]["logContracts"]["required"]
        cls.current = _current_generation()

    def _required_with(self, needle):
        return [p for p in self.required if needle in p]

    def test_the_spec_points_at_the_fixture_and_injects_nothing(self):
        self.assertEqual("fixtures/saves/career-earned-pad-oldgen",
                         self.spec["fixture"]["saveTemplate"])
        self.assertEqual("none", self.spec["fixture"]["injectedRecordings"])

    def test_the_reload_is_a_cold_save_file_load_of_the_saved_game(self):
        steps = self.spec["driver"]["steps"]
        cmds = [s.get("cmd") for s in steps]
        save = cmds.index("SaveGame")
        self.assertEqual("persistent", steps[save]["args"]["name"])
        loads = [i for i, c in enumerate(cmds) if c == "LoadGame"]
        self.assertEqual(2, len(loads))
        self.assertLess(loads[0], save)
        self.assertLess(save, loads[1])
        self.assertEqual("persistent", steps[loads[1]]["args"]["name"])
        # Lowercase wire literal: TryParsePlayerLoad rejects a TOML bool's "True".
        self.assertEqual("true", steps[loads[1]]["args"]["playerLoad"])

    def test_the_pinned_tree_literals_match_the_fixture(self):
        tree_id = self.builder.TREE_ID
        summary = self._required_with("kept inert \\(current generation=")
        self.assertEqual(2, len(summary))
        for p in summary:
            self.assertIn("current generation=%d, recordings=2" % self.current, p)
            self.assertIn("%s\\(generation=%d reason=tree-generation-older\\)"
                          % (tree_id, self.builder.OTHER_GENERATION), p)
        skipped = self._required_with("due to unsupported schema generation")
        self.assertEqual(1, len(skipped))
        self.assertIn("generation=%d expected=%d" % (self.builder.OTHER_GENERATION, self.current),
                      skipped[0])
        name = [l for l in self.lines if l.startswith("\t\t\ttreeName = ")]
        self.assertEqual(["\t\t\ttreeName = Jumping Flea"], name)
        self.assertTrue(self._required_with("tree 'Jumping Flea' id=%s" % tree_id))

    def test_the_pinned_ledger_counts_match_the_fixture(self):
        recon = self._required_with("Reconcile complete: ")
        self.assertEqual(2, len(recon))
        for p in recon:
            self.assertIn("before=%d, kept=%d," % (len(self.rows), len(self.rows)), p)
            self.assertIn("validRecordingIds=2", p)
        self.assertTrue(self._required_with("ledgerRows=%d " % len(self.rows)))
        generation = self._required_with("carries recording schema generation")
        self.assertEqual(1, len(generation))
        self.assertIn("'%d' \\(current=%d\\)" % (self.builder.OTHER_GENERATION, self.current),
                      generation[0])
        milestones = sum(1 for r in self.rows if r.get("type") == "4")
        self.assertTrue(self._required_with("moduleCredited=%d" % milestones))

    def test_the_recording_count_window_is_the_fixture_prec_count(self):
        rec_dir = os.path.join(SAVES, "career-earned-pad-oldgen", "Parsek", "Recordings")
        precs = [f for f in os.listdir(rec_dir) if f.endswith(".prec")]
        self.assertEqual({"min": len(precs), "max": len(precs)},
                         self.spec["expectations"]["recordings"]["count"])


if __name__ == "__main__":
    unittest.main()
