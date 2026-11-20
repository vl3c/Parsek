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
            HomeWorld.HomeBodyNameOverrideForTesting = null;
        }

        public void Dispose()
        {
            HomeWorld.HomeBodyNameOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
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
