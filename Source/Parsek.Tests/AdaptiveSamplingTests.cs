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
    }
}
