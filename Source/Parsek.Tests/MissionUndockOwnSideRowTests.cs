using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// MISSION-VESSEL-ROW-UNDOCK-FOLLOWS-PARTNER: KSP keeps one identity for a docked pair, and
    /// when that identity is another mission's vessel the composition run goes on along the
    /// partner's half after the undock (the recorder lists the half that kept the identity
    /// first). The Missions vessel rows follow the physical vessel instead: the ship's row
    /// continues into its own post-undock leg and the partner's half is a partner row beside
    /// it. The fixture cells are the three ships the defect was filed on (GUI-3's Duna Supply 1
    /// and Kerbal X #4 on <c>interbody-route-recorded</c>, GUI-4's docking mission on
    /// <c>bdock-recorded</c>); the key cells prove the fix moved no stored interval key; the
    /// synthetic cells are the mirror shapes.
    /// </summary>
    [Collection("Sequential")]
    public class MissionUndockOwnSideRowTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private readonly List<string> logLines = new List<string>();

        private const string DunaTreeId = "3daf0cff159d413794c626137cd81002";
        private const string DunaHead = "d23e453bc982482b850ce717ba83bffd";
        private const string DunaOwnAfterUndock = "1331a21bddfb49418be6ebec99dabf98";
        private const string DunaDepotAfterUndock = "fca32e43";   // prefix: the partner's half
        private const string MunTreeId = "02382fcdcb1a465388529350c0879cd6";
        private const string MunHead = "c549ef6e4ed14f0fb93236a72f435cb4";
        private const string MunOwnAfterUndock = "b9f08d0f269346ee84162dd763e462aa";
        private const string MunDepotAfterUndock = "7e0d79b5";
        private const string BdockTreeId = "8c677bba0ec04e558c6ebec4ad8c3c9f";
        private const string BdockHead = "5157d6555bd3499592c46d8508dbedf4";
        private const string BdockOwnAfterUndock = "4af6cfd725d646ccbac9ef2f7749667e";
        private const string BdockPartnerAfterUndock = "37d0dc07";

        private static readonly string A = MissionPresentation.SummarySpanArrow;

        public MissionUndockOwnSideRowTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionStructureBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionStructureBuilder.SuppressLogging = false;
            MissionCompositionBuilder.SuppressLogging = false;
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static string MissionOf(string treeId)
        {
            switch (treeId)
            {
                case "400394def77b42d39596a86e325d8d75": return "Kerbal X #3";
                case MunTreeId: return "Kerbal X #4";
                case "54c5efe5ab1942fda6f53ffdfe8d4ce5": return "Kerbal X #5";
                case DunaTreeId: return "Duna Supply 1";
                case "788554a928324f148dd313dd26322638": return "Kerbal X";
                case BdockTreeId: return "Kerbal X #2";
                default: return null;
            }
        }

        private static List<RecordingTree> InterbodyTrees()
            => StructureListBdockFixtureTests.LoadFixtureTrees("interbody-route-recorded", 4);

        private static List<RecordingTree> TreesOf(string treeId)
            => treeId == BdockTreeId ? StructureListBdockFixtureTests.LoadBdockTrees() : InterbodyTrees();

        // One tree as the Missions tab reads it (MissionsWindowUI.GetVesselRows).
        private sealed class Built
        {
            public MissionStructure Structure;
            public List<MissionCompositionNode> Roots;
            public Dictionary<string, string> Names;
            public HashSet<string> Partners;
            public MissionUndockSides Sides;
            public List<MissionVesselRow> Rows;
            public List<MissionVesselRow> RowsWithoutSides;
        }

        private static Built BuildLikeTheWindow(List<RecordingTree> trees, string treeId)
        {
            RecordingTree tree = trees.Single(t => t.Id == treeId);
            var b = new Built { Structure = MissionStructureBuilder.Build(tree) };
            b.Partners = new HashSet<string>(StringComparer.Ordinal);
            b.Names = MissionVesselNaming.Build(tree, b.Structure,
                MissionVesselNaming.LaunchIndex.Build(trees), MissionOf,
                out MissionVesselNaming.Tally _, null, b.Partners);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(b.Structure);
            b.Roots = MissionCompositionBuilder.Build(b.Structure);
            b.Sides = MissionVesselRowBuilder.ResolveUndockSides(b.Structure, b.Partners);
            Func<string, double, string> dock = (head, ut) =>
                MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                    b.Structure, view, head, ut, b.Names);
            Func<string, double, string> terminal = (head, ut) =>
                MissionPresentation.ResolveTerminalDockPartnerVesselName(b.Structure, head, ut, b.Names);
            b.Rows = MissionVesselRowBuilder.Build(b.Roots, dock, b.Names, b.Partners, terminal, b.Sides);
            b.RowsWithoutSides = MissionVesselRowBuilder.Build(b.Roots, dock, b.Names, b.Partners, terminal);
            return b;
        }

        private static IEnumerable<MissionVesselRow> AllRows(IEnumerable<MissionVesselRow> rows)
        {
            foreach (MissionVesselRow r in rows)
            {
                yield return r;
                foreach (MissionVesselRow c in AllRows(r.Children))
                    yield return c;
            }
        }

        private void Dump(IEnumerable<MissionVesselRow> rows)
        {
            foreach (MissionVesselRow r in AllRows(rows))
                output.WriteLine((r.IsPartner ? "[partner] " : "") + r.VesselName + "   " + r.EventPhrase
                    + "   keys=" + string.Join(",", MissionVesselRowBuilder.IntervalKeys(r)));
        }

        // ------------------------------------------------------------ the filed examples

        // catches: Duna Supply 1 reading "... Undocked (Duna Supply 1) -> Orbiting" with its
        // last interval on the depot's half, and its real post-undock leg a separate child row.
        [Fact]
        public void Duna_ShipRowFollowsItsOwnPostUndockLeg()
        {
            Built b = BuildLikeTheWindow(InterbodyTrees(), DunaTreeId);
            Dump(b.Rows);

            MissionVesselRow ship = b.Rows.Single(r => r.OwnerHeadId == DunaHead);
            Assert.False(ship.IsPartner);
            Assert.Equal("Launch" + A + "Decoupled (Duna Supply 1 Probe)" + A
                + "Docked (Depot Station Duna I (mission 'Kerbal X #5'))" + A
                + "Undocked (Depot Station Duna I (mission 'Kerbal X #5'))" + A + "Orbiting",
                ship.EventPhrase);
            Assert.Equal(new[]
            {
                DunaHead, DunaHead + "/seg1", DunaHead + "/seg2", DunaHead + "/seg2@dock1",
                DunaOwnAfterUndock,
            }, MissionVesselRowBuilder.IntervalKeys(ship).ToArray());
            Assert.InRange(ship.EndUT, 72353312.0, 72353313.0);   // the ship's own leg ends here
            Assert.Equal("after undock: Depot Station Duna I (mission 'Kerbal X #5') left - (pod x1, crew x1)",
                MissionVesselRowBuilder.IntervalDetailLabel(ship, 4, b.Names));
            Assert.DoesNotContain(AllRows(b.Rows), r => r.OwnerHeadId == DunaOwnAfterUndock);

            MissionVesselRow depotHalf = b.Rows.Single(r => r.OwnerHeadId.StartsWith(DunaDepotAfterUndock));
            Assert.True(depotHalf.IsPartner);
            Assert.Equal("Depot Station Duna I (mission 'Kerbal X #5')", depotHalf.VesselName);
            Assert.Equal("Undocked (Duna Supply 1)" + A + "Orbiting", depotHalf.EventPhrase);
            Assert.Equal(new[] { DunaHead + "/seg3" },
                MissionVesselRowBuilder.IntervalKeys(depotHalf).ToArray());
            Assert.Equal("Depot Station Duna I (mission 'Kerbal X #5') (pod x1, crew x1)",
                MissionVesselRowBuilder.IntervalDetailLabel(depotHalf, 0, b.Names));
        }

        // catches: Kerbal X #4's interval detail "after undock: Deliverer Mun 1 left" - its own
        // post-undock self named as the piece that left, on the depot's half.
        [Fact]
        public void Mun_ShipRowFollowsItsOwnPostUndockLeg()
        {
            Built b = BuildLikeTheWindow(InterbodyTrees(), MunTreeId);
            Dump(b.Rows);

            MissionVesselRow ship = b.Rows.Single(r => r.OwnerHeadId == MunHead);
            Assert.Equal("Launch" + A + "Decoupled (Kerbal X Probe)" + A
                + "Docked (Kerbal X (mission 'Kerbal X #3'))" + A
                + "Undocked (Depot (mission 'Kerbal X #3'))" + A + "Orbiting", ship.EventPhrase);
            Assert.Equal(MunOwnAfterUndock, ship.Intervals.Last().HeadLegId);
            Assert.Equal("after undock: Depot (mission 'Kerbal X #3') left - (pod x1, crew x1)",
                MissionVesselRowBuilder.IntervalDetailLabel(ship, ship.Intervals.Count - 1, b.Names));
            Assert.All(Enumerable.Range(0, ship.Intervals.Count),
                j => Assert.DoesNotContain("Deliverer Mun 1 left",
                    MissionVesselRowBuilder.IntervalDetailLabel(ship, j, b.Names)));
            Assert.DoesNotContain(AllRows(b.Rows), r => r.OwnerHeadId == MunOwnAfterUndock);

            MissionVesselRow depotHalf = b.Rows.Single(r => r.OwnerHeadId.StartsWith(MunDepotAfterUndock));
            Assert.True(depotHalf.IsPartner);
            Assert.Equal("Undocked (Kerbal X)" + A + "Orbiting", depotHalf.EventPhrase);
            Assert.Equal(new[] { MunHead + "/seg3" },
                MissionVesselRowBuilder.IntervalKeys(depotHalf).ToArray());
        }

        // catches: GUI-4's docking mission reading "Undocked (Kerbal X)" on the partner's half.
        [Fact]
        public void Bdock_ShipRowFollowsItsOwnPostUndockLeg()
        {
            Built b = BuildLikeTheWindow(StructureListBdockFixtureTests.LoadBdockTrees(), BdockTreeId);
            Dump(b.Rows);

            MissionVesselRow ship = b.Rows.Single(r => r.OwnerHeadId == BdockHead);
            Assert.EndsWith("Undocked (Kerbal X (mission 'Kerbal X'))" + A + "Orbiting", ship.EventPhrase);
            Assert.Equal(BdockOwnAfterUndock, ship.Intervals.Last().HeadLegId);
            Assert.Equal(new[] { "Kerbal X Probe" }, ship.Children.Select(c => c.VesselName).ToArray());

            MissionVesselRow half = b.Rows.Single(r => r.OwnerHeadId.StartsWith(BdockPartnerAfterUndock));
            Assert.True(half.IsPartner);
            Assert.Equal("Kerbal X (mission 'Kerbal X')", half.VesselName);
            Assert.Equal("Undocked (Kerbal X)" + A + "Orbiting", half.EventPhrase);
            Assert.Equal(new[] { BdockHead + "/seg2" }, MissionVesselRowBuilder.IntervalKeys(half).ToArray());
        }

        // catches: the summary counting the ship's own post-undock leg as a second vessel, and
        // reading the outcome off the partner's half.
        [Theory]
        [InlineData(DunaTreeId, 2)]
        [InlineData(MunTreeId, 2)]
        [InlineData(BdockTreeId, 2)]
        public void SummaryFacts_CountTheShipOnce(string treeId, int vessels)
        {
            Built b = BuildLikeTheWindow(TreesOf(treeId), treeId);
            MissionPresentation.MissionSummaryFacts facts = MissionPresentation.ComputeSummaryFacts(
                b.Structure, MissionThroughLineBuilder.Build(b.Structure), b.Roots, b.Partners);
            Assert.Equal(vessels, facts.VesselCount);
            Assert.Equal(vessels, AllRows(b.Rows).Count(r => !r.IsPartner && !r.IsPerson));
            Assert.Equal(b.Rows[0].EndEvent, facts.TerminalWord);
        }

        // catches: the own-side swap not being logged (KSP.log is the debugging surface).
        [Fact]
        public void Build_LogsTheOwnSideUndocks()
        {
            BuildLikeTheWindow(InterbodyTrees(), DunaTreeId);
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("VesselRow: ownSideUndocks=1 skipped=0")
                && l.Contains("first row head=" + DunaHead));
        }

        // ------------------------------------------- the Interact cell (Fly / Seal)

        // catches (review): the ship's row resolving Fly / Seal off its launch head, which
        // ended at the dock, so the ship's own post-undock flight - a Re-Fly slot of the
        // Undock - lost its button on the collapsed row (Basic has no interval detail). Repro:
        // the Duna ship's own leg crashed and the undock carries a RewindPoint.
        [Fact]
        public void Duna_CrashedOwnLegAfterUndock_ShipRowCarriesItsFlySeal()
        {
            List<RecordingTree> trees = InterbodyTrees();
            RecordingTree tree = trees.Single(t => t.Id == DunaTreeId);
            Recording own = tree.Recordings[DunaOwnAfterUndock];
            own.TerminalStateValue = TerminalState.Destroyed;
            own.MergeState = MergeState.CommittedProvisional;
            BranchPoint undock = tree.BranchPoints.Single(bp => bp.Type == BranchPointType.Undock);
            Assert.Equal(undock.Id, own.ParentBranchPointId);
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording r in tree.Recordings.Values)
            {
                r.TreeId = tree.Id;
                RecordingStore.AddRecordingWithTreeForTesting(r);
            }
            var slots = new List<ChildSlot>();
            for (int i = 0; i < undock.ChildRecordingIds.Count; i++)
                slots.Add(new ChildSlot
                {
                    SlotIndex = i,
                    OriginChildRecordingId = undock.ChildRecordingIds[i],
                    Controllable = true,
                });
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>
                {
                    new RewindPoint
                    {
                        RewindPointId = "rp_undock",
                        BranchPointId = undock.Id,
                        FocusSlotIndex = 0,
                        SessionProvisional = false,
                        ChildSlots = slots,
                    },
                },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            Assert.True(EffectiveState.IsUnfinishedFlight(own));

            Built b = BuildLikeTheWindow(trees, DunaTreeId);
            MissionVesselRow ship = b.Rows.Single(r => r.OwnerHeadId == DunaHead);
            Assert.Equal(DunaOwnAfterUndock, ship.InteractHeadId);
            Assert.Equal(RecordingsTableUI.ReFlyColumnAction.FlySeal,
                RecordingsTableUI.ResolveReFlyColumnAction(tree.Recordings[ship.InteractHeadId]));
            // The launch head the row read before: no button, which was the defect.
            Assert.Equal(RecordingsTableUI.ReFlyColumnAction.None,
                RecordingsTableUI.ResolveReFlyColumnAction(tree.Recordings[DunaHead]));

            // The partner's half keeps its own slot on its own row.
            MissionVesselRow half = b.Rows.Single(r => r.OwnerHeadId.StartsWith(DunaDepotAfterUndock));
            Assert.Equal(half.OwnerHeadId, half.InteractHeadId);
        }

        // catches (mirror): the Interact head moving on a row that crosses no own-side undock
        // (the passive side, an ordinary child row).
        [Fact]
        public void UnstitchedRows_KeepTheirHeadsInteractCell()
        {
            List<MissionVesselRow> passive = Rows(DockUndockTree("Ship", stationKeepsIdentity: false),
                new[] { "station", "stationAfter" }, out _, out _);
            Assert.All(AllRows(passive), r => Assert.Equal(r.OwnerHeadId, r.InteractHeadId));

            Built b = BuildLikeTheWindow(InterbodyTrees(), DunaTreeId);
            Assert.All(AllRows(b.Rows).Where(r => r.OwnerHeadId != DunaHead),
                r => Assert.Equal(r.OwnerHeadId, r.InteractHeadId));
        }

        // ------------------------------------------------- stored keys: none moves

        // catches: the fix moving a stored interval key. The rows only regroup the
        // composition's intervals: every tree's rows carry exactly the composition's
        // selectable keys, each once, with and without the fix, so a key names the same
        // interval (recording id + UT span) it named before. No selection migration exists
        // because none is needed - this cell is the proof, run over every fixture tree.
        [Theory]
        [InlineData("interbody-route-recorded")]
        [InlineData("bdock-recorded")]
        public void EveryFixtureTree_RowsCarryTheSameKeysOnce(string fixture)
        {
            List<RecordingTree> trees = fixture == "bdock-recorded"
                ? StructureListBdockFixtureTests.LoadBdockTrees() : InterbodyTrees();
            int moved = 0;
            foreach (RecordingTree tree in trees)
            {
                Built b = BuildLikeTheWindow(trees, tree.Id);
                var compKeys = new List<string>();
                foreach (MissionCompositionNode root in b.Roots) CollectKeys(root, compKeys);
                compKeys.Sort(StringComparer.Ordinal);

                List<string> after = RowKeys(b.Rows);
                List<string> before = RowKeys(b.RowsWithoutSides);
                Assert.Equal(compKeys, after);
                Assert.Equal(compKeys, before);

                Dictionary<string, string> ownerBefore = KeyToRow(b.RowsWithoutSides);
                Dictionary<string, string> ownerAfter = KeyToRow(b.Rows);
                foreach (string key in compKeys)
                    if (ownerBefore[key] != ownerAfter[key])
                    {
                        moved++;
                        output.WriteLine($"{tree.Id}: key {key} row {ownerBefore[key]} -> {ownerAfter[key]}");
                    }
            }
            // Only the row a key is DRAWN in moves (2 keys per own-side undock: the ship's
            // own post-undock key joins the ship row, the partner's half leaves it).
            Assert.Equal(fixture == "bdock-recorded" ? 2 : 4, moved);
        }

        // catches: an exclusion stored on an affected interval re-targeting another interval
        // after the fix, or a save written before it needing a selection-schema bump. Every
        // key on both sides of each own-side undock round-trips the codec unchanged, names the
        // same UT span, stays valid through ReconcileSelections, and the generation is not
        // bumped (no key moved, so no remap runs).
        [Theory]
        [InlineData(DunaTreeId, DunaHead + "/seg3", 72353267.0, 72353277.0, DunaOwnAfterUndock, 72353267.0, 72353313.0)]
        [InlineData(MunTreeId, MunHead + "/seg3", 6738307.0, 6738317.0, MunOwnAfterUndock, 6738307.0, 6738332.0)]
        [InlineData(BdockTreeId, BdockHead + "/seg2", 8950.0, 8952.0, BdockOwnAfterUndock, 8950.0, 8952.0)]
        public void AffectedKeys_RoundTripToTheSameIntervals_WithoutABump(string treeId,
            string partnerHalfKey, double halfStart, double halfEnd,
            string ownKey, double ownStart, double ownEnd)
        {
            List<RecordingTree> trees = TreesOf(treeId);
            Built b = BuildLikeTheWindow(trees, treeId);
            Dictionary<string, MissionCompositionNode> nodes = NodesByKey(b.Roots);

            // The physical interval each key names: the partner's half and the ship's own leg.
            AssertSpan(nodes[partnerHalfKey], halfStart, halfEnd);
            AssertSpan(nodes[ownKey], ownStart, ownEnd);
            Assert.True(nodes[partnerHalfKey].EndUT < nodes[ownKey].EndUT
                || treeId == BdockTreeId);

            // Exclusions on every key, both sides of the undock included, on the tree's
            // default mission; the save codec round-trips them and the generation untouched.
            MissionStore.EnsureDefaultsForTrees(trees);
            Mission m = MissionStore.Missions.Single(x => x.TreeId == treeId);
            foreach (string key in nodes.Keys) m.ExcludedIntervalKeys.Add(key);
            Assert.Equal(1, Mission.CurrentSelectionSchemaGeneration);

            var node = new ConfigNode("MISSION");
            m.Save(node);
            Mission roundTripped = Mission.Load(node);
            Assert.Equal(Mission.CurrentSelectionSchemaGeneration, roundTripped.SelectionSchemaGeneration);
            Assert.Equal(m.ExcludedIntervalKeys.OrderBy(k => k, StringComparer.Ordinal),
                roundTripped.ExcludedIntervalKeys.OrderBy(k => k, StringComparer.Ordinal));

            Mission loaded = m;
            Assert.Equal(0, MissionStore.ReconcileSelections(trees));
            Assert.Equal(nodes.Count, loaded.ExcludedIntervalKeys.Count);
            Assert.Equal(Mission.CurrentSelectionSchemaGeneration, loaded.SelectionSchemaGeneration);

            // Each row of the fixed model classifies off the same keys: every row fully
            // excluded, and including the ship's row writes back exactly its own keys.
            foreach (MissionVesselRow r in AllRows(b.Rows))
                Assert.Equal(MissionVesselInclusion.None,
                    MissionVesselRowBuilder.ClassifyInclusion(r, loaded.ExcludedIntervalKeys));
            MissionVesselRow ship = b.Rows[0];
            Assert.Contains(ownKey, MissionVesselRowBuilder.IntervalKeys(ship));
            Assert.DoesNotContain(partnerHalfKey, MissionVesselRowBuilder.IntervalKeys(ship));
            int changed = MissionVesselRowBuilder.ApplyVesselInclusion(ship, true, loaded.ExcludedIntervalKeys);
            Assert.Equal(ship.Intervals.Count, changed);
            Assert.Contains(partnerHalfKey, loaded.ExcludedIntervalKeys);
            Assert.DoesNotContain(ownKey, loaded.ExcludedIntervalKeys);
        }

        // catches: the render windows (what the loop unit and playback read) depending on
        // the rows: they are a function of the composition and the excluded keys only.
        [Fact]
        public void RenderWindows_DoNotReadTheRows()
        {
            Built b = BuildLikeTheWindow(InterbodyTrees(), DunaTreeId);
            var excluded = new HashSet<string>(StringComparer.Ordinal)
            {
                DunaHead + "/seg3", DunaOwnAfterUndock,
            };
            Dictionary<string, MissionIntervalSelection.RenderWindow> windows =
                MissionIntervalSelection.ComputeRenderWindows(b.Roots, excluded);
            // The ship's run (composition owner) ends at the undock once the partner's half
            // is excluded; the own post-undock leg is its own run, dropped.
            Assert.InRange(windows[DunaHead].EndUT, 72353267.0, 72353268.0);
            Assert.False(windows.ContainsKey(DunaOwnAfterUndock));
        }

        // ------------------------------------------------------------- mirror shapes

        private static Recording Rec(string id, string vessel, double start, double end, uint pid,
            TerminalState? terminal = null)
            => new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                VesselPersistentId = pid,
                TerminalStateValue = terminal,
                Controllers = new List<ControllerInfo> { new ControllerInfo { type = "ProbeCore" } },
            };

        private static BranchPoint BP(string id, BranchPointType type, double ut,
            string[] parents, string[] children)
            => new BranchPoint
            {
                Id = id, Type = type, UT = ut,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };

        // This mission's Ship docks with another mission's Station; the pair keeps
        // `pairIdentity`'s name, and the undock lists `firstChild` first (the half that kept it).
        private static RecordingTree DockUndockTree(string pairIdentity, bool stationKeepsIdentity)
        {
            var recs = new[]
            {
                Rec("ship", "Ship", 0, 100, 1),
                Rec("station", "Station", 0, 100, 2),
                Rec("combined", pairIdentity, 100, 200, stationKeepsIdentity ? 2u : 1u),
                Rec("stationAfter", "Station", 200, 300, 2, TerminalState.Orbiting),
                Rec("shipAfter", "Ship", 200, 350, 3, TerminalState.Landed),
            };
            var tree = new RecordingTree { Id = "t", RootRecordingId = "ship" };
            foreach (Recording r in recs) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.Add(BP("dock", BranchPointType.Dock, 100,
                new[] { "ship", "station" }, new[] { "combined" }));
            tree.BranchPoints.Add(BP("undock", BranchPointType.Undock, 200, new[] { "combined" },
                stationKeepsIdentity ? new[] { "stationAfter", "shipAfter" }
                                     : new[] { "shipAfter", "stationAfter" }));
            return tree;
        }

        private static List<MissionVesselRow> Rows(RecordingTree tree, string[] partners,
            out MissionUndockSides sides, out List<MissionCompositionNode> roots)
        {
            MissionStructure s = MissionStructureBuilder.Build(tree);
            HashSet<string> set = partners == null ? null : new HashSet<string>(partners, StringComparer.Ordinal);
            sides = MissionVesselRowBuilder.ResolveUndockSides(s, set);
            roots = MissionCompositionBuilder.Build(s);
            return MissionVesselRowBuilder.Build(roots, null, null, set, null, sides);
        }

        private static string Shape(IEnumerable<MissionVesselRow> rows)
            => string.Join(" | ", AllRows(rows).Select(r => (r.IsPartner ? "P:" : "") + r.OwnerHeadId
                + "[" + string.Join(",", MissionVesselRowBuilder.IntervalKeys(r)) + "] " + r.EventPhrase));

        // catches (the defect, synthetic): the active side - the pair kept the partner's
        // identity, so the run goes on along the station's half.
        [Fact]
        public void ActiveSide_ShipRowContinuesIntoItsOwnLeg_StationHalfIsAPartnerRow()
        {
            RecordingTree tree = DockUndockTree("Station", stationKeepsIdentity: true);
            List<MissionVesselRow> rows = Rows(tree, new[] { "station", "combined", "stationAfter" },
                out MissionUndockSides sides, out _);
            output.WriteLine(Shape(rows));
            Assert.Equal("stationAfter", sides.OwnSideByOwnChild["shipAfter"].PartnerChildId);

            MissionVesselRow ship = rows.Single(r => r.OwnerHeadId == "ship");
            Assert.Equal("shipAfter", ship.Intervals.Last().HeadLegId);
            Assert.Equal("Landed", ship.EndEvent);
            Assert.Equal(350, ship.EndUT);
            Assert.Contains("Undocked (Station)", ship.EventPhrase);
            MissionVesselRow half = rows.Single(r => r.OwnerHeadId == "stationAfter");
            Assert.True(half.IsPartner);
            Assert.Equal("Undocked (Ship)" + A + "Orbiting", half.EventPhrase);
            Assert.DoesNotContain(AllRows(rows), r => r.OwnerHeadId == "shipAfter");
        }

        // catches (mirror): the passive side - this mission's ship kept the pair's identity
        // and the partner left. The ship's row is already right; the partner's half is the
        // partner's (beside the ship, not a piece of it), and no key moves.
        [Fact]
        public void PassiveSide_ShipRowUnchanged_PartnerHalfBesideIt()
        {
            RecordingTree tree = DockUndockTree("Ship", stationKeepsIdentity: false);
            string[] partners = { "station", "stationAfter" };
            List<MissionVesselRow> rows = Rows(tree, partners, out MissionUndockSides sides,
                out List<MissionCompositionNode> roots);
            List<MissionVesselRow> before = MissionVesselRowBuilder.Build(roots, null, null,
                new HashSet<string>(partners));
            output.WriteLine(Shape(before));
            output.WriteLine(Shape(rows));
            Assert.Empty(sides.OwnSideByOwnChild);
            Assert.Contains("stationAfter", sides.PartnerHalves);

            MissionVesselRow ship = rows.Single(r => r.OwnerHeadId == "ship");
            Assert.Equal(MissionVesselRowBuilder.IntervalKeys(before.Single(r => r.OwnerHeadId == "ship")),
                MissionVesselRowBuilder.IntervalKeys(ship));
            Assert.Equal(350, ship.EndUT);           // the ship's own leg, as before the fix
            Assert.Equal("Landed", ship.EndEvent);
            Assert.DoesNotContain(ship.Children, c => c.OwnerHeadId == "stationAfter");
            Assert.True(rows.Single(r => r.OwnerHeadId == "stationAfter").IsPartner);
            Assert.Equal(RowKeys(before), RowKeys(rows));

            MissionStructure s = MissionStructureBuilder.Build(tree);
            MissionPresentation.MissionSummaryFacts facts = MissionPresentation.ComputeSummaryFacts(
                s, MissionThroughLineBuilder.Build(s), roots, new HashSet<string>(partners));
            Assert.Equal(AllRows(rows).Count(r => !r.IsPartner), facts.VesselCount);
        }

        // catches (mirror): both sides this mission's own, and no partner set at all - the
        // structural reading stays, row for row.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void NoPartnerSide_RowsAreTheStructuralRows(bool emptySet)
        {
            RecordingTree tree = DockUndockTree("Station", stationKeepsIdentity: true);
            List<MissionVesselRow> rows = Rows(tree, emptySet ? new string[0] : null,
                out MissionUndockSides sides, out List<MissionCompositionNode> roots);
            Assert.True(sides.IsEmpty);
            Assert.Equal(Shape(MissionVesselRowBuilder.Build(roots)), Shape(rows));
            Assert.Contains(AllRows(rows), r => r.OwnerHeadId == "shipAfter");
        }

        // catches: an undock where every recorded side is another mission's vessel inventing
        // an own side, and an undock whose run already continues on the own child.
        [Fact]
        public void AllPartnerUndock_AndOwnContinuation_DoNotSwap()
        {
            RecordingTree tree = DockUndockTree("Station", stationKeepsIdentity: true);
            Rows(tree, new[] { "station", "combined", "stationAfter", "shipAfter" },
                out MissionUndockSides allPartner, out _);
            Assert.Empty(allPartner.OwnSideByOwnChild);

            RecordingTree ownFirst = DockUndockTree("Station", stationKeepsIdentity: false);
            Rows(ownFirst, new[] { "station", "combined", "stationAfter" },
                out MissionUndockSides ownContinues, out _);
            Assert.Empty(ownContinues.OwnSideByOwnChild);
        }

        // catches (review): the vessel count leaving out an own post-undock leg whose undock
        // the rows do NOT follow (the docked pair sits on a run headed by another mission's
        // leg), so the summary disagreed with the rows drawn.
        [Fact]
        public void UndockOnAPartnerHeadedRun_IsNotFollowed_AndTheCountAgreesWithTheRows()
        {
            RecordingTree tree = DockUndockTree("Station", stationKeepsIdentity: true);
            // The partner's run claims the docked pair: its root is walked before the ship's
            // (the structure lists roots in recording order).
            List<Recording> all = tree.Recordings.Values.OrderBy(r => r.RecordingId == "station" ? 0 : 1).ToList();
            tree.Recordings.Clear();
            foreach (Recording r in all) tree.Recordings[r.RecordingId] = r;
            tree.Recordings["station"].ExplicitStartUT = -10;
            tree.BranchPoints[0].ParentRecordingIds.Reverse();
            tree.RootRecordingId = "station";
            string[] partners = { "station", "combined", "stationAfter" };
            MissionStructure s = MissionStructureBuilder.Build(tree);
            List<MissionVesselRow> rows = Rows(tree, partners, out MissionUndockSides sides,
                out List<MissionCompositionNode> roots);
            output.WriteLine(Shape(rows));
            Assert.Contains("shipAfter", sides.OwnSideByOwnChild.Keys);
            MissionVesselRow owner = AllRows(rows).Single(
                r => r.Intervals.Any(iv => iv.EndEvent == "Undocked"));
            Assert.Empty(owner.OwnSideUndockPartners);
            Assert.Contains(AllRows(rows), r => r.OwnerHeadId == "shipAfter");

            MissionPresentation.MissionSummaryFacts facts = MissionPresentation.ComputeSummaryFacts(
                s, MissionThroughLineBuilder.Build(s), roots, new HashSet<string>(partners));
            Assert.Equal(AllRows(rows).Count(r => !r.IsPartner && !r.IsPerson), facts.VesselCount);
        }

        // catches: the documented ambiguity - two of this mission's ships docked to one
        // partner - losing or duplicating a stored key. The row walk follows the first own
        // child at each undock (the Log's rule), and every key is still drawn exactly once.
        [Fact]
        public void TwoOwnShipsDockedToOnePartner_EveryKeyDrawnOnce()
        {
            var recs = new[]
            {
                Rec("a", "Ship A", 0, 100, 1),
                Rec("p", "Station", 0, 100, 2),
                Rec("b", "Ship B", 0, 150, 3),
                Rec("c1", "Station", 100, 150, 2),
                Rec("c2", "Station", 150, 200, 2),
                Rec("c3", "Station", 200, 250, 2),
                Rec("bAfter", "Ship B", 200, 400, 4, TerminalState.Orbiting),
                Rec("c4", "Station", 250, 300, 2, TerminalState.Orbiting),
                Rec("aAfter", "Ship A", 250, 500, 5, TerminalState.Landed),
            };
            var tree = new RecordingTree { Id = "t2", RootRecordingId = "a" };
            foreach (Recording r in recs) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.Add(BP("dockA", BranchPointType.Dock, 100, new[] { "a", "p" }, new[] { "c1" }));
            tree.BranchPoints.Add(BP("dockB", BranchPointType.Dock, 150, new[] { "c1", "b" }, new[] { "c2" }));
            tree.BranchPoints.Add(BP("undockB", BranchPointType.Undock, 200, new[] { "c2" }, new[] { "c3", "bAfter" }));
            tree.BranchPoints.Add(BP("undockA", BranchPointType.Undock, 250, new[] { "c3" }, new[] { "c4", "aAfter" }));
            string[] partners = { "p", "c1", "c2", "c3", "c4" };
            List<MissionVesselRow> rows = Rows(tree, partners, out MissionUndockSides sides,
                out List<MissionCompositionNode> roots);
            output.WriteLine(Shape(rows));
            Assert.Equal(2, sides.OwnSideByOwnChild.Count);

            var compKeys = new List<string>();
            foreach (MissionCompositionNode root in roots) CollectKeys(root, compKeys);
            compKeys.Sort(StringComparer.Ordinal);
            Assert.Equal(compKeys, RowKeys(rows));
            Assert.Equal(compKeys.Count, KeyToRow(rows).Count);   // KeyToRow throws on a duplicate
            Assert.Equal(compKeys, RowKeys(MissionVesselRowBuilder.Build(roots)));
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertSpan(MissionCompositionNode node, double start, double end)
        {
            Assert.InRange(node.StartUT, start, end);
            Assert.InRange(node.EndUT, start, end);
        }

        private static void CollectKeys(MissionCompositionNode node, List<string> into)
        {
            if (node.IsSelectable) into.Add(node.HeadLegId);
            foreach (MissionCompositionNode c in node.Children) CollectKeys(c, into);
        }

        private static Dictionary<string, MissionCompositionNode> NodesByKey(List<MissionCompositionNode> roots)
        {
            var map = new Dictionary<string, MissionCompositionNode>(StringComparer.Ordinal);
            var stack = new Stack<MissionCompositionNode>(roots);
            while (stack.Count > 0)
            {
                MissionCompositionNode n = stack.Pop();
                if (n.IsSelectable) map[n.HeadLegId] = n;
                foreach (MissionCompositionNode c in n.Children) stack.Push(c);
            }
            return map;
        }

        private static List<string> RowKeys(IEnumerable<MissionVesselRow> rows)
        {
            var keys = new List<string>();
            foreach (MissionVesselRow r in AllRows(rows))
                keys.AddRange(MissionVesselRowBuilder.IntervalKeys(r));
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static Dictionary<string, string> KeyToRow(IEnumerable<MissionVesselRow> rows)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (MissionVesselRow r in AllRows(rows))
                foreach (string k in MissionVesselRowBuilder.IntervalKeys(r))
                    map.Add(k, r.OwnerHeadId);
            return map;
        }
    }
}
