# GhostMapPresence extraction research, 2026-09-22

Read-only structural study of `GhostMapPresence`, a partial class whose members are all
static: what is in it, what each part touches, who calls it, what is already pure, what
the tests and gates pin, and which extractions are worth doing in what order. This is evidence plus
a ranked candidate list, in the vocabulary of
`docs/dev/research/architecture-opportunities-2026-09-14.md`. Nothing here has been
started; no file under the repo was modified.

Status note: this study was produced by one read-only research agent and predates the
generator's size view (`python scripts/arch/archview.py --check`, SIZE section). Two of
its claims were re-verified mechanically by the supervisor: the partial-class hotspot
misattribution in the generator (since fixed) and the disagreement between the two arms
of the non-loop live-pid grep gate. The cluster boundaries, the purity totals and the
predicted test and gate breakages are the agent's own and were not proven by a build or
a test run. Its mutable-static count (55) used a wider definition than the size view's
(16 reassignable plus 35 static readonly collections). Treat every candidate as a
candidate: each still needs a focused proposal and a clean-context review against
`docs/dev/refactor-guidelines.md`.

Two numbers in it have since been re-derived by the fixed tool and should be read from
there instead. Its "second in the tree" hotspot rank was computed for
`GhostMapPresence` alone against the unfixed, misattributed list; with every partial
class re-attributed, `GhostMapPresence` ranks **seventh** (`ParsekFlight`,
`RecordingStore`, `ParsekScenario`, `Recording`, `GhostPlaybackLogic` and
`FlightRecorder` are ahead of it). And the static-state count is now measured as 17
reassignable fields (16 fields plus one static auto-property with a setter) and 35
`static readonly` collections whose contents change.

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek\.claude\worktrees\vigorous-greider-b5560b`
at `ce9a875ae`. Architecture views regenerated with
`python scripts/arch/archview.py --check` before reading (they are gitignored and go
stale on every commit). Every number below regenerates from the scripts in Appendix B.

---

## 0. The headline the 2026-09-14 report missed, and why it missed it

The 2026-09-14 architecture findings (now the "Main findings" and "Opportunities, ranked"
sections of the generated atlas, `docs/dev/arch/atlas.html`) said almost nothing about
`GhostMapPresence` because the architecture tool's own hotspot ranking could not see it.

`archview.py` merges the parts of a partial class and attributes the merged type to the
FIRST file it scanned. For `GhostMapPresence` that is `GhostMapPresence.Observability.cs`
(`docs/dev/arch/types.json`: `"file": "GhostMapPresence.Observability.cs"`), a 324-line
partial with 2 commits. The hotspot score is `fileCommits * fanIn`
(`scripts/arch/archview.py:3115`), so the tool scores the type at `2 * 36 = 72` and it
falls out of the top-30 list entirely. Scored against the file that actually holds the
code, it is `271 * 36 = 9,756`, which would rank it SECOND in the whole tree, behind
`Recording` (21,842) and ahead of `LedgerOrchestrator` (7,632).

The artifact is systematic, not specific to this class. Every large partial is
mis-attributed the same way:

| type | attributed file | that file's commits | the real hot file | its commits |
| --- | --- | --- | --- | --- |
| `ParsekFlight` | `ParsekFlight.BreakupChildSeed.cs` | small | `ParsekFlight.cs` | 1,134 |
| `RecordingStore` | `RecordingStore.CommittedListNotifications.cs` | small | `RecordingStore.cs` | 412 |
| `GhostPlaybackEngine` | `GhostPlaybackEngine.ActivationHold.cs` | small | `GhostPlaybackEngine.cs` | 315 |
| `GhostMapPresence` | `GhostMapPresence.Observability.cs` | 2 | `GhostMapPresence.cs` | 271 |

The opportunities doc ranked `ParsekFlight` and `RecordingStore` from the FILE churn
table (`history.json -> files`), which is correct, and noted `PendingTreeState` as "the
seventh hotspot" without noticing that `RecordingStore` itself was absent from the same
list for the same reason. `GhostMapPresence` has no nested type churny enough to smuggle
it onto the list, so it vanished.

What the file-level signals actually say about `GhostMapPresence.cs`:

- **Churn rank 8 of 530** (271 commits of 5,182 since 2025-03-01), and it is the
  youngest file in the top ten: its first commit is `4a7af303b`, 2026-03-20, so those
  271 commits landed in six months. `ParsekFlight.cs` took 18 months to reach 1,134.
- **13,527 lines** in the main partial, 14,446 across all five.
- **In the 285-type knot with in-knot fan-in 34**, the eighth-largest hub after
  `RecordingStore` (78), `RecordingTree` (72), `Route` (51), `GameAction` (50),
  `GhostPlaybackLogic` (43), `EffectiveState` (40) and `ParsekScenario` (39). It is the
  **eighth greedy cut** (`135 -> 120`, freeing 15 types: `FlightGlobalsBodyInfo`,
  `FlightRecorder`, `GhostChainWalker`, `GhostPlaybackEngine`,
  `GhostTrajectoryPolylineRenderer`, `MapRenderTrace`, `MissionLoopUnitBuilder`,
  `ParsekFlight`, `ParsekPlaybackPolicy`, `ParsekScenario`,
  `RecordedRelativeAnchorPoseResolver`, `RouteGhostDriverSelector`, `RouteStore`,
  `SessionSuppressionState`, `ShadowRenderDriver`, `VesselSpawner`).
- **Four of the forty top cross-module co-change file pairs have it on one side**:
  `ParsekPlaybackPolicy.cs` (50), `ParsekFlight.cs` (48), `ParsekTrackingStation.cs`
  (35), `Patches/GhostOrbitLinePatch.cs` (29), `Display/GhostTrajectoryPolylineRenderer.cs`
  (23). It is the only Ghost-module file besides `GhostPlaybackEngine` /
  `GhostPlaybackLogic` to appear there.
- **1,542 qualified `GhostMapPresence.<member>` references** outside its own partials,
  across **101 files** (63 production, 38 test).

Measured against the four criteria the opportunities doc used (coupled / locked /
hurts / already partly delegated), `GhostMapPresence` scores on all four and belongs on
the ranked list between item 4 (`RecordingStore`) and item 7 (`VesselSpawner`).

---

## A. Structural inventory

### A.1 Files

| file | lines | commits | last touched |
| --- | --- | --- | --- |
| `Source/Parsek/GhostMapPresence.cs` | 13,527 | 271 | 2026-09-16 |
| `Source/Parsek/GhostMapPresence.Observability.cs` | 324 | 2 | 2026-07-07 |
| `Source/Parsek/GhostMapPresence.OverlapSchedule.cs` | 260 | 2 | 2026-09-11 |
| `Source/Parsek/GhostMapPresence.SuppressionFinders.cs` | 185 | 2 | 2026-09-15 |
| `Source/Parsek/GhostMapPresence.ReFlySuppressionCache.cs` | 150 | 1 | 2026-06-25 |
| **total** | **14,446** | | |

`grep -c "#region" Source/Parsek/GhostMapPresence.cs` returns **0**: there are no
regions, so the clusters below are derived from code, not from an authored grouping.

### A.2 Member counts

Parsed by brace matching over a line-preserving comment / string stripper
(Appendix B, `gmp_parse.py`); 415 direct members of the class, no gaps and no overlaps
between member spans.

| kind | count | internal | private |
| --- | --- | --- | --- |
| method | 311 (290 distinct names, 17 overload groups) | 201 | 110 |
| field | 87 | 59 | 28 |
| property | 3 | 3 | 0 |
| nested struct | 9 | 9 | 0 |
| nested enum | 3 | 3 | 0 |
| nested class | 2 | 0 | 2 |
| **total** | **415** | | |

Method bodies total **11,596 code lines** (declaration line to closing brace);
**13,803** counting the doc comments and attributes that would travel with a member on
an extraction. There are no public members: the whole class is `internal`.

### A.3 Nested types (14)

| type | kind | file:line | lines | used outside the partials |
| --- | --- | --- | --- | --- |
| `TrackingStationGhostSource` | enum | GhostMapPresence.cs:32 | 9 | **25 files, 118 refs** (22 prod / 96 test) |
| `TrackingStationSpawnHandoffState` | struct | :140 | 16 | no |
| `GhostTargetVerificationStatus` | enum | :157 | 11 | via `LogGhostTargetVerificationForTesting` only |
| `LastKnownGhostFrame` | struct | :192 | 12 | no |
| `GhostProtoOrbitSeedDiagnostics` | struct | :329 | 16 | no |
| `OrbitalCheckpointStateVectorFallbackDecision` | struct | :346 | 16 | tests only |
| `TrackingStationGhostSourceBatch` | private class | :363 | 109 | no |
| `IconDrivePropagation` | struct | :871 | 5 | no |
| `NoBoundsSuppressTransition` | enum | :1052 | 16 | `Patches/GhostOrbitLinePatch.cs` |
| `StateVectorWorldFrame` | struct | :7715 | 8 | `StateVectorWorldFrameTests.cs` |
| `PendingMapVessel` | struct | :11182 | 16 | pinned ABSENT from `ParsekPlaybackPolicy.cs` by a gate |
| `GhostMapVisibilityCounters` | struct | Observability.cs:15 | 11 | no |
| `GhostMapDecisionFields` | struct | Observability.cs:31 | 23 | tests |
| `ReFlySuppressionSearchTreeCache` | private class | ReFlySuppressionCache.cs:17 | 19 | no |

`TrackingStationGhostSource` is the one nested type with a real external surface. It is
the return type of `ResolveMapPresenceGhostSource` / `ResolveTrackingStationGhostSource`
and is named by 25 files. Any move of the source-resolution cluster moves this enum with
it, or leaves a type alias behind.

### A.4 Static state, grouped by what it represents

87 fields plus 3 properties. **32 are `const`** reason / threshold strings
(`TrackingStationGhostSkip*` x13, `TrackingStationSpawnSkip*` x7,
`OrbitalCheckpointStateVectorReject*` x4, four `StateVector*` thresholds,
`GhostVesselNamePrefix`, `Tag`, `TsFlyBeforeStockIndexofReason`,
`FlyIndexDriftTargetNotInLiveList`, `LegacyPointCoverageMaxGapSeconds`,
`MapOrbitUpdateIntervalSec`). Those are data, not state, and 20 of them are asserted on
directly by tests.

The remaining **55 mutable statics** split as follows.

**Index-keyed by committed-recording POSITION (10)** - these are the ones the
`RecordingStore.CommittedListNotifications.cs` contract governs; every one of them is
shifted by `ShiftIndexKeyedPresence`:

`vesselsByRecordingIndex`, `vesselPidToRecordingIndex` (reverse), `lastKnownByRecordingIndex`,
`trackingStationStateVectorOrbitTrajectories`, `trackingStationStateVectorCachedIndices`,
`activeReFlyDeferredStateVectorGhostSessions`, `flightPendingMapVessels`,
`flightSoiGapStateVectorExpectedBodies`, `flightStateVectorCachedIndices`,
`flightStateVectorOrbitTrajectories`, `flightLastMapOrbitByIndex`, plus the
`(recIdx, cycle)`-keyed `overlapInstanceVessels` and the value-side of the
`chainId -> index` map `flightChainMapOwner`.

**Pid-keyed (13)**: `ghostMapVesselPids` (the O(1) guard set),
`ghostsWithSuppressedIcon`, `vesselsByChainPid`, `lastKnownByChainPid`,
`vesselPidToRecordingId`, `ghostOrbitBounds`, `ghostBodyFrameOrbitBounds`,
`ghostOrbitLoopShiftedPids`, `ghostOrbitEpochShift`, `ghostIconDrivePropagation`,
`ghostLastAppliedOrbitBody`, `ghostLastAppliedOrbitElements`,
`ghostNoBoundsSuppressLastFrame`, `lastPolylineOwningRealTimePerPid`,
`ghostParkingConicLineHoldUntilUT`, `boundaryOverlapSecondaryPids`.

**Per-frame scratch sets (4)**, all cleared at the top of each frame:
`drawnRecordingIdsThisFrame`, `protoLessCoverageRecordingIdsThisFrame`,
`paintedRecordingIdsThisFrame`, `protoBearingRecordingIdScratch`.

**Versioned caches (4)**: `chainTipSegmentsCache` +
`chainTipSegmentsCacheVersion` + `chainTipSegmentsCacheSupersedeVersion`;
`cachedReFlySuppressionSearchTrees`; `CachedTrackingStationSuppressedIds` (property).

**Counters, timers, depth (10)**: `lifecycleCreatedThisTick` / `lifecycleDestroyedThisTick`
/ `lifecycleUpdatedThisTick`, `gapGlideInertialSeedCount` / `gapGlideBodyFixedFallbackCount`,
`seamComdRefreshCount` / `seamComdSteadyCount` / `seamComdMaxOffOrbitDegPostRefresh`,
`ghostTeardownDepth`, `ghostTargetRequestSequence`, `nextMapOrbitUpdateTime`,
`flightTerminalMapRetentionLoggedIds`.

**Test / injection seams (3)**: `CurrentUTNow` (a `Func<double>` clock),
`FindBodyByNameForTesting`, and the shared `ic` `CultureInfo`. `CurrentUTNow` is NOT a
test-only seam despite the shape: **52 production references across 17 files** call it
as the map-presence clock.

### A.5 The cluster table

Clusters were derived from the code: ordered name-prefix rules written after reading all
311 method names, then validated against the shared-state matrix and the call graph.
Every method matched exactly one rule (zero UNASSIGNED). "state" is the count of
distinct non-const, non-seam statics the cluster's methods mention in their stripped
bodies; "excl" is how many of those NO other cluster touches; "pureL" are lines whose
methods reference no live Unity/KSP type and no mutable static (direct), and
additionally call only transitively pure siblings (trans).

| cluster | meth | code lines | with docs | state | excl | direct-pure L | trans-pure L | ext refs | ext prod | ext files | `.CommittedRecordings` reads |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| SourceResolve | 31 | 1159 | 1365 | 0 | 0 | 1137 | 394 | 157 | 10 | 16 | 0 |
| OrbitApply | 21 | 1135 | 1344 | 16 | 5 | 115 | 51 | 100 | 32 | 19 | 4 |
| FlightMapPass | 11 | 1032 | 1107 | 9 | 2 | 33 | 25 | 9 | 7 | 5 | 6 |
| VesselLifecycle | 14 | 958 | 1056 | 25 | 1 | 0 | 0 | 70 | 61 | 18 | 1 |
| TsPass | 5 | 916 | 976 | 7 | 1 | 79 | 74 | 21 | 14 | 6 | 2 |
| Observability | 31 | 910 | 1022 | 6 | 0 | 774 | 426 | 46 | 2 | 5 | 0 |
| OverlapInstances | 22 | 907 | 1190 | 16 | 0 | 333 | 254 | 73 | 21 | 6 | 0 |
| StateVectorMath | 18 | 680 | 761 | 0 | 0 | 544 | 419 | 32 | 0 | 3 | 1 |
| ReFlySuppression | 14 | 662 | 857 | 2 | 1 | 546 | 546 | 67 | 1 | 4 | 0 |
| ProtoBuild | 14 | 585 | 670 | 2 | 0 | 169 | 62 | 20 | 9 | 7 | 0 |
| TsHostReflection | 16 | 512 | 589 | 4 | 0 | 58 | 58 | 30 | 7 | 5 | 0 |
| Targeting | 21 | 504 | 552 | 1 | 1 | 140 | 140 | 20 | 3 | 4 | 0 |
| TsSpawnHandoff | 11 | 332 | 354 | 0 | 0 | 332 | 130 | 20 | 3 | 3 | 1 |
| EndpointTail | 8 | 304 | 377 | 0 | 0 | 148 | 141 | 23 | 1 | 4 | 0 |
| TestSeam | 15 | 237 | 304 | 37 | 0 | 38 | 38 | 85 | 9 | 19 | 0 |
| Registry | 17 | 176 | 272 | 6 | 0 | 59 | 46 | 182 | 140 | 42 | 3 |
| MarkerAndCoverage | 18 | 176 | 397 | 8 | 5 | 65 | 33 | 102 | 47 | 14 | 0 |
| ChainTip | 9 | 167 | 269 | 5 | 3 | 102 | 102 | 16 | 2 | 5 | 2 |
| SuppressionFinders | 11 | 143 | 208 | 0 | 0 | 143 | 143 | 29 | 5 | 6 | 0 |
| IndexShift | 4 | 101 | 133 | 13 | 0 | 8 | 0 | 8 | 6 | 3 | 0 |
| **TOTAL** | **311** | **11596** | **13803** | | | **4823** | **3082** | **1110** | **380** | | |

(The 1,110 external method references plus 234 field references plus 146 nested-type
references and 52 `CurrentUTNow` calls make up the 1,542 total.)

What the cluster names mean:

- **SourceResolve** - the "which data source does this ghost's map presence come from?"
  decision. Dominated by the 601-line `ResolveMapPresenceGhostSource`
  (`GhostMapPresence.cs:5064`), the single largest method in the class, plus
  `HasOrbitData` (2 overloads), `ResolveTrackingStationGhostSource`,
  `GetTrackingStationGhostRemovalReason`, the frame classifiers and the coverage
  predicates.
- **OrbitApply** - writing an `OrbitSegment` onto a live ghost `Vessel` and tracking
  what was written: `ApplyOrbitToVessel` (150 lines), `UpdateGhostOrbitForRecording`
  (132), `UpdateGhostOrbitFromStateVectors` (394), the bounds getters, the icon-drive
  epoch shift, `EnsureGhostOrbitRenderers`.
- **FlightMapPass** - the flight-scene per-frame driver:
  `UpdateFlightMapGhostLifecycle` plus its three named sub-passes (`RunFlightMapDeferredCreatePass`
  209 / `RunFlightMapOrbitReseedPass` 327 / `RunFlightMapStateVectorUpdatePass` 202) and
  the two ghost-lifecycle handlers relocated here from `ParsekPlaybackPolicy`.
- **VesselLifecycle** - create / remove the ProtoVessel: `CreateGhostVessel`,
  `CreateGhostVesselFromSource`, `CreateGhostVesselFromStateVectors` (267),
  `RemoveGhostVessel` (93), `RemoveGhostVesselForRecording` (108),
  `RemoveAllGhostVessels`, the teardown-depth guard.
- **TsPass** - the Tracking Station driver: `UpdateTrackingStationGhostLifecycle` (196
  incl. overload) and `RefreshTrackingStationGhosts` (485, the second-largest method).
- **Observability** - decision-line builders, log formatters, visibility counters.
- **OverlapInstances** - the per-`(recording, cycle)` overlap ghost instances plus the
  overlap schedule predicates already carved into `.OverlapSchedule.cs`.
- **StateVectorMath** - resolving a world position / map point from a recorded
  `TrajectoryPoint` when no orbit is available, and the body-fixed-primary selection.
- **ReFlySuppression** - the "is this recording in the active Re-Fly's parent chain, so
  suppress its proto?" predicate family plus the search-tree cache.
- **ProtoBuild** - building and loading the ghost `ProtoVessel` node, the orbit seed.
- **TsHostReflection** - reflection into stock `SpaceTracking`: find and invoke
  `SetVessel` / the vessel-list refresh, restore selection, `ComputeFlyIndexDrift`.
- **Targeting** - `SetGhostMapNavigationTarget` and the target-verification / describe
  helpers.
- **TsSpawnHandoff** - the decision (and driver) for the Tracking Station terminal
  vessel spawn handoff.
- **EndpointTail** - the endpoint-tail override for terminal map presence.
- **TestSeam** - `ResetForTesting`, `ResetBetweenTestRuns` and the `*ForTesting` setters.
- **Registry** - the pid / index / id lookup surface: `IsGhostMapVessel`,
  `GetGhostVesselPidForRecording`, `FindRecordingIdByVesselPid`, ...
- **MarkerAndCoverage** - the Appendix-A marker-draw decision, the polyline-owning and
  parking-conic stamps, the S0 frame-coverage instrument.
- **ChainTip** - resolving which chain segment owns a ghost's map presence.
- **SuppressionFinders** - the suppressed / superseded id sets and the playback-toggle
  predicate, already carved into `.SuppressionFinders.cs`.
- **IndexShift** - the committed-list notification handlers.

---

## B. Per-cluster state footprint: which clusters are entangled with the ProtoVessel core

The measurement that matters for extraction cost is not "how many fields does it touch"
but "how many fields does it SHARE with another cluster", because a shared field is a
field that cannot move and must become a parameter or a property on the seam left
behind. `TestSeam` is excluded from the sharing count throughout (it touches 37 fields
by construction: it is the reset surface).

### B.1 Clusters with ZERO shared mutable state

These five touch no non-const, non-seam static of the class at all. They read only their
arguments and call siblings:

| cluster | code lines | shared state | exclusive state |
| --- | --- | --- | --- |
| SourceResolve | 1,159 | 0 | 0 |
| StateVectorMath | 680 | 0 | 0 |
| TsSpawnHandoff | 332 | 0 | 0 |
| EndpointTail | 304 | 0 | 0 |
| SuppressionFinders | 143 | 0 | 0 |

That is **2,618 lines of decision logic with no static-state coupling to the rest of the
class at all**. `SourceResolve` in particular - including the 601-line
`ResolveMapPresenceGhostSource` - takes `IPlaybackTrajectory`, primitives, `ref`/`out`
parameters and returns an enum. It does not touch a single field.

### B.2 Clusters with a narrow footprint (1 to 3 fields, mostly exclusive)

| cluster | lines | exclusive state | shared state (with whom) |
| --- | --- | --- | --- |
| Targeting | 504 | `ghostTargetRequestSequence` | none |
| ReFlySuppression | 662 | `cachedReFlySuppressionSearchTrees` | `activeReFlyDeferredStateVectorGhostSessions` (IndexShift, VesselLifecycle) |
| ChainTip | 167 | `chainTipSegmentsCache` + its 2 version ints | `flightChainMapOwner`, `flightLastMapOrbitByIndex` (FlightMapPass, IndexShift) |
| ProtoBuild | 585 | none | `ghostMapVesselPids`, `ghostsWithSuppressedIcon` |
| TsHostReflection | 512 | none | `ghostMapVesselPids`, `lifecycleCreated/DestroyedThisTick`, `vesselsByRecordingIndex` |
| Observability | 910 | none | 6 read-only counters / maps |
| MarkerAndCoverage | 176 | the 4 per-frame scratch sets + `lastPolylineOwningRealTimePerPid` | `ghostParkingConicLineHoldUntilUT` (VesselLifecycle), `ghostsWithSuppressedIcon`, `vesselPidToRecordingId` |

### B.3 The entangled core

| cluster | lines | shared state count | reads |
| --- | --- | --- | --- |
| VesselLifecycle | 958 | **24** shared, 1 exclusive | every pid-keyed map, every index-keyed map, the counters |
| OverlapInstances | 907 | **16** shared, 0 exclusive | the same pid-keyed maps VesselLifecycle owns, plus `overlapInstanceVessels` |
| OrbitApply | 1,135 | 11 shared, 5 exclusive | the orbit-bounds / applied-element maps, `vesselsByChainPid`, `vesselsByRecordingIndex` |
| IndexShift | 101 | **13** shared, 0 exclusive | every index-keyed store, by definition |
| FlightMapPass | 1,032 | 7 shared, 2 exclusive | the six flight-scene index-keyed dicts |
| TsPass | 916 | 6 shared, 1 exclusive | the two TS index-keyed dicts + the pid maps |
| Registry | 176 | 6 shared, 0 exclusive | the lookup maps, read-only |

`VesselLifecycle` is the hub: it is the ONLY cluster that writes essentially every
pid-keyed store, and `OverlapInstances` is a second writer of the same stores for the
overlap population. Those two plus `OrbitApply`, `IndexShift`, `Registry` and the two
per-frame passes are one mutable-state component; **4,415 lines** that cannot be pulled
apart without first moving the state itself into a type both halves hold.

### B.4 Cross-cluster call edges worth naming

- `SourceResolve` is DIRECT-pure at 1,137 of 1,159 lines but only TRANSITIVELY pure at
  394, because `ResolveMapPresenceGhostSource` calls into `Observability`
  (`EmitSourceResolveLine`, `BuildGhostSourceStructuredDetail`,
  `FormatOrbitalCheckpointStateVectorFallbackDecision`, `CombineSourceDetails`). The
  impurity is logging, not decision logic.
- `Observability` is direct-pure at 774 of 910 lines; the impure remainder reads live
  vessel / scene state to build a counter line (`CollectMapVisibilityCounters`,
  `CountMapVisibility`, `GetCurrentSceneName`, `BuildGhostProtoVesselVisibilityState`).
- `VesselLifecycle` has **zero** pure lines. It is the live-KSP boundary.

---

## C. External callers per cluster

`grep GhostMapPresence\.<member>` across `Source/Parsek` and `Source/Parsek.Tests`, with
comments and string literals stripped first (`archview.strip_comments_and_strings`), and
the five partial files themselves excluded. **1,542 references in 101 files** (63
production, 38 test). Zero referenced names failed to resolve to a parsed member, so the
member inventory is complete.

### C.1 Production surface, by cluster

| cluster | prod refs | prod files | the callers |
| --- | --- | --- | --- |
| Registry | 140 | 30 | `ParsekFlight.cs` (18), `GhostTrackingStationPatch.cs` (14), `VesselSpawner.cs` (9), `GhostVesselLoadPatch.cs` (8), + 26 more |
| VesselLifecycle | 61 | 18 | `RuntimeTests.cs` (in-game, 7), 15 in-game render tests, `ParsekTrackingStation.cs`, `ParsekFlight.cs` |
| MarkerAndCoverage | 47 | 8 | `GhostOrbitLinePatch.cs` (17), `MapRenderProbe.cs` (8), `GhostTrajectoryPolylineRenderer.cs` (5), `ParsekTrackingStation.cs` (4), `ParsekUI.cs` (3), `RenderCompositionRecorder.cs` (2) |
| OrbitApply | 32 | 11 | `GhostOrbitLinePatch.cs` (15), `MapRenderProbe.cs` (4), in-game parity tests |
| OverlapInstances | 21 | 4 | `ParsekTrackingStation.cs` (3), `ParsekUI.cs` (3), `GhostTrajectoryPolylineRenderer.cs` (1), `ExtendedRuntimeTests.cs` (14) |
| TsPass | 14 | 4 | `TrackingStationScene.cs`, `ParsekTrackingStation.cs`, `GhostTrackingStationPatch.cs`, `RuntimeTests.cs` |
| SourceResolve | **10** | 5 | `ParsekPlaybackPolicy.cs` (6), `ParsekTrackingStation.cs` (1), `GhostTrackingStationPatch.cs` (1), `RuntimeTests.cs` (2) |
| ProtoBuild | 9 | 6 | `GhostVisualBuilder.cs`, `ParsekScenario.cs`, `ParsekTrackingStation.cs`, `ParsekUI.cs`, 2 in-game tests |
| TsHostReflection | 7 | 3 | `ParsekTrackingStation.cs` (2), `GhostTrackingStationPatch.cs` (1), `RuntimeTests.cs` (4) |
| FlightMapPass | 7 | 3 | `MapViewScene.cs` (1), `ParsekPlaybackPolicy.cs` (5), one in-game canary |
| IndexShift | 6 | 2 | `ParsekFlight.cs` (3), `ParsekTrackingStation.cs` (3) |
| SuppressionFinders | 5 | 3 | `ParsekTrackingStation.cs` (2), `GhostTrajectoryPolylineRenderer.cs` (2), `ParsekUI.cs` (1) |
| Targeting | 3 | 3 | `ParsekTrackingStation.cs`, `GhostVesselLoadPatch.cs`, `RuntimeTests.cs` |
| TsSpawnHandoff | 3 | 2 | `GhostTrackingStationPatch.cs` (2), `ParsekTrackingStation.cs` (1) |
| ChainTip | 2 | 2 | `GhostTrajectoryPolylineRenderer.cs`, `ShadowRenderDriver.cs` |
| Observability | 2 | 1 | `ParsekPlaybackPolicy.cs` |
| EndpointTail | 1 | 1 | `ParsekPlaybackPolicy.cs` |
| ReFlySuppression | **1** | 1 | `RewindInvoker.cs` |
| StateVectorMath | **0** | 0 | none |

### C.2 The concentration

- **`IsGhostMapVessel` alone is 115 of the 1,110 method references** (96 production
  across 29 files). It is a 4-line `ghostMapVesselPids.Contains(pid)` and is the
  CLAUDE.md-mandated O(1) guard for every `FlightGlobals.Vessels` iteration and vessel
  `GameEvent` handler in the mod.
- **234 external references reach FIELDS directly**, not methods: `CurrentUTNow` (52
  prod / 4 test, 17 files), `ghostMapVesselPids` (25 prod / 5 test, 13 files),
  `ghostsWithSuppressedIcon` (16 prod, `GhostOrbitLinePatch.cs` + `RuntimeTests.cs`),
  `ghostNoBoundsSuppressLastFrame` (3 prod / 9 test), plus 20 reason-string consts read
  by tests. Every one of those is a call site an extraction has to preserve or update.
- Only **five files** are responsible for the bulk of the production surface:
  `ParsekFlight.cs` (26), `ParsekTrackingStation.cs` (33),
  `Patches/GhostOrbitLinePatch.cs` (59), `Patches/GhostTrackingStationPatch.cs` (28),
  `ParsekPlaybackPolicy.cs` (20). Four of those five are the top co-change partners, so
  the reference map and the history agree on the same seams.

### C.3 `internal static` pure functions already unit-tested directly

The cheapest moves are members already exercised as pure functions by xUnit with no
live-KSP setup. Sorted by how much moves per call-site change:

| member | cluster | lines | prod refs | test refs | test file |
| --- | --- | --- | --- | --- | --- |
| `ResolveStateVectorWorldPositionPure` | StateVectorMath | 123 | 0 | 19 | `StateVectorWorldFrameTests.cs` |
| `ShouldSuppressStateVectorProtoVesselForActiveReFly` | ReFlySuppression | 167 | 0 | 26 | `Bug587ThirdFacetDoubledGhostMapTests.cs` |
| `IsRecordingInParentChainOfActiveReFly` | ReFlySuppression | 185+13 | 1 | 23 | `Bug614GhostMapAncestorChainTests.cs` |
| `ResolveMapPresenceGhostSource` | SourceResolve | 601 | 3 | 18 | `GhostMapPresenceTests.cs` |
| `ShouldCreateTrackingStationGhost` | SourceResolve | 11 | **0** | 31 | `GhostMapPresenceTests.cs` |
| `ShouldSpawnAtTrackingStationEnd` (4 overloads) | TsSpawnHandoff | 85 | 1 | 9 | `TrackingStationSpawnTests.cs` |
| `IsTerminalOrbitSynthesisSafeForLoopMember` | EndpointTail | 70 | 0 | 10 | `TerminalOrbitLoopSynthesisTests.cs` |
| `EvaluateOrbitalCheckpointStateVectorFallback` | StateVectorMath | 72 | 0 | via `GhostMapSoiGapStateVectorTests.cs` | |
| `ComputeFlyIndexDrift` | TsHostReflection | 33 | 0 | 10 | `FlyIndexDriftTests.cs` |
| `ResolveMarkerDrawDecision` | MarkerAndCoverage | 5 | 0 | 6 | `MarkerDrawDecisionTests.cs`, `ShadowRenderDriverTests.cs` |
| `IsMapPresenceHiddenByPlaybackToggle` | SuppressionFinders | 4 | 2 | 7 | `PlaybackTogglePresenceScopeTests.cs` |

---

## D. Purity scan, and the carve-out pattern the owner already accepts

### D.1 The pure pool

A method is **direct-pure** when its stripped body mentions none of ~60 live Unity/KSP
type tokens (`Vessel`, `ProtoVessel`, `FlightGlobals`, `MapView`, `PlanetariumCamera`,
`OrbitDriver`, `Orbit`, `CelestialBody`, `GameObject`, `Transform`, `Time`,
`GameEvents`, `HighLogic`, `Planetarium`, `SpaceTracking`, `ConfigNode`, the reflection
types, ...) and no mutable static of the class. **Transitively pure** additionally
requires every sibling it calls to be transitively pure (fixed point over the
intra-class call graph). `Vector3d` / `Vector3` / `Quaternion` are counted separately:
they are UnityEngine value types the headless xUnit host already constructs, so a member
using only those is still unit-testable.

- **164 of 311 methods, 4,823 of 11,596 code lines (42 percent) are direct-pure.**
- **134 methods, 3,082 lines (27 percent) are transitively pure.**
- Only **two** transitively pure methods touch a Unity value type at all:
  `ResolveStateVectorWorldPositionPure` (`Vector3d`, `Quaternion`) and its callee chain.
  The rest is `double`, `string`, `Recording`, `TrackSection`, `OrbitSegment`,
  `IPlaybackTrajectory` and enums.

The largest transitively pure bodies, all of which could sit in a pure helper type with
no seam at all:

| lines | cluster | member |
| --- | --- | --- |
| 185 | ReFlySuppression | `IsRecordingInParentChainOfActiveReFly` |
| 167 | ReFlySuppression | `ShouldSuppressStateVectorProtoVesselForActiveReFly` |
| 123 | StateVectorMath | `ResolveStateVectorWorldPositionPure` |
| 74 | TsPass | `BuildStartupTrackingStationLoopUnits` |
| 72 | StateVectorMath | `EvaluateOrbitalCheckpointStateVectorFallback` |
| 70 | EndpointTail | `IsTerminalOrbitSynthesisSafeForLoopMember` |
| 64 | ReFlySuppression | `TryFindActiveReFlyRecordingInSearchTrees` |
| 62 | ProtoBuild | `TryResolveTerminalOrbitGhostSeed` |
| 60 | Targeting | `LogGhostTargetVerificationOutcome` |
| 59 | Observability | `BuildEndpointTailBypassDetail` |

And the biggest single fact: **`ResolveMapPresenceGhostSource`, 601 lines, is
direct-pure.** It is not transitively pure only because it calls four logging helpers.
Its signature is `(IPlaybackTrajectory, bool, bool, double, bool, string, ref int, out
OrbitSegment, out TrajectoryPoint, out string, int)` -> `TrackingStationGhostSource`.
Nothing in it reaches the scene or the class's state.

### D.2 How the four existing partials were carved out

All four were created in one week, June 24-26 2026, by four commits with an identical
message shape:

```
1c23095a4 2026-06-24 refactor(ghostmap): split observability decision-line/lifecycle formatters into GhostMapPresence.Observability.cs
242aff586 2026-06-25 refactor(ghostmap): split overlap-schedule predicates into GhostMapPresence.OverlapSchedule.cs
fd9f08bb5 2026-06-25 refactor(ghostmap): split Re-Fly suppression cache into GhostMapPresence.ReFlySuppressionCache.cs
0f5a926e0 2026-06-26 refactor(ghostmap): split suppression finder/Add helpers into GhostMapPresence.SuppressionFinders.cs
```

Every one is a **same-class, new-file** move. `internal static partial class
GhostMapPresence` is reopened in the new file; the members keep their names and their
`internal static` modifiers; **no call site anywhere changed**, because the type did not
change. Three of the four also carried their private state with them
(`cachedReFlySuppressionSearchTrees` moved into `.ReFlySuppressionCache.cs`; the
observability counters stayed in the main file), and each picked a slice with a narrow
or zero footprint: `.SuppressionFinders.cs` touches no mutable static at all,
`.OverlapSchedule.cs` touches none either, `.ReFlySuppressionCache.cs` touches one.

The four have been nearly untouched since: 2, 2, 1 and 2 commits total, versus 271 for
the main file. **The carve-out works.** What it does NOT do is reduce the type's fan-in,
its knot membership or its co-change, because the class is still one class.

So there are two tiers of candidate, and the report ranks them together:

- **Tier 1 - new partial FILE.** Zero call-site change, zero risk of an API break. The
  only things that can red are file-path-keyed gates (section E.2) and the ERS allowlist
  (section E.3). This is the pattern above.
- **Tier 2 - new TYPE.** Reduces fan-in and can leave the knot. Costs a call-site sweep
  and, for anything holding state, a decision about where the state lives.

---

## E. Test coverage shape, seams and gates

### E.1 Which tests exercise which cluster

38 test files reference the class, 1,542 references total. The heaviest:

| test file | refs | clusters (top) |
| --- | --- | --- |
| `GhostMapPresenceTests.cs` | 152 | SourceResolve 98, Registry 23, SuppressionFinders 16, MarkerAndCoverage 12 |
| `GhostMapObservabilityTests.cs` | 71 | Observability 30, StateVectorMath 12, ChainTip 8, OrbitApply 8 |
| `GhostOrbitIconDriveTests.cs` | 71 | OrbitApply 24, field access 22, MarkerAndCoverage 10 |
| `OverlapPerInstanceTests.cs` | 56 | OverlapInstances 47, TestSeam 6 |
| `TrackingStationSpawnTests.cs` | 55 | TsSpawnHandoff 17, Targeting 17, Registry 9 |
| `Bug587ThirdFacetDoubledGhostMapTests.cs` | 50 | ReFlySuppression 48 |
| `MapRenderS0CoverageTests.cs` | 46 | MarkerAndCoverage 27, TestSeam 19 |
| `GhostMapSoiGapStateVectorTests.cs` | 43 | field access 17, nested type 15, SourceResolve 8 |
| `StateVectorWorldFrameTests.cs` | 19 | StateVectorMath 17 |
| `OptimizationPassInvalidationTests.cs` | 21 | field access 18, IndexShift 2 |

The mapping is clean: most clusters have a dedicated test file, which means a Tier-2
extraction can move a whole test file with the code.

### E.2 Reflection into private state (breaks on any move of the field)

`Source/Parsek.Tests/GhostMapPresenceTests.cs:315-318` reads two private statics through
reflection:

```csharp
var stateVectorTrajectories = (Dictionary<int, IPlaybackTrajectory>)typeof(GhostMapPresence)
    .GetField("flightStateVectorOrbitTrajectories", ...)
