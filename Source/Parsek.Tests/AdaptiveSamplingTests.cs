using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class AdaptiveSamplingTests
    {
        // Default test thresholds. MinInterval = 0 disables the floor for legacy
        // tests so they exercise the velocity gates the same way they always have.
        // Tests that exercise the floor explicitly use a non-zero MinInterval and
        // override the constants locally.
        private const float MinInterval = 0f;
        private const float MaxInterval = 3.0f;
        private const float VelDirThreshold = 2.0f;
        private const float SpeedThreshold = 0.05f;

        [Fact]
        public void FirstPoint_AlwaysRecords()
        {
            var result = TrajectoryMath.ShouldRecordPoint(
                Vector3.zero, Vector3.zero,
                currentUT: 100, lastRecordedUT: -1,
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void WithinMaxInterval_NoChange_Skips()
        {
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 100.5, lastRecordedUT: 100,
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void BothVelocitiesNearZero_OnlyMaxIntervalTriggers()
        {
            // Sitting on pad, both ~0 — should skip (not enough time elapsed)
            var result = TrajectoryMath.ShouldRecordPoint(
                Vector3.zero, Vector3.zero,
                currentUT: 100.5, lastRecordedUT: 100,
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);

            // After max interval, should record
            var result2 = TrajectoryMath.ShouldRecordPoint(
                Vector3.zero, Vector3.zero,
                currentUT: 103.1, lastRecordedUT: 100,
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

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
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void BackwardTime_NoRecord()
        {
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 99, lastRecordedUT: 100,
                MinInterval, MaxInterval, VelDirThreshold, SpeedThreshold);

            // Negative interval: 99-100 = -1, not >= 3
            Assert.False(result);
        }

        // --- Min interval floor tests (bug #256) ---

        [Fact]
        public void MinInterval_BlocksWithinFloorWindow()
        {
            // 10 -> 11 m/s (10% speed change) would normally fire the speed gate,
            // but the elapsed (0.1s) is below the 0.2s floor.
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(11, 0, 0);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.1, lastRecordedUT: 100,
                minInterval: 0.2f, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.False(result);
        }

        [Fact]
        public void MinInterval_AllowsAfterFloor()
        {
            // Same velocity change, elapsed (0.25s) past the floor — velocity gate fires.
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(11, 0, 0);

            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.25, lastRecordedUT: 100,
                minInterval: 0.2f, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void MinInterval_DoesNotBlockFirstPoint()
        {
            // First point ignores all thresholds, including the floor.
            var result = TrajectoryMath.ShouldRecordPoint(
                Vector3.zero, Vector3.zero,
                currentUT: 100, lastRecordedUT: -1,
                minInterval: 0.2f, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void MinInterval_DoesNotBlockMaxIntervalBackstop()
        {
            // Degenerate config: minInterval > maxInterval. Max-interval backstop must
            // still fire so the recorder doesn't starve. Same velocity, elapsed > max.
            var vel = new Vector3(10, 0, 0);
            var result = TrajectoryMath.ShouldRecordPoint(
                vel, vel,
                currentUT: 105, lastRecordedUT: 100,
                minInterval: 10f, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void MinInterval_EvaWalkingPattern_CapsAtFiveHz()
        {
            // Simulate an EVA walking on the surface: every physics frame (50 Hz)
            // the velocity wiggles enough to defeat the velocity gates without the
            // floor (~1 m/s walking with 0.05 m/s perpendicular noise = ~2.86°
            // direction change). With the 0.2s floor, max commits should be ~5/sec.
            double lastRecordedUT = -1;
            Vector3 lastRecordedVelocity = Vector3.zero;
            int commits = 0;

            // 50 frames over 1 second of UT (50 Hz physics)
            for (int i = 0; i < 50; i++)
            {
                double ut = i * 0.02; // 50 Hz
                // Walking velocity ~1 m/s with per-frame perpendicular noise
                float noise = (i % 2 == 0) ? 0.05f : -0.05f;
                var vel = new Vector3(1f, 0, noise);

                bool record = TrajectoryMath.ShouldRecordPoint(
                    vel, lastRecordedVelocity, ut, lastRecordedUT,
                    minInterval: 0.2f, MaxInterval, VelDirThreshold, SpeedThreshold);

                if (record)
                {
                    commits++;
                    lastRecordedUT = ut;
                    lastRecordedVelocity = vel;
                }
            }

            // 1 second of recording with 0.2s floor → 5 commits max (plus the first-point
            // exception → up to 6). Without the floor, this loop would commit ~50 times.
            Assert.True(commits <= 6, $"Expected ≤6 commits with 0.2s floor, got {commits}");
            Assert.True(commits >= 4, $"Expected ≥4 commits at floor cadence, got {commits}");
        }

        [Fact]
        public void MinInterval_HighSpeedAscent_NoLossVsLegacy()
        {
            // Atmospheric ascent: 1000 → 1100 m/s with steady direction. Velocity gates
            // dominate well above the floor. Adding the floor must not reduce commit count
            // below the legacy (no-floor) baseline because the velocity gates fire at >5 Hz.
            // 50 frames over 1 second at 50 Hz physics.

            // Run with no floor
            int commitsLegacy = SimulateAscent(minIntervalForRun: 0f);

            // Run with 0.2s floor
            int commitsWithFloor = SimulateAscent(minIntervalForRun: 0.2f);

            // The floor must not cap below the legacy commit count for normal ascents.
            // (In practice both should be around the same value because acceleration is
            //  bounded and the gates fire at intervals > 0.2s.)
            Assert.True(commitsWithFloor >= commitsLegacy - 1,
                $"Floor must not over-restrict ascent sampling: legacy={commitsLegacy}, floor={commitsWithFloor}");
        }

        private static int SimulateAscent(float minIntervalForRun)
        {
            double lastRecordedUT = -1;
            Vector3 lastRecordedVelocity = Vector3.zero;
            int commits = 0;
            for (int i = 0; i < 50; i++)
            {
                double ut = i * 0.02;
                // Linear acceleration 1000→1100 m/s over 1 second
                float speed = 1000f + (i / 50f) * 100f;
                var vel = new Vector3(speed, 0, 0);

                bool record = TrajectoryMath.ShouldRecordPoint(
                    vel, lastRecordedVelocity, ut, lastRecordedUT,
                    minIntervalForRun, MaxInterval, VelDirThreshold, SpeedThreshold);

                if (record)
                {
                    commits++;
                    lastRecordedUT = ut;
                    lastRecordedVelocity = vel;
                }
            }
            return commits;
        }

        [Fact]
        public void MinInterval_ZeroPreservesLegacyBehavior()
        {
            // Regression guard: minInterval=0 must produce identical results to the
            // pre-fix version of ShouldRecordPoint. This is what all existing tests
            // assert via the MinInterval=0 const, but assert it explicitly here too.
            var last = new Vector3(10, 0, 0);
            var current = new Vector3(11, 0, 0); // 10% speed change

            // Even at 0.01s elapsed (well inside any floor), should record because
            // the velocity gate fires and the floor is disabled.
            var result = TrajectoryMath.ShouldRecordPoint(
                current, last,
                currentUT: 100.01, lastRecordedUT: 100,
                minInterval: 0f, MaxInterval, VelDirThreshold, SpeedThreshold);

            Assert.True(result);
        }

        [Fact]
        public void AttitudeSampling_AboveThreshold_Records()
        {
            bool result = FlightRecorder.ShouldRecordAttitudePoint(
                TrajectoryMath.PureAngleAxis(2f, Vector3.up),
                Quaternion.identity,
                currentUT: 100.25,
                lastRecordedUT: 100,
                hasLastWorldRotation: true,
                minInterval: 0.2f,
                rotationThresholdDegrees: 1f);

            Assert.True(result);
        }

        [Fact]
        public void AttitudeSampling_BelowThreshold_Skips()
        {
            bool result = FlightRecorder.ShouldRecordAttitudePoint(
                TrajectoryMath.PureAngleAxis(0.5f, Vector3.up),
                Quaternion.identity,
                currentUT: 100.25,
                lastRecordedUT: 100,
                hasLastWorldRotation: true,
                minInterval: 0.2f,
                rotationThresholdDegrees: 1f);

            Assert.False(result);
        }

        [Fact]
        public void AttitudeSampling_SignEquivalentQuaternion_DoesNotFalseTrigger()
        {
            Quaternion q = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f).normalized;
            Quaternion negQ = new Quaternion(-q.x, -q.y, -q.z, -q.w);

            bool result = FlightRecorder.ShouldRecordAttitudePoint(
                negQ,
                q,
                currentUT: 100.25,
                lastRecordedUT: 100,
                hasLastWorldRotation: true,
                minInterval: 0.2f,
                rotationThresholdDegrees: 1f);

            Assert.False(result);
        }

        // --- Seed-skip guard tests ---

        [Fact]
        public void SeedSkip_InactiveSection_DoesNotSkip()
        {
            bool result = FlightRecorder.ShouldSkipSeedDueToRelativeSection(
                trackSectionActive: false,
                sectionReferenceFrame: ReferenceFrame.Relative);

            Assert.False(result);
        }

        [Fact]
        public void SeedSkip_AbsoluteSection_DoesNotSkip()
        {
            bool result = FlightRecorder.ShouldSkipSeedDueToRelativeSection(
                trackSectionActive: true,
                sectionReferenceFrame: ReferenceFrame.Absolute);

            Assert.False(result);
        }

        [Fact]
        public void SeedSkip_ActiveRelativeSection_Skips()
        {
            bool result = FlightRecorder.ShouldSkipSeedDueToRelativeSection(
                trackSectionActive: true,
                sectionReferenceFrame: ReferenceFrame.Relative);

            Assert.True(result);
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
