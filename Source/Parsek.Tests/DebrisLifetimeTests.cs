using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Debris lifetime rules (owner rulings Q12 / Q13, 2026-11-18): a debris recording ends at
    /// its 60 s time limit, when its vessel is gone or leaves the physics bubble, or when the
    /// recording it is anchored to stops being written; time warp putting the parent on rails
    /// does not end it.
    /// </summary>
    [Collection("Sequential")]
    public class DebrisLifetimeTests : IDisposable
    {
        public DebrisLifetimeTests()
        {
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        private static BackgroundRecorder.BackgroundRecordingEndReason? Classify(
            double currentUT = 100.0,
            double expiryUT = 160.0,
            bool vesselFound = true,
            bool vesselLoaded = true,
            bool hasParentAnchor = true,
            bool parentRecordingFound = true,
            bool parentRecordingClosed = false,
            bool parentVesselFound = true,
            bool parentVesselLoaded = true,
            bool parentVesselPacked = false)
        {
            return BackgroundRecorder.ClassifyDebrisTtlEntry(
                currentUT, expiryUT, vesselFound, vesselLoaded, hasParentAnchor,
                parentRecordingFound, parentRecordingClosed, parentVesselFound,
                parentVesselLoaded, parentVesselPacked);
        }

        [Fact]
        public void ParentPackedByTimeWarp_DebrisKeepsRecording()
        {
            // DEBRIS-ENDS-WHEN-PARENT-GOES-ON-RAILS: the parent packing for time warp ended
            // every parent-anchored debris recording at once, cutting a booster's fall short.
            Assert.Null(Classify(parentVesselLoaded: true, parentVesselPacked: true));
        }

        [Fact]
        public void ParentUnloaded_DebrisKeepsRecording()
        {
            Assert.Null(Classify(parentVesselLoaded: false, parentVesselPacked: true));
        }

        [Fact]
        public void ParentVesselGone_EndsDebris()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.ParentVesselDestroyed,
                Classify(parentVesselFound: false, parentVesselLoaded: false));
        }

        [Fact]
        public void TimeLimit_EndsDebrisEvenWhileParentIsOnRails()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.TtlExpired,
                Classify(currentUT: 160.0, parentVesselPacked: true));
        }

        [Fact]
        public void DebrisVesselGone_EndsAsDestroyed()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.VesselDestroyedOrDespawned,
                Classify(vesselFound: false, vesselLoaded: false));
        }

        [Fact]
        public void DebrisLeftPhysicsBubble_Ends()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.LeftPhysicsBubble,
                Classify(vesselLoaded: false));
        }

        [Fact]
        public void ParentRecordingMissingOrClosed_Ends()
        {
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.ParentRecordingMissing,
                Classify(parentRecordingFound: false, parentVesselFound: false));
            Assert.Equal(BackgroundRecorder.BackgroundRecordingEndReason.ParentRecordingClosedOrSuperseded,
                Classify(parentRecordingClosed: true, parentVesselFound: false));
        }

        [Fact]
        public void LegacyDebrisWithoutParentAnchor_KeepsDebrisOnlyChecks()
        {
            Assert.Null(Classify(hasParentAnchor: false, parentRecordingFound: false,
                parentVesselFound: false, parentVesselLoaded: false));
        }

        [Fact]
        public void NoTimeLimit_NeverEnds()
        {
            Assert.Null(Classify(expiryUT: double.NaN, vesselFound: false));
        }
    }
}
