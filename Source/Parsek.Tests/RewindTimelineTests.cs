using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the spawn-at-recording-end decision logic (GhostPlaybackLogic.ShouldSpawnAtRecordingEnd)
    /// and CanRewind validation (RecordingStore.CanRewind).
    /// These cover the pure guard logic extracted from ParsekFlight.UpdateTimelinePlayback.
    /// </summary>
    [Collection("Sequential")]
    public class RewindTimelineTests : IDisposable
    {
        public RewindTimelineTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        #region ShouldSpawnAtRecordingEnd — Base Conditions

        [Fact]
        public void ShouldSpawn_NormalRecording_ReturnsTrue()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = false,
                VesselDestroyed = false
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        [Fact]
        public void ShouldSpawn_NoSnapshot_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = null,
                VesselSpawned = false,
                VesselDestroyed = false
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("no vessel snapshot", reason);
        }

        [Fact]
        public void ShouldSpawn_AlreadySpawned_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = true,
                VesselDestroyed = false
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("already spawned", reason);
        }

        [Fact]
        public void ShouldSpawn_VesselDestroyed_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = false,
                VesselDestroyed = true
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("vessel destroyed", reason);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Branch Suppression

        [Fact]
        public void ShouldSpawn_BranchGreaterThanZero_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChainBranch = 1
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("branch > 0", reason);
        }

        [Fact]
        public void ShouldSpawn_BranchZero_Allowed()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChainBranch = 0
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Active Chain Suppression

        [Fact]
        public void ShouldSpawn_ActiveChainMember_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChainId = "chain-abc"
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: true, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("active chain being built", reason);
        }

        [Fact]
        public void ShouldSpawn_NotActiveChainMember_Allowed()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChainId = "chain-abc"
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Looping/Disabled Chain Suppression

        [Fact]
        public void ShouldSpawn_ChainLoopingOrDisabled_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChainId = "chain-loop"
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: true);

            Assert.False(needsSpawn);
            Assert.Contains("chain looping or fully disabled", reason);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Non-Leaf Tree Suppression

        [Fact]
        public void ShouldSpawn_NonLeafWithChildBranch_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = "branch-point-1"
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("non-leaf tree recording", reason);
        }

        [Fact]
        public void ShouldSpawn_LeafRecording_Allowed()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = null
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Terminal State Suppression

        [Fact]
        public void ShouldSpawn_Destroyed_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Destroyed
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state Destroyed", reason);
        }

        [Fact]
        public void ShouldSpawn_Recovered_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Recovered
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state Recovered", reason);
        }

        [Fact]
        public void ShouldSpawn_Docked_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Docked
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state Docked", reason);
        }

        [Fact]
        public void ShouldSpawn_Boarded_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Boarded
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state Boarded", reason);
        }

        [Fact]
        public void ShouldSpawn_Landed_Allowed()
        {
            // Landed is a non-blocking terminal state — vessel should still spawn
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawn_Orbiting_Allowed()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Orbiting
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawn_SubOrbital_ReturnsFalse()
        {
            // SubOrbital vessel would materialize mid-air and crash (#45)
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.SubOrbital
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("terminal state SubOrbital", reason);
        }

        [Fact]
        public void ShouldSpawn_Splashed_Allowed()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Splashed
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawn_NullTerminalState_Allowed()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = null
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — PID Dedup

        [Fact]
        public void ShouldSpawn_NonZeroPid_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                SpawnedVesselPersistentId = 42000
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("already spawned", reason);
            Assert.Contains("42000", reason);
        }

        [Fact]
        public void ShouldSpawn_ZeroPid_Allowed()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                SpawnedVesselPersistentId = 0
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Guard Priority

        [Fact]
        public void ShouldSpawn_MultipleGuardsTriggered_FirstWins()
        {
            // VesselDestroyed + branch > 0 + terminal Destroyed
            // VesselDestroyed should win (checked first after base conditions)
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselDestroyed = true,
                ChainBranch = 2,
                TerminalStateValue = TerminalState.Destroyed
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("vessel destroyed", reason);
        }

        [Fact]
        public void ShouldSpawn_BranchBeforeTerminal_BranchWins()
        {
            // Branch > 0 is checked before terminal state
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChainBranch = 1,
                TerminalStateValue = TerminalState.Recovered
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("branch > 0", reason);
        }

        #endregion

        #region CanRewind — Validation Paths

        [Fact]
        public void CanRewind_AlreadyRewinding_ReturnsFalse()
        {
            RewindContext.BeginRewind(0, default(BudgetSummary), 0, 0, 0);
            var rec = new Recording { RewindSaveFileName = "parsek_rw_test" };

            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: false));
            Assert.Equal("Rewind already in progress", reason);
        }

        [Fact]
        public void CanRewind_NoSaveFile_ReturnsFalse()
        {
            var rec = new Recording { RewindSaveFileName = null };

            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: false));
            Assert.Equal("No rewind save available", reason);
        }

        [Fact]
        public void CanRewind_EmptySaveFileName_ReturnsFalse()
        {
            var rec = new Recording { RewindSaveFileName = "" };

            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: false));
            Assert.Equal("No rewind save available", reason);
        }

        [Fact]
        public void CanRewind_RecordingInProgress_ReturnsFalse()
        {
            var rec = new Recording { RewindSaveFileName = "parsek_rw_test" };

            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: true));
            Assert.Equal("Stop recording before rewinding", reason);
        }

        [Fact]
        public void CanRewind_PendingTree_ReturnsFalse()
        {
            // Create a pending tree to trigger HasPendingTree
            RecordingStore.StashPendingTree(new RecordingTree());

            var rec = new Recording { RewindSaveFileName = "parsek_rw_test" };

            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: false));
            Assert.Equal("Merge or discard pending tree first", reason);
        }

        [Fact]
        public void CanRewind_PriorityOrder_AlreadyRewindingFirst()
        {
            // Even with pending tree and isRecording, "already rewinding" should win
            RewindContext.BeginRewind(0, default(BudgetSummary), 0, 0, 0);
            RecordingStore.StashPendingTree(new RecordingTree());

            var rec = new Recording { RewindSaveFileName = "parsek_rw_test" };

            string reason;
            Assert.False(RecordingStore.CanRewind(rec, out reason, isRecording: true));
            Assert.Equal("Rewind already in progress", reason);
        }

        #endregion

        #region ResetAllPlaybackState — Spawn Fields

        [Fact]
        public void ResetAllPlaybackState_ClearsSpawnedVesselPersistentId()
        {
            var rec = new Recording
            {
                VesselSpawned = true,
                SpawnedVesselPersistentId = 42000,
                SpawnAttempts = 2
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.ResetAllPlaybackState();

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(0, rec.SpawnAttempts);
        }

        [Fact]
        public void ResetAllPlaybackState_EnablesReSpawn()
        {
            // After reset, a recording that was previously spawned should be spawn-eligible again
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = true,
                SpawnedVesselPersistentId = 42000
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.ResetAllPlaybackState();

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region Revert Scenario — PID Reset Enables Re-Spawn

        [Fact]
        public void Revert_PidResetToZero_AllowsReSpawn()
        {
            // Simulates the revert flow: quicksave has PID=0, so after loading
            // the quicksave the recording's SpawnedVesselPersistentId is 0,
            // which allows the spawn to re-trigger.
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = false,
                SpawnedVesselPersistentId = 0 // Reset from quicksave
            };

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void Revert_PidStillSet_BlocksReSpawn()
        {
            // If somehow PID wasn't reset (e.g., save/load without revert),
            // spawn should be blocked to prevent duplicates.
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = false,
                SpawnedVesselPersistentId = 42000
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("already spawned", reason);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Non-Leaf / Effective Leaf

        [Fact]
        public void ShouldSpawn_NonLeafWithSamePidChild_ReturnsFalse()
        {
            // True non-leaf: branch point has a same-PID continuation child
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "TestTree",
                RootRecordingId = "parent-rec"
            };
            var parentRec = new Recording
            {
                RecordingId = "parent-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100,
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = "bp-decouple"
            };
            var continuationRec = new Recording
            {
                RecordingId = "continuation-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100, // Same PID — true continuation
                ParentBranchPointId = "bp-decouple"
            };
            var bp = new BranchPoint
            {
                Id = "bp-decouple",
                Type = BranchPointType.JointBreak,
                UT = 50.0
            };
            bp.ParentRecordingIds.Add("parent-rec");
            bp.ChildRecordingIds.Add("continuation-rec");
            tree.Recordings["parent-rec"] = parentRec;
            tree.Recordings["continuation-rec"] = continuationRec;
            tree.BranchPoints.Add(bp);
            RecordingStore.CommittedTrees.Add(tree);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                parentRec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.False(needsSpawn);
            Assert.Contains("non-leaf tree recording", reason);
        }

        [Fact]
        public void ShouldSpawn_NonLeafWithSamePidChildInPendingTreeContext_ReturnsFalse()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "TestTree",
                RootRecordingId = "parent-rec"
            };
            var parentRec = new Recording
            {
                RecordingId = "parent-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100,
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = "bp-decouple"
            };
            var continuationRec = new Recording
            {
                RecordingId = "continuation-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100,
                ParentBranchPointId = "bp-decouple"
            };
            var bp = new BranchPoint
            {
                Id = "bp-decouple",
                Type = BranchPointType.JointBreak,
                UT = 50.0
            };
            bp.ParentRecordingIds.Add("parent-rec");
            bp.ChildRecordingIds.Add("continuation-rec");
            tree.Recordings["parent-rec"] = parentRec;
            tree.Recordings["continuation-rec"] = continuationRec;
            tree.BranchPoints.Add(bp);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                parentRec, isActiveChainMember: false, isChainLoopingOrDisabled: false, tree);

            Assert.False(needsSpawn);
            Assert.Contains("non-leaf tree recording", reason);
        }

        [Fact]
        public void ShouldSpawn_EffectiveLeaf_BreakupDebrisOnly_ReturnsTrue()
        {
            // Breakup-continuous: ChildBranchPointId set but no same-PID child.
            // Only debris children exist. Recording IS the effective leaf. (#224)
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "TestTree",
                RootRecordingId = "parent-rec"
            };
            var parentRec = new Recording
            {
                RecordingId = "parent-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100,
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = "bp-crash",
                TerminalStateValue = TerminalState.Splashed
            };
            var debrisRec = new Recording
            {
                RecordingId = "debris-rec",
                TreeId = "tree-1",
                VesselPersistentId = 999, // Different PID — debris, not continuation
                ParentBranchPointId = "bp-crash",
                IsDebris = true
            };
            var bp = new BranchPoint
            {
                Id = "bp-crash",
                Type = BranchPointType.Breakup,
                UT = 102.8
            };
            bp.ParentRecordingIds.Add("parent-rec");
            bp.ChildRecordingIds.Add("debris-rec");
            tree.Recordings["parent-rec"] = parentRec;
            tree.Recordings["debris-rec"] = debrisRec;
            tree.BranchPoints.Add(bp);
            RecordingStore.CommittedTrees.Add(tree);

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                parentRec, isActiveChainMember: false, isChainLoopingOrDisabled: false);

            Assert.True(needsSpawn);
        }

        [Fact]
        public void ShouldSpawn_EffectiveLeaf_BreakupDebrisOnlyInPendingTreeContext_ReturnsTrue()
        {
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "TestTree",
                RootRecordingId = "parent-rec"
            };
            var parentRec = new Recording
            {
                RecordingId = "parent-rec",
                TreeId = "tree-1",
                VesselPersistentId = 100,
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChildBranchPointId = "bp-crash",
                TerminalStateValue = TerminalState.Splashed
            };
            var debrisRec = new Recording
            {
                RecordingId = "debris-rec",
                TreeId = "tree-1",
                VesselPersistentId = 999,
                ParentBranchPointId = "bp-crash",
                IsDebris = true
            };
            var bp = new BranchPoint
            {
                Id = "bp-crash",
                Type = BranchPointType.Breakup,
                UT = 102.8
            };
            bp.ParentRecordingIds.Add("parent-rec");
            bp.ChildRecordingIds.Add("debris-rec");
            tree.Recordings["parent-rec"] = parentRec;
            tree.Recordings["debris-rec"] = debrisRec;
            tree.BranchPoints.Add(bp);

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                parentRec, isActiveChainMember: false, isChainLoopingOrDisabled: false, tree);

            Assert.True(needsSpawn);
        }

        #endregion
    }
}
