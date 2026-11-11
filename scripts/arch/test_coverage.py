"""Tests for the coverage snapshot (coverage.py) and how the code facts read it.

All DLL-free: the reader runs on a hand-written Cobertura file in coverlet's
shape (fixtures/coverage_sample.cobertura.xml), and the facts side on inline
IL built with test_ilanalysis' helpers. No real snapshot is needed: with none,
everything says "not measured".
"""

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import archview  # noqa: E402
import coverage  # noqa: E402
import ilanalysis  # noqa: E402
import metrics  # noqa: E402
import test_ilanalysis  # noqa: E402
from test_ilanalysis import cls, meth  # noqa: E402

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "coverage_sample.cobertura.xml"


def parsed_fixture():
    return coverage.parse_cobertura(FIXTURE.read_text(encoding="utf-8"))


def lines_of(row):
    return row["lines"]["valid"], row["lines"]["covered"]


class FoldTests(unittest.TestCase):
    def test_generated_classes_fold_into_their_source_type(self):
        cases = {
            "Parsek.Alpha": "Parsek.Alpha",
            "Parsek.Alpha/<>c": "Parsek.Alpha",
            "Parsek.Alpha/<>c__DisplayClass5_0": "Parsek.Alpha",
            "Parsek.Alpha/<Run>d__12": "Parsek.Alpha",
            "Parsek.Alpha/<RunAsync>d__3": "Parsek.Alpha",
            "Parsek.Alpha+<>c__DisplayClass2_0": "Parsek.Alpha",
            "Parsek.Alpha.<>c": "Parsek.Alpha",
            "Parsek.Outer/Inner/<>c": "Parsek.Outer/Inner",
            "Parsek.Outer+Inner": "Parsek.Outer/Inner",
            "Parsek.Box`1/<Get>d__1": "Parsek.Box`1",
            "Parsek.Alpha/<>c__DisplayClass1_0/<<Run>b__0>d": "Parsek.Alpha",
        }
        for name, expected in cases.items():
            with self.subTest(name=name):
                self.assertEqual(coverage.fold_type_name(name), expected)

    def test_a_name_with_no_source_owner_is_dropped(self):
        for name in ("<PrivateImplementationDetails>", "<Module>", "", None):
            with self.subTest(name=name):
                self.assertIsNone(coverage.fold_type_name(name))


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.parsed = parsed_fixture()
        self.types = self.parsed["types"]

    def test_only_the_production_package_is_read(self):
        self.assertEqual(self.parsed["packages"], ["Parsek", "Parsek.Tests"])
        self.assertNotIn("Parsek.Tests.AlphaTests", self.types)
        self.assertEqual(sorted(self.types),
                         ["Parsek.Alpha", "Parsek.Beta", "Parsek.Beta/Inner", "Parsek.Delta`1", "Parsek.Gamma"])
        self.assertEqual((self.parsed["classes"], self.parsed["dropped"]), (11, 1))

    def test_folded_classes_add_their_lines_once(self):
        # Alpha's own 10, 11, 12; the lambda cache's 20, 21; the iterator's 30
        # (method lines only); the `+` closure's 40 and a repeat of 10.
        self.assertEqual(lines_of(self.types["Parsek.Alpha"]), (7, 5))
        self.assertEqual(self.types["Parsek.Alpha"]["branches"], {"valid": 4, "covered": 3})

    def test_a_partial_type_sums_its_files_and_a_nested_type_stays_apart(self):
        # Line 5 of two different files is two lines.
        self.assertEqual(lines_of(self.types["Parsek.Beta"]), (3, 1))
        self.assertEqual(self.types["Parsek.Beta"]["branches"], {"valid": 2, "covered": 0})
        # Inner keeps its own line and its dotted-separator state machine's.
        self.assertEqual(lines_of(self.types["Parsek.Beta/Inner"]), (2, 1))
        self.assertEqual(self.types["Parsek.Beta/Inner"]["branches"], {"valid": 0, "covered": 0})

    def test_missing_branch_data_is_unknown_not_zero(self):
        # No `branch` attribute at all, and a branch line with no condition-coverage.
        self.assertIsNone(self.types["Parsek.Gamma"]["branches"])
        self.assertIsNone(self.types["Parsek.Delta`1"]["branches"])
        self.assertEqual(coverage.totals(self.types)["branchesUnknownTypes"], 2)

    def test_unknown_attributes_and_unreadable_lines_are_tolerated(self):
        # Gamma has an unknown attribute and element, a line with hits="lots"
        # and one with no number: two readable lines remain.
        self.assertEqual(lines_of(self.types["Parsek.Gamma"]), (2, 1))

    def test_the_wrong_file_or_package_is_refused(self):
        with self.assertRaises(ValueError):
            coverage.parse_cobertura("<CoverageSession/>")
        with self.assertRaises(ValueError) as caught:
            coverage.parse_cobertura(FIXTURE.read_text(encoding="utf-8"), assembly="Other")
        self.assertIn("Parsek.Tests", str(caught.exception))

    def test_a_path_a_file_object_and_text_read_the_same(self):
        with open(FIXTURE, "rb") as handle:
            from_file = coverage.parse_cobertura(handle)
        self.assertEqual(coverage.parse_cobertura(str(FIXTURE)), from_file)
        self.assertEqual(self.parsed, from_file)
        self.assertEqual(from_file["timestamp"], 1794439549)

    def test_the_root_totals_are_cross_checked_not_enforced(self):
        # As written, the package's class elements sum to the root totals; folded,
        # Alpha's line 10 (listed by Alpha and by its closure) counts once.
        self.assertEqual(self.parsed["raw"], {"linesValid": 18, "linesCovered": 12, "branchesValid": 6,
                                              "branchesCovered": 3})
        self.assertEqual([row[3] for row in coverage.root_check(self.parsed)], [0, 0, 0, 0])
        text = FIXTURE.read_text(encoding="utf-8").replace('lines-valid="18"', 'lines-valid="21"', 1)
        shifted = coverage.parse_cobertura(text)
        self.assertEqual(coverage.root_check(shifted)[0], ("linesValid", 18, 21, -3))
        self.assertIn("differs by -3", coverage.format_summary(shifted))
        self.assertEqual(shifted["types"], self.types)
        bare = coverage.parse_cobertura(text.replace(' lines-covered="12"', "", 1))
        self.assertIsNone(coverage.root_check(bare)[1][2])
        self.assertIn("no root total", coverage.format_summary(bare))

    def test_a_class_with_method_lines_and_a_class_list_counts_each_line_once(self):
        text = ('<coverage><packages><package name="Parsek"><classes>'
                '<class name="Parsek.A" filename="A.cs"><methods><method name="M"><lines>'
                '<line number="1" hits="0" branch="True" condition-coverage="50% (1/2)"/>'
                '</lines></method></methods><lines>'
                '<line number="1" hits="2" branch="True" condition-coverage="50% (1/2)"/>'
                '<line number="2" hits="0" branch="False"/></lines></class>'
                '</classes></package></packages></coverage>')
        row = coverage.parse_cobertura(text)["types"]["Parsek.A"]
        self.assertEqual(lines_of(row), (2, 1))
        self.assertEqual(row["branches"], {"valid": 2, "covered": 1})


