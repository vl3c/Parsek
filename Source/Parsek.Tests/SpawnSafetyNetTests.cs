using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #114 fixes: non-leaf tree spawn suppression safety net,
    /// snapshot situation unsafe check, and crew stripping for destroyed vessels.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnSafetyNetTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnSafetyNetTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region IsNonLeafInCommittedTree

        [Fact]
        public void IsNonLeafInCommittedTree_ParentOfBranchPoint_ReturnsTrue()
        {
            // Build a committed tree where "root-rec" is a parent of a branch point
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "CrashTree",
                RootRecordingId = "root-rec"
            };

            var rootRec = new Recording
            {
                RecordingId = "root-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100,
                // Simulate the bug: ChildBranchPointId is null (not set)
                ChildBranchPointId = null
            };

            var childRec = new Recording
            {
                RecordingId = "child-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100,
                ParentBranchPointId = "bp-1"
            };

            var bp = new BranchPoint
            {
                Id = "bp-1",
                Type = BranchPointType.Breakup,
                UT = 78.0
            };
            bp.ParentRecordingIds.Add("root-rec");
            bp.ChildRecordingIds.Add("child-rec");

            tree.Recordings["root-rec"] = rootRec;
            tree.Recordings["child-rec"] = childRec;
            tree.BranchPoints.Add(bp);

            RecordingStore.CommittedRecordings.Add(rootRec);
            RecordingStore.CommittedRecordings.Add(childRec);
            RecordingStore.CommittedTrees.Add(tree);

            bool result = GhostPlaybackLogic.IsNonLeafInCommittedTree(rootRec);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("IsNonLeafInCommittedTree") &&
                l.Contains("root-rec") &&
                l.Contains("safety net triggered"));
        }

        [Fact]
        public void IsNonLeafInCommittedTree_LeafRecording_ReturnsFalse()
        {
            var tree = new RecordingTree
            {
                Id = "tree-2",
                TreeName = "SimpleTree",
                RootRecordingId = "root-rec"
            };

            var leafRec = new Recording
            {
                RecordingId = "leaf-rec",
                TreeId = "tree-2",
                VesselPersistentId = 200,
                ChildBranchPointId = null
            };

            // No branch point references leaf-rec as parent
            tree.Recordings["leaf-rec"] = leafRec;
            RecordingStore.CommittedRecordings.Add(leafRec);
            RecordingStore.CommittedTrees.Add(tree);

            bool result = GhostPlaybackLogic.IsNonLeafInCommittedTree(leafRec);

            Assert.False(result);
        }

        [Fact]
        public void IsNonLeafInCommittedTree_StandaloneRecording_ReturnsFalse()
        {
            // Recording without TreeId — standalone, not in any tree
            var standalone = new Recording
            {
                RecordingId = "standalone-1",
                TreeId = null,
                VesselPersistentId = 300
            };

            bool result = GhostPlaybackLogic.IsNonLeafInCommittedTree(standalone);

            Assert.False(result);
        }

        [Fact]
        public void IsNonLeafInCommittedTree_EmptyRecordingId_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "",
                TreeId = "tree-x"
            };

            bool result = GhostPlaybackLogic.IsNonLeafInCommittedTree(rec);

            Assert.False(result);
        }

        [Fact]
        public void IsNonLeafInCommittedTree_TreeNotCommitted_ReturnsFalse()
        {
            // Recording references a tree that is not in CommittedTrees
            var rec = new Recording
            {
                RecordingId = "orphan-rec",
                TreeId = "nonexistent-tree"
            };

            bool result = GhostPlaybackLogic.IsNonLeafInCommittedTree(rec);

            Assert.False(result);
        }

        #endregion

        #region IsSnapshotSituationUnsafe

        [Fact]
        public void IsSnapshotSituationUnsafe_Flying_ReturnsTrue()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");

            Assert.True(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_SubOrbital_ReturnsTrue()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "SUB_ORBITAL");

            Assert.True(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_Landed_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");

            Assert.False(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_Orbiting_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "ORBITING");

            Assert.False(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_Splashed_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "SPLASHED");

            Assert.False(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_Prelaunch_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "PRELAUNCH");

            Assert.False(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_NullSnapshot_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.IsSnapshotSituationUnsafe(null));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_NoSitField_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            // No sit field at all
            Assert.False(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        [Fact]
        public void IsSnapshotSituationUnsafe_CaseInsensitive()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "flying");

            Assert.True(GhostPlaybackLogic.IsSnapshotSituationUnsafe(snapshot));
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Snapshot Situation Suppression

        [Fact]
        public void ShouldSpawn_FlyingSnapshot_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = null // No terminal state set
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("snapshot situation unsafe", reason);
        }

        [Fact]
        public void ShouldSpawn_SubOrbitalSnapshot_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "SUB_ORBITAL");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = null
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("snapshot situation unsafe", reason);
        }

        [Fact]
        public void ShouldSpawn_LandedSnapshot_NoTerminalState_Allowed()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = null
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Non-Leaf Safety Net

        [Fact]
        public void ShouldSpawn_NonLeafInTree_NullChildBranchPointId_ReturnsFalse()
        {
            // Tree where root has children but ChildBranchPointId is null (the bug)
            var tree = new RecordingTree
            {
                Id = "tree-crash",
                TreeName = "CrashTree",
                RootRecordingId = "root-rec"
            };

            var rootRec = new Recording
            {
                RecordingId = "root-rec",
                TreeId = "tree-crash",
                VesselPersistentId = 100,
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = null // Bug: not set
            };

            var bp = new BranchPoint
            {
                Id = "bp-crash",
                Type = BranchPointType.Breakup,
                UT = 78.0
            };
            bp.ParentRecordingIds.Add("root-rec");
            bp.ChildRecordingIds.Add("child-rec");

            tree.Recordings["root-rec"] = rootRec;
            tree.BranchPoints.Add(bp);
            RecordingStore.CommittedTrees.Add(tree);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rootRec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("non-leaf in committed tree", reason);
        }

        #endregion

        #region ShouldStripCrewForSpawn

        [Fact]
        public void ShouldStripCrewForSpawn_Destroyed_ReturnsTrue()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Destroyed };

            Assert.True(VesselSpawner.ShouldStripCrewForSpawn(rec));
        }

        [Fact]
        public void ShouldStripCrewForSpawn_Landed_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Landed };

            Assert.False(VesselSpawner.ShouldStripCrewForSpawn(rec));
        }

        [Fact]
        public void ShouldStripCrewForSpawn_Orbiting_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Orbiting };

            Assert.False(VesselSpawner.ShouldStripCrewForSpawn(rec));
        }

        [Fact]
        public void ShouldStripCrewForSpawn_NullTerminalState_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = null };

            Assert.False(VesselSpawner.ShouldStripCrewForSpawn(rec));
        }

        [Fact]
        public void ShouldStripCrewForSpawn_Recovered_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Recovered };

            Assert.False(VesselSpawner.ShouldStripCrewForSpawn(rec));
        }

        [Fact]
        public void ShouldStripCrewForSpawn_SubOrbital_ReturnsFalse()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.SubOrbital };

            Assert.False(VesselSpawner.ShouldStripCrewForSpawn(rec));
        }

        #endregion

        #region StripAllCrewFromSnapshot

        [Fact]
        public void StripAllCrewFromSnapshot_RemovesAllCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part1 = new ConfigNode("PART");
            part1.AddValue("crew", "Jebediah Kerman");
            part1.AddValue("crew", "Bill Kerman");
            var part2 = new ConfigNode("PART");
            part2.AddValue("crew", "Valentina Kerman");
            snapshot.AddNode(part1);
            snapshot.AddNode(part2);

            int removed = VesselSpawner.StripAllCrewFromSnapshot(snapshot);

            Assert.Equal(3, removed);

            // Verify no crew remain
            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                Assert.Empty(partNode.GetValues("crew"));
            }
        }

        [Fact]
        public void StripAllCrewFromSnapshot_NoCrew_ReturnsZero()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            snapshot.AddNode(part);

            int removed = VesselSpawner.StripAllCrewFromSnapshot(snapshot);

            Assert.Equal(0, removed);
        }

        [Fact]
        public void StripAllCrewFromSnapshot_NullSnapshot_ReturnsZero()
        {
            int removed = VesselSpawner.StripAllCrewFromSnapshot(null);

            Assert.Equal(0, removed);
        }

        [Fact]
        public void StripAllCrewFromSnapshot_LogsRemovedCrew()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = new ConfigNode("PART");
            part.AddValue("crew", "Jebediah Kerman");
            snapshot.AddNode(part);

            VesselSpawner.StripAllCrewFromSnapshot(snapshot);

            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("StripAllCrewFromSnapshot") &&
                l.Contains("Jebediah Kerman"));
        }

        #endregion
    }
}
