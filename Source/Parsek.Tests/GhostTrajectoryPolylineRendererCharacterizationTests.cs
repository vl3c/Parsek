using System.Collections.Generic;
using Parsek.Display;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for the pure builder helpers of
    /// <see cref="GhostTrajectoryPolylineRenderer"/> that no test called directly: the
    /// per-section source-list policy, the section and orbital-interval membership checks and
    /// the per-leg point filter.
    /// </summary>
    public class GhostTrajectoryPolylineRendererCharacterizationTests
    {
        private static TrajectoryPoint Pt(double ut) => new TrajectoryPoint { ut = ut, bodyName = "Kerbin" };

        // ---------- ResolveSourceListForSection ---------------------------------

        [Fact]
        public void ResolveSourceListForSection_PicksSurfaceByReferenceFrame()
        {
            var frames = new List<TrajectoryPoint> { Pt(1) };
            var bodyFixed = new List<TrajectoryPoint> { Pt(2) };

            Assert.Same(frames, GhostTrajectoryPolylineRenderer.ResolveSourceListForSection(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute, frames = frames, bodyFixedFrames = bodyFixed
            }));
            Assert.Same(bodyFixed, GhostTrajectoryPolylineRenderer.ResolveSourceListForSection(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative, frames = frames, bodyFixedFrames = bodyFixed
            }));
            Assert.Null(GhostTrajectoryPolylineRenderer.ResolveSourceListForSection(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative, frames = frames, bodyFixedFrames = null
            }));
            Assert.Null(GhostTrajectoryPolylineRenderer.ResolveSourceListForSection(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint, frames = frames, bodyFixedFrames = bodyFixed
            }));
        }

        // ---------- IsInsideAnySection -------------------------------------------

        [Fact]
        public void IsInsideAnySection_InclusiveOnBothEnds()
        {
            var sections = new List<TrackSection>
            {
                new TrackSection { startUT = 10.0, endUT = 20.0 },
                new TrackSection { startUT = 30.0, endUT = 40.0 },
            };
            Assert.True(GhostTrajectoryPolylineRenderer.IsInsideAnySection(10.0, sections));
            Assert.True(GhostTrajectoryPolylineRenderer.IsInsideAnySection(20.0, sections));
            Assert.True(GhostTrajectoryPolylineRenderer.IsInsideAnySection(35.0, sections));
            Assert.False(GhostTrajectoryPolylineRenderer.IsInsideAnySection(25.0, sections));
            Assert.False(GhostTrajectoryPolylineRenderer.IsInsideAnySection(9.999, sections));
            Assert.False(GhostTrajectoryPolylineRenderer.IsInsideAnySection(10.0, null));
            Assert.False(GhostTrajectoryPolylineRenderer.IsInsideAnySection(10.0, new List<TrackSection>()));
        }

        // ---------- FilterPointsForLeg -------------------------------------------

        [Fact]
        public void FilterPointsForLeg_KeepsInRangePointsOutsideOrbitalIntervals()
        {
            var source = new List<TrajectoryPoint> { Pt(5), Pt(10), Pt(15), Pt(20), Pt(25), Pt(30), Pt(35) };
            var orbital = new List<(double startUT, double endUT)> { (15.0, 20.0) };

            var result = GhostTrajectoryPolylineRenderer.FilterPointsForLeg(source, 10.0, 30.0, orbital);

            // Section bounds and orbital interval bounds are both inclusive.
            Assert.Equal(new[] { 10.0, 25.0, 30.0 }, result.ConvertAll(p => p.ut));
            Assert.NotSame(source, result);
        }

        [Fact]
        public void FilterPointsForLeg_NullIntervalsAndEmptySource()
        {
            var source = new List<TrajectoryPoint> { Pt(1), Pt(2), Pt(3) };
            Assert.Equal(new[] { 2.0, 3.0 },
                GhostTrajectoryPolylineRenderer.FilterPointsForLeg(source, 2.0, 3.0, null).ConvertAll(p => p.ut));
            Assert.Empty(GhostTrajectoryPolylineRenderer.FilterPointsForLeg(
                new List<TrajectoryPoint>(), 0.0, 10.0, null));
        }

        // ---------- IsInsideAnyOrbitalInterval -----------------------------------

        [Fact]
        public void IsInsideAnyOrbitalInterval_InclusiveBounds()
        {
            var intervals = new List<(double startUT, double endUT)> { (0.0, 1.0), (5.0, 6.0) };
            Assert.True(GhostTrajectoryPolylineRenderer.IsInsideAnyOrbitalInterval(0.0, intervals));
            Assert.True(GhostTrajectoryPolylineRenderer.IsInsideAnyOrbitalInterval(6.0, intervals));
            Assert.False(GhostTrajectoryPolylineRenderer.IsInsideAnyOrbitalInterval(3.0, intervals));
            Assert.False(GhostTrajectoryPolylineRenderer.IsInsideAnyOrbitalInterval(0.5, null));
        }
    }
}
