# GhostMapPresence static-state map, 2026-11-07

The static-state map for `GhostMapPresence` (split rule S4: "build the static mutable state
map before moving anything"). For every static field of the type: where it is declared,
which methods write it, who outside the type writes or reads it, which reset paths clear it,
and which scenes reach a writer. It is the input for any later change that moves the type's
state, and for the long-method extractions of wave 2 stream W5 (5c), which must not reorder
a write against a read.

Measured on the refactor branch `claude/project-thread-bkwmrr` at `5dc4ebd78`, from the
compiled Debug `Parsek.dll` and `Parsek.Tests.dll` of that tree. Read-only: no production
code changed.

## How it was measured, and why not from source

A source grep cannot answer "who touches this field": a bare name is also a local, a
parameter or another type's field. Concretely, `CurrentUTNow` is declared on BOTH
`GhostMapPresence` and `WatchModeController` (`WatchModeController.Runtime.cs:10`), and
`WatchModeController.cs:2979` calls its own; the 2026-09-22 extraction study counted "52
production references across 17 files" for `GhostMapPresence.CurrentUTNow` by name, while
the compiled code has 7 `GhostMapPresence` methods and `MapRender.PhaseFactory` reading it.
(The shared `ic` CultureInfo name is declared by 18 other types too; it is immutable and
left out below.)

So the map is read from IL, where every field operand names its declaring type:
`scripts/refactor/static_state_map.py` disassembles the assembly with `ikdasm`, lists the
type's static non-literal fields, and for every method body runs a stack simulation that
follows each loaded field value to the instruction that consumes it (`stsfld` = reassign,
`ldsflda` = address taken, a mutating collection method called on the value = mutate, any
other call on it = read, passed as an argument = read unless the callee mutates). Lambdas,
closures, local functions and iterator state machines are credited to the source method
that wrote them. Its unit tests (`scripts/refactor/test_static_state_map.py`) include a
same-named field on a second type and fail if the declaring type is ignored (mutation
checked).

How the table columns were derived from the tool's JSON:

- **written by**: `GhostMapPresence` methods whose access kinds include reassign, addr,
  mutate, or an argument pass to `IndexShift.DictAfterDelete` / `DictAfterInsert` (which
  shift the dictionary in place). The type initialiser and the eight reset paths below are
  left out of this column; `(T)` marks a `*ForTesting` seam.
- **outside the type**: production methods of other types that write (W) or read (R) the
  field itself, not through a `GhostMapPresence` method. In-game tests (in `Parsek.dll`) are
  counted, not listed.
- **reset paths**: a reset root clears the field when the root or any `GhostMapPresence`
  method it calls (transitively) writes it.
- **scenes reaching a writer**: the production host types that reach any writer through
  `GhostMapPresence`'s own call chains, mapped to a scene: `FLIGHT` (`ParsekFlight*`,
  `ParsekPlaybackPolicy`, `MapRender.MapViewScene`, `WatchModeController`, `VesselGhoster`,
  the recorders, `VesselSpawner`, the flight-only patches), `TS` (`ParsekTrackingStation`,
  `MapRender.TrackingStationScene`, `GhostTracking*` and the TS Fly patches), `map(F+TS)`
  (the orbit-renderer / map-object patches, the polyline driver and the `MapRender` types
  that run in both map scenes), `any` (`ParsekScenario`, `ParsekUI`, `ParsekHarmony`, the
  alarm-jump patch, the command seam). `test-only` means only tests write it.

Reset roots (all in `GhostMapPresence`):

