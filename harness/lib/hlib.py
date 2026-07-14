"""Pure decision logic for the M-A5 automated-testing harness orchestrator.

This module is the M-A5 analogue of the provisioner's ``provlib.py``: it holds
every non-trivial decision the harness makes as a side-effect-free function so
it is unit-testable with NO KSP, NO network, and NO filesystem writes. The thin
imperative shell (``run.py`` -- a SEPARATE module, not built here) does all I/O
(launch KSP, tail the channel files, kill the process tree, copy fixtures,
subprocess the ps1 verifiers) and calls into here for every branch it takes.

Covered here (design docs/dev/design-autotest-harness-core.md):
  - spec TOML validation (``validate_spec``)
  - scenario selection by id / tier / tag / cadence (``select_scenarios``)
  - seam response-stream evaluation with first-wins dedupe (``evaluate_response_stream``)
  - the named line parsers: ``parse_batch_complete_line``,
    ``parse_analysis_red_token``, ``parse_analysis_json``, ``parse_results_failures``
  - verdict classification (``classify_verdict`` + ``should_retry`` +
    ``classify_expected_fail`` + ``resolve_terminal``)
  - expectations evaluation (``evaluate_expectations``)
  - the M-B2 ledger-oracle PURE support: the ``[expectations.ledger]`` spec-surface
    validator (``validate_ledger_expectations``), the produced-save ``careerSave``
    block read (``parse_career_save_block``), and the leg-A stock-award capture
    (``parse_stock_award_lines`` / ``dedupe_captured_awards`` /
    ``unmatched_captured_awards``); the oracle MATH is the sibling ``oracle.py``
  - log-validation profile selection (``select_logvalidate_profile``)
  - budget arithmetic (``step_wait_ok`` / ``required_step_wait``)
  - instance admission reuse over provlib (``admit_instance`` / ``build_expected_admission``)
  - coverage + flake computation (``compute_coverage`` / ``compute_flake``)
  - result-record serialization + schema gate (``serialize_result`` /
    ``deserialize_result`` / ``check_schema``)

Design authority: docs/dev/design-autotest-harness-core.md (Module M-A5).
Consumed contracts pinned against their public surfaces: the command seam
(response line grammar + verb table), the autorun hooks (BATCH_COMPLETE v1
line), the offline analyzer + baseline (``RED=`` gate token + the
``.analysis.json`` fail/stale split + ``BASELINE-*`` findings), the provisioner
(``provlib.compare_manifest`` / ``project_admission``).

ASCII only; stdlib only (plus provlib, the M-A6 pure sibling, for admission).
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence, Tuple

SCHEMA_VERSION = 1

# ---------------------------------------------------------------------------
# Vocabulary tables (design Data Model + consumed seam verb table).
# ---------------------------------------------------------------------------

# Scenario tiers (design spec `tier` enum). `pending-fixture` is a readiness state,
# NOT a cadence: a scenario whose fixture save is not committed yet self-declares
# `pending-fixture` so it is EXCLUDED from every --cadence run (CADENCE_TIERS maps no
# cadence to it) instead of INVALID(staging)-ing terminally on each daily run and
# self-quarantining a scenario that never actually ran (M-A5 integration item 4). An
# operator re-tiers it to its real cadence tier the moment the fixture lands.
TIERS: Tuple[str, ...] = ("perpr", "daily", "nightly", "weekly", "pending-fixture")

# The two provisioned instance profiles (design + M-A6).
INSTANCE_PROFILES: Tuple[str, ...] = ("stock-minimal", "modded-compat")

# v1 injectedRecordings value set (design S4). Any other value is rejected;
# preset/corpus-scoped injection is DEFERRED to M-A4 / M-B5.
INJECTED_RECORDINGS: Tuple[str, ...] = ("none", "all-synthetic")

# Retry policies (design [retry].policy).
RETRY_POLICIES: Tuple[str, ...] = ("once", "none")

# Seam verdicts an `expect` may name (design). INTERRUPTED is UNREACHABLE in v1
# (the harness never restarts KSP mid-run, so the seam's at-most-once replay
# verdict is never observed) and is therefore NOT a valid `expect`.
SEAM_EXPECT_VERDICTS: Tuple[str, ...] = ("OK", "ERROR", "REJECTED", "TIMEOUT")

# M-B1 autopilot driver (design "Scenario spec [driver] extension"). validate_spec
# now accepts kind == "autopilot" as a SUPERSET of the seam driver; the mission
# step's `expect` is a fixed token, distinct from the seam verdicts above.
DRIVER_KINDS: Tuple[str, ...] = ("seam", "autopilot")
MISSION_STEP_EXPECT = "MISSION-OK"

# The mission subprocess's terminal verdicts (design Mission verdict). MISSION-OK
# is the met signal; the other four are DRIVER-VALIDITY INVALIDs mapped by
# classify_mission_step into retryable INVALID subkinds.
MISSION_VERDICT_OK = "MISSION-OK"
MISSION_VERDICT_ASSERT_FAIL = "MISSION-ASSERT-FAIL"
MISSION_VERDICT_CONNECT_TIMEOUT = "MISSION-CONNECT-TIMEOUT"
MISSION_VERDICT_FLAKE = "MISSION-FLAKE"
MISSION_VERDICT_ERROR = "MISSION-ERROR"
MISSION_VERDICTS: Tuple[str, ...] = (
    MISSION_VERDICT_OK, MISSION_VERDICT_CONNECT_TIMEOUT, MISSION_VERDICT_ASSERT_FAIL,
    MISSION_VERDICT_FLAKE, MISSION_VERDICT_ERROR,
)

# Seam known-verb table (consumed contract, design-autotest-command-seam.md).
# M-C1 (design-autotest-seam-verbs-c1.md) moved four verbs from RESERVED to
# IMPLEMENTED, mirroring the C# ReservedVerbs -> ImplementedVerbs move: InvokeRewind,
# AnswerMergeDialog, TimeJump, KscAction. The other eleven stay RESERVED. The tuple order
# below mirrors the C# ImplementedVerbs set (TestCommandVerbs.cs) exactly.
IMPLEMENTED_SEAM_VERBS: Tuple[str, ...] = (
    "SetSetting", "StartRecording", "StopRecording", "CommitTree", "DiscardTree",
    "RecordingState", "RunTests", "LoadGame", "MissionMark", "FlushAndQuit",
    "InvokeRewind", "AnswerMergeDialog", "TimeJump", "KscAction",
)
RESERVED_SEAM_VERBS: Tuple[str, ...] = (
    "StartLoopPlayback", "StopPlayback", "EnterWatchMode", "SealSlot", "StashSlot",
    "FlySlot", "RouteCommand", "MissionConfig", "SimulateStockSwitchClick",
    "CrashAfterJournalPhase", "RunInvariantReport",
)

# Harness-owned, fixed Tier-C anomaly token set (design verifier 6 / N2). A
# scenario only ADDS known-benign exceptions via `allowedAnomalies`; it never
# redefines this set.
ANOMALY_TOKENS: Tuple[str, ...] = (
    "icon-jump", "line-blink", "parity-drift", "decision-vs-truth",
    "polyline-orbit-overlap", "rigid-seam-tangent-discontinuity", "ledger-vs-truth",
)

# Log-validator rule codes (consumed contract, design verifier 4).
LOGVALIDATE_MARKER_PAIRING_RULES: Tuple[str, ...] = (
    "SES-000", "SES-001", "REC-001", "REC-003",
)
LOGVALIDATE_RECORDING_RULES: Tuple[str, ...] = ("REC-001", "REC-003")
LOGVALIDATE_ALWAYS_MANDATORY: Tuple[str, ...] = ("FMT-001", "FMT-002", "WRN-001")

# The seam's own fallback deferral ceiling (design S8). Spec validation caps a
# deferred-step budget at 60s BELOW this so the harness step-wait can always
# clear the seam's deferral window with margin.
SEAM_FALLBACK_DEFERRAL_SECONDS = 600
MAX_DEFERRED_STEP_BUDGET_SECONDS = 540
STEP_WAIT_MARGIN_SECONDS = 60

# Deferred (long-running) seam verbs whose per-step budget the 540s cap governs.
# M-C1 added InvokeRewind and TimeJump: the two two-phase verbs whose per-step budget
# the 540s cap + step-wait margin must govern. AnswerMergeDialog and KscAction are
# bounded-wait but complete quickly, so they ride the ordinary per-verb deferral budget
# and are NOT deferred here (design-autotest-seam-verbs-c1.md, hlib companion changes).
DEFERRED_SEAM_VERBS: Tuple[str, ...] = ("RunTests", "LoadGame", "InvokeRewind", "TimeJump")

# Per-verb seam-side DISPATCH deferral budgets (seconds), mirroring the C#
# DeferralBudget.BudgetSeconds table (TestCommands/TestCommandDispatcher.cs). A verb
# that is NOT a two-phase DEFERRED_SEAM_VERB still parks at the seam FIFO head up to
# its OWN dispatch deferral budget before the seam self-emits a TIMEOUT terminal
# (classified retryable driver-INVALID). If the harness step-wait for such a verb is
# only the bare per-step budget it can KILL a genuinely-deferring verb BEFORE the seam
# surfaces that TIMEOUT (M-A5 integration item 3): AnswerMergeDialog (120s dialog wait)
# and KscAction (60s career-ready wait) are the motivating cases -- deliberately NOT
# two-phase-deferred (they complete quickly once ready), but their nonzero deferral
# budget must be out-waited + margin so the seam's own verdict is OBSERVED, not
# pre-empted. Unlisted verbs ride DISPATCH_DEFERRAL_DEFAULT_SECONDS (the C# default,
# 60s). RunTests is scenario-budget-authoritative and is handled by the deferred
# branch; it is intentionally absent here.
DISPATCH_DEFERRAL_DEFAULT_SECONDS = 60.0
DISPATCH_DEFERRAL_BUDGET_SECONDS: Dict[str, float] = {
    "LoadGame": 300.0,
    "StartRecording": 180.0,
    "InvokeRewind": 300.0,
    "AnswerMergeDialog": 120.0,
    "TimeJump": 120.0,
    "KscAction": 60.0,
}

# The literal the harness substitutes with runSaveName before writing a LoadGame
# line to the channel (design [driver]).
RUN_SAVE_TOKEN = "${runSave}"

# Findings-list precedence marker (design S2): a rule id with this prefix is a
# fixture-authoring/baseline meta-finding, never a real Parsek defect.
BASELINE_RULE_PREFIX = "BASELINE-"


# ---------------------------------------------------------------------------
# Logging format (pure). run.py writes these to harness/results/<runId>.log.
# ---------------------------------------------------------------------------


def format_log_line(level: str, step: str, message: str) -> str:
    """Format one harness-log line: ``[Harness][LEVEL][Step] message``.

    Mirrors ParsekLog's ``[Parsek][LEVEL][Subsystem]`` and the provisioner's
    ``[Provision][LEVEL][Step]`` (design Diagnostic Logging). ``level`` /
    ``step`` are passed through so a caller typo is visible, not swallowed.
    """
    return "[Harness][%s][%s] %s" % (level, step, message)


# ---------------------------------------------------------------------------
# Named line parsers (design "named line parsers"). All pure over strings.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class BatchComplete:
    total: int
    passed: int
    failed: int
    skipped: int
    category: str
    scene: str


_BATCH_RE = re.compile(
    r"BATCH_COMPLETE v1 "
    r"total=(?P<total>\d+) passed=(?P<passed>\d+) failed=(?P<failed>\d+) "
    r"skipped=(?P<skipped>\d+) category=(?P<category>\S+) scene=(?P<scene>\S+)"
)


def parse_batch_complete_line(line: str) -> Optional[BatchComplete]:
    """Parse an M-A3 ``BATCH_COMPLETE v1`` line into its tally.

    ``line`` may carry the ``[Parsek][INFO][TestRunner]`` prefix; only the
    ``BATCH_COMPLETE v1 ...`` span is matched. Returns None for anything that is
    not a v1 line -- crucially a FUTURE ``BATCH_COMPLETE v2`` line returns None
    (contract guard, design Test Plan): a v1 harness must NOT silently misparse a
    v2 tally. The regex ends at the six fixed tokens in fixed order (the frozen
    orchestrator contract, InGameTestRunner.FormatBatchCompleteLine).
    """
    if line is None:
        return None
    m = _BATCH_RE.search(line)
    if not m:
        return None
    return BatchComplete(
        total=int(m.group("total")),
        passed=int(m.group("passed")),
        failed=int(m.group("failed")),
        skipped=int(m.group("skipped")),
        category=m.group("category"),
        scene=m.group("scene"),
    )


def find_batch_complete_lines(log_text: str) -> List[BatchComplete]:
    """Parse every ``BATCH_COMPLETE v1`` line in a KSP.log body (multi-category
    autorun emits per-token + aggregate lines, design edge 19)."""
    out: List[BatchComplete] = []
    for line in (log_text or "").splitlines():
        bc = parse_batch_complete_line(line)
        if bc is not None:
            out.append(bc)
    return out


def select_batch_complete(
    batches: Sequence[BatchComplete], category: str, scene: Optional[str] = None
) -> Optional[BatchComplete]:
    """Select the BATCH_COMPLETE line matching the driven category (+ scene when
    given), design edge 19. Exact category match; if a scene is supplied it must
    also match. Returns None when no line matches (a declared category with no
    per-token line -> the caller reds batch-incomplete)."""
    for bc in batches:
        if bc.category == category and (scene is None or bc.scene == scene):
            return bc
    return None


# ---------------------------------------------------------------------------
# Multi-category BATCH_COMPLETE aggregate (M-A5.1 revision of design note N3).
# A RunTests step with multiple categories ("A,B") or the literal "all" drives the
# M-A3 multi-category autorun: each constituent RunCategory batch emits its OWN
# per-category BATCH_COMPLETE line, then H1's multi-category driver emits ONE final
# aggregate line with category=multi:<count> carrying the UNION tally (design
# design-autotest-autorun-hooks.md ~270). v1's single-batch parser (select_batch_complete
# exact-category match) never matched such a selector: no per-category line carries
# "A,B", so the run false-reds batch-incomplete. This resolves that.
# ---------------------------------------------------------------------------

# The aggregate line's category token: "multi:<count>" (design M-A3 ~270). Anchored
# so a real C# category name (alphanumerics) can never be mistaken for the aggregate.
_MULTI_CATEGORY_RE = re.compile(r"^multi:(\d+)$")


def is_multi_category_selector(selector: Optional[str]) -> bool:
    """True when a RunTests/autorun selector drives MORE THAN ONE category and the
    M-A3 multi-category autorun therefore emits a category=multi:<count> aggregate.

    Two forms drive multiple categories (design M-A3 "Multi-category selector"): the
    literal ``all`` and a comma-separated token list. A single bare category token
    drives one batch + one BATCH_COMPLETE line (no aggregate). A None/empty selector
    (a seam-only scenario with no batch) is not multi.
    """
    if not selector:
        return False
    s = selector.strip()
    return s == "all" or "," in s


def _is_aggregate(bc: BatchComplete) -> bool:
    return _MULTI_CATEGORY_RE.match(bc.category or "") is not None


def aggregate_category_count(bc: BatchComplete) -> Optional[int]:
    """The ``<count>`` an aggregate ``category=multi:<count>`` line declares, or None
    when ``bc`` is not an aggregate (design M-A3 ~270). This READS the regex count
    group v1 parsed but never cross-checked (SF2 / NIT 2): the count is the number of
    categories the multi-category autorun ran, so exactly that many per-category
    BATCH_COMPLETE lines must be present -- resolve_batch_complete gates on it."""
    m = _MULTI_CATEGORY_RE.match(bc.category or "")
    return int(m.group(1)) if m else None


def select_aggregate_batch_complete(
    batches: Sequence[BatchComplete],
) -> Optional[BatchComplete]:
    """Return the multi-category AGGREGATE BATCH_COMPLETE line (category
    ``multi:<count>``), or None when absent (design M-A3 ~270).

    The aggregate's ``failed`` is the UNION failed count across every category, so
    ``failed == 0`` on the aggregate is necessary (though the caller also cross-checks
    the per-category lines, see resolve_batch_complete) for ALL categories to have
    passed. A missing aggregate is NOT this function's concern -- resolve_batch_complete
    classifies a missing aggregate with per-category lines present as a defined fault.
    Likewise a DUPLICATE aggregate (two ``multi:<count>`` lines) is resolve_batch_complete's
    concern (item 10): this returns the FIRST for its `failed`, but resolve_batch_complete
    reds the duplicate as a defined fault rather than silently first-winning.
    """
    for bc in batches:
        if _is_aggregate(bc):
            return bc
    return None


@dataclass(frozen=True)
class BatchCompleteSelection:
    present: bool             # a usable gating BATCH_COMPLETE line was resolved
    failed: Optional[int]     # the gating failed count (aggregate UNION for multi)
    category: Optional[str]
    scene: Optional[str]
    multi: bool               # the selector drove multiple categories
    aggregate_missing: bool   # multi selector, per-category lines present, NO aggregate
    per_category_count: int   # non-aggregate BATCH_COMPLETE lines seen
    # SF2: aggregate present but its multi:<count> != per_category_count (a category
    # batch was cut off, or an unexpected extra batch appeared). A defined fault,
    # same treatment as aggregate_missing (present=False), never a silent pass.
    category_count_mismatch: bool = False
    expected_category_count: Optional[int] = None  # the aggregate's declared <count>
    # Item 10: MORE THAN ONE category=multi:<count> aggregate line present. Two aggregates
    # mean the multi-category summary emitted twice (a duplicated / concatenated run); a
    # silent first-wins could gate green off the wrong summary. A defined fault, same
    # treatment as aggregate_missing (present=False), never a silent pass.
    duplicate_aggregate: bool = False


def resolve_batch_complete(
    batches: Sequence[BatchComplete], selector: Optional[str], scene: Optional[str] = None
) -> BatchCompleteSelection:
    """Resolve the gating BATCH_COMPLETE line for a driven selector (M-A5.1 N3).

    Single-category (or seam-only) selector: unchanged v1 behavior -- the exact
    per-category line (select_batch_complete), or the first line when the selector is
    empty. Multi-category selector ("all" / "A,B"): the gating line is the
    ``category=multi:<count>`` AGGREGATE, whose ``failed`` is the UNION across every
    category. Two invariants this enforces that v1 could not:

    - ``failed == 0`` means ALL categories passed. The gating ``failed`` is the MAX of
      the aggregate's union count and the sum of the per-category lines' failed counts,
      so a mis-summarized aggregate that under-reports (``multi:2 ... failed=0`` while a
      per-category line shows ``failed=3``) can NEVER read as an all-passed run.
    - A MISSING aggregate with per-category lines PRESENT is a DEFINED FAULT (the
      multi-category run emitted category batches but was cut off before H1's summary,
      or the aggregate emit failed): ``present=False`` + ``aggregate_missing=True`` so
      the caller reds batch-incomplete. It NEVER silently falls back to a per-category
      line as an all-passed pass (the regression this guards: a truncated multi-category
      run reading green off one category's line).
    - An aggregate whose declared ``multi:<count>`` does NOT equal the number of
      per-category lines present is a DEFINED FAULT (SF2): the count is the number of
      categories the autorun ran (design M-A3 ~270), so exactly that many per-category
      BATCH_COMPLETE lines must be present. FEWER lines than the count means a category
      batch was cut off before its BATCH_COMPLETE; MORE lines than the count means an
      unexpected extra batch (the aggregate and the per-category stream disagree). BOTH
      red via STRICT EQUALITY (design M-A3: one per-category line per sequentially-run
      token, so the count IS the line count): ``present=False`` + ``category_count_
      mismatch=True``, never a silent pass off a mis-counted aggregate.
    """
    per_category = [bc for bc in batches if not _is_aggregate(bc)]
    if not is_multi_category_selector(selector):
        sel = (select_batch_complete(batches, selector, scene) if selector
               else (batches[0] if batches else None))
        return BatchCompleteSelection(
            present=sel is not None,
            failed=(sel.failed if sel else None),
            category=(sel.category if sel else None),
            scene=(sel.scene if sel else None),
            multi=False, aggregate_missing=False,
            per_category_count=len(per_category),
            category_count_mismatch=False, expected_category_count=None)

    aggregates = [bc for bc in batches if _is_aggregate(bc)]
    if len(aggregates) > 1:
        # Item 10: two multi:<count> aggregate lines -> a defined fault (the summary
        # emitted twice); never silently first-win one. present=False reds
        # batch-incomplete; the distinct duplicate_aggregate flag names the reason.
        return BatchCompleteSelection(
            present=False, failed=None, category=aggregates[0].category,
            scene=aggregates[0].scene, multi=True, aggregate_missing=False,
            per_category_count=len(per_category),
            category_count_mismatch=False,
            expected_category_count=aggregate_category_count(aggregates[0]),
            duplicate_aggregate=True)

    agg = select_aggregate_batch_complete(batches)
    if agg is not None:
        expected_n = aggregate_category_count(agg)  # the multi:<count> the regex parses
        if expected_n is not None and expected_n != len(per_category):
            # STRICT EQUALITY (SF2): the aggregate claims a category count the
            # per-category stream does not match -> a defined fault, never a silent
            # pass. present=False so the caller reds batch-incomplete (same treatment
            # as a missing aggregate); the distinct category_count_mismatch flag names
            # the reason.
            return BatchCompleteSelection(
                present=False, failed=None, category=agg.category, scene=agg.scene,
                multi=True, aggregate_missing=False,
                per_category_count=len(per_category),
                category_count_mismatch=True, expected_category_count=expected_n)
        # The gating failed is the UNION; take the larger of the aggregate's count and
        # the per-category sum so failed==0 can never hide a category that reported
        # failures (defends against a mis-summarized aggregate).
        per_cat_failed = sum(bc.failed for bc in per_category)
        effective_failed = max(agg.failed, per_cat_failed)
        return BatchCompleteSelection(
            present=True, failed=effective_failed, category=agg.category,
            scene=agg.scene, multi=True, aggregate_missing=False,
            per_category_count=len(per_category),
            category_count_mismatch=False, expected_category_count=expected_n)

    # Multi selector, no aggregate line. Per-category lines present -> a defined fault
    # (never a silent pass off a per-category line). No lines at all -> a plain
    # batch-absent (batch never started), same as the single-category empty case.
    return BatchCompleteSelection(
        present=False, failed=None, category=None, scene=None,
        multi=True, aggregate_missing=(len(per_category) > 0),
        per_category_count=len(per_category),
        category_count_mismatch=False, expected_category_count=None)


# The terminal RED token is the LAST token on the [Analyzer] header line and the
# SOLE gate source (baseline doc). Anchored at end-of-line so a save leaf that
# literally contains "RED=0" earlier in the header can never spoof the gate.
_RED_RE = re.compile(r"\bRED=(\d+)\s*$")


def parse_analysis_red_token(analysis_txt: str) -> Optional[int]:
    """Read the terminal ``RED=<0|1>`` gate token from a ``.analysis.txt`` body.

    Scans for the ``[Analyzer]`` header line and returns the int value of its
    trailing ``RED=`` token (anchored end-of-line, never an earlier literal).
    Returns None when the header or the trailing RED token is ABSENT -- an absent
    gate token must NEVER read as RED=0 (design edge 12: the most dangerous
    silent pass); the caller treats None as an analyzer TOOLING failure.
    """
    if not analysis_txt:
        return None
    for line in analysis_txt.splitlines():
        if not line.startswith("[Analyzer]"):
            continue
        m = _RED_RE.search(line.rstrip())
        if m:
            return int(m.group(1))
        return None
    return None


@dataclass(frozen=True)
class AnalysisFinding:
    rule_id: str
    level: str  # FAIL | WARN | STALE | INFO
    target: str
    baselined: bool


@dataclass(frozen=True)
class AnalysisJson:
    fail_non_baselined: int
    stale_non_baselined: int
    findings: Tuple[AnalysisFinding, ...]

    def non_baseline_fail_findings(self) -> List[AnalysisFinding]:
        """FAIL findings whose rule id is NOT a ``BASELINE-*`` meta-finding and
        that are not baselined -- the REAL Parsek-defect FAILs (design S2)."""
        return [
            f for f in self.findings
            if f.level == "FAIL" and not f.baselined
            and not (f.rule_id or "").startswith(BASELINE_RULE_PREFIX)
        ]

    def baseline_fail_findings(self) -> List[AnalysisFinding]:
        """Non-baselined FAIL findings that ARE ``BASELINE-*`` meta-findings
        (fixture-authoring FAILs, e.g. BASELINE-FORBIDDEN, design S2)."""
        return [
            f for f in self.findings
            if f.level == "FAIL" and not f.baselined
            and (f.rule_id or "").startswith(BASELINE_RULE_PREFIX)
        ]


def parse_analysis_json(analysis_json: str) -> Optional[AnalysisJson]:
    """Read the fail/stale split + findings list from a ``.analysis.json`` body.

    The FAIL-vs-STALE SUBCLASSIFICATION of a RED=1 comes from HERE, never from
    the txt header (design S1): ``counts.failNonBaselined`` and
    ``counts.staleNonBaselined`` are JSON-only fields. Also lifts the findings
    list (rule id + level + baselined) so the harness can apply the BASELINE-*
    precedence (S2). Accepts a JSON string or an already-parsed dict. Returns
    None on a parse failure (the caller treats it as an analyzer tooling error).
    """
    if analysis_json is None:
        return None
    if isinstance(analysis_json, dict):
        obj = analysis_json
    else:
        try:
            obj = json.loads(analysis_json)
        except (ValueError, TypeError):
            return None
    if not isinstance(obj, dict):
        return None
    counts = obj.get("counts", {}) or {}
    findings: List[AnalysisFinding] = []
    for f in obj.get("findings", []) or []:
        if not isinstance(f, dict):
            continue
        findings.append(AnalysisFinding(
            rule_id=str(f.get("ruleId", "")),
            level=str(f.get("level", "")),
            target=str(f.get("target", "")),
            baselined=bool(f.get("baselined", False)),
        ))
    try:
        fnb = int(counts.get("failNonBaselined", 0))
        snb = int(counts.get("staleNonBaselined", 0))
    except (TypeError, ValueError):
        return None
    return AnalysisJson(fnb, snb, tuple(findings))


# A results-file FAILURE row is "    FAIL  <name> (...)". The ALL-RESULTS block
# uses the padded status "FAILED" -- a \bFAIL\b word boundary matches "FAIL" but
# NOT "FAILED" (E is a word char, so no boundary after the L), so this counts
# each failing row from the FAILURES block once and never double-counts the
# per-scene status rows.
_RESULTS_FAIL_RE = re.compile(r"^\s*FAIL\b")


def parse_results_failures(results_txt: str) -> int:
    """Count FAIL rows in a ``parsek-test-results.txt`` body (design verifier 5).

    Matches the ``FAILURES (grouped by scene)`` block's ``FAIL  <name>`` rows
    and deliberately does NOT match the ALL-RESULTS block's ``FAILED`` status
    rows (the ``\\bFAIL\\b`` boundary excludes ``FAILED``). Cross-checked by the
    caller against the BATCH_COMPLETE ``failed=`` count; a disagreement is itself
    a PARSEK-FAIL (the runner's own accounting is inconsistent).
    """
    if not results_txt:
        return 0
    count = 0
    for line in results_txt.splitlines():
        if _RESULTS_FAIL_RE.match(line):
            count += 1
    return count


def _parse_response_line(line: str) -> Optional[Dict[str, str]]:
    """Parse one seam response line ``id=.. cmd=.. verdict=.. seq=.. ...`` into a
    key->value dict. Returns None if it lacks the reserved id/cmd/verdict keys
    (a non-terminal / malformed line). Values stay percent-encoded (raw); the
    harness only decides on the un-encoded id/cmd/verdict tokens."""
    if not line or "=" not in line:
        return None
    fields: Dict[str, str] = {}
    for tok in line.split():
        if "=" not in tok:
            continue
        k, _, v = tok.partition("=")
        if k and k not in fields:
            fields[k] = v
    if "id" not in fields or "cmd" not in fields or "verdict" not in fields:
        return None
    return fields


# ---------------------------------------------------------------------------
# Response-stream evaluation (design "Driving the seam" / evaluate_response_stream).
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class StepOutcome:
    step_id: str
    cmd: str
    expect: str
    verdict: Optional[str]  # observed; None when no response line for this id
    found: bool
    met: bool
    # The seam response line's `msg=` token (percent-encoded, as it appears on the
    # wire) or "" when absent. Carries the M-C1 verb-refusal reason prefix
    # (refly-gate / unknown-rp / no-live-dialog / career-not-ready / backward-jump ...)
    # the driver-stage subkind mapping reads (classify_seam_refusal_subkind).
    msg: str = ""


@dataclass(frozen=True)
class ResponseEvaluation:
    steps: Tuple[StepOutcome, ...]
    all_expected_met: bool
    first_unmet: Optional[StepOutcome]
    duplicate_ids: Tuple[str, ...]


def evaluate_response_stream(
    response_lines: Sequence[str], expected_steps: Sequence[Dict]
) -> ResponseEvaluation:
    """Match observed seam verdicts against each expected step's ``expect``.

    ``response_lines`` is the raw tail of ``parsek-test-responses.txt``;
    ``expected_steps`` is the driver's ordered steps, each a dict carrying
    ``id`` (the harness-assigned monotonic id, e.g. "0001"), ``cmd`` and
    ``expect``. Two contract points (design):
      - DEDUPE by id keeping the FIRST terminal line: an M-A2 crash-recovery
        rewrite re-emits a byte-equivalent terminal line, and first-wins matches
        the seam's own orchestrator contract, so a rewrite is NOT a second
        outcome (design edge 20).
      - A step whose observed verdict != its ``expect`` (or that has no response
        line at all) is UNMET and marks the driver stage failed at that step.
    """
    observed: Dict[str, str] = {}
    observed_msg: Dict[str, str] = {}
    duplicate_ids: List[str] = []
    for line in response_lines:
        parsed = _parse_response_line(line)
        if parsed is None:
            continue
        rid = parsed["id"]
        if rid in observed:
            duplicate_ids.append(rid)  # first-wins; the rewrite is ignored
            continue
        observed[rid] = parsed["verdict"]
        observed_msg[rid] = parsed.get("msg", "")

    outcomes: List[StepOutcome] = []
    first_unmet: Optional[StepOutcome] = None
    for step in expected_steps:
        sid = str(step.get("id"))
        cmd = str(step.get("cmd", ""))
        expect = str(step.get("expect", "OK"))
        verdict = observed.get(sid)
        found = verdict is not None
        met = found and verdict == expect
        outcome = StepOutcome(sid, cmd, expect, verdict, found, met,
                              msg=observed_msg.get(sid, ""))
        outcomes.append(outcome)
        if not met and first_unmet is None:
            first_unmet = outcome

    all_met = all(o.met for o in outcomes) if outcomes else False
    # dedupe preserving order for a stable, testable list
    seen: set = set()
    uniq_dups: List[str] = []
    for d in duplicate_ids:
        if d not in seen:
            seen.add(d)
            uniq_dups.append(d)
    return ResponseEvaluation(tuple(outcomes), all_met, first_unmet, tuple(uniq_dups))


# ---------------------------------------------------------------------------
# Spec validation (design Spec-validation rules / validate_spec). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class SpecValidation:
    ok: bool
    errors: Tuple[str, ...]
    warnings: Tuple[str, ...]


_ID_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")

# runSaveName (the saveTemplate leaf) becomes a filesystem directory name the
# shell rmtree's + copytree's, so it gets RecordingPaths-style ID discipline:
# alphanumerics, dash, underscore, space ONLY. Dots are deliberately EXCLUDED so
# "."/".." (and any dotted traversal token) cannot pass; the "+" rejects "".
_SAVE_NAME_RE = re.compile(r"^[A-Za-z0-9 _-]+$")


def _leaf_of(path: str) -> str:
    """Basename of a forward/back-slash path (the saveTemplate leaf = runSaveName)."""
    norm = (path or "").replace("\\", "/").rstrip("/")
    return norm.rsplit("/", 1)[-1] if norm else ""


def _registry_values(registry: Dict, dimension: str) -> Optional[List[str]]:
    table = registry.get(dimension)
    if not isinstance(table, dict):
        return None
    vals = table.get("values")
    if not isinstance(vals, list):
        return None
    return [str(v) for v in vals]


# A mission ref (design [driver].mission) becomes a `harness/missions/<mission>.py`
# filename leaf, so it gets filename discipline: alphanumerics, dash, underscore
# ONLY. Dots are EXCLUDED so "."/".." (and any dotted traversal token) cannot pass;
# the "+" rejects "". Mirrors _SAVE_NAME_RE's stance for the same reason.
_MISSION_RE = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_-]*$")


def _check_param_type(name: str, value, decl: Dict) -> List[str]:
    """Type/range check one declared missionParam value against its schema entry
    (pure). ``decl`` is the mission schema's per-param table
    ``{"type": "<t>", "min": <num?>, "max": <num?>}``; only the declared facets are
    enforced. bool is rejected where a number is declared (Python ``bool`` is an
    ``int`` subclass, so an unguarded numeric check would silently accept True/False
    as 1/0)."""
    errs: List[str] = []
    ptype = decl.get("type")
    lo, hi = decl.get("min"), decl.get("max")
    if ptype in ("float", "int", "number"):
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            errs.append("missionParams.%s: expected %s, got %r" % (name, ptype, value))
            return errs
        if ptype == "int" and not isinstance(value, int):
            errs.append("missionParams.%s: expected int, got %r" % (name, value))
        if isinstance(lo, (int, float)) and value < lo:
            errs.append("missionParams.%s: %r < min %r" % (name, value, lo))
        if isinstance(hi, (int, float)) and value > hi:
            errs.append("missionParams.%s: %r > max %r" % (name, value, hi))
    elif ptype == "window":
        if not (isinstance(value, dict)
                and isinstance(value.get("min"), (int, float))
                and isinstance(value.get("max"), (int, float))):
            errs.append("missionParams.%s: expected a window {min,max}, got %r" % (name, value))
        # min <= max is enforced structurally by _validate_mission_params below.
    elif ptype == "list":
        if not isinstance(value, (list, tuple)):
            errs.append("missionParams.%s: expected a list, got %r" % (name, value))
    elif ptype == "string":
        if not isinstance(value, str):
            errs.append("missionParams.%s: expected a string, got %r" % (name, value))
    elif ptype == "bool":
        if not isinstance(value, bool):
            errs.append("missionParams.%s: expected a bool, got %r" % (name, value))
    return errs


def _validate_mission_params(params: Dict, schema: Optional[Dict]) -> List[str]:
    """Validate a ``missionParams`` block (design "Spec-validation rules").

    PURE / SHELL split: the mission's declared param schema lives in
    ``harness/missions/<mission>.schema.toml`` and is parsed SHELL-SIDE (I/O), then
    injected here as ``schema``. When ``schema`` is None ONLY the schema-INDEPENDENT
    structural check runs -- every window-shaped value (a table carrying ``min`` and
    ``max``) must have ``min <= max``. When a schema IS provided, its ``params``
    declaration additionally drives required-key presence and per-value type/range
    checks. A missing required param or a window with ``min > max`` -> reject.

    Declared-schema shape (parsed shell-side):
        {"params": {"<name>": {"required": bool, "type": "<t>",
                                "min": <num?>, "max": <num?>}}}
    where ``<t>`` in {float, int, number, window, list, string, bool}; a ``window``
    param value is a table ``{"min": <num>, "max": <num>}``.
    """
    errs: List[str] = []
    params = params or {}
    # Structural (schema-independent): any window-shaped value must be min <= max.
    for pname, val in params.items():
        if isinstance(val, dict) and "min" in val and "max" in val:
            lo, hi = val.get("min"), val.get("max")
            if isinstance(lo, (int, float)) and isinstance(hi, (int, float)) and lo > hi:
                errs.append("missionParams.%s: window min %r > max %r (ill-formed)" % (pname, lo, hi))
    if schema is None:
        return errs
    declared = (schema.get("params", {}) or {}) if isinstance(schema, dict) else {}
    for pname, decl in declared.items():
        decl = decl or {}
        present = pname in params
        if bool(decl.get("required", False)) and not present:
            errs.append("missionParams.%s: required param missing" % (pname,))
            continue
        if present:
            errs.extend(_check_param_type(pname, params[pname], decl))
    return errs


def validate_spec(spec: Dict, registry: Dict, bug_ids: Optional[Sequence[str]] = None,
                  mission_schemas: Optional[Dict] = None) -> SpecValidation:
    """Validate a parsed scenario spec against the design rules + the registry.

    Returns every failing rule (not just the first) so a spec author sees the
    whole problem set. Hard errors fail the spec (recorded INVALID-SPEC, KSP
    never launched); an unresolvable ``expectedFail.bugId`` is a WARNING only
    (a scenario may land just ahead of its todo-doc row, design).
    ``bug_ids`` is the injected set of resolvable todo-doc bug ids (I/O-free).

    M-B1 autopilot (design "Spec-validation rules for kind = autopilot"): a
    ``kind = "autopilot"`` spec is a SUPERSET of the seam driver -- the seam-step
    rules above still apply to its ``cmd``-kind steps, and it ADDS a mission ref,
    a ``missionParams`` block, and exactly one ``mission``-kind handoff step.
    ``mission_schemas`` is the injected registry of parsed
    ``harness/missions/<mission>.schema.toml`` bodies (mission name -> schema
    dict); it is the PURE half of the mission-ref / param check. SHELL-SIDE (I/O,
    NOT here): confirming the mission ``.py`` resolves on disk and reading the
    schema toml. When ``mission_schemas`` is None the mission-existence /
    declared-schema content checks are DEFERRED to the shell (only the
    structural mission-step / window checks run); when provided, an unknown
    mission and a param that violates its declared schema reject.
    """
    errors: List[str] = []
    warnings: List[str] = []

    schema = spec.get("schema")
    if schema != SCHEMA_VERSION:
        errors.append("schema: expected %d got %r" % (SCHEMA_VERSION, schema))

    sid = spec.get("id")
    if not isinstance(sid, str) or not sid or not _ID_RE.match(sid):
        errors.append("id: missing or not filename-safe: %r" % (sid,))

    tier = spec.get("tier")
    if tier not in TIERS:
        errors.append("tier: %r not in %s" % (tier, list(TIERS)))

    profile = spec.get("instanceProfile")
    if profile not in INSTANCE_PROFILES:
        errors.append("instanceProfile: %r not in %s" % (profile, list(INSTANCE_PROFILES)))

    fixture = spec.get("fixture", {}) or {}
    save_template = fixture.get("saveTemplate", "")
    run_save_name = _leaf_of(save_template)
    # The saveTemplate leaf IS runSaveName, staged as a directory the shell
    # rmtree's + copytree's. Reject anything that is not filename-safe (empty,
    # ".", "..", or a name outside [alnum dash underscore space]) so a spec can
    # never point staging at "saves/.." (an rmtree escape); the shell keeps a
    # belt-and-braces realpath-containment assert too (S1). Also reject an ABSOLUTE
    # saveTemplate: it is joined under harness/ as a relative fixture path, and an
    # absolute value would make the copytree source arbitrary.
    _tmpl_norm = (save_template or "").replace("\\", "/")
    _is_absoluteish = (
        _os.path.isabs(save_template)
        or _tmpl_norm.startswith("/")                        # POSIX-root / UNC-ish
        or (len(_tmpl_norm) >= 2 and _tmpl_norm[1] == ":"))  # drive-letter (C:/...)
    if _is_absoluteish:
        errors.append(
            "fixture.saveTemplate: %r must be a relative fixture path under harness/ "
            "(absolute path rejected)" % (save_template,))
    if not run_save_name or run_save_name in (".", "..") or not _SAVE_NAME_RE.match(run_save_name):
        errors.append(
            "fixture.saveTemplate: runSaveName %r not filename-safe "
            "(alphanumerics, dash, underscore, space only)" % (run_save_name,))
    inj = fixture.get("injectedRecordings")
    if inj not in INJECTED_RECORDINGS:
        errors.append("fixture.injectedRecordings: %r not in %s" % (inj, list(INJECTED_RECORDINGS)))

    driver = spec.get("driver", {}) or {}
    kind = driver.get("kind")
    if kind not in DRIVER_KINDS:
        errors.append("driver.kind: %r must be 'seam' or 'autopilot'" % (kind,))
    is_autopilot = kind == "autopilot"

    steps = driver.get("steps", []) or []
    autorun = driver.get("autorun")

    # First step must be a LoadGame boot handshake whose save arg is ${runSave}
    # or a literal equal to runSaveName (S3), so the loaded save cannot drift
    # from the staged save.
    if not steps:
        errors.append("driver.steps: empty; first step must be LoadGame")
    else:
        first = steps[0] or {}
        if first.get("cmd") != "LoadGame":
            errors.append("driver.steps[0]: must be LoadGame, got %r" % (first.get("cmd"),))
        else:
            save_arg = (first.get("args", {}) or {}).get("save")
            if save_arg not in (RUN_SAVE_TOKEN, run_save_name):
                errors.append(
                    "driver.steps[0] LoadGame save=%r must be '%s' or runSaveName %r"
                    % (save_arg, RUN_SAVE_TOKEN, run_save_name))

    run_tests_steps = 0
    mission_step_indices: List[int] = []
    first_loadgame_index: Optional[int] = None
    for i, step in enumerate(steps):
        step = step or {}
        # A mission-kind step (design M-B1) is a HARNESS-SIDE handoff, NOT a seam
        # command: it writes nothing to the channel, so it is EXEMPT from the
        # seam-verb / reserved-verb / seam-expect checks (which apply only to
        # cmd-kind steps). It carries its own fixed expect (MISSION-OK) and an
        # optional positive budget bounding the mission subprocess wall-clock.
        if step.get("phase") == "mission":
            mission_step_indices.append(i)
            m_expect = step.get("expect", MISSION_STEP_EXPECT)
            if m_expect != MISSION_STEP_EXPECT:
                errors.append(
                    "driver.steps[%d].expect: mission step must be %r, got %r"
                    % (i, MISSION_STEP_EXPECT, m_expect))
            m_budget = step.get("budget")
            if m_budget is not None and (not isinstance(m_budget, (int, float)) or m_budget <= 0):
                errors.append(
                    "driver.steps[%d].budget: mission step budget %r must be > 0" % (i, m_budget))
            continue
        cmd = step.get("cmd")
        if cmd == "LoadGame" and first_loadgame_index is None:
            first_loadgame_index = i
        if cmd in RESERVED_SEAM_VERBS:
            errors.append("driver.steps[%d].cmd: %r is RESERVED, not v1-drivable" % (i, cmd))
        elif cmd not in IMPLEMENTED_SEAM_VERBS:
            errors.append("driver.steps[%d].cmd: %r is not a known seam verb" % (i, cmd))
        expect = step.get("expect", "OK")
        if expect not in SEAM_EXPECT_VERDICTS:
            errors.append(
                "driver.steps[%d].expect: %r not in %s (INTERRUPTED unreachable in v1)"
                % (i, expect, list(SEAM_EXPECT_VERDICTS)))
        budget = step.get("budget")
        if cmd == "RunTests":
            run_tests_steps += 1
        if budget is not None and cmd in DEFERRED_SEAM_VERBS:
            if not isinstance(budget, (int, float)) or budget > MAX_DEFERRED_STEP_BUDGET_SECONDS:
                errors.append(
                    "driver.steps[%d].budget: %r must be <= %ds for a deferred %s (S8)"
                    % (i, budget, MAX_DEFERRED_STEP_BUDGET_SECONDS, cmd))

    # Exactly one BATCH owner: a RunTests step XOR an [driver.autorun] block,
    # never both; never neither when logContracts.required names BATCH_COMPLETE.
    autorun_has_tests = bool(autorun and autorun.get("tests"))
    expectations = spec.get("expectations", {}) or {}
    log_contracts = expectations.get("logContracts", {}) or {}
    required_patterns = log_contracts.get("required", []) or []
    requires_batch = any("BATCH_COMPLETE" in str(p) for p in required_patterns)
    batch_owners = (1 if run_tests_steps > 0 else 0) + (1 if autorun_has_tests else 0)
    if batch_owners > 1:
        errors.append("BATCH owner: both a RunTests step and [driver.autorun] declared (exactly one allowed)")
    if batch_owners == 0 and requires_batch:
        errors.append("BATCH owner: none declared but logContracts.required names BATCH_COMPLETE")

    # Exactly one QUIT owner: a FlushAndQuit step XOR autorun.exit = true (N3).
    has_flush = any((s or {}).get("cmd") == "FlushAndQuit" for s in steps)
    autorun_exit = bool(autorun and autorun.get("exit"))
    quit_owners = (1 if has_flush else 0) + (1 if autorun_exit else 0)
    if quit_owners > 1:
        errors.append("QUIT owner: both FlushAndQuit and autorun.exit declared (exactly one allowed)")
    if quit_owners == 0:
        errors.append("QUIT owner: neither FlushAndQuit nor autorun.exit declared")

    # --- M-B1 autopilot driver rules (pure half; design "Spec-validation rules
    # for kind = autopilot"). The seam-kind rules above still apply to the seam
    # steps. A mission-kind step is exempt from the seam-verb / batch-owner /
    # quit-owner checks (it is neither a seam verb nor a BATCH/QUIT owner).
    mission = driver.get("mission")
    if is_autopilot:
        # EXACTLY ONE mission-kind step marks the handoff.
        if len(mission_step_indices) != 1:
            errors.append(
                "driver: kind 'autopilot' requires exactly one mission-kind step (found %d)"
                % (len(mission_step_indices),))
        # Mission ref present + filename-safe (a .py leaf; dots / traversal rejected).
        if not isinstance(mission, str) or not mission or not _MISSION_RE.match(mission):
            errors.append("driver.mission: missing or not filename-safe: %r" % (mission,))
        elif mission_schemas is not None and mission not in mission_schemas:
            # Unknown mission (the injected registry has no declared schema for it):
            # a boot would be wasted launching KSP for a mission that cannot run.
            # When mission_schemas is None this existence check is deferred to the
            # shell (the .py resolution is I/O).
            errors.append("driver.mission: unknown mission %r (no declared schema)" % (mission,))
        # Each mission step must FOLLOW a LoadGame (the FLIGHT handoff owner): the
        # mission cannot connect before KSP is in FLIGHT, so a mission step at index
        # 0 or before the first LoadGame is rejected.
        for mi in mission_step_indices:
            if first_loadgame_index is None or mi <= first_loadgame_index:
                errors.append(
                    "driver.steps[%d]: mission step must follow a LoadGame step "
                    "(no preceding LoadGame)" % (mi,))
        # missionParams: windows well-formed (min <= max) is structural and always
        # checked; required-keys + type/range are checked against the declared
        # schema only when it is injected (see _validate_mission_params).
        mission_schema = (mission_schemas or {}).get(mission) if isinstance(mission, str) else None
        errors.extend(_validate_mission_params(driver.get("missionParams", {}) or {}, mission_schema))
    else:
        # A mission-kind step only belongs under an autopilot driver.
        for mi in mission_step_indices:
            errors.append(
                "driver.steps[%d]: mission-kind step requires driver.kind 'autopilot'" % (mi,))

    # M-B2 ledger-oracle spec surface (design ~226): a malformed
    # [expectations.ledger] block must never launch KSP. Structural only; the
    # per-entry manifest validation runs at run time (oracle.parse_manifest_entries).
    if "ledger" in expectations:
        errors.extend(validate_ledger_expectations(expectations.get("ledger")))

    # An [expectations.ledger] block cannot be modeled across an in-run rewind or a
    # merge-dialog answer: InvokeRewind rewrites the career pools (funds/science/rep)
    # from a quicksave the seed + manifest contract cannot reconstruct, and
    # AnswerMergeDialog drives the merge that commits/discards those rewound pools. The
    # oracle for a rewound/merged career is DEFERRED to L4, so pairing the two is a
    # hard spec error (never launch KSP for an unassertable run). TimeJump + ledger
    # stays allowed (design-blessed: a forward jump keeps the seed + manifest sum
    # valid). M-A5 integration item 1.
    if "ledger" in expectations:
        for i, step in enumerate(steps):
            scmd = (step or {}).get("cmd")
            if scmd in ("InvokeRewind", "AnswerMergeDialog"):
                errors.append(
                    "driver.steps[%d].cmd: %r cannot pair with [expectations.ledger] -- a "
                    "rewind/merge rewrites the career pools the seed+manifest contract cannot "
                    "model; the rewound-career ledger oracle is DEFERRED to L4" % (i, scmd))

    # Dimensions covered: every key + value present in the registry.
    dims = spec.get("dimensionsCovered", {}) or {}
    for dim, values in dims.items():
        reg_vals = _registry_values(registry, dim)
        if reg_vals is None:
            errors.append("dimensionsCovered: unknown dimension %r" % (dim,))
            continue
        for v in values or []:
            if v not in reg_vals:
                errors.append("dimensionsCovered.%s: unknown value %r" % (dim, v))

    # Runtime budget + retry policy.
    runtime = spec.get("runtime", {}) or {}
    budget_seconds = runtime.get("budgetSeconds")
    if not isinstance(budget_seconds, (int, float)) or budget_seconds <= 0:
        errors.append("runtime.budgetSeconds: %r must be > 0" % (budget_seconds,))

    retry = spec.get("retry", {}) or {}
    if retry.get("policy") not in RETRY_POLICIES:
        errors.append("retry.policy: %r not in %s" % (retry.get("policy"), list(RETRY_POLICIES)))

    # Expected-fail bug id: WARN (not hard-fail) if unresolvable.
    exp_fail = spec.get("expectedFail", {}) or {}
    bug_id = exp_fail.get("bugId", "") or ""
    if bug_id and bug_ids is not None and bug_id not in bug_ids:
        warnings.append("expectedFail.bugId: %r not resolvable in the todo doc (dangling key)" % (bug_id,))
    # Optional expectedFail.subkind narrows the signature match to one PARSEK-FAIL
    # class (S2); an unknown subkind is a hard error (it could never match).
    ef_subkind = exp_fail.get("subkind", "") or ""
    if ef_subkind and ef_subkind not in PARSEK_FAIL_SUBKINDS:
        errors.append("expectedFail.subkind: %r not in %s"
                      % (ef_subkind, list(PARSEK_FAIL_SUBKINDS)))

    return SpecValidation(len(errors) == 0, tuple(errors), tuple(warnings))


# ---------------------------------------------------------------------------
# Scenario selection (design Scenario selection / select_scenarios). Pure.
# ---------------------------------------------------------------------------

# A cadence maps to a tier set (design section 10). per-pr is analyzer-on-fixtures
# only (no KSP), but at the SELECTION layer it resolves to the perpr tier specs.
CADENCE_TIERS: Dict[str, Tuple[str, ...]] = {
    "per-pr": ("perpr",),
    "daily": ("daily",),
    "nightly": ("daily", "nightly"),
    "weekly": ("perpr", "daily", "nightly", "weekly"),
}


def select_scenarios(specs: Sequence[Dict], expr: str) -> List[Dict]:
    """Select scenarios by ``--id X`` / ``--tier T`` / ``--tag G`` / ``--cadence C``.

    ``expr`` is a "<kind> <value>" string. Selection is deterministic (input
    order preserved) so the exact set a cadence resolves to is unit-testable
    (design: a cadence must never silently drop or add scenarios). An unknown
    kind or an unmatched value yields an empty list.
    """
    parts = (expr or "").strip().split(None, 1)
    if len(parts) != 2:
        return []
    kind, value = parts[0], parts[1].strip()

    def _matches(spec: Dict) -> bool:
        if kind == "--id":
            return spec.get("id") == value
        if kind == "--tier":
            return spec.get("tier") == value
        if kind == "--tag":
            return value in (spec.get("tags", []) or [])
        if kind == "--cadence":
            tiers = CADENCE_TIERS.get(value)
            return tiers is not None and spec.get("tier") in tiers
        return False

    return [s for s in specs if _matches(s)]


# ---------------------------------------------------------------------------
# Instance admission (design Instance admission, reusing the M-A6 provlib pure
# functions so the harness and the provisioner diff the SAME projection).
# ---------------------------------------------------------------------------

import os as _os  # noqa: E402  (kept local to the admission/provlib coupling)
import sys as _sys  # noqa: E402

_PROVISION_DIR = _os.path.abspath(
    _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), "..", "provision"))
if _PROVISION_DIR not in _sys.path:
    _sys.path.insert(0, _PROVISION_DIR)
import provlib  # noqa: E402  (the M-A6 pure sibling; admission reuse, design)


def build_expected_admission(
    profile_name: str,
    ksp_version: str,
    components: Dict,
    settings_deltas: Dict,
    dev_sourced_mods: Dict,
) -> Dict:
    """Assemble the admission-relevant projection the harness expects for a run.

    This is the PURE half of the S11 expected-manifest construction recipe: the
    harness does NOT hand-author pins; run.py computes the hashed ``components``
    (incl. the CURRENT build's Parsek.dll hash), the applied ``settings_deltas``,
    and the ``dev_sourced_mods`` hashes exactly the way the provisioner stamps
    the on-disk manifest (that hashing is I/O, done in the shell), then feeds
    them here. The result is shaped so ``provlib.project_admission`` /
    ``provlib.compare_manifest`` diff it against the on-disk manifest field for
    field. Consequence (design policy): a Parsek rebuild changes the DLL hash, so
    the instance must be re-provisioned before the harness runs or admission
    correctly reds the run as drifted.
    """
    return {
        "profile": profile_name,
        "kspVersion": ksp_version,
        "components": components,
        "settingsDeltasApplied": settings_deltas,
        "devSourcedMods": dev_sourced_mods,
    }


@dataclass(frozen=True)
class AdmissionDecision:
    admitted: bool
    subkind: str  # "" | "manifest-missing" | "provision-incomplete" | "drift"
    diff: Tuple  # provlib.ManifestDiff tuple


def admit_instance(
    expected: Dict,
    actual_manifest: Optional[Dict],
    incomplete_marker: bool = False,
) -> AdmissionDecision:
    """Admit (or refuse) an instance before any KSP launch (design edge 6).

    A missing manifest, a ``.provision-incomplete`` marker beside it, or a
    NONEMPTY field-level diff from ``provlib.compare_manifest`` means the instance
    is not the one the scenario assumes -> refuse with INVALID(admission), NO
    launch. Empty diff (and no marker/missing) -> admit. Classifying this INVALID
    (not PARSEK-FAIL) keeps environment drift out of the Parsek-defect bucket.
    """
    if actual_manifest is None:
        return AdmissionDecision(False, "manifest-missing", tuple())
    if incomplete_marker:
        return AdmissionDecision(False, "provision-incomplete", tuple())
    diff = tuple(provlib.compare_manifest(expected, actual_manifest))
    if diff:
        return AdmissionDecision(False, "drift", diff)
    return AdmissionDecision(True, "", tuple())


# ---------------------------------------------------------------------------
# Log-validation profile selection (design verifier 4 / B1 / S13). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class LogValidateProfile:
    suppress_recording_rules: bool  # B1: count.max == 0 no-recording scenario
    killed_run_mode: bool           # S13: a KILLED attempt truncates the tail
    suppressed_rules: Tuple[str, ...]
    mandatory_rules: Tuple[str, ...]


def spec_expects_live_recording(spec: Dict) -> bool:
    """True when the RUN itself is expected to write recording start/stop log
    lines: the driver carries a StartRecording step, or pins
    autoRecordOnLaunch=true via SetSetting. Injection-seeded scenarios
    (recordings present in the SAVE but never recorded live) return False -
    the first live S1.4 run proved count.max>0 is the WRONG suppression key:
    it red-flagged REC-001/REC-003 on a run that legitimately never records."""
    driver = spec.get("driver", {}) or {}
    for step in driver.get("steps", []) or []:
        cmd = (step.get("cmd") or "")
        if cmd == "StartRecording":
            return True
        if cmd == "SetSetting":
            name = (step.get("args", {}) or {}).get("name", "")
            value = str((step.get("args", {}) or {}).get("value", "")).lower()
            if name == "autoRecordOnLaunch" and value == "true":
                return True
    return False


def select_logvalidate_profile(live_recording_expected: bool, killed: bool) -> LogValidateProfile:
    """Select the two orthogonal log-validation suppression profiles by run shape.

    - Recording-rules suppression (B1, REVISED after the first live run): IFF
      the run does NOT expect live recording (``spec_expects_live_recording``
      is False - no StartRecording step, no autoRecordOnLaunch=true pin) the
      harness suppresses exactly REC-001/REC-003. The original key
      (``recordings.count.max == 0``) mis-fired on injection-seeded scenarios
      whose SAVE holds recordings the run never records live. When live
      recording IS expected the REC rules stay mandatory (a dropped recording
      still reds).
    - Killed-run mode (S13): a KILLED attempt adds ``-KilledRun``, suppressing the
      marker-pairing rules SES-000/SES-001/REC-001/REC-003 (a kill legitimately
      truncates the tail) while FMT/WRN stay mandatory.
    The two are independent (a run can be in one, both, or neither).
    """
    suppress_rec = not live_recording_expected
    suppressed: set = set()
    if suppress_rec:
        suppressed.update(LOGVALIDATE_RECORDING_RULES)
    if killed:
        suppressed.update(LOGVALIDATE_MARKER_PAIRING_RULES)
    all_rules = set(LOGVALIDATE_MARKER_PAIRING_RULES) | set(LOGVALIDATE_ALWAYS_MANDATORY)
    mandatory = sorted(all_rules - suppressed)
    return LogValidateProfile(
        suppress_recording_rules=suppress_rec,
        killed_run_mode=bool(killed),
        suppressed_rules=tuple(sorted(suppressed)),
        mandatory_rules=tuple(mandatory),
    )


# ---------------------------------------------------------------------------
# Budget arithmetic (design Budget enforcement / S8). Pure.
# ---------------------------------------------------------------------------


def required_step_wait(seam_deferral_budget: float) -> float:
    """The harness step-wait a deferred step REQUIRES: the seam's deferral budget
    plus a 60s margin, so a genuine seam TIMEOUT is OBSERVED (a driver-INVALID,
    distinct from a hang) rather than pre-empted by a harness kill (S8)."""
    return float(seam_deferral_budget) + STEP_WAIT_MARGIN_SECONDS


def step_wait_ok(harness_step_wait: float, seam_deferral_budget: float) -> bool:
    """True iff ``harness_step_wait >= seam_deferral_budget + 60s`` (S8): the
    harness always gives the seam a full deferral window plus slack."""
    return float(harness_step_wait) >= required_step_wait(seam_deferral_budget)


def dispatch_deferral_budget(verb: str,
                             scenario_budget_seconds: Optional[float] = None) -> float:
    """The seam-side DISPATCH deferral budget (seconds) for ``verb``, mirroring the C#
    DeferralBudget.BudgetSeconds table (M-A5 integration item 3). RunTests defers to
    the scenario's declared runtime budget when supplied (else the two-phase branch's
    600s fallback governs it); every other verb reads DISPATCH_DEFERRAL_BUDGET_SECONDS,
    falling back to the 60s default. This is what a NON-two-phase verb parks at the seam
    head for before it self-emits a TIMEOUT terminal, so the harness step-wait for such
    a verb must out-wait it plus the standard margin."""
    if verb == "RunTests" and scenario_budget_seconds is not None:
        return float(scenario_budget_seconds)
    return DISPATCH_DEFERRAL_BUDGET_SECONDS.get(verb, DISPATCH_DEFERRAL_DEFAULT_SECONDS)


def required_dispatch_step_wait(verb: str,
                                scenario_budget_seconds: Optional[float] = None) -> float:
    """The harness step-wait a NON-two-phase seam verb REQUIRES so the seam's own
    dispatch-deferral TIMEOUT (a retryable driver-INVALID) is OBSERVED rather than
    pre-empted by a harness KILL: the verb's dispatch deferral budget plus the same 60s
    margin the two-phase ``required_step_wait`` uses (M-A5 integration item 3)."""
    return dispatch_deferral_budget(verb, scenario_budget_seconds) + STEP_WAIT_MARGIN_SECONDS


# ---------------------------------------------------------------------------
# Expectations evaluation (design verifier 7 / evaluate_expectations). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ExpectationResult:
    status: str  # "PASS" | "FAIL"
    mismatches: Tuple[str, ...]
    reserved: Tuple[str, ...]


# M-B2 (design ~495): on activation ``world`` LEAVES this tuple -- the ledger-oracle
# verifier (chain slot 8) becomes its SOLE owner (vessel resource totals), so slot 7
# STOPS recording it as reserved and there is exactly ONE owner (no double-count).
# ``ledger`` was never reserved here (it is a tolerated-unknown block slot 7 ignores).
RESERVED_EXPECTATION_BLOCKS: Tuple[str, ...] = ("route", "rewind", "loop")


def evaluate_expectations(
    expectations: Dict, recording_count: Optional[int], log_text: str
) -> ExpectationResult:
    """Evaluate the v1-EVALUATED expectation blocks with tolerances.

    v1 evaluates: ``recordings.count`` (min/max window) and
    ``logContracts.required`` / ``logContracts.forbidden`` (LITERAL KSP.log line
    regex patterns applied with ``re.search`` over the log body). A mismatch ->
    FAIL (the caller reds PARSEK-FAIL expectation). The route/rewind/loop blocks
    are RESERVED: parsed + recorded SKIPPED until their verifiers land (M-C2), so a
    scenario written now needs no format break then. ``world`` is NO LONGER reserved
    here (M-B2 gave verifier 8 sole ownership, design ~495) and ``ledger`` is a
    tolerated-unknown block this evaluator ignores (verifier 8 owns it).
    """
    expectations = expectations or {}
    mismatches: List[str] = []

    recordings = expectations.get("recordings", {}) or {}
    count_spec = recordings.get("count")
    if isinstance(count_spec, dict) and recording_count is not None:
        cmin = count_spec.get("min", 0)
        cmax = count_spec.get("max")
        if isinstance(cmin, (int, float)) and recording_count < cmin:
            mismatches.append("recordings.count %d < min %s" % (recording_count, cmin))
        if isinstance(cmax, (int, float)) and recording_count > cmax:
            mismatches.append("recordings.count %d > max %s" % (recording_count, cmax))

    log_contracts = expectations.get("logContracts", {}) or {}
    text = log_text or ""
    for pat in log_contracts.get("required", []) or []:
        try:
            if re.search(pat, text) is None:
                mismatches.append("logContracts.required not matched: %s" % (pat,))
        except re.error:
            mismatches.append("logContracts.required invalid regex: %s" % (pat,))
    for pat in log_contracts.get("forbidden", []) or []:
        try:
            if re.search(pat, text) is not None:
                mismatches.append("logContracts.forbidden matched: %s" % (pat,))
        except re.error:
            mismatches.append("logContracts.forbidden invalid regex: %s" % (pat,))

    reserved = tuple(b for b in RESERVED_EXPECTATION_BLOCKS if b in expectations)
    status = "PASS" if not mismatches else "FAIL"
    return ExpectationResult(status, tuple(mismatches), reserved)


# ---------------------------------------------------------------------------
# Anomaly sweep (design verifier 6 / N2). Pure over pre-grepped hit tokens.
# ---------------------------------------------------------------------------


def evaluate_anomaly_sweep(hit_tokens: Sequence[str], allowed_anomalies: Sequence[str]) -> List[str]:
    """Return the anomaly hits NOT in ``allowedAnomalies`` (design verifier 6).

    ``hit_tokens`` is the set of harness-owned Tier-C anomaly tokens grepped from
    the KSP.log; a scenario only ADDS known-benign exceptions via
    ``allowedAnomalies`` (a DEDICATED field, never logContracts.forbidden), so
    the harness-owned sweep set stays fixed. Any hit not allowed -> the caller
    reds PARSEK-FAIL(anomaly). Unknown tokens (not in ANOMALY_TOKENS) are ignored
    (the sweep set is fixed; a scenario cannot invent a new anomaly).
    """
    allowed = set(allowed_anomalies or ())
    return [t for t in hit_tokens if t in ANOMALY_TOKENS and t not in allowed]


# ---------------------------------------------------------------------------
# Ledger-oracle support (design M-B2, docs/dev/design-autotest-ledger-oracle.md).
# The PURE half of the leg-A manifest capture + the produced-save careerSave read.
# The oracle MATH itself lives in the sibling ``oracle.py`` (parse / compute /
# diff / build-result); run.py glues these two libraries together. Everything
# here is side-effect-free over strings / dicts and imports NOTHING from oracle
# (it emits the raw entry-dict shape oracle.parse_manifest_entries consumes, and
# reads oracle-entry objects structurally via duck typing in the cross-check).
# ---------------------------------------------------------------------------

# The [expectations.ledger] spec-surface vocabulary (design Data Model ~226). v1
# accepts exactly one value each; a literal {funds,science,reputation} seed and
# non-default tolerance profiles are RESERVED (validate rejects an unknown value).
LEDGER_SEED_FROM_VALUES: Tuple[str, ...] = ("template",)
LEDGER_TOLERANCE_VALUES: Tuple[str, ...] = ("default",)


def validate_ledger_expectations(ledger_block: Optional[Dict]) -> List[str]:
    """Validate the ``[expectations.ledger]`` spec-surface block (design ~226).

    Structural spec-surface only (a malformed ledger block must never launch KSP):
    ``seedFrom`` in the accepted set, ``tolerances`` in the accepted set,
    ``rec3CarveOut`` a bool, ``manifest`` an array. The per-ENTRY validation (the
    ``kind`` enum, the every-amount-is-a-DELTA rule, the state-dependent-facet
    author-constant rule) is oracle.parse_manifest_entries's job at RUN time (a
    captured line can only be judged against the produced log), so this stays a
    cheap pre-launch gate. Returns every failing rule (mirrors validate_spec)."""
    if not isinstance(ledger_block, dict):
        return ["expectations.ledger: must be a table"]
    errs: List[str] = []
    sf = ledger_block.get("seedFrom", "template")
    if sf not in LEDGER_SEED_FROM_VALUES:
        errs.append("expectations.ledger.seedFrom: %r not in %s (a literal seed is reserved)"
                    % (sf, list(LEDGER_SEED_FROM_VALUES)))
    tol = ledger_block.get("tolerances", "default")
    if tol not in LEDGER_TOLERANCE_VALUES:
        errs.append("expectations.ledger.tolerances: %r not in %s"
                    % (tol, list(LEDGER_TOLERANCE_VALUES)))
    r3 = ledger_block.get("rec3CarveOut", False)
    if not isinstance(r3, bool):
        errs.append("expectations.ledger.rec3CarveOut: %r must be a bool" % (r3,))
    manifest = ledger_block.get("manifest", [])
    if not isinstance(manifest, list):
        errs.append("expectations.ledger.manifest: must be an array of entry tables")
    return errs


def parse_career_save_block(analysis_json) -> Optional[Dict]:
    """Extract the ``careerSave`` block from a ``.analysis.json`` (string or dict).

    Design verifier step 1 (~455): the ledger-oracle verifier reads the parsed
    produced-save totals from THIS block. Returns the block dict when present (it
    carries its own ``parsed`` / ``hasX`` facet flags, so facet-absence is read
    from the flags, NEVER from a missing block). Returns None when the block is
    ABSENT ENTIRELY (an old / broken analyzer -> the caller treats it as
    INVALID(tooling), edge 13, NEVER a silent pass) or the JSON is unparseable. A
    ``{parsed:false}`` block is returned AS-IS (facet-absent, not tooling-missing,
    per the WRITER CONTRACT that the block is ALWAYS emitted when the analyzer ran).
    """
    obj = analysis_json
    if isinstance(analysis_json, str):
        try:
            obj = json.loads(analysis_json)
        except (ValueError, TypeError):
            return None
    if not isinstance(obj, dict):
        return None
    block = obj.get("careerSave")
    if not isinstance(block, dict):
        return None
    return block


@dataclass(frozen=True)
class StockAwardPattern:
    """One enumerated, EN-pinned stock KSP.log award-line pattern (design Behavior
    "Manifest capture" ~372). ``facet`` is the career pool the award credits
    (``funds`` / ``science`` / ``reputation``); ``kind`` is the manifest kind. The
    ``regex`` MUST define a named group ``amount`` (the per-event DELTA) and MAY
    define ``guid`` (contract identity) / ``subject`` (per-subject science id).
    Every pattern CITES its stock emitter; a candidate NOT confirmed stable on the
    EN instance is EXCLUDED (VERIFY-PENDING-OPERATOR) until an operator verifies it
    against a live EN KSP.log before any NONZERO-delta L1 scenario trusts it. v1
    (B10) is a ZERO-delta cross-check, so an incomplete/imperfect set is SAFE: a
    captured award that MISSED the enumeration still moves the produced save, so
    the save-diff reds anyway; a false-positive capture on B10 reds as an
    unexpected award, cross-checked by the save-diff (design Mental Model ~199)."""
    kind: str
    facet: str
    regex: "re.Pattern"
    emitter: str


# UT correlation source (design ~390): a stock award line is not self-stamped, so
# the capture assigns ``ut`` by the NEAREST UT-stamped [Parsek] line at or before
# it. Parsek log lines carry ``ut=<value>``.
_STOCK_UT_RE = re.compile(r"\[Parsek\].*?\but=(?P<ut>-?\d+(?:\.\d+)?)")

# The v1 EN-pinned candidate enumeration (design ~380, each VERIFY-PENDING-OPERATOR
# before a NONZERO L1 scenario trusts it). Deliberately CONSERVATIVE: the B10
# zero-delta cross-check makes an incomplete set safe. A candidate that is not
# stable in EN KSP.log (message-system chatter, localized text) is NOT enumerated;
# where stock is silent the capture falls to the RESERVED gameevents-captured
# provenance (M-B3), NEVER to a Parsek recalc read.
STOCK_AWARD_PATTERNS: Tuple[StockAwardPattern, ...] = (
    # ResearchAndDevelopment science credit on transmit / recover (design ~384). A
    # per-event DELTA line (``delta=``), never a running R&D pool balance.
    StockAwardPattern(
        "science-transmit", "science",
        re.compile(r"\bResearchAndDevelopment\b.*?\bscience\b"
                   r"(?:.*?\bsubject=(?P<subject>\S+))?.*?\bdelta=(?P<amount>-?\d+(?:\.\d+)?)"),
        "ResearchAndDevelopment"),
    # ContractSystem funds payout on completion (design ~383).
    StockAwardPattern(
        "contract-complete", "funds",
        re.compile(r"\bContractSystem\b.*?\bcontract\b.*?\bcompleted\b"
                   r"(?:.*?\bguid=(?P<guid>\S+))?.*?\bfunds=(?P<amount>-?\d+(?:\.\d+)?)"),
        "ContractSystem"),
    # ContractSystem reputation delta on completion (design ~383).
    StockAwardPattern(
        "contract-complete", "reputation",
        re.compile(r"\bContractSystem\b.*?\bcontract\b.*?\bcompleted\b"
                   r"(?:.*?\bguid=(?P<guid>\S+))?.*?\breputation=(?P<amount>-?\d+(?:\.\d+)?)"),
        "ContractSystem"),
)

# A line reporting a post-grant running BALANCE (not a per-event DELTA) is
# INADMISSIBLE (design ~398 / Mental Model ~196): admitting it would double-count
# against the seed. Such a line is explicitly REJECTED (counted, never captured).
BALANCE_LINE_PATTERNS: Tuple["re.Pattern", ...] = (
    re.compile(r"\b(?:total|current|running|new)\s+funds\b", re.IGNORECASE),
    re.compile(r"\bfunds\s+balance\b", re.IGNORECASE),
    re.compile(r"\b(?:total|current|running|new)\s+science\b", re.IGNORECASE),
    re.compile(r"\b(?:total|current|running|new)\s+reputation\b", re.IGNORECASE),
)


@dataclass(frozen=True)
class CapturedAward:
    """One ``stock-log-captured`` award (design ~85 / ~372). ``ut`` is the nearest
    preceding UT-stamped [Parsek] line's UT, or None; ``seq`` is the log line
    ordinal (the seqKey when ``ut`` is null). Every captured amount is a DELTA."""
    kind: str
    facet: str
    amount: float
    contract_guid: str
    subject_id: str
    ut: Optional[float]
    seq: int
    raw_line: str

    @property
    def seq_key(self):
        """The dedupe / sort seqKey, TYPE-TAGGED (design ~394), mirroring
        oracle.ManifestEntry.seq_key EXACTLY (the two are compared across types in the
        fill / unmatched matchers): ``("ut", <float>)`` when ``ut`` is known, else
        ``("ord", <int>)`` (the line ordinal). The tag prevents a null-UT ordinal 3
        from spuriously matching a captured award at UT 3.0 (3 == 3.0 untagged)."""
        return ("ut", self.ut) if self.ut is not None else ("ord", self.seq)

    def to_entry_dict(self) -> Dict:
        """The raw entry-dict shape oracle.parse_manifest_entries consumes (a
        ``stock-log-captured`` DELTA entry). Omits a facet key when the amount is
        on a different pool (a missing facet key parses as a 0 delta there)."""
        d: Dict = {
            "kind": self.kind,
            "provenance": "stock-log-captured",
            "amountKind": "delta",
            "seq": self.seq,
            self.facet: self.amount,
        }
        if self.ut is not None:
            d["ut"] = self.ut
        if self.contract_guid:
            d["contractGuid"] = self.contract_guid
        if self.subject_id:
            d["subjectIds"] = [self.subject_id]
        return d


@dataclass(frozen=True)
class StockCaptureResult:
    """Result of grepping the produced KSP.log for stock awards (design leg A)."""
    captured: Tuple[CapturedAward, ...]
    rejected_balance: int
    stock_lines: int  # award lines matched (before dedupe)


def parse_stock_award_lines(log_text: str) -> StockCaptureResult:
    """Grep a produced KSP.log body for enumerated stock-award DELTA lines (pure).

    Walks the log once, tracking the most recent UT-stamped [Parsek] line so each
    award correlates to the nearest preceding UT (``ut = None`` + the line ordinal
    ``seq`` as the seqKey when none is in range, design ~390). A running-BALANCE
    line is explicitly REJECTED (counted, never captured; design ~398). At most one
    award is captured per line (the first matching enumerated pattern). This is the
    leg-A CAPTURE the oracle cross-checks against the produced save; a conservative
    enumeration is SAFE for the zero-delta flagship (design Mental Model ~199)."""
    captured: List[CapturedAward] = []
    rejected = 0
    last_ut: Optional[float] = None
    for idx, line in enumerate((log_text or "").splitlines()):
        m_ut = _STOCK_UT_RE.search(line)
        if m_ut is not None:
            try:
                last_ut = float(m_ut.group("ut"))
            except (TypeError, ValueError):
                pass
        # A [Parsek] diagnostic line is NEVER a stock award, but Parsek logs mention
        # stock emitter class names + delta= (e.g. the ledger tracer's ledger-vs-truth
        # lines), which would false-capture as an award and false-red an empty-manifest
        # B10. Skip award/balance matching on any [Parsek]-tagged line -- but only AFTER
        # the last_ut update above, because the UT correlation DEPENDS on these lines
        # (the stock-award UT is read from the nearest preceding [Parsek] ut= stamp).
        if "[Parsek]" in line:
            continue
        if any(bp.search(line) for bp in BALANCE_LINE_PATTERNS):
            rejected += 1
            continue
        for pat in STOCK_AWARD_PATTERNS:
            m = pat.regex.search(line)
            if m is None:
                continue
            try:
                amount = float(m.group("amount"))
            except (TypeError, ValueError):
                continue
            gd = m.groupdict()
            captured.append(CapturedAward(
                kind=pat.kind, facet=pat.facet, amount=amount,
                contract_guid=str(gd.get("guid") or ""),
                subject_id=str(gd.get("subject") or ""),
                ut=last_ut, seq=idx, raw_line=line.strip()))
            break
    return StockCaptureResult(tuple(captured), rejected, len(captured))


def dedupe_captured_awards(captured: Sequence[CapturedAward]) -> List[CapturedAward]:
    """Dedupe captured awards on ``(seqKey, kind, contractGuid|subjectId,
    roundedAmount)`` keeping the FIRST (design ~404 / edge 2). A stock line
    re-emitted on a scene reload at the SAME seqKey is one effect; a genuine second
    identical award at a DISTINCT seqKey survives (the seqKey is in the key).
    ``roundedAmount`` is the amount to 3 decimals (design ~402) so float-format
    jitter across re-emitted lines does not defeat the dedupe."""
    seen = set()
    out: List[CapturedAward] = []
    for c in captured:
        ident = c.contract_guid or c.subject_id
        key = (c.seq_key, c.kind, ident, round(c.amount, 3))
        if key in seen:
            continue
        seen.add(key)
        out.append(c)
    return out


def unmatched_captured_awards(seam_entries, captured: Sequence[CapturedAward]
                              ) -> List[CapturedAward]:
    """Return captured awards NOT explained by a seam-declared entry (design edge 4).

    A captured award is EXPECTED iff a seam-declared entry shares its
    ``(seqKey, kind, identity)``; an UNMATCHED captured award is an UNEXPECTED
    stock award. On the empty-manifest B10 (no seam entries) EVERY captured award
    is unexpected, which is exactly the economy-drift signal the passive-safety
    scenario reds on (an unexpected award fired during passive play). ``seam_entries``
    are oracle.ManifestEntry objects, read structurally (``.seq_key`` / ``.kind`` /
    ``.contract_guid`` / ``.subject_ids``) so this imports nothing from oracle.

    MULTI-SUBJECT (item 10): a seam entry may declare MANY ``subject_ids`` (one entry
    covering several science subjects) while a captured award carries a SINGLE
    ``subject_id``. Register a key for the contract guid AND for EVERY declared subject
    id so an award on the entry's 2nd+ subject is explained, not falsely flagged
    unmatched (the prior code registered only ``subject_ids[0]``, false-redding awards
    on any later subject). Fail-closed: an entry with no guid and no subjects registers
    the empty identity "" (matching only an award that itself has no identity)."""
    seam_keys = set()
    for e in seam_entries or ():
        for ident in _entry_identities(e.contract_guid, e.subject_ids):
            seam_keys.add((e.seq_key, e.kind, ident))
    out: List[CapturedAward] = []
    for c in captured:
        ident = c.contract_guid or c.subject_id
        if (c.seq_key, c.kind, ident) not in seam_keys:
            out.append(c)
    return out


def _entry_identities(contract_guid: str, subject_ids: Sequence[str]) -> List[str]:
    """The identity keys a manifest entry can explain: its contract guid (if any) plus
    each of its declared subject ids. Falls back to the single empty identity "" when
    the entry declares neither (a scalar-pool entry matches only an identity-less
    award). Fail-closed (item 10)."""
    ids: List[str] = []
    if contract_guid:
        ids.append(contract_guid)
    for s in subject_ids or ():
        if s and s not in ids:
            ids.append(s)
    return ids or [""]


# ---------------------------------------------------------------------------
# Analyzer sub-classification (design verifier 3 / S1 / S2). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class AnalyzerVerdict:
    status: str    # "PASS" | "PARSEK-FAIL" | "INVALID"
    subkind: str   # "" | "analyzer" | "analyzer-error" | "fixture-stale" | "fixture-authoring"
    top_rule: Optional[str]


def classify_analyzer(red: Optional[int], analysis_json: Optional[AnalysisJson]) -> AnalyzerVerdict:
    """Classify an analyzer result from the GATE token + the JSON split (S1/S2).

    The GATE is the terminal ``RED=`` token (the sole gate source). RED absent ->
    INVALID(analyzer-error) (never a green pass). RED=0 -> PASS. RED=1 splits via
    the JSON (never the txt header): a REAL non-BASELINE FAIL -> PARSEK-FAIL
    (analyzer), and it WINS over any BASELINE-* fixture-authoring FAIL (S2);
    BASELINE-*-only FAIL -> INVALID(fixture-authoring); stale-only
    (staleNonBaselined>0, failNonBaselined==0) -> INVALID(fixture-stale). A RED=1
    with no JSON detail falls back to PARSEK-FAIL (a red gate must never read green).
    """
    if red is None:
        return AnalyzerVerdict("INVALID", "analyzer-error", None)
    if red == 0:
        return AnalyzerVerdict("PASS", "", None)
    # red == 1 (or any nonzero): subclassify from the JSON.
    if analysis_json is None:
        return AnalyzerVerdict("PARSEK-FAIL", "analyzer", None)
    real_fails = analysis_json.non_baseline_fail_findings()
    if real_fails:
        return AnalyzerVerdict("PARSEK-FAIL", "analyzer", real_fails[0].rule_id)
    if analysis_json.fail_non_baselined > 0:
        # failNonBaselined counts a fail the findings list did not surface a
        # non-baseline entry for; a red fail must never read green -> analyzer defect.
        return AnalyzerVerdict("PARSEK-FAIL", "analyzer", None)
    baseline_fails = analysis_json.baseline_fail_findings()
    if baseline_fails:
        return AnalyzerVerdict("INVALID", "fixture-authoring", baseline_fails[0].rule_id)
    if analysis_json.stale_non_baselined > 0:
        return AnalyzerVerdict("INVALID", "fixture-stale", None)
    # RED=1 but the JSON shows no non-baselined fail/stale: a gate/JSON
    # disagreement; never read green -> treat as an analyzer defect.
    return AnalyzerVerdict("PARSEK-FAIL", "analyzer", None)


# ---------------------------------------------------------------------------
# Verdict classification (design Verdict classification / classify_verdict). Pure.
# ---------------------------------------------------------------------------

VERDICT_PASS = "PASS"
VERDICT_INVALID = "INVALID"
VERDICT_KILLED = "KILLED"
VERDICT_PARSEK_FAIL = "PARSEK-FAIL"
VERDICT_EXPECTED_FAIL = "EXPECTED-FAIL"
VERDICT_XPASS = "XPASS"

VERDICTS: Tuple[str, ...] = (
    VERDICT_PASS, VERDICT_PARSEK_FAIL, VERDICT_INVALID, VERDICT_KILLED,
    VERDICT_EXPECTED_FAIL, VERDICT_XPASS,
)

# The PARSEK-FAIL subkinds classify_verdict can assign (the analyzer PARSEK-FAIL
# path carries subkind "analyzer"). An expectedFail.subkind, when present, must
# name one of these so the signature match is against a real failure class (S2).
PARSEK_FAIL_SUBKINDS: Tuple[str, ...] = (
    "batch-crashed", "analyzer", "log-contract", "results", "anomaly",
    "expectation", "ledger",
)

# INVALID subkinds that are retry-once-then-INVALID for the driver/tooling
# stages (design). Everything else (admission, instance-locked/busy, fixture-*,
# spec-invalid, boot-crash-repeated) is a terminal INVALID.
RETRYABLE_INVALID_SUBKINDS: Tuple[str, ...] = (
    "boot-crash", "load-failed", "driver-verdict-mismatch", "driver-stage",
    "seam-timeout", "tooling", "analyzer-error",
    # M-B1 mission subkinds (design "hlib additions"): the FOUR retryable mission
    # verdicts join the driver/tooling retry set. tooling-venv is TERMINAL and is
    # deliberately NOT here (a missing / drifted venv is a provisioning fault a
    # retry cannot fix, caught at pre-launch ADMIT before any KSP boot).
    "mission", "tooling-krpc", "tooling-mission", "autopilot-flake",
    # M-C1 seam-verb refusal subkinds (design-autotest-seam-verbs-c1.md, M-A5
    # integration): every M-C1 verb refusal is a DRIVER problem (a gate decline,
    # insufficient funds/science, a backward jump, a missing dialog), classified
    # INVALID retry-once, NEVER PARSEK-FAIL. These refine the reporting; even before
    # a driver step emits them, a verdict-mismatch already retries via
    # driver-verdict-mismatch.
    "driver-gate", "driver-rewind", "driver-dialog", "driver-arg", "driver-career",
)

# M-C1 verb-refusal `msg=` prefix -> finer driver-* subkind (M-A5 integration item 6).
# The C# executors emit a typed refusal reason as the response line's msg (see
# TestCommands/ParsekTestCommandAddon*.cs + TestCommandKscAction.cs / GateRefusalMsg);
# without this map every M-C1 refusal collapses to the coarse driver-verdict-mismatch,
# leaving the five driver-* subkinds dead vocabulary. Keyed on the leading msg token
# (the wire msg is percent-encoded, so `refly-gate <reason>` arrives as
# `refly-gate%20<reason>` and matches on the `refly-gate` head). Verbs / reasons not in
# the table fall back to driver-verdict-mismatch (all retry-once INVALID either way).
_SEAM_REFUSAL_SUBKINDS: Dict[str, str] = {
    # InvokeRewind: a precondition gate declined (refly-gate) vs a bad RP/slot arg.
    "refly-gate": "driver-gate",
    "unknown-rp": "driver-arg",
    "unknown-slot": "driver-arg",
    # AnswerMergeDialog: the merge dialog the verb needed was not live / lacked the choice.
    "unknown-choice": "driver-dialog",
    "no-live-dialog": "driver-dialog",
    "choice-unavailable": "driver-dialog",
    # TimeJump: a refused / backward jump is rewind-class; a missing target is a bad arg.
    "backward-jump": "driver-rewind",
    "jump-refused": "driver-rewind",
    "missing-jump-target": "driver-arg",
    # KscAction: dispatch not-ready + career-state declines are career-class; unknown /
    # missing targets are arg-class.
    "career-not-ready": "driver-career",
    "unknown-action": "driver-arg",
    "unknown-tech-node": "driver-arg",
    "unknown-facility": "driver-arg",
    "unknown-kerbal": "driver-arg",
    "unknown-target": "driver-arg",
    "missing-arg": "driver-arg",
    "insufficient-science": "driver-career",
    "insufficient-funds": "driver-career",
    "facility-at-max": "driver-career",
    "node-already-unlocked": "driver-career",
    "kerbal-not-applicant": "driver-career",
    "kerbal-parsek-managed": "driver-career",
    "kerbal-not-dismissable": "driver-career",
    "blocked-committed": "driver-career",
}


def classify_seam_refusal_subkind(msg: Optional[str]) -> str:
    """Map an M-C1 verb-refusal response ``msg`` to a finer driver-* subkind, or ""
    when the reason is unrecognized (the caller then uses driver-verdict-mismatch).
    Reads only the leading token so a compound gate reason (``refly-gate <detail>``,
    on the wire ``refly-gate%20<detail>``) still classifies. Pure (M-A5 item 6)."""
    token = (msg or "").split("%20", 1)[0].strip()
    return _SEAM_REFUSAL_SUBKINDS.get(token, "")


@dataclass(frozen=True)
class Verdict:
    verdict: str
    subkind: str
    retryable: bool
    reason: str
    expected_fail_matched: bool = False
    note: str = ""


def expected_fail_signature_matched(base_verdict: str, base_subkind: str,
                                    ef_subkind: str) -> bool:
    """Decide whether a computed verdict matches the tracked expected-fail signature
    (S2). Only a PARSEK-FAIL can match. When ``ef_subkind`` is empty the match is
    bugId-only (ANY PARSEK-FAIL matches -- the v1 adaptation the run.py caller warns
    about at demotion time); when set, the base verdict's subkind must equal it, so
    an expected-fail scenario that fails a DIFFERENT way (subkind mismatch) stays
    PARSEK-FAIL instead of being demoted to EXPECTED-FAIL."""
    if base_verdict != VERDICT_PARSEK_FAIL:
        return False
    if not ef_subkind:
        return True
    return base_subkind == ef_subkind


def classify_expected_fail(base: Verdict, bug_id: str, signature_matched: bool) -> Verdict:
    """Overlay the expected-fail semantics on a computed verdict (design N8/N11).

    An ``expectedFail.bugId`` scenario is NEVER a plain PASS: a clean run is XPASS
    (the guard must not silently drop). A PARSEK-FAIL whose SIGNATURE matches the
    tracked bug is demoted to EXPECTED-FAIL (green-for-triage); a PARSEK-FAIL that
    fails a DIFFERENT way stays PARSEK-FAIL (signature-based, not any-failure).
    INVALID/KILLED are environment/tooling events, unaffected by the key.
    """
    if not bug_id:
        return base
    if base.verdict == VERDICT_PARSEK_FAIL and signature_matched:
        return Verdict(VERDICT_EXPECTED_FAIL, base.subkind, False,
                       "expected-fail signature matched bugId=%s" % bug_id, True, base.note)
    if base.verdict == VERDICT_PASS:
        return Verdict(VERDICT_XPASS, "", False,
                       "expected-fail bugId=%s unexpectedly passed" % bug_id, False, base.note)
    return base


def classify_verdict(driver: Dict, verifiers: Dict, expected_fail: Dict,
                     attempt: int, retry_policy: str) -> Verdict:
    """Map a run attempt's facts to the taxonomy
    {PASS, PARSEK-FAIL, INVALID, KILLED, EXPECTED-FAIL, XPASS} (design).

    Precedence (first match wins), then the expected-fail overlay:
      spec-invalid / admission / instance-locked / instance-busy -> INVALID (no retry)
      watchdog KILL -> KILLED (no retry; torn save skipped)
      boot-crash -> INVALID (retry once; repeated -> boot-crash-repeated, terminal)
      post-boot self-exit w/ pending step OR expected-batch absent -> PARSEK-FAIL(batch-crashed)
      driver stage failed -> INVALID (retry once)
      verifier tooling timeout / analyzer-error -> INVALID (retry the subprocess)
      analyzer RED=1 real fail -> PARSEK-FAIL; stale-only/baseline-only -> INVALID
      log-contract / results / anomaly / expectation / ledger -> PARSEK-FAIL
      else -> PASS
    ``retryable`` is a recommendation; ``should_retry`` is the authority
    combining attempt + policy.
    """
    def V(verdict, subkind, reason, retryable=False):
        return Verdict(verdict, subkind, retryable, reason)

    base: Optional[Verdict] = None

    if not driver.get("spec_valid", True):
        base = V(VERDICT_INVALID, "spec-invalid", "spec failed validation")
    elif not driver.get("admission_ok", True):
        base = V(VERDICT_INVALID, driver.get("admission_subkind", "admission"), "admission drift/missing")
    elif not driver.get("instance_lock_ok", True):
        base = V(VERDICT_INVALID, "instance-locked", "run lock held by a live sibling")
    elif driver.get("instance_busy", False):
        base = V(VERDICT_INVALID, "instance-busy", "a live KSP is bound to the instance")
    elif verifiers.get("killed", False):
        base = V(VERDICT_KILLED, "budget", "watchdog killed the process tree")
    elif driver.get("boot_crashed", False):
        if driver.get("boot_crash_repeated", False):
            base = V(VERDICT_INVALID, "boot-crash-repeated", "deterministic boot crash on retry")
        else:
            base = V(VERDICT_INVALID, "boot-crash", "process exited during boot-wait", retryable=True)
    elif driver.get("batch_crashed", False):
        base = V(VERDICT_PARSEK_FAIL, "batch-crashed", "post-boot self-exit aborted the batch")
    elif not driver.get("valid", True):
        subkind = driver.get("stage_subkind", "driver-stage")
        base = V(VERDICT_INVALID, subkind, "driver stage failed", retryable=True)
    elif verifiers.get("batch_expected", False) and not verifiers.get("batch_present", True):
        base = V(VERDICT_PARSEK_FAIL, "batch-crashed", "expected BATCH_COMPLETE absent")
    elif verifiers.get("tooling_invalid", False):
        base = V(VERDICT_INVALID, verifiers.get("tooling_subkind", "tooling"),
                 "verifier subprocess tooling failure", retryable=True)
    else:
        analyzer = verifiers.get("analyzer")
        if analyzer is not None:
            if analyzer.status == "INVALID":
                base = V(VERDICT_INVALID, analyzer.subkind, "analyzer %s" % analyzer.subkind,
                         retryable=(analyzer.subkind == "analyzer-error"))
            elif analyzer.status == "PARSEK-FAIL":
                base = V(VERDICT_PARSEK_FAIL, analyzer.subkind,
                         "analyzer red topRule=%s" % (analyzer.top_rule,))
        if base is None:
            if verifiers.get("log_validate_failed", False):
                base = V(VERDICT_PARSEK_FAIL, "log-contract", "log validation failed")
            elif verifiers.get("results_failed", False) or verifiers.get("results_mismatch", False):
                base = V(VERDICT_PARSEK_FAIL, "results", "results FAIL rows or count mismatch")
            elif verifiers.get("anomaly_hit", False):
                base = V(VERDICT_PARSEK_FAIL, "anomaly", "unallowed Tier-C anomaly line")
            elif verifiers.get("expectation_mismatch", False):
                base = V(VERDICT_PARSEK_FAIL, "expectation", "expectations manifest mismatch")
            elif verifiers.get("ledger_drift", False):
                base = V(VERDICT_PARSEK_FAIL, "ledger", "world/ledger oracle drift")
            else:
                base = V(VERDICT_PASS, "", "driver valid, every verifier PASS/SKIPPED")

    ef = expected_fail or {}
    return classify_expected_fail(base, ef.get("bugId", "") or "", bool(ef.get("signature_matched", False)))


def should_retry(verdict: Verdict, attempt: int, retry_policy: str) -> bool:
    """Authority on whether to retry (design retry decision / edges 25/12/30).

    Retry iff policy is ``once``, this is attempt 1, and the verdict is a
    retryable INVALID subkind (driver/boot/tooling/analyzer-error). PARSEK-FAIL is
    NEVER retried (a defect is a defect); KILLED is not retried by default (a hang
    recurs); a terminal INVALID (admission, fixture-*, boot-crash-repeated,
    spec-invalid) is not retried.
    """
    if retry_policy != "once":
        return False
    if attempt >= 2:
        return False
    if verdict.verdict != VERDICT_INVALID:
        return False
    return verdict.subkind in RETRYABLE_INVALID_SUBKINDS


def resolve_terminal(attempts: Sequence[Verdict]) -> Verdict:
    """Reduce an ordered list of attempt verdicts to the terminal result.

    An attempt-1 INVALID followed by an attempt-2 PASS terminates PASS carrying a
    ``flakedThenPassed`` note (there is no FLAKE verdict; the attempt-1 INVALID
    still feeds the flake ledger numerator). Otherwise the last attempt's verdict
    is terminal.
    """
    if not attempts:
        return Verdict(VERDICT_INVALID, "no-attempts", False, "no attempts recorded")
    last = attempts[-1]
    if last.verdict == VERDICT_PASS and any(a.verdict == VERDICT_INVALID for a in attempts[:-1]):
        return Verdict(last.verdict, last.subkind, last.retryable, last.reason,
                       last.expected_fail_matched, "flakedThenPassed")
    return last


# ---------------------------------------------------------------------------
# Subprocess-scoped retry scope (M-A5.1, design note "subprocess-scoped tooling
# retry" / S14 / edges 12,30). Pure. v1 retried the WHOLE attempt (re-stage +
# re-boot KSP, ~10 min) for a retryable INVALID even when only a cheap verifier
# subprocess flaked (a wedged pwsh analyzer, a transient log-validate failure).
# This classifier lets run.py re-run JUST the wedged verifier subprocess over the
# SAME already-produced run artifacts ONCE before falling back to the whole-attempt
# retry. The re-invocation itself is the shell's (behind the Runtime seam); the
# SCOPE decision is here.
# ---------------------------------------------------------------------------

RETRY_SCOPE_NONE = "none"                    # not retryable at the verifier level
RETRY_SCOPE_SUBPROCESS = "subprocess"        # re-run THIS subprocess over the SAME artifacts, once
RETRY_SCOPE_WHOLE_ATTEMPT = "whole-attempt"  # fall back to the whole-attempt retry (fresh stage + boot)

# The verifier stages that shell out over the PRODUCED run artifacts (KSP.log / the
# produced save) and can be re-invoked WITHOUT a fresh KSP boot. Only these two shell
# scripts are subprocess-retryable in v1 (design: "re-invoke just the wedged
# analyze-recordings.ps1 / validate-ksp-log.ps1 over the already-produced save").
SUBPROCESS_RETRYABLE_STAGES: Tuple[str, ...] = ("analyzer", "logValidate")

# The verifier-stage fault subkinds a subprocess retry can address: a wedged/killed
# subprocess (``tooling`` -- a per-subprocess wall-clock timeout, S14) or an analyzer
# that RAN but emitted no terminal RED gate token (``analyzer-error`` -- the analyzer
# CRASH case, distinct from a RED=1 VERDICT). A Parsek VERDICT (analyzer RED=1 ->
# PARSEK-FAIL, or a log-contract FAIL) is NEVER in this set: it is a real signal that
# must never be re-run away (the HARD CONSTRAINT "analyzer RED is a verdict, analyzer
# CRASH is tooling").
SUBPROCESS_RETRYABLE_SUBKINDS: Tuple[str, ...] = ("tooling", "analyzer-error")


def classify_retry_scope(stage: str, is_tooling_fault: bool, subkind: str) -> str:
    """Decide the retry SCOPE for one verifier-stage outcome (M-A5.1).

    ``is_tooling_fault`` is the caller's assertion that this outcome is a TOOLING
    fault (a wedged/crashed subprocess), NOT a Parsek verdict -- the load-bearing
    guard: a Parsek verdict (analyzer RED=1 -> PARSEK-FAIL, a log-contract FAIL)
    passes ``is_tooling_fault=False`` and gets RETRY_SCOPE_NONE, so a real defect is
    never re-run away (regression: a subprocess retry silently flipping a RED to green
    on a nondeterministic analyzer). Given a genuine tooling fault:

    - a subprocess-retryable stage (``analyzer`` / ``logValidate``) with a
      subprocess-retryable subkind (``tooling`` / ``analyzer-error``) -> SUBPROCESS:
      re-run that subprocess over the SAME artifacts once (no fresh boot);
    - any other tooling fault (a stage that cannot be re-run over the same artifacts,
      e.g. the ledger-oracle careerSave read, or a non-retryable subkind) ->
      WHOLE_ATTEMPT: the existing whole-attempt retry path is unchanged for it.

    The subprocess retry is a REFINEMENT in front of the whole-attempt retry, never a
    replacement: a SUBPROCESS retry that still faults falls through to WHOLE_ATTEMPT
    via the unchanged classify_verdict / should_retry taxonomy (this function does not
    decide that fall-through; the caller re-runs once, then lets the second outcome
    flow through the normal INVALID path).
    """
    if not is_tooling_fault:
        return RETRY_SCOPE_NONE
    if stage in SUBPROCESS_RETRYABLE_STAGES and subkind in SUBPROCESS_RETRYABLE_SUBKINDS:
        return RETRY_SCOPE_SUBPROCESS
    return RETRY_SCOPE_WHOLE_ATTEMPT


# ---------------------------------------------------------------------------
# Mission step classification + venv admission (design M-B1 "hlib additions").
# Pure; feeds the EXISTING driver-validity stage. run.py maps the mission
# subprocess's verdict through classify_mission_step and admits the mission venv
# via venv_admission at the pre-launch ADMIT phase (alongside instance admission).
# ---------------------------------------------------------------------------

# Mission verdict -> INVALID subkind map (design Failure taxonomy mapping table).
# MISSION-OK has NO subkind (it is the met signal). All four failure subkinds are
# RETRYABLE (they are in RETRYABLE_INVALID_SUBKINDS); venv drift is handled
# separately by venv_admission and maps to the TERMINAL tooling-venv.
MISSION_VERDICT_SUBKINDS: Dict[str, str] = {
    MISSION_VERDICT_CONNECT_TIMEOUT: "tooling-krpc",
    MISSION_VERDICT_ASSERT_FAIL: "mission",
    MISSION_VERDICT_FLAKE: "autopilot-flake",
    MISSION_VERDICT_ERROR: "tooling-mission",
}

# The terminal (non-retryable) venv-admission subkind (design edge 4). Deliberately
# ABSENT from RETRYABLE_INVALID_SUBKINDS: should_retry therefore never retries it.
VENV_INVALID_SUBKIND = "tooling-venv"


def classify_mission_step(mission_verdict: Optional[str]) -> Tuple[bool, str]:
    """Map a mission subprocess verdict to ``(met, INVALID subkind)`` (design table).

    ``MISSION-OK`` -> ``(True, "")``: the mission step is MET; run.py proceeds into
    the seam teardown and the FULL verifier chain runs. That verifier chain is
    ORTHOGONAL to this gate -- a MISSION-OK flight that Parsek then mis-records is
    still PARSEK-FAIL, decided by ``classify_verdict`` over the produced save, NOT
    here (the mission-validity gate only answers "did we get a valid flight to test
    against"). Each non-OK verdict -> ``(False, subkind)``: CONNECT-TIMEOUT ->
    ``tooling-krpc``, ASSERT-FAIL -> ``mission``, FLAKE -> ``autopilot-flake``,
    ERROR -> ``tooling-mission``; all four are retryable-once. A None / unknown
    verdict FAILS CLOSED to ``(False, "tooling-mission")`` -- the design's
    missing-result fallback (edge 12): a mission that never wrote a readable verdict
    is a tooling INVALID, never a silent met.
    """
    if mission_verdict == MISSION_VERDICT_OK:
        return True, ""
    subkind = MISSION_VERDICT_SUBKINDS.get(mission_verdict or "")
    if subkind is None:
        return False, "tooling-mission"
    return False, subkind


def venv_admission(stamp: Optional[Dict], requirements: Optional[Dict]) -> Tuple[bool, str]:
    """Admit (or refuse) the mission venv before any KSP launch (design edge 4).

    Mirrors ``admit_instance`` for the mission venv: run at the pre-launch ADMIT
    phase. The venv is admitted only when its ``.venv-stamp.json`` records a pin set
    that MATCHES the committed ``requirements.txt`` pins; a MISSING stamp (never
    bootstrapped) or a DRIFTED pin (requirements changed without a re-bootstrap) is
    refused. A refusal ALWAYS carries the TERMINAL, non-retryable ``tooling-venv``
    subkind (absent from RETRYABLE_INVALID_SUBKINDS, so ``should_retry`` never
    retries it): a retry cannot re-bootstrap a venv, and a stale / absent kRPC
    client must never silently certify a flight.

    PURE / SHELL split: the caller reads the stamp JSON and parses
    ``requirements.txt`` (I/O); both arrive here already parsed. ``stamp`` is
    None / empty when the stamp file is absent. ``requirements`` maps distribution
    name -> pinned version (e.g. ``{"krpc": "0.5.4", "protobuf": "4.21.0"}``); the
    stamp's frozen resolved pins live under ``stamp["pins"]`` (same shape). Only the
    COMMITTED requirements are enforced -- an extra pin in the stamp not yet promoted
    into ``requirements`` (the PROVISIONAL protobuf line before the first verified
    bootstrap) is tolerated, so the venv is not falsely refused pre-promotion.
    """
    if not stamp:
        return False, VENV_INVALID_SUBKIND
    reqs = requirements or {}
    stamp_pins = stamp.get("pins", {}) or {}
    for dist, want in reqs.items():
        if str(stamp_pins.get(dist)) != str(want):
            return False, VENV_INVALID_SUBKIND
    return True, ""


# ---------------------------------------------------------------------------
# Result record serialization + schema gate (design Result record). Pure.
# ---------------------------------------------------------------------------


def check_schema(obj: Dict, expected: int = SCHEMA_VERSION) -> Tuple[bool, str]:
    """Gate a persisted artifact's top-level ``schema`` (design Backward Compat).

    A future schema is REFUSED with a clear message (not mis-parsed): a schema
    bump must make the harness refuse an old/new artifact rather than silently
    mis-admit it. Returns (ok, message).
    """
    got = (obj or {}).get("schema")
    if got == expected:
        return True, "schema %d ok" % expected
    if isinstance(got, int) and got > expected:
        return False, "schema %d newer than supported %d; refusing" % (got, expected)
    return False, "schema %r != expected %d" % (got, expected)


def serialize_result(result: Dict) -> str:
    """Serialize a result record deterministically (stable key order, no volatile
    absolute paths in the compared fields, floats via repr through json).

    Byte-identical output for identical inputs, so results diff cleanly and the
    coverage tool parses them without guessing (design determinism test). Uses
    ``\\n`` line endings explicitly so a record written on Windows and Linux is
    byte-identical.
    """
    text = json.dumps(result, sort_keys=True, indent=2, ensure_ascii=True)
    return text.replace("\r\n", "\n") + "\n"


def deserialize_result(text: str) -> Dict:
    """Parse a serialized result record back to a dict (round-trip partner of
    ``serialize_result``)."""
    return json.loads(text)


# ---------------------------------------------------------------------------
# Coverage computation (design Coverage + flake generation / compute_coverage).
# ---------------------------------------------------------------------------

# Result verdicts that count as a GREEN for coverage (design: PASS or
# EXPECTED-FAIL for a scenario that covers the value).
GREEN_VERDICTS: Tuple[str, ...] = (VERDICT_PASS, VERDICT_EXPECTED_FAIL)


def _result_utc(result: Dict) -> str:
    """The comparable UTC timestamp of a result (endedUtc preferred, else
    startedUtc, else empty). UTC ISO-8601 string compare is immune to tz/DST
    (design edge 26)."""
    return str(result.get("endedUtc") or result.get("startedUtc") or "")


@dataclass(frozen=True)
class CoverageValue:
    dimension: str
    value: str
    covered_by: Tuple[str, ...]
    last_green: Optional[str]
    status: str  # "" | "UNCOVERED" | "EXPECTED-FAIL:<bugId>"


@dataclass(frozen=True)
class CoverageReport:
    values: Tuple[CoverageValue, ...]
    uncovered: Tuple[str, ...]           # "<D>/<value>" tokens
    expected_fail_table: Dict            # bugId -> [(scenarioId, latestVerdict)]
    rollup: Dict


def _registry_pairs(registry: Dict) -> List[Tuple[str, str]]:
    pairs: List[Tuple[str, str]] = []
    for dim in sorted(k for k in registry if k != "schema"):
        vals = _registry_values(registry, dim)
        if vals is None:
            continue
        for v in vals:
            pairs.append((dim, v))
    return pairs


def _latest_result_by_scenario(results: Sequence[Dict]) -> Dict[str, Dict]:
    latest: Dict[str, Dict] = {}
    for r in results:
        sid = r.get("scenarioId")
        if sid is None:
            continue
        prev = latest.get(sid)
        if prev is None or _result_utc(r) >= _result_utc(prev):
            latest[sid] = r
    return latest


def compute_coverage(specs: Sequence[Dict], results: Sequence[Dict], registry: Dict) -> CoverageReport:
    """Map every registry ``(dimension, value)`` to its covering scenarios + last
    green run, plus the uncovered list, the expected-fail table, and a rollup.

    A value's ``last_green`` is the newest result (UTC string compare) whose
    verdict is PASS or EXPECTED-FAIL for a scenario that covers it; a value with
    zero covering scenarios is UNCOVERED; a value covered ONLY by expected-fail
    scenarios is tagged EXPECTED-FAIL:<bugId>. Deterministic given the inputs
    (sorted iteration), so coverage diffs are readable (design stability test):
    a red run must never count as coverage (false "exhaustive" signal), and a
    genuinely covered value must never show uncovered.
    """
    # scenario id -> its expectedFail bugId (from the spec)
    spec_bug: Dict[str, str] = {}
    covered_by: Dict[Tuple[str, str], List[str]] = {}
    for spec in specs:
        sid = spec.get("id")
        if sid is None:
            continue
        spec_bug[sid] = (spec.get("expectedFail", {}) or {}).get("bugId", "") or ""
        dims = spec.get("dimensionsCovered", {}) or {}
        for dim, values in dims.items():
            for v in values or []:
                covered_by.setdefault((dim, v), []).append(sid)

    # newest green result per scenario id
    green_utc: Dict[str, str] = {}
    for r in results:
        if r.get("verdict") in GREEN_VERDICTS:
            sid = r.get("scenarioId")
            u = _result_utc(r)
            if sid is not None and (sid not in green_utc or u >= green_utc[sid]):
                green_utc[sid] = u

    values: List[CoverageValue] = []
    uncovered: List[str] = []
    covered_count = 0
    ef_value_count = 0
    for dim, val in _registry_pairs(registry):
        scenarios = sorted(covered_by.get((dim, val), []))
        last_green: Optional[str] = None
        for sid in scenarios:
            u = green_utc.get(sid)
            if u and (last_green is None or u > last_green):
                last_green = u
        if not scenarios:
            status = "UNCOVERED"
            uncovered.append("%s/%s" % (dim, val))
        elif all(spec_bug.get(sid) for sid in scenarios):
            # covered only by expected-fail scenarios
            bug = next((spec_bug[sid] for sid in scenarios if spec_bug.get(sid)), "")
            status = "EXPECTED-FAIL:%s" % bug
            ef_value_count += 1
            covered_count += 1
        else:
            status = ""
            covered_count += 1
        values.append(CoverageValue(dim, val, tuple(scenarios), last_green, status))

    # expected-fail table: bugId -> [(scenarioId, latestVerdict)]
    latest = _latest_result_by_scenario(results)
    ef_table: Dict[str, List[Tuple[str, str]]] = {}
    for sid, bug in sorted(spec_bug.items()):
        if not bug:
            continue
        latest_verdict = (latest.get(sid, {}) or {}).get("verdict", "never")
        ef_table.setdefault(bug, []).append((sid, latest_verdict))

    rollup = {
        "values": len(values),
        "covered": covered_count,
        "uncovered": len(uncovered),
        "expectedFailValues": ef_value_count,
        "xpass": sum(1 for sid in spec_bug if spec_bug[sid]
                     and (latest.get(sid, {}) or {}).get("verdict") == VERDICT_XPASS),
    }
    return CoverageReport(tuple(values), tuple(uncovered), ef_table, rollup)


def coverage_to_json_obj(report: CoverageReport) -> Dict:
    """Deterministic JSON-serializable projection of a CoverageReport (stable key
    order via json.dumps sort_keys; design coverage stability test)."""
    return {
        "schema": SCHEMA_VERSION,
        "rollup": report.rollup,
        "values": [
            {
                "dimension": cv.dimension,
                "value": cv.value,
                "coveredBy": list(cv.covered_by),
                "lastGreen": cv.last_green,
                "status": cv.status,
            }
            for cv in report.values
        ],
        "uncovered": list(report.uncovered),
        "expectedFail": {bug: [list(t) for t in rows]
                         for bug, rows in report.expected_fail_table.items()},
    }


def coverage_to_txt(report: CoverageReport) -> str:
    """Grep-friendly coverage report, one line per value (design):
    ``<D> <value> coveredBy=<n> lastGreen=<utc|never> [UNCOVERED|EXPECTED-FAIL:<bugId>]``."""
    lines: List[str] = []
    for cv in report.values:
        tag = (" " + cv.status) if cv.status else ""
        lines.append("%s %s coveredBy=%d lastGreen=%s%s" % (
            cv.dimension, cv.value, len(cv.covered_by),
            cv.last_green if cv.last_green else "never", tag))
    return "\n".join(lines) + ("\n" if lines else "")


# ---------------------------------------------------------------------------
# Flake computation + quarantine (design Coverage + flake generation / N4). Pure.
# ---------------------------------------------------------------------------

from datetime import datetime, timedelta  # noqa: E402

QUARANTINE_RATE = 0.20
FLAKE_WINDOW_DAYS = 7

# Attempt outcomes that count toward the flake (quarantine) rate: KILLED counts
# too (N4) -- a scenario that keeps timing out is as unusable in nightly as one
# that keeps going INVALID.
FLAKE_NUMERATOR_VERDICTS: Tuple[str, ...] = (VERDICT_INVALID, VERDICT_KILLED)


@dataclass(frozen=True)
class FlakeResult:
    total: int
    numerator: int  # INVALID + KILLED within the window
    rate: float
    quarantined: bool


def _parse_iso(ts: str) -> Optional[datetime]:
    if not ts:
        return None
    try:
        return datetime.fromisoformat(ts.replace("Z", "+00:00"))
    except (ValueError, TypeError):
        return None


def compute_flake(
    attempts: Sequence[Dict],
    now: Optional[str] = None,
    prior_quarantined: bool = False,
    window_days: int = FLAKE_WINDOW_DAYS,
) -> FlakeResult:
    """Compute the rolling flake rate + quarantine for one (scenario, stage).

    ``attempts`` are per-attempt records ``{"utc": iso, "outcome": verdict}``.
    rate = (INVALID + KILLED) / attempts over the trailing ``window_days``
    (KILLED counts, N4). ``> 0.20`` sets ``quarantined = True``. Quarantine is
    STICKY and human-only: ``prior_quarantined`` carries forward regardless of a
    subsequent quiet window (a benched scenario runs 0 attempts, so its window
    cannot self-heal; only a human spec edit unquarantines it). A flakedThenPassed
    PASS still contributes its attempt-1 INVALID to the numerator (the caller
    records both attempts).
    """
    cutoff: Optional[datetime] = None
    now_dt = _parse_iso(now) if now else None
    if now_dt is not None:
        cutoff = now_dt - timedelta(days=window_days)

    total = 0
    numerator = 0
    for a in attempts:
        if cutoff is not None:
            adt = _parse_iso(str(a.get("utc", "")))
            if adt is not None and adt < cutoff:
                continue
        total += 1
        if a.get("outcome") in FLAKE_NUMERATOR_VERDICTS:
            numerator += 1

    rate = (numerator / total) if total else 0.0
    quarantined = bool(prior_quarantined) or rate > QUARANTINE_RATE
    return FlakeResult(total, numerator, rate, quarantined)


# ---------------------------------------------------------------------------
# Subprocess-recovered flake accrual (M-A5.1 SF1). Pure. A whole-attempt flake
# writes its attempt-1 INVALID as its OWN durable result JSON, so the flake
# numerator sees that INVALID directly (see resolve_terminal's flakedThenPassed).
# A SUBPROCESS-recovered flake (a wedged analyzer / log-validate re-run once over
# the same artifacts, recovering WITHOUT a fresh boot) writes only ONE PASS result
# JSON, so its in-attempt tooling fault would be INVISIBLE to the numerator and a
# chronically-wedging tool would never accrue toward the 20% quarantine threshold.
# These helpers make a recovered subprocess retry accrue exactly like a whole-attempt
# flakedThenPassed: alongside the PASS attempt entry, a synthetic INVALID entry.
# ---------------------------------------------------------------------------


def recovered_subprocess_retries(result: Dict) -> List[Dict]:
    """The RECOVERED ``verifiers.subprocessRetry`` entries in one durable result.

    A recovered entry is one that ``retried`` a wedged verifier subprocess AND had the
    re-run clear the tooling fault (``recovered``). Only recovered retries are counted:
    a subprocess retry that did NOT recover fails the whole attempt INVALID, which is
    written as its OWN result JSON and accrues via its verdict -- adding a synthetic
    INVALID for it here would double-count. Reads the field structurally (a list of
    ``{"stage","retried","attempt1","attempt2","recovered"}`` dicts) so a result with
    no field (a clean run) yields an empty list.
    """
    verifiers = (result or {}).get("verifiers", {}) or {}
    retries = verifiers.get("subprocessRetry", []) or []
    out: List[Dict] = []
    for r in retries:
        if isinstance(r, dict) and r.get("retried") and r.get("recovered"):
            out.append(r)
    return out


def flake_attempt_entries(result: Dict) -> List[Dict]:
    """The per-attempt flake-ledger entries one durable result contributes (SF1).

    Always the result's own ``{"utc","outcome"}`` entry (the verdict the numerator
    already counts for a plain INVALID/KILLED). PLUS, when the result carries a
    RECOVERED subprocess retry AND its own verdict does NOT already count toward the
    flake numerator, ONE synthetic INVALID entry -- so a flake that the subprocess retry
    papered over inside a single boot still accrues toward the scenario's quarantine
    rate, exactly as a whole-attempt flakedThenPassed's attempt-1 INVALID JSON does.

    Item 10: the synthetic entry fires for ANY non-numerator verdict (PASS, PARSEK-FAIL,
    EXPECTED-FAIL, XPASS), not PASS alone -- a chronically-wedging analyzer on a FAILING
    scenario must still accrue toward quarantine, otherwise a scenario that reds for a
    genuine Parsek reason masks its own tooling flake forever. A result whose verdict is
    ALREADY a numerator verdict (INVALID/KILLED) adds NO synthetic entry: its own entry
    already accrues, and a non-recovered whole-attempt retry writes its own INVALID JSON
    -- either way a synthetic would double-count. The caller extends the scenario's
    attempt list with the returned entries.
    """
    utc = (result or {}).get("endedUtc", "")
    verdict = (result or {}).get("verdict")
    entries: List[Dict] = [{"utc": utc, "outcome": verdict}]
    if verdict not in FLAKE_NUMERATOR_VERDICTS and recovered_subprocess_retries(result):
        entries.append({"utc": utc, "outcome": VERDICT_INVALID})
    return entries
