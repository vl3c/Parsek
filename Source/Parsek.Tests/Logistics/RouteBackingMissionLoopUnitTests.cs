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
    /// <c>[launch .. undock]</c> (the post-undock survivor / payload trimmed) and
    /// whose <c>OwnerByIndex</c> covers ONLY that window's committed indices,
    /// disjoint from a parallel manual mission loop on a different tree.
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

        [Fact]
        public void RouteMission_ThroughUnchangedBuild_YieldsOneUnit_TrimmedToLaunchUndock()
        {
            const string treeId = "route-tree";
            RecordingTree tree = BuildLaunchDockUndockTree(treeId);
            var committed = new List<Recording>(tree.Recordings.Values);
            int idxLaunch = committed.FindIndex(r => r.RecordingId == "launch");
            int idxDocked = committed.FindIndex(r => r.RecordingId == "docked");
            int idxSurvivor = committed.FindIndex(r => r.RecordingId == "survivor");
            int idxPayload = committed.FindIndex(r => r.RecordingId == "payload");

            // Build the route-owned Mission via the production helpers exactly as the
            // host seams do: derive the excluded keys, then build the Mission.
            HashSet<string> excluded =
                RouteBackingMission.ComputeExcludedIntervalKeys(tree, segmentEndUT: 3000.0, launchUT: 1000.0);
            Route route = new RouteFixtureBuilder()
                .WithId("route-loopunit")
                .WithName("Loop Unit Route")
                .WithBackingMissionTreeId(treeId)
                .WithSchedule(2000.0, 2000.0)   // span, dispatchInterval (== span)
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

            // The unit's member window is [launch..undock]: the launch + docked
            // legs are members; the post-undock survivor + payload are NOT.
            Assert.True(set.IsMember(idxLaunch), "launch leg must be a unit member");
            Assert.True(set.IsMember(idxDocked), "docked leg must be a unit member");
            Assert.False(set.IsMember(idxSurvivor), "post-undock survivor must be trimmed");
            Assert.False(set.IsMember(idxPayload), "post-undock payload must be trimmed");

            // The unit's span end-trims at the undock.
            int ownerIndex = -1;
            foreach (var kvp in set.UnitsByOwner)
            {
                ownerIndex = kvp.Key;
                GhostPlaybackLogic.LoopUnit unit = kvp.Value;
                Assert.Equal(1000.0, unit.SpanStartUT); // ROOT launch, not dock-child 2000
                Assert.Equal(3000.0, unit.SpanEndUT);   // end-trimmed at undock
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
                RouteBackingMission.ComputeExcludedIntervalKeys(routeTree, segmentEndUT: 3000.0, launchUT: 1000.0);
            Route route = new RouteFixtureBuilder()
                .WithId("route-disjoint")
                .WithBackingMissionTreeId(routeTreeId)
                .WithSchedule(2000.0, 2000.0)
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
        // the rendered members + the dock-child leaf, excluded keys are the
        // production creation-time derivation.
        private static Route FrozenRoute(RecordingTree creationTree, string id)
        {
            Route route = new RouteFixtureBuilder()
                .WithId(id)
                .WithName("Frozen Route")
                .WithBackingMissionTreeId(creationTree.Id)
                .WithSourceRef(new RouteSourceRef { RecordingId = "launch", TreeId = creationTree.Id })
                .WithSourceRef(new RouteSourceRef { RecordingId = "docked", TreeId = creationTree.Id })
                .WithSchedule(2000.0, 2000.0)
                .WithLoopAnchorUT(1000.0)
                .Build();
            foreach (string key in RouteBackingMission.ComputeExcludedIntervalKeys(
                         creationTree, segmentEndUT: 3000.0, launchUT: 1000.0))
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
                Assert.Equal(3000.0, kvp.Value.SpanEndUT); // delivery cadence span unchanged
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
                Assert.Equal(3000.0, kvp.Value.SpanEndUT);
            }
        }
    }
}
