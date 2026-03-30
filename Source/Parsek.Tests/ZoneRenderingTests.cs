using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for zone-based rendering decisions — zone classification, transition detection,
    /// rendering policy, looped ghost spawn gating, and diagnostic logging.
    /// </summary>
    [Collection("Sequential")]
    public class ZoneRenderingTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ZoneRenderingTests()
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

        #region Zone Classification

        [Fact]
        public void ClassifyDistance_WithinPhysicsBubble_ReturnsPhysics()
        {
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(0));
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(1000));
            Assert.Equal(RenderingZone.Physics, RenderingZoneManager.ClassifyDistance(2299));
        }

        [Fact]
        public void ClassifyDistance_AtPhysicsBoundary_ReturnsVisual()
        {
            // Exactly at 2300m boundary transitions to Visual
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(2300));
        }

        [Fact]
        public void ClassifyDistance_InVisualRange_ReturnsVisual()
        {
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(5000));
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(50000));
            Assert.Equal(RenderingZone.Visual, RenderingZoneManager.ClassifyDistance(119999));
        }

        [Fact]
        public void ClassifyDistance_AtVisualBoundary_ReturnsBeyond()
        {
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(RenderingZoneManager.VisualRangeRadius));
        }

        [Fact]
        public void ClassifyDistance_FarBeyond_ReturnsBeyond()
        {
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(RenderingZoneManager.VisualRangeRadius + 100000));
            Assert.Equal(RenderingZone.Beyond, RenderingZoneManager.ClassifyDistance(double.MaxValue));
        }

        #endregion

        #region Zone Transition Detection

        [Fact]
        public void DetectZoneTransition_SameZone_ReturnsFalse()
        {
            string desc;
            Assert.False(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Physics, RenderingZone.Physics, out desc));
            Assert.Null(desc);
        }

        [Fact]
        public void DetectZoneTransition_PhysicsToVisual_ReturnsOutward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Physics, RenderingZone.Visual, out desc));
            Assert.Equal("outward", desc);
        }

        [Fact]
        public void DetectZoneTransition_VisualToBeyond_ReturnsOutward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Visual, RenderingZone.Beyond, out desc));
            Assert.Equal("outward", desc);
        }

        [Fact]
        public void DetectZoneTransition_PhysicsToBeyond_ReturnsOutward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Physics, RenderingZone.Beyond, out desc));
            Assert.Equal("outward", desc);
        }

        [Fact]
        public void DetectZoneTransition_BeyondToVisual_ReturnsInward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Beyond, RenderingZone.Visual, out desc));
            Assert.Equal("inward", desc);
        }

        [Fact]
        public void DetectZoneTransition_VisualToPhysics_ReturnsInward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Visual, RenderingZone.Physics, out desc));
            Assert.Equal("inward", desc);
        }

        [Fact]
        public void DetectZoneTransition_BeyondToPhysics_ReturnsInward()
        {
            string desc;
            Assert.True(GhostPlaybackLogic.DetectZoneTransition(
                RenderingZone.Beyond, RenderingZone.Physics, out desc));
            Assert.Equal("inward", desc);
        }

        #endregion

        #region Zone Rendering Policy

        [Fact]
        public void GetZoneRenderingPolicy_Physics_FullRendering()
        {
            var (shouldHide, skipPartEvents, skipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(RenderingZone.Physics);
            Assert.False(shouldHide);
            Assert.False(skipPartEvents);
            Assert.False(skipPositioning);
        }

        [Fact]
        public void GetZoneRenderingPolicy_Visual_MeshAndPartEvents()
        {
            var (shouldHide, skipPartEvents, skipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(RenderingZone.Visual);
            Assert.False(shouldHide);
            Assert.False(skipPartEvents); // part events apply in Visual zone for staging/jettison/destruction
            Assert.False(skipPositioning);
        }

        [Fact]
        public void GetZoneRenderingPolicy_Beyond_EverythingSkipped()
        {
            var (shouldHide, skipPartEvents, skipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(RenderingZone.Beyond);
            Assert.True(shouldHide);
            Assert.True(skipPartEvents);
            Assert.True(skipPositioning);
        }

        #endregion

        #region Should Hide Ghost / Exit Watch Mode

        [Fact]
        public void ShouldHideGhostForZone_ActiveAndBeyond_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldHideGhostForZone(true, RenderingZone.Beyond));
        }

        [Fact]
        public void ShouldHideGhostForZone_ActiveAndVisual_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldHideGhostForZone(true, RenderingZone.Visual));
        }

        [Fact]
        public void ShouldHideGhostForZone_ActiveAndPhysics_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldHideGhostForZone(true, RenderingZone.Physics));
        }

        [Fact]
        public void ShouldHideGhostForZone_InactiveAndBeyond_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldHideGhostForZone(false, RenderingZone.Beyond));
        }

        [Fact]
        public void ShouldExitWatchModeForZone_WatchingAndBeyond_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldExitWatchModeForZone(
                watchedRecordingIndex: 5, currentRecordingIndex: 5, zone: RenderingZone.Beyond));
        }

        [Fact]
        public void ShouldExitWatchModeForZone_WatchingDifferentGhost_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldExitWatchModeForZone(
                watchedRecordingIndex: 3, currentRecordingIndex: 5, zone: RenderingZone.Beyond));
        }

        [Fact]
        public void ShouldExitWatchModeForZone_NotWatching_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldExitWatchModeForZone(
                watchedRecordingIndex: -1, currentRecordingIndex: 5, zone: RenderingZone.Beyond));
        }

        [Fact]
        public void ShouldExitWatchModeForZone_WatchingButNotBeyond_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldExitWatchModeForZone(
                watchedRecordingIndex: 5, currentRecordingIndex: 5, zone: RenderingZone.Visual));
            Assert.False(GhostPlaybackLogic.ShouldExitWatchModeForZone(
                watchedRecordingIndex: 5, currentRecordingIndex: 5, zone: RenderingZone.Physics));
        }

        #endregion

        #region Part Event Gating

        [Fact]
        public void ShouldApplyPartEventsForZone_Physics_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldApplyPartEventsForZone(RenderingZone.Physics));
        }

        [Fact]
        public void ShouldApplyPartEventsForZone_Visual_ReturnsTrue()
        {
            Assert.True(GhostPlaybackLogic.ShouldApplyPartEventsForZone(RenderingZone.Visual));
        }

        [Fact]
        public void ShouldApplyPartEventsForZone_Beyond_ReturnsFalse()
        {
            Assert.False(GhostPlaybackLogic.ShouldApplyPartEventsForZone(RenderingZone.Beyond));
        }

        #endregion

        #region Looped Ghost Spawn Gating

        [Fact]
        public void EvaluateLoopedGhostSpawn_WithinPhysicsBubble_FullFidelity()
        {
            var (shouldSpawn, simplified) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(1000);
            Assert.True(shouldSpawn);
            Assert.False(simplified);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_BetweenPhysicsAndSimplified_Simplified()
        {
            var (shouldSpawn, simplified) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(10000);
            Assert.True(shouldSpawn);
            Assert.True(simplified);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_AtSimplifiedBoundary_NotSpawned()
        {
            // At exactly 50km: beyond spawn threshold
            var (shouldSpawn, _) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(50000);
            Assert.False(shouldSpawn);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_FarAway_NotSpawned()
        {
            var (shouldSpawn, _) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(200000);
            Assert.False(shouldSpawn);
        }

        [Fact]
        public void EvaluateLoopedGhostSpawn_AtPhysicsBoundary_Simplified()
        {
            // Exactly at 2300m: transitions to simplified
            var (shouldSpawn, simplified) = GhostPlaybackLogic.EvaluateLoopedGhostSpawn(2300);
            Assert.True(shouldSpawn);
            Assert.True(simplified);
        }

        #endregion

        #region Mesh Rendering

        [Fact]
        public void ShouldRenderMesh_InPhysicsZone_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderMesh(1000));
        }

        [Fact]
        public void ShouldRenderMesh_InVisualZone_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderMesh(50000));
        }

        [Fact]
        public void ShouldRenderMesh_InBeyondZone_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderMesh(RenderingZoneManager.VisualRangeRadius));
            Assert.False(RenderingZoneManager.ShouldRenderMesh(RenderingZoneManager.VisualRangeRadius + 100000));
        }

        #endregion

        #region Part Event Rendering

        [Fact]
        public void ShouldRenderPartEvents_InPhysicsZone_ReturnsTrue()
        {
            Assert.True(RenderingZoneManager.ShouldRenderPartEvents(1000));
        }

        [Fact]
        public void ShouldRenderPartEvents_InVisualZone_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(5000));
        }

        [Fact]
        public void ShouldRenderPartEvents_InBeyondZone_ReturnsFalse()
        {
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(200000));
        }

        #endregion

        #region Zone Transition Logging

        [Fact]
        public void LogZoneTransition_EmitsLogWithZoneTag()
        {
            RenderingZoneManager.LogZoneTransition(
                "#0 \"TestVessel\"", RenderingZone.Physics, RenderingZone.Visual, 5000);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("Physics->Visual") &&
                l.Contains("TestVessel") &&
                l.Contains("5000m"));
        }

        [Fact]
        public void LogZoneTransition_BeyondTransition_EmitsLog()
        {
            RenderingZoneManager.LogZoneTransition(
                "#3 \"Orbiter\"", RenderingZone.Visual, RenderingZone.Beyond, 150000);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("Visual->Beyond") &&
                l.Contains("Orbiter") &&
                l.Contains("150000m"));
        }

        [Fact]
        public void LogZoneTransition_InwardTransition_EmitsLog()
        {
            RenderingZoneManager.LogZoneTransition(
                "#1 \"Lander\"", RenderingZone.Beyond, RenderingZone.Visual, 100000);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("Beyond->Visual") &&
                l.Contains("Lander"));
        }

        #endregion

        #region Looped Ghost Spawn Decision Logging

        [Fact]
        public void LogLoopedGhostSpawnDecision_Suppressed_EmitsLog()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision(
                "#2 \"Shuttle\"", 60000, false, false);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("suppressed") &&
                l.Contains("Shuttle") &&
                l.Contains("60000m"));
        }

        [Fact]
        public void LogLoopedGhostSpawnDecision_SimplifiedSpawn_EmitsLog()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision(
                "#4 \"Rover\"", 10000, true, true);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("simplified") &&
                l.Contains("Rover") &&
                l.Contains("no part events"));
        }

        [Fact]
        public void LogLoopedGhostSpawnDecision_FullFidelity_EmitsLog()
        {
            RenderingZoneManager.LogLoopedGhostSpawnDecision(
                "#0 \"Rocket\"", 500, true, false);

            Assert.Contains(logLines, l =>
                l.Contains("[Zone]") &&
                l.Contains("full fidelity") &&
                l.Contains("Rocket"));
        }

        #endregion

        #region GhostPlaybackState Zone Default

        [Fact]
        public void GhostPlaybackState_DefaultZone_IsPhysics()
        {
            var state = new GhostPlaybackState();
            Assert.Equal(RenderingZone.Physics, state.currentZone);
        }

        #endregion

        #region Zone Boundary Constants

        [Fact]
        public void ZoneBoundaries_CorrectValues()
        {
            Assert.Equal(2300.0, RenderingZoneManager.PhysicsBubbleRadius);
            Assert.Equal(500000.0, RenderingZoneManager.VisualRangeRadius);
            Assert.Equal(2300.0, RenderingZoneManager.LoopFullFidelityRadius);
            Assert.Equal(50000.0, RenderingZoneManager.LoopSimplifiedRadius);
        }

        #endregion

        #region Integration: Zone + Loop Spawn Cross-Check

        [Fact]
        public void ZoneAndLoopSpawn_ConsistentAtBoundaries()
        {
            // At physics boundary (2300m):
            // Zone: Visual, Loop: simplified
            var zone = RenderingZoneManager.ClassifyDistance(2300);
            var (spawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(2300);
            Assert.Equal(RenderingZone.Visual, zone);
            Assert.True(spawn);
            Assert.True(simplified);

            // Both agree: no part events at 2300m
            Assert.False(RenderingZoneManager.ShouldRenderPartEvents(2300));
        }

        [Fact]
        public void ZoneAndLoopSpawn_BeyondVisualRange_BothSuppress()
        {
            // At visual range boundary: Beyond zone, loop spawn suppressed (>50km)
            double vr = RenderingZoneManager.VisualRangeRadius;
            var zone = RenderingZoneManager.ClassifyDistance(vr);
            var (spawn, _) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(vr);
            Assert.Equal(RenderingZone.Beyond, zone);
            Assert.False(spawn);
            Assert.False(RenderingZoneManager.ShouldRenderMesh(vr));
        }

        [Fact]
        public void ZoneAndLoopSpawn_InPhysicsBubble_BothFull()
        {
            // At 1000m: Physics zone, loop full fidelity
            var zone = RenderingZoneManager.ClassifyDistance(1000);
            var (spawn, simplified) = RenderingZoneManager.ShouldSpawnLoopedGhostAtDistance(1000);
            Assert.Equal(RenderingZone.Physics, zone);
            Assert.True(spawn);
            Assert.False(simplified);
            Assert.True(RenderingZoneManager.ShouldRenderPartEvents(1000));
            Assert.True(RenderingZoneManager.ShouldRenderMesh(1000));
        }

        #endregion
    }
}
