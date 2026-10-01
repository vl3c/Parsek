"""Unit tests for mutledger (the ledger-oracle mutation checker, phase 2 PR 2).

Synthetic seeds, careerSave blocks and logs shaped like the archived L1 / CL-2
artifacts, so nothing here depends on an archive being present. Each perturbation
type has a positive cell (the edit kills on a correct oracle) and a negative cell
(the same edit kept inside the tolerance leaves the verifier green). The vacuity
cells break one oracle path at a time by monkeypatching it and assert the checker
names exactly the affected gates VACUOUS, so a sweep's zero-vacuous result cannot
come from a checker that never reds.
"""

import copy
import json
import os
import sys
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import hlib  # noqa: E402
import ledgerverify  # noqa: E402
import mutledger  # noqa: E402
import mutlib  # noqa: E402
import oracle  # noqa: E402
from test_mutsave import Patched  # noqa: E402

SEED = {"funds": 500000.0, "sciencePool": 100.0, "reputation": 0.0,
        "hasFunds": True, "hasScience": True, "hasRep": True}
HIRE = -62113.0
ROSTER = [{"name": n, "gender": "Male", "type": "Crew", "trait": "Pilot", "state": "Available"}
          for n in ("Jebediah Kerman", "Bob Kerman", "Valentina Kerman")]

# A measured stock award line shape (CL-2's archived capturedRaw).
REP_LINE = "[LOG 19:46:27.776] Added 0.9999995 (1) reputation: 'Progression'."
UT_LINE = "[LOG 19:46:27.700] [Parsek][INFO][Flight] tick ut=12.6"
LOG = "\n".join(["[LOG 19:46:00.000] boot", UT_LINE, "[LOG 19:46:27.750] noise", REP_LINE,
                 "[LOG 19:46:30.000] more noise"]) + "\n"


def _career(funds=500000.0 + HIRE, science=100.0, rep=0.0, roster=None):
    return {"parsed": True, "hasFunds": True, "funds": funds, "hasScience": True,
            "sciencePool": science, "hasRep": True, "reputation": rep,
            "subjectScience": {}, "activeContractGuids": [], "vessels": [],
            "hasRoster": True, "roster": copy.deepcopy(ROSTER if roster is None else roster)}


def _analysis(career):
    return json.dumps({"analyzerVersion": 4, "careerSave": career})


def _hire_spec(roster=None, manifest=None):
    exp = {"ledger": {"seedFrom": "template", "tolerances": "default", "rec3CarveOut": False,
                      "manifest": manifest if manifest is not None else [
                          {"seq": 0, "ut": 0.0, "kind": "kerbal-hire", "funds": HIRE}]}}
    if roster is not None:
        exp["world"] = {"roster": roster}
    return {"id": "T-hire", "expectations": exp}


def _cc_spec():
    return {"id": "T-cc", "expectations": {"ledger": {
        "seedFrom": "template", "tolerances": "default", "rec3CarveOut": False,
        "captureCrossCheck": "gate",
        "manifest": [{"seq": 0, "kind": "stock-reputation-award", "reputation": 0.9999995,
                      "repMode": "applied", "stockReason": "Progression",
                      "utWindow": [0.0, 100.0], "provenance": "gameevents-captured"}]}}}


def _lane(spec, career=None, seed=SEED, log=LOG):
    career = _career() if career is None else career
    inputs = mutledger.LedgerInputs(seed, _analysis(career), log)
    lane = mutlib.check_ledger_lane(spec, "test", inputs)
    return lane, {g.label: g for g in lane.ledger_gates}


def _eval(spec, career, seed=SEED, log=LOG):
    return mutledger.evaluate(spec["expectations"], seed, career, log)


def _verdicts(gates, prefix=""):
    return {k: g.verdict for k, g in gates.items() if k.startswith(prefix)}


