using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class AdaptiveSamplingTests
    {
        private const float MaxInterval = 3.0f;
        private const float VelDirThreshold = 2.0f;
        private const float SpeedThreshold = 0.05f;

        [Fact]
        public void FirstPoint_AlwaysRecords()
        {
            var result = TrajectoryMath.ShouldRecordPoint(
                Vector3.zero, Vector3.zero,
                currentUT: 100, lastRecordedUT: -1,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void MaxIntervalExceeded_Records()
        {
            // Same velocity, but 3+ seconds elapsed
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 103.1, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void WithinMaxInterval_NoChange_Skips()
        {
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 100.5, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void VelocityDirectionChange_AboveThreshold_Records()
        {
            // 10 m/s forward, then angled 5 degrees — above 2deg threshold
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(10, 0, Mathf.Tan(5f * Mathf.Deg2Rad) * 10f);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void VelocityDirectionChange_BelowThreshold_Skips()
        {
            // 10 m/s forward, then angled 1 degree — below 2deg threshold
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(10, 0, Mathf.Tan(1f * Mathf.Deg2Rad) * 10f);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void SpeedIncrease_AboveThreshold_Records()
        {
            // 10 -> 11 m/s = 10% change, above 5% threshold
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(11, 0, 0);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void SpeedDecrease_AboveThreshold_Records()
        {
            // 10 -> 9 m/s = 10% change, above 5% threshold
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(9, 0, 0);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void SpeedChange_BelowThreshold_Skips()
        {
            // 10 -> 10.3 m/s = 3% change, below 5% threshold
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(10.3f, 0, 0);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void BothVelocitiesNearZero_OnlyMaxIntervalTriggers()
        {
            // Sitting on pad, both ~0 — should skip (not enough time elapsed)
            var result = TrajectoryMath.ShouldRecordPoint(
                Vector3.zero, Vector3.zero,
                currentUT: 100.5, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);

            // After max interval, should record
            var result2 = TrajectoryMath.ShouldRecordPoint(
                Vector3.zero, Vector3.zero,
                currentUT: 103.1, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result2);
        }

        [Fact]
        public void Liftoff_ZeroToNonzero_Records()
        {
            // Last velocity ~0, current has speed — should trigger speed change
            var last = Vector3.zero;
            var current = new Vector3(0, 5, 0);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void MaxIntervalExact_Records()
        {
            // Exactly at the max interval boundary
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 103, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void SmallSpeedNearFloor_NoFalsePositive()
        {
            // Both velocities very small (0.05 m/s) — delta is tiny relative to floor
            var last = new Vector3(0.05f, 0, 0);
            var current = new Vector3(0.06f, 0, 0);

            // delta = 0.01, reference = max(0.05, 0.1) = 0.1, ratio = 0.1 = 10% > 5%
            // This actually records because even small absolute changes are significant
            // at near-zero speeds (floor kicks in at 0.1)
            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            // 0.01 / 0.1 = 0.1 > 0.05, so it records
            Assert.True(result);
        }

        [Fact]
        public void HighSpeed_TinyAbsoluteChange_Skips()
        {
            // 100 m/s -> 100.5 m/s = 0.5% change, below 5%
            var last = new Vector3(100, 0, 0);
            var current = new Vector3(100.5f, 0, 0);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.2, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void NaNVelocity_DoesNotCrash()
        {
            var nan = new Vector3(float.NaN, float.NaN, float.NaN);
            var normal = new Vector3(10, 0, 0);

            // NaN magnitude is NaN, NaN > threshold is false, so no direction/speed trigger
            var result = TrajectoryMath.ShouldRecordPoint(
                nan, normal,
                currentUT: 100.5, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            // Should not throw — result depends on whether NaN comparisons trigger thresholds
            // NaN comparisons return false, so neither direction nor speed triggers fire
            Assert.False(result);
        }

        [Fact]
        public void InfinityVelocity_DoesNotCrash()
        {
            var inf = new Vector3(float.PositiveInfinity, 0, 0);
            var normal = new Vector3(10, 0, 0);

            // Infinity speed - delta is Infinity, Infinity / 10 is Infinity > threshold
            var result = TrajectoryMath.ShouldRecordPoint(
                inf, normal,
                currentUT: 100.5, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            // Infinity speed change should trigger recording
            Assert.True(result);
        }

        [Fact]
        public void ZeroTimeDelta_NoRecord()
        {
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 100, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void BackwardTime_NoRecord()
        {
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 99, lastRecordedUT: 100,
                MaxInterval, VelDirThreshold, SpeedThreshold);

            // Negative interval: 99-100 = -1, not >= 3
            Assert.False(result);
        }

        // --- FindFirstMovingPoint tests ---

        private static List<TrajectoryPoint> MakePoints(params (double alt, float speed)[] specs)
        {
            var points = new List<TrajectoryPoint>();
            for (int i = 0; i < specs.Length; i++)
            {
                points.Add(new TrajectoryPoint
                {
                    ut = 100 + i,
                    altitude = specs[i].alt,
                    velocity = new Vector3(specs[i].speed, 0, 0)
                });
            }
            return points;
        }

        [Fact]
        public void FindFirstMoving_AllStationary_ReturnsZero()
        {
            var points = MakePoints((78, 0.3f), (78, 0.5f), (78, 0.2f));
            Assert.Equal(0, TrajectoryMath.FindFirstMovingPoint(points));
        }

        [Fact]
        public void FindFirstMoving_MovingFromStart_ReturnsZero()
        {
            var points = MakePoints((78, 10f), (80, 15f));
            Assert.Equal(0, TrajectoryMath.FindFirstMovingPoint(points));
        }

        [Fact]
        public void FindFirstMoving_AltitudeChange_ReturnsCorrectIndex()
        {
            // Pad at alt 78, then lifts off at index 3
            var points = MakePoints((78, 0.3f), (78, 0.5f), (78.2, 0.4f), (79.5, 2f), (85, 10f));
            Assert.Equal(3, TrajectoryMath.FindFirstMovingPoint(points));
        }

        [Fact]
        public void FindFirstMoving_SpeedThreshold_ReturnsCorrectIndex()
        {
            // Runway: same altitude, picks up speed at index 2
            var points = MakePoints((10, 0.3f), (10, 2f), (10, 6f), (10, 15f));
            Assert.Equal(2, TrajectoryMath.FindFirstMovingPoint(points));
        }

        [Fact]
        public void FindFirstMoving_NullOrShort_ReturnsZero()
        {
            Assert.Equal(0, TrajectoryMath.FindFirstMovingPoint(null));
            Assert.Equal(0, TrajectoryMath.FindFirstMovingPoint(new List<TrajectoryPoint>()));
            Assert.Equal(0, TrajectoryMath.FindFirstMovingPoint(MakePoints((78, 0.1f))));
        }
    }
}
