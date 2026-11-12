"""Tests for scripts/arch/metrics.py and metrics_append.py, and the CI wiring
that builds a row for every tested commit and appends main's to the metrics
branch. Stdlib only; the git cells drive a temporary bare repository."""

from __future__ import annotations

import contextlib
import fnmatch
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import metrics  # noqa: E402
import metrics_append  # noqa: E402
import test_semantic  # noqa: E402

IL_ENV = test_semantic.IL_ENV
WORKFLOW = test_semantic.WORKFLOW
SHA = "0123456789abcdef0123456789abcdef01234567"
SHA2 = "fedcba9876543210fedcba9876543210fedcba98"
SHA3 = "1111111111111111111111111111111111111111"


def fixture_model():
    """A two-module text model: A <-> B is a knot, C hangs off it."""
    return {
        "modules": [
            {"name": "Core", "files": 2, "tooling": False},
            {"name": "UI", "files": 1, "tooling": False},
            {"name": "Tests", "files": 1, "tooling": True},
        ],
        "edges": [
            {"from": "UI", "to": "Core", "upward": False},
            {"from": "Core", "to": "UI", "upward": True},
        ],
        "unclassified": [],
        "types": [
            {"name": "Alpha", "module": "Core", "referencesTo": ["Beta"]},
            {"name": "Beta", "module": "UI", "referencesTo": ["Alpha"]},
            {"name": "Gamma", "module": "Core", "referencesTo": ["Alpha"]},
        ],
        "knots": [
            {"size": 2, "members": ["Alpha", "Beta"],
             "cuts": [{"sink": "Beta", "sizeBefore": 2, "sizeAfter": 1, "droppedReferences": ["Alpha"]}]},
        ],
        "sizes": {"files": [
            {"file": "A.cs", "module": "Core", "lines": 100},
            {"file": "B.cs", "module": "Core", "lines": 50},
            {"file": "C.cs", "module": "UI", "lines": 30},
        ]},
    }


def fixture_semantic():
    return {
        "text": {"Alpha": ["Beta"], "Beta": ["Alpha"], "Gamma": ["Alpha"]},
        "raw": {"Alpha": ["Beta"], "Beta": ["Alpha", "Gamma"], "Gamma": []},
        "semantic": {"Alpha": ["Beta"], "Beta": ["Alpha"], "Gamma": []},
        "typeModules": {"Alpha": "Core", "Beta": "UI", "Gamma": "Core"},
        "textMeasure": {"knot": 2, "greedy": 1, "replayed": 2, "residue": ["Alpha", "Beta"]},
        "semanticMeasure": {"knot": 2, "greedy": 1, "replayed": 1, "residue": []},
        "rawMeasure": {"knot": 3, "greedy": 2, "replayed": 3, "residue": ["Alpha", "Beta", "Gamma"]},
        "semanticTextOrder": 2,
        "forwarderCalls": [("Beta", "Fwd", "Do", "Gamma")],
        "forwarderEdges": ([("Beta", "Gamma")], []),
    }


def fixture_report(with_tests=False):
    report = {
        "statics": {"totals": {"reassignable": 7, "readonlyCollections": 3, "mutatedCollections": 2,
                               "writtenElsewhere": 1, "addressTakenElsewhere": 1}},
        "complexity": {"atLeast": {20: 4, 50: 1, 100: 0}},
        "coupling": {"share": 0.123456, "byModule": {
            "Core": {"share": 0.1, "directShare": 0.05, "instructions": 900, "methods": 40},
            "UI": {"share": 2 / 3.0, "directShare": 0.5, "instructions": 300, "methods": 10},
        }},
        "uncalled": {"uncalled": ["A::x()", "A::y()"], "instructions": {"A::x()": 10, "A::y()": 5},
                     "rootedBy": {}},
        "testReach": None,
        "gates": {"crossTypeStaticWrites": ["A::s"], "complexity50": ["A::Big"],
                  "complexityWatch": ["A::Big", "A::Near"], "uncalledInAssembly": ["A::x()"]},
    }
    if with_tests:
        report["uncalled"]["noCaller"] = ["A::y()"]
        report["uncalled"]["testOnly"] = ["A::x()"]
        report["testReach"] = {
            "byModule": {"Core": {"methodShare": 0.5, "instructionShare": 0.25, "methods": 40,
                                  "instructions": 900, "reachedMethods": 20, "reachedInstructions": 225}},
            "untestedComplex": [{"key": "A::Big"}],
            "unmatchedTestCalls": [],
        }
    return report


def fixture_counts():
    return {
        "head": "abc1234 2026-11-11",
        "areas": [{"area": "Mod source (C#)", "label": "Mod source (C#)", "files": 3, "lines": 180,
                   "code": 150, "blank": 20, "comment": 10}],
        "filesOutsideAreas": 2,
        "tests": {"xunitFacts": 5, "pythonTestMethods": 9},
    }


def fixture_history():
    """An archview.read_history payload, cut to the keys the row reads."""
    def window(units, fix, kinds):
        return {"units": units, "fixUnits": fix, "kinds": kinds}

    def by_window(fix90, fix18):
        return {"30d": {"fix": 0}, "90d": {"fix": fix90}, "18m": {"fix": fix18}}

    return {
        "measured": True,
        "anchor": "2026-11-11",
        "renames": 4, "moves": 2, "dropped": 3, "skippedSweeps": 1,
        "windows": {"30d": {"since": "2026-10-12", "commits": 5},
                    "90d": {"since": "2026-08-13", "commits": 12},
                    "18m": {"since": "2025-05-01", "commits": 40}},
        "changeKinds": {
            "measured": True,
            "windows": {"30d": window(3, 1, {"fix": 1, "docs": 2}),
                        "90d": window(7, 2, {"fix": 2, "feature": 5}),
                        "18m": window(20, 7, {"fix": 7, "feature": 13})},
            "modules": [{"name": "Core", "byWindow": by_window(2, 6)},
                        {"name": "UI", "byWindow": by_window(0, 1)}],
        },
    }


