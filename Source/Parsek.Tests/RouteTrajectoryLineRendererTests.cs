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

        // --- IsSameBodyRoute / IsInterBodyByEndpoints ---

        [Fact]
        public void IsSameBody_SameBodyEndpointsNoMembers_True()
        {
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                "Kerbin", new List<string> { "Kerbin" }, null));
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                "Kerbin", new List<string> { "Kerbin" }, new List<string>()));
        }

        [Fact]
        public void IsSameBody_NoEndpointsConsistentMembers_True()
        {
            // No readable endpoint bodies -> the shipped v1 member-body consistency read decides.
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                null, null, new List<string> { "Kerbin", "Kerbin" }));
        }

        [Fact]
        public void IsSameBody_NullBodyInMemberList_SkippedNotFailed()
        {
            // A null / empty member body is skipped (not resolved yet), not treated as a mismatch.
            Assert.True(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                "Kerbin", new List<string> { "Kerbin" },
                new List<string> { "Kerbin", null, "Kerbin" }));
        }

        [Fact]
        public void IsSameBody_SameBodyEndpointsMixedMembers_False()
        {
            // Declared same-body endpoints with a member on another body is malformed -> declined.
            Assert.False(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                "Kerbin", new List<string> { "Kerbin" }, new List<string> { "Kerbin", "Mun" }));
        }

        [Fact]
        public void IsSameBody_InterBodyEndpoints_False()
        {
            // Different endpoint bodies mean inter-body, never same-body - even when every
            // RESOLVED member body happens to agree (the transfer member may not have resolved).
            Assert.False(RouteTrajectoryLineRenderer.IsSameBodyRoute(
                "Kerbin", new List<string> { "Duna" }, new List<string> { "Kerbin", "Kerbin" }));
        }

        [Fact]
        public void IsInterBodyByEndpoints_RequiresBothSidesKnownAndDifferent()
        {
            Assert.True(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(
                "Kerbin", new List<string> { "Duna" }));
            // Origin unknown -> cannot answer.
            Assert.False(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(
                null, new List<string> { "Duna" }));
            Assert.False(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(
                "", new List<string> { "Duna" }));
            // No stop, or no stop carrying a body -> cannot answer.
            Assert.False(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints("Kerbin", null));
            Assert.False(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(
                "Kerbin", new List<string> { null, "" }));
            // Same body -> not inter-body.
            Assert.False(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(
                "Kerbin", new List<string> { "Kerbin" }));
            // Multi-stop: ANY stop off the origin body makes it inter-body.
            Assert.True(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(
                "Kerbin", new List<string> { "Kerbin", "Mun" }));
        }

        [Fact]
        public void IsInterBodyByEndpoints_RouteOverload_ReadsOriginAndStops()
        {
            var route = new Route
            {
                Id = "r1",
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
            };
            Assert.True(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(route));

            route.Stops[0].Endpoint = new RouteEndpoint { BodyName = "Kerbin" };
            Assert.False(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(route));

            Assert.False(RouteTrajectoryLineRenderer.IsInterBodyByEndpoints(null));
        }

        // --- ClassifyRouteScope ---

        [Fact]
        public void Scope_SameBodyEndpointsConsistentMembers_SameBody_BasisEndpoints()
        {
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.SameBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    "Kerbin", new List<string> { "Kerbin" }, null,
                    out RouteTrajectoryLineRenderer.RouteScopeBasis basis));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteScopeBasis.Endpoints, basis);

            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.SameBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    "Kerbin", new List<string> { "Kerbin" },
                    new List<string> { "Kerbin", "Kerbin" }));
        }

        [Fact]
        public void Scope_SameBodyEndpointsMixedMembers_Malformed()
        {
            // THE malformed case: the route declares one body at both ends, but its recorded
            // members visit another - drawing the whole path would paint a cross-body chord.
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.MalformedMixedBodies,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    "Kerbin", new List<string> { "Kerbin" }, new List<string> { "Kerbin", "Mun" },
                    out RouteTrajectoryLineRenderer.RouteScopeBasis basis));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteScopeBasis.Endpoints, basis);
        }

        [Fact]
        public void Scope_KerbinToDunaWithTransferMember_InterBody()
        {
            // The G10 subject shape: origin Kerbin, stop Duna, and a member on neither (the
            // transfer leg, whose start body never resolves). The stored period is 0.0 - what
            // RouteBuilder writes - and it no longer has any say.
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.InterBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    "Kerbin", new List<string> { "Duna" },
                    new List<string> { "Kerbin", null, "Duna", "Duna" },
                    out RouteTrajectoryLineRenderer.RouteScopeBasis basis));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteScopeBasis.Endpoints, basis);
        }

        [Fact]
        public void Scope_InterBodyEndpointsThirdBodyMember_StillInterBodyNotMalformed()
        {
            // A THIRD body among the members of an inter-body route is the RATIFIED transfer gap
            // (the Sun-frame mid-course leg), not a malformation: FilterLegsToEndpointBodies drops
            // it. The member-body cross-check must not run here.
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.InterBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    "Kerbin", new List<string> { "Duna" },
                    new List<string> { "Kerbin", "Sun", "Duna" }));
        }

        [Fact]
        public void Scope_MissingEndpointBodies_FallsBackToMemberBodies()
        {
            // Endpoints unreadable -> shipped v1 behaviour, basis MemberBodies.
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.SameBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    null, null, new List<string> { "Kerbin", "Kerbin" },
                    out RouteTrajectoryLineRenderer.RouteScopeBasis sameBasis));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteScopeBasis.MemberBodies, sameBasis);

            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.MalformedMixedBodies,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    null, null, new List<string> { "Kerbin", "Duna" },
                    out RouteTrajectoryLineRenderer.RouteScopeBasis mixedBasis));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteScopeBasis.MemberBodies, mixedBasis);

            // An origin body with NO readable stop body is equally unreadable.
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.MalformedMixedBodies,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    "Kerbin", new List<string> { null }, new List<string> { "Kerbin", "Duna" },
                    out RouteTrajectoryLineRenderer.RouteScopeBasis noStopBasis));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteScopeBasis.MemberBodies, noStopBasis);
        }

        [Fact]
        public void Scope_StoredPeriodHasNoSay_EitherDirection()
        {
            // Regression pin for the ROUTE-INTERBODY-SCOPE-NEVER-REACHABLE fix: the stored period
            // is informational. A same-body route carrying a legacy non-zero period still
            // classifies SameBody; an inter-body route at period 0 (what RouteBuilder writes)
            // classifies InterBody - the reading that was unreachable before.
            var sameBody = new Route
            {
                Id = "r-same",
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Kerbin" } } },
                DispatchWindowPeriod = 5000000.0,
            };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.SameBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    sameBody, new List<string> { "Kerbin" }));

            var interBody = new Route
            {
                Id = "r-inter",
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
                DispatchWindowPeriod = 0.0,
            };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.InterBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(interBody, null));
        }

        [Fact]
        public void Scope_NullRoute_SameBody()
        {
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.SameBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(
                    (Route)null, null, out RouteTrajectoryLineRenderer.RouteScopeBasis basis));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteScopeBasis.MemberBodies, basis);
        }

        [Fact]
        public void DestinationBodyLabel_LastKnownStopBodyOrNone()
        {
            Assert.Equal("<none>", RouteTrajectoryLineRenderer.DestinationBodyLabel(null));
            Assert.Equal("<none>",
                RouteTrajectoryLineRenderer.DestinationBodyLabel(new List<string> { null, "" }));
            Assert.Equal("Duna",
                RouteTrajectoryLineRenderer.DestinationBodyLabel(
                    new List<string> { "Mun", "Duna" }));
            Assert.Equal("Mun",
                RouteTrajectoryLineRenderer.DestinationBodyLabel(
                    new List<string> { "Mun", null }));
        }

        // --- ClassifyRouteLineSkip ---

        [Fact]
        public void Classify_NullRoute_NullRoute()
        {
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.NullRoute,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(
                    null, true, RouteTrajectoryLineRenderer.RouteLineScope.SameBody, 1));
        }

        [Fact]
        public void Classify_Disabled_Disabled()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.Disabled,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(
                    route, false, RouteTrajectoryLineRenderer.RouteLineScope.SameBody, 1));
        }

        [Fact]
        public void Classify_MalformedMixedBodies_Skipped()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.MalformedMixedBodies,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(
                    route, true, RouteTrajectoryLineRenderer.RouteLineScope.MalformedMixedBodies, 1));
        }

        [Fact]
        public void Classify_InterBodyWithMembers_None()
        {
            // Inter-body routes are drawable (endpoint-body legs) since the M6 follow-up.
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.None,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(
                    route, true, RouteTrajectoryLineRenderer.RouteLineScope.InterBody, 1));
        }

        [Fact]
        public void Classify_NoDrawableMembers_NoBackingRecordings()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.NoBackingRecordings,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(
                    route, true, RouteTrajectoryLineRenderer.RouteLineScope.SameBody, 0));
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.NoBackingRecordings,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(
                    route, true, RouteTrajectoryLineRenderer.RouteLineScope.InterBody, 0));
        }

        [Fact]
        public void Classify_AllGood_None()
        {
            var route = new Route { Id = "r1" };
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineSkipReason.None,
                RouteTrajectoryLineRenderer.ClassifyRouteLineSkip(
                    route, true, RouteTrajectoryLineRenderer.RouteLineScope.SameBody, 2));
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
                route, Resolver(rec), out int resolvable, out int totalLegs, out _);

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
                route, Resolver(a, b), out int resolvable, out int totalLegs, out _);

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
                route, Resolver(a), out int resolvable, out int totalLegs, out _);

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
                route, Resolver(a), out int resolvable, out int totalLegs, out _);

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
                route, Resolver(a, b), out int resolvable, out int totalLegs, out _);

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
                null, Resolver(), out _, out _, out _));
            var route = new Route { Id = "r1", RecordingIds = { "rec-a" } };
            Assert.Empty(RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, null, out _, out _, out _));
        }

        // --- Inter-body endpoint-body filter ---

        [Fact]
        public void Build_SameBodyRoute_MultiBodyLegsNotFiltered_ByteIdentity()
        {
            // A period-0 route is NEVER endpoint-filtered at build time, even when a member's legs
            // span bodies (the malformed-mixed-bodies guard handles that at draw classification).
            // Pins the shipped same-body build path byte-identical.
            var rec = InterBodyRecording("rec-a", 100.0);
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Kerbin" } } },
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(rec), out _, out int totalLegs, out int transferDropped);

            Assert.Single(groups);
            Assert.Equal(3, groups[0].legs.Length); // Kerbin + Sun + Duna legs all kept
            Assert.Equal(3, totalLegs);
            Assert.Equal(0, transferDropped);
        }

        [Fact]
        public void Build_InterBodyRoute_DropsTransferFrameLeg_KeepsEndpointLegs()
        {
            // Kerbin ascent -> Sun mid-course burn -> Duna approach: the Sun leg is transfer-frame
            // geometry the M5 re-aim replaces per window, so the static route line drops it and
            // keeps the origin + destination legs.
            var rec = InterBodyRecording("rec-a", 100.0);
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(rec), out _, out int totalLegs, out int transferDropped);

            Assert.Single(groups);
            Assert.Equal(2, groups[0].legs.Length);
            Assert.Equal("Kerbin", groups[0].legs[0].bodyName);
            Assert.Equal("Duna", groups[0].legs[1].bodyName);
            Assert.Equal(2, totalLegs);
            Assert.Equal(1, transferDropped);
        }

        [Fact]
        public void Build_InterBodyRoute_EndpointsResolvedAcrossMembers()
        {
            // Chain: member 1 ends on a Sun transfer-frame burn, member 2 is the Duna arrival.
            // Endpoint bodies resolve across ALL members (origin=Kerbin, dest=Duna), so member 1's
            // trailing Sun leg is dropped even though it is that member's own last leg.
            var m1 = new Recording { RecordingId = "rec-1", StartBodyName = "Kerbin" };
            m1.Points.Add(MakePoint(100.0, 0.0, -74.0, 5000.0));
            m1.Points.Add(MakePoint(150.0, 0.5, -74.0, 45000.0));
            m1.Points.Add(MakePoint(400.0, 10.0, 20.0, 2.0e9, "Sun"));
            m1.Points.Add(MakePoint(450.0, 10.5, 20.5, 2.0e9, "Sun"));
            var m2 = new Recording { RecordingId = "rec-2", StartBodyName = "Duna" };
            m2.Points.Add(MakePoint(900.0, 5.0, 30.0, 40000.0, "Duna"));
            m2.Points.Add(MakePoint(950.0, 5.5, 30.0, 20000.0, "Duna"));
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-1", "rec-2" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(m1, m2), out _, out int totalLegs, out int transferDropped);

            Assert.Equal(2, groups.Count);
            Assert.Single(groups[0].legs);
            Assert.Equal("Kerbin", groups[0].legs[0].bodyName);
            Assert.Single(groups[1].legs);
            Assert.Equal("Duna", groups[1].legs[0].bodyName);
            Assert.Equal(2, totalLegs);
            Assert.Equal(1, transferDropped);
        }

        [Fact]
        public void Build_InterBodyRoute_MemberLeftEmptyByFilter_Removed()
        {
            // A member contributing ONLY transfer-frame legs is dropped entirely.
            var m1 = new Recording { RecordingId = "rec-1", StartBodyName = "Kerbin" };
            m1.Points.Add(MakePoint(100.0, 0.0, -74.0, 5000.0));
            m1.Points.Add(MakePoint(150.0, 0.5, -74.0, 45000.0));
            var mid = new Recording { RecordingId = "rec-mid", StartBodyName = "Sun" };
            mid.Points.Add(MakePoint(400.0, 10.0, 20.0, 2.0e9, "Sun"));
            mid.Points.Add(MakePoint(450.0, 10.5, 20.5, 2.0e9, "Sun"));
            var m2 = new Recording { RecordingId = "rec-2", StartBodyName = "Duna" };
            m2.Points.Add(MakePoint(900.0, 5.0, 30.0, 40000.0, "Duna"));
            m2.Points.Add(MakePoint(950.0, 5.5, 30.0, 20000.0, "Duna"));
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-1", "rec-mid", "rec-2" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(m1, mid, m2), out int resolvable, out int totalLegs,
                out int transferDropped);

            Assert.Equal(3, resolvable);
            Assert.Equal(2, groups.Count);
            Assert.DoesNotContain(groups, g => g.memberRecordingId == "rec-mid");
            Assert.Equal(2, totalLegs);
            Assert.Equal(1, transferDropped);
        }

        [Fact]
        public void Build_InterBodyRoute_DockClipStillApplies()
        {
            // The dock clip runs before the endpoint filter: a Duna leg starting at/after the dock
            // UT is dropped by the clip, exactly as for same-body routes.
            var rec = InterBodyRecording("rec-a", 100.0);
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
                // Dock before the Duna leg's start (InterBodyRecording puts Duna at +800..+900).
                RecordedDockUT = 100.0 + 700.0,
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(rec), out _, out int totalLegs, out int transferDropped);

            // Clip leaves Kerbin + Sun; endpoints resolve to Kerbin (origin) + Sun (latest) and
            // nothing is between them, so both survive (documented degenerate heuristic).
            Assert.Single(groups);
            Assert.Equal(2, totalLegs);
            Assert.Equal(0, transferDropped);
            Assert.DoesNotContain(groups[0].legs, l => l.bodyName == "Duna");
        }

        [Fact]
        public void Build_InterBodyRoundTrip_OriginEqualsDestination_FilterStandsDown()
        {
            // Kerbin -> Sun -> Duna -> Kerbin with no dock clip: endpoints resolve to the SAME
            // body, and filtering on that pair would drop the entire far-body (Duna) arc — the
            // geometry the route exists to show. The filter stands down and keeps everything.
            var rec = new Recording { RecordingId = "rec-a", StartBodyName = "Kerbin" };
            rec.Points.Add(MakePoint(100.0, 0.0, -74.0, 5000.0));
            rec.Points.Add(MakePoint(150.0, 0.5, -74.0, 45000.0));
            rec.Points.Add(MakePoint(400.0, 10.0, 20.0, 2.0e9, "Sun"));
            rec.Points.Add(MakePoint(450.0, 10.5, 20.5, 2.0e9, "Sun"));
            rec.Points.Add(MakePoint(800.0, 5.0, 30.0, 40000.0, "Duna"));
            rec.Points.Add(MakePoint(900.0, 5.5, 30.0, 20000.0, "Duna"));
            rec.Points.Add(MakePoint(1200.0, 1.0, -70.0, 30000.0));
            rec.Points.Add(MakePoint(1300.0, 1.5, -70.0, 5000.0));
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolver(rec), out _, out int totalLegs, out int transferDropped);

            Assert.Single(groups);
            Assert.Equal(4, groups[0].legs.Length); // Kerbin + Sun + Duna + Kerbin, all kept
            Assert.Equal(4, totalLegs);
            Assert.Equal(0, transferDropped);
        }

        [Fact]
        public void ResolveEndpointBodies_NoLegs_False()
        {
            Assert.False(RouteTrajectoryLineRenderer.ResolveEndpointBodies(
                new List<RouteTrajectoryLineRenderer.RouteMemberLegs>(), out _, out _));
            Assert.False(RouteTrajectoryLineRenderer.ResolveEndpointBodies(null, out _, out _));
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
        public void Signature_ChangesWhenEndpointBodiesFlipScope()
        {
            // The endpoint bodies ARE the scope authority now, so a scope flip must invalidate the
            // cached line through them (the period fold used to carry this).
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Kerbin" } } },
            };
            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            route.Stops[0].Endpoint = new RouteEndpoint { BodyName = "Duna" };
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Signature_ChangesWhenOriginBodyChanges()
        {
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route
            {
                Id = "r1",
                RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
            };
            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            route.Origin = new RouteEndpoint { BodyName = "Eve" };
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Signature_InterBody_ChangesOnScheduleChange()
        {
            // Window/schedule changes must rebuild an inter-body route's line. The route is
            // inter-body by its ENDPOINTS; the period is one of the folded schedule scalars, not
            // the thing that decides whether the fold happens.
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route
            {
                Id = "r1", RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
                DispatchWindowPeriod = 5000.0, DispatchWindowEpochUT = 1000.0, CadenceMultiplier = 1,
            };
            long baseline = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            route.DispatchWindowPeriod = 6000.0;
            long periodChanged = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));
            route.DispatchWindowPeriod = 5000.0;
            route.DispatchWindowEpochUT = 2000.0;
            long epochChanged = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));
            route.DispatchWindowEpochUT = 1000.0;
            route.CadenceMultiplier = 3;
            long cadenceChanged = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            Assert.NotEqual(baseline, periodChanged);
            Assert.NotEqual(baseline, epochChanged);
            Assert.NotEqual(baseline, cadenceChanged);
        }

        [Fact]
        public void Signature_SameBody_IgnoresScheduleFields()
        {
            // A same-body route folds NO schedule field - its signature is unchanged by epoch /
            // cadence / period values, exactly as the shipped v1 behaved for period 0.
            var rec = FlatRecording("rec-a", 100.0, 200.0);
            var route = new Route
            {
                Id = "r1", RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Kerbin" } } },
                DispatchWindowPeriod = 0.0, DispatchWindowEpochUT = 1000.0, CadenceMultiplier = 1,
            };
            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            route.DispatchWindowEpochUT = 9999.0;
            route.CadenceMultiplier = 5;
            route.DispatchWindowPeriod = 5000000.0;
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolver(rec));

            Assert.Equal(before, after);
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
        public void Refresh_InterBodyScheduleChange_Rebuilds()
        {
            var rec = InterBodyRecording("rec-a", 100.0);
            var route = new Route
            {
                Id = "r1", RecordingIds = { "rec-a" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Duna" } } },
                DispatchWindowPeriod = 5000.0, DispatchWindowEpochUT = 1000.0,
            };

            RouteTrajectoryLineRenderer.RefreshForRouteForTesting(route, Resolver(rec));
            int buildsBefore = RouteTrajectoryLineRenderer.BuildInvocationCountForTesting;

            route.DispatchWindowEpochUT = 2000.0;
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

        /// <summary>
        /// An inter-body run shape: Kerbin ascent leg [t0, t0+100], Sun transfer-frame burn leg
        /// [t0+400, t0+500], Duna approach leg [t0+800, t0+900]. Legs split on the body change, so
        /// <c>BuildLegsForRecording</c> yields exactly three legs.
        /// </summary>
        private static Recording InterBodyRecording(string id, double t0)
        {
            var rec = new Recording { RecordingId = id, StartBodyName = "Kerbin" };
            rec.Points.Add(MakePoint(t0, -0.1, -74.5, 70.0));
            rec.Points.Add(MakePoint(t0 + 100.0, 0.5, -74.0, 45000.0));
            rec.Points.Add(MakePoint(t0 + 400.0, 10.0, 20.0, 2.0e9, "Sun"));
            rec.Points.Add(MakePoint(t0 + 500.0, 10.5, 20.5, 2.0e9, "Sun"));
            rec.Points.Add(MakePoint(t0 + 800.0, 5.0, 30.0, 40000.0, "Duna"));
            rec.Points.Add(MakePoint(t0 + 900.0, 5.5, 30.0, 20000.0, "Duna"));
            return rec;
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