| abbr | method | production callers |
| --- | --- | --- |
| RFT | `ResetForTesting` | none (xUnit only) |
| RBTR | `ResetBetweenTestRuns` | `InGameTestRunner.PerformBetweenRunCleanup` |
| RAGV | `RemoveAllGhostVessels` | `ParsekFlight.OnDestroy`, `ParsekTrackingStation.OnDestroy`, `ParsekFlightPlaybackHost.DestroyAllTimelineGhosts`, and via `RemoveAllGhostVesselsBeforeStockFly` the TS Fly patch and `AlarmJumpSwitchPatch` |
| CFMPS | `ClearFlightMapPresenceState` | `ParsekPlaybackPolicy.Dispose`, `ParsekPlaybackPolicy.HandleAllGhostsDestroying` |
| CFCS | `ClearFrameCoverageSets` | `GhostTrajectoryPolylineRenderer.Driver.LateUpdate` (every frame), `MapRenderProbe.OnGameSceneSwitchRequested` |
| ROCT | `ResetOverlapCollapseTally` | `RunOverlapPerInstanceSweep` (start of every sweep) |
| ELS* | `EmitLifecycleSummary` | `RunFlightMapStateVectorUpdatePass`, `UpdateTrackingStationGhostLifecycle` (per tick, after the summary line) |
| SHIFT | `ShiftIndexKeyedPresence` | `ReindexPresenceAfterDelete` / `ReindexPresenceAfterInsert` (the committed-list notifications). Not a reset: listed to show which stores the index contract covers. |

## Counts

62 fields, the same 62 the architecture view's SIZE section counts: 27 reassignable (one of
them the static auto-property `CachedTrackingStationSuppressedIds`) and 35 `static readonly`
collections whose contents change. The IL has one more static, `ic`, an immutable
CultureInfo. Every field is declared in `GhostMapPresence.cs` except the five
`overlapCollapse*` counters and `lastOverlapSweepUT` (`GhostMapPresence.OverlapHighWarp.cs`)
and `cachedReFlySuppressionSearchTrees` (`GhostMapPresence.ReFlySuppressionCache.cs`).

## The map

### Index-keyed by committed-recording position (shifted by `ShiftIndexKeyedPresence`)

