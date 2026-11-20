using System;
using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Planet packs with another home world (GPP's Gael, RSS's Earth): the Space Center's
    /// ghost and route checks read <see cref="HomeWorld"/>, never the literal "Kerbin". Each
    /// test injects a non-Kerbin home through <see cref="HomeWorld.HomeBodyNameOverrideForTesting"/>
    /// and checks both directions (the pack's home is accepted, a body named Kerbin is not).
    /// </summary>
    [Collection("Sequential")]
    public class HomeWorldTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public HomeWorldTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            ClearSeams();
        }

        public void Dispose()
        {
            ClearSeams();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ClearSeams()
        {
            HomeWorld.HomeBodyNameOverrideForTesting = null;
            HomeWorld.LiveHomeBodyReaderForTesting = null;
            HomeWorld.BodyNameReaderForTesting = null;
            HomeWorld.ResetResolvedForTesting();
        }

        private static Recording MakeRecording(string body, double startUT = 100, double endUT = 200)
        {
            var rec = new Recording { RecordingId = "rec-" + body, VesselName = "Pack Rocket" };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 1, longitude = 2, altitude = 70, bodyName = body,
                rotation = new Quaternion(0, 0, 0, 1),
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 1, longitude = 2, altitude = 5000, bodyName = body,
                rotation = new Quaternion(0, 0, 0, 1),
            });
            return rec;
        }

        // ------------------------------------------------------------ the helper

        [Fact]
        public void HomeBodyName_Headless_FallsBackToStockKerbin_Silently()
        {
            Assert.Equal("Kerbin", HomeWorld.HomeBodyName);
            Assert.True(HomeWorld.IsHomeBodyName("Kerbin"));
            Assert.False(HomeWorld.IsHomeBodyName("Gael"));
            Assert.False(HomeWorld.IsHomeBodyName(null));
            Assert.False(HomeWorld.IsHomeBodyName(""));
            // Pure callers (ParsekKSC.ShouldShowInKSC) must stay log-free headless.
            Assert.DoesNotContain(logLines, l => l.Contains("[HomeWorld]"));
        }

        [Fact]
        public void HomeBodyName_Override_IsThePlanetPackHome()
        {
            HomeWorld.HomeBodyNameOverrideForTesting = "Gael";

            Assert.Equal("Gael", HomeWorld.HomeBodyName);
            Assert.True(HomeWorld.IsHomeBodyName("Gael"));
            Assert.False(HomeWorld.IsHomeBodyName("Kerbin"));
            Assert.False(HomeWorld.IsHomeBodyName("gael"));
        }

        // ------------------------------------------------------------ the memo

        [Fact]
        public void IsResolvedFor_MatchesOnlyTheSameBodyObject()
        {
            object gael = new object();
            var entry = new HomeWorld.ResolvedHomeBody(gael, "Gael");

            Assert.True(HomeWorld.IsResolvedFor(entry, gael));
            // Another body object, even one that would read the same name, re-resolves.
            Assert.False(HomeWorld.IsResolvedFor(entry, new object()));
            Assert.False(HomeWorld.IsResolvedFor(entry, null));
            Assert.False(HomeWorld.IsResolvedFor(null, gael));
        }

        [Fact]
        public void HomeBodyName_ReadsTheBodyNameOncePerBody_AndLogsTheNoticeOnce()
        {
            // catches: the per-frame KSC dispatch / pose callers re-reading Unity's allocating
            // CelestialBody.name (and re-building the non-stock notice) on every call.
            object gael = new object();
            int nameReads = 0;
            HomeWorld.LiveHomeBodyReaderForTesting = () => gael;
            HomeWorld.BodyNameReaderForTesting = body =>
            {
                nameReads++;
                return "Gael";
            };

            string first = HomeWorld.HomeBodyName;
            for (int i = 0; i < 1000; i++)
            {
                Assert.True(HomeWorld.IsHomeBodyName("Gael"));
                Assert.False(HomeWorld.IsHomeBodyName("Kerbin"));
            }

            Assert.Equal("Gael", first);
            Assert.Equal(1, nameReads);
            // The very same string instance every call: the hot path allocates no name.
            Assert.Same(first, HomeWorld.HomeBodyName);
            Assert.Single(logLines, l => l.Contains("[HomeWorld]")
                && l.Contains("home world resolved: 'Gael', not stock 'Kerbin'"));
        }

        [Fact]
        public void HomeBodyName_ADifferentHomeBodyObject_ReResolves()
        {
            // A game loaded with another home body object (never seen in one process with a
            // planet pack, but the memo must not answer for a body it did not read).
            object first = new object();
            object second = new object();
            object live = first;
            int nameReads = 0;
            HomeWorld.LiveHomeBodyReaderForTesting = () => live;
            HomeWorld.BodyNameReaderForTesting = body =>
            {
                nameReads++;
                return object.ReferenceEquals(body, first) ? "Kerbin" : "Gael";
            };

            Assert.Equal("Kerbin", HomeWorld.HomeBodyName);
            Assert.Equal("Kerbin", HomeWorld.HomeBodyName);
            live = second;
            Assert.Equal("Gael", HomeWorld.HomeBodyName);
            Assert.Equal("Gael", HomeWorld.HomeBodyName);

            Assert.Equal(2, nameReads);
            Assert.Single(logLines, l => l.Contains("home world resolved: 'Kerbin' (stock)"));
            Assert.Single(logLines, l => l.Contains("home world resolved: 'Gael', not stock"));
        }

        [Fact]
        public void HomeBodyName_NoHomeBody_FallsBackWithoutCaching_AndWarnsRateLimited()
        {
            object live = null;
            int nameReads = 0;
            HomeWorld.LiveHomeBodyReaderForTesting = () => live;
            HomeWorld.BodyNameReaderForTesting = body =>
            {
                nameReads++;
                return "Gael";
            };

            for (int i = 0; i < 50; i++)
                Assert.Equal("Kerbin", HomeWorld.HomeBodyName);

            Assert.Equal(0, nameReads);
            Assert.Single(logLines, l => l.Contains("[WARN][HomeWorld]")
                && l.Contains("home world unavailable (no-home-body); using stock 'Kerbin'"));

            // The registry comes up later: the real home resolves (the fallback was not cached).
            live = new object();
            Assert.Equal("Gael", HomeWorld.HomeBodyName);
            Assert.Equal(1, nameReads);
        }

        [Fact]
        public void HomeBodyName_NameReadThrows_FallsBackAndRetriesNextCall()
        {
            object home = new object();
            bool fail = true;
            HomeWorld.LiveHomeBodyReaderForTesting = () => home;
            HomeWorld.BodyNameReaderForTesting = body =>
            {
                if (fail)
                    throw new InvalidOperationException("boom");
                return "Gael";
            };

            Assert.Equal("Kerbin", HomeWorld.HomeBodyName);
            Assert.Contains(logLines, l => l.Contains("[WARN][HomeWorld]")
                && l.Contains("read-failed:InvalidOperationException"));

            fail = false;
            Assert.Equal("Gael", HomeWorld.HomeBodyName);
        }

        [Fact]
        public void HomeBodyNameOverride_IsInvisibleToAnotherThread()
        {
            // catches: the override leaking a planet-pack home into a parallel xUnit class
            // (RoverRelayCOracleTests and the other non-Sequential classes reach
            // IsKscOriginRecording / the KSC dispatch with Kerbin data on their own threads).
            HomeWorld.HomeBodyNameOverrideForTesting = "Gael";
            string seenElsewhere = null;
            bool kerbinIsHomeElsewhere = false;
            var other = new System.Threading.Thread(() =>
            {
                seenElsewhere = HomeWorld.HomeBodyName;
                kerbinIsHomeElsewhere = RouteAnalysisEngine.IsKscOriginRecording(
                    new Recording { StartBodyName = "Kerbin", LaunchSiteName = "LaunchPad" });
            });
            other.Start();
            other.Join();

            Assert.Equal("Gael", HomeWorld.HomeBodyName);
            Assert.Equal("Kerbin", seenElsewhere);
            Assert.True(kerbinIsHomeElsewhere);
        }

        // ------------------------------------------------------------ KSC ghosts

        [Fact]
        public void KscStructuralRoute_PlanetPackHome_RendersAKscGhost()
        {
            HomeWorld.HomeBodyNameOverrideForTesting = "Gael";

            Recording gael = MakeRecording("Gael");
            Assert.Equal(ParsekKSC.KscStructuralRoute.Render,
                ParsekKSC.ClassifyKscStructuralRoute(gael, out string reason));
            Assert.Null(reason);
            Assert.True(ParsekKSC.IsKscDrawEligible(gael));

            // A body named Kerbin that is not the home world is off the Space Center's planet.
            Recording kerbin = MakeRecording("Kerbin");
            Assert.Equal(ParsekKSC.KscStructuralRoute.SpawnOnly,
                ParsekKSC.ClassifyKscStructuralRoute(kerbin, out reason));
            Assert.Equal(ParsekKSC.KscStructuralReasonOffKerbin, reason);
        }

        [Fact]
        public void KscStructuralRoute_StockHome_IsUnchanged()
        {
            Assert.Equal(ParsekKSC.KscStructuralRoute.Render,
                ParsekKSC.ClassifyKscStructuralRoute(MakeRecording("Kerbin"), out _));
            Assert.Equal(ParsekKSC.KscStructuralRoute.SpawnOnly,
                ParsekKSC.ClassifyKscStructuralRoute(MakeRecording("Gael"), out _));
        }

        private static ParsekKSC.KscSurfaceLookup AnyBodySurfaceLookup(List<string> bodiesAsked)
        {
            return (string bodyName, double lat, double lon, double alt,
                out Vector3d worldPos, out Quaternion bodyRot) =>
            {
                bodiesAsked.Add(bodyName);
                bodyRot = Quaternion.identity;
                worldPos = new Vector3d(lat, lon, alt);
                return true;
            };
        }

        private static bool NoAnchor(Recording rec, TrackSection section, int sectionIndex,
            double targetUT, out ParsekKSC.KscAnchorFrame anchor)
        {
            anchor = default(ParsekKSC.KscAnchorFrame);
            return false;
        }

        [Fact]
        public void KscPlaybackPose_PlanetPackHome_ResolvesOnTheHomeWorld()
        {
            HomeWorld.HomeBodyNameOverrideForTesting = "Gael";
            var asked = new List<string>();
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                MakeRecording("Gael"), ref cachedIndex, ref cachedKey, 150,
                AnyBodySurfaceLookup(asked), NoAnchor, out ParsekKSC.KscPoseResolution pose);

            Assert.True(resolved);
            Assert.Contains("Gael", asked);
            Assert.NotEqual("non-kerbin", pose.FailureReason);
        }

        [Fact]
        public void KscPlaybackPose_PlanetPackHome_SkipsABodyNamedKerbin()
        {
            HomeWorld.HomeBodyNameOverrideForTesting = "Gael";
            var asked = new List<string>();
            int cachedIndex = 0;
            int cachedKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                MakeRecording("Kerbin"), ref cachedIndex, ref cachedKey, 150,
                AnyBodySurfaceLookup(asked), NoAnchor, out ParsekKSC.KscPoseResolution pose);

            Assert.False(resolved);
            Assert.Empty(asked);
            Assert.Equal("non-kerbin", pose.FailureReason);
            Assert.Contains(logLines, l => l.Contains("[KSCGhost]") && l.Contains("KSC segment skipped")
                && l.Contains("beforeBody=Kerbin"));
        }

        // ------------------------------------------------------------ Space Center routes

        [Fact]
        public void IsKscOriginRecording_FollowsTheHomeWorld()
        {
            var gael = new Recording { StartBodyName = "Gael", LaunchSiteName = "LaunchPad" };
            var kerbin = new Recording { StartBodyName = "Kerbin", LaunchSiteName = "LaunchPad" };

            Assert.False(RouteAnalysisEngine.IsKscOriginRecording(gael));
            Assert.True(RouteAnalysisEngine.IsKscOriginRecording(kerbin));

            HomeWorld.HomeBodyNameOverrideForTesting = "Gael";

            Assert.True(RouteAnalysisEngine.IsKscOriginRecording(gael));
            Assert.False(RouteAnalysisEngine.IsKscOriginRecording(kerbin));
            // A launch site is still required.
            Assert.False(RouteAnalysisEngine.IsKscOriginRecording(
                new Recording { StartBodyName = "Gael", LaunchSiteName = "" }));
        }
    }
}
