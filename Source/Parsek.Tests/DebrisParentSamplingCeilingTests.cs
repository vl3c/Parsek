using System;
using Parsek;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    // Unit tests for the v13 debris parent-anchored sampling caps added in
    // BackgroundRecorder. The cap is gated on
    // (Recording.IsDebris && ParentAnchorRecordingId != null) and bounds BOTH
    // adaptive-sampler thresholds:
    //   * ResolveDebrisAwareSampleInterval lowers the proximity-tier MIN floor
    //     to ProximityRateSelector.MidInterval (0.5 s) while preserving closer
    //     tiers. The OutOfRange sentinel stays unchanged; the skip gate separately
    //     checks whether a parent-proximity debris tier is active.
    //   * ResolveDebrisAwareMaxSampleInterval caps the configured MAX backstop
    //     at MidInterval so stable-velocity drift past the high-fidelity window
    //     cannot starve the recording for 3.0 s (Medium) or 8.0 s (Low).
    [Collection("Sequential")]
    public class DebrisParentSamplingCeilingTests
    {
        [Fact]
        public void Cap_AppliesAtFarTier_WhenDebrisAndParentSet()
        {
            var rec = new Recording
            {
                RecordingId = "debris-1",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-1"
            };

            double tierInterval = ProximityRateSelector.FarInterval; // 2.0 s
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec, beyondRange: false);

            Assert.Equal(ProximityRateSelector.MidInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenLegacyV11Debris_ParentIdNull()
        {
            var rec = new Recording
            {
                RecordingId = "legacy-debris",
                IsDebris = true,
                ParentAnchorRecordingId = null
            };

            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec, beyondRange: false);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenLegacyV11Debris_ParentIdEmpty()
        {
            var rec = new Recording
            {
                RecordingId = "legacy-debris-empty",
                IsDebris = true,
                ParentAnchorRecordingId = string.Empty
            };

            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec, beyondRange: false);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenNonDebrisRecording()
        {
            var rec = new Recording
            {
                RecordingId = "non-debris",
                IsDebris = false,
                ParentAnchorRecordingId = "parent-x"
            };

            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec, beyondRange: false);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenTreeRecNull()
        {
            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, null, beyondRange: false);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_PreservesCloserDockingTier()
        {
            var rec = new Recording
            {
                RecordingId = "debris-close",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-close"
            };

            double tierInterval = ProximityRateSelector.DockingInterval; // 0.2 s
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec, beyondRange: false);

            Assert.Equal(ProximityRateSelector.DockingInterval, resolved);
        }

        [Fact]
        public void Cap_PreservesMidTierAtIdentity()
        {
            var rec = new Recording
            {
                RecordingId = "debris-mid",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-mid"
            };

            double tierInterval = ProximityRateSelector.MidInterval; // 0.5 s
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec, beyondRange: false);

            Assert.Equal(ProximityRateSelector.MidInterval, resolved);
        }

        [Fact]
        public void Cap_BeyondRange_DebrisTakesTheOneHertzFloor()
        {
            var rec = new Recording
            {
                RecordingId = "debris-far",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-far"
            };

            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(
                ProximityRateSelector.GetSampleInterval(2304.0), rec, beyondRange: true);

            Assert.Equal(1.0, resolved);
        }

        [Fact]
        public void Cap_BeyondRange_ControlledChildKeepsTheFarFloor()
        {
            var rec = new Recording
            {
                RecordingId = "child-far",
                IsDebris = false,
                ParentAnchorRecordingId = "parent-far"
            };

            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(
                ProximityRateSelector.GetSampleInterval(2304.0), rec, beyondRange: true);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        // ===== Resting past the sampling range =====

        private static bool Rests(
            bool beyondRange = true,
            bool highFidelity = false,
            ProximitySamplingTier tier = ProximitySamplingTier.None,
            bool landed = true,
            double speed = 0.0,
            double lastUT = 100.0,
            double velocityChange = 0.0)
        {
            return BackgroundRecorder.ShouldRestSamplingBeyondRange(
                beyondRange, highFidelity, tier, landed, speed, lastUT, velocityChange);
        }

        [Fact]
        public void Rest_ParkedBeyondRangeWithARestSample_IsSilent()
        {
            Assert.True(Rests());
        }

        [Fact]
        public void Rest_EveryOtherVessel_IsSampled()
        {
            // In range, a window that overrides the range, or a piece near its own parent.
            Assert.False(Rests(beyondRange: false));
            Assert.False(Rests(highFidelity: true));
            Assert.False(Rests(tier: ProximitySamplingTier.Full));
            Assert.False(Rests(tier: ProximitySamplingTier.Half));
            // Flying, however slowly (a canopy, the top of an arc).
            Assert.False(Rests(landed: false));
            // Still moving, or its last sample was taken moving: one more sample at rest.
            Assert.False(Rests(speed: BackgroundRecorder.BeyondRangeRestSpeed));
            Assert.False(Rests(velocityChange: BackgroundRecorder.BeyondRangeRestSpeed));
            // No sample yet, unreadable speed.
            Assert.False(Rests(lastUT: -1.0));
            Assert.False(Rests(speed: double.NaN));
            Assert.False(Rests(velocityChange: double.NaN));
        }

        [Fact]
        public void Cap_PreservesUnsetSentinel()
        {
            var rec = new Recording
            {
                RecordingId = "debris-inf",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-inf"
            };

            Assert.Equal(
                ProximityRateSelector.UnsetInterval,
                BackgroundRecorder.ResolveDebrisAwareSampleInterval(
                    ProximityRateSelector.UnsetInterval, rec, beyondRange: true));
            Assert.Equal(
                double.PositiveInfinity,
                BackgroundRecorder.ResolveDebrisAwareSampleInterval(
                    double.PositiveInfinity, rec, beyondRange: false));
        }

        // ===== Gate predicate truth table =====

        [Fact]
        public void GateEligible_DebrisWithParentId_True()
        {
            var rec = new Recording { IsDebris = true, ParentAnchorRecordingId = "p" };
            Assert.True(BackgroundRecorder.IsDebrisAwareSampleCapEligible(rec));
        }

        [Fact]
        public void GateEligible_NonDebris_False()
        {
            var rec = new Recording { IsDebris = false, ParentAnchorRecordingId = "p" };
            Assert.False(BackgroundRecorder.IsDebrisAwareSampleCapEligible(rec));
        }

        [Fact]
        public void GateEligible_DebrisWithoutParentId_False()
        {
            var rec = new Recording { IsDebris = true, ParentAnchorRecordingId = null };
            Assert.False(BackgroundRecorder.IsDebrisAwareSampleCapEligible(rec));
        }

        [Fact]
        public void GateEligible_Null_False()
        {
            Assert.False(BackgroundRecorder.IsDebrisAwareSampleCapEligible(null));
        }

        // ===== Max backstop cap =====

        [Fact]
        public void MaxCap_AppliesAtMediumDefault_WhenDebrisAndParentSet()
        {
            var rec = new Recording
            {
                RecordingId = "debris-max-medium",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-max-medium"
            };

            float configuredMax = ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium); // 3.0 s
            float resolved = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(configuredMax, rec);

            Assert.Equal((float)ProximityRateSelector.MidInterval, resolved);
        }

        [Fact]
        public void MaxCap_AppliesAtLowDefault_WhenDebrisAndParentSet()
        {
            var rec = new Recording
            {
                RecordingId = "debris-max-low",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-max-low"
            };

            float configuredMax = ParsekSettings.GetMaxSampleInterval(SamplingDensity.Low); // 8.0 s
            float resolved = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(configuredMax, rec);

            Assert.Equal((float)ProximityRateSelector.MidInterval, resolved);
        }

        [Fact]
        public void MaxCap_NoOp_WhenLegacyV11Debris()
        {
            var rec = new Recording
            {
                RecordingId = "legacy-max",
                IsDebris = true,
                ParentAnchorRecordingId = null
            };

            float configuredMax = ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            float resolved = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(configuredMax, rec);

            Assert.Equal(configuredMax, resolved);
        }

        [Fact]
        public void MaxCap_NoOp_WhenNonDebris()
        {
            var rec = new Recording
            {
                RecordingId = "non-debris-max",
                IsDebris = false,
                ParentAnchorRecordingId = "p"
            };

            float configuredMax = ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium);
            float resolved = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(configuredMax, rec);

            Assert.Equal(configuredMax, resolved);
        }

        [Fact]
        public void MaxCap_PreservesTighterMaxAtIdentity_WhenAlreadyBelowMid()
        {
            var rec = new Recording
            {
                RecordingId = "debris-max-tight",
                IsDebris = true,
                ParentAnchorRecordingId = "p"
            };

            // High-density preset has max=1.0 s; we're already > MidInterval(0.5 s),
            // so the cap drops it to 0.5 s — not a no-op.
            float resolved = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(1.0f, rec);
            Assert.Equal((float)ProximityRateSelector.MidInterval, resolved);

            // But a configured max at or below MidInterval (e.g. high-fidelity-active path
            // already clamped max to min=0.2 s) must not be raised by the cap.
            float resolvedTight = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(0.2f, rec);
            Assert.Equal(0.2f, resolvedTight);
        }

        // ===== Integration: stable-velocity drift across the backstop window =====

        // This is the exact scenario the reviewer flagged: parent-anchored debris
        // with stable velocity (e.g. drifting in vacuum or descending at terminal)
        // past the 3-second high-fidelity post-decouple window. ShouldRecordPoint's
        // velocity-change branches stay false; only the max-interval backstop forces
        // a sample. Without the max cap, gaps reach configuredMax (3.0 s on Medium,
        // 8.0 s on Low). With the cap, gaps cannot exceed MidInterval (0.5 s).
        [Fact]
        public void Integration_StableVelocity_V12Debris_BackstopFiresWithin500ms()
        {
            var rec = new Recording
            {
                RecordingId = "debris-stable",
                IsDebris = true,
                ParentAnchorRecordingId = "parent-stable"
            };

            float minBeforeCap = ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium); // 0.2 s
            float maxBeforeCap = ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium); // 3.0 s
            float velDir = ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
            float speed = ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium) / 100f;

            // Mid-tier proximity (e.g. 800 m from focus) returns 0.5 s; cap is a no-op
            // here on the min path. The whole point of this test is the MAX path.
            double cappedMin = BackgroundRecorder.ResolveDebrisAwareSampleInterval(
                ProximityRateSelector.MidInterval, rec, beyondRange: false);
            float cappedMax = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(maxBeforeCap, rec);

            Assert.Equal(0.5, cappedMin);
            Assert.Equal(0.5f, cappedMax);

            // Stable velocity: lastVel == currentVel, so velocity-change branches stay false.
            var stableVel = new Vector3(100f, 0f, 0f);
            double lastUT = 100.0;

            // Just under the cap: must NOT record (no velocity change, no backstop).
            Assert.False(TrajectoryMath.ShouldRecordPoint(
                stableVel, stableVel, lastUT + 0.499, lastUT,
                (float)cappedMin, cappedMax, velDir, speed));

            // At the cap: backstop fires.
            Assert.True(TrajectoryMath.ShouldRecordPoint(
                stableVel, stableVel, lastUT + 0.500, lastUT,
                (float)cappedMin, cappedMax, velDir, speed));

            // Pre-fix Medium-default behaviour (regression baseline): with the
            // 3.0 s configured max and stable velocity, no sample fires until the
            // 3-second backstop. Anything in [0.5 s, 3.0 s) was a silent gap.
            Assert.False(TrajectoryMath.ShouldRecordPoint(
                stableVel, stableVel, lastUT + 1.500, lastUT,
                minBeforeCap, maxBeforeCap, velDir, speed));
            Assert.True(TrajectoryMath.ShouldRecordPoint(
                stableVel, stableVel, lastUT + 3.000, lastUT,
                minBeforeCap, maxBeforeCap, velDir, speed));
        }

        [Fact]
        public void Integration_StableVelocity_NonDebris_BackstopUnchanged()
        {
            var rec = new Recording
            {
                RecordingId = "non-debris-stable",
                IsDebris = false,
                ParentAnchorRecordingId = "p"
            };

            float maxBeforeCap = ParsekSettings.GetMaxSampleInterval(SamplingDensity.Medium); // 3.0 s
            float minInterval = ParsekSettings.GetMinSampleInterval(SamplingDensity.Medium);
            float velDir = ParsekSettings.GetVelocityDirThreshold(SamplingDensity.Medium);
            float speed = ParsekSettings.GetSpeedChangeThreshold(SamplingDensity.Medium) / 100f;

            float cappedMax = BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(maxBeforeCap, rec);
            Assert.Equal(maxBeforeCap, cappedMax);

            var stableVel = new Vector3(100f, 0f, 0f);
            double lastUT = 100.0;

            // Non-debris recording must keep the original 3-second backstop,
            // not be pulled in to 0.5 s.
            Assert.False(TrajectoryMath.ShouldRecordPoint(
                stableVel, stableVel, lastUT + 1.000, lastUT,
                minInterval, cappedMax, velDir, speed));
            Assert.True(TrajectoryMath.ShouldRecordPoint(
                stableVel, stableVel, lastUT + 3.000, lastUT,
                minInterval, cappedMax, velDir, speed));
        }
    }
}
