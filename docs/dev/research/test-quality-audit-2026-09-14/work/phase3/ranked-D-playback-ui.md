# Phase 3 ranked coverage proposals - partition D (playback / UI)

Baseline 4aedb0a. 83 candidates triaged: 79 kept, 0 already-covered, 2 invalid, 2 merged away
(C-ghost-playback-009-01 into C-recorder-events-008-01; C-legacy-bugfix-018-01 into C-legacy-bugfix-004-01).
Feasibility of kept: 60 direct, 15 seam, 3 generator, 1 in-game. Priority 1 highest.

## ghost-playback (14 kept, showing 10)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-ghost-playback-008-01 | GhostMapPresence.cs:9221 | negative | direct | GhostMapPresence.cs:4055 add TerminalState.Landed to IsTerminalStateEligibleForTerminalOrbitMapPresence -> red | S | 2 |
| C-ghost-playback-012-01 | GhostChainWalker.cs:537 | state-transition | direct | GhostChainWalker.cs:535 delete the chainVisited.Contains(tipVesselPid) break -> the test hangs/reds | M | 2 |
| C-ghost-playback-011-01 | WatchModeController.Diagnostics.cs:154 | boundary | direct | WatchModeController.Diagnostics.cs:164 compare ClassifyMapFocusRestore(...) to a literal other than "ready" -> red | S | 3 |
| C-ghost-playback-013-01 | GhostPlaybackLogic.cs:5483 | mirror-direction | seam | GhostPlaybackLogic.cs:5483 drop the leading minus on steeringHeadingRate -> red | S | 3 |
| C-ghost-playback-018-01 | Recording.cs:1398 | mirror-direction | direct | Recording.cs:1398 change IPlaybackTrajectory.LoopPlayback to => LoopPlayback (drop the !IsDebris mask) -> red | S | 3 |
| C-ghost-playback-014-01 | GhostMapPresence.cs:4407 | integration | seam | GhostMapPresence.cs EmitSourceResolveLine: delete fields.RecordingIndex = recordingIndex -> red | M | 3 |
| C-ghost-playback-003-01 | GhostPlaybackEngine.cs:6452 | integration | seam | GhostPlaybackEngine.cs:6462 drop result.engineParticleSystemCount += engineSystemsForGhost -> red | S | 4 |
| C-ghost-playback-015-01 | PlaybackTrace.cs:126 | state-transition | direct | PlaybackTrace.cs:129 delete traceStates.Clear() -> red | S | 4 |
| C-ghost-playback-017-01 | GhostExtender.cs:86 | negative | direct | GhostExtender.cs:86 drop the rec={0} field from the None fallthrough line -> red | S | 4 |
| C-ghost-playback-025-01 | ParsekFlight.cs:19754 | integration | direct | ParsekFlight.cs:19754 move ExitWatchModeBeforeTimelineGhostCleanup below engine.DestroyAllGhosts() -> red | S | 4 |

## recording-tree (12 kept, showing 10)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-recording-tree-034-02 | ParsekFlight.cs:17805 | negative | direct | ParsekFlight.cs:17805 drop the !visited.Contains(current.RecordingId) term -> the preferred-child walk never terminates | M | 2 |
| C-recording-tree-051-01 | GhostPlaybackLogic.cs:7898 | negative | direct | GhostPlaybackLogic.cs:7898 drop the chain.TipRecordingId == rec.RecordingId conjunct -> red | M | 2 |
| C-recording-tree-014-01 | UI/RecordingsTableUI.cs:5298 | boundary | direct | RecordingsTableUI.cs:5298 drop the dur > 0 guard (total += dur unconditionally) -> red | S | 3 |
| C-recording-tree-014-02 | UI/RecordingsTableUI.cs:1074 | state-transition | direct | RecordingsTableUI.cs:1074 delete lastResolvedWatchTargetByGroup?.Remove(stale[i]) -> red | S | 3 |
| C-recording-tree-015-01 | UI/RecordingsTableUI.cs:6164 | negative | direct | RecordingsTableUI.cs:6164-6165 drop the IsNaN / IsInfinity terms of the clamped flag -> red | S | 3 |
| C-recording-tree-027-01 | GhostVisualBuilder.Parsing.cs:220 | roundtrip | direct | GhostVisualBuilder.Parsing.cs:221 swap the arguments to Quaternion.AngleAxis(axis, angle order) -> red | S | 3 |
| C-recording-tree-049-01 | UI/RecordingsTableUI.cs:4919 | negative | direct | RecordingsTableUI.cs:4919 drop the && !rec.TerminalStateValue.HasValue term -> red | S | 3 |
| C-recording-tree-054-01 | MergeDialog.DialogText.cs:534 | integration | direct | MergeDialog.DialogText.cs:541-... read rec.ExplicitStartUT / ExplicitEndUT instead of rec.StartUT / rec.EndUT -> red (range 0 for point-carrying recordings) | S | 3 |
| C-recording-tree-015-02 | UI/RecordingsTableUI.cs:6174 | mirror-direction | direct | RecordingsTableUI.cs:6174-6175 delete the !preserveSecondResolution early return -> red | S | 4 |
| C-recording-tree-015-03 | UI/RecordingsTableUI.cs:6226 | boundary | direct | RecordingsTableUI.cs:6226-6231 delete the cap-only arm (fall through to the min-only string) -> red | S | 4 |

