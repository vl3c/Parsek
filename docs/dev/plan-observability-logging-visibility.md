# Plan: Observability Logging Visibility

Branch: `observability-audit`
Worktree: `C:/Users/vlad3/Documents/Code/Parsek/Parsek-observability-audit`
Date: 2026-04-26

## Source Material

- Audit: `docs/dev/observability-audit-2026-04-26.md`
- Workflow: `docs/dev/development-workflow.md`
- Latest log packages sampled:
  - `logs/2026-04-26_0118_refly-postfix-still-broken`
  - `logs/2026-04-25_2334_refly-followup-test`
  - `logs/2026-04-25_2243_stationary-ghost-visibility`

## Problem

Parsek's logs are structurally better than the older 96 MB playtest logs, but
the current signal is still not good enough for debugging game-visible bugs.
The newest package (`2026-04-26_0118_refly-postfix-still-broken`) has
19,087 Parsek lines out of 19,137 total KSP.log lines. That means Parsek is
still effectively the whole log, and missing decision-path logs are made worse
by repeated low-value summaries.

The work should not add raw "log everything" output. It should make every
important runtime decision reconstructable while reducing repeated steady-state
noise.

## Current Log Signal

### Latest package: `2026-04-26_0118_refly-postfix-still-broken`

KSP.log size is about 2.6 MB. Top structured Parsek subsystem/level counts:

| Count | Level | Subsystem |
| ---: | --- | --- |
| 686 | VERBOSE | GhostVisual |
| 524 | VERBOSE | RecordingStore |
| 404 | VERBOSE | Recorder |
| 361 | VERBOSE | KspStatePatcher |
| 356 | VERBOSE | Milestones |
| 348 | VERBOSE | Extrapolator |
| 343 | INFO | Extrapolator |
| 299 | VERBOSE | Flight |
| 243 | VERBOSE | Funds |
| 197 | VERBOSE | LedgerOrchestrator |
| 185 | VERBOSE | KerbalsModule |
| 179 | VERBOSE | ScienceModule |

Top exact repeaters in that package:

| Count | Message shape |
| ---: | --- |
| 80 | `FinalizerCache refresh summary: owner=ActiveRecorder reason=periodic recordingsExamined=1 alreadyClassified=0 newlyClassified=0` |
| 67 | `SnapshotPatchedConicChain: ... patchIndex=1 body=(missing-reference-body); truncated chain after 1 valid patch(es)` |
| 67 | `SnapshotPatchedConicChain: ... captured=1 hasTruncatedTail=True ...` |
| 67 | `TryFinalizeRecording: seeded orbital-frame rotation on 1/1 predicted segments ...` |
| 59 | `FinalizerCache refresh summary: owner=BackgroundLoaded reason=background_periodic recordingsExamined=1 alreadyClassified=0 newlyClassified=1` |
| 45 | repeated all-zero game-action reset/seed/spending summaries |
| 39 | repeated sandbox/no-target `KspStatePatcher` skip lines plus `PatchAll complete` |

Warn/error signal in the newest package is not a storm. Most warning shapes are
singletons or rate-limited pairs. That supports focusing first on repeated
verbose/info output and missing decision-path coverage, not on blanket warning
demotion.

### Previous package: `2026-04-25_2334_refly-followup-test`

This package still shows the recently fixed GhostMap shape:

- 1,613 exact repeats of
  `HasOrbitData(IPlaybackTrajectory): body=Kerbin sma=742959.380465312 result=True`
- 65 exact repeats of the same shape for another SMA.

Because this is not visible in the newest package, treat GhostMap `HasOrbitData`
as a regression guard, not Phase 1 work, unless it reappears in current-branch
logs.

## Goals

1. Reduce low-value repeated output in the newest log packages.
2. Add missing decision logs where gameplay-visible behavior is currently
   unexplained.
3. Keep hot paths safe with `VerboseOnChange`, `VerboseRateLimited`,
   `WarnRateLimited`, and aggregate counters.
4. Add log assertion tests so future refactors cannot silently remove the new
   observability.
5. Make `collect-logs.py` / log validation catch malformed or misleading log
   output early.

## Non-Goals

- Do not turn verbose logging off by default as the primary fix.
- Do not add per-frame per-recording logs without on-change or rate-limit gates.
- Do not change gameplay behavior unless a logging fix exposes an existing
  behavior bug that must be addressed to make the log truthful.
