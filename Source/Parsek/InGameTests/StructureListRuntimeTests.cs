using System;
using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Exercises the mission / route structure-list builders against LIVE committed
    /// recording data, catching anything the synthetic xUnit fixtures miss (real
    /// terminal states, branch-point shapes, connection windows). The builders are pure,
    /// so this needs no scene; it just needs committed data to be present.
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

                // Steps must be UT-ordered (NaN UTs only appear on the route path, not here).
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
            Description = "Route structure list builds an origin-first step list for live committed routes")]
        public void RouteStructureList_BuildsOriginFirst()
        {
            var routes = Logistics.RouteStore.CommittedRoutes;
            if (routes.Count == 0)
                InGameAssert.Skip("No committed routes");

            Func<string, Recording> lookup = id =>
            {
                if (string.IsNullOrEmpty(id)) return null;
                var committed = RecordingStore.CommittedRecordings;
                for (int i = 0; i < committed.Count; i++)
                    if (committed[i] != null && string.Equals(committed[i].RecordingId, id, StringComparison.Ordinal))
                        return committed[i];
                return null;
            };

            foreach (var route in routes)
            {
                if (route == null) continue;
                List<StructureStep> steps = RouteStructureListBuilder.Build(route, lookup);
                InGameAssert.IsTrue(steps.Count > 0, $"Route {route.Id} built 0 structure steps");
                InGameAssert.AreEqual(StructureStepKind.Origin, steps[0].Kind,
                    $"Route {route.Id} first step is {steps[0].Kind}, expected Origin");
            }

            ParsekLog.Info("TestRunner",
                $"Route structure list: built origin-first step lists for {routes.Count} committed route(s)");
        }
    }
}