# Core below UI: Alpha -> Beta runs backward, Beta -> Alpha down.
FIXTURE_LAYERS = {"order": ["low", "high"], "modules": {"low": ["Core"], "high": ["UI"]}}


def full_row(with_tests=True):
    return metrics.build_row(
        SHA, "2026-11-11T19:42:00+02:00",
        metrics.text_section(fixture_model(), ["UI -> Core", "Core -> Missing", "not a spec"], FIXTURE_LAYERS),
        metrics.semantic_section(fixture_semantic(), FIXTURE_LAYERS),
        metrics.facts_section(fixture_report(with_tests)),
        metrics.sizes_section(fixture_counts()),
        tests_dll=with_tests,
    )


class RowBuilderTests(unittest.TestCase):
    def test_top_level_keys_and_identity(self):
        row = full_row()
        self.assertEqual(sorted(row),
                         ["commit", "facts", "history", "measured", "schema", "semantic", "sizes", "text"])
        self.assertEqual(row["schema"], "parsek-metrics/1")
        self.assertEqual(row["commit"], {"sha": SHA, "date": "2026-11-11T17:42:00Z"})
        self.assertEqual(row["measured"], {"text": True, "sizes": True, "il": True, "testsDll": True,
                                           "history": False})
        self.assertIsNone(row["history"])

    def test_text_section(self):
        text = full_row()["text"]
        self.assertEqual(text["modules"], 2)
        self.assertEqual(text["toolingModules"], 1)
        self.assertEqual(text["byModule"], {"Core": {"files": 2, "lines": 150}, "UI": {"files": 1, "lines": 30}})
        self.assertEqual((text["productionFiles"], text["productionLines"]), (3, 180))
        # Two well-formed specs declared, one of them present; the malformed one is skipped.
        self.assertEqual(text["forbiddenEdges"], {"declared": 2, "violations": 1})
        self.assertEqual(text["upwardEdges"], 1)
        self.assertEqual(text["knots"], [2])
        self.assertEqual((text["knot"], text["greedyResidue"]), (2, 1))
        self.assertEqual(text["replayedResidue"], 2)
        self.assertEqual(text["firstCut"], {"sink": "Beta", "sizeBefore": 2, "sizeAfter": 1})
        self.assertEqual(text["productionTypes"], 3)
        self.assertEqual(text["unclassifiedFiles"], 0)
        self.assertEqual(text["layers"], {"backwardModulePairs": 1, "backwardTypePairs": 1,
                                          "lateralModulePairs": 0, "lateralTypePairs": 0})

    def test_the_layer_keys_are_null_without_a_declaration(self):
        self.assertIsNone(metrics.text_section(fixture_model())["layers"])
        self.assertIsNone(metrics.semantic_section(fixture_semantic())["layers"])
        model = fixture_model()
        model["types"].append({"name": "Delta", "module": "UI", "referencesTo": ["Alpha", "Gamma", "Beta"]})
        lateral = {"order": ["one"], "modules": {"one": ["Core", "UI"]}}
        self.assertEqual(metrics.text_section(model, (), lateral)["layers"],
                         {"backwardModulePairs": 0, "backwardTypePairs": 0,
                          "lateralModulePairs": 2, "lateralTypePairs": 4})

    def test_text_section_without_a_knot(self):
        model = fixture_model()
        model["knots"] = []
        text = metrics.text_section(model)
        self.assertEqual((text["knot"], text["greedyResidue"], text["knots"], text["firstCut"]), (0, 0, [], None))
        self.assertEqual(text["forbiddenEdges"], {"declared": 0, "violations": 0})

    def test_semantic_section(self):
        sem = full_row()["semantic"]
        self.assertEqual((sem["knot"], sem["greedyResidue"], sem["replayedResidue"]), (2, 1, 1))
        self.assertEqual(sem["ratchetResidue"], 1)
        self.assertEqual(sem["textOrderResidue"], 2)
        self.assertEqual(sem["text"], {"knot": 2, "greedyResidue": 1, "replayedResidue": 2, "ratchetResidue": 1})
        self.assertEqual(sem["raw"], {"knot": 3, "greedyResidue": 2, "replayedResidue": 3})
        self.assertEqual(sem["edges"], {"text": 3, "raw": 3, "semantic": 2, "textOnly": 1, "ilOnly": 1})
        self.assertEqual(sem["forwarders"], {"callSites": 1, "edgesRemoved": 1, "edgesAdded": 0})
        self.assertEqual(sem["residueMembership"], {"both": 0, "textOnly": 2, "semanticOnly": 0})
        self.assertEqual(sem["layers"], {"backwardModulePairs": 1, "backwardTypePairs": 1,
                                         "lateralModulePairs": 0, "lateralTypePairs": 0})

    def test_facts_section(self):
        facts = full_row()["facts"]
        self.assertEqual(facts["ratchets"], {"crossTypeStaticWrites": 1, "complexity50": 1,
                                             "complexityWatch": 2, "uncalledInAssembly": 1})
        self.assertEqual(facts["statics"]["reassignable"], 7)
        self.assertEqual(facts["statics"]["mutatedElsewhere"], 0)
        self.assertEqual(facts["complexity"], {"20": 4, "50": 1, "100": 0})
        self.assertEqual(facts["coupling"]["share"], 0.1235)
        self.assertEqual(facts["coupling"]["byModule"]["UI"],
                         {"share": 0.6667, "directShare": 0.5, "instructions": 300, "methods": 10})
        self.assertEqual(facts["uncalled"], {"methods": 2, "instructions": 15, "noTestCaller": 1, "testOnly": 1})
        self.assertEqual(facts["testReach"], {
            "byModule": {"Core": {"methodShare": 0.5, "instructionShare": 0.25, "methods": 40}},
            "untestedComplex": 1})

    def test_facts_carry_the_type_reach_and_coverage_when_measured(self):
        # Both additive: absent from a report without them, so the row above is unchanged.
        self.assertIsNone(full_row()["facts"]["coverage"])
        report = fixture_report(True)
        report["testReach"]["types"] = {"production": 12, "reached": 5}
        report["coverage"] = {"commit": "abcdef1", "date": "2026-11-01", "lineShare": 2 / 3.0,
                              "byModule": {"Core": {"lineShare": 0.123456}, "UI": {"lineShare": None}}}
        section = metrics.facts_section(report)
        self.assertEqual(section["testReach"]["types"], {"production": 12, "reached": 5})
        self.assertEqual(section["coverage"], {"commit": "abcdef1", "date": "2026-11-01", "lineShare": 0.6667,
                                               "byModule": {"Core": 0.1235, "UI": None}})
        json.loads(metrics.dumps(metrics.build_row(SHA, "2026-11-11T17:42:00Z", facts=section)))

    def test_facts_carry_the_lifetime_summary(self):
        self.assertIsNone(full_row()["facts"]["lifetimes"])
        report = fixture_report(True)
        report["lifetimes"] = {"population": 3, "tagged": 2, "untagged": 1, "byLifetime": {"save": 2},
                               "coldClosureMethods": 9, "findings": {"coldLoadMisses": 0,
                                                                     "testWrittenUntagged": None}}
        section = metrics.facts_section(report)
        self.assertEqual(section["lifetimes"], report["lifetimes"])
        json.loads(metrics.dumps(metrics.build_row(SHA, "2026-11-11T17:42:00Z", facts=section)))

    def test_facts_carry_the_headless_counts(self):
        self.assertIsNone(full_row()["facts"]["headless"])
        import test_ilheadless  # noqa: PLC0415

        report = fixture_report(True)
        report["headless"] = test_ilheadless.run()
        section = metrics.facts_section(report)["headless"]
        self.assertEqual((section["modules"], section["leaks"], section["leakingMethods"], section["seams"]),
                         (["Rules"], 6, 6, 1))
        self.assertEqual(section["byModule"]["Rules"],
                         {"headless": True, "methods": 14, "reachMethods": 8, "directMethods": 4, "leaks": 6})
        self.assertIsNone(section["byModule"]["Ctl"]["leaks"])
        json.loads(metrics.dumps(metrics.build_row(SHA, "2026-11-11T17:42:00Z",
                                                   facts=metrics.facts_section(report))))

    def test_facts_without_the_test_dll(self):
        row = full_row(with_tests=False)
        self.assertIsNone(row["facts"]["testReach"])
        self.assertIsNone(row["facts"]["uncalled"]["noTestCaller"])
        self.assertIsNone(row["facts"]["uncalled"]["testOnly"])
        self.assertFalse(row["measured"]["testsDll"])
        self.assertTrue(row["measured"]["il"])

    def test_sizes_section(self):
        sizes = full_row()["sizes"]
        self.assertEqual(sizes["areas"], {"Mod source (C#)": {"files": 3, "lines": 180, "code": 150}})
        self.assertEqual(sizes["filesOutsideAreas"], 2)
        self.assertEqual(sizes["tests"], {"xunitFacts": 5, "pythonTestMethods": 9})

    def test_the_dll_half_is_null_without_a_dll(self):
        row = metrics.build_row(SHA, "2026-11-11T17:42:00Z", metrics.text_section(fixture_model()),
                                None, None, metrics.sizes_section(fixture_counts()), tests_dll=True)
        self.assertIsNone(row["semantic"])
        self.assertIsNone(row["facts"])
        self.assertEqual(row["measured"], {"text": True, "sizes": True, "il": False, "testsDll": False,
                                           "history": False})
        json.loads(metrics.dumps(row))

    def test_output_is_deterministic(self):
        first, second = metrics.dumps(full_row()), metrics.dumps(full_row())
        self.assertEqual(first, second)
        self.assertTrue(first.endswith("\n"))
        self.assertEqual(first.count("\n"), 1)
        self.assertEqual(first, json.dumps(json.loads(first), sort_keys=True, separators=(",", ":")) + "\n")
        # Insertion order does not leak into the output.
        row = full_row()
        shuffled = dict(reversed(list(row.items())))
        self.assertEqual(metrics.dumps(shuffled), first)

    def test_dates_normalize_to_utc(self):
        self.assertEqual(metrics.normalize_date("2026-11-11T17:42:00Z"), "2026-11-11T17:42:00Z")
        self.assertEqual(metrics.normalize_date("2026-11-11T00:30:00+02:00"), "2026-11-10T22:30:00Z")
        self.assertEqual(metrics.normalize_date("2026-11-11T17:42:00"), "2026-11-11T17:42:00Z")
        with self.assertRaises(ValueError):
            metrics.normalize_date("yesterday")

    def test_a_bad_sha_is_refused(self):
        for bad in ("", "xyz", "0123456789abcdef0123456789abcdef012345678"):
            with self.assertRaises(ValueError):
                metrics.build_row(bad, "2026-11-11T17:42:00Z")
        self.assertEqual(metrics.build_row(SHA.upper(), "2026-11-11T17:42:00Z")["commit"]["sha"], SHA)

    def test_the_history_section(self):
        section = metrics.history_section(fixture_history())
        self.assertEqual(section["anchor"], "2026-11-11")
        self.assertEqual((section["renames"], section["moves"], section["dropped"], section["skippedSweeps"]),
                         (4, 2, 3, 1))
        self.assertEqual(sorted(section["windows"]), ["18m", "30d", "90d"])
        self.assertEqual(section["windows"]["30d"], {"since": "2026-10-12", "commits": 5, "units": 3,
                                                     "fixUnits": 1, "kinds": {"fix": 1, "docs": 2}})
        self.assertEqual(section["windows"]["18m"]["commits"], 40)
        self.assertEqual(section["fixUnitsByModule"], {"90d": {"Core": 2, "UI": 0}, "18m": {"Core": 6, "UI": 1}})
        row = metrics.build_row(SHA, "2026-11-11T17:42:00Z", history=section)
        self.assertTrue(row["measured"]["history"])
        self.assertEqual(json.loads(metrics.dumps(row))["history"], section)

    def test_the_history_section_says_what_was_not_measured(self):
        self.assertIsNone(metrics.history_section(None))
        self.assertIsNone(metrics.history_section({"measured": False, "reason": "shallow clone"}))
        history = fixture_history()
        history["changeKinds"] = {"measured": False, "reason": "CHANGELOG rebuild did not match HEAD"}
        section = metrics.history_section(history)
        self.assertEqual(section["windows"]["90d"], {"since": "2026-08-13", "commits": 12, "units": None,
                                                     "fixUnits": None, "kinds": None})
        self.assertIsNone(section["fixUnitsByModule"])

    def test_the_size_counts_come_from_count_code(self):
        module = metrics.load_count_code()
        self.assertTrue(callable(module.collect))


