using System.Collections.Generic;

namespace Parsek.InGameTests
{
    /// <summary>
    /// M-MIS-5 in-game composition assertion: a dock tree surfaces the "Docked"
    /// interval row. The pure xUnit suite (MissionCompositionTests) covers the
    /// derivation math; this pins that the production pipeline running inside KSP
    /// (the same MissionStructureBuilder -> MissionCompositionBuilder chain the
    /// Missions window rows and the loop-unit builder consume) emits the dock
    /// sub-interval with the rebased combined-composition label. Self-contained:
    /// builds a local synthetic tree, registers nothing, mutates no store.
    /// </summary>
    public sealed class MissionDockCompositionRuntimeTest
    {
        [InGameTest(Category = "Missions", Scene = GameScenes.FLIGHT,
            Description = "A dock tree's composition surfaces the 'Docked' interval row: the docked stretch keys as an @dock sub-interval starting at the merge UT, labeled with the merged leg's combined composition")]
        public void DockTree_Composition_SurfacesDockedIntervalRow()
        {
            // launch [1000..2000] pod x1 -> Dock BP@2000 -> docked combined leg
            // [2000..3000] pod x1 + probe x1 -> Undock BP@3000 -> survivor + depot.
            var tree = new RecordingTree { Id = "ingame-mmis5-dock", RootRecordingId = "launch" };
            tree.Recordings["launch"] = MakeLeg("launch", "C", 0, 1000, 2000, pods: 1);
            tree.Recordings["docked"] = MakeLeg("docked", "C2", 0, 2000, 3000, pods: 1, probes: 1);
            tree.Recordings["survivor"] = MakeLeg("survivor", "C3", 0, 3000, 4000, pods: 1);
            tree.Recordings["depot"] = MakeLeg("depot", "C4", 0, 3000, 3500, probes: 1);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "dockbp",
                Type = BranchPointType.Dock,
                UT = 2000,
                ParentRecordingIds = new List<string> { "launch" },
                ChildRecordingIds = new List<string> { "docked" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "undockbp",
                Type = BranchPointType.Undock,
                UT = 3000,
                SplitCause = "UNDOCK",
                ParentRecordingIds = new List<string> { "docked" },
                ChildRecordingIds = new List<string> { "survivor", "depot" },
            });

            List<MissionCompositionNode> roots =
                MissionCompositionBuilder.Build(MissionStructureBuilder.Build(tree));

            InGameAssert.IsTrue(roots.Count == 1,
                "Expected exactly one composition root, got " + roots.Count);
            MissionCompositionNode root = roots[0];
            InGameAssert.IsTrue(root.EndEvent == "Docked",
                "Pre-dock interval must end with the 'Docked' event, was '" + root.EndEvent + "'");

            // The docked interval row: @dock sub-interval key, starts at the merge UT,
            // "Docked" start event, rebased combined label, selectable.
            MissionCompositionNode docked = null;
            for (int i = 0; i < root.Children.Count; i++)
                if (root.Children[i] != null && root.Children[i].HeadLegId == "launch@dock1")
                    docked = root.Children[i];
            InGameAssert.IsNotNull(docked,
                "Dock tree did not surface the 'launch@dock1' docked interval row");
            InGameAssert.IsTrue(docked.StartEvent == "Docked",
                "Docked row start event must be 'Docked', was '" + docked.StartEvent + "'");
            InGameAssert.IsTrue(docked.StartUT == 2000.0 && docked.EndUT == 3000.0,
                "Docked row must span [2000..3000], was [" + docked.StartUT + ".." + docked.EndUT + "]");
            InGameAssert.IsTrue(docked.CompositionLabel == "pod x1, probe x1",
                "Docked row label must be the merged combined composition 'pod x1, probe x1', was '"
                + docked.CompositionLabel + "'");
            InGameAssert.IsTrue(docked.IsSelectable,
                "Docked row must be independently selectable");

            ParsekLog.Info("TestRunner",
                "MissionDockComposition_InGame: PASS key=" + docked.HeadLegId
                + " label='" + docked.CompositionLabel + "'");
        }

        private static Recording MakeLeg(
            string id, string chainId, int chainIndex, double start, double end,
            int pods = 0, int probes = 0)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "MMIS5 Dock Fixture",
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            return rec;
        }
    }
}
