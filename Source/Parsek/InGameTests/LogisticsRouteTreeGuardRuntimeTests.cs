using System;
using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime check for the Phase 2 mutual-exclusion guard
    /// (<see cref="RouteTreeGuard"/>): a route-bound tree is reported bound, and
    /// forcing a clear turns OFF both the mission loop and the per-recording loop
    /// on that tree while leaving a non-route tree's loops intact. This is the
    /// load-bearing behavior behind the greyed UI toggles: the toggles call the
    /// same predicate, and a turn-ON would route through the same clear/commit
    /// guard. The headless test exercises the predicate + clear directly because
    /// asserting on <c>GUI.enabled</c> render state needs a live OnGUI pass.
    /// </summary>
    public sealed class LogisticsRouteTreeGuardRuntimeTests
    {
        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - mutates RouteStore, MissionStore, and the committed-tree list; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "A committed route binds its source tree; ForceClearManualLoopForRouteTree turns off the mission loop AND the per-recording loop on that tree, leaving a non-route tree's loops untouched")]
        public void RouteTreeGuard_BindsTreeAndClearsBothLoopSurfaces()
        {
            string boundTreeId = "ingame-guard-bound-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string freeTreeId = "ingame-guard-free-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-guard-route-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot the route list so teardown can restore it regardless of outcome.
            var preExistingRoutes = new List<Route>();
            IReadOnlyList<Route> committed = RouteStore.CommittedRoutes;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null)
                    preExistingRoutes.Add(committed[i]);

            // Synthetic committed trees with a looping recording each.
            Recording boundRec = MakeLoopingRecording("ingame-rec-bound", boundTreeId);
            Recording freeRec = MakeLoopingRecording("ingame-rec-free", freeTreeId);
            RecordingTree boundTree = MakeTree(boundTreeId, boundRec);
            RecordingTree freeTree = MakeTree(freeTreeId, freeRec);

            // Snapshot mission state by id so we only remove what we add.
            var addedMissionIds = new HashSet<string>(StringComparer.Ordinal);
            bool boundTreeAdded = false;
            bool freeTreeAdded = false;
            bool routeAdded = false;

            try
            {
                RecordingStore.AddCommittedTreeForTesting(boundTree);
                boundTreeAdded = true;
                RecordingStore.AddCommittedTreeForTesting(freeTree);
                freeTreeAdded = true;

                // Seed a default mission for each tree and turn its loop ON.
                MissionStore.EnsureDefaultsForTrees(new List<RecordingTree> { boundTree, freeTree });
                Mission boundMission = MissionStore.FindOriginalMission(boundTreeId);
                Mission freeMission = MissionStore.FindOriginalMission(freeTreeId);
                InGameAssert.IsNotNull(boundMission, "No default mission created for the bound tree");
                InGameAssert.IsNotNull(freeMission, "No default mission created for the free tree");
                if (boundMission != null) addedMissionIds.Add(boundMission.Id);
                if (freeMission != null) addedMissionIds.Add(freeMission.Id);

                double ut = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0;
                MissionStore.SetLoopEnabled(boundMission, true, ut);
                MissionStore.SetLoopEnabled(freeMission, true, ut);
                InGameAssert.IsTrue(boundMission.LoopPlayback, "Bound mission loop did not enable");
                InGameAssert.IsTrue(freeMission.LoopPlayback, "Free mission loop did not enable");

                // Commit a route that binds the bound tree only.
                Route route = new Route
                {
                    Id = routeId,
                    Name = "Parsek Guard In-Game Route",
                    BackingMissionTreeId = boundTreeId,
                    RecordingIds = new List<string> { boundRec.RecordingId },
                    SourceRefs = new List<RouteSourceRef>
                    {
                        new RouteSourceRef { RecordingId = boundRec.RecordingId, TreeId = boundTreeId }
                    },
                    Stops = new List<RouteStop> { new RouteStop() },
                    Status = RouteStatus.Active
                };
                RouteStore.AddRoute(route);
                routeAdded = RouteStore.TryGetRoute(routeId, out _);
                InGameAssert.IsTrue(routeAdded, "Route was not stored");

                // PREDICATE: bound tree reports bound; free tree does not.
                InGameAssert.IsTrue(RouteTreeGuard.IsTreeBoundToActiveRoute(boundTreeId),
                    "Route-bound tree was not reported bound");
                InGameAssert.IsFalse(RouteTreeGuard.IsTreeBoundToActiveRoute(freeTreeId),
                    "Non-route tree was incorrectly reported bound");

                // CLEAR: clearing the bound tree turns off BOTH its surfaces.
                RouteTreeGuard.ForceClearManualLoopForRouteTree(boundTreeId, ut);
                InGameAssert.IsFalse(boundMission.LoopPlayback,
                    "Mission loop on the route-bound tree was not cleared");
                InGameAssert.IsFalse(boundRec.LoopPlayback,
                    "Per-recording loop on the route-bound tree was not cleared");

                // The non-route tree's loops survive (the clear targets one tree).
                InGameAssert.IsTrue(freeMission.LoopPlayback,
                    "Mission loop on the non-route tree was wrongly cleared");
                InGameAssert.IsTrue(freeRec.LoopPlayback,
                    "Per-recording loop on the non-route tree was wrongly cleared");

                ParsekLog.Info("TestRunner",
                    $"RouteTreeGuard_InGame: PASS boundTree={boundTreeId} freeTree={freeTreeId} route={routeId}");
            }
            finally
            {
                if (routeAdded)
                    RouteStore.RemoveRoute(routeId);
                RestoreRoutes(preExistingRoutes);

                RemoveAddedMissions(addedMissionIds);
                if (boundTreeAdded) RemoveCommittedTree(boundTreeId);
                if (freeTreeAdded) RemoveCommittedTree(freeTreeId);
            }
        }

        private static Recording MakeLoopingRecording(string idPrefix, string treeId)
        {
            return new Recording
            {
                RecordingId = idPrefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                TreeId = treeId,
                VesselName = "GuardTestVessel",
                LoopPlayback = true
            };
        }

        private static RecordingTree MakeTree(string treeId, Recording rec)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = rec.RecordingId };
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        private static void RestoreRoutes(List<Route> preExisting)
        {
            RouteStore.ResetForTesting();
            for (int i = 0; i < preExisting.Count; i++)
                if (preExisting[i] != null)
                    RouteStore.AddRoute(preExisting[i]);
        }

        private static void RemoveAddedMissions(HashSet<string> addedMissionIds)
        {
            if (addedMissionIds == null || addedMissionIds.Count == 0)
                return;
            // The default mission of a tree is non-deletable via MissionStore.Delete
            // (CanDelete guards the original). Since we are also removing the trees,
            // PruneOrphans drops the now-orphaned default missions for us.
            var liveTrees = RecordingStore.CommittedTrees;
            MissionStore.PruneOrphans(liveTrees);
        }

        private static void RemoveCommittedTree(string treeId)
        {
            // CommittedTrees has no public single-tree remove; rebuild the list
            // without the synthetic tree. Snapshot, clear, re-add the survivors.
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
            // Re-prune any missions orphaned by the tree removal.
            MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
        }
    }
}
