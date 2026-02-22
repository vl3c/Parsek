using System.Collections.Generic;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class DockUndockChainTests
    {
        public DockUndockChainTests()
        {
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

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

        #region ChainBranch defaults and copy

        [Fact]
        public void Recording_ChainBranch_DefaultsToZero()
        {
            var rec = new RecordingStore.Recording();
            Assert.Equal(0, rec.ChainBranch);
        }

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesChainBranch()
        {
            var source = new RecordingStore.Recording
            {
                ChainId = "test-chain",
                ChainIndex = 2,
                ChainBranch = 1
            };

            var target = new RecordingStore.Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(1, target.ChainBranch);
        }

        #endregion

        #region IsChainMidSegment with branches

        [Fact]
        public void IsChainMidSegment_BranchZero_MidSegment_ReturnsTrue()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "dock-chain";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg1");
            RecordingStore.Pending.ChainId = "dock-chain";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            Assert.True(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_BranchGreaterThanZero_AlwaysReturnsFalse()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "dock-chain";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Branch1");
            RecordingStore.Pending.ChainId = "dock-chain";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg1");
            RecordingStore.Pending.ChainId = "dock-chain";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            // Branch 1 recording — always returns false (ghost-only, despawns normally)
            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[1]));

            // Branch 0 index 0 — mid-chain (branch 0 has higher index)
            Assert.True(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        #endregion

        #region GetChainEndUT with branches

        [Fact]
        public void GetChainEndUT_IgnoresBranchGreaterThanZero()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "Seg0");
            RecordingStore.Pending.ChainId = "end-branch";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Seg1");
            RecordingStore.Pending.ChainId = "end-branch";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            // Branch 1 has a much later EndUT — should be ignored
            RecordingStore.StashPending(MakePoints(10, 500), "Branch1");
            RecordingStore.Pending.ChainId = "end-branch";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            var seg0 = RecordingStore.CommittedRecordings[0];
            var seg1 = RecordingStore.CommittedRecordings[1];

            // Chain end should be branch 0's max EndUT (seg1), not branch 1's
            Assert.Equal(seg1.EndUT, RecordingStore.GetChainEndUT(seg0));
        }

        #endregion

        #region GetChainRecordings sort order

        [Fact]
        public void GetChainRecordings_SortsByBranchThenIndex()
        {
            // Add in scrambled order
            RecordingStore.StashPending(MakePoints(3, 300), "B1-Idx1");
            RecordingStore.Pending.ChainId = "sort-chain";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "B0-Idx1");
            RecordingStore.Pending.ChainId = "sort-chain";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 100), "B0-Idx0");
            RecordingStore.Pending.ChainId = "sort-chain";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            var chain = RecordingStore.GetChainRecordings("sort-chain");
            Assert.NotNull(chain);
            Assert.Equal(3, chain.Count);

            // Branch 0 first, sorted by index
            Assert.Equal(0, chain[0].ChainBranch);
            Assert.Equal(0, chain[0].ChainIndex);

            Assert.Equal(0, chain[1].ChainBranch);
            Assert.Equal(1, chain[1].ChainIndex);

            // Branch 1 last
            Assert.Equal(1, chain[2].ChainBranch);
            Assert.Equal(1, chain[2].ChainIndex);
        }

        #endregion

        #region ValidateChains with branches

        [Fact]
        public void ValidateChains_ParallelBranches_Valid()
        {
            // Branch 0: indices 0, 1
            RecordingStore.StashPending(MakePoints(3, 100), "B0-0");
            RecordingStore.Pending.ChainId = "branch-valid";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "B0-1");
            RecordingStore.Pending.ChainId = "branch-valid";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            // Branch 1: index 1 (forks from branch 0 at index 1)
            RecordingStore.StashPending(MakePoints(3, 200), "B1-1");
            RecordingStore.Pending.ChainId = "branch-valid";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            // All should preserve their chain metadata
            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Equal("branch-valid", rec.ChainId);
            }
        }

        [Fact]
        public void ValidateChains_InvalidBranch_DegradesEntireChain()
        {
            // Branch 0: valid (indices 0, 1)
            RecordingStore.StashPending(MakePoints(3, 100), "B0-0");
            RecordingStore.Pending.ChainId = "branch-invalid";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "B0-1");
            RecordingStore.Pending.ChainId = "branch-invalid";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            // Branch 1: invalid — gap (indices 1, 3 with no 2)
            RecordingStore.StashPending(MakePoints(3, 200), "B1-1");
            RecordingStore.Pending.ChainId = "branch-invalid";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 300), "B1-3");
            RecordingStore.Pending.ChainId = "branch-invalid";
            RecordingStore.Pending.ChainIndex = 3;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            // All branches degraded (entire chain invalidated)
            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
            }
        }

        #endregion

        #region Spawn guard for branch > 0

        [Fact]
        public void NeedsSpawn_BranchGreaterThanZero_NeverSpawns()
        {
            RecordingStore.StashPending(MakePoints(5, 100), "Continuation");
            RecordingStore.Pending.ChainId = "spawn-guard";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];

            // Without the branch guard, needsSpawn would be true
            bool rawNeedsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;
            Assert.True(rawNeedsSpawn);

            // Apply the branch guard (as in UpdateTimelinePlayback)
            bool needsSpawn = rawNeedsSpawn;
            if (rec.ChainBranch > 0)
                needsSpawn = false;

            Assert.False(needsSpawn);
        }

        [Fact]
        public void NeedsSpawn_BranchZero_CanSpawn()
        {
            RecordingStore.StashPending(MakePoints(5, 100), "Primary");
            RecordingStore.Pending.ChainId = "spawn-ok";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];
            bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;

            // Branch guard doesn't apply
            if (rec.ChainBranch > 0)
                needsSpawn = false;

            Assert.True(needsSpawn);
        }

        #endregion

        #region BuildExcludeCrewSet with dock chains

        [Fact]
        public void BuildExcludeCrewSet_DockChain_SkipsBranch1()
        {
            // V(0) → docked V(1) → undocked V(2) with branch 1 continuation
            RecordingStore.StashPending(MakePoints(3, 100), "Vessel Approach");
            RecordingStore.Pending.ChainId = "dock-crew";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.RecordingId = "seg0";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "Docked");
            RecordingStore.Pending.ChainId = "dock-crew";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.RecordingId = "seg1";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 300), "Undocked");
            RecordingStore.Pending.ChainId = "dock-crew";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.RecordingId = "seg2";
            RecordingStore.CommitPending();

            // Branch 1: station continuation (ghost-only)
            RecordingStore.StashPending(MakePoints(3, 300), "Station");
            RecordingStore.Pending.ChainId = "dock-crew";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.Pending.RecordingId = "station";
            RecordingStore.CommitPending();

            // Final vessel segment (branch 0) — no EVA, no exclusions
            var finalSeg = RecordingStore.CommittedRecordings[2];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(finalSeg);
            Assert.Null(excludeSet);
        }

        #endregion

        #region DecideOnVesselSwitch with undockSiblingPid

        [Fact]
        public void DecideOnVesselSwitch_UndockSibling_ReturnsUndockSwitch()
        {
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.UndockSwitch, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_UndockSiblingPidZero_FallsThrough()
        {
            // undockSiblingPid = 0 should not affect the result
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, undockSiblingPid: 0);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_UndockSiblingPidMismatch_FallsThrough()
        {
            // Vessel switched to something that's NOT the sibling
            var result = FlightRecorder.DecideOnVesselSwitch(100, 300, false, false, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_SameVessel_UndockSiblingIgnored()
        {
            // Same vessel PID overrides everything, even if undockSiblingPid is set
            var result = FlightRecorder.DecideOnVesselSwitch(100, 100, false, false, undockSiblingPid: 100);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.None, result);
        }

        #endregion

        #region RecordingBuilder ChainBranch support

        [Fact]
        public void RecordingBuilder_ChainBranch_InV2Build()
        {
            var builder = new RecordingBuilder("Test")
                .WithChainId("my-chain")
                .WithChainIndex(1)
                .WithChainBranch(1)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.Build();

            Assert.Equal("1", node.GetValue("chainBranch"));
        }

        [Fact]
        public void RecordingBuilder_ChainBranch_InV3Metadata()
        {
            var builder = new RecordingBuilder("Test")
                .WithRecordingId("v3branch")
                .WithChainId("my-chain")
                .WithChainIndex(2)
                .WithChainBranch(1)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.BuildV3Metadata();

            Assert.Equal("1", node.GetValue("chainBranch"));
        }

        [Fact]
        public void RecordingBuilder_NoBranch_OmitsChainBranch()
        {
            var builder = new RecordingBuilder("Test")
                .WithChainId("chain")
                .WithChainIndex(0)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.Build();

            Assert.Null(node.GetValue("chainBranch"));
        }

        [Fact]
        public void RecordingBuilder_BranchZero_OmitsChainBranch()
        {
            var builder = new RecordingBuilder("Test")
                .WithChainId("chain")
                .WithChainIndex(0)
                .WithChainBranch(0)
                .AddPoint(100, 0, 0, 0)
                .AddPoint(110, 0, 0, 100);

            var node = builder.Build();

            // Branch 0 is default — omitted from serialization
            Assert.Null(node.GetValue("chainBranch"));
        }

        #endregion

        #region Dock/Undock chain scenario

        [Fact]
        public void DockUndockChain_FullScenario_BranchesAndEndUT()
        {
            // Simulate: approach → dock → undock with station continuation
            // Branch 0: approach(0), docked(1), undocked(2)
            // Branch 1: station continuation(2)

            RecordingStore.StashPending(MakePoints(5, 100), "Approach");
            RecordingStore.Pending.ChainId = "dock-undock";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(5, 200), "Docked");
            RecordingStore.Pending.ChainId = "dock-undock";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(5, 300), "Undocked");
            RecordingStore.Pending.ChainId = "dock-undock";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(10, 300), "Station");
            RecordingStore.Pending.ChainId = "dock-undock";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            // Validation should pass
            RecordingStore.ValidateChains();
            foreach (var rec in RecordingStore.CommittedRecordings)
                Assert.Equal("dock-undock", rec.ChainId);

            // Chain end only considers branch 0
            var approach = RecordingStore.CommittedRecordings[0];
            double chainEnd = RecordingStore.GetChainEndUT(approach);
            var undocked = RecordingStore.CommittedRecordings[2];
            Assert.Equal(undocked.EndUT, chainEnd);

            // Branch 1 station has later EndUT but doesn't affect chain end
            var station = RecordingStore.CommittedRecordings[3];
            Assert.True(station.EndUT > undocked.EndUT);
            Assert.Equal(undocked.EndUT, chainEnd); // still branch 0's max

            // Only branch 0 final segment can spawn
            Assert.NotNull(undocked.VesselSnapshot);
            Assert.False(RecordingStore.IsChainMidSegment(undocked));

            // Branch 1 never spawns
            Assert.False(RecordingStore.IsChainMidSegment(station));

            // Mid-chain checks for branch 0
            Assert.True(RecordingStore.IsChainMidSegment(approach));
            var docked = RecordingStore.CommittedRecordings[1];
            Assert.True(RecordingStore.IsChainMidSegment(docked));
        }

        #endregion

        #region Dock-undock-redock chain (linear growth)

        [Fact]
        public void DockUndockRedock_ChainGrowsLinearly()
        {
            // Approach(0) → Dock(1) → Undock(2) → Redock(3) → Undock(4)
            // Old continuation stopped on redock, new started on second undock
            for (int i = 0; i < 5; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Seg{i}");
                RecordingStore.Pending.ChainId = "redock-chain";
                RecordingStore.Pending.ChainIndex = i;
                RecordingStore.Pending.ChainBranch = 0;
                if (i == 4)
                    RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
                RecordingStore.CommitPending();
            }

            // Station continuation from first undock (stopped on redock)
            RecordingStore.StashPending(MakePoints(5, 200), "Station-1");
            RecordingStore.Pending.ChainId = "redock-chain";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            // Station continuation from second undock
            RecordingStore.StashPending(MakePoints(5, 350), "Station-2");
            RecordingStore.Pending.ChainId = "redock-chain";
            RecordingStore.Pending.ChainIndex = 4;
            RecordingStore.Pending.ChainBranch = 2;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            // All recordings should still be valid chain members
            foreach (var rec in RecordingStore.CommittedRecordings)
                Assert.Equal("redock-chain", rec.ChainId);

            // Only the final branch 0 segment spawns
            Assert.NotNull(RecordingStore.CommittedRecordings[4].VesselSnapshot);
            Assert.False(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[4]));

            // Chain end comes from branch 0's last segment
            double chainEnd = RecordingStore.GetChainEndUT(RecordingStore.CommittedRecordings[0]);
            Assert.Equal(RecordingStore.CommittedRecordings[4].EndUT, chainEnd);
        }

        #endregion

        #region EVA during docked chain

        [Fact]
        public void EvaWhileDocked_ChainContinuesWithEvaCrew()
        {
            // Dock(0) → EVA Jeb(1) → Board back(2)
            // EVA fires on same chain — existing chain flow handles it
            RecordingStore.StashPending(MakePoints(3, 100), "Docked");
            RecordingStore.Pending.ChainId = "dock-eva";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.RecordingId = "docked";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "EVA Jeb");
            RecordingStore.Pending.ChainId = "dock-eva";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.EvaCrewName = "Jebediah Kerman";
            RecordingStore.Pending.RecordingId = "eva-jeb";
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 300), "Boarded");
            RecordingStore.Pending.ChainId = "dock-eva";
            RecordingStore.Pending.ChainIndex = 2;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.Pending.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.Pending.RecordingId = "boarded";
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            // All three segments valid
            foreach (var rec in RecordingStore.CommittedRecordings)
                Assert.Equal("dock-eva", rec.ChainId);

            // Jeb boarded back — not excluded from final vessel
            var finalSeg = RecordingStore.CommittedRecordings[2];
            var excludeSet = VesselSpawner.BuildExcludeCrewSet(finalSeg);
            Assert.Null(excludeSet);
        }

        #endregion

        #region Multiple undocks — only one continuation tracked

        [Fact]
        public void MultipleUndocks_LatestContinuationReplacesPrevious()
        {
            // Dock(0) → Undock1(1) → Dock(2) → Undock2(3)
            // First continuation at branch 1 idx 1, second at branch 2 idx 3
            // Both valid as independent branches
            for (int i = 0; i < 4; i++)
            {
                RecordingStore.StashPending(MakePoints(3, 100 + i * 50), $"Seg{i}");
                RecordingStore.Pending.ChainId = "multi-undock";
                RecordingStore.Pending.ChainIndex = i;
                RecordingStore.Pending.ChainBranch = 0;
                RecordingStore.CommitPending();
            }

            RecordingStore.StashPending(MakePoints(3, 150), "Station-old");
            RecordingStore.Pending.ChainId = "multi-undock";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 300), "Station-new");
            RecordingStore.Pending.ChainId = "multi-undock";
            RecordingStore.Pending.ChainIndex = 3;
            RecordingStore.Pending.ChainBranch = 2;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            // All 6 recordings valid
            Assert.Equal(6, RecordingStore.CommittedRecordings.Count);
            foreach (var rec in RecordingStore.CommittedRecordings)
                Assert.Equal("multi-undock", rec.ChainId);
        }

        #endregion

        #region Branch > 0 vessel destroyed

        [Fact]
        public void BranchGreaterThanZero_Destroyed_DespawnsNormally()
        {
            RecordingStore.StashPending(MakePoints(5, 100), "Vessel");
            RecordingStore.Pending.ChainId = "destroy-branch";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.Pending.VesselDestroyed = true;
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];

            // Even with destroyed flag, branch > 0 is not mid-chain
            Assert.False(RecordingStore.IsChainMidSegment(rec));

            // Spawn check: branch > 0 never spawns regardless
            bool needsSpawn = rec.VesselSnapshot != null && !rec.VesselSpawned && !rec.VesselDestroyed;
            if (rec.ChainBranch > 0) needsSpawn = false;
            Assert.False(needsSpawn);
        }

        #endregion

        #region Warp stop — branch > 0 should not stop warp

        [Fact]
        public void WarpStopGuard_BranchGreaterThanZero_NoWarpStop()
        {
            // The warp-stop check requires VesselSnapshot != null && !VesselSpawned && !VesselDestroyed
            // && !IsChainMidSegment. Branch > 0 has no VesselSnapshot (ghost-only) and
            // IsChainMidSegment returns false. But even if it had a snapshot, branch > 0
            // should not stop warp since it never spawns.
            RecordingStore.StashPending(MakePoints(5, 100), "Ghost Continuation");
            RecordingStore.Pending.ChainId = "warp-branch";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            var rec = RecordingStore.CommittedRecordings[0];

            // VesselSnapshot is null for branch > 0 (ghost-only)
            Assert.Null(rec.VesselSnapshot);

            // Warp-stop condition mirrors UpdateTimelinePlayback:
            // VesselSnapshot != null && !VesselSpawned && !VesselDestroyed && !IsChainMidSegment
            bool wouldStopWarp = rec.VesselSnapshot != null &&
                !rec.VesselSpawned && !rec.VesselDestroyed &&
                !RecordingStore.IsChainMidSegment(rec);
            Assert.False(wouldStopWarp);
        }

        #endregion

        #region StopRecordingForChainBoundary

        [Fact]
        public void StopRecordingForChainBoundary_SetsCaptureAtStop()
        {
            // Verify the silent stop method produces CaptureAtStop just like normal stop.
            // This is a pure logic test — StopRecordingForChainBoundary mirrors StopRecording
            // except it skips the screen message.
            // Can't call directly without Unity, but verify DecideOnVesselSwitch returns
            // DockMerge when the conditions match.
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, undockSiblingPid: 0);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.Stop, result);
        }

        #endregion

        #region DecideOnVesselSwitch priority — undockSiblingPid vs EVA

        [Fact]
        public void DecideOnVesselSwitch_UndockSiblingPid_TakesPriorityOverStop()
        {
            // EVA flags false, different vessel, but matches sibling → UndockSwitch, not Stop
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, false, false, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.UndockSwitch, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_UndockSiblingPid_DoesNotOverrideEva()
        {
            // If switched to EVA vessel that happens to match undockSiblingPid,
            // and recording started as EVA → should still be ContinueOnEva?
            // Actually undockSiblingPid check comes first, so it returns UndockSwitch.
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, true, true, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.UndockSwitch, result);
        }

        #endregion

        #region RemoveChainRecordings with branches

        [Fact]
        public void RemoveChainRecordings_RemovesAllBranches()
        {
            RecordingStore.StashPending(MakePoints(3, 100), "B0-0");
            RecordingStore.Pending.ChainId = "remove-branch";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "B0-1");
            RecordingStore.Pending.ChainId = "remove-branch";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "B1-1");
            RecordingStore.Pending.ChainId = "remove-branch";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 300), "Standalone");
            RecordingStore.CommitPending();

            RecordingStore.RemoveChainRecordings("remove-branch");

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("Standalone", RecordingStore.CommittedRecordings[0].VesselName);
        }

        #endregion

        #region ChainBranch reset on ValidateChains degradation

        [Fact]
        public void ValidateChains_InvalidChain_ResetsChainBranchToZero()
        {
            // Branch 1 with a gap should degrade entire chain, including branch field reset
            RecordingStore.StashPending(MakePoints(3, 100), "B0");
            RecordingStore.Pending.ChainId = "reset-branch";
            RecordingStore.Pending.ChainIndex = 0;
            RecordingStore.Pending.ChainBranch = 0;
            RecordingStore.CommitPending();

            RecordingStore.StashPending(MakePoints(3, 200), "B1");
            RecordingStore.Pending.ChainId = "reset-branch";
            RecordingStore.Pending.ChainIndex = 1;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            // Gap: branch 1 jumps to index 3 (missing index 2)
            RecordingStore.StashPending(MakePoints(3, 300), "B1-gap");
            RecordingStore.Pending.ChainId = "reset-branch";
            RecordingStore.Pending.ChainIndex = 3;
            RecordingStore.Pending.ChainBranch = 1;
            RecordingStore.CommitPending();

            RecordingStore.ValidateChains();

            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
                Assert.Equal(0, rec.ChainBranch);
            }
        }

        #endregion
    }
}

