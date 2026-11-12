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
            # Arities go, as ilfacts drops them: the facts know Parsek.GreyLedger.
            "Parsek.Box`1/<Get>d__1": "Parsek.Box",
            "Parsek.GreyLedger`1": "Parsek.GreyLedger",
            "Parsek.Logistics.EndpointScopedCache`1": "Parsek.Logistics.EndpointScopedCache",
            "Parsek.Outer`2/Inner`1/<>c": "Parsek.Outer/Inner",
            "Parsek.Alpha/<>c__DisplayClass1_0/<<Run>b__0>d": "Parsek.Alpha",
        }
        for name, expected in cases.items():
            with self.subTest(name=name):
                self.assertEqual(coverage.fold_type_name(name), expected)

    def test_a_name_with_no_source_owner_is_dropped(self):
        for name in ("<PrivateImplementationDetails>", "<Module>", "", None):
            with self.subTest(name=name):
                self.assertIsNone(coverage.fold_type_name(name))

    def test_generated_methods_fold_into_their_source_method_by_name(self):
        cases = {
            # A method of the source type is itself; a generic method's arity goes.
            ("Parsek.Alpha", "Run"): ("Parsek.Alpha", "Run"),
            ("Parsek.Alpha", ".ctor"): ("Parsek.Alpha", ".ctor"),
            ("Parsek.Box`1", "Get"): ("Parsek.Box", "Get"),
            # An iterator or async state machine's MoveNext is the method it names.
            ("Parsek.Alpha/<Walk>d__3", "MoveNext"): ("Parsek.Alpha", "Walk"),
            ("Parsek.Beta/Inner.<LoadAsync>d__1", "MoveNext"): ("Parsek.Beta/Inner", "LoadAsync"),
            ("Parsek.Alpha/<Get>d__1`1", "MoveNext"): ("Parsek.Alpha", "Get"),
            # An async lambda's state machine is the lambda's method.
            ("Parsek.Alpha/<>c__DisplayClass1_0/<<Run>b__0>d", "MoveNext"): ("Parsek.Alpha", "Run"),
            # A lambda in the lambda cache or a display class.
            ("Parsek.Alpha/<>c", "<Run>b__5_0"): ("Parsek.Alpha", "Run"),
            ("Parsek.Alpha+<>c__DisplayClass2_0", "<Run>b__0"): ("Parsek.Alpha", "Run"),
            ("Parsek.Alpha/<>c", "<get_Name>b__3_0"): ("Parsek.Alpha", "get_Name"),
            ("Parsek.Alpha/<>c", "<.ctor>b__1_0"): ("Parsek.Alpha", ".ctor"),
            # A generic type whose lambda sits in a nested closure class.
            ("Parsek.Cache`1/<>c__DisplayClass1_0", "<Get>b__0"): ("Parsek.Cache", "Get"),
            # A local function, on the type itself or in a closure class.
            ("Parsek.Alpha", "<Run>g__Clamp|3_0"): ("Parsek.Alpha", "Run"),
            ("Parsek.Alpha/<>c__DisplayClass3_0", "<Run>g__Inner|1"): ("Parsek.Alpha", "Run"),
        }
        for (class_name, method), expected in cases.items():
            with self.subTest(class_name=class_name, method=method):
                self.assertEqual(coverage.fold_method_name(class_name, method), expected)

    def test_other_generated_members_count_for_the_type_only(self):
        for class_name, method in (("Parsek.Alpha/<>c", ".cctor"), ("Parsek.Alpha/<>c", ".ctor"),
                                   ("Parsek.Alpha/<>c__DisplayClass2_0", ".ctor"),
                                   ("Parsek.Alpha/<Walk>d__3", ".ctor"),
                                   ("Parsek.Alpha/<Walk>d__3", "System.IDisposable.Dispose"),
                                   ("Parsek.Alpha/<>c__DisplayClass2_0", "MoveNext"),
                                   ("<PrivateImplementationDetails>", "ComputeStringHash"),
                                   ("Parsek.Alpha", ""), ("Parsek.Alpha", None)):
            with self.subTest(class_name=class_name, method=method):
                self.assertIsNone(coverage.fold_method_name(class_name, method))


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.parsed = parsed_fixture()
        self.types = self.parsed["types"]

    def test_only_the_production_package_is_read(self):
        self.assertEqual(self.parsed["packages"], ["Parsek", "Parsek.Tests"])
        self.assertNotIn("Parsek.Tests.AlphaTests", self.types)
        self.assertEqual(sorted(self.types),
                         ["Parsek.Alpha", "Parsek.Beta", "Parsek.Beta/Inner", "Parsek.Cache", "Parsek.Delta",
                          "Parsek.Gamma"])
        self.assertEqual((self.parsed["classes"], self.parsed["dropped"]), (13, 1))

    def test_folded_classes_add_their_lines_once(self):
        # Alpha's own 10, 11, 12, 13; the lambda cache's 20, 21; the iterator's 30
        # (method lines only); the `+` closure's 40 and a repeat of 10.
        self.assertEqual(lines_of(self.types["Parsek.Alpha"]), (8, 6))
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
        self.assertIsNone(self.types["Parsek.Delta"]["branches"])
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
        self.assertEqual(self.parsed["raw"], {"linesValid": 21, "linesCovered": 14, "branchesValid": 6,
                                              "branchesCovered": 3})
        self.assertEqual([row[3] for row in coverage.root_check(self.parsed)], [0, 0, 0, 0])
        text = FIXTURE.read_text(encoding="utf-8").replace('lines-valid="21"', 'lines-valid="24"', 1)
        shifted = coverage.parse_cobertura(text)
        self.assertEqual(coverage.root_check(shifted)[0], ("linesValid", 21, 24, -3))
        self.assertIn("differs by -3", coverage.format_summary(shifted))
        self.assertEqual(shifted["types"], self.types)
        bare = coverage.parse_cobertura(text.replace(' lines-covered="14"', "", 1))
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

    def test_methods_keep_their_own_lines_with_generated_code_and_overloads_folded_in(self):
        methods = self.parsed["methods"]
        self.assertEqual(sorted(methods), ["Parsek.Alpha::Run", "Parsek.Alpha::Walk", "Parsek.Beta/Inner::LoadAsync",
                                           "Parsek.Beta::Load", "Parsek.Cache::Get"])
        # Run: its own 10 (4 hits, 9 in the closure's repeat), 11, 12 (not run);
        # its local function's 13, the cached lambda's 20, the closure lambda's 40.
        # Not 21: the lambda cache's static constructor is the type's only.
        # Complexity 3 + 4 + 2 + 1.
        self.assertEqual(methods["Parsek.Alpha::Run"], {"valid": 6, "covered": 5, "complexity": 10})
        self.assertEqual(methods["Parsek.Alpha::Walk"], {"valid": 1, "covered": 1, "complexity": 2})
        # Two overloads in two files of a partial type: one method, line 5 of each file.
        self.assertEqual(methods["Parsek.Beta::Load"], {"valid": 3, "covered": 1, "complexity": 11})
        self.assertEqual(methods["Parsek.Beta/Inner::LoadAsync"], {"valid": 1, "covered": 0, "complexity": 1})
        # A generic type's lambda in its nested closure class joins the method.
        self.assertEqual(methods["Parsek.Cache::Get"], {"valid": 2, "covered": 1, "complexity": 12})
        self.assertEqual(lines_of(self.types["Parsek.Cache"]), (2, 1))


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
        self.assertEqual(back["totals"], {"linesValid": 19, "linesCovered": 12, "branchesValid": 6,
                                          "branchesCovered": 3, "branchesUnknownTypes": 2})
        # Methods at complexity 10 or more, as [covered, valid].
        self.assertEqual(back["methodComplexityMin"], 10)
        self.assertEqual(back["methods"], {"Parsek.Alpha::Run": [5, 6], "Parsek.Beta::Load": [1, 3],
                                           "Parsek.Cache::Get": [1, 2]})
        # One method and one type per line, in sorted order, so a refresh diffs line by line.
        method_lines = [line for line in text.splitlines() if line.startswith('    "Parsek.') and "::" in line]
        type_lines = [line for line in text.splitlines() if line.startswith('    "Parsek.') and "::" not in line]
        self.assertEqual((len(method_lines), len(type_lines)), (3, 6))
        self.assertEqual(method_lines, sorted(method_lines))
        self.assertEqual(type_lines, sorted(type_lines))

    def test_a_snapshot_without_methods_still_reads(self):
        old = self.snapshot()
        del old["methods"], old["methodComplexityMin"]
        back = coverage.read_snapshot(coverage.dumps_snapshot(old))
        self.assertNotIn("methods", back)
        self.assertEqual(back["types"], json.loads(json.dumps(old["types"])))
        broken = dict(old, methods={"Parsek.Alpha::Run": {"covered": 1}})
        with self.assertRaises(ValueError):
            coverage.read_snapshot(json.dumps(broken))

    def test_the_method_threshold_rises_until_the_snapshot_fits(self):
        parsed = parsed_fixture()
        full, text = coverage.fit_snapshot(parsed, "abcdef1", "2026-11-11")
        self.assertEqual((full["methodComplexityMin"], len(full["methods"])), (10, 3))
        # Too small for all three: 11 drops Run (10), 12 drops Load (11).
        size = len(text.encode("utf-8"))
        fitted, fitted_text = coverage.fit_snapshot(parsed, "abcdef1", "2026-11-11", max_bytes=size - 1)
        self.assertEqual((fitted["methodComplexityMin"], sorted(fitted["methods"])),
                         (11, ["Parsek.Beta::Load", "Parsek.Cache::Get"]))
        self.assertLess(len(fitted_text.encode("utf-8")), size)
        # Nothing fits: every method goes, the types stay.
        none, _text = coverage.fit_snapshot(parsed, "abcdef1", "2026-11-11", max_bytes=10)
        self.assertEqual((none["methods"], len(none["types"])), ({}, 6))

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
            self.assertIn("lines: 12 / 19", printed.getvalue())
            self.assertIn("methods: 5 source methods with lines, 3 with summed complexity >= 10", printed.getvalue())
            with contextlib.redirect_stdout(io.StringIO()) as printed:
                coverage.main([str(FIXTURE), "--write-snapshot", "--commit", "abcdef1", "--date", "2026-11-11",
                               "--out", str(out)])
            self.assertEqual(coverage.load_snapshot(out)["commit"], "abcdef1")
            self.assertIn("6 types, 3 methods with complexity >= 10, 1 KB", printed.getvalue())
            with contextlib.redirect_stdout(io.StringIO()) as printed:
                coverage.main([str(FIXTURE), "--write-snapshot", "--commit", "abcdef1", "--date", "2026-11-11",
                               "--out", str(out), "--max-kb", "0"])
            self.assertIn("0 methods with complexity >= 13 (raised from 10 to fit 0 KB)", printed.getvalue())
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
            flags="public abstract auto ansi beforefieldinit")
      # A class that declares no constructor: the compiler's has no source line.
      + cls("Parsek.Plain", test_ilanalysis.ctor()))


