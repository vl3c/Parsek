# Refactor-4 Inventory

**Date:** 2026-04-25.
**Last updated:** 2026-04-29.
**Worktree:** `Parsek-refactor-4`, branch `refactor-4`.
**Base:** `3c863ff0` (`main`, after `git pull origin main`, already up to date).
**Status:** Archived after the Refactor-4 zero-logic extraction pass. Remaining
opportunities are tracked in
`docs/dev/plans/refactor-remaining-opportunities.md`.

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

Pass key: Pass 0 is file-level read/inventory; Pass 1 is behavior-neutral
same-file helper extraction; Pass 2 is owner proposal/scoping; Pass 3 is a
landed cross-file extraction.

Snapshot policy: Baseline, Directory Size, Largest Production Files, Growth,
and Large Files Absent From Refactor-3 are frozen at the refactor-4 base
`3c863ff0` unless a row explicitly says it was updated by a later closeout.
Detailed-read notes, closure status, and the mechanical method scan track the
current planning state for this pass. Re-measure the frozen tables only when a
new inventory pass starts.

Build warning note: the baseline build emitted MSB3026/MSB3027/MSB3021 warnings
when the post-build copy could not replace the deployed KSP `Parsek.dll`. This
matched the locked KSP process/log condition above; the build itself succeeded.
Re-verify this warning shape with a fresh build before publishing a new live
baseline.

Test-count note: the frozen baseline suite was 8,628 tests. Later validation
for PR #621 cites 9,261 tests after subsequent test additions.

Closeout note after PR #621: global baseline counts above remain the original
Pass 0 snapshot. The Pass 3 KSC split leaves
`LedgerOrchestrator.cs` at 6,596 lines and adds
`KscActionExpectationClassifier.cs` (281 lines) plus
`KscActionReconciler.cs` (309 lines).

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
| `Source/Parsek/ParsekFlight.cs` | 14,503 | Pass1-Done; post-switch auto-record trigger helpers extracted, finalization deferred |
| `Source/Parsek/GhostVisualBuilder.cs` | 7,193 | Pass1-Deferred; visual-builder split needs owner plan and runtime validation |
| `Source/Parsek/FlightRecorder.cs` | 6,689 | Pass1-Done; visual coverage logging helpers extracted |
| `Source/Parsek/GameActions/LedgerOrchestrator.cs` | 6,596 | Pass3-Done for KSC classifier/reconciler extraction; earnings-window, vessel-cost, and recalculation helpers remain Pass1-Done |
| `Source/Parsek/GhostPlaybackLogic.cs` | 5,343 | Pass1-Done; ghost info population and part-event helpers extracted |
| `Source/Parsek/UI/RecordingsTableUI.cs` | 4,868 | Pass3-Done for formatter owner; high-coupling IMGUI row/tree split deferred |
| `Source/Parsek/BackgroundRecorder.cs` | 4,489 | Pass1-Done; split discovery and loaded-state helpers extracted |
| `Source/Parsek/RecordingStore.cs` | 4,350 | Pass2-Done for `SidecarFileCommitBatch`, save/load-path `RecordingSidecarStore`, `TrajectoryTextSidecarCodec`, `RecordingManifestCodec`, and `RecordingGroupStore`; tree codec work deferred |
| `Source/Parsek/GhostPlaybackEngine.cs` | 4,312 | Pass1-Done; per-frame playback reset helper extracted |
| `Source/Parsek/ParsekScenario.cs` | 4,172 | Pass1-Done; recording metadata load helpers extracted |
| `Source/Parsek/VesselSpawner.cs` | 4,166 | Pass1-Done; spawn-state snapshot override helper extracted |
| `Source/Parsek/GhostMapPresence.cs` | 3,408 | Pass1-Done; proto-vessel node helpers extracted |
| `Source/Parsek/WatchModeController.cs` | 3,197 | Pass1-Done; watch entry helpers extracted |
| `Source/Parsek/GameStateRecorder.cs` | 2,004 | Pass3-Done for facility polling owner; remaining event-handler families need owner map |
| `Source/Parsek/UI/CareerStateWindowUI.cs` | 1,867 | Pass1-Done; `Build` tab view-model helpers extracted |
| `Source/Parsek/GameActions/KspStatePatcher.cs` | 1,759 | Pass3-Done for facility patcher; remaining state families deferred |
| `Source/Parsek/BallisticExtrapolator.cs` | 1,639 | Pass1-Deferred; math/iteration-order split deferred |
| `Source/Parsek/RecordingOptimizer.cs` | 1,621 | Pass1-Deferred; optimizer identity/order split deferred |
| `Source/Parsek/TrajectoryTextSidecarCodec.cs` | 1,563 | Pass2-Done; text trajectory ConfigNode codec extracted behind `RecordingStore` wrappers |
| `Source/Parsek/ParsekKSC.cs` | 1,520 | Pass1-Deferred; KSC/flight playback sharing deferred |
| `Source/Parsek/RecordingTree.cs` | 971 | Pass2-Done; record-only codec extracted behind wrappers, branch point serialization and caller migration deferred |

`RecordingTree.cs` remains listed here for pass-tracking continuity even though
the record-only extraction drops it below the original largest-file threshold.

## Growth Since Refactor-3 Inventory

