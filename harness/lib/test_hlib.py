"""Unit tests for hlib.py, the pure decision logic of the M-A5 harness.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Each test names the regression it guards (design Test Plan). Fixtures are the
REAL on-disk registry + sample specs where a placement/parse bug could only be
caught against a real file (mirroring test_provlib.py's RealProfileFileTests).
"""

import ast
import contextlib
import copy
import glob
import inspect
import io
import os
import re
import shutil
import sys
import tempfile
import textwrap
import tomllib
import unittest

import ghostlife
import hlib
import rendercompose
import saveparse


HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
REGISTRY_PATH = os.path.join(HARNESS_ROOT, "coverage", "registry.toml")
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")
MISSIONS_DIR = os.path.join(HARNESS_ROOT, "missions")
# The mod assembly the in-game test runner reflects over. DiscoverTests scans the
# WHOLE executing assembly, so the sweep walks the whole project rather than just
# InGameTests/ -- an [InGameTest] method that lands elsewhere in Parsek.dll counts
# toward a category total exactly the same, and must not become invisible here.
PARSEK_SOURCE_DIR = os.path.join(REPO_ROOT, "Source", "Parsek")

# The committed-spec round-trip test (BLOCKER-1 regression) drives the REAL run.py
# admission path (run.resolve_mission_schemas), so import the orchestrator. run.py
# is stdlib + hlib/provlib only and has no import-time side effects beyond the
# sys.path bootstrap it does for its own siblings.
if HARNESS_ROOT not in sys.path:
    sys.path.insert(0, HARNESS_ROOT)
import run  # noqa: E402


def load_registry():
    with open(REGISTRY_PATH, "rb") as fh:
        return tomllib.load(fh)


def load_spec(name):
    with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
        return tomllib.load(fh)


def walk_parsek_sources():
    """(relpath, text) for every .cs file in the mod assembly that mentions
    InGameTest.

    The ONLY file I/O the batch-tally sync gate needs; every decision it then
    makes is a pure hlib call. bin/obj are pruned so a generated or stale copy of
    a source file cannot double-count a category.
    """
    for dirpath, dirnames, filenames in os.walk(PARSEK_SOURCE_DIR):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for fn in sorted(filenames):
            if not fn.endswith(".cs"):
                continue
            path = os.path.join(dirpath, fn)
            with open(path, "r", encoding="utf-8") as fh:
                text = fh.read()
            if "InGameTest" not in text:
                continue
            yield os.path.relpath(path, REPO_ROOT).replace("\\", "/"), text


def load_ingame_test_declarations():
    """Every [InGameTest] declaration in the mod assembly's source tree."""
    decls = []
    for rel, text in walk_parsek_sources():
        decls.extend(hlib.parse_ingame_test_declarations(text, rel))
    return decls


def load_unclaimed_ingame_attribute_tokens():
    """Every attribute-position InGameTest spelling the strict parse did not claim
    (the RECOGNITION-completeness half of the gate)."""
    out = []
    for rel, text in walk_parsek_sources():
        out.extend(hlib.unclaimed_ingame_attribute_tokens(text, rel))
    return out


def batch_owning_specs():
    """(name, spec, selector) for every committed spec that OWNS a batch.

    Ownership mirrors hlib.validate_spec exactly: a RunTests step XOR an
    [driver.autorun] block (hlib.py's "Exactly one BATCH owner" rule), with the
    same selector resolution -- the FIRST RunTests category, else autorun.tests.
    Matching only cmd == "RunTests" would leave an autorun-owned spec carrying a
    pinned tally that the anti-vacuity rule validates and this one never sees.
    Discovered, never hardcoded, so a scenario added later is gated automatically.
    """
    out = []
    for name in sorted(os.listdir(SCENARIOS_DIR)):
        if not name.endswith(".toml"):
            continue
        spec = load_spec(name)
        driver = spec.get("driver", {}) or {}
        steps = driver.get("steps", []) or []
        run_tests = [s for s in steps if (s or {}).get("cmd") == "RunTests"]
        autorun_tests = (driver.get("autorun", {}) or {}).get("tests")
        if not run_tests and not autorun_tests:
            continue
        selector = (((run_tests[0].get("args", {}) or {}).get("category"))
                    if run_tests else autorun_tests)
        out.append((name, spec, selector))
    return out


# ---------------------------------------------------------------------------
# Line parsers.
# ---------------------------------------------------------------------------


class BatchCompleteParserTests(unittest.TestCase):
    """Guards: a v1 harness must read the frozen M-A3 tally AND reject a future
    v2 line (never silently misparse it as v1)."""

    def test_parses_v1_line_with_prefix(self):
        line = ("[Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=12 passed=12 "
                "failed=0 skipped=0 category=RecordingInvariants scene=FLIGHT")
        bc = hlib.parse_batch_complete_line(line)
        self.assertIsNotNone(bc)
        self.assertEqual((bc.total, bc.passed, bc.failed, bc.skipped), (12, 12, 0, 0))
        self.assertEqual(bc.category, "RecordingInvariants")
        self.assertEqual(bc.scene, "FLIGHT")

    def test_rejects_v2_line(self):
        # A v2 bump MUST NOT parse as v1 (contract guard).
        line = "BATCH_COMPLETE v2 total=1 passed=1 failed=0 skipped=0 category=X scene=FLIGHT"
        self.assertIsNone(hlib.parse_batch_complete_line(line))

    def test_non_batch_line_is_none(self):
        self.assertIsNone(hlib.parse_batch_complete_line("just a log line"))

    def test_select_by_category_and_scene(self):
        text = "\n".join([
            "BATCH_COMPLETE v1 total=3 passed=3 failed=0 skipped=0 category=A scene=FLIGHT",
            "BATCH_COMPLETE v1 total=2 passed=1 failed=1 skipped=0 category=B scene=FLIGHT",
        ])
        batches = hlib.find_batch_complete_lines(text)
        self.assertEqual(len(batches), 2)
        self.assertEqual(hlib.select_batch_complete(batches, "B", "FLIGHT").failed, 1)
        self.assertIsNone(hlib.select_batch_complete(batches, "C"))


def _bc_line(category, failed, total=5, passed=None, skipped=0, scene="FLIGHT"):
    passed = (total - failed - skipped) if passed is None else passed
    return ("BATCH_COMPLETE v1 total=%d passed=%d failed=%d skipped=%d category=%s scene=%s"
            % (total, passed, failed, skipped, category, scene))


class MultiCategoryBatchCompleteTests(unittest.TestCase):
    """M-A5.1 (N3): a multi-category selector ("all" / "A,B") emits per-category
    lines PLUS a category=multi:<count> aggregate; resolve_batch_complete gates on
    the aggregate union (failed==0 => ALL categories passed) and flags a missing
    aggregate with per-category lines present as a defined fault. Regressions guarded:
    (1) a truncated multi-category run reading green off one per-category line;
    (2) a mis-summarized aggregate (multi failed=0 while a category shows failures)
    reading green; (3) a single-category selector regressing off the v1 exact match."""

    def test_selector_multi_detection(self):
        self.assertTrue(hlib.is_multi_category_selector("all"))
        self.assertTrue(hlib.is_multi_category_selector("A,B"))
        self.assertTrue(hlib.is_multi_category_selector("  A, B "))
        self.assertFalse(hlib.is_multi_category_selector("RecordingInvariants"))
        self.assertFalse(hlib.is_multi_category_selector(""))
        self.assertFalse(hlib.is_multi_category_selector(None))

    def test_single_category_unchanged_v1_exact_match(self):
        # A single-category selector still resolves the exact per-category line (v1).
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 2)]))
        sel = hlib.resolve_batch_complete(batches, "B")
        self.assertTrue(sel.present)
        self.assertFalse(sel.multi)
        self.assertEqual(sel.failed, 2)
        self.assertEqual(sel.category, "B")

    def test_multi_aggregate_all_passed(self):
        # Per-category lines all failed=0 + a multi:2 aggregate failed=0 -> ALL passed.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 0),
            _bc_line("multi:2", 0, total=10, passed=10)]))
        sel = hlib.resolve_batch_complete(batches, "A,B")
        self.assertTrue(sel.present)
        self.assertTrue(sel.multi)
        self.assertFalse(sel.aggregate_missing)
        self.assertEqual(sel.failed, 0)
        self.assertEqual(sel.per_category_count, 2)

    def test_multi_aggregate_union_reports_failure(self):
        # The aggregate's union failed>0 gates: not an all-passed run.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 3),
            _bc_line("multi:2", 3, total=10, passed=7)]))
        sel = hlib.resolve_batch_complete(batches, "all")
        self.assertTrue(sel.present)
        self.assertEqual(sel.failed, 3)

    def test_mis_summarized_aggregate_cannot_hide_category_failure(self):
        # A mis-summarized aggregate under-reports (multi failed=0) while a per-category
        # line shows 3 failures -> the gating failed is the MAX (union), never 0.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 3),
            _bc_line("multi:2", 0, total=10, passed=10)]))
        sel = hlib.resolve_batch_complete(batches, "A,B")
        self.assertTrue(sel.present)
        self.assertEqual(sel.failed, 3, "failed==0 must never hide a category that reported failures")

    def test_missing_aggregate_with_per_category_lines_is_defined_fault(self):
        # Per-category lines present but NO multi:<n> aggregate -> defined fault, NOT a
        # silent pass off a per-category line (the truncated-run regression).
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 0)]))
        sel = hlib.resolve_batch_complete(batches, "A,B")
        self.assertFalse(sel.present)
        self.assertTrue(sel.aggregate_missing)
        self.assertEqual(sel.per_category_count, 2)

    def test_two_aggregates_is_defined_fault(self):
        # Item 10: two category=multi:<n> aggregate lines (the summary emitted twice) is a
        # defined fault -> present=False + duplicate_aggregate, never a silent first-wins.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 0),
            _bc_line("multi:2", 0, total=10, passed=10),
            _bc_line("multi:2", 0, total=10, passed=10)]))
        sel = hlib.resolve_batch_complete(batches, "A,B")
        self.assertFalse(sel.present)
        self.assertTrue(sel.duplicate_aggregate)
        self.assertFalse(sel.aggregate_missing)

    def test_multi_selector_no_lines_at_all_is_plain_absent(self):
        # No BATCH_COMPLETE lines at all: batch never started (not the aggregate-missing
        # defined fault, which requires per-category lines present).
        sel = hlib.resolve_batch_complete([], "all")
        self.assertFalse(sel.present)
        self.assertFalse(sel.aggregate_missing)
        self.assertEqual(sel.per_category_count, 0)

    def test_select_aggregate_helper(self):
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("multi:1", 0)]))
        agg = hlib.select_aggregate_batch_complete(batches)
        self.assertIsNotNone(agg)
        self.assertEqual(agg.category, "multi:1")
        # A category literally shaped like a real name is never mistaken for aggregate.
        self.assertIsNone(hlib.select_aggregate_batch_complete(
            hlib.find_batch_complete_lines(_bc_line("RecordingInvariants", 0))))

    # --- SF2: cross-check the aggregate's multi:<count> against the per-category
    # line count (the regex count group v1 parsed but never read / NIT 2). ---

    def test_aggregate_count_reader_un_deadens_the_group(self):
        # aggregate_category_count reads the previously-dead regex group; a non-aggregate
        # yields None.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("multi:3", 0), _bc_line("A", 0)]))
        agg = hlib.select_aggregate_batch_complete(batches)
        self.assertEqual(hlib.aggregate_category_count(agg), 3)
        non_agg = hlib.find_batch_complete_lines(_bc_line("A", 0))[0]
        self.assertIsNone(hlib.aggregate_category_count(non_agg))

    def test_aggregate_count_equals_lines_passes(self):
        # multi:2 aggregate + exactly 2 per-category lines -> present, no mismatch.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 0),
            _bc_line("multi:2", 0, total=10, passed=10)]))
        sel = hlib.resolve_batch_complete(batches, "A,B")
        self.assertTrue(sel.present)
        self.assertFalse(sel.category_count_mismatch)
        self.assertEqual(sel.expected_category_count, 2)
        self.assertEqual(sel.per_category_count, 2)

    def test_aggregate_count_exceeds_lines_is_mismatch(self):
        # N > lines: the aggregate claims 3 categories but only 2 per-category lines are
        # present (a category batch cut off before its BATCH_COMPLETE) -> defined fault.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 0),
            _bc_line("multi:3", 0, total=10, passed=10)]))
        sel = hlib.resolve_batch_complete(batches, "all")
        self.assertFalse(sel.present, "an N>lines aggregate must NOT read as an all-passed run")
        self.assertTrue(sel.category_count_mismatch)
        self.assertFalse(sel.aggregate_missing)
        self.assertEqual(sel.expected_category_count, 3)
        self.assertEqual(sel.per_category_count, 2)

    def test_aggregate_count_below_lines_is_mismatch(self):
        # N < lines (documented choice: STRICT EQUALITY, so this also reds): the aggregate
        # claims 1 category but 2 per-category lines are present (an unexpected extra
        # batch) -> defined fault, never a silent pass off the mis-counted aggregate.
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("B", 0),
            _bc_line("multi:1", 0, total=10, passed=10)]))
        sel = hlib.resolve_batch_complete(batches, "A,B")
        self.assertFalse(sel.present)
        self.assertTrue(sel.category_count_mismatch)
        self.assertEqual(sel.expected_category_count, 1)
        self.assertEqual(sel.per_category_count, 2)

    def test_count_mismatch_reds_even_when_aggregate_failed_zero(self):
        # The mismatch is orthogonal to the union failed count: a failed=0 aggregate whose
        # count disagrees with the per-category stream still reds (never a silent pass).
        batches = hlib.find_batch_complete_lines("\n".join([
            _bc_line("A", 0), _bc_line("multi:2", 0, total=5, passed=5)]))
        sel = hlib.resolve_batch_complete(batches, "A,B")
        self.assertFalse(sel.present)
        self.assertTrue(sel.category_count_mismatch)
        self.assertIsNone(sel.failed)


class RetryScopeClassifierTests(unittest.TestCase):
    """M-A5.1: classify_retry_scope routes a verifier-stage outcome to a subprocess
    retry / whole-attempt fallback / no retry. Regressions guarded: (1) a Parsek
    VERDICT (analyzer RED=1, log-contract FAIL) must NEVER be re-run (is_tooling_fault
    False -> NONE); (2) a wedged analyzer/log-validate subprocess re-runs over the same
    artifacts (SUBPROCESS); (3) a tooling fault on a non-re-runnable stage falls back to
    the whole-attempt retry."""

    def test_verdict_is_never_retried(self):
        # A Parsek verdict passes is_tooling_fault=False -> NONE, regardless of stage.
        self.assertEqual(hlib.RETRY_SCOPE_NONE,
                         hlib.classify_retry_scope("analyzer", False, "analyzer"))
        self.assertEqual(hlib.RETRY_SCOPE_NONE,
                         hlib.classify_retry_scope("logValidate", False, ""))

    def test_analyzer_tooling_and_crash_are_subprocess(self):
        # analyzer subprocess timeout (tooling) + analyzer crash (analyzer-error/no
        # gate token) both re-run over the same save.
        self.assertEqual(hlib.RETRY_SCOPE_SUBPROCESS,
                         hlib.classify_retry_scope("analyzer", True, "tooling"))
        self.assertEqual(hlib.RETRY_SCOPE_SUBPROCESS,
                         hlib.classify_retry_scope("analyzer", True, "analyzer-error"))

    def test_log_validate_timeout_is_subprocess(self):
        self.assertEqual(hlib.RETRY_SCOPE_SUBPROCESS,
                         hlib.classify_retry_scope("logValidate", True, "tooling"))

    def test_analyzer_fixture_faults_are_not_subprocess(self):
        # Deterministic fixture faults are not subprocess flakes -> whole-attempt (and
        # the taxonomy then treats them terminal); re-running the subprocess won't help.
        for sk in ("fixture-authoring", "fixture-stale"):
            self.assertEqual(hlib.RETRY_SCOPE_WHOLE_ATTEMPT,
                             hlib.classify_retry_scope("analyzer", True, sk), sk)

    def test_non_rerunnable_stage_tooling_is_whole_attempt(self):
        # A tooling fault on a stage that is not one of the two re-runnable shell
        # scripts (e.g. the ledger careerSave read) falls back to the whole attempt.
        self.assertEqual(hlib.RETRY_SCOPE_WHOLE_ATTEMPT,
                         hlib.classify_retry_scope("ledgerOracle", True, "tooling"))


class RedTokenParserTests(unittest.TestCase):
    """Guards the single most dangerous silent pass: an absent RED token must
    read as None (analyzer-error), NEVER RED=0; and an earlier literal 'RED=0'
    in a save leaf must never spoof the terminal gate token."""

    def test_terminal_red_zero(self):
        txt = "[Analyzer] save=persistent generation=4 FAIL=0 WARN=1 INFO=2 STALE=0 BASELINED=0 RED=0\n"
        self.assertEqual(hlib.parse_analysis_red_token(txt), 0)

    def test_terminal_red_one(self):
        txt = "[Analyzer] save=x generation=4 FAIL=2 WARN=0 INFO=0 STALE=0 BASELINED=0 RED=1\n"
        self.assertEqual(hlib.parse_analysis_red_token(txt), 1)

    def test_absent_red_is_none_not_zero(self):
        txt = "[Analyzer] save=x generation=4 FAIL=0 WARN=0 INFO=0 STALE=0 BASELINED=0\n"
        self.assertIsNone(hlib.parse_analysis_red_token(txt))

    def test_no_header_is_none(self):
        self.assertIsNone(hlib.parse_analysis_red_token("no analyzer header here\n"))

    def test_earlier_literal_does_not_spoof_gate(self):
        # A save named "...RED=0" appears earlier on the line; the terminal token is RED=1.
        txt = "[Analyzer] save=probe-RED=0-leaf generation=4 FAIL=1 WARN=0 INFO=0 STALE=0 BASELINED=0 RED=1\n"
        self.assertEqual(hlib.parse_analysis_red_token(txt), 1)


class AnalysisJsonParserTests(unittest.TestCase):
    """Guards S1/S2: the FAIL-vs-STALE split is JSON-only (never the txt header),
    and BASELINE-* FAILs must be separable from REAL FAILs so a real defect never
    hides behind a fixture-authoring FAIL and vice-versa."""

    def _json(self, fnb, snb, findings):
        return {
            "counts": {"failNonBaselined": fnb, "staleNonBaselined": snb},
            "findings": findings,
        }

    def test_reads_split_and_findings(self):
        obj = self._json(2, 0, [
            {"ruleId": "INV2-NO-DOUBLE-COVER", "level": "FAIL", "target": "rec", "baselined": False},
        ])
        aj = hlib.parse_analysis_json(obj)
        self.assertEqual((aj.fail_non_baselined, aj.stale_non_baselined), (2, 0))
        self.assertEqual(len(aj.non_baseline_fail_findings()), 1)
        self.assertEqual(len(aj.baseline_fail_findings()), 0)

    def test_baseline_forbidden_only(self):
        obj = self._json(1, 0, [
            {"ruleId": "BASELINE-FORBIDDEN", "level": "FAIL", "target": "baseline.cfg", "baselined": False},
        ])
        aj = hlib.parse_analysis_json(obj)
        self.assertEqual(len(aj.non_baseline_fail_findings()), 0)
        self.assertEqual(len(aj.baseline_fail_findings()), 1)

    def test_real_fail_wins_over_baseline(self):
        obj = self._json(2, 0, [
            {"ruleId": "BASELINE-FORBIDDEN", "level": "FAIL", "target": "baseline.cfg", "baselined": False},
            {"ruleId": "INV3-ABSOLUTE-RANGE", "level": "FAIL", "target": "rec", "baselined": False},
        ])
        aj = hlib.parse_analysis_json(obj)
        self.assertEqual(len(aj.non_baseline_fail_findings()), 1)
        self.assertEqual(aj.non_baseline_fail_findings()[0].rule_id, "INV3-ABSOLUTE-RANGE")

    def test_baselined_finding_is_not_a_real_fail(self):
        obj = self._json(0, 0, [
            {"ruleId": "INV2-NO-DOUBLE-COVER", "level": "FAIL", "target": "rec", "baselined": True},
        ])
        aj = hlib.parse_analysis_json(obj)
        self.assertEqual(len(aj.non_baseline_fail_findings()), 0)

    def test_parse_string_json(self):
        import json
        aj = hlib.parse_analysis_json(json.dumps(self._json(0, 3, [])))
        self.assertEqual(aj.stale_non_baselined, 3)

    def test_bad_json_is_none(self):
        self.assertIsNone(hlib.parse_analysis_json("{not json"))


class ResultsFailureParserTests(unittest.TestCase):
    """Guards: a FAILURES-block row counts once and the padded 'FAILED' status
    rows in the ALL-RESULTS block are NOT double-counted (\\bFAIL\\b boundary)."""

    def test_counts_failure_rows_not_failed_status(self):
        txt = "\n".join([
            "FAILURES (grouped by scene):",
            "  [FLIGHT]",
            "    FAIL  RecordingInvariants.SomeTest (12.3ms)",
            "          boom",
            "    FAIL  RecordingInvariants.Other (1.0ms)",
            "",
            "ALL RESULTS (one row per scene, per test):",
            "  [RecordingInvariants]",
            "    SomeTest",
            "      FLIGHT         FAILED  (12.3ms)",
        ])
        self.assertEqual(hlib.parse_results_failures(txt), 2)

    def test_clean_results_zero(self):
        txt = "ALL RESULTS:\n  [X]\n    T\n      FLIGHT         PASSED  (1.0ms)\n"
        self.assertEqual(hlib.parse_results_failures(txt), 0)


# ---------------------------------------------------------------------------
# Response-stream evaluation.
# ---------------------------------------------------------------------------


class ResponseStreamTests(unittest.TestCase):
    """Guards: a crash-recovery rewrite (M-A2) must NOT count as a second
    outcome (first-wins), and a verdict mismatch must be flagged (a driver
    failure must never read as a pass)."""

    STEPS = [
        {"id": "0001", "cmd": "LoadGame", "expect": "OK"},
        {"id": "0002", "cmd": "RunTests", "expect": "OK"},
        {"id": "0003", "cmd": "FlushAndQuit", "expect": "OK"},
    ]

    def test_all_met(self):
        lines = [
            "id=0001 cmd=LoadGame verdict=OK seq=1 ut=10.0 scene=FLIGHT save=fresh-career",
            "id=0002 cmd=RunTests verdict=OK seq=2 ut=20.0 passed=12 failed=0 skipped=0",
            "id=0003 cmd=FlushAndQuit verdict=OK seq=3 saved=true",
        ]
        ev = hlib.evaluate_response_stream(lines, self.STEPS)
        self.assertTrue(ev.all_expected_met)
        self.assertIsNone(ev.first_unmet)

    def test_first_wins_dedupe(self):
        lines = [
            "id=0001 cmd=LoadGame verdict=OK seq=1 ut=10.0",
            "id=0002 cmd=RunTests verdict=OK seq=2 passed=12 failed=0 skipped=0",
            "id=0002 cmd=RunTests verdict=OK seq=9 passed=12 failed=0 skipped=0",  # rewrite
            "id=0003 cmd=FlushAndQuit verdict=OK seq=3",
        ]
        ev = hlib.evaluate_response_stream(lines, self.STEPS)
        self.assertTrue(ev.all_expected_met)
        self.assertEqual(ev.duplicate_ids, ("0002",))

    def test_verdict_mismatch_flagged(self):
        lines = [
            "id=0001 cmd=LoadGame verdict=ERROR seq=1 msg=load-failed",
            "id=0002 cmd=RunTests verdict=OK seq=2 passed=1 failed=0 skipped=0",
        ]
        ev = hlib.evaluate_response_stream(lines, self.STEPS)
        self.assertFalse(ev.all_expected_met)
        self.assertEqual(ev.first_unmet.step_id, "0001")
        self.assertEqual(ev.first_unmet.verdict, "ERROR")

    def test_missing_response_is_unmet(self):
        lines = ["id=0001 cmd=LoadGame verdict=OK seq=1"]
        ev = hlib.evaluate_response_stream(lines, self.STEPS)
        self.assertFalse(ev.all_expected_met)
        self.assertEqual(ev.first_unmet.step_id, "0002")
        self.assertFalse(ev.first_unmet.found)

    def test_refusal_msg_threaded_onto_outcome(self):
        # Item 6: the response line's msg= token is captured onto the StepOutcome so the
        # driver-stage subkind mapping can read the M-C1 refusal reason.
        steps = [{"id": "0001", "cmd": "InvokeRewind", "expect": "OK"}]
        lines = ["id=0001 cmd=InvokeRewind verdict=REJECTED seq=1 msg=refly-gate%20not-ready"]
        ev = hlib.evaluate_response_stream(lines, steps)
        self.assertFalse(ev.all_expected_met)
        self.assertEqual(ev.first_unmet.verdict, "REJECTED")
        self.assertEqual(ev.first_unmet.msg, "refly-gate%20not-ready")


class SeamRefusalSubkindTests(unittest.TestCase):
    """Item 6: the M-C1 verb-refusal msg= reason maps to the finer driver-* subkind so
    the five subkinds are no longer dead vocabulary. Unknown reasons fall back to ""
    (the caller then uses driver-verdict-mismatch)."""

    def test_known_refusal_prefixes_map(self):
        cases = {
            "refly-gate%20some-detail": "driver-gate",   # compound gate reason
            "refly-gate": "driver-gate",
            "unknown-rp": "driver-arg",
            "unknown-slot": "driver-arg",
            "no-live-dialog": "driver-dialog",
            "choice-unavailable": "driver-dialog",
            "unknown-choice": "driver-dialog",
            "career-not-ready": "driver-career",
            "insufficient-funds": "driver-career",
            "backward-jump": "driver-rewind",
            "jump-refused": "driver-rewind",
            "missing-jump-target": "driver-arg",
            "unknown-facility": "driver-arg",
        }
        for msg, expect in cases.items():
            with self.subTest(msg=msg):
                self.assertEqual(hlib.classify_seam_refusal_subkind(msg), expect)

    def test_unknown_reason_falls_back(self):
        self.assertEqual(hlib.classify_seam_refusal_subkind("some-other-reason"), "")
        self.assertEqual(hlib.classify_seam_refusal_subkind(""), "")
        self.assertEqual(hlib.classify_seam_refusal_subkind(None), "")


# ---------------------------------------------------------------------------
# Spec validation.
# ---------------------------------------------------------------------------


class RealSpecFileTests(unittest.TestCase):
    """Guards: the shipped sample specs must validate against the shipped
    registry (a TOML placement bug or a stale dimension token would only surface
    against the REAL files, not an inline dict -- mirrors test_provlib's
    RealProfileFileTests)."""

    def test_b10_validates(self):
        reg = load_registry()
        spec = load_spec("B10-career-passive-safety.toml")
        v = hlib.validate_spec(spec, reg)
        self.assertTrue(v.ok, "B10 spec must validate; errors=%s" % (v.errors,))

    def test_injected_playback_validates(self):
        reg = load_registry()
        spec = load_spec("S1.4-injected-playback.toml")
        v = hlib.validate_spec(spec, reg)
        self.assertTrue(v.ok, "playback spec must validate; errors=%s" % (v.errors,))


class CommittedSpecValidationTests(unittest.TestCase):
    """BLOCKER-1 regression: EVERY committed scenario spec must validate through the
    REAL admission path -- parse the .toml with tomllib, resolve mission schemas via
    ``run.resolve_mission_schemas`` EXACTLY as run.py does at spec admission, then
    ``hlib.validate_spec`` -- so a committed-spec regression can NEVER escape to a
    scheduled run that would waste a KSP boot. This specifically catches the failure
    that shipped: an autopilot spec's ``steps`` array placed AFTER the
    ``[driver.missionParams]`` header, which TOML scopes to
    ``driver.missionParams.steps`` and leaves ``driver.steps`` empty (validation then
    reports 'driver.steps: empty' / 'found 0 mission steps'). Reads the scenarios dir
    relative to THIS test file."""

    def test_every_committed_spec_validates_via_real_path(self):
        reg = run.load_registry()
        bug_ids = run._load_bug_ids()
        names = sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml"))
        self.assertTrue(names, "no committed scenario specs found under %s" % SCENARIOS_DIR)
        for name in names:
            with self.subTest(spec=name):
                with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                    spec = tomllib.load(fh)
                mission_schemas, shell_errors = run.resolve_mission_schemas(spec)
                validation = hlib.validate_spec(spec, reg, bug_ids, mission_schemas)
                self.assertEqual([], list(validation.errors),
                                 "%s failed real-path validation: %s"
                                 % (name, list(validation.errors)))
                self.assertEqual([], list(shell_errors),
                                 "%s shell (mission-ref) errors: %s" % (name, shell_errors))

    def test_autopilot_specs_keep_steps_in_driver_table(self):
        """Direct guard on the exact BLOCKER-1 shape: an autopilot spec's ``steps``
        must live in the ``[driver]`` table with the mission handoff step present,
        NOT re-nested under ``[driver.missionParams]``."""
        for name in ("B1-pad-hop.toml", "B2-lko-ascent.toml",
                     "B4-reentry-splashdown.toml"):
            with self.subTest(spec=name):
                with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                    spec = tomllib.load(fh)
                driver = spec.get("driver", {})
                steps = driver.get("steps", [])
                self.assertTrue(steps, "%s: driver.steps must be non-empty" % name)
                self.assertNotIn("steps", driver.get("missionParams", {}),
                                 "%s: steps leaked into [driver.missionParams]" % name)
                self.assertEqual(
                    1, sum(1 for s in steps if s.get("phase") == "mission"),
                    "%s: exactly one mission handoff step expected in driver.steps" % name)


class SpecValidationRejectTests(unittest.TestCase):
    """Each reject names the regression: a malformed spec launching KSP wastes a
    boot and yields a meaningless verdict, and a valid spec wrongly rejected
    drops coverage."""

    def setUp(self):
        self.reg = load_registry()
        self.base = load_spec("B10-career-passive-safety.toml")

    def _reject(self, mutate):
        spec = copy.deepcopy(self.base)
        mutate(spec)
        return hlib.validate_spec(spec, self.reg)

    def test_missing_required_field(self):
        v = self._reject(lambda s: s.pop("tier"))
        self.assertFalse(v.ok)
        self.assertTrue(any("tier" in e for e in v.errors))

    def test_unknown_dimension_value(self):
        v = self._reject(lambda s: s["dimensionsCovered"].__setitem__("D8", ["not-a-real-value"]))
        self.assertFalse(v.ok)
        self.assertTrue(any("D8" in e and "not-a-real-value" in e for e in v.errors))

    def test_unknown_dimension_key(self):
        v = self._reject(lambda s: s["dimensionsCovered"].__setitem__("D99", ["x"]))
        self.assertFalse(v.ok)
        self.assertTrue(any("D99" in e for e in v.errors))

    def test_autopilot_driver_kind_rejected(self):
        v = self._reject(lambda s: s["driver"].__setitem__("kind", "autopilot"))
        self.assertFalse(v.ok)
        self.assertTrue(any("autopilot" in e or "seam" in e for e in v.errors))

    # --- R5 `isolated` (hlib.BATCH_ISOLATED_KEY) ---
    #
    # Every cell here guards the SAME failure direction: a malformed or misplaced
    # flag is silently inert, the batch runs NON-isolated, the tests the spec meant
    # to drive are all skipped, and the resulting tally reads like a Parsek
    # regression rather than a spec typo. Fail-closed at validation time is the only
    # place that costs nothing.

    def _run_tests_step(self, spec):
        for step in spec["driver"]["steps"]:
            if step.get("cmd") == "RunTests":
                return step
        raise AssertionError("base spec has no RunTests step")

    def test_isolated_accepts_the_two_wire_literals(self):
        # The POSITIVE control. Without it, every negative cell below would still
        # pass if the key were rejected unconditionally.
        for value in ("true", "false"):
            with self.subTest(value=value):
                def m(s):
                    self._run_tests_step(s)["args"]["isolated"] = value
                v = self._reject(m)
                self.assertTrue(v.ok, v.errors)

    def test_isolated_as_a_toml_bool_rejected(self):
        # THE TRAP. run.py::encode_value is str(value), so the TOML bool `true`
        # travels as the token `isolated=True`, which the seam's case-sensitive
        # TryParseIsolatedArg REJECTS. Catching it here saves a full KSP boot.
        def m(s):
            self._run_tests_step(s)["args"]["isolated"] = True
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("isolated" in e and "STRING" in e for e in v.errors),
                        v.errors)

    def test_isolated_with_an_unrecognised_value_rejected(self):
        # "True" is str(True) - THE trap the module note names, and it was the one
        # spelling missing from this sweep.
        for value in ("1", "yes", "TRUE", "True", "False", "", " true"):
            with self.subTest(value=value):
                def m(s):
                    self._run_tests_step(s)["args"]["isolated"] = value
                v = self._reject(m)
                self.assertFalse(v.ok)
                self.assertTrue(any("isolated" in e for e in v.errors), v.errors)

    def test_isolated_on_a_non_runtests_step_rejected(self):
        # No other verb reads the arg, so it would be silently ignored.
        def m(s):
            s["driver"]["steps"].insert(
                1, {"cmd": "RecordingState", "args": {"isolated": "true"},
                    "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("isolated" in e and "RunTests" in e for e in v.errors),
                        v.errors)

    def test_isolated_beside_cmd_instead_of_in_args_rejected(self):
        # MISPLACED-KEY guard: a key outside `args` is never written to the channel.
        def m(s):
            self._run_tests_step(s)["isolated"] = "true"
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("isolated" in e and "args" in e for e in v.errors),
                        v.errors)

    def test_isolated_in_the_wrong_table_rejected(self):
        # The autorun flag is read ONLY off [driver.autorun]. A key written before
        # that header lands in [driver]; one written at the spec root or under
        # [expectations] lands nowhere useful. All are silently inert today.
        for scope in ("driver", "root", "expectations"):
            with self.subTest(scope=scope):
                def m(s, scope=scope):
                    target = (s["driver"] if scope == "driver"
                              else s if scope == "root"
                              else s.setdefault("expectations", {}))
                    target["isolated"] = True
                v = self._reject(m)
                self.assertFalse(v.ok)
                self.assertTrue(
                    any("isolated" in e and "driver.autorun" in e for e in v.errors),
                    v.errors)

    def test_autorun_isolated_must_be_a_bool(self):
        # A string "false" is TRUTHY in Python, so coercing instead of rejecting
        # would arm the very route the author was disabling.
        def m(s):
            s["driver"]["steps"] = [
                st for st in s["driver"]["steps"] if st.get("cmd") != "RunTests"]
            s["driver"]["autorun"] = {"tests": "SceneExitMerge", "isolated": "false"}
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("autorun.isolated" in e and "bool" in e
                            for e in v.errors), v.errors)

    def test_autorun_isolated_without_tests_rejected(self):
        def m(s):
            s["driver"]["autorun"] = {"isolated": True}
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("autorun.isolated" in e and "inert" in e
                            for e in v.errors), v.errors)

    def test_isolated_in_any_nested_table_rejected(self):
        # RECURSIVE guard, not an allowlist. The first cut named four scopes; a
        # review found four more that slipped through, and the most dangerous is
        # [expectations.logContracts] - the table where the OTHER batch-behaviour
        # flag (batchVacuityOptOut) lives, so it is the most plausible wrong home.
        for path in (("expectations", "logContracts"), ("expectations", "recordings"),
                     ("runtime",), ("fixture",), ("retry",)):
            with self.subTest(table=".".join(path)):
                def m(s, path=path):
                    node = s
                    for part in path:
                        node = node.setdefault(part, {})
                    node["isolated"] = True
                v = self._reject(m)
                self.assertFalse(v.ok, "isolated under [%s] must not validate"
                                 % ".".join(path))
                self.assertTrue(any("isolated" in e and "never read" in e
                                    for e in v.errors), v.errors)

    def test_isolated_on_a_mission_step_rejected(self):
        # The mission branch `continue's before the R5 guards, so both levels need
        # naming explicitly.
        for where in ("args", "step"):
            with self.subTest(where=where):
                def m(s, where=where):
                    step = {"phase": "mission", "expect": hlib.MISSION_STEP_EXPECT}
                    if where == "args":
                        step["args"] = {"isolated": "true"}
                    else:
                        step["isolated"] = True
                    s["driver"]["steps"].insert(1, step)
                v = self._reject(m)
                self.assertFalse(v.ok)
                self.assertTrue(any("isolated" in e and "mission" in e
                                    for e in v.errors), v.errors)

    def test_a_case_variant_isolated_arg_key_rejected(self):
        # No per-verb arg vocabulary exists, so an unknown key is forwarded verbatim
        # and the C# `ArgOrNull(cmd, "isolated")` (exact, case-sensitive) misses it.
        for key in ("Isolated", "ISOLATED", "isoLated"):
            with self.subTest(key=key):
                def m(s, key=key):
                    self._run_tests_step(s)["args"][key] = "true"
                v = self._reject(m)
                self.assertFalse(v.ok)
                self.assertTrue(any("case-sensitive" in e for e in v.errors), v.errors)

    def test_run_tests_steps_may_not_disagree_on_the_batch_mode(self):
        # Reachable only through the documented batchVacuityOptOut escape, which is
        # exactly where a review found it: two RunTests steps, the FIRST ordinary and
        # the SECOND isolated, validated cleanly while spec_batch_isolated answered
        # for the wrong one.
        def m(s):
            step = copy.deepcopy(self._run_tests_step(s))
            step["args"]["isolated"] = "true"
            s["driver"]["steps"].insert(
                s["driver"]["steps"].index(self._run_tests_step(s)) + 1, step)
            lc = s.setdefault("expectations", {}).setdefault("logContracts", {})
            lc[hlib.BATCH_VACUITY_OPT_OUT_KEY] = True
            lc[hlib.BATCH_VACUITY_OPT_OUT_REASON_KEY] = "probe"
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("DISAGREE" in e for e in v.errors), v.errors)

    # --- The ISOLATED AXIS of the pre-existing gates (the review's F1) ---
    #
    # R5 threaded an `isolated` mode through the tally cross-check and past four
    # gates, and pinned NOWHERE that those gates still apply to an isolated spec.
    # Four one-line "isolated specs are exempt" mutations all survived the suite.
    # That matters because the change's own narrative is "the ordinary derivation
    # would have REJECTED a correct isolated pin" - so the instinct on hitting a
    # second such rejection is to exempt, which silently reinstates the whole
    # B10 GREEN-over-ZERO-executed-tests class for every isolated spec.

    def _isolated_spec(self):
        spec = copy.deepcopy(load_spec("H21-scene-exit-merge-isolated.toml"))
        self.assertTrue(hlib.spec_batch_isolated(spec))
        return spec

    def test_the_anti_vacuity_gate_still_applies_to_an_isolated_spec(self):
        spec = self._isolated_spec()
        lc = spec["expectations"]["logContracts"]
        lc["required"] = [r"BATCH_COMPLETE v1 .*failed=0\b"]
        v = hlib.validate_spec(spec, self.reg)
        self.assertFalse(
            v.ok, "an isolated spec with a failed=0-only contract must still be "
                  "rejected: that pattern is satisfied by the all-skipped batch, "
                  "which is exactly what the ordinary path prints for this category")

    def test_the_single_selector_rule_still_applies_to_an_isolated_spec(self):
        spec = self._isolated_spec()
        self._run_tests_step(spec)["args"]["category"] = "SceneExitMerge,MergeDialog"
        v = hlib.validate_spec(spec, self.reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("multi-category" in e for e in v.errors), v.errors)

    # NOTE on why these two look contrived. For a SELF-CONSISTENT pin the floor and
    # the ceiling are algebraically the same check: executed = total - skipped and
    # executable = total - attribute_skipped, so executed > executable holds exactly
    # when skipped < attribute_skipped. A pin that trips one trips the other, and
    # disabling either alone is invisible. Each is therefore isolated by OMITTING
    # the token the other one reads.

    @staticmethod
    def _isolated_decls():
        # 2 restore-backed + 1 manual-only, all FLIGHT. Isolated derivation:
        # total=3, attribute_skipped=1, executable=2.
        return [hlib.InGameTestDecl(category="C", scene="FLIGHT", allow_batch=False,
                                    origin="F.cs:1 a", restore_baseline=True),
                hlib.InGameTestDecl(category="C", scene="FLIGHT", allow_batch=False,
                                    origin="F.cs:2 b", restore_baseline=True),
                hlib.InGameTestDecl(category="C", scene="FLIGHT", allow_batch=False,
                                    origin="F.cs:3 c", restore_baseline=False)]

    def test_the_skipped_floor_still_applies_on_the_isolated_derivation(self):
        # passed=/failed= OMITTED so the ceiling cannot fire and only the floor can.
        pin = hlib.resolve_batch_tally_pin(
            ["BATCH_COMPLETE v1 total=3 skipped=0 category=C scene=FLIGHT"])
        self.assertIsNone(pin.passed)
        problems = hlib.batch_tally_pin_mismatches(pin, self._isolated_decls(),
                                                   isolated=True)
        self.assertNotEqual(
            problems, [],
            "one declaration is manual-only (neither flag), so even the isolated "
            "filter forces a skip and skipped=0 is underivable - the floor must "
            "still fire when the spec is isolated")

    def test_the_executable_ceiling_still_applies_on_the_isolated_derivation(self):
        # skipped= OMITTED so the floor cannot fire and only the ceiling can.
        pin = hlib.resolve_batch_tally_pin(
            ["BATCH_COMPLETE v1 total=3 passed=3 failed=0 category=C scene=FLIGHT"])
        self.assertIsNone(pin.skipped)
        problems = hlib.batch_tally_pin_mismatches(pin, self._isolated_decls(),
                                                   isolated=True)
        self.assertNotEqual(
            problems, [],
            "only 2 of the 3 are admitted even by the isolated filter, so passed=3 "
            "is impossible - the ceiling must still fire when the spec is isolated")

    def test_reserved_seam_verb_rejected(self):
        # SealSlot stays RESERVED after M-C1 (the four implemented verbs were removed
        # from the reserved set; SealSlot is one of the eleven that stay reserved).
        def m(s):
            s["driver"]["steps"].insert(1, {"cmd": "SealSlot", "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("SealSlot" in e and "RESERVED" in e for e in v.errors))

    def test_mc1_implemented_verbs_not_reserved(self):
        # M-C1 moved these four RESERVED -> IMPLEMENTED, mirroring the C# verb-table move.
        for verb in ("InvokeRewind", "AnswerMergeDialog", "KscAction", "TimeJump"):
            with self.subTest(verb=verb):
                self.assertIn(verb, hlib.IMPLEMENTED_SEAM_VERBS)
                self.assertNotIn(verb, hlib.RESERVED_SEAM_VERBS)

    def test_mc1_reserved_verbs_still_reserved(self):
        # The remaining SEVEN names stay RESERVED (not v1-drivable).
        # SimulateStockSwitchClick WAS in this list and left it in R12; MissionConfig
        # left it for the arrival-validation lane; StartLoopPlayback and EnterWatchMode
        # left it for the player-workflow lane - see the promotion cells below.
        # StopPlayback stays reserved on purpose: teardown is FlushAndQuit's job.
        for verb in ("StopPlayback", "SealSlot",
                     "StashSlot", "FlySlot", "RouteCommand",
                     "CrashAfterJournalPhase", "RunInvariantReport"):
            with self.subTest(verb=verb):
                self.assertIn(verb, hlib.RESERVED_SEAM_VERBS)
                self.assertNotIn(verb, hlib.IMPLEMENTED_SEAM_VERBS)

    def test_mc1_verb_step_accepted(self):
        # A spec step using an M-C1 verb is no longer flagged RESERVED / unknown. The
        # base carries [expectations.ledger]; InvokeRewind / AnswerMergeDialog CANNOT
        # pair with a ledger block (item 1), so drop it here -- this test is about verb
        # ACCEPTANCE, not ledger pairing (that rejection has its own test below).
        for verb in ("InvokeRewind", "AnswerMergeDialog", "KscAction", "TimeJump"):
            with self.subTest(verb=verb):
                def m(s):
                    s.get("expectations", {}).pop("ledger", None)
                    s["driver"]["steps"].insert(1, {"cmd": verb, "expect": "OK"})
                v = self._reject(m)
                self.assertFalse(any(verb in e for e in v.errors),
                                 "%s wrongly flagged: %s" % (verb, list(v.errors)))

    def test_mc11_savegame_implemented_not_reserved(self):
        # M-C1.1 follow-up: SaveGame is a NEW implemented verb (never in the RESERVED
        # envelope), the M-B3 L2/R6 persist-before-reload dependency.
        self.assertIn("SaveGame", hlib.IMPLEMENTED_SEAM_VERBS)
        self.assertNotIn("SaveGame", hlib.RESERVED_SEAM_VERBS)

    def test_mc11_savegame_step_accepted(self):
        # A spec step using SaveGame is not flagged RESERVED / unknown.
        def m(s):
            s.get("expectations", {}).pop("ledger", None)
            s["driver"]["steps"].insert(1, {"cmd": "SaveGame", "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(any("SaveGame" in e for e in v.errors),
                         "SaveGame wrongly flagged: %s" % list(v.errors))

    def test_mc2_eva_verbs_implemented_not_reserved(self):
        # M-C2: EvaExit / EvaBoard / PlantFlag are NEW implemented verbs (never in the
        # RESERVED envelope), additive like SaveGame; EVA-4 added EvaChuteDeploy the same
        # way. Verb table is 24 implemented / 7 reserved after the player-workflow
        # lane (mirrors the C# TestCommandVerbs counts: 19 + ExitToSpaceCenter
        # additive + THREE promotions out of the reserved list - R12's
        # SimulateStockSwitchClick, the arrival-validation lane's MissionConfig, and
        # the player-workflow lane's StartLoopPlayback + EnterWatchMode - which take
        # reserved 11 -> 7).
        for verb in ("EvaExit", "EvaBoard", "PlantFlag", "EvaChuteDeploy"):
            with self.subTest(verb=verb):
                self.assertIn(verb, hlib.IMPLEMENTED_SEAM_VERBS)
                self.assertNotIn(verb, hlib.RESERVED_SEAM_VERBS)
        # 25 after M-A7's ExportRenderManifest, which is ADDITIVE (never in the RESERVED
        # envelope, like SaveGame and the EVA family), so reserved stays at 7. 27 after
        # the map-view pair (EnterMapView / ExitMapView), additive the same way - the
        # reserved envelope never carried a camera / scene-presentation verb - so reserved
        # is STILL 7. 28 after InvokeRewindToLaunch, additive the same way: the reserved
        # envelope's rewind names (FlySlot / SealSlot / StashSlot) are all SLOT verbs
        # against Rewind-to-SEPARATION's RewindPoint model, so reserved is STILL 7.
        self.assertEqual(len(hlib.IMPLEMENTED_SEAM_VERBS), 28)
        self.assertEqual(len(hlib.RESERVED_SEAM_VERBS), 7)

    def test_ma7_export_render_manifest_implemented_not_reserved(self):
        # M-A7: ExportRenderManifest is a NEW implemented verb (never in the RESERVED
        # envelope) - the additive SaveGame / EVA shape, not a promotion.
        self.assertIn("ExportRenderManifest", hlib.IMPLEMENTED_SEAM_VERBS)
        self.assertNotIn("ExportRenderManifest", hlib.RESERVED_SEAM_VERBS)

    def test_ma7_export_render_manifest_step_accepted(self):
        # A spec step using ExportRenderManifest is not flagged RESERVED / unknown.
        # The block is DECLARED alongside it because the verb and the block are
        # COUPLED (cells below); a bare verb is now its own rejection and would
        # mask the reserved/unknown check this cell is actually about.
        def m(s):
            s.get("expectations", {}).pop("ledger", None)
            s.setdefault("expectations", {})["renderComposition"] = {
                "dwells": {"min": 1}}
            s["driver"]["steps"].insert(
                1, {"cmd": "ExportRenderManifest", "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(
            any("ExportRenderManifest" in e for e in v.errors),
            "ExportRenderManifest wrongly flagged: %s" % list(v.errors))

    def test_ma7_export_verb_expecting_ok_without_the_block_is_rejected(self):
        # The recorder is armed by the DECLARATION and by nothing else, so this
        # spec exports nothing and reads nobody - and every symptom of that is
        # quiet. Refused before KSP boots, naming both fixes.
        def m(s):
            s.get("expectations", {}).pop("ledger", None)
            s.get("expectations", {}).pop("renderComposition", None)
            s["driver"]["steps"].insert(
                1, {"cmd": "ExportRenderManifest", "expect": "OK"})
        v = self._reject(m)
        hits = [e for e in v.errors if "ExportRenderManifest" in e]
        self.assertEqual(1, len(hits), list(v.errors))
        self.assertIn("renderComposition", hits[0])
        self.assertIn("REJECTED", hits[0])

    def test_ma7_export_verb_expecting_rejected_is_a_legal_negative_control(self):
        # The unarmed-recorder negative control: a lane proving the verb declines
        # when nothing armed it. It asserts something precisely BECAUSE the block
        # is absent, so the coupling rule must leave it alone.
        def m(s):
            s.get("expectations", {}).pop("ledger", None)
            s.get("expectations", {}).pop("renderComposition", None)
            s["driver"]["steps"].insert(
                1, {"cmd": "ExportRenderManifest", "expect": "REJECTED"})
        v = self._reject(m)
        self.assertFalse(any("ExportRenderManifest" in e for e in v.errors),
                         list(v.errors))

    def test_map_view_verbs_implemented_not_reserved(self):
        # The map-view pair is ADDITIVE (never in the RESERVED envelope), the SaveGame /
        # EVA / ExportRenderManifest shape rather than a promotion.
        for verb in ("EnterMapView", "ExitMapView"):
            with self.subTest(verb=verb):
                self.assertIn(verb, hlib.IMPLEMENTED_SEAM_VERBS)
                self.assertNotIn(verb, hlib.RESERVED_SEAM_VERBS)

    def test_map_view_verbs_step_accepted(self):
        # A spec step using either map-view verb is not flagged RESERVED / unknown.
        # Deliberately NO expectations block is added: unlike ExportRenderManifest,
        # these verbs are NOT coupled to `[expectations.renderComposition]` - they open
        # and close the map, and a lane may want the map open for reasons the manifest
        # never reads (a screenshot moment, a tracer window). Coupling them would refuse
        # those specs for a dependency they do not have.
        for verb in ("EnterMapView", "ExitMapView"):
            with self.subTest(verb=verb):
                def m(s, _verb=verb):
                    s.get("expectations", {}).pop("ledger", None)
                    s["driver"]["steps"].insert(1, {"cmd": _verb, "expect": "OK"})
                v = self._reject(m)
                self.assertFalse(any(verb in e for e in v.errors),
                                 "%s wrongly flagged: %s" % (verb, list(v.errors)))

    def test_rewind_to_launch_verb_implemented_not_reserved(self):
        # ADDITIVE (never in the RESERVED envelope), the SaveGame / EVA /
        # ExportRenderManifest shape rather than a promotion. The reserved envelope's
        # rewind names are all SLOT verbs against Rewind-to-SEPARATION's RewindPoint
        # model, so none of them is this verb under another spelling - asserted, not
        # assumed, because a promotion and an addition have different bookkeeping.
        self.assertIn("InvokeRewindToLaunch", hlib.IMPLEMENTED_SEAM_VERBS)
        self.assertNotIn("InvokeRewindToLaunch", hlib.RESERVED_SEAM_VERBS)
        for slot_verb in ("FlySlot", "SealSlot", "StashSlot"):
            self.assertIn(slot_verb, hlib.RESERVED_SEAM_VERBS)

    def test_rewind_to_launch_step_accepted(self):
        # A spec step using the verb is not flagged RESERVED / unknown. The base
        # carries [expectations.ledger], which this verb CANNOT pair with (the L4
        # deferral, its own cell below), so drop it here - this cell is about verb
        # ACCEPTANCE and the ledger rejection would mask it.
        for args in ({}, {"tree": "tree-1"}):
            with self.subTest(args=args):
                def m(s, _args=args):
                    s.get("expectations", {}).pop("ledger", None)
                    s["driver"]["steps"].insert(
                        1, {"cmd": "InvokeRewindToLaunch", "args": _args,
                            "expect": "OK"})
                v = self._reject(m)
                self.assertFalse(
                    any("InvokeRewindToLaunch" in e for e in v.errors),
                    "InvokeRewindToLaunch wrongly flagged: %s" % list(v.errors))

    def test_rewind_to_launch_is_two_phase_deferred(self):
        # It joins DEFERRED_SEAM_VERBS for InvokeRewind's reason verbatim (a whole
        # world reload gates the terminal), and its dispatch budget mirrors the C#
        # InvokeRewindSeconds = 300 rather than the 120 s scene-EXIT class.
        self.assertIn("InvokeRewindToLaunch", hlib.DEFERRED_SEAM_VERBS)
        self.assertEqual(300.0, hlib.dispatch_deferral_budget("InvokeRewindToLaunch"))
        self.assertEqual(hlib.dispatch_deferral_budget("InvokeRewind"),
                         hlib.dispatch_deferral_budget("InvokeRewindToLaunch"))

    def test_rewind_to_launch_budget_cap(self):
        # A deferred verb step over the 540 s cap is rejected (S8), same as
        # InvokeRewind / TimeJump.
        def m(s):
            s.get("expectations", {}).pop("ledger", None)
            s["driver"]["steps"].insert(
                1, {"cmd": "InvokeRewindToLaunch", "expect": "OK", "budget": 600})
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(
            any("InvokeRewindToLaunch" in e and "540" in e for e in v.errors),
            "expected S8 budget-cap error: %s" % list(v.errors))

    def test_rewind_to_launch_cannot_pair_with_a_ledger_block(self):
        # The InvokeRewind rejection extended to the launch rewind: restoring a
        # quicksave rewrites the career pools the seed+manifest contract cannot
        # reconstruct, and the rewound-career oracle is DEFERRED to L4. The base
        # already declares [expectations.ledger].
        def m(s):
            s["driver"]["steps"].insert(
                1, {"cmd": "InvokeRewindToLaunch", "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(
            any("InvokeRewindToLaunch" in e and "L4" in e and "ledger" in e
                for e in v.errors),
            "expected ledger+InvokeRewindToLaunch L4-deferral rejection: %s"
            % list(v.errors))

    def test_rewind_to_launch_refusal_reasons_classify(self):
        # The verb's REJECTED taxonomy maps to the finer driver-* subkinds instead of
        # collapsing to the coarse driver-verdict-mismatch. `rewind-gate` is compound
        # on the wire (percent-encoded), so it must classify off the head token the way
        # `refly-gate` does.
        self.assertEqual("driver-gate",
                         hlib.classify_seam_refusal_subkind("rewind-gate%20not-ready"))
        self.assertEqual("driver-gate",
                         hlib.classify_seam_refusal_subkind("no-committed-tree"))
        self.assertEqual("driver-arg",
                         hlib.classify_seam_refusal_subkind("ambiguous-tree"))
        # Shared with the loop lanes, mapped once: this table is keyed by msg token
        # alone, never by (verb, msg).
        self.assertEqual("driver-arg",
                         hlib.classify_seam_refusal_subkind("unknown-tree"))
        for sk in ("driver-gate", "driver-arg"):
            self.assertIn(sk, hlib.RETRYABLE_INVALID_SUBKINDS)

    def test_no_committed_spec_trips_the_export_verb_coupling_rule(self):
        # Repo-wide, DISCOVERED rather than hardcoded: a rule the committed corpus
        # cannot satisfy on the day it lands is a rule nobody can satisfy.
        checked = 0
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            spec = load_spec(name)
            declared = "renderComposition" in (spec.get("expectations", {}) or {})
            steps = (spec.get("driver", {}) or {}).get("steps", []) or []
            offenders = [s for s in steps
                         if (s or {}).get("cmd") == hlib.RENDER_MANIFEST_EXPORT_VERB
                         and (s or {}).get("expect") == "OK"]
            if not offenders:
                continue
            checked += 1
            self.assertTrue(
                declared,
                "%s drives %s expecting OK with no "
                "[expectations.renderComposition] block"
                % (name, hlib.RENDER_MANIFEST_EXPORT_VERB))
        # Phase 3 (2026-08-25) landed the first two drivers - V14M-ike-player-loop
        # and V8-eve-player-loop - so the sweep now actually bites. The floor is
        # asserted at that count rather than left at `>= 0`: a lane losing its
        # export step would otherwise slip past this cell silently, and the
        # roster that names both lanes lives in
        # `RenderComposeVerifierWiringTests.RENDERCOMPOSE_DECLARER_SPECS`.
        self.assertGreaterEqual(checked, 2)

    def test_r12_verbs_implemented_not_reserved(self):
        # R12 landed TWO verbs of DIFFERENT shapes, and the distinction is the point:
        # ExitToSpaceCenter is ADDITIVE (never in the reserved envelope, like SaveGame
        # and the EVA family), SimulateStockSwitchClick is a PROMOTION out of it - the
        # first since M-C1. Both must end up implemented and neither reserved.
        for verb in ("ExitToSpaceCenter", "SimulateStockSwitchClick"):
            with self.subTest(verb=verb):
                self.assertIn(verb, hlib.IMPLEMENTED_SEAM_VERBS)
                self.assertNotIn(verb, hlib.RESERVED_SEAM_VERBS)

    def test_r12_switchclick_step_is_no_longer_rejected_as_reserved(self):
        # The BEHAVIOURAL half of the promotion: before R12 a spec naming this verb
        # failed validation with "is RESERVED, not v1-drivable", which is what made a
        # switch-segment spec unauthorable. Fails if the promotion is cosmetic.
        def m(s):
            s.get("expectations", {}).pop("ledger", None)
            s["driver"]["steps"].insert(
                1, {"cmd": "SimulateStockSwitchClick",
                    "args": {"site": "map", "vessel": "Test Craft"}, "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(any("SimulateStockSwitchClick" in e for e in v.errors),
                         "SimulateStockSwitchClick wrongly flagged: %s" % list(v.errors))

    def test_r12_exit_to_space_center_step_accepted(self):
        def m(s):
            s.get("expectations", {}).pop("ledger", None)
            s["driver"]["steps"].insert(1, {"cmd": "ExitToSpaceCenter", "expect": "OK",
                                            "budget": 120})
        v = self._reject(m)
        self.assertFalse(any("ExitToSpaceCenter" in e for e in v.errors),
                         "ExitToSpaceCenter wrongly flagged: %s" % list(v.errors))

    def test_r12_verbs_are_not_two_phase_deferred(self):
        # Neither joins DEFERRED_SEAM_VERBS, and each for its OWN reason.
        # ExitToSpaceCenter IS two-phase but sits in the AnswerMergeDialog class (drives a
        # scene exit, completes on the settle, 120 s), so it rides the per-verb dispatch
        # dict; SimulateStockSwitchClick is single-phase and rides the 60 s default.
        # Membership in DEFERRED_SEAM_VERBS is about the 540 s cap governing a
        # spec-declared budget, which neither needs.
        for verb in ("ExitToSpaceCenter", "SimulateStockSwitchClick"):
            with self.subTest(verb=verb):
                self.assertNotIn(verb, hlib.DEFERRED_SEAM_VERBS)
                self.assertLess(hlib.dispatch_deferral_budget(verb),
                                hlib.MAX_DEFERRED_STEP_BUDGET_SECONDS)

    def test_r12_dispatch_budgets_mirror_c_sharp(self):
        # Mirrors TestCommandDispatcher.DeferralBudget: ExitToSpaceCenterSeconds = 120,
        # SimulateStockSwitchClick falls through to DefaultSeconds = 60. Without the
        # ExitToSpaceCenter row the harness step-wait would ride 60 s + margin and could
        # KILL a healthy KSC bootstrap at ~120 s, converting a retryable seam TIMEOUT into
        # a terminal KILLED.
        self.assertEqual(hlib.dispatch_deferral_budget("ExitToSpaceCenter"), 120.0)
        self.assertEqual(hlib.dispatch_deferral_budget("SimulateStockSwitchClick"), 60.0)
        self.assertEqual(hlib.required_dispatch_step_wait("ExitToSpaceCenter"), 180.0)

    def test_player_workflow_verbs_implemented_not_reserved(self):
        # The player-workflow lane's two promotions (the THIRD and FOURTH strict ones
        # since M-C1): both must end up implemented and neither reserved. Fails the way
        # a half-done promotion fails - a name in BOTH sets, or in neither.
        for verb in ("StartLoopPlayback", "EnterWatchMode"):
            with self.subTest(verb=verb):
                self.assertIn(verb, hlib.IMPLEMENTED_SEAM_VERBS)
                self.assertNotIn(verb, hlib.RESERVED_SEAM_VERBS)
        # StopPlayback is the deliberate hold-out beside them: teardown is
        # FlushAndQuit's job, so a stop verb would be a second, weaker owner of it.
        self.assertIn("StopPlayback", hlib.RESERVED_SEAM_VERBS)
        self.assertNotIn("StopPlayback", hlib.IMPLEMENTED_SEAM_VERBS)

    def test_player_workflow_steps_are_no_longer_rejected_as_reserved(self):
        # The BEHAVIOURAL half of both promotions: before this lane a spec naming
        # either verb failed validation with "is RESERVED, not v1-drivable", which is
        # what made the warp-and-watch player loop unauthorable. Fails if either
        # promotion is cosmetic.
        for verb, args in (("StartLoopPlayback", {"tree": "tree-1"}),
                           ("EnterWatchMode", {})):
            with self.subTest(verb=verb):
                def m(s):
                    s.get("expectations", {}).pop("ledger", None)
                    s["driver"]["steps"].insert(
                        1, {"cmd": verb, "args": args, "expect": "OK"})
                v = self._reject(m)
                self.assertFalse(any(verb in e for e in v.errors),
                                 "%s wrongly flagged: %s" % (verb, list(v.errors)))

    def test_player_workflow_deferral_shape_mirrors_c_sharp(self):
        # StartLoopPlayback is a forward CLOCK jump, so it joins DEFERRED_SEAM_VERBS
        # (the 540 s cap must govern any spec-declared budget) and mirrors the C#
        # StartLoopPlaybackSeconds = 120. EnterWatchMode is two-phase but its
        # completion is a camera read-back, so it stays OUT and rides the 60 s default
        # - the same call SimulateStockSwitchClick's absence records.
        self.assertIn("StartLoopPlayback", hlib.DEFERRED_SEAM_VERBS)
        self.assertNotIn("EnterWatchMode", hlib.DEFERRED_SEAM_VERBS)
        self.assertEqual(hlib.dispatch_deferral_budget("StartLoopPlayback"), 120.0)
        self.assertEqual(hlib.dispatch_deferral_budget("EnterWatchMode"), 60.0)
        self.assertLess(hlib.dispatch_deferral_budget("StartLoopPlayback"),
                        hlib.MAX_DEFERRED_STEP_BUDGET_SECONDS)

    def test_r12_typed_refusals_map_to_finer_driver_subkinds(self):
        # Both verbs ship a typed refusal taxonomy; without these rows every one of them
        # collapses to the coarse driver-verdict-mismatch and the taxonomy is decorative
        # harness-side. Retryability is unchanged either way - this refines WHICH
        # driver-* subkind the report names. The wire msg is percent-encoded, so a
        # compound reason arrives with its detail after %20 and matches on the head.
        for msg, expected in (
                ("scene-arg-invalid%20scene%3DTRACKSTATION", "driver-arg"),
                ("dialog-required%20variant%3DRegularMerge", "driver-gate"),
                ("dialog-required%20case%3DA-session", "driver-gate"),
                ("site-arg-invalid%20site%3DMAP", "driver-arg"),
                ("site-not-implemented%20site%3Dts", "driver-arg"),
                ("target-arg-missing", "driver-arg"),
                ("pid-arg-invalid%20pid%3Dxyz", "driver-arg"),
                ("vessel-arg-invalid%20vessel%3D", "driver-arg"),
                ("target-not-found%20vessel%3DNope", "driver-arg"),
                ("target-name-ambiguous%20vessel%3DPod%20matches%3D2", "driver-arg"),
                ("target-is-ghost%20pid%3D7", "driver-arg"),
                ("scenario-not-ready", "driver-gate"),
                ("cannot-switch-vessels-far", "driver-gate"),
                ("target-already-active", "driver-gate"),
                ("target-unloaded", "driver-gate"),
                ("dialog-pending", "driver-dialog")):
            with self.subTest(msg=msg):
                self.assertEqual(expected, hlib.classify_seam_refusal_subkind(msg))
                self.assertIn(expected, hlib.RETRYABLE_INVALID_SUBKINDS)
        # The two POST-arm ERROR terminals are NOT refusals: they stay unmapped and
        # ride the coarse driver-verdict-mismatch, because the verb ACTED.
        for msg in ("switch-threw", "switch-refused-by-stock"):
            with self.subTest(msg=msg):
                self.assertEqual("", hlib.classify_seam_refusal_subkind(msg))

    def test_loop_lane_typed_refusals_map_to_finer_driver_subkinds(self):
        # Same statement as the R12 cell, for the three loop-lane verbs. MissionConfig
        # landed with NO rows at all (the pre-existing gap); StartLoopPlayback and
        # EnterWatchMode are swept in with it. Every one of these is a REFUSAL, so a
        # missing row costs the report the finer subkind and nothing else - which is
        # exactly the silent decay this cell exists to catch.
        for msg, expected in (
                # StartLoopPlayback, arg half.
                ("tree-arg-missing", "driver-arg"),
                ("unknown-tree%20tree%3Ddeadbeef", "driver-arg"),
                # StartLoopPlayback, gate half.
                ("loop-not-armed", "driver-gate"),
                ("unit-not-built%20tree%3Dccb5e4af", "driver-gate"),
                ("no-next-window%20tree%3Dccb5e4af", "driver-gate"),
                ("window-not-forward%20tree%3Dccb5e4af%20relaunchUt%3D1", "driver-gate"),
                ("no-flight-instance", "driver-gate"),
                # EnterWatchMode.
                ("index-arg-invalid%20index%3Dxyz", "driver-arg"),
                ("index-out-of-range%20index%3D9%20committed%3D3", "driver-arg"),
                ("no-watchable-ghost%20committed%3D3%20tree%3D(any)", "driver-gate"),
                ("watch-not-entered", "driver-gate"),
                # MissionConfig.
                ("loop-arg-invalid%20loop%3Dyes", "driver-arg"),
                ("interval-arg-invalid%20intervalSeconds%3D-1", "driver-arg")):
            with self.subTest(msg=msg):
                self.assertEqual(expected, hlib.classify_seam_refusal_subkind(msg))
                self.assertIn(expected, hlib.RETRYABLE_INVALID_SUBKINDS)
        # THE SHARED KEYS. The table is keyed by msg token alone, so one row serves
        # every verb that emits the token - including `unknown-tree`, which is
        # REJECTED on StartLoopPlayback / EnterWatchMode but ERROR on MissionConfig.
        # The subkind is verdict-independent by construction: classify reads the msg
        # and never the verdict, so both spellings of the same lookup miss land on
        # driver-arg and both retry once. (Spec authors still have to match `expect`
        # per verb - that is the design doc's note, not this table's job.)
        for shared in ("tree-arg-missing", "unknown-tree", "no-flight-instance"):
            with self.subTest(shared=shared):
                self.assertIn(shared, hlib._SEAM_REFUSAL_SUBKINDS)
        # StartLoopPlayback's post-arm terminal is NOT a refusal, same rule as
        # switch-threw above: the jump was initiated and the clock never landed.
        self.assertEqual("", hlib.classify_seam_refusal_subkind("jump-timeout"))

    def test_r12_loadgame_scene_arg_is_validated_pre_launch(self):
        # The seam's scene= parse is fail-closed and case-sensitive, so a typo is a typed
        # REJECTED - but only after a whole KSP boot. Catch it in validate_spec instead,
        # exactly as the `isolated` guard does.
        def _scene_step(value, cmd="LoadGame"):
            def m(s):
                s.get("expectations", {}).pop("ledger", None)
                s["driver"]["steps"].insert(
                    1, {"cmd": cmd, "args": {"scene": value}, "expect": "OK"})
            return m

        for good in ("spacecenter", "trackstation"):
            with self.subTest(scene=good):
                v = self._reject(_scene_step(good))
                self.assertFalse(any("args.scene" in e for e in v.errors),
                                 "%r wrongly flagged: %s" % (good, list(v.errors)))
        # Rejected spellings, including the two the C# doc calls out by name and
        # `flight`, which is deliberately NOT an accepted value (a forced FLIGHT boot is
        # not expressible and would widen known-gate 6).
        for bad in ("TRACKSTATION", "ts", "ksc", "flight", ""):
            with self.subTest(scene=bad):
                v = self._reject(_scene_step(bad))
                self.assertTrue(any("args.scene" in e for e in v.errors),
                                "%r not rejected: %s" % (bad, list(v.errors)))
        # The arg on a verb that does not read it is silently inert on the wire.
        v = self._reject(_scene_step("trackstation", cmd="RecordingState"))
        self.assertTrue(any("args.scene" in e and "only the LoadGame verb" in e
                            for e in v.errors),
                        "scene= on a non-LoadGame step not rejected: %s" % list(v.errors))

        # A case-variant KEY would be sent and silently ignored (the C# lookup is an
        # exact dictionary hit), so it is caught on the KEY, not the value.
        def key_variant(s):
            s.get("expectations", {}).pop("ledger", None)
            s["driver"]["steps"].insert(
                1, {"cmd": "LoadGame", "args": {"Scene": "trackstation"}, "expect": "OK"})
        v = self._reject(key_variant)
        self.assertTrue(any("args.Scene" in e for e in v.errors),
                        "case-variant scene key not rejected: %s" % list(v.errors))

    def test_r12_switchclick_site_arg_is_validated_pre_launch(self):
        # Same treatment as scene=, with one deliberate difference: `ts` / `ksc` are LEGAL
        # spellings v1 answers REJECTED site-not-implemented, so a capability-probe spec
        # driving one with expect = "REJECTED" must still validate. Only the SPELLING is
        # closed here, never the implementedness.
        def _site_step(value, cmd="SimulateStockSwitchClick", expect="OK"):
            def m(s):
                s.get("expectations", {}).pop("ledger", None)
                s["driver"]["steps"].insert(
                    1, {"cmd": cmd, "args": {"site": value, "vessel": "Test Craft"},
                        "expect": expect})
            return m

        for good in ("map", "ts", "ksc"):
            with self.subTest(site=good):
                v = self._reject(_site_step(good, expect="REJECTED"))
                self.assertFalse(any("args.site" in e for e in v.errors),
                                 "%r wrongly flagged: %s" % (good, list(v.errors)))
        for bad in ("MAP", "trackstation", "flight", ""):
            with self.subTest(site=bad):
                v = self._reject(_site_step(bad))
                self.assertTrue(any("args.site" in e for e in v.errors),
                                "%r not rejected: %s" % (bad, list(v.errors)))
        v = self._reject(_site_step("map", cmd="RecordingState"))
        self.assertTrue(any("args.site" in e and
                            "only the SimulateStockSwitchClick verb" in e
                            for e in v.errors),
                        "site= on a non-switchclick step not rejected: %s" % list(v.errors))

    def test_runtests_strict_arg_is_validated_pre_launch(self):
        # career-ledger B.4. The `strict` arg is the per-scenario seam for
        # LedgerGroundTruthDiff.StrictPerIdentityForTesting, and its C# parse
        # (TestCommandRunTests.TryParseStrictArg) is TryParseIsolatedArg's contract
        # verbatim - fail-closed and case-sensitive. So the three ways of getting it
        # wrong are the same three, and each must red here rather than after a boot.
        #
        # The strict/non-strict difference is INVISIBLE in the tally: `strict` changes
        # how one in-game cell classifies divergences it already found, not which tests
        # run. A spec that meant to arm it and misspelled it would therefore fly, green,
        # having asserted the looser thing - which is precisely why the spelling gate is
        # the whole harness-side contract for this arg.
        def _strict(value):
            # MUTATE the existing RunTests step rather than insert a second one:
            # SINGLE_BATCH_SELECTOR_RULE permits only one batch selector, and a spurious
            # second-selector error would mask the arg error this cell is reading.
            def m(s):
                for step in s["driver"]["steps"]:
                    if step.get("cmd") == "RunTests":
                        step.setdefault("args", {})["strict"] = value
            return m

        for good in ("true", "false"):
            with self.subTest(strict=good):
                v = self._reject(_strict(good))
                self.assertFalse(any("args.strict" in e for e in v.errors),
                                 "%r wrongly flagged: %s" % (good, list(v.errors)))
        # `True` is the exact spelling a TOML bool puts on the wire
        # (run.py::encode_value is str(value)), and the C# parse REJECTS it.
        for bad in ("True", "False", "TRUE", "1", "yes", ""):
            with self.subTest(strict=bad):
                v = self._reject(_strict(bad))
                self.assertTrue(any("args.strict" in e for e in v.errors),
                                "%r not rejected: %s" % (bad, list(v.errors)))

        # The arg on a verb that does not read it is silently inert on the wire, and
        # inert in the unsafe direction: the author believes the diff is strict.
        def wrong_verb(s):
            s["driver"]["steps"].insert(
                1, {"cmd": "RecordingState", "args": {"strict": "true"}, "expect": "OK"})
        v = self._reject(wrong_verb)
        self.assertTrue(any("args.strict" in e and "only the RunTests verb" in e
                            for e in v.errors),
                        "strict= on a non-RunTests step not rejected: %s" % list(v.errors))

        # A case-variant KEY is sent verbatim and missed by the C# exact dictionary
        # lookup, so it is caught on the KEY rather than on the value.
        v = self._reject(lambda s: [
            step.setdefault("args", {}).__setitem__("Strict", "true")
            for step in s["driver"]["steps"] if step.get("cmd") == "RunTests"])
        self.assertTrue(any("args.Strict" in e for e in v.errors),
                        "case-variant strict key not rejected: %s" % list(v.errors))

    # The armed set for RunTests' `strict` arg, and the SUBJECT each row stands on.
    # Same shape and same discipline as the saveParse allowlist next door: the
    # roster lives HERE rather than in a doc, so arming costs a deliberate edit in
    # the file whose test reds when someone arms without one.
    #
    # THE NAME AVOIDS THE SUBSTRING `ARMED_ALLOWLIST` ON PURPOSE, and a future
    # sibling roster must do the same. `Cl3SpecArmedTests
    # .test_it_is_on_the_save_structure_armed_allowlist` reads the save-structure
    # roster OUT OF THIS FILE'S SOURCE with `re.search(r"ARMED_ALLOWLIST\s*=\s*\{
    # ([^}]*)\}")`, which takes the FIRST match - so a second constant whose name
    # ended in `ARMED_ALLOWLIST` and sat above it would silently hand CL-3 the wrong
    # list. It did exactly that on the first draft of this roster.
    RUNTESTS_STRICT_ARMED_SPECS = {
        # career-ledger B.4, ARMED 2026-08-20 (wave C). Subject:
        # `career-earned-pad` - the save harness run
        # `2026-08-19_2130_L3-career-science-recover` produced (a driven career that
        # EARNED flight science and RECOVERED a crewed craft), with a PRELAUNCH
        # vessel spliced in so the FLIGHT-scene cell can run at all. It carries the
        # populated per-identity facets the 2026-08-17 deferral was waiting for -
        # three science subjects, a vessel-recovery credit, five milestones and a
        # kerbal career log - and all three of its pools reproduce to float noise
        # (`C2CareerPostFixReplayTests`, the closes-to-zero proof).
        "L4-ledger-groundtruth-strict.toml",
    }

    def test_only_the_strict_allowlist_arms_the_runtests_strict_arg(self):
        # THE B.4 DEFERRAL, DISCHARGED AND REPLACED IN PLACE. Its history, kept
        # because the reasoning is what makes the current row readable:
        #
        #   2026-08-17  DEFERRED. `strict` promotes the ground-truth diff's
        #     report-only per-identity divergences to hard failures, and the one
        #     committed spec that drove that diff (L2-ledger-groundtruth-career)
        #     measured `reportOnly=0` on career-pad-craft - nothing to promote, so
        #     arming THERE added no coverage for a value-drift regression. It would
        #     still have caught a recon-invents-an-identity regression, so the gate
        #     was never inert; the SUBJECT was what was thin. The close condition
        #     this cell named was explicit: "a subject with populated per-identity
        #     facets ... or any future career fixture with recorded crewed
        #     recoveries".
        #   2026-08-20  MET, and armed on the subject named in the allowlist above.
        #
        # The cell is REWRITTEN rather than deleted, and that is the change of
        # posture worth noticing: a bare "nobody arms this" fence retires the moment
        # anyone does, taking its evidence trail with it. An allowlist keeps the
        # question open forever - a SECOND spec arming strict still reds here, and
        # still has to record its own subject before it can go green.
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            for step in ((spec.get("driver") or {}).get("steps") or []):
                if ((step or {}).get("args") or {}).get(hlib.RUNTESTS_STRICT_KEY) is not None:
                    armed.append(name)
        self.assertEqual(sorted(self.RUNTESTS_STRICT_ARMED_SPECS), sorted(set(armed)),
                         "a committed spec declares RunTests strict= with no recorded "
                         "subject justifying it (career-ledger B.4). Add it to "
                         "RUNTESTS_STRICT_ARMED_SPECS with the subject and the reading run "
                         "that justified it - never widen the assertion.")

    def test_every_strict_armed_spec_pins_the_strict_true_token(self):
        # CLAIM-IS-NOT-GATE, for the arming itself. A spec may sit in the allowlist
        # only if its own log contract pins `strict=True` - the ground-truth cell's
        # own echo of the flag it ran under. Without that token an armed spec is
        # indistinguishable from an unarmed one at read time, and a run that greened
        # on an automation DLL predating the seam would look identical to a real one.
        for name in sorted(self.RUNTESTS_STRICT_ARMED_SPECS):
            path = os.path.join(SCENARIOS_DIR, name)
            self.assertTrue(os.path.isfile(path),
                            "RUNTESTS_STRICT_ARMED_SPECS names a spec that does not "
                            "exist: %s" % name)
            with open(path, "rb") as fh:
                spec = tomllib.load(fh)
            required = (((spec.get("expectations") or {})
                         .get("logContracts") or {}).get("required") or [])
            self.assertTrue(any("strict=True" in tok for tok in required),
                            "%s arms strict but pins no strict=True token: %s"
                            % (name, required))

    def test_eva4_chute_verb_is_deferred_and_capped(self):
        # EVA-4: EvaChuteDeploy holds the FIFO head through the kerbal's whole chuted
        # descent, so it MUST be in the deferred family (the 540 s per-step cap governs
        # it) - a spec step above the cap is rejected, one at the cap is accepted.
        self.assertIn("EvaChuteDeploy", hlib.DEFERRED_SEAM_VERBS)

        def over(s):
            s.get("expectations", {}).pop("ledger", None)
            s["driver"]["steps"].insert(
                1, {"cmd": "EvaChuteDeploy", "expect": "OK",
                    "budget": hlib.MAX_DEFERRED_STEP_BUDGET_SECONDS + 1})
        v = self._reject(over)
        self.assertTrue(any("EvaChuteDeploy" in e and "540" in e for e in v.errors),
                        "over-cap EvaChuteDeploy budget not rejected: %s" % list(v.errors))

        def at_cap(s):
            s.get("expectations", {}).pop("ledger", None)
            s["driver"]["steps"].insert(
                1, {"cmd": "EvaChuteDeploy", "expect": "OK",
                    "budget": hlib.MAX_DEFERRED_STEP_BUDGET_SECONDS})
        v = self._reject(at_cap)
        self.assertFalse(any("EvaChuteDeploy" in e for e in v.errors),
                         "at-cap EvaChuteDeploy budget wrongly flagged: %s" % list(v.errors))

    def test_mc2_eva_verb_step_accepted(self):
        # A spec step using an EVA verb is not flagged RESERVED / unknown.
        for verb in ("EvaExit", "EvaBoard", "PlantFlag", "EvaChuteDeploy"):
            with self.subTest(verb=verb):
                def m(s):
                    s.get("expectations", {}).pop("ledger", None)
                    s["driver"]["steps"].insert(1, {"cmd": verb, "expect": "OK", "budget": 120})
                v = self._reject(m)
                self.assertFalse(any(verb in e for e in v.errors),
                                 "%s wrongly flagged: %s" % (verb, list(v.errors)))

    def test_mc2_eva_dispatch_budgets_mirror_c_sharp(self):
        # F5: the per-verb dispatch deferral budgets mirror the C# DeferralBudget table
        # (120 / 180 / 120). Without them the harness step-wait would ride the 60s default
        # and could KILL a genuinely-deferring PlantFlag at ~120s.
        self.assertEqual(hlib.dispatch_deferral_budget("EvaExit"), 120.0)
        self.assertEqual(hlib.dispatch_deferral_budget("PlantFlag"), 180.0)
        self.assertEqual(hlib.dispatch_deferral_budget("EvaBoard"), 120.0)
        # None is a two-phase DEFERRED_SEAM_VERB (all under the 540s cap).
        for verb in ("EvaExit", "PlantFlag", "EvaBoard"):
            self.assertNotIn(verb, hlib.DEFERRED_SEAM_VERBS)
            self.assertLess(hlib.dispatch_deferral_budget(verb), hlib.MAX_DEFERRED_STEP_BUDGET_SECONDS)

    def test_ledger_with_rewind_or_dialog_rejected(self):
        # Item 1: an [expectations.ledger] block cannot pair with InvokeRewind /
        # AnswerMergeDialog (a rewind/merge rewrites the career pools the seed+manifest
        # cannot model; the oracle is DEFERRED to L4). The base already declares ledger.
        for verb in ("InvokeRewind", "AnswerMergeDialog"):
            with self.subTest(verb=verb):
                def m(s):
                    s["driver"]["steps"].insert(1, {"cmd": verb, "expect": "OK"})
                v = self._reject(m)
                self.assertFalse(v.ok)
                self.assertTrue(
                    any(verb in e and "L4" in e and "ledger" in e for e in v.errors),
                    "expected ledger+%s L4-deferral rejection: %s" % (verb, list(v.errors)))

    def test_ledger_with_timejump_allowed(self):
        # Item 1: TimeJump + ledger stays design-blessed (a forward jump keeps the
        # seed+manifest sum valid); only rewind/merge-dialog are rejected.
        def m(s):
            s["driver"]["steps"].insert(1, {"cmd": "TimeJump", "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(any("cannot pair with [expectations.ledger]" in e for e in v.errors),
                         "TimeJump+ledger must be allowed: %s" % list(v.errors))

    def test_mc1_deferred_verb_membership(self):
        # InvokeRewind + TimeJump are the two two-phase verbs the 540s cap governs;
        # AnswerMergeDialog + KscAction are bounded-wait but quick (NOT deferred).
        self.assertIn("InvokeRewind", hlib.DEFERRED_SEAM_VERBS)
        self.assertIn("TimeJump", hlib.DEFERRED_SEAM_VERBS)
        self.assertNotIn("AnswerMergeDialog", hlib.DEFERRED_SEAM_VERBS)
        self.assertNotIn("KscAction", hlib.DEFERRED_SEAM_VERBS)

    def test_mc1_deferred_verb_budget_cap(self):
        # A deferred M-C1 verb step over the 540s cap is rejected (S8).
        for verb in ("InvokeRewind", "TimeJump"):
            with self.subTest(verb=verb):
                def m(s):
                    s["driver"]["steps"].insert(1, {"cmd": verb, "expect": "OK", "budget": 600})
                v = self._reject(m)
                self.assertFalse(v.ok)
                self.assertTrue(any(verb in e and "540" in e for e in v.errors),
                                "expected S8 budget-cap error for %s: %s" % (verb, list(v.errors)))

    def test_mc1_invalid_subkinds_retryable(self):
        # Every M-C1 verb refusal is a DRIVER problem: retry-once, never PARSEK-FAIL.
        for sk in ("driver-gate", "driver-rewind", "driver-dialog", "driver-arg", "driver-career"):
            with self.subTest(subkind=sk):
                self.assertIn(sk, hlib.RETRYABLE_INVALID_SUBKINDS)

    def test_both_batch_owners_rejected(self):
        def m(s):
            s["driver"]["autorun"] = {"tests": "RecordingInvariants", "exit": False}
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("BATCH owner" in e for e in v.errors))

    def test_neither_batch_owner_when_required(self):
        # Drop the RunTests step but keep the BATCH_COMPLETE required pattern.
        def m(s):
            s["driver"]["steps"] = [
                {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"}, "expect": "OK", "budget": 300},
                {"cmd": "FlushAndQuit", "expect": "OK"},
            ]
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("BATCH owner" in e and "none" in e for e in v.errors))

    def test_both_quit_owners_rejected(self):
        def m(s):
            s["driver"]["autorun"] = {"tests": "", "exit": True}  # exit owner
            # keep the FlushAndQuit step too -> two quit owners
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("QUIT owner" in e for e in v.errors))

    def test_neither_quit_owner_rejected(self):
        def m(s):
            s["driver"]["steps"] = [x for x in s["driver"]["steps"] if x.get("cmd") != "FlushAndQuit"]
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("QUIT owner" in e and "neither" in e for e in v.errors))

    def test_first_step_not_loadgame(self):
        def m(s):
            s["driver"]["steps"][0] = {"cmd": "SetSetting", "args": {"name": "x", "value": "y"}, "expect": "OK"}
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("must be LoadGame" in e for e in v.errors))

    def test_loadgame_save_arg_mismatch(self):
        def m(s):
            s["driver"]["steps"][0]["args"]["save"] = "some-other-save"
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("LoadGame save" in e for e in v.errors))

    def test_loadgame_literal_runsavename_accepted(self):
        # A literal equal to runSaveName (the saveTemplate leaf) is valid (S3).
        def m(s):
            s["driver"]["steps"][0]["args"]["save"] = "fresh-career"
        v = self._reject(m)
        self.assertTrue(v.ok, "literal runSaveName save arg must be accepted; errors=%s" % (v.errors,))

    def test_empty_save_template_rejected(self):
        # S1: an empty saveTemplate leaf is not a filename-safe runSaveName.
        v = self._reject(lambda s: s["fixture"].__setitem__("saveTemplate", ""))
        self.assertFalse(v.ok)
        self.assertTrue(any("runSaveName" in e and "filename-safe" in e for e in v.errors))

    def test_dotdot_save_template_rejected(self):
        # S1: a ".." leaf would stage into saves/.. (an rmtree escape).
        v = self._reject(lambda s: s["fixture"].__setitem__("saveTemplate", "fixtures/saves/.."))
        self.assertFalse(v.ok)
        self.assertTrue(any("runSaveName" in e and "filename-safe" in e for e in v.errors))

    def test_absolute_save_template_rejected(self):
        # S1: an absolute saveTemplate makes the copytree source arbitrary.
        v = self._reject(lambda s: s["fixture"].__setitem__("saveTemplate", "/etc/evil"))
        self.assertFalse(v.ok)
        self.assertTrue(any("saveTemplate" in e and "absolute" in e for e in v.errors))

    def test_injected_recordings_out_of_set(self):
        v = self._reject(lambda s: s["fixture"].__setitem__("injectedRecordings", "some-preset"))
        self.assertFalse(v.ok)
        self.assertTrue(any("injectedRecordings" in e for e in v.errors))

    def test_runtests_budget_over_cap(self):
        def m(s):
            for step in s["driver"]["steps"]:
                if step.get("cmd") == "RunTests":
                    step["budget"] = 600
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("budget" in e and "540" in e for e in v.errors))

    def test_expect_interrupted_rejected(self):
        def m(s):
            s["driver"]["steps"][0]["expect"] = "INTERRUPTED"
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("expect" in e and "INTERRUPTED" in e for e in v.errors))

    def test_dangling_bugid_warns_not_fails(self):
        def m(s):
            s["expectedFail"]["bugId"] = "R99-does-not-exist"
        spec = copy.deepcopy(self.base)
        m(spec)
        v = hlib.validate_spec(spec, self.reg, bug_ids=["R1-known"])
        self.assertTrue(v.ok, "a dangling bugId must WARN, not hard-fail")
        self.assertTrue(any("dangling" in w or "not resolvable" in w for w in v.warnings))


# ---------------------------------------------------------------------------
# In-game batch anti-vacuity gate.
# ---------------------------------------------------------------------------


class BatchVacuityGateTests(unittest.TestCase):
    """The regression this exists for, verbatim from the live instance
    (2026-07-26): B10-career-passive-safety shipped at daily tier and read GREEN
    while executing ZERO tests, for as long as it had been running.

        BATCH_COMPLETE v1 total=2 passed=0 failed=0 skipped=2 category=RecordingInvariants scene=SPACECENTER
        Scene eligibility skip summary: skipped=2 currentScene=SPACECENTER byRequiredScene=FLIGHT:2

    Both RecordingInvariants tests are Scene = FLIGHT; the fresh-career fixture has
    no VESSEL nodes so LoadGame routes to SPACECENTER; both were scene-skipped; and
    the spec's only batch contract was ``BATCH_COMPLETE v1 .* failed=0\\b``, which
    that line satisfies. Fails if the gate ever stops rejecting a contract an empty
    or all-skipped batch could satisfy, or starts rejecting one that discriminates.
    """

    def setUp(self):
        self.reg = load_registry()

    # ----- the pure probe / gap functions -----

    def test_bare_failed_zero_pin_is_a_gap(self):
        gap = hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 .* failed=0\\b"], "RecordingInvariants")
        self.assertIsNotNone(gap, "failed=0 alone cannot tell 2 passes from 2 skips")
        self.assertIn("passed=0", gap)

    def test_the_exact_shipped_b10_line_is_among_the_probes(self):
        # Not a paraphrase: the literal tally the live instance emitted must be one
        # of the lines the gate probes for, or the gate would not have caught it.
        probes = hlib.vacuous_batch_complete_probes(
            "RecordingInvariants", ["BATCH_COMPLETE v1 .* failed=0\\b"])
        self.assertIn(
            "BATCH_COMPLETE v1 total=2 passed=0 failed=0 skipped=2 "
            "category=RecordingInvariants scene=SPACECENTER", probes)

    def test_failed_and_skipped_zero_pin_is_still_a_gap(self):
        # H6's pre-fix contract. skipped=0 catches an all-skipped batch but NOT an
        # EMPTY one (total=0 passed=0 failed=0 skipped=0), which a renamed or
        # mis-typed category selector produces.
        gap = hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 .* failed=0 skipped=0\\b"], "RouteRewindTimeline")
        self.assertIsNotNone(gap)
        self.assertIn("total=0 passed=0 failed=0 skipped=0", gap)

    def test_total_only_pin_is_a_gap(self):
        # A total= pin without a passed= pin still accepts the all-skipped tally at
        # that total -- the reason the rule is not "must mention total=".
        gap = hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 total=12 .* failed=0\\b"], "Missions")
        self.assertIsNotNone(gap)
        self.assertIn("total=12 passed=0", gap)

    def test_whole_tally_pin_discriminates(self):
        self.assertIsNone(hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 total=12 passed=5 failed=0 skipped=7 "
             "category=Missions scene=SPACECENTER"], "Missions"))

    def test_nonzero_passed_class_pin_discriminates(self):
        # The weaker but honest form used where the exact split is not yet measured.
        self.assertIsNone(hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 total=42 passed=[1-9][0-9]* failed=0 skipped=[0-9]+ "
             "category=GhostPlayback scene=FLIGHT"], "GhostPlayback"))

    def test_pin_above_the_enumeration_bound_still_discriminates(self):
        # A total larger than the bounded all-skipped sweep is probed at exactly the
        # value the pattern itself names, so a big-category pin cannot slip through.
        big = 1024
        self.assertGreater(big, hlib._BATCH_PROBE_MAX_SKIPPED)
        gap = hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 total=%d .* failed=0\\b" % big], "Huge")
        self.assertIsNotNone(gap)
        self.assertIn("total=%d passed=0" % big, gap)

    def test_no_batch_complete_pattern_at_all_is_a_gap(self):
        gap = hlib.batch_contract_vacuity_gap(["Recording started"], "Missions")
        self.assertIsNotNone(gap)
        self.assertIn("BATCH_COMPLETE", gap)

    def test_probe_matches_prefixed_and_bare_lines(self):
        # A contract anchored on the KSP.log prefix and one anchored at line start
        # must BOTH be exercised; neither anchoring style is a silent exemption.
        for pat in (r"\[Parsek\]\[INFO\]\[TestRunner\] BATCH_COMPLETE v1 .* failed=0\b",
                    r"^BATCH_COMPLETE v1 .* failed=0\b"):
            with self.subTest(pattern=pat):
                self.assertIsNotNone(hlib.batch_contract_vacuity_gap([pat], "Missions"))

    def test_off_scene_vacuous_batch_is_rejected_by_a_scene_pin(self):
        # The B10 shape exactly: the spec expected FLIGHT, the fixture delivered
        # SPACECENTER. A scene-pinned contract reds that even before the counts.
        probes = hlib.vacuous_batch_complete_probes("RecordingInvariants", [])
        pinned = ("BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                  "category=RecordingInvariants scene=FLIGHT")
        self.assertTrue(any("scene=SPACECENTER" in p for p in probes))
        self.assertIsNone(hlib.batch_contract_vacuity_gap([pinned], "RecordingInvariants"))

    # ----- validate_spec wiring -----

    def _spec_with_contract(self, required, base="H5-invariants-corpus.toml"):
        spec = copy.deepcopy(load_spec(base))
        spec["expectations"]["logContracts"]["required"] = list(required)
        return spec

    def test_spec_with_only_failed_zero_is_rejected(self):
        v = hlib.validate_spec(
            self._spec_with_contract(["BATCH_COMPLETE v1 .* failed=0\\b"]), self.reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("cannot detect a vacuous batch" in e for e in v.errors),
                        list(v.errors))

    def test_spec_with_total_and_passed_pin_is_accepted(self):
        v = hlib.validate_spec(
            self._spec_with_contract([
                "BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                "category=RecordingInvariants scene=FLIGHT"]), self.reg)
        self.assertTrue(v.ok, list(v.errors))

    def test_spec_with_no_runtests_step_is_unaffected(self):
        # A seam-only scenario owns no batch, so the gate must not fire on it at all
        # (and its batchComplete verifier is correctly SKIPPED at run time).
        spec = copy.deepcopy(load_spec("S0.5-live-record-discard.toml"))
        self.assertFalse(any((s or {}).get("cmd") == "RunTests"
                             for s in spec["driver"]["steps"]))
        v = hlib.validate_spec(spec, self.reg)
        self.assertTrue(v.ok, list(v.errors))
        self.assertFalse(any("vacuous" in e for e in v.errors))

    def test_opt_out_requires_a_reason(self):
        spec = self._spec_with_contract(["BATCH_COMPLETE v1 .* failed=0\\b"])
        spec["expectations"]["logContracts"][hlib.BATCH_VACUITY_OPT_OUT_KEY] = True
        v = hlib.validate_spec(spec, self.reg)
        self.assertFalse(v.ok)
        self.assertTrue(any(hlib.BATCH_VACUITY_OPT_OUT_REASON_KEY in e for e in v.errors),
                        list(v.errors))

    def test_opt_out_with_reason_accepts_the_weak_contract(self):
        spec = self._spec_with_contract(["BATCH_COMPLETE v1 .* failed=0\\b"])
        lc = spec["expectations"]["logContracts"]
        lc[hlib.BATCH_VACUITY_OPT_OUT_KEY] = True
        lc[hlib.BATCH_VACUITY_OPT_OUT_REASON_KEY] = "documented reason"
        v = hlib.validate_spec(spec, self.reg)
        self.assertTrue(v.ok, list(v.errors))

    def test_opt_out_must_be_a_bool(self):
        spec = self._spec_with_contract([
            "BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
            "category=RecordingInvariants scene=FLIGHT"])
        spec["expectations"]["logContracts"][hlib.BATCH_VACUITY_OPT_OUT_KEY] = "false"
        v = hlib.validate_spec(spec, self.reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("must be a bool" in e for e in v.errors), list(v.errors))

    def test_misplaced_opt_out_key_is_rejected_not_silently_ignored(self):
        # TOML scoping trap: written outside [expectations.logContracts] the flag is
        # inert, and this one fails UNSAFE (the author thinks they waived the gate).
        for scope in ("expectations", "root", "driver"):
            with self.subTest(scope=scope):
                spec = self._spec_with_contract([
                    "BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                    "category=RecordingInvariants scene=FLIGHT"])
                target = (spec["expectations"] if scope == "expectations"
                          else spec if scope == "root" else spec["driver"])
                target[hlib.BATCH_VACUITY_OPT_OUT_KEY] = True
                v = hlib.validate_spec(spec, self.reg)
                self.assertFalse(v.ok)
                self.assertTrue(
                    any("belongs in [expectations.logContracts]" in e for e in v.errors),
                    list(v.errors))

    def test_opt_out_on_a_batchless_spec_warns_inert(self):
        spec = copy.deepcopy(load_spec("S0.5-live-record-discard.toml"))
        spec["expectations"]["logContracts"][hlib.BATCH_VACUITY_OPT_OUT_KEY] = True
        v = hlib.validate_spec(spec, self.reg)
        self.assertTrue(v.ok, list(v.errors))
        self.assertTrue(any("inert" in w for w in v.warnings), list(v.warnings))

    def test_every_committed_batch_spec_pins_a_nonvacuous_tally(self):
        """Repo-wide: no committed spec may own a batch it cannot gate. Discovered,
        never hardcoded, so a scenario added later is covered automatically."""
        checked = []
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            spec = load_spec(name)
            steps = (spec.get("driver", {}) or {}).get("steps", []) or []
            selector = next((((s or {}).get("args", {}) or {}).get("category")
                             for s in steps if (s or {}).get("cmd") == "RunTests"), None)
            if not any((s or {}).get("cmd") == "RunTests" for s in steps):
                continue
            checked.append(name)
            lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
            if lc.get(hlib.BATCH_VACUITY_OPT_OUT_KEY) is True:
                continue
            self.assertIsNone(
                hlib.batch_contract_vacuity_gap(lc.get("required", []) or [], selector),
                "%s: batch contract accepts a vacuous tally" % name)
        self.assertTrue(checked, "no committed RunTests spec found - the sweep is inert")


class BatchVacuityGateShapeTests(unittest.TestCase):
    """The three ways a spec could satisfy the gate and STILL admit a vacuous batch,
    found by adversarial review 2026-07-26 and closed here. Each cell is the
    reviewer's counterexample verbatim; each must now be REJECTED.
    """

    def setUp(self):
        self.reg = load_registry()
        self.pin = ["BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                    "category=RecordingInvariants scene=FLIGHT"]

    def _h5(self):
        spec = copy.deepcopy(load_spec("H5-invariants-corpus.toml"))
        spec["expectations"]["logContracts"]["required"] = list(self.pin)
        return spec

    def test_second_runtests_step_is_rejected(self):
        # Dodge 1: validate_spec probed only the FIRST category and batch_owners
        # counted 1 for any n>0, so a whole-tally pin on the first batch left a
        # second `RunTests GhostPlayback` batch completely ungated.
        spec = self._h5()
        steps = spec["driver"]["steps"]
        idx = next(i for i, s in enumerate(steps) if (s or {}).get("cmd") == "RunTests")
        steps.insert(idx + 1, {"cmd": "RunTests", "args": {"category": "GhostPlayback"},
                               "expect": "OK", "budget": 540})
        v = hlib.validate_spec(spec, self.reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("2 RunTests steps declared" in e for e in v.errors), list(v.errors))

    def test_multi_category_selector_is_rejected(self):
        # Dodge 2: a pin naming ONE constituent rejects the other constituent's
        # probes for the wrong reason (category-token mismatch), so the gate reported
        # no gap while category B could run all-skipped.
        spec = self._h5()
        for s in spec["driver"]["steps"]:
            if (s or {}).get("cmd") == "RunTests":
                s["args"]["category"] = "RecordingInvariants,GhostPlayback"
        v = hlib.validate_spec(spec, self.reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("multi-category" in e for e in v.errors), list(v.errors))

    def test_absent_category_selector_is_rejected(self):
        # Same class: RunTests with no category is RunAll, i.e. every category at
        # once, with the same inexpressible per-constituent tally.
        spec = self._h5()
        for s in spec["driver"]["steps"]:
            if (s or {}).get("cmd") == "RunTests":
                s["args"].pop("category", None)
        v = hlib.validate_spec(spec, self.reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("absent" in e for e in v.errors), list(v.errors))

    def test_shape_errors_are_waivable_by_the_documented_opt_out(self):
        # Both shape errors ride the SAME reason-required opt-out as the contract
        # gate itself: opting out means "this spec's batch cannot be non-vacuity
        # gated", which is exactly what an unpinnable second batch or aggregate is.
        for shape in ("multi-selector", "second-step"):
            with self.subTest(shape=shape):
                spec = self._h5()
                if shape == "multi-selector":
                    for s in spec["driver"]["steps"]:
                        if (s or {}).get("cmd") == "RunTests":
                            s["args"]["category"] = "RecordingInvariants,GhostPlayback"
                else:
                    steps = spec["driver"]["steps"]
                    idx = next(i for i, s in enumerate(steps)
                               if (s or {}).get("cmd") == "RunTests")
                    steps.insert(idx + 1, {"cmd": "RunTests",
                                           "args": {"category": "GhostPlayback"},
                                           "expect": "OK", "budget": 540})
                lc = spec["expectations"]["logContracts"]
                lc[hlib.BATCH_VACUITY_OPT_OUT_KEY] = True
                lc[hlib.BATCH_VACUITY_OPT_OUT_REASON_KEY] = "documented shape exception"
                v = hlib.validate_spec(spec, self.reg)
                self.assertTrue(v.ok, list(v.errors))

    def test_two_patterns_satisfiable_by_different_lines_is_a_gap(self):
        # Dodge 3: the gate ANDed its patterns against ONE synthesized probe, but
        # evaluate_expectations re.searches each pattern over the WHOLE log
        # independently. `total=42 passed=40` is satisfied by the LogContractTests
        # decoy line while `.* failed=0` is satisfied by the vacuous tally, so the
        # contract passed over a batch that executed nothing.
        gap = hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 total=42 passed=40",
             "BATCH_COMPLETE v1 .* failed=0\\b"], "GhostPlayback")
        self.assertIsNotNone(gap)
        self.assertIn("accepted by required pattern", gap)

    def test_a_pattern_only_the_decoy_satisfies_is_not_a_discriminator(self):
        # The LogContractTests literal is emitted by ParsekLog.Info regardless of the
        # driven batch, so a pattern matching only IT proves nothing about the batch.
        self.assertEqual(1, len(hlib._BATCH_DECOY_BODIES))
        gap = hlib.batch_contract_vacuity_gap(
            [hlib._BATCH_DECOY_BODIES[0], "BATCH_COMPLETE v1 .* failed=0\\b"],
            "RecordingInvariants")
        self.assertIsNotNone(gap)

    def test_the_decoy_line_matches_the_shipped_c_sharp_literal(self):
        # If LogContractTests.BatchCompleteFormatValid ever changes its numbers, the
        # decoy this gate models goes stale and the gate silently weakens. Cross-check
        # against the C# source, normalising away its string-concatenation line breaks.
        src = os.path.join(os.path.dirname(HARNESS_ROOT), "Source", "Parsek",
                           "InGameTests", "LogContractTests.cs")
        self.assertTrue(os.path.isfile(src), src)
        with open(src, "r", encoding="utf-8", errors="replace") as fh:
            text = fh.read()
        # Join C# adjacent-literal concatenation ("a" + "b") and collapse whitespace.
        joined = re.sub(r'"\s*\+\s*"', "", text)
        joined = re.sub(r"\s+", " ", joined)
        for body in hlib._BATCH_DECOY_BODIES:
            self.assertIn(re.sub(r"\s+", " ", body), joined,
                          "decoy body is no longer the literal LogContractTests asserts")

    def test_single_strong_pin_still_passes(self):
        # Guard against over-tightening: the shipped shape must stay accepted.
        self.assertIsNone(hlib.batch_contract_vacuity_gap(
            ["BATCH_COMPLETE v1 total=42 passed=40 failed=0 skipped=2 "
             "category=GhostPlayback scene=FLIGHT"], "GhostPlayback"))

    def test_every_committed_batch_spec_still_validates_clean(self):
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            with self.subTest(spec=name):
                v = hlib.validate_spec(load_spec(name), self.reg)
                self.assertTrue(v.ok, "%s: %s" % (name, list(v.errors)))


# ---------------------------------------------------------------------------
# Batch-tally source sync: the pinned tally vs the C# [InGameTest] attributes.
# ---------------------------------------------------------------------------


class InGameAttributeParseTests(unittest.TestCase):
    """Guards the parse against the four forms the real tree uses, each of which
    defeats a single-line regex. Every case is written as C# text so a failure
    points at the parse rule, not at whichever file happened to change."""

    def one(self, text):
        decls = hlib.parse_ingame_test_declarations(text, "T.cs")
        self.assertEqual(len(decls), 1, decls)
        return decls[0]

    def test_single_line_form(self):
        d = self.one('[InGameTest(Category = "Missions", Scene = GameScenes.FLIGHT)]\n'
                     '        public void Foo() { }')
        self.assertEqual((d.category, d.scene, d.allow_batch),
                         ("Missions", "FLIGHT", True))
        self.assertIn("T.cs:1 Foo", d.origin)

    def test_multi_line_form(self):
        d = self.one('        [InGameTest(\n'
                     '            Category = "SwitchIntentPatch",\n'
                     '            Description = "TS Fly patch is registered",\n'
                     '            Scene = GameScenes.TRACKSTATION)]\n'
                     '        public void Bar() { }')
        self.assertEqual((d.category, d.scene), ("SwitchIntentPatch", "TRACKSTATION"))

    def test_category_resolves_through_a_const(self):
        # RouteRewindTimelineRuntimeTests' real form: the attribute names a const,
        # so a regex looking for Category = "<literal>" sees the category as absent
        # and silently attributes all 7 tests to "General".
        d = self.one('        private const string Category = "RouteRewindTimeline";\n'
                     '        [InGameTest(Category = Category)]\n'
                     '        public void Baz() { }')
        self.assertEqual(d.category, "RouteRewindTimeline")

    def test_category_resolves_through_a_qualified_const(self):
        d = self.one('    const string Category = "Periodicity";\n'
                     '    [InGameTest(Category = Fixture.Category)]\n'
                     '    public void Q() { }')
        self.assertEqual(d.category, "Periodicity")

    def test_description_commas_and_parens_do_not_split_the_arg_list(self):
        d = self.one('[InGameTest(Description = "a, b (c), d = e",'
                     ' Category = "Missions", AllowBatchExecution = false)]\n'
                     'public void W() { }')
        self.assertEqual((d.category, d.allow_batch), ("Missions", False))

    # --- R5: RestoreBatchFlightBaselineAfterExecution ---

    def test_the_decl_dataclass_default_for_restore_is_false(self):
        # Distinct from the ATTRIBUTE default below: this is the python dataclass's
        # own default, which every caller currently passes explicitly, so flipping it
        # survived the whole suite. A 4-arg positional construction would then
        # silently mark every declaration restore-backed and over-count an isolated
        # derivation's `executable` - the exact direction _resolve_bool_default_false
        # is written to avoid.
        d = hlib.InGameTestDecl(category="C", scene="FLIGHT", allow_batch=True,
                                origin="F.cs:1 m")
        self.assertFalse(d.restore_baseline)

    def test_restore_baseline_defaults_false(self):
        # The C# property carries NO initializer, unlike AllowBatchExecution, so its
        # default is false. Defaulting it true would silently inflate every isolated
        # derivation's `passed=` and let a spec pin more tests than can run.
        d = self.one('[InGameTest(Category = "X")]\npublic void A() { }')
        self.assertFalse(d.restore_baseline)

    def test_restore_baseline_is_read_off_the_attribute(self):
        d = self.one('[InGameTest(Category = "SceneExitMerge", '
                     'AllowBatchExecution = false, '
                     'RestoreBatchFlightBaselineAfterExecution = true)]\n'
                     'public void B() { }')
        self.assertEqual((d.category, d.allow_batch, d.restore_baseline),
                         ("SceneExitMerge", False, True))

    def test_restore_baseline_fails_closed_on_an_unreadable_expression(self):
        # Only a literal `true` admits. Anything this parse cannot evaluate reads as
        # NOT restore-backed, so an isolated derivation UNDER-counts admissions and a
        # spec pinning the higher number reds. The opposite default would silently
        # over-count and the spec would red only on the nightly.
        for expr in ("SomeConst", "!false", "True"):
            with self.subTest(expr=expr):
                d = self.one('[InGameTest(Category = "X", '
                             'RestoreBatchFlightBaselineAfterExecution = %s)]\n'
                             'public void C() { }' % expr)
                self.assertFalse(d.restore_baseline)

    def test_restore_baseline_survives_the_long_batch_skip_reason(self):
        # Both real SceneExitMerge declarations carry a multi-sentence
        # BatchSkipReason containing commas, parens and "=" before the restore flag.
        # If the arg-splitter broke on any of those the flag would silently read
        # false and the isolated derivation would collapse to the ordinary one.
        d = self.one('[InGameTest(Category = "SceneExitMerge", '
                     'Scene = GameScenes.FLIGHT, RunLast = true, '
                     'AllowBatchExecution = false, '
                     'BatchSkipReason = "Isolated-run only - excluded from Run All '
                     '(ordinary), because this starts a recording, launches, and '
                     'exits FLIGHT; use Run All + Isolated.", '
                     'RestoreBatchFlightBaselineAfterExecution = true, '
                     'Description = "Space Center exit, merge dialog")]\n'
                     'public IEnumerator D() { yield break; }')
        self.assertEqual((d.category, d.scene, d.allow_batch, d.restore_baseline),
                         ("SceneExitMerge", "FLIGHT", False, True))

    def test_the_real_scene_exit_merge_declarations_parse_as_restore_backed(self):
        # Reads the ACTUAL source, not a synthetic string: H21's whole premise is
        # that these two are batch-disabled AND restore-backed. A spelling change in
        # the tree that the parse cannot follow would silently turn the isolated
        # derivation back into the ordinary one, and this cell is what notices.
        decls = [d for d in load_ingame_test_declarations()
                 if d.category == "SceneExitMerge"]
        self.assertEqual(2, len(decls), [d.origin for d in decls])
        for d in decls:
            with self.subTest(origin=d.origin):
                self.assertEqual("FLIGHT", d.scene)
                self.assertFalse(d.allow_batch)
                self.assertTrue(d.restore_baseline)

    def test_attribute_shaped_text_inside_a_string_is_not_a_declaration(self):
        # The real trap: IncompleteBallisticRuntimeTests has a Description that
        # literally reads "no [InGameTest] declares Scene=EDITOR".
        decls = hlib.parse_ingame_test_declarations(
            '[InGameTest(Category = "X",\n'
            '    Description = "Contract: no [InGameTest] declares Scene=EDITOR")]\n'
            'public void V() { }', "T.cs")
        self.assertEqual([d.category for d in decls], ["X"])

    def test_commented_out_attributes_are_ignored(self):
        decls = hlib.parse_ingame_test_declarations(
            '// [InGameTest(Category = "Ghost")]\n'
            '/* [InGameTest(Category = "Ghost")]\n'
            '   [InGameTest(Category = "Ghost")] */\n'
            '[InGameTest(Category = "Ghost")]\n'
            'public void R() { }', "T.cs")
        self.assertEqual([d.category for d in decls], ["Ghost"])

    def test_verbatim_string_description_does_not_swallow_the_file(self):
        decls = hlib.parse_ingame_test_declarations(
            '[InGameTest(Category = "A", Description = @"quote "" and , inside")]\n'
            'public void A1() { }\n'
            '[InGameTest(Category = "B")]\n'
            'public void B1() { }', "T.cs")
        self.assertEqual([d.category for d in decls], ["A", "B"])

    def test_scene_defaults_to_any_scene_and_accepts_the_explicit_sentinel(self):
        self.assertIs(self.one('[InGameTest(Category = "A")] void M(){}').scene,
                      hlib.INGAME_ANY_SCENE)
        self.assertIs(
            self.one('[InGameTest(Category = "A",\n'
                     ' Scene = InGameTestAttribute.AnyScene)] void M(){}').scene,
            hlib.INGAME_ANY_SCENE)

    def test_allow_batch_execution_defaults_true_and_only_false_disables(self):
        self.assertTrue(self.one('[InGameTest(Category = "A")] void M(){}').allow_batch)
        self.assertTrue(self.one(
            '[InGameTest(Category = "A", AllowBatchExecution = true)] void M(){}'
        ).allow_batch)
        self.assertFalse(self.one(
            '[InGameTest(Category = "A", AllowBatchExecution = false)] void M(){}'
        ).allow_batch)
        # Every resolved (absent-or-literal) form carries an EMPTY marker - the
        # Whole-tree declaration recount must be byte-identical to pre-fix (542 at
        # HEAD; the count is recomputed mechanically here, never asserted as a
        # literal, so this comment is orientation only).
        for src in ('[InGameTest(Category = "A")] void M(){}',
                    '[InGameTest(Category = "A", AllowBatchExecution = true)] void M(){}',
                    '[InGameTest(Category = "A", AllowBatchExecution = false)] void M(){}'):
            with self.subTest(src=src):
                self.assertEqual("", self.one(src).allow_batch_marker)

    def test_non_literal_allow_batch_fails_closed_with_a_marker(self):
        # HLIB-ALLOWBATCH-NONLITERAL-FAILS-OPEN: `(expr or "true").strip() !=
        # "false"` read ANY non-literal as batch-allowed, loosening the derived
        # tally bounds in the direction that under-reports skips. Both malformed
        # shapes the todo entry names - a const indirection and a computed
        # expression - must now (a) resolve fail-CLOSED (allow_batch False, so the
        # derivation under-counts admissions and a pinned tally reds, the
        # _resolve_bool_default_false direction), and (b) carry the
        # `<unresolved:...>` marker so `unresolved_ingame_declarations` reds the
        # sync gate ON THE DECLARATION instead of leaving a count mismatch to be
        # reverse-engineered.
        const_indirection = self.one(
            '[InGameTest(Category = "A", AllowBatchExecution = SomeConsts.Allow)]'
            ' void M(){}')
        computed = self.one(
            '[InGameTest(Category = "A", AllowBatchExecution = !manualOnly)]'
            ' void M(){}')
        for d, expr in ((const_indirection, "SomeConsts.Allow"),
                        (computed, "!manualOnly")):
            with self.subTest(expr=expr):
                self.assertFalse(d.allow_batch, "non-literal must fail CLOSED")
                self.assertEqual("<unresolved:%s>" % expr, d.allow_batch_marker)
                self.assertEqual([d], hlib.unresolved_ingame_declarations([d]),
                                 "the marker must red the sweep")
        # The C# capitalized literals are NOT the attribute grammar's lowercase
        # `true`/`false` - they are identifiers to this parse and must mark, not
        # silently resolve either way.
        self.assertEqual("<unresolved:False>", self.one(
            '[InGameTest(Category = "A", AllowBatchExecution = False)] void M(){}'
        ).allow_batch_marker)

    def test_bare_attribute_counts_as_the_default_category(self):
        # Legal C#, absent from the tree today. It must COUNT (as "General"), not
        # vanish, or a future bare declaration would go unnoticed.
        d = self.one('[InGameTest]\npublic void N() { }')
        self.assertEqual(d.category, "General")

    def test_an_indexer_on_a_similarly_named_type_is_not_a_declaration(self):
        # `[\s*InGameTest` alone also matches ordinary subscript code, inventing a
        # phantom "General" declaration; the trailing `(`/`]` requirement is what
        # keeps a real indexer out of the tally.
        self.assertEqual(hlib.parse_ingame_test_declarations(
            'var t = lookup[InGameTestRunner.Tag];\n'
            'var u = byName[InGameTestInfo.Key];\n', "T.cs"), [])

    def test_member_name_survives_a_following_attribute(self):
        d = self.one('[InGameTest(Category = "A")]\n'
                     '[Obsolete("x")]\n'
                     'public IEnumerator Later() { yield break; }')
        self.assertIn("Later", d.origin)

    def test_unresolvable_forms_are_reported_not_dropped(self):
        # Fail LOUD: a form the parse does not model must not silently shrink a
        # category total, which is the one way this gate could fail OPEN.
        decls = hlib.parse_ingame_test_declarations(
            '[InGameTest(Category = SomeOther.Thing, Scene = ResolveScene())]\n'
            'void M(){}', "T.cs")
        self.assertEqual(len(hlib.unresolved_ingame_declarations(decls)), 1)


class RoslynAttributeSpellingTests(unittest.TestCase):
    """The four spellings Roslyn binds that the ORIGINAL `[`-anchored parse missed.

    Each was mutation-proved to add a REAL test to a category while leaving the
    gate green - a SILENT DROP, because unresolved_ingame_declarations only ever
    sees forms the parse already recognised. Not hypothetical either: the tree
    already carried five `[Parsek.InGameTests.InGameTest(...)]` declarations that
    both this parse AND a raw `[InGameTest(` grep dropped identically, which is
    why the two agreeing on 534 proved nothing. All four must now be COUNTED, and
    the ordinary code the old anchor existed to exclude must stay excluded.
    """

    def cats(self, text):
        return [d.category
                for d in hlib.parse_ingame_test_declarations(text, "T.cs")]

    def test_stacked_attribute_list_is_counted(self):
        self.assertEqual(self.cats(
            '[System.Obsolete("x"), InGameTest(Category = "GameActionsHealth")]\n'
            'public void Foo() { }'), ["GameActionsHealth"])

    def test_explicit_attribute_suffix_is_counted(self):
        self.assertEqual(self.cats(
            '[InGameTestAttribute(Category = "GameActionsHealth")]\n'
            'public void Foo() { }'), ["GameActionsHealth"])

    def test_namespace_qualified_name_is_counted(self):
        # The form the tree ACTUALLY uses in IncompleteBallisticRuntimeTests.cs.
        decls = hlib.parse_ingame_test_declarations(
            '[Parsek.InGameTests.InGameTest(Category = "Ledger",\n'
            '    Scene = GameScenes.SPACECENTER,\n'
            '    Description = "x")]\n'
            'public void Foo() { }', "T.cs")
        self.assertEqual([(d.category, d.scene) for d in decls],
                         [("Ledger", "SPACECENTER")])
        self.assertIn("T.cs:1 Foo", decls[0].origin)

    def test_attribute_target_prefix_is_counted(self):
        self.assertEqual(self.cats(
            '[method: InGameTest(Category = "GameActionsHealth")]\n'
            'public void Foo() { }'), ["GameActionsHealth"])

    def test_bare_attribute_sharing_a_bracket_is_counted(self):
        self.assertEqual(self.cats(
            '[InGameTest, System.Obsolete("x")]\npublic void Foo() { }'),
            ["General"])

    def test_member_name_survives_a_shared_bracket(self):
        d = hlib.parse_ingame_test_declarations(
            '[InGameTest(Category = "A"), Obsolete("x")]\n'
            'public IEnumerator Later() { yield break; }', "T.cs")[0]
        self.assertIn("Later", d.origin)


class UnclaimedInGameAttributeTests(unittest.TestCase):
    """The residual backstop: an attribute-bracket occurrence of the name that the
    parse claims NOTHING for. Empty for every form the parse models; non-empty
    (and asserted empty repo-wide) for the next spelling nobody anticipated."""

    def unclaimed(self, text):
        return hlib.unclaimed_ingame_attribute_tokens(text, "T.cs")

    def test_house_style_declaration_is_claimed(self):
        self.assertEqual(self.unclaimed(
            '[InGameTest(Category = "Missions", Scene = GameScenes.FLIGHT)]\n'
            'public void Foo() { }'), [])

    def test_bare_attribute_is_claimed(self):
        self.assertEqual(self.unclaimed('[InGameTest]\npublic void Foo() { }'), [])

    def test_multi_line_declaration_is_claimed(self):
        self.assertEqual(self.unclaimed(
            '        [InGameTest(\n'
            '            Category = "X",\n'
            '            Scene = GameScenes.FLIGHT)]\n'
            '        public void Foo() { }'), [])

    def test_the_four_roslyn_spellings_are_all_claimed(self):
        for text in (
                '[System.Obsolete("x"), InGameTest(Category = "G")]\nvoid F(){}',
                '[InGameTestAttribute(Category = "G")]\nvoid F(){}',
                '[Parsek.InGameTests.InGameTest(Category = "G")]\nvoid F(){}',
                '[method: InGameTest(Category = "G")]\nvoid F(){}'):
            with self.subTest(text=text):
                self.assertEqual(self.unclaimed(text), [])

    def test_an_unmodelled_spelling_is_reported(self):
        # `@InGameTest` is C#'s verbatim-identifier escape: legal, binds to the
        # same attribute, and the element grammar rejects it. Stands for the
        # general case - a spelling nobody anticipated must RED, not vanish.
        got = self.unclaimed(
            '[@InGameTest(Category = "GameActionsHealth")]\npublic void Bar(){}')
        self.assertEqual(len(got), 1, got)
        self.assertIn("T.cs:1", got[0])
        self.assertEqual(
            [d.category for d in hlib.parse_ingame_test_declarations(
                '[@InGameTest(Category = "GameActionsHealth")]\nvoid Bar(){}')],
            [], "the point of the cell: the parse really does drop it")

    def test_indexer_on_a_similarly_named_type_is_not_reported(self):
        # The false-positive direction: these are ordinary subscript code, and the
        # `\b` boundaries plus the "(" / "]" follow requirement keep them out.
        self.assertEqual(self.unclaimed(
            'var t = lookup[InGameTestRunner.Tag];\n'
            'var u = byName[InGameTestInfo.Key];\n'
            'var v = map[InGameTestAttribute.Something];\n'), [])

    def test_a_type_reference_outside_a_bracket_is_not_reported(self):
        self.assertEqual(self.unclaimed(
            'var a = m.GetCustomAttribute<InGameTestAttribute>();\n'
            'if (attr is InGameTestAttribute) { }\n'
            'typeof(InGameTest);\n'), [])

    def test_a_typeof_reference_inside_a_bracket_is_not_reported(self):
        self.assertEqual(self.unclaimed(
            '[SomeAttr(typeof(InGameTestAttribute))]\npublic void Foo() { }'), [])

    def test_commented_out_and_stringified_forms_are_not_reported(self):
        self.assertEqual(self.unclaimed(
            '// [InGameTestAttribute(Category = "X")]\n'
            '/* [method: InGameTest(Category = "X")] */\n'
            'var s = "[InGameTestAttribute(Category = \\"X\\")]";\n'), [])


class DeriveBatchTallyTests(unittest.TestCase):
    """Guards the two-filter model against InGameTestRunner.RunCategory."""

    @staticmethod
    def decl(cat="C", scene=hlib.INGAME_ANY_SCENE, allow=True, origin="o",
             restore=False):
        return hlib.InGameTestDecl(category=cat, scene=scene, allow_batch=allow,
                                   origin=origin, restore_baseline=restore)

    # --- R5: the isolated admission filter ---

    def test_isolated_admits_a_restore_backed_batch_disabled_test(self):
        # The R5 unlock in one cell: AllowBatchExecution=false +
        # RestoreBatchFlightBaselineAfterExecution=true is skipped by the ordinary
        # filter and EXECUTED by the isolated one. `total` is unchanged in both --
        # BATCH_COMPLETE counts filtered tests too -- so the whole difference lands
        # in the batch_skipped/executable split, which is what a pin rests on.
        ds = [self.decl(allow=False, restore=True),
              self.decl(allow=False, restore=True, origin="p")]
        ordinary = hlib.derive_batch_tally(ds, "C", "FLIGHT")
        isolated = hlib.derive_batch_tally(ds, "C", "FLIGHT", isolated=True)
        self.assertEqual((ordinary.total, ordinary.batch_skipped, ordinary.executable),
                         (2, 2, 0))
        self.assertEqual((isolated.total, isolated.batch_skipped, isolated.executable),
                         (2, 0, 2))

    def test_isolated_still_skips_a_genuinely_manual_only_test(self):
        # The isolated filter is `allow || restore`, NOT "admit everything". A test
        # that is batch-disabled and NOT restore-backed stays skipped on both paths;
        # if it did not, the isolated route would run destructive manual-only tests
        # with nothing to revert them.
        ds = [self.decl(allow=False, restore=False)]
        for isolated in (False, True):
            with self.subTest(isolated=isolated):
                d = hlib.derive_batch_tally(ds, "C", "FLIGHT", isolated=isolated)
                self.assertEqual((d.batch_skipped, d.executable), (1, 0))

    def test_isolated_changes_nothing_for_a_batch_allowed_test(self):
        # The four restore-only declarations in the tree (Contracts 2, TestCommands
        # 1, LedgerGroundTruth 1) carry restore=true WITHOUT allow=false, so they are
        # already admitted by `allow_batch` alone. The isolated filter's `|| restore`
        # disjunct is redundant for them and must not move any count -- which is why
        # they cannot serve as a differential proof that the arg took effect.
        ds = [self.decl(allow=True, restore=True), self.decl(allow=True, restore=False,
                                                             origin="p")]
        ordinary = hlib.derive_batch_tally(ds, "C", "FLIGHT")
        isolated = hlib.derive_batch_tally(ds, "C", "FLIGHT", isolated=True)
        self.assertEqual(
            (ordinary.total, ordinary.batch_skipped, ordinary.executable),
            (isolated.total, isolated.batch_skipped, isolated.executable))

    def test_isolated_does_not_relax_the_scene_filter(self):
        # FilterSceneEligibleBatchCandidates runs BEFORE either admission filter on
        # all four entry points and knows nothing about the restore flag. A
        # scene-ineligible restore-backed test stays in the SCENE bucket, counted
        # once, on both paths.
        ds = [self.decl(scene="SPACECENTER", allow=False, restore=True)]
        d = hlib.derive_batch_tally(ds, "C", "FLIGHT", isolated=True)
        self.assertEqual((d.total, d.scene_skipped, d.batch_skipped, d.executable),
                         (1, 1, 0, 0))

    def test_isolated_defaults_off(self):
        # Back-compatibility: every pre-R5 caller passes two positional args and
        # must keep getting the ordinary model.
        ds = [self.decl(allow=False, restore=True)]
        self.assertEqual(0, hlib.derive_batch_tally(ds, "C", "FLIGHT").executable)

    def test_any_scene_is_eligible_everywhere(self):
        d = hlib.derive_batch_tally([self.decl(), self.decl(origin="p")], "C",
                                    "SPACECENTER")
        self.assertEqual((d.total, d.scene_skipped, d.batch_skipped, d.executable),
                         (2, 0, 0, 2))

    def test_other_categories_are_not_counted(self):
        d = hlib.derive_batch_tally(
            [self.decl(cat="C"), self.decl(cat="D")], "C", "FLIGHT")
        self.assertEqual(d.total, 1)

    def test_scene_mismatch_skips(self):
        d = hlib.derive_batch_tally(
            [self.decl(scene="FLIGHT"), self.decl(scene="SPACECENTER", origin="p")],
            "C", "SPACECENTER")
        self.assertEqual((d.total, d.scene_skipped, d.executable), (2, 1, 1))
        self.assertEqual(d.scene_skipped_members, ("o",))

    def test_allow_batch_false_skips_after_the_scene_filter(self):
        d = hlib.derive_batch_tally(
            [self.decl(allow=False), self.decl(origin="p")], "C", "SPACECENTER")
        self.assertEqual((d.total, d.scene_skipped, d.batch_skipped, d.executable),
                         (2, 0, 1, 1))

    def test_a_test_failing_both_filters_is_counted_once_in_the_scene_bucket(self):
        # The reason the filters are modelled IN ORDER. RunCategory hands
        # FilterSceneEligibleBatchCandidates' OUTPUT to PrepareBatchExecution, so a
        # scene-ineligible batch-disabled test never reaches the second filter.
        # Double-counting it would inflate the derived floor and false-red a spec.
        d = hlib.derive_batch_tally(
            [self.decl(scene="FLIGHT", allow=False)], "C", "SPACECENTER")
        self.assertEqual((d.total, d.scene_skipped, d.batch_skipped), (1, 1, 0))
        self.assertEqual(d.attribute_skipped, 1)

    def test_total_always_partitions_into_the_three_buckets(self):
        d = hlib.derive_batch_tally(
            [self.decl(scene="FLIGHT"), self.decl(allow=False, origin="p"),
             self.decl(origin="q")], "C", "SPACECENTER")
        self.assertEqual(d.total, d.scene_skipped + d.batch_skipped + d.executable)

    def test_runall_token_spans_every_category(self):
        # RunTests with no category argument drives InGameTestRunner.RunAll, which
        # stamps currentBatchSelector = "all" and scene-filters the WHOLE allTests
        # set. Reading "all" as a category name would derive total=0 and report the
        # category as missing -- a false RED pointing the wrong way.
        decls = [self.decl(cat="C"), self.decl(cat="D", origin="p"),
                 self.decl(cat="E", scene="FLIGHT", origin="q"),
                 self.decl(cat="F", allow=False, origin="r")]
        d = hlib.derive_batch_tally(decls, hlib.INGAME_RUNALL_CATEGORY,
                                    "SPACECENTER")
        self.assertEqual((d.total, d.scene_skipped, d.batch_skipped, d.executable),
                         (4, 1, 1, 2))

    def test_runall_pin_is_checked_not_reported_as_a_missing_category(self):
        decls = [self.decl(cat="C"), self.decl(cat="D", origin="p")]
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
            "category=all scene=SPACECENTER"])
        self.assertEqual(hlib.batch_tally_pin_mismatches(pin, decls), [])
        stale = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=3 passed=3 failed=0 skipped=0 "
            "category=all scene=SPACECENTER"])
        problems = hlib.batch_tally_pin_mismatches(stale, decls)
        self.assertTrue(any("the source declares 2" in p for p in problems),
                        problems)
        # The false-RED this replaces: reading "all" as a category name derived
        # total=0 and claimed the batch "would run EMPTY" - the wrong direction.
        self.assertFalse(any("would run EMPTY" in p for p in problems), problems)

    def test_duplicate_looking_declarations_are_both_counted(self):
        # Frozen dataclasses compare by value; a membership-based split would
        # mis-bucket the twin.
        twin = self.decl(scene="FLIGHT")
        d = hlib.derive_batch_tally([twin, twin], "C", "SPACECENTER")
        self.assertEqual((d.total, d.scene_skipped), (2, 2))

    def test_unknown_category_derives_an_empty_batch(self):
        d = hlib.derive_batch_tally([self.decl(cat="C")], "Renamed", "FLIGHT")
        self.assertEqual((d.total, d.executable), (0, 0))


class BatchTallyPinTests(unittest.TestCase):
    """Guards which tokens are read as PINNED literals and which as deliberately
    unpinned regex classes (S1.4's honest interim form)."""

    def test_reads_every_literal_token(self):
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=12 passed=5 failed=0 skipped=7 "
            "category=Missions scene=SPACECENTER"])
        self.assertEqual((pin.total, pin.passed, pin.failed, pin.skipped),
                         (12, 5, 0, 7))
        self.assertEqual((pin.category, pin.scene), ("Missions", "SPACECENTER"))
        self.assertTrue(pin.statically_checkable)

    def test_regex_class_tokens_read_as_unpinned(self):
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=42 passed=[1-9][0-9]* failed=0 "
            "skipped=[0-9]+ category=GhostPlayback scene=FLIGHT"])
        self.assertEqual(pin.total, 42)
        self.assertIsNone(pin.passed)
        self.assertIsNone(pin.skipped)
        self.assertEqual(pin.failed, 0)
        self.assertTrue(pin.statically_checkable)

    def test_trailing_regex_anchor_does_not_hide_a_literal(self):
        pin = hlib.resolve_batch_tally_pin(
            ["BATCH_COMPLETE v1 total=7 passed=7 failed=0 skipped=0\\b "
             "category=RouteRewindTimeline scene=FLIGHT$"])
        self.assertEqual((pin.skipped, pin.scene), (0, "FLIGHT"))

    def test_non_batch_patterns_are_ignored_and_absence_reads_none(self):
        self.assertIsNone(hlib.resolve_batch_tally_pin(
            ["RecordingInvariants walk: recordings=306 trees=276"]))
        self.assertIsNone(hlib.resolve_batch_tally_pin([]))

    def test_tokens_merge_across_patterns(self):
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=4 ",
            "BATCH_COMPLETE v1 .* category=GameActionsHealth scene=SPACECENTER"])
        self.assertEqual((pin.total, pin.category, pin.scene),
                         (4, "GameActionsHealth", "SPACECENTER"))

    def test_the_old_bare_failed_zero_pin_is_not_statically_checkable(self):
        pin = hlib.resolve_batch_tally_pin(["BATCH_COMPLETE v1 .* failed=0\\b"])
        self.assertIsNotNone(pin)
        self.assertFalse(pin.statically_checkable)

    def test_a_hyphenated_category_reads_as_a_pinned_literal(self):
        # REGRESSION. _pin_literal_word's class used to be [A-Za-z0-9_:]+, which
        # excluded `-` and therefore made ALL SEVEN of the tree's hyphenated
        # categories (Pipeline-Anchor, Pipeline-Smoothing, Pipeline-Frame,
        # Pipeline-Outlier, Pipeline-Terrain, Pipeline-AnchorPropagate,
        # Pipeline-Anchor-BubbleEntry) structurally unpinnable: category read None,
        # statically_checkable went False, and the sync sweep rejected the spec
        # with a message blaming the author. The runtime side never had the gap
        # (_BATCH_RE reads category=\S+), so this was a static-path-only
        # disagreement with the line the game actually prints.
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=7 passed=7 failed=0 skipped=0 "
            "category=Pipeline-Anchor scene=FLIGHT"])
        self.assertEqual(pin.category, "Pipeline-Anchor")
        self.assertTrue(pin.statically_checkable)
        # Two hyphens must survive too - the longest name in the tree.
        self.assertEqual(
            hlib.resolve_batch_tally_pin([
                "BATCH_COMPLETE v1 total=2 category=Pipeline-Anchor-BubbleEntry "
                "scene=FLIGHT"]).category,
            "Pipeline-Anchor-BubbleEntry")

    def test_the_runtime_parser_and_the_pin_parser_agree_on_a_hyphen(self):
        # The two must not disagree about what a category token IS: the pin says
        # what must appear, the runtime parser reads what did. A hyphen that only
        # one of them accepts is how a pin silently stops gating.
        line = ("[LOG 00:00:00.000] [Parsek][INFO][TestRunner] BATCH_COMPLETE v1 "
                "total=4 passed=4 failed=0 skipped=0 category=Pipeline-Smoothing "
                "scene=FLIGHT")
        parsed = hlib.parse_batch_complete_line(line)
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=4 passed=4 failed=0 skipped=0 "
            "category=Pipeline-Smoothing scene=FLIGHT"])
        self.assertEqual(parsed.category, "Pipeline-Smoothing")
        self.assertEqual(pin.category, parsed.category)

    def test_a_bracketed_regex_range_is_still_not_a_literal(self):
        # The safety half of widening the class. `-` is a metacharacter only inside
        # a character class, and `[` / `]` stay excluded, so a genuine regex token
        # must keep reading as UNPINNED rather than as a category literally named
        # "[A-Z]-x" - which would make the sweep report a category that cannot
        # exist and send the reader hunting for a rename that never happened.
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=3 passed=3 failed=0 skipped=0 "
            "category=[A-Z]-x scene=FLIGHT"])
        self.assertIsNone(pin.category)
        self.assertFalse(pin.statically_checkable)

    def test_a_multi_category_aggregate_pin_is_recognized_as_such(self):
        # A future RunTests category = "A,B" spec gates on the UNION line, whose
        # total sums categories the pin never enumerates. It must read as
        # out-of-scope, NOT as a category that vanished from the source.
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=19 passed=19 failed=0 skipped=0 "
            "category=multi:2 scene=FLIGHT"])
        self.assertTrue(pin.is_aggregate)
        self.assertFalse(pin.statically_checkable)
        self.assertEqual(pin.total, 19)


class BatchTallyMismatchTests(unittest.TestCase):
    """Guards each mismatch rule, and (as much) each NON-mismatch: a rule that
    reds a legitimate pin is worse than no rule, because the fix is to weaken it."""

    @staticmethod
    def decls(*specs):
        return [hlib.InGameTestDecl(category=c, scene=s, allow_batch=a,
                                    origin="F.cs:%d m%d" % (i + 1, i + 1))
                for i, (c, s, a) in enumerate(specs)]

    @staticmethod
    def pin(text):
        return hlib.resolve_batch_tally_pin([text])

    def test_agreeing_pin_has_no_problems(self):
        d = self.decls(("C", hlib.INGAME_ANY_SCENE, True),
                       ("C", hlib.INGAME_ANY_SCENE, True))
        self.assertEqual(hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                     "category=C scene=FLIGHT"), d), [])

    def test_an_added_test_reds_the_total(self):
        # THE regression this gate exists for: someone adds one method to a
        # category and the daily scenario reds hours later on the nightly run.
        d = self.decls(("C", hlib.INGAME_ANY_SCENE, True),
                       ("C", hlib.INGAME_ANY_SCENE, True),
                       ("C", hlib.INGAME_ANY_SCENE, True))
        problems = hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                     "category=C scene=FLIGHT"), d)
        self.assertTrue(any("pins total=2" in p and "declares 3" in p
                            for p in problems), problems)

    def test_a_renamed_category_reds_loudly(self):
        problems = hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                     "category=Renamed scene=FLIGHT"),
            self.decls(("C", hlib.INGAME_ANY_SCENE, True)))
        self.assertTrue(any("was renamed" in p for p in problems), problems)

    def test_a_new_flight_scene_test_reds_the_skipped_floor(self):
        d = self.decls(("C", "SPACECENTER", True), ("C", "FLIGHT", True),
                       ("C", "FLIGHT", True))
        problems = hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=3 passed=2 failed=0 skipped=1 "
                     "category=C scene=SPACECENTER"), d)
        self.assertTrue(any("pins skipped=1" in p and "force 2" in p
                            for p in problems), problems)

    def test_runtime_self_skips_above_the_floor_are_accepted(self):
        # L1-passive-sandbox's real shape: 4 AnyScene batch-allowed tests, 3 of
        # which self-skip on Mode != CAREER. skipped is a FLOOR, never an equality.
        d = self.decls(*[("C", hlib.INGAME_ANY_SCENE, True)] * 4)
        self.assertEqual(hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=4 passed=1 failed=0 skipped=3 "
                     "category=C scene=SPACECENTER"), d), [])

    def test_over_claimed_passed_reds_the_eligible_ceiling(self):
        d = self.decls(("C", hlib.INGAME_ANY_SCENE, True),
                       ("C", hlib.INGAME_ANY_SCENE, False))
        problems = hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=2 passed=2 failed=0 skipped=0 "
                     "category=C scene=FLIGHT"), d)
        self.assertTrue(any("can never run" in p for p in problems), problems)

    def test_a_self_inconsistent_pin_reds(self):
        d = self.decls(*[("C", hlib.INGAME_ANY_SCENE, True)] * 5)
        problems = hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=5 passed=3 failed=0 skipped=1 "
                     "category=C scene=FLIGHT"), d)
        self.assertTrue(any("not self-consistent" in p for p in problems), problems)

    def test_only_total_is_checked_when_passed_and_skipped_are_regex_classes(self):
        # S1.4's form. total= must still be enforced; the unpinned tokens must not
        # be silently read as 0 (which would red on the eligible ceiling).
        d = self.decls(*[("C", "FLIGHT", True)] * 3)
        pin = self.pin("BATCH_COMPLETE v1 total=3 passed=[1-9][0-9]* failed=0 "
                       "skipped=[0-9]+ category=C scene=FLIGHT")
        self.assertEqual(hlib.batch_tally_pin_mismatches(pin, d), [])
        stale = self.pin("BATCH_COMPLETE v1 total=2 passed=[1-9][0-9]* failed=0 "
                         "skipped=[0-9]+ category=C scene=FLIGHT")
        self.assertTrue(hlib.batch_tally_pin_mismatches(stale, d))

    def test_conflicting_pins_across_patterns_red(self):
        d = self.decls(*[("C", hlib.INGAME_ANY_SCENE, True)] * 4)
        pin = hlib.resolve_batch_tally_pin([
            "BATCH_COMPLETE v1 total=4 passed=4 failed=0 skipped=0 category=C "
            "scene=FLIGHT",
            "BATCH_COMPLETE v1 total=5"])
        self.assertTrue(any("conflicting total=" in p
                            for p in hlib.batch_tally_pin_mismatches(pin, d)))

    def test_an_uncheckable_pin_is_reported_not_waved_through(self):
        problems = hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 .* failed=0"), self.decls())
        self.assertTrue(any("cannot be cross-checked" in p for p in problems),
                        problems)

    def test_an_aggregate_pin_is_out_of_scope_not_a_missing_category(self):
        # The distinction the two branches draw: an UNREADABLE pin is suspicious
        # and complains; a multi-category aggregate is understood and stays silent.
        # Without the split, `category=multi:2` would derive total=0 and red as
        # "the category was renamed".
        self.assertEqual(hlib.batch_tally_pin_mismatches(
            self.pin("BATCH_COMPLETE v1 total=19 passed=19 failed=0 skipped=0 "
                     "category=multi:2 scene=FLIGHT"),
            self.decls(("C", hlib.INGAME_ANY_SCENE, True))), [])


class CommittedBatchTallySourceSyncTests(unittest.TestCase):
    """THE gate. Repo-wide: every committed spec's pinned BATCH_COMPLETE tally is
    cross-checked against the real C# [InGameTest] attributes in Source/Parsek.

    Why it exists: the anti-vacuity gate above forces the tally to be pinned WHOLE,
    which makes the pin a hardcoded copy of a number that lives in C#. Adding one
    [InGameTest] method to Missions / Periodicity / GameActionsHealth /
    RouteRewindTimeline / RecordingInvariants / GhostPlayback moved `total` and
    reds that category's daily scenario on its next NIGHTLY run, in another
    process, hours later, on a spec the author never opened. This turns that into a
    local `python -m unittest` failure that names the spec and the new number.
    """

    @classmethod
    def setUpClass(cls):
        cls.decls = load_ingame_test_declarations()
        cls.specs = batch_owning_specs()

    def test_the_isolated_flag_is_read_off_the_spec_not_assumed(self):
        # R5 FLOOR. Every cell below that derives a tally must pass the spec's OWN
        # batch mode to derive_batch_tally; hardcoding False would validate an
        # isolated spec against the ordinary admission filter (rejecting a correct
        # pin), and hardcoding True would let a spec that LOST its isolated arg keep
        # validating against the isolated derivation while running nothing. This
        # cell exists so the resolver itself is exercised over the committed set and
        # cannot silently start answering False for everything.
        modes = {name: hlib.spec_batch_isolated(spec)
                 for name, spec, _ in self.specs}
        self.assertTrue(modes, "no batch-owning spec found - sweep is inert")
        for name, spec, _ in self.specs:
            with self.subTest(spec=name):
                steps = (spec.get("driver", {}) or {}).get("steps", []) or []
                run_tests = [s for s in steps if (s or {}).get("cmd") == "RunTests"]
                expected = bool(run_tests) and (
                    (run_tests[0].get("args", {}) or {}).get("isolated") == "true")
                if not run_tests:
                    expected = ((spec.get("driver", {}) or {})
                                .get("autorun", {}) or {}).get("isolated") is True
                self.assertEqual(expected, modes[name])

    def test_the_source_tree_is_actually_readable(self):
        # Guards the gate itself: if the walk silently found nothing (a moved
        # source tree, a bad relative path), every assertion below passes vacuously
        # -- which is the exact class of defect this whole family exists for.
        self.assertTrue(os.path.isdir(PARSEK_SOURCE_DIR), PARSEK_SOURCE_DIR)
        self.assertGreater(len(self.decls), 100,
                           "only %d [InGameTest] declarations parsed out of %s - the "
                           "sweep would be vacuous" % (len(self.decls),
                                                       PARSEK_SOURCE_DIR))

    def test_every_declaration_resolves(self):
        unresolved = hlib.unresolved_ingame_declarations(self.decls)
        self.assertEqual(
            [d.origin for d in unresolved], [],
            "these [InGameTest] attributes use a Category/Scene form the harness "
            "parse does not model, so their category totals are wrong: %s"
            % [(d.origin, d.category, d.scene) for d in unresolved])

    def test_every_attribute_occurrence_is_claimed_by_the_parse(self):
        # The OTHER half of "reported, never dropped". test_every_declaration_
        # resolves only sees forms the strict regex already RECOGNISED; a spelling
        # it never matches (a stacked attribute list, an explicit `Attribute`
        # suffix, a namespace-qualified name, a `[method: ...]` target) is simply
        # absent from self.decls and silently shrinks a pinned category total.
        unclaimed = load_unclaimed_ingame_attribute_tokens()
        self.assertEqual(
            unclaimed, [],
            "these attribute brackets name InGameTest in a spelling the C# "
            "compiler binds but hlib.parse_ingame_test_declarations does not "
            "claim, so the tests they declare are MISSING from every category "
            "total. Either write them in house style ([InGameTest(...)] first in "
            "its own bracket) or teach the parse the new form:\n  - %s"
            % "\n  - ".join(unclaimed))

    def test_no_category_is_literally_the_runall_selector_token(self):
        # derive_batch_tally reads "all" as RunAll's whole-assembly batch, not as a
        # category name. A declaration categorised "all" would make the two
        # readings silently disagree.
        clashing = [d.origin for d in self.decls
                    if d.category == hlib.INGAME_RUNALL_CATEGORY]
        self.assertEqual(
            clashing, [],
            "Category = %r collides with the token InGameTestRunner.RunAll stamps "
            "as currentBatchSelector; rename the category: %s"
            % (hlib.INGAME_RUNALL_CATEGORY, clashing))

    def test_every_batch_spec_pins_a_statically_checkable_tally(self):
        checked = []
        for name, spec, selector in self.specs:
            lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
            if lc.get(hlib.BATCH_VACUITY_OPT_OUT_KEY) is True:
                continue
            # A multi-category selector gates on the union aggregate, whose total
            # spans categories the pin does not enumerate; nothing to demand here.
            if hlib.is_multi_category_selector(selector):
                continue
            pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
            self.assertIsNotNone(
                pin, "%s owns a RunTests batch but no logContracts.required pattern "
                     "names BATCH_COMPLETE" % name)
            self.assertTrue(
                pin.statically_checkable,
                "%s pins a BATCH_COMPLETE line with no literal category=/scene=, so "
                "its tally cannot be kept in sync with the source" % name)
            self.assertIsNotNone(
                pin.total,
                "%s must pin total= as a LITERAL: it is the one token derivable "
                "exactly from the [InGameTest] attributes, and the token that "
                "catches an added or removed test" % name)
            checked.append(name)
        self.assertTrue(checked, "no committed RunTests spec found - sweep is inert")

    def test_every_pinned_tally_agrees_with_the_source(self):
        for name, spec, _selector in self.specs:
            with self.subTest(spec=name):
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                if pin is None or not pin.statically_checkable:
                    continue
                problems = hlib.batch_tally_pin_mismatches(
                    pin, self.decls, isolated=hlib.spec_batch_isolated(spec))
                self.assertEqual(
                    problems, [],
                    "%s's pinned BATCH_COMPLETE tally no longer matches "
                    "Source/Parsek. Re-derive it and update the spec (and its "
                    "derivation comment) in the same commit:\n  - %s"
                    % (name, "\n  - ".join(problems)))

    def test_an_isolated_spec_would_red_if_its_isolated_arg_were_dropped(self):
        # MUTATION CELL, and the reason the flag has to be threaded rather than
        # defaulted. For every committed isolated spec, re-derive its pin against
        # the ORDINARY admission filter and require that it FAILS. If it passes,
        # the spec is not actually exercising the isolated path -- its category is
        # batch-allowed anyway -- and the spec proves nothing about R5. This is the
        # static half of the same contrast the shakedown spec proves live.
        isolated = [(n, s) for n, s, _ in self.specs if hlib.spec_batch_isolated(s)]
        self.assertTrue(
            isolated,
            "no committed spec drives an isolated batch, so the R5 seam argument "
            "has no gated user and could be deleted without reding anything")
        for name, spec in isolated:
            with self.subTest(spec=name):
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                self.assertIsNotNone(pin, name)
                # Self-standing (review F9): on an INTERIM pin
                # (`passed=[1-9][0-9]*`) batch_tally_pin_mismatches returns
                # "cannot be cross-checked" on BOTH paths, so the contrast below
                # would pass while proving nothing.
                self.assertTrue(
                    pin.statically_checkable,
                    "%s must pin a literal category= and scene= or the contrast "
                    "below is vacuous" % name)
                ordinary = hlib.batch_tally_pin_mismatches(
                    pin, self.decls, isolated=False)
                if ordinary:
                    continue
                # THE TALLY COULD NOT DISCRIMINATE. That is not automatically a
                # defect, and R7a is the case that showed why: for a category only
                # PARTLY batch-disabled, the isolated and ordinary paths differ in
                # the ATTRIBUTE FLOOR (Rewind: 5 vs 11) but a measured pin sits above
                # both floors, and `batch_tally_pin_mismatches` treats the floor as a
                # floor -- so `skipped=21` is arithmetically consistent with either
                # path and no tally comparison can separate them.
                #
                # What CAN separate them is the seam's own echo. The runner logs the
                # literal `isolated=true` on the RunTests step only when the arg is
                # present (ParsekTestCommandAddon.RunTestsImpl), so a run that lost,
                # ignored or misspelled the arg cannot print it, and
                # evaluate_expectations requires every `required` pattern to match.
                # So the spec must pin that token instead. This keeps the cell's
                # thesis intact -- a run that silently lost the arg still reds -- and
                # only changes WHICH pinned evidence carries it.
                selector = [s for _n, _s, s in self.specs if _n == name]
                category = (selector[0] or "").strip() if selector else ""
                req = ((spec.get("expectations", {}) or {})
                       .get("logContracts", {}) or {}).get("required", []) or []
                echo = "runtests start category=%s isolated=true" % category
                self.assertIn(
                    echo, req,
                    "%s declares isolated = \"true\" and its pinned tally is ALSO "
                    "satisfiable on the ordinary batch path, so the tally alone "
                    "cannot prove the isolated route ran. Either the category is "
                    "wholly batch-allowed (drop the arg), or the spec must pin the "
                    "seam's echo %r as the discriminator." % (name, echo))

    def test_the_spec_selector_matches_the_pinned_category(self):
        # A RunTests selector and the category= it pins are two independent copies
        # of one name. If they drift, the batch runs one category while the contract
        # gates another -- and hlib.resolve_batch_complete would red on a mismatch
        # only at run time, on the nightly.
        for name, spec, selector in self.specs:
            with self.subTest(spec=name):
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                if pin is None or pin.category is None or selector is None:
                    continue
                if hlib.is_multi_category_selector(selector):
                    continue
                self.assertEqual(
                    pin.category, selector.strip(),
                    "%s drives RunTests category=%r but pins category=%r"
                    % (name, selector, pin.category))


class IngameBatchWiringGroupTests(unittest.TestCase):
    """The H7-H20 + H22 in-game batch-wiring group: 15 batch-only specs that each drive
    one previously-undriven [InGameTest] category over a committed fixture.

    H7-H20 were committed as one wave; H22 joined the same family afterward, arriving
    with the Basic/Advanced UI-mode feature (it drives UiComplexityMode over the same
    gloops-airshow fixture on the ordinary, non-isolated batch path). H23 joined with
    R12 and is the one member that does NOT share the family's boot: its LoadGame
    carries `scene = "trackstation"`, so its batch runs in TRACKSTATION rather than
    FLIGHT. It belongs to the group anyway - one category, one RunTests step, one
    whole pinned tally - and the `scene=` token in that tally is precisely what the
    cross-check below has to agree with.

    The generic sweeps above already cover these specs as members of "every committed
    spec". This class asserts the properties that are specific to the GROUP and that
    a generic sweep cannot state: that the group is exactly this set (so a 17th
    arrives with its doc row rather than silently), that every member is non-vacuous
    when probed DIRECTLY rather than through validate_spec, and that each pinned
    total equals the count derived from the C# attributes for the category the spec
    actually drives.
    """

    # id -> (category, total, scene). The total is the ATTRIBUTE-EXACT declaration
    # count, re-derived below from Source/Parsek rather than trusted from this table;
    # the table exists so a category rename reds HERE with both names in the message.
    #
    # SCENE IS A PER-MEMBER PROPERTY, and it stopped being a constant on 2026-07-30.
    # Every member through H22 boots the gloops-airshow Focusable route and batches in
    # FLIGHT, so the scene used to be hardcoded in three cells below. H23 drives
    # `LoadGame scene=trackstation` and batches in TRACKSTATION, where the
    # scene-eligibility stage of derive_batch_tally answers completely differently -
    # all ten TrackingStation declarations scene-SKIP at FLIGHT. A hardcoded FLIGHT
    # would have derived total=10 skipped=10 for it and red on a CORRECT pin.
    GROUP = {
        "H7-trajectory-math":        ("TrajectoryMath", 8, "FLIGHT"),
        "H8-spawn-rotation":         ("SpawnRotation", 10, "FLIGHT"),
        "H9-incomplete-ballistic":   ("IncompleteBallistic", 11, "FLIGHT"),
        "H10-finalize-backfill":     ("FinalizeBackfill", 7, "FLIGHT"),
        "H11-pipeline-anchor":       ("Pipeline-Anchor", 7, "FLIGHT"),
        "H12-switch-segment":        ("SwitchSegment", 6, "FLIGHT"),
        "H13-ksp-api-smoke":         ("KSP", 6, "FLIGHT"),
        "H14-corpus-data-health":    ("DataHealth", 4, "FLIGHT"),
        "H15-corpus-ghost-visuals":  ("GhostVisuals", 4, "FLIGHT"),
        "H16-corpus-spawn-health":   ("SpawnHealth", 3, "FLIGHT"),
        "H17-flight-integration":    ("FlightIntegration", 4, "FLIGHT"),
        "H18-pipeline-smoothing":    ("Pipeline-Smoothing", 4, "FLIGHT"),
        "H19-recording-finalization": ("RecordingFinalization", 3, "FLIGHT"),
        "H20-eva-spawn-position":    ("EvaSpawnPosition", 2, "FLIGHT"),
        "H22-ui-complexity-mode":    ("UiComplexityMode", 4, "FLIGHT"),
        "H23-tracking-station":      ("TrackingStation", 10, "TRACKSTATION"),
        "H24-ksp-api-sanity":        ("KspApiSanity", 5, "FLIGHT"),
        "H25-serialization":         ("Serialization", 4, "FLIGHT"),
        "H26-log-contracts":         ("LogContracts", 10, "FLIGHT"),
        "H27-diagnostics":           ("Diagnostics", 6, "FLIGHT"),
        "H28-map-presence":          ("MapPresence", 5, "FLIGHT"),
        "H29-localized-name":        ("LocalizedName", 3, "FLIGHT"),
        "H30-ghost-audio":           ("GhostAudio", 9, "FLIGHT"),
        "H31-crew-reservation":      ("CrewReservation", 15, "FLIGHT"),
        "H32-snapshot-baseline":     ("SnapshotBaseline", 7, "FLIGHT"),
        "H33-recorded-signals":      ("RecordedSignals", 3, "FLIGHT"),
        # The first member whose category is only PARTLY reachable at its boot
        # scene: 45 of the 47 Logistics declarations are FLIGHT-scoped and
        # scene-skip at SPACECENTER, so its skip floor is 45 where every FLIGHT
        # member's is 0 or 1. That floor is ATTRIBUTE-derived, so it needs no
        # RUNTIME_SKIPS entry - the measured run skipped nothing at run time.
        # (Flown as `H32-logistics-inter-body`; renamed H34 post-merge after a
        # sibling lane landed a different H32 on main first. Nothing re-flown.)
        "H34-logistics-inter-body":  ("Logistics", 47, "SPACECENTER"),
        # The OTHER half of the SAME category, at the other scene - the first
        # time two members share a category, and it needs nothing special: the
        # tally is derived PER MEMBER from (category, scene), so H34's
        # SPACECENTER slice (45 scene-skipped, 2 executable) and H35's FLIGHT
        # slice (1 scene-skipped + 38 batch-skipped, 8 executable) each check
        # against their own derivation off the same 47 declarations. Adding a
        # Logistics [InGameTest] moves BOTH pins, in the same commit.
        # (Flown as `H33-logistics-route-proof`; renamed H35 post-merge for the
        # same collision.)
        "H35-logistics-route-proof": ("Logistics", 47, "FLIGHT"),
        # P5/P6's live half. Flown twice (PARSEK-FAIL 5/7 on two product defects,
        # then PASS 7/7 after both fixes), so its tally is now pinned WHOLE and it
        # has left INTERIM_PIN_IDS - see that set's comment for the measurement.
        "H36-playback-fidelity":     ("PlaybackFidelity", 7, "FLIGHT"),
        # P8's live half. LIVE-PROVEN 2026-08-12 on the re-fly (`total=5 passed=5 failed=0
        # skipped=0`) after a first flight that red 3/5 on one product defect and one
        # fixture bug; the pin is whole and the id has left INTERIM_PIN_IDS.
        "H37-part-event-fidelity":   ("PartEventFidelity", 5, "FLIGHT"),
        # PHASE-4 WAVE 1 (2026-08-29), thirteen lanes from the roster audit. They are
        # NOT a homogeneous block and the table should not be read as one: four boot
        # outside FLIGHT (two TRACKSTATION, two SPACECENTER), four inject the corpus,
        # two run over RECORDED fixtures and carry a kill triple, and five pin their
        # tally EXACTLY on first authoring while eight are reading runs. Which is which,
        # and WHY, is in each spec's own header; the discriminator throughout is a
        # REACHABILITY scan of the category's `InGameAssert.Skip` guards at that lane's
        # boot, never a guard count.
        "H42-claw-couple":           ("ClawCouple", 2, "FLIGHT"),
        "H43-terrain-clearance":     ("TerrainClearance", 6, "FLIGHT"),
        "H44-ghost-map-trackstation": ("GhostMap", 25, "TRACKSTATION"),
        "H45-stock-ui-overlay":      ("StockUiOverlay", 6, "SPACECENTER"),
        "H46-settings":              ("Settings", 5, "FLIGHT"),
        "H47-map-view":              ("MapView", 4, "TRACKSTATION"),
        "H48-ledger-drawdown":       ("Ledger", 4, "SPACECENTER"),
        "H49-tree-integrity":        ("TreeIntegrity", 4, "FLIGHT"),
        "H50-ghost-chains":          ("GhostChains", 4, "FLIGHT"),
        "H51-save-load":             ("SaveLoad", 4, "FLIGHT"),
        "H52-reentry-fx":            ("ReentryFx", 3, "FLIGHT"),
        "H53-scene-and-patch":       ("SceneAndPatch", 7, "FLIGHT"),
        "H54-missions":              ("Missions", 13, "FLIGHT"),
    }

    # Declared MEASURED run-time skips per member: InGameAssert.Skip firings the
    # ATTRIBUTES cannot predict, each with its reason recorded in the spec's
    # derivation comment and confirmed by a live run. The floor cells below add
    # these to the attribute-derived skip floor, so a member whose fixture
    # legitimately cannot satisfy one guard can still pin its tally WHOLE without
    # the group asserting a wrong split. Discipline for adding an entry: the skip
    # must be a FIXTURE property stated in the spec (H26: REC-002 skips on "No
    # committed recordings to validate" because career-pad-craft carries zero
    # recordings and injection is deliberately "none" - the corpus is not proven
    # against REC-002's point-count rule), never a way to absorb an unexplained
    # red. An empty entry and an absent entry mean the same thing; only nonzero
    # counts belong here.
    RUNTIME_SKIPS = {
        "H26-log-contracts": 1,
        # H28: three, all MEASURED on run 2026-08-05_1855 and all three the
        # W2-VACUOUS-CELLS conversions for this category (they used to bail
        # through a silent `return` and report PASSED, which is why the old
        # passed=5 pin was green).
        #   * GhostPidsResolveToProtoVessels and NoPidCollisionWithRealVessels
        #     skip on an empty GhostMapPresence.ghostMapVesselPids. THIS IS THE
        #     ENTRY TO READ TWICE: the spec injects an all-synthetic corpus
        #     specifically because the pre-2026-08-05 belief was that live
        #     ghosts would repopulate that set and de-vacuate both cells. The
        #     measured run says otherwise - the set is empty for the whole
        #     driven batch WITH the corpus injected, so it is a fixture property
        #     of the driven FLIGHT batch, not a missing injection.
        #   * AntennaSpecsProduceRelayPower skips because no recording among the
        #     306 committed carries AntennaSpecs - no generator sets the field
        #     at all, so this one is corpus-INDEPENDENT.
        "H28-map-presence": 3,
        # H31: three, MEASURED on run 2026-08-05_1857, on top of an attribute
        # floor of 1 (the SPACECENTER-scoped CrewAutoAssignPatch cell scene-skips
        # at FLIGHT) for a pinned skipped=4. ReplacementsAreValid,
        # NoSelfReplacements and NoCircularReplacements all walk
        # CrewReservationManager.CrewReplacements, empty under every committed
        # fixture x preset (ScenarioWriter.AddCrewReplacement has zero callers).
        # Fixture property, and the spec says so.
        "H31-crew-reservation": 3,
        # H35: three, MEASURED identically on all three 2026-08-11 runs (flown
        # under its pre-rename id `H33-logistics-route-proof`), on top
        # of an attribute floor of 39 (1 scene-skip + 38 AllowBatchExecution=
        # false) for a pinned skipped=42. All three are FIXTURE properties of
        # bdock-recorded, stated in the spec's STATUS block:
        #   * RouteProof_ActiveAsTargetDockWindow - the save's ONE route
        #     connection window has TransferTargetVesselPid == the recording's
        #     own pid (the same-craft-twice baked pid), so it satisfies the
        #     INITIATOR predicate and the target predicate is its strict
        #     complement.
        #   * RouteProof_CrossTreeCommittedPartner - its predicate short-circuits
        #     on the initiator case first (LogisticsRouteProofRuntimeTests.cs:
        #     81-82, "covered by sibling test"), so the same one window can never
        #     reach the cross-tree walk. NOT a claim that the fixture is
        #     single-tree: BDOCK-1 genuinely docks two committed trees.
        #   * RouteOriginProof_StartedDockedToNonKsc - the save carries ZERO
        #     ROUTE_ORIGIN_PROOF nodes because both BDOCK-1 flights start
        #     PRELAUNCH on the pad; the cell needs a mission that STARTS docked
        #     to a non-PRELAUNCH partner, which no committed profile produces.
        "H35-logistics-route-proof": 3,
        # PHASE-4 WAVE 1 (measured 2026-08-28). Only THREE of the eight new members owe
        # an entry; the rest skip on scene eligibility alone or not at all.
        #
        # H45 (`career-contract-pad`): both Mission Control overlay cells, identical
        # reason - "No Mission Control offered contract row with a non-empty title/Guid
        # is available (rows=0, contractRows=0, offeredRows=0, activeRows=0)". THE
        # COUNTERS ARE THE POINT: the live screen instantiated and was WALKED and found
        # nothing, so this is not the contract-picker rejecting a row's state - the
        # fixture puts no OFFERED contract in front of the screen at all. A fixture
        # property, exactly as the discipline above requires, and closable by a career
        # save carrying one offered contract with a non-empty title and Guid.
        "H45-stock-ui-overlay": 2,
        # H53 (`gloops-airshow` + the 274-row corpus): "No ghost map PIDs - patch not
        # exercised" and "No live active tree to use as a synth source". BOTH ARE
        # DRIVER-STATE rather than fixture properties - the first wants playback armed
        # so `ghostMapVesselPids` is non-empty, the second wants a `StartRecording`
        # before the batch - so neither is closable by harvesting a better save, and
        # both are left because closing them would cost this lane's `count = 274`
        # corpus-integrity assertion. Recorded here so the entry is not later mistaken
        # for a fixture shortfall.
        "H53-scene-and-patch": 2,
        # H54 (`duna-one-recorded`): four, and three of them are the same structural
        # fact rather than four separate gaps. `RealSaveMissionInGameTests.cs` holds
        # FOUR mission ARCHETYPES (re-aim, station rendezvous, joint landing+station
        # arrival, off-Kerbin pad launch) and any ONE real save is at most one or two of
        # them; this fixture is a single Kerbin->Duna re-aim mission, so it satisfies
        # exactly the re-aim archetype and skips the other three by construction. The
        # fourth is `DescentHandoff_OneGhostAtHandoff_...`, which FOUND this save's
        # 'Duna One' mission and rejected it for carrying no descent trigger (it is not
        # a looped LANDING arrival) while naming an OPERATOR save, `s15`, that is not a
        # committed fixture. Each is satisfiable by its OWN harvest; none by this one.
        "H54-missions": 4,
    }

    # NOTE the asymmetry this leaves: for 13 of the 16, the skipped= floor is
    # DERIVABLE from the attributes plus a reachable-Skip scan. For H20 it is MEASURED only - the
    # attributes put a floor of 0 on it and nothing more, and a fixture change that
    # moves the parent's collider geometry can legitimately make it skip. H22 is in
    # the same position: all three UiComplexityMode cells carry in-body
    # InGameAssert.Skip guards (no live ParsekUI, Gloops recording in progress), so
    # its skipped=0 is a claim about the gloops-airshow fixture that the 2026-07-28
    # run measured, not an attribute derivation. Only H18 among the remaining 13 has
    # a comparable caveat (its AssertHandlerRegistered helper skips if a KSP version
    # renames EventData<T>'s internal `events` field, unreachable on the pinned
    # 1.12.5). H23 is the THIRD measured-only member and the clearest case of the
    # asymmetry: its skipped=1 IS attribute-derived (one TrackingStation declaration
    # carries AllowBatchExecution = false), but its passed=9 is not - three members
    # guard on whether KSP built a Vectrosity orbit line for a synthetic ghost that
    # session, which no attribute predicts. The 2026-07-30 flight measured all three
    # satisfied.
    # EMPTY, and that is the healthy state: an interim pin is a temporary weakening
    # (the form accepts 1-of-N by design), so it should exist only between a spec
    # landing and its first PASSING flight. Three members passed through it, all gone:
    #
    #   H32-snapshot-baseline FLEW 2026-08-11 (run `2026-08-11_1111`, PASS) reading
    #   `total=7 passed=7 failed=0 skipped=0`. BOTH pre-flight reasons for leaving it
    #   loose turned out not to hold - the stock-minimal profile DOES carry Breaking
    #   Ground (the Clone phase junctions the dev install's whole
    #   `GameData/SquadExpansion`, Serenity robotics included), and the four
    #   deployable cells found stock prefabs whose animation clips do separate stow
    #   from deploy. So a future interim pin justified by "the profile lacks X" should
    #   CHECK the provisioned instance rather than reason from the profile's name.
    #
    #   H33-recorded-signals FLEW 2026-08-11 (run `2026-08-11_1118`, PASS attempt 1)
    #   reading `total=3 passed=3 failed=0 skipped=0`. Both cells whose run-time Skip
    #   guards motivated the loose form EXECUTED on stock-minimal - the stock chute
    #   prefab resolves both a canopy and a cap transform, and the stock rover wheel
    #   satisfied all four of its guards (motor module, resolved spin transform, a
    #   body-relative surface normal, and a spin axis not parallel to it) - so no
    #   RUNTIME_SKIPS entry is owed for either.
    #
    #   H36-playback-fidelity took TWO flights. EVERY one of its seven cells carries a
    #   run-time InGameAssert.Skip keyed on what the provisioned install loaded and on
    #   what the ghost builder resolved (an engine whose FX clone yields a captured
    #   magnitude baseline, an RCS block ditto, a deployable whose sampled poses
    #   differ, a resolvable ModuleGimbal / ModuleWheelSteering transform, a tracking
    #   pivot), so no attribute predicted the split. The first flight (2026-08-11, run
    #   `2026-08-12_0015`) was PARSEK-FAIL 5/7 on two PRODUCT defects, NOT on any of
    #   those guards - which is worth recording, because a red is not a measurement and
    #   the id stayed interim through it. The RE-FLY after both fixes (2026-08-12, run
    #   `2026-08-11_2211`, PASS attempt 1) read `total=7 passed=7 failed=0 skipped=0`:
    #   all seven guards were satisfied on stock-minimal, so no RUNTIME_SKIPS entry is
    #   owed and the pin is now whole.
    #
    #   H37-part-event-fidelity (P8) also took TWO flights, and its first one is the
    #   sharpest illustration yet of why this set exists. All five cells carry a run-time
    #   InGameAssert.Skip keyed on what the install loaded AND on what the ghost builder
    #   resolved, so no attribute predicted the split. The FIRST flight (2026-08-12) read
    #   `total=5 passed=3 failed=1 skipped=1`, and NEITHER non-green cell was a fixture
    #   shortfall of the kind the loose pin was hedging against:
    #     * the RED was a PRODUCT defect - ParticleSystem.Play() on a ghost that is not
    #       activeInHierarchy is a SILENT no-op, and a ghost is inactive for the whole of
    #       its spawn-time prefix replay, so an EVA ghost spawning mid-burst stayed dark
    #       for the entire burst while the log claimed it was emitting; and
    #     * the SKIP was a FIXTURE BUG, not an install property - the cell's precondition
    #       tested POSITION only while a science canister's Deploy clip swings its doors,
    #       so it was blind to the one motion the part has. The re-fly measured
    #       `span(pos=0 rot=29.99998)` on mk2LanderCabin.v2: a literally ZERO position
    #       span, which is the diagnosis in one number.
    #   THE LESSON, which is the durable part: a loose `passed=` hedges against the
    #   install, but the things it actually caught here were a product bug and a test bug.
    #   Do not read a non-green interim flight as "the profile lacks X" - measure which of
    #   the three it is. The RE-FLY after both fixes (2026-08-12, PASS attempt 1) read
    #   `total=5 passed=5 failed=0 skipped=0`, every verifier green (analyzer red=0,
    #   anomalySweep hits=[], expectations mismatches=0, unityExceptions 0), so all five
    #   guards were satisfied on stock-minimal, no RUNTIME_SKIPS entry is owed, and the pin
    #   is now whole.
    #
    # All four specs now pin their tallies whole, so the set was empty again.
    #
    # PHASE-4 WAVE 1 (2026-08-29) PUT EIGHT MEMBERS BACK IN IT, and the wave is worth
    # reading as a single lesson about this set: the ROSTER AUDIT that proposed these
    # thirteen lanes claimed nine of them had "zero self-skips" and could be pinned
    # exactly. Re-deriving each one against the source before authoring says FIVE can.
    # The audit counted guards in the wrong place - it read whether a category's cells
    # LOOK guarded, where the question is whether a guard can FIRE at the lane's own
    # boot. Both directions of that error appeared:
    #   * `H42` (ClawCouple) and `H52` (ReentryFx) have guarded bodies and still pin
    #     EXACTLY, because a `PartLoader not ready` check cannot fire in a loaded FLIGHT
    #     scene and an atmosphere check cannot fire on Kerbin.
    #   * `H43` (TerrainClearance), `H44` (GhostMap) and `H45` (StockUiOverlay) were all
    #     proposed as exact and are NOT, because each hides a guard family the audit did
    #     not open: three live-geometry conditions on the explosion-anchor cell, a
    #     marker-decision cell that needs an active PAD vessel and therefore cannot pass
    #     at TRACKSTATION AT ALL, and a contract-picker plus building-instance pair.
    # The rule the wave leaves behind: an exact pin is earned by an enumerated
    # reachability argument in the spec header, one line per guard, or it is not earned.
    #
    # The four corpus-backed and recorded-host members here are interim for a further
    # reason worth separating: their guards are about what the STORE contains
    # ("No branch point children to check", "No ghost chains computed", "No live active
    # tree to use as a synth source"), and what an INJECTED corpus actually lands is a
    # different statement from what its generator can build. No committed spec has
    # measured it.
    #
    # AND ALL EIGHT LEFT ON 2026-08-29, one flight each, every one PASS attempt 1.
    # Measured splits: H43 6/0/0, H44 9/0/16, H45 4/0/2, H46 4/0/1, H49 4/0/0,
    # H50 4/0/0, H53 2/0/5, H54 3/0/10. Four things the wave settled, kept here because
    # they are what the NEXT interim member should be reasoned against:
    #
    #   1. THE INTERIM FORM EARNED ITS KEEP TWICE, BOTH TIMES BY THE PREDICTION BEING
    #      TOO PESSIMISTIC RATHER THAN TOO OPTIMISTIC. H43's explosion-anchor cell
    #      satisfied all three of its live-geometry guards, and BOTH of H46's
    #      IMGUI-layout cells passed - so an unattended FLIGHT batch DOES lay out a
    #      Settings window and DOES produce Repaint passes, which is the H22-class
    #      unknown answered for anyone writing the next IMGUI cell.
    #   2. **H44 REFUTED ITS OWN PREDICTION, AND THE ERROR IS INSTRUCTIVE.** Its header
    #      predicted `skipped=17` because `MarkerDrawDecision_DispatchesOnLiveGate_NoGap`
    #      "cannot pass at TRACKSTATION" - it guards on an active PAD vessel. It passed,
    #      in 0.9 ms, and the source says why: that cell (`RuntimeTests.cs:10820`)
    #      carries NO GUARDS AT ALL. The pad-vessel strings belong to
    #      `FlightIntegrationTests` (`:3197`, `:10934`), a different class in the same
    #      10,000-line file. A guard was attributed by PROXIMITY rather than by the
    #      method body containing it - the "source-derived guards use AST" house rule,
    #      arrived at from the other direction. NOTE THE PARAGRAPH ABOVE STILL SAYS
    #      "cannot pass at TRACKSTATION AT ALL"; it is left standing deliberately, as
    #      the record of what was believed before the flight.
    #   3. TWO MEMBERS UNDER-RAN THEIR DERIVED EXECUTABLE AND NEITHER IS A DEFECT.
    #      H53 ran 2 of 4: both residual skips are DRIVER-state requirements (ghost map
    #      pids need playback armed; the Bug266 cell wants a live active tree that a
    #      `StartRecording` step would create), so both are satisfiable but NOT by a
    #      better save. H54 ran 3 of 7, and its finding bounds every future `Missions`
    #      lane: `RealSaveMissionInGameTests.cs` holds FOUR mission ARCHETYPES and any
    #      one real save is at most one or two of them, so "7 executable" is an
    #      attribute-level ceiling no single-mission fixture can reach.
    #   4. A FIXTURE NAMED BY A SKIP STRING NEED NOT SATISFY IT. H54 boots
    #      `duna-one-recorded`; its descent-handoff cell FOUND the 'Duna One' mission,
    #      walked it, rejected it for not being the looped LANDING variant, and named an
    #      OPERATOR save (`s15`) that is not a committed fixture at all. Reading a guard
    #      string as a fixture spec is a hypothesis, not a derivation.
    #
    # Only H45 / H53 / H54 owe a RUNTIME_SKIPS entry (2 / 2 / 4). H44's 16 and H46's 1
    # are PURE SCENE FILTERING - the runner's own `Scene eligibility skip summary` line
    # accounts for every one and neither run contains a single per-test `SKIPPED:` line -
    # and that distinction is the one to keep straight: a scene skip is a lane's SCOPE,
    # a run-time skip is its DEBT, and only the second is something a fixture could pay.
    #
    # It must stay a set LITERAL of ids (or a `set()` call when empty, never a `{}`
    # literal, which would be an empty DICT - the two membership cells below would then
    # answer False for every id and pass vacuously).
    INTERIM_PIN_IDS: set = set()

    # Every committed spec whose id matches this is an H-SERIES batch spec.
    # Membership is DISCOVERED from disk and then compared for set equality against
    # GROUP, which is what makes "a 17th spec arrives with its doc row" true: an
    # id-filtered intersection (the first cut) could only ever see members that were
    # in GROUP already, so a brand-new spec on disk was invisible to every cell here.
    #
    # R5 WIDENED THE PATTERN from `^H(?:[7-9]|1[0-9]|20)-` to any two-digit H id.
    # The old pattern stopped dead at H20, so the very next spec committed -- H21 --
    # would have reproduced the exact hole the set-equality cell was written to
    # close, silently and on its first day. It still excludes H5 / H6, which are
    # single-digit and predate this group.
    #
    # The discovered set is then PARTITIONED on each spec's own batch mode
    # (hlib.spec_batch_isolated), never on a hardcoded id list: this group is the
    # ORDINARY-path family, IsolatedBatchWiringGroupTests below owns the isolated
    # one, and the partition key comes from the spec itself so a member cannot drift
    # into the wrong family or fall between them.
    GROUP_ID_RE = re.compile(r"^H(?:[7-9]|[1-9][0-9]+)-")

    @classmethod
    def setUpClass(cls):
        cls.decls = load_ingame_test_declarations()
        cls.specs = {}
        cls.on_disk = set()
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            spec = load_spec(name)
            sid = spec.get("id") or ""
            if cls.GROUP_ID_RE.match(sid) and not hlib.spec_batch_isolated(spec):
                cls.on_disk.add(sid)
            if sid in cls.GROUP:
                cls.specs[sid] = spec

    def test_the_group_table_is_not_empty(self):
        # ANTI-VACUITY FLOOR, and the reason it exists is this PR's own thesis.
        # Every other cell in this class iterates `self.specs`, which is built by
        # filtering the committed specs against GROUP. Delete an entry from GROUP and
        # that spec silently drops out of all of them; empty GROUP entirely and all
        # eight cells pass over ZERO specs while asserting nothing. The membership
        # cell below cannot catch either, because it compares two sets that shrink
        # together. Same shape as CommittedBatchTallySourceSyncTests's
        # test_the_source_tree_is_actually_readable.
        self.assertEqual(43, len(self.GROUP),
                         "the H7-H20 + H22-H37 + Phase-4 Wave 1 (H42-H54) group is 43 "
                         "specs; if it genuinely changed size, update this floor AND the "
                         "counts in docs/dev/autotest-ingame-category-inventory.md and "
                         "docs/dev/autotest-status.md in the same commit")
        # A RUNTIME_SKIPS key for a non-member is silently inert (both floor
        # cells read it via .get(sid, 0) over GROUP members only), so a stale
        # entry for a removed/renamed spec would linger forever. Fail loud here.
        self.assertLessEqual(
            set(self.RUNTIME_SKIPS), set(self.GROUP),
            "RUNTIME_SKIPS names spec ids that are not GROUP members: %s"
            % sorted(set(self.RUNTIME_SKIPS) - set(self.GROUP)))
        self.assertEqual(len(self.GROUP), len(self.specs),
                         "GROUP names %d specs but only %d were loaded from %s - the "
                         "rest of this class would assert over the missing ones' "
                         "absence" % (len(self.GROUP), len(self.specs), SCENARIOS_DIR))

    def test_the_group_is_exactly_the_committed_set(self):
        # SET EQUALITY against what is on disk, not an intersection: this fires both
        # when a listed member is removed/renamed AND when a new H-series spec is
        # committed without being added here.
        self.assertEqual(sorted(self.on_disk), sorted(self.GROUP),
                         "the H-series ordinary-path specs on disk differ from the "
                         "table in this "
                         "test. A spec here but not on disk was removed or renamed; a "
                         "spec on disk but not here is new and must be added to GROUP, "
                         "to the enumeration table in "
                         "docs/dev/autotest-ingame-category-inventory.md, and to the "
                         "section table + scenario total in "
                         "docs/dev/autotest-status.md, in the same commit")

    def test_each_drives_exactly_one_named_category(self):
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                steps = (spec.get("driver", {}) or {}).get("steps", []) or []
                run_tests = [s for s in steps if (s or {}).get("cmd") == "RunTests"]
                self.assertEqual(1, len(run_tests),
                                 "%s must own exactly one RunTests batch "
                                 "(hlib.SINGLE_BATCH_SELECTOR_RULE)" % sid)
                selector = (run_tests[0].get("args", {}) or {}).get("category")
                self.assertEqual(self.GROUP[sid][0], selector)
                self.assertFalse(hlib.is_multi_category_selector(selector))

    def test_none_is_vacuous_when_probed_directly(self):
        # Probed through batch_contract_vacuity_gap itself, not via validate_spec:
        # the whole point of the group is that none of them can read GREEN over zero
        # executed tests, and that property should be asserted against the probe
        # rather than inherited from another test's pass.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                self.assertNotIn(hlib.BATCH_VACUITY_OPT_OUT_KEY, lc,
                                 "%s must not opt out of the anti-vacuity gate" % sid)
                gap = hlib.batch_contract_vacuity_gap(
                    lc.get("required", []) or [], self.GROUP[sid][0])
                self.assertIsNone(gap, "%s accepts a vacuous batch: %s" % (sid, gap))

    def test_each_pinned_total_equals_the_source_derivation(self):
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, expected_total, scene = self.GROUP[sid]
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                self.assertTrue(pin.statically_checkable, sid)
                self.assertEqual(pin.category, category)
                self.assertEqual(pin.scene, scene,
                                 "%s: the scene its pin claims must be the scene this "
                                 "table says it boots into - FLIGHT for the "
                                 "gloops-airshow Focusable route, TRACKSTATION for a "
                                 "spec carrying LoadGame scene=trackstation" % sid)
                derived = hlib.derive_batch_tally(self.decls, category, scene)
                self.assertEqual(derived.total, expected_total,
                                 "%s: the source now declares %d %s test(s), not %d"
                                 % (sid, derived.total, category, expected_total))
                self.assertEqual(pin.total, derived.total)
                self.assertEqual([], hlib.batch_tally_pin_mismatches(pin, self.decls))

    def test_whole_tally_members_pin_the_attribute_derived_skip_floor(self):
        # What this actually checks, stated precisely because it is weaker than it
        # looks for three members: the ATTRIBUTES give a skipped FLOOR at the scene
        # each member boots into, and the pinned tally must agree with it exactly -
        # so passed = total - skipped and failed = 0. For 13 of the 16 that floor is
        # 0 and, plus a reachable-Skip scan, makes skipped=0 genuinely DERIVABLE. For
        # H20 and H22 the floor is all the attributes give and their skipped=0 is
        # MEASURED off a live run; H23's floor is 1 (one TrackingStation declaration
        # carries AllowBatchExecution = false) while its passed=9 is likewise
        # measured. For those three this cell confirms consistency, not derivability.
        # If a member later gains a scene-mismatched or AllowBatchExecution=false
        # declaration, this reds pointing at the member.
        #
        # RENAMED 2026-07-30 from ..._pin_a_derivable_zero_skip. The old name and its
        # hardcoded assertEqual(0, attribute_skipped) encoded "no member ever skips"
        # as a group invariant, which held only while every member ran a whole
        # category, in one scene, with nothing batch-disabled in it.
        for sid, spec in sorted(self.specs.items()):
            if sid in self.INTERIM_PIN_IDS:
                continue
            with self.subTest(spec=sid):
                category, _, scene = self.GROUP[sid]
                derived = hlib.derive_batch_tally(self.decls, category, scene)
                # RUNTIME_SKIPS entries are declared measured skips on top of the
                # attribute floor (see the map's comment); zero for most members.
                expected_skipped = (derived.attribute_skipped
                                    + self.RUNTIME_SKIPS.get(sid, 0))
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                self.assertEqual(
                    (pin.passed, pin.failed, pin.skipped),
                    (derived.total - expected_skipped, 0, expected_skipped),
                    "%s: pinned tally disagrees with the attribute derivation "
                    "plus declared RUNTIME_SKIPS at scene=%s (scene-skipped %s, "
                    "batch-skipped %s, declared runtime %d)"
                    % (sid, scene, derived.scene_skipped_members,
                       derived.batch_skipped_members,
                       self.RUNTIME_SKIPS.get(sid, 0)))

    def test_the_interim_pin_member_is_declared_and_deliberately_loose(self):
        # Guards the OTHER direction: the interim form accepts 1-of-N by design, so
        # an accidental interim pin is a real weakening. Exactly the declared member
        # may leave passed / skipped unpinned, and it must still pin total literally.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                # Resolved INSIDE the subTest: a spec whose pin fails to resolve
                # would otherwise error the whole cell without naming which one.
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                loose = pin.passed is None or pin.skipped is None
                self.assertEqual(sid in self.INTERIM_PIN_IDS, loose,
                                 "%s: interim-vs-whole pin state disagrees with "
                                 "INTERIM_PIN_IDS" % sid)
                self.assertIsNotNone(pin.total,
                                     "%s must pin total= even when the split is "
                                     "unmeasured" % sid)

    def test_each_pin_matches_the_line_the_runner_would_actually_print(self):
        # END-TO-END round trip, the guard the other cells cannot give: synthesize
        # the exact BATCH_COMPLETE line InGameTestRunner emits for the derived tally,
        # and require (a) the spec's pattern MATCHES it, (b) the same pattern REJECTS
        # the vacuous line for the same category, and (c) hlib's own runtime parser
        # reads the category back unchanged. (c) is what would have caught the
        # hyphen defect from the run-time side rather than the static side.
        prefix = "[LOG 00:00:00.000] [Parsek][INFO][TestRunner] "
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, total, scene = self.GROUP[sid]
                derived = hlib.derive_batch_tally(self.decls, category, scene)
                skipped = (derived.attribute_skipped
                           + self.RUNTIME_SKIPS.get(sid, 0))
                real = (prefix + "BATCH_COMPLETE v1 total=%d passed=%d failed=0 "
                        "skipped=%d category=%s scene=%s"
                        % (total, total - skipped, skipped, category, scene))
                vacuous = (prefix + "BATCH_COMPLETE v1 total=%d passed=0 failed=0 "
                           "skipped=%d category=%s scene=%s"
                           % (total, total, category, scene))
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                batch_pats = [p for p in (lc.get("required", []) or [])
                              if "BATCH_COMPLETE" in p]
                self.assertEqual(1, len(batch_pats),
                                 "%s: expected exactly one BATCH_COMPLETE pattern" % sid)
                pat = batch_pats[0]
                self.assertRegex(real, pat,
                                 "%s: its own pin does not match the line the runner "
                                 "would print for the derived tally" % sid)
                self.assertNotRegex(vacuous, pat,
                                    "%s: its pin ACCEPTS the vacuous line" % sid)
                parsed = hlib.parse_batch_complete_line(real)
                self.assertIsNotNone(parsed, sid)
                self.assertEqual(parsed.category, category,
                                 "%s: the runtime parser and the pin disagree about "
                                 "the category token" % sid)

    def test_corpus_backed_members_inject_and_pin_the_corpus(self):
        # The four members whose category walks RecordingStore would PASS over an
        # empty store while asserting over zero items (two of them by a silent
        # `yield break` that is not even reported as a Skip). The tally cannot see
        # that; only the fixture can. So those four must inject the corpus AND pin a
        # non-zero recordings count, and the others must pin zero so a leak reds.
        # Wave 2 added three: H27 (StorageBreakdown walks the store), H28 (two
        # cells bail through a SILENT return on an empty ghost-map pid set -
        # exactly the fourth-trap shape this cell exists for) and H30 (the
        # engine-level pause/unpause cell iterates the live ghost set).
        # H33 is in this set for a DIFFERENT reason than the other seven, and the
        # difference is worth stating: its three cells do NOT walk RecordingStore
        # (each builds its own single-part ghost from a PartLoader prefab), so
        # injection is not an anti-vacuity guard for the cells. It injects because
        # its subject IS the two corpus rows `recorded-signal-fixes` added (the
        # chute-repack showcase and the surface rover drive), and its count pin is
        # the only committed assertion that those two land through the sidecar /
        # schema-gate load path. Same requirement either way: inject AND pin
        # non-zero, so a corpus that silently stopped landing reds.
        # PHASE-4 WAVE 1 adds four, all of the ORIGINAL shape rather than H33's: each
        # walks `RecordingStore` and would bail through its own guards over an empty
        # store while the tally still read green. H44's parity and polyline cells, H49's
        # tree-topology cells and H50's chain cells are the purest instances of that
        # trap in the tree - three of H50's four guards are literally "No committed
        # trees" - and H53 injects for ONE cell rather than for the batch (no ghosts, no
        # ghost-map pids, nothing for the patch cell to check).
        corpus_backed = {"H14-corpus-data-health", "H15-corpus-ghost-visuals",
                         "H16-corpus-spawn-health", "H17-flight-integration",
                         "H27-diagnostics", "H28-map-presence", "H30-ghost-audio",
                         "H33-recorded-signals",
                         "H44-ghost-map-trackstation", "H49-tree-integrity",
                         "H50-ghost-chains", "H53-scene-and-patch"}
        # THE THIRD SHAPE (wave 3, the spec now called H35). A RECORDED-FIXTURE member injects
        # NOTHING - `injectedRecordings = "none"`, same as the zero-pin majority -
        # but its saveTemplate is a harvested save whose own COMMITTED recordings
        # ARE the payload the batch walks. So the zero-pin rule is not merely
        # wrong for it, it is backwards: pinning 0/0 would demand the fixture's
        # entire point leak out of the produced save. It still owes the same
        # anti-vacuity guarantee the corpus-backed set owes, discharged three
        # ways: a nonzero count, an EXACT count (min == max - a range would let a
        # fixture quietly shrink toward the vacuous end), and a template that
        # really carries .prec sidecars on disk, checked mechanically below so a
        # future re-harvest that dropped them reds HERE rather than as a batch of
        # silent Skips on the next flight.
        # NOTE (post-review cross-reference): this bucket's anti-vacuity
        # guarantee is deliberately DISTRIBUTED - the on-disk check below only
        # proves sidecars exist; the payload's IDENTITY and full shape are
        # owned by test_saveparse.py's committed-fixture closure
        # (test_fixture_set_is_exactly_the_committed_set forces every
        # fixtures/saves/ dir into either the zero-recording set or the fully
        # pinned RECORDED_FIXTURES). A wrong-but-present payload reds THERE.
        # PHASE-4 WAVE 1 adds two, and they are the two that also carry a KILL TRIPLE
        # for the same reason H35 does: a recorded host resumes a promotion-stub
        # recording ~1 s into the scene before any step can run. `H51-save-load` needs
        # `mun-orbit-recorded` because its sidecar-probe cell skips outright without
        # "current-format committed recordings"; `H54-missions` needs
        # `duna-one-recorded` because the re-aim cells' skip strings NAME that mission
        # ("load s15 (the Kerbin->Duna 'Duna ...')").
        recorded_fixture = {"H35-logistics-route-proof", "H51-save-load",
                            "H54-missions"}
        self.assertEqual(set(), corpus_backed & recorded_fixture,
                         "a member cannot be both corpus-backed and "
                         "recorded-fixture; the two rules contradict")
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                fixture = spec.get("fixture", {}) or {}
                count = ((spec.get("expectations", {}) or {})
                         .get("recordings", {}) or {}).get("count", {}) or {}
                if sid in corpus_backed:
                    self.assertEqual("all-synthetic", fixture.get("injectedRecordings"))
                    self.assertGreater(count.get("min", 0), 0,
                                       "%s must pin a non-zero corpus count - it is "
                                       "the only guard against a store walk over "
                                       "nothing" % sid)
                elif sid in recorded_fixture:
                    self.assertEqual("none", fixture.get("injectedRecordings"),
                                     "%s carries its payload in the TEMPLATE; "
                                     "injecting on top would make the pinned count "
                                     "un-attributable" % sid)
                    self.assertGreater(count.get("min", 0), 0,
                                       "%s must pin a non-zero count - its whole "
                                       "premise is that the batch walks recorded "
                                       "state" % sid)
                    if sid not in self.INTERIM_PIN_IDS:
                        self.assertEqual(
                            count.get("min"), count.get("max"),
                            "%s must pin its count EXACTLY (min == max): a range cannot "
                            "tell a load-time optimizer split from a leaked promotion "
                            "stub. A WINDOW is allowed only while the id is declared in "
                            "INTERIM_PIN_IDS, i.e. before its first flight" % sid)
                    template = fixture.get("saveTemplate", "")
                    self.assertTrue(template.startswith("fixtures/saves/"), sid)
                    rec_dir = os.path.join(
                        HARNESS_ROOT, template.replace("/", os.sep),
                        "Parsek", "Recordings")
                    self.assertTrue(os.path.isdir(rec_dir),
                                    "%s: %s carries no Parsek/Recordings, so the "
                                    "batch would walk nothing" % (sid, template))
                    precs = [f for f in os.listdir(rec_dir) if f.endswith(".prec")]
                    self.assertGreater(
                        len(precs), 0,
                        "%s: %s carries no .prec sidecars - the recorded payload is "
                        "gone and every read-side cell would silently Skip"
                        % (sid, template))
                    self.assertGreaterEqual(
                        count.get("min", 0), len(precs),
                        "%s pins count=%s but its template stages %d .prec files; "
                        "the pin must be at least the staged floor"
                        % (sid, count.get("min"), len(precs)))
                else:
                    self.assertEqual("none", fixture.get("injectedRecordings"))
                    self.assertEqual({"min": 0, "max": 0}, count,
                                     "%s pins no recordings, so a leak into the save "
                                     "must red" % sid)


def discover_isolated_spec_ids(named_specs):
    """Spec ids whose OWN batch mode is isolated, from (id, spec) pairs.

    Factored out of IsolatedBatchWiringGroupTests.setUpClass so the discovery RULE
    is testable with synthetic input. As an inline expression it could be swapped
    for `sid in GROUP` with the whole suite green, because with exactly one isolated
    spec on disk the discovered set and the table coincide - the membership cell
    then compares two sets that are equal for the wrong reason.
    """
    return {sid for sid, spec in named_specs if hlib.spec_batch_isolated(spec)}


class IsolatedBatchWiringGroupTests(unittest.TestCase):
    """The R5 isolated-batch group: specs whose RunTests step carries
    `isolated = "true"`, routing to InGameTestRunner's *IncludingFlightRestore
    entry point so tests that are AllowBatchExecution=false but
    RestoreBatchFlightBaselineAfterExecution=true actually execute.

    Membership is DISCOVERED from each spec's own batch mode, not from an id
    pattern, and then compared for set equality against GROUP -- the same shape
    IngameBatchWiringGroupTests uses, for the same reason. The two groups partition
    the H-series between them: this one asserts the properties an isolated spec has
    that an ordinary one does not, above all that its category is one the ordinary
    path genuinely cannot run.
    """

    # id -> (category, total). Re-derived below from Source/Parsek rather than
    # trusted; the table exists so a rename reds HERE with both names named.
    GROUP = {
        "H21-scene-exit-merge-isolated": ("SceneExitMerge", 2),
        "R7a-rewind-session-absent": ("Rewind", 38),
        # The THIRD slice of `Logistics` and the one that is actually the category:
        # H34 owns its 2 SPACECENTER-eligible declarations and H35 the 8 the ORDINARY
        # FLIGHT filter admits, while the other 38 are AllowBatchExecution = false +
        # RestoreBatchFlightBaselineAfterExecution = true and were reachable by no
        # unattended path at all. Isolated executable at FLIGHT is 46 vs the ordinary
        # 8. FLOWN TWICE 2026-08-28 and PINNED WHOLE.
        "H38-logistics-isolated": ("Logistics", 47),
        # The SAME category on TWO RECORDED hosts, and the reason there are
        # three Logistics members rather than one: the tally is derived PER
        # MEMBER from (category, scene), but the run-time split is a FIXTURE
        # property, and these two hosts carry recorded state `logi-cargo-pad`
        # cannot. H38 measured seven run-time skips and named five of them as
        # ONE debt - a dock-window / origin-proof RECORDED subject - which is
        # exactly what these two answer. BOTH FLOWN 3x on 2026-08-28 and pinned
        # whole (34/0/13 and 35/0/12): each pays TWO of H38's five, and the
        # remaining three are a HARVEST requirement now proven on both hosts.
        "H39-logistics-isolated-bdock": ("Logistics", 47),
        "H40-logistics-isolated-depot-route": ("Logistics", 47),
        # A DIFFERENT CATEGORY, and the first member whose isolated arg buys exactly ONE
        # cell. `LogisticsGrapple` has 4 declarations of which one - the
        # self-provisioning GrappleCapture cell - is AllowBatchExecution = false +
        # RestoreBatchFlightBaselineAfterExecution = true. See
        # TALLY_CANNOT_DISCRIMINATE_IDS below for why that single-cell margin changes
        # which pinned evidence carries the proof.
        "H41-logistics-grapple-isolated": ("LogisticsGrapple", 4),
    }

    # Members whose category is only PARTLY batch-disabled, i.e. the ordinary path
    # would execute SOME of it. THE WHOLE CLASS WAS WRITTEN AGAINST H21, whose
    # category is WHOLLY batch-disabled, and three cells below silently encoded that
    # coincidence as a group invariant. R7a is the first member where it does not
    # hold, and the generalisations are marked GENERALISED-BY-R7A where they occur.
    #
    # For a member listed here the isolated arg earns its place by admitting
    # STRICTLY MORE than the ordinary filter rather than by being the only way to
    # run anything; the strict "ordinary executes zero" property is still asserted
    # for every member NOT listed here, so H21 does not lose a check.
    #
    # H38 joins for the same reason with a wider margin: at FLIGHT the ordinary
    # filter admits 8 `Logistics` declarations (which H35 already flies) and the
    # isolated one admits 46, so the arg buys 38 real cells.
    # H39 and H40 join on the identical arithmetic - the derivation is
    # attribute-level, so it is the same 8-vs-46 for every Logistics member
    # regardless of which host it boots.
    #
    # H41 joins with the NARROWEST margin the set has ever held: ordinary 3, isolated 4.
    # The arg still does real work - it is the only way to run the capture cell at all -
    # but one cell of margin is what makes its tally non-discriminating, which is a
    # separate declaration (TALLY_CANNOT_DISCRIMINATE_IDS).
    PARTLY_BATCH_DISABLED_IDS = {"R7a-rewind-session-absent",
                                 "H38-logistics-isolated",
                                 "H39-logistics-isolated-bdock",
                                 "H40-logistics-isolated-depot-route",
                                 "H41-logistics-grapple-isolated"}

    # Members whose BATCH_COMPLETE line cannot distinguish the isolated path from the
    # ordinary one, whatever it is pinned to, so the discrimination duty transfers to
    # the two isolated-path-only tokens every member already owes.
    #
    # WHY THIS IS NOT A LOOPHOLE. `CommittedBatchTallySourceSyncTests` already reasons
    # this way for R7a - "What CAN separate them is the seam's own echo ... the spec
    # must pin that token instead. This keeps the cell's thesis intact - a run that
    # silently lost the arg still reds - and only changes WHICH pinned evidence carries
    # it." What is new here is that the condition is now DERIVED rather than argued: the
    # cell below re-computes `isolated.executable - ordinary.executable` from the
    # attributes and grants the exemption ONLY at a margin of exactly 1, where a single
    # run-time skip anywhere collapses the two lines onto each other. A member with a
    # wider margin is refused the exemption and must keep discriminating on the tally,
    # so this cannot be used to escape a floor that was merely inconvenient.
    #
    #   H41-logistics-grapple-isolated - ordinary 3, isolated 4, and one of the four
    #   (`GrappleWindow_LiveRecordedClawCouple_StampedGrapple`) is a CERTAIN skip on any
    #   host without a persisted Grapple window. Predicted isolated line
    #   `total=4 passed=3 failed=0 skipped=1`, byte-identical to what the ordinary path
    #   prints when nothing self-skips.
    TALLY_CANNOT_DISCRIMINATE_IDS = {"H41-logistics-grapple-isolated"}

    # Members whose tally split has NOT been measured yet, mirroring
    # IngameBatchWiringGroupTests.INTERIM_PIN_IDS for the isolated family. A member
    # listed here may leave `passed=` and `skipped=` unpinned (a regex class rather
    # than a literal); it must still pin `total=` literally, and the two cells below
    # that would otherwise demand the whole split skip it.
    #
    # EMPTY IS ITS HEALTHY STATE. The obligation an entry carries: the FIRST flight
    # measures the split, the spec's pin is replaced with the whole tally, a
    # MEASURED_SKIPPED entry is added if the run-time guards push `skipped` above the
    # attribute floor, and the id LEAVES this set in the same commit. An interim pin
    # that outlives its first flight is a weakening, not a convenience.
    #
    # WHAT IS STILL ASSERTED FOR AN INTERIM MEMBER, so this is not a hole: the
    # `total=` literal is checked against the source derivation by
    # test_each_pinned_total_agrees_with_the_isolated_derivation, the seam echo and
    # the LITERAL restore-count token are still demanded, the ordinary-path contrast
    # (isolated admits strictly more) is still derived both ways, and
    # test_each_pin_rejects_both_the_vacuous_and_the_non_isolated_line still runs
    # unchanged - which is the load-bearing one, because it is what forces the
    # interim spelling to be a floor ABOVE the ordinary path's executable ceiling
    # rather than the usual `passed=[1-9][0-9]*`. When H38 was a member that ceiling
    # was 8, so it pinned `passed=(?:9|[1-9][0-9]+)` rather than the plain interim
    # spelling, which would have accepted `passed=8` - the exact line a run that
    # silently lost the isolated arg prints.
    #
    # BACK TO EMPTY ON 2026-08-28, WHICH IS THE OBLIGATION BEING DISCHARGED RATHER
    # THAN A LOOSENING. `H38-logistics-isolated` was the one member, authored as a
    # reading run because its 46 admitted cells guard at run time on things no
    # attribute predicts (what UnloadedFuelVesselFixture managed to snapshot and
    # re-spawn, live LF stored/free floors, inventory PROBE ORDER as KSP actually
    # walks it, a converter that will activate on the pad, warp/unpack races). It has
    # now FLOWN TWICE - `2026-08-28_1802` (PARSEK-FAIL(results): one product defect,
    # the D4 harvest rails funnel, fixed at f98d5477a, plus two test defects) and
    # `2026-08-28_1833` (PASS attempt 1) - and run 2 measured
    # `BATCH_COMPLETE v1 total=47 passed=39 failed=0 skipped=8 category=Logistics
    # scene=FLIGHT`. The spec now pins that whole, its `skipped=8` is declared in
    # MEASURED_SKIPPED below, and the id leaves here in the same commit.
    # test_the_interim_pin_members_are_declared_and_deliberately_loose is what makes
    # that simultaneous: it requires the declared set and the OBSERVED looseness to
    # agree exactly, so a whole pin left declared here reds, and so does an interim
    # pin left undeclared.
    #
    # It must stay a set LITERAL of ids (or a `set()` call when empty, never a `{}`
    # literal, which would be an empty DICT and make every membership read False).
    #
    # IT WENT BACK TO TWO ON 2026-08-28, in the same wave, for the two RECORDED-host
    # Logistics lanes `H39-logistics-isolated-bdock` and
    # `H40-logistics-isolated-depot-route`. They were interim for a DIFFERENT reason
    # than H38 was, and the difference decided what their first flights meant. H38's
    # unknown was whether a purpose-BUILT craft satisfied five preconditions - a
    # property of a file this repo authors. Theirs was what a RECORDED CORPUS happens
    # to CONTAIN: which dock windows exist and on which branch, which committed
    # recordings started in PRELAUNCH, whether a committed route survives the
    # load-time optimizer. No attribute, no craft property and no amount of reading
    # the .sfs settles a run-time `InGameAssert.Skip` that walks committed trees.
    # Both carried an expected-skip HYPOTHESIS in their headers, written as
    # predictions and deliberately NOT as pins, precisely so the first census could
    # refute them.
    #
    # AND BACK TO EMPTY ON 2026-08-29 - both obligations discharged, both after THREE
    # censuses, and the reading discipline paid for itself twice over:
    #   H39: `_1947` PARSEK-FAIL(results) 33/1/13 (one test defect - an unset
    #        `CreatedUT` parking the synthetic route dormant), `_2053`
    #        PARSEK-FAIL(expectation) - BATCH GREEN 34/0/13 but `recordings.count 9 <
    #        min 19` - and `_2119` PASS 34/0/13 with count 21.
    #   H40: `_1951` PARSEK-FAIL(results) 25/10/12 (a nine-cell destination-headroom
    #        test-defect family against a 720/720 tank, plus the same `CreatedUT`
    #        cell), `_2056` PARSEK-FAIL(expectation) - BATCH GREEN 35/0/12 but
    #        `recordings.count 9 < min 20` - and `_2122` PASS 35/0/12 with count 22.
    # THE SECOND CENSUS OF EACH IS THE ONE WORTH REMEMBERING: both batches went green
    # and both runs red ANYWAY, on the recordings floor, which is how
    # QUICKLOAD-OVER-COMMITTED-RESTORE-OVERLAP-DELETES-TREE-ON-SAVE was found
    # (player-reachable data loss, fixed at 5218b13a8). Not one of the 46 in-game
    # cells could see it; the count row was the only instrument pointed at the
    # committed corpus. Both specs now pin their tally WHOLE, pin their count EXACTLY
    # (21 / 22) rather than as a window, declare their `skipped=` in MEASURED_SKIPPED
    # below, and leave here in the same commit. The expected-skip hypotheses in both
    # headers were CONFIRMED and none refuted, with one unpredicted skip on H39
    # (`Escrow_CompetingRouteSeesReservation_Holds` - the shared source is too LARGE,
    # the mirror image of the risk that header worried about).
    #
    # AND BACK TO ONE ON 2026-08-29 for `H41-logistics-grapple-isolated`, which is
    # interim for a reason neither H38 nor H39/H40 had. Its demanding cell
    # (`GrappleCapture_ProgrammaticCoupleReleaseCycle_StampsAndCompletes`) carries
    # TWELVE guards, and the half that matters is not a fixture property at all: two
    # `SpawnAtPosition` calls that can return 0 and two settle waits on the spawned
    # pids. No save file and no attribute settles whether a programmatic claw spawn
    # succeeds in an unattended batch - only a run does.
    #
    # AND BACK TO EMPTY ON 2026-08-29. H41 flew `2026-08-28_2216`, PASS attempt 1, wall
    # 57 s, measuring `total=4 passed=3 failed=0 skipped=1`. **THE CAPTURE CELL
    # EXECUTED AND PASSED**: all twelve guards satisfied, both `SpawnAtPosition` calls
    # returned live vessels, both settle waits completed in 76 frames each. The one
    # skip is the predicted certain one
    # (`GrappleWindow_LiveRecordedClawCouple_StampedGrapple`, which needs a PERSISTED
    # Grapple window this host has no recordings to carry), declared as 1 in
    # MEASURED_SKIPPED below.
    #
    # TWO THINGS THIS MEMBER LEAVES BEHIND, both unusual enough to keep:
    #   * ITS TALLY-COLLISION PREDICTION WAS CONFIRMED BYTE FOR BYTE. The spec header
    #     predicted `total=4 passed=3 failed=0 skipped=1` and called it "byte-identical
    #     to the line the ordinary path would print if nothing self-skipped". It is.
    #     So the `TALLY_CANNOT_DISCRIMINATE_IDS` exemption below is now backed by an
    #     OBSERVED collision rather than a derived one, and the exact pin this commit
    #     writes still cannot separate the two paths - only the structural and cell
    #     tokens do. This is the one member of the family whose tally proves nothing
    #     on its own.
    #   * IT IS THE FIRST MEMBER TO EARN A ZERO-DECLARER D10 ROW OFF A PRODUCTION
    #     EMITTER. `claw-producer` is claimed on three REQUIRED tokens naming one
    #     causal chain - `OnPartCouple producer classified: kind=Grapple
    #     fromPart=PotatoRoid toPart=GrapplingDevice`, `Route proof dock window
    #     captured: ... kind=Grapple`, and the cell's own
    #     `GrappleCapture PASS: ... complete=True` - where H38's four D10 rows rest on
    #     a whole-tally token. A stronger gate, and the shape to copy.
    INTERIM_PIN_IDS: set = set()

    # id -> measured `skipped=` for members whose RUN-TIME InGameAssert.Skip guards
    # push the split above the attribute-derived floor. The attributes give a FLOOR
    # only (hlib.batch_tally_pin_mismatches states it: "Run-time InGameAssert.Skip
    # guards can only push skipped HIGHER, never lower"), and H21 sits exactly ON its
    # floor because neither SceneExitMerge cell carries a self-skip. 24 of the 37
    # Rewind declarations do.
    #
    # A member listed here must still pin its tally WHOLE - the value is checked for
    # equality, so any drift in the measured split reds locally exactly as an
    # attribute change does. What the entry buys is the ability to state a split the
    # attributes cannot derive; what it costs is that the number is MEASURED, so it
    # must be re-measured (not re-guessed) whenever the fixture or the guards move.
    MEASURED_SKIPPED = {
        # R7a: 6 attribute-forced (the SPACECENTER-scoped six scene-skip at FLIGHT)
        # + 16 run-time, the marker-dependent family plus the two-command-pod staging
        # trio plus InvokeRPStripAndActivate. Derivation in the spec's own comment.
        "R7a-rewind-session-absent": 22,
        # H38: 1 attribute-forced (InterBodyRoute_RealBuilder_ClassifiesReaimWindows_
        # AndModuloFires, the one SPACECENTER-scoped Logistics cell, which H34 owns)
        # + 7 run-time, and every one of the seven is a MISSING RECORDED SUBJECT
        # rather than a rig defect: the two RouteOriginProof producer cells (a
        # docked-origin and a PRELAUNCH-committed recording), the three RouteProof
        # dock-window cells (one debt - the planned H39 bdock-recorded lane), the
        # HarvestCapture catch-up cell (a drill rig landed on ore, which the FuelCell
        # deliberately is not), and the HarvestRoute cell (the injected synthetic tree
        # `tree-drill-harvest-m2`, which `injectedRecordings = "none"` withholds by
        # measured choice). MEASURED off run `2026-08-28_1833`, whose seven `SKIPPED:`
        # lines plus `Scene eligibility skip summary: skipped=1 currentScene=FLIGHT
        # byRequiredScene=SPACECENTER:1` are quoted in full in the spec's header
        # roster. Re-measure, never re-guess: all seven are fixture properties.
        "H38-logistics-isolated": 8,
        # H39 (`bdock-recorded`): 1 attribute-forced + 12 run-time. MEASURED off census
        # 3, `2026-08-28_2119`. The twelve decompose as 3 residual missing-recorded-
        # subject cells (the docked-origin producer, and the active-as-TARGET and
        # cross-tree dock-window pair - all three a HARVEST requirement: every committed
        # ROUTE_CONNECTION_WINDOWS node in the suite is initiator-branch because the two
        # docking craft are Kerbal X descendants sharing one BAKED persistentId), 6 host
        # capability (no BaseConverter x2, one ModuleInventoryPart x2, empty container
        # x2), 1 injected-corpus, 1 drill-rig-on-ore, and 1 the census did NOT predict -
        # `Escrow_CompetingRouteSeesReservation_Holds`, which needs a source too SMALL to
        # cover two routes and this host's holds 645.42 LF. Full verbatim roster in the
        # spec header.
        "H39-logistics-isolated-bdock": 13,
        # H40 (`depot-route-recorded`): 1 attribute-forced + 11 run-time - H39's roster
        # MINUS the escrow cell, which PASSES here because this host's shared source is
        # small enough to demonstrate the competing-route net. MEASURED off census 3,
        # `2026-08-28_2122`. That the two recorded hosts differ by exactly one skip, and
        # that the one is a corpus-size property rather than a craft property, is itself
        # the measurement: the remaining eleven are the same on both, so they are
        # properties of the RECORDED-HOST SHAPE rather than of either fixture.
        "H40-logistics-isolated-depot-route": 12,
        # H41 (`logi-cargo-pad`): 0 attribute-forced + 1 run-time, MEASURED off
        # `2026-08-28_2216`. The one is `GrappleWindow_LiveRecordedClawCouple_
        # StampedGrapple`, and its skip string names THIS LANE'S OWN capture cell as the
        # automated gate that replaces it ("The automated gate (GrappleCaptureInGameTest,
        # isolated tier) stamps and asserts a live grapple window in-session ... this
        # check stays as the stock-contact-capture evidence hook"). So it is a
        # PERSISTED-window evidence hook standing down while the in-session gate covers
        # the behaviour, not a hole. Closing it is a HARVEST requirement - a committed
        # fixture from a flight that actually grappled - and it is STRUCTURALLY
        # unreachable inside one isolated batch, because `GrappleCapture`'s own
        # `RestoreBatchFlightBaselineAfterExecution` restore wipes the window it just
        # stamped. The two cells cannot hand off to each other by construction.
        "H41-logistics-grapple-isolated": 1,
    }

    @classmethod
    def setUpClass(cls):
        cls.decls = load_ingame_test_declarations()
        cls.specs = {}
        cls.on_disk = set()
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            spec = load_spec(name)
            sid = spec.get("id") or ""
            cls.on_disk |= discover_isolated_spec_ids([(sid, spec)])
            if sid in cls.GROUP:
                cls.specs[sid] = spec

    def test_the_autorun_arm_is_identity_true_not_truthy(self):
        # spec_batch_isolated is called on UNVALIDATED specs (two setUpClass sweeps
        # walk every file on disk), so it cannot rely on validate_spec having
        # rejected a non-bool. A truthy read would make the STRING "false" isolate.
        for value, expected in (("false", False), ("true", False), (1, False),
                                (True, True), (False, False)):
            with self.subTest(value=repr(value)):
                spec = {"driver": {"autorun": {"tests": "X", "isolated": value}}}
                self.assertEqual(expected, hlib.spec_batch_isolated(spec))

    def test_the_discovery_rule_reads_the_spec_not_an_id_list(self):
        # Synthetic input, so this holds no matter how many isolated specs exist on
        # disk. An id-lookup implementation returns an empty set here.
        iso = copy.deepcopy(load_spec("H21-scene-exit-merge-isolated.toml"))
        ordinary = copy.deepcopy(load_spec("H7-trajectory-math.toml"))
        got = discover_isolated_spec_ids(
            [("ZZ-stranger", iso), ("YY-ordinary", ordinary)])
        self.assertEqual({"ZZ-stranger"}, got,
                         "discovery must read each spec's own batch mode; an id "
                         "lookup would return an empty set for these two")

    def test_membership_is_discovered_from_the_spec_not_from_the_table(self):
        # The class's headline claim, and it was asserted by nothing: replacing the
        # discovery key with `sid in GROUP` kept the suite green and turned
        # test_the_group_is_exactly_the_isolated_specs_on_disk into a tautology. It
        # worked only because there is exactly one isolated spec today.
        #
        # Proven by CONSTRUCTION: hand the resolver a spec that is isolated but is
        # NOT in GROUP and require it to be recognised anyway.
        stranger = copy.deepcopy(load_spec("H21-scene-exit-merge-isolated.toml"))
        stranger["id"] = "ZZ-not-in-the-group"
        self.assertNotIn(stranger["id"], self.GROUP)
        self.assertTrue(
            hlib.spec_batch_isolated(stranger),
            "membership must come from the spec's own batch mode; if this reads "
            "False the discovery key has been swapped for an id lookup and "
            "test_the_group_is_exactly_the_isolated_specs_on_disk is a tautology")
        # And the converse: an ordinary spec must not be claimed.
        ordinary = copy.deepcopy(load_spec("H7-trajectory-math.toml"))
        self.assertFalse(hlib.spec_batch_isolated(ordinary))

    def test_the_h_series_id_pattern_covers_three_digit_ids(self):
        # IngameBatchWiringGroupTests.GROUP_ID_RE was widened from `^H(?:[7-9]|1[0-9]|20)-`
        # so a new H-series spec cannot be invisible to both wiring groups. The
        # widening had no coverage, and as first written it stopped dead at H99 -
        # the same hole one order of magnitude out. Pinned here because this class
        # is the one that exists because of that hole.
        rx = IngameBatchWiringGroupTests.GROUP_ID_RE
        for sid in ("H7-x", "H9-x", "H10-x", "H20-x", "H21-x", "H99-x", "H100-x"):
            self.assertIsNotNone(rx.match(sid), "%s must be recognised" % sid)
        for sid in ("H5-invariants-corpus", "H6-route-rewind-timeline", "B1-pad-hop"):
            self.assertIsNone(rx.match(sid), "%s must NOT be recognised" % sid)

    def test_the_group_table_is_not_empty(self):
        # ANTI-VACUITY FLOOR (see IngameBatchWiringGroupTests for the full argument):
        # every cell below iterates self.specs, so an emptied GROUP would pass them
        # all while asserting nothing.
        self.assertTrue(self.GROUP, "the isolated group must have at least one member")
        self.assertEqual(len(self.GROUP), len(self.specs),
                         "GROUP names %d spec(s) but only %d were loaded from %s"
                         % (len(self.GROUP), len(self.specs), SCENARIOS_DIR))

    def test_the_group_is_exactly_the_isolated_specs_on_disk(self):
        self.assertEqual(sorted(self.on_disk), sorted(self.GROUP),
                         "the isolated specs on disk differ from the table in this "
                         "test. A spec here but not on disk was removed, renamed, or "
                         "lost its isolated arg; a spec on disk but not here is new "
                         "and must be added to GROUP, to "
                         "docs/dev/autotest-ingame-category-inventory.md, and to "
                         "docs/dev/autotest-status.md, in the same commit")

    def test_each_drives_exactly_one_named_category_isolated(self):
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                steps = (spec.get("driver", {}) or {}).get("steps", []) or []
                run_tests = [s for s in steps if (s or {}).get("cmd") == "RunTests"]
                self.assertEqual(1, len(run_tests),
                                 "%s must own exactly one RunTests batch "
                                 "(hlib.SINGLE_BATCH_SELECTOR_RULE)" % sid)
                args = run_tests[0].get("args", {}) or {}
                self.assertEqual(self.GROUP[sid][0], args.get("category"))
                self.assertFalse(hlib.is_multi_category_selector(args.get("category")))
                # The STRING "true", not the TOML bool: step args are wire-encoded
                # with str(value), so a bool would travel as `isolated=True` and the
                # seam's case-sensitive parse would REJECT the step.
                self.assertEqual("true", args.get("isolated"))
                self.assertIsInstance(args.get("isolated"), str)

    def test_the_ordinary_path_could_not_run_this_category(self):
        # THE THESIS CELL. An isolated spec earns its arg only if the arg actually
        # changes what the batch executes -- otherwise it is decoration and the spec
        # proves nothing about R5. Derived from the attributes both ways and
        # compared.
        #
        # GENERALISED-BY-R7A. The thesis used to be spelled `ordinary.executable ==
        # 0`, which is the STRONGEST form of "the arg does work" and is true of
        # SceneExitMerge, whose every declaration is AllowBatchExecution = false. It
        # is NOT the thesis itself. `Rewind` is 37 declarations of which 6 are
        # batch-disabled, so the ordinary path executes 26 and the isolated path 32:
        # the arg buys six real cells and is plainly doing work, but the old spelling
        # would have rejected it. The general property is that the isolated filter
        # admits STRICTLY MORE, and the strict form is still required of every member
        # not declared PARTLY_BATCH_DISABLED -- so H21 keeps the stronger check and a
        # member cannot quietly weaken into the general one.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, _ = self.GROUP[sid]
                scene = hlib.resolve_batch_tally_pin(
                    ((spec.get("expectations", {}) or {})
                     .get("logContracts", {}) or {}).get("required", []) or []).scene
                ordinary = hlib.derive_batch_tally(self.decls, category, scene)
                isolated = hlib.derive_batch_tally(self.decls, category, scene,
                                                   isolated=True)
                if sid not in self.PARTLY_BATCH_DISABLED_IDS:
                    self.assertEqual(
                        0, ordinary.executable,
                        "%s drives %s with isolated = \"true\", but %d of its tests "
                        "are batch-eligible on the ORDINARY path too. The arg is not "
                        "doing any work here; drop it, drive a category that needs "
                        "it, or -- if the arg genuinely buys additional cells -- "
                        "declare the id in PARTLY_BATCH_DISABLED_IDS with the "
                        "reason." % (sid, category, ordinary.executable))
                self.assertGreater(
                    isolated.executable, ordinary.executable,
                    "%s drives %s with isolated = \"true\" but the isolated filter "
                    "admits no more than the ordinary one at scene=%s (%d vs %d), so "
                    "the arg cannot change a single test's outcome."
                    % (sid, category, scene, isolated.executable,
                       ordinary.executable))
                self.assertGreater(
                    isolated.executable, 0,
                    "%s drives %s isolated but NO test is admitted even by the "
                    "isolated filter at scene=%s: every declaration is manual-only "
                    "(neither AllowBatchExecution nor "
                    "RestoreBatchFlightBaselineAfterExecution), so this batch would "
                    "run empty." % (sid, category, scene))

    def test_each_pinned_tally_agrees_with_the_isolated_derivation(self):
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, expected_total = self.GROUP[sid]
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                self.assertTrue(pin.statically_checkable, sid)
                self.assertEqual(pin.category, category)
                derived = hlib.derive_batch_tally(self.decls, category, pin.scene,
                                                  isolated=True)
                self.assertEqual(derived.total, expected_total,
                                 "%s: the source now declares %d %s test(s), not %d"
                                 % (sid, derived.total, category, expected_total))
                self.assertEqual(pin.total, derived.total)
                self.assertEqual(
                    [], hlib.batch_tally_pin_mismatches(pin, self.decls, isolated=True))
                # Whole-tally pin, never the `passed=[1-9][0-9]*` interim form: R5's
                # proof IS the passed/skipped split, so leaving either unpinned would
                # accept the very tally the change exists to move.
                #
                # EXCEPT for a DECLARED interim member, which has not flown yet and
                # whose split no attribute predicts. The exemption is narrow: the
                # `total=` literal above is still cross-checked against the source,
                # and the vacuous / non-isolated rejection cell below still runs
                # unchanged, which is what keeps an interim pin from accepting the
                # ordinary path's line. See INTERIM_PIN_IDS for the obligation an
                # entry carries.
                if sid in self.INTERIM_PIN_IDS:
                    continue
                self.assertIsNotNone(pin.passed, sid)
                self.assertIsNotNone(pin.skipped, sid)
                # GENERALISED-BY-R7A. This used to read `(derived.total, 0, 0)`,
                # which bakes in TWO H21 coincidences: that nothing scene-skips (so
                # the attribute floor is 0) and that no member carries a run-time
                # InGameAssert.Skip. The general contract is the attribute floor,
                # plus a DECLARED measured value for the run-time guards the
                # attributes cannot see. Equality is kept in both branches, so this
                # is not a loosening: an undeclared member is still held to the
                # floor exactly as before, and a declared one is held to its
                # measured number.
                expected_skipped = self.MEASURED_SKIPPED.get(
                    sid, derived.attribute_skipped)
                self.assertGreaterEqual(
                    expected_skipped, derived.attribute_skipped,
                    "%s declares MEASURED_SKIPPED=%d below the attribute floor of "
                    "%d at scene=%s; run-time guards can only push skipped HIGHER"
                    % (sid, expected_skipped, derived.attribute_skipped, pin.scene))
                self.assertEqual(
                    (pin.passed, pin.failed, pin.skipped),
                    (derived.total - expected_skipped, 0, expected_skipped),
                    "%s: pinned tally disagrees with the isolated derivation at "
                    "scene=%s (attribute floor %d, expected skipped %d)"
                    % (sid, pin.scene, derived.attribute_skipped, expected_skipped))

    def test_the_interim_pin_members_are_declared_and_deliberately_loose(self):
        # Guards the OTHER direction, exactly as IngameBatchWiringGroupTests does for
        # the ordinary family: the interim form leaves the split unpinned by design,
        # so an ACCIDENTAL interim pin is a real weakening and a STALE one (an entry
        # left behind after the spec's first flight measured the split) silently keeps
        # a member exempt forever. Both are caught by requiring the declared set and
        # the observed looseness to agree exactly.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                loose = pin.passed is None or pin.skipped is None
                self.assertEqual(
                    sid in self.INTERIM_PIN_IDS, loose,
                    "%s: interim-vs-whole pin state disagrees with INTERIM_PIN_IDS. "
                    "A spec that has now FLOWN must pin its measured split whole and "
                    "leave the set; a spec that has not must be declared in it" % sid)
                self.assertIsNotNone(
                    pin.total,
                    "%s must pin total= even when the split is unmeasured - it is the "
                    "one token the [InGameTest] attributes derive exactly" % sid)
        self.assertLessEqual(
            self.INTERIM_PIN_IDS, set(self.GROUP),
            "INTERIM_PIN_IDS names ids that are not GROUP members: %s"
            % sorted(self.INTERIM_PIN_IDS - set(self.GROUP)))

    def test_an_interim_pin_still_rejects_the_ordinary_paths_executable_ceiling(self):
        # THE CELL THAT MAKES THE INTERIM EXEMPTION SAFE, and it is not implied by the
        # rejection cell below - that one synthesizes ONE ordinary line, from the
        # ATTRIBUTE split. The real risk of a loose `passed=` is broader: an isolated
        # spec that lost its arg prints SOME line with passed <= (ordinary executable),
        # because the ordinary filter cannot admit more than that however the run-time
        # guards fall. So sweep the WHOLE range and require the pin to reject all of
        # it. The usual `passed=[1-9][0-9]*` interim spelling FAILS this for any member
        # whose ordinary path executes 2 or more, which is why H38 pins passed >= 9.
        prefix = "[LOG 00:00:00.000] [Parsek][INFO][TestRunner] "
        # WHAT DEFENDS THE SUBTRACTED SPECS. A member in INTERIM_PIN_IDS *and*
        # TALLY_CANNOT_DISCRIMINATE_IDS is exempt from this sweep, so its tally pin
        # cannot tell a lost-isolated-arg run from a correct one - and that is exactly
        # why such a spec must carry STRUCTURAL tokens (the recorded-corpus count floor
        # above, and its required per-cell log literals) as the deliberate substitute:
        # those measure what the batch DID to the save and to the log, which a tally
        # cannot, and they are the reason the exemption is an accepted narrowing rather
        # than an unguarded hole.
        for sid in sorted(self.INTERIM_PIN_IDS - self.TALLY_CANNOT_DISCRIMINATE_IDS):
            with self.subTest(spec=sid):
                spec = self.specs[sid]
                category, _ = self.GROUP[sid]
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                req = lc.get("required", []) or []
                batch_pats = [p for p in req if "BATCH_COMPLETE" in p]
                self.assertEqual(1, len(batch_pats), sid)
                pat = batch_pats[0]
                scene = hlib.resolve_batch_tally_pin(batch_pats).scene
                ord_ = hlib.derive_batch_tally(self.decls, category, scene)
                self.assertGreater(ord_.executable, 0,
                                   "%s is declared PARTLY batch-disabled but the "
                                   "ordinary path executes nothing - this sweep would "
                                   "be inert" % sid)
                for passed in range(0, ord_.executable + 1):
                    line = (prefix + "BATCH_COMPLETE v1 total=%d passed=%d failed=0 "
                            "skipped=%d category=%s scene=%s"
                            % (ord_.total, passed, ord_.total - passed, category,
                               scene))
                    self.assertNotRegex(
                        line, pat,
                        "%s's interim pin ACCEPTS passed=%d, which is within the "
                        "ORDINARY path's executable ceiling of %d at scene=%s - so a "
                        "run that silently lost the isolated arg could read GREEN. An "
                        "interim pin on a partly-batch-disabled category must pin "
                        "passed >= %d, not the plain [1-9][0-9]* form"
                        % (sid, passed, ord_.executable, scene, ord_.executable + 1))

    def test_each_pin_rejects_both_the_vacuous_and_the_non_isolated_line(self):
        # END-TO-END round trip. Three lines are synthesized from the derivation and
        # matched against the spec's own pattern:
        #   (a) the ISOLATED line the runner would print -> must MATCH,
        #   (b) the all-skipped vacuous line              -> must be REJECTED,
        #   (c) the line TODAY'S ordinary path prints for the same category
        #       -> must be REJECTED.
        # (c) is the one that makes this an R5 proof rather than another tally
        # check: it is what the batch emits if the isolated arg is dropped, ignored,
        # or misspelled, and note it is a MEMBER of the vacuity family (passed=0,
        # failed=0, total==skipped), so the anti-vacuity gate already guarantees the
        # rejection. Asserting it here states that guarantee where R5 depends on it.
        prefix = "[LOG 00:00:00.000] [Parsek][INFO][TestRunner] "
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, _ = self.GROUP[sid]
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                batch_pats = [p for p in (lc.get("required", []) or [])
                              if "BATCH_COMPLETE" in p]
                self.assertEqual(1, len(batch_pats),
                                 "%s: expected exactly one BATCH_COMPLETE pattern" % sid)
                pat = batch_pats[0]
                scene = hlib.resolve_batch_tally_pin(batch_pats).scene
                iso = hlib.derive_batch_tally(self.decls, category, scene,
                                              isolated=True)
                ord_ = hlib.derive_batch_tally(self.decls, category, scene)

                def line(total, passed, failed, skipped):
                    return (prefix + "BATCH_COMPLETE v1 total=%d passed=%d failed=%d "
                            "skipped=%d category=%s scene=%s"
                            % (total, passed, failed, skipped, category, scene))

                # GENERALISED-BY-R7A, same reason as the tally cell above: the line
                # the runner ACTUALLY prints carries the measured split, which sits
                # at the attribute floor only when no member self-skips.
                iso_skipped = self.MEASURED_SKIPPED.get(sid, iso.attribute_skipped)
                real = line(iso.total, iso.total - iso_skipped, 0, iso_skipped)
                vacuous = line(iso.total, 0, 0, iso.total)
                # The ordinary-path contrast keeps its ATTRIBUTE-derived split. It is
                # a synthetic "what would a run that lost the arg print" line, and
                # the measured isolated split is not a prediction about that run.
                non_isolated = line(ord_.total,
                                    ord_.total - ord_.attribute_skipped, 0,
                                    ord_.attribute_skipped)

                self.assertRegex(real, pat,
                                 "%s: its own pin does not match the line the runner "
                                 "would print for the isolated derivation" % sid)
                self.assertNotRegex(vacuous, pat, "%s: its pin ACCEPTS the vacuous "
                                                  "line" % sid)
                if sid in self.TALLY_CANNOT_DISCRIMINATE_IDS:
                    # DECLARED non-discriminating - the exemption is earned below by
                    # test_the_tally_exemption_is_earned_by_a_one_cell_margin, and the
                    # proof transfers to the two isolated-path-only tokens that
                    # test_each_pins_an_isolated_path_only_proof_token and
                    # test_each_pins_the_seam_isolated_arg_echo demand of EVERY member.
                    # The vacuous rejection above still applies and is not exempted.
                    continue
                self.assertNotRegex(
                    non_isolated, pat,
                    "%s: its pin ACCEPTS the line the ORDINARY (non-isolated) path "
                    "would print, so a run that silently lost the isolated arg would "
                    "read GREEN. That contrast is the whole proof of R5." % sid)
                parsed = hlib.parse_batch_complete_line(real)
                self.assertIsNotNone(parsed, sid)
                self.assertEqual(parsed.category, category)

    # category -> the NAMED CAPABILITY its cells need of the host's ACTIVE vessel.
    #
    # GENERALISED-BY-H39/H40, and the same shape as the two GENERALISED-BY-R7A moves
    # above: a property that happened to be true of every member so far had been
    # written as if it were the group's invariant. Here it was `sit = PRELAUNCH` plus
    # at least one `ModuleEngines`, which is the STAGING requirement and nothing else.
    # It exists because H21's `SceneExitMerge` cells call
    # `StageManager.ActivateNextStage()` and then wait for the vessel to leave
    # PRELAUNCH and clear 80 m, so an engineless or already-flying host makes both
    # cells self-skip and print the all-skipped tally the isolated arg exists to rule
    # out.
    #
    # `Logistics` DOES NOT STAGE, and that is a MEASURED fact about the source rather
    # than a convenience. A grep for `ActivateNextStage`, `Situations.PRELAUNCH`,
    # `WaitForRecordingToLeavePrelaunch` and `ClassifyLaunchWaitTimeout` across all 18
    # `Logistics` test files plus the helper they share returns exactly ONE hit, and
    # it is a COMMENT about a RECORDED start situation
    # (`LogisticsRouteProducerRuntimeTests.cs:165`, quoting
    # `VesselSpawner.HumanizeSituation(Vessel.Situations.PRELAUNCH)` to explain what a
    # committed recording's `startSituation` field holds). Not one Logistics cell
    # stages, and not one reads the LIVE situation. `UnloadedFuelVesselFixture`'s own
    # docstring says a "fueled PRELAUNCH pad rocket satisfies them after the unpack
    # wait" - a SUFFICIENT condition offered as an example, never a necessary one, and
    # reading it as necessary is what encoded PRELAUNCH here in the first place.
    #
    # WHAT LOGISTICS ACTUALLY NEEDS is the capability that helper's own failure path
    # names. `EnsureUnloadedLiquidFuelVessel` snapshots the ACTIVE vessel with
    # `VesselSpawner.TryBackupSnapshot`, rewrites the snapshot's FIRST LiquidFuel tank
    # to the required stored / free floors and re-spawns it as the unloaded depot - so
    # with no LiquidFuel RESOURCE node on the active vessel it returns
    # `reason = "no-liquidfuel-resource"` and every unloaded-depot cell skips. That is
    # the Logistics analogue of H21's engineless host: the same failure mode, the same
    # tally that is indistinguishable from a broken isolated arg, a different missing
    # capability.
    #
    # FAIL-CLOSED BY DESIGN - there is NO default. A new isolated member whose
    # category is absent from this table reds by name rather than silently inheriting
    # a requirement that may not fit it, which is precisely the mistake this
    # generalisation is correcting.
    FIXTURE_REQUIREMENTS = {
        # Both cells stage and then wait to clear 80 m.
        "SceneExitMerge": "staging",
        # CaptureRPOnStaging / SavePathRootThenMove / WarpZeroedDuringSave stage the
        # active vessel to force a separation, so R7a's host genuinely owes PRELAUNCH
        # + engines and keeps the original requirement, unchanged and unweakened.
        "Rewind": "staging",
        # See the derivation above: never stages, needs a snapshottable active vessel
        # carrying shapeable LiquidFuel.
        "Logistics": "logistics",
        # `LogisticsGrapple` is a THIRD class, and giving it `Logistics`' row on the
        # strength of a similar category name would have been the same mistake this
        # table was created to fix. Its one demanding cell
        # (`GrappleCapture_ProgrammaticCoupleReleaseCycle_StampsAndCompletes`) spawns a
        # claw and a PotatoRoid BESIDE the active vessel and couples them
        # programmatically, so what it needs is a real vessel to spawn beside: a live
        # active vessel with parts, not an EVA kerbal. It does NOT stage, and it does
        # NOT touch LiquidFuel - `UnloadedFuelVesselFixture` is not in its call graph at
        # all, so holding it to the `logistics` requirement would assert a capability
        # none of its cells reads.
        "LogisticsGrapple": "loaded-vessel",
    }

    @staticmethod
    def _active_vessel_block(sfs_path):
        """``(block, index)`` for the save's ACTIVE vessel, or ``(None, reason)``."""
        with open(sfs_path, encoding="utf-8", errors="replace") as fh:
            body = fh.read()
        active = re.search(r"activeVessel = (\d+)", body)
        if active is None:
            return None, "no activeVessel declared"
        idx = int(active.group(1))
        blocks = re.split(r"^\s*VESSEL\s*$", body, flags=re.M)[1:]
        if len(blocks) <= idx:
            return None, ("activeVessel=%d but only %d VESSEL nodes"
                          % (idx, len(blocks)))
        # Scoped to the ACTIVE vessel, not the whole file: a fixture with an
        # engine-bearing ORBITER and an engineless PRELAUNCH active vessel passes a
        # file-wide substring check while producing exactly the all-skipped tally this
        # predicate exists to prevent. b2-lko-craft really does carry two.
        return blocks[idx], idx

    @classmethod
    def _fixture_flight_problems(cls, sfs_path, requirement="staging"):
        """Why ``sfs_path``'s ACTIVE vessel cannot fly ``requirement``'s cells.

        Empty list = fine. ONE implementation per requirement, used by BOTH the real
        cell and the positive controls below. Duplicating the checks instead left the
        engine floor weakenable to ">= 0" with the control still green, because the
        control was only asserting properties OF the control fixture rather than
        running the predicate ON it - and that argument is exactly why each control
        below runs a NAMED requirement over a fixture known to be wrong FOR THAT
        requirement, rather than over a fixture that is merely wrong for something.
        """
        vessel, idx = cls._active_vessel_block(sfs_path)
        if vessel is None:
            return [idx]
        problems = []
        if requirement == "staging":
            if "sit = PRELAUNCH" not in vessel:
                problems.append("active vessel (index %d) is not PRELAUNCH" % idx)
            if vessel.count("name = ModuleEngines") < 1:
                problems.append(
                    "active vessel (index %d) carries NO ModuleEngines" % idx)
            return problems
        if requirement == "logistics":
            # (a) A real craft to snapshot. `VesselSpawner.TryBackupSnapshot` has
            #     nothing to copy off a part-less VESSEL node - an asteroid /
            #     SpaceObject is the realistic way a fixture lands here, and
            #     `logi-cargo-pad` really does carry one as its OTHER vessel - and
            #     every unloaded-depot cell then skips.
            if not re.search(r"^\t\t\tPART\s*$", vessel, flags=re.M):
                problems.append(
                    "active vessel (index %d) declares no PART nodes - there is "
                    "nothing for TryBackupSnapshot to copy" % idx)
            # (b) The capability the helper's own skip reason names. A RESOURCE node
            #     with maxAmount = 0 is not a tank the snapshot rewrite can shape, so
            #     the floor is POSITIVE CAPACITY rather than mere presence of the
            #     string - a presence check would pass on a drained placeholder.
            tanks = re.findall(
                r"name = LiquidFuel\s*\n\s*amount = [0-9.eE+-]+"
                r"\s*\n\s*maxAmount = ([0-9.eE+-]+)", vessel)
            if not any(float(m) > 0.0 for m in tanks):
                problems.append(
                    "active vessel (index %d) carries NO LiquidFuel RESOURCE node "
                    "with positive maxAmount - UnloadedFuelVesselFixture returns "
                    "reason=no-liquidfuel-resource and every unloaded-depot cell "
                    "skips" % idx)
            return problems
        if requirement == "loaded-vessel":
            # A real, controllable vessel to spawn beside and couple with. Two
            # conditions, each traceable to a guard in
            # `GrappleCaptureInGameTest.cs`:
            #   (a) PART nodes - an EVA kerbal or a part-less SpaceObject is not
            #       something the capture cell can place a claw against, and the cell
            #       skips on "Active vessel is an EVA kerbal; a couple involving it
            #       would be EVA-suppressed".
            #   (b) NOT an asteroid / SpaceObject as the ACTIVE vessel, for the same
            #       reason plus the couple itself.
            # Deliberately NOT checked here: "loaded+unpacked" and the 5-degree pole
            # guard. The first is a runtime state no .sfs settles, and the second is a
            # live latitude the spec's own header argues about (the KSC sits near 0
            # degrees). A static predicate that pretended to settle either would be
            # claiming more than it can see - the floor is what a save file can prove.
            if not re.search(r"^\t\t\tPART\s*$", vessel, flags=re.M):
                problems.append(
                    "active vessel (index %d) declares no PART nodes - there is nothing "
                    "for the capture cell to spawn a claw beside" % idx)
            vtype = re.search(r"^\t\t\ttype = (\S+)", vessel, flags=re.M)
            if vtype is not None and vtype.group(1) in ("SpaceObject", "EVA", "Debris"):
                problems.append(
                    "active vessel (index %d) is type=%s - a couple involving it is "
                    "EVA-suppressed or meaningless" % (idx, vtype.group(1)))
            return problems
        return ["unknown fixture requirement %r" % requirement]

    def test_each_pins_an_isolated_path_only_proof_token(self):
        # The tally alone cannot distinguish "the isolated route ran" from "the
        # category happened to be batch-allowed". `Using batch baseline slot ... for
        # N restore-after-run test(s)` is emitted ONLY by
        # PrepareBatchFlightRestoreExecution, which RunCategory never calls, so it is
        # independent proof - but only if N is pinned as a LITERAL. A `[0-9]+` there
        # is satisfied by a batch that admitted a different population.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, total = self.GROUP[sid]
                req = ((spec.get("expectations", {}) or {})
                       .get("logContracts", {}) or {}).get("required", []) or []
                slot = [r for r in req if "batch baseline slot" in r]
                self.assertEqual(1, len(slot),
                                 "%s must pin the baseline-slot line - it is the only "
                                 "token that proves the ISOLATED entry point ran" % sid)
                # GENERALISED-BY-R7A. This used to pin `total`, which is right only
                # when EVERY admitted test carries the restore flag - true of
                # SceneExitMerge (2 of 2) and false of Rewind (6 of 37). The number
                # the runner actually prints is
                # `ordered.Count(t => t.RestoreBatchFlightBaselineAfterExecution)`
                # over the ADMITTED batch (InGameTestRunner.PrepareBatchFlightRestore-
                # Execution), so derive it. Pinning `total` for R7a would have
                # demanded the literal 37 against a line that says 6, i.e. red on a
                # correct spec.
                scene = hlib.resolve_batch_tally_pin(req).scene
                admitted = hlib.derive_batch_tally(self.decls, category, scene,
                                                   isolated=True)
                restore_count = sum(
                    1 for d in self.decls
                    if d.category == category and d.restore_baseline
                    and d.origin not in admitted.scene_skipped_members)
                self.assertGreater(
                    restore_count, 0,
                    "%s: no admitted %s declaration carries "
                    "RestoreBatchFlightBaselineAfterExecution, so "
                    "PrepareBatchFlightRestoreExecution returns before logging the "
                    "slot line at all and this token can never match" % (sid, category))
                self.assertIn("for %d restore-after-run" % restore_count, slot[0],
                              "%s must pin the restore count as a LITERAL (%d - the "
                              "restore-flagged declarations admitted at scene=%s), "
                              "not a class and not the category total: a class "
                              "accepts a batch that admitted a different population"
                              % (sid, restore_count, scene))

    def test_each_pins_the_seam_isolated_arg_echo(self):
        # The canary for the provisioning trap: the harness flies a DIFFERENT KSP
        # instance from `dotnet build`'s, so this line's ABSENCE means a stale
        # Parsek.dll rather than a Parsek regression.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, _ = self.GROUP[sid]
                req = ((spec.get("expectations", {}) or {})
                       .get("logContracts", {}) or {}).get("required", []) or []
                self.assertIn("runtests start category=%s isolated=true" % category, req,
                              "%s must pin the seam's own isolated echo" % sid)

    def test_the_tally_exemption_is_earned_by_a_one_cell_margin(self):
        # THE EXEMPTION MUST BE DERIVED, NOT DECLARED. A member may leave the
        # ordinary-line rejection to its structural tokens ONLY when the tally provably
        # cannot do the job - i.e. when the isolated filter admits exactly ONE more cell
        # than the ordinary one, so a single run-time skip anywhere collapses the two
        # lines onto each other. At a wider margin a floor exists and must be used.
        self.assertLessEqual(
            self.TALLY_CANNOT_DISCRIMINATE_IDS, set(self.GROUP),
            "TALLY_CANNOT_DISCRIMINATE_IDS names ids that are not GROUP members: %s"
            % sorted(self.TALLY_CANNOT_DISCRIMINATE_IDS - set(self.GROUP)))
        for sid in sorted(self.TALLY_CANNOT_DISCRIMINATE_IDS):
            with self.subTest(spec=sid):
                spec = self.specs[sid]
                category, _ = self.GROUP[sid]
                req = ((spec.get("expectations", {}) or {})
                       .get("logContracts", {}) or {}).get("required", []) or []
                scene = hlib.resolve_batch_tally_pin(req).scene
                iso = hlib.derive_batch_tally(self.decls, category, scene, isolated=True)
                ordinary = hlib.derive_batch_tally(self.decls, category, scene)
                self.assertEqual(
                    1, iso.executable - ordinary.executable,
                    "%s claims its tally cannot discriminate, but the isolated filter "
                    "admits %d where the ordinary one admits %d at scene=%s. At a "
                    "margin of %d a `passed=` floor above the ordinary ceiling DOES "
                    "separate the two paths, so the exemption is not earned - pin the "
                    "floor instead" % (sid, iso.executable, ordinary.executable, scene,
                                       iso.executable - ordinary.executable))
                # And the tokens the duty transferred TO must actually be there. The two
                # dedicated cells assert this for every member; asserting it again here
                # is what makes the exemption self-contained rather than dependent on a
                # sibling cell nobody would think to check when editing this one.
                self.assertIn("runtests start category=%s isolated=true" % category, req,
                              "%s: the seam echo is now the ONLY thing separating this "
                              "spec from an ordinary run" % sid)
                self.assertTrue(
                    any("batch baseline slot" in r for r in req),
                    "%s: the baseline-slot line is now load-bearing and must be pinned"
                    % sid)

    def test_each_pins_the_recordings_count_its_fixture_implies(self):
        # For an isolated spec the recordings pin doubles as the CAMPAIGN-ISOLATION
        # assertion: the tests create real trees mid-run and the batch teardown
        # reverts persistent.sfs from the pre-batch .bak, so a produced count that
        # does not match the STAGED one means the restore contract did not hold -
        # the property R5 is betting on.
        #
        # GENERALISED-BY-H39/H40, and this one had the SAME shape of error as the
        # fixture predicate. It read `injectedRecordings == "none"` -> the produced
        # save must carry ZERO recordings, which conflates "injects nothing" with
        # "starts with nothing". True of H21 (b2-lko-craft), R7a (career-pad-craft)
        # and H38 (logi-cargo-pad), all of which stage an empty corpus. NOT true of a
        # RECORDED-fixture host: `bdock-recorded` stages 19 `.prec` sidecars and
        # `depot-route-recorded` 22, carried by the TEMPLATE rather than injected -
        # which is exactly why `injectedRecordings` must stay "none" for them (an
        # injected corpus on top would make the count un-attributable, the same rule
        # `IngameBatchWiringGroupTests` states for H35). Pinning {0, 0} there would
        # assert the batch DESTROYED the committed corpus.
        #
        # So the discriminator is the TEMPLATE's staged sidecar count, read off disk,
        # not the injection field - and both branches keep a real assertion.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                fixture = spec.get("fixture", {}) or {}
                count = ((spec.get("expectations", {}) or {})
                         .get("recordings", {}) or {}).get("count", {}) or {}
                template = fixture.get("saveTemplate", "")
                self.assertTrue(template.startswith("fixtures/saves/"), sid)
                rec_dir = os.path.join(HARNESS_ROOT,
                                       template.replace("/", os.sep),
                                       "Parsek", "Recordings")
                staged = ([f for f in os.listdir(rec_dir) if f.endswith(".prec")]
                          if os.path.isdir(rec_dir) else [])
                if not staged:
                    self.assertEqual("none", fixture.get("injectedRecordings"), sid)
                    self.assertEqual(
                        {"min": 0, "max": 0}, count,
                        "%s stages an EMPTY corpus and injects nothing, so the "
                        "produced save must carry no recordings; a window here would "
                        "accept a leaked tree or a failed baseline revert" % sid)
                    continue
                # RECORDED host. Injection must stay off, or the pinned count cannot
                # be attributed to the template.
                self.assertEqual(
                    "none", fixture.get("injectedRecordings"),
                    "%s carries its payload in the TEMPLATE; injecting on top would "
                    "make the pinned count un-attributable" % sid)
                self.assertGreater(
                    count.get("min", 0), 0,
                    "%s stages %d .prec sidecar(s) and its whole premise is that the "
                    "batch walks recorded state - a floor of 0 would accept a run "
                    "that destroyed the corpus" % (sid, len(staged)))
                # THE FLOOR IS THE STAGED SET, not merely non-zero - and it is asserted
                # HERE, ahead of the interim allowance below, exactly as the in-game
                # group's cell does it (`IngameBatchWiringGroupTests`' assertGreaterEqual
                # of count.min against len(precs)). MEASURED REASON: H40's census-2 red
                # was a produced save that collapsed 22 staged recordings to 9. A floor
                # of 1 accepts that silently; a floor of >= 22 is what makes it the red
                # it was. An interim pin may still be wide at the TOP - the load-time
                # optimizer's split count is a genuine measurement - but its floor may
                # never sit below the corpus the template put on disk.
                self.assertGreaterEqual(
                    count.get("min", 0), len(staged),
                    "%s pins count=%s but its template stages %d .prec files; the floor "
                    "must be at least the staged set or a run that DESTROYED part of the "
                    "corpus still passes (H40 census-2 measured exactly that: 22 -> 9)"
                    % (sid, count, len(staged)))
                self.assertGreaterEqual(
                    count.get("max", 0), len(staged),
                    "%s pins count=%s but its template stages %d .prec files, so the "
                    "window cannot even contain the staged set and the spec reds on "
                    "a correct run" % (sid, count, len(staged)))
                if sid in self.INTERIM_PIN_IDS:
                    # A reading run may declare a WINDOW at the TOP: the load-time
                    # optimizer's behaviour on a corpus nobody has batched over is a
                    # measurement, and V18T pins {20, 30} over 22 staged on exactly that
                    # argument. The staged-set FLOOR above still applies to it.
                    continue
                self.assertEqual(
                    count.get("min"), count.get("max"),
                    "%s has FLOWN, so it must pin its count EXACTLY (min == max): a "
                    "range cannot tell a load-time optimizer split from a leaked "
                    "promotion stub. Widen only by leaving INTERIM_PIN_IDS behind, "
                    "never by re-opening a measured window" % sid)

    # fixture -> the token each requirement's rejection message must carry when run
    # over a host that is KNOWN-WRONG for it. One row per (requirement, failure
    # mode), so a weakened check makes the real cell vacuous and this one reds.
    #
    # WHY EVERY ROW NAMES A TOKEN rather than just asserting non-empty: the original
    # control asserted only that gloops-airshow produced SOME complaint, which a
    # predicate that had lost its engine floor entirely would still satisfy via the
    # PRELAUNCH check on a different host. Requiring the specific token pins WHICH
    # check fired.
    FIXTURE_PREDICATE_CONTROLS = (
        # STAGING, missing engines. gloops-airshow is the 14 ordinary H-specs' host
        # and its active vessel is a 1-part engineless mk1-capsule; both
        # SceneExitMerge cells self-skip there and the batch prints the all-skipped
        # tally the isolated arg exists to rule out.
        ("staging", "gloops-airshow", "ModuleEngines"),
        # STAGING, already flying. THE ROW THAT PROVES THE GENERALISATION DID NOT
        # QUIETLY DELETE THE PRELAUNCH CHECK - `bdock-recorded`'s active vessel is an
        # ORBITING Kerbal X WITH an engine, so it clears the engine floor and must
        # still be rejected for staging. Without this row, dropping the PRELAUNCH
        # branch to "make the new lanes pass" would go unnoticed, which is the single
        # most likely way to get this change wrong.
        ("staging", "bdock-recorded", "PRELAUNCH"),
        # LOGISTICS, no fuel. gloops-airshow again, and it is wrong for BOTH
        # requirements for DIFFERENT stated reasons: its 1-part mk1-capsule carries no
        # LiquidFuel RESOURCE node at all, so `UnloadedFuelVesselFixture` returns
        # `reason = no-liquidfuel-resource` and every unloaded-depot cell skips - the
        # exact Logistics analogue of the engineless case.
        ("logistics", "gloops-airshow", "LiquidFuel"),
        # LOADED-VESSEL, wrong active-vessel TYPE. `mun-orbit-recorded`'s active vessel
        # is a real craft, so the PART floor alone would accept every committed fixture
        # and the class would be a tautology; this row runs the requirement over a save
        # whose active vessel is an ASTEROID, which is the shape the capture cell cannot
        # couple. Built as a temporary in-memory fixture rather than committed, since no
        # committed save makes an asteroid active - see the cell below.
        )

    # (requirement, a synthetic active-vessel VESSEL block that must be REJECTED, token)
    SYNTHETIC_PREDICATE_CONTROLS = (
        ("loaded-vessel",
         "activeVessel = 0\nVESSEL\n\t\t\tname = Ast. ABC-123\n\t\t\ttype = SpaceObject\n"
         "\t\t\tPART\n\t\t\t{\n\t\t\t}\n",
         "type=SpaceObject"),
        ("loaded-vessel",
         "activeVessel = 0\nVESSEL\n\t\t\tname = Bob Kerman\n\t\t\ttype = EVA\n",
         "no PART nodes"),
    )

    def test_each_requirement_rejects_a_host_that_is_wrong_for_it(self):
        # POSITIVE CONTROLS: run each NAMED requirement ON a host known to be wrong
        # FOR THAT requirement and require it to complain, with the right token.
        # Without these, weakening a floor makes the real cell vacuous and nothing
        # notices.
        for requirement, fixture, token in self.FIXTURE_PREDICATE_CONTROLS:
            with self.subTest(requirement=requirement, fixture=fixture):
                problems = self._fixture_flight_problems(
                    os.path.join(HARNESS_ROOT, "fixtures", "saves", fixture,
                                 "persistent.sfs"),
                    requirement)
                self.assertNotEqual(
                    [], problems,
                    "the %r requirement must REJECT %s. If this passes, that "
                    "requirement has been weakened into a tautology and the real "
                    "cell can no longer catch the fixture trap"
                    % (requirement, fixture))
                self.assertTrue(
                    any(token in prob for prob in problems),
                    "the %r requirement rejected %s, but not for the %s reason this "
                    "control exists to pin: %s"
                    % (requirement, fixture, token, problems))

    def test_each_requirement_rejects_a_synthetic_host_that_is_wrong_for_it(self):
        # The committed-fixture controls above cannot cover every failure mode, because
        # no committed save makes an ASTEROID or an EVA kerbal the active vessel - and a
        # requirement whose only control is a fixture that fails it for a DIFFERENT
        # reason is half-tested. These run the predicate over a synthesized save body
        # instead, so each branch of `loaded-vessel` has a control of its own.
        import tempfile
        for requirement, body, token in self.SYNTHETIC_PREDICATE_CONTROLS:
            with self.subTest(requirement=requirement, token=token):
                fd, path = tempfile.mkstemp(suffix=".sfs")
                try:
                    with os.fdopen(fd, "w", encoding="utf-8") as fh:
                        fh.write(body)
                    problems = self._fixture_flight_problems(path, requirement)
                finally:
                    os.unlink(path)
                self.assertTrue(
                    any(token in p for p in problems),
                    "the %r requirement must reject this synthetic host for the %s "
                    "reason; got %s" % (requirement, token, problems))

    def test_the_requirement_classes_are_not_interchangeable(self):
        # The OTHER direction, and it is what keeps FIXTURE_REQUIREMENTS meaningful:
        # if both requirements resolved to the same predicate the table would be
        # decoration, every control above would still pass, and routing `Logistics`
        # to `logistics` would buy nothing. Proven by CONSTRUCTION on a host that is
        # ACCEPTABLE under one and REJECTED under the other.
        sfs = os.path.join(HARNESS_ROOT, "fixtures", "saves", "bdock-recorded",
                           "persistent.sfs")
        self.assertNotEqual(
            [], self._fixture_flight_problems(sfs, "staging"),
            "bdock-recorded's active vessel is ORBITING and must fail `staging`")
        self.assertEqual(
            [], self._fixture_flight_problems(sfs, "logistics"),
            "bdock-recorded's active vessel is a 28-part Kerbal X carrying a "
            "LiquidFuel tank, so it must PASS `logistics` - if it does not, the "
            "logistics requirement has inherited the staging one")
        # And the third class must not have collapsed into either of the other two:
        # gloops-airshow's 1-part engineless, fuel-less mk1-capsule is REJECTED by both
        # `staging` and `logistics` and must be ACCEPTED by `loaded-vessel`, which asks
        # only for a real vessel to spawn beside.
        gl = os.path.join(HARNESS_ROOT, "fixtures", "saves", "gloops-airshow",
                          "persistent.sfs")
        self.assertNotEqual([], self._fixture_flight_problems(gl, "staging"))
        self.assertNotEqual([], self._fixture_flight_problems(gl, "logistics"))
        self.assertEqual(
            [], self._fixture_flight_problems(gl, "loaded-vessel"),
            "gloops-airshow's active vessel is a real 1-part craft, so it must PASS "
            "`loaded-vessel` - if it does not, that requirement has inherited one of "
            "the other two and the table is decoration")

    def test_an_unknown_requirement_fails_closed(self):
        # FAIL-CLOSED: a typo in FIXTURE_REQUIREMENTS, or a category routed to a
        # requirement nobody implemented, must red rather than return "no problems"
        # and silently pass every fixture.
        problems = self._fixture_flight_problems(
            os.path.join(HARNESS_ROOT, "fixtures", "saves", "logi-cargo-pad",
                         "persistent.sfs"),
            "no-such-requirement")
        self.assertNotEqual([], problems)
        self.assertTrue(any("unknown fixture requirement" in p for p in problems),
                        problems)

    def test_the_requirement_table_agrees_with_what_the_cells_actually_do(self):
        # FIXTURE_REQUIREMENTS is DECLARED, and a declaration about someone else's code
        # rots. This derives the same fact from SOURCE: a `staging` category's cells must
        # actually reach the stage manager, and a `logistics` / `loaded-vessel` one must
        # not - because "needs a PRELAUNCH host with engines" is a claim about staging and
        # nothing else.
        #
        # COMMENT-STRIPPED, per the house rule (`feedback-source-derived-guards-use-ast`):
        # the raw text of the Logistics category contains the word PRELAUNCH and a
        # staging-shaped sentence, both inside prose comments. A regex over raw source
        # would read those as code and this gate would pass for the wrong reason - which
        # is the exact failure mode that rule was written for. hlib._mask_csharp_noise is
        # the same masker parse_ingame_test_declarations uses.
        staging_call = "ActivateNextStage"
        by_category = {}
        source_by_rel = {}
        for rel, text in walk_parsek_sources():
            source_by_rel[rel] = hlib._mask_csharp_noise(text)
            for decl in hlib.parse_ingame_test_declarations(text, rel):
                by_category.setdefault(decl.category, set()).add(rel)

        for category, requirement in sorted(self.FIXTURE_REQUIREMENTS.items()):
            with self.subTest(category=category):
                files = by_category.get(category)
                self.assertTrue(
                    files,
                    "FIXTURE_REQUIREMENTS names category %r but no [InGameTest] "
                    "declaration in Source/Parsek carries it - the row is stale or "
                    "misspelled" % category)
                stages = any(staging_call in source_by_rel[rel] for rel in sorted(files))
                if requirement == "staging":
                    self.assertTrue(
                        stages,
                        "%r is routed to the `staging` requirement - which asserts the "
                        "host is PRELAUNCH with engines - but none of its files (%s) "
                        "calls %s outside a comment. Either the row is wrong or the "
                        "cells stopped staging."
                        % (category, sorted(files), staging_call))
                else:
                    self.assertFalse(
                        stages,
                        "%r is routed to the %r requirement, which does NOT demand a "
                        "stageable host - but one of its files (%s) calls %s outside a "
                        "comment, so a real cell may need staging the fixture is not "
                        "held to." % (category, requirement, sorted(files), staging_call))

    def test_every_member_category_declares_a_fixture_requirement(self):
        # The table is fail-closed only if something checks it is TOTAL over the
        # group. A member whose category is missing would otherwise KeyError deep
        # inside the real cell with no explanation.
        missing = sorted({self.GROUP[sid][0] for sid in self.GROUP}
                         - set(self.FIXTURE_REQUIREMENTS))
        self.assertEqual(
            [], missing,
            "these isolated-group categories declare no entry in "
            "FIXTURE_REQUIREMENTS, so nobody has stated what their cells need of a "
            "host: %s. Read the category's bodies and add a row - do NOT default it "
            "to `staging`, which is a specific claim about StageManager" % missing)
        unknown = sorted(set(self.FIXTURE_REQUIREMENTS.values())
                         - {"staging", "logistics", "loaded-vessel"})
        self.assertEqual([], unknown,
                         "FIXTURE_REQUIREMENTS names requirement(s) with no "
                         "implementation in _fixture_flight_problems: %s" % unknown)

    def test_the_budget_clears_the_deferred_worst_case(self):
        # An isolated batch's plausible failure is a slow or wedged quickload, and a
        # KILLED run prints no tally and burns a retry - so this family in particular
        # must be able to surface a seam TIMEOUT as a retryable driver-INVALID.
        #
        # The arithmetic is easy to get wrong and was: LoadGame is ITSELF a deferred
        # verb, so run.py waits required_step_wait(max(budget, 600)) on it too, not
        # the declared 300. Computed here from hlib rather than restated.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                steps = (spec.get("driver", {}) or {}).get("steps", []) or []
                worst = 0.0
                for st in steps:
                    cmd = (st or {}).get("cmd")
                    if cmd not in hlib.DEFERRED_SEAM_VERBS:
                        continue
                    declared = float((st or {}).get("budget") or 0)
                    worst += hlib.required_step_wait(
                        max(declared, float(hlib.SEAM_FALLBACK_DEFERRAL_SECONDS)))
                budget = float((spec.get("runtime", {}) or {}).get("budgetSeconds", 0))
                self.assertGreaterEqual(
                    budget, worst,
                    "%s declares budgetSeconds=%.0f but its deferred steps can wait "
                    "%.0f s in total, so a seam TIMEOUT would be pre-empted by a "
                    "harness KILL - which prints no tally to re-derive from"
                    % (sid, budget, worst))

    def test_the_fixture_can_actually_fly_the_category(self):
        # THE FIXTURE TRAP, and it is not hypothetical. Both SceneExitMerge cells
        # stage the active vessel and wait for it to leave PRELAUNCH and clear 80 m;
        # on an engineless craft they self-skip and the batch prints
        # total=2 passed=0 skipped=2 - numerically identical to the non-isolated
        # failure this spec exists to rule out, and therefore the single most
        # expensive way to get R5 wrong.
        #
        # The requirement is now looked up PER CATEGORY (FIXTURE_REQUIREMENTS) rather
        # than assumed to be staging - see that table for why, and note the check is
        # not thereby weaker: the staging categories are held to exactly the same two
        # conditions as before, and `Logistics` is held to the capability ITS helper's
        # skip reason names instead of one no Logistics cell reads.
        #
        # WHAT IT STILL CANNOT PROVE, unchanged: a live property. It cannot show the
        # craft has the TWR to clear 80 m in 30 s (only a run does that, and H21 has),
        # and it cannot show a Logistics host's tank can be shaped to the live
        # stored / free floors or that its corpus contains the dock windows a
        # read-side cell wants. Those are the reading runs' job, which is why H39 and
        # H40 carry expected-skip hypotheses rather than pins.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                template = (spec.get("fixture", {}) or {}).get("saveTemplate", "")
                sfs = os.path.join(HARNESS_ROOT, template, "persistent.sfs")
                self.assertTrue(os.path.isfile(sfs),
                                "%s names fixture %r but %s does not exist"
                                % (sid, template, sfs))
                requirement = self.FIXTURE_REQUIREMENTS[self.GROUP[sid][0]]
                self.assertEqual(
                    [], self._fixture_flight_problems(sfs, requirement),
                    "%s's fixture %s cannot fly this category under the %r "
                    "requirement" % (sid, template, requirement))


class IsolatedAutorunEnvWiringTests(unittest.TestCase):
    """R5's autorun half has NO committed consumer: no spec uses `[driver.autorun]`,
    so `run.py`'s env construction is never exercised end to end. Deleting the two
    lines that set `PARSEK_AUTORUN_ISOLATED` kept the entire suite green.

    That is the same "caught by nothing at all" shape the C# side of this feature
    already had to close, and the fix is the same: a source-text fence, because the
    thing being asserted is a CROSS-PROCESS wire contract (the name here must equal
    `TestRunnerShortcut.EnvIsolatedVar`, pinned on the C# side by
    IsolatedBatchDispatchWiringTests) rather than a decision a pure function makes.
    """

    @classmethod
    def setUpClass(cls):
        with open(os.path.join(HARNESS_ROOT, "run.py"), encoding="utf-8") as fh:
            cls.src = fh.read()

    def test_the_env_block_sets_the_isolated_var_from_the_autorun_flag(self):
        self.assertIn('env["PARSEK_AUTORUN_ISOLATED"] = "1"', self.src,
                      "run.py no longer arms the isolated autorun env var")
        self.assertIn("autorun.get(hlib.BATCH_ISOLATED_KEY)", self.src,
                      "the env var must be driven by the spec's own autorun flag, "
                      "not by a literal or an unrelated key")

    def test_the_isolated_var_is_set_only_inside_the_autorun_block(self):
        # It must sit under `if autorun and autorun.get("tests")`: arming it for a
        # RunTests-step spec would set an env var the addon reads at Awake, which
        # would isolate a batch the spec never asked to isolate.
        i_tests = self.src.index('env["PARSEK_AUTORUN_TESTS"]')
        i_iso = self.src.index('env["PARSEK_AUTORUN_ISOLATED"]')
        i_pop = self.src.index('env.pop("PARSEK_ANALYZER_BASELINE_MODE"')
        self.assertLess(i_tests, i_iso,
                        "the isolated var must be set after (and therefore inside) "
                        "the autorun-tests guard")
        self.assertLess(i_iso, i_pop)

    def test_the_launch_line_reports_the_resolved_batch_mode(self):
        # A run's own log is the only post-hoc record of which filter it used.
        self.assertIn("hlib.spec_batch_isolated(spec)", self.src)
        self.assertIn("batchIsolated=%s", self.src)


# ---------------------------------------------------------------------------
# Selection.
# ---------------------------------------------------------------------------


class SelectionTests(unittest.TestCase):
    """Guards: a cadence must not silently drop or add scenarios (nightly
    coverage would be wrong without anyone noticing)."""

    SPECS = [
        {"id": "A", "tier": "daily", "tags": ["R14", "ledger"]},
        {"id": "B", "tier": "nightly", "tags": ["R14"]},
        {"id": "C", "tier": "weekly", "tags": ["mods"]},
        {"id": "D", "tier": "perpr", "tags": []},
        {"id": "E", "tier": "operator", "tags": ["rewind", "pending-operator"]},
        {"id": "P", "tier": "pending-fixture", "tags": ["awaiting-fixture"]},
    ]

    def test_by_id(self):
        self.assertEqual([s["id"] for s in hlib.select_scenarios(self.SPECS, "--id B")], ["B"])

    def test_by_tier(self):
        self.assertEqual([s["id"] for s in hlib.select_scenarios(self.SPECS, "--tier daily")], ["A"])

    def test_by_tag(self):
        self.assertEqual([s["id"] for s in hlib.select_scenarios(self.SPECS, "--tag R14")], ["A", "B"])

    def test_cadence_nightly_is_daily_plus_nightly(self):
        got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence nightly")]
        self.assertEqual(got, ["A", "B"])

    def test_cadence_weekly_excludes_noncadence_tiers(self):
        # Weekly is the widest cadence yet NEITHER non-cadence tier is selected:
        # operator specs (E) run only under an explicit --tier/--id, and
        # pending-fixture (P) is a readiness state, not a cadence.
        got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence weekly")]
        self.assertEqual(got, ["A", "B", "C", "D"])

    def test_operator_tier_in_no_cadence(self):
        for cadence in ("per-pr", "daily", "nightly", "weekly"):
            got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence " + cadence)]
            self.assertNotIn("E", got, "operator spec leaked into cadence %s" % cadence)

    def test_operator_tier_selectable_by_explicit_tier(self):
        got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--tier operator")]
        self.assertEqual(got, ["E"])

    def test_operator_is_valid_tier_vocabulary(self):
        self.assertIn("operator", hlib.TIERS)
        self.assertNotIn("operator", [t for ts in hlib.CADENCE_TIERS.values() for t in ts])

    def test_cadence_daily(self):
        # Item 4: a daily cadence run must NOT pick up the pending-fixture spec (it
        # would INVALID(staging) terminally and self-quarantine a scenario that never ran).
        got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence daily")]
        self.assertEqual(got, ["A"])

    def test_pending_fixture_excluded_from_all_cadences_but_tier_selectable(self):
        # Item 4: no cadence resolves to pending-fixture, but --tier still selects it so
        # an operator can smoke-run it the moment its fixture lands.
        for cadence in ("per-pr", "daily", "nightly", "weekly"):
            got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence %s" % cadence)]
            self.assertNotIn("P", got, "pending-fixture leaked into --cadence %s" % cadence)
        self.assertEqual(
            [s["id"] for s in hlib.select_scenarios(self.SPECS, "--tier pending-fixture")], ["P"])
        self.assertIn("pending-fixture", hlib.TIERS)

    def test_unknown_kind_empty(self):
        self.assertEqual(hlib.select_scenarios(self.SPECS, "--bogus x"), [])


# ---------------------------------------------------------------------------
# Analyzer sub-classification (STALE vs FAIL split from the analysis JSON).
# ---------------------------------------------------------------------------


class ClassifyAnalyzerTests(unittest.TestCase):
    """Guards S1/S2: an absent RED token must never read green (the most
    dangerous silent pass); a stale corpus must not be triaged as a code defect;
    a real defect must never hide behind a fixture-authoring FAIL; and the split
    is read from the JSON, not the txt header."""

    def _aj(self, fnb, snb, findings=()):
        return hlib.AnalysisJson(fnb, snb, tuple(findings))

    def _f(self, rid, level="FAIL", baselined=False):
        return hlib.AnalysisFinding(rid, level, "t", baselined)

    def test_red_absent_is_analyzer_error(self):
        v = hlib.classify_analyzer(None, None)
        self.assertEqual((v.status, v.subkind), ("INVALID", "analyzer-error"))

    def test_red_zero_is_pass(self):
        self.assertEqual(hlib.classify_analyzer(0, self._aj(0, 0)).status, "PASS")

    def test_red_one_real_fail_is_parsek_fail(self):
        aj = self._aj(2, 0, [self._f("INV3-ABSOLUTE-RANGE")])
        v = hlib.classify_analyzer(1, aj)
        self.assertEqual((v.status, v.top_rule), ("PARSEK-FAIL", "INV3-ABSOLUTE-RANGE"))

    def test_red_one_stale_only_is_fixture_stale(self):
        v = hlib.classify_analyzer(1, self._aj(0, 3))
        self.assertEqual((v.status, v.subkind), ("INVALID", "fixture-stale"))

    def test_red_one_baseline_only_is_fixture_authoring(self):
        aj = self._aj(0, 0, [self._f("BASELINE-FORBIDDEN")])
        v = hlib.classify_analyzer(1, aj)
        self.assertEqual((v.status, v.subkind), ("INVALID", "fixture-authoring"))

    def test_red_one_baseline_plus_real_is_parsek_fail(self):
        aj = self._aj(1, 0, [self._f("BASELINE-FORBIDDEN"), self._f("INV2-NO-DOUBLE-COVER")])
        self.assertEqual(hlib.classify_analyzer(1, aj).status, "PARSEK-FAIL")

    def test_red_one_no_json_fallback_parsek_fail(self):
        # A red gate with no JSON detail must never read green.
        self.assertEqual(hlib.classify_analyzer(1, None).status, "PARSEK-FAIL")


# ---------------------------------------------------------------------------
# Verdict classification matrix.
# ---------------------------------------------------------------------------


def _clean_pass_facts():
    driver = {
        "spec_valid": True, "admission_ok": True, "instance_lock_ok": True,
        "instance_busy": False, "boot_crashed": False, "batch_crashed": False,
        "valid": True,
    }
    verifiers = {
        "killed": False, "batch_expected": True, "batch_present": True,
        "tooling_invalid": False, "analyzer": hlib.AnalyzerVerdict("PASS", "", None),
        "log_validate_failed": False, "results_failed": False, "results_mismatch": False,
        "anomaly_hit": False, "expectation_mismatch": False, "ledger_drift": False,
    }
    return driver, verifiers


class ClassifyVerdictMatrixTests(unittest.TestCase):
    """Guards: a fixture-stale run must not poison the Parsek-defect bucket, an
    expected-fail bug must not red the nightly, an XPASS must not silently
    promote and drop the guard, a real defect must not hide behind a
    fixture-authoring FAIL, and PARSEK-FAIL must never be retried."""

    def _classify(self, driver, verifiers, expected_fail=None, attempt=1, policy="once"):
        return hlib.classify_verdict(driver, verifiers, expected_fail or {"bugId": ""}, attempt, policy)

    def test_clean_pass(self):
        d, v = _clean_pass_facts()
        self.assertEqual(self._classify(d, v).verdict, "PASS")

    def test_admission_drift_invalid(self):
        d, v = _clean_pass_facts()
        d["admission_ok"] = False
        d["admission_subkind"] = "admission"
        r = self._classify(d, v)
        self.assertEqual((r.verdict, r.subkind), ("INVALID", "admission"))
        self.assertFalse(hlib.should_retry(r, 1, "once"))

    def test_instance_busy_invalid(self):
        d, v = _clean_pass_facts()
        d["instance_busy"] = True
        self.assertEqual(self._classify(d, v).subkind, "instance-busy")

    def test_killed_short_circuits(self):
        d, v = _clean_pass_facts()
        v["killed"] = True
        v["analyzer"] = None  # torn save; analyzer skipped
        r = self._classify(d, v)
        self.assertEqual(r.verdict, "KILLED")
        self.assertFalse(hlib.should_retry(r, 1, "once"))

    def test_boot_crash_retryable_then_repeated(self):
        d, v = _clean_pass_facts()
        d["boot_crashed"] = True
        r = self._classify(d, v)
        self.assertEqual((r.verdict, r.subkind), ("INVALID", "boot-crash"))
        self.assertTrue(hlib.should_retry(r, 1, "once"))
        d["boot_crash_repeated"] = True
        r2 = self._classify(d, v, attempt=2)
        self.assertEqual(r2.subkind, "boot-crash-repeated")
        self.assertFalse(hlib.should_retry(r2, 2, "once"))

    def test_batch_crashed_is_parsek_fail_not_retried(self):
        d, v = _clean_pass_facts()
        d["batch_crashed"] = True
        r = self._classify(d, v)
        self.assertEqual((r.verdict, r.subkind), ("PARSEK-FAIL", "batch-crashed"))
        self.assertFalse(hlib.should_retry(r, 1, "once"))

    def test_driver_stage_failed_invalid_retryable(self):
        d, v = _clean_pass_facts()
        d["valid"] = False
        d["stage_subkind"] = "load-failed"
        r = self._classify(d, v)
        self.assertEqual((r.verdict, r.subkind), ("INVALID", "load-failed"))
        self.assertTrue(hlib.should_retry(r, 1, "once"))

    def test_expected_batch_absent_is_batch_crashed(self):
        d, v = _clean_pass_facts()
        v["batch_present"] = False
        self.assertEqual(self._classify(d, v).subkind, "batch-crashed")

    def test_tooling_invalid_retryable(self):
        d, v = _clean_pass_facts()
        v["tooling_invalid"] = True
        v["tooling_subkind"] = "tooling"
        r = self._classify(d, v)
        self.assertEqual((r.verdict, r.subkind), ("INVALID", "tooling"))
        self.assertTrue(hlib.should_retry(r, 1, "once"))

    def test_analyzer_stale_only_invalid(self):
        d, v = _clean_pass_facts()
        v["analyzer"] = hlib.AnalyzerVerdict("INVALID", "fixture-stale", None)
        r = self._classify(d, v)
        self.assertEqual((r.verdict, r.subkind), ("INVALID", "fixture-stale"))
        self.assertFalse(hlib.should_retry(r, 1, "once"))  # not retryable

    def test_analyzer_error_retryable(self):
        d, v = _clean_pass_facts()
        v["analyzer"] = hlib.AnalyzerVerdict("INVALID", "analyzer-error", None)
        r = self._classify(d, v)
        self.assertTrue(hlib.should_retry(r, 1, "once"))

    def test_analyzer_real_fail_parsek_not_retried(self):
        d, v = _clean_pass_facts()
        v["analyzer"] = hlib.AnalyzerVerdict("PARSEK-FAIL", "analyzer", "INV3")
        r = self._classify(d, v)
        self.assertEqual(r.verdict, "PARSEK-FAIL")
        self.assertFalse(hlib.should_retry(r, 1, "once"))

    def test_log_contract_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["log_validate_failed"] = True
        self.assertEqual(self._classify(d, v).subkind, "log-contract")

    def test_anomaly_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["anomaly_hit"] = True
        self.assertEqual(self._classify(d, v).subkind, "anomaly")

    def test_expectation_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["expectation_mismatch"] = True
        self.assertEqual(self._classify(d, v).subkind, "expectation")


class ExpectedFailOverlayTests(unittest.TestCase):
    """Guards N8/N11: an expected-fail bug must not red the nightly when its
    signature matches; a DIFFERENT failure still surfaces as PARSEK-FAIL; and a
    clean run is XPASS, never a silent PASS that drops the guard."""

    def _classify(self, driver, verifiers, ef):
        return hlib.classify_verdict(driver, verifiers, ef, 1, "once")

    def test_signature_match_demotes_to_expected_fail(self):
        d, v = _clean_pass_facts()
        v["analyzer"] = hlib.AnalyzerVerdict("PARSEK-FAIL", "analyzer", "INV3")
        r = self._classify(d, v, {"bugId": "R10-reaim", "signature_matched": True})
        self.assertEqual(r.verdict, "EXPECTED-FAIL")
        self.assertTrue(r.expected_fail_matched)

    def test_different_failure_stays_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["log_validate_failed"] = True  # bug targets analyzer, failed on log-contract
        r = self._classify(d, v, {"bugId": "R10-reaim", "signature_matched": False})
        self.assertEqual(r.verdict, "PARSEK-FAIL")

    def test_clean_run_is_xpass_not_pass(self):
        d, v = _clean_pass_facts()
        r = self._classify(d, v, {"bugId": "R10-reaim", "signature_matched": False})
        self.assertEqual(r.verdict, "XPASS")

    def test_invalid_unaffected_by_expected_fail(self):
        d, v = _clean_pass_facts()
        d["admission_ok"] = False
        r = self._classify(d, v, {"bugId": "R10-reaim", "signature_matched": True})
        self.assertEqual(r.verdict, "INVALID")


class ExpectedFailSignatureMatchTests(unittest.TestCase):
    """Guards S2: expectedFail.subkind narrows the signature match to one PARSEK-FAIL
    class, so an expected-fail scenario that fails a DIFFERENT way (subkind mismatch)
    stays PARSEK-FAIL rather than being demoted to EXPECTED-FAIL; an empty subkind is
    bugId-only (any PARSEK-FAIL matches). The design's own regression row is the
    same-scenario-different-subkind case."""

    def test_empty_subkind_matches_any_parsek_fail(self):
        self.assertTrue(hlib.expected_fail_signature_matched("PARSEK-FAIL", "analyzer", ""))
        self.assertTrue(hlib.expected_fail_signature_matched("PARSEK-FAIL", "log-contract", ""))

    def test_matching_subkind_matches(self):
        self.assertTrue(hlib.expected_fail_signature_matched("PARSEK-FAIL", "analyzer", "analyzer"))

    def test_different_subkind_does_not_match(self):
        # The design's regression row: same scenario, tracked subkind=analyzer, but
        # this run failed on log-contract -> NOT a signature match -> stays PARSEK-FAIL.
        self.assertFalse(hlib.expected_fail_signature_matched("PARSEK-FAIL", "log-contract", "analyzer"))
        base = hlib.Verdict(hlib.VERDICT_PARSEK_FAIL, "log-contract", False, "log validation failed")
        matched = hlib.expected_fail_signature_matched(base.verdict, base.subkind, "analyzer")
        overlaid = hlib.classify_expected_fail(base, "R10-reaim", matched)
        self.assertEqual(overlaid.verdict, hlib.VERDICT_PARSEK_FAIL)

    def test_non_parsek_fail_never_matches(self):
        self.assertFalse(hlib.expected_fail_signature_matched("PASS", "", ""))
        self.assertFalse(hlib.expected_fail_signature_matched("INVALID", "boot-crash", ""))

    def test_unknown_subkind_rejected_by_spec_validation(self):
        reg = load_registry()
        spec = load_spec("B10-career-passive-safety.toml")
        spec["expectedFail"]["subkind"] = "not-a-subkind"
        v = hlib.validate_spec(spec, reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("expectedFail.subkind" in e for e in v.errors))

    def test_known_subkind_accepted_by_spec_validation(self):
        reg = load_registry()
        spec = load_spec("B10-career-passive-safety.toml")
        spec["expectedFail"]["bugId"] = "R10-reaim"
        spec["expectedFail"]["subkind"] = "analyzer"
        v = hlib.validate_spec(spec, reg, bug_ids=["R10-reaim"])
        self.assertTrue(v.ok, "a known PARSEK-FAIL subkind must validate; errors=%s" % (v.errors,))


class ResolveTerminalTests(unittest.TestCase):
    """Guards: a flaked-then-passed pair must terminate PASS with the note (no
    FLAKE verdict), while its attempt-1 INVALID stays visible for the ledger."""

    def _inv(self):
        return hlib.Verdict("INVALID", "boot-crash", True, "boot")

    def _pass(self):
        return hlib.Verdict("PASS", "", False, "clean")

    def test_flaked_then_passed(self):
        t = hlib.resolve_terminal([self._inv(), self._pass()])
        self.assertEqual(t.verdict, "PASS")
        self.assertEqual(t.note, "flakedThenPassed")

    def test_plain_pass_no_note(self):
        t = hlib.resolve_terminal([self._pass()])
        self.assertEqual(t.note, "")

    def test_two_invalids_terminal_invalid(self):
        t = hlib.resolve_terminal([self._inv(), self._inv()])
        self.assertEqual(t.verdict, "INVALID")


# ---------------------------------------------------------------------------
# Log-validation profile selection.
# ---------------------------------------------------------------------------


class LogValidateProfileTests(unittest.TestCase):
    """Guards B1/S13: a clean no-recording B10 run must not red on REC-001/003,
    and a killed run must not red on marker-pairing; the two profiles compose."""

    def test_no_recording_suppresses_rec_only(self):
        p = hlib.select_logvalidate_profile(False, killed=False)
        self.assertTrue(p.suppress_recording_rules)
        self.assertFalse(p.killed_run_mode)
        self.assertEqual(set(p.suppressed_rules), {"REC-001", "REC-003"})
        self.assertIn("SES-000", p.mandatory_rules)
        self.assertIn("FMT-001", p.mandatory_rules)

    def test_recording_scenario_no_suppression(self):
        p = hlib.select_logvalidate_profile(True, killed=False)
        self.assertFalse(p.suppress_recording_rules)
        self.assertEqual(p.suppressed_rules, ())
        self.assertIn("REC-001", p.mandatory_rules)

    def test_killed_suppresses_marker_pairing(self):
        p = hlib.select_logvalidate_profile(True, killed=True)
        self.assertTrue(p.killed_run_mode)
        self.assertEqual(set(p.suppressed_rules), {"SES-000", "SES-001", "REC-001", "REC-003"})
        self.assertEqual(set(p.mandatory_rules), {"FMT-001", "FMT-002", "WRN-001"})

    def test_both_profiles_compose(self):
        p = hlib.select_logvalidate_profile(False, killed=True)
        self.assertTrue(p.suppress_recording_rules and p.killed_run_mode)
        self.assertEqual(set(p.suppressed_rules), {"SES-000", "SES-001", "REC-001", "REC-003"})


# ---------------------------------------------------------------------------
# Budget arithmetic.
# ---------------------------------------------------------------------------


class BudgetArithmeticTests(unittest.TestCase):
    """Guards S8: the harness step-wait must clear the seam's deferral budget by
    the 60s margin so a genuine seam TIMEOUT is observed, not pre-empted."""

    def test_required_step_wait_adds_margin(self):
        self.assertEqual(hlib.required_step_wait(540), 600)

    def test_step_wait_ok_boundary(self):
        self.assertTrue(hlib.step_wait_ok(600, 540))
        self.assertFalse(hlib.step_wait_ok(599, 540))

    def test_dispatch_deferral_budget_mirrors_c_sharp(self):
        # Item 3: the per-verb dispatch deferral budgets mirror the C# DeferralBudget.
        self.assertEqual(hlib.dispatch_deferral_budget("AnswerMergeDialog"), 120.0)
        self.assertEqual(hlib.dispatch_deferral_budget("KscAction"), 60.0)
        self.assertEqual(hlib.dispatch_deferral_budget("StartRecording"), 180.0)
        # R12: ExitToSpaceCenterSeconds = 120 (sized like AnswerMergeDialog, the other
        # scene-exit driver), and SimulateStockSwitchClick is single-phase on the default.
        self.assertEqual(hlib.dispatch_deferral_budget("ExitToSpaceCenter"), 120.0)
        self.assertEqual(hlib.dispatch_deferral_budget("SimulateStockSwitchClick"), 60.0)
        # An unlisted verb rides the 60s default (the C# DefaultSeconds).
        self.assertEqual(hlib.dispatch_deferral_budget("SetSetting"), 60.0)
        # RunTests defers to the declared scenario budget when supplied.
        self.assertEqual(hlib.dispatch_deferral_budget("RunTests", 900.0), 900.0)

    def test_required_dispatch_step_wait_adds_margin(self):
        # Item 3: AnswerMergeDialog (120s) + margin => a non-two-phase verb still
        # out-waits its seam-side deferral so the seam TIMEOUT is observed, not KILLed.
        self.assertEqual(hlib.required_dispatch_step_wait("AnswerMergeDialog"), 180.0)
        self.assertEqual(hlib.required_dispatch_step_wait("KscAction"), 120.0)
        # A default-60s verb also clears the 60s window + margin (no 60==60 race).
        self.assertEqual(hlib.required_dispatch_step_wait("SetSetting"), 120.0)


# ---------------------------------------------------------------------------
# Expectations evaluation + anomaly sweep.
# ---------------------------------------------------------------------------


class EvaluateExpectationsTests(unittest.TestCase):
    """Guards verifier 7: a count outside the window or an unmet required pattern
    reds; a forbidden pattern present reds; reserved blocks stay SKIPPED."""

    def test_pass_when_all_met(self):
        exp = {
            "recordings": {"count": {"min": 0, "max": 0}},
            "logContracts": {"required": [r"BATCH_COMPLETE v1 .* failed=0\b"],
                             "forbidden": [r"\[Parsek\]\[Error\]"]},
        }
        log = "BATCH_COMPLETE v1 total=12 passed=12 failed=0 skipped=0 category=X scene=FLIGHT"
        r = hlib.evaluate_expectations(exp, 0, log)
        self.assertEqual(r.status, "PASS")

    def test_count_out_of_window(self):
        exp = {"recordings": {"count": {"min": 0, "max": 0}}}
        r = hlib.evaluate_expectations(exp, 3, "")
        self.assertEqual(r.status, "FAIL")
        self.assertTrue(any("count" in m for m in r.mismatches))

    def test_required_not_matched(self):
        exp = {"logContracts": {"required": [r"BATCH_COMPLETE v1 .* failed=0\b"]}}
        r = hlib.evaluate_expectations(exp, None, "nothing here")
        self.assertEqual(r.status, "FAIL")

    def test_forbidden_matched(self):
        exp = {"logContracts": {"forbidden": [r"\[Parsek\]\[Error\]"]}}
        r = hlib.evaluate_expectations(exp, None, "[Parsek][Error] boom")
        self.assertEqual(r.status, "FAIL")

    def test_forbidden_is_case_sensitive_lowercase_pattern_misses_uppercase(self):
        # S4 policy: forbidden patterns are case-sensitive re.search, and
        # ParsekLog.Write emits an UPPERCASE level ("[Parsek][ERROR][...]"). A
        # LOWERCASE "[Parsek][Error]" pattern therefore does NOT match a real
        # uppercase error line -> it would silently PASS a run that logged an
        # error. The committed specs use the uppercase pattern for exactly this
        # reason; this test documents the case-sensitivity as the policy.
        real_line = "[Parsek][ERROR][Recorder] boom"
        lower = {"logContracts": {"forbidden": [r"\[Parsek\]\[Error\]"]}}
        self.assertEqual(hlib.evaluate_expectations(lower, None, real_line).status, "PASS",
                         "a lowercase forbidden pattern must NOT match an uppercase ERROR line")
        upper = {"logContracts": {"forbidden": [r"\[Parsek\]\[ERROR\]"]}}
        self.assertEqual(hlib.evaluate_expectations(upper, None, real_line).status, "FAIL",
                         "the uppercase pattern (as the committed specs use) must catch a real ERROR line")

    def test_anchored_failed_zero_not_matched_by_failed_five(self):
        # \b anchor: "failed=0" must not match "failed=05".
        exp = {"logContracts": {"required": [r"BATCH_COMPLETE v1 .* failed=0\b"]}}
        log = "BATCH_COMPLETE v1 total=12 passed=7 failed=05 skipped=0 category=X scene=FLIGHT"
        self.assertEqual(hlib.evaluate_expectations(exp, None, log).status, "FAIL")

    def test_reserved_blocks_recorded(self):
        # route/loop stay reserved until their verifiers land (their consumers do
        # not exist yet; rewind LEFT the tuple with M-C2/R9, see below).
        exp = {"route": {"x": 1}, "recordings": {"count": {"min": 0, "max": 0}}}
        r = hlib.evaluate_expectations(exp, 0, "")
        self.assertIn("route", r.reserved)

    def test_rewind_no_longer_reserved_after_mc2(self):
        # M-C2 (R9): rewind LEFT RESERVED_EXPECTATION_BLOCKS the same way world did
        # with M-B2 -- the save-parse verifier row is its SOLE owner, so slot 7 must
        # NOT record it as reserved (exactly ONE owner, no double-count).
        exp = {"rewind": {"supersedeRows": {"max": 0}},
               "recordings": {"count": {"min": 0, "max": 0}}}
        r = hlib.evaluate_expectations(exp, 0, "")
        self.assertNotIn("rewind", r.reserved)
        self.assertNotIn("rewind", hlib.RESERVED_EXPECTATION_BLOCKS)
        self.assertEqual(("route", "loop"), hlib.RESERVED_EXPECTATION_BLOCKS)

    def test_world_no_longer_reserved_after_mb2(self):
        # M-B2 (design ~495): world LEFT RESERVED_EXPECTATION_BLOCKS -- verifier 8
        # is its SOLE owner now, so slot 7 must NOT record it as reserved (no
        # double-count). A world-only expectation therefore has no reserved block here.
        exp = {"world": {"vessels": {"entry": []}}, "recordings": {"count": {"min": 0, "max": 0}}}
        r = hlib.evaluate_expectations(exp, 0, "")
        self.assertNotIn("world", r.reserved)
        self.assertNotIn("world", hlib.RESERVED_EXPECTATION_BLOCKS)
        # ledger was never reserved (it is a tolerated-unknown block slot 7 ignores).
        self.assertNotIn("ledger", hlib.RESERVED_EXPECTATION_BLOCKS)

    def test_rendercomposition_never_reserved_because_the_evaluator_ships(self):
        """M-A7 takes the LEDGER shape, not the world/rewind one.

        RESERVED_EXPECTATION_BLOCKS is for a block with NO evaluator: slot 7
        records it as `reserved` so a spec declaring it does not silently assert
        nothing. `renderComposition` never enters the tuple because its evaluator
        (rendercompose.evaluate_render_composition, row 7c) lands in the SAME
        change - exactly as `ledger` never entered it. Slot 7 tolerates unknown
        blocks, so the block validates, arms PARSEK_RENDER_MANIFEST at launch, is
        evaluated by its own sole owner, and is NOT double-counted as reserved.

        Were this to invert - the block entering the tuple while row 7c also owns
        it - a declaring spec would carry the block in `expectations.reserved`
        (reading as "nothing evaluates this") while it was in fact gating."""
        exp = {rendercompose.RENDER_COMPOSITION_BLOCK: {"dwells": {"min": 1}},
               "recordings": {"count": {"min": 0, "max": 0}}}
        r = hlib.evaluate_expectations(exp, 0, "")
        self.assertNotIn(rendercompose.RENDER_COMPOSITION_BLOCK, r.reserved)
        self.assertNotIn(rendercompose.RENDER_COMPOSITION_BLOCK,
                         hlib.RESERVED_EXPECTATION_BLOCKS)
        # The tuple itself is unchanged by M-A7: route/loop still have no owner.
        self.assertEqual(("route", "loop"), hlib.RESERVED_EXPECTATION_BLOCKS)
        # ... and slot 7 does not red the run for the unknown block either.
        self.assertEqual("PASS", r.status)


class ObservedExpectationFacetsTests(unittest.TestCase):
    """Guards the MEASURED-facet record verifier 7 now carries. The gap it closes:
    a PASS does not run collect-logs and the produced save is transient, so a green
    run's recordings count was unrecoverable post-hoc - which is precisely the number
    needed to turn a provisional count window into an honest pin."""

    def test_measured_count_is_recorded(self):
        self.assertEqual(hlib.observed_expectation_facets(7), {"recordings": {"count": 7}})

    def test_zero_is_recorded_not_omitted(self):
        # 0 is a MEASUREMENT (a no-recording scenario legitimately produces none);
        # only None means "not measured", so a falsy-check would lose real data.
        self.assertEqual(hlib.observed_expectation_facets(0), {"recordings": {"count": 0}})

    def test_none_omits_the_key_entirely(self):
        # ABSENT means "not measured" - never zero. A consumer pinning a window off
        # a defaulted 0 would pin a lie.
        self.assertEqual(hlib.observed_expectation_facets(None), {})

    def test_evaluate_carries_observed_even_with_no_count_spec(self):
        # Recording is unconditional on the spec: a scenario that declares NO count
        # window still gets its measured count, which is how a new scenario earns
        # its first honest window.
        r = hlib.evaluate_expectations({"logContracts": {"required": []}}, 4, "")
        self.assertEqual(r.status, "PASS")
        self.assertEqual(r.observed, {"recordings": {"count": 4}})

    def test_observed_is_recorded_on_a_failing_run_too(self):
        # The measured value is the diagnostic on a FAIL ("9 > max 8" only tells you
        # the window is wrong if you can see the 9), so it must not be PASS-only.
        exp = {"recordings": {"count": {"min": 1, "max": 8}}}
        r = hlib.evaluate_expectations(exp, 9, "")
        self.assertEqual(r.status, "FAIL")
        self.assertEqual(r.observed, {"recordings": {"count": 9}})

    def test_observed_defaults_empty_for_backward_compatible_construction(self):
        # Backward compatibility: the field is OPTIONAL, so an old 3-arg positional
        # construction still builds and reads as "no measurement recorded".
        legacy = hlib.ExpectationResult("PASS", tuple(), tuple())
        self.assertEqual(legacy.observed, {})

    def test_unmeasured_run_leaves_observed_absent(self):
        # A None count (save unreadable) must leave the block empty rather than
        # inventing a number.
        r = hlib.evaluate_expectations({"recordings": {"count": {"min": 1, "max": 8}}}, None, "")
        self.assertEqual(r.observed, {})


class AnomalySweepTests(unittest.TestCase):
    """Guards N2: an unallowed Tier-C line reds; a known-benign token in
    allowedAnomalies is tolerated; a scenario cannot invent a new anomaly."""

    def test_unallowed_hit_returned(self):
        hits = hlib.evaluate_anomaly_sweep(["line-blink"], [])
        self.assertEqual(hits, ["line-blink"])

    def test_allowed_token_tolerated(self):
        hits = hlib.evaluate_anomaly_sweep(["polyline-orbit-overlap"], ["polyline-orbit-overlap"])
        self.assertEqual(hits, [])

    def test_unknown_token_ignored(self):
        hits = hlib.evaluate_anomaly_sweep(["not-a-real-anomaly"], [])
        self.assertEqual(hits, [])

    def test_retired_dead_token_can_never_be_a_hit(self):
        # `icon-jump` was REMOVED from ANOMALY_TOKENS (2026-07-29): no producer raises
        # it, so gating it advertised coverage that did not exist. Even fed directly
        # as a hit it is ignored, which is why the removal moves no verdict.
        self.assertNotIn("icon-jump", hlib.ANOMALY_TOKENS)
        self.assertEqual([], hlib.evaluate_anomaly_sweep(["icon-jump"], []))


class AnomalyBudgetParseTests(unittest.TestCase):
    """The `allowedAnomalies` declaration surface: bare token (every committed spec's
    form) and the `{ token, maxCount }` budget, mixed freely in one array.

    Why a budget at all: a bare token tolerates an anomaly at ANY count, so a
    regression that turns one benign transient into a per-frame storm is
    indistinguishable from the transient. A ceiling makes those different claims."""

    def test_bare_token_is_unbudgeted(self):
        p = hlib.parse_allowed_anomalies(["line-blink"])
        self.assertEqual([], list(p.errors))
        self.assertEqual({"line-blink": None}, p.budgets)

    def test_budgeted_token_parses(self):
        p = hlib.parse_allowed_anomalies([{"token": "line-blink", "maxCount": 3}])
        self.assertEqual([], list(p.errors))
        self.assertEqual({"line-blink": 3}, p.budgets)

    def test_mixed_forms_parse_together(self):
        p = hlib.parse_allowed_anomalies(
            ["polyline-orbit-overlap", {"token": "line-blink", "maxCount": 2}])
        self.assertEqual([], list(p.errors))
        self.assertEqual({"polyline-orbit-overlap": None, "line-blink": 2}, p.budgets)

    def test_empty_and_none_parse_clean(self):
        for declared in (None, [], tuple()):
            p = hlib.parse_allowed_anomalies(declared)
            self.assertEqual({}, p.budgets)
            self.assertEqual([], list(p.errors))

    def test_table_without_token_rejects(self):
        p = hlib.parse_allowed_anomalies([{"maxCount": 3}])
        self.assertTrue(any("token" in e for e in p.errors))

    def test_negative_or_non_int_max_count_rejects(self):
        for bad in (-1, 1.5, "3", True):
            with self.subTest(maxCount=bad):
                p = hlib.parse_allowed_anomalies([{"token": "line-blink", "maxCount": bad}])
                self.assertTrue(any("maxCount" in e for e in p.errors), bad)

    def test_unknown_table_key_rejects(self):
        # A misspelled ceiling that silently parses as "unbudgeted" is the fail-open
        # this surface exists to close.
        p = hlib.parse_allowed_anomalies([{"token": "line-blink", "maxcount": 3}])
        self.assertTrue(any("unknown key" in e for e in p.errors))

    def test_non_string_non_table_entry_rejects(self):
        p = hlib.parse_allowed_anomalies([7])
        self.assertTrue(p.errors)

    def test_a_bare_string_declaration_rejects_instead_of_iterating_characters(self):
        # `allowedAnomalies = "line-blink"` (no array). Python iterates a string by
        # CHARACTER, so without the whole-value type check this produced budgets like
        # {"l": None, "i": None, ...} with warnings only - and validate_spec still
        # returned ok=True, leaving the author believing a tolerance was in force.
        p = hlib.parse_allowed_anomalies("line-blink")
        self.assertEqual({}, p.budgets)
        self.assertEqual(1, len(p.errors))
        self.assertIn("must be an array", p.errors[0])

    def test_a_bare_table_declaration_rejects_instead_of_iterating_keys(self):
        # The other half: an inline table written without the enclosing array
        # iterates by KEY ("token", "maxCount") into two garbage budgets.
        p = hlib.parse_allowed_anomalies({"token": "line-blink", "maxCount": 3})
        self.assertEqual({}, p.budgets)
        self.assertEqual(1, len(p.errors))

    def test_a_malformed_declaration_fails_spec_validation(self):
        # The end-to-end consequence: it must be a PRE-LAUNCH spec-invalid, not a
        # warning nobody reads (the same call the misplaced-key guard makes).
        spec = load_spec("B10-career-passive-safety.toml")
        spec["expectations"]["allowedAnomalies"] = "line-blink"
        v = hlib.validate_spec(spec, load_registry())
        self.assertFalse(v.ok)
        self.assertTrue(any("allowedAnomalies" in e for e in v.errors))

    def test_duplicate_token_keeps_the_tightest_ceiling(self):
        # A later bare entry must not widen an earlier budget back to unlimited.
        p = hlib.parse_allowed_anomalies([{"token": "line-blink", "maxCount": 2}, "line-blink"])
        self.assertEqual({"line-blink": 2}, p.budgets)
        p2 = hlib.parse_allowed_anomalies([{"token": "line-blink", "maxCount": 5},
                                           {"token": "line-blink", "maxCount": 1}])
        self.assertEqual({"line-blink": 1}, p2.budgets)

    def test_inert_token_warns_but_does_not_reject(self):
        p = hlib.parse_allowed_anomalies(["icon-jump"])
        self.assertEqual([], list(p.errors))
        self.assertTrue(any("RETIRED" in w for w in p.warnings))


class AnomalyBudgetSweepTests(unittest.TestCase):
    """The budget's verdict behavior: at/below the ceiling passes, above it reds, and
    an undeclared token is unchanged (any raise reds)."""

    ALLOWED = [{"token": "line-blink", "maxCount": 3}]

    def _sweep(self, count):
        return hlib.evaluate_anomaly_sweep(["line-blink"], self.ALLOWED,
                                           {"line-blink": count})

    def test_below_budget_passes(self):
        self.assertEqual([], self._sweep(1))

    def test_at_budget_passes(self):
        self.assertEqual([], self._sweep(3))

    def test_above_budget_reds(self):
        self.assertEqual(["line-blink"], self._sweep(4))

    def test_zero_budget_reds_on_the_first_raise(self):
        self.assertEqual(["line-blink"],
                         hlib.evaluate_anomaly_sweep(["line-blink"],
                                                     [{"token": "line-blink", "maxCount": 0}],
                                                     {"line-blink": 1}))

    def test_bare_token_still_tolerates_any_count(self):
        self.assertEqual([], hlib.evaluate_anomaly_sweep(
            ["line-blink"], ["line-blink"], {"line-blink": 9999}))

    def test_counts_are_optional_and_default_to_one(self):
        # BACKWARD COMPATIBILITY: every pre-existing 2-arg call must behave exactly as
        # before. With no counts a hit counts as ONE raise, so any budget >= 1 tolerates.
        self.assertEqual([], hlib.evaluate_anomaly_sweep(["line-blink"], self.ALLOWED))
        self.assertEqual(["line-blink"],
                         hlib.evaluate_anomaly_sweep(["line-blink"],
                                                     [{"token": "line-blink", "maxCount": 0}]))

    def test_undeclared_token_reds_regardless_of_budgets_elsewhere(self):
        self.assertEqual(["parity-drift"],
                         hlib.evaluate_anomaly_sweep(["parity-drift"], self.ALLOWED,
                                                     {"parity-drift": 1}))


class AnomalyTokenCountTests(unittest.TestCase):
    """`count_anomaly_tokens` is the budget's input: per-token RAISE counts, anchored
    on the same `phase=Anomaly ... reason=<token>` shape as the hit grep."""

    def _raise(self, token, n=1):
        return "\n".join("[Parsek][INFO][MapRenderTrace] phase=Anomaly pid=%d reason=%s"
                         % (i, token) for i in range(n))

    def test_counts_every_raise_not_just_the_first(self):
        self.assertEqual({"line-blink": 4}, hlib.count_anomaly_tokens(self._raise("line-blink", 4)))

    def test_ungated_reason_is_not_counted(self):
        # An ungated reason rides `unlisted_anomaly_reasons`; it must never enter a
        # gate's arithmetic. Example is `factory-parity` (a shadow comparator that
        # never drives a draw) since the 2026-08-04 promotion - the old example,
        # `icon-teleport`, is now a GATED token and does count.
        self.assertEqual({}, hlib.count_anomaly_tokens(self._raise("factory-parity", 3)))

    def test_a_line_merely_naming_a_token_is_not_counted(self):
        self.assertEqual({}, hlib.count_anomaly_tokens(
            "[Parsek][INFO][TestRunner] SpineDrive line-blink: over=False"))

    def test_empty_and_none_are_clean(self):
        for empty in (None, "", "\n\n"):
            self.assertEqual({}, hlib.count_anomaly_tokens(empty))


class UnityExceptionScanTests(unittest.TestCase):
    """GAP: nothing in the verifier chain ever read a line Parsek did not write.

    Every committed spec's `logContracts.forbidden` list carries Parsek-authored
    tokens only, and `validate-ksp-log.ps1` -> `ParsekLogContractChecker` parses ONLY
    `[Parsek]`-tagged lines (session markers, line FORMAT, WARN content, recording
    pairing). So a KSP.log full of raw `NullReferenceException` stack traces or an
    IMGUI `ArgumentException: GUILayout` storm passed every gate. This scan is the
    complement of that layer, REPORT-ONLY until a scenario arms it - which 14
    specs do since the 2026-08-04 calibration sweep (the allowlist cell below)."""

    NRE = ("NullReferenceException: Object reference not set to an instance of an object\n"
           "  at Something.Update () [0x00000] in <filename unknown>:0 \n")
    GUI = "ArgumentException: GUILayout: Mismatched LayoutGroup.repaint\n"

    def test_counts_each_pattern(self):
        counts = hlib.scan_unity_exceptions(self.NRE + self.GUI + self.NRE)
        self.assertEqual(2, counts["NullReferenceException"])
        self.assertEqual(1, counts["ArgumentException: GUILayout"])
        self.assertEqual(0, counts["MissingReferenceException"])

    def test_every_pattern_reports_a_number_even_at_zero(self):
        # A measurement ("we looked, and saw none"), not an absence.
        counts = hlib.scan_unity_exceptions("")
        self.assertEqual(sorted(n for n, _ in hlib.UNITY_EXCEPTION_PATTERNS), sorted(counts))
        self.assertEqual(0, sum(counts.values()))

    def test_parsek_reported_exception_is_not_counted(self):
        # A [Parsek] line naming an exception is the mod REPORTING a caught one -
        # already covered by the [Parsek][ERROR] forbidden tokens and WRN-001. Counting
        # it here would double-signal one event and make the number uncalibratable.
        counts = hlib.scan_unity_exceptions(
            "[Parsek][ERROR][Recorder] caught NullReferenceException in sample\n")
        self.assertEqual(0, counts["NullReferenceException"])

    def test_absent_block_is_report_only(self):
        r = hlib.evaluate_unity_exceptions(hlib.scan_unity_exceptions(self.NRE), None)
        self.assertEqual(hlib.UNITY_EXCEPTIONS_STATUS_REPORT, r.status)
        self.assertFalse(r.gating)
        self.assertEqual(1, r.total)
        self.assertEqual(tuple(), r.mismatches)

    def test_declared_max_total_gates_over_budget(self):
        r = hlib.evaluate_unity_exceptions(hlib.scan_unity_exceptions(self.NRE + self.GUI),
                                           {"maxTotal": 0})
        self.assertEqual("FAIL", r.status)
        self.assertTrue(r.gating)
        self.assertEqual(2, r.total)
        self.assertTrue(r.mismatches)
        self.assertIn("maxTotal 0", r.mismatches[0])

    def test_declared_max_total_passes_at_budget(self):
        r = hlib.evaluate_unity_exceptions(hlib.scan_unity_exceptions(self.NRE),
                                           {"maxTotal": 1})
        self.assertEqual("PASS", r.status)
        self.assertTrue(r.gating)

    def test_clean_log_with_declared_block_passes(self):
        r = hlib.evaluate_unity_exceptions(hlib.scan_unity_exceptions("all good\n"),
                                           {"maxTotal": 0})
        self.assertEqual("PASS", r.status)
        self.assertEqual(0, r.total)

    def test_declared_block_without_max_total_reports(self):
        r = hlib.evaluate_unity_exceptions(hlib.scan_unity_exceptions(self.NRE), {})
        self.assertEqual(hlib.UNITY_EXCEPTIONS_STATUS_REPORT, r.status)
        self.assertFalse(r.gating)

    def test_block_validation_rejects_a_ceiling_that_would_degrade_silently(self):
        self.assertEqual([], hlib.validate_unity_exception_expectations(None))
        self.assertEqual([], hlib.validate_unity_exception_expectations({"maxTotal": 0}))
        for bad in ({"maxTotal": -1}, {"maxTotal": "0"}, {"maxTotal": True},
                    {"maxTotals": 0}, {"maxTotal": 0, "extra": 1}):
            with self.subTest(block=bad):
                self.assertTrue(hlib.validate_unity_exception_expectations(bad), bad)
        self.assertTrue(hlib.validate_unity_exception_expectations("nope"))

    def test_declared_block_without_max_total_warns_that_it_gates_nothing(self):
        # NIT 2: a declared-but-empty block degrades silently to report-only - the
        # author wrote a header and armed nothing. WARN (nothing fails at run time),
        # not ERROR, and it must name the missing key.
        self.assertEqual([], hlib.unity_exception_expectation_warnings(None))
        self.assertEqual([], hlib.unity_exception_expectation_warnings({"maxTotal": 0}))
        warns = hlib.unity_exception_expectation_warnings({})
        self.assertEqual(1, len(warns))
        self.assertIn("maxTotal", warns[0])
        # Wired into validate_spec as a WARNING, and the spec still validates.
        spec = load_spec("B10-career-passive-safety.toml")
        spec["expectations"][hlib.UNITY_EXCEPTIONS_BLOCK] = {}
        v = hlib.validate_spec(spec, load_registry())
        self.assertTrue(v.ok, "an inert block is a warning, not a spec-invalid")
        self.assertTrue(any(hlib.UNITY_EXCEPTIONS_BLOCK in w for w in v.warnings))

    def test_only_the_armed_allowlist_arms_it(self):
        # The HARD SAFETY PROPERTY, in its post-calibration form. This cell asserted the
        # EMPTY set ("nothing declares the block, so the scan cannot move any nightly
        # verdict") from the day the scan shipped until the 2026-08-04 calibration sweep.
        # It is now an explicit ALLOWLIST of what that sweep MEASURED, so a 16th spec
        # arming the gate still reds here until its own evidence is recorded - and so
        # LOOSENING a ceiling is a deliberate edit in this file too, not a quiet widening
        # in a spec nobody re-reads.
        #
        # THE EVIDENCE, one population per group. Every reading below is DRIVER-VALID: a
        # run that did not fly measures the abort, not the lane, which is why CL-3's two
        # nonzero collected-log readings (1 and 2, both mission aborts) are excluded.
        #
        #   MAX 0 (12 specs) - every driver-valid reading of each is 0, across the
        #   failure-population collected logs, the archived green result JSONs, and the
        #   fresh all-green 2026-08-04 daily pass plus the singles flown beside it. The
        #   thinnest is L1-passive-sandbox, armed on its own fresh 0 plus the six-spec L1
        #   family's homogeneity (14+ readings, every one 0, one boot profile); the
        #   widest are V1 (8x0), CL-2 (9x0) and CL-3 (6x0 driver-valid).
        #
        #   The 12th is L2-ledger-groundtruth-career, armed in career-ledger B.2 and the
        #   THINNEST sample in the table: n=4, all 0 in each of the four counted
        #   classes. The readings are its reading run `2026-08-17_2202`
        #   (`status=REPORT gating=False total=0`), the two arming negative controls
        #   `_2228` and `_2231` (both driver-valid PARSEK-FAILs on OTHER verifiers, so
        #   the scan's own reading counts), and the armed run `_2233`. Three of those
        #   four came after the decision to arm, so the honest statement of the evidence
        #   AT ARMING TIME is one reading plus a borrowed half: L2 boots the same
        #   LoadGame -> SetSetting -> RunTests -> FlushAndQuit shape, on the same
        #   stock-minimal profile, over a career pad fixture, as B10 and the six L1
        #   specs above - eleven zeros on one boot profile - and L2 adds no vessel
        #   loading, no scene churn and no GUI surface of its own. Stated plainly so a
        #   future nonzero is read as what it is: the first raise this shape has ever
        #   produced, and a finding rather than a flake to be papered over with a
        #   ceiling.
        #
        #   CEILINGS (3 specs) - each has at least one nonzero driver-valid reading, so
        #   0 would be a flake rather than a gate:
        #     H23  n=29: 25x0 plus 2, 2, 2, 4 (observed max 4). Those raises are the
        #          gate-13 stock buildVesselsList SHUTDOWN race, counted twice each,
        #          once per vessel Unity reclaims - so the ceiling is gate 13's own
        #          mechanism bound for the 3-vessel fixture (3 reclaims x 2 = 6),
        #          not the observed max: 5 would red the legal third reclaim.
        #     S4.1 n=19: 18x0 and one 1.
        #     H5   n=3: 4 and 2 in the failure population, 0 fresh - a thin sample, so
        #          the ceiling is the status doc's short-spec band top, not a pin.
        expected = {
            "B10-career-passive-safety.toml": 0,
            "CL-2-pod-impact-ledger.toml": 0,
            "CL-3-refly-crew-tombstone.toml": 0,
            "L1-dismiss-kerbal-career.toml": 0,
            "L1-hire-kerbal-career.toml": 0,
            "L1-passive-sandbox.toml": 0,
            "L1-research-node-career.toml": 0,
            "L1-research-node-science.toml": 0,
            "L1-upgrade-facility-career.toml": 0,
            "L2-ledger-groundtruth-career.toml": 0,
            # L3-strategy-currency-conversion, armed 2026-08-18 on BORROWED evidence
            # with no reading of its own - and now n=2 of its own, both 0
            # (`2026-08-18_2019` driver-valid PARSEK-FAIL(results) and
            # `2026-08-18_2039` PASS). The Administration-canvas question the note
            # below raises is therefore ANSWERED for this shape: the hidden canvas
            # raises no counted class. The original reasoning is kept verbatim
            # because it is the reasoning that armed it before there was a reading.
            # THINNER than L2's already-thin n=1, and said plainly rather
            # than dressed up. The borrowed evidence is the eleven-spec zero-armed
            # family that boots the identical LoadGame -> SetSetting -> RunTests ->
            # FlushAndQuit shape on the same stock-minimal profile: B10 and the six
            # L1 specs over the SAME `fresh-career` template, every driver-valid
            # reading 0. What L3 adds beyond that family is a hidden Administration
            # canvas (the StrategyLifecycle readiness idiom instantiates and destroys
            # one), which is a GUI surface the L1 family never raises and which
            # `ArgumentException: GUILayout` counts - and no committed spec has ever
            # flown that category, so it is UNMEASURED. A nonzero first reading is
            # therefore a FINDING about the Administration hydration, and the honest
            # response is to record the count and decide, never to raise the ceiling.
            "L3-strategy-currency-conversion.toml": 0,
            # L3-strategy-exchanger-floor, armed 2026-08-20 on the STRONGEST evidence
            # any first-flight arming in this table has had. It is the sibling above
            # with ONE difference - the fixture reverts to the UNSEEDED `fresh-career`
            # - and it drives the SAME category through the SAME four seam steps on the
            # same stock-minimal profile. So it inherits both populations at once: the
            # eleven-spec zero-armed family that boots this exact shape over this exact
            # template, AND the sibling's own four driver-valid readings of this exact
            # category, two of which (`2026-08-18_2039`, `2026-08-18_2140`) were flown
            # on this very unseeded template. The Administration-canvas question that
            # armed the sibling on borrowed evidence is ANSWERED for this shape by those
            # readings: the hidden canvas raises no counted class. A nonzero reading
            # here would still be a FINDING - record the count and decide, never raise
            # the ceiling.
            "L3-strategy-exchanger-floor.toml": 0,
            "M1-mission-loop-unit.toml": 0,
            "V1-map-dwell-mun-orbit.toml": 0,
            "H23-tracking-station.toml": 6,
            "S4.1-rewind-merge.toml": 3,
            "H5-invariants-corpus.toml": 5,
        }
        armed = {}
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            block = (spec.get("expectations") or {}).get(hlib.UNITY_EXCEPTIONS_BLOCK)
            if block is not None:
                armed[name] = block.get(hlib.UNITY_EXCEPTIONS_MAX_TOTAL_KEY)
        self.assertEqual(sorted(expected), sorted(armed),
                         "a committed spec outside the armed allowlist armed the scan")
        # The declared VALUES, not just the membership. A declared block with no
        # `maxTotal` gates NOTHING (it degrades to the same report-only an absent block
        # gets), so a None here would be an allowlisted spec that arms nothing at all.
        self.assertEqual(expected, armed,
                         "an armed ceiling moved without its evidence moving with it")

    def test_over_budget_classifies_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["unity_exceptions_over_budget"] = True
        verdict = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(("PARSEK-FAIL", "unity-exception"),
                         (verdict.verdict, verdict.subkind))


class PendingOperatorTagHonestyTests(unittest.TestCase):
    """`pending-operator` must mean operator work is actually outstanding.

    The tag is NON-GATING (hlib's comment above TIERS: "A `pending-operator` tag
    alone is non-gating; the tier is"), and nothing selects on it but a generic
    `--tag`. That is exactly why it rots quietly: no run ever fails because a
    finished scenario still claims to be waiting on a human, so the only thing
    keeping `--tag pending-operator` useful is that someone notices. Nobody did -
    `L1-passive-sandbox` carried a stale one until 2026-07-26 and six more were
    still carrying theirs on 2026-07-31, by which point a two-reviewer panel had
    SPLIT on whether S4.1's was stale.

    WHY THIS IS TWO HAND-MAINTAINED LISTS AND NOT A CLEVERER CHECK. Three
    successive attempts to DECIDE the question from the spec text all failed, and
    the way they failed is the argument:

      1. "Does the spec mention PENDING-OPERATOR?" - counts a spec's own obituary
         for a debt ("the former PENDING-OPERATOR is CLOSED") as proof the debt
         lives. Six specs kept stale tags under it.
      2. "...in a comment block with no discharge phrase?" - still counted the
         drop-rationale prose written BY the commit that dropped the tags, so
         re-adding any of those six passed. And the phrase list is blunt in the
         other direction: "no longer" discounts S1.5's canonical GAP note, a
         genuinely STANDING marker, because the same block also says flight-scene
         entry is "NO LONGER a gap".
      3. Reading the marker prose at face value - tagged `B15-eve-flyby` on a
         PRE-FLIGHT risk note ("no headless test can verify it") whose question
         flight 7 had already ANSWERED, as that spec's own status row says.

    Every one of those is the same error: the truth lives in the STATUS ROW and
    the FIXTURE LEDGER, not in whether a token appears near some words. String
    matching over English cannot tell a live claim from a dead one, a true claim
    from a false one, or a spec's own debt from one it mentions on another's
    behalf (`S0.5` describes B1/B2's fixtures). So this cell does not try. It
    pins two INVENTORIES that a human maintains, and makes any change to either
    an explicit, reviewable edit:

      CARRIERS         - who owns the tag, and the reason.
      REVIEWED_UNTAGGED - every untagged spec that MENTIONS the token, and why
                          it does not carry it.

    Together they are total over the two populations this check can DETECT -
    specs that MENTION the token, and specs that are `tier = "operator"` - so a
    spec that gains the tag, loses it, starts mentioning it, or becomes
    operator-tier reds here until someone records which it is. (Covering only the
    first was a real gap: `B16-eve-orbit` is operator-tier with a documented
    outstanding human call and never writes the string.)

    DETECTABLE IS NOT THE SAME AS OWED, and the difference is not closable here.
    A spec can owe a human something while giving neither signal, which is how
    `EVA-1-pad-flag` went unnoticed: it says "the tier stays nightly until the
    operator promotes it" - a pending human call, on a nightly spec, with no
    token, so nothing made it red and nothing could, short of reading every
    spec. It was found by hand and is now a CARRIER.

    THAT CASE ALSO SETTLED WHAT COUNTS AS A DEBT, so the next reader does not
    re-derive it. The first instinct was to refuse the tag on the grounds that
    pending promotions are everywhere and tagging them would pad the list back
    out. The corpus says otherwise: a dozen specs carry re-tier or promotion
    prose, but B10, BDOCK-1, EVA-3 and the whole L1 family record promotions
    already DONE, so among untagged specs EVA-1 was the only OPEN one - tagging
    it added exactly one. And `S1.5-rewind-loop` is the precedent that settles
    the principle: it is `tier = "nightly"`, green, fully automated, and tagged
    purely because something needs a human. A cadence decision needs a human
    too. So the tag means WORK ONLY A HUMAN CAN DISCHARGE, not "a human must
    drive the run" - operator-tier is one sufficient reason, never the
    definition.

    What this cell can honestly promise is that the two DETECTABLE populations
    stay classified. That is
    a weaker guarantee than "the tag is always truthful" and a much more honest
    one - the check enforces that the inventory was REVIEWED, and the reviewer
    supplies the truth. For a fixture constant, `harness/fixtures/saves/README.md`
    is the semantic ledger; for whether a question is still open, the spec's row
    in `docs/dev/autotest-status.md` is."""

    # Who carries the tag, and why. `tier = "operator"` reasons are additionally
    # machine-checked below; the rest are human judgements recorded here.
    CARRIERS = {
        "R1-rewind-loop-flown.toml":   "tier=operator",
        # Landed 2026-08-03 with the CL stage-B re-fly lane. FLOWN since: three runs
        # (two PASS + a deliberate negative control) on 2026-08-03, armed the same
        # day, plus the crewless-root discrimination run on 2026-08-04. THREE of the
        # four debts this entry has carried are DISCHARGED - the `vesselStateChanged`
        # row was removed (it was constant-False on this lane), the arming follow-up
        # was done, and the `dead-crew-strip` fixture-change-plus-re-fly was SPENT
        # (2026-08-04_2136), which claimed the cell. What is recorded below is the one
        # debt still open, and it is narrower than its predecessor.
        "CL-3-refly-crew-tombstone.toml":
                                       "tier=operator AND one open operator call: the "
                                       "CONFIRM FLY of the D12 `dead-crew-strip` "
                                       "token. The cell is CLAIMED as of 2026-08-05, "
                                       "off a re-pinned half (ii) - `permanent=0` in "
                                       "the `Recomputed after tombstones:` line - and "
                                       "the 2026-08-04_2136 crewless-root re-fly that "
                                       "measured the semantics: the death-sourced "
                                       "reservation IS released, the survivor is the "
                                       "re-flown fork's own live one, and the stand-in "
                                       "is CREATED BY the release (a Dead row makes "
                                       "the reservation permanent and PostWalk skips "
                                       "permanents before slot creation, so the old "
                                       "`forbidden=[Stand-in generated]` candidate was "
                                       "INVERTED). The gate-armed confirm fly is "
                                       "DONE: 2026-08-04_2324, PASS attempt 1, the "
                                       "`permanent=0` token matched live. What the "
                                       "tag still names is the human tier-promotion "
                                       "call, nothing else.",
        "EVA-1-pad-flag.toml":         "open promotion call - 'the tier stays nightly until the "
                                       "operator promotes it'. P1/P3/P6 are all done and it has "
                                       "been LIVE-PROVEN since 2026-07-24, so nothing is blocked "
                                       "except the cadence decision itself, which only a human "
                                       "makes. NOT tier=operator: a nightly spec can owe operator "
                                       "work, exactly as S1.5 does.",
        "B16-eve-orbit.toml":          "tier=operator AND a documented outstanding human call - "
                                       "the PROMOTE note ('the PROVISIONAL pins need a human "
                                       "reading the result'); status doc: 'TIER NOT CHANGED ... "
                                       "left as an explicit human call'.",
        "S1.5-rewind-loop.toml":       "three live asserts: crew re-reservation and resource "
                                       "reset need a career fixture the sandbox host lacks; the "
                                       "self-authored RewindPoint needs a multi-controllable "
                                       "split plus a seam channel. No unattended run discharges them.",
        # S4.2-refly-world-preservation DROPPED 2026-08-12 - see DROPPED_2026_08_12.
    }

    # Untagged specs that are CANDIDATES - they MENTION the token, or they are
    # `tier = "operator"` (the FORGE trio is in by tier and never mentions it) -
    # each classified by hand. A NEW one reds
    # `test_every_untagged_candidate_is_classified` until someone decides.
    REVIEWED_UNTAGGED = {
        # tier=operator by the CALIBRATION DISCIPLINE, the whole B18-B26 family's
        # tier, and NOT a debt: a first-flight B lane is operator because its
        # windows are derived rather than measured and the first run is a
        # calibration reading, exactly the disposition B23/B24/B25/B26 carry.
        # IT HAS NOW FLOWN: run 2026-08-20_2330, PASS attempt 1, and its row in
        # autotest-status.md says LIVE-PROVEN. So the re-classification an earlier
        # draft of this comment asked a future reader to make is DONE, and what
        # remains open is the ordinary operator -> nightly PROMOTION call, which
        # is the shape H34/H35 above already record. Nothing technical is owed.
        "B28-laythe-jool-return.toml":      "tier=operator by the calibration discipline (the B18-B26 family's tier), NOT debt; FLOWN 2026-08-20_2330 PASS attempt 1 (the full chain through ORBIT-COMMITTED, all eight assertions met, every verifier green) - what is open is the ordinary operator -> nightly PROMOTION call, the H34/H35 shape",
        # B29, tier=operator by the SAME calibration discipline and at the earliest
        # point of it: AUTHORED 2026-08-26 AND NEVER FLOWN. Every window in the spec
        # is DERIVED (from `jool-park-nerv`'s own bytes plus the stock body
        # constants) rather than measured, which is precisely what the operator tier
        # is for on a first-flight B lane. NOT DEBT: nothing is outstanding that a
        # human decision would discharge - what is owed is the FLIGHT, and the tier
        # is the mechanism that schedules it rather than a marker of unfinished
        # human work. It carries one thing B23-B28 did not, and it is written down
        # here because it is the reason a reader might expect a tag: pre-registration
        # (1) in the spec records an UNTESTED ASSUMPTION (no committed lane has ever
        # run a non-relay `interplanetaryTransfer` from a non-Kerbin park) together
        # with the MechJeb refusal shape it would produce. That is a pre-registered
        # question with a named outcome, not an operator debt - if it fires the run
        # is driver-INVALID and report-only, and the re-argument belongs in the
        # spec's flight ledger.
        "B29-jool-kerbin-return.toml":      "tier=operator by the calibration discipline (the B18-B28 family's tier) at its earliest point, NOT debt; AUTHORED 2026-08-26 and NEVER FLOWN, every window derived rather than measured, so what is owed is the first flight and not a human call",
        # The V19 pair, tier=operator by the same calibration discipline and for
        # the same reason as every V lane before them: their windows were DERIVED
        # from B28's harvested bytes and the first run was a calibration reading.
        # NOT debt and no human call is owed. **THE DISCIPLINE IS COMPLETE AS OF
        # 2026-08-21 AND NOTHING REMAINS**: both reading runs flew green, both
        # lanes are ARMED off their own bytes (see ARMED_ALLOWLIST in this file),
        # both flew ARMED re-flights, and EACH RAN ITS OWN NEGATIVE CONTROL.
        # An earlier draft of this comment said what remained was "the armed
        # re-flights plus one shared negative control", which was wrong twice
        # over: nothing remains, and the controls were NOT shared. They could not
        # be - the halves pin DIFFERENT LENSES (V19M the proto ORBIT LINE on the
        # flight map, V19T the proto ICON in TRACKSTATION), so one shared
        # inversion would have proven exactly one of them. That is also why this
        # pair is the FIRST in the program to discharge roadmap confirmation
        # criterion (b), the required-RENDER-token inversion, rather than reusing
        # the shared `rewind.supersedeRows` evaluator inversion. So what is open
        # here is now only the ordinary operator -> nightly PROMOTION call, the
        # shape H34/H35 above record.
        "V19M-laythe-jool-player-loop.toml": "tier=operator by the calibration discipline (derived windows, first run is a calibration reading), NOT debt; committed 2026-08-21 and DISCIPLINE-COMPLETE the same day - reading `2026-08-21_0746` PASS attempt 1 (wall 98 s), ARMED off its own bytes, armed re-flight `_0852` PASS with saveParse gating and 0 mismatches, and its OWN negative control `_0855` PARSEK-FAIL(expectation) on 1 mismatch (`surface=ProtoOrbitLine .*body=Vall`) with saveParse still PASS, then reverted; what is open is the ordinary operator -> nightly PROMOTION call, the H34/H35 shape",
        "V19T-laythe-jool-ts-arrival.toml":  "tier=operator by the calibration discipline (derived windows, first run is a calibration reading), NOT debt; committed 2026-08-21 and DISCIPLINE-COMPLETE the same day - reading `2026-08-21_0750` PASS attempt 1 (wall 60 s), ARMED off its own bytes, armed re-flight `_0854` PASS, its OWN negative control `_0858` PARSEK-FAIL(expectation) on 1 mismatch (`surface=ProtoIcon ... body=Vall scene=TRACKSTATION`) with saveParse still PASS, and revert confirmation `_0859` PASS; `_0857` was an ATTEMPTED control that PASSED because a mis-escaped regex made no edit, so it is an extra armed confirmation and NOT a control; what is open is the ordinary operator -> nightly PROMOTION call, the H34/H35 shape",
        # The V20 pair, tier=operator by the same calibration discipline every V
        # lane before them carries, and at the EARLIEST point of it: AUTHORED
        # 2026-08-27 off `kerbin-return-recorded`'s harvested bytes and NEVER
        # FLOWN. Both are READING-RUN specs by construction - nothing armed, no
        # `gating = true` anywhere, no routing token in `required` - so what is
        # owed is the FIRST FLIGHT, not a human review call. NOT DEBT.
        # What they add beyond V19: the first KERBIN-ARRIVAL loop subject (planet
        # -> Kerbin, where V19 read moon -> parent), and with it the first loop
        # subject whose span is measured in Kerbin YEARS - 32,606,575.77 s,
        # 2,719x V19's - which is what makes their destination pin the most
        # falsifiable in the program: exactly ONE of twenty live instances is
        # Kerbin-framed at any observation epoch, and on the TS half it is the
        # OLDEST and therefore the last to spawn under the 2-per-tick throttle.
        # THE KSC THIRD IS DELIBERATELY NOT THEIRS: `V20K` over the same bytes is
        # where the KSC-host question becomes either a closed payoff or a cited
        # limitation, and under roadmap confirmation criterion (c) no limitation
        # may be written up before that run exists.
        "V20M-jool-kerbin-player-loop.toml": "tier=operator by the calibration discipline (derived windows, first run is a calibration reading), NOT debt; AUTHORED 2026-08-27 off `kerbin-return-recorded` and NOT YET FLOWN - the flight-map half of the suite's first KERBIN-ARRIVAL loop pair, reading-run posture with nothing armed; what is open is the FLIGHT itself, not a human review call",
        "V20T-jool-kerbin-ts-arrival.toml":  "tier=operator by the calibration discipline (derived windows, first run is a calibration reading), NOT debt; AUTHORED 2026-08-27 off `kerbin-return-recorded` and NOT YET FLOWN - the Tracking-Station half of the same pair, reading-run posture with nothing armed and the TS init-walk reading pre-registered in both directions; what is open is the FLIGHT itself, not a human review call",
        # W1: the GS-4 follow-up the ghost-derender lane deliberately did not carry
        # (`docs/dev/todo-and-known-bugs.md` -> GS4-WATCH-DISTANCE-CUTOFF). Same
        # posture as the V20 pair above: tier=operator by the calibration discipline,
        # not by debt - and READING RUN 1 (`2026-08-28_1902`) is exactly what that
        # discipline is for. It flew INVALID and refuted the spec's SUBJECT MAP (the
        # load-time optimizer splits the fixture's parent recording into three chain
        # members, so committed index 0 is the ASCENT segment alone and was inactive
        # at both probe epochs) while VALIDATING the derived loop clock to the
        # millisecond and the body-fixed separation model to 0.1%, and ANSWERING both
        # of the spec's pre-registered open questions (the distance field renders
        # digits; the body check above the guard is satisfied at spawn). Round 2
        # re-points the probes at the coast and descent members off that run's own
        # bytes; nothing is armed and no evaluator block is declared. What is open is
        # the next FLIGHT, not a human review call.
        "W1-watch-distance-cutoff.toml":     "tier=operator by the calibration discipline (derived geometry, the first runs are calibration readings), NOT debt; AUTHORED 2026-08-28 over V22M's `kerbin-splashdown-recorded`, READING RUN 1 flew INVALID and refuted the spec's SUBJECT MAP rather than the product, round 2 re-derived off that run's bytes and NOT YET FLOWN GREEN - the watch-entry 300 km cutoff as the single measured variable (REFUSED at 1,069.7 km on the coast chain member then ENTERED at 0.46 km on the descent member, both MEASURED), nothing armed; what is open is the FLIGHT itself, not a human review call",
        # THE G4 REPLICATION LANE, tier=operator by the same calibration
        # discipline the whole B18-B28 family carries: its windows are DERIVED
        # (from the fixture's own bytes, from cited stock constants and from
        # mlib's own formulas) rather than measured, and the first run is a
        # calibration reading. NOT debt.
        # IT HAS NOT FLOWN. That is the honest state and it is not a
        # classification problem: what is open is the FLIGHT, which a cloud
        # session cannot run, not a human review call. The lane exists to convert
        # the research doc's PREDICTION - that H3 is a property of the flight
        # profile rather than of the Jool system - into a measurement at a second
        # parent, and its five observation targets are pre-registered in the spec
        # header precisely so the post-flight paragraph cannot be written after
        # the fact.
        "B30-mun-minmus-transfer.toml":     "tier=operator by the calibration discipline (derived windows, first run is a calibration reading), NOT debt; AUTHORED 2026-08-23 and NOT YET FLOWN - the G4 replication of B26's moon-to-moon hop at a second parent (Mun -> Minmus under Kerbin), with five observation targets pre-registered in the header; what is open is the FLIGHT itself, not a human review call",
        # The V21 pair, tier=operator by the same calibration discipline and for
        # the same reason as every V lane before them: their windows are DERIVED
        # and the first run is a calibration reading. NOT debt.
        # NEITHER HAS FLOWN, and neither CAN from a cloud session. What is open
        # is the flight, not a human review call - and on this pair run 1 is
        # expected to be a CLOCK READ rather than a green: the seed weakness has
        # MOVED relative to V17's (whose routing was unknown), because the
        # routing shape is now predicted and what is unknown is B30's FLIGHT
        # DURATION - dominated by a stage-2 transfer-window wait uniform on
        # [0, 159,570.7 s] and seeded at its midpoint. A dwell-nowhere run 1 is
        # the accepted pre-registered outcome, exactly as V17M's was.
        "V21M-mun-minmus-player-loop.toml": "tier=operator by the calibration discipline (derived windows, first run is a calibration reading), NOT debt; AUTHORED 2026-08-23 ahead of its subject, then RE-PINNED 2026-08-24 off `mun-minmus-recorded`'s real bytes the day B30 flew it (run `2026-08-24_1536`, PASS attempt 1) - the ZERO placeholder tree id is replaced by 029afab30803454894b02be12567af81, the span envelope / both seam offsets / the segment-less tail are byte facts, and the PENDING_FIXTURE_LANES exemption is RETIRED (that map is empty again; its self-retirement cell fired red first). The eight jump UTs REMAIN calibration seeds because the anchor `A` is one, but their error term is now seconds rather than the ~100 Ms span miss the S1 seed carried. READING RUN FLOWN 2026-08-24 (`2026-08-24_1639`): PARSEK-FAIL(expectations) on EXACTLY ONE WORD - its shadow pin asked for `treatment=TracedPath` where this subject's hyperbolic Minmus approach (ecc 4.1888) renders `treatment=StockConic` in all 238 measured lines - with all 61 steps met, anomalySweep completely silent and every other verifier green. **H3 REPLICATED AT A SECOND PARENT** (`reaimed=False` x41, `support=UnsupportedCrossParent`, `not re-aim ...; faithful`) and the clock shape reproduced the logged relaunch UTs to the last digit, the only seed error being the 2.814 s anchor; the pin is corrected off the measured word and the eight UTs re-derived off the logged clock (each 2-3 s earlier). What is open is the ARMING PASS and this pair's two negative controls, not a human review call",
        "V21T-mun-minmus-ts-arrival.toml":  "tier=operator by the calibration discipline (derived windows, first run is a calibration reading), NOT debt; AUTHORED 2026-08-23 ahead of its subject, then RE-PINNED 2026-08-24 off the same real bytes (tree id 029afab30803454894b02be12567af81; the single jump moved to 587226, still V21M's third cycle-1 bracket reused so the pair observes the same instant from two scenes). The PENDING_FIXTURE_LANES exemption is RETIRED. The jump REMAINS a calibration seed because the anchor `A` is one, though the seam offset behind it (+267,230.864 s from ut0) is now a byte fact. READING RUN FLOWN AND GREEN 2026-08-24 (`2026-08-24_1642_a2`, PASS attempt 2, `flakedThenPassed`; attempt 1 INVALID on the transient TS-re-entry `LoadGame REJECTED` V17T's attempt 1 also hit, which is what `[retry]` is for): all 54 steps met, all ten required tokens matched, analyzer RED=0. THREE READINGS - `icon-off-orbit` SILENT (strengthening the surviving self-overlap candidate to 3-silent/6-raising), `seam-endpoint-outside-soi` RECURRED report-only against the pre-registered expectation but at a 1.91% hairline ratio rather than V16T's 157x, and the init walk measured `created 1 ghost vessel(s)` rather than zero, which makes that forbid armable by this lane's own stated rule. The jump moved 587226 -> 587223 with V21M's re-derived table, so this lane OWES A RE-FLY at the new value before arming. What is open is the arming pass, not a human review call",
        "H5-invariants-corpus.toml":        "discharged - 'resolving the former PENDING-OPERATOR check'",
        "H6-route-rewind-timeline.toml":    "discharged - 'The former PENDING-OPERATOR ...'",
        "M1-mission-loop-unit.toml":        "discharged - 'CLOSED by the 2026-07-26 flights'",
        "M2-periodicity-solver.toml":       "discharged - 'CLOSED by the 2026-07-26 flight'",
        # tier=operator because it is a READING RUN, which is the same reason the
        # calibration lanes are operator-tier and not a debt. AUTHORED 2026-08-29
        # (R4, the AUTOMERGE-ON-BY-DEFAULT wave), NEVER FLOWN. Its subject is a
        # QUESTION - which branch `AutoCommitPendingTreeOutsideFlight` takes when a
        # NON-Finalized (isActive-marker) pending tree reaches it outside FLIGHT -
        # so it deliberately pins only the structural preconditions and leaves the
        # two branch-discriminating tokens as documented readings. It owes no
        # outstanding HUMAN call: it owes a flight, after which the branch token is
        # pinned, `[expectations.recordings]` tightens from its honest {1,2} range,
        # and the saveParse blocks it correctly ships WITHOUT get authored from
        # measured facets. Nothing here is armed and ARMED_ALLOWLIST is untouched.
        "S0.9-automerge-pending-limbo-cold-load.toml":
            "operator by the reading-run discipline (V1/V2/V24W precedent); AUTHORED "
            "2026-08-29, NEVER FLOWN, reading pending. Owes a flight, not a human call",
        "S1.4-injected-playback.toml":      "discharged - 'THAT PENDING-OPERATOR IS NOW CLOSED'",
        # tier=operator by CALIBRATION DISCIPLINE, not debt - the B18/B19/B20 shape,
        # and the GS-1/GS-4 shape before promotion. S1.9 has never flown: its
        # `recordings.count` and `ghostLifecycle.spawned` numbers are MEASURED
        # LOCALLY off the injector (243 `.prec` sidecars, no ReStock+) but every
        # log token is DERIVED from the emitters rather than read off a flight,
        # and the one thing source cannot settle - whether the seam's probe chain
        # buys enough real frames for 243 ghost meshes to build - is exactly what
        # a reading run is for. Nothing operator-shaped is owed beyond flying it:
        # seam driver, no RequiresFlight verb, no human judgement in the loop.
        # Promotion off `operator` is the post-reading re-pin, taken in the same
        # commit that replaces the [DERIVED]/[INTERIM] labels with measurements -
        # the rule the L2/L3 notes below state.
        "S1.9-part-showcase-render.toml":   "calibration-discipline - READING RUN 1 FLOWN 2026-08-28 (`2026-08-28_1945`) and RED on a SPEC defect, not a product one: the lane never entered the corpus playback window, so spawned=0 and all 33 required tokens went unmatched. v2 re-cut the same day (window derived and pinned, TimeJump staircase added, the loop tokens and the D6 self-overlap claim CUT after the run refuted the loop analysis). The 26 mesh tokens and the apply tokens are still DERIVED - none of them could fire without a ghost - so run 2 is a READING run by construction; promotion waits on that re-pin, not on outstanding work",
        "S4.1-rewind-merge.toml":           "discharged - historical mention; tag dropped 2026-07-31",
        "L1-hire-kerbal-career.toml":       "discharged - drop-rationale prose; tag dropped 2026-07-31",
        "L1-dismiss-kerbal-career.toml":    "discharged - OPERATOR-VERIFIED; tag dropped 2026-07-31",
        "L1-research-node-career.toml":     "discharged - OPERATOR-VERIFIED; tag dropped 2026-07-31",
        "L1-research-node-science.toml":    "discharged - OPERATOR-VERIFIED; tag dropped 2026-07-31",
        "L1-upgrade-facility-career.toml":  "discharged - OPERATOR-VERIFIED; tag dropped 2026-07-31",
        # Found by this cell on its first run, absent from the hand-written list.
        "L1-passive-sandbox.toml":          "discharged - records its own 2026-07-26 drop",
        # NOTE, no entry owed: `L3-career-science-recover` held `operator` through four
        # flights and was PROMOTED to `nightly` on 2026-08-20, in the commit that pinned
        # its measurements - the rule the two NOTEs below state. Its entry here is
        # REMOVED rather than rewritten, on the same grounds: a nightly spec that never
        # mentions the token is not a candidate at all.
        #
        # This entry is worth a longer epitaph than the other two, because it is the one
        # case where the tier was held for something REAL rather than transient. The
        # predecessor entry read `product-finding-blocked`: flight 3 (run
        # `2026-08-19_1912`) flew MISSION-OK on the sibling `career-science-pad` fixture
        # and still classified `PARSEK-FAIL(expectation)` on a single forbidden
        # `[Parsek][ERROR]` line, behind which sat three deterministic product findings
        # this lane was built to reach - CAREER-RECOVERY-FUNDS-NOT-LEDGERED,
        # CAREER-SCIENCE-SEED-LOST-ON-FLIGHT-ROUTE and
        # CAREER-TRANSMIT-SCIENCE-EMITS-NO-CORROBORATING-EVENT. Re-flying could not green
        # it, which is exactly why the entry stated its own removal condition as "the
        # commit that closes those three findings and promotes this spec off `operator`"
        # rather than as a date. All three are fixed (PR #1498), a fourth - the recalc-side
        # CAREER-MILESTONE-REP-AWARD-RECONSTRUCTS-LOW - landed with the promotion, and
        # flight 4 (`2026-08-19_2130`) flew PASS on attempt 1 with zero `[Parsek][ERROR]`
        # lines and every verifier PASS or SKIPPED. The condition is met in full.
        #
        # Nothing operator-shaped was ever owed on this spec: autopilot driver, no
        # RequiresFlight verb, no human judgement in the loop. `operator` was only ever
        # the tier that keeps a spec off every cadence while it cannot pass.
        # NOTE, no entry owed: `L3-strategy-currency-conversion` held `operator` for
        # the length of ONE reading run - the same transient hold the L2 note below
        # describes - and was promoted to `nightly` in the commit that replaced its
        # predicted BATCH_COMPLETE pin with the measured one
        # (`2026-08-18_2039_L3-strategy-currency-conversion`, PASS attempt 1, 57 s,
        # total=3 passed=3 failed=0 skipped=0, zero GUARDED lines). It never owed
        # operator work: seam driver, no RequiresFlight verb, no human judgement in
        # the loop. Its entry is REMOVED rather than rewritten, because a nightly
        # spec that never mentions the token is not a candidate at all.
        # NOTE, no entry owed: `L2-ledger-groundtruth-career` was authored
        # `operator` for the length of one reading run - the tier that keeps a
        # PREDICTED BATCH_COMPLETE pin out of every cadence - and was promoted to
        # `nightly` the same day by the run that measured the pin
        # (`2026-08-17_2202_L2-ledger-groundtruth-career`). It never owed operator
        # work: seam driver, no RequiresFlight verb, 75 s wall, PASS attempt 1
        # unattended. Recorded here rather than left silent because the transient
        # operator tier is the pattern a future reading run will reuse, and the
        # rule it must follow is the one this comment states: promote off
        # `operator` in the same commit that pins the measurement, or the tier
        # becomes an unrecorded standing call.
        # ANSWERED by flight 7 (2026-07-26), per its own status row - the prose
        # that reads like a live debt is a PRE-FLIGHT risk note. Tagged here on
        # 2026-08-01 and reverted the same day; see the spec's comment.
        "B15-eve-flyby.toml":               "discharged - inward transfer ANSWERED by flight 7",
        # NOT this spec's own debt: it describes B1/B2's pad-craft fixtures.
        "S0.5-live-record-discard.toml":    "other-spec - B1/B2 fixtures, not S0.5's own",
        # tier=operator by MECHANISM, not debt: fixture-FORGE runs are manual by
        # nature ("NEVER runs on a cadence, only under an explicit invocation")
        # and their harvested fixtures are committed. Operator-tier alone is not
        # an operator debt - which is why the tier is recorded here rather than
        # assumed to imply the tag.
        "FORGE-bdock-station.toml":         "forge-mechanism - manual by design; fixture committed",
        "FORGE-eva2-lko.toml":              "forge-mechanism - manual by design; fixture committed",
        "FORGE-eva3-pad.toml":              "forge-mechanism - manual by design; fixture committed",
        # The fourth forge, same mechanism as the trio above: it stamps
        # gs1-two-stage-pad from the committed `GS1 Auto-Chute Booster.craft`.
        # Its fixture is NOT committed yet, which is the ONE way it differs from
        # its siblings - but that debt is carried by the `pending-flight` tag and
        # by GS-1's own STATUS block, not as an operator-REVIEW debt.
        "FORGE-gs1-two-stage.toml":         "forge-mechanism - manual by design; FLOWN 2026-08-05, fixture gs1-two-stage-pad committed + pinned",
        # tier=operator by PROMOTION POLICY, not debt. GS-1 is unflown and its
        # fixture is the one the forge above produces, so it cannot sit on a
        # cadence yet; promotion is a later human call after the report-only
        # reading run and the arming sequence its header specifies. Nothing is
        # outstanding beyond flying it, which is what `--tier operator` is for.
        "GS-1-auto-chute-booster.toml":     "FLOWN 4x 2026-08-05 (flight 4 PASS) and ARMED; operator tier is now an open PROMOTION call, not debt",
        # FLOWN GREEN 2026-08-27, same-day discipline: reading run
        # `2026-08-27_2145` (MISSION-OK attempt 1; red on exactly the two
        # watch tokens - the pre-spawn EnterWatchMode race, fixed as the WATCH
        # hold-then-retry loop) then green run `2026-08-27_2204` PASS attempt
        # 1, windows re-pinned to the measured census (spawned=8,
        # unbalanced=0, both flights). What remains open is the ghostLifecycle
        # ARMING pass (three-run discipline, GHOSTLIFE_ARMED_SPECS) and the
        # ordinary cadence PROMOTION call - the GS-1/GS-2/GS-3 shape exactly.
        "GS-4-kerbalx-rewind-watch.toml":   "FLOWN GREEN 2026-08-27 (2145 reading, 2204 green, both attempt 1); operator tier is now the arming + PROMOTION call, not debt",
        # The FIFTH forge, same mechanism again: it stamps gs2-orbital-stack by
        # flying the live-proven forge_lko ascent with the new parkAttached=true,
        # which skips the SEPARATE phase so the stack is parked ATTACHED. Its
        # fixture is not committed yet; that debt rides the `pending-flight` tag
        # and GS-2's STATUS block, not an operator-REVIEW debt.
        "FORGE-gs2-orbital-stack.toml":     "forge-mechanism - manual by design; FLOWN 2026-08-05 (PASS attempt 1), fixture gs2-orbital-stack committed + pinned",
        # The SIXTH forge, same mechanism again: it stamps b17-duna-pad by
        # launching the committed DD1 Duna Direct Probe (built by construction,
        # build_dd1_craft.py) onto the pad UNCREWED. Flown + harvested
        # 2026-08-06; the tier is the forge mechanism, not a review debt.
        "FORGE-b17-duna-pad.toml":          "forge-mechanism - manual by design; FLOWN 2026-08-06 (PASS attempt 1 after the UInt32 uid finding), fixture b17-duna-pad committed + registered",
        # The SIXTH forge, same mechanism argument as the five above: it launches
        # the committed stock `Duna Rocket` (KerbalX, Steltuck) onto the pad with
        # one named crew for the Dres program. Flown + harvested 2026-08-11.
        "FORGE-b18-dres-pad.toml":          "forge-mechanism - manual by design; FLOWN 2026-08-11 (PASS attempt 1, 105 s wall), fixture b18-dres-pad committed + registered",
        # The SEVENTH forge, same mechanism argument as the six above: it launches
        # the committed `Logi Cargo Rig` (built by construction,
        # build_logi_craft.py) onto the pad UNCREWED to stamp `logi-cargo-pad`, the
        # fixture the future isolated Logistics lane (H38) will consume. NOT FLOWN
        # yet, which is the one way it differs from its siblings - but that debt
        # rides the `pending-flight` tag and its own status row, exactly as
        # FORGE-gs1-two-stage's and FORGE-b17-duna-pad's did before they flew, and
        # it is a flight to run rather than an operator REVIEW debt.
        "FORGE-logi-pad.toml":              "forge-mechanism - manual by design (a forge is operator-invoked to MINT a fixture, never a tier member); FLOWN 2026-08-28, run `2026-08-28_1734` PASS attempt 1, which stamped the committed logi-cargo-pad save the H38/H41 isolated Logistics lanes consume",
        # B18 is operator BY THE CALIBRATION DISCIPLINE (the V1/V2 precedent), and
        # for a reason particular to it: it is the FIRST flight of a DOWNLOADED
        # human-built craft, so its recordings-count window and its debris
        # logContract token are deliberately unpinned on the first run and the
        # run's job is to measure them. Promotion is the post-measurement
        # re-pinning call, which is a recorded human decision and not a debt this
        # tag would name.
        "B18-dres-lko-ascent.toml":         "calibration-discipline - the first flight of an unmeasured downloaded craft is a READING run by construction (count window min=1/max=12 and the debris token are unpinned on purpose); promotion waits on the re-pin, not on outstanding work",
        # B19 is operator by the SAME calibration discipline as B16 (whose first
        # flight shape it copies) plus one reason of its own: it is the FIRST
        # flight of the new pre-transfer JETTISON phase, so its recordings-count
        # window stays unpinned until the jettison's debris topology has been
        # measured once. Promotion is that post-measurement re-pinning call.
        "B19-dres-orbit.toml":              "calibration-discipline - first flight of a new profile AND of the pre-transfer JETTISON phase; the recordings count window is unpinned pending the jettison debris topology, and promotion waits on that re-pin rather than on outstanding work",
        # B20 is operator for B19's FIRST reason but not its second: the jettison
        # debris topology is now MEASURED, so what is unpinned here is the Moho
        # ARRIVAL rather than the staging. Three of its numbers are DERIVED from
        # arithmetic rather than from a flight -- the approach ceiling (lowered
        # 5 -> 4 because Moho's SOI-entry -> periapsis coast is 2,168-4,119 game
        # s against Dres's measured ~25,000), the two correction triggers (scaled
        # to a ~2.7M game-second tof), and the wall budget the lower ceiling's
        # ~2,000 s approach traversal drives. Promotion is the post-flight re-pin
        # of those, plus the recordings count.
        "B20-moho-orbit.toml":              "calibration-discipline - first flight of a new destination whose approach sizing is DERIVED rather than measured (approachMaxWarpFactor lowered to 4 by Moho's short SOI coast, correction triggers scaled to the ~2.7M s tof, wall budget driven by the lower ceiling); the recordings count window is deliberately wide on the B19 first-flight precedent, and promotion waits on re-pinning those to measured values rather than on outstanding work",
        # B23 is operator by the SAME calibration discipline as B18/B19/B20, and
        # for a reason of its own: it is the FIRST flight of the shared B5
        # machine's SECOND entry door (`startInOrbit` -- start from an
        # already-parked fixture instead of a pad), and the first recording the
        # suite has ever produced whose LAUNCH BODY is not Kerbin. Nothing about
        # it is measured: the recordings window is a DERIVED range ({3,4}, the
        # fixture's carried-in pair plus this lane's product, with the fourth
        # admitting the B11 FIRST-FLIGHT-TO-CONFIRM post-commit-tree question),
        # the arrival periapsis MechJeb will actually deliver at Ike's scale is
        # unknown (finding 16d's under-delivery has only ever been measured at
        # the Mun), the park window is deliberately wide because of it, and the
        # save-structure topology of a produced save that carries BOTH the
        # fixture's committed tree and a new one has never been observed -- which
        # is why the structure block is absent rather than guessed. NOTHING is
        # armed. Promotion is the post-measurement re-pinning call, a recorded
        # human decision, not a debt this tag would name.
        "B23-ike-orbit.toml":               "calibration-discipline - LIVE-PROVEN 2026-08-18 on FLIGHT 2 (`_2308`, PASS attempt 1, wall 370.5 s): fresh standalone Duna-rooted tree, one Duna->Ike boundary, terminal Orbiting/Ike, produced save harvested as the committed `ike-orbit-recorded` fixture. FLIGHT 1 (`_2242`) was ALSO PASS attempt 1 on the same parameters and produced a structurally wrong subject (the seam StartRecording no-opped onto a re-resumed COMMITTED recording), which no verifier could see - fixed by the fixture, filed report-only. Nothing armed: the count window is still a derived range and no save-structure block is declared. Operator tier is now the ordinary promotion call plus the arming pass, not outstanding work",
        # THE B24/V15 GILLY TRIO, all three FLOWN on 2026-08-19 and all three operator
        # for the calibration discipline rather than for outstanding human work
        # (V1/V2/B18-B21/B23 precedent). B24 flew first; the V15 pair was calibrated off
        # its harvested bytes (the `PENDING_FIXTURE_LANES` exemption they needed while
        # their fixture did not exist is retired, that map is empty again) and both
        # lanes then flew their reading runs, were ARMED the same day off their own
        # bytes, and CLOSED THE FULL THREE-RUN DISCIPLINE the same evening (armed
        # re-flights `_1808` / `_1809`, one shared negative control `_1810`). Five
        # flights, one day. Nothing is owed on any of the three but the ordinary
        # operator -> nightly promotion call.
        "B24-gilly-orbit.toml":             "calibration-discipline - LIVE-PROVEN 2026-08-19 (`_1655`, PASS attempt 1, mission wall 1,075 s, every assertion met with NOT ONE PARAMETER MOVED): `startrecording ... already=false` minting the fresh standalone Eve-rooted tree, one `Eve to Gilly` boundary, terminal Orbiting/Gilly, a 27,024 x 26,321 m Gilly park at ecc 0.009, saveParse 1 recording / 520 points / all rewind facets 0. Its produced save is the committed `gilly-orbit-recorded` fixture, and BOTH consumers have since flown and armed off it (V15M `_1736`, V15T `_1739`), which is the strongest confirmation the produced subject is structurally right. Nothing armed on THIS lane: the count window stays a derived range and no save-structure block is declared, because the fixture's structure is pinned where it is CONSUMED. Operator tier is now the ordinary promotion call, not outstanding work",
        "V15M-gilly-player-loop.toml":      "calibration-discipline - READING RUN FLOWN GREEN 2026-08-19 (`_1736`, PASS attempt 1, all 21 steps met, all 8 TimeJumps OK, anomalySweep hits=[] hitCounts={}) and ARMED off its own bytes the same day: 12 required tokens incl. the MEASURED routing conjunction `method=single-orbital ... zeroDrift=no`, V14M's full six forbids, count pinned {1,1} and both save-structure blocks gating. THE DERIVED CALIBRATION HELD to 0.041 s against the product's own anchor and no jump UT moved. TWO MEASUREMENTS WORTH THE TIER: the cycle-1 EnterWatchMode is a GENUINE ENTRY at 775 m ghost separation - only the suite's second ever, after V7M's - while the cycle-2 step answered `already-watching` (idempotency + survival across a loop re-arm, NOT a second entry); and the seam-endpoint census fired ONCE because both lens summaries are VerboseRateLimited on a shared key and this lane's two brackets are ~1.4 wall-s apart, so the cycle-1-vs-cycle-2 comparison the lane was designed around is NOT readable from one run at this pacing (recorded as a measured limitation with three named recourses, no product change proposed). Report-only and deliberately unarmed: the NRE storm now filed as WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM, 447 on the reading run and 443 on the armed one - the same family as V7M's filed teardown NRE, which V7M also declines to ceiling. DISCIPLINE COMPLETE 2026-08-19: armed re-flight `_1808` PASS attempt 1 (all 12 required, all 6 forbidden, count {1,1}, both gated save-structure blocks PASS), negative control `_1810` correctly PARSEK-FAIL(save-structure) on the inverted `supersedeRows` window and reverted - the pair's single shared inversion. That control run also swept one INTERMITTENT `line-blink` (1-of-3, cycle-2 park, `director-traced-path-suppress` OFF edge - a case the window-exit exemption deliberately does not cover); the lane keeps `allowedAnomalies = []` and the raise is filed as the 14th archived one. Operator tier is now the ordinary promotion call, not outstanding work",
        "V15T-gilly-ts-arrival.toml":       "calibration-discipline - READING RUN FLOWN 2026-08-19 (`_1739`, PARSEK-FAIL(anomaly) attempt 1 with ALL 16 STEPS GREEN and the TS session clean - the CORRECT catch this spec pre-registered by shipping `allowedAnomalies = []` on purpose) and ARMED off its own bytes the same day: 13 required tokens incl. the measured routing conjunction, the `body=Gilly scene=TRACKSTATION` proto pin and `reaimed=False`, V14T's full six forbids, count {1,1}, both save-structure blocks gating, and the anomaly tolerated by the BARE token. WHAT THE RED BOUGHT: the single-jump creation-frame `icon-off-orbit` trigger is now MEASURED PARENT-INDEPENDENT (Duna/Ike 94.05 deg, Eve/Gilly 26.49 deg, deterministic at both, with V15M the stepped-bracket control at the same arrival UT) - the discriminating experiment the todo entry named, now answered and written up there. DISCIPLINE COMPLETE 2026-08-19: armed re-flight `_1809` PASS attempt 1 (all 13 required, all 6 forbidden, count {1,1}, both gated save-structure blocks PASS) with the tolerated anomaly RECURRING at `hits=[] counts={'icon-off-orbit': 1}` - the FOURTH sighting of the trigger, two per body pair, and this lane's first GREEN `hitCounts` baseline; negative control shared with V15M (`_1810`). THE CEILING `{ token = ..., maxCount = 1 }` IS NOW ONLY 'NOT YET TAKEN': the doctrine's precondition (measured hitCounts from a green run) is met on BOTH V14T and V15T, and the sole remaining blocker is the whole-set inert-budget invariant, which must move to a named allowlist in the same edit and should then cover both lanes at once. Operator tier is now the ordinary promotion call, not outstanding work",
        # THE B26/V17 LAYTHE->VALL TRIO, the THIRD two-stage program and the first
        # MOON-TO-MOON lane. All three are operator for the CALIBRATION DISCIPLINE.
        # THE BLOCKER IS GONE: flight 1's MechJeb moon-origin refusal was routed
        # around by the flag-gated PARENT-RELAY mode, flight 2 measured two defects in
        # it, and flight 3 flew the whole hop green on 2026-08-20. The V17 pair then
        # flew its reading runs, armed off its own bytes and CLOSED THE THREE-RUN
        # DISCIPLINE the same day. What is owed is the ordinary operator -> nightly
        # promotion call, NOT outstanding human review work, which is what this list
        # records.
        "B26-laythe-vall-transfer.toml":    "calibration-discipline - LIVE-PROVEN 2026-08-20 ON FLIGHT 3 (`_1752`, PASS attempt 1, mission wall 1,408 s, the full twenty-phase chain through ORBIT-COMMITTED with all eight assertions met) VIA THE FLAG-GATED PARENT-RELAY MODE built to route around flight 1's MechJeb moon-origin refusal (docs/dev/todo-and-known-bugs.md -> MECHJEB-INTERPLANETARY-PLANNER-REJECTS-MOON-ORIGIN; its `PENDING_FIXTURE_LANES` exemption is retired and that map is empty again). A READING lane and THE FIRST MOON-TO-MOON TRANSFER: a Laythe park -> Jool frame -> Vall, committed at Vall, producing the M-MIS-7 subject that lets V17M/V17T measure what a CROSS-PARENT recording does at `MissionConfig loop=true`. The machine side is a COMPOSITION of two live-proven blocks that have never met - the interplanetary path (eight lanes, always Kerbin-from-the-Sun) with JOOL as the transfer frame, entered through the orbit-start door (three lanes, always on the moon path) - audited end to end before authoring: zero Sun-frame assumptions on the live path (the heliocentric ephemeris is quarantined behind `padAlignEjection`, three call sites, all flag-gated; the phase angle is entirely MechJeb's), `ejectionEccFloor` reads the Laythe frame correctly, `_b5_correction_via_bodies` narrows to an identity, the warp table already carries all three bodies, and `startInOrbit` composes with `interplanetaryTransfer` with no coupling. VERDICT CLEAN, VALUES ONLY - and flight 1 CONFIRMED that verdict by failing somewhere else entirely. Three value consequences the audit named are handled at their own keys and one would have cost a flight: `ejectionEccFloor` had to drop to 1.001 because the required Laythe ejection hyperbola has ecc 1.0352 and B7's 1.05 WOULD NEVER BE REACHED - and flight 2 then RETIRED the key to 0 outright on this lane, because a correctly-sized PATCHED-CONIC escape from Laythe is BOUND (ecc 0.7586), so stage-1 evidence is `_relay_escape_burn_done`'s SOI-reach disjunct rather than any eccentricity floor. FLIGHT 1 (`2026-08-19_2214` / `_2215_a2`): BOTH attempts INVALID(autopilot-flake), deterministic, refused in PLAN-TRANSFER six wall-seconds in on `OrbitExtensions.NextTimeOfRadius: given radius of 3723645.81113302 is never achieved` out of `KRPC.MechJeb.Maneuver.Operation.MakeNodes` - MechJeb idealises a moon-parked origin to a circle at the park's mean radius and builds a SUB-ESCAPE ejection whose apoapsis lands 2.443% short of the SOI. NOT mlib, NOT this spec, NOT Parsek: the ORBIT-START door, the fixture and target acquisition all behaved (`reachedOrbit met=True`, `startedInHomeOrbit value=0.028 met=True`, `tgtD` snapped to Vall's 59,303,828 m). FLIGHT 2 (`_1646` / `_1701_a2`) flew the relay - both attempts INVALID, and most of it worked - and measured the two defects that fixed it: the escape CONTRACT (`escapeSoiSpeedMps` = the speed delivered AT THE SOI BOUNDARY, replacing the retired `escapeTargetVInfMps` which asked for an excess at infinity and delivered 3.12x it) and a coast-warp thrash on an AMBIGUOUS SOI clock. FLIGHT 3 (`_1752`) then came back green with all four pre-registered observation targets where the fixes predicted, and its produced save is the committed `vall-transfer-recorded` fixture BOTH V17 lanes have since flown and armed off - the strongest confirmation the subject is structurally right. STILL A READING LANE and nothing armed on B26 itself: count window {1,4} - deliberately wider than every predecessor because this recording has TWO body-change seams and the optimizer's answer at an ESCAPE boundary had never been measured - and it came back 1, the BOTTOM of the range, with both boundaries kept cohesive; no save-structure block, no `gating = true`, because the fixture's structure is pinned where it is CONSUMED (the B24/B25 disposition). Operator tier is now the ordinary promotion call, not outstanding human work",
        "V17M-laythe-vall-player-loop.toml": "calibration-discipline - RE-PINNED 2026-08-20 OFF ITS FIXTURE'S REAL BYTES, then FLOWN, ARMED and DISCIPLINE-COMPLETE the same day: green reading run `2026-08-20_1915` (PASS attempt 1, the H3-clock re-pin round; facets 0/0/0/0 and 1/1/1, points 746/746/746), ARMED off its own bytes (`rewind` all max 0 plus `structure` with recordings pinned {1,1} - the sharp form of the reading-era {1,3} window), armed re-flight `_1934` PASS attempt 1, and ONE negative control shared with V17T (`_1941`, red EXACTLY on `rewind.supersedeRows 0 < min 1` and nowhere else, then reverted). See ARMED_ALLOWLIST in this file. `vall-transfer-recorded` EXISTS, produced by B26 flight 3 (run 2026-08-20_1752, PASS attempt 1) - the suite's first completed moon-to-moon transfer, after flight 1 hit a MechJeb moon-origin refusal and flight 2 measured two defects in the parent-relay mode built for it. The `PENDING_FIXTURE_LANES` exemption is retired (that map is empty again; its self-retirement cell fired red first, as designed). Operator by construction, and with a claim to derived seeds that turned out STRONGER than the pre-flight worry: the anchor formula that served V14M/V15M/V16M is a phase-lock artifact and this subject's routing IS the measurement, so H1 (re-aim, synodic-spaced) and H2 (the Tier-1 NextWindow snap) are anchored by DIFFERENT PLANNERS - but at this pair the 1:2:4 resonance puts P_Vall and the Laythe-Vall synodic 0.6615 s apart, so the two candidate jump tables are IDENTICAL on cycle 1 and differ by 1 s on cycle 2, far inside the -180/-60/+140 bracket. The table is authored under H1 with the H2 re-pin table printed beside it. Both outcomes stay pre-registered with the exact discriminating lines and the lane gates on neither - the re-aim trio is unrequired AND unforbidden, the exact inversion of its three predecessors. It also carries V16M's now-PROVEN forty-tick census-pacing block, with the new question of what the lens does with a recording that has TWO cross-body seams. Operator tier is now the ordinary promotion call, not outstanding human work",
        "V17T-laythe-vall-ts-arrival.toml": "calibration-discipline - RE-PINNED 2026-08-20 off the same real bytes (one jump UT reused from V17M so the pair observes the same instant from the two scenes), then FLOWN, ARMED and DISCIPLINE-COMPLETE the same day: green reading run `2026-08-20_1933` (PASS attempt 1, the dynamic-overlap-path re-pin round: all 20 cycles spawned, 9 Vall-frame TS lines), ARMED off its own bytes with a byte-identical gating saveParse payload to V17M's - the pair's determinism statement - armed re-flight `_1939_a2` (PASS; attempt 1 INVALID on a transient TS-re-entry LoadGame REJECTED, the retry's job), negative control shared with V17M (`_1941`). See ARMED_ALLOWLIST in this file. Its fixture `vall-transfer-recorded` EXISTS, produced by B26 flight 3 (run 2026-08-20_1752); the `PENDING_FIXTURE_LANES` exemption is retired. Operator by construction. It is the FIRST TS loop subject since V5 that might legitimately render a RE-AIMED chain, so `factory chain ... reaimed=True|False` - V6T's sharpest line and V16T's armed pin - is deliberately left unrequired AND unforbidden here, and the TS conic is written out as arithmetic but NOT pinned, because a re-aimed chain does not render the recorded conic at all and predicting it would assume the answer. It additionally ships `allowedAnomalies = []` ON PURPOSE and is EXPECTED TO RED on `icon-off-orbit` - the V15T/V16T pattern, now deterministic across six runs at three parents - with a second, sharper prediction pre-registered and expected to come back NEGATIVE: V16T's `seam-endpoint-outside-soi` raise scales with the cadence multiple, and this lane's cadence is a synodic rather than 20 moon periods. Operator tier is now the ordinary promotion call, not outstanding human work",
        # THE G3a SURFACE-ENDPOINT LANES (V22 x3, V23 x2), committed 2026-08-21
        # AHEAD OF THEIR FIXTURES under the re-armed `PENDING_FIXTURE_LANES`
        # exemption. Operator by the SAME calibration discipline as every V lane
        # before them - a first flight against an unmeasured subject is a READING
        # run by construction - plus one reason specific to this gap: their
        # subjects will be the FIRST landed/splashed loop recordings the suite
        # has ever held, so the arrival lens itself is unmeasured. Every
        # committed loop lane to date ends at an ORBIT, and below atmosphere
        # there is no conic, so these lanes pin the OWNED DESCENT POLYLINE and
        # the SUPPRESSED-ICON marker fallback rather than a proto orbit line.
        # NOTHING IS ARMED: no `gating = true` on any block, zero-placeholder
        # tree ids, calibration-seed jump UTs. Promotion is the post-harvest
        # re-pin plus the arming pass, not outstanding human work.
        "V22M-kerbin-splashdown-player-loop.toml": "calibration-discipline - RE-PINNED 2026-08-24 OFF ITS FIXTURE'S REAL BYTES. `kerbin-splashdown-recorded` EXISTS, harvested `--keep-parsek` from `B4-reentry-splashdown` run 2026-08-24_1431 (PASS attempt 1, wall 1,065 s, mission wall 989.2 s), and the `PENDING_FIXTURE_LANES` exemption is retired (that map is empty again; its self-retirement cell fired red first, as designed). Real tree id c05c834c...; the eight jump UTs were then **RE-DERIVED IN ROUND 2 OFF THE MEASURED LOOP CLOCK** after READING RUN 1 (`2026-08-24_1616`, retry `_1618`) red INVALID(driver-rewind) on `timejump refused reason=backward-jump` at all eight - the pre-registered calibration catch, no render pin reached, lens still unmeasured. Round 2 pins L1 = 21,583.965183089826 / L2 = 43,133.39036617965 off `startloopplayback initiated: relaunchUt=`, arrivals at relaunch + the 1,240.1436 s touchdown offset. Run 1 also measured two mechanics no byte table gives: the loop unit's span is the TRACK_SECTION ENVELOPE (1,241.8836, 1.740 s past `explicitEndUT`) and `StartLoopPlayback` issues its OWN forward jump to relaunch-15 s. The settle stays +300 s on a NEW argument (the replay is only 1,241.88 s, so +1,200 would dwell past any plausible ghost lifetime), not round 1's backward-jump one. TWO FACETS THE HARVEST SETTLED: the terminal measured `Landed`, NOT `Splashed` (a dry-land shoreline touchdown east of KSC) - the one facet this spec deliberately left undeclared until the bytes existed, now declared report-only - and the committed tree carries SEVEN recordings rather than the predicted 8-9 (the commit-blind count does read 9; the difference is two uncommitted single-POINT sidecar stubs). THE FIRST LOOP SUBJECT IN THE SUITE THAT DOES NOT END AT AN ORBIT, and the flight-map half of roadmap gap G3a. **ROUTING WAS ITSELF A READING AND THE READING IS IN: THE PRE-REGISTERED `FAITHFUL FIXED CADENCE` HYPOTHESIS IS REFUTED.** A launch-body-only loop does NOT starve both roads - it starves the TARGET side only, while the LAUNCH SITE supplies `constraints=1 [Rotation(Kerbin) P=21549.425183089825 off=0]`, giving `method=single-rotation` with `fixedCadenceResidual=0` / `fixedCadenceWithinTol=yes` / `zeroDrift=no`: the cadence quantizes to Kerbin's ROTATION period so every replay leaves the pad at the same position under the sky. NO GATE FIRED ON THE REFUTATION, because both roads were left unrequired AND unforbidden - V17M's reasoning vindicated on the first subject able to test it - and they stay that way for run 2. The historical pre-registration is kept in the spec, marked as refuted. TWO shape dimensions move at once - surface endpoint AND, for the first time, a multi-recording debris tree - which the induction caveat asks to be PRE-REGISTERED rather than avoided, because no single-recording landed subject exists to avoid it with. WHY THE LENS MOVES: a `surface=Proto(Icon|OrbitLine)` arrival pin would not be unsatisfiable here but VACUOUS, since the harness matches the WHOLE log and the same loop's coast phase satisfies it - so the arrival claim rests on tokens only the below-atmosphere state emits. Ships `allowedAnomalies = []` on purpose and EXPECTS to read the gated Tier-C `rigid-seam-tangent-discontinuity` raise at the owned descent draw - run 1 never reached it. Promotion is the round-2 reading run plus the arming pass",
        "V22T-kerbin-splashdown-ts-arrival.toml":  "calibration-discipline - RE-PINNED 2026-08-24 off the same real bytes (`PENDING_FIXTURE_LANES` retired in the same commit), the Tracking-Station half of the Kerbin surface-arrival pair over the same `kerbin-splashdown-recorded` subject, observing the SAME instant from the TS host - its single jump UT is **RE-DERIVED IN ROUND 2 TO 22,964** (reused verbatim from V22M's third cycle-1 bracket) after READING RUN 1 (`2026-08-24_1619`, retry `_1620`) red INVALID(driver-rewind) on `timejump refused reason=backward-jump` - the spec's own forbid working, no TS render pin reached, the lens unmeasured. It is the measured L1 = 21,583.965183089826 plus the 1,240.1436 s touchdown offset plus +140. **THE ROUTING PRE-REGISTRATION THIS LANE INHERITED IS REFUTED**: `constraints=1 [Rotation(Kerbin) off=0]`, `method=single-rotation` - the launch SITE, not the target, supplies the constraint - and no gate fired, both roads being unrequired AND unforbidden. Inherits the harvest's two measurements too: terminal `Landed` (declared report-only) and a SEVEN-recording tree. Carries the forty-tick TS dwell V17T's run-1 reading proved necessary: the init walk skips a segment-less tail and the real ghosts arrive on the DYNAMIC overlap path at `MaxSpawnsPerFrame = 2` newest-cycle-first, so the arrival-leg instance spawns LAST and a short phase cuts it off. Same lens rationale as V22M. Nothing armed. Promotion is the post-harvest re-pin plus the arming pass",
        "V22K-kerbin-splashdown-ksc-arrival.toml": "calibration-discipline - RE-PINNED 2026-08-24 off the same real bytes (`PENDING_FIXTURE_LANES` retired in the same commit; real tree id, jump UT **RE-DERIVED IN ROUND 2 TO 22,964** off the measured clock, reused from V22M). READING RUN 1 (`2026-08-24_1621`, retry `_1622`) red INVALID(driver-rewind) on `timejump refused reason=backward-jump` - the spec's own forbid working - and **LEFT THE FIRST-CONTACT QUESTION EXACTLY WHERE IT WAS**: the run never reached SPACECENTER's render path, so NO KSC RENDER LINE HAS STILL EVER BEEN READ BY ANY LANE and every pin here remains source-derived. It did measure the clock (L1 = 21,583.965183089826) and the routing - `constraints=1 [Rotation(Kerbin) off=0]`, `method=single-rotation`, which REFUTES the inherited faithful hypothesis and, being the LAUNCH-SITE constraint, means every replay leaves the pad at the same position under the sky - the most favourable clock this host could have drawn. No gate fired on it, both roads being unrequired AND unforbidden. The harvest separately CONFIRMED the gate reading this lane rests on: the subject is Kerbin-frame END TO END - `startBodyName = Kerbin`, four ORBIT_SEGMENTs all Kerbin, zero body-change seams, terminal on Kerbin - so both Kerbin gates admit it and the two forbidden skip lines are forbidden against a subject that cannot produce them. Terminal measured `Landed` and declared report-only. STILL THE FIRST V LANE EVER TO USE THE KSC RENDER HOST, which the program's definition of done requires wherever the Kerbin gate makes it non-vacuous. Its plumbing did not wait on G2 and is not new: R12's `LoadGame scene=spacecenter` (`TestCommandLoadGame.RequestedBootScene.SpaceCenter`), used by exactly one committed spec before this (`H34-logistics-inter-body.toml:84`), with `ParsekKSC.cs:282` driving the same `DriveMissionLoopUnits` seam as the other two hosts. `ParsekKSC` HARD-GATES to Kerbin-frame points (skip reason `non-kerbin`, `ParsekKSC.Playback.cs:292/366`), which is exactly why a Kerbin-frame SURFACE subject is the first subject that makes this host non-vacuous. AND THE OBVIOUS PIN IS THE WRONG ONE: `KSC SURFACE playback resolved` is emitted from TWO sites with different field sets under ONE rate-limit key, the interpolation variant (which dominates a dwell) carrying no `body=` at all - so the lane pins the body-agnostic form and forbids the two skip lines instead, losing nothing because the Kerbin gate is UPSTREAM of both emits. Filed as KSC-SURFACE-RESOLVED-TWO-EMITTERS-SHARE-ONE-RATE-LIMIT-KEY, report-only. Nothing armed. Promotion is the post-harvest re-pin plus the arming pass",
        "V23M-mun-landing-player-loop.toml":       "calibration-discipline - RE-PINNED 2026-08-24 OFF ITS FIXTURE'S REAL BYTES (`PENDING_FIXTURE_LANES` retired in the same commit). `mun-landing-recorded` EXISTS, harvested `--keep-parsek` from `B13-mun-landing` run 2026-08-24_1449 - PASS attempt 1, wall 2,811 s / mission wall 2,764.1 s, the suite's most expensive scenario, MISSION-OK across all twenty-one phases through SURFACE-COMMITTED. Real tree id 0da22482...; measured UT0 34.520 / span 23,222.425 / ONE Kerbin->Mun seam at 16,425.169 (offset 16,390.649, against V6M's 16,370.562 on the orbit-only sibling) / 8 recordings / a 2,290.5 s segment-less descent-and-landed tail. **THE JUMP TABLE WAS RE-DERIVED IN ROUND 2 OFF THE MEASURED PHASE-LOCK CLOCK** after READING RUN 1 (`2026-08-24_1623`, retry `_1624`) red INVALID(driver-rewind) on `timejump refused reason=backward-jump` at all eight - the pre-registered catch, no render pin reached, lens unmeasured. Round 2 pins L1 = 1,249,901.1806192098 / L2 = 1,530,043.7079993775 off `startloopplayback initiated: relaunchUt=`, arrivals at relaunch + a MEASURED 23,091.405 s touchdown offset. **AND RUN 1 PRODUCED THE A/B THIS PAIR EXISTS FOR**: against V6M's committed line, moving the endpoint orbit -> surface left `method=joint-best-fit`, `P=1250859.3891702818`, cadence 280,142.527, `fixedCadenceResidual=992.72855107198848`, `fixedCadenceWithinTol=no`, `zeroDrift=yes` IDENTICAL TO THE DIGIT and changed exactly two fields - `firstLaunch` 280,176.947 -> 1,249,901.181 and `scheduleWorstResidual` 4,347.548 -> 1,797.140 (better) - caused by a THIRD constraint V6M lacks, `Rotation(Mun) off=22937.185293623374`, which a recording only acquires by ENDING ON A SURFACE and which (the Mun being tidally locked) shares `Orbital(Mun)`'s period exactly, contributing a phase offset and no new period. Round 1's derived CADENCE was right to the digit and its derived ANCHOR wrong by 970 ks: a formula that reproduces a cadence does not thereby reproduce an anchor. The same line CONFIRMS `Orbital(Mun) same-parent ... off=16390.648888550266` against the ORBIT_SEGMENT chain read, to nine decimals. Also worth one line: the tree's first branch point (UT 34.52, debrisCount 3) carries no childId rows at all. B13 was already LIVE-PROVEN full PASS on flight 1 (2026-07-25). THE MISSION LIBRARY ALREADY LANDS - `landingEnabled` is a flag-gated, inert-by-default phase driving MechJeb `LandingAutopilot.LandUntargeted` through `mission_runner._perform_land_untargeted` against the installed darchambault KRPC.MechJeb v0.8.1 pin - so this lane costs a re-fly and a harvest, not a new mission mode. Kerbin -> its own moon is the PHASE-LOCK road measured five times over and V6M/V6T are Kerbin -> Mun exactly, so this lane is V6 WITH THE ENDPOINT MOVED FROM ORBIT TO SURFACE: the clean single-dimension extension the induction caveat asks for, and its own A/B control. What it must NOT reuse from V6M is that lane's `surface=Proto(Icon|OrbitLine) .*body=Mun` arrival pin, which the coast phase of the same loop would satisfy. THE MUN IS AIRLESS, so the icon-suppression reason token is `polyline-owns-phase` with `belowAtmosphere=False` - `below-atmosphere` never fires there and pinning it would be structurally unsatisfiable. Nothing armed. Promotion is the post-harvest re-pin plus the arming pass",
        "V23T-mun-landing-ts-arrival.toml":        "calibration-discipline - RE-PINNED 2026-08-24 off the same real bytes (`PENDING_FIXTURE_LANES` retired in the same commit), the Tracking-Station half of the Mun landing pair over the same `mun-landing-recorded` subject - real tree id, one jump UT **RE-DERIVED IN ROUND 2 TO 1,273,133** (reused verbatim from V23M's third cycle-1 bracket) after READING RUN 1 (`2026-08-24_1626`, retry `_1627`) red INVALID(driver-rewind) on `timejump refused reason=backward-jump` - the spec's own forbid working, no TS render pin reached, so the lens and the V6T A/B on what the TS scene assembles are both entirely unmeasured. It is the measured L1 = 1,249,901.1806192098 plus a measured 23,091.405 s touchdown offset plus +140. **THE ROUTING CAME BACK CONFIRMED HERE, unlike the V22 trio's refuted faithful pre-registration**: `method=joint-best-fit` over `constraints=3` including `Orbital(Mun) same-parent off=16390.648888550266`, `zeroDrift=yes`, `scheduleWithinTol=yes`, no `[ReaimDiag]` - which is why requiring a road was defensible on this pair and would not have been on that trio. NO KSC HALF EXISTS FOR THIS SUBJECT and that is a scoping fact rather than an omission: `ParsekKSC` hard-gates to Kerbin-frame points, so the KSC host is VACUOUS for a Mun recording - the asymmetry with V22K is the gate working. Carries the forty-tick TS dwell for V17T's spawn-throttle reason. Same airless lens rationale as V23M. Nothing armed. Promotion is the post-harvest re-pin plus the arming pass",
        # THE B25/V16 LAYTHE TRIO, the SECOND two-stage program to use the
        # `PENDING_FIXTURE_LANES` exemption and the first Jool-moon lane. All three are
        # operator for the CALIBRATION DISCIPLINE. B25 has now FLOWN (flight 1 INVALID on
        # a park window written before anyone had measured a finite-burn periapsis drop,
        # flight 2 PASS attempt 1 once it was resized); the V16 pair has now flown its
        # reading runs, ARMED off their own bytes, re-flown green under the arming and
        # taken a shared negative control, so the CALIBRATION DISCIPLINE IS COMPLETE on
        # all three. Nothing is owed on any of them but the ordinary operator -> nightly
        # promotion call, which is what this list records.
        "B25-laythe-orbit.toml":            "calibration-discipline - LIVE-PROVEN 2026-08-19 ON FLIGHT 2 (`_2039`, PASS attempt 1, mission wall 741.6 s, full chain through ORBIT-COMMITTED): an 87,931 x 56,240 m Laythe park at ecc 0.0277, terminal Orbiting/Laythe, all five required tokens and ALL NINE forbidden (the ERROR floor plus eight Vall/Tylo/Bop/Pol named-poison forms) met, zero `Atmospheric`, and `already=false` minting the standalone Jool-rooted tree. The suite's FIRST INWARD TRANSFER, and all three of its values-only workarounds held on both flights. FLIGHT 1 (`_1948` / `_2001`) was INVALID(driver-flake) on both attempts and the flake was the WINDOW, not the flight: a healthy park 4,911 m under a floor written before anyone had measured that a 163.5 s capture burn at 5.40 m/s^2 drops the periapsis ~15.4 km (now 15,382 / 15,385 / 15,415 m over three flights, a 33 m spread). Fixed by resizing `parkMinPeriapsisMeters` 60,000 -> 52,000 and nothing else - the B17 precedent - which flight 2 vindicated. Its produced save is the committed `laythe-orbit-recorded` fixture. Nothing armed: the count window stays a derived range and no save-structure block is declared, because the fixture's structure is pinned where it is CONSUMED (the B24 disposition). Operator tier is now the ordinary promotion call, not outstanding work",
        "V16M-laythe-player-loop.toml":     "calibration-discipline - CALIBRATED 2026-08-19 off the harvested `laythe-orbit-recorded` bytes, then FLOWN, ARMED and DISCIPLINE-COMPLETE the same day. Operator additionally because BOTH of its claims were pre-registered and BOTH came back MEASURED: the suite's FIRST k > 1 CADENCE - the recording's span/P = 19.435357 gives k = 20, so cycle 2 lands TWENTY moon orbits after cycle 1 rather than one, with k = 21 excluded outright across the transfer-window-wait band - and the CENSUS-PACING UPGRADE that is supposed to make it readable: forty `RecordingState` dwell ticks spending >= 10 wall s at run.py's 0.25 s poll floor, implementing research section 9.3's RECOURSE 1 verbatim against the limiter that swallowed V15M's cycle-2 census on all three of its runs - and the upgrade WORKED, retiring research section 9.3's 'unmeasurable at seam pacing' limit for lanes carrying the block. Both watch steps are pinned REJECTED on arithmetic that, unlike V15M's, is PARK-INDEPENDENT (2a >= 1,120,000 m against a ~120 km boundary), which also predicted the WATCH-LOOPED-PARK-TARGET-LOSS-NRE-STORM population ABSENT. DISCIPLINE COMPLETE 2026-08-19: reading run `_2114` (PASS attempt 1), ARMED off its own bytes (12 required incl. the strict census pin `seam-endpoint summary evaluated=[1-9]\\d* outsideSoi=0`, 6 forbidden, count {1,1}, both save-structure blocks gating), armed re-flight `_2211` PASS attempt 1 and COMPLETELY SILENT (`hitCounts={} hits=[] unlistedReasons=[]`, so the strict pin held), negative control `_2213` correctly PARSEK-FAIL(save-structure) on `rewind.supersedeRows 0 < min 1` with every other verifier PASS, then reverted - the pair's single shared inversion. Operator tier is now the ordinary promotion call, not outstanding work",
        "V16T-laythe-ts-arrival.toml":      "calibration-discipline - CALIBRATED 2026-08-19 off the same harvested bytes (one jump UT reused from V16M so the pair observes the same instant from the two scenes), then FLOWN, ARMED and DISCIPLINE-COMPLETE the same day. Operator by construction, and it shipped `allowedAnomalies = []` ON PURPOSE and DID RED on `icon-off-orbit` with every step green - the V15T pattern verbatim, and the PRE-REGISTERED correct catch it was written to be rather than a debt. Parent-independence of that trigger is already MEASURED (Duna/Ike and Eve/Gilly, deterministic at both), so what a third body pair adds is the widest lever anyone has on the MAGNITUDE question the todo entry records as NOT constant (26.49 deg at Gilly vs 94.05 at Ike): Laythe's SOI is 29.5x Gilly's, so three points across that range is where a correlation with SOI scale would first be visible. Its pre-flight TS-conic prediction has ALREADY been scored by B25 (predicted |a| 1.29 Mm / e 1.582, measured 2.11 Mm / 1.271 - both within a factor of 1.7, against V15T's three-orders-of-magnitude miss). DISCIPLINE COMPLETE 2026-08-19: reading run `_2115` (PARSEK-FAIL(anomaly) attempt 1, all sixteen steps green - the correct catch, and it read 129.15 deg, the FIRST count > 1 sighting and a SECOND lens), ARMED off its own bytes (13 required, 6 forbidden, count {1,1}, both blocks gating, the anomaly tolerated by the BARE token and the census pin left at the PRESENCE form because the strict value form would red on the known artifact), armed re-flight `_2212` PASS attempt 1 with BOTH lenses RECURRING exactly as predicted (`allowed=['icon-off-orbit'] hitCounts={'icon-off-orbit': 2} hits=[] unlistedReasons=['seam-endpoint-outside-soi']`), negative control shared with V16M (`_2213`). NEITHER RECURRENCE IS DRIFT: the seam lens is the creation-frame instrument artifact, and V16M's stepped censuses read `outsideSoi=0` on both cycles with its armed run silent, so the true k = 20 recurrence is clean. Operator tier is now the ordinary promotion call, not outstanding work",
        # THE V14 PAIR, and they are operator for the same calibration discipline for a
        # reason that is theirs alone: they are the FIRST loop lanes whose subject is not
        # rooted at Kerbin (B23's Duna->Ike recording), and the first to reach a TIDALLY
        # LOCKED constraint pair - Rotation(Duna) 65,517.859375 s against Orbital(Ike)
        # 65,517.862350 s, |dP| 0.002975 s inside a 0.065518 s equality band. That predicts a
        # routing outcome (`method=tidal-collapse`, `zeroDrift=no`, a uniform one-period
        # cadence) NO existing lane has measured, so every bracket UT in both specs is a
        # derived CALIBRATION SEED rather than a pin, and both ship with nothing armed and
        # only the ERROR floor forbidden. Promotion is the post-reading arming call.
        "V14M-ike-player-loop.toml":         "calibration-discipline - READING RUN FLOWN GREEN 2026-08-18 (`_2336`, PASS attempt 1, wall 50 s, anomalySweep hits=[], all 8 TimeJumps OK, both watch attempts REJECTED as pinned) and ARMED off its own bytes the same day: 12 required tokens incl. the measured routing conjunction, V6M's full six forbids, count pinned {1,1} and both save-structure blocks gating. THE PREDICTION IT WAS BUILT ON WAS REFUTED and that is its value: `ExtractConstraints` emits `constraints=1` with NO Rotation(Duna), because an ORBIT-ROOTED recording has no surface phase, so `method=single-orbital` rather than the predicted tidal-collapse - phase-lock for a single-moon orbit-rooted subject is EXACT (residual 0, cadence = one moon period, no schedule) rather than within-tolerance. The derived anchor still held to 0.04 s and no jump UT moved. Operator tier is now the armed re-flight + the ordinary promotion call, not outstanding work",
        "V14T-ike-ts-arrival.toml":          "calibration-discipline - READING RUN FLOWN 2026-08-18 (`_2337`, PARSEK-FAIL(anomaly) attempt 1 with ALL 16 STEPS GREEN - a CORRECT catch, not a lane defect: the armed Tier-C sweep found ONE `icon-off-orbit` on a ghost-proto creation frame in FLIGHT, a second before the TS load) and ARMED off its own bytes the same day: 13 required tokens incl. the `body=Ike scene=TRACKSTATION` proto pin and `reaimed=False`, V6T's full six forbids, count {1,1}, both save-structure blocks gating, and the anomaly tolerated by the BARE token plus a filed report-only entry (MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP). THE TOLERANCE HAS NO CEILING: `{ token = ..., maxCount = 1 }` was written first and rejected by this file's own `test_no_committed_spec_arms_a_count_budget`, which holds the budget mechanism INERT across the suite; per that todo entry the arming is now READY (the armed run supplied the green-run `hitCounts` baseline the doctrine asks for) but not taken, so a SECOND raise in one run currently passes unnoticed - the measured population is 1 on each of two runs, and that gap is the whole exposure. V14M is the control: same fixture, same tracers, same arrival UT, stepped bracket instead of one 17,223-s jump, hits=[] on both its runs. FLOWN GREEN ARMED 2026-08-19 (`_0002`, PASS attempt 1, the anomaly recurring and tolerated); negative control shared with V14M (`_0003`). Operator tier is now the ordinary promotion call, not outstanding work",
        # V11 is a pure READING RUN in V9's original posture: nothing armed
        # beyond the plumbing triple, count window deliberately wide, and the
        # decline reasons V9 forbids left UNFORBIDDEN here on purpose -- if
        # Moho declines, this lane must RECORD that rather than red on it.
        # Promotion is the post-reading arming call, a recorded human decision.
        "V11-moho-player-loop.toml":        "calibration-discipline - it began as a READING run (V8/V9 iteration-1 pattern) and is now ARMED on what those readings measured: the ENGAGED classification, the schedule (the unit takes ONE window spacing where Dres took two), the loiter cut on a ~398,000 s LKO wait, and count {6,6}, with the two cohesion decline reasons forbidden. Two byte-identical readings, an armed run and a reverted negative control back it. Operator tier is now the ordinary promotion call, not outstanding work",
        # V11A is a TimeJump observation lane whose brackets are derived from
        # V11's measured D0/tof, so it is re-derived rather than re-run when the
        # fixture changes. Pass 1 reads the tilt; the census bracket needs a
        # soiEntryUT only pass 1 can print.
        "V11A-moho-loop-arrival.toml":      "calibration-discipline - a TimeJump observation lane whose brackets come from V11's measured loop-unit line, so it is re-derived rather than re-run when the fixture changes. ARMED on the tilt disposition at Moho's 7 deg (state=retained, the TOP of the band the synthesizer's comments call its failing population), the arrival geometry, the ready line and the eccentric-band token, with state=declined forbidden. The seam-endpoint census WAS the one thing it deliberately did not arm; it is ARMED as of 2026-08-14 (branch `line-blink-census`) with NO change to the flown shape. The blocker was never the geometry: the census summary rode a SHARED 5 s rate-limit key that this lane's first jump primed, and class-splitting that key (measured vs skip-only) made `evaluated=1 outsideSoi=0` readable on the existing jumps. Re-flown verbatim twice green, armed, negative-controlled and reverted",
        # B21 is B19's profile RETARGETED, and it is operator by the same
        # calibration discipline for a reason that is Eeloo's alone: e = 0.26 makes
        # the transfer TIME a 2x band rather than a number, so the arrival end
        # MechJeb's window lands on is unknowable pre-flight and every game-second
        # budget is sized on the aphelion worst case. FLOWN GREEN TWICE 2026-08-12
        # (`_2003` and `_2239`, both PASS attempt 1) with NOT ONE PARAMETER CHANGED,
        # and the recordings count is now RE-PINNED {5,5} off `_2239`'s produced save
        # with every span UT named -- so the re-pin this entry used to wait on is
        # DONE. THE ENTRY STAYS ANYWAY, and not as residue: this list's completeness
        # cell keys on `tier == "operator"` and NOT on the `flown` tag, so every
        # operator-tier spec owes a recorded human call here for as long as it is
        # operator-tier, green or not. What the call now says is that operator tier
        # is the ORDINARY promotion judgement for a ~52-minute interplanetary lane
        # (its wall measured 3,092 s), not an outstanding debt. No ASSERTION is
        # loosened for the retarget beyond one derived park ceiling; three budgets
        # ARE widened and the correction cap is LOWERED (1,200 -> 550, because the
        # cap is per round and this lane can fire FOUR capped rounds -- the two the
        # spec schedules plus up to two arrival-quality extras granted at
        # mlib.py:10343-10376 on their own MAX_ARRIVAL_EXTRA_ROUNDS = 2 counter --
        # so the worst correction spend is 4 x cap, and 550 is the largest round
        # value surviving that tail on the sizing geometry), each with its
        # arithmetic in the spec.
        "B21-eeloo-orbit.toml":             "calibration-discipline - LIVE-PROVEN 2026-08-12, green twice (`_2003` / `_2239`, both PASS attempt 1) with no parameter re-tuned, and the recordings count re-pinned {5,5} from the measured flight with every span UT named, so the re-pin this entry once waited on is discharged; it stays only because the completeness cell keys on tier==operator rather than on the flown tag, and the call it records is that operator tier is the ordinary promotion judgement for a ~52-minute interplanetary lane whose arrival end (Eeloo e=0.26 makes the transfer time a 2x band, 23.3M-46.5M game s) is unknowable pre-flight - not an outstanding debt",
        # V9 began as a pure READING RUN and did its job: it measured the optimizer
        # split defeating the re-aim classifier. That finding is now FIXED and V9 is
        # ARMED as its regression floor, so the post-reading arming call it was
        # waiting on has been taken. What remains is the ordinary operator -> cadence
        # promotion decision, which is a human call and not a debt this tag names.
        "V9-dres-player-loop.toml":         "calibration-discipline - it began as a reading run (V8 iteration-1 pattern), MEASURED the optimizer/re-aim defect, and is now ARMED as that fix's regression floor (classification + schedule + count tokens, two decline reasons forbidden, control run recorded). Operator tier is the ordinary promotion call; the tilt disposition it could not reach is V10's lane, not outstanding work here",
        # V10 is operator by the same calibration discipline: it is a TimeJump
        # observation lane whose brackets are derived from a specific recording's
        # replay clock, so it is re-derived rather than re-run when the fixture
        # changes. Armed on its measurements; the one thing it deliberately does
        # not arm (the seam-endpoint census pair) is documented in the spec with
        # the trade that forced it.
        "V10-dres-loop-arrival.toml":       "calibration-discipline - a TimeJump observation lane whose brackets come from one recording's replay clock; armed on the tilt/geometry/ready tokens PLUS, as of 2026-08-14 (branch `line-blink-census`), the census pair `evaluated=1 outsideSoi=0`. Iteration 5 executed iteration 4's own restoration recipe: both instrument blockers were closed upstream (a WINDOW-EXIT exemption on the line-blink detector, and a class-split census rate-limit key), and iteration 3's -900/-300/+600 escape bracket was restored. It is KEPT not for the census -- which reads without it, falsifying iteration 4's pre-D0 conclusion -- but because the restored pre-D0 jumps make this lane the LIVE REGRESSION FLOOR for the exemption, which visibly fires there (`line-blink-suppressed ... windowTransitionExempt=True toggleVerdict=InsideWindowOn priorToggleVerdict=WindowExitOff` at currentUT~31276442, the very UT that red three times, and now ARMED as a required token so the floor cannot go vacuous)",
        # V12 is V9's shape on the Eeloo fixture and it has now reached V9's FINISHING
        # point: it flew twice green on 2026-08-13 (_0053/_0055, byte-identical on
        # every measured token) and is ARMED on those measurements. The three headline
        # quantities it was waiting on all came back - cadence 48,883,481.633 is
        # EXACTLY 5x the 9,776,696.327 s synodic at a 4.8111-synodic raw span (the
        # program's first multiple past Dres's and Eve's 2), the compressor cut
        # 5,086,416 s of the LKO ejection wait, and the member reads segs=20
        # supported=True target=Eeloo. Unlike V9 it measured no defect, so the armed
        # set is a regression floor for a HEALTHY reading; V9's two decline forbids
        # come with it because a decline here is now a regression rather than a
        # reading. Operator tier is the ordinary promotion call. THE NEGATIVE CONTROL
        # IS RUN AND PASSED, not owed: `_0114` inverted the last digit of the armed
        # cadence token in the required array only and the lane red
        # PARSEK-FAIL(expectation) naming exactly `cadence=48883481.632992939`, with
        # the other nine tokens and every other verifier still green; `_0116` reverted
        # exactly and re-flew PASS.
        "V12-eeloo-player-loop.toml":       "calibration-discipline - FLOWN TWICE GREEN 2026-08-13 (_0053 wall 48 s / _0055 wall 49 s, both PASS attempt 1, byte-identical on every measured token) and ARMED on those measurements: a ten-token required list (plumbing trio + the ENGAGED classification + the member topology + cadence==5x synodic to the digit + the loiter cut and the 10.8% compressedSpan cut fraction), V9's two decline reasons forbidden, and count re-pinned {6,6} from the interim window. It measured no defect, so the armed set is a regression floor for a HEALTHY ENGAGED reading at the program's deepest span>synodic ratio. Claims no coverage cells (the value is the measurement and the floor); operator tier is the ordinary promotion call, not a debt - the negative control is RUN AND PASSED (`_0114` inverted the armed cadence token's last digit in the required array only and red PARSEK-FAIL(expectation) naming exactly `cadence=48883481.632992939` with every other token and verifier green; `_0116` reverted exactly and re-flew PASS)",
        # V12A is V12's missing half and V10's shape on the Eeloo fixture: the
        # TimeJump lane that actually reaches the per-window synthesizer. It WAS a
        # TWO-PASS lane by construction and BOTH PASSES ARE NOW FLOWN - pass 1
        # (`_0120`) printed the five replay-clock quantities, pass 2 recomputed all
        # seven jump UTs from them and flew twice byte-identically (`_1513`/`_1515`),
        # and the lane is ARMED, negative-controlled and reverted (`_1536`/`_1537`/
        # `_1539`). So the post-reading arming call this entry used to wait on is
        # DONE, and the entry stays only because the completeness cell keys on
        # `tier == "operator"` rather than on the `flown` tag.
        # WHAT THE MEASUREMENT SAYS, and it is not what the lane predicted: the tilt
        # came back `state=noop reason=in-plane`, because the solved conic's 4.0725 deg
        # is BELOW the 6.6500 deg bound so the excessive-tilt gate never opened. Eeloo
        # therefore tested the BOUND ARITHMETIC and NOT the retention branch, which
        # remains Eve-only-validated - the tilt plan's claim scope is NOT widened by
        # this lane (filed REAIM-TILT-NOOP-AT-EELOO-6.15-DEG). The M-MIS-3 claim is
        # likewise NARROWER than the pre-flight header asserted: the band is pinned as
        # COMPUTED at e=0.26 but was never WALKED (step 0 accepted, devFromRecorded=0s),
        # so the behavioural half of that debt is still open
        # (M-MIS-3-BAND-COMPUTED-NOT-EXERCISED). The census stays unarmed for a reason
        # now better understood than V10's carried one.
        "V12A-eeloo-loop-arrival.toml":     "calibration-discipline - BOTH PASSES FLOWN AND ARMED 2026-08-13: a two-pass TimeJump observation lane whose seven brackets are arithmetic on one recording's replay clock and on the product's own re-aimed soiEntryUT (pass 1 `_0120` printed them, pass 2 `_1513`/`_1515` flew the recomputed shape byte-identically, `_1536` armed PASS, `_1537` negative control correctly PARSEK-FAIL(expectation) naming the one inverted digit, `_1539` reverted PASS). ARMED on 13 tokens - the measured noop tilt literal plus its derivable bound pair, the e=0.26 band, all three synth-geometry proximity checks including the MEASURED Eeloo SOI constant, and the ready line - with count re-pinned {6,6} and the decline forbid narrowed to `state=declined reason=unreachable-plane`. TWO FINDINGS LIMIT WHAT IT PROVES, both filed: the tilt read noop/in-plane because the solved conic sat BELOW the bound, so Eeloo tested the bound arithmetic and NOT the retention branch (still Eve-only-validated); and the tof band is pinned as COMPUTED but never WALKED (step 0 accepted), so the behavioural half of M-MIS-3 stays open. The seam-endpoint census was a documented reading until 2026-08-14, when the shared-key blocker this lane DIAGNOSED was fixed on branch `line-blink-census` by class-splitting the key (measured vs skip-only); re-flown verbatim twice green, `evaluated=1 outsideSoi=0` is now armed as a 14th token (16 at HEAD, after the conic-shape arming), confirming the bracket was dead on the seam all along and the census merely silent. Operator tier is the ordinary promotion call, not a debt",
        # B22 is B21's flown Eeloo profile RETARGETED to Jool, and it is operator by
        # B20's half of the calibration discipline rather than B19's: the pre-transfer
        # JETTISON debris topology is MEASURED, so what is DERIVED here is the ARRIVAL.
        # Nine of the 56 missionParams changed and the AIM drives the rest --
        # courseCorrectPeriapsisMeters 600,000,000 m is 24.43% of Jool's 2,455,985,185 m
        # SOI (600e6 / 2,455,985,185 = 0.2443), a req/SOI regime only TWO of the corpus's
        # 25 correction-complete points have ever been flown in (Eve at 5.875%, k=0.997;
        # the Mun at 10.29%, k=0.545-0.563), so the delivered periapsis is an
        # extrapolation past the end of the data by more than an order of magnitude in
        # SOI. Everything downstream of the aim is arithmetic on it rather than on a
        # flight: parkMaxApoapsisMeters 1,500,000,000 (1,506 Mm radius = 61.3% of SOI),
        # the targetPeriapsisFloorMeters / parkMinPeriapsisMeters pair at 1,000,000 (5x
        # Jool's 200 km atmosphere top), capturePlanTimeoutSeconds 300,000 and
        # captureBurnTimeoutSeconds 3,000,000 (2.20x the worst 1,361,783 s in-SOI coast).
        # Promotion is the post-flight re-pin of those plus the recordings count.
        "B22-jool-orbit.toml":              "calibration-discipline - LIVE-PROVEN 2026-08-17 (`2026-08-17_1959`, PASS attempt 1, wall 2,441 s, every verifier PASS or SKIPPED), with the arrival, the in-SOI coast, the round count and the recordings count all re-pinned from the measurement: requested 600,000,000 m arrival ALTITUDE delivered 584,327,170.912 (k = 0.9739) = 590,327,171 m RADIUS, 2.789x Pol's clearance edge and 24.04% of the SOI, which REFUTES the request-independent law (d) that predicted an 88.3 Mm median regardless of request and makes Jool the THIRD BODY above req/SOI 4% (Mun 0.545, Eve 0.997, Jool 0.974). Recordings re-pinned {5,5} with 145.082 LF units (10.1% of the drop tanks) unspent, ~300x further from the shed boundary than B21's own {5,5}; the shed threshold is B21's committed 3,944.7 m/s, against which this flight ran 148.6 m/s under and the worst realistic geometry 11.0 m/s over. It stays operator because the completeness cell keys on tier==operator rather than on the flown tag, and operator is the ordinary promotion judgement for a ~41-minute interplanetary lane - not an outstanding debt",
        # V13 is V12's shape on the Jool fixture, shipped as a READING run until its
        # measurements come back.
        "V13-jool-player-loop.toml":        "calibration-discipline - ARMED AND CONFIRMED 2026-08-17: shipped in the V8/V9/V11 iteration-1 reading posture, then armed off the reading run's own bytes and flown twice green (`_2055`/`_2101`) with the measured payload byte-identical across both. Synodic, cadence, the 25-segment classification and the loiter cut are all pinned from measurement, and the decline reasons are now forbidden because ENGAGED is measured. It stays operator because the completeness cell keys on tier==operator rather than on the flown tag, not because anything is outstanding",
        # V13A is V13's missing half and V12A's shape on the Jool fixture: the two-pass
        # TimeJump lane whose brackets are arithmetic on V13's replay clock.
        "V13A-jool-loop-arrival.toml":      "calibration-discipline - ARMED AT PASS 2 AND CONFIRMED 2026-08-17. The two-pass shape earned itself: pass 1's bracket was computed as D0 + tof and the synthesizer emitted a soiEntryUT 1,162,892 s earlier, so a one-pass lane would have armed a bracket that misses the arrival seam. Pass 2 re-derived it from the emitted value, the skip-only census line disappeared, and all 17 tokens are pinned from measurement across four green runs. It stays operator because the completeness cell keys on tier==operator rather than on the flown tag",
        # The V2 dwell is operator BY THE CALIBRATION DISCIPLINE (V1 precedent):
        # its first flight is a deliberately under-gated READING run whose red,
        # if any, is evidence; promotion is the post-reading arming call, not a
        # review debt.
        "V2-loop-arrival-dwell.toml":       "operator by the calibration discipline (V1 precedent); FLOWN 2026-08-06 (nine runs: six findings iterated, true armed run PASS, negative control correctly red, reverted) - promotion past operator is the open human call",
        # The V3 flight-scene A/B pair is operator by the SAME calibration
        # discipline: reading runs first (the faithful half's clean sweep and
        # the re-aim half's EXPECTED PARSEK-FAIL(anomaly) are both
        # measurements), arming is the post-reading call.
        "V3F-flight-arrival-faithful.toml": "operator by the calibration discipline; FLOWN 2026-08-07 PASS attempt 1 - the reading run measured the hidden-by-zone gate (vacuous for seams, decisive as a finding); keeps the knob mode-discrimination gate",
        "V3R-flight-arrival-reaim.toml":    "operator by the calibration discipline; FLOWN 2026-08-07 PASS attempt 1 - expected red did not occur for the measured structural reason (hidden-by-zone); the GS-3 flip moved to V3C; keeps the ENGAGED mode gate",
        "V3C-flight-arrival-companion.toml": "reading-run instrument (calibration discipline); SIX runs flown 2026-08-07/08 (runs 1-2: cycle misalignment then trace reached; runs 3-5: the 800 cap closed the encounter, run 3 PASS) - the seam-instant observation needs the co-location design named in the spec header (120 km zone wall)",
        # tier=operator by PROMOTION POLICY, not debt, on the same ground as GS-1:
        # both are unflown and both consume a fixture the forge above has yet to
        # produce, so neither can sit on a cadence. Promotion is a later human
        # call after the report-only reading run and the arming sequence each
        # header specifies. GS-3 additionally must not be promoted before GS-2 has
        # flown - its entire value is the DIFFERENCE from GS-2's outcome, and a
        # difference measured against an unflown baseline is not a difference.
        "GS-2-orbital-probe-deploy.toml":   "FLOWN GREEN 2026-08-05 (0853 reading, 0856 armed); operator tier is now an open PROMOTION call, not debt",
        "GS-3-switch-nudge-deployed.toml":  "FLOWN 3x 2026-08-05 (0903 measured the bug, 1132 measured the fix) and ARMED; operator tier is now an open PROMOTION call, not debt",
        # The V4/V5 player-workflow pair, operator by the SAME calibration
        # discipline as V1/V2/V3: the first flight was a deliberately under-gated
        # READING run whose red, if any, would have been evidence. Both READ
        # GREEN on 2026-08-08 and both were then ARMED on the V2/B17 three-run
        # discipline, so the post-reading arming call these entries pointed at
        # has been TAKEN. What remains for each is the ordinary operator ->
        # nightly PROMOTION call, which is a cadence decision for a human and not
        # a review debt.
        "V4-player-loop-workflow.toml":     "FLOWN GREEN 2026-08-08 (1135 reading, 1154 armed, 1156 min=1 negative control PARSEK-FAIL(save-structure)) and ARMED on both save-structure blocks; both EnterWatchMode verdicts came back REJECTED as predicted from the camera-only range gate (this lane's 643,913 m parked-tail draw is 2.15x outside the production 300 km WatchEnterCutoffMeters and 5.4x outside the entry boundary V7M later measured, so its verdicts stand under every correction and it discriminates nothing between them; the finding is owned by docs/dev/todo-and-known-bugs.md -> WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE). Operator tier is now an open PROMOTION call, not debt",
        "V5-ts-loop-arrival.toml":          "FLOWN GREEN 2026-08-08 (1144 reading, 1155 armed; negative control shared with V4's 1156) and ARMED on both save-structure blocks; the TS host's own ghost-creation count answered 1, so the anti-vacuity gate is satisfied by measurement. Operator tier is now an open PROMOTION call, not debt",
        # The V6/V7 MOON quartet (Mun/Minmus x FLIGHT/TRACKSTATION), operator by the
        # SAME calibration discipline as V1/V2/V3/V4/V5 and for one extra structural
        # reason worth recording: their TimeJump targets are PRE-FLIGHT PREDICTIONS of
        # a phase-locked zero-drift schedule (Mun first window k=13 pad rotations,
        # Minmus k=50), and the anchor they are computed from is only knowable at run
        # time, so the first flight of each is a calibration run by construction.
        # Posture as of 2026-08-08: the three GREEN lanes have taken the post-reading
        # arming call (both save-structure blocks now `gating = true` on V6M, V6T and
        # V7M, each off its own reading run, with one shared negative control on V6M),
        # so what remains for them is the ordinary operator -> nightly PROMOTION call,
        # which is a cadence decision for a human and not a review debt. V7T stays
        # UNGATED: it is red by finding, and arming a second gate on a lane whose
        # verdict already carries one would be arming off a red.
        "V6M-mun-player-loop.toml":         "FLOWN GREEN 2026-08-08 (2026-08-08_1554 reading, PASS attempt 1, 54 s) - the pre-flight schedule prediction (k=13 pad rotations, phaseAnchorUt 280,176.945) matched the measured 280,176.94738016772, so no re-pin was needed. ARMED on both save-structure blocks (armed run 2026-08-08_1640 PASS attempt 1; negative control 2026-08-08_1644 PARSEK-FAIL(save-structure) on `rewind.supersedeRows 0 < min 1`, reverted). Operator tier is now an open PROMOTION call, not debt",
        "V6T-mun-ts-arrival.toml":          "FLOWN GREEN 2026-08-08 (2026-08-08_1559 reading, PASS attempt 1, 50 s) - the TS host materialized the looped faithful moon member (`created 1 ghost vessel(s)`, Mun-framed hyperbola, `factory chain ... reaimed=False`, V5's token inverted). ARMED on both save-structure blocks (armed run 2026-08-08_1641 PASS attempt 1; negative control shared with V6M's 1644). Operator tier is now an open PROMOTION call, not debt",
        "V7M-minmus-player-loop.toml":      "FLOWN GREEN 2026-08-08 after one falsification and one calibration (_1600 INVALID: the pinned watch OK was wrong because being inside the 300 km figure V4 quotes is NOT sufficient for entry - refused at 144-199 km, entered at 51.5 km, so the entry boundary brackets in (51.5 km, 144.3 km); the MECHANISM behind that boundary is UNESTABLISHED and the 120 km render-zone explanation first written for it was retracted 2026-08-09, see docs/dev/todo-and-known-bugs.md -> WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE; _1607 calibration located an in-boundary epoch; _1613 PASS attempt 1, 53 s, with the suite's FIRST watch-mode entry). ARMED on both save-structure blocks (armed run 2026-08-08_1642 PASS attempt 1; negative control shared with V6M's 1644). Carries one report-only product finding (a teardown NRE in WatchModeController.RestoreCameraAfterWatchExit when a run ends inside watch mode) which is FILED, not owed by this spec - and deliberately NOT armed as a unityExceptions ceiling, since the two green flights of the identical shape counted 1 and 5 raw NREs. Operator tier is now an open PROMOTION call, not debt",
        "V7T-minmus-ts-arrival.toml":       "FLOWN 2026-08-08, RED BY FINDING and deliberately kept red (2026-08-08_1614 and _1616, both PARSEK-FAIL(anomaly), both `icon-off-orbit angleIconVsOrbitEff=131.22` to the decimal - deterministic, not a flake). Every other verifier green, all 16 steps met. The V1-map-dwell-mun-orbit precedent applies: a red-by-finding lane is an outcome, not a debt this tag would name. What a human owns here is the icon question itself, and it is written up in the spec header with a named discriminating experiment; report-only, nothing armed",
        "V8-eve-player-loop.toml":          "FLOWN GREEN 2026-08-11 through four reading iterations on the new eve-orbit-recorded fixture - the program's first ENGAGED inward transfer and its first span>synodic loop unit (_0802 arm-and-read; _0807 brackets, PASS, and the FIRST-EVER seam-endpoint-outside-soi raise, ratio 4.6216 on the Sun->Eve seam after the tilt gate declined all 27 tof candidates to a faithful window; _0810 and _0814 PARSEK-FAIL(anomaly) on a line-blink detector gap at back-to-back seam-straddling TimeJumps, both filed with artifacts in todo-and-known-bugs.md, spec re-paced with RecordingState spacers rather than any anomaly exemption; _0818 and _0819 both PASS attempt 1 with clean sweeps - two consecutive flights of the final shape, the raise reproducing bit-identically at ratio=4.6216 on every bracketed run). ARMED on both save-structure blocks (armed run 2026-08-11_0828 PASS attempt 1, gating=True mismatches=0; negative control _0830 PARSEK-FAIL(save-structure) on the single inverted window `rewind.supersedeRows 0 < min 1`, reverted). The finding trio (tilt-decline, faithful window, the seam-endpoint raise) plus the D11 cut token are REQUIRED - the GS-3-style regression floor: a change that un-declines Eve's windows or moves the arrival geometry reds this lane and forces a re-read. FIX ERA (2026-08-11, branch reaim-inclined-targets): the tilt-retention fix red the floor BY DESIGN (_1242, exactly the three trio mismatches) and the lane was re-pinned to the healthy state (re-aimed transfer ready devFromRecorded=0s, state=retained, census outsideSoi=0; readings _1244/_1245, control _1246) with the old trio inverted into forbidden. Operator tier is calibration discipline, not debt",
        "V8T-eve-ts-arrival.toml":          "FLOWN GREEN 2026-08-11 (reading _0835 a1 INVALID on the TS-LOADGAME-RECORDING-ACTIVE-RACE, sighting 3, filed; _0836 a2 PASS clean; armed _0843 PASS attempt 1, gating=True mismatches=0; control shared with V8's _0830). First TS observation of a looped inward-transfer arrival: Eve-framed inbound materialized (created 1 ghost vessel(s), body=Eve TS token gates the D14 eve claim), factory reaimed=False (V5's Duna pin inverted - the tilt-declined faithful-window shape), census structural zero told from blindness, and the parity pair V5 omitted carried here. Surfaced + filed TS-FLUSHED-SAVE-DROPS-DEBRIS-TERMINALSTATE (byte-verified). FIX ERA (2026-08-11): the tilt-retention fix flipped the TS chain to genuinely re-aimed (baseline _1247 red on the reaimed=False pin; live reaimed=True phases=11); re-pinned to reaimed=True with fallback tokens forbidden (readings _1252/_1253). Operator tier is calibration discipline, not debt",
        "V8F-eve-loop-faithful.toml":       "FLOWN GREEN 2026-08-11 (iteration 1 PARSEK-FAIL on the author's own unescaped-parens regex, fixed; then two consecutive PASS runs with the five-raise set reproducing (four of five ratios to four decimals, the fifth 1 ulp: 138.2108/138.2109); armed same day, control shared with V8's _0830). The deliberate-faithful A/B half: FORCED FAITHFUL required + ENGAGED forbidden, the forced unit measured SELF-OVERLAPPING (overlapCadence = span/20, where the ENGAGED unit reads overlaps=no), and the hlib promotion blocker (2) population measured and PINNED - the first outsideSoi=[1-9] census pin, four per-instance Sun->Eve arrival raises (52.70-203.20) plus a Kerbin->Mun transit-seam raise (4.80). Calibration fact filed: benign ratios straddle V8's 4.6216 defect reading, so ratio cannot separate the classes. FIX ERA (2026-08-11): confirmed BYTE-IDENTICAL on the tilt-retention-fixed DLL (_1250 PASS - forced faithful bypasses the synth, the knob isolation held). Operator tier is calibration discipline, not debt",
        # The M-A7 RC-WARP lane, operator by the SAME calibration discipline as
        # V1/V2/V3: its first flights were deliberately under-gated REPORT-ONLY
        # reading runs, and its most likely red (`icon-teleport` under a rails
        # histogram, the token the first free-play ground-truth session raised 95
        # times on a render the operator called visually correct) was EVIDENCE
        # rather than debt - which is why its `allowedAnomalies` shipped empty on
        # purpose and why the red it drew is a discharge rather than a defect.
        # The arming call has now been TAKEN (2026-08-25, off the matching
        # _1502/_1616 pair) AND the discipline is COMPLETE the same day: armed
        # re-flight (twice) plus negative control. What is left on this lane is the
        # ordinary operator -> nightly PROMOTION call, a cadence decision for a
        # human and not a review debt.
        "V24W-duna-one-warp-stair.toml":    "operator by the calibration discipline (V1/V2 precedent); AUTHORED and ARMED 2026-08-25; readings 1415 (empty, root-caused), 1502 (full measurement, doctrine anomaly red) and 1616 (clean PASS re-fly, anomaly counts identical 65/2/2, every composition facet equal and the histogram within 0.5 % bucket for bucket) flown. ARMED off the matching 1502+1616 PAIR - a histogram read once is a sample - with dwells {1,32}, unevaluable {max 500000}, requireSeamKinds [rigid, flexible-soi] and the suite's FIRST warpBuckets [warp100, warp1000], the key no other subject may ever declare (their clocks are instantaneous TimeJumps, 1x-only by construction). It is the RC-WARP lane and the last M-A7 Phase-3 debt, and that debt is now DISCHARGED IN FULL - the arming closed its measurement half and the discipline closed the rest. THE DISCIPLINE IS COMPLETE ACROSS SIX FLIGHTS: armed re-flight 1722 (PASS attempt 1, gating=True, zero mismatches) plus 1811, which was flown as the control, never armed (a substring edit hit a rationale comment quoting the same key) and therefore counts as a SECOND armed re-flight (PASS attempt 1, zero mismatches); then the genuine negative control 1925, PARSEK-FAIL(render-composition) attempt 1 on the single mismatch `RC-WARP [FAIL] warpBuckets.warpHigh` with every sibling verifier row clean and the run JSON's new `declared` field recording warpBuckets ['warpHigh'] - the audit fix proving its own control - reverted in the same change. Anomaly counts 65/2/2 to the integer on all four full PASS flights (66/2/2 on the control). Promotion past operator is now an open cadence call, not debt",
        # THE TWO PHASE-4 / WAVE-B LANES, authored 2026-08-26 against the two fixtures
        # the route+park harvest landed. Both are operator-tier for the SAME
        # calibration-discipline reason V24W was and V8's iteration 1 was: a first flight
        # of a subject whose clock anchor is DERIVED rather than measured is a
        # CALIBRATION run by construction, and the derived jump UTs get re-pinned from
        # what the run reads. Neither is `pending-operator` because neither owes
        # outstanding HUMAN work - what they owe is a flight, and the derivation each
        # header carries is what makes that flight readable rather than a fishing trip.
        "V18T-depot-route-ts-arrival.toml":  "operator by the calibration discipline (V1/V2/V24W precedent); AUTHORED 2026-08-26, NEVER FLOWN, reading pending. THE SUITE'S FIRST ROUTE LANE and G1's first lane of any kind: a tracking-station observation of the committed Active GhostDriving SameBody route in the B27 harvest `depot-route-recorded`, arming NO mission loop because the ROUTE drives. Its anchor branch is genuinely unresolvable pre-flight - the header derives all THREE candidates (unlocked-faithful, single-rotation phase lock, and a VesselOrbital-dominant joint/zero-drift road whose anchor is not computable from the committed bytes at all) and the two forward jumps are chosen to be honest under every one of them, with the calibration recipe written down. So the reading run measures the anchor and round 2 re-pins; that is the discipline, not a debt. What IS gated on the first flight is anti-vacuity, three ways: `RevalidateSources ... transitioned=0` (the route did not flip to SourceChanged under the load-time optimizer - the one failure mode that would make this lane green and empty at once), `ghostDriving=[1-9]` and `routeMissions=[1-9]`. `[expectations.renderComposition]` is BARE and D10 `route-map-lines` is deliberately UNDECLARED (H35 CLAIM-IS-NOT-GATE): the headline facet `routeLineBuilds >= 1` would be the first non-zero reading of that census anywhere, and it gets declared in the commit that arms it, citing the run",
        "V25M-duna-park-player-loop.toml":   "operator by the calibration discipline (V8-iteration-1 precedent); AUTHORED 2026-08-26, NEVER FLOWN, reading pending. RE-AIM'S SECOND DEPARTURE CLASS - a heliocentric-parking departure, over `duna-park-recorded`, the path `ReaimClassifier`'s own exception comment names by fixture ('EXCEPTION (s15 Kerbal X #2)') and that no committed lane has driven. Unlike V18T its clock IS fully derivable and the header derives it end to end off the committed .prec bytes: classifier verdict (parking=True, via a replay of DetectRuns / the empty-cut scope gate / the ecc+sma admissibility gate), loiter cuts (ONE, destination-side, 43,963.92 s at the Duna capture, downstream of every window so all three map uncompressed), synodic 19,645,697.250367, span/synodic 1.185268 -> cadence = 2x synodic with PadAlignLaunch declined, k=142, D0 5,350,759,909.583645 and phaseAnchorUT 5,336,966,486.982761 - with the k shown robust to the seconds of scene time between LoadGame and the MissionConfig that stamps LoopAnchorUT. Operator tier is therefore the ordinary first-flight promotion call: the run confirms or refutes a written prediction rather than discovering one. The prediction is pinned as ONE conjunction regex over the ReaimDiag line and its exact inverse (the 'transfer departs from a heliocentric parking orbit' decline) is FORBIDDEN, so a refutation reds loudly instead of quietly measuring a faithful replay",
    }

    def _specs(self):
        out = {}
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            path = os.path.join(SCENARIOS_DIR, name)
            with open(path, "rb") as fh:
                spec = tomllib.load(fh)
            with open(path, "r", encoding="utf-8") as fh:
                text = fh.read()
            out[name] = (spec.get("tier"),
                         "pending-operator" in (spec.get("tags") or []),
                         "PENDING-OPERATOR" in text.upper())
        return out

    def test_the_carrier_set_is_exactly_the_reviewed_inventory(self):
        """Deliberately strict in BOTH directions: a spec gaining the tag reds,
        and a spec losing it reds. Adding a legitimate new carrier is meant to
        cost an edit here, with the reason written down, in the same commit."""
        carriers = sorted(n for n, (_, tagged, _) in self._specs().items() if tagged)
        self.assertEqual(sorted(self.CARRIERS), carriers,
                         "the set of specs carrying `pending-operator` changed; record the "
                         "spec in CARRIERS with the debt it owes, or drop the tag citing the "
                         "run that discharged it")

    def test_carriers_claiming_operator_tier_really_are_operator_tier(self):
        """The one half of a carrier's justification that IS machine-checkable."""
        specs = self._specs()
        # startswith, not ==: a reason may CITE operator-tier and then add more
        # (B16 does), and that citation must still be checked.
        wrong = sorted(n for n, why in self.CARRIERS.items()
                       if why.startswith("tier=operator") and specs[n][0] != "operator")
        self.assertEqual([], wrong, "CARRIERS claims tier=operator for a spec that is not")

    def test_every_untagged_candidate_is_classified(self):
        """The completeness half, over BOTH populations that can owe operator
        work: specs that MENTION the token, and specs that are `tier =
        "operator"`. Covering only the first was a real gap - `B16-eve-orbit` is
        operator-tier with a documented outstanding human call and never writes
        the string, so it sat in neither list and reds nothing. An untagged
        candidate is honest for three reasons - the debt is discharged, it
        belongs to another spec, or the tier is a mechanism rather than a debt
        (the FORGE runs) - and each must be a recorded human call."""
        mentions = sorted(n for n, (tier, tagged, m) in self._specs().items()
                          if (m or tier == "operator") and not tagged)
        self.assertEqual(sorted(self.REVIEWED_UNTAGGED), mentions,
                         "an untagged spec mentions PENDING-OPERATOR without a recorded "
                         "classification; read its row in docs/dev/autotest-status.md, then "
                         "either tag it (live debt) or add it to REVIEWED_UNTAGGED with why")

    def test_the_two_inventories_do_not_overlap(self):
        """A spec is in exactly one list. Overlap would mean the tag question was
        answered twice, differently."""
        self.assertEqual(set(), set(self.CARRIERS) & set(self.REVIEWED_UNTAGGED))

    # The 2026-07-31 sweep. Every one was discharged by a GREEN RUN, and a green
    # run cannot un-happen, so the tag reappearing means a hand edit or a merge
    # resurrection - not new information.
    DROPPED_2026_07_31 = (
        "S4.1-rewind-merge.toml",             # own rule: "stays until that first green run"
        "L1-hire-kerbal-career.toml",         # pending-FIXTURE residue; fixture landed
        "L1-dismiss-kerbal-career.toml",      # README records the kerbal; pool-neutral
        "L1-research-node-career.toml",       # README marks the node cost **VERIFIED**
        "L1-research-node-science.toml",      # rides the same VERIFIED node cost
        "L1-upgrade-facility-career.toml",    # proven live at -150,000, hardDivergences=0
    )

    # The 2026-08-12 drop, on the entry's OWN written rule ("DROP THIS ENTRY on that
    # green run") and S4.1's quoted rule that the tag "stays until that first green
    # run". Discharged by run `2026-08-11_2111` attempt 2 (PASS, wall 62 s): the three
    # post-fix conclusion tokens the entry was still holding for
    # (`outcome=retired-empty-provisional`, `AppendRelations
    # outcome=refused-unflown-provisional`, `outcome=concluded-no-supersede`) all fired
    # verbatim, both pre-fix `forbidden` cascade lines stayed absent, and expectations
    # read mismatches=0. Attempt 1 was INVALID(driver, seam-timeout) and is recorded as
    # a driver flake in the spec header - an INVALID is not a failed green run.
    DROPPED_2026_08_12 = (
        "S4.2-refly-world-preservation.toml",  # own rule: "DROP THIS ENTRY on that green run"
    )

    # THE 2026-08-29 TIER PROMOTION, and it is a DIFFERENT KIND OF DEPARTURE from the
    # two tuples above - which is the whole reason it is recorded separately rather
    # than appended to them. Those two hold specs that SHED THE TAG because a debt was
    # discharged by a green run. These six never carried the tag at all: they sat in
    # REVIEWED_UNTAGGED, classified as "operator tier is an open PROMOTION call, not
    # debt". The operator has now MADE that call - all in-game test lanes go on cadence
    # - so their tier is `nightly` and they leave the population this class can see at
    # all, because membership is `(mentions the token OR tier == "operator") AND NOT
    # tagged` and a nightly lane that never writes the string is neither.
    #
    # NOTHING WAS DISCHARGED AND NOTHING WAS HIDDEN. The classification these entries
    # carried was always "no work is owed, a human has to choose a cadence"; the human
    # chose. The per-lane evidence those entries summarised (flight counts, pinned
    # tallies, the fixture/corpus skip rosters, the two standing HARVEST requirements)
    # lives in each spec header and in docs/dev/autotest-status.md, which is where it
    # belongs - it was never this roster's to hold.
    TIER_PROMOTED_2026_08_29 = (
        "H34-logistics-inter-body.toml",
        "H35-logistics-route-proof.toml",
        "H38-logistics-isolated.toml",
        "H39-logistics-isolated-bdock.toml",
        "H40-logistics-isolated-depot-route.toml",
        "H41-logistics-grapple-isolated.toml",
    )

    def test_the_tier_promoted_specs_left_both_inventories(self):
        """The promotion is an OPERATOR DECISION, so it is pinned rather than
        trusted. Three ways it could rot, and each reds here:
          * a spec quietly flipped back to `tier = "operator"` without regaining a
            classification (the completeness cell would also red, but this one names
            the promotion as the thing that was undone);
          * a stale REVIEWED_UNTAGGED entry re-added for a lane that is no longer an
            operator-tier candidate, which would make the roster claim a decision
            nobody is waiting on;
          * the `pending-operator` tag appearing on one of them, which would assert
            outstanding human work against a call that has been made."""
        for name in self.TIER_PROMOTED_2026_08_29:
            with self.subTest(spec=name):
                spec = load_spec(name)
                self.assertEqual(
                    "nightly", spec.get("tier"),
                    "%s was promoted to the nightly cadence by operator decision on "
                    "2026-08-29; a change back is a new operator decision and needs "
                    "its own record here and in docs/dev/autotest-status.md" % name)
                self.assertNotIn(name, self.REVIEWED_UNTAGGED,
                                 "%s is nightly and mentions no PENDING-OPERATOR, so it is not a member of either population this class tracks" % name)
                self.assertNotIn("pending-operator", spec.get("tags") or [],
                                 "%s owes no operator work - the cadence call was made" % name)

    def test_the_specs_promoted_out_stay_out(self):
        for name in self.DROPPED_2026_07_31 + self.DROPPED_2026_08_12:
            tags = load_spec(name).get("tags") or []
            self.assertNotIn("pending-operator", tags,
                             "%s was live-proven and owes no operator work" % name)

    def test_the_tag_is_still_non_gating(self):
        """The whole rule above rests on the tag moving no verdict and gating no
        cadence. If `pending-operator` ever becomes a real tier, this class is
        reasoning about the wrong thing and must be revisited."""
        self.assertNotIn("pending-operator", hlib.TIERS)


class SaveStructureVerifierWiringTests(unittest.TestCase):
    """The M-C2/R9 save-parse verifier's hlib-side wiring: spec-surface
    validation routes through validate_spec, the gating flag classifies its own
    PARSEK-FAIL subkind, and - the SAFETY PROPERTY, mirroring the
    unityExceptions precedent - arming stays a deliberate, per-scenario,
    live-proven act.

    THAT PROPERTY WAS RESTATED 2026-07-31, not abandoned. It shipped as "no
    committed spec arms gating, so landing the verifier cannot move any
    nightly's verdict", which was the right guarantee while every arming was
    still unproven. S4.1-rewind-merge was then promoted on evidence (reading run
    `2026-07-31_1628`, armed `_1635`, negative control `_1637`), so the
    guarantee became the next one along: the ARMED SET IS AN ALLOWLIST, and a
    spec joining it needs an explicit edit citing its run ids.

    The two cells below are COMPLEMENTARY AND MUST BE MAINTAINED AS A PAIR.
    `test_no_committed_spec_arms_gating` is per-SPEC-FILE, so on its own it
    would not notice S4.1 arming a second block or re-pinning a window;
    `test_s41_declares_the_rewind_block_armed` supplies that per-block
    granularity. Neither alone is the guard."""

    def test_gating_mismatch_classifies_save_structure_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["save_structure_mismatch"] = True
        verdict = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(("PARSEK-FAIL", "save-structure"),
                         (verdict.verdict, verdict.subkind))
        self.assertIn("save-structure", hlib.PARSEK_FAIL_SUBKINDS)

    def test_clean_flag_stays_pass(self):
        d, v = _clean_pass_facts()
        v["save_structure_mismatch"] = False
        verdict = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(hlib.VERDICT_PASS, verdict.verdict)

    def test_validate_spec_rejects_malformed_rewind_block(self):
        spec = load_spec("S4.1-rewind-merge.toml")
        reg = load_registry()
        self.assertTrue(hlib.validate_spec(spec, reg).ok,
                        "the committed S4.1 spec must keep validating untouched")
        bad = copy.deepcopy(spec)
        bad["expectations"]["rewind"] = {"supersedeRows": {"min": 2, "max": 1}}
        v = hlib.validate_spec(bad, reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("expectations.rewind.supersedeRows" in e for e in v.errors))
        bad["expectations"]["rewind"] = {"gating": "yes"}
        v = hlib.validate_spec(bad, reg)
        self.assertFalse(v.ok)

    def test_validate_spec_rejects_malformed_structure_block(self):
        spec = copy.deepcopy(load_spec("S4.1-rewind-merge.toml"))
        spec["expectations"]["recordings"]["structure"] = {
            "terminalStates": {"Exploded": {"min": 1}}}
        v = hlib.validate_spec(spec, load_registry())
        self.assertFalse(v.ok)
        self.assertTrue(any("recordings.structure" in e for e in v.errors))
        spec["expectations"]["recordings"]["structure"] = {
            "trees": 1, "branchPoints": {"VesselSwitchContinuation": {"max": 0}}}
        self.assertTrue(hlib.validate_spec(spec, load_registry()).ok,
                        "a well-formed structure block must validate")

    def test_validate_spec_rejects_malformed_points_block(self):
        # Gate 12's block reaches validate_spec through the same delegation as
        # the other two, so a malformed window is a PRE-LAUNCH rejection rather
        # than a block that silently evaluates as a no-op mid-chain.
        spec = copy.deepcopy(load_spec("S4.1-rewind-merge.toml"))
        reg = load_registry()
        spec["expectations"]["recordings"]["points"] = {"biggest": {"min": 2}}
        v = hlib.validate_spec(spec, reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("recordings.points" in e for e in v.errors))
        # An ARMED-and-empty points block is a gate that can never red.
        spec["expectations"]["recordings"]["points"] = {"gating": True}
        v = hlib.validate_spec(spec, reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("gates nothing" in e for e in v.errors))
        spec["expectations"]["recordings"]["points"] = {"largest": {"min": 2}}
        self.assertTrue(hlib.validate_spec(spec, reg).ok,
                        "a well-formed points block must validate")

    def test_eva2_declares_the_points_block_unarmed(self):
        # Gate 12 landed REPORT-ONLY on EVA-2 (the scenario whose green
        # `count = {min=2,max=2}` let the empty-recording defect through).
        # UNARMED is the whole point of the landing: the window is measured
        # from live runs BEFORE it may move a verdict, so this cell must be
        # flipped in the same commit that arms it - alongside the allowlist
        # below and the run ids that justify it.
        exp = load_spec("EVA-2-orbital-board.toml")["expectations"]
        self.assertEqual(("recordings.points",),
                         saveparse.declared_structure_blocks(exp))
        self.assertEqual((), saveparse.armed_structure_blocks(exp))
        self.assertFalse(saveparse.gating_armed(exp))
        # It must still ASSERT something, or it is an inert header that reports
        # nothing (the warn case) and could never be promoted from a reading.
        self.assertTrue(
            set(exp["recordings"]["points"]) & set(saveparse.POINTS_ASSERTION_KEYS),
            "EVA-2's points block declares no assertion key - nothing to read")

    def test_the_c_sharp_writer_still_emits_pointcount(self):
        # SOURCE-SYNC GATE, same shape as CommittedBatchTallySourceSyncTests:
        # this cell reads OUTSIDE harness/ on purpose. The ENTIRE points
        # assertion rests on one C# line - RecordingTreeRecordCodec writing
        # `pointCount` UNCONDITIONALLY on every RECORDING node. If that write is
        # renamed, removed, or made conditional, every declared points window
        # silently degrades to "unparsed": quiet while the block is report-only,
        # and a confusing red once someone arms it. Fail HERE, locally, naming
        # the cause, instead of on a nightly.
        path = os.path.join(PARSEK_SOURCE_DIR, "RecordingTreeRecordCodec.cs")
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            src = fh.read()
        self.assertIn('recNode.AddValue("pointCount"', src,
                      "RecordingTreeRecordCodec no longer writes pointCount onto the "
                      "RECORDING node - [expectations.recordings.points] reads that key "
                      "as its ONLY source of truth (the FinalizeTreeRecordings log line "
                      "was rejected: it is Verbose, so it fails open). Either restore "
                      "the write or re-source the block.")
        # ...and UNCONDITIONALLY. A `pointCount` written under an `if` would make
        # an absent key mean "this recording had none" rather than "this save
        # predates the key" - exactly the ambiguity the `unparsed` counter exists
        # to refuse, and it would flip the facet from fail-loud to fail-quiet.
        # Checked structurally by INDENTATION against `lastResIdx`, the adjacent
        # write that is unconditional today: wrapping either in a guard block
        # indents it and reds here. (This catches the guard-block form; a guard
        # expressed some other way is not claimed to be caught.)
        lines = src.splitlines()
        def _indent(needle):
            # Default None rather than a bare next(): without it, deleting the
            # ANCHOR line turns this guard into a StopIteration *error* with no
            # message instead of the authored assertion below.
            ln = next((l for l in lines if needle in l), None)
            self.assertIsNotNone(ln, "anchor line %r is gone from %s - the "
                                     "indentation guard has nothing to compare against"
                                 % (needle, os.path.basename(path)))
            return len(ln) - len(ln.lstrip())
        self.assertEqual(_indent('recNode.AddValue("lastResIdx"'),
                         _indent('recNode.AddValue("pointCount"'),
                         "the pointCount write is no longer at the same (unconditional) "
                         "nesting as the lastResIdx write beside it - if it is now "
                         "guarded, an absent key stops meaning 'not measured'")

    # THE ARMED ALLOWLIST. This started life as `assertEqual([], armed)` - the
    # hard verdict-neutrality property that shipped with the verifier, when
    # nothing was armed and every arming was still unproven. S4.1 was promoted
    # 2026-07-31 after its report-only reading run, so the property it guards is
    # now the NEXT one along: arming stays a deliberate, per-scenario, live-proven
    # act. An allowlist keeps that guard biting - a second spec quietly growing a
    # `gating = true` still reds here and still needs an explicit edit plus the run
    # ids to justify it - where relaxing to "any spec may arm" would have thrown
    # the guarantee away entirely on the day it first got used.
    # CL-3 joined 2026-08-03, by the same route S4.1 took: a REPORT-ONLY reading
    # run first (`2026-08-03_1834`, PASS attempt 1) which MEASURED
    # `supersedeRows=1 tombstones=1`, then arming those two as `min = 1` FLOORS.
    # So arming again made an observed behaviour load-bearing rather than
    # asserting an unobserved one - the precondition this allowlist exists to
    # enforce. It is the first spec in the suite to gate on a TOMBSTONE, and the
    # floors are what separate "the merge ran" from "the merge retired
    # something": a refused batch writes `Added 0 supersede relations`.
    # GS-1-auto-chute-booster armed [expectations.rewind] 2026-08-05 after reading run
    # 2026-08-05_0824 (flight 4, PASS attempt 1) measured rewindPoints=0
    # supersedeRows=0 tombstones=0 - every declared window already met, so arming
    # moved no verdict; the reap windows make the critical-regression-guard shape
    # (routine two-stage flight leaves no RP behind) load-bearing.
    # GS-2-orbital-probe-deploy armed [expectations.rewind] 2026-08-05 after reading
    # run 2026-08-05_0853 (flight 2, PASS attempt 1) measured rewindPoints=1
    # supersedeRows=0 tombstones=0 - every declared window already met. The
    # rewindPoints={min 1} floor is the EXACT INVERSION of GS-1's {max 0}: between
    # them both branches of RewindPointReaper.IsReapEligible are load-bearing.
    # GS-3-switch-nudge-deployed armed [expectations.rewind] 2026-08-05 after the
    # POST-FIX reading run 2026-08-05_1132 (PASS attempt 1) measured rewindPoints=1
    # supersedeRows=0 tombstones=0. Its window was INVERTED from {max 0} to {min 1}
    # in the same edit: while it was measuring the S17 bug, {max 0} described the
    # reap that cost the player the re-fly affordance, and arming it then would have
    # pinned the defect as the contract. Post-fix (570960da1) it declares GS-2's
    # window verbatim - with the glance and without it, the RewindPoint survives -
    # so this spec is now the REGRESSION GUARD for that fix rather than the
    # experiment that found it.
    # V14M / V14T: rewind (all max 0 - a pure replay-observation workflow authors
    # nothing durable) + structure (trees {1,2} on the duplicate-writer hazard,
    # committedTrees / recordings / terminalStates pinned at the measured 1/1/Orbiting)
    # armed 2026-08-18 on the V2/B17/V4 three-run discipline, off each lane's OWN
    # reading run: V14M `2026-08-18_2336` (PASS attempt 1, wall 50 s) and V14T
    # `2026-08-18_2337`. BOTH readings measured every declared window already met, so
    # the arming re-pinned nothing and moved no verdict (the S4.1 rule).
    #
    # TWO THINGS ABOUT THIS PAIR THAT DIFFER FROM EVERY OTHER ENTRY HERE, recorded so a
    # reviewer does not have to reconstruct them:
    #   (1) NEITHER LANE DECLARED THESE BLOCKS BEFORE. Their reading runs shipped with
    #       no `[expectations.rewind]` and no `[expectations.recordings.structure]` at
    #       all, on the ground that a report-only block of PREDICTED numbers reads like
    #       a measurement. So this is not the usual `gating` flip onto an existing
    #       report-only window - the windows are WRITTEN FROM the measurement.
    #   (2) V14T'S READING RUN WAS A PARSEK-FAIL, and arming off it is still correct.
    #       The red is `anomalySweep hits=['icon-off-orbit']` - a DIFFERENT verifier,
    #       one Tier-C raise on a ghost-proto creation frame - while the saveParse
    #       facets it arms were clean (rewindPoints 0, supersedeRows 0, tombstones 0,
    #       committedTrees 1, trees 1, recordings 1). The anomaly is tolerated
    #       separately by the BARE token - NOT a ceiling - and filed report-only as
    #       MAPRENDER-ICON-OFF-ORBIT-CREATION-FRAME-AFTER-JUMP. The
    #       `{ token = ..., maxCount = 1 }` form was written first and rejected by
    #       `test_no_committed_spec_arms_a_count_budget` in THIS file, which holds the
    #       budget mechanism inert across the whole suite. Per that todo entry the
    #       arming is now READY (the armed run `2026-08-19_0002` supplied the green-run
    #       `hitCounts` baseline the doctrine requires) but NOT TAKEN, so a second raise
    #       in one run currently passes unnoticed; taking it means moving that invariant
    #       cell to a named allowlist in the same edit.
    # DISCIPLINE COMPLETE 2026-08-19: armed re-flights `_0001` (V14M) and `_0002`
    # (V14T), both PASS attempt 1 with every gated window green, plus ONE negative
    # control `_0003` across the pair - a temporary `supersedeRows = { min = 1 }` on
    # V14M, correctly PARSEK-FAIL(save-structure) with mismatches=1, reverted
    # immediately (the committed spec is the armed one). One inversion, not two: the
    # lanes gate through the single shared saveParse evaluator, so a second would
    # re-prove the evaluator rather than these windows (the V4/V5 precedent).
    # THE V15 GILLY PAIR, armed 2026-08-19 off their OWN reading runs
    # (`2026-08-19_1736` V15M PASS attempt 1; `2026-08-19_1739` V15T
    # PARSEK-FAIL(anomaly) attempt 1 - the pre-registered correct catch, whose
    # save-structure facets were clean). Both blocks on both lanes: `rewind` (all
    # max 0 - a pure replay-observation workflow authors nothing durable) and
    # `structure` (trees {1,2} for V2's duplicate-writer hazard, everything else
    # pinned at the measured 1/1 with terminalStates {Orbiting: 1}). Neither lane
    # declared either block before, so the windows are written FROM the
    # measurement rather than flipped onto a prediction. DISCHARGED 2026-08-19:
    # armed re-flights `_1808` (V15M) and `_1809` (V15T), both PASS attempt 1 with
    # every gated block green, and ONE negative control `_1810` shared across the
    # pair - the V4/V5/V14 precedent, since they gate through the single shared
    # saveParse evaluator, so a second inversion would re-prove the evaluator
    # rather than these windows.
    ARMED_ALLOWLIST = {"S4.1-rewind-merge.toml", "CL-3-refly-crew-tombstone.toml",
                       "V14M-ike-player-loop.toml", "V14T-ike-ts-arrival.toml",
                       "V15M-gilly-player-loop.toml", "V15T-gilly-ts-arrival.toml",
                       # V16M / V16T: `rewind` (all max 0 - a replay-observation
                       # workflow, even one carrying forty extra seam round-trips,
                       # authors nothing durable) + `structure` (trees {1,2} for
                       # V2's duplicate-writer hazard, everything else pinned at
                       # the measured 1/1 with terminalStates {Orbiting: 1}) armed
                       # 2026-08-19 off their OWN reading runs `2026-08-19_2114`
                       # (V16M, PASS attempt 1) and `_2115` (V16T,
                       # PARSEK-FAIL(anomaly) with all sixteen steps green - the
                       # pre-registered catch, whose save-structure facets were
                       # clean). Both measured 0/0/0/0 and 1/1/1 before arming, so
                       # the arming re-pinned nothing. DISCHARGED 2026-08-19: armed
                       # re-flights `_2211` (V16M) and `_2212` (V16T), both PASS
                       # attempt 1 with byte-identical gating saveParse payloads,
                       # and ONE negative control `_2213` shared across the pair,
                       # which red exactly on `rewind.supersedeRows 0 < min 1` and
                       # nowhere else, then reverted (the V4/V5/V14/V15 precedent -
                       # both gate through the single shared saveParse evaluator).
                       "V16M-laythe-player-loop.toml", "V16T-laythe-ts-arrival.toml",
                       # V17M: `rewind` (all max 0 - the family's replay-observation
                       # claim, now across a SELF-OVERLAPPING 20-instance loop whose
                       # re-arms the jumps repeatedly cross) + `structure` (trees
                       # {1,2} for V2's duplicate-writer hazard, everything else
                       # pinned at the measured 1/1 with terminalStates
                       # {Orbiting: 1}; recordings {1,1} is the sharp form of the
                       # reading-era {1,3} count window - the admitted load-time
                       # optimizer split never materialized on any of four runs)
                       # armed 2026-08-20 off its OWN green reading run
                       # `2026-08-20_1915` (PASS attempt 1, the H3-clock re-pin
                       # round; facets 0/0/0/0 and 1/1/1, points 746/746/746).
                       # V17T: the same two blocks, armed 2026-08-20 off ITS own
                       # green reading run `2026-08-20_1933` (PASS attempt 1, the
                       # dynamic-overlap-path re-pin round: all 20 cycles
                       # spawned, 9 Vall-frame TS lines) with a byte-identical
                       # gating saveParse payload - the pair's determinism
                       # statement. V5's mid-run-save shape makes the rewind arm
                       # worth most here. DISCHARGED 2026-08-20: armed re-flights
                       # `_1934` (V17M, PASS attempt 1) and `_1939_a2` (V17T,
                       # PASS; attempt 1 INVALID on a transient TS-re-entry
                       # LoadGame REJECTED, the retry's job), and ONE negative
                       # control shared across the pair flown on V17M (`_1941`,
                       # red EXACTLY on `rewind.supersedeRows 0 < min 1` and
                       # nowhere else, then reverted).
                       "V17M-laythe-vall-player-loop.toml",
                       "V17T-laythe-vall-ts-arrival.toml",
                       # V19M / V19T, the first RETURN-DIRECTION loop pair (G2),
                       # armed 2026-08-21 EACH OFF ITS OWN green reading run:
                       # `2026-08-21_0746` (V19M, PASS attempt 1, wall 98 s) and
                       # `2026-08-21_0750` (V19T, PASS attempt 1, wall 60 s).
                       # Both blocks on both lanes: `rewind` (all max 0 - the
                       # family's replay-observation claim, now on the inverted
                       # same-parent direction, and worth most on V19T for V5's
                       # reason since that lane writes a save mid-run and reads it
                       # back through a SECOND scene load) + `structure` (trees
                       # {1,2} for V2's duplicate-writer hazard, everything else
                       # pinned at the measured 1/1 with terminalStates
                       # {Orbiting: 1}; `recordings` {1,1} is the sharp form of the
                       # reading-era {1,2} count window, whose admitted load-time
                       # optimizer split at the ONE body-change seam did not
                       # materialize - both runs printed
                       # `exoCoastBodyChangeKept=1 splittableButRejected=0`).
                       # BOTH lanes measured 0/0/0/0 and 1/1/1 {Orbiting: 1} with
                       # points 201/201/201 BEFORE arming, so the arming re-pinned
                       # NOTHING and moved NO verdict on either lane, and the two
                       # gating saveParse payloads are byte-identical - the pair's
                       # determinism statement, the V17M/V17T shape.
                       # Two additive gates landed in the same pass and are NOT
                       # part of the saveParse arming: V19M promotes the measured
                       # `Split summary: .*exoCoastBodyChangeKept=1
                       # splittableButRejected=0` into `required` (the V14M
                       # precedent, so the optimizer-cohesion answer regresses
                       # loudly rather than silently), and V19T adds the
                       # `created 0 ghost vessel\(s\)` forbid now that its own run
                       # measured `created 1` with `noOrbit=0` (the S1.4 rule;
                       # V17T had retired that forbid because init-ZERO was the
                       # correct product outcome on ITS segment-less-tail subject,
                       # and this subject's jump lands inside a SEGMENTED coast).
                       # **DISCHARGED 2026-08-21, AND NOT BY A SHARED CONTROL.**
                       # An earlier draft of this comment said what remained was
                       # "the armed re-flights and the shared negative control";
                       # that was wrong on both counts and is corrected here.
                       # V19M: armed re-flight `2026-08-21_0852` PASS with both
                       # blocks gating and 0 mismatches, then its OWN negative
                       # control `2026-08-21_0855` PARSEK-FAIL(expectation) on
                       # ONE mismatch - `logContracts.required not matched:
                       # phase=body-orbit surface=ProtoOrbitLine .*body=Vall` -
                       # with saveParse still PASS and driverValidity /
                       # anomalySweep clean, then reverted.
                       # V19T: armed re-flight `2026-08-21_0854` PASS, its OWN
                       # negative control `2026-08-21_0858` PARSEK-FAIL
                       # (expectation) on ONE mismatch - `phase=GhostCreated
                       # surface=ProtoIcon pid=\d+ .*body=Vall
                       # scene=TRACKSTATION` - again with saveParse PASS, and
                       # revert confirmation `2026-08-21_0859` PASS. Its
                       # `2026-08-21_0857` is recorded as an EXTRA ARMED
                       # CONFIRMATION and NOT a control: it was attempted as one
                       # and PASSED, because a mis-escaped regex in the editing
                       # script silently made no edit at all. A control that
                       # passes is a FAILED control.
                       # WHY TWO CONTROLS RATHER THAN THE FAMILY'S USUAL ONE:
                       # the halves pin DIFFERENT LENSES (proto ORBIT LINE on the
                       # flight map, proto ICON in TRACKSTATION), so one shared
                       # inversion would have proven exactly one of them. Both
                       # inverted a required RENDER token rather than the
                       # standing `rewind.supersedeRows` evaluator minimum, which
                       # makes this pair the FIRST in the program to discharge
                       # roadmap confirmation criterion (b).
                       "V19M-laythe-jool-player-loop.toml",
                       "V19T-laythe-jool-ts-arrival.toml",
                       # V20M / V20T, the first KERBIN-ARRIVAL loop pair (the
                       # planet-to-Kerbin half of G2, where V19 did moon-to-parent):
                       # `rewind` (all max 0 - the family's replay-observation claim,
                       # here across a 32.6 Ms self-overlapping span whose 1,630,328.8 s
                       # re-arms every jump crosses) + `structure` (trees {1,2} for V2's
                       # duplicate-writer hazard, everything else pinned at the measured
                       # 1/1 with terminalStates {Orbiting: 1}; recordings {1,1} is the
                       # sharp form of the reading-era {1,3} window, which admitted a
                       # load-time split at EITHER of this subject's TWO body seams and
                       # never saw one). Armed 2026-08-27 off their OWN green runs -
                       # `2026-08-27_1925` (V20M reading run 3, PASS attempt 1, wall
                       # 71 s, the coast-epoch-first reorder round) and `2026-08-27_1913`
                       # (V20T reading run 2, PASS attempt 1, wall 61 s, with the
                       # `icon-teleport` tolerance live). BOTH lanes measured 0/0/0/0 and
                       # 1/1/1 {Orbiting: 1} with points 739/739/739 before arming - and
                       # so did V20M's run 2 and V20T's run 1, so the payload is armed
                       # off FOUR agreeing measurements through TWO scene chains and TWO
                       # jump orders. The arming re-pinned nothing and moved no verdict.
                       # `[expectations.recordings] count` tightens {1,3} -> {1,1} on
                       # both, and V20M ALONE promotes the measured `Split summary:
                       # .*exoCoastBodyChangeKept=2 splittableButRejected=0` into
                       # `required` (the V14M/V19M precedent; this subject is the FIRST
                       # in the corpus whose cohesion depends on
                       # `ShouldKeepCohesiveCrossBodyExoCoast`'s SECOND disjunct) while
                       # V20T pins the COUNT - one gate per fact across the pair, the
                       # V19M/V19T split verbatim. NO routing token is promoted on either
                       # lane and NO D11 cell is claimed.
                       # OWED: the two armed re-flights and the two PER-LANE negative
                       # controls. This pair CANNOT share one control, for the V19
                       # reason: V20M's own lens is `phase=GhostCreated
                       # surface=ProtoIcon ... body=Kerbin scene=FLIGHT` and V20T's is
                       # the `scene=TRACKSTATION` form, so one inversion would prove
                       # exactly one of them. Each spec header carries its own inversion
                       # and the tomllib pre-flight gate for it.
                       "V20M-jool-kerbin-player-loop.toml",
                       "V20T-jool-kerbin-ts-arrival.toml",
                       # V21M / V21T: `rewind` (all max 0 - the family's
                       # replay-observation claim at a SECOND moon-to-moon
                       # parent) + `structure` (trees {1,2} for V2's
                       # duplicate-writer hazard, everything else pinned at the
                       # measured 1/1 with terminalStates {Orbiting: 1}, points
                       # 1444/1444/1444) armed 2026-08-24 off their OWN green
                       # reading runs `2026-08-24_1704` (V21M, PASS attempt 1,
                       # the StockConic-lens re-pin round) and `_1705` (V21T,
                       # PASS attempt 1 at the moved 587,223 epoch). Both
                       # measured byte-identical facets before arming, so the
                       # arming re-pinned nothing - the pair's determinism
                       # statement. Armed re-flights + the pair's TWO
                       # render-token controls (different lenses, producers and
                       # scenes - one inversion would prove at most one of
                       # four things) recorded in the spec ledgers.
                       "V21M-mun-minmus-player-loop.toml",
                       "V21T-mun-minmus-ts-arrival.toml",
                       # V22M/V22T/V22K + V23M/V23T: the G3a surface-endpoint
                       # five, armed 2026-08-24 off their OWN green reading runs
                       # (`_2050`/`_2057`/`_2053`/`_2054`/`_2058`, all PASS
                       # attempt 1 after the three-round lens calibration that
                       # measured the landed-terminal render policy - see the
                       # todo entry and the spec ledgers). `rewind` all max 0;
                       # `structure` pinned at the measured MULTI-RECORDING
                       # debris-tree values (9/9 with points 1619 at V22, 11/11
                       # with 1841 at V23; terminal Landed=1, plus Destroyed
                       # min 1 on the M halves - the measured M-vs-T save
                       # asymmetry). V22K is the first armed KSC-scene lane.
                       "V22M-kerbin-splashdown-player-loop.toml",
                       "V22T-kerbin-splashdown-ts-arrival.toml",
                       "V22K-kerbin-splashdown-ksc-arrival.toml",
                       "V23M-mun-landing-player-loop.toml",
                       "V23T-mun-landing-ts-arrival.toml",
                       "GS-1-auto-chute-booster.toml", "GS-2-orbital-probe-deploy.toml",
                       "GS-3-switch-nudge-deployed.toml",
                       # B17: rewind (all max 0 - a clean single-launch flight
                       # authors no RP/supersede/tombstone) + structure (the
                       # exact two-recording committed topology) armed
                       # 2026-08-06 on the three-run discipline; reading run
                       # 2026-08-06_0007 (every window already met), armed +
                       # negative-control runs cited in the status doc row.
                       "B17-duna-direct-orbit.toml",
                       # V2: rewind (all max 0 - the dwell authors nothing
                       # durable) + structure (exactly one committed tree; the
                       # scene-entry promotion stub never commits) armed
                       # 2026-08-06 on the three-run discipline; reading runs =
                       # V2 flights 4-6 (all reads 0 / committedTrees 1), armed
                       # + negative-control runs cited in the status doc row.
                       "V2-loop-arrival-dwell.toml",
                       # V4: rewind (all max 0 - the player workflow arms, warps,
                       # watches and jumps but authors nothing durable) +
                       # structure (exactly one committed tree; the scene-entry
                       # promotion stub never commits) armed 2026-08-08 on the
                       # V2/B17 discipline; reading run 2026-08-08_1135 (PASS
                       # attempt 1, every declared window already met - rewind
                       # facets all 0, committedTrees 1, trees 1), armed +
                       # negative-control runs cited in the status doc row.
                       "V4-player-loop-workflow.toml",
                       # V5: the same two blocks, armed 2026-08-08 on the same
                       # discipline; reading run 2026-08-08_1144 (PASS attempt 1,
                       # rewind facets all 0, committedTrees 1, trees 1). The
                       # arming is worth more here than on a single-scene dwell:
                       # V5 is the one committed spec that writes a save mid-run
                       # and reads it back through a SECOND scene load, so a
                       # stray durable write is easiest to miss in this shape.
                       # Armed run cited in the status doc row; the min=1
                       # negative control was flown once, on V4, since both specs
                       # gate through the one shared saveParse path.
                       "V5-ts-loop-arrival.toml",
                       # The V6/V7 MOON lanes (three of the quartet), armed
                       # 2026-08-08 on the same V2/B17/V4 discipline. Each was
                       # armed off its OWN reading run, all three of which
                       # measured every declared window already met (rewind
                       # facets all 0, committedTrees 1, trees 1):
                       #   V6M reading 2026-08-08_1554, armed 2026-08-08_1640
                       #       PASS attempt 1;
                       #   V6T reading 2026-08-08_1559, armed 2026-08-08_1641
                       #       PASS attempt 1;
                       #   V7M reading 2026-08-08_1613, armed 2026-08-08_1642
                       #       PASS attempt 1.
                       # NEGATIVE CONTROL flown once, on V6M
                       # (2026-08-08_1644, PARSEK-FAIL(save-structure), single
                       # mismatch `rewind.supersedeRows 0 < min 1`, reverted) -
                       # the V4/V5 precedent: all three gate through the one
                       # shared saveParse evaluator, so a second identical
                       # inversion would re-prove the evaluator rather than
                       # these windows, at the cost of a flight.
                       # The FOURTH moon lane, V7T-minmus-ts-arrival, is
                       # DELIBERATELY ABSENT: it flew RED BY FINDING (a
                       # deterministic `icon-off-orbit` raise) and a lane whose
                       # verdict is already carrying a finding must not have a
                       # second gate armed on top of it.
                       "V6M-mun-player-loop.toml",
                       "V6T-mun-ts-arrival.toml",
                       "V7M-minmus-player-loop.toml",
                       # V8: armed 2026-08-11 off the two consecutive clean
                       # reading runs of the final paced shape (_0818/_0819,
                       # saveParse REPORT all-zero rewind facets, trees=1,
                       # committedTrees=1); armed + negative-control run ids
                       # on the spec header's ARMING LEDGER and the status
                       # row.
                       "V8-eve-player-loop.toml",
                       # V8T: armed 2026-08-11 off its reading run (_0836
                       # a2, all-zero rewind facets, trees=1,
                       # committedTrees=1); negative control shared with
                       # V8's _0830 (the shared-evaluator precedent).
                       "V8T-eve-ts-arrival.toml",
                       # V8F: armed 2026-08-11 off its two consecutive
                       # clean runs (_0853/_0854, the five-raise set; four
                       # of five ratios to four decimals, fifth 1 ulp; armed run
                       # _0857); control shared with V8's _0830.
                       "V8F-eve-loop-faithful.toml"}

    def test_no_committed_spec_arms_gating(self):
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            if saveparse.gating_armed(spec.get("expectations") or {}):
                armed.append(name)
        self.assertEqual(sorted(self.ARMED_ALLOWLIST), armed,
                         "the set of specs arming save-structure gating changed; arming is "
                         "a per-scenario operator decision taken only after a report-only "
                         "reading run whose facets match the declared windows - add the "
                         "spec here in the same commit that arms it, citing the run id")

    def test_s41_declares_the_rewind_block_armed(self):
        # S4.1 is the one committed declarer AND (2026-07-31) the one armed spec.
        # Reading run `2026-07-31_1628` measured supersedeRows=0 / tombstones=0
        # against the block's `max = 0` windows; `2026-07-31_1635` then flew PASS
        # armed, and the `min = 1` negative control reddened `2026-07-31_1637`
        # PARSEK-FAIL(save-structure). Flipped from ..._unarmed, which was the
        # verdict-neutrality assertion for the report-only landing.
        spec = load_spec("S4.1-rewind-merge.toml")
        exp = spec["expectations"]
        self.assertEqual(("rewind",), saveparse.declared_structure_blocks(exp))
        self.assertTrue(saveparse.gating_armed(exp))
        # The windows themselves are deliberately untouched by the arming commit:
        # arming must not smuggle in a re-pinned window.
        self.assertEqual({"max": 0}, exp["rewind"]["supersedeRows"])
        self.assertEqual({"max": 0}, exp["rewind"]["tombstones"])
        # ...nor an ADDED one. Pinning only the two VALUES above left a gap:
        # appending e.g. `rewindPoints = { max = 0 }` passed both guard cells,
        # yet that would be a newly ARMED, GATING window with no reading run
        # behind it - and rewindPoints is precisely the key the block's own
        # comment says it declined to pin ("one observation is not a window").
        # Pin the KEY SET so growing the armed block is as deliberate as arming
        # it was.
        self.assertEqual({"gating", "supersedeRows", "tombstones"},
                         set(exp["rewind"]),
                         "a window was added to (or removed from) S4.1's ARMED block; "
                         "every armed window needs its own report-only reading run first")


class RenderComposeVerifierWiringTests(unittest.TestCase):
    """The M-A7 render-composition verifier's hlib-side wiring: spec-surface
    validation routes through validate_spec, the gating flag classifies its own
    PARSEK-FAIL subkind at the right precedence, and - the SAFETY PROPERTY,
    mirroring the save-structure precedent above - arming stays a deliberate,
    per-scenario, live-proven act.

    The guarantee AS SHIPPED was the strong form the save-structure row shipped
    with in 2026-07: NO committed spec arms `gating = true`. THAT PHASE ENDED
    2026-08-25, when the operator armed both Phase-3 declarers off their own
    report-only reading runs, so the guarantee is now the next one along - the
    armed set is an ALLOWLIST, and a spec joining it needs an explicit edit here
    citing its run ids. Row 7c can move a verdict from here on, which is the point
    of arming it.

    NAMING: the roster below is deliberately NOT called `*ARMED_ALLOWLIST`.
    `harness/missions/lib/test_cl3_refly_crew_tombstone.py` scrapes the
    save-structure roster out of THIS FILE'S SOURCE with a first-match
    `ARMED_ALLOWLIST\\s*=\\s*\\{([^}]*)\\}` regex; a second symbol whose name ends
    in `ARMED_ALLOWLIST` would be a coin-flip on file order. A distinct name is
    the fix that does not depend on staying below the other one."""

    # THE ARMED ROSTER. Entries follow the save-structure ARMED_ALLOWLIST
    # convention: name the READING run that the windows were authored from, then
    # the ARMED RE-FLIGHT and the NEGATIVE CONTROL that discharge the three-run
    # workflow (filled in the commit that flies them, not before). A spec joining
    # this set needs its edit here in the same commit that arms it. ALL THREE
    # ENTRIES BELOW ARE DISCHARGED as of 2026-08-25 - the first two flew their six
    # runs that day, and V24W closed its own discipline the same day across SIX
    # flights (three readings, two armed re-flights, one negative control), the
    # second re-flight being a control attempt that never armed.
    RENDERCOMPOSE_ARMED_SPECS = {
        # V14M: ARMED 2026-08-25 off its OWN report-only reading run
        # `2026-08-25_0953` (PASS attempt 1, `renderCompose status=REPORT
        # gating=false armedBlocks=[] mismatches=[]`, one INFO finding, zero WARN,
        # zero FAIL). Windows written FROM that run's facets: dwells {1,32}
        # (measured 3), cycles {1,16} (measured 1 CLOSED cycle), unevaluable
        # {max 200} (measured 56), requireSeamKinds
        # ["rigid","flexible-soi"] (measured rigid 14 / flexible-soi 2). The two
        # floors are the anti-vacuity halves; the ceilings are runaway guards, not
        # pins, because dwell and endpoint counts move with frame timing.
        # `warpBuckets` is NOT declared and never may be on this lane (every clock
        # move is an instantaneous TimeJump, so the histogram is 1x-only by
        # construction). Arming re-pinned NOTHING in the flown shape.
        # ONE PRICED-IN SHIFT: the sticky `mapRenderTracingOn` fix that landed in
        # the same pass removes this lane's spurious
        # `seam-data-unavailable-tracing-off`, so the next run reads 55 rather
        # than 56 unevaluable - inside the declared ceiling by design.
        # ARMED RE-FLIGHT: `2026-08-25_1050` PASS, gating=True,
        # armedBlocks=['renderComposition'], ZERO mismatches - dwells 3, cycles 1,
        # seamKinds {rigid 14, flexible-soi 2}, one INFO RC-QUAL, zero WARN/FAIL.
        # It CONFIRMED the sticky-bit fix (`mapRenderTracingOn=true`, and
        # `seam-data-unavailable-tracing-off` gone from the census). The predicted
        # 55 did NOT land: unevaluable read 108 (seam-endpoint-skipped 106,
        # no-cycle-rollover-events 1, warp-hold-traversal-evidence-absent 1),
        # because seam-endpoint-skipped itself ran 106 against the reading run's
        # 53 - run-to-run endpoint variance, not a regression, and exactly the
        # movement the `{max 200}` runaway-guard ceiling (not a pin) was written to
        # absorb. NEGATIVE CONTROL: `2026-08-25_1052`, temporary
        # `cycles = { min = 5 }`, red on exactly
        # `PARSEK-FAIL(render-composition)` with the single mismatch
        # `renderComposition.cycles 1 < min 5`; every sibling row stayed clean
        # (saveParse / anomalySweep / driverValidity / logValidate / analyzer all
        # PASS), so the red is on THIS lane's own armed clause and not on the
        # shared evaluator. Control reverted in the same change. V8 flew its OWN
        # control rather than sharing this one - see that entry.
        #
        # ** ONE ROUND TRIP, 2026-08-28, COMPLETED - ENTRY RESTORED UNCHANGED. **
        # The watch-entry acceptance change (docs/dev/todo-and-known-bugs.md ->
        # WATCH-ENTRY-REFUSED-INSIDE-QUOTED-RANGE) flipped this lane's two
        # `EnterWatchMode` pins REJECTED -> OK, and a run that ENTERS watch mode
        # force-builds the watched ghost at full fidelity - a composition NO
        # archived run of this lane produced, while every window above was written
        # from runs where both watch attempts were REFUSED. Armed across that, a
        # red would have classified `PARSEK-FAIL(render-composition)` for a
        # MIGRATION reason, so the entry was REMOVED and the spec de-armed.
        # THE READING FLIGHT THEN REMOVED THE PREMISE: `2026-08-28_1932` /
        # `_1933_a2` measured `candidates=[0 ghost=T body=T range=F]` at
        # 449.6/449.9 km - the body term passes, the RANGE term refuses, and this
        # lane still does not enter watch mode - so the pins went back to REJECTED
        # and the flown shape is the one the windows above describe.
        # RE-ARMED off CONFIRMING RUN `2026-08-28_1940` (PASS attempt 1, 57 s,
        # corrected pins, mismatches=0): dwells 3, cycles 1, unevaluable 59,
        # findings FAIL 0 / WARN 0 / INFO 1 - every retained window met, within
        # noise of the arming run's 3 / 1 / 56. NO WINDOW VALUE CHANGED ACROSS THE
        # CYCLE, which is what makes it a RESTORATION rather than a re-pin; the
        # 2026-08-25 three-run discipline recorded above is neither re-run nor
        # re-claimed by it.
        "V14M-ike-player-loop.toml",
        # V8: ARMED 2026-08-25 off its OWN report-only reading run
        # `2026-08-25_0956` (PASS attempt 1, same REPORT/zero-FAIL shape). Windows:
        # dwells {1,32} (measured 2), unevaluable {max 250} (measured 76 - 73 of
        # them `seam-endpoint-skipped` over a 272-record endpoint population, 2.5x
        # V14M's, which is why the ceiling is 250 against that lane's 200 at the
        # same ratio-to-measurement), requireSeamKinds ["rigid","flexible-soi"]
        # (measured rigid 6 / flexible-soi 4).
        # `cycles` DELIBERATELY OMITTED: this subject closed ZERO cycles (one
        # rollover bounding none, `no-cycle-rollover-events: 2`), so a floor would
        # red the run it was armed off and a `{min = 0}` pin can never red at all.
        # `dwells` is this block's anti-vacuity floor. RC-CUT stays unarmed for a
        # MEASURED reason - `cut-run-period-absent: 1`, the manifest carried no run
        # period so the corpus's only loiter cut could not be evaluated - and
        # RC-HOLD because one observed engage/release pair is not a window.
        # `warpBuckets` never, same 1x-only-by-construction reason as V14M.
        # ARMED RE-FLIGHT: `2026-08-25_1051` PASS, gating=True,
        # armedBlocks=['renderComposition'], ZERO mismatches - dwells 2,
        # unevaluable 78 (seam-endpoint-skipped 75, no-cycle-rollover-events 2,
        # cut-run-period-absent 1), seamKinds {rigid 6, flexible-soi 4},
        # `mapRenderTracingOn=true`, one INFO RC-QUAL, zero WARN/FAIL. The reading
        # run's headline reproduced: the `hold-engage`/`hold-release` pair and the
        # `reaim-window` clock event are both there again, so that observation was
        # the subject and not a one-run accident. NEGATIVE CONTROL:
        # `2026-08-25_1054`, temporary `dwells = { min = 50 }`, red on exactly
        # `PARSEK-FAIL(render-composition)` with the single mismatch
        # `renderComposition.dwells 2 < min 50`; every sibling row stayed clean
        # (saveParse / anomalySweep / driverValidity / logValidate / analyzer all
        # PASS). Control reverted in the same change. THE PAIR DID NOT SHARE ONE
        # CONTROL after all: each lane inverted a window of its OWN (V14M `cycles`,
        # this lane `dwells`), which is the stronger discharge - a shared inversion
        # would have re-proven the `rendercompose` evaluator rather than these two
        # blocks, and the two lanes do not even arm the same key set.
        "V8-eve-player-loop.toml",
        # V24W: ARMED 2026-08-25 off TWO MATCHING READINGS rather than one, because
        # this lane's subject IS a histogram and a histogram read once is a sample.
        # READING A `2026-08-25_1502` - the full-measurement run, PARSEK-FAIL(anomaly)
        # attempt 1 by this spec's own PRE-REGISTERED doctrine (its header promised in
        # writing, before any run existed, that a red on `icon-teleport` was the most
        # valuable first measurement and that `allowedAnomalies` would ship empty so
        # the count would be recorded rather than swallowed). Every other verifier row
        # green, all ten driver steps met, `renderCompose status=REPORT gating=false`,
        # four INFO RC-QUAL findings, zero WARN, zero FAIL.
        # READING B `2026-08-25_1616` - the CLEAN PASS re-fly of the unchanged spec
        # with the three tolerances reading A authored. The gate it had to clear was
        # recurrence, and it cleared it EXACTLY: hitCounts {icon-teleport 65,
        # icon-off-orbit 2, loop-seam-teleport 2}, the same three integers, plus the
        # same single report-only `seam-endpoint-outside-soi` echo.
        # THE PAIR MATCHES FACET FOR FACET, which is what the arming rests on: dwells
        # 2 (+2 open) BOTH, cycles 1 BOTH, transitions 2 / chainBuilds 2 /
        # lineBranches 2 / treatments StockConic 2 / coverages InSegment 2 BOTH,
        # seamKinds {rigid 11, flexible-soi 4} BOTH, seamEndpoints 1024 BOTH,
        # seamTangents 0 BOTH, holdsAboveOneX 1 and seamsAboveOneX 2 BOTH, findings
        # 4 x INFO RC-QUAL and nothing worse BOTH. Histogram within 0.5 % bucket for
        # bucket: warp100 10602 -> 10626, warp1000 2170 -> 2160, warpHigh 0 -> 0,
        # warpPhys 0 -> 0, warp1x 322078 -> 322868. unevaluable 334342 -> 335146.
        # WINDOWS: dwells {1,32} (the sibling lanes' identical anti-vacuity floor),
        # unevaluable {max 500000}, requireSeamKinds ["rigid","flexible-soi"], and -
        # THE FIRST IN THE SUITE - warpBuckets ["warp100","warp1000"]. That key is
        # what this lane exists to author: both armed lanes above may NEVER declare it
        # (their clocks are instantaneous TimeJumps, 1x-only by construction), and
        # declaring it also arms RC-WARP's two non-list clauses at FAIL level -
        # `seamsAboveOneX` and `holdsAboveOneX` must be non-zero - both backed twice.
        # The unevaluable ceiling is ~1.5x rather than the siblings' ~3.3x on purpose:
        # 99.8 % of this census is the SEAM_ENDPOINT decimation (293250 decimated +
        # 41380 truncated on reading B), the per-pid cap reporting loudly on a rails
        # drive shape, so the ceiling is an anti-vacuity bound over the decimation
        # regime and tightening it would red on the instrument's own bookkeeping.
        # NOT DECLARED, each for a measured reason: `warpHigh` (0 twice - the
        # commanded ladder tops out at rails index 5, so 1000x IS this subject's
        # ceiling; it is the negative-control token instead), `cycles` (reads 1 twice
        # and a V14M-spelled window WOULD hold, but the closed-cycle count here is a
        # property of three supervisor-chosen windows on a COMPRESSED span clock, not
        # of what this lane contributes - V14M owns that clause), any RC-CUT surface
        # (`cut-run-period-absent: 2` on both runs, V8's constraint reproduced), any
        # RC-HOLD clause (one engage/release pair per run, though the ~18-20 %
        # observed/planned ratio reproduced at both plan units on both runs and is
        # handed to RC-HOLD as a measurement), and any endpoint count window (the
        # population is decimated). Arming re-pinned NOTHING in the flown shape, and
        # claimed D14 `warp-rails` in the same commit - the gate that makes the claim
        # true - while still declining `warp-high`.
        # ARMED RE-FLIGHT: `2026-08-25_1722` PASS attempt 1, gating=True,
        # armedBlocks=['renderComposition'], ZERO mismatches - histogram
        # warp100 10644 / warp1000 2168 / warpHigh 0, dwells 2 (+2 open), seamKinds
        # {rigid 11, flexible-soi 4}, holdsAboveOneX 1, seamsAboveOneX 2, four INFO
        # RC-QUAL and nothing worse, unevaluable 334093. A SECOND armed re-flight
        # rode in by accident: `2026-08-25_1811` was flown as the negative control,
        # its substring edit hit a rationale COMMENT quoting this same key ahead of
        # the real line, and it therefore evaluated the UNINVERTED block - PASS
        # attempt 1, zero mismatches, warp100 10580 / warp1000 2162 / warpHigh 0.
        # Reclassified as a re-flight rather than discarded, because it IS one. The
        # anomaly triple {icon-teleport 65, icon-off-orbit 2, loop-seam-teleport 2}
        # repeated to the integer across ALL FOUR full flights (two readings, two
        # re-flights) - a stronger determinism statement than the arming pair alone.
        # NEGATIVE CONTROL: `2026-08-25_1925`, **`PARSEK-FAIL(render-composition)`
        # attempt 1** off the temporary `warpBuckets = ["warpHigh"]` - a composition
        # token of THIS lane's own that measured 0 on every flight - applied by a
        # LINE-ANCHORED edit of the real key and confirmed pre-launch through
        # `run.py --dry-run`'s `declared:` line. EXACTLY ONE mismatch, and it names
        # the zero-count bucket: `RC-WARP [FAIL] warpBuckets.warpHigh: spec declared
        # warp bucket 'warpHigh' and the manifest counted zero frames in it - the run
        # did not visit that warp regime`. Every sibling row stayed clean
        # (driverValidity / mission / analyzer red=0 / logValidate / anomalySweep /
        # expectations / testResults PASS, saveParse + unityExceptions REPORT), the
        # four INFO RC-QUAL findings stood beside the one FAIL, and the composition
        # facets equalled the PASSing flights' - so the red is the declaration and
        # nothing else. The run JSON's `verifiers.renderCompose.declared` records
        # `warpBuckets: ['warpHigh']`: the audit surface added because of the 1811
        # miss, demonstrated by the control that needed it. Reverted in the same
        # change on the verified real key. Not shared with the two lanes above, for
        # their own stated reason: a shared inversion re-proves the evaluator rather
        # than this block.
        "V24W-duna-one-warp-stair.toml",
        # V25M: ARMED 2026-08-26 off THREE of its OWN report-only readings, all of one
        # unchanged drive shape. READING 1 `2026-08-26_1744` - the full-measurement
        # flight, PARSEK-FAIL(anomaly) attempt 1 by this spec's own PRE-REGISTERED
        # doctrine, and the run that DIAGNOSED the wave-1 RC-SEAM misread (a transition
        # that warped across an interior segment spanned boundaries 7 and 8; the
        # evaluator keyed the seam table on `toSegmentIndex` and blamed boundary 8's
        # correct `rigid` for boundary 7's Sun->Duna change). READING 2
        # `2026-08-26_1817` - red on `line-blink`, since tolerated; it VALIDATED the
        # RC-SEAM fix live (zero FAIL findings where reading 1 raised one). READING 3
        # `2026-08-26_1823` - the CLEAN PASS the arming doctrine requires: dwells 3
        # (+2 open), cycles 0, unevaluable 409, 0 FAIL / 0 WARN, anomalies
        # {icon-teleport 3, icon-off-orbit 2, line-blink 0}.
        # THE STRUCTURE IS EQUAL TO THE INTEGER ON ALL THREE: dwells 3 (+2 open),
        # cycles 0, treatments {StockConic 2, TracedPath 1}, coverages {InSegment 3},
        # seamKinds {rigid 8, flexible-soi 2}. The ONE facet that moved is
        # `unevaluable`: 384 / 410 / 409.
        # WINDOWS: dwells {1,32} (the suite's identical anti-vacuity floor and, on this
        # lane, the ONLY one), unevaluable {max 1400}, requireSeamKinds
        # ["rigid","flexible-soi"]. The ceiling is ~3.4x the largest reading - the SAME
        # ratio-to-measurement the two 1x siblings carry (V14M 200/56, V8 250/76),
        # SCALED to this census rather than copied: 98 % of it is
        # `seam-endpoint-skipped` (308/322/306) plus `reaimed-seam-instant-absent`
        # (69/81/96) over a ~400-record endpoint population, and those two traded 15
        # records between themselves across readings 2 and 3 while their sum barely
        # moved. Four census entries are STRUCTURAL and stay:
        # `plan-primitive-body-unidentified 1` (Sun is not in the stock body table),
        # `hold-observed-evidence-absent 1` + `warp-hold-traversal-evidence-absent 1`
        # (the plan holds 3,918.25 s and every window sits at or before the arrival SOI
        # entry by design), `ownership-publish-surface-never-ran 1` (this lane drives no
        # EnterMapView - V6M is the lane that closed RC-OWN, and opening the map here
        # would be a change to the flown shape that arming may not make).
        # `cycles` DELIBERATELY OMITTED, the V8 shape exactly: zero closed cycles on all
        # three readings, so a floor would red the runs it was armed off and a {min = 0}
        # pin can never red. `warpBuckets` never (1x-only by construction, 468/470
        # frames, every other bucket 0). No RC-CUT window although this lane owns the
        # program's first non-empty `cutWholeRatios = [2.0]` - one ratio from one cut is
        # a headline, not a population - and no RC-HOLD clause
        # (`observedHoldSeconds []`).
        # ARMED RE-FLIGHT: `2026-08-26_1837` PASS attempt 1, gating=True,
        # armedBlocks=['renderComposition'], ZERO mismatches - dwells 3 (+2 open),
        # cycles 0, treatments {StockConic 2, TracedPath 1}, seamKinds {rigid 8,
        # flexible-soi 2}, seamEndpoints 381, unevaluable 388, one INFO RC-QUAL, zero
        # WARN/FAIL. The windows hold against a run they were not written from, and 388
        # sits INSIDE the three readings' spread rather than trending - exactly what the
        # ceiling was sized to absorb.
        # NEGATIVE CONTROL: `2026-08-26_1839`, temporary `dwells = { min = 50 }` applied
        # by a LINE-ANCHORED edit of the real key (verified with `grep -n '^dwells'` AND
        # through `run.py --dry-run`'s `declared:` line before launch - the V24W `_1811`
        # miss is why that second check is mandatory), red on exactly
        # `PARSEK-FAIL(render-composition)` with EXACTLY ONE mismatch,
        # `renderComposition.dwells 3 < min 50`. Every sibling row stayed clean
        # (driverValidity / analyzer red=0 / logValidate / anomalySweep / expectations /
        # testResults PASS, saveParse + unityExceptions REPORT) and the composition
        # facets equalled the PASSing flights', so the red is the declaration and nothing
        # else; the run JSON's `verifiers.renderCompose.declared` records
        # `dwells: {min: 50}`. Reverted in the same change on the re-grepped real key.
        # NOT SHARED with the V6M lane armed alongside it, which inverted `cycles` - a
        # clause this block does not even carry.
        "V25M-duna-park-player-loop.toml",
        # V6M: ARMED 2026-08-26 off a PAIR of its own report-only readings that bracket
        # the one change the lane made between them - the map. READING A
        # `2026-08-25_2056` flew with the map CLOSED and raised THREE report-only RC-OWN
        # FAILs, whose cause was an INSTRUMENT GAP and not a renderer defect: the
        # TracedPath INTENT half runs from ParsekFlight's per-frame update while the
        # PUBLISH half sits past `if (!MapView.MapIsEnabled) return;` at the end of the
        # polyline Driver's LateUpdate, so a lane that never opened the map could draw
        # without ever publishing. READING B `2026-08-26_1745` is the MAP-OPEN re-fly
        # (EnterMapView / ExitMapView, PR #1539) and it is the RC-OWN CLOSURE:
        # `ownershipChanges = 6` (three clean appear/disappear pairs, one per TracedPath
        # dwell), findings ALL ZERO where reading A raised three, and
        # `ownership-publish-surface-never-ran` gone from the census entirely.
        # THE ARMED RE-FLIGHTS ARE MAP-OPEN, so every window is written off READING B
        # for anything the map moves, and off the PAIR for the facets that are equal to
        # the integer across both: dwells 5 (+3 open) BOTH, cycles 2 BOTH, transitions 5
        # BOTH, treatments {StockConic 2, TracedPath 3} BOTH, coverages {InSegment 5}
        # BOTH, lineBranches 11 BOTH, seamKinds {rigid 21, flexible-soi 3} BOTH,
        # seamTangents 0 BOTH, clockEvents {cycle-rollover 3, inter-cycle-tail 2} BOTH.
        # WINDOWS: dwells {1,32} (the suite's identical floor), cycles {min 2, max 16},
        # unevaluable {max 300}, requireSeamKinds ["rigid","flexible-soi"].
        # `cycles = {min = 2}` IS THE CLAUSE THIS LANE EXISTS TO CARRY and the reason it
        # grew a third playback cycle: rendercompose closes N-1 cycles off N
        # `cycle-rollover` events, so three rollovers close TWO, and no other lane in the
        # suite can carry a floor above 1 (V14M closes 1, V8 and V25M close 0). The
        # ceiling is loose for the same reason as `dwells`. THE FLOOR IS NOT VACUOUS
        # HERE, and that had to be checked rather than assumed: `_rule_cycle` builds each
        # window's role structure from the CLOSED dwells whose midpoint falls inside it
        # and compares with a plain `roles[a] == roles[b]`, so two EMPTY role sets would
        # compare equal and say nothing - this lane's 5 closed dwells over 2 closed
        # cycles are what make the isomorphism statement real.
        # `unevaluable = {max 300}` is ~3.6x reading B's 84 - the sibling lanes' ratio -
        # and it also clears reading A's 110 with room, so the window does not depend on
        # which of the two shapes a future run happens to fly. The census is
        # `seam-endpoint-skipped` (83 map-open, 109 map-closed) plus one
        # `warp-hold-traversal-evidence-absent`, i.e. it moves with endpoint population
        # and frame timing, not with correctness.
        # `ownershipChanges` WAS NOT DECLARED AT ARMING and COULD not be - it was a
        # recorded FACET and not a windowable key, and the block validator rejected any
        # other key outright - so the RC-OWN premise (ownership is conserved on this
        # subject) was armed INDIRECTLY: with `gating = true` every FAIL-level rule
        # finding gates, so the three RC-OWN FAILs reading A raised would classify
        # `PARSEK-FAIL(render-composition)`. The schema gap was filed as a harness
        # improvement rather than invented in the arming pass, and CLOSED LATER THE SAME
        # DAY in ANOTHER lane's change (PR #1546, V18T's arming):
        # `RENDER_COMPOSITION_WINDOW_KEYS` now carries `ownershipChanges` plus the two
        # route keys - docs/dev/todo-and-known-bugs.md ->
        # RENDERCOMPOSE-OWNERSHIPCHANGES-IS-NOT-WINDOWABLE. That change deliberately did
        # NOT upgrade this block: an already-armed lane's window set may only grow
        # through its own armed re-flight + negative control, never as a follow-up edit
        # riding someone else's commit.
        # ** UPGRADE 2026-08-26, THAT DEFERRED PASS, FLOWN: `ownershipChanges = { min = 1 }`
        # DECLARED.** Nothing else moved - the four windows above keep their exact values,
        # the flown shape is byte-identical, no jump UT / step / budget / tolerance / other
        # expectation was touched (S4.1 across an upgrade).
        # A FLOOR ONLY, and the floor is the PRE-REGISTERED CRITERION VERBATIM: the
        # header's MAP-OPEN RE-FLY row wrote `ownershipChanges > 0` - "the discriminator
        # is GLOBAL, so ONE record anywhere proves the walk published" - BEFORE any
        # map-open run existed, so `{min = 1}` states the pre-registration rather than a
        # count read back from the runs that satisfied it.
        # NO CEILING, for a measured reason rather than caution. `ownershipChanges` is one
        # appear/disappear PAIR per TracedPath dwell, i.e. 2 x `treatments.TracedPath`,
        # NOT an independent quantity - and `_1840` demonstrates the coupling in a single
        # reading: that flight lost a dwell close (dwells 4, transitions 4, StockConic
        # 2 -> 1) and `ownershipChanges` HELD AT 6 because TracedPath held at 3. A ceiling
        # would therefore re-gate the dwell population `dwells` already governs, red-ing
        # twice for one cause. Same shape and same argument as V18T's
        # `routeLineBuilds = {min = 1}`.
        # THE EVIDENCE, re-read from the archived result JSONs rather than from the spec's
        # own prose: `ownershipChanges = 6` on ALL TWELVE map-open flights the lane has
        # flown (_1745, _1838, _1840, _1842, _1843, _1844, _1918, _1919, _1920, _1925,
        # _1926, _1927) and 0 on the single map-CLOSED reading 2026-08-25_2056. Zero
        # variance across twelve; the one 0 is exactly the condition the facet separates.
        # WHAT THE FLOOR ADDS OVER THE INDIRECT ARMING, which is the whole point: gating
        # already reds an RC-OWN FAIL, but it CANNOT red the STAND-DOWN. A lane that
        # silently stopped opening the map takes RC-OWN to the
        # `ownership-publish-surface-never-ran` unevaluable and GREENS, the census entry
        # absorbed inside the 300 ceiling. Until now that was stated only by a NEGATIVE
        # (a reason absent from the census) and only in the run JSON, never in a
        # declaration. This is the block's first positive assertion that the publish
        # surface RAN.
        # UPGRADE ARMED RE-FLIGHT: `2026-08-26_2042` PASS attempt 1, gating=True,
        # armedBlocks=['renderComposition'], ZERO mismatches, zero findings at every
        # level, `declared` recording `ownershipChanges: {min: 1}` among five assertions.
        # ownershipChanges 6 (the thirteenth map-open reading), dwells 5, cycles 2,
        # transitions 5, treatments {StockConic 2, TracedPath 3}, seamKinds {rigid 21,
        # flexible-soi 3}, unevaluable 79 - inside the ceiling and inside the 75-112
        # spread the four pre-upgrade re-flights set.
        # UPGRADE NEGATIVE CONTROL: `2026-08-26_2043`, temporary
        # `ownershipChanges = { min = 50 }` applied by a LINE-ANCHORED edit of the real key
        # (a python pass asserting EXACTLY ONE line starting `ownershipChanges`, then
        # `grep -n '^ownershipChanges'` AND `run.py --dry-run`'s `declared:` line before
        # launch), red on exactly `PARSEK-FAIL(render-composition)` with EXACTLY ONE
        # mismatch, `renderComposition.ownershipChanges 6 < min 50`. Every sibling row
        # stayed clean (driverValidity / analyzer red=0 / logValidate / anomalySweep /
        # expectations / testResults PASS, saveParse PASS on its own two armed blocks,
        # unityExceptions REPORT). THE SUITE'S FIRST NEGATIVE CONTROL ON `ownershipChanges`
        # itself - not shared with V18T, whose control inverted `routeLineBuilds`.
        # Reverted in the same change on the re-grepped real key, spec byte-identical to
        # the pre-control state.
        # `warpBuckets` never (1x-only by construction, instantaneous TimeJumps).
        # ARMED RE-FLIGHT: DISCHARGED FOUR TIMES - `2026-08-26_1838`, `_1842`, `_1843`,
        # `_1844`, all PASS attempt 1 with gating=True,
        # armedBlocks=['renderComposition'], ZERO mismatches and zero findings at every
        # level. dwells 5 (+3 open), cycles 2, seamKinds {rigid 21, flexible-soi 3} and
        # `ownershipChanges = 6` on ALL FOUR, so the RC-OWN closure is backed by FIVE
        # map-open flights rather than the single re-fly that first made it. unevaluable
        # 91 / 75 / 83 / 112 - scattered inside the 300 ceiling, not trending.
        # NEGATIVE CONTROL: `2026-08-26_1840`, temporary `cycles = { min = 9 }` applied
        # by a LINE-ANCHORED edit of the real key (verified with `grep -n '^cycles'` AND
        # through `run.py --dry-run`'s `declared:` line), red on exactly
        # `PARSEK-FAIL(render-composition)`; the inverted clause named itself
        # (`renderComposition.cycles 2 < min 9`) and every SIBLING VERIFIER ROW stayed
        # clean (saveParse PASS on its own two armed blocks, driverValidity / analyzer
        # red=0 / logValidate / anomalySweep / expectations / testResults PASS).
        # Reverted in the same change on the re-grepped real key. Not shared with V25M,
        # which inverted `dwells`.
        # THE CONTROL ALSO CARRIED A SECOND MISMATCH THAT WAS NOT THE CONTROL, and it is
        # the most valuable thing this arming pass produced. A window edit cannot change
        # the flown shape, yet that one run measured dwells 4, transitions 4, treatments
        # {StockConic 1, TracedPath 3} and an RC-CYCLE FAIL: "cycles 0 and 1 share warp
        # bucket warp1x but their role structures differ: ((1, 'Descent'),) vs
        # ((1, 'ArrivalLoiter'), (1, 'Descent'))". RECURRENCE MEASURED, not guessed: 1 of
        # 6 map-open flights (_1745, _1838, _1840, _1842, _1843, _1844).
        # DIAGNOSED 2026-08-26 offline across all six archived manifests, AND THE
        # DIAGNOSIS REFUTES THE FIRST READING. The dwell is not absent and the renderer is
        # not intermittent: cycle 0's ArrivalLoiter dwell opens at 296690 with minHeadUT
        # 16547.472619832275 and 44-45 frames on ALL SIX flights. What is missing on _1840
        # is its CLOSE - the `TRANSITION ut=560304.47476033552 from=7 to=-1` was never
        # recorded and no segmentIndex=-1 tail dwell opened, so the dwell ran to export
        # with openAtExport=True and `_rule_cycle` (closed dwells only) saw cycle 0 as
        # Descent-only. The `7 -> -1` tail state is observed for 15/10/7/7/4 frames on the
        # green flights and 0 (cycle 0) / 1 (cycle 1) on the red one, which is also the
        # sparsest-sampling flight of the six by total dwell frames (235 vs 245-265). The
        # CLOCK_EVENT inter-cycle-tail at that UT is PRESENT on all six including _1840,
        # so the unit clock reached the tail and only the per-frame surface missed it.
        # NOT an entry-clock race (cycle-0 phase entry UTs are bit-identical across all
        # six, so no jump-target change applies) and NOT renderer intermittency.
        # CONSEQUENCE: the gate would have red ~1-in-6 on a RECORDER bookkeeping gap
        # rather than on a product defect, which undercut the reason the arming row gave.
        # RULING: land the fix and keep the lane armed - done the same day.
        # THE FIX (2026-08-26): RenderCompositionRecorder arms a pending close when it
        # emits the inter-cycle-tail clock event (the path that never misses) and
        # RenderCompositionManifest.FallbackCloseStaleOwnerDwells applies it A FRAME LATER
        # (the two callbacks have no pinned order, so closing at the emission instant
        # would steal the TRANSITION the render path is about to emit), retiring a dwell
        # still open, opened within the ENDING cycle, and last sampled strictly before the
        # event, stamped AT the event UT. A fallback, not a new primary path.
        # THE FIRST CUT WAS WRONG AND THE PROOF FLIGHTS CAUGHT IT: scoped by owner alone
        # it also retired the previous cycle's leftover `-1` dwell (open by design),
        # inventing a (1,'None') role and red-ing _1918/_1919/_1920 at dwells 6.
        # DETERMINISM PROOF: 2026-08-26_1925/_1926/_1927 all PASS attempt 1, dwells 5,
        # cycles 2, zero findings - and DIRECTLY rather than statistically, because the
        # fallback actually FIRED on two of the three (_1925 twice, _1926 once, _1927 not
        # at all) and they passed anyway, with the CLOSED dwell set identical on all three
        # and matching the canonical pre-fix greens to the digit. Sibling check:
        # V25M re-flown armed on the same DLL, 2026-08-26_1929 PASS, windows reproduced,
        # fallback fired zero times (that lane emits no inter-cycle-tail event).
        # Full write-up: docs/dev/todo-and-known-bugs.md ->
        # V6M-CYCLE0-ARRIVALLOITER-DWELL-CLOSE-RECORD-LOST (CLOSED).
        "V6M-mun-player-loop.toml",
        # V18T: ARMED 2026-08-26 off TWO of its own report-only readings, and it is the
        # SUITE'S FIRST ARMED ROUTE LANE. READING 1 `2026-08-26_1741` INVALID attempt 1
        # on a mid-run `LoadGame reason=recording-active` stop/load race (a driver flake,
        # quarantined at rate 0.50, and it says nothing about this lane's subject),
        # `2026-08-26_1742_a2` PASS. READING 2 `2026-08-26_1958` PASS attempt 1 on the
        # unchanged spec.
        # THE PAIR MATCHES FACET FOR FACET: routeLineBuilds 1 / routeCoDrawViolations 0 /
        # routeLegDefers 0, planUnits 1, chainBuilds 1, lineBranches 1, dwells 0 (+1
        # open), transitions 0, cycles 0, ownershipChanges 0, seamKinds {rigid 12},
        # seamTangents 0, clockEvents {cycle-rollover 1}, unevaluable 3 at the SAME three
        # reasons, zero findings at every level. The only facet that moved is the 1x warp
        # frame count (56 -> 59), which is the export instant's frame budget.
        # WINDOWS: routeLineBuilds {min 1} - THE SUITE'S FIRST ROUTE WINDOW and the
        # headline; routeCoDrawViolations {max 0} (the arbitration half); unevaluable
        # {max 10} = ~3.4x measured 3, the SAME ratio-to-measurement the siblings carry
        # (V14M 200/56, V8 250/76, V25M 1400/410, V6M 300/84) scaled to a census that is
        # small only because this lane closes no dwell and decimates no endpoint
        # population; requireSeamKinds ["rigid"] (rigid 12 and nothing else on both -
        # `flexible-soi` is absent BY SCOPE, the route being SameBody Kerbin -> Kerbin).
        # NO `dwells` AND NO `cycles` FLOOR, and THE SUITE'S SHARED `dwells {1,32}`
        # CONVENTION DOES NOT FIT THIS LANE - stated rather than copied. A single-epoch
        # tracking-station observation closes no dwell and rolls over no cycle by
        # construction (both read 0 twice), so either floor would red the two green runs
        # the block was armed off, and a {min = 0} pin can never red at all. The
        # anti-vacuity job those keys do on a loop lane is done here by `routeLineBuilds`.
        # `warpBuckets` never (two instantaneous TimeJumps, 1x-only by construction).
        # `ownershipChanges` not declared although it is now windowable: measured 0 twice
        # and correctly so - the TS host publishes no TracedPath ownership here - and a
        # {max = 0} would assert the absence of a surface this lane does not exercise.
        # THE THREE ROUTE/OWNERSHIP WINDOW KEYS DID NOT EXIST BEFORE THIS CHANGE:
        # `RENDER_COMPOSITION_WINDOW_KEYS` was exactly ("dwells","cycles","unevaluable")
        # and the validator refused anything else pre-launch. The schema extension (todo
        # RENDERCOMPOSE-OWNERSHIPCHANGES-IS-NOT-WINDOWABLE) ships in the same change, and
        # this lane's armed re-flight + negative control are its LIVE PROOF end to end.
        # ARMED RE-FLIGHT: `2026-08-26_2015` PASS attempt 1, gating=True,
        # armedBlocks=['renderComposition'], ZERO mismatches, zero findings at every
        # level - routeLineBuilds 1, routeCoDrawViolations 0, routeLegDefers 0, planUnits
        # 1, chainBuilds 1, lineBranches 1, dwells 0 (+1 open), cycles 0, unevaluable 3 at
        # the same three reasons, seamKinds {rigid 12}. THE THIRD READING OF THE CENSUS IS
        # EQUAL TO THE FIRST TWO ON EVERY FACET, against a run the windows were not
        # written from.
        # NEGATIVE CONTROL: `2026-08-26_2017_a2`, temporary `routeLineBuilds = { min = 5 }`
        # applied by a LINE-ANCHORED edit of the real key (a python pass asserting EXACTLY
        # ONE line starting `routeLineBuilds`, then `grep -n '^routeLineBuilds'` AND
        # `run.py --dry-run`'s `declared:` line before launch - the V24W `_1811` miss is
        # why the second check is mandatory). Red on exactly
        # `PARSEK-FAIL(render-composition)` with EXACTLY ONE mismatch,
        # `renderComposition.routeLineBuilds 1 < min 5`. Every sibling row stayed clean
        # (driverValidity / analyzer red=0 / logValidate / anomalySweep / expectations /
        # testResults PASS, saveParse + unityExceptions REPORT), zero findings at every
        # level stood beside the one mismatch, and the composition facets equalled the
        # PASSing flights' - so the red is THIS lane's own declaration and not the shared
        # evaluator. THE CONTROL IS ALSO THE LIVE END-TO-END PROOF OF THE SCHEMA
        # EXTENSION: `routeLineBuilds` is a key that did not exist before this change, and
        # it flew, validated pre-launch, evaluated, and red on its own mismatch spelling.
        # Attempt 1 (`2026-08-26_2016`) INVALID(driver-verdict-mismatch) on the SAME known
        # flake reading 1 hit - step 15 id=0016 `LoadGame` REJECTED after the preceding
        # StopRecording, the stop/load race - and the retry policy absorbed it. Not a
        # composition fact. Control reverted in the same change on the re-grepped real key
        # (byte-identical to HEAD after the revert, checked rather than assumed).
        # Not shared with any sibling: each armed lane inverts a window of its own.
        "V18T-depot-route-ts-arrival.toml",
    }

    def test_no_committed_spec_arms_render_composition_gating(self):
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            if rendercompose.gating_armed(spec.get("expectations") or {}):
                armed.append(name)
        self.assertEqual(sorted(self.RENDERCOMPOSE_ARMED_SPECS), armed,
                         "the set of specs arming render-composition gating changed; arming "
                         "is a per-scenario operator decision taken only after a report-only "
                         "reading run whose facets match the declared windows - add the spec "
                         "here in the same commit that arms it, citing the run id")

    def test_gating_mismatch_classifies_render_composition_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["render_composition_mismatch"] = True
        verdict = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(("PARSEK-FAIL", "render-composition"),
                         (verdict.verdict, verdict.subkind))
        self.assertIn("render-composition", hlib.PARSEK_FAIL_SUBKINDS)

    def test_absent_flag_is_a_pass_not_a_fail(self):
        """The report-only default in the classifier: with the flag ABSENT (every
        run today, since run.py only sets it under `if rc.gating:`) the verdict is
        an ordinary PASS. A `verifiers.get(..., True)` slip would red every run."""
        d, v = _clean_pass_facts()
        self.assertNotIn("render_composition_mismatch", v)
        self.assertEqual("PASS", hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").verdict)
        # False is likewise a PASS: a GATING row that evaluated clean.
        v["render_composition_mismatch"] = False
        self.assertEqual("PASS", hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").verdict)

    def test_precedence_after_save_structure_and_before_ledger(self):
        """Placement is the claim: a run that reds BOTH structurally and
        compositionally is named by the SAVE-structure subkind (the structure is
        upstream of what the map drew), and a run that reds BOTH compositionally
        and on the ledger is named by render-composition."""
        d, v = _clean_pass_facts()
        v["save_structure_mismatch"] = True
        v["render_composition_mismatch"] = True
        self.assertEqual("save-structure",
                         hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").subkind)
        d, v = _clean_pass_facts()
        v["render_composition_mismatch"] = True
        v["ledger_drift"] = True
        self.assertEqual("render-composition",
                         hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").subkind)

    def test_parsek_fail_is_never_retried(self):
        verdict = hlib.Verdict("PARSEK-FAIL", "render-composition", False, "x")
        self.assertFalse(hlib.should_retry(verdict, 1, "once"))

    # -- validate_spec routing -------------------------------------------------

    def _spec_with(self, block):
        spec = copy.deepcopy(load_spec("B1-pad-hop.toml"))
        spec["expectations"][rendercompose.RENDER_COMPOSITION_BLOCK] = block
        return spec

    def test_malformed_block_rejects_pre_launch(self):
        """A malformed window must never launch KSP: the run would green having
        evaluated a no-op. Routed through rendercompose's own validator so the
        validator and the evaluator share one vocabulary."""
        res = hlib.validate_spec(self._spec_with({"dwells": "lots"}), load_registry())
        self.assertFalse(res.ok)
        self.assertTrue(any("renderComposition" in e for e in res.errors), res.errors)

    def test_unknown_key_rejects_pre_launch(self):
        res = hlib.validate_spec(self._spec_with({"minDwells": 3}), load_registry())
        self.assertFalse(res.ok)
        self.assertTrue(any("unknown key" in e for e in res.errors), res.errors)

    def test_unknown_warp_bucket_token_rejects_pre_launch(self):
        res = hlib.validate_spec(self._spec_with({"warpBuckets": ["warp1x", "warpNope"]}),
                                 load_registry())
        self.assertFalse(res.ok)
        self.assertTrue(any("warpBuckets" in e for e in res.errors), res.errors)

    def test_armed_with_no_assertion_rejects_pre_launch(self):
        """Anti-vacuity: an ARMED block asserting nothing is a gate that can never
        red - the most expensive kind of green."""
        res = hlib.validate_spec(self._spec_with({"gating": True}), load_registry())
        self.assertFalse(res.ok)

    def test_unarmed_assertionless_block_is_a_warning_not_an_error(self):
        """The reading-run state the arming workflow MANDATES: a bare
        `[expectations.renderComposition]` declares nothing, arms the recorder at
        launch, and reports the measured facets. Hard-rejecting it would make the
        prescribed first step impossible."""
        res = hlib.validate_spec(self._spec_with({}), load_registry())
        self.assertTrue(res.ok, res.errors)
        self.assertTrue(any("renderComposition" in w for w in res.warnings), res.warnings)

    def test_a_well_formed_report_only_block_validates(self):
        block = {"dwells": {"min": 1}, "unevaluable": {"max": 4},
                 "warpBuckets": ["warp1x"], "requireSeamKinds": ["rigid"]}
        spec = self._spec_with(block)
        res = hlib.validate_spec(spec, load_registry())
        self.assertTrue(res.ok, res.errors)
        # ... and it is DECLARED without being ARMED: the two are distinct facts,
        # and only the second can move a verdict.
        exp = spec["expectations"]
        self.assertEqual(("renderComposition",),
                         rendercompose.declared_composition_blocks(exp))
        self.assertFalse(rendercompose.gating_armed(exp))

    # The DECLARER roster - distinct from the ARMED roster above, and a strictly
    # wider set: DECLARING is what sets PARSEK_RENDER_MANIFEST=1 at launch, ARMING
    # is what lets the row move a verdict. Phase 3 (2026-08-25) wired the two lanes
    # the design names as first subjects in "Harness integration and lanes":
    #
    #   V14M-ike-player-loop.toml - the phase-lock moon loop (V6/V14 class).
    #   V8-eve-player-loop.toml   - the re-aim interplanetary landing loop
    #                               (V8/V13 class: re-aim + descent trigger + holds).
    #
    # Both were BARE declarations through their report-only reading runs, which FLEW
    # 2026-08-25 (`2026-08-25_0953_V14M-ike-player-loop` and
    # `2026-08-25_0956_V8-eve-player-loop`, both PASS, both `renderCompose
    # status=REPORT gating=false`). BOTH ARE NOW ARMED off exactly those facets - the
    # operator's call, taken 2026-08-25 - and both appear in
    # RENDERCOMPOSE_ARMED_SPECS above with their windows and with the armed
    # re-flight + negative control that DISCHARGED the three-run workflow the same
    # day (V14M `_1050` / `_1052`, V8 `_1051` / `_1054`; each lane inverted a
    # window of its own). Facets live in each spec's arming ledger and in
    # docs/dev/autotest-status.md -> M-A7. Both arm the three tracers the seam capture
    # needs, and both drive ExportRenderManifest once after their last observation
    # step and immediately before FlushAndQuit. SIX cells stand on this roster: the
    # roster itself, that the declarer set and the armed set agree with the two
    # recorded rosters, that every declarer arms the three tracers, that every
    # declarer exports immediately before teardown, and the THREE per-lane armed
    # KEY-SET pins - because a declarer that flies with the tracer off, or exports at
    # the wrong instant, greens while measuring nothing, and an armed block that
    # GROWS a window silently arms a clause no reading run stands behind.
    #
    # THE THIRD DECLARER, added 2026-08-25 and ARMED THE SAME DAY:
    #
    #   V24W-duna-one-warp-stair.toml - the RC-WARP lane, and the ONLY lane in the
    #       suite entitled to declare `warpBuckets` (both lanes above say in their
    #       own headers that the key may NEVER appear on them, their clocks being
    #       instantaneous TimeJumps whose histogram is 1x-only by construction).
    #       THREE READINGS FLEW 2026-08-25: `_1415` (empty observation, both root
    #       causes outside Parsek's render pipeline, fixed), `_1502` (the full
    #       measurement, red by this spec's own pre-registered anomaly doctrine),
    #       and `_1616` (the clean PASS re-fly of the unchanged spec with the three
    #       tolerances `_1502` authored). ARMED off the LAST TWO as a MATCHING PAIR
    #       rather than off a single green reading - the deviation from the V14M/V8
    #       precedent is deliberate and is stated in the roster entry: this lane's
    #       subject is a histogram, and a histogram read once is a sample. The pair
    #       matched facet for facet with the histogram inside 0.5 % bucket for
    #       bucket. It declared the block from its first commit because DECLARING is
    #       what sets PARSEK_RENDER_MANIFEST=1 at launch, and a lane whose whole
    #       purpose is the warp histogram must be capturing a manifest on its first
    #       flight or that flight measures nothing.
    #       ITS THREE-RUN WORKFLOW IS DISCHARGED, across SIX flights: the armed
    #       re-flight flew TWICE (`_1722` and `_1811`, both PASS attempt 1 with
    #       gating=True and zero mismatches - `_1811` was meant to be the control
    #       and never armed, so it counts as a re-flight), and the negative control
    #       flew as `_1925`, `PARSEK-FAIL(render-composition)` attempt 1 on the
    #       single mismatch `RC-WARP [FAIL] warpBuckets.warpHigh`, reverted in the
    #       same change - see the roster entry above for the full ids and facets.
    #
    # THE FOURTH DECLARER, added 2026-08-25, BARE and UNARMED:
    #
    #   V6M-mun-player-loop.toml - the MUN half of the "phase-lock moon loop (V6/V14
    #       class)" subject the design names, and the suite's FIRST TWO-CLOSED-CYCLE
    #       render-composition dataset. Two things make it a distinct subject rather
    #       than a second copy of V14M. (1) It is PAD-ROOTED with TWO constraints and a
    #       NON-UNIFORM ZERO-DRIFT SCHEDULE (`zeroDrift=yes`, faithful-k series 13, 26,
    #       45, 58), where V14M is orbit-rooted, single-constraint and uniform-cadence -
    #       so RC-CYCLE's `cycleLengthResidualsSeconds` trend is a genuinely different
    #       surface here. (2) It ENTERS THREE CYCLES. `rendercompose._cycle_windows`
    #       pairs consecutive `cycle-rollover` events, so N rollovers close N-1 cycles
    #       and `_rule_cycle` skips a unit with fewer than two closed windows as
    #       `no-cycle-rollover-events`; every loop lane in the suite flies two cycles
    #       and closes ONE (V14M measured exactly that), so RC-CYCLE has never compared
    #       two structures anywhere. Three entered cycles close two and give the rule
    #       its first comparison - same warp bucket (warp1x), so the sharp FAIL-level
    #       clause rather than the cross-bucket INFO one.
    #       The spec's cycle-3 anchor is DERIVED (k=45 -> relaunchUt 969,758.553) off
    #       the same replay of `TryFindNextScheduleK` that reproduces this lane's two
    #       MEASURED anchors to the digit.
    #       ITS READING RUN FLEW 2026-08-25 (`2026-08-25_2056_V6M-mun-player-loop`,
    #       PASS, REPORT): `cycles = 2` off three `cycle-rollover` events - RC-CYCLE's
    #       FIRST real evaluation anywhere - and the two closed cycles came back
    #       ISOMORPHIC in the same `warp1x` bucket. THE ARMING PASS IS STILL OWED, so
    #       the lane is deliberately NOT in RENDERCOMPOSE_ARMED_SPECS and its block is
    #       still bare; windows get written FROM those facets, at arming, by operator
    #       decision, and the first clause should be `cycles = { min = 2 }`.
    #
    # PHASE 3C, WAVE A (2026-08-26): FIFTEEN MORE DECLARERS IN ONE PASS, ALL BARE.
    #
    # The wave plan is in docs/dev/todo-and-known-bugs.md -> "PHASE 3C". Its premise is
    # that the rollout is DECLARATION, not new machinery: the B-flights already produce
    # the recordings, the V-lanes already loop them at new UTs, and the manifest already
    # audits the composed render - so the corpus-wide step is to arm the recorder
    # everywhere it can honestly measure something and let readings accumulate off the
    # normal tier cadence (Wave B), arming in batches off those facets (Wave C).
    #
    # NOTHING IN THIS WAVE ARMS ANYTHING. Each lane gained exactly three things: the
    # `ExportRenderManifest` step immediately before `FlushAndQuit`, the bare block last
    # in `[expectations]`, and - on the TWO HOST LANES ONLY - the `ghostRenderTracing`
    # step they were missing. No step, no jump UT, no budget and no existing expectation
    # moved anywhere (the S4.1 rule), and each lane's header carries a compact
    # renderComposition arming ledger recording the posture and the reading debt.
    #
    # THE ONE EXPOSURE CHANGE IN THE WAVE, named because it is the thing a reader must
    # not discover from a red: V14T and V22K previously armed only `mapRenderTracing` +
    # `verboseLogging`. `test_every_declarer_arms_the_tracers_the_seam_capture_needs`
    # requires all three of every declarer, so both gained `ghostRenderTracing`. On those
    # two lanes the third tracer buys NO manifest content - every capture predicate the
    # recorder reads is `MapRenderTrace.IsEnabled`-gated (the seam-tangent evaluation in
    # GhostTrajectoryPolylineRenderer, MapRenderProbe's truth + endpoint census) - so what
    # it adds is an anomaly SURFACE that was previously dark: the FLIGHT phase each lane
    # runs before its TS / KSC re-entry can now raise the `ghostRenderTracing`-gated
    # family (`loop-seam-teleport` and the GhostRenderTrace raises), and both lanes run a
    # tight sweep (V22K `allowedAnomalies = []`, V14T tolerating only `icon-off-orbit`).
    # A red on either reading run from a newly gated raise is a TRACER-ARMING READING to
    # record, not a regression to re-diagnose. They are in the wave anyway because they
    # are the FIRST tracking-station-host and FIRST KSC-host manifests the module has
    # ever been able to take, and the KSC host in particular composes nothing anyone has
    # measured (it draws no trajectory at all - it resolves a pose and places a ghost).
    #
    # THE FOUR ARM-ONLY LANES (V9 / V11 / V12 / V13) will read THIN and that is their
    # point: each quits ~1 s after `StartLoopPlayback`, so their manifests are the
    # corpus's FLOOR case - what a composition looks like with the loop armed and nothing
    # yet rendered. Read them as the baseline the dwell lanes are measured against; an
    # empty reading there is a measurement, not an instrument failure (V24W's reading
    # flight 1 is the precedent, and its two root causes were both outside the render
    # pipeline).
    #
    # ONE MORE EXPECTED-THIN CLASS, recorded so an arming pass does not misread it:
    # V22M and V23M are LANDED-TERMINAL subjects, and the landed-terminal render policy
    # their own lens-calibration rounds measured is flight-mesh only (no map / TS proto
    # in the terminal sliver). A thin ownership half on those two is the expected
    # reading.
    RENDERCOMPOSE_DECLARER_SPECS = {"V14M-ike-player-loop.toml",
                                    "V8-eve-player-loop.toml",
                                    "V24W-duna-one-warp-stair.toml",
                                    "V6M-mun-player-loop.toml",
                                    # -- Phase 3C Wave A, 2026-08-26: all bare, all
                                    # reading-pending. Render host in brackets.
                                    # [M] watch-mode entry on a Kerbin->Minmus loop.
                                    "V7M-minmus-player-loop.toml",
                                    # [M] arm-only floor case, Dres fixture.
                                    "V9-dres-player-loop.toml",
                                    # [M] the Dres re-aimed arrival; synthesizer runs.
                                    "V10-dres-loop-arrival.toml",
                                    # [M] arm-only floor case, Moho fixture.
                                    "V11-moho-player-loop.toml",
                                    # [M] arm-only floor case, Eeloo fixture.
                                    "V12-eeloo-player-loop.toml",
                                    # [M] arm-only floor case, Jool B22 fixture.
                                    "V13-jool-player-loop.toml",
                                    # [M] smallest-SOI arrival body in the corpus.
                                    "V15M-gilly-player-loop.toml",
                                    # [M] eight-bracket Jool-system drive.
                                    "V16M-laythe-player-loop.toml",
                                    # [M] moon-to-moon, nested SOI, routing H3.
                                    "V17M-laythe-vall-player-loop.toml",
                                    # [M] return-direction loop (target is the parent).
                                    "V19M-laythe-jool-player-loop.toml",
                                    # [M] two-moon lane, StockConic-lens re-pin round.
                                    "V21M-mun-minmus-player-loop.toml",
                                    # [M] landed-terminal; expect a thin ownership half.
                                    "V22M-kerbin-splashdown-player-loop.toml",
                                    # [M] landed-terminal; expect a thin ownership half.
                                    "V23M-mun-landing-player-loop.toml",
                                    # [T] FIRST tracking-station-host manifest ever;
                                    #     ghostRenderTracing NEWLY armed (see the
                                    #     exposure paragraph above).
                                    "V14T-ike-ts-arrival.toml",
                                    # [K] FIRST KSC-host manifest ever; ghostRenderTracing
                                    #     NEWLY armed (see the exposure paragraph above).
                                    "V22K-kerbin-splashdown-ksc-arrival.toml",
                                    # -- THE V20 PAIR, 2026-08-27: BOTH BARE, both
                                    # reading-pending, and both NEW LANES authored against
                                    # the `kerbin-return-recorded` harvest rather than
                                    # re-declarations of a lane that had already flown - so
                                    # each owes a FIRST FLIGHT before it owes a window.
                                    # [M] the suite's first KERBIN-ARRIVAL loop subject and
                                    #     by far its longest span (32,606,575.77 s = 3.54
                                    #     Kerbin years, 2,719x V19M's), so the manifest's
                                    #     per-leg composition is measured over an ownership
                                    #     window no prior declarer has produced.
                                    "V20M-jool-kerbin-player-loop.toml",
                                    # [T] the TS half of the same pair. ghostRenderTracing
                                    #     is NEWLY armed here relative to V19T, which is not
                                    #     a declarer - V14T's TS-host precedent and its
                                    #     exposure note apply unchanged.
                                    "V20T-jool-kerbin-ts-arrival.toml",
                                    # -- PHASE 4 / WAVE B, 2026-08-26: TWO NEW SUBJECTS,
                                    # both bare, both reading-pending, and neither a
                                    # re-declaration of an existing shape. Unlike Wave A -
                                    # which declared the block on lanes that had already
                                    # flown - these are NEW LANES authored against the two
                                    # fixtures the Phase-4 harvest landed
                                    # (`depot-route-recorded`, `duna-park-recorded`), so
                                    # each owes a FIRST FLIGHT before it owes a window.
                                    #
                                    # [T] THE SUITE'S FIRST ROUTE LANE, and the only
                                    #     subject that can put a non-zero number in
                                    #     `routeLineBuilds` or produce a per-unit `ROUTE`
                                    #     node at all - so `rendercompose._rule_route` has
                                    #     never executed against live data. It arms NO
                                    #     mission loop: the committed ROUTE drives,
                                    #     through SelectGhostDrivingBackingMissions ->
                                    #     RouteBackingMission.BuildMission -> the TS host
                                    #     union. It flies the TS half FIRST on a measured
                                    #     fact - RouteTrajectoryLineRenderer.DrawAll's
                                    #     only call site carries no MapView gate, so the
                                    #     tracking station draws route lines without
                                    #     EnterMapView while its own FLIGHT prelude draws
                                    #     none. D10 `route-map-lines` stays UNDECLARED
                                    #     until a gating token earns it (H35).
                                    "V18T-depot-route-ts-arrival.toml",
                                    # [M] THE SECOND RE-AIM DEPARTURE CLASS. Every prior
                                    #     re-aim subject (V2 / V8 / V10 / V24W) is a
                                    #     direct ejection; this one phases on the SUN for
                                    #     13,502,219.94 s and only then burns for Duna -
                                    #     the heliocentric-parking departure that
                                    #     ReaimClassifier's own exception comment names
                                    #     ("EXCEPTION (s15 Kerbal X #2)") and that no
                                    #     committed lane has driven. Its manifest is also
                                    #     the first to carry a DESTINATION-side loiter cut
                                    #     (43,963.92 s at the Duna capture) rather than
                                    #     V8's launch-side one.
                                    "V25M-duna-park-player-loop.toml",
                                    # -- PHASE 4, 2026-08-26: ONE NEW **PRODUCER** LANE,
                                    # bare and reading-pending, and the first declarer
                                    # that is not a V-lane at all.
                                    #
                                    # [-] THE FIRST INBOUND INTERPLANETARY SUBJECT: a
                                    #     Jool-rooted recording that ARRIVES AT KERBIN,
                                    #     closing the planet-to-Kerbin half of G2. It is a
                                    #     FLIGHT-scene PRODUCER and claims no render-host
                                    #     dimension, but it arms ALL THREE tracers like
                                    #     every other declarer: the seam capture is
                                    #     `MapRenderTrace.IsEnabled`-gated, so declaring
                                    #     without them would green while measuring
                                    #     nothing. `ghostRenderTracing` and
                                    #     `mapRenderTracing` are NEWLY armed here, and the
                                    #     exposure costs no re-baseline because the lane
                                    #     has no flown shape to perturb.
                                    #     WHY DECLARE A PRODUCER AT ALL: this is the only
                                    #     lane that will ever take a manifest on a
                                    #     recording whose ARRIVAL BODY is Kerbin while its
                                    #     FIRST POINT is not - the exact pair
                                    #     `ParsekKSC.IsKscStructurallyEligible`
                                    #     discriminates on, and the question V20K exists
                                    #     to settle. NEVER FLOWN, so it owes a first
                                    #     flight before it owes a window.
                                    "B29-jool-kerbin-return.toml"}

    def test_render_composition_declarers_are_the_recorded_roster(self):
        """Pinned so a declarer is always a deliberate edit, in BOTH directions. A
        spec picking the block up by accident silently changes what its KSP does at
        boot; a spec LOSING the block silently stops writing the manifest its own
        ExportRenderManifest step expects (which the coupling rule then refuses
        pre-launch, but by then the roster here has already gone stale)."""
        declarers = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            if rendercompose.declared_composition_blocks(spec.get("expectations") or {}):
                declarers.append(name)
        self.assertEqual(sorted(self.RENDERCOMPOSE_DECLARER_SPECS), declarers,
                         "the set of specs declaring [expectations.renderComposition] "
                         "changed; those specs' KSP boots with PARSEK_RENDER_MANIFEST=1 - "
                         "record the decision here in the same commit")

    def test_every_declarers_arming_state_matches_the_recorded_rosters(self):
        """REPLACES `test_no_declarer_arms_gating_yet`, per that cell's own stated
        discipline: it asserted "today the second roster is empty", and 2026-08-25 is
        the day that stopped being true - both declarers were armed off their own
        report-only reading runs. Weakening the cell to nothing, or deleting it, would
        drop the only statement tying the two rosters together, so it becomes the
        NEXT guarantee along: a declarer is armed IF AND ONLY IF it is named in
        RENDERCOMPOSE_ARMED_SPECS, and no non-declarer may be listed there at all.

        The roster IS the arming record now, which is why this is stated against the
        two rosters rather than against the scenario directory (the directory sweep is
        `test_no_committed_spec_arms_render_composition_gating`'s job, and it is the
        cell that catches an arming with no roster edit)."""
        self.assertLessEqual(
            set(self.RENDERCOMPOSE_ARMED_SPECS), set(self.RENDERCOMPOSE_DECLARER_SPECS),
            "a spec is in RENDERCOMPOSE_ARMED_SPECS without declaring the block; "
            "arming without a declaration cannot happen - the block IS the declaration")
        for name in sorted(self.RENDERCOMPOSE_DECLARER_SPECS):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            armed = rendercompose.gating_armed(spec.get("expectations") or {})
            self.assertEqual(
                name in self.RENDERCOMPOSE_ARMED_SPECS, armed,
                "%s: `gating` in the spec and membership of RENDERCOMPOSE_ARMED_SPECS "
                "disagree; arming is a per-scenario operator decision and the roster "
                "entry (with its reading-run id and its armed-run / negative-control "
                "run ids) is where that decision is recorded" % name)

    # -- the per-lane ARMED KEY-SET pins --------------------------------------
    #
    # `test_s41_declares_the_rewind_block_armed`'s shape, and for its reason: pinning
    # only the VALUES leaves a gap where APPENDING a window passes every guard cell
    # while arming a gating clause that no reading run stands behind. Pin the KEY SET
    # so growing an armed block is as deliberate as arming it was. The values are
    # pinned beside them because the arming commit must not smuggle in a re-pin
    # either (the S4.1 rule, stated in both spec headers).

    def _armed_block(self, name):
        spec = load_spec(name)
        exp = spec["expectations"]
        self.assertEqual(("renderComposition",),
                         rendercompose.declared_composition_blocks(exp))
        self.assertTrue(rendercompose.gating_armed(exp))
        return exp[rendercompose.RENDER_COMPOSITION_BLOCK]

    def test_v14m_declares_the_render_composition_block_armed(self):
        """V14M, ARMED 2026-08-25 off reading run `2026-08-25_0953`: dwells 3,
        cycles 1 closed, unevaluable 56, seamKinds {rigid 14, flexible-soi 2}. Every
        window below is that measurement with a stated margin.

        ONE ROUND TRIP, 2026-08-28, AND THE WINDOWS SURVIVED IT UNCHANGED. The
        watch-entry acceptance change de-armed this block (its `EnterWatchMode` pins
        were flipped REJECTED -> OK, and a run that enters watch mode composes
        differently); the reading flight then measured that the lane still does NOT
        enter watch mode, the pins went back, and confirming run `2026-08-28_1940`
        re-measured dwells 3 / cycles 1 / unevaluable 59 - every window below met,
        within noise of the arming run. So the pins here are the SAME NUMBERS they
        have been since 2026-08-25, and this cell asserts them for the same reason it
        always did. See RENDERCOMPOSE_ARMED_SPECS for the cycle's run ids."""
        block = self._armed_block("V14M-ike-player-loop.toml")
        self.assertEqual({"gating", "dwells", "cycles", "unevaluable",
                          "requireSeamKinds"}, set(block),
                         "a window was added to (or removed from) V14M's ARMED "
                         "render-composition block; every armed window needs its own "
                         "report-only reading run behind it")
        self.assertEqual({"min": 1, "max": 32}, block["dwells"])
        self.assertEqual({"min": 1, "max": 16}, block["cycles"])
        self.assertEqual({"max": 200}, block["unevaluable"])
        self.assertEqual(["rigid", "flexible-soi"], block["requireSeamKinds"])
        # `warpBuckets` may NEVER be declared here: every clock move on this lane is
        # an instantaneous TimeJump, so RC-WARP's histogram is 1x-only by
        # construction and the key would pin the drive shape, not the product.
        self.assertNotIn("warpBuckets", block)

    def test_v8_declares_the_render_composition_block_armed_without_a_cycles_floor(self):
        """V8, ARMED 2026-08-25 off reading run `2026-08-25_0956`: dwells 2,
        unevaluable 76, seamKinds {rigid 6, flexible-soi 4} - and cycles 0.

        THE OMISSION IS THE POINT and is pinned as such. This subject closed ZERO
        cycles (one `cycle-rollover` bounding none), so a `min = 1` floor would red
        the run the block was armed off, and a `{ min = 0 }` pin can never red at all
        (`_validate_armed_unreddable` refuses that shape). `dwells` carries the
        anti-vacuity floor instead. A later edit that "completes" the block by adding
        a cycles window must come with a reading run that measured one."""
        block = self._armed_block("V8-eve-player-loop.toml")
        self.assertEqual({"gating", "dwells", "unevaluable", "requireSeamKinds"},
                         set(block),
                         "a window was added to (or removed from) V8's ARMED "
                         "render-composition block; every armed window needs its own "
                         "report-only reading run behind it")
        self.assertEqual({"min": 1, "max": 32}, block["dwells"])
        self.assertEqual({"max": 250}, block["unevaluable"])
        self.assertEqual(["rigid", "flexible-soi"], block["requireSeamKinds"])
        self.assertNotIn("cycles", block)
        self.assertNotIn("warpBuckets", block)

    def test_v24w_declares_the_render_composition_block_armed_with_the_warp_buckets(self):
        """V24W, ARMED 2026-08-25 off the PAIR `2026-08-25_1502` (full measurement,
        pre-registered anomaly red) + `2026-08-25_1616` (clean PASS re-fly): dwells 2
        (+2 open), cycles 1, unevaluable 334342 / 335146, seamKinds
        {rigid 11, flexible-soi 4}, and the histogram warp100 10602/10626 and
        warp1000 2170/2160 with warpHigh and warpPhys ZERO on both.

        THE `warpBuckets` LIST IS THE POINT OF THIS CELL and is pinned exactly. It is
        the FIRST and ONLY armed occurrence of the key in the suite - both sibling
        pins above assert `assertNotIn("warpBuckets", block)`, because those lanes'
        clocks are instantaneous TimeJumps whose histogram is 1x-only by construction.
        Declaring it is also what arms RC-WARP's two non-list clauses at FAIL level
        (`rendercompose._rule_warp`: `seamsAboveOneX == 0` and `holdsAboveOneX == 0`
        become findings only when a bucket list is declared), both backed twice at 2
        and 1 - so GROWING or SHRINKING this list changes what the lane asserts about
        the product, not just about itself.

        `warpHigh` IS PINNED ABSENT, and the absence is a MEASUREMENT: it read 0 on
        both readings because the commanded ladder tops out at KSP rails index 5
        (1000x), so requiring it would red the runs the block was armed off. It is
        this lane's NEGATIVE-CONTROL token instead, which is exactly why a later edit
        must not quietly promote it into the required list.

        `cycles` IS PINNED ABSENT for a different reason from V8's: the facet reads 1
        CLOSED cycle on both runs, so V14M's `{min 1, max 16}` window WOULD hold here.
        It is declined because the closed-cycle count on this lane is a property of
        three supervisor-chosen dwell windows on a COMPRESSED span clock and of the
        export instant - the drive shape under active development - rather than of
        what this lane contributes. `dwells` and `requireSeamKinds` carry the
        anti-vacuity floors."""
        block = self._armed_block("V24W-duna-one-warp-stair.toml")
        self.assertEqual({"gating", "dwells", "unevaluable", "warpBuckets",
                          "requireSeamKinds"}, set(block),
                         "a window was added to (or removed from) V24W's ARMED "
                         "render-composition block; every armed window needs its own "
                         "reading run behind it - and this lane's arming rests on a "
                         "MATCHING PAIR of readings, not one")
        self.assertEqual({"min": 1, "max": 32}, block["dwells"])
        self.assertEqual({"max": 500000}, block["unevaluable"])
        self.assertEqual(["warp100", "warp1000"], block["warpBuckets"])
        self.assertEqual(["rigid", "flexible-soi"], block["requireSeamKinds"])
        self.assertNotIn("cycles", block)
        # Stated as its own assertion rather than left to the key-set pin: the list
        # above could grow this token without changing the KEY set at all.
        self.assertNotIn("warpHigh", block["warpBuckets"])
        # And the suite property the pair of sibling pins states from the other side:
        # exactly ONE armed block in the corpus declares warpBuckets, and it is this
        # one. A second lane picking the key up is an arming decision of its own.
        with_buckets = sorted(n for n in self.RENDERCOMPOSE_ARMED_SPECS
                              if "warpBuckets" in self._armed_block(n))
        self.assertEqual(["V24W-duna-one-warp-stair.toml"], with_buckets,
                         "warpBuckets is armed on a lane other than the RC-WARP one; "
                         "every other committed subject moves the clock with "
                         "instantaneous TimeJumps, so its histogram is 1x-only BY "
                         "CONSTRUCTION and the key would pin the drive shape rather "
                         "than the product")

    def test_every_armed_block_keeps_an_anti_vacuity_floor(self):
        """The property both key-set pins exist to protect, stated once against the
        roster so a THIRD armed lane inherits it: an armed block whose every
        assertion is a ceiling passes green off a manifest that observed nothing.
        A floor is a `min` on a count window or a `requireSeamKinds` list (a kind
        that must be PRESENT); `unevaluable` is a ceiling by nature and never counts.
        The grammar's `_validate_armed_empty` notch only refuses a block with NO
        assertion key at all, so this is the sharper statement."""
        for name in sorted(self.RENDERCOMPOSE_ARMED_SPECS):
            block = self._armed_block(name)
            floors = [k for k in ("dwells", "cycles")
                      if isinstance(block.get(k), dict) and block[k].get("min", 0) > 0]
            floors += ["requireSeamKinds"] if block.get("requireSeamKinds") else []
            self.assertTrue(floors,
                            "%s arms render-composition with ceilings only; a manifest "
                            "that observed nothing would pass it" % name)

    def test_every_declarer_arms_the_tracers_the_seam_capture_needs(self):
        """The declaration alone buys a manifest with NO seam numbers in it: the
        tangent/endpoint capture predicates are `mapRenderTracing`-gated (SPEC
        decision 2), so on a tracer-off lane every RC-SEAM numeric clause counts as
        `seam-data-unavailable-tracing-off` unevaluable. A lane that declares the
        block without arming the tracer flies, greens, and measures nothing - the
        exact shape a reading run must not have. `verboseLogging` is load-bearing
        beside it (the V1 lesson: tracer truth is emitted at Verbose)."""
        for name in sorted(self.RENDERCOMPOSE_DECLARER_SPECS):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            steps = (spec.get("driver", {}) or {}).get("steps", []) or []
            armed = {((s or {}).get("args") or {}).get("name")
                     for s in steps
                     if (s or {}).get("cmd") == "SetSetting"
                     and ((s or {}).get("args") or {}).get("value") == "true"}
            for setting in ("ghostRenderTracing", "mapRenderTracing", "verboseLogging"):
                self.assertIn(setting, armed,
                              "%s declares [expectations.renderComposition] without "
                              "arming %s" % (name, setting))

    def test_every_declarer_exports_immediately_before_teardown(self):
        """Placement rule, pinned. The recorder accumulates from Awake, so the only
        thing placement decides is how much of the lane's observation is already
        inside the exported manifest - an export taken earlier silently drops
        whatever the lane observed after it, and no export at all leaves the
        composition to the scene-exit auto-flush, i.e. to the teardown moment rather
        than the one the lane spent its steps building (`hlib`'s stated reason for
        the verb existing)."""
        for name in sorted(self.RENDERCOMPOSE_DECLARER_SPECS):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            cmds = [(s or {}).get("cmd")
                    for s in ((spec.get("driver", {}) or {}).get("steps", []) or [])]
            self.assertEqual([hlib.RENDER_MANIFEST_EXPORT_VERB, "FlushAndQuit"],
                             cmds[-2:],
                             "%s: expected the manifest export immediately before "
                             "FlushAndQuit, got %s" % (name, cmds[-2:]))


class GhostLifecycleVerifierWiringTests(unittest.TestCase):
    """The ghost-lifecycle verifier's hlib-side wiring: spec-surface validation
    routes through ``hlib.validate_spec``, the gating flag classifies its own
    PARSEK-FAIL subkind at the right precedence, and - the SAFETY PROPERTY,
    mirroring the two rows before it - arming stays a deliberate, per-scenario,
    live-proven act.

    The guarantee AS SHIPPED is the strong form both predecessors shipped with:
    NO committed spec arms ``gating = true``, so landing the row cannot move any
    nightly's verdict. When that phase ends the roster below becomes an ALLOWLIST
    and a spec joining it needs an explicit edit here citing its run ids - the
    save-structure / render-composition workflow verbatim.

    NAMING: the roster is deliberately NOT called ``*ARMED_ALLOWLIST``.
    ``harness/missions/lib/test_cl3_refly_crew_tombstone.py`` scrapes the
    save-structure roster out of THIS FILE'S SOURCE with a first-match
    ``ARMED_ALLOWLIST\\s*=\\s*\\{([^}]*)\\}`` regex; a second symbol whose name
    ends in ``ARMED_ALLOWLIST`` would be a coin-flip on file order. The
    ``RENDERCOMPOSE_ARMED_SPECS`` precedent is followed exactly."""

    # THE ARMED ROSTER. An entry follows the RENDERCOMPOSE / save-structure
    # convention: name the READING run the windows were authored from, then the
    # ARMED RE-FLIGHT and the NEGATIVE CONTROL that discharge the three-run
    # workflow.
    GHOSTLIFE_ARMED_SPECS = {
        # ARMED 2026-08-28. Windows authored from TWO green measurements of the
        # same census (reading run `2026-08-27_2145` red only on the
        # since-fixed watch-entry race; green run `2026-08-27_2204` PASS
        # attempt 1) - both read spawned=8 destroyLines=8 unbalanced=0 against
        # the armed `spawned = { min = 8 }` + requireBalanced. Armed re-flight
        # and negative-control run ids: recorded below in this comment by the
        # arming pass's own flights (see the spec's ARMED comment).
        # ARMED RE-FLIGHTS: `2026-08-28_1527` (PASS attempt 1, gate live) and
        # `2026-08-28_1544` (PASS attempt 1 on the final MAP-EXIT flight-view
        # sequence - the map is closed before watch entry).
        # NEGATIVE CONTROL: `2026-08-28_1550` - temporary spawned={min 9} red
        # PARSEK-FAIL(ghost-lifecycle) on exactly that window, then reverted.
        "GS-4-kerbalx-rewind-watch.toml",
        # ARMED 2026-08-28, same-day discipline on the injected part-showcase
        # census: reading runs `2026-08-28_2010` (red only on the since-cut
        # colour-changer token; census 243/243/0) and `2026-08-28_2014` (green,
        # PASS attempt 1, census 243/243/0 again - the EXACT spawn census with
        # requireBalanced=true holding through the one-shot window endings).
        # ARMED RE-FLIGHT: `2026-08-28_2016` - PASS attempt 1, gate live.
        # NEGATIVE CONTROL: `2026-08-28_2017` - temporary spawned={min 244} red
        # PARSEK-FAIL(ghost-lifecycle) on exactly that window, then reverted.
        "S1.9-part-showcase-render.toml",
    }

    def test_no_committed_spec_arms_ghost_lifecycle_gating(self):
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            if ghostlife.gating_armed(spec.get("expectations") or {}):
                armed.append(name)
        self.assertEqual(sorted(self.GHOSTLIFE_ARMED_SPECS), armed,
                         "the set of specs arming ghost-lifecycle gating changed; arming "
                         "is a per-scenario operator decision taken only after a "
                         "report-only reading run whose facets match the declared "
                         "windows - add the spec here in the same commit that arms it, "
                         "citing the run id")

    def test_gating_mismatch_classifies_ghost_lifecycle_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["ghost_lifecycle_mismatch"] = True
        verdict = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(("PARSEK-FAIL", "ghost-lifecycle"),
                         (verdict.verdict, verdict.subkind))
        self.assertIn("ghost-lifecycle", hlib.PARSEK_FAIL_SUBKINDS)

    def test_absent_flag_is_a_pass_not_a_fail(self):
        # The report-only default in the classifier: with the flag ABSENT (every
        # run today, since run.py only sets it under `if gl.gating:`) the verdict
        # is an ordinary PASS. A `verifiers.get(..., True)` slip would red every
        # run in the suite.
        d, v = _clean_pass_facts()
        self.assertNotIn("ghost_lifecycle_mismatch", v)
        self.assertEqual("PASS", hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").verdict)
        v["ghost_lifecycle_mismatch"] = False
        self.assertEqual("PASS", hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").verdict)

    def test_precedence_after_render_composition_and_before_ledger(self):
        # Placement is the claim: a run that reds BOTH compositionally and on the
        # flight-scene lifecycle is named by render-composition (what the map drew
        # is upstream), and a run that reds BOTH on the lifecycle and on the
        # ledger is named by ghost-lifecycle.
        d, v = _clean_pass_facts()
        v["render_composition_mismatch"] = True
        v["ghost_lifecycle_mismatch"] = True
        self.assertEqual("render-composition",
                         hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").subkind)
        d, v = _clean_pass_facts()
        v["ghost_lifecycle_mismatch"] = True
        v["ledger_drift"] = True
        self.assertEqual("ghost-lifecycle",
                         hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").subkind)

    def test_parsek_fail_is_never_retried(self):
        verdict = hlib.Verdict("PARSEK-FAIL", "ghost-lifecycle", False, "x")
        self.assertFalse(hlib.should_retry(verdict, 1, "once"))

    # -- validate_spec routing -------------------------------------------------

    def _spec_with(self, block, tracer_on=True):
        spec = copy.deepcopy(load_spec("B1-pad-hop.toml"))
        spec["expectations"][ghostlife.GHOST_LIFECYCLE_BLOCK] = block
        if tracer_on:
            # Index 1, not 0: step 0 must be LoadGame (validate_spec's own rule).
            spec["driver"]["steps"].insert(
                1, {"cmd": "SetSetting",
                    "args": {"name": "ghostRenderTracing", "value": "true"},
                    "expect": "OK"})
        return spec

    def test_malformed_window_rejects_pre_launch(self):
        res = hlib.validate_spec(self._spec_with({"spawned": "lots"}), load_registry())
        self.assertFalse(res.ok)
        self.assertTrue(any("ghostLifecycle" in e for e in res.errors), res.errors)

    def test_unknown_key_rejects_pre_launch(self):
        res = hlib.validate_spec(self._spec_with({"minSpawned": 3}), load_registry())
        self.assertFalse(res.ok)
        self.assertTrue(any("unknown key" in e and "ghostLifecycle" in e
                            for e in res.errors), res.errors)

    def test_bad_forbidden_regex_rejects_pre_launch(self):
        res = hlib.validate_spec(
            self._spec_with({"destroyedReasons": {"forbidden": ["(unclosed"]}}),
            load_registry())
        self.assertFalse(res.ok)
        self.assertTrue(any("not a valid regex" in e for e in res.errors), res.errors)

    def test_a_well_formed_report_only_block_validates(self):
        block = {"spawned": {"min": 1, "max": 8}, "requireBalanced": True,
                 "destroyedReasons": {"forbidden": ["explod"]}}
        spec = self._spec_with(block)
        res = hlib.validate_spec(spec, load_registry())
        self.assertTrue(res.ok, res.errors)
        self.assertEqual([], [w for w in res.warnings if "ghostLifecycle" in w],
                         res.warnings)
        exp = spec["expectations"]
        self.assertEqual(("ghostLifecycle",),
                         ghostlife.declared_ghost_lifecycle_blocks(exp))
        self.assertFalse(ghostlife.gating_armed(exp))

    def test_an_armed_bare_block_validates_unlike_its_two_siblings(self):
        # The documented divergence from saveparse / rendercompose: an armed bare
        # block here still asserts the vacuity floor plus the requireBalanced
        # default, so it is NOT a gate that can never red.
        res = hlib.validate_spec(self._spec_with({"gating": True}), load_registry())
        self.assertTrue(res.ok, res.errors)

    def test_the_tracer_coupling_warns_but_never_rejects(self):
        # The declared block with no `SetSetting ghostRenderTracing = true` step.
        # A WARNING and not an error, deliberately - the vacuity floor already
        # reds the tracer-off run, and an error would refuse that floor's own
        # negative control.
        res = hlib.validate_spec(
            self._spec_with({"spawned": {"min": 1}}, tracer_on=False),
            load_registry())
        self.assertTrue(res.ok, res.errors)
        self.assertTrue(any("ghostRenderTracing" in w for w in res.warnings),
                        res.warnings)

    # THE DECLARER roster, distinct from the armed one. An entry records WHY the
    # lane declares the block and where its windows came from, so a spec picking
    # the block up (or dropping it) is always a deliberate, reviewed edit - the
    # RENDERCOMPOSE_DECLARER_SPECS convention exactly.
    GHOSTLIFE_DECLARER_SPECS = {
        # [D] THE FIRST DECLARER, and the lane the evaluator was built FOR: the
        #     full player-workflow derender tripwire (staged Kerbal X ascent ->
        #     commit -> Rewind-to-Launch -> Jumping Flea watch anchor -> map view
        #     + watch mode -> playback to completion). FLOWN GREEN 2026-08-27:
        #     both flights (`2026-08-27_2145` reading, `2026-08-27_2204` green)
        #     measured the census EXACTLY - spawned=8 (parent + 6 booster
        #     debris + the "Kerbal X Probe" controlled-decoupled core),
        #     destroyLines=8, unbalanced=0 - and the window is RE-PINNED to
        #     `spawned = { min = 8 }`, still REPORT-ONLY. Arming (through
        #     GHOSTLIFE_ARMED_SPECS above) follows the standard three-run
        #     discipline as its own pass.
        "GS-4-kerbalx-rewind-watch.toml",
        # [D] THE SECOND DECLARER, and the first whose census IS the point rather
        #     than a balance check: the synthetic PART SHOWCASE corpus (243
        #     ghost-only, one-part recordings standing in front of the KSC pad,
        #     injected through the new `part-showcase` preset). Its
        #     `spawned = { min = 243 }` floor says "every showcase row rendered a
        #     ghost mesh" - the operator's eyeball pass in one number, which no
        #     regex can state because the recIds are fresh GUIDs. The floor is
        #     MEASURED, not guessed, from BOTH ends: `dotnet test --filter
        #     InjectPartShowcase` against a scratch KSP root wrote exactly 243
        #     `.prec` sidecars / 243 distinct vesselNames with ReStock+ absent
        #     (the stock-minimal shape), and reading run 1 read the same 243 back
        #     off the produced save as `recordings.count`.
        #     REPORT-ONLY and deliberately absent from GHOSTLIFE_ARMED_SPECS:
        #     reading run 1 (`2026-08-28_1945`) measured `spawned=0` for a pure
        #     TIMING reason - the lane never entered the corpus's playback window
        #     - so the floor has still never been measured against a run that
        #     could satisfy it. Arming waits on reading run 2.
        #     `requireBalanced = true` HERE, and the flip is itself a measurement.
        #     The first cut set it FALSE on the argument that these rows loop and
        #     self-overlap, demoting primaries without destroying them. Reading
        #     run 1 killed that at the mechanism, in one line:
        #     `SanitizeNonLoopableLoopPlayback: cleared LoopPlayback on 243
        #     non-loopable recording(s)` - a showcase row fails every arm of
        #     `Recording.IsLoopableRecording`, so the authored loop flag is
        #     STRIPPED AT LOAD and what actually plays is 243 ORDINARY one-shot
        #     windows. A window that ends destroys its ghost, so the balance is a
        #     real statement here; the spec's jump staircase deliberately lands
        #     past the window end so the endings happen inside the run rather
        #     than at teardown. See SHOWCASE-LOOPFLAG-STRIPPED-AT-LOAD in
        #     docs/dev/todo-and-known-bugs.md.
        "S1.9-part-showcase-render.toml",
    }

    def test_ghost_lifecycle_declarers_are_the_recorded_roster(self):
        """Pinned so a declarer is always a deliberate edit, in BOTH directions
        (the RENDERCOMPOSE declarer-roster rationale): a spec picking the block up
        by accident starts gating-adjacent measurement its author never argued
        for, and a spec LOSING the block silently stops measuring the derender
        balance its own name promises."""
        declarers = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            if ghostlife.declared_ghost_lifecycle_blocks(spec.get("expectations") or {}):
                declarers.append(name)
        self.assertEqual(sorted(self.GHOSTLIFE_DECLARER_SPECS), declarers,
                         "the set of specs declaring [expectations.ghostLifecycle] "
                         "changed; record the decision in the roster here in the "
                         "same commit (why the lane declares it, and where its "
                         "windows came from)")

    def test_every_declarer_arms_the_flight_tracer(self):
        """The declarer-side half of the tracer coupling (validate_spec only WARNS,
        deliberately - see test_the_tracer_coupling_warns_but_never_rejects). A
        COMMITTED declarer with no `SetSetting ghostRenderTracing=true` step would
        fly straight into the vacuity floor on every run, so the roster refuses the
        shape outright rather than letting a lane ship a guaranteed red."""
        for name in sorted(self.GHOSTLIFE_DECLARER_SPECS):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            steps = (spec.get("driver") or {}).get("steps") or []
            armed = any(
                step.get("cmd") == "SetSetting"
                and (step.get("args") or {}).get("name") == "ghostRenderTracing"
                and (step.get("args") or {}).get("value") == "true"
                for step in steps)
            self.assertTrue(armed,
                            "%s declares [expectations.ghostLifecycle] but never "
                            "arms ghostRenderTracing; the tracer is the block's "
                            "only measurement surface" % name)

    def test_armed_roster_is_subset_of_declarers(self):
        # Arming without a declaration cannot happen - the block IS the
        # declaration (the rendercompose arming-state rule).
        self.assertLessEqual(self.GHOSTLIFE_ARMED_SPECS,
                             self.GHOSTLIFE_DECLARER_SPECS,
                             "a spec is in GHOSTLIFE_ARMED_SPECS without declaring "
                             "the block")


class WorldRosterDeclarerTests(unittest.TestCase):
    """The world ROSTER sub-facet is HARD (a declared claim reds the run), so its
    declarer set is a deliberate, reviewed list exactly like the save-structure
    arming allowlist above - not something a spec picks up by accident.

    L1-dismiss-kerbal-career is the FIRST and only declarer. Its claim is discharged
    by the STAGED TEMPLATE rather than by a run: fresh-career carries the four stock
    crew, and the spec dismisses one of them. That is why it needs no report-only
    reading run the way a measured window does - the names are authored, not
    observed.
    """

    ROSTER_DECLARERS = {"L1-dismiss-kerbal-career.toml"}

    def _declarers(self):
        out = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            world = (spec.get("expectations") or {}).get("world") or {}
            roster = world.get("roster") or {}
            if roster.get("present") or roster.get("absent"):
                out.append(name)
        return out

    def test_declarer_set_is_pinned(self):
        self.assertEqual(sorted(self.ROSTER_DECLARERS), self._declarers(),
                         "the set of specs declaring [expectations.world.roster] changed; "
                         "the sub-facet is HARD, so add the spec here in the same commit "
                         "that declares it, with the fixture row backing its names")

    def test_l1_declares_the_dismissed_kerbal_absent_and_the_bystanders_present(self):
        spec = load_spec("L1-dismiss-kerbal-career.toml")
        roster = spec["expectations"]["world"]["roster"]
        self.assertEqual(["Bill Kerman"], roster["absent"])
        self.assertEqual(
            {"Jebediah Kerman", "Bob Kerman", "Valentina Kerman"}, set(roster["present"]))
        # The absent name MUST be the kerbal the driver actually dismisses, or the
        # assertion would certify a dismissal that never happened.
        dismissed = [s["args"]["kerbal"] for s in spec["driver"]["steps"]
                     if s.get("cmd") == "KscAction"
                     and (s.get("args") or {}).get("action") == "dismiss-kerbal"]
        self.assertEqual(["Bill Kerman"], dismissed)
        # ...and no declared-present name may be the dismissed one.
        self.assertNotIn("Bill Kerman", roster["present"])
        # The key set is pinned so a COUNT / status claim cannot be appended without
        # review: applicant slots are stock's to regenerate and are not this
        # scenario's claim.
        self.assertEqual({"absent", "present"}, set(roster))


class AnomalyGrepAnchoringTests(unittest.TestCase):
    """The sweep must match an actual EmitAnomaly RAISE, not a token appearing
    anywhere in KSP.log.

    Found by S1.7's first flight (2026-07-26), which reddened PARSEK-FAIL(anomaly)
    on a line that reports the ABSENCE of drift. The old grep was
    `if tok in log_text` over the whole file."""

    # The exact line that reddened S1.7's first flight. A PhaseSpineSwap test
    # diagnostic whose LABEL is the token; over=False, maxDev=0.0m.
    FALSE_POSITIVE = ("[Parsek][INFO][TestRunner] SpineDrive parity-drift: sampled=True"
                      " skip=(none) hasMeas=True maxDev=0.0m tol=1989.4m over=False")
    # The real shape, from MapRenderTrace.EmitAnomaly -> EmitRaw.
    REAL_RAISE = ("[Parsek][INFO][MapRenderTrace] phase=Anomaly surface=ProtoOrbitLine"
                  " pid=123 recId=r1 frame=7887 currentUT=21.500 effUT=21.500"
                  " reason=parity-drift maxDev=91234m tol=1927m")

    def test_a_token_named_in_an_ordinary_log_line_is_not_a_hit(self):
        self.assertEqual([], hlib.grep_anomaly_tokens(self.FALSE_POSITIVE))

    def test_a_real_raise_is_a_hit(self):
        self.assertEqual(["parity-drift"], hlib.grep_anomaly_tokens(self.REAL_RAISE))

    def test_the_false_positive_does_not_mask_a_real_raise_in_the_same_log(self):
        log = self.FALSE_POSITIVE + "\n" + self.REAL_RAISE + "\n"
        self.assertEqual(["parity-drift"], hlib.grep_anomaly_tokens(log))

    def test_ledger_trace_raises_match_too(self):
        # LedgerTrace.FormatAnomaly builds the same phase=Anomaly ... reason= shape.
        line = ("[Parsek][WARN][LedgerTrace] phase=Anomaly recalcSeq=3 resource=funds"
                " id=pool reason=ledger-vs-truth target=10 actual=20")
        self.assertEqual(["ledger-vs-truth"], hlib.grep_anomaly_tokens(line))

    def test_a_reason_prefix_does_not_match_a_longer_token(self):
        # Both anchors, on REAL production shapes rather than invented ones. The old
        # version used `reason=icon-teleport` for this, which stopped being a
        # not-a-hit example when icon-teleport was PROMOTED into the gate 2026-08-04.
        #
        # PHASE anchor. `line-blink-suppressed` is the Tier-B line the dark-window
        # guard emits when it eats a would-be raise (the `line-blink-suppressed`
        # EmitOnChange in MapRenderProbe's proto-orbit-line block ->
        # MapRenderTrace.EmitOnChange -> `phase=line-blink-suppressed`, NOT
        # phase=Anomaly), and its phase token CONTAINS the gated `line-blink`. A
        # substring sweep would red a green V1 dwell on the very line that proves
        # the guard worked.
        suppressed = ("[Parsek][VERBOSE][MapRenderTrace] phase=line-blink-suppressed"
                      " surface=ProtoOrbitLine pid=123 recId=r1 frame=8100"
                      " lineActive=True sinceFrames=3 offWindowCovered=True")
        self.assertEqual([], hlib.grep_anomaly_tokens(suppressed))
        self.assertEqual([], hlib.unlisted_anomaly_reasons(suppressed))
        # WHOLE-FIELD anchor. `factory-parity` is a real raise and shares the word
        # "parity" with the gated `parity-drift`; the reason is read whole, so it is
        # REPORTED and never counted as a hit for the gated token.
        parity = ("[Parsek][INFO][MapRenderTrace] phase=Anomaly surface=ProtoOrbitLine"
                  " pid=1 reason=factory-parity shadow=1 live=0")
        self.assertEqual([], hlib.grep_anomaly_tokens(parity))
        self.assertEqual(["factory-parity"], hlib.unlisted_anomaly_reasons(parity))

    def test_hits_come_back_in_registry_order_not_emit_order(self):
        log = "\n".join(
            "[Parsek][INFO][MapRenderTrace] phase=Anomaly pid=1 reason=" + t
            for t in ("ledger-vs-truth", "parity-drift", "line-blink"))
        self.assertEqual(["line-blink", "parity-drift", "ledger-vs-truth"],
                         hlib.grep_anomaly_tokens(log))

    def test_unlisted_reasons_are_reported_not_gated(self):
        # The report-not-gate channel, exercised on two of the reasons that are
        # deliberately ungated (both INSTRUMENTS: `factory-parity` is a shadow
        # comparator that never drives a draw, `unaccounted-drawn-recording` is the
        # S0 polyline-coverage probe). Two of them, not "the two" - the ungated set
        # is three since the encounter-geometry lens joined it on 2026-08-09, and
        # `test_the_ungated_list_is_the_settled_instrument_set` owns the membership
        # claim. They must surface without changing the verdict; the gated token
        # alongside them must.
        log = "\n".join(
            "[Parsek][INFO][MapRenderTrace] phase=Anomaly pid=1 reason=" + t
            for t in ("factory-parity", "unaccounted-drawn-recording", "parity-drift"))
        self.assertEqual(["parity-drift"], hlib.grep_anomaly_tokens(log))
        self.assertEqual(["factory-parity", "unaccounted-drawn-recording"],
                         hlib.unlisted_anomaly_reasons(log))

    def test_icon_jump_is_retired_and_icon_teleport_is_now_gated(self):
        # BOTH HALVES OF THE DRIFT ARE NOW CLOSED, and this cell pins each.
        # HALF ONE (2026-07-29): `icon-jump` no longer sits in the gated set
        # advertising coverage of a raise that does not exist. It is RETIRED to
        # ANOMALY_TOKENS_DEAD, and the two tuples are disjoint.
        self.assertNotIn("icon-jump", hlib.ANOMALY_TOKENS)
        self.assertIn("icon-jump", hlib.ANOMALY_TOKENS_DEAD)
        self.assertEqual(set(), set(hlib.ANOMALY_TOKENS) & set(hlib.ANOMALY_TOKENS_DEAD))
        # HALF TWO (2026-08-04): the REAL raise (`icon-teleport`) is now GATED. The
        # old version of this cell said the measurement of whether it fires on a
        # green tracer-armed run would decide it; that measurement came in - the
        # fresh S1.4 reading 2026-08-04_1228 exercised the probe and stayed SILENT,
        # matching five V1 real-geometry dwells and 155 tracer-on historical runs.
        # So a raise is now a HIT, not a report.
        line = ("[Parsek][INFO][MapRenderTrace] phase=Anomaly surface=ProtoIcon pid=1"
                " reason=icon-teleport TELEPORT dPos=900m = 42x expected(21m)")
        self.assertIn("icon-teleport", hlib.ANOMALY_TOKENS)
        self.assertEqual(["icon-teleport"], hlib.grep_anomaly_tokens(line))
        self.assertEqual([], hlib.unlisted_anomaly_reasons(line))

    def test_empty_and_none_are_clean(self):
        for empty in (None, "", "\n\n"):
            self.assertEqual([], hlib.grep_anomaly_tokens(empty))
            self.assertEqual([], hlib.unlisted_anomaly_reasons(empty))


# ---------------------------------------------------------------------------
# The ungated-reason ground truth, DERIVED FROM SOURCE.
# ---------------------------------------------------------------------------

REPO_ROOT = os.path.dirname(HARNESS_ROOT)
PARSEK_SRC_DIR = os.path.join(REPO_ROOT, "Source", "Parsek")
DOCS_DEV_DIR = os.path.join(REPO_ROOT, "docs", "dev")
AUTOTEST_STATUS_DOC = os.path.join(DOCS_DEV_DIR, "autotest-status.md")
INGAME_INVENTORY_DOC = os.path.join(DOCS_DEV_DIR,
                                    "autotest-ingame-category-inventory.md")


class IngameCategoryInventoryDocTests(unittest.TestCase):
    """`autotest-ingame-category-inventory.md`'s 98-row table says of itself "Do NOT
    hand-edit the table: re-derive it" - and shipped with nothing enforcing that.

    The gap that leaves: add one `[InGameTest(Category = "Rewind")]` and NOTHING
    reds. `CommittedBatchTallySourceSyncTests` does not (Rewind is unpinned),
    `IngameBatchWiringGroupTests` does not (Rewind is not in the H-series group), and
    the Rewind row, the 542 / 98 totals repeated across four documents, and the
    A/B/C declaration sums all go quietly stale. The table is the stated authority
    for what remains to wire, so a stale row is how the next wave plans against
    fiction.

    Scope: the five columns that are mechanically derivable from the attributes.
    The "Members with self-skip" column is NOT gated - it needs a call-graph walk
    whose name resolution over-approximates, so pinning it here would trade a
    silent-staleness bug for a false-red one."""

    HEADER_RE = re.compile(r"^\|\s*Category\s*\|")
    SEPARATOR_RE = re.compile(r"^\|[\s\-:|]+\|$")
    ROW_RE = re.compile(
        r"^\|\s*`(?P<cat>[^`]+)`\s*\|\s*(?P<decls>\d+)\s*\|\s*(?P<f>\d+)\s*\|"
        r"\s*(?P<s>\d+)\s*\|\s*(?P<t>\d+)\s*\|\s*(?P<nb>\d+)\s*\|")

    @classmethod
    def setUpClass(cls):
        with open(INGAME_INVENTORY_DOC, encoding="utf-8") as fh:
            cls.lines = fh.read().split("\n")
        cls.decls = load_ingame_test_declarations()
        cls.rows = {}
        in_table = False
        for line in cls.lines:
            if cls.HEADER_RE.match(line):
                in_table = True
                continue
            if in_table and cls.SEPARATOR_RE.match(line):
                continue
            m = cls.ROW_RE.match(line)
            if m:
                in_table = True
                cls.rows[m.group("cat")] = (
                    int(m.group("decls")), int(m.group("f")), int(m.group("s")),
                    int(m.group("t")), int(m.group("nb")))
            elif in_table and not line.startswith("|"):
                in_table = False

    def test_the_table_was_actually_parsed(self):
        # Anti-vacuity floor: a table this cell cannot parse must RED, not silently
        # verify zero rows. Same reason CommittedBatchTallySourceSyncTests asserts
        # its source walk found something.
        self.assertGreater(
            len(self.rows), 90,
            "only %d rows parsed out of %s - the row regex no longer matches the "
            "committed table, so every assertion below would be vacuous"
            % (len(self.rows), INGAME_INVENTORY_DOC))

    def test_the_table_lists_exactly_the_categories_in_the_source(self):
        source = {d.category for d in self.decls}
        self.assertEqual(
            sorted(source), sorted(self.rows),
            "the inventory table's category set has drifted from Source/Parsek. "
            "Re-derive the table (hlib.parse_ingame_test_declarations + "
            "derive_batch_tally) rather than editing rows by hand, and update the "
            "542 / 98 totals and the A/B/C sums in the same commit")

    def test_every_row_matches_the_source_derivation(self):
        for cat in sorted(self.rows):
            with self.subTest(category=cat):
                in_cat = [d for d in self.decls if d.category == cat]
                stated = self.rows[cat]
                derived = (
                    len(in_cat),
                    hlib.derive_batch_tally(in_cat, cat, "FLIGHT").executable,
                    hlib.derive_batch_tally(in_cat, cat, "SPACECENTER").executable,
                    hlib.derive_batch_tally(in_cat, cat, "TRACKSTATION").executable,
                    sum(1 for d in in_cat if not d.allow_batch))
                self.assertEqual(
                    derived, stated,
                    "%s row is stale: stated (decls, execF, execS, execT, "
                    "batch-disabled) = %s but the source derives %s"
                    % (cat, stated, derived))

    def test_the_stated_totals_match_the_table(self):
        stated_decls = sum(r[0] for r in self.rows.values())
        body = "\n".join(self.lines)
        # The category COUNT is hardcoded here on purpose: it is the one token the
        # table cannot self-check (a row added AND the totals line updated by hand
        # would agree with each other while both drifted from the source). 99 -> 100
        # with the `ReFlyWorldPreservation` category (S4.2), 100 -> 101 with
        # `RecordedSignals` (H33), 101 -> 102 with `SnapshotBaseline` (H32),
        # 102 -> 103 with `PlaybackFidelity` (H36), 103 -> 104 with
        # `PartEventFidelity` (H37), 104 -> 105 with `RenderComposition` (M-A7),
        # 105 -> 106 with `DisabledHoverEcho` (the greyed-button hover explainer's
        # live IMGUI cell; deliberately its OWN category rather than `Settings`,
        # whose BATCH_COMPLETE tally H46 pins from a flown run), 106 -> 107 with
        # `AutoMergeCommit` (R4, the plan-§7 autoMerge=ON scene-exit cell).
        self.assertIn("**107 categories / %d declarations**" % stated_decls, body,
                      "the triage totals line disagrees with the table it summarises "
                      "(table sums to %d declarations across %d categories)"
                      % (stated_decls, len(self.rows)))
        self.assertEqual(len(self.decls), stated_decls)
TODO_DOC = os.path.join(DOCS_DEV_DIR, "todo-and-known-bugs.md")

# A reason token: lowercase words joined by hyphens. Deliberately requires a
# hyphen so it cannot match a bare word, and forbids spaces / `=` so it cannot
# match a formatted `details` string.
_REASON_TOKEN_RE = re.compile(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)+\Z")

# Reason-argument POSITION (0-based) per tracer signature. Both are checked by
# qualifier so the LedgerTrace `resource` argument (which is also a hyphenated
# literal on the tech-node / subject-science call sites) can never be mistaken
# for a reason.
#   MapRenderTrace.EmitAnomaly(surface, pidKey, currentUT, effUT, reason, details, recId)
#   LedgerTrace.EmitAnomaly(resource, id, reason, details)
#   GhostRenderTrace.EmitAnomaly(recordingId, ghostIndex, currentUT, playbackUT, reason, details)
_REASON_ARG_INDEX = {"MapRenderTrace": 4, "LedgerTrace": 2, "GhostRenderTrace": 4, "": 4}


def _split_top_level_args(blob):
    """Split a C# argument list on TOP-LEVEL commas (paren/bracket/brace depth 0,
    outside string and char literals)."""
    args, depth, i, start = [], 0, 0, 0
    in_str = in_chr = False
    while i < len(blob):
        c = blob[i]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
        elif in_chr:
            if c == "\\":
                i += 2
                continue
            if c == "'":
                in_chr = False
        elif c == '"':
            in_str = True
        elif c == "'":
            in_chr = True
        elif c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            args.append(blob[start:i])
            start = i + 1
        i += 1
    args.append(blob[start:])
    return args


def _anomaly_const_map():
    path = os.path.join(PARSEK_SRC_DIR, "MapRenderTrace.cs")
    with open(path, encoding="utf-8-sig") as fh:
        src = fh.read()
    return dict(re.findall(r'internal const string (Anomaly\w+)\s*=\s*"([^"]+)"', src))


def _production_anomaly_raises():
    """Walk every `EmitAnomaly(` call site under Source/Parsek EXCLUDING
    InGameTests/, and return {reason: [producer file:line, ...]}.

    The call sites are matched by paren balancing (the calls are multi-line), the
    reason is taken by ARGUMENT POSITION for the qualifier's signature, and a
    `MapRenderTrace.Anomaly*` constant is resolved through the const map. Bare
    `EmitAnomaly(` inside MapRenderTrace.cs itself is the four thin
    cutover-hardening wrappers, which share the MapRenderTrace signature."""
    consts = _anomaly_const_map()
    out = {}
    for dirpath, dirnames, filenames in os.walk(PARSEK_SRC_DIR):
        dirnames[:] = [d for d in dirnames if d != "InGameTests"]
        for filename in sorted(filenames):
            if not filename.endswith(".cs"):
                continue
            path = os.path.join(dirpath, filename)
            with open(path, encoding="utf-8-sig") as fh:
                src = fh.read()
            for m in re.finditer(r"(?:(\w+)\.)?EmitAnomaly\s*\(", src):
                qualifier = m.group(1) or ""
                if qualifier not in _REASON_ARG_INDEX:
                    continue
                i, depth = m.end(), 1
                while i < len(src) and depth:
                    if src[i] == "(":
                        depth += 1
                    elif src[i] == ")":
                        depth -= 1
                    i += 1
                args = _split_top_level_args(src[m.end():i - 1])
                idx = _REASON_ARG_INDEX[qualifier]
                if idx >= len(args):
                    continue
                arg = args[idx].strip()
                literal = re.fullmatch(r'"([^"]*)"', arg)
                if literal:
                    reason = literal.group(1)
                elif arg.split(".")[-1] in consts:
                    reason = consts[arg.split(".")[-1]]
                else:
                    continue
                if not _REASON_TOKEN_RE.match(reason):
                    continue
                rel = os.path.relpath(path, os.path.dirname(HARNESS_ROOT))
                producer = "%s:%d" % (rel.replace(os.sep, "/"),
                                      src.count("\n", 0, m.start()) + 1)
                out.setdefault(reason, []).append(producer)
    return out


class AnomalyGroundTruthEnumerationTests(unittest.TestCase):
    """Every reason the mod raises must be accounted for by exactly one of the two
    tuples - GATED (a raise reds) or a declared report-only INSTRUMENT - and that
    accounting must not be hand-maintained prose.

    The 2026-07-26 first pass listed 5 of the then-9 ungated reasons - it missed the
    four cutover-hardening raises (clock-not-ready / retire-not-held /
    anchor-resolve-fail / factory-parity), which reach EmitAnomaly through thin
    MapRenderTrace wrappers rather than at the guard site. An incomplete
    enumeration understates a fail-open, which is exactly the thing the list
    exists to size, so it is derived from the C# source here.

    The 2026-08-04 calibration sweep PROMOTED seven of the nine into
    ANOMALY_TOKENS, leaving two declared instruments; the encounter-geometry lens
    made it three on 2026-08-09. That changes what these cells guard but not why
    they exist: the partition still has to hold, and now a raise site added without
    a decision lands as an un-gated, un-declared reason and reds here. Membership,
    never a count, is the contract - a count written in prose is the thing that
    goes stale, and understating it understates the fail-open."""

    def setUp(self):
        self.assertTrue(os.path.isdir(PARSEK_SRC_DIR),
                        "Source/Parsek must be present: this gate is a source scan, "
                        "and skipping it would make the enumeration unmeasured again")
        self.raised = _production_anomaly_raises()

    def test_scanner_sees_the_known_raise_sites(self):
        # Anti-vacuity for the scanner itself: an empty / near-empty walk would make
        # every set assertion below trivially true.
        self.assertGreaterEqual(len(self.raised), 15)
        # 1012 -> 1021 when the M-A7 recorder hooks landed above it in the same file;
        # 1021 -> 1079 (2026-08-28) when the line-blink TracedPath-handoff exemption
        # landed above it. The raise site itself is unchanged both times.
        self.assertIn("Source/Parsek/MapRenderProbe.cs:1079",
                      self.raised.get("icon-teleport", []))
        self.assertIn("Source/Parsek/GameActions/FacilityStatePatcher.cs:158",
                      self.raised.get("ledger-vs-truth", []))
        # The LedgerTrace `resource` argument is also hyphenated on two call sites;
        # positional resolution must not mistake it for a reason.
        self.assertNotIn("tech-node", self.raised)
        self.assertNotIn("subject-science", self.raised)

    def test_raised_set_partitions_exactly_into_gated_plus_documented_ungated(self):
        gated_live = set(hlib.ANOMALY_TOKENS) - set(hlib.ANOMALY_TOKENS_DEAD)
        documented_ungated = {r for r, _ in hlib.ANOMALY_REASONS_RAISED_UNGATED}
        self.assertEqual(set(), gated_live & documented_ungated)
        self.assertEqual(
            gated_live | documented_ungated, set(self.raised),
            "a production EmitAnomaly reason is neither gated by ANOMALY_TOKENS nor "
            "listed in ANOMALY_REASONS_RAISED_UNGATED (or a listed one no longer "
            "exists) - re-derive the todo-doc table in the same change")

    def test_the_ungated_list_is_the_settled_instrument_set(self):
        # After the 2026-08-04 calibration promotion the ungated list is no longer a
        # backlog to be worked down - it is the SETTLED instrument list, and its
        # membership is the claim worth pinning. Each entry survives for a written
        # reason (see the tuple's comment block), and the reasons are NOT all the
        # same shape, which is why the cell pins membership rather than a count:
        #   - `unaccounted-drawn-recording` / `factory-parity`: a raise reports an
        #     instrumentation/diagnostic condition, not a rendered defect, so gating
        #     it would red a flight for the probe's own gap.
        #   - `seam-endpoint-outside-soi` (added with the encounter-geometry
        #     instrument): a raise WOULD be a real finding. This comment used to say
        #     the instrument had never flown; the 2026-08-09 seam-endpoint census
        #     retired that - it flew, healthy, on V4 and V7M. The blockers hlib's
        #     tuple comment names are now the RAISE never having fired, plus TWO
        #     unmeasured benign populations (a faithful loop replay of an
        #     interplanetary transfer, and a re-aimed member whose arrival the
        #     producer re-timed). Read that comment, not this list, for the current
        #     promotion decision.
        # The cell was named `..._count_is_two_instruments` while two was the whole
        # claim; it is renamed rather than re-numbered because the COUNT was never
        # the contract - the membership is.
        self.assertEqual(
            [("unaccounted-drawn-recording", "Source/Parsek/MapRenderProbe.cs:544"),
             ("factory-parity", "Source/Parsek/MapRender/ShadowRenderDriver.cs:726"),
             ("seam-endpoint-outside-soi", "Source/Parsek/MapRenderProbe.cs:2361")],
            list(hlib.ANOMALY_REASONS_RAISED_UNGATED),
            "the report-only instrument list changed - that is a calibration "
            "decision (defect signal vs instrument), not a bookkeeping edit")
        # ...and every one is still genuinely RAISED. An instrument nobody raises is
        # a dead token and belongs in ANOMALY_TOKENS_DEAD, not here.
        for reason, _ in hlib.ANOMALY_REASONS_RAISED_UNGATED:
            self.assertIn(reason, self.raised)
        # ...and the pinned file:line POINTER still resolves, which nothing checked
        # until 2026-08-09 - the seam entry's line had already gone stale twice inside
        # one branch by then, once while the author was fixing stale line citations.
        # The tuple's own comment block declares the convention: the pointer is the
        # DECISION site, which for a wrapper-routed raise (of the live entries, only
        # `factory-parity`) is the guard rather than the EmitAnomaly call the scanner
        # sees. Those are exempted by name; everything else must match a site the C#
        # scan actually found, so a pointer cannot rot silently.
        wrapper_routed_pointer = {"factory-parity"}
        for reason, pointer in hlib.ANOMALY_REASONS_RAISED_UNGATED:
            if reason in wrapper_routed_pointer:
                continue
            self.assertIn(
                pointer, self.raised[reason],
                "ANOMALY_REASONS_RAISED_UNGATED pins %s at %s, but the C# scan finds "
                "its EmitAnomaly at %s - re-derive the pointer here, in the literal "
                "above, and in the todo-doc table" % (reason, pointer, self.raised[reason]))

    def test_the_four_wrapper_routed_raises_are_accounted_for(self):
        # The 2026-07-26 first pass listed 5 of 9 ungated reasons because these four
        # reach EmitAnomaly through thin once-per-event MapRenderTrace wrappers
        # rather than at the guard site, so a naive grep misses them. They are still
        # the easiest reasons to lose track of; three are now GATED and the fourth
        # (`factory-parity`) is a declared instrument, so every one of them is
        # accounted for by exactly one of the two tuples.
        gated = set(hlib.ANOMALY_TOKENS)
        instruments = {r for r, _ in hlib.ANOMALY_REASONS_RAISED_UNGATED}
        for reason in ("clock-not-ready", "retire-not-held", "anchor-resolve-fail"):
            self.assertIn(reason, gated,
                          "%s was promoted 2026-08-04; a raise must be a HIT" % (reason,))
            self.assertIn(reason, self.raised)
        self.assertIn("factory-parity", instruments)
        self.assertNotIn("factory-parity", gated)
        self.assertIn("factory-parity", self.raised)

    def test_the_promoted_seven_are_gated_and_no_longer_merely_reported(self):
        # The promotion itself, pinned as membership rather than as a count so the
        # failure message names the token that moved. Measured basis (recorded on
        # hlib.ANOMALY_TOKENS): five V1 real-geometry dwells at ~130 nonzero-ghost
        # probe frames each, fresh S1.4/S1.6/S1.7 2026-08-04 readings with the probe
        # exercised and silent, and 155 tracer-on historical runs with zero raises.
        promoted = ("icon-teleport", "icon-off-orbit", "gap-vs-retire",
                    "decision-vs-old-truth", "clock-not-ready", "retire-not-held",
                    "anchor-resolve-fail")
        instruments = {r for r, _ in hlib.ANOMALY_REASONS_RAISED_UNGATED}
        for reason in promoted:
            with self.subTest(reason=reason):
                self.assertIn(reason, hlib.ANOMALY_TOKENS)
                self.assertNotIn(reason, instruments)
                # A gated token that nothing raises is the dead-token mistake the
                # icon-jump retirement fixed; do not repeat it by promoting prose.
                self.assertIn(reason, self.raised)
        self.assertEqual(14, len(hlib.ANOMALY_TOKENS))
        # ORDER IS A CONTRACT: grep_anomaly_tokens returns hits in tuple order, so
        # the original six must stay first and keep their relative order.
        self.assertEqual(
            ["line-blink", "parity-drift", "decision-vs-truth",
             "polyline-orbit-overlap", "rigid-seam-tangent-discontinuity",
             "ledger-vs-truth"],
            list(hlib.ANOMALY_TOKENS[:6]),
            "the six pre-promotion tokens must stay first and in order - "
            "hit-list determinism is pinned off this ordering")

    def test_dead_token_is_retired_from_the_gate_and_raised_by_nothing(self):
        # Post-2026-07-29 bookkeeping: a dead token is OUT of the gated set (it can
        # never fire, so gating it was coverage theatre) and stays named here so a
        # producer appearing for it reds instead of quietly leaving it ungated.
        for dead in hlib.ANOMALY_TOKENS_DEAD:
            self.assertNotIn(dead, hlib.ANOMALY_TOKENS,
                             "%s is RETIRED; it must not be back in the gated set "
                             "without a producer" % (dead,))
            self.assertNotIn(dead, self.raised,
                             "%s is retired as dead but a producer now raises it - "
                             "re-decide whether it belongs in ANOMALY_TOKENS or in "
                             "ANOMALY_REASONS_RAISED_UNGATED" % (dead,))

    def test_status_doc_names_every_report_only_instrument(self):
        # autotest-status.md is declared the single status authority for this
        # system, and its gate-0 list is what a reader acts on. It said FIVE for
        # one commit; keep it tied to the tuple rather than to any number spelled
        # out here. The cell asserts NAME PRESENCE per entry and deliberately counts
        # nothing, so it stays correct as the set grows (it went two -> three on
        # 2026-08-09) - which is also why it never caught the count prose that did
        # drift.
        with open(AUTOTEST_STATUS_DOC, encoding="utf-8") as fh:
            body = fh.read()
        for reason, _ in hlib.ANOMALY_REASONS_RAISED_UNGATED:
            self.assertIn("`%s`" % (reason,), body,
                          "the status doc omits the report-only reason %s" % (reason,))
        self.assertNotIn("five further reasons are ungated", body.lower())

    def test_todo_doc_table_lists_every_raised_reason(self):
        # The todo-doc table was the DECISION INPUT for the reconciliation and is
        # now the RECORD of it (which reasons were promoted 2026-08-04, which two
        # stayed instruments), so an incomplete table is the defect, not a cosmetic
        # slip. Promotion removes no row - every raised reason still needs one.
        with open(TODO_DOC, encoding="utf-8") as fh:
            body = fh.read()
        # The entry heading was struck through when the reconciliation resolved
        # (2026-08-04), so the anchor carries the `~~` markers.
        start = body.index("## ~~The harness anomaly token set has drifted")
        entry = body[start:body.index("\n## ", start + 10)]
        for reason in sorted(self.raised):
            self.assertIn("| `%s` |" % (reason,), entry,
                          "the ground-truth table omits %s" % (reason,))

    def test_documented_producers_are_real_file_line_pairs(self):
        # The guard site (where the decision is made) is what the table names; for a
        # wrapper-routed raise (`factory-parity`) that is NOT the EmitAnomaly line,
        # so this checks the cited file:line rather than reusing the scanner's output.
        root = os.path.dirname(HARNESS_ROOT)
        for reason, producer in hlib.ANOMALY_REASONS_RAISED_UNGATED:
            rel, _, lineno = producer.rpartition(":")
            path = os.path.join(root, rel.replace("/", os.sep))
            self.assertTrue(os.path.isfile(path), producer)
            with open(path, encoding="utf-8-sig") as fh:
                lines = fh.read().split("\n")
            window = "\n".join(lines[max(0, int(lineno) - 1):int(lineno) + 8])
            self.assertRegex(
                window, r"EmitAnomaly|Emit(ClockNotReady|RetireNotHeld|"
                        r"AnchorResolveFail|FactoryParity)",
                "%s (%s) does not name an anomaly raise" % (reason, producer))


class AutotestStatusScenarioCountTests(unittest.TestCase):
    """`autotest-status.md` is the declared single status authority for this
    system, so a count in it that its own table contradicts is the exact class of
    error the doc rules exist to prevent.

    It happened twice in one day: the live-proven section grew by two and the
    not-yet-live-run header was decremented to 13 while its table still had 14
    rows, which also broke the stated 30-scenario total. This cell re-derives all
    of it - per-section header counts, their sum, and the sum against the number
    of committed spec files - so the next drift reds here instead of in review."""

    HEADER_RE = re.compile(r"^### (?P<name>.+?)\((?P<count>\d+)\)")
    TOTAL_RE = re.compile(r"^## Test cases \(all (?P<count>\d+) committed scenarios\)")
    SEPARATOR_RE = re.compile(r"^\|[\s\-|]+\|$")

    @classmethod
    def setUpClass(cls):
        with open(AUTOTEST_STATUS_DOC, encoding="utf-8") as fh:
            cls.lines = fh.read().split("\n")

    def _sections(self):
        """{section header line: (declared count, counted table rows)}."""
        out, current = {}, None
        for line in self.lines:
            header = self.HEADER_RE.match(line)
            if line.startswith("## "):
                # An H2 CLOSES the open H3 section. The doc carries non-scenario
                # tables after the last counted section (`## Run telemetry`), and
                # without this the EVA section swallowed all seven of its rows
                # and read 11. Found by this cell on the 2026-07-26 merge, which
                # is the first tree to hold both the counter and that section.
                current = None
                continue
            if line.startswith("### "):
                current = line if header else None
                if header:
                    out[line] = [int(header.group("count")), 0]
                continue
            if current is None or not line.startswith("| "):
                continue
            if line.startswith("| Test case") or self.SEPARATOR_RE.match(line):
                continue
            out[current][1] += 1
        return out

    def test_every_section_header_matches_its_table(self):
        sections = self._sections()
        self.assertGreaterEqual(len(sections), 3, "no counted sections parsed")
        for header, (declared, counted) in sorted(sections.items()):
            self.assertEqual(declared, counted,
                             "%s declares %d but has %d table rows"
                             % (header.strip(), declared, counted))

    def test_the_sections_sum_to_the_stated_total(self):
        total = None
        for line in self.lines:
            m = self.TOTAL_RE.match(line)
            if m:
                total = int(m.group("count"))
                break
        self.assertIsNotNone(total, "the 'Test cases (all N ...)' header is missing")
        self.assertEqual(total, sum(c for _, c in self._sections().values()))

    def test_the_stated_total_matches_the_committed_spec_files(self):
        committed = [n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")]
        total = next(int(self.TOTAL_RE.match(l).group("count"))
                     for l in self.lines if self.TOTAL_RE.match(l))
        self.assertEqual(len(committed), total,
                         "the status doc's scenario total disagrees with "
                         "harness/scenarios/")


# ---------------------------------------------------------------------------
# Admission reuse over provlib.
# ---------------------------------------------------------------------------


class AdmissionTests(unittest.TestCase):
    """Guards the M-A6 seam reuse: a drifted / missing / incomplete instance must
    be refused before any launch; an identical projection admits."""

    def _manifest(self, **over):
        base = {
            "profile": "stock-minimal", "kspVersion": "1.12.5",
            "components": {"parsek": {"sha256": "abc"}},
            "settingsDeltasApplied": {}, "devSourcedMods": {},
        }
        base.update(over)
        return base

    def test_identical_admits(self):
        exp = self._manifest()
        d = hlib.admit_instance(exp, self._manifest())
        self.assertTrue(d.admitted)
        self.assertEqual(d.diff, ())

    def test_drift_refused(self):
        exp = self._manifest()
        act = self._manifest(components={"parsek": {"sha256": "DIFFERENT"}})
        d = hlib.admit_instance(exp, act)
        self.assertFalse(d.admitted)
        self.assertEqual(d.subkind, "drift")
        self.assertTrue(len(d.diff) >= 1)

    def test_missing_manifest_refused(self):
        d = hlib.admit_instance(self._manifest(), None)
        self.assertEqual(d.subkind, "manifest-missing")

    def test_incomplete_marker_refused(self):
        d = hlib.admit_instance(self._manifest(), self._manifest(), incomplete_marker=True)
        self.assertEqual(d.subkind, "provision-incomplete")

    def test_build_expected_admission_shape(self):
        exp = hlib.build_expected_admission("stock-minimal", "1.12.5",
                                            {"parsek": {"sha256": "x"}}, {}, {})
        self.assertEqual(exp["profile"], "stock-minimal")
        self.assertIn("components", exp)


# ---------------------------------------------------------------------------
# Result record serialization + schema gate.
# ---------------------------------------------------------------------------


class ResultSerializationTests(unittest.TestCase):
    """Guards: a result must round-trip to an equal object and serialize
    byte-identically for identical inputs (else diffs churn or a field drops and
    the coverage parser breaks); a future schema must be refused, not mis-parsed."""

    def _result(self):
        return {
            "schema": 1, "runId": "2026-07-12_1830_B10", "scenarioId": "B10",
            "verdict": "PASS", "wallSeconds": 412,
            "verifiers": {"analyzer": {"status": "PASS", "red": 0}},
        }

    def test_round_trip(self):
        r = self._result()
        self.assertEqual(hlib.deserialize_result(hlib.serialize_result(r)), r)

    def test_byte_identical(self):
        a = hlib.serialize_result(self._result())
        # a freshly-built equal dict with keys inserted in a different order
        r2 = {}
        r2["verdict"] = "PASS"
        r2["schema"] = 1
        r2["scenarioId"] = "B10"
        r2["runId"] = "2026-07-12_1830_B10"
        r2["wallSeconds"] = 412
        r2["verifiers"] = {"analyzer": {"red": 0, "status": "PASS"}}
        self.assertEqual(a, hlib.serialize_result(r2))

    def test_schema_ok(self):
        ok, _ = hlib.check_schema({"schema": 1})
        self.assertTrue(ok)

    def test_future_schema_refused(self):
        ok, msg = hlib.check_schema({"schema": 2})
        self.assertFalse(ok)
        self.assertIn("newer", msg)

    def test_missing_schema_refused(self):
        ok, _ = hlib.check_schema({})
        self.assertFalse(ok)


# ---------------------------------------------------------------------------
# Coverage computation.
# ---------------------------------------------------------------------------


class CoverageTests(unittest.TestCase):
    """Guards: a red run must never count as coverage (false 'exhaustive'
    signal), a genuinely covered value must not show uncovered, an XPASS surfaces
    its amber, and the report is deterministic (unreadable diffs otherwise)."""

    REGISTRY = {
        "schema": 1,
        "D8": {"values": ["funds", "science", "reputation"]},
        "D14": {"values": ["career"]},
    }

    def _specs(self):
        return [
            {"id": "B10", "dimensionsCovered": {"D8": ["funds", "science"], "D14": ["career"]},
             "expectedFail": {"bugId": ""}},
            {"id": "R10", "dimensionsCovered": {"D8": ["reputation"]},
             "expectedFail": {"bugId": "R10-reaim"}},
        ]

    def test_pass_run_shows_last_green(self):
        results = [{"scenarioId": "B10", "verdict": "PASS", "endedUtc": "2026-07-12T18:00:00Z"}]
        rep = hlib.compute_coverage(self._specs(), results, self.REGISTRY)
        funds = next(cv for cv in rep.values if cv.value == "funds")
        self.assertEqual(funds.last_green, "2026-07-12T18:00:00Z")
        self.assertIn("B10", funds.covered_by)

    def test_parsek_fail_is_not_coverage(self):
        results = [{"scenarioId": "B10", "verdict": "PARSEK-FAIL", "endedUtc": "2026-07-12T18:00:00Z"}]
        rep = hlib.compute_coverage(self._specs(), results, self.REGISTRY)
        funds = next(cv for cv in rep.values if cv.value == "funds")
        self.assertIsNone(funds.last_green)

    def test_uncovered_value(self):
        rep = hlib.compute_coverage([self._specs()[0]], [], self.REGISTRY)
        rep_vals = {cv.value: cv for cv in rep.values}
        self.assertEqual(rep_vals["reputation"].status, "UNCOVERED")
        self.assertIn("D8/reputation", rep.uncovered)

    def test_expected_fail_only_value_tagged(self):
        rep = hlib.compute_coverage(self._specs(), [], self.REGISTRY)
        rep_vals = {cv.value: cv for cv in rep.values}
        self.assertEqual(rep_vals["reputation"].status, "EXPECTED-FAIL:R10-reaim")

    def test_xpass_surfaced_in_rollup(self):
        results = [{"scenarioId": "R10", "verdict": "XPASS", "endedUtc": "2026-07-12T18:00:00Z"}]
        rep = hlib.compute_coverage(self._specs(), results, self.REGISTRY)
        self.assertEqual(rep.rollup["xpass"], 1)
        self.assertIn("R10-reaim", rep.expected_fail_table)

    def test_deterministic_json(self):
        results = [{"scenarioId": "B10", "verdict": "PASS", "endedUtc": "2026-07-12T18:00:00Z"}]
        a = hlib.coverage_to_json_obj(hlib.compute_coverage(self._specs(), results, self.REGISTRY))
        b = hlib.coverage_to_json_obj(hlib.compute_coverage(self._specs(), results, self.REGISTRY))
        import json
        self.assertEqual(json.dumps(a, sort_keys=True), json.dumps(b, sort_keys=True))

    def test_txt_lines(self):
        rep = hlib.compute_coverage(self._specs(), [], self.REGISTRY)
        txt = hlib.coverage_to_txt(rep)
        self.assertTrue(any("D8 reputation coveredBy=1 lastGreen=never EXPECTED-FAIL:R10-reaim" in l
                            for l in txt.splitlines()))

    def test_real_registry_denominator(self):
        # Every real registry value appears exactly once in the coverage output.
        reg = load_registry()
        rep = hlib.compute_coverage([], [], reg)
        pairs = [(cv.dimension, cv.value) for cv in rep.values]
        self.assertEqual(len(pairs), len(set(pairs)))
        self.assertIn(("D15", "timeline-projection"), pairs)
        self.assertEqual(len([p for p in pairs if p[0] == "D15"]), 1)


# ---------------------------------------------------------------------------
# Cross-run duration record (2026-07-25 telemetry audit, finding G5).
# ---------------------------------------------------------------------------


def _dresult(scenario, verdict, wall, ended, run_id=None):
    row = {"schema": hlib.SCHEMA_VERSION, "scenarioId": scenario,
           "verdict": verdict, "wallSeconds": wall, "endedUtc": ended}
    if run_id is not None:
        row["runId"] = run_id
    return row


class DurationRecordTests(unittest.TestCase):
    """Guards the record that did not exist: nothing durable knew how long a
    scenario takes, so the B12 spec claimed B11 was the SHORTER run for four
    measured runs each while B11 read 1315-1319 s and B12 read 626-627 s."""

    def _b12(self):
        return [_dresult("B12-minmus-orbit", "PASS", w,
                         "2026-07-25T0%d:00:00Z" % i)
                for i, w in enumerate((627, 627, 627, 626), start=1)]

    def test_measured_b11_vs_b12_medians_are_the_real_ordering(self):
        b11 = [_dresult("B11-mun-orbit", "PASS", w,
                        "2026-07-25T0%d:00:00Z" % i)
               for i, w in enumerate((1315, 1319, 1317, 1317), start=1)]
        d = hlib.compute_durations(b11 + self._b12())
        self.assertEqual(d["B11-mun-orbit"]["n"], 4)
        self.assertEqual(d["B12-minmus-orbit"]["n"], 4)
        # B11 is the LONGER scenario, the opposite of what the spec claimed.
        self.assertGreater(d["B11-mun-orbit"]["p50"],
                           d["B12-minmus-orbit"]["p50"])
        self.assertEqual(d["B12-minmus-orbit"]["p50"], 627.0)
        self.assertEqual(d["B12-minmus-orbit"]["last"], 626.0)

    def test_non_pass_results_are_excluded(self):
        """An INVALID that died on a budget measures the BOUND, not the
        scenario; folding it in would drag the median toward the timeout."""
        rows = self._b12() + [
            _dresult("B12-minmus-orbit", "INVALID", 4200, "2026-07-25T09:00:00Z"),
            _dresult("B12-minmus-orbit", "KILLED", 4700, "2026-07-25T10:00:00Z"),
        ]
        d = hlib.compute_durations(rows)
        self.assertEqual(d["B12-minmus-orbit"]["n"], 4)
        self.assertEqual(d["B12-minmus-orbit"]["last"], 626.0)

    def test_single_sample_is_its_own_percentiles_and_never_warns(self):
        d = hlib.compute_durations(
            [_dresult("S1", "PASS", 500, "2026-07-25T01:00:00Z")])
        self.assertEqual(d["S1"], {"n": 1, "p50": 500.0, "p95": 500.0,
                                   "last": 500.0, "lastVsP50": 1.0,
                                   "samples": {"2026-07-25T01:00:00Z": 500.0}})
        self.assertEqual(hlib.duration_regressions(d), [])

    def test_too_few_samples_never_warns_even_on_a_huge_outlier(self):
        rows = [_dresult("S1", "PASS", 500, "2026-07-25T01:00:00Z"),
                _dresult("S1", "PASS", 5000, "2026-07-25T02:00:00Z")]
        d = hlib.compute_durations(rows)
        self.assertEqual(d["S1"]["n"], 2)
        self.assertGreater(d["S1"]["lastVsP50"], hlib.DURATION_WARN_FACTOR)
        self.assertEqual(hlib.duration_regressions(d), [])  # n < 3

    def test_outlier_last_warns_once_enough_samples_exist(self):
        rows = [_dresult("S1", "PASS", 600, "2026-07-25T01:00:00Z"),
                _dresult("S1", "PASS", 600, "2026-07-25T02:00:00Z"),
                _dresult("S1", "PASS", 600, "2026-07-25T03:00:00Z"),
                _dresult("S1", "PASS", 1800, "2026-07-25T04:00:00Z")]
        d = hlib.compute_durations(rows)
        self.assertEqual(d["S1"]["p50"], 600.0)
        self.assertEqual(d["S1"]["last"], 1800.0)
        self.assertEqual(d["S1"]["lastVsP50"], 3.0)
        self.assertEqual(hlib.duration_regressions(d), ["S1"])

    def test_warn_boundary_is_strictly_greater_than_the_factor(self):
        # last == exactly 1.5 * p50 does NOT warn; a hair over does.
        at = [_dresult("S1", "PASS", 100, "2026-07-25T01:00:00Z"),
              _dresult("S1", "PASS", 100, "2026-07-25T02:00:00Z"),
              _dresult("S1", "PASS", 150, "2026-07-25T03:00:00Z")]
        d = hlib.compute_durations(at)
        self.assertEqual(d["S1"]["lastVsP50"], 1.5)
        self.assertEqual(hlib.duration_regressions(d), [])
        over = at[:2] + [_dresult("S1", "PASS", 151, "2026-07-25T03:00:00Z")]
        self.assertEqual(hlib.duration_regressions(hlib.compute_durations(over)),
                         ["S1"])

    def test_last_is_by_ended_utc_not_input_order(self):
        rows = [_dresult("S1", "PASS", 900, "2026-07-25T03:00:00Z"),
                _dresult("S1", "PASS", 600, "2026-07-25T01:00:00Z"),
                _dresult("S1", "PASS", 600, "2026-07-25T02:00:00Z")]
        d = hlib.compute_durations(rows)
        self.assertEqual(d["S1"]["last"], 900.0)

    def test_malformed_rows_are_skipped(self):
        rows = [{"verdict": "PASS", "wallSeconds": 10},              # no id
                {"scenarioId": "S1", "verdict": "PASS"},             # no wall
                {"scenarioId": "S1", "verdict": "PASS",
                 "wallSeconds": "600"},                              # not numeric
                _dresult("S1", "PASS", 600, "2026-07-25T01:00:00Z")]
        d = hlib.compute_durations(rows)
        self.assertEqual(d["S1"]["n"], 1)

    def test_empty_input_yields_an_empty_record(self):
        self.assertEqual(hlib.compute_durations([]), {})
        self.assertEqual(hlib.duration_regressions({}), [])

    def test_percentile_is_nearest_rank(self):
        vals = [1.0, 2.0, 3.0, 4.0]
        self.assertEqual(hlib._percentile(vals, 50.0), 2.0)
        self.assertEqual(hlib._percentile(vals, 95.0), 4.0)
        self.assertEqual(hlib._percentile([7.0], 95.0), 7.0)

    def test_samples_ride_the_entry_keyed_by_ended_utc(self):
        d = hlib.compute_durations(self._b12())
        self.assertEqual(sorted(d["B12-minmus-orbit"]["samples"]),
                         ["2026-07-25T01:00:00Z", "2026-07-25T02:00:00Z",
                          "2026-07-25T03:00:00Z", "2026-07-25T04:00:00Z"])

    def test_one_result_file_per_run_id_cannot_double_count(self):
        """results/<runId>.json is ONE file per run, so two rows carrying the
        same runId can only be a caller passing the same run twice."""
        rows = [_dresult("S1", "PASS", 600, "2026-07-25T01:00:00Z", run_id="r1"),
                _dresult("S1", "PASS", 600, "2026-07-25T01:00:01Z", run_id="r1")]
        d = hlib.compute_durations(rows)
        self.assertEqual(d["S1"]["n"], 1)


class DurationLedgerMergeTests(unittest.TestCase):
    """MAJOR-2 (2026-07-26 review): the ledger stores SAMPLES, not a summary.

    Recomputing the committed record from this checkout's gitignored results/
    dir wiped 23 of 24 scenarios (observed live 2026-07-25); merging only the
    MISSING scenarios forward still downgraded the measured one from n=5 to
    n=1, which is below DURATION_MIN_SAMPLES and therefore DISARMS the warn.
    """

    # The real committed B11 entry on 2026-07-25, summary-only (pre-samples).
    LEGACY_B11 = {"n": 5, "p50": 1317.0, "p95": 1319.0, "last": 1318.0,
                  "lastVsP50": 1.001}

    def _prior(self):
        return {
            "B11-mun-orbit": dict(self.LEGACY_B11),
            "B12-minmus-orbit": {"n": 4, "p50": 627.0, "p95": 627.0,
                                 "last": 626.0, "lastVsP50": 0.998},
            "B6-minmus-flyby": {"n": 3, "p50": 409.0, "p95": 426.0,
                                "last": 409.0, "lastVsP50": 1.0},
        }

    def test_a_checkout_that_measured_one_scenario_keeps_the_others(self):
        """BLOCKER-1: the wipe. One fresh-worktree B11 run must not delete B12
        and B6 from the committed record."""
        merged = hlib.merge_durations(
            self._prior(), {"B11-mun-orbit": {"2026-07-26T10:00:00Z": 1400.0}})
        self.assertEqual(sorted(merged),
                         ["B11-mun-orbit", "B12-minmus-orbit", "B6-minmus-flyby"])
        self.assertEqual(merged["B12-minmus-orbit"],
                         self._prior()["B12-minmus-orbit"])
        self.assertEqual(merged["B6-minmus-flyby"],
                         self._prior()["B6-minmus-flyby"])

    def test_the_measured_scenario_keeps_its_n_and_stays_armed(self):
        """MAJOR-2: the n=5 -> n=1 downgrade the old tests never exercised. The
        legacy entry carries no samples, so this is the BOOTSTRAP case: n is
        max(prior_n, samples) rather than a sum, because a long-lived worktree's
        results dir holds exactly the samples the committed n already counted.
        A fresh worktree therefore KEEPS n=5 and the warn stays armed."""
        merged = hlib.merge_durations(
            self._prior(), {"B11-mun-orbit": {"2026-07-26T10:00:00Z": 1400.0}})
        entry = merged["B11-mun-orbit"]
        self.assertEqual(entry["n"], 5)
        self.assertGreaterEqual(entry["n"], hlib.DURATION_MIN_SAMPLES)
        self.assertEqual(entry["last"], 1400.0)
        self.assertEqual(entry["samples"], {"2026-07-26T10:00:00Z": 1400.0})
        # From the NEXT merge on the incremental rule applies.
        again = hlib.merge_durations(
            merged, {"B11-mun-orbit": {"2026-07-26T10:00:00Z": 1400.0,
                                       "2026-07-27T10:00:00Z": 1310.0}})
        self.assertEqual(again["B11-mun-orbit"]["n"], 6)

    def test_a_long_lived_results_dir_cannot_double_count_after_truncation(self):
        """The trap this rule exists for. results/ accumulates, so once the tail
        truncates, the samples that AGED OUT are still in the results dir and
        absent from the tail -- re-adding them as new turned 25 real samples
        into n=41 on the very next run."""
        all_samples = {"2026-07-25T%02d:00:00Z" % i: 100.0 + i
                       for i in range(1, 26)}
        ledger = hlib.merge_durations({}, {"S1": all_samples})
        self.assertEqual(ledger["S1"]["n"], 25)
        self.assertEqual(len(ledger["S1"]["samples"]),
                         hlib.DURATION_SAMPLE_TAIL)
        # The next run re-reads the SAME dir plus one new result.
        all_samples["2026-07-26T01:00:00Z"] = 130.0
        ledger = hlib.merge_durations(ledger, {"S1": all_samples})
        self.assertEqual(ledger["S1"]["n"], 26)
        # ...and again with nothing new at all.
        ledger = hlib.merge_durations(ledger, {"S1": all_samples})
        self.assertEqual(ledger["S1"]["n"], 26)

    def test_a_sample_older_than_the_watermark_is_skipped(self):
        ledger = hlib.merge_durations({}, {"S1": {"2026-07-25T05:00:00Z": 500.0}})
        stale = hlib.merge_durations(ledger,
                                     {"S1": {"2026-07-25T01:00:00Z": 900.0}})
        self.assertEqual(stale["S1"]["n"], 1)
        self.assertEqual(stale["S1"]["samples"], {"2026-07-25T05:00:00Z": 500.0})

    def test_the_old_behaviour_would_have_disarmed_the_warn(self):
        """The regression this fix exists for, pinned: recomputing the entry
        from THIS checkout's single result yields n=1, and n=1 is below the
        arming gate, so a genuinely slow run could never warn."""
        recomputed = hlib.compute_durations(
            [_dresult("B11-mun-orbit", "PASS", 1400, "2026-07-26T10:00:00Z")])
        self.assertEqual(recomputed["B11-mun-orbit"]["n"], 1)
        self.assertLess(recomputed["B11-mun-orbit"]["n"],
                        hlib.DURATION_MIN_SAMPLES)
        merged = hlib.merge_durations(
            self._prior(), {"B11-mun-orbit": {"2026-07-26T10:00:00Z": 1400.0}})
        self.assertGreaterEqual(merged["B11-mun-orbit"]["n"],
                                hlib.DURATION_MIN_SAMPLES)

    def test_merging_the_same_samples_twice_is_idempotent(self):
        fresh = {"S1": {"2026-07-26T10:00:00Z": 500.0}}
        once = hlib.merge_durations({}, fresh)
        twice = hlib.merge_durations(once, fresh)
        self.assertEqual(twice, once)
        self.assertEqual(twice["S1"]["n"], 1)
        thrice = hlib.merge_durations(twice, fresh)
        self.assertEqual(thrice["S1"]["n"], 1)

    def test_a_re_read_results_dir_does_not_re_count_its_own_samples(self):
        """The live shape: a long-lived worktree's results/ dir accumulates, so
        every run's merge re-reads samples the ledger already holds."""
        rows = [_dresult("S1", "PASS", 500, "2026-07-25T01:00:00Z", run_id="r1"),
                _dresult("S1", "PASS", 520, "2026-07-25T02:00:00Z", run_id="r2")]
        ledger = hlib.merge_durations({}, hlib.duration_samples(rows))
        self.assertEqual(ledger["S1"]["n"], 2)
        rows.append(_dresult("S1", "PASS", 510, "2026-07-25T03:00:00Z",
                             run_id="r3"))
        ledger = hlib.merge_durations(ledger, hlib.duration_samples(rows))
        self.assertEqual(ledger["S1"]["n"], 3)
        self.assertEqual(sorted(ledger["S1"]["samples"].values()),
                         [500.0, 510.0, 520.0])

    def test_the_sample_tail_is_bounded_and_n_keeps_counting(self):
        ledger: dict = {}
        for i in range(1, 26):
            ledger = hlib.merge_durations(
                ledger, {"S1": {"2026-07-25T%02d:00:00Z" % i: 100.0 + i}})
        entry = ledger["S1"]
        self.assertEqual(entry["n"], 25)
        self.assertEqual(len(entry["samples"]), hlib.DURATION_SAMPLE_TAIL)
        # The TAIL is kept: the oldest 15 aged out, the newest 10 remain.
        self.assertEqual(sorted(entry["samples"]),
                         ["2026-07-25T%02d:00:00Z" % i for i in range(16, 26)])
        self.assertEqual(entry["last"], 125.0)

    def test_percentiles_are_recomputed_over_the_union_not_the_run(self):
        prior = hlib.merge_durations(
            {}, {"S1": {"2026-07-25T0%d:00:00Z" % i: 600.0
                        for i in range(1, 4)}})
        merged = hlib.merge_durations(prior,
                                      {"S1": {"2026-07-26T01:00:00Z": 1800.0}})
        self.assertEqual(merged["S1"]["n"], 4)
        self.assertEqual(merged["S1"]["p50"], 600.0)   # union median, not 1800
        self.assertEqual(merged["S1"]["last"], 1800.0)
        self.assertEqual(merged["S1"]["lastVsP50"], 3.0)
        self.assertEqual(hlib.duration_regressions(merged), ["S1"])

    def test_a_scenario_only_this_run_measured_is_added(self):
        merged = hlib.merge_durations(self._prior(),
                                      {"NEW-1": {"2026-07-26T10:00:00Z": 42.0}})
        self.assertIn("NEW-1", merged)
        self.assertEqual(merged["NEW-1"]["n"], 1)

    def test_a_committed_sample_wins_a_key_collision(self):
        prior = hlib.merge_durations({}, {"S1": {"2026-07-25T01:00:00Z": 500.0}})
        merged = hlib.merge_durations(prior,
                                      {"S1": {"2026-07-25T01:00:00Z": 9999.0}})
        self.assertEqual(merged["S1"]["samples"],
                         {"2026-07-25T01:00:00Z": 500.0})
        self.assertEqual(merged["S1"]["n"], 1)

    def test_a_malformed_committed_entry_is_dropped_not_carried(self):
        """MINOR-8: the ledger is committed and hand-editable, and the warn's
        log line formats last/p50/p95/n. A partial entry must never reach it."""
        prior = {"X": {"n": 5, "lastVsP50": 2.0}}          # no last/p50/p95
        merged = hlib.merge_durations(prior, {})
        self.assertEqual(merged, {})
        self.assertEqual(hlib.duration_regressions(prior), [])

    def test_a_malformed_entry_that_this_run_measured_is_rebuilt(self):
        prior = {"X": {"n": 5, "lastVsP50": 2.0}}
        merged = hlib.merge_durations(prior,
                                      {"X": {"2026-07-26T01:00:00Z": 300.0}})
        self.assertEqual(merged["X"]["n"], 5)   # the readable n still counts
        self.assertEqual(merged["X"]["last"], 300.0)
        self.assertEqual(merged["X"]["p50"], 300.0)

    def test_garbage_samples_values_are_ignored(self):
        prior = {"X": {"n": 2, "p50": 10.0, "p95": 10.0, "last": 10.0,
                       "lastVsP50": 1.0,
                       "samples": {"2026-07-25T01:00:00Z": "nope",
                                   "2026-07-25T02:00:00Z": 10.0,
                                   "": 5.0}}}
        merged = hlib.merge_durations(prior, {})
        self.assertEqual(merged["X"]["samples"], {"2026-07-25T02:00:00Z": 10.0})
        self.assertEqual(merged["X"]["n"], 2)

    def test_empty_inputs(self):
        self.assertEqual(hlib.merge_durations({}, {}), {})
        self.assertEqual(hlib.merge_durations(None, None), {})
        self.assertEqual(hlib.duration_samples([]), {})


# ---------------------------------------------------------------------------
# Flake computation + quarantine.
# ---------------------------------------------------------------------------


class FlakeTests(unittest.TestCase):
    """Guards: the 20% threshold must not be mis-evaluated, KILLED must count
    toward the rate (a KILLED-heavy scenario cannot escape quarantine), and a
    benched scenario must not auto-unquarantine on a window it never ran in."""

    def _attempts(self, outcomes, base="2026-07-12T00:00:00Z"):
        return [{"utc": base, "outcome": o} for o in outcomes]

    def test_three_of_ten_invalid_quarantines(self):
        att = self._attempts(["INVALID"] * 3 + ["PASS"] * 7)
        r = hlib.compute_flake(att)
        self.assertAlmostEqual(r.rate, 0.30)
        self.assertTrue(r.quarantined)

    def test_one_of_ten_not_quarantined(self):
        att = self._attempts(["INVALID"] * 1 + ["PASS"] * 9)
        r = hlib.compute_flake(att)
        self.assertFalse(r.quarantined)

    def test_killed_counts_toward_rate(self):
        att = self._attempts(["INVALID"] * 2 + ["KILLED"] * 1 + ["PASS"] * 7)
        r = hlib.compute_flake(att)
        self.assertAlmostEqual(r.rate, 0.30)
        self.assertTrue(r.quarantined)

    def test_quarantine_is_sticky(self):
        # A subsequent quiet (all-PASS) window must stay quarantined (human-only).
        att = self._attempts(["PASS"] * 10)
        r = hlib.compute_flake(att, prior_quarantined=True)
        self.assertTrue(r.quarantined)

    def test_out_of_window_attempts_dropped(self):
        old = self._attempts(["INVALID"] * 5, base="2026-06-01T00:00:00Z")
        recent = self._attempts(["PASS"] * 5, base="2026-07-12T00:00:00Z")
        r = hlib.compute_flake(old + recent, now="2026-07-13T00:00:00Z")
        self.assertEqual(r.total, 5)  # only the recent window
        self.assertFalse(r.quarantined)


class SubprocessRecoveredFlakeAccrualTests(unittest.TestCase):
    """SF1: a subprocess-recovered flake writes only ONE PASS result JSON, so without
    accrual its in-attempt tooling fault is INVISIBLE to the flake numerator and a
    chronically-wedging pwsh tool never reaches quarantine. flake_attempt_entries emits
    a synthetic INVALID alongside the PASS -- mirroring a whole-attempt flakedThenPassed
    -- so it accrues. Regressions guarded: (1) a recovered flake counting toward
    nothing; (2) a NON-recovered retry (whole-attempt fallback) being double-counted
    (it has its OWN INVALID result JSON)."""

    def _result(self, verdict, retries=None, utc="2026-07-12T00:00:00Z"):
        r = {"scenarioId": "S", "verdict": verdict, "endedUtc": utc, "verifiers": {}}
        if retries is not None:
            r["verifiers"]["subprocessRetry"] = retries
        return r

    def _retry(self, stage="analyzer", recovered=True):
        return {"stage": stage, "retried": True, "attempt1": "INVALID/tooling",
                "attempt2": "PASS" if recovered else "INVALID/tooling",
                "recovered": recovered}

    def test_clean_pass_contributes_one_entry(self):
        entries = hlib.flake_attempt_entries(self._result("PASS"))
        self.assertEqual([e["outcome"] for e in entries], ["PASS"])

    def test_recovered_retry_adds_synthetic_invalid(self):
        entries = hlib.flake_attempt_entries(self._result("PASS", [self._retry()]))
        self.assertEqual([e["outcome"] for e in entries], ["PASS", "INVALID"])
        # The synthetic entry shares the result's UTC so the window math is stable.
        self.assertTrue(all(e["utc"] == "2026-07-12T00:00:00Z" for e in entries))

    def test_recovered_helper_filters_non_recovered(self):
        both = [self._retry("analyzer", True), self._retry("logValidate", False)]
        rec = hlib.recovered_subprocess_retries(self._result("PASS", both))
        self.assertEqual(len(rec), 1)
        self.assertEqual(rec[0]["stage"], "analyzer")

    def test_non_recovered_retry_no_synthetic(self):
        # A retry that did NOT recover fails the whole attempt INVALID -> its OWN result
        # JSON accrues; a synthetic INVALID here would double-count.
        entries = hlib.flake_attempt_entries(
            self._result("INVALID", [self._retry(recovered=False)]))
        self.assertEqual([e["outcome"] for e in entries], ["INVALID"])

    def test_recovered_retry_on_failing_scenario_still_accrues(self):
        # Item 10: a wedging-analyzer flake that recovered inside a boot whose OWN verdict
        # is PARSEK-FAIL / EXPECTED-FAIL (non-numerator) must STILL accrue a synthetic
        # INVALID -- the prior PASS-only gate let a scenario that reds for a genuine
        # Parsek reason mask its own tooling flake forever.
        for verdict in ("PARSEK-FAIL", "EXPECTED-FAIL", "XPASS"):
            with self.subTest(verdict=verdict):
                entries = hlib.flake_attempt_entries(self._result(verdict, [self._retry()]))
                self.assertEqual([e["outcome"] for e in entries], [verdict, "INVALID"])

    def test_recovered_retry_on_invalid_verdict_no_double_count(self):
        # Item 10 guard: an INVALID/KILLED result already accrues via its own entry, so a
        # recovered retry on it adds NO synthetic (would double-count).
        for verdict in ("INVALID", "KILLED"):
            with self.subTest(verdict=verdict):
                entries = hlib.flake_attempt_entries(self._result(verdict, [self._retry()]))
                self.assertEqual([e["outcome"] for e in entries], [verdict])

    def test_recovered_flake_accrues_toward_quarantine(self):
        # End-to-end through compute_flake: three clean passes + one recovered-flake PASS.
        # Without SF1 the numerator would be 0 (all four verdicts are PASS); with it the
        # recovered flake accrues one INVALID.
        results = [self._result("PASS") for _ in range(3)]
        results.append(self._result("PASS", [self._retry()]))
        attempts = []
        for r in results:
            attempts.extend(hlib.flake_attempt_entries(r))
        fr = hlib.compute_flake(attempts)
        self.assertEqual(fr.total, 5, "3 clean PASS + (PASS + synthetic INVALID)")
        self.assertEqual(fr.numerator, 1, "the recovered subprocess flake accrued")

    def test_missing_field_is_a_clean_pass(self):
        # A result predating SF1 (no verifiers / no subprocessRetry) is a clean pass.
        self.assertEqual(hlib.recovered_subprocess_retries({}), [])
        self.assertEqual([e["outcome"] for e in hlib.flake_attempt_entries(
            {"verdict": "PASS", "endedUtc": ""})], ["PASS"])


class FlakeEnvironmentExemptionTests(unittest.TestCase):
    """A scenario-agnostic environment INVALID must not quarantine a scenario.

    Quarantine is STICKY and human-only, so one venv/lock fault permanently
    benched whatever scenario it landed on. Measured: CL-3-refly-crew-tombstone
    went rate=0.50 quarantined=True off ONE tooling-venv INVALID in a fresh
    worktree whose gitignored missions/.venv had never been bootstrapped -- a
    fault that never booted KSP and would have hit any scenario selected.
    """

    @staticmethod
    def _entry(outcome, subkind="", utc="2026-08-04T12:00:00Z"):
        return {"utc": utc, "outcome": outcome, "subkind": subkind}

    def test_the_measured_cl3_pair_no_longer_quarantines(self):
        # The exact shape from the incident: one tooling-venv INVALID, then a PASS.
        fr = hlib.compute_flake([self._entry("INVALID", "tooling-venv"),
                                 self._entry("PASS")])
        self.assertEqual(fr.total, 1, "the venv fault leaves the DENOMINATOR too")
        self.assertEqual(fr.numerator, 0)
        self.assertEqual(fr.rate, 0.0)
        self.assertFalse(fr.quarantined)

    def test_every_exempt_subkind_is_dropped(self):
        for subkind in hlib.FLAKE_EXEMPT_INVALID_SUBKINDS:
            with self.subTest(subkind=subkind):
                fr = hlib.compute_flake([self._entry("INVALID", subkind)])
                self.assertEqual((fr.total, fr.numerator), (0, 0))
                self.assertFalse(fr.quarantined)

    def test_scenario_attributable_invalids_still_count(self):
        # spec-invalid is that scenario's OWN spec; the retryable subkinds ARE the
        # scenario being unstable. Quarantine must still catch every one of them.
        for subkind in ("spec-invalid", "boot-crash", "driver-stage",
                        "seam-timeout", "mission", "tooling-krpc",
                        "autopilot-flake", "admission"):
            with self.subTest(subkind=subkind):
                fr = hlib.compute_flake([self._entry("INVALID", subkind),
                                         self._entry("PASS")])
                self.assertEqual((fr.total, fr.numerator), (2, 1))
                self.assertTrue(fr.quarantined, "rate 0.50 > 0.20")

    def test_killed_is_never_exempt(self):
        # A KILLED is a real budget overrun of THIS scenario, whatever the subkind.
        for subkind in hlib.FLAKE_EXEMPT_INVALID_SUBKINDS:
            with self.subTest(subkind=subkind):
                self.assertFalse(hlib.flake_entry_is_exempt(
                    self._entry("KILLED", subkind)))

    def test_legacy_entries_without_subkind_keep_old_arithmetic(self):
        # Ledger entries written before the field shipped must not change meaning.
        fr = hlib.compute_flake([{"utc": "2026-08-04T12:00:00Z", "outcome": "INVALID"},
                                 {"utc": "2026-08-04T12:00:00Z", "outcome": "PASS"}])
        self.assertEqual((fr.total, fr.numerator), (2, 1))
        self.assertTrue(fr.quarantined)

    def test_entries_carry_subkind_from_the_result(self):
        entries = hlib.flake_attempt_entries(
            {"verdict": "INVALID", "subkind": "tooling-venv", "endedUtc": "x"})
        self.assertEqual(entries[0]["subkind"], "tooling-venv")

    def test_recovered_subprocess_synthetic_is_never_exempt(self):
        # The synthetic INVALID stands for a real tooling flake of THIS run, so it
        # must keep accruing even when the result itself carries an exempt subkind.
        synthetic = {"utc": "x", "outcome": "INVALID", "subkind": ""}
        self.assertFalse(hlib.flake_entry_is_exempt(synthetic))

    def test_an_all_environment_window_is_not_quarantined(self):
        # A worktree whose venv is broken runs nothing but tooling-venv: total
        # collapses to 0 and nothing is quarantined (no information, no verdict).
        fr = hlib.compute_flake([self._entry("INVALID", "tooling-venv")
                                 for _ in range(5)])
        self.assertEqual((fr.total, fr.numerator, fr.rate), (0, 0, 0.0))
        self.assertFalse(fr.quarantined)

    def test_exemption_cannot_unstick_an_existing_quarantine(self):
        # Sticky-and-human-only is unchanged: this fix stops a scenario BECOMING
        # quarantined by misattribution, it does not silently release one.
        fr = hlib.compute_flake([self._entry("INVALID", "tooling-venv")],
                                prior_quarantined=True)
        self.assertTrue(fr.quarantined)


# ---------------------------------------------------------------------------
# Log-line format (log-assertion support).
# ---------------------------------------------------------------------------


class LogLineTests(unittest.TestCase):
    """Guards: every classify branch carries a non-empty reason so the harness
    log ([Harness][LEVEL][Step]) can reconstruct why a run was classified (an
    undebuggable unattended run is the whole failure the harness log prevents)."""

    def test_format_shape(self):
        self.assertEqual(hlib.format_log_line("Info", "Classify", "verdict=PASS"),
                         "[Harness][Info][Classify] verdict=PASS")

    def test_every_verdict_branch_has_a_reason(self):
        d, v = _clean_pass_facts()
        cases = [
            (dict(d), dict(v)),  # PASS
        ]
        # exercise a spread of branches and assert each reason is non-empty
        for mutate in [
            lambda dd, vv: dd.__setitem__("admission_ok", False),
            lambda dd, vv: vv.__setitem__("killed", True),
            lambda dd, vv: dd.__setitem__("boot_crashed", True),
            lambda dd, vv: dd.__setitem__("batch_crashed", True),
            lambda dd, vv: vv.__setitem__("log_validate_failed", True),
        ]:
            dd, vv = _clean_pass_facts()
            mutate(dd, vv)
            r = hlib.classify_verdict(dd, vv, {"bugId": ""}, 1, "once")
            self.assertTrue(r.reason, "verdict %s must carry a reason" % r.verdict)
            line = hlib.format_log_line("Info", "Classify",
                                        "verdict=%s reason=%s" % (r.verdict, r.reason))
            self.assertTrue(line.startswith("[Harness][Info][Classify]"))

    def test_xpass_amber_reason(self):
        d, v = _clean_pass_facts()
        r = hlib.classify_verdict(d, v, {"bugId": "R10-reaim", "signature_matched": False}, 1, "once")
        self.assertEqual(r.verdict, "XPASS")
        self.assertIn("R10-reaim", r.reason)


if __name__ == "__main__":
    unittest.main()


class SpecExpectsLiveRecordingTests(unittest.TestCase):
    """Regression for the first live S1.4 run: REC-rule suppression keyed on
    recordings.count.max==0 red-flagged REC-001/REC-003 on an injection-seeded
    scenario that never records live. The key is now the spec's own
    live-recording expectation. Fails if the derivation loses either trigger
    (StartRecording step / autoRecordOnLaunch=true pin) or starts treating
    injected-save recordings as live."""

    def _spec(self, steps):
        return {"driver": {"steps": steps}}

    def test_injection_seeded_no_live_recording(self):
        spec = self._spec([
            {"cmd": "LoadGame", "args": {"save": "x", "name": "persistent"}},
            {"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "false"}},
            {"cmd": "RunTests", "args": {"category": "RecordingInvariants"}},
            {"cmd": "FlushAndQuit", "args": {}},
        ])
        self.assertFalse(hlib.spec_expects_live_recording(spec))
        prof = hlib.select_logvalidate_profile(hlib.spec_expects_live_recording(spec), False)
        self.assertTrue(prof.suppress_recording_rules)

    def test_start_recording_step_expects_live(self):
        spec = self._spec([{"cmd": "StartRecording", "args": {}}])
        self.assertTrue(hlib.spec_expects_live_recording(spec))

    def test_autorecord_pin_true_expects_live(self):
        spec = self._spec([{"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "true"}}])
        self.assertTrue(hlib.spec_expects_live_recording(spec))
        prof = hlib.select_logvalidate_profile(True, False)
        self.assertFalse(prof.suppress_recording_rules)

    def test_mc2_auto_record_on_eva_pin_true_expects_live(self):
        # F6: EVA-2's ONLY recording trigger is the autoRecordOnEva=true pin (no
        # StartRecording, no autoRecordOnLaunch). Without learning it, this genuinely-
        # recording run's REC-001/REC-003 rules would be SUPPRESSED (oracle invariant 5
        # silently false).
        spec = self._spec([
            {"cmd": "SetSetting", "args": {"name": "autoRecordOnEva", "value": "true"}},
            {"cmd": "EvaExit", "args": {"settleSeconds": "10"}},
        ])
        self.assertTrue(hlib.spec_expects_live_recording(spec))
        prof = hlib.select_logvalidate_profile(hlib.spec_expects_live_recording(spec), False)
        self.assertFalse(prof.suppress_recording_rules)

    def test_mc2_auto_record_on_eva_pin_false_not_live(self):
        # An autoRecordOnEva=false pin does NOT imply live recording.
        spec = self._spec([{"cmd": "SetSetting", "args": {"name": "autoRecordOnEva", "value": "false"}}])
        self.assertFalse(hlib.spec_expects_live_recording(spec))


# ---------------------------------------------------------------------------
# M-B1 autopilot spec validation (design "Spec-validation rules for kind =
# autopilot"; Test Plan "Autopilot spec validation accept + each reject").
# ---------------------------------------------------------------------------


def _autopilot_spec():
    """A well-formed kind='autopilot' spec (design B1 example, trimmed). Built
    inline (no file I/O) so the accept/reject cells are self-contained."""
    return {
        "schema": 1,
        "id": "B1-pad-hop",
        "tier": "daily",
        "instanceProfile": "stock-minimal",
        "tags": ["B1", "flown"],
        "fixture": {
            "saveTemplate": "fixtures/saves/b1-pad-craft",
            "injectedRecordings": "none",
        },
        "driver": {
            "kind": "autopilot",
            "mission": "b1_pad_hop",
            "missionParams": {
                "throttle": 1.0,
                "apoapsisWindowMeters": {"min": 6000, "max": 30000},
                "landedSituations": ["LANDED", "SPLASHED"],
                "ascentTimeoutSeconds": 90,
            },
            "steps": [
                {"cmd": "LoadGame", "args": {"save": "${runSave}", "name": "persistent"},
                 "expect": "OK", "budget": 240},
                {"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "true"},
                 "expect": "OK"},
                {"phase": "mission", "expect": "MISSION-OK", "budget": 600},
                {"cmd": "CommitTree", "expect": "OK"},
                {"cmd": "FlushAndQuit", "expect": "OK"},
            ],
        },
        "expectations": {
            "recordings": {"count": {"min": 1, "max": 1}},
            "logContracts": {"required": ["Recording started", "Recording stopped"],
                             "forbidden": [r"\[Parsek\]\[ERROR\]"]},
        },
        "runtime": {"budgetSeconds": 900},
        "retry": {"policy": "once"},
        "expectedFail": {"bugId": "", "subkind": ""},
    }


def _mission_schemas():
    """The injected registry surface (design: parsed shell-side from
    <mission>.schema.toml, passed pure)."""
    return {
        "b1_pad_hop": {
            "params": {
                "throttle": {"required": True, "type": "float", "min": 0.0, "max": 1.0},
                "apoapsisWindowMeters": {"required": True, "type": "window"},
                "ascentTimeoutSeconds": {"required": False, "type": "int", "min": 1},
            }
        }
    }


class AutopilotSpecValidationTests(unittest.TestCase):
    """Guards (design Test Plan): a well-formed autopilot spec validates, and each
    malformed one rejects with the right reason. A malformed autopilot spec that
    slipped through would launch KSP and waste a boot on a mission that cannot run;
    a valid one wrongly rejected blocks every flown scenario."""

    _DEFAULT_SCHEMAS = object()  # sentinel: distinguish "use default" from explicit None

    def _v(self, mutate=None, schemas=_DEFAULT_SCHEMAS):
        spec = copy.deepcopy(_autopilot_spec())
        if mutate is not None:
            mutate(spec)
        reg = _mission_schemas() if schemas is self._DEFAULT_SCHEMAS else schemas
        return hlib.validate_spec(spec, {}, mission_schemas=reg)

    def test_accept_well_formed(self):
        v = self._v()
        self.assertTrue(v.ok, "well-formed autopilot spec must validate; errors=%s" % (v.errors,))

    def test_reject_a_unknown_mission(self):
        v = self._v(lambda s: s["driver"].__setitem__("mission", "no_such_mission"))
        self.assertFalse(v.ok)
        self.assertTrue(any("unknown mission" in e for e in v.errors))

    def test_reject_b_missing_required_param(self):
        v = self._v(lambda s: s["driver"]["missionParams"].pop("throttle"))
        self.assertFalse(v.ok)
        self.assertTrue(any("throttle" in e and "required" in e for e in v.errors))

    def test_reject_c_window_min_gt_max(self):
        def m(s):
            s["driver"]["missionParams"]["apoapsisWindowMeters"] = {"min": 30000, "max": 6000}
        v = self._v(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("apoapsisWindowMeters" in e and "min" in e and "max" in e for e in v.errors))

    def test_reject_c_window_min_gt_max_without_schema(self):
        # Window well-formedness is schema-INDEPENDENT: it rejects even when the
        # shell has injected no schema registry (mission_schemas=None defers the
        # mission-existence check but the min > max window check is structural).
        def m(s):
            s["driver"]["missionParams"]["apoapsisWindowMeters"] = {"min": 30000, "max": 6000}
        v = self._v(m, schemas=None)
        self.assertFalse(v.ok)
        self.assertTrue(any("window min" in e for e in v.errors))

    def test_reject_d_mission_step_before_loadgame(self):
        def m(s):
            steps = s["driver"]["steps"]
            mission = [x for x in steps if x.get("phase") == "mission"][0]
            steps.remove(mission)
            steps.insert(0, mission)  # mission now at index 0, before LoadGame
        v = self._v(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("must follow a LoadGame" in e for e in v.errors))

    def test_reject_e_two_mission_steps(self):
        def m(s):
            s["driver"]["steps"].insert(3, {"phase": "mission", "expect": "MISSION-OK", "budget": 600})
        v = self._v(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("exactly one mission-kind step" in e for e in v.errors))

    def test_reject_f_mission_step_wrong_expect(self):
        def m(s):
            for x in s["driver"]["steps"]:
                if x.get("phase") == "mission":
                    x["expect"] = "OK"
        v = self._v(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("mission step must be" in e and "MISSION-OK" in e for e in v.errors))

    def test_mission_step_bad_budget_rejected(self):
        def m(s):
            for x in s["driver"]["steps"]:
                if x.get("phase") == "mission":
                    x["budget"] = 0
        v = self._v(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("mission step budget" in e for e in v.errors))

    def test_mission_step_under_seam_kind_rejected(self):
        # A mission-kind step only belongs under an autopilot driver: a seam driver
        # carrying one is malformed (guards against a half-converted spec).
        def m(s):
            s["driver"]["kind"] = "seam"
        v = self._v(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("requires driver.kind 'autopilot'" in e for e in v.errors))

    def test_seam_spec_still_validates_unchanged(self):
        # The autopilot extension is ADDITIVE: a real seam spec still validates.
        reg = load_registry()
        spec = load_spec("B10-career-passive-safety.toml")
        v = hlib.validate_spec(spec, reg)
        self.assertTrue(v.ok, "seam spec must be unaffected by the autopilot addition; errors=%s" % (v.errors,))


class MissionSchemaDeclaredTypeTests(unittest.TestCase):
    """Guard: every ``type`` a committed harness/missions/*.schema.toml declares must
    be one hlib actually understands.

    The regression this names is SILENT, not a load error. Three committed schemas
    shipped ``type = "boolean"`` (b17_duna_direct, b26_laythe_vall,
    m3_loop_arrival_dwell), which is not one of ``_check_param_type``'s spellings, so
    the declaration fell through the whole if/elif chain, returned no errors, and
    validated ANY value -- a string, a list, 7. A param that LOOKS type-checked and is
    checked by nothing is worse than an undeclared one, and nothing red'd when it
    happened; it was caught by eye while a fourth schema was being authored.

    The accepted set is READ FROM ``hlib.MISSION_PARAM_TYPES`` rather than re-listed
    here, so this class cannot drift from the validator it guards. The later cells
    walk the SAME silent-hole class in its other directions, because the bug shape is
    "a declaration nobody reads", not the ``type`` key specifically:

      - a misspelled FACET (``minimum`` for ``min``, ``require`` for ``required``);
      - a whole block under a misspelled TOP-LEVEL key (``[parameters.x]``), where a
        ``required = true`` param is silently never required;
      - bounds on a type whose branch never consults them (``min`` on a ``string``).

    All three are zero-instance today; the guards exist so they stay zero.

    DERIVATION DISCIPLINE: the sets are read out of hlib's own AST, never its source
    TEXT. A regex over ``inspect.getsource`` also sees comments and docstrings, and
    that breaks in BOTH directions -- a commented-out ``elif`` branch keeps its
    literal, so the pin stays GREEN while that type falls through unchecked again
    (this file's own ``_dry_run_exit_code`` docstring records a getsource cell being
    defeated exactly that way, twice); and a prose comment merely MENTIONING a
    spelling reds the pin with a message whose obvious fix -- add it to the constant
    -- is the one move that reopens the hole. ``test_every_accepted_type_has_a_live_branch``
    is the behavioural backstop for dispatch refactors the AST walk cannot follow."""

    # ---- derivations: hlib's AST is the single source; nothing is re-listed here ----

    @staticmethod
    def _ptype_literals():
        """The type spellings ``_check_param_type``'s dispatch actually BRANCHES on."""
        tree = ast.parse(textwrap.dedent(inspect.getsource(hlib._check_param_type)))
        found = set()
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Compare)
                    and isinstance(node.left, ast.Name) and node.left.id == "ptype"):
                continue
            for op, comparator in zip(node.ops, node.comparators):
                if isinstance(op, ast.NotIn):
                    continue  # the gate itself (`ptype not in MISSION_PARAM_TYPES`)
                for sub in ast.walk(comparator):
                    if isinstance(sub, ast.Constant) and isinstance(sub.value, str):
                        found.add(sub.value)
        return found

    @staticmethod
    def _decl_facet_reads():
        """The per-param facet keys hlib actually READS (``decl.get("...")``)."""
        found = set()
        for fn in (hlib._check_param_type, hlib._validate_mission_params):
            tree = ast.parse(textwrap.dedent(inspect.getsource(fn)))
            for node in ast.walk(tree):
                if (isinstance(node, ast.Call)
                        and isinstance(node.func, ast.Attribute)
                        and node.func.attr == "get"
                        and isinstance(node.func.value, ast.Name)
                        and node.func.value.id == "decl"
                        and node.args
                        and isinstance(node.args[0], ast.Constant)
                        and isinstance(node.args[0].value, str)):
                    found.add(node.args[0].value)
        return found

    def _declared_rows(self):
        """(schema filename, param name, decl table) for every committed declaration.

        Anti-vacuity lives HERE so every sweep below shares one non-empty population:
        an empty missions dir, or schemas with no ``[params.*]`` tables, reds instead
        of passing a guard that scanned nothing."""
        rows = []
        paths = sorted(glob.glob(os.path.join(MISSIONS_DIR, "*.schema.toml")))
        self.assertTrue(paths, "no mission schemas found under %s" % MISSIONS_DIR)
        for path in paths:
            with open(path, "rb") as fh:
                schema = tomllib.load(fh)
            for pname, decl in (schema.get("params") or {}).items():
                rows.append((os.path.basename(path), pname, decl or {}))
        self.assertTrue(rows, "no [params.*] declarations found to check")
        return rows

    # ---- the guards ----

    def test_every_declared_type_is_one_hlib_accepts(self):
        bad = [(f, p, d.get("type")) for (f, p, d) in self._declared_rows()
               if d.get("type") not in hlib.MISSION_PARAM_TYPES]
        self.assertEqual(
            [], bad,
            "mission schema declares a type hlib does not accept (accepted: %s). "
            "Such a declaration checks NOTHING -- it validates any value while "
            "looking checked: %s" % (", ".join(hlib.MISSION_PARAM_TYPES), bad))

    def test_accepted_set_matches_the_dispatch_chains_own_literals(self):
        """The constant and the if/elif chain must not gain a spelling separately: one
        in the CHAIN but not the constant is a live branch the gate made dead, and one
        in the CONSTANT but not the chain falls through unchecked -- the exact hole
        this class closes.

        READ A FAILURE THIS WAY: if a spelling is missing from the constant, the fix
        is to add its BRANCH, or to delete it from the chain. Adding it to the
        constant to make this cell green is the one move that reopens the hole."""
        self.assertEqual(
            set(hlib.MISSION_PARAM_TYPES), self._ptype_literals(),
            "hlib.MISSION_PARAM_TYPES and _check_param_type's dispatch disagree. A "
            "spelling in the constant with no branch validates ANYTHING: give it a "
            "branch or drop it -- do NOT reconcile by widening the constant")

    def test_every_accepted_type_has_a_live_branch(self):
        """Behavioural backstop to the AST pin above, which can only see a dispatch it
        recognises: a sentinel no branch can accept must be REJECTED under every
        accepted spelling, and that holds only while each spelling's branch is live.
        Catches a branch commented out, deleted, or refactored into a shape the walk
        above reads as absent -- each of which puts that type back to checking
        nothing while the two named sets still agree on paper."""
        sentinel = object()
        dead = [t for t in hlib.MISSION_PARAM_TYPES
                if not hlib._check_param_type("p", sentinel, {"type": t})]
        self.assertEqual([], dead,
                         "accepted type(s) whose branch checks NOTHING: %s" % (dead,))

    def test_an_unknown_spelling_rejects_instead_of_passing_anything(self):
        """The gate is live, not documentation. Before it, ``"boolean"`` returned []
        for every value; now it names the declaration as the fault."""
        errs = hlib._check_param_type("padAlignEjection", "not-a-bool", {"type": "boolean"})
        self.assertTrue(errs, "an unknown declared type must not validate silently")
        self.assertTrue(any("unknown type" in e for e in errs), errs)
        # Even a value that WOULD have been fine is rejected: the declaration is the
        # fault, and a schema author must see it rather than have it pass by luck.
        self.assertTrue(hlib._check_param_type("padAlignEjection", True, {"type": "boolean"}))
        # A decl with NO type at all is the same fault, deliberately: a presence-only
        # declaration type-checks nothing. Zero committed declarations omit it, and
        # the sweep above keeps it that way -- this pins the choice rather than
        # leaving it an accident of the gate's `not in` test.
        self.assertTrue(hlib._check_param_type("padAlignEjection", True, {"required": True}))
        # ... while the corrected spelling checks the value for real, both ways.
        self.assertEqual([], hlib._check_param_type("padAlignEjection", True,
                                                    {"type": "bool"}))
        self.assertTrue(hlib._check_param_type("padAlignEjection", "true",
                                               {"type": "bool"}))

    def test_every_declared_facet_key_is_one_hlib_actually_reads(self):
        """Mirror of the type hole: hlib reads a fixed set of per-param facet keys and
        IGNORES every other, so ``minimum = 0.0`` (for ``min``) or ``require = true``
        (for ``required``) silently drops a bound / makes a param optional with
        nothing red. The subset assert is the derivation's own sanity check -- it reds
        if the AST walk stops finding the reads, rather than letting an empty set flag
        every declaration as stray, and it reds if hlib DROPS one of the four."""
        read_facets = self._decl_facet_reads()
        self.assertLessEqual({"required", "type", "min", "max"}, read_facets,
                             "hlib no longer reads a facet this guard derives from; "
                             "derived set was %s" % (sorted(read_facets),))
        stray = [(f, p, facet) for (f, p, d) in self._declared_rows()
                 for facet in d if facet not in read_facets]
        self.assertEqual([], stray,
                         "mission schema declares a facet hlib never reads (read: %s); "
                         "it is silently ignored, not applied: %s"
                         % (sorted(read_facets), stray))

    def test_no_declaration_is_parked_under_a_top_level_key_hlib_never_reads(self):
        """Third mirror, one level up: ``_validate_mission_params`` reads
        ``schema["params"]`` and NOTHING else, so an entire block under
        ``[parameters.x]`` / ``[param.x]`` is read by nobody -- and a ``required =
        true`` param declared there is silently never required (measured: the
        validator returns [] for a spec that omits it). Same fat-finger class as
        ``boolean``, except it hides a whole table rather than one key."""
        self.assertTrue(self._declared_rows())  # shared non-empty population
        stray = []
        for path in sorted(glob.glob(os.path.join(MISSIONS_DIR, "*.schema.toml"))):
            with open(path, "rb") as fh:
                schema = tomllib.load(fh)
            stray += [(os.path.basename(path), key) for key in schema if key != "params"]
        self.assertEqual([], stray,
                         "top-level table hlib never reads (it reads `params` only), "
                         "so every declaration under it is inert: %s" % (stray,))

    def test_bounds_are_only_declared_on_types_that_consult_them(self):
        """Fourth mirror: ``min``/``max`` are read ONLY inside the numeric branch, so a
        bound on a string / bool / list / window declaration reads as checked and
        checks nothing -- the same silent-nothing shape, spelled with keys that ARE
        legitimately read facets elsewhere, which is exactly why the facet sweep above
        cannot see it. The bound-consulting set is MEASURED (declare a bound, offer a
        value that violates it) rather than re-listed, so it tracks the dispatch."""
        consults_bounds = {t for t in hlib.MISSION_PARAM_TYPES
                           if hlib._check_param_type("p", 7, {"type": t, "min": 5}) == []
                           and hlib._check_param_type("p", 1, {"type": t, "min": 5}) != []}
        self.assertTrue(consults_bounds,
                        "no accepted type was observed to consult a declared bound; "
                        "the measurement below would be vacuous")
        bad = [(f, p, d.get("type"), sorted(k for k in ("min", "max") if k in d))
               for (f, p, d) in self._declared_rows()
               if ("min" in d or "max" in d) and d.get("type") not in consults_bounds]
        self.assertEqual([], bad,
                         "bound declared on a type whose branch never reads it "
                         "(bounds are consulted only for %s): %s"
                         % (sorted(consults_bounds), bad))


class ClassifyMissionStepTests(unittest.TestCase):
    """Guards (design Test Plan "Mission-verdict -> harness classification"): each
    mission verdict maps to the right (met, subkind) and every failure subkind is
    retryable-once; and the orthogonality -- a MISSION-OK flight whose verifier
    chain reds still classifies PARSEK-FAIL. Fails if an assertion miss poisons the
    Parsek-defect bucket (INVALID(mission) misread as PARSEK-FAIL) or a mis-recorded
    good flight is swallowed as a mission problem."""

    def test_ok_is_met(self):
        met, subkind = hlib.classify_mission_step("MISSION-OK")
        self.assertTrue(met)
        self.assertEqual(subkind, "")

    def test_full_mapping_and_retryable(self):
        cases = {
            "MISSION-CONNECT-TIMEOUT": "tooling-krpc",
            "MISSION-ASSERT-FAIL": "mission",
            "MISSION-FLAKE": "autopilot-flake",
            "MISSION-ERROR": "tooling-mission",
        }
        for verdict, subkind in cases.items():
            met, sk = hlib.classify_mission_step(verdict)
            self.assertFalse(met, verdict)
            self.assertEqual(sk, subkind, verdict)
            # Each failure subkind is retryable-once (feeds the driver-stage retry).
            self.assertIn(sk, hlib.RETRYABLE_INVALID_SUBKINDS, verdict)
            r = hlib.Verdict(hlib.VERDICT_INVALID, sk, True, "mission %s" % verdict)
            self.assertTrue(hlib.should_retry(r, 1, "once"), verdict)

    def test_none_or_unknown_fails_closed(self):
        # Missing result / unknown verdict fails CLOSED to tooling-mission (edge 12),
        # never a silent met.
        for bad in (None, "MISSION-WAT", ""):
            met, sk = hlib.classify_mission_step(bad)
            self.assertFalse(met)
            self.assertEqual(sk, "tooling-mission")

    def test_orthogonality_mission_ok_but_parsek_mis_records_is_parsek_fail(self):
        # A MISSION-OK flight (mission step MET, driver valid) whose verifier chain
        # reds -- here the analyzer reds the produced recording -- is PARSEK-FAIL,
        # NOT a mission INVALID. classify_mission_step gates the flight; the verifier
        # chain gates whether Parsek recorded it right; the two are orthogonal.
        met, _ = hlib.classify_mission_step("MISSION-OK")
        self.assertTrue(met)
        d, v = _clean_pass_facts()  # driver valid == the MET mission step
        v["analyzer"] = hlib.AnalyzerVerdict("PARSEK-FAIL", "analyzer", "INV3")
        r = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(r.verdict, "PARSEK-FAIL")

    def test_carveout_mission_ok_missing_recording_is_parsek_fail(self):
        # Edge 13 carve-out: a MISSION-OK run whose recording EVIDENCE is missing
        # (expectations.recordings.count.min unmet) is PARSEK-FAIL(expectation), a
        # verdict-driving Parsek defect -- NOT a driver-INVALID a retry would paper
        # over. A good flight Parsek failed to record is exactly what M-B1 catches.
        met, _ = hlib.classify_mission_step("MISSION-OK")
        self.assertTrue(met)
        d, v = _clean_pass_facts()
        v["expectation_mismatch"] = True
        r = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual((r.verdict, r.subkind), ("PARSEK-FAIL", "expectation"))
        self.assertFalse(hlib.should_retry(r, 1, "once"))


def _outcome(step_id, cmd, met, expect="OK", verdict="OK", msg=""):
    """A StepOutcome as evaluate_response_stream would build it."""
    return hlib.StepOutcome(step_id, cmd, expect, verdict, verdict is not None, met, msg=msg)


class PostMissionOutcomeGateTests(unittest.TestCase):
    """Guards the EVA-4 fail-open (2026-07-25, todo-and-known-bugs "EVA-4 ... the
    MISSION still returns MISSION-OK").

    THE INCIDENT. `eva4_atmo_chute` is a HANDOFF mission: its success terminal is
    EVA-WINDOW, reached while the craft is still airborne and crewed, and the mission
    SUBPROCESS EXITS there. The kerbal EVA vessel is created afterwards, by the seam
    step EvaExit. So no assertion the mission machine can hold observes the kerbal:
    on flight 3 the canopy was cut mid-descent, the kerbal died, and the mission
    reported `MISSION-OK reason=all telemetry assertions met`. The only channel that
    SAW it was the EvaChuteDeploy step's own `eva-chute-kerbal-lost` terminal, and
    run.py's autopilot carve-out made every post-mission step non-gating, so
    driverValidity read PASS beside `allExpectedMet: false`. The run red'd solely on
    spec-authored expectation regexes.

    THE CLOSURE. Post-mission OUTCOME verbs gate; post-mission RECORDING verbs stay
    non-gating exactly as before. Fails if a new verb slips in without a role, if the
    role split drifts, if a recording verb starts gating (which would resurrect the
    driver-INVALID-papers-over-it problem the carve-out exists to prevent), or if a
    dead subject stops reddening the run."""

    def test_table_is_total_over_implemented_verbs(self):
        missing = [v for v in hlib.IMPLEMENTED_SEAM_VERBS
                   if v not in hlib.SEAM_VERB_POST_MISSION_ROLE]
        self.assertEqual([], missing,
                         "every IMPLEMENTED_SEAM_VERBS entry needs an explicit "
                         "SEAM_VERB_POST_MISSION_ROLE row; missing=%s" % (missing,))
        for verb, role in hlib.SEAM_VERB_POST_MISSION_ROLE.items():
            self.assertIn(role, hlib.POST_MISSION_ROLES, verb)

    def test_table_has_no_rows_for_unimplemented_verbs(self):
        extra = [v for v in hlib.SEAM_VERB_POST_MISSION_ROLE
                 if v not in hlib.IMPLEMENTED_SEAM_VERBS]
        self.assertEqual([], extra, "stale post-mission-role rows: %s" % (extra,))

    def test_outcome_set_is_exactly_the_four_eva_verbs(self):
        # The whole gating set. Each one's verdict is a claim about a KERBAL's
        # in-world state that no verifier re-derives; widening this set to a Parsek
        # verb would route a Parsek defect through the wrong subkind.
        outcome = sorted(v for v, r in hlib.SEAM_VERB_POST_MISSION_ROLE.items()
                         if r == hlib.POST_MISSION_ROLE_OUTCOME)
        self.assertEqual(["EvaBoard", "EvaChuteDeploy", "EvaExit", "PlantFlag"], outcome)

    def test_recording_verbs_do_not_gate(self):
        # The ORIGINAL carve-out, preserved: a good flight Parsek then failed to
        # record must still red through the verifier chain, not as a retryable
        # driver-INVALID.
        for verb in ("StopRecording", "CommitTree", "FlushAndQuit", "SaveGame",
                     "RunTests", "InvokeRewind", "SetSetting", "RecordingState"):
            self.assertFalse(hlib.post_mission_step_gates(verb), verb)

    def test_r12_verbs_are_recording_not_outcome(self):
        # Both R12 verbs are `recording`, and each has a concrete reason to be:
        #   ExitToSpaceCenter's OK means "SPACECENTER settled with a game loaded"; the
        #     auto-commit it exists to reach is proven by the spec's pinned commit log
        #     lines, so a Parsek failure to commit belongs in the expectation channel.
        #   SimulateStockSwitchClick's OK deliberately does NOT mean "a switch segment
        #     armed" - the consume site refuses surface targets by design and still
        #     reports OK switched=true - so gating on it would certify nothing.
        # Note the two axes disagree on purpose: both are WORLD-MUTATING on the tail
        # axis (asserted in SeamVerbTailRoleTests) and `recording` here.
        for verb in ("ExitToSpaceCenter", "SimulateStockSwitchClick"):
            with self.subTest(verb=verb):
                self.assertEqual(hlib.POST_MISSION_ROLE_RECORDING,
                                 hlib.SEAM_VERB_POST_MISSION_ROLE[verb])
                self.assertFalse(hlib.post_mission_step_gates(verb))

    def test_player_workflow_verbs_are_recording_not_outcome(self):
        # Both player-workflow verbs are `recording`. The `outcome` set is exactly the
        # verbs whose verdict is a claim about a KERBAL's physical in-world state:
        #   StartLoopPlayback's OK means "the clock reached the next departure window"
        #     - a Parsek clock/playback claim; whether the loop then replayed is proven
        #     by the tracers, the anomaly sweep and the spec's pinned log lines.
        #   EnterWatchMode's OK means "the flight camera is watching recording #N", a
        #     read-back of Parsek's own camera state.
        # Note the two axes disagree on purpose: both are WORLD-MUTATING on the tail
        # axis (asserted in SeamVerbTailRoleTests) and `recording` here.
        for verb in ("StartLoopPlayback", "EnterWatchMode"):
            with self.subTest(verb=verb):
                self.assertEqual(hlib.POST_MISSION_ROLE_RECORDING,
                                 hlib.SEAM_VERB_POST_MISSION_ROLE[verb])
                self.assertFalse(hlib.post_mission_step_gates(verb))

    def test_map_view_verbs_are_recording_not_outcome(self):
        # EnterMapView's OK means "MapView.MapIsEnabled reads true" - a read-back of the
        # game's own camera mode, the EnterWatchMode call one step further out. Gating on
        # it would route a stock camera-switch decline (ConstantMode, CanUseMap off, a
        # MissionSystem block) through the mission-outcome subkind, which is reserved for
        # a flight that failed after the handoff. What the open map is FOR - the
        # ownership-publish half actually running - is proven downstream by the manifest's
        # OWNERSHIP_CHANGE records through the renderCompose row.
        # Note the two axes disagree on purpose: both are WORLD-MUTATING on the tail axis
        # (asserted in SeamVerbTailRoleTests) and `recording` here.
        for verb in ("EnterMapView", "ExitMapView"):
            with self.subTest(verb=verb):
                self.assertEqual(hlib.POST_MISSION_ROLE_RECORDING,
                                 hlib.SEAM_VERB_POST_MISSION_ROLE[verb])
                self.assertFalse(hlib.post_mission_step_gates(verb))

    def test_unknown_verb_does_not_gate(self):
        # Opposite fail-safe direction from SEAM_VERB_TAIL_ROLE, deliberately: an
        # unrecognised verb is a spec fault validate_spec already rejects, and
        # gating on a vocabulary miss would red runs for a typo rather than for an
        # outcome.
        # StopPlayback is the canonical still-RESERVED sample here (StartLoopPlayback
        # held that spot until the player-workflow lane promoted it, at which point it
        # stopped being an unknown verb at all).
        for unknown in ("StopPlayback", "SomeFutureVerb", "", None):
            self.assertFalse(hlib.post_mission_step_gates(unknown), repr(unknown))

    def test_the_eva4_flight3_step_stream_names_the_chute_step(self):
        # The REAL 2026-07-25 stream (results/2026-07-25_1007_EVA-4-atmo-chute.json):
        # ten steps, mission at 0005 MET, EvaChuteDeploy at 0007 ERROR, the three
        # teardown steps OK after it.
        steps = [
            _outcome("0001", "LoadGame", True), _outcome("0002", "SetSetting", True),
            _outcome("0003", "SetSetting", True), _outcome("0004", "SetSetting", True),
            _outcome("0006", "EvaExit", True),
            _outcome("0007", "EvaChuteDeploy", False, verdict="ERROR",
                     msg="eva-chute-kerbal-lost"),
            _outcome("0008", "StopRecording", True), _outcome("0009", "CommitTree", True),
            _outcome("0010", "FlushAndQuit", True),
        ]
        first = hlib.first_unmet_post_mission_outcome(steps, "0005")
        self.assertIsNotNone(first)
        self.assertEqual("EvaChuteDeploy", first.cmd)
        self.assertEqual("eva-chute-kerbal-lost", first.msg)

    def test_a_pre_mission_failure_is_not_an_outcome_miss(self):
        # Pre-mission steps are already gated by run.py's pre_met branch; reporting
        # them here too would double-classify and mask the load-failed subkind.
        steps = [_outcome("0001", "LoadGame", False, verdict="ERROR"),
                 _outcome("0006", "EvaExit", True)]
        self.assertIsNone(hlib.first_unmet_post_mission_outcome(steps, "0005"))

    def test_a_failed_recording_verb_is_not_an_outcome_miss(self):
        steps = [_outcome("0006", "EvaExit", True),
                 _outcome("0009", "CommitTree", False, verdict="ERROR")]
        self.assertIsNone(hlib.first_unmet_post_mission_outcome(steps, "0005"))

    def test_seam_only_driver_reads_none(self):
        # No mission step -> every step already gates through all_expected_met.
        steps = [_outcome("0006", "EvaExit", False, verdict="ERROR")]
        self.assertIsNone(hlib.first_unmet_post_mission_outcome(steps, None))

    def test_a_dead_subject_reds_the_run_as_mission_outcome(self):
        d, v = _clean_pass_facts()
        v["mission_outcome_unmet"] = True
        r = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual((r.verdict, r.subkind), ("PARSEK-FAIL", "mission-outcome"))
        # NEVER retried: a subject that died is a defect to look at, not a flake to
        # re-roll into a green.
        self.assertFalse(hlib.should_retry(r, 1, "once"))
        self.assertIn("mission-outcome", hlib.PARSEK_FAIL_SUBKINDS)

    def test_it_names_the_cause_ahead_of_the_downstream_symptoms(self):
        # Flight 3 tripped FOUR expectation rows (three missing required tokens plus
        # the forbidden [Parsek][ERROR]) - all symptoms of the same dead kerbal. The
        # subkind a sweep reader sees must be the cause.
        d, v = _clean_pass_facts()
        v["mission_outcome_unmet"] = True
        v["expectation_mismatch"] = True
        v["log_validate_failed"] = True
        r = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual("mission-outcome", r.subkind)

    def test_a_quarantine_key_cannot_turn_a_dead_subject_green(self):
        # An `[expectedFail] bugId` with no subkind matches ANY PARSEK-FAIL (the
        # documented v1 adaptation), and EXPECTED-FAIL is GREEN: it is in
        # GREEN_VERDICTS, sets lastGreen in compute_coverage, and exits 0. So a
        # scenario quarantined for one unrelated Parsek defect would have silently
        # absorbed the next subject death - re-opening this PR's own fail-open through
        # the back door. Demoting a subject death must be spelled out.
        d, v = _clean_pass_facts()
        v["mission_outcome_unmet"] = True
        r = hlib.classify_verdict(d, v, {"bugId": "BUG-1", "signature_matched":
                                         hlib.expected_fail_signature_matched(
                                             "PARSEK-FAIL", "mission-outcome", "")},
                                  1, "once")
        self.assertEqual("PARSEK-FAIL", r.verdict)
        self.assertNotIn(r.verdict, hlib.GREEN_VERDICTS)
        # Every OTHER PARSEK-FAIL subkind still demotes bugId-only, unchanged.
        self.assertTrue(hlib.expected_fail_signature_matched(
            "PARSEK-FAIL", "expectation", ""))
        self.assertFalse(hlib.expected_fail_signature_matched(
            "PARSEK-FAIL", "mission-outcome", ""))
        # ... and an EXPLICIT subkind still demotes it (quarantining a known-flaky
        # subject death stays possible, it just has to be named).
        self.assertTrue(hlib.expected_fail_signature_matched(
            "PARSEK-FAIL", "mission-outcome", "mission-outcome"))

    def test_a_refusal_is_a_driver_fault_not_a_flight_outcome(self):
        # The four outcome verbs emit ~30 terminals and most are NOT "the flight failed
        # after handoff". The seam already draws the line: a no-side-effect refusal
        # rides REJECTED, a real terminal rides ERROR. Without the split, a typo'd
        # targetPid on a POST-mission EvaBoard reports PARSEK-FAIL(mission-outcome) and
        # is never retried, while the SAME typo pre-mission reports INVALID(driver-arg)
        # and retries once.
        cases = [
            # (verdict, msg, expect_flight_outcome, expect_driver_subkind)
            ("ERROR", "eva-chute-kerbal-lost", True, ""),
            ("ERROR", "eva-exit-timeout", True, ""),
            ("REJECTED", "no-crew", False, "driver-verdict-mismatch"),
            ("REJECTED", "kerbal-not-aboard", False, "driver-verdict-mismatch"),
            ("REJECTED", "unknown-target", False, "driver-arg"),   # the M-C1 table wins
            ("TIMEOUT", "", False, "seam-timeout"),
            (None, "", False, "driver-stage"),                     # never answered
        ]
        for verdict, msg, want_outcome, want_subkind in cases:
            step = _outcome("0007", "EvaChuteDeploy", False, verdict=verdict, msg=msg)
            is_outcome, subkind = hlib.classify_post_mission_outcome_miss(step)
            self.assertEqual(want_outcome, is_outcome, (verdict, msg))
            self.assertEqual(want_subkind, subkind, (verdict, msg))
            # Whatever the classification, the miss is never silently dropped.
            self.assertTrue(is_outcome or bool(subkind), (verdict, msg))

    def test_every_driver_subkind_it_can_emit_is_retryable(self):
        # A spec/fixture fault routed to the driver stage must retry-once exactly like
        # its pre-mission twin; a subkind outside the retry set would strand it.
        for verdict, msg in (("REJECTED", "no-crew"), ("REJECTED", "unknown-target"),
                             ("TIMEOUT", ""), (None, "")):
            _, subkind = hlib.classify_post_mission_outcome_miss(
                _outcome("0007", "EvaBoard", False, verdict=verdict, msg=msg))
            self.assertIn(subkind, hlib.RETRYABLE_INVALID_SUBKINDS, (verdict, msg))

    def test_a_clean_run_is_untouched(self):
        # Every other scenario in the suite: no post-mission outcome step, or all of
        # them met. The fact defaults False and the verdict stays PASS.
        d, v = _clean_pass_facts()
        self.assertEqual("PASS", hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").verdict)
        v["mission_outcome_unmet"] = False
        self.assertEqual("PASS", hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once").verdict)


class SeamVerbTailRoleTests(unittest.TestCase):
    """Guards (design "The unmet-mission tail"): the per-verb tail-role table is
    TOTAL over the implemented verbs, names exactly the two cleanup verbs, and fails
    SAFE on anything it does not know. A verb promoted from RESERVED (or added, as
    SaveGame and the M-C2 EVA batch were) with no row here would otherwise inherit a
    silent default -- which is how EvaChuteDeploy-class in-world actions end up being
    driven after a mission that never reached its envelope."""

    def test_table_is_total_over_implemented_verbs(self):
        missing = [v for v in hlib.IMPLEMENTED_SEAM_VERBS
                   if v not in hlib.SEAM_VERB_TAIL_ROLE]
        self.assertEqual([], missing,
                         "every IMPLEMENTED_SEAM_VERBS entry needs an explicit "
                         "SEAM_VERB_TAIL_ROLE row; missing=%s" % (missing,))
        for verb, role in hlib.SEAM_VERB_TAIL_ROLE.items():
            self.assertIn(role, hlib.TAIL_ROLES, verb)

    def test_table_has_no_rows_for_unimplemented_verbs(self):
        # A row for a RESERVED / deleted verb is dead metadata that would quietly
        # outlive the verb it describes.
        extra = [v for v in hlib.SEAM_VERB_TAIL_ROLE
                 if v not in hlib.IMPLEMENTED_SEAM_VERBS]
        self.assertEqual([], extra, "stale tail-role rows: %s" % (extra,))

    def test_cleanup_set_is_exactly_stop_recording_and_flush_and_quit(self):
        cleanup = sorted(v for v, r in hlib.SEAM_VERB_TAIL_ROLE.items()
                         if r == hlib.TAIL_ROLE_CLEANUP)
        self.assertEqual(["FlushAndQuit", "StopRecording"], cleanup)

    def test_the_irreversible_in_world_verbs_are_world_mutating(self):
        # EvaExit + EvaChuteDeploy are the EVA-4 flight-1 pair: the two verbs the old
        # drive-the-whole-tail contract fired at 356 m and -277 m/s.
        for verb in ("EvaExit", "EvaChuteDeploy", "EvaBoard", "PlantFlag", "KscAction",
                     "InvokeRewind", "CommitTree", "DiscardTree", "SaveGame"):
            self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                             hlib.seam_verb_tail_role(verb), verb)

    def test_r12_verbs_are_world_mutating_not_cleanup(self):
        # ExitToSpaceCenter is the trap here: it LOOKS like teardown (it leaves the
        # flight scene) but it reaches the pending-tree AUTO-COMMIT on KSC entry, which
        # is CommitTree's durable write arriving by another route - so it inherits
        # CommitTree's call verbatim and must never be driven on an unmet run.
        # FlushAndQuit already owns the quit, so nothing needs it to be cleanup.
        # SimulateStockSwitchClick arms a real StockActionIntentMarker and switches the
        # active vessel, starting a switch-segment branch on the live tree.
        for verb in ("ExitToSpaceCenter", "SimulateStockSwitchClick"):
            with self.subTest(verb=verb):
                self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                                 hlib.seam_verb_tail_role(verb))

    def test_player_workflow_verbs_are_world_mutating(self):
        # StartLoopPlayback advances the game CLOCK to the mission's next departure
        # window (tens of millions of seconds on an interplanetary loop) and drives the
        # spawn queue + ledger recalc across everything crossed - the TimeJump class.
        # EnterWatchMode is the weaker call and is labelled deliberately: it writes no
        # save and no durable record, but it takes an InputLockManager control lock on
        # the active vessel, so it is not the read-only `inert` class either, and this
        # table's fail-safe direction is world-mutating.
        for verb in ("StartLoopPlayback", "EnterWatchMode"):
            with self.subTest(verb=verb):
                self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                                 hlib.seam_verb_tail_role(verb))

    def test_map_view_verbs_are_world_mutating_not_inert_or_cleanup(self):
        # EnterMapView's label is EnterWatchMode's argument verbatim: no save, no career,
        # no durable Parsek record, but stock's MapView.enterMapView() takes an
        # InputLockManager control lock (ControlTypes.MAPVIEW), disables the flight
        # scripts, switches the camera and fires GameEvents.OnMapEntered. `inert` means
        # "reads state or stamps the log, never changes the game" (RecordingState /
        # MissionMark), and locking an unattended vessel's controls is not that.
        #
        # ExitMapView is the trap here, and it is the mirror of ExitToSpaceCenter's: it
        # LOOKS like teardown. It is not `cleanup`, because cleanup is the "must always
        # run" role and each of its two members earns it with an EVIDENCE argument -
        # FlushAndQuit owns the quit (skipping it lets the watchdog KILL the tree and mask
        # the mission subkind) and StopRecording pairs the recorder's log markers. Nothing
        # analogous holds for the map: the render-composition flush runs whether the map
        # is open or shut, and the process is about to die.
        for verb in ("EnterMapView", "ExitMapView"):
            with self.subTest(verb=verb):
                self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                                 hlib.seam_verb_tail_role(verb))

    def test_read_only_verbs_are_inert_not_mislabelled_mutating(self):
        for verb in ("RecordingState", "MissionMark"):
            self.assertEqual(hlib.TAIL_ROLE_INERT, hlib.seam_verb_tail_role(verb), verb)

    def test_unknown_verb_fails_safe_to_world_mutating(self):
        # A verb this table has never heard of (a RESERVED verb driven by a spec that
        # slipped validation, a typo, or a verb implemented on a branch that has not
        # added its row yet) must be presumed to DO something, so the unmet tail skips
        # it. EvaChuteDeploy WAS this case until PR #1348 merged it, which is exactly
        # what the totality cell above now keeps from recurring silently.
        # StopPlayback is the canonical still-RESERVED sample here (StartLoopPlayback
        # held that spot until the player-workflow lane promoted it - it now has a real
        # row, which is what the totality cell above enforces).
        for unknown in ("StopPlayback", "SomeFutureVerb", ""):
            self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                             hlib.seam_verb_tail_role(unknown), unknown)


class PlanUnmetMissionTailTests(unittest.TestCase):
    """Guards (design "The unmet-mission tail"): after an UNMET mission step only the
    CLEANUP tail runs. The motivating incident is EVA-4-atmo-chute flight 1
    (2026-07-24): the mission ASSERT-FAILed with eva-window-missed and the harness
    drove EvaExit + EvaChuteDeploy anyway, EVAing a kerbal out of a pod at terminal
    velocity 356 m above the ground. Fails if the tail plan drives a world-mutating
    verb, drops a cleanup verb, renumbers step ids, or reaches back before the
    mission step."""

    # The EVA-4 tail shape (the incident): two irreversible in-world verbs, then the
    # commit, then teardown.
    EVA4_STEPS = [
        {"cmd": "LoadGame", "args": {"save": "${runSave}"}, "expect": "OK"},
        {"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "true"}},
        {"phase": "mission", "expect": "MISSION-OK", "budget": 900},
        {"cmd": "EvaExit", "args": {"release": "true"}, "expect": "OK", "budget": 120},
        {"cmd": "EvaChuteDeploy", "args": {"awaitDown": "true"}, "expect": "OK", "budget": 420},
        {"cmd": "StopRecording", "expect": "OK"},
        {"cmd": "CommitTree", "expect": "OK"},
        {"cmd": "FlushAndQuit", "expect": "OK"},
    ]

    # A B1-SHAPED tail (a literal, deliberately: the focused cells below assert on an
    # exact minimal shape). Every REAL committed autopilot spec is covered by
    # test_every_committed_autopilot_spec_keeps_its_quit_owner below, which loads them
    # from disk rather than trusting this literal to stay representative.
    B1_STEPS = [
        {"cmd": "LoadGame", "args": {"save": "${runSave}"}, "expect": "OK"},
        {"cmd": "SetSetting", "args": {"name": "autoRecordOnLaunch", "value": "true"}},
        {"phase": "mission", "expect": "MISSION-OK", "budget": 600},
        {"cmd": "CommitTree", "expect": "OK"},
        {"cmd": "FlushAndQuit", "expect": "OK"},
    ]

    def test_eva4_tail_skips_both_in_world_verbs_and_the_commit(self):
        plan = hlib.plan_unmet_mission_tail(self.EVA4_STEPS, 2)
        skipped = [d.cmd for d in plan.dispositions if not d.run]
        ran = [d.cmd for d in plan.dispositions if d.run]
        self.assertEqual(["EvaExit", "EvaChuteDeploy", "CommitTree"], skipped)
        self.assertEqual(["StopRecording", "FlushAndQuit"], ran)
        self.assertEqual((3, 4, 6), plan.skipped_indices)
        self.assertEqual((5, 7), plan.run_indices)

    def test_b1_tail_skips_the_commit_and_still_quits_cleanly(self):
        # FlushAndQuit must survive: it is the QUIT owner, and skipping it would let
        # the watchdog KILL the tree -- KILLED outranks driver-INVALID in
        # classify_verdict, MASKING the mission subkind and dropping the retry.
        plan = hlib.plan_unmet_mission_tail(self.B1_STEPS, 2)
        self.assertEqual(["CommitTree"], [d.cmd for d in plan.dispositions if not d.run])
        self.assertEqual(["FlushAndQuit"], [d.cmd for d in plan.dispositions if d.run])

    def test_forge_tail_skips_the_savegame_that_would_mint_a_bad_fixture(self):
        steps = [
            {"cmd": "LoadGame", "args": {"save": "${runSave}"}, "expect": "OK"},
            {"phase": "mission", "expect": "MISSION-OK"},
            {"cmd": "SaveGame", "expect": "OK"},
            {"cmd": "FlushAndQuit", "expect": "OK"},
        ]
        plan = hlib.plan_unmet_mission_tail(steps, 1)
        self.assertEqual(["SaveGame"], [d.cmd for d in plan.dispositions if not d.run])

    def test_step_ids_are_index_derived_so_a_skip_never_renumbers(self):
        plan = hlib.plan_unmet_mission_tail(self.EVA4_STEPS, 2)
        self.assertEqual(["0004", "0005", "0006", "0007", "0008"],
                         [d.step_id for d in plan.dispositions])
        # The ids the drive loop assigns are the SAME function, so a planned skip row
        # points at the spec step it stands for.
        for d in plan.dispositions:
            self.assertEqual(hlib.step_id_for_index(d.index), d.step_id)

    def test_pre_mission_steps_are_never_in_the_plan(self):
        plan = hlib.plan_unmet_mission_tail(self.EVA4_STEPS, 2)
        self.assertTrue(all(d.index > 2 for d in plan.dispositions))
        self.assertNotIn("LoadGame", [d.cmd for d in plan.dispositions])
        self.assertNotIn("SetSetting", [d.cmd for d in plan.dispositions])

    def test_skip_disabled_drives_the_full_tail(self):
        plan = hlib.plan_unmet_mission_tail(self.EVA4_STEPS, 2, skip_tail=False)
        self.assertFalse(plan.skip_enabled)
        self.assertEqual((), plan.skipped_indices)
        self.assertEqual(5, len(plan.run_indices))
        self.assertTrue(all(d.run for d in plan.dispositions))
        self.assertIn("skipTailOnUnmetMission=false", plan.summary)

    def test_empty_tail_and_cleanup_only_tail(self):
        no_tail = hlib.plan_unmet_mission_tail(self.B1_STEPS[:3], 2)
        self.assertEqual((), no_tail.dispositions)
        self.assertEqual((), no_tail.skipped_indices)
        self.assertIn("no post-mission tail", no_tail.summary)

        cleanup_only = hlib.plan_unmet_mission_tail(
            self.B1_STEPS[:3] + [{"cmd": "FlushAndQuit", "expect": "OK"}], 2)
        self.assertEqual((), cleanup_only.skipped_indices)
        self.assertEqual((3,), cleanup_only.run_indices)
        self.assertIn("cleanup-only", cleanup_only.summary)

    def test_summary_names_every_skipped_step_with_its_role(self):
        plan = hlib.plan_unmet_mission_tail(self.EVA4_STEPS, 2)
        for token in ("0004:EvaExit(world-mutating)",
                      "0005:EvaChuteDeploy(world-mutating)",
                      "0007:CommitTree(world-mutating)",
                      "0006:StopRecording", "0008:FlushAndQuit"):
            self.assertIn(token, plan.summary)

    def test_a_second_mission_step_in_the_tail_is_skipped_not_driven(self):
        # validate_spec rejects two mission steps, so this is belt-and-braces: an
        # unreachable shape must still fail safe rather than plan a second flight.
        steps = list(self.B1_STEPS)
        steps.insert(3, {"phase": "mission", "expect": "MISSION-OK"})
        plan = hlib.plan_unmet_mission_tail(steps, 2)
        second = plan.dispositions[0]
        self.assertFalse(second.run)
        self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING, second.role)

    # ---- Route-1 mid-mission writes (HARNESS-MIDMISSION-COMMIT-BYPASS) --------
    # The gate above covers `driver.steps`. These cover the writes the MISSION
    # subprocess makes on its own account, which that gate never sees because they
    # land mid-flight, before a verdict exists. REPORT-ONLY by design.

    MIDMISSION_RESERVED = "0003"

    # A B-DOCK-shaped channel: the driver's own steps, plus the mission's route-1
    # writes under the reserved id it was handed.
    MIDMISSION_CHANNEL = "\n".join([
        "id=0001 cmd=LoadGame save=bdock name=persistent",
        "id=0002 cmd=StartRecording",
        "id=0003 cmd=CommitTree",
        "id=0003.stop cmd=StopRecording",
        "id=0004 cmd=FlushAndQuit",
    ])

    def test_mid_mission_counts_only_the_missions_own_writes(self):
        w = hlib.parse_mid_mission_seam_writes(
            self.MIDMISSION_CHANNEL, self.MIDMISSION_RESERVED, mission_met=True)
        # The driver's index-derived ids (0001/0002/0004) are NOT the mission's, even
        # though 0004 sorts adjacent to the reserved id.
        self.assertEqual(2, w.total)
        self.assertEqual(("CommitTree", "StopRecording"), w.verbs)

    def test_mid_mission_sub_ids_of_the_reserved_id_belong_to_the_mission(self):
        """The generalized bridge writes `<reserved>.<tag>`; the C# seam skips
        duplicate ids, which is WHY the sub-id exists and why it must be counted."""
        w = hlib.parse_mid_mission_seam_writes(
            "id=0003.commit cmd=CommitTree", self.MIDMISSION_RESERVED, mission_met=False)
        self.assertEqual(1, w.total)
        self.assertTrue(w.exposed)

    def test_mid_mission_role_classification_reuses_the_tail_gate_function(self):
        w = hlib.parse_mid_mission_seam_writes(
            self.MIDMISSION_CHANNEL, self.MIDMISSION_RESERVED, mission_met=True)
        # CommitTree is world-mutating, StopRecording is cleanup.
        self.assertEqual(1, w.world_mutating)
        # PIN THE CLAIM, not just the shape. The old cell asserted only this count, so it
        # passed while the two paths genuinely DISAGREED on the unknown-verb fallback (the
        # instrument did a bare table lookup; the gate calls a function that fails safe).
        # Assert agreement VERB BY VERB, including one with no row.
        for verb in ("CommitTree", "StopRecording", "RecordingState", "NoSuchVerbAnywhere"):
            expected = hlib.seam_verb_tail_role(verb) == hlib.TAIL_ROLE_WORLD_MUTATING
            one = hlib.parse_mid_mission_seam_writes(
                "id=%s cmd=%s" % (self.MIDMISSION_RESERVED, verb),
                self.MIDMISSION_RESERVED, mission_met=True)
            self.assertEqual(1 if expected else 0, one.world_mutating, verb)

    def test_mid_mission_world_mutating_write_then_unmet_is_the_exposed_shape(self):
        w = hlib.parse_mid_mission_seam_writes(
            self.MIDMISSION_CHANNEL, self.MIDMISSION_RESERVED, mission_met=False)
        self.assertTrue(w.exposed)
        self.assertIn("REPORT-ONLY", w.summary)
        self.assertIn("UNMET", w.summary)

    def test_mid_mission_met_mission_is_not_exposed_however_much_it_wrote(self):
        """Today's ONLY committed emitters (the B-DOCK / orbit-commit machines) fire
        their mid-mission commit on the SUCCESS path. That must stay quiet."""
        w = hlib.parse_mid_mission_seam_writes(
            self.MIDMISSION_CHANNEL, self.MIDMISSION_RESERVED, mission_met=True)
        self.assertFalse(w.exposed)
        self.assertNotIn("REPORT-ONLY", w.summary)

    def test_mid_mission_cleanup_only_writes_are_never_exposed(self):
        w = hlib.parse_mid_mission_seam_writes(
            "id=0003.stop cmd=StopRecording", self.MIDMISSION_RESERVED, mission_met=False)
        self.assertEqual(1, w.total)
        self.assertEqual(0, w.world_mutating)
        self.assertFalse(w.exposed)

    def test_mid_mission_unknown_verb_fails_SAFE_exactly_like_the_tail_gate(self):
        """An unknown verb is presumed world-mutating, matching `seam_verb_tail_role`.

        This cell used to assert the OPPOSITE (fail-OPEN, `world_mutating=0`), which put
        the instrument at odds with the tail gate on the single case the shared-scale
        claim is about. Failing open in a RISK instrument is backwards: it reports
        `exposed=False` for a write the tail gate would have refused to drive."""
        w = hlib.parse_mid_mission_seam_writes(
            "id=0003 cmd=SomeFutureVerb", self.MIDMISSION_RESERVED, mission_met=False)
        self.assertEqual(1, w.total)
        self.assertEqual(("SomeFutureVerb",), w.verbs)
        self.assertEqual(hlib.TAIL_ROLE_WORLD_MUTATING,
                         hlib.seam_verb_tail_role("SomeFutureVerb"))
        self.assertEqual(1, w.world_mutating)
        self.assertTrue(w.exposed)

    def test_mid_mission_line_with_no_cmd_is_counted_but_not_classified(self):
        """An UNPARSED line is not an UNKNOWN VERB; conflating them invents attribution.

        A line under the mission's id with no `cmd=` is counted in `total` (it IS a
        mission write) but is NOT claimed world-mutating - there is no verb to reason
        about. The fail-safe above applies where a NAME exists and no row does."""
        w = hlib.parse_mid_mission_seam_writes(
            "id=0003 nocmd=1", self.MIDMISSION_RESERVED, mission_met=False)
        self.assertEqual(1, w.total)
        self.assertEqual((), w.verbs)
        self.assertEqual(0, w.world_mutating)
        self.assertFalse(w.exposed)

    def test_mid_mission_summary_never_says_mission_met_on_an_unmet_run(self):
        """The summary is the one operator-facing line this instrument produces, and it
        must not contradict the run. The non-exposed branch hardcoded "; mission met", so
        a cleanup/inert-only write on an UNMET mission printed a flat falsehood - and both
        StopRecording and RecordingState are real mission-emitted verbs."""
        for text in ("id=0003 cmd=StopRecording", "id=0003 cmd=RecordingState",
                     "id=0003 nocmd=1"):
            unmet = hlib.parse_mid_mission_seam_writes(
                text, self.MIDMISSION_RESERVED, mission_met=False)
            self.assertFalse(unmet.exposed, text)
            self.assertIn("mission UNMET", unmet.summary, text)
            self.assertNotIn("; mission met", unmet.summary, text)
            met = hlib.parse_mid_mission_seam_writes(
                text, self.MIDMISSION_RESERVED, mission_met=True)
            self.assertIn("; mission met", met.summary, text)

    def test_mid_mission_exposed_summary_names_the_world_mutating_verbs_only(self):
        """The count and the bracketed list must describe the SAME set. The exposed line
        printed EVERY verb seen against the world-mutating COUNT, so one CommitTree among
        two cleanup verbs read `1 world-mutating ... [CommitTree, RecordingState,
        StopRecording]` - overstating the blast radius in the line an operator reads when
        deciding whether to arm a gate."""
        w = hlib.parse_mid_mission_seam_writes(
            "id=0003 cmd=CommitTree\nid=0003 cmd=StopRecording\nid=0003 cmd=RecordingState",
            self.MIDMISSION_RESERVED, mission_met=False)
        self.assertEqual(1, w.world_mutating)
        head = w.summary.split("(all verbs:")[0]
        self.assertIn("[CommitTree]", head)
        self.assertNotIn("StopRecording", head)
        self.assertNotIn("RecordingState", head)
        # The full set is still reported, just no longer conflated with the count.
        self.assertIn("all verbs: [CommitTree, RecordingState, StopRecording]", w.summary)

    def test_mid_mission_seam_only_and_unparseable_channels_report_nothing(self):
        for text in ("", "\n\n", "garbage with no id", "cmd=CommitTree"):
            w = hlib.parse_mid_mission_seam_writes(
                text, self.MIDMISSION_RESERVED, mission_met=False)
            self.assertEqual(0, w.total, text)
            self.assertFalse(w.exposed, text)
            self.assertIn("no route-1", w.summary)

    def test_mid_mission_no_reserved_id_attributes_nothing(self):
        """A seam-only driver has no mission step and no reserved id; the instrument
        must not then attribute the DRIVER's own writes to a mission."""
        w = hlib.parse_mid_mission_seam_writes(
            self.MIDMISSION_CHANNEL, "", mission_met=False)
        self.assertEqual(0, w.total)
        self.assertFalse(w.exposed)

    def test_real_committed_eva4_spec_skips_its_two_in_world_verbs(self):
        # The REAL committed spec, not a hand-built shape: this is the exact step list
        # flight 1 drove at terminal velocity. Fixture-over-mock for the same reason
        # RealProfileFileTests exists -- a spec edit that reorders or renames the tail
        # must be caught here, not discovered on the next flight.
        spec = load_spec("EVA-4-atmo-chute.toml")
        steps = spec["driver"]["steps"]
        mission_index = next(i for i, s in enumerate(steps) if s.get("phase") == "mission")
        plan = hlib.plan_unmet_mission_tail(
            steps, mission_index, skip_tail=hlib.spec_skips_tail_on_unmet_mission(spec))

        self.assertEqual(["EvaExit", "EvaChuteDeploy", "CommitTree"],
                         [d.cmd for d in plan.dispositions if not d.run])
        self.assertEqual(["StopRecording", "FlushAndQuit"],
                         [d.cmd for d in plan.dispositions if d.run])
        # The named deferral budgets the skip stops burning on a dead attempt.
        self.assertEqual(120.0, hlib.DISPATCH_DEFERRAL_BUDGET_SECONDS["EvaExit"])
        self.assertEqual(420.0, hlib.DISPATCH_DEFERRAL_BUDGET_SECONDS["EvaChuteDeploy"])

    def test_every_committed_autopilot_spec_keeps_its_quit_owner(self):
        """Data-driven over EVERY committed autopilot spec, loaded from disk. Closes the
        gap left by the hand-written literals above (which pin a shape, not the suite)
        and cannot go stale when a scenario is added or its tail is edited: the list of
        autopilot specs is DISCOVERED, never hardcoded.

        The invariant that matters for every one of them: an unmet run must still be
        able to bring KSP down. A scenario whose QUIT owner is a FlushAndQuit step must
        keep it; one that owns the quit via autorun.exit needs no tail step at all.
        Fails if a role-table edit ever makes a quit-owning step skippable - which would
        convert a retryable driver-INVALID into a KILLED that MASKS the mission subkind.
        """
        checked = []
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            spec = load_spec(name)
            driver = spec.get("driver", {}) or {}
            steps = driver.get("steps", []) or []
            mission_index = next((i for i, s in enumerate(steps)
                                  if s.get("phase") == "mission"), None)
            if mission_index is None:
                continue  # seam-only: never reaches the unmet-tail path
            checked.append(name)
            plan = hlib.plan_unmet_mission_tail(
                steps, mission_index,
                skip_tail=hlib.spec_skips_tail_on_unmet_mission(spec))
            tail_cmds = [s.get("cmd") for s in steps[mission_index + 1:]]
            run_cmds = [d.cmd for d in plan.dispositions if d.run]

            if "FlushAndQuit" in tail_cmds:
                self.assertIn("FlushAndQuit", run_cmds,
                              "%s: the QUIT owner must survive an unmet tail" % name)
            else:
                self.assertTrue(driver.get("autorun", {}).get("exit"),
                                "%s: no FlushAndQuit step and no autorun.exit" % name)
            # Nothing outside the cleanup set is ever driven.
            for cmd in run_cmds:
                self.assertEqual(hlib.TAIL_ROLE_CLEANUP, hlib.seam_verb_tail_role(cmd),
                                 "%s drives non-cleanup %r on an unmet tail" % (name, cmd))
            # And every tail step is accounted for exactly once.
            self.assertEqual(len(tail_cmds), len(plan.dispositions), name)

        # Sanity: the sweep actually swept. If autopilot specs stop being discovered the
        # loop above would pass vacuously.
        self.assertGreaterEqual(len(checked), 8,
                                "expected the committed autopilot specs; found %s" % (checked,))
        self.assertIn("EVA-4-atmo-chute.toml", checked)
        self.assertIn("B1-pad-hop.toml", checked)

    def test_skipped_steps_are_absent_from_response_evaluation(self):
        # The drive loop records only the steps it actually SENDS, so a skipped step
        # must not surface as an unmet step (no response line for a line never
        # written). This pins the PURE half of that contract by construction; the
        # run.py wiring that actually builds the shortened list is pinned by
        # UnmetMissionTailSmokeTests over the real drive loop.
        plan = hlib.plan_unmet_mission_tail(self.EVA4_STEPS, 2)
        driven = [{"id": hlib.step_id_for_index(0), "cmd": "LoadGame", "expect": "OK"},
                  {"id": hlib.step_id_for_index(1), "cmd": "SetSetting", "expect": "OK"}]
        driven += [{"id": d.step_id, "cmd": d.cmd, "expect": "OK"}
                   for d in plan.dispositions if d.run]
        lines = ["id=%s cmd=%s verdict=OK seq=%d" % (s["id"], s["cmd"], n)
                 for n, s in enumerate(driven, start=1)]
        ev = hlib.evaluate_response_stream(lines, driven)
        self.assertTrue(ev.all_expected_met)
        self.assertIsNone(ev.first_unmet)
        self.assertNotIn("EvaExit", [o.cmd for o in ev.steps])


class SkipTailSpecSurfaceTests(unittest.TestCase):
    """Guards the [driver].skipTailOnUnmetMission spec surface: the DEFAULT is the
    safe skip, an explicit false is honored, a non-bool is REJECTED (a string
    "false" would read truthy and silently keep the legacy tail), and declaring it
    on a seam-kind driver warns instead of silently doing nothing."""

    def test_default_is_skip(self):
        self.assertTrue(hlib.spec_skips_tail_on_unmet_mission({}))
        self.assertTrue(hlib.spec_skips_tail_on_unmet_mission({"driver": {}}))
        self.assertTrue(hlib.SKIP_TAIL_ON_UNMET_MISSION_DEFAULT)

    def test_explicit_false_is_honored(self):
        spec = {"driver": {"skipTailOnUnmetMission": False}}
        self.assertFalse(hlib.spec_skips_tail_on_unmet_mission(spec))

    def test_non_bool_reads_as_the_safe_default_and_fails_validation(self):
        spec = copy.deepcopy(_autopilot_spec())
        spec["driver"]["skipTailOnUnmetMission"] = "false"
        # The reader never guesses what a string meant: it falls back to the SAFE
        # default while validate_spec reds the spec outright.
        self.assertTrue(hlib.spec_skips_tail_on_unmet_mission(spec))
        v = hlib.validate_spec(spec, {}, mission_schemas=_mission_schemas())
        self.assertFalse(v.ok)
        self.assertTrue(any("skipTailOnUnmetMission" in e for e in v.errors))

    def test_bool_validates_on_an_autopilot_driver_with_no_warning(self):
        for value in (True, False):
            spec = copy.deepcopy(_autopilot_spec())
            spec["driver"]["skipTailOnUnmetMission"] = value
            v = hlib.validate_spec(spec, {}, mission_schemas=_mission_schemas())
            self.assertTrue(v.ok, "errors=%s" % (v.errors,))
            self.assertFalse(any("skipTailOnUnmetMission" in w for w in v.warnings))

    def test_misplaced_key_is_rejected_not_silently_ignored(self):
        """The TOML-scoping trap EVA-4-atmo-chute.toml already documents for `steps`: a
        key written after the [driver.missionParams] header is scoped to that sub-table.
        The flag is read off [driver] ONLY, so a misplaced opt-out would be silently
        inert and the SAFE default would apply against the author's explicit intent.
        Reject it with a message that names where it belongs."""
        for path in (("driver", "missionParams"), ("driver", "autorun")):
            spec = copy.deepcopy(_autopilot_spec())
            spec[path[0]].setdefault(path[1], {})[
                hlib.SKIP_TAIL_ON_UNMET_MISSION_KEY] = False
            v = hlib.validate_spec(spec, {}, mission_schemas=_mission_schemas())
            self.assertFalse(v.ok, "%s must reject" % (path,))
            self.assertTrue(any("belongs in [driver]" in e for e in v.errors),
                            "errors=%s" % (v.errors,))
        # ... and at the spec root (a key written ABOVE the [driver] header).
        spec = copy.deepcopy(_autopilot_spec())
        spec[hlib.SKIP_TAIL_ON_UNMET_MISSION_KEY] = False
        v = hlib.validate_spec(spec, {}, mission_schemas=_mission_schemas())
        self.assertFalse(v.ok)
        self.assertTrue(any("belongs in [driver]" in e for e in v.errors))

    def test_correctly_placed_key_is_not_caught_by_the_misplaced_guard(self):
        spec = copy.deepcopy(_autopilot_spec())
        spec["driver"][hlib.SKIP_TAIL_ON_UNMET_MISSION_KEY] = False
        v = hlib.validate_spec(spec, {}, mission_schemas=_mission_schemas())
        self.assertTrue(v.ok, "errors=%s" % (v.errors,))
        self.assertFalse(hlib.spec_skips_tail_on_unmet_mission(spec))

    def test_seam_driver_declaration_warns_but_still_validates(self):
        spec = load_spec("S0.6-live-record-commit.toml")
        spec["driver"]["skipTailOnUnmetMission"] = True
        v = hlib.validate_spec(spec, load_registry())
        self.assertTrue(v.ok, "errors=%s" % (v.errors,))
        self.assertTrue(any("skipTailOnUnmetMission" in w and "inert" in w
                            for w in v.warnings))

    def test_every_committed_spec_takes_the_default(self):
        # No committed scenario opts out today; if one ever does, the opt-out must be
        # a deliberate, reviewed edit rather than something that drifted in.
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            spec = load_spec(name)
            self.assertTrue(hlib.spec_skips_tail_on_unmet_mission(spec), name)


class VenvAdmissionTests(unittest.TestCase):
    """Guards (design Test Plan "Venv admission"): a matching stamp admits, a
    missing stamp or a drifted pin refuses with the TERMINAL non-retryable
    tooling-venv, checked at pre-launch ADMIT (no KSP boot). Fails if a stale /
    absent kRPC client silently certifies a flight, or a venv fault is wrongly made
    retryable (a retry cannot re-bootstrap a venv)."""

    REQS = {"krpc": "0.5.4", "protobuf": "4.21.0"}

    def test_admits_matching_stamp(self):
        stamp = {"schema": 1, "pins": {"krpc": "0.5.4", "protobuf": "4.21.0"}, "freezeHash": "abc"}
        ok, subkind = hlib.venv_admission(stamp, self.REQS)
        self.assertTrue(ok)
        self.assertEqual(subkind, "")

    def test_missing_stamp_refused(self):
        for absent in (None, {}):
            ok, subkind = hlib.venv_admission(absent, self.REQS)
            self.assertFalse(ok)
            self.assertEqual(subkind, "tooling-venv")

    def test_drifted_pin_refused(self):
        stamp = {"pins": {"krpc": "0.5.3", "protobuf": "4.21.0"}}  # krpc drifted
        ok, subkind = hlib.venv_admission(stamp, self.REQS)
        self.assertFalse(ok)
        self.assertEqual(subkind, "tooling-venv")

    def test_stamp_missing_required_pin_refused(self):
        stamp = {"pins": {"krpc": "0.5.4"}}  # protobuf pin absent from the stamp
        ok, subkind = hlib.venv_admission(stamp, self.REQS)
        self.assertFalse(ok)
        self.assertEqual(subkind, "tooling-venv")

    def test_provisional_extra_stamp_pin_tolerated(self):
        # Before protobuf is promoted into requirements, the stamp may carry an
        # extra resolved pin; only the committed requirements are enforced, so the
        # venv is NOT falsely refused pre-promotion.
        stamp = {"pins": {"krpc": "0.5.4", "protobuf": "4.21.0"}}
        ok, subkind = hlib.venv_admission(stamp, {"krpc": "0.5.4"})
        self.assertTrue(ok)
        self.assertEqual(subkind, "")

    def test_tooling_venv_is_terminal_non_retryable(self):
        # The load-bearing guarantee: tooling-venv is NOT in the retryable set, so a
        # venv fault terminates INVALID and is never retried.
        self.assertNotIn("tooling-venv", hlib.RETRYABLE_INVALID_SUBKINDS)
        r = hlib.Verdict(hlib.VERDICT_INVALID, "tooling-venv", False, "venv drift")
        self.assertFalse(hlib.should_retry(r, 1, "once"))


# ---------------------------------------------------------------------------
# M-B2 ledger-oracle PURE support (design docs/dev/design-autotest-ledger-oracle.md).
# The leg-A stock-award capture, the produced-save careerSave block read, the
# manifest dedupe, the unexpected-award cross-check, and the ledger spec surface.
# ---------------------------------------------------------------------------

import oracle  # noqa: E402


class StockAwardCaptureTests(unittest.TestCase):
    """Guards the leg-A capture (design Test Plan "Stock-log capture + dedupe" ~786):
    a scene-reload re-emit must not double-count, a genuine second award must not be
    dropped, a null-UT entry must still order, and a running-balance line must NEVER
    be admitted as a manifest amount (it would double-count against the seed).

    EVERY LITERAL LINE BELOW IS MEASURED, not composed (2026-07-29 rewrite, known-gate
    3). The cells this class shipped with fed INVENTED shapes
    (`ContractSystem ... funds=50000`, `ResearchAndDevelopment ... delta=8.5`) and so
    passed green while the capture matched nothing in the field - the tests and the
    patterns agreed with each other and both disagreed with KSP. Sources:
      docs/dev/todo-and-known-bugs.md:248,257
      docs/dev/done/todo-and-known-bugs-v4.md:1954
      logs/2026-07-28_1913_CL-1-pod-impact/KSP.log:10361,10382,10660

    SECOND PASS, same day: the FUNDS half of that rewrite was itself composed, not
    measured - `Added 4800 funds: 'RecordsSpeed'` never appeared in any KSP.log. Its
    cited source quotes PARSEK's own `MilestoneAchieved ... funds=4800` line, which
    the capture skips as [Parsek]-tagged. KSP writes NO funds and NO science award
    line at all (assembly literal counts for `" funds: '"` and `" science: '"` are
    both ZERO; `Funding.AddFunds` / `ResearchAndDevelopment.AddScience` carry no
    Debug.Log). The funds pattern is retired to `STOCK_AWARD_PATTERNS_DEAD`, and every
    mechanical cell below now drives a REAL reputation line."""

    # MEASURED, CL-1 flights 1+2 (2026-07-28), identical across both. These three
    # lines ARE the complete stock-award output of that flight, in order.
    REP_LOSS_LINE = "Added -9.999828 (-10) reputation: 'VesselLoss'."
    REP_PROGRESSION_LINE = "Added 0.9999995 (1) reputation: 'Progression'."
    # NOT a KSP line - the retired funds shape, kept only to prove it stays unmatched.
    DEAD_FUNDS_LINE = "Added 4800 funds: 'RecordsSpeed'"

    def test_measured_reputation_penalty_line_captured_with_amount_and_reason(self):
        log = ("[LOG] [Parsek][INFO][Recorder] tick ut=12345.6\n"
               "[LOG] " + self.REP_LOSS_LINE + "\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured),
                         "the MEASURED stock rep line must capture; an empty capture "
                         "is the dead-pattern no-op this rewrite closes")
        c = res.captured[0]
        self.assertEqual("reputation", c.facet)
        # The APPLIED delta (the leading number), NOT the parenthesised nominal -10.
        self.assertEqual(-9.999828, c.amount)
        self.assertEqual("VesselLoss", c.reason)
        self.assertEqual("stock-reputation-award", c.kind)
        self.assertEqual(12345.6, c.ut)          # correlated to the nearest UT line.
        # Stamped `applied` so the oracle does not re-curve an already-curved delta.
        self.assertEqual("applied", c.to_entry_dict()["repMode"])

    def test_measured_reputation_award_line_captured(self):
        log = "[LOG] " + self.REP_PROGRESSION_LINE + "\n"
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured))
        c = res.captured[0]
        self.assertEqual(0.9999995, c.amount)
        self.assertEqual("Progression", c.reason)

    def test_retired_funds_shape_is_not_captured(self):
        # KSP emits NO funds award line: `" funds: '"` occurs ZERO times in
        # Assembly-CSharp.dll and `Funding.AddFunds` has no Debug.Log. The shape the
        # 2026-07-29 rewrite briefly enumerated is retired, and must stay unmatched by
        # the LIVE table - if a future KSP build starts logging funds, this cell reds
        # and the facet gets re-decided instead of quietly re-opening.
        log = ("[LOG] [Parsek][INFO] ut=10.0\n"
               "[LOG] " + self.DEAD_FUNDS_LINE + "\n")
        self.assertEqual(0, len(hlib.parse_stock_award_lines(log).captured))
        self.assertEqual([], [p for p in hlib.STOCK_AWARD_PATTERNS if p.facet == "funds"])
        # Retired, not deleted: the shape is still named, with its evidence.
        self.assertEqual(["stock-funds-award"],
                         [p.kind for p in hlib.STOCK_AWARD_PATTERNS_DEAD])

    def test_live_and_dead_pattern_tables_are_disjoint(self):
        # Mirrors the ANOMALY_TOKENS / ANOMALY_TOKENS_DEAD invariant: a retired shape
        # is retired FROM the live table, never carried inside it.
        live = {p.kind for p in hlib.STOCK_AWARD_PATTERNS}
        dead = {p.kind for p in hlib.STOCK_AWARD_PATTERNS_DEAD}
        self.assertEqual(set(), live & dead)
        self.assertTrue(live, "the live table must not be empty")

    def test_the_whole_measured_cl1_burst_captures(self):
        # The COMPLETE stock-award output of both CL-1 flights, read off the collected
        # log: two `Progression` +1 rep awards and the death penalty. Nothing else -
        # the flight also credited three funds milestones, and KSP logged none of them.
        # Deduping must keep all three (the two Progression awards are identical in
        # amount and reason, and are distinguished ONLY by their distinct UTs).
        log = "\n".join("[LOG] " + line for line in (
            "[Parsek][INFO][Recorder] tick ut=12.0",
            self.REP_PROGRESSION_LINE,
            "[Parsek][INFO][Recorder] tick ut=15.98",
            self.REP_PROGRESSION_LINE,
            "[Parsek][INFO][Recorder] tick ut=119.8",
            self.REP_LOSS_LINE)) + "\n"
        deduped = hlib.dedupe_captured_awards(hlib.parse_stock_award_lines(log).captured)
        self.assertEqual(3, len(deduped))
        self.assertEqual(["Progression", "Progression", "VesselLoss"],
                         sorted(c.reason for c in deduped))
        # The arithmetic closes against the MEASURED produced pool: CL-1's career save
        # carried reputation -7.99982834 (seed 0) and the captured deltas sum to
        # -7.999829. The residual ~6.6e-7 is the LOG's display rounding, not drift -
        # stock prints the applied delta at 7 significant figures while the save keeps
        # full precision. Anyone arming a rep cross-check tolerance must budget for
        # this: an exact compare against a summed capture cannot succeed.
        self.assertAlmostEqual(-7.99982834, sum(c.amount for c in deduped), places=5)

    def test_null_ut_when_no_parsek_neighbor(self):
        # No UT-stamped [Parsek] line precedes the award -> ut=None, seq=line ordinal
        # (the seqKey), still ordered + deduped deterministically (design ~394).
        log = "[LOG] " + self.REP_PROGRESSION_LINE + "\n"
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured))
        c = res.captured[0]
        self.assertIsNone(c.ut)
        self.assertEqual(0, c.seq)               # first (0th) log line.
        self.assertEqual(("ord", c.seq), c.seq_key)  # type-tagged ordinal seqKey (item 8).

    def test_balance_line_rejected_not_captured(self):
        # A running-BALANCE line is inadmissible (a post-grant balance would
        # double-count against the seed, design ~398). It is counted, never captured.
        log = ("[LOG] [Parsek][INFO] ut=10.0\n"
               "[LOG] ResearchAndDevelopment: total science 128.0\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(0, len(res.captured))
        self.assertEqual(1, res.rejected_balance)

    def test_balance_line_in_the_award_idiom_still_rejected(self):
        # The BALANCE-INADMISSIBILITY rule survives the pattern rewrite: a line
        # reporting a post-grant RUNNING TOTAL is rejected even when it is worded in
        # the same family as an award line. Admitting one would double-count the whole
        # pool against the seed - the single worst error this capture can make.
        log = ("[LOG] [Parsek][INFO] ut=10.0\n"
               "[LOG] Funding: total funds 529600\n"
               "[LOG] Reputation: current reputation -7.99982834\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(0, len(res.captured))
        self.assertEqual(2, res.rejected_balance)

    def test_prose_naming_a_pool_is_not_an_award(self):
        # Negative: the shape is anchored on the whole `Added <n> <pool>: '<reason>'`
        # idiom, so ordinary log prose that merely contains "Added" and a pool word
        # captures nothing (the class of false positive that made the anomaly sweep
        # red a clean S1.7 flight).
        log = ("[LOG] Added 14 developer-only scenarios\n"
               "[LOG] Added 1 supersede relations for subtree rooted at b9-booster-a\n"
               "[LOG] funds: 529600 reputation: -7.99982834\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(0, len(res.captured))

    def test_scene_reload_reemit_same_seqkey_dedupes(self):
        # The same award line re-emitted at the SAME seqKey (no new UT between) is ONE
        # effect after dedupe (design edge 2).
        log = ("[LOG] [Parsek][INFO] ut=100.0\n"
               "[LOG] " + self.REP_PROGRESSION_LINE + "\n"
               "[LOG] " + self.REP_PROGRESSION_LINE + "\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(2, len(res.captured))            # both matched before dedupe
        deduped = hlib.dedupe_captured_awards(res.captured)
        self.assertEqual(1, len(deduped))                 # collapsed to one effect

    def test_genuine_second_award_at_distinct_seqkey_survives(self):
        # A genuine second identical award at a DISTINCT seqKey (a new UT between)
        # survives the dedupe (design edge 2): the seqKey is part of the dedupe key.
        log = ("[LOG] [Parsek][INFO] ut=100.0\n"
               "[LOG] " + self.REP_PROGRESSION_LINE + "\n"
               "[LOG] [Parsek][INFO] ut=200.0\n"
               "[LOG] " + self.REP_PROGRESSION_LINE + "\n")
        res = hlib.parse_stock_award_lines(log)
        deduped = hlib.dedupe_captured_awards(res.captured)
        self.assertEqual(2, len(deduped))
        self.assertEqual({100.0, 200.0}, {c.ut for c in deduped})

    def test_empty_log_captures_nothing(self):
        res = hlib.parse_stock_award_lines("")
        self.assertEqual(0, len(res.captured))
        self.assertEqual(0, res.rejected_balance)

    def test_parsek_tagged_line_not_captured_as_award(self):
        # Review SF7: a [Parsek] diagnostic line that QUOTES a stock award line (the
        # ledger tracer echoing an event) must NOT false-capture as a stock award,
        # which would false-red an empty-manifest B10. A genuine (untagged) stock line
        # on the next line still captures, and the [Parsek] line's ut= stamp still
        # drives the UT correlation of that genuine award.
        log = ("[LOG] [Parsek][VERBOSE][LedgerTrace] ut=42.0 saw Added 999 (999) "
               "reputation: 'Bogus'\n"
               "[LOG] " + self.REP_PROGRESSION_LINE + "\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured))          # only the genuine stock line
        c = res.captured[0]
        self.assertEqual(0.9999995, c.amount)
        self.assertEqual("Progression", c.reason)
        self.assertEqual(42.0, c.ut)                     # UT still read from the [Parsek] stamp
        # The 999.0 from the [Parsek] line was never admitted as an award amount.
        self.assertNotIn(999.0, [a.amount for a in res.captured])

    def test_no_science_award_line_exists_in_ksp_so_none_is_enumerated(self):
        # KNOWN-GATE 3, RESOLVED 2026-07-29 - and resolved NEGATIVELY, which is why
        # this cell is a permanent pin rather than a TODO. The open question was
        # "measure the science award shape from an L1 science flight". There is no
        # shape to measure: `ResearchAndDevelopment.AddScience` mutates the pool with
        # NO Debug.Log, and `" science: '"` occurs ZERO times in Assembly-CSharp.dll.
        # The only R&D line KSP writes is the DATA line
        # `[Research & Development]: +<n> data on <subject>. Subject value is <v>`,
        # which reports experiment data, not a science-currency delta, and is
        # correctly inadmissible. Do NOT add a science pattern on the strength of that
        # line - an unmatched award still moves the produced save, so the
        # seam-declared-vs-save diff remains the trusted leg for the science facet.
        self.assertEqual([], [p for p in hlib.STOCK_AWARD_PATTERNS if p.facet == "science"])
        res = hlib.parse_stock_award_lines("[LOG] Added 5 science: 'ScienceTransmission'\n")
        self.assertEqual(0, len(res.captured))
        # The real R&D data line is likewise not an award.
        rd = hlib.parse_stock_award_lines(
            "[LOG] [Research & Development]: +5 data on Crew Report from LaunchPad. "
            "Subject value is 1.00\n")
        self.assertEqual(0, len(rd.captured))
        # Every enumerated pattern cites the archived line it was derived from.
        for pat in hlib.STOCK_AWARD_PATTERNS:
            self.assertTrue(pat.measured_from, "%s pattern cites no measured line" % pat.facet)


class UnmatchedCapturedAwardTests(unittest.TestCase):
    """Guards the unexpected-award cross-check (design edge 4): a captured award not
    explained by a seam-declared entry is an unexpected stock award (the B10
    economy-drift signal). A captured award matched by a seam entry is corroboration,
    NOT a drift."""

    def _captured(self, ut, kind, guid, amount, facet="funds"):
        return hlib.CapturedAward(kind=kind, facet=facet, amount=amount,
                                  contract_guid=guid, subject_id="", ut=ut, seq=0,
                                  raw_line="line")

    def test_empty_seam_all_captured_unexpected(self):
        captured = [self._captured(100.0, "contract-complete", "g1", 1000.0)]
        unmatched = hlib.unmatched_captured_awards((), captured)
        self.assertEqual(1, len(unmatched))

    def test_matching_seam_entry_corroborates_not_unexpected(self):
        # A seam-declared author constant at the same (seqKey, kind, identity)
        # EXPLAINS the captured award -> it is corroboration, not an unexpected award.
        parse = oracle.parse_manifest_entries([
            {"ut": 100.0, "kind": "contract-complete", "funds": 1000.0, "contractGuid": "g1"}])
        self.assertEqual([], list(parse.errors))
        captured = [self._captured(100.0, "contract-complete", "g1", 1000.0)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, captured)
        self.assertEqual([], unmatched)

    def test_seam_at_different_key_leaves_capture_unexpected(self):
        parse = oracle.parse_manifest_entries([
            {"ut": 999.0, "kind": "contract-complete", "funds": 1000.0, "contractGuid": "g1"}])
        captured = [self._captured(100.0, "contract-complete", "g1", 1000.0)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, captured)
        self.assertEqual(1, len(unmatched))

    def test_type_tag_probe_ordinal_seq_not_matched_by_ut(self):
        # Item 8: a null-UT seam entry with ordinal seq=3 must NOT explain a captured
        # award at UT 3.0 (3 == 3.0 untagged would spuriously match). The award stays
        # unexpected.
        parse = oracle.parse_manifest_entries([
            {"kind": "contract-complete", "funds": 1000.0, "contractGuid": "g1", "seq": 3}])
        self.assertEqual([], list(parse.errors))
        self.assertIsNone(parse.entries[0].ut)
        self.assertEqual(("ord", 3), parse.entries[0].seq_key)
        captured = [self._captured(3.0, "contract-complete", "g1", 1000.0)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, captured)
        self.assertEqual(1, len(unmatched), "ord 3 must NOT match ut 3.0")

    def test_multi_subject_entry_explains_award_on_any_subject(self):
        # Item 10: a science seam entry declaring MANY subjects explains a captured award
        # on ANY of them (not just subject_ids[0]); the prior code false-flagged the 2nd+.
        parse = oracle.parse_manifest_entries([
            {"ut": 50.0, "kind": "science-transmit", "science": 5.0,
             "subjectIds": ["subjA", "subjB", "subjC"]}])
        self.assertEqual([], list(parse.errors))
        aw = hlib.CapturedAward(kind="science-transmit", facet="science", amount=5.0,
                                contract_guid="", subject_id="subjB", ut=50.0, seq=0,
                                raw_line="line")
        self.assertEqual([], hlib.unmatched_captured_awards(parse.entries, [aw]),
                         "award on the 2nd declared subject must be explained")
        # An award on an UNDECLARED subject is still unexpected (fail-closed).
        aw2 = hlib.CapturedAward(kind="science-transmit", facet="science", amount=5.0,
                                 contract_guid="", subject_id="subjZ", ut=50.0, seq=0,
                                 raw_line="line")
        self.assertEqual(1, len(hlib.unmatched_captured_awards(parse.entries, [aw2])))


class CapturedAwardCorroborationKeyTests(unittest.TestCase):
    """THE REGRESSION THE KIND GENERALIZATION INTRODUCED, and its fix.

    Captured awards carry the GENERIC kinds (`stock-funds-award` /
    `stock-reputation-award`) because a real stock line names only its
    TransactionReasons key; seam entries carry SCENARIO kinds (`kerbal-hire`, ...).
    While `kind` was part of the corroboration key those two vocabularies could never
    be equal, so EVERY captured award - including the scenario's own declared one -
    reported "unexpected", and `captureCrossCheck = "gate"` could never be armed
    (reproduced against L1-hire-kerbal-career's own -62113 hire debit). The key is now
    (seqKey, facet, amount within the facet tolerance), one-to-one, with structured
    identity as a fail-closed discriminator and the optional per-entry `stockReason`
    as a tightener."""

    def _award(self, amount, reason, facet="funds", ut=0.0, guid="", subject=""):
        kind = ("stock-reputation-award" if facet == "reputation"
                else "stock-funds-award")
        return hlib.CapturedAward(kind=kind, facet=facet, amount=amount,
                                  contract_guid=guid, subject_id=subject, ut=ut,
                                  seq=0, raw_line="line", reason=reason)

    def test_the_l1_hire_debit_corroborates_its_own_seam_entry(self):
        # The reproduction, verbatim: L1-hire-kerbal-career declares
        # kind="kerbal-hire" funds=-62113 at ut=0.0, and stock logs the debit under
        # its own reason key. Before the fix this read as an unexpected award.
        parse = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "kerbal-hire", "funds": -62113.0,
             "provenance": "seam-declared"}])
        self.assertEqual([], list(parse.errors))
        award = self._award(-62113.0, "CrewRecruited")
        self.assertEqual(
            [], hlib.unmatched_captured_awards(parse.entries, [award]),
            "a scenario's OWN declared award must corroborate, or the gate is unarmable")

    def test_an_undeclared_award_is_still_unexpected(self):
        # The signal must survive the fix: a milestone award no manifest declares is
        # exactly what an operator has to review before arming the gate.
        parse = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "kerbal-hire", "funds": -62113.0}])
        awards = [self._award(-62113.0, "CrewRecruited"),
                  self._award(4800.0, "RecordsSpeed")]
        unmatched = hlib.unmatched_captured_awards(parse.entries, awards)
        self.assertEqual(["RecordsSpeed"], [c.reason for c in unmatched])

    def test_two_same_ut_same_amount_awards_do_not_cross_corroborate_one_entry(self):
        # MEASURED on CL-1: RecordsSpeed 4800 and RecordsAltitude 4800 fire at the
        # same UT. One declared 4800 explains exactly ONE of them (the match is
        # one-to-one and consumes its entry); the second stays unexpected.
        parse = oracle.parse_manifest_entries([
            {"ut": 119.7, "kind": "milestone", "funds": 4800.0}])
        awards = [self._award(4800.0, "RecordsSpeed", ut=119.7),
                  self._award(4800.0, "RecordsAltitude", ut=119.7)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, awards)
        self.assertEqual(1, len(unmatched))
        self.assertEqual("RecordsAltitude", unmatched[0].reason)
        # Two declared entries explain both.
        parse2 = oracle.parse_manifest_entries([
            {"ut": 119.7, "kind": "milestone", "funds": 4800.0, "seq": 0},
            {"ut": 119.7, "kind": "milestone", "funds": 4800.0, "seq": 1}])
        self.assertEqual([], hlib.unmatched_captured_awards(parse2.entries, awards))

    def test_a_wrong_amount_or_wrong_facet_does_not_corroborate(self):
        parse = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "kerbal-hire", "funds": -62113.0}])
        # Same seqKey, same facet, different amount well outside the funds tolerance.
        self.assertEqual(1, len(hlib.unmatched_captured_awards(
            parse.entries, [self._award(-1000.0, "CrewRecruited")])))
        # Same seqKey and amount but on a pool the entry does not touch.
        self.assertEqual(1, len(hlib.unmatched_captured_awards(
            parse.entries, [self._award(-62113.0, "X", facet="reputation")])))

    def test_nominal_vs_applied_reputation_corroborates_inside_the_tolerance(self):
        # A seam entry declares the NOMINAL rep delta (-10); the stock line reports
        # the APPLIED post-curve one (MEASURED -9.999828). Exact-compare would make
        # every rep award unexpected forever; the facet tolerance is what closes it.
        parse = oracle.parse_manifest_entries([
            {"ut": 119.7, "kind": "milestone", "reputation": -10.0}])
        award = self._award(-9.999828, "VesselLoss", facet="reputation", ut=119.7)
        self.assertEqual([], hlib.unmatched_captured_awards(parse.entries, [award]))

    def test_a_multi_facet_entry_corroborates_one_award_per_pool(self):
        # SECOND-ORDER REGRESSION, same class as the kind join one level down:
        # consumption used to be per ENTRY, so the canonical contract-complete shape -
        # ONE entry declaring BOTH funds and reputation against TWO award lines at the
        # same seqKey - was swallowed whole by whichever line matched first, stranding
        # its sibling as permanently "unexpected". NOTE the two-line premise is
        # SYNTHETIC: KSP logs no funds award, so a real log never carries the funds half
        # (hlib.STOCK_AWARD_PATTERNS_DEAD). The per-pool rule is kept as fail-closed
        # structure against a future second-pool producer, not as live behaviour.
        parse = oracle.parse_manifest_entries([
            {"ut": 500.0, "kind": "contract-complete", "funds": 1000.0,
             "reputation": 5.0, "contractGuid": "g1"}])
        self.assertEqual([], list(parse.errors))
        awards = [self._award(1000.0, "ContractReward", ut=500.0),
                  self._award(5.0, "ContractReward", facet="reputation", ut=500.0)]
        self.assertEqual([], hlib.unmatched_captured_awards(parse.entries, awards),
                         "a funds+rep entry must explain BOTH of its own award lines")
        # ... in either log order.
        self.assertEqual([], hlib.unmatched_captured_awards(parse.entries,
                                                            list(reversed(awards))))

    def test_one_to_one_still_holds_within_each_pool(self):
        # The per-pool relaxation must not become per-pool-unlimited: a SECOND funds
        # award at the same seqKey has nothing left to explain it.
        parse = oracle.parse_manifest_entries([
            {"ut": 500.0, "kind": "contract-complete", "funds": 1000.0,
             "reputation": 5.0, "contractGuid": "g1"}])
        awards = [self._award(1000.0, "ContractReward", ut=500.0),
                  self._award(5.0, "ContractReward", facet="reputation", ut=500.0),
                  self._award(1000.0, "ContractReward", ut=500.0)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, awards)
        self.assertEqual(1, len(unmatched))
        self.assertEqual("funds", unmatched[0].facet)

    def test_a_pinned_entry_is_not_stranded_by_a_greedy_unconstrained_match(self):
        # GREEDY-ORDER TRAP: one entry pinned to a reason and one unconstrained entry,
        # same amount and seqKey. The match is greedy, so without trying pinned
        # entries FIRST the pinned award could take the unconstrained entry and leave
        # the other award unexplained - penalizing the author who pinned reasons.
        # Must hold in BOTH log orders and regardless of declaration order.
        for manifest in (
                [{"ut": 0.0, "kind": "milestone", "funds": 4800.0},
                 {"ut": 0.0, "kind": "milestone", "funds": 4800.0,
                  "stockReason": ["RecordsSpeed"]}],
                [{"ut": 0.0, "kind": "milestone", "funds": 4800.0,
                  "stockReason": ["RecordsSpeed"]},
                 {"ut": 0.0, "kind": "milestone", "funds": 4800.0}]):
            parse = oracle.parse_manifest_entries(manifest)
            self.assertEqual([], list(parse.errors))
            awards = [self._award(4800.0, "RecordsSpeed"),
                      self._award(4800.0, "RecordsAltitude")]
            for order in (awards, list(reversed(awards))):
                with self.subTest(declared=manifest[0].get("stockReason"),
                                  first=order[0].reason):
                    self.assertEqual(
                        [], hlib.unmatched_captured_awards(parse.entries, order),
                        "both awards must be explained in every order")

    def test_declared_stock_reason_tightens_the_match(self):
        # The OPTIONAL tightener: an author who has read a green run's capturedRaw can
        # pin the entry to a named stock effect, so a coincidental same-amount award
        # at the same UT no longer corroborates it.
        parse = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "kerbal-hire", "funds": -62113.0,
             "stockReason": ["CrewRecruited"]}])
        self.assertEqual([], list(parse.errors))
        self.assertEqual(("CrewRecruited",), parse.entries[0].stock_reasons)
        self.assertEqual([], hlib.unmatched_captured_awards(
            parse.entries, [self._award(-62113.0, "CrewRecruited")]))
        self.assertEqual(1, len(hlib.unmatched_captured_awards(
            parse.entries, [self._award(-62113.0, "SomethingElse")])))

    def test_stock_reason_accepts_a_bare_string_and_rejects_a_non_string(self):
        one = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "kerbal-hire", "funds": -1.0, "stockReason": "X"}])
        self.assertEqual([], list(one.errors))
        self.assertEqual(("X",), one.entries[0].stock_reasons)
        bad = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "kerbal-hire", "funds": -1.0, "stockReason": [7]}])
        self.assertFalse(bad.ok)
        self.assertTrue(any("stockReason" in e for e in bad.errors))

    def test_corroboration_never_touches_the_expected_totals(self):
        # M-B2 INDEPENDENCE: a captured award must not be summed into EXPECTED, or the
        # capture would start certifying itself.
        #
        # THE PINNED 437887 IS THE INDEPENDENCE WITNESS - seed 500000 plus the ONE
        # seam-declared -62113, and nothing else. It is what distinguishes the two
        # worlds: in a world where captured amounts leaked into EXPECTED, running the
        # capture over a log carrying FIVE more awards would move this number. (A bare
        # double-compute over identical args would prove nothing - compute_expected is
        # pure, so it is a tautology.)
        seed = oracle.SeedBaseline(funds=500000.0, science=0.0, reputation=0.0)
        parse = oracle.parse_manifest_entries([
            {"ut": 0.0, "kind": "kerbal-hire", "funds": -62113.0}])
        expected = oracle.compute_expected(seed, parse.entries, oracle.default_tolerances(), [])
        self.assertEqual(437887.0, expected.funds)

        # One corroborating award plus several UNRELATED ones the manifest never
        # declared (each worth real funds/science/rep).
        awards = [self._award(-62113.0, "CrewRecruited"),
                  self._award(4800.0, "RecordsSpeed", ut=119.7),
                  self._award(800.0, "FirstLaunch", ut=119.7),
                  self._award(4800.0, "RecordsAltitude", ut=119.7),
                  self._award(-9.999828, "VesselLoss", facet="reputation", ut=119.7),
                  self._award(0.9999995, "Progression", facet="reputation", ut=119.7)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, awards)
        self.assertEqual(5, len(unmatched), "only the declared hire corroborates")

        after = oracle.compute_expected(seed, parse.entries, oracle.default_tolerances(), [])
        self.assertEqual(
            437887.0, after.funds,
            "EXPECTED must STILL be seed + the one seam-declared delta after "
            "corroborating over a log carrying 10,400 more funds of stock awards")
        self.assertEqual(0.0, after.reputation,
                         "captured rep awards must not reach the expected rep pool")
        self.assertEqual(0.0, after.science)

    def test_only_the_armed_allowlist_arms_the_capture_cross_check(self):
        # NIT 1 / the HARD SAFETY PROPERTY for gap 1. This cell asserted the empty set
        # ("the escalation path exists but nothing walks it yet") from the day the knob
        # shipped until 2026-07-31, when the path was walked end to end against the
        # real game. It is now an explicit ALLOWLIST: the escalation path has exactly
        # one walker, and a SECOND spec arming the gate still reds here until someone
        # records that spec's own arming evidence.
        #
        # CL-2-pod-impact-ledger is the only committed scenario measured producing
        # reputation, and the capture is reputation-only (KSP logs no funds and no
        # science award line), so it is the only place the gate has a non-empty input
        # at all - arming an L1 spec would arm a no-op gate reading green forever.
        # Evidence: three flights on 2026-07-31, one per checklist step - baseline
        # `2026-07-31_1630` (3 unexpected, report-only), windows-declared
        # `2026-07-31_1638` (0 unexpected, still report), armed `2026-07-31_1645`
        # (PASS with the gate live). Full record in the spec's own comment block.
        allowed = {"CL-2-pod-impact-ledger.toml"}
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            ledger = ((load_spec(name).get("expectations", {}) or {}).get("ledger") or {})
            if hlib.capture_cross_check_gates(ledger):
                armed.append(name)
        self.assertEqual(sorted(allowed), sorted(set(armed)),
                         "a committed spec outside the armed allowlist armed captureCrossCheck")

    def test_gate_mode_resolution_and_validation(self):
        self.assertFalse(hlib.capture_cross_check_gates(None))
        self.assertFalse(hlib.capture_cross_check_gates({}))
        self.assertFalse(hlib.capture_cross_check_gates({"captureCrossCheck": "report"}))
        self.assertTrue(hlib.capture_cross_check_gates({"captureCrossCheck": "gate"}))
        self.assertEqual([], hlib.validate_ledger_expectations({"captureCrossCheck": "gate"}))
        errs = hlib.validate_ledger_expectations({"captureCrossCheck": "GATE"})
        self.assertTrue(any("captureCrossCheck" in e for e in errs))


class FlownScenarioUtWindowCorroborationTests(unittest.TestCase):
    """THE FLOWN-SCENARIO CORROBORATION KEY (found by CL-2-pod-impact-ledger's first
    live capture, 2026-07-30; fixed 2026-07-31).

    A captured award's seq_key is UT-valued whenever a UT-stamped [Parsek] line
    precedes it, and a flown spec cannot pin that UT: the same impact measured
    ut 119.7 / 119.9 / 119.8 across three runs of one craft. So the exact seqKey join
    could never fire on a flight and every captured award reported "unexpected",
    which made `captureCrossCheck = "gate"` unarmable exactly where it matters (the
    only committed scenario that can ARM the check - CL-1 earns the same three
    awards but carries no ledger block and is forbidden one). The fix is the OPT-IN
    per-entry `utWindow = [lo, hi]`: a windowed entry corroborates a captured award
    whose UT falls inside the inclusive bounds, with every OTHER predicate (facet,
    amount-within-tolerance, structured identity, stockReason, one-to-one per
    (entry, pool) consumption) unchanged. Exactly ONE committed spec declares a
    window - CL-2 itself, ARMED 2026-07-31 over three flights (allowlisted below);
    every other manifest is window-free and matches byte-identically to the
    pre-window behavior."""

    # The three CL-2 flight-1 capture lines, VERBATIM from run
    # 2026-07-30_1711_CL-2-pod-impact-ledger (2026-07-30_1721 measured the same
    # award set at 12.4 / 19.0 / 119.8 - the UTs move, the shape does not): all
    # three captured cleanly (`stockLines=3 deduped=3 seamRejected=0`) and all three
    # then reported UNEXPECTED against a manifest that declares every one of them.
    CL2_MEASURED_UNEXPECTED = [
        "manifest-capture: unexpected stock award ut=12.5  kind=stock-reputation-award reason=Progression",
        "manifest-capture: unexpected stock award ut=19.0  kind=stock-reputation-award reason=Progression",
        "manifest-capture: unexpected stock award ut=119.9 kind=stock-reputation-award reason=VesselLoss",
    ]
    # The measured impact-UT spread of the SAME craft: archived B1 run, CL-2
    # flight 1, CL-2 flight 2. What makes the exact key unpinnable.
    CL2_IMPACT_UT_SPREAD = (119.7, 119.9, 119.8)

    def _rep_award(self, amount, reason, ut, seq=0):
        return hlib.CapturedAward(kind="stock-reputation-award", facet="reputation",
                                  amount=amount, contract_guid="", subject_id="",
                                  ut=ut, seq=seq, raw_line="line", reason=reason)

    def _cl2_windowed_manifest(self):
        # CL-2's four manifest entries, reshaped onto windows an author can honestly
        # declare from mission knowledge: the two Progression awards land during the
        # early ascent, the VesselLoss award at the ~120 s impact. (The committed
        # CL-2 spec deliberately does NOT declare these - arming is an operator
        # action; this is the shape that MAKES it possible.)
        return oracle.parse_manifest_entries([
            {"utWindow": [0.0, 60.0], "kind": "stock-reputation-award",
             "reputation": 0.9999995, "repMode": "applied",
             "stockReason": "Progression", "provenance": "gameevents-captured", "seq": 0},
            {"utWindow": [0.0, 60.0], "kind": "stock-reputation-award",
             "reputation": 0.9999995, "repMode": "applied",
             "stockReason": "Progression", "provenance": "gameevents-captured", "seq": 1},
            {"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
             "reputation": -9.999828, "repMode": "applied",
             "stockReason": "VesselLoss", "provenance": "gameevents-captured", "seq": 2},
            {"seq": 3, "kind": "milestone", "funds": 29600.0,
             "provenance": "seam-declared"}])

    def test_the_cl2_capture_corroborates_through_declared_windows(self):
        # The end-to-end reproduction, from the REAL log idiom: the three stock rep
        # lines CL-2 captured, each UT-correlated off its neighbouring [Parsek]
        # stamp, against the windowed manifest. Before utWindow every one of these
        # reported unexpected (the CL2_MEASURED_UNEXPECTED literals above).
        log = ("[LOG] [Parsek][INFO][Flight] launch ut=12.5\n"
               "[LOG] Added 0.9999995 (1) reputation: 'Progression'.\n"
               "[LOG] [Parsek][INFO][Flight] ascent ut=19.0\n"
               "[LOG] Added 0.9999995 (1) reputation: 'Progression'.\n"
               "[LOG] [Parsek][INFO][Flight] impact ut=119.9\n"
               "[LOG] Added -9.999828 (-10) reputation: 'VesselLoss'.\n")
        cap = hlib.parse_stock_award_lines(log)
        self.assertEqual(3, len(cap.captured), "all three CL-2 lines must capture")
        self.assertEqual([12.5, 19.0, 119.9], [c.ut for c in cap.captured])
        parse = self._cl2_windowed_manifest()
        self.assertEqual([], list(parse.errors))
        deduped = hlib.dedupe_captured_awards(cap.captured)
        self.assertEqual(
            [], hlib.unmatched_captured_awards(parse.entries, deduped),
            "the CL-2 capture must fully corroborate once windows are declared - "
            "this is what makes captureCrossCheck armable on a flown scenario")

    def test_the_vessel_loss_award_corroborates_across_the_measured_ut_spread(self):
        # One declared window explains the impact award at EVERY measured UT of the
        # three runs - the exact property the exact-key join lacked.
        parse = oracle.parse_manifest_entries([
            {"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
             "reputation": -9.999828, "repMode": "applied",
             "stockReason": "VesselLoss", "provenance": "gameevents-captured"}])
        self.assertEqual([], list(parse.errors))
        for ut in self.CL2_IMPACT_UT_SPREAD:
            with self.subTest(ut=ut):
                award = self._rep_award(-9.999828, "VesselLoss", ut=ut)
                self.assertEqual(
                    [], hlib.unmatched_captured_awards(parse.entries, [award]),
                    "impact at ut=%r must corroborate the [100,140] window" % ut)

    def test_an_undeclared_award_is_still_unexpected_alongside_windows(self):
        # The signal survives: an award no window (and no exact key) explains stays
        # unexpected - exactly what an operator reviews before arming the gate.
        parse = self._cl2_windowed_manifest()
        awards = [self._rep_award(0.9999995, "Progression", ut=12.5),
                  self._rep_award(0.9999995, "Progression", ut=19.0),
                  self._rep_award(-9.999828, "VesselLoss", ut=119.9),
                  self._rep_award(5.0, "ContractReward", ut=119.9)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, awards)
        self.assertEqual(["ContractReward"], [c.reason for c in unmatched])

    def test_a_near_window_miss_stays_unexpected_and_the_bounds_are_inclusive(self):
        parse = oracle.parse_manifest_entries([
            {"utWindow": [100.0, 119.8], "kind": "stock-reputation-award",
             "reputation": -9.999828, "repMode": "applied",
             "provenance": "gameevents-captured"}])
        self.assertEqual([], list(parse.errors))
        # 119.9 is 0.1 s past the declared hi -> unexpected (the window is a declared
        # bound, not a tolerance that stretches).
        miss = self._rep_award(-9.999828, "VesselLoss", ut=119.9)
        self.assertEqual(1, len(hlib.unmatched_captured_awards(parse.entries, [miss])))
        # ... and exactly ON either bound matches (inclusive).
        for ut in (100.0, 119.8):
            with self.subTest(ut=ut):
                on_bound = self._rep_award(-9.999828, "VesselLoss", ut=ut)
                self.assertEqual(
                    [], hlib.unmatched_captured_awards(parse.entries, [on_bound]))

    def test_two_awards_straddling_one_window_leave_the_second_unexpected(self):
        # ONE-TO-ONE PER (ENTRY, POOL) is preserved across the window key: CL-2's two
        # 'Progression' +1 awards both fall inside a single early-ascent window; one
        # declared entry explains exactly one of them.
        one = oracle.parse_manifest_entries([
            {"utWindow": [0.0, 60.0], "kind": "stock-reputation-award",
             "reputation": 0.9999995, "repMode": "applied",
             "provenance": "gameevents-captured"}])
        self.assertEqual([], list(one.errors))
        awards = [self._rep_award(0.9999995, "Progression", ut=12.5),
                  self._rep_award(0.9999995, "Progression", ut=19.0)]
        unmatched = hlib.unmatched_captured_awards(one.entries, awards)
        self.assertEqual(1, len(unmatched), "the second award has nothing left to consume")
        # Two declared entries (CL-2's actual shape) explain both, in either order.
        two = oracle.parse_manifest_entries([
            {"utWindow": [0.0, 60.0], "kind": "stock-reputation-award",
             "reputation": 0.9999995, "repMode": "applied",
             "provenance": "gameevents-captured", "seq": 0},
            {"utWindow": [0.0, 60.0], "kind": "stock-reputation-award",
             "reputation": 0.9999995, "repMode": "applied",
             "provenance": "gameevents-captured", "seq": 1}])
        self.assertEqual([], hlib.unmatched_captured_awards(two.entries, awards))
        self.assertEqual([], hlib.unmatched_captured_awards(two.entries,
                                                            list(reversed(awards))))

    def test_a_null_ut_award_never_window_matches(self):
        # Fail-closed: an award with no UT-stamped [Parsek] neighbor has no position
        # to judge against the bounds, so it stays unexpected rather than being
        # window-matched on faith.
        parse = oracle.parse_manifest_entries([
            {"utWindow": [0.0, 1e9], "kind": "stock-reputation-award",
             "reputation": 1.0, "repMode": "applied",
             "provenance": "gameevents-captured"}])
        self.assertEqual([], list(parse.errors))
        award = self._rep_award(1.0, "Progression", ut=None, seq=7)
        self.assertEqual(1, len(hlib.unmatched_captured_awards(parse.entries, [award])),
                         "a null-UT award must not match even an everything-window")

    def test_window_matching_still_honors_amount_facet_and_reason(self):
        parse = oracle.parse_manifest_entries([
            {"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
             "reputation": -9.999828, "repMode": "applied",
             "stockReason": "VesselLoss", "provenance": "gameevents-captured"}])
        # Wrong amount (outside the 0.1 rep tolerance) inside the window -> unexpected.
        self.assertEqual(1, len(hlib.unmatched_captured_awards(
            parse.entries, [self._rep_award(-5.0, "VesselLoss", ut=119.9)])))
        # Right amount, wrong declared reason -> unexpected (the tightener composes).
        self.assertEqual(1, len(hlib.unmatched_captured_awards(
            parse.entries, [self._rep_award(-9.999828, "Progression", ut=119.9)])))
        # Nominal-vs-applied still rides the facet tolerance inside a window.
        parse2 = oracle.parse_manifest_entries([
            {"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
             "reputation": -10.0, "provenance": "seam-declared"}])
        self.assertEqual([], hlib.unmatched_captured_awards(
            parse2.entries, [self._rep_award(-9.999828, "VesselLoss", ut=119.9)]))

    def test_an_exact_key_entry_is_not_stranded_by_a_greedy_window(self):
        # Candidate-order refinement, same reasoning as pinned-first: the narrower
        # exact key is tried before a window, so a window cannot swallow the one
        # award an exact entry names. Must hold in both log orders and both
        # declaration orders.
        for manifest in (
                [{"ut": 119.9, "kind": "milestone", "reputation": -10.0},
                 {"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
                  "reputation": -10.0, "provenance": "seam-declared", "seq": 1}],
                [{"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
                  "reputation": -10.0, "provenance": "seam-declared", "seq": 1},
                 {"ut": 119.9, "kind": "milestone", "reputation": -10.0}]):
            parse = oracle.parse_manifest_entries(manifest)
            self.assertEqual([], list(parse.errors))
            awards = [self._rep_award(-9.999828, "VesselLoss", ut=119.9),
                      self._rep_award(-9.999828, "CrewDeath", ut=130.0)]
            for order in (awards, list(reversed(awards))):
                with self.subTest(first=order[0].reason,
                                  declared_first="ut" in manifest[0]):
                    self.assertEqual(
                        [], hlib.unmatched_captured_awards(parse.entries, order),
                        "both awards must be explained in every order")

    def test_window_validation_rejects_every_malformed_shape(self):
        base = {"kind": "stock-reputation-award", "reputation": 1.0,
                "repMode": "applied", "provenance": "gameevents-captured"}
        bad_shapes = [
            "not-a-list", [1.0], [1.0, 2.0, 3.0], [1.0, "x"], [True, 2.0],
            [float("nan"), 2.0], [1.0, float("inf")], {"lo": 1.0, "hi": 2.0},
        ]
        for shape in bad_shapes:
            with self.subTest(shape=shape):
                parse = oracle.parse_manifest_entries([dict(base, utWindow=shape)])
                self.assertFalse(parse.ok, "shape %r must reject" % (shape,))
                self.assertTrue(any("utWindow" in e for e in parse.errors))
        # lo > hi is an empty window: a declaration that can never match must reject
        # loudly rather than silently corroborating nothing.
        inverted = oracle.parse_manifest_entries([dict(base, utWindow=[140.0, 100.0])])
        self.assertFalse(inverted.ok)
        self.assertTrue(any("empty window" in e for e in inverted.errors))
        # ut + utWindow together is ambiguous (two keys, one entry) -> reject.
        both = oracle.parse_manifest_entries(
            [dict(base, ut=119.9, utWindow=[100.0, 140.0])])
        self.assertFalse(both.ok)
        self.assertTrue(any("mutually exclusive" in e for e in both.errors))
        # A degenerate lo == hi window is legal (an author pinning an exact UT
        # through the window spelling) and matches exactly that UT.
        pin = oracle.parse_manifest_entries([dict(base, utWindow=[19.0, 19.0])])
        self.assertEqual([], list(pin.errors))
        self.assertEqual((19.0, 19.0), pin.entries[0].ut_window)

    def test_window_never_touches_the_expected_totals(self):
        # M-B2 INDEPENDENCE across the new key: the window is a MATCHING hint only.
        # EXPECTED is seed + the declared deltas whether or not anything corroborates,
        # and a corroborated captured amount is never summed in.
        seed = oracle.SeedBaseline(funds=500000.0, science=100.0, reputation=0.0)
        parse = self._cl2_windowed_manifest()
        expected = oracle.compute_expected(seed, parse.entries,
                                           oracle.default_tolerances(), [])
        self.assertEqual(529600.0, expected.funds)
        self.assertEqual(100.0, expected.science)
        # The rep pool accumulates the three APPLIED declared deltas (the applied
        # mode adds them raw): 0.9999995 + 0.9999995 - 9.999828.
        self.assertAlmostEqual(-7.999829, expected.reputation, places=6)
        # Running the cross-check over a log with MORE awards moves nothing.
        awards = [self._rep_award(0.9999995, "Progression", ut=12.5),
                  self._rep_award(-9.999828, "VesselLoss", ut=119.9),
                  self._rep_award(100.0, "ContractReward", ut=50.0)]
        hlib.unmatched_captured_awards(parse.entries, awards)
        after = oracle.compute_expected(seed, parse.entries,
                                        oracle.default_tolerances(), [])
        self.assertEqual(expected.funds, after.funds)
        self.assertEqual(expected.reputation, after.reputation)

    def test_a_narrow_window_is_not_stranded_by_a_wide_one(self):
        # PR #1397 adversarial-review finding (probe S1): with the exact-vs-window
        # secondary key alone, a WIDE window declared first swallowed the award a
        # NARROW window named and stranded it - a false "unexpected" row on a
        # correct run, which under `gate` is a false PARSEK-FAIL(ledger). The
        # secondary key is now acceptance WIDTH (exact = 0), so the narrow window is
        # tried first in every declaration order and every log order.
        for manifest in (
                [{"utWindow": [0.0, 200.0], "kind": "stock-reputation-award",
                  "reputation": -10.0, "provenance": "seam-declared", "seq": 0},
                 {"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
                  "reputation": -10.0, "provenance": "seam-declared", "seq": 1}],
                [{"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
                  "reputation": -10.0, "provenance": "seam-declared", "seq": 0},
                 {"utWindow": [0.0, 200.0], "kind": "stock-reputation-award",
                  "reputation": -10.0, "provenance": "seam-declared", "seq": 1}]):
            parse = oracle.parse_manifest_entries(manifest)
            self.assertEqual([], list(parse.errors))
            awards = [self._rep_award(-9.999828, "VesselLoss", ut=120.0),
                      self._rep_award(-9.999828, "CrewDeath", ut=50.0)]
            for order in (awards, list(reversed(awards))):
                with self.subTest(wide_first="200" in str(manifest[0]),
                                  first_award=order[0].ut):
                    self.assertEqual(
                        [], hlib.unmatched_captured_awards(parse.entries, order),
                        "the narrow window must be tried before the wide one")

    def test_the_documented_greedy_limit_a_pinned_wide_window_beats_an_unpinned_exact(self):
        # KNOWN GREEDY LIMIT, pinned deliberately (review probe S4; documented in the
        # unmatched_captured_awards ordering comment and the arming checklist): the
        # pinned-first PRIMARY key dominates the width key, so a stockReason-PINNED
        # wide window is tried before an UNPINNED exact entry and takes the one award
        # that entry names when both awards carry the pinned reason. The stranding is
        # fail-CLOSED (one false "unexpected" row, never a false green). If this
        # assert ever flips to [], the matcher gained a bipartite search - update the
        # ordering comment and the arming checklist in the same change.
        parse = oracle.parse_manifest_entries([
            {"ut": 50.0, "kind": "milestone", "reputation": -10.0, "seq": 0},
            {"utWindow": [0.0, 200.0], "kind": "stock-reputation-award",
             "reputation": -10.0, "stockReason": "VesselLoss",
             "provenance": "seam-declared", "seq": 1}])
        self.assertEqual([], list(parse.errors))
        awards = [self._rep_award(-9.999828, "VesselLoss", ut=50.0),
                  self._rep_award(-9.999828, "VesselLoss", ut=120.0)]
        unmatched = hlib.unmatched_captured_awards(parse.entries, awards)
        self.assertEqual(1, len(unmatched), "the documented greedy limit")

    def test_a_malformed_window_reds_pre_launch_in_the_ledger_block_validator(self):
        # Review MINOR-4: a window typo used to survive ADMIT and surface AFTER the
        # flight as a hard manifest-parse-error PARSEK-FAIL. validate_ledger_
        # expectations (the same call run.py makes before launching KSP) now mirrors
        # the oracle's structural utWindow rules, so the typo costs seconds.
        def block(entry):
            return {"seedFrom": "template", "manifest": [entry]}
        base = {"kind": "stock-reputation-award", "reputation": 1.0,
                "repMode": "applied", "provenance": "gameevents-captured"}
        self.assertEqual([], hlib.validate_ledger_expectations(
            block(dict(base, utWindow=[100.0, 140.0]))))
        self.assertEqual([], hlib.validate_ledger_expectations(
            block(base)), "a window-free entry stays untouched pre-launch")
        for shape in ("oops", [1.0], [1.0, 2.0, 3.0], [True, 2.0],
                      [float("nan"), 2.0], [1.0, "x"]):
            with self.subTest(shape=shape):
                errs = hlib.validate_ledger_expectations(block(dict(base, utWindow=shape)))
                self.assertTrue(any("manifest[0].utWindow" in e for e in errs),
                                "shape %r must red pre-launch" % (shape,))
        inverted = hlib.validate_ledger_expectations(
            block(dict(base, utWindow=[140.0, 100.0])))
        self.assertTrue(any("empty window" in e for e in inverted))
        both = hlib.validate_ledger_expectations(
            block(dict(base, ut=119.9, utWindow=[100.0, 140.0])))
        self.assertTrue(any("mutually exclusive" in e for e in both))
        # A non-table entry now reds pre-launch too (it used to be left for the
        # oracle's own indexed rejection, i.e. for after the flight), and it reds
        # with the ORACLE's own wording because the gate delegates to it.
        self.assertTrue(any("not a table/object" in e for e in
                            hlib.validate_ledger_expectations(
                                {"seedFrom": "template", "manifest": ["not-a-table"]})))

    def test_manifest_artifact_round_trips_window_and_stock_reason(self):
        # The <runId>.manifest.json audit artifact must carry the entry's full
        # matching constraints (review NIT-1: stockReason was omitted) and parse
        # back identically through the same entry parser.
        parse = oracle.parse_manifest_entries([
            {"utWindow": [100.0, 140.0], "kind": "stock-reputation-award",
             "reputation": -9.999828, "repMode": "applied",
             "stockReason": "VesselLoss", "provenance": "gameevents-captured"},
            {"seq": 3, "kind": "milestone", "funds": 29600.0}])
        self.assertEqual([], list(parse.errors))
        serialized = [run._manifest_entry_to_dict(e) for e in parse.entries]
        self.assertEqual([100.0, 140.0], serialized[0]["utWindow"])
        self.assertEqual(["VesselLoss"], serialized[0]["stockReason"])
        self.assertIsNone(serialized[1]["utWindow"])
        self.assertEqual([], serialized[1]["stockReason"])
        reparse = oracle.parse_manifest_entries(serialized)
        self.assertEqual([], list(reparse.errors))
        self.assertEqual(parse.entries[0].ut_window, reparse.entries[0].ut_window)
        self.assertEqual(parse.entries[0].stock_reasons, reparse.entries[0].stock_reasons)
        self.assertIsNone(reparse.entries[1].ut_window)

    def test_only_the_armed_allowlist_declares_a_ut_window(self):
        # THE WHOLE-SET GUARD, mirroring test_only_the_armed_allowlist_arms_the_
        # capture_cross_check. It was "NO committed spec declares one" until the
        # mechanism was walked end to end against the real game on 2026-07-31; it is
        # now an explicit ALLOWLIST, so a window appearing on any OTHER spec is still
        # a drift this cell reds on. Adding a name here is the deliberate act: it
        # requires the arming evidence below (a green run's capturedRaw, then a green
        # run with the windows declared showing the unexpected rows at zero).
        #
        # CL-2-pod-impact-ledger, ARMED 2026-07-31 (this file's own three flights):
        #   `2026-07-31_1630`  PASS 168 s a1, spec UNCHANGED - the honest baseline.
        #                      stockLines=3 deduped=3 seamRejected=0, all three
        #                      UNEXPECTED (report-only) at ut 12.5 / 19.1 / 119.9.
        #   `2026-07-31_1638`  PASS a1, windows declared + still "report" - ZERO
        #                      unexpected rows, ledgerOracle reportOnly 3 -> 0.
        #   `2026-07-31_1645`  PASS a1, `captureCrossCheck = "gate"` live.
        # The windows are PHASE BOUNDS, never pins: that same second Progression
        # award measured ut 19.0 on 2026-07-30 and 19.1 on the baseline flight, and
        # the impact has now measured 119.7 / 119.8 / 119.9 across six runs.
        allowed = {"CL-2-pod-impact-ledger.toml"}
        declaring = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            ledger = ((load_spec(name).get("expectations", {}) or {}).get("ledger") or {})
            for entry in (ledger.get("manifest", []) or []):
                if isinstance(entry, dict) and (
                        "utWindow" in entry or "ut_window" in entry):
                    declaring.append(name)
        self.assertEqual(sorted(allowed), sorted(set(declaring)),
                         "a committed spec outside the armed allowlist declares utWindow")

    def test_the_armed_cl2_windows_are_the_measured_phase_bounds(self):
        # The allowlist above says WHICH spec may declare windows; this says WHAT it
        # declares, so a later broadening (a window quietly widened to swallow a red,
        # which the spec's RE-PIN CONTRACT forbids) reds here rather than passing
        # silently. Bounds are the mission's two phases and both comfortably bracket
        # every UT the six archived runs measured, with pad-dwell headroom on top
        # (the bounds are absolute Planetarium UT, not T+ - see the spec's comment).
        ledger = ((load_spec("CL-2-pod-impact-ledger.toml").get("expectations", {}) or {})
                  .get("ledger") or {})
        self.assertEqual(4, len(ledger["manifest"]),
                         "a 5th entry appeared; re-derive the bounds deliberately")
        windows = {e["seq"]: e.get("utWindow") for e in ledger["manifest"]
                   if isinstance(e, dict)}
        self.assertEqual([0.0, 100.0], windows[0], "Progression #1 = ascent phase")
        self.assertEqual([0.0, 100.0], windows[1], "Progression #2 = ascent phase")
        self.assertEqual([100.0, 400.0], windows[2], "VesselLoss = impact phase")
        self.assertIsNone(windows.get(3),
                          "the funds milestone entry must stay window-free: KSP logs "
                          "no funds award, so it is never capture-matched")
        # The two groups do not OVERLAP (they abut at 100). A gap between them would
        # be a place an award could land and be unmatched for no intended reason;
        # a true overlap would let a phase's window reach the other's awards before
        # the amount / stockReason predicates run.
        self.assertLessEqual(windows[0][1], windows[2][0])


class WhatThePreLaunchGateMirrorsTests(unittest.TestCase):
    """THE PRE-LAUNCH GATE **IS** THE RUN-TIME PARSER, minus one carve-out.

    A manifest entry the oracle rejects becomes a HARD manifest-parse-error
    PARSEK-FAIL(ledger) AFTER the flight (run.py: "a rejected seam entry ... would
    false-PASS if silently dropped; each rejection reds PARSEK-FAIL(ledger)"). So
    every rule the pre-launch validator does NOT enforce is a shape that costs a full
    flight to discover - the same economics that moved `utWindow` into the gate
    (review MINOR-4) and that the stage-inject postcondition closed on the staging
    side.

    HISTORY, because the boundary moved twice and the second move is the lesson. The
    gate first mirrored `utWindow` alone, then `utWindow` + `ut` + entry-is-a-table,
    justifying the remainder as "value/semantic rules rather than shape rules". The
    PR #1397 review refuted that: running the oracle with `captured=None` - exactly
    pre-launch knowledge - rejects EVERY one of its rules deterministically, and two
    of the supposedly-semantic ones (`seq` must be an int; `stockReason` must be a
    non-empty string or array of them) are pure type/shape rules that CL-2's own
    manifest hand-writes on every entry. The honest question is not "shape or
    value?" but "does the rule need the produced log?" - and only one does. The gate
    now DELEGATES to `oracle.parse_manifest_entries`, so there is no second
    implementation to drift.

    THE ONE CARVE-OUT: the funds fill-from-capture ambiguity reads the CAPTURED pool,
    empty by construction pre-launch, so it would reject a spec the oracle could
    accept at run time - the unsafe direction. It is skipped, and these cells pin
    that it stays skipped.

    These cells therefore no longer police a hand-rolled mirror; they pin the
    DELEGATION (every oracle rule reaches the gate, the carve-out does not, key paths
    are re-prefixed to the spec's own namespace, and no committed spec is refused).
    """

    BASE = {"kind": "stock-reputation-award", "reputation": 1.0,
            "repMode": "applied", "provenance": "gameevents-captured"}

    # THE FULL ENTRY-SHAPE SPACE, not just the once-mirrored keys: since the gate
    # delegates, agreement must hold over EVERY rule the oracle has, in BOTH
    # directions (W4-probe shape from the PR #1397 review). The `_unmirrored_*`
    # group below is the set the old hand-rolled mirror let through - it is here
    # precisely because those shapes used to diverge.
    def _mirrored_shape_space(self):
        shapes = [("well-formed, no ut/window", dict(self.BASE)),
                  ("ut valid", dict(self.BASE, ut=119.9)),
                  ("ut null", dict(self.BASE, ut=None)),
                  ("window valid", dict(self.BASE, utWindow=[100.0, 140.0])),
                  ("window degenerate lo==hi", dict(self.BASE, utWindow=[5.0, 5.0])),
                  ("not a table (str)", "not-a-table"),
                  ("not a table (list)", [1, 2]),
                  ("not a table (int)", 7),
                  ("not a table (None)", None)]
        for bad_ut in (True, False, "x", float("nan"), float("inf"),
                       float("-inf"), [1.0], {"a": 1}):
            shapes.append(("ut=%r" % (bad_ut,), dict(self.BASE, ut=bad_ut)))
        for bad_win in ("oops", [1.0], [1.0, 2.0, 3.0], [True, 2.0], [1.0, False],
                        [float("nan"), 2.0], [1.0, float("inf")], [1.0, "x"],
                        {"lo": 1.0}, 5.0, [140.0, 100.0]):
            shapes.append(("utWindow=%r" % (bad_win,), dict(self.BASE, utWindow=bad_win)))
        shapes.append(("ut + window together",
                       dict(self.BASE, ut=119.9, utWindow=[100.0, 140.0])))
        # A malformed ut AND a window: both sides must blame the ut, not the
        # mutual exclusion, because the oracle parses ut first.
        shapes.append(("bad ut + valid window",
                       dict(self.BASE, ut="x", utWindow=[100.0, 140.0])))
        # THE FORMERLY-UNMIRRORED RULES. Every one of these passed the hand-rolled
        # gate and hard-failed after the flight; `seq` and `stockReason` in
        # particular are hand-written on every CL-2 entry.
        for label, over in (("seq float", {"seq": 1.5}),
                            ("seq str", {"seq": "0"}),
                            ("seq bool", {"seq": True}),
                            ("stockReason int", {"stockReason": 5}),
                            ("stockReason empty member", {"stockReason": ["Progression", ""]}),
                            ("stockReason nested", {"stockReason": [["x"]]}),
                            ("kind unknown", {"kind": "bogus"}),
                            ("provenance unknown", {"provenance": "nope"}),
                            ("amountKind balance", {"amountKind": "balance"}),
                            ("repMode unknown", {"repMode": "weird"}),
                            ("rep magnitude cap", {"reputation": 1e9}),
                            ("rep null fill", {"reputation": None}),
                            ("science null fill", {"kind": "science-award", "science": None}),
                            ("facet wrong type", {"reputation": "lots"})):
            shapes.append(("unmirrored: " + label, dict(self.BASE, **over)))
        # UNBOUNDED INTEGER: tomllib does not clamp to int64, and float() on a
        # 400-digit int raises OverflowError. Both sides must REJECT, never raise -
        # an exception here escapes validate_spec, which has no try/except, and
        # takes down the whole batch instead of one spec.
        shapes.append(("huge int ut", dict(self.BASE, ut=10 ** 400)))
        shapes.append(("huge negative int ut", dict(self.BASE, ut=-(10 ** 400))))
        shapes.append(("huge int in window", dict(self.BASE, utWindow=[0.0, 10 ** 400])))
        return shapes

    def test_the_two_implementations_agree_over_the_mirrored_shape_space(self):
        for label, entry in self._mirrored_shape_space():
            with self.subTest(shape=label):
                # NEITHER side may raise: an exception escapes validate_spec (called
                # with no try/except) and aborts the whole batch rather than one spec.
                try:
                    pre = hlib.validate_ledger_expectations(
                        {"seedFrom": "template", "manifest": [entry]})
                except Exception as ex:                      # noqa: BLE001
                    self.fail("pre-launch gate RAISED %r on %s (aborts the batch)"
                              % (ex, label))
                try:
                    run = list(oracle.parse_manifest_entries([entry]).errors)
                except Exception as ex:                      # noqa: BLE001
                    self.fail("oracle RAISED %r on %s" % (ex, label))
                self.assertEqual(
                    bool(run), bool(pre),
                    "pre-launch and run-time disagree on %s: pre=%r run=%r - a "
                    "run-time-only rejection costs a full flight; a pre-launch-only "
                    "rejection refuses a spec the oracle would have accepted"
                    % (label, pre, run))

    def test_the_shared_reason_is_the_same_key_on_both_sides(self):
        # Agreeing on "reject" is not enough: an author reading the pre-launch error
        # must be pointed at the SAME key the oracle would have named, otherwise the
        # cheap gate sends them to the wrong line. Keys carry the trailing COLON:
        # ".ut" is a substring of ".utWindow", so a window-first implementation would
        # satisfy a bare ".ut" assertion and the ordering claim would go unpinned.
        for label, entry, key, forbidden in [
                ("bad ut", dict(self.BASE, ut="x"), ".ut:", ".utWindow"),
                ("bad window", dict(self.BASE, utWindow="oops"), ".utWindow:", None),
                ("bad ut beats window", dict(self.BASE, ut="x",
                                             utWindow=[1.0, 2.0]), ".ut:", ".utWindow"),
                ("seq", dict(self.BASE, seq="0"), ".seq:", None),
                ("stockReason", dict(self.BASE, stockReason=5), ".stockReason:", None)]:
            with self.subTest(shape=label):
                pre = hlib.validate_ledger_expectations(
                    {"seedFrom": "template", "manifest": [entry]})
                run = list(oracle.parse_manifest_entries([entry]).errors)
                self.assertTrue(any(key in e for e in pre), "pre-launch: %r" % pre)
                self.assertTrue(any(key in e for e in run), "run-time: %r" % run)
                if forbidden:
                    self.assertFalse(any(forbidden in e for e in pre),
                                     "wrong key blamed pre-launch: %r" % pre)

    def test_the_funds_fill_carve_out_is_the_only_accepted_oracle_rejection(self):
        # The carve-out exists because that rule reads the CAPTURED pool, which is
        # empty pre-launch, so raising it here would refuse a spec the oracle could
        # accept at run time (the unsafe direction). Pin both halves: the gate stays
        # silent, and the oracle really does reject it - so this is a deliberate
        # carve-out and not a rule that quietly stopped existing.
        entry = {"kind": "milestone", "funds": None, "provenance": "seam-declared"}
        self.assertEqual([], hlib.validate_ledger_expectations(
            {"seedFrom": "template", "manifest": [entry]}))
        run = list(oracle.parse_manifest_entries([entry]).errors)
        self.assertTrue(any("fill-from-capture is ambiguous" in e for e in run), run)

    def test_malformed_ut_now_reds_pre_launch(self):
        # THE FINDING, stated directly. Each of these used to pass ADMIT and hard-fail
        # AFTER the flight as a manifest-parse-error PARSEK-FAIL(ledger).
        for bad in (True, "x", float("nan"), float("inf")):
            with self.subTest(ut=bad):
                errs = hlib.validate_ledger_expectations(
                    {"seedFrom": "template", "manifest": [dict(self.BASE, ut=bad)]})
                self.assertTrue(any("manifest[0].ut" in e for e in errs),
                                "ut=%r must red pre-launch" % (bad,))
        # A valid ut and an absent ut both stay clean.
        self.assertEqual([], hlib.validate_ledger_expectations(
            {"seedFrom": "template", "manifest": [dict(self.BASE, ut=119.9)]}))
        self.assertEqual([], hlib.validate_ledger_expectations(
            {"seedFrom": "template", "manifest": [dict(self.BASE)]}))
        # 0.0 is a real UT, not a falsy absence - the guard must not treat it as one.
        self.assertEqual([], hlib.validate_ledger_expectations(
            {"seedFrom": "template", "manifest": [dict(self.BASE, ut=0.0)]}))

    def test_a_non_table_entry_now_reds_pre_launch_and_is_indexed(self):
        errs = hlib.validate_ledger_expectations(
            {"seedFrom": "template", "manifest": [dict(self.BASE), "not-a-table"]})
        self.assertTrue(any("manifest[1]" in e and "not a table/object" in e
                            for e in errs), errs)
        # The valid sibling is not blamed, mirroring the oracle's per-entry indexing.
        self.assertFalse(any("manifest[0]" in e for e in errs), errs)

    def test_the_key_path_is_re_prefixed_into_the_spec_namespace(self):
        # The oracle says `entry[N]`; a spec author is reading a TOML file whose path
        # is `expectations.ledger.manifest[N]`. Delegation must not leak the oracle's
        # internal namespace into a spec-authoring error.
        errs = hlib.validate_ledger_expectations(
            {"seedFrom": "template", "manifest": [dict(self.BASE, ut="x")]})
        self.assertEqual(1, len(errs), errs)
        self.assertTrue(errs[0].startswith("expectations.ledger.manifest[0].ut:"), errs)
        self.assertNotIn("entry[", errs[0])

    def test_every_committed_spec_still_passes_the_widened_gate(self):
        # THE VERDICT-NEUTRALITY CHECK for this widening: a stricter pre-launch gate
        # must not start refusing a spec that flies today. (The armed CL-2 included -
        # its three utWindow entries are the only committed windows in the tree.)
        offenders = {}
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            ledger = (load_spec(name).get("expectations", {}) or {}).get("ledger")
            if ledger is None:
                continue
            errs = hlib.validate_ledger_expectations(ledger)
            if errs:
                offenders[name] = errs
        self.assertEqual({}, offenders)


class ParseCareerSaveBlockTests(unittest.TestCase):
    """Guards the produced-save careerSave read (design verifier step 1 / edge 13):
    a present block is returned with its own hasX flags; an ABSENT block is None
    (tooling-missing, NEVER a silent pass); a {parsed:false} block is returned as-is
    (facet-absent, not tooling-missing)."""

    def test_present_block_returned(self):
        js = ('{"counts": {"failNonBaselined": 0, "staleNonBaselined": 0}, '
              '"careerSave": {"parsed": true, "hasFunds": true, "funds": 25000.0}}')
        block = hlib.parse_career_save_block(js)
        self.assertIsNotNone(block)
        self.assertTrue(block["parsed"])
        self.assertEqual(25000.0, block["funds"])

    def test_absent_block_is_none(self):
        # No careerSave key -> None (old/broken analyzer -> the caller INVALID(tooling)).
        js = '{"counts": {"failNonBaselined": 0}, "findings": []}'
        self.assertIsNone(hlib.parse_career_save_block(js))

    def test_parsed_false_block_returned_as_is(self):
        block = hlib.parse_career_save_block('{"careerSave": {"parsed": false}}')
        self.assertIsNotNone(block)
        self.assertFalse(block["parsed"])

    def test_unparseable_json_is_none(self):
        self.assertIsNone(hlib.parse_career_save_block("{not json"))

    def test_accepts_already_parsed_dict(self):
        block = hlib.parse_career_save_block({"careerSave": {"parsed": True, "hasFunds": True}})
        self.assertTrue(block["hasFunds"])


class LedgerSpecSurfaceValidationTests(unittest.TestCase):
    """Guards the [expectations.ledger] spec surface (design ~226): a malformed
    ledger block is a spec-invalid INVALID (no KSP boot). The empty-manifest B10
    block validates; a bad seedFrom / tolerances / rec3CarveOut / manifest rejects."""

    def test_empty_manifest_block_valid(self):
        self.assertEqual([], hlib.validate_ledger_expectations(
            {"seedFrom": "template", "tolerances": "default", "rec3CarveOut": False}))

    def test_defaults_when_keys_omitted_valid(self):
        self.assertEqual([], hlib.validate_ledger_expectations({}))

    def test_bad_seed_from_rejected(self):
        errs = hlib.validate_ledger_expectations({"seedFrom": "literal"})
        self.assertTrue(any("seedFrom" in e for e in errs))

    def test_bad_tolerances_rejected(self):
        errs = hlib.validate_ledger_expectations({"tolerances": "loose"})
        self.assertTrue(any("tolerances" in e for e in errs))

    def test_non_bool_rec3_rejected(self):
        errs = hlib.validate_ledger_expectations({"rec3CarveOut": "yes"})
        self.assertTrue(any("rec3CarveOut" in e for e in errs))

    def test_non_array_manifest_rejected(self):
        errs = hlib.validate_ledger_expectations({"manifest": {"k": "v"}})
        self.assertTrue(any("manifest" in e for e in errs))

    def test_wired_into_validate_spec_rejects_bad_ledger(self):
        reg = load_registry()
        spec = load_spec("B10-career-passive-safety.toml")
        spec["expectations"]["ledger"]["seedFrom"] = "literal"
        v = hlib.validate_spec(spec, reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("seedFrom" in e for e in v.errors))

    def test_b10_ledger_block_still_validates(self):
        reg = load_registry()
        spec = load_spec("B10-career-passive-safety.toml")
        v = hlib.validate_spec(spec, reg)
        self.assertTrue(v.ok, "B10 with [expectations.ledger] must still validate; errors=%s" % (v.errors,))
        # The block is present with the expected v1 surface.
        self.assertEqual("template", spec["expectations"]["ledger"]["seedFrom"])
        self.assertEqual([], spec["expectations"]["ledger"].get("manifest", []))


class WorldRosterSpecSurfaceValidationTests(unittest.TestCase):
    """Guards the [expectations.world.roster] spec surface: a malformed roster block
    is a spec-invalid INVALID (no KSP boot). Two shapes are silent no-ops without the
    gate - an unknown key (asserts nothing) and a scalar string (the oracle would
    iterate CHARACTERS and red for the wrong reason)."""

    def test_valid_lists_ok(self):
        self.assertEqual([], hlib.validate_world_roster_expectations(
            {"absent": ["Bill Kerman"],
             "present": ["Jebediah Kerman", "Bob Kerman", "Valentina Kerman"]}))

    def test_empty_block_ok(self):
        # A block declaring nothing is a no-op by design (every non-declaring spec is
        # byte-unaffected); it is not a spec ERROR.
        self.assertEqual([], hlib.validate_world_roster_expectations({}))
        self.assertEqual([], hlib.validate_world_roster_expectations(
            {"present": [], "absent": []}))

    def test_scalar_string_rejected(self):
        for key in ("present", "absent"):
            errs = hlib.validate_world_roster_expectations({key: "Bill Kerman"})
            self.assertTrue(any(("world.roster.%s" % key) in e for e in errs),
                            "a bare string must be a spec error; errs=%s" % (errs,))

    def test_unknown_key_rejected(self):
        errs = hlib.validate_world_roster_expectations({"dead": ["Bill Kerman"]})
        self.assertTrue(any("unknown key" in e for e in errs), errs)
        errs = hlib.validate_world_roster_expectations({"presnt": ["Bill Kerman"]})
        self.assertTrue(any("unknown key" in e for e in errs), errs)

    def test_non_string_and_blank_names_rejected(self):
        errs = hlib.validate_world_roster_expectations({"absent": ["Bill Kerman", "", 7]})
        self.assertEqual(2, len(errs), errs)

    def test_non_table_rejected(self):
        errs = hlib.validate_world_roster_expectations(["Bill Kerman"])
        self.assertTrue(any("must be a table" in e for e in errs), errs)

    def test_wired_into_validate_spec(self):
        reg = load_registry()
        spec = load_spec("L1-dismiss-kerbal-career.toml")
        self.assertTrue(hlib.validate_spec(spec, reg).ok,
                        "the committed L1 roster block must validate")
        spec["expectations"]["world"]["roster"]["absent"] = "Bill Kerman"
        v = hlib.validate_spec(spec, reg)
        self.assertFalse(v.ok)
        self.assertTrue(any("world.roster.absent" in e for e in v.errors), v.errors)


# NOTE: MergeDurationsTests was DELETED when the orbit branch's sample-based
# merge_durations superseded this branch's minimal "keep the richer entry"
# rule. Its seven cells encoded the OLD contract (summary-only entries,
# fresh-wins-by-n). DurationLedgerMergeTests above covers every one of those
# intents and adds the truncation double-count, watermark, idempotence,
# tail-bound and percentile-recompute cases this class never had.


class SettingsSidecarBaselineTests(unittest.TestCase):
    """The tracer-leak fix (pure half).

    THE REGRESSION. `SetSetting` on a sidecar-tracked setting persists
    INSTANCE-WIDE (GameData/Parsek/PluginData/settings.cfg) and Parsek applies
    that file OVER every loaded save, so S1.4's `mapRenderTracing=true` left the
    per-frame map/TS render tracer pinned on for every subsequent run on the
    automation instance - including multi-thousand-second flights that never
    declared it, whose anomaly sweep then gated on a tracer they did not ask for.
    Verified live: the instance's sidecar contained exactly
    `mapRenderTracing = True`.
    """

    def test_baseline_body_pins_every_tracer_off(self):
        body = hlib.render_settings_sidecar_baseline()
        values = hlib.parse_settings_sidecar(body)
        self.assertEqual(sorted(hlib.TRACER_SETTING_KEYS), sorted(values))
        for key in hlib.TRACER_SETTING_KEYS:
            self.assertEqual("False", values[key], key)

    def test_baseline_body_is_its_own_fixed_point(self):
        # Re-reading the file the harness wrote must report ZERO tracers on, so a
        # second (teardown) write never logs a phantom "cleared leaked" line.
        body = hlib.render_settings_sidecar_baseline()
        self.assertEqual([], hlib.settings_sidecar_tracers_on(body))

    def test_baseline_writes_no_key_the_mod_does_not_read(self):
        # The two OTHER sidecar-tracked settings must stay UNSET so the fixture's
        # own GameParameters keep governing them (a stored value would override
        # every save on the instance, which is the bug being fixed). The 2026-08-27
        # settings simplification shrank this residue from five keys to these two.
        values = hlib.parse_settings_sidecar(hlib.render_settings_sidecar_baseline())
        for key in ("writeReadableSidecarMirrors", "showRouteLines"):
            self.assertNotIn(key, values, key)
        # The three keys RETIRED by that simplification are no longer read by the
        # mod at all; a baseline that started writing one would be pure noise that
        # masks a future authoring mistake, so pin their absence separately.
        for key in ("autoBackupExistingSaves", "showCommittedFutureOverlays",
                    "blockCommittedActions"):
            self.assertNotIn(key, values, key)

    def test_leaked_tracer_is_detected_in_the_real_leaked_shape(self):
        # The EXACT body observed on the live automation instance after S1.4.
        self.assertEqual(["mapRenderTracing"],
                         hlib.settings_sidecar_tracers_on("mapRenderTracing = True\n"))

    def test_all_three_tracers_reported_in_declaration_order(self):
        body = ("ledgerTracing = True\nmapRenderTracing = true\n"
                "ghostRenderTracing = True\n")
        self.assertEqual(["ghostRenderTracing", "mapRenderTracing", "ledgerTracing"],
                         hlib.settings_sidecar_tracers_on(body))

    def test_absent_or_false_or_garbage_is_not_on(self):
        for body in (None, "", "mapRenderTracing = False\n",
                     "mapRenderTracing = \n", "mapRenderTracing\n",
                     "// mapRenderTracing = True\n", "mapRenderTracing = yes\n"):
            self.assertEqual([], hlib.settings_sidecar_tracers_on(body), repr(body))

    def test_parser_tolerates_config_flavoured_wrapper_lines(self):
        # A hand-written file may carry the node-name wrapper even though
        # ConfigNode.Save does not; the parser must still find the values.
        body = "PARSEK_SETTINGS\n{\n\tmapRenderTracing = True\n}\n"
        self.assertEqual(["mapRenderTracing"], hlib.settings_sidecar_tracers_on(body))

    def test_relpath_matches_the_mod_side_location(self):
        # ParsekSettingsPersistence.GetFilePath: GameData/Parsek/PluginData/settings.cfg
        self.assertEqual(("GameData", "Parsek", "PluginData", "settings.cfg"),
                         hlib.SETTINGS_SIDECAR_RELPATH)


# The three gating lines TRANSCRIBED VERBATIM from the first S1.6 flight
# (2026-07-26, run 2026-07-26_0950_S1.6-render-parity, verdict PASS). These are
# not hand-rendered from the C# format strings any more - they are what KSP
# actually wrote - so a format-string edit that breaks the contract is caught
# here rather than on the next flight.
MEASURED_PARITY_SAMPLER_LINE = (
    "[Parsek][INFO][TestRunner] ParitySampler_CapturesHandComputedOrbitGeometry:"
    " pid=2905038605 sma=800000 ecc=0.00 iconR=800000 orbitAtLiveR=800000"
    " iconOnLineDelta=0m refDelta=0.0m")
MEASURED_LOOP_SHIFT_LINE = (
    "[Parsek][INFO][TestRunner]"
    " SynthesizedParity_LoopShiftedGhost_PhaseMatched_ZeroDrift:"
    " pid=2279670454 loopShift=1100.0 matchedDev=0m mismatchedDev=1049421m"
    " tol=1927m")
MEASURED_BATCH_LINE = (
    "[Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=25 passed=14 failed=0"
    " skipped=11 category=GhostMap scene=FLIGHT")
# The runner's own accounting for 9 of the 11 skips (the other 2 are documented
# self-skips from the loop-icon warp cluster; see the spec header).
MEASURED_SCENE_SKIP_LINE = (
    "[Parsek][INFO][TestRunner] Scene eligibility skip summary: skipped=9"
    " currentScene=FLIGHT byRequiredScene=TRACKSTATION:9")


class RenderParityScenarioTests(unittest.TestCase):
    """S1.6-render-parity's ANTI-VACUITY conjunct, pinned.

    The whole point of that scenario is that a green anomaly sweep over ZERO
    parity samples must NOT read as a pass: S1.4's archived result showed
    `anomalySweep: {hits: [], status: PASS}` on a run where every probe frame
    logged `ghosts=0 sampled=0`. These cells fail if the conjunct is weakened or
    silently dropped, which is exactly how such a guard rots.
    """

    def setUp(self):
        self.spec = load_spec("S1.6-render-parity.toml")
        self.required = self.spec["expectations"]["logContracts"]["required"]
        self.forbidden = self.spec["expectations"]["logContracts"]["forbidden"]

    def test_spec_validates(self):
        v = hlib.validate_spec(self.spec, load_registry())
        self.assertTrue(v.ok, "S1.6 must validate; errors=%s" % (v.errors,))

    def test_drives_exactly_one_runtests_batch(self):
        # run.py's _driven_category returns the FIRST RunTests category, so a
        # second batch would not be gated by batchComplete at all.
        steps = self.spec["driver"]["steps"]
        run_tests = [s for s in steps if s.get("cmd") == "RunTests"]
        self.assertEqual(1, len(run_tests))
        self.assertEqual("GhostMap", run_tests[0]["args"]["category"])
        self.assertEqual("GhostMap", run._driven_category(self.spec))

    def test_pins_the_map_render_tracer_on(self):
        # Every MapRenderTrace emit the anomaly sweep greps for early-returns on
        # MapRenderTrace.IsEnabled; without this SetSetting the sweep is inert.
        tracer = [s for s in self.spec["driver"]["steps"]
                  if s.get("cmd") == "SetSetting"
                  and s.get("args", {}).get("name") == "mapRenderTracing"]
        self.assertEqual(1, len(tracer))
        self.assertEqual("true", tracer[0]["args"]["value"])

    def test_anti_vacuity_pins_the_measured_tally(self):
        # An InGameAssert.Skip is not a failure, so an all-skip batch reads
        # failed=0. This conjunct was a structural `passed=[1-9][0-9]*` until the
        # first flight (2026-07-26) measured the real split; it now pins that line
        # verbatim, so it also reds when the pass/skip split drifts silently.
        pat = next((p for p in self.required if "passed=" in p), None)
        self.assertIsNotNone(pat, "the structural anti-vacuity conjunct is gone")
        self.assertIsNotNone(re.search(pat, MEASURED_BATCH_LINE),
                             "the pinned tally must match the flown line")
        for bad, why in (
                ("BATCH_COMPLETE v1 total=25 passed=0 failed=0 skipped=25 "
                 "category=GhostMap scene=FLIGHT", "an all-skip batch"),
                ("BATCH_COMPLETE v1 total=25 passed=13 failed=0 skipped=12 "
                 "category=GhostMap scene=FLIGHT", "a test that flipped to Skip"),
                ("BATCH_COMPLETE v1 total=26 passed=15 failed=0 skipped=11 "
                 "category=GhostMap scene=FLIGHT", "a newly added GhostMap test"),
                ("BATCH_COMPLETE v1 total=25 passed=14 failed=0 skipped=11 "
                 "category=GhostMap scene=TRACKSTATION", "a FLIGHT/TS host change")):
            self.assertIsNone(re.search(pat, bad), "%s must NOT match" % (why,))

    def test_anti_vacuity_requires_the_two_measurement_lines(self):
        # These are emitted by RenderParitySamplerFixtureTest ONLY after every Skip
        # precondition passed and a real diff ran on live ghost geometry. The
        # second is unreachable unless the phase-MISMATCHED negative control also
        # flagged drift, so it is the anti-tautology proof, not a "we ran" marker.
        joined = "\n".join(self.required)
        self.assertIn("ParitySampler_CapturesHandComputedOrbitGeometry", joined)
        self.assertIn("SynthesizedParity_LoopShiftedGhost_PhaseMatched_ZeroDrift", joined)

    def test_required_patterns_match_the_flown_log(self):
        # The FLOWN lines, verbatim (see the module constants above) - not lines
        # hand-rendered from the C# format strings. Every required pattern must
        # match this log and no forbidden pattern may.
        log = "\n".join((MEASURED_PARITY_SAMPLER_LINE, MEASURED_LOOP_SHIFT_LINE,
                         MEASURED_SCENE_SKIP_LINE, MEASURED_BATCH_LINE)) + "\n"
        for pat in self.required:
            self.assertIsNotNone(re.search(pat, log),
                                 "required pattern never matches the flown line: %s" % pat)
        for pat in self.forbidden:
            self.assertIsNone(re.search(pat, log),
                              "forbidden pattern matches a CLEAN log: %s" % pat)

    def test_the_negative_control_arm_is_what_makes_the_second_line_load_bearing(self):
        # SynthesizedParity_LoopShiftedGhost_PhaseMatched_ZeroDrift emits only if
        # BOTH arms measured: the phase-matched one within tolerance AND the raw
        # epoch control OVER it. The flown line shows matchedDev=0m against
        # mismatchedDev=1049421m at tol=1927m, so the mismatched arm is ~545x the
        # tolerance - the comparison provably can still fail, which is what stops
        # the zero-drift assertion being a circle compared with itself.
        pat = next(p for p in self.required if "LoopShiftedGhost" in p)
        self.assertIsNotNone(re.search(pat, MEASURED_LOOP_SHIFT_LINE))
        # A line whose control ALSO read zero must not satisfy the contract's
        # intent; pin the flown numbers so a future re-pin cannot quietly drop it.
        self.assertIn("mismatchedDev=1049421m", MEASURED_LOOP_SHIFT_LINE)
        self.assertIn("tol=1927m", MEASURED_LOOP_SHIFT_LINE)

    def test_the_skipped_count_is_fully_accounted_for(self):
        # 11 skips = 9 scene-eligibility (TRACKSTATION tests filtered out of a
        # FLIGHT batch) + 2 documented loop-icon self-skips. If a future flight
        # moves `skipped`, the pinned tally reds and this accounting is the thing
        # that has to be re-derived alongside it.
        self.assertIn("skipped=11", MEASURED_BATCH_LINE)
        self.assertIn("skipped=9", MEASURED_SCENE_SKIP_LINE)
        self.assertIn("byRequiredScene=TRACKSTATION:9", MEASURED_SCENE_SKIP_LINE)
        header = open(os.path.join(SCENARIOS_DIR, "S1.6-render-parity.toml"),
                      encoding="utf-8").read()
        for name in ("StateVectorReseed_CalculatePhysicsStatsResnapsIconOntoConic",
                     "FreshLoopGhostIcon_OnRecordedPhaseAtCreation"):
            self.assertIn(name, header,
                          "the spec header must NAME the non-scene skip %s" % (name,))

    def test_forbidden_over_tolerance_catches_a_rate_limited_drift(self):
        # MapRenderProbe increments faithfulParityOverCount BEFORE the per-pid rate
        # limit, so a throttled drift still shows here while the anomaly sweep sees
        # no parity-drift token at all.
        drifted = ("[Parsek][VERBOSE][MapRenderTrace] faithful-parity summary"
                   " sampled=3 overTolerance=1 skip.no-covering-segment=2\n")
        clean = ("[Parsek][VERBOSE][MapRenderTrace] faithful-parity summary"
                 " sampled=3 overTolerance=0 skip.no-covering-segment=2\n")
        pat = next(p for p in self.forbidden if "overTolerance" in p)
        self.assertIsNotNone(re.search(pat, drifted))
        self.assertIsNone(re.search(pat, clean))

    def test_tolerates_no_anomaly_and_reuses_the_s14_fixture_verbatim(self):
        self.assertEqual([], self.spec["expectations"]["allowedAnomalies"])
        s14 = load_spec("S1.4-injected-playback.toml")
        self.assertEqual(s14["fixture"]["saveTemplate"],
                         self.spec["fixture"]["saveTemplate"])
        self.assertEqual(s14["fixture"]["injectedRecordings"],
                         self.spec["fixture"]["injectedRecordings"])
        self.assertEqual(s14["expectations"]["recordings"]["count"],
                         self.spec["expectations"]["recordings"]["count"])

    def test_allowed_anomalies_is_declared_where_run_py_reads_it(self):
        # The MISPLACED-KEY trap every other committed spec fell into until
        # 2026-07-26: a bare `allowedAnomalies` after the
        # [expectations.logContracts] header is TOML-scoped to that sub-table, and
        # run.py reads expectations.allowedAnomalies - so the declaration is
        # silently ignored. validate_spec now rejects it outright.
        exp = self.spec["expectations"]
        self.assertIn("allowedAnomalies", exp,
                      "allowedAnomalies must sit in [expectations] to bind")
        self.assertNotIn("allowedAnomalies", exp.get("logContracts", {}),
                         "allowedAnomalies leaked into [expectations.logContracts]")
        v = hlib.validate_spec(self.spec, load_registry())
        self.assertFalse([m for m in v.errors + v.warnings if "allowedAnomalies" in m],
                         "S1.6 must not trip the misplaced-key guard: %s"
                         % (v.errors + v.warnings,))


# The gating lines TRANSCRIBED VERBATIM from S1.7's archived first-flight log
# (2026-07-26, run 2026-07-26_1015; run 2026-07-26_1021 on the fixed anomaly sweep
# is the green flight and its expectations verifier matched every pattern below).
S17_MEASURED_KERBIN_LINE = (
    "[Parsek][INFO][TestRunner] MultiBodyConcurrent Kerbin: pid=2361972787"
    " body=Kerbin sampled=True skip=(none) hasMeas=True maxDev=0.0m tol=1989.4m"
    " over=False")
S17_MEASURED_MUN_LINE = (
    "[Parsek][INFO][TestRunner] MultiBodyConcurrent Mun: pid=1693297703"
    " body=Mun sampled=True skip=(none) hasMeas=True maxDev=0.0m tol=955.8m"
    " over=False")
S17_MEASURED_REAIM_LINE = (
    "[Parsek][INFO][TestRunner] ReaimedLoop_SynthOracle: pid=3494681962"
    " recordedLAN=0 reaimedLAN=70 | synthDev=0m synthTol=2726m (ZERO) |"
    " faithfulDev=1319093m faithfulTol=2701m (FLAGGED)")
S17_MEASURED_BATCH_LINE = (
    "[Parsek][INFO][TestRunner] BATCH_COMPLETE v1 total=22 passed=21 failed=0"
    " skipped=1 category=MapRender scene=FLIGHT")
# The runner's accounting for the single skip.
S17_MEASURED_BATCH_SKIP_LINE = (
    "[Parsek][INFO][TestRunner] Batch execution skipped 1 single-run-only test(s)")


class MapRenderParityScenarioTests(unittest.TestCase):
    """S1.7-maprender-parity: the same anti-vacuity discipline as S1.6 over the
    STRONGER category, with the sink trap accounted for.

    The obvious anti-vacuity candidate - the three-oracle flag-on baselines - is
    UNUSABLE: `ParsekLog.Write` returns right after calling `TestSinkForTesting`
    instead of teeing, and `FlagOnParityBaselineInGameTest` emits its
    `FlagOnBaseline_AllThreeModes` line inside the try whose finally restores the
    sink, so it never reaches KSP.log. These cells pin the arms that DO survive.
    """

    def setUp(self):
        self.spec = load_spec("S1.7-maprender-parity.toml")
        self.required = self.spec["expectations"]["logContracts"]["required"]
        self.forbidden = self.spec["expectations"]["logContracts"]["forbidden"]

    def test_spec_validates(self):
        v = hlib.validate_spec(self.spec, load_registry())
        self.assertTrue(v.ok, "S1.7 must validate; errors=%s" % (v.errors,))
        self.assertEqual([], list(v.warnings), "S1.7 must validate warning-free")

    def test_drives_exactly_one_maprender_batch(self):
        steps = self.spec["driver"]["steps"]
        run_tests = [s for s in steps if s.get("cmd") == "RunTests"]
        self.assertEqual(1, len(run_tests))
        self.assertEqual("MapRender", run_tests[0]["args"]["category"])
        self.assertEqual("MapRender", run._driven_category(self.spec))

    def test_pins_the_map_render_tracer_on(self):
        tracer = [s for s in self.spec["driver"]["steps"]
                  if s.get("cmd") == "SetSetting"
                  and s.get("args", {}).get("name") == "mapRenderTracing"]
        self.assertEqual(1, len(tracer))
        self.assertEqual("true", tracer[0]["args"]["value"])

    def test_required_patterns_match_the_flown_log(self):
        log = "\n".join((S17_MEASURED_KERBIN_LINE, S17_MEASURED_MUN_LINE,
                         S17_MEASURED_REAIM_LINE, S17_MEASURED_BATCH_SKIP_LINE,
                         S17_MEASURED_BATCH_LINE)) + "\n"
        for pat in self.required:
            self.assertIsNotNone(re.search(pat, log),
                                 "required pattern never matches the flown line: %s" % pat)
        for pat in self.forbidden:
            self.assertIsNone(re.search(pat, log),
                              "forbidden pattern matches a CLEAN log: %s" % pat)

    def test_the_pinned_tally_reds_on_any_drift(self):
        pat = next(p for p in self.required if "passed=" in p)
        self.assertIsNotNone(re.search(pat, S17_MEASURED_BATCH_LINE))
        for bad, why in (
                ("BATCH_COMPLETE v1 total=22 passed=0 failed=0 skipped=22 "
                 "category=MapRender scene=FLIGHT", "an all-skip batch"),
                ("BATCH_COMPLETE v1 total=22 passed=20 failed=0 skipped=2 "
                 "category=MapRender scene=FLIGHT", "a test that flipped to Skip"),
                ("BATCH_COMPLETE v1 total=23 passed=22 failed=0 skipped=1 "
                 "category=MapRender scene=FLIGHT", "a newly added MapRender test"),
                ("BATCH_COMPLETE v1 total=22 passed=21 failed=0 skipped=1 "
                 "category=MapRender scene=TRACKSTATION", "a FLIGHT/TS host change")):
            self.assertIsNone(re.search(pat, bad), "%s must NOT match" % (why,))

    def test_both_multibody_arms_are_pinned_and_a_skip_cannot_satisfy_them(self):
        # sampled=True / skip=(none) / hasMeas=True / over=False are all pinned, so
        # neither a precondition skip, a blind lens nor drift can satisfy the
        # contract. The MUN arm is the cross-body-leak proof: parity resolves in
        # each ghost's OWN body frame and ComputeFaithfulOrbitParity skips with
        # body-mismatch when the rendered body differs, so a leak reads
        # sampled=False.
        for body in ("Kerbin", "Mun"):
            pat = next((p for p in self.required
                        if "MultiBodyConcurrent " + body in p), None)
            self.assertIsNotNone(pat, "the %s multi-body arm is not pinned" % (body,))
            good = (S17_MEASURED_KERBIN_LINE if body == "Kerbin"
                    else S17_MEASURED_MUN_LINE)
            self.assertIsNotNone(re.search(pat, good))
            for repl, why in ((("sampled=True", "sampled=False"), "a skipped sample"),
                              (("hasMeas=True", "hasMeas=False"), "a blind lens"),
                              (("over=False", "over=True"), "measured drift")):
                self.assertIsNone(re.search(pat, good.replace(*repl)),
                                  "%s must NOT satisfy the %s arm" % (why, body))

    def test_the_two_multibody_arms_resolved_in_different_body_frames(self):
        # Distinct pids and distinct scale-derived tolerances are themselves the
        # evidence that two separate ghosts resolved against two separate bodies.
        self.assertIn("pid=2361972787", S17_MEASURED_KERBIN_LINE)
        self.assertIn("pid=1693297703", S17_MEASURED_MUN_LINE)
        self.assertIn("tol=1989.4m", S17_MEASURED_KERBIN_LINE)
        self.assertIn("tol=955.8m", S17_MEASURED_MUN_LINE)

    def test_the_reaim_arm_carries_the_load_bearing_negative_control(self):
        # ReaimedLoop_SynthOracle emits only AFTER the FAITHFUL block asserted
        # OverTolerance TRUE on the same rendered conic the SYNTHESIZED lens read as
        # zero, so the line is unreachable unless the two lenses genuinely disagree.
        # 1319093 m against a 2701 m tolerance is ~488x over.
        pat = next(p for p in self.required if "ReaimedLoop_SynthOracle" in p)
        self.assertIsNotNone(re.search(pat, S17_MEASURED_REAIM_LINE))
        self.assertIn("(ZERO)", S17_MEASURED_REAIM_LINE)
        self.assertIn("(FLAGGED)", S17_MEASURED_REAIM_LINE)
        self.assertIn("faithfulDev=1319093m", S17_MEASURED_REAIM_LINE)
        self.assertIn("faithfulTol=2701m", S17_MEASURED_REAIM_LINE)

    def test_the_sink_trap_is_documented_and_the_sinking_arms_are_not_pinned(self):
        # A future author's first instinct is the three-oracle flag-on baseline.
        # The header must say why it cannot be used, and the contract must not
        # reference it (requiring it would red every flight).
        header = open(os.path.join(SCENARIOS_DIR, "S1.7-maprender-parity.toml"),
                      encoding="utf-8").read()
        self.assertIn("TestSinkForTesting", header)
        self.assertIn("FlagOnParityBaselineInGameTest", header)
        for pat in self.required + self.forbidden:
            self.assertNotIn("FlagOnBaseline", pat,
                             "a sink-diverted line can never satisfy a contract")

    def test_reuses_the_s16_fixture_verbatim(self):
        s16 = load_spec("S1.6-render-parity.toml")
        for key in ("saveTemplate", "injectedRecordings"):
            self.assertEqual(s16["fixture"][key], self.spec["fixture"][key])
        self.assertEqual(s16["expectations"]["recordings"]["count"],
                         self.spec["expectations"]["recordings"]["count"])
        self.assertEqual([], self.spec["expectations"]["allowedAnomalies"])

    def test_claims_no_new_registry_value(self):
        # S1.7's value is DEPTH on an axis S1.6 already opened, not breadth.
        # Inventing a token so the coverage ledger grows would be the same vacuity
        # this scenario family exists to prevent.
        s16 = load_spec("S1.6-render-parity.toml")
        s16_vals = {(d, v) for d, vs in s16["dimensionsCovered"].items() for v in vs}
        s17_vals = {(d, v) for d, vs in self.spec["dimensionsCovered"].items() for v in vs}
        self.assertEqual(set(), s17_vals - s16_vals,
                         "S1.7 must not claim a dimension value S1.6 does not")


class MisplacedAllowedAnomaliesRejectionTests(unittest.TestCase):
    """Found 2026-07-26 while wiring S1.6: EVERY committed spec wrote
    `allowedAnomalies` after the `[expectations.logContracts]` header, which TOML
    scopes to `expectations.logContracts.allowedAnomalies` - a key run.py never
    reads (it reads `expectations.allowedAnomalies`). Inert for the 27 specs
    declaring `[]`, but it had made S1.4's declared polyline-orbit-overlap
    exception silently INERT since its first commit.

    It shipped as a WARN for one commit only, because rejecting would have
    invalidated the whole committed set. All 28 were relocated in the change that
    promoted this to an ERROR, so the gate is now hard: nothing FAILS on a
    warning, and the failure mode this guards against is precisely a spec author
    believing a declared tolerance applies when it does not."""

    def _spec_with(self, block):
        spec = load_spec("S1.6-render-parity.toml")
        spec["expectations"] = block
        return spec

    def test_misplaced_empty_list_is_rejected(self):
        # Even the inert `[]` form is rejected: it is the template a future author
        # copies, and the sub-table it lands in depends on the preceding header.
        spec = self._spec_with({"logContracts": {
            "required": ["BATCH_COMPLETE v1 .* failed=0"], "allowedAnomalies": []}})
        v = hlib.validate_spec(spec, load_registry())
        self.assertFalse(v.ok, "a misplaced key must REJECT, not merely warn")
        hit = [e for e in v.errors if "allowedAnomalies" in e]
        self.assertEqual(1, len(hit))
        self.assertIn("[expectations.logContracts]", hit[0])

    def test_the_error_names_the_exact_required_placement(self):
        # This error is what every in-flight sibling PR that ADDS a spec will hit
        # the moment this lands (they all write the key under
        # [expectations.logContracts]), so the message has to carry the fix, not
        # just the diagnosis: the literal table, the literal key line, and the
        # ordering requirement relative to the sub-tables.
        spec = self._spec_with({"logContracts": {
            "required": ["BATCH_COMPLETE v1 .* failed=0"], "allowedAnomalies": []}})
        msg = [e for e in hlib.validate_spec(spec, load_registry()).errors
               if "allowedAnomalies" in e][0]
        self.assertIn("[expectations]", msg)
        self.assertIn("allowedAnomalies = []", msg)
        self.assertIn("BEFORE", msg)

    def test_misplaced_nonempty_list_names_the_inert_exceptions(self):
        spec = self._spec_with({"logContracts": {
            "required": ["BATCH_COMPLETE v1 .* failed=0"],
            "allowedAnomalies": ["polyline-orbit-overlap"]}})
        v = hlib.validate_spec(spec, load_registry())
        self.assertFalse(v.ok)
        hit = [e for e in v.errors if "allowedAnomalies" in e]
        self.assertEqual(1, len(hit))
        self.assertIn("polyline-orbit-overlap", hit[0])
        self.assertIn("INERT", hit[0])

    def test_any_expectations_subtable_is_checked_not_just_log_contracts(self):
        # The key binds to whichever sub-table header precedes it, so the trap
        # re-arms as specs grow new [expectations.<sub>] blocks. Checking only
        # logContracts would let the next one through.
        spec = self._spec_with({
            "recordings": {"count": {"min": 0, "max": 0}, "allowedAnomalies": []},
            "logContracts": {"required": ["BATCH_COMPLETE v1 .* failed=0"]}})
        v = hlib.validate_spec(spec, load_registry())
        self.assertFalse(v.ok)
        self.assertTrue(any("expectations.recordings.allowedAnomalies" in e
                            for e in v.errors), v.errors)

    def test_correct_placement_is_silent(self):
        spec = self._spec_with({
            "allowedAnomalies": ["polyline-orbit-overlap"],
            "logContracts": {"required": ["BATCH_COMPLETE v1 .* failed=0"]}})
        v = hlib.validate_spec(spec, load_registry())
        self.assertEqual([], [m for m in v.errors + v.warnings
                              if "allowedAnomalies" in m])

    def test_no_committed_spec_still_carries_the_misplaced_key(self):
        # The relocation is complete, and every committed spec declares the key
        # where run.py reads it. A spec that regresses fails validate_spec now,
        # but this cell names the offender instead of just reddening a run.
        tripped, undeclared = [], []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            exp = load_spec(name).get("expectations", {}) or {}
            for sub_name, sub in exp.items():
                if isinstance(sub, dict) and "allowedAnomalies" in sub:
                    tripped.append("%s -> expectations.%s" % (name, sub_name))
            if "allowedAnomalies" not in exp:
                undeclared.append(name)
        self.assertEqual([], tripped, "misplaced allowedAnomalies still committed")
        self.assertEqual([], undeclared,
                         "every spec declares allowedAnomalies where it binds")

    def test_s14_kept_the_gate_strength_it_actually_flew_with(self):
        # S1.4's polyline-orbit-overlap exception was never in force, so its green
        # runs prove the overlap did not OCCUR, not that it was tolerated.
        # Relocating it as-declared would have widened the gate on zero evidence;
        # it is relocated as [] instead, and the history is recorded in the spec.
        s14 = load_spec("S1.4-injected-playback.toml")
        self.assertEqual([], s14["expectations"]["allowedAnomalies"])
        body = open(os.path.join(SCENARIOS_DIR, "S1.4-injected-playback.toml"),
                    encoding="utf-8").read()
        self.assertIn("polyline-orbit-overlap", body,
                      "the dead exception must stay documented, not vanish")
        self.assertIn("NEVER", body)

    def test_every_committed_spec_parses_under_the_budget_surface(self):
        # BACKWARD-COMPATIBILITY GATE for the `{ token, maxCount }` form (2026-07-29).
        # The budget entry is ADDITIVE: an entry is either a bare token (what all 55
        # committed specs declare) or a table. Every committed declaration must still
        # parse with ZERO errors and produce an UNBUDGETED (None) ceiling, so the new
        # surface cannot have changed a single scenario's tolerance.
        names = sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml"))
        self.assertTrue(names)
        for name in names:
            with self.subTest(spec=name):
                declared = (load_spec(name).get("expectations", {}) or {}).get(
                    "allowedAnomalies", [])
                parsed = hlib.parse_allowed_anomalies(declared)
                self.assertEqual([], list(parsed.errors),
                                 "%s: %s" % (name, list(parsed.errors)))
                self.assertEqual([], list(parsed.warnings),
                                 "%s: %s" % (name, list(parsed.warnings)))
                for token, budget in parsed.budgets.items():
                    self.assertIsNone(budget,
                                      "%s arms a count budget on %s; no committed spec "
                                      "may, until an operator calibrates one"
                                      % (name, token))

    def test_no_committed_spec_arms_a_count_budget(self):
        # The HARD SAFETY PROPERTY, stated once at the whole-set level: the budget
        # mechanism ships INERT. Arming one is an operator decision taken against
        # measured `anomalySweep.hitCounts` from a green run.
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            declared = (load_spec(name).get("expectations", {}) or {}).get(
                "allowedAnomalies", [])
            if any(isinstance(e, dict) for e in declared):
                armed.append(name)
        self.assertEqual([], armed)


class LogContractPatternCompilabilityTests(unittest.TestCase):
    """`logContracts` patterns are REGEXES, and the natural way to write a KSP.log
    token containing metacharacters is the broken way.

    Found 2026-07-27 authoring the R1 debris gate. The background emit is
    `Child recording created (debris, TTL=60s): recId=...`, and pasting it
    verbatim into `required` yields `re.error: missing ), unterminated subpattern`.
    `evaluate_expectations` already CATCHES that and turns it into a mismatch, so
    nothing crashes - but it runs on the COLLECTED log, i.e. after the flight. On
    B13 that is its measured 2,825 s p50 spent to learn the author mistyped a
    pattern, and the red reads `invalid regex` rather than naming a Parsek defect.
    `validate_spec` rejects it, and `run.py --dry-run` now runs that validation
    (it used to return 0 before reaching it - see the dry-run cell below)."""

    def _spec_with_patterns(self, facet, patterns):
        spec = load_spec("B2-lko-ascent.toml")
        spec["expectations"]["logContracts"][facet] = patterns
        return spec

    def test_unescaped_paren_in_required_is_rejected(self):
        spec = self._spec_with_patterns(
            "required", ["Recording started", "Child recording created (debris, TTL="])
        v = hlib.validate_spec(spec, load_registry())
        self.assertFalse(v.ok, "an uncompilable required pattern must REJECT")
        hit = [e for e in v.errors if "not a valid regex" in e]
        self.assertEqual(1, len(hit))
        self.assertIn("Child recording created (debris, TTL=", hit[0])

    def test_the_error_carries_the_fix_not_just_the_diagnosis(self):
        spec = self._spec_with_patterns(
            "required", ["Child recording created (debris, TTL="])
        msg = [e for e in hlib.validate_spec(spec, load_registry()).errors
               if "not a valid regex" in e][0]
        self.assertIn("re.search", msg)              # WHY it is a regex at all
        self.assertIn(r'"\\("', msg)                 # the literal escape to write
        self.assertIn("TOML basic", msg)             # ...and in WHICH string kind
        self.assertIn("unterminated subpattern", msg)

    def test_forbidden_patterns_are_checked_too(self):
        # NOTE the reason is NOT "a broken forbidden pattern silently passes" -
        # evaluate_expectations has an explicit `except re.error` that reds it
        # (verified below). The reason is the same as for `required`: it reds
        # AFTER the flight instead of before it.
        spec = self._spec_with_patterns("forbidden", ["\\[Parsek\\]\\[ERROR\\](unclosed"])
        v = hlib.validate_spec(spec, load_registry())
        self.assertFalse(v.ok)
        self.assertTrue(any("logContracts.forbidden" in e and "not a valid regex" in e
                            for e in v.errors))
        broken = {"logContracts": {"forbidden": ["\\[Parsek\\](unclosed"]}}
        self.assertEqual("FAIL", hlib.evaluate_expectations(broken, 1, "x").status)

    def test_committed_metacharacter_patterns_still_validate(self):
        # The gate must not punish CORRECT escaping. These are committed,
        # load-bearing and deliberately full of metacharacters.
        spec = self._spec_with_patterns("required", [
            r"CommitTreeFlight terminal: rec=\w+ terminalState=Destroyed terminalOrbitBody=\(null\)",
            r"Environment transition: (Approach|ExoBallistic|ExoPropulsive) -> Surface(Stationary|Mobile) at UT=",
            r"Materialize: 1 dormant route\(s\) materialized",
        ])
        self.assertTrue(hlib.validate_spec(spec, load_registry()).ok)

    def test_type_guards_reject_malformed_facets(self):
        # Both isinstance guards were uncovered on the first cut: deleting either
        # left the whole 740-cell suite green.
        for bad, needle in ((["ok", 5], "must be a string"),
                            ({"a": 1}, "must be a list")):
            with self.subTest(bad=bad):
                spec = self._spec_with_patterns("forbidden", bad)
                v = hlib.validate_spec(spec, load_registry())
                self.assertFalse(v.ok)
                self.assertTrue(any(needle in e for e in v.errors), v.errors)

    def test_an_absent_facet_is_legal(self):
        # Guards the `if patterns is None: continue` branch. Deleting it would
        # reject every spec that omits `forbidden` - all 38 happen to declare
        # both, so nothing else notices.
        spec = load_spec("B2-lko-ascent.toml")
        del spec["expectations"]["logContracts"]["forbidden"]
        self.assertTrue(hlib.validate_spec(spec, load_registry()).ok)
        spec["expectations"]["logContracts"]["required"] = []
        self.assertTrue(hlib.validate_spec(spec, load_registry()).ok)

    def test_every_committed_spec_validates_through_the_real_gate(self):
        # Drives hlib.validate_spec rather than re-implementing re.compile. NOTE
        # what this does and does not do: it is a SWEEP, not a gate-detector.
        # Deleting the production gate leaves it GREEN, because all 38 committed
        # specs are valid either way - the cells above are what red on deletion.
        # The first cut's comment claimed otherwise; that over-claim is the same
        # class of error that let the wrong token ship, so it is corrected rather
        # than left for the next reader to trust.
        registry = load_registry()
        broken = []
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            for err in hlib.validate_spec(load_spec(name), registry).errors:
                if "not a valid regex" in err or "logContracts" in err:
                    broken.append("%s: %s" % (name, err))
        self.assertEqual([], broken)

    def _dry_run_exit_code(self, mutate=None):
        """Drive the REAL `run.run(... --dry-run)` over a scratch scenarios dir.

        In-process and launches nothing (the dry-run branch returns before any
        instance is resolved), so this is a behavioural assertion rather than a
        grep over the function's source. It has to be: the first cut of this cell
        searched `inspect.getsource` for the substrings "validate_spec" and
        "return 1", and a reviewer defeated it twice - once with
        `if False and dry_errors:` and once by deleting the block and leaving a
        TODO comment naming both substrings. Both restored the original defect
        and both stayed green.
        """
        with tempfile.TemporaryDirectory() as tmp:
            for name in os.listdir(SCENARIOS_DIR):
                if name.endswith(".toml"):
                    shutil.copy(os.path.join(SCENARIOS_DIR, name), os.path.join(tmp, name))
            target = os.path.join(tmp, "B2-lko-ascent.toml")
            if mutate is not None:
                with open(target, encoding="utf-8") as fh:
                    body = fh.read()
                with open(target, "w", encoding="utf-8") as fh:
                    fh.write(mutate(body))
            # Also redirect the harness log, which run.run() mints per invocation:
            # without this every suite run drops another
            # harness/results/<ts>_harness.log on disk, unbounded. And swallow the
            # plan print, which is 2 full ACTION PLAN blocks of unittest noise.
            original_dir = run.SCENARIOS_DIR
            original_log = run.default_harness_log_path
            run.SCENARIOS_DIR = tmp
            run.default_harness_log_path = lambda: os.path.join(tmp, "harness.log")
            try:
                with contextlib.redirect_stdout(io.StringIO()):
                    return run.run(["--id", "B2-lko-ascent", "--dry-run", "--no-coverage"])
            finally:
                # NOTE these are process-global mutations, safe only because
                # stdlib unittest runs sequentially. A future parallel runner
                # would need this helper reworked, not just re-ordered.
                run.SCENARIOS_DIR = original_dir
                run.default_harness_log_path = original_log

    def test_dry_run_exits_zero_on_a_valid_spec(self):
        self.assertEqual(0, self._dry_run_exit_code())

    def test_dry_run_exits_nonzero_on_an_uncompilable_pattern(self):
        # The behaviour finding 2 of the review was about: --dry-run used to
        # `return 0` straight after printing the plan, so a spec validate_spec
        # rejects rendered a clean plan and exit 0 - while the in-code advice
        # pointed authors at exactly that command.
        def break_pattern(body):
            broken = body.replace(
                '"ProcessBreakupEvent: debris child created"',
                '"Child recording created (debris, TTL="')
            # Fail LOUD if the spec's token is reworded: a no-op replace would
            # leave the spec valid and this cell would assert 1 == 0 with no clue
            # why. The point is to feed validate_spec an UNCOMPILABLE pattern.
            assert broken != body, "B2's debris token moved; update this mutation"
            return broken
        self.assertEqual(1, self._dry_run_exit_code(break_pattern))


class DebrisPopulationGateTests(unittest.TestCase):
    """R1: the five Kerbal X flights that produced a parent-anchored debris
    population on every run and asserted nothing about it.

    B2/B4/B5/B6/B7 all fly `fixtures/saves/b2-lko-craft` (the stock Kerbal X),
    shed six radial boosters, and record each as a parent-anchored debris child.
    Their count windows read `min = 1`, so the total loss of that population
    still read PASS.

    THE FIRST CUT OF THIS GATE PINNED THE WRONG TOKEN. It required only
    `Child recording created (debris, TTL=` (BackgroundRecorder.cs:1177), which
    is the BACKGROUND-split site. These flights shed boosters while the craft is
    the ACTIVE focused vessel, and `RecordingTree.IsBackgroundMapEligible`
    excludes `rec.RecordingId == ActiveRecordingId`, so `OnBackgroundPartJointBreak`
    early-returns and that line never fires. All five would have red. The gate now
    accepts EITHER creation site."""

    # spec -> (min, max, decide fn, commands a debris-producing stage drop beyond
    # launch ignition). The FLOOR follows the last field, NOT the decide function:
    # B5/B6/B7 drop a flameout-staged core via _b5_flameout_stage, and B4 drops its
    # service stage via an ACTION_ACTIVATE_STAGE on the sole path into B4_REENTRY.
    # Only B2 stages once (at ignition) and therefore floors at 7. The first cut
    # floored B4 at 7 by keying on _b5_flameout_stage alone.
    GATED = {
        "B2-lko-ascent.toml":         (7, 8, "b2_decide", False),
        "B4-reentry-splashdown.toml": (8, 9, "b4_decide", True),
        "B5-mun-flyby.toml":          (8, 9, "b5_decide", True),
        "B6-minmus-flyby.toml":       (8, 9, "b5_decide", True),
        "B7-duna-flyby.toml":         (8, 8, "b5_decide", True),
    }

    # spec -> (measured count, the PASS run ids it was read from). Every value is
    # `verifiers.expectations.observed.recordings.count` off a verdict=PASS result
    # JSON, read 2026-07-27. Before this, B4's floor was structural-only and B6's
    # was inferred from sharing b5_decide; both measured at exactly what was
    # derived. The field only exists on runs after 72cf344fb (2026-07-25 06:48),
    # which is why all seven run ids are from that morning.
    #
    # These are NOT re-derivable from the archived `logs/*/KSP.log` folders:
    # run.py collects logs on NON-PASS only, so every archived B-lane folder is a
    # run whose expectations were SKIPPED rather than judged
    # (`if driver_valid and not short_circuited`). Their .prec counts are lower
    # and are not this population.
    MEASURED = {
        "B2-lko-ascent.toml":         (7, ("2026-07-25_0824_B2-lko-ascent",)),
        "B4-reentry-splashdown.toml": (8, ("2026-07-25_0828_B4-reentry-splashdown",)),
        "B5-mun-flyby.toml":          (8, ("2026-07-25_0643_B5-mun-flyby",
                                           "2026-07-25_0847_B5-mun-flyby")),
        "B6-minmus-flyby.toml":       (8, ("2026-07-25_0636_B6-minmus-flyby",
                                           "2026-07-25_0856_B6-minmus-flyby")),
        "B7-duna-flyby.toml":         (8, ("2026-07-25_0916_B7-duna-flyby_a2",)),
    }

    CELL = "parent-anchored-debris"
    # TIGHTENED from an EITHER-site alternation on 2026-07-27, against 60
    # archived B-lane KSP.logs: the foreground token appears in 58 of them (the
    # 2 without it are INVALID runs that recorded nothing), and the substring
    # `Child recording created` appears in ZERO. See the block comment in any of
    # the five gated specs for the full grep.
    TOKEN = r"ProcessBreakupEvent: debris child created"

    # The two literal lines the two creation sites emit.
    EMITTED_FG = ("[Parsek][INFO][Coalescer] ProcessBreakupEvent: debris child created: "
                  "pid=1140654732, name='Kerbal X Debris', recId=rec_ab12, alive=True")
    EMITTED_BG = ("[Parsek][INFO][BgRecorder] Child recording created (debris, TTL=60s): "
                  "recId=rec_ab12 vesselPid=1140654732 name='Kerbal X Debris' "
                  "parentRecId=rec_root")

    def _source(self, name):
        with open(os.path.join(PARSEK_SOURCE_DIR, name), encoding="utf-8-sig") as fh:
            return fh.read()

    def test_each_gated_spec_requires_the_debris_token(self):
        for name in self.GATED:
            with self.subTest(spec=name):
                self.assertIn(self.TOKEN,
                              load_spec(name)["expectations"]["logContracts"]["required"])

    def test_the_token_matches_the_line_this_profile_actually_emits(self):
        # The correspondence the whole gate rests on.
        self.assertIsNotNone(re.search(self.TOKEN, self.EMITTED_FG))

    def test_the_token_no_longer_matches_the_background_line(self):
        # Not a property the gate NEEDS - it is a tripwire on the tightening.
        # The alternation was removed on log evidence that the background site
        # never fires on any B-lane profile. If someone restores it, this cell
        # reds and makes them say why in a commit message, rather than the
        # widening passing as a formatting change.
        self.assertIsNone(re.search(self.TOKEN, self.EMITTED_BG))

    # The sibling branch at both creation sites. A gate that matches these does
    # not gate the debris population, so they are the decoy family every claimed
    # token must REJECT (see the reverse check below).
    CONTROLLED = (
        "[Parsek][INFO][Coalescer] ProcessBreakupEvent: controlled child "
        "created: pid=1, name='x', recId=r",
        "[Parsek][INFO][BgRecorder] Child recording created (controlled, "
        "no TTL): recId=r vesselPid=1 name='x'",
    )

    def test_the_token_does_not_match_a_controlled_child(self):
        # If a booster ever took that branch, the run SHOULD red rather than pass
        # on the wrong population.
        for line in self.CONTROLLED:
            self.assertIsNone(re.search(self.TOKEN, line))

    def test_both_emit_sites_still_exist_at_Info(self):
        # Asserts the message is a DIRECT argument of a ParsekLog.Info call. The
        # earlier "nearest preceding ParsekLog" form was defeated by hoisting the
        # message into a local (mutation 12); requiring adjacency fails SAFE - a
        # refactor that separates them reds here, for free, instead of on a
        # nightly.
        # The two sites are asserted for DIFFERENT reasons, and only one is
        # gate-bearing: the FOREGROUND site is what all five specs require
        # without a verboseLogging pin, so a downgrade there reds five nightly
        # flights. The BACKGROUND site is no longer in any pattern; it is
        # asserted because the five spec comment blocks and the reachability
        # cell below all cite it by name and level as the path NOT taken, and a
        # silent rename would leave that evidentiary record pointing at nothing.
        fg = self._source("ParsekFlight.cs")
        self.assertRegex(
            fg, r'ParsekLog\.Info\(\s*"Coalescer",\s*\$"ProcessBreakupEvent: '
                r'debris child created: pid=')
        bg = self._source("BackgroundRecorder.cs")
        self.assertRegex(
            bg, r'ParsekLog\.Info\(\s*"BgRecorder",\s*\n?\s*\$"Child recording '
                r'created \(debris, TTL=\{DebrisTTLSeconds:F0\}s\)')
        self.assertIn("internal const double DebrisTTLSeconds = 60.0;", bg)

    @staticmethod
    def _member_body(source, signature):
        """The text from a C# member signature to the next member at that depth.

        Needed because a bare `assertIn` over a 340 KB file cannot say WHICH
        occurrence it matched. The reviewer defeated the first cut of the cell
        below by renaming a local in `OnBackgroundPartJointBreak`: the guard
        literal appears FOUR times in BackgroundRecorder.cs, so the assertion
        passed on an unrelated one.
        """
        start = source.index(signature)
        rest = source[start + len(signature):]
        ends = [i for i in (rest.find("\n        internal "), rest.find("\n        private "),
                            rest.find("\n        public ")) if i != -1]
        body = rest[:min(ends)] if ends else rest
        # Strip comments, BOTH syntaxes. Without this the assertions match CODE
        # THAT WAS COMMENTED OUT: a reviewer deleted the ActiveRecordingId
        # exclusion and left `// was: rec.RecordingId != ActiveRecordingId`
        # behind, and the cell stayed green on the corpse of the thing it was
        # pinning - then did it again one comment syntax deeper with `/* ... */`.
        body = re.sub(r"/\*.*?\*/", "", body, flags=re.S)
        # `//` only outside a string literal, so a `//` inside one (a URL, say)
        # cannot silently truncate real code out of the slice.
        out = []
        for line in body.split("\n"):
            in_str = False
            cut = len(line)
            i = 0
            while i < len(line) - 1:
                ch = line[i]
                if in_str:
                    if ch == "\\":
                        i += 2
                        continue
                    if ch == '"':
                        in_str = False
                elif ch == '"':
                    in_str = True
                elif ch == "/" and line[i + 1] == "/":
                    cut = i
                    break
                i += 1
            out.append(line[:cut])
        return "\n".join(out)

    def test_the_foreground_path_is_the_one_this_profile_takes(self):
        # Pins the reachability facts the first cut got wrong, each INSIDE the
        # member that owns it, so a change to either surfaces here with this
        # explanation attached rather than as five red flights.
        eligible = self._member_body(self._source("RecordingTree.cs"),
                                     "internal bool IsBackgroundMapEligible(Recording rec)")
        self.assertIn("rec.RecordingId != ActiveRecordingId", eligible,
                      "the active vessel must stay out of BackgroundMap - this "
                      "exclusion is why the background debris token cannot fire "
                      "on a foreground booster drop")
        joint_break = self._member_body(
            self._source("BackgroundRecorder.cs"),
            "internal void OnBackgroundPartJointBreak(PartJoint joint, float breakForce)")
        self.assertIn("if (!tree.BackgroundMap.TryGetValue(vesselPid, out recordingId)) return;",
                      joint_break,
                      "the BackgroundMap early-return is the guard that makes the "
                      "background creation site unreachable from a foreground split")

    def test_the_anchor_contract_call_is_unconditional_on_the_foreground_path(self):
        # Why "a debris child was created" is allowed to buy a REFERENCE-FRAME
        # cell: the same function calls ApplyParentAnchorContract for EVERY child
        # it makes. "Unconditional" was in the first cut's name and nowhere in its
        # assertion - a reviewer wrapped the call in `if (isDebris)` and it stayed
        # green, falsifying the premise while satisfying the substring.
        #
        # Two structural checks together cover both ways to add a condition:
        # brace depth catches a braced `if { ... }`, and requiring the statement
        # to START the line catches a single-line `if (x) Call();`.
        call = "Recording.ApplyParentAnchorContract(childRec, parentRecordingId);"
        body = self._member_body(
            self._source("ParsekFlight.cs"),
            "internal static Recording CreateBreakupChildRecording(")
        idx = body.index(call)
        line_start = body.rindex("\n", 0, idx) + 1
        self.assertEqual(call, body[line_start:idx + len(call)].strip(),
                         "the call must be a bare statement, not the tail of an "
                         "inline `if (...) Call();`")
        depth = body.count("{", 0, idx) - body.count("}", 0, idx)
        self.assertEqual(1, depth,
                         "the call must sit at the method's top level, not inside "
                         "a conditional block (depth %d)" % depth)
        # CONTROL FLOW, not just syntax. The two checks above cover both ways to
        # WRAP the call in a condition; a reviewer then skipped it a third way,
        # with `if (!isDebris) return childRec;` immediately above - depth still
        # 1, statement still starting its line, premise still falsified. Any
        # early return before the call means some children are not stamped.
        preceding = body[:idx]
        self.assertNotIn("return", preceding,
                         "no early return may precede the anchor stamp - the cell's "
                         "premise is that EVERY child this method makes is stamped, "
                         "which is what lets a creation token buy a reference-frame "
                         "coverage cell")

    def test_no_gated_spec_relies_on_verbose_logging_for_it(self):
        for name in self.GATED:
            with self.subTest(spec=name):
                steps = load_spec(name)["driver"].get("steps", [])
                self.assertEqual([], [s for s in steps
                                      if (s or {}).get("args", {}).get("name") == "verboseLogging"])

    def test_floors_match_the_per_mission_derivation(self):
        # The floor is NOT one number: it depends on whether the spec's decide
        # function reaches _b5_flameout_stage (which adds an 8th recording).
        # B11/B12 rejected min=7 for THEIR 8-population runs because 7 is exactly
        # what one dropped recording looks like; that reasoning is why the
        # b5_decide specs are 8 here and only the b2/b4 ones are 7.
        for name, (cmin, cmax, _fn, extra_stage) in self.GATED.items():
            with self.subTest(spec=name):
                count = load_spec(name)["expectations"]["recordings"]["count"]
                self.assertEqual(cmin, count["min"])
                self.assertEqual(cmax, count["max"])
                self.assertEqual(8 if extra_stage else 7, cmin,
                                 "a spec that commands a debris-producing stage drop "
                                 "beyond ignition floors at 8, the others at 7")
                self.assertGreater(cmin, 1, "min = 1 is the vacuity this gate removes")

    def test_every_floor_admits_its_measured_count(self):
        # The cell that converts this gate from derived to measured. Each spec's
        # window must actually admit the count a green run of THAT spec produced,
        # and the floor must not sit below it either - a floor under the measured
        # population is the one-below-population blind spot B11/B12 reject, and a
        # floor above it would red every green run.
        self.assertEqual(sorted(self.GATED), sorted(self.MEASURED),
                         "every gated spec needs a measured count")
        for name, (measured, run_ids) in self.MEASURED.items():
            with self.subTest(spec=name):
                self.assertTrue(run_ids, "a measurement needs a run id to cite")
                count = load_spec(name)["expectations"]["recordings"]["count"]
                self.assertEqual(
                    measured, count["min"],
                    "%s: measured %d on %s but floors at %d. The MEASUREMENT "
                    "wins: re-pin count.min to it and say so in the spec, do not "
                    "widen the window." % (name, measured, run_ids[0], count["min"]))
                self.assertLessEqual(
                    measured, count["max"],
                    "%s: measured %d, above max %d" % (name, measured, count["max"]))

    def test_each_spec_comment_cites_the_run_it_was_measured_from(self):
        # Rule learned the hard way on this branch: a comment block that reads as
        # verified is the thing the next reviewer trusts instead of re-deriving.
        # If a floor claims a measurement, the run id has to be IN the spec, so
        # the claim is checkable without this table.
        for name, (_measured, run_ids) in self.MEASURED.items():
            with self.subTest(spec=name):
                body = open(os.path.join(SCENARIOS_DIR, name), encoding="utf-8").read()
                self.assertTrue(
                    any(r in body for r in run_ids),
                    "%s cites no measured run id; expected one of %s"
                    % (name, ", ".join(run_ids)))

    def test_the_floor_split_matches_the_decide_functions_on_disk(self):
        # Guards the table against mission-wiring drift, in BOTH directions: which
        # decide function each spec drives, and whether that function actually
        # commands an extra stage drop. Keying on `_b5_flameout_stage` alone is
        # what floored B4 one too low on the first cut.
        with open(os.path.join(HARNESS_ROOT, "missions", "lib", "mlib.py"),
                  encoding="utf-8") as fh:
            mlib_src = fh.read()

        def decide_body(fn):
            start = mlib_src.index("def %s(" % fn)
            return mlib_src[start:mlib_src.index("\ndef ", start + 10)]

        for name, (_lo, _hi, fn, extra_stage) in self.GATED.items():
            with self.subTest(spec=name):
                mission = load_spec(name)["driver"]["mission"]
                with open(os.path.join(HARNESS_ROOT, "missions", "%s.py" % mission),
                          encoding="utf-8") as fh:
                    shell = fh.read()
                self.assertIn("mlib.%s" % fn, shell,
                              "%s no longer drives %s" % (name, fn))
                body = decide_body(fn)
                # One ACTIVATE_STAGE is launch ignition; a second (or a flameout
                # watchdog call) is what adds the 8th recording.
                #
                # LIMITATION, stated because the failure message would otherwise
                # invite the wrong fix: this counts within the NAMED decide
                # function only. If a commanded stage drop is refactored into a
                # helper, `stages` falls to 1 and this cell reds - correctly, but
                # the numbers alone read like "this spec should floor at 7". The
                # remedy is to follow the action into the helper, NOT to lower a
                # floor. b5_decide has the `_b5_flameout_stage(` backstop;
                # b4_decide has none.
                stages = body.count("ACTION_ACTIVATE_STAGE")
                flameout = "_b5_flameout_stage(" in body
                drops = stages > 1 or flameout
                self.assertEqual(
                    extra_stage, drops,
                    "%s: %s has %d ACTION_ACTIVATE_STAGE site(s) and flameout=%s, "
                    "so 'commands a stage drop beyond ignition' computes as %s but "
                    "the table says %s. If a stage action moved into a HELPER, fix "
                    "the scan (or this table's flag) - do NOT lower the spec's "
                    "count.min, which is what the population actually is."
                    % (name, fn, stages, flameout, drops, extra_stage))

    def test_every_spec_claiming_the_cell_carries_the_token(self):
        # REVERSE direction, and the one the first cut missed entirely: adding
        # the claim to any spec with no token stayed green, which is precisely
        # the "claim is not gate" fail-open the roadmap lists as risk 5.
        offenders = []
        for name in sorted(os.listdir(SCENARIOS_DIR)):
            if not name.endswith(".toml"):
                continue
            spec = load_spec(name)
            if self.CELL not in (spec.get("dimensionsCovered", {}) or {}).get("D3", []):
                continue
            required = (spec.get("expectations", {}).get("logContracts", {}) or {}).get("required", [])
            # SEMANTIC AND DISCRIMINATING. Two earlier forms of this check were
            # each defeated: a substring match let through an `^`-anchored token
            # that can never match a multi-line body (evaluate_expectations uses
            # re.search with no re.MULTILINE), and a bare "matches the fixture"
            # match let through `recId=`, which fires on any of these lines and
            # gates nothing. So the pattern must match a debris-creation line AND
            # reject both controlled-child lines - the same "must reject the decoy
            # family" shape hlib.batch_contract_vacuity_gap already uses.
            # EITHER creation line satisfies this, deliberately: the check is
            # about the CLAIM being gated, not about which site fires. No
            # committed spec currently uses the background form (all five gated
            # ones tightened to the foreground token on 2026-07-27 log evidence),
            # so that branch is presently unexercised by the corpus.
            if not any((re.search(p, self.EMITTED_FG) or re.search(p, self.EMITTED_BG))
                       and not any(re.search(p, decoy) for decoy in self.CONTROLLED)
                       for p in required):
                offenders.append(name)
        self.assertEqual([], offenders,
                         "a spec claims D3 %s with no token gating it" % self.CELL)

    def test_the_claimed_cell_exists_in_the_registry(self):
        self.assertIn(self.CELL, load_registry()["D3"]["values"])

    def test_the_gate_bites_end_to_end(self):
        spec = load_spec("B2-lko-ascent.toml")
        exp = spec["expectations"]
        good = "Recording started\n%s\nRecording stopped\n" % self.EMITTED_FG
        self.assertEqual("PASS", hlib.evaluate_expectations(exp, 7, good).status)

        no_debris = "Recording started\nRecording stopped\n"
        r = hlib.evaluate_expectations(exp, 1, no_debris)
        self.assertEqual("FAIL", r.status)
        self.assertTrue(any("debris child created" in m for m in r.mismatches))
        self.assertTrue(any("< min 7" in m for m in r.mismatches))

    def test_the_old_window_would_have_passed_that_same_regression(self):
        # Negative control: without the change, the identical log and count read
        # PASS. This is what makes the fix load-bearing rather than decorative.
        old = {"recordings": {"count": {"min": 1, "max": 8}},
               "logContracts": {"required": ["Recording started", "Recording stopped"],
                                "forbidden": [r"\[Parsek\]\[ERROR\]"]}}
        self.assertEqual("PASS", hlib.evaluate_expectations(
            old, 1, "Recording started\nRecording stopped\n").status)

    def test_the_first_cut_token_alone_would_have_red_every_gated_spec(self):
        # Records the defect the review caught, as an executable fact: the
        # background-only token does not appear in a foreground-path log.
        first_cut = r"Child recording created \(debris, TTL="
        log = "Recording started\n%s\nRecording stopped\n" % self.EMITTED_FG
        self.assertIsNone(re.search(first_cut, log))
        self.assertIsNotNone(re.search(self.TOKEN, log))


class RunIdCollisionTests(unittest.TestCase):
    """The runId stamps only the MINUTE, and results/<runId>.json,
    <runId>_shots/ (with the collected KSP.log), <runId>_contact.html,
    <runId>_mission.json and <runId>.manifest.json are ALL keyed by it alone.

    Observed 2026-08-01: two H23-tracking-station flights 52 seconds apart shared
    one id; the second overwrote the first's clean verdict (unityExceptions
    total=0) and its collected KSP.log, and nothing warned. These cells pin the
    resolution that makes that overwrite impossible.
    """

    STAMP = "2026-08-01_1628"
    SCENARIO = "H23-tracking-station"
    BASE = "2026-08-01_1628_H23-tracking-station"

    def _resolve(self, listing, attempt=1, min_ordinal=1):
        """Compose the two halves exactly as run.py does: a results/ LISTING ->
        claimed ids -> the resolved id."""
        return hlib.resolve_run_id(self.STAMP, self.SCENARIO,
                                   hlib.claimed_run_ids(listing),
                                   attempt=attempt, min_ordinal=min_ordinal)

    def test_an_uncontested_run_id_is_exactly_what_it_always_was(self):
        # The guard must be invisible on the ordinary path -- every committed doc
        # example, results/ listing and status panel reads this shape.
        res = self._resolve([])
        self.assertEqual(self.BASE, res.run_id)
        self.assertEqual(1, res.ordinal)
        self.assertFalse(res.collided)
        self.assertEqual((), res.collided_with)

    def test_the_ordinal_sits_before_the_terminal_attempt_suffix(self):
        # status.split_run_id reads `_a<N>` at the END of the id. An ordinal
        # appended after it would make every collided run's attempt unparseable,
        # and would split one run's two attempts across two stems.
        self.assertEqual(self.BASE + "_a2",
                         hlib.format_run_id(self.STAMP, self.SCENARIO, attempt=2))
        self.assertEqual(self.BASE + "_run2",
                         hlib.format_run_id(self.STAMP, self.SCENARIO, ordinal=2))
        self.assertEqual(self.BASE + "_run2_a2",
                         hlib.format_run_id(self.STAMP, self.SCENARIO,
                                            attempt=2, ordinal=2))

    def test_the_observed_incident_no_longer_overwrites(self):
        # results/ as the 19:28:02 flight left it, verbatim.
        first = [self.BASE + ".json", self.BASE + "_shots", self.BASE + "_contact.html"]
        res = self._resolve(first)
        self.assertEqual(self.BASE + "_run2", res.run_id)
        self.assertEqual(2, res.ordinal)
        self.assertEqual((self.BASE,), res.collided_with)
        self.assertTrue(res.collided, "a collision the caller cannot see is the bug")
        # Nothing the 19:28:54 flight writes can land on a first-flight path.
        for suffix in hlib.RUN_ID_ARTIFACT_SUFFIXES:
            self.assertNotIn(res.run_id + suffix, first)

    def test_the_old_construction_would_have_reused_that_very_id(self):
        # Negative control -- what makes the fix load-bearing rather than
        # decorative. The pre-fix id was the bare stamp+scenario unconditionally,
        # so the second flight's result path WAS the first flight's result path.
        old_id = "%s_%s" % (self.STAMP, self.SCENARIO)
        self.assertEqual(self.BASE, old_id)
        self.assertNotEqual(old_id, self._resolve([old_id + ".json"]).run_id)

    def test_a_third_run_in_the_same_minute_steps_over_both(self):
        res = self._resolve([self.BASE + ".json", self.BASE + "_run2.json"])
        self.assertEqual(self.BASE + "_run3", res.run_id)
        self.assertEqual((self.BASE, self.BASE + "_run2"), res.collided_with)

    def test_any_surviving_artifact_claims_the_id_not_just_the_result_json(self):
        # A run can leave a _shots dir (with its KSP.log) whose result JSON never
        # landed -- the write degraded to .pending, or the record was hand-moved.
        # Writing over that dir would still destroy collected evidence.
        for suffix in hlib.RUN_ID_ARTIFACT_SUFFIXES:
            res = self._resolve([self.BASE + suffix])
            self.assertEqual(self.BASE + "_run2", res.run_id,
                             "a surviving '%s' must claim the id" % suffix)

    def test_the_json_suffix_does_not_swallow_the_more_specific_shapes(self):
        # Ordering regression: ".json" matched before "_mission.json" would claim
        # "<runId>_mission" and leave the REAL run id free to be overwritten.
        for suffix in ("_mission.json", ".manifest.json", "_status.json"):
            self.assertEqual({self.BASE}, hlib.claimed_run_ids([self.BASE + suffix]),
                             "'%s' must claim the run id, not a mangled stem" % suffix)

    def test_entries_that_belong_to_no_run_claim_nothing(self):
        # results/ also holds per-INVOCATION harness logs, the rolling summary,
        # the index and tmp files. Treating any of those as a claimed run would
        # push every run id up an ordinal for no reason.
        self.assertEqual(set(), hlib.claimed_run_ids([
            "summary.txt", "index.html", "2026-08-01_162803_harness.log",
            ".pending", self.BASE + ".json.tmp", "index.html.tmp.1234",
            ".json", "_shots", ".seed"]))

    def test_a_retry_of_one_run_is_not_a_collision(self):
        # The `[retry] policy = "once"` path: attempt 2 of ONE run legitimately
        # shares the stem and is distinguished by `_a2`. It must not be pushed to
        # a new ordinal by its own attempt-1 artifacts.
        res = self._resolve([self.BASE + ".json", self.BASE + "_shots"], attempt=2)
        self.assertEqual(self.BASE + "_a2", res.run_id)
        self.assertEqual(1, res.ordinal)
        self.assertFalse(res.collided)

    def test_min_ordinal_keeps_the_attempts_of_one_run_on_one_stem(self):
        # A second run whose FIRST attempt took `_run2` must retry as
        # `_run2_a2`, not drift back to a bare `_a2` just because the first run
        # happened never to retry. Same run, one stem.
        listing = [self.BASE + ".json"]
        first_attempt = self._resolve(listing)
        self.assertEqual(2, first_attempt.ordinal)
        retry = self._resolve(listing, attempt=2, min_ordinal=first_attempt.ordinal)
        self.assertEqual(self.BASE + "_run2_a2", retry.run_id)
        self.assertFalse(retry.collided,
                         "carrying the run's own ordinal forward is not a new collision")

    def test_resolution_terminates_over_a_dense_claim_set(self):
        # No arbitrary ordinal cap exists, deliberately: a cap would have to
        # either raise (losing a verdict, which the design forbids) or overwrite
        # (the bug). `claimed` is finite, so the scan is bounded by len + 1.
        listing = [hlib.format_run_id(self.STAMP, self.SCENARIO, ordinal=n) + ".json"
                   for n in range(1, 51)]
        res = self._resolve(listing)
        self.assertEqual(51, res.ordinal)
        self.assertEqual(self.BASE + "_run51", res.run_id)
        self.assertEqual(50, len(res.collided_with))

    def test_a_different_scenario_in_the_same_minute_never_collides(self):
        res = hlib.resolve_run_id(self.STAMP, "B5-mun-flyby",
                                  hlib.claimed_run_ids([self.BASE + ".json"]))
        self.assertEqual(self.STAMP + "_B5-mun-flyby", res.run_id)
        self.assertFalse(res.collided)

    def test_the_claim_stake_is_one_of_the_claiming_shapes(self):
        # The stake exists to be SEEN by the next resolution; a suffix the scan
        # does not recognize would stake nothing.
        self.assertIn(hlib.RUN_ID_CLAIM_SUFFIX, hlib.RUN_ID_ARTIFACT_SUFFIXES)
        self.assertEqual({self.BASE},
                         hlib.claimed_run_ids([self.BASE + hlib.RUN_ID_CLAIM_SUFFIX]))

    def test_run_py_stamps_the_format_hlib_declares(self):
        # The stamp is produced in run.py and parsed in status.py against the
        # format hlib names; a drift between them breaks the run-age readout.
        self.assertEqual("%Y-%m-%d_%H%M", hlib.RUN_ID_TIMESTAMP_FORMAT)
        self.assertRegex(run._run_id_stamp(), r"^\d{4}-\d{2}-\d{2}_\d{4}$")


# The producer's own entry shape (GhostFxFingerprint.FormatEntry): an asset name,
# its parent, and the placement/scale/size/speed fields, with SPACES and
# PARENTHESES inside. Two of these joined by a bare '|' is the ordinary
# multi-system payload -- the exact shape a naive split would destroy.
FX_FLAME_ENTRY = ("flamejet<thrustTransform pos=(0.00,0.00,0.00) dir=(0.0,-1.0,0.0)"
                  " scale=(1.00,1.00,1.00) size=1.00 speed=1.00")
FX_SMOKE_ENTRY = ("smokeTrail<thrustTransform pos=(0.00,-0.20,0.00) dir=(0.0,-1.0,0.0)"
                  " scale=(1.00,1.00,1.00) size=1.20 speed=0.80")
FX_RCS_ENTRY = ("rcsJet<RCSthruster pos=(0.10,0.00,0.00) dir=(1.0,0.0,0.0)"
                " scale=(1.00,1.00,1.00) size=0.35 speed=2.00")
# A collected KSP.log stamps every line before the [Parsek] tag.
FX_LOG_PREFIX = "[LOG 00:04:12.345] "


def fx_line(part, kind, midx, systems, null_systems, curves, fp,
            prefix=FX_LOG_PREFIX, suffix="", sep=" "):
    """One on-disk KSP.log FxFingerprint line, built the way the producer builds it."""
    return ("%s%s part='%s'%skind=%s%smidx=%d%ssystems=%d%snullSystems=%d%s"
            "curves=%s%sfp=[%s]%s"
            % (prefix, hlib.FX_FINGERPRINT_ANCHOR, part, sep, kind, sep, midx, sep,
               systems, sep, null_systems, sep, curves, sep, fp, suffix))


class FxFingerprintCaptureTests(unittest.TestCase):
    """The capture must survive every shape the REAL producer + ParsekLog emit.

    Guards the five parse traps the fingerprint line carries: a quoted part name
    with dots, a payload full of spaces / parentheses / '|', the rate limiter's
    " | suppressed=N" tail landing AFTER the closing bracket, the legitimate
    `fp=[(none)]` empty payload, and a KSP.log timestamp ahead of the anchor. Any
    one of them mis-handled silently SHRINKS the captured set, which reads as a
    clean A/B rather than as a broken one."""

    ENGINE = fx_line("liquidEngine.v2", "engine", 0, 2, 0, "em3/sp2",
                     FX_FLAME_ENTRY + "|" + FX_SMOKE_ENTRY)
    RCS = fx_line("RCSBlock.v2", "rcs", 1, 1, 1, "em2/sp2", FX_RCS_ENTRY)

    def test_realistic_engine_and_rcs_lines_round_trip(self):
        got = hlib.parse_fx_fingerprint_lines([self.ENGINE, self.RCS])
        self.assertEqual(0, got.malformed)
        self.assertEqual(
            {("liquidEngine.v2", "engine", 0), ("RCSBlock.v2", "rcs", 1)},
            set(got.entries))
        # The captured value is the canonical head fields + the bracketed fp,
        # byte-for-byte (the producer already sorted the entries Ordinal).
        self.assertEqual(
            ("systems=2 nullSystems=0 curves=em3/sp2 fp=[%s|%s]"
             % (FX_FLAME_ENTRY, FX_SMOKE_ENTRY),),
            got.entries[("liquidEngine.v2", "engine", 0)])
        self.assertEqual(
            ("systems=1 nullSystems=1 curves=em2/sp2 fp=[%s]" % FX_RCS_ENTRY,),
            got.entries[("RCSBlock.v2", "rcs", 1)])

    def test_the_dotted_part_name_and_the_module_index_survive_the_key(self):
        # KSP's runtime part names are dot-form (cfg `solidBooster_v2` ->
        # `solidBooster.v2`), and a part can carry more than one engine module, so
        # midx is part of the identity and must be an INT (midx 10 sorts after 2).
        lines = [fx_line("liquidEngine.v2", "engine", n, 1, 0, "em2/sp2",
                         FX_FLAME_ENTRY) for n in (2, 10)]
        got = hlib.parse_fx_fingerprint_lines(lines)
        self.assertEqual([("liquidEngine.v2", "engine", 2),
                          ("liquidEngine.v2", "engine", 10)], sorted(got.entries))

    def test_a_ksp_log_timestamp_prefix_does_not_defeat_the_anchor(self):
        # Anchored on the SUBSTRING, never on line start: a collected log always
        # carries a stamp, so a start-anchored match would capture nothing at all.
        stamped = hlib.parse_fx_fingerprint_lines([self.ENGINE])
        bare = hlib.parse_fx_fingerprint_lines(
            [fx_line("liquidEngine.v2", "engine", 0, 2, 0, "em3/sp2",
                     FX_FLAME_ENTRY + "|" + FX_SMOKE_ENTRY, prefix="")])
        self.assertEqual(stamped.entries, bare.entries)
        self.assertEqual(1, len(stamped.entries))

    def test_the_rate_limiter_suppressed_tail_is_stripped(self):
        # ParsekLog appends " | suppressed=N" AFTER the closing "]" on the next
        # changed emission. Cutting at the FIRST "]" or at the first " | " would
        # slice the fingerprint open; cutting at the LAST "]" is what makes the
        # suppressed line compare equal to the unsuppressed one.
        suppressed = fx_line("liquidEngine.v2", "engine", 0, 2, 0, "em3/sp2",
                             FX_FLAME_ENTRY + "|" + FX_SMOKE_ENTRY,
                             suffix=" | suppressed=3")
        got = hlib.parse_fx_fingerprint_lines([self.ENGINE, suppressed])
        self.assertEqual(0, got.malformed)
        # Same key, ONE value: the tail is not part of the fingerprint.
        self.assertEqual(1, len(got.entries[("liquidEngine.v2", "engine", 0)]))
        self.assertNotIn("suppressed",
                         got.entries[("liquidEngine.v2", "engine", 0)][0])

    def test_a_pipe_joined_multi_entry_payload_survives_whole(self):
        # The '|' is the producer's own entry join AND the rate limiter's tail
        # separator. Three entries with spaces inside must come back intact.
        fp = "|".join([FX_FLAME_ENTRY, FX_RCS_ENTRY, FX_SMOKE_ENTRY])
        got = hlib.parse_fx_fingerprint_lines(
            [fx_line("someEngine.v1", "engine", 0, 3, 0, "em4/sp4", fp)])
        value = got.entries[("someEngine.v1", "engine", 0)][0]
        self.assertTrue(value.endswith("fp=[%s]" % fp), value)
        self.assertEqual(2, value.count("|"))

    def test_the_no_systems_payload_is_a_value_not_a_parse_failure(self):
        # `fp=[(none)]` is what BuildFingerprint emits for zero systems. It must
        # capture as an ordinary value: "(none) on one side, real systems on the
        # other" is the single most interesting finding this diff can report.
        got = hlib.parse_fx_fingerprint_lines(
            [fx_line("strutConnector", "engine", 0, 0, 0, "em0/sp0", "(none)")])
        self.assertEqual(0, got.malformed)
        self.assertEqual(("systems=0 nullSystems=0 curves=em0/sp0 fp=[(none)]",),
                         got.entries[("strutConnector", "engine", 0)])

    def test_whitespace_jitter_between_tokens_normalizes_to_one_payload(self):
        # The captured value is REBUILT from the parsed fields, so incidental
        # spacing can never read as an A/B divergence.
        loose = fx_line("liquidEngine.v2", "engine", 0, 2, 0, "em3/sp2",
                        FX_FLAME_ENTRY + "|" + FX_SMOKE_ENTRY, sep="   ")
        got = hlib.parse_fx_fingerprint_lines([self.ENGINE, loose])
        self.assertEqual(1, len(got.entries[("liquidEngine.v2", "engine", 0)]))

    def test_a_malformed_anchored_line_is_counted_not_raised(self):
        # A producer-side format drift must surface as a NUMBER, not as a quietly
        # smaller captured set and not as an exception mid-parse.
        malformed = [
            hlib.FX_FINGERPRINT_ANCHOR + " part='x' kind=engine midx=0",
            hlib.FX_FINGERPRINT_ANCHOR + " kind=engine midx=0 systems=1 "
            "nullSystems=0 curves=em1/sp1 fp=[a]",
            hlib.FX_FINGERPRINT_ANCHOR + " part='x' kind=engine midx=zero "
            "systems=1 nullSystems=0 curves=em1/sp1 fp=[a]",
            hlib.FX_FINGERPRINT_ANCHOR + " part='x' kind=engine midx=0 systems=1 "
            "nullSystems=0 curves=em1/sp1 fp=[a",
        ]
        got = hlib.parse_fx_fingerprint_lines(malformed + [self.ENGINE])
        self.assertEqual(4, got.malformed)
        self.assertEqual(1, len(got.entries), "the good line still captures")

    def test_a_line_without_the_anchor_is_neither_captured_nor_counted(self):
        # Every other line in a large KSP.log is not an FX line; counting them
        # malformed would drown the drift signal the counter exists to carry.
        noise = ["[LOG 00:00:01.000] [Parsek][INFO][Recorder] recording started",
                 "", "some stock line with fp=[whatever] in it"]
        got = hlib.parse_fx_fingerprint_lines(noise)
        self.assertEqual(0, got.malformed)
        self.assertEqual({}, got.entries)

    def test_repeated_identical_lines_collapse_to_one_value(self):
        # The 5 s rate limiter re-emits a stable fingerprint; a stable module must
        # not read as unstable just because it was logged three times.
        got = hlib.parse_fx_fingerprint_lines([self.ENGINE] * 3)
        self.assertEqual((("systems=2 nullSystems=0 curves=em3/sp2 fp=[%s|%s]"
                           % (FX_FLAME_ENTRY, FX_SMOKE_ENTRY)),),
                         got.entries[("liquidEngine.v2", "engine", 0)])

    def test_repeated_differing_lines_for_one_key_keep_every_distinct_value(self):
        # A fingerprint that changes WITHIN one run is itself a reportable fact
        # (an FX rebuild moved the geometry). Collapsing to the first (or last)
        # value would hide a divergence inside a single side of the A/B.
        rebuilt = fx_line("liquidEngine.v2", "engine", 0, 2, 0, "em3/sp2",
                          FX_FLAME_ENTRY + "|" + FX_SMOKE_ENTRY.replace(
                              "size=1.20", "size=2.40"))
        got = hlib.parse_fx_fingerprint_lines([self.ENGINE, rebuilt, self.ENGINE])
        values = got.entries[("liquidEngine.v2", "engine", 0)]
        self.assertEqual(2, len(values))
        self.assertEqual(tuple(sorted(values)), values, "values come back sorted")


class FxFingerprintDiffTests(unittest.TestCase):
    """The A/B set-diff: what the modded-compat lane actually reads.

    Guards that a module built on only one side, a module built differently on
    the two sides, and a module that disagreed with ITSELF are three distinct
    findings -- and that the proven-equivalent set is counted, so a clean diff
    reads as coverage rather than as silence."""

    A_KEY = ("liquidEngine.v2", "engine", 0)
    B_KEY = ("RCSBlock.v2", "rcs", 1)
    V1 = "systems=2 nullSystems=0 curves=em3/sp2 fp=[%s]" % FX_FLAME_ENTRY
    V2 = "systems=1 nullSystems=1 curves=em3/sp2 fp=[(none)]"

    def test_shared_identical_keys_count_as_matches(self):
        cap = {self.A_KEY: (self.V1,), self.B_KEY: (self.V2,)}
        d = hlib.diff_fx_fingerprints(cap, dict(cap))
        self.assertEqual(2, d.match_count)
        self.assertEqual([], d.changed)
        self.assertEqual([], d.keys_only_in_a)
        self.assertEqual([], d.keys_only_in_b)

    def test_a_key_only_one_side_built_is_reported_on_that_side(self):
        # The coverage half: a config pack deleting a stock particle definition
        # makes the module vanish from one install's fingerprint set entirely.
        d = hlib.diff_fx_fingerprints({self.A_KEY: (self.V1,)},
                                      {self.B_KEY: (self.V2,)})
        self.assertEqual([self.A_KEY], d.keys_only_in_a)
        self.assertEqual([self.B_KEY], d.keys_only_in_b)
        self.assertEqual(0, d.match_count)
        self.assertEqual([], d.changed)

    def test_a_differing_payload_is_reported_as_changed_with_both_values(self):
        d = hlib.diff_fx_fingerprints({self.A_KEY: (self.V1,)},
                                      {self.A_KEY: (self.V2,)})
        self.assertEqual([(self.A_KEY, (self.V1,), (self.V2,))], d.changed)
        self.assertEqual(0, d.match_count)

    def test_an_unstable_key_is_flagged_on_its_own_side_and_still_diffs(self):
        # An unstable key is NOT excused from the diff -- it reports its
        # disagreement AND says the evidence is untrustworthy.
        d = hlib.diff_fx_fingerprints({self.A_KEY: (self.V1, self.V2)},
                                      {self.A_KEY: (self.V1,)})
        self.assertEqual([self.A_KEY], d.unstable_a)
        self.assertEqual([], d.unstable_b)
        # Values come back sorted on both sides (the parser's own order), so the
        # compare cannot depend on which payload the log happened to emit first.
        self.assertEqual([(self.A_KEY, tuple(sorted((self.V1, self.V2))), (self.V1,))],
                         d.changed)
        self.assertEqual(0, d.match_count)

    def test_a_capture_diffs_straight_out_of_the_parser(self):
        # End to end over the real line shape: the parser's `entries` mapping is
        # exactly what the diff consumes.
        a = hlib.parse_fx_fingerprint_lines(
            [fx_line("liquidEngine.v2", "engine", 0, 1, 0, "em3/sp2", FX_FLAME_ENTRY)])
        b = hlib.parse_fx_fingerprint_lines(
            [fx_line("liquidEngine.v2", "engine", 0, 0, 0, "em3/sp2", "(none)")])
        d = hlib.diff_fx_fingerprints(a.entries, b.entries)
        self.assertEqual(1, len(d.changed))
        self.assertIn("fp=[(none)]", d.changed[0][2][0])

    def test_empty_and_missing_captures_diff_cleanly(self):
        d = hlib.diff_fx_fingerprints({}, {})
        self.assertEqual(hlib.FxFingerprintDiff([], [], [], [], [], 0), d)
        self.assertEqual(d, hlib.diff_fx_fingerprints(None, None))

    def test_every_list_comes_back_sorted_by_key(self):
        # Numeric midx ordering (2 before 10) is what makes the report readable
        # for a part carrying more than nine modules; a lexical key would not.
        a = {("p", "engine", n): (self.V1,) for n in (10, 2, 1)}
        d = hlib.diff_fx_fingerprints(a, {})
        self.assertEqual([("p", "engine", 1), ("p", "engine", 2),
                          ("p", "engine", 10)], d.keys_only_in_a)


class FxFingerprintReportTests(unittest.TestCase):
    """The report render must be byte-stable: one header, one line per finding.

    A report that reordered between two invocations could not itself be diffed,
    which is the whole point of pasting it into a PR."""

    KEY_A = ("liquidEngine.v2", "engine", 0)
    KEY_B = ("RCSBlock.v2", "rcs", 1)
    KEY_C = ("solidBooster.v2", "engine", 2)
    V1 = "systems=1 nullSystems=0 curves=em3/sp2 fp=[%s]" % FX_FLAME_ENTRY
    V2 = "systems=0 nullSystems=0 curves=em0/sp0 fp=[(none)]"

    def test_a_clean_diff_renders_only_the_summary_header(self):
        d = hlib.diff_fx_fingerprints({self.KEY_A: (self.V1,)},
                                      {self.KEY_A: (self.V1,)})
        self.assertEqual(
            ["fx-fingerprint-diff a=stock b=waterfall match=1 changed=0 "
             "only-in-a=0 only-in-b=0 unstable-a=0 unstable-b=0"],
            hlib.format_fx_fingerprint_diff(d, "stock", "waterfall"))

    def test_every_finding_renders_exactly_one_grep_stable_line(self):
        a = {self.KEY_A: (self.V1,), self.KEY_B: (self.V1, self.V2)}
        b = {self.KEY_B: (self.V1,), self.KEY_C: (self.V2,)}
        got = hlib.format_fx_fingerprint_diff(
            hlib.diff_fx_fingerprints(a, b), "stock", "waterfall")
        self.assertEqual([
            "fx-fingerprint-diff a=stock b=waterfall match=0 changed=1 "
            "only-in-a=1 only-in-b=1 unstable-a=1 unstable-b=0",
            "only-in-a side=stock part='liquidEngine.v2' kind=engine midx=0",
            "only-in-b side=waterfall part='solidBooster.v2' kind=engine midx=2",
            "changed part='RCSBlock.v2' kind=rcs midx=1 :: stock -> %s ;; %s "
            ":: waterfall -> %s" % (self.V2, self.V1, self.V1),
            "unstable-a side=stock part='RCSBlock.v2' kind=rcs midx=1",
        ], got)

    def test_the_render_is_byte_stable_across_input_insertion_order(self):
        keys = [self.KEY_A, self.KEY_B, self.KEY_C]
        forward = {k: (self.V1,) for k in keys}
        backward = {k: (self.V1,) for k in reversed(keys)}
        self.assertEqual(
            hlib.format_fx_fingerprint_diff(
                hlib.diff_fx_fingerprints(forward, {}), "stock", "waterfall"),
            hlib.format_fx_fingerprint_diff(
                hlib.diff_fx_fingerprints(backward, {}), "stock", "waterfall"))

    def test_a_label_with_spaces_cannot_break_the_leading_grep_token(self):
        d = hlib.diff_fx_fingerprints({self.KEY_A: (self.V1,)}, {})
        lines = hlib.format_fx_fingerprint_diff(d, "stock minimal", "modded compat")
        self.assertTrue(lines[0].startswith("fx-fingerprint-diff "))
        self.assertTrue(lines[1].startswith("only-in-a side=stock minimal part='"))


# ---------------------------------------------------------------------------
# Shared craft library (harness/fixtures/ships + shared-ships.toml).
# ---------------------------------------------------------------------------


FIXTURES_DIR = os.path.join(HARNESS_ROOT, "fixtures")
FIXTURE_SAVES_DIR = os.path.join(FIXTURES_DIR, "saves")
SHIPS_DIR = os.path.join(FIXTURES_DIR, hlib.SHARED_SHIPS_DIRNAME)
SHARED_SHIPS_PATH = os.path.join(FIXTURES_DIR, hlib.SHARED_SHIPS_MANIFEST_NAME)


def _library_ship_names():
    suffix = hlib.SHARED_SHIP_SUFFIX
    if not os.path.isdir(SHIPS_DIR):
        return []
    return sorted(n[:-len(suffix)] for n in os.listdir(SHIPS_DIR) if n.endswith(suffix))


def _fixture_save_names():
    if not os.path.isdir(FIXTURE_SAVES_DIR):
        return []
    return sorted(n for n in os.listdir(FIXTURE_SAVES_DIR)
                  if os.path.isdir(os.path.join(FIXTURE_SAVES_DIR, n)))


def _read_manifest():
    with open(SHARED_SHIPS_PATH, "rb") as fh:
        return hlib.parse_shared_ships_manifest(tomllib.load(fh))


class SharedShipOverlayPlanTests(unittest.TestCase):
    """Pure resolution of the per-save craft overlay (hlib.plan_shared_ship_overlay).

    Guards the decision half of the fixture dedup: a save fixture no longer commits
    its own copy of a craft two or more fixtures share, so the copy has to arrive at
    stage time or `launch_vessel` cannot resolve `<save>/Ships/VAB/<name>.craft`.
    """

    MAN = {"a-save": ("Kerbal X", "Duna Rocket"), "b-save": ("Kerbal X",)}
    LIB = ("Kerbal X", "Duna Rocket")

    def test_a_declared_save_resolves_its_craft_in_declaration_order(self):
        plan = hlib.plan_shared_ship_overlay("a-save", self.MAN, self.LIB, [])
        self.assertEqual((), plan.errors)
        self.assertEqual([("Kerbal X.craft", "Kerbal X.craft"),
                          ("Duna Rocket.craft", "Duna Rocket.craft")], list(plan.entries))

    def test_a_save_absent_from_the_manifest_overlays_nothing_and_is_not_an_error(self):
        # Most fixtures (the fresh-* KSC templates) carry no craft at all; an
        # unlisted save must stage exactly as a verbatim copytree would.
        plan = hlib.plan_shared_ship_overlay("fresh-career", self.MAN, self.LIB, [])
        self.assertEqual((), plan.entries)
        self.assertEqual((), plan.errors)

    def test_a_declared_ship_missing_from_the_library_is_an_error_not_a_silent_skip(self):
        # The resolvable sibling still appears in entries -- the plan is partial,
        # not empty. The shell must gate on `errors` and never on `entries` being
        # empty, which is what the staging-abort test below pins.
        plan = hlib.plan_shared_ship_overlay("a-save", self.MAN, ("Kerbal X",), [])
        self.assertEqual([("Kerbal X.craft", "Kerbal X.craft")], list(plan.entries))
        self.assertEqual(1, len(plan.errors))
        self.assertIn("Duna Rocket", plan.errors[0])
        self.assertIn("not in the ship library", plan.errors[0])

    def test_a_ship_both_declared_and_committed_physically_is_an_error(self):
        # This is the dedup-regressed shape: silently overlaying would overwrite a
        # committed copy that may have diverged, hiding exactly the drift the
        # shared library exists to prevent.
        plan = hlib.plan_shared_ship_overlay("b-save", self.MAN, self.LIB, ["Kerbal X.craft"])
        self.assertEqual((), plan.entries)
        self.assertEqual(1, len(plan.errors))
        self.assertIn("declared shared AND committed", plan.errors[0])

    def test_an_unrelated_committed_craft_beside_the_overlay_is_left_alone(self):
        plan = hlib.plan_shared_ship_overlay("b-save", self.MAN, self.LIB, ["Auto-Saved Ship.craft"])
        self.assertEqual((), plan.errors)
        self.assertEqual([("Kerbal X.craft", "Kerbal X.craft")], list(plan.entries))

    def test_a_traversal_ship_name_is_rejected(self):
        man = {"a-save": ("../../../etc/passwd", "..")}
        plan = hlib.plan_shared_ship_overlay("a-save", man, self.LIB, [])
        self.assertEqual((), plan.entries)
        self.assertEqual(2, len(plan.errors))
        self.assertTrue(all("not filename-safe" in e for e in plan.errors))

    def test_a_duplicate_row_entry_copies_once(self):
        man = {"a-save": ("Kerbal X", "Kerbal X")}
        plan = hlib.plan_shared_ship_overlay("a-save", man, self.LIB, [])
        self.assertEqual((), plan.errors)
        self.assertEqual(1, len(plan.entries))

    def test_a_malformed_manifest_body_parses_to_empty_rather_than_raising(self):
        # Staging turns an unresolved save into a named error; a traceback out of
        # the TOML shape would lose that naming.
        self.assertEqual({}, hlib.parse_shared_ships_manifest(None))
        self.assertEqual({}, hlib.parse_shared_ships_manifest({"ships": "not-a-table"}))
        self.assertEqual({"s": ("Kerbal X",)},
                         hlib.parse_shared_ships_manifest({"ships": {"s": ["Kerbal X", 7]}}))

    def test_a_bare_table_without_the_ships_wrapper_is_accepted(self):
        self.assertEqual({"s": ("Kerbal X",)},
                         hlib.parse_shared_ships_manifest({"s": ["Kerbal X"]}))


class SharedShipsManifestTests(unittest.TestCase):
    """The COMMITTED fixture tree against the COMMITTED manifest.

    This is the regression floor for the dedup itself. Before it, a craft flown by
    several fixtures was committed once per fixture: 27 files, 180,799 duplicated
    lines, twelve byte-identical copies of `Kerbal X.craft` alone -- and each copy
    drifted independently, which is why `build_dd1_craft.py` had grown a tuple
    naming "EVERY committed copy of the craft" so its byte gate could walk them.
    A re-introduced copy reds HERE instead of quietly restoring that state.
    """

    def test_the_committed_manifest_satisfies_every_invariant(self):
        errors = hlib.validate_shared_ships_manifest(
            _read_manifest(), _library_ship_names(), _fixture_save_names())
        self.assertEqual((), errors, "shared-ships.toml invariants: %s" % (errors,))

    def test_no_committed_fixture_file_duplicates_a_shared_library_craft(self):
        # The anti-regression sweep. Hash every committed file under fixtures/saves
        # and assert none is byte-identical to a library craft. Content-addressed on
        # purpose: a re-introduced copy under a DIFFERENT name (a rename, a harvest
        # that renamed the ship) is the same duplication and must red the same way.
        import hashlib
        lib_digests = {}
        for name in os.listdir(SHIPS_DIR):
            path = os.path.join(SHIPS_DIR, name)
            with open(path, "rb") as fh:
                lib_digests[hashlib.sha256(fh.read()).hexdigest()] = name
        offenders = []
        for dirpath, _, filenames in os.walk(FIXTURE_SAVES_DIR):
            for fn in filenames:
                path = os.path.join(dirpath, fn)
                with open(path, "rb") as fh:
                    digest = hashlib.sha256(fh.read()).hexdigest()
                if digest in lib_digests:
                    offenders.append("%s duplicates fixtures/%s/%s"
                                     % (os.path.relpath(path, FIXTURES_DIR).replace("\\", "/"),
                                        hlib.SHARED_SHIPS_DIRNAME, lib_digests[digest]))
        self.assertEqual([], sorted(offenders),
                         "committed fixture files duplicate a shared library craft; add a "
                         "shared-ships.toml row instead of copying the .craft in")

    def test_every_manifest_row_resolves_against_the_committed_library(self):
        manifest, lib = _read_manifest(), _library_ship_names()
        for save in sorted(manifest):
            vab = os.path.join(FIXTURE_SAVES_DIR, save, *hlib.SHARED_SHIPS_DEST_SEGMENTS)
            existing = os.listdir(vab) if os.path.isdir(vab) else []
            plan = hlib.plan_shared_ship_overlay(save, manifest, lib, existing)
            self.assertEqual((), plan.errors, "%s: %s" % (save, plan.errors))
            self.assertEqual(len(manifest[save]), len(plan.entries), save)

    def test_every_spec_that_launches_a_craft_can_resolve_it(self):
        """The consumer-side gate, and the one that stops a dedup slip costing a flight.

        A spec that drives `launch_vessel` names its craft in
        `driver.missionParams.craftName`, and kRPC resolves it against
        `<save>/Ships/VAB/<craftName>.craft` in the STAGED save. Post-dedup that
        file arrives one of two ways: physically in the fixture, or via a
        `shared-ships.toml` row. Neither present means the save stages craftless
        and `stage_fixture` reports success -- an unlisted save resolves zero
        entries AND zero errors by design, because most fixtures need no shared
        craft at all. The run then boots and dies minutes later inside the mission
        as a driver-INVALID against a perfectly good spec, which is the exact
        misdirection the overlay's fail-closed path exists to prevent.

        An earlier version of this cell asserted only that each saveTemplate leaf
        was a real directory and that EXISTING rows resolve -- both of which stay
        true when a row is dropped, so it could not see the failure it was named
        for. This is the strong form: physical-or-declared, per launching spec.

        `watcherCraftName` is swept alongside `craftName` (added with GS-4, whose
        kx_rewind_watch mission `launch_vessel`s a SECOND craft after the rewind):
        every param that a mission resolves through `<save>/Ships/VAB/<name>.craft`
        must pass the same physical-or-declared gate, or the run boots and dies at
        the watcher rollout with the identical misdirection this cell exists to
        prevent."""
        manifest = _read_manifest()
        library = set(_library_ship_names())
        unresolved = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            params = ((spec.get("driver", {}) or {}).get("missionParams", {}) or {})
            crafts = [params.get(key) for key in ("craftName", "watcherCraftName")]
            crafts = [c for c in crafts if c]
            if not crafts:
                continue
            leaf = ((spec.get("fixture", {}) or {}).get("saveTemplate", "")
                    ).replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
            for craft in crafts:
                physical = os.path.isfile(os.path.join(
                    FIXTURE_SAVES_DIR, leaf, *(hlib.SHARED_SHIPS_DEST_SEGMENTS
                                               + (craft + hlib.SHARED_SHIP_SUFFIX,))))
                declared = craft in manifest.get(leaf, ()) and craft in library
                if not (physical or declared):
                    unresolved.append("%s: %s launches %r from %r, which carries it "
                                      "neither physically nor via shared-ships.toml"
                                      % (name, name[:-5], craft, leaf))
        self.assertEqual([], unresolved, "\n".join(unresolved))

    # LANES COMMITTED AHEAD OF THE FIXTURE THEY CONSUME, mapped to the fixture leaf
    # they are waiting on. NARROW AND SELF-RETIRING BY CONSTRUCTION: the cell below
    # reds if an entry's fixture EXISTS (the exemption must then be deleted in the
    # calibration commit) and reds if a listed spec no longer names that leaf, so
    # this cannot quietly become a place specs go to avoid the gate.
    #
    # WHY IT EXISTS AT ALL, since a committed spec pointing at a missing directory is
    # normally exactly the misdirection this class is named for: the DOWNSTREAM half
    # of a two-stage program cannot name a fixture its UPSTREAM lane has not yet
    # flown and harvested, and the downstream lane's whole value is that its
    # predictions are written DOWN BEFORE the flight rather than after it (the V14
    # lanes' refuted tidal-collapse prediction is the standing demonstration that a
    # pre-registered prediction is worth more than a post-hoc description). Any spec
    # listed here must carry a NOT FLYABLE banner and calibration-seed jump UTs, so
    # nothing here pretends a listed lane is runnable.
    #
    # EMPTY IS ITS HEALTHY STATE (the `INTERIM_PIN_IDS` convention), and it has been
    # empty exactly once so far. Its FIRST use was the B24/V15 Gilly program:
    # `V15M-gilly-player-loop` and `V15T-gilly-ts-arrival` were listed on 2026-08-19
    # because they were committed ahead of `gilly-orbit-recorded`, and BOTH ENTRIES
    # WERE REMOVED the same day when `B24-gilly-orbit` flew (run 2026-08-19_1655,
    # PASS attempt 1) and its produced save was harvested - which is exactly the red
    # the retirement cell below raises. The MECHANISM was kept rather than deleted
    # because the shape recurs (B23/V14 had it too and paid for it by landing the
    # pair only after the flight), AND IT RECURRED IMMEDIATELY: the B25/V16 Laythe
    # program is its SECOND use, which is the cheapest possible vindication of not
    # deleting it.
    #
    # ITS SECOND USE RETIRED THE SAME WAY THE FIRST DID, AND THE CELL BELOW FIRED
    # BOTH TIMES. `V16M-laythe-player-loop` and `V16T-laythe-ts-arrival` were listed
    # on 2026-08-19 because they were committed ahead of `laythe-orbit-recorded`;
    # `B25-laythe-orbit` then flew (flight 1 `_1948` / `_2001` INVALID on a park
    # window written before anyone had measured what a finite capture burn does to a
    # periapsis, flight 2 `_2039` PASS attempt 1 once the window was resized to that
    # measurement), its produced save was harvested, and BOTH ENTRIES WERE REMOVED
    # in the calibration commit - which is exactly the red the retirement cell below
    # raises. The map is EMPTY again, its healthy state, which restores the gate
    # below to full strength: with nothing listed, EVERY committed spec's
    # saveTemplate is checked.
    #
    # WORTH RECORDING FROM THAT PROGRAM, because it is what the exemption bought:
    # the pair's ORIGINAL seed set put the seam offset 14,768 s off against a
    # +-180 s bracket, and flight 1 - a FAILED run whose CLOCK was nonetheless real -
    # corrected it to within 1 s of the harvested truth before the fixture existed.
    # Committing the downstream lanes early is what made that mid-course correction
    # possible at all, which is the strongest argument yet for keeping this
    # mechanism rather than deleting it between programs.
    # THE CURRENT ENTRIES, both awaiting `B26-laythe-vall-transfer`'s produced
    # save: `V17M-laythe-vall-player-loop` and `V17T-laythe-vall-ts-arrival`. This
    # is the mechanism's THIRD use in three consecutive programs, which is by now
    # less a special case than the shape a two-stage program has.
    #
    # WHAT MAKES THIS PAIR DIFFERENT FROM THE TWO BEFORE IT, and why the exemption
    # earns its keep even more here: V15's and V16's seeds were merely UNMEASURED -
    # the ROUTING was known (same-parent -> phase-lock), so the anchor formula was
    # known and only the UTs had to be substituted. V17's subject is CROSS-PARENT
    # and its routing IS the measurement, so the two candidate roads produce
    # anchors from DIFFERENT PLANNERS and no seed set can be right for both. The
    # specs say so at length, seed under the re-aim hypothesis, and pre-register
    # both outcomes with the log lines that discriminate. A dwell-nowhere run 1 is
    # an accepted outcome there rather than a lane failure.
    #
    # DELETE BOTH in the same commit that re-pins the pair off the harvested bytes;
    # this cell reds until then.
    # RETIRED 2026-08-20 by B26 flight 3 (run 2026-08-20_1752, PASS attempt 1):
    # `vall-transfer-recorded` EXISTS and V17M/V17T are re-pinned off its bytes,
    # so the two entries were deleted in the same commit - which is exactly the
    # discipline the cell below enforces, and it DID fire red first.
    #
    # RE-ARMED 2026-08-21 BY THE G3a SURFACE-ENDPOINT PROGRAM, and this is the
    # THIRD two-stage program to use the exemption (V15's, V17's, now V22/V23's).
    # What is different about this one is WHY the fixtures do not exist yet: it
    # is NOT waiting on a flight capability. Both producers are committed and
    # LIVE-PROVEN today - `B4-reentry-splashdown` (2026-07-20) and
    # `B13-mun-landing` (full PASS on flight 1, 2026-07-25,
    # `terminalState=Landed terminalOrbitBody=Mun`) - and the mission library's
    # `landingEnabled` phase has driven MechJeb's LandingAutopilot since well
    # before the parent-relay mode existed. What has never happened is the
    # HARVEST: fourteen orbit and transfer fixtures are committed and not one
    # landed or splashed save is, which is why every loop subject in the suite
    # ends at an ORBIT. These five lanes are the first to need one, and they
    # were authored in a session that cannot fly.
    #
    # DELETE AN ENTRY in the same commit that re-pins its spec off the harvested
    # bytes (real tree id, real jump UTs derived from the MEASURED loop clock);
    # this cell reds until then, in BOTH directions.
    #
    # RETIRED 2026-08-24 BY THE G3a HARVEST PAIR - all five entries at once, which
    # is the first time the exemption has closed in a single step rather than a
    # pair at a time. `B4-reentry-splashdown` (run 2026-08-24_1431, PASS attempt 1)
    # produced `kerbin-splashdown-recorded` and `B13-mun-landing` (run
    # 2026-08-24_1449, PASS attempt 1) produced `mun-landing-recorded`; both were
    # harvested `--keep-parsek`, both are registered in
    # `test_saveparse.RECORDED_FIXTURES` with their full provenance, and all five
    # specs are re-pinned off those bytes in the same commit. The cell DID fire red
    # first, in all three of its arms (this one, the mirror cell and the committed-
    # fixture-set sweep), which is the discipline working.
    #
    # ONE THING THE PROGRAM DID NOT GET TO CHOOSE, worth leaving here for the next
    # two-stage program: the V22 loop clock could not be re-pinned to a MEASURED
    # value, only to a better-grounded derived one. The anchor is resolved at run
    # time by `MissionLoopUnitBuilder` and neither harvest flight loops, so neither
    # KSP.log carries a `PhaseLock APPLIED` or `missionconfig applied:` line at all
    # (grepped: zero occurrences in both). The jump tables therefore stay
    # CALIBRATION SEEDS through run 1 exactly as each spec's section 3 says, now
    # seeded off the fixtures' real UT0 / span / seam rather than off guesses.
    #
    # RE-ARMED 2026-08-23 by the G4 pair, which is exactly the case this
    # mechanism exists for: `V21M`/`V21T` were the DOWNSTREAM half of a two-stage
    # program whose UPSTREAM lane (`B30-mun-minmus-transfer`) was authored but
    # NOT FLOWN, so the fixture they named could not exist yet - and their whole
    # value was that their predictions were written DOWN BEFORE the flight.
    # RETIRED 2026-08-24 by B30 flight 1 (run
    # `2026-08-24_1536_B30-mun-minmus-transfer`, PASS attempt 1):
    # `mun-minmus-recorded` EXISTS, is registered in
    # `test_saveparse.RECORDED_FIXTURES`, and BOTH lanes are re-pinned off its
    # bytes (tree id, span envelope, both seam offsets, the segment-less tail),
    # so the two entries were deleted in the same commit - the discipline this
    # cell enforces, and it DID fire red first, for the second time.
    PENDING_FIXTURE_LANES = {
    }

    def test_the_pending_fixture_exemption_retires_itself(self):
        """The exemption's own guard, in BOTH directions. If the awaited fixture
        has appeared, the calibration pass owes this list an edit in the same
        commit that re-pins the spec - and if a listed spec has been re-pointed
        somewhere else, the entry is stale. Either way the reminder fires here
        rather than being remembered.

        With the map EMPTY this cell asserts nothing per-entry, which IS the
        healthy reading: the exemption is inert and the gate below is at full
        strength. It fires again the moment a future two-stage program lists a
        lane here."""
        saves = set(_fixture_save_names())
        arrived, moved = [], []
        for name, leaf in sorted(self.PENDING_FIXTURE_LANES.items()):
            path = os.path.join(SCENARIOS_DIR, name)
            self.assertTrue(os.path.isfile(path),
                            "PENDING_FIXTURE_LANES names a spec that does not exist: %s"
                            % name)
            with open(path, "rb") as fh:
                spec = tomllib.load(fh)
            actual = ((spec.get("fixture", {}) or {}).get("saveTemplate", "")
                      ).replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
            if actual != leaf:
                moved.append("%s now names %r, not the awaited %r" % (name, actual, leaf))
            if leaf in saves:
                arrived.append("%s: fixture %r now EXISTS - delete this entry"
                               % (name, leaf))
        self.assertEqual([], arrived + moved, "\n".join(arrived + moved))

    def test_every_spec_saveTemplate_names_a_real_fixture(self):
        manifest = _read_manifest()
        saves = set(_fixture_save_names())
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            path = os.path.join(SCENARIOS_DIR, name)
            with open(path, "rb") as fh:
                spec = tomllib.load(fh)
            template = (spec.get("fixture", {}) or {}).get("saveTemplate", "")
            if not template:
                continue
            if name in self.PENDING_FIXTURE_LANES:
                # Committed ahead of its fixture; guarded by the self-retiring
                # cell above rather than skipped silently.
                continue
            leaf = template.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
            self.assertIn(leaf, saves,
                          "%s: saveTemplate %r names no fixture directory"
                          % (os.path.basename(path), template))
            for ship in manifest.get(leaf, ()):
                self.assertTrue(
                    os.path.isfile(os.path.join(SHIPS_DIR, ship + hlib.SHARED_SHIP_SUFFIX)),
                    "%s: fixture %s declares missing library craft %r"
                    % (os.path.basename(path), leaf, ship))


class CommittedFixtureMirrorTests(unittest.TestCase):
    """No readable SNAPSHOT mirror may be committed under `fixtures/saves/`.

    Parsek writes a readable text mirror beside each authoritative sidecar
    (default-on diagnostics), and harvesting a produced save used to bring all
    three into the fixture tree. The two snapshot mirrors cost 334,023 lines over
    99 files - 1.85x the craft duplication the shared ship library removed -
    and are strictly derived from the committed `_vessel.craft` / `_ghost.craft`
    binaries: an offline decode reconstructs all 99 byte-for-byte, the binary being
    a strict superset of the text. Loading the fixture in KSP rewrites them - the
    vessel mirror through `ReconcileReadableSidecarMirrors`'s `AuthoritativeSidecar`
    fallback, the ghost mirror through load-time snapshot hydration (there is no
    ghost write-path fallback; `GhostSource` is only ever `InMemory`). So the
    observability is regenerable and the committed copies are pure redundancy.

    The TRAJECTORY mirror (`.prec.txt`) is deliberately NOT gated: four scenario
    headers cite values read straight out of one, so it stays committed. This test
    asserts BOTH halves, so a later sweep cannot quietly take the cited surface
    with it.

    TWO EXEMPTIONS, and both are facts about what Parsek WRITES rather than
    concessions. `RecordingSidecarStore.SaveRecordingFiles` writes a
    `_vessel.craft` only when the recording carries a `VesselSnapshot`, and there
    are two shapes where it legitimately does not:

      * a chain CONTINUATION recording (`chainIndex > 0`) is the same physical
        vessel as its chain head and reuses the head's snapshot. See
        `_chain_continuations_without_own_snapshot`.
      * a SAME-LAUNCH sibling: a recording of a vessel some other recording in
        the same fixture already snapshotted, correlated by
        `recordedVesselGuid`. See `_same_launch_recordings_without_own_snapshot`.

    Both are unioned in `_stems_exempt_from_vessel_snapshot`, and both are
    narrow in the same way: the exemption exists only when the snapshot the
    pruned mirror would be rebuilt from IS committed somewhere in that fixture."""

    SNAPSHOT_MIRROR_SUFFIXES = ("_vessel.craft.txt", "_ghost.craft.txt")

    def _committed(self):
        out = []
        for dirpath, _, filenames in os.walk(FIXTURE_SAVES_DIR):
            for fn in filenames:
                out.append(os.path.relpath(os.path.join(dirpath, fn),
                                           FIXTURE_SAVES_DIR).replace("\\", "/"))
        return out

    def test_no_snapshot_mirror_is_committed(self):
        offenders = sorted(f for f in self._committed()
                           if f.endswith(self.SNAPSHOT_MIRROR_SUFFIXES))
        self.assertEqual([], offenders,
                         "readable snapshot mirrors are derived from the committed "
                         "_vessel.craft / _ghost.craft binaries and must not be "
                         "committed; harvest_bdock_station.py prunes them")

    def test_the_authoritative_snapshot_binaries_are_still_committed(self):
        # The other half of the trade: the mirrors are safe to drop only BECAUSE
        # the binaries they are rebuilt from are here. A sweep that took both would
        # destroy the fixture, and this cell is what catches that.
        committed = self._committed()
        precs = [f for f in committed if f.endswith(".prec")]
        vessels = [f for f in committed if f.endswith("_vessel.craft")]
        self.assertTrue(precs, "no committed .prec trajectory sidecars remain")
        self.assertTrue(vessels, "no committed _vessel.craft snapshot sidecars remain")
        # Every recording with a trajectory must keep its authoritative snapshot,
        # UNLESS it is a chain continuation that never had one (see
        # _chain_continuations_without_own_snapshot).
        exempt = self._stems_exempt_from_vessel_snapshot(committed)
        for prec in precs:
            stem = prec[:-len(".prec")]
            if stem in exempt:
                continue
            self.assertIn(stem + "_vessel.craft", committed,
                          "%s lost its authoritative vessel snapshot" % prec)

    # A RECORDING node's own `chainId` / `chainIndex`, read off the first
    # occurrence inside each block. Deliberately a local regex parse rather than
    # a saveparse call: `RecordingRow` models neither field, and the two things
    # this cell needs to correlate are a FILE SET and a SAVE, so it already owns
    # both halves.
    _RECORDING_BLOCK_RE = re.compile(r"\n\t{3}RECORDING\n")
    _CHAIN_ID_RE = re.compile(r"^\s*chainId = (\S+)\s*$", re.MULTILINE)
    _CHAIN_INDEX_RE = re.compile(r"^\s*chainIndex = (\d+)\s*$", re.MULTILINE)
    _RECORDING_ID_RE = re.compile(r"^\s*recordingId = (\S+)\s*$", re.MULTILINE)
    _VESSEL_GUID_RE = re.compile(r"^\s*recordedVesselGuid = (\S+)\s*$",
                                 re.MULTILINE)

    def _stems_exempt_from_vessel_snapshot(self, committed):
        """The union of the two legitimate shapes; see the class docstring."""
        return (self._chain_continuations_without_own_snapshot(committed)
                | self._same_launch_recordings_without_own_snapshot(committed))

    def _chain_continuations_without_own_snapshot(self, committed):
        """Stems a missing `_vessel.craft` is LEGITIMATE for.

        Parsek writes ONE `_vessel.craft` per chain, on the chain HEAD: a
        continuation segment (`chainIndex > 0`) is the same physical vessel and
        reuses the head's snapshot, so it is written with a `_ghost.craft` and no
        vessel snapshot of its own. Measured on the source of `duna-one-recorded`
        (the first free-play harvest, whose mission runs a four-segment chain
        across a Kerbin -> Duna transfer): the recordings with no
        `_vessel.craft` on disk are EXACTLY the six with `chainIndex >= 1`, in
        three different trees. No file was deleted to make that true.

        The exemption is therefore a POSITIVE fact read out of the fixture's own
        save, not a name list, and it is deliberately narrow in both directions:
        a chain HEAD must still carry its snapshot, and a continuation is exempt
        only when SOME EARLIER MEMBER OF ITS OWN CHAIN carries one in the same
        fixture. A fixture that lost the head's snapshot therefore still reds -
        which is the claim this cell exists to make, since the pruned
        `_vessel.craft.txt` mirrors are rebuilt from exactly those binaries."""
        exempt = set()
        for name in sorted(os.listdir(FIXTURE_SAVES_DIR)):
            sfs = os.path.join(FIXTURE_SAVES_DIR, name, "persistent.sfs")
            if not os.path.isfile(sfs):
                continue
            with open(sfs, encoding="utf-8", errors="replace") as fh:
                text = fh.read()
            by_chain = {}
            rows = []
            for block in self._RECORDING_BLOCK_RE.split(text)[1:]:
                rid = self._RECORDING_ID_RE.search(block)
                cid = self._CHAIN_ID_RE.search(block)
                cix = self._CHAIN_INDEX_RE.search(block)
                if rid is None or cid is None or cix is None:
                    continue
                row = (rid.group(1), cid.group(1), int(cix.group(1)))
                rows.append(row)
                by_chain.setdefault(row[1], []).append(row)
            prefix = "%s/Parsek/Recordings/" % name
            for rid, cid, index in rows:
                if index <= 0:
                    continue
                # An earlier member of this chain must carry the snapshot both
                # this recording and the pruned mirrors are rebuilt from.
                head_has_snapshot = any(
                    other_index < index
                    and (prefix + other_rid + "_vessel.craft") in committed
                    for other_rid, _cid, other_index in by_chain[cid])
                if head_has_snapshot:
                    exempt.add(prefix + rid)
        return exempt

    def _same_launch_recordings_without_own_snapshot(self, committed):
        """Stems a missing `_vessel.craft` is legitimate for, shape two.

        `RecordingSidecarStore.SaveRecordingFiles` writes the sidecar only when
        the recording carries a `VesselSnapshot`, and a recording of a vessel
        another recording already snapshotted can legitimately be persisted
        without one - `ParsekFlight`'s vessel-gone defensive null and the
        auto-unreserve-crew pass both leave a transient in-memory null, and the
        store deliberately does NOT delete or re-create the sidecar for it (see
        the #278 follow-up comment at `RecordingSidecarStore.cs:63-76`).

        FOUND BY `depot-route-recorded` (B27, 2026-08-26). Its `0c8ec58d...` is a
        `Depot` recording carrying NO `chainId` at all - so the chain rule above
        cannot reach it - and it has only a `_ghost.craft` IN THE OPERATOR'S
        SOURCE SAVE, before any harvest ran. Nothing deleted it.

        THE CORRELATOR IS `recordedVesselGuid`, NOT `vesselPersistentId`, and
        that choice is the whole safety argument. A pid is craft-baked and
        reused verbatim on every launch of the same craft, so a pid match across
        two recordings is not proof they are the same physical object; the guid
        is assigned fresh per launch and IS launch-unique (see
        `VesselLaunchIdentity`). A same-guid sibling is therefore the same
        physical vessel, and its committed snapshot is exactly the one this
        recording's pruned `_vessel.craft.txt` mirror would be rebuilt from.

        NARROW IN THE SAME WAY AS THE CHAIN RULE: the exemption requires that
        SOME recording of that launch carries a committed `_vessel.craft` in the
        SAME fixture. A fixture that lost every snapshot of a launch still reds,
        which is the claim this cell exists to make. Recordings with no
        `recordedVesselGuid` (older captures) are never exempted here - they fall
        through to the chain rule or fail."""
        exempt = set()
        for name in sorted(os.listdir(FIXTURE_SAVES_DIR)):
            sfs = os.path.join(FIXTURE_SAVES_DIR, name, "persistent.sfs")
            if not os.path.isfile(sfs):
                continue
            with open(sfs, encoding="utf-8", errors="replace") as fh:
                text = fh.read()
            by_guid = {}
            for block in self._RECORDING_BLOCK_RE.split(text)[1:]:
                rid = self._RECORDING_ID_RE.search(block)
                guid = self._VESSEL_GUID_RE.search(block)
                if rid is None or guid is None:
                    continue
                by_guid.setdefault(guid.group(1), []).append(rid.group(1))
            prefix = "%s/Parsek/Recordings/" % name
            for _guid, rids in by_guid.items():
                snapshotted = [r for r in rids
                               if (prefix + r + "_vessel.craft") in committed]
                if not snapshotted:
                    continue
                for rid in rids:
                    if (prefix + rid + "_vessel.craft") not in committed:
                        exempt.add(prefix + rid)
        return exempt

    def test_every_committed_trajectory_keeps_its_readable_mirror(self):
        """`.prec.txt` is a LIVE TEST INPUT, and the gate has to be per-file.

        `OptimizerTransferCohesionTests` globs a fixture's Recordings dir for
        `*.prec.txt` (line 91) and sweeps every fixture recursively (line 530);
        `ReaimTransferSynthesizerTests:639` names one directly. Four scenario
        headers also quote values read out of one.

        An earlier version asserted only that at least ONE `.prec.txt` survived
        anywhere under fixtures/saves/. That could not trip until the last mirror
        in the repo went - deleting every mirror from two whole fixtures left it
        green. The real invariant is that the mirror set tracks the trajectory set,
        so this pairs them one to one."""
        committed = set(self._committed())
        missing = sorted(p[:-len(".prec")] for p in committed
                         if p.endswith(".prec") and (p + ".txt") not in committed)
        self.assertEqual([], missing,
                         "these recordings lost the readable trajectory mirror that "
                         "OptimizerTransferCohesionTests globs: %s" % (missing,))


class CommittedFixtureQuarantineTests(unittest.TestCase):
    """No fixture may carry `Parsek/Recordings/_quarantine`.

    `RecordingStore.OrphanQuarantineDirName` is where `CleanOrphanFiles` PARKS a
    sidecar whose recording the store could no longer resolve, instead of
    deleting it - non-destructive by design, so a save that has been played for a
    while accumulates every such file forever. It is unreachable by construction:
    the store's own directory scans are top-level-only and never descend into it
    (its doc comment says so), so no fixture consumer, no spec and no analyzer
    rule can read a byte of it.

    The sibling of `CommittedFixtureRewindSaveTests` below, and it exists for the
    same reason: harvest exhaust arrives by accident rather than by decision and
    nothing downstream notices. Measured on the first free-play harvest
    (`duna-one-recorded`, 2026-08-25): 12 MB over 375 files, LARGER than the
    recorded payload the fixture is for. `harvest_bdock_station.py` prunes it via
    `_PRUNE_RECORDINGS_SUBDIRS`; this cell is what keeps a future harvest from
    re-introducing it."""

    QUARANTINE_DIR_NAME = "_quarantine"

    def test_no_fixture_commits_a_quarantine_directory(self):
        offenders = []
        for dirpath, dirnames, _filenames in os.walk(FIXTURE_SAVES_DIR):
            for d in dirnames:
                if d == self.QUARANTINE_DIR_NAME:
                    offenders.append(
                        os.path.relpath(os.path.join(dirpath, d),
                                        FIXTURE_SAVES_DIR).replace("\\", "/"))
        self.assertEqual([], sorted(offenders),
                         "Parsek/Recordings/_quarantine is orphan-sweep exhaust "
                         "nothing reads (RecordingStore's scans never descend "
                         "into it); harvest_bdock_station.py prunes it")


class CommittedFixtureRewindSaveTests(unittest.TestCase):
    """No fixture may carry a legacy Rewind-to-LAUNCH quicksave, or a hint to one.

    `Parsek/Saves/parsek_rw_*.sfs` is `Recording.RewindSaveFileName` payload, and
    arrived in six recorded fixtures as harvest exhaust rather than by decision:
    137,355 lines over 7 files. Nothing automated reads it - no spec names one, and
    the analyzer's Inv9RewindPoint only does existence + parse checks.

    THE SEAM HALF OF THAT CLAIM NARROWED when `InvokeRewindToLaunch` landed. It used
    to read "no seam verb reaches it", true while `InvokeRewind` was the only rewind
    verb (that one is Rewind-to-SEPARATION and targets
    `Parsek/RewindPoints/<rpId>.sfs` through `RewindInvoker`, a different system).
    `InvokeRewindToLaunch` drives Rewind-to-LAUNCH, so it CAN reach this payload. The
    cells below are unaffected TODAY because the one committed lane that drives the
    verb (`GS-4-kerbalx-rewind-watch`, over the kx_rewind_watch mission's seam
    bridge) rewinds to a quicksave its OWN in-run recording captured, never a
    fixture-committed one - its fixture (`gs1-two-stage-pad`) carries no
    `parsek_rw_*` payload, which is exactly what these cells enforce. A spec that
    drives the verb against a fixture-committed rewind save must re-check both
    halves here, not just the RewindPoints one.

    The FILE and the HINT must go together. A `rewindSave = ` key pointing at a
    deleted file is a dangling reference: Inv9RewindPoint raises WARN for it, and
    escalates to FAIL when the owning recording is `CommittedProvisional`
    (`Inv9RewindPoint.cs:136`) - which `bdock-recorded` carries three of, so
    deleting the payload alone would turn the analyzer RED under the harness's
    Forbid fresh-save gate. This cell pins both halves.

    KNOWN, TOLERATED RESIDUAL: `bdock-recorded/Parsek/RewindPoints/rp_*.sfs` embed
    their own copies of the ParsekScenario, hints included, and are NOT edited -
    they are byte-sensitive payload (`RewindInvoker.PartLoaderPrecondition.Check`
    deep-parses their PART names) and `test_saveparse` pins `rewind_points: 3`.
    Those hints only surface if a run actually invokes a rewind against
    `bdock-recorded`, and neither consuming spec (`BDOCK-1-station-interceptor`,
    `H35-logistics-route-proof`) has an `InvokeRewind` step. A spec that adds one
    must re-check this."""

    def test_no_fixture_commits_a_rewind_to_launch_quicksave(self):
        offenders = []
        for dirpath, _, filenames in os.walk(FIXTURE_SAVES_DIR):
            norm = dirpath.replace("\\", "/")
            if norm.endswith("/Parsek/Saves"):
                offenders.extend(
                    os.path.relpath(os.path.join(dirpath, f),
                                    FIXTURE_SAVES_DIR).replace("\\", "/")
                    for f in filenames)
        self.assertEqual([], sorted(offenders),
                         "Parsek/Saves is harvest exhaust nothing automated reads; "
                         "harvest_bdock_station.py prunes it")

    def test_no_fixture_persistent_save_carries_a_dangling_rewind_hint(self):
        offenders = []
        for name in sorted(os.listdir(FIXTURE_SAVES_DIR)):
            path = os.path.join(FIXTURE_SAVES_DIR, name, "persistent.sfs")
            if not os.path.isfile(path):
                continue
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                for lineno, line in enumerate(fh, 1):
                    if re.match(r"^\s*rewindSave = parsek_rw_\w+\s*$", line):
                        offenders.append("%s:%d" % (name, lineno))
        self.assertEqual([], offenders,
                         "a rewindSave hint whose payload is not committed is a "
                         "dangling reference: Inv9RewindPoint WARNs, and FAILs when "
                         "the owning recording is CommittedProvisional")

    def test_the_rewind_points_payload_is_untouched(self):
        # The other half of the trade. RewindPoints are NOT exhaust - they are
        # deep-parsed payload, and a sweep that confused the two would break
        # bdock-recorded. `test_saveparse` pins the count at 3; this pins the files.
        rp_dir = os.path.join(FIXTURE_SAVES_DIR, "bdock-recorded", "Parsek", "RewindPoints")
        self.assertTrue(os.path.isdir(rp_dir), "bdock-recorded lost its RewindPoints")
        rps = sorted(f for f in os.listdir(rp_dir) if f.endswith(".sfs"))
        self.assertEqual(3, len(rps), "expected 3 committed rewind points, got %s" % (rps,))


class PartShowcaseWindowSyncTests(unittest.TestCase):
    """`S1.9-part-showcase-render` steers the clock into the part-showcase corpus's
    playback window with an ABSOLUTE `TimeJump ut=<N>`, and that number is only
    meaningful because THREE things outside the spec hold still: the staged
    fixture's UT, the showcase clip's start offset, and the clip's length. This
    cell re-derives the window from those three and asserts the spec's jump plan
    lands inside it.

    WHY IT EXISTS, in one sentence: reading run 1 (`2026-08-28_1945`) red with
    `spawned=0` because the lane never entered the window at all, and an absolute
    jump target is exactly the kind of number that goes quietly stale - a
    re-harvested `gloops-airshow`, or a change to the showcase clip shape, would
    move the window and leave the spec jumping into empty time. That failure costs
    a whole KSP boot to discover and looks identical to a render regression. Here
    it costs a local `discover -s lib`.

    Reads OUTSIDE `harness/` (the `CommittedBatchTallySourceSyncTests` /
    `test_the_c_sharp_writer_still_emits_pointcount` precedent): the committed
    fixture save and `Source/Parsek.Tests/SyntheticRecordingTests.cs`.

    COMMENT-STRIPPED BEFORE MATCHING. The C# is scanned with `//` line comments
    removed, because a rationale comment quoting `baseUT + 30` would otherwise be
    read as code - the same class of error that has bitten this repo before.
    Every extraction asserts it found EXACTLY ONE match, so an ambiguous parse
    reds rather than silently picking the first hit.
    """

    SPEC = "S1.9-part-showcase-render.toml"
    FIXTURE = "gloops-airshow"
    SHOWCASE_BUILDER = "BuildPartShowcaseRecording"

    @classmethod
    def setUpClass(cls):
        cls.spec_path = os.path.join(SCENARIOS_DIR, cls.SPEC)
        with open(cls.spec_path, "rb") as fh:
            cls.spec = tomllib.load(fh)
        cls.cs_path = os.path.join(REPO_ROOT, "Source", "Parsek.Tests",
                                   "SyntheticRecordingTests.cs")
        with open(cls.cs_path, encoding="utf-8-sig", errors="replace") as fh:
            cls.cs = fh.read()
        cls.fixture_sfs = os.path.join(HARNESS_ROOT, "fixtures", "saves",
                                       cls.FIXTURE, "persistent.sfs")

    # -- extraction helpers ---------------------------------------------------

    @staticmethod
    def _strip_line_comments(text):
        return re.sub(r"//[^\n]*", "", text)

    def _builder_body(self, name):
        """The brace-matched body of a C# method, comments stripped."""
        m = re.search(r"RecordingBuilder %s\s*\(" % re.escape(name), self.cs)
        self.assertIsNotNone(m, "%s not found in SyntheticRecordingTests.cs" % name)
        brace = self.cs.index("{", m.end())
        depth, i = 0, brace
        while True:
            c = self.cs[i]
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        return self._strip_line_comments(self.cs[brace:i])

    def _fixture_ut(self):
        with open(self.fixture_sfs, encoding="utf-8", errors="replace") as fh:
            body = fh.read()
        uts = re.findall(r"^\s*UT = ([0-9.]+)\s*$", body, re.M)
        self.assertEqual(1, len(uts),
                         "expected exactly one `UT = ` line in the %s fixture save, "
                         "found %d - the window derivation would be ambiguous"
                         % (self.FIXTURE, len(uts)))
        return float(uts[0])

    def _showcase_window(self):
        """(startUT, shortEndUT, unionEndUT) for the injected corpus."""
        body = self._builder_body(self.SHOWCASE_BUILDER)
        offs = re.findall(r"double t = baseUT \+ ([0-9]+);", body)
        self.assertEqual(1, len(offs), "clip start offset not uniquely parseable")
        offset = float(offs[0])
        # for (int i = 0; i <= 8; i++) b.AddPoint(t + (i * 3), ...)
        loops = re.findall(r"for \(int i = 0; i <= ([0-9]+); i\+\+\)", body)
        steps = re.findall(r"AddPoint\(t \+ \(i \* ([0-9]+)\)", body)
        self.assertEqual(1, len(loops), "clip point count not uniquely parseable")
        self.assertEqual(1, len(steps), "clip point spacing not uniquely parseable")
        short_len = float(loops[0]) * float(steps[0])
        rover = re.findall(
            r"SurfaceRoverDriveDurationSeconds = ([0-9.]+);", self.cs)
        self.assertEqual(1, len(rover), "rover clip duration not uniquely parseable")
        base = self._fixture_ut()
        start = base + offset
        return start, start + short_len, start + max(short_len, float(rover[0]))

    def _jumps(self):
        """(absolute entry targets, [deltaSeconds, ...]) from the spec's steps."""
        absolute, deltas = [], []
        for step in self.spec["driver"]["steps"]:
            if step.get("cmd") != "TimeJump":
                continue
            args = step.get("args") or {}
            if "ut" in args:
                absolute.append(float(args["ut"]))
            if "deltaSeconds" in args:
                deltas.append(float(args["deltaSeconds"]))
        return absolute, deltas

    # -- the assertions -------------------------------------------------------

    def test_the_derivation_actually_parsed(self):
        """Anti-vacuity floor: a cell that silently parsed nothing verifies
        nothing (the CommittedBatchTallySourceSyncTests rule)."""
        start, short_end, union_end = self._showcase_window()
        self.assertGreater(short_end, start)
        self.assertGreaterEqual(union_end, short_end)
        absolute, deltas = self._jumps()
        self.assertEqual(1, len(absolute),
                         "expected exactly one ABSOLUTE TimeJump (the entry jump); "
                         "every follow-up must use deltaSeconds so it is forward by "
                         "construction")
        self.assertTrue(deltas, "the staircase lost its deltaSeconds jumps")

    def test_the_entry_jump_lands_inside_every_row_window(self):
        start, short_end, _ = self._showcase_window()
        (entry,), _ = self._jumps()
        self.assertGreater(
            entry, start,
            "the entry TimeJump target %.2f is at or before the showcase window "
            "opens (%.2f) - this is the reading-run-1 failure exactly: the engine "
            "iterates every row, finds it renderable, and correctly spawns nothing"
            % (entry, start))
        self.assertLess(
            entry, short_end,
            "the entry TimeJump target %.2f is past the SHORT rows' end (%.2f), so "
            "the census could never reach the full corpus even if everything else "
            "worked" % (entry, short_end))

    def test_the_staircase_lands_past_the_union_window_end(self):
        _, _, union_end = self._showcase_window()
        (entry,), deltas = self._jumps()
        reach = entry + sum(deltas)
        self.assertGreater(
            reach, union_end,
            "the jump staircase reaches only UT %.2f but the corpus's union window "
            "ends at %.2f - playback would still be mid-window at FlushAndQuit, and "
            "the spec's MeshDestroyed token plus `requireBalanced = true` both rest "
            "on the windows ENDING inside the run" % (reach, union_end))

    def test_no_backward_jump_is_structurally_possible_on_the_staircase(self):
        """`deltaSeconds` must be strictly positive: `TestCommandTimeJump
        .IsForwardJump` refuses a backward or zero jump, and a refused step is an
        INVALID that reaches no verdict about the product."""
        _, deltas = self._jumps()
        for d in deltas:
            self.assertGreater(d, 0.0, "a non-positive deltaSeconds would be refused")
