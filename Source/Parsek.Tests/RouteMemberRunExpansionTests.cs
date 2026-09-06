using System;
using System.Collections.Generic;
using System.IO;
using Parsek.Display;
using Parsek.Logistics;
using Parsek.Tests.Analyzer;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// ROUTE-LINE-MEMBER-DROPS-CONTINUATION-SEGMENTS: a route member is a composition RUN HEAD, so
    /// the route line must draw the member's whole continuation run, not just its first segment.
    /// Covers the pure expansion (<see cref="RouteMemberRunExpansion"/>) and the renderer builder
    /// driven with an expander, including the committed <c>interbody-route-recorded</c> fixture -
    /// the subject whose Sun-frame transfer legs the head-only build could not reach, which is why
    /// <c>transferDropped</c> was structurally 0 on every chained subject.
    /// </summary>
    [Collection("Sequential")]
    public class RouteMemberRunExpansionTests : IDisposable
    {
        public RouteMemberRunExpansionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = true;
            RouteTrajectoryLineRenderer.ResetForTesting();
            GhostTrajectoryPolylineRenderer.Clear();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = false;
            RouteTrajectoryLineRenderer.ResetForTesting();
            GhostTrajectoryPolylineRenderer.Clear();
        }

        // ==============================================================
        // Pure expansion
        // ==============================================================

        [Fact]
        public void SuccessorIdsAfter_ChainedRun_ReturnsTheContinuationsInWalkOrder()
        {
            RecordingTree tree = ChainTree();
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(tree));

            var after = RouteMemberRunExpansion.SuccessorIdsAfter(view, "head");

            Assert.Equal(new[] { "seg1", "seg2" }, after);
            // From the middle of the run: only what follows it.
            Assert.Equal(new[] { "seg2" },
                RouteMemberRunExpansion.SuccessorIdsAfter(view, "seg1"));
            // The tail, and an id that is no leg at all.
            Assert.Empty(RouteMemberRunExpansion.SuccessorIdsAfter(view, "seg2"));
            Assert.Null(RouteMemberRunExpansion.SuccessorIdsAfter(view, "not-a-leg"));
        }

        [Fact]
        public void BuildRunClaims_TwoHeadDockMerge_GivesTheMergedRunToOneHeadOnly()
        {
            // REVIEW F1: MissionStructure.BuildBranchLinks gives a Dock branch point's single merged
            // child to BOTH parents' BranchChildIds and marks it IsBranchContinuation, so
            // ContinuationSuccessor returns it for both and MissionThroughLineBuilder - which has no
            // shared visited set - lists it in TWO runs. MissionComposition, the producer of
            // Route.RecordingIds, uses a shared visited set (first head wins), so the expander must
            // too or it walks a run from a head the member set does not own it under.
            RecordingTree tree = DockMergeTree();
            var view = MissionThroughLineBuilder.Build(MissionStructureBuilder.Build(tree));

            // The disagreement is real: the raw view lists the merged leg under both heads.
            Assert.Contains("merged", view.ByHeadId["a0"].MemberLegIds);
            Assert.Contains("merged", view.ByHeadId["b0"].MemberLegIds);

            var claims = RouteMemberRunExpansion.BuildRunClaims(view);

            // First head wins, and "first" is composition's order: roots by (StartUT, id), so a0
            // (1000.0) claims the merged run and b0 (1010.0) stops before it.
            Assert.Equal("a0", claims.HeadByMemberId["merged"]);
            Assert.Equal(new[] { "a0", "merged" }, claims.RunByHeadId["a0"].ToArray());
            Assert.Equal(new[] { "b0" }, claims.RunByHeadId["b0"].ToArray());
            Assert.Equal(1, claims.ContestedMembers);

            // ... which is what the expansion reads: b0's run ends at b0, a0's carries the merge.
            Assert.Equal(new[] { "merged" },
                RouteMemberRunExpansion.SuccessorIdsAfter(claims, "a0").ToArray());
            Assert.Empty(RouteMemberRunExpansion.SuccessorIdsAfter(claims, "b0"));

            // AGREEMENT with the member-set producer, mechanically: composition's own shared-visited
            // walk carries a0's tree past the dock to the merged leg's end and leaves b0's at the
            // dock. Read over each root's whole subtree, since a structural interval boundary splits
            // the run into a node chain rather than one node.
            var roots = MissionCompositionBuilder.Build(MissionStructureBuilder.Build(tree));
            MissionCompositionNode a0 = roots.Find(n => n.HeadLegId == "a0");
            MissionCompositionNode b0 = roots.Find(n => n.HeadLegId == "b0");
            Assert.NotNull(a0);
            Assert.NotNull(b0);
            Assert.Equal(1200.0, SubtreeEndUT(a0), 3);
            Assert.Equal(1100.0, SubtreeEndUT(b0), 3);
        }

        [Fact]
        public void ExpandVisibleRun_TwoHeadDockMerge_WalksOnlyFromTheClaimingHead()
        {
            RecordingTree tree = DockMergeTree();

            var fromA = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "a0", tree.Recordings["a0"], Resolve(tree), out var tallyA);
            var fromB = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "b0", tree.Recordings["b0"], Resolve(tree), out var tallyB);

            Assert.Equal(new[] { "a0", "merged" }, Ids(fromA));
            Assert.Equal(1, tallyA.Segments);
            Assert.Equal(new[] { "b0" }, Ids(fromB));
            Assert.Equal(0, tallyB.Segments);
        }

        [Fact]
        public void ApplyRouteFilters_FreshTally_NeverReportsNegativeSegments()
        {
            // REVIEW F3: the live expander memoizes the visible run and re-runs the filters per call
            // with a FRESH tally, so the old blind decrement reported segments=-2. Segments means
            // "kept after the head" and is clamped; Dropped is the filter's own counter.
            RecordingTree tree = ChainTree();
            var visibleRun = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "head", tree.Recordings["head"], Resolve(tree), out _);
            Assert.Equal(3, visibleRun.Count);

            var fresh = default(RouteMemberRunExpansion.RunTally);
            var kept = RouteMemberRunExpansion.ApplyRouteFilters(
                visibleRun, new[] { "head" }, null, ref fresh);

            Assert.Single(kept);
            Assert.Equal(0, fresh.Segments);
            Assert.Equal(2, fresh.Dropped);
        }

        [Fact]
        public void ExpandVisibleRun_SingleSegmentRun_IsTheHeadAlone()
        {
            var head = Rec("solo", "C", 0, 100.0, 200.0);
            RecordingTree tree = Tree("t-solo", head);

            var run = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "solo", head, Resolve(head), out var tally);

            Assert.Single(run);
            Assert.Same(head, run[0]);
            Assert.Equal(0, tally.Segments);
        }

        [Fact]
        public void ExpandVisibleRun_NullTree_IsTheHeadAlone()
        {
            // An unresolvable owning tree keeps the shipped head-only shape rather than guessing.
            var head = Rec("solo", "C", 0, 100.0, 200.0);

            var run = RouteMemberRunExpansion.ExpandVisibleRun(
                null, "solo", head, Resolve(head), out var tally);

            Assert.Single(run);
            Assert.Equal(0, tally.Segments);
        }

        [Fact]
        public void ExpandVisibleRun_SupersededSegment_StopsAtTheHiddenOne()
        {
            // The visible resolver IS the visibility authority (live: the ERS index). A segment it
            // does not answer for is superseded / rewind-retired / not committed, and the walk stops
            // there instead of skipping it - past that point the visible through-line has ended.
            RecordingTree tree = ChainTree();
            var visible = new Dictionary<string, Recording>(StringComparer.Ordinal);
            foreach (var kv in tree.Recordings)
                if (kv.Key != "seg1") visible[kv.Key] = kv.Value;

            var run = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "head", tree.Recordings["head"],
                id => visible.TryGetValue(id, out var r) ? r : null, out var tally);

            Assert.Single(run);
            Assert.Equal("head", run[0].RecordingId);
            Assert.Equal(1, tally.StoppedHidden);
            // seg2 is visible but unreachable: the run is the vessel's through-line, not a set.
            Assert.DoesNotContain(run, r => r.RecordingId == "seg2");
        }

        [Fact]
        public void ApplyRouteFilters_SegmentUnknownAtCreation_StopsTheRun()
        {
            RecordingTree tree = ChainTree();
            var full = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "head", tree.Recordings["head"], Resolve(tree), out var tally);
            Assert.Equal(3, full.Count);

            // A post-creation branch (re-fly fork / switch-fly continuation) mints ids outside the
            // creation snapshot; the backing mission auto-excludes exactly those.
            var creationKnown = new HashSet<string>(StringComparer.Ordinal) { "head", "seg1" };
            var filtered = RouteMemberRunExpansion.ApplyRouteFilters(
                full, creationKnown, null, ref tally);

            Assert.Equal(new[] { "head", "seg1" }, Ids(filtered));
            Assert.Equal(1, tally.StoppedUnknownAtCreation);
            Assert.Equal(1, tally.Segments);
        }

        [Fact]
        public void ApplyRouteFilters_EmptyCreationSet_FailsOpen()
        {
            RecordingTree tree = ChainTree();
            var full = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "head", tree.Recordings["head"], Resolve(tree), out var tally);

            var filtered = RouteMemberRunExpansion.ApplyRouteFilters(
                full, new HashSet<string>(), null, ref tally);

            // No snapshot (degenerate / pre-field route) = fail open, the RouteRunCostCalculator
            // contract for the same set.
            Assert.Equal(new[] { "head", "seg1", "seg2" }, Ids(filtered));
        }

        [Fact]
        public void ApplyRouteFilters_WholeRecordingExclusion_StopsTheRun_SubIntervalKeyDoesNot()
        {
            RecordingTree tree = ChainTree();
            var full = RouteMemberRunExpansion.ExpandVisibleRun(
                tree, "head", tree.Recordings["head"], Resolve(tree), out var tally);

            var stopped = RouteMemberRunExpansion.ApplyRouteFilters(
                full, null, new HashSet<string>(StringComparer.Ordinal) { "seg1" }, ref tally);
            Assert.Equal(new[] { "head" }, Ids(stopped));
            Assert.Equal(1, tally.StoppedExcluded);

            // A "/segN" or "@dockM" key names a SUB-interval of a recording the renderer cannot
            // cut; it is not a whole-recording exclusion and must not start dropping a segment.
            Assert.False(RouteMemberRunExpansion.IsWholeRecordingExcluded(
                new HashSet<string>(StringComparer.Ordinal) { "seg1/seg3", "seg1@dock0" }, "seg1"));
            Assert.True(RouteMemberRunExpansion.IsWholeRecordingExcluded(
                new HashSet<string>(StringComparer.Ordinal) { "seg1" }, "seg1"));
        }

        // ==============================================================
        // Renderer builder, driven with an expander
        // ==============================================================

        [Fact]
        public void Build_ChainedMember_YieldsLegsFromEverySegment()
        {
            // Same-body route (Kerbin -> Kerbin): the mirror direction of the inter-body case. The
            // hidden continuations become legs and NOTHING is dropped.
            RecordingTree tree = ChainTree();
            var route = new Route
            {
                Id = "r-chained",
                RecordingIds = { "head" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Kerbin" } } },
                RecordedDockUT = -1.0,
            };

            var headOnly = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), out int m0, out int legs0, out int dropped0);
            var expanded = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), Expander(route, tree),
                out int m1, out int legs1, out int dropped1, out int segments1);

            Assert.Single(headOnly);
            Assert.Equal(1, legs0);
            Assert.Equal(3, expanded.Count);
            Assert.Equal(3, legs1);
            Assert.Equal(2, segments1);
            // The declared member count is unchanged - members= still means "declared members that
            // resolved", and each segment is its own group under its OWN recording id.
            Assert.Equal(m0, m1);
            Assert.Equal(new[] { "head", "seg1", "seg2" },
                expanded.ConvertAll(g => g.memberRecordingId).ToArray());
            Assert.Equal("seg1", expanded[1].rec.RecordingId);
            Assert.Equal(0, dropped0);
            Assert.Equal(0, dropped1);
        }

        [Fact]
        public void Build_SingleSegmentRun_IsByteIdenticalToTheHeadOnlyBuild()
        {
            var solo = Rec("solo", "C", 0, 100.0, 200.0);
            RecordingTree tree = Tree("t-solo", solo);
            var route = new Route { Id = "r-solo", RecordingIds = { "solo" }, RecordedDockUT = -1.0 };

            var headOnly = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), out int m0, out int legs0, out int dropped0);
            var expanded = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), Expander(route, tree),
                out int m1, out int legs1, out int dropped1, out int segments1);

            Assert.Equal(headOnly.Count, expanded.Count);
            Assert.Equal(m0, m1);
            Assert.Equal(legs0, legs1);
            Assert.Equal(dropped0, dropped1);
            Assert.Equal(0, segments1);
        }

        [Fact]
        public void Build_SameBodyRoute_ContinuationOnAnotherBody_DoesNotTurnTheRouteMalformed()
        {
            // THE MIRROR DIRECTION the fix owes: ClassifyRouteScope's member-body consistency read
            // is fed from the resolved groups, so if the expansion's segments fed it too, ONE
            // continuation on another body would classify a declared same-body route
            // MalformedMixedBodies and its line - which drew before - would vanish. Only DECLARED
            // members feed it, so the verdict is the head-only one.
            var head = Rec("head", "chain-b", 0, 1000.0, 1100.0);
            var seg = Rec("seg1", "chain-b", 1, 1100.0, 1200.0);
            seg.StartBodyName = "Mun";
            foreach (var idx in new[] { 0, 1, 2 })
            {
                TrajectoryPoint p = seg.Points[idx];
                p.bodyName = "Mun";
                seg.Points[idx] = p;
            }
            RecordingTree tree = Tree("t-mixed", head, seg);
            var route = new Route
            {
                Id = "r-mixed",
                RecordingIds = { "head" },
                Origin = new RouteEndpoint { BodyName = "Kerbin" },
                Stops = { new RouteStop { Endpoint = new RouteEndpoint { BodyName = "Kerbin" } } },
                RecordedDockUT = -1.0,
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), Expander(route, tree),
                out _, out int legs, out _, out int segments);

            Assert.Equal(1, segments);
            Assert.Equal(2, legs);                       // the Mun continuation IS drawn
            var bodies = RouteTrajectoryLineRenderer.CollectMemberBodies(groups);
            Assert.Equal(new[] { "Kerbin" }, bodies);    // ... but only the declared member is read
            Assert.Equal(RouteTrajectoryLineRenderer.RouteLineScope.SameBody,
                RouteTrajectoryLineRenderer.ClassifyRouteScope(route, bodies));
        }

        [Fact]
        public void Build_ExpandedMemberThatIsAlsoDeclared_CountsAsDeclaredOnce()
        {
            // A declared member can also BE a continuation segment of an earlier member's run (a
            // dock-merged child under its parent). It must be drawn once, counted once as a member,
            // and still read as DECLARED by the scope cross-check.
            RecordingTree tree = ChainTree();
            var route = new Route
            {
                Id = "r-both",
                RecordingIds = { "head", "seg1" },
                RecordedDockUT = -1.0,
            };

            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), Expander(route, tree),
                out int members, out int legs, out _, out int segments);

            Assert.Equal(2, members);
            Assert.Equal(3, groups.Count);               // head, seg1, seg2 - seg1 not doubled
            Assert.Equal(3, legs);
            Assert.Equal(2, segments);
            Assert.Equal(new[] { "Kerbin", "Kerbin" },
                RouteTrajectoryLineRenderer.CollectMemberBodies(groups));
        }

        [Fact]
        public void Signature_ContinuationSegmentContentChange_InvalidatesTheCachedLine()
        {
            RecordingTree tree = ChainTree();
            var route = new Route { Id = "r-sig", RecordingIds = { "head" }, RecordedDockUT = -1.0 };

            long before = RouteTrajectoryLineRenderer.ComputeRouteSignature(
                route, Resolve(tree), Expander(route, tree));
            tree.Recordings["seg2"].Points.Add(Point(2600.0, 0.2, -74.0, 90000.0));
            long after = RouteTrajectoryLineRenderer.ComputeRouteSignature(
                route, Resolve(tree), Expander(route, tree));

            Assert.NotEqual(before, after);
            // The head-only signature cannot see it - which is why the run-aware fold exists.
            Assert.Equal(
                RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolve(tree)),
                RouteTrajectoryLineRenderer.ComputeRouteSignature(route, Resolve(tree), null));
        }

        // ==============================================================
        // The committed fixture: interbody-route-recorded
        // ==============================================================

        /// <summary>
        /// THE READING THIS DEFECT BLOCKED. On the committed <c>interbody-route-recorded</c> save,
        /// the Kerbin -&gt; Duna route's member <c>d23e453b</c> is chain index 0 of a run whose index
        /// 1 (<c>36c7688b</c>) carries the whole 8.5 Ms journey INCLUDING the Sun-frame samples. The
        /// head-only build reports <c>transferDropped=0</c> because that segment is unreachable;
        /// with the run expansion the Sun legs exist and <c>FilterLegsToEndpointBodies</c> drops
        /// them. Both builds run here against the same bytes, so the cell IS the before/after.
        /// </summary>
        [Fact]
        public void Build_InterbodyFixture_ExpandedRunDropsTheThirdBodyLegs()
        {
            string saveDir = FixtureSaveDir("interbody-route-recorded");
            Assert.True(Directory.Exists(saveDir), "fixture save not found at " + saveDir);

            AnalyzerModel model = SaveDirectoryLoader.Load(saveDir, _ => null);
            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            foreach (Recording rec in model.Recordings)
                if (rec != null && !string.IsNullOrEmpty(rec.RecordingId))
                    byId[rec.RecordingId] = rec;

            Route route = LoadFixtureRoute(saveDir, "71a983a1");
            Assert.NotNull(route);
            RecordingTree tree = null;
            foreach (RecordingTree candidate in model.Trees)
                if (candidate != null && candidate.Id == route.BackingMissionTreeId)
                    tree = candidate;
            Assert.NotNull(tree);

            Func<string, Recording> resolve = id =>
                id != null && byId.TryGetValue(id, out var r) ? r : null;
            Func<string, IReadOnlyList<Recording>> expander = memberId =>
            {
                Recording head = resolve(memberId);
                if (head == null) return null;
                return RouteMemberRunExpansion.ExpandRunForRoute(
                    tree, memberId, head, resolve,
                    route.CreationTreeRecordingIds, route.ExcludedIntervalKeys, out _);
            };

            // The defect, measured: the head of the member run resolves alone and no leg is on a
            // third body, so the endpoint filter has nothing to drop.
            RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, resolve, out int headMembers, out int headLegs, out int headDropped);
            Assert.Equal(0, headDropped);

            var expanded = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, resolve, expander,
                out int members, out int legs, out int dropped, out int segments);

            // The declared member set is untouched; the run walk only ADDS segments.
            Assert.Equal(headMembers, members);
            Assert.True(segments >= 1,
                "expected at least one continuation segment, got " + segments);
            Assert.True(dropped >= 1,
                "expected the Sun-frame transfer legs to be dropped, got transferDropped=" + dropped);
            Assert.True(legs > headLegs,
                "expected more surviving legs than the head-only build (" + legs + " vs " + headLegs + ")");

            // The dropped legs are the third-body ones, and nothing on an endpoint body was lost.
            foreach (var group in expanded)
                foreach (var leg in group.legs)
                    Assert.True(leg.bodyName == "Kerbin" || leg.bodyName == "Duna",
                        "surviving leg on a non-endpoint body: " + leg.bodyName);

            // The member whose run carries the transfer, named: chain index 0 -> index 1.
            var run = expander("d23e453bc982482b850ce717ba83bffd");
            Assert.Contains(run, r => r.RecordingId == "36c7688b8e5141f7809e2d4dbe9dc094");
            Recording transfer = byId["36c7688b8e5141f7809e2d4dbe9dc094"];
            var transferLegs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(transfer);
            Assert.Contains(transferLegs, l => l.bodyName == "Sun");

            // The route excludes 1331a21b whole; the walk must not draw it back in.
            Assert.Contains("1331a21bddfb49418be6ebec99dabf98", route.ExcludedIntervalKeys);
            Assert.DoesNotContain(expanded,
                g => g.memberRecordingId == "1331a21bddfb49418be6ebec99dabf98");
        }

        // ==============================================================
        // No-double-draw arbitration, per segment
        // ==============================================================

        [Fact]
        public void Draw_ExpandedSegmentTheGhostHas_IsSkippedAndCountedInSkippedOwned()
        {
            // ROUTE-LINE-EXPANDED-SEGMENT-CO-DRAWS-THE-GHOST-POLYLINE (measured 2026-09-06 on V26M /
            // V26T as ROUTE_CODRAW_VIOLATION with skippedOwned=0): every group is arbitrated by its
            // OWN recording id, and for a continuation segment the ghost's answer comes from the PAINT
            // arm - its forward run-leg pass draws the segment under its own id and never publishes
            // ownership, so an ownership-only read said "nobody has it" while the ghost was drawing it.
            RecordingTree tree = ChainTree();
            var route = new Route { Id = "r-own", RecordingIds = { "head" }, RecordedDockUT = -1.0 };
            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), Expander(route, tree),
                out _, out _, out _, out int segments);
            Assert.Equal(2, segments);

            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting("seg2", true);
            // seg1's mesh covers its whole span (1100..1200): the ghost is painting that segment.
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("seg1", 0, 1100.0, 1200.0, true);

            int skippedOwned = 0;
            var drawn = new List<string>();
            foreach (var group in groups)
                skippedOwned += ArbitrateGroup(group, drawn);

            // Both expanded segments stand down - one owned, one painted - and the declared member
            // the ghost has nothing on still draws every leg it has.
            Assert.Equal(2, skippedOwned);
            Assert.Equal(new[] { "head" }, drawn.ToArray());
        }

        [Fact]
        public void Draw_DeclaredMember_ArbitratesExactlyAsBefore()
        {
            RecordingTree tree = ChainTree();
            var route = new Route { Id = "r-decl", RecordingIds = { "head" }, RecordedDockUT = -1.0 };
            var groups = RouteTrajectoryLineRenderer.BuildRouteMemberLegs(
                route, Resolve(tree), Expander(route, tree), out _, out _, out _, out _);

            // Nothing published: every group draws (the shipped behaviour).
            var drawn = new List<string>();
            foreach (var group in groups) Assert.Equal(0, ArbitrateGroup(group, drawn));
            Assert.Equal(new[] { "head", "seg1", "seg2" }, drawn.ToArray());

            // The declared member owned: the whole group is skipped, exactly as it was before the
            // expansion existed (ownership carries no span, so this arm stays group-level).
            GhostTrajectoryPolylineRenderer.SetOwnershipPublishForTesting("head", true);
            Assert.True(RouteTrajectoryLineRenderer.ShouldSkipGroupAsGhostDrawn(
                groups[0], OwnsProbe()));

            // A DECLARED member can be forward-painted the same way a segment can (mirror direction),
            // and it is arbitrated by the same per-leg paint arm.
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("seg1", 0, 1100.0, 1200.0, true);
            drawn.Clear();
            Assert.Equal(1, ArbitrateGroup(groups[1], drawn));
            Assert.Empty(drawn);
        }

        [Fact]
        public void Draw_PartlyPaintedMember_StillDrawsItsUnpaintedLegs()
        {
            // Re-review F1: the ghost's forward run-leg pass paints the legs the ghost is ON, so
            // standing the whole member down erased every leg it was NOT painting - a hole in the
            // route line. Three cells over one three-leg member: NONE painted, PART painted, ALL
            // painted. Reading run 2 sized the defect: V26M's stood-down group carried 13 legs.
            var group = SyntheticGroup("m3",
                new[] { 100.0, 200.0, 300.0 }, new[] { 200.0, 300.0, 400.0 });

            // NONE: every leg draws.
            var drawn = new List<string>();
            Assert.Equal(0, ArbitrateGroup(group, drawn));

            // PART: only the middle leg's span is covered by a visible mesh.
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("m3", 1, 200.0, 300.0, true);
            Assert.Equal(1, ArbitrateGroup(group, drawn));
            Assert.Equal(2, CountDrawableLegs(group));

            // The shared endpoints do NOT drag the neighbours down with it: overlap is strict.
            Assert.False(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "m3", 100.0, 200.0));
            Assert.False(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "m3", 300.0, 400.0));

            // ALL: three meshes, nothing left for the route line.
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("m3", 0, 100.0, 200.0, true);
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("m3", 2, 300.0, 400.0, true);
            Assert.Equal(3, ArbitrateGroup(group, drawn));
            Assert.Equal(0, CountDrawableLegs(group));
        }

        [Fact]
        public void PaintMembership_IsTheMeshFact_NotAPerFrameStamp()
        {
            // THE READING-RUN-2 DEFECT, as a cell. The ghost's draw pass early-returns whenever its
            // -50 decide walk did not run, and the deactivation sweep lives inside that pass - so on
            // such a frame nothing paints, nothing is hidden, and last frame's mesh is still on
            // screen. Membership must survive that frame; only an actual HIDE may clear it.
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("seg1", 0, 1100.0, 1200.0, true);
            Assert.True(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "seg1", 1100.0, 1200.0));

            // A bailed frame maintains nothing at all - and the answer does not change, because the
            // mesh did not. (The old frame-stamped set flipped to false here, the route line drew,
            // and V26M recorded 403 co-draw violations.)
            Assert.True(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "seg1", 1100.0, 1200.0));

            // Hiding that leg's mesh clears the paint fact.
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("seg1", 0, 1100.0, 1200.0, false);
            Assert.False(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "seg1", 1100.0, 1200.0));

            // A scene change / cross-save flush clears it too, membership and staleness guard alike.
            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("seg1", 0, 1100.0, 1200.0, true);
            GhostTrajectoryPolylineRenderer.Clear();
            Assert.False(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "seg1", 1100.0, 1200.0));
        }

        [Fact]
        public void ResolveLegPaintFromMesh_IsMembershipAndTheDeadRendererGuard()
        {
            Assert.True(GhostTrajectoryPolylineRenderer.ResolveLegPaintFromMesh(true, true));
            Assert.False(GhostTrajectoryPolylineRenderer.ResolveLegPaintFromMesh(false, true));
            // Nothing has maintained the membership since the last flush / destroy: no paint fact
            // may be read, whatever the set still holds.
            Assert.False(GhostTrajectoryPolylineRenderer.ResolveLegPaintFromMesh(true, false));

            GhostTrajectoryPolylineRenderer.SetLegPaintForTesting("seg1", 0, 1100.0, 1200.0, true);
            GhostTrajectoryPolylineRenderer.SetPaintMaintenanceRanForTesting(false);
            Assert.False(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "seg1", 1100.0, 1200.0));
            GhostTrajectoryPolylineRenderer.SetPaintMaintenanceRanForTesting(true);
            Assert.True(GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan(
                "seg1", 1100.0, 1200.0));
        }

        [Fact]
        public void LegSpansOverlap_IsStrictOnASharedEndpoint()
        {
            // Adjacent legs of one recording share an endpoint UT exactly; touching is not covering.
            Assert.False(GhostTrajectoryPolylineRenderer.LegSpansOverlap(100.0, 200.0, 200.0, 300.0));
            Assert.False(GhostTrajectoryPolylineRenderer.LegSpansOverlap(200.0, 300.0, 100.0, 200.0));
            Assert.True(GhostTrajectoryPolylineRenderer.LegSpansOverlap(100.0, 200.5, 200.0, 300.0));
            // Containment either way round is coverage.
            Assert.True(GhostTrajectoryPolylineRenderer.LegSpansOverlap(100.0, 400.0, 200.0, 300.0));
            Assert.True(GhostTrajectoryPolylineRenderer.LegSpansOverlap(200.0, 300.0, 100.0, 400.0));
        }

        // --- Helpers ---

        /// <summary>The latest EndUT anywhere in a composition node's subtree.</summary>
        private static double SubtreeEndUT(MissionCompositionNode node)
        {
            double end = node.EndUT;
            for (int i = 0; i < node.Children.Count; i++)
                end = Math.Max(end, SubtreeEndUT(node.Children[i]));
            return end;
        }

        /// <summary>The live OWNERSHIP arm (group-level): the ghost owns this recording's phase.</summary>
        private static Func<string, bool> OwnsProbe()
            => GhostTrajectoryPolylineRenderer.IsRenderingNonOrbitalLeg;

        /// <summary>The live PAINT arm (per leg): a visible ghost mesh of this recording covers the
        /// leg's span.</summary>
        private static Func<string, double, double, bool> PaintsProbe()
            => GhostTrajectoryPolylineRenderer.IsPaintingNonOrbitalLegSpan;

        /// <summary>
        /// DrawAll's arbitration for one group, exactly as the live loop runs it: the ownership arm
        /// stands the whole group down, then each surviving leg is arbitrated on the paint arm.
        /// Appends the ids of the members that drew at least one leg to <paramref name="drawn"/> and
        /// returns the number of LEGS stood down (the live <c>skippedOwned</c> counter's unit).
        /// </summary>
        private static int ArbitrateGroup(
            RouteTrajectoryLineRenderer.RouteMemberLegs group, List<string> drawn)
        {
            if (group.legs == null || group.legs.Length == 0) return 0;
            if (RouteTrajectoryLineRenderer.ShouldSkipGroupAsGhostDrawn(group, OwnsProbe()))
                return group.legs.Length;

            int skipped = 0, drew = 0;
            for (int i = 0; i < group.legs.Length; i++)
            {
                if (RouteTrajectoryLineRenderer.ShouldSkipLegAsGhostPainted(
                        group.memberRecordingId, group.legs[i].startUT, group.legs[i].endUT,
                        PaintsProbe()))
                    skipped++;
                else
                    drew++;
            }
            if (drew > 0) drawn.Add(group.memberRecordingId);
            return skipped;
        }

        /// <summary>Legs of <paramref name="group"/> the arbitration would leave for the route line
        /// to draw this frame.</summary>
        private static int CountDrawableLegs(RouteTrajectoryLineRenderer.RouteMemberLegs group)
        {
            int drawable = 0;
            for (int i = 0; i < group.legs.Length; i++)
                if (!RouteTrajectoryLineRenderer.ShouldSkipLegAsGhostPainted(
                        group.memberRecordingId, group.legs[i].startUT, group.legs[i].endUT,
                        PaintsProbe()))
                    drawable++;
            return drawable;
        }

        /// <summary>A member group with N legs at the given spans, for the per-leg arbitration cells
        /// (the arbitration reads only the member id and each leg's span).</summary>
        private static RouteTrajectoryLineRenderer.RouteMemberLegs SyntheticGroup(
            string memberRecordingId, double[] startUTs, double[] endUTs)
        {
            var legs = new GhostTrajectoryPolylineRenderer.LegPolyline[startUTs.Length];
            for (int i = 0; i < startUTs.Length; i++)
                legs[i] = new GhostTrajectoryPolylineRenderer.LegPolyline
                {
                    startUT = startUTs[i],
                    endUT = endUTs[i],
                    bodyName = "Kerbin",
                };
            return new RouteTrajectoryLineRenderer.RouteMemberLegs
            {
                memberRecordingId = memberRecordingId,
                legs = legs,
                isDeclaredMember = true,
            };
        }

        private static string FixtureSaveDir(string name)
            => Path.Combine(SyntheticRecordingTests.ResolveProjectRoot(),
                "harness", "fixtures", "saves", name);

        /// <summary>Reads one committed ROUTE node out of a fixture save (GAME &gt; SCENARIO
        /// ParsekScenario &gt; ROUTES), matched on an id prefix.</summary>
        private static Route LoadFixtureRoute(string saveDir, string idPrefix)
        {
            ConfigNode root = ConfigNode.Load(Path.Combine(saveDir, "persistent.sfs"));
            Assert.NotNull(root);
            ConfigNode game = root.GetNode("GAME") ?? root;
            foreach (ConfigNode scenario in game.GetNodes("SCENARIO"))
            {
                if (scenario.GetValue("name") != "ParsekScenario") continue;
                ConfigNode routes = scenario.GetNode("ROUTES");
                if (routes == null) continue;
                foreach (ConfigNode routeNode in routes.GetNodes("ROUTE"))
                {
                    string id = routeNode.GetValue("id");
                    if (id != null && id.StartsWith(idPrefix, StringComparison.Ordinal))
                        return RouteCodec.DeserializeFrom(routeNode);
                }
            }
            return null;
        }

        private static string[] Ids(List<Recording> recs)
            => recs.ConvertAll(r => r.RecordingId).ToArray();

        private static Func<string, Recording> Resolve(RecordingTree tree)
            => id => id != null && tree.Recordings.TryGetValue(id, out var r) ? r : null;

        private static Func<string, Recording> Resolve(params Recording[] recs)
        {
            var map = new Dictionary<string, Recording>(StringComparer.Ordinal);
            foreach (var r in recs) map[r.RecordingId] = r;
            return id => id != null && map.TryGetValue(id, out var rec) ? rec : null;
        }

        private static Func<string, IReadOnlyList<Recording>> Expander(Route route, RecordingTree tree)
        {
            Func<string, Recording> resolve = Resolve(tree);
            return memberId =>
            {
                Recording head = resolve(memberId);
                if (head == null) return null;
                return RouteMemberRunExpansion.ExpandRunForRoute(
                    tree, memberId, head, resolve,
                    route?.CreationTreeRecordingIds, route?.ExcludedIntervalKeys, out _);
            };
        }

        /// <summary>head -&gt; seg1 -&gt; seg2: one vessel's env-split run (same chain, adjacent
        /// chain indices), each segment carrying its own drawable Kerbin leg.</summary>
        private static RecordingTree ChainTree()
        {
            return Tree("t-chain",
                Rec("head", "chain-a", 0, 1000.0, 1100.0),
                Rec("seg1", "chain-a", 1, 1100.0, 1200.0),
                Rec("seg2", "chain-a", 2, 2500.0, 2600.0));
        }

        /// <summary>Two vessels docking: a0 (from 1000) and b0 (from 1010) merge at UT 1100 into the
        /// single child "merged". The recorder lists the merged child first, so it is the branch
        /// continuation of BOTH parents - the F1 shape.</summary>
        private static RecordingTree DockMergeTree()
        {
            var a0 = Rec("a0", "chain-a", 0, 1000.0, 1100.0);
            var b0 = Rec("b0", "chain-b", 0, 1010.0, 1100.0);
            var merged = Rec("merged", "chain-m", 0, 1100.0, 1200.0);
            RecordingTree tree = Tree("t-dock", a0, b0, merged);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = 1100.0,
                Type = BranchPointType.Dock,
                MergeCause = "DOCK",
                ParentRecordingIds = { "a0", "b0" },
                ChildRecordingIds = { "merged" },
            });
            return tree;
        }

        private static RecordingTree Tree(string id, params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null,
            };
            foreach (var r in recs) tree.Recordings[r.RecordingId] = r;
            return tree;
        }

        private static Recording Rec(
            string id, string chainId, int chainIndex, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "V",
                ChainId = chainId,
                ChainIndex = chainIndex,
                IsDebris = false,
                StartBodyName = "Kerbin",
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
            };
            rec.Points.Add(Point(startUT, -0.1, -74.5, 70.0));
            rec.Points.Add(Point((startUT + endUT) * 0.5, -0.05, -74.5, 20000.0));
            rec.Points.Add(Point(endUT, 0.0, -74.5, 100000.0));
            return rec;
        }

        private static TrajectoryPoint Point(double ut, double lat, double lon, double alt)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }
    }
}
