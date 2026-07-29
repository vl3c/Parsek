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
  - the unmet-mission tail decision (``SEAM_VERB_TAIL_ROLE`` +
    ``plan_unmet_mission_tail`` + ``spec_skips_tail_on_unmet_mission``)
  - expectations evaluation (``evaluate_expectations``)
  - the in-game batch anti-vacuity gate (``batch_contract_vacuity_gap`` +
    ``vacuous_batch_complete_probes``), enforced by ``validate_spec``
  - the batch-tally SOURCE-SYNC gate (``parse_ingame_test_declarations`` +
    ``derive_batch_tally`` + ``resolve_batch_tally_pin`` +
    ``batch_tally_pin_mismatches``): the pure half of the check that a spec's
    pinned ``total=``/``skipped=`` still agrees with the C# ``[InGameTest]``
    attributes. Enforced by a test-suite sweep, not ``validate_spec`` -- the
    C# tree is not on disk at harness run time (see the section note)
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
import math
import re
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional, Sequence, Tuple

SCHEMA_VERSION = 1

# ---------------------------------------------------------------------------
# Vocabulary tables (design Data Model + consumed seam verb table).
# ---------------------------------------------------------------------------

# Scenario tiers (design spec `tier` enum). Two non-cadence readiness tiers exist
# alongside the four cadence tiers; CADENCE_TIERS maps NO cadence to either, so
# neither is ever scheduled by a --cadence run:
# - `pending-fixture`: the scenario's fixture save is not committed yet; excluding
#   it prevents a terminal INVALID(staging) on every daily run self-quarantining a
#   scenario that never actually ran (M-A5 integration item 4). An operator
#   re-tiers it to its real cadence tier the moment the fixture lands.
# - `operator`: the scenario must not be picked up by a cadence run. Two reasons
#   qualify: (a) its PASS is unreachable unattended (e.g. RequiresFlight verbs
#   with no flight-entry verb - under a cadence it would only ever defer to a
#   TIMEOUT and burn boots); or (b) it is EXPENSIVE AND UNFLOWN, where a
#   systematic first-flight failure would red the sweep every night at full
#   budget x retries until someone sits down with it (B16-eve-orbit: 4,700 s
#   x `retry.policy = "once"` = ~2.6 h per night). A (b)-tiered spec carries an
#   inline PROMOTE note naming the nightly tier to restore after its first
#   green flight. It runs ONLY on an explicit `--tier operator` / `--id`
#   invocation. A `pending-operator` tag alone is non-gating; the tier is.
TIERS: Tuple[str, ...] = ("perpr", "daily", "nightly", "weekly", "pending-fixture", "operator")

# The two provisioned instance profiles (design + M-A6).
INSTANCE_PROFILES: Tuple[str, ...] = ("stock-minimal", "modded-compat")

# v1 injectedRecordings value set (design S4). Any other value is rejected;
# broad preset/corpus-scoped injection is DEFERRED to M-A4 / M-B5. "rewind-b9" is
# the one named fixture preset added ahead of that: the B9 rewindable-tree fixture
# (a committed tree with a crashed booster sibling + a Rewind-to-Separation
# RewindPoint), injected via `dotnet test --filter InjectRewindB9` for S4.1 / S1.5.
INJECTED_RECORDINGS: Tuple[str, ...] = ("none", "all-synthetic", "rewind-b9")

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
# AnswerMergeDialog, TimeJump, KscAction. The M-C1.1 follow-up added SaveGame (the M-B3
# L2/R6 persist-before-reload dependency); it was never in the RESERVED envelope, so it
# is a NEW implemented verb name. The other eleven stay RESERVED. The tuple order below
# mirrors the C# ImplementedVerbs set (TestCommandVerbs.cs) exactly.
IMPLEMENTED_SEAM_VERBS: Tuple[str, ...] = (
    "SetSetting", "StartRecording", "StopRecording", "CommitTree", "DiscardTree",
    "RecordingState", "RunTests", "LoadGame", "MissionMark", "FlushAndQuit",
    "InvokeRewind", "AnswerMergeDialog", "TimeJump", "KscAction", "SaveGame",
    # M-C2 EVA batch (design-autotest-eva-missions.md): EvaExit / EvaBoard / PlantFlag,
    # additive like SaveGame (never in the RESERVED envelope). EVA-4 added EvaChuteDeploy
    # (the kerbal personal parachute), additive in the same way. 19 total, mirroring the C#
    # TestCommandVerbs.ImplementedVerbs set exactly.
    "EvaExit", "EvaBoard", "PlantFlag", "EvaChuteDeploy",
)
RESERVED_SEAM_VERBS: Tuple[str, ...] = (
    "StartLoopPlayback", "StopPlayback", "EnterWatchMode", "SealSlot", "StashSlot",
    "FlySlot", "RouteCommand", "MissionConfig", "SimulateStockSwitchClick",
    "CrashAfterJournalPhase", "RunInvariantReport",
)

# Harness-owned, fixed Tier-C anomaly token set (design verifier 6 / N2). A
# scenario only ADDS known-benign exceptions via `allowedAnomalies`; it never
# redefines this set. Every token here is LIVE: some production `EmitAnomaly` call
# site raises it (pinned by AnomalyGroundTruthEnumerationTests).
#
# DEAD-TOKEN REMOVAL (2026-07-29). `icon-jump` used to sit in this tuple and was
# raised by NOTHING - the probe raises the icon-teleport family with
# `reason=icon-teleport` (MapRenderProbe.cs), so the token could never fire. It is
# RETIRED to ANOMALY_TOKENS_DEAD below. The removal changes no verdict by
# construction (a token no producer raises cannot be a hit), and it stops the
# gated set advertising coverage it does not have.
#
# The DRIFT ITSELF IS NOT CLOSED: ANOMALY_REASONS_RAISED_UNGATED below still lists
# NINE reasons the mod raises that nothing gates. That reconciliation is a
# per-token call (defect signal vs instrumentation signal) and stays deferred -
# see the todo-doc entry. Note what the deferral is NOT: after the settings-sidecar
# baseline only three specs arm the map tracer (S1.4, S1.6, S1.7), so widening the
# set can only move THEIR verdicts, not "every committed scenario's". Until it is
# decided, `unlisted_anomaly_reasons` REPORTS every anomaly reason seen that is not
# in this set, so the drift is visible on every run instead of silent.
ANOMALY_TOKENS: Tuple[str, ...] = (
    "line-blink", "parity-drift", "decision-vs-truth",
    "polyline-orbit-overlap", "rigid-seam-tangent-discontinuity", "ledger-vs-truth",
)

# GROUND TRUTH: every `reason=` token the mod raises from PRODUCTION code (outside
# `Source/Parsek/InGameTests/`) that ANOMALY_TOKENS does not gate. Each entry is
# (reason, producer file:line) where the producer is the DECISION site - for the
# four cutover-hardening raises that is the guard site, which calls a thin
# once-per-event MapRenderTrace wrapper that calls EmitAnomaly, so the emitted
# line is shaped exactly like a direct raise.
#
# This tuple is the decision input for the deferred reconciliation, so it is
# pinned against the C# source by AnomalyGroundTruthEnumerationTests: that test
# walks every EmitAnomaly call site under Source/Parsek (excluding InGameTests/),
# resolves the reason argument by position for both tracer signatures, and
# requires the derived set to partition EXACTLY into ANOMALY_TOKENS (all live now
# that the dead token is retired) plus this tuple. A new raise site that nobody
# gates therefore reds the harness suite instead of quietly widening the fail-open.
ANOMALY_REASONS_RAISED_UNGATED: Tuple[Tuple[str, str], ...] = (
    ("icon-teleport", "Source/Parsek/MapRenderProbe.cs:753"),
    ("icon-off-orbit", "Source/Parsek/MapRenderProbe.cs:834"),
    ("unaccounted-drawn-recording", "Source/Parsek/MapRenderProbe.cs:437"),
    ("gap-vs-retire", "Source/Parsek/MapRender/GhostRenderReconciler.cs:240"),
    ("decision-vs-old-truth", "Source/Parsek/MapRender/GhostRenderReconciler.cs:260"),
    ("clock-not-ready", "Source/Parsek/MapRender/ShadowRenderDriver.cs:316"),
    ("retire-not-held", "Source/Parsek/MapRender/ShadowRenderDriver.cs:394"),
    ("anchor-resolve-fail", "Source/Parsek/MapRender/AnchorFrameResolver.cs:87"),
    ("factory-parity", "Source/Parsek/MapRender/ShadowRenderDriver.cs:709"),
)

# RETIRED tokens: gated once, raised by nothing, REMOVED from ANOMALY_TOKENS
# (2026-07-29). Kept as a named constant for two reasons, both mechanical: the
# source-derived enumeration asserts each entry is still raised by NO producer (so
# a token that gains a producer stops being dead and has to be re-decided rather
# than silently staying out of the gate), and spec validation warns on an
# `allowedAnomalies` entry naming one (declaring a tolerance for a token the sweep
# cannot raise is inert, and the author should know).
#
# INVARIANT: this tuple and ANOMALY_TOKENS are DISJOINT. A dead token is retired
# FROM the gated set, never carried inside it.
ANOMALY_TOKENS_DEAD: Tuple[str, ...] = ("icon-jump",)

# Both tracers build their Tier-C line the same way: MapRenderTrace.EmitAnomaly ->
# EmitRaw(phase "Anomaly", "reason=" + Token(reason) + " " + details) and
# LedgerTrace.FormatAnomaly -> "phase=Anomaly ... reason=" + Token(reason). So an
# anomaly RAISE is identified by the phase marker plus the reason field - never by
# the bare token appearing somewhere in KSP.log.
ANOMALY_LINE_PHASE: str = "phase=Anomaly"
_ANOMALY_REASON_RE = re.compile(r"\breason=([^\s]+)")

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
# EVA-4 added EvaChuteDeploy: its awaitDown stage holds the FIFO head through the kerbal's
# WHOLE chuted descent (minutes of real time at 1x), so it is genuinely long-running and the
# 540 s cap + step-wait margin must govern its per-step budget like the other four.
DEFERRED_SEAM_VERBS: Tuple[str, ...] = ("RunTests", "LoadGame", "InvokeRewind", "TimeJump",
                                        "EvaChuteDeploy")

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
    # M-C2 EVA verbs (F5): each per-verb DeferralBudget governs BOTH the head-defer AND the
    # two-phase completion wait (there is ONE C# budget per verb). Without these the harness
    # step-wait would ride the 60s default + margin and could KILL a genuinely-deferring
    # PlantFlag (180s) at ~120s, converting a retryable seam TIMEOUT into a terminal KILLED.
    # None is a DEFERRED_SEAM_VERB (all under the 540s cap); they ride this per-verb dict like
    # AnswerMergeDialog / KscAction.
    "EvaExit": 120.0,
    "PlantFlag": 180.0,
    "EvaBoard": 120.0,
    # EVA-4. Unlike the three above this one IS a DEFERRED_SEAM_VERB: its awaitDown
    # stage holds the head through the kerbal's whole chuted descent (~5-6 m/s under
    # the full stock EVA canopy). 420 s covers a ~2 km opening altitude with margin and
    # stays under the 540 s cap.
    "EvaChuteDeploy": 420.0,
}

# Per-verb TAIL ROLE: what a seam verb DOES, used to decide whether it may still be
# driven after the mission step came back UNMET (design "The unmet-mission tail").
# Three roles, sitting alongside the DEFERRED / DISPATCH tables above because the
# classification is per-verb metadata like they are:
#   cleanup        teardown that MUST always run so the harness can collect and exit
#                  cleanly, whatever the mission did.
#   inert          observation / annotation only: reads state or stamps the log, never
#                  changes the game, the save, or Parsek's durable record.
#   world-mutating changes game / world / durable state: an in-world action, a career
#                  or ledger mutation, a save write, or a persisted setting.
# The UNMET-mission tail runs the CLEANUP verbs only. Inert verbs are skipped not
# because they are dangerous but because their observation is worthless on a run that
# is already terminally INVALID (see plan_unmet_mission_tail).
TAIL_ROLE_CLEANUP = "cleanup"
TAIL_ROLE_INERT = "inert"
TAIL_ROLE_WORLD_MUTATING = "world-mutating"
TAIL_ROLES: Tuple[str, ...] = (TAIL_ROLE_CLEANUP, TAIL_ROLE_INERT, TAIL_ROLE_WORLD_MUTATING)

# Every IMPLEMENTED_SEAM_VERBS entry carries an EXPLICIT role; the pairing is gated by
# a unit cell, so promoting a verb from RESERVED (or adding a new one, as SaveGame and
# the M-C2 EVA batch were) forces an explicit decision here rather than inheriting a
# default silently.
#
# Only StopRecording and FlushAndQuit are cleanup, and each earns it:
#   FlushAndQuit is the QUIT owner. Skipping it would leave KSP alive until the
#     step-wait / run-budget watchdog kills the tree, and KILLED outranks driver-INVALID
#     in classify_verdict -- the mission's own subkind would be MASKED and the attempt
#     would stop being retryable. It must always run.
#   StopRecording is the recorder's own teardown. It closes the recording so the
#     recorder flushes instead of being torn down mid-sample by the quit, and so the
#     collected KSP.log's recording markers pair. HONEST SCOPE: today EVA-4 is the ONLY
#     scenario that can ever reach this role. Six specs carry a StopRecording step
#     (EVA-1/2/3, S0.5, S0.6, EVA-4), but the first five are kind="seam" with no mission
#     step, so they never take the unmet-tail path at all; and every other autopilot
#     scenario (B1/B2/B4/B5/B6/B7, BDOCK-1, the three FORGE specs) has no StopRecording
#     step, so on THEIR unmet runs the recorder is live at quit either way. It is
#     cleanup because it is teardown that costs nothing and performs no in-world action,
#     not because the suite currently leans on it.
# Everything else is skipped on the unmet tail. Three calls deserve their reasoning:
#   CommitTree is world-mutating, NOT cleanup: it writes the failed attempt's junk tree
#     into the durable committed set and applies its resource deltas to the career. It
#     cannot buy anything back either -- an unmet mission is already terminal INVALID at
#     the classify_verdict "driver stage failed" branch, which precedes EVERY
#     save-reading verifier in that precedence chain, so the analyzer / expectations /
#     ledger-oracle verifiers are triage-only or SKIPPED on this path whether or not the
#     tree was committed.
#   DiscardTree is world-mutating too: deleting the failed attempt's recorded data would
#     destroy the exact forensics the collect-logs snapshot exists to preserve.
#   SaveGame is world-mutating because an explicit persist is not teardown. Do NOT read
#     that as protecting the FORGE fixture path: FlushAndQuit ITSELF forces a
#     GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, OVERWRITE)
#     (ParsekTestCommandAddon.FlushAndQuitImpl), which is the SAME slot all three FORGE
#     specs' SaveGame step targets, so a half-forged state is persisted either way and
#     skipping the step changes nothing about what lands on disk. Guarding the mint is
#     the harvest tool's job (harvest_bdock_station.py --expect-situation), tracked
#     separately.
SEAM_VERB_TAIL_ROLE: Dict[str, str] = {
    "SetSetting": TAIL_ROLE_WORLD_MUTATING,      # persisted Parsek setting
    "StartRecording": TAIL_ROLE_WORLD_MUTATING,  # starts the recorder / an active tree
    "StopRecording": TAIL_ROLE_CLEANUP,
    "CommitTree": TAIL_ROLE_WORLD_MUTATING,
    "DiscardTree": TAIL_ROLE_WORLD_MUTATING,
    "RecordingState": TAIL_ROLE_INERT,           # read-only probe
    "RunTests": TAIL_ROLE_WORLD_MUTATING,        # in-game batch mutates the save
    "LoadGame": TAIL_ROLE_WORLD_MUTATING,        # replaces the loaded world
    "MissionMark": TAIL_ROLE_INERT,              # stamps one log line, nothing else
    "FlushAndQuit": TAIL_ROLE_CLEANUP,
    "InvokeRewind": TAIL_ROLE_WORLD_MUTATING,
    "AnswerMergeDialog": TAIL_ROLE_WORLD_MUTATING,
    "TimeJump": TAIL_ROLE_WORLD_MUTATING,
    "KscAction": TAIL_ROLE_WORLD_MUTATING,       # spends funds / hires / upgrades
    "SaveGame": TAIL_ROLE_WORLD_MUTATING,
    "EvaExit": TAIL_ROLE_WORLD_MUTATING,         # irreversible in-world action
    "EvaBoard": TAIL_ROLE_WORLD_MUTATING,
    "PlantFlag": TAIL_ROLE_WORLD_MUTATING,
    "EvaChuteDeploy": TAIL_ROLE_WORLD_MUTATING,  # the EVA-4 flight-1 pair, with EvaExit
}

# ---------------------------------------------------------------------------
# Post-mission GATING role (EVA-4 fail-open closure, 2026-07-26).
#
# A DIFFERENT axis from SEAM_VERB_TAIL_ROLE above. That one answers "may the harness
# still DRIVE this verb after the mission came back UNMET?"; this one answers "on a
# MISSION-OK run, does this verb's own verdict GATE the result?".
#
# WHY IT EXISTS. On an autopilot driver run.py deliberately carves post-mission seam
# steps out of driverValidity, on the stated grounds that "a good flight Parsek then
# failed to record is a PARSEK-FAIL(expectation), NOT a driver-INVALID a retry would
# paper over". That reasoning is right for the RECORDING verbs, and the carve-out is
# kept for them. It was WRONG as a blanket rule, and EVA-4 flight 3 (2026-07-25) is
# the proof: the kerbal's canopy was cut mid-descent, the kerbal died, and the ONE
# channel that observed it - the EvaChuteDeploy step's own `eva-chute-kerbal-lost`
# terminal - was recorded as `verdict=ERROR met=false` and then consulted by NO gate.
# driverValidity reported PASS next to `allExpectedMet: false`. The run only red'd
# because the scenario's author happened to have written a forbidden-`[Parsek][ERROR]`
# regex and three required log tokens; delete any of those from the spec and a run
# that killed its own subject reports PASS.
#
# THE SPLIT. A post-mission step is `outcome` when its verdict is a statement about
# the WORLD's physical outcome that no other verifier re-derives, and `recording`
# when its verdict is a statement about PARSEK (or harness plumbing), which the
# analyzer / expectations / ledger chain already owns.
#   outcome   -> gates. Its failure means the FLIGHT failed after the handoff.
#   recording -> non-gating, exactly as before. Preserves the original carve-out.
# The four M-C2 EVA verbs are the whole outcome set today: each one's OK/ERROR is a
# claim about a kerbal's in-world state (left the pod / boarded / flag planted /
# canopy opened and landed alive). Everything else is Parsek or plumbing.
#
# The pairing is gated by a unit cell, so promoting a RESERVED verb or adding a new
# one forces an explicit decision here instead of inheriting a default silently.
POST_MISSION_ROLE_OUTCOME = "outcome"
POST_MISSION_ROLE_RECORDING = "recording"
POST_MISSION_ROLES: Tuple[str, ...] = (POST_MISSION_ROLE_OUTCOME, POST_MISSION_ROLE_RECORDING)

SEAM_VERB_POST_MISSION_ROLE: Dict[str, str] = {
    "SetSetting": POST_MISSION_ROLE_RECORDING,
    "StartRecording": POST_MISSION_ROLE_RECORDING,
    "StopRecording": POST_MISSION_ROLE_RECORDING,
    "CommitTree": POST_MISSION_ROLE_RECORDING,
    "DiscardTree": POST_MISSION_ROLE_RECORDING,
    "RecordingState": POST_MISSION_ROLE_RECORDING,
    "RunTests": POST_MISSION_ROLE_RECORDING,     # in-game batch = a Parsek claim
    "LoadGame": POST_MISSION_ROLE_RECORDING,     # harness plumbing
    "MissionMark": POST_MISSION_ROLE_RECORDING,  # stamps one log line
    "FlushAndQuit": POST_MISSION_ROLE_RECORDING,
    "InvokeRewind": POST_MISSION_ROLE_RECORDING,      # a Parsek feature under test
    "AnswerMergeDialog": POST_MISSION_ROLE_RECORDING,  # ditto
    "TimeJump": POST_MISSION_ROLE_RECORDING,
    "KscAction": POST_MISSION_ROLE_RECORDING,    # career mutation, ledger-oracle territory
    "SaveGame": POST_MISSION_ROLE_RECORDING,
    "EvaExit": POST_MISSION_ROLE_OUTCOME,        # "the kerbal is out and clear"
    "EvaBoard": POST_MISSION_ROLE_OUTCOME,       # "the kerbal is aboard"
    "PlantFlag": POST_MISSION_ROLE_OUTCOME,      # "the flag is in the ground"
    "EvaChuteDeploy": POST_MISSION_ROLE_OUTCOME,  # "the canopy opened and the kerbal landed alive"
}


def post_mission_step_gates(cmd: str) -> bool:
    """True iff a post-mission seam step running ``cmd`` gates the run result.

    Unknown / empty verbs read False (non-gating): an unrecognised verb is a spec
    fault the validator already rejects, and inventing a gate for it here would red
    runs on a vocabulary miss rather than on an outcome."""
    return SEAM_VERB_POST_MISSION_ROLE.get(str(cmd or "")) == POST_MISSION_ROLE_OUTCOME


