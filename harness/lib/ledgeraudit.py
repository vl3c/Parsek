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
    ("warnings", "Parsek WARN / ERROR surface"),
    ("ledger-ut0", "Saved ledger rows at UT 0 that are not seeds"),
)
CHECK_IDS = tuple(c for c, _ in CHECKS)
CHECK_TITLES = dict(CHECKS)

# Names an expectations file may list under ``expected``. Only these findings can
# read EXPECTED; every other FAIL (an anomaly, a double credit, an abort) cannot be
# masked by a lane's expectations.
EXPECTABLE_NAMES = ("take-back", "debt", "not-affordable", "refly-leftover")

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
    40: "ContractParameterComplete",
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
    return _scan_check("refly-leftover", recs, exp, used, (
        (LIT_UNBOUND, None, "refly-leftover", True),
        (LIT_RETIRED, None, "refly-leftover", True),
    ), "Re-Fly leftover")


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
