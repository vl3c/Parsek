using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class OrbitSegmentTests
    {
        public OrbitSegmentTests()
        {
            RecordingStore.SuppressLogging = true;
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
        public void OrbitSegment_SerializationRoundTrip()
        {
            var original = MakeSegment(12345.6789, 99999.1234, "Mun");
            original.inclination = 45.123456789;
            original.eccentricity = 0.0023456;
            original.semiMajorAxis = 1234567.89;
            original.longitudeOfAscendingNode = 180.5;
            original.argumentOfPeriapsis = 270.25;
            original.meanAnomalyAtEpoch = 3.14159;
            original.epoch = 11111.2222;

            // Serialize (using InvariantCulture, matching production code)
            var ic = CultureInfo.InvariantCulture;
            var node = new ConfigNode("ORBIT_SEGMENT");
            node.AddValue("startUT", original.startUT.ToString("R", ic));
            node.AddValue("endUT", original.endUT.ToString("R", ic));
            node.AddValue("inc", original.inclination.ToString("R", ic));
            node.AddValue("ecc", original.eccentricity.ToString("R", ic));
            node.AddValue("sma", original.semiMajorAxis.ToString("R", ic));
            node.AddValue("lan", original.longitudeOfAscendingNode.ToString("R", ic));
            node.AddValue("argPe", original.argumentOfPeriapsis.ToString("R", ic));
            node.AddValue("mna", original.meanAnomalyAtEpoch.ToString("R", ic));
            node.AddValue("epoch", original.epoch.ToString("R", ic));
            node.AddValue("body", original.bodyName);

            // Deserialize
            var loaded = new OrbitSegment();
            var inv = NumberStyles.Float;

            double.TryParse(node.GetValue("startUT"), inv, ic, out loaded.startUT);
            double.TryParse(node.GetValue("endUT"), inv, ic, out loaded.endUT);
            double.TryParse(node.GetValue("inc"), inv, ic, out loaded.inclination);
            double.TryParse(node.GetValue("ecc"), inv, ic, out loaded.eccentricity);
            double.TryParse(node.GetValue("sma"), inv, ic, out loaded.semiMajorAxis);
            double.TryParse(node.GetValue("lan"), inv, ic, out loaded.longitudeOfAscendingNode);
            double.TryParse(node.GetValue("argPe"), inv, ic, out loaded.argumentOfPeriapsis);
            double.TryParse(node.GetValue("mna"), inv, ic, out loaded.meanAnomalyAtEpoch);
            double.TryParse(node.GetValue("epoch"), inv, ic, out loaded.epoch);
            loaded.bodyName = node.GetValue("body");

            // Verify round-trip
            Assert.Equal(original.startUT, loaded.startUT);
            Assert.Equal(original.endUT, loaded.endUT);
            Assert.Equal(original.inclination, loaded.inclination);
            Assert.Equal(original.eccentricity, loaded.eccentricity);
            Assert.Equal(original.semiMajorAxis, loaded.semiMajorAxis);
            Assert.Equal(original.longitudeOfAscendingNode, loaded.longitudeOfAscendingNode);
            Assert.Equal(original.argumentOfPeriapsis, loaded.argumentOfPeriapsis);
            Assert.Equal(original.meanAnomalyAtEpoch, loaded.meanAnomalyAtEpoch);
            Assert.Equal(original.epoch, loaded.epoch);
            Assert.Equal(original.bodyName, loaded.bodyName);
        }

        [Fact]
        public void OrbitSegment_MissingBody_DefaultsToKerbin()
        {
            var node = new ConfigNode("ORBIT_SEGMENT");
            // No "body" value
            string bodyName = node.GetValue("body") ?? "Kerbin";
            Assert.Equal("Kerbin", bodyName);
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
