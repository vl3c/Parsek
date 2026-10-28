"""Offline ledger audit of one run's KSP.log: the career economy as Parsek logged it.

Pure library behind ``harness/tools/ledger_audit.py``. Every function takes
strings / lists of lines / dicts and returns data; nothing here opens a file, so
the whole audit is unit-tested with synthetic lines built from the real C#
format strings (``lib/test_ledgeraudit.py``).

It MEASURES only: it never changes a verdict of ``run.py``, a verifier row or a
spec. A run's own gates stay authoritative; this is the reading aid a human
uses after a ledger-touching flight (where did the pools move, was anything paid
twice, did a hard stop fire).

Line shape. ``ParsekLog.Write`` renders ``[Parsek][LEVEL][Subsystem] message``
(LEVEL is INFO / VERBOSE / WARN / ERROR) and hands it to ``Debug.Log``, so in
KSP.log every Parsek line, Warn and Error included, sits behind KSP's own
``[LOG hh:mm:ss.fff] `` stamp. A multi-line message (the ground-truth divergence
report) continues on raw lines with no stamp; those are attached to the record
they follow. A line with no stamp that starts with ``[Parsek][`` (a test-sink
capture, a Player.log) is its own record with no wall time.

Every literal the checks key on is listed in ``SOURCE_ANCHORS`` with the C#
file that writes it; ``test_ledgeraudit.SourceSyncTests`` asserts each one still
occurs in that file with comments stripped, so a rewording reds the suite instead
of turning a check into a silent 0.

Statuses: PASS (looked, found nothing wrong), FAIL, EXPECTED (found only what the
lane's expectations file declared), SKIP (the input the check needs is not in
the log, e.g. ``ledgerTracing`` off), REPORT (informational; never fails unless
an expectation arms it).

Judgement calls the checks make (the report text repeats each one):

* ``refly-leftover`` is REPORT-only. ``outcome=unbound-refly-provisional`` and
  ``retired-empty-provisional`` are by design (a Re-Fly session whose provisional
  never had a recorder bound is reachable from ordinary play, one line per
  InvokeRewind); ``expected = ["refly-leftover"]`` still marks them EXPECTED.
* ``walk-past-clock`` FAILs only when the walk's own decision line says committed
  rows lie ahead of a ready clock (``useCurrentUtCutoff=False`` with
  ``hasFutureLedgerActions=True`` / ``hasFutureActions=True``) and the walk then
  patched a pool upward. A full replay whose decision does not say (the commit walk
  with an untrusted clock, a current-timeline replay with no ready clock) is REPORT:
  the log does not state whether rows lie ahead. A revert walk with no decision of
  its own (the ``ApplyBudgetDeduction`` walk, which runs inside OnLoad and often
  BEFORE the in-session decision line) takes that load's decision when it says rows
  lie ahead of a ready clock (same ``Load classified`` window, within
  ``LOAD_BIND_SECONDS``; RW-2, L13, LAB-10). The Re-Fly start's
  ``RecalculateAndPatch(double.MaxValue)`` (``cutoffUT`` of 1e308 and up) is by
  design and only reported.
* ``guarded-clamp``: an UPLIFT / DRAWDOWN pair on one resource inside
  ``GUARDED_BURST_SECONDS`` whose leaks net to about zero is an ordering transient
  (PASS); the leak is the patch's ``filed StockPoolAdjustment amount=`` (falling back
  to ``clampedTo - wouldBeTarget``, then ``live - running``, on older builds); a
  one-sided GUARDED line is a missing channel and FAILs unless the lane
  lists ``guarded-one-sided`` (or a literal of its line) as expected.
* ``leak-shape`` FAILs a ``reason=leak`` StockPoolAdjustment whose amount equals,
  within ``LEAK_MATCH_TOLERANCE``, the funds / science of the rows the same walk
  zeroed as not effective (all of them, or one), and any ``reason=leak`` row in the
  first two patched walks after a ``Loaded (DiscardReFly)`` reference load (RW-5).

ASCII only; stdlib only.
"""

from __future__ import annotations

import re
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

PASS = "PASS"
FAIL = "FAIL"
EXPECTED = "EXPECTED"
SKIP = "SKIP"
REPORT = "REPORT"

# How many finding lines a check prints before it summarises the rest.
DETAIL_LIMIT = 25
# Pool timeline rows printed before the rest are summarised.
TIMELINE_LIMIT = 300
# A subject's creditedTotal may exceed its maxValue by float noise only.
SUBJECT_CAP_EPSILON = 1e-6
# A wall-clock drop larger than this between two stamped lines is a midnight rollover.
ROLLOVER_SECONDS = 43200.0
RATE_WINDOW_SECONDS = 60.0

# ---------------------------------------------------------------------------
# The C# literals the checks key on: (check id, repo-relative C# file, literal).
# The literal must occur in the comment-stripped file (SourceSyncTests).
# ---------------------------------------------------------------------------

SRC_LEDGER_TRACE = "Source/Parsek/LedgerTrace.cs"
SRC_MILESTONES = "Source/Parsek/GameActions/MilestonesModule.cs"
SRC_SCIENCE = "Source/Parsek/GameActions/ScienceModule.cs"
SRC_FUNDS = "Source/Parsek/GameActions/FundsModule.cs"
SRC_REPUTATION = "Source/Parsek/GameActions/ReputationModule.cs"
SRC_TAKE_BACK = "Source/Parsek/GameActions/StockShadowTakeBack.cs"
SRC_PATCHER = "Source/Parsek/GameActions/KspStatePatcher.cs"
SRC_LEDGER = "Source/Parsek/GameActions/Ledger.cs"
SRC_ORCHESTRATOR = "Source/Parsek/GameActions/LedgerOrchestrator.cs"
SRC_LOAD_POLICY = "Source/Parsek/LoadReconcilePolicy.cs"
SRC_SCENARIO = "Source/Parsek/ParsekScenario.cs"
SRC_TERMINAL_ORBIT = "Source/Parsek/ParsekFlight.TerminalOrbit.cs"
SRC_FLIGHT = "Source/Parsek/ParsekFlight.cs"
SRC_CONCLUSION = "Source/Parsek/ReFlyConclusionRoute.cs"
SRC_DOMINANCE = "Source/Parsek/ReFlyMergeDominance.cs"
SRC_CHAIN_WALKER = "Source/Parsek/GhostChainWalker.cs"
SRC_FINALIZER_CACHE = "Source/Parsek/RecordingFinalizationCacheProducer.cs"
SRC_EXTRAPOLATOR = "Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs"
SRC_GT_HARNESS = "Source/Parsek/InGameTests/LedgerGroundTruthHarness.cs"
SRC_GT_REPORT = "Source/Parsek/LedgerGroundTruth.cs"
SRC_LOG = "Source/Parsek/ParsekLog.cs"
SRC_REWIND_INVOKER = "Source/Parsek/RewindInvoker.cs"
SRC_POOL_REF = "Source/Parsek/GameActions/StockPoolReference.cs"

LIT_TRACE_TAG = "LedgerTrace"
LIT_ANOMALY = "phase=Anomaly"
LIT_STRUCTURAL = "phase=Structural"
LIT_MILESTONE_RESET = "Reset: cleared {previousCount} credited milestones"
LIT_CREDITED = "Credited milestone '"
LIT_EARNING_CAP = "Earning (cap hit): subject="
LIT_EARNING = "Earning: subject="
LIT_TOOK_BACK = "Took back "
LIT_SHORTFALL = "take-back shortfall "
LIT_BREAK_OFF = "Timeline break not recorded for load kind="
LIT_POOL_ADJ = "StockPoolAdjustment: "
LIT_DEBT = "Debt charge: "
LIT_SCIENCE_DEBT = "Spending charged into science debt"
LIT_ABORT = "ABORTING rewind ledger patch"
LIT_DESERIALIZE = "Failed to deserialize action"
LIT_REFUSE_UPGRADE = "refusing to upgrade to amount="
LIT_NOT_AFFORDABLE = "NOT affordable"
LIT_DEFERRED = "RecalculateAndPatch: deferred KSP state patch ("
LIT_PATCHED = "RecalculateAndPatch: drawdown-guard authoritativeReduction="
LIT_CLASSIFIED = "Load classified: early="
LIT_ONLOAD_DONE = "OnLoad: {0}ms ({1} recordings) phase={2} status={3}"
LIT_UNBOUND = "outcome=unbound-refly-provisional"
LIT_RETIRED = "retired-empty-provisional"
LIT_DOMINANCE = "Merge dominance check sess="
LIT_OWNED = "is already owned at ut="
LIT_PID_ONLY = "chain is pid-only"
LIT_GUID_UNKNOWN = "live-launch-guid-unknown"
LIT_SUBSURFACE = "subsurface-destroyed-suppressed"
LIT_SUBSURFACE_DECLINE = "Refresh declined: owner={0} rec={1} pid={2} reason={3}"
LIT_FALLBACK = "recovered sub-surface live-orbit fallback for '"
LIT_GT_RESULT = "result: hardFailures="
LIT_GT_REPORT = "LedgerDivergenceReport: total={0} facetsCompared={1}"
LIT_GT_FACET = "facet={0} kind={1} id={2} expected={3} recon={4} detail={5}"
# walk-past-clock
LIT_INSESSION_DECISION = "OnLoad: post-rewind current-UT cutoff decision useCurrentUtCutoff="
LIT_KSPLOAD_DECISION = "OnKspLoad recalc decision: useCurrentUtCutoff="
LIT_COMMIT_FULL = "Commit ledger walk: full replay (cutoffUT=null), live clock not trusted - reason="
# The C# line continues with a non-ASCII dash before " reason="; key on the ASCII head.
LIT_CURRENT_FULL = "Current-timeline recalc: full replay (cutoffUT=null) "
LIT_INPUT_SUMMARY = "RecalculateAndPatch: actionsTotal="
# The revert follow-up walk (ParsekScenario.ApplyBudgetDeductionWhenReady): the
# singletons line opens it on every build; the fixed build adds its own decision line.
LIT_BUDGET_READY = "ApplyBudgetDeduction: singletons ready after "
LIT_REVERT_BUDGET = "Revert budget walk: scene="
LIT_BUDGET_DONE = "Budget deduction already applied for this revert load"
LIT_PATCH_FUNDS = 'PatchFunds: {currentFunds.ToString("F1", IC)} -> {afterFunds.ToString("F1", IC)} '
LIT_PATCH_SCIENCE = ('PatchScience: {currentScience.ToString("F1", IC)} -> '
                     '{afterScience.ToString("F1", IC)} ')
LIT_PATCH_REP = 'PatchReputation: {currentRep.ToString("F2", IC)} -> {afterRep.ToString("F2", IC)} '
# guarded-clamp
LIT_GUARDED_UPLIFT = "GUARDED UPLIFT clamped"
LIT_GUARDED_DRAWDOWN = "GUARDED DRAWDOWN clamped"
LIT_GUARDED_HEAD = "Patch{resource}: {label} resource={resource} "
LIT_GUARDED_NUMBERS = ('wouldBeTarget={wouldBeTarget.ToString("R", IC)} '
                       'clampedTo={clampedTo.ToString("R", IC)} ')
# leak-shape
LIT_POOL_REF_TAG = 'internal const string Tag = "StockPoolReference";'
LIT_FILED = "Patch{resource}: filed StockPoolAdjustment amount="
LIT_FILED_REASON = 'm.LeakKept ? "leak"'
LIT_REF_LOADED = 'Loaded ({loadKindLabel ?? "unknown"}): pools='
LIT_MS_FUNDS_SKIPPED = "Milestone funds skipped (not effective): "
LIT_CTR_FUNDS_SKIPPED = "funds skipped (not effective): "
LIT_EARN_SKIPPED = "FundsEarning skipped (not effective): "
LIT_SCI_SKIPPED = "science skipped (not effective): "

