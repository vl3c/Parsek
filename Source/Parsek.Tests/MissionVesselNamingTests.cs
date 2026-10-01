using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// The shared mission vessel names (<see cref="MissionVesselNaming"/>): another mission's
    /// vessel keeps the partner phrase, this mission's own same-named vessels are numbered
    /// "Kerbal X [2]", and the Log and the Missions vessel rows read the same names.
    /// </summary>
    [Collection("Sequential")]
    public class MissionVesselNamingTests : IDisposable
    {
        private const string DockingTreeId = "8c677bba0ec04e558c6ebec4ad8c3c9f";
        private const string FirstTreeId = "788554a928324f148dd313dd26322638";
        private const string DockingRoot = "5157d6555bd3499592c46d8508dbedf4";
        private const string DockedStack = "f049901e1f4641ffae490b2f52b1d55e";
        private const string PartnerHalf = "37d0dc074351408ba0374230793abb1c";
        private const string OwnHalf = "4af6cfd725d646ccbac9ef2f7749667e";
        private const string DockingProbe = "500c0ba9c18b4e2f96d64dd4d3b40b63";
        private const string Partner = "Kerbal X (mission 'Kerbal X')";

        private readonly ITestOutputHelper output;

        public MissionVesselNamingTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = true;
            MissionStructureListBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            DockEventGraph.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionStructureBuilder.SuppressLogging = false;
            MissionStructureListBuilder.SuppressLogging = false;
            MissionCompositionBuilder.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
        }

        private static string MissionNameOf(string treeId)
            => treeId == FirstTreeId ? "Kerbal X" : treeId == DockingTreeId ? "Kerbal X #2" : null;

        private static Dictionary<string, string> Names(
            List<RecordingTree> trees, RecordingTree tree, out MissionVesselNaming.Tally tally)
            => MissionVesselNaming.Build(tree, MissionStructureBuilder.Build(tree),
                MissionVesselNaming.LaunchIndex.Build(trees), MissionNameOf, out tally);

        // ---------------------------------------------------------------- bdock-recorded

        // The GUI-4 docking mission. Verified against the fixture: the undocked half 37d0dc07
        // and the docked stack f049901e carry the FIRST mission's launch identity (pid
        // 3620499050, guid 97813bb6...), so they are that mission's Kerbal X; 4af6cfd7 is this
        // mission's own ship, which KSP re-pidded at the undock (fresh pid, no guid) and which
        // shares 7 part pids with the root 5157d655 - the SAME vessel, so no number anywhere.
        // catches: the first cut of the rule, which numbered it "Kerbal X [2]".
        [Fact]
        public void DockingMission_PartnerHalvesKeepThePhrase_RepiddedOwnShipIsNotNumbered()
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            Dictionary<string, string> names = Names(trees, tree, out MissionVesselNaming.Tally tally);
            foreach (var kv in names.OrderBy(k => k.Key, StringComparer.Ordinal))
                output.WriteLine(kv.Key + " -> " + kv.Value);

            Assert.Equal("Kerbal X", names[DockingRoot]);
            Assert.Equal("Kerbal X Probe", names[DockingProbe]);
            Assert.Equal(Partner, names[DockedStack]);
            Assert.Equal(Partner, names[PartnerHalf]);
            Assert.Equal("Kerbal X", names[OwnHalf]);
            Assert.Equal(5, tally.Legs);
            Assert.Equal(2, tally.Partners);
            Assert.Equal(0, tally.Numbered);
            Assert.Equal(1, tally.Continuations);
        }

        // The mirror direction: the FIRST mission's own Kerbal X matches the docking tree's
        // stack too, but it launched first, so it is nobody's partner and nothing is numbered.
        [Fact]
        public void FirstMission_LaunchedFirst_IsNotAPartner()
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == FirstTreeId);
            Dictionary<string, string> names = Names(trees, tree, out MissionVesselNaming.Tally tally);
            Assert.Equal("Kerbal X", names["a32f62f52dc84d6a94daf93460ec6548"]);
            Assert.Equal("Kerbal X Probe", names["b07cfd6cc27d47e7a6fb497d9836e665"]);
            Assert.Equal(0, tally.Partners);
            Assert.Equal(0, tally.Numbered);
        }

        [Fact]
        public void DockingMission_LogReadsTheSharedNames()
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            Dictionary<string, string> names = Names(trees, tree, out _);
            DockEventGraph graph = DockEventGraph.Build(trees, id => true, "test");
            Func<string, string, string> missionNames = (treeId, recId) => MissionNameOf(treeId);
            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StructureListBdockFixtureTests.StockTitles,
                (bp, viewer) => MissionStructureListBuilder.DescribeMergePartnerFromGraph(
                    graph, tree.Id, bp, viewer, missionNames),
                null, names);
            string[] rows = steps.Select(s => s.Label + " | " + s.Location + " | " + s.VesselName).ToArray();
            foreach (string r in rows) output.WriteLine(r);

            var expected = new[]
            {
                "Launch | Kerbin, Launch Pad | Kerbal X",
                "Staged: 3 pieces (TT18-A Launch Stability Enhancer x3) | Kerbin, Launch Pad | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Decoupled (Kerbal X Probe) | Kerbin | Kerbal X",
                "Docked (" + Partner + ") | Kerbin orbit | Kerbal X",
                "Undocked (Kerbal X) | Kerbin orbit | " + Partner,
                "End: Orbiting | Kerbin orbit | Kerbal X",
                "End: Orbiting | Kerbin orbit | " + Partner,
                "End: Orbiting | Kerbin orbit | Kerbal X Probe",
            };
            Assert.DoesNotContain(rows, r => r.Contains("["));
            Assert.Equal(expected, rows);
        }

        [Fact]
        public void DockingMission_VesselRowsReadTheSharedNames()
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            Dictionary<string, string> names = Names(trees, tree, out _);
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            List<MissionVesselRow> rows = MissionVesselRowBuilder.Build(
                MissionCompositionBuilder.Build(structure),
                (head, ut) => MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                    structure, view, head, ut, names),
                names);

            MissionVesselRow main = Assert.Single(rows);
            string a = MissionPresentation.SummarySpanArrow;
            output.WriteLine(main.VesselName + " | " + main.EventPhrase);
            foreach (MissionVesselRow c in main.Children)
                output.WriteLine("  " + c.VesselName + " | " + c.EventPhrase);
            Assert.Equal("Kerbal X", main.VesselName);
            Assert.Equal("Launch" + a + "Decoupled (Kerbal X Probe)" + a + "Docked" + a
                + "Undocked (Kerbal X)" + a + "Orbiting", main.EventPhrase);
            Assert.Equal(new[] { "Kerbal X Probe", "Kerbal X" },
                main.Children.Select(c => c.VesselName).ToArray());
            // The expanded interval line names the peel with the same helper.
            MissionCompositionNode root = MissionCompositionBuilder.Build(structure)[0];
            MissionCompositionNode undockInterval = FindInterval(root, n => n.StartEvent == "Undocked");
            MissionCompositionNode undockParent = FindParent(root, undockInterval);
            Assert.Equal("Kerbal X", MissionPresentation.ResolvePeeledSiblingVesselName(
                undockParent, undockInterval, names));
        }

        private static MissionCompositionNode FindInterval(
            MissionCompositionNode n, Func<MissionCompositionNode, bool> match)
        {
            if (n == null) return null;
            if (match(n)) return n;
            foreach (MissionCompositionNode c in n.Children)
            {
                MissionCompositionNode hit = FindInterval(c, match);
                if (hit != null) return hit;
            }
            return null;
        }

        private static MissionCompositionNode FindParent(
            MissionCompositionNode n, MissionCompositionNode target)
        {
            if (n == null) return null;
            foreach (MissionCompositionNode c in n.Children)
            {
                if (ReferenceEquals(c, target)) return n;
                MissionCompositionNode hit = FindParent(c, target);
                if (hit != null) return hit;
            }
            return null;
        }

        // GUI-3's Duna mission had the same shape: its own ship 1331a21b got a fresh pid AND
        // a fresh launch guid at the undock from the depot, and shares 17 part pids with its
        // own chain segment 36c7688b, so it is the same Duna Supply 1, not a second one.
        [Fact]
        public void DunaMission_RepiddedOwnShipWithFreshGuid_IsTheSameVessel()
        {
            List<RecordingTree> trees =
                StructureListBdockFixtureTests.LoadFixtureTrees("interbody-route-recorded", 4);
            RecordingTree tree = trees.Single(t => t.Id == "3daf0cff159d413794c626137cd81002");
            Dictionary<string, string> names = MissionVesselNaming.Build(
                tree, MissionStructureBuilder.Build(tree),
                MissionVesselNaming.LaunchIndex.Build(trees), id => "M", out MissionVesselNaming.Tally tally);
            string ownHalf = names.Keys.Single(k => k.StartsWith("1331a21b", StringComparison.Ordinal));
            Assert.Equal("Duna Supply 1", names[ownHalf]);
            Assert.Equal(1, tally.Continuations);
            Assert.Equal(0, tally.Numbered);
            Assert.DoesNotContain(names.Values, v => v.Contains("["));
        }

        // ---------------------------------------------------------------- synthetic shapes

        private static Recording Rec(string id, string vessel, double start, double end,
            uint pid = 0, string guid = null, string chain = null, int chainIndex = -1,
            string eva = null, params uint[] partPids)
        {
            var r = new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                VesselPersistentId = pid,
                RecordedVesselGuid = guid,
                ChainId = chain,
                ChainIndex = chainIndex,
                EvaCrewName = eva,
                TerminalStateValue = TerminalState.Orbiting,
                StartBodyName = "Kerbin",
            };
            foreach (uint p in partPids)
                r.PartEvents.Add(new PartEvent
                {
                    ut = start + 1, partPersistentId = p, eventType = PartEventType.EngineIgnited,
                });
            return r;
        }

        private static BranchPoint BP(string id, BranchPointType type, double ut,
            string[] parents, string[] children)
            => new BranchPoint
            {
                Id = id, Type = type, UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };

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

        private static Dictionary<string, string> Names(RecordingTree tree, params RecordingTree[] others)
        {
            var all = new List<RecordingTree> { tree };
            all.AddRange(others);
            return MissionVesselNaming.Build(tree, MissionStructureBuilder.Build(tree),
                MissionVesselNaming.LaunchIndex.Build(all), id => "M-" + id, out _);
        }

        // catches: an undock of two same-named own halves at one UT numbering by GUID order;
        // the half that kept the parent's parts is the one that continues it.
        [Fact]
        public void Tie_TheVesselSharingParentPartsKeepsThePlainName()
        {
            Recording stack = Rec("stack", "Combined", 0, 100, pid: 1, guid: "g1", partPids: new uint[] { 500, 900 });
            Recording kept = Rec("zz-kept", "Ship", 100, 200, pid: 2, partPids: 500);
            Recording other = Rec("aa-other", "Ship", 100, 200, pid: 3, partPids: 77);
            RecordingTree tree = Tree("t", new[] { stack, kept, other },
                BP("u", BranchPointType.Undock, 100, new[] { "stack" }, new[] { "zz-kept", "aa-other" }));
            Dictionary<string, string> names = Names(tree);
            Assert.Equal("Combined", names["stack"]);
            Assert.Equal("Ship", names["zz-kept"]);
            Assert.Equal("Ship [2]", names["aa-other"]);
        }

        [Fact]
        public void Tie_WithNoPartEvidence_FallsBackToRecordingId()
        {
            Recording root = Rec("root", "Lander", 0, 100, pid: 1, guid: "g1");
            Recording b = Rec("b", "Probe", 100, 200, pid: 2);
            Recording a = Rec("a", "Probe", 100, 200, pid: 3);
            RecordingTree tree = Tree("t", new[] { root, b, a },
                BP("s", BranchPointType.JointBreak, 100, new[] { "root" }, new[] { "b", "a" }));
            Dictionary<string, string> names = Names(tree);
            Assert.Equal("Probe", names["a"]);
            Assert.Equal("Probe [2]", names["b"]);
        }

        // catches: a ship re-pidded at an undock (fresh pid, fresh guid) numbered as a second
        // vessel of its own mission. It descends from the own ship after that ship's legs
        // ended (it docked into a partner's stack) and shares its part pids.
        [Fact]
        public void RepiddedOwnShipAfterUndock_KeepsItsName()
        {
            RecordingTree station = Tree("S", new[] { Rec("s1", "Station", 0, 500, pid: 100, guid: "GS") });
            Recording ship = Rec("ship", "Ship", 10, 50, pid: 200, guid: "G2", partPids: new uint[] { 11, 12, 13 });
            Recording stack = Rec("stack", "Station", 50, 60, pid: 100, guid: "GS", partPids: new uint[] { 11, 900 });
            Recording stationHalf = Rec("halfS", "Station", 60, 90, pid: 100, guid: "GS", partPids: 900);
            Recording shipHalf = Rec("halfA", "Ship", 60, 90, pid: 201, guid: "G3", partPids: new uint[] { 12, 13 });
            RecordingTree mission = Tree("A", new[] { ship, stack, stationHalf, shipHalf },
                BP("d", BranchPointType.Dock, 50, new[] { "ship" }, new[] { "stack" }),
                BP("u", BranchPointType.Undock, 60, new[] { "stack" }, new[] { "halfS", "halfA" }));
            ship.TerminalStateValue = TerminalState.Docked;
            Dictionary<string, string> names = MissionVesselNaming.Build(
                mission, MissionStructureBuilder.Build(mission),
                MissionVesselNaming.LaunchIndex.Build(new[] { mission, station }), id => "M-" + id,
                out MissionVesselNaming.Tally tally);
            Assert.Equal("Ship", names["ship"]);
            Assert.Equal("Ship", names["halfA"]);
            Assert.Equal("Station (mission 'M-S')", names["stack"]);
            Assert.Equal("Station (mission 'M-S')", names["halfS"]);
            Assert.Equal(1, tally.Continuations);
            Assert.Equal(0, tally.Numbered);
        }

        // catches: the continuation rule swallowing a genuine twin - a same-named piece that
        // decouples from a ship that carries on is a second vessel even though it shares the
        // parent's part pids.
        [Fact]
        public void SameNamedPieceDecoupledFromAShipThatCarriesOn_IsStillNumbered()
        {
            Recording root = Rec("root", "Ship", 0, 300, pid: 1, guid: "g1", partPids: new uint[] { 5, 6 });
            Recording piece = Rec("piece", "Ship", 100, 200, pid: 2, partPids: 6);
            RecordingTree tree = Tree("t", new[] { root, piece },
                BP("s", BranchPointType.JointBreak, 100, new[] { "root" }, new[] { "piece" }));
            Dictionary<string, string> names = Names(tree);
            Assert.Equal("Ship", names["root"]);
            Assert.Equal("Ship [2]", names["piece"]);
        }

        // catches: an optimizer chain split or a same-launch continuation numbered as a second
        // vessel; a genuinely different later vessel of the same name is.
        [Fact]
        public void ChainSegments_ShareOneName_ALaterVesselIsNumbered()
        {
            Recording head = Rec("head", "X", 0, 50, pid: 1, guid: "g1", chain: "c", chainIndex: 0);
            Recording tail = Rec("tail", "X", 50, 100, pid: 1, guid: "g1", chain: "c", chainIndex: 1);
            Recording later = Rec("later", "X", 80, 120, pid: 9);
            Recording eva = Rec("eva", "X", 90, 95, pid: 0, eva: "Bob Kerman");
            RecordingTree tree = Tree("t", new[] { head, tail, later, eva },
                BP("s", BranchPointType.JointBreak, 80, new[] { "tail" }, new[] { "later" }),
                BP("e", BranchPointType.EVA, 90, new[] { "tail" }, new[] { "eva" }));
            Dictionary<string, string> names = Names(tree);
            Assert.Equal("X", names["head"]);
            Assert.Equal("X", names["tail"]);
            Assert.Equal("X [2]", names["later"]);
            Assert.False(names.ContainsKey("eva"));
        }

        // catches: the partner test running the wrong way (the mission that launched first
        // reading its own vessel as the other mission's) or reading a root as a partner.
        [Fact]
        public void Partner_IsTheOtherMissionsEarlierLaunch_NeverARoot()
        {
            RecordingTree a = Tree("A", new[] { Rec("a1", "Station", 0, 500, pid: 100, guid: "G1") });
            Recording b1 = Rec("b1", "Tug", 10, 50, pid: 200, guid: "G2");
            Recording b2 = Rec("b2", "Station", 50, 80, pid: 100, guid: "G1");
            RecordingTree b = Tree("B", new[] { b1, b2 },
                BP("d", BranchPointType.Dock, 50, new[] { "b1" }, new[] { "b2" }));
            RecordingTree c = Tree("C", new[] { Rec("c1", "Station", 600, 700, pid: 100, guid: "G1") });

            Assert.Equal("Station (mission 'M-A')", Names(b, a, c)["b2"]);
            Assert.Equal("Tug", Names(b, a, c)["b1"]);
            Assert.Equal("Station", Names(a, b, c)["a1"]);
            Assert.Equal("Station", Names(c, a, b)["c1"]);
        }

        // catches: a craft-baked pid read as identity when the launch guids conclusively differ.
        [Fact]
        public void Partner_NeedsTheLaunchGuidToAgree()
        {
            RecordingTree a = Tree("A", new[] { Rec("a1", "Kerbal X", 0, 500, pid: 100, guid: "G1") });
            Recording b1 = Rec("b1", "Tug", 10, 50, pid: 200, guid: "G2");
            Recording b2 = Rec("b2", "Kerbal X", 50, 80, pid: 100, guid: "G9");
            RecordingTree b = Tree("B", new[] { b1, b2 },
                BP("s", BranchPointType.JointBreak, 50, new[] { "b1" }, new[] { "b2" }));
            Assert.Equal("Kerbal X", Names(b, a)["b2"]);
        }

        [Fact]
        public void FormatNumbered_SquareBrackets_PlainForTheFirst()
        {
            Assert.Equal("Kerbal X", MissionVesselNaming.FormatNumbered("Kerbal X", 1));
            Assert.Equal("Kerbal X [2]", MissionVesselNaming.FormatNumbered("Kerbal X", 2));
            Assert.Equal("Kerbal X [12]", MissionVesselNaming.FormatNumbered("Kerbal X", 12));
        }
    }
}
