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
| `Source/Parsek/ParsekFlight.cs` | 14,503 | Pass1-Partial; post-switch auto-record trigger helpers extracted, finalization remains |
| `Source/Parsek/GhostVisualBuilder.cs` | 7,193 | Pass0-OpportunityMap; old large visual builder included in sweep |
| `Source/Parsek/GameActions/LedgerOrchestrator.cs` | 6,976 | Pass1-Done; earnings-window, vessel-cost, and recalculation helpers extracted |
| `Source/Parsek/RecordingStore.cs` | 6,902 | Pass1-Done; optimization and rewind helpers extracted |
| `Source/Parsek/FlightRecorder.cs` | 6,689 | Pass1-Done; visual coverage logging helpers extracted |
| `Source/Parsek/GhostPlaybackLogic.cs` | 5,343 | Pass1-Done; ghost info population and part-event helpers extracted |
| `Source/Parsek/UI/RecordingsTableUI.cs` | 4,868 | Pass0-Done; high-coupling UI surface, not a canary |
| `Source/Parsek/BackgroundRecorder.cs` | 4,489 | Pass1-Done; split discovery and loaded-state helpers extracted |
| `Source/Parsek/GhostPlaybackEngine.cs` | 4,312 | Pass0-OpportunityMap; playback engine phases mapped |
| `Source/Parsek/ParsekScenario.cs` | 4,172 | Pass1-Done; recording metadata load helpers extracted |
| `Source/Parsek/VesselSpawner.cs` | 4,166 | Pass1-Done; spawn-state snapshot override helper extracted |
| `Source/Parsek/GhostMapPresence.cs` | 3,408 | Pass1-Done; proto-vessel node helpers extracted |
| `Source/Parsek/WatchModeController.cs` | 3,197 | Pass1-Done; watch entry helpers extracted |
| `Source/Parsek/GameStateRecorder.cs` | 2,004 | Pass0-OpportunityMap; event handler candidates mapped |
| `Source/Parsek/UI/CareerStateWindowUI.cs` | 1,867 | Pass1-Done; `Build` tab view-model helpers extracted |
| `Source/Parsek/GameActions/KspStatePatcher.cs` | 1,759 | Pass0-OpportunityMap; patcher candidates mapped |
| `Source/Parsek/BallisticExtrapolator.cs` | 1,639 | Pass0-OpportunityMap; math-heavy file, cautious only |
| `Source/Parsek/RecordingOptimizer.cs` | 1,621 | Pass0-OpportunityMap; optimizer candidates mapped, high semantic risk |
| `Source/Parsek/RecordingTree.cs` | 1,615 | Pass0-OpportunityMap; serialization candidates mapped |
| `Source/Parsek/ParsekKSC.cs` | 1,520 | Pass0-OpportunityMap; old large KSC playback file included in sweep |

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
| `Source/Parsek/Timeline/TimelineBuilder.cs` | 711 | Timeline builder; Pass1 canary completed |
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

## Pass 1 Canary

`Source/Parsek/Timeline/TimelineBuilder.cs` was selected as the first canary
because it is smaller than the Tier 1 files, has clear ordered phases in the
recording collector, and has a dedicated `TimelineBuilderTests` suite.

The canary extracted only same-file private helpers from `CollectRecordingEntries`:

- `TryAddRecordingStartEntry`
- `TryAddSeparationEntry`
- `TryAddVesselSpawnEntry`
- `AddCrewDeathEntries`