These frozen deltas compare the refactor-4 base snapshot against files that
also existed in `docs/dev/done/refactor/refactor-3-inventory.md`.

| File | Refactor-3 lines | Refactor-4 base lines | Delta |
|------|------------------|---------------|-------|
| `LedgerOrchestrator.cs` | 900 | 6,596 | +5,696 |
| `ParsekFlight.cs` | 8,765 | 14,503 | +5,738 |
| `GhostPlaybackLogic.cs` | 2,589 | 5,343 | +2,754 |
| `VesselSpawner.cs` | 1,473 | 4,166 | +2,693 |
| `GhostPlaybackEngine.cs` | 1,770 | 4,312 | +2,542 |
| `GhostMapPresence.cs` | 1,211 | 3,408 | +2,197 |
| `ParsekScenario.cs` | 2,248 | 4,172 | +1,924 |
| `BackgroundRecorder.cs` | 2,788 | 4,489 | +1,701 |
| `FlightRecorder.cs` | 5,267 | 6,689 | +1,422 |
| `RecordingStore.cs` | 2,958 | 4,350 | +1,392 |
| `GameStateRecorder.cs` | 975 | 2,004 | +1,029 |
| `KspStatePatcher.cs` | 777 | 1,759 | +982 |
| `CrewReservationManager.cs` | 686 | 1,447 | +761 |
| `RecordingOptimizer.cs` | 863 | 1,621 | +758 |
| `GhostVisualBuilder.cs` | 6,484 | 7,193 | +709 |
| `ParsekKSC.cs` | 897 | 1,520 | +623 |
| `RecordingTree.cs` | 1,013 | 971 | -42 |
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
| `Source/Parsek/RewindInvoker.cs` | 1,218 | New rewind-to-separation surface |
| `Source/Parsek/TrajectorySidecarBinary.cs` | 1,124 | Binary storage/serialization surface |
| `Source/Parsek/Diagnostics/DiagnosticsComputation.cs` | 1,054 | Diagnostics computation surface |
| `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` | 934 | Finalization/extrapolation bridge |
| `Source/Parsek/UI/KerbalsWindowUI.cs` | 841 | Kerbals UI surface |
| `Source/Parsek/Patches/GhostTrackingStationPatch.cs` | 817 | Tracking Station patch surface |
| `Source/Parsek/RecordingEndpointResolver.cs` | 816 | Endpoint resolution helper |
| `Source/Parsek/RecordingTreeRecordCodec.cs` | 815 | New record-level `.sfs` ConfigNode codec extracted from `RecordingTree` |
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
| `LedgerOrchestrator.cs` | 6.6k-line GameActions hub, +5.7k since refactor-3 |
| `FlightRecorder.cs` | 6.7k-line sampling/event surface |
| `GhostPlaybackLogic.cs` | 5.3k-line playback/visual logic helper |
| `UI/RecordingsTableUI.cs` | 4.9k-line extracted UI surface with prior coupling risk |
| `BackgroundRecorder.cs` | 4.5k-line background sampling surface |
| `RecordingStore.cs` | 4.4k-line storage surface, +1.4k since refactor-3; Pass 2 sidecar commit batch, save/load paths, text trajectory codec, manifest codec, and group store extracted |
| `GhostPlaybackEngine.cs` | 4.3k-line engine core |
| `ParsekScenario.cs` | 4.2k-line save/load/lifecycle host |
| `VesselSpawner.cs` | 4.2k-line spawn/snapshot utility |

### Tier 2 - Large Focused Files

These may be suitable for small parallel batches only after ownership is
clearly separated by file.

| File | Lines | Pass 1 status |
|------|-------|---------------|
| `GhostMapPresence.cs` | 3,408 | Done; proto-vessel node helpers extracted |
| `WatchModeController.cs` | 3,197 | Done; watch entry helpers extracted |
| `GameStateRecorder.cs` | 2,004 | Pass3-Done for facility polling owner; remaining event-handler families need owner map |
| `UI/CareerStateWindowUI.cs` | 1,867 | Done; tab view-model helpers extracted |
| `GameActions/KspStatePatcher.cs` | 1,759 | Pass3-Done for facility patcher; remaining state families need owner proposals |
| `BallisticExtrapolator.cs` | 1,639 | Deferred; math/order sensitive |
| `RecordingOptimizer.cs` | 1,621 | Deferred; optimizer identity/order sensitive |
| `RecordingTree.cs` | 971 | Done; record-only codec extracted behind wrappers, branch point serialization and caller migration deferred |
| `ParsekKSC.cs` | 1,520 | Deferred; KSC/flight playback owner proposal needed |
| `CrewReservationManager.cs` | 1,447 | Deferred; reservation/roster/Harmony-patch ownership needs a focused proposal |
| `EngineFxBuilder.cs` | 1,367 | Pass 0 read pending; visual/FX builder likely belongs with visual runtime validation |
| `KerbalsModule.cs` | 1,273 | Pass 0 read pending; kerbal-state and mission-outcome ownership needs classification |
| `ParsekPlaybackPolicy.cs` | 1,268 | Pass 0 read pending; playback event policy split likely needs lifecycle owner proposal |
| `UI/TimelineWindowUI.cs` | 1,255 | Deferred; timeline filter/action ownership needed |
| `RewindInvoker.cs` | 1,218 | Deferred; scene-load checkpoint ownership needed |
| `VesselGhoster.cs` | 1,190 | Pass 0 read pending; snapshot/ghost materialization ownership needs classification |
| `TrajectorySidecarBinary.cs` | 1,124 | Pass 0 read pending; binary sidecar codec ownership needs classification |
| `Diagnostics/DiagnosticsComputation.cs` | 1,054 | Pass 0 read pending; diagnostics thresholds/metrics ownership needs classification |

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