class AppendLogicTests(unittest.TestCase):
    def row(self, sha, schema=metrics.SCHEMA):
        return {"schema": schema, "commit": {"sha": sha, "date": "2026-11-11T17:42:00Z"}, "text": {"knot": 1}}

    def test_append_keeps_arrival_order_and_lines(self):
        text, appended = metrics_append.append_text("", self.row(SHA2))
        self.assertTrue(appended)
        text, appended = metrics_append.append_text(text, self.row(SHA))
        self.assertTrue(appended)
        lines = text.splitlines()
        self.assertEqual([json.loads(line)["commit"]["sha"] for line in lines], [SHA2, SHA])
        self.assertTrue(text.endswith("\n"))
        self.assertEqual(metrics_append.read_shas(text), [SHA2, SHA])

    def test_a_repeated_sha_changes_nothing(self):
        text, _ = metrics_append.append_text("", self.row(SHA))
        again, appended = metrics_append.append_text(text, self.row(SHA))
        self.assertFalse(appended)
        self.assertEqual(again, text)

    def test_existing_lines_are_kept_byte_for_byte(self):
        existing = '{"schema": "parsek-metrics/1", "commit": {"sha": "%s"}}' % SHA2  # no final newline
        text, appended = metrics_append.append_text(existing, self.row(SHA))
        self.assertTrue(appended)
        self.assertTrue(text.startswith(existing + "\n"))

    def test_schema_refusal(self):
        for row in (self.row(SHA, "parsek-metrics/2"), self.row(SHA, None), [], {"schema": metrics.SCHEMA}):
            with self.assertRaises(metrics_append.RowError):
                metrics_append.append_text("", row)

    def test_a_malformed_history_is_not_written_over(self):
        with self.assertRaises(metrics_append.RowError):
            metrics_append.append_text("not json\n", self.row(SHA))
        with self.assertRaises(metrics_append.RowError):
            metrics_append.append_text('{"schema": "other"}\n', self.row(SHA))

    def test_the_writer_and_the_appender_agree_on_the_schema(self):
        self.assertIn(metrics.SCHEMA, metrics_append.KNOWN_SCHEMAS)
        line = metrics.dumps(full_row())
        text, appended = metrics_append.append_text("", json.loads(line))
        self.assertTrue(appended)
        self.assertEqual(text, line)

    def test_append_file_and_cli(self):
        with tempfile.TemporaryDirectory() as tmp:
            row_path = Path(tmp) / "row.json"
            row_path.write_text(json.dumps(self.row(SHA)), encoding="utf-8")
            history = Path(tmp) / "metrics.jsonl"
            with contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(metrics_append.main(["append", "--row", str(row_path), "--file", str(history)]), 0)
                self.assertEqual(metrics_append.main(["append", "--row", str(row_path), "--file", str(history)]), 0)
            self.assertEqual(out.getvalue().split("\n")[:2], ["metrics: %s appended" % SHA[:12],
                                                              "metrics: %s already present" % SHA[:12]])
            self.assertEqual(metrics_append.read_shas(history.read_text(encoding="utf-8")), [SHA])
            row_path.write_text(json.dumps(self.row(SHA2, "nope")), encoding="utf-8")
            with open(os.devnull, "w") as sink:
                stderr, sys.stderr = sys.stderr, sink
                try:
                    self.assertEqual(metrics_append.main(["append", "--row", str(row_path), "--file", str(history)]), 1)
                finally:
                    sys.stderr = stderr