No conditions, counters, sort order, or logging text were intentionally changed.
Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~TimelineBuilderTests`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

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

Pass 1 completed:

- Extracted `ReconcileEarningsWindow` phases into same-file helpers for
  store-side event deltas, emitted action deltas, contribution summary logging,
  and mismatch warning emission. Tolerances, warning keys, science dump
  behavior, scope filtering, and action math remain unchanged.
- Extracted `CreateVesselCostActions` phases into same-file helpers for
  build/rollout cost handling and recovery funds handling. Rollout adoption,
  residual build-cost emission, paired recovery event preference, and legacy
  last-point fallback remain unchanged.
- Extracted `RecalculateAndPatchCore` phases into same-file helpers for
  effective-ledger input construction/logging and KSP state patch application.
  Cutoff counting, post-walk reconciliation order, patch deferral rules,
  committed-science rebuild, timeline invalidation, and rewind tech-tree patch
  behavior remain unchanged.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~EarningsReconciliationTests|FullyQualifiedName~Bug445RolloutCostLeakTests|FullyQualifiedName~FullCareerTimelineTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~Bug445RolloutCostLeakTests|FullyQualifiedName~EarningsReconciliationTests|FullyQualifiedName~TreeCommitTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~LedgerOrchestratorTests|FullyQualifiedName~RewindUtCutoffTests|FullyQualifiedName~RewindTechStickinessTests|FullyQualifiedName~GloopsEventSuppressionTests|FullyQualifiedName~PostWalkReconciliationIntegrationTests|FullyQualifiedName~KspStatePatcherTests|FullyQualifiedName~EffectiveStateTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

No remaining Pass 1 same-file candidates are planned for this file.

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

Pass 1 completed:

- Extracted `EvaluatePostSwitchAutoRecordTrigger` phases into same-file helpers
  for cache refresh, engine activity, RCS activity, attitude debounce, manifest
  diff, landed motion, and orbit change checks. Trigger priority still flows
  through the existing pure `EvaluatePostSwitchAutoRecordTrigger(...)`
  overload.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~PostSwitchAutoRecordTests|FullyQualifiedName~AutoRecordDecisionTests|FullyQualifiedName~VesselSwitchTreeTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

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

## Pass 0 Large-File Opportunity Map

This follow-up sweep intentionally includes old large files, not only files
added or heavily grown since refactor-3. Entries below are not permission to
change behavior. Pass 1 means same-file private helper extraction only;
cross-file moves, deduplication, and new owners remain Pass 2 proposals that
must be discussed before implementation.

### `Source/Parsek/GhostVisualBuilder.cs`

This older 7k-line visual builder contains separate subsystems for FX prefab
lookup, snapshot parsing, model clone maps, animation sampling caches, RCS,
fairings, variants, robotics, heat/reentry, explosions, flags, audio, and part
visual construction.

Pass 1 same-file candidates:

- Extract contiguous preflight and renderer/variant setup phases from
  `AddPartVisuals`.
- Extract the model-node/clone-map creation phase from `AddPartVisuals`.
- Extract the animation capability scan from `AddPartVisuals` without changing
  the checks or order.
- Split `TryBuildRcsFX` into same-file phases for module discovery,
  effect-node extraction, transform resolution, particle setup, and diagnostics.

Pass 2 discussion only: `RcsFxBuilder`, `VariantVisualRules`,
`ReentryFxBuilder`, or a narrower part-visual builder owner.

### `Source/Parsek/RecordingStore.cs`

This file combines recording/tree commit, grouping, optimization, deletion,
rewind, trajectory serialization, manifests, file I/O, and sidecar mirrors.

Pass 1 completed:

- Split `RunOptimizationPass` into same-file helpers for merge, split, and
  boring-tail trim phases while keeping loop-sync, background-map rebuild, and
  dirty-file flush order unchanged.
- Split `InitiateRewind` into same-file helpers for rewind context setup,
  rewind strip-name collection, and temporary save cleanup. Owner resolution,
  temp save copy/preprocess, load-game invocation, adjusted-UT capture, scene
  load, and failure cleanup behavior remain unchanged.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingOptimizerTests|FullyQualifiedName~RecordingStoreTests|FullyQualifiedName~LegacyTreeMigrationTests|FullyQualifiedName~RewindLoggingTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Remaining sidecar mirror reconciliation work is deferred unless a later read
finds a contiguous extraction that preserves file ordering and exception
handling exactly.

Pass 2 discussion only: storage codec/sidecar deduplication, recording tree
I/O ownership, and rewind service ownership.

### `Source/Parsek/FlightRecorder.cs`

This older large file is the foreground sampling and part-event surface. It
owns recorder state, part subscriptions, many event pollers, engine/RCS/robotic
caches, environment/altitude/track sampling, start/stop/finalization, physics
sampling, rails transitions, and visual coverage logging.

Pass 1 completed:

- Extracted `LogVisualRecordingCoverage` into same-file helpers for part
  visual coverage accumulation, cached engine/RCS/robotic coverage
  accumulation, summary logging, and detail logging. Category order, counts,
  and log strings remain unchanged.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~FlightRecorderExtractedTests|FullyQualifiedName~BackgroundPartEventAuditTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Remaining Pass 1 same-file candidates:

- Consider narrow same-file part-category scanning helpers only where the loop
  body is contiguous and does not change subscription/polling order.

Pass 2 discussion only: deduplicating part-event pollers with
`BackgroundRecorder` or moving event families into shared owners.

### `Source/Parsek/GhostPlaybackLogic.cs`

This static helper mixes loop/warp policy, ghost info dictionaries, visibility,
explosions, part events, canopy/engine/audio/RCS/robotic/heat/light playback,
spawn-at-end policy, zone rendering policy, and watch-mode queries.

Pass 1 completed:

- Split `PopulateGhostInfoDictionaries` into private same-file helpers for
  engine, heat, light, RCS, robotic, audio, and orphan-engine auto-start
  population. The original dictionary fill order, heat cold-start loop, audio
  selection-order assignment, orphan event-key detection, and logs remain in
  the same sequence.
- Split `ApplyPartEvents` destructive part events, parachute cleanup, and
  inventory visibility updates into private same-file helpers. The event loop,
  switch order, applied-count accounting, visibility/reentry rebuild flags,
  blinking-light update, and robotic update order remain unchanged.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~OrphanEngineFxAutoStartTests|FullyQualifiedName~Bug450B3LazyReentryTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~GhostPlaybackEngineTests|FullyQualifiedName~PartEventTests|FullyQualifiedName~ExplosionFxTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Remaining Pass 1 same-file candidates:

- Leave `ShouldSpawnAtRecordingEnd` alone unless a focused read finds a
  contiguous guard-clause extraction that improves the current pure decision.

Pass 2 discussion only: part-event applier, audio controller, spawn policy,
watch policy, or flag replay ownership.

### `Source/Parsek/BackgroundRecorder.cs`

This file mirrors several foreground recording concerns for unloaded or
background vessels. It owns background vessel state, GameEvent subscriptions,
split detection, lifecycle transitions, finalization cache support, environment
and track recording, and part-event polling.

Pass 1 completed:

- Extracted background split child-vessel discovery from
  `HandleBackgroundVesselSplit` into a same-file helper. Parent validation,
  cascade-cap checks, branch construction, parent close/continuation,
  child registration, rewind-point authoring, and logging order remain
  unchanged.
- Extracted `InitializeLoadedState` inherited engine/RCS merge and seed-event
  emission into same-file helpers. Module cache setup, part-state seeding,
  environment classification, initial trajectory point handling, track-section
  startup, and loaded-state registration remain unchanged.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~BackgroundRecorderTests|FullyQualifiedName~BackgroundPartEventAuditTests|FullyQualifiedName~BackgroundTrackSectionTests|FullyQualifiedName~SeedEventTests|FullyQualifiedName~ReviewFollowupTests|FullyQualifiedName~RecordingOptimizerTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Pass 2 discussion only: shared foreground/background part-event pollers and
cross-file recorder state owners.

### `Source/Parsek/GhostPlaybackEngine.cs`

This engine owns per-frame playback dispatch, ghost state dictionaries,
overlap/loop schedules, metrics, in-range rendering, loop/overlap updates,
reentry FX, observability, lifecycle, visual population, and interpolation
diagnostics.

Pass 1 same-file candidates:

- Split `UpdatePlayback` into frame context/counters, loop schedule rebuild,
  per-recording dispatch, and observability flush helpers.
- Split `UpdateLoopingPlayback` and `UpdateOverlapPlayback` by contiguous
  schedule/update phases if validation stays focused.
- Split `TryPopulateGhostVisuals` only around metrics and visual-population
  phases that already execute contiguously.

Pass 2 discussion only: spawn loader, loop scheduler, reentry driver, and
observability collector owners.

### `Source/Parsek/ParsekScenario.cs`

This ScenarioModule is the persistence and lifecycle hub for game state,
rewind-to-staging state, active tree restore, external file loading, recording
metadata, deferred coroutines, and vessel lifecycle events.

Pass 1 completed:

- Split `LoadRecordingMetadata` into same-file helpers for identity/loop,
  budget/rewind, grouping/segment, location/terminal, playback flag, and
  resource/inventory/crew/dock metadata. Existing save-node read order,
  legacy loop migration timing, endpoint backfill, and manifest
  deserialization order remain unchanged.
- Avoid coroutine restructuring beyond tiny helper extraction or logging.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~AutoLoopTests|FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~LoopAnchorTests|FullyQualifiedName~ChainSaveLoadTests|FullyQualifiedName~BackwardCompatTests|FullyQualifiedName~Bug422SidecarHydrationRollupTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Pass 2 discussion only: scenario persistence codec, load coordinator, and
rewind staging persistence owner.

### `Source/Parsek/VesselSpawner.cs`

This spawn/snapshot utility owns source-vessel adoption, backup snapshots,
respawn-at-position, collision/walkback/surface altitude checks, crew filtering,
dead-crew handling, snapshot normalization, body/orbit repairs, and terminal
orbit spawn state.

Pass 1 completed:

- Split the resolved spawn-state snapshot override phase out of
  `SpawnOrRecoverIfTooClose` into a same-file helper. EVA endpoint overrides,
  breakup-continuous overrides, surface-terminal overrides, collision walkback
  ordering, dead-crew guard, spawn-at-position dispatch, and validated respawn
  fallback remain unchanged.
- Left `TryRepairSnapshotBodyProvenance` inline for Pass 1. Its malformed
  snapshot, landed-like repair, recorded terminal orbit repair, endpoint state
  vector fallback, and failure logging paths share a dense repair context; a
  wider split should be part of a later snapshot normalizer proposal.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~VesselSpawnerExtractedTests|FullyQualifiedName~SpawnSafetyNetTests|FullyQualifiedName~Bug170Tests|FullyQualifiedName~DuplicateBlockerRecoveryTests|FullyQualifiedName~EndOfRecordingWalkbackTests|FullyQualifiedName~TrajectoryWalkbackTests|FullyQualifiedName~VesselGhosterTests|FullyQualifiedName~ChainTipSpawnTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Pass 2 discussion only: spawn planner, snapshot normalizer, and orbit spawn
helper owners.

### `Source/Parsek/GhostMapPresence.cs`

This Tracking Station/map presence file owns ghost source batch logging, proto
vessel create/update/removal, Tracking Station handoffs, source resolution,
orbit updates, state-vector/terminal-orbit seed data, save stripping, and
spawn-at-Tracking-Station-end behavior.

Pass 1 completed:

- Split `BuildAndLoadGhostProtoVesselCore` around proto-vessel node
  construction and ghost proto-vessel cleanup. Ghost PID pre-registration,
  `ProtoVessel.Load`, `vesselRef` null handling, orbit renderer setup, vessel
  type restore, and diagnostics remain in the original order.
- Left `ResolveMapPresenceGhostSource` and
  `CreateGhostVesselsFromCommittedRecordings` inline for Pass 1. Their guard
  precedence, source counters, skip buckets, cached state-vector indices, and
  rate-limited batch logging are tightly coupled enough that a wider extraction
  should wait for the Pass 2 Tracking Station owner proposal.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~GhostMapPresenceTests|FullyQualifiedName~TrackingStationSpawnTests|FullyQualifiedName~ShowGhostsInTrackingStationTests|FullyQualifiedName~TrackingStationControlSurfaceUITests|FullyQualifiedName~SessionSuppressionWiringTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Pass 2 discussion only: Tracking Station source resolver, map lifecycle owner,
and proto-vessel builder.

### `Source/Parsek/WatchModeController.cs`

This controller owns watch target state, overlap bridge state, camera memories,
overlay drawing, map focus, camera transfer, horizon math, validation/update,
and watch-hold timers.

Pass 1 completed:

- Split `EnterWatchMode` into same-file helpers for entry validation/range
  gating, unattended-flight warning, and hold-state reset. The camera
  capture/switch preservation block remains inline because it owns a dense
  set of preserved fields; target activation, input lock setup, focus logging,
  and watch session state remain in the original order.
- Left `ProcessWatchEndHoldTimer` and `TryResolveOverlapBridgeRetarget` inline
  for Pass 1 because the existing control flow is already guarded by focused
  pure helpers and extra extraction would add plumbing without reducing risk.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~WatchModeControllerTests|FullyQualifiedName~CameraFollowTests|FullyQualifiedName~Issue316WatchProtectionTests|FullyQualifiedName~WatchModeCleanupRegressionTests|FullyQualifiedName~BugFixTests|FullyQualifiedName~SessionSuppressionWiringTests|FullyQualifiedName~DeferredSpawnTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Pass 2 discussion only: camera state service, overlap bridge owner, and lineage
protection owner.

### `Source/Parsek/GameStateRecorder.cs`

This event aggregator owns replay/suppress flags, contract events, tech and
part purchase events, crew events, resource/science subject tracking, progress
milestone enrichment, strategy lifecycle, facility polling, and test hooks.

Pass 1 same-file candidates:

- Extract repeated resource handler phases where the handler shapes match
  exactly.
- Split `EnrichPendingMilestoneRewards` by reward source only if the existing
  precedence and pending-list behavior stay unchanged.
- Split facility polling helpers around contiguous facility families.

Pass 2 discussion only: contract, crew, resource, milestone, and facility
event handler owners.

### `Source/Parsek/GameActions/KspStatePatcher.cs`

This focused but large patcher applies resource, tech, facility, destruction,
per-subject science, milestone, progress, and contract state back into KSP.

Pass 1 same-file candidates:

- Split `PatchContracts` and `PatchProgressNodeTree` by contiguous patch
  phases.
- Extract repeatable record-state helpers only when values, reflection access,
  and UI patching order remain unchanged.

Pass 2 discussion only: separate patchers per state family.

### `Source/Parsek/BallisticExtrapolator.cs`

This math-heavy extrapolation file is large but cohesive. It includes nested
state types, `Extrapolate`, event search, and `TwoBodyOrbit` math.

Pass 1 same-file candidates:

- Only consider `Extrapolate` phase helpers if a focused read can prove the
  extraction preserves iteration order, floating-point operations, and stop
  conditions.

Pass 2 discussion only: moving `TwoBodyOrbit` or solver helpers into separate
math owners.

### `Source/Parsek/RecordingOptimizer.cs`

This optimizer is behavior-sensitive because ordering affects merge/split/trim
decisions and recording identity.

Pass 1 same-file candidates:

- Split `TrimBoringTail`, `SplitAtSection`, and `MergeInto` by local phases
  only with focused optimizer tests.

Pass 2 discussion only: broader optimizer strategy decomposition.

### `Source/Parsek/RecordingTree.cs`

This serialization/tree owner is already partly extracted, but it still has
resource/state save-load density.

Pass 1 same-file candidates:

- Review `SaveRecordingResourceAndState` and
  `LoadRecordingResourceAndState` for local contiguous helper extraction.

Pass 2 discussion only: a recording tree serialization codec.

### `Source/Parsek/ParsekKSC.cs`

This older KSC playback file has overlap with `GhostPlaybackEngine`, especially
loop schedules, overlap state, audio, and interpolation.

Pass 1 same-file candidates:

- Split playback update and `InterpolateAndPositionKsc` phases if they are
  contiguous and validation can stay local.
- Extract local loop schedule helpers only without deduplicating with flight
  playback yet.

Pass 2 discussion only: cross-scene playback abstraction shared with
`GhostPlaybackEngine`.

### `Source/Parsek/UI/CareerStateWindowUI.cs`

This UI surface mixes view-model construction, draw methods, and formatting
helpers. Its `Build` method was a strong candidate because the tab data shapes
are already explicit.

Pass 1 completed:

- Extracted same-file tab view-model builders from `Build` for contracts,
  strategies, facilities, and milestones. The action walk, live/current
  snapshot boundary, row sorting, and divergence calculation remain in the
  original order.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~CareerStateWindowUITests`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Pass 2 discussion only: separate presentation/model builder ownership.