Pass 1 status: `Timeline/TimelineBuilder.cs` was used as the canary and is
Done. The other Tier 3 examples remain Deferred for Pass 1 unless a later Pass 2
proposal promotes one with a focused owner boundary and validation scope.

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
| `BackgroundRecorder.cs` | `HandleBackgroundVesselSplit` | 533 | 157 |
| `BackgroundRecorder.cs` | `OnBackgroundPhysicsFrame` | 1389 | 163 |
| `FlightRecorder.cs` | `RestoreTrackSectionAfterFalseAlarm` | 5260 | 150 |
| `LedgerOrchestrator.cs` | `NotifyLedgerTreeCommitted` | 3201 | 122 |
| `LedgerLoadMigration.cs` | `MigrateLegacyTreeResources` | 158 | 199 |
| `PostWalkActionReconciler.cs` | `ClassifyPostWalk` | 91 | 182 |
| `PostWalkActionReconciler.cs` | `ReconcilePostWalk` | 292 | 120 |
| `PostWalkActionReconciler.cs` | `AggregatePostWalkWindow` | 975 | 131 |
| `GameActions/KspStatePatcher.cs` | `PatchRepeatableRecordNode` | 1099 | 120 |
| `GameActions/KspStatePatcher.cs` | `PatchContracts` | 1343 | 224 |
| `GhostMapPresence.cs` | `ShouldSuppressStateVectorProtoVesselForActiveReFly` | 822 | 169 |
| `GhostMapPresence.cs` | `IsRecordingInParentChainOfActiveReFly` | 1236 | 192 |
| `GhostMapPresence.cs` | `ResolveMapPresenceGhostSource` | 3381 | 373 |
| `GhostMapPresence.cs` | `RefreshTrackingStationGhosts` | 4461 | 160 |
| `GhostMapPresence.cs` | `ResolveStateVectorWorldPositionPure` | 4858 | 129 |
| `GhostMapPresence.cs` | `CreateGhostVesselFromStateVectors` | 5165 | 266 |
| `GhostMapPresence.cs` | `UpdateGhostOrbitFromStateVectors` | 5457 | 188 |
| `GhostMapPresence.cs` | `CreateGhostVesselsFromCommittedRecordings` | 6072 | 126 |
| `GhostPlaybackEngine.cs` | `UpdatePlayback` | 451 | 371 |
| `GhostPlaybackEngine.cs` | `RenderInRangeGhost` | 892 | 196 |
| `GhostPlaybackEngine.cs` | `UpdateLoopingPlayback` | 1201 | 325 |
| `GhostPlaybackEngine.cs` | `UpdateOverlapPlayback` | 1531 | 208 |
| `GhostPlaybackEngine.cs` | `UpdateExpireAndPositionOverlaps` | 1745 | 145 |
| `GhostPlaybackEngine.cs` | `UpdateReentryFx` | 2392 | 126 |
| `GhostPlaybackEngine.cs` | `ReusePrimaryGhostAcrossCycle` | 2995 | 159 |
| `GhostPlaybackEngine.cs` | `TryPopulateGhostVisuals` | 3481 | 200 |
| `GhostPlaybackLogic.cs` | `ReapplySpawnTimeModuleBaselinesForLoopCycle` | 1581 | 123 |
| `GhostPlaybackLogic.cs` | `ApplyPartEvents` | 1852 | 201 |
| `GhostPlaybackLogic.cs` | `TryGetPendingWatchActivationUT` | 5369 | 132 |
| `ParsekFlight.cs` | `GetActiveRecordingIdForTagging` | 183 | 123 |
| `ParsekFlight.cs` | `CapturePostSwitchPartStateTokens` | 6098 | 135 |
| `ParsekFlight.cs` | `StartRecording` | 8408 | 130 |
| `ParsekFlight.cs` | `EnsureActiveRecordingTerminalState` | 10181 | 147 |
| `ParsekFlight.cs` | `FinalizeIndividualRecording` | 10389 | 302 |
| `ParsekFlight.cs` | `CollectNearbySpawnCandidates` | 18111 | 121 |
| `ParsekScenario.cs` | `SaveActiveTreeIfAny` | 625 | 127 |
| `ParsekScenario.cs` | `HandleRewindOnLoad` | 2001 | 124 |
| `ParsekScenario.cs` | `TryRestoreActiveTreeNode` | 2720 | 138 |
| `ParsekScenario.cs` | `SpliceMissingCommittedRecordingsIntoLoadedTree` | 3448 | 193 |
| `ParsekScenario.cs` | `RefreshLoadedRecordingFromCommittedSplit` | 3680 | 121 |
| `RecordingStore.cs` | `RunOptimizationSplitPass` | 1727 | 147 |
| `RecordingSidecarStore.cs` | `LoadRecordingFilesFromPathsInternal` | 187 | 149 |
| `RecordingSidecarStore.cs` | `SaveRecordingFilesToPathsInternal` | 712 | 156 |
| `RecordingSidecarStore.cs` | `ReconcileReadableSidecarMirrors` | 869 | 128 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingsTableHeader` | 879 | 136 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingsWindow` | 1089 | 234 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingRow` | 1327 | 419 |
| `UI/RecordingsTableUI.cs` | `DrawGroupTree` | 1823 | 494 |
| `UI/RecordingsTableUI.cs` | `DrawVirtualUnfinishedFlightsGroup` | 2383 | 204 |
| `UI/RecordingsTableUI.cs` | `DrawRecordingBlock` | 3053 | 138 |
| `UI/RecordingsTableUI.cs` | `DrawLoopPeriodCell` | 4323 | 129 |
| `VesselSpawner.cs` | `SpawnAtPosition` | 959 | 128 |
| `VesselSpawner.cs` | `SpawnOrRecoverIfTooClose` | 1143 | 278 |
| `VesselSpawner.cs` | `TryRepairSnapshotBodyProvenance` | 3687 | 164 |
| `WatchModeController.cs` | `EnterWatchMode` | 1441 | 175 |
| `Rendering/AnchorPropagator.cs` | `Run` | 193 | 469 |

## Pass 0 Detailed Read Notes

### `Source/Parsek/GameActions/LedgerOrchestrator.cs`

At the Pass 0 detailed read, the file was no longer just the compact 900-line
hub recorded during refactor-3. It contained several distinct bands:

- module initialization, resource tracker availability, and seed state
- commit-window earnings reconciliation
- vessel-cost actions, rollout adoption, and recovery pairing
- recalculation, KSP patching, committed-science rebuild, and timeline
  invalidation
- load repair and legacy tree-resource migration
- KSC action expectation reconciliation
- post-walk reconciliation and aggregation
- facility slot and pending-science notification helpers

Several of those bands have since moved to focused owners, as recorded in the
Pass 3 notes below.

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

Pass 3 KSC split completed:

- PR #620 added the proposal for the KSC action classifier/reconciler slice.
- PR #621 extracted `KscActionExpectationClassifier` for the
  `CreateExpectationLeg` and `ClassifyAction` body, then moved the KSC
  DTO/enum types into that classifier.
- PR #621 also extracted `KscActionReconciler` for
  `KscExpectedLegMatch`, `ReconcileKscAction`, KSC expectation-leg matching,
  aggregate expected-delta computation, resource channel tagging, and the
  canonical `KscReconcileEpsilonSeconds` source.
- `LedgerOrchestrator` keeps compatibility facades for `ClassifyAction`,
  `ReconcileKscAction`, `ResourceChannelTag`, and
  `KscReconcileEpsilonSeconds`.
- KSC event entry points, ledger writes, sequence assignment, ledger mutation,
  and resource/currency mutation order remain in `LedgerOrchestrator`.
- Validation for the landed slice: build passed, focused ledger/career gate
  passed 326 tests, and the full non-injection xUnit gate passed 9,261 tests.

Additional Pass 3 splits landed after the original inventory:

- `PostWalkActionReconciler` now owns post-walk classification,
  reconciliation, aggregation, and post-walk-only DTOs/constants behind
  `LedgerOrchestrator` facades.
- `LedgerLoadMigration` now owns old-save event migration, legacy tree-resource
  migration, load repair, broken-ledger recovery, and migration helper
  compatibility wrappers.
- `LedgerRecoveryFundsPairing` now owns vessel recovery funds pairing,
  deferred recovery requests, repair of legacy recovery dedup keys, and related
  test-facing accessors behind `LedgerOrchestrator` facades.
- `LedgerRolloutAdoption` now owns rollout spending/adoption matching and its
  helper state behind `LedgerOrchestrator` facades.

Remaining LedgerOrchestrator cross-file decomposition candidates are now
narrower: commit-window earnings reconciliation, vessel-cost action creation,
recalculation/patch orchestration, science reconciliation helpers shared across
commit-window and post-walk paths, and tree-commit notification.

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
- sort and grouping data helpers
- stateless table formatting helpers now owned by `RecordingsTableFormatters`
- stats, tooltip, and loop-period helpers

Pass 3 formatter extraction completed:

- Extracted stateless altitude, speed, distance, start/end position, and
  resource/inventory/crew manifest formatting into
  `RecordingsTableFormatters`. `RecordingsTableUI` keeps forwarding wrappers
  for existing tests and callers, while `ResolveParentVesselName` stays in the
  table owner because it depends on committed tree lookup through
  `RecordingStore`.

Pass 1 deferred:

- Left `DrawRecordingsWindow`, `DrawRecordingRow`, group tree drawing, and the
  filter/action rows inline. The candidate blocks are contiguous on screen but
  share IMGUI call order, static per-frame buffers, edit/confirmation state,
  cross-link scrolling, and group-picker state. Extracting them now would add
  callback plumbing without proving an ownership boundary.
- Keep `FormatAltitude`/`FormatDistance` and the three manifest formatter
  shapes deliberately duplicated for this behavior-neutral move. A later
  formatter cleanup can fold the distance units through one helper and consider
  a shared manifest template after direct owner tests are in place.
- Existing formatter tests still target `RecordingsTableUI`/`ParsekUI`
  wrappers. Retarget them to `RecordingsTableFormatters` before deleting the
  wrapper layer.

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
`RecordingEndpointResolver`, and terminal-orbit helpers. Pass 1 leaves
`FinalizeIndividualRecording` inline because the remaining phases share live
vessel/cache lookup, scene-exit finalizer resolution, terminal state selection,
stable terminal snapshot refresh, terminal-orbit repair, endpoint finalization,
and warning/log emission. A future `RecordingFinalizer` or similar split may be
useful, but only after Pass 2 maps the current finalization cache producer,
endpoint resolver, scene-exit finalizer, and `ParsekFlight` call sites.

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

Pass 1 deferred:

- Left `AddPartVisuals` and `TryBuildRcsFX` inline. The candidate blocks
  interleave model-node cloning, renderer/variant selection, prefab discovery,
  transform resolution, particle setup, runtime diagnostics, and module-order
  assumptions. Even same-file helper extraction here should wait for a visual
  builder owner plan plus runtime visual validation.

Pass 2 discussion only: `RcsFxBuilder`, `VariantVisualRules`,
`ReentryFxBuilder`, or a narrower part-visual builder owner.

### `Source/Parsek/RecordingStore.cs`

This file combines recording/tree commit, grouping wrappers, optimization,
deletion, rewind, trajectory serialization, manifests, file I/O, and sidecar
mirrors.

Pass 1 completed:

- Split `RunOptimizationPass` into same-file helpers for merge, split, and
  boring-tail trim phases while keeping loop-sync, background-map rebuild, and
  dirty-file flush order unchanged.
- Split `InitiateRewind` into same-file helpers for rewind context setup,
  rewind strip-name collection, and temporary save cleanup. Owner resolution,
  temp save copy/preprocess, load-game invocation, adjusted-UT capture, scene
  load, and failure cleanup behavior remain unchanged.

Pass 2 first slice completed:

- Extracted `SidecarFileCommitBatch` into `Source/Parsek/SidecarFileCommitBatch.cs`
  for staged sidecar write/delete commits, rollback, and artifact cleanup.
  At this checkpoint, `RecordingStore` still owned save/load orchestration,
  sidecar epoch ownership and mutation order, `FilesDirty`, readable mirrors,
  snapshot policy, and codec dispatch.

Pass 2 second slice completed:

- Extracted save-path `RecordingSidecarStore` into
  `Source/Parsek/RecordingSidecarStore.cs` for save-side path resolution,
  sidecar epoch bump/rollback, staged authoritative sidecar writes, readable
  mirror reconciliation, and `FilesDirty` clearing. `RecordingStore` keeps the
  existing save wrappers and still owns grouping, optimization, deletion, and
  rewind entry points.

Pass 2 third slice completed:

- Extracted load-path `RecordingSidecarStore` orchestration for
  `LoadRecordingFiles`, test-path loading, sidecar load-failure
  marking/clearing, sidecar epoch validation, snapshot fallback/failure policy,
  loop migration/repair, terminal-orbit backfill, and endpoint backfill.
  `RecordingStore` keeps compatibility wrappers and the stable
  `SnapshotSidecarLoadState` / `SnapshotSidecarLoadSummary` surface while
  trajectory and snapshot codec dispatch stay in their existing owners.

Pass 2 fourth slice completed:

- Extracted `TrajectoryTextSidecarCodec` into
  `Source/Parsek/TrajectoryTextSidecarCodec.cs` for text ConfigNode trajectory
  serialization/deserialization, point/orbit/part/flag/segment-event codecs,
  track-section text serialization, section-authoritative helpers, flat
  trajectory sync/fallback repair, and text format version probing.
  `RecordingStore` keeps compatibility wrappers for production, tests, and
  generators while manifest codecs, snapshot codecs, sidecar epoch ownership,
  and `FilesDirty` mutation remain outside the text codec.

Pass 2 fifth slice completed:

- Extracted `RecordingManifestCodec` into
  `Source/Parsek/RecordingManifestCodec.cs` for crew end-state, resource
  manifest, inventory manifest, and crew manifest ConfigNode
  serialization/deserialization. `RecordingStore` keeps compatibility wrappers
  for `RecordingTree`, `ParsekScenario`, tests, and generators while tree record
  save/load, sidecar orchestration, trajectory codecs, snapshot codecs, sidecar
  epoch ownership, and `FilesDirty` mutation remain outside the manifest codec.

Pass 2 sixth slice completed after merging `origin/main`:

- Extracted `RecordingGroupStore` into `Source/Parsek/RecordingGroupStore.cs`
  for auto-generated tree groups, standalone auto-assignment tracking, group
  hierarchy coordination, and UI group mutation helpers. `RecordingStore` keeps
  compatibility wrappers and still owns the timeline state hub, tree commit
  pipeline, optimizer orchestration, deletion, and rewind entry points.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingOptimizerTests|FullyQualifiedName~RecordingStoreTests|FullyQualifiedName~LegacyTreeMigrationTests|FullyQualifiedName~RewindLoggingTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`