| field | declared | kind | written by (GhostMapPresence methods) | written / read outside the type (production) | reset paths | scenes reaching a writer |
| --- | --- | --- | --- | --- | --- | --- |
| `vesselsByRecordingIndex` | GhostMapPresence.cs:420 | readonly collection | `RemoveGhostVesselForRecording`, `TrackRecordingGhostVessel` | - | RFT, RBTR, RAGV, SHIFT | FLIGHT, TS |
| `vesselPidToRecordingIndex` | GhostMapPresence.cs:426 | readonly collection | `CreateOverlapInstanceVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `TrackRecordingGhostIdentityForTesting(T)`, `TrackRecordingGhostVessel` | - | RFT, RBTR, RAGV, SHIFT | FLIGHT, TS |
| `lastKnownByRecordingIndex` | GhostMapPresence.cs:69 | readonly collection | `RemoveGhostVesselForRecording`, `StashLastKnownFrame` | - | RFT, RBTR, RAGV, SHIFT | FLIGHT, TS |
| `overlapInstanceVessels` | GhostMapPresence.cs:445 | readonly collection | `CreateOverlapInstanceVessel`, `RemoveOverlapInstance`, `TryRepointOverlapInstance` | - | RFT, RBTR, RAGV, SHIFT | FLIGHT, TS |
| `activeReFlyDeferredStateVectorGhostSessions` | GhostMapPresence.cs:467 | readonly collection | `IsStateVectorGhostDeferredForActiveReFlySession`, `MarkStateVectorGhostDeferredForActiveReFly`, `RemoveGhostVesselForRecording` | - | RFT, RBTR, RAGV, SHIFT | FLIGHT, TS |
| `trackingStationStateVectorOrbitTrajectories` | GhostMapPresence.cs:461 | readonly collection | `CreateGhostVesselsFromCommittedRecordings`, `RefreshTrackingStationGhosts`, `RemoveGhostVesselForRecording`, `UpdateTrackingStationGhostLifecycle` | - | RFT, RBTR, RAGV, SHIFT | FLIGHT, TS |
| `trackingStationStateVectorCachedIndices` | GhostMapPresence.cs:464 | readonly collection | `CreateGhostVesselsFromCommittedRecordings`, `RefreshTrackingStationGhosts`, `RemoveGhostVesselForRecording`, `UpdateTrackingStationGhostLifecycle` | - | RFT, RBTR, RAGV, SHIFT | FLIGHT, TS |
| `flightPendingMapVessels` | GhostMapPresence.cs:1589 | readonly collection | `HandleFlightGhostDestroyedMapPresenceCore`, `RunFlightMapDeferredCreatePass` | - | CFMPS, SHIFT | FLIGHT, TS |
| `flightSoiGapStateVectorExpectedBodies` | GhostMapPresence.cs:1592 | readonly collection | `HandleFlightGhostCreatedMapPresence`, `HandleFlightGhostDestroyedMapPresenceCore`, `RunFlightMapDeferredCreatePass`, `RunFlightMapStateVectorUpdatePass` | - | CFMPS, SHIFT | FLIGHT, TS |
| `flightStateVectorOrbitTrajectories` | GhostMapPresence.cs:1622 | readonly collection | `HandleFlightGhostCreatedMapPresence`, `HandleFlightGhostDestroyedMapPresenceCore`, `RunFlightMapDeferredCreatePass`, `RunFlightMapStateVectorUpdatePass` | - | CFMPS, SHIFT | FLIGHT, TS |
| `flightStateVectorCachedIndices` | GhostMapPresence.cs:1629 | readonly collection | `HandleFlightGhostCreatedMapPresence`, `HandleFlightGhostDestroyedMapPresenceCore`, `RunFlightMapDeferredCreatePass`, `RunFlightMapOrbitReseedPass`, `RunFlightMapStateVectorUpdatePass` | - | CFMPS, SHIFT | FLIGHT, TS |
| `flightLastMapOrbitByIndex` | GhostMapPresence.cs:1605 | readonly collection | `HandleFlightGhostCreatedMapPresence`, `HandleFlightGhostDestroyedMapPresenceCore`, `RemovePreviousChainMapVessel`, `RunFlightMapDeferredCreatePass`, `RunFlightMapOrbitReseedPass`, `RunFlightMapStateVectorUpdatePass` | - | CFMPS, SHIFT | FLIGHT, TS |
| `flightChainMapOwner` | GhostMapPresence.cs:1616 | readonly collection | `RemovePreviousChainMapVessel` | - | CFMPS, SHIFT | FLIGHT |

### Ghost-pid-keyed and id-keyed presence stores

| field | declared | kind | written by (GhostMapPresence methods) | written / read outside the type (production) | reset paths | scenes reaching a writer |
| --- | --- | --- | --- | --- | --- | --- |
| `ghostMapVesselPids` | GhostMapPresence.cs:197 | readonly collection | `BuildAndLoadGhostProtoVesselCore`, `CreateOverlapInstanceVessel`, `RegisterGhostMapVesselPidForTesting(T)`, `RemoveGhostProtoVessel`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `RemoveRegisteredGhostVesselsBeforeTestReset` | R `Logistics.RouteEndpointResolver`; R `MapRender.GhostMapSceneBase`; R `MapRenderProbe`; R `ParsekScenario`; R `Patches.GhostOrbitDominantBodyPatch`; in-game tests read (10 types) | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostsWithSuppressedIcon` | GhostMapPresence.cs:266 | readonly collection | `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance` | W `Patches.GhostOrbitIconDrivePatch::Prefix`; W `Patches.GhostOrbitLinePatch::Postfix`; in-game tests write (1 method) | RFT, RBTR, RAGV | FLIGHT, TS, map(F+TS) |
| `vesselsByChainPid` | GhostMapPresence.cs:414 | readonly collection | `CreateGhostVessel`, `RemoveGhostVessel` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `lastKnownByChainPid` | GhostMapPresence.cs:72 | readonly collection | `CreateGhostVessel`, `RemoveGhostVessel`, `UpdateGhostOrbit` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `vesselPidToRecordingId` | GhostMapPresence.cs:475 | readonly collection | `CreateGhostVessel`, `CreateOverlapInstanceVessel`, `RebindGhostRecordingId`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `SetProtoBearingPidForTesting(T)`, `TrackRecordingGhostIdentityForTesting(T)`, `TrackRecordingGhostVessel` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostOrbitBounds` | GhostMapPresence.cs:553 | readonly collection | `ApplyOrbitToVessel`, `CreateGhostVesselFromSegment`, `CreateOverlapInstanceVessel`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `TrackEndpointTailGhostBoundsForTesting(T)`, `UpdateGhostOrbitFromStateVectors` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostBodyFrameOrbitBounds` | GhostMapPresence.cs:566 | readonly collection | `CreateOverlapInstanceVessel`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `UpdateGhostOrbitForRecording`, `UpdateGhostOrbitFromStateVectors` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostOrbitLoopShiftedPids` | GhostMapPresence.cs:579 | readonly collection | `ApplyOrbitToVessel`, `CreateOverlapInstanceVessel`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `UpdateGhostOrbitFromStateVectors` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostOrbitEpochShift` | GhostMapPresence.cs:596 | readonly collection | `ApplyOrbitToVessel`, `CreateOverlapInstanceVessel`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `UpdateGhostOrbitFromStateVectors` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostIconDrivePropagation` | GhostMapPresence.cs:636 | readonly collection | `RecordIconDrivePropagateUT`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `TryRepointOverlapInstance`, `UpdateGhostOrbitFromStateVectors` | - | RFT, RBTR, RAGV | FLIGHT, TS, map(F+TS) |
| `ghostLastAppliedOrbitBody` | GhostMapPresence.cs:692 | readonly collection | `ApplyOrbitToVessel`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance`, `UpdateGhostOrbitFromStateVectors` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostLastAppliedOrbitElements` | GhostMapPresence.cs:704 | readonly collection | `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostNoBoundsSuppressLastFrame` | GhostMapPresence.cs:867 | readonly collection | `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance` | W `Patches.GhostOrbitIconDrivePatch::EmitIconSuppressTransition` | RFT, RBTR, RAGV | FLIGHT, TS, map(F+TS) |
| `ghostParkingConicLineHoldUntilUT` | GhostMapPresence.cs:328 | readonly collection | `ClearParkingConicLineHold`, `ClearParkingConicLineHoldsForTesting(T)`, `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `StampParkingConicLineHold` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `lastPolylineOwningRealTimePerPid` | GhostMapPresence.cs:283 | readonly collection | `ClearPolylineOwningStampsForTesting(T)`, `StampPolylineOwning` | - | RFT | map(F+TS) |
| `boundaryOverlapSecondaryPids` | GhostMapPresence.cs:459 | readonly collection | `RemoveOverlapInstance`, `TryEnsureBoundaryOverlapSecondaryInstance` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `flightTerminalMapRetentionLoggedIds` | GhostMapPresence.cs:1608 | readonly collection | `HandleFlightGhostDestroyedMapPresenceCore`, `PruneTerminalMapRetentionLogKeys`, `RunFlightMapOrbitReseedPass` | - | CFMPS | FLIGHT, TS |

