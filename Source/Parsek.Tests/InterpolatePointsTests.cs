using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for TrajectoryMath.InterpolatePoints — the internal static method
    /// that finds the bracketing pair of trajectory points for a given targetUT
    /// and computes the interpolation parameter t.
    /// </summary>
    [Collection("Sequential")]
    public class InterpolatePointsTests : IDisposable
    {
        public InterpolatePointsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static TrajectoryPoint MakePoint(double ut, double lat = 0, double lon = 0, double alt = 100)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
        }

        private static List<TrajectoryPoint> MakeTimeline(params double[] uts)
        {
            var list = new List<TrajectoryPoint>();
            foreach (var ut in uts)
                list.Add(MakePoint(ut, lat: ut * 0.001, lon: ut * 0.002, alt: ut));
            return list;
        }

        #region Normal interpolation

        /// <summary>
        /// Catches: broken interpolation where targetUT between two points returns false
        /// or computes wrong t value.
        /// </summary>
        [Fact]
        public void NormalCase_TargetBetweenTwoPoints_ReturnsTrueWithCorrectT()
        {
            var points = MakeTimeline(100, 200, 300, 400);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 150.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(100.0, before.ut, 5);
            Assert.Equal(200.0, after.ut, 5);
            Assert.Equal(0.5, (double)t, 4);
        }

        /// <summary>
        /// Catches: off-by-one in segment selection — targetUT at 75% through a segment
        /// should produce t=0.75.
        /// </summary>
        [Fact]
        public void NormalCase_QuarterWayThroughSegment_CorrectT()
        {
            var points = MakeTimeline(1000, 2000);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 1250.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(1000.0, before.ut, 5);
            Assert.Equal(2000.0, after.ut, 5);
            Assert.Equal(0.25, (double)t, 4);
        }

        /// <summary>
        /// Catches: wrong segment selected when target is in a later segment.
        /// </summary>
        [Fact]
        public void NormalCase_TargetInThirdSegment_SelectsCorrectPair()
        {
            var points = MakeTimeline(100, 200, 300, 400, 500);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 350.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(300.0, before.ut, 5);
            Assert.Equal(400.0, after.ut, 5);
            Assert.Equal(0.5, (double)t, 4);
        }

        /// <summary>
        /// Catches: t not clamped to [0,1] range at segment boundaries.
        /// targetUT exactly at a point boundary should produce t=0 for the next segment.
        /// </summary>
        [Fact]
        public void NormalCase_TargetExactlyAtPoint_TIsZero()
        {
            var points = MakeTimeline(100, 200, 300);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 200.0,
                out var before, out var after, out float t);

            Assert.True(result);
            // At ut=200, FindWaypointIndex returns index 1 (200<=200 && 300>200)
            Assert.Equal(200.0, before.ut, 5);
            Assert.Equal(300.0, after.ut, 5);
            Assert.Equal(0.0, (double)t, 4);
        }

        #endregion

        #region Edge cases — before/after timeline

        /// <summary>
        /// Catches: crash or wrong result when targetUT is before the first recorded point.
        /// Should return false with before set to the first point.
        /// </summary>
        [Fact]
        public void BeforeFirstPoint_ReturnsFalse_BeforeSetToFirst()
        {
            var points = MakeTimeline(100, 200, 300);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 50.0,
                out var before, out var after, out float t);

            Assert.False(result);
            Assert.Equal(100.0, before.ut, 5);
            Assert.Equal(0f, t);
        }

        /// <summary>
        /// Catches: crash when targetUT is after the last recorded point.
        /// FindWaypointIndex returns Count-2, so interpolation clamps at end.
        /// </summary>
        [Fact]
        public void AfterLastPoint_ReturnsTrueWithClampedT()
        {
            var points = MakeTimeline(100, 200, 300);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 500.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(200.0, before.ut, 5);
            Assert.Equal(300.0, after.ut, 5);
            // t should be clamped to 1.0
            Assert.Equal(1.0, (double)t, 4);
        }

        #endregion

        #region Edge cases — degenerate point lists

        /// <summary>
        /// Catches: IndexOutOfRangeException or NullReferenceException on empty list.
        /// </summary>
        [Fact]
        public void EmptyList_ReturnsFalse()
        {
            var points = new List<TrajectoryPoint>();
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 100.0,
                out var before, out var after, out float t);

            Assert.False(result);
        }

        /// <summary>
        /// Catches: IndexOutOfRangeException on null list.
        /// </summary>
        [Fact]
        public void NullList_ReturnsFalse()
        {
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                null, ref cached, 100.0,
                out var before, out var after, out float t);

            Assert.False(result);
        }

        /// <summary>
        /// Catches: single-point list cannot form a segment pair — should return false
        /// with before set to the single point (for fallback positioning).
        /// </summary>
        [Fact]
        public void SinglePoint_ReturnsFalse_BeforeSetToOnlyPoint()
        {
            var points = MakeTimeline(100);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 100.0,
                out var before, out var after, out float t);

            Assert.False(result);
            // The single point is returned via before for caller fallback
            Assert.Equal(100.0, before.ut, 5);
        }

        /// <summary>
        /// Catches: division by zero when two adjacent points have identical UT
        /// (degenerate segment with zero duration).
        /// </summary>
        [Fact]
        public void DegenerateSegment_IdenticalUT_TIsZero()
        {
            // Two points with same UT + a third to make a valid timeline
            var points = new List<TrajectoryPoint>
            {
                MakePoint(100),
                MakePoint(100), // same UT — degenerate
                MakePoint(200)
            };
            int cached = -1;

            // Target at 100 lands in the degenerate segment
            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 100.0,
                out var before, out var after, out float t);

            Assert.True(result);
            // Degenerate segment (duration <= 0.0001) should return t=0, not NaN
            Assert.Equal(0f, t);
            Assert.False(float.IsNaN(t), "t should not be NaN for degenerate segment");
        }

        #endregion

        #region Cached index optimization

        /// <summary>
        /// Catches: cachedIndex not updated after lookup, causing repeated binary search
        /// on sequential playback.
        /// </summary>
        [Fact]
        public void CachedIndex_SequentialPlayback_UpdatesCache()
        {
            var points = MakeTimeline(100, 200, 300, 400, 500);
            int cached = -1;

            // First call — binary search
            TrajectoryMath.InterpolatePoints(
                points, ref cached, 150.0,
                out _, out _, out _);

            // cached should now be 0 (segment [100,200])
            Assert.Equal(0, cached);

            // Second call — sequential advance to next segment
            TrajectoryMath.InterpolatePoints(
                points, ref cached, 250.0,
                out var before, out var after, out _);

            // Should find segment [200,300] and update cache to 1
            Assert.Equal(200.0, before.ut, 5);
            Assert.Equal(300.0, after.ut, 5);
            Assert.Equal(1, cached);
        }

        /// <summary>
        /// Catches: stale cachedIndex from previous playback session causes wrong segment
        /// when jumping backward in time.
        /// </summary>
        [Fact]
        public void CachedIndex_JumpBackward_StillFindsCorrectSegment()
        {
            var points = MakeTimeline(100, 200, 300, 400, 500);
            int cached = 3; // Stale: pointing at segment [400,500]

            // Jump back to segment [100,200]
            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 150.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(100.0, before.ut, 5);
            Assert.Equal(200.0, after.ut, 5);
        }

        /// <summary>
        /// Catches: cachedIndex out of range (e.g., after recording was trimmed)
        /// should not crash — falls back to binary search.
        /// </summary>
        [Fact]
        public void CachedIndex_OutOfRange_FallsBackGracefully()
        {
            var points = MakeTimeline(100, 200, 300);
            int cached = 99; // Way out of range

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 150.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(100.0, before.ut, 5);
            Assert.Equal(200.0, after.ut, 5);
        }

        #endregion

        #region Interpolation parameter precision

        /// <summary>
        /// Catches: floating-point precision loss on very large UT values (e.g., year 10 in KSP).
        /// With double arithmetic, t should still be precise even at UT ~ 10 million.
        /// </summary>
        [Fact]
        public void LargeUT_MaintainsPrecision()
        {
            double baseUT = 10_000_000.0;
            var points = MakeTimeline(baseUT, baseUT + 100.0);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, baseUT + 50.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(0.5, (double)t, 3); // float precision is lower, allow 3 decimals
        }

        /// <summary>
        /// Catches: very small segment duration (e.g., 0.01s physics frames) treated as degenerate
        /// when it should be a valid interpolation.
        /// </summary>
        [Fact]
        public void SmallButValidSegment_Interpolates()
        {
            // Duration = 0.02s, above the 0.0001 threshold
            var points = MakeTimeline(100.0, 100.02);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 100.01,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal(0.5, (double)t, 2);
        }

        /// <summary>
        /// Catches: segment duration well below the degenerate threshold (0.0001)
        /// not being handled — should return t=0, not NaN from division.
        /// </summary>
        [Fact]
        public void TinySegmentBelowThreshold_TreatedAsDegenerate()
        {
            // Duration = 0.00001, clearly below the 0.0001 threshold
            var points = new List<TrajectoryPoint>
            {
                MakePoint(100.0),
                MakePoint(100.00001),
                MakePoint(200.0) // need a third point so the first two form a segment
            };
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 100.000005,
                out var before, out var after, out float t);

            Assert.True(result);
            // Below threshold, degenerate path returns t=0
            Assert.Equal(0f, t);
            Assert.False(float.IsNaN(t), "t should not be NaN for degenerate segment");
        }

        #endregion

        #region Body name and data preservation

        /// <summary>
        /// Catches: interpolation returns default/empty TrajectoryPoint with lost body name,
        /// latitude, or other payload data.
        /// </summary>
        [Fact]
        public void InterpolatedPoints_PreserveBodyNameAndCoordinates()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100.0, latitude = -0.0972, longitude = -74.5575,
                    altitude = 77.3, bodyName = "Kerbin",
                    rotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                    velocity = new Vector3(10, 20, 30)
                },
                new TrajectoryPoint
                {
                    ut = 200.0, latitude = 5.0, longitude = -70.0,
                    altitude = 50000, bodyName = "Kerbin",
                    rotation = new Quaternion(0.4f, 0.5f, 0.6f, 0.7f),
                    velocity = new Vector3(100, 200, 300)
                }
            };
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 150.0,
                out var before, out var after, out float t);

            Assert.True(result);
            Assert.Equal("Kerbin", before.bodyName);
            Assert.Equal("Kerbin", after.bodyName);
            Assert.Equal(-0.0972, before.latitude, 4);
            Assert.Equal(5.0, after.latitude, 4);
            Assert.Equal(77.3, before.altitude, 1);
            Assert.Equal(50000.0, after.altitude, 1);
        }

        #endregion

        #region Two-point timeline (minimum valid)

        /// <summary>
        /// Catches: off-by-one with exactly two points (the minimum for interpolation).
        /// </summary>
        [Fact]
        public void TwoPoints_TargetAtStart_TIsZero()
        {
            var points = MakeTimeline(100, 200);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 100.0,
                out var before, out var after, out float t);

            // At exactly the start, FindWaypointIndex returns -1 (targetUT < points[0].ut is false,
            // but targetUT == points[0].ut falls through to the segment check)
            // Actually, 100 >= points[0].ut=100, so it enters the segment search
            // For two points, index 0 is valid: points[0].ut<=100 && points[1].ut>100 is false (200>100 true, 100<=100 true)
            // Wait: FindWaypointIndex checks `points[mid].ut <= targetUT && points[mid+1].ut > targetUT`
            // 100 <= 100 = true, 200 > 100 = true → returns 0
            Assert.True(result);
            Assert.Equal(100.0, before.ut, 5);
            Assert.Equal(200.0, after.ut, 5);
            Assert.Equal(0.0, (double)t, 4);
        }

        /// <summary>
        /// Catches: targetUT exactly at last point boundary with only two points.
        /// </summary>
        [Fact]
        public void TwoPoints_TargetAtEnd_TIsOne()
        {
            var points = MakeTimeline(100, 200);
            int cached = -1;

            bool result = TrajectoryMath.InterpolatePoints(
                points, ref cached, 200.0,
                out var before, out var after, out float t);

            // targetUT >= points[Count-1].ut → returns Count-2 = 0
            Assert.True(result);
            Assert.Equal(100.0, before.ut, 5);
            Assert.Equal(200.0, after.ut, 5);
            Assert.Equal(1.0, (double)t, 4);
        }

        #endregion

        #region BracketPointAtUT

        [Fact]
        public void BracketPointAtUT_EmptyList_ReturnsNull()
        {
            var points = new List<TrajectoryPoint>();
            int cached = -1;
            Assert.Null(TrajectoryMath.BracketPointAtUT(points, 100, ref cached));
        }

        [Fact]
        public void BracketPointAtUT_NullList_ReturnsNull()
        {
            int cached = -1;
            Assert.Null(TrajectoryMath.BracketPointAtUT(null, 100, ref cached));
        }

        [Fact]
        public void BracketPointAtUT_BeforeStart_ReturnsNull()
        {
            var points = new List<TrajectoryPoint>
            {
                MakePoint(100, alt: 500),
                MakePoint(200, alt: 1000)
            };
            int cached = -1;
            Assert.Null(TrajectoryMath.BracketPointAtUT(points, 50, ref cached));
        }

        [Fact]
        public void BracketPointAtUT_MidRange_ReturnsBracketPoint()
        {
            var points = new List<TrajectoryPoint>
            {
                MakePoint(100, alt: 500),
                MakePoint(200, alt: 1000),
                MakePoint(300, alt: 1500)
            };
            int cached = -1;
            var result = TrajectoryMath.BracketPointAtUT(points, 150, ref cached);
            Assert.NotNull(result);
            // Returns the lower bracket point (ut=100, alt=500)
            Assert.Equal(100.0, result.Value.ut);
            Assert.Equal(500.0, result.Value.altitude);
        }

        [Fact]
        public void BracketPointAtUT_ExactPoint_ReturnsThatPoint()
        {
            var points = new List<TrajectoryPoint>
            {
                MakePoint(100, alt: 500),
                MakePoint(200, alt: 1000),
                MakePoint(300, alt: 1500)
            };
            int cached = -1;
            var result = TrajectoryMath.BracketPointAtUT(points, 200, ref cached);
            Assert.NotNull(result);
            Assert.Equal(200.0, result.Value.ut);
            Assert.Equal(1000.0, result.Value.altitude);
        }

        [Fact]
        public void BracketPointAtUT_PastEnd_ReturnsLastPoint()
        {
            var points = new List<TrajectoryPoint>
            {
                MakePoint(100, alt: 500),
                MakePoint(200, alt: 1000)
            };
            int cached = -1;
            var result = TrajectoryMath.BracketPointAtUT(points, 999, ref cached);
            Assert.NotNull(result);
            // Past end: t >= 1, returns upper bracket (last point)
            Assert.Equal(200.0, result.Value.ut);
            Assert.Equal(1000.0, result.Value.altitude);
        }

        [Fact]
        public void BracketPointAtUT_SinglePoint_ReturnsNull()
        {
            // Single point → FindWaypointIndex returns -1 (needs at least 2 points)
            var points = new List<TrajectoryPoint>
            {
                MakePoint(100, alt: 500)
            };
            int cached = -1;
            Assert.Null(TrajectoryMath.BracketPointAtUT(points, 100, ref cached));
        }

        [Fact]
        public void BracketPointAtUT_PreservesBodyName()
        {
            var points = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100, altitude = 500, bodyName = "Mun", rotation = Quaternion.identity },
                new TrajectoryPoint { ut = 200, altitude = 1000, bodyName = "Mun", rotation = Quaternion.identity }
            };
            int cached = -1;
            var result = TrajectoryMath.BracketPointAtUT(points, 150, ref cached);
            Assert.NotNull(result);
            Assert.Equal("Mun", result.Value.bodyName);
        }

        #endregion
    }
}