- Do not rewrite `ParsekLog` wholesale; existing primitives are sufficient.

## Phase 0 - Baseline And Guardrails

Purpose: make every later phase measurable.

### Task 0.1 - Add a repeat-signal analysis script

Files:

- `scripts/analyze-log-signal.ps1` or a small C# test utility if preferred.

Behavior:

- Input: path to `KSP.log`.
- Output: total lines, Parsek line count, top subsystem/level counts, top exact
  repeated messages, top WARN/ERROR messages, and rate-limit suppressed counts.
- Keep it read-only and independent of KSP runtime.

Verification:

- Run against the three sampled packages above and confirm the script reports
  the counts used in this plan within expected formatting differences.

### Task 0.2 - Tighten post-hoc log validation

Files:

- `Source/Parsek.Tests/LogValidation/ParsekLogContractChecker.cs`
- `Source/Parsek.Tests/LogValidation/ParsekKspLogParser.cs`
- tests under `Source/Parsek.Tests/LogValidation/`

Behavior:

- Fail on malformed `[Parsek]` lines in the latest session.
- Fail on invalid levels in real parsed log lines.
- Fail on `[Parsek][WARN]` payloads that start with `WARNING:` or `WARN:`.
- Ensure timeout/generic validation failures from `scripts/collect-logs.py`
  still produce a failed `log-validation.txt` artifact.

Verification:

- Unit tests with synthetic log snippets for malformed lines, bad levels,
  redundant warning prefixes, and clean logs.
- Run `pwsh -File scripts/validate-ksp-log.ps1` on a current retained log.

## Phase 1 - Current Spam Hygiene

Purpose: reduce repeated current-branch output before adding new coverage.

### Task 1.1 - Fix KSC steady-state spam risks

Files:

- `Source/Parsek/ParsekKSC.cs`
- related tests in `Source/Parsek.Tests/`

Changes:

- Gate `RebuildAutoLoopLaunchScheduleCache` logging with `VerboseOnChange`
  using a queue fingerprint: count, anchor UT, cadence, ordered recording ids.
- Move playback-disabled past-end spawn-attempt logging behind one-shot dedupe,
  or convert the pre-dedupe log to `VerboseOnChange` keyed by recording id and
  reason.

Verification:

- Unit test that repeated `Update` calls with unchanged auto-loop queue emit one
  rebuild log, then emit again when the fingerprint changes.
- Unit test that playback-disabled past-end spawn attempt logs once per
  recording/reason and does not repeat per frame.

### Task 1.2 - Rate-limit diagnostics storage warnings

Files:

- `Source/Parsek/Diagnostics/DiagnosticsComputation.cs`
- `Source/Parsek/UI/RecordingsTableUI.cs` if call-site context is needed.

Changes:

- Convert repeated missing-sidecar `Warn` in `SafeGetFileSize` to
  `WarnRateLimited` keyed by recording/file type/path, or aggregate missing
  sidecar counts per storage scan.

Verification:

- Log assertion test that repeated scans of the same missing file emit once and
  later include `suppressed=N`.

### Task 1.3 - Quiet finalizer cache periodic summaries

Files:

- `Source/Parsek/RecordingFinalizationCacheProducer.cs`
- `Source/Parsek/FlightRecorder.cs`
- `Source/Parsek/BackgroundRecorder.cs`

Changes:

- Investigate why the newest log repeats active no-delta finalizer summaries
  80 times and background `newlyClassified=1` summaries 59 times.
- Keep first classification and unexpected declines visible.
- Route stable no-delta periodic refreshes through `VerboseRateLimited` with a
  wider cadence or `VerboseOnChange` keyed by owner/recording/terminal digest.
- If `BackgroundLoaded newlyClassified=1` is repeatedly classifying the same
  record, fix the stale identity/cache update first, then adjust logging.

Verification:

- Unit/log assertion test for first classification, stable no-delta refresh, and
  terminal-impacting decline.
- Re-run the log signal script on the latest retained package after an in-game
  run; target is no repeated exact finalizer summary in the top 10 unless the
  recording state actually changes.

### Task 1.4 - Collapse patched-snapshot/extrapolator periodic repeats

Files:

- `Source/Parsek/PatchedConicSnapshot.cs`
- `Source/Parsek/BallisticExtrapolator.cs`
- `Source/Parsek/RecordingFinalizationCacheProducer.cs` if finalizer cadence is
  the root trigger.

Changes:

