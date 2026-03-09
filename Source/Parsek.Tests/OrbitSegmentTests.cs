using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class OrbitSegmentTests
    {
        public OrbitSegmentTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private OrbitSegment MakeSegment(double startUT, double endUT, string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                inclination = 28.5,
                eccentricity = 0.001,
                semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90,
                argumentOfPeriapsis = 45,
                meanAnomalyAtEpoch = 1.23,
                epoch = startUT,
                bodyName = body
            };
        }

        private List<TrajectoryPoint> MakePoints(int count, double startUT = 100)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < count; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + i * 10,
                    latitude = 0,
                    longitude = 0,
                    altitude = 100,
                    rotation = UnityEngine.Quaternion.identity,
                    velocity = UnityEngine.Vector3.zero,
                    bodyName = "Kerbin"
                });
            }
            return points;
        }

        #region FindOrbitSegment

        [Fact]
        public void FindOrbitSegment_EmptyList_ReturnsNull()
        {
            var segments = new List<OrbitSegment>();
            var result = TrajectoryMath.FindOrbitSegment(segments, 500);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_NullList_ReturnsNull()
        {
            var result = TrajectoryMath.FindOrbitSegment(null, 500);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_UTInRange_ReturnsSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 150);
            Assert.NotNull(result);
            Assert.Equal(100, result.Value.startUT);
            Assert.Equal(200, result.Value.endUT);
        }

        [Fact]
        public void FindOrbitSegment_UTBeforeRange_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 50);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_UTAfterRange_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 250);
            Assert.Null(result);
        }

        [Fact]
        public void FindOrbitSegment_UTAtExactStart_ReturnsSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 100);
            Assert.NotNull(result);
        }

        [Fact]
        public void FindOrbitSegment_UTAtExactEnd_ReturnsSegment()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 200);
            Assert.NotNull(result);
        }

        [Fact]
        public void FindOrbitSegment_MultipleSegments_FindsCorrectOne()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(300, 400, "Mun"),
                MakeSegment(500, 600, "Minmus")
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 350);
            Assert.NotNull(result);
            Assert.Equal("Mun", result.Value.bodyName);
        }

        [Fact]
        public void FindOrbitSegment_UTBetweenSegments_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200),
                MakeSegment(300, 400)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, 250);
            Assert.Null(result);
        }

        #endregion

        #region OrbitSegment Serialization

        [Fact]
        public void FindOrbitSegment_AdjacentSegments_NoOverlap()
        {
            // Two segments sharing a boundary at ut=200
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(200, 300, "Mun")
            };

            // ut=200 is the exclusive end of first, inclusive start of second
            var result = TrajectoryMath.FindOrbitSegment(segments, 200);
            Assert.NotNull(result);
            Assert.Equal("Mun", result.Value.bodyName);
        }

        [Fact]
        public void FindOrbitSegment_LastSegment_InclusiveEnd()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200, "Kerbin"),
                MakeSegment(300, 400, "Mun")
            };

            // ut=400 at endUT of last segment — should match (inclusive)
            var result = TrajectoryMath.FindOrbitSegment(segments, 400);
            Assert.NotNull(result);
            Assert.Equal("Mun", result.Value.bodyName);
        }

        [Fact]
        public void FindOrbitSegment_NegativeUT_ReturnsNull()
        {
            var segments = new List<OrbitSegment>
            {
                MakeSegment(100, 200)
            };

            var result = TrajectoryMath.FindOrbitSegment(segments, -50);
            Assert.Null(result);
        }

        [Fact]
        public void Recording_OnlyOrbitSegments_EmptyPoints()
        {
            var rec = new RecordingStore.Recording();
            rec.OrbitSegments.Add(MakeSegment(500, 1000));

            // StartUT/EndUT come from Points, which is empty
            Assert.Equal(0, rec.StartUT);
            Assert.Equal(0, rec.EndUT);
        }

        [Fact]
        public void FindOrbitSegment_SinglePointBoundary()
        {
            // startUT == endUT, a degenerate segment
            var segments = new List<OrbitSegment>
            {
                MakeSegment(150, 150)
            };

            // Last (only) segment uses inclusive end, so exact match works
            var result = TrajectoryMath.FindOrbitSegment(segments, 150);
            Assert.NotNull(result);
            Assert.Equal(150, result.Value.startUT);
        }

        #endregion

        #region Recording with OrbitSegments

        [Fact]
        public void Recording_OrbitSegments_InitializedEmpty()
        {
            var rec = new RecordingStore.Recording();
            Assert.NotNull(rec.OrbitSegments);
            Assert.Empty(rec.OrbitSegments);
        }

        [Fact]
        public void Recording_WithOrbitSegments_PreservesStartEndUT()
        {
            var rec = new RecordingStore.Recording();
            rec.Points = MakePoints(5, startUT: 100);
            rec.OrbitSegments.Add(MakeSegment(120, 130));

            // StartUT and EndUT come from trajectory points, not orbit segments
            Assert.Equal(100, rec.StartUT);
            Assert.Equal(140, rec.EndUT); // 100 + 4*10
        }

        [Fact]
        public void StashPending_WithOrbitSegments_CopiesSegments()
        {
            var points = MakePoints(5);
            var segments = new List<OrbitSegment>
            {
                MakeSegment(110, 120),
                MakeSegment(130, 140)
            };

            RecordingStore.StashPending(points, "TestVessel", segments);

            Assert.True(RecordingStore.HasPending);
            Assert.Equal(2, RecordingStore.Pending.OrbitSegments.Count);
            Assert.Equal(110, RecordingStore.Pending.OrbitSegments[0].startUT);
            Assert.Equal(130, RecordingStore.Pending.OrbitSegments[1].startUT);
        }

        [Fact]
        public void StashPending_WithNullSegments_InitializesEmptyList()
        {
            var points = MakePoints(5);

            RecordingStore.StashPending(points, "TestVessel", null);

            Assert.True(RecordingStore.HasPending);
            Assert.NotNull(RecordingStore.Pending.OrbitSegments);
            Assert.Empty(RecordingStore.Pending.OrbitSegments);
        }

        [Fact]
        public void StashPending_WithoutSegmentsParam_InitializesEmptyList()
        {
            var points = MakePoints(5);

            RecordingStore.StashPending(points, "TestVessel");

            Assert.True(RecordingStore.HasPending);
            Assert.NotNull(RecordingStore.Pending.OrbitSegments);
            Assert.Empty(RecordingStore.Pending.OrbitSegments);
        }

        [Fact]
        public void CommitPending_WithOrbitSegments_PreservesSegments()
        {
            var points = MakePoints(5);
            var segments = new List<OrbitSegment>
            {
                MakeSegment(110, 120, "Mun")
            };

            RecordingStore.StashPending(points, "Ship", segments);
            RecordingStore.CommitPending();

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Single(RecordingStore.CommittedRecordings[0].OrbitSegments);
            Assert.Equal("Mun", RecordingStore.CommittedRecordings[0].OrbitSegments[0].bodyName);
        }

        #endregion
    }
}