- Pass 2 save-path slice: `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~SnapshotSidecarCodecTests|FullyQualifiedName~TrajectorySidecarBinaryTests|FullyQualifiedName~Bug270SidecarEpochTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~TrackSectionSerializationTests|FullyQualifiedName~LoopIntervalLoadNormalizationTests|FullyQualifiedName~QuickloadResumeTests"` passed 235 tests; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings` passed 8,707 tests; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~InjectAllRecordings` passed 3 tests.
- Pass 2 load-path slice: `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~SnapshotSidecarCodecTests|FullyQualifiedName~TrajectorySidecarBinaryTests|FullyQualifiedName~Bug270SidecarEpochTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~TrackSectionSerializationTests|FullyQualifiedName~LoopIntervalLoadNormalizationTests|FullyQualifiedName~QuickloadResumeTests"` passed 236 tests; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings` passed 8,708 tests; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName~InjectAllRecordings` is currently blocked by a locked `KSP.log` / `Parsek.dll` from a running KSP process.
- Pass 2 text-codec slice: `dotnet build Source/Parsek/Parsek.csproj -c Debug -m:1 -v minimal --nologo` passed; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~DeserializeExtractedTests|FullyQualifiedName~SegmentEventSerializationTests|FullyQualifiedName~TrackSectionSerializationTests|FullyQualifiedName~TrackSectionSourceTests|FullyQualifiedName~FormatVersionTests|FullyQualifiedName~SerializationEdgeCaseTests|FullyQualifiedName~TrajectorySidecarBinaryTests|FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~Bug419DebrisMonotonicityTests|FullyQualifiedName~RecordingBuilderV6Tests"` passed 252 tests; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName!~InjectAllRecordings"` passed 9,051 tests.
- Pass 2 manifest-codec slice: `dotnet build Source/Parsek/Parsek.csproj -c Debug -m:1 -v minimal --nologo` passed; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName~CrewManifestSerializationTests|FullyQualifiedName~InventoryManifestSerializationTests|FullyQualifiedName~ResourceManifestSerializationTests|FullyQualifiedName~KerbalEndStateTests|FullyQualifiedName~RecordingStorageRoundTripTests|FullyQualifiedName~RecordingBuilderV6Tests"` passed 163 tests; `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj -c Debug -v minimal --nologo --filter "FullyQualifiedName!~InjectAllRecordings"` passed 9,051 tests.

