"""Unit tests for hlib.py, the pure decision logic of the M-A5 harness.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Each test names the regression it guards (design Test Plan). Fixtures are the
REAL on-disk registry + sample specs where a placement/parse bug could only be
caught against a real file (mirroring test_provlib.py's RealProfileFileTests).
"""

import copy
import os
import sys
import tomllib
import unittest

import hlib


HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REGISTRY_PATH = os.path.join(HARNESS_ROOT, "coverage", "registry.toml")
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")

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
        for name in ("B1-pad-hop.toml", "B2-lko-ascent.toml"):
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
        # A spec step using an M-C1 verb is no longer flagged RESERVED / unknown.
        for verb in ("InvokeRewind", "AnswerMergeDialog", "KscAction", "TimeJump"):
            with self.subTest(verb=verb):
                def m(s):
                    s["driver"]["steps"].insert(1, {"cmd": verb, "expect": "OK"})
                v = self._reject(m)
                self.assertFalse(any(verb in e for e in v.errors),
                                 "%s wrongly flagged: %s" % (verb, list(v.errors)))

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

    def test_cadence_weekly_excludes_operator(self):
        # Weekly is the widest cadence yet the operator-tier spec E is NOT selected:
        # operator specs run only under an explicit --tier/--id, never a cadence.
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
        got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence daily")]
        self.assertEqual(got, ["A"])

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


class AnomalySweepTests(unittest.TestCase):
    """Guards N2: an unallowed Tier-C line reds; a known-benign token in
    allowedAnomalies is tolerated; a scenario cannot invent a new anomaly."""

    def test_unallowed_hit_returned(self):
        hits = hlib.evaluate_anomaly_sweep(["icon-jump"], [])
        self.assertEqual(hits, ["icon-jump"])

    def test_allowed_token_tolerated(self):
        hits = hlib.evaluate_anomaly_sweep(["polyline-orbit-overlap"], ["polyline-orbit-overlap"])
        self.assertEqual(hits, [])

    def test_unknown_token_ignored(self):
        hits = hlib.evaluate_anomaly_sweep(["not-a-real-anomaly"], [])
        self.assertEqual(hits, [])


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
    be admitted as a manifest amount (it would double-count against the seed)."""

    def test_award_with_ut_stamped_parsek_neighbor(self):
        log = (
            "[LOG] [Parsek][INFO][Recorder] tick ut=12345.6\n"
            "[LOG] ContractSystem: contract Foo completed guid=g-1 funds=50000\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured))
        c = res.captured[0]
        self.assertEqual("funds", c.facet)
        self.assertEqual(50000.0, c.amount)
        self.assertEqual("g-1", c.contract_guid)
        self.assertEqual(12345.6, c.ut)          # correlated to the nearest UT line.
        self.assertEqual("contract-complete", c.kind)

    def test_science_award_carries_subject_and_delta(self):
        log = ("[LOG] [Parsek][INFO] ut=10.0\n"
               "[LOG] ResearchAndDevelopment: science subject=crewReport@KerbinSrf delta=8.5\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured))
        c = res.captured[0]
        self.assertEqual("science", c.facet)
        self.assertEqual(8.5, c.amount)
        self.assertEqual("crewReport@KerbinSrf", c.subject_id)

    def test_null_ut_when_no_parsek_neighbor(self):
        # No UT-stamped [Parsek] line precedes the award -> ut=None, seq=line ordinal
        # (the seqKey), still ordered + deduped deterministically (design ~394).
        log = "[LOG] ContractSystem: contract Foo completed guid=g-1 funds=50000\n"
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured))
        c = res.captured[0]
        self.assertIsNone(c.ut)
        self.assertEqual(0, c.seq)               # first (0th) log line.
        self.assertEqual(c.seq, c.seq_key)       # seqKey falls back to the ordinal.

    def test_balance_line_rejected_not_captured(self):
        # A running-BALANCE line is inadmissible (a post-grant balance would
        # double-count against the seed, design ~398). It is counted, never captured.
        log = ("[LOG] [Parsek][INFO] ut=10.0\n"
               "[LOG] ResearchAndDevelopment: total science 128.0\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(0, len(res.captured))
        self.assertEqual(1, res.rejected_balance)

    def test_scene_reload_reemit_same_seqkey_dedupes(self):
        # The same award line re-emitted at the SAME seqKey (no new UT between) is ONE
        # effect after dedupe (design edge 2).
        log = ("[LOG] [Parsek][INFO] ut=100.0\n"
               "[LOG] ContractSystem: contract A completed guid=g1 funds=1000\n"
               "[LOG] ContractSystem: contract A completed guid=g1 funds=1000\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(2, len(res.captured))            # both matched before dedupe
        deduped = hlib.dedupe_captured_awards(res.captured)
        self.assertEqual(1, len(deduped))                 # collapsed to one effect

    def test_genuine_second_award_at_distinct_seqkey_survives(self):
        # A genuine second identical award at a DISTINCT seqKey (a new UT between)
        # survives the dedupe (design edge 2): the seqKey is part of the dedupe key.
        log = ("[LOG] [Parsek][INFO] ut=100.0\n"
               "[LOG] ContractSystem: contract A completed guid=g1 funds=1000\n"
               "[LOG] [Parsek][INFO] ut=200.0\n"
               "[LOG] ContractSystem: contract A completed guid=g1 funds=1000\n")
        res = hlib.parse_stock_award_lines(log)
        deduped = hlib.dedupe_captured_awards(res.captured)
        self.assertEqual(2, len(deduped))
        self.assertEqual({100.0, 200.0}, {c.ut for c in deduped})

    def test_empty_log_captures_nothing(self):
        res = hlib.parse_stock_award_lines("")
        self.assertEqual(0, len(res.captured))
        self.assertEqual(0, res.rejected_balance)

    def test_parsek_tagged_line_not_captured_as_award(self):
        # Review SF7: a [Parsek] diagnostic line that MENTIONS a stock emitter class +
        # delta= (e.g. the ledger tracer's ledger-vs-truth lines) must NOT false-capture
        # as a stock award, which would false-red an empty-manifest B10. A genuine
        # (untagged) stock line on the next line still captures, and the [Parsek] line's
        # ut= stamp still drives the UT correlation of that genuine award.
        log = ("[LOG] [Parsek][VERBOSE][LedgerTrace] ut=42.0 ResearchAndDevelopment science delta=999.0\n"
               "[LOG] ResearchAndDevelopment: science subject=crewReport@X delta=8.5\n")
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(1, len(res.captured))          # only the genuine stock line
        c = res.captured[0]
        self.assertEqual(8.5, c.amount)
        self.assertEqual("crewReport@X", c.subject_id)
        self.assertEqual(42.0, c.ut)                     # UT still read from the [Parsek] stamp
        # The 999.0 from the [Parsek] line was never admitted as an award amount.
        self.assertNotIn(999.0, [a.amount for a in res.captured])

    def test_parsek_tagged_contract_line_not_captured(self):
        # A [Parsek] line quoting a ContractSystem funds= award (a Parsek log echoing a
        # stock event) is not captured either; capture is stock-native lines only.
        log = "[LOG] [Parsek][INFO][Recorder] ContractSystem contract Foo completed guid=g funds=50000\n"
        res = hlib.parse_stock_award_lines(log)
        self.assertEqual(0, len(res.captured))


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
