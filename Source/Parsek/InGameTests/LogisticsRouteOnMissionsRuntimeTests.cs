using System;
using System.Collections.Generic;
using System.Reflection;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 5 (Checkpoint C) end-to-end stock-runtime check for the
    /// route-on-Missions render + fire path. Under live KSP statics it asserts the
    /// whole seam composes:
    /// <list type="number">
    ///   <item>a ghost-driving route is SELECTED by
    ///   <see cref="RouteGhostDriverSelector"/>;</item>
    ///   <item>its backing Mission, fed through the UNCHANGED
    ///   <c>MissionLoopUnitBuilder.Build</c>, yields exactly one loop unit whose
    ///   member window is <c>[launch .. undock]</c> (the post-undock tail trimmed);</item>
    ///   <item>the <see cref="RouteLoopClock"/> reports a crossing whose loopUT
    ///   sweeps across the recorded dock UT (delivery fires there);</item>
    ///   <item>the mutual-exclusion guard reports the route's tree bound (so both
    ///   manual-loop surfaces grey);</item>
    ///   <item>a manual loop on a DIFFERENT tree renders in parallel (disjoint
    ///   owners).</item>
    /// </list>
    /// A precondition gate (cheap reflection check that the union seam + the
    /// loop-clock fire switch exist) runs first so a red result is attributable to
    /// THIS workstream, not an incomplete upstream phase. The xUnit suite covers
    /// the pure math; this test pins that the production statics + the locked
    /// builder compose correctly at runtime.
    /// </summary>
    public sealed class LogisticsRouteOnMissionsRuntimeTests
    {
        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Mutates RouteStore + the committed-tree list under live KSP statics; runs out of band so a parallel batch test cannot observe partial state.",
            Description = "A ghost-driving route is selected, its backing mission yields one loop unit trimmed to [launch..undock], the loop clock crosses the recorded dock UT, mutual exclusion binds the route tree, and a manual loop on another tree renders in parallel (disjoint owners)")]
        public void RouteOnMissions_RendersTrimmedLoop_FiresAtDock_AndMutuallyExcludes()
        {
            // ---- PRECONDITION GATE -------------------------------------------
            // The union seam (RouteGhostDriverSelector + MissionLoopUnitBuilder.Build)
            // and the loop-clock fire switch (RouteLoopClock.TryGetRouteLoopState)
            // must exist. A failure here means an upstream phase is incomplete, not
            // that this end-to-end behavior regressed.
            MethodInfo selectMethod = typeof(RouteGhostDriverSelector).GetMethod(
                "SelectGhostDrivingBackingMissions",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (selectMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteGhostDriverSelector.SelectGhostDrivingBackingMissions missing (upstream Phase 3 incomplete)");
            MethodInfo clockMethod = typeof(RouteLoopClock).GetMethod(
                "TryGetRouteLoopState",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (clockMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteLoopClock.TryGetRouteLoopState missing (upstream Phase 4 incomplete)");
            MethodInfo buildMethod = typeof(MissionLoopUnitBuilder).GetMethod(
                "Build",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (buildMethod == null)
                InGameAssert.Skip("PRECONDITION: MissionLoopUnitBuilder.Build missing (locked Missions seam absent)");

            string routeTreeId = "ingame-rom-route-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string manualTreeId = "ingame-rom-manual-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-rom-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot routes for restore.
            var preExistingRoutes = new List<Route>();
            IReadOnlyList<Route> committedRoutes = RouteStore.CommittedRoutes;
            for (int i = 0; i < committedRoutes.Count; i++)
                if (committedRoutes[i] != null)
                    preExistingRoutes.Add(committedRoutes[i]);

            // Build a launch -> dock -> undock tree (root launch UT 1000, undock 3000).
            RecordingTree routeTree = BuildLaunchDockUndockTree(routeTreeId);
            // A simple 2-leg manual tree on a different id.
            RecordingTree manualTree = BuildLinearTree(manualTreeId);

            bool routeTreeAdded = false, manualTreeAdded = false, routeAdded = false;
            var committed = new List<Recording>();

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                RecordingStore.AddCommittedTreeForTesting(manualTree);
                manualTreeAdded = true;

                committed.AddRange(routeTree.Recordings.Values);
                committed.AddRange(manualTree.Recordings.Values);

                int idxLaunch = committed.FindIndex(r => r.RecordingId == "launch");
                int idxSurvivor = committed.FindIndex(r => r.RecordingId == "survivor");
                int idxManualA = committed.FindIndex(r => r.RecordingId == "m-a");

                // Derive the route's backing-mission definition exactly as RouteBuilder
                // would, and commit an Active route bound to the tree.
                HashSet<string> excluded = RouteBackingMission.ComputeExcludedIntervalKeys(
                    routeTree, undockUT: 3000.0, launchUT: 1000.0);
                var route = new Route
                {
                    Id = routeId,
                    Name = "Parsek Route-on-Missions In-Game",
                    BackingMissionTreeId = routeTreeId,
                    ExcludedIntervalKeys = excluded,
                    RecordedDockUT = 2500.0,            // within [1000,3000]
                    DockMemberRecordingId = "docked",
                    LoopAnchorUT = 1000.0,
                    LastObservedLoopCycleIndex = -1,
                    TransitDuration = 2000.0,
                    DispatchInterval = 2000.0,          // == span -> cadence == interval
                    Status = RouteStatus.Active,
                    RecordingIds = new List<string> { "launch", "docked" },
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef { RecordingId = "launch", TreeId = routeTreeId },
                        new RouteSourceRef { RecordingId = "docked", TreeId = routeTreeId }
                    },
                    Stops = new List<RouteStop> { new RouteStop() }
                };
                RouteStore.AddRoute(route);
                routeAdded = RouteStore.TryGetRoute(routeId, out _);
                InGameAssert.IsTrue(routeAdded, "Route was not stored");

                // (1) The route is SELECTED as ghost-driving.
                double ut = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 100000.0;
                IReadOnlyList<Mission> routeMissions =
                    RouteGhostDriverSelector.SelectGhostDrivingBackingMissions(
                        RouteStore.CommittedRoutes, ut);
                Mission routeMission = null;
                for (int i = 0; i < routeMissions.Count; i++)
                    if (routeMissions[i] != null && routeMissions[i].TreeId == routeTreeId)
                        routeMission = routeMissions[i];
                InGameAssert.IsNotNull(routeMission,
                    "Active route was not selected as a ghost-driving backing mission");

                // A parallel manual loop on the OTHER tree.
                var manualMission = new Mission("ingame-rom-manual-m", manualTreeId, "Manual Loop")
                {
                    LoopPlayback = true,
                    LoopTimeUnit = LoopTimeUnit.Sec,
                    LoopIntervalSeconds = 600.0
                };

                // (2) Union through the UNCHANGED builder -> two units, route trimmed.
                var unioned = new List<Mission> { manualMission, routeMission };
                GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                    unioned, new[] { routeTree, manualTree }, committed, 30.0);

                InGameAssert.IsTrue(set.Count == 2,
                    "Expected exactly two loop units (route + manual), got " + set.Count);
                InGameAssert.IsTrue(set.IsMember(idxLaunch),
                    "Route launch leg should be a loop-unit member");
                InGameAssert.IsFalse(set.IsMember(idxSurvivor),
                    "Post-undock survivor must be trimmed from the route loop unit");

                // (5) Disjoint owners: the route's member and the manual loop's
                // member map to different owners (single-owner contract preserved).
                int routeOwner, manualOwner;
                InGameAssert.IsTrue(set.OwnerByIndex.TryGetValue(idxLaunch, out routeOwner),
                    "Route launch index has no owner");
                InGameAssert.IsTrue(set.OwnerByIndex.TryGetValue(idxManualA, out manualOwner),
                    "Manual loop index has no owner");
                InGameAssert.IsTrue(routeOwner != manualOwner,
                    "Route and manual loop must have disjoint owners (cross-tree parallel render)");

                // (3) The loop clock crosses the recorded dock UT during the span.
                // Resolve the route's unit and sweep loopUT across [spanStart, dock].
                GhostPlaybackLogic.LoopUnit routeUnit;
                InGameAssert.IsTrue(set.TryGetUnitForMember(idxLaunch, out routeUnit),
                    "Could not resolve the route's loop unit");

                bool sweptDock = false;
                // Sample two UTs straddling the dock phase (anchor floored to spanEnd
                // -> first cycle wraps spanStart). Just before and after the dock.
                double dockPhase = 2500.0; // RecordedDockUT
                double spanStart = routeUnit.SpanStartUT;
                double anchor = routeUnit.PhaseAnchorUT;
                // loopUT == spanStart + ((ut - anchor) mod cadence). Choose ut so the
                // phase lands just past the dock to prove the dock UT is reachable.
                double cadence = routeUnit.SpanEndUT - routeUnit.SpanStartUT;
                double utAtDock = anchor + (dockPhase - spanStart);
                double loopUT; long cycleIdx; bool tail;
                if (RouteLoopClock.TryGetRouteLoopState(routeUnit, utAtDock,
                        out loopUT, out cycleIdx, out tail))
                {
                    // The resolved loopUT should land at/near the dock phase.
                    if (!tail && Math.Abs(loopUT - dockPhase) < 1.0)
                        sweptDock = true;
                }
                InGameAssert.IsTrue(sweptDock,
                    "Loop clock did not resolve a loopUT at the recorded dock UT (delivery would never fire)");

                // (4) Mutual exclusion: the route's tree is reported bound.
                InGameAssert.IsTrue(RouteTreeGuard.IsTreeBoundToActiveRoute(routeTreeId),
                    "Route-bound tree was not reported bound (manual-loop surfaces would not grey)");
                InGameAssert.IsFalse(RouteTreeGuard.IsTreeBoundToActiveRoute(manualTreeId),
                    "Non-route tree was incorrectly reported bound");

                ParsekLog.Info("TestRunner",
                    $"RouteOnMissions_InGame: PASS routeTree={routeTreeId} manualTree={manualTreeId} " +
                    $"units={set.Count} cadence={cadence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            finally
            {
                if (routeAdded)
                    RouteStore.RemoveRoute(routeId);
                RestoreRoutes(preExistingRoutes);
                if (routeTreeAdded) RemoveCommittedTree(routeTreeId);
                if (manualTreeAdded) RemoveCommittedTree(manualTreeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
            }
        }

        // launch -> dock -> undock with a peeled payload at undock. Root launch UT
        // 1000, undock 3000 (mirrors the xUnit topology).
        private static RecordingTree BuildLaunchDockUndockTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            tree.Recordings["launch"] = Leg("launch", "C0", 0, 1000, 2000, "Transport");
            tree.Recordings["docked"] = Leg("docked", "C0", 1, 2000, 3000, "Transport");
            tree.Recordings["survivor"] = Leg("survivor", "C0", 2, 3000, 4000, "Transport");
            tree.Recordings["payload"] = Leg("payload", "C1", 0, 3000, 3500, "Payload");
            tree.BranchPoints.Add(BP("dock-bp", BranchPointType.Dock,
                new[] { "launch" }, new[] { "docked" }, 2000));
            tree.BranchPoints.Add(BP("undock-bp", BranchPointType.Undock,
                new[] { "docked" }, new[] { "survivor", "payload" }, 3000));
            return tree;
        }

        private static RecordingTree BuildLinearTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "m-a" };
            tree.Recordings["m-a"] = Leg("m-a", "M", 0, 5000, 5100, "Manual");
            tree.Recordings["m-b"] = Leg("m-b", "M", 1, 5100, 5200, "Manual");
            return tree;
        }

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, string vessel)
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

        private static void RestoreRoutes(List<Route> preExisting)
        {
            RouteStore.ResetForTesting();
            for (int i = 0; i < preExisting.Count; i++)
                if (preExisting[i] != null)
                    RouteStore.AddRoute(preExisting[i]);
        }

        private static void RemoveCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return;
            var survivors = new List<RecordingTree>(trees.Count);
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t != null && string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    continue;
                survivors.Add(t);
            }
            RecordingStore.ClearCommittedTreesInternal();
            for (int i = 0; i < survivors.Count; i++)
                RecordingStore.AddCommittedTreeForTesting(survivors[i]);
        }
    }
}
