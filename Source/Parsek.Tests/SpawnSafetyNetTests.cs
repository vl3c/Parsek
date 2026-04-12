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

            RecordingStore.AddRecordingWithTreeForTesting(rootRec);
            RecordingStore.AddRecordingWithTreeForTesting(childRec);
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
            RecordingStore.AddRecordingWithTreeForTesting(leafRec);
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

        [Fact]
        public void ShouldSpawn_FlyingSnapshot_TerminalOrbiting_Allowed()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Orbiting
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawn_FlyingSnapshot_TerminalLanded_Allowed()
        {
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

        [Fact]
        public void ShouldSpawn_NonLeafInTree_NullChildBranchPointId_StillReturnsFalse()
        {
            // Safety net catches non-leaf even when ChildBranchPointId is null.
            var tree = new RecordingTree
            {
                Id = "tree-splash",
                TreeName = "SplashTree",
                RootRecordingId = "root-rec"
            };

            var rootRec = new Recording
            {
                RecordingId = "root-rec",
                TreeId = "tree-splash",
                VesselPersistentId = 100,
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = null,
                TerminalStateValue = TerminalState.Splashed
            };

            var bp = new BranchPoint
            {
                Id = "bp-splash-break",
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
        public void ComputeCorrectedSituation_FlyingWithOrbiting_ReturnsOrbiting()
        {
            // Vessel captured during ascent (FLYING) that achieved orbit — correct to ORBITING
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Orbiting);
            Assert.Equal("ORBITING", result);
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

        #region OverrideSnapshotPosition (EVA spawn fix)

        [Fact]
        public void OverrideSnapshotPosition_UpdatesLatLonAlt()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "1.0");
            snapshot.AddValue("lon", "2.0");
            snapshot.AddValue("alt", "3.0");

            VesselSpawner.OverrideSnapshotPosition(snapshot, 10.5, 20.5, 30.5, 0, "Test");

            Assert.Equal("10.5", snapshot.GetValue("lat"));
            Assert.Equal("20.5", snapshot.GetValue("lon"));
            Assert.Equal("30.5", snapshot.GetValue("alt"));
        }

        [Fact]
        public void OverrideSnapshotPosition_CreatesValuesWhenMissing()
        {
            var snapshot = new ConfigNode("VESSEL");

            VesselSpawner.OverrideSnapshotPosition(snapshot, 10.5, 20.5, 30.5, 0, "Test");

            Assert.Equal("10.5", snapshot.GetValue("lat"));
            Assert.Equal("20.5", snapshot.GetValue("lon"));
            Assert.Equal("30.5", snapshot.GetValue("alt"));
        }

        [Fact]
        public void OverrideSnapshotPosition_NullSnapshot_NoThrow()
        {
            VesselSpawner.OverrideSnapshotPosition(null, 10.5, 20.5, 30.5, 0, "Test");
            // No exception = pass
        }

        [Fact]
        public void OverrideSnapshotPosition_LogsOverride()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "1.0");
            snapshot.AddValue("lon", "2.0");
            snapshot.AddValue("alt", "3.0");

            VesselSpawner.OverrideSnapshotPosition(snapshot, 10.5, 20.5, 30.5, 7, "Jeb");

            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") && l.Contains("Snapshot position override") && l.Contains("Jeb"));
        }

        [Fact]
        public void ResolveSpawnPosition_EvaVessel_UsesTrajectoryEndpoint()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "1.0");
            snapshot.AddValue("lon", "2.0");
            snapshot.AddValue("alt", "3.0");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                EvaCrewName = "Jebediah Kerman"
            };

            var lastPt = new TrajectoryPoint { latitude = 10.0, longitude = 20.0, altitude = 30.0 };
            VesselSpawner.ResolveSpawnPosition(rec, 0, lastPt, out double lat, out double lon, out double alt);

            Assert.Equal(10.0, lat);
            Assert.Equal(20.0, lon);
            Assert.Equal(30.0, alt);
        }

        [Fact]
        public void ResolveSpawnPosition_NonEvaVessel_UsesSnapshotPosition()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "1.0");
            snapshot.AddValue("lon", "2.0");
            snapshot.AddValue("alt", "3.0");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                EvaCrewName = null
            };

            var lastPt = new TrajectoryPoint { latitude = 10.0, longitude = 20.0, altitude = 30.0 };
            VesselSpawner.ResolveSpawnPosition(rec, 0, lastPt, out double lat, out double lon, out double alt);

            Assert.Equal(1.0, lat);
            Assert.Equal(2.0, lon);
            Assert.Equal(3.0, alt);
        }

        [Fact]
        public void ResolveSpawnPosition_BreakupContinuousSplashed_ClampsAltitudeToZero()
        {
            // Breakup-continuous recording with terminal=Splashed: last trajectory point
            // is still above sea level. Altitude must be clamped to 0. (#224)
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                EvaCrewName = null,
                ChildBranchPointId = "bp-crash",
                TerminalStateValue = TerminalState.Splashed
            };

            var lastPt = new TrajectoryPoint { latitude = -0.12, longitude = -73.72, altitude = 42.5 };
            VesselSpawner.ResolveSpawnPosition(rec, 13, lastPt, out double lat, out double lon, out double alt);

            Assert.Equal(-0.12, lat);
            Assert.Equal(-73.72, lon);
            Assert.Equal(0.0, alt);
            Assert.Contains(logLines, l => l.Contains("Clamped altitude") && l.Contains("SPLASHED"));
        }

        [Fact]
        public void ResolveSpawnPosition_BreakupContinuousSplashed_NegativeAlt_NoClamp()
        {
            // Already at or below sea level — no clamping needed
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                EvaCrewName = null,
                ChildBranchPointId = "bp-crash",
                TerminalStateValue = TerminalState.Splashed
            };

            var lastPt = new TrajectoryPoint { latitude = -0.12, longitude = -73.72, altitude = -1.5 };
            VesselSpawner.ResolveSpawnPosition(rec, 13, lastPt, out double lat, out double lon, out double alt);

            Assert.Equal(-1.5, alt);
        }

        [Fact]
        public void ResolveSpawnPosition_NonBreakupSplashed_ClampsAltitudeToZero()
        {
            // Non-breakup recording that uses snapshot position: snapshot alt > 0 with
            // terminal=Splashed should still be clamped (safety net for all paths).
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("lat", "-0.12");
            snapshot.AddValue("lon", "-73.72");
            snapshot.AddValue("alt", "15.3");

            var rec = new Recording
            {
                VesselSnapshot = snapshot,
                EvaCrewName = null,
                ChildBranchPointId = null,
                TerminalStateValue = TerminalState.Splashed
            };

            var lastPt = new TrajectoryPoint { latitude = 0, longitude = 0, altitude = 0 };
            VesselSpawner.ResolveSpawnPosition(rec, 5, lastPt, out double lat, out double lon, out double alt);

            Assert.Equal(-0.12, lat);
            Assert.Equal(-73.72, lon);
            Assert.Equal(0.0, alt);
        }

        [Fact]
        public void ResolveSpawnPosition_EvaLanded_FallsThroughToSplashedClamp()
        {
            // EVA recordings previously returned early, bypassing altitude clamping.
            // With terminal=Splashed and alt > 0, the EVA should now be clamped to 0. (#231)
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                EvaCrewName = "Jebediah Kerman",
                TerminalStateValue = TerminalState.Splashed
            };

            var lastPt = new TrajectoryPoint { latitude = 5.0, longitude = 10.0, altitude = 150.0, bodyName = "Kerbin" };
            VesselSpawner.ResolveSpawnPosition(rec, 7, lastPt, out double lat, out double lon, out double alt);

            Assert.Equal(5.0, lat);
            Assert.Equal(10.0, lon);
            Assert.Equal(0.0, alt);
            Assert.Contains(logLines, l => l.Contains("Clamped altitude") && l.Contains("SPLASHED"));
        }

        // ── ClampAltitudeForLanded (pure, testable without CelestialBody) ──
        //
        // NEW SEMANTICS (#309 mesh-object positioning fix):
        //   The recorded altitude is trusted — it's where the vessel came to rest
        //   per the LANDED terminal state. We no longer down-clamp to PQS terrain,
        //   because body.TerrainAltitude() is blind to mesh objects (Island
        //   Airfield, launchpad, KSC buildings) and the old clamp buried any
        //   vessel recorded on a raised surface 19m below the runway. The only
        //   case where the clamp fires is when the recorded altitude is below
        //   (PQS terrain + UndergroundSafetyFloorMeters), meaning PQS terrain
        //   has shifted UP since recording (rare: KSP update / terrain mod).

        [Fact]
        public void ClampAltitudeForLanded_HighAboveTerrain_Preserved_Airfield()
        {
            // Butterfly Rover on the Island Airfield: recorded alt=133.9m, PQS
            // terrain beneath the airfield mesh=114.9m, mesh offset ~19m. The
            // old down-clamp buried the rover 17m underground. New behavior:
            // preserve the recorded altitude; KSP CheckGroundCollision will
            // settle against real colliders (airfield runway) after spawn.
            double result = VesselSpawner.ClampAltitudeForLanded(133.9, 114.9, 2, "Butterfly Rover");
            Assert.Equal(133.9, result);
            Assert.DoesNotContain(logLines, l => l.Contains("below-pqs-floor"));
        }

        [Fact]
        public void ClampAltitudeForLanded_BelowTerrain_PushedUp()
        {
            // Vessel at -5m, PQS terrain at 67m → below safety floor, push up.
            // This is the only case where we clamp now.
            double expected = 67.0 + VesselSpawner.UndergroundSafetyFloorMeters;
            double result = VesselSpawner.ClampAltitudeForLanded(-5.0, 67.0, 3, "Kerbal X");
            Assert.Equal(expected, result);
            Assert.Contains(logLines, l =>
                l.Contains("Clamped altitude") && l.Contains("below-pqs-floor"));
        }

        [Fact]
        public void ClampAltitudeForLanded_LowClearanceAboveTerrain_PushedToSafetyFloor_Bug282()
        {
            // #282 scenario: vessel recorded 0.9m above PQS terrain (Mk1-3 pod root
            // with wheels/legs extending below). This is below the 2m underground
            // safety floor, so we push up to terrain+2m. The safety floor handles
            // the "bury the pod" case without the old aggressive blanket down-clamp
            // that was breaking mesh-object positioning (Island Airfield, launchpad).
            double expected = 175.6 + VesselSpawner.UndergroundSafetyFloorMeters;
            double result = VesselSpawner.ClampAltitudeForLanded(176.5, 175.6, 41, "Kerbal X");
            Assert.Equal(expected, result);
            Assert.Contains(logLines, l =>
                l.Contains("Clamped altitude") && l.Contains("below-pqs-floor"));
        }

        [Fact]
        public void ClampAltitudeForLanded_AtSafetyFloor_Preserved()
        {
            // alt == safetyFloor exactly — treat as preserved (boundary).
            double safetyFloor = 67.0 + VesselSpawner.UndergroundSafetyFloorMeters;
            double result = VesselSpawner.ClampAltitudeForLanded(safetyFloor, 67.0, 3, "Kerbal X");
            Assert.Equal(safetyFloor, result);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("below-pqs-floor"));
        }

        [Fact]
        public void ClampAltitudeForLanded_JustBelowSafetyFloor_PushedUp()
        {
            // 1 cm below the safety floor → push up by 1 cm to the floor.
            double safetyFloor = 67.0 + VesselSpawner.UndergroundSafetyFloorMeters;
            double result = VesselSpawner.ClampAltitudeForLanded(safetyFloor - 0.01, 67.0, 3, "Kerbal X");
            Assert.Equal(safetyFloor, result);
            Assert.Contains(logLines, l =>
                l.Contains("Clamped altitude") && l.Contains("below-pqs-floor"));
        }

        [Fact]
        public void UndergroundSafetyFloorMeters_IsPinned()
        {
            // Pin the constant so unintended drift is caught in review.
            Assert.Equal(2.0, VesselSpawner.UndergroundSafetyFloorMeters);
            Assert.Equal(4.0, VesselSpawner.LandedGhostClearanceMeters);
        }

        #endregion

        #region StripEvaLadderState

        [Fact]
        public void StripEvaLadderState_ClearsLadderState()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "KerbalEVA");
            module.AddValue("state", "Ladder (Acquire)");
            module.AddValue("OnALadder", "True");

            VesselSpawner.StripEvaLadderState(snapshot, 0, "Jeb");

            // The state value is removed entirely so KerbalEVA.StartEVA picks the
            // correct st_idle_gr / st_idle_fl / st_swim_idle name at runtime
            // (#264 follow-up — was hardcoding invalid "idle" string).
            Assert.False(module.HasValue("state"));
            Assert.Equal("False", module.GetValue("OnALadder"));
        }

        [Fact]
        public void StripEvaLadderState_IgnoresNonLadderState()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "KerbalEVA");
            module.AddValue("state", "st_idle_gr");

            VesselSpawner.StripEvaLadderState(snapshot, 0, "Jeb");

            // Non-ladder state must be left alone — strip only when "ladder" appears.
            Assert.Equal("st_idle_gr", module.GetValue("state"));
        }

        [Fact]
        public void StripEvaLadderState_HandlesKerbalEVAFlight()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "KerbalEVAFlight");
            module.AddValue("state", "Ladder_Idle");

            VesselSpawner.StripEvaLadderState(snapshot, 0, "Jeb");

            Assert.False(module.HasValue("state"));
        }

        [Fact]
        public void StripEvaLadderState_NullSnapshot_NoThrow()
        {
            VesselSpawner.StripEvaLadderState(null, 0, "Jeb");
        }

        [Fact]
        public void StripEvaLadderState_NoModules_NoThrow()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddNode("PART");

            VesselSpawner.StripEvaLadderState(snapshot, 0, "Jeb");
        }

        [Fact]
        public void StripEvaLadderState_LogsWhenStripped()
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            var module = part.AddNode("MODULE");
            module.AddValue("name", "KerbalEVA");
            module.AddValue("state", "Ladder (Acquire)");

            VesselSpawner.StripEvaLadderState(snapshot, 3, "Val");

            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") && l.Contains("ladder state stripped") && l.Contains("Val"));
        }

        #endregion

        #region OverrideSituationFromTerminalState (#264 + #176 regression guard)

        [Fact]
        public void OverrideSituationFromTerminalState_FlyingLanded_ReturnsLanded()
        {
            string result = VesselSpawner.OverrideSituationFromTerminalState("FLYING", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_FlyingSplashed_ReturnsSplashed()
        {
            string result = VesselSpawner.OverrideSituationFromTerminalState("FLYING", TerminalState.Splashed);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_FlyingOrbiting_ReturnsOrbiting()
        {
            // Regression guard for #176 — this path used to be an inline override in SpawnAtPosition.
            string result = VesselSpawner.OverrideSituationFromTerminalState("FLYING", TerminalState.Orbiting);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_FlyingDocked_ReturnsOrbiting()
        {
            // Regression guard for #176.
            string result = VesselSpawner.OverrideSituationFromTerminalState("FLYING", TerminalState.Docked);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_FlyingSubOrbital_ReturnsFlyingUnchanged()
        {
            // SubOrbital terminal doesn't map to an explicit override — we don't force LANDED
            // or ORBITING on a sub-orbital recording. The caller's decision logic decides.
            string result = VesselSpawner.OverrideSituationFromTerminalState("FLYING", TerminalState.SubOrbital);
            Assert.Equal("FLYING", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_LandedLandedInput_ReturnsUnchanged()
        {
            // Only fires on FLYING input. If DetermineSituation already returned LANDED,
            // we don't re-override.
            string result = VesselSpawner.OverrideSituationFromTerminalState("LANDED", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_OrbitingHighSpeedLandedTerminal_ReturnsOrbitingUnchanged()
        {
            // A high-velocity classifier result is trusted over a potentially stale terminal state —
            // forcing LANDED on a fast-moving vessel would be dangerous. Only FLYING gets overridden.
            string result = VesselSpawner.OverrideSituationFromTerminalState("ORBITING", TerminalState.Landed);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_FlyingNullTerminal_ReturnsFlyingUnchanged()
        {
            string result = VesselSpawner.OverrideSituationFromTerminalState("FLYING", null);
            Assert.Equal("FLYING", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_FlyingDestroyed_ReturnsFlyingUnchanged()
        {
            // Destroyed has no stable resting situation — no override.
            string result = VesselSpawner.OverrideSituationFromTerminalState("FLYING", TerminalState.Destroyed);
            Assert.Equal("FLYING", result);
        }

        [Fact]
        public void OverrideSituationFromTerminalState_WalkingKerbalRepro_ProducesLanded()
        {
            // Butterfly Rover repro in pure-function form (#264).
            // A kerbal walking on Kerbin's surface at 5m above terrain with 2 m/s speed has:
            //   alt = 5.0  (ClampAltitudeForLanded + clearance)
            //   overWater = false
            //   speed = 2.0
            //   orbitalSpeed ≈ 2296 (sqrt(GM/r) for Kerbin surface)
            // DetermineSituation returns "FLYING" (alt > 0, speed < 0.9 * orbitalSpeed).
            // Without OverrideSituationFromTerminalState, the kerbal spawns as FLYING and hits
            // the OrbitDriver.updateMode=UPDATE stale-orbit bug.
            string classified = VesselSpawner.DetermineSituation(
                alt: 5.0, overWater: false, speed: 2.0, orbitalSpeed: 2296.0);
            Assert.Equal("FLYING", classified);

            string overridden = VesselSpawner.OverrideSituationFromTerminalState(classified, TerminalState.Landed);
            Assert.Equal("LANDED", overridden);
        }

        #endregion
    }
}
