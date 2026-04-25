# Refactor-4 Inventory

**Date:** 2026-04-25.
**Worktree:** `Parsek-refactor-4`, branch `refactor-4`.
**Base:** `3c863ff0` (`main`, after `git pull origin main`, already up to date).

This inventory is the starting map for the next structural refactor pass. It is
not an implementation plan by itself; every extraction target still needs a
file-level read before code moves.

## Baseline

| Item | Result |
|------|--------|
| Production C# files | 176, excluding `Source/Parsek/InGameTests`, `bin`, and `obj` |
| Production C# lines | 145,229, excluding `Source/Parsek/InGameTests`, `bin`, and `obj` |
| Test C# files | 370, excluding `bin` and `obj` |
| Test C# lines | 187,894, excluding `bin` and `obj` |
| xUnit attributes | 7,796 `[Fact]`, 140 `[Theory]`, 7,936 total |
| Build | `dotnet build Source/Parsek/Parsek.csproj` succeeded |
| Full test run | `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` ran 8,628 tests, 8,627 passed, 1 failed because `InjectAllRecordings` refused to purge a KSP save while `KSP.log` was locked |
| Refactor baseline test command while KSP is open | `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings` passed 8,625 tests |

Build warning note: the build emits MSB3026/MSB3027/MSB3021 warnings when the
post-build copy cannot replace the deployed KSP `Parsek.dll`. This matches the
locked KSP process/log condition above; the build itself succeeds.

## Directory Size Summary

| Directory | Files | Lines |
|-----------|-------|-------|
| `<root>` | 123 | 113,100 |
| `GameActions` | 18 | 15,087 |
| `UI` | 14 | 11,599 |
| `Patches` | 13 | 2,606 |
| `Diagnostics` | 4 | 1,553 |
| `Timeline` | 3 | 1,261 |
| `Properties` | 1 | 23 |

## Largest Production Files

| File | Lines | Initial status |
|------|-------|----------------|
| `Source/Parsek/ParsekFlight.cs` | 14,503 | Pass0-Done; same-file candidates in post-switch auto-recording and finalization |
| `Source/Parsek/GhostVisualBuilder.cs` | 7,193 | Pending detailed read |
| `Source/Parsek/GameActions/LedgerOrchestrator.cs` | 6,976 | Pass0-Done; same-file candidates exist, cross-file split deferred |
| `Source/Parsek/RecordingStore.cs` | 6,902 | Pending detailed read |
| `Source/Parsek/FlightRecorder.cs` | 6,689 | Pending detailed read |
| `Source/Parsek/GhostPlaybackLogic.cs` | 5,343 | Pending detailed read |
| `Source/Parsek/UI/RecordingsTableUI.cs` | 4,868 | Pass0-Done; high-coupling UI surface, not a canary |
| `Source/Parsek/BackgroundRecorder.cs` | 4,489 | Pending detailed read |
| `Source/Parsek/GhostPlaybackEngine.cs` | 4,312 | Pending detailed read |
| `Source/Parsek/ParsekScenario.cs` | 4,172 | Pending detailed read |
| `Source/Parsek/VesselSpawner.cs` | 4,166 | Pending detailed read |
| `Source/Parsek/GhostMapPresence.cs` | 3,408 | Pending detailed read |
| `Source/Parsek/WatchModeController.cs` | 3,197 | Pending detailed read |
| `Source/Parsek/GameStateRecorder.cs` | 2,004 | Pending detailed read |
| `Source/Parsek/UI/CareerStateWindowUI.cs` | 1,867 | Pending detailed read |
| `Source/Parsek/GameActions/KspStatePatcher.cs` | 1,759 | Pending detailed read |
| `Source/Parsek/BallisticExtrapolator.cs` | 1,639 | Pending detailed read |
| `Source/Parsek/RecordingOptimizer.cs` | 1,621 | Pending detailed read |
| `Source/Parsek/RecordingTree.cs` | 1,615 | Pending detailed read |
| `Source/Parsek/ParsekKSC.cs` | 1,520 | Pending detailed read |

## Growth Since Refactor-3 Inventory

These deltas compare files that also existed in
`docs/dev/done/refactor/refactor-3-inventory.md`.

