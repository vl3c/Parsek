# Coverage opportunities (D3) - Phase 3 output, 2026-09-15

Baseline `4aedb0a1a`. Source: the 249 Phase 1 coverage candidates after Phase 3 triage (SUT guard confirmed at the pinned SHA, headless feasibility classified, existing coverage grepped, one mutant named per proposal). Full table: `coverage-opportunities.csv`; per-partition detail: `work/phase3/ranked-*.md` (committed).

- Kept 245; already-covered 1; invalid 3; merged 3 duplicates folded.
- Feasibility of kept: {'seam': 36, 'direct': 193, 'in-game': 9, 'generator': 7}. Priority (1 = highest): {1: 16, 2: 86, 3: 97, 4: 38, 5: 8}. Risk: {'career': 54, 'data-loss': 12, 'recording': 100, 'playback': 53, 'ui': 26}.
- Priority = risk order (data-loss > career > recording > playback > ui), then feasibility (direct > seam > generator > in-game), then whether a Medium/High finding already names the gap.
- Every kept row carries `mutant`: the one-line production change the proposed test reds on. A row without one was dropped.

## Top 20 across clusters

| # | cand_id | cluster | risk | SUT | kind | feas | effort | P | mutant |
|---|---|---|---|---|---|---|---|---|---|
| 1 | C-legacy-bugfix-023-01 | legacy-bugfix | data-loss | `Source/Parsek/RecordingStore.OrphanCleanup.cs:278` | negative | direct | S | 1 | delete the savedPendingTreeDuringActiveRestore contribution at RecordingStore.OrphanCleanup.cs:276-277 |
| 2 | C-legacy-bugfix-024-01 | legacy-bugfix | data-loss | `Source/Parsek/RecordingSidecarStore.cs:63` | negative | direct | S | 1 | re-add File.Delete(vesselPath) when rec.VesselSnapshot is null (RecordingSidecarStore.cs:63 region) |
| 3 | C-recording-tree-034-01 | recording-tree | data-loss | `Source/Parsek/RecordingStore.cs:4606` | negative | direct | S | 1 | delete // hasSavedPendingDuringActiveRestore from the play-mode guard at RecordingStore.cs:4608-4611 |
| 4 | C-rewind-refly-011-01 | rewind-refly | data-loss | `Source/Parsek/RewindPointReaper.cs:284` | state-transition | direct | S | 1 | delete the File.Delete(absolute) call at RewindPointReaper.cs:286 (log only) |
| 5 | C-rewind-refly-020-01 | rewind-refly | data-loss | `Source/Parsek/ParsekScenario.cs:6733` | integration | direct | M | 1 | walk protoVessels forward with RemoveAt (i++ over the removal) at ParsekScenario.cs:6740 so consecutive strips are skipped |
| 6 | C-legacy-bugfix-005-01 | legacy-bugfix | recording | `Source/Parsek/ParsekFlight.cs:16405` | boundary | direct | S | 1 | delete 'return TerminalState.Orbiting;' at ParsekFlight.cs:16406 (falls through to the SubOrbital default) |
| 7 | C-recorder-events-023-01 | recorder-events | recording | `Source/Parsek/ParsekFlight.TerminalEvents.cs:326` | state-transition | direct | S | 1 | move 'rec.VesselDestroyed = true;' below the 'if (rec.TerminalStateValue == TerminalState.Destroyed) return false;' early return at ParsekFl |
| 8 | C-recording-tree-011-01 | recording-tree | recording | `Source/Parsek/ParsekScenario.HydrationRepair.cs:25` | negative | direct | S | 1 | delete the 'RecordingStore.PendingTree.Id != loadedTree.Id' disjunct at HydrationRepair.cs:25 |
| 9 | C-rewind-refly-004-02 | rewind-refly | recording | `Source/Parsek/RecordingStore.RewindSupersedeRollback.cs:99` | boundary | direct | S | 1 | RecordingStore.RewindSupersedeRollback.cs:99 delete the string.IsNullOrEmpty(rel.OldRecordingId) skip |
| 10 | C-rewind-refly-019-01 | rewind-refly | recording | `Source/Parsek/RewindInvoker.cs:2433` | state-transition | direct | S | 1 | RewindInvoker.cs:2433-2438 delete the PendingTree removal block |
| 11 | C-legacy-bugfix-010-01 | legacy-bugfix | recording | `Source/Parsek/EffectiveState.cs:1065` | state-transition | direct | M | 1 | invert IsSlotEffectiveTipOpen's use at EffectiveState.cs:1064 so an Immutable tip is admitted |
| 12 | C-recorder-events-024-01 | recorder-events | recording | `Source/Parsek/BackgroundRecorder.PartEventPolling.cs:92` | integration | direct | M | 1 | delete the CheckFairingState call from BackgroundRecorder.PollPartEvents |
| 13 | C-recording-tree-022-01 | recording-tree | recording | `Source/Parsek/RecordingOptimizer.cs:1849` | mirror-direction | direct | M | 1 | RecordingOptimizer.cs:1849 change headSection.bodyFixedFrames.Count < 2 to < 1 |
| 14 | C-recording-tree-030-01 | recording-tree | recording | `Source/Parsek/RecordingStore.cs:539` | boundary | direct | M | 1 | RecordingStore.cs:539 force firstMoving = 0 so the trim / orbit-drop / event-retime block never runs |
| 15 | C-rewind-refly-015-01 | rewind-refly | recording | `Source/Parsek/RecordingStore.cs:4216` | mirror-direction | direct | M | 1 | RecordingStore.cs:4220 change (emptyParents // emptyChildren) to emptyChildren only |
| 16 | C-recording-tree-023-01 | recording-tree | recording | `Source/Parsek/RecordingTreeSplitter.cs:785` | state-transition | direct | L | 1 | RecordingTreeSplitter.cs:785 delete the RollBackInMemory(scenario, freshSnapshot) call in the catch |
| 17 | C-recording-tree-047-02 | recording-tree | data-loss | `Source/Parsek/RecordingStore.cs:2495` | negative | direct | M | 2 | move the marker-owned bypass below the attempt-set / cutoff checks at RecordingStore.cs:2495 |
| 18 | C-catchall-049-01 | catchall | data-loss | `Source/Parsek/ChainSegmentManager.cs:1` | state-transition | seam | M | 2 | delete rec.MarkFilesDirty() after the continuation point append (ChainSegmentManager.cs:429) |
| 19 | C-io-serialization-004-01 | io-serialization | data-loss | `Source/Parsek/FileIOUtils.cs:302` | negative | seam | M | 2 | delete the destination-preserving fallback in the File.Replace catch (let destPath be removed before the move-aside retry) |
| 20 | C-recording-tree-039-01 | recording-tree | data-loss | `Source/Parsek/RecordingStore.OrphanCleanup.cs:56` | integration | seam | M | 2 | delete the RewindSaveFileName limb at RecordingStore.OrphanCleanup.cs:56-57 |

## Per cluster (cap 10, highest priority first; the rest stay in the CSV)

### rewind-refly (21 kept, showing 10)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-rewind-refly-011-01 | `Source/Parsek/RewindPointReaper.cs:284` | state-transition | direct |  | S | 1 | none | delete the File.Delete(absolute) call at RewindPointReaper.cs:286 (log only) |
| C-rewind-refly-020-01 | `Source/Parsek/ParsekScenario.cs:6733` | integration | direct |  | M | 1 | none | walk protoVessels forward with RemoveAt (i++ over the removal) at ParsekScenario.cs:6740 so consecutive strips are skipp |
| C-rewind-refly-004-02 | `Source/Parsek/RecordingStore.RewindSupersedeRollback.cs:99` | boundary | direct |  | S | 1 | none | RecordingStore.RewindSupersedeRollback.cs:99 delete the string.IsNullOrEmpty(rel.OldRecordingId) skip |
| C-rewind-refly-019-01 | `Source/Parsek/RewindInvoker.cs:2433` | state-transition | direct |  | S | 1 | none | RewindInvoker.cs:2433-2438 delete the PendingTree removal block |
| C-rewind-refly-015-01 | `Source/Parsek/RecordingStore.cs:4216` | mirror-direction | direct |  | M | 1 | none | RecordingStore.cs:4220 change (emptyParents // emptyChildren) to emptyChildren only |
| C-rewind-refly-006-01 | `Source/Parsek/GameActions/LedgerOrchestrator.cs:2269` | boundary | direct |  | S | 2 | none | change candidate.ut < initialBaseline.ut to > at LedgerOrchestrator.cs:2269 (last-wins seed) |
| C-rewind-refly-016-01 | `Source/Parsek/GameActions/TombstoneEligibility.cs:196` | boundary | direct |  | S | 2 | none | delete the source-agnostic timing arm at TombstoneEligibility.cs:195-200 (return false unless RepPenaltySource == Kerbal |
| C-rewind-refly-016-02 | `Source/Parsek/GameActions/TombstoneEligibility.cs:78` | mirror-direction | direct |  | S | 2 | none | delete the case GameActionType.StrategyScienceCredit: label at TombstoneEligibility.cs:78 so it falls to the preserve-un |
| C-rewind-refly-017-01 | `Source/Parsek/GameActions/KspStatePatcher.cs:3693` | mirror-direction | direct |  | S | 2 | none | delete the RewindReadbackGuard.AbortRewindPatchOnDivergence // operand at KspStatePatcher.cs:3693 |
| C-rewind-refly-003-01 | `Source/Parsek/SupersedeCommit.cs:651` | negative | direct |  | S | 2 | F-rewind-refly-003-01 | SupersedeCommit.cs:651 delete the rec.ChainBranch == tip.ChainBranch conjunct |

### recording-tree (71 kept, showing 10)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-recording-tree-034-01 | `Source/Parsek/RecordingStore.cs:4606` | negative | direct |  | S | 1 | none | delete // hasSavedPendingDuringActiveRestore from the play-mode guard at RecordingStore.cs:4608-4611 |
| C-recording-tree-011-01 | `Source/Parsek/ParsekScenario.HydrationRepair.cs:25` | negative | direct |  | S | 1 | none | delete the 'RecordingStore.PendingTree.Id != loadedTree.Id' disjunct at HydrationRepair.cs:25 |
| C-recording-tree-022-01 | `Source/Parsek/RecordingOptimizer.cs:1849` | mirror-direction | direct |  | M | 1 | none | RecordingOptimizer.cs:1849 change headSection.bodyFixedFrames.Count < 2 to < 1 |
| C-recording-tree-030-01 | `Source/Parsek/RecordingStore.cs:539` | boundary | direct |  | M | 1 | none | RecordingStore.cs:539 force firstMoving = 0 so the trim / orbit-drop / event-retime block never runs |
| C-recording-tree-023-01 | `Source/Parsek/RecordingTreeSplitter.cs:785` | state-transition | direct |  | L | 1 | none | RecordingTreeSplitter.cs:785 delete the RollBackInMemory(scenario, freshSnapshot) call in the catch |
| C-recording-tree-047-02 | `Source/Parsek/RecordingStore.cs:2495` | negative | direct |  | M | 2 | none | move the marker-owned bypass below the attempt-set / cutoff checks at RecordingStore.cs:2495 |
| C-recording-tree-039-01 | `Source/Parsek/RecordingStore.OrphanCleanup.cs:56` | integration | seam | save-root override for RecordingPaths.ResolveSaveScopedPath (OrphanCleanup has C | M | 2 | none | delete the RewindSaveFileName limb at RecordingStore.OrphanCleanup.cs:56-57 |
| C-recording-tree-042-01 | `Source/Parsek/SceneExitInterceptor.cs:565` | negative | seam | injectable GamePersistence.SaveGame / ShowSaveFailedPopup seam so the save can t | M | 2 | F-recording-tree-042-04 | return true (warn) instead of false for GameScenes.MAINMENU in the catch at SceneExitInterceptor.cs:565-572 |
| C-recording-tree-021-02 | `Source/Parsek/CrewReservationManager.cs:131` | state-transition | direct |  | S | 2 | none | delete rescuePlacedKerbals.Clear() in the null-roster arm at CrewReservationManager.cs:131 |
| C-recording-tree-032-01 | `Source/Parsek/EffectiveState.cs:1500` | negative | direct |  | S | 2 | F-recording-tree-032-05 | add a supersede filter to ComputeELS that skips actions tagged to a superseded recording (EffectiveState.cs:1496-1506) |

### ledger-career (39 kept, showing 10)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-ledger-career-001-01 | `Source/Parsek/GameActions/KscActionExpectationClassifier.cs:139` | state-transition | direct |  | S | 2 | none | in the FacilityUpgrade arm use -action.FundsSpent (always 0) instead of -action.FacilityCost (KscActionExpectationClassi |
| C-ledger-career-003-01 | `Source/Parsek/GameActions/LedgerOrchestrator.cs:5775` | negative | direct |  | S | 2 | none | delete the evt.key == TechResearchScienceReasonKey filter at LedgerOrchestrator.cs:5775 |
| C-ledger-career-004-01 | `Source/Parsek/GameActions/LedgerOrchestrator.cs:1721` | negative | direct |  | S | 2 | none | delete the // !string.IsNullOrEmpty(rec.EvaCrewName) conjunct at LedgerOrchestrator.cs:1721 |
| C-ledger-career-006-01 | `Source/Parsek/GameActions/LedgerRecoveryFundsPairing.cs:591` | negative | direct |  | S | 2 | none | delete the delta <= 0 early return at LedgerRecoveryFundsPairing.cs:591 |
| C-ledger-career-007-01 | `Source/Parsek/GameStateEvent.cs:205` | negative | direct |  | S | 2 | none | delete the observedDelta vs entryCost tolerance check at GameStateEvent.cs:205-207 |
| C-ledger-career-007-02 | `Source/Parsek/GameStateEvent.cs:238` | negative | direct |  | S | 2 | none | return true on any nearby TechResearched with a non-empty parts list (drop the per-part name compare at GameStateEvent.c |
| C-ledger-career-008-01 | `Source/Parsek/GameStateStore.cs:819` | negative | direct |  | S | 2 | none | drop action.ScienceAwarded <= 0f from the skip disjunct at GameStateStore.cs:819-822 |
| C-ledger-career-009-01 | `Source/Parsek/GameActions/Ledger.cs:683` | negative | direct |  | S | 2 | none | make generationOk unconditionally true at Ledger.cs:683-685 |
| C-ledger-career-009-02 | `Source/Parsek/GameActions/Ledger.cs:929` | state-transition | direct |  | S | 2 | none | delete if (before != actions.Count) BumpStateVersion(); at Ledger.cs:928-929 |
| C-ledger-career-011-01 | `Source/Parsek/GameActions/GameStateEventConverter.cs:619` | negative | direct |  | S | 2 | none | delete the toLevel<1 clamp at GameStateEventConverter.cs:619-623 |

### recorder-events (28 kept, showing 10)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-recorder-events-023-01 | `Source/Parsek/ParsekFlight.TerminalEvents.cs:326` | state-transition | direct |  | S | 1 | F-recorder-events-023-04 | move 'rec.VesselDestroyed = true;' below the 'if (rec.TerminalStateValue == TerminalState.Destroyed) return false;' earl |
| C-recorder-events-024-01 | `Source/Parsek/BackgroundRecorder.PartEventPolling.cs:92` | integration | direct |  | M | 1 | none | delete the CheckFairingState call from BackgroundRecorder.PollPartEvents |
| C-recorder-events-026-01 | `Source/Parsek/BackgroundRecorder.cs:553` | integration | seam | injectable RewindPointAuthor seam around BackgroundRecorder.HandleBackgroundVess | L | 2 | none | require >= 3 controllable outputs before RewindPointAuthor.Begin (BackgroundRecorder.cs:896 branch) |
| C-recorder-events-003-01 | `Source/Parsek/FlightRecorder.cs:4832` | state-transition | direct |  | S | 2 | none | change 'bool movingNow = movingSignal // inferredMoving;' to 'bool movingNow = movingSignal;' at FlightRecorder.cs:4833 |
| C-recorder-events-004-01 | `Source/Parsek/BackgroundRecorder.cs:6743` | mirror-direction | direct |  | S | 2 | none | invert the trailing ternary to 'source == AnchorCandidateSource.Ghost ? "live" : "recorded"' at BackgroundRecorder.cs:67 |
| C-recorder-events-021-01 | `Source/Parsek/FlightRecorder.cs:297` | state-transition | direct |  | S | 2 | none | delete 'HasPendingJointBreakCheck = false;' at FlightRecorder.cs:299 (the consume never clears) |
| C-recorder-events-008-01 | `Source/Parsek/GhostMapPresence.cs:12959` | negative | seam | one extracted predicate over (inRelativeFrame, altitude, speed, atmosphereDepth) | M | 2 | none | GhostMapPresence.cs:12956 delete the if (!inRelativeFrame) gate -> red |
| C-recorder-events-015-02 | `Source/Parsek/WatchModeController.cs:3488` | state-transition | seam | extract the cadence-selection step of the private instance method WatchModeContr | M | 2 | none | WatchModeController.cs ResolveWatchPlaybackUT: hand the raw user period to ComputeOverlapCycleLoopUT instead of ComputeE |
| C-recorder-events-003-03 | `Source/Parsek/FlightRecorder.cs:2567` | boundary | direct |  | S | 3 | none | change 'normalizedHeat >= AnimateHeatMediumThreshold' to '>' at FlightRecorder.cs:2567 |
| C-recorder-events-015-01 | `Source/Parsek/RecordingTreeRecordCodec.cs:671` | negative | direct |  | S | 3 | F-recorder-events-015-01 | drop the IsNullOrEmpty guard at RecordingTreeRecordCodec.cs:672 and assign 'loopAnchorBodyNameStr ?? string.Empty' |

### logistics-route (6 kept, showing 6)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-logistics-route-014-01 | `Source/Parsek/Logistics/RouteOrchestrator.cs:5697` | mirror-direction | direct |  | S | 2 | none | drop the hasInventory term from the OR at RouteOrchestrator.cs:5703 |
| C-logistics-route-002-01 | `Source/Parsek/Logistics/RouteAnalysisEngine.cs:1134` | negative | direct |  | S | 2 | none | delete 'if (originWindow.TransferKind != RouteConnectionKind.DockingPort) return false;' at RouteAnalysisEngine.cs:1134 |
| C-logistics-route-033-01 | `Source/Parsek/Logistics/LiveDeliveryCapacityProbe.cs:561` | integration | seam | internal pure helper over (partIndex, moduleIndex, ModuleInventoryPart values Co | M | 3 | none | change the module-to-module continue to break in the ProbeUnloadedFirstEmpty walk |
| C-logistics-route-032-01 | `Source/Parsek/RouteHarvestCapture.cs:160` | negative | direct |  | S | 3 | none | delete 'if (window == null) return;' at RouteHarvestCapture.cs:160 |
| C-logistics-route-031-01 | `Source/Parsek/UI/LogisticsWindowUI.cs:2968` | integration | direct | house source-text wiring gate, same pattern as RouteGoBackRewindReconcileTests.H | M | 3 | none | LogisticsWindowUI.cs:2968-2972 delete the manual-loop-cleared toast block -> red |
| C-logistics-route-020-01 | `Source/Parsek/Logistics/RouteRunCostCalculator.cs:257` | negative | direct |  | M | 4 | F-logistics-route-020-02 | delete the treeRecordingIds / ELS recovery filter so a retired recovery row re-credits the run cost |

### spawn-vessel (5 kept, showing 5)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-spawn-vessel-021-01 | `Source/Parsek/ParsekScenario.cs:3550` | state-transition | seam | pure predicate over (pendingPids, pendingNames, freshPids, freshNames) extracted | M | 2 | none | drop the alreadyHasCleanupData guard at ParsekScenario.cs:3550 so the revert path overwrites rewind cleanup data with nu |
| C-spawn-vessel-020-01 | `Source/Parsek/ParsekPlaybackPolicy.cs:230` | state-transition | direct |  | M | 2 | none | ParsekPlaybackPolicy.cs:233 also reset rec.VesselSpawned = false / pid = 0 on the abandon branch -> red (infinite respaw |
| C-spawn-vessel-014-01 | `Source/Parsek/VesselSpawner.cs:6560` | boundary | direct |  | S | 3 | none | VesselSpawner.cs:6560 change speed > orbitalSpeed * 0.9 to >= -> red |
| C-spawn-vessel-021-02 | `Source/Parsek/VesselSpawner.cs:491` | state-transition | seam | add internal static Func<string,string> LocalizedNameResolverForTesting beside V | S | 4 | none | VesselSpawner.cs:498-500 return before the write-back when resolved != raw |
| C-spawn-vessel-004-01 | `Source/Parsek/FlightRecorder.cs:6983` | integration | in-game | FLIGHT InGameTest: StartRecording is an instance method needing a live Vessel | M | 5 | none | FlightRecorder.cs:6983 change AdoptControllersIfEmpty(pendingStartControllers) to a no-op returning false |

### ghost-playback (15 kept, showing 10)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-ghost-playback-023-01 | `Source/Parsek/GameStateStore.cs:513` | integration | direct |  | S | 2 | none | delete the MilestoneStore.PurgeTaggedEvents call at GameStateStore.cs:513 (milestoneRemoved = empty list) |
| C-ghost-playback-008-01 | `Source/Parsek/GhostMapPresence.cs:9221` | negative | direct |  | S | 2 | F-ghost-playback-008-01 | GhostMapPresence.cs:4055 add TerminalState.Landed to IsTerminalStateEligibleForTerminalOrbitMapPresence -> red |
| C-ghost-playback-012-01 | `Source/Parsek/GhostChainWalker.cs:537` | state-transition | direct |  | M | 2 | none | GhostChainWalker.cs:535 delete the chainVisited.Contains(tipVesselPid) break -> the test hangs/reds |
| C-ghost-playback-011-01 | `Source/Parsek/WatchModeController.Diagnostics.cs:154` | boundary | direct |  | S | 3 | F-ghost-playback-011-02 | WatchModeController.Diagnostics.cs:164 compare ClassifyMapFocusRestore(...) to a literal other than "ready" -> red |
| C-ghost-playback-018-01 | `Source/Parsek/Recording.cs:1398` | mirror-direction | direct |  | S | 3 | F-ghost-playback-018-02 | Recording.cs:1398 change IPlaybackTrajectory.LoopPlayback to => LoopPlayback (drop the !IsDebris mask) -> red |
| C-ghost-playback-013-01 | `Source/Parsek/GhostPlaybackLogic.cs:5483` | mirror-direction | seam | extract internal static float ComputeWheelSteeringTargetDegrees(float steeringHe | S | 3 | F-ghost-playback-013-01 | GhostPlaybackLogic.cs:5483 drop the leading minus on steeringHeadingRate -> red |
| C-ghost-playback-014-01 | `Source/Parsek/GhostMapPresence.cs:4407` | integration | seam | make GhostMapPresence.EmitSourceResolveLine internal static, or drive ResolveMap | M | 3 | none | GhostMapPresence.cs EmitSourceResolveLine: delete fields.RecordingIndex = recordingIndex -> red |
| C-ghost-playback-015-01 | `Source/Parsek/PlaybackTrace.cs:126` | state-transition | direct |  | S | 4 | F-ghost-playback-015-02 | PlaybackTrace.cs:129 delete traceStates.Clear() -> red |
| C-ghost-playback-017-01 | `Source/Parsek/GhostExtender.cs:86` | negative | direct |  | S | 4 | none | GhostExtender.cs:86 drop the rec={0} field from the None fallthrough line -> red |
| C-ghost-playback-025-01 | `Source/Parsek/ParsekFlight.cs:19754` | integration | direct | house source-gate pattern already used by GuiCensusApplierSourceGateTests / Miss | S | 4 | F-ghost-playback-025-01 | ParsekFlight.cs:19754 move ExitWatchModeBeforeTimelineGhostCleanup below engine.DestroyAllGhosts() -> red |

### map-render (9 kept, showing 9)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-map-render-005-01 | `Source/Parsek/Rendering/SmoothingPipeline.cs:667` | boundary | direct |  | S | 3 | none | SmoothingPipeline.cs:667-669 delete the frames.Count < MinSamplesPerSection gate -> red (short section is fitted / Catmu |
| C-map-render-011-01 | `Source/Parsek/Rendering/AnchorCandidateBuilder.cs:467` | negative | direct |  | S | 3 | none | AnchorCandidateBuilder.cs:467 delete the rec.LoopAnchorVesselId == 0u return -> red |
| C-map-render-014-01 | `Source/Parsek.Tests/MapRender/PhaseSpineParityTests.cs:224` | integration | direct | strengthens an existing test, no production seam | S | 3 | none | make the assembler spine emit Visible=false on every frame -> the sweep stays green today, reds with the sawVisible anch |
| C-map-render-023-01 | `Source/Parsek/ParsekFlight.cs:22163` | state-transition | direct |  | S | 3 | F-map-render-023-01 | ParsekFlight.cs:22169 swap the splineApplied and suppressHermite guards -> red on the splineApplied=true + suppressHermi |
| C-map-render-004-02 | `Source/Parsek/MapRenderTrace.cs:402` | boundary | direct |  | S | 3 | none | MapRenderTrace.cs:402-403 move the tsSkip append above the outcome/ride fields -> red |
| C-map-render-012-01 | `Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:31` | negative | seam | the first four guards (ProductionAnchorWorldFrameResolver.cs:31,32,39,42) return | M | 4 | none | ProductionAnchorWorldFrameResolver.cs:42 delete the relSection.referenceFrame != Relative guard -> red on the reason tok |
| C-map-render-004-01 | `Source/Parsek/MapRenderTrace.cs:346` | boundary | direct |  | S | 4 | none | MapRenderTrace.cs:346 switch drop the FallbackNonAnchoredUseHead arm (falls to default) -> red |
| C-map-render-022-01 | `Source/Parsek/ParsekTrackingStation.cs:105` | boundary | direct |  | S | 4 | none | ParsekTrackingStation.cs:105 add an AtmosphericMarkerSkipReason member with no token arm -> red |
| C-map-render-013-01 | `Source/Parsek/MapMarkerRenderer.cs:233` | boundary | seam | extract internal static Rect ComputeHandlerHitRect(Rect iconRect) from the inlin | S | 4 | none | MapMarkerRenderer.cs:35 set ClickPadding = ToggleClickPadding -> red on strict containment |

### trajectory-orbit (10 kept, showing 10)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-trajectory-orbit-011-01 | `Source/Parsek/AnchorDetector.cs:384` | negative | direct |  | S | 3 | none | AnchorDetector.cs:389 make the VesselPersistentId comparison return true unconditionally |
| C-trajectory-orbit-016-01 | `Source/Parsek/TrajectoryMath.cs:1143` | boundary | direct |  | S | 3 | none | TrajectoryMath.cs:1143-1150 clamp to the nearest section instead of returning -1 -> red |
| C-trajectory-orbit-017-01 | `Source/Parsek/Reaim/ReaimLoiterCompressor.cs:110` | negative | direct |  | S | 3 | none | ReaimLoiterCompressor.cs:110 drop the next.bodyName != body term -> red |
| C-trajectory-orbit-017-02 | `Source/Parsek/SpawnCollisionDetector.cs:786` | boundary | direct |  | S | 3 | F-trajectory-orbit-017-03 | SpawnCollisionDetector.cs:792-794 swap the y and z assignments -> red |
| C-trajectory-orbit-018-01 | `Source/Parsek/Reaim/ReaimedTrajectory.cs:81` | boundary | direct |  | S | 3 | none | ReaimedTrajectory.cs:81-82 set spanStartUT/spanEndUT to 0.0 in the empty branch -> red |
| C-trajectory-orbit-010-01 | `Source/Parsek/Reaim/ReaimSegmentAssembler.cs:426` | state-transition | direct |  | M | 3 | none | ReaimSegmentAssembler.cs:426 skip RotateLanForParkRephase for the arrival chain (or apply it to every segment) -> red |
| C-trajectory-orbit-006-01 | `Source/Parsek/ParsekFlight.cs:22679` | state-transition | seam | extract the spin-forward composition (worldAxis = boundaryWorldRot * angVel; Pur | M | 3 | none | apply the spin axis in body frame instead of boundaryWorldRot * angVel -> red |
| C-trajectory-orbit-002-01 | `Source/Parsek/MissionLoopUnitBuilder.cs:1178` | integration | generator | needs a re-aim mission fixture carrying a descent run with a known EndUT and a n | L | 3 | none | MissionLoopUnitBuilder.cs:1178 drop + descCaptureShift -> red |
| C-trajectory-orbit-014-01 | `Source/Parsek/Reaim/ReaimClassifier.cs:221` | negative | generator | needs a two-member committed fixture (transfer plus an LKO-parked interloper spa | L | 3 | F-trajectory-orbit-014-01 | ReaimClassifier.cs:221 walk the flattened multi-member segment list instead of the per-member one -> red (tof halves) |
| C-trajectory-orbit-015-01 | `Source/Parsek/FlightRecorder.cs:9334` | roundtrip | in-game | FLIGHT InGameTest: ApplyRelativeOffsetForAnchorPose is a private instance method | L | 5 | none | FlightRecorder.cs:9337 pass the vessel CoM instead of anchorPose.WorldPos to ComputeRelativeLocalOffset |

### mission-groups (6 kept, showing 6)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-mission-groups-014-01 | `Source/Parsek/UI/RecordingsTableUI.cs:6377` | integration | direct |  | S | 2 | none | replace 'int ri = sortedIndices[row];' with 'int ri = row;' at RecordingsTableUI.cs:6377 |
| C-mission-groups-004-01 | `Source/Parsek/RecordingGroupStore.cs:811` | mirror-direction | direct |  | S | 3 | none | drop '+ toleranceSeconds' from the late bound at RecordingGroupStore.cs:819 |
| C-mission-groups-003-01 | `Source/Parsek/GhostPlaybackLogic.SpanClock.cs:3080` | state-transition | direct |  | S | 3 | F-mission-groups-003-02 | GhostPlaybackLogic.SpanClock.cs:3086 return false instead of units.TryGetUnitForMember(...) -> red |
| C-mission-groups-003-02 | `Source/Parsek/GhostPlaybackLogic.SpanClock.cs:3195` | negative | direct |  | S | 3 | F-mission-groups-003-03 | GhostPlaybackLogic.SpanClock.cs:3200 return true unconditionally -> red |
| C-mission-groups-005-01 | `Source/Parsek/MissionLoopUnitBuilder.cs:1540` | negative | direct |  | S | 3 | none | MissionLoopUnitBuilder.cs ComputeTrimmedMemberWindows: stop incrementing skippedNotCommitted -> red |
| C-mission-groups-006-01 | `Source/Parsek/MissionLoopUnitBuilder.cs:420` | integration | direct |  | L | 3 | F-mission-groups-006-01 | MissionLoopUnitBuilder.cs:420 set minSpacing = cadence without the span floor (drop the Math.Max against span) -> red on |

### logging (3 kept, showing 3)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-logging-004-02 | `Source/Parsek/VesselSpawner.cs:3198` | integration | direct |  | S | 4 | none | add 'break;' after the first PART node in the LogSpawnContext crew loop (VesselSpawner.cs:3198) |
| C-logging-004-01 | `Source/Parsek/Diagnostics/DiagnosticsComputation.cs:1121` | boundary | direct |  | S | 4 | none | DiagnosticsComputation.cs:1121 change totalMs > RecordingBudgetThresholdMs to >= -> red |
| C-logging-001-01 | `Source/Parsek/Diagnostics/DiagnosticsComputation.cs:131` | boundary | seam | extract internal static double ComputeBytesPerSecond(long totalBytes, double dur | S | 4 | none | DiagnosticsComputation.cs:132 swap to bd.durationSeconds / bd.totalBytes -> red |

### harness-seam (6 kept, showing 6)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-harness-seam-020-01 | `Source/Parsek/TestCommands/TestCommandKscAction.cs:164` | boundary | direct |  | S | 3 | none | change > to >= in the HireKerbal funds compare (TestCommandKscAction.cs:164) |
| C-harness-seam-015-01 | `Source/Parsek/InGameTests/InGameTestSidecarReaper.cs:174` | integration | direct |  | M | 3 | none | delete '.pann' from InGameTestSidecarReaper.SidecarSuffixes |
| C-harness-seam-001-01 | `Source/Parsek/TestCommands/TestCommandUiFind.cs:81` | serializer-key | direct |  | S | 3 | F-harness-seam-001-01 | GuiTreeModel.cs:468 rename one GuiNodeKind KindName string (e.g. scrollview -> scroll) -> red |
| C-harness-seam-016-01 | `Source/Parsek/TestCommands/TestCommandPlantFlag.cs:140` | negative | direct |  | S | 4 | none | TestCommandPlantFlag.cs:140 drop the !vesselActive arm -> red |
| C-harness-seam-002-01 | `Source/Parsek/InGameTests/InGameTestRunner.cs:1080` | state-transition | seam | extract the shared per-test reset body of ResetResults / ClearAllSceneHistory in | M | 4 | none | InGameTestRunner.cs:1082 add t.ResultsByScene.Clear() inside ResetResults -> red |
| C-harness-seam-019-01 | `Source/Parsek/TestCommands/TestCommandMergeAnswer.cs:150` | negative | direct |  | S | 5 | none | TestCommandMergeAnswer.cs:150 change the default arm to return "committed" -> red |

### wiring-gates (1 kept, showing 1)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-wiring-gates-003-01 | `Source/Parsek/ParsekFlight.cs:4914` | integration | direct |  | M | 4 | none | ParsekFlight.cs undock/EVA split blocks: add a coalescer.OnSplitEvent call inside the Undock block |

### ui-settings (1 kept, showing 1)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-ui-settings-003-01 | `Source/Parsek/UI/RecordingsTableFormatters.cs:146` | boundary | direct |  | S | 4 | none | RecordingsTableFormatters.cs:150-151 skip keys missing from end instead of leaving endAmt 0 -> red |

### io-serialization (1 kept, showing 1)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-io-serialization-004-01 | `Source/Parsek/FileIOUtils.cs:302` | negative | seam | ForceReplaceFailureForTesting flag next to FileIOUtils.ForceMoveAsideFallbackFor | M | 2 | none | delete the destination-preserving fallback in the File.Replace catch (let destPath be removed before the move-aside retr |

### legacy-bugfix (17 kept, showing 10)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-legacy-bugfix-023-01 | `Source/Parsek/RecordingStore.OrphanCleanup.cs:278` | negative | direct |  | S | 1 | none | delete the savedPendingTreeDuringActiveRestore contribution at RecordingStore.OrphanCleanup.cs:276-277 |
| C-legacy-bugfix-024-01 | `Source/Parsek/RecordingSidecarStore.cs:63` | negative | direct |  | S | 1 | none | re-add File.Delete(vesselPath) when rec.VesselSnapshot is null (RecordingSidecarStore.cs:63 region) |
| C-legacy-bugfix-005-01 | `Source/Parsek/ParsekFlight.cs:16405` | boundary | direct |  | S | 1 | F-legacy-bugfix-005-01 | delete 'return TerminalState.Orbiting;' at ParsekFlight.cs:16406 (falls through to the SubOrbital default) |
| C-legacy-bugfix-010-01 | `Source/Parsek/EffectiveState.cs:1065` | state-transition | direct |  | M | 1 | F-legacy-bugfix-010-01 | invert IsSlotEffectiveTipOpen's use at EffectiveState.cs:1064 so an Immutable tip is admitted |
| C-legacy-bugfix-016-01 | `Source/Parsek/MilestoneStore.cs:562` | negative | direct |  | S | 2 | none | delete if (!m.Committed) continue; at MilestoneStore.cs:568 |
| C-legacy-bugfix-002-01 | `Source/Parsek/RecordingStore.cs:972` | negative | direct |  | S | 2 | F-legacy-bugfix-002-01 | make CommitTree commit only the tree root recording (drop the child loop) |
| C-legacy-bugfix-011-01 | `Source/Parsek/EnvironmentDetector.cs:135` | negative | direct |  | S | 2 | none | drop the 'heightFromTerrain >= 0.0' conjunct at EnvironmentDetector.cs:136 |
| C-legacy-bugfix-015-01 | `Source/Parsek/TimeJumpManager.cs:408` | roundtrip | direct |  | S | 2 | F-legacy-bugfix-015-01 | transpose lat and lon in the CreateTimeJumpEvent format arguments at TimeJumpManager.cs:415 |
| C-legacy-bugfix-007-01 | `Source/Parsek/FlightRecorder.cs:5459` | state-transition | direct |  | M | 2 | none | swap the divisor at FlightRecorder.cs:5464 to 'duration / frames.Count' (mirror: BackgroundRecorder.cs:7155) |
| C-legacy-bugfix-008-01 | `Source/Parsek/GameStateRecorder.Handlers.cs:856` | boundary | generator | FakeProgressNode test double must carry the private body field QualifyMilestoneI | M | 3 | none | return the bare id from QualifyMilestoneId (drop the body-name prefix) |

### catchall (6 kept, showing 6)

| cand_id | SUT | kind | feas | seam / generator | effort | P | related | mutant |
|---|---|---|---|---|---|---|---|---|
| C-catchall-049-01 | `Source/Parsek/ChainSegmentManager.cs:1` | state-transition | seam | internal static AppendContinuationPoint(Recording, TrajectoryPoint) extracted fr | M | 2 | none | delete rec.MarkFilesDirty() after the continuation point append (ChainSegmentManager.cs:429) |
| C-catchall-021-01 | `Source/Parsek/VesselSpawner.cs:3143` | negative | direct |  | S | 2 | none | delete 'if (sibling.ChainBranch != 0) continue;' at VesselSpawner.cs:3143 |
| C-catchall-011-01 | `Source/Parsek/RecordingOptimizer.cs:1685` | state-transition | direct |  | M | 2 | none | delete the OrbitSegmentCheckpointBridge.InvalidateSectionAnnotationsForOrdinalShift call at RecordingOptimizer.cs:1685 |
| C-catchall-022-01 | `Source/Parsek/GameActions/KspStatePatcher.cs:3347` | state-transition | seam | internal accessors for the six static clamp-toast latches in KspStatePatcher (or | M | 3 | none | empty the body of KspStatePatcher.ResetDrawdownGuardSessionLatches (delete the six latch assignments) |
| C-catchall-029-01 | `Source/Parsek/TrajectoryMath.Stats.cs:244` | boundary | direct |  | S | 4 | none | change 'kvp.Value > maxCount' to '>=' at TrajectoryMath.Stats.cs:244 |
| C-catchall-043-01 | `Source/Parsek/WatchModeController.Cycle.cs:106` | negative | direct |  | S | 4 | none | WatchModeController.Cycle.cs:106 pass watchedIndex instead of IndexShift.AfterDelete(watchedIndex, deletedIndex) -> red |

## Triage notes carried forward (Phase 4 / Phase B input)

- C-recorder-events-011-01 (ShouldRecordPoint zero-delta) is refiled as a T7 question: the candidate's expected value contradicts current production (minInterval 0 does not floor; the speed gate returns true).
- C-logistics-route-020-01 vs C-recording-tree-032-01 conflict on whether ELS excludes a superseded recovery row; the route exclusion likely comes from treeRecordingIds. Confirm before writing either cell.
- Two chain-walk mutants (GhostChainWalker.cs:535, ParsekFlight.cs:17805) hang rather than red; those cells need a bounded-time assertion.
- C-ghost-playback-013-02 needs an AST walk (no Microsoft.CodeAnalysis reference in Parsek.Tests today); regex over source is barred by the rubric, so it waits on a package decision.
- C-map-render-012-01 needs a small production observability addition (four resolver guards return bare false) before a test can discriminate guard removal.
- In-game-only rows (need a live Vessel/Part/Transform) are kept at P5 as the InGameTests backlog, not xUnit work.
- `july-crosswalk.csv` is built in Phase 4; `dupe_of_july_id` is filled there, not here.