- The newest package repeats the same partial-tail truncation/captured/seeded
  rotation trio 67 times. Keep first occurrence and reason changes; suppress
  stable repeats by recording id, vessel name, patch index, and failure reason.
- Consider folding "seeded orbital-frame rotation on N/N predicted segments"
  into the finalizer summary when it is a stable periodic refresh detail.

Verification:

- Log assertion tests that repeated NullSolver/missing-reference-body truncation
  emits once per stable tuple and emits again when patch index/body/reason
  changes.

### Task 1.5 - Reduce game-state recalculation boilerplate

Files:

- `Source/Parsek/GameActions/LedgerOrchestrator.cs`
- `Source/Parsek/GameActions/*Module.cs`
- `Source/Parsek/KspStatePatcher.cs`

Changes:

- Repeated all-zero reset/seed/spending summaries and sandbox/no-target patch
  skips currently occupy many top repeat slots.
- Add a per-recalc aggregate summary for boring no-op recalculations.
- Use `VerboseOnChange` for stable sandbox/no-target patch skip state.
- Keep `Info` only when a patch mutates visible KSP state or a baseline changes.

Verification:

- Existing ledger behavior tests remain unchanged.
- Log assertion test for one no-op recalculation summary with per-module counts.

## Phase 2 - Flight Playback Explainability

Purpose: when a ghost is missing, hidden, delayed, or never spawned, the log must
say why without per-frame spam.

### Task 2.1 - Carry explicit ghost skip reasons

Files:

