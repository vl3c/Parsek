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
        public void HighFidelityWindow_UsesConfiguredMinIntervalBackstop()
        {
            bool active = FlightRecorder.IsHighFidelitySamplingActive(
                currentUT: 101.0,
                highFidelityUntilUT: 105.0);
            float min = FlightRecorder.ResolveEffectiveMinSampleInterval(
                active,
                configuredMin: 0.2f);
            float max = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                active,
                configuredMax: 3.0f,
                configuredMin: 0.2f);

            bool insideConfiguredInterval = TrajectoryMath.ShouldRecordPoint(
                new Vector3(10, 0, 0),
                new Vector3(10, 0, 0),
                currentUT: 100.03,
                lastRecordedUT: 100.0,
                min,
                max,
                VelDirThreshold,
                SpeedThreshold);
            bool atConfiguredInterval = TrajectoryMath.ShouldRecordPoint(
                new Vector3(10, 0, 0),
                new Vector3(10, 0, 0),
                currentUT: 100.21,
                lastRecordedUT: 100.0,
                min,
                max,
                VelDirThreshold,
                SpeedThreshold);

            Assert.True(active);
            Assert.Equal(0.2f, min);
            Assert.Equal(0.2f, max);
            Assert.False(insideConfiguredInterval);
            Assert.True(atConfiguredInterval);
        }

        [Fact]
        public void HighFidelityWindow_ExpiresAfterUntilUT()
        {
            bool active = FlightRecorder.IsHighFidelitySamplingActive(
                currentUT: 105.01,
                highFidelityUntilUT: 105.0);
            float min = FlightRecorder.ResolveEffectiveMinSampleInterval(
                active,
                configuredMin: 0.2f);
            float max = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                active,
                configuredMax: 3.0f,
                configuredMin: 0.2f);

            Assert.False(active);
            Assert.Equal(0.2f, min);
            Assert.Equal(3.0f, max);
        }

        [Fact]
        public void HighFidelityWindow_DerivesFromConfiguredMaxInterval()
        {
            Assert.Equal(2.5, FlightRecorder.ResolveHighFidelitySamplingWindowSeconds(2.5f), 6);
            Assert.Equal(0.0, FlightRecorder.ResolveHighFidelitySamplingWindowSeconds(float.NaN), 6);
        }

        [Fact]
        public void HighFidelityProximity_ActivatesInsideRange()
        {
            Assert.True(FlightRecorder.IsHighFidelityProximityActive(199.9));
            Assert.True(FlightRecorder.IsHighFidelitySamplingActive(
                currentUT: 150.0,
                highFidelityUntilUT: 100.0,
                proximityDistanceMeters: 42.0));
        }

        [Fact]
        public void HighFidelityProximity_SelectsNearestFiniteSource()
        {
            Assert.Equal(25.0, FlightRecorder.SelectNearestHighFidelityProximityMeters(50.0, 25.0), 6);
            Assert.Equal(50.0, FlightRecorder.SelectNearestHighFidelityProximityMeters(50.0, double.NaN), 6);
            Assert.Equal(25.0, FlightRecorder.SelectNearestHighFidelityProximityMeters(double.NaN, 25.0), 6);
            Assert.True(double.IsNaN(FlightRecorder.SelectNearestHighFidelityProximityMeters(double.NaN, double.PositiveInfinity)));
        }

        [Fact]
        public void HighFidelityProximity_RejectsInvalidOrOutsideRange()
        {
            Assert.Equal(250.0, FlightRecorder.HighFidelityProximityRangeMeters);
            Assert.False(FlightRecorder.IsHighFidelityProximityActive(250.1));
            Assert.False(FlightRecorder.IsHighFidelityProximityActive(double.NaN));
            Assert.False(FlightRecorder.IsHighFidelitySamplingActive(
                currentUT: 150.0,
                highFidelityUntilUT: 100.0,
                proximityDistanceMeters: 250.1));
        }

        [Fact]
        public void ActiveReFlyTreeSampling_FullCadenceInside250Meters()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-root",
            };

            ProximitySamplingTier cadence =
                FlightRecorder.ResolveActiveReFlyTreeSamplingCadence(
                    activeRecordingId: "rec-optimizer-successor",
                    activeTreeId: "tree-1",
                    marker,
                    currentUT: 650.0,
                    proximityDistanceMeters: 250.0,
                    out string reason);
            float min = FlightRecorder.ResolveEffectiveMinSampleInterval(
                cadence,
                ProximitySamplingTier.None,
                highFidelityActive: false,
                configuredMin: 0.2f,
                configuredMax: 3.0f);
            float max = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                cadence,
                ProximitySamplingTier.None,
                highFidelityActive: false,
                configuredMax: 3.0f,
                configuredMin: 0.2f);

            Assert.Equal(ProximitySamplingTier.Full, cadence);
            Assert.Equal("active-refly-tree-full", reason);
            Assert.Equal(0.2f, min);
            Assert.Equal(0.2f, max);
        }

        [Fact]
        public void ActiveReFlyTreeSampling_HalfCadenceBetween250And500Meters()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-root",
            };

            ProximitySamplingTier cadence =
                FlightRecorder.ResolveActiveReFlyTreeSamplingCadence(
                    activeRecordingId: "rec-optimizer-successor",
                    activeTreeId: "tree-1",
                    marker,
                    currentUT: 650.0,
                    proximityDistanceMeters: 500.0,
                    out string reason);
            float min = FlightRecorder.ResolveEffectiveMinSampleInterval(
                cadence,
                ProximitySamplingTier.None,
                highFidelityActive: false,
                configuredMin: 0.2f,
                configuredMax: 3.0f);
            float max = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                cadence,
                ProximitySamplingTier.None,
                highFidelityActive: false,
                configuredMax: 3.0f,
                configuredMin: 0.2f);

            Assert.Equal(ProximitySamplingTier.Half, cadence);
            Assert.Equal("active-refly-tree-half", reason);
            Assert.Equal(0.4f, min);
            Assert.Equal(0.4f, max);
        }

        [Fact]
        public void ActiveReFlyTreeSampling_RejectsPast500Meters()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = "tree-1",
                ActiveReFlyRecordingId = "rec-root",
            };

            ProximitySamplingTier cadence =
                FlightRecorder.ResolveActiveReFlyTreeSamplingCadence(
                    activeRecordingId: "rec-optimizer-successor",
                    activeTreeId: "tree-1",
                    marker,
                    currentUT: 650.0,
                    proximityDistanceMeters: 500.1,
                    out string reason);
            float min = FlightRecorder.ResolveEffectiveMinSampleInterval(
                cadence,
                ProximitySamplingTier.None,
                highFidelityActive: false,
                configuredMin: 0.2f,
                configuredMax: 3.0f);
            float max = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                cadence,
                ProximitySamplingTier.None,
                highFidelityActive: false,
                configuredMax: 3.0f,
                configuredMin: 0.2f);

            Assert.Equal(ProximitySamplingTier.None, cadence);
            Assert.Equal("proximity-out-of-range", reason);
            Assert.Equal(0.2f, min);
            Assert.Equal(3.0f, max);
        }

        [Fact]
        public void ActiveReFlyTreeSampling_RejectsNormalRecordingWithoutMarker()
        {
            ProximitySamplingTier cadence =
                FlightRecorder.ResolveActiveReFlyTreeSamplingCadence(
                    activeRecordingId: "rec-normal",
                    activeTreeId: "tree-1",
                    marker: null,
                    currentUT: 650.0,
                    proximityDistanceMeters: 100.0,
                    out string reason);

            Assert.Equal(ProximitySamplingTier.None, cadence);
            Assert.Equal("marker-missing", reason);
        }

        [Fact]
        public void ActiveReFlyTreeSampling_RejectsDifferentTree()
        {
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess-1",
                TreeId = "tree-refly",
                ActiveReFlyRecordingId = "rec-root",
            };

            ProximitySamplingTier cadence =
                FlightRecorder.ResolveActiveReFlyTreeSamplingCadence(
                    activeRecordingId: "rec-other",
                    activeTreeId: "tree-normal",
                    marker,
                    currentUT: 650.0,
                    proximityDistanceMeters: 100.0,
                    out string reason);

            Assert.Equal(ProximitySamplingTier.None, cadence);
            Assert.Equal("tree-mismatch", reason);
        }

        [Fact]
        public void ProximitySamplingCadence_ResolvesDebrisTierBoundaries()
        {
            Assert.Equal(
                ProximitySamplingTier.Full,
                ProximitySamplingCadence.Resolve(
                    250.0,
                    BackgroundRecorder.DebrisFullFidelityProximityRangeMeters,
                    BackgroundRecorder.DebrisHalfFidelityProximityRangeMeters,
                    out string fullReason));
            Assert.Equal("full", fullReason);

            Assert.Equal(
                ProximitySamplingTier.Half,
                ProximitySamplingCadence.Resolve(
                    500.0,
                    BackgroundRecorder.DebrisFullFidelityProximityRangeMeters,
                    BackgroundRecorder.DebrisHalfFidelityProximityRangeMeters,
                    out string halfReason));
            Assert.Equal("half", halfReason);

            Assert.Equal(
                ProximitySamplingTier.None,
                ProximitySamplingCadence.Resolve(
                    500.1,
                    BackgroundRecorder.DebrisFullFidelityProximityRangeMeters,
                    BackgroundRecorder.DebrisHalfFidelityProximityRangeMeters,
                    out string outOfRangeReason));
            Assert.Equal("out-of-range", outOfRangeReason);
        }

        [Theory]
        [InlineData(double.NaN, "distance-missing")]
        [InlineData(double.PositiveInfinity, "distance-missing")]
        [InlineData(-1.0, "distance-invalid")]
        public void ProximitySamplingCadence_InvalidDistance_ReturnsNone(
            double distanceMeters,
            string expectedReason)
        {
            Assert.Equal(
                ProximitySamplingTier.None,
                ProximitySamplingCadence.Resolve(
                    distanceMeters,
                    BackgroundRecorder.DebrisFullFidelityProximityRangeMeters,
                    BackgroundRecorder.DebrisHalfFidelityProximityRangeMeters,
                    out string reason));
            Assert.Equal(expectedReason, reason);
        }

        [Theory]
        [InlineData((int)ProximitySamplingTier.Full)]
        [InlineData((int)ProximitySamplingTier.Half)]
        public void ProximitySamplingCadence_ZeroConfiguredMin_HonorsDefensiveFloor(int tier)
        {
            // configuredMin == 0 is degenerate (no production caller produces
            // it today; ParsekSettings.GetMinSampleInterval is positive). The
            // helper must not collapse to 0 and record every physics frame.
            float interval = ProximitySamplingCadence.ResolveSampleInterval(
                (ProximitySamplingTier)tier,
                configuredMin: 0f,
                configuredMax: 3.0f);

            Assert.True(
                interval >= ProximitySamplingCadence.MinimumSampleIntervalSeconds,
                $"interval={interval} must respect the defensive floor "
                    + $"({ProximitySamplingCadence.MinimumSampleIntervalSeconds})");
        }

        [Theory]
        [InlineData((int)ProximitySamplingTier.None, (int)ProximitySamplingTier.None, 3.0f)]
        [InlineData((int)ProximitySamplingTier.None, (int)ProximitySamplingTier.Full, 0.2f)]
        [InlineData((int)ProximitySamplingTier.None, (int)ProximitySamplingTier.Half, 0.4f)]
        [InlineData((int)ProximitySamplingTier.Full, (int)ProximitySamplingTier.None, 0.2f)]
        [InlineData((int)ProximitySamplingTier.Full, (int)ProximitySamplingTier.Full, 0.2f)]
        [InlineData((int)ProximitySamplingTier.Full, (int)ProximitySamplingTier.Half, 0.2f)]
        [InlineData((int)ProximitySamplingTier.Half, (int)ProximitySamplingTier.None, 0.4f)]
        [InlineData((int)ProximitySamplingTier.Half, (int)ProximitySamplingTier.Full, 0.4f)]
        [InlineData((int)ProximitySamplingTier.Half, (int)ProximitySamplingTier.Half, 0.4f)]
        public void EffectiveMaxSampleInterval_ReFlyTierWinsThenDebrisTier(
            int reFlyTier,
            int debrisTier,
            float expected)
        {
            float actual = FlightRecorder.ResolveEffectiveMaxSampleInterval(
                (ProximitySamplingTier)reFlyTier,
                (ProximitySamplingTier)debrisTier,
                highFidelityActive: false,
                configuredMax: 3.0f,
                configuredMin: 0.2f);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(499.0, false, true)]
        [InlineData(500.0, false, true)]
        [InlineData(500.1, false, false)]
        [InlineData(549.0, true, true)]
        [InlineData(550.0, true, true)]
        [InlineData(550.1, true, false)]
        public void DebrisRelativeSectionRange_UsesEnterAndRetainBoundaries(
            double distanceMeters,
            bool alreadyRelative,
            bool expected)
        {
            Assert.Equal(
                expected,
                BackgroundRecorder.ShouldUseDebrisRelativeSectionForDistance(
                    distanceMeters,
                    alreadyRelative));
        }

        [Fact]
        public void SectionGapStats_ComputesAverageMaxAndLargeGaps()
        {
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 10.0 },
                new TrajectoryPoint { ut = 10.1 },
                new TrajectoryPoint { ut = 10.7 },
                new TrajectoryPoint { ut = 10.9 }
            };

            FlightRecorder.SectionGapStats stats =
                FlightRecorder.ComputeSectionGapStats(frames, largeGapThresholdSeconds: 0.5);

            Assert.Equal(4, stats.FrameCount);
            Assert.Equal(10.0, stats.FirstUT);
            Assert.Equal(10.9, stats.LastUT);
            Assert.Equal(0.3, stats.AverageGapSeconds, precision: 6);
            Assert.Equal(0.6, stats.MaxGapSeconds, precision: 6);
            Assert.Equal(1, stats.LargeGapCount);
            // No per-sample warp data supplied: every large gap counts as
            // normal-rate so the WARN behaviour is unchanged.
            Assert.Equal(1, stats.LargeGapCountAtNormalRate);
        }

        [Fact]
        public void SectionGapStats_AllGapsUnderWarp_NoNormalRateLargeGap()
        {
            // Two large gaps, every bounding sample taken under warp.
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 }, // 10s gap
                new TrajectoryPoint { ut = 125.0 }  // 15s gap
            };
            var warpFlags = new List<bool> { true, true, true };

            FlightRecorder.SectionGapStats stats =
                FlightRecorder.ComputeSectionGapStats(frames, largeGapThresholdSeconds: 0.5, warpFlags: warpFlags);

            Assert.Equal(2, stats.LargeGapCount);
            Assert.Equal(0, stats.LargeGapCountAtNormalRate);
        }

        [Fact]
        public void SectionGapStats_MixedSection_OneNormalRateGapAndOneWarpGap()
        {
            // The reviewer's edge case: a single section holds BOTH a real 1x
            // dropped-sample gap AND a later physics-warp gap. The 1x gap must
            // still count toward LargeGapCountAtNormalRate so it WARNs; the warp
            // gap must not.
            // frames:  0     1(0.7s gap @1x)  2(warp on)  3(20s gap, warp->warp)
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 50.0 },
                new TrajectoryPoint { ut = 50.7 },  // 0.7s gap, both ends 1x  -> normal-rate large gap
                new TrajectoryPoint { ut = 51.0 },  // small gap (warp just engaged at this sample)
                new TrajectoryPoint { ut = 71.0 }   // 20s gap, both ends warp -> warp gap
            };
            var warpFlags = new List<bool> { false, false, true, true };

            FlightRecorder.SectionGapStats stats =
                FlightRecorder.ComputeSectionGapStats(frames, largeGapThresholdSeconds: 0.5, warpFlags: warpFlags);

            Assert.Equal(2, stats.LargeGapCount);
            Assert.Equal(1, stats.LargeGapCountAtNormalRate);
            // The mixed section still WARNs because a 1x gap is present.
            Assert.True(FlightRecorder.ShouldWarnOnSparseSampling(stats.LargeGapCountAtNormalRate));
        }

        [Fact]
        public void SectionGapStats_LargeGapTouchingWarpSampleOnEitherEnd_NotNormalRate()
        {
            // A large gap with warp active at only ONE bounding sample (e.g. the
            // frame straddling a warp transition) is still warp-attributable.
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 0.0 },
                new TrajectoryPoint { ut = 5.0 },  // 5s gap: prev 1x, cur warp
                new TrajectoryPoint { ut = 11.0 }  // 6s gap: prev warp, cur 1x
            };
            var warpFlags = new List<bool> { false, true, false };

            FlightRecorder.SectionGapStats stats =
                FlightRecorder.ComputeSectionGapStats(frames, largeGapThresholdSeconds: 0.5, warpFlags: warpFlags);

            Assert.Equal(2, stats.LargeGapCount);
            Assert.Equal(0, stats.LargeGapCountAtNormalRate);
        }

        [Fact]
        public void SectionGapStats_MismatchedWarpFlagLength_TreatsAllAsNormalRate()
        {
            // Defensive: a length mismatch falls back to "no warp data" so every
            // large gap stays WARN-eligible rather than being silently downgraded.
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 0.0 },
                new TrajectoryPoint { ut = 10.0 }
            };
            var warpFlags = new List<bool> { true }; // wrong length

            FlightRecorder.SectionGapStats stats =
                FlightRecorder.ComputeSectionGapStats(frames, largeGapThresholdSeconds: 0.5, warpFlags: warpFlags);

            Assert.Equal(1, stats.LargeGapCount);
            Assert.Equal(1, stats.LargeGapCountAtNormalRate);
        }

        [Fact]
        public void ShouldWarnOnSparseSampling_NoNormalRateLargeGaps_DoesNotWarn()
        {
            Assert.False(FlightRecorder.ShouldWarnOnSparseSampling(0));
        }

        [Fact]
        public void ShouldWarnOnSparseSampling_HasNormalRateLargeGap_Warns()
        {
            Assert.True(FlightRecorder.ShouldWarnOnSparseSampling(1));
            Assert.True(FlightRecorder.ShouldWarnOnSparseSampling(11));
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
