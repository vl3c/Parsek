using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class VesselSwitchTreeTests : System.IDisposable
    {
        public VesselSwitchTreeTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        /// <summary>
        /// Helper: creates a minimal RecordingTree with the given background vessel PIDs.
        /// </summary>
        private RecordingTree MakeTree(string activeRecId, params (uint pid, string recId)[] backgroundVessels)
        {
            var tree = new RecordingTree
            {
                Id = "tree_test",
                TreeName = "Test Tree",
                RootRecordingId = "rec_root",
                ActiveRecordingId = activeRecId
            };

            // Add active recording
            if (activeRecId != null)
            {
                tree.Recordings[activeRecId] = new Recording
                {
                    RecordingId = activeRecId,
                    VesselName = "Active Vessel",
                    ExplicitStartUT = 100.0,
                    ExplicitEndUT = 200.0
                };
            }

            // Add background recordings and populate BackgroundMap
            for (int i = 0; i < backgroundVessels.Length; i++)
            {
                var (pid, recId) = backgroundVessels[i];
                if (!tree.Recordings.ContainsKey(recId))
                {
                    tree.Recordings[recId] = new Recording
                    {
                        RecordingId = recId,
                        VesselName = $"Background Vessel {i}",
                        VesselPersistentId = pid,
                        ExplicitStartUT = 100.0,
                        ExplicitEndUT = 200.0
                    };
                }
                tree.BackgroundMap[pid] = recId;
            }

            return tree;
        }

        private static void AddTreeToCommittedStore(RecordingTree tree)
        {
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
        }

        #region DecideOnVesselSwitch with tree parameter

        [Fact]
        public void DecideOnVesselSwitch_NoTree_FallbackTransitionToBackground()
        {
            // No tree active, different PIDs -> TransitionToBackground (fallback)
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, 0, activeTree: null);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_TargetInBackgroundMap_ReturnsPromote()
        {
            var tree = MakeTree("rec_active", (200, "rec_bg"));

            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, 0, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.PromoteFromBackground, result);
        }

        [Fact]
        public void IdleSwitchToTrackedBackgroundMember_ArmsWatcherInsteadOfPromotingImmediately()
        {
            var tree = MakeTree("rec_active", (200, "rec_bg"));

            var treeDecision = FlightRecorder.DecideOnVesselSwitch(
                100,
                200,
                currentIsEva: false,
                recordingStartedAsEva: false,
                undockSiblingPid: 0,
                activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.PromoteFromBackground, treeDecision);

            bool shouldArm = ParsekFlight.ShouldArmPostSwitchAutoRecord(
                autoRecordOnFirstModificationAfterSwitchEnabled: true,
                isRecording: false,
                hasNewVessel: true,
                newVesselIsGhost: false,
                newVesselIsEva: false);
            var armDecision = ParsekFlight.EvaluatePostSwitchAutoRecordArmDecision(
                shouldArm,
                trackedInActiveTree: tree.BackgroundMap.ContainsKey(200));

            Assert.Equal(
                ParsekFlight.PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember,
                armDecision);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_TargetNotInTree_ReturnsTransition()
        {
            var tree = MakeTree("rec_active", (300, "rec_bg"));

            // PID 200 is not in the background map
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, 0, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_UndockSibling_TakesPriority()
        {
            var tree = MakeTree("rec_active", (200, "rec_bg"));

            // Even though PID 200 is in the background map, undock sibling takes priority
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, undockSiblingPid: 200, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.UndockSwitch, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_EvaToEva_TakesPriority()
        {
            var tree = MakeTree("rec_active", (200, "rec_bg"));

            // EVA-to-EVA when started as EVA -> ContinueOnEva (tree does not override)
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, currentIsEva: true, recordingStartedAsEva: true, 0, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ContinueOnEva, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_ChainToVessel_TakesPriority()
        {
            var tree = MakeTree("rec_active", (200, "rec_bg"));

            // Non-EVA when started as EVA -> ChainToVessel (tree does not override)
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, currentIsEva: false, recordingStartedAsEva: true, 0, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.ChainToVessel, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_SamePid_ReturnsNone()
        {
            var tree = MakeTree("rec_active");

            var result = FlightRecorder.DecideOnVesselSwitch(100, 100, false, false, 0, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.None, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeActive_EmptyBackgroundMap_ReturnsTransition()
        {
            // Tree is active but background map is empty
            var tree = MakeTree("rec_active");

            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, 0, activeTree: tree);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_TreeNull_DoesNotCrash()
        {
            // Explicit null tree -> fallback TransitionToBackground
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, 0, activeTree: null);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        #endregion

        #region BackgroundMap consistency

        [Fact]
        public void BackgroundMap_RebuiltCorrectly_AfterTransition()
        {
            var tree = new RecordingTree
            {
                Id = "tree_map",
                TreeName = "Map Test",
                RootRecordingId = "R1",
                ActiveRecordingId = "R1"
            };

            // R1 is active -> not in background
            tree.Recordings["R1"] = new Recording
            {
                RecordingId = "R1",
                VesselPersistentId = 100,
                TerminalStateValue = null
            };
            // R2 is background (not active, not terminated)
            tree.Recordings["R2"] = new Recording
            {
                RecordingId = "R2",
                VesselPersistentId = 200,
                TerminalStateValue = null
            };
            // R3 is terminated -> not in background
            tree.Recordings["R3"] = new Recording
            {
                RecordingId = "R3",
                VesselPersistentId = 300,
                TerminalStateValue = TerminalState.Destroyed
            };

            tree.RebuildBackgroundMap();

            // Only R2 should be in the background map
            Assert.Single(tree.BackgroundMap);
            Assert.True(tree.BackgroundMap.ContainsKey(200));
            Assert.Equal("R2", tree.BackgroundMap[200]);

            // Simulate transition: R1 goes to background, R2 becomes active
            tree.ActiveRecordingId = "R2";
            tree.RebuildBackgroundMap();

            // Now R1 should be in background, R2 should not (it's active)
            Assert.Single(tree.BackgroundMap);
            Assert.True(tree.BackgroundMap.ContainsKey(100));
            Assert.Equal("R1", tree.BackgroundMap[100]);
            Assert.False(tree.BackgroundMap.ContainsKey(200));
        }

        #endregion

        #region Committed spawned-vessel restore

        [Fact]
        public void TryFindCommittedTreeForSpawnedVessel_PrefersLatestRestorableRecordingForSpawnedPid()
        {
            var tree = MakeTree("rec_other");
            tree.Recordings["rec_old"] = new Recording
            {
                RecordingId = "rec_old",
                VesselName = "Old Chain Segment",
                VesselPersistentId = 20,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 200,
                ChainId = "chain",
                ChainIndex = 0,
                TreeOrder = 0,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 150.0
            };
            tree.Recordings["rec_tip"] = new Recording
            {
                RecordingId = "rec_tip",
                VesselName = "Current Chain Tip",
                VesselPersistentId = 30,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 200,
                ChainId = "chain",
                ChainIndex = 1,
                TreeOrder = 1,
                ExplicitStartUT = 151.0,
                ExplicitEndUT = 200.0
            };

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                activeVesselPid: 200,
                out RecordingTree matchedTree,
                out string matchedRecordingId);

            Assert.True(found);
            Assert.Same(tree, matchedTree);
            Assert.Equal("rec_tip", matchedRecordingId);
        }

        [Fact]
        public void TryFindCommittedTreeForSpawnedVessel_PrefersNewestCommittedTreeAcrossList()
        {
            var olderTree = MakeTree("old_active");
            olderTree.Id = "tree_old";
            olderTree.Recordings["old_tip"] = new Recording
            {
                RecordingId = "old_tip",
                VesselName = "Older Tip",
                VesselPersistentId = 10,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 200,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 150.0,
                TreeOrder = 0
            };

            var newerTree = MakeTree("new_active");
            newerTree.Id = "tree_new";
            newerTree.Recordings["new_tip"] = new Recording
            {
                RecordingId = "new_tip",
                VesselName = "Newer Tip",
                VesselPersistentId = 20,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 200,
                ExplicitStartUT = 151.0,
                ExplicitEndUT = 200.0,
                TreeOrder = 0
            };

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { olderTree, newerTree },
                activeVesselPid: 200,
                out RecordingTree matchedTree,
                out string matchedRecordingId);

            Assert.True(found);
            Assert.Same(newerTree, matchedTree);
            Assert.Equal("new_tip", matchedRecordingId);
        }

        [Fact]
        public void TryFindCommittedTreeForSpawnedVessel_AdoptedSourcePidMatchesSourceVessel()
        {
            var tree = MakeTree("rec_active");
            tree.Recordings["rec_active"].VesselPersistentId = 12345;
            tree.Recordings["rec_active"].VesselSpawned = true;
            tree.Recordings["rec_active"].SpawnedVesselPersistentId = 12345;

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                activeVesselPid: 12345,
                out RecordingTree matchedTree,
                out string matchedRecordingId);

            Assert.True(found);
            Assert.Same(tree, matchedTree);
            Assert.Equal("rec_active", matchedRecordingId);
        }

        [Fact]
        public void TryFindCommittedTreeForSpawnedVessel_MatchesTreeReloadedFromConfigNode()
        {
            // Regression for the v0.8.3 playtest bug: after Save → Load, the
            // scene-enter restore lookup used to miss because VesselSpawned was
            // not re-derived from the persisted spawnedPid. The full pipeline
            // covered here (tree save → ConfigNode → tree load → lookup) is the
            // realistic path that fires on every KSC → FLIGHT scene change.
            var tree = new RecordingTree
            {
                Id = "tree_reload",
                TreeName = "Reload Test",
                RootRecordingId = "rec_reload",
                ActiveRecordingId = "rec_reload"
            };
            tree.Recordings["rec_reload"] = new Recording
            {
                RecordingId = "rec_reload",
                VesselName = "Crater Crawler",
                VesselPersistentId = 4206290288u,
                SpawnedVesselPersistentId = 4206290288u,
                VesselSpawned = true,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 25.9,
                TreeId = "tree_reload"
            };

            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            var reloaded = RecordingTree.Load(node);

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { reloaded },
                activeVesselPid: 4206290288u,
                out RecordingTree matchedTree,
                out string matchedRecordingId);

            Assert.True(found,
                "After Save → Load the restore-lookup must still match the spawned pid; " +
                "if this fails, the VesselSpawned invariant is not being rebuilt on load.");
            Assert.Same(reloaded, matchedTree);
            Assert.Equal("rec_reload", matchedRecordingId);
        }

        [Fact]
        public void PrepareCommittedTreeRestoreForSpawnedVessel_BackgroundTarget_UsesSpawnedPidAndClearsStaleBackgroundEntry()
        {
            var tree = MakeTree("rec_active", (20, "rec_tip"));
            tree.Recordings["rec_active"].VesselPersistentId = 100;
            tree.Recordings["rec_active"].VesselSpawned = true;
            tree.Recordings["rec_active"].SpawnedVesselPersistentId = 100;
            tree.Recordings["rec_tip"].VesselSpawned = true;
            tree.Recordings["rec_tip"].SpawnedVesselPersistentId = 200;
            tree.Recordings["rec_tip"].TerminalStateValue = TerminalState.Orbiting;

            var action = ParsekFlight.PrepareCommittedTreeRestoreForSpawnedVessel(
                tree,
                targetRecordingId: "rec_tip",
                activeVesselPid: 200);

            Assert.Equal(
                ParsekFlight.CommittedSpawnedVesselRestoreAction.PromoteFromBackground,
                action);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Equal("rec_active", tree.BackgroundMap[100]);
            Assert.False(tree.BackgroundMap.ContainsKey(20));
            Assert.DoesNotContain("rec_tip", tree.BackgroundMap.Values);
        }

        [Fact]
        public void PrepareCommittedTreeRestoreForSpawnedVessel_ActiveTarget_ResumesAndClearsStaleBackgroundEntry()
        {
            var tree = MakeTree("rec_active", (300, "rec_bg"));
            tree.Recordings["rec_active"].VesselPersistentId = 100;
            tree.Recordings["rec_active"].VesselSpawned = true;
            tree.Recordings["rec_active"].SpawnedVesselPersistentId = 200;
            tree.BackgroundMap[50] = "rec_active";

            var action = ParsekFlight.PrepareCommittedTreeRestoreForSpawnedVessel(
                tree,
                targetRecordingId: "rec_active",
                activeVesselPid: 200);

            Assert.Equal(
                ParsekFlight.CommittedSpawnedVesselRestoreAction.ResumeActiveRecording,
                action);
            Assert.Equal("rec_active", tree.ActiveRecordingId);
            Assert.Single(tree.BackgroundMap);
            Assert.Equal("rec_bg", tree.BackgroundMap[300]);
            Assert.DoesNotContain("rec_active", tree.BackgroundMap.Values);
        }

        [Fact]
        public void TryTakeCommittedTreeForSpawnedVesselRestore_DetachesTreeAndAllowsRecommit()
        {
            var tree = MakeTree("rec_active", (20, "rec_tip"));
            tree.Id = "tree_restore";
            tree.RootRecordingId = "rec_active";

            tree.Recordings["rec_active"].TreeId = tree.Id;
            tree.Recordings["rec_active"].VesselPersistentId = 100;
            tree.Recordings["rec_active"].VesselSpawned = true;
            tree.Recordings["rec_active"].SpawnedVesselPersistentId = 100;

            tree.Recordings["rec_tip"].TreeId = tree.Id;
            tree.Recordings["rec_tip"].VesselPersistentId = 20;
            tree.Recordings["rec_tip"].VesselSpawned = true;
            tree.Recordings["rec_tip"].SpawnedVesselPersistentId = 200;
            tree.Recordings["rec_tip"].TerminalStateValue = TerminalState.Orbiting;

            AddTreeToCommittedStore(tree);

            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                activeVesselPid: 200,
                out RecordingTree liveTree,
                out string matchedRecordingId,
                out ParsekFlight.CommittedSpawnedVesselRestoreAction action);

            Assert.True(taken);
            Assert.Same(tree, liveTree);
            Assert.Equal("rec_tip", matchedRecordingId);
            Assert.Equal(
                ParsekFlight.CommittedSpawnedVesselRestoreAction.PromoteFromBackground,
                action);
            Assert.Empty(RecordingStore.CommittedTrees);
            Assert.Empty(RecordingStore.CommittedRecordings);

            RecordingStore.CommitTree(liveTree);

            Assert.Single(RecordingStore.CommittedTrees);
            Assert.Equal(tree.Id, RecordingStore.CommittedTrees[0].Id);
            Assert.Equal(liveTree.Recordings.Count, RecordingStore.CommittedRecordings.Count);
        }

        [Fact]
        public void TryTakeCommittedTreeForSpawnedVesselRestore_ClearsPriorSpawnFlagsAndKeepsLivePid()
        {
            // Regression for v0.8.3 post-fix playtest: after the detach-for-resume
            // lands a committed tree back into the active slot, the target recording's
            // prior-commit VesselSpawned / SpawnedVesselPersistentId must be cleared.
            // Otherwise the merge dialog's CanPersistVessel → ShouldSpawnAtRecordingEnd
            // chain sees "already spawned (VesselSpawned=true)" and defaults the leaf
            // to ghost-only, which nulls the VesselSnapshot at commit time and breaks
            // subsequent KSC spawns with "no vessel snapshot".
            var tree = MakeTree("rec_resume");
            tree.Id = "tree_clear_flags";
            tree.RootRecordingId = "rec_resume";
            tree.ActiveRecordingId = "rec_resume";

            tree.Recordings["rec_resume"].TreeId = tree.Id;
            tree.Recordings["rec_resume"].VesselPersistentId = 11111;
            tree.Recordings["rec_resume"].VesselSpawned = true;
            tree.Recordings["rec_resume"].SpawnedVesselPersistentId = 12345;
            tree.Recordings["rec_resume"].TerminalStateValue = TerminalState.Landed;

            AddTreeToCommittedStore(tree);

            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                activeVesselPid: 12345,
                out RecordingTree liveTree,
                out string matchedRecordingId,
                out ParsekFlight.CommittedSpawnedVesselRestoreAction action);

            Assert.True(taken);
            Assert.Equal("rec_resume", matchedRecordingId);
            Assert.Equal(
                ParsekFlight.CommittedSpawnedVesselRestoreAction.ResumeActiveRecording,
                action);

            Recording resumed = liveTree.Recordings["rec_resume"];
            Assert.False(resumed.VesselSpawned,
                "VesselSpawned must reset on detach so CanPersistVessel returns true at re-commit.");
            Assert.Equal(0u, resumed.SpawnedVesselPersistentId);
            Assert.Equal(12345u, resumed.VesselPersistentId);
        }

        [Fact]
        public void IsCommittedSpawnedRecordingRestorable_RejectsTerminalStates()
        {
            TerminalState[] blockedStates =
            {
                TerminalState.Destroyed,
                TerminalState.Recovered,
                TerminalState.Docked,
                TerminalState.Boarded
            };

            for (int i = 0; i < blockedStates.Length; i++)
            {
                var tree = MakeTree("rec_other");
                var rec = new Recording
                {
                    RecordingId = "rec_tip",
                    VesselPersistentId = 20,
                    VesselSpawned = true,
                    SpawnedVesselPersistentId = 200,
                    TerminalStateValue = blockedStates[i]
                };

                tree.Recordings["rec_tip"] = rec;
                Assert.False(ParsekFlight.IsCommittedSpawnedRecordingRestorable(tree, rec));
            }
        }

        [Fact]
        public void IsCommittedSpawnedRecordingRestorable_RejectsMidChainSegment()
        {
            var tree = MakeTree("rec_other");
            var rec = new Recording
            {
                RecordingId = "rec_mid",
                VesselPersistentId = 20,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 200,
                ChainId = "chain",
                ChainIndex = 0,
                ChainBranch = 0
            };
            var next = new Recording
            {
                RecordingId = "rec_tip",
                VesselPersistentId = 21,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 201,
                ChainId = "chain",
                ChainIndex = 1,
                ChainBranch = 0
            };

            tree.Recordings[rec.RecordingId] = rec;
            tree.Recordings[next.RecordingId] = next;

            Assert.False(ParsekFlight.IsCommittedSpawnedRecordingRestorable(tree, rec));
        }

        #endregion

        #region Fresh-launch restore guard

        // Regression for the relaunch tree-collision bug: the previous mission's
        // committed recording carries a stale SpawnedVesselPersistentId that KSP's
        // deterministic craft-derived persistentId reuses on the next VAB launch.
        // The guard is two pieces:
        //   1. IsFreshLaunchStartupBehaviour gates the Start-time capture on
        //      FlightDriver.StartupBehaviour (KSP's scene-startup mode, stable
        //      for the entire scene — no missionTime expiry, no event race).
        //   2. ShouldSkipCommittedTreeRestoreForFreshLaunch is a pure pid match
        //      against the captured rollout vessel pid. The identity component
        //      is what keeps mid-scene switches to other already-spawned
        //      committed vessels working: their pid won't match the captured
        //      rollout pid, so their committed-tree restore proceeds.
        // See logs/2026-05-13_1850_kerbal-x-merge-bug; KSP.log line 53466 shows
        // the VAB craft loader ("Loading ship from file: ...Auto-Saved Ship.craft")
        // running through FlightDriver's NEW_FROM_FILE branch.

        [Fact]
        public void IsFreshLaunchStartupBehaviour_TrueForNewFromFile()
        {
            // VAB/SPH Launch button.
            Assert.True(ParsekFlight.IsFreshLaunchStartupBehaviour(
                FlightDriver.StartupBehaviours.NEW_FROM_FILE));
        }

        [Fact]
        public void IsFreshLaunchStartupBehaviour_TrueForNewFromCraftNode()
        {
            // Mission Builder / scenario inline-craft launch path.
            Assert.True(ParsekFlight.IsFreshLaunchStartupBehaviour(
                FlightDriver.StartupBehaviours.NEW_FROM_CRAFT_NODE));
        }

        [Fact]
        public void IsFreshLaunchStartupBehaviour_FalseForResumeSavedFile()
        {
            // Tracking station load, F9 quickload, or any .sfs-based resume.
            Assert.False(ParsekFlight.IsFreshLaunchStartupBehaviour(
                FlightDriver.StartupBehaviours.RESUME_SAVED_FILE));
        }

        [Fact]
        public void IsFreshLaunchStartupBehaviour_FalseForResumeSavedCache()
        {
            // Revert to launch or other GameBackup cache restore.
            Assert.False(ParsekFlight.IsFreshLaunchStartupBehaviour(
                FlightDriver.StartupBehaviours.RESUME_SAVED_CACHE));
        }

        [Fact]
        public void ShouldSkipCommittedTreeRestoreForFreshLaunch_SkipsForCapturedRolloutPid()
        {
            // The fresh-launch vessel coming back through the dispatcher in a
            // NEW_FROM_FILE scene: pid matches the captured scene-entry pid → skip.
            Assert.True(ParsekFlight.ShouldSkipCommittedTreeRestoreForFreshLaunch(
                activeVesselPid: 2708531065u,
                freshRolloutVesselPid: 2708531065u));
        }

        [Fact]
        public void ShouldSkipCommittedTreeRestoreForFreshLaunch_AllowsDifferentVesselSwitchedToInSameScene()
        {
            // P2 regression: in a NEW_FROM_FILE scene, the player switches from
            // the freshly-launched craft (pid X) to an already-spawned committed
            // vessel (pid Y). The guard must skip ONLY pid X, not every pid.
            // Without this, auto-record-disabled players could never resume a
            // legitimate committed recording in a fresh-launch scene.
            Assert.False(ParsekFlight.ShouldSkipCommittedTreeRestoreForFreshLaunch(
                activeVesselPid: 99999u,
                freshRolloutVesselPid: 2708531065u));
        }

        [Fact]
        public void ShouldSkipCommittedTreeRestoreForFreshLaunch_AllowsWhenNoRolloutCaptured()
        {
            // RESUME_SAVED_FILE / RESUME_SAVED_CACHE scene: the capture step
            // skipped, freshRolloutVesselPid is 0, and the active vessel keeps
            // its committed recording restore path.
            Assert.False(ParsekFlight.ShouldSkipCommittedTreeRestoreForFreshLaunch(
                activeVesselPid: 2708531065u,
                freshRolloutVesselPid: 0u));
        }

        [Fact]
        public void ShouldSkipCommittedTreeRestoreForFreshLaunch_AllowsWhenActivePidZero()
        {
            // No active vessel yet — the existing activeVesselPid==0 short-circuit
            // in the dispatcher handles this uniformly, but the helper still
            // returns false defensively so a stray call without active vessel
            // doesn't suppress a future legitimate restore.
            Assert.False(ParsekFlight.ShouldSkipCommittedTreeRestoreForFreshLaunch(
                activeVesselPid: 0u,
                freshRolloutVesselPid: 2708531065u));
        }

        [Fact]
        public void ShouldSkipCommittedTreeRestoreForFreshLaunch_AllowsWhenBothZero()
        {
            Assert.False(ParsekFlight.ShouldSkipCommittedTreeRestoreForFreshLaunch(
                activeVesselPid: 0u,
                freshRolloutVesselPid: 0u));
        }

        [Fact]
        public void TryFindCommittedTreeForSpawnedVessel_StillMatchesAfterFreshLaunchGuardLayer()
        {
            // The fresh-launch guard lives at the instance dispatcher in
            // TryRestoreCommittedTreeForSpawnedActiveVessel, not in the static
            // lookup. The lookup must keep returning the matching tree so any
            // non-fresh-launch caller (background promotion, missed-switch
            // recovery for a loaded vessel) keeps working.
            var tree = MakeTree("rec_active");
            tree.Recordings["rec_active"].VesselPersistentId = 2708531065u;
            tree.Recordings["rec_active"].VesselSpawned = true;
            tree.Recordings["rec_active"].SpawnedVesselPersistentId = 2708531065u;

            bool found = ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                new List<RecordingTree> { tree },
                activeVesselPid: 2708531065u,
                out RecordingTree matchedTree,
                out string matchedRecordingId);

            Assert.True(found);
            Assert.Same(tree, matchedTree);
            Assert.Equal("rec_active", matchedRecordingId);
        }

        #endregion

        #region Existing tests still pass with default activeTree parameter

        [Fact]
        public void DecideOnVesselSwitch_LegacyCallsite_NoTreeParam_StillWorks()
        {
            // This verifies that existing callers using the old 5-parameter signature
            // still compile and produce correct results (activeTree defaults to null)
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_LegacyCallsite_WithUndock_StillWorks()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.UndockSwitch, result);
        }

        #endregion
    }
}
