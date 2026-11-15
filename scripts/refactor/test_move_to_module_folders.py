"""Unit tests for move_to_module_folders.py (plan, collision checks, --check)."""

import contextlib
import io
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import move_to_module_folders as mtm  # noqa: E402

RULES = [
    {"name": "Ghost", "folder": "Ghost"},
    {"name": "Ghost", "prefix": r"^Ghost"},
    {"name": "UI", "folder": "UI"},
    {"name": "UI", "prefix": r"^ParsekUI"},
]


class PlanTests(unittest.TestCase):
    def test_first_rule_set_that_places_a_file_wins(self):
        older = [{"name": "Recording", "prefix": r"^FlightRecorder"}]
        moves, unplaced = mtm.plan_moves(
            ["FlightRecorder.cs", "GhostX.cs", "Stray.cs"],
            [("tree", RULES), ("origin/main", older)],
        )
        self.assertEqual(moves, [("FlightRecorder.cs", "Recording"), ("GhostX.cs", "Ghost")])
        self.assertEqual(unplaced, ["Stray.cs"])

    def test_root_files_lists_only_root_cs_files_sorted(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "Ghost").mkdir()
            for rel in ("b.cs", "a.cs", "notes.txt", "Ghost/c.cs"):
                (root / rel).write_text("x")
            self.assertEqual(mtm.root_files(root), ["a.cs", "b.cs"])


class TargetCheckTests(unittest.TestCase):
    def _tree(self, rels, dirs=()):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        for d in dirs:
            (root / d).mkdir(parents=True, exist_ok=True)
        for rel in rels:
            (root / rel).parent.mkdir(parents=True, exist_ok=True)
            (root / rel).write_text("x")
        return root

    def test_a_free_target_passes(self):
        root = self._tree(["GhostA.cs"], dirs=["Ghost"])
        self.assertEqual(mtm.check_targets(root, [("GhostA.cs", "Ghost")]), [])

    def test_an_existing_target_is_refused(self):
        root = self._tree(["GhostA.cs", "Ghost/GhostA.cs"])
        problems = mtm.check_targets(root, [("GhostA.cs", "Ghost")])
        self.assertEqual(len(problems), 1)
        self.assertIn("target exists", problems[0])

    def test_a_folder_differing_only_by_case_is_refused(self):
        root = self._tree(["GhostA.cs"], dirs=["ghost"])
        problems = mtm.check_targets(root, [("GhostA.cs", "Ghost")])
        self.assertTrue(any("differs only by case" in p for p in problems), problems)

    def test_a_basename_differing_only_by_case_is_refused(self):
        root = self._tree(["GhostA.cs", "Ghost/ghosta.cs"])
        problems = mtm.check_targets(root, [("GhostA.cs", "Ghost")])
        self.assertTrue(any("differs only by case" in p for p in problems), problems)

    def test_two_files_with_one_target_are_refused(self):
        root = self._tree([], dirs=["Ghost"])
        problems = mtm.check_targets(root, [("GhostA.cs", "Ghost"), ("ghosta.cs", "Ghost")])
        self.assertTrue(any("two root files map to" in p for p in problems), problems)


class CheckModeTests(unittest.TestCase):
    def _repo(self, rels):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        repo = Path(tmp.name)
        for rel in rels:
            path = repo / mtm.SOURCE_REL / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("x")
        return repo

    def _run(self, argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = mtm.main(argv)
        return rc, out.getvalue()

    def test_check_reds_on_a_root_file(self):
        repo = self._repo(["Ghost/GhostA.cs", "Stray.cs"])
        rc, out = self._run(["--check", "--repo", str(repo)])
        self.assertEqual(rc, 1)
        self.assertIn("Stray.cs", out)

    def test_check_passes_and_a_run_is_a_no_op_with_no_root_file(self):
        repo = self._repo(["Ghost/GhostA.cs"])
        self.assertEqual(self._run(["--check", "--repo", str(repo)])[0], 0)
        rc, out = self._run(["--repo", str(repo)])
        self.assertEqual(rc, 0)
        self.assertIn("nothing to move", out)


if __name__ == "__main__":
    unittest.main()