class SnapshotTests(unittest.TestCase):
    def snapshot(self):
        return coverage.make_snapshot(parsed_fixture(), "ABCDEF1234", "2026-11-11", "Debug")

    def test_the_snapshot_is_deterministic_and_reads_back(self):
        text = coverage.dumps_snapshot(self.snapshot())
        self.assertEqual(text, coverage.dumps_snapshot(coverage.make_snapshot(
            parsed_fixture(), "abcdef1234", "2026-11-11")))
        self.assertTrue(text.endswith("}\n"))
        back = coverage.read_snapshot(text)
        self.assertEqual(back, json.loads(json.dumps(self.snapshot())))
        self.assertEqual(back["schema"], "parsek-coverage/1")
        self.assertEqual((back["commit"], back["date"], back["configuration"]), ("abcdef1234", "2026-11-11", "Debug"))
        self.assertEqual(back["totals"], {"linesValid": 16, "linesCovered": 10, "branchesValid": 6,
                                          "branchesCovered": 3, "branchesUnknownTypes": 2})
        # One type per line, in sorted order, so a refresh diffs line by line.
        type_lines = [line for line in text.splitlines() if line.startswith('    "Parsek.')]
        self.assertEqual(len(type_lines), 5)
        self.assertEqual(type_lines, sorted(type_lines))

    def test_a_bad_commit_or_date_is_refused(self):
        for commit, date in (("not-a-sha", "2026-11-11"), ("abcdef1", "11/11/2026"), ("", "2026-11-11")):
            with self.subTest(commit=commit, date=date):
                with self.assertRaises(ValueError):
                    coverage.make_snapshot(parsed_fixture(), commit, date)

    def test_no_snapshot_file_is_not_measured_and_a_wrong_one_raises(self):
        with tempfile.TemporaryDirectory() as tmp:
            self.assertIsNone(coverage.load_snapshot(Path(tmp) / "none.json"))
            wrong = Path(tmp) / "wrong.json"
            wrong.write_text('{"schema": "parsek-metrics/1"}', encoding="utf-8")
            with self.assertRaises(ValueError):
                coverage.load_snapshot(wrong)

    def test_the_cli_writes_the_snapshot_only_when_asked(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "snap.json"
            with contextlib.redirect_stdout(io.StringIO()) as printed:
                coverage.main([str(FIXTURE), "--out", str(out)])
            self.assertFalse(out.exists())
            self.assertIn("lines: 10 / 16", printed.getvalue())
            with contextlib.redirect_stdout(io.StringIO()):
                coverage.main([str(FIXTURE), "--write-snapshot", "--commit", "abcdef1", "--date", "2026-11-11",
                               "--out", str(out)])
            self.assertEqual(coverage.load_snapshot(out)["commit"], "abcdef1")
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                coverage.main([str(FIXTURE), "--write-snapshot", "--out", str(out)])

    def test_no_snapshot_is_committed_from_the_fixture(self):
        # The real snapshot comes from a Windows run; a fixture-made one would
        # read as measured on the real tree.
        if coverage.DEFAULT_SNAPSHOT.is_file():
            snapshot = coverage.load_snapshot()
            self.assertNotIn("Parsek.Gamma", snapshot["types"])

    def test_the_module_name_resolves_to_this_file(self):
        self.assertIs(ilanalysis.coverage_module(), coverage)


# Production types Alpha (nested Inner) and Beta, tooling type Tool, and a
# delegate-like Empty with no body; the snapshot lists Alpha, Inner, Tool and
# a type the assembly no longer has.
IL = (cls("Parsek.Alpha", meth("Run", ["ldarg.0", "brtrue.s   IL_0000", "ret"], params="bool a"),
          "  .class nested public auto ansi beforefieldinit Inner\n         extends [mscorlib]System.Object\n  {\n"
          + meth("Go") + "\n  } // end of class Inner")
      + cls("Parsek.Beta", meth("Work"))
      + cls("Parsek.Tool", meth("Help"))
      + cls("Parsek.Empty", meth("Abstract", (), flags="public hidebysig newslot abstract virtual instance"),
            flags="public abstract auto ansi beforefieldinit"))


def fixture_snapshot():
    def row(valid, covered, branches=None):
        return {"lines": {"valid": valid, "covered": covered}, "branches": branches}

    return {"schema": coverage.SCHEMA, "assembly": "Parsek", "commit": "abcdef1", "date": "2026-11-01",
            "configuration": "Debug", "totals": {},
            "types": {"Parsek.Alpha": row(10, 6, {"valid": 4, "covered": 1}),
                      "Parsek.Alpha/Inner": row(10, 2, None),
                      "Parsek.Tool": row(5, 5, {"valid": 0, "covered": 0}),
                      "Parsek.Gone": row(3, 3, None)}}


def analyse(snapshot):
    facts = test_ilanalysis.facts_of(IL, tooling_types=("Parsek.Tool",))
    return ilanalysis.report(facts, ("Tools",), test_ilanalysis.CONFIG, None, None, snapshot)


class CoverageReportTests(unittest.TestCase):
    def test_modules_count_production_types_only(self):
        cover = analyse(fixture_snapshot())["coverage"]
        self.assertEqual(list(cover["byModule"]), ["Core"])
        core = cover["byModule"]["Core"]
        self.assertEqual((core["linesValid"], core["linesCovered"]), (20, 8))
        self.assertAlmostEqual(core["lineShare"], 0.4)
        # Inner's branches are unknown: the branch share is Alpha's alone.
        self.assertEqual((core["branchesValid"], core["branchesCovered"], core["branchesUnknownTypes"]), (4, 1, 1))
        self.assertEqual((cover["commit"], cover["date"]), ("abcdef1", "2026-11-01"))
        self.assertAlmostEqual(cover["byType"]["Parsek.Alpha"], 0.6)
        self.assertNotIn("Parsek.Tool", cover["byType"])

    def test_staleness_is_counted_both_ways_and_never_fails(self):
        stale = analyse(fixture_snapshot())["coverage"]["stale"]
        # Beta has code the snapshot lacks (Empty has none); Gone left the assembly.
        self.assertEqual((stale["notInSnapshot"], stale["notInAssembly"]), (1, 1))
        self.assertEqual((stale["notInSnapshotExamples"], stale["notInAssemblyExamples"]),
                         (["Parsek.Beta"], ["Parsek.Gone"]))
        line = ilanalysis.coverage_stale_line(analyse(fixture_snapshot())["coverage"])
        self.assertIn("does not list: 1", line)
        self.assertIn("lacks: 1", line)

    def test_the_atlas_states_when_it_was_measured(self):
        text = "\n".join(archview._atlas_coverage(analyse(fixture_snapshot())["coverage"]))
        self.assertIn("Measured at abcdef1, 2026-11-01", text)
        self.assertIn("does not list: 1", text)
        self.assertIn("40%", text)

    def test_no_snapshot_says_not_measured_everywhere(self):
        result = analyse(None)
        self.assertIsNone(result["coverage"])
        self.assertIn("Not measured", "\n".join(archview._atlas_coverage(result["coverage"])))
        self.assertIn("coverage: not measured", ilanalysis.format_report(result))
        self.assertIsNone(metrics.facts_section(result)["coverage"])
        # The Code facts section renders whole without it.
        self.assertIn("Line coverage", archview._atlas_code_facts(result))

    def test_the_metrics_row_carries_the_snapshot_shares(self):
        section = metrics.facts_section(analyse(fixture_snapshot()))["coverage"]
        self.assertEqual(section, {"commit": "abcdef1", "date": "2026-11-01", "lineShare": 0.4,
                                   "byModule": {"Core": 0.4}})
        json.loads(metrics.dumps(metrics.build_row("abcdef1", "2026-11-11T00:00:00Z", facts=metrics.facts_section(
            analyse(fixture_snapshot())))))


if __name__ == "__main__":
    unittest.main()
