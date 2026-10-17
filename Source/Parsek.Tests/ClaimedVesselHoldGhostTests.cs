using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// After a rewind, a vessel a committed future claims (EVA-11's rover, claimed by an EVA
    /// construction) is despawned at once but its claiming recording starts only at the
    /// claim, so before the fix the rover vanished for the gap. It is now drawn from its
    /// despawn snapshot until the claim; these cells pin the decision and the pose read.
    /// </summary>
    [Collection("Sequential")]
    public class ClaimedVesselHoldGhostTests : IDisposable
    {
        public ClaimedVesselHoldGhostTests()
        {
            ClaimedVesselHoldGhost.ResetRewindSaveVesselsForTesting();
        }

        public void Dispose()
        {
            ClaimedVesselHoldGhost.ResetRewindSaveVesselsForTesting();
        }

        private const uint RoverPid = 1871615455u;
        private const string RoverGuid = "aaaaaaaabbbbccccddddeeeeeeeeeeee";

        [Theory]
        [InlineData(true, true, 30.0, 45.0, false, null)]
        [InlineData(false, true, 30.0, 45.0, false, "no-snapshot")]
        [InlineData(true, false, 30.0, 45.0, false, "chain-inactive")]
        [InlineData(true, true, 45.0, 45.0, false, "claimed")]
        [InlineData(true, true, 60.0, 45.0, false, "claimed")]
        [InlineData(true, true, 30.0, double.NaN, false, "claimed")]
        [InlineData(true, true, 30.0, 45.0, true, "covered-by-recording")]
        public void DecideHideReason_Truth(
            bool hasSnapshot, bool chainActive, double ut, double firstClaimUT, bool covered, string expected)
        {
            Assert.Equal(expected, ClaimedVesselHoldGhost.DecideHideReason(
                hasSnapshot, chainActive, ut, firstClaimUT, covered));
        }

        private static Recording Rec(uint pid, string guid, double start, double end)
        {
            var rec = new Recording
            {
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
            };
            return rec;
        }

        [Fact]
        public void CoveredByRecording_OnlyInsideTheSameLaunchsWindow()
        {
            var recs = new List<Recording>
            {
                Rec(RoverPid, RoverGuid, 100.0, 200.0),
                Rec(RoverPid, "ffffffffffffffffffffffffffffffff", 0.0, 50.0), // another launch
                Rec(42u, RoverGuid, 0.0, 50.0),                               // another vessel
            };

            Assert.True(ClaimedVesselHoldGhost.CoveredByRecording(RoverPid, RoverGuid, recs, 150.0));
            Assert.True(ClaimedVesselHoldGhost.CoveredByRecording(RoverPid, RoverGuid, recs, 100.0));
            Assert.False(ClaimedVesselHoldGhost.CoveredByRecording(RoverPid, RoverGuid, recs, 30.0));
            Assert.False(ClaimedVesselHoldGhost.CoveredByRecording(RoverPid, RoverGuid, recs, 250.0));
            // An unknown claim guid falls back to the pid: the other launch now covers it.
            Assert.True(ClaimedVesselHoldGhost.CoveredByRecording(RoverPid, null, recs, 30.0));
            Assert.False(ClaimedVesselHoldGhost.CoveredByRecording(0u, RoverGuid, recs, 150.0));
            Assert.False(ClaimedVesselHoldGhost.CoveredByRecording(RoverPid, RoverGuid, null, 150.0));
        }

        private static ConfigNode Snapshot(string sit, string landed = "False")
        {
            var v = new ConfigNode("VESSEL");
            v.AddValue("name", "Rover");
            v.AddValue("sit", sit);
            v.AddValue("landed", landed);
            v.AddValue("splashed", "False");
            v.AddValue("lat", "-0.0972");
            v.AddValue("lon", "-74.5577");
            v.AddValue("alt", "68.25");
            v.AddValue("rot", "0.1,-0.2,0.3,0.927");
            var orbit = new ConfigNode("ORBIT");
            orbit.AddValue("SMA", "700000");
            orbit.AddValue("REF", "1");
            v.AddNode(orbit);
            return v;
        }

        [Fact]
        public void TryReadPose_Landed_ReadsBodyFixedCoordinatesAndRotation()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(ClaimedVesselHoldGhost.TryReadPose(Snapshot("LANDED"), out var pose));
                Assert.True(pose.Landed);
                Assert.Equal(1, pose.BodyIndex);
                Assert.Equal(-0.0972, pose.Lat, 9);
                Assert.Equal(-74.5577, pose.Lon, 9);
                Assert.Equal(68.25, pose.Alt, 9);
                Assert.True(pose.HasRotation);
                Assert.Equal(0.1, pose.RotX, 9);
                Assert.Equal(-0.2, pose.RotY, 9);
                Assert.Equal(0.3, pose.RotZ, 9);
                Assert.Equal(0.927, pose.RotW, 9);
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData("PRELAUNCH", "False", true)]
        [InlineData("SPLASHED", "False", true)]
        [InlineData("FLYING", "True", true)]
        [InlineData("ORBITING", "False", false)]
        [InlineData("SUB_ORBITAL", "False", false)]
        public void TryReadPose_ClassifiesSurfaceVersusOrbit(string sit, string landed, bool expectLanded)
        {
            Assert.True(ClaimedVesselHoldGhost.TryReadPose(Snapshot(sit, landed), out var pose));
            Assert.Equal(expectLanded, pose.Landed);
        }

        [Fact]
        public void TryReadPose_RefusesASnapshotWithoutABody()
        {
            var v = Snapshot("LANDED");
            v.RemoveNode("ORBIT");
            Assert.False(ClaimedVesselHoldGhost.TryReadPose(v, out _));
            Assert.False(ClaimedVesselHoldGhost.TryReadPose(null, out _));
        }

        [Fact]
        public void TryReadPose_RefusesALandedSnapshotWithoutCoordinates()
        {
            var v = Snapshot("LANDED");
            v.RemoveValue("lat");
            Assert.False(ClaimedVesselHoldGhost.TryReadPose(v, out _));
        }

        [Fact]
        public void TryReadPose_OrbitingSnapshotNeedsNoCoordinates()
        {
            var v = Snapshot("ORBITING");
            v.RemoveValue("lat");
            v.RemoveValue("rot");
            Assert.True(ClaimedVesselHoldGhost.TryReadPose(v, out var pose));
            Assert.False(pose.HasRotation);
        }

        [Fact]
        public void Messages_AreInvariant()
        {
            var saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(
                    "Claimed vessel hold ghost shown: pid=1871615455 name='Rover' chain=1871615455 " +
                    "source=snapshot-surface body=Kerbin parts=16 firstClaimUT=45.50 UT=12.25 from=despawn",
                    ClaimedVesselHoldGhost.BuildShownMessage(
                        RoverPid, "Rover", RoverPid, true, "Kerbin", 16, 45.5, 12.25));
                Assert.EndsWith(" from=rewind-save",
                    ClaimedVesselHoldGhost.BuildShownMessage(
                        RoverPid, "Rover", RoverPid, false, "Kerbin", 16, 45.5, 12.25, fromRewindSave: true));
                Assert.Equal(
                    "Claimed vessel hold ghost released: pid=1871615455 name='Rover' reason=claimed " +
                    "shownSinceUT=12.25 UT=45.50",
                    ClaimedVesselHoldGhost.BuildReleasedMessage(RoverPid, "Rover", "claimed", 12.25, 45.5));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }

        private static ConfigNode FlightState(params (uint pid, string guid, int parts)[] vessels)
        {
            var fs = new ConfigNode("FLIGHTSTATE");
            foreach (var v in vessels)
            {
                var node = fs.AddNode("VESSEL");
                node.AddValue("persistentId", v.pid.ToString(CultureInfo.InvariantCulture));
                if (v.guid != null) node.AddValue("pid", v.guid);
                for (int i = 0; i < v.parts; i++) node.AddNode("PART");
            }
            return fs;
        }

        [Fact]
        public void RewindSaveCopy_CapturesOnlyClaimedVesselsAndIsADetachedCopy()
        {
            ConfigNode fs = FlightState((RoverPid, RoverGuid, 16), (42u, null, 3));
            int n = ClaimedVesselHoldGhost.CaptureRewindSaveVessels(fs, new HashSet<uint> { RoverPid, 7u });
            Assert.Equal(1, n);
            Assert.Equal(1, ClaimedVesselHoldGhost.RewindSaveVesselCount);

            // A later edit of the save tree (the preprocess keeps working on it) leaves the copy alone.
            fs.GetNodes("VESSEL")[0].AddNode("PART");
            ConfigNode copy = ClaimedVesselHoldGhost.TryGetRewindSaveVessel(RoverPid, RoverGuid);
            Assert.NotNull(copy);
            Assert.Equal(16, copy.GetNodes("PART").Length);
            Assert.Null(ClaimedVesselHoldGhost.TryGetRewindSaveVessel(42u, null));
            Assert.Null(ClaimedVesselHoldGhost.TryGetRewindSaveVessel(0u, null));
        }

        [Fact]
        public void RewindSaveCopy_RefusesAnotherLaunchOfTheSameCraft_AndFallsBackToPidWhenGuidUnknown()
        {
            ClaimedVesselHoldGhost.CaptureRewindSaveVessels(
                FlightState((RoverPid, "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", 16)), new HashSet<uint> { RoverPid });
            Assert.Null(ClaimedVesselHoldGhost.TryGetRewindSaveVessel(RoverPid, RoverGuid));
            Assert.NotNull(ClaimedVesselHoldGhost.TryGetRewindSaveVessel(RoverPid, null));
            Assert.NotNull(ClaimedVesselHoldGhost.TryGetRewindSaveVessel(RoverPid, "0f1e2d3c4b5a69788796a5b4c3d2e1f0"));
        }

        [Fact]
        public void RewindSaveCopy_EveryRewindReplacesTheLastCapture()
        {
            ClaimedVesselHoldGhost.CaptureRewindSaveVessels(FlightState((RoverPid, null, 16)), new HashSet<uint> { RoverPid });
            ClaimedVesselHoldGhost.CaptureRewindSaveVessels(FlightState((RoverPid, null, 16)), null);
            Assert.Equal(0, ClaimedVesselHoldGhost.RewindSaveVesselCount);
            Assert.Null(ClaimedVesselHoldGhost.TryGetRewindSaveVessel(RoverPid, null));
        }

        [Fact]
        public void CollectHeldBackVesselPids_KeysAndFoldedClaims()
        {
            var chain = new GhostChain { OriginalVesselPid = RoverPid, LaunchGuid = RoverGuid, GhostStartUT = 45.0 };
            chain.ClaimedVessels.Add(new ChainClaimedVessel { Pid = 99u, FirstClaimUT = 50.0 });
            chain.ClaimedVessels.Add(new ChainClaimedVessel { Pid = 0u, FirstClaimUT = 50.0 });
            var chains = new Dictionary<uint, GhostChain> { { RoverPid, chain }, { 5u, null } };
            HashSet<uint> pids = RecordingStore.CollectHeldBackVesselPids(chains);
            Assert.Equal(new HashSet<uint> { RoverPid, 99u }, pids);
            Assert.Empty(RecordingStore.CollectHeldBackVesselPids(null));
            Assert.Null(RecordingStore.ResolveRewindClaimedVesselPids(double.NaN));
        }
    }
}
