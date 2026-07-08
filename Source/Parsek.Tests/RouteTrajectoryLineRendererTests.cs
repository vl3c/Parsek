using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Display;
using Parsek.Logistics;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="RouteTrajectoryLineRenderer"/>'s pure decision + builder helpers
    /// (leg extraction, dock clip, same-body classification, the show/hide decision, the cache
    /// signature). The Vectrosity draw + onPreCull slot are runtime-only and covered by the in-game
    /// tests (design doc §15). Mirrors the <see cref="GhostTrajectoryPolylineRenderer"/> build-test
    /// pattern.
    /// </summary>
    [Collection("Sequential")]
    public class RouteTrajectoryLineRendererTests : IDisposable
    {
        public RouteTrajectoryLineRendererTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RouteTrajectoryLineRenderer.ResetForTesting();
            GhostTrajectoryPolylineRenderer.Clear();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RouteTrajectoryLineRenderer.ResetForTesting();
            GhostTrajectoryPolylineRenderer.Clear();
            ParsekSettings.CurrentOverrideForTesting = null;
        }

        // --- RouteLinesEnabled ---

        [Fact]
        public void RouteLinesEnabled_NullSettings_UsesDefault()
        {
            Assert.Equal(RouteTrajectoryLineRenderer.DefaultShowRouteLines,
                RouteTrajectoryLineRenderer.RouteLinesEnabled(null));
        }

        [Fact]
        public void RouteLinesEnabled_SettingOff_False()
        {
            var s = new ParsekSettings { showRouteLines = false };
            Assert.False(RouteTrajectoryLineRenderer.RouteLinesEnabled(s));
        }

        [Fact]
        public void RouteLinesEnabled_SettingOn_True()
        {
            var s = new ParsekSettings { showRouteLines = true };
            Assert.True(RouteTrajectoryLineRenderer.RouteLinesEnabled(s));
        }

        // --- IsSameBodyRoute ---

        [Fact]
        public void IsSameBody_PeriodZeroNoBodies_True()
        {
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(0.0, null));
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(0.0, new List<string>()));
        }

        [Fact]
        public void IsSameBody_PeriodZeroConsistentBodies_True()
        {
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                0.0, new List<string> { "Kerbin", "Kerbin" }));
        }

        [Fact]
        public void IsSameBody_PeriodZeroNullBodyInList_SkippedNotFailed()
        {
            // A null / empty member body is skipped (not resolved yet), not treated as a mismatch.
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                0.0, new List<string> { "Kerbin", null, "Kerbin" }));
        }

        [Fact]
        public void IsSameBody_PeriodZeroMixedBodies_False()
        {
            // A same-body flag (period 0) with members on different bodies is malformed -> declined.
            Assert.False(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                0.0, new List<string> { "Kerbin", "Mun" }));
        }

        [Fact]
        public void IsSameBody_NonZeroPeriod_False()
        {
            // A non-zero synodic period means inter-body -> out of v1 scope.
            Assert.False(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                5000.0, new List<string> { "Kerbin", "Kerbin" }));
        }

        // --- ClassifyRouteLineSkip ---

        [Fact]
        public void Classify_NullRoute_NullRoute()
        {
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.NullRoute,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(null, true, true, 1));
        }

        [Fact]
        public void Classify_Disabled_Disabled()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.Disabled,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(route, false, true, 1));
        }

        [Fact]
        public void Classify_NotSameBody_NotSameBody()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.NotSameBody,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(route, true, false, 1));
        }

        [Fact]
        public void Classify_NoDrawableMembers_NoBackingRecordings()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.NoBackingRecordings,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(route, true, true, 0));
        }

        [Fact]
        public void Classify_AllGood_None()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.None,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(route, true, true, 2));
        }

        // --- LegWithinDockClip ---

        [Fact]
        public void DockClip_Unset_AlwaysIncluded()
        {
            Assert.True(RouteTrajectoryLineRenderer.LegWithinDockClip(100.0, 200.0, -1.0));
            Assert.True(RouteTrajectoryLineRenderer.LegWithinDockClip(100.0, 200.0, 0.0));
        }

        [Fact]
        public void DockClip_LegBeforeDock_Included()
        {
            Assert.True(RouteTrajectoryLineRenderer.LegWithinDockClip(100.0, 140.0, 150.0));
        }

        [Fact]
        public void DockClip_LegCrossingDock_Included()
        {
            // A leg that begins before the dock but ends after it (the dock-arrival leg) is kept.
            Assert.True(RouteTrajectoryLineRenderer.LegWithinDockClip(100.0, 180.0, 150.0));
        }

        [Fact]
        public void DockClip_LegAtOrAfterDock_Excluded()
        {
            // Post-dock docked-stretch tail: dropped from the route render.
            Assert.False(RouteTrajectoryLineRenderer.LegWithinDockClip(150.0, 200.0, 150.0));
            Assert.False(RouteTrajectoryLineRenderer.LegWithinDockClip(200.0, 300.0, 150.0));
        }

        // --- BuildRouteMemberLegs ---

        [Fact]
        public void Build_SingleMember_OneGroupOneLeg()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(rec), out int resolvable, out int totalLegs);

            Assert.Single(groups);
            Assert.Equal("rec-a", groups[0].memberRecordingId);
            Assert.Same(rec, groups[0].rec);
            Assert.Single(groups[0].legs);
            Assert.Equal(1, resolvable);
            Assert.Equal(1, totalLegs);
        }

        [Fact]
        public void Build_TwoMembers_TwoGroups()
        {
            var a = FlatRecording("rec-a", 100.0, 200.0);
            var b = FlatRecording("rec-b", 300.0, 400.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a", "rec-b" } };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(a, b), out int resolvable, out int totalLegs);

            Assert.Equal(2, groups.Count);
            Assert.Equal(2, resolvable);
            Assert.Equal(2, totalLegs);
        }

        [Fact]
        public void Build_UnresolvableMember_Skipped()
        {
            var a = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a", "rec-missing" } };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(a), out int resolvable, out int totalLegs);

            Assert.Single(groups);
            Assert.Equal("rec-a", groups[0].memberRecordingId);
            // Only the resolvable member is counted; the missing id is dropped silently.
            Assert.Equal(1, resolvable);
        }

        [Fact]
        public void Build_DuplicateRecordingId_BuiltOnce()
        {
            var a = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a", "rec-a" } };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(a), out int resolvable, out int totalLegs);

            Assert.Single(groups);
            Assert.Equal(1, resolvable);
        }

        [Fact]
        public void Build_DockClipDropsPostDockMember()
        {
            // Member A runs [100,140] (before the dock at 150); member B runs [200,300] (after).
            // The route render stops at the dock, so B's leg is clipped out and B produces no group.
            var a = FlatRecording("rec-a", 100.0, 140.0);
            var b = FlatRecording("rec-b", 200.0, 300.0);
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-a", "rec-b" },
                RecordedDockUT = 150.0,
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(a, b), out int resolvable, out int totalLegs);

            Assert.Single(groups);
            Assert.Equal("rec-a", groups[0].memberRecordingId);
            // Both members resolved, but only the pre-dock one contributes a drawable leg.
            Assert.Equal(2, resolvable);
            Assert.Equal(1, totalLegs);
        }

        [Fact]
        public void Build_NullRouteOrResolver_Empty()
        {
            Assert.Empty(RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                null, Resolver(), out _, out _));
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };
            Assert.Empty(RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, null, out _, out _));
        }

        // --- ComputeRouteSignature ---

        [Fact]
        public void Signature_Stable_ForUnchangedInputs()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" }, RecordedDockUT = 150.0 };
            long s1 = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));
            long s2 = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));
            Assert.Equal(s1, s2);
        }

        [Fact]
        public void Signature_ChangesWhenRecordingContentChanges()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };
            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            rec.Points.Add(MakePoint(250.0, 0.0, 0.0, 5000.0));
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Signature_ChangesWhenDockUTChanges()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" }, RecordedDockUT = 150.0 };
            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            route.RecordedDockUT = 180.0;
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Signature_ChangesWhenRecordingListChanges()
        {
            var a = FlatRecording("rec-a", 100.0, 200.0);
            var b = FlatRecording("rec-b", 300.0, 400.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };
            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(a, b));

            route.RecordingIds.Add("rec-b");
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(a, b));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Signature_ChangesWhenPeriodFlipsInterBody()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" }, DispatchWindowPeriod = 0.0 };
            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            route.DispatchWindowPeriod = 5000.0;
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            Assert.NotEqual(before, after);
        }

        // --- Cache lifecycle (headless: legs carry null VectorLines) ---

        [Fact]
        public void Refresh_CacheHit_DoesNotRebuild()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };

            RouteTrajectoryLineRenderer.RefreshForRouteForTesting(route, Resolver(rec));
            int buildsAfterFirst = RouteTrajectoryLineRenderer.BuildInvocationCountForTesting;
            Assert.Equal(1, RouteTrajectoryLineRenderer.CacheCountForTesting);

            RouteTrajectoryLineRenderer.RefreshForRouteForTesting(route, Resolver(rec));

            // Same signature -> no rebuild.
            Assert.Equal(buildsAfterFirst, RouteTrajectoryLineRenderer.BuildInvocationCountForTesting);
        }

        [Fact]
        public void Refresh_SignatureChange_Rebuilds()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };

            RouteTrajectoryLineRenderer.RefreshForRouteForTesting(route, Resolver(rec));
            int buildsBefore = RouteTrajectoryLineRenderer.BuildInvocationCountForTesting;

            rec.Points.Add(MakePoint(250.0, 0.0, 0.0, 5000.0));
            RouteTrajectoryLineRenderer.RefreshForRouteForTesting(route, Resolver(rec));

            Assert.True(RouteTrajectoryLineRenderer.BuildInvocationCountForTesting > buildsBefore);
        }

        [Fact]
        public void ReleaseForRoute_DropsCacheEntry()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };
            RouteTrajectoryLineRenderer.RefreshForRouteForTesting(route, Resolver(rec));
            Assert.Equal(1, RouteTrajectoryLineRenderer.CacheCountForTesting);

            RouteTrajectoryLineRenderer.ReleaseForRoute("r1");

            Assert.Equal(0, RouteTrajectoryLineRenderer.CacheCountForTesting);
        }

        // --- Helpers ---

        private static Func<string, Recording> Resolver(params Recording[] recs)
        {
            var map = new Dictionary<string, Recording>(StringComparer.Ordinal);
            foreach (var r in recs)
                map[r.RecordingId] = r;
            return id => map.TryGetValue(id, out var rec) ? rec : null;
        }

        private static Recording FlatRecording(string id, double startUT, double endUT)
        {
            var rec = new Recording { RecordingId = id, StartBodyName = "Kerbin" };
            rec.Points.Add(MakePoint(startUT, -0.1, -74.5, 70.0));
            rec.Points.Add(MakePoint((startUT + endUT) * 0.5, -0.05, -74.5, 20000.0));
            rec.Points.Add(MakePoint(endUT, 0.0, -74.5, 100000.0));
            return rec;
        }

        private static TrajectoryPoint MakePoint(
            double ut, double lat, double lon, double alt, string body = "Kerbin")
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = body,
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
        }
    }
}
