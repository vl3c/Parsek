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
    /// ROUTE-LINE-OWNERSHIP-ARM-IS-STILL-WHOLE-MEMBER: the route overview line's OWNERSHIP arm used
    /// to stand a member's WHOLE recorded path down the moment the ghost polyline owned the
    /// recording's non-orbital phase, because the ownership publish carried no UT span. The owning
    /// draw now publishes its recorded span at the SAME feeder as the ownership id, and the arm
    /// arbitrates per LEG through the same <see cref="GhostTrajectoryPolylineRenderer.LegSpansOverlap"/>
    /// rule the PAINT arm uses.
    ///
    /// <para>Two layers: the pure predicate, and the live <c>DrawAll</c> loop's own
    /// <c>Route line draw:</c> summary counters read off a log sink. The second layer is reachable
    /// headlessly because a null body resolver makes every surviving leg skip its Vectrosity draw
    /// AFTER both arbitration arms have run, which is exactly the part under test.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteLineOwnershipSpanArbitrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> addedRouteIds = new List<string>();

        public RouteLineOwnershipSpanArbitrationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RouteTrajectoryLineRenderer.ResetForTesting();
            GhostTrajectoryPolylineRenderer.Clear();
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            for (int i = 0; i < addedRouteIds.Count; i++)
                RouteStore.RemoveRoute(addedRouteIds[i]);
            RouteTrajectoryLineRenderer.ResetForTesting();
            GhostTrajectoryPolylineRenderer.Clear();
            RecordingStore.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = null;
            ParsekLog.ResetTestOverrides();
        }

        // ==============================================================
        // Layer 1: the pure predicate
        // ==============================================================

        [Fact]
        public void ResolveOwnedLegOverlap_NotOwned_NeverStandsALegDown()
        {
            Assert.False(GhostTrajectoryPolylineRenderer.ResolveOwnedLegOverlap(
                owned: false, haveSpan: true,
                ownedStartUT: 100.0, ownedEndUT: 400.0, legStartUT: 100.0, legEndUT: 200.0));
        }

        [Fact]
        public void ResolveOwnedLegOverlap_OwnedWithoutASpan_StandsTheLegDown()
        {
            // FAIL-CLOSED: ownership without a span is a broken publish contract (the one feeder
            // writes both), and the safe direction is the shipped v1 whole-member stand-down rather
            // than a second identical line over a live ghost mesh.
            Assert.True(GhostTrajectoryPolylineRenderer.ResolveOwnedLegOverlap(
                owned: true, haveSpan: false,
                ownedStartUT: 0.0, ownedEndUT: 0.0, legStartUT: 100.0, legEndUT: 200.0));
        }

        [Fact]
        public void ResolveOwnedLegOverlap_IsTheSameOverlapRuleAsThePaintArm()
        {
            // One shared predicate, so the two arms cannot drift apart on endpoint handling. The
            // table deliberately includes the touching-endpoint pair adjacent legs of one recording
            // produce (strict on both ends: a shared endpoint UT is not a second line).
            double[][] pairs =
            {
                new[] { 100.0, 200.0, 100.0, 200.0 }, // identical
                new[] { 100.0, 200.0, 150.0, 250.0 }, // partial
                new[] { 100.0, 400.0, 150.0, 250.0 }, // contained
                new[] { 100.0, 200.0, 200.0, 300.0 }, // touching endpoint -> NOT an overlap
                new[] { 100.0, 200.0, 300.0, 400.0 }, // disjoint
                new[] { 300.0, 400.0, 100.0, 200.0 }, // disjoint, mirrored
            };
            foreach (double[] p in pairs)
            {
                bool shared = GhostTrajectoryPolylineRenderer.LegSpansOverlap(p[0], p[1], p[2], p[3]);
                Assert.Equal(shared, GhostTrajectoryPolylineRenderer.ResolveOwnedLegOverlap(
                    owned: true, haveSpan: true,
                    ownedStartUT: p[0], ownedEndUT: p[1], legStartUT: p[2], legEndUT: p[3]));
            }
            // ... and the two named cases, spelled out so a deleted table still reads.
            Assert.True(GhostTrajectoryPolylineRenderer.ResolveOwnedLegOverlap(
                true, true, 100.0, 200.0, 150.0, 250.0));
            Assert.False(GhostTrajectoryPolylineRenderer.ResolveOwnedLegOverlap(
                true, true, 100.0, 200.0, 200.0, 300.0));
        }

        [Fact]
        public void IsOwningNonOrbitalLegSpan_PublishedThenCleared_UnStandsTheLeg()
        {
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-a", inDrewSet: true, ownedStartUT: 300.0, ownedEndUT: 400.0);

            Assert.True(GhostTrajectoryPolylineRenderer.IsOwningNonOrbitalLegSpan("rec-a", 300.0, 400.0));
            Assert.False(GhostTrajectoryPolylineRenderer.IsOwningNonOrbitalLegSpan("rec-a", 100.0, 200.0));
            // Ownership itself - the question GhostMapPresence asks to hide a proto orbit line - is
            // unchanged by the span: it is still "does the polyline own this recording's phase".
            Assert.True(GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg("rec-a"));

            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-a", inDrewSet: false, ownedStartUT: 300.0, ownedEndUT: 400.0);

            Assert.False(GhostTrajectoryPolylineRenderer.IsOwningNonOrbitalLegSpan("rec-a", 300.0, 400.0));
            Assert.False(GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg("rec-a"));
        }

        [Fact]
        public void IsOwningNonOrbitalLegSpan_OwnedWithoutASpan_LogsTheGuardAndStandsDownWhole()
        {
            // The span-less seam models the broken publish; the guard skip must be logged.
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting("rec-a", inDrewSet: true);

            Assert.True(GhostTrajectoryPolylineRenderer.IsOwningNonOrbitalLegSpan("rec-a", 100.0, 200.0));
            Assert.Contains(logLines, l =>
                l.Contains("[GhostMap]") && l.Contains("Polyline owned-span missing")
                && l.Contains("rec=rec-a"));
        }

        // ==============================================================
        // Layer 2: the live DrawAll loop's summary counters
        // ==============================================================

        [Fact]
        public void DrawAll_MultiLegMemberTheGhostIsFlying_StandsDownOnlyTheOwnedLeg()
        {
            // THE DEFECT, on the shape that shows it: a three-leg member whose MIDDLE leg the ghost
            // is drawing. v1 read ownedLegs=3 (the whole member); the per-leg arm reads 1, and the
            // route line keeps the other two legs - the hole this closes.
            Recording rec = ThreeLegRecording("rec-3");
            SetUpRoute("r-multi", rec, out RouteTrajectoryLineRenderer.RouteMemberLegs group);
            Assert.Equal(3, group.legs.Length);

            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-3", inDrewSet: true,
                ownedStartUT: group.legs[1].startUT, ownedEndUT: group.legs[1].endUT);

            string line = DrawOnce(frame: 11);
            Assert.Contains("ownedLegs=1", line);
            Assert.Contains("skippedOwned=1", line);
            Assert.Contains("paintedLegs=0", line);
        }

        [Fact]
        public void DrawAll_OneLegMemberOwned_StillReadsSkippedOwnedOne()
        {
            // H59's ARMED census pin: on a one-leg member the two granularities agree and the
            // counters must NOT move.
            Recording rec = OneLegRecording("rec-1");
            SetUpRoute("r-one", rec, out RouteTrajectoryLineRenderer.RouteMemberLegs group);
            Assert.Single(group.legs);

            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-1", inDrewSet: true,
                ownedStartUT: group.legs[0].startUT, ownedEndUT: group.legs[0].endUT);

            string line = DrawOnce(frame: 21);
            Assert.Contains("skippedOwned=1", line);
            Assert.Contains("ownedLegs=1", line);
        }

        [Fact]
        public void DrawAll_MemberWithNoGhostSlot_IsUntouched()
        {
            // MIRROR: nothing published for this recording at all. Neither arm may stand a leg down.
            Recording rec = ThreeLegRecording("rec-3");
            SetUpRoute("r-none", rec, out RouteTrajectoryLineRenderer.RouteMemberLegs group);
            Assert.Equal(3, group.legs.Length);

            string line = DrawOnce(frame: 31);
            Assert.Contains("skippedOwned=0", line);
            Assert.Contains("ownedLegs=0", line);
            Assert.Contains("paintedLegs=0", line);
        }

        [Fact]
        public void DrawAll_OwnershipClearedNextFrame_UnStandsTheLeg()
        {
            Recording rec = ThreeLegRecording("rec-3");
            SetUpRoute("r-clear", rec, out RouteTrajectoryLineRenderer.RouteMemberLegs group);

            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting(
                "rec-3", inDrewSet: true,
                ownedStartUT: group.legs[0].startUT, ownedEndUT: group.legs[0].endUT);
            Assert.Contains("ownedLegs=1", DrawOnce(frame: 41));

            // The Driver clears BOTH the id set and the span at the top of every LateUpdate.
            GhostTrajectoryPolylineRenderer.Clear();
            Assert.Contains("ownedLegs=0", DrawOnce(frame: 42));
        }

        // ---- helpers ----

        /// <summary>
        /// Runs the live <c>DrawAll</c> with a NULL body resolver - every surviving leg then skips
        /// its Vectrosity draw AFTER both arbitration arms have run - and returns the
        /// <c>Route line draw:</c> summary line. The rate limit is reset first so consecutive frames
        /// in one cell each emit.
        /// </summary>
        private string DrawOnce(int frame)
        {
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            RouteTrajectoryLineRenderer.DrawAll(frame, targetLayer: 31, resolveBody: name => null);
            string line = logLines.Find(l => l.Contains("Route line draw:"));
            Assert.True(line != null, "DrawAll emitted no 'Route line draw:' summary line");
            return line;
        }

        /// <summary>Commits <paramref name="rec"/>, commits a one-member route over it, and hands
        /// back the group the route line built for that member.</summary>
        private void SetUpRoute(
            string routeId, Recording rec, out RouteTrajectoryLineRenderer.RouteMemberLegs group)
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings { showRouteLines = true };
            RecordingStore.AddCommittedInternal(rec);

            var route = new Route
            {
                Id = routeId,
                RecordingIds = { rec.RecordingId },
                RecordedDockUT = -1.0,
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Kerbin" } } },
            };
            RouteStore.AddRoute(route);
            addedRouteIds.Add(routeId);

            List<RouteTrajectoryLineRenderer.RouteMemberLegs> groups =
                RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                    route, id => RecordingStore.TryFindCommittedRecordingById(id),
                    out _, out _, out _);
            Assert.Single(groups);
            group = groups[0];
        }

        /// <summary>A same-body recording whose non-orbital samples are split into THREE legs by two
        /// intervening orbital coasts (the leg builder starts a new leg when an OrbitSegment lies
        /// between two consecutive non-orbital samples).</summary>
        private static Recording ThreeLegRecording(string id)
        {
            var rec = new Recording { RecordingId = id, StartBodyName = "Kerbin" };
            AddLegPoints(rec, 100.0, 200.0);
            AddLegPoints(rec, 300.0, 400.0);
            AddLegPoints(rec, 500.0, 600.0);
            rec.OrbitSegments.Add(new OrbitSegment
            { startUT = 200.5, endUT = 299.5, bodyName = "Kerbin", semiMajorAxis = 800000.0 });
            rec.OrbitSegments.Add(new OrbitSegment
            { startUT = 400.5, endUT = 499.5, bodyName = "Kerbin", semiMajorAxis = 800000.0 });
            return rec;
        }

        private static Recording OneLegRecording(string id)
        {
            var rec = new Recording { RecordingId = id, StartBodyName = "Kerbin" };
            AddLegPoints(rec, 100.0, 200.0);
            return rec;
        }

        private static void AddLegPoints(Recording rec, double startUT, double endUT)
        {
            rec.Points.Add(MakePoint(startUT, -0.1, -74.5, 70.0));
            rec.Points.Add(MakePoint((startUT + endUT) * 0.5, -0.05, -74.5, 20000.0));
            rec.Points.Add(MakePoint(endUT, 0.0, -74.5, 100000.0));
        }

        private static TrajectoryPoint MakePoint(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            };
        }
    }
}