# The seam's own verdict families, and what an unmet post-mission OUTCOME step means.
# The C# executors already draw the line this needs: a REFUSAL that had NO side effect
# rides "REJECTED" (`no-crew`, `kerbal-not-aboard`, `not-near-target`, `unknown-target`,
# `eva-chute-unavailable` ...), while a real terminal after the verb acted rides "ERROR"
# (`eva-exit-timeout`, `board-timeout`, `eva-chute-kerbal-lost` ...). Only the second
# family is a FLIGHT OUTCOME.
#
# Without this split every one of the ~30 terminals the four outcome verbs can emit
# collapses to "the flight failed after handoff": a typo in a post-mission EvaBoard's
# targetPid would report PARSEK-FAIL(mission-outcome), never be retried, and be filed
# against the mod - while the SAME typo on a pre-mission step reports INVALID(driver-arg)
# and retries once. Same fault, same classification, wherever it sits in the step list.
SEAM_VERDICT_OUTCOME_TERMINAL = "ERROR"


def classify_post_mission_outcome_miss(step: "StepOutcome") -> Tuple[bool, str]:
    """Classify an UNMET post-mission outcome step as (is_flight_outcome, driver_subkind).

    ``(True, "")``  -> a genuine flight outcome; the caller reds PARSEK-FAIL(mission-outcome).
    ``(False, sk)`` -> a refusal / tooling / never-answered miss; the caller treats it as a
    driver-stage failure with subkind ``sk``, exactly as it would pre-mission.

    A ``msg`` the M-C1 refusal table recognises wins over the verdict family, so a verb
    that reports a known gate decline is classified by its REASON however it spells its
    verdict."""
    refusal = classify_seam_refusal_subkind(getattr(step, "msg", ""))
    if refusal:
        return False, refusal
    verdict = getattr(step, "verdict", None)
    if verdict is None:
        return False, "driver-stage"          # never answered
    if verdict == "TIMEOUT":
        return False, "seam-timeout"          # the seam never reached a terminal
    if verdict == SEAM_VERDICT_OUTCOME_TERMINAL:
        return True, ""
    return False, "driver-verdict-mismatch"   # REJECTED, or an unexpected verdict


def first_unmet_post_mission_outcome(
    steps: Sequence["StepOutcome"], mission_step_id: Optional[str]
) -> Optional["StepOutcome"]:
    """The first UNMET post-mission OUTCOME step, or None.

    ``mission_step_id`` is the mission-kind step's harness id; steps with a strictly
    GREATER id are post-mission. None (a seam-only driver with no mission step) reads
    None: on that driver every step already gates through ``all_expected_met``, so
    re-gating here would double-count and change nothing.

    Steps the harness never drove are not in ``steps`` at all (the unmet-mission tail
    records them separately), so a skipped tail cannot manufacture an outcome miss."""
    if mission_step_id is None:
        return None
    mid = str(mission_step_id)
    for outcome in steps:
        if str(outcome.step_id) <= mid:
            continue
        if outcome.met:
            continue
        if post_mission_step_gates(outcome.cmd):
            return outcome
    return None


# The spec-level opt-out, read off [driver]. DEFAULT TRUE = the safe behaviour (skip
# the world-mutating tail after an unmet mission); a spec sets it false only to opt
# back into the pre-2026-07-25 drive-everything tail, and must say why.
SKIP_TAIL_ON_UNMET_MISSION_KEY = "skipTailOnUnmetMission"
SKIP_TAIL_ON_UNMET_MISSION_DEFAULT = True

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


# ---------------------------------------------------------------------------
# Anti-vacuity gate for in-game batch contracts (the "GREEN over ZERO executed
# tests" class, proved live on B10-career-passive-safety 2026-07-26).
#
# THE DEFECT THIS EXISTS FOR. B10 declared `RunTests category="RecordingInvariants"`
# and gated the batch on the single contract `BATCH_COMPLETE v1 .* failed=0\b`.
# Both RecordingInvariants tests are Scene = FLIGHT; B10's fresh-career fixture has
# zero VESSEL nodes, so TestCommandLoadGame.DecideLoadRoute takes the
# NoVesselSpaceCenter route and the batch runs at the Space Center. Every test was
# scene-eligibility skipped and the runner emitted
#   BATCH_COMPLETE v1 total=2 passed=0 failed=0 skipped=2 category=... scene=SPACECENTER
# which SATISFIES `failed=0` - a shipped daily scenario reading GREEN while executing
# ZERO tests, indefinitely. `failed=0` alone cannot distinguish "everything passed"
# from "nothing ran".
#
# THE RULE. A spec that OWNS a batch (a RunTests step or an [driver.autorun] block)
# must carry a logContracts.required pin that a VACUOUS batch cannot satisfy. This is
# NOT checked syntactically (a syntactic "must contain total=" rule is itself a
# tautology - `total=\d+` pins nothing). It is checked by CONSTRUCTION: synthesize
# every BATCH_COMPLETE line a vacuous batch could emit and confirm the spec's own
# patterns REJECT each one. A contract that accepts any of them is rejected at
# spec-validation time, before KSP is ever launched.
#
# WHAT COUNTS AS VACUOUS. The runner's tally always satisfies
# total == passed + failed + skipped (Passed/Failed/Skipped are recounted over the
# same `considered` set, InGameTestRunner.RunBatch). So `passed == 0 and failed == 0`
# forces `total == skipped`: the empty batch (all zero) and the all-skipped batch are
# the SAME one-parameter family, enumerated over `skipped`.
#
# EXACTLY WHAT THE GATE GUARANTEES, and the three ways it could be dodged before the
# 2026-07-26 review hardening closed them. The probe family covers ONE batch driven by
# ONE named category, so the guarantee only holds when the spec drives exactly that:
#   1. TWO RunTests steps - only the FIRST category was probed (and `batch_owners`
#      counts 1 for any n > 0), so a whole-tally pin on the first left the second
#      batch entirely ungated. CLOSED by SINGLE_BATCH_SELECTOR_RULE below: more than
#      one RunTests step is an ERROR.
#   2. A MULTI-category selector ("all" or "A,B") - the run-time gate is the
#      `category=multi:<n>` AGGREGATE, whose tally sums every constituent, so an
#      aggregate pin cannot express "category B was not vacuous" and a
#      single-constituent pin trivially rejects the OTHER constituent's probes for
#      the wrong reason (category-token mismatch). Per-constituent non-vacuity is NOT
#      expressible on this contract surface at all. CLOSED FAIL-CLOSED by
#      SINGLE_BATCH_SELECTOR_RULE: a batch-owning spec must name exactly one
#      category; a multi or absent selector is an ERROR.
#   3. TWO BATCH_COMPLETE patterns satisfied by DIFFERENT log lines -
#      `evaluate_expectations` re.searches each required pattern over the whole log
#      independently, so ANDing them against one synthesized probe was too weak.
#      CLOSED in batch_contract_vacuity_gap: discrimination now requires ONE single
#      pattern to reject the entire vacuous family.
# What the gate still does NOT do, stated plainly: it blocks only `passed == 0`. The
# `passed=[1-9][0-9]*` form its own error message recommends accepts 1-of-42. That
# form is for a spec whose exact split has not been measured yet; pin the whole tally
# as soon as a live run gives you one.
# ---------------------------------------------------------------------------

# The opt-out lives in [expectations.logContracts] beside the contract it exempts.
# It is deliberately two keys: a bare bool could drift in unexplained, so the reason
# is REQUIRED and a spec author has to write down why the batch cannot be pinned.
BATCH_VACUITY_OPT_OUT_KEY = "batchVacuityOptOut"
BATCH_VACUITY_OPT_OUT_REASON_KEY = "batchVacuityOptOutReason"

# The shape the anti-vacuity guarantee is defined over. Quoted verbatim in the
# validate_spec errors so a rejected spec author reads the reason, not just the rule.
SINGLE_BATCH_SELECTOR_RULE = (
    "a batch-owning spec must drive exactly ONE RunTests step naming exactly ONE "
    "category: the vacuity probe is built for a single named category, and neither a "
    "second batch nor a multi-category aggregate can be pinned non-vacuously on the "
    "current contract surface")

# ---------------------------------------------------------------------------
# R5: the ISOLATED batch argument.
#
# `RunTests` gained an optional `isolated` arg (and `[driver.autorun]` an optional
# `isolated` bool) selecting InGameTestRunner's OTHER batch entry point. See
# derive_batch_tally for what changes inside the runner. What matters HERE is that
# the flag silently changes which tests execute, so every way of getting it wrong
# must be an ERROR at spec-validation time, before KSP is launched:
#
#   - a value that is not the string "true"/"false". The wire encoding is
#     run.py::encode_value == str(value), so the TOML bool `isolated = true`
#     travels as the token `isolated=True`, which the C# parse REJECTS
#     (TestCommandRunTests.TryParseIsolatedArg is case-sensitive by design). Left
#     unchecked the author would burn a full KSP boot to learn about a capital T.
#   - the arg on a step that is not RunTests. No other verb reads it, so it would
#     be silently inert -- and inert in the direction of "the author thinks the
#     batch is isolated and it is not", which produces an all-skipped tally that
#     reads as a Parsek regression.
#   - the key one TOML table too high (`driver.steps[i].isolated` instead of
#     `...args.isolated`, or `[driver]`/spec-root instead of `[driver.autorun]`).
#     Same silent-inert failure, same wrong direction. This mirrors the
#     misplaced-key guards already written for skipTailOnUnmetMission and
#     batchVacuityOptOut, both added after the same class of silent drop.
#   - `[driver.autorun] isolated` with no `tests`: nothing auto-runs, so the arm
#     is inert. The C# side WARNs on this at startup; a spec can be checked
#     statically, so here it is an error.
#
# The flag is deliberately NOT part of the selector string. See
# TestRunnerShortcut.EnvIsolatedVar for why a prefix would break the anti-vacuity
# probe family.
# ---------------------------------------------------------------------------

BATCH_ISOLATED_KEY = "isolated"

# The only two tokens the seam's TryParseIsolatedArg accepts, as TOML strings.
BATCH_ISOLATED_VALUES: Tuple[str, ...] = ("true", "false")


def spec_batch_isolated(spec: Dict) -> bool:
    """True when ``spec``'s batch runs on the ISOLATED entry point (R5).

    Resolved over EVERY RunTests step, not just the first, then the
    ``[driver.autorun]`` block. Returns True when ANY of them is isolated.

    The first cut read only the FIRST RunTests step, on the reasoning that
    ``SINGLE_BATCH_SELECTOR_RULE`` permits only one. An adversarial review showed
    that is false in one committed, documented shape: a spec that takes the
    ``batchVacuityOptOut`` escape waives that rule, so

        steps[2] = RunTests category="X"                    # ordinary
        steps[3] = RunTests category="X" isolated="true"    # the REAL batch

    validated with zero errors while this function answered False -- so run.py
    logged ``batchIsolated=False``, ``CommittedBatchTallySourceSyncTests``
    validated the pin against the ORDINARY derivation (weaker, in the unsafe
    direction), and neither wiring-group class saw it. Answering True if ANY step
    is isolated fails toward the STRICTER derivation, and ``validate_spec``
    additionally rejects a spec whose RunTests steps disagree, so the ambiguous
    shape cannot be committed at all.
    """
    driver = (spec.get("driver", {}) or {})
    for step in (driver.get("steps", []) or []):
        if (step or {}).get("cmd") != "RunTests":
            continue
        if (((step or {}).get("args", {}) or {}).get(BATCH_ISOLATED_KEY)) == "true":
            return True
    autorun = driver.get("autorun")
    if not isinstance(autorun, dict):
        return False
    return autorun.get(BATCH_ISOLATED_KEY) is True

# Every scene token InGameTestRunner can print (HighLogic.LoadedScene.ToString()).
# The B10 defect WAS a scene surprise (the spec assumed FLIGHT, the fixture routed to
# SPACECENTER / earlier MAINMENU), so the probe sweeps them all: a contract that pins
# `scene=FLIGHT` rejects the off-scene probes by scene mismatch, which is exactly the
# behavior wanted - an off-scene vacuous batch reds.
_BATCH_PROBE_SCENES: Tuple[str, ...] = (
    "FLIGHT", "SPACECENTER", "TRACKSTATION", "EDITOR", "MAINMENU",
    "LOADING", "LOADINGBUFFER", "SETTINGS", "CREDITS", "PSYSTEM",
)

# Bounded enumeration of the all-skipped family. 256 comfortably exceeds every
# in-game category (the largest today is Logistics at 47), and any integer literal
# the spec's own patterns mention is added on top, so a pattern that pins a total
# ABOVE the bound is still probed at exactly the value it pins.
_BATCH_PROBE_MAX_SKIPPED = 256

# DECOY BATCH_COMPLETE-shaped lines that a KSP.log can carry REGARDLESS of what the
# driven batch did. `evaluate_expectations` re.searches each required pattern over the
# WHOLE log, so a pattern satisfied by one of these is satisfied even when the real
# batch was vacuous - which is why the discrimination test below must reject these too,
# not only the synthesized vacuous tallies.
#
# There is exactly one today: LogContractTests.BatchCompleteFormatValid (in-game
# category "LogContracts") pushes this literal through ParsekLog.Info to pin the H3
# format. It asserts the exact string, so it is stable, not a moving target. ANY new
# code path that prints a BATCH_COMPLETE-shaped line outside InGameTestRunner's batch
# end MUST be added here or the gate silently weakens.
_BATCH_DECOY_BODIES: Tuple[str, ...] = (
    "BATCH_COMPLETE v1 total=42 passed=40 failed=1 skipped=1 "
    "category=RecordingInvariants scene=FLIGHT",
)

# The probes carry the real KSP.log line prefix so a contract that anchors on
# "\[Parsek\]\[INFO\]\[TestRunner\]" is exercised as written; a BARE probe is swept
# too so a "^BATCH_COMPLETE"-anchored contract is not silently exempted.
_BATCH_PROBE_PREFIX = "[LOG 00:00:00.000] [Parsek][INFO][TestRunner] "


def _batch_probe_skipped_counts(patterns: Sequence[str]) -> List[int]:
    counts = set(range(0, _BATCH_PROBE_MAX_SKIPPED + 1))
    for pat in patterns:
        for lit in re.findall(r"\d+", str(pat)):
            try:
                counts.add(int(lit))
            except ValueError:  # pragma: no cover - findall only yields digits
                pass
    return sorted(counts)


def _batch_probe_categories(selector: Optional[str]) -> Tuple[str, ...]:
    """Category token the GATING BATCH_COMPLETE line carries for ``selector``.

    ONE token, always. A batch-owning spec is required by ``validate_spec`` to name
    exactly one category on exactly one RunTests step (see
    ``SINGLE_BATCH_SELECTOR_RULE`` and the module note above for why), so the probe
    family is single-category by construction. A None/empty selector is the RunAll shape the
    runner stamps as ``all``; ``validate_spec`` rejects THAT on a batch-owning spec
    too, and the token is kept here only so a direct caller of the pure function
    gets a defined answer.
    """
    if not selector:
        return ("all",)
    return (selector.strip(),)


def vacuous_batch_complete_probes(
    selector: Optional[str], patterns: Sequence[str]
) -> List[str]:
    """Every KSP.log line a VACUOUS batch for ``selector`` could emit.

    Vacuous = zero tests EXECUTED: ``passed=0 failed=0``, which (see the module note
    above) forces ``total == skipped``. skipped=0 is the empty batch (the category
    matched nothing at all); skipped=N is the all-skipped batch (scene-ineligible or
    AllowBatchExecution=false). Pure; no I/O.
    """
    out: List[str] = []
    counts = _batch_probe_skipped_counts(patterns)
    for cat in _batch_probe_categories(selector):
        for scene in _BATCH_PROBE_SCENES:
            for n in counts:
                body = ("BATCH_COMPLETE v1 total=%d passed=0 failed=0 skipped=%d "
                        "category=%s scene=%s" % (n, n, cat, scene))
                out.append(_BATCH_PROBE_PREFIX + body)
                out.append(body)
    return out


def batch_contract_vacuity_gap(
    required_patterns: Sequence[str], selector: Optional[str]
) -> Optional[str]:
    """A vacuous BATCH_COMPLETE line this contract would ACCEPT, or None.

    THE DISCRIMINATION TEST IS PER-PATTERN, NOT PER-LINE, and that is load-bearing.
    ``evaluate_expectations`` applies each required pattern with ``re.search`` over
    the WHOLE log body INDEPENDENTLY - two patterns may be satisfied by two
    DIFFERENT lines. So it is NOT enough that no single probe satisfies every
    pattern at once: the log of a vacuous run contains other BATCH_COMPLETE-shaped
    text (``LogContractTests.BatchCompleteFormatValid`` pushes a literal
    ``BATCH_COMPLETE v1 total=42 passed=40 failed=1 skipped=1 ...`` through
    ParsekLog.Info), so a second pattern can be satisfied by a decoy while the first
    is satisfied by the vacuous tally. The contract therefore detects vacuity IFF
    at least ONE required pattern rejects the ENTIRE vacuous family on its own.

    Returns a probe line the contract would let through (so the error message can
    quote the exact tally that would have passed) or None when some single pattern
    discriminates.

    Patterns that do not name BATCH_COMPLETE are IGNORED here: they are satisfied by
    other log lines that a vacuous batch still emits, so they can never be the thing
    that reds an empty tally.
    """
    batch_patterns = [str(p) for p in (required_patterns or []) if "BATCH_COMPLETE" in str(p)]
    if not batch_patterns:
        return "<no logContracts.required pattern names BATCH_COMPLETE>"
    compiled = []
    for pat in batch_patterns:
        try:
            compiled.append(re.compile(pat))
        except re.error:
            # An invalid regex is already an expectations-evaluation mismatch at run
            # time; treat it as non-discriminating here rather than crashing validation.
            continue
    if not compiled:
        return "<every BATCH_COMPLETE pattern is an invalid regex>"
    probes = vacuous_batch_complete_probes(selector, batch_patterns)
    # A vacuous run's log carries the vacuous tally AND any batch-independent decoy,
    # and a pattern satisfied by EITHER is satisfied. So the discriminating pattern
    # must reject the union.
    decoys = [p for body in _BATCH_DECOY_BODIES
              for p in (_BATCH_PROBE_PREFIX + body, body)]
    admissible = probes + decoys
    for rx in compiled:
        if not any(rx.search(line) is not None for line in admissible):
            # This ONE pattern rejects every line a vacuous run could offer it, so no
            # combination of log lines can satisfy the contract over a vacuous batch.
            return None
    # Every pattern admits at least one vacuous tally. Quote the strongest evidence:
    # a probe the WHOLE contract accepts if one exists, else the first vacuous line
    # any single pattern accepts (the others being satisfiable by other log lines).
    for probe in probes:
        if all(rx.search(probe) is not None for rx in compiled):
            return probe
    for rx in compiled:
        for line in admissible:
            if rx.search(line) is not None:
                return ("%s  [accepted by required pattern %r; every OTHER "
                        "BATCH_COMPLETE pattern here can be satisfied by a DIFFERENT "
                        "log line, so the contract as a whole does not reject it]"
                        % (line, rx.pattern))
    return None  # pragma: no cover - unreachable: the loop above found a match


# ---------------------------------------------------------------------------
# Batch-tally SOURCE SYNC (the second half of the anti-vacuity rule).
#
# THE GAP THIS CLOSES. The anti-vacuity gate above forces a batch-owning spec to
# pin its tally WHOLE (`total=N passed=P failed=0 skipped=S`). Nothing then keeps
# those numbers in step with the C# they describe: `total` is the count of
# `[InGameTest(Category = "X")]` methods, and `P`/`S` follow from each test's
# `Scene` / `AllowBatchExecution` attributes. A developer adding one test to
# Missions / Periodicity / GameActionsHealth / RouteRewindTimeline /
# RecordingInvariants / GhostPlayback moves `total` by one and silently reds that
# category's daily scenario on its NEXT NIGHTLY RUN, with no local signal at all
# -- the failure surfaces hours later, in a different process, as a logContract
# mismatch on a spec they never opened.
#
# WHAT IS DERIVABLE, AND WHAT IS NOT. `RunTests` drives
# InGameTestRunner.RunCategory, which applies exactly two filters in this order:
#   1. FilterSceneEligibleBatchCandidates(scene) -- marks Skipped every test whose
#      RequiredScene is neither AnyScene nor the scene the batch runs in;
#   2. PrepareBatchExecution -- marks Skipped every REMAINING test declaring
#      AllowBatchExecution = false.
# Both mark Skipped, and `total` is `allTests.Count(Status != NotRun)` over the
# single-category batch, so total == sceneSkipped + batchSkipped + executable.
# The order matters for ATTRIBUTION (a test that is both scene-ineligible AND
# batch-disabled lands in bucket 1 only, never counted twice), which is why the
# derivation applies them in the same order the runner does.
#
# `total` is therefore EXACT. `skipped` is only a LOWER BOUND: a test that clears
# both filters can still self-skip at run time via InGameAssert.Skip on live
# fixture state (mode != CAREER, a non-stock body graph, a missing vessel), and no
# static read of the source can predict that. L1-passive-sandbox is the standing
# proof -- it pins skipped=3 over a category whose attributes force ZERO skips,
# because SANDBOX mode is precisely its subject. So the checks are:
#   total    == derived total                       (exact; a rename or an added
#                                                    test reds here)
#   skipped  >= sceneSkipped + batchSkipped        (attribute floor)
#   passed+failed <= executable                    (attribute ceiling)
#   passed+failed+skipped == total                 (the runner's own invariant)
# A pin using a regex class rather than a literal for a token simply leaves that
# token unchecked (no committed spec writes one today); `total=` is required as a
# LITERAL by the sweep, so the load-bearing check always applies.
#
# THE RESIDUAL, STATED. `skipped` being a floor and `passed+failed` a ceiling
# leaves a near-vacuous pin EXPRESSIBLE: `total=42 passed=1 failed=0 skipped=41`
# satisfies the exact total, the floor, the ceiling and the runner's own sum, and
# #1353's anti-vacuity probes only cover the `passed=0 failed=0` family, so
# nothing here reds it. Static analysis cannot close that: run-time
# `InGameAssert.Skip` is unbounded and a legitimate spec (L1-passive-sandbox)
# pins a skipped count its attributes do not force. Only a MEASURED tally off a
# live run distinguishes the two, which is why every committed pin carries one.
#
# TWO FAILURE MODES, TWO CHECKS. A form that was RECOGNISED but whose
# Category/Scene could not be resolved is reported by
# unresolved_ingame_declarations. A form NEVER RECOGNISED at all would simply be
# absent, shrinking a category total silently -- the one way this gate fails
# open. unclaimed_ingame_attribute_tokens is the second check: it re-scans for
# every attribute-position spelling of the name Roslyn accepts and reports the
# ones the strict parse did not claim.
#
# WHY A TEST-SUITE GATE AND NOT validate_spec. validate_spec is pure and runs
# inside run.py against a PROVISIONED instance, where the C# tree need not exist
# (the harness gates a shipped DLL, not a checkout). Reading the source is
# therefore the test suite's job; everything decided here is pure over strings so
# the sweep is a thin file-reading shell over these functions.
# ---------------------------------------------------------------------------