## recorder-events (11 kept, showing 10)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-recorder-events-008-01 | GhostMapPresence.cs:12959 | negative | seam | GhostMapPresence.cs:12956 delete the if (!inRelativeFrame) gate -> red | M | 2 |
| C-recorder-events-015-02 | WatchModeController.cs:3488 | state-transition | seam | WatchModeController.cs ResolveWatchPlaybackUT: hand the raw user period to ComputeOverlapCycleLoopUT instead of ComputeEffectiveLaunchCadence -> red | M | 2 |
| C-recorder-events-001-01 | GhostVisualBuilder.Parsing.cs:268 | boundary | direct | GhostVisualBuilder.Parsing.cs:268 delete the parentIdx == i self-parent skip -> red | S | 3 |
| C-recorder-events-001-02 | FlightRecorder.cs:2430 | boundary | direct | FlightRecorder.cs:2430 replace safeBlinkRate with blinkRate (drop the > 0f clamp) -> red | S | 3 |
| C-recorder-events-009-01 | GhostPlaybackEngine.cs:882 | boundary | direct | GhostPlaybackEngine.cs:883 drop the counters.chainShadowed > 0 term -> red | S | 3 |
| C-recorder-events-012-01 | ParsekFlight.cs:23370 | negative | direct | ParsekFlight.cs:23374 return (double?)double.MaxValue unconditionally -> red | S | 3 |
| C-recorder-events-012-02 | GhostPlaybackLogic.WarpLoopPolicy.cs:164 | boundary | direct | GhostPlaybackLogic.WarpLoopPolicy.cs:167 return isWatchProtectedRecording (drop && !isOrbitTailPlayback) -> red | S | 3 |
| C-recorder-events-003-02 | GhostVisualBuilder.cs:2142 | boundary | seam | GhostVisualBuilder.cs multi-instance selection: drop the heat-name preference and always take the first MODULE -> red | M | 3 |
| C-recorder-events-019-01 | GhostMapPresence.cs:4604 | negative | direct | GhostMapPresence.cs:4604 return true instead of the sameBody comparison -> red (cross-body terminal orbit synthesized as EndpointTail) | M | 3 |
| C-recorder-events-020-01 | GhostVisualBuilder.Variants.cs:236 | serializer-key | seam | GhostVisualBuilder.Variants.cs:243 stop reading the shader key -> red | M | 3 |

## spawn-vessel (2 kept, showing 2)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-spawn-vessel-020-01 | ParsekPlaybackPolicy.cs:230 | state-transition | direct | ParsekPlaybackPolicy.cs:233 also reset rec.VesselSpawned = false / pid = 0 on the abandon branch -> red (infinite respawn loop) | M | 2 |
| C-spawn-vessel-014-01 | VesselSpawner.cs:6560 | boundary | direct | VesselSpawner.cs:6560 change speed > orbitalSpeed * 0.9 to >= -> red | S | 3 |

## map-render (9 kept, showing 9)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-map-render-004-02 | MapRenderTrace.cs:402 | boundary | direct | MapRenderTrace.cs:402-403 move the tsSkip append above the outcome/ride fields -> red | S | 3 |
| C-map-render-005-01 | Rendering/SmoothingPipeline.cs:667 | boundary | direct | SmoothingPipeline.cs:667-669 delete the frames.Count < MinSamplesPerSection gate -> red (short section is fitted / Catmull-Rom-fit-failed Warn) | S | 3 |
| C-map-render-011-01 | Rendering/AnchorCandidateBuilder.cs:467 | negative | direct | AnchorCandidateBuilder.cs:467 delete the rec.LoopAnchorVesselId == 0u return -> red | S | 3 |
| C-map-render-014-01 | tests: MapRender/PhaseSpineParityTests.cs:224 | integration | direct | make the assembler spine emit Visible=false on every frame -> the sweep stays green today, reds with the sawVisible anchor | S | 3 |
| C-map-render-023-01 | ParsekFlight.cs:22163 | state-transition | direct | ParsekFlight.cs:22169 swap the splineApplied and suppressHermite guards -> red on the splineApplied=true + suppressHermite=true precedence row | S | 3 |
| C-map-render-004-01 | MapRenderTrace.cs:346 | boundary | direct | MapRenderTrace.cs:346 switch drop the FallbackNonAnchoredUseHead arm (falls to default) -> red | S | 4 |
| C-map-render-013-01 | MapMarkerRenderer.cs:233 | boundary | seam | MapMarkerRenderer.cs:35 set ClickPadding = ToggleClickPadding -> red on strict containment | S | 4 |
| C-map-render-022-01 | ParsekTrackingStation.cs:105 | boundary | direct | ParsekTrackingStation.cs:105 add an AtmosphericMarkerSkipReason member with no token arm -> red | S | 4 |
| C-map-render-012-01 | Rendering/ProductionAnchorWorldFrameResolver.cs:31 | negative | seam | ProductionAnchorWorldFrameResolver.cs:42 delete the relSection.referenceFrame != Relative guard -> red on the reason token (a bare-false assertion cannot see it) | M | 4 |