Remaining tree-record codec work follows the Pass 2 owner plan and remains
gated on preserving file ordering, exception handling, sidecar epoch ordering,
and `FilesDirty` mutation order exactly.

Pass 2 storage/sidecar owner proposal:
`docs/dev/plans/refactor-4-pass2-storage-sidecars.md`. Rewind service
ownership remains discussion-only.

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

Remaining Pass 1 same-file candidates deferred:

- Narrow part-category scanning helpers were left inline because the remaining
  candidates touch subscription/polling order. Revisit only with a
  foreground/background part-event owner map.

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

Remaining Pass 1 same-file candidates deferred:

- `ShouldSpawnAtRecordingEnd` remains inline. The current pure decision is
  already compact enough that a guard-clause extraction would add churn without
  reducing the Pass 2 risk surface.

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

Pass 1 completed:

- Extracted the `UpdatePlayback` per-frame reset block into
  `ResetPerFramePlaybackCounters`. Deferred event buffers, spawn/destroy
  counters, overlap/lazy-reentry counters, heaviest-spawn fields, and
  diagnostics stopwatches reset in the original order. The
  `frameSessionSuppressed` local remains inline because it is consumed by the
  dispatch loop and frame-summary log.
- Left `UpdateLoopingPlayback`, `UpdateOverlapPlayback`, and
  `TryPopulateGhostVisuals` inline for Pass 1. Their schedule, metrics,
  pending-build, FX restoration, and lifecycle-event paths are tightly
  interleaved enough that a wider split should wait for the Pass 2 playback
  owner proposal.

