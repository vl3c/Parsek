"""Unit tests for harness/lib/ledgeraudit.py (the offline ledger audit) and its
thin shell harness/tools/ledger_audit.py.

Every synthetic line is built from the C# format string that writes it (the
builders below name the file); ``SourceSyncTests`` reads those files with
comments stripped so a reworded literal reds here instead of turning a check into
a silent 0.

Runs under ``python -m unittest discover -s lib -q``. ASCII only; stdlib only.
"""

import contextlib
import gzip
import io
import json
import os
import re
import shutil
import sys
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_HARNESS = os.path.dirname(_HERE)
_TOOLS = os.path.join(_HARNESS, "tools")
for _p in (_HERE, _TOOLS):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import ledgeraudit as la  # noqa: E402
import ledger_audit as tool  # noqa: E402

REPO_ROOT = os.path.dirname(_HARNESS)


# ---------------------------------------------------------------------------
# Line builders (each mirrors one C# format; file named per builder)
# ---------------------------------------------------------------------------

def stamp(sec):
    s = int(sec)
    ms = int(round((sec - s) * 1000))
    return "[LOG %02d:%02d:%02d.%03d] " % ((s // 3600) % 24, (s // 60) % 60, s % 60, ms)


def P(level, sub, msg, t=36000.0):
    """ParsekLog.Write: "[Parsek][{level}][{safeSubsystem}] {safeMessage}" behind Debug.Log."""
    return stamp(t) + "[Parsek][%s][%s] %s" % (level, sub, msg)


def scene(frm, to, t=36000.0):
    return stamp(t) + ("[HighLogic]: =========================== Scene Change : From %s to %s "
                       "(Async) =====================" % (frm, to))


def structural(seq, funds=25000.0, sci=1.5, rep=0.0, contracts=0, t=36000.0, cutoff="null",
               facilities="LaunchPad:0"):
    # LedgerTrace.FormatStructural
    return P("INFO", "LedgerTrace",
             "phase=Structural recalcSeq=%d funds=%.2f science=%.2f rep=%.2f facilities=[%s] "
             "targetTechNodes=0 contracts=%d cutoff=%s authReduction=false"
             % (seq, funds, sci, rep, facilities, contracts, cutoff), t)


def anomaly(seq=1, resource="funds", ident="pool", t=36000.0):
    # LedgerTrace.FormatAnomaly
    return P("WARN", "LedgerTrace",
             "phase=Anomaly recalcSeq=%d resource=%s id=%s reason=ledger-vs-truth "
             "target=1 actual=2 delta=1 tol=0.01" % (seq, resource, ident), t)


def trace_change(seq=1, t=36000.0):
    return P("VERBOSE", "LedgerTrace", "phase=Change recalcSeq=%d resource=funds id=pool "
             "change=1->2" % seq, t)


def ms_reset(prev=0, seeded=False, t=36000.0):
    # MilestonesModule.Reset (both shapes)
    if seeded:
        return P("VERBOSE", "Milestones", "Reset: cleared %d credited milestones; pre-ledger seed "
                 "credits 2 node(s) and 1 record band(s)" % prev, t)
    return P("VERBOSE", "Milestones", "Reset: cleared %d credited milestones" % prev, t)


def credited(mid, ut=15.1, total=1, record=False, t=36000.0):
    # MilestonesModule.ProcessAction / ProcessRecordRow
    tail = ", thresholds=1, crossed=[1]" if record else ""
    return P("VERBOSE", "Milestones",
             "Credited milestone '%s' at UT=%.1f (recording=r1, funds=800, rep=0, sci=0.0%s), "
             "total credited=%d" % (mid, ut, tail, total), t)


def earning(subject, credited_total, max_value, cap_hit=False, t=36000.0):
    # ScienceModule.ProcessEarning (both shapes)
    if cap_hit:
        msg = ("Earning (cap hit): subject=%s, awarded=5, effective=1, headroom=1, "
               "creditedTotal=%r, maxValue=%r, runningScience=3, recordingId=r1"
               % (subject, credited_total, max_value))
    else:
        msg = ("Earning: subject=%s, awarded=1.5, effective=1.5, creditedTotal=%r, maxValue=%r, "
               "runningScience=1.5, recordingId=r1" % (subject, credited_total, max_value))
    return P("VERBOSE", "ScienceModule", msg, t)


def took_back(t=36000.0):
    # StockShadowTakeBack.FormatTakeBack
    return P("WARN", "TakeBack", "Took back 800 Funds stock paid again for milestone 'FirstLaunch' "
             "(row act_1 ut=15.1 cseq=3 reason=MilestoneAlreadyAchieved; already credited by "
             "row act_0; FirstLaunch)", t)


def shortfall(t=36000.0):
    # KspStatePatcher.EmitTakeBackShortfall
    return P("WARN", "TakeBack", "PatchFunds: take-back shortfall 100 of 800: the duplicate payment "
             "was spent before this update; running=-100 stays below 0 and later gains repay it "
             "first (a debt, design 3.13 part 4)", t)


def break_off(t=36000.0):
    # StockShadowTakeBack.NoteLoad
    return P("WARN", "TakeBack", "Timeline break not recorded for load kind=QuickloadFlight: the "
             "loaded UT is unknown (a duplicate stock payment after this load is kept as before)", t)


def pool_adj(sub="Funds", amount="250", ident="act_9", t=36000.0):
    # FundsModule / ScienceModule / ReputationModule ProcessStockPoolAdjustment
    if sub == "Reputation":
        return P("VERBOSE", sub, "StockPoolAdjustment: %s (post-curve) id=%s ut=12 runningRep=3"
                 % (amount, ident), t)
    return P("VERBOSE", sub, "StockPoolAdjustment: %s id=%s ut=12 runningBalance=900"
             % (amount, ident), t)


def debt_charge(sub="Funds", t=36000.0):
    return P("VERBOSE", sub, "Debt charge: FundsSpending id=act_2 ut=40 charged=100 debt=100 "
             "runningBalance=-100", t)


def science_debt(t=36000.0):
    return P("INFO", "ScienceModule", "Spending charged into science debt: nodeId=basicRocketry "
             "ut=40 cost=5 runningBefore=2 debt=3 runningScience=-3 (the tech stays researched; "
             "later earnings repay the debt before the pool rises)", t)


def aborting(t=36000.0):
    return P("WARN", "KspStatePatcher", "PatchAll: ABORTING rewind ledger patch: divergence flagged "
             "and abort forced; economy/tech/contracts/milestones NOT patched, crew roster already "
             "applied; loaded quicksave values stand", t)


def deserialize_fail(t=36000.0):
    return P("WARN", "Ledger", "Failed to deserialize action at index 4: bad type", t)


def refuse_upgrade(t=36000.0):
    return P("WARN", "Ledger", "SeedInitialScience: ScienceInitial already exists (amount=0), "
             "refusing to upgrade to amount=12 (#557 / PR #499: baseline is authoritative once "
             "seeded)", t)


def not_affordable(t=36000.0):
    return P("WARN", "Funds", "FundsSpending NOT affordable: -500, source=VesselBuild, "
             "runningBalance=100, recordingId=(none) - possible bug or data corruption", t)


def deferred(reason="live recorder active", t=36000.0):
    return P("VERBOSE", "LedgerOrchestrator", "RecalculateAndPatch: deferred KSP state patch (%s)"
             % reason, t)


def patched(t=36000.0):
    return P("VERBOSE", "LedgerOrchestrator", "RecalculateAndPatch: drawdown-guard "
             "authoritativeReduction=False (rewinding=False, reFlyMarker=False, mergeJournal=False, "
             "tombstone=False, rewindResAdjust=False, reFlyDiscard=False)", t)


def classified(early="Cold", refined="Cold", scn="SPACECENTER", t=36000.0):
    # LoadReconcilePolicy.FormatClassificationLine
    return P("INFO", "LoadPolicy", "Load classified: early=%s refined=%s scene=%s "
             "initialLoadDone=false rewinding=false reFlyInvoke=false discardReFly=none "
             "handoff=absent coldReason=none saveFileLoad=false revertPending=false "
             "intentAgeFrames=-1 nodeUT=NaN" % (early, refined, scn), t)


def onload_done(t=36000.0, status="completed"):
    # ParsekScenario.FormatLoadTimingMessage
    return P("VERBOSE", "Diagnostics", "OnLoad: 120ms (3 recordings) phase=done status=%s" % status, t)


def unbound(t=36000.0):
    return P("WARN", "ReFlySession", "outcome=unbound-refly-provisional reason=restore-give-up "
             "sess=s1 provisional=p1 markerTree=t1", t)


def retired(t=36000.0):
    return P("INFO", "ReFlySession", "outcome=retired-empty-provisional rec=p1 sess=s1 origin=o1 "
             "prune=leaf - the session's provisional carries no payload", t)


def dominance(result="pass", t=36000.0):
    # ReFlyMergeDominance.LogVerdict
    fields = ("pool=funds ut=12 before=1 after=2" if result == "pass"
              else "world=milestone:FirstLaunch ut=12")
    return P("INFO", "LedgerSwap", "Merge dominance check sess=s1 result=%s %s context=merge "
             "attemptRows=1 retireSet=0 closure=1 notCounted=0" % (result, fields), t)


def owned(t=36000.0):
    return P("INFO", "Funds", "FacilityUpgrade not charged: facility LaunchPad level 1 is already "
             "owned at ut=40 cseq=7 (held by act_3, captured after this row), cost=100, "
             "recording=(none), runningBalance=900", t)


def pid_only(t=36000.0):
    return P("VERBOSE", "ChainWalker", "Claim PID=100000 tree=t1 bp=b1 resolved no launch guid; "
             "chain is pid-only", t)


def guid_unknown(t=36000.0):
    return P("INFO", "LedgerOrchestrator", "PickRecoveryRecordingId path: vessel='X' pid=1 ut=40 "
             "path=name reason=live-launch-guid-unknown nameMatches=2", t)


def subsurface(rec="rec42", t=36000.0):
    return P("VERBOSE", "FinalizerCache", "Refresh declined: owner=flight rec=%s pid=7 "
             "reason=subsurface-destroyed-suppressed" % rec, t)


def fallback(rec="rec43", t=36000.0):
    return P("INFO", "Extrapolator", "TryFinalizeRecording: recovered sub-surface live-orbit "
             "fallback for '%s' by extrapolating recorded surface point (pointUT=1.000, "
             "source=last, recordedAlt=-3.0) -> terminal=Orbiting terminalUT=50.0 "
             "appendedSegments=1" % rec, t)


def gt_report(facets, t=36000.0):
    # LedgerDivergenceReport.Format: header + one raw line per divergence
    lines = [P("WARN", "LedgerGroundTruth", "LedgerDivergenceReport: total=%d facetsCompared=7"
               % len(facets), t)]
    for facet, kind, detail in facets:
        lines.append("facet=%s kind=%s id=x expected=1 recon=2 detail=%s" % (facet, kind, detail))
    return lines


def gt_result(hard=0, report_only=0, t=36000.0):
    return P("INFO", "LedgerGroundTruth", "result: hardFailures=%d reportOnly=%d facetsCompared=7 "
             "strict=False" % (hard, report_only), t)


def _b(v):
    return "True" if v else "False"


def insession_decision(use=False, future=True, ready=True, loaded_ut=150000.0, revert=True,
                       scn="FLIGHT", t=36000.0):
    # ParsekScenario.RecalculateLedgerForInSessionLoad
    return P("INFO", "Scenario",
             "OnLoad: post-rewind current-UT cutoff decision useCurrentUtCutoff=%s loadedUT=%r "
             "planetariumUT=%r isRevert=%s loadedScene=%s loadedSceneSupportsCurrentUtCutoff=True "
             "planetariumReady=%s hasPendingTree=False restoreMode=None hasLiveRecorder=False "
             "hasActiveUncommittedTree=False hasFutureLedgerActions=%s"
             % (_b(use), loaded_ut, loaded_ut, _b(revert), scn, _b(ready), _b(future)), t)


def kspload_decision(use=False, ready=True, future=True, max_ut=150000.0, t=36000.0):
    # LedgerOrchestrator.OnKspLoad
    return P("INFO", "LedgerOrchestrator",
             "OnKspLoad recalc decision: useCurrentUtCutoff=%s maxUT=%r featureSupportsCutoff=True "
             "currentUtReady=%s hasFutureActions=%s" % (_b(use), max_ut, _b(ready), _b(future)), t)


def commit_full(t=36000.0):
    # LedgerOrchestrator.RecalculateAndPatchAfterCommit
    return P("VERBOSE", "LedgerOrchestrator",
             "Commit ledger walk: full replay (cutoffUT=null), live clock not trusted - "
             "reason=tree-commit onLoadInProgress=True rewindUTAdjustmentPending=False", t)


def current_full(ready=True, t=36000.0):
    # LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions (an em dash
    # before " reason=" in the C# literal)
    return P("VERBOSE", "LedgerOrchestrator",
             "Current-timeline recalc: full replay (cutoffUT=null) \u2014 reason=revert-budget "
             "currentUT=150000 currentUtReady=%s" % _b(ready), t)


def input_summary(cutoff="null", total=10, t=36000.0):
    # LedgerOrchestrator.LogRecalculationInputSummary
    return P("VERBOSE", "LedgerOrchestrator",
             "RecalculateAndPatch: actionsTotal=%d, actionsAfterCutoff=%d, cutoffUT=%s"
             % (total, total, cutoff), t)


def budget_ready(t=36000.0):
    # ParsekScenario.ApplyBudgetDeductionWhenReady
    return P("VERBOSE", "Scenario", "ApplyBudgetDeduction: singletons ready after 0 frames "
             "(expected=Funding,R&D,Reputation). Funding=True, R&D=True, Rep=True", t)


def budget_done(t=36000.0):
    return P("VERBOSE", "Scenario", "Budget deduction already applied for this revert load", t)


def revert_budget(cut=True, ready=True, scn="FLIGHT", revert_ut=185785.98, t=36000.0):
    # ParsekScenario.RecalculateAndPatchAfterRevert
    return P("INFO", "Scenario", "Revert budget walk: scene=%s cutoffScene=%s clockReady=%s "
             "revertUT=%r" % (scn, _b(cut), _b(ready), revert_ut), t)


def patch_move(pool, before, after, t=36000.0):
    # KspStatePatcher.PatchFunds / PatchScience / PatchReputation
    if pool == "Reputation":
        return P("INFO", "KspStatePatcher", "PatchReputation: %.2f -> %.2f (target=%.2f)"
                 % (before, after, after), t)
    return P("INFO", "KspStatePatcher", "Patch%s: %.1f -> %.1f (delta=%.1f, target=%.1f)"
             % (pool, before, after, after - before, after), t)


def guarded(direction, resource="Funds", running=153564.18, live=150632.18, would=150632.18,
            clamped=110200.18, t=36000.0):
    # KspStatePatcher.EmitDrawdownGuardClamp
    tail = ("(no time-travel context) - spent value held; ledger may be missing a spending channel"
            if direction == "UPLIFT" else
            "(no time-travel context) - earned value preserved; ledger may be missing an earning "
            "channel")
    return P("WARN", "KspStatePatcher",
             "Patch%s: GUARDED %s clamped resource=%s running=%r live=%r wouldBeTarget=%r "
             "clampedTo=%r %s" % (resource, direction, resource, running, live, would, clamped, tail),
             t)


def filed(resource="Funds", amount=800.0, reason="leak", t=36000.0):
    # KspStatePatcher.FileStockPoolAdjustment
    return P("INFO", "StockPoolReference",
             "Patch%s: filed StockPoolAdjustment amount=%r reason=%s ut=150000 id=act_leak"
             % (resource, amount, reason), t)


def ref_loaded(kind="DiscardReFly", t=36000.0):
    # StockPoolReference.LoadFrom
    return P("INFO", "StockPoolReference",
             "Loaded (%s): pools=3 rows=4 badRows=0 funds=lastWritten=1 science=lastWritten=2 "
             "reputation=lastWritten=3" % kind, t)


def ms_funds_skipped(mid="FirstLaunch", amount=800.0, t=36000.0):
    # FundsModule.ProcessMilestoneEarning
    return P("VERBOSE", "Funds", "Milestone funds skipped (not effective): milestoneId=%s, "
             "fundsAwarded=%r" % (mid, amount), t)


def ctr_funds_skipped(amount=5000.0, t=36000.0):
    # FundsModule.ProcessContractComplete
    return P("VERBOSE", "Funds", "ContractComplete funds skipped (not effective): contractId=c1, "
             "fundsReward=%r" % amount, t)


def earn_skipped(amount=100.0, t=36000.0):
    # FundsModule.ProcessFundsEarning
    return P("VERBOSE", "Funds", "FundsEarning skipped (not effective): fundsAwarded=%r, "
             "source=Other, recordingId=(none)" % amount, t)


def sci_skipped(amount=2.0, t=36000.0):
    # ScienceModule.ProcessMilestoneScienceReward
    return P("VERBOSE", "ScienceModule", "MilestoneAchievement science skipped (not effective): "
             "id=FirstLaunch, sciAwarded=%r" % amount, t)


def run_audit(lines, exp=None, baseline=None, ledgers=None):
    return la.audit(lines, exp, baseline, ledgers)


def by_check(result, check):
    for r in result["results"]:
        if r["check"] == check:
            return r
    raise KeyError(check)


# ---------------------------------------------------------------------------
# Parsing
# ---------------------------------------------------------------------------

class ParseLogTests(unittest.TestCase):
    def test_stamped_parsek_line(self):
        recs = la.parse_log([P("INFO", "Funds", "hello world", t=3661.5)])
        self.assertEqual(1, len(recs))
        r = recs[0]
        self.assertEqual(("INFO", "Funds", "hello world"), (r.level, r.sub, r.msg))
        self.assertAlmostEqual(3661.5, r.t)
        self.assertEqual(1, r.n)

    def test_warn_stamp_and_colon_variant(self):
        recs = la.parse_log(["[WRN 01:00:00.250]: [Parsek][WARN][Ledger] x",
                             "[ERR 01:00:01] [Parsek][ERROR][Ledger] y"])
        self.assertEqual(["x", "y"], [r.msg for r in recs])
        self.assertAlmostEqual(3600.25, recs[0].t)

    def test_bare_parsek_line_has_no_time(self):
        recs = la.parse_log(["[Parsek][VERBOSE][Milestones] Reset: cleared 0 credited milestones"])
        self.assertIsNone(recs[0].t)
        self.assertEqual("Milestones", recs[0].sub)

    def test_continuation_lines_attach_to_previous_record(self):
        recs = la.parse_log(gt_report([("Funds", "ValueMismatch", "d")]))
        self.assertEqual(1, len(recs))
        self.assertEqual(1, len(recs[0].cont))

    def test_midnight_rollover_unwraps(self):
        recs = la.parse_log([P("INFO", "A", "a", t=86399.0), P("INFO", "A", "b", t=1.0)])
        self.assertAlmostEqual(86401.0, recs[1].t)

    def test_scene_change_record(self):
        recs = la.parse_log([scene("SPACECENTER", "FLIGHT")])
        self.assertEqual(("SPACECENTER", "FLIGHT"), recs[0].scene)
        self.assertFalse(recs[0].is_parsek)

    def test_the_real_committed_excerpt_parses(self):
        path = os.path.join(_HERE, "testdata", "flighteff_b11_ksp_excerpt.log")
        with open(path, "r", encoding="utf-8") as fh:
            recs = la.parse_log(fh.read().splitlines())
        parsek = [r for r in recs if r.is_parsek]
        self.assertTrue(parsek)
        self.assertTrue(all(r.t is not None for r in parsek))
        self.assertTrue(any(r.scene for r in recs))
        self.assertEqual("TestCommands", parsek[0].sub)


# ---------------------------------------------------------------------------
# 1 + 2
# ---------------------------------------------------------------------------

class LedgerTraceTests(unittest.TestCase):
    def test_no_trace_line_skips_both(self):
        res = run_audit([P("INFO", "Funds", "x")])
        self.assertEqual(la.SKIP, by_check(res, "ledger-anomaly")["status"])
        self.assertIn("ledgerTracing off", by_check(res, "ledger-anomaly")["summary"])
        self.assertEqual(la.SKIP, by_check(res, "recalc-rate")["status"])

    def test_clean_trace_passes(self):
        res = run_audit([structural(1), trace_change(1)])
        self.assertEqual(la.PASS, by_check(res, "ledger-anomaly")["status"])

    def test_anomaly_fails_and_cannot_be_expected(self):
        exp = la.parse_expectations({"expectedLines": ["phase=Anomaly"]})
        res = run_audit([structural(1), anomaly(1, "science", "pool")], exp)
        r = by_check(res, "ledger-anomaly")
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("resource=science id=pool reason=ledger-vs-truth", "\n".join(r["details"]))
        self.assertEqual(1, la.exit_code(res))

    def test_structural_rows_and_rate(self):
        lines = [structural(i + 1, funds=1000.0 + i, t=36000.0 + i * 10) for i in range(12)]
        r = by_check(run_audit(lines), "recalc-rate")
        self.assertEqual(la.REPORT, r["status"])
        self.assertEqual(12, len(r["rows"]))
        self.assertEqual(7, r["peakPerMinute"])  # 0..60 s inclusive holds 7 rows
        self.assertAlmostEqual(1001.0, r["rows"][1]["funds"])

    def test_rate_expectation_arms_the_check(self):
        lines = [structural(i + 1, t=36000.0 + i) for i in range(10)]
        exp = la.parse_expectations({"maxStructuralPerMinute": 5})
        self.assertEqual(la.FAIL, by_check(run_audit(lines, exp), "recalc-rate")["status"])
        exp = la.parse_expectations({"maxStructuralPerMinute": 10})
        self.assertEqual(la.PASS, by_check(run_audit(lines, exp), "recalc-rate")["status"])

    def test_seq_restart_and_gap_reported(self):
        r = by_check(run_audit([structural(3), structural(5), structural(1)]), "recalc-rate")
        self.assertIn("recalcSeq restarts: 1, gaps (a burst with no structural line): 1",
                      r["details"][0])

    def test_peak_window(self):
        self.assertEqual(0, la.peak_window_count([]))
        self.assertEqual(3, la.peak_window_count([0, 30, 60, 61]))

    def test_pool_timeline_collapses_unchanged_rows(self):
        rows = la.parse_structural(la.parse_log(
            [structural(1, funds=10), structural(2, funds=10), structural(3, funds=20)]))
        out = la.pool_timeline(rows)
        self.assertEqual(4, len(out))  # header, row 1, collapse note, row 3
        self.assertIn("(1 unchanged recalc(s))", out[2])
        self.assertIn("20.00", out[3])


# ---------------------------------------------------------------------------
# 3
# ---------------------------------------------------------------------------

class PaidTwiceTests(unittest.TestCase):
    def test_credit_once_per_walk_passes(self):
        lines = [ms_reset(0), credited("FirstLaunch"), credited("RecordsSpeed", record=True),
                 ms_reset(2), credited("FirstLaunch"), credited("RecordsSpeed", record=True)]
        r = by_check(run_audit(lines), "paid-twice")
        self.assertEqual(la.PASS, r["status"])
        self.assertIn("2 walk(s), 4 credit line(s), 0 id(s)", r["summary"])

    def test_same_id_twice_in_one_walk_fails(self):
        lines = [ms_reset(0), credited("FirstLaunch"), credited("Kerbin/Orbit"),
                 credited("FirstLaunch")]
        res = run_audit(lines, la.parse_expectations({"expectedLines": ["Credited milestone"]}))
        r = by_check(res, "paid-twice")
        self.assertEqual(la.FAIL, r["status"])
        self.assertEqual([{"walk": 1, "walkLine": 1, "id": "FirstLaunch", "count": 2,
                           "lines": [2, 4]}], r["dups"])
        self.assertIn("FirstLaunch", "\n".join(la.credited_twice_table(r["dups"])))

    def test_seeded_reset_shape_splits_walks(self):
        lines = [ms_reset(0, seeded=True), credited("A"), ms_reset(1, seeded=True), credited("A")]
        self.assertEqual(la.PASS, by_check(run_audit(lines), "paid-twice")["status"])

    def test_other_modules_reset_lines_do_not_split(self):
        # ScienceModule / FacilitiesModule / ContractsModule print their own Reset lines.
        lines = [ms_reset(0), credited("A"),
                 P("VERBOSE", "ScienceModule", "Reset: cleared 3 subjects, runningScience=0, "
                   "totalCommittedSpendings=0, totalEffectiveEarnings=0"),
                 P("VERBOSE", "Facilities", "Reset: cleared 9 tracked facilities"),
                 credited("A")]
        self.assertEqual(la.FAIL, by_check(run_audit(lines), "paid-twice")["status"])

    def test_conversion_leg_rewalk_is_its_own_walk(self):
        # RunWalk's second RunWalkPass resets every module again, so it logs a Reset.
        lines = [ms_reset(0), credited("A"),
                 P("VERBOSE", "RecalcEngine",
                   "Conversion legs follow their cause: walking again with the rescaled legs"),
                 ms_reset(1), credited("A")]
        self.assertEqual(la.PASS, by_check(run_audit(lines), "paid-twice")["status"])

    def test_probe_and_projection_walks_are_silent_so_nothing_is_excluded(self):
        # A probe walk between two visible walks logs nothing (SuppressScope); the
        # visible walks' credits stay separated by their own resets.
        walks = la.split_milestone_walks(la.parse_log(
            [ms_reset(0), credited("A"), ms_reset(1), credited("A"), credited("B")]))
        self.assertEqual([1, 2], [w["index"] for w in walks])
        self.assertEqual([["A"], ["A", "B"]], [[c[0] for c in w["credits"]] for w in walks])

    def test_credits_before_any_reset_form_walk_zero(self):
        walks = la.split_milestone_walks(la.parse_log([credited("A"), credited("A"), ms_reset(2)]))
        self.assertEqual(0, walks[0]["index"])
        self.assertIsNone(walks[0]["startLine"])
        self.assertEqual(1, len(la.credited_twice(walks)))

    def test_no_milestone_line_skips(self):
        self.assertEqual(la.SKIP, by_check(run_audit([P("INFO", "X", "y")]), "paid-twice")["status"])

    def test_duplicate_line_is_not_a_credit(self):
        lines = [ms_reset(0), credited("A"),
                 P("VERBOSE", "Milestones", "Duplicate milestone 'A' zeroed at UT=20.0 "
                   "(recording=r2)")]
        self.assertEqual(la.PASS, by_check(run_audit(lines), "paid-twice")["status"])


# ---------------------------------------------------------------------------
# 4
# ---------------------------------------------------------------------------

class SubjectCapTests(unittest.TestCase):
    def test_within_cap_passes_both_shapes(self):
        lines = [earning("s1", 1.5, 1.5), earning("s1", 6.0, 6.0, cap_hit=True),
                 earning("s2", 6.0000001, 6.0)]
        r = by_check(run_audit(lines), "subject-over-cap")
        self.assertEqual(la.PASS, r["status"])
        self.assertIn("3 earning line(s), 0 over", r["summary"])

    def test_over_cap_fails(self):
        r = by_check(run_audit([earning("crewReport@KerbinSrfLanded", 7.5, 6.0, cap_hit=True)]),
                     "subject-over-cap")
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("subject=crewReport@KerbinSrfLanded creditedTotal=7.5 > maxValue=6.0",
                      r["details"][0])

    def test_exponent_values_parse(self):
        r = by_check(run_audit([earning("s", 1e-05, 2e-05)]), "subject-over-cap")
        self.assertEqual(la.PASS, r["status"])

    def test_unparsed_earning_line_fails(self):
        r = by_check(run_audit([P("VERBOSE", "ScienceModule", "Earning: subject=s, awarded=1")]),
                     "subject-over-cap")
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("did not parse", r["details"][-1])

    def test_no_earning_skips(self):
        self.assertEqual(la.SKIP, by_check(run_audit([]), "subject-over-cap")["status"])


# ---------------------------------------------------------------------------
# 5, 6, 8, 9, 12 + expectations
# ---------------------------------------------------------------------------

class LiteralScanTests(unittest.TestCase):
    def test_clean_log_passes_every_absence_check(self):
        res = run_audit([P("INFO", "Funds", "nothing to see")])
        for check in ("take-back", "take-back-off", "debt", "hard-stop", "refly-leftover"):
            self.assertEqual(la.PASS, by_check(res, check)["status"], check)
        self.assertEqual(0, la.exit_code(res))

    def test_take_back_fails_unless_expected(self):
        lines = [took_back(), shortfall()]
        self.assertEqual(la.FAIL, by_check(run_audit(lines), "take-back")["status"])
        exp = la.parse_expectations({"expected": ["take-back"]})
        r = by_check(run_audit(lines, exp), "take-back")
        self.assertEqual(la.EXPECTED, r["status"])
        self.assertIn("2 expected", r["summary"])

    def test_expected_line_literal_marks_only_matching_lines(self):
        exp = la.parse_expectations({"expectedLines": ["Took back"]})
        r = by_check(run_audit([took_back(), shortfall()], exp), "take-back")
        self.assertEqual(la.FAIL, r["status"])  # the shortfall is not covered
        self.assertEqual([True, False], [f["expected"] for f in r["findings"]])

    def test_take_back_off_is_never_expectable(self):
        exp = la.parse_expectations({"expectedLines": ["Timeline break"], "expected": ["take-back"]})
        self.assertEqual(la.FAIL, by_check(run_audit([break_off()], exp), "take-back-off")["status"])

    def test_debt(self):
        lines = [debt_charge("Funds"), debt_charge("ScienceModule"), debt_charge("Reputation"),
                 science_debt()]
        r = by_check(run_audit(lines), "debt")
        self.assertEqual(la.FAIL, r["status"])
        self.assertEqual(4, len(r["findings"]))
        exp = la.parse_expectations({"expected": ["debt"]})
        self.assertEqual(la.EXPECTED, by_check(run_audit(lines, exp), "debt")["status"])

    def test_hard_stops_fail_and_cannot_be_masked(self):
        exp = la.parse_expectations({"expected": ["not-affordable"],
                                     "expectedLines": ["ABORTING", "Failed", "refusing"]})
        for line in (aborting(), deserialize_fail(), refuse_upgrade()):
            self.assertEqual(la.FAIL, by_check(run_audit([line], exp), "hard-stop")["status"])

    def test_not_affordable_expectable(self):
        self.assertEqual(la.FAIL, by_check(run_audit([not_affordable()]), "hard-stop")["status"])
        for exp in (la.parse_expectations({"expected": ["not-affordable"]}),
                    la.parse_expectations({"expectedLines": ["NOT affordable"]})):
            self.assertEqual(la.EXPECTED,
                             by_check(run_audit([not_affordable()], exp), "hard-stop")["status"])
        sci = P("WARN", "ScienceModule", "StrategyActivate science NOT affordable: strategyId=x, "
                "cost=5, runningScience=1")
        exp = la.parse_expectations({"expected": ["not-affordable"]})
        self.assertEqual(la.EXPECTED, by_check(run_audit([sci], exp), "hard-stop")["status"])

    def test_not_affordable_expected_but_abort_still_fails(self):
        exp = la.parse_expectations({"expected": ["not-affordable"]})
        r = by_check(run_audit([not_affordable(), aborting()], exp), "hard-stop")
        self.assertEqual(la.FAIL, r["status"])

    def test_refly_leftovers_are_report_only(self):
        # One unbound line per InvokeRewind in a green Re-Fly lane (RW-5, RW-6).
        lines = [unbound(), retired()]
        res = run_audit(lines)
        r = by_check(res, "refly-leftover")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("2 Re-Fly leftover line(s) (report-only", r["summary"])
        self.assertEqual(0, la.exit_code(res))

    def test_refly_leftover_expectation_still_reads_expected(self):
        lines = [unbound(), retired()]
        exp = la.parse_expectations({"expected": ["refly-leftover"]})
        res = run_audit(lines, exp)
        self.assertEqual(la.EXPECTED, by_check(res, "refly-leftover")["status"])
        self.assertEqual([], res["unusedExpectations"])
        # A literal covering only one line leaves the check REPORT, never FAIL.
        exp = la.parse_expectations({"expectedLines": ["unbound-refly-provisional"]})
        self.assertEqual(la.REPORT, by_check(run_audit(lines, exp), "refly-leftover")["status"])
        # A lane that expected leftovers and showed none lists the expectation.
        res = run_audit([P("INFO", "X", "y")], la.parse_expectations({"expected": ["refly-leftover"]}))
        self.assertEqual(la.PASS, by_check(res, "refly-leftover")["status"])
        self.assertEqual(["refly-leftover"], res["unusedExpectations"])

    def test_literal_in_other_subsystem_ignored_where_scoped(self):
        # "Took back" is scoped to the TakeBack tag.
        r = by_check(run_audit([P("INFO", "UI", "Took back the window")]), "take-back")
        self.assertEqual(la.PASS, r["status"])

    def test_unused_expectations_are_listed(self):
        exp = la.parse_expectations({"expected": ["debt"], "expectedLines": ["NOT affordable"]})
        res = run_audit([P("INFO", "X", "y")], exp)
        self.assertEqual(["debt", "NOT affordable"], res["unusedExpectations"])
        self.assertIn("expectations never matched", la.render(res))
        self.assertEqual(0, la.exit_code(res))


class ExpectationsParseTests(unittest.TestCase):
    def test_none_is_empty(self):
        e = la.parse_expectations(None)
        self.assertEqual((), e.expected)
        self.assertFalse(e.merge_must_pass)

    def test_valid_table(self):
        e = la.parse_expectations({"expected": ["take-back"], "expectedLines": ["Took back"],
                                   "maxStructuralPerMinute": 30, "mergeMustPass": True}, "x.toml")
        self.assertEqual(("take-back",), e.expected)
        self.assertEqual(30.0, e.max_structural_per_minute)
        self.assertTrue(e.merge_must_pass)
        self.assertEqual("x.toml", e.source)

    def test_rejections(self):
        for bad in ({"expect": []}, {"expected": ["paid-twice"]}, {"expected": ["nope"]},
                    {"expected": "debt"}, {"expectedLines": [""]}, {"expectedLines": [3]},
                    {"maxStructuralPerMinute": 0}, {"maxStructuralPerMinute": True},
                    {"maxStructuralPerMinute": "5"}, {"mergeMustPass": "yes"}, ["debt"]):
            with self.assertRaises(ValueError, msg=repr(bad)):
                la.parse_expectations(bad)


# ---------------------------------------------------------------------------
# 7, 10, 11, 13 - 16
# ---------------------------------------------------------------------------

class ReportChecksTests(unittest.TestCase):
    def test_stock_pool_adjustment_dedupes_per_row(self):
        lines = [pool_adj("Funds", "250", "act_9"), pool_adj("Funds", "250", "act_9"),
                 pool_adj("Reputation", "1.5", "act_8"), pool_adj("ScienceModule", "-2", "act_7")]
        r = by_check(run_audit(lines), "stock-pool-adjustment")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("3 distinct row(s), 4 line(s)", r["summary"])
        self.assertIn("[Funds] id=act_9 amount=250 ut=12 walked 2x (first L1)", r["details"])
        self.assertIn("[Reputation] id=act_8 amount=1.5 ut=12 walked 1x (first L3)", r["details"])

    def test_deferred_patch_closed_before_scene_change(self):
        r = by_check(run_audit([deferred(), patched(), scene("FLIGHT", "SPACECENTER")]),
                     "deferred-patch")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("scene changes with a deferral open: 0", r["details"])
        self.assertIn("open at log end: no", r["details"])

    def test_deferred_patch_open_across_scene_change_is_reported_not_failed(self):
        lines = [deferred("pending tree 'Kerbal X' state=Limbo"), scene("FLIGHT", "SPACECENTER"),
                 patched(), deferred()]
        res = run_audit(lines)
        r = by_check(res, "deferred-patch")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("1 scene change(s) with one open", r["summary"])
        self.assertIn("reason 'pending tree '<tree>' state=Limbo': 1", r["details"])
        self.assertTrue(any(d.startswith("open at log end: yes, since L4") for d in r["details"]))
        self.assertEqual(0, la.exit_code(res))

    def test_deferred_patch_skip_without_lines(self):
        self.assertEqual(la.SKIP, by_check(run_audit([]), "deferred-patch")["status"])

    def test_load_kind_one_per_load_passes(self):
        lines = [classified("Cold", "Cold"), onload_done(),
                 classified("InSession", "QuickloadFlight", "FLIGHT"), onload_done(),
                 onload_done()]  # an inert / early-return load classifies nothing
        r = by_check(run_audit(lines), "load-kind")
        self.assertEqual(la.PASS, r["status"])
        self.assertIn("sequence: Cold(Cold)@SPACECENTER -> QuickloadFlight(InSession)@FLIGHT",
                      r["details"])
        self.assertIn("2 classification(s) over 3 OnLoad(s)", r["summary"])

    def test_two_classifications_in_one_load_fail(self):
        lines = [classified("Cold", "Cold"), classified("InSession", "StockRevert"), onload_done()]
        r = by_check(run_audit(lines), "load-kind")
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("second classification in the load that began at L1", r["details"][-1])

    def test_load_kind_without_terminators_reports_only(self):
        r = by_check(run_audit([classified(), classified()]), "load-kind")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("loads cannot be delimited", r["details"][1])

    def test_load_kind_skip(self):
        self.assertEqual(la.SKIP, by_check(run_audit([onload_done()]), "load-kind")["status"])

    def test_merge_refused_report_and_armed(self):
        lines = [dominance("pass"), dominance("refuse")]
        r = by_check(run_audit(lines), "merge-refused")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("2 verdict(s), 1 refused", r["summary"])
        exp = la.parse_expectations({"mergeMustPass": True})
        self.assertEqual(la.FAIL, by_check(run_audit(lines, exp), "merge-refused")["status"])
        self.assertEqual(la.PASS, by_check(run_audit([dominance("pass")], exp),
                                           "merge-refused")["status"])

    def test_merge_skipped_and_threw_lines_are_not_verdicts(self):
        lines = [P("INFO", "LedgerSwap", "Merge dominance check skipped (merge): no marker")]
        r = by_check(run_audit(lines), "merge-refused")
        self.assertIn("0 verdict(s)", r["summary"])

    def test_already_owned_and_same_craft(self):
        res = run_audit([owned(), pid_only(), guid_unknown()])
        self.assertIn("1 line(s)", by_check(res, "already-owned")["summary"])
        r = by_check(res, "same-craft")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("2 line(s)", r["summary"])

    def test_finalizer_oddities_name_recording_ids(self):
        r = by_check(run_audit([subsurface("recA"), subsurface("recA"), fallback("recB")]),
                     "finalizer-oddity")
        text = "\n".join(r["details"])
        self.assertIn("'subsurface-destroyed-suppressed': 2 line(s) ids: recA", text)
        self.assertIn("ids: recB", text)


# ---------------------------------------------------------------------------
# 17
# ---------------------------------------------------------------------------

class GroundTruthTests(unittest.TestCase):
    def test_clean_result_passes_and_lists_report_only_facets(self):
        lines = gt_report([("Milestone", "ValueMismatch", "x"),
                           ("Funds", "UpliftClampedExpected", "y")]) + [gt_result(0, 2)]
        r = by_check(run_audit(lines), "ground-truth")
        self.assertEqual(la.PASS, r["status"])
        self.assertIn("report-only facets: Funds, Milestone", r["summary"])

    def test_hard_failures_fail(self):
        lines = gt_report([("Funds", "ValueMismatch", "x")]) + [gt_result(1, 0)]
        r = by_check(run_audit(lines), "ground-truth")
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("facet Funds kind=ValueMismatch x1 (hard)", r["details"])

    def test_facet_hardness_mirrors_the_csharp_rule(self):
        self.assertTrue(la.gt_facet_is_hard("SciencePool", "ValueMismatch", ""))
        self.assertFalse(la.gt_facet_is_hard("Reputation", "UpliftClampedExpected", ""))
        self.assertTrue(la.gt_facet_is_hard("Vessel", "Consistency", "a guidCorroborated=true b"))
        self.assertFalse(la.gt_facet_is_hard("Vessel", "Consistency", "guidCorroborated=false"))
        self.assertFalse(la.gt_facet_is_hard("Contract", "Missing", ""))

    def test_skip_without_result(self):
        self.assertEqual(la.SKIP, by_check(run_audit([]), "ground-truth")["status"])


# ---------------------------------------------------------------------------
# 18, 19
# ---------------------------------------------------------------------------

class WarningsTests(unittest.TestCase):
    def test_counts_by_subsystem(self):
        lines = [P("WARN", "Funds", "a 1"), P("WARN", "Funds", "a 2"), P("ERROR", "Ledger", "b"),
                 P("INFO", "Funds", "c")]
        r = by_check(run_audit(lines), "warnings")
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("2 WARN, 1 ERROR in 2 subsystem(s)", r["summary"])

    def test_key_normalisation(self):
        k1 = la.warning_key("Funds", "WARN", "row act_0123456789abcdef0123456789abcdef at ut=12.5 "
                            "for 'Kerbal X' guid=0f8fad5b-d9cb-469f-a165-70867728950e")
        k2 = la.warning_key("Funds", "WARN", "row act_fedcba9876543210fedcba9876543210 at ut=99 "
                            "for 'Other' guid=7c9e6679-7425-40de-944b-e07fc1f90ae7")
        self.assertEqual(k1, k2)
        self.assertNotEqual(k1, la.warning_key("Science", "WARN", "row"))

    def test_baseline_diff(self):
        base = [P("WARN", "Funds", "drift 1.5"), P("WARN", "Ledger", "old")]
        mine = [P("WARN", "Funds", "drift 3.0"), P("WARN", "Ledger", "brand new thing 7")]
        r = by_check(run_audit(mine, baseline=base), "warnings")
        self.assertEqual(["WARN [Ledger] brand new thing <n>"], r["newKeys"])
        self.assertIn("1 key(s) not in the baseline", r["summary"])


LEDGER_TEXT = """version = 0
recordingSchemaGeneration = 4
GAME_ACTION
{
	ut = 0
	type = 20
	actionId = act_seed
	initialFunds = 500000
}
GAME_ACTION
{
	ut = 0
	type = 15
	actionId = act_fac
	recordingId =
	CHILD
	{
		ut = 99
	}
}
GAME_ACTION
{
	ut = 12.5
	type = 4
	actionId = act_ms
}
"""


class LedgerUt0Tests(unittest.TestCase):
    def test_parse_ledger_actions(self):
        rows = la.parse_ledger_actions(LEDGER_TEXT)
        self.assertEqual(3, len(rows))
        self.assertEqual("0", rows[1]["ut"])  # the child node's ut does not leak

    def test_non_seed_ut0_reported(self):
        r = la.check_ledger_ut0([("saves/x/Parsek/GameState/ledger.pgld", LEDGER_TEXT)])
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("1 non-seed row(s) at UT 0", r["summary"])
        self.assertIn("  FacilityUpgrade actionId=act_fac recordingId=-", r["details"])

    def test_skip_without_ledger(self):
        self.assertEqual(la.SKIP, la.check_ledger_ut0([])["status"])


# ---------------------------------------------------------------------------
# walk-past-clock
# ---------------------------------------------------------------------------

RW2_POOLS = (151682.2, 192114.2)


class WalkPastClockTests(unittest.TestCase):
    def _r(self, lines, exp=None):
        return by_check(run_audit(lines, exp), "walk-past-clock")

    def test_rw2_shape_fails(self):
        # RW-2's first flight: StockRevert load walked cutoffUT=null with rows ahead.
        lines = [insession_decision(revert=True), input_summary("null"), ms_reset(0),
                 patched(), patch_move("Funds", *RW2_POOLS)]
        res = run_audit(lines, la.parse_expectations({"expected": ["refly-leftover"],
                                                      "expectedLines": ["useCurrentUtCutoff"]}))
        r = by_check(res, "walk-past-clock")
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("isRevert=True", r["findings"][0]["note"])
        self.assertIn("Funds 151682.20 -> 192114.20 (L5)", r["findings"][0]["note"])
        self.assertFalse(r["findings"][0]["expectable"])
        self.assertEqual(1, la.exit_code(res))

    def test_rw2_real_order_fails(self):
        # The live order: the revert walk runs synchronously inside OnLoad, BEFORE the
        # in-session decision line that says rows lie ahead.
        lines = [classified("InSessionOther", "StockRevert", "FLIGHT"), budget_ready(),
                 input_summary("null"), patched(), patch_move("Funds", *RW2_POOLS),
                 insession_decision(revert=True)]
        r = self._r(lines)
        self.assertEqual(la.FAIL, r["status"])
        note = r["findings"][0]["note"]
        self.assertIn("revert budget walk (ApplyBudgetDeduction)", note)
        self.assertIn("the same load's decision (L6)", note)
        self.assertEqual(2, r["findings"][0]["n"])

    def test_a_revert_walk_after_the_decision_binds_too(self):
        # Singletons not ready yet: the coroutine walks a few frames after OnLoad.
        lines = [classified("InSessionOther", "StockRevert", "FLIGHT"),
                 insession_decision(use=True), input_summary("150000"), patched(),
                 patch_move("Funds", 5.0, 1.0), budget_ready(t=36001.0), patched(t=36001.0),
                 patch_move("Funds", 1.0, 9.0, t=36001.0)]
        self.assertEqual(la.FAIL, self._r(lines)["status"])

    def test_the_fixed_revert_walk_cuts_off_and_passes(self):
        lines = [classified("InSessionOther", "StockRevert", "FLIGHT"), budget_ready(),
                 revert_budget(), input_summary("185785.98"), patched(),
                 patch_move("Funds", 151682.2, 160000.0), insession_decision(use=True)]
        r = self._r(lines)
        self.assertEqual(la.PASS, r["status"])
        units = la.split_patch_walks(la.parse_log(lines))
        self.assertEqual(["revert-budget", "in-session-load"], [u["kind"] for u in units])

    def test_a_fixed_revert_walk_with_no_ready_clock_still_binds(self):
        lines = [classified("InSessionOther", "StockRevert", "FLIGHT"), budget_ready(),
                 revert_budget(ready=False), patched(), patch_move("Funds", 1.0, 9.0),
                 insession_decision()]
        r = self._r(lines)
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("clockReady=False", r["findings"][0]["note"])

    def test_what_does_not_bind(self):
        rise = [budget_ready(), patched(), patch_move("Funds", 1.0, 9.0)]
        cases = (
            # no Load classified line: the load is unknown
            rise + [insession_decision()],
            # another load
            [classified()] + rise + [classified(), insession_decision()],
            # too far apart in wall time
            [classified()] + rise + [insession_decision(t=36000.0 + la.LOAD_BIND_SECONDS + 1)],
            # the decision says nothing lies ahead, or the clock was not ready
            [classified()] + rise + [insession_decision(future=False)],
            [classified()] + rise + [insession_decision(ready=False)],
            # a plain walk after the decision is a later walk of the scene
            [classified(), insession_decision(), deferred(), patched(),
             patch_move("Funds", 1.0, 2.0)],
        )
        for lines in cases:
            r = self._r(lines)
            self.assertNotEqual(la.FAIL, r["status"], lines)
            self.assertEqual([], r["findings"])

    def test_an_already_applied_budget_walk_is_no_walk(self):
        units = la.split_patch_walks(la.parse_log(
            [classified(), budget_ready(), budget_done(), insession_decision(), deferred()]))
        self.assertEqual(["in-session-load"], [u["kind"] for u in units])

    def test_without_the_input_summary_still_binds(self):
        # The cutoff-less input summary is VerboseOnChange and often absent.
        lines = [insession_decision(scn="EDITOR"), patched(), patch_move("Science", 10.0, 30.0)]
        self.assertEqual(la.FAIL, self._r(lines)["status"])

    def test_deferred_or_falling_or_cutoff_walks_pass(self):
        for lines in ([insession_decision(), deferred()],
                      [insession_decision(), patched(), patch_move("Funds", 200.0, 100.0)],
                      [insession_decision(use=True), input_summary("150000"), patched(),
                       patch_move("Funds", 1.0, 2.0)],
                      [insession_decision(future=False), patched(), patch_move("Funds", 1, 2)]):
            self.assertEqual(la.PASS, self._r(lines)["status"], lines[0])

    def test_a_walk_after_a_deferral_is_its_own_unit(self):
        # The deferred walk patched nothing; the next patched walk has no decision line.
        r = self._r([insession_decision(), deferred(), patched(), patch_move("Funds", 1.0, 2.0)])
        self.assertEqual(la.REPORT, r["status"])
        self.assertEqual([], r["findings"])

    def test_a_rise_after_the_next_walk_starts_is_not_this_walks(self):
        lines = [insession_decision(), patched(), patch_move("Funds", 2.0, 1.0), ms_reset(0),
                 patch_move("Funds", 1.0, 2.0)]
        self.assertEqual(la.PASS, self._r(lines)["status"])

    def test_unready_clock_is_report_not_fail(self):
        r = self._r([insession_decision(ready=False), patched(), patch_move("Funds", 1.0, 5.0)])
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("clock was not ready", "\n".join(r["details"]))
        # BUG-F: a cold load before the clock is ready reads every row as ahead.
        r = self._r([kspload_decision(ready=False), patched(), patch_move("Funds", 1.0, 5.0)])
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("unready clock", "\n".join(r["details"]))

    def test_cold_load_with_rows_ahead_of_a_ready_clock_fails(self):
        r = self._r([kspload_decision(), patched(), patch_move("Reputation", 1.0, 4.0)])
        self.assertEqual(la.FAIL, r["status"])

    def test_untrusted_commit_walk_is_report_with_the_reason(self):
        r = self._r([commit_full(), patched(), patch_move("Funds", 1.0, 5.0)])
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("does not check for rows ahead", "\n".join(r["details"]))
        # With no rise there is nothing to report.
        self.assertEqual(la.PASS, self._r([commit_full(), patched(),
                                           patch_move("Funds", 5.0, 1.0)])["status"])

    def test_current_timeline_full_replay(self):
        # currentUtReady=True means HasActionsAfterUT said nothing lies ahead.
        self.assertEqual(la.PASS, self._r([current_full(True), patched(),
                                           patch_move("Funds", 1.0, 5.0)])["status"])
        self.assertEqual(la.REPORT, self._r([current_full(False), patched(),
                                             patch_move("Funds", 1.0, 5.0)])["status"])

    def test_refly_start_horizon_walk_is_by_design(self):
        lines = [input_summary("1.7976931348623157E+308"), ms_reset(0), patched(),
                 patch_move("Funds", 150632.2, 191064.2)]
        r = self._r(lines)
        self.assertEqual(la.REPORT, r["status"])
        text = "\n".join(r["details"])
        self.assertIn("Re-Fly start horizon walks (by design, RewindInvoker): 1", text)
        self.assertIn("RecalculateAndPatch(double.MaxValue)", text)
        # .NET Framework's shorter round-trip rendering reads the same.
        self.assertEqual(la.REPORT, self._r([input_summary("1.79769313486232E+308"), patched(),
                                             patch_move("Funds", 1.0, 2.0)])["status"])

    def test_refly_start_after_a_deferred_decision_is_its_own_walk(self):
        lines = [insession_decision(), deferred(), input_summary("1.7976931348623157E+308"),
                 patched(), patch_move("Funds", 1.0, 9.0)]
        self.assertEqual(la.REPORT, self._r(lines)["status"])

    def test_plain_walk_with_no_decision_line(self):
        r = self._r([patched(), patch_move("Funds", 1.0, 2.0)])
        self.assertEqual(la.REPORT, r["status"])
        self.assertIn("no decision or input line", "\n".join(r["details"]))

    def test_skip_without_walk_lines(self):
        self.assertEqual(la.SKIP, self._r([P("INFO", "X", "y")])["status"])


# ---------------------------------------------------------------------------
# guarded-clamp
# ---------------------------------------------------------------------------

REAL_GUARDED = ("[LOG 10:00:00.000] [Parsek][WARN][KspStatePatcher] PatchFunds: GUARDED UPLIFT "
                "clamped resource=Funds running=153564.18 live=150632.18 wouldBeTarget=150632.18 "
                "clampedTo=110200.18 (no time-travel context) - spent value held; ledger may be "
                "missing a spending channel")


class GuardedClampTests(unittest.TestCase):
    def _r(self, lines, exp=None):
        return by_check(run_audit(lines, exp), "guarded-clamp")

    def test_the_real_line_parses_with_its_signed_leak(self):
        g = la.parse_guarded(la.parse_log([REAL_GUARDED]))
        self.assertEqual(1, len(g))
        self.assertEqual(("Funds", "UPLIFT"), (g[0]["resource"], g[0]["dir"]))
        self.assertAlmostEqual(-40432.0, g[0]["leak"], places=6)

    def test_opposite_pair_in_one_burst_passes(self):
        lines = [guarded("UPLIFT", would=1000.0, clamped=200.0, t=36000.0),
                 guarded("DRAWDOWN", would=200.0, clamped=1000.0, t=36002.0)]
        res = run_audit(lines)
        r = by_check(res, "guarded-clamp")
        self.assertEqual(la.PASS, r["status"])
        self.assertEqual(1, r["pairs"])
        self.assertIn("2 GUARDED line(s): 1 paired transient(s), 0 one-sided", r["summary"])
        self.assertIn("UPLIFT -800.00 + L2 DRAWDOWN 800.00 = 0.00", "\n".join(r["details"]))
        self.assertEqual(0, la.exit_code(res))

    def test_the_filed_amount_is_the_leak(self):
        # StockPoolReference era: the row is carried, so clampedTo == wouldBeTarget and
        # the patch's filed amount is the leak (CS-3 2026-10-26_0146, 2.35 s apart).
        lines = [filed(amount=-2.0, t=36000.0),
                 guarded("UPLIFT", running=12.0, live=10.0, would=10.0, clamped=10.0, t=36000.0),
                 filed(amount=2.0, t=36002.35),
                 guarded("DRAWDOWN", running=10.0, live=12.0, would=12.0, clamped=12.0,
                         t=36002.35)]
        g = la.parse_guarded(la.parse_log(lines))
        self.assertEqual([-2.0, 2.0], [x["leak"] for x in g])
        self.assertEqual(["filed L1", "filed L3"], [x["leakFrom"] for x in g])
        r = self._r(lines)
        self.assertEqual(la.PASS, r["status"])
        self.assertEqual(1, r["pairs"])

    def test_leak_fallbacks(self):
        # A filed line of another patch (older than a second) is not this clamp's.
        g = la.parse_guarded(la.parse_log([
            filed(amount=-7.0, t=36000.0),
            guarded("UPLIFT", running=12.0, live=10.0, would=10.0, clamped=10.0, t=36005.0)]))
        self.assertEqual((-2.0, "live-running"), (g[0]["leak"], g[0]["leakFrom"]))
        # A filed line of another resource is not this clamp's either.
        g = la.parse_guarded(la.parse_log([
            filed("Science", amount=-7.0),
            guarded("UPLIFT", would=1000.0, clamped=200.0)]))
        self.assertEqual((-800.0, "clampedTo-wouldBeTarget"), (g[0]["leak"], g[0]["leakFrom"]))
        # A filed amount of the wrong sign for the direction is not this clamp's.
        g = la.parse_guarded(la.parse_log([
            filed(amount=7.0),
            guarded("UPLIFT", running=12.0, live=10.0, would=10.0, clamped=10.0)]))
        self.assertEqual((-2.0, "live-running"), (g[0]["leak"], g[0]["leakFrom"]))
        # A filed line is used once.
        g = la.parse_guarded(la.parse_log([
            filed(amount=-7.0),
            guarded("UPLIFT", running=12.0, live=10.0, would=10.0, clamped=10.0),
            guarded("UPLIFT", running=12.0, live=10.0, would=10.0, clamped=10.0)]))
        self.assertEqual([-7.0, -2.0], [x["leak"] for x in g])

    def test_one_sided_fails_unless_expected(self):
        r = self._r([REAL_GUARDED])
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("UPLIFT leak=-40432.00", r["findings"][0]["note"])
        self.assertEqual(la.EXPECTED, self._r(
            [REAL_GUARDED], la.parse_expectations({"expected": ["guarded-one-sided"]}))["status"])
        self.assertEqual(la.EXPECTED, self._r(
            [REAL_GUARDED], la.parse_expectations({"expectedLines": ["GUARDED UPLIFT"]}))["status"])

    def test_what_does_not_pair(self):
        cases = (
            # another resource
            [guarded("UPLIFT", "Funds", would=10.0, clamped=0.0),
             guarded("DRAWDOWN", "Science", would=0.0, clamped=10.0)],
            # outside the burst
            [guarded("UPLIFT", would=10.0, clamped=0.0, t=36000.0),
             guarded("DRAWDOWN", would=0.0, clamped=10.0, t=36000.0 + la.GUARDED_BURST_SECONDS + 1)],
            # same direction
            [guarded("UPLIFT", would=10.0, clamped=0.0), guarded("UPLIFT", would=0.0, clamped=-10.0)],
            # does not net out
            [guarded("UPLIFT", would=100.0, clamped=0.0), guarded("DRAWDOWN", would=0.0, clamped=50.0)],
        )
        for lines in cases:
            r = self._r(lines)
            self.assertEqual(la.FAIL, r["status"], lines)
            self.assertEqual(0, r["pairs"])

    def test_pairing_is_one_to_one(self):
        lines = [guarded("UPLIFT", would=10.0, clamped=0.0),
                 guarded("DRAWDOWN", would=0.0, clamped=10.0),
                 guarded("DRAWDOWN", would=0.0, clamped=10.0)]
        r = self._r(lines)
        self.assertEqual(1, r["pairs"])
        self.assertEqual([3], [f["n"] for f in r["findings"]])

    def test_unparsed_guarded_line_fails_and_cannot_be_expected(self):
        exp = la.parse_expectations({"expected": ["guarded-one-sided"], "expectedLines": ["GUARDED"]})
        r = self._r([P("WARN", "KspStatePatcher", "PatchFunds: GUARDED UPLIFT clamped garbled")], exp)
        self.assertEqual(la.FAIL, r["status"])

    def test_no_line_passes(self):
        self.assertEqual(la.PASS, self._r([P("INFO", "X", "y")])["status"])


# ---------------------------------------------------------------------------
# leak-shape
# ---------------------------------------------------------------------------

class LeakShapeTests(unittest.TestCase):
    def _r(self, lines):
        return by_check(run_audit(lines), "leak-shape")

    def test_leak_equal_to_one_zeroed_duplicate_fails(self):
        r = self._r([ms_reset(0), ms_funds_skipped("FirstLaunch", 800.0), earn_skipped(100.0),
                     patched(), filed("Funds", 800.0)])
        self.assertEqual(la.FAIL, r["status"])
        self.assertEqual("leak-equals-zeroed", r["findings"][0]["key"])
        self.assertIn("the zeroed row at L2", r["findings"][0]["note"])

    def test_leak_equal_to_the_sum_fails(self):
        r = self._r([ms_reset(0), ms_funds_skipped("A", 800.0), ctr_funds_skipped(5000.0),
                     patched(), filed("Funds", 5800.004)])
        self.assertEqual(la.FAIL, r["status"])
        self.assertIn("the sum 5800.00 of 2 zeroed row(s)", r["findings"][0]["note"])

    def test_science_twin(self):
        self.assertEqual(la.FAIL, self._r([ms_reset(0), sci_skipped(2.0), patched(),
                                           filed("Science", 2.0)])["status"])
        # A science leak never matches a funds row.
        self.assertEqual(la.PASS, self._r([ms_reset(0), ms_funds_skipped("A", 2.0), patched(),
                                           filed("Science", 2.0)])["status"])

    def test_what_does_not_match(self):
        for lines in (
                [ms_reset(0), ms_funds_skipped("A", 800.0), patched(), filed("Funds", 800.5)],
                [ms_reset(0), ms_funds_skipped("A", 800.0), patched(), filed("Funds", -800.0)],
                [ms_reset(0), ms_funds_skipped("A", 800.0), patched(),
                 filed("Funds", 800.0, reason="adopted-gap")],
                # the zeroed row belongs to the previous walk
                [ms_reset(0), ms_funds_skipped("A", 800.0), ms_reset(1), patched(),
                 filed("Funds", 800.0)]):
            self.assertEqual(la.PASS, self._r(lines)["status"], lines)

    def test_rw5_leak_after_discard_refly_load_fails(self):
        lines = [scene("FLIGHT", "SPACECENTER"), ref_loaded("DiscardReFly"),
                 insession_decision(use=True, scn="SPACECENTER"), patched(),
                 filed("Funds", -40432.0), guarded("UPLIFT")]
        res = run_audit(lines)
        r = by_check(res, "leak-shape")
        self.assertEqual(la.FAIL, r["status"])
        self.assertEqual("leak-after-discard-refly", r["findings"][0]["key"])
        self.assertIn("patched walk 1 after the Loaded (DiscardReFly) of L2", r["findings"][0]["note"])
        self.assertEqual(la.FAIL, by_check(res, "guarded-clamp")["status"])

    def test_the_currency_repatch_is_inside_the_discard_window(self):
        lines = [ref_loaded("DiscardReFly"), patched(), patched(), filed("Funds", -5.0)]
        self.assertEqual(la.FAIL, self._r(lines)["status"])

    def test_discard_window_ends(self):
        for lines in ([ref_loaded("DiscardReFly"), patched(), patched(), patched(),
                       filed("Funds", -5.0)],
                      [ref_loaded("DiscardReFly"), scene("SPACECENTER", "FLIGHT"), patched(),
                       filed("Funds", -5.0)],
                      [ref_loaded("DiscardReFly"), ref_loaded("InSession"), patched(),
                       filed("Funds", -5.0)],
                      [ref_loaded("InSession"), patched(), filed("Funds", -5.0)]):
            self.assertEqual(la.PASS, self._r(lines)["status"], lines)

    def test_counts(self):
        r = self._r([patched(), filed("Funds", 3.0), filed("Science", 1.0, reason="carried-removed")])
        self.assertIn("2 StockPoolAdjustment filed, 1 reason=leak, 0 repeat", r["summary"])


# ---------------------------------------------------------------------------
# Whole audit, render, exit code
# ---------------------------------------------------------------------------

def green_run():
    return [
        scene("MAINMENU", "SPACECENTER", 36000), classified(t=36001), onload_done(36001.5),
        ms_reset(0, t=36002), credited("FirstLaunch", t=36002),
        earning("s1", 1.5, 1.5, t=36002), patched(36002), structural(1, t=36002),
        deferred(t=36100), scene("SPACECENTER", "FLIGHT", 36110), patched(36200),
        ms_reset(1, t=36200), credited("FirstLaunch", t=36200), structural(2, funds=26000, t=36200),
        dominance("pass", 36300), *gt_report([]), gt_result(0, 0, 36301),
    ]


class AuditTests(unittest.TestCase):
    def test_green_run_has_no_fail(self):
        res = run_audit(green_run())
        self.assertEqual(la.PASS, res["verdict"])
        self.assertEqual(0, la.exit_code(res))
        self.assertEqual(0, res["counts"][la.FAIL])
        self.assertEqual(len(la.CHECK_IDS), len(res["results"]))
        self.assertEqual(list(la.CHECK_IDS), [r["check"] for r in res["results"]])

    def test_green_refly_lane_shapes_do_not_fail(self):
        # RW-5 / RW-6 after their fixes: leftovers, a transient GUARDED pair, the Re-Fly
        # start's horizon walk and a cutoff walk at the discard load.
        lines = green_run() + [
            unbound(36400), input_summary("1.7976931348623157E+308", t=36401), ms_reset(1, t=36401),
            patched(36401), patch_move("Funds", 150632.2, 191064.2, 36401),
            filed("Funds", -800.0, t=36500),
            guarded("UPLIFT", would=1000.0, clamped=200.0, t=36500),
            filed("Funds", 800.0, t=36501),
            guarded("DRAWDOWN", would=200.0, clamped=1000.0, t=36501),
            scene("FLIGHT", "SPACECENTER", 36600), ref_loaded("DiscardReFly", 36601),
            insession_decision(use=True, scn="SPACECENTER", t=36601),
            input_summary("150000", t=36601), patched(36601),
            patch_move("Funds", 191064.2, 150632.2, 36601), retired(36602)]
        res = run_audit(lines)
        self.assertEqual(0, res["counts"][la.FAIL], [
            (r["check"], r["summary"]) for r in res["results"] if r["status"] == la.FAIL])
        self.assertEqual(la.REPORT, by_check(res, "refly-leftover")["status"])
        self.assertEqual(la.REPORT, by_check(res, "walk-past-clock")["status"])
        self.assertEqual(la.PASS, by_check(res, "guarded-clamp")["status"])
        self.assertEqual(la.PASS, by_check(res, "leak-shape")["status"])
        la.render(res).encode("ascii")

    def test_one_fail_flips_exit_code(self):
        res = run_audit(green_run() + [aborting()])
        self.assertEqual(la.FAIL, res["verdict"])
        self.assertEqual(1, la.exit_code(res))

    def test_render_sections(self):
        text = la.render(run_audit(green_run() + [ms_reset(1), credited("X"), credited("X")]))
        for heading in ("SUMMARY", "RECALC / POOL TIMELINE", "IDENTITIES CREDITED TWICE WITHIN "
                        "ONE LEDGER WALK", "DETAILS", "verdict: FAIL"):
            self.assertIn(heading, text)
        self.assertIn("26000.00", text)
        text.encode("ascii")

    def test_every_check_has_a_title_and_unique_id(self):
        self.assertEqual(len(la.CHECK_IDS), len(set(la.CHECK_IDS)))
        for check, _, _ in la.SOURCE_ANCHORS:
            self.assertIn(check, la.CHECK_IDS)
        self.assertEqual(set(la.EXPECTABLE_NAMES), set(la.EXPECTABLE_CHECK))
        for name, check in la.EXPECTABLE_CHECK.items():
            self.assertIn(check, la.CHECK_IDS, name)


# ---------------------------------------------------------------------------
# The thin shell
# ---------------------------------------------------------------------------

class ShellTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="ledgeraudit-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def _write(self, rel, text, gz=False):
        path = os.path.join(self.tmp, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        if gz:
            with gzip.open(path, "wt", encoding="utf-8") as fh:
                fh.write(text)
        else:
            with open(path, "w", encoding="utf-8") as fh:
                fh.write(text)
        return path

    def _run(self, argv):
        out = io.StringIO()
        err = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = tool.run(argv)
        return code, out.getvalue(), err.getvalue()

    def test_run_folder_with_gz_log_and_ledger(self):
        self._write("KSP.log.gz", "\n".join(green_run()) + "\n", gz=True)
        self._write("saves/s/Parsek/GameState/ledger.pgld", LEDGER_TEXT)
        code, out, _ = self._run([self.tmp])
        self.assertEqual(0, code)
        self.assertIn("verdict: PASS", out)
        with open(os.path.join(self.tmp, "audit.txt"), encoding="utf-8") as fh:
            self.assertEqual(out, fh.read())
        self.assertIn("1 non-seed row(s) at UT 0", out)

    def test_log_one_level_down(self):
        self._write("2026-10-01_1200_X_shots/KSP.log", "\n".join(green_run()) + "\n")
        code, out, _ = self._run([self.tmp, "--no-write"])
        self.assertEqual(0, code)
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "audit.txt")))

    def test_fail_exit_and_out_path(self):
        log = self._write("KSP.log", "\n".join(green_run() + [not_affordable()]) + "\n")
        out_path = os.path.join(self.tmp, "custom.txt")
        code, _, _ = self._run([log, "--out", out_path])
        self.assertEqual(1, code)
        self.assertTrue(os.path.isfile(out_path))

    def test_toml_and_json_expectations(self):
        self._write("KSP.log", "\n".join(green_run() + [not_affordable()]) + "\n")
        toml = self._write("exp.toml", 'expectedLines = ["NOT affordable"]\n')
        code, out, _ = self._run([self.tmp, "--expect", toml, "--no-write"])
        self.assertEqual(0, code)
        self.assertIn("EXPECTED  hard-stop", out)
        js = self._write("exp.json", json.dumps({"expected": ["not-affordable"]}))
        code, _, _ = self._run([self.tmp, "--expect", js, "--no-write"])
        self.assertEqual(0, code)

    def test_bad_expectations_and_missing_log_exit_2(self):
        self._write("KSP.log", "\n".join(green_run()) + "\n")
        bad = self._write("bad.toml", 'expected = ["paid-twice"]\n')
        code, _, err = self._run([self.tmp, "--expect", bad, "--no-write"])
        self.assertEqual(2, code)
        self.assertIn("cannot read EXPECTED", err)
        code, _, err = self._run([os.path.join(self.tmp, "nope"), "--no-write"])
        self.assertEqual(2, code)
        empty = os.path.join(self.tmp, "empty")
        os.makedirs(empty)
        code, _, err = self._run([empty, "--no-write"])
        self.assertEqual(2, code)
        self.assertIn("no KSP.log", err)

    def test_stdout_under_cp1252_does_not_crash(self):
        # A KSP.log line can carry an arrow; a redirect under cp1252 used to raise.
        self._write("KSP.log", "\n".join(green_run() + [
            P("WARN", "Funds", "drift \u2192 kept \u2014 x")]) + "\n")
        raw = io.BytesIO()
        wrapper = io.TextIOWrapper(raw, encoding="cp1252", errors="strict", newline="\n")
        with contextlib.redirect_stdout(wrapper), contextlib.redirect_stderr(io.StringIO()):
            code = tool.run([self.tmp, "--no-write", "--baseline", self._write(
                "base/KSP.log", "\n".join(green_run()) + "\n")])
        wrapper.flush()
        self.assertEqual(0, code)
        text = raw.getvalue().decode("utf-8")
        self.assertIn("verdict: PASS", text)
        self.assertIn("WARN [Funds] drift \u2192 kept \u2014 x", text)

    def test_write_text_safely_on_a_stream_that_cannot_reconfigure(self):
        class StrictCp1252(object):
            encoding = "cp1252"

            def __init__(self):
                self.parts = []

            def write(self, text):
                text.encode("cp1252")  # raises UnicodeEncodeError like a cp1252 console
                self.parts.append(text)

            def flush(self):
                pass

        s = StrictCp1252()
        tool.write_text_safely("a \u2192 b\n", s)
        self.assertEqual(["a ? b\n"], s.parts)
        with self.assertRaises(UnicodeEncodeError):
            StrictCp1252().write("\u2192")

    def test_write_text_safely_falls_back_to_the_buffer(self):
        class NoReconfigure(object):
            encoding = "ascii"

            def __init__(self):
                self.buffer = io.BytesIO()

            def write(self, text):
                text.encode("ascii")
                self.buffer.write(text.encode("ascii"))

            def flush(self):
                pass

        s = NoReconfigure()
        tool.write_text_safely("x\u2192y", s)
        self.assertEqual(b"x?y", s.buffer.getvalue())

    def test_baseline(self):
        self._write("KSP.log", "\n".join(green_run() + [P("WARN", "Ledger", "new 1")]) + "\n")
        base = self._write("base/KSP.log", "\n".join(green_run()) + "\n")
        code, out, _ = self._run([self.tmp, "--baseline", base, "--no-write"])
        self.assertEqual(0, code)
        self.assertIn("WARN [Ledger] new <n>", out)