### Per-frame coverage scratch (map render instrument)

| field | declared | kind | written by (GhostMapPresence methods) | written / read outside the type (production) | reset paths | scenes reaching a writer |
| --- | --- | --- | --- | --- | --- | --- |
| `drawnRecordingIdsThisFrame` | GhostMapPresence.cs:494 | readonly collection | `NoteDrawnRecordingCoverage`, `ResetCoverageSetsForTesting(T)`, `SetFrameCoverageForTesting(T)` | - | RFT, CFCS | map(F+TS) |
| `protoLessCoverageRecordingIdsThisFrame` | GhostMapPresence.cs:508 | readonly collection | `NoteDrawnRecordingCoverage`, `ResetCoverageSetsForTesting(T)`, `SetFrameCoverageForTesting(T)` | - | RFT, CFCS | map(F+TS) |
| `paintedRecordingIdsThisFrame` | GhostMapPresence.cs:539 | readonly collection | `NotePaintedRecordingLine`, `SetPaintedRecordingLineForTesting(T)` | - | CFCS | map(F+TS) |
| `protoBearingRecordingIdScratch` | GhostMapPresence.cs:545 | readonly collection | `AssertDrawnRecordingsAccounted`, `ResetCoverageSetsForTesting(T)` | - | RFT | map(F+TS) |