def run_git(args, cwd):
    subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false",
                    *args], cwd=str(cwd), check=True, capture_output=True, text=True)


def git_out(args, cwd):
    return subprocess.run(["git", *args], cwd=str(cwd), check=True, capture_output=True, text=True).stdout


@unittest.skipUnless(shutil.which("git"), "git is not on PATH")
class GitRoundTripTests(unittest.TestCase):
    """publish() against a temporary bare repository standing in for the remote."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="parsek-metrics-"))
        self.origin = self.tmp / "origin.git"
        run_git(["init", "--quiet", "--bare", str(self.origin)], self.tmp)
        self.source = self.clone("source")
        (self.source / "code.txt").write_text("code\n", encoding="utf-8")
        run_git(["add", "code.txt"], self.source)
        run_git(["commit", "--quiet", "-m", "code"], self.source)
        run_git(["push", "--quiet", "origin", "HEAD:refs/heads/main"], self.source)
        self.logs = []

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def clone(self, name):
        path = self.tmp / name
        if (self.origin / "refs" / "heads" / "main").exists():
            run_git(["clone", "--quiet", "--branch", "main", str(self.origin), str(path)], self.tmp)
        else:
            path.mkdir()
            run_git(["init", "--quiet"], path)
            run_git(["remote", "add", "origin", str(self.origin)], path)
        return path

    def row(self, sha):
        return {"schema": metrics.SCHEMA, "commit": {"sha": sha, "date": "2026-11-11T17:42:00Z"}}

    def publish(self, repo, workdir, sha, **kwargs):
        kwargs.setdefault("delay", 0)
        return metrics_append.publish(repo, "origin", "metrics", self.tmp / workdir, self.row(sha),
                                      log=self.logs.append, **kwargs)

    def remote_file(self, name):
        return git_out(["--git-dir", str(self.origin), "show", "metrics:%s" % name], self.tmp)

    def remote_log(self):
        return git_out(["--git-dir", str(self.origin), "log", "--format=%s|%an", "metrics"], self.tmp).splitlines()

    def test_creates_the_branch_then_appends_then_is_idempotent(self):
        self.assertEqual(self.publish(self.source, "w1", SHA), "pushed")
        self.assertIn("Parsek metrics history", self.remote_file("README.md"))
        self.assertEqual(metrics_append.read_shas(self.remote_file("metrics.jsonl")), [SHA])
        # The orphan holds only the history files, nothing from the source branch.
        tree = git_out(["--git-dir", str(self.origin), "ls-tree", "--name-only", "metrics"], self.tmp).split()
        self.assertEqual(sorted(tree), ["README.md", "metrics.jsonl"])
        self.assertEqual(self.remote_log(), ["metrics: %s|github-actions[bot]" % SHA[:7]])

        # A second job (fresh worktree, branch present) appends.
        self.assertEqual(self.publish(self.source, "w2", SHA2), "pushed")
        self.assertEqual(metrics_append.read_shas(self.remote_file("metrics.jsonl")), [SHA, SHA2])
        # No worktree holds a local branch named like the remote one: a newer
        # git refuses to check out a branch a second worktree already has.
        self.assertEqual(git_out(["for-each-ref", "refs/heads/metrics"], self.source), "")
        # A re-run for a sha already there pushes nothing.
        self.assertEqual(self.publish(self.source, "w3", SHA2), "present")
        self.assertEqual(len(self.remote_log()), 2)
        # The source branch is untouched.
        self.assertEqual(git_out(["--git-dir", str(self.origin), "ls-tree", "--name-only", "main"], self.tmp).split(),
                         ["code.txt"])

    def test_a_rejected_push_is_retried_on_the_new_tip(self):
        self.publish(self.source, "w1", SHA)
        other = self.clone("other")

        def race(attempt):
            if attempt == 1:
                self.publish(other, "w-other", SHA3)

        self.assertEqual(self.publish(self.source, "w2", SHA2, before_push=race), "pushed")
        self.assertEqual(metrics_append.read_shas(self.remote_file("metrics.jsonl")), [SHA, SHA3, SHA2])
        self.assertTrue(any("rejected" in line for line in self.logs), self.logs)

    def test_a_creation_race_lands_on_the_winner(self):
        other = self.clone("other")

        def race(attempt):
            if attempt == 1:
                self.publish(other, "w-other", SHA3)

        self.assertEqual(self.publish(self.source, "w1", SHA, before_push=race), "pushed")
        self.assertEqual(metrics_append.read_shas(self.remote_file("metrics.jsonl")), [SHA3, SHA])
        self.assertEqual(len(self.remote_log()), 2)

    def test_gives_up_after_the_attempts(self):
        self.publish(self.source, "w1", SHA)
        other = self.clone("other")
        shas = iter(["2" * 40, "3" * 40, "4" * 40])

        def always(attempt):
            self.publish(other, "w-other-%d" % attempt, next(shas))

        with self.assertRaises(RuntimeError):
            self.publish(self.source, "w2", SHA2, attempts=3, before_push=always)
        self.assertNotIn(SHA2, metrics_append.read_shas(self.remote_file("metrics.jsonl")))


# --- the CI wiring ----------------------------------------------------------------


def workflow_jobs(text):
    """{job name: its block of lines} for the `jobs:` map, comments dropped.

    Stdlib only: a job is a key indented two spaces under `jobs:`, and its
    block is every line until the next such key."""
    lines = [line for line in text.splitlines() if not line.lstrip().startswith("#")]
    jobs, current, inside = {}, None, False
    for line in lines:
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        if indent == 0:
            inside = line.rstrip() == "jobs:"
            current = None
            continue
        if inside and indent == 2 and line.rstrip().endswith(":"):
            current = line.strip()[:-1]
            jobs[current] = []
            continue
        if inside and current is not None:
            jobs[current].append(line)
    return {name: "\n".join(block) + "\n" for name, block in jobs.items()}


def job_keys(block):
    """The job's own keys (indent 4) and their inline values."""
    keys = {}
    for line in block.splitlines():
        if len(line) - len(line.lstrip()) == 4 and ":" in line:
            key, _sep, value = line.strip().partition(":")
            keys[key] = value.strip()
    return keys