# The InGameTestAttribute.AnyScene sentinel ((GameScenes)(-1), "runs in any
# scene"). Modelled as None rather than -1 so a scene comparison against a real
# scene NAME can never accidentally succeed.
INGAME_ANY_SCENE: Optional[str] = None


@dataclass(frozen=True)
class InGameTestDecl:
    """One `[InGameTest(...)]` attribute as the runner's DiscoverTests would see it.

    ``scene`` is the bare enum member name ("FLIGHT") or ``INGAME_ANY_SCENE``
    (None) for a declaration with no ``Scene =`` argument, mirroring the
    attribute's own default. ``origin`` is "<file>:<line> <Member>" and exists
    only so a mismatch message can name the declarations it counted.

    ``restore_baseline`` is ``RestoreBatchFlightBaselineAfterExecution``, which
    defaults FALSE (the C# property has no initializer, unlike
    ``AllowBatchExecution``). It is the second admission input, and it matters only
    on the ISOLATED batch path: ``PrepareBatchExecutionIncludingFlightRestore``
    admits ``allow_batch OR restore_baseline`` where the ordinary
    ``PrepareBatchExecution`` admits ``allow_batch`` alone. Declared LAST so the
    positional constructor stays source-compatible.
    """

    category: str
    scene: Optional[str]
    allow_batch: bool
    origin: str = ""
    restore_baseline: bool = False


# The selector token InGameTestRunner.RunAll stamps as currentBatchSelector, and
# therefore the `category=` a RunAll batch prints. It is NOT a C# category: its
# batch spans EVERY declaration in the assembly (RunAll scene-filters `allTests`
# rather than a Where(t => t.Category == x) slice), which is how derive_batch_tally
# treats it. A declaration literally categorised "all" would be ambiguous with it;
# the sweep asserts none exists.
INGAME_RUNALL_CATEGORY = "all"

# `const string Foo = "bar"` -- the `Category = <const>` indirection
# RouteRewindTimelineRuntimeTests uses. Matched on the MASKED text (so a
# commented-out const is invisible) with the value read back off the ORIGINAL
# text at the captured span.
#
# Resolution is FILE-FLAT and LAST-WINS: consts are collected into one per-file
# dict with no class/namespace scoping, so two nested types in one file each
# declaring `const string Category` would resolve both to the second value. The
# tree has const-name collisions but none in a `Category =` position, and the
# `<unresolved:...>` marker only covers a name that is absent entirely, not one
# that is shadowed. If a file ever needs two differently-valued Category consts,
# scope this by enclosing type rather than trusting the flat dict.
_CS_CONST_STRING_RE = re.compile(
    r"\bconst\s+string\s+(?P<name>[A-Za-z_]\w*)\s*=\s*\"(?P<val>[^\"]*)\"")

# One ELEMENT of an attribute section, anchored at the element's start: an
# optional attribute target (`method:`), an optional namespace/type qualifier, the
# attribute name with C#'s optional `Attribute` suffix sugar, and then either its
# argument list or the end of the element (a bare `[InGameTest]`, legal C# taking
# every default including Category = "General" -- counted, not silently dropped).
#
# Anchoring at an element start is what keeps ORDINARY CODE out. An indexer whose
# subscript starts with a similarly named type (`lookup[InGameTestRunner.Tag]`,
# `map[InGameTestAttribute.Something]`) forms one element whose name is `Tag` /
# `Something`, so the trailing `(`-or-end requirement rejects it -- the same job
# the old `\[\s*InGameTest\s*(?:\(|\])` did by demanding the name sit immediately
# after `[`, but without also rejecting the four legal spellings Roslyn accepts
# (see unclaimed_ingame_attribute_tokens).
#
# No `^` anchor: this runs via re.match(mask, pos, endpos), which already anchors
# at pos, and `^` would additionally demand a LINE start there (see _CS_ASSIGN_RE).
# `$` does honour endpos, and is what recognises the bare form.
_CS_INGAME_ELEMENT_RE = re.compile(
    r"\s*(?:[A-Za-z_]\w*\s*:\s*)?"                  # [method: ...] target
    r"(?:global::)?(?:[A-Za-z_]\w*\s*\.\s*)*"       # Parsek.InGameTests. qualifier
    r"(?P<name>InGameTest(?:Attribute)?)\s*(?:(?P<open>\()|$)")

# Every spelling of the attribute NAME the C# compiler binds to
# InGameTestAttribute, for the RECOGNITION-completeness scan. `Attribute` is C#'s
# own attribute-name sugar ([InGameTest] and [InGameTestAttribute] are the same
# attribute), and the `\b` on both sides is what keeps InGameTestRunner /
# InGameTestInfo / InGameTests out.
_CS_INGAME_NAME_RE = re.compile(r"\bInGameTest(?:Attribute)?\b")

_CS_BRACKET_RE = re.compile(r"[\[\]]")


def _matching_bracket(mask: str, open_idx: int) -> int:
    """Offset of the `]` closing the `[` at ``open_idx``, or len(mask) when the
    source is truncated. Nesting-aware."""
    depth = 0
    for i in range(open_idx, len(mask)):
        c = mask[i]
        if c == "[":
            depth += 1
        elif c == "]":
            depth -= 1
            if depth == 0:
                return i
    return len(mask)


def _iter_ingame_attribute_uses(mask: str):
    """Yield ``(name_start, name_end, open_paren, section_close)`` for every
    attribute-position `InGameTest` use in ``mask``.

    Walks each balanced `[...]` region, splits it into top-level elements (the
    existing ``_split_arg_spans``, which already ignores commas nested in
    ``()``/``[]``/``{}`` and, running on the mask, commas inside strings), and
    matches each element against ``_CS_INGAME_ELEMENT_RE``. ``open_paren`` is None
    for a bare attribute. ``section_close`` is the region's `]`, which is where the
    member-name lookup must resume: an attribute written alongside others in one
    bracket has more text after its own `)`.
    """
    for bm in _CS_BRACKET_RE.finditer(mask):
        if bm.group() != "[":
            continue
        open_idx = bm.start()
        close_idx = _matching_bracket(mask, open_idx)
        for a, b in _split_arg_spans(mask, open_idx + 1, close_idx):
            em = _CS_INGAME_ELEMENT_RE.match(mask, a, b)
            if em is None:
                continue
            name_start, name_end = em.span("name")
            open_paren = em.start("open") if em.group("open") else None
            yield name_start, name_end, open_paren, close_idx


_CS_STRING_LITERAL_RE = re.compile(r"^\"((?:[^\"\\]|\\.)*)\"$")
_CS_SCENE_MEMBER_RE = re.compile(r"^(?:global::)?GameScenes\.([A-Za-z_]\w*)$")
# No `^` anchor: this is used with re.match(mask, pos, endpos), which already
# anchors at pos, and `^` would additionally demand a line start there.
_CS_ASSIGN_RE = re.compile(r"\s*(?P<name>[A-Za-z_]\w*)\s*=\s*")
_CS_MEMBER_NAME_RE = re.compile(r"([A-Za-z_]\w*)\s*\(")


def _mask_csharp_noise(text: str) -> str:
    """Blank comment bodies and string/char literal INTERIORS, preserving length.

    The result is positionally identical to ``text`` (newlines survive, so line
    numbers do too), which lets every structural scan below run on the mask while
    values are read back off the original at the same offsets. Masking is what
    makes the parse immune to the two traps a naive regex hits: an attribute-shaped
    string inside a ``Description =`` value (RuntimeTests has a Description that
    literally contains "[InGameTest]") and a commented-out attribute. Quotes
    themselves are KEPT so the argument splitter still sees literal boundaries.
    """
    out = list(text)
    n = len(text)

    def blank(a: int, b: int) -> None:
        for k in range(max(a, 0), min(b, n)):
            if out[k] != "\n":
                out[k] = " "

    i = 0
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if c == "/" and nxt == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
        elif c == "/" and nxt == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
        elif c == "@" and nxt == "\"":
            # Verbatim string: no backslash escapes, "" is one literal quote.
            j = i + 2
            while j < n:
                if text[j] == "\"":
                    if j + 1 < n and text[j + 1] == "\"":
                        j += 2
                        continue
                    break
                j += 1
            blank(i + 2, j)
            i = min(j + 1, n)
        elif c == "\"" or c == "'":
            j = i + 1
            while j < n and text[j] != c and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            blank(i + 1, j)
            i = min(j + 1, n)
        else:
            i += 1
    return "".join(out)


