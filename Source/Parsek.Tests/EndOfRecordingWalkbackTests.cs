using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided"/> —
    /// the linear-interpolation walkback added in #264 for end-of-recording EVA spawns.
    /// All tests drive the pure helper via injected closures; no KSP runtime dependency.
    /// </summary>
    [Collection("Sequential")]
    public class EndOfRecordingWalkbackTests : IDisposable
    {
        // Kerbin radius — large enough that SurfaceDistance's small-angle approximation
        // is accurate to <0.1% at EVA scales.
        private const double KerbinRadius = 600000.0;

        // Default sub-step size matches the production constant.
        private readonly float StepMeters = SpawnCollisionDetector.DefaultWalkbackStepMeters;

        private readonly List<string> logLines = new List<string>();

        public EndOfRecordingWalkbackTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ─────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Simple flat-Earth lat/lon/alt → world position (x=east, y=north, z=up).
        /// Matches the scale used by SurfaceDistance — the walkback only cares about
        /// relative distances and whether `isOverlapping` returns true at each candidate.
        /// </summary>
        private static Vector3d FlatWorldPos(double lat, double lon, double alt, double bodyRadius)
        {
            double dLat = lat * Math.PI / 180.0;
            double dLon = lon * Math.PI / 180.0;
            double avgLat = dLat; // we're working near equator in tests
            double x = dLon * Math.Cos(avgLat) * bodyRadius;
            double y = dLat * bodyRadius;
            double z = alt;
            return new Vector3d(x, y, z);
        }

        private Func<double, double, double, Vector3d> PosClosure =>
            (lat, lon, alt) => FlatWorldPos(lat, lon, alt, KerbinRadius);

        /// <summary>
        /// Convert a metric offset in metres to a latitude delta in degrees.
        /// </summary>
        private static double MetersToLatDegrees(double meters, double bodyRadius)
        {
            return meters / bodyRadius * 180.0 / Math.PI;
        }

        private static TrajectoryPoint Pt(double lat, double lon, double alt, double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        // ─────────────────────────────────────────────────────────────
        //  Base cases and edge cases
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void WalkbackSubdivided_NullTrajectory_ReturnsNotFound()
        {
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                null, KerbinRadius, StepMeters, PosClosure, _ => true);

            Assert.False(result.found);
            Assert.Equal(0.0, result.lat);
            Assert.Equal(0.0, result.lon);
            Assert.Equal(0.0, result.alt);
        }

        [Fact]
        public void WalkbackSubdivided_EmptyTrajectory_ReturnsNotFound()
        {
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                new List<TrajectoryPoint>(), KerbinRadius, StepMeters, PosClosure, _ => true);

            Assert.False(result.found);
        }

        [Fact]
        public void WalkbackSubdivided_SinglePointClear_ReturnsThatPoint()
        {
            var points = new List<TrajectoryPoint> { Pt(0.001, 0.001, 70.0, 100.0) };
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, StepMeters, PosClosure, _ => false);

            Assert.True(result.found);
            Assert.Equal(0.001, result.lat);
            Assert.Equal(0.001, result.lon);
            Assert.Equal(70.0, result.alt);
        }

        [Fact]
        public void WalkbackSubdivided_SinglePointOverlaps_ReturnsNotFound()
        {
            var points = new List<TrajectoryPoint> { Pt(0.001, 0.001, 70.0, 100.0) };
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, StepMeters, PosClosure, _ => true);

            Assert.False(result.found);
        }

        [Fact]
        public void WalkbackSubdivided_LastPointClear_ReturnsLastPoint()
        {
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(0.0001, 0.0, 70.0, 101.0),
                Pt(0.0002, 0.0, 70.0, 102.0),
            };
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, StepMeters, PosClosure, _ => false);

            Assert.True(result.found);
            Assert.Equal(0.0002, result.lat);
        }

        // ─────────────────────────────────────────────────────────────
        //  Step-count correctness
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void WalkbackSubdivided_100MeterSegment_Uses67SubSteps()
        {
            // 2 points 100 m apart on Kerbin. stepMeters = 1.5.
            // Expected: ceil(100 / 1.5) = 67 sub-steps in the segment.
            double latDelta100m = MetersToLatDegrees(100.0, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(latDelta100m, 0.0, 70.0, 101.0),
            };

            int overlapCalls = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { overlapCalls++; return true; });

            Assert.False(result.found);
            // Base case (last point) + 67 sub-steps from [0↔1] = 68 calls total.
            Assert.Equal(68, overlapCalls);
        }

        [Fact]
        public void WalkbackSubdivided_SegmentShorterThanStep_OneSubStep()
        {
            // 2 points 0.8 m apart. ceil(0.8 / 1.5) = 1 sub-step.
            double latDelta = MetersToLatDegrees(0.8, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(latDelta, 0.0, 70.0, 101.0),
            };

            int overlapCalls = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { overlapCalls++; return true; });

            Assert.False(result.found);
            // Base case + 1 sub-step = 2 calls.
            Assert.Equal(2, overlapCalls);
        }

        [Fact]
        public void WalkbackSubdivided_SegmentNearStepMeters_CorrectCallCount()
        {
            // Off-by-one guard. 2 points ~1.4 m apart (slightly under stepMeters).
            // Use a round lat-delta that round-trips cleanly to avoid float-precision
            // drift pushing ceil() one higher.
            double latDelta = MetersToLatDegrees(1.4, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(latDelta, 0.0, 70.0, 101.0),
            };

            // Compute the actual distance the helper will see — this is what drives n.
            double actualD = SpawnCollisionDetector.SurfaceDistance(0.0, 0.0, latDelta, 0.0, KerbinRadius);
            int expectedN = Math.Max(1, (int)Math.Ceiling(actualD / 1.5));
            int expectedCalls = 1 + expectedN; // base case + n sub-steps

            int overlapCalls = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { overlapCalls++; return true; });

            Assert.False(result.found);
            Assert.Equal(expectedCalls, overlapCalls);
            // For 1.4 m, n should be exactly 1 — guard against any future bug that
            // splits a sub-step-sized segment into multiple candidates.
            Assert.Equal(1, expectedN);
        }

        [Fact]
        public void WalkbackSubdivided_SegmentJustOverStepMeters_IncrementsSubStepCount()
        {
            // Adjacent off-by-one case: a segment clearly over stepMeters produces n >= 2.
            double latDelta = MetersToLatDegrees(2.0, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(latDelta, 0.0, 70.0, 101.0),
            };

            double actualD = SpawnCollisionDetector.SurfaceDistance(0.0, 0.0, latDelta, 0.0, KerbinRadius);
            int expectedN = Math.Max(1, (int)Math.Ceiling(actualD / 1.5));
            int expectedCalls = 1 + expectedN;

            int overlapCalls = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { overlapCalls++; return true; });

            Assert.False(result.found);
            Assert.Equal(expectedCalls, overlapCalls);
            Assert.Equal(2, expectedN);
        }

        [Fact]
        public void WalkbackSubdivided_ZeroLengthSegment_ContinuesToNextSegment()
        {
            // 3 points: [A, A, B]. Middle segment is zero-length (duplicate points).
            // Walkback should traverse it cleanly and continue to the [A, B] segment.
            double latDelta = MetersToLatDegrees(10.0, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),          // A
                Pt(0.0, 0.0, 70.0, 101.0),          // A (duplicate)
                Pt(latDelta, 0.0, 70.0, 102.0),     // B
            };

            // Derive expected call count from actual SurfaceDistance to avoid fp drift.
            double d_1_2 = SpawnCollisionDetector.SurfaceDistance(0.0, 0.0, latDelta, 0.0, KerbinRadius);
            int n_1_2 = Math.Max(1, (int)Math.Ceiling(d_1_2 / 1.5));
            int expectedCalls = 1 /*base case*/ + n_1_2 /*[1↔2]*/ + 1 /*[0↔1] zero-length n=1*/;

            int overlapCalls = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { overlapCalls++; return true; });

            Assert.False(result.found);
            Assert.Equal(expectedCalls, overlapCalls);
        }

        // ─────────────────────────────────────────────────────────────
        //  Overlap-then-clear: the walkback actually walks
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void WalkbackSubdivided_LastPointOverlaps_FindsFirstClearSubStep()
        {
            // 2 points 10 m apart on Kerbin. First 3 calls overlap (base case + 2 sub-steps),
            // 4th call clears. Computes expected lat/lon from the actual segment distance
            // rather than hardcoding n (fp round-trip can shift n by ±1).
            double latDelta = MetersToLatDegrees(10.0, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(latDelta, 0.0, 70.0, 101.0),
            };

            double actualD = SpawnCollisionDetector.SurfaceDistance(0.0, 0.0, latDelta, 0.0, KerbinRadius);
            int n = Math.Max(1, (int)Math.Ceiling(actualD / 1.5));

            int callCount = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { callCount++; return callCount <= 3; });

            Assert.True(result.found);
            // 4th call clears. Base case = call 1, sub-step 1 = call 2, sub-step 2 = call 3,
            // sub-step 3 = call 4 (first clear). So k=3, t=3/n.
            double t = 3.0 / n;
            double expectedLat = latDelta + (0.0 - latDelta) * t; // lerp(segEnd, segStart, t)
            Assert.Equal(expectedLat, result.lat, 8);
        }

        [Fact]
        public void WalkbackSubdivided_AllOverlap_LogsExhaustion()
        {
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(MetersToLatDegrees(5.0, KerbinRadius), 0.0, 70.0, 101.0),
            };

            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure, _ => true);

            Assert.False(result.found);
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") &&
                l.Contains("WalkbackSubdivided") &&
                l.Contains("entire trajectory overlaps"));
        }

        [Fact]
        public void WalkbackSubdivided_LastPointClear_LogsBaseCaseLine()
        {
            // Safe base-case path: caller already confirmed overlap at the last point,
            // but if isOverlapping returns false for the last-point check we take the
            // fast path and log a distinct "last point clear at index" line.
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(MetersToLatDegrees(5.0, KerbinRadius), 0.0, 70.0, 101.0),
            };

            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure, _ => false);

            Assert.True(result.found);
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") &&
                l.Contains("WalkbackSubdivided: last point clear"));
        }

        [Fact]
        public void WalkbackSubdivided_Clears_LogsSuccessLine()
        {
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(MetersToLatDegrees(5.0, KerbinRadius), 0.0, 70.0, 101.0),
            };

            int call = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { call++; return call == 1; }); // only last point overlaps, first sub-step clears

            Assert.True(result.found);
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") &&
                l.Contains("WalkbackSubdivided: cleared at segment"));
        }

        // ─────────────────────────────────────────────────────────────
        //  Linear interpolation accuracy
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void WalkbackSubdivided_InterpolatesLatLonLinearly()
        {
            // 2 points 6 m apart. Skip base case + first 2 sub-steps (3 overlap calls),
            // clear on sub-step 3 (k=3). Expected lat = lerp(segEnd, segStart, 3/n).
            double latDelta = MetersToLatDegrees(6.0, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(latDelta, 0.0, 70.0, 101.0),
            };

            double actualD = SpawnCollisionDetector.SurfaceDistance(0.0, 0.0, latDelta, 0.0, KerbinRadius);
            int n = Math.Max(1, (int)Math.Ceiling(actualD / 1.5));

            int call = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { call++; return call <= 3; });

            Assert.True(result.found);
            double t = 3.0 / n;
            double expectedLat = latDelta + (0.0 - latDelta) * t;
            Assert.Equal(expectedLat, result.lat, 10);
        }

        [Fact]
        public void WalkbackSubdivided_InterpolatesAltitudeLinearly()
        {
            // 2 points 6 m apart horizontally, alt differs by 8 m (kerbal stepped down a rock).
            // Clear on first sub-step (k=1, t=1/n). Expected alt = lerp(segEnd.alt, segStart.alt, t).
            double latDelta = MetersToLatDegrees(6.0, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                Pt(0.0, 0.0, 70.0, 100.0),
                Pt(latDelta, 0.0, 80.0, 101.0),
            };

            double actualD = SpawnCollisionDetector.SurfaceDistance(0.0, 0.0, latDelta, 0.0, KerbinRadius);
            int n = Math.Max(1, (int)Math.Ceiling(actualD / 1.5));

            int call = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 1.5f, PosClosure,
                _ => { call++; return call <= 1; });

            Assert.True(result.found);
            float t = 1f / n;
            double expectedAlt = 80.0 + (70.0 - 80.0) * t;
            Assert.Equal(expectedAlt, result.alt, 10);
        }

        [Fact]
        public void WalkbackSubdividedDetailed_InterpolatesUtVelocityAndRotation()
        {
            double latDelta = MetersToLatDegrees(6.0, KerbinRadius);
            var yaw90 = new Quaternion(0f, 0.70710677f, 0f, 0.70710677f);
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100.0,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 70.0,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                },
                new TrajectoryPoint
                {
                    ut = 104.0,
                    latitude = latDelta,
                    longitude = 0.0,
                    altitude = 80.0,
                    bodyName = "Kerbin",
                    rotation = yaw90,
                    velocity = new Vector3(8f, 0f, 0f),
                },
            };

            double actualD = SpawnCollisionDetector.SurfaceDistance(0.0, 0.0, latDelta, 0.0, KerbinRadius);
            int n = Math.Max(1, (int)Math.Ceiling(actualD / 1.5));

            int call = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdividedDetailed(
                points,
                KerbinRadius,
                1.5f,
                PosClosure,
                _ => { call++; return call <= 1; });

            Assert.True(result.found);
            float t = 1f / n;
            Assert.Equal(104.0 + (100.0 - 104.0) * t, result.point.ut, 10);
            Assert.Equal((double)(8f + (0f - 8f) * t), (double)result.point.velocity.x, 5);
            Assert.True(result.point.rotation.y > 0.001f);
            Assert.True(result.point.rotation.y < yaw90.y - 0.001f);
            Assert.True(result.point.rotation.w < 0.999f);
            Assert.True(result.point.rotation.w > yaw90.w + 0.001f);
        }

        [Fact]
        public void WalkbackSubdividedDetailed_BodyTransition_LogsTieBreakChoice()
        {
            double latDelta = MetersToLatDegrees(6.0, KerbinRadius);
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100.0,
                    latitude = 0.0,
                    longitude = 0.0,
                    altitude = 70.0,
                    bodyName = "Mun",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                },
                new TrajectoryPoint
                {
                    ut = 104.0,
                    latitude = latDelta,
                    longitude = 0.0,
                    altitude = 80.0,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                },
            };

            int call = 0;
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdividedDetailed(
                points,
                KerbinRadius,
                1.5f,
                PosClosure,
                _ => { call++; return call <= 1; });

            Assert.True(result.found);
            Assert.Equal("Kerbin", result.point.bodyName);
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") &&
                l.Contains("body transition") &&
                l.Contains("later='Kerbin'") &&
                l.Contains("earlier='Mun'") &&
                l.Contains("using 'Kerbin'"));
        }

        // ─────────────────────────────────────────────────────────────
        //  Degenerate inputs
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void WalkbackSubdivided_ZeroStepMeters_ClampsToDefault()
        {
            // Negative/zero step should not cause infinite loop — helper clamps to default.
            var points = new List<TrajectoryPoint> { Pt(0.0, 0.0, 70.0, 100.0) };
            var result = SpawnCollisionDetector.WalkbackAlongTrajectorySubdivided(
                points, KerbinRadius, 0f, PosClosure, _ => false);

            Assert.True(result.found);
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("invalid stepMeters"));
        }
    }
}