Validation:

- `dotnet build Source/Parsek/Parsek.csproj`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~GhostPlaybackEngineTests|FullyQualifiedName~LoopPhaseTests|FullyQualifiedName~DeferredSpawnTests|FullyQualifiedName~Bug406GhostReuseLoopCycleTests|FullyQualifiedName~PartEventTests|FullyQualifiedName~ExplosionFxTests"`
- `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter FullyQualifiedName!~InjectAllRecordings`

Pass 2 discussion only: spawn loader, loop scheduler, reentry driver, and
observability collector owners.

### `Source/Parsek/ParsekScenario.cs`

This ScenarioModule is the persistence and lifecycle hub for game state,
rewind-to-separation state, active tree restore, external file loading, recording
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
rewind separation persistence owner.

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

Pass 1 deferred:

- Left the resource handlers, milestone enrichment, and facility polling inline.
  The resource paths differ in threshold/capture behavior, milestone enrichment
  mutates the pending map plus store/ledger copies in one ordered sequence, and
  facility polling mixes live KSP reads, cache updates, direct-ledger forwarding,
  and emitted counters. These should wait for an event-handler owner map.

Pass 3 facility-polling slice completed:

- `GameStateFacilityRecorder` now owns facility/building cache state, seeding,
  polling, and pure transition helpers.
