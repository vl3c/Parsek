using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo REFLY-FORK-SELF-SPAWN-AFTER-QUICKSAVE. (1) A save during a Re-Fly flushes the
    /// live points into the session's committed provisional, which then reads as ended,
    /// and its hidden past-end completion asked for a terminal spawn: a real duplicate of
    /// the vessel being flown. The flight scene now holds the terminal spawn of the
    /// recording the player is flying. (2) The deferred joint-break scan swept in the
    /// fragments of that duplicate (a different vessel breaking in the same window) and
    /// wrote fallback Decoupled events for them on the Re-Fly recording; a candidate whose
    /// decouple origin chain never reaches the recorded vessel is now dropped.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyForkSelfSpawnTests : IDisposable
    {
        public ReFlyForkSelfSpawnTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        const string ForkId = "rec_fork";

        // The provisional as RewindInvoker.BuildProvisionalRecording creates it
        // (NotCommitted, PlaybackEnabled=false, no terminal), after a save flushed the
        // live recorder's points and vessel snapshot into it.
        static Recording MakeFlushedReFlyProvisional()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "ORBITING");
            snapshot.AddValue("type", "Probe");
            snapshot.AddValue("name", "Kerbal X Probe");
            return new Recording
            {
                RecordingId = ForkId,
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = "sess_1",
                ProvisionalForRpId = "rp_1",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 495850567,
                PlaybackEnabled = false,
                VesselSnapshot = snapshot,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 650, bodyName = "Kerbin", latitude = 0, longitude = 0, altitude = 80000 },
                    new TrajectoryPoint { ut = 693.7, bodyName = "Kerbin", latitude = 0, longitude = 1, altitude = 80000 },
                },
            };
        }

        [Fact]
        public void FlushedProvisional_ReadsSpawnEligibleAndCompletesPastEnd_TheHazardTheHoldCovers()
        {
            var fork = MakeFlushedReFlyProvisional();

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                fork, isActiveChainMember: false, treeContext: null,
                liveSameLaunchVesselPresent: true);
            Assert.True(needsSpawn, reason);

            var flags = new TrajectoryPlaybackFlags { chainEndUT = fork.EndUT, skipGhost = true };
            Assert.True(GhostPlaybackLogic.ShouldFireHiddenPastEndCompletion(
                fork, flags, currentUT: 694.0,
                completionAlreadyFired: false, earlyDebrisCompletion: false));
        }

        [Fact]
        public void FlushedProvisional_IsReFlySessionRecording_FinalNeedsSpawnFalse()
        {
            var fork = MakeFlushedReFlyProvisional();
            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                fork, isActiveChainMember: false, treeContext: null,
                liveSameLaunchVesselPresent: true);

            bool liveFlown = GhostPlaybackLogic.IsLiveFlownRecording(
                fork.RecordingId, activeReFlyRecordingId: ForkId, liveRecorderRecordingId: null);
            Assert.True(liveFlown);

            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(
                needsSpawn,
                chainSuppressed: false,
                supersededByRelation: false,
                rewindRetired: false,
                spawnHistoricalNeverReplayed: false,
                liveFlownRecording: liveFlown));
        }

        [Fact]
        public void FlushedProvisional_IsLiveRecorderRecording_FinalNeedsSpawnFalse()
        {
            // After the quicksave's LoadGame the marker may not be read back yet; the
            // recorder writing the fork is enough on its own.
            bool liveFlown = GhostPlaybackLogic.IsLiveFlownRecording(
                ForkId, activeReFlyRecordingId: null, liveRecorderRecordingId: ForkId);
            Assert.True(liveFlown);
            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(
                true, false, false, false, false, liveFlown));
        }

        [Fact]
        public void OtherRecording_DuringReFly_KeepsItsTerminalSpawn()
        {
            bool liveFlown = GhostPlaybackLogic.IsLiveFlownRecording(
                "rec_other", activeReFlyRecordingId: ForkId, liveRecorderRecordingId: ForkId);
            Assert.False(liveFlown);
            Assert.True(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(
                true, false, false, false, false, liveFlown));
        }

        [Theory]
        [InlineData(null, null, null)]
        [InlineData("", "", "")]
        [InlineData(null, "rec_a", "rec_a")]
        [InlineData("", "", "rec_a")]
        [InlineData("rec_a", null, null)]
        [InlineData("rec_a", "", "")]
        [InlineData("rec_a", "rec_b", "rec_c")]
        public void IsLiveFlownRecording_NoMatch_False(
            string recordingId, string reFlyId, string recorderId)
        {
            Assert.False(GhostPlaybackLogic.IsLiveFlownRecording(recordingId, reFlyId, recorderId));
        }

        [Fact]
        public void IsLiveFlownRecording_IsOrdinal()
        {
            Assert.False(GhostPlaybackLogic.IsLiveFlownRecording("REC_A", "rec_a", null));
        }

        [Fact]
        public void ComposeFlightFinalNeedsSpawn_EachHoldBlocks()
        {
            Assert.True(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(true, false, false, false, false, false));
            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(false, false, false, false, false, false));
            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(true, true, false, false, false, false));
            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(true, false, true, false, false, false));
            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(true, false, false, true, false, false));
            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(true, false, false, false, true, false));
            Assert.False(GhostPlaybackLogic.ComposeFlightFinalNeedsSpawn(true, false, false, false, false, true));
        }

        // ---- (2) deferred joint-break scan: foreign decouple origins ----

        const uint Recorded = 1000u;
        const uint Copy = 495850567u;

        [Fact]
        public void IsForeignDecoupleOrigin_NoRecordedOrigin_StaysInScan()
        {
            var origins = new Dictionary<uint, uint> { { 7u, Copy } };
            Assert.False(SegmentBoundaryLogic.IsForeignDecoupleOrigin(5u, Recorded, origins));
            Assert.False(SegmentBoundaryLogic.IsForeignDecoupleOrigin(5u, Recorded, null));
        }

        [Fact]
        public void IsForeignDecoupleOrigin_CameOffRecordedVessel_NotForeign()
        {
            var origins = new Dictionary<uint, uint> { { 5u, Recorded } };
            Assert.False(SegmentBoundaryLogic.IsForeignDecoupleOrigin(5u, Recorded, origins));
        }

        [Fact]
        public void IsForeignDecoupleOrigin_FragmentOfOurFragment_NotForeign()
        {
            var origins = new Dictionary<uint, uint> { { 5u, Recorded }, { 6u, 5u }, { 7u, 6u } };
            Assert.False(SegmentBoundaryLogic.IsForeignDecoupleOrigin(7u, Recorded, origins));
        }

        [Fact]
        public void IsForeignDecoupleOrigin_CameOffSpawnedCopy_Foreign()
        {
            var origins = new Dictionary<uint, uint> { { 595832184u, Copy } };
            Assert.True(SegmentBoundaryLogic.IsForeignDecoupleOrigin(595832184u, Recorded, origins));
        }

        [Fact]
        public void IsForeignDecoupleOrigin_FragmentOfCopyFragment_Foreign()
        {
            var origins = new Dictionary<uint, uint> { { 20u, Copy }, { 21u, 20u } };
            Assert.True(SegmentBoundaryLogic.IsForeignDecoupleOrigin(21u, Recorded, origins));
        }

        [Fact]
        public void IsForeignDecoupleOrigin_CycleWithoutRecorded_ForeignAndTerminates()
        {
            var origins = new Dictionary<uint, uint> { { 30u, 31u }, { 31u, 30u } };
            Assert.True(SegmentBoundaryLogic.IsForeignDecoupleOrigin(30u, Recorded, origins));
        }

        [Fact]
        public void IsForeignDecoupleOrigin_UnknownRecordedPidOrZeroOrigin_NoEvidence()
        {
            var origins = new Dictionary<uint, uint> { { 5u, Copy }, { 6u, 0u } };
            Assert.False(SegmentBoundaryLogic.IsForeignDecoupleOrigin(5u, 0u, origins));
            Assert.False(SegmentBoundaryLogic.IsForeignDecoupleOrigin(6u, Recorded, origins));
        }

        [Fact]
        public void CopyBreakup_InTheSameWindow_YieldsNoNewVesselsForTheRecording()
        {
            // The 2026-10-13_0304 shape: the live vessel lost parts it already captured
            // synchronously (none here), and the spawned copy broke into six fragments.
            // The scan keeps none of them, so the split stays within-segment and no
            // fallback Decoupled event is written on the Re-Fly recording.
            uint[] copyFragments = { 595832184u, 716316011u, 2812521149u, 1959506014u, 4216142280u, 867109314u };
            var origins = new Dictionary<uint, uint>();
            foreach (uint pid in copyFragments)
                origins[pid] = Copy;

            var newVesselPids = new List<uint>();
            foreach (uint pid in copyFragments)
            {
                if (SegmentBoundaryLogic.IsForeignDecoupleOrigin(pid, Recorded, origins))
                    continue;
                newVesselPids.Add(pid);
            }

            Assert.Empty(newVesselPids);
            Assert.Equal(JointBreakResult.WithinSegment,
                SegmentBoundaryLogic.ClassifyJointBreakResult(Recorded, newVesselPids, false));
        }
    }
}