var stateVectorCachedIndices = (Dictionary<int, int>)typeof(GhostMapPresence)
    .GetField("flightStateVectorCachedIndices", ...)
```

Both are FlightMapPass / IndexShift index-keyed stores. Moving either field to a new
TYPE reds these two assertions with a runtime null, not a compile error. A Tier-1 move
to a new partial FILE does not break them (the field stays on the same type).

This is the only reflection access; there is no `AccessTools` / `nameof(GhostMapPresence)`
/ string-keyed lookup anywhere in `Source`.

### E.3 Gates and allowlists keyed on the FILE PATH

These five things name `GhostMapPresence.cs` by path. Each one must be handled by name
in any extraction that moves code out of that file.

1. **`scripts/ers-els-audit-allowlist.txt:55`** - `Source/Parsek/GhostMapPresence.cs` is
   an exact-file entry (the matcher at `scripts/grep-audit-ers-els.ps1:49` treats only
   trailing-`/` entries as prefixes). The gate runs from
   `GrepAuditTests.GrepAudit_*` with a managed fallback on Linux, and fails the suite on
   any unapproved `.CommittedRecordings` / `Ledger.Actions` read under `Source/Parsek`.
   `GhostMapPresence.cs` has **22 gate-pattern (`\.CommittedRecordings\b`) hits and 0
   `Ledger.Actions` hits**; 20 of the 22 are inside method bodies (the other 2 are doc
   comments, which the gate still counts because it greps raw text), distributed as:
   FlightMapPass 6, OrbitApply 4, Registry 3, ChainTip 2, TsPass 2, StateVectorMath 1,
   VesselLifecycle 1, TsSpawnHandoff 1 (full site list in `ers.txt`). **The four existing
   partials carry zero**, which is why none of them
   needed an allowlist line. Any new file that carries one of those 20 sites needs a new
   allowlist entry plus a `[ERS-exempt]` file comment and a one-line rationale, per
   CLAUDE.md.
2. **`Source/Parsek.Tests/MapRender/MapPresenceSeamTests.cs`** - four cells read
   `Source/Parsek/GhostMapPresence.cs` as text and assert declarations are present:
   `internal static void UpdateFlightMapGhostLifecycle(`,
   `internal static void HandleFlightGhostCreatedMapPresence(`,
   `internal static void HandleFlightGhostDestroyedMapPresence(`,
   `private static void RunFlightMapDeferredCreatePass(`,
   `private static void RunFlightMapOrbitReseedPass(`,
   `private static void RunFlightMapStateVectorUpdatePass(`; plus
   `UpdateFlightMapGhostLifecycle_DoesNotGateOnDirectorDrive`, which bounds the method
   body BETWEEN its own signature and `private static void PruneTerminalMapRetentionLogKeys(`
   and asserts three Director predicates are absent. **Moving the FlightMapPass cluster
   out of `GhostMapPresence.cs` reds all of these, and reordering
   `PruneTerminalMapRetentionLogKeys` away from directly after the orchestrator reds the
   body-bounding cell even without a move.**
3. **`Source/Parsek.Tests/MapRender/ShadowRenderDriverTests.cs:601`** -
   `ReadGhostMapPresenceSource()` -> `ReadParsekSource("GhostMapPresence.cs")`, asserting
   the marker site assigns its disjunct from
   `Parsek.MapRender.ShadowRenderDriver.IsTracedPathOwnedThisFrame(`. Moving
   MarkerAndCoverage out of `GhostMapPresence.cs` reds this.
4. **`Source/Parsek.Tests/MapRenderTracerCoverageTests.cs:225`** -
   `GhostMapPresence_EmitsGhostCreatedStructuralEvent_SourceGate` asserts
   `MapRenderTrace.EmitStructural(` and `"GhostCreated"` are present in
   `GhostMapPresence.cs`. The emit sits in VesselLifecycle. Moving the create funnel out
   reds this.
5. **`Source/Parsek.Tests/GhostOrbitLineCascadeDeleteGateTests.cs:54`** - an
   `[InlineData("GhostMapPresence.cs")]` NEGATIVE gate: seven deleted cascade symbols
   must not reappear. A move out does not red it, but it silently narrows the gate,
   because the new file is not in the `[InlineData]` list.

And one more, which is a forbidden-pattern gate rather than a presence gate:

6. **`scripts/grep-audit-non-loop-live-pid.ps1:34-35`** plus its managed fallback at
   `Source/Parsek.Tests/GrepAuditNonLoopLivePidTests.cs:84` forbid six symbols in
   `Source/Parsek/GhostMapPresence.cs`. A move out silently drops the moved code from
   the gate's scope.

### E.4 A latent defect found while reading gate 6

The pwsh gate and its managed fallback forbid **different symbol sets** for
`GhostMapPresence.cs`:

- `scripts/grep-audit-non-loop-live-pid.ps1:35` forbids
  `... |TryResolveActiveReFlyAbsoluteShadowPoint|...`
- `Source/Parsek.Tests/GrepAuditNonLoopLivePidTests.cs:84` forbids
  `... |TryResolveActiveReFlyBodyFixedPrimaryPoint|...`

The other five rows of the two lists are byte-identical; only the `GhostMapPresence.cs`
row differs. CLAUDE.md states each gate "probes PATH for `pwsh`/`pwsh.exe` and falls back
to an equivalent managed scan when pwsh is absent", so exactly one of the two arms runs on
any given machine, and the two arms do not forbid the same thing. Both currently pass:
`grep -rn TryResolveActiveReFlyAbsoluteShadowPoint --include=*.cs Source/` returns **0**
hits and `TryResolveActiveReFlyBodyFixedPrimaryPoint` returns **1** (its own mention
inside the gate). The drift dates to `ed15897db` (2026-05-12), which edited the managed
fallback after `a0e1e87b5` (2026-05-06) wrote the ps1. This is a pre-existing finding,
not an extraction risk; it is reported here because it names `GhostMapPresence.cs`.

---

## F. Change history

### F.1 File level

- **271 commits, all within 18 months** (`git log --since=18.months ... | wc -l` and the
  unbounded count both return 271). The file's first commit is `4a7af303b`, 2026-03-20;
  its last is `0f55a706c`, 2026-09-16. **271 commits in six months** against
  `ParsekFlight.cs`'s 1,134 in eighteen.
- Churn rank **8** of 486 tracked files.
- Top co-change partners: `ParsekPlaybackPolicy.cs` 50, `ParsekFlight.cs` 48,
  `ParsekTrackingStation.cs` 35, `Patches/GhostOrbitLinePatch.cs` 29,
  `Display/GhostTrajectoryPolylineRenderer.cs` 23. All five are among the fourteen
  heaviest production callers, so the history and the reference map name the same seams.

### F.2 Do recent commits cluster in particular regions?

`git blame --line-porcelain` over all 13,527 lines of `GhostMapPresence.cs`, each line
mapped to the cluster whose member span contains it, aged against 2026-09-22:

| cluster | lines | distinct blame commits | lines <= 90 days old | share | median line date |
| --- | --- | --- | --- | --- | --- |
| IndexShift | 129 | 2 | 121 | **94%** | 2026-09-01 |
| ChainTip | 260 | 13 | 103 | **40%** | 2026-09-05 |
| (field block) | 480 | 54 | 96 | 20% | 2026-06-06 |
| MarkerAndCoverage | 394 | 18 | 61 | 15% | 2026-06-06 |
| Registry | 238 | 19 | 22 | 9% | 2026-04-23 |
| SourceResolve | 1,335 | 60 | 81 | 6% | 2026-04-25 |
| TestSeam | 290 | 36 | 17 | 6% | 2026-05-10 |
| VesselLifecycle | 1,042 | 53 | 50 | 5% | 2026-04-25 |
| ProtoBuild | 656 | 29 | 27 | 4% | 2026-04-23 |
| FlightMapPass | 1,096 | 18 | 38 | 3% | 2026-06-05 |
| TsPass | 971 | 65 | 21 | 2% | 2026-05-26 |
| OverlapInstances | 930 | 22 | 18 | 2% | 2026-06-06 |
| Observability | 739 | 22 | 7 | 1% | 2026-04-25 |
| OrbitApply | 1,323 | 53 | 6 | 0% | 2026-05-28 |
| EndpointTail | 369 | 11 | 1 | 0% | 2026-05-28 |
| StateVectorMath | 744 | 23 | **0** | 0% | 2026-04-25 |
| ReFlySuppression | 734 | 14 | **0** | 0% | 2026-04-26 |
| TsHostReflection | 573 | 9 | **0** | 0% | 2026-05-07 |
| Targeting | 531 | 6 | **0** | 0% | 2026-04-25 |
| TsSpawnHandoff | 343 | 17 | **0** | 0% | 2026-04-23 |
| SuppressionFinders (in main file) | 20 | 5 | **0** | 0% | 2026-04-29 |
| NestedType | 298 | 17 | **0** | 0% | 2026-04-25 |

Two clean readings:

1. **Recent work is concentrated in a small set of clusters.** `IndexShift` (born
   2026-09-01 with the committed-list notification contract), `ChainTip` (2026-09-08),
   `MarkerAndCoverage` and the field block account for 381 of the 464 lines younger than
   90 days.
2. **Five whole clusters totalling 2,925 lines have not been touched in three months,
   and their median line is five months old**: `StateVectorMath`, `ReFlySuppression`,
   `TsHostReflection`, `Targeting`, `TsSpawnHandoff`. Measured against the opportunities
   doc's rule that "it is stable, so the change is cheap and the regression surface is
   small" (the argument for item 2, ParsekLog), these five are the cheap moves.

Note `TsPass` has the most distinct blame commits (65) for 971 lines: it is edited
constantly in small slices without the lines aging out, the signature of an orchestrator
everyone reaches into.

---

## G. Ranked extraction candidates

Ranked by (payoff / risk), with the two tiers interleaved. The suggested order runs top
to bottom; each is independently landable.

### G.1 The candidate table

| # | new type / file | tier | what moves | lines | state that moves | external call sites changed | tests + gates that move or red | risk | payoff |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `GhostMapPresence.StateVectorResolution.cs` | 1 (file) | StateVectorMath, 18 methods | 680 (761 w/ docs) | none | **0** | `StateVectorWorldFrameTests.cs` unchanged; needs 1 ERS allowlist line ONLY if `TryFindRecordingByIdForBodyFixedAnchorPid` moves with it | **Low** | removes the single biggest zero-coupling slice; 419 transitively pure lines become locatable |
| 2 | `GhostMapPresence.ReFlySuppression.cs` (merge with the existing cache partial) | 1 (file) | ReFlySuppression, 14 methods | 662 (857) | `cachedReFlySuppressionSearchTrees` already in the partial | **0** | 3 dedicated test files unchanged; no gate names it | **Low** | 546 of 662 lines transitively pure; untouched since 2026-05-11 |
| 3 | `GhostMapPresence.TrackingStationHost.cs` | 1 (file) | TsHostReflection + TsSpawnHandoff, 27 methods | 844 (943) | none | **0** | `FlyIndexDriftTests.cs`, `TrackingStationSpawnTests.cs`, `GhostTrackingStationPatchTests.cs` unchanged; 1 ERS line for the one `ShouldSpawnAtTrackingStationEnd` read | **Low** | isolates every stock-`SpaceTracking` reflection probe in one file; zero recent churn |
| 4 | `GhostMapTargeting` | **2 (type)** | Targeting, 21 methods | 504 (552) | `ghostTargetRequestSequence` moves | **3 prod** (`ParsekTrackingStation.cs`, `GhostVesselLoadPatch.cs`, `RuntimeTests.cs`) + 17 in `TrackingStationSpawnTests.cs` | test file moves wholesale; no gate names it | **Low-Medium** | first real fan-in reduction; 140 pure lines; zero recent churn; the seam is 6 members |
| 5 | `MapPresenceSourceResolver` | **2 (type)** | SourceResolve + EndpointTail, 39 methods, and `TrackingStationGhostSource` | 1,463 (1,742) | none | **11 prod** across 5 files + 25 files naming the enum (keep a `using` alias or forward the enum) | `GhostMapPresenceTests.cs` (98 refs) moves; `MapPresenceSeamTests.cs` has 2 SourceResolve refs; no source-text gate | **Medium** | the biggest single win: **1,463 lines with ZERO static-state coupling**, including the 601-line direct-pure `ResolveMapPresenceGhostSource`; drops GhostMapPresence's fan-out by the largest block |
| 6 | `GhostMapPresence.FlightPass.cs` | 1 (file) | FlightMapPass, 11 methods | 1,032 (1,107) | its 6 index-keyed dicts stay on the class (IndexShift needs them) | **0** | **reds 4 cells in `MapPresenceSeamTests.cs`** (they read `GhostMapPresence.cs` by path); needs 1 ERS allowlist line (6 reads); `GhostMapPresenceTests.cs:315-318` reflection still works | **Medium** | halves the main file's per-frame surface; the gates are mechanical to repoint |
| 7 | `GhostMapPresence.TrackingStationPass.cs` | 1 (file) | TsPass, 5 methods (incl. the 485-line `RefreshTrackingStationGhosts`) | 916 (976) | none moves | **0** | needs 1 ERS allowlist line (2 reads); `MapPresenceSeamTests` pins `UpdateTrackingStationGhostLifecycle(` in **`TrackingStationScene.cs`**, not here, so it is safe | **Medium** | isolates the second-largest method; 65 blame commits say it is edited constantly |
| 8 | `GhostMapPresence.Overlap.cs` (merge with `.OverlapSchedule.cs`) | 1 (file) | OverlapInstances, 22 methods | 907 (1,190) | `overlapInstanceVessels`, `boundaryOverlapSecondaryPids` move; **16 pid-keyed fields stay shared** with VesselLifecycle | **0** | `OverlapPerInstanceTests.cs` (56 refs) unchanged; `GhostPlaybackLogHygieneTests.cs` comments name the lifecycle lines but do not read the file | **Medium-High** | biggest slice still in the main file after 1-7, but the 16 shared fields mean the two halves stay coupled by state |

Doing 1, 2, 3, 6, 7 and 8 as partial-file moves plus 4 and 5 as types would take
`GhostMapPresence.cs` from 13,527 lines to **5,901** (summing the blame-mapped
per-cluster line counts in section F.2 for the ten clusters involved: 7,626 lines
leave the file), and would remove **1,967 lines and 60 methods** from the TYPE entirely
(candidates 4 and 5). Both are arithmetic on the tables above and assume nothing is left
behind as a forwarder.

### G.2 Per-candidate seam notes

**#1 StateVectorResolution.** The seam is nothing: the whole cluster is already
`internal static` on the class and has zero production callers. The one snag is
`TryFindRecordingByIdForBodyFixedAnchorPid` (56 lines at `:6213`), whose body reads
`RecordingStore.CommittedRecordings`. Either leave it in the main file or add the new
path to `scripts/ers-els-audit-allowlist.txt` with a rationale; do not "fix" it by
routing through `EffectiveState.ComputeERS()`, because the allowlist comment for this
file says these are deliberate raw index-based readers.

**#2 ReFlySuppression.** `RewindInvoker.cs` makes the one production call
(`IsRecordingInParentChainOfActiveReFly`). The cluster's only coupling is
`activeReFlyDeferredStateVectorGhostSessions`, which is index-keyed and therefore
shifted by `ShiftIndexKeyedPresence`; it must stay on the class, so the two
`Mark/IsStateVectorGhostDeferredForActiveReFlySession` helpers either stay behind or
keep reading the class field (free in Tier 1).

**#3 TrackingStationHost.** Merging TsHostReflection and TsSpawnHandoff is a judgement
call: they share no state (both are zero-footprint) and both exist only to talk to stock
`SpaceTracking`, so one file reads better than two. Split them if the reviewer prefers.

**#4 GhostMapTargeting (type).** The six externally-called members are
`SetGhostMapNavigationTarget`, `TrySetReadyMapObjectTarget`, `TryProbeMapObjectName`,
`ShouldTransferTrackingStationMapFocus`, `ShouldTransferTrackingStationNavigationTarget`,
`IsTrackingStationMapFocusSceneActive`. `TrySetReadyMapObjectTarget` /
`TryProbeMapObjectName` already take `Func<string>` / `Action` delegates, so they are
seam-shaped already. `GhostTargetVerificationStatus` and
`LogGhostTargetVerificationForTesting` move with it.

**#5 MapPresenceSourceResolver (type).** The one real cost is
`TrackingStationGhostSource`: 25 files name it, 22 of them through the
`GhostMapPresence.` prefix. Either move the enum to the new type and sweep the 118
references, or hoist the enum to file scope in `Parsek` so both spellings keep working.
The other cost is that `ResolveMapPresenceGhostSource` calls four `Observability`
helpers; take them along (they are direct-pure builders) or pass a logging delegate. Do
NOT try to take `Observability` wholesale - `CollectMapVisibilityCounters` /
`CountMapVisibility` read live scene state.

**#6 FlightPass.** Four `MapPresenceSeamTests` cells hold the flight pass in place BY
FILE PATH. They are mechanical to repoint (change `ReadSource("Source","Parsek",
"GhostMapPresence.cs")` to the new file name), but every one must be named in the PR, and
`UpdateFlightMapGhostLifecycle_DoesNotGateOnDirectorDrive` also depends on
`PruneTerminalMapRetentionLogKeys` being the NEXT member declared, so the move must keep
the two adjacent or the gate needs a new end-needle.

**#7 TrackingStationPass.** Safe from the seam tests (their
`UpdateTrackingStationGhostLifecycle(` assertion is against
`Source/Parsek/MapRender/TrackingStationScene.cs`, the caller). The catch is the ERS
allowlist: 2 of the file's 20 in-body `.CommittedRecordings` reads are here, one in
`UpdateTrackingStationGhostLifecycle` (`:6467`) and one in
`CreateGhostVesselsFromCommittedRecordings` (`:9377`).

**#8 Overlap.** The 16 pid-keyed fields it shares with `VesselLifecycle` are the reason
this is last. As a Tier-1 file move it is fine; as a Tier-2 type it is not possible
without first extracting a `GhostMapVesselRegistry` that owns the pid-keyed stores, and
that is a separate design step (see G.4).

### G.3 Do NOT extract

Each of these is pinned by a contract in `docs/dev/design-map-ts-render-architecture.md`
Appendix A, `docs/dev/design-map-ts-render-tracer.md` Appendix A, or `.claude/CLAUDE.md`.
The citation is given so a future session does not re-litigate it.

1. **`ResolveMarkerDrawDecision` and `ShouldDrawNonProtoMarkerForGhost`** - do not move,
   do not split, do not change their signatures. Architecture Appendix A, "The playback
   tick box is a whole-presence gate": *"`ResolveMarkerDrawDecision` /
   `ShouldDrawNonProtoMarkerForGhost` are UNCHANGED, and must stay so: they take only a
   `uint ghostPid`, the decision must remain a SUPERSET of the line-hide, and a
   recording-keyed suppression belongs at an EARLIER gate rather than as a fourth
   disjunct flipped false."* `ShadowRenderDriverTests.MarkerSite_TracedPathDisjunct_ReadsSharedSelector_SourceGate`
   also reads the site out of `GhostMapPresence.cs` by path.
2. **The icon floor: `ghostsWithSuppressedIcon`, `IsIconSuppressed`.** CLAUDE.md and
   Appendix A both: *"a KEPT PERMANENT fallback - the ONLY marker signal for
   below-atmosphere descent, off-arc / window-clamp, and no-bounds (loiter / terminal /
   atmospheric) ghosts; do not delete them."* `Patches/GhostOrbitLinePatch.cs` writes the
   set directly (16 production references) and
   `GhostOrbitLineCascadeDeleteGateTests.SurvivingMechanisms_StayWired` asserts both
   `ghostsWithSuppressedIcon.Add(` and `.Remove(` are present in the patch. Moving the
   field changes those write sites.
3. **`FindTrackingStationSuppressedRecordingIds` and `IsMapPresenceHiddenByPlaybackToggle`
   must stay SEPARATE.** Appendix A: *"Do NOT fold the predicate into the `suppressedIds`
   set ... the same set gates the Tracking Station SPAWN HANDOFF, so folding it in would
   silently suppress the recording's CAREER effect."* Merging SuppressionFinders and
   TsSpawnHandoff into one "suppression" type would put both on one surface and invite
   exactly that fold. Keep them apart.
4. **`ghostMapVesselPids`.** CLAUDE.md: *"`ghostMapVesselPids` HashSet for O(1) guards -
   every `FlightGlobals.Vessels` iteration and vessel GameEvent handler must check
   `IsGhostMapVessel(pid)` first."* 25 production sites read the field directly and 96
   more call `IsGhostMapVessel`. Any change to where it lives is a 30-file sweep with a
   correctness risk out of all proportion to the 4-line method. Leave the field and the
   guard on `GhostMapPresence`.
5. **`ReindexPresenceAfterDelete` / `ReindexPresenceAfterInsert` /
   `HandleCommittedRecordingRemoving` / `ShiftIndexKeyedPresence`.** CLAUDE.md's
   committed-list index contract: *"A new producer must raise the seam (or route through
   the primitive), never add a private reindex copy."* `ShiftIndexKeyedPresence` is the
   single place that knows every index-keyed store; splitting the stores across types
   would force either a second reindex copy or a cross-type callback per store. It also
   holds 94 percent of the file's lines younger than 90 days, so it is the least settled
   region in the file.
6. **The `MapRenderTrace.EmitStructural("GhostCreated", ...)` call site in the create
   funnel.** Tracer Appendix A plus
   `MapRenderTracerCoverageTests.GhostMapPresence_EmitsGhostCreatedStructuralEvent_SourceGate`,
   whose comment states *"Losing this call site would silence ghost-lifecycle tracing
   while the matrix tests stay green."* If VesselLifecycle ever moves, the gate moves
   with it in the same commit.
7. **Anything that would make `MapRenderTrace` / `GhostRenderTrace` share a formatter.**
   Tracer Appendix A: *"Formatters are SELF-CONTAINED (duplicated from `GhostRenderTrace`;
   do NOT refactor a shared formatter out or touch `GhostRenderTrace.cs`)."* The
   Observability cluster looks like an obvious deduplication target against
   `GhostRenderTrace`; it is explicitly not one.
8. **The `RecordLineIntent` single-writer channel and the four `GhostOrbitLinePatch`
   stamp sites.** Tracer Appendix A: *"do not add stamp sites, derive coverage
   generically from logged bounds, or refactor a shared formatter"*. The
   `ghostParkingConicLineHoldUntilUT` / `StampPolylineOwning` pair that
   `MarkerAndCoverage` owns feeds those exemptions; a refactor that changes when the
   stamps are written changes `IsLineBlink`'s answer.

### G.4 The step that has to come before a real decomposition

Candidates 1-7 can all be done without touching the state. Candidate 8 and anything
beyond it cannot, because 4,415 lines across `VesselLifecycle`, `OverlapInstances`,
`OrbitApply`, `IndexShift`, `Registry`, `FlightMapPass` and `TsPass` write the same
pid-keyed and index-keyed dictionaries.

The prerequisite is a `GhostMapVesselRegistry` that owns the 13 pid-keyed stores and the
10+ index-keyed stores, exposes `IsGhostMapVessel` / the lookups / the index shift, and
is the one thing `VesselLifecycle` and `OverlapInstances` both write through. That is
design work of the same shape and size as item 4 of the opportunities doc
(`RecordingStore` as a list with notifications plus services on top), and it should be
ranked and planned separately rather than attempted inside an extraction PR.

---

## H. Dead, duplicated, and doc-vs-code drift

### H.1 Dead code (verified against the whole tree, not from comments)

Three members have zero intra-class callers and zero references anywhere in the
repository, counting tests, in-game tests, harness Python and docs
(`grep -rn <name> --include=*.cs --include=*.py --include=*.toml --include=*.md .`):

| member | file:line | lines | tree-wide hits |
| --- | --- | --- | --- |
| `GetOverlapInstanceCyclesForTesting` | GhostMapPresence.cs:12122 | 11 | **1** (its own declaration) |
| `BuildTrackingStationGhostSourceSummaryKey` (private) | :1970 | 9 | **2** (declaration + one doc mention) |
| `ResetGapGlideCountersForTesting` | :226 | 5 | **1** (its own declaration) |

25 lines total. Two are `*ForTesting` seams nothing calls; both have live siblings
(`OverlapCyclesForTesting` at `:12101`, and `ResetSeamComdCountersForTesting` at `:245`,
which IS called). Worth a chip, not a PR of its own.

### H.2 Stranded seams: internal members with tests but no production caller

Not dead, but nothing in the shipping product reaches them:

| member | lines | production refs | test refs |
| --- | --- | --- | --- |
| `ShouldCreateTrackingStationGhost` | 11 | **0** | 31 in `GhostMapPresenceTests.cs` |
| `ComputeGhostDisplayInfo` | 47 | **0** | 5 |
| `FindSupersededRecordingIds` | 12 | **0** | 7 |
| `TrackEndpointTailGhostBoundsForTesting` | 23 | 0 | 2 |
| `LogGhostTargetVerificationForTesting` | 19 | 0 | 3 |
| `GetGhostVessel` | 5 | 0 | 3 |

`ShouldCreateTrackingStationGhost` is the notable one: it wraps
`ResolveTrackingStationGhostSource` into a `(bool shouldCreate, string skipReason)` tuple
and is pinned by 31 test references, but the production path calls
`ResolveTrackingStationGhostSource` directly. It is a decision function kept alive by its
tests. Decide deliberately whether it is a retained contract or a leftover; do not delete
it on this evidence alone.

### H.3 Duplication

A normalized-body similarity sweep over all 232 methods of 8 lines or more found
**exactly one pair above 85 percent**: `IsInRelativeFrame` (:4119) and
`IsInAbsoluteFrame` (:4141), 0.90, which is the intended mirrored pair. The class is not
copy-paste bloated.

There is one exact functional duplicate that the similarity sweep missed because both
bodies are short:

- `GhostMapPresence.Observability.cs:252` `FormatVec3d(Vector3d v)` and
  `GhostMapPresence.cs:5937` `FormatWorldPosition(Vector3d value)` both return
  `string.Format(ic, "({0:F1},{1:F1},{2:F1})", x, y, z)`. Same output, two names, two
  files, both private. One should go - but note contract G.3.7 forbids hoisting a shared
  formatter OUT to `GhostRenderTrace`; collapsing two copies WITHIN `GhostMapPresence` is
  a different thing and is fine.

Against the sibling marker code in `ParsekTrackingStation.cs` and `ParsekUI.cs` there is
**no duplication to remove**: `AtmosphericMarkerSkipReason` /
`AtmosphericMarkerSummary` / `ClassifyAtmosphericMarkerSkip` are the TS-side draw
authority, and `MapMarkerSummary` / `DrawMapMarkers` the flight-map one; both route the
shared decision through `GhostMapPresence.ShouldDrawNonProtoMarkerForGhost(pid)`, which
is exactly the design in Appendix A ("Both marker call sites ... route ... through
`ShouldDrawNonProtoMarkerForGhost(pid)`"). The per-surface enums are deliberately
distinct, not copies.

### H.4 Doc-vs-code drift found while checking scope claims

`docs/dev/design-map-ts-render-tracer.md:373-374` states that
`GhostMapPresence.vesselPidToRecordingId` *"is written only inside `TrackRecordingGhostVessel`
(`GhostMapPresence.cs:9032, 9044`)"*. Re-derived from the full write set, the map is
written at **six** sites spread across five clusters:

| line | member | cluster |
| --- | --- | --- |
| 2311 | `CreateGhostVessel` | VesselLifecycle |
| 9768 | `RebindGhostRecordingId` | Registry |
| 9966 | `SetProtoBearingPidForTesting` | TestSeam |
| 10472, 10484 | `TrackRecordingGhostVessel` / `TrackRecordingGhostIdentityForTesting` | Registry |
| 11928 | `CreateOverlapInstanceVessel` | OverlapInstances |

Those six are the ASSIGNMENT sites. Re-grepped 2026-09-22 during the review of the size
view, the map is emptied at ten more: seven `vesselPidToRecordingId.Remove` calls and
three `.Clear` calls. An earlier draft of this paragraph said "plus six removal sites",
which mirrored the write count instead of counting them. The cited line numbers
9032 / 9044 now land in `ShouldPreserveIdentityForTrackingStationSpawn` territory, so
both the scope claim and its citation are stale. The doc's conclusion ("that reverse map
is currently INCOMPLETE") may still hold, but it needs re-deriving from the full
six-write / ten-clear set rather than from the one site it names. Treat the comment as a
hypothesis, per CLAUDE.md.

---

## Appendix A: what could not be verified

- **No build, no test run, no flight.** Every claim here is static: source text, git
  history and the architecture views. Whether a proposed move compiles, and whether the
  named gates actually red, is unverified by construction.
- **The cluster assignment is mine, not the code's.** There are no `#region`s. The rules
  are ordered name-prefix regexes validated against the shared-state matrix, and every
  method matched exactly one. A different reader would draw `SourceResolve` vs
  `EndpointTail` vs `ChainTip` differently; the line counts would move by a few hundred
  and the ranking would not.
- **The purity scan is a token scan, not a type check.** A method reached through an
  interface that a live type implements, or through a `Func<>` a caller binds to a live
  lookup, reads as pure. `CurrentUTNow` and `FindBodyByNameForTesting` were treated as
  seams for exactly that reason; there may be others I did not spot.
- **The reference scan is textual.** `GhostMapPresence\.<member>` after comment and
  string stripping. It cannot see a reference reached by reflection with a computed name,
  and the only reflection I found (`GhostMapPresenceTests.cs:315-318`) uses literal
  strings, so I believe the set is complete - but that is a belief about this codebase,
  not a proof.
- **"Payoff" in the candidate table is a judgement, not a measurement.** The line counts,
  call-site counts, state footprints and gate lists are measured; the ranking that weighs
  them is not.
- **The 5,901-line end state in G.1 is arithmetic** on the blame-mapped per-cluster line
  counts, and assumes every move lands cleanly with no code left behind as a forwarder
  and no `using` block duplicated into the new files. Call it an estimate, not a target.
- **I did not read every one of the 311 method bodies.** I read the members the
  candidates and the do-not-extract list depend on, the four partial files in full, and
  spot-checked the parser against six members. The rest is machine-read.

---

## Appendix B: commands and scripts

All scripts live in this session's scratchpad
(`.../77b92023-beb0-4fd9-9d06-817fdd3ea4b4/scratchpad/`). Nothing under the repo was
written. Regex-heavy Python was put in script files rather than heredocs, because a
doubled backslash collapses in a Git Bash heredoc.

```bash
# 0. regenerate the gitignored architecture views before reading them
python scripts/arch/archview.py --check > <scratch>/archcheck.txt

# A. structural parse: line-preserving stripper + brace-matching member walk
python <scratch>/gmp_parse.py            # -> gmp_members.json  (415 members)

# A/B. cluster assignment + per-cluster state footprint
python <scratch>/gmp_cluster.py          # -> gmp_clustered.json, cluster.txt, cluster_state.txt

# D. purity scan (direct + transitive fixed point over the intra-class call graph)
python <scratch>/gmp_purity.py           # -> gmp_pure.json, purity.txt

# C/E. external callers, bucketed by cluster (comments/strings stripped via archview)
python <scratch>/gmp_callers.py          # -> callers_raw.tsv, callers.txt

# F. blame-age histogram by cluster
git blame --line-porcelain -- Source/Parsek/GhostMapPresence.cs > <scratch>/blame.txt
python <scratch>/gmp_blame.py            # -> blame_by_cluster.txt

# F. churn
git log --since=18.months --format=%h --no-merges -- Source/Parsek/GhostMapPresence.cs | wc -l   # 271
git log --format='%ad %h %s' --date=short --no-merges -- Source/Parsek/GhostMapPresence.cs | tail -1

# E.3/E.4. gates
grep -rn "GhostMapPresence" scripts/ --include=*.ps1 --include=*.txt --include=*.py
grep -rln "GhostMapPresence\.cs" Source/Parsek.Tests Source/Parsek

# H.1. deadness, verified tree-wide
grep -rn "<name>" --include=*.cs --include=*.py --include=*.toml --include=*.md .

# H.3. near-duplicate body sweep (difflib over normalized stripped bodies, >=8 lines)
#      inline in dupes.txt
```

Derived data files in the scratchpad: `gmp_members.json`, `gmp_clustered.json`,
`gmp_pure.json`, `callers_raw.tsv`, and the human-readable reports `archcheck.txt`,
`cluster.txt`, `cluster_state.txt`, `cluster_table.txt`, `purity.txt`, `callers.txt`,
`fields_external.txt`, `tests_by_cluster.txt`, `candidate_detail.txt`, `ers.txt`,
`unused.txt`, `dupes.txt`, `history.txt`, `blame_by_cluster.txt`.
