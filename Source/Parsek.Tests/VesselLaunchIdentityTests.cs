using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="Parsek.VesselLaunchIdentity"/> - the launch-unique-Guid
    /// disambiguator that distinguishes two launches of the same craft (which KSP gives
    /// the same baked persistentId) by their per-launch Vessel.id Guid.
    ///
    /// The two real Guids below are taken from save s15: both Kerbal X recordings share
    /// persistentId 2708531065 but were launched separately, so they carry distinct Guids.
    /// </summary>
    public class VesselLaunchIdentityTests
    {
        private const string GuidA = "2b6e6a60d2c947489753371317fa067e"; // launch A
        private const string GuidB = "a424011b746440baae6030e225c9de31"; // launch B (same craft, different launch)
        private const uint Pid = 2708531065u;                            // craft-baked pid (shared)

        private static Recording Rec(uint pid, string guid) =>
            new Recording { VesselPersistentId = pid, RecordedVesselGuid = guid };

        // -- NormalizeGuid ------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NormalizeGuid_NullOrBlank_ReturnsNull(string input)
        {
            Assert.Null(VesselLaunchIdentity.NormalizeGuid(input));
        }

        [Fact]
        public void NormalizeGuid_DashedForm_NormalizedToN()
        {
            Assert.Equal(GuidA, VesselLaunchIdentity.NormalizeGuid("2b6e6a60-d2c9-4748-9753-371317fa067e"));
        }

        [Fact]
        public void NormalizeGuid_UpperCase_NormalizedToLowerN()
        {
            Assert.Equal(GuidA, VesselLaunchIdentity.NormalizeGuid(GuidA.ToUpperInvariant()));
        }

        [Fact]
        public void NormalizeGuid_NonGuidJunk_ReturnsTrimmedOriginal()
        {
            // A malformed value must still compare equal to itself (not collapse to null).
            Assert.Equal("not-a-guid", VesselLaunchIdentity.NormalizeGuid("  not-a-guid  "));
        }

        // -- GuidsConclusivelyDiffer --------------------------------

        [Fact]
        public void GuidsConclusivelyDiffer_TwoKnownDifferent_True()
        {
            Assert.True(VesselLaunchIdentity.GuidsConclusivelyDiffer(GuidA, GuidB));
        }

        [Fact]
        public void GuidsConclusivelyDiffer_SameValueDifferentForm_False()
        {
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDiffer(
                GuidA, "2b6e6a60-d2c9-4748-9753-371317fa067e"));
        }

        [Theory]
        [InlineData(null, GuidB)]
        [InlineData(GuidA, null)]
        [InlineData("", GuidB)]
        [InlineData(null, null)]
        public void GuidsConclusivelyDiffer_EitherUnknown_False(string a, string b)
        {
            // Unknown on either side is never conclusive - preserves pid-only fallback.
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDiffer(a, b));
        }

        // -- LiveVesselIsRecordedLaunch -----------------------------

        [Fact]
        public void LiveVesselIsRecordedLaunch_PidAndGuidMatch_True()
        {
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(Rec(Pid, GuidA), Pid, GuidA));
        }

        [Fact]
        public void LiveVesselIsRecordedLaunch_PidMatchGuidMismatch_False()
        {
            // The core relaunch case: same baked pid, different launch guid -> not the same launch.
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(Rec(Pid, GuidA), Pid, GuidB));
        }

        [Fact]
        public void LiveVesselIsRecordedLaunch_PidMatchRecordingGuidEmpty_FallsBackToPidMatch()
        {
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(Rec(Pid, null), Pid, GuidA));
        }

        [Fact]
        public void LiveVesselIsRecordedLaunch_PidMatchLiveGuidEmpty_FallsBackToPidMatch()
        {
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(Rec(Pid, GuidA), Pid, null));
        }

        [Fact]
        public void LiveVesselIsRecordedLaunch_PidMismatch_False()
        {
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(Rec(Pid, GuidA), 999u, GuidA));
        }

        [Theory]
        [InlineData(0u)]      // recording pid unset
        public void LiveVesselIsRecordedLaunch_RecordingPidZero_False(uint recPid)
        {
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(Rec(recPid, GuidA), Pid, GuidA));
        }

        [Fact]
        public void LiveVesselIsRecordedLaunch_LivePidZeroOrNullRec_False()
        {
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(Rec(Pid, GuidA), 0u, GuidA));
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedLaunch(null, Pid, GuidA));
        }

        // -- RecordingsShareLaunch ----------------------------------

        [Fact]
        public void RecordingsShareLaunch_SamePidSameGuid_True()
        {
            // Chain-continuation segments of one launch share a guid.
            Assert.True(VesselLaunchIdentity.RecordingsShareLaunch(Rec(Pid, GuidA), Rec(Pid, GuidA)));
        }

        [Fact]
        public void RecordingsShareLaunch_SamePidDifferentGuid_False()
        {
            // Two launches of the same craft: same baked pid, different launch guids -> distinct.
            Assert.False(VesselLaunchIdentity.RecordingsShareLaunch(Rec(Pid, GuidA), Rec(Pid, GuidB)));
        }

        [Theory]
        [InlineData(null, GuidB)]
        [InlineData(GuidA, null)]
        public void RecordingsShareLaunch_EitherGuidEmpty_FallsBackToPidMatch(string a, string b)
        {
            Assert.True(VesselLaunchIdentity.RecordingsShareLaunch(Rec(Pid, a), Rec(Pid, b)));
        }

        [Fact]
        public void RecordingsShareLaunch_DifferentPid_False()
        {
            Assert.False(VesselLaunchIdentity.RecordingsShareLaunch(Rec(Pid, GuidA), Rec(999u, GuidA)));
        }

        [Fact]
        public void RecordingsShareLaunch_PidZeroOrNull_False()
        {
            Assert.False(VesselLaunchIdentity.RecordingsShareLaunch(Rec(0u, GuidA), Rec(0u, GuidA)));
            Assert.False(VesselLaunchIdentity.RecordingsShareLaunch(null, Rec(Pid, GuidA)));
        }

        // -- LiveVesselIsRecordedSpawn (BUG-H spawn-endpoint match) -----
        // SpawnRec(spawnPid, sourcePid, guid): the recording's spawn/adoption endpoint.
        // Adoption stamp == (sourcePid != 0 && spawnPid == sourcePid).

        private static Recording SpawnRec(uint spawnPid, uint sourcePid, string guid) =>
            new Recording
            {
                SpawnedVesselPersistentId = spawnPid,
                VesselPersistentId = sourcePid,
                RecordedVesselGuid = guid
            };

        [Fact]
        public void LiveVesselIsRecordedSpawn_GenuineSpawn_PidMatch_IgnoresGuid_True()
        {
            // Genuine Parsek spawn: spawn pid is KSP-unique (!= craft-baked source pid). A pid match
            // is conclusive regardless of Guid — the spawned vessel carries its own fresh Guid, not
            // the recorded source Guid.
            const uint spawnPid = 555555555u;
            var rec = SpawnRec(spawnPid, Pid, GuidA);
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, spawnPid, GuidB));
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, spawnPid, null));
        }

        [Fact]
        public void LiveVesselIsRecordedSpawn_GenuineSpawn_PidMismatch_False()
        {
            var rec = SpawnRec(555555555u, Pid, GuidA);
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, 999u, GuidA));
        }

        [Fact]
        public void LiveVesselIsRecordedSpawn_AdoptionStamp_GuidMatch_True()
        {
            // Adoption stamp: spawnedPid == craft-baked source pid. Same launch (Guid agrees).
            var rec = SpawnRec(Pid, Pid, GuidA);
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, Pid, GuidA));
        }

        [Fact]
        public void LiveVesselIsRecordedSpawn_AdoptionStamp_GuidMismatch_False()
        {
            // THE BUG-H CASE: a different real launch reuses the craft-baked pid; its Guid differs,
            // so it is NOT the recording's adopted vessel and must never be stripped in its place.
            var rec = SpawnRec(Pid, Pid, GuidA);
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, Pid, GuidB));
        }

        [Fact]
        public void LiveVesselIsRecordedSpawn_AdoptionStamp_UnknownGuid_FallsBackToPid()
        {
            var rec = SpawnRec(Pid, Pid, GuidA);
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, Pid, null));
            var recNoGuid = SpawnRec(Pid, Pid, null);
            Assert.True(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(recNoGuid, Pid, GuidB));
        }

        [Fact]
        public void LiveVesselIsRecordedSpawn_SpawnPidZeroOrNullRecOrCandidateZero_False()
        {
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(SpawnRec(0u, Pid, GuidA), Pid, GuidA));
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(null, Pid, GuidA));
            Assert.False(VesselLaunchIdentity.LiveVesselIsRecordedSpawn(SpawnRec(Pid, Pid, GuidA), 0u, GuidA));
        }

        // -- CandidateMatchesRecording / FindMatchingRecording ----------

        [Fact]
        public void CandidateMatchesRecording_MatchSpawnOnly_IgnoresSourceVessel()
        {
            // matchSource=false: a candidate equal to the recorded SOURCE vessel (pid=VesselPersistentId)
            // is NOT a match unless it is also the spawn endpoint.
            var rec = SpawnRec(555555555u, Pid, GuidA); // genuine spawn, source pid = Pid
            Assert.False(VesselLaunchIdentity.CandidateMatchesRecording(rec, Pid, GuidA, matchSource: false, matchSpawn: true));
            Assert.True(VesselLaunchIdentity.CandidateMatchesRecording(rec, 555555555u, GuidA, matchSource: false, matchSpawn: true));
        }

        [Fact]
        public void CandidateMatchesRecording_MatchSourceTrue_MatchesRecordedSource()
        {
            var rec = SpawnRec(0u, Pid, GuidA); // not spawned; recorded source pid = Pid, guid = GuidA
            Assert.True(VesselLaunchIdentity.CandidateMatchesRecording(rec, Pid, GuidA, matchSource: true, matchSpawn: true));
            // Different launch of the same craft -> not a match.
            Assert.False(VesselLaunchIdentity.CandidateMatchesRecording(rec, Pid, GuidB, matchSource: true, matchSpawn: true));
        }

        [Fact]
        public void FindMatchingRecording_ReturnsFirstSameLaunchMatch_NullWhenNone()
        {
            var recordings = new List<Recording>
            {
                SpawnRec(Pid, Pid, GuidA),       // adoption stamp, launch A
                SpawnRec(777u, 0u, null),        // genuine spawn pid 777
            };
            // Candidate pid=Pid guid=GuidB (launch B) -> different launch from recordings[0] -> no match.
            Assert.Null(VesselLaunchIdentity.FindMatchingRecording(recordings, Pid, GuidB, false, true));
            // Candidate pid=Pid guid=GuidA -> matches recordings[0].
            Assert.Same(recordings[0], VesselLaunchIdentity.FindMatchingRecording(recordings, Pid, GuidA, false, true));
            // Candidate pid=777 -> genuine spawn match.
            Assert.Same(recordings[1], VesselLaunchIdentity.FindMatchingRecording(recordings, 777u, "anything", false, true));
            // Null recordings -> null.
            Assert.Null(VesselLaunchIdentity.FindMatchingRecording(null, Pid, GuidA, false, true));
        }

        // -- TryReadVesselGuid --------------------------------------

        [Fact]
        public void TryReadVesselGuid_SnapshotWithPid_ReturnsNormalizedGuid()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("pid", GuidA);
            node.AddValue("persistentId", Pid.ToString());
            Assert.Equal(GuidA, VesselLaunchIdentity.TryReadVesselGuid(node));
        }

        [Fact]
        public void TryReadVesselGuid_DashedPid_Normalized()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("pid", "2b6e6a60-d2c9-4748-9753-371317fa067e");
            Assert.Equal(GuidA, VesselLaunchIdentity.TryReadVesselGuid(node));
        }

        [Fact]
        public void TryReadVesselGuid_NoPidValue_ReturnsNull()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("persistentId", Pid.ToString());
            Assert.Null(VesselLaunchIdentity.TryReadVesselGuid(node));
        }

        [Fact]
        public void TryReadVesselGuid_MalformedPid_ReturnsNull()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("pid", "not-a-guid");
            Assert.Null(VesselLaunchIdentity.TryReadVesselGuid(node));
        }

        [Fact]
        public void TryReadVesselGuid_NullSnapshot_ReturnsNull()
        {
            Assert.Null(VesselLaunchIdentity.TryReadVesselGuid(null));
        }
    }
}
