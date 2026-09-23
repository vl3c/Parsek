# Phase 3 ranked coverage proposals - partition B-recording-1

Baseline 4aedb0a. Input 52 candidates; kept 49, already-covered 1, invalid 1, merged 1.
Risk class is `recording` for every candidate in this partition, so ranking is feasibility first,
then whether a Medium/High T3 finding already names the gap, then consequence.

## Cluster: catchall (3 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-catchall-011-01 | Source/Parsek/RecordingOptimizer.cs:1685 | state-transition | direct | delete the OrbitSegmentCheckpointBridge.InvalidateSectionAnnotationsForOrdinalShift call at RecordingOptimizer.cs:1685 | M | 2 |
| C-catchall-021-01 | Source/Parsek/VesselSpawner.cs:3143 | negative | direct | delete 'if (sibling.ChainBranch != 0) continue;' at VesselSpawner.cs:3143 | S | 2 |
| C-catchall-029-01 | Source/Parsek/TrajectoryMath.Stats.cs:244 | boundary | direct | change 'kvp.Value > maxCount' to '>=' at TrajectoryMath.Stats.cs:244 | S | 4 |

## Cluster: harness-seam (1 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-harness-seam-015-01 | Source/Parsek/InGameTests/InGameTestSidecarReaper.cs:174 | integration | direct | delete '.pann' from InGameTestSidecarReaper.SidecarSuffixes | M | 3 |

## Cluster: ledger-career (1 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-ledger-career-036-01 | Source/Parsek/Recording.cs:683 | negative | direct | delete the 'if (!string.IsNullOrEmpty(RecordedVesselGuid)) return false;' no-overwrite guard at Recording.cs:687 | S | 2 |

## Cluster: legacy-bugfix (9 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-legacy-bugfix-005-01 | Source/Parsek/ParsekFlight.cs:16405 | boundary | direct | delete 'return TerminalState.Orbiting;' at ParsekFlight.cs:16406 (falls through to the SubOrbital default) | S | 1 |
| C-legacy-bugfix-010-01 | Source/Parsek/EffectiveState.cs:1065 | state-transition | direct | invert IsSlotEffectiveTipOpen's use at EffectiveState.cs:1064 so an Immutable tip is admitted | M | 1 |
| C-legacy-bugfix-002-01 | Source/Parsek/RecordingStore.cs:972 | negative | direct | make CommitTree commit only the tree root recording (drop the child loop) | S | 2 |
| C-legacy-bugfix-007-01 | Source/Parsek/FlightRecorder.cs:5459 | state-transition | direct | swap the divisor at FlightRecorder.cs:5464 to 'duration / frames.Count' (mirror: BackgroundRecorder.cs:7155) | M | 2 |
| C-legacy-bugfix-011-01 | Source/Parsek/EnvironmentDetector.cs:135 | negative | direct | drop the 'heightFromTerrain >= 0.0' conjunct at EnvironmentDetector.cs:136 | S | 2 |
| C-legacy-bugfix-015-01 | Source/Parsek/TimeJumpManager.cs:408 | roundtrip | direct | transpose lat and lon in the CreateTimeJumpEvent format arguments at TimeJumpManager.cs:415 | S | 2 |
| C-legacy-bugfix-010-02 | Source/Parsek/ParsekScenario.HydrationRepair.cs:369 | boundary | direct | replace 'return string.CompareOrdinal(a.RecordingId, b.RecordingId);' with 'return 0;' at HydrationRepair.cs:369 | S | 3 |
| C-legacy-bugfix-013-01 | Source/Parsek/CrashCoalescer.cs:257 | negative | direct | add 'lastEmittedSnapshots.Clear(); lastEmittedTrajectoryPoints.Clear();' to CrashCoalescer.Reset | S | 3 |
| C-legacy-bugfix-027-01 | Source/Parsek/VesselSpawner.cs:3000 | negative | seam | re-add the 'Missing && !reserved' clause to BuildDeadCrewSet (VesselSpawner.cs:2909-2920) | M | 3 |

## Cluster: logging (1 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-logging-004-02 | Source/Parsek/VesselSpawner.cs:3198 | integration | direct | add 'break;' after the first PART node in the LogSpawnContext crew loop (VesselSpawner.cs:3198) | S | 4 |

## Cluster: logistics-route (2 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-logistics-route-002-01 | Source/Parsek/Logistics/RouteAnalysisEngine.cs:1134 | negative | direct | delete 'if (originWindow.TransferKind != RouteConnectionKind.DockingPort) return false;' at RouteAnalysisEngine.cs:1134 | S | 2 |
| C-logistics-route-032-01 | Source/Parsek/RouteHarvestCapture.cs:160 | negative | direct | delete 'if (window == null) return;' at RouteHarvestCapture.cs:160 | S | 3 |

## Cluster: mission-groups (2 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-mission-groups-014-01 | Source/Parsek/UI/RecordingsTableUI.cs:6377 | integration | direct | replace 'int ri = sortedIndices[row];' with 'int ri = row;' at RecordingsTableUI.cs:6377 | S | 2 |
| C-mission-groups-004-01 | Source/Parsek/RecordingGroupStore.cs:811 | mirror-direction | direct | drop '+ toleranceSeconds' from the late bound at RecordingGroupStore.cs:819 | S | 3 |

