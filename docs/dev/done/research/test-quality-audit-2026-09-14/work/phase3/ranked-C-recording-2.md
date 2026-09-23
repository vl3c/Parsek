# Phase 3 ranked coverage proposals - partition C-recording-2

Baseline SHA 4aedb0a. 51 candidates in, 51 kept, 0 already-covered, 0 invalid, 0 merged.
Every SUT guard was confirmed present at the cited line; no line corrections were needed.
Risk class is `recording` for all 51, so ranking is guard blast radius first (structural
or destructive tree / supersede mutations), then feasibility (direct > seam > generator >
in-game), then whether a Medium/High Phase 1 finding already names the gap.

## recording-tree (35 kept, showing 10)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | pri |
| --- | --- | --- | --- | --- | --- | --- |
| C-recording-tree-022-01 | Source/Parsek/RecordingOptimizer.cs:1849 | mirror-direction | direct | RecordingOptimizer.cs:1849 change headSection.bodyFixedFrames.Count < 2 to < 1 | M | 1 |
| C-recording-tree-023-01 | Source/Parsek/RecordingTreeSplitter.cs:785 | state-transition | direct | RecordingTreeSplitter.cs:785 delete the RollBackInMemory(scenario, freshSnapshot) call in the catch | L | 1 |
| C-recording-tree-030-01 | Source/Parsek/RecordingStore.cs:539 | boundary | direct | RecordingStore.cs:539 force firstMoving = 0 so the trim / orbit-drop / event-retime block never runs | M | 1 |
| C-recording-tree-022-02 | Source/Parsek/RecordingOptimizer.cs:1752 | boundary | direct | RecordingOptimizer.cs:1752 change cp.endUT <= splitUT to cp.endUT < splitUT | S | 2 |
| C-recording-tree-023-02 | Source/Parsek/RecordingTreeSplitter.cs:407 | negative | direct | RecordingTreeSplitter.cs:407 delete the Math.Abs(predecessor.EndUT - rewindUT) >= epsilon abort | M | 2 |
| C-recording-tree-026-01 | Source/Parsek/SurfacePosition.cs:73 | serializer-key | direct | SurfacePosition.cs:73 change hasRotationFields ? (bool?)true : false to plain false | S | 2 |
| C-recording-tree-026-02 | Source/Parsek/SurfacePosition.cs:45 | mirror-direction | direct | SurfacePosition.cs:45 write pos.rotationRecorded ?? false instead of pos.HasRecordedRotation | S | 2 |
| C-recording-tree-029-01 | Source/Parsek/EffectiveState.cs:2157 | negative | direct | EffectiveState.cs:2157 delete the bp.Type Breakup/JointBreak fence | M | 2 |
| C-recording-tree-029-02 | Source/Parsek/EffectiveState.cs:2135 | negative | direct | EffectiveState.cs:2135 delete the ParentAnchorRecordingId == rec.RecordingId equality | M | 2 |
| C-recording-tree-031-01 | Source/Parsek/RecordingStore.cs:3451 | boundary | direct | RecordingStore.cs:3451 delete the ++safety > cap break | M | 2 |

Below the per-cluster cap (full rows in ranked-C-recording-2.jsonl): C-recording-tree-040-01, C-recording-tree-040-02, C-recording-tree-046-01, C-recording-tree-046-02, C-recording-tree-047-01, C-recording-tree-050-01, C-recording-tree-017-03, C-recording-tree-018-01, C-recording-tree-018-02, C-recording-tree-020-01, C-recording-tree-020-02, C-recording-tree-025-01, C-recording-tree-025-02, C-recording-tree-025-03, C-recording-tree-025-04, C-recording-tree-025-05, C-recording-tree-034-03, C-recording-tree-035-01, C-recording-tree-035-02, C-recording-tree-044-01, C-recording-tree-052-01, C-recording-tree-053-01, C-recording-tree-042-02, C-recording-tree-048-01, C-recording-tree-050-02

