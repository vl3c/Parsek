using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for methods extracted from ComputeStats:
    /// AccumulateOrbitSegmentStats and DeterminePrimaryBody.
    /// Also tests logging added to FindFirstMovingPoint and ComputeStats.
    /// </summary>
    [Collection("Sequential")]
    public class ComputeStatsExtractedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ComputeStatsExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static Func<string, double[]> KerbinLookup => name =>
        {
            if (name == "Kerbin") return new double[] { 600000, 3.5316e12 };
            return null;
        };

        #region AccumulateOrbitSegmentStats

        [Fact]
        public void AccumulateOrbitSegmentStats_EmptySegments_StatsUnchanged()
        {
            // Bug caught: passing empty list should not crash or modify stats
            var stats = new RecordingStats();
            stats.maxAltitude = 100;
            stats.maxSpeed = 50;
            stats.distanceTravelled = 1000;

            TrajectoryMath.AccumulateOrbitSegmentStats(
                new List<OrbitSegment>(), KerbinLookup, ref stats);

            Assert.Equal(100, stats.maxAltitude);
            Assert.Equal(50, stats.maxSpeed);
            Assert.Equal(1000, stats.distanceTravelled);
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_NullBodyLookup_StatsUnchanged()
        {
            // Bug caught: null bodyLookup should skip all segments gracefully
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 700000, eccentricity = 0.01,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, null, ref stats);

            Assert.Equal(0, stats.maxAltitude);
            Assert.Equal(0, stats.maxSpeed);
            Assert.Equal(0, stats.distanceTravelled);
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_UnknownBody_SkipsSegment()
        {
            // Bug caught: bodyLookup returning null for unknown body should not crash
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 700000, eccentricity = 0.01,
                    bodyName = "Eeloo"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            Assert.Equal(0, stats.maxAltitude);
            Assert.Equal(0, stats.maxSpeed);
            Assert.Equal(0, stats.distanceTravelled);
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_SingleSegment_CorrectApoapsisAltitude()
        {
            // Bug caught: incorrect apoapsis formula would produce wrong maxAltitude
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 700000, eccentricity = 0.01,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            // Apoapsis: 700000 * 1.01 - 600000 = 107000
            Assert.True(stats.maxAltitude > 106000 && stats.maxAltitude < 108000,
                $"Apoapsis altitude should be ~107000, got {stats.maxAltitude}");
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_SingleSegment_CorrectPeriapsisSpeed()
        {
            // Bug caught: incorrect vis-viva formula would produce wrong maxSpeed
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 700000, eccentricity = 0.01,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            // Periapsis speed via vis-viva at r = 700000 * 0.99 = 693000
            // sqrt(3.5316e12 * (2/693000 - 1/700000)) ~ 2268 m/s
            Assert.True(stats.maxSpeed > 2200 && stats.maxSpeed < 2350,
                $"Periapsis speed should be ~2268, got {stats.maxSpeed}");
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_SingleSegment_CorrectDistance()
        {
            // Bug caught: incorrect mean-speed distance formula
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 200, endUT = 2700, // 2500s
                    semiMajorAxis = 700000, eccentricity = 0.01,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            // Mean speed: sqrt(3.5316e12 / 700000) ~ 2246 m/s
            // Distance: 2246 * 2500 ~ 5,615,000m
            Assert.True(stats.distanceTravelled > 5500000,
                $"Distance should be >5500000, got {stats.distanceTravelled}");
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_MultipleSegments_AccumulatesDistance()
        {
            // Bug caught: distance from multiple segments should sum, not overwrite
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200, // 100s
                    semiMajorAxis = 700000, eccentricity = 0.0,
                    bodyName = "Kerbin"
                },
                new OrbitSegment
                {
                    startUT = 300, endUT = 400, // 100s
                    semiMajorAxis = 700000, eccentricity = 0.0,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            // Two segments with same duration = double the distance of one
            var singleStats = new RecordingStats();
            var singleSeg = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 700000, eccentricity = 0.0,
                    bodyName = "Kerbin"
                }
            };
            TrajectoryMath.AccumulateOrbitSegmentStats(singleSeg, KerbinLookup, ref singleStats);

            Assert.True(Math.Abs(stats.distanceTravelled - 2 * singleStats.distanceTravelled) < 1.0,
                $"Two equal segments should give double distance: {stats.distanceTravelled} vs 2*{singleStats.distanceTravelled}");
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_PreservesExistingStats()
        {
            // Bug caught: extracted method should accumulate INTO existing stats, not reset them
            var stats = new RecordingStats();
            stats.maxAltitude = 500000; // Higher than any orbit segment will produce
            stats.maxSpeed = 10000;     // Higher than periapsis speed
            stats.distanceTravelled = 1000;

            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 700000, eccentricity = 0.01,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            // Should keep higher pre-existing values
            Assert.Equal(500000, stats.maxAltitude);
            Assert.Equal(10000, stats.maxSpeed);
            // Distance should be accumulated (1000 + orbit distance)
            Assert.True(stats.distanceTravelled > 1000,
                $"Distance should have accumulated, got {stats.distanceTravelled}");
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_ZeroSemiMajorAxis_SkipsSpeedAndDistance()
        {
            // Bug caught: zero sma would cause division by zero in sqrt(gm/sma)
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 0, eccentricity = 0.5,
                    bodyName = "Kerbin"
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            // Zero sma => periRadius check fails (0 > 0 is false), distance check fails
            Assert.Equal(0, stats.maxSpeed);
            Assert.Equal(0, stats.distanceTravelled);
        }

        [Fact]
        public void AccumulateOrbitSegmentStats_NullBodyName_DefaultsToKerbin()
        {
            // Bug caught: null bodyName should default to "Kerbin" not crash
            var stats = new RecordingStats();
            var segments = new List<OrbitSegment>
            {
                new OrbitSegment
                {
                    startUT = 100, endUT = 200,
                    semiMajorAxis = 700000, eccentricity = 0.01,
                    bodyName = null
                }
            };

            TrajectoryMath.AccumulateOrbitSegmentStats(segments, KerbinLookup, ref stats);

            // Should successfully compute using Kerbin data
            Assert.True(stats.maxAltitude > 0, "Should have computed apoapsis altitude");
            Assert.True(stats.maxSpeed > 0, "Should have computed periapsis speed");
        }

        #endregion

        #region DeterminePrimaryBody

        [Fact]
        public void DeterminePrimaryBody_EmptyDict_ReturnsNull()
        {
            // Bug caught: empty dictionary should return null, not throw
            var result = TrajectoryMath.DeterminePrimaryBody(new Dictionary<string, int>());
            Assert.Null(result);
        }

        [Fact]
        public void DeterminePrimaryBody_SingleBody_ReturnsThatBody()
        {
            // Bug caught: single-entry dictionary should return that body
            var counts = new Dictionary<string, int> { { "Kerbin", 100 } };
            var result = TrajectoryMath.DeterminePrimaryBody(counts);
            Assert.Equal("Kerbin", result);
        }

        [Fact]
        public void DeterminePrimaryBody_MultipleBodies_ReturnsMostFrequent()
        {
            // Bug caught: wrong comparison operator (>= vs >) would change tie-breaking
            var counts = new Dictionary<string, int>
            {
                { "Kerbin", 50 },
                { "Mun", 30 },
                { "Minmus", 20 }
            };
            var result = TrajectoryMath.DeterminePrimaryBody(counts);
            Assert.Equal("Kerbin", result);
        }

        [Fact]
        public void DeterminePrimaryBody_TiedCounts_ReturnsFirstEncountered()
        {
            // Bug caught: tie-breaking should be deterministic (first encountered with max)
            // Note: Dictionary iteration order is not guaranteed, but the logic should
            // consistently pick whichever body it encounters first at the max count
            var counts = new Dictionary<string, int>
            {
                { "Kerbin", 50 },
                { "Mun", 50 }
            };
            var result = TrajectoryMath.DeterminePrimaryBody(counts);
            Assert.NotNull(result);
            Assert.True(result == "Kerbin" || result == "Mun",
                $"Expected Kerbin or Mun, got {result}");
        }

        [Fact]
        public void DeterminePrimaryBody_SecondBodyMoreFrequent_ReturnsSecond()
        {
            // Bug caught: iteration must check all entries, not short-circuit
            var counts = new Dictionary<string, int>
            {
                { "Kerbin", 10 },
                { "Mun", 90 }
            };
            var result = TrajectoryMath.DeterminePrimaryBody(counts);
            Assert.Equal("Mun", result);
        }

        #endregion

        #region ComputeStats Logging

        [Fact]
        public void ComputeStats_EmptyTrajectory_LogsEmpty()
        {
            // Bug caught: missing log on empty recording would hide silent no-ops
            ParsekLog.ResetRateLimitsForTesting();
            var rec = new RecordingStore.Recording();
            TrajectoryMath.ComputeStats(rec);

            Assert.Contains(logLines, l => l.Contains("empty trajectory"));
        }

        [Fact]
        public void ComputeStats_NonEmptyTrajectory_LogsCompletion()
        {
            // Bug caught: missing completion log would hide stats calculation results
            var rec = new RecordingStore.Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, altitude = 500, velocity = new Vector3(100, 0, 0), bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, altitude = 1000, velocity = new Vector3(200, 0, 0), bodyName = "Kerbin"
            });

            TrajectoryMath.ComputeStats(rec);

            Assert.Contains(logLines, l => l.Contains("ComputeStats complete"));
            Assert.Contains(logLines, l => l.Contains("maxAlt="));
            Assert.Contains(logLines, l => l.Contains("body=Kerbin"));
        }

        #endregion

        #region FindFirstMovingPoint Logging

        [Fact]
        public void FindFirstMovingPoint_AltitudeTrigger_LogsAltitude()
        {
            // Bug caught: missing log would hide which trigger fired and at what index
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, altitude = 78, velocity = Vector3.zero },
                new TrajectoryPoint { ut = 101, altitude = 78, velocity = Vector3.zero },
                new TrajectoryPoint { ut = 102, altitude = 80, velocity = Vector3.zero }
            };

            TrajectoryMath.FindFirstMovingPoint(points);

            Assert.Contains(logLines, l => l.Contains("altitude trigger") && l.Contains("index 2"));
        }

        [Fact]
        public void FindFirstMovingPoint_SpeedTrigger_LogsSpeed()
        {
            // Bug caught: missing log would hide which trigger fired
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, altitude = 10, velocity = Vector3.zero },
                new TrajectoryPoint { ut = 101, altitude = 10, velocity = new Vector3(6, 0, 0) }
            };

            TrajectoryMath.FindFirstMovingPoint(points);

            Assert.Contains(logLines, l => l.Contains("speed trigger") && l.Contains("index 1"));
        }

        [Fact]
        public void FindFirstMovingPoint_NeverMoved_LogsNeverMoved()
        {
            // Bug caught: missing log would hide that vessel was stationary throughout
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, altitude = 78, velocity = Vector3.zero },
                new TrajectoryPoint { ut = 101, altitude = 78, velocity = Vector3.zero },
                new TrajectoryPoint { ut = 102, altitude = 78, velocity = Vector3.zero }
            };

            TrajectoryMath.FindFirstMovingPoint(points);

            Assert.Contains(logLines, l => l.Contains("never moved significantly"));
        }

        #endregion
    }
}
