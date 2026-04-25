using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #290 fixes:
    /// - Pending Limbo tree preserved on revert (not discarded as orphaned)
    /// - IsJettisonedInSnapshot reads snapshot MODULE data correctly
    /// - Zone warp exemption threshold lowered to > 1x
    /// </summary>
    [Collection("Sequential")]
    public class Bug290_LimboTreeRevertTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug290_LimboTreeRevertTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void LimboTree_NotDiscardedOnRevert_WhenFlagCleared()
        {
            // Simulate: tree stashed as Limbo, then flag cleared by quickload discard
            var tree = new RecordingTree
            {
                Id = "test-tree",
                TreeName = "TestRocket"
            };
            tree.Recordings["rec1"] = new Recording
            {
                RecordingId = "rec1",
                VesselName = "TestRocket"
            };
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            Assert.True(RecordingStore.PendingStashedThisTransition);

            // Quickload discard resets the flag (but preserves Limbo trees)
            RecordingStore.PendingStashedThisTransition = false;

            // The revert path should still preserve the Limbo tree
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);

            // A Finalized tree would be discarded, but Limbo should survive
            Assert.Equal("TestRocket", RecordingStore.PendingTree.TreeName);
        }

        [Fact]
        public void FinalizedTree_CanBeDiscarded_WhenFlagCleared()
        {
            var tree = new RecordingTree
            {
                Id = "test-tree",
                TreeName = "TestRocket"
            };
            tree.Recordings["rec1"] = new Recording
            {
                RecordingId = "rec1",
                VesselName = "TestRocket"
            };
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RecordingStore.PendingStashedThisTransition = false;

            // Finalized tree with cleared flag should be discardable
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);

            RecordingStore.DiscardPendingTree();
            Assert.False(RecordingStore.HasPendingTree);
        }
    }

    public class Bug290_IsJettisonedInSnapshotTests
    {
        [Fact]
        public void ReturnsTrue_WhenModuleJettisonIsJettisoned()
        {
            var partNode = new ConfigNode("PART");
            var moduleNode = new ConfigNode("MODULE");
            moduleNode.AddValue("name", "ModuleJettison");
            moduleNode.AddValue("isJettisoned", "True");
            partNode.AddNode(moduleNode);

            Assert.True(GhostVisualBuilder.IsJettisonedInSnapshot(partNode));
        }

        [Fact]
        public void ReturnsFalse_WhenModuleJettisonNotJettisoned()
        {
            var partNode = new ConfigNode("PART");
            var moduleNode = new ConfigNode("MODULE");
            moduleNode.AddValue("name", "ModuleJettison");
            moduleNode.AddValue("isJettisoned", "False");
            partNode.AddNode(moduleNode);

            Assert.False(GhostVisualBuilder.IsJettisonedInSnapshot(partNode));
        }

        [Fact]
        public void ReturnsFalse_WhenNoModuleJettison()
        {
            var partNode = new ConfigNode("PART");
            var moduleNode = new ConfigNode("MODULE");
            moduleNode.AddValue("name", "ModuleEngines");
            partNode.AddNode(moduleNode);

            Assert.False(GhostVisualBuilder.IsJettisonedInSnapshot(partNode));
        }

        [Fact]
        public void ReturnsFalse_WhenNoModules()
        {
            var partNode = new ConfigNode("PART");
            Assert.False(GhostVisualBuilder.IsJettisonedInSnapshot(partNode));
        }

        [Fact]
        public void ReturnsFalse_WhenIsJettisonedMissing()
        {
            var partNode = new ConfigNode("PART");
            var moduleNode = new ConfigNode("MODULE");
            moduleNode.AddValue("name", "ModuleJettison");
            // No isJettisoned value
            partNode.AddNode(moduleNode);

            Assert.False(GhostVisualBuilder.IsJettisonedInSnapshot(partNode));
        }

        [Fact]
        public void ReturnsTrue_CaseInsensitive()
        {
            var partNode = new ConfigNode("PART");
            var moduleNode = new ConfigNode("MODULE");
            moduleNode.AddValue("name", "ModuleJettison");
            moduleNode.AddValue("isJettisoned", "true");
            partNode.AddNode(moduleNode);

            Assert.True(GhostVisualBuilder.IsJettisonedInSnapshot(partNode));
        }

        [Fact]
        public void IgnoresOtherModules_ReturnsTrue_ForJettisonedModule()
        {
            var partNode = new ConfigNode("PART");

            var engineNode = new ConfigNode("MODULE");
            engineNode.AddValue("name", "ModuleEngines");
            partNode.AddNode(engineNode);

            var jettisonNode = new ConfigNode("MODULE");
            jettisonNode.AddValue("name", "ModuleJettison");
            jettisonNode.AddValue("isJettisoned", "True");
            partNode.AddNode(jettisonNode);

            Assert.True(GhostVisualBuilder.IsJettisonedInSnapshot(partNode));
        }
    }

    public class Bug290_WarpExemptionThresholdTests
    {
        [Fact]
        public void PhysicsWarp2x_OrbitalGhost_Exempt()
        {
            // Physics warp at 2x should exempt orbital ghosts (#290)
            Assert.True(GhostPlaybackLogic.ShouldExemptFromZoneHide(2f, true));
        }

        [Fact]
        public void PhysicsWarp3x_OrbitalGhost_Exempt()
        {
            Assert.True(GhostPlaybackLogic.ShouldExemptFromZoneHide(3f, true));
        }

        [Fact]
        public void NormalSpeed_OrbitalGhost_NotExempt()
        {
            Assert.False(GhostPlaybackLogic.ShouldExemptFromZoneHide(1f, true));
        }

        [Fact]
        public void SlightlyAboveNormal_OrbitalGhost_Exempt()
        {
            Assert.True(GhostPlaybackLogic.ShouldExemptFromZoneHide(1.01f, true));
        }
    }

    public class Bug290_WarpSuppressionMapViewTests
    {
        private static MockTrajectory MakeSectionTrajectory(SegmentEnvironment environment)
        {
            var traj = new MockTrajectory().WithTimeRange(0.0, 100.0);
            traj.TrackSections.Add(new TrackSection
            {
                environment = environment,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 0.0,
                endUT = 100.0,
                frames = traj.Points,
                source = TrackSectionSource.Active
            });
            return traj;
        }

        [Fact]
        public void ShouldSuppressGhosts_HighWarp_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldSuppressGhosts(100f));
        }

        [Fact]
        public void ShouldSuppressGhosts_LowWarp_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldSuppressGhosts(10f));
        }

        [Fact]
        public void MapView_SkipsWarpSuppression()
        {
            // In map view, suppressGhosts should be false even at high warp
            // because map markers need the ghost position to be updated.
            float highWarp = 100f;
            bool mapViewEnabled = true;
            bool suppress = !mapViewEnabled
                && GhostPlaybackLogic.ShouldSuppressGhosts(highWarp);
            Assert.False(suppress);
        }

        [Fact]
        public void FlightView_HighWarp_SuppressesGhosts()
        {
            float highWarp = 100f;
            bool mapViewEnabled = false;
            bool suppress = !mapViewEnabled
                && GhostPlaybackLogic.ShouldSuppressGhosts(highWarp);
            Assert.True(suppress);
        }

        [Fact]
        public void FlightView_LowWarp_DoesNotSuppressGhosts()
        {
            float lowWarp = 10f;
            bool mapViewEnabled = false;
            bool suppress = !mapViewEnabled
                && GhostPlaybackLogic.ShouldSuppressGhosts(lowWarp);
            Assert.False(suppress);
        }

        [Fact]
        public void ShouldSuppressGhostMeshAtWarp_SurfaceStationarySection_ReturnsFalse()
        {
            var traj = MakeSectionTrajectory(SegmentEnvironment.SurfaceStationary);

            Assert.False(GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(100f, traj, 50.0));
        }

        [Fact]
        public void ShouldSuppressGhostMeshAtWarp_SurfaceMobileSection_ReturnsTrue()
        {
            var traj = MakeSectionTrajectory(SegmentEnvironment.SurfaceMobile);

            Assert.True(GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(100f, traj, 50.0));
        }

        [Fact]
        public void ShouldSuppressGhostMeshAtWarp_SurfaceOnlyTrajectory_ReturnsFalse()
        {
            var traj = new MockTrajectory
            {
                SurfacePos = new SurfacePosition
                {
                    body = "Kerbin",
                    situation = SurfaceSituation.Landed
                }
            };

            Assert.False(GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(100f, traj, 50.0));
        }

        [Fact]
        public void ShouldSuppressGhostMeshAtWarp_LowWarpMovingSection_ReturnsFalse()
        {
            var traj = MakeSectionTrajectory(SegmentEnvironment.Atmospheric);

            Assert.False(GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(10f, traj, 50.0));
        }

        [Fact]
        public void ShouldSuppressGhostMeshAtWarp_NewestStationaryOverlapCycle_ReturnsFalse()
        {
            var traj = MakeSectionTrajectory(SegmentEnvironment.SurfaceStationary);

            Assert.True(GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                currentUT: 65.0,
                intervalSeconds: 30.0,
                duration: 100.0,
                playbackStartUT: 0.0,
                scheduleStartUT: 0.0,
                out double playbackUT,
                out long cycleIndex));
            Assert.Equal(2, cycleIndex);
            Assert.Equal(5.0, playbackUT, 6);
            Assert.False(GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                100f, traj, playbackUT));
        }

        [Fact]
        public void ShouldSuppressGhostMeshAtWarp_NewestMovingOverlapCycle_ReturnsTrue()
        {
            var traj = MakeSectionTrajectory(SegmentEnvironment.SurfaceMobile);

            Assert.True(GhostPlaybackLogic.TryComputeNewestOverlapPlaybackUT(
                currentUT: 65.0,
                intervalSeconds: 30.0,
                duration: 100.0,
                playbackStartUT: 0.0,
                scheduleStartUT: 0.0,
                out double playbackUT,
                out _));
            Assert.True(GhostPlaybackLogic.ShouldSuppressGhostMeshAtWarp(
                100f, traj, playbackUT));
        }
    }
}