def job_permissions(block):
    """{scope: access} under the job's `permissions:` key."""
    found, inside = {}, False
    for line in block.splitlines():
        indent = len(line) - len(line.lstrip())
        if indent == 4:
            inside = line.strip() == "permissions:"
            continue
        if inside and indent == 6 and ":" in line:
            key, _sep, value = line.strip().partition(":")
            found[key] = value.strip()
    return found


MAIN_PUSH = "github.event_name == 'push' && github.ref == 'refs/heads/main'"
ROW_ARTIFACT = "metrics-row"


def metrics_wiring_problems(text):
    """Why the workflow would not build, upload and append the row safely; empty when it does."""
    problems = []
    jobs = workflow_jobs(text)
    if "tests" not in jobs:
        return ["no tests job"]
    top = [line for line in text.splitlines() if line.startswith("permissions:") or line.startswith("  contents:")]
    if top[:2] != ["permissions:", "  contents: read"]:
        problems.append("the workflow-level permissions are not contents: read")
    for name, block in jobs.items():
        writes = [line.strip() for line in block.splitlines() if line.strip().endswith(": write")]
        if writes and name != "metrics":
            problems.append("job %s asks for write permission (%s)" % (name, ", ".join(writes)))
    steps = test_semantic.workflow_steps(jobs["tests"])
    build = [i for i, (_n, _k, _e, run) in enumerate(steps) if "scripts/cloud-test.sh" in run]
    rows = [i for i, (_n, _k, _e, run) in enumerate(steps) if "scripts/arch/metrics.py" in run]
    if not rows:
        problems.append("the tests job does not run scripts/arch/metrics.py")
    else:
        name, keys, _env, run = steps[rows[0]]
        if build and rows[0] < build[0]:
            problems.append("%s: builds the row before the DLL is built" % name)
        # Main pushes only (a pull request keeps no row), and it may never
        # turn the required check red: the history gates nothing.
        if keys.get("if") != MAIN_PUSH:
            problems.append("%s: runs on %r, not only on a push to main" % (name, keys.get("if")))
        if keys.get("continue-on-error", "false").lower() != "true":
            problems.append("%s: is not continue-on-error, so it can red the required tests job" % name)
        if "--dll" not in run or "--tests-dll" not in run:
            problems.append("%s: does not measure both DLLs" % name)
    uploads = [i for i, (_n, keys, _e, _r) in enumerate(steps)
               if keys.get("uses", "").startswith("actions/upload-artifact@")]
    if not uploads or ("name: %s" % ROW_ARTIFACT) not in jobs["tests"]:
        problems.append("the tests job does not upload the %s artifact" % ROW_ARTIFACT)
    else:
        name, keys, _env, _run = steps[uploads[-1]]
        if rows and uploads[-1] < rows[0]:
            problems.append("the upload runs before the row is built")
        row_id = steps[rows[0]][1].get("id") if rows else None
        condition = keys.get("if", "")
        if not condition.startswith(MAIN_PUSH) or not row_id or "steps.%s.outcome == 'success'" % row_id not in condition:
            problems.append("%s: not limited to a main push whose row was built (if: %r)" % (name, condition))
        if keys.get("continue-on-error", "false").lower() != "true":
            problems.append("%s: is not continue-on-error, so it can red the required tests job" % name)
    # A pull request's only metrics check: the compiled-IL step runs this
    # file's real-row cells over the parse it already holds.
    il_runs = [run for _n, _k, env, run in steps if IL_ENV in env and "scripts/arch" in run]
    patterns = [m.group(1) for run in il_runs for m in [re.search(r"-p\s+['\"]?([^'\"\s]+)", run)] if m]
    if not any(fnmatch.fnmatch("test_metrics.py", p) for p in patterns):
        problems.append("the compiled-IL step does not discover test_metrics.py (patterns %r)" % patterns)
    if "metrics" not in jobs:
        problems.append("no metrics job")
        return problems
    block = jobs["metrics"]
    keys = job_keys(block)
    if keys.get("needs") != "tests":
        problems.append("metrics: needs is %r, not tests" % keys.get("needs"))
    if keys.get("if") != MAIN_PUSH:
        problems.append("metrics: runs on %r, not only on a push to main" % keys.get("if"))
    if job_permissions(block) != {"contents": "write"}:
        problems.append("metrics: permissions are %r, not contents: write alone" % job_permissions(block))
    if "concurrency" not in keys or "cancel-in-progress: false" not in block:
        problems.append("metrics: no concurrency group that keeps a running append")
    if "timeout-minutes" not in keys:
        problems.append("metrics: no timeout")
    if keys.get("continue-on-error", "false").lower() != "true":
        problems.append("metrics: is not continue-on-error, so a failed append reds the run and the README badge")
    if "name: %s" % ROW_ARTIFACT not in block or "actions/download-artifact@" not in block:
        problems.append("metrics: does not download the %s artifact" % ROW_ARTIFACT)
    if "scripts/arch/metrics_append.py publish" not in block:
        problems.append("metrics: does not run metrics_append.py publish")
    for line in text.splitlines():
        stripped = line.strip().lstrip("- ")
        if stripped.startswith("uses:"):
            action = stripped.split(":", 1)[1].strip()
            if action.split("@")[0] not in ("actions/checkout", "actions/cache", "actions/upload-artifact",
                                            "actions/download-artifact"):
                problems.append("third-party action %s" % action)
    return problems


