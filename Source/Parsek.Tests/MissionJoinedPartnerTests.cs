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
            return BuildRowsLikeTheWindow(tree, structure, trees, out _);
        }

        // The Missions tab's wiring (MissionsWindowUI.GetVesselRows): the naming pass's names
        // and partner legs, the same-tree dock resolver and the terminal dock resolver.
        private static List<MissionVesselRow> BuildRowsLikeTheWindow(RecordingTree tree,
            MissionStructure structure, List<RecordingTree> trees, out HashSet<string> partners)
        {
            var partnerSet = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> names = MissionVesselNaming.Build(
                tree, structure, MissionVesselNaming.LaunchIndex.Build(trees), MissionOf,
                out MissionVesselNaming.Tally _, null, partnerSet);
            partners = partnerSet;
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            return MissionVesselRowBuilder.Build(
                MissionCompositionBuilder.Build(structure),
                (head, ut) => MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                    structure, view, head, ut, names),
                names, partnerSet,
                (head, ut) => MissionPresentation.ResolveTerminalDockPartnerVesselName(
                    structure, head, ut, names),
                MissionVesselRowBuilder.ResolveUndockSides(structure, partnerSet));
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
        // from the ship they docked with. The partner is a row BESIDE the ship (the ship is a
        // root here, so at the top level), named with its mission, with no "Launch".
        [Theory]
        [InlineData(DunaTreeId, DunaHead, DunaJoinedDepot,
            "Depot Station Duna I (mission 'Kerbal X #5')", "Docked (Duna Supply 1)")]
        [InlineData(MunTreeId, MunHead, MunJoinedPartner,
            "Kerbal X (mission 'Kerbal X #3')", "Docked (Kerbal X)")]
        public void JoinedPartner_IsASiblingRowOfTheShipItDockedWith(string treeId, string shipHead,
            string joinedId, string partnerName, string partnerPhrase)
        {
            List<MissionVesselRow> rows = BuildRows(treeId);
            foreach (MissionVesselRow r in AllRows(rows))
                output.WriteLine((r.IsPartner ? "[partner] " : "") + r.VesselName + "   " + r.EventPhrase);

            int shipAt = rows.FindIndex(r => r.OwnerHeadId == shipHead);
            Assert.True(shipAt >= 0);
            Assert.DoesNotContain(AllRows(rows[shipAt].Children), r => r.OwnerHeadId == joinedId);
            MissionVesselRow partner = Assert.Single(rows, r => r.OwnerHeadId == joinedId);
            Assert.True(partner.IsPartner);
            Assert.Equal(partnerName, partner.VesselName);
            Assert.Equal("", partner.StartEvent);
            Assert.Equal(partnerPhrase, partner.EventPhrase);
            Assert.Equal(new[] { joinedId }, MissionVesselRowBuilder.IntervalKeys(partner).ToArray());
            // No row anywhere starts at, or names, a mid-flight "Launch" except the real one.
            Assert.All(AllRows(rows).Where(r => r.OwnerHeadId != shipHead),
                r => Assert.NotEqual("Launch", r.StartEvent));
            Assert.All(AllRows(rows), r => Assert.DoesNotContain("Launch (", r.EventPhrase));
        }

        // catches: the Missions tab's cached naming pass not handing the window the partner
        // legs its rows and vessel count read, or keeping them past a state change.
        [Fact]
        public void NamingCache_HandsBackThePartnerLegs()
        {
            List<RecordingTree> trees = LoadTrees();
            RecordingTree tree = trees.Single(t => t.Id == DunaTreeId);
            var cache = new MissionVesselNaming.Cache();
            Assert.Null(cache.PartnerLegIds(DunaTreeId));
            cache.GetOrBuild(tree, MissionStructureBuilder.Build, trees, 1, 1, MissionOf);
            HashSet<string> partners = cache.PartnerLegIds(DunaTreeId);
            Assert.Contains(DunaJoinedDepot, partners);
            Assert.DoesNotContain(DunaHead, partners);
            cache.GetOrBuild(trees[0].Id == DunaTreeId ? trees[1] : trees[0],
                MissionStructureBuilder.Build, trees, 2, 1, MissionOf);
            Assert.Null(cache.PartnerLegIds(DunaTreeId));   // the state version moved
        }

        // catches: a partner key the player excluded before the fix becoming unreachable: the
        // partner row's checkbox re-includes it (and trims it again), touching no other key.
        [Fact]
        public void JoinedPartner_ExcludedKey_CanBeReincludedThroughItsRow()
        {
            MissionVesselRow partner = BuildRows(DunaTreeId).Single(r => r.OwnerHeadId == DunaJoinedDepot);
            var excluded = new List<string> { DunaJoinedDepot, DunaHead + "/seg1" };
            Assert.Equal(MissionVesselInclusion.None,
                MissionVesselRowBuilder.ClassifyInclusion(partner, excluded));
            Assert.Equal(1, MissionVesselRowBuilder.ApplyVesselInclusion(partner, true, excluded));
            Assert.Equal(new[] { DunaHead + "/seg1" }, excluded.ToArray());
            Assert.Equal(1, MissionVesselRowBuilder.ApplyVesselInclusion(partner, false, excluded));
            Assert.Equal(MissionVesselInclusion.None,
                MissionVesselRowBuilder.ClassifyInclusion(partner, excluded));
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

            // Each ship's own row owns its four keys up to the undock plus its own post-undock
            // leg (the partner's half, "/seg3", is the partner's row:
            // MissionUndockOwnSideRowTests), and a key stored before the fix still selects the
            // same interval (the one from the join to the dock).
            MissionVesselRow munShip = BuildRows(MunTreeId).Single(r => r.OwnerHeadId == MunHead);
            Assert.Equal(new[]
            {
                MunHead, MunHead + "/seg1", MunHead + "/seg2", MunHead + "/seg2@dock1",
                "b9f08d0f269346ee84162dd763e462aa",
            }, MissionVesselRowBuilder.IntervalKeys(munShip).ToArray());
            MissionVesselRow ship = BuildRows(DunaTreeId).Single(r => r.OwnerHeadId == DunaHead);
            Assert.Equal(new[]
            {
                DunaHead, DunaHead + "/seg1", DunaHead + "/seg2", DunaHead + "/seg2@dock1",
                "1331a21bddfb49418be6ebec99dabf98",
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

        // catches: the mission summary counting another mission's joined vessel as its own.
        [Fact]
        public void JoinedPartner_IsNotCountedAsAMissionVessel()
        {
            List<RecordingTree> trees = LoadTrees();
            RecordingTree tree = trees.Single(t => t.Id == DunaTreeId);
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            List<MissionVesselRow> rows = BuildRowsLikeTheWindow(tree, structure, trees,
                out HashSet<string> partners);
            Assert.Contains(DunaJoinedDepot, partners);
            MissionPresentation.MissionSummaryFacts facts = MissionPresentation.ComputeSummaryFacts(
                structure, MissionThroughLineBuilder.Build(structure),
                MissionCompositionBuilder.Build(structure), partners);
            // Duna Supply 1 (its own post-undock leg is the same ship's row, which carries
            // that leg's Fly / Seal) and its probe; the depot's half is a partner row.
            Assert.Equal(2, facts.VesselCount);
            Assert.Equal(2, AllRows(rows).Count(r => !r.IsPartner));
        }

        // catches (review): a joined leg the naming pass calls this mission's own - in no
        // other tree's launch index (a pre-Parsek station, a stock or contract vessel, a craft
        // from a never-committed tree, this tree's own debris) - hidden and uncounted because
        // the join is structural. It keeps an ordinary row under the vessel it joined at, and
        // it counts.
        [Fact]
        public void JoinedLegInNoLaunchIndex_KeepsAnOrdinaryRowAndItsCount()
        {
            var ship = new Recording
            {
                RecordingId = "ship", VesselName = "Ship", VesselPersistentId = 11,
                RecordedVesselGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ChainId = "c", ChainIndex = 0,
                ExplicitStartUT = 0, ExplicitEndUT = 50,
                Controllers = new List<ControllerInfo> { new ControllerInfo { type = "ProbeCore" } },
            };
            var shipCont = new Recording
            {
                RecordingId = "shipCont", VesselName = "Ship", VesselPersistentId = 11,
                RecordedVesselGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ChainId = "c", ChainIndex = 1,
                ExplicitStartUT = 50, ExplicitEndUT = 200, TerminalStateValue = TerminalState.Orbiting,
                Controllers = new List<ControllerInfo> { new ControllerInfo { type = "ProbeCore" } },
            };
            var station = new Recording
            {
                RecordingId = "station", VesselName = "Old Station", VesselPersistentId = 22,
                RecordedVesselGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                ExplicitStartUT = 50, ExplicitEndUT = 180, TerminalStateValue = TerminalState.Orbiting,
                Controllers = new List<ControllerInfo> { new ControllerInfo { type = "ProbeCore" } },
            };
            var tree = new RecordingTree { Id = "own", TreeName = "Ship", RootRecordingId = "ship" };
            foreach (Recording r in new[] { ship, shipCont, station }) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "launchbp", Type = BranchPointType.Launch, UT = 50,
                ParentRecordingIds = new List<string> { "ship" },
                ChildRecordingIds = new List<string> { "station" },
            });
            station.ParentBranchPointId = "launchbp";

            var trees = new List<RecordingTree> { tree };
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            List<MissionVesselRow> rows = BuildRowsLikeTheWindow(tree, structure, trees,
                out HashSet<string> partners);
            Assert.Empty(partners);
            MissionVesselRow shipRow = Assert.Single(rows);
            MissionVesselRow stationRow = Assert.Single(shipRow.Children);
            Assert.Equal("station", stationRow.OwnerHeadId);
            Assert.Equal("Old Station", stationRow.VesselName);
            Assert.False(stationRow.IsPartner);
            Assert.Equal("", stationRow.StartEvent);   // it did not launch there
            MissionPresentation.MissionSummaryFacts facts = MissionPresentation.ComputeSummaryFacts(
                structure, MissionThroughLineBuilder.Build(structure),
                MissionCompositionBuilder.Build(structure), partners);
            Assert.Equal(2, facts.VesselCount);
        }

        // catches: a kerbal's row that ends boarding a vessel reading a bare "Boarded" - the
        // terminal resolver names what the line joined (CHANGELOG: "Boarded (Kerbal X)").
        [Fact]
        public void KerbalRowEndingInBoarding_NamesTheVessel()
        {
            var ship = new Recording
            {
                RecordingId = "ship", VesselName = "Kerbal X", ExplicitStartUT = 0, ExplicitEndUT = 100,
                Controllers = new List<ControllerInfo> { new ControllerInfo { type = "CrewedPod" } },
            };
            var eva = new Recording
            {
                RecordingId = "eva", VesselName = "Bob Kerman", EvaCrewName = "Bob Kerman",
                ExplicitStartUT = 40, ExplicitEndUT = 100, TerminalStateValue = TerminalState.Boarded,
                Controllers = new List<ControllerInfo> { new ControllerInfo { type = "KerbalEVA" } },
            };
            var merged = new Recording
            {
                RecordingId = "merged", VesselName = "Kerbal X", ExplicitStartUT = 100, ExplicitEndUT = 200,
                TerminalStateValue = TerminalState.Orbiting,
                Controllers = new List<ControllerInfo> { new ControllerInfo { type = "CrewedPod" } },
            };
            var tree = new RecordingTree { Id = "b", RootRecordingId = "ship" };
            foreach (Recording r in new[] { ship, eva, merged }) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "evabp", Type = BranchPointType.EVA, UT = 40,
                ParentRecordingIds = new List<string> { "ship" }, ChildRecordingIds = new List<string> { "eva" },
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "board", Type = BranchPointType.Board, UT = 100,
                ParentRecordingIds = new List<string> { "ship", "eva" },
                ChildRecordingIds = new List<string> { "merged" },
            });
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            List<MissionVesselRow> rows = MissionVesselRowBuilder.Build(
                MissionCompositionBuilder.Build(structure), null, null, null,
                (head, ut) => MissionPresentation.ResolveTerminalDockPartnerVesselName(structure, head, ut));
            MissionVesselRow bob = AllRows(rows).Single(r => r.IsPerson);
            output.WriteLine(bob.EventPhrase);
            Assert.EndsWith("Boarded (Kerbal X)", bob.EventPhrase);
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

        // catches (review): an own child with no other child recorded naming nothing; the
        // label falls back to the combined vessel the ship undocked from.
        [Fact]
        public void UndockWithOnlyTheOwnChildRecorded_NamesTheCombinedVessel()
        {
            RecordingTree tree = Tree(new[]
                {
                    Rec("combined", "Depot", 0, 100, 2),
                    Rec("ship", "Deliverer", 100, 200, 3, TerminalState.Orbiting),
                },
                BP("undock", BranchPointType.Undock, 100, new[] { "combined" }, new[] { "ship" }));
            StructureStep undock = Log(tree, "combined").Single(s => s.Kind == StructureStepKind.Undock);
            Assert.Equal("Undocked (Depot)", undock.Label);
            Assert.Equal("Deliverer", undock.VesselName);
            Assert.Equal("combined", undock.RecordingId);
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
