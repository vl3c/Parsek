using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The segment pick of the orbital-checkpoint anchor pose
    /// (<see cref="TrajectoryMath.FindFirstOrbitSegmentIndexClosed"/>): closed at both ends,
    /// first covering segment wins, NaN reads as covered. Headless this is the only part of
    /// the pose whose choice is observable (every body lookup misses), so its boundaries are
    /// pinned here rather than through the delegate.
    /// </summary>
    public class FindFirstOrbitSegmentIndexClosedTests
    {
        private static List<OrbitSegment> Segments(params (double start, double end)[] ranges)
        {
            var list = new List<OrbitSegment>();
            foreach (var r in ranges)
                list.Add(new OrbitSegment { startUT = r.start, endUT = r.end, bodyName = "Kerbin" });
            return list;
        }

        [Fact]
        public void NullOrEmpty_IsMinusOne()
        {
            Assert.Equal(-1, TrajectoryMath.FindFirstOrbitSegmentIndexClosed(null, 15.0));
            Assert.Equal(-1, TrajectoryMath.FindFirstOrbitSegmentIndexClosed(new List<OrbitSegment>(), 15.0));
        }

        [Theory]
        [InlineData(9.999, -1)]
        [InlineData(10.0, 0)]
        [InlineData(15.0, 0)]
        [InlineData(20.0, 0)]
        [InlineData(20.001, -1)]
        [InlineData(25.0, -1)]
        [InlineData(30.0, 1)]
        [InlineData(40.0, 1)]
        [InlineData(40.001, -1)]
        public void BothEndsClosed_GapsAndOutsideMiss(double ut, int expected)
        {
            Assert.Equal(expected, TrajectoryMath.FindFirstOrbitSegmentIndexClosed(
                Segments((10.0, 20.0), (30.0, 40.0)), ut));
        }

        [Fact]
        public void SharedBoundary_TheFirstSegmentWins()
        {
            // FindOrbitSegmentIndex is half-open and would pick the second segment here.
            List<OrbitSegment> segments = Segments((10.0, 20.0), (20.0, 30.0));
            Assert.Equal(0, TrajectoryMath.FindFirstOrbitSegmentIndexClosed(segments, 20.0));
            Assert.Equal(1, TrajectoryMath.FindOrbitSegmentIndex(segments, 20.0));
        }

        [Fact]
        public void Overlap_TheFirstCoveringSegmentWins()
        {
            Assert.Equal(1, TrajectoryMath.FindFirstOrbitSegmentIndexClosed(
                Segments((0.0, 5.0), (10.0, 20.0), (12.0, 18.0)), 15.0));
        }

        [Fact]
        public void NaN_ReadsAsCoveredByTheFirstSegment()
        {
            Assert.Equal(0, TrajectoryMath.FindFirstOrbitSegmentIndexClosed(
                Segments((10.0, 20.0), (30.0, 40.0)), double.NaN));
        }
    }
}