SOURCE_ANCHORS: Tuple[Tuple[str, str, str], ...] = (
    ("ledger-anomaly", SRC_LEDGER_TRACE, 'Tag = "' + LIT_TRACE_TAG + '"'),
    ("ledger-anomaly", SRC_LEDGER_TRACE, LIT_ANOMALY),
    ("recalc-rate", SRC_LEDGER_TRACE, LIT_STRUCTURAL),
    ("paid-twice", SRC_MILESTONES, LIT_MILESTONE_RESET),
    ("paid-twice", SRC_MILESTONES, LIT_CREDITED),
    ("subject-over-cap", SRC_SCIENCE, LIT_EARNING_CAP),
    ("subject-over-cap", SRC_SCIENCE, LIT_EARNING),
    ("take-back", SRC_TAKE_BACK, LIT_TOOK_BACK),
    ("take-back", SRC_PATCHER, LIT_SHORTFALL),
    ("take-back-off", SRC_TAKE_BACK, LIT_BREAK_OFF),
    ("stock-pool-adjustment", SRC_FUNDS, LIT_POOL_ADJ),
    ("stock-pool-adjustment", SRC_SCIENCE, LIT_POOL_ADJ),
    ("stock-pool-adjustment", SRC_REPUTATION, LIT_POOL_ADJ),
    ("debt", SRC_FUNDS, LIT_DEBT),
    ("debt", SRC_SCIENCE, LIT_DEBT),
    ("debt", SRC_REPUTATION, LIT_DEBT),
    ("debt", SRC_SCIENCE, LIT_SCIENCE_DEBT),
    ("hard-stop", SRC_PATCHER, LIT_ABORT),
    ("hard-stop", SRC_LEDGER, LIT_DESERIALIZE),
    ("hard-stop", SRC_LEDGER, LIT_REFUSE_UPGRADE),
    ("hard-stop", SRC_FUNDS, LIT_NOT_AFFORDABLE),
    ("hard-stop", SRC_SCIENCE, LIT_NOT_AFFORDABLE),
    ("deferred-patch", SRC_ORCHESTRATOR, LIT_DEFERRED),
    ("deferred-patch", SRC_ORCHESTRATOR, LIT_PATCHED),
    ("load-kind", SRC_LOAD_POLICY, LIT_CLASSIFIED),
    ("load-kind", SRC_SCENARIO, LIT_ONLOAD_DONE),
    ("refly-leftover", SRC_SCENARIO, LIT_UNBOUND),
    ("refly-leftover", SRC_FLIGHT, LIT_UNBOUND),
    ("refly-leftover", SRC_TERMINAL_ORBIT, "outcome=" + LIT_RETIRED),
    ("refly-leftover", SRC_CONCLUSION, LIT_RETIRED),
    ("merge-refused", SRC_DOMINANCE, LIT_DOMINANCE),
    ("already-owned", SRC_FUNDS, LIT_OWNED),
    ("same-craft", SRC_CHAIN_WALKER, LIT_PID_ONLY),
    ("same-craft", SRC_ORCHESTRATOR, LIT_GUID_UNKNOWN),
    ("finalizer-oddity", SRC_FINALIZER_CACHE, LIT_SUBSURFACE),
    ("finalizer-oddity", SRC_FINALIZER_CACHE, LIT_SUBSURFACE_DECLINE),
    ("finalizer-oddity", SRC_EXTRAPOLATOR, LIT_FALLBACK),
    ("ground-truth", SRC_GT_HARNESS, LIT_GT_RESULT),
    ("ground-truth", SRC_GT_REPORT, LIT_GT_REPORT),
    ("ground-truth", SRC_GT_REPORT, LIT_GT_FACET),
    ("walk-past-clock", SRC_SCENARIO, LIT_INSESSION_DECISION),
    ("walk-past-clock", SRC_SCENARIO, "planetariumReady={planetariumReady} "),
    ("walk-past-clock", SRC_SCENARIO, "hasFutureLedgerActions={hasFutureLedgerActions}"),
    ("walk-past-clock", SRC_ORCHESTRATOR, LIT_KSPLOAD_DECISION),
    ("walk-past-clock", SRC_ORCHESTRATOR, '" currentUtReady=" + currentUtReady'),
    ("walk-past-clock", SRC_ORCHESTRATOR, '" hasFutureActions=" + hasFutureActions'),
    ("walk-past-clock", SRC_ORCHESTRATOR, LIT_COMMIT_FULL),
    ("walk-past-clock", SRC_ORCHESTRATOR, LIT_CURRENT_FULL),
    ("walk-past-clock", SRC_ORCHESTRATOR, LIT_INPUT_SUMMARY),
    ("walk-past-clock", SRC_SCENARIO, LIT_BUDGET_READY),
    ("walk-past-clock", SRC_SCENARIO, LIT_REVERT_BUDGET),
    ("walk-past-clock", SRC_SCENARIO, LIT_BUDGET_DONE),
    ("walk-past-clock", SRC_SCENARIO, "cutoffScene={cutoffScene} clockReady={clockReady}"),
    ("walk-past-clock", SRC_LOAD_POLICY, LIT_CLASSIFIED),
    ("walk-past-clock", SRC_ORCHESTRATOR, "cutoffUT={cutoffLabel}"),
    ("walk-past-clock", SRC_REWIND_INVOKER, "double.MaxValue, deadlineClockUT: retireCutoffUT"),
    ("walk-past-clock", SRC_PATCHER, LIT_PATCH_FUNDS),
    ("walk-past-clock", SRC_PATCHER, LIT_PATCH_SCIENCE),
    ("walk-past-clock", SRC_PATCHER, LIT_PATCH_REP),
    ("guarded-clamp", SRC_PATCHER, LIT_GUARDED_UPLIFT),
    ("guarded-clamp", SRC_PATCHER, LIT_GUARDED_DRAWDOWN),
    ("guarded-clamp", SRC_PATCHER, LIT_GUARDED_HEAD),
    ("guarded-clamp", SRC_PATCHER, LIT_GUARDED_NUMBERS),
    ("leak-shape", SRC_POOL_REF, LIT_POOL_REF_TAG),
    ("leak-shape", SRC_PATCHER, LIT_FILED),
    ("leak-shape", SRC_PATCHER, LIT_FILED_REASON),
    ("leak-shape", SRC_POOL_REF, LIT_REF_LOADED),
    ("leak-shape", SRC_LOAD_POLICY, "DiscardReFly,"),
    ("leak-shape", SRC_FUNDS, LIT_MS_FUNDS_SKIPPED),
    ("leak-shape", SRC_FUNDS, "{action.Type} " + LIT_CTR_FUNDS_SKIPPED),
    ("leak-shape", SRC_FUNDS, LIT_EARN_SKIPPED),
    ("leak-shape", SRC_SCIENCE, "{action.Type} " + LIT_SCI_SKIPPED),
    ("leak-shape", SRC_SCIENCE, "MilestoneAchievement " + LIT_SCI_SKIPPED),
)

# Check ids in report order, with a one-line title each.
CHECKS: Tuple[Tuple[str, str], ...] = (
    ("ledger-anomaly", "LedgerTrace ledger-vs-truth anomalies"),
    ("recalc-rate", "Patched recalcs (LedgerTrace structural) and their rate"),
    ("paid-twice", "Milestone credited twice within one ledger walk"),
    ("subject-over-cap", "Science subject credited over its cap"),
    ("take-back", "Stock-shadow take-back fired"),
    ("take-back-off", "Timeline break not recorded (take-back switched off)"),
    ("stock-pool-adjustment", "StockPoolAdjustment rows walked"),
    ("debt", "Pool debt charged"),
    ("hard-stop", "Ledger hard stops"),
    ("deferred-patch", "Deferred KSP state patches"),
    ("load-kind", "Load classification per OnLoad"),
    ("refly-leftover", "Re-Fly provisional leftovers"),
    ("merge-refused", "Re-Fly merge dominance verdicts"),
    ("already-owned", "Purchases already owned (not charged)"),
    ("same-craft", "Same-craft identity fallbacks (pid-only / guid unknown)"),
    ("finalizer-oddity", "Scene-exit finalizer oddities"),
    ("ground-truth", "LedgerGroundTruth in-game harness result"),
    ("walk-past-clock", "No-cutoff walk with committed rows ahead of the clock"),
    ("guarded-clamp", "GUARDED clamps: paired transients vs one-sided"),
    ("leak-shape", "Leak rows that repeat a zeroed duplicate or follow a Discard Re-fly"),
    ("warnings", "Parsek WARN / ERROR surface"),
    ("ledger-ut0", "Saved ledger rows at UT 0 that are not seeds"),
)
CHECK_IDS = tuple(c for c, _ in CHECKS)
CHECK_TITLES = dict(CHECKS)

# Names an expectations file may list under ``expected``. Only these findings can
# read EXPECTED; every other FAIL (an anomaly, a double credit, an abort) cannot be
# masked by a lane's expectations.
EXPECTABLE_NAMES = ("take-back", "debt", "not-affordable", "refly-leftover", "guarded-one-sided")
# The check each expectable finding belongs to.
EXPECTABLE_CHECK = {"take-back": "take-back", "debt": "debt", "not-affordable": "hard-stop",
                    "refly-leftover": "refly-leftover", "guarded-one-sided": "guarded-clamp"}

# guarded-clamp: an opposite pair this close in wall time is one burst ...
GUARDED_BURST_SECONDS = 10.0
# ... and nets to about zero when the leaks cancel within the larger of these.
GUARDED_NET_ABS = 0.01
GUARDED_NET_REL = 1e-3
# leak-shape: a leak "equals" a zeroed amount within this.
LEAK_MATCH_TOLERANCE = 0.01
# walk-past-clock: a cutoff at or above this is the Re-Fly start's double.MaxValue horizon.
HORIZON_CUTOFF = 1e308
# walk-past-clock: a revert walk with no decision of its own binds to the same load's
# in-session decision line when the two are this close in wall time.
LOAD_BIND_SECONDS = 10.0
# A pool patch "rises" when it moves up by more than this.
RISE_EPSILON = 0.01

# GameActionType values (Source/Parsek/GameActions/GameAction.cs; pinned by
# SourceSyncTests) and the seed types RecalculationEngine.IsSeedType names.
GAME_ACTION_TYPES: Dict[int, str] = {
    0: "ScienceEarning", 1: "ScienceSpending", 2: "FundsEarning", 3: "FundsSpending",
    4: "MilestoneAchievement", 5: "ContractAccept", 6: "ContractComplete",
    7: "ContractFail", 8: "ContractCancel", 9: "ReputationEarning",
    10: "ReputationPenalty", 11: "KerbalAssignment", 12: "KerbalHire",
    13: "KerbalRescue", 14: "KerbalStandIn", 15: "FacilityUpgrade",
    16: "FacilityDestruction", 17: "FacilityRepair", 18: "StrategyActivate",
    19: "StrategyDeactivate", 20: "FundsInitial", 21: "ScienceInitial",
    22: "ReputationInitial", 23: "RouteDispatched", 24: "RouteCargoDebited",
    25: "RouteCargoDelivered", 26: "RoutePaused", 27: "RouteEndpointLost",
    28: "RouteRecoveryCredited", 29: "RouteCargoPickedUp", 30: "RouteResumed",
    31: "KerbalExperience", 32: "StrategyScienceDebit", 33: "StrategyScienceCredit",
    34: "KerbalRecovered", 35: "VesselRecovered", 36: "RouteHeld",
    37: "ResourceScanUnlocked", 38: "ScienceLabEarning", 39: "StockPoolAdjustment",
    40: "ContractParameterComplete", 41: "KerbalDismissal",
}
SEED_ACTION_TYPES = frozenset((20, 21, 22))

