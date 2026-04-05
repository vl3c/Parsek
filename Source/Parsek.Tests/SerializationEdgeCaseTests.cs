using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Edge-case roundtrip tests for RecordingStore serialization via the internal static
    /// SerializeTrajectoryInto / DeserializeTrajectoryFrom API.
    /// These exercise the private SerializePoint/DeserializePoint and
    /// SerializeOrbitSegment/DeserializeOrbitSegment methods indirectly.
    /// Existing tests cover happy-path; these target locale safety, extremes, and optional fields.
    /// </summary>
    [Collection("Sequential")]
    public class SerializationEdgeCaseTests : System.IDisposable
    {
        public SerializationEdgeCaseTests()
        {
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;
            ParsekLog.SuppressLogging = true;
        }

        private Recording RoundTrip(Recording original)
        {
            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, original);
            var restored = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, restored);
            return restored;
        }

        #region Point serialization edge cases

        /// <summary>
        /// Catches: locale-dependent serialization bug where 1234.5 becomes "1234,5"
        /// on comma-locale systems, then fails to parse back.
        /// Uses values with decimal parts that would break under comma locale.
        /// </summary>
        [Fact]
        public void Point_RoundTrip_InvariantCulture_CommaLocaleSafe()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 17123.456,
                latitude = -0.0972345,
                longitude = -74.557589,
                altitude = 1234.567,
                rotation = new Quaternion(0.123f, 0.456f, 0.789f, 0.321f),
                bodyName = "Kerbin",
                velocity = new Vector3(12.34f, 56.78f, 90.12f),
                funds = 123456.789,
                science = 12.5f,
                reputation = 3.7f
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.Points);
            var p = restored.Points[0];
            Assert.Equal(17123.456, p.ut, 10);
            Assert.Equal(-0.0972345, p.latitude, 6);
            Assert.Equal(-74.557589, p.longitude, 5);
            Assert.Equal(1234.567, p.altitude, 3);
            Assert.Equal(0.123, (double)p.rotation.x, 3);
            Assert.Equal(0.456, (double)p.rotation.y, 3);
            Assert.Equal(0.789, (double)p.rotation.z, 3);
            Assert.Equal(0.321, (double)p.rotation.w, 3);
            Assert.Equal(12.34, (double)p.velocity.x, 2);
            Assert.Equal(56.78, (double)p.velocity.y, 2);
            Assert.Equal(90.12, (double)p.velocity.z, 2);
            Assert.Equal(123456.789, p.funds, 3);
            Assert.Equal(12.5, (double)p.science, 1);
            Assert.Equal(3.7, (double)p.reputation, 1);
        }

        /// <summary>
        /// Catches: very large double values (near max) losing precision or becoming Infinity
        /// during serialization with "R" format specifier.
        /// </summary>
        [Fact]
        public void Point_RoundTrip_VeryLargeDoubles()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 1e15,       // ~31.7 million years in seconds
                latitude = 89.999999,
                longitude = 179.999999,
                altitude = 1e10, // 10 billion meters
                bodyName = "Jool",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
                funds = 1e14
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.Points);
            var p = restored.Points[0];
            Assert.Equal(1e15, p.ut, 0);
            Assert.Equal(89.999999, p.latitude, 5);
            Assert.Equal(179.999999, p.longitude, 5);
            Assert.Equal(1e10, p.altitude, 0);
            Assert.Equal("Jool", p.bodyName);
            Assert.Equal(1e14, p.funds, 0);
        }

        /// <summary>
        /// Catches: negative altitude or coordinates rejected or corrupted during serialization.
        /// Negative altitude is valid (below sea level on Kerbin).
        /// </summary>
        [Fact]
        public void Point_RoundTrip_NegativeCoordinates()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100.0,
                latitude = -45.678,
                longitude = -123.456,
                altitude = -50.0, // Below sea level
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = new Vector3(-100, -200, -300),
                funds = -500.0 // Debt?
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.Points);
            var p = restored.Points[0];
            Assert.Equal(-45.678, p.latitude, 3);
            Assert.Equal(-123.456, p.longitude, 3);
            Assert.Equal(-50.0, p.altitude, 1);
            Assert.Equal(-100.0, (double)p.velocity.x, 1);
            Assert.Equal(-500.0, p.funds, 1);
        }

        /// <summary>
        /// Catches: zero values incorrectly omitted or parsed as empty string.
        /// </summary>
        [Fact]
        public void Point_RoundTrip_AllZeros()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 0.0,
                latitude = 0.0,
                longitude = 0.0,
                altitude = 0.0,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 0),
                velocity = Vector3.zero,
                funds = 0.0,
                science = 0f,
                reputation = 0f
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.Points);
            var p = restored.Points[0];
            Assert.Equal(0.0, p.ut, 10);
            Assert.Equal(0.0, p.latitude, 10);
            Assert.Equal(0.0, p.altitude, 10);
            Assert.Equal(0.0, (double)p.rotation.x, 10);
            Assert.Equal(0.0, (double)p.velocity.x, 10);
            Assert.Equal(0.0, p.funds, 10);
        }

        /// <summary>
        /// Catches: multiple points serialized/deserialized out of order or with count mismatch.
        /// </summary>
        [Fact]
        public void Point_RoundTrip_MultiplePointsPreserveOrder()
        {
            var rec = new Recording();
            for (int i = 0; i < 5; i++)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = 100.0 + i * 10.0,
                    latitude = i * 1.5,
                    longitude = i * -2.5,
                    altitude = i * 1000.0,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                });
            }

            var restored = RoundTrip(rec);

            Assert.Equal(5, restored.Points.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(100.0 + i * 10.0, restored.Points[i].ut, 5);
                Assert.Equal(i * 1.5, restored.Points[i].latitude, 5);
                Assert.Equal(i * -2.5, restored.Points[i].longitude, 5);
            }
        }

        /// <summary>
        /// Catches: missing body field not defaulting to "Kerbin".
        /// DeserializePoint defaults body to "Kerbin" when null.
        /// </summary>
        [Fact]
        public void Point_Deserialize_MissingBody_DefaultsToKerbin()
        {
            var node = new ConfigNode("TEST");
            var ptNode = node.AddNode("POINT");
            ptNode.AddValue("ut", "100");
            ptNode.AddValue("lat", "0");
            ptNode.AddValue("lon", "0");
            ptNode.AddValue("alt", "0");
            // No "body" key

            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.Points);
            Assert.Equal("Kerbin", rec.Points[0].bodyName);
        }

        #endregion

        #region OrbitSegment serialization edge cases

        /// <summary>
        /// Catches: orbital frame rotation fields (ofrX/Y/Z/W) not serialized when present,
        /// or lost during deserialization.
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_WithOrbitalFrameRotation()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000, endUT = 2000,
                inclination = 28.5, eccentricity = 0.01, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90.0, argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.5, epoch = 1000, bodyName = "Kerbin",
                orbitalFrameRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.OrbitSegments);
            var s = restored.OrbitSegments[0];
            Assert.Equal(0.1, (double)s.orbitalFrameRotation.x, 3);
            Assert.Equal(0.2, (double)s.orbitalFrameRotation.y, 3);
            Assert.Equal(0.3, (double)s.orbitalFrameRotation.z, 3);
            Assert.Equal(0.9, (double)s.orbitalFrameRotation.w, 3);
        }

        /// <summary>
        /// Catches: orbital frame rotation not omitted when default (0,0,0,0) — sentinel value.
        /// When HasOrbitalFrameRotation returns false, fields should not be serialized,
        /// and deserialized result should have default (0,0,0,0).
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_WithoutOrbitalFrameRotation_DefaultSentinel()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000, endUT = 2000,
                inclination = 28.5, eccentricity = 0.01, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 90.0, argumentOfPeriapsis = 45.0,
                meanAnomalyAtEpoch = 0.5, epoch = 1000, bodyName = "Kerbin"
                // orbitalFrameRotation left at default (0,0,0,0)
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.OrbitSegments);
            var s = restored.OrbitSegments[0];
            // Default sentinel should be preserved
            Assert.Equal(0.0, (double)s.orbitalFrameRotation.x, 10);
            Assert.Equal(0.0, (double)s.orbitalFrameRotation.y, 10);
            Assert.Equal(0.0, (double)s.orbitalFrameRotation.z, 10);
            Assert.Equal(0.0, (double)s.orbitalFrameRotation.w, 10);
        }

        /// <summary>
        /// Catches: angular velocity fields (avX/Y/Z) not serialized when spinning,
        /// or serialized when below threshold (wasting space).
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_WithAngularVelocity()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000, endUT = 2000,
                inclination = 0, eccentricity = 0, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 0, argumentOfPeriapsis = 0,
                meanAnomalyAtEpoch = 0, epoch = 1000, bodyName = "Kerbin",
                angularVelocity = new Vector3(0.5f, 1.0f, 1.5f) // Above SpinThreshold
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.OrbitSegments);
            var s = restored.OrbitSegments[0];
            Assert.Equal(0.5, (double)s.angularVelocity.x, 3);
            Assert.Equal(1.0, (double)s.angularVelocity.y, 3);
            Assert.Equal(1.5, (double)s.angularVelocity.z, 3);
        }

        /// <summary>
        /// Catches: angular velocity below spin threshold being serialized (should be omitted).
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_SubThresholdAngularVelocity_NotSerialized()
        {
            var rec = new Recording();
            // angularVelocity with magnitude below SpinThreshold (0.05 rad/s)
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000, endUT = 2000,
                inclination = 0, eccentricity = 0, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 0, argumentOfPeriapsis = 0,
                meanAnomalyAtEpoch = 0, epoch = 1000, bodyName = "Kerbin",
                angularVelocity = new Vector3(0.01f, 0.01f, 0.01f) // Below threshold
            });

            // Serialize and check raw node — avX/Y/Z should NOT be present
            var node = new ConfigNode("TEST");
            RecordingStore.SerializeTrajectoryInto(node, rec);
            var segNode = node.GetNodes("ORBIT_SEGMENT")[0];
            Assert.Null(segNode.GetValue("avX"));
            Assert.Null(segNode.GetValue("avY"));
            Assert.Null(segNode.GetValue("avZ"));

            // Roundtrip should give back zero angular velocity
            var restored = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, restored);
            Assert.Equal(0.0, (double)restored.OrbitSegments[0].angularVelocity.x, 10);
            Assert.Equal(0.0, (double)restored.OrbitSegments[0].angularVelocity.y, 10);
            Assert.Equal(0.0, (double)restored.OrbitSegments[0].angularVelocity.z, 10);
        }

        /// <summary>
        /// Catches: zero eccentricity (perfectly circular orbit) causing division by zero
        /// or special-case serialization issues.
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_ZeroEccentricity()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1000, endUT = 2000,
                inclination = 0, eccentricity = 0, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 0, argumentOfPeriapsis = 0,
                meanAnomalyAtEpoch = 0, epoch = 1000, bodyName = "Kerbin"
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.OrbitSegments);
            Assert.Equal(0.0, restored.OrbitSegments[0].eccentricity, 10);
        }

        /// <summary>
        /// Catches: high inclination (polar or retrograde orbit) serialization issue.
        /// Inclination in radians can exceed pi.
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_HighInclination()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 5000, endUT = 6000,
                inclination = 3.0, // ~172 degrees — nearly retrograde
                eccentricity = 0.5, semiMajorAxis = 1000000,
                longitudeOfAscendingNode = 2.5, argumentOfPeriapsis = 1.8,
                meanAnomalyAtEpoch = 4.2, epoch = 5000, bodyName = "Eve"
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.OrbitSegments);
            var s = restored.OrbitSegments[0];
            Assert.Equal(3.0, s.inclination, 10);
            Assert.Equal(0.5, s.eccentricity, 10);
            Assert.Equal(1000000.0, s.semiMajorAxis, 5);
            Assert.Equal("Eve", s.bodyName);
        }

        /// <summary>
        /// Catches: missing body field on orbit segment not defaulting to "Kerbin".
        /// </summary>
        [Fact]
        public void OrbitSegment_Deserialize_MissingBody_DefaultsToKerbin()
        {
            var node = new ConfigNode("TEST");
            var seg = node.AddNode("ORBIT_SEGMENT");
            seg.AddValue("startUT", "1000");
            seg.AddValue("endUT", "2000");
            seg.AddValue("inc", "0");
            seg.AddValue("ecc", "0");
            seg.AddValue("sma", "700000");
            seg.AddValue("lan", "0");
            seg.AddValue("argPe", "0");
            seg.AddValue("mna", "0");
            seg.AddValue("epoch", "1000");
            // No "body" key

            var rec = new Recording();
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            Assert.Single(rec.OrbitSegments);
            Assert.Equal("Kerbin", rec.OrbitSegments[0].bodyName);
        }

        /// <summary>
        /// Catches: multiple orbit segments serialized/deserialized out of order or miscounted.
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_MultipleSegmentsPreserveOrder()
        {
            var rec = new Recording();
            for (int i = 0; i < 3; i++)
            {
                rec.OrbitSegments.Add(new OrbitSegment
                {
                    startUT = 1000 + i * 1000,
                    endUT = 2000 + i * 1000,
                    inclination = 0.5 * (i + 1),
                    eccentricity = 0.01 * (i + 1),
                    semiMajorAxis = 700000 + i * 100000,
                    longitudeOfAscendingNode = 30 * i,
                    argumentOfPeriapsis = 15 * i,
                    meanAnomalyAtEpoch = 0.1 * i,
                    epoch = 1000 + i * 1000,
                    bodyName = i == 2 ? "Mun" : "Kerbin"
                });
            }

            var restored = RoundTrip(rec);

            Assert.Equal(3, restored.OrbitSegments.Count);
            Assert.Equal(1000.0, restored.OrbitSegments[0].startUT, 1);
            Assert.Equal(2000.0, restored.OrbitSegments[1].startUT, 1);
            Assert.Equal(3000.0, restored.OrbitSegments[2].startUT, 1);
            Assert.Equal("Mun", restored.OrbitSegments[2].bodyName);
        }

        /// <summary>
        /// Catches: InvariantCulture not applied to orbit segment doubles,
        /// causing comma-locale parse failure on values like 700000.5.
        /// </summary>
        [Fact]
        public void OrbitSegment_RoundTrip_InvariantCulture_CommaLocaleSafe()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 1234.567,
                endUT = 2345.678,
                inclination = 28.123456,
                eccentricity = 0.012345,
                semiMajorAxis = 700123.456,
                longitudeOfAscendingNode = 90.987,
                argumentOfPeriapsis = 45.654,
                meanAnomalyAtEpoch = 0.543210,
                epoch = 1234.567,
                bodyName = "Kerbin",
                orbitalFrameRotation = new Quaternion(0.123f, 0.456f, 0.789f, 0.321f),
                angularVelocity = new Vector3(0.5f, 1.0f, 1.5f)
            });

            var restored = RoundTrip(rec);

            Assert.Single(restored.OrbitSegments);
            var s = restored.OrbitSegments[0];
            Assert.Equal(1234.567, s.startUT, 3);
            Assert.Equal(2345.678, s.endUT, 3);
            Assert.Equal(28.123456, s.inclination, 5);
            Assert.Equal(0.012345, s.eccentricity, 5);
            Assert.Equal(700123.456, s.semiMajorAxis, 3);
            Assert.Equal(0.123, (double)s.orbitalFrameRotation.x, 3);
            Assert.Equal(0.5, (double)s.angularVelocity.x, 3);
        }

        #endregion

        #region Mixed points and segments

        /// <summary>
        /// Catches: points and orbit segments interfering with each other during
        /// serialization into the same ConfigNode.
        /// </summary>
        [Fact]
        public void Mixed_PointsAndSegments_RoundTrip_Independent()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 1, longitude = 2, altitude = 3,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 4, longitude = 5, altitude = 6,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 300, endUT = 400,
                inclination = 0.5, eccentricity = 0.01, semiMajorAxis = 700000,
                longitudeOfAscendingNode = 0, argumentOfPeriapsis = 0,
                meanAnomalyAtEpoch = 0, epoch = 300, bodyName = "Kerbin"
            });

            var restored = RoundTrip(rec);

            Assert.Equal(2, restored.Points.Count); // Intentional count check — validating both types survive in same node
            Assert.Single(restored.OrbitSegments);
            Assert.Equal(100.0, restored.Points[0].ut, 5);
            Assert.Equal(200.0, restored.Points[1].ut, 5);
            Assert.Equal(300.0, restored.OrbitSegments[0].startUT, 5);
        }

        #endregion
    }
}
