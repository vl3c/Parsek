using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="Parsek.QuickloadResumeMatchGuard"/> - the quickload-resume
    /// vessel-match rejection guard added after BDOCK-1 flight 16 (2026-07-24,
    /// INV4-PARTEVENT-PID red).
    ///
    /// Incident constants below are the real values from the flight-16 save
    /// (bdock-station-pad): recording d5355cc6 (the Station launch) carried
    /// recordedGuid 97813bb6... and live pid 3620499050; the same-session
    /// launch_vessel reload rolled out the Interceptor from the SAME .craft file as
    /// pid 4183326108 with a fresh Vessel.id, and the restore coroutine's name
    /// fallback ("Kerbal X" == "Kerbal X") adopted it, corrupting the recording with
    /// the Interceptor's craft-baked part-event pids.
    /// </summary>
    public class QuickloadResumeMatchGuardTests
    {
        private const string StationGuid = "97813bb6fd9b462288d98258fcff0bbd"; // recorded launch
        private const string InterceptorGuid = "0c0ffee0badd4dadb105f00dfeedface"; // fresh launch, same craft
        private const uint StationPid = 3620499050u;
        private const uint InterceptorPid = 4183326108u;

        // -- IsFreshRolloutCandidate --------------------------------

        [Fact]
        public void FreshRollout_CandidateIsCapturedRolloutPid_Rejects()
        {
            Assert.True(QuickloadResumeMatchGuard.IsFreshRolloutCandidate(
                InterceptorPid, sceneEntryFreshRolloutPid: InterceptorPid));
        }

        [Fact]
        public void FreshRollout_NoCapturedRolloutPid_NeverRejects()
        {
            Assert.False(QuickloadResumeMatchGuard.IsFreshRolloutCandidate(
                InterceptorPid, sceneEntryFreshRolloutPid: 0));
        }

        [Fact]
        public void FreshRollout_DifferentCandidatePid_DoesNotReject()
        {
            Assert.False(QuickloadResumeMatchGuard.IsFreshRolloutCandidate(
                StationPid, sceneEntryFreshRolloutPid: InterceptorPid));
        }

        // -- LaunchGuidConclusivelyDiffers --------------------------

        [Fact]
        public void GuidGate_BothKnownAndDifferent_Rejects()
        {
            Assert.True(QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                StationGuid, InterceptorGuid));
        }

        [Fact]
        public void GuidGate_SameGuid_DoesNotReject()
        {
            Assert.False(QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                StationGuid, StationGuid));
        }

        [Theory]
        [InlineData(null, InterceptorGuid)]
        [InlineData(StationGuid, null)]
        [InlineData(null, null)]
        [InlineData("", InterceptorGuid)]
        public void GuidGate_UnknownEitherSide_Inconclusive_DoesNotReject(
            string recordedGuid, string candidateGuid)
        {
            // Legacy recordings without a guid must keep the pid/name fallback behavior.
            Assert.False(QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                recordedGuid, candidateGuid));
        }

        [Fact]
        public void GuidGate_DashedVsNForm_SameLaunch_DoesNotReject()
        {
            Assert.False(QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                "97813bb6-fd9b-4622-88d9-8258fcff0bbd", StationGuid));
        }

        // -- EvaluateCandidate (composition + precedence) -----------

        [Fact]
        public void Evaluate_Bdock1Flight16Incident_RejectsAsFreshRollout()
        {
            // The exact incident shape: fresh rollout captured, guid also differs.
            // FreshRollout wins (checked first; it is recording-independent and
            // conclusive even when the recording has no guid).
            Assert.Equal(
                QuickloadResumeRejection.FreshRollout,
                QuickloadResumeMatchGuard.EvaluateCandidate(
                    StationGuid, InterceptorPid, InterceptorGuid,
                    sceneEntryFreshRolloutPid: InterceptorPid));
        }

        [Fact]
        public void Evaluate_FreshRolloutWithUnknownGuids_StillRejects()
        {
            Assert.Equal(
                QuickloadResumeRejection.FreshRollout,
                QuickloadResumeMatchGuard.EvaluateCandidate(
                    recordedVesselGuid: null, candidatePid: InterceptorPid,
                    candidateGuid: null, sceneEntryFreshRolloutPid: InterceptorPid));
        }

        [Fact]
        public void Evaluate_NotRolloutButGuidDiffers_RejectsAsGuidMismatch()
        {
            Assert.Equal(
                QuickloadResumeRejection.LaunchGuidMismatch,
                QuickloadResumeMatchGuard.EvaluateCandidate(
                    StationGuid, InterceptorPid, InterceptorGuid,
                    sceneEntryFreshRolloutPid: 0));
        }

        [Fact]
        public void Evaluate_GenuineQuickload_SameGuidNewPid_Accepts()
        {
            // KSP may regenerate the pid across a real quickload; the guid survives
            // the save round-trip, so the same launch is accepted for the PID remap.
            Assert.Equal(
                QuickloadResumeRejection.None,
                QuickloadResumeMatchGuard.EvaluateCandidate(
                    StationGuid, candidatePid: 12345u, candidateGuid: StationGuid,
                    sceneEntryFreshRolloutPid: 0));
        }

        [Fact]
        public void Evaluate_LegacyRecordingNoGuid_Accepts()
        {
            Assert.Equal(
                QuickloadResumeRejection.None,
                QuickloadResumeMatchGuard.EvaluateCandidate(
                    recordedVesselGuid: null, candidatePid: StationPid,
                    candidateGuid: InterceptorGuid, sceneEntryFreshRolloutPid: 0));
        }
    }
}
