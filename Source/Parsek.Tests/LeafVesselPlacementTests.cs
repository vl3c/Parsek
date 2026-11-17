using System;
using System.Globalization;
using System.Threading;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="LeafVesselPlacement"/>, the pure comparison core of the
    /// <c>LeafVesselPlacement</c> in-game cell (far-body timeline lanes FAR-*). No Unity or KSP
    /// types, so it runs headlessly.
    /// </summary>
    public class LeafVesselPlacementTests
    {
        // Ike: mu and a ~166.5 km SMA circular-ish orbit, the duna-ike fixture's leaf.
        private const double IkeMu = 1.8568e10;

        private static LeafVesselPlacement.OrbitElements IkeOrbit()
        {
            return new LeafVesselPlacement.OrbitElements
            {
                Body = "Ike",
                SemiMajorAxis = 166501.87,
                Eccentricity = 0.000122,
                InclinationDeg = 12.03,
                LanDeg = 40.0,
                ArgPeDeg = 100.0,
                MeanAnomalyAtEpochRad = 1.0,
                Epoch = 9181000.0,
            };
        }

        // ---------------------------------------------------------------
        //  WrapDeg / IsClosedOrbit / MeanLongitudeDeg
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(180.0, 180.0)]
        [InlineData(-180.0, 180.0)]
        [InlineData(190.0, -170.0)]
        [InlineData(-190.0, 170.0)]
        [InlineData(720.5, 0.5)]
        [InlineData(-359.0, 1.0)]
        public void WrapDeg_FoldsIntoHalfOpenRange(double input, double expected)
        {
            Assert.Equal(expected, LeafVesselPlacement.WrapDeg(input), 9);
        }

        [Fact]
        public void WrapDeg_NonFinite_IsNaN()
        {
            Assert.True(double.IsNaN(LeafVesselPlacement.WrapDeg(double.NaN)));
            Assert.True(double.IsNaN(LeafVesselPlacement.WrapDeg(double.PositiveInfinity)));
        }

        [Fact]
        public void IsClosedOrbit_RejectsHyperbolicAndBadSma()
        {
            var e = IkeOrbit();
            Assert.True(LeafVesselPlacement.IsClosedOrbit(e));
            e.Eccentricity = 1.2;
            Assert.False(LeafVesselPlacement.IsClosedOrbit(e));
            e = IkeOrbit();
            e.SemiMajorAxis = -5000.0;
            Assert.False(LeafVesselPlacement.IsClosedOrbit(e));
            e.SemiMajorAxis = double.NaN;
            Assert.False(LeafVesselPlacement.IsClosedOrbit(e));
        }

        [Fact]
        public void MeanLongitude_AdvancesByMeanMotion()
        {
            var e = IkeOrbit();
            double n = Math.Sqrt(IkeMu / Math.Pow(e.SemiMajorAxis, 3));
            double atEpoch = LeafVesselPlacement.MeanLongitudeDeg(e, IkeMu, e.Epoch);
            double later = LeafVesselPlacement.MeanLongitudeDeg(e, IkeMu, e.Epoch + 100.0);
            Assert.Equal(40.0 + 100.0 + 1.0 * 180.0 / Math.PI, atEpoch, 9);
            Assert.Equal(n * 100.0 * 180.0 / Math.PI, later - atEpoch, 9);
        }

        [Fact]
        public void MeanLongitude_BadMu_IsNaN()
        {
            Assert.True(double.IsNaN(LeafVesselPlacement.MeanLongitudeDeg(IkeOrbit(), 0.0, 0.0)));
        }

        // ---------------------------------------------------------------
        //  CompareOrbits
        // ---------------------------------------------------------------

        [Fact]
        public void CompareOrbits_Identical_IsOk()
        {
            var v = LeafVesselPlacement.CompareOrbits(IkeOrbit(), IkeOrbit(), IkeMu, 9181900.0);
            Assert.True(v.Ok);
            Assert.Equal("ok", v.Reason);
            Assert.Equal(0.0, v.MeanLongitudeDeltaDeg, 12);
        }

        [Fact]
        public void CompareOrbits_SameOrbitReEpochedAtLaterUt_IsOk()
        {
            // A correct on-rails spawn re-states the same orbit at a later epoch: the mean
            // anomaly at the new epoch has advanced by n * dt. The phase must read zero.
            var recorded = IkeOrbit();
            var live = IkeOrbit();
            double n = Math.Sqrt(IkeMu / Math.Pow(recorded.SemiMajorAxis, 3));
            live.Epoch = recorded.Epoch + 5000.0;
            live.MeanAnomalyAtEpochRad = recorded.MeanAnomalyAtEpochRad + n * 5000.0;
            var v = LeafVesselPlacement.CompareOrbits(recorded, live, IkeMu, 9190000.0);
            Assert.True(v.Ok, v.Reason);
            Assert.True(Math.Abs(v.MeanLongitudeDeltaDeg) < 1e-6);
        }

        [Fact]
        public void CompareOrbits_PhaseOffByRewindSpan_FailsPhase()
        {
            // The same orbit, but the vessel sits where it was 300 s earlier (a phase not
            // advanced across a rewind / warp span).
            var recorded = IkeOrbit();
            var live = IkeOrbit();
            live.Epoch = recorded.Epoch + 300.0;
            var v = LeafVesselPlacement.CompareOrbits(recorded, live, IkeMu, 9190000.0);
            Assert.False(v.Ok);
            Assert.Equal("phase", v.Reason);
            Assert.True(v.AlongTrackMeters > 1000.0);
        }

        [Fact]
        public void CompareOrbits_OtherBody_FailsBodyMismatch()
        {
            var live = IkeOrbit();
            live.Body = "Duna";
            var v = LeafVesselPlacement.CompareOrbits(IkeOrbit(), live, IkeMu, 9181900.0);
            Assert.False(v.Ok);
            Assert.Equal("body-mismatch", v.Reason);
        }

        [Theory]
        [InlineData("sma")]
        [InlineData("eccentricity")]
        [InlineData("inclination")]
        [InlineData("lan")]
        [InlineData("phase")]
        public void CompareOrbits_EachElementOverTolerance_NamesIt(string which)
        {
            var live = IkeOrbit();
            switch (which)
            {
                case "sma": live.SemiMajorAxis *= 1.01; break;
                case "eccentricity": live.Eccentricity = 0.01; break;
                case "inclination": live.InclinationDeg += 0.5; break;
                case "lan": live.LanDeg += 2.0; live.ArgPeDeg -= 2.0; break;
                case "phase": live.MeanAnomalyAtEpochRad += 0.1; break;
            }
            var v = LeafVesselPlacement.CompareOrbits(IkeOrbit(), live, IkeMu, 9181900.0);
            Assert.False(v.Ok);
            Assert.Equal(which, v.Reason);
        }

        [Fact]
        public void CompareOrbits_ArgPeOnEccentricOrbit_FailsArgPe()
        {
            var recorded = IkeOrbit();
            recorded.Eccentricity = 0.3;
            var live = recorded;
            live.ArgPeDeg += 5.0;
            live.MeanAnomalyAtEpochRad -= 5.0 * Math.PI / 180.0;
            var v = LeafVesselPlacement.CompareOrbits(recorded, live, IkeMu, 9181900.0);
            Assert.False(v.Ok);
            Assert.Equal("argpe", v.Reason);
        }

        [Fact]
        public void CompareOrbits_ArgPeOnNearCircularOrbit_IsIgnored()
        {
            // Near-circular: argPe and mean anomaly trade freely; only the mean longitude
            // (their sum with LAN) is physical.
            var live = IkeOrbit();
            live.ArgPeDeg += 90.0;
            live.MeanAnomalyAtEpochRad -= Math.PI / 2.0;
            var v = LeafVesselPlacement.CompareOrbits(IkeOrbit(), live, IkeMu, 9181900.0);
            Assert.True(v.Ok, v.Reason);
        }

        [Fact]
        public void CompareOrbits_LanOnEquatorialOrbit_IsIgnored()
        {
            var recorded = IkeOrbit();
            recorded.InclinationDeg = 0.02;
            var live = recorded;
            live.LanDeg += 120.0;
            live.ArgPeDeg -= 120.0;
            var v = LeafVesselPlacement.CompareOrbits(recorded, live, IkeMu, 9181900.0);
            Assert.True(v.Ok, v.Reason);
        }

        [Fact]
        public void CompareOrbits_Hyperbolic_IsUnsupported()
        {
            var live = IkeOrbit();
            live.Eccentricity = 1.5;
            live.SemiMajorAxis = -200000.0;
            var v = LeafVesselPlacement.CompareOrbits(IkeOrbit(), live, IkeMu, 9181900.0);
            Assert.False(v.Ok);
            Assert.Equal("unsupported-open-orbit", v.Reason);
        }

        // ---------------------------------------------------------------
        //  CompareSurface
        // ---------------------------------------------------------------

        [Fact]
        public void SurfaceDistance_OneDegreeOfLatitudeOnDuna()
        {
            double r = 320000.0;
            double d = LeafVesselPlacement.SurfaceDistanceMeters(0.0, 0.0, 1.0, 0.0, r);
            Assert.Equal(r * Math.PI / 180.0, d, 3);
        }

        [Fact]
        public void CompareSurface_SamePlace_IsOk()
        {
            var v = LeafVesselPlacement.CompareSurface("Duna", 20.5, -135.2, "Duna", 20.5, -135.2, 320000.0);
            Assert.True(v.Ok);
            Assert.Equal("ok", v.Reason);
            Assert.Equal(0.0, v.DistanceMeters, 9);
        }

        [Fact]
        public void CompareSurface_LongitudeWrap_IsOk()
        {
            var v = LeafVesselPlacement.CompareSurface("Duna", 0.0, 179.9999, "Duna", 0.0, -179.9999, 320000.0);
            Assert.True(v.Ok, v.Reason);
            Assert.True(v.DistanceMeters < 5.0);
        }

        [Fact]
        public void CompareSurface_KilometresAway_FailsDistance()
        {
            var v = LeafVesselPlacement.CompareSurface("Duna", 20.5, -135.2, "Duna", 20.6, -135.2, 320000.0);
            Assert.False(v.Ok);
            Assert.Equal("distance", v.Reason);
            Assert.True(v.DistanceMeters > 500.0);
        }

        [Fact]
        public void CompareSurface_OtherBody_FailsBodyMismatch()
        {
            var v = LeafVesselPlacement.CompareSurface("Duna", 0.0, 0.0, "Kerbin", 0.0, 0.0, 600000.0);
            Assert.False(v.Ok);
            Assert.Equal("body-mismatch", v.Reason);
        }

        [Fact]
        public void CompareSurface_NaNPosition_Fails()
        {
            var v = LeafVesselPlacement.CompareSurface("Duna", double.NaN, 0.0, "Duna", 0.0, 0.0, 320000.0);
            Assert.False(v.Ok);
            Assert.Equal("distance", v.Reason);
        }

        // ---------------------------------------------------------------
        //  Formatting (the lines the lanes pin)
        // ---------------------------------------------------------------

        [Fact]
        public void Format_IsCultureInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var ov = LeafVesselPlacement.CompareOrbits(IkeOrbit(), IkeOrbit(), IkeMu, 9181900.0);
                string orbit = LeafVesselPlacement.FormatOrbitLine("abc", "DD1 Duna Direct Probe", 2200110044u,
                    "Ike", "Ike", ov);
                Assert.Contains("LeafPlacement: rec=abc vessel='DD1 Duna Direct Probe' pid=2200110044 kind=orbit body=Ike liveBody=Ike ", orbit);
                Assert.Contains("dIncDeg=0.0000 ", orbit);
                Assert.Contains("alongTrackM=0.0 verdict=ok", orbit);

                var sv = LeafVesselPlacement.CompareSurface("Duna", 1.0, 2.0, "Duna", 1.0, 2.001, 320000.0);
                string surface = LeafVesselPlacement.FormatSurfaceLine("def", "Kerbal X", 567464503u, "Duna", "Duna", sv);
                Assert.Matches(@"^LeafPlacement: rec=def vessel='Kerbal X' pid=567464503 kind=surface body=Duna liveBody=Duna distM=5\.6 verdict=ok$", surface);

                string summary = LeafVesselPlacement.FormatSummary("SPACECENTER", 2, 2, 0, 1, 0, 0, "Duna,Ike");
                Assert.Equal("LeafPlacement summary: scene=SPACECENTER compared=2 ok=2 bad=0 absent=1 duplicate=0 unsupported=0 bodies=[Duna,Ike]", summary);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void FormatSummary_NullBodies_IsEmptyList()
        {
            Assert.EndsWith("bodies=[]", LeafVesselPlacement.FormatSummary("FLIGHT", 0, 0, 0, 0, 0, 0, null));
        }
        // ---------------------------------------------------------------
        //  CompareHeight (report-only vertical reading)
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(TerminalState.Landed, 66.5, 65.0, 66.0, true, "ok")]
        [InlineData(TerminalState.Landed, 64.2, 65.0, 66.0, true, "ok")]          // 0.8 m down: settling, not buried
        [InlineData(TerminalState.Landed, 63.5, 65.0, 66.0, false, "below-surface")]
        [InlineData(TerminalState.Landed, 75.5, 65.0, 66.0, false, "above-surface")]
        [InlineData(TerminalState.Landed, 74.5, 65.0, 66.0, true, "ok")]
        [InlineData(TerminalState.Landed, -40.0, -41.0, -40.0, true, "ok")]      // on a sea floor: terrain is negative
        // Splashed: read against the recorded depth (owner ruling 2026-11-17), not sea level.
        [InlineData(TerminalState.Splashed, -0.4, -300.0, -0.3, true, "ok")]
        [InlineData(TerminalState.Splashed, -48.0, -300.0, -50.0, true, "ok")]   // a submarine at its depth
        [InlineData(TerminalState.Splashed, 0.2, -300.0, 1.5, true, "ok")]       // recorded above water: sea level
        [InlineData(TerminalState.Splashed, 0.0, -300.0, -50.0, false, "off-recorded-depth")]  // surfaced
        [InlineData(TerminalState.Splashed, -65.0, -300.0, -50.0, false, "off-recorded-depth")]
        [InlineData(TerminalState.Splashed, -32.0, -30.0, -29.5, false, "below-sea-floor")]
        [InlineData(TerminalState.Splashed, -30.5, -30.0, -29.5, true, "ok")]    // within 1 m of the floor
        public void CompareHeight_LandedAgainstTerrain_SplashedAgainstRecordedDepth(
            TerminalState terminal, double alt, double floor, double recorded, bool ok, string reason)
        {
            var v = LeafVesselPlacement.CompareHeight(terminal, alt, floor, recorded);
            Assert.Equal(ok, v.Ok);
            Assert.Equal(reason, v.Reason);
        }

        [Fact]
        public void CompareHeight_OrbitingOrNaN_IsUnsupported_UnknownSplashedDepthIsSeaLevel()
        {
            Assert.Equal("unsupported", LeafVesselPlacement.CompareHeight(TerminalState.Orbiting, 80000.0, 0.0, 80000.0).Reason);
            var nan = LeafVesselPlacement.CompareHeight(TerminalState.Landed, double.NaN, 65.0, 66.0);
            Assert.False(nan.Ok);
            Assert.Equal("unsupported", nan.Reason);
            var unknownDepth = LeafVesselPlacement.CompareHeight(TerminalState.Splashed, -0.5, -300.0, double.NaN);
            Assert.True(unknownDepth.Ok);
            Assert.Equal(0.0, unknownDepth.ReferenceMeters);
        }

        [Fact]
        public void HeightLines_AreInvariantAndNeverMatchThePinnedPatterns()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var buried = LeafVesselPlacement.CompareHeight(TerminalState.Landed, 60.2, 65.0, 66.0);
                string line = LeafVesselPlacement.FormatHeightLine("abc", "Stack Probe A", 42u, "Kerbin",
                    TerminalState.Landed, 60.2, buried);
                Assert.Equal("LeafPlacement height: rec=abc vessel='Stack Probe A' pid=42 terminal=Landed body=Kerbin "
                    + "altM=60.2 refM=65.0 deltaM=-4.8 floorM=65.0 heightVerdict=below-surface", line);
                // 21 committed lanes forbid `LeafPlacement: [^\r\n]*verdict=(?!ok)` and 25 pin the
                // summary: a bad height must not trip either.
                Assert.DoesNotMatch(@"LeafPlacement: [^\r\n]*verdict=(?!ok)", line);

                string summary = LeafVesselPlacement.FormatHeightSummary("FLIGHT", 2, 1, 1, 0);
                Assert.Equal("LeafPlacement height summary: scene=FLIGHT checked=2 ok=1 bad=1 unsupported=0", summary);
                Assert.DoesNotContain("LeafPlacement summary:", summary);
                Assert.DoesNotMatch(@"LeafPlacement: [^\r\n]*verdict=(?!ok)", summary);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