class BaselineTests(unittest.TestCase):

    def test_green_baseline_proves_every_gate(self):
        lane, gates = _lane(_hire_spec(roster={"present": ["Jebediah Kerman"],
                                               "absent": ["Bill Kerman"]}))
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline)
        self.assertTrue(gates)
        self.assertEqual({"PROVEN"}, {g.verdict for g in gates.values()},
                         [(g.label, g.reason) for g in gates.values() if g.verdict != "PROVEN"])
        self.assertEqual(0, lane.count(mutlib.SURVIVED))

    def test_a_drifted_produced_pool_is_not_green(self):
        lane, gates = _lane(_hire_spec(), career=_career(funds=500000.0))
        self.assertEqual(mutlib.BASELINE_NOT_GREEN, lane.baseline)
        self.assertIn("funds", lane.baseline_reasons[0])
        self.assertEqual({}, gates)

    def test_missing_seed_or_analysis_is_not_green(self):
        spec = _hire_spec()
        base = mutledger.prepare_baseline(spec["expectations"],
                                          mutledger.LedgerInputs(None, _analysis(_career()), LOG))
        self.assertIn("no archived seed", base.reasons[0])
        base = mutledger.prepare_baseline(spec["expectations"],
                                          mutledger.LedgerInputs(SEED, None, LOG))
        self.assertIn(".analysis.json", base.reasons[0])

    def test_the_reduced_log_is_used_only_when_it_captures_identically(self):
        spec = _cc_spec()
        inputs = mutledger.LedgerInputs(SEED, _analysis(_career(funds=500000.0, rep=0.9999995)), LOG)
        base = mutledger.prepare_baseline(spec["expectations"], inputs)
        self.assertTrue(base.reduced)
        self.assertNotIn("noise", base.log_text)
        self.assertEqual(LOG.count("\n"), base.log_text.count("\n"))
        broken = mutledger.prepare_baseline(spec["expectations"], inputs, reducer=lambda t: "")
        self.assertFalse(broken.reduced)
        self.assertIn("full log", broken.note)
        self.assertEqual(LOG, broken.log_text)

    def test_edits_never_touch_the_baseline(self):
        spec = _hire_spec(roster={"present": ["Bob Kerman"]})
        base = mutledger.prepare_baseline(spec["expectations"], mutledger.LedgerInputs(
            SEED, _analysis(_career()), LOG))
        before = (copy.deepcopy(base.career_block), dict(base.seed_block),
                  copy.deepcopy(spec["expectations"]))
        mutledger.mutate_ledger(spec["expectations"], base)
        self.assertEqual(before, (base.career_block, base.seed_block, spec["expectations"]))


class PoolTests(unittest.TestCase):

    def test_produced_pool_crossings_kill_both_sides(self):
        _lane_, gates = _lane(_hire_spec())
        for facet in ("funds", "sciencePool", "reputation"):
            self.assertEqual("PROVEN", gates["ledger.%s:produced" % facet].verdict)
            self.assertEqual("PROVEN", gates["ledger.%s:seed" % facet].verdict)
            self.assertEqual("PROVEN", gates["ledger:drop-%s" % facet].verdict)

    def test_produced_pool_inside_the_tolerance_stays_green(self):
        spec = _hire_spec()
        for key, delta in (("funds", 0.99), ("sciencePool", 0.099), ("reputation", -0.099)):
            career = _career()
            career[key] += delta
            self.assertEqual("PASS", _eval(spec, career).result["status"], key)

    def test_seed_inside_the_tolerance_stays_green(self):
        seed = dict(SEED, funds=SEED["funds"] + 0.99, reputation=0.05)
        self.assertEqual("PASS", _eval(_hire_spec(), _career(), seed=seed).result["status"])

    def test_crossings_account_for_the_baseline_residual(self):
        # Produced funds 0.9 above expected: a fixed +1.01 shift of the expected side
        # would land 0.11 away and never cross; the planner must aim at the parsed value.
        _lane_, gates = _lane(_hire_spec(), career=_career(funds=500000.0 + HIRE + 0.9))
        self.assertEqual("PROVEN", gates["ledger.funds:seed"].verdict)
        self.assertEqual("PROVEN", gates["manifest[0].funds"].verdict)

    def test_pools_absent_from_the_seed_carry_no_gate(self):
        seed = {"hasFunds": False, "hasScience": True, "sciencePool": 100.0, "hasRep": False}
        career = {"parsed": True, "hasFunds": False, "hasScience": True, "sciencePool": 100.0,
                  "hasRep": False, "subjectScience": {}, "activeContractGuids": [], "vessels": []}
        spec = {"id": "T-sci", "expectations": {"ledger": {"manifest": []}}}
        lane, gates = _lane(spec, career=career, seed=seed)
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline)
        self.assertIn("ledger.sciencePool:produced", gates)
        self.assertNotIn("ledger.funds:produced", gates)
        self.assertTrue(any("funds, reputation" in n for n in lane.notes))