def _split_arg_spans(mask: str, start: int, end: int) -> List[Tuple[int, int]]:
    """Split an attribute argument list into top-level `(a, b)` spans.

    Runs on the MASK, so a comma inside a ``Description`` string is already blank
    and cannot split an argument. Nested ``()``/``[]``/``{}`` are tracked so an
    argument like ``Scene = (GameScenes)(-1)`` arrives at ``_resolve_scene`` as ONE
    span instead of being split at a comma it does not contain. Keeping it whole is
    all this does: ``_resolve_scene`` recognises ``GameScenes.X`` and the
    ``AnyScene`` member by name, so a raw cast still resolves to
    ``<unresolved:...>`` and reds the sweep. That is the intended outcome (an
    unmodelled form must be reported), not a gap in this splitter.
    """
    spans: List[Tuple[int, int]] = []
    depth = 0
    a = start
    for i in range(start, end):
        c = mask[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c == "," and depth == 0:
            spans.append((a, i))
            a = i + 1
    if mask[a:end].strip():
        spans.append((a, end))
    return spans


def _attr_arg_end(mask: str, open_paren: int) -> int:
    """Offset of the `)` closing the attribute argument list opened at
    ``open_paren``, or len(mask) when the source is truncated."""
    depth = 1
    for i in range(open_paren + 1, len(mask)):
        c = mask[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i
    return len(mask)


def _member_name_after(mask: str, pos: int) -> str:
    """The method name following an attribute, skipping the attribute's own closing
    `]` and any FURTHER `[...]` attribute blocks stacked on the same member.

    Diagnostics only -- an empty string is harmless. Skipping the stacked blocks
    matters anyway: without it the name reads as the next attribute's own type
    ("Obsolete"), which would point a mismatch message at the wrong line.
    """
    i = pos
    n = len(mask)
    while i < n:
        while i < n and mask[i].isspace():
            i += 1
        if i < n and mask[i] == "]":
            i += 1  # the closing bracket of the attribute just parsed
            continue
        if i < n and mask[i] == "[":
            depth = 0
            while i < n:
                if mask[i] == "[":
                    depth += 1
                elif mask[i] == "]":
                    depth -= 1
                    if depth == 0:
                        i += 1
                        break
                i += 1
            continue
        break
    m = _CS_MEMBER_NAME_RE.search(mask, i, min(i + 400, n))
    return m.group(1) if m else ""


def parse_ingame_test_declarations(
    source_text: str, source_name: str = ""
) -> List[InGameTestDecl]:
    """Every `[InGameTest(...)]` declaration in one C# file, as the runner sees it.

    Pure over the file TEXT (the caller does the reading). Handles the four forms
    the tree actually uses, each of which defeats a one-line regex:
      - multi-line argument lists (``[InGameTest(\\n Category = "X",\\n ...)]``);
      - ``Category = <const>`` resolved against a ``const string`` in the same
        file (RouteRewindTimelineRuntimeTests declares
        ``private const string Category = "RouteRewindTimeline"``);
      - ``Description`` strings carrying commas, parentheses, or attribute-shaped
        text;
      - commented-out code and a bare ``[InGameTest]``.

    An unresolvable ``Category`` / ``Scene`` expression is NOT dropped: it is kept
    with a ``<unresolved:...>`` marker so it still counts toward a category total
    somewhere visible, and the sweep can assert that none exist. Silently dropping
    it would make the gate fail OPEN in exactly the case where the source grew a
    form this parser does not model.
    """
    text = source_text or ""
    mask = _mask_csharp_noise(text)
    consts: Dict[str, str] = {}
    for m in _CS_CONST_STRING_RE.finditer(mask):
        a, b = m.span("val")
        consts[m.group("name")] = text[a:b]

    out: List[InGameTestDecl] = []
    for name_start, _name_end, open_paren, section_close in \
            _iter_ingame_attribute_uses(mask):
        line = text.count("\n", 0, name_start) + 1
        named: Dict[str, str] = {}
        if open_paren is not None:
            args_end = _attr_arg_end(mask, open_paren)
            for a, b in _split_arg_spans(mask, open_paren + 1, args_end):
                am = _CS_ASSIGN_RE.match(mask, a, b)
                if am:
                    named[am.group("name")] = text[am.end():b].strip()
        # Resume the member-name lookup after the WHOLE attribute section, not
        # after this attribute's own `)`: an attribute sharing a bracket with
        # others has more text before the member ([Obsolete("x"), InGameTest(...)]).
        member = _member_name_after(mask, section_close)
        origin = "%s:%d %s" % (source_name or "<text>", line, member or "?")
        out.append(InGameTestDecl(
            category=_resolve_category(named.get("Category"), consts),
            scene=_resolve_scene(named.get("Scene")),
            allow_batch=_resolve_bool_default_true(named.get("AllowBatchExecution")),
            origin=origin,
            restore_baseline=_resolve_bool_default_false(
                named.get("RestoreBatchFlightBaselineAfterExecution"))))
    return out


def _resolve_category(expr: Optional[str], consts: Dict[str, str]) -> str:
    """The attribute's effective Category. Absent -> "General" (the property
    initializer, which DiscoverTests also falls back to)."""
    if expr is None:
        return "General"
    sm = _CS_STRING_LITERAL_RE.match(expr)
    if sm:
        return sm.group(1)
    if expr in consts:
        return consts[expr]
    tail = expr.rsplit(".", 1)[-1]  # SomeType.Category
    if tail in consts:
        return consts[tail]
    return "<unresolved:%s>" % expr


def _resolve_scene(expr: Optional[str]) -> Optional[str]:
    """The attribute's effective Scene as a bare enum member name. Absent, or the
    explicit AnyScene sentinel, -> ``INGAME_ANY_SCENE``."""
    if expr is None:
        return INGAME_ANY_SCENE
    sm = _CS_SCENE_MEMBER_RE.match(expr)
    if sm:
        return sm.group(1)
    if expr.rsplit(".", 1)[-1] == "AnyScene":
        return INGAME_ANY_SCENE
    return "<unresolved:%s>" % expr


def _resolve_bool_default_true(expr: Optional[str]) -> bool:
    """AllowBatchExecution: absent -> true (the property initializer). Anything
    that is not literally ``false`` is treated as true, matching the runner's
    ``if (test.AllowBatchExecution)`` branch on the default."""
    return (expr or "true").strip() != "false"


def _resolve_bool_default_false(expr: Optional[str]) -> bool:
    """RestoreBatchFlightBaselineAfterExecution: absent -> FALSE.

    The mirror image of ``_resolve_bool_default_true`` and deliberately not the
    same helper: the C# property carries NO initializer
    (``public bool RestoreBatchFlightBaselineAfterExecution { get; set; }``,
    InGameTestAttribute.cs), so its default is `default(bool)` == false, whereas
    ``AllowBatchExecution`` is initialized to true. Only a literal ``true``
    admits, which fails CLOSED: an expression this parse cannot read (a const
    indirection, say) reads as NOT restore-backed, so an isolated derivation
    UNDER-counts admissions and a spec pinning the higher number reds. The
    opposite default would silently inflate an isolated `passed=` pin.
    """
    return (expr or "false").strip() == "true"


def unresolved_ingame_declarations(
    decls: Sequence[InGameTestDecl],
) -> List[InGameTestDecl]:
    """Declarations whose Category or Scene this parser could not resolve. A
    non-empty result means the source grew an attribute form the parse does not
    model -- a gate that must FAIL, not shrug.

    This catches only forms that were RECOGNISED and then failed to resolve. A
    form never recognised at all is caught by
    ``unclaimed_ingame_attribute_tokens``; the two together are what make
    "reported, never dropped" true."""
    return [d for d in decls
            if str(d.category).startswith("<unresolved:")
            or str(d.scene or "").startswith("<unresolved:")]


def _line_text_at(text: str, offset: int) -> str:
    """The whitespace-collapsed source line containing ``offset``, truncated. Used
    only to make an unclaimed-token message actionable at a glance."""
    a = text.rfind("\n", 0, offset) + 1
    b = text.find("\n", offset)
    b = len(text) if b < 0 else b
    line = " ".join(text[a:b].split())
    return line if len(line) <= 160 else line[:157] + "..."


def unclaimed_ingame_attribute_tokens(
    source_text: str, source_name: str = ""
) -> List[str]:
    """Attribute-position `InGameTest` spellings ``parse_ingame_test_declarations``
    did NOT claim.

    RECOGNITION completeness, the mirror of ``unresolved_ingame_declarations``'s
    resolution completeness. The resolution check only ever sees forms the parse
    already recognised; a spelling it never matches is simply ABSENT, shrinking a
    category total with no marker anywhere -- the one way this gate fails open.

    That is not hypothetical. The original parse anchored on `[` immediately
    followed by the name, and the tree already contained FIVE
    ``[Parsek.InGameTests.InGameTest(...)]`` declarations (4 Ledger, 1 Rewind in
    IncompleteBallisticRuntimeTests.cs) that it silently dropped -- and that a raw
    ``[InGameTest(`` grep drops identically, so the two agreeing proved nothing.
    The parse now models the attribute-section grammar (optional `method:` target,
    optional namespace/type qualifier, C#'s optional `Attribute` suffix, and
    position anywhere in a comma-separated bracket), so all four spellings below
    are COUNTED rather than reported::

        [System.Obsolete("x"), InGameTest(Category = "GameActionsHealth")]
        [InGameTestAttribute(Category = "GameActionsHealth")]
        [Parsek.InGameTests.InGameTest(Category = "GameActionsHealth")]
        [method: InGameTest(Category = "GameActionsHealth")]

    This function is the residual backstop: it re-scans for EVERY occurrence of
    the attribute name inside an attribute bracket that looks like a use (followed
    by `(` or the bracket's end) and reports the ones no claim covers. The sweep
    asserts the list is empty, so the next spelling nobody anticipated reds
    locally instead of quietly moving a pinned total.

    Pure over the file TEXT. A reported occurrence is not automatically a bug in
    the source: the fix is EITHER to write the declaration in house style OR to
    teach ``_CS_INGAME_ELEMENT_RE`` the new form. What is not allowed is for it to
    pass unnoticed.
    """
    text = source_text or ""
    mask = _mask_csharp_noise(text)
    hits = [m.span() for m in _CS_INGAME_NAME_RE.finditer(mask)]
    if not hits:
        return []
    claimed = {ns for ns, _ne, _op, _sc in _iter_ingame_attribute_uses(mask)}
    # Attribute-bracket context, from a single ordered walk of the `[`/`]` marks
    # (masked, so a bracket inside a comment or string literal is already gone).
    # depth > 0 at the token start is what separates an attribute list from an
    # ordinary expression mentioning the type.
    marks = [(m.start(), m.group()) for m in _CS_BRACKET_RE.finditer(mask)]
    out: List[str] = []
    mi = 0
    depth = 0
    for a, b in hits:
        while mi < len(marks) and marks[mi][0] < a:
            depth = depth + 1 if marks[mi][1] == "[" else max(0, depth - 1)
            mi += 1
        if depth <= 0 or a in claimed:
            continue
        j = b
        while j < len(mask) and mask[j].isspace():
            j += 1
        # An attribute USE is the name followed by its argument list or the end of
        # the attribute; `typeof(InGameTestAttribute)` or `InGameTestAttribute.X`
        # inside a bracket is a reference, not a declaration. A `,` follow is NOT
        # in the set: the bare-attribute-sharing-a-bracket form it would cover is
        # already claimed by the parse, so admitting it would only add a
        # false-positive surface (a generic type argument inside an attribute
        # argument list).
        if j >= len(mask) or mask[j] not in "(]":
            continue
        out.append("%s:%d %s" % (source_name or "<text>",
                                 text.count("\n", 0, a) + 1,
                                 _line_text_at(text, a)))
    return out


@dataclass(frozen=True)
class BatchTallyDerivation:
    """What `RunCategory(category)` at ``scene`` must tally, from the attributes
    alone. ``skipped`` is split into the two runner filters, applied in the
    runner's order, so a mismatch message can say WHICH filter moved."""

    category: str
    scene: str
    total: int
    scene_skipped: int
    batch_skipped: int
    executable: int
    scene_skipped_members: Tuple[str, ...] = ()
    batch_skipped_members: Tuple[str, ...] = ()

    @property
    def attribute_skipped(self) -> int:
        """The FLOOR on the runner's `skipped=` token: the tests the two
        attribute filters skip before any test body runs. Run-time
        ``InGameAssert.Skip`` self-skips add to this and are not derivable."""
        return self.scene_skipped + self.batch_skipped


def derive_batch_tally(
    decls: Sequence[InGameTestDecl], category: str, scene: str,
    isolated: bool = False,
) -> BatchTallyDerivation:
    """Model InGameTestRunner.RunCategory's two filters over ``decls``.

    Order is the runner's (InGameTestRunner.cs:411): scene eligibility FIRST,
    then AllowBatchExecution over what survived. A test that is both
    scene-ineligible and batch-disabled is counted in the scene bucket ONLY,
    exactly as the runner counts it once.

    ``category`` may be ``INGAME_RUNALL_CATEGORY`` ("all"), the token RunAll
    stamps when a spec drives RunTests with no category argument. RunAll runs the
    two filters over the WHOLE ``allTests`` set rather than a category slice, so
    the derivation is the same two filters over every declaration -- not a lookup
    for a C# category named "all", which would derive total=0 and report the
    category as missing.

    ``isolated`` (R5) models the OTHER batch entry point. A spec whose RunTests
    step carries ``isolated = "true"`` (or whose ``[driver.autorun]`` sets
    ``isolated = true``) routes to ``RunCategoryIncludingFlightRestore``, whose
    admission filter is ``PrepareBatchExecutionIncludingFlightRestore``:

        if (test.AllowBatchExecution || test.RestoreBatchFlightBaselineAfterExecution)

    a strict SUPERSET of the ordinary filter's ``if (test.AllowBatchExecution)``.
    So the second stage drops only declarations that are neither batch-allowed nor
    restore-backed -- the genuinely manual-only ones. ``total`` is unaffected in
    both modes (BATCH_COMPLETE counts filtered tests too); what moves is the
    batch_skipped/executable split, which is exactly what an isolated spec's
    ``passed=`` / ``skipped=`` pin rests on.

    The scene stage is IDENTICAL in both modes: ``FilterSceneEligibleBatchCandidates``
    runs BEFORE either admission filter on all four entry points and knows nothing
    about the restore flag.

    NOT modelled, deliberately: ``PrepareBatchFlightRestoreExecution``, which the
    isolated path runs afterwards and which skips the restore-backed tests when no
    flight baseline is available. That outcome depends on live scene state
    (``FlightGlobals.ActiveVessel``, ``HighLogic.SaveFolder``), not on the
    attributes, so it belongs with the run-time ``InGameAssert.Skip`` guards this
    derivation already declines to predict. It can only push ``skipped`` HIGHER,
    which is the direction ``batch_tally_pin_mismatches`` already treats as
    un-derivable.
    """
    in_category = (list(decls) if category == INGAME_RUNALL_CATEGORY
                   else [d for d in decls if d.category == category])
    # Partitioned by predicate, never by membership: two declarations with the
    # same category/scene/flags would compare EQUAL as frozen dataclasses, and an
    # `in`-based split would then mis-bucket the duplicate.
    scene_skipped = [d for d in in_category
                     if d.scene is not INGAME_ANY_SCENE and d.scene != scene]
    eligible = [d for d in in_category
                if d.scene is INGAME_ANY_SCENE or d.scene == scene]
    batch_skipped = [d for d in eligible
                     if not (d.allow_batch or (isolated and d.restore_baseline))]
    return BatchTallyDerivation(
        category=category,
        scene=scene,
        total=len(in_category),
        scene_skipped=len(scene_skipped),
        batch_skipped=len(batch_skipped),
        executable=len(eligible) - len(batch_skipped),
        scene_skipped_members=tuple(d.origin for d in scene_skipped),
        batch_skipped_members=tuple(d.origin for d in batch_skipped))


@dataclass(frozen=True)
class BatchTallyPin:
    """The literal tokens a spec's `logContracts.required` pins on its
    BATCH_COMPLETE line. A token given as a regex class rather than a literal
    (``passed=[1-9][0-9]*``, the honest interim form when no live tally has been
    measured; no committed spec uses one today) reads as None = deliberately
    unpinned. ``total=`` is required as a literal by the sweep regardless."""

    total: Optional[int] = None
    passed: Optional[int] = None
    failed: Optional[int] = None
    skipped: Optional[int] = None
    category: Optional[str] = None
    scene: Optional[str] = None
    patterns: Tuple[str, ...] = ()

    @property
    def is_aggregate(self) -> bool:
        """True when the pinned category is the multi-category AGGREGATE token
        (``multi:<n>``, see resolve_batch_complete). No single C# category
        corresponds to it, and the pin does not carry the constituent list, so a
        per-category derivation is OUT OF SCOPE rather than failed."""
        return (self.category is not None
                and _MULTI_CATEGORY_RE.match(self.category) is not None)

    @property
    def statically_checkable(self) -> bool:
        """True when the pin names a literal, non-aggregate category + scene, which
        is what the derivation needs as its input. Everything else is optional."""
        return (self.category is not None and self.scene is not None
                and not self.is_aggregate)


# A trailing regex anchor a literal token may legitimately carry ("failed=0\b").
_PIN_TOKEN_TAIL_RE = re.compile(r"(?:\\b|\$)+$")


def _pin_token(pattern: str, name: str) -> Optional[str]:
    m = re.search(r"\b%s=(\S+)" % name, pattern)
    return m.group(1) if m else None


def _pin_literal_int(tok: Optional[str]) -> Optional[int]:
    if tok is None:
        return None
    t = _PIN_TOKEN_TAIL_RE.sub("", tok)
    return int(t) if re.fullmatch(r"\d+", t) else None


def _pin_literal_word(tok: Optional[str]) -> Optional[str]:
    """A pinned ``category=`` / ``scene=`` token, when it is a plain literal.

    The class admits ``-`` because C# category names really do carry one: the tree
    has seven hyphenated categories (Pipeline-Anchor, Pipeline-Smoothing,
    Pipeline-Frame, Pipeline-Outlier, Pipeline-Terrain, Pipeline-AnchorPropagate,
    Pipeline-Anchor-BubbleEntry). Without it every one of them was structurally
    UNPINNABLE: `resolve_batch_tally_pin` read category=None, `statically_checkable`
    went False, and `CommittedBatchTallySourceSyncTests` rejected the spec with
    "pins a BATCH_COMPLETE line with no literal category=/scene=" - a message that
    blames the spec author for a limitation of this character class. The runtime
    side never had the gap: _BATCH_RE reads `category=(?P<category>\\S+)`, so a real
    hyphenated BATCH_COMPLETE line has always parsed correctly, and this only ever
    disagreed with it on the static-pin path.

    Widening is safe against mistaking a REGEX for a literal: `-` is a metacharacter
    only inside a character class, and `[` / `]` are still excluded, so `[A-Z]-x`
    stays non-literal and is reported as unpinned rather than silently read as the
    seven-character category "[A-Z]-x". The hyphen is written last so it is a literal
    member of the class rather than a range.
    """
    if tok is None:
        return None
    t = _PIN_TOKEN_TAIL_RE.sub("", tok)
    return t if re.fullmatch(r"[A-Za-z0-9_:-]+", t) else None


def resolve_batch_tally_pin(
    required_patterns: Sequence[str],
) -> Optional[BatchTallyPin]:
    """Merge the BATCH_COMPLETE tokens a spec pins across ALL of its required
    patterns, or None when no pattern names BATCH_COMPLETE.

    Merging rather than reading one pattern is deliberate: nothing in the spec
    schema says the six tokens arrive in one pattern, and a spec that splits them
    over two patterns is still fully pinned. First literal wins; a LATER pattern
    contradicting an earlier literal is reported by
    ``batch_tally_pin_mismatches`` rather than silently resolved, since two
    patterns pinning different totals can never both be met.
    """
    pats = [str(p) for p in (required_patterns or []) if "BATCH_COMPLETE" in str(p)]
    if not pats:
        return None
    vals: Dict[str, object] = {}
    for pat in pats:
        for name, conv in (("total", _pin_literal_int), ("passed", _pin_literal_int),
                           ("failed", _pin_literal_int), ("skipped", _pin_literal_int),
                           ("category", _pin_literal_word),
                           ("scene", _pin_literal_word)):
            got = conv(_pin_token(pat, name))
            if got is not None and vals.get(name) is None:
                vals[name] = got
    return BatchTallyPin(
        total=vals.get("total"), passed=vals.get("passed"),
        failed=vals.get("failed"), skipped=vals.get("skipped"),
        category=vals.get("category"), scene=vals.get("scene"),
        patterns=tuple(pats))


def batch_tally_pin_mismatches(
    pin: BatchTallyPin, decls: Sequence[InGameTestDecl], isolated: bool = False,
) -> List[str]:
    """Every way ``pin`` contradicts the `[InGameTest]` attributes in ``decls``.

    Empty list = the pin still agrees with the source. Each message is written to
    be actionable on its own, because the developer who reads it is the one who
    just added a test to a category they may never have heard of.

    ``isolated`` must be the spec's OWN batch mode (``spec_batch_isolated``). It
    is not a cosmetic pass-through: an isolated spec over an all-batch-disabled
    category derives ``executable = 0`` under the ordinary model, so its perfectly
    correct ``passed=N`` pin would be rejected, and -- worse in the other
    direction -- a spec that DROPPED its isolated arg would keep validating
    against the isolated derivation and read as green while running nothing.
    """
    problems: List[str] = []
    if pin.is_aggregate:
        # A multi-category batch gates on the UNION line, whose total sums several
        # categories the pin does not enumerate. Understood and out of scope, so
        # empty rather than a complaint -- unlike the case below, where the pin
        # simply could not be read.
        return []
    if not pin.statically_checkable:
        return ["pin does not name a literal category= and scene=, so the tally "
                "cannot be cross-checked against the source: %s"
                % " | ".join(pin.patterns)]

    d = derive_batch_tally(decls, pin.category, pin.scene, isolated=isolated)
    if d.total == 0:
        problems.append(
            "no [InGameTest(Category = \"%s\")] method exists in the source: the "
            "category was renamed or its file was lost, and the batch this spec "
            "gates would run EMPTY" % pin.category)
        return problems

    if pin.total is not None and pin.total != d.total:
        problems.append(
            "pins total=%d but the source declares %d [InGameTest(Category = "
            "\"%s\")] method(s). BATCH_COMPLETE's total is "
            "allTests.Count(Status != NotRun) over the single RunCategory batch, "
            "so it counts filtered tests too. At scene=%s the source derives "
            "total=%d = %d scene-skipped + %d batch-skipped + %d executable. "
            "passed= and skipped= are NOT derivable and must be RE-MEASURED off a "
            "live run of this spec: run-time InGameAssert.Skip decides the split, "
            "so do not compute them from the derivation above."
            % (pin.total, d.total, pin.category, pin.scene, d.total,
               d.scene_skipped, d.batch_skipped, d.executable))

    if pin.skipped is not None and pin.skipped < d.attribute_skipped:
        problems.append(
            "pins skipped=%d but the attributes already force %d skip(s) at "
            "scene=%s: %d scene-ineligible (%s) + %d %s (%s). Run-time "
            "InGameAssert.Skip guards can only push skipped HIGHER, never lower."
            % (pin.skipped, d.attribute_skipped, pin.scene, d.scene_skipped,
               ", ".join(d.scene_skipped_members) or "-", d.batch_skipped,
               "neither AllowBatchExecution nor "
               "RestoreBatchFlightBaselineAfterExecution (isolated batch)"
               if isolated else "AllowBatchExecution=false",
               ", ".join(d.batch_skipped_members) or "-"))

    if pin.passed is not None and pin.failed is not None:
        executed = pin.passed + pin.failed
        if executed > d.executable:
            problems.append(
                "pins passed=%d failed=%d (%d executed) but only %d test(s) in "
                "%s are batch-eligible at scene=%s on the %s batch path, so that "
                "many can never run.%s"
                % (pin.passed, pin.failed, executed, d.executable, pin.category,
                   pin.scene, "ISOLATED" if isolated else "ordinary",
                   "" if isolated else
                   " If this category's tests are AllowBatchExecution=false but "
                   "RestoreBatchFlightBaselineAfterExecution=true, the spec needs "
                   "isolated = \"true\" on its RunTests step (R5)."))

    if None not in (pin.total, pin.passed, pin.failed, pin.skipped):
        summed = pin.passed + pin.failed + pin.skipped
        if summed != pin.total:
            problems.append(
                "pinned tally is not self-consistent: passed=%d + failed=%d + "
                "skipped=%d = %d, but total=%d. The runner recounts all three "
                "over the same `considered` set, so total == passed + failed + "
                "skipped always holds and this line can never match."
                % (pin.passed, pin.failed, pin.skipped, summed, pin.total))

    for pat in pin.patterns:
        for name, conv, got in (("total", _pin_literal_int, pin.total),
                                ("passed", _pin_literal_int, pin.passed),
                                ("failed", _pin_literal_int, pin.failed),
                                ("skipped", _pin_literal_int, pin.skipped),
                                ("category", _pin_literal_word, pin.category),
                                ("scene", _pin_literal_word, pin.scene)):
            here = conv(_pin_token(pat, name))
            if here is not None and got is not None and here != got:
                problems.append(
                    "two required patterns pin conflicting %s= (%s vs %s); no "
                    "single BATCH_COMPLETE line can satisfy both"
                    % (name, got, here))
    return problems


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

    # skipTailOnUnmetMission (the unmet-mission tail opt-out): bool when present.
    # A string "false" would read as truthy and silently keep the legacy tail, so a
    # non-bool is an ERROR, not a coerced value. It only bites on an UNMET mission
    # step, so declaring it on a seam-only driver is inert -- warned, not rejected,
    # since it costs nothing and a driver may be converted to autopilot later.
    skip_tail = driver.get(SKIP_TAIL_ON_UNMET_MISSION_KEY)
    if skip_tail is not None and not isinstance(skip_tail, bool):
        errors.append("driver.%s: %r must be a bool"
                      % (SKIP_TAIL_ON_UNMET_MISSION_KEY, skip_tail))
    # MISPLACED-KEY guard. The flag is only read off [driver], and a key written after
    # the [driver.missionParams] header is scoped to driver.missionParams by TOML -- the
    # exact trap EVA-4-atmo-chute.toml already documents in-line for `steps`. Without
    # this the misplacement is SILENT and the reader falls back to the default, so an
    # author's deliberate opt-out is inert with nothing to notice. It fails in the safe
    # direction (the skip stays ON), which is why a silent drop would be so easy to miss;
    # reject it outright rather than warn, since the author explicitly asked for the
    # opposite of what they would get.
    for scope_name, scope in (("driver.missionParams", driver.get("missionParams")),
                              ("driver.autorun", autorun),
                              ("the spec root", spec)):
        if isinstance(scope, dict) and SKIP_TAIL_ON_UNMET_MISSION_KEY in scope:
            errors.append(
                "%s: %s belongs in [driver], not here (a key after the "
                "[driver.missionParams] header is TOML-scoped to that sub-table, so it "
                "would be silently ignored and the default would apply)"
                % (scope_name, SKIP_TAIL_ON_UNMET_MISSION_KEY))

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
    run_tests_selector: Optional[str] = None
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
            # R5: the mission branch `continue`s before the isolated guards below,
            # so without this a mission step could carry `isolated` at either level
            # and be silently inert. A mission step is a harness-side handoff that
            # writes nothing to the channel, so the flag can never mean anything here.
            if BATCH_ISOLATED_KEY in step or BATCH_ISOLATED_KEY in (step.get("args", {}) or {}):
                errors.append(
                    "driver.steps[%d]: %s is meaningless on a mission-phase step "
                    "(a mission step writes nothing to the command channel), so it "
                    "would be silently ignored" % (i, BATCH_ISOLATED_KEY))
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

        # R5 `isolated`: see the BATCH_ISOLATED_KEY note above for why each of
        # these is an ERROR rather than a coerced value or a warning.
        step_args = step.get("args", {}) or {}
        if BATCH_ISOLATED_KEY in step:
            errors.append(
                "driver.steps[%d]: %s belongs in this step's args table, not "
                "beside cmd/expect (a key outside args is never written to the "
                "channel, so the batch would silently run NON-isolated)"
                % (i, BATCH_ISOLATED_KEY))
        # CASE VARIANT. There is no per-verb arg vocabulary, so an unknown arg key is
        # forwarded verbatim and the C# `ArgOrNull(cmd, "isolated")` (an exact,
        # case-sensitive dictionary lookup) simply misses `Isolated` / `ISOLATED`.
        # Silent, and inert in the unsafe direction.
        for key in step_args:
            if (isinstance(key, str) and key != BATCH_ISOLATED_KEY
                    and key.lower() == BATCH_ISOLATED_KEY):
                errors.append(
                    "driver.steps[%d].args.%s: the seam arg is spelled %r exactly "
                    "(the C# lookup is case-sensitive), so this key would be sent "
                    "and silently ignored" % (i, key, BATCH_ISOLATED_KEY))
        if BATCH_ISOLATED_KEY in step_args:
            if cmd != "RunTests":
                errors.append(
                    "driver.steps[%d].args.%s: only the RunTests verb reads it, "
                    "but this step is %r -- the arg would be silently ignored"
                    % (i, BATCH_ISOLATED_KEY, cmd))
            raw = step_args.get(BATCH_ISOLATED_KEY)
            if raw not in BATCH_ISOLATED_VALUES:
                errors.append(
                    "driver.steps[%d].args.%s: %r must be the STRING %s. Step args "
                    "are wire-encoded with str(value), so the TOML bool `true` "
                    "would be sent as the token `isolated=True` and the seam's "
                    "case-sensitive parse REJECTS it. Write %s = \"true\"."
                    % (i, BATCH_ISOLATED_KEY, raw,
                       " or ".join(repr(v) for v in BATCH_ISOLATED_VALUES),
                       BATCH_ISOLATED_KEY))
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
            # The FIRST RunTests category is what run.py's _driven_category resolves,
            # so it is the selector the vacuity probe must be built for.
            if run_tests_steps == 0:
                run_tests_selector = (step.get("args", {}) or {}).get("category")
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

    # R5 `isolated` on the AUTORUN half. Unlike a step arg (a wire token, so a
    # string) this is a native TOML bool, because it becomes an env var the harness
    # sets from a truth test rather than a value it forwards verbatim.
    if isinstance(autorun, dict) and BATCH_ISOLATED_KEY in autorun:
        autorun_isolated = autorun.get(BATCH_ISOLATED_KEY)
        if not isinstance(autorun_isolated, bool):
            errors.append(
                "driver.autorun.%s: %r must be a bool (a string \"false\" reads as "
                "TRUTHY and would arm the isolated route the author was disabling)"
                % (BATCH_ISOLATED_KEY, autorun_isolated))
        elif autorun_isolated and not autorun_has_tests:
            errors.append(
                "driver.autorun.%s is armed but driver.autorun.tests is absent, so "
                "no batch auto-runs and the arm is inert (PARSEK_AUTORUN_ISOLATED "
                "without PARSEK_AUTORUN_TESTS; the addon WARNs about this at "
                "startup, and a spec can be caught here instead)"
                % BATCH_ISOLATED_KEY)
    # MISPLACED-KEY guard, same shape as skipTailOnUnmetMission / batchVacuityOptOut.
    # The autorun flag is read ONLY off [driver.autorun]; anywhere else it is
    # silently ignored and the batch runs NON-isolated while the author believes
    # otherwise.
    #
    # RECURSIVE, not an allowlist. The first cut named four scopes and a review
    # found four more that slipped through - including [expectations.logContracts],
    # which is where the OTHER batch-behaviour flag (batchVacuityOptOut) lives and is
    # therefore the single most plausible wrong home. An allowlist of wrong places is
    # the wrong shape for a rule of the form "anywhere but here": sweep everything and
    # carve out the two legitimate homes instead.
    _isolated_legit = [driver.get("autorun") if isinstance(driver.get("autorun"), dict) else None]
    for _s in (steps or []):
        if isinstance(_s, dict) and isinstance(_s.get("args"), dict):
            _isolated_legit.append(_s.get("args"))

    def _sweep_isolated(scope, path):
        if not isinstance(scope, dict):
            return
        if any(scope is legit for legit in _isolated_legit if legit is not None):
            return
        if BATCH_ISOLATED_KEY in scope:
            errors.append(
                "%s: %s belongs in [driver.autorun] (or in a RunTests step's args "
                "table), not here -- a key in this table is never read, so the batch "
                "would silently run NON-isolated" % (path or "the spec root",
                                                     BATCH_ISOLATED_KEY))
        for k, v in scope.items():
            if isinstance(v, dict):
                _sweep_isolated(v, "%s.%s" % (path, k) if path else k)
            elif isinstance(v, list):
                for j, item in enumerate(v):
                    if isinstance(item, dict):
                        _sweep_isolated(item, "%s.%s[%d]" % (path, k, j) if path else "%s[%d]" % (k, j))

    _sweep_isolated(spec, "")

    # R5: every RunTests step must agree on the batch mode. `spec_batch_isolated`
    # answers True if ANY step is isolated, so a spec whose steps DISAGREE would be
    # derived against a mode half its batches do not use. Unreachable while
    # SINGLE_BATCH_SELECTOR_RULE holds, but reachable through the documented
    # batchVacuityOptOut escape, which is exactly where a review found it.
    _rt_modes = {(((s or {}).get("args", {}) or {}).get(BATCH_ISOLATED_KEY) == "true")
                 for s in (steps or []) if (s or {}).get("cmd") == "RunTests"}
    if len(_rt_modes) > 1:
        errors.append(
            "driver.steps: RunTests steps DISAGREE on %s. The batch mode is a "
            "whole-spec property (it selects which tests are admitted and therefore "
            "which derivation every pinned tally is checked against), so a spec "
            "cannot drive one isolated batch and one ordinary batch. Split them into "
            "separate scenario specs." % BATCH_ISOLATED_KEY)

    # --- Anti-vacuity gate (the B10 "GREEN over ZERO executed tests" class). A spec
    # that owns a batch must pin a tally an EMPTY or ALL-SKIPPED batch cannot satisfy;
    # see the batch_contract_vacuity_gap module note for the mechanism (probe by
    # construction, never a syntactic "must mention total=" tautology).
    opt_out = log_contracts.get(BATCH_VACUITY_OPT_OUT_KEY)
    opt_out_reason = log_contracts.get(BATCH_VACUITY_OPT_OUT_REASON_KEY)
    if opt_out is not None and not isinstance(opt_out, bool):
        errors.append("expectations.logContracts.%s: %r must be a bool"
                      % (BATCH_VACUITY_OPT_OUT_KEY, opt_out))
    # MISPLACED-KEY guard, mirroring skipTailOnUnmetMission: a key written before the
    # [expectations.logContracts] header lands in a different TOML table and the flag
    # would be SILENTLY inert - and this one fails in the UNSAFE direction (the author
    # believes they opted out; validation then rejects the spec for a reason the
    # misplaced key was meant to waive). Reject outright so the misplacement is named.
    for scope_name, scope in (("expectations", expectations),
                              ("the spec root", spec),
                              ("driver", driver)):
        if isinstance(scope, dict):
            for key in (BATCH_VACUITY_OPT_OUT_KEY, BATCH_VACUITY_OPT_OUT_REASON_KEY):
                if key in scope:
                    errors.append(
                        "%s: %s belongs in [expectations.logContracts], not here "
                        "(a key outside that table is silently ignored)" % (scope_name, key))
    if batch_owners > 0:
        selector = run_tests_selector if run_tests_steps > 0 else (autorun or {}).get("tests")
        # SHAPE GATE (2026-07-26 review). The probe family is built for ONE batch
        # driven by ONE named category; outside that shape the anti-vacuity
        # guarantee simply does not hold, so the shape itself is enforced rather
        # than left as an unstated precondition. See the SINGLE_BATCH_SELECTOR_RULE
        # note above the probe helpers for the two dodges this closes.
        if opt_out is not True and run_tests_steps > 1:
            errors.append(
                "driver.steps: %d RunTests steps declared -- %s. Only the FIRST "
                "category is resolved (run.py::_driven_category) and probed, so every "
                "later batch would run UNGATED. Split the extra batch into its own "
                "scenario spec. Deliberate exception: set "
                "expectations.logContracts.%s = true with a %s."
                % (run_tests_steps, SINGLE_BATCH_SELECTOR_RULE,
                   BATCH_VACUITY_OPT_OUT_KEY, BATCH_VACUITY_OPT_OUT_REASON_KEY))
        if opt_out is not True and (not selector or is_multi_category_selector(selector)):
            errors.append(
                "driver: batch selector %r is %s -- %s. The gating line for a "
                "multi-category run is the category=multi:<n> AGGREGATE, whose tally "
                "sums the constituents, so 'category B executed nothing' is not "
                "expressible. Name one category per spec. Deliberate exception: set "
                "expectations.logContracts.%s = true with a %s."
                % (selector,
                   "absent (RunTests with no category runs ALL categories)"
                   if not selector else "multi-category",
                   SINGLE_BATCH_SELECTOR_RULE,
                   BATCH_VACUITY_OPT_OUT_KEY, BATCH_VACUITY_OPT_OUT_REASON_KEY))
        if opt_out is True:
            if not isinstance(opt_out_reason, str) or not opt_out_reason.strip():
                errors.append(
                    "expectations.logContracts.%s: a non-empty %s is REQUIRED (an "
                    "unexplained opt-out is how the vacuous-batch class comes back)"
                    % (BATCH_VACUITY_OPT_OUT_KEY, BATCH_VACUITY_OPT_OUT_REASON_KEY))
        else:
            gap = batch_contract_vacuity_gap(required_patterns, selector)
            if gap is not None:
                errors.append(
                    "expectations.logContracts.required: the batch contract for "
                    "selector %r cannot detect a vacuous batch -- it ACCEPTS %s. Pin "
                    "the tally that must actually execute (e.g. 'BATCH_COMPLETE v1 "
                    "total=N passed=N failed=0 skipped=0 category=X scene=Y', or "
                    "'passed=[1-9][0-9]*' when the exact split is not yet measured); "
                    "derive N from the category's [InGameTest] Scene / "
                    "AllowBatchExecution attributes and the fixture's LoadGame route. "
                    "Deliberate exception: set %s = true with a %s."
                    % (selector, gap, BATCH_VACUITY_OPT_OUT_KEY,
                       BATCH_VACUITY_OPT_OUT_REASON_KEY))
    elif opt_out is not None:
        warnings.append(
            "expectations.logContracts.%s declared on a spec with no batch owner: "
            "inert (there is no batch whose tally could be vacuous)"
            % (BATCH_VACUITY_OPT_OUT_KEY,))

    # PATTERN COMPILABILITY (2026-07-27, autotest-roadmap R1). Every logContracts
    # pattern is a REGEX applied with re.search (see evaluate_expectations), not a
    # literal. A KSP.log token containing regex metacharacters therefore has to be
    # escaped, and the natural way to write one is the broken way: the R1 debris
    # token `Child recording created (debris, TTL=` raises
    # `re.error: missing ), unterminated subpattern` verbatim.
    #
    # evaluate_expectations already CATCHES re.error and turns it into a mismatch,
    # so nothing crashes - but that check runs on the collected log, i.e. AFTER the
    # flight. A malformed pattern on B13 costs its measured 2,825 s p50 before it
    # reds, and the red says "invalid regex" rather than naming a Parsek defect.
    # Rejecting at validation time makes it a `--dry-run` error instead: free, and
    # before the run lock is even taken.
    #
    # Deliberately NOT paired with a "did you mean to escape this?" heuristic. A
    # pattern that compiles is the author's business - `terminalOrbitBody=\(null\)`
    # and `(Approach|ExoBallistic)` are both committed, load-bearing and correct -
    # so the only thing checkable without guessing intent is compilability.
    for facet in ("required", "forbidden"):
        patterns = log_contracts.get(facet)
        if patterns is None:
            continue
        if not isinstance(patterns, list):
            # Reachable for `forbidden` only: a non-list `required` is already
            # iterated at the BATCH_COMPLETE check above and raises there first.
            # Pre-existing, and out of this change's blast radius - noted so a
            # reader does not assume this line covers both facets.
            errors.append("expectations.logContracts.%s: %r must be a list of regex "
                          "patterns" % (facet, patterns))
            continue
        for pat in patterns:
            if not isinstance(pat, str):
                errors.append("expectations.logContracts.%s: %r must be a string"
                              % (facet, pat))
                continue
            try:
                re.compile(pat)
            except re.error as exc:
                errors.append(
                    "expectations.logContracts.%s: %r is not a valid regex (%s). "
                    "These patterns are applied with re.search, so a KSP.log token "
                    "carrying regex metacharacters must escape them. In a TOML "
                    "basic (double-quoted) string write %s to match a literal '(' "
                    "- the backslash is DOUBLED because TOML consumes one before "
                    "the regex engine ever sees it. Caught here rather than after "
                    "the flight, where evaluate_expectations would red the run on "
                    "a collected log."
                    % (facet, pat, exc, '"\\\\("'))

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
        if isinstance(skip_tail, bool):
            warnings.append(
                "driver.%s declared on a seam-kind driver: inert (there is no mission "
                "step whose UNMET outcome could skip a tail)"
                % (SKIP_TAIL_ON_UNMET_MISSION_KEY,))

    # MISPLACED-KEY guard for allowedAnomalies (same class as the
    # skipTailOnUnmetMission guard above, found 2026-07-26). The anomaly sweep
    # reads ``expectations.allowedAnomalies``, but a bare ``allowedAnomalies``
    # written after an ``[expectations.<sub>]`` header is TOML-scoped to that
    # sub-table and is silently never read.
    #
    # ERROR, not WARN. It shipped as a WARN for exactly one commit, while all 28
    # committed specs still carried the misplaced form and rejecting would have
    # invalidated the whole set. They were relocated in the same change that
    # promoted this, so the committed set validates clean and the check can be
    # hard. A warning is the wrong instrument here for three reasons:
    #   1. Nothing FAILS on a warning. The misplacement is invisible at exactly
    #      the moment it matters - a spec author declaring a tolerance believes
    #      it applies, and a green run then means "the anomaly did not happen",
    #      not "the anomaly was tolerated". S1.4 carried a dead
    #      polyline-orbit-overlap exception that way from its first commit.
    #   2. validate_spec runs BEFORE the KSP launch, so an error costs a fast
    #      pre-flight rejection with the fix named in the message - never a
    #      wasted run.
    #   3. The sub-table a bare key lands in depends on which header precedes it,
    #      so the trap re-arms every time a spec grows a new [expectations.<sub>]
    #      block. A hard gate is what makes it unrepeatable.
    # Checked over EVERY expectations sub-table, not just logContracts: the key
    # binds to whichever one it was written under.
    for sub_name, sub in sorted((expectations or {}).items()):
        if not isinstance(sub, dict) or "allowedAnomalies" not in sub:
            continue
        declared = sub.get("allowedAnomalies") or []
        errors.append(
            "expectations.%s.allowedAnomalies: allowedAnomalies belongs in "
            "[expectations], not [expectations.%s] (a bare key after that header "
            "is TOML-scoped to the sub-table, so the anomaly sweep never reads "
            "it). REQUIRED PLACEMENT: delete the key from under "
            "[expectations.%s] and write it in an explicit [expectations] table "
            "declared BEFORE every [expectations.<sub>] header, i.e. exactly "
            "`[expectations]` then `allowedAnomalies = %s` then the sub-tables%s"
            % (sub_name, sub_name, sub_name,
               # json renders a TOML-valid array (double quotes); repr() would
               # emit single quotes, which TOML rejects.
               json.dumps(list(declared)),
               "" if not declared
               else "; the declared exception(s) %s are INERT where they sit and "
                    "the sweep would run with none allowed" % (list(declared),)))

    # The allowedAnomalies ENTRY shapes (bare token or { token, maxCount } budget).
    # Structural errors reject pre-launch for the same reason the misplaced-key guard
    # above does: a budget that silently fails to parse is a tolerance the author
    # believes is in force and is not. An inert declaration (a token the sweep can
    # never raise, e.g. the RETIRED icon-jump) is a WARNING, not an error - it costs
    # nothing at run time and hard-rejecting it would make a future token rename a
    # spec-invalid instead of a no-op.
    allowed_parse = parse_allowed_anomalies(expectations.get("allowedAnomalies") or [])
    errors.extend(allowed_parse.errors)
    warnings.extend(allowed_parse.warnings)

    # The REPORT-ONLY-by-default raw-Unity-exception scan's opt-in block. Declared in
    # ZERO committed specs; validated here so an armed ceiling that would silently
    # degrade to report-only (misspelled key, non-int, negative) is a pre-launch
    # rejection instead of a gate everyone believes is on.
    if UNITY_EXCEPTIONS_BLOCK in expectations:
        errors.extend(validate_unity_exception_expectations(
            expectations.get(UNITY_EXCEPTIONS_BLOCK)))
        warnings.extend(unity_exception_expectation_warnings(
            expectations.get(UNITY_EXCEPTIONS_BLOCK)))

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
# NOTE: "operator" is intentionally in NO set here - operator-tier specs are never
# picked up by a cadence and run only under an explicit `--tier operator` / `--id`.
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
# Instance settings-sidecar baseline (the tracer-leak fix). Pure.
#
# THE LEAK. `SetSetting` does NOT only mutate the live per-save GameParameters:
# eight of the sixteen whitelisted settings (Parsek's SettingWhitelist
# PersistenceRoute.GameParametersPlusSidecar) are ALSO written to the
# INSTANCE-WIDE `GameData/Parsek/PluginData/settings.cfg`, and Parsek's
# ParsekScenario.OnLoad applies that sidecar OVER whatever the loaded save
# carries. So one scenario's `SetSetting mapRenderTracing=true` silently pins the
# per-frame map/TS render tracer ON for EVERY later run on that instance -
# including multi-thousand-second autopilot flights that never asked for it, and
# whose anomaly sweep then gates on a tracer they did not declare. This was
# observed live: the automation instance's sidecar held exactly
# `mapRenderTracing = True` after S1.4.
#
# THE SECOND HALF OF THE LEAK. EIGHT of the eleven committed fixture saves ALSO
# carry `mapRenderTracing = True` inside their own GameParameters ParsekSettings
# node (they were harvested from a dev save with tracing on). The three that do
# NOT carry the key at all are fresh-career, fresh-sandbox and fresh-science.
# So for the eight, even a pristine sidecar would leave the tracer on for every
# flight off that fixture. Deleting the sidecar therefore does NOT fix it; only
# an explicit stored OFF does, because the sidecar is the layer that WINS over
# the save.
#
# THE FIX. run.py writes this deterministic baseline (the three diagnostic
# tracers pinned OFF) into the sidecar at STAGE, before launch, and again at
# TEARDOWN in the per-attempt finally. A scenario that wants a tracer declares it
# with its own SetSetting step, which is honoured for that run and reverted after
# it. Idempotent and self-healing: a run killed hard enough to skip teardown is
# cleaned by the next run's stage. Only the three tracer keys are written, so the
# other five sidecar-tracked settings stay unset and the fixture's own values
# continue to govern them.
# ---------------------------------------------------------------------------

# Path of the sidecar RELATIVE to the instance directory (the shell joins it).
SETTINGS_SIDECAR_RELPATH: Tuple[str, ...] = (
    "GameData", "Parsek", "PluginData", "settings.cfg")

# The diagnostic tracer flags, pinned OFF. Values are written exactly as Parsek's
# ParsekSettingsPersistence.Save emits them (bool.ToString() -> "True"/"False");
# its reader is bool.TryParse, which accepts either casing.
TRACER_SETTING_KEYS: Tuple[str, ...] = (
    "ghostRenderTracing", "mapRenderTracing", "ledgerTracing")


def render_settings_sidecar_baseline() -> str:
    """The exact settings.cfg body the harness stages: the three tracer flags
    pinned False, nothing else.

    The file format is ConfigNode CONTENTS ONLY (no node-name wrapper) - that is
    what ConfigNode.Save writes and what ConfigNode.Load expects back, and it is
    the shape the live instance's leaked file had.
    """
    return "".join("%s = False\n" % key for key in TRACER_SETTING_KEYS)


def parse_settings_sidecar(text: Optional[str]) -> Dict[str, str]:
    """Parse a settings.cfg body into {key: raw-value}.

    Tolerant on purpose (this only ever feeds a LOG line, never a decision that
    could red a run): blank lines, comment lines and brace lines are ignored, and
    a duplicate key keeps the LAST occurrence, matching ConfigNode.GetValue's
    single-value read being last-write-wins for our single-writer file.
    """
    out: Dict[str, str] = {}
    for raw in (text or "").splitlines():
        line = raw.strip()
        if not line or line.startswith("//") or line in ("{", "}"):
            continue
        if "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        if key:
            out[key] = value.strip()
    return out


def settings_sidecar_tracers_on(text: Optional[str]) -> List[str]:
    """The tracer keys the given sidecar body leaves switched ON, in
    TRACER_SETTING_KEYS order.

    Used to make the leak VISIBLE in the harness log when the baseline is
    written: an empty list means the instance was already clean, a non-empty one
    names exactly which tracer a previous run (or a hand edit) left on. Anything
    other than a parseable true is treated as not-on, mirroring the mod's
    bool.TryParse-or-default read.
    """
    values = parse_settings_sidecar(text)
    return [k for k in TRACER_SETTING_KEYS
            if values.get(k, "").strip().lower() == "true"]


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
    autoRecordOnLaunch=true / autoRecordOnEva=true via SetSetting. Injection-seeded
    scenarios (recordings present in the SAVE but never recorded live) return False -
    the first live S1.4 run proved count.max>0 is the WRONG suppression key:
    it red-flagged REC-001/REC-003 on a run that legitimately never records.
    M-C2 (F6): EVA-2 is a genuinely-recording run whose ONLY recording trigger is the
    autoRecordOnEva=true pin (no StartRecording, no autoRecordOnLaunch). Without this
    clause its REC-001/REC-003 marker rules would be SUPPRESSED and oracle invariant 5
    would be silently false on a run that really records."""
    driver = spec.get("driver", {}) or {}
    for step in driver.get("steps", []) or []:
        cmd = (step.get("cmd") or "")
        if cmd == "StartRecording":
            return True
        if cmd == "SetSetting":
            name = (step.get("args", {}) or {}).get("name", "")
            value = str((step.get("args", {}) or {}).get("value", "")).lower()
            if name in ("autoRecordOnLaunch", "autoRecordOnEva") and value == "true":
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
    # The MEASURED counterpart of the evaluated spec facets, mirroring the
    # [expectations.*] block shape ({"recordings": {"count": 7}}). Added AFTER
    # the schema-1 results already in results/ were written, so it is purely
    # ADDITIVE and OPTIONAL: it defaults to empty, every existing positional
    # construction still type-checks, and a consumer must treat ABSENT as "this
    # run predates the measurement" - never as zero. See
    # ``observed_expectation_facets`` for what lands in it and why.
    observed: Dict[str, Any] = field(default_factory=dict)


# M-B2 (design ~495): on activation ``world`` LEAVES this tuple -- the ledger-oracle
# verifier (chain slot 8) becomes its SOLE owner (vessel resource totals), so slot 7
# STOPS recording it as reserved and there is exactly ONE owner (no double-count).
# ``ledger`` was never reserved here (it is a tolerated-unknown block slot 7 ignores).
RESERVED_EXPECTATION_BLOCKS: Tuple[str, ...] = ("route", "rewind", "loop")


def observed_expectation_facets(recording_count: Optional[int]) -> Dict[str, Any]:
    """Build the MEASURED-facet dict verifier 7 records alongside its verdict.

    Why this exists: verifier 7 evaluated ``recordings.count`` against a window
    but recorded only the VERDICT, never the number. On a PASS the harness does
    not run collect-logs (design results layout / edge 18) and the produced save
    is transient, so a green run's measured count was UNRECOVERABLE post-hoc -
    which is exactly the number an operator needs to turn a provisional
    ``count = { min = 1, max = 10 }`` window into an honest pin. Recording it
    costs nothing (run.py already computed it for the comparison) and closes the
    loop: fly once green, read the count out of ``results/<runId>.json``, pin.

    Shape: mirrors the ``[expectations.*]`` spec surface, so a future measured
    facet slots in beside its spec counterpart without a format break
    (``{"recordings": {"count": 7}}``). ``recordings.count`` is presently the
    ONLY facet the recordings block declares (hence the only one to observe);
    the logContracts facets are regex predicates with no numeric counterpart
    worth persisting, and route/rewind/loop stay RESERVED.

    Recording is UNCONDITIONAL on the spec: a scenario that declares no count
    window still gets its measured count recorded, which is how a NEW scenario
    earns its first honest window. A ``None`` count (save unreadable / not
    counted) omits the key entirely - ABSENT means "not measured", never zero.
    """
    observed: Dict[str, Any] = {}
    if recording_count is not None:
        observed["recordings"] = {"count": int(recording_count)}
    return observed


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

    The result also carries ``observed`` - the MEASURED facets
    (``observed_expectation_facets``) - so a green run's numbers survive into
    ``results/<runId>.json`` instead of dying with the transient save.
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
    return ExpectationResult(status, tuple(mismatches), reserved,
                             observed_expectation_facets(recording_count))


# ---------------------------------------------------------------------------
# Anomaly sweep (design verifier 6 / N2). Pure over pre-grepped hit tokens.
# ---------------------------------------------------------------------------


def _anomaly_reasons(log_text: Optional[str]) -> List[str]:
    """Every ``reason=`` value on a Tier-C ANOMALY line, in first-seen order.

    Anchored on ``phase=Anomaly`` so only an actual EmitAnomaly raise counts. This
    is the whole point: the sweep used to be a bare substring search for each token
    over the entire KSP.log, which made any log line that merely NAMED a token an
    anomaly hit. It was not hypothetical - S1.7's first flight reddened on
    ``[Parsek][INFO][TestRunner] SpineDrive parity-drift: sampled=True skip=(none)
    hasMeas=True maxDev=0.0m tol=1989.4m over=False``, a PhaseSpineSwap test line
    reporting ZERO drift (``over=False``) whose own diagnostic label happened to be
    the token. Any test, comment echo or future message naming a token would red an
    otherwise clean run.
    """
    seen: List[str] = []
    for line in (log_text or "").splitlines():
        if ANOMALY_LINE_PHASE not in line:
            continue
        m = _ANOMALY_REASON_RE.search(line)
        if m is not None and m.group(1) not in seen:
            seen.append(m.group(1))
    return seen


def grep_anomaly_tokens(log_text: Optional[str]) -> List[str]:
    """The harness-owned Tier-C anomaly tokens actually RAISED in ``log_text``.

    Returned in ``ANOMALY_TOKENS`` order so the hit list is deterministic
    regardless of emit order.
    """
    raised = set(_anomaly_reasons(log_text))
    return [t for t in ANOMALY_TOKENS if t in raised]


def count_anomaly_tokens(log_text: Optional[str]) -> Dict[str, int]:
    """RAISE COUNTS per harness-owned Tier-C token (design verifier 6, budget form).

    Same anchoring as ``grep_anomaly_tokens`` (a ``phase=Anomaly`` line whose
    ``reason=`` field is the whole token), but counts every raise instead of
    collapsing to first-seen. This is the input the per-token COUNT BUDGET needs: a
    tolerance of "this anomaly is known-benign" and a tolerance of "this anomaly
    fires at most N times on a healthy run" are different claims, and only the
    second can catch a regression that turns a rare transient into a storm.

    Only tokens in ANOMALY_TOKENS appear (an ungated reason is reported through
    ``unlisted_anomaly_reasons``, never counted into a gate).

    ZERO-COUNT CONVENTION, and it differs from ``scan_unity_exceptions`` on purpose -
    both feed operator budget-sizing, so the difference is worth naming. Here a token
    that never fired is ABSENT (an empty dict on a clean run reads as "no anomaly
    raised"); there every pattern reports its zero ("we looked, and saw none"). The
    reason is what an absent key would otherwise mean: the anomaly set is fixed and
    grep-able, so absence is unambiguous, whereas an absent exception pattern could not
    be told apart from a scan that never ran.
    """
    counts: Dict[str, int] = {}
    gated = set(ANOMALY_TOKENS)
    for line in (log_text or "").splitlines():
        if ANOMALY_LINE_PHASE not in line:
            continue
        m = _ANOMALY_REASON_RE.search(line)
        if m is None:
            continue
        token = m.group(1)
        if token in gated:
            counts[token] = counts.get(token, 0) + 1
    return counts


def unlisted_anomaly_reasons(log_text: Optional[str]) -> List[str]:
    """Anomaly reasons RAISED but absent from ``ANOMALY_TOKENS`` (REPORT-ONLY).

    Non-gating by design. The mod raises several reasons the harness set does not
    carry (see the ANOMALY_TOKENS note), so a run can contain a real Tier-C raise
    that the sweep is structurally blind to. Widening the gating set is a decision
    with verdict consequences for every committed scenario; surfacing the drift is
    not. Sorted for a stable log line.
    """
    return sorted(r for r in _anomaly_reasons(log_text) if r not in ANOMALY_TOKENS)


# ---------------------------------------------------------------------------
# `allowedAnomalies` declaration surface (bare token + per-token count budget).
# ---------------------------------------------------------------------------

# The budgeted entry's keys. BACKWARD-COMPATIBLE BY CONSTRUCTION: an entry is
# EITHER a bare string (the historical form every committed spec uses, meaning
# "tolerate this token however often it fires") OR a table carrying `token` and
# `maxCount` ("tolerate up to N raises; the N+1st reds"). All 55 committed specs
# parse unchanged because none of them declares the table form.
ALLOWED_ANOMALY_TOKEN_KEY = "token"
ALLOWED_ANOMALY_MAX_COUNT_KEY = "maxCount"

# A bare token carries no ceiling. Sentinel rather than a huge int so the reporting
# layer can say "unbudgeted" instead of printing a magic number.
ANOMALY_BUDGET_UNLIMITED: Optional[int] = None


@dataclass(frozen=True)
class AllowedAnomalyParse:
    """Parsed ``expectations.allowedAnomalies`` (pure).

    ``budgets`` maps token -> ceiling, where the ceiling is ``None`` for a bare
    token (unbudgeted: tolerated at any count) and an ``int >= 0`` for the table
    form. ``errors`` are STRUCTURAL rejects (spec-invalid, no KSP boot);
    ``warnings`` are inert-declaration notes (a token the sweep can never raise).
    """
    budgets: Dict[str, Optional[int]] = field(default_factory=dict)
    errors: Tuple[str, ...] = tuple()
    warnings: Tuple[str, ...] = tuple()


def parse_allowed_anomalies(declared: Optional[Sequence]) -> AllowedAnomalyParse:
    """Parse the ``allowedAnomalies`` array into per-token budgets (pure).

    Accepted entry forms::

        "polyline-orbit-overlap"                          # bare  -> unbudgeted
        { token = "icon-teleport", maxCount = 3 }          # table -> reds at 4+

    A duplicate token keeps the TIGHTEST ceiling (a later bare entry cannot widen an
    earlier budget back to unlimited): the whole point of the budget is to be a
    ceiling, and a declaration that silently loosens one is the fail-open this
    mechanism exists to close.

    Structural rejects (errors): a NON-ARRAY declaration, a non-string / non-table
    entry, a table with no ``token``, a non-int or negative ``maxCount``, an unknown
    key in the table. Inert-declaration warnings: a token outside ANOMALY_TOKENS (the
    sweep can never raise it, so the tolerance does nothing) - RETIRED dead tokens are
    named as such.

    THE WHOLE-VALUE TYPE CHECK IS LOAD-BEARING, not defensive boilerplate. Python
    iterates a string by CHARACTER and a dict by KEY, so `allowedAnomalies =
    "line-blink"` or a bare inline table used to walk into per-character "tokens" -
    producing a garbage budget map with warnings only, and `validate_spec` still
    returning ok=True. A tolerance the author believes is in force and is not is a
    FALSE-RED risk, and it is the exact failure mode the misplaced-key guard was
    promoted from WARN to ERROR to close.
    """
    budgets: Dict[str, Optional[int]] = {}
    errors: List[str] = []
    warnings: List[str] = []
    if declared is not None and not isinstance(declared, (list, tuple)):
        return AllowedAnomalyParse(
            {},
            ("expectations.allowedAnomalies: %r must be an array of token strings or "
             "{ %s = \"...\", %s = N } tables (a bare string or table iterates by "
             "character / key into garbage budgets)"
             % (declared, ALLOWED_ANOMALY_TOKEN_KEY, ALLOWED_ANOMALY_MAX_COUNT_KEY),),
            tuple())
    for i, entry in enumerate(declared or ()):
        token: Optional[str] = None
        budget: Optional[int] = ANOMALY_BUDGET_UNLIMITED
        if isinstance(entry, str):
            token = entry
        elif isinstance(entry, dict):
            raw_token = entry.get(ALLOWED_ANOMALY_TOKEN_KEY)
            if not isinstance(raw_token, str) or not raw_token:
                errors.append(
                    "expectations.allowedAnomalies[%d]: a table entry requires a "
                    "non-empty `%s` (got %r)" % (i, ALLOWED_ANOMALY_TOKEN_KEY, entry))
                continue
            unknown = sorted(k for k in entry
                             if k not in (ALLOWED_ANOMALY_TOKEN_KEY,
                                          ALLOWED_ANOMALY_MAX_COUNT_KEY))
            if unknown:
                errors.append(
                    "expectations.allowedAnomalies[%d]: unknown key(s) %s (accepted: "
                    "%s, %s)" % (i, unknown, ALLOWED_ANOMALY_TOKEN_KEY,
                                 ALLOWED_ANOMALY_MAX_COUNT_KEY))
                continue
            token = raw_token
            if ALLOWED_ANOMALY_MAX_COUNT_KEY in entry:
                raw_max = entry[ALLOWED_ANOMALY_MAX_COUNT_KEY]
                if isinstance(raw_max, bool) or not isinstance(raw_max, int) or raw_max < 0:
                    errors.append(
                        "expectations.allowedAnomalies[%d].%s: %r must be a "
                        "non-negative integer raise ceiling"
                        % (i, ALLOWED_ANOMALY_MAX_COUNT_KEY, raw_max))
                    continue
                budget = int(raw_max)
        else:
            errors.append(
                "expectations.allowedAnomalies[%d]: %r must be a token string or a "
                "{ %s = \"...\", %s = N } table"
                % (i, entry, ALLOWED_ANOMALY_TOKEN_KEY, ALLOWED_ANOMALY_MAX_COUNT_KEY))
            continue

        if token not in ANOMALY_TOKENS:
            warnings.append(
                "expectations.allowedAnomalies[%d]: %r is %s, so the sweep can never "
                "raise it and this tolerance is INERT"
                % (i, token,
                   "a RETIRED dead token" if token in ANOMALY_TOKENS_DEAD
                   else "not a harness-owned Tier-C anomaly token"))
        if token in budgets:
            prior = budgets[token]
            # Tightest wins: a real ceiling beats unlimited, and the smaller of two
            # ceilings wins.
            if budget is None:
                budget = prior
            elif prior is not None:
                budget = min(prior, budget)
        budgets[token] = budget
    return AllowedAnomalyParse(budgets, tuple(errors), tuple(warnings))


def evaluate_anomaly_sweep(hit_tokens: Sequence[str], allowed_anomalies: Sequence,
                           hit_counts: Optional[Dict[str, int]] = None) -> List[str]:
    """Return the anomaly hits NOT tolerated by ``allowedAnomalies`` (verifier 6).

    ``hit_tokens`` is the set of harness-owned Tier-C anomaly tokens grepped from
    the KSP.log; a scenario only ADDS known-benign exceptions via
    ``allowedAnomalies`` (a DEDICATED field, never logContracts.forbidden), so
    the harness-owned sweep set stays fixed. Any hit not tolerated -> the caller
    reds PARSEK-FAIL(anomaly). Unknown tokens (not in ANOMALY_TOKENS) are ignored
    (the sweep set is fixed; a scenario cannot invent a new anomaly).

    TOLERANCE, two forms (see ``parse_allowed_anomalies``):
      - bare token -> tolerated at ANY count (the historical behavior, unchanged);
      - ``{ token, maxCount = N }`` -> tolerated up to N raises; N+1 reds.

    ``hit_counts`` is ``count_anomaly_tokens``'s per-token raise count. It is
    OPTIONAL so every existing 2-arg call still behaves identically: with no counts
    a hit is assumed to have fired ONCE, so any budget >= 1 tolerates it and a
    ``maxCount = 0`` declaration still reds. A budgeted token that is OVER budget is
    returned as a hit (with the count named by the caller's reporting row).
    """
    parse = parse_allowed_anomalies(allowed_anomalies)
    counts = hit_counts or {}
    unallowed: List[str] = []
    for t in hit_tokens:
        if t not in ANOMALY_TOKENS:
            continue
        if t not in parse.budgets:
            unallowed.append(t)
            continue
        budget = parse.budgets[t]
        if budget is None:
            continue
        if int(counts.get(t, 1)) > budget:
            unallowed.append(t)
    return unallowed


# ---------------------------------------------------------------------------
# Raw Unity exception scan (REPORT-ONLY by default; spec-armable). Pure.
# ---------------------------------------------------------------------------

# THE GAP THIS CLOSES. Every committed spec's `logContracts.forbidden` list carries
# PARSEK-AUTHORED tokens only (`\[Parsek\]\[ERROR\]` and friends), and the
# log-validation layer below it (`scripts/validate-ksp-log.ps1` ->
# `ParsekLogContractChecker`) parses ONLY `[Parsek]`-tagged lines - its five rules
# (SES-000/001, FMT-001/002, WRN-001, REC-001/003) are about session markers, the
# Parsek line FORMAT, WARN-level content and recording start/stop pairing. So a
# KSP.log full of raw Unity `NullReferenceException` stack traces or an IMGUI
# `ArgumentException: GUILayout` storm passes every gate the harness has: nothing
# in the chain looks at a line the mod did not write. No overlap is duplicated
# here - this scan is deliberately the complement of that layer.
#
# REPORT-ONLY BY DEFAULT, and that is not timidity: nobody has ever measured how
# many of these a healthy KSP 1.12 + Parsek boot emits (stock KSP itself throws
# during scene loads), so an armed ceiling picked without a calibration run would
# red live-proven scenarios on the next nightly. The counts land in the result JSON
# on EVERY run; an operator reads them off a few green runs, then arms
# `[expectations.unityExceptions] maxTotal = N` per scenario.
#
# Each entry is (name, regex). Matched per LINE (an exception header line; the
# stack-trace lines below it do not repeat the type name), so the count is
# "exception occurrences", not "log lines mentioning exceptions".
UNITY_EXCEPTION_PATTERNS: Tuple[Tuple[str, "re.Pattern"], ...] = (
    # The overwhelmingly common Unity/KSP crash-in-a-frame signature.
    ("NullReferenceException", re.compile(r"\bNullReferenceException\b")),
    # A destroyed UnityEngine.Object still being touched - the ghost/proto-vessel
    # lifecycle class this project has hit repeatedly.
    ("MissingReferenceException", re.compile(r"\bMissingReferenceException\b")),
    ("IndexOutOfRangeException", re.compile(r"\bIndexOutOfRangeException\b")),
    # IMGUI layout mismatch: one bad OnGUI frame emits this EVERY frame for the
    # rest of the scene, which is exactly the storm a count budget catches.
    ("ArgumentException: GUILayout", re.compile(r"\bArgumentException:\s*GUILayout\b")),
)

# The spec block that ARMS the scan. Absent (the state of all 55 committed specs)
# -> report-only.
UNITY_EXCEPTIONS_BLOCK = "unityExceptions"
UNITY_EXCEPTIONS_MAX_TOTAL_KEY = "maxTotal"

UNITY_EXCEPTIONS_STATUS_REPORT = "REPORT"


@dataclass(frozen=True)
class UnityExceptionResult:
    """Outcome of the raw-Unity-exception scan.

    ``status`` is ``REPORT`` when the scenario declares no
    ``[expectations.unityExceptions]`` block (counts recorded, verdict untouched),
    else ``PASS`` / ``FAIL``. ``gating`` mirrors that as a bool so the caller does
    not string-compare. ``counts`` is per-pattern; ``total`` is their sum.
    """
    status: str
    gating: bool
    total: int
    counts: Dict[str, int] = field(default_factory=dict)
    max_total: Optional[int] = None
    mismatches: Tuple[str, ...] = tuple()


def scan_unity_exceptions(log_text: Optional[str]) -> Dict[str, int]:
    """Count raw Unity exception occurrences per pattern in a KSP.log body (pure).

    `[Parsek]`-tagged lines are SKIPPED: a Parsek line that names an exception is
    the mod REPORTING a caught one (already covered by the `[Parsek][ERROR]`
    forbidden tokens and by WRN-001), while this scan is about the exceptions
    nobody caught. Counting both would double-signal the same event and make the
    number an operator has to calibrate against un-interpretable.

    Every pattern gets a key, including a zero, so a result JSON reads as a
    measurement ("we looked, and saw none") rather than an absence. This is the
    OPPOSITE convention to ``count_anomaly_tokens`` (which omits zeros) - deliberate,
    and explained there.

    FUTURE WORK, deliberately not done here: this is one of several full passes over
    ``log_text`` per run (anomaly reasons, anomaly counts, this scan). Consolidating
    them into a single walk is a real saving on a multi-hundred-MB KSP.log, but it
    couples four independent decisions into one loop and is not a change to make in a
    fail-open-closing commit.
    """
    counts: Dict[str, int] = {name: 0 for name, _ in UNITY_EXCEPTION_PATTERNS}
    for line in (log_text or "").splitlines():
        if "[Parsek]" in line:
            continue
        for name, pat in UNITY_EXCEPTION_PATTERNS:
            if pat.search(line) is not None:
                counts[name] += 1
    return counts


def evaluate_unity_exceptions(counts: Optional[Dict[str, int]],
                              block: Optional[Dict]) -> UnityExceptionResult:
    """Judge the scanned counts against an optional ``[expectations.unityExceptions]``.

    ABSENT block -> ``REPORT`` (non-gating), which is the state of every committed
    spec and the reason this cannot move a nightly verdict. A DECLARED block with
    ``maxTotal = N`` gates: total > N -> ``FAIL`` with a mismatch string naming the
    per-pattern breakdown. A declared block with no ``maxTotal`` still reports (it
    declares nothing to gate on).
    """
    counts = dict(counts or {})
    total = sum(int(v) for v in counts.values())
    if not isinstance(block, dict):
        return UnityExceptionResult(UNITY_EXCEPTIONS_STATUS_REPORT, False, total, counts)
    raw_max = block.get(UNITY_EXCEPTIONS_MAX_TOTAL_KEY)
    if raw_max is None or isinstance(raw_max, bool) or not isinstance(raw_max, int):
        return UnityExceptionResult(UNITY_EXCEPTIONS_STATUS_REPORT, False, total, counts)
    max_total = int(raw_max)
    if total <= max_total:
        return UnityExceptionResult("PASS", True, total, counts, max_total)
    breakdown = ", ".join("%s=%d" % (n, counts.get(n, 0))
                          for n, _ in UNITY_EXCEPTION_PATTERNS if counts.get(n, 0))
    return UnityExceptionResult(
        "FAIL", True, total, counts, max_total,
        ("unityExceptions.total %d > maxTotal %d (%s)" % (total, max_total, breakdown),))


def validate_unity_exception_expectations(block: Optional[Dict]) -> List[str]:
    """Validate the optional ``[expectations.unityExceptions]`` block (pre-launch).

    Structural only, and deliberately strict about the ONE key it accepts: a
    misspelled ceiling that silently degrades to report-only is precisely the
    fail-open this block exists to close.
    """
    if block is None:
        return []
    if not isinstance(block, dict):
        return ["expectations.%s: must be a table" % (UNITY_EXCEPTIONS_BLOCK,)]
    errs: List[str] = []
    unknown = sorted(k for k in block if k != UNITY_EXCEPTIONS_MAX_TOTAL_KEY)
    if unknown:
        errs.append("expectations.%s: unknown key(s) %s (accepted: %s)"
                    % (UNITY_EXCEPTIONS_BLOCK, unknown, UNITY_EXCEPTIONS_MAX_TOTAL_KEY))
    if UNITY_EXCEPTIONS_MAX_TOTAL_KEY in block:
        raw = block[UNITY_EXCEPTIONS_MAX_TOTAL_KEY]
        if isinstance(raw, bool) or not isinstance(raw, int) or raw < 0:
            errs.append("expectations.%s.%s: %r must be a non-negative integer"
                        % (UNITY_EXCEPTIONS_BLOCK, UNITY_EXCEPTIONS_MAX_TOTAL_KEY, raw))
    return errs


def unity_exception_expectation_warnings(block: Optional[Dict]) -> List[str]:
    """Inert-declaration warnings for ``[expectations.unityExceptions]``.

    A declared block with no ``maxTotal`` GATES NOTHING - it degrades silently to the
    same report-only behavior an absent block gets. That is not an error (the block is
    still well-formed, and a future key could make it meaningful), but an author who
    wrote the header believing they had armed a ceiling must be told they have not.
    WARN rather than ERROR for the same reason the inert-token case is a warning:
    nothing fails at run time, and hard-rejecting would make the block's own future
    growth a spec-invalid."""
    if not isinstance(block, dict):
        return []
    if UNITY_EXCEPTIONS_MAX_TOTAL_KEY in block:
        return []
    return ["expectations.%s: declared with no `%s`, so it gates NOTHING - the scan "
            "stays REPORT-ONLY exactly as if the block were absent. Add "
            "`%s = N` (sized from a green run's unityExceptions.total) or delete the "
            "block." % (UNITY_EXCEPTIONS_BLOCK, UNITY_EXCEPTIONS_MAX_TOTAL_KEY,
                        UNITY_EXCEPTIONS_MAX_TOTAL_KEY)]


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

# The stock-award CROSS-CHECK mode (2026-07-29, shipped with the pattern rewrite).
# `report` (the DEFAULT, and what all 55 committed specs take by declaring nothing)
# records every unmatched captured award as a REPORT-ONLY oracle divergence;
# `gate` restores the hard PARSEK-FAIL(ledger) the code always intended.
#
# WHY THE DEFAULT IS `report` AND NOT `gate`. The cross-check was WRITTEN as a hard
# gate, but it has never once run with a working capture: STOCK_AWARD_PATTERNS
# matched invented shapes, so the captured set was empty on every flight and the
# gate could not fire. Making the patterns real turns that dormant gate live in one
# step, against scenarios NOBODY has measured it on - and the measurement that does
# exist says it would red them: the CL-1 flights show a career pad hop tripping
# `RecordsSpeed` / `FirstLaunch` / `RecordsAltitude` funds awards plus two
# `Progression` rep awards, none of which any L1 seam manifest declares. So the
# mechanism lands report-only, the counts land in the result JSON, and an operator
# arms `captureCrossCheck = "gate"` per scenario once a green run shows what that
# scenario's baseline award set actually is.
LEDGER_CAPTURE_CROSS_CHECK_KEY = "captureCrossCheck"
LEDGER_CAPTURE_CROSS_CHECK_REPORT = "report"
LEDGER_CAPTURE_CROSS_CHECK_GATE = "gate"
LEDGER_CAPTURE_CROSS_CHECK_VALUES: Tuple[str, ...] = (
    LEDGER_CAPTURE_CROSS_CHECK_REPORT, LEDGER_CAPTURE_CROSS_CHECK_GATE,
)


def capture_cross_check_gates(ledger_block: Optional[Dict]) -> bool:
    """Does this scenario's ``[expectations.ledger]`` ARM the stock-award
    cross-check (pure)?

    True only for an explicit ``captureCrossCheck = "gate"``. An absent block, an
    absent key, or an unparsed value -> False (report-only). Fail-OPEN is correct
    here and only here: the alternative is a gate armed by accident on a scenario
    whose award baseline nobody has measured, which is how a green nightly turns
    red on a regex rather than on a defect. A malformed value never reaches this
    function - ``validate_ledger_expectations`` rejects it pre-launch."""
    if not isinstance(ledger_block, dict):
        return False
    return ledger_block.get(LEDGER_CAPTURE_CROSS_CHECK_KEY) == LEDGER_CAPTURE_CROSS_CHECK_GATE


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
    if LEDGER_CAPTURE_CROSS_CHECK_KEY in ledger_block:
        mode = ledger_block.get(LEDGER_CAPTURE_CROSS_CHECK_KEY)
        if mode not in LEDGER_CAPTURE_CROSS_CHECK_VALUES:
            errs.append("expectations.ledger.%s: %r not in %s"
                        % (LEDGER_CAPTURE_CROSS_CHECK_KEY, mode,
                           list(LEDGER_CAPTURE_CROSS_CHECK_VALUES)))
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
    define ``reason`` (the stock ``TransactionReasons`` key), ``guid`` (contract
    identity) or ``subject`` (per-subject science id). ``measuredFrom`` cites the
    ARCHIVED REAL LOG LINE the pattern was derived from - not a design paragraph, a
    line a flight actually produced. A shape nobody has measured is NOT enumerated
    (see the science note below); inventing one is what made this table dead."""
    kind: str
    facet: str
    regex: "re.Pattern"
    emitter: str
    measured_from: str = ""


# UT correlation source (design ~390): a stock award line is not self-stamped, so
# the capture assigns ``ut`` by the NEAREST UT-stamped [Parsek] line at or before
# it. Parsek log lines carry ``ut=<value>``.
_STOCK_UT_RE = re.compile(r"\[Parsek\].*?\but=(?P<ut>-?\d+(?:\.\d+)?)")

# THE PATTERNS WERE DEAD AGAINST REAL LOGS, and this is the rewrite (2026-07-29,
# known-gate 3). The previous enumeration matched INVENTED shapes - keyed forms
# like `ContractSystem ... funds=<n>` and `ResearchAndDevelopment ... delta=<n>` -
# that no KSP build emits. Consequence: `parse_stock_award_lines` captured NOTHING
# on every run ever flown, so `unmatched_captured_awards` had an empty input and
# the ledger oracle's leg-A independence cross-check was a STRUCTURAL NO-OP
# dressed as a gate.
#
# The real KSP 1.12 idiom, MEASURED (both CL-1 flights, 2026-07-28, identical
# across them - quoted in docs/dev/todo-and-known-bugs.md:248-257 and, for the
# Progression line, docs/dev/done/todo-and-known-bugs-v4.md:1954):
#
#     Added -9.999828 (-10) reputation: 'VesselLoss'.
#     Added 0.9999995 (1) reputation: 'Progression'.
#
# i.e. `Added <appliedDelta> (<nominal>) reputation: '<TransactionReasons key>'`. The
# leading number is the APPLIED per-event DELTA (the parenthesised value is stock's
# rounded display of the nominal); the quoted tail is the stock reason key, which
# is the only identity a stock award line carries.
#
# ONLY REPUTATION IS CAPTURABLE. KSP LOGS NO FUNDS AWARD AND NO SCIENCE AWARD, and
# that is a property of the game, not a gap in our measurement (2026-07-29,
# known-gate 3 follow-up). Three independent proofs, all reproducible:
#
#   1. Assembly string table (KSP 1.12.5 Assembly-CSharp.dll, UTF-16LE literal
#      counts): `") reputation: '"` = 1, `") reputation. Total Rep: "` = 1,
#      `" funds: '"` = 0, `" science: '"` = 0. A concatenated Debug.Log keeps its
#      format fragment as one literal, so a zero count is conclusive: no code path
#      can emit `Added <n> funds: '<reason>'` or the science equivalent.
#   2. Decompiled bodies: `Funding.AddFunds(double, TransactionReasons)` and
#      `ResearchAndDevelopment.AddScience(float, TransactionReasons)` mutate the pool
#      and fire GameEvents with NO Debug.Log of any kind. Only `Reputation` logs.
#   3. Field corpus: 137 collected KSP.logs, including a career run that credited
#      +4800 `RecordsSpeed` milestone funds (logs/2026-07-10_2339_rerun4-green),
#      contain ZERO stock funds/science award lines and exactly the rep lines above.
#
# The `Added 4800 funds: 'RecordsSpeed'` line the 2026-07-29 rewrite shipped was
# NEVER MEASURED - it was composed by analogy from the rep line. Its cited source
# (CL-1-pod-impact.toml "progress milestones: RecordsSpeed funds=4800") quotes
# PARSEK's own `[Parsek][INFO][GameStateRecorder] Game state: MilestoneAchieved ...
# funds=4800` line, and `parse_stock_award_lines` skips [Parsek]-tagged lines. So the
# rewrite reproduced, on the funds facet, the exact defect it closed on the others:
# a pattern and its tests agreeing with each other and both disagreeing with KSP.
# The funds pattern is RETIRED to STOCK_AWARD_PATTERNS_DEAD below rather than left
# in place advertising coverage it cannot have (same call, same reasoning, as the
# `icon-jump` retirement in ANOMALY_TOKENS_DEAD).
#
# KNOWN CAPTURE GAPS, both real KSP lines this enumeration deliberately does NOT
# match, because neither carries a TransactionReasons key to correlate on:
#   - `Reputation.addReputation_discrete` logs "Adding ...", not "Added ...".
#   - The no-reason `AddReputation` branch logs
#     `Added <n> (<r>) reputation. Total Rep: <total>` - period, not colon.
# An award on either path moves the produced save, so the seam-declared-vs-save diff
# still catches it; only the leg-A corroboration is blind to it.
STOCK_AWARD_PATTERNS: Tuple[StockAwardPattern, ...] = (
    # Reputation - the ONLY stock award KSP writes to the log. `kind` is the generic
    # stock-award kind (see oracle.KINDS): a stock line names its TransactionReasons
    # key, not a manifest semantic, so mapping 'CrewRecruited' onto `kerbal-hire`
    # would be an unmeasured inference layered on top of the capture. The captured
    # amount is the APPLIED delta (post stock rep curve), which is why
    # `to_entry_dict` stamps repMode=applied rather than letting the oracle re-curve
    # a number that is already curved.
    StockAwardPattern(
        "stock-reputation-award", "reputation",
        re.compile(r"\bAdded\s+(?P<amount>-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s*"
                   r"(?:\(\s*-?\d+(?:\.\d+)?\s*\)\s*)?"
                   r"reputation:\s*'(?P<reason>[^']*)'"),
        "Reputation.AddReputation",
        "Added -9.999828 (-10) reputation: 'VesselLoss'.  (CL-1 flights 1+2, "
        "2026-07-28); Added 0.9999995 (1) reputation: 'Progression'. "
        "(logs/2026-04-19_0049_career-ledger, archived in todo-and-known-bugs-v4.md:1954)"),
)

# RETIRED patterns: enumerated once, emitted by NOTHING in KSP, REMOVED from
# STOCK_AWARD_PATTERNS (2026-07-29). Kept as a named constant for the same two
# mechanical reasons ANOMALY_TOKENS_DEAD is: the retirement is evidenced rather than
# silent, and a test asserts the shape stays unmatched by the live enumeration, so a
# future KSP build that DOES start logging funds reds here instead of quietly
# re-opening a dead facet. Removing these moves no verdict by construction - a shape
# no build emits can never be captured.
#
# INVARIANT: this tuple and STOCK_AWARD_PATTERNS are DISJOINT by `kind`.
STOCK_AWARD_PATTERNS_DEAD: Tuple[StockAwardPattern, ...] = (
    StockAwardPattern(
        "stock-funds-award", "funds",
        re.compile(r"\bAdded\s+(?P<amount>-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s*"
                   r"(?:\(\s*-?\d+(?:\.\d+)?\s*\)\s*)?"
                   r"funds:\s*'(?P<reason>[^']*)'"),
        "Funding.AddFunds (NO Debug.Log exists in this method)",
        "NEVER MEASURED - composed by analogy 2026-07-29, retired the same day. "
        "Assembly-CSharp.dll contains zero occurrences of \" funds: '\"."),
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
    ordinal (the seqKey when ``ut`` is null). Every captured amount is a DELTA.

    ``reason`` is the stock ``TransactionReasons`` key the award line quotes
    (``'VesselLoss'``, ``'RecordsSpeed'``, ...). It is the ONLY identity a stock
    award line carries - there is no guid and no subject id on one - so it is what
    distinguishes two same-amount awards at the same UT (``RecordsSpeed`` 4800 and
    ``RecordsAltitude`` 4800 fired on the same CL-1 hop; without the reason in the
    dedupe key they would collapse into one). OPTIONAL with a default so every
    existing positional/keyword construction still builds."""
    kind: str
    facet: str
    amount: float
    contract_guid: str
    subject_id: str
    ut: Optional[float]
    seq: int
    raw_line: str
    reason: str = ""

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
        if self.facet == "reputation":
            # A stock rep award line reports the APPLIED (post-curve) delta - the
            # measured `-9.999828` for a nominal -10 IS the curve output. Stamping
            # `applied` stops the oracle putting a second curve pass over a number
            # that already carries one (the 15.1 double-curve distortion).
            d["repMode"] = "applied"
        if self.ut is not None:
            d["ut"] = self.ut
        if self.contract_guid:
            d["contractGuid"] = self.contract_guid
        if self.subject_id:
            d["subjectIds"] = [self.subject_id]
        if self.reason:
            d["stockReason"] = self.reason
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
                ut=last_ut, seq=idx, raw_line=line.strip(),
                reason=str(gd.get("reason") or "")))
            break
    return StockCaptureResult(tuple(captured), rejected, len(captured))


def _captured_identity(award: "CapturedAward") -> str:
    """The identity a captured award carries: contract guid, else science subject,
    else the stock reason key. The reason fallback is load-bearing for the rewritten
    patterns - a stock award line has NO guid and NO subject, so without it two
    DIFFERENT awards of the same size at the same UT (CL-1 measured `RecordsSpeed`
    4800 and `RecordsAltitude` 4800 on one hop) share an identity and the dedupe
    silently collapses them into one effect."""
    return award.contract_guid or award.subject_id or award.reason


def dedupe_captured_awards(captured: Sequence[CapturedAward]) -> List[CapturedAward]:
    """Dedupe captured awards on ``(seqKey, kind, contractGuid|subjectId|reason,
    roundedAmount)`` keeping the FIRST (design ~404 / edge 2). A stock line
    re-emitted on a scene reload at the SAME seqKey is one effect; a genuine second
    identical award at a DISTINCT seqKey survives (the seqKey is in the key).
    ``roundedAmount`` is the amount to 3 decimals (design ~402) so float-format
    jitter across re-emitted lines does not defeat the dedupe."""
    seen = set()
    out: List[CapturedAward] = []
    for c in captured:
        ident = _captured_identity(c)
        key = (c.seq_key, c.kind, ident, round(c.amount, 3))
        if key in seen:
            continue
        seen.add(key)
        out.append(c)
    return out


# Per-facet agreement window for captured-vs-seam corroboration, mirroring
# oracle.Tolerances defaults. run.py passes the run's ACTUAL tolerances; this is the
# fallback for a direct call. Reputation needs a real window rather than an exact
# compare: a seam entry declares the NOMINAL delta (-10) while the stock line reports
# the APPLIED, post-curve one (the measured -9.999828).
DEFAULT_CAPTURE_MATCH_TOLERANCES: Dict[str, float] = {
    "funds": 1.0, "science": 0.1, "reputation": 0.1,
}


def _entry_facet_amount(entry, facet: str) -> float:
    """The seam entry's declared delta on ``facet`` (structural read, duck-typed so
    this module still imports nothing from oracle). Unknown facet -> 0.0."""
    try:
        return float(getattr(entry, facet, 0.0) or 0.0)
    except (TypeError, ValueError):
        return 0.0


def _identity_conflicts(entry, award: "CapturedAward") -> bool:
    """True when BOTH sides carry a STRUCTURED identity and they disagree.

    Structured = a contract guid or a science subject id. A real stock award line
    carries NEITHER (only its reason key), so for the funds / reputation lines this is
    always False and the match falls to seqKey + facet + amount. When the award DOES
    carry one (a future guid/subject-bearing capture), it must be among the entry's -
    fail-closed, and the multi-subject rule (item 10) is preserved: ANY declared
    subject id explains an award on that subject."""
    award_ident = award.contract_guid or award.subject_id
    if not award_ident:
        return False
    declared = _entry_identities(getattr(entry, "contract_guid", ""),
                                getattr(entry, "subject_ids", ()) or ())
    if declared == [""]:
        return False
    return award_ident not in declared


def unmatched_captured_awards(seam_entries, captured: Sequence[CapturedAward],
                              facet_tolerances: Optional[Dict[str, float]] = None
                              ) -> List[CapturedAward]:
    """Return captured awards NOT explained by a seam-declared entry (design edge 4).

    THE CORROBORATION KEY IS (seqKey, FACET, AMOUNT), NOT kind. `kind` was the join
    until 2026-07-29 and became structurally unsatisfiable the moment the capture
    started stamping the GENERIC `stock-funds-award` / `stock-reputation-award` kinds
    (oracle.KINDS): a seam entry carries a scenario semantic (`kerbal-hire`,
    `facility-upgrade`, ...) and a captured line carries the generic one, so the two
    can never be equal and EVERY captured award reported "unexpected" - including the
    scenario's own declared one. Reproduced against L1-hire-kerbal-career, whose own
    -62113 hire debit read as an unexpected award, which would have made
    `captureCrossCheck = "gate"` impossible to arm and the documented escalation path
    unwalkable. Kind cannot be repaired by mapping reasons onto semantics (nobody has
    measured such a mapping); what BOTH sides do carry comparably is the seqKey, the
    POOL and the AMOUNT.

    An award is EXPECTED iff some seam entry:
      - shares its type-tagged ``seq_key``;
      - declares a NON-ZERO delta on the award's facet (an entry that does not touch
        that pool cannot explain an award on it);
      - agrees on the amount within the facet tolerance (reputation needs the window:
        the entry is NOMINAL, the stock line is post-curve APPLIED);
      - does not CONFLICT on a structured identity (guid / science subject) when both
        sides carry one - fail-closed, multi-subject preserved;
      - and, when the entry declares ``stockReason``, lists the award's stock reason
        key. That optional field TIGHTENS the match for an author who has read a green
        run's ``capturedRaw`` and wants the entry pinned to a named stock effect.

    ONE-TO-ONE PER (ENTRY, POOL): a match CONSUMES the pair ``(entry, facet)``, so one
    declared effect explains at most one award ON EACH POOL IT DECLARES. That is what
    stops the CL-1 pair (`RecordsSpeed` 4800 and `RecordsAltitude` 4800, same UT, same
    size) from BOTH corroborating a single declared 4800 - the second stays unexpected,
    which is the correct signal.

    The PER-POOL part is not a refinement, it is the multi-facet case: a
    contract-complete entry declares funds AND reputation, and stock logs those as TWO
    separate award lines at the same seqKey. Consuming per ENTRY let whichever line
    matched first swallow the whole entry and left its sibling permanently
    "unexpected" (reproduced: a funds 1000 + rep 5 entry against its own two log lines
    -> 1 unexpected). oracle's fill path already documents exactly this pairing and
    carries a facet filter for it; this is the same rule on the matching side. An entry
    that declares one pool is unchanged - it can still only ever explain one award.

    INDEPENDENCE IS PRESERVED (M-B2). This function only classifies; it never feeds
    ``compute_expected``, so a captured amount is still never summed into EXPECTED,
    and corroboration does not touch ``diff_expected_vs_parsed`` - the seam-declared
    vs produced-save leg reds exactly as before whether or not an award corroborated.
    Corroboration can only SUPPRESS the extra unexpected-award row, never weaken the
    save diff.

    ``seam_entries`` are oracle.ManifestEntry objects, read structurally so this
    imports nothing from oracle.

    WHAT THIS RETURNS IS NOT AUTOMATICALLY A RED. The run.py caller decides whether an
    unmatched award is a HARD divergence or a REPORT-ONLY row from the scenario's
    ``[expectations.ledger] captureCrossCheck`` mode. Report-only is the default
    because nobody has yet flown a run with a LIVE capture: an L1 career scenario also
    trips stock MILESTONE awards no seam manifest declares (those stay unexpected and
    are exactly what an operator must review before arming)."""
    tolerances = facet_tolerances or DEFAULT_CAPTURE_MATCH_TOLERANCES
    available = list(seam_entries or ())
    # CANDIDATE ORDER: `stockReason`-PINNED entries first. The match is greedy (first
    # acceptable entry wins, no bipartite search), so with one pinned entry and one
    # unconstrained entry of the same amount at the same seqKey, LOG ORDER decided the
    # outcome: an award whose reason the pinned entry names could take the
    # unconstrained entry instead, stranding the pinned one and leaving the second
    # award unexpected. That penalizes exactly the author who did the extra work of
    # pinning reasons. Trying constrained entries first fixes it in both log orders,
    # because a pinned entry only accepts the award it names. Stable sort, so
    # declaration order is preserved inside each group.
    order = sorted(range(len(available)),
                   key=lambda i: 0 if getattr(available[i], "stock_reasons", ()) else 1)
    # Consumption is keyed (entry index, FACET) - see the per-pool rule above.
    consumed = set()
    out: List[CapturedAward] = []
    for c in captured:
        tol = float(tolerances.get(c.facet, 0.0))
        matched = False
        for i in order:
            if (i, c.facet) in consumed:
                continue
            e = available[i]
            if e.seq_key != c.seq_key:
                continue
            declared_amount = _entry_facet_amount(e, c.facet)
            if declared_amount == 0.0:
                continue
            if abs(declared_amount - c.amount) > tol:
                continue
            if _identity_conflicts(e, c):
                continue
            reasons = tuple(getattr(e, "stock_reasons", ()) or ())
            if reasons and c.reason not in reasons:
                continue
            consumed.add((i, c.facet))
            matched = True
            break
        if not matched:
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
    # EVA-4 2026-07-25: a post-mission OUTCOME step failed on a MISSION-OK run
    # (see SEAM_VERB_POST_MISSION_ROLE). Named separately from "expectation" on
    # purpose - it is the CAUSE a sweep reader wants, where the expectation rows
    # are the downstream symptoms.
    "mission-outcome",
)

# Subkinds a bugId-ONLY expectedFail key may NOT demote to EXPECTED-FAIL. An
# `[expectedFail] bugId = "..."` with no `subkind` matches ANY PARSEK-FAIL, which is
# the documented v1 adaptation - but EXPECTED-FAIL is GREEN (it sets lastGreen in
# compute_coverage and exits 0), so a scenario quarantined for one Parsek defect would
# silently absorb an unrelated subject death too. A quarantine must never be able to
# turn "the flight killed the kerbal it existed to save" green; demoting that requires
# spelling the subkind out.
NEVER_BUGID_ONLY_SUBKINDS: Tuple[str, ...] = ("mission-outcome",)

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
        # bugId-only demotion, EXCEPT for the subkinds in NEVER_BUGID_ONLY_SUBKINDS. A
        # quarantine key is a statement about ONE tracked Parsek defect; letting it also
        # swallow "the flight killed its own subject" would re-open the exact fail-open
        # this subkind exists to close - and EXPECTED-FAIL is in GREEN_VERDICTS, sets
        # lastGreen in compute_coverage, and exits 0. Demoting a subject death requires
        # naming it explicitly (subkind = "mission-outcome").
        return base_subkind not in NEVER_BUGID_ONLY_SUBKINDS
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
      post-mission outcome step unmet -> PARSEK-FAIL(mission-outcome)
      log-contract / results / anomaly / unity-exception / expectation / ledger
          -> PARSEK-FAIL
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
            if verifiers.get("mission_outcome_unmet", False):
                # EVA-4 2026-07-25: a post-mission OUTCOME step failed on a run whose
                # mission returned MISSION-OK. Deliberately ahead of the three
                # contract verifiers below, because they see the SYMPTOMS (a missing
                # completion token, a forbidden ERROR line, a short recordings count)
                # while this row names the CAUSE, and a subkind that names the cause
                # is what a sweep reader needs. Deliberately PARSEK-FAIL rather than a
                # retryable driver-INVALID: routing it through the driver stage would
                # (1) preempt and SKIP every verifier below, throwing away the very
                # evidence that made this run diagnosable, and (2) let an intermittent
                # subject death retry into a PASS-with-a-flake-note. The carve-out
                # this closes made the same argument in reverse ("NOT a driver-INVALID
                # a retry would paper over"), so this keeps its reasoning and only
                # stops assuming a spec author will always have written the regex.
                base = V(VERDICT_PARSEK_FAIL, "mission-outcome",
                         "a post-mission outcome step failed on a MISSION-OK run")
            elif verifiers.get("log_validate_failed", False):
                base = V(VERDICT_PARSEK_FAIL, "log-contract", "log validation failed")
            elif verifiers.get("results_failed", False) or verifiers.get("results_mismatch", False):
                base = V(VERDICT_PARSEK_FAIL, "results", "results FAIL rows or count mismatch")
            elif verifiers.get("anomaly_hit", False):
                base = V(VERDICT_PARSEK_FAIL, "anomaly", "unallowed Tier-C anomaly line")
            elif verifiers.get("unity_exceptions_over_budget", False):
                # Only reachable for a scenario that DECLARED
                # [expectations.unityExceptions]; the scan is report-only otherwise,
                # so this branch is inert for every committed spec today.
                base = V(VERDICT_PARSEK_FAIL, "unity-exception",
                         "raw Unity exception count over the declared maxTotal")
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


# ---------------------------------------------------------------------------
# The unmet-mission tail (design "The unmet-mission tail"). Pure.
# ---------------------------------------------------------------------------


def step_id_for_index(index: int) -> str:
    """The harness-assigned monotonic seam id for a zero-based step index.

    ONE definition shared by the drive loop and the tail planner, so a planned
    skip row and the line the drive loop would have written carry the same id.
    Ids are index-derived, never sequence-derived: skipping a step does NOT
    renumber the ones after it, so a result row still points at the spec's step.
    """
    return "%04d" % (index + 1)


def seam_verb_tail_role(verb: str) -> str:
    """The TAIL ROLE of a seam verb (cleanup / inert / world-mutating).

    An UNKNOWN verb FAILS SAFE to ``world-mutating``: an unclassified verb is
    presumed to do something, so the unmet tail skips it rather than driving a
    verb nobody has reasoned about. The companion unit cell asserts every
    IMPLEMENTED_SEAM_VERBS entry has an explicit row, so this fallback covers a
    typo or an unreleased verb, never a shipped one.
    """
    return SEAM_VERB_TAIL_ROLE.get(verb, TAIL_ROLE_WORLD_MUTATING)


def spec_skips_tail_on_unmet_mission(spec: Dict) -> bool:
    """Read ``[driver].skipTailOnUnmetMission``, defaulting to the SAFE True.

    Absent / non-bool -> the default (validate_spec is the place that reds a
    non-bool; this reader never has to guess what a string "false" meant)."""
    driver = (spec or {}).get("driver", {}) or {}
    value = driver.get(SKIP_TAIL_ON_UNMET_MISSION_KEY)
    if isinstance(value, bool):
        return value
    return SKIP_TAIL_ON_UNMET_MISSION_DEFAULT


@dataclass(frozen=True)
class TailStepDisposition:
    index: int          # zero-based index into driver.steps
    step_id: str        # the id the drive loop assigns that index
    cmd: str            # "" for a mission-kind step (never reachable in a tail)
    role: str           # one of TAIL_ROLES
    run: bool           # True = still driven, False = skipped


@dataclass(frozen=True)
class UnmetTailPlan:
    skip_enabled: bool
    dispositions: Tuple[TailStepDisposition, ...]
    run_indices: Tuple[int, ...]
    skipped_indices: Tuple[int, ...]
    summary: str


def plan_unmet_mission_tail(steps: Sequence[Dict], mission_index: int,
                            skip_tail: bool = SKIP_TAIL_ON_UNMET_MISSION_DEFAULT
                            ) -> UnmetTailPlan:
    """Decide which post-mission seam steps still run after an UNMET mission step.

    Motivating incident (EVA-4-atmo-chute flight 1, 2026-07-24): the mission
    ASSERT-FAILed with ``eva-window-missed`` (the pod was descending at -295 m/s,
    never inside the EVA envelope) and the harness drove the tail anyway, EVAing a
    kerbal out of a pod at terminal velocity ~356 m above the ground. That tail was
    harmless while every post-mission tail was StopRecording / CommitTree /
    FlushAndQuit; it stopped being harmless the moment a tail contained IRREVERSIBLE
    IN-WORLD actions.

    The rule: after an unmet mission, drive the CLEANUP steps only (``role ==
    cleanup``), skip everything else. Steps at or before ``mission_index`` are NOT
    part of the tail and never appear in the plan -- they already ran, and the
    pre-mission steps are what gate driver validity.

    Skipping costs the run NOTHING it could have used: an unmet mission is already
    a terminal-for-this-attempt driver-INVALID at ``classify_verdict``'s "driver stage
    failed" branch, which precedes EVERY save-reading verifier in that chain, so the
    analyzer (triage-only),
    logValidate / testResults / anomalySweep / expectations (SKIPPED on
    ``not driver_valid``) and the ledger oracle (SKIPPED, reason driver-invalid)
    contribute nothing to the verdict on this path whether or not the tail ran.
    What skipping buys: no in-world action the scenario's own design says cannot
    happen, no deferral budget burned per failed attempt (EvaExit 120s +
    EvaChuteDeploy 420s, doubled under retry-once), and a collected save / log that
    carries only what the flight actually did.

    ``skip_tail=False`` (spec opt-out) returns the legacy plan: every tail step
    runs, ``skipped_indices`` empty. The dispositions are still computed so the
    harness can log WHAT it is about to drive.
    """
    dispositions: List[TailStepDisposition] = []
    run_indices: List[int] = []
    skipped_indices: List[int] = []
    for i in range(mission_index + 1, len(steps)):
        step = steps[i] or {}
        if step.get("phase") == "mission":
            # A second mission-kind step is rejected by validate_spec ("exactly one"),
            # so this is unreachable for an admitted spec; classify it world-mutating
            # (it flies a vessel) rather than silently treating it as runnable.
            cmd, role = "", TAIL_ROLE_WORLD_MUTATING
        else:
            cmd = str(step.get("cmd", ""))
            role = seam_verb_tail_role(cmd)
        run = (not skip_tail) or role == TAIL_ROLE_CLEANUP
        dispositions.append(TailStepDisposition(i, step_id_for_index(i), cmd, role, run))
        (run_indices if run else skipped_indices).append(i)

    if not skip_tail:
        summary = ("skipTailOnUnmetMission=false: driving the FULL %d-step tail "
                   "despite the unmet mission" % len(dispositions))
    elif not dispositions:
        summary = "no post-mission tail steps"
    elif not skipped_indices:
        summary = "tail is cleanup-only (%d step(s)); nothing to skip" % len(run_indices)
    else:
        summary = ("skipping %d of %d tail step(s) [%s]; driving cleanup [%s]"
                   % (len(skipped_indices), len(dispositions),
                      ", ".join("%s:%s(%s)" % (d.step_id, d.cmd or "mission", d.role)
                                for d in dispositions if not d.run),
                      ", ".join("%s:%s" % (d.step_id, d.cmd)
                                for d in dispositions if d.run) or "none"))
    return UnmetTailPlan(bool(skip_tail), tuple(dispositions),
                         tuple(run_indices), tuple(skipped_indices), summary)


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


# ---------------------------------------------------------------------------
# Cross-run DURATION record (2026-07-25 telemetry audit, finding G5).
#
# flake.json tracked {total, numerator, rate, quarantined} and NOTHING about how
# long a scenario takes, and every artifact that carried a duration was
# gitignored (results/*.json, results/summary.txt, coverage/*), so no durable
# cross-run history existed at all.
#
# MEASURED CONSEQUENCE: the B12 spec header claimed "Fly B11 FIRST ... it prices
# the capture tail in ~20 wall-minutes instead of ~30", while every archived run
# says the opposite -- B11 PASS wall 1315 / 1319 / 1317 / 1317 s, B12 PASS wall
# 627 / 627 / 627 / 626 s. Backwards across four runs each, unnoticed, because
# nothing ever compared two runs of the same scenario.
#
# duration.json IS the history and is COMMITTED (it is a few hundred bytes and
# regenerating it requires re-flying the suite).
#
# THE LEDGER IS SAMPLE-BASED, NOT SUMMARY-BASED (2026-07-26 review, MAJOR-2).
# The first cut recomputed the whole record from ``results/*.json`` and wrote it
# over the committed file. ``results/`` is GITIGNORED and per-checkout, so a
# fresh worktree that flew ONE scenario replaced the 24-scenario record with one
# entry (observed live 2026-07-25). Merging only the MISSING scenarios forward
# fixes the wipe but not the measured one: under the project's
# one-worktree-per-branch workflow, the scenario the run DID measure had its
# committed ``{"n": 5, "p50": 1317}`` replaced by a per-checkout ``{"n": 1,
# "p50": 1400}`` -- and ``n < DURATION_MIN_SAMPLES`` DISARMS the regression warn.
# Only 3 of the 24 committed entries carry ``n >= 3``, so that is one worktree
# run away from disarming the whole warn.
#
# So the ledger stores the SAMPLES (a bounded per-scenario tail, keyed by
# ``endedUtc``) and recomputes ``n / p50 / p95 / last`` over the union of the
# committed samples and this run's new PASS values. ``n`` is then a real global
# count, not a per-checkout artifact, and merging the same result set twice is
# idempotent because the key already exists. See ``merge_durations`` for the
# watermark rule that keeps an accumulating results dir from re-counting the
# samples that have aged out of the bounded tail.
# ---------------------------------------------------------------------------

# A scenario needs at least this many PASS samples before a slow run is worth
# warning about. With 1 sample last == p50 by construction; with 2 a single
# outlier moves the median enough to make the ratio meaningless.
DURATION_MIN_SAMPLES = 3
# How many PASS samples per scenario the committed ledger keeps. 10 bounds the
# file (one JSON line per sample, so ~240 lines for the whole suite) while
# leaving p50/p95 computed over a window several runs deep. Older samples fall
# off the FRONT; ``n`` keeps counting them, so a scenario that has been green 40
# times reports n=40 with a 10-sample percentile window.
DURATION_SAMPLE_TAIL = 10
# last > factor * p50 warns. 1.5x is well outside the measured run-to-run
# spread: B12's four passes span 626-627 s (0.2%) and B11's four span
# 1315-1319 s (0.3%), so anything approaching 1.5x is a real change, not noise.
DURATION_WARN_FACTOR = 1.5


def _percentile(values: Sequence[float], pct: float) -> float:
    """Nearest-rank percentile over a NON-EMPTY sorted-able sequence. Chosen
    over interpolation because it always returns a value that was actually
    measured, and it is exact for n == 1. Pure."""
    ordered = sorted(float(v) for v in values)
    if not ordered:
        raise ValueError("percentile of an empty sequence")
    rank = int(math.ceil((pct / 100.0) * len(ordered)))
    rank = min(max(rank, 1), len(ordered))
    return ordered[rank - 1]


def duration_samples(results: Sequence[Dict]) -> Dict[str, Dict[str, float]]:
    """The PASS wall-duration SAMPLES one result set contributes, as
    ``{scenarioId: {endedUtc: wallSeconds}}``.

    PASS results ONLY: an INVALID that died on a budget or a KILLED that was
    reaped at the wall bound measures the BOUND, not the scenario, and folding
    those in would drag the median toward the timeout.

    The sample KEY is ``endedUtc`` (design edge 26: UTC ISO-8601 string compare
    is immune to tz/DST, and sorts chronologically), which makes the merge
    idempotent: re-reading a results directory that already contributed a sample
    cannot count it twice. Results carrying the same ``runId`` collapse to the
    newest one FIRST -- ``results/<runId>.json`` is one file per run, so two rows
    with one runId can only be a caller passing the same run twice.

    Pure; ``run.py`` owns the I/O.
    """
    by_run: Dict[Tuple[str, str], Tuple[str, float]] = {}
    loose: Dict[str, List[Tuple[str, float]]] = {}
    for result in results or []:
        if (result or {}).get("verdict") != VERDICT_PASS:
            continue
        sid = result.get("scenarioId")
        wall = result.get("wallSeconds")
        if not sid or not isinstance(wall, (int, float)) or isinstance(wall, bool):
            continue
        if not math.isfinite(float(wall)):
            continue
        utc = _result_utc(result)
        run_id = result.get("runId")
        if run_id:
            key = (str(sid), str(run_id))
            prev = by_run.get(key)
            if prev is None or utc >= prev[0]:
                by_run[key] = (utc, float(wall))
        else:
            loose.setdefault(str(sid), []).append((utc, float(wall)))

    out: Dict[str, Dict[str, float]] = {}
    for (sid, _run_id), (utc, wall) in by_run.items():
        out.setdefault(sid, {})[utc] = wall
    for sid, rows in loose.items():
        for utc, wall in rows:
            out.setdefault(sid, {})[utc] = wall
    return out


def _summarize_samples(samples: Dict[str, float], n: int) -> Dict:
    """One ledger entry from a NON-EMPTY sample map plus the global count ``n``.
    ``last`` is the newest sample by UTC key; ``lastVsP50`` is None when p50 is 0
    (a degenerate all-zero record) so the caller never divides by zero. Pure."""
    ordered = [samples[k] for k in sorted(samples)]
    p50 = _percentile(ordered, 50.0)
    p95 = _percentile(ordered, 95.0)
    last = ordered[-1]
    return {
        "n": int(n),
        "p50": round(p50, 3),
        "p95": round(p95, 3),
        "last": round(last, 3),
        "lastVsP50": (round(last / p50, 3) if p50 > 0.0 else None),
        "samples": {k: round(float(v), 3) for k, v in samples.items()},
    }


def _entry_samples(entry: Dict) -> Dict[str, float]:
    """The usable ``samples`` map out of a persisted ledger entry (hand-edited
    or older-shape entries yield {}). Pure."""
    raw = (entry or {}).get("samples")
    if not isinstance(raw, dict):
        return {}
    out: Dict[str, float] = {}
    for key, value in raw.items():
        if not isinstance(key, str) or not key:
            continue
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            continue
        if not math.isfinite(float(value)):
            continue
        out[key] = float(value)
    return out


def _entry_is_wellformed(entry: Dict) -> bool:
    """True when a persisted ledger entry carries the FULL numeric shape that
    the warn and the warn's log line read (``n``, ``p50``, ``p95``, ``last``,
    and a numeric-or-null ``lastVsP50``). A hand-edited partial entry -- the
    file is committed and therefore editable -- is dropped by the merge rather
    than carried forward to KeyError at the end of a whole flown suite. Pure."""
    if not isinstance(entry, dict):
        return False
    n = entry.get("n")
    if isinstance(n, bool) or not isinstance(n, int) or n < 0:
        return False
    for key in ("p50", "p95", "last"):
        value = entry.get(key)
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return False
        if not math.isfinite(float(value)):
            return False
    ratio = entry.get("lastVsP50")
    if ratio is not None and (isinstance(ratio, bool)
                              or not isinstance(ratio, (int, float))
                              or not math.isfinite(float(ratio))):
        return False
    return True


def merge_durations(prior: Dict[str, Dict], fresh_samples: Dict[str, Dict[str, float]],
                    tail: int = DURATION_SAMPLE_TAIL) -> Dict[str, Dict]:
    """Merge this run's PASS samples into the COMMITTED ledger and recompute.

    ``prior`` is the committed ``scenarios`` block; ``fresh_samples`` is
    ``duration_samples(results)`` over THIS checkout's ``results/`` directory.
    Three properties, each of which a live failure or a review finding paid for:

    1. A scenario this run did not measure keeps its committed entry verbatim.
       Without this a fresh worktree flying one scenario overwrote the whole
       24-scenario record with 1 entry (observed 2026-07-25).
    2. A scenario this run DID measure keeps its committed SAMPLES too -- the
       new value is appended, it does not replace them. Without this the same
       fresh worktree turned ``{"n": 5, "p50": 1317}`` into ``{"n": 1, "p50":
       1400}``, and ``n=1 < DURATION_MIN_SAMPLES`` silently DISARMS the
       regression warn for that scenario.
    3. ``n`` counts every sample ever contributed, including ones that have
       aged out of the bounded tail, so the warn's arming gate reflects real
       history rather than the window size.

    THE LEDGER ONLY EVER ADVANCES. A sample is NEW only when its ``endedUtc``
    key is strictly newer than every key the committed tail holds. Anything at
    or below that watermark is assumed already counted and is ignored --
    otherwise a long-lived worktree double-counts on every run: its ``results/``
    dir accumulates, so once the tail truncates, the samples that aged OUT of
    the tail are still in the results dir, absent from the tail, and would be
    re-added as if new (25 real samples -> n=41 after one more run). The cost is
    that a result carrying an out-of-order older ``endedUtc`` is skipped; the
    error direction is an UNDER-count, which can only make the warn more
    conservative, never falsely loud.

    BOOTSTRAP (a summary-only committed entry, no ``samples`` key): there is no
    watermark, so this run's samples may or may not be the very ones the
    committed ``n`` already counted -- a long-lived worktree's results dir holds
    exactly them. ``n`` is therefore ``max(prior_n, len(samples))``, not a sum:
    a fresh worktree keeps the committed ``n`` (so the warn stays armed, which
    is the whole point) and a long-lived one does not double it. One
    transitional run may under-count by the samples it measured; from the next
    merge on the incremental rule takes over.

    STATE OF THE COMMITTED LEDGER (2026-07-26 review round 2). The first cut of
    this file shipped SUMMARY-ONLY: all 24 entries lacked ``samples``, so every
    scenario would have taken the BOOTSTRAP branch on the next run and neither
    the watermark rule nor the bounded tail was exercised by the artifact the
    repo actually carries. That is now fixed at the source: ``duration.json``
    was regenerated through this exact function over the archived
    ``results/*.json``, so all 24 entries carry their samples and every one of
    them takes the INCREMENTAL branch from the next merge on. BOOTSTRAP now
    only covers a genuinely new scenario or a hand-stripped entry.

    Entries with no samples on either side are carried forward only when they
    carry the full numeric shape; a hand-edited partial entry is DROPPED (it
    would otherwise reach the warn's format string and KeyError at the end of a
    flown suite). Pure; ``run.py`` owns the I/O.
    """
    prior = prior or {}
    fresh_samples = fresh_samples or {}
    out: Dict[str, Dict] = {}
    for sid in sorted(set(prior) | set(fresh_samples)):
        entry = prior.get(sid) or {}
        if not isinstance(entry, dict):
            entry = {}
        kept = _entry_samples(entry)
        watermark = max(kept) if kept else None
        incoming = fresh_samples.get(sid) or {}
        added = {k: v for k, v in incoming.items()
                 if k not in kept and (watermark is None or k > watermark)}
        # Prior samples WIN on a key collision: the committed value is the one
        # every other checkout already agrees on.
        merged = dict(kept)
        merged.update(added)
        if not merged:
            if _entry_is_wellformed(entry):
                out[sid] = dict(entry)
            continue
        prior_n = entry.get("n")
        prior_n = int(prior_n) if (isinstance(prior_n, int)
                                   and not isinstance(prior_n, bool)
                                   and prior_n >= 0) else 0
        if watermark is None:
            n = max(prior_n, len(merged))
        else:
            n = max(prior_n, len(kept)) + len(added)
        window = merged
        if tail and len(merged) > tail:
            window = {k: merged[k] for k in sorted(merged)[-int(tail):]}
        out[sid] = _summarize_samples(window, n)
    return out


def compute_durations(results: Sequence[Dict]) -> Dict[str, Dict]:
    """The ledger a result set produces ON ITS OWN, i.e. with NO committed prior:
    ``merge_durations({}, duration_samples(results))``.

    Kept as the one-shot form for tests and for the very first write of a fresh
    ledger. The live path in ``run.py`` NEVER uses it -- it must merge into the
    committed file, because ``results/`` is gitignored and per-checkout. Pure.
    """
    return merge_durations({}, duration_samples(results))


def duration_regressions(durations: Dict[str, Dict],
                         warn_factor: float = DURATION_WARN_FACTOR,
                         min_samples: int = DURATION_MIN_SAMPLES) -> List[str]:
    """The scenario ids whose LAST PASS ran more than ``warn_factor`` times the
    median, once at least ``min_samples`` PASS samples exist. Sorted, pure.

    The min-samples gate is what keeps this from crying on a scenario's second
    ever green run, where the median is one sample away from the value it is
    being compared against.

    An entry that does not carry the FULL numeric shape is never flagged: the
    caller formats ``last`` / ``p50`` / ``p95`` / ``n`` into the warn line, and
    the ledger is a committed, hand-editable file (2026-07-26 review, MINOR-8).
    """
    hits: List[str] = []
    for sid, entry in sorted((durations or {}).items()):
        if not _entry_is_wellformed(entry):
            continue
        if int(entry.get("n", 0)) < min_samples:
            continue
        ratio = entry.get("lastVsP50")
        if isinstance(ratio, (int, float)) and not isinstance(ratio, bool) \
                and math.isfinite(float(ratio)) and float(ratio) > warn_factor:
            hits.append(sid)
    return hits


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
