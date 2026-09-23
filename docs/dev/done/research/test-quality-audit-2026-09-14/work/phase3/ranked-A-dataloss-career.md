# Phase 3 ranked coverage proposals - partition A (data-loss / career)

66 candidates in: 66 kept, 0 already-covered, 0 invalid, 0 merged.
All 66 SUT guards were confirmed present at the pinned SHA (no line corrections needed).
Priority 1 = highest. Risk order data-loss > career; then feasibility direct > seam > generator > in-game.

## Cluster: catchall (2 kept, showing 2)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-catchall-049-01 | ChainSegmentManager.cs:1 | state-transition | seam | delete rec.MarkFilesDirty() after the continuation point append (ChainSegmentManager.cs:429) | M | 2 |
| C-catchall-022-01 | GameActions/KspStatePatcher.cs:3347 | state-transition | seam | empty the body of KspStatePatcher.ResetDrawdownGuardSessionLatches (delete the six latch assignments) | M | 3 |

## Cluster: ghost-playback (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-ghost-playback-023-01 | GameStateStore.cs:513 | integration | direct | delete the MilestoneStore.PurgeTaggedEvents call at GameStateStore.cs:513 (milestoneRemoved = empty list) | S | 2 |

## Cluster: harness-seam (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-harness-seam-020-01 | TestCommands/TestCommandKscAction.cs:164 | boundary | direct | change > to >= in the HireKerbal funds compare (TestCommandKscAction.cs:164) | S | 3 |

## Cluster: io-serialization (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-io-serialization-004-01 | FileIOUtils.cs:302 | negative | seam | delete the destination-preserving fallback in the File.Replace catch (let destPath be removed before the move-aside retry) | M | 2 |

## Cluster: ledger-career (34 kept, showing 10)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-ledger-career-001-01 | GameActions/KscActionExpectationClassifier.cs:139 | state-transition | direct | in the FacilityUpgrade arm use -action.FundsSpent (always 0) instead of -action.FacilityCost (KscActionExpectationClassifier.cs:146) | S | 2 |
| C-ledger-career-003-01 | GameActions/LedgerOrchestrator.cs:5775 | negative | direct | delete the evt.key == TechResearchScienceReasonKey filter at LedgerOrchestrator.cs:5775 | S | 2 |
| C-ledger-career-004-01 | GameActions/LedgerOrchestrator.cs:1721 | negative | direct | delete the // !string.IsNullOrEmpty(rec.EvaCrewName) conjunct at LedgerOrchestrator.cs:1721 | S | 2 |
| C-ledger-career-006-01 | GameActions/LedgerRecoveryFundsPairing.cs:591 | negative | direct | delete the delta <= 0 early return at LedgerRecoveryFundsPairing.cs:591 | S | 2 |
| C-ledger-career-007-01 | GameStateEvent.cs:205 | negative | direct | delete the observedDelta vs entryCost tolerance check at GameStateEvent.cs:205-207 | S | 2 |
| C-ledger-career-007-02 | GameStateEvent.cs:238 | negative | direct | return true on any nearby TechResearched with a non-empty parts list (drop the per-part name compare at GameStateEvent.cs:238) | S | 2 |
| C-ledger-career-008-01 | GameStateStore.cs:819 | negative | direct | drop action.ScienceAwarded <= 0f from the skip disjunct at GameStateStore.cs:819-822 | S | 2 |
| C-ledger-career-009-01 | GameActions/Ledger.cs:683 | negative | direct | make generationOk unconditionally true at Ledger.cs:683-685 | S | 2 |
| C-ledger-career-009-02 | GameActions/Ledger.cs:929 | state-transition | direct | delete if (before != actions.Count) BumpStateVersion(); at Ledger.cs:928-929 | S | 2 |
| C-ledger-career-011-01 | GameActions/GameStateEventConverter.cs:619 | negative | direct | delete the toLevel<1 clamp at GameStateEventConverter.cs:619-623 | S | 2 |

## Cluster: legacy-bugfix (4 kept, showing 4)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-legacy-bugfix-023-01 | RecordingStore.OrphanCleanup.cs:278 | negative | direct | delete the savedPendingTreeDuringActiveRestore contribution at RecordingStore.OrphanCleanup.cs:276-277 | S | 1 |
| C-legacy-bugfix-024-01 | RecordingSidecarStore.cs:63 | negative | direct | re-add File.Delete(vesselPath) when rec.VesselSnapshot is null (RecordingSidecarStore.cs:63 region) | S | 1 |
| C-legacy-bugfix-016-01 | MilestoneStore.cs:562 | negative | direct | delete if (!m.Committed) continue; at MilestoneStore.cs:568 | S | 2 |
| C-legacy-bugfix-008-01 | GameStateRecorder.Handlers.cs:856 | boundary | generator | return the bare id from QualifyMilestoneId (drop the body-name prefix) | M | 3 |

## Cluster: logistics-route (3 kept, showing 3)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-logistics-route-014-01 | Logistics/RouteOrchestrator.cs:5697 | mirror-direction | direct | drop the hasInventory term from the OR at RouteOrchestrator.cs:5703 | S | 2 |
| C-logistics-route-033-01 | Logistics/LiveDeliveryCapacityProbe.cs:561 | integration | seam | change the module-to-module continue to break in the ProbeUnloadedFirstEmpty walk | M | 3 |
| C-logistics-route-020-01 | Logistics/RouteRunCostCalculator.cs:257 | negative | direct | delete the treeRecordingIds / ELS recovery filter so a retired recovery row re-credits the run cost | M | 4 |

