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
    }
}
