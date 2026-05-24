using Xunit;

namespace Parsek.Tests
{
    public class DistanceThresholdsTests
    {
        [Fact]
        public void PhysicsBubbleThreshold_IsSharedAcrossCoreGhostSystems()
        {
            // The 2.3 km physics bubble is shared by relative-frame anchoring and
            // background sampling. The rendering full-fidelity radius is deliberately
            // NOT tied to it (see FullFidelityDecoupledFromPhysicsBubble below).
            Assert.Equal(DistanceThresholds.RelativeFrame.EntryMeters, AnchorDetector.RelativeEntryDistance);
            Assert.Equal(DistanceThresholds.BackgroundSampling.MaxDistanceMeters, ProximityRateSelector.PhysicsBubble);
        }

        [Fact]
        public void FullFidelityDecoupledFromPhysicsBubble()
        {
            // Engine plumes / smoke read well from several km away, so the rendering
            // full-fidelity range is larger than KSP's physics-load bubble.
            Assert.Equal(DistanceThresholds.GhostFlight.FullFidelityRangeMeters, RenderingZoneManager.FullFidelityRadius);
            Assert.True(RenderingZoneManager.FullFidelityRadius > DistanceThresholds.PhysicsBubbleMeters);
            Assert.True(DistanceThresholds.GhostFlight.FullFidelityRestoreMeters < DistanceThresholds.GhostFlight.FullFidelityRangeMeters);
        }

        [Fact]
        public void RenderingThresholds_AreDerivedFromCentralDefinitions()
        {
            Assert.Equal(DistanceThresholds.GhostVisualRangeMeters, RenderingZoneManager.VisualRangeRadius);
            Assert.Equal(DistanceThresholds.GhostFlight.LoopFullFidelityMeters, RenderingZoneManager.LoopFullFidelityRadius);
            Assert.Equal(DistanceThresholds.GhostFlight.LoopSimplifiedMeters, RenderingZoneManager.LoopSimplifiedRadius);
        }

        [Fact]
        public void RelativeFrameThresholds_PreserveExpectedOrdering()
        {
            Assert.Equal(DistanceThresholds.PhysicsBubbleMeters, DistanceThresholds.RelativeFrame.EntryMeters);
            Assert.True(DistanceThresholds.RelativeFrame.ExitMeters > DistanceThresholds.RelativeFrame.EntryMeters);
            Assert.True(DistanceThresholds.RelativeFrame.DockingApproachMeters < DistanceThresholds.RelativeFrame.EntryMeters);
        }

        [Fact]
        public void WatchCutoffDefault_IsFixedAtThreeHundredKilometers()
        {
            Assert.Equal(
                DistanceThresholds.GhostFlight.DefaultWatchCameraCutoffKm,
                DistanceThresholds.GhostFlight.GetWatchCameraCutoffKm());
            Assert.Equal(
                DistanceThresholds.GhostFlight.DefaultWatchCameraCutoffKm * 1000.0,
                DistanceThresholds.GhostFlight.GetWatchCameraCutoffMeters());
        }

        [Fact]
        public void ParsekSettings_NoLongerExposesMutableWatchCutoffField()
        {
            Assert.Null(typeof(ParsekSettings).GetField("ghostCameraCutoffKm"));
        }

        [Fact]
        public void AudioAndKscThresholds_RemainPositiveAndOrdered()
        {
            Assert.True(DistanceThresholds.GhostAudio.RolloffMinDistanceMeters > 0f);
            Assert.True(DistanceThresholds.GhostAudio.RolloffMaxDistanceMeters > DistanceThresholds.GhostAudio.RolloffMinDistanceMeters);
            Assert.True(DistanceThresholds.KscGhosts.CullDistanceMeters > DistanceThresholds.GhostAudio.RolloffMaxDistanceMeters);
            Assert.Equal(
                DistanceThresholds.KscGhosts.CullDistanceMeters * DistanceThresholds.KscGhosts.CullDistanceMeters,
                DistanceThresholds.KscGhosts.CullDistanceSq);
        }
    }
}