| File | Refactor-3 lines | Current lines | Delta |
|------|------------------|---------------|-------|
| `LedgerOrchestrator.cs` | 900 | 6,976 | +6,076 |
| `ParsekFlight.cs` | 8,765 | 14,503 | +5,738 |
| `RecordingStore.cs` | 2,958 | 6,902 | +3,944 |
| `GhostPlaybackLogic.cs` | 2,589 | 5,343 | +2,754 |
| `VesselSpawner.cs` | 1,473 | 4,166 | +2,693 |
| `GhostPlaybackEngine.cs` | 1,770 | 4,312 | +2,542 |
| `GhostMapPresence.cs` | 1,211 | 3,408 | +2,197 |
| `ParsekScenario.cs` | 2,248 | 4,172 | +1,924 |
| `BackgroundRecorder.cs` | 2,788 | 4,489 | +1,701 |
| `FlightRecorder.cs` | 5,267 | 6,689 | +1,422 |
| `GameStateRecorder.cs` | 975 | 2,004 | +1,029 |
| `KspStatePatcher.cs` | 777 | 1,759 | +982 |
| `CrewReservationManager.cs` | 686 | 1,447 | +761 |
| `RecordingOptimizer.cs` | 863 | 1,621 | +758 |
| `GhostVisualBuilder.cs` | 6,484 | 7,193 | +709 |
| `ParsekKSC.cs` | 897 | 1,520 | +623 |
| `RecordingTree.cs` | 1,013 | 1,615 | +602 |
| `VesselGhoster.cs` | 709 | 1,190 | +481 |
| `GameStateStore.cs` | 709 | 1,138 | +429 |
| `TrajectoryMath.cs` | 702 | 1,110 | +408 |
| `KerbalsModule.cs` | 892 | 1,273 | +381 |
| `SpawnCollisionDetector.cs` | 489 | 868 | +379 |
| `EngineFxBuilder.cs` | 988 | 1,367 | +379 |
| `ParsekPlaybackPolicy.cs` | 892 | 1,268 | +376 |

## Large Files Absent From Refactor-3 Inventory

These files were not listed in the refactor-3 inventory and need first-pass
classification before they are assigned to extraction tiers.

| File | Lines | Notes |
|------|-------|-------|
| `Source/Parsek/UI/RecordingsTableUI.cs` | 4,868 | Extracted UI surface that has since become a major file |
| `Source/Parsek/WatchModeController.cs` | 3,197 | Extracted watch-mode controller; now large enough for its own pass |
| `Source/Parsek/UI/CareerStateWindowUI.cs` | 1,867 | New large UI surface |
| `Source/Parsek/BallisticExtrapolator.cs` | 1,639 | New ballistic/extrapolation logic |
| `Source/Parsek/UI/TimelineWindowUI.cs` | 1,255 | New timeline UI surface |
| `Source/Parsek/RewindInvoker.cs` | 1,218 | New rewind-to-staging surface |
| `Source/Parsek/TrajectorySidecarBinary.cs` | 1,124 | Binary storage/serialization surface |
| `Source/Parsek/Diagnostics/DiagnosticsComputation.cs` | 1,054 | Diagnostics computation surface |
| `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` | 934 | Finalization/extrapolation bridge |
| `Source/Parsek/UI/KerbalsWindowUI.cs` | 841 | Kerbals UI surface |
| `Source/Parsek/Patches/GhostTrackingStationPatch.cs` | 817 | Tracking Station patch surface |
| `Source/Parsek/RecordingEndpointResolver.cs` | 816 | Endpoint resolution helper |
| `Source/Parsek/RevertInterceptor.cs` | 794 | Revert interception surface |
| `Source/Parsek/ParsekTrackingStation.cs` | 778 | Tracking Station controller |
| `Source/Parsek/EffectiveState.cs` | 744 | Rewind/effective-state logic |
| `Source/Parsek/Timeline/TimelineBuilder.cs` | 711 | Timeline builder |
| `Source/Parsek/RecordingFinalizationCacheProducer.cs` | 702 | Finalization cache producer |

## Initial Tiering

### Tier 0 - File-Level Inventory Before Extraction

All Tier 1 and Tier 2 files need a targeted read for:

- long methods with multiple logical phases
- duplicated helpers or serialization blocks
- silent guard branches and missing batch summaries
- test coverage gaps for extracted pure decisions
- static mutable state and test isolation requirements
- magic values that should move into `ParsekConfig` or an existing constants host

### Tier 1 - Critical Sequential Files

Process one at a time after a detailed read. These files are large enough or
central enough that parallel edits would make review and rollback worse.