### Caches and their version stamps

| field | declared | kind | written by (GhostMapPresence methods) | written / read outside the type (production) | reset paths | scenes reaching a writer |
| --- | --- | --- | --- | --- | --- | --- |
| `chainTipSegmentsCache` | GhostMapPresence.cs:1049 | readonly collection | `ResolveLiveSegmentBearingChainTip` | - | none | FLIGHT, TS |
| `chainTipSegmentsCacheVersion` | GhostMapPresence.cs:1047 | reassignable | `ResolveLiveSegmentBearingChainTip` | - | none | FLIGHT, TS |
| `chainTipSegmentsCacheSupersedeVersion` | GhostMapPresence.cs:1048 | reassignable | `ResolveLiveSegmentBearingChainTip` | - | none | FLIGHT, TS |
| `cachedReFlySuppressionSearchTrees` | GhostMapPresence.ReFlySuppressionCache.cs:15 | reassignable | `ClearCachedReFlySuppressionSearchTrees`, `ComposeSearchTreesForReFlySuppression` | - | RFT, RBTR | FLIGHT, TS |
| `CachedTrackingStationSuppressedIds` | GhostMapPresence.cs:1035 | auto-prop | `CreateGhostVesselsFromCommittedRecordings`, `UpdateTrackingStationGhostLifecycle` | R `Display.GhostTrajectoryPolylineRenderer/Driver`; R `ParsekTrackingStation` | none | TS |
| `ghostNamesAppliedVersion` | GhostMapPresence.cs:775 | reassignable | `RefreshGhostProtoNamesIfDisplayNamesMoved` | - | RFT | FLIGHT, TS |

### Clocks, throttles, re-entrancy depth, sequence

| field | declared | kind | written by (GhostMapPresence methods) | written / read outside the type (production) | reset paths | scenes reaching a writer |
| --- | --- | --- | --- | --- | --- | --- |
| `nextMapOrbitUpdateTime` | GhostMapPresence.cs:1600 | reassignable | `UpdateFlightMapGhostLifecycle` | - | CFMPS | FLIGHT |
| `lastFlightMapLifecycleUT` | GhostMapPresence.cs:1603 | reassignable | `UpdateFlightMapGhostLifecycle` | - | CFMPS | FLIGHT |
| `lastOverlapSweepUT` | GhostMapPresence.OverlapHighWarp.cs:32 | reassignable | `RunOverlapPerInstanceSweep` | - | RFT, RBTR, RAGV | FLIGHT, TS |
| `ghostTeardownDepth` | GhostMapPresence.cs:212 | reassignable | `BeginGhostTeardown`, `EndGhostTeardown` | - | RFT, RAGV | FLIGHT, TS, any |
| `removeAllGhostVesselsDepth` | GhostMapPresence.cs:246 | reassignable | - | - | RFT, RAGV | FLIGHT, TS, any |
| `ghostTargetRequestSequence` | GhostMapPresence.cs:35 | reassignable | `SetGhostMapNavigationTarget` | - | RFT, RBTR | map(F+TS) |

### Diagnostic counters