class EntryTests(unittest.TestCase):

    def test_declared_amount_crossings_kill(self):
        _lane_, gates = _lane(_hire_spec())
        self.assertEqual("PROVEN", gates["manifest[0].funds"].verdict)
        self.assertEqual("PROVEN", gates["manifest[0]:removed"].verdict)

    def test_declared_amount_inside_the_tolerance_stays_green(self):
        spec = _hire_spec(manifest=[{"seq": 0, "ut": 0.0, "kind": "kerbal-hire",
                                     "funds": HIRE + 0.99}])
        self.assertEqual("PASS", _eval(spec, _career()).result["status"])

    def test_a_zero_amount_entry_is_shifted_but_never_removed(self):
        spec = _hire_spec(manifest=[{"seq": 0, "ut": 0.0, "kind": "kerbal-dismiss",
                                     "funds": 0.0, "reputation": 0.0}])
        _lane_, gates = _lane(spec, career=_career(funds=500000.0))
        self.assertEqual("PROVEN", gates["manifest[0].funds"].verdict)
        self.assertEqual("PROVEN", gates["manifest[0].reputation"].verdict)
        self.assertNotIn("manifest[0]:removed", gates)

    def test_nominal_reputation_is_scaled_through_the_curve(self):
        # At rep 900 the gain curve is far below 1, so a 0.101 nominal shift moves the
        # expected pool by much less than the tolerance; the planner must double it.
        seed = dict(SEED, reputation=900.0)
        spec = _hire_spec(manifest=[{"seq": 0, "ut": 0.0, "kind": "milestone",
                                     "reputation": 5.0, "repMode": "nominal"}])
        _d, rep = oracle.apply_rep_curve(5.0, 900.0)
        _lane_, gates = _lane(spec, career=_career(funds=500000.0, rep=rep), seed=seed)
        self.assertEqual("PROVEN", gates["manifest[0].reputation"].verdict,
                         gates["manifest[0].reputation"].reason)

    def test_an_entry_too_small_for_the_tolerance_is_vacuous_on_removal(self):
        spec = _hire_spec(manifest=[{"seq": 0, "ut": 0.0, "kind": "milestone", "funds": 0.5}])
        _lane_, gates = _lane(spec, career=_career(funds=500000.5))
        self.assertEqual("VACUOUS", gates["manifest[0]:removed"].verdict)
        self.assertEqual("PROVEN", gates["manifest[0].funds"].verdict)


class CrossCheckTests(unittest.TestCase):

    def _career(self):
        return _career(funds=500000.0, rep=0.9999995)

    def test_injected_and_shifted_awards_kill_an_armed_gate(self):
        lane, gates = _lane(_cc_spec(), career=self._career())
        self.assertEqual(mutlib.BASELINE_GREEN, lane.baseline)
        self.assertEqual("PROVEN", gates["ledger.captureCrossCheck"].verdict)
        targets = [m for m in lane.mutations if m.kind == mutledger.KIND_CAPTURE]
        self.assertEqual(3, len(targets))  # inject + one award shifted both ways

    def test_an_award_inside_the_tolerance_stays_green(self):
        log = LOG.replace("0.9999995 (1)", "1.05 (1)")
        self.assertEqual("PASS", _eval(_cc_spec(), self._career(), log=log).result["status"])

    def test_a_report_mode_cross_check_is_not_a_gate(self):
        spec = _cc_spec()
        spec["expectations"]["ledger"]["captureCrossCheck"] = "report"
        lane, gates = _lane(spec, career=self._career())
        self.assertNotIn("ledger.captureCrossCheck", gates)
        self.assertTrue(any("report-only" in n for n in lane.notes))