## Cluster: recorder-events (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-recorder-events-026-01 | BackgroundRecorder.cs:553 | integration | seam | require >= 3 controllable outputs before RewindPointAuthor.Begin (BackgroundRecorder.cs:896 branch) | L | 2 |

## Cluster: recording-tree (10 kept, showing 10)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-recording-tree-034-01 | RecordingStore.cs:4606 | negative | direct | delete // hasSavedPendingDuringActiveRestore from the play-mode guard at RecordingStore.cs:4608-4611 | S | 1 |
| C-recording-tree-021-02 | CrewReservationManager.cs:131 | state-transition | direct | delete rescuePlacedKerbals.Clear() in the null-roster arm at CrewReservationManager.cs:131 | S | 2 |
| C-recording-tree-032-01 | EffectiveState.cs:1500 | negative | direct | add a supersede filter to ComputeELS that skips actions tagged to a superseded recording (EffectiveState.cs:1496-1506) | S | 2 |
| C-recording-tree-051-02 | MilestoneStore.cs:626 | negative | direct | delete the IsEventVisibleToCurrentTimeline skip at MilestoneStore.cs:626 (and the facility twin at :652) | S | 2 |
| C-recording-tree-039-01 | RecordingStore.OrphanCleanup.cs:56 | integration | seam | delete the RewindSaveFileName limb at RecordingStore.OrphanCleanup.cs:56-57 | M | 2 |
| C-recording-tree-042-01 | SceneExitInterceptor.cs:565 | negative | seam | return true (warn) instead of false for GameScenes.MAINMENU in the catch at SceneExitInterceptor.cs:565-572 | M | 2 |
| C-recording-tree-047-02 | RecordingStore.cs:2495 | negative | direct | move the marker-owned bypass below the attempt-set / cutoff checks at RecordingStore.cs:2495 | M | 2 |
| C-recording-tree-045-01 | GameActions/LedgerOrchestrator.cs:4158 | negative | direct | add // type == GameStateEventType.ScienceChanged to IsIrreversibleLiveGameplayEvent (LedgerOrchestrator.cs:4160-4163) | S | 3 |
| C-recording-tree-033-01 | GameStateRecorder.Handlers.cs:144 | integration | seam | change ShouldForwardDirectLedgerEvent(evt.recordingId, ...) to ShouldForwardDirectLedgerEvent(ResolveCurrentRecordingTag(), ...) at Handlers.cs:144 | M | 3 |
| C-recording-tree-021-01 | KerbalsModule.cs:1566 | state-transition | direct | use slot.OwnerName unconditionally as replacedName (drop the i==0 ternary at KerbalsModule.cs:1566) | L | 3 |

## Cluster: rewind-refly (8 kept, showing 8)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-rewind-refly-011-01 | RewindPointReaper.cs:284 | state-transition | direct | delete the File.Delete(absolute) call at RewindPointReaper.cs:286 (log only) | S | 1 |
| C-rewind-refly-020-01 | ParsekScenario.cs:6733 | integration | direct | walk protoVessels forward with RemoveAt (i++ over the removal) at ParsekScenario.cs:6740 so consecutive strips are skipped | M | 1 |
| C-rewind-refly-006-01 | GameActions/LedgerOrchestrator.cs:2269 | boundary | direct | change candidate.ut < initialBaseline.ut to > at LedgerOrchestrator.cs:2269 (last-wins seed) | S | 2 |
| C-rewind-refly-016-01 | GameActions/TombstoneEligibility.cs:196 | boundary | direct | delete the source-agnostic timing arm at TombstoneEligibility.cs:195-200 (return false unless RepPenaltySource == KerbalDeath) | S | 2 |
| C-rewind-refly-016-02 | GameActions/TombstoneEligibility.cs:78 | mirror-direction | direct | delete the case GameActionType.StrategyScienceCredit: label at TombstoneEligibility.cs:78 so it falls to the preserve-unknown default | S | 2 |
| C-rewind-refly-017-01 | GameActions/KspStatePatcher.cs:3693 | mirror-direction | direct | delete the RewindReadbackGuard.AbortRewindPatchOnDivergence // operand at KspStatePatcher.cs:3693 | S | 2 |
| C-rewind-refly-020-02 | ParsekScenario.cs:4566 | state-transition | seam | delete the three pending-cleanup clears at ParsekScenario.cs:4566-4568 | S | 3 |
| C-rewind-refly-008-01 | RecordingTreeSplitter.cs:964 | mirror-direction | generator | change a.UT >= rewindUT to a.UT > rewindUT at RecordingTreeSplitter.cs:964 | L | 3 |

## Cluster: spawn-vessel (1 kept, showing 1)

| cand_id | SUT file:line | case kind | feasibility | mutant | effort | priority |
|---|---|---|---|---|---|---|
| C-spawn-vessel-021-01 | ParsekScenario.cs:3550 | state-transition | seam | drop the alreadyHasCleanupData guard at ParsekScenario.cs:3550 so the revert path overwrites rewind cleanup data with nulls | M | 2 |