- `GameStateRecorder` keeps the existing `SeedFacilityCacheFromCurrentState`,
  `PollFacilityState`, `CheckFacilityTransitions`, and
  `CheckBuildingTransitions` call surfaces as facades.
- Cross-family policy, event emission, recording tagging, suppression flags,
  and direct-ledger forwarding remain in `GameStateRecorder`.

Pass 2 discussion remains for contract, crew, resource/science, milestone,
strategy lifecycle, and other event handler owners.

### `Source/Parsek/GameActions/KspStatePatcher.cs`

This focused but large patcher applies resource, tech, facility, destruction,
per-subject science, milestone, progress, and contract state back into KSP.

Pass 1 same-file candidates:

- Split `PatchContracts` and `PatchProgressNodeTree` by contiguous patch
  phases.
- Extract repeatable record-state helpers only when values, reflection access,
  and UI patching order remain unchanged.

Pass 1 deferred:

- Left `PatchContracts` and `PatchProgressNodeTree` inline. The candidate
  blocks interleave KSP collection mutation, reflection-backed proto state,
  append-only bucket preservation, UI refresh events, and summary counters.
  Same-file extraction would mostly pass mutable counter/context bags without
  clarifying ownership.

Pass 3 facility-family split completed:

- `FacilityStatePatcher` now owns facility level and destruction patch behavior.
- `KspStatePatcher.PatchFacilities` and `PatchDestructionState` remain as
  wrappers so existing call signatures and patch order stay stable.
- Remaining patch families still need separate owner proposals before moving.

### `Source/Parsek/BallisticExtrapolator.cs`

This math-heavy extrapolation file is large but cohesive. It includes nested
state types, `Extrapolate`, event search, and `TwoBodyOrbit` math.

Pass 1 same-file candidates:

- Only consider `Extrapolate` phase helpers if a focused read can prove the
  extraction preserves iteration order, floating-point operations, and stop
  conditions.

Pass 1 deferred:

- Left `Extrapolate` and the nested solver/math helpers inline. The remaining
  seams are floating-point and iteration-order sensitive; a structural split
  should be preceded by math-specific regression review.

Pass 2 discussion only: moving `TwoBodyOrbit` or solver helpers into separate
math owners.

### `Source/Parsek/RecordingOptimizer.cs`

This optimizer is behavior-sensitive because ordering affects merge/split/trim
decisions and recording identity.

Pass 1 same-file candidates:

- Split `TrimBoringTail`, `SplitAtSection`, and `MergeInto` by local phases
  only with focused optimizer tests.

Pass 1 deferred:

- Left `TrimBoringTail`, `SplitAtSection`, and `MergeInto` inline. Their local
  ordering controls recording identity, branch points, split boundaries, and
  optimizer logs, so helper extraction is deferred until an optimizer-specific
  test-and-review slice.

Pass 2 discussion only: broader optimizer strategy decomposition.

### `Source/Parsek/RecordingTree.cs`

This serialization/tree owner now keeps tree-level save/load and branch-point
serialization while per-record `.sfs` field serialization lives in
`RecordingTreeRecordCodec`.

Pass 1 same-file candidates:

- Review `SaveRecordingResourceAndState` and
  `LoadRecordingResourceAndState` for local contiguous helper extraction.

Pass 1 deferred:

- Left the resource/state save-load helpers inline. They already sit behind
  extracted save/load entry points, but the remaining repeated shape is really a
  serialization codec question: field order, defaulting, legacy reads, and
  logging must be proposed together.

Pass 2 completed: record-only `RecordingTreeRecordCodec` extracted behind
wrappers. Branch point serialization and selective raw-record caller migration
remain separate follow-ups.

### `Source/Parsek/ParsekKSC.cs`

This older KSC playback file has overlap with `GhostPlaybackEngine`, especially
loop schedules, overlap state, audio, and interpolation.

Pass 1 same-file candidates:

- Split playback update and `InterpolateAndPositionKsc` phases if they are
  contiguous and validation can stay local.
- Extract local loop schedule helpers only without deduplicating with flight
  playback yet.

Pass 1 deferred:

- Left KSC playback update, interpolation, and local loop scheduling inline.
  The valuable split is the shared KSC/flight playback abstraction, which is an
  architectural Pass 2 proposal rather than a Pass 1 helper move.

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

Pass 1 deferred:

- Left timeline drawing, filter bars, time-range controls, and row rendering
  inline. The remaining helpers would still share IMGUI call order, filter
  mutation, row actions, and action-width/layout state, so this waits for a
  timeline filter/action model proposal.

Pass 2 discussion only: timeline filter/action model ownership.

### `Source/Parsek/RewindInvoker.cs`

This static rewind invocation file owns precondition handling, start invoke,
post-load consumption, strip activation marker work, and staging cleanup.

Pass 1 same-file candidates:

- Split `StartInvoke`, `ConsumePostLoad`, and `RunStripActivateMarker` by
  ordered precondition, marker, load, and cleanup phases.

Pass 1 deferred:

- Left `StartInvoke`, `ConsumePostLoad`, and `RunStripActivateMarker` inline.
  They encode a synchronous pre-load/post-load invariant across KSP scene
  teardown, temp-save cleanup, context survival, flight-ready deferral, and
  ledger recalculation. A helper-only move here would obscure the checkpoint
  sequence without creating a new owner.

