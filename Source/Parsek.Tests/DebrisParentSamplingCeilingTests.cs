using System;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Unit tests for the v12+ debris parent-anchored sampling cap added in
    // BackgroundRecorder.ResolveDebrisAwareSampleInterval. Cap is gated on
    // (Recording.IsDebris && DebrisParentRecordingId != null) and lowers any
    // finite proximity-tier interval to ProximityRateSelector.MidInterval (0.5 s)
    // while preserving closer tiers and the OutOfRange short-circuit.
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
                DebrisParentRecordingId = "parent-1"
            };

            double tierInterval = ProximityRateSelector.FarInterval; // 2.0 s
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec);

            Assert.Equal(ProximityRateSelector.MidInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenLegacyV11Debris_ParentIdNull()
        {
            var rec = new Recording
            {
                RecordingId = "legacy-debris",
                IsDebris = true,
                DebrisParentRecordingId = null
            };

            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenLegacyV11Debris_ParentIdEmpty()
        {
            var rec = new Recording
            {
                RecordingId = "legacy-debris-empty",
                IsDebris = true,
                DebrisParentRecordingId = string.Empty
            };

            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenNonDebrisRecording()
        {
            var rec = new Recording
            {
                RecordingId = "non-debris",
                IsDebris = false,
                DebrisParentRecordingId = "parent-x"
            };

            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_NoOp_WhenTreeRecNull()
        {
            double tierInterval = ProximityRateSelector.FarInterval;
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, null);

            Assert.Equal(ProximityRateSelector.FarInterval, resolved);
        }

        [Fact]
        public void Cap_PreservesCloserDockingTier()
        {
            var rec = new Recording
            {
                RecordingId = "debris-close",
                IsDebris = true,
                DebrisParentRecordingId = "parent-close"
            };

            double tierInterval = ProximityRateSelector.DockingInterval; // 0.2 s
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec);

            Assert.Equal(ProximityRateSelector.DockingInterval, resolved);
        }

        [Fact]
        public void Cap_PreservesMidTierAtIdentity()
        {
            var rec = new Recording
            {
                RecordingId = "debris-mid",
                IsDebris = true,
                DebrisParentRecordingId = "parent-mid"
            };

            double tierInterval = ProximityRateSelector.MidInterval; // 0.5 s
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec);

            Assert.Equal(ProximityRateSelector.MidInterval, resolved);
        }

        [Fact]
        public void Cap_PreservesOutOfRangeShortCircuit()
        {
            var rec = new Recording
            {
                RecordingId = "debris-far",
                IsDebris = true,
                DebrisParentRecordingId = "parent-far"
            };

            double tierInterval = ProximityRateSelector.OutOfRangeInterval; // double.MaxValue
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(tierInterval, rec);

            // Critical invariant: OutOfRange must NOT be capped to a finite value,
            // otherwise out-of-bubble debris would falsely pass the early-return
            // check at BackgroundRecorder.cs OnBackgroundPhysicsFrame and start
            // sampling against an unloaded vessel.
            Assert.Equal(ProximityRateSelector.OutOfRangeInterval, resolved);
        }

        [Fact]
        public void Cap_PreservesPositiveInfinityAsOutOfRangeEquivalent()
        {
            var rec = new Recording
            {
                RecordingId = "debris-inf",
                IsDebris = true,
                DebrisParentRecordingId = "parent-inf"
            };

            // Freezes the contract that any sentinel >= OutOfRangeInterval (including
            // double.PositiveInfinity if a future tier path produces it) short-circuits
            // the cap rather than collapsing to MidInterval.
            double resolved = BackgroundRecorder.ResolveDebrisAwareSampleInterval(double.PositiveInfinity, rec);

            Assert.Equal(double.PositiveInfinity, resolved);
        }
    }
}
