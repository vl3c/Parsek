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
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence, Tuple

SCHEMA_VERSION = 1

# ---------------------------------------------------------------------------
# Vocabulary tables (design Data Model + consumed seam verb table).
# ---------------------------------------------------------------------------

# Scenario tiers (design spec `tier` enum).
TIERS: Tuple[str, ...] = ("perpr", "daily", "nightly", "weekly")

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

# Seam known-verb table (consumed contract, design-autotest-command-seam.md).
IMPLEMENTED_SEAM_VERBS: Tuple[str, ...] = (
    "SetSetting", "StartRecording", "StopRecording", "CommitTree", "DiscardTree",
    "RecordingState", "RunTests", "LoadGame", "MissionMark", "FlushAndQuit",
)
RESERVED_SEAM_VERBS: Tuple[str, ...] = (
    "StartLoopPlayback", "StopPlayback", "EnterWatchMode", "InvokeRewind",
    "AnswerMergeDialog", "KscAction", "SealSlot", "StashSlot", "FlySlot",
    "RouteCommand", "MissionConfig", "TimeJump", "SimulateStockSwitchClick",
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
DEFERRED_SEAM_VERBS: Tuple[str, ...] = ("RunTests", "LoadGame")

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

    outcomes: List[StepOutcome] = []
    first_unmet: Optional[StepOutcome] = None
    for step in expected_steps:
        sid = str(step.get("id"))
        cmd = str(step.get("cmd", ""))
        expect = str(step.get("expect", "OK"))
        verdict = observed.get(sid)
        found = verdict is not None
        met = found and verdict == expect
        outcome = StepOutcome(sid, cmd, expect, verdict, found, met)
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


def validate_spec(spec: Dict, registry: Dict, bug_ids: Optional[Sequence[str]] = None) -> SpecValidation:
    """Validate a parsed scenario spec against the design rules + the registry.

    Returns every failing rule (not just the first) so a spec author sees the
    whole problem set. Hard errors fail the spec (recorded INVALID-SPEC, KSP
    never launched); an unresolvable ``expectedFail.bugId`` is a WARNING only
    (a scenario may land just ahead of its todo-doc row, design).
    ``bug_ids`` is the injected set of resolvable todo-doc bug ids (I/O-free).
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
    inj = fixture.get("injectedRecordings")
    if inj not in INJECTED_RECORDINGS:
        errors.append("fixture.injectedRecordings: %r not in %s" % (inj, list(INJECTED_RECORDINGS)))

    driver = spec.get("driver", {}) or {}
    kind = driver.get("kind")
    if kind != "seam":
        errors.append("driver.kind: %r must be 'seam' (autopilot RESERVED for M-B1)" % (kind,))

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
    for i, step in enumerate(steps):
        step = step or {}
        cmd = step.get("cmd")
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