| File | Reason |
|------|--------|
| `ParsekFlight.cs` | 14.5k-line scene controller, +5.7k since refactor-3 |
| `LedgerOrchestrator.cs` | 7.0k-line GameActions hub, +6.1k since refactor-3 |
| `RecordingStore.cs` | 6.9k-line storage surface, +3.9k since refactor-3 |
| `FlightRecorder.cs` | 6.7k-line sampling/event surface |
| `GhostPlaybackLogic.cs` | 5.3k-line playback/visual logic helper |
| `UI/RecordingsTableUI.cs` | 4.9k-line extracted UI surface with prior coupling risk |
| `BackgroundRecorder.cs` | 4.5k-line background sampling surface |
| `GhostPlaybackEngine.cs` | 4.3k-line engine core |
| `ParsekScenario.cs` | 4.2k-line save/load/lifecycle host |
| `VesselSpawner.cs` | 4.2k-line spawn/snapshot utility |

### Tier 2 - Large Focused Files

These may be suitable for small parallel batches only after ownership is
clearly separated by file.

| File | Lines |
|------|-------|
| `GhostMapPresence.cs` | 3,408 |
| `WatchModeController.cs` | 3,197 |
| `GameStateRecorder.cs` | 2,004 |
| `UI/CareerStateWindowUI.cs` | 1,867 |
| `GameActions/KspStatePatcher.cs` | 1,759 |
| `BallisticExtrapolator.cs` | 1,639 |
| `RecordingOptimizer.cs` | 1,621 |
| `RecordingTree.cs` | 1,615 |
| `ParsekKSC.cs` | 1,520 |
| `CrewReservationManager.cs` | 1,447 |
| `EngineFxBuilder.cs` | 1,367 |
| `KerbalsModule.cs` | 1,273 |
| `ParsekPlaybackPolicy.cs` | 1,268 |
| `UI/TimelineWindowUI.cs` | 1,255 |
| `RewindInvoker.cs` | 1,218 |
| `VesselGhoster.cs` | 1,190 |
| `TrajectorySidecarBinary.cs` | 1,124 |
| `Diagnostics/DiagnosticsComputation.cs` | 1,054 |

### Tier 3 - Medium New/Changed Files

Scan for logging gaps, obvious method extraction candidates, and magic values.
Most should remain unchanged unless the detailed read finds real complexity.

Examples from the current snapshot: `IncompleteBallisticSceneExitFinalizer.cs`,
`UI/KerbalsWindowUI.cs`, `Patches/GhostTrackingStationPatch.cs`,
`RecordingEndpointResolver.cs`, `RevertInterceptor.cs`,
`ParsekTrackingStation.cs`, `EffectiveState.cs`, `Timeline/TimelineBuilder.cs`,
`RecordingFinalizationCacheProducer.cs`, `MapMarkerRenderer.cs`,
`SupersedeCommit.cs`, `MergeJournalOrchestrator.cs`, `LoadTimeSweep.cs`,
`Patches/GhostVesselLoadPatch.cs`, `RewindPointAuthor.cs`, and
`GameStateEvent.cs`.

## Mechanical Long-Method Scan

Initial regex/brace scan for methods at least 120 lines long in the largest
files. This is only a candidate list; each item still needs a real read before
editing because signatures, nested scopes, comments, and coherent single-purpose
loops can fool a mechanical scan.

