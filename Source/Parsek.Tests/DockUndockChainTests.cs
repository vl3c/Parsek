using System;
using System.Collections.Generic;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class DockUndockChainTests : IDisposable
    {
        public DockUndockChainTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
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
            var rec = new Recording();
            Assert.Equal(0, rec.ChainBranch);
        }

        [Fact]
        public void ApplyPersistenceArtifacts_CopiesChainBranch()
        {
            var source = new Recording
            {
                ChainId = "test-chain",
                ChainIndex = 2,
                ChainBranch = 1
            };

            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(1, target.ChainBranch);
        }

        #endregion

        #region IsChainMidSegment with branches

        [Fact]
        public void IsChainMidSegment_BranchZero_MidSegment_ReturnsTrue()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "dock-chain";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg1");
            Assert.NotNull(rec2);
            rec2.ChainId = "dock-chain";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            Assert.True(RecordingStore.IsChainMidSegment(RecordingStore.CommittedRecordings[0]));
        }

        [Fact]
        public void IsChainMidSegment_BranchGreaterThanZero_AlwaysReturnsFalse()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "dock-chain";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Branch1");
            Assert.NotNull(rec2);
            rec2.ChainId = "dock-chain";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg1");
            Assert.NotNull(rec3);
            rec3.ChainId = "dock-chain";
            rec3.ChainIndex = 1;
            rec3.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec3);

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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Seg0");
            Assert.NotNull(rec1);
            rec1.ChainId = "end-branch";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Seg1");
            Assert.NotNull(rec2);
            rec2.ChainId = "end-branch";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            // Branch 1 has a much later EndUT — should be ignored
            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(10, 500), "Branch1");
            Assert.NotNull(rec3);
            rec3.ChainId = "end-branch";
            rec3.ChainIndex = 1;
            rec3.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec3);

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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "B1-Idx1");
            Assert.NotNull(rec1);
            rec1.ChainId = "sort-chain";
            rec1.ChainIndex = 1;
            rec1.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B0-Idx1");
            Assert.NotNull(rec2);
            rec2.ChainId = "sort-chain";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "B0-Idx0");
            Assert.NotNull(rec3);
            rec3.ChainId = "sort-chain";
            rec3.ChainIndex = 0;
            rec3.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec3);

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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "B0-0");
            Assert.NotNull(rec1);
            rec1.ChainId = "branch-valid";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B0-1");
            Assert.NotNull(rec2);
            rec2.ChainId = "branch-valid";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            // Branch 1: index 1 (forks from branch 0 at index 1)
            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B1-1");
            Assert.NotNull(rec3);
            rec3.ChainId = "branch-valid";
            rec3.ChainIndex = 1;
            rec3.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec3);

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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "B0-0");
            Assert.NotNull(rec1);
            rec1.ChainId = "branch-invalid";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B0-1");
            Assert.NotNull(rec2);
            rec2.ChainId = "branch-invalid";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            // Branch 1: invalid — gap (indices 1, 3 with no 2)
            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B1-1");
            Assert.NotNull(rec3);
            rec3.ChainId = "branch-invalid";
            rec3.ChainIndex = 1;
            rec3.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec3);

            var rec4 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "B1-3");
            Assert.NotNull(rec4);
            rec4.ChainId = "branch-invalid";
            rec4.ChainIndex = 3;
            rec4.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec4);

            RecordingStore.ValidateChains();

            // All branches degraded (entire chain invalidated)
            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
            }
        }

        #endregion

        #region BuildExcludeCrewSet with dock chains

        [Fact]
        public void BuildExcludeCrewSet_DockChain_SkipsBranch1()
        {
            // V(0) -> docked V(1) -> undocked V(2) with branch 1 continuation
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Vessel Approach");
            Assert.NotNull(rec1);
            rec1.ChainId = "dock-crew";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            rec1.RecordingId = "seg0";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "Docked");
            Assert.NotNull(rec2);
            rec2.ChainId = "dock-crew";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            rec2.RecordingId = "seg1";
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "Undocked");
            Assert.NotNull(rec3);
            rec3.ChainId = "dock-crew";
            rec3.ChainIndex = 2;
            rec3.ChainBranch = 0;
            rec3.RecordingId = "seg2";
            RecordingStore.CommitRecordingDirect(rec3);

            // Branch 1: station continuation (ghost-only)
            var rec4 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "Station");
            Assert.NotNull(rec4);
            rec4.ChainId = "dock-crew";
            rec4.ChainIndex = 2;
            rec4.ChainBranch = 1;
            rec4.RecordingId = "station";
            RecordingStore.CommitRecordingDirect(rec4);

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
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
        }

        [Fact]
        public void DecideOnVesselSwitch_UndockSiblingPidMismatch_FallsThrough()
        {
            // Vessel switched to something that's NOT the sibling
            var result = FlightRecorder.DecideOnVesselSwitch(100, 300, false, false, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.TransitionToBackground, result);
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
            // Simulate: approach -> dock -> undock with station continuation
            // Branch 0: approach(0), docked(1), undocked(2)
            // Branch 1: station continuation(2)

            var approachRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(5, 100), "Approach");
            Assert.NotNull(approachRec);
            approachRec.ChainId = "dock-undock";
            approachRec.ChainIndex = 0;
            approachRec.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(approachRec);

            var dockedRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(5, 200), "Docked");
            Assert.NotNull(dockedRec);
            dockedRec.ChainId = "dock-undock";
            dockedRec.ChainIndex = 1;
            dockedRec.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(dockedRec);

            var undockedRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(5, 300), "Undocked");
            Assert.NotNull(undockedRec);
            undockedRec.ChainId = "dock-undock";
            undockedRec.ChainIndex = 2;
            undockedRec.ChainBranch = 0;
            undockedRec.VesselSnapshot = new ConfigNode("VESSEL");
            RecordingStore.CommitRecordingDirect(undockedRec);

            var stationRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(10, 300), "Station");
            Assert.NotNull(stationRec);
            stationRec.ChainId = "dock-undock";
            stationRec.ChainIndex = 2;
            stationRec.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(stationRec);

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
            // Approach(0) -> Dock(1) -> Undock(2) -> Redock(3) -> Undock(4)
            // Old continuation stopped on redock, new started on second undock
            for (int i = 0; i < 5; i++)
            {
                var segRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Seg{i}");
                Assert.NotNull(segRec);
                segRec.ChainId = "redock-chain";
                segRec.ChainIndex = i;
                segRec.ChainBranch = 0;
                if (i == 4)
                    segRec.VesselSnapshot = new ConfigNode("VESSEL");
                RecordingStore.CommitRecordingDirect(segRec);
            }

            // Station continuation from first undock (stopped on redock)
            var station1Rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(5, 200), "Station-1");
            Assert.NotNull(station1Rec);
            station1Rec.ChainId = "redock-chain";
            station1Rec.ChainIndex = 2;
            station1Rec.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(station1Rec);

            // Station continuation from second undock
            var station2Rec = RecordingStore.CreateRecordingFromFlightData(MakePoints(5, 350), "Station-2");
            Assert.NotNull(station2Rec);
            station2Rec.ChainId = "redock-chain";
            station2Rec.ChainIndex = 4;
            station2Rec.ChainBranch = 2;
            RecordingStore.CommitRecordingDirect(station2Rec);

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
            // Dock(0) -> EVA Jeb(1) -> Board back(2)
            // EVA fires on same chain — existing chain flow handles it
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "Docked");
            Assert.NotNull(rec1);
            rec1.ChainId = "dock-eva";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            rec1.RecordingId = "docked";
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "EVA Jeb");
            Assert.NotNull(rec2);
            rec2.ChainId = "dock-eva";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            rec2.EvaCrewName = "Jebediah Kerman";
            rec2.RecordingId = "eva-jeb";
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "Boarded");
            Assert.NotNull(rec3);
            rec3.ChainId = "dock-eva";
            rec3.ChainIndex = 2;
            rec3.ChainBranch = 0;
            rec3.VesselSnapshot = new ConfigNode("VESSEL");
            rec3.RecordingId = "boarded";
            RecordingStore.CommitRecordingDirect(rec3);

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
            // Dock(0) -> Undock1(1) -> Dock(2) -> Undock2(3)
            // First continuation at branch 1 idx 1, second at branch 2 idx 3
            // Both valid as independent branches
            for (int i = 0; i < 4; i++)
            {
                var segRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100 + i * 50), $"Seg{i}");
                Assert.NotNull(segRec);
                segRec.ChainId = "multi-undock";
                segRec.ChainIndex = i;
                segRec.ChainBranch = 0;
                RecordingStore.CommitRecordingDirect(segRec);
            }

            var stationOldRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 150), "Station-old");
            Assert.NotNull(stationOldRec);
            stationOldRec.ChainId = "multi-undock";
            stationOldRec.ChainIndex = 1;
            stationOldRec.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(stationOldRec);

            var stationNewRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "Station-new");
            Assert.NotNull(stationNewRec);
            stationNewRec.ChainId = "multi-undock";
            stationNewRec.ChainIndex = 3;
            stationNewRec.ChainBranch = 2;
            RecordingStore.CommitRecordingDirect(stationNewRec);

            RecordingStore.ValidateChains();

            // All 6 recordings valid
            Assert.Equal(6, RecordingStore.CommittedRecordings.Count);
            foreach (var rec in RecordingStore.CommittedRecordings)
                Assert.Equal("multi-undock", rec.ChainId);
        }

        #endregion

        #region DecideOnVesselSwitch priority — undockSiblingPid vs EVA

        [Fact]
        public void DecideOnVesselSwitch_UndockSiblingPid_DoesNotOverrideEva()
        {
            // If switched to EVA vessel that happens to match undockSiblingPid,
            // and recording started as EVA -> should still be ContinueOnEva?
            // Actually undockSiblingPid check comes first, so it returns UndockSwitch.
            var result = FlightRecorder.DecideOnVesselSwitch(100, 200, true, true, undockSiblingPid: 200);
            Assert.Equal(FlightRecorder.VesselSwitchDecision.UndockSwitch, result);
        }

        #endregion

        #region RemoveChainRecordings with branches

        [Fact]
        public void RemoveChainRecordings_RemovesAllBranches()
        {
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "B0-0");
            Assert.NotNull(rec1);
            rec1.ChainId = "remove-branch";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B0-1");
            Assert.NotNull(rec2);
            rec2.ChainId = "remove-branch";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec2);

            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B1-1");
            Assert.NotNull(rec3);
            rec3.ChainId = "remove-branch";
            rec3.ChainIndex = 1;
            rec3.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec3);

            var standaloneRec = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "Standalone");
            Assert.NotNull(standaloneRec);
            RecordingStore.CommitRecordingDirect(standaloneRec);

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
            var rec1 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 100), "B0");
            Assert.NotNull(rec1);
            rec1.ChainId = "reset-branch";
            rec1.ChainIndex = 0;
            rec1.ChainBranch = 0;
            RecordingStore.CommitRecordingDirect(rec1);

            var rec2 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 200), "B1");
            Assert.NotNull(rec2);
            rec2.ChainId = "reset-branch";
            rec2.ChainIndex = 1;
            rec2.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec2);

            // Gap: branch 1 jumps to index 3 (missing index 2)
            var rec3 = RecordingStore.CreateRecordingFromFlightData(MakePoints(3, 300), "B1-gap");
            Assert.NotNull(rec3);
            rec3.ChainId = "reset-branch";
            rec3.ChainIndex = 3;
            rec3.ChainBranch = 1;
            RecordingStore.CommitRecordingDirect(rec3);

            RecordingStore.ValidateChains();

            foreach (var rec in RecordingStore.CommittedRecordings)
            {
                Assert.Null(rec.ChainId);
                Assert.Equal(-1, rec.ChainIndex);
                Assert.Equal(0, rec.ChainBranch);
            }
        }

        #endregion

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }
    }
}