class RosterTests(unittest.TestCase):

    def test_present_and_absent_claims_kill(self):
        spec = _hire_spec(roster={"present": ["Jebediah Kerman", "Bob Kerman"],
                                  "absent": ["Bill Kerman"]})
        _lane_, gates = _lane(spec)
        self.assertEqual("PROVEN", gates["world.roster.present[Jebediah Kerman]"].verdict)
        self.assertEqual("PROVEN", gates["world.roster.present[Bob Kerman]"].verdict)
        self.assertEqual("PROVEN", gates["world.roster.absent[Bill Kerman]"].verdict)
        self.assertEqual("PROVEN", gates["ledger:drop-roster"].verdict)

    def test_an_undeclared_kerbal_moving_stays_green(self):
        spec = _hire_spec(roster={"present": ["Jebediah Kerman"]})
        career = _career(roster=[k for k in ROSTER if k["name"] != "Valentina Kerman"])
        self.assertEqual("PASS", _eval(spec, career).result["status"])

    def test_world_vessels_are_reported_unchecked(self):
        spec = _hire_spec()
        spec["expectations"]["world"] = {"vessels": {"entry": [
            {"guid": "g1", "resources": {"LiquidFuel": {"expected": 1.0}}}]}}
        career = _career()
        career["vessels"] = [{"guid": "g1", "persistentId": 1, "resourceTotals": {"LiquidFuel": 1.0}}]
        _lane_, gates = _lane(spec, career=career)
        self.assertEqual("UNCHECKED", gates["world.vessels"].verdict)


class FaultTests(unittest.TestCase):

    def test_every_fault_reds(self):
        _lane_, gates = _lane(_hire_spec(roster={"present": ["Bob Kerman"]}))
        for fault in ("tear", "drop-career-block", "unparsed", "drop-seed",
                      "bad-manifest-entry", "drop-roster"):
            self.assertEqual("PROVEN", gates["ledger:%s" % fault].verdict, fault)

    def test_a_contradicting_seed_routes_to_tooling(self):
        seed = dict(SEED, funds=None)
        v = mutledger.evaluate(_hire_spec()["expectations"], seed, _career(), LOG)
        self.assertTrue(v.tooling)


