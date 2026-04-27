using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    public class RenderingZoneClassifyDistanceTests
    {
        #region ClassifyDistance

        [Fact]
        public void ClassifyDistance_Zero_ReturnsPhysics()
        {
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(0));
        }

        [Fact]
        public void ClassifyDistance_JustInsidePhysicsBubble_ReturnsPhysics()
        {
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(2299));
        }

        [Fact]
        public void ClassifyDistance_AtPhysicsBoundary_ReturnsVisual()
        {
            // 2300m is the boundary — no longer < 2300, so it falls to Visual
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(2300));
        }

        [Fact]
        public void ClassifyDistance_MidVisualRange_ReturnsVisual()
        {
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(50000));
        }

        [Fact]
        public void ClassifyDistance_JustInsideVisualRange_ReturnsVisual()
        {
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(119999));
        }

        [Fact]
        public void ClassifyDistance_AtVisualBoundary_ReturnsBeyond()
        {
            // At boundary — no longer < VisualRangeRadius, so it falls to Beyond
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(RenderingZoneManager.VisualRangeRadius));
        }

        [Fact]
        public void ClassifyDistance_FarBeyond_ReturnsBeyond()
        {
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(RenderingZoneManager.VisualRangeRadius + 100000));
        }

        #endregion
    }

    public class ShouldSpawnLoopedGhostTests
    {
        #region ShouldSpawnLoopedGhostAtDistance

        [Fact]
        public void ShouldSpawnLoopedGhost_WellInsidePhysics_FullFidelity()
        {
            var (shouldSpawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(1000);
            Assert.True(shouldSpawn);
            Assert.False(simplified);
        }

        [Fact]
        public void ShouldSpawnLoopedGhost_JustInsidePhysics_FullFidelity()
        {
            var (shouldSpawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(2299);
            Assert.True(shouldSpawn);
            Assert.False(simplified);
        }

        [Fact]
        public void ShouldSpawnLoopedGhost_AtPhysicsBoundary_Simplified()
        {
            // 2300m is the boundary — no longer < 2300, so it falls to simplified
            var (shouldSpawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(2300);
            Assert.True(shouldSpawn);
            Assert.True(simplified);
        }

        [Fact]
        public void ShouldSpawnLoopedGhost_JustInsideSimplifiedRange_Simplified()
        {
            var (shouldSpawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(49999);
            Assert.True(shouldSpawn);
            Assert.True(simplified);
        }

        [Fact]
        public void ShouldSpawnLoopedGhost_AtSimplifiedBoundary_NotSpawned()
        {
            // 50000m is the boundary — no longer < 50000, so too far to spawn
            var (shouldSpawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(50000);
            Assert.False(shouldSpawn);
            Assert.False(simplified);
        }

        [Fact]
        public void ShouldSpawnLoopedGhost_WayBeyond_NotSpawned()
        {
            var (shouldSpawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(120000);
            Assert.False(shouldSpawn);
            Assert.False(simplified);
        }

        #endregion
    }

    public class ShouldRenderPartEventsTests
    {
        #region ShouldRenderPartEvents

        [Fact]
        public void ShouldRenderPartEvents_InsidePhysicsBubble_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderPartEvents(0));
            Assert.True(RenderingZoneManager.ShouldRenderPartEvents(1000));
            Assert.True(RenderingZoneManager.ShouldRenderPartEvents(2299));
        }

        [Fact]
        public void ShouldRenderPartEvents_AtPhysicsBoundary_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(2300));
        }

        [Fact]
        public void ShouldRenderPartEvents_BeyondPhysicsBubble_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(50000));
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(120000));
        }

        #endregion
    }

    public class ShouldRenderMeshTests
    {
        #region ShouldRenderMesh

        [Fact]
        public void ShouldRenderMesh_InsideVisualRange_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderMesh(0));
            Assert.True(RenderingZoneManager.ShouldRenderMesh(2300));
            Assert.True(RenderingZoneManager.ShouldRenderMesh(50000));
            Assert.True(RenderingZoneManager.ShouldRenderMesh(119999));
        }

        [Fact]
        public void ShouldRenderMesh_AtVisualBoundary_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderMesh(RenderingZoneManager.VisualRangeRadius));
        }

        [Fact]
        public void ShouldRenderMesh_BeyondVisualRange_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderMesh(RenderingZoneManager.VisualRangeRadius + 100000));
        }

        #endregion
    }

    public class RenderingZoneConstantsTests
    {
        #region Constants Consistency

        [Fact]
        public void Constants_PhysicsBubbleLessThanVisualRange()
        {
            Assert.True(RenderingZoneManager.PhysicsBubbleRadius < RenderingZoneManager.VisualRangeRadius);
        }

        [Fact]
        public void Constants_LoopFullFidelityNotExceedPhysicsBubble()
        {
            Assert.True(RenderingZoneManager.LoopFullFidelityRadius <= RenderingZoneManager.PhysicsBubbleRadius);
        }

        [Fact]
        public void Constants_LoopSimplifiedLessThanVisualRange()
        {
            Assert.True(RenderingZoneManager.LoopSimplifiedRadius < RenderingZoneManager.VisualRangeRadius);
        }

        #endregion
    }

    [Collection("Sequential")]
    public class RenderingZoneLoggingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RenderingZoneLoggingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        #region LogZoneTransition

        [Fact]
        public void LogZoneTransition_PhysicsToVisual_LogsTransition()
        {
            RenderingZoneManager.LogZoneTransition("ghost-1", RenderingZone.Physics, RenderingZone.Visual, 2500.0);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[Zone]", line);
            Assert.Contains("Zone transition", line);
            Assert.Contains("ghost=ghost-1", line);
            Assert.Contains("Physics->Visual", line);
            Assert.Contains("dist=2500m", line);
        }

        [Fact]
        public void LogZoneTransition_VisualToBeyond_LogsTransition()
        {
            RenderingZoneManager.LogZoneTransition("ghost-2", RenderingZone.Visual, RenderingZone.Beyond, 120500.0);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[Zone]", line);
            Assert.Contains("Visual->Beyond", line);
            Assert.Contains("ghost=ghost-2", line);
        }

        [Fact]
        public void LogZoneTransition_BeyondToVisual_LogsTransition()
        {
            RenderingZoneManager.LogZoneTransition("ghost-3", RenderingZone.Beyond, RenderingZone.Visual, 119000.0);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("Beyond->Visual", line);
        }

        [Fact]
        public void LogZoneTransition_UnresolvedDistance_LogsUnresolved()
        {
            RenderingZoneManager.LogZoneTransition(
                "ghost-relative",
                RenderingZone.Physics,
                RenderingZone.Beyond,
                double.MaxValue);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("dist=unresolved", line);
            Assert.DoesNotContain("179769313486232", line);
        }

        #endregion

        #region LogLoopedGhostSpawnDecision

        [Fact]
        public void LogLoopedGhostSpawn_FullFidelity_LogsCorrectly()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision("loop-1", 500.0, true, false);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[Zone]", line);
            Assert.Contains("full fidelity", line);
            Assert.Contains("ghost=loop-1", line);
            Assert.Contains("dist=500m", line);
        }

        [Fact]
        public void LogLoopedGhostSpawn_Simplified_LogsCorrectly()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision("loop-2", 10000.0, true, true);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[Zone]", line);
            Assert.Contains("simplified", line);
            Assert.Contains("no part events", line);
            Assert.Contains("ghost=loop-2", line);
        }

        [Fact]
        public void LogLoopedGhostSpawn_Suppressed_LogsCorrectly()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision("loop-3", 60000.0, false, false);

            Assert.Single(logLines);
            string line = logLines[0];
            Assert.Contains("[Zone]", line);
            Assert.Contains("suppressed", line);
            Assert.Contains("ghost=loop-3", line);
            Assert.Contains("50000m threshold", line);
        }

        #endregion
    }
}
