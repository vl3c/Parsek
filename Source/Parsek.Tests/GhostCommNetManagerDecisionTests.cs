using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The decisions of <c>GhostCommNetManager</c>'s live shell (test gap audit C9), pulled
    /// out as pure statics: the registration row identity rule, the removal reason, the
    /// timeline sample UT, the derivation cache key, the per-tick prune, the held-source
    /// flags and the chain-snapshot surface read. What stays live: the CommNode objects and
    /// the network add / remove (a <c>CommNetwork</c> needs the CommNet scenario), prefab and
    /// roster derivation (<c>GhostCommNetDerivation.DeriveSpec</c> reads <c>PartLoader</c>),
    /// world positions (<c>CelestialBody</c> and <c>Orbit</c> math) and the pre-update hook;
    /// those are driven by the in-game <c>GhostCommNet</c> categories and lanes CN-1 .. CN-5.
    /// The three-pass registration resolver itself is pure in <c>GhostCommNet.cs</c> and
    /// pinned by the <c>ResolveRegistrations_*</c> cells in <c>GhostCommNetTests</c>.
    /// </summary>
    public class GhostCommNetManagerDecisionTests
    {
        // ---- registration row ----

        [Fact]
        public void Row_ForARecording_UsesTheRecordedLaunchIdentity()
        {
            var rec = new Recording { VesselPersistentId = 4242u, RecordedVesselGuid = "rec-guid" };
            var c = new GhostCommNetCandidate
            {
                Key = "k1",
                Recording = rec,
                VesselPid = 99u,
                LaunchGuid = "live-guid",
                Eligibility = new GhostCommNetEligibility { Eligible = true, Reason = "in-window" },
            };

            GhostCommNetRegistrationRow row = GhostCommNetManager.BuildRegistrationRow(c);

            Assert.Equal("k1", row.Key);
            Assert.False(row.IsChain);
            Assert.True(row.Eligible);
            Assert.Equal("in-window", row.Reason);
            Assert.Equal(4242u, row.Pid);
            Assert.Equal("rec-guid", row.Guid);
        }

        [Fact]
        public void Row_ForAChainVessel_UsesTheLiveIdentityEvenWithARecording()
        {
            var c = new GhostCommNetCandidate
            {
                Key = "chain:7",
                Recording = new Recording { VesselPersistentId = 4242u, RecordedVesselGuid = "rec-guid" },
                ChainSnapshot = new ConfigNode("VESSEL"),
                VesselPid = 77u,
                LaunchGuid = "live-guid",
                Eligibility = new GhostCommNetEligibility { Eligible = false, Reason = "chain-claimed" },
            };

            GhostCommNetRegistrationRow row = GhostCommNetManager.BuildRegistrationRow(c);

            Assert.True(row.IsChain);
            Assert.False(row.Eligible);
            Assert.Equal("chain-claimed", row.Reason);
            Assert.Equal(77u, row.Pid);
            Assert.Equal("live-guid", row.Guid);
        }

        [Fact]
        public void Row_WithNoRecording_FallsBackToTheCandidateIdentity()
        {
            var c = new GhostCommNetCandidate { Key = "k2", VesselPid = 5u, LaunchGuid = "g" };
            GhostCommNetRegistrationRow row = GhostCommNetManager.BuildRegistrationRow(c);
            Assert.False(row.IsChain);
            Assert.Equal(5u, row.Pid);
            Assert.Equal("g", row.Guid);
        }

        // ---- removal reason ----

        [Theory]
        [InlineData(false, true, "type-ignored", "in-window", "left-timeline")]
        [InlineData(false, false, null, null, "left-timeline")]
        [InlineData(true, true, "covered-by-recording", "in-window", "covered-by-recording")]
        [InlineData(true, false, null, "superseded", "superseded")]
        // A noted exclusion wins even when its stored reason is null.
        [InlineData(true, true, null, "superseded", null)]
        public void RemovalReason(bool stillCandidate, bool hasExclusion, string exclusion,
            string eligibilityReason, string expected)
        {
            Assert.Equal(expected, GhostCommNetManager.ResolveRemovalReason(
                stillCandidate, hasExclusion, exclusion, eligibilityReason));
        }

        // ---- timeline sample UT ----

        [Theory]
        [InlineData(100.0, 200.0, false, 100.0)]
        [InlineData(200.0, 200.0, false, 200.0)]
        [InlineData(250.0, 200.0, false, 200.0)]
        [InlineData(100.0, 200.0, true, 200.0)]
        public void SampleUT_IsTheEndStateOnceHeldOrPastEnd(double now, double endUT, bool hold, double expected)
        {
            Assert.Equal(expected, GhostCommNetManager.TimelineSampleUT(now, endUT, hold));
        }

        // ---- derivation cache ----

        [Fact]
        public void DerivationSnapshot_PrefersTheVesselSnapshot()
        {
            var vessel = new ConfigNode("VESSEL");
            var ghost = new ConfigNode("GHOST");
            string source;

            Assert.Same(vessel, GhostCommNetManager.SelectDerivationSnapshot(
                new Recording { VesselSnapshot = vessel, GhostVisualSnapshot = ghost }, out source));
            Assert.Equal("vessel-snapshot", source);

            Assert.Same(ghost, GhostCommNetManager.SelectDerivationSnapshot(
                new Recording { GhostVisualSnapshot = ghost }, out source));
            Assert.Equal("ghost-visual-snapshot", source);

            Assert.Null(GhostCommNetManager.SelectDerivationSnapshot(new Recording(), out source));
            Assert.Equal("none", source);
        }

        [Fact]
        public void DerivationIsCurrent_OnlyForTheSameObjectsAtTheSameLength()
        {
            var snap = new ConfigNode("VESSEL");
            var events = new List<PartEvent>();

            Assert.True(GhostCommNetManager.IsDerivationCurrent(snap, events, 0, snap, events, 0));
            Assert.True(GhostCommNetManager.IsDerivationCurrent(null, null, 0, null, null, 0));
            // A replaced snapshot (same content, new object) re-derives.
            Assert.False(GhostCommNetManager.IsDerivationCurrent(
                snap, events, 0, new ConfigNode("VESSEL"), events, 0));
            // A replaced event list re-derives.
            Assert.False(GhostCommNetManager.IsDerivationCurrent(
                snap, events, 0, snap, new List<PartEvent>(), 0));
            // An appended event re-derives.
            Assert.False(GhostCommNetManager.IsDerivationCurrent(snap, events, 0, snap, events, 1));
        }

        // ---- per-tick prune ----

        [Fact]
        public void Prune_ForgetsUnseenKeys_AndClearsTheNoteOfEveryEligibleKey()
        {
            var derived = new Dictionary<string, int> { { "seen", 1 }, { "gone", 2 } };
            var exclusions = new Dictionary<string, string>
            {
                { "seen-excluded", "type-ignored" },
                { "seen-eligible", "incapable" },
                { "gone", "superseded" },
            };
            var seen = new HashSet<string> { "seen", "seen-excluded", "seen-eligible" };
            var desired = new HashSet<string> { "seen-eligible" };
            var scratch = new List<string> { "stale-entry" };

            GhostCommNetManager.PruneUnseenKeys(derived, exclusions, seen, desired, scratch);

            Assert.Equal(new[] { "seen" }, new List<string>(derived.Keys).ToArray());
            Assert.Single(exclusions);
            Assert.Equal("type-ignored", exclusions["seen-excluded"]);
        }

        [Fact]
        public void Prune_WithNothingSeen_EmptiesBothMaps()
        {
            var derived = new Dictionary<string, string> { { "a", "x" } };
            var exclusions = new Dictionary<string, string> { { "b", "r" } };
            GhostCommNetManager.PruneUnseenKeys(derived, exclusions,
                new HashSet<string>(), new HashSet<string>(), new List<string>());
            Assert.Empty(derived);
            Assert.Empty(exclusions);
        }

        // ---- held-source flags ----

        [Theory]
        [InlineData(TerminalState.Landed, true)]
        [InlineData(TerminalState.Splashed, true)]
        [InlineData(TerminalState.Orbiting, false)]
        [InlineData(TerminalState.Destroyed, false)]
        public void SurfaceTerminal(TerminalState state, bool expected)
        {
            Assert.Equal(expected, GhostCommNetManager.IsSurfaceTerminal(state));
        }

        [Fact]
        public void SurfaceTerminal_NoTerminalState_IsFalse()
        {
            Assert.False(GhostCommNetManager.IsSurfaceTerminal(null));
        }

        private static Recording WithSection(ReferenceFrame frame, double startUT, double endUT)
        {
            var rec = new Recording();
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = frame,
                startUT = startUT,
                endUT = endUT,
                frames = new List<TrajectoryPoint>(),
                checkpoints = new List<OrbitSegment>(),
            });
            return rec;
        }

        [Fact]
        public void EndBodyFixed_PlainAbsoluteEnd_IsTrue()
        {
            Assert.True(GhostCommNetManager.IsRecordedEndBodyFixed(
                WithSection(ReferenceFrame.Absolute, 100.0, 200.0), 150.0));
            Assert.True(GhostCommNetManager.IsRecordedEndBodyFixed(new Recording(), 150.0));
        }

        [Theory]
        [InlineData(ReferenceFrame.Relative)]
        [InlineData(ReferenceFrame.OrbitalCheckpoint)]
        public void EndBodyFixed_AnchorOrCheckpointSection_IsFalse(ReferenceFrame frame)
        {
            Assert.False(GhostCommNetManager.IsRecordedEndBodyFixed(WithSection(frame, 100.0, 200.0), 150.0));
        }

        [Fact]
        public void EndBodyFixed_OrbitSegmentCoveringTheEnd_IsFalse()
        {
            Recording rec = WithSection(ReferenceFrame.Absolute, 100.0, 200.0);
            rec.OrbitSegments.Add(new OrbitSegment { startUT = 120.0, endUT = 180.0, bodyName = "Kerbin" });
            Assert.False(GhostCommNetManager.IsRecordedEndBodyFixed(rec, 150.0));
            Assert.True(GhostCommNetManager.IsRecordedEndBodyFixed(rec, 190.0));
        }

        // ---- chain snapshot surface ----

        private static ConfigNode ChainVessel(string sit, string landed = null, string splashed = null)
        {
            var v = new ConfigNode("VESSEL");
            if (sit != null) v.AddValue("sit", sit);
            if (landed != null) v.AddValue("landed", landed);
            if (splashed != null) v.AddValue("splashed", splashed);
            return v;
        }

        [Theory]
        [InlineData("LANDED", null, null, true)]
        [InlineData("SPLASHED", null, null, true)]
        [InlineData("PRELAUNCH", null, null, true)]
        [InlineData("ORBITING", null, null, false)]
        [InlineData("ORBITING", "True", null, true)]
        [InlineData(null, null, "True", true)]
        [InlineData("FLYING", "False", "False", false)]
        [InlineData(null, null, null, false)]
        public void ChainSnapshotLanded(string sit, string landed, string splashed, bool expected)
        {
            Assert.Equal(expected, GhostCommNetManager.IsChainSnapshotLanded(ChainVessel(sit, landed, splashed)));
        }

        private static ConfigNode LandedChain(string refValue, string lat = "-0.0972", string lon = "-74.5577",
            string alt = "70.5")
        {
            ConfigNode v = ChainVessel("LANDED");
            if (lat != null) v.AddValue("lat", lat);
            if (lon != null) v.AddValue("lon", lon);
            if (alt != null) v.AddValue("alt", alt);
            ConfigNode orbit = v.AddNode("ORBIT");
            if (refValue != null) orbit.AddValue("REF", refValue);
            return v;
        }

        [Fact]
        public void ChainSurface_ReadsTheBodyIndexAndCoordinatesInvariantly()
        {
            ConfigNode v = LandedChain("1");
            Assert.True(GhostCommNetManager.TryReadChainSnapshotSurface(
                v, v.GetNode("ORBIT"), 17, out int refIndex, out double lat, out double lon, out double alt));
            Assert.Equal(1, refIndex);
            Assert.Equal(-0.0972, lat);
            Assert.Equal(-74.5577, lon);
            Assert.Equal(70.5, alt);
        }

        [Fact]
        public void ChainSurface_RefusesEveryUnreadableShape()
        {
            ConfigNode ok = LandedChain("1");
            int r;
            double a, b, c;
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(ok, null, 17, out r, out a, out b, out c));
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(ok, ok.GetNode("ORBIT"), -1, out r, out a, out b, out c));
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(ok, ok.GetNode("ORBIT"), 1, out r, out a, out b, out c));

            ConfigNode noRef = LandedChain(null);
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(noRef, noRef.GetNode("ORBIT"), 17, out r, out a, out b, out c));
            ConfigNode badRef = LandedChain("Kerbin");
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(badRef, badRef.GetNode("ORBIT"), 17, out r, out a, out b, out c));
            ConfigNode negRef = LandedChain("-1");
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(negRef, negRef.GetNode("ORBIT"), 17, out r, out a, out b, out c));
            ConfigNode noAlt = LandedChain("1", alt: null);
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(noAlt, noAlt.GetNode("ORBIT"), 17, out r, out a, out b, out c));
            ConfigNode commaLat = LandedChain("1", lat: "0,5");
            Assert.False(GhostCommNetManager.TryReadChainSnapshotSurface(commaLat, commaLat.GetNode("ORBIT"), 17, out r, out a, out b, out c));
        }

        // ---- curves and finiteness ----

        [Fact]
        public void CurveOf_FallsBackToTheStockDefaultRangeCurve_AndNoScienceCurve()
        {
            var spec = new GhostCommNetVesselSpec();
            Assert.Same(GhostCommNetManager.DefaultRangeCurve, GhostCommNetManager.CurveOf(spec, -1, true));
            Assert.Same(GhostCommNetManager.DefaultRangeCurve, GhostCommNetManager.CurveOf(spec, 0, true));
            Assert.Same(GhostCommNetManager.DefaultRangeCurve, GhostCommNetManager.CurveOf(null, 0, true));
            Assert.Null(GhostCommNetManager.CurveOf(spec, 0, false));
        }

        [Fact]
        public void CurveOf_ReturnsTheWinningAntennasOwnCurves()
        {
            var range = new DoubleCurve();
            var science = new DoubleCurve();
            var spec = new GhostCommNetVesselSpec();
            spec.Antennas.Add(new GhostAntennaSpec());
            spec.Antennas.Add(new GhostAntennaSpec { RangeCurve = range, ScienceCurve = science });

            Assert.Same(range, GhostCommNetManager.CurveOf(spec, 1, true));
            Assert.Same(science, GhostCommNetManager.CurveOf(spec, 1, false));
            // An antenna with no curves: default range, no science.
            Assert.Same(GhostCommNetManager.DefaultRangeCurve, GhostCommNetManager.CurveOf(spec, 0, true));
            Assert.Null(GhostCommNetManager.CurveOf(spec, 0, false));
            // A curve object of another type is not a DoubleCurve.
            spec.Antennas.Add(new GhostAntennaSpec { RangeCurve = "not-a-curve" });
            Assert.Same(GhostCommNetManager.DefaultRangeCurve, GhostCommNetManager.CurveOf(spec, 2, true));
        }

        [Fact]
        public void IsFinite_RejectsNaNAndInfinityOnEveryAxis()
        {
            Assert.True(GhostCommNetManager.IsFinite(new Vector3d(1.0, -2.0, 3.0)));
            Assert.False(GhostCommNetManager.IsFinite(new Vector3d(double.NaN, 0, 0)));
            Assert.False(GhostCommNetManager.IsFinite(new Vector3d(0, double.PositiveInfinity, 0)));
            Assert.False(GhostCommNetManager.IsFinite(new Vector3d(0, 0, double.NegativeInfinity)));
        }
    }
}
