using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Vessel and mission names Phase 3 (design 7.10 / 7.11): every display reader of a
    /// tree's name shows its original mission's current name through
    /// <see cref="MissionStore.DisplayNameForTree(string, string)"/>, falling back to the raw
    /// TreeName when the tree has no mission; log lines keep the raw TreeName. The Logistics
    /// "Flights used" line names vessels the way the Missions tab does (one number per
    /// physical vessel, "X", "X [2]").
    /// </summary>
    [Collection("Sequential")]
    public class MissionDisplayNameReadersTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionDisplayNameReadersTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = true;
            ParsekScenario.ResetInstanceForTesting();
            MergeDialog.ResetTestOverrides();
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            MissionStructureBuilder.SuppressLogging = true;
            VesselNameStore.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            RecordingStore.ResetForTesting();
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;
            MergeDialog.ResetTestOverrides();
            ParsekTimeFormat.ResetForTesting();
            MissionStructureBuilder.SuppressLogging = false;
            VesselNameStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void RenameMissionOf(RecordingTree tree, string name)
        {
            MissionStore.EnsureDefaultsForTrees(new[] { tree });
            MissionStore.RenameMission(MissionStore.FindOriginalMission(tree.Id), name);
        }

        private static RecordingTree OneRecTree(string treeId, string treeName, string recId, string vessel)
        {
            var rec = new Recording
            {
                RecordingId = recId,
                TreeId = treeId,
                VesselName = vessel,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 160.0,
            };
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeName,
                RootRecordingId = recId,
                ActiveRecordingId = recId,
            };
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        // ---------------------------------------------------------------- the helper

        // catches: the helper preferring TreeName over a renamed mission, or blanking a tree
        // that has no mission (or a mission with an empty name).
        [Fact]
        public void DisplayNameForTree_MissionNameElseTreeName()
        {
            RecordingTree tree = OneRecTree("t1", "Kerbal X", "r1", "Kerbal X");
            Assert.Equal("Kerbal X", MissionStore.DisplayNameForTree(tree));

            RenameMissionOf(tree, "Mun Tanker");
            Assert.Equal("Mun Tanker", MissionStore.DisplayNameForTree(tree));
            Assert.Equal("Mun Tanker", MissionStore.DisplayNameForTree("t1", "ignored"));
            Assert.Equal("Raw", MissionStore.DisplayNameForTree("t-none", "Raw"));
            Assert.Null(MissionStore.DisplayNameForTree((RecordingTree)null));
            Assert.Null(MissionStore.DisplayNameForTree(null, null));

            MissionStore.FindOriginalMission("t1").Name = "";
            Assert.Equal("Kerbal X", MissionStore.DisplayNameForTree(tree));
        }

        // catches: a clone's name read instead of the original (main) mission's.
        [Fact]
        public void DisplayNameForTree_ReadsTheOriginalMissionNotAClone()
        {
            RecordingTree tree = OneRecTree("t1", "Kerbal X", "r1", "Kerbal X");
            RenameMissionOf(tree, "Main");
            Mission clone = MissionStore.Clone(MissionStore.FindOriginalMission("t1"));
            Assert.NotNull(clone);
            MissionStore.RenameMission(clone, "Clone");
            Assert.Equal("Main", MissionStore.DisplayNameForTree(tree));
        }

        // ---------------------------------------------------------------- merge dialog

        // catches: the merge dialog body naming the raw launch TreeName after a mission rename.
        [Fact]
        public void MergeDialogBody_ShowsRenamedMission_FallsBackToTreeName()
        {
            RecordingTree tree = OneRecTree("t-merge", "Kerbal X", "r1", "Kerbal X");
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());
            MergeDialog.LiveResumedSegmentProviderForTesting = () => (null, double.NaN);
            MergeDialog.NowUtProviderForTesting = () => 200.0;

            Assert.StartsWith("Kerbal X - ", MergeDialog.BuildWholeTreeMergeDialogBody(tree));

            RenameMissionOf(tree, "Mun Tanker");
            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.StartsWith("Mun Tanker - ", body);
            Assert.DoesNotContain("Kerbal X", body);
        }

        // ---------------------------------------------------------------- Logistics

        // catches: the near-miss label or the tree-name resolver (dismissed rows, ownership
        // note, the "Built from mission" line) showing the launch name after a rename.
        [Fact]
        public void LogisticsTreeLabels_ShowRenamedMission_FallBackToTreeNameThenShortId()
        {
            RecordingTree tree = OneRecTree("0123456789abcdef", "Kerbal X", "r1", "Kerbal X");
            RecordingStore.AddCommittedTreeForTesting(tree);

            Assert.Equal("Kerbal X", LogisticsWindowUI.NearMissTreeLabel(tree));
            Assert.Equal("Kerbal X", LogisticsWindowUI.ResolveTreeDisplayName(tree.Id));

            RenameMissionOf(tree, "Duna Supply 1");
            Assert.Equal("Duna Supply 1", LogisticsWindowUI.NearMissTreeLabel(tree));
            Assert.Equal("Duna Supply 1", LogisticsWindowUI.ResolveTreeDisplayName(tree.Id));

            var unnamed = new RecordingTree { Id = "fedcba9876543210", TreeName = null };
            Assert.Equal("fedcba98", LogisticsWindowUI.NearMissTreeLabel(unnamed));
            Assert.Equal("<unknown mission>", LogisticsWindowUI.ResolveTreeDisplayName(null));
        }

        // catches: the Flights used accessor returning the launch name for the tree after a
        // rename, or its log line drifting off the raw TreeName.
        [Fact]
        public void RecordingDisplayInfo_TreeNameIsTheMissionName_LogKeepsTheRawTreeName()
        {
            var rec = new Recording { RecordingId = "rec-p3", VesselName = "Tanker", TreeOrder = 1 };
            RecordingStore.AddRecordingWithTreeForTesting(rec, treeName: "Kerbal X");
            RecordingTree tree = null;
            foreach (RecordingTree t in RecordingStore.CommittedTrees)
                if (t.Id == rec.TreeId) tree = t;
            Assert.NotNull(tree);

            Assert.True(RecordingStore.TryResolveRecordingDisplayInfo(
                "rec-p3", out _, out string before, out _));
            Assert.Equal("Kerbal X", before);

            RenameMissionOf(tree, "Mun Tanker");
            logLines.Clear();
            Assert.True(RecordingStore.TryResolveRecordingDisplayInfo(
                "rec-p3", out string name, out string treeName, out int order,
                out Recording foundRec, out RecordingTree foundTree));
            Assert.Equal("Tanker", name);
            Assert.Equal("Mun Tanker", treeName);
            Assert.Equal(1, order);
            Assert.Same(rec, foundRec);
            Assert.Same(tree, foundTree);
            Assert.Contains(logLines, l => l.Contains("Logistics display resolve")
                && l.Contains("tree='Kerbal X'"));
        }

        // ---------------------------------------------------------------- partner phrase

        private static Recording Rec(string id, string vessel, double start, double end,
            uint pid, string guid, string chain = null, int chainIndex = -1)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ChainId = chain,
                ChainIndex = chainIndex,
                TerminalStateValue = TerminalState.Orbiting,
                StartBodyName = "Kerbin",
            };
        }

        private static RecordingTree Tree(string id, Recording[] recs, params BranchPoint[] bps)
        {
            var tree = new RecordingTree { Id = id, TreeName = id, RootRecordingId = recs[0].RecordingId };
            foreach (Recording r in recs) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.AddRange(bps);
            foreach (BranchPoint bp in bps)
                foreach (string c in bp.ChildRecordingIds)
                    if (tree.Recordings.TryGetValue(c, out Recording child))
                        child.ParentBranchPointId = bp.Id;
            return tree;
        }

        private static BranchPoint BP(string id, BranchPointType type, double ut, string parent, params string[] children)
            => new BranchPoint
            {
                Id = id, Type = type, UT = ut,
                ParentRecordingIds = new List<string> { parent },
                ChildRecordingIds = new List<string>(children),
            };

        private static Dictionary<string, string> Names(RecordingTree tree, Func<string, string> missionName,
            params RecordingTree[] others)
        {
            var all = new List<RecordingTree> { tree };
            all.AddRange(others);
            return MissionVesselNaming.Build(tree, MissionStructureBuilder.Build(tree),
                MissionVesselNaming.LaunchIndex.Build(all), missionName, out _);
        }

        // catches: the partner phrase's fallback (no resolver passed) naming the other
        // mission by its raw TreeName after that mission was renamed.
        [Fact]
        public void PartnerPhraseFallback_ShowsRenamedMission_ElseTreeName()
        {
            RecordingTree a = Tree("A", new[] { Rec("a1", "Station", 0, 500, 100, "G1") });
            Recording b1 = Rec("b1", "Tug", 10, 50, 200, "G2");
            Recording b2 = Rec("b2", "Station", 50, 80, 100, "G1");
            RecordingTree b = Tree("B", new[] { b1, b2 }, BP("d", BranchPointType.Dock, 50, "b1", "b2"));

            Assert.Equal("Station (mission 'A')", Names(b, null, a)["b2"]);

            RenameMissionOf(a, "Kerbin Station");
            Assert.Equal("Station (mission 'Kerbin Station')", Names(b, null, a)["b2"]);
            // A resolver that answers wins; one that answers nothing falls back the same way.
            Assert.Equal("Station (mission 'R')", Names(b, id => "R", a)["b2"]);
            Assert.Equal("Station (mission 'Kerbin Station')", Names(b, id => null, a)["b2"]);
        }

        // ---------------------------------------------------------------- Flights used numbering

        private static string FlightsUsed(RecordingTree tree, params string[] recordingIds)
        {
            Dictionary<string, string> names = Names(tree, null);
            var shown = new List<string>();
            var keys = new List<string>();
            foreach (string id in recordingIds)
            {
                shown.Add(LogisticsRoutePresentation.ResolveFlightUsedName(
                    id, tree.Recordings[id].VesselName, tree.Id, names, out string key));
                keys.Add(key);
            }
            return LogisticsRoutePresentation.FormatFlightsUsed(shown, null, keys);
        }

        // catches: two legs of ONE vessel numbered as two flights ("X [1], X [2]", the old
        // per-occurrence numbering) instead of the Missions tab's single "X".
        [Fact]
        public void FlightsUsed_TwoLegsOfOneVessel_IsX()
        {
            Recording head = Rec("head", "X", 0, 50, 1, "g1", "c", 0);
            Recording tail = Rec("tail", "X", 50, 100, 1, "g1", "c", 1);
            RecordingTree tree = Tree("t", new[] { head, tail });

            Assert.Equal("Flights used: X", FlightsUsed(tree, "head", "tail"));
            Dictionary<string, string> missionsTab = Names(tree, null);
            Assert.Equal("X", missionsTab["head"]);
            Assert.Equal("X", missionsTab["tail"]);
        }

        // catches: two vessels with one name numbered per occurrence ("X [1]", "X [2]")
        // instead of one number per physical vessel ("X", "X [2]"), the Missions numbering.
        [Fact]
        public void FlightsUsed_TwoVesselsWithOneName_IsXThenX2()
        {
            Recording head = Rec("head", "X", 0, 50, 1, "g1", "c", 0);
            Recording tail = Rec("tail", "X", 50, 100, 1, "g1", "c", 1);
            Recording later = Rec("later", "X", 80, 120, 9, "g9");
            RecordingTree tree = Tree("t", new[] { head, tail, later },
                BP("s", BranchPointType.JointBreak, 80, "tail", "later"));

            Assert.Equal("Flights used: X, X [2]", FlightsUsed(tree, "head", "tail", "later"));
            Assert.Equal("Flights used: X, X [2]", FlightsUsed(tree, "head", "later"));
            Dictionary<string, string> missionsTab = Names(tree, null);
            Assert.Equal("X [2]", missionsTab["later"]);
        }

        // catches: a recording the naming map does not cover (EVA, standalone) losing its
        // raw name or being folded into another entry.
        [Fact]
        public void ResolveFlightUsedName_NotInTheMap_KeepsRawNameAndNeverFolds()
        {
            string n = LogisticsRoutePresentation.ResolveFlightUsedName(
                "eva", "Bob Kerman", "t", new Dictionary<string, string>(), out string key);
            Assert.Equal("Bob Kerman", n);
            Assert.Null(key);
            Assert.Equal("Raw", LogisticsRoutePresentation.ResolveFlightUsedName(
                "r", "Raw", null, null, out key));
            Assert.Null(key);
            Assert.Equal("Flights used: Raw, Raw", LogisticsRoutePresentation.FormatFlightsUsed(
                new[] { "Raw", "Raw" }, null, new string[] { null, null }));
        }
    }
}