def yaml_problems(text):
    """The same contract read through PyYAML, when it is installed (CI does not promise it)."""
    import yaml  # noqa: PLC0415

    doc = yaml.safe_load(text)
    jobs = doc["jobs"]
    problems = []
    if doc.get("permissions") != {"contents": "read"}:
        problems.append("workflow permissions %r" % doc.get("permissions"))
    if "permissions" in jobs["tests"]:
        problems.append("tests job sets its own permissions")
    metrics_job = jobs.get("metrics") or {}
    if metrics_job.get("if") != MAIN_PUSH or metrics_job.get("needs") != "tests":
        problems.append("metrics job trigger %r / needs %r" % (metrics_job.get("if"), metrics_job.get("needs")))
    if metrics_job.get("permissions") != {"contents": "write"}:
        problems.append("metrics job permissions %r" % metrics_job.get("permissions"))
    if (metrics_job.get("concurrency") or {}).get("cancel-in-progress") is not False:
        problems.append("metrics job concurrency %r" % metrics_job.get("concurrency"))
    if metrics_job.get("continue-on-error") is not True:
        problems.append("metrics job continue-on-error %r" % metrics_job.get("continue-on-error"))
    steps = jobs["tests"]["steps"]
    rows = [s for s in steps if "scripts/arch/metrics.py" in str(s.get("run", ""))]
    uploads = [s for s in steps if str(s.get("uses", "")).startswith("actions/upload-artifact@")
               and (s.get("with") or {}).get("name") == ROW_ARTIFACT]
    if not rows or not uploads:
        problems.append("tests job builds or uploads no %s" % ROW_ARTIFACT)
        return problems
    row, upload = rows[0], uploads[0]
    if row.get("if") != MAIN_PUSH or row.get("continue-on-error") is not True:
        problems.append("row step if %r continue-on-error %r" % (row.get("if"), row.get("continue-on-error")))
    expected_upload_if = "%s && steps.%s.outcome == 'success'" % (MAIN_PUSH, row.get("id"))
    if upload.get("if") != expected_upload_if or upload.get("continue-on-error") is not True:
        problems.append("upload step if %r continue-on-error %r" % (upload.get("if"), upload.get("continue-on-error")))
    return problems