# ---------------------------------------------------------------------------
# Source sync: the C# literals the audit keys on
# ---------------------------------------------------------------------------

def _strip_cs_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def _src(rel):
    with open(os.path.join(REPO_ROOT, *rel.split("/")), "r", encoding="utf-8") as fh:
        return _strip_cs_comments(fh.read().replace("\r\n", "\n"))


def _method_body(src, name):
    """Body of the method DECLARED as ``name`` (a call site never matches: the
    pattern needs an access modifier and a return type in front)."""
    m = re.search(r"(?:internal|private|public|protected)\s+(?:static\s+)?[\w<>\[\], ?]+?\s+"
                  r"%s\s*(?:<[^>]*>)?\s*\(" % re.escape(name), src)
    if not m:
        return None
    start = src.index("{", m.end())
    depth = 0
    for i in range(start, len(src)):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[start:i + 1]
    return None


class SourceSyncTests(unittest.TestCase):
    def test_every_anchor_literal_is_in_its_source_file(self):
        missing = []
        cache = {}
        for check, rel, lit in la.SOURCE_ANCHORS:
            src = cache.get(rel)
            if src is None:
                src = cache[rel] = _src(rel)
            if lit not in src:
                missing.append("%s: %r not in %s" % (check, lit, rel))
        self.assertEqual([], missing)

    def test_parsek_log_line_shape(self):
        src = _src(la.SRC_LOG)
        self.assertIn('string line = $"[Parsek][{level}][{safeSubsystem}] {safeMessage}";', src)
        for level in ("INFO", "VERBOSE", "WARN", "ERROR"):
            self.assertIn('Write("%s", subsystem, message);' % level, src)

    def test_structural_and_anomaly_field_order(self):
        src = _src(la.SRC_LEDGER_TRACE)
        self.assertRegex(src, r'(?s)"phase=Structural"\s*\+ " recalcSeq=".*?\+ " funds=".*?'
                         r'\+ " science=".*?\+ " rep=".*?\+ " facilities=\[".*?'
                         r'\+ " targetTechNodes=".*?\+ " contracts=".*?\+ " cutoff=".*?'
                         r'\+ " authReduction="')
        self.assertRegex(src, r'(?s)"phase=Anomaly"\s*\+ " recalcSeq=".*?\+ " resource=".*?'
                         r'\+ " id=".*?\+ " reason="')
        self.assertIn("ParsekLog.Info(Tag, FormatStructural(", src)
        self.assertIn("ParsekLog.Warn(Tag, FormatAnomaly(", src)

    def test_milestone_reset_and_credit_lines(self):
        src = _src(la.SRC_MILESTONES)
        self.assertIn('ParsekLog.Verbose("Milestones", $"Reset: cleared {previousCount} '
                      'credited milestones");', src)
        self.assertIn('$"Reset: cleared {previousCount} credited milestones; pre-ledger seed', src)
        self.assertEqual(2, src.count(
            """$"Credited milestone '{milestoneId}' at UT={action.UT.ToString("F1", IC)}\""""))

    def test_visible_walks_reset_every_module(self):
        src = _src("Source/Parsek/GameActions/RecalculationEngine.cs")
        body = _method_body(src, "RunWalkPass")
        self.assertIsNotNone(body)
        self.assertIn("ResetAllModules(firstTier, strategy, secondTier, facilities);", body)

    def test_probe_and_clone_walks_log_nothing(self):
        # The walk split relies on every non-visible walk running under SuppressScope.
        src = _src("Source/Parsek/GameActions/RecalculationEngine.cs")
        allowed = ("RunProjectionWalk", "ProbeModuleAtCutoff", "ProbeClickReservationAtCutoff",
                   "WalkBalancesOnClones")
        for name in allowed:
            body = _method_body(src, name)
            self.assertIsNotNone(body, name)
            self.assertIn("using (ParsekLog.SuppressScope())", body, name)
        heads = [(m.start(), m.group(1)) for m in re.finditer(
            r"(?:internal|private|public)\s+static\s+[\w<>\[\], ]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\(", src)]
        callers = set()
        for m in re.finditer(r"CreateProjectionModules?\(", src):
            owner = [n for pos, n in heads if pos < m.start()][-1]
            if owner not in ("CreateProjectionModules", "CreateProjectionModule"):
                callers.add(owner)
        self.assertEqual(set(allowed), callers)

    def test_science_earning_field_order(self):
        src = _src(la.SRC_SCIENCE)
        for head in ("Earning (cap hit): subject={subjectId}, awarded=",
                     "Earning: subject={subjectId}, awarded="):
            i = src.index(head)
            seg = src[i:i + 700]
            self.assertLess(seg.index("creditedTotal="), seg.index("maxValue="))
            self.assertIn('$"creditedTotal={state.CreditedTotal.ToString("R", IC)}, " +', seg)
            self.assertIn('$"maxValue={state.MaxValue.ToString("R", IC)}, " +', seg)
        self.assertIn('ParsekLog.Verbose("ScienceModule",\n                    $"Earning (cap hit)', src)

    def test_take_back_tag(self):
        self.assertIn('internal const string Tag = "TakeBack";', _src(la.SRC_TAKE_BACK))
        self.assertIn("ParsekLog.Warn(StockShadowTakeBack.Tag,", _src(la.SRC_PATCHER))

    def test_pool_adjustment_shapes(self):
        self.assertIn('$"StockPoolAdjustment: {amount.ToString("R", IC)} id={action.ActionId',
                      _src(la.SRC_FUNDS))
        self.assertIn('$"StockPoolAdjustment: {amount.ToString("R", IC)} (post-curve) id=',
                      _src(la.SRC_REPUTATION))

    def test_deferred_and_patched_lines(self):
        src = _src(la.SRC_ORCHESTRATOR)
        self.assertIn('$"RecalculateAndPatch: deferred KSP state patch ({patchDeferralReason})");',
                      src)
        self.assertIn('internal const string Tag = "LedgerOrchestrator";', src)

    def test_classification_and_onload_lines(self):
        src = _src(la.SRC_LOAD_POLICY)
        self.assertRegex(src, r'"Load classified: early=" \+ early\s*\+ " refined=" \+ refined\s*'
                         r'\+ " scene="')
        self.assertIn('internal const string LogTag = "LoadPolicy";', src)
        scen = _src(la.SRC_SCENARIO)
        self.assertEqual(2, scen.count("LogLoadClassification(earlyLoadKind,"))
        self.assertIn("if (earlyLoadKind != EarlyLoadKind.InSession)\n"
                      "                    LogLoadClassification(", scen)
        self.assertIn("if (earlyLoadKind == EarlyLoadKind.InSession)\n"
                      "                        LogLoadClassification(", scen)
        self.assertIn('ParsekLog.Verbose("Diagnostics",\n                '
                      'FormatLoadTimingMessage(', scen)
        self.assertEqual(1, scen.count("WriteLoadTiming(sw,"))

    def test_dominance_verdict_shape(self):
        self.assertIn('$"Merge dominance check sess={sess} result={(v.Pass ? "pass" : "refuse")} '
                      '{fields} " +', _src(la.SRC_DOMINANCE))

    def test_guid_unknown_and_owned(self):
        self.assertIn('reason={(identity.HasLaunchGuid ? "no-recording-carries-launch-guid" : '
                      '"live-launch-guid-unknown")}', _src(la.SRC_ORCHESTRATOR))
        self.assertIn('$"is already owned at ut={action.UT.ToString("R", IC)} " +',
                      _src(la.SRC_FUNDS))

    def test_finalizer_shapes(self):
        self.assertIn('Fail(cache, "subsurface-destroyed-suppressed")', _src(la.SRC_FINALIZER_CACHE))
        self.assertIn("\"TryFinalizeRecording: recovered sub-surface live-orbit fallback for '{0}' \"",
                      _src(la.SRC_EXTRAPOLATOR))

    def test_ground_truth_shapes(self):
        src = _src(la.SRC_GT_HARNESS)
        self.assertIn('private const string Tag = "LedgerGroundTruth";', src)
        self.assertIn('$"result: hardFailures={hard.ToString(IC)} reportOnly={reportOnly.ToString(IC)} " +',
                      src)
        self.assertIn('$"facetsCompared={report.FacetsCompared.ToString(IC)} " +', src)
        self.assertIn('$"strict={LedgerGroundTruthDiff.StrictPerIdentityForTesting.ToString(IC)}");',
                      src)
        self.assertIn("ParsekLog.Warn(Tag, report.Format());", src)
        rep = _src(la.SRC_GT_REPORT)
        body = _method_body(rep, "IsAlwaysHard")
        for token in ("DivergenceFacet.Funds:", "DivergenceFacet.SciencePool:",
                      "DivergenceFacet.Reputation:",
                      "return d.Kind != DivergenceKind.UpliftClampedExpected;",
                      "d.Kind == DivergenceKind.Consistency", '"guidCorroborated=true"'):
            self.assertIn(token, body)

    def test_walk_decision_field_order(self):
        scen = _src(la.SRC_SCENARIO)
        i = scen.index(la.LIT_INSESSION_DECISION)
        seg = scen[i:i + 1200]
        order = [seg.index(k) for k in ("useCurrentUtCutoff=", "loadedUT=", "isRevert=",
                                        "loadedScene=", "planetariumReady=",
                                        "hasFutureLedgerActions=")]
        self.assertEqual(sorted(order), order)
        self.assertIn('ParsekLog.Info("Scenario",\n                $"OnLoad: post-rewind current-UT '
                      'cutoff decision', scen)
        orch = _src(la.SRC_ORCHESTRATOR)
        self.assertRegex(orch, r'(?s)"OnKspLoad recalc decision: useCurrentUtCutoff=" \+ useCurrentUtCutoff'
                         r'\s*\+\s*" maxUT=".*?\+\s*" featureSupportsCutoff=".*?'
                         r'\+\s*" currentUtReady=" \+ currentUtReady\s*\+\s*" hasFutureActions="')
        self.assertRegex(orch, r'(?s)"Current-timeline recalc: full replay \(cutoffUT=null\) .*?'
                         r'reason=" \+ safeReason \+\s*" currentUT=".*?\+\s*" currentUtReady=" '
                         r'\+ currentUtReady\);')
        self.assertIn('$"RecalculateAndPatch: actionsTotal={actions.Count}, " +\n'
                      '                $"actionsAfterCutoff={actionsAfterCutoff}, cutoffUT={cutoffLabel}";',
                      orch)
        self.assertIn('? utCutoff.Value.ToString("R", CultureInfo.InvariantCulture)\n'
                      '                : "null";', orch)

    def test_the_only_horizon_walk_is_the_refly_start(self):
        # walk-past-clock classifies any cutoff >= 1e308 as RewindInvoker's horizon walk.
        hits = []
        for dirpath, _, names in os.walk(os.path.join(REPO_ROOT, "Source", "Parsek")):
            for name in names:
                if name.endswith(".cs"):
                    path = os.path.join(dirpath, name)
                    rel = os.path.relpath(path, REPO_ROOT).replace(os.sep, "/")
                    if re.search(r"RecalculateAndPatch\(\s*double\.MaxValue", _src(rel)):
                        hits.append(rel)
        self.assertEqual([la.SRC_REWIND_INVOKER], hits)

    def test_patch_move_lines(self):
        src = _src(la.SRC_PATCHER)
        for lit in (la.LIT_PATCH_FUNDS, la.LIT_PATCH_SCIENCE, la.LIT_PATCH_REP):
            self.assertIn("ParsekLog.Info(Tag,\n" + " " * 16 + '$"' + lit, src.replace(
                "ParsekLog.Info(Tag,\n" + " " * 20, "ParsekLog.Info(Tag,\n" + " " * 16))

    def test_guarded_shape_and_its_single_caller(self):
        src = _src(la.SRC_PATCHER)
        body = _method_body(src, "EmitDrawdownGuardClamp")
        self.assertIn('$"Patch{resource}: {label} resource={resource} " +', body)
        self.assertIn('$"running={runningBalance.ToString("R", IC)} live={currentLive.ToString("R", IC)} " +',
                      body)
        self.assertIn('? "GUARDED UPLIFT clamped"\n                : "GUARDED DRAWDOWN clamped";', body)
        # The signed leak (clampedTo - wouldBeTarget) holds because EmitStockPoolLeak is
        # the one caller and passes the target with and without the kept leak.
        self.assertEqual(1, len(re.findall(r"\bEmitDrawdownGuardClamp\(\s*resource,", src)))
        self.assertEqual(2, src.count("EmitDrawdownGuardClamp("))
        leak = _method_body(src, "EmitStockPoolLeak")
        self.assertIn("wouldBeTarget: targetWithoutLeak, clampedTo: writtenTarget,", leak)
        self.assertIn("ClampDirection direction = m.Leak > 0.0 ? ClampDirection.Up : ClampDirection.Down;",
                      leak)

    def test_filed_adjustment_and_reference_load_shapes(self):
        src = _src(la.SRC_PATCHER)
        body = _method_body(src, "FileStockPoolAdjustment")
        self.assertIn('string reason = m.GapAdopted ? "adopted-gap" : m.LeakKept ? "leak" : '
                      '"carried-removed";', body)
        self.assertIn('$"Patch{resource}: filed StockPoolAdjustment amount={m.Adjustment.ToString("R", IC)} " +\n'
                      '                $"reason={reason} ut={nowUT.ToString("R", IC)} id={row.ActionId}"', body)
        self.assertIn("ParsekLog.Info(StockPoolReference.Tag,", body)
        self.assertIn("StockPoolReference.LoadFrom(node, earlyLoadKind.ToString());",
                      _src(la.SRC_SCENARIO))

    def test_zeroed_row_amount_fields(self):
        funds = _src(la.SRC_FUNDS)
        self.assertIn('$"Milestone funds skipped (not effective): " +\n'
                      '                    $"milestoneId={action.MilestoneId ?? "(none)"}, " +\n'
                      '                    $"fundsAwarded={action.MilestoneFundsAwarded.ToString("R", IC)}");',
                      funds)
        self.assertIn('$"fundsReward={action.TransformedFundsReward.ToString("R", IC)}");', funds)
        self.assertIn('$"FundsEarning skipped (not effective): " +\n'
                      '                    $"fundsAwarded={action.FundsAwarded.ToString("R", IC)}, " +', funds)
        sci = _src(la.SRC_SCIENCE)
        self.assertIn('$"scienceReward={action.ScienceReward.ToString("R", IC)}");', sci)
        self.assertIn('$"sciAwarded={action.MilestoneScienceAwarded.ToString("R", IC)}");', sci)

    def test_game_action_types_and_seeds(self):
        src = _src("Source/Parsek/GameActions/GameAction.cs")
        block = src[src.index("enum GameActionType"):]
        block = block[:block.index("}")]
        found = {int(v): n for n, v in re.findall(r"\b([A-Z]\w*)\s*=\s*(\d+)\s*,?", block)}
        self.assertEqual(la.GAME_ACTION_TYPES, found)
        seeds = _method_body(_src("Source/Parsek/GameActions/RecalculationEngine.cs"), "IsSeedType")
        names = set(re.findall(r"GameActionType\.(\w+)", seeds))
        self.assertEqual({la.GAME_ACTION_TYPES[t] for t in la.SEED_ACTION_TYPES}, names)
        self.assertIn('node.AddValue("type", ((int)Type).ToString(IC));', src)


if __name__ == "__main__":
    unittest.main()
