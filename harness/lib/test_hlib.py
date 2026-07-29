"""Unit tests for hlib.py, the pure decision logic of the M-A5 harness.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Each test names the regression it guards (design Test Plan). Fixtures are the
REAL on-disk registry + sample specs where a placement/parse bug could only be
caught against a real file (mirroring test_provlib.py's RealProfileFileTests).
"""

import contextlib
import copy
import io
import os
import re
import shutil
import sys
import tempfile
import tomllib
import unittest

import hlib


HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REPO_ROOT = os.path.dirname(HARNESS_ROOT)
REGISTRY_PATH = os.path.join(HARNESS_ROOT, "coverage", "registry.toml")
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")
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
        # The mission branch `continue`s before the R5 guards, so both levels need
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
        # The other eleven names stay RESERVED (not v1-drivable).
        for verb in ("StartLoopPlayback", "StopPlayback", "EnterWatchMode", "SealSlot",
                     "StashSlot", "FlySlot", "RouteCommand", "MissionConfig",
                     "SimulateStockSwitchClick", "CrashAfterJournalPhase", "RunInvariantReport"):
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
        # way. Verb table is 19 implemented / 11 reserved (mirrors the C#
        # TestCommandVerbs counts).
        for verb in ("EvaExit", "EvaBoard", "PlantFlag", "EvaChuteDeploy"):
            with self.subTest(verb=verb):
                self.assertIn(verb, hlib.IMPLEMENTED_SEAM_VERBS)
                self.assertNotIn(verb, hlib.RESERVED_SEAM_VERBS)
        self.assertEqual(len(hlib.IMPLEMENTED_SEAM_VERBS), 19)
        self.assertEqual(len(hlib.RESERVED_SEAM_VERBS), 11)

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
                self.assertNotEqual(
                    ordinary, [],
                    "%s declares isolated = \"true\" but its pinned tally is ALSO "
                    "satisfiable on the ordinary batch path, so the isolated arg is "
                    "doing no work. Either the category is batch-allowed (drop the "
                    "arg) or the pin is too loose to tell the two paths apart."
                    % name)

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
    """The H7-H20 in-game batch-wiring group: 14 batch-only specs that each drive one
    previously-undriven [InGameTest] category over a committed fixture.

    The generic sweeps above already cover these specs as members of "every committed
    spec". This class asserts the properties that are specific to the GROUP and that
    a generic sweep cannot state: that the group is exactly this set (so a 15th
    arrives with its doc row rather than silently), that every member is non-vacuous
    when probed DIRECTLY rather than through validate_spec, and that each pinned
    total equals the count derived from the C# attributes for the category the spec
    actually drives.
    """

    # id -> (category, total). The total is the ATTRIBUTE-EXACT declaration count,
    # re-derived below from Source/Parsek rather than trusted from this table; the
    # table exists so a category rename reds HERE with both names in the message.
    GROUP = {
        "H7-trajectory-math":        ("TrajectoryMath", 8),
        "H8-spawn-rotation":         ("SpawnRotation", 10),
        "H9-incomplete-ballistic":   ("IncompleteBallistic", 8),
        "H10-finalize-backfill":     ("FinalizeBackfill", 7),
        "H11-pipeline-anchor":       ("Pipeline-Anchor", 7),
        "H12-switch-segment":        ("SwitchSegment", 6),
        "H13-ksp-api-smoke":         ("KSP", 6),
        "H14-corpus-data-health":    ("DataHealth", 4),
        "H15-corpus-ghost-visuals":  ("GhostVisuals", 4),
        "H16-corpus-spawn-health":   ("SpawnHealth", 3),
        "H17-flight-integration":    ("FlightIntegration", 4),
        "H18-pipeline-smoothing":    ("Pipeline-Smoothing", 4),
        "H19-recording-finalization": ("RecordingFinalization", 3),
        "H20-eva-spawn-position":    ("EvaSpawnPosition", 2),
    }

    # EMPTY, and deliberately kept rather than deleted. H20 was the one member that
    # carried the loose interim pin, because both its cells have run-time
    # InGameAssert.Skip guards and one of them (the walkback endpoint-overlap probe,
    # a live Physics.OverlapBox) is not decidable from source. It was re-flown ALONE
    # on 2026-07-27 so its log would survive the sweep, measured
    # `total=2 passed=2 failed=0 skipped=0`, and is now pinned whole like the rest.
    # So every member of the group pins its tally whole, and
    # test_the_interim_pin_member_is_declared_and_deliberately_loose now asserts that
    # NONE of them is loose - which is the guard worth having, because the interim
    # form accepts 1-of-N by design and re-introducing one silently would be a real
    # weakening. Add an id back here only alongside a written reason in the spec.
    #
    # NOTE the asymmetry this leaves: for the other 13, skipped=0 is DERIVABLE from
    # the attributes plus a reachable-Skip scan. For H20 it is MEASURED only - the
    # attributes put a floor of 0 on it and nothing more, and a fixture change that
    # moves the parent's collider geometry can legitimately make it skip. Only H18
    # among the 13 has a comparable caveat (its AssertHandlerRegistered helper skips
    # if a KSP version renames EventData<T>'s internal `events` field, unreachable on
    # the pinned 1.12.5).
    INTERIM_PIN_IDS = set()

    # Every committed spec whose id matches this is an H-SERIES batch spec.
    # Membership is DISCOVERED from disk and then compared for set equality against
    # GROUP, which is what makes "a 15th spec arrives with its doc row" true: an
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
        self.assertEqual(14, len(self.GROUP),
                         "the H7-H20 group is 14 specs; if it genuinely changed size, "
                         "update this floor AND the counts in "
                         "docs/dev/autotest-ingame-category-inventory.md and "
                         "docs/dev/autotest-status.md in the same commit")
        self.assertEqual(len(self.GROUP), len(self.specs),
                         "GROUP names %d specs but only %d were loaded from %s - the "
                         "rest of this class would assert over the missing ones' "
                         "absence" % (len(self.GROUP), len(self.specs), SCENARIOS_DIR))

    def test_the_group_is_exactly_the_committed_set(self):
        # SET EQUALITY against what is on disk, not an intersection: this fires both
        # when a listed member is removed/renamed AND when a new H-series spec is
        # committed without being added here.
        self.assertEqual(sorted(self.on_disk), sorted(self.GROUP),
                         "the H7-H20 specs on disk differ from the table in this "
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
                category, expected_total = self.GROUP[sid]
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                self.assertTrue(pin.statically_checkable, sid)
                self.assertEqual(pin.category, category)
                self.assertEqual(pin.scene, "FLIGHT",
                                 "%s: every member of this group loads the "
                                 "gloops-airshow Focusable route" % sid)
                derived = hlib.derive_batch_tally(self.decls, category, "FLIGHT")
                self.assertEqual(derived.total, expected_total,
                                 "%s: the source now declares %d %s test(s), not %d"
                                 % (sid, derived.total, category, expected_total))
                self.assertEqual(pin.total, derived.total)
                self.assertEqual([], hlib.batch_tally_pin_mismatches(pin, self.decls))

    def test_whole_tally_members_pin_a_derivable_zero_skip(self):
        # What this actually checks, stated precisely because it is weaker than it
        # looks for one member: nothing the ATTRIBUTES control forces a skip at
        # FLIGHT, so the attribute-derived skipped FLOOR is 0 and the pinned tally
        # must agree with it. For 13 of the 14 that floor plus a reachable-Skip scan
        # makes skipped=0 genuinely derivable. For H20 the floor is all the
        # attributes give - its skipped=0 is MEASURED off a live run - so this cell
        # confirms consistency, not derivability, there. If a member later gains a
        # scene-mismatched or AllowBatchExecution=false declaration, this reds
        # pointing at the member.
        for sid, spec in sorted(self.specs.items()):
            if sid in self.INTERIM_PIN_IDS:
                continue
            with self.subTest(spec=sid):
                category, _ = self.GROUP[sid]
                derived = hlib.derive_batch_tally(self.decls, category, "FLIGHT")
                self.assertEqual(
                    0, derived.attribute_skipped,
                    "%s pins skipped=0 but the attributes force %d skip(s): %s %s"
                    % (sid, derived.attribute_skipped,
                       derived.scene_skipped_members, derived.batch_skipped_members))
                lc = (spec.get("expectations", {}) or {}).get("logContracts", {}) or {}
                pin = hlib.resolve_batch_tally_pin(lc.get("required", []) or [])
                self.assertEqual((pin.passed, pin.failed, pin.skipped),
                                 (derived.total, 0, 0), sid)

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
                category, total = self.GROUP[sid]
                derived = hlib.derive_batch_tally(self.decls, category, "FLIGHT")
                skipped = derived.attribute_skipped
                real = (prefix + "BATCH_COMPLETE v1 total=%d passed=%d failed=0 "
                        "skipped=%d category=%s scene=FLIGHT"
                        % (total, total - skipped, skipped, category))
                vacuous = (prefix + "BATCH_COMPLETE v1 total=%d passed=0 failed=0 "
                           "skipped=%d category=%s scene=FLIGHT"
                           % (total, total, category))
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
        corpus_backed = {"H14-corpus-data-health", "H15-corpus-ghost-visuals",
                         "H16-corpus-spawn-health", "H17-flight-integration"}
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
        # THE THESIS CELL. An isolated spec earns its arg only if the ordinary
        # admission filter would execute ZERO of its category's tests -- otherwise
        # the arg is decoration and the spec proves nothing about R5. Derived from
        # the attributes both ways and compared.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                category, _ = self.GROUP[sid]
                scene = hlib.resolve_batch_tally_pin(
                    ((spec.get("expectations", {}) or {})
                     .get("logContracts", {}) or {}).get("required", []) or []).scene
                ordinary = hlib.derive_batch_tally(self.decls, category, scene)
                isolated = hlib.derive_batch_tally(self.decls, category, scene,
                                                   isolated=True)
                self.assertEqual(
                    0, ordinary.executable,
                    "%s drives %s with isolated = \"true\", but %d of its tests are "
                    "batch-eligible on the ORDINARY path too. The arg is not doing "
                    "any work here; drop it, or drive a category that needs it."
                    % (sid, category, ordinary.executable))
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
                self.assertEqual((pin.passed, pin.failed, pin.skipped),
                                 (derived.total, 0, 0), sid)

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

                real = line(iso.total, iso.total - iso.attribute_skipped, 0,
                            iso.attribute_skipped)
                vacuous = line(iso.total, 0, 0, iso.total)
                non_isolated = line(ord_.total,
                                    ord_.total - ord_.attribute_skipped, 0,
                                    ord_.attribute_skipped)

                self.assertRegex(real, pat,
                                 "%s: its own pin does not match the line the runner "
                                 "would print for the isolated derivation" % sid)
                self.assertNotRegex(vacuous, pat, "%s: its pin ACCEPTS the vacuous "
                                                  "line" % sid)
                self.assertNotRegex(
                    non_isolated, pat,
                    "%s: its pin ACCEPTS the line the ORDINARY (non-isolated) path "
                    "would print, so a run that silently lost the isolated arg would "
                    "read GREEN. That contrast is the whole proof of R5." % sid)
                parsed = hlib.parse_batch_complete_line(real)
                self.assertIsNotNone(parsed, sid)
                self.assertEqual(parsed.category, category)

    @staticmethod
    def _fixture_flight_problems(sfs_path):
        """Why ``sfs_path``'s ACTIVE vessel cannot fly a staging test. Empty = fine.

        ONE implementation, used by both the real cell and the positive control
        below. Duplicating the checks instead left the engine floor weakenable to
        ">= 0" with the control still green, because the control was only asserting
        properties OF the control fixture rather than running the predicate ON it.
        """
        problems = []
        with open(sfs_path, encoding="utf-8", errors="replace") as fh:
            body = fh.read()
        active = re.search(r"activeVessel = (\d+)", body)
        if active is None:
            return ["no activeVessel declared"]
        idx = int(active.group(1))
        blocks = re.split(r"^\s*VESSEL\s*$", body, flags=re.M)[1:]
        if len(blocks) <= idx:
            return ["activeVessel=%d but only %d VESSEL nodes" % (idx, len(blocks))]
        # Scoped to the ACTIVE vessel, not the whole file: a fixture with an
        # engine-bearing ORBITER and an engineless PRELAUNCH active vessel passes a
        # file-wide substring check while producing exactly the all-skipped tally
        # this predicate exists to prevent. b2-lko-craft really does carry two.
        vessel = blocks[idx]
        if "sit = PRELAUNCH" not in vessel:
            problems.append("active vessel (index %d) is not PRELAUNCH" % idx)
        if vessel.count("name = ModuleEngines") < 1:
            problems.append("active vessel (index %d) carries NO ModuleEngines" % idx)
        return problems

    def test_each_pins_an_isolated_path_only_proof_token(self):
        # The tally alone cannot distinguish "the isolated route ran" from "the
        # category happened to be batch-allowed". `Using batch baseline slot ... for
        # N restore-after-run test(s)` is emitted ONLY by
        # PrepareBatchFlightRestoreExecution, which RunCategory never calls, so it is
        # independent proof - but only if N is pinned as a LITERAL. A `[0-9]+` there
        # is satisfied by a batch that admitted a different population.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                _, total = self.GROUP[sid]
                req = ((spec.get("expectations", {}) or {})
                       .get("logContracts", {}) or {}).get("required", []) or []
                slot = [r for r in req if "batch baseline slot" in r]
                self.assertEqual(1, len(slot),
                                 "%s must pin the baseline-slot line - it is the only "
                                 "token that proves the ISOLATED entry point ran" % sid)
                self.assertIn("for %d restore-after-run" % total, slot[0],
                              "%s must pin the restore count as a LITERAL (%d), not a "
                              "class: a class accepts a batch that admitted a "
                              "different population" % (sid, total))

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

    def test_each_pins_a_zero_recordings_count(self):
        # For an isolated spec this doubles as the CAMPAIGN-ISOLATION assertion: the
        # tests create real trees mid-run and the batch teardown reverts
        # persistent.sfs from the pre-batch .bak, so a non-zero count means the
        # restore contract did not hold - the property R5 is betting on.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                fixture = spec.get("fixture", {}) or {}
                count = ((spec.get("expectations", {}) or {})
                         .get("recordings", {}) or {}).get("count", {}) or {}
                if fixture.get("injectedRecordings") == "none":
                    self.assertEqual(
                        {"min": 0, "max": 0}, count,
                        "%s injects nothing, so the produced save must carry no "
                        "recordings; a window here would accept a leaked tree or a "
                        "failed baseline revert" % sid)
                else:
                    self.assertGreater(count.get("min", 0), 0, sid)

    def test_the_fixture_predicate_rejects_the_known_engineless_host(self):
        # POSITIVE CONTROL: run the predicate ON the known-bad fixture and require
        # it to complain. Without this, weakening the engine floor makes the real
        # cell vacuous and nothing notices. gloops-airshow is the 14 ordinary
        # H-specs' host and its active vessel is a 1-part engineless mk1-capsule.
        problems = self._fixture_flight_problems(
            os.path.join(HARNESS_ROOT, "fixtures/saves/gloops-airshow/persistent.sfs"))
        self.assertNotEqual(
            [], problems,
            "the fixture predicate must REJECT gloops-airshow - its active vessel is "
            "engineless, so a staging test self-skips there and the batch prints the "
            "same all-skipped tally the isolated arg exists to rule out. If this "
            "passes, the predicate has been weakened into a tautology")
        self.assertTrue(any("ModuleEngines" in p for p in problems), problems)

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
        # This cannot prove the craft has the TWR to clear 80 m in 30 s (only a live
        # run does that, and H21 has), but it catches the engineless case statically.
        for sid, spec in sorted(self.specs.items()):
            with self.subTest(spec=sid):
                template = (spec.get("fixture", {}) or {}).get("saveTemplate", "")
                sfs = os.path.join(HARNESS_ROOT, template, "persistent.sfs")
                self.assertTrue(os.path.isfile(sfs),
                                "%s names fixture %r but %s does not exist"
                                % (sid, template, sfs))
                self.assertEqual(
                    [], self._fixture_flight_problems(sfs),
                    "%s's fixture %s cannot fly this category" % (sid, template))


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
        # route/rewind/loop stay reserved until their verifiers land (M-C2).
        exp = {"route": {"x": 1}, "recordings": {"count": {"min": 0, "max": 0}}}
        r = hlib.evaluate_expectations(exp, 0, "")
        self.assertIn("route", r.reserved)

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
        # gate's arithmetic.
        self.assertEqual({}, hlib.count_anomaly_tokens(self._raise("icon-teleport", 3)))

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
    complement of that layer, REPORT-ONLY until a scenario arms it."""

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

    def test_no_committed_spec_arms_it(self):
        # The HARD SAFETY PROPERTY: this scan cannot move any nightly verdict, because
        # nothing declares the block. Arming one is an operator decision taken after
        # reading the report-only counts off a few green runs.
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            with open(os.path.join(SCENARIOS_DIR, name), "rb") as fh:
                spec = tomllib.load(fh)
            if hlib.UNITY_EXCEPTIONS_BLOCK in (spec.get("expectations") or {}):
                armed.append(name)
        self.assertEqual([], armed, "a committed spec armed the report-only scan")

    def test_over_budget_classifies_parsek_fail(self):
        d, v = _clean_pass_facts()
        v["unity_exceptions_over_budget"] = True
        verdict = hlib.classify_verdict(d, v, {"bugId": ""}, 1, "once")
        self.assertEqual(("PARSEK-FAIL", "unity-exception"),
                         (verdict.verdict, verdict.subkind))


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
        # reason=icon-teleport must not satisfy a search for a different token, and
        # the field is read whole rather than by substring.
        line = ("[Parsek][INFO][MapRenderTrace] phase=Anomaly surface=ProtoIcon pid=1"
                " reason=icon-teleport TELEPORT dPos=900m")
        self.assertEqual([], hlib.grep_anomaly_tokens(line))

    def test_hits_come_back_in_registry_order_not_emit_order(self):
        log = "\n".join(
            "[Parsek][INFO][MapRenderTrace] phase=Anomaly pid=1 reason=" + t
            for t in ("ledger-vs-truth", "parity-drift", "line-blink"))
        self.assertEqual(["line-blink", "parity-drift", "ledger-vs-truth"],
                         hlib.grep_anomaly_tokens(log))

    def test_unlisted_reasons_are_reported_not_gated(self):
        # The known ANOMALY_TOKENS drift: these are RAISED by the mod and absent
        # from the harness set, so they must surface without changing the verdict.
        log = "\n".join(
            "[Parsek][INFO][MapRenderTrace] phase=Anomaly pid=1 reason=" + t
            for t in ("icon-teleport", "gap-vs-retire", "parity-drift"))
        self.assertEqual(["parity-drift"], hlib.grep_anomaly_tokens(log))
        self.assertEqual(["gap-vs-retire", "icon-teleport"],
                         hlib.unlisted_anomaly_reasons(log))

    def test_icon_jump_is_retired_and_icon_teleport_is_still_only_reported(self):
        # HALF THE DRIFT IS CLOSED (2026-07-29), and this cell pins WHICH half.
        # CLOSED: `icon-jump` no longer sits in the gated set advertising coverage of
        # a raise that does not exist. It is RETIRED to ANOMALY_TOKENS_DEAD, and the
        # two tuples are disjoint.
        self.assertNotIn("icon-jump", hlib.ANOMALY_TOKENS)
        self.assertIn("icon-jump", hlib.ANOMALY_TOKENS_DEAD)
        self.assertEqual(set(), set(hlib.ANOMALY_TOKENS) & set(hlib.ANOMALY_TOKENS_DEAD))
        # STILL OPEN: the real raise (`icon-teleport`) is REPORT-ONLY. Gating it is
        # the per-token call the todo-doc entry defers - it needs a measurement of
        # whether it fires on a green S1.4, which only a nightly with the
        # unlistedReasons channel can supply.
        line = ("[Parsek][INFO][MapRenderTrace] phase=Anomaly surface=ProtoIcon pid=1"
                " reason=icon-teleport TELEPORT dPos=900m = 42x expected(21m)")
        self.assertEqual([], hlib.grep_anomaly_tokens(line))
        self.assertEqual(["icon-teleport"], hlib.unlisted_anomaly_reasons(line))

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
    """`autotest-ingame-category-inventory.md`'s 97-row table says of itself "Do NOT
    hand-edit the table: re-derive it" - and shipped with nothing enforcing that.

    The gap that leaves: add one `[InGameTest(Category = "Rewind")]` and NOTHING
    reds. `CommittedBatchTallySourceSyncTests` does not (Rewind is unpinned),
    `IngameBatchWiringGroupTests` does not (Rewind is not in the H7-H20 group), and
    the Rewind row, the 539 / 97 totals repeated across four documents, and the
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
            "539 / 97 totals and the A/B/C sums in the same commit")

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
        self.assertIn("**97 categories / %d declarations**" % stated_decls, body,
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
_REASON_ARG_INDEX = {"MapRenderTrace": 4, "LedgerTrace": 2, "": 4}


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
    """The ungated-reason list is the DECISION INPUT for the deferred
    ANOMALY_TOKENS reconciliation, so it must not be hand-maintained prose.

    The 2026-07-26 first pass listed 5 of the 9 ungated reasons - it missed the
    four cutover-hardening raises (clock-not-ready / retire-not-held /
    anchor-resolve-fail / factory-parity), which reach EmitAnomaly through thin
    MapRenderTrace wrappers rather than at the guard site. An incomplete
    enumeration understates a fail-open, which is exactly the thing the list
    exists to size, so it is now derived from the C# source here."""

    def setUp(self):
        self.assertTrue(os.path.isdir(PARSEK_SRC_DIR),
                        "Source/Parsek must be present: this gate is a source scan, "
                        "and skipping it would make the enumeration unmeasured again")
        self.raised = _production_anomaly_raises()

    def test_scanner_sees_the_known_raise_sites(self):
        # Anti-vacuity for the scanner itself: an empty / near-empty walk would make
        # every set assertion below trivially true.
        self.assertGreaterEqual(len(self.raised), 15)
        self.assertIn("Source/Parsek/MapRenderProbe.cs:753",
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

    def test_the_ungated_count_is_nine_not_five(self):
        self.assertEqual(9, len(hlib.ANOMALY_REASONS_RAISED_UNGATED))
        for reason in ("clock-not-ready", "retire-not-held", "anchor-resolve-fail",
                       "factory-parity"):
            self.assertIn(reason, {r for r, _ in hlib.ANOMALY_REASONS_RAISED_UNGATED},
                          "the four wrapper-routed raises the first pass missed")
            self.assertIn(reason, self.raised)

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

    def test_status_doc_reports_the_same_nine(self):
        # autotest-status.md is declared the single status authority for this
        # system, and its gate-0 list is what a reader acts on. It said FIVE for
        # one commit; keep it tied to the source-derived tuple.
        with open(AUTOTEST_STATUS_DOC, encoding="utf-8") as fh:
            body = fh.read()
        for reason, _ in hlib.ANOMALY_REASONS_RAISED_UNGATED:
            self.assertIn("`%s`" % (reason,), body,
                          "gate 0 omits the ungated reason %s" % (reason,))
        self.assertNotIn("five further reasons are ungated", body.lower())

    def test_todo_doc_table_lists_every_raised_reason(self):
        # The todo-doc table is the DECISION INPUT for the deferred reconciliation
        # (the PR that defers it says so explicitly), so an incomplete table is the
        # defect, not a cosmetic slip.
        with open(TODO_DOC, encoding="utf-8") as fh:
            body = fh.read()
        start = body.index("## The harness anomaly token set has drifted")
        entry = body[start:body.index("\n## ", start + 10)]
        for reason in sorted(self.raised):
            self.assertIn("| `%s` |" % (reason,), entry,
                          "the ground-truth table omits %s" % (reason,))

    def test_documented_producers_are_real_file_line_pairs(self):
        # The guard site (where the decision is made) is what the table names; for
        # the four wrapper-routed raises that is NOT the EmitAnomaly line, so this
        # checks the cited file:line rather than reusing the scanner's output.
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

    def test_unknown_verb_does_not_gate(self):
        # Opposite fail-safe direction from SEAM_VERB_TAIL_ROLE, deliberately: an
        # unrecognised verb is a spec fault validate_spec already rejects, and
        # gating on a vocabulary miss would red runs for a typo rather than for an
        # outcome.
        for unknown in ("StartLoopPlayback", "SomeFutureVerb", "", None):
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

    def test_read_only_verbs_are_inert_not_mislabelled_mutating(self):
        for verb in ("RecordingState", "MissionMark"):
            self.assertEqual(hlib.TAIL_ROLE_INERT, hlib.seam_verb_tail_role(verb), verb)

    def test_unknown_verb_fails_safe_to_world_mutating(self):
        # A verb this table has never heard of (a RESERVED verb driven by a spec that
        # slipped validation, a typo, or a verb implemented on a branch that has not
        # added its row yet) must be presumed to DO something, so the unmet tail skips
        # it. EvaChuteDeploy WAS this case until PR #1348 merged it, which is exactly
        # what the totality cell above now keeps from recurring silently.
        for unknown in ("StartLoopPlayback", "SomeFutureVerb", ""):
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
        # ONE entry declaring BOTH funds and reputation, which stock logs as TWO award
        # lines at the same seqKey - was swallowed whole by whichever line matched
        # first, stranding its sibling as permanently "unexpected".
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

    def test_no_committed_spec_arms_the_capture_cross_check(self):
        # NIT 1 / the HARD SAFETY PROPERTY for gap 1, mirroring the unityExceptions
        # cell: the escalation path exists but nothing walks it yet.
        armed = []
        for name in sorted(n for n in os.listdir(SCENARIOS_DIR) if n.endswith(".toml")):
            ledger = ((load_spec(name).get("expectations", {}) or {}).get("ledger") or {})
            if hlib.capture_cross_check_gates(ledger):
                armed.append(name)
        self.assertEqual([], armed, "a committed spec armed captureCrossCheck")

    def test_gate_mode_resolution_and_validation(self):
        self.assertFalse(hlib.capture_cross_check_gates(None))
        self.assertFalse(hlib.capture_cross_check_gates({}))
        self.assertFalse(hlib.capture_cross_check_gates({"captureCrossCheck": "report"}))
        self.assertTrue(hlib.capture_cross_check_gates({"captureCrossCheck": "gate"}))
        self.assertEqual([], hlib.validate_ledger_expectations({"captureCrossCheck": "gate"}))
        errs = hlib.validate_ledger_expectations({"captureCrossCheck": "GATE"})
        self.assertTrue(any("captureCrossCheck" in e for e in errs))


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
        # The five OTHER sidecar-tracked settings must stay UNSET so the fixture's
        # own GameParameters keep governing them (a stored value would override
        # every save on the instance, which is the bug being fixed).
        values = hlib.parse_settings_sidecar(hlib.render_settings_sidecar_baseline())
        for key in ("writeReadableSidecarMirrors", "autoBackupExistingSaves",
                    "showCommittedFutureOverlays", "blockCommittedActions",
                    "showRouteLines"):
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