# ---------------------------------------------------------------------------
# Line parsing
# ---------------------------------------------------------------------------

_KSP_PREFIX_RE = re.compile(
    r"^\[(LOG|WRN|ERR|EXC|AST)\s+(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,6}))?\]:?[ \t]?")
_PARSEK_RE = re.compile(r"^\[Parsek\]\[(INFO|VERBOSE|WARN|ERROR)\]\[([^\]]*)\] ?(.*)$")
_SCENE_RE = re.compile(r"Scene Change : From (\S+) to (\S+)")


class Rec(object):
    """One log record: a stamped (or bare Parsek) line plus its raw continuation lines."""

    __slots__ = ("n", "t", "level", "sub", "msg", "raw", "cont", "scene")

    def __init__(self, n, t, level, sub, msg, raw, scene=None):
        self.n = n          # 1-based line number in the log
        self.t = t          # unwrapped wall seconds, or None
        self.level = level  # Parsek level, or None for a non-Parsek line
        self.sub = sub      # Parsek subsystem, or None
        self.msg = msg      # Parsek message, or the line after the KSP stamp
        self.raw = raw
        self.cont = []      # continuation lines (multi-line messages)
        self.scene = scene  # (from, to) for a KSP scene-change line

    @property
    def is_parsek(self):
        return self.level is not None


def _stamp_seconds(m) -> float:
    frac = m.group(5) or "0"
    return int(m.group(2)) * 3600 + int(m.group(3)) * 60 + int(m.group(4)) + float("0." + frac)


def parse_log(lines: Iterable[str]) -> List[Rec]:
    """Records of a KSP.log (or any list of Parsek lines). Wall times are unwrapped
    across a midnight rollover (a drop of more than 12 h)."""
    recs: List[Rec] = []
    day = 0.0
    prev_t: Optional[float] = None
    for i, raw in enumerate(lines):
        line = raw.rstrip("\r\n")
        m = _KSP_PREFIX_RE.match(line)
        t = None
        body = line
        if m:
            t = _stamp_seconds(m) + day
            if prev_t is not None and t < prev_t - ROLLOVER_SECONDS:
                day += 86400.0
                t += 86400.0
            prev_t = t
            body = line[m.end():]
        elif not line.startswith("[Parsek]["):
            if recs and line.strip():
                recs[-1].cont.append(line)
            continue
        pm = _PARSEK_RE.match(body)
        if pm:
            recs.append(Rec(i + 1, t, pm.group(1), pm.group(2), pm.group(3), line))
            continue
        sm = _SCENE_RE.search(body) if "Scene Change" in body else None
        recs.append(Rec(i + 1, t, None, None, body, line,
                        scene=(sm.group(1), sm.group(2)) if sm else None))
    return recs


