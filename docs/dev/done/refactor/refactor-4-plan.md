# Refactor-4 Plan

Status: archived after the Refactor-4 zero-logic extraction pass. Remaining
refactor opportunities are tracked in
`docs/dev/plans/refactor-remaining-opportunities.md`.

## Overview

Structural refactoring pass for the code added after refactor-3 and the later
0.8.x/0.9.x work. The codebase has grown from the refactor-3 baseline of
68,282 production lines to 145,229 production lines, excluding
`Source/Parsek/InGameTests`, `bin`, and `obj`.

**Hard requirement:** no behavior changes. Allowed work is structural
reshuffling, logging coverage, test coverage for extracted decisions, and
mechanical constant centralization. Any latent bug found during refactor gets
called out explicitly and fixed in its own logical commit.

Scope note: the opportunity sweep is not limited to files that are new or grew
the most since refactor-3. Older large files such as `GhostVisualBuilder.cs`,
`FlightRecorder.cs`, and `ParsekKSC.cs` are included because current size and
method shape matter even when the file predates the previous pass.

## Setup Phase

1. `main` pulled from `origin/main`; it was already up to date.
2. Worktree created: `Parsek-refactor-4`, branch `refactor-4`, base
   `3c863ff0`.
3. Baseline build completed:
   `dotnet build Source/Parsek/Parsek.csproj`.
4. Baseline tests:
   - Full `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` ran 8,628
     tests, with 1 environment failure in `InjectAllRecordings` because
     `KSP.log` was locked.
   - Working baseline while KSP is open:
     `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`
     passed 8,625 tests.
5. Inventory created: `docs/dev/plans/refactor-4-inventory.md`.
6. Deferred tracker created: `docs/dev/plans/refactor-4-deferred.md`.

## Scope

Primary scope is production source under `Source/Parsek`, excluding generated
build output and excluding `Source/Parsek/InGameTests` for production line-count
baselines. In-game tests may still need updates when extraction changes helper
names or test seams.

The main target is all large production code with meaningful refactor surface,
with special attention to both old large files and code that has grown
substantially since refactor-3:

- large files with thousands of new lines
- old large files that still contain multi-phase methods or duplicated local
  patterns
- extracted UI/controller files that have since become large modules
- storage/finalization/rewind/tracking-station code added after prior passes
- magic values added after `ParsekConfig` centralization

Out of scope unless explicitly promoted by Pass 2:

- schema redesigns
- binary sidecar format changes
- gameplay semantics
- optimizer behavior changes
- broad UI behavior changes

## Subagent Rules

If agents are used for implementation later, give each one:

- one file or disjoint ownership set
- this plan path
- `docs/dev/plans/refactor-4-inventory.md`
- `docs/dev/done/refactor/refactor-review-checklist.md`
- the exact build/test command expected for the tier

Every agent must read the assigned file before editing. The orchestrator reviews
every diff before accepting it.

## Extraction Rules

### Allowed

- Extracting a contiguous block into a well-named private method in the same
  file during Pass 1.
- Passing locals into the extracted method when needed.
- Returning a bool or small result type only when required to preserve existing
  `return`, `continue`, or branch behavior.
- Adding observational logging with existing `ParsekLog` patterns.
- Adding unit tests for pure/static extracted decisions.
- Centralizing literals into an existing constants/config host in a dedicated
  magic-values pass.

### Not Allowed

- Changing behavior while doing structural extraction.
- Changing access modifiers on pre-existing methods.
- Reordering calls.
- Splitting one loop into multiple passes.
- Combining similar code unless Pass 2 proves the original blocks are
  semantically identical.
- Moving methods across files during Pass 1.
- Refactoring `IEnumerator` coroutine bodies beyond logging or tiny
  non-control-flow helpers.
- Introducing broad parameter bags to force an extraction.

### Conflict Resolution Principle

If an extraction needs flags, mode enums, many nullable parameters, or a new
abstraction that is harder to read than the original code, skip it and record
the reason in the deferred doc.

## Pass 0 - Inventory Completion

Goal: finish the map before moving code.

Deliverables:

