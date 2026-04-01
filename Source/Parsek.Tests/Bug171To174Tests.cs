using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug fixes #171-174.
    /// </summary>
    public class Bug171_ZoneHideWarpExemptionTests
    {
        #region ShouldExemptFromZoneHide

        [Fact]
        public void NoWarp_OrbitalGhost_NotExempt()
        {
            Assert.False(GhostPlaybackLogic.ShouldExemptFromZoneHide(1f, true));
        }

        [Fact]
        public void Warp5x_OrbitalGhost_Exempt()
        {
            Assert.True(GhostPlaybackLogic.ShouldExemptFromZoneHide(5f, true));
        }

        [Fact]
        public void Warp10x_OrbitalGhost_Exempt()
        {
            Assert.True(GhostPlaybackLogic.ShouldExemptFromZoneHide(10f, true));
        }

        [Fact]
        public void Warp50x_OrbitalGhost_Exempt()
        {
            Assert.True(GhostPlaybackLogic.ShouldExemptFromZoneHide(50f, true));
        }

        [Fact]
        public void Warp4x_OrbitalGhost_NotExempt_AtThreshold()
        {
            // Threshold is >4, so exactly 4 is not exempt
            Assert.False(GhostPlaybackLogic.ShouldExemptFromZoneHide(4f, true));
        }

        [Fact]
        public void Warp10x_NoOrbitalSegments_NotExempt()
        {
            Assert.False(GhostPlaybackLogic.ShouldExemptFromZoneHide(10f, false));
        }

        [Fact]
        public void Warp50x_NoOrbitalSegments_NotExempt()
        {
            Assert.False(GhostPlaybackLogic.ShouldExemptFromZoneHide(50f, false));
        }

        [Fact]
        public void NoWarp_NoOrbitalSegments_NotExempt()
        {
            Assert.False(GhostPlaybackLogic.ShouldExemptFromZoneHide(1f, false));
        }

        #endregion
    }

    public class Bug173_ZeroPointLeafPruningTests
    {
        #region IsZeroPointLeaf

        [Fact]
        public void LeafWithNoData_IsZeroPoint()
        {
            var rec = new Recording
            {
                RecordingId = "leaf-1",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = null,
                ChildBranchPointId = null
            };
            Assert.True(ParsekFlight.IsZeroPointLeaf(rec));
        }

        [Fact]
        public void LeafWithPoints_NotZeroPoint()
        {
            var rec = new Recording
            {
                RecordingId = "leaf-2",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100 }
                },
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = null,
                ChildBranchPointId = null
            };
            Assert.False(ParsekFlight.IsZeroPointLeaf(rec));
        }

        [Fact]
        public void LeafWithOrbitSegments_NotZeroPoint()
        {
            var rec = new Recording
            {
                RecordingId = "leaf-3",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100, endUT = 200 }
                },
                SurfacePos = null,
                ChildBranchPointId = null
            };
            Assert.False(ParsekFlight.IsZeroPointLeaf(rec));
        }

        [Fact]
        public void LeafWithSurfacePos_NotZeroPoint()
        {
            var rec = new Recording
            {
                RecordingId = "leaf-4",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = new SurfacePosition { latitude = 0, longitude = 0, altitude = 0 },
                ChildBranchPointId = null
            };
            Assert.False(ParsekFlight.IsZeroPointLeaf(rec));
        }

        [Fact]
        public void NonLeaf_WithNoData_NotZeroPoint()
        {
            var rec = new Recording
            {
                RecordingId = "non-leaf",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = null,
                ChildBranchPointId = "bp-1"
            };
            Assert.False(ParsekFlight.IsZeroPointLeaf(rec));
        }

        #endregion

        #region CollectZeroPointLeafIds

        [Fact]
        public void CollectZeroPointLeafIds_NoZeroPointLeaves_ReturnsNull()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                Points = new List<TrajectoryPoint> { new TrajectoryPoint { ut = 1 } },
                OrbitSegments = new List<OrbitSegment>(),
                ChildBranchPointId = null
            };

            var result = ParsekFlight.CollectZeroPointLeafIds(tree);
            Assert.Null(result);
        }

        [Fact]
        public void CollectZeroPointLeafIds_TwoZeroPointLeaves_ReturnsBothIds()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                Points = new List<TrajectoryPoint> { new TrajectoryPoint { ut = 1 } },
                OrbitSegments = new List<OrbitSegment>(),
                ChildBranchPointId = "bp1"
            };
            tree.Recordings["debris-a"] = new Recording
            {
                RecordingId = "debris-a",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = null,
                ChildBranchPointId = null
            };
            tree.Recordings["debris-b"] = new Recording
            {
                RecordingId = "debris-b",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = null,
                ChildBranchPointId = null
            };

            var result = ParsekFlight.CollectZeroPointLeafIds(tree);
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Contains("debris-a", result);
            Assert.Contains("debris-b", result);
        }

        [Fact]
        public void CollectZeroPointLeafIds_MixedLeaves_OnlyReturnsZeroPoint()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Test" };
            tree.Recordings["good-leaf"] = new Recording
            {
                RecordingId = "good-leaf",
                Points = new List<TrajectoryPoint> { new TrajectoryPoint { ut = 50 } },
                OrbitSegments = new List<OrbitSegment>(),
                ChildBranchPointId = null
            };
            tree.Recordings["zero-leaf"] = new Recording
            {
                RecordingId = "zero-leaf",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                SurfacePos = null,
                ChildBranchPointId = null
            };

            var result = ParsekFlight.CollectZeroPointLeafIds(tree);
            Assert.NotNull(result);
            Assert.Single(result);
            Assert.Equal("zero-leaf", result[0]);
        }

        #endregion
    }

    [Collection("Sequential")]
    public class Bug174_TerminatedChainSkipTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug174_TerminatedChainSkipTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region IsTreeFullyTerminated

        [Fact]
        public void EmptyTree_IsFullyTerminated()
        {
            var tree = new RecordingTree { Id = "t1" };
            Assert.True(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void AllLeavesDestroyed_IsFullyTerminated()
        {
            var tree = new RecordingTree { Id = "t1" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                TerminalStateValue = TerminalState.Destroyed,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            tree.Recordings["r2"] = new Recording
            {
                RecordingId = "r2",
                TerminalStateValue = TerminalState.Recovered,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            Assert.True(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void LeafWithNoTerminalState_NotFullyTerminated()
        {
            var tree = new RecordingTree { Id = "t1" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                TerminalStateValue = null,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            Assert.False(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void LeafLanded_NotFullyTerminated()
        {
            var tree = new RecordingTree { Id = "t1" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                TerminalStateValue = TerminalState.Landed,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            Assert.False(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void NonLeaf_Destroyed_IgnoredByCheck()
        {
            // Non-leaf recording (has ChildBranchPointId) should be ignored;
            // only leaf terminals matter.
            var tree = new RecordingTree { Id = "t1" };
            tree.Recordings["parent"] = new Recording
            {
                RecordingId = "parent",
                TerminalStateValue = TerminalState.Destroyed,
                ChildBranchPointId = "bp1",
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            tree.Recordings["child"] = new Recording
            {
                RecordingId = "child",
                TerminalStateValue = TerminalState.Orbiting,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            Assert.False(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        [Fact]
        public void MixedLeaves_OneOrbitingOneDestroyed_NotFullyTerminated()
        {
            var tree = new RecordingTree { Id = "t1" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                TerminalStateValue = TerminalState.Destroyed,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            tree.Recordings["r2"] = new Recording
            {
                RecordingId = "r2",
                TerminalStateValue = TerminalState.Orbiting,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>()
            };
            Assert.False(GhostChainWalker.IsTreeFullyTerminated(tree));
        }

        #endregion

        #region ComputeAllGhostChains skips fully-terminated trees

        [Fact]
        public void FullyTerminatedTree_SkippedInChainComputation()
        {
            // Tree with one leaf, destroyed — should be skipped entirely
            var tree = new RecordingTree { Id = "t1" };
            tree.Recordings["r1"] = new Recording
            {
                RecordingId = "r1",
                VesselPersistentId = 100,
                TerminalStateValue = TerminalState.Destroyed,
                ChildBranchPointId = null,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>(),
                ExplicitStartUT = 10, ExplicitEndUT = 20
            };

            var trees = new List<RecordingTree> { tree };
            var chains = GhostChainWalker.ComputeAllGhostChains(trees, 0);

            Assert.Empty(chains);
            Assert.Contains(logLines, l => l.Contains("fully-terminated"));
        }

        #endregion
    }
}
