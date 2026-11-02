using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for two pure static helpers of <see cref="GhostPlaybackEngine"/>
    /// that no test called directly: the checkpoint-backed orbit endpoint clamp and the legacy
    /// loop-gap to loop-period conversion.
    /// </summary>
    public class GhostPlaybackEngineCharacterizationTests
    {
        private static OrbitSegment Seg(double start, double end) =>
            new OrbitSegment { startUT = start, endUT = end, bodyName = "Kerbin" };

        private static TrackSection Section(double start, double end) =>
            new TrackSection { startUT = start, endUT = end, referenceFrame = ReferenceFrame.OrbitalCheckpoint };

        // ---------- TryResolveCheckpointBackedOrbitEndpointUT --------------------

        [Fact]
        public void TryResolveCheckpointBackedOrbitEndpointUT_NoSegments_False()
        {
            Assert.False(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                null, Section(0, 100), 50.0, out double ut));
            Assert.Equal(0.0, ut);
            Assert.False(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                new List<OrbitSegment>(), Section(0, 100), 50.0, out _));
        }

        [Fact]
        public void TryResolveCheckpointBackedOrbitEndpointUT_DesiredInsideASegment_Unchanged()
        {
            var segments = new List<OrbitSegment> { Seg(0, 100), Seg(200, 300) };
            Assert.True(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                segments, Section(0, 300), 250.0, out double ut));
            Assert.Equal(250.0, ut);
        }

        [Fact]
        public void TryResolveCheckpointBackedOrbitEndpointUT_InGap_ClampsToNearestSegmentEdge()
        {
            var segments = new List<OrbitSegment> { Seg(0, 100), Seg(200, 300) };
            Assert.True(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                segments, Section(0, 300), 140.0, out double nearEarlier));
            Assert.Equal(100.0, nearEarlier);
            Assert.True(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                segments, Section(0, 300), 170.0, out double nearLater));
            Assert.Equal(200.0, nearLater);
        }

        [Fact]
        public void TryResolveCheckpointBackedOrbitEndpointUT_EquidistantTie_PrefersLaterUT()
        {
            var segments = new List<OrbitSegment> { Seg(0, 100), Seg(200, 300) };
            Assert.True(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                segments, Section(0, 300), 150.0, out double ut));
            Assert.Equal(200.0, ut);
        }

        [Fact]
        public void TryResolveCheckpointBackedOrbitEndpointUT_PastEnd_ClampsToLastEnd()
        {
            var segments = new List<OrbitSegment> { Seg(0, 100), Seg(200, 300) };
            Assert.True(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                segments, Section(0, 400), 400.0, out double ut));
            Assert.Equal(300.0, ut);
        }

        [Fact]
        public void TryResolveCheckpointBackedOrbitEndpointUT_OnlySegmentsOverlappingTheSection()
        {
            var segments = new List<OrbitSegment> { Seg(0, 100), Seg(200, 300) };
            // The section only overlaps the first segment, so the later one is not a candidate.
            Assert.True(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                segments, Section(0, 150), 170.0, out double ut));
            Assert.Equal(100.0, ut);
            // No segment overlaps the section at all.
            Assert.False(GhostPlaybackEngine.TryResolveCheckpointBackedOrbitEndpointUT(
                segments, Section(500, 600), 550.0, out _));
        }

        // ---------- TryConvertLegacyGapToLoopPeriodSeconds -----------------------

        private static Recording Span(double start, double end)
        {
            var rec = new Recording { RecordingId = "loop" };
            rec.Points.Add(new TrajectoryPoint { ut = start, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = end, bodyName = "Kerbin" });
            return rec;
        }

        [Fact]
        public void TryConvertLegacyGapToLoopPeriodSeconds_PeriodIsDurationPlusGap()
        {
            Assert.True(GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                Span(1000.0, 1100.0), 30.0, out double period, out double duration));
            Assert.Equal(100.0, duration);
            Assert.Equal(130.0, period);
        }

        [Fact]
        public void TryConvertLegacyGapToLoopPeriodSeconds_UnderflowClampsToMinCycle()
        {
            Assert.True(GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                Span(0.0, 100.0), -98.0, out double period, out _));
            Assert.Equal(LoopTiming.MinCycleDuration, period);
            // Exactly the minimum is kept as is.
            Assert.True(GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                Span(0.0, 100.0), LoopTiming.MinCycleDuration - 100.0, out period, out _));
            Assert.Equal(LoopTiming.MinCycleDuration, period);
        }

        [Fact]
        public void TryConvertLegacyGapToLoopPeriodSeconds_NonFiniteGap_FalseAndGapEchoed()
        {
            Assert.False(GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                Span(0.0, 100.0), double.NaN, out double period, out double duration));
            Assert.True(double.IsNaN(period));
            Assert.Equal(100.0, duration);
            Assert.False(GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                Span(0.0, 100.0), double.PositiveInfinity, out _, out _));
        }

        [Fact]
        public void TryConvertLegacyGapToLoopPeriodSeconds_NoDuration_False()
        {
            Assert.False(GhostPlaybackEngine.TryConvertLegacyGapToLoopPeriodSeconds(
                new Recording { RecordingId = "empty" }, 10.0, out double period, out double duration));
            Assert.Equal(10.0, period);
            Assert.Equal(0.0, duration);
        }
    }
}
