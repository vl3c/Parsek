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