## Cluster: recorder-events (16 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-recorder-events-023-01 | Source/Parsek/ParsekFlight.TerminalEvents.cs:326 | state-transition | direct | move 'rec.VesselDestroyed = true;' below the 'if (rec.TerminalStateValue == TerminalState.Destroyed) return false;' early return at ParsekFlight.TerminalEvents.cs:328 | S | 1 |
| C-recorder-events-024-01 | Source/Parsek/BackgroundRecorder.PartEventPolling.cs:92 | integration | direct | delete the CheckFairingState call from BackgroundRecorder.PollPartEvents | M | 1 |
| C-recorder-events-003-01 | Source/Parsek/FlightRecorder.cs:4832 | state-transition | direct | change 'bool movingNow = movingSignal // inferredMoving;' to 'bool movingNow = movingSignal;' at FlightRecorder.cs:4833 | S | 2 |
| C-recorder-events-004-01 | Source/Parsek/BackgroundRecorder.cs:6743 | mirror-direction | direct | invert the trailing ternary to 'source == AnchorCandidateSource.Ghost ? "live" : "recorded"' at BackgroundRecorder.cs:6743 | S | 2 |
| C-recorder-events-021-01 | Source/Parsek/FlightRecorder.cs:297 | state-transition | direct | delete 'HasPendingJointBreakCheck = false;' at FlightRecorder.cs:299 (the consume never clears) | S | 2 |
| C-recorder-events-003-03 | Source/Parsek/FlightRecorder.cs:2567 | boundary | direct | change 'normalizedHeat >= AnimateHeatMediumThreshold' to '>' at FlightRecorder.cs:2567 | S | 3 |
| C-recorder-events-006-01 | Source/Parsek/FlightRecorder.cs:10963 | state-transition | seam | drop thrustingJetpackParts.Clear() / evaThrustFrameCounts.Clear() from the extracted clear block | M | 3 |
| C-recorder-events-013-01 | Source/Parsek/BackgroundRecorder.cs:602 | state-transition | seam | make the continuation unconditional (drop the parentAlive gate) - the bug #285 shape | M | 3 |
| C-recorder-events-013-02 | Source/Parsek/BackgroundRecorder.cs:1262 | boundary | seam | change the TTL to 'branchUT * DebrisTTLSeconds' at BackgroundRecorder.cs:1262 | M | 3 |
| C-recorder-events-015-01 | Source/Parsek/RecordingTreeRecordCodec.cs:671 | negative | direct | drop the IsNullOrEmpty guard at RecordingTreeRecordCodec.cs:672 and assign 'loopAnchorBodyNameStr ?? string.Empty' | S | 3 |

## Cluster: recording-tree (14 kept)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
| --- | --- | --- | --- | --- | --- | --- |
| C-recording-tree-011-01 | Source/Parsek/ParsekScenario.HydrationRepair.cs:25 | negative | direct | delete the 'RecordingStore.PendingTree.Id != loadedTree.Id' disjunct at HydrationRepair.cs:25 | S | 1 |
| C-recording-tree-001-01 | Source/Parsek/RecordingOptimizer.cs:2133 | negative | direct | delete the VesselPersistentId mismatch conjunct at RecordingOptimizer.cs:2133-2135 | S | 2 |
| C-recording-tree-001-02 | Source/Parsek/RecordingOptimizer.cs:177 | mirror-direction | direct | drop the firstHalfDuration clause at RecordingOptimizer.cs:177 (keep only secondHalfDuration < 5.0) | S | 2 |
| C-recording-tree-005-01 | Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:1639 | mirror-direction | direct | delete the flat-Points else branch at IncompleteBallisticSceneExitFinalizer.cs:1639-1651 | M | 2 |
| C-recording-tree-006-01 | Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:859 | state-transition | direct | delete the Orbiting terminalOrbit assignment at IncompleteBallisticSceneExitFinalizer.cs:859-864 | M | 2 |
| C-recording-tree-008-01 | Source/Parsek/SessionMerger.cs:794 | boundary | direct | delete the 'endUT <= startUT' skip at SessionMerger.cs:794 | S | 2 |
| C-recording-tree-009-01 | Source/Parsek/SessionMerger.cs:1734 | negative | direct | drop the moduleIndex component from the dedup key at SessionMerger.cs:1734 | S | 2 |
| C-recording-tree-010-01 | Source/Parsek/FlightRecorder.cs:686 | negative | direct | replace the tail 'return false;' at FlightRecorder.cs:687 with 'return true;' | S | 2 |
| C-recording-tree-011-02 | Source/Parsek/ParsekScenario.HydrationRepair.cs:17 | mirror-direction | direct | delete the 'RecordingStore.PendingTree.Id == loadedTree.Id' conjunct at HydrationRepair.cs:17 | S | 2 |
| C-recording-tree-013-01 | Source/Parsek/TrajectorySidecarBinary.cs:118 | negative | direct | delete the magic byte comparison loop at TrajectorySidecarBinary.cs:114-121 (accept any long-enough file) | S | 2 |

## Not kept

| cand_id | status | why |
| --- | --- | --- |
| C-recorder-events-011-01 | invalid | With minInterval 0 and elapsed <= 0 the floor at TrajectoryMath.cs:59 does not fire and the speed gate returns TRUE, so the proposed Assert.False fails against current code. With any production minInterval > 0 the floor already blocks it. Refile as a T7 question (duplicate-UT sample admitted at minInterval 0), not a coverage cell. |
| C-recorder-events-014-02 | already-covered | FlightRecorderExtractedTests.EncodeEngineKey_BasicEncoding (100*256) and _WithModuleIndex (100*256+3) pin the exact shift width and mask, which subsumes the 255-vs-next-pid collision case. |

## Merges

- C-recorder-events-010-01 merged into C-legacy-bugfix-007-01: the sample-rate formula is
  copied verbatim into FlightRecorder.CloseCurrentTrackSection (:5464) and
  BackgroundRecorder.CloseBackgroundTrackSection (:7155); one paired cell pins both close paths.