| field | declared | kind | written by (GhostMapPresence methods) | written / read outside the type (production) | reset paths | scenes reaching a writer |
| --- | --- | --- | --- | --- | --- | --- |
| `lifecycleCreatedThisTick` | GhostMapPresence.cs:77 | reassignable | `CreateGhostVessel`, `CreateGhostVesselForRecording`, `CreateGhostVesselFromSegment`, `CreateGhostVesselFromStateVectors`, `CreateOverlapInstanceVessel` | - | RFT, ELS* | FLIGHT, TS |
| `lifecycleDestroyedThisTick` | GhostMapPresence.cs:78 | reassignable | `RemoveGhostVessel`, `RemoveGhostVesselForRecording`, `RemoveOverlapInstance` | - | RFT, ELS* | FLIGHT, TS |
| `lifecycleUpdatedThisTick` | GhostMapPresence.cs:79 | reassignable | `UpdateGhostOrbit`, `UpdateGhostOrbitForRecording`, `UpdateGhostOrbitFromStateVectors` | - | RFT, ELS* | FLIGHT, TS |
| `gapGlideInertialSeedCount` | GhostMapPresence.cs:87 | reassignable | `ResetGapGlideCountersForTesting(T)`, `UpdateGhostOrbitFromStateVectors` | - | none | FLIGHT, TS |
| `gapGlideBodyFixedFallbackCount` | GhostMapPresence.cs:88 | reassignable | `ResetGapGlideCountersForTesting(T)`, `UpdateGhostOrbitFromStateVectors` | - | none | FLIGHT, TS |
| `seamComdRefreshCount` | GhostMapPresence.cs:105 | reassignable | `ResetSeamComdCountersForTesting(T)`, `UpdateGhostOrbitFromStateVectors` | in-game tests read (1 type) | none | FLIGHT, TS |
| `seamComdSteadyCount` | GhostMapPresence.cs:106 | reassignable | `ResetSeamComdCountersForTesting(T)`, `UpdateGhostOrbitFromStateVectors` | in-game tests read (1 type) | none | FLIGHT, TS |
| `seamComdMaxOffOrbitDegPostRefresh` | GhostMapPresence.cs:107 | reassignable | `ResetSeamComdCountersForTesting(T)`, `UpdateGhostOrbitFromStateVectors` | - | none | FLIGHT, TS |
| `overlapCollapseRecordings` | GhostMapPresence.OverlapHighWarp.cs:35 | reassignable | `EnsureCollapsedOverlapInstance` | - | ROCT | FLIGHT, TS |
| `overlapCollapseRepointed` | GhostMapPresence.OverlapHighWarp.cs:36 | reassignable | `EnsureCollapsedOverlapInstance` | - | ROCT | FLIGHT, TS |
| `overlapCollapseRebuilt` | GhostMapPresence.OverlapHighWarp.cs:37 | reassignable | `EnsureCollapsedOverlapInstance` | - | ROCT | FLIGHT, TS |
| `overlapCollapseDestroyed` | GhostMapPresence.OverlapHighWarp.cs:38 | reassignable | `EnsureCollapsedOverlapInstance` | - | ROCT | FLIGHT, TS |
| `overlapCollapseCreated` | GhostMapPresence.OverlapHighWarp.cs:39 | reassignable | `EnsureCollapsedOverlapInstance` | - | ROCT | FLIGHT, TS |

### Injection seams

| field | declared | kind | written by (GhostMapPresence methods) | written / read outside the type (production) | reset paths | scenes reaching a writer |
| --- | --- | --- | --- | --- | --- | --- |
| `CurrentUTNow` | GhostMapPresence.cs:190 | reassignable | - | R `MapRender.PhaseFactory`; in-game tests write (18 methods) | RFT | test-only |
| `FindBodyByNameForTesting` | GhostMapPresence.cs:191 | reassignable | - | - | RFT | test-only |
| `TrackingStationSpawnOverrideForTesting` | GhostMapPresence.cs:1056 | reassignable | - | - | RFT | test-only |

## xUnit tests that write the state directly

From the same scan over `Parsek.Tests.dll` (field list taken from the `Parsek.dll` run).
These tests set up state by writing the field, not through a method, so any move of the field
(to another type, or behind a property) breaks them at compile time:

