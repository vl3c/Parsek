using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RestorePointTests
    {
        public RestorePointTests()
        {
            RestorePointStore.SuppressLogging = true;
            RestorePointStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void SerializeRoundTrip()
        {
            var rp = new RestorePoint(true)
            {
                Id = "abc123def456",
                UT = 17030.5,
                SaveFileName = "parsek_rp_abc123",
                Label = "\"Flea Rocket\" launch (1 recording)",
                RecordingCount = 1,
                Funds = 47500.0,
                Science = 12.75,
                Reputation = 8.5f,
                ReservedFundsAtSave = 15000.0,
                ReservedScienceAtSave = 3.25,
                ReservedRepAtSave = 2.0f,
                SaveFileExists = true
            };

            var parent = new ConfigNode("TEST");
            rp.SerializeInto(parent);

            ConfigNode rpNode = parent.GetNode("RESTORE_POINT");
            Assert.NotNull(rpNode);

            RestorePoint loaded = RestorePoint.DeserializeFrom(rpNode);

            Assert.Equal(rp.Id, loaded.Id);
            Assert.Equal(rp.UT, loaded.UT);
            Assert.Equal(rp.SaveFileName, loaded.SaveFileName);
            Assert.Equal(rp.Label, loaded.Label);
            Assert.Equal(rp.RecordingCount, loaded.RecordingCount);
            Assert.Equal(rp.Funds, loaded.Funds);
            Assert.Equal(rp.Science, loaded.Science);
            Assert.Equal(rp.Reputation, loaded.Reputation);
            Assert.Equal(rp.ReservedFundsAtSave, loaded.ReservedFundsAtSave);
            Assert.Equal(rp.ReservedScienceAtSave, loaded.ReservedScienceAtSave);
            Assert.Equal(rp.ReservedRepAtSave, loaded.ReservedRepAtSave);
        }

        [Fact]
        public void SerializeRoundTrip_ExtremeDoubles()
        {
            var rp = new RestorePoint(true)
            {
                Id = "extreme_test",
                UT = 1e15,
                SaveFileName = "parsek_rp_extreme",
                Label = "extreme test",
                RecordingCount = 999999,
                Funds = -1e12,
                Science = 1e-10,
                Reputation = -999.999f,
                ReservedFundsAtSave = double.MaxValue / 2,
                ReservedScienceAtSave = double.MinValue / 2,
                ReservedRepAtSave = float.MaxValue / 2,
                SaveFileExists = true
            };

            var parent = new ConfigNode("TEST");
            rp.SerializeInto(parent);

            ConfigNode rpNode = parent.GetNode("RESTORE_POINT");
            RestorePoint loaded = RestorePoint.DeserializeFrom(rpNode);

            Assert.Equal(rp.UT, loaded.UT);
            Assert.Equal(rp.Funds, loaded.Funds);
            Assert.Equal(rp.Science, loaded.Science);
            Assert.Equal(rp.Reputation, loaded.Reputation);
            Assert.Equal(rp.ReservedFundsAtSave, loaded.ReservedFundsAtSave);
            Assert.Equal(rp.ReservedScienceAtSave, loaded.ReservedScienceAtSave);
            Assert.Equal(rp.ReservedRepAtSave, loaded.ReservedRepAtSave);
            Assert.Equal(rp.RecordingCount, loaded.RecordingCount);
        }

        [Fact]
        public void LabelGeneration_Standalone()
        {
            string label = RestorePointStore.BuildLabel("Flea Rocket", 3, false);
            Assert.Equal("\"Flea Rocket\" launch (3 recordings)", label);
        }

        [Fact]
        public void LabelGeneration_Tree()
        {
            string label = RestorePointStore.BuildLabel("Mun Lander", 5, true);
            Assert.Equal("\"Mun Lander\" tree launch (5 recordings)", label);
        }

        [Fact]
        public void LabelGeneration_SingleRecording()
        {
            string label = RestorePointStore.BuildLabel("Test Vessel", 1, false);
            Assert.Equal("\"Test Vessel\" launch (1 recording)", label);
        }

        [Fact]
        public void SaveFileNameGeneration()
        {
            string name = RestorePointStore.RestorePointSaveName("abc123");
            Assert.Equal("parsek_rp_abc123", name);
        }

        [Fact]
        public void ResetForTesting_ClearsAll()
        {
            // Add a restore point
            var rp = new RestorePoint(true)
            {
                Id = "test_reset",
                UT = 100,
                SaveFileName = "parsek_rp_test",
                Label = "test",
                RecordingCount = 1,
                Funds = 50000,
                SaveFileExists = true
            };
            RestorePointStore.AddForTesting(rp);

            // Set pending launch save
            RestorePointStore.pendingLaunchSave = new PendingLaunchSave
            {
                SaveFileName = "pending_save",
                UT = 200,
                Funds = 40000
            };

            // Set go-back flags
            RestorePointStore.IsGoingBack = true;
            RestorePointStore.GoBackUT = 300;
            RestorePointStore.GoBackReserved = new ResourceBudget.BudgetSummary
            {
                reservedFunds = 10000,
                reservedScience = 5,
                reservedReputation = 2
            };

            // Verify state is set
            Assert.True(RestorePointStore.HasRestorePoints);
            Assert.True(RestorePointStore.HasPendingLaunchSave);
            Assert.True(RestorePointStore.IsGoingBack);

            // Reset
            RestorePointStore.ResetForTesting();

            // Verify all cleared
            Assert.False(RestorePointStore.HasRestorePoints);
            Assert.Empty(RestorePointStore.RestorePoints);
            Assert.False(RestorePointStore.HasPendingLaunchSave);
            Assert.Null(RestorePointStore.pendingLaunchSave);
            Assert.False(RestorePointStore.IsGoingBack);
            Assert.Equal(0, RestorePointStore.GoBackUT);
            Assert.Equal(0, RestorePointStore.GoBackReserved.reservedFunds);
            Assert.Equal(0, RestorePointStore.GoBackReserved.reservedScience);
            Assert.Equal(0, RestorePointStore.GoBackReserved.reservedReputation);
        }

        [Fact]
        public void BuildRestorePointsRelativePath_CorrectPath()
        {
            string path = RecordingPaths.BuildRestorePointsRelativePath().Replace('\\', '/');
            Assert.Equal("Parsek/GameState/restore_points.pgrp", path);
        }

        #region File I/O Tests (ConfigNode round-trip, no KSP runtime)

        private RestorePoint MakeTestRP(string id, double ut, string saveFile, string label,
            int recCount = 1, double funds = 50000, double science = 5, float rep = 2,
            double resFunds = 10000, double resSci = 1, float resRep = 0.5f)
        {
            return new RestorePoint(true)
            {
                Id = id,
                UT = ut,
                SaveFileName = saveFile,
                Label = label,
                RecordingCount = recCount,
                Funds = funds,
                Science = science,
                Reputation = rep,
                ReservedFundsAtSave = resFunds,
                ReservedScienceAtSave = resSci,
                ReservedRepAtSave = resRep,
                SaveFileExists = true
            };
        }

        [Fact]
        public void MetadataNodeRoundTrip()
        {
            // Add 3 restore points
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_aaa111", 17030, "parsek_rp_aaa111",
                "\"Flea\" launch (1 recording)", 1, 47500, 12.75, 8.5f, 15000, 3.25, 2.0f));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_bbb222", 17060, "parsek_rp_bbb222",
                "\"Hopper\" launch (2 recordings)", 2, 43200, 5, 2, 12000, 2.5, 1.0f));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_ccc333", 17090, "parsek_rp_ccc333",
                "\"Orbiter\" launch (3 recordings)", 3, 38000, 0, 0, 8000, 0, 0));

            // Serialize to ConfigNode
            ConfigNode node = RestorePointStore.SerializeAllToNode();

            // Verify structure
            Assert.Equal(3, node.GetNodes("RESTORE_POINT").Length);

            // Clear and deserialize
            RestorePointStore.ResetForTesting();
            Assert.Empty(RestorePointStore.RestorePoints);

            RestorePointStore.DeserializeAllFromNode(node);

            // Verify all 3 restored
            Assert.Equal(3, RestorePointStore.RestorePoints.Count);

            Assert.Equal("rp_aaa111", RestorePointStore.RestorePoints[0].Id);
            Assert.Equal(17030, RestorePointStore.RestorePoints[0].UT);
            Assert.Equal("parsek_rp_aaa111", RestorePointStore.RestorePoints[0].SaveFileName);
            Assert.Equal("\"Flea\" launch (1 recording)", RestorePointStore.RestorePoints[0].Label);
            Assert.Equal(1, RestorePointStore.RestorePoints[0].RecordingCount);
            Assert.Equal(47500, RestorePointStore.RestorePoints[0].Funds);
            Assert.Equal(12.75, RestorePointStore.RestorePoints[0].Science);
            Assert.Equal(8.5f, RestorePointStore.RestorePoints[0].Reputation);
            Assert.Equal(15000, RestorePointStore.RestorePoints[0].ReservedFundsAtSave);
            Assert.Equal(3.25, RestorePointStore.RestorePoints[0].ReservedScienceAtSave);
            Assert.Equal(2.0f, RestorePointStore.RestorePoints[0].ReservedRepAtSave);

            Assert.Equal("rp_bbb222", RestorePointStore.RestorePoints[1].Id);
            Assert.Equal(17060, RestorePointStore.RestorePoints[1].UT);
            Assert.Equal(2, RestorePointStore.RestorePoints[1].RecordingCount);

            Assert.Equal("rp_ccc333", RestorePointStore.RestorePoints[2].Id);
            Assert.Equal(17090, RestorePointStore.RestorePoints[2].UT);
            Assert.Equal(3, RestorePointStore.RestorePoints[2].RecordingCount);
        }

        [Fact]
        public void RemoveRestorePointFromList_RemovesMiddleElement()
        {
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_first", 100, "parsek_rp_first", "first"));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_middle", 200, "parsek_rp_middle", "middle"));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_last", 300, "parsek_rp_last", "last"));

            Assert.Equal(3, RestorePointStore.RestorePoints.Count);

            bool removed = RestorePointStore.RemoveRestorePointFromList("rp_middle");

            Assert.True(removed);
            Assert.Equal(2, RestorePointStore.RestorePoints.Count);
            Assert.Equal("rp_first", RestorePointStore.RestorePoints[0].Id);
            Assert.Equal("rp_last", RestorePointStore.RestorePoints[1].Id);
        }

        [Fact]
        public void RemoveRestorePointFromList_NonexistentId_ReturnsFalse()
        {
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_only", 100, "parsek_rp_only", "only"));

            bool removed = RestorePointStore.RemoveRestorePointFromList("nonexistent");

            Assert.False(removed);
            Assert.Equal(1, RestorePointStore.RestorePoints.Count);
        }

        [Fact]
        public void ClearAllInMemory_EmptiesListAndPending()
        {
            // Add 3 restore points
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_1", 100, "parsek_rp_1", "first"));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_2", 200, "parsek_rp_2", "second"));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_3", 300, "parsek_rp_3", "third"));

            // Set a pending launch save
            RestorePointStore.pendingLaunchSave = new PendingLaunchSave
            {
                SaveFileName = "pending_save",
                UT = 400,
                Funds = 30000
            };

            Assert.Equal(3, RestorePointStore.RestorePoints.Count);
            Assert.True(RestorePointStore.HasRestorePoints);
            Assert.True(RestorePointStore.HasPendingLaunchSave);

            RestorePointStore.ClearAllInMemory();

            Assert.Equal(0, RestorePointStore.RestorePoints.Count);
            Assert.False(RestorePointStore.HasRestorePoints);
            Assert.False(RestorePointStore.HasPendingLaunchSave);
        }

        // Test 11: LoadGuard_OnlyOncePerSave
        // This test requires KSP runtime (HighLogic.SaveFolder, file system).
        // The load guard pattern (initialLoadDone + lastSaveFolder) is tested
        // in integration by verifying that LoadRestorePointFile returns true
        // on second call without re-reading the file. The pattern is identical
        // to MilestoneStore.LoadMilestoneFile which is proven in production.

        [Fact]
        public void MissingSaveFile_MarkedUnavailable()
        {
            // Create a restore point via ConfigNode
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_exists", 100, "parsek_rp_exists", "exists"));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_missing", 200, "parsek_rp_missing", "missing"));
            RestorePointStore.AddForTesting(MakeTestRP(
                "rp_also_exists", 300, "parsek_rp_also_exists", "also exists"));

            // Serialize to node
            ConfigNode node = RestorePointStore.SerializeAllToNode();

            // Create a temp directory simulating saves/<saveName>
            string tempDir = Path.Combine(Path.GetTempPath(), "parsek_test_saves_" + System.Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            try
            {
                // Create .sfs files for the first and third RP only
                File.WriteAllText(Path.Combine(tempDir, "parsek_rp_exists.sfs"), "dummy");
                File.WriteAllText(Path.Combine(tempDir, "parsek_rp_also_exists.sfs"), "dummy");
                // parsek_rp_missing.sfs intentionally NOT created

                // Deserialize with validation
                RestorePointStore.ResetForTesting();
                RestorePointStore.DeserializeAllFromNode(node, tempDir);

                Assert.Equal(3, RestorePointStore.RestorePoints.Count);

                // First and third should have SaveFileExists = true (default)
                Assert.True(RestorePointStore.RestorePoints[0].SaveFileExists);
                Assert.Equal("rp_exists", RestorePointStore.RestorePoints[0].Id);

                // Second should be marked as missing
                Assert.False(RestorePointStore.RestorePoints[1].SaveFileExists);
                Assert.Equal("rp_missing", RestorePointStore.RestorePoints[1].Id);

                // Third should exist
                Assert.True(RestorePointStore.RestorePoints[2].SaveFileExists);
                Assert.Equal("rp_also_exists", RestorePointStore.RestorePoints[2].Id);
            }
            finally
            {
                // Cleanup temp directory
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public void DeserializeSkipsInvalidFileNames()
        {
            var root = new ConfigNode("RESTORE_POINTS");

            // Valid RP
            var validRp = new RestorePoint(true)
            {
                Id = "valid1",
                SaveFileName = "parsek_rp_abc123",
                UT = 100,
                Label = "test"
            };
            validRp.SerializeInto(root);

            // RP with path traversal in save file name
            var badNode = root.AddNode("RESTORE_POINT");
            badNode.AddValue("id", "bad1");
            badNode.AddValue("saveFile", "../../../hack");
            badNode.AddValue("ut", "200");
            badNode.AddValue("label", "bad");

            // RP with backslash
            var badNode2 = root.AddNode("RESTORE_POINT");
            badNode2.AddValue("id", "bad2");
            badNode2.AddValue("saveFile", "hack\\path");
            badNode2.AddValue("ut", "300");
            badNode2.AddValue("label", "bad2");

            RestorePointStore.DeserializeAllFromNode(root);

            Assert.Equal(1, RestorePointStore.RestorePoints.Count);
            Assert.Equal("valid1", RestorePointStore.RestorePoints[0].Id);
        }

        [Fact]
        public void DeserializeEmptyNode()
        {
            var node = new ConfigNode("RESTORE_POINTS");

            RestorePointStore.DeserializeAllFromNode(node);

            Assert.Empty(RestorePointStore.RestorePoints);
        }

        [Fact]
        public void SerializeEmptyList()
        {
            ConfigNode node = RestorePointStore.SerializeAllToNode();

            Assert.Equal("RESTORE_POINTS", node.name);
            Assert.Empty(node.GetNodes("RESTORE_POINT"));
        }

        #endregion

        #region Helpers

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0, longitude = 0, altitude = 100,
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        /// <summary>
        /// Commits a recording with the given name and startUT via StashPending+CommitPending.
        /// Returns the committed recording for further manipulation.
        /// </summary>
        private RecordingStore.Recording CommitRecording(string name, double startUT, int pointCount = 5)
        {
            RecordingStore.StashPending(MakePoints(pointCount, startUT), name);
            RecordingStore.CommitPending();
            var list = RecordingStore.CommittedRecordings;
            return list[list.Count - 1];
        }

        #endregion

        #region Playback State Reset Tests

        [Fact]
        public void ResetAllPlaybackState_Standalone()
        {
            // Create 5 standalone recordings with various states set
            var rec1 = CommitRecording("Ship1", 100);
            rec1.VesselSpawned = true;
            rec1.SpawnAttempts = 2;
            rec1.SpawnedVesselPersistentId = 12345;
            rec1.LastAppliedResourceIndex = 3;
            rec1.TakenControl = true;
            rec1.SceneExitSituation = 32; // ORBITING

            var rec2 = CommitRecording("Ship2", 200);
            rec2.VesselSpawned = true;
            rec2.LastAppliedResourceIndex = 4;

            var rec3 = CommitRecording("Ship3", 300);
            rec3.SpawnAttempts = 3;
            rec3.SpawnedVesselPersistentId = 99999;

            var rec4 = CommitRecording("Ship4", 400);
            rec4.TakenControl = true;
            rec4.SceneExitSituation = 1; // LANDED

            var rec5 = CommitRecording("Ship5", 500);
            rec5.VesselSpawned = true;
            rec5.LastAppliedResourceIndex = 0;

            // Call reset
            var (standaloneCount, treeCount) = RestorePointStore.ResetAllPlaybackState();

            // Verify counts
            Assert.Equal(5, standaloneCount);
            Assert.Equal(0, treeCount);

            // Verify all 5 recordings have fields reset to defaults
            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.False(rec.VesselSpawned);
                Assert.Equal(0, rec.SpawnAttempts);
                Assert.Equal(0u, rec.SpawnedVesselPersistentId);
                Assert.Equal(-1, rec.LastAppliedResourceIndex);
                Assert.False(rec.TakenControl);
                Assert.Equal(-1, rec.SceneExitSituation);
            }
        }

        [Fact]
        public void ResetAllPlaybackState_Trees()
        {
            // Create 2 trees with recordings in various states
            var tree1 = new RecordingTree
            {
                Id = "tree1",
                TreeName = "Mun Lander",
                RootRecordingId = "rec_t1a",
                ResourcesApplied = true,
                DeltaFunds = -5000
            };
            var t1recA = new RecordingStore.Recording
            {
                RecordingId = "rec_t1a",
                VesselName = "LanderA",
                TreeId = "tree1",
                Points = MakePoints(5, 100),
                VesselSpawned = true,
                SpawnAttempts = 1,
                SpawnedVesselPersistentId = 111,
                LastAppliedResourceIndex = 4,
                TakenControl = true,
                SceneExitSituation = 32
            };
            var t1recB = new RecordingStore.Recording
            {
                RecordingId = "rec_t1b",
                VesselName = "LanderB",
                TreeId = "tree1",
                Points = MakePoints(3, 200),
                VesselSpawned = true,
                LastAppliedResourceIndex = 2,
                SceneExitSituation = 1
            };
            tree1.Recordings["rec_t1a"] = t1recA;
            tree1.Recordings["rec_t1b"] = t1recB;

            var tree2 = new RecordingTree
            {
                Id = "tree2",
                TreeName = "Station",
                RootRecordingId = "rec_t2a",
                ResourcesApplied = true,
                DeltaFunds = -10000
            };
            var t2recA = new RecordingStore.Recording
            {
                RecordingId = "rec_t2a",
                VesselName = "Core",
                TreeId = "tree2",
                Points = MakePoints(4, 300),
                SpawnAttempts = 3,
                SpawnedVesselPersistentId = 222,
                TakenControl = true
            };
            tree2.Recordings["rec_t2a"] = t2recA;

            RecordingStore.CommittedTrees.Add(tree1);
            RecordingStore.CommittedTrees.Add(tree2);

            // Call reset
            var (standaloneCount, treeCount) = RestorePointStore.ResetAllPlaybackState();

            Assert.Equal(0, standaloneCount);
            Assert.Equal(2, treeCount);

            // Verify tree ResourcesApplied reset
            Assert.False(tree1.ResourcesApplied);
            Assert.False(tree2.ResourcesApplied);

            // Verify all tree recordings have fields reset
            foreach (var tree in RecordingStore.CommittedTrees)
            {
                foreach (var rec in tree.Recordings.Values)
                {
                    Assert.False(rec.VesselSpawned);
                    Assert.Equal(0, rec.SpawnAttempts);
                    Assert.Equal(0u, rec.SpawnedVesselPersistentId);
                    Assert.Equal(-1, rec.LastAppliedResourceIndex);
                    Assert.False(rec.TakenControl);
                    Assert.Equal(-1, rec.SceneExitSituation);
                }
            }
        }

        #endregion

        #region MarkAllFullyApplied Tests

        [Fact]
        public void MarkAllFullyApplied_RecordingsAndTreesAndMilestones()
        {
            // Create standalone recordings with points
            var rec1 = CommitRecording("Ship1", 100, 5);
            Assert.Equal(-1, rec1.LastAppliedResourceIndex);

            var rec2 = CommitRecording("Ship2", 200, 10);
            rec2.LastAppliedResourceIndex = 3; // partially applied

            var rec3 = CommitRecording("Ship3", 300, 3);

            // Create a tree
            var tree = new RecordingTree
            {
                Id = "tree1",
                TreeName = "TestTree",
                RootRecordingId = "rec_tree1",
                ResourcesApplied = false
            };
            RecordingStore.CommittedTrees.Add(tree);

            // Create a committed milestone
            var milestone = new Milestone
            {
                MilestoneId = "mile1",
                Committed = true,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent { eventType = GameStateEventType.TechResearched, key = "basicRocketry", ut = 100 },
                    new GameStateEvent { eventType = GameStateEventType.FacilityUpgraded, key = "LaunchPad", ut = 150 }
                },
                LastReplayedEventIndex = -1
            };
            MilestoneStore.AddMilestoneForTesting(milestone);

            // Call mark
            var (recCount, treeCount) = RestorePointStore.MarkAllFullyApplied();

            // All recordings should be fully applied
            Assert.Equal(3, recCount);
            Assert.Equal(4, rec1.LastAppliedResourceIndex); // Points.Count-1 = 5-1 = 4
            Assert.Equal(9, rec2.LastAppliedResourceIndex); // 10-1 = 9
            Assert.Equal(2, rec3.LastAppliedResourceIndex); // 3-1 = 2

            // Tree should be marked applied
            Assert.Equal(1, treeCount);
            Assert.True(tree.ResourcesApplied);

            // Milestone should be fully replayed
            Assert.Equal(1, milestone.LastReplayedEventIndex); // Events.Count-1 = 2-1 = 1
        }

        [Fact]
        public void MarkAllFullyApplied_EmptyRecording_Skipped()
        {
            // Recording with no points should be skipped (guard: Points.Count > 0)
            var rec = new RecordingStore.Recording
            {
                RecordingId = "empty_rec",
                VesselName = "Empty",
                LastAppliedResourceIndex = -1
                // No points added
            };
            RecordingStore.CommittedRecordings.Add(rec);

            var (recCount, _) = RestorePointStore.MarkAllFullyApplied();

            Assert.Equal(0, recCount);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);
        }

        #endregion

        #region CanGoBack Tests

        [Fact]
        public void CanGoBack_NoRestorePoints_ReturnsFalse()
        {
            // No restore points added
            string reason;
            bool result = RestorePointStore.CanGoBack(out reason);

            Assert.False(result);
            Assert.Equal("No restore points available", reason);
        }

        [Fact]
        public void CanGoBack_IsRecording_ReturnsFalse()
        {
            RestorePointStore.AddForTesting(MakeTestRP("rp1", 100, "save1", "test"));

            string reason;
            bool result = RestorePointStore.CanGoBack(out reason, isRecording: true);

            Assert.False(result);
            Assert.Equal("Stop recording before going back", reason);
        }

        [Fact]
        public void CanGoBack_HasPending_ReturnsFalse()
        {
            RestorePointStore.AddForTesting(MakeTestRP("rp1", 100, "save1", "test"));

            // Create a pending recording
            RecordingStore.StashPending(MakePoints(3), "Pending");

            string reason;
            bool result = RestorePointStore.CanGoBack(out reason);

            Assert.False(result);
            Assert.Equal("Merge or discard pending recording first", reason);
        }

        [Fact]
        public void CanGoBack_HasPendingTree_ReturnsFalse()
        {
            RestorePointStore.AddForTesting(MakeTestRP("rp1", 100, "save1", "test"));

            // Set up a pending tree via StashPendingTree
            var tree = new RecordingTree
            {
                Id = "tree_pending",
                TreeName = "PendingTree",
                RootRecordingId = "rec_root"
            };
            RecordingStore.StashPendingTree(tree);

            string reason;
            bool result = RestorePointStore.CanGoBack(out reason);

            Assert.False(result);
            Assert.Equal("Merge or discard pending tree first", reason);
        }

        [Fact]
        public void CanGoBack_NotInFlight_ReturnsFalse()
        {
            RestorePointStore.AddForTesting(MakeTestRP("rp1", 100, "save1", "test"));

            string reason;
            bool result = RestorePointStore.CanGoBack(out reason, isInFlight: false);

            Assert.False(result);
            Assert.Equal("Go back is only available in flight", reason);
        }

        [Fact]
        public void CanGoBack_AllClear_ReturnsTrue()
        {
            RestorePointStore.AddForTesting(MakeTestRP("rp1", 100, "save1", "test"));

            string reason;
            bool result = RestorePointStore.CanGoBack(out reason);

            Assert.True(result);
            Assert.Equal("", reason);
        }

        [Fact]
        public void CanGoBack_PreconditionPriority()
        {
            // Test that preconditions are checked in the right order:
            // 1. No restore points (checked first)
            // 2. Not in flight
            // 3. isRecording
            // 4. HasPending
            // 5. HasPendingTree

            // No restore points + not in flight + recording active => "No restore points" wins
            string reason;
            bool result = RestorePointStore.CanGoBack(out reason, isRecording: true, isInFlight: false);
            Assert.False(result);
            Assert.Equal("No restore points available", reason);

            // Has restore points + not in flight + recording active => "not in flight" wins
            RestorePointStore.AddForTesting(MakeTestRP("rp1", 100, "save1", "test"));
            result = RestorePointStore.CanGoBack(out reason, isRecording: true, isInFlight: false);
            Assert.False(result);
            Assert.Equal("Go back is only available in flight", reason);
        }

        #endregion

        #region IsStableState Tests

        [Fact]
        public void IsStableState_AllSituations()
        {
            // Stable states
            Assert.True(RestorePointStore.IsStableState(4));   // PRELAUNCH
            Assert.True(RestorePointStore.IsStableState(1));   // LANDED
            Assert.True(RestorePointStore.IsStableState(2));   // SPLASHED
            Assert.True(RestorePointStore.IsStableState(32));  // ORBITING

            // Unstable states
            Assert.False(RestorePointStore.IsStableState(8));  // FLYING
            Assert.False(RestorePointStore.IsStableState(16)); // SUB_ORBITAL
            Assert.False(RestorePointStore.IsStableState(64)); // ESCAPING

            // Also test DOCKED (128) — not explicitly stable for Commit Flight
            Assert.False(RestorePointStore.IsStableState(128)); // DOCKED
        }

        [Fact]
        public void IsStableState_WithVesselSituationsEnum()
        {
            // Use the actual Vessel.Situations enum values
            Assert.True(RestorePointStore.IsStableState((int)Vessel.Situations.PRELAUNCH));
            Assert.True(RestorePointStore.IsStableState((int)Vessel.Situations.LANDED));
            Assert.True(RestorePointStore.IsStableState((int)Vessel.Situations.SPLASHED));
            Assert.True(RestorePointStore.IsStableState((int)Vessel.Situations.ORBITING));

            Assert.False(RestorePointStore.IsStableState((int)Vessel.Situations.FLYING));
            Assert.False(RestorePointStore.IsStableState((int)Vessel.Situations.SUB_ORBITAL));
            Assert.False(RestorePointStore.IsStableState((int)Vessel.Situations.ESCAPING));
        }

        #endregion

        #region CountFutureRecordings Tests

        [Fact]
        public void CountFutureRecordings_Mixed()
        {
            // Create 3 recordings at UT 100, 200, 300
            CommitRecording("Ship1", 100);
            CommitRecording("Ship2", 200);
            CommitRecording("Ship3", 300);

            // Count future recordings from UT 150 => Ship2 (200) and Ship3 (300)
            Assert.Equal(2, RestorePointStore.CountFutureRecordings(150));

            // Count from UT 50 => all 3
            Assert.Equal(3, RestorePointStore.CountFutureRecordings(50));

            // Count from UT 300 => none (StartUT must be strictly > ut)
            Assert.Equal(0, RestorePointStore.CountFutureRecordings(300));

            // Count from UT 299 => Ship3 only
            Assert.Equal(1, RestorePointStore.CountFutureRecordings(299));

            // Count from UT 500 => none
            Assert.Equal(0, RestorePointStore.CountFutureRecordings(500));
        }

        [Fact]
        public void CountFutureRecordings_AfterDeletion()
        {
            // Create 3 recordings
            CommitRecording("Ship1", 100);
            CommitRecording("Ship2", 200);
            CommitRecording("Ship3", 300);

            Assert.Equal(2, RestorePointStore.CountFutureRecordings(150));

            // Remove Ship2 (index 1) — the one at UT 200
            RecordingStore.CommittedRecordings.RemoveAt(1);

            // Now only Ship3 (UT 300) is future from UT 150
            Assert.Equal(1, RestorePointStore.CountFutureRecordings(150));
        }

        [Fact]
        public void CountFutureRecordings_Empty()
        {
            Assert.Equal(0, RestorePointStore.CountFutureRecordings(100));
        }

        #endregion
    }
}
