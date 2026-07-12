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
from dataclasses import dataclass
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


def select_logvalidate_profile(recordings_count_max: Optional[int], killed: bool) -> LogValidateProfile:
    """Select the two orthogonal log-validation suppression profiles by run shape.

    - Recording-rules suppression (B1): IFF ``count.max == 0`` the harness
      suppresses exactly REC-001/REC-003, so a legitimately recording-free run
      (the flagship B10 daily loop) validates clean instead of redding on the
      marker-pairing rules; SES/FMT/WRN stay mandatory. For ``count.max > 0`` the
      REC rules stay mandatory (a dropped recording still reds).
    - Killed-run mode (S13): a KILLED attempt adds ``-KilledRun``, suppressing the
      marker-pairing rules SES-000/SES-001/REC-001/REC-003 (a kill legitimately
      truncates the tail) while FMT/WRN stay mandatory.
    The two are independent (a run can be in one, both, or neither).
    """
    suppress_rec = (recordings_count_max == 0)
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


# ---------------------------------------------------------------------------
# Expectations evaluation (design verifier 7 / evaluate_expectations). Pure.
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ExpectationResult:
    status: str  # "PASS" | "FAIL"
    mismatches: Tuple[str, ...]
    reserved: Tuple[str, ...]


RESERVED_EXPECTATION_BLOCKS: Tuple[str, ...] = ("world", "route", "rewind", "loop")


def evaluate_expectations(
    expectations: Dict, recording_count: Optional[int], log_text: str
) -> ExpectationResult:
    """Evaluate the v1-EVALUATED expectation blocks with tolerances.

    v1 evaluates: ``recordings.count`` (min/max window) and
    ``logContracts.required`` / ``logContracts.forbidden`` (LITERAL KSP.log line
    regex patterns applied with ``re.search`` over the log body). A mismatch ->
    FAIL (the caller reds PARSEK-FAIL expectation). The world/route/rewind/loop
    blocks are RESERVED: parsed + recorded SKIPPED until their verifiers land
    (M-B2/M-C2), so a scenario written now needs no format break then.
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

# INVALID subkinds that are retry-once-then-INVALID for the driver/tooling
# stages (design). Everything else (admission, instance-locked/busy, fixture-*,
# spec-invalid, boot-crash-repeated) is a terminal INVALID.
RETRYABLE_INVALID_SUBKINDS: Tuple[str, ...] = (
    "boot-crash", "load-failed", "driver-verdict-mismatch", "driver-stage",
    "seam-timeout", "tooling", "analyzer-error",
)


@dataclass(frozen=True)
class Verdict:
    verdict: str
    subkind: str
    retryable: bool
    reason: str
    expected_fail_matched: bool = False
    note: str = ""


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