def fmt_wall(t: Optional[float]) -> str:
    if t is None:
        return "--:--:--"
    s = int(t) % 86400
    return "%02d:%02d:%02d" % (s // 3600, (s // 60) % 60, s % 60)


def _num(text: Optional[str]) -> Optional[float]:
    if text is None:
        return None
    try:
        return float(text)
    except ValueError:
        return None


# ---------------------------------------------------------------------------
# Expectations
# ---------------------------------------------------------------------------

class Expectations(object):
    """A lane's declared ledger behaviour.

    ``expected``: names from EXPECTABLE_NAMES whose findings read EXPECTED.
    ``expected_lines``: literals; an EXPECTABLE finding whose line contains one
    reads EXPECTED (a literal never masks a non-expectable finding).
    ``max_structural_per_minute``: arms recalc-rate (FAIL when the peak 60 s window
    holds more patched recalcs). ``merge_must_pass``: arms merge-refused (FAIL on
    any ``result=refuse``)."""

    def __init__(self, expected=(), expected_lines=(), max_structural_per_minute=None,
                 merge_must_pass=False, source=None):
        self.expected = tuple(expected)
        self.expected_lines = tuple(expected_lines)
        self.max_structural_per_minute = max_structural_per_minute
        self.merge_must_pass = bool(merge_must_pass)
        self.source = source


EXPECTATION_KEYS = ("expected", "expectedLines", "maxStructuralPerMinute", "mergeMustPass")


def parse_expectations(obj: Optional[dict], source: Optional[str] = None) -> Expectations:
    """Validate a loaded TOML / JSON table. Raises ValueError on an unknown key, an
    unknown or non-expectable name, or a wrongly typed value, so a typo never turns
    into an expectation that silently does nothing."""
    if obj is None:
        return Expectations(source=source)
    if not isinstance(obj, dict):
        raise ValueError("expectations must be a table / object")
    unknown = sorted(k for k in obj if k not in EXPECTATION_KEYS)
    if unknown:
        raise ValueError("unknown expectations key(s): %s (known: %s)"
                         % (", ".join(unknown), ", ".join(EXPECTATION_KEYS)))
    expected = obj.get("expected", [])
    if not isinstance(expected, list) or not all(isinstance(x, str) for x in expected):
        raise ValueError("'expected' must be a list of strings")
    bad = [x for x in expected if x not in EXPECTABLE_NAMES]
    if bad:
        raise ValueError("'expected' names a check that cannot read EXPECTED: %s "
                         "(expectable: %s)" % (", ".join(bad), ", ".join(EXPECTABLE_NAMES)))
    lines = obj.get("expectedLines", [])
    if not isinstance(lines, list) or not all(isinstance(x, str) and x for x in lines):
        raise ValueError("'expectedLines' must be a list of non-empty strings")
    rate = obj.get("maxStructuralPerMinute")
    if rate is not None and (isinstance(rate, bool) or not isinstance(rate, (int, float))
                             or rate <= 0):
        raise ValueError("'maxStructuralPerMinute' must be a positive number")
    must = obj.get("mergeMustPass", False)
    if not isinstance(must, bool):
        raise ValueError("'mergeMustPass' must be true or false")
    return Expectations(expected, lines, float(rate) if rate is not None else None, must, source)


# ---------------------------------------------------------------------------
# Findings and check results
# ---------------------------------------------------------------------------

def _finding(rec: Rec, key: str, expectable: bool, note: str = "") -> Dict:
    return {"n": rec.n, "t": rec.t, "sub": rec.sub, "msg": rec.msg, "key": key,
            "expectable": expectable, "expected": False, "note": note}


def _apply_expectations(findings: List[Dict], exp: Expectations, used: Dict[str, int]) -> None:
    for f in findings:
        if not f["expectable"]:
            continue
        if f["key"] in exp.expected:
            f["expected"] = True
            used[f["key"]] = used.get(f["key"], 0) + 1
            continue
        for lit in exp.expected_lines:
            if lit in f["msg"]:
                f["expected"] = True
                used[lit] = used.get(lit, 0) + 1
                break


def _status_of(findings: List[Dict]) -> str:
    if not findings:
        return PASS
    if any(not f["expected"] for f in findings):
        return FAIL
    return EXPECTED


def _result(check: str, status: str, summary: str, details: Optional[List[str]] = None,
            findings: Optional[List[Dict]] = None, **extra) -> Dict:
    out = {"check": check, "title": CHECK_TITLES[check], "status": status,
           "summary": summary, "details": list(details or []),
           "findings": list(findings or [])}
    out.update(extra)
    return out


def _finding_lines(findings: List[Dict], limit: int = DETAIL_LIMIT) -> List[str]:
    out = []
    for f in findings[:limit]:
        tag = "EXPECTED" if f["expected"] else ("expectable" if f["expectable"] else "hard")
        note = (" (" + f["note"] + ")") if f["note"] else ""
        out.append("L%d %s [%s] %s [%s]%s" % (f["n"], fmt_wall(f["t"]), f["sub"] or "-",
                                             f["msg"], tag, note))
    if len(findings) > limit:
        out.append("... %d more" % (len(findings) - limit))
    return out


def _parsek(recs: Sequence[Rec], sub: Optional[str] = None) -> List[Rec]:
    return [r for r in recs if r.is_parsek and (sub is None or r.sub == sub)]


# ---------------------------------------------------------------------------
# 1 + 2: LedgerTrace
# ---------------------------------------------------------------------------

_STRUCTURAL_RE = re.compile(
    r"^phase=Structural recalcSeq=(\d+) funds=(\S+) science=(\S+) rep=(\S+) "
    r"facilities=\[([^\]]*)\] targetTechNodes=(\d+) contracts=(\d+) cutoff=(\S+) "
    r"authReduction=(true|false)")
_ANOMALY_RE = re.compile(r"^phase=Anomaly recalcSeq=(\d+) resource=(\S+) id=(\S+) reason=(\S+)")


def check_ledger_anomaly(recs: Sequence[Rec], exp: Expectations) -> Dict:
    trace = _parsek(recs, LIT_TRACE_TAG)
    if not trace:
        return _result("ledger-anomaly", SKIP, "ledgerTracing off (no [LedgerTrace] line)")
    findings = []
    for r in trace:
        if r.msg.startswith(LIT_ANOMALY):
            m = _ANOMALY_RE.match(r.msg)
            note = ("resource=%s id=%s reason=%s" % (m.group(2), m.group(3), m.group(4))) if m else ""
            findings.append(_finding(r, "ledger-anomaly", False, note))
    st = _status_of(findings)
    return _result("ledger-anomaly", st, "%d anomaly line(s) in %d [LedgerTrace] line(s)"
                   % (len(findings), len(trace)), _finding_lines(findings), findings)


def parse_structural(recs: Sequence[Rec]) -> List[Dict]:
    rows = []
    for r in _parsek(recs, LIT_TRACE_TAG):
        m = _STRUCTURAL_RE.match(r.msg)
        if not m:
            continue
        rows.append({"n": r.n, "t": r.t, "seq": int(m.group(1)),
                     "funds": _num(m.group(2)), "science": _num(m.group(3)),
                     "rep": _num(m.group(4)), "facilities": m.group(5),
                     "techNodes": int(m.group(6)), "contracts": int(m.group(7)),
                     "cutoff": m.group(8), "authReduction": m.group(9) == "true"})
    return rows


def peak_window_count(times: Sequence[float], window: float = RATE_WINDOW_SECONDS) -> int:
    """Most events inside any closed window of ``window`` seconds."""
    ts = sorted(times)
    best = 0
    lo = 0
    for hi in range(len(ts)):
        while ts[hi] - ts[lo] > window:
            lo += 1
        best = max(best, hi - lo + 1)
    return best


def check_recalc_rate(recs: Sequence[Rec], exp: Expectations) -> Dict:
    if not _parsek(recs, LIT_TRACE_TAG):
        return _result("recalc-rate", SKIP, "ledgerTracing off (no [LedgerTrace] line)", rows=[])
    rows = parse_structural(recs)
    times = [r["t"] for r in rows if r["t"] is not None]
    details = []
    resets = sum(1 for a, b in zip(rows, rows[1:]) if b["seq"] <= a["seq"])
    gaps = sum(1 for a, b in zip(rows, rows[1:]) if b["seq"] > a["seq"] + 1)
    peak = peak_window_count(times) if times else 0
    span = (max(times) - min(times)) if len(times) > 1 else 0.0
    mean = (len(times) / (span / 60.0)) if span >= RATE_WINDOW_SECONDS else float(len(times))
    summary = ("%d patched recalc(s); mean %.1f/min over %s; peak %d in any 60 s"
               % (len(rows), mean, _fmt_span(span), peak))
    details.append("recalcSeq restarts: %d, gaps (a burst with no structural line): %d"
                   % (resets, gaps))
    status = REPORT
    if exp.max_structural_per_minute is not None:
        details.append("expectation: at most %g per 60 s" % exp.max_structural_per_minute)
        status = FAIL if peak > exp.max_structural_per_minute else PASS
    if not rows:
        details.append("tracing on but no phase=Structural line: no recalc patched KSP state")
    return _result("recalc-rate", status, summary, details, rows=rows,
                   peakPerMinute=peak, meanPerMinute=mean)


def _fmt_span(seconds: float) -> str:
    s = int(round(seconds))
    return "%dm%02ds" % (s // 60, s % 60)


def pool_timeline(rows: Sequence[Dict], limit: int = TIMELINE_LIMIT) -> List[str]:
    """One line per structural row whose pools / contracts / facilities moved; a
    run of unchanged rows collapses into the count on the next printed row."""
    out = ["%-8s %6s %14s %10s %8s %4s %4s %-10s %-5s %s" % (
        "wall", "seq", "funds", "science", "rep", "ctr", "tech", "cutoff", "auth", "facilities")]
    prev_key = None
    same = 0
    printed = 0
    for r in rows:
        key = (r["funds"], r["science"], r["rep"], r["contracts"], r["facilities"])
        if key == prev_key:
            same += 1
            continue
        if printed >= limit:
            same += 1
            continue
        if same:
            out.append("         (%d unchanged recalc(s))" % same)
            same = 0
        out.append("%-8s %6d %14s %10s %8s %4d %4d %-10s %-5s [%s]" % (
            fmt_wall(r["t"]), r["seq"], _fmt_pool(r["funds"]), _fmt_pool(r["science"]),
            _fmt_pool(r["rep"]), r["contracts"], r["techNodes"], r["cutoff"][:10],
            "true" if r["authReduction"] else "false", r["facilities"]))
        prev_key = key
        printed += 1
    if same:
        out.append("         (%d unchanged or unprinted recalc(s))" % same)
    return out


def _fmt_pool(v: Optional[float]) -> str:
    return "?" if v is None else "%.2f" % v


# ---------------------------------------------------------------------------
# 3: credited twice within one walk
# ---------------------------------------------------------------------------

_CREDITED_RE = re.compile(r"^Credited milestone '([^']*)' at UT=(\S+)")
_MILESTONE_RESET_PREFIX = "Reset: cleared "
_MILESTONE_RESET_SUFFIX = " credited milestones"


def _is_milestone_reset(r: Rec) -> bool:
    if r.sub != "Milestones" or not r.msg.startswith(_MILESTONE_RESET_PREFIX):
        return False
    head = r.msg[len(_MILESTONE_RESET_PREFIX):]
    count, _, rest = head.partition(" ")
    return count.isdigit() and (" " + rest).startswith(_MILESTONE_RESET_SUFFIX)


def split_milestone_walks(recs: Sequence[Rec]) -> List[Dict]:
    """Milestone walks in log order. A walk opens at a MilestonesModule
    ``Reset: cleared N credited milestones`` line (every visible walk pass resets
    every registered module first, the conversion-leg re-walk included). Probe and
    projection walks run over module CLONES inside ``ParsekLog.SuppressScope`` and
    log nothing, so every credit line in the log belongs to a visible walk
    (SourceSyncTests pin the suppression). Credits before the first reset form
    walk 0 (a log that starts mid-walk)."""
    walks: List[Dict] = []
    cur = {"index": 0, "startLine": None, "t": None, "credits": []}
    resets = 0
    for r in recs:
        if not r.is_parsek or r.sub != "Milestones":
            continue
        if _is_milestone_reset(r):
            if cur["startLine"] is not None or cur["credits"]:
                walks.append(cur)
            resets += 1
            cur = {"index": resets, "startLine": r.n, "t": r.t, "credits": []}
            continue
        m = _CREDITED_RE.match(r.msg)
        if m:
            cur["credits"].append((m.group(1), r.n, r.t))
    if cur["startLine"] is not None or cur["credits"]:
        walks.append(cur)
    return walks


def credited_twice(walks: Sequence[Dict]) -> List[Dict]:
    rows = []
    for w in walks:
        by_id: Dict[str, List[int]] = {}
        for ident, n, _ in w["credits"]:
            by_id.setdefault(ident, []).append(n)
        for ident, ns in by_id.items():
            if len(ns) > 1:
                rows.append({"walk": w["index"], "walkLine": w["startLine"], "id": ident,
                             "count": len(ns), "lines": ns})
    rows.sort(key=lambda x: (x["lines"][0], x["id"]))
    return rows


def check_paid_twice(recs: Sequence[Rec], exp: Expectations) -> Dict:
    walks = split_milestone_walks(recs)
    if not walks:
        return _result("paid-twice", SKIP,
                       "no milestone walk logged (verbose logging off, or no recalc ran)", dups=[])
    dups = credited_twice(walks)
    credits = sum(len(w["credits"]) for w in walks)
    by_line = {r.n: r for r in recs}
    findings = []
    for d in dups:
        r = by_line[d["lines"][1]]
        findings.append(_finding(r, "paid-twice", False,
                                 "id '%s' credited %d times in walk %d (lines %s)"
                                 % (d["id"], d["count"], d["walk"],
                                    ",".join(str(x) for x in d["lines"]))))
    status = FAIL if dups else PASS
    return _result("paid-twice", status, "%d walk(s), %d credit line(s), %d id(s) credited twice"
                   % (len(walks), credits, len(dups)), _finding_lines(findings), findings, dups=dups)


def credited_twice_table(dups: Sequence[Dict]) -> List[str]:
    if not dups:
        return ["(none)"]
    out = ["%-6s %-9s %-40s %5s  %s" % ("walk", "resetL", "identity", "count", "credit lines")]
    for d in dups:
        out.append("%-6d %-9s %-40s %5d  %s" % (
            d["walk"], ("L%d" % d["walkLine"]) if d["walkLine"] else "-", d["id"], d["count"],
            ",".join("L%d" % x for x in d["lines"])))
    return out


# ---------------------------------------------------------------------------
# 4: subject over cap
# ---------------------------------------------------------------------------

_EARNING_RE = re.compile(
    r"^Earning(?: \(cap hit\))?: subject=(.*?), awarded=.*?creditedTotal=([^,]+), maxValue=([^,]+),")


def check_subject_over_cap(recs: Sequence[Rec], exp: Expectations) -> Dict:
    lines = [r for r in _parsek(recs, "ScienceModule")
             if r.msg.startswith(LIT_EARNING) or r.msg.startswith(LIT_EARNING_CAP)]
    if not lines:
        return _result("subject-over-cap", SKIP, "no science earning line (no science walked, "
                       "or verbose logging off)")
    findings = []
    unparsed = 0
    for r in lines:
        m = _EARNING_RE.match(r.msg)
        credited = _num(m.group(2)) if m else None
        cap = _num(m.group(3)) if m else None
        if credited is None or cap is None:
            unparsed += 1
            continue
        if credited > cap + SUBJECT_CAP_EPSILON:
            findings.append(_finding(r, "subject-over-cap", False,
                                     "subject=%s creditedTotal=%r > maxValue=%r"
                                     % (m.group(1), credited, cap)))
    details = _finding_lines(findings)
    if unparsed:
        details.append("%d earning line(s) did not parse (format drift?)" % unparsed)
    status = FAIL if findings or unparsed else PASS
    return _result("subject-over-cap", status, "%d earning line(s), %d over the cap"
                   % (len(lines), len(findings)), details, findings)


# ---------------------------------------------------------------------------
# 5, 6, 8, 9, 12: literal scans
# ---------------------------------------------------------------------------

def _scan(recs: Sequence[Rec], rules: Sequence[Tuple[str, Optional[str], str, bool]]) -> List[Dict]:
    """rules: (literal, subsystem or None, finding key, expectable)."""
    out = []
    for r in recs:
        if not r.is_parsek:
            continue
        for lit, sub, key, expectable in rules:
            if (sub is None or r.sub == sub) and lit in r.msg:
                out.append(_finding(r, key, expectable))
                break
    return out


def _scan_check(check: str, recs: Sequence[Rec], exp: Expectations, used: Dict[str, int],
                rules, what: str) -> Dict:
    findings = _scan(recs, rules)
    _apply_expectations(findings, exp, used)
    st = _status_of(findings)
    n_exp = sum(1 for f in findings if f["expected"])
    summary = "%d %s line(s)" % (len(findings), what)
    if n_exp:
        summary += ", %d expected" % n_exp
    return _result(check, st, summary, _finding_lines(findings), findings)


def check_take_back(recs, exp, used):
    return _scan_check("take-back", recs, exp, used, (
        (LIT_TOOK_BACK, "TakeBack", "take-back", True),
        (LIT_SHORTFALL, "TakeBack", "take-back", True),
    ), "take-back")


def check_take_back_off(recs, exp, used):
    return _scan_check("take-back-off", recs, exp, used, (
        (LIT_BREAK_OFF, "TakeBack", "take-back-off", False),
    ), "timeline-break-not-recorded")


def check_debt(recs, exp, used):
    return _scan_check("debt", recs, exp, used, (
        (LIT_DEBT, None, "debt", True),
        (LIT_SCIENCE_DEBT, "ScienceModule", "debt", True),
    ), "debt")


def check_hard_stop(recs, exp, used):
    return _scan_check("hard-stop", recs, exp, used, (
        (LIT_ABORT, None, "hard-stop", False),
        (LIT_DESERIALIZE, "Ledger", "hard-stop", False),
        (LIT_REFUSE_UPGRADE, "Ledger", "hard-stop", False),
        (LIT_NOT_AFFORDABLE, None, "not-affordable", True),
    ), "hard-stop")


def check_refly_leftover(recs, exp, used):
    """REPORT-only. Both outcomes are by design: a Re-Fly session whose provisional
    never had a recorder bound is reachable from ordinary play (rewind, F5/F9,
    conclude without flying; see ``ReFlyProvisionalBinding``), so a green lane logs
    one per InvokeRewind. An expectation still marks them EXPECTED (and the header
    lists it when the lane showed none)."""
    findings = _scan(recs, (
        (LIT_UNBOUND, None, "refly-leftover", True),
        (LIT_RETIRED, None, "refly-leftover", True),
    ))
    _apply_expectations(findings, exp, used)
    n_exp = sum(1 for f in findings if f["expected"])
    if not findings:
        status = PASS
    elif n_exp == len(findings):
        status = EXPECTED
    else:
        status = REPORT
    summary = "%d Re-Fly leftover line(s)" % len(findings)
    if n_exp:
        summary += ", %d expected" % n_exp
    if findings:
        summary += " (report-only: by design, see ReFlyProvisionalBinding)"
    return _result("refly-leftover", status, summary, _finding_lines(findings), findings)


# ---------------------------------------------------------------------------
# 7, 14, 15, 16: reports
# ---------------------------------------------------------------------------

_POOL_ADJ_RE = re.compile(r"^StockPoolAdjustment: (\S+)(?: \(post-curve\))? id=(\S+) ut=(\S+)")


def check_stock_pool_adjustment(recs: Sequence[Rec], exp: Expectations) -> Dict:
    """Every walk re-walks every row, so one row prints once per recalc; the
    report lists each (pool, row) once with its walk count."""
    seen: Dict[Tuple[str, str, str, str], List[int]] = {}
    for r in _parsek(recs):
        if not r.msg.startswith(LIT_POOL_ADJ):
            continue
        m = _POOL_ADJ_RE.match(r.msg)
        key = (r.sub, m.group(2) if m else "?", m.group(1) if m else "?", m.group(3) if m else "?")
        seen.setdefault(key, []).append(r.n)
    details = ["[%s] id=%s amount=%s ut=%s walked %dx (first L%d)"
               % (k[0], k[1], k[2], k[3], len(v), v[0]) for k, v in seen.items()]
    return _result("stock-pool-adjustment", REPORT, "%d distinct row(s), %d line(s)"
                   % (len(seen), sum(len(v) for v in seen.values())), details[:DETAIL_LIMIT * 2])


def _report_literals(check: str, recs: Sequence[Rec], literals: Sequence[str],
                     extract=None) -> Dict:
    hits: Dict[str, List[Rec]] = {}
    for r in _parsek(recs):
        for lit in literals:
            if lit in r.msg:
                hits.setdefault(lit, []).append(r)
                break
    details = []
    for lit in literals:
        rs = hits.get(lit, [])
        if not rs:
            continue
        extra = ""
        if extract:
            ids = []
            for r in rs:
                for x in extract(r):
                    if x not in ids:
                        ids.append(x)
            extra = " ids: " + ", ".join(ids[:DETAIL_LIMIT]) + (" ..." if len(ids) > DETAIL_LIMIT else "")
        details.append("'%s': %d line(s)%s" % (lit, len(rs), extra))
        for r in rs[:5]:
            details.append("  L%d %s [%s] %s" % (r.n, fmt_wall(r.t), r.sub, r.msg))
        if len(rs) > 5:
            details.append("  ... %d more" % (len(rs) - 5))
    total = sum(len(v) for v in hits.values())
    return _result(check, REPORT, "%d line(s)" % total, details)


def check_already_owned(recs, exp):
    return _report_literals("already-owned", recs, (LIT_OWNED,))


def check_same_craft(recs, exp):
    return _report_literals("same-craft", recs, (LIT_PID_ONLY, LIT_GUID_UNKNOWN))


_REC_TOKEN_RE = re.compile(r"\brec=(\S+)")
_FALLBACK_ID_RE = re.compile(r"live-orbit fallback for '([^']*)'")


def _finalizer_ids(r: Rec) -> List[str]:
    m = _FALLBACK_ID_RE.search(r.msg) or _REC_TOKEN_RE.search(r.msg)
    return [m.group(1)] if m else []


def check_finalizer_oddity(recs, exp):
    return _report_literals("finalizer-oddity", recs, (LIT_SUBSURFACE, LIT_FALLBACK),
                            extract=_finalizer_ids)


# ---------------------------------------------------------------------------
# 10: deferred patches
# ---------------------------------------------------------------------------

_DEFERRED_RE = re.compile(r"^RecalculateAndPatch: deferred KSP state patch \((.*)\)$")


def check_deferred_patch(recs: Sequence[Rec], exp: Expectations) -> Dict:
    """REPORT-only. A deferred recalc patches nothing; the next recalc that is not
    deferred (``drawdown-guard authoritativeReduction=``, logged on every patching
    recalc) is what lands the ledger in KSP. A deferral normally stays open across
    a scene change (a live tree defers in FLIGHT, and its commit's recalc after the
    scene change patches), so an open deferral at a scene change is reported, not
    failed."""
    deferred = 0
    patched = 0
    reasons: Dict[str, int] = {}
    open_since: Optional[Rec] = None
    open_reason = None
    open_at_scene: List[str] = []
    for r in recs:
        if r.scene is not None:
            if open_since is not None:
                open_at_scene.append("L%d %s %s->%s with the deferral of L%d open (%s)"
                                     % (r.n, fmt_wall(r.t), r.scene[0], r.scene[1],
                                        open_since.n, open_reason))
            continue
        if not r.is_parsek or r.sub != "LedgerOrchestrator":
            continue
        m = _DEFERRED_RE.match(r.msg)
        if m:
            deferred += 1
            reason = re.sub(r"'[^']*'", "'<tree>'", m.group(1))
            reasons[reason] = reasons.get(reason, 0) + 1
            if open_since is None:
                open_since, open_reason = r, m.group(1)
            continue
        if r.msg.startswith(LIT_PATCHED):
            patched += 1
            open_since = None
    if not deferred and not patched:
        return _result("deferred-patch", SKIP, "no RecalculateAndPatch line (verbose logging off?)")
    details = ["reason '%s': %d" % (k, v) for k, v in sorted(reasons.items(), key=lambda x: -x[1])]
    details.append("scene changes with a deferral open: %d" % len(open_at_scene))
    details.extend("  " + x for x in open_at_scene[:DETAIL_LIMIT])
    details.append("open at log end: %s" % (("yes, since L%d (%s)" % (open_since.n, open_reason))
                                           if open_since is not None else "no"))
    return _result("deferred-patch", REPORT, "%d deferred, %d patched recalc(s), %d scene "
                   "change(s) with one open" % (deferred, patched, len(open_at_scene)), details)


# ---------------------------------------------------------------------------
# 11: load classification
# ---------------------------------------------------------------------------

_CLASSIFIED_RE = re.compile(r"^Load classified: early=(\S+) refined=(\S+) scene=(\S+)")
_ONLOAD_DONE_RE = re.compile(r"^OnLoad: \d+ms \(\d+ recordings\) phase=\S+ status=(\S+)")


def check_load_kind(recs: Sequence[Rec], exp: Expectations) -> Dict:
    """One load = one ParsekScenario.OnLoad call. Its finally block logs
    ``[Diagnostics] OnLoad: Nms (M recordings) phase=P status=S`` exactly once,
    on every path, so a load is the stretch up to and including that line. The
    classification is logged at most once per load (an early kind before, or a
    refined InSession kind after, the revert detector), so two classify lines in
    one stretch FAIL. With no OnLoad line (verbose logging off) loads cannot be
    delimited and the check only reports the sequence."""
    seq: List[str] = []
    segments: List[List[Rec]] = []
    cur: List[Rec] = []
    terminators = 0
    for r in _parsek(recs):
        if r.sub == "LoadPolicy" and r.msg.startswith(LIT_CLASSIFIED):
            m = _CLASSIFIED_RE.match(r.msg)
            seq.append("%s(%s)@%s" % (m.group(2), m.group(1), m.group(3)) if m else "?")
            cur.append(r)
        elif r.sub == "Diagnostics" and _ONLOAD_DONE_RE.match(r.msg):
            terminators += 1
            segments.append(cur)
            cur = []
    if not seq:
        return _result("load-kind", SKIP, "no [LoadPolicy] Load classified line")
    details = ["sequence: " + " -> ".join(seq)]
    if terminators == 0:
        details.append("no [Diagnostics] OnLoad timing line: loads cannot be delimited")
        return _result("load-kind", REPORT, "%d classification(s), loads not delimited" % len(seq),
                       details)
    if cur:
        details.append("%d classification(s) after the last OnLoad timing line (load cut off?)"
                       % len(cur))
    findings = []
    for seg in segments:
        if len(seg) > 1:
            for r in seg[1:]:
                findings.append(_finding(r, "load-kind", False,
                                         "second classification in the load that began at L%d"
                                         % seg[0].n))
    details.extend(_finding_lines(findings))
    return _result("load-kind", FAIL if findings else PASS,
                   "%d classification(s) over %d OnLoad(s)" % (len(seq), terminators),
                   details, findings)


# ---------------------------------------------------------------------------
# 13: merge dominance
# ---------------------------------------------------------------------------

_DOMINANCE_RE = re.compile(r"^Merge dominance check sess=(\S+) result=(pass|refuse) (.*)$")


def check_merge_refused(recs: Sequence[Rec], exp: Expectations) -> Dict:
    verdicts = []
    for r in _parsek(recs):
        m = _DOMINANCE_RE.match(r.msg)
        if m:
            verdicts.append((r, m.group(1), m.group(2), m.group(3)))
    refused = [v for v in verdicts if v[2] == "refuse"]
    details = ["L%d %s sess=%s result=%s %s" % (r.n, fmt_wall(r.t), s, res, rest)
               for r, s, res, rest in verdicts[:DETAIL_LIMIT]]
    findings = [_finding(v[0], "merge-refused", False) for v in refused]
    status = REPORT
    if exp.merge_must_pass:
        details.append("expectation: every merge dominance check passes")
        status = FAIL if refused else PASS
        if not verdicts:
            details.append("no dominance verdict logged (no Re-Fly merge ran)")
    return _result("merge-refused", status, "%d verdict(s), %d refused" % (len(verdicts), len(refused)),
                   details, findings if status == FAIL else [])


# ---------------------------------------------------------------------------
# 17: LedgerGroundTruth
# ---------------------------------------------------------------------------

_GT_RESULT_RE = re.compile(
    r"^result: hardFailures=(\d+) reportOnly=(\d+) facetsCompared=(\d+) strict=(\S+)")
_GT_FACET_RE = re.compile(r"^facet=(\S+) kind=(\S+) id=(\S+) expected=(\S+) recon=(\S+) detail=(.*)$")
_GT_ALWAYS_HARD_POOLS = ("Funds", "SciencePool", "Reputation")


def gt_facet_is_hard(facet: str, kind: str, detail: str) -> bool:
    """Mirror of LedgerDivergenceReport.IsAlwaysHard (non-strict)."""
    if facet in _GT_ALWAYS_HARD_POOLS:
        return kind != "UpliftClampedExpected"
    if facet == "Vessel":
        return kind == "Consistency" and "guidCorroborated=true" in detail
    return False


def check_ground_truth(recs: Sequence[Rec], exp: Expectations) -> Dict:
    results = []
    facets: List[Tuple[str, str, str, bool]] = []
    for r in _parsek(recs, "LedgerGroundTruth"):
        m = _GT_RESULT_RE.match(r.msg)
        if m:
            results.append((r, int(m.group(1)), int(m.group(2)), int(m.group(3)), m.group(4)))
            continue
        if r.msg.startswith("LedgerDivergenceReport: total="):
            for c in r.cont:
                fm = _GT_FACET_RE.match(c.strip())
                if fm:
                    facets.append((fm.group(1), fm.group(2), fm.group(3),
                                   gt_facet_is_hard(fm.group(1), fm.group(2), fm.group(6))))
    if not results:
        return _result("ground-truth", SKIP, "no LedgerGroundTruth result line (harness not run)")
    findings = [_finding(r, "ground-truth", False, "hardFailures=%d" % hard)
                for r, hard, _, _, _ in results if hard > 0]
    details = ["L%d result hardFailures=%d reportOnly=%d facetsCompared=%d strict=%s"
               % (r.n, h, ro, fc, s) for r, h, ro, fc, s in results]
    names: Dict[Tuple[str, str, bool], int] = {}
    for f, k, _, hard in facets:
        names[(f, k, hard)] = names.get((f, k, hard), 0) + 1
    for (f, k, hard), c in sorted(names.items()):
        details.append("facet %s kind=%s x%d (%s)" % (f, k, c, "hard" if hard else "report-only"))
    hard_total = sum(h for _, h, _, _, _ in results)
    return _result("ground-truth", FAIL if findings else PASS,
                   "%d run(s), hardFailures=%d, report-only facets: %s" % (
                       len(results), hard_total,
                       ", ".join(sorted(set(f for f, _, _, hard in facets if not hard))) or "none"),
                   details, findings)


# ---------------------------------------------------------------------------
# walk-past-clock: a no-cutoff walk while committed rows lie ahead
# ---------------------------------------------------------------------------

_INSESSION_RE = re.compile(
    r"^OnLoad: post-rewind current-UT cutoff decision useCurrentUtCutoff=(True|False) "
    r"loadedUT=(\S+) .*?\bisRevert=(\S+) loadedScene=(\S+) .*?\bplanetariumReady=(True|False) "
    r".*?\bhasFutureLedgerActions=(True|False)")
_KSPLOAD_RE = re.compile(
    r"^OnKspLoad recalc decision: useCurrentUtCutoff=(True|False) maxUT=(\S+) "
    r"featureSupportsCutoff=(\S+) currentUtReady=(True|False) hasFutureActions=(True|False)")
_CURRENT_FULL_RE = re.compile(r"reason=(\S+) currentUT=(\S+) currentUtReady=(True|False)")
_COMMIT_FULL_RE = re.compile(r"reason=(\S+) onLoadInProgress=(\S+) rewindUTAdjustmentPending=(\S+)")
_INPUT_RE = re.compile(r"^RecalculateAndPatch: actionsTotal=(\d+), actionsAfterCutoff=(\d+), "
                       r"cutoffUT=(\S+)")
_PATCH_MOVE_RE = re.compile(r"^Patch(Funds|Science|Reputation): (\S+) -> (\S+) ")
_REVERT_BUDGET_RE = re.compile(r"^Revert budget walk: scene=(\S+) cutoffScene=(True|False) "
                               r"clockReady=(True|False) revertUT=(\S+)")

# Decision outcomes: what the walk's own decision line says about rows ahead.
AHEAD_KNOWN = "ahead"        # no cutoff, and the line says committed rows lie ahead of a ready clock
AHEAD_NONE = "none-ahead"    # no cutoff because nothing lies ahead (or a cutoff walk)
AHEAD_UNKNOWN = "unknown"    # no cutoff, and the line cannot say
AHEAD_HORIZON = "horizon"    # the Re-Fly start's double.MaxValue walk (by design)


def _insession_rows_ahead(r: Rec) -> bool:
    """Does an in-session decision line say committed rows lie ahead of a ready loaded
    clock (whatever its own walk then did)?"""
    if r.sub != "Scenario" or not r.msg.startswith(LIT_INSESSION_DECISION):
        return False
    m = _INSESSION_RE.match(r.msg)
    if not m:
        return False
    ut = _num(m.group(2))
    return m.group(5) == "True" and m.group(6) == "True" and ut is not None and ut > 0.0


def _classify_decision(r: Rec) -> Optional[Tuple[str, str, str]]:
    """(kind, ahead-outcome, why) for a walk decision / full-replay line, else None."""
    if r.sub == "Scenario" and r.msg.startswith(LIT_BUDGET_READY):
        return ("revert-budget", AHEAD_UNKNOWN,
                "revert budget walk (ApplyBudgetDeduction) with no decision line of its own")
    if r.sub == "Scenario" and r.msg.startswith(LIT_REVERT_BUDGET):
        m = _REVERT_BUDGET_RE.match(r.msg)
        if not m:
            return ("revert-budget", AHEAD_UNKNOWN, "revert budget line did not parse")
        scn, cut, ready, revert_ut = m.groups()
        why = "revert budget walk scene=%s revertUT=%s" % (scn, revert_ut)
        if cut == "True" and ready == "True":
            return ("revert-budget", AHEAD_NONE,
                    why + " (walks at the revert clock when rows lie ahead)")
        return ("revert-budget", AHEAD_UNKNOWN,
                why + " cutoffScene=%s clockReady=%s: a plain full replay" % (cut, ready))
    if r.sub == "Scenario" and r.msg.startswith(LIT_INSESSION_DECISION):
        m = _INSESSION_RE.match(r.msg)
        if not m:
            return ("in-session-load", AHEAD_UNKNOWN, "decision line did not parse")
        use, loaded_ut, revert, scn, ready, future = m.groups()
        ut = _num(loaded_ut)
        why = "in-session load scene=%s isRevert=%s loadedUT=%s" % (scn, revert, loaded_ut)
        if use == "True":
            return ("in-session-load", AHEAD_NONE, why + " (cutoff walk)")
        if future == "False":
            return ("in-session-load", AHEAD_NONE, why + " hasFutureLedgerActions=False")
        if ready == "True" and ut is not None and ut > 0.0 and ut == ut:
            return ("in-session-load", AHEAD_KNOWN, why + " hasFutureLedgerActions=True")
        return ("in-session-load", AHEAD_UNKNOWN,
                why + " planetariumReady=%s: the clock was not ready, so rows-ahead is not "
                "a reading against it" % ready)
    if r.sub != "LedgerOrchestrator":
        return None
    if r.msg.startswith(LIT_KSPLOAD_DECISION):
        m = _KSPLOAD_RE.match(r.msg)
        if not m:
            return ("ksp-load", AHEAD_UNKNOWN, "decision line did not parse")
        use, max_ut, feature, ready, future = m.groups()
        why = "cold load maxUT=%s featureSupportsCutoff=%s" % (max_ut, feature)
        if use == "True" or future == "False":
            return ("ksp-load", AHEAD_NONE, why)
        if ready == "True":
            return ("ksp-load", AHEAD_KNOWN, why + " currentUtReady=True hasFutureActions=True")
        return ("ksp-load", AHEAD_UNKNOWN,
                why + " currentUtReady=False: hasFutureActions is read against an unready "
                "clock (BUG-F full replay; the deferred seed walks again)")
    if r.msg.startswith(LIT_COMMIT_FULL):
        m = _COMMIT_FULL_RE.search(r.msg)
        return ("commit", AHEAD_UNKNOWN,
                "commit walk with an untrusted clock (%s): the commit path does not check for "
                "rows ahead then" % (" ".join(m.groups()) if m else "?"))
    if r.msg.startswith(LIT_CURRENT_FULL):
        m = _CURRENT_FULL_RE.search(r.msg)
        if m and m.group(3) == "True":
            return ("current-timeline", AHEAD_NONE,
                    "reason=%s currentUT=%s: nothing ahead of a ready clock" % m.groups()[:2])
        return ("current-timeline", AHEAD_UNKNOWN,
                "reason=%s currentUT=%s currentUtReady=False: rows-ahead was not checked"
                % (m.group(1), m.group(2)) if m else "full replay line did not parse")
    return None


def split_patch_walks(recs: Sequence[Rec]) -> List[Dict]:
    """Recalc units in log order. A unit opens at a walk decision / full-replay line
    (``_classify_decision``) or, with none pending, at the walk's input summary
    ``RecalculateAndPatch: actionsTotal=`` (a no-cutoff summary is VerboseOnChange,
    so it can be missing; a cutoff summary always prints). It is ``deferred`` at
    ``RecalculateAndPatch: deferred KSP state patch (``, or ``patched`` at the
    ``drawdown-guard authoritativeReduction=`` line, after which every
    ``Patch<Pool>: a -> b`` line belongs to it until the next walk boundary (a
    decision, an input summary, a guard / deferred line, a milestones reset)."""
    units: List[Dict] = []
    cur: Optional[Dict] = None
    window = 0  # count of ``Load classified`` lines seen: the load each unit belongs to

    def close():
        if cur is not None:
            units.append(cur)

    def new(rec, kind, ahead, why, cutoff=None, state="await"):
        return {"rec": rec, "kind": kind, "ahead": ahead, "why": why, "cutoff": cutoff,
                "state": state, "outcome": None, "patches": [], "input": None,
                "window": window}

    for r in _parsek(recs):
        if r.sub == "LoadPolicy" and r.msg.startswith(LIT_CLASSIFIED):
            window += 1
            continue
        d = _classify_decision(r)
        if d is not None:
            if d[0] == "revert-budget" and cur is not None and cur["kind"] == "revert-budget" \
                    and cur["state"] == "await" and cur["input"] is None:
                # The fixed build's decision line follows its singletons line: one walk.
                cur["ahead"], cur["why"] = d[1], d[2]
                continue
            close()
            cur = new(r, d[0], d[1], d[2])
            continue
        if r.sub == "LedgerOrchestrator" and r.msg.startswith(LIT_INPUT_SUMMARY):
            m = _INPUT_RE.match(r.msg)
            cutoff = m.group(3) if m else "?"
            if cur is not None and cur["state"] == "await" and cur["input"] is None:
                cur["input"] = r
                cur["cutoff"] = cutoff
                continue
            close()
            val = _num(cutoff)
            if val is not None and val >= HORIZON_CUTOFF:
                cur = new(r, "refly-start", AHEAD_HORIZON,
                          "cutoffUT=%s (RewindInvoker's RecalculateAndPatch(double.MaxValue))"
                          % cutoff, cutoff)
            elif cutoff == "null":
                cur = new(r, "plain", AHEAD_UNKNOWN,
                          "cutoffUT=null with no decision line before it", cutoff)
            else:
                cur = new(r, "cutoff", AHEAD_NONE, "cutoffUT=%s" % cutoff, cutoff)
            continue
        if r.sub == "Scenario" and r.msg.startswith(LIT_BUDGET_DONE):
            # The singletons line opened a revert walk that does not run.
            if cur is not None and cur["kind"] == "revert-budget" and cur["state"] == "await":
                cur = None
            continue
        if r.sub == "LedgerOrchestrator" and r.msg.startswith(LIT_DEFERRED):
            if cur is not None and cur["state"] == "await":
                cur["outcome"] = "deferred"
                cur["state"] = "done"
            close()
            cur = None
            continue
        if r.sub == "LedgerOrchestrator" and r.msg.startswith(LIT_PATCHED):
            if cur is not None and cur["state"] == "await":
                cur["state"] = "patch"
                cur["outcome"] = "patched"
                cur["guard"] = r
                continue
            close()
            cur = new(r, "plain", AHEAD_UNKNOWN, "a patched walk with no decision or input line",
                      state="patch")
            cur["outcome"] = "patched"
            cur["guard"] = r
            continue
        if _is_milestone_reset(r):
            if cur is not None and cur["state"] == "patch":
                close()
                cur = None
            continue
        if cur is not None and cur["state"] == "patch" and r.sub == "KspStatePatcher":
            m = _PATCH_MOVE_RE.match(r.msg)
            if m:
                before, after = _num(m.group(2)), _num(m.group(3))
                if before is not None and after is not None:
                    cur["patches"].append((m.group(1), before, after, r))
    close()
    _bind_load_decisions(units, recs)
    return units


def _bind_load_decisions(units: List[Dict], recs: Sequence[Rec]) -> None:
    """A revert walk with no cutoff and no decision of its own (``revert-budget``, or a
    ``plain`` walk before the decision line) runs inside the same OnLoad as the in-session
    decision, often BEFORE it (the singletons are already there, so the coroutine runs
    synchronously). When that load's decision line says committed rows lie ahead of a
    ready clock, the walk had the same rows ahead: it reads AHEAD_KNOWN. Bound only inside
    one load (between ``Load classified`` lines, so never on a log without one) and within
    ``LOAD_BIND_SECONDS``; a ``plain`` walk binds only to a decision AFTER it, since a
    plain walk after the decision is some later walk of that scene."""
    window = 0
    decisions: List[Tuple[int, Rec]] = []
    for r in _parsek(recs):
        if r.sub == "LoadPolicy" and r.msg.startswith(LIT_CLASSIFIED):
            window += 1
        elif window > 0 and _insession_rows_ahead(r):
            decisions.append((window, r))
    if not decisions:
        return
    for u in units:
        if u["ahead"] != AHEAD_UNKNOWN or u["kind"] not in ("revert-budget", "plain") \
                or u["window"] == 0:
            continue
        for w, d in decisions:
            if w != u["window"]:
                continue
            if u["kind"] == "plain" and d.n < u["rec"].n:
                continue
            if u["rec"].t is not None and d.t is not None \
                    and abs(d.t - u["rec"].t) > LOAD_BIND_SECONDS:
                continue
            u["ahead"] = AHEAD_KNOWN
            u["why"] += ("; the same load's decision (L%d) says committed rows lie ahead of "
                         "a ready clock (hasFutureLedgerActions=True, planetariumReady=True)"
                         % d.n)
            break


def _rises(unit: Dict) -> List[str]:
    return ["%s %s -> %s (L%d)" % (pool, _fmt_pool(b), _fmt_pool(a), r.n)
            for pool, b, a, r in unit["patches"] if a - b > RISE_EPSILON]


def check_walk_past_clock(recs: Sequence[Rec], exp: Expectations) -> Dict:
    """RW-2 / L13: a ledger walk with no cutoff while committed rows are dated after
    the clock pays the committed future early. FAIL only on full evidence: the walk's
    own decision line says rows lie ahead of a ready clock, the walk was not deferred,
    and a pool patch after it rose. The log does not say how much of a rise is the
    rows ahead, so a rise is the RW-2 signature, not a measured amount. A full replay
    whose decision line cannot say (an untrusted commit clock, an unready clock, a
    plain walk with no decision line) is REPORT. The Re-Fly start's horizon walk is
    by design and REPORT."""
    units = split_patch_walks(recs)
    if not units:
        return _result("walk-past-clock", SKIP, "no ledger walk decision or input line "
                       "(verbose logging off, or no recalc ran)")
    findings = []
    unknown_rises = []
    horizon = []
    ahead_quiet = 0
    for u in units:
        rises = _rises(u)
        if u["ahead"] == AHEAD_KNOWN:
            if u["outcome"] == "patched" and rises:
                findings.append(_finding(u["rec"], "walk-past-clock", False,
                                         "%s; no cutoff, then %s" % (u["why"], ", ".join(rises))))
            else:
                ahead_quiet += 1
        elif u["ahead"] == AHEAD_HORIZON:
            horizon.append("L%d %s %s; %s" % (u["rec"].n, fmt_wall(u["rec"].t), u["why"],
                                              ("rose: " + ", ".join(rises)) if rises
                                              else (u["outcome"] or "no patch line")))
        elif u["ahead"] == AHEAD_UNKNOWN and u["outcome"] == "patched" and rises:
            unknown_rises.append("L%d %s [%s] %s; rose: %s" % (
                u["rec"].n, fmt_wall(u["rec"].t), u["kind"], u["why"], ", ".join(rises)))
    counts: Dict[str, int] = {}
    for u in units:
        counts[u["ahead"]] = counts.get(u["ahead"], 0) + 1
    details = _finding_lines(findings)
    details.append("walks: %d (rows ahead stated: %d, nothing ahead or a cutoff: %d, "
                   "rows ahead unknown: %d, Re-Fly start horizon: %d)" % (
                       len(units), counts.get(AHEAD_KNOWN, 0), counts.get(AHEAD_NONE, 0),
                       counts.get(AHEAD_UNKNOWN, 0), counts.get(AHEAD_HORIZON, 0)))
    if ahead_quiet:
        details.append("no-cutoff walks with rows ahead that deferred or lifted no pool: %d"
                       % ahead_quiet)
    if unknown_rises:
        details.append("REPORT: no-cutoff walks that lifted a pool where the log does not say "
                       "whether committed rows lie ahead: %d" % len(unknown_rises))
        details.extend("  " + x for x in unknown_rises[:DETAIL_LIMIT])
    if horizon:
        details.append("REPORT: Re-Fly start horizon walks (by design, RewindInvoker): %d"
                       % len(horizon))
        details.extend("  " + x for x in horizon[:DETAIL_LIMIT])
    if findings:
        status = FAIL
    elif unknown_rises or horizon:
        status = REPORT
    else:
        status = PASS
    return _result("walk-past-clock", status,
                   "%d walk(s); %d paid committed rows past the clock, %d unknown with a rise, "
                   "%d Re-Fly start horizon" % (len(units), len(findings), len(unknown_rises),
                                                len(horizon)),
                   details, findings)


# ---------------------------------------------------------------------------
# guarded-clamp: paired transients vs one-sided
# ---------------------------------------------------------------------------

_GUARDED_RE = re.compile(
    r"^Patch(\w+): GUARDED (UPLIFT|DRAWDOWN) clamped resource=(\S+) running=(\S+) live=(\S+) "
    r"wouldBeTarget=(\S+) clampedTo=(\S+)")


def parse_guarded(recs: Sequence[Rec]) -> List[Dict]:
    """Every GUARDED clamp line with its signed leak: DRAWDOWN (live above the
    expectation) positive, UPLIFT negative. With the stock pool reference the leak is the
    ``filed StockPoolAdjustment amount=`` (``reason=leak``) of the same patch, filed just
    before the WARN (KspStatePatcher.MeasureStockPool, then EmitStockPoolLeak; used once,
    when within a second and of the direction's sign); there
    ``clampedTo - wouldBeTarget`` reads 0 once the row is carried. With no filed line (a
    pre-reference build) it is ``clampedTo - wouldBeTarget``, and when that is 0 too,
    ``live - running``. ``leakFrom`` names the source."""
    out = []
    last_filed: Dict[str, Tuple[float, Rec]] = {}
    for r in _parsek(recs):
        if r.sub == "StockPoolReference":
            fm = _FILED_RE.match(r.msg)
            if fm and fm.group(3) == "leak":
                amount = _num(fm.group(2))
                if amount is not None:
                    last_filed[fm.group(1)] = (amount, r)
            continue
        if r.sub != "KspStatePatcher" or "GUARDED " not in r.msg:
            continue
        m = _GUARDED_RE.match(r.msg)
        if not m:
            out.append({"rec": r, "resource": None, "dir": None, "leak": None, "leakFrom": None})
            continue
        running, live = _num(m.group(4)), _num(m.group(5))
        would, clamped = _num(m.group(6)), _num(m.group(7))
        leak, source = None, None
        filed_line = last_filed.pop(m.group(1), None)
        if filed_line is not None and (filed_line[0] < 0.0) == (m.group(2) == "UPLIFT") \
                and (filed_line[1].t is None or r.t is None
                     or abs(r.t - filed_line[1].t) <= 1.0):
            leak, source = filed_line[0], "filed L%d" % filed_line[1].n
        elif would is not None and clamped is not None and abs(clamped - would) > GUARDED_NET_ABS:
            leak, source = clamped - would, "clampedTo-wouldBeTarget"
        elif running is not None and live is not None:
            leak, source = live - running, "live-running"
        out.append({"rec": r, "resource": m.group(3), "dir": m.group(2), "leak": leak,
                    "leakFrom": source})
    return out


def _nets_to_zero(a: float, b: float) -> bool:
    tol = max(GUARDED_NET_ABS, GUARDED_NET_REL * max(abs(a), abs(b)))
    return abs(a + b) <= tol


def _same_burst(ra: Rec, rb: Rec) -> bool:
    if ra.t is None or rb.t is None:
        return True  # unstamped lines: the next opposite line of the resource is the burst
    return abs(rb.t - ra.t) <= GUARDED_BURST_SECONDS


def pair_guarded(lines: Sequence[Dict]) -> Tuple[List[Tuple[Dict, Dict]], List[Dict]]:
    """Greedy in log order: each unpaired line pairs with the next unpaired line of the
    same resource in the opposite direction inside one burst whose leak cancels it."""
    paired = set()
    pairs = []
    for i, a in enumerate(lines):
        if i in paired or a["leak"] is None:
            continue
        for j in range(i + 1, len(lines)):
            b = lines[j]
            if j in paired or b["leak"] is None:
                continue
            if not _same_burst(a["rec"], b["rec"]):
                break
            if b["resource"] == a["resource"] and b["dir"] != a["dir"] \
                    and _nets_to_zero(a["leak"], b["leak"]):
                paired.update((i, j))
                pairs.append((a, b))
                break
    single = [x for k, x in enumerate(lines) if k not in paired]
    return pairs, single


def check_guarded_clamp(recs: Sequence[Rec], exp: Expectations, used: Dict[str, int]) -> Dict:
    """A GUARDED clamp keeps a leak the ledger has no row for. An UPLIFT / DRAWDOWN
    pair on one resource inside one burst that nets to about zero is an ordering
    transient (a row landing one patch late) and passes. A one-sided line is a
    missing spending / earning channel and FAILs, unless the lane names
    ``guarded-one-sided`` (or a literal of the line) as expected."""
    lines = parse_guarded(recs)
    if not lines:
        return _result("guarded-clamp", PASS, "no GUARDED clamp line")
    pairs, single = pair_guarded(lines)
    findings = []
    for x in single:
        note = ("resource=%s %s leak=%s (%s), no opposite line within %gs nets it out"
                % (x["resource"], x["dir"], _fmt_pool(x["leak"]), x["leakFrom"],
                   GUARDED_BURST_SECONDS)
                if x["leak"] is not None else "GUARDED line did not parse (format drift?)")
        findings.append(_finding(x["rec"], "guarded-one-sided", x["leak"] is not None, note))
    _apply_expectations(findings, exp, used)
    details = _finding_lines(findings)
    details.append("paired transients (opposite directions, one burst, net about 0): %d"
                   % len(pairs))
    for a, b in pairs[:DETAIL_LIMIT]:
        details.append("  L%d %s %s %s + L%d %s %s = %s" % (
            a["rec"].n, a["resource"], a["dir"], _fmt_pool(a["leak"]),
            b["rec"].n, b["dir"], _fmt_pool(b["leak"]), _fmt_pool(a["leak"] + b["leak"])))
    n_exp = sum(1 for f in findings if f["expected"])
    summary = "%d GUARDED line(s): %d paired transient(s), %d one-sided" % (
        len(lines), len(pairs), len(single))
    if n_exp:
        summary += ", %d expected" % n_exp
    return _result("guarded-clamp", _status_of(findings), summary, details, findings,
                   pairs=len(pairs))


# ---------------------------------------------------------------------------
# leak-shape: a leak that repeats a zeroed duplicate, or follows a Discard Re-fly
# ---------------------------------------------------------------------------

_FILED_RE = re.compile(r"^Patch(\w+): filed StockPoolAdjustment amount=(\S+) reason=(\S+) ut=(\S+)")
_ZEROED_RES = (
    ("Funds", "Funds", re.compile(r"^Milestone funds skipped \(not effective\): .*?"
                                  r"fundsAwarded=(\S+?),?$")),
    ("Funds", "Funds", re.compile(r"^\w+ funds skipped \(not effective\): .*?fundsReward=(\S+?),?$")),
    ("Funds", "Funds", re.compile(r"^FundsEarning skipped \(not effective\): fundsAwarded=([^,\s]+)")),
    ("ScienceModule", "Science", re.compile(r"^\w+ science skipped \(not effective\): .*?"
                                            r"(?:scienceReward|sciAwarded)=(\S+?),?$")),
)
_REF_LOADED_RE = re.compile(r"^Loaded \((\w+)\): pools=")
DISCARD_WALKS = 2


def _zeroed_amount(r: Rec) -> Optional[Tuple[str, float]]:
    for sub, pool, rx in _ZEROED_RES:
        if r.sub != sub:
            continue
        m = rx.match(r.msg)
        if m:
            v = _num(m.group(1))
            return (pool, v) if v is not None else None
    return None


def check_leak_shape(recs: Sequence[Rec], exp: Expectations) -> Dict:
    """Two shapes of a ``reason=leak`` StockPoolAdjustment that a pool already paid:

    * The leak equals (within LEAK_MATCH_TOLERANCE) the funds / science the same walk
      zeroed as not effective - their sum, or one row: stock paid the duplicate and
      the patch filed it again instead of taking it back. A walk opens at the
      milestones reset; the zeroed rows are the module lines
      ``Milestone funds skipped (not effective):``, ``<Type> funds skipped (not
      effective):``, ``FundsEarning skipped (not effective):`` and their science
      twins (reputation logs no amount, so it is not matched). Only a positive leak
      can repeat a payment.
    * RW-5 (fixed by PR #2314): any ``reason=leak`` row in the first DISCARD_WALKS
      patched walks after ``[StockPoolReference] Loaded (DiscardReFly)`` (the load's
      first walk and its currency re-patch), before the next scene change or
      reference load. That patch is authoritative, so it never measures a leak."""
    zeroed: List[Tuple[str, float, int]] = []
    findings = []
    leaks = 0
    filed = 0
    discard: Optional[Rec] = None
    discard_walks = 0
    for r in recs:
        if r.scene is not None:
            discard = None
            continue
        if not r.is_parsek:
            continue
        if _is_milestone_reset(r):
            zeroed = []
            continue
        z = _zeroed_amount(r)
        if z is not None:
            if z[1] > 0.0:
                zeroed.append((z[0], z[1], r.n))
            continue
        if r.sub == "StockPoolReference":
            lm = _REF_LOADED_RE.match(r.msg)
            if lm:
                discard = r if lm.group(1) == "DiscardReFly" else None
                discard_walks = 0
                continue
        if discard is not None and r.sub == "LedgerOrchestrator" and r.msg.startswith(LIT_PATCHED):
            discard_walks += 1
            if discard_walks > DISCARD_WALKS:
                discard = None
            continue
        if r.sub != "StockPoolReference":
            continue
        m = _FILED_RE.match(r.msg)
        if not m:
            continue
        filed += 1
        if m.group(3) != "leak":
            continue
        leaks += 1
        pool, amount = m.group(1), _num(m.group(2))
        if discard is not None:
            findings.append(_finding(r, "leak-after-discard-refly", False,
                                     "%s leak %s in patched walk %d after the Loaded (DiscardReFly) "
                                     "of L%d (RW-5)" % (pool, m.group(2), max(discard_walks, 1),
                                                        discard.n)))
        if amount is None or amount <= 0.0:
            continue
        mine = [(v, n) for p, v, n in zeroed if p == pool]
        if not mine:
            continue
        total = sum(v for v, _ in mine)
        hit = None
        if abs(amount - total) <= LEAK_MATCH_TOLERANCE:
            hit = "the sum %s of %d zeroed row(s) (lines %s)" % (
                _fmt_pool(total), len(mine), ",".join(str(n) for _, n in mine[:10]))
        else:
            for v, n in mine:
                if abs(amount - v) <= LEAK_MATCH_TOLERANCE:
                    hit = "the zeroed row at L%d (%s)" % (n, _fmt_pool(v))
                    break
        if hit:
            findings.append(_finding(r, "leak-equals-zeroed", False,
                                     "%s leak %s equals %s in the same walk: stock paid the "
                                     "duplicate and the patch filed it again" % (pool, m.group(2), hit)))
    details = _finding_lines(findings)
    return _result("leak-shape", FAIL if findings else PASS,
                   "%d StockPoolAdjustment filed, %d reason=leak, %d repeat a zeroed duplicate, "
                   "%d after a Discard Re-fly load" % (
                       filed, leaks, sum(1 for f in findings if f["key"] == "leak-equals-zeroed"),
                       sum(1 for f in findings if f["key"] == "leak-after-discard-refly")),
                   details, findings)


# ---------------------------------------------------------------------------
# 18: warnings surface
# ---------------------------------------------------------------------------

_NORM_RULES = (
    (re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"), "<id>"),
    (re.compile(r"\b(?:act_)?[0-9a-fA-F]{16,}\b"), "<id>"),
    (re.compile(r"'[^']*'"), "'<s>'"),
    (re.compile(r"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?"), "<n>"),
)


def warning_key(sub: str, level: str, msg: str) -> str:
    """Stable key for a WARN / ERROR line: level, subsystem and the message with
    guids, hex ids, quoted names and numbers replaced."""
    text = msg
    for rx, repl in _NORM_RULES:
        text = rx.sub(repl, text)
    return "%s [%s] %s" % (level, sub, text[:200])


def warning_keys(recs: Sequence[Rec]) -> Dict[str, List[Rec]]:
    out: Dict[str, List[Rec]] = {}
    for r in _parsek(recs):
        if r.level in ("WARN", "ERROR"):
            out.setdefault(warning_key(r.sub, r.level, r.msg), []).append(r)
    return out


def check_warnings(recs: Sequence[Rec], exp: Expectations,
                   baseline: Optional[Sequence[Rec]] = None) -> Dict:
    by_sub: Dict[Tuple[str, str], int] = {}
    for r in _parsek(recs):
        if r.level in ("WARN", "ERROR"):
            by_sub[(r.level, r.sub)] = by_sub.get((r.level, r.sub), 0) + 1
    details = ["%-5s %-28s %d" % (lv, sub, c)
               for (lv, sub), c in sorted(by_sub.items(), key=lambda x: (-x[1], x[0]))]
    warns = sum(c for (lv, _), c in by_sub.items() if lv == "WARN")
    errs = sum(c for (lv, _), c in by_sub.items() if lv == "ERROR")
    summary = "%d WARN, %d ERROR in %d subsystem(s)" % (warns, errs, len(set(s for _, s in by_sub)))
    new_keys: List[str] = []
    if baseline is not None:
        mine = warning_keys(recs)
        base = warning_keys(baseline)
        new_keys = [k for k in mine if k not in base]
        details.append("only in this run (vs baseline): %d key(s)" % len(new_keys))
        for k in new_keys[:DETAIL_LIMIT * 2]:
            details.append("  %dx L%d %s" % (len(mine[k]), mine[k][0].n, k))
        if len(new_keys) > DETAIL_LIMIT * 2:
            details.append("  ... %d more" % (len(new_keys) - DETAIL_LIMIT * 2))
        summary += "; %d key(s) not in the baseline" % len(new_keys)
    return _result("warnings", REPORT, summary, details, newKeys=new_keys)


# ---------------------------------------------------------------------------
# 19: saved ledger rows at UT 0
# ---------------------------------------------------------------------------

def parse_ledger_actions(text: str) -> List[Dict[str, str]]:
    """Top-level values of every GAME_ACTION node of a ledger.pgld (ConfigNode
    text). Child nodes inside an action are skipped."""
    actions: List[Dict[str, str]] = []
    depth = 0
    cur: Optional[Dict[str, str]] = None
    pending_action = False
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("//"):
            continue
        if line == "{":
            depth += 1
            if pending_action and depth == 1:
                cur = {}
            pending_action = False
            continue
        if line == "}":
            if depth == 1 and cur is not None:
                actions.append(cur)
                cur = None
            depth = max(0, depth - 1)
            continue
        if "=" not in line:
            pending_action = depth == 0 and line == "GAME_ACTION"
            continue
        if depth == 1 and cur is not None:
            k, _, v = line.partition("=")
            cur[k.strip()] = v.strip()
    return actions


def check_ledger_ut0(ledgers: Optional[Sequence[Tuple[str, str]]]) -> Dict:
    """ledgers: (label, ledger.pgld text) pairs found in the run folder."""
    if not ledgers:
        return _result("ledger-ut0", SKIP, "no saved ledger.pgld in the run folder")
    details = []
    total = 0
    for label, text in ledgers:
        rows = parse_ledger_actions(text)
        odd = []
        for a in rows:
            ut = _num(a.get("ut"))
            try:
                typ = int(a.get("type", "-1"))
            except ValueError:
                typ = -1
            if ut == 0.0 and typ not in SEED_ACTION_TYPES:
                odd.append(a)
        total += len(odd)
        details.append("%s: %d row(s), %d at UT 0 that are not seeds" % (label, len(rows), len(odd)))
        for a in odd[:DETAIL_LIMIT]:
            typ = a.get("type", "?")
            name = GAME_ACTION_TYPES.get(int(typ), "type " + typ) if typ.lstrip("-").isdigit() else typ
            details.append("  %s actionId=%s recordingId=%s" % (
                name, a.get("actionId", "?"), a.get("recordingId", "") or "-"))
    return _result("ledger-ut0", REPORT, "%d non-seed row(s) at UT 0" % total, details)


# ---------------------------------------------------------------------------
# The audit
# ---------------------------------------------------------------------------

def audit(lines: Sequence[str], expectations: Optional[Expectations] = None,
          baseline_lines: Optional[Sequence[str]] = None,
          ledgers: Optional[Sequence[Tuple[str, str]]] = None,
          log_label: str = "KSP.log") -> Dict:
    exp = expectations or Expectations()
    recs = parse_log(lines)
    base = parse_log(baseline_lines) if baseline_lines is not None else None
    used: Dict[str, int] = {}
    results = [
        check_ledger_anomaly(recs, exp),
        check_recalc_rate(recs, exp),
        check_paid_twice(recs, exp),
        check_subject_over_cap(recs, exp),
        check_take_back(recs, exp, used),
        check_take_back_off(recs, exp, used),
        check_stock_pool_adjustment(recs, exp),
        check_debt(recs, exp, used),
        check_hard_stop(recs, exp, used),
        check_deferred_patch(recs, exp),
        check_load_kind(recs, exp),
        check_refly_leftover(recs, exp, used),
        check_merge_refused(recs, exp),
        check_already_owned(recs, exp),
        check_same_craft(recs, exp),
        check_finalizer_oddity(recs, exp),
        check_ground_truth(recs, exp),
        check_walk_past_clock(recs, exp),
        check_guarded_clamp(recs, exp, used),
        check_leak_shape(recs, exp),
        check_warnings(recs, exp, base),
        check_ledger_ut0(ledgers),
    ]
    unused = [n for n in exp.expected if n not in used] + \
             [lit for lit in exp.expected_lines if lit not in used]
    times = [r.t for r in recs if r.t is not None]
    counts = {s: sum(1 for r in results if r["status"] == s)
              for s in (PASS, FAIL, EXPECTED, SKIP, REPORT)}
    return {
        "log": log_label,
        "lines": len(lines),
        "parsekLines": sum(1 for r in recs if r.is_parsek),
        "span": (max(times) - min(times)) if len(times) > 1 else 0.0,
        "expectations": exp.source,
        "unusedExpectations": unused,
        "baseline": baseline_lines is not None,
        "results": results,
        "counts": counts,
        "verdict": FAIL if counts[FAIL] else PASS,
    }


def exit_code(result: Dict) -> int:
    return 1 if result["verdict"] == FAIL else 0


def render(result: Dict) -> str:
    c = result["counts"]
    out = [
        "Parsek ledger audit",
        "log: %s  lines=%d  parsek=%d  span=%s" % (
            result["log"], result["lines"], result["parsekLines"], _fmt_span(result["span"])),
        "expectations: %s" % (result["expectations"] or "none"),
        "verdict: %s (fail=%d expected=%d pass=%d skip=%d report=%d)" % (
            result["verdict"], c[FAIL], c[EXPECTED], c[PASS], c[SKIP], c[REPORT]),
    ]
    if result["unusedExpectations"]:
        out.append("expectations never matched (the lane did not show them): "
                   + ", ".join(result["unusedExpectations"]))
    out += ["", "SUMMARY"]
    for i, r in enumerate(result["results"], 1):
        out.append("  %2d %-9s %-22s %s" % (i, r["status"], r["check"], r["summary"]))
    by_id = {r["check"]: r for r in result["results"]}
    out += ["", "RECALC / POOL TIMELINE (one row per patched recalc that moved a pool)"]
    rows = by_id["recalc-rate"].get("rows") or []
    if rows:
        out += ["  " + x for x in pool_timeline(rows)]
    else:
        out.append("  (" + by_id["recalc-rate"]["summary"] + ")")
    out += ["", "IDENTITIES CREDITED TWICE WITHIN ONE LEDGER WALK"]
    out += ["  " + x for x in credited_twice_table(by_id["paid-twice"].get("dups") or [])]
    out += ["", "DETAILS"]
    for r in result["results"]:
        out.append("== %s: %s [%s]" % (r["check"], r["title"], r["status"]))
        out.append("   " + r["summary"])
        out += ["   " + d for d in r["details"]]
    return "\n".join(out) + "\n"
