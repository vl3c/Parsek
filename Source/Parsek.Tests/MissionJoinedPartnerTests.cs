using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// Another mission's vessel the player switched to and flew to a dock joins the tree under
    /// a <see cref="BranchPointType.Launch"/> branch point
    /// (<c>ParsekFlight.PrepareActiveTreeForFreshPostSwitchRecording</c>). The committed
    /// <c>interbody-route-recorded</c> fixture carries two such joins: Duna Supply 1 docking
    /// with "Depot Station Duna I" (mission 'Kerbal X #5'), and Kerbal X #4 docking with
    /// "Kerbal X" (mission 'Kerbal X #3') and undocking as "Deliverer Mun 1". Each cell names
    /// the defect it pins. The Log cells build exactly as <c>StructureListWindowUI</c> does,
    /// with the naming pass's partner legs.
    /// </summary>
    [Collection("Sequential")]
    public class MissionJoinedPartnerTests : IDisposable
    {
        private readonly ITestOutputHelper output;

        private const string DunaTreeId = "3daf0cff159d413794c626137cd81002";
        private const string DunaHead = "d23e453bc982482b850ce717ba83bffd";
        private const string DunaJoinedDepot = "3700f40e66c84ff79ce5197b362cf937";
        private const string MunTreeId = "02382fcdcb1a465388529350c0879cd6";
        private const string MunHead = "c549ef6e4ed14f0fb93236a72f435cb4";
        private const string MunJoinedPartner = "5737c255fba64ad6aa062b3fd7b0683d";
        private const string MunCombined = "4759c914f4ce4e789abc0dd91238e75b";

        public MissionJoinedPartnerTests(ITestOutputHelper output)
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

        // The fixture's missions, as each tree's original mission is named in the career.
        private static string MissionOf(string treeId)
        {
            switch (treeId)
            {
                case "400394def77b42d39596a86e325d8d75": return "Kerbal X #3";
                case MunTreeId: return "Kerbal X #4";
                case "54c5efe5ab1942fda6f53ffdfe8d4ce5": return "Kerbal X #5";
                case DunaTreeId: return "Duna Supply 1";
                default: return null;
            }
        }

        private static List<RecordingTree> LoadTrees()
            => StructureListBdockFixtureTests.LoadFixtureTrees("interbody-route-recorded", 4);

        private static List<MissionVesselRow> BuildRows(string treeId)
        {
            List<RecordingTree> trees = LoadTrees();
            RecordingTree tree = trees.Single(t => t.Id == treeId);
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            Dictionary<string, string> names = MissionVesselNaming.Build(
                tree, structure, MissionVesselNaming.LaunchIndex.Build(trees), MissionOf,
                out MissionVesselNaming.Tally _);
            return MissionVesselRowBuilder.Build(
                MissionCompositionBuilder.Build(structure), null, names);
        }

        private static IEnumerable<MissionVesselRow> AllRows(List<MissionVesselRow> rows)
        {
            foreach (MissionVesselRow r in rows)
            {
                yield return r;
                foreach (MissionVesselRow c in AllRows(r.Children))
                    yield return c;
            }
        }

        private static List<string> SelectableKeys(string treeId)
        {
            RecordingTree tree = LoadTrees().Single(t => t.Id == treeId);
            var keys = new List<string>();
            foreach (MissionCompositionNode root in MissionCompositionBuilder.Build(
                         MissionStructureBuilder.Build(tree)))
                CollectKeys(root, keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static void CollectKeys(MissionCompositionNode node, List<string> into)
        {
            if (node.IsSelectable) into.Add(node.HeadLegId);
            foreach (MissionCompositionNode c in node.Children) CollectKeys(c, into);
        }

        // ------------------------------------------------------------- Missions tab rows

        // catches: "Depot Station Duna I (mission 'Kerbal X #5')   Launch -> Docked" and
        // "Kerbal X (mission 'Kerbal X #3')   Launch -> Docked" listed as pieces that separated
        // from the ship they docked with.
        [Theory]
        [InlineData(DunaTreeId, DunaJoinedDepot)]
        [InlineData(MunTreeId, MunJoinedPartner)]
        public void JoinedPartner_IsNoVesselRowOfThisMission(string treeId, string joinedId)
        {
            List<MissionVesselRow> rows = BuildRows(treeId);
            foreach (MissionVesselRow r in AllRows(rows))
                output.WriteLine(r.VesselName + "   " + r.EventPhrase);

            Assert.DoesNotContain(AllRows(rows), r => r.OwnerHeadId == joinedId);
            Assert.DoesNotContain(AllRows(rows), r => r.VesselName.Contains("(mission '"));
            // Every row below the top is a real separation (decouple / undock), never a launch.
            foreach (MissionVesselRow top in rows)
                Assert.All(AllRows(top.Children), r => Assert.NotEqual("Launch", r.StartEvent));
        }

        // catches: the fix renumbering the interval keys Mission.ExcludedIntervalKeys stores.
        // The lists are the keys this fixture produced BEFORE the fix (the joined vessel's own
        // key included: its node stays in the composition), so a removed or reordered edge
        // reds here.
        [Fact]
        public void JoinedPartner_KeepsEveryStoredIntervalKey()
        {
            Assert.Equal(new[]
            {
                "1331a21bddfb49418be6ebec99dabf98",
                DunaJoinedDepot,
                "5ca48c99fa55435e8cf8547a6ef27a39",
                DunaHead,
                DunaHead + "/seg1",
                DunaHead + "/seg2",
                DunaHead + "/seg2@dock1",
                DunaHead + "/seg3",
            }, SelectableKeys(DunaTreeId).ToArray());
            Assert.Equal(new[]
            {
                "2b28d6020d9d419e861f23124e50dc07",
                MunJoinedPartner,
                "b9f08d0f269346ee84162dd763e462aa",
                MunHead,
                MunHead + "/seg1",
                MunHead + "/seg2",
                MunHead + "/seg2@dock1",
                MunHead + "/seg3",
            }, SelectableKeys(MunTreeId).ToArray());

            // The ship's own row still owns the same five keys, and a key stored before the fix
            // still selects the same interval (the one from the join to the dock).
            MissionVesselRow ship = BuildRows(DunaTreeId).Single(r => r.OwnerHeadId == DunaHead);
            Assert.Equal(new[]
            {
                DunaHead, DunaHead + "/seg1", DunaHead + "/seg2", DunaHead + "/seg2@dock1",
                DunaHead + "/seg3",
            }, MissionVesselRowBuilder.IntervalKeys(ship).ToArray());
            var excluded = new List<string> { DunaHead + "/seg2" };
            Assert.Equal(MissionVesselInclusion.Partial,
                MissionVesselRowBuilder.ClassifyInclusion(ship, excluded));
            MissionCompositionNode seg2 = ship.Intervals[2];
            Assert.False(MissionIntervalSelection.IsIntervalIncluded(seg2, excluded));
            Assert.InRange(seg2.StartUT, 72353179.0, 72353180.0);   // the depot joins
            Assert.InRange(seg2.EndUT, 72353218.5, 72353219.0);     // the dock
        }

        // catches: the expanded interval rows (Advanced) reading "Decoupled -> Launch" and
        // "Launch -> Docked" at the join, and the interval after it labelled "(crew x1)" - the
        // joined vessel's pod subtracted from a ship it was never part of.
        [Theory]
        [InlineData(DunaTreeId, DunaHead, "pod x1, crew x1")]
        [InlineData(MunTreeId, MunHead, "pod x1, crew x1")]
        public void JoinBoundary_ReadsAsAPlainContinuation(string treeId, string head, string label)
        {
            MissionVesselRow ship = BuildRows(treeId).Single(r => r.OwnerHeadId == head);
            Assert.Equal("Launch", ship.Intervals[0].StartEvent);
            for (int i = 0; i < ship.Intervals.Count; i++)
            {
                MissionCompositionNode iv = ship.Intervals[i];
                output.WriteLine(iv.HeadLegId + "  " + iv.StartEvent + " -> " + iv.EndEvent
                    + "  (" + iv.CompositionLabel + ")");
                if (i > 0) Assert.NotEqual("Launch", iv.StartEvent);
                Assert.NotEqual("Launch", iv.EndEvent);
            }
            Assert.Equal("Decoupled", ship.Intervals[1].StartEvent);
            Assert.Equal("", ship.Intervals[1].EndEvent);
            Assert.Equal("", ship.Intervals[2].StartEvent);
            Assert.Equal("Docked", ship.Intervals[2].EndEvent);
            Assert.Equal(label, ship.Intervals[1].CompositionLabel);
            Assert.Equal(label, ship.Intervals[2].CompositionLabel);
            Assert.DoesNotContain("Launch", ship.EventPhrase.Substring("Launch".Length));
        }

        // catches: the mission summary counting the joined vessel as one of its own.
        [Fact]
        public void JoinedPartner_IsNotCountedAsAMissionVessel()
        {
            RecordingTree tree = LoadTrees().Single(t => t.Id == DunaTreeId);
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionPresentation.MissionSummaryFacts facts = MissionPresentation.ComputeSummaryFacts(
                structure, MissionThroughLineBuilder.Build(structure),
                MissionCompositionBuilder.Build(structure));
            // Duna Supply 1, its probe, and its post-undock half (a separate row today).
            Assert.Equal(3, facts.VesselCount);
            Assert.Equal(3, AllRows(BuildRows(DunaTreeId)).Count());
        }

        // ------------------------------------------------------------------ the mission Log

        private static List<StructureStep> BuildLog(List<RecordingTree> trees, string treeId,
            Func<string, string> missionOf, bool withPartners = true)
        {
            RecordingTree tree = trees.Single(t => t.Id == treeId);
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            DockEventGraph graph = DockEventGraph.Build(trees, id => true, "test");
            var partners = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> names = MissionVesselNaming.Build(
                tree, structure, MissionVesselNaming.LaunchIndex.Build(trees), missionOf,
                out MissionVesselNaming.Tally _, null, partners);
            return MissionStructureListBuilder.Build(tree, structure,
                StructureListBdockFixtureTests.StockTitles,
                (bp, viewer) => MissionStructureListBuilder.DescribeMergePartnerFromGraph(
                    graph, tree.Id, bp, viewer, (t, r) => missionOf(t)),
                null, names, withPartners ? partners : null);
        }

        private static string Row(StructureStep s) => s.Label + " | " + s.Location + " | " + s.VesselName;

        // catches: the Kerbal X #4 Log's "Undocked (Deliverer Mun 1) | Kerbal X (mission
        // 'Kerbal X #3')" - the Vessel column naming the partner, because KSP kept the docked
        // pair under the partner's identity, instead of the ship that undocked.
        [Fact]
        public void MunMissionLog_UndockNamesTheShipThatUndocked()
        {
            List<StructureStep> steps = BuildLog(LoadTrees(), MunTreeId, MissionOf);
            foreach (StructureStep s in steps) output.WriteLine(Row(s));

            StructureStep undock = steps.Single(s => s.Kind == StructureStepKind.Undock);
            Assert.Equal("Undocked (Depot (mission 'Kerbal X #3')) | Mun orbit | Deliverer Mun 1",
                Row(undock));
            // The include filter still reads the leg the undock happened on.
            Assert.Equal(MunCombined, undock.RecordingId);
            StructureStep dock = steps.Single(s => s.Kind == StructureStepKind.Dock);
            Assert.Equal("Docked (Kerbal X (mission 'Kerbal X #3')) | Mun orbit | Kerbal X",
                Row(dock));
        }

        // catches: the same wrong side on GUI-3's Duna mission and GUI-4's docking mission.
        [Fact]
        public void DunaAndBdockLogs_UndockNamesThisMissionsShip()
        {
            List<StructureStep> duna = BuildLog(LoadTrees(), DunaTreeId, MissionOf);
            Assert.Equal(
                "Undocked (Depot Station Duna I (mission 'Kerbal X #5')) | Duna orbit | Duna Supply 1",
                Row(duna.Single(s => s.Kind == StructureStepKind.Undock)));
            Assert.Equal(
                "Docked (Depot Station Duna I (mission 'Kerbal X #5')) | Duna orbit | Duna Supply 1",
                Row(duna.Single(s => s.Kind == StructureStepKind.Dock)));

            List<RecordingTree> bdock = StructureListBdockFixtureTests.LoadBdockTrees();
            Func<string, string> bdockMissions = t =>
                t == "788554a928324f148dd313dd26322638" ? "Kerbal X"
                : t == "8c677bba0ec04e558c6ebec4ad8c3c9f" ? "Kerbal X #2" : null;
            List<StructureStep> docking = BuildLog(bdock, "8c677bba0ec04e558c6ebec4ad8c3c9f",
                bdockMissions);
            Assert.Equal("Undocked (Kerbal X (mission 'Kerbal X')) | Kerbin orbit | Kerbal X",
                Row(docking.Single(s => s.Kind == StructureStepKind.Undock)));
            Assert.Equal("Docked (Kerbal X (mission 'Kerbal X')) | Kerbin orbit | Kerbal X",
                Row(docking.Single(s => s.Kind == StructureStepKind.Dock)));
        }

        // catches: the attribution moving without the naming pass's partner set (the column
        // must not guess which side is the mission's).
        [Fact]
        public void MunMissionLog_WithoutPartnerSet_KeepsTheStructuralAttribution()
        {
            List<StructureStep> steps = BuildLog(LoadTrees(), MunTreeId, MissionOf,
                withPartners: false);
            Assert.Equal(
                "Undocked (Deliverer Mun 1) | Mun orbit | Kerbal X (mission 'Kerbal X #3')",
                Row(steps.Single(s => s.Kind == StructureStepKind.Undock)));
        }

        // ------------------------------------------------------ synthetic mirror shapes

        private static Recording Rec(string id, string vessel, double start, double end, uint pid,
            TerminalState? terminal = null)
            => new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                VesselPersistentId = pid,
                StartBodyName = "Kerbin",
                TerminalOrbitBody = "Kerbin",
                StartSituation = "Orbiting",
                TerminalStateValue = terminal,
            };

        private static BranchPoint BP(string id, BranchPointType type, double ut,
            string[] parents, string[] children)
            => new BranchPoint
            {
                Id = id, Type = type, UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };

        private static RecordingTree Tree(Recording[] recs, params BranchPoint[] bps)
        {
            var tree = new RecordingTree { Id = "t", RootRecordingId = recs[0].RecordingId };
            foreach (Recording r in recs) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.AddRange(bps);
            return tree;
        }

        private static List<StructureStep> Log(RecordingTree tree, params string[] partners)
            => MissionStructureListBuilder.Build(tree, MissionStructureBuilder.Build(tree),
                null, null, null, null,
                partners == null ? null : new HashSet<string>(partners, StringComparer.Ordinal));

        // A station of this mission docks with another mission's tug; the station keeps the
        // combined identity and the tug leaves.
        private static RecordingTree StationKeepsIdentity(bool tugFirst)
            => Tree(new[]
                {
                    Rec("station", "Station", 0, 100, 1),
                    Rec("tug", "Tug", 0, 100, 2),
                    Rec("combined", "Station", 100, 200, 1),
                    Rec("stationAfter", "Station", 200, 300, 1, TerminalState.Orbiting),
                    Rec("tugAfter", "Tug", 200, 300, 2, TerminalState.Orbiting),
                },
                BP("dock", BranchPointType.Dock, 100,
                    tugFirst ? new[] { "tug", "station" } : new[] { "station", "tug" },
                    new[] { "combined" }),
                BP("undock", BranchPointType.Undock, 200, new[] { "combined" },
                    new[] { "stationAfter", "tugAfter" }));

        // catches (mirror, passive side): this mission's ship kept the identity and the
        // partner left; the row must keep reading "the station, the tug left".
        [Fact]
        public void PassiveSideUndock_KeepsTheShipThatStayedAndNamesThePartner()
        {
            List<StructureStep> steps = Log(StationKeepsIdentity(false), "tug", "tugAfter");
            StructureStep undock = steps.Single(s => s.Kind == StructureStepKind.Undock);
            Assert.Equal("Undocked (Tug)", undock.Label);
            Assert.Equal("Station", undock.VesselName);
            StructureStep dock = steps.Single(s => s.Kind == StructureStepKind.Dock);
            Assert.Equal("Docked (Tug)", dock.Label);
            Assert.Equal("Station", dock.VesselName);
        }

        // catches (mirror, dock): a two-parent dock that lists the partner first named the
        // partner in the Vessel column and this mission's ship in the label.
        [Fact]
        public void PartnerFirstDock_NamesThisMissionsShip()
        {
            StructureStep before = Log(StationKeepsIdentity(true), null)
                .Single(s => s.Kind == StructureStepKind.Dock);
            Assert.Equal("Tug", before.VesselName);   // structural: the first parent

            StructureStep dock = Log(StationKeepsIdentity(true), "tug", "tugAfter")
                .Single(s => s.Kind == StructureStepKind.Dock);
            Assert.Equal("Docked (Tug)", dock.Label);
            Assert.Equal("Station", dock.VesselName);
            Assert.Equal("tug", dock.RecordingId);
        }

        // catches (mirror, dock into this ship): the partner is the dock's only parent and
        // the docked pair took this mission's identity.
        [Fact]
        public void PartnerOnlyParentDock_NamesThisMissionsShipAndThePartner()
        {
            RecordingTree tree = Tree(new[]
                {
                    Rec("tug", "Tug", 0, 100, 2),
                    Rec("combined", "Station", 100, 200, 1, TerminalState.Orbiting),
                },
                BP("dock", BranchPointType.Dock, 100, new[] { "tug" }, new[] { "combined" }));
            StructureStep dock = Log(tree, "tug").Single(s => s.Kind == StructureStepKind.Dock);
            Assert.Equal("Docked (Tug)", dock.Label);
            Assert.Equal("Station", dock.VesselName);
        }

        // catches: an undock where every recorded side is another mission's vessel inventing
        // an own side; the structural reading stays.
        [Fact]
        public void AllPartnerUndock_KeepsTheStructuralReading()
        {
            List<StructureStep> steps = Log(StationKeepsIdentity(false),
                "station", "tug", "combined", "stationAfter", "tugAfter");
            StructureStep undock = steps.Single(s => s.Kind == StructureStepKind.Undock);
            Assert.Equal("Undocked (Tug)", undock.Label);
            Assert.Equal("Station", undock.VesselName);
        }
    }
}
