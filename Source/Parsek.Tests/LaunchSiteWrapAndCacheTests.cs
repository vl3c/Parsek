using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Two single-player launch-site fixes (multiplayer plan r0b-prep-small P1-02 / P1-04,
    /// owner answer "Fix both"): the launch-site circles wrap the longitude difference, so a
    /// flight that ends parked on the pad with a wound stored longitude (-434.5 is -74.5) is
    /// retired; and a first runtime read of the stock launch sites that missed a site (filled
    /// from the fallback table) is not kept for the process.
    /// </summary>
    [Collection("Sequential")]
    public class LaunchSiteWrapAndCacheTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private const double KerbinRadius = 600000.0;

        public LaunchSiteWrapAndCacheTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            LaunchSiteExclusionZones.ResetForTesting();
            LaunchSiteExclusionZones.MakingHistoryInstalledOverrideForTesting = () => true;

            var kerbin = TestBodyRegistry.CreateBody("Kerbin", KerbinRadius, 3.5316e12);
            FieldInfo field = typeof(CelestialBody).GetField("isHomeWorld");
            field.SetValue(kerbin, true);
            LiveVesselSnapshot.BodyResolverForTesting = (string name, out CelestialBody body) =>
            {
                body = name == "Kerbin" ? kerbin : null;
                return !ReferenceEquals(body, null);
            };
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => false);
        }

        public void Dispose()
        {
            LiveVesselSnapshot.BodyResolverForTesting = null;
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            LaunchSiteExclusionZones.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------------- the longitude wrap ----------------

        [Theory]
        [InlineData(2.0, 2.0)]
        [InlineData(-179.0 - 179.0, 2.0)]
        [InlineData(179.0 - (-179.0), -2.0)]
        [InlineData(-434.5 - (-74.5), 0.0)]
        [InlineData(180.0, 180.0)]
        [InlineData(-180.0, 180.0)]
        [InlineData(540.0, 180.0)]
        [InlineData(-720.25, -0.25)]
        public void WrapLongitudeDelta_BringsIntoHalfOpenRange(double delta, double expected)
        {
            Assert.Equal(expected, SpawnCollisionDetector.WrapLongitudeDeltaDegrees(delta), 9);
        }

        [Fact]
        public void WrapLongitudeDelta_NonFinite_IsNaN()
        {
            Assert.True(double.IsNaN(SpawnCollisionDetector.WrapLongitudeDeltaDegrees(double.NaN)));
            Assert.True(double.IsNaN(SpawnCollisionDetector.WrapLongitudeDeltaDegrees(double.PositiveInfinity)));
        }

        // catches: the wrap changing any distance whose longitudes are already within 180
        // degrees of each other (every decision on an unwound longitude must stay identical).
        [Fact]
        public void LaunchSiteDistance_EqualsSurfaceDistanceBitForBit_WithinHalfTurn()
        {
            int checkedCount = 0;
            for (double lat = -80.0; lat <= 80.0; lat += 7.3)
            {
                for (double lon = -254.5; lon <= 105.4; lon += 3.7)
                {
                    double a = SpawnCollisionDetector.SurfaceDistance(
                        lat, lon, SpawnCollisionDetector.KscPadLatitude,
                        SpawnCollisionDetector.KscPadLongitude, KerbinRadius);
                    double b = SpawnCollisionDetector.LaunchSiteSurfaceDistance(
                        lat, lon, SpawnCollisionDetector.KscPadLatitude,
                        SpawnCollisionDetector.KscPadLongitude, KerbinRadius);
                    Assert.Equal(BitConverter.DoubleToInt64Bits(a), BitConverter.DoubleToInt64Bits(b));
                    checkedCount++;
                }
            }
            Assert.True(checkedCount > 1000);
        }

        [Theory]
        [InlineData(-360.0)]
        [InlineData(360.0)]
        [InlineData(-720.0)]
        public void KscClassify_WoundPadLongitude_IsPad(double turns)
        {
            Assert.Equal(KscExclusionZone.Pad, SpawnCollisionDetector.ClassifyKscExclusionZone(
                SpawnCollisionDetector.KscPadLatitude,
                SpawnCollisionDetector.KscPadLongitude + turns,
                KerbinRadius, SpawnCollisionDetector.DefaultKscExclusionRadiusMeters));
            Assert.Equal(KscExclusionZone.Runway, SpawnCollisionDetector.ClassifyKscExclusionZone(
                SpawnCollisionDetector.KscRunwayLatitude,
                SpawnCollisionDetector.KscRunwayLongitude + turns,
                KerbinRadius, SpawnCollisionDetector.DefaultKscExclusionRadiusMeters));
        }

        [Fact]
        public void Classify_WoundAltSiteLongitude_HitsThatSite()
        {
            var desert = LaunchSiteExclusionZones.FallbackAltSiteCircles[0];
            LaunchSiteZoneHit hit = LaunchSiteExclusionZones.Classify(
                desert.Latitude, desert.Longitude - 360.0, KerbinRadius,
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters,
                LaunchSiteExclusionZones.FallbackAltSiteCircles);
            Assert.True(hit.IsHit);
            Assert.Equal("Desert_Launch_Site", hit.SiteName);
            Assert.False(hit.IsKsc);
        }

        // catches: the date-line case; the Woomerang pad at +136 and a point at -224 are the
        // same place.
        [Fact]
        public void Classify_AcrossDateLine_HitsWoomerang()
        {
            var woomerang = LaunchSiteExclusionZones.FallbackAltSiteCircles[2];
            LaunchSiteZoneHit hit = LaunchSiteExclusionZones.Classify(
                woomerang.Latitude, woomerang.Longitude - 360.0, KerbinRadius,
                SpawnCollisionDetector.DefaultKscExclusionRadiusMeters,
                LaunchSiteExclusionZones.FallbackAltSiteCircles);
            Assert.Equal("Woomerang_Launch_Site", hit.SiteName);
        }

        // catches: the reported bug - a flight that ends parked on the KSC pad, stored with
        // a longitude wound past the date line, spawned on the pad instead of retiring.
        [Fact]
        public void TryRetire_KscPad_WoundLongitude_Retires()
        {
            double woundLon = SpawnCollisionDetector.KscPadLongitude - 360.0;
            var rec = MakeParked("rec-ksc-pad-wound", SpawnCollisionDetector.KscPadLatitude, woundLon);

            VesselSpawner.KscRetirementDecision decision = VesselSpawner.EvaluateKscEndOfFlightRetirement(rec);
            Assert.True(decision.Retire);
            Assert.Equal(KscExclusionZone.Pad, decision.Zone);
            Assert.True(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 11));
            Assert.True(VesselSpawner.IsSettledAsKscRetirement(rec));
        }

        [Fact]
        public void TryRetire_WoundLongitudeAwayFromPad_StillSpawns()
        {
            // One turn wound and 2 km east of the pad: an ordinary landing.
            double woundLon = SpawnCollisionDetector.KscPadLongitude - 360.0 + MetersToDegrees(2000.0);
            var rec = MakeParked("rec-ksc-field-wound", SpawnCollisionDetector.KscPadLatitude, woundLon);

            Assert.False(VesselSpawner.EvaluateKscEndOfFlightRetirement(rec).Retire);
            Assert.False(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 12));
            Assert.False(rec.VesselSpawned);
        }

        // ---------------- the partial first read ----------------

        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(0, 4, false)]
        [InlineData(2, 4, false)]
        [InlineData(5, 1, false)]
        [InlineData(6, 0, true)]
        [InlineData(2, 0, true)]
        public void ShouldCacheRuntimeRead_Table(int runtime, int fallback, bool expected)
        {
            Assert.Equal(expected, LaunchSiteExclusionZones.ShouldCacheRuntimeRead(runtime, fallback));
        }

        // catches: a first read made before the Making History sites were built freezing
        // their fallback coordinates for the process.
        [Fact]
        public void GetAltSiteCircles_PartialThenFullRead_CachesTheFullRead()
        {
            int reads = 0;
            List<LaunchSiteCircle> partial = KscOnly();
            List<LaunchSiteCircle> full = KscOnly();
            full.AddRange(RuntimeAltSites());
            LaunchSiteExclusionZones.RuntimeAltSiteReaderForTesting = () =>
            {
                reads++;
                return reads == 1 ? partial : full;
            };

            IList<LaunchSiteCircle> first = LaunchSiteExclusionZones.GetAltSiteCircles();
            Assert.Equal(4, CountSource(first, "fallback"));
            Assert.Contains(logLines, l => l.Contains("[LaunchSiteZones]")
                && l.Contains("partial read: runtime=2 fallback=4")
                && l.Contains("missing=Desert_Launch_Site,Desert_Airfield,Woomerang_Launch_Site,Island_Airfield"));

            IList<LaunchSiteCircle> second = LaunchSiteExclusionZones.GetAltSiteCircles();
            Assert.Equal(2, reads);
            Assert.Equal(0, CountSource(second, "fallback"));
            Assert.Equal(6, second.Count);
            Assert.Contains(logLines, l => l.Contains("[LaunchSiteZones]")
                && l.Contains("Launch-site retirement circles resolved: runtime=6 fallback=0"));

            IList<LaunchSiteCircle> third = LaunchSiteExclusionZones.GetAltSiteCircles();
            Assert.Equal(2, reads);
            Assert.Same(second, third);
        }

        [Fact]
        public void GetAltSiteCircles_FullFirstRead_CachesAtOnce()
        {
            int reads = 0;
            List<LaunchSiteCircle> full = KscOnly();
            full.AddRange(RuntimeAltSites());
            LaunchSiteExclusionZones.RuntimeAltSiteReaderForTesting = () => { reads++; return full; };

            IList<LaunchSiteCircle> first = LaunchSiteExclusionZones.GetAltSiteCircles();
            IList<LaunchSiteCircle> second = LaunchSiteExclusionZones.GetAltSiteCircles();
            Assert.Equal(1, reads);
            Assert.Same(first, second);
            Assert.DoesNotContain(logLines, l => l.Contains("partial read"));
        }

        [Fact]
        public void GetAltSiteCircles_WithoutMakingHistory_KscOnlyReadCaches()
        {
            LaunchSiteExclusionZones.MakingHistoryInstalledOverrideForTesting = () => false;
            int reads = 0;
            LaunchSiteExclusionZones.RuntimeAltSiteReaderForTesting = () => { reads++; return KscOnly(); };

            IList<LaunchSiteCircle> first = LaunchSiteExclusionZones.GetAltSiteCircles();
            IList<LaunchSiteCircle> second = LaunchSiteExclusionZones.GetAltSiteCircles();
            Assert.Equal(1, reads);
            Assert.Same(first, second);
            Assert.Equal(0, CountSource(first, "fallback"));
        }

        [Fact]
        public void GetAltSiteCircles_PartialReadsKeepRereading()
        {
            int reads = 0;
            LaunchSiteExclusionZones.RuntimeAltSiteReaderForTesting = () => { reads++; return KscOnly(); };

            LaunchSiteExclusionZones.GetAltSiteCircles();
            LaunchSiteExclusionZones.GetAltSiteCircles();
            LaunchSiteExclusionZones.GetAltSiteCircles();
            Assert.Equal(3, reads);
            Assert.DoesNotContain(logLines, l => l.Contains("Launch-site retirement circles resolved"));
        }

        // ---------------- helpers ----------------

        private static double MetersToDegrees(double meters)
            => meters / (KerbinRadius * Math.PI / 180.0);

        private static int CountSource(IList<LaunchSiteCircle> circles, string source)
        {
            int n = 0;
            for (int i = 0; i < circles.Count; i++)
                if (circles[i].Source == source) n++;
            return n;
        }

        private static List<LaunchSiteCircle> KscOnly()
        {
            return new List<LaunchSiteCircle>
            {
                new LaunchSiteCircle { SiteName = "LaunchPad", Kind = KscExclusionZone.Pad,
                    Latitude = SpawnCollisionDetector.KscPadLatitude,
                    Longitude = SpawnCollisionDetector.KscPadLongitude, Source = "runtime" },
                new LaunchSiteCircle { SiteName = "Runway", Kind = KscExclusionZone.Runway,
                    Latitude = SpawnCollisionDetector.KscRunwayLatitude,
                    Longitude = SpawnCollisionDetector.KscRunwayLongitude, Source = "runtime" },
            };
        }

        private static List<LaunchSiteCircle> RuntimeAltSites()
        {
            var list = new List<LaunchSiteCircle>();
            foreach (LaunchSiteCircle c in LaunchSiteExclusionZones.FallbackAltSiteCircles)
            {
                list.Add(new LaunchSiteCircle
                {
                    SiteName = c.SiteName, Kind = c.Kind,
                    Latitude = c.Latitude + 0.0001, Longitude = c.Longitude + 0.0001,
                    Source = "runtime",
                });
            }
            return list;
        }

        private static Recording MakeParked(string id, double lat, double lon)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("lat", lat.ToString("R", CultureInfo.InvariantCulture));
            snapshot.AddValue("lon", lon.ToString("R", CultureInfo.InvariantCulture));
            snapshot.AddValue("alt", "70");
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Pad Rig",
                VesselSnapshot = snapshot,
                GhostVisualSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                CrewEndStatesResolved = true,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, latitude = lat, longitude = lon, altitude = 70.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, latitude = lat, longitude = lon, altitude = 70.0, bodyName = "Kerbin" });
            return rec;
        }
    }
}