| field | test classes writing it |
| --- | --- |
| `CurrentUTNow` | `GhostMapPresenceTests`, `OverlapPerInstanceTests`, `TrackingStationSpawnTests` |
| `FindBodyByNameForTesting` | `GhostMapEndpointTailTests`, `PlaybackTogglePresenceScopeTests`, `TerminalOrbitLoopSynthesisTests` |
| `TrackingStationSpawnOverrideForTesting` | `ChainTipStaleVesselTests` |
| `ghostMapVesselPids` | `GhostMapPresenceTests`, `GhostVesselLoadPatchTests`, `PostLoadStripperTests` |
| `ghostsWithSuppressedIcon` | `GhostMapPresenceTests` |
| `ghostOrbitBounds` | `GhostMapEndpointTailTests`, `GhostMapObservabilityTests`, `GhostMapPresenceTests`, `GhostOrbitIconDriveTests` |
| `ghostOrbitEpochShift`, `ghostOrbitLoopShiftedPids` | `GhostMapEndpointTailTests`, `GhostOrbitIconDriveTests` |
| `ghostBodyFrameOrbitBounds`, `ghostNoBoundsSuppressLastFrame` | `GhostOrbitIconDriveTests` |
| `lifecycleCreatedThisTick`, `lifecycleDestroyedThisTick`, `lifecycleUpdatedThisTick` | `GhostMapObservabilityTests` |
| `flightChainMapOwner`, `flightSoiGapStateVectorExpectedBodies`, `flightStateVectorCachedIndices` | `OptimizationPassInvalidationTests` |

Plus one reflection read the compiler cannot see:
`GhostMapPresenceTests.ResetForTesting_ClearsAllState` (`GhostMapPresenceTests.cs:315-320`)
reads `trackingStationStateVectorOrbitTrajectories` and
`trackingStationStateVectorCachedIndices` by `GetField(name, NonPublic | Static)`; a rename
or a move to another type fails that test at run time with a null, not at compile time.
(The 2026-09-22 study named the two `flight*` dictionaries there; the test reads the
Tracking Station pair.) `BetweenRunGhostVesselRemovalTests.cs:136-138` looks up the two
methods `RemoveRegisteredGhostVesselsBeforeTestReset` and `ResetBetweenTestRuns` by name.

## What the map says

1. **The index-keyed stores and the committed-list contract agree.** Every store keyed (or
   valued) by committed-recording position is shifted by `ShiftIndexKeyedPresence`: the 13
   in the first table, no more and no fewer. A new index-keyed store that is not added there
   is the defect the contract in `RecordingStore.CommittedListNotifications.cs` exists to
   prevent; re-run the scan and compare the SHIFT column.

2. **The state is well encapsulated; the exceptions are named.** Outside the type, production
   code writes only two fields, both in render patches: `ghostsWithSuppressedIcon`
   (`GhostOrbitIconDrivePatch.Prefix`, `GhostOrbitLinePatch.Postfix`, the icon floor that
   `GhostOrbitLineCascadeDeleteGateTests` pins) and `ghostNoBoundsSuppressLastFrame`
   (`GhostOrbitIconDrivePatch.EmitIconSuppressTransition`). Production code reads three
   fields directly: `ghostMapVesselPids` (5 types), `CachedTrackingStationSuppressedIds`
   (`ParsekTrackingStation`, the polyline `Driver`), `CurrentUTNow` (`PhaseFactory`). Every
   other field is reached only through `GhostMapPresence` methods.

