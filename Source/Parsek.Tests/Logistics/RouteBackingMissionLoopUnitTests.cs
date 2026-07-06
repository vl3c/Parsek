using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 5 (Checkpoint C) end-to-end glue: a route-owned backing Mission, fed
    /// through the UNCHANGED <c>MissionLoopUnitBuilder.Build</c> (the locked
    /// Missions seam, appended as a one-element list exactly as the three host push
    /// seams do), yields exactly one <c>LoopUnit</c> whose member window is
    /// <c>[launch .. dock]</c> (M-MIS-5: the docked combined stretch, the
    /// post-undock survivor and the payload are all trimmed) and whose
    /// <c>OwnerByIndex</c> covers ONLY that window's committed indices, disjoint
    /// from a parallel manual mission loop on a different tree.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing proof that the route render path needs NO change to
    /// the locked builder: the route's <c>ExcludedIntervalKeys</c> (derived by
    /// <see cref="RouteBackingMission.ComputeExcludedIntervalKeys"/>) fold into the
    /// builder's existing interval selection, and the single-owner
    /// <c>OwnerByIndex</c> contract is preserved across the route + manual union.
    /// </remarks>
    [Collection("Sequential")]
    public class RouteBackingMissionLoopUnitTests : IDisposable
    {
        private const double AutoInterval = 30.0;

        public RouteBackingMissionLoopUnitTests()
        {
            // The M-MIS-9 branch-freeze tests register their tree in
            // RecordingStore so BuildMission's signature-gated derivation can
            // resolve it; reset around every test to keep the static store clean.
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
        }

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, string vessel = "V")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end
            };
        }

        private static BranchPoint BP(string id, BranchPointType type,
            string[] parents, string[] children, double ut)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                SplitCause = type == BranchPointType.Undock ? "UNDOCK" : null,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children)
            };
        }

        private static RecordingTree Tree(string id, Recording[] recs, BranchPoint[] bps = null)
        {
            var tree = new RecordingTree
            {
                Id = id,
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null
            };
            foreach (var r in recs)
                tree.Recordings[r.RecordingId] = r;
            if (bps != null)
                tree.BranchPoints.AddRange(bps);
            return tree;
        }

        // launch -> dock -> undock, payload peeled at undock (same topology as
        // RouteBackingMissionTests). Root launch UT 1000, undock UT 3000.
        private static RecordingTree BuildLaunchDockUndockTree(string treeId)
        {
            return Tree(treeId, new[]
                {
                    Leg("launch", "C0", 0, 1000, 2000, vessel: "Transport"),
                    Leg("docked", "C0", 1, 2000, 3000, vessel: "Transport"),
                    Leg("survivor", "C0", 2, 3000, 4000, vessel: "Transport"),
                    Leg("payload", "C1", 0, 3000, 3500, vessel: "Payload")
                },
                new[]
                {
                    BP("dock-bp", BranchPointType.Dock,
                        new[] { "launch" }, new[] { "docked" }, ut: 2000),
                    BP("undock-bp", BranchPointType.Undock,
                        new[] { "docked" }, new[] { "survivor", "payload" }, ut: 3000)
                });
        }

        // M4a (A4) multi-stop tree (verified composition shape; mirrors the
        // production dock-merged-child topology the single-stop fixtures use, with
        // an INTERMEDIATE depot-A undock added). The transport launches, docks at
        // depot A, delivers, UNDOCKS from depot A (peeling its delivered payloadA -
        // the intermediate undock) and CONTINUES to depot B, docks at depot B
        // (the LAST dock), delivers, and undocks from depot B (the TERMINAL undock,
        // peeling payloadB) flying home. Root launch UT 1000.
        //   launch     C0/0  [1000..1500]  (ROOT; transport solo to depot A dock)
        //   midA2B     C0/1  [1500..2500]  (INTERMEDIATE undock@1500 from depot A;
        //                                    transport CONTINUES to depot B)
        //   payloadA   C1/0  [1500..1800]  (delivered payload dropped at depot A)
        //   dockedB    C0/2  [2500..3000]  (Dock BP@2500 at depot B = the LAST dock;
        //                                    the dock-merged combined leg, deliver)
        //   tail       C0/3  [3000..3500]  (TERMINAL undock@3000 from depot B; home)
        //   payloadB   C2/0  [3000..3300]  (delivered payload dropped at depot B)
        // Composition (M-MIS-5) folds the transport into ONE through-line owned by
        // "launch", split at the two undocks AND subdivided at the depot-B dock:
        // launch [1000..1500], launch/seg1 [1500..2500] (the intermediate-undock
        // survivor flying A->B), launch/seg1@dock1 [2500..3000] (the depot-B docked
        // combined stretch - its own selectable sub-interval since M-MIS-5),
        // launch/seg2 [3000..3500] (the post-terminal tail). The route span END is
        // the LAST DOCK = 2500: production passes the last stop's
        // RouteConnectionWindow.DockUT (RouteBuilder -> ComputeExcludedIntervalKeys),
        // and since M-MIS-5 the dock UT and the terminal undock UT are NO LONGER
        // interchangeable here - the docked stretch launch/seg1@dock1 STARTS at 2500,
        // so passing 2500 excludes it (production behavior: the ghost retires at the
        // dock) while passing 3000 would keep it rendered. The pre-M-MIS-5 fixture
        // passed the terminal undock 3000 under an equivalence ("no selectable
        // interval starts inside (2500, 3000)") that the dock edge EXPIRED; the
        // fixture now models production faithfully. Everything at/after 2500 (the
        // docked stretch, the post-terminal tail + payloadB) is excluded; the
        // intermediate-undock survivor (StartUT 1500 < 2500) stays rendered.
        private const double MultiStopSegmentEndUT = 2500.0; // the depot-B (LAST) dock
        private const double MultiStopRootLaunchUT = 1000.0;

        private static RecordingTree BuildMultiStopTree(string treeId)
        {
            return Tree(treeId, new[]
                {
                    Leg("launch", "C0", 0, 1000, 1500, vessel: "Transport"),
                    Leg("midA2B", "C0", 1, 1500, 2500, vessel: "Transport"),
                    Leg("payloadA", "C1", 0, 1500, 1800, vessel: "Payload"),
                    Leg("dockedB", "C0", 2, 2500, 3000, vessel: "Transport"),
                    Leg("tail", "C0", 3, 3000, 3500, vessel: "Transport"),
                    Leg("payloadB", "C2", 0, 3000, 3300, vessel: "Payload")
                },
                new[]
                {
                    // INTERMEDIATE undock from depot A: peels the delivered payloadA;
                    // the transport (midA2B) CONTINUES to depot B. This survivor leg
                    // must NOT be trimmed (StartUT 1500 < the span boundary 3000).
                    BP("undockA-bp", BranchPointType.Undock,
                        new[] { "launch" }, new[] { "midA2B", "payloadA" }, ut: 1500),
                    BP("dockB-bp", BranchPointType.Dock,
                        new[] { "midA2B" }, new[] { "dockedB" }, ut: 2500),
                    // TERMINAL undock from depot B (closest to the span end = the
                    // route span end): peels payloadB; the tail flies home.
                    BP("undockB-bp", BranchPointType.Undock,
                        new[] { "dockedB" }, new[] { "tail", "payloadB" }, ut: 3000)
                });
        }

        [Fact]
        public void RouteMission_ThroughUnchangedBuild_TrimmedToLaunchDock()
        {
            const string treeId = "route-tree";
            RecordingTree tree = BuildLaunchDockUndockTree(treeId);
            var committed = new List<Recording>(tree.Recordings.Values);
            int idxLaunch = committed.FindIndex(r => r.RecordingId == "launch");
            int idxDocked = committed.FindIndex(r => r.RecordingId == "docked");
            int idxSurvivor = committed.FindIndex(r => r.RecordingId == "survivor");
            int idxPayload = committed.FindIndex(r => r.RecordingId == "payload");

            // Build the route-owned Mission via the production helpers exactly as the
            // host seams do: derive the excluded keys at the DOCK UT (production
            // passes ConnectionWindow.DockUT, M-MIS-5), then build the Mission.
            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, segmentEndUT: 2000.0, launchUT: 1000.0);
            Route route = new RouteFixtureBuilder()
                .WithId("route-loopunit")
                .WithName("Loop Unit Route")
                .WithBackingMissionTreeId(treeId)
                .WithSchedule(1000.0, 1000.0)   // span [launch..dock], dispatchInterval (== span)
                .WithLoopAnchorUT(1000.0)
                .Build();
            foreach (string key in excluded)
                route.ExcludedIntervalKeys.Add(key);
            Mission routeMission = RouteBackingMission.BuildMission(route, currentUT: 100000.0);

            // Append the one route Mission to the (empty here) player mission list,
            // exactly as the host seams union it.
            var unioned = new List<Mission> { routeMission };

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                unioned, new[] { tree }, committed, AutoInterval);

            // Exactly one unit (the route's backing Mission).
            Assert.Equal(1, set.Count);

            // The unit's member window is [launch..dock] (the M-MIS-5 D4 flip): the
            // launch leg is a member; the docked combined leg (starts AT the dock),
            // the post-undock survivor and the payload are NOT.
            Assert.True(set.IsMember(idxLaunch), "launch leg must be a unit member");
            Assert.False(set.IsMember(idxDocked),
                "docked combined leg must be trimmed (rendering stops at the dock, M-MIS-5)");
            Assert.False(set.IsMember(idxSurvivor), "post-undock survivor must be trimmed");
            Assert.False(set.IsMember(idxPayload), "post-undock payload must be trimmed");

            // The unit's span end-trims at the DOCK.
            int ownerIndex = -1;
            foreach (var kvp in set.UnitsByOwner)
            {
                ownerIndex = kvp.Key;
                GhostPlaybackLogic.LoopUnit unit = kvp.Value;
                Assert.Equal(1000.0, unit.SpanStartUT); // ROOT launch, not dock-child 2000
                Assert.Equal(2000.0, unit.SpanEndUT);   // end-trimmed at the dock
            }
            Assert.True(ownerIndex >= 0);
        }

        [Fact]
        public void RouteMissionUnion_WithManualLoopOnOtherTree_HasDisjointOwnerByIndex()
        {
            // Route loops tree X; a manual mission loops tree Y. Their committed
            // indices are disjoint, so OwnerByIndex maps each index to exactly ONE
            // owner (the single-owner contract holds across the union).
            const string routeTreeId = "tree-route";
            const string manualTreeId = "tree-manual";
            RecordingTree routeTree = BuildLaunchDockUndockTree(routeTreeId);
            RecordingTree manualTree = Tree(manualTreeId, new[]
            {
                Leg("m-a", "M", 0, 5000, 5100, vessel: "Manual"),
                Leg("m-b", "M", 1, 5100, 5200, vessel: "Manual")
            });

            // Committed list spans both trees (same frame, as the host passes).
            var committed = new List<Recording>();
            committed.AddRange(routeTree.Recordings.Values);
            committed.AddRange(manualTree.Recordings.Values);

            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(routeTree, segmentEndUT: 2000.0, launchUT: 1000.0);
            Route route = new RouteFixtureBuilder()
                .WithId("route-disjoint")
                .WithBackingMissionTreeId(routeTreeId)
                .WithSchedule(1000.0, 1000.0)
                .WithLoopAnchorUT(1000.0)
                .Build();
            foreach (string key in excluded)
                route.ExcludedIntervalKeys.Add(key);
            Mission routeMission = RouteBackingMission.BuildMission(route, currentUT: 100000.0);

            var manualMission = new Mission("manual-m", manualTreeId, "Manual Loop")
            {
                LoopPlayback = true,
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 600.0
            };

            // Union: manual player missions + appended route mission.
            var unioned = new List<Mission> { manualMission };
            unioned.Add(routeMission);

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                unioned, new[] { routeTree, manualTree }, committed, AutoInterval);

            // Two units (route + manual), disjoint owners.
            Assert.Equal(2, set.Count);

            // Each committed index maps to at most one owner; the route's members
            // and the manual loop's members never share an owner.
            int routeOwner = -1, manualOwner = -1;
            int idxRouteLaunch = committed.FindIndex(r => r.RecordingId == "launch");
            int idxManualA = committed.FindIndex(r => r.RecordingId == "m-a");
            Assert.True(set.OwnerByIndex.TryGetValue(idxRouteLaunch, out routeOwner));
            Assert.True(set.OwnerByIndex.TryGetValue(idxManualA, out manualOwner));
            Assert.NotEqual(routeOwner, manualOwner);
        }

        // -----------------------------------------------------------------
        // M-MIS-9 branch freeze: post-creation branches never extend the unit
        // -----------------------------------------------------------------

        // Route captured at creation time over the dock tree: SourceRefs cover
        // the rendered members + the dock-child leaf, the dock binding carries
        // the recorded DOCK UT (2000 = the segment end since M-MIS-5, arming the
        // UT end-trim prong), and the excluded keys are the production
        // creation-time derivation ({launch@dock1, launch/seg1, payload}).
        private static Route FrozenRoute(RecordingTree creationTree, string id)
        {
            Route route = new RouteFixtureBuilder()
                .WithId(id)
                .WithName("Frozen Route")
                .WithBackingMissionTreeId(creationTree.Id)
                .WithSourceRef(new RouteSourceRef { RecordingId = "launch", TreeId = creationTree.Id })
                .WithSourceRef(new RouteSourceRef { RecordingId = "docked", TreeId = creationTree.Id })
                .WithSchedule(1000.0, 2000.0)
                .WithLoopAnchorUT(1000.0)
                .WithDockBinding(2000.0, "docked")
                .Build();
            foreach (string key in RouteBackingMission.ComputeExcludedIntervalKeys(
                         creationTree, segmentEndUT: 2000.0, launchUT: 1000.0))
                route.ExcludedIntervalKeys.Add(key);
            return route;
        }

        // catches: a post-creation fork (re-fly style: a NEW recording landing
        // at the undock BP, outside the route's member path) silently joining
        // the synthesized backing mission and extending the loop-unit span -
        // the delivery-cadence regression M-MIS-9 closes. The span end must
        // stay the creation-time segment end.
        [Fact]
        public void PostCreationFork_AutoExcluded_SpanEndStaysAtCreationSegmentEnd()
        {
            const string treeId = "tree-fork";
            RecordingTree tree = BuildLaunchDockUndockTree(treeId);
            Route route = FrozenRoute(tree, "route-fork01");

            // The fork lands AFTER creation; if it joined the mission its
            // [3000..6000] window would extend the span end to 6000.
            tree.Recordings["refly-fork"] = Leg("refly-fork", "C2", 0, 3000, 6000, vessel: "Fork");
            tree.BranchPoints.Find(b => b.Id == "undock-bp").ChildRecordingIds.Add("refly-fork");
            RecordingStore.CommittedTrees.Add(tree);

            var committed = new List<Recording>(tree.Recordings.Values);
            Mission routeMission = RouteBackingMission.BuildMission(route, currentUT: 100000.0);

            // The fork's key is auto-excluded on the synthesized mission.
            Assert.Contains("refly-fork", routeMission.ExcludedIntervalKeys);

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                new List<Mission> { routeMission }, new[] { tree }, committed, AutoInterval);

            Assert.Equal(1, set.Count);
            int idxFork = committed.FindIndex(r => r.RecordingId == "refly-fork");
            Assert.False(set.IsMember(idxFork), "post-creation fork must not join the unit");
            foreach (var kvp in set.UnitsByOwner)
            {
                Assert.Equal(1000.0, kvp.Value.SpanStartUT);
                Assert.Equal(2000.0, kvp.Value.SpanEndUT);      // delivery span still ends at the dock
                Assert.Equal(2000.0, kvp.Value.CadenceSeconds); // dispatch cadence unchanged
            }
        }

        // catches: a switch-fly continuation appended to a MEMBER through-line
        // (same chain, base recording known) being auto-excluded (the base-id
        // rule must keep it out of the auto set) or leaking into the rendered
        // window (the creation-time end-trim already drops the extended tail
        // interval, so the span stays stable either way).
        [Fact]
        public void PostCreationContinuation_OnMemberThroughLine_SpanStable_NothingAutoExcluded()
        {
            const string treeId = "tree-contin";
            RecordingTree tree = BuildLaunchDockUndockTree(treeId);
            Route route = FrozenRoute(tree, "route-contin01");

            // The continuation extends the transport's own chain (C0/3) past
            // the recorded end: it folds into the KNOWN "launch" through-line
            // (keys stay "launch"/"launch/segN", base "launch").
            tree.Recordings["contin"] = Leg("contin", "C0", 3, 4000, 5000, vessel: "Transport");
            RecordingStore.CommittedTrees.Add(tree);

            var committed = new List<Recording>(tree.Recordings.Values);
            Mission routeMission = RouteBackingMission.BuildMission(route, currentUT: 100000.0);

            // Nothing auto-excluded: every selectable key bases at a recording
            // known at creation.
            Assert.NotNull(route.AutoExcludedNewIntervalKeys);
            Assert.Empty(route.AutoExcludedNewIntervalKeys);

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                new List<Mission> { routeMission }, new[] { tree }, committed, AutoInterval);

            Assert.Equal(1, set.Count);
            int idxContin = committed.FindIndex(r => r.RecordingId == "contin");
            Assert.False(set.IsMember(idxContin),
                "post-undock continuation must stay outside the trimmed window");
            foreach (var kvp in set.UnitsByOwner)
            {
                Assert.Equal(1000.0, kvp.Value.SpanStartUT);
                Assert.Equal(2000.0, kvp.Value.SpanEndUT);
                Assert.Equal(2000.0, kvp.Value.CadenceSeconds);
            }
        }

        // catches: the positional-key hole, case (a) of review finding 1 - a
        // switch-fly continuation onto the route's transport PLUS an undock
        // during that flight. The new peel edge renumbers the through-line's
        // intervals and mints a base-KNOWN tail key ("launch/seg2"
        // [4500..5000]) past the creation-time index range; the base-id rule
        // alone would include it, extending the owner window to [1000..5000]
        // and inflating the dispatch cadence. The UT end-trim prong must drop
        // it: span end stays 3000 and CadenceSeconds stays 2000.
        [Fact]
        public void PostCreationContinuationWithPeel_MintedTailKeyTrimmed_SpanAndCadenceStable()
        {
            const string treeId = "tree-contin2";
            RecordingTree tree = BuildLaunchDockUndockTree(treeId);
            Route route = FrozenRoute(tree, "route-contin02");

            // Post-creation growth: the transport's chain continues (C0/3) and
            // a payload undocks mid-continuation at 4500 (recorder shape: the
            // undock SPLITS the continuation - contin ends at the undock, the
            // continuing half "contin2" is listed first in the BP children,
            // "drop" peels). Edges become [1000, 3000, 4500, 5000] ->
            // launch [1000..3000], launch/seg1 [3000..4500],
            // launch/seg2 [4500..5000] (base-known minted tail) + the NEW
            // "drop" recording's own key.
            tree.Recordings["contin"] = Leg("contin", "C0", 3, 4000, 4500, vessel: "Transport");
            tree.Recordings["contin2"] = Leg("contin2", "C0", 4, 4500, 5000, vessel: "Transport");
            tree.Recordings["drop"] = Leg("drop", "C3", 0, 4500, 4800, vessel: "Drop");
            tree.BranchPoints.Add(BP("late-undock", BranchPointType.Undock,
                new[] { "contin" }, new[] { "contin2", "drop" }, ut: 4500));
            RecordingStore.CommittedTrees.Add(tree);

            var committed = new List<Recording>(tree.Recordings.Values);
            Mission routeMission = RouteBackingMission.BuildMission(route, currentUT: 100000.0);

            // The minted base-known tail key AND the new recording's key are
            // both auto-excluded on the synthesized mission.
            Assert.Contains("launch/seg2", routeMission.ExcludedIntervalKeys);
            Assert.Contains("drop", routeMission.ExcludedIntervalKeys);

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                new List<Mission> { routeMission }, new[] { tree }, committed, AutoInterval);

            Assert.Equal(1, set.Count);
            int idxContin = committed.FindIndex(r => r.RecordingId == "contin");
            int idxContin2 = committed.FindIndex(r => r.RecordingId == "contin2");
            int idxDrop = committed.FindIndex(r => r.RecordingId == "drop");
            Assert.False(set.IsMember(idxContin),
                "continuation past the dock must stay outside the trimmed window");
            Assert.False(set.IsMember(idxContin2),
                "post-peel continuation half must stay outside the trimmed window");
            Assert.False(set.IsMember(idxDrop),
                "mid-continuation peel must not join the unit");
            foreach (var kvp in set.UnitsByOwner)
            {
                Assert.Equal(1000.0, kvp.Value.SpanStartUT);
                Assert.Equal(2000.0, kvp.Value.SpanEndUT);      // span end stays the dock
                Assert.Equal(2000.0, kvp.Value.CadenceSeconds); // cadence not inflated
            }
        }

        // catches: the positional-key hole, case (b) of review finding 1 - a
        // re-fly fork rooted MID-RECORDING in the excluded post-undock subtree
        // (the member SourceRefs are untouched, so RevalidateSources stays
        // green). The fork's edge at the rewind UT renumbers the excluded tail
        // into launch/seg1 [3000..3500] + launch/seg2 [3500..4000]; the
        // base-KNOWN launch/seg2 would join via the string freeze alone. The
        // UT end-trim must drop it AND the fork recording itself: span end
        // stays 3000 and CadenceSeconds stays 2000.
        [Fact]
        public void PostCreationMidSubtreeFork_RenumberedTailTrimmed_SpanAndCadenceStable()
        {
            const string treeId = "tree-fork2";
            RecordingTree tree = BuildLaunchDockUndockTree(treeId);
            Route route = FrozenRoute(tree, "route-fork02");

            // Post-creation growth: a re-fly fork roots inside the excluded
            // survivor leg at UT 3500 with a long new branch [3500..6000].
            // Merge-time splitter shape (RecordingTreeSplitter): the survivor
            // splits into HEAD (keeps the id, [3000..3500]) + TIP (the
            // superseded remainder, [3500..4000], listed first as the
            // continuing child), and the fork attaches at the split BP.
            tree.Recordings["survivor"] = Leg("survivor", "C0", 2, 3000, 3500, vessel: "Transport");
            tree.Recordings["survivor-tip"] = Leg("survivor-tip", "C0", 3, 3500, 4000, vessel: "Transport");
            tree.Recordings["refork"] = Leg("refork", "C2", 0, 3500, 6000, vessel: "Fork");
            tree.BranchPoints.Add(BP("refly-bp", BranchPointType.Undock,
                new[] { "survivor" }, new[] { "survivor-tip", "refork" }, ut: 3500));
            RecordingStore.CommittedTrees.Add(tree);

            var committed = new List<Recording>(tree.Recordings.Values);
            Mission routeMission = RouteBackingMission.BuildMission(route, currentUT: 100000.0);

            // Both the renumbered base-known tail and the new fork recording
            // are auto-excluded on the synthesized mission.
            Assert.Contains("launch/seg2", routeMission.ExcludedIntervalKeys);
            Assert.Contains("refork", routeMission.ExcludedIntervalKeys);

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                new List<Mission> { routeMission }, new[] { tree }, committed, AutoInterval);

            Assert.Equal(1, set.Count);
            int idxRefork = committed.FindIndex(r => r.RecordingId == "refork");
            int idxSurvivor = committed.FindIndex(r => r.RecordingId == "survivor");
            Assert.False(set.IsMember(idxRefork), "mid-subtree fork must not join the unit");
            Assert.False(set.IsMember(idxSurvivor), "excluded survivor must stay excluded");
            foreach (var kvp in set.UnitsByOwner)
            {
                Assert.Equal(1000.0, kvp.Value.SpanStartUT);
                Assert.Equal(2000.0, kvp.Value.SpanEndUT);      // span end stays the dock
                Assert.Equal(2000.0, kvp.Value.CadenceSeconds); // cadence not inflated
            }
        }

        // -----------------------------------------------------------------
        // M4a (A4) D7 / D8: last-dock end-trim + the no-spawn regression pin
        // -----------------------------------------------------------------

        // D7 (last-dock end-trim, pure derivation): an N-stop route's excluded-key
        // set must cover EVERYTHING at/after the LAST dock (the post-last-dock tail
        // + the terminal peel), while the INTERMEDIATE-undock survivor leg (the
        // transport that undocked from depot A and continued to depot B, StartUT <
        // the span boundary) must STAY rendered, INCLUDING the depot-B docked
        // combined leg. Catches: the walk keying on a single/first dock (which would
        // trim the survivor or fail to trim the B tail), or the terminal-undock
        // scoping wrongly picking the intermediate undock (which would trim the
        // continuing survivor + everything after it).
        [Fact]
        public void Compute_MultiStop_ExcludesAtAndAfterLastDock_KeepsIntermediateSurvivor()
        {
            const string treeId = "tree-multistop-d7";
            RecordingTree tree = BuildMultiStopTree(treeId);

            // The route span END is the LAST dock (depot B) = 2500. RouteBuilder
            // passes this (the max stop DockUT, A2 RecordedDockUT=last-dock) to
            // ComputeExcludedIntervalKeys.
            HashSet<string> excluded = RouteBackingMission.ComputeExcludedIntervalKeys(
                tree, segmentEndUT: MultiStopSegmentEndUT, launchUT: MultiStopRootLaunchUT);

            var structure = MissionStructureBuilder.Build(tree);
            var roots = MissionCompositionBuilder.Build(structure);
            var windows = MissionIntervalSelection.ComputeRenderWindows(roots, excluded);

            // The transport through-line (root-owned "launch") renders
            // [launch .. lastDock]: it starts at the ROOT launch (1000, NOT a
            // mid-flight dock child) and end-trims at the last dock (2500), so the
            // intermediate-undock survivor leg (the transport flying A->B,
            // StartUT 1500 < 2500) is INSIDE the rendered window - the run did
            // not stop at depot A.
            Assert.True(windows.ContainsKey("launch"),
                "transport (root-owned) through-line must still render");
            MissionIntervalSelection.RenderWindow w = windows["launch"];
            Assert.Equal(MultiStopRootLaunchUT, w.StartUT);   // 1000, the ROOT launch
            Assert.Equal(MultiStopSegmentEndUT, w.EndUT);     // 2500, end-trimmed at the last dock

            // The post-last-dock tail vessel and its peel are fully dropped.
            Assert.False(windows.ContainsKey("payloadB"),
                "the terminal-undock peel must be excluded (post-last-dock)");

            // The excluded set drops the docked combined stretch, the post-terminal
            // tail interval + the peel.
            Assert.NotEmpty(excluded);
            Assert.Contains("launch/seg1@dock1", excluded); // the depot-B docked stretch (M-MIS-5)
            Assert.Contains("payloadB", excluded);          // terminal-undock peel
            Assert.Contains("launch/seg2", excluded);       // the post-terminal tail interval
            // The intermediate survivor's interval is NOT excluded: the transport
            // renders launch -> undock A -> dock B as ONE through-line whose middle
            // interval ("launch/seg1") is kept (StartUT 1500 < 2500).
            Assert.DoesNotContain("launch/seg1", excluded);
            Assert.DoesNotContain("launch", excluded);
        }

        // D8 (the no-spawn regression pin, BOTH halves + the intermediate survivor).
        // Through the UNCHANGED MissionLoopUnitBuilder.Build (the locked Missions
        // seam the engine intercepts at GhostPlaybackEngine.cs:1141 - a loop-unit
        // member is driven by UpdateUnitMemberPlayback and NEVER reaches the past-
        // end / ShouldSpawnAtRecordingEnd path), this pins, for an N-stop
        // route-driven loop unit:
        //   (a) every KEPT member (launch through-line up to the last dock,
        //       including the intermediate-undock survivor) IS a loop-unit member
        //       (it loops, never spawns);
        //   (b) every recording AT/AFTER the last dock is a NON-member (the
        //       docked-combined tail leg, the post-last-dock tail continuation,
        //       and the terminal peel are excluded entirely - so they never become
        //       a unit member and never spawn).
        // Multi-stop widens the rendered window PAST the first dock (to the last
        // dock), so this guards that the widening did not start spawning the kept
        // members nor start RENDERING the post-last-dock tail. Architectural: the
        // no-spawn guarantee is the loop-unit interception; this PINS it via
        // set.IsMember at the build seam.
        [Fact]
        public void NoSpawnPin_MultiStopLoopUnit_KeptMembersLoop_PostLastDockNonMembers()
        {
            const string treeId = "tree-multistop-nospawn";
            RecordingTree tree = BuildMultiStopTree(treeId);
            var committed = new List<Recording>(tree.Recordings.Values);
            int idxLaunch = committed.FindIndex(r => r.RecordingId == "launch");
            int idxMidA2B = committed.FindIndex(r => r.RecordingId == "midA2B");
            int idxPayloadA = committed.FindIndex(r => r.RecordingId == "payloadA");
            int idxDockedB = committed.FindIndex(r => r.RecordingId == "dockedB");
            int idxTail = committed.FindIndex(r => r.RecordingId == "tail");
            int idxPayloadB = committed.FindIndex(r => r.RecordingId == "payloadB");

            HashSet<string> excluded = RouteBackingMission.ComputeExcludedIntervalKeys(
                tree, segmentEndUT: MultiStopSegmentEndUT, launchUT: MultiStopRootLaunchUT);
            Route route = new RouteFixtureBuilder()
                .WithId("route-multistop-nospawn")
                .WithName("Multi-Stop No-Spawn Pin")
                .WithBackingMissionTreeId(treeId)
                // span = [1000..3000], dispatchInterval == span (cadence == span)
                .WithSchedule(MultiStopSegmentEndUT - MultiStopRootLaunchUT,
                              MultiStopSegmentEndUT - MultiStopRootLaunchUT)
                .WithLoopAnchorUT(MultiStopRootLaunchUT)
                .Build();
            foreach (string key in excluded)
                route.ExcludedIntervalKeys.Add(key);
            Mission routeMission = RouteBackingMission.BuildMission(route, currentUT: 100000.0);

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                new List<Mission> { routeMission }, new[] { tree }, committed, AutoInterval);

            // Exactly one loop unit (the route's backing Mission).
            Assert.Equal(1, set.Count);

            // (a) KEPT members loop (are unit members -> intercepted at
            //     GhostPlaybackEngine.cs:1141 by UpdateUnitMemberPlayback, never
            //     reaching ShouldSpawnAtRecordingEnd): the transport through-line up
            //     to the last dock, INCLUDING the intermediate-undock survivor leg
            //     (the transport flew depot A -> depot B - the widened multi-stop
            //     window keeps it).
            Assert.True(set.IsMember(idxLaunch),
                "launch leg must be a loop-unit member (loops, never spawns)");
            Assert.True(set.IsMember(idxMidA2B),
                "intermediate-undock survivor (transport continues depot A -> depot B) must STAY a member");

            // (A4 review nit c) The intermediate depot-A peel (payloadA, the cargo
            // the transport dropped at depot A, [1500..1800]) is INTENTIONALLY a
            // KEPT member: the route renders the whole [launch .. last dock] window,
            // so everything that peeled BEFORE the last dock (StartUT 1500 < the
            // span boundary, and payloadA roots at the INTERMEDIATE undock, NOT the
            // terminal undock whose children are trimmed) stays in the loop unit.
            // It is a member (loops, never spawns), NOT a post-last-dock non-member.
            Assert.True(set.IsMember(idxPayloadA),
                "intermediate depot-A peel (payloadA [1500..1800]) is intentionally a KEPT member - it dropped before the last dock");

            // (b) recordings AT/after the last dock are NON-members (excluded
            //     entirely -> never become a unit member, never spawn). Since
            //     M-MIS-5 this includes the depot-B docked combined leg itself:
            //     its interval STARTS at the last dock (launch/seg1@dock1) and the
            //     route's rendered window stops there (the ghost retires at the
            //     dock instead of sitting docked to the undock).
            Assert.False(set.IsMember(idxDockedB),
                "the depot-B docked combined leg must be a NON-member (rendering stops at the last dock, M-MIS-5)");
            Assert.False(set.IsMember(idxTail),
                "the post-last-dock tail continuation must be a NON-member");
            Assert.False(set.IsMember(idxPayloadB),
                "the terminal-undock peel must be a NON-member");

            // The unit span end-trims at the last dock (depot B = 2500), not the
            // terminal undock (3000) or the post-last-dock tail (3500).
            foreach (var kvp in set.UnitsByOwner)
            {
                Assert.Equal(MultiStopRootLaunchUT, kvp.Value.SpanStartUT);  // 1000, ROOT launch
                Assert.Equal(MultiStopSegmentEndUT, kvp.Value.SpanEndUT);    // 2500, the last dock
            }
        }

    }
}