## trajectory-orbit (8 kept, showing 8)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-trajectory-orbit-016-01 | TrajectoryMath.cs:1143 | boundary | direct | TrajectoryMath.cs:1143-1150 clamp to the nearest section instead of returning -1 -> red | S | 3 |
| C-trajectory-orbit-017-01 | Reaim/ReaimLoiterCompressor.cs:110 | negative | direct | ReaimLoiterCompressor.cs:110 drop the next.bodyName != body term -> red | S | 3 |
| C-trajectory-orbit-017-02 | SpawnCollisionDetector.cs:786 | boundary | direct | SpawnCollisionDetector.cs:792-794 swap the y and z assignments -> red | S | 3 |
| C-trajectory-orbit-018-01 | Reaim/ReaimedTrajectory.cs:81 | boundary | direct | ReaimedTrajectory.cs:81-82 set spanStartUT/spanEndUT to 0.0 in the empty branch -> red | S | 3 |
| C-trajectory-orbit-006-01 | ParsekFlight.cs:22679 | state-transition | seam | apply the spin axis in body frame instead of boundaryWorldRot * angVel -> red | M | 3 |
| C-trajectory-orbit-010-01 | Reaim/ReaimSegmentAssembler.cs:426 | state-transition | direct | ReaimSegmentAssembler.cs:426 skip RotateLanForParkRephase for the arrival chain (or apply it to every segment) -> red | M | 3 |
| C-trajectory-orbit-002-01 | MissionLoopUnitBuilder.cs:1178 | integration | generator | MissionLoopUnitBuilder.cs:1178 drop + descCaptureShift -> red | L | 3 |
| C-trajectory-orbit-014-01 | Reaim/ReaimClassifier.cs:221 | negative | generator | ReaimClassifier.cs:221 walk the flattened multi-member segment list instead of the per-member one -> red (tof halves) | L | 3 |

## harness-seam (4 kept, showing 4)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-harness-seam-001-01 | TestCommands/TestCommandUiFind.cs:81 | serializer-key | direct | GuiTreeModel.cs:468 rename one GuiNodeKind KindName string (e.g. scrollview -> scroll) -> red | S | 3 |
| C-harness-seam-016-01 | TestCommands/TestCommandPlantFlag.cs:140 | negative | direct | TestCommandPlantFlag.cs:140 drop the !vesselActive arm -> red | S | 4 |
| C-harness-seam-002-01 | InGameTests/InGameTestRunner.cs:1080 | state-transition | seam | InGameTestRunner.cs:1082 add t.ResultsByScene.Clear() inside ResetResults -> red | M | 4 |
| C-harness-seam-019-01 | TestCommands/TestCommandMergeAnswer.cs:150 | negative | direct | TestCommandMergeAnswer.cs:150 change the default arm to return "committed" -> red | S | 5 |

## ledger-career (4 kept, showing 4)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-ledger-career-010-02 | UI/CareerStateWindowUI.cs:1134 | mirror-direction | direct | CareerStateWindowUI.cs:1129-1139 remove the try/catch around LookupStrategyTitleLive -> red (exception escapes Build) | S | 3 |
| C-ledger-career-010-01 | UI/CareerStateWindowUI.cs:456 | state-transition | direct | CareerStateWindowUI.cs:457 delete the case GameActionType.ContractFail fallthrough -> red | M | 3 |
| C-ledger-career-010-03 | UI/CareerStateWindowUI.cs:496 | negative | direct | CareerStateWindowUI.cs:496 delete the FacilityUpgrade !a.Effective guard -> red (an ineffective upgrade mutates the facility row) | M | 3 |
| C-ledger-career-010-04 | UI/CareerStateWindowUI.cs:479 | mirror-direction | direct | CareerStateWindowUI.cs:479-480 swap SourceResource and TargetResource -> red | S | 4 |