Pass 2 discussion only: rewind invocation service ownership.

## Pass 1 Closure Summary

Pass 1 is closed for the mapped files. Files marked Done received
behavior-neutral same-file helper extraction and validation. Files marked
Deferred were reviewed and left inline because the next useful change is either
semantic, architectural, runtime-visual, math-sensitive, or UI-order-sensitive.
Files marked Pass 0 read pending were tiered mechanically but do not yet have a
detailed read note.

| File | Pass 1 result |
| --- | --- |
| `GhostVisualBuilder.cs` | Deferred; visual-builder helper split needs runtime visual validation and an owner plan. |
| `UI/RecordingsTableUI.cs` | Pass3-Done for `RecordingsTableFormatters` (post-PR-657 size: 4,781 lines); IMGUI row/tree extraction still needs field ownership map. |
| `ParsekFlight.cs` | Done for post-switch auto-record; finalization split deferred to Pass 2. |
| `FlightRecorder.cs` | Done for visual coverage logging; remaining part-event poller work deferred. |
| `GhostPlaybackLogic.cs` | Done for dictionary population and part events; remaining spawn policy cleanup deferred. |
| `BackgroundRecorder.cs` | Done for split discovery and loaded-state helper extraction. |
| `RecordingStore.cs` | Pass 2 first through sixth slices done for `SidecarFileCommitBatch`, save/load-path `RecordingSidecarStore`, `TrajectoryTextSidecarCodec`, `RecordingManifestCodec`, and `RecordingGroupStore`; optimization, deletion, and rewind wrappers remain with `RecordingStore` until separately approved. |
| `GhostPlaybackEngine.cs` | Done for per-frame playback reset helper extraction. |
| `ParsekScenario.cs` | Done for recording metadata load helper extraction. |
| `VesselSpawner.cs` | Done for spawn-state snapshot override helper extraction. |
| `GhostMapPresence.cs` | Done for proto-vessel node helper extraction. |
| `WatchModeController.cs` | Done for watch entry helper extraction. |
| `GameStateRecorder.cs` | Pass3-Done for `GameStateFacilityRecorder`; remaining handler families still need owner map. |
| `UI/CareerStateWindowUI.cs` | Done for `Build` tab view-model helper extraction. |
| `GameActions/KspStatePatcher.cs` | Pass3-Done for `FacilityStatePatcher`; remaining patch families still need owner proposals. |
| `BallisticExtrapolator.cs` | Deferred; math and iteration-order sensitive. |
| `RecordingOptimizer.cs` | Deferred; recording identity/order sensitive. |
| `RecordingTree.cs` | Done for record-only codec extraction; branch point serialization and selective raw-record caller migration deferred. |
| `ParsekKSC.cs` | Deferred; useful split is KSC/flight playback architecture. |
| `UI/TimelineWindowUI.cs` | Deferred; timeline filter/action model ownership needed. |
| `RewindInvoker.cs` | Deferred; scene-load checkpoint sequence should remain visible until service ownership is proposed. |
| `CrewReservationManager.cs` | Deferred; reservation/roster/Harmony-patch ownership needs a focused proposal. |
| `EngineFxBuilder.cs` | Pass 0 read pending; likely grouped with visual/FX builder runtime validation. |
| `KerbalsModule.cs` | Pass 0 read pending; kerbal-state and mission-outcome ownership needs classification. |
| `ParsekPlaybackPolicy.cs` | Pass 0 read pending; playback event policy split likely needs lifecycle owner proposal. |
| `VesselGhoster.cs` | Pass 0 read pending; snapshot/ghost materialization ownership needs classification. |
| `TrajectorySidecarBinary.cs` | Pass 0 read pending; binary sidecar codec ownership needs classification. |
| `Diagnostics/DiagnosticsComputation.cs` | Pass 0 read pending; diagnostics thresholds/metrics ownership needs classification. |
| Tier 3 examples | Deferred except `Timeline/TimelineBuilder.cs`, which was the Pass 1 canary. |

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

## Next Investigation Priorities

1. Prepare the next Pass 3 owner proposal before moving more code across files:
   candidates include remaining LedgerOrchestrator commit-window/recalculation
   bands, visual builders, foreground/background part-event pollers,
   KSC/flight playback, remaining event handler families, remaining state patcher
   families, and rewind invocation.
2. Compare `RecordingStore.cs`, `TrajectorySidecarBinary.cs`, and snapshot
   sidecar helpers for repeated binary/text serialization patterns before any
   deduplication. Initial result: `refactor-4-pass2-storage-sidecars.md`
   recommends separate sidecar commit, sidecar orchestration, trajectory text
   codec, manifest codec, and tree-record codec owners. Those Pass 2 slices are
   complete; schema redesign remains out of scope, and binary/text format
   unification is deferred.
3. Build a static mutable state map for `GameStateRecorder`,
   `LedgerOrchestrator`, `RecordingStore`, `ParsekScenario`,
   `WatchModeController`, and `GhostPlaybackEngine`.
4. Audit magic thresholds and literal keys introduced after the `ParsekConfig`
   centralization entry in 0.8.3.