- `Source/Parsek/GhostPlaybackEvents.cs`
- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/GhostPlaybackEngine.cs`

Changes:

- Add a compact skip reason enum/string to `TrajectoryPlaybackFlags`.
- Populate it in `ComputePlaybackFlags` for no renderable data,
  playback-disabled, external-vessel suppression, and any existing structural
  skip.
- Log reason flips via `VerboseOnChange` keyed by recording id.
- Add aggregate counters to the existing engine frame summary.

Verification:

- Unit tests for every skip reason.
- Log assertion tests prove stable repeats suppress and reason changes re-emit.

### Task 2.2 - Add engine skip counters for under-summarized paths

Files:

- `Source/Parsek/GhostPlaybackEngine.cs`
- `Source/Parsek/Diagnostics/DiagnosticsStructs.cs` if diagnostics structs need
  fields.

Changes:

- Count and summarize: `beforeActivation`, `anchorMissing`, `loopSyncFailed`,
  `parentLoopPaused`, `warpHidden`, `visualLoadFailed`, `noRenderableData`,
  `sessionSuppressed`.
- Use `VerboseRateLimited("Engine", "frame-summary", ...)` for aggregate counts.
- Use `VerboseOnChange` only for per-recording reason flips.

Verification:

- Headless tests for frame-summary counter construction where possible.
- Existing in-game playback tests should not show top repeated frame-summary
  spam.

### Task 2.3 - Log fast-forward watch handoff failures

Files:

- `Source/Parsek/ParsekFlight.cs`
- `Source/Parsek/WatchModeController.cs` if helper extraction is useful.

Changes:

- When `pendingWatchAfterFFId` clears and no active ghost is watchable, log:
  recording id, whether committed recording exists, playback enabled, current
  UT, active ghost state, and reason.
- Use `Warn` when the recording exists but no ghost is available; use `Verbose`
  for stale id.

Verification:

- Unit/log assertion test around a pure helper that classifies watch handoff
  result.

### Task 2.4 - Log watch camera infrastructure failures

Files:

- `Source/Parsek/WatchModeController.cs`

Changes:

- Add `WarnRateLimited` when watch mode cannot update because
  `FlightCamera.fetch`, transform, or transform parent is null.
- Include watched recording id/name, cycle, scene, target state, and which
  object was null.

Verification:

- Log assertion test through a seam/helper if direct Unity objects are not
  constructible in xUnit.

## Phase 3 - Save/Load, Sidecars, And Rewind Preconditions

Purpose: persistence failures and disabled rewind buttons must be diagnosable
from one log package.

### Task 3.1 - Add scenario top-level exception context

Files:

- `Source/Parsek/ParsekScenario.cs`

Changes:

- Wrap `OnSave` and `OnLoad` phase bodies with top-level catch blocks that log
  `Error("Scenario", ...)` with phase, scene, save folder, committed/pending
  counts, active tree state, marker state, and exception type/message.
- Emit `RecState("OnSave:exception", ...)` or `RecState("OnLoad:exception", ...)`.
- Rethrow after logging.

Verification:

- Injected exception tests through test seams, asserting both rethrow and log
  context.

### Task 3.2 - Fix duplicate/miscounted OnLoad timing

Files:

- `Source/Parsek/ParsekScenario.cs`

Changes:

- Centralize `WriteLoadTiming` so each `OnLoad` invocation emits one timing line.
- Make recording count final and phase/status explicit.

Verification:

- Unit/log assertion tests for cold-start, early-return, and normal load paths.

### Task 3.3 - Upgrade sidecar/path failure severity and context

Files:

- `Source/Parsek/RecordingStore.cs`
- `Source/Parsek/SnapshotSidecarCodec.cs`
- `Source/Parsek/RecordingPaths.cs`
- `Source/Parsek/SidecarFileCommitBatch.cs`

Changes:

- Save/load sidecar exceptions that return false must log at `Warn` or `Error`,
  not INFO.
- Include sidecar path, file kind, save folder, epoch, probe encoding/version,
  staged-file count, ghost snapshot mode, exception type, and message.
- Add low-noise cleanup summaries for transient artifact deletion failures.

Verification:

- Tests for sidecar save exception, sidecar load exception, stale/unsupported
  probe, directory create failure, and cleanup failure summary.

### Task 3.4 - Log RewindInvoker.CanInvoke reason changes

Files:

- `Source/Parsek/RewindInvoker.cs`
- `Source/Parsek/UI/RecordingsTableUI.cs` if cache coordination is needed.

Changes:

- Add `VerboseOnChange` keyed by rewind point id and reason for precondition
  failures.
- Keep UI draw loops quiet by logging only when reason changes, not every draw.

Verification:

- Log assertion tests for null/corrupt/missing-quicksave/pending-invoke/deep
  parse failure paths and stable repeat suppression.

## Phase 4 - Recorder And Auto-Record Decision Visibility

Purpose: recording did not start, stopped sampling, or moved to background must
always have a reason.

### Task 4.1 - Background recorder state-drift warnings

Files:

- `Source/Parsek/BackgroundRecorder.cs`
- `Source/Parsek/Patches/PhysicsFramePatch.cs`

Changes:

- Add `Info` attach/clear logs for `BackgroundRecorderInstance`.
- Add `WarnRateLimited` for background map/state disagreements where a vessel is
  in `BackgroundMap` but loaded/on-rails state or tree recording is missing.

Verification:

- Tests around extracted decision helper; live KSP smoke can confirm attach/clear
  lines.

### Task 4.2 - Active-to-background missing-vessel warning

Files:

- `Source/Parsek/FlightRecorder.cs`

Changes:

- When transition-to-background cannot resolve the live vessel, emit `Warn` with
  pid, recording id, vessel name, points, orbit segments, track sections, and
  finalizer-cache state.

Verification:

- Unit/log assertion test via `FindVesselByPid` seam if available; otherwise
  introduce a small helper for decision formatting.

### Task 4.3 - Post-switch auto-record no-trigger summaries

Files:

- `Source/Parsek/ParsekFlight.cs`

Changes:

- Add a `VerboseOnChange` summary keyed by watched vessel pid for suppression,
  baseline, settle, and trigger state.
- Add a rate-limited manifest delta summary at the existing manifest-evaluation
  cadence.

Verification:

- Existing post-switch auto-record tests plus log assertions for no-trigger,
  suppression, and accepted trigger paths.

### Task 4.4 - EVA/boarding split skip logs

Files:

- `Source/Parsek/ParsekFlight.cs`

Changes:

- Log `pendingSplitInProgress`, source-vessel mismatch, and target/source null
  paths using `VerboseOnChange` or `VerboseRateLimited` keyed by active
  recording id/source pid.

Verification:

- Log assertion tests for active-recording EVA skip and boarding skip helpers.

## Phase 5 - Game Actions, UI, Map Markers, And Test Runner

Purpose: lower-priority but still user-visible paths get complete summaries.

### Task 5.1 - Game-action skip reason summaries

Files:

- `Source/Parsek/GameStateRecorder.cs`
- `Source/Parsek/GameActions/GameStateEventConverter.cs`
- `Source/Parsek/KerbalsModule.cs`

Changes:

- Log null/unsuccessful event rejects at `Verbose` or `VerboseRateLimited`.
- Add per-event-type skip counters to converter summaries.
- Add kerbal assignment pre-pass/post-walk batch counters.

Verification:

- Unit/log assertion tests for event rejects and converter skip counts.

### Task 5.2 - UI/window and Real Spawn Control visibility

Files:

- `Source/Parsek/ParsekUI.cs`
- `Source/Parsek/UI/SpawnControlUI.cs`

Changes:

- Add missing Career window toggle log.
- Add `VerboseOnChange` for Real Spawn Control auto-close reasons:
  not in flight, flight null, zero candidates.

Verification:

- UI helper tests where available; otherwise log assertion on extracted
  reason-format helpers.

### Task 5.3 - Map marker and Tracking Station skip summaries

Files:

- `Source/Parsek/ParsekUI.cs`
- `Source/Parsek/ParsekTrackingStation.cs`
- `Source/Parsek/Patches/GhostOrbitLinePatch.cs`

Changes:

- Add per-frame/per-refresh counters for map-marker skips:
  camera unavailable, native-icon skip, chain non-tip, position failure,
  missing body.
- Add Tracking Station atmospheric marker skip counts.
- Add Harmony orbit suppression `VerboseRateLimited` / `VerboseOnChange`
  summaries.

Verification:

- Headless tests for pure classification helpers.
- In-game smoke for map/tracking station scenes.

### Task 5.4 - Runtime test runner skip visibility

Files:

- `Source/Parsek/InGameTests/InGameTestRunner.cs`

Changes:

- Emit one `Info` aggregate per batch for scene eligibility skips.
- Keep optional per-test names at `Verbose`.

Verification:

- In-game test runner unit coverage or direct log assertion around skip
  aggregation helper.

## Phase 6 - End-To-End Validation

Purpose: prove the plan improves the support workflow.

### Required automated checks

- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj`
- `pwsh -File scripts/validate-ksp-log.ps1`
- New log signal script against retained packages and current generated package.