3. **Two lifetimes.** The ghost-pid-keyed stores and the Tracking Station index stores die
   with the ghosts (RAGV, from both scenes' `OnDestroy`). The flight per-frame stores
   (`flight*`, `nextMapOrbitUpdateTime`, `lastFlightMapLifecycleUT`) are cleared only by
   `ClearFlightMapPresenceState`, from `ParsekPlaybackPolicy`; RAGV, RBTR and RFT do not
   touch them. They are still written in the Tracking Station: the committed-list removal
   notification (`ParsekTrackingStation.OnCommittedRecordingRemoving` ->
   `HandleCommittedRecordingRemoving` -> `HandleFlightGhostDestroyedMapPresenceCore`) and
   SHIFT reach them there, which is why most of them read `FLIGHT, TS`.

4. **Never reset in a player session** (by design, recorded so a move keeps it): the
   version-keyed `chainTipSegmentsCache` and its two stamps, the diagnostic counters
   `gapGlide*` and `seamComd*` (reset only by `*ForTesting`), `ghostNamesAppliedVersion`,
   `ghostTargetRequestSequence` (RBTR resets it between in-game test runs),
   `lastPolylineOwningRealTimePerPid` (one stamp per ghost pid the polyline ever owned; read
   only within a grace window of real time, so a stale stamp is inert) and
   `CachedTrackingStationSuppressedIds` (replaced, not cleared, on every Tracking Station
   lifecycle pass).

5. **Re-entrancy guards are statics.** `ghostTeardownDepth` (`BeginGhostTeardown` /
   `EndGhostTeardown`) and `removeAllGhostVesselsDepth` (inside `RemoveAllGhostVessels`) gate
   what other code does while a teardown runs (`IsGhostTeardownInProgress`,
   `IsRemoveAllGhostVesselsInProgress`). An extraction that splits a teardown method must keep
   the increment and the decrement in the same frame of control (same try/finally).

## For the W5 tasks

- **5b (move `ResolveMapPresenceGhostSource` into a resolver type).** The method and most of
  its helpers already sit in the partial `GhostMapPresence.SourceResolution.cs` (pre-wave
  split). Its transitive call set inside the type is 53 methods; the statics they touch are
  only `FindBodyByNameForTesting` (through `FindBodyByName`) and the chain-tip cache trio
  (through `ResolveLiveSegmentBearingChainTip`, reached via the default
  `MapPresenceChainTipResolver` delegate). But the same set calls five members of the
  largest knot: `ParsekFlight.BodyFixedPrimaryCoversPlaybackUT`,
  `GhostPlaybackEngine.ShouldUseLoopAnchoredDebrisChain`, and (in
  `ResolveLiveSegmentBearingChainTip`) `EffectiveState.EffectiveTipRecordingId`,
  `ParsekScenario.Instance`, `RecordingStore.CommittedRecordings`. Replaying the ratchet
  graph with a new `MapPresenceSourceResolver` node that carries those references: residue
  263 -> 264 whether or not it reads `GhostMapPresence` statics; 263 -> 263 only when the
  five knot calls stay in `GhostMapPresence` and reach the resolver as delegates or values.
  A MOVE therefore cannot meet the bound; the residue-neutral form is a DECOUPLE (pass the
  five answers in), plus a decision on the `TrackingStationGhostSource` enum that 25 files
  spell as `GhostMapPresence.TrackingStationGhostSource`.
- **5c (extract long methods).** Of the 33 methods at or above 90 lines, the ones that write
  state appear in the "written by" columns above; that column is the list of stores an
  extracted block of such a method may write, and a block that writes a store keeps its
  position relative to every read of that store in the original order.

## Reproducing

```bash
# from a worktree of the measured commit
cd Source/Parsek && KSPDIR=/home/user/ksp-refs dotnet build -p:SkipKspDeploy=true   # Parsek.dll
cd ../.. && KSPDIR=/home/user/ksp-refs scripts/cloud-test.sh -class X                # Parsek.Tests.dll
python3 scripts/refactor/static_state_map.py Source/Parsek/bin/Debug/Parsek.dll \
    --type Parsek.GhostMapPresence --json gmp.json
python3 scripts/refactor/static_state_map.py \
    Source/Parsek.Tests/bin/Debug/net472/Parsek.Tests.dll \
    --type Parsek.GhostMapPresence --fields-from gmp.json --json gmp-tests.json
```

The columns above are derived from `gmp.json` by the rules in "How it was measured"; the
residue replay uses `archview.replay_cut_residue` with `RATCHET_CUT_ORDER` from
`scripts/arch/test_archview.py`. Outputs go to the scratchpad or `logs/`, never the repo.
