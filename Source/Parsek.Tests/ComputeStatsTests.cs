using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class ComputeStatsTests
    {
        [Fact]
        public void EmptyRecording_AllStatsZero()
        {
            var rec = new RecordingStore.Recording();
            var stats = TrajectoryMath.ComputeStats(rec);

            Assert.Equal(0, stats.maxAltitude);
            Assert.Equal(0, stats.maxSpeed);
            Assert.Equal(0, stats.distanceTravelled);
            Assert.Equal(0, stats.pointCount);
            Assert.Equal(0, stats.orbitSegmentCount);
            Assert.Equal(0, stats.partEventCount);
            Assert.Null(stats.primaryBody);
            Assert.Equal(0, stats.maxRange);
        }

        [Fact]
        public void PointsOnly_NoBodyLookup_AltitudeAndSpeedFromPoints()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 100,
                velocity = new Vector3(50, 0, 0), bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 110, latitude = 0.001, longitude = 0.001, altitude = 500,
                velocity = new Vector3(0, 200, 0), bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 120, latitude = 0.002, longitude = 0.002, altitude = 300,
                velocity = new Vector3(100, 0, 0), bodyName = "Kerbin"
            });

            var stats = TrajectoryMath.ComputeStats(rec);

            Assert.Equal(500, stats.maxAltitude);
            Assert.Equal(200, stats.maxSpeed, 1);
            Assert.Equal(0, stats.distanceTravelled); // No body lookup → no distance
            Assert.Equal(3, stats.pointCount);
            Assert.Equal(0, stats.orbitSegmentCount);
            Assert.Equal("Kerbin", stats.primaryBody);
            Assert.Equal(0, stats.maxRange); // No body lookup → no range
        }

        [Fact]
        public void PointsWithBodyLookup_DistanceAndRangeComputed()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 100,
                velocity = Vector3.zero, bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 110, latitude = 0, longitude = 0.01, altitude = 100,
                velocity = Vector3.zero, bodyName = "Kerbin"
            });

            Func<string, double[]> bodyLookup = name =>
            {
                if (name == "Kerbin") return new double[] { 600000, 3.5316e12 };
                return null;
            };

            var stats = TrajectoryMath.ComputeStats(rec, bodyLookup);

            // 0.01 degrees of longitude at equator with radius ~600100m
            // Expected: ~104.7m (pi/180 * 0.01 * 600100)
            Assert.True(stats.distanceTravelled > 100,
                $"Distance should be >100m, got {stats.distanceTravelled}");
            Assert.True(stats.distanceTravelled < 120,
                $"Distance should be <120m, got {stats.distanceTravelled}");
            Assert.True(stats.maxRange > 100);
        }

        [Fact]
        public void OrbitSegment_WithBodyLookup_UpdatesAltitudeSpeedDistance()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 80000,
                velocity = Vector3.zero, bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0, longitude = 0, altitude = 80000,
                velocity = Vector3.zero, bodyName = "Kerbin"
            });

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 200,
                endUT = 2700, // 2500s
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                bodyName = "Kerbin"
            });

            Func<string, double[]> bodyLookup = name =>
            {
                if (name == "Kerbin") return new double[] { 600000, 3.5316e12 };
                return null;
            };

            var stats = TrajectoryMath.ComputeStats(rec, bodyLookup);

            // Apoapsis altitude: 700000 * 1.01 - 600000 = 107000m
            Assert.True(stats.maxAltitude > 106000 && stats.maxAltitude < 108000,
                $"Max altitude should be ~107km, got {stats.maxAltitude}");

            // Periapsis speed: vis-viva at r=693000
            // sqrt(3.5316e12 * (2/693000 - 1/700000)) ≈ 2268 m/s
            Assert.True(stats.maxSpeed > 2200 && stats.maxSpeed < 2350,
                $"Max speed should be ~2268 m/s, got {stats.maxSpeed}");

            // Distance: mean_speed * 2500s ≈ 2246 * 2500 = 5,615,000m
            Assert.True(stats.distanceTravelled > 5500000,
                $"Distance should be >5500km, got {stats.distanceTravelled}");

            Assert.Equal(1, stats.orbitSegmentCount);
        }

        [Fact]
        public void OrbitSegment_NullBodyLookup_SkippedGracefully()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, altitude = 80000, bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, altitude = 80000, bodyName = "Kerbin"
            });

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 200,
                endUT = 2700,
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                bodyName = "Kerbin"
            });

            var stats = TrajectoryMath.ComputeStats(rec, null);

            Assert.Equal(80000, stats.maxAltitude);
            Assert.Equal(0, stats.maxSpeed);
            Assert.Equal(0, stats.distanceTravelled);
            Assert.Equal(1, stats.orbitSegmentCount);
        }

        [Fact]
        public void OrbitSegment_BodyNotFound_SkippedGracefully()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, altitude = 100, bodyName = "Unknown"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, altitude = 100, bodyName = "Unknown"
            });

            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 200,
                endUT = 2700,
                semiMajorAxis = 700000,
                eccentricity = 0.01,
                bodyName = "Unknown"
            });

            Func<string, double[]> bodyLookup = name => null;

            var stats = TrajectoryMath.ComputeStats(rec, bodyLookup);

            Assert.Equal(100, stats.maxAltitude);
            Assert.Equal(0, stats.distanceTravelled);
        }

        [Fact]
        public void FleaFlight_MaxAltitudeFromPoints()
        {
            // Build Flea Flight via synthetic generator and deserialize
            var builder = SyntheticRecordingTests.FleaFlight(17000);
            var node = builder.Build();

            var rec = new RecordingStore.Recording();
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.DeserializeTrajectoryFrom(node, rec);

            var stats = TrajectoryMath.ComputeStats(rec);

            // Flea Flight peak altitude is 620m
            Assert.Equal(620, stats.maxAltitude);
            Assert.True(stats.pointCount > 20);
            Assert.Equal("Kerbin", stats.primaryBody);
        }

        [Fact]
        public void PartEvents_CountIncluded()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent { ut = 110, eventType = PartEventType.Decoupled });
            rec.PartEvents.Add(new PartEvent { ut = 120, eventType = PartEventType.ParachuteDeployed });
            rec.PartEvents.Add(new PartEvent { ut = 130, eventType = PartEventType.EngineIgnited });

            var stats = TrajectoryMath.ComputeStats(rec);

            Assert.Equal(3, stats.partEventCount);
        }

        [Fact]
        public void MultipleBodies_PrimaryBodyIsMostFrequent()
        {
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 300, bodyName = "Mun" });

            var stats = TrajectoryMath.ComputeStats(rec);

            Assert.Equal("Kerbin", stats.primaryBody);
        }
    }

    public class FormatAltitudeTests
    {
        [Theory]
        [InlineData(0, "0m")]
        [InlineData(100, "100m")]
        [InlineData(999, "999m")]
        [InlineData(1000, "1.0km")]
        [InlineData(1500, "1.5km")]
        [InlineData(12345, "12.3km")]
        [InlineData(620, "620m")]
        [InlineData(1000000, "1.0Mm")]
        [InlineData(2500000, "2.5Mm")]
        public void FormatAltitude_CorrectOutput(double meters, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatAltitude(meters));
        }
    }

    public class FormatSpeedTests
    {
        [Theory]
        [InlineData(0, "0m/s")]
        [InlineData(50, "50m/s")]
        [InlineData(999, "999m/s")]
        [InlineData(1000, "1.0km/s")]
        [InlineData(2268, "2.3km/s")]
        public void FormatSpeed_CorrectOutput(double mps, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatSpeed(mps));
        }
    }

    public class FormatDistanceTests
    {
        [Theory]
        [InlineData(0, "0m")]
        [InlineData(500, "500m")]
        [InlineData(999, "999m")]
        [InlineData(1000, "1.0km")]
        [InlineData(8200, "8.2km")]
        [InlineData(1000000, "1.0Mm")]
        [InlineData(18000000, "18.0Mm")]
        public void FormatDistance_CorrectOutput(double meters, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatDistance(meters));
        }
    }
}