### Required retained evidence

Run at least one fresh in-game session after phases 1-5 and collect:

- `python scripts/collect-logs.py observability-followup`
- `log-validation.txt`
- `parsek-test-results.txt`
- log signal summary output

### Acceptance targets

- Parsek remains visible but does not dominate >95% of KSP.log lines in a normal
  short session unless the session is intentionally running diagnostic tests.
- No exact repeated message should appear in the top 10 unless it is explicitly
  rate-limited with `suppressed=N` or it represents a real repeated gameplay
  event.
- A missing/hidden ghost has a reason line or a frame-summary counter.
- A disabled Rewind/Re-Fly button has a reason-change line.
- Save/load and sidecar failures include phase/path/recording context.
- Log validation fails malformed Parsek lines and redundant warning prefixes.

## Phase Ordering Rationale

Phase 0 and Phase 1 come first because adding more logs before reducing current
repeaters will make the support signal worse. Phase 2 follows because ghost
visibility failures are the main player-visible symptom class. Phase 3 covers
persistence and rewind, the highest-risk failure domain. Phase 4 covers
recording start/continuation decisions. Phase 5 fills UI/map/game-action gaps.
Phase 6 validates the whole support workflow against real KSP log packages.

## Parallelization Notes

After Phase 0, these workstreams can run in separate worktrees with low conflict
risk:

- KSC/diagnostics spam hygiene (`ParsekKSC`, diagnostics).
- Playback explainability (`GhostPlaybackEvents`, `ParsekFlight`,
  `GhostPlaybackEngine`).
- Persistence/rewind (`ParsekScenario`, `RecordingStore`, `RewindInvoker`).
- Game actions/UI/map summaries (`GameStateRecorder`, `GameActions`, UI files).

Do not parallelize tasks that edit `ParsekFlight.cs` with each other unless the
write scopes are split carefully.

## Documentation Updates Per Phase

Each implementation PR should update:

- `CHANGELOG.md` under the current version.
- `docs/dev/todo-and-known-bugs.md`, marking the relevant plan item closed or
  adding discoveries from new log packages.
- `docs/dev/observability-audit-2026-04-26.md` only if the audit findings are
  superseded or materially corrected.

## Review Checklist

For every phase, review against these questions:

- Does every new decision path log why it chose that path?
- Is every hot-path log batched, rate-limited, or on-change?
- Are rate-limit keys stable and identity-aware where identity matters?
- Do tests assert both behavior and log output?
- Does the phase reduce or preserve log volume in the newest packages?
- Can a future support pass answer "what happened and why" from `KSP.log`
  alone?