### `Source/Parsek/UI/TimelineWindowUI.cs`

This UI surface owns timeline drawing, filtering, time range controls, rows,
entry actions, and formatting.

Pass 1 same-file candidates:

- Extract contiguous helpers from `DrawTimelineWindow`, `DrawFilterBar`,
  `DrawTimeRangeFilterBar`, or `DrawEntryRow` only where IMGUI call order stays
  exactly the same.

Pass 2 discussion only: timeline filter/action model ownership.

### `Source/Parsek/RewindInvoker.cs`

This static rewind invocation file owns precondition handling, start invoke,
post-load consumption, strip activation marker work, and staging cleanup.

Pass 1 same-file candidates:

- Split `StartInvoke`, `ConsumePostLoad`, and `RunStripActivateMarker` by
  ordered precondition, marker, load, and cleanup phases.

Pass 2 discussion only: rewind invocation service ownership.

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

1. Select the next Pass 1 same-file extraction from the low-to-medium risk
   candidates in the large-file opportunity map. Good next choices are
   `ParsekFlight.EvaluatePostSwitchAutoRecordTrigger` or
   `LedgerOrchestrator.ReconcileEarningsWindow`.
2. Compare `RecordingStore.cs`, `TrajectorySidecarBinary.cs`, and snapshot
   sidecar helpers for repeated binary/text serialization patterns before any
   deduplication.
3. Build a static mutable state map for `GameStateRecorder`,
   `LedgerOrchestrator`, `RecordingStore`, `ParsekScenario`,
   `WatchModeController`, and `GhostPlaybackEngine`.
4. Audit magic thresholds and literal keys introduced after the `ParsekConfig`
   centralization entry in 0.8.3.