def fixture_snapshot():
    def row(valid, covered, branches=None):
        return {"lines": {"valid": valid, "covered": covered}, "branches": branches}

    return {"schema": coverage.SCHEMA, "assembly": "Parsek", "commit": "abcdef1", "date": "2026-11-01",
            "configuration": "Debug", "totals": {},
            "types": {"Parsek.Alpha": row(10, 6, {"valid": 4, "covered": 1}),
                      "Parsek.Alpha/Inner": row(10, 2, None),
                      "Parsek.Tool": row(5, 5, {"valid": 0, "covered": 0}),
                      "Parsek.Gone": row(3, 3, None)},
            "methodComplexityMin": 10,
            "methods": {"Parsek.Alpha::Run": [1, 4], "Parsek.Alpha/Inner::Go": [0, 2], "Parsek.Tool::Help": [1, 1],
                        "Parsek.Gone::Lost": [1, 1]}}


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
        # Beta has code the snapshot lacks (Empty has none, Plain only an implicit
        # constructor coverlet never lists); Gone left the assembly.
        self.assertEqual((stale["notInSnapshot"], stale["notInAssembly"], stale["noInstrumentedCode"]), (1, 1, 1))
        self.assertEqual((stale["notInSnapshotExamples"], stale["notInAssemblyExamples"]),
                         (["Parsek.Beta"], ["Parsek.Gone"]))
        line = ilanalysis.coverage_stale_line(analyse(fixture_snapshot())["coverage"])
        self.assertIn("instrumented code in this assembly the snapshot does not list: 1", line)
        self.assertIn("lacks: 1", line)
        self.assertIn("implicit constructor, which coverlet never lists: 1", line)

    def test_a_generic_arity_in_an_older_snapshot_still_joins(self):
        snapshot = fixture_snapshot()
        snapshot["types"]["Parsek.Beta`1"] = {"lines": {"valid": 4, "covered": 1}, "branches": None}
        cover = analyse(snapshot)["coverage"]
        self.assertAlmostEqual(cover["byType"]["Parsek.Beta"], 0.25)
        self.assertEqual(cover["stale"]["notInSnapshot"], 0)

    def test_kept_methods_join_by_type_and_name(self):
        cover = analyse(fixture_snapshot())["coverage"]
        # Production methods only: Tool is tooling, Gone left the assembly.
        self.assertEqual(cover["byMethod"], {"Parsek.Alpha::Run": 0.25, "Parsek.Alpha/Inner::Go": 0.0})
        self.assertEqual(cover["methodComplexityMin"], 10)
        self.assertIn("2 methods with complexity >= 10 kept", "\n".join(ilanalysis.format_coverage(cover)))
        old = fixture_snapshot()
        del old["methods"], old["methodComplexityMin"]
        self.assertEqual(analyse(old)["coverage"]["byMethod"], {})
        self.assertIn("regenerate it", "\n".join(ilanalysis.format_coverage(analyse(old)["coverage"])))

    def test_the_fixture_snapshot_joins_the_facts_and_lowers_the_score(self):
        # The Cobertura fixture's source types as IL: Run has two decisions, Walk
        # none, Load two overloads, Cache`1 a generic type whose Get has one.
        branch = ["ldarg.0", "brtrue.s   IL_0000"]
        il = (cls("Parsek.Alpha", meth("Run", branch * 2 + ["ret"], params="bool a"), meth("Walk"))
              + cls("Parsek.Beta", meth("Load"), meth("Load", params="string s"))
              + cls("Parsek.Cache`1<T>", meth("Get", branch + ["ret"], flags="public hidebysig instance",
                                               params="!T a")))
        facts = test_ilanalysis.facts_of(il)
        tests = test_ilanalysis.ilfacts.build(test_ilanalysis.ilgraph.parse_il(
            cls("Parsek.Tests.T", meth("Fact", ["call       void [Parsek]Parsek.Alpha::Walk()", "ret"]))))
        snapshot = coverage.make_snapshot(parsed_fixture(), "abcdef1", "2026-11-11", method_min=1)
        result = ilanalysis.report(facts, ("Tools",), test_ilanalysis.CONFIG, tests, None, snapshot)
        cover = result["coverage"]
        self.assertEqual(sorted(cover["byType"]), ["Parsek.Alpha", "Parsek.Beta", "Parsek.Cache"])
        self.assertEqual({key: round(share, 3) for key, share in cover["byMethod"].items()},
                         {"Parsek.Alpha::Run": 0.833, "Parsek.Alpha::Walk": 1.0, "Parsek.Beta::Load": 0.333,
                          "Parsek.Cache::Get": 0.5})
        rows = {row["key"]: row for row in result["testReach"]["risk"]["rows"]}
        self.assertEqual(sorted(rows), ["Parsek.Alpha::Run(bool)", "Parsek.Cache::Get(!0)"])
        # Run: 2 x 2 x (1 - 0.8 x 5/6) = 1.33; Get: 1 x 2 x (1 - 0.8 x 1/2) = 1.2.
        self.assertEqual((rows["Parsek.Alpha::Run(bool)"]["score"], rows["Parsek.Alpha::Run(bool)"]["lineShareOf"]),
                         (1.33, "method"))
        self.assertEqual((rows["Parsek.Cache::Get(!0)"]["score"], rows["Parsek.Cache::Get(!0)"]["lineShareOf"]),
                         (1.2, "method"))

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