class WorkflowTests(unittest.TestCase):
    def test_the_workflow_builds_uploads_and_appends_the_row(self):
        self.assertEqual(metrics_wiring_problems(WORKFLOW.read_text(encoding="utf-8")), [])

    def test_with_pyyaml_when_installed(self):
        try:
            import yaml  # noqa: F401, PLC0415
        except ImportError:
            self.skipTest("PyYAML is not installed (the stdlib reader above holds the contract)")
        self.assertEqual(yaml_problems(WORKFLOW.read_text(encoding="utf-8")), [])

    def test_the_reader_reds_every_way_to_widen_or_lose_the_job(self):
        good = WORKFLOW.read_text(encoding="utf-8")
        self.assertEqual(metrics_wiring_problems(good), [])
        mutants = {
            "pull requests too": good.replace("    if: " + MAIN_PUSH, "    if: github.event_name != 'schedule'"),
            "write on the tests job": good.replace("  tests:\n", "  tests:\n    permissions:\n      contents: write\n", 1),
            "write for the workflow": good.replace("permissions:\n  contents: read", "permissions:\n  contents: write", 1),
            "no upload": good.replace("name: " + ROW_ARTIFACT, "name: something-else"),
            "cancel a running append": good.replace("      cancel-in-progress: false", "      cancel-in-progress: true"),
            "no needs": good.replace("    needs: tests\n", ""),
            "a failed append reds the run": good.replace("    timeout-minutes: 10\n    continue-on-error: true\n",
                                                         "    timeout-minutes: 10\n"),
            "no row step": good.replace("scripts/arch/metrics.py", "scripts/arch/other.py"),
            "row on pull requests": good.replace(
                "        id: metrics_row\n        if: " + MAIN_PUSH + "\n", "        id: metrics_row\n"),
            "row can red tests": good.replace(
                "        id: metrics_row\n        if: " + MAIN_PUSH + "\n        continue-on-error: true\n",
                "        id: metrics_row\n        if: " + MAIN_PUSH + "\n"),
            "upload can red tests": good.replace(
                "steps.metrics_row.outcome == 'success'\n        continue-on-error: true\n",
                "steps.metrics_row.outcome == 'success'\n"),
            "upload with no row": good.replace(" && steps.metrics_row.outcome == 'success'", ""),
            "no proof on pull requests": good.replace("-p 'test_[ims]*.py'", "-p 'test_[si]*.py'"),
            "third-party action": good.replace("uses: actions/download-artifact@", "uses: someone/download@", 1),
        }
        try:
            import yaml  # noqa: F401, PLC0415
        except ImportError:
            yaml = None
        for label, text in mutants.items():
            self.assertNotEqual(text, good, label)
            self.assertNotEqual(metrics_wiring_problems(text), [], label)
            if yaml is not None and label not in ("no row step", "third-party action", "no proof on pull requests"):
                self.assertNotEqual(yaml_problems(text), [], label)

    def test_the_job_reader(self):
        text = (
            "on: push\njobs:\n  a:\n    runs-on: x\n    # if: false\n    permissions:\n      contents: write\n"
            "  b:\n    needs: a\n    if: always()\n"
        )
        jobs = workflow_jobs(text)
        self.assertEqual(sorted(jobs), ["a", "b"])
        self.assertEqual(job_permissions(jobs["a"]), {"contents": "write"})
        self.assertEqual(job_keys(jobs["b"]), {"needs": "a", "if": "always()"})
        self.assertNotIn("if", job_keys(jobs["a"]))


