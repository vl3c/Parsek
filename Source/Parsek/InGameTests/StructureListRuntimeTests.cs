using System;
using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Exercises the mission Log builder against LIVE committed recording data, catching
    /// anything the synthetic xUnit fixtures miss (real terminal states, branch-point
    /// shapes), and the Logistics route Log button's resolution to its source mission.
    /// The builders are pure, so this needs no scene; it just needs committed data.
    /// </summary>
    public class StructureListRuntimeTests
    {
        [InGameTest(Category = "Structure",
            Description = "Mission structure list builds non-empty, UT-ordered steps for live committed trees")]
        public void MissionStructureList_BuildsOrderedSteps()
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees.Count == 0)
                InGameAssert.Skip("No committed trees");

            int treesWithSteps = 0;
            foreach (var tree in trees)
            {
                if (tree == null) continue;
                MissionStructure structure = MissionStructureBuilder.Build(tree);
                List<StructureStep> steps = MissionStructureListBuilder.Build(tree, structure);

                // A tree with at least one controlled leg must produce at least one step.
                if (structure.LegsById.Count > 0)
                {
                    InGameAssert.IsTrue(steps.Count > 0,
                        $"Tree {tree.Id} has {structure.LegsById.Count} legs but built 0 structure steps");
                    treesWithSteps++;
                }

                // Steps must be UT-ordered.
                for (int i = 1; i < steps.Count; i++)
                {
                    InGameAssert.IsTrue(steps[i].UT >= steps[i - 1].UT,
                        $"Tree {tree.Id} step {i} out of order: {steps[i - 1].UT} -> {steps[i].UT}");
                }
            }

            ParsekLog.Info("TestRunner",
                $"Mission structure list: {treesWithSteps}/{trees.Count} committed trees produced ordered steps");
        }

        [InGameTest(Category = "Structure",
            Description = "A committed route's Go to button resolves to its source mission, whose Mission Log builds steps")]
        public void RouteLog_OpensTheSourceMissionLog()
        {
            var routes = Logistics.RouteStore.CommittedRoutes;
            if (routes.Count == 0)
                InGameAssert.Skip("No committed routes");

            int resolved = 0;
            foreach (var route in routes)
            {
                if (route == null) continue;
                string treeId = LogisticsWindowUI.ResolveRouteSourceTreeId(route);
                if (string.IsNullOrEmpty(treeId)) continue; // a hand-made route: the button greys out
                Mission mission = MissionStore.FindOriginalMission(treeId);
                InGameAssert.IsTrue(mission != null,
                    $"Route {route.Id} source tree {treeId} has no mission for its Go to button to reveal");
                RecordingTree tree = null;
                var trees = RecordingStore.CommittedTrees;
                for (int i = 0; i < trees.Count; i++)
                    if (trees[i] != null && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                        tree = trees[i];
                InGameAssert.IsTrue(tree != null, $"Route {route.Id} source tree {treeId} is not committed");
                List<StructureStep> steps = MissionStructureListBuilder.Build(
                    tree, MissionStructureBuilder.Build(tree), null, null, mission.ExcludedIntervalKeys);
                InGameAssert.IsTrue(steps.Count > 0,
                    $"Route {route.Id} source mission '{mission.Name}' built 0 Log steps");
                resolved++;
            }
            if (resolved == 0)
                InGameAssert.Skip("No committed route names a source mission");

            ParsekLog.Info("TestRunner",
                $"Route Log: {resolved}/{routes.Count} committed route(s) resolved to a source mission with Log steps");
        }
    }
}
