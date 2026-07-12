"""Unit tests for hlib.py, the pure decision logic of the M-A5 harness.

Runnable with the stdlib runner only (NO pytest, NO KSP, NO network)::

    python -m unittest discover -s harness/lib

Each test names the regression it guards (design Test Plan). Fixtures are the
REAL on-disk registry + sample specs where a placement/parse bug could only be
caught against a real file (mirroring test_provlib.py's RealProfileFileTests).
"""

import copy
import os
import tomllib
import unittest

import hlib


HERE = os.path.dirname(os.path.abspath(__file__))
HARNESS_ROOT = os.path.dirname(HERE)
REGISTRY_PATH = os.path.join(HARNESS_ROOT, "coverage", "registry.toml")
SCENARIOS_DIR = os.path.join(HARNESS_ROOT, "scenarios")


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
        def m(s):
            s["driver"]["steps"].insert(1, {"cmd": "InvokeRewind", "expect": "OK"})
        v = self._reject(m)
        self.assertFalse(v.ok)
        self.assertTrue(any("InvokeRewind" in e and "RESERVED" in e for e in v.errors))

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

    def test_cadence_weekly_is_all(self):
        got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence weekly")]
        self.assertEqual(got, ["A", "B", "C", "D"])

    def test_cadence_daily(self):
        got = [s["id"] for s in hlib.select_scenarios(self.SPECS, "--cadence daily")]
        self.assertEqual(got, ["A"])

    def test_unknown_kind_empty(self):
        self.assertEqual(hlib.select_scenarios(self.SPECS, "--bogus x"), [])


if __name__ == "__main__":
    unittest.main()
