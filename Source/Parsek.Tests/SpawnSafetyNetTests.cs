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

        #region ComputeCorrectedSituation (#169)

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithLanded_ReturnsLanded()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithSplashed_ReturnsSplashed()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Splashed);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_SubOrbitalWithLanded_ReturnsLanded()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("SUB_ORBITAL", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_SubOrbitalWithSplashed_ReturnsSplashed()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("SUB_ORBITAL", TerminalState.Splashed);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithDestroyed_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Destroyed);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithOrbiting_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Orbiting);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithNull_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", null);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_LandedWithLanded_ReturnsNull()
        {
            // Already safe — no correction needed
            string result = VesselSpawner.ComputeCorrectedSituation("LANDED", TerminalState.Landed);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_OrbitingWithLanded_ReturnsNull()
        {
            // ORBITING is not unsafe — no correction needed
            string result = VesselSpawner.ComputeCorrectedSituation("ORBITING", TerminalState.Landed);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_NullSit_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation(null, TerminalState.Landed);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_EmptySit_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("", TerminalState.Landed);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_CaseInsensitive()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("flying", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithSubOrbital_ReturnsNull()
        {
            // SubOrbital terminal state should not override — vessel is still in flight
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.SubOrbital);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithDocked_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Docked);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithBoarded_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Boarded);
            Assert.Null(result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingWithRecovered_ReturnsNull()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Recovered);
            Assert.Null(result);
        }

        #endregion

        #region CorrectUnsafeSnapshotSituation (#169)

        [Fact]
        public void CorrectUnsafeSnapshotSituation_FlyingToLanded_CorrectsSitAndFlags()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");
            snapshot.AddValue("landed", "False");
            snapshot.AddValue("splashed", "False");

            bool corrected = VesselSpawner.CorrectUnsafeSnapshotSituation(snapshot, TerminalState.Landed);

            Assert.True(corrected);
            Assert.Equal("LANDED", snapshot.GetValue("sit"));
            Assert.Equal("True", snapshot.GetValue("landed"));
            Assert.Equal("False", snapshot.GetValue("splashed"));
        }

        [Fact]
        public void CorrectUnsafeSnapshotSituation_FlyingToSplashed_CorrectsSitAndFlags()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");
            snapshot.AddValue("landed", "False");
            snapshot.AddValue("splashed", "False");

            bool corrected = VesselSpawner.CorrectUnsafeSnapshotSituation(snapshot, TerminalState.Splashed);

            Assert.True(corrected);
            Assert.Equal("SPLASHED", snapshot.GetValue("sit"));
            Assert.Equal("False", snapshot.GetValue("landed"));
            Assert.Equal("True", snapshot.GetValue("splashed"));
        }

        [Fact]
        public void CorrectUnsafeSnapshotSituation_LandedSnapshot_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");

            bool corrected = VesselSpawner.CorrectUnsafeSnapshotSituation(snapshot, TerminalState.Landed);

            Assert.False(corrected);
            Assert.Equal("LANDED", snapshot.GetValue("sit"));
        }

        [Fact]
        public void CorrectUnsafeSnapshotSituation_NullSnapshot_ReturnsFalse()
        {
            bool corrected = VesselSpawner.CorrectUnsafeSnapshotSituation(null, TerminalState.Landed);

            Assert.False(corrected);
        }

        [Fact]
        public void CorrectUnsafeSnapshotSituation_NullTerminal_ReturnsFalse()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");

            bool corrected = VesselSpawner.CorrectUnsafeSnapshotSituation(snapshot, null);

            Assert.False(corrected);
            Assert.Equal("FLYING", snapshot.GetValue("sit"));
        }

        [Fact]
        public void CorrectUnsafeSnapshotSituation_Logs_Correction()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");
            snapshot.AddValue("landed", "False");
            snapshot.AddValue("splashed", "False");

            VesselSpawner.CorrectUnsafeSnapshotSituation(snapshot, TerminalState.Landed);

            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("FLYING") &&
                l.Contains("LANDED") &&
                l.Contains("#169"));
        }

        [Fact]
        public void CorrectUnsafeSnapshotSituation_NoLogWhenNotCorrected()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");

            VesselSpawner.CorrectUnsafeSnapshotSituation(snapshot, TerminalState.Landed);

            Assert.DoesNotContain(logLines, l => l.Contains("#169"));
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Terminal Override with FLYING (#169)

        [Fact]
        public void ShouldSpawn_FlyingSnapshot_TerminalLanded_Allowed()
        {
            // Bug #169: terminal state Landed overrides the FLYING unsafe check,
            // allowing the spawn to proceed (situation will be corrected before spawn)
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawn_FlyingSnapshot_TerminalSplashed_Allowed()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Splashed
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawn_FlyingSnapshot_TerminalDestroyed_Blocked()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Destroyed
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state Destroyed", reason);
        }

        #endregion
    }
}
