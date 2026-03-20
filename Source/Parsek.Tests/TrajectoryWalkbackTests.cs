using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for trajectory walkback methods in <see cref="SpawnCollisionDetector"/>:
    /// ShouldTriggerWalkback, WalkbackAlongTrajectory, SimplePointToWorldPos.
    /// These methods resolve immovable vessel spawn deadlocks by walking backward
    /// along a recorded trajectory to find the latest non-overlapping position.
    /// </summary>
    [Collection("Sequential")]
    public class TrajectoryWalkbackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TrajectoryWalkbackTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  Helpers
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Build a list of trajectory points along a straight line of latitudes,
        /// with uniform time spacing. All points share the same longitude, altitude,
        /// and body name.
        /// </summary>
        private static List<TrajectoryPoint> MakeLinearTrajectory(
            int count, double startLat, double endLat,
            double lon, double alt, double startUT, double endUT)
        {
            var points = new List<TrajectoryPoint>(count);
            for (int i = 0; i < count; i++)
            {
                double t = count > 1 ? (double)i / (count - 1) : 0.0;
                points.Add(new TrajectoryPoint
                {
                    ut = startUT + t * (endUT - startUT),
                    latitude = startLat + t * (endLat - startLat),
                    longitude = lon,
                    altitude = alt,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero,
                });
            }
            return points;
        }

        /// <summary>Kerbin radius for test coordinate conversions.</summary>
        private const double KerbinRadius = 600000.0;

        /// <summary>
        /// Wraps SimplePointToWorldPos with a fixed body radius for use as
        /// the pointToWorldPos callback.
        /// </summary>
        private static Vector3d TestPointToWorldPos(TrajectoryPoint pt)
        {
            return SpawnCollisionDetector.SimplePointToWorldPos(pt, KerbinRadius);
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldTriggerWalkback
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldTriggerWalkback_TimeoutReached_BlockerStationary_True()
        {
            // 6s elapsed, blocker moved 0.5m (< 1m threshold) → true
            bool result = SpawnCollisionDetector.ShouldTriggerWalkback(
                blockedSinceUT: 100.0,
                currentUT: 106.0,
                timeoutSeconds: 5.0,
                blockerDistanceChangeMeters: 0.5f,
                movementThreshold: 1.0f);

            Assert.True(result);
        }

        [Fact]
        public void ShouldTriggerWalkback_TimeoutNotReached_False()
        {
            // 3s elapsed (< 5s timeout) → false regardless of movement
            bool result = SpawnCollisionDetector.ShouldTriggerWalkback(
                blockedSinceUT: 100.0,
                currentUT: 103.0,
                timeoutSeconds: 5.0,
                blockerDistanceChangeMeters: 0.0f,
                movementThreshold: 1.0f);

            Assert.False(result);
        }

        [Fact]
        public void ShouldTriggerWalkback_BlockerMoved_False()
        {
            // 6s elapsed, but blocker moved 50m (>> 1m threshold) → false
            bool result = SpawnCollisionDetector.ShouldTriggerWalkback(
                blockedSinceUT: 100.0,
                currentUT: 106.0,
                timeoutSeconds: 5.0,
                blockerDistanceChangeMeters: 50.0f,
                movementThreshold: 1.0f);

            Assert.False(result);
        }

        // ────────────────────────────────────────────────────────────
        //  WalkbackAlongTrajectory
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void Walkback_FindsValidPosition_3FramesBack()
        {
            // 10-point trajectory. Blocker covers last 3 points (indices 7,8,9).
            // Point [6] is clear → returns 6.
            var points = MakeLinearTrajectory(10, 0.0, 9.0, 0.0, 100.0, 1000.0, 1009.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            // Compute world positions for blocked points to define the overlap zone
            Vector3d pos7 = TestPointToWorldPos(points[7]);
            Vector3d pos8 = TestPointToWorldPos(points[8]);
            Vector3d pos9 = TestPointToWorldPos(points[9]);
            var blockedPositions = new HashSet<int> { 7, 8, 9 };

            // Track which index we're checking by comparing world positions
            int callIndex = points.Count - 1;
            Func<Vector3d, bool> isOverlapping = pos =>
            {
                // Walk from end toward start: first call is index 9, then 8, etc.
                bool result = blockedPositions.Contains(callIndex);
                callIndex--;
                return result;
            };

            int index = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, isOverlapping);

            Assert.Equal(6, index);
        }

        [Fact]
        public void Walkback_EntireTrajectoryOverlaps_ReturnsNegative()
        {
            var points = MakeLinearTrajectory(5, 0.0, 4.0, 0.0, 100.0, 1000.0, 1004.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int index = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, pos => true);

            Assert.Equal(-1, index);
        }

        [Fact]
        public void Walkback_FirstPointClear_ReturnsLastIndex()
        {
            // Only the last point (index 4) overlaps, point [3] is clear → returns 3
            var points = MakeLinearTrajectory(5, 0.0, 4.0, 0.0, 100.0, 1000.0, 1004.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int callIndex = points.Count - 1;
            Func<Vector3d, bool> isOverlapping = pos =>
            {
                bool result = callIndex == points.Count - 1; // only last point overlaps
                callIndex--;
                return result;
            };

            int index = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, isOverlapping);

            Assert.Equal(3, index);
        }

        [Fact]
        public void Walkback_EmptyTrajectory_ReturnsNegative()
        {
            var points = new List<TrajectoryPoint>();
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int index = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, pos => false);

            Assert.Equal(-1, index);
        }

        [Fact]
        public void Walkback_NullTrajectory_ReturnsNegative()
        {
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int index = SpawnCollisionDetector.WalkbackAlongTrajectory(
                null, spawnBounds, 5f, TestPointToWorldPos, pos => false);

            Assert.Equal(-1, index);
        }

        [Fact]
        public void Walkback_SinglePoint_Clear_ReturnsZero()
        {
            var points = MakeLinearTrajectory(1, 0.0, 0.0, 0.0, 100.0, 1000.0, 1000.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int index = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, pos => false);

            Assert.Equal(0, index);
        }

        [Fact]
        public void Walkback_SinglePoint_Blocked_ReturnsNegative()
        {
            var points = MakeLinearTrajectory(1, 0.0, 0.0, 0.0, 100.0, 1000.0, 1000.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            int index = SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, pos => true);

            Assert.Equal(-1, index);
        }

        // ────────────────────────────────────────────────────────────
        //  SimplePointToWorldPos
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void SimplePointToWorldPos_KnownPosition()
        {
            // Equatorial point at lat=0, lon=0, alt=100000 on body radius=600000
            // Expected: x = (600000+100000)*cos(0)*cos(0) = 700000
            //           y = (600000+100000)*sin(0) = 0
            //           z = (600000+100000)*cos(0)*sin(0) = 0
            var pt = new TrajectoryPoint
            {
                latitude = 0.0,
                longitude = 0.0,
                altitude = 100000.0,
                bodyName = "Kerbin"
            };

            Vector3d result = SpawnCollisionDetector.SimplePointToWorldPos(pt, 600000.0);

            Assert.Equal(700000.0, result.x, 1);
            Assert.Equal(0.0, result.y, 1);
            Assert.Equal(0.0, result.z, 1);
        }

        // ────────────────────────────────────────────────────────────
        //  Log assertion tests
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldTriggerWalkback_LogsDecision()
        {
            logLines.Clear();

            SpawnCollisionDetector.ShouldTriggerWalkback(
                100.0, 106.0, 5.0, 0.5f, 1.0f);

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("ShouldTriggerWalkback"));
        }

        [Fact]
        public void Walkback_LogsPerPointCheck()
        {
            logLines.Clear();

            var points = MakeLinearTrajectory(3, 0.0, 2.0, 0.0, 100.0, 1000.0, 1002.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, pos => false);

            // Should log for each point checked (only index 2, since it finds it clear immediately)
            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("index=2"));
        }

        [Fact]
        public void Walkback_EmptyTrajectory_LogsMessage()
        {
            logLines.Clear();

            SpawnCollisionDetector.WalkbackAlongTrajectory(
                new List<TrajectoryPoint>(),
                new Bounds(Vector3.zero, new Vector3(2, 2, 2)),
                5f, TestPointToWorldPos, pos => false);

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("null or empty trajectory"));
        }

        [Fact]
        public void Walkback_EntireOverlap_LogsWarning()
        {
            logLines.Clear();

            var points = MakeLinearTrajectory(3, 0.0, 2.0, 0.0, 100.0, 1000.0, 1002.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, pos => true);

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("entire trajectory overlaps"));
        }

        [Fact]
        public void Walkback_FindsValid_LogsFoundMessage()
        {
            logLines.Clear();

            var points = MakeLinearTrajectory(3, 0.0, 2.0, 0.0, 100.0, 1000.0, 1002.0);
            Bounds spawnBounds = new Bounds(Vector3.zero, new Vector3(2, 2, 2));

            // Last point blocked, second-to-last is clear
            int callIndex = 2;
            SpawnCollisionDetector.WalkbackAlongTrajectory(
                points, spawnBounds, 5f, TestPointToWorldPos, pos =>
                {
                    bool result = callIndex == 2;
                    callIndex--;
                    return result;
                });

            Assert.Contains(logLines, l =>
                l.Contains("[SpawnCollision]") && l.Contains("found valid position"));
        }
    }
}
