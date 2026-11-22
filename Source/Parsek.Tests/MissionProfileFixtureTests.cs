using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// The Mission Profile over committed harness fixtures (design-mission-profile.md section
    /// 11.2, Appendix A): the profile each worked example pins, row by row, and the line set
    /// against the Missions tab's vessel-row set.
    /// </summary>
    [Collection("Sequential")]
    public class MissionProfileFixtureTests : IDisposable
    {
        private readonly ITestOutputHelper output;

        internal const string MunLandingTree = "0da22482a8a648c6835b7bcd6b0f200d";
        internal const string BdockFirstTree = "788554a928324f148dd313dd26322638";
        internal const string BdockDockingTree = "8c677bba0ec04e558c6ebec4ad8c3c9f";
        internal const string MunScienceTree = "101f15b0b8f641cba703154cfe90e39a";

        public MissionProfileFixtureTests(ITestOutputHelper output)
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

        internal sealed class Built
        {
            public RecordingTree Tree;
            public MissionStructure Structure;
            public MissionProfileRequest Request;
            public List<MissionVesselRow> TabRows;
            public MissionProfile Profile;
        }

        internal static Built BuildLikeTheWindow(List<RecordingTree> trees, string treeId,
            Func<string, string> missionOfTree, Mission mission = null,
            ICollection<string> effective = null, IList<RecordingSupersedeRelation> supersedes = null)
        {
            RecordingTree tree = trees.First(t => t.Id == treeId);
            var b = new Built { Tree = tree, Structure = MissionStructureBuilder.Build(tree) };
            var partners = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> names = MissionVesselNaming.Build(tree, b.Structure,
                MissionVesselNaming.LaunchIndex.Build(trees), missionOfTree,
                out MissionVesselNaming.Tally _, null, partners);
            DockEventGraph graph = DockEventGraph.Build(trees, id => true, "test");
            Func<string, string, string> missionNames = (tid, rid) => missionOfTree(tid);
            b.Request = new MissionProfileRequest
            {
                Mission = mission ?? new Mission("m-" + treeId, treeId, missionOfTree(treeId)),
                Tree = tree,
                Structure = b.Structure,
                VesselNames = names,
                PartnerLegIds = partners,
                EffectiveRecordingIds = effective,
                SupersedeRelations = supersedes,
                BodyFacts = RecordingOptimizer.StockBodyFacts,
                DateFormatter = ut => "UT " + ut.ToString("F1", CultureInfo.InvariantCulture),
                PartTitleResolver = StructureListBdockFixtureTests.StockTitles,
                MergePartnerResolver = (bp, viewer) => MissionStructureListBuilder.DescribeMergePartnerFromGraph(
                    graph, tree.Id, bp, viewer, missionNames),
            };
            // The tab's own rows, built exactly as MissionsWindowUI.GetVesselRows does.
            b.TabRows = MissionProfileBuilder.BuildVesselRows(b.Request, b.Structure);
            b.Profile = MissionProfileBuilder.BuildProfile(b.Request);
            return b;
        }

        internal static string MissionNameOf(List<RecordingTree> trees, string treeId)
        {
            if (treeId == BdockFirstTree) return "Kerbal X";
            if (treeId == BdockDockingTree) return "Kerbal X #2";
            return trees.FirstOrDefault(t => t.Id == treeId)?.TreeName;
        }

        internal static string Row(MissionProfileRow r)
            => new string(' ', 2 * r.Depth) + r.Element + " | " + r.Time + " | " + r.Location + " | " + r.Duration
               + " | " + r.Spent + " | " + r.Left + " | " + r.Result;

        private void Dump(MissionProfile p)
        {
            foreach (MissionProfileRow r in p.Rows) output.WriteLine(Row(r) + "   [" + r.Hover + "]");
        }

        private Built Load(string fixture, string treeId)
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadFixtureTrees(fixture, -1);
            Built b = BuildLikeTheWindow(trees, treeId, id => MissionNameOf(trees, id));
            Dump(b.Profile);
            return b;
        }

        private static string[] Shape(MissionProfile p)
            => p.Rows.Where(r => r.Kind != MissionProfileRowKind.Summary && r.Kind != MissionProfileRowKind.SummaryFact)
                .Select(r => r.Depth.ToString(CultureInfo.InvariantCulture) + " " + r.Element).ToArray();

        private static MissionProfileRow RowOf(MissionProfile p, string element, int nth = 0)
            => p.Rows.Where(r => r.Element == element).Skip(nth).First();

        private static double EstimateOf(MissionProfileRow r)
        {
            Assert.True(r.SpentIsEstimate, r.Element + " has no estimate");
            return double.Parse(r.Spent.TrimStart('~').Replace(",", ""), CultureInfo.InvariantCulture);
        }

        private const string Deg = "\u00B0";

        private static void AssertNear(double expected, double actual, double tolerance)
            => Assert.InRange(actual, expected - tolerance, expected + tolerance);

        // Appendix A.2: Kerbal X (mun-landing-recorded), a Mun landing. The appendix's hand
        // estimates are matched within 2 %; times and durations come from the recorded engine
        // events and sections.
        [Fact]
        public void AppendixA2_MunLanding_ReadsTheWorkedProfile()
        {
            MissionProfile p = Load("mun-landing-recorded", MunLandingTree).Profile;
            Assert.Equal(new[]
            {
                "0 Kerbal X (Jebediah)",
                "1 Ascent", "2 Liftoff", "2 Stage separation (2 pieces)", "2 Stage separation (2 pieces)",
                "2 Stage separation (2 pieces)", "2 Atmosphere exit",
                "1 Circularization", "2 Circularization burn",
                "1 Kerbin orbit",
                "1 Transfer to Mun", "2 Transfer burn", "2 Correction", "2 Stage separation (1 piece)", "2 Correction",
                "2 Entered Mun SOI", "2 Capture burn",
                "1 Mun orbit",
                "1 Descent", "2 Descent burn", "2 Landing burn", "2 Touchdown",
                "1 On the surface", "2 End: Landed",
            }, Shape(p));
            MissionProfileRow header = RowOf(p, "Kerbal X (Jebediah)");
            Assert.Equal("UT 34.5", header.Time);
            Assert.Equal("Kerbin, Launch Pad", header.Location);
            Assert.Equal("Landed, Mun", header.Result);
            Assert.Equal("T+0:00:04", RowOf(p, "Ascent").Time);
            Assert.Equal("2m 52s", RowOf(p, "Ascent").Duration);
            Assert.Equal("to 70 km, Ap 80 km", RowOf(p, "Ascent").Result);
            Assert.Equal("84 x 76 km, 0.1" + Deg, RowOf(p, "Circularization").Result);
            Assert.Equal("26m 12s", RowOf(p, "Kerbin orbit").Duration);
            Assert.Equal("Kerbin -> Mun", RowOf(p, "Transfer to Mun").Location);
            Assert.Equal("5h 28m", RowOf(p, "Transfer to Mun").Duration);
            Assert.Equal("about 140 km Mun orbit", RowOf(p, "Transfer to Mun").Result);
            AssertNear(736, EstimateOf(RowOf(p, "Circularization burn")), 15);
            AssertNear(856, EstimateOf(RowOf(p, "Transfer burn")), 17);
            AssertNear(120, EstimateOf(RowOf(p, "Correction")), 3);
            AssertNear(9, EstimateOf(RowOf(p, "Correction", 1)), 1);
            Assert.Equal("Ap 11,480 km, Pe 77 km", RowOf(p, "Transfer burn").Result);
            Assert.Equal("Ap 11,100 km, Pe -201 km", RowOf(p, "Correction").Result);
            Assert.Equal("-", RowOf(p, "Capture burn").Spent);
            Assert.True(RowOf(p, "Capture burn").IsInferred);
            Assert.Equal("about 140 km", RowOf(p, "Capture burn").Result);
            Assert.Equal("3m 1s", RowOf(p, "Mun orbit").Duration);
            Assert.Equal("falling (coast to the surface)", RowOf(p, "Descent burn").Result);
            Assert.Equal("51s", RowOf(p, "Landing burn").Duration);
            Assert.Equal("T+6:24:11", RowOf(p, "Landing burn").Time);
            Assert.Equal("landed 3,854 m", RowOf(p, "Touchdown").Result);
            Assert.Equal("T+6:25:08", RowOf(p, "Touchdown").Time);
            Assert.Equal("1m 54s", RowOf(p, "On the surface").Duration);
        }

        // Appendix A.1: CS Mun Science (cs-career-mun4): Mun orbit, two short EVAs, return,
        // recovery.
        [Fact]
        public void AppendixA1_MunScience_ReadsTheWorkedProfile()
        {
            MissionProfile p = Load("cs-career-mun4", MunScienceTree).Profile;
            Assert.Equal(new[]
            {
                "0 CS Mun Science (Jebediah)",
                "1 Ascent", "2 Liftoff", "2 Stage separation (2 pieces)", "2 Stage separation (1 piece)", "2 Atmosphere exit",
                "1 Circularization", "2 Circularization burn",
                "1 Kerbin orbit",
                "1 Transfer to Mun", "2 Transfer burn", "2 Correction", "2 Entered Mun SOI", "2 Capture burn",
                "1 Mun orbit", "2 EVA: Jebediah Kerman (1s), boarded", "2 Orbit change", "2 Orbit change",
                "2 EVA: Jebediah Kerman (1s), boarded",
                "1 Return to Kerbin", "2 Return burn", "2 Stage separation (1 piece)", "2 Left Mun SOI",
                "1 Reentry", "2 Atmosphere entry", "2 Parachutes deployed", "2 Touchdown", "2 Recovered",
            }, Shape(p));
            Assert.Equal("Recovered", RowOf(p, "CS Mun Science (Jebediah)").Result);
            Assert.Equal("3m 52s", RowOf(p, "Ascent").Duration);
            Assert.Equal("4m 1s", RowOf(p, "Kerbin orbit").Duration);
            Assert.Equal("83 x 77 km, 0.1" + Deg, RowOf(p, "Kerbin orbit").Result);
            Assert.Equal("Mun orbit", RowOf(p, "Transfer to Mun").Result);
            Assert.Equal("Ap 11,524 km, Pe 82 km", RowOf(p, "Transfer burn").Result);
            Assert.Equal("Mun encounter, Pe 229 km", RowOf(p, "Correction").Result);
            Assert.Equal("T+7:03:27", RowOf(p, "Entered Mun SOI").Time);
            Assert.Equal("229 x 229 km, 1.0" + Deg, RowOf(p, "Capture burn").Result);
            Assert.Equal("2h 26m", RowOf(p, "Mun orbit").Duration);
            Assert.Equal("45 x 30 km, 1.0" + Deg, RowOf(p, "Mun orbit").Result);
            Assert.Equal("229 x 30 km, 1.0" + Deg, RowOf(p, "Orbit change").Result);
            Assert.Equal("escape, Pe 38 km", RowOf(p, "Return burn").Result);
            Assert.Equal("Mun -> Kerbin", RowOf(p, "Return to Kerbin").Location);
            Assert.Equal("Ap 10,820 km, Pe 27 km", RowOf(p, "Left Mun SOI").Result);
            Assert.Equal("direct entry", RowOf(p, "Reentry").Result);
            Assert.Equal("4m 30s", RowOf(p, "Reentry").Duration);
            Assert.Equal("Kerbin, Shores", RowOf(p, "Touchdown").Location);
            Assert.Equal("T+18:22:02", RowOf(p, "Touchdown").Time);
            AssertNear(362, EstimateOf(RowOf(p, "Circularization burn")), 8);
            AssertNear(860, EstimateOf(RowOf(p, "Transfer burn")), 17);
            AssertNear(220, EstimateOf(RowOf(p, "Capture burn")), 5);
            AssertNear(64, EstimateOf(RowOf(p, "Orbit change")), 2);
            AssertNear(67, EstimateOf(RowOf(p, "Orbit change", 1)), 2);
            AssertNear(262, EstimateOf(RowOf(p, "Return burn")), 6);
            Assert.Contains(p.SummaryFacts, f => f.StartsWith("Crew of CS Mun Science: Jebediah Kerman (2 EVAs", StringComparison.Ordinal));
        }

        // Appendix A.3: Kerbal X #2 (bdock-recorded, second tree): probe separation,
        // rendezvous, docking with the first mission's Kerbal X, undock.
        [Fact]
        public void AppendixA3_Bdock_ReadsTheWorkedProfile()
        {
            MissionProfile p = Load("bdock-recorded", BdockDockingTree).Profile;
            Assert.Equal(new[]
            {
                "0 Kerbal X (Bill, Bob, Valentina)",
                "1 Ascent", "2 Liftoff", "2 Stage separation (2 pieces)", "2 Stage separation (2 pieces)",
                "2 Stage separation (2 pieces)", "2 Atmosphere exit",
                "1 Circularization", "2 Circularization burn",
                "1 Kerbin orbit", "2 Separated: Kerbal X Probe",
                "3 Kerbal X Probe", "4 Kerbin orbit", "5 End: Orbiting Kerbin",
                "1 Rendezvous with Kerbal X (mission 'Kerbal X')", "2 Phasing burn", "2 Approach burns (3)",
                "1 Docked to Kerbal X (mission 'Kerbal X')", "2 Undocked: Kerbal X (mission 'Kerbal X')",
                "1 Kerbin orbit", "2 End: Orbiting Kerbin",
            }, Shape(p));
            Assert.Equal("95 x 84 km, 0.1" + Deg, RowOf(p, "Circularization").Result);
            Assert.Equal("1h 57m", RowOf(p, "Kerbin orbit").Duration);
            Assert.Equal("2h 17m", RowOf(p, "Kerbal X Probe").Duration);
            Assert.Equal("Orbiting Kerbin", RowOf(p, "Kerbal X Probe").Result);
            Assert.Equal("T+2:02:32", RowOf(p, "Rendezvous with Kerbal X (mission 'Kerbal X')").Time);
            Assert.Equal("20s", RowOf(p, "Approach burns (3)").Duration);
            Assert.Equal("117 x 85 km, 0.1" + Deg, RowOf(p, "Phasing burn").Result);
            AssertNear(592, EstimateOf(RowOf(p, "Circularization burn")), 12);
            AssertNear(18, EstimateOf(RowOf(p, "Phasing burn")), 2);
            Assert.Equal("own ship absorbed into the partner", RowOf(p, "Docked to Kerbal X (mission 'Kerbal X')").Result);
        }

        // catches: the Profile's vessel lines drifting from the Missions tab's vessel rows on
        // the appendix fixtures (section 5's agreement, pinned by row identity).
        [Theory]
        [InlineData("mun-landing-recorded", MunLandingTree)]
        [InlineData("bdock-recorded", BdockDockingTree)]
        [InlineData("bdock-recorded", BdockFirstTree)]
        [InlineData("cs-career-mun4", MunScienceTree)]
        public void LineSet_EqualsTheTabsVesselRowSet(string fixture, string treeId)
        {
            Built b = Load(fixture, treeId);
            var rowIds = AllRows(b.TabRows).Select(r => r.OwnerHeadId).OrderBy(x => x, StringComparer.Ordinal);
            var lineIds = MissionProfileLineBuilder.All(b.Profile.Lines).Select(l => l.LineId).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(rowIds, lineIds);
        }

        // catches: the Re-Fly bridge missing: the superseded attempt (TIP) replaced by the
        // fork, a "Flown again from here" event at the fork's start, and the fork's own row
        // drawn inside the line it continues.
        [Fact]
        public void Refly_BridgesHeadToTheForkWithAReflownEvent()
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadFixtureTrees("refly-split-crewed-merged", -1);
            ConfigNode scen = ScenarioNode("refly-split-crewed-merged");
            List<RecordingSupersedeRelation> rels = LoadSupersedes(scen);
            Built b = BuildLikeTheWindow(trees, trees[0].Id, id => MissionNameOf(trees, id), null,
                EffectiveIds(trees, rels), rels);
            Dump(b.Profile);
            MissionProfileLine root = b.Profile.Lines.First(l => l.IsDrawn);
            Assert.True(root.Reflown);
            Assert.Contains(root.Pieces, r => r.RecordingId == "rec_92498045bde0451ebb7a7a1038a0d06c");
            Assert.DoesNotContain(root.Pieces, r => r.RecordingId == "2e31fb1e46d34f8b92aacb9cff38507d");
            MissionProfileRow reflown = b.Profile.Rows.Single(r => r.EventKind == ProfileEventKind.Reflown);
            Assert.True(reflown.IsReflown);
            Assert.StartsWith("Flown again from here (Re-Fly at T+0:01:28)", reflown.Hover, StringComparison.Ordinal);
            Assert.Equal(1, b.Profile.Reflown);
            Assert.Contains(b.Profile.Rows, r => r.Element == "Circularization burn");
            // Without the effective set (the raw tree, as during a session) the first attempt shows.
            Built raw = BuildLikeTheWindow(trees, trees[0].Id, id => MissionNameOf(trees, id));
            Assert.DoesNotContain(raw.Profile.Rows, r => r.EventKind == ProfileEventKind.Reflown);
        }

        // catches: the include set ignored by the Profile: an excluded interval clips the
        // phases over it, drops its events, and marks the rows (non-cascading: the child
        // line is untouched).
        [Fact]
        public void ExcludedInterval_ClipsTheLineButNotItsChild()
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadFixtureTrees("bdock-recorded", 2);
            Built first = BuildLikeTheWindow(trees, BdockDockingTree, id => MissionNameOf(trees, id));
            MissionVesselRow root = first.TabRows.First(r => !r.IsPartner);
            var mission = new Mission("m-x", BdockDockingTree, "Kerbal X #2");
            mission.ExcludedIntervalKeys.Add(root.Intervals[0].HeadLegId);
            Built b = BuildLikeTheWindow(trees, BdockDockingTree, id => MissionNameOf(trees, id), mission);
            Dump(b.Profile);
            MissionProfileLine line = b.Profile.Lines.First(l => l.LineId == root.OwnerHeadId);
            Assert.Single(line.ExcludedSpans);
            Assert.DoesNotContain(b.Profile.Rows, r => r.Element == "Ascent");
            Assert.True(b.Profile.Rows.First(r => r.Kind == MissionProfileRowKind.Line).IsClipped);
            Assert.Contains(b.Profile.Rows, r => r.Element == "Kerbal X Probe");
            Assert.True(b.Profile.ClippedByInclude > 0);
        }

        // catches: collapse keys that move across a rebuild of the same data, duplicate keys,
        // a Location cell that repeats its label, a row with no hover, or a broken tree.
        [Theory]
        [InlineData("mun-landing-recorded", MunLandingTree)]
        [InlineData("bdock-recorded", BdockDockingTree)]
        [InlineData("cs-career-mun4", MunScienceTree)]
        public void Rows_StableKeysLocationsHoversAndTree(string fixture, string treeId)
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadFixtureTrees(fixture, -1);
            MissionProfile a = BuildLikeTheWindow(trees, treeId, id => MissionNameOf(trees, id)).Profile;
            MissionProfile b = BuildLikeTheWindow(trees, treeId, id => MissionNameOf(trees, id)).Profile;
            Assert.Equal(a.Rows.Select(r => r.Key), b.Rows.Select(r => r.Key));
            Assert.Equal(a.Rows.Count, a.Rows.Select(r => r.Key).Distinct().Count());
            var keys = new HashSet<string>(a.Rows.Select(r => r.Key));
            foreach (MissionProfileRow r in a.Rows)
            {
                Assert.False(string.IsNullOrEmpty(r.Hover), r.Element + " has no hover");
                if (!string.IsNullOrEmpty(r.Location)) Assert.NotEqual(r.Element, r.Location);
                if (r.ParentKey != null) Assert.Contains(r.ParentKey, keys);
            }
            Assert.Equal(MissionProfileRowKind.Summary, a.Rows[0].Kind);
            Assert.Equal("Parsek - Profile: " + MissionNameOf(trees, treeId), a.Title);
            Assert.All(a.Rows, r => Assert.DoesNotContain("\u2014", r.Element + r.Hover));
        }

        // ------------------------------------------------------------------ sweep helpers

        internal static string FixtureRoot()
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "harness", "fixtures", "saves"));

        internal static ConfigNode ScenarioNode(string fixture)
        {
            ConfigNode root = ConfigNode.Load(System.IO.Path.Combine(FixtureRoot(), fixture, "persistent.sfs"));
            ConfigNode game = root?.GetNode("GAME") ?? root;
            if (game == null) return null;
            foreach (ConfigNode scen in game.GetNodes("SCENARIO"))
                if (scen.GetValue("name") == "ParsekScenario") return scen;
            return null;
        }

        internal static List<Mission> LoadMissions(ConfigNode scenario)
        {
            var list = new List<Mission>();
            if (scenario == null) return list;
            foreach (ConfigNode m in scenario.GetNodes("MISSION")) list.Add(Mission.Load(m));
            return list;
        }

        internal static List<RecordingSupersedeRelation> LoadSupersedes(ConfigNode scenario)
        {
            var list = new List<RecordingSupersedeRelation>();
            ConfigNode node = scenario?.GetNode("RECORDING_SUPERSEDES");
            if (node == null) return list;
            foreach (ConfigNode e in node.GetNodes("ENTRY"))
                list.Add(new RecordingSupersedeRelation
                {
                    RelationId = e.GetValue("relationId"),
                    OldRecordingId = e.GetValue("oldRecordingId"),
                    NewRecordingId = e.GetValue("newRecordingId"),
                });
            return list;
        }

        // The effective set the UI would pass: every recording minus the superseded ones.
        internal static HashSet<string> EffectiveIds(List<RecordingTree> trees, List<RecordingSupersedeRelation> rels)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (RecordingTree t in trees) foreach (string id in t.Recordings.Keys) ids.Add(id);
            foreach (RecordingSupersedeRelation r in rels) ids.Remove(r.OldRecordingId);
            return ids;
        }

        internal static IEnumerable<MissionVesselRow> AllRows(IEnumerable<MissionVesselRow> rows)
        {
            foreach (MissionVesselRow r in rows)
            {
                yield return r;
                foreach (MissionVesselRow c in AllRows(r.Children)) yield return c;
            }
        }

        internal static List<string> FixturesWithTrees()
        {
            var list = new List<string>();
            foreach (string dir in System.IO.Directory.GetDirectories(FixtureRoot()))
            {
                string sfs = System.IO.Path.Combine(dir, "persistent.sfs");
                if (System.IO.File.Exists(sfs) && System.IO.File.ReadAllText(sfs).Contains("RECORDING_TREE"))
                    list.Add(System.IO.Path.GetFileName(dir));
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // catches: a crash, a broken tiling, or a line set that drifted from the Missions tab's
        // vessel rows, on any committed fixture (section 11.2 sweep; counts report-only).
        [Fact]
        public void EveryCommittedFixture_BuildsWithoutThrow_TilesAndMatchesTheTabRows()
        {
            int fixtures = 0, missions = 0, unclassified = 0, inferred = 0, burns = 0, estimated = 0;
            var problems = new List<string>();
            foreach (string fixture in FixturesWithTrees())
            {
                // A save can hold the same tree twice (committed and the active copy): one each.
                List<RecordingTree> trees = StructureListBdockFixtureTests.LoadFixtureTrees(fixture, -1)
                    .GroupBy(t => t.Id).Select(g => g.First()).ToList();
                if (trees.Count == 0) continue;
                fixtures++;
                ConfigNode scen = ScenarioNode(fixture);
                List<Mission> ms = LoadMissions(scen);
                List<RecordingSupersedeRelation> rels = LoadSupersedes(scen);
                HashSet<string> effective = rels.Count > 0 ? EffectiveIds(trees, rels) : null;
                foreach (RecordingTree tree in trees)
                {
                    Mission mission = ms.FirstOrDefault(m => m.TreeId == tree.Id)
                        ?? new Mission("m-" + tree.Id, tree.Id, tree.TreeName);
                    Built b;
                    try
                    {
                        b = BuildLikeTheWindow(trees, tree.Id, id => trees.FirstOrDefault(t => t.Id == id)?.TreeName,
                            mission, effective, rels);
                    }
                    catch (Exception ex)
                    {
                        problems.Add(fixture + "/" + tree.Id + ": threw " + ex.GetType().Name + " " + ex.Message);
                        continue;
                    }
                    missions++;
                    var rowIds = new HashSet<string>(AllRows(b.TabRows).Select(r => r.OwnerHeadId));
                    var lineIds = new HashSet<string>(MissionProfileLineBuilder.All(b.Profile.Lines).Select(l => l.LineId));
                    if (!rowIds.SetEquals(lineIds))
                        problems.Add(fixture + "/" + tree.Id + ": line set differs from the tab rows");
                    foreach (MissionProfileLine l in MissionProfileLineBuilder.All(b.Profile.Lines))
                    {
                        if (l.Plan == null) continue;
                        if (l.ExcludedSpans.Count == 0 && !MissionPhaseAnalyzer.PhasesTile(l.Plan, out string why))
                            problems.Add(fixture + "/" + l.LineId + ": " + why);
                    }
                    foreach (MissionProfileLine l in MissionProfileLineBuilder.All(b.Profile.Lines))
                        if (l.Plan != null)
                            foreach (ProfilePhase ph in l.Plan.Phases)
                                if (ph.Kind == ProfilePhaseKind.Unclassified)
                                    output.WriteLine("unclassified " + fixture + "/" + l.LineId + " '" + l.Name + "' "
                                        + ph.StartUT.ToString("F1", CultureInfo.InvariantCulture) + ": " + ph.UnclassifiedReason);
                    unclassified += b.Profile.Unclassified;
                    inferred += b.Profile.Inferred;
                    burns += b.Profile.Burns;
                    estimated += b.Profile.DvEstimated;
                }
            }
            output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "fixtures={0} missions={1} burns={2} dvEstimated={3} inferred={4} unclassified={5}",
                fixtures, missions, burns, estimated, inferred, unclassified));
            foreach (string p in problems) output.WriteLine(p);
            Assert.Empty(problems);
        }
    }
}
