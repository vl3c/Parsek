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

        [Fact]
        public void ShouldSkipTimelinePlaybackForPendingReFlyInvoke_ReturnsPendingState()
        {
            Assert.True(GhostPlaybackLogic.ShouldSkipTimelinePlaybackForPendingReFlyInvoke(true));
            Assert.False(GhostPlaybackLogic.ShouldSkipTimelinePlaybackForPendingReFlyInvoke(false));
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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

            Assert.False(needsSpawn);
            Assert.Contains("vessel destroyed", reason);
        }

        [Fact]
        public void ShouldSpawn_DestroyedWithoutSnapshot_ReturnsDestroyedReason()
        {
            var rec = new Recording
            {
                VesselSnapshot = null,
                VesselSpawned = false,
                VesselDestroyed = true
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLooping: false);

            Assert.False(needsSpawn);
            Assert.Equal("vessel destroyed", reason);
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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: true, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

            Assert.True(needsSpawn);
        }

        #endregion

        #region ShouldSpawnAtRecordingEnd — Looping Chain Suppression

        [Fact]
        public void ShouldSpawn_ChainLooping_ReturnsFalse()
        {
            var rec = new Recording
            {
                VesselSnapshot = new ConfigNode("VESSEL"),
                ChainId = "chain-loop"
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, isActiveChainMember: false, isChainLooping: true);

            Assert.False(needsSpawn);
            Assert.Contains("chain looping", reason);
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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                rec, isActiveChainMember: false, isChainLooping: false);

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
                parentRec, isActiveChainMember: false, isChainLooping: false);

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
                parentRec, isActiveChainMember: false, isChainLooping: false, tree);

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
                parentRec, isActiveChainMember: false, isChainLooping: false);

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
                parentRec, isActiveChainMember: false, isChainLooping: false, tree);

            Assert.True(needsSpawn);
        }

        #endregion

        #region SpawnSuppressedByRewind (#573 follow-up to PR #541)

        /// <summary>
        /// Production-sequence regression for #573: the active/source recording
        /// protected by rewind strip cleanup must NOT spawn a duplicate real vessel
        /// after a plain Rewind-to-Launch, even though
        /// <see cref="ParsekScenario.HandleRewindOnLoad"/> has already cleared
        /// <see cref="RewindContext.IsRewinding"/> by the time the FLIGHT
        /// update path picks up terminal activation.
        /// </summary>
        [Fact]
        public void ShouldSpawn_PostRewindSameRecordingProtection_ReturnsFalse()
        {
            const uint kBoosterPid = 2708531065u;
            var sourceRecording = new Recording
            {
                RecordingId = "source-rewound",
                VesselName = "Kerbal X (post-rewind source)",
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselSpawned = false,
                VesselPersistentId = kBoosterPid,
                ChainId = "89e6ecae5d184f26bf84973c138e36aa",
                ChainIndex = 0,
                TerminalStateValue = TerminalState.Splashed,
                SpawnSuppressedByRewind = true,
                SpawnSuppressedByRewindReason = ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                sourceRecording, isActiveChainMember: false, isChainLooping: false);

            Assert.False(needsSpawn);
            Assert.Contains("spawn suppressed post-rewind", reason);
            Assert.Contains("#573", reason);
        }

        /// <summary>
        /// Driving the production sequence end-to-end through
        /// <see cref="ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly"/>
        /// (the helper that <see cref="ParsekScenario.HandleRewindOnLoad"/>
        /// invokes after <see cref="RecordingStore.ResetAllPlaybackState"/>):
        /// only the active/source recording gets <c>SpawnSuppressedByRewind</c>
        /// set. Same-tree future recordings remain eligible for normal terminal
        /// materialization when playback reaches their EndUT (#589).
        /// </summary>
        [Fact]
        public void HandleRewindOnLoad_SuppressesSourceOnly_FutureSameTreeSpawnAllowed()
        {
            const uint kBoosterPid = 2708531065u;
            const string kTreeId = "7e46a9f16c9a4dcd90d1c1baaea6e2f5";
            const string kChainId = "89e6ecae5d184f26bf84973c138e36aa";
            const double kRewindUT = 100.0;

            var boosterRoot = new Recording
            {
                RecordingId = "8e27ba1144a7484b815847c05c49d10e",
                VesselName = "Kerbal X",
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselPersistentId = kBoosterPid,
                TreeId = kTreeId,
                ChainId = kChainId,
                ChainIndex = 0,
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 200.0,
                TerminalStateValue = TerminalState.Splashed,
            };
            var futureLander = new Recording
            {
                RecordingId = "b85acd51ea7f4005bb5d879207749e8c",
                VesselName = "Kerbal X Mun Lander",
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselPersistentId = kBoosterPid,
                TreeId = kTreeId,
                ExplicitStartUT = 24034.0,
                ExplicitEndUT = 24062.0,
                TerminalStateValue = TerminalState.Landed,
            };
            var unrelatedFromOtherTree = new Recording
            {
                RecordingId = "unrelated-other-tree",
                VesselName = "Mun Lander",
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselPersistentId = 12345u,
                TreeId = "different-tree",
                TerminalStateValue = TerminalState.Landed,
            };

            var recordings = new List<Recording>
            {
                boosterRoot, futureLander, unrelatedFromOtherTree
            };

            // Simulate HandleRewindOnLoad's state right after ResetAllPlaybackState:
            // VesselSpawned=false on every recording, RewindReplayTargetSourcePid armed,
            // RewindReplayTargetRecordingId points at the booster root.
            RecordingStore.RewindReplayTargetSourcePid = kBoosterPid;
            RecordingStore.RewindReplayTargetRecordingId = boosterRoot.RecordingId;
            RewindContext.BeginRewind(kRewindUT, default(BudgetSummary), 0, 0, 0);

            int marked = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(recordings);

            try
            {
                Assert.Equal(1, marked);
                Assert.True(boosterRoot.SpawnSuppressedByRewind);
                Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                    boosterRoot.SpawnSuppressedByRewindReason);
                Assert.False(futureLander.SpawnSuppressedByRewind);
                Assert.False(unrelatedFromOtherTree.SpawnSuppressedByRewind);

                var rootResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    boosterRoot, isActiveChainMember: false, isChainLooping: false);
                Assert.False(rootResult.needsSpawn);
                Assert.Contains("spawn suppressed post-rewind", rootResult.reason);

                var futureResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    futureLander, isActiveChainMember: false, isChainLooping: false);
                Assert.True(futureResult.needsSpawn);

                var kscResult = GhostPlaybackLogic.ShouldSpawnAtKscEnd(
                    futureLander, currentUT: futureLander.EndUT + 1.0);
                Assert.True(kscResult.needsSpawn);

                // Unrelated tree is unaffected — only the rewound tree's
                // source recording flips to protected.
                var unrelatedResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                    unrelatedFromOtherTree, isActiveChainMember: false, isChainLooping: false);
                Assert.True(unrelatedResult.needsSpawn);
            }
            finally
            {
                RecordingStore.RewindReplayTargetSourcePid = 0;
                RecordingStore.RewindReplayTargetRecordingId = null;
                RewindContext.ResetForTesting();
            }
        }

        /// <summary>
        /// Standalone-rewind variant: when the rewind owner's TreeId is null
        /// (rare, but exercised by some legacy fixtures), the helper still
        /// suppresses recordings whose <c>VesselPersistentId</c> matches the
        /// armed <see cref="RecordingStore.RewindReplayTargetSourcePid"/>.
        /// </summary>
        [Fact]
        public void MarkRewoundTreeRecordingsAsGhostOnly_StandaloneRecording_MarksByPidMatch()
        {
            const uint kSourcePid = 4242u;
            var standaloneRec = new Recording
            {
                RecordingId = "standalone-rec",
                VesselName = "Standalone",
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselPersistentId = kSourcePid,
                TreeId = null,
                TerminalStateValue = TerminalState.Landed,
            };
            var unrelated = new Recording
            {
                RecordingId = "unrelated-rec",
                VesselName = "Other",
                VesselPersistentId = 9999u,
                TreeId = null,
            };

            var recordings = new List<Recording> { standaloneRec, unrelated };

            RecordingStore.RewindReplayTargetSourcePid = kSourcePid;
            RecordingStore.RewindReplayTargetRecordingId = standaloneRec.RecordingId;

            try
            {
                int marked = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(recordings);

                Assert.Equal(1, marked);
                Assert.True(standaloneRec.SpawnSuppressedByRewind);
                Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                    standaloneRec.SpawnSuppressedByRewindReason);
                Assert.False(unrelated.SpawnSuppressedByRewind);
            }
            finally
            {
                RecordingStore.RewindReplayTargetSourcePid = 0;
                RecordingStore.RewindReplayTargetRecordingId = null;
            }
        }

        /// <summary>
        /// Idempotency: an already-suppressed source recording is retained in
        /// the protected-source count, and missing legacy metadata is refreshed
        /// to the scoped same-recording reason.
        /// </summary>
        [Fact]
        public void MarkRewoundTreeRecordingsAsGhostOnly_AlreadyMarked_ReturnsRetainedSource()
        {
            const uint kSourcePid = 7777u;
            var rec = new Recording
            {
                RecordingId = "rec-idempotent",
                VesselName = "Rewound",
                VesselPersistentId = kSourcePid,
                TreeId = "tree-rewound",
                SpawnSuppressedByRewind = true,
            };

            var recordings = new List<Recording> { rec };

            RecordingStore.RewindReplayTargetSourcePid = kSourcePid;
            RecordingStore.RewindReplayTargetRecordingId = rec.RecordingId;

            try
            {
                int protectedCount = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(recordings);

                Assert.Equal(1, protectedCount);
                Assert.True(rec.SpawnSuppressedByRewind);
                Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                    rec.SpawnSuppressedByRewindReason);
            }
            finally
            {
                RecordingStore.RewindReplayTargetSourcePid = 0;
                RecordingStore.RewindReplayTargetRecordingId = null;
            }
        }

        /// <summary>
        /// ResetRecordingPlaybackFields clears the marker so a SUBSEQUENT
        /// rewind starts from a clean slate. Without this, a second rewind
        /// can never refresh the suppression scope (e.g., when a different
        /// tree gets rewound after the first one).
        /// </summary>
        [Fact]
        public void ResetAllPlaybackState_ClearsSpawnSuppressedByRewind()
        {
            var rec = new Recording
            {
                RecordingId = "rec-reset-clears",
                VesselName = "Cleared",
                VesselSnapshot = new ConfigNode("VESSEL"),
                VesselPersistentId = 5555u,
                SpawnSuppressedByRewind = true,
                SpawnSuppressedByRewindReason = ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                SpawnSuppressedByRewindUT = 123.0,
            };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.ResetAllPlaybackState();

            Assert.False(rec.SpawnSuppressedByRewind);
            Assert.Null(rec.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(rec.SpawnSuppressedByRewindUT));
        }

        #endregion
    }
}