| File | Method | Start line | Lines |
|------|--------|------------|-------|
| `BackgroundRecorder.cs` | `HandleBackgroundVesselSplit` | 451 | 179 |
| `BackgroundRecorder.cs` | `InitializeLoadedState` | 2246 | 147 |
| `FlightRecorder.cs` | `LogVisualRecordingCoverage` | 2495 | 204 |
| `LedgerOrchestrator.cs` | `ReconcileEarningsWindow` | 338 | 221 |
| `LedgerOrchestrator.cs` | `CreateVesselCostActions` | 804 | 148 |
| `LedgerOrchestrator.cs` | `RecalculateAndPatchCore` | 1421 | 146 |
| `LedgerOrchestrator.cs` | `MigrateLegacyTreeResources` | 1881 | 199 |
| `LedgerOrchestrator.cs` | `ClassifyAction` | 4276 | 201 |
| `LedgerOrchestrator.cs` | `ClassifyPostWalk` | 4844 | 182 |
| `LedgerOrchestrator.cs` | `ReconcilePostWalk` | 5045 | 120 |
| `LedgerOrchestrator.cs` | `AggregatePostWalkWindow` | 5798 | 131 |
| `LedgerOrchestrator.cs` | `NotifyLedgerTreeCommitted` | 6482 | 122 |
| `GhostMapPresence.cs` | `ResolveMapPresenceGhostSource` | 1135 | 223 |
| `GhostMapPresence.cs` | `CreateGhostVesselsFromCommittedRecordings` | 2422 | 126 |
| `GhostMapPresence.cs` | `BuildAndLoadGhostProtoVesselCore` | 3254 | 132 |
| `GhostPlaybackEngine.cs` | `UpdatePlayback` | 335 | 410 |
| `GhostPlaybackEngine.cs` | `RenderInRangeGhost` | 750 | 154 |
| `GhostPlaybackEngine.cs` | `UpdateLoopingPlayback` | 1017 | 224 |
| `GhostPlaybackEngine.cs` | `UpdateOverlapPlayback` | 1246 | 171 |
| `GhostPlaybackEngine.cs` | `UpdateReentryFx` | 1991 | 120 |
| `GhostPlaybackEngine.cs` | `ReusePrimaryGhostAcrossCycle` | 2569 | 136 |
| `GhostPlaybackEngine.cs` | `TryPopulateGhostVisuals` | 2994 | 200 |
| `GhostPlaybackLogic.cs` | `PopulateGhostInfoDictionaries` | 992 | 148 |
| `GhostPlaybackLogic.cs` | `ReapplySpawnTimeModuleBaselinesForLoopCycle` | 1441 | 123 |
| `GhostPlaybackLogic.cs` | `ApplyPartEvents` | 1712 | 232 |
| `GhostPlaybackLogic.cs` | `TryGetPendingWatchActivationUT` | 5095 | 132 |
| `ParsekFlight.cs` | `GetActiveRecordingIdForTagging` | 150 | 120 |
| `ParsekFlight.cs` | `CapturePostSwitchPartStateTokens` | 5126 | 135 |
| `ParsekFlight.cs` | `EvaluatePostSwitchAutoRecordTrigger` | 5262 | 155 |
| `ParsekFlight.cs` | `StartRecording` | 7208 | 130 |
| `ParsekFlight.cs` | `FinalizeIndividualRecording` | 9064 | 259 |
| `ParsekFlight.cs` | `CollectNearbySpawnCandidates` | 14162 | 121 |
| `ParsekScenario.cs` | `LoadRecordingMetadata` | 3660 | 244 |
| `RecordingStore.cs` | `RunOptimizationPass` | 1912 | 216 |
| `RecordingStore.cs` | `InitiateRewind` | 3464 | 141 |
| `RecordingStore.cs` | `ReconcileReadableSidecarMirrors` | 6413 | 128 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingsTableHeader` | 835 | 136 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingsWindow` | 1045 | 232 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingRow` | 1281 | 382 |
| `UI/RecordingsTableUI.cs` | `DrawGroupTree` | 1740 | 493 |
| `UI/RecordingsTableUI.cs` | `DrawVirtualUnfinishedFlightsGroup` | 2299 | 209 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingBlock` | 2895 | 137 |
| `UI/RecordingsTableUI.cs` | `DrawLoopPeriodCell` | 4430 | 129 |
| `VesselSpawner.cs` | `SpawnOrRecoverIfTooClose` | 1075 | 308 |
| `VesselSpawner.cs` | `TryRepairSnapshotBodyProvenance` | 3175 | 144 |
| `WatchModeController.cs` | `EnterWatchMode` | 1349 | 224 |

## Pass 0 Detailed Read Notes

### `Source/Parsek/GameActions/LedgerOrchestrator.cs`

The file is no longer just the compact 900-line hub recorded during
refactor-3. It now contains several distinct bands:

- module initialization, resource tracker availability, and seed state
- commit-window earnings reconciliation
- vessel-cost actions, rollout adoption, and recovery pairing
- recalculation, KSP patching, committed-science rebuild, and timeline
  invalidation
- load repair and legacy tree-resource migration
- KSC action expectation reconciliation
- post-walk reconciliation and aggregation
- facility slot and pending-science notification helpers

Pass 1 should stay same-file only. The best candidates are phase extractions
inside `ReconcileEarningsWindow`, `CreateVesselCostActions`, and
`RecalculateAndPatchCore`. These are long because they perform ordered
orchestration, so extractions must keep call order unchanged and avoid broad
parameter bags.

