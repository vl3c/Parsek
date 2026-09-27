using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// KSP-SETTINGS-FOLLOWUPS-RECORDING-2026-09-27 items 2 and 3: the stale launch-site tag
    /// (FlightDriver.LaunchSiteName is only fresh for a new launch) and the owner ruling that
    /// a flight ending parked on ANY stock launch site is retired like the KSC pad / runway.
    /// </summary>
    [Collection("Sequential")]
    public class LaunchSiteFollowupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private const double KerbinRadius = 600000.0;

        public LaunchSiteFollowupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            LaunchSiteExclusionZones.ResetForTesting();

            var kerbin = TestBodyRegistry.CreateBody("Kerbin", KerbinRadius, 3.5316e12);
            FieldInfo field = typeof(CelestialBody).GetField("isHomeWorld");
            field.SetValue(kerbin, true);
            VesselSpawner.BodyResolverForTesting = (string name, out CelestialBody body) =>
            {
                body = name == "Kerbin" ? kerbin : null;
                return !ReferenceEquals(body, null);
            };
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => false);
        }

        public void Dispose()
        {
            VesselSpawner.BodyResolverForTesting = null;
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            LaunchSiteExclusionZones.ResetForTesting();
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static double MetersToDegrees(double meters)
            => meters / (KerbinRadius * Math.PI / 180.0);

        // ---------------- item 2: launch-site capture gate ----------------

        [Theory]
        [InlineData(false, false, Vessel.Situations.PRELAUNCH, false, false, false, true, "prelaunch")]
        [InlineData(false, false, Vessel.Situations.FLYING, true, false, false, true, "launch-transition")]
        [InlineData(false, false, Vessel.Situations.LANDED, false, true, false, true, "fresh-rollout")]
        [InlineData(false, false, Vessel.Situations.LANDED, false, false, true, true, "landed-at-launch-site")]
        // The stale shapes: a take-off from a remote landing, a switched-to orbiter.
        [InlineData(false, false, Vessel.Situations.FLYING, false, false, false, false, "not-a-launch-start")]
        [InlineData(false, false, Vessel.Situations.ORBITING, false, false, false, false, "not-a-launch-start")]
        [InlineData(true, false, Vessel.Situations.PRELAUNCH, true, true, true, false, "eva")]
        [InlineData(false, true, Vessel.Situations.PRELAUNCH, true, true, true, false, "promotion-or-continuation")]
        public void ShouldCaptureLaunchSite_OnlyForALaunchStart(
            bool eva, bool promo, Vessel.Situations sit, bool transition, bool fresh, bool standing,
            bool expected, string expectedReason)
        {
            string reason;
            Assert.Equal(expected,
                FlightRecorder.ShouldCaptureLaunchSite(eva, promo, sit, transition, fresh, standing, out reason));
            Assert.Equal(expectedReason, reason);
        }

        [Theory]
        [InlineData(Vessel.Situations.LANDED, "Runway", "Runway")]
        [InlineData(Vessel.Situations.PRELAUNCH, "Desert_Launch_Site", "Desert_Launch_Site")]
        [InlineData(Vessel.Situations.LANDED, "KSC_Crawlerway", null)]
        [InlineData(Vessel.Situations.FLYING, "Runway", null)]
        [InlineData(Vessel.Situations.LANDED, "", null)]
        public void ResolveLandedAtLaunchSite_NamesOnlyAStockSiteAtRest(
            Vessel.Situations sit, string landedAt, string expected)
        {
            Assert.Equal(expected, FlightRecorder.ResolveLandedAtLaunchSite(sit, landedAt));
        }

        // ---------------- item 3: every stock launch site retires ----------------

        public static IEnumerable<object[]> AltSites()
        {
            foreach (var c in LaunchSiteExclusionZones.FallbackAltSiteCircles)
                yield return new object[] { c.SiteName, c.Latitude, c.Longitude, (int)c.Kind };
        }

        [Theory]
        [MemberData(nameof(AltSites))]
        public void Predicate_LandedOnAnAltSite_Retires(
            string site, double lat, double lon, int kind)
        {
            LaunchSiteZoneHit hit = SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement(
                TerminalState.Landed, false, true, lat, lon + MetersToDegrees(20.0), KerbinRadius,
                LaunchSiteExclusionZones.FallbackAltSiteCircles);
            Assert.True(hit.IsHit);
            Assert.False(hit.IsKsc);
            Assert.Equal(site, hit.SiteName);
            Assert.Equal((KscExclusionZone)kind, hit.Kind);
        }

        [Theory]
        [MemberData(nameof(AltSites))]
        public void Predicate_OutsideAnAltSiteCircle_IsNotRetired(
            string site, double lat, double lon, int kind)
        {
            LaunchSiteZoneHit hit = SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement(
                TerminalState.Landed, false, true, lat + MetersToDegrees(80.0), lon, KerbinRadius,
                LaunchSiteExclusionZones.FallbackAltSiteCircles);
            Assert.False(hit.IsHit, site + " " + kind);
        }

        [Fact]
        public void Predicate_KscIsUnchanged_AndTheKscOnlyFormIgnoresAltSites()
        {
            LaunchSiteZoneHit pad = SpawnCollisionDetector.DecideLaunchSiteEndOfFlightRetirement(
                TerminalState.Landed, false, true,
                SpawnCollisionDetector.KscPadLatitude, SpawnCollisionDetector.KscPadLongitude,
                KerbinRadius, LaunchSiteExclusionZones.FallbackAltSiteCircles);
            Assert.True(pad.IsKsc);
            Assert.Equal(KscExclusionZone.Pad, pad.Kind);

            var desert = LaunchSiteExclusionZones.FallbackAltSiteCircles[0];
            Assert.Equal(KscExclusionZone.None, SpawnCollisionDetector.DecideKscEndOfFlightRetirement(
                TerminalState.Landed, false, true, desert.Latitude, desert.Longitude, KerbinRadius));
        }

        [Fact]
        public void MergeWithFallback_RuntimeWins_PerSite()
        {
            var runtime = new List<LaunchSiteCircle>
            {
                new LaunchSiteCircle { SiteName = "Desert_Launch_Site", Kind = KscExclusionZone.Pad,
                    Latitude = -6.0, Longitude = -143.0, Source = "runtime" },
            };
            var merged = LaunchSiteExclusionZones.MergeWithFallback(
                runtime, LaunchSiteExclusionZones.FallbackAltSiteCircles);
            Assert.Equal(LaunchSiteExclusionZones.FallbackAltSiteCircles.Length, merged.Count);
            Assert.Single(merged.FindAll(c => c.SiteName == "Desert_Launch_Site"));
            Assert.Equal(-6.0, merged.Find(c => c.SiteName == "Desert_Launch_Site").Latitude);
        }

        private static Recording MakeParked(string id, double lat, double lon)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("lat", lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            snapshot.AddValue("lon", lon.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            snapshot.AddValue("alt", "70");
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Desert Rig",
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

        [Fact]
        public void TryRetire_ParkedOnTheDesertPad_RetiresWithTheSiteNamed()
        {
            var desert = LaunchSiteExclusionZones.FallbackAltSiteCircles[0];
            var rec = MakeParked("rec-desert", desert.Latitude, desert.Longitude);

            Assert.True(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 4));
            Assert.True(rec.VesselSpawned);
            Assert.True(rec.SpawnAbandoned);
            Assert.True(VesselSpawner.IsSettledAsKscRetirement(rec));
            Assert.Contains(logLines, l => l.Contains("[Spawner]")
                && l.Contains("Spawn RETIRED for #4 (Desert Rig): flight ended within launch-site exclusion zone (Desert Launch Site pad) - no vessel")
                && l.Contains("rec=rec-desert"));
        }

        [Fact]
        public void TryRetire_LandedAwayFromEverySite_StillSpawns()
        {
            // 2 km east of the Desert pad: an ordinary landing.
            var desert = LaunchSiteExclusionZones.FallbackAltSiteCircles[0];
            var rec = MakeParked("rec-desert-field", desert.Latitude, desert.Longitude + MetersToDegrees(2000.0));

            Assert.False(VesselSpawner.EvaluateKscEndOfFlightRetirement(rec).Retire);
            Assert.False(VesselSpawner.TryRetireEndedFlightAtKsc(rec, 5));
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void FormatRetirementZone_KeepsTheKscWording()
        {
            var ksc = new VesselSpawner.KscRetirementDecision
            {
                Zone = KscExclusionZone.Runway, SiteName = "KSC", IsKsc = true,
            };
            Assert.Equal("KSC exclusion zone (runway)", VesselSpawner.FormatRetirementZone(ksc));
            var island = new VesselSpawner.KscRetirementDecision
            {
                Zone = KscExclusionZone.Runway, SiteName = "Island_Airfield", IsKsc = false,
            };
            Assert.Equal("launch-site exclusion zone (Island Airfield runway)",
                VesselSpawner.FormatRetirementZone(island));
        }
    }
}