class VacuityDetectionTests(unittest.TestCase):
    """Break one oracle path at a time; the checker must name the gates it feeds
    VACUOUS and leave the rest PROVEN."""

    def test_a_tolerance_that_always_agrees_is_vacuous(self):
        with Patched(oracle, "within_tolerance", lambda e, p, t: True):
            _lane_, gates = _lane(_hire_spec())
        for facet in ("funds", "sciencePool", "reputation"):
            self.assertEqual("VACUOUS", gates["ledger.%s:produced" % facet].verdict)
            self.assertEqual("VACUOUS", gates["ledger.%s:seed" % facet].verdict)
            self.assertEqual("PROVEN", gates["ledger:drop-%s" % facet].verdict)
        self.assertEqual("VACUOUS", gates["manifest[0].funds"].verdict)
        self.assertEqual("VACUOUS", gates["manifest[0]:removed"].verdict)

    def test_a_one_sided_compare_is_vacuous_on_the_other_side(self):
        real = oracle.within_tolerance

        def one_sided(e, p, t):
            return real(e, p, t) or (p is not None and e is not None and p < e)

        with Patched(oracle, "within_tolerance", one_sided):
            _lane_, gates = _lane(_hire_spec())
        g = gates["ledger.funds:produced"]
        self.assertEqual("VACUOUS", g.verdict)
        self.assertIn("down", g.reason)
        self.assertNotIn("up:", g.reason)

    def test_a_skipped_pool_is_vacuous_alone(self):
        real = oracle._diff_pool

        def skip_rep(facet, *a, **k):
            return None if facet == "reputation" else real(facet, *a, **k)

        with Patched(oracle, "_diff_pool", skip_rep):
            _lane_, gates = _lane(_hire_spec())
        self.assertEqual("VACUOUS", gates["ledger.reputation:produced"].verdict)
        self.assertEqual("VACUOUS", gates["ledger:drop-reputation"].verdict)
        self.assertEqual("PROVEN", gates["ledger.funds:produced"].verdict)
        self.assertEqual("PROVEN", gates["manifest[0].funds"].verdict)

    def test_an_expected_value_that_ignores_the_manifest_is_a_dead_path(self):
        real = oracle.compute_expected

        def ignore_entries(seed, entries, *a, **k):
            return real(seed, [], *a, **k)

        with Patched(oracle, "compute_expected", ignore_entries):
            _lane_, gates = _lane(_hire_spec(), career=_career(funds=500000.0))
        g = gates["manifest[0].funds"]
        self.assertEqual("VACUOUS", g.verdict)
        self.assertIn("dead path", g.reason)
        self.assertEqual("VACUOUS", gates["manifest[0]:removed"].verdict)
        self.assertEqual("PROVEN", gates["ledger.funds:seed"].verdict)

    def test_a_roster_diff_that_never_reds_is_vacuous(self):
        spec = _hire_spec(roster={"present": ["Bob Kerman"], "absent": ["Bill Kerman"]})
        with Patched(oracle, "diff_world_roster", lambda d, c: []):
            _lane_, gates = _lane(spec)
        self.assertEqual("VACUOUS", gates["world.roster.present[Bob Kerman]"].verdict)
        self.assertEqual("VACUOUS", gates["world.roster.absent[Bill Kerman]"].verdict)
        self.assertEqual("PROVEN", gates["ledger:drop-roster"].verdict)

    def test_a_cross_check_that_explains_everything_is_vacuous(self):
        with Patched(hlib, "unmatched_captured_awards", lambda s, c, t=None: []):
            _lane_, gates = _lane(_cc_spec(), career=_career(funds=500000.0, rep=0.9999995))
        self.assertEqual("VACUOUS", gates["ledger.captureCrossCheck"].verdict)
        self.assertEqual("PROVEN", gates["ledger.reputation:produced"].verdict)

    def test_a_manifest_parse_that_drops_errors_is_vacuous(self):
        real = oracle.parse_manifest_entries

        def no_errors(*a, **k):
            return oracle.ManifestParse(real(*a, **k).entries, ())

        with Patched(oracle, "parse_manifest_entries", no_errors):
            _lane_, gates = _lane(_hire_spec())
        self.assertEqual("VACUOUS", gates["ledger:bad-manifest-entry"].verdict)
        self.assertEqual("PROVEN", gates["ledger:tear"].verdict)


class SharedPathTests(unittest.TestCase):

    def test_run_py_delegates_to_the_function_the_checker_replays(self):
        harness = os.path.dirname(_HERE)
        if harness not in sys.path:
            sys.path.insert(0, harness)
        import run  # noqa: E402

        calls = []

        def spy(*a, **k):
            calls.append((a, k))
            return ledgerverify.LedgerVerdict({"status": "PASS"}, False, False)

        class Quiet:
            def info(self, *a): pass
            def warn(self, *a): pass
            def verbose(self, *a): pass
            def error(self, *a): pass

        with Patched(ledgerverify, "evaluate", spy):
            out = run._run_ledger_oracle({"manifest": []}, None, _career(), None, "", "r", Quiet())
        self.assertEqual(({"status": "PASS"}, False, False), out)
        self.assertEqual(1, len(calls))


class ReportTests(unittest.TestCase):

    def test_report_lists_vacuous_ledger_gates(self):
        with Patched(oracle, "diff_world_roster", lambda d, c: []):
            lane, _g = _lane(_hire_spec(roster={"absent": ["Bill Kerman"]}))
        text = mutlib.render_report([lane], [])
        self.assertIn("ledger-oracle gates (ledger edits): proven", text)
        self.assertIn("## Vacuous ledger-oracle gates", text)
        self.assertIn("world.roster.absent[Bill Kerman]", text)
        self.assertIn("ledgerGates(proven=", mutlib.summary_line(lane))
        t = mutlib.sweep_totals([lane], 0)
        self.assertEqual(1, t.ledger_vacuous)


if __name__ == "__main__":
    unittest.main()