Cross-file decomposition should wait for Pass 2. Likely owners to evaluate are
ledger migration/load repair, KSC expectation reconciliation, post-walk
reconciliation, recovery-funds pairing, and rollout adoption. These regions are
large enough to justify a split, but they share tolerances, ledger/action
classification details, and commit-window state.

Magic-value follow-up candidates include the resource tolerances used by
earnings/post-walk reconciliation, `KscReconcileEpsilonSeconds`,
`PostWalkReconcileEpsilonSeconds`, `PostWalkAggregateContributionEpsilon`, and
nearby coalescing thresholds. Treat this as a later constants pass, not part of
the first structural extraction.

### `Source/Parsek/UI/RecordingsTableUI.cs`

This is a stateful IMGUI surface with many shared fields: window and scroll
state, resize and input-lock state, column widths, static per-frame buffers,
expansion state, unfinished-flight cache, rename and double-click state, group
picker state, sort state, tooltips, expanded stats, loop editing, watch/R/FF
tracking, deletion confirmation, budget cache, and cross-link scrolling.

The main ownership bands are:

- window lifecycle, input, layout, and style setup
- header, bottom controls, and time indicator drawing
- table orchestration in `DrawRecordingsWindow`
- row rendering in `DrawRecordingRow`
- group tree drawing in `DrawGroupTree` and
  `DrawVirtualUnfinishedFlightsGroup`
- command and confirmation handlers
- sort, format, and grouping data helpers
- stats, tooltip, and loop-period helpers

This file should not be the Pass 1 canary. Size alone is not enough to prove a
safe split because prior refactor notes already called out high field coupling
when the table was much smaller. Conservative same-file extractions may be
reasonable inside `DrawRecordingsWindow` and `DrawRecordingRow`, but only where
the extracted block is contiguous and does not reorder IMGUI layout calls.

The existing TODO to migrate rows to recording-id-keyed state is semantic work
and remains deferred. A cross-file split should wait until Pass 2 produces a
field ownership map for row rendering, group data, sorting, and edit state.

### `Source/Parsek/ParsekFlight.cs`

The post-switch auto-recording region already has many extracted pure/static
decisions, including launch decision, arming, suppression, meaningful vessel
activity, and manifest-diff checks. The strongest Pass 1 same-file candidate is
`EvaluatePostSwitchAutoRecordTrigger`, which has clear ordered phases for cache
refresh, engine/RCS transition scans, attitude debounce, manifest diff, landed
and orbit guards, and final trigger selection.

`CapturePostSwitchPartStateTokens` is long but repetitive. It can be revisited
after the auto-record trigger extraction, but extracting every module case may
reduce readability rather than improve it.

The finalization region is partly delegated already through
`IncompleteBallisticSceneExitFinalizer`, `RecordingFinalizationCache`,
`RecordingEndpointResolver`, and terminal-orbit helpers. The best same-file
candidate is `FinalizeIndividualRecording`, split by ordered phases such as
explicit time initialization, live vessel/cache lookup, scene-exit finalizer
resolution, terminal state selection, stable terminal snapshot refresh,
terminal-orbit repair, endpoint finalization, and warning/log emission.

Do not start Pass 1 by moving finalization into a new owner. A future
`RecordingFinalizer` or similar split may be useful, but only after Pass 2 maps
the current finalization cache producer, endpoint resolver, scene-exit
finalizer, and `ParsekFlight` call sites.

## Static State Scan Note

A raw regex scan for mutable static fields is too noisy to use directly:
intentional global stores, test hooks, constants that are not marked readonly,
and real mutable runtime state all appear together. Pass 0 should replace the
raw scan with a manual map for the high-risk owners:

- `RecordingStore`
- `ParsekScenario`
- `GameStateRecorder`
- `LedgerOrchestrator`
- `GhostPlaybackEngine`
- `WatchModeController`
- `GameStateStore`
- `MilestoneStore`
- rewind/effective-state helpers

## Immediate Investigation Priorities

1. Compare `RecordingStore.cs`, `TrajectorySidecarBinary.cs`, and snapshot
   sidecar helpers for repeated binary/text serialization patterns before any
   deduplication.
2. Build a static mutable state map for `GameStateRecorder`,
   `LedgerOrchestrator`, `RecordingStore`, `ParsekScenario`,
   `WatchModeController`, and `GhostPlaybackEngine`.
3. Audit magic thresholds and literal keys introduced after the `ParsekConfig`
   centralization entry in 0.8.3.