@unittest.skipUnless(os.environ.get(IL_ENV), "%s is not set (a real row needs the built DLL)" % IL_ENV)
class RealTreeRowTests(unittest.TestCase):
    """A full row over the built Debug DLL, from the text model, semantic
    result and facts the compiled-IL step's other real-tree cells hold in the
    same process (the per-process caches in test_semantic and test_ilfacts), so
    this costs the size counts and the analysis report, not another parse. A
    pull request's CI runs this; only a main push writes a row."""

    @classmethod
    def setUpClass(cls):
        import ilanalysis  # noqa: PLC0415
        import test_ilfacts  # noqa: PLC0415

        il_path = Path(os.environ[IL_ENV])
        if not il_path.is_file():
            raise AssertionError("%s=%s does not exist (build Parsek first)" % (IL_ENV, il_path))
        model, result = test_semantic.real_tree_semantic(il_path)
        facts, tooling = test_ilfacts.real_tree_facts(il_path)
        _rules, _tooling, forbidden, _allowed = metrics.archview.load_rules(metrics.archview.DEFAULT_MODULES)
        import layers  # noqa: PLC0415

        import lifetimes  # noqa: PLC0415
        import test_ilanalysis  # noqa: PLC0415
        import test_static_lifetimes  # noqa: PLC0415

        # The cached report and lifetime result the other real-tree cells hold; the
        # row reads them, so a shallow copy keeps the cache untouched.
        report = dict(test_ilanalysis.real_tree_report(il_path))
        report["lifetimes"] = lifetimes.summary(test_static_lifetimes.real_tree_lifetimes(il_path))
        cls.lifetime_pins = lifetimes.load_pins()
        cls.pins = layers.load_pins()
        row = metrics.row_from_tools(SHA, "2026-11-11T17:42:00Z", model, forbidden, result, report,
                                     metrics.load_count_code().collect(), layer_table=layers.load_layers())
        cls.line = metrics.dumps(row)
        cls.row = json.loads(cls.line)

    def test_every_half_is_measured(self):
        row = self.row
        self.assertEqual(row["measured"], {"text": True, "sizes": True, "il": True, "testsDll": False,
                                           "history": False})
        for half in ("text", "semantic", "facts", "sizes"):
            self.assertIsInstance(row[half], dict, half)

    def test_the_numbers_are_the_tools_numbers(self):
        row = self.row
        self.assertEqual(row["text"]["unclassifiedFiles"], 0)
        self.assertEqual(row["text"]["knot"], row["text"]["knots"][0])
        self.assertEqual(row["semantic"]["text"]["knot"], row["text"]["knot"])
        self.assertEqual(row["semantic"]["text"]["replayedResidue"], row["text"]["replayedResidue"])
        self.assertEqual(set(row["text"]["byModule"]), set(row["facts"]["coupling"]["byModule"]))
        self.assertEqual(sum(m["files"] for m in row["text"]["byModule"].values()), row["text"]["productionFiles"])
        self.assertGreater(row["text"]["productionLines"], 0)
        self.assertGreater(row["facts"]["ratchets"]["uncalledInAssembly"], 0)
        self.assertIn("Mod source (C#)", row["sizes"]["areas"])
        # The semantic layer numbers are what layer_pins.toml ratchets: never above the pins.
        sem_layers = row["semantic"]["layers"]
        self.assertLessEqual(sem_layers["backwardModulePairs"], len(self.pins["backward"]))
        self.assertLessEqual(sem_layers["backwardTypePairs"], sum(e["count"] for e in self.pins["backward"]))
        self.assertGreater(row["text"]["layers"]["lateralModulePairs"], 0)
        # The lifetime counts are what lifetime_pins.toml ratchets: never above the pins.
        lifetimes = row["facts"]["lifetimes"]
        self.assertEqual(lifetimes["tagged"] + lifetimes["untagged"], lifetimes["population"])
        self.assertLessEqual(lifetimes["untagged"], len(self.lifetime_pins["untagged"]["keys"]))
        for name, count in lifetimes["findings"].items():
            self.assertIsNotNone(count, name)
            self.assertLessEqual(count, self.lifetime_pins[name]["bound"], name)

    def test_the_headless_counts_are_what_the_pins_hold(self):
        import headless  # noqa: PLC0415

        section = self.row["facts"]["headless"]
        pins = headless.load_pins()
        self.assertEqual(section["modules"], sorted(entry["name"] for entry in pins["module"]))
        self.assertLessEqual(section["leaks"], sum(len(entry["leaks"]) for entry in pins["module"]))
        self.assertTrue(all(section["byModule"][m]["headless"] for m in section["modules"]))

    def test_the_row_appends(self):
        text, appended = metrics_append.append_text("", self.row)
        self.assertTrue(appended)
        self.assertEqual(text, self.line)


if __name__ == "__main__":
    unittest.main()