## rewind-refly (11 kept, showing 10)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | pri |
| --- | --- | --- | --- | --- | --- | --- |
| C-rewind-refly-004-02 | Source/Parsek/RecordingStore.RewindSupersedeRollback.cs:99 | boundary | direct | RecordingStore.RewindSupersedeRollback.cs:99 delete the string.IsNullOrEmpty(rel.OldRecordingId) skip | S | 1 |
| C-rewind-refly-015-01 | Source/Parsek/RecordingStore.cs:4216 | mirror-direction | direct | RecordingStore.cs:4220 change (emptyParents // emptyChildren) to emptyChildren only | M | 1 |
| C-rewind-refly-019-01 | Source/Parsek/RewindInvoker.cs:2433 | state-transition | direct | RewindInvoker.cs:2433-2438 delete the PendingTree removal block | S | 1 |
| C-rewind-refly-003-01 | Source/Parsek/SupersedeCommit.cs:651 | negative | direct | SupersedeCommit.cs:651 delete the rec.ChainBranch == tip.ChainBranch conjunct | S | 2 |
| C-rewind-refly-003-02 | Source/Parsek/SupersedeCommit.cs:646 | negative | direct | SupersedeCommit.cs:648 delete the tip != null conjunct so a vanished TIP still carves out | S | 2 |
| C-rewind-refly-004-01 | Source/Parsek/RecordingStore.cs:5701 | negative | direct | RecordingStore.cs:5701 delete the owner == null early return | S | 2 |
| C-rewind-refly-005-01 | Source/Parsek/LoadTimeSweep.cs:442 | state-transition | direct | LoadTimeSweep.cs:442 make SweepOrphanPreReFlyAnchorSnapshots return 0 without clearing | M | 2 |
| C-rewind-refly-005-02 | Source/Parsek/LoadTimeSweep.cs:766 | negative | direct | LoadTimeSweep.cs:766 make restoredMissing also remove the retirement row | M | 2 |
| C-rewind-refly-001-01 | Source/Parsek/SupersedeCommit.cs:1606 | integration | direct | SupersedeCommit.cs:1606 delete the BranchPointType.Undock case from IsStructuralBranchPointType | S | 3 |
| C-rewind-refly-014-01 | Source/Parsek/ReFlyConclusionRoute.cs:117 | boundary | direct | ReFlyConclusionRoute.cs:117 delete the empty-RecordingId LeaveForSweep return | S | 3 |

Below the per-cluster cap (full rows in ranked-C-recording-2.jsonl): C-rewind-refly-018-01

## spawn-vessel (2 kept, showing 2)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | pri |
| --- | --- | --- | --- | --- | --- | --- |
| C-spawn-vessel-021-02 | Source/Parsek/VesselSpawner.cs:491 | state-transition | seam | VesselSpawner.cs:498-500 return before the write-back when resolved != raw | S | 4 |
| C-spawn-vessel-004-01 | Source/Parsek/FlightRecorder.cs:6983 | integration | in-game | FlightRecorder.cs:6983 change AdoptControllersIfEmpty(pendingStartControllers) to a no-op returning false | M | 5 |

## trajectory-orbit (2 kept, showing 2)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | pri |
| --- | --- | --- | --- | --- | --- | --- |
| C-trajectory-orbit-011-01 | Source/Parsek/AnchorDetector.cs:384 | negative | direct | AnchorDetector.cs:389 make the VesselPersistentId comparison return true unconditionally | S | 3 |
| C-trajectory-orbit-015-01 | Source/Parsek/FlightRecorder.cs:9334 | roundtrip | in-game | FlightRecorder.cs:9337 pass the vessel CoM instead of anchorPose.WorldPos to ComputeRelativeLocalOffset | L | 5 |

## wiring-gates (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | pri |
| --- | --- | --- | --- | --- | --- | --- |
| C-wiring-gates-003-01 | Source/Parsek/ParsekFlight.cs:4914 | integration | direct | ParsekFlight.cs undock/EVA split blocks: add a coalescer.OnSplitEvent call inside the Undock block | M | 4 |

## Seams and generators named by this partition

- C-recording-tree-053-01: the SUT is the generator itself: Source/Parsek.Tests/Generators/RecordingBuilder.cs
- C-recording-tree-042-02: extract the BuildPostChoice lambda body into internal static void RunPostChoice(GameScenes destination, Action<GameScenes> loadScene)
- C-recording-tree-048-01: extract internal static bool MatchesReFlyRecordingByPid(uint activePid, ReFlySessionMarker marker) from CrewReservationManager.cs:1168-1179
- C-rewind-refly-018-01: add internal static Func<string,string> ResolveQuicksaveAbsolutePathOverrideForTesting to RewindPointReaper, consumed at LoadTimeSweep.cs:313
- C-spawn-vessel-021-02: add internal static Func<string,string> LocalizedNameResolverForTesting beside VesselSpawner's BodyResolverForTesting hooks, consumed at VesselSpawner.cs:498

## In-game only (not reachable from Source/Parsek.Tests)

- C-recording-tree-050-02 (ParsekFlight): ContinuationIntegrity category, ExtendedRuntimeTests.cs:2058
- C-spawn-vessel-004-01 (FlightRecorder): FLIGHT InGameTest: StartRecording is an instance method needing a live Vessel
- C-trajectory-orbit-015-01 (FlightRecorder): FLIGHT InGameTest: ApplyRelativeOffsetForAnchorPose is a private instance method taking Vessel