1. File-level reads for Tier 1 and Tier 2 files.
2. Long-method list with candidate extraction names and risk notes.
3. Static mutable state inventory.
4. Cross-file duplication candidates with enough detail to confirm or reject.
5. Magic values audit, including thresholds, format tags, user-visible strings,
   rate-limit keys, file extensions, and save-node keys.
6. Test coverage gaps for extracted decisions.
7. Update inventory statuses from `Pending detailed read` to either
   `Pass0-Done`, `Skip`, or `Pass1-Candidate`.

The large-file opportunity map is part of Pass 0. It must cover the current
largest files regardless of whether they were newly added, heavily changed, or
already large during refactor-3. The map should record safe same-file
extractions separately from architecture proposals that require discussion.

## Pass 1 - Same-File Extraction, Logging, Tests

Goal: reduce long multi-phase methods without changing file ownership or public
contracts.

Per-file workflow:

1. Read the full file or a coherent region.
2. Identify methods with multiple logical phases.
3. Extract contiguous same-file helpers.
4. Add/verify logs for non-obvious guard branches and state transitions.
5. Add focused tests only where extracted decisions become practical to test.
6. Run the tier validation command.
7. Update `refactor-4-inventory.md`.
8. Commit the file or batch.

### Canary

Pick one Tier 2 file after Pass 0 confirms it has clear phase boundaries and
low external coupling. Candidate files: `Diagnostics/DiagnosticsComputation.cs`,
`BallisticExtrapolator.cs`, or `Timeline/TimelineBuilder.cs`.

The canary validates extraction style, review checklist usage, and baseline
test commands before touching central files.

Selected canary: `Timeline/TimelineBuilder.cs`. The first code commit extracts
recording-start, separation, vessel-spawn, and crew-death row emission from
`CollectRecordingEntries` into private same-file helpers. This is intentionally
limited to contiguous block extraction with no cross-file movement.

Current next candidates after the canary and large-file map are selected by
risk, not file size alone. The first follow-up is
`UI/CareerStateWindowUI.Build`, extracting only private same-file tab
view-model helpers. The second follow-up is
`GhostPlaybackLogic.PopulateGhostInfoDictionaries`, extracting only private
same-file dictionary and orphan auto-start helpers. After that, lower-risk
candidates include same-file phase helpers in
`FlightRecorder.LogVisualRecordingCoverage` or `GhostPlaybackLogic.ApplyPartEvents`.
`FlightRecorder.LogVisualRecordingCoverage` has also been completed with
private same-file coverage accumulation and logging helpers.
`GhostPlaybackLogic.ApplyPartEvents` has also been completed for destructive
part events, parachute cleanup, and inventory visibility updates. Central files
such as `ParsekFlight.EvaluatePostSwitchAutoRecordTrigger` remain candidates;
the post-switch trigger extraction has now been completed with focused
validation. Remaining central candidates still need their own focused validation
and review before editing.

### Tier 1 - Sequential

These files are too large or central for parallel edits:

1. `ParsekFlight.cs`
2. `LedgerOrchestrator.cs`
3. `RecordingStore.cs`
4. `FlightRecorder.cs`
5. `GhostPlaybackLogic.cs`
6. `UI/RecordingsTableUI.cs`
7. `BackgroundRecorder.cs`
8. `GhostPlaybackEngine.cs`
9. `ParsekScenario.cs`
10. `VesselSpawner.cs`

Each file gets its own validation and commit.

### Tier 2 - Disjoint File Batches

After Pass 0, group by ownership so no two agents edit the same file:

- Tracking/map/watch: `GhostMapPresence.cs`, `WatchModeController.cs`,
  `ParsekTrackingStation.cs`, Tracking Station patches.
- Career/actions: `GameStateRecorder.cs`, `GameActions/KspStatePatcher.cs`,
  `KerbalsModule.cs`, `CrewReservationManager.cs`.
- Rewind/finalization: `RewindInvoker.cs`, `EffectiveState.cs`,
  `IncompleteBallisticSceneExitFinalizer.cs`,
  `RecordingFinalizationCacheProducer.cs`.
- UI surfaces: `UI/CareerStateWindowUI.cs`, `UI/TimelineWindowUI.cs`,
  `UI/KerbalsWindowUI.cs`.