## legacy-bugfix (4 kept, showing 4)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-legacy-bugfix-017-01 | Diagnostics/DiagnosticsComputation.cs:842 | boundary | direct | DiagnosticsComputation.cs:842 change totalMs <= PlaybackBudgetThresholdMs to < -> red (an exactly-at-budget frame burns the one-shot breakdown latches) | S | 3 |
| C-legacy-bugfix-025-01 | GhostPlaybackEngine.cs:1090 | negative | seam | GhostPlaybackEngine.cs:1090 drop the !ctx.mapViewEnabled && term -> red | S | 3 |
| C-legacy-bugfix-004-01 | GhostMapPresence.cs:1782 | boundary | direct | GhostMapPresence.cs:1820 drop the !visitedRecs.Add(recId) term (and the matching visitedBPs guard) -> the walk never terminates on a cyclic save | M | 3 |
| C-legacy-bugfix-020-01 | Diagnostics/DiagnosticsComputation.cs:954 | boundary | direct | DiagnosticsComputation.cs:954 drop the trajectoriesIterated > 0 ternary and always divide -> red (DivideByZero or 0.00us instead of n/a) | S | 4 |

## mission-groups (4 kept, showing 4)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-mission-groups-003-01 | GhostPlaybackLogic.SpanClock.cs:3080 | state-transition | direct | GhostPlaybackLogic.SpanClock.cs:3086 return false instead of units.TryGetUnitForMember(...) -> red | S | 3 |
| C-mission-groups-003-02 | GhostPlaybackLogic.SpanClock.cs:3195 | negative | direct | GhostPlaybackLogic.SpanClock.cs:3200 return true unconditionally -> red | S | 3 |
| C-mission-groups-005-01 | MissionLoopUnitBuilder.cs:1540 | negative | direct | MissionLoopUnitBuilder.cs ComputeTrimmedMemberWindows: stop incrementing skippedNotCommitted -> red | S | 3 |
| C-mission-groups-006-01 | MissionLoopUnitBuilder.cs:420 | integration | direct | MissionLoopUnitBuilder.cs:420 set minSpacing = cadence without the span floor (drop the Math.Max against span) -> red on UnitMemberOverlaps | L | 3 |

## rewind-refly (2 kept, showing 2)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-rewind-refly-013-01 | RecordingStore.cs:5103 | mirror-direction | direct | RecordingStore.cs:5103 return false unconditionally from CanRewindWithResolvedSaveState -> red | S | 3 |
| C-rewind-refly-012-01 | ReFlyAutoSealPreview.cs:103 | serializer-key | direct | ReFlyAutoSealPreview.cs:103 add a ReFlyAutoSealReason member with no case arm -> red | S | 4 |

## logistics-route (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-logistics-route-031-01 | UI/LogisticsWindowUI.cs:2968 | integration | direct | LogisticsWindowUI.cs:2968-2972 delete the manual-loop-cleared toast block -> red | M | 3 |

## logging (2 kept, showing 2)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-logging-001-01 | Diagnostics/DiagnosticsComputation.cs:131 | boundary | seam | DiagnosticsComputation.cs:132 swap to bd.durationSeconds / bd.totalBytes -> red | S | 4 |
| C-logging-004-01 | Diagnostics/DiagnosticsComputation.cs:1121 | boundary | direct | DiagnosticsComputation.cs:1121 change totalMs > RecordingBudgetThresholdMs to >= -> red | S | 4 |

## catchall (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-catchall-043-01 | WatchModeController.Cycle.cs:106 | negative | direct | WatchModeController.Cycle.cs:106 pass watchedIndex instead of IndexShift.AfterDelete(watchedIndex, deletedIndex) -> red | S | 4 |

## ui-settings (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | prio |
|---|---|---|---|---|---|---|
| C-ui-settings-003-01 | UI/RecordingsTableFormatters.cs:146 | boundary | direct | RecordingsTableFormatters.cs:150-151 skip keys missing from end instead of leaving endAmt 0 -> red | S | 4 |

## Invalid

- C-ghost-playback-002-01 (Source/Parsek/GhostPlaybackLogic.WarpLoopPolicy.cs:345): cycleIndex<0 is unreachable: line 340 returns false for currentUT<scheduleStartUT and cycleDuration>=LoopTiming.MinCycleDuration=5.0 (ParsekConfig.cs:303), so floor(elapsed/cycleDuration)>=0 always.
- C-mission-groups-003-03 (Source/Parsek/GhostPlaybackLogic.SpanClock.cs:1985): No spanLoopUT < activationUT gate exists at GhostPlaybackLogic.SpanClock.cs:1985 (grep for activationUT in that file returns zero hits); line 1985 is the IsLoopUTInMemberWindow dispatch, driven by 27 DecideUnitMemberRender assertions.