- Storage/trajectory: `TrajectorySidecarBinary.cs`, `RecordingOptimizer.cs`,
  `RecordingTree.cs`, `TrajectoryMath.cs`.

## Pass 2 - Architecture Analysis (Read-Only)

Goal: decide which cross-file changes are worth doing. No code changes.

Deliverables:

1. Dependency graph for large modules.
2. Static mutable state map and reset/test isolation notes.
3. Cross-reference map for public/internal methods in files over 1,000 lines.
4. Nested type and multi-type file inventory.
5. Duplication analysis with explicit include/reject decisions.
6. Magic value centralization plan.
7. Concrete Pass 3 split recommendations with ordering and rollback plan.

Pass 3 starts only after the Pass 2 analysis is reviewed and approved.

## Pass 3 - Structural Splits and Deduplication

Likely candidates to evaluate, not promises:

- `LedgerOrchestrator` decomposition if Pass 2 finds separable lifecycle,
  reconciliation, migration, or UI-reporting responsibilities. The first Pass 3
  LedgerOrchestrator slice is complete: PR #620 proposed the KSC action
  classifier/reconciler boundary and PR #621 extracted
  `KscActionExpectationClassifier` plus `KscActionReconciler` behind stable
  `LedgerOrchestrator` facades with zero production logic changes.
- `UI/RecordingsTableUI` sub-splitting if the field/callback coupling has a
  clean ownership boundary.
- storage helper extraction across `RecordingStore`,
  `TrajectorySidecarBinary`, snapshot codecs, and sidecar cache helpers.
- watch/tracking station split follow-ups if `WatchModeController` and map
  presence dependencies are clean.
- finalization cache producer/applier/shared endpoint helpers.

Every Pass 3 change must be a separate logical commit with a focused review.
Do not fold more work into the completed PR #621 stack; the next Pass 3 split
needs its own proposal/update and PR.

## Magic Values Pass

Run separately from structural moves. For each candidate literal:

- prefer an existing host such as `ParsekConfig`
- skip identity values, index arithmetic, and one-off test fixtures
- centralize repeated thresholds, file extensions, save-node keys, rate-limit
  keys, format tags, user-visible labels, and enum-int mappings
- preserve every value exactly

## Quality Gates

| Gate | When | Criteria |
|------|------|----------|
| `dotnet build Source/Parsek/Parsek.csproj` | After every Tier 1 file or Tier 2 batch | Build succeeds; no new warnings from code changes |
| Filtered xUnit baseline | While KSP is open/locked | `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings` passes the current non-injection gate (9,261 tests at PR #621 closeout) |
| Full xUnit baseline | When KSP is closed or a clean `KSPDIR` is provided | `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` passes, including `InjectAllRecordings` |
| Orchestrator diff review | Every change | No behavior change; logs/tests meaningful |
| Refactor checklist review | Every commit or small group | Use `docs/dev/done/refactor/refactor-review-checklist.md` |
| Inventory update | Every file/batch | Status and findings kept current |

## Documentation Updates

During the pass:

- keep `refactor-4-inventory.md` current
- add rejected ideas to `refactor-4-deferred.md`
- update `CHANGELOG.md` only once the refactor has real code changes worth
  release notes
- update `AGENTS.md` / `.claude/CLAUDE.md` if source ownership or workflows
  change

At completion, move the refactor docs to `docs/dev/done/refactor/`.

## Commit Strategy

| Phase | Granularity |
|-------|-------------|
| Setup docs | one commit |
| Pass 0 inventory updates | one or more docs-only commits |
| Pass 1 canary | one file, one commit |
| Pass 1 Tier 1 | one commit per file |
| Pass 1 Tier 2 | one commit per disjoint batch |
| Pass 2 analysis | docs-only commit |
| Pass 3 | one commit per split/deduplication |
| Magic values | separate commit or small grouped commits by subsystem |
| Final docs | one cleanup commit |

## Rollback Strategy

- Keep commits small enough for targeted `git revert`.
- If tests break and the cause is unclear, revert the last unit and retry with
  a narrower extraction.
- Do not patch forward on a shaky extraction; the branch exists to make discard
  and retry cheap.
