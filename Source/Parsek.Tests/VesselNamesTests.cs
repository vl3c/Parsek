using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 1 of design-vessel-and-mission-names.md: the shared identity resolver
    /// (<see cref="VesselIdentity"/>), the decision store and its coverage rule
    /// (<see cref="VesselNameStore"/>), the apply pass, the codec, the
    /// <see cref="DisplayNames.Version"/> cache keys, and the load-policy cells.
    /// </summary>
    [Collection("Sequential")]
    public class VesselNamesTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string G1 = "11111111111111111111111111111111";
        private const string G2 = "22222222222222222222222222222222";
        private const string G3 = "33333333333333333333333333333333";

        public VesselNamesTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionStructureBuilder.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            VesselNameStore.NowUtProviderForTesting = () => 1000.0;
            DisplayNames.ResetSubscribersForTesting();
        }

        public void Dispose()
        {
            VesselNameStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            DisplayNames.ResetSubscribersForTesting();
            MissionStructureBuilder.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------- builders

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
            foreach (Recording r in recs)
            {
                r.TreeId = id;
                tree.Recordings[r.RecordingId] = r;
            }
            tree.BranchPoints.AddRange(bps);
            foreach (BranchPoint bp in bps)
            {
                foreach (string c in bp.ChildRecordingIds)
                    if (tree.Recordings.TryGetValue(c, out Recording child))
                        child.ParentBranchPointId = bp.Id;
                foreach (string p in bp.ParentRecordingIds)
                    if (tree.Recordings.TryGetValue(p, out Recording par))
                        par.ChildBranchPointId = bp.Id;
            }
            return tree;
        }

        private static VesselIdentity.Resolution Resolve(params RecordingTree[] trees)
            => VesselIdentity.Resolve(trees, null);

        private static bool Same(VesselIdentity.Resolution res, string a, string b)
            => ReferenceEquals(res.ByRecordingId[a], res.ByRecordingId[b]);

        private void UseUniverse(params RecordingTree[] trees)
        {
            VesselNameStore.TreesProviderForTesting = () => trees;
        }

        // ---------------------------------------------------------------- VesselIdentity

        [Fact]
        public void Identity_SameGuidAcrossTrees_IsOneVessel_EvenWithADifferentPid()
        {
            RecordingTree a = Tree("A", new[] { Rec("a1", "Probe", 0, 10, pid: 5, guid: G1) });
            RecordingTree b = Tree("B", new[] { Rec("b1", "Probe", 20, 30, pid: 99, guid: G1) });
            VesselIdentity.Resolution res = Resolve(a, b);
            Assert.True(Same(res, "a1", "b1"));
            Assert.Contains(G1, res.ByRecordingId["a1"].Guids);
        }

        [Fact]
        public void Identity_Chain_JoinsGuidlessSegments()
        {
            RecordingTree t = Tree("T", new[]
            {
                Rec("h", "X", 0, 10, chain: "c", chainIndex: 0),
                Rec("t", "X", 10, 20, chain: "c", chainIndex: 1),
            });
            Assert.True(Same(Resolve(t), "h", "t"));
        }

        [Fact]
        public void Identity_SwitchContinuationSoleChild_JoinsParentAndChild()
        {
            Recording p = Rec("p", "Lander", 0, 10, pid: 1, guid: G1);
            Recording c = Rec("c", "Lander", 10, 20, pid: 2);
            RecordingTree t = Tree("T", new[] { p, c },
                BP("s", BranchPointType.VesselSwitchContinuation, 10, new[] { "p" }, new[] { "c" }));
            VesselIdentity.Resolution res = Resolve(t);
            Assert.True(Same(res, "p", "c"));
            Assert.Single(VesselIdentity.SwitchContinuationPairs(t));
        }

        [Fact]
        public void Identity_LegacyPidMatch_InOneTreeOnly()
        {
            RecordingTree t = Tree("T", new[] { Rec("x1", "Old", 0, 10, pid: 7), Rec("x2", "Old", 10, 20, pid: 7) });
            RecordingTree u = Tree("U", new[] { Rec("y1", "Old", 30, 40, pid: 7) });
            VesselIdentity.Resolution res = Resolve(t, u);
            Assert.True(Same(res, "x1", "x2"));
            Assert.False(Same(res, "x1", "y1"));
            Assert.False(res.ByRecordingId["x1"].HasGuid);
        }

        [Fact]
        public void Identity_TwoLaunchesOfOneCraft_StayApart()
        {
            // Craft-baked pid shared, launch guids differ: never one vessel, in one tree or two.
            RecordingTree a = Tree("A", new[] { Rec("a1", "Kerbal X", 0, 10, pid: 100, guid: G1) });
            RecordingTree b = Tree("B", new[] { Rec("b1", "Kerbal X", 50, 60, pid: 100, guid: G2) });
            VesselIdentity.Resolution res = Resolve(a, b);
            Assert.False(Same(res, "a1", "b1"));
        }

        [Fact]
        public void Identity_InTreeRepiddedContinuation_JoinsTheShipNotThePartner()
        {
            RecordingTree station = Tree("S", new[] { Rec("s1", "Station", 0, 500, pid: 100, guid: G3) });
            Recording ship = Rec("ship", "Ship", 10, 50, pid: 200, guid: G1, partPids: new uint[] { 11, 12, 13 });
            Recording stack = Rec("stack", "Station", 50, 60, pid: 100, guid: G3, partPids: new uint[] { 11, 900 });
            Recording stationHalf = Rec("halfS", "Station", 60, 90, pid: 100, guid: G3, partPids: 900);
            Recording shipHalf = Rec("halfA", "Ship", 60, 90, pid: 201, guid: G2, partPids: new uint[] { 12, 13 });
            ship.TerminalStateValue = TerminalState.Docked;
            RecordingTree mission = Tree("A", new[] { ship, stack, stationHalf, shipHalf },
                BP("d", BranchPointType.Dock, 50, new[] { "ship" }, new[] { "stack" }),
                BP("u", BranchPointType.Undock, 60, new[] { "stack" }, new[] { "halfS", "halfA" }));

            VesselIdentity.Resolution res = Resolve(mission, station);
            Assert.True(Same(res, "ship", "halfA"));
            Assert.False(Same(res, "ship", "stack"));
            Assert.True(Same(res, "stack", "s1"));
            Assert.True(Same(res, "halfS", "s1"));
            Assert.Equal(1, res.InTreeContinuations);
            Assert.Contains(G2, res.ByRecordingId["ship"].Guids);
        }

        [Fact]
        public void Identity_EvaLegs_AreExcluded()
        {
            RecordingTree t = Tree("T", new[]
            {
                Rec("v", "Ship", 0, 10, pid: 1, guid: G1),
                Rec("e", "Bob Kerman", 5, 8, pid: 1, guid: G1, eva: "Bob Kerman"),
            });
            VesselIdentity.Resolution res = Resolve(t);
            Assert.False(res.ByRecordingId.ContainsKey("e"));
            Assert.Equal(1, res.EvaSkipped);
        }

        [Fact]
        public void Identity_LiveVessel_ResolvesByGuidOnly_AndSkipsGhostMapPids()
        {
            RecordingTree t = Tree("T", new[] { Rec("v", "Ship", 0, 10, pid: 1, guid: G1) });
            VesselIdentity.Resolution res = Resolve(t);
            Assert.Same(res.ByRecordingId["v"], res.ResolveLiveVessel(
                "11111111-1111-1111-1111-111111111111", 1, pid => false));
            Assert.Null(res.ResolveLiveVessel(G1, 1, pid => pid == 1));
            Assert.Null(res.ResolveLiveVessel(null, 1, pid => false));
            Assert.Null(res.ResolveLiveVessel(G2, 1, pid => false));
        }

        [Fact]
        public void Identity_SameIdInTwoTrees_IsOneLegWithBothCopies()
        {
            // The live tree is a copy-on-write clone of a committed tree: same ids.
            Recording committed = Rec("v", "Ship", 0, 10, pid: 1, guid: G1);
            Recording live = Rec("v", "Ship", 0, 10, pid: 1, guid: G1);
            VesselIdentity.Resolution res = Resolve(Tree("T", new[] { committed }), Tree("T", new[] { live }));
            Assert.Equal(1, res.Legs);
            Assert.Equal(2, res.Copies("v").Count);
        }

        [Fact]
        public void Identity_LinkSeam_JoinsTwoGuids()
        {
            RecordingTree a = Tree("A", new[] { Rec("a1", "P", 0, 10, pid: 1, guid: G1) });
            RecordingTree b = Tree("B", new[] { Rec("b1", "P", 20, 30, pid: 2, guid: G2) });
            Assert.False(Same(Resolve(a, b), "a1", "b1"));
            VesselIdentity.Resolution linked = VesselIdentity.Resolve(new[] { a, b }, null,
                new[] { new KeyValuePair<string, string>(G2, G1) });
            Assert.True(Same(linked, "a1", "b1"));
            Assert.Equal(1, linked.LinkEdges);
        }

        // ---------------------------------------------------------------- decisions

        [Fact]
        public void Rename_SetsEveryLegOfTheIdentity_AndRecordsOneDecision()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1, chain: "c", chainIndex: 0);
            Recording a2 = Rec("a2", "Mission1", 100, 200, pid: 1, guid: G1, chain: "c", chainIndex: 1);
            Recording other = Rec("b1", "P2", 100, 300, pid: 2, guid: G2);
            RecordingTree t = Tree("T", new[] { a1, a2, other },
                BP("s", BranchPointType.JointBreak, 100, new[] { "a1" }, new[] { "b1" }));
            int before = DisplayNames.Version;

            Assert.True(MissionVesselNaming.ApplyRecordingRename(a2, " P1 ", new[] { t }));

            Assert.Equal("P1", a1.VesselName);
            Assert.Equal("P1", a2.VesselName);
            Assert.Equal("P2", other.VesselName);
            VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal(1, d.Seq);
            Assert.Equal(2, VesselNameStore.NextSeq);
            Assert.Equal(VesselNameSource.ParsekRename, d.Source);
            Assert.Equal(new[] { "Mission1" }, d.PriorNames.ToArray());
            Assert.True(d.CoveredLegIds.SetEquals(new[] { "a1", "a2" }));
            Assert.Same(d, VesselNameStore.FindByGuid(G1));
            Assert.True(DisplayNames.Version > before);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Vessel name decision recorded: identity=a1 'Mission1' -> 'P1' source=ParsekRename seq=1 legs=2"));
            Assert.Contains(logLines, l => l.Contains("[INFO][UI]")
                && l.Contains("Recording 'Mission1' renamed to 'P1' identity=a1 legs=2"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][VesselNames]")
                && l.Contains("Apply pass (parsek-rename): identities=2 withDecision=1 newlyCovered=0 newlyUncovered=0 legsRenamed=2"));
        }

        [Fact]
        public void Rename_Again_ReplacesTheDecision_KeepingEveryEarlierNameAsPrior()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", new[] { a1 });
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "P1", new[] { t }));
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Relay", new[] { t }));

            VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal(2, d.Seq);
            Assert.Equal("Relay", d.Name);
            Assert.Equal(new[] { "Mission1", "P1" }, d.PriorNames.OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal("Relay", a1.VesselName);
        }

        [Fact]
        public void Rename_BlankOrUnchanged_WritesNothing()
        {
            Recording a1 = Rec("a1", "Ship", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", new[] { a1 });
            int version = DisplayNames.Version;
            int storeVersion = VesselNameStore.Version;

            Assert.False(VesselNameStore.RecordParsekRename(a1, "   ", new[] { t }, out string blank));
            Assert.False(VesselNameStore.RecordParsekRename(a1, " Ship ", new[] { t }, out string same));

            Assert.Equal(VesselNameStore.RefusalBlank, blank);
            Assert.Equal(VesselNameStore.RefusalUnchanged, same);
            Assert.Empty(VesselNameStore.Decisions);
            Assert.Equal(version, DisplayNames.Version);
            Assert.Equal(storeVersion, VesselNameStore.Version);
        }

        [Fact]
        public void Rename_EvaRowAndGuidlessIdentity_AreRefusedAndLogged()
        {
            Recording v = Rec("v", "Ship", 0, 100, pid: 1);
            Recording e = Rec("e", "Bob Kerman", 10, 20, pid: 3, guid: G2, eva: "Bob Kerman");
            RecordingTree t = Tree("T", new[] { v, e });

            Assert.False(VesselNameStore.RecordParsekRename(e, "Bob", new[] { t }, out string eva));
            Assert.False(VesselNameStore.RecordParsekRename(v, "Renamed", new[] { t }, out string noGuid));

            Assert.Equal(VesselNameStore.RefusalEva, eva);
            Assert.Equal(VesselNameStore.RefusalNoGuid, noGuid);
            Assert.Equal("Ship", v.VesselName);
            Assert.Equal("Bob Kerman", e.VesselName);
            Assert.Empty(VesselNameStore.Decisions);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Parsek rename refused: rec=v name='Renamed' reason=no-guid"));
            Assert.Contains(logLines, l => l.Contains("Parsek rename refused: rec=e name='Bob' reason=eva"));
        }

        [Fact]
        public void Decision_IsIndexedUnderEveryIdentityGuid()
        {
            // A re-pidded continuation (fresh guid) is one identity with two guids.
            Recording h = Rec("h", "Ship", 0, 10, pid: 1, guid: G1, chain: "c", chainIndex: 0);
            Recording k = Rec("k", "Ship", 10, 20, pid: 2, guid: G2, chain: "c", chainIndex: 1);
            RecordingTree t = Tree("T", new[] { h, k });
            Assert.True(MissionVesselNaming.ApplyRecordingRename(h, "Twin", new[] { t }));
            Assert.Same(VesselNameStore.FindByGuid(G1), VesselNameStore.FindByGuid(G2));
            Assert.Equal(2, VesselNameStore.Decisions[0].Guids.Count);
        }

        [Fact]
        public void Conflict_HigherSeqWins_NotTheLaterUT_AndTheLoserFoldsIn()
        {
            Recording a = Rec("a", "A0", 0, 10, pid: 1, guid: G1);
            Recording b = Rec("b", "B0", 20, 30, pid: 2, guid: G2);
            RecordingTree ta = Tree("A", new[] { a });
            RecordingTree tb = Tree("B", new[] { b });

            // seq 1 decided "later" in game UT (a rewind moved the clock back before seq 2).
            VesselNameStore.NowUtProviderForTesting = () => 9000.0;
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "First", new[] { ta, tb }));
            VesselNameStore.NowUtProviderForTesting = () => 100.0;
            Assert.True(MissionVesselNaming.ApplyRecordingRename(b, "Second", new[] { ta, tb }));
            Assert.Equal(2, VesselNameStore.Decisions.Count);

            // A later link joins the two components (the Phase 2b edge, through its seam).
            VesselIdentity.Resolution joined = VesselIdentity.Resolve(new[] { ta, tb }, null,
                new[] { new KeyValuePair<string, string>(G2, G1) });
            VesselNameStore.ApplyTo(joined, "test-join");

            VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal(2, d.Seq);
            Assert.Equal("Second", a.VesselName);
            Assert.Equal("Second", b.VesselName);
            Assert.Same(d, VesselNameStore.FindByGuid(G1));
            Assert.Contains("First", d.PriorNames);
            Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]")
                && l.Contains("keeping seq=2 'Second', dropping seq=1 'First'"));
        }

        // ---------------------------------------------------------------- coverage

        private RecordingTree NamedProbeTree(out Recording launch)
        {
            launch = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", new[] { launch });
            Assert.True(MissionVesselNaming.ApplyRecordingRename(launch, "P1", new[] { t }));
            return t;
        }

        [Fact]
        public void Coverage_LaterLegWithAPriorOrTheDecisionName_IsCoveredAndRenamed()
        {
            RecordingTree t = NamedProbeTree(out _);
            // Phase 1 writes no live vessel: the next leg captures the old stock name.
            Recording stale = Rec("a2", "Mission1", 100, 200, pid: 1, guid: G1);
            Recording current = Rec("a3", "P1", 200, 300, pid: 1, guid: G1);
            t.Recordings["a2"] = stale;
            t.Recordings["a3"] = current;

            VesselNameStore.ApplyResult r = VesselNameStore.ApplyTo(Resolve(t), "test");

            Assert.Equal(2, r.NewlyCovered);
            Assert.Equal(0, r.NewlyUncovered);
            Assert.Equal(1, r.LegsRenamed);
            Assert.Equal("P1", stale.VesselName);
            Assert.True(VesselNameStore.Decisions[0].CoveredLegIds.SetEquals(new[] { "a1", "a2", "a3" }));
        }

        [Fact]
        public void Coverage_LaterLegWithAnotherName_IsUncovered_WhateverItsUT()
        {
            RecordingTree t = NamedProbeTree(out Recording launch);
            // Stock AutoRename after the control part left at staging, captured on a leg that
            // starts BEFORE the decision's UT (an F9 to an earlier quicksave): still uncovered.
            Recording auto = Rec("a2", "Mission1 Probe", 50, 150, pid: 1, guid: G1);
            t.Recordings["a2"] = auto;

            VesselNameStore.ApplyResult r = VesselNameStore.ApplyTo(Resolve(t), "test");

            Assert.Equal(1, r.NewlyUncovered);
            Assert.Equal("Mission1 Probe", auto.VesselName);
            Assert.Equal("P1", launch.VesselName);
            Assert.Contains("a2", VesselNameStore.Decisions[0].UncoveredLegIds);

            // Classified once: renaming the leg later (a copy) never re-classifies it.
            auto.VesselName = "P1";
            VesselNameStore.ApplyTo(Resolve(t), "test");
            Assert.Contains("a2", VesselNameStore.Decisions[0].UncoveredLegIds);

            // A new decision covers every existing leg again (ruling 7).
            Assert.True(MissionVesselNaming.ApplyRecordingRename(launch, "P1b", new[] { t }));
            Assert.Equal("P1b", auto.VesselName);
        }

        [Fact]
        public void Coverage_ReappliesAfterAReFlyUnionSwap()
        {
            RecordingTree t = NamedProbeTree(out _);
            // The union swaps the session's older recording in by reference: same id, old name.
            Recording older = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            t.Recordings["a1"] = older;

            VesselNameStore.ApplyResult r = VesselNameStore.ApplyTo(Resolve(t), "test");

            Assert.Equal(0, r.NewlyCovered + r.NewlyUncovered);
            Assert.Equal(1, r.LegsRenamed);
            Assert.Equal("P1", older.VesselName);
        }

        [Fact]
        public void Coverage_FollowsAnOptimizerSplitAndMerge()
        {
            RecordingTree t = NamedProbeTree(out Recording launch);
            launch.ChainId = "c";
            launch.ChainIndex = 0;
            // Split: the second half copies the post-apply name under a new id.
            Recording half = Rec("a1b", "P1", 50, 100, pid: 1, guid: G1, chain: "c", chainIndex: 1);
            t.Recordings["a1b"] = half;
            VesselNameStore.ApplyResult split = VesselNameStore.ApplyTo(Resolve(t), "optimizer");
            Assert.Equal(1, split.NewlyCovered);
            Assert.Equal(0, split.LegsRenamed);

            // Merge: the half's id goes away; nothing breaks and the survivor keeps the name.
            t.Recordings.Remove("a1b");
            launch.VesselName = "Mission1";
            VesselNameStore.ApplyResult merged = VesselNameStore.ApplyTo(Resolve(t), "optimizer");
            Assert.Equal(1, merged.LegsRenamed);
            Assert.Equal("P1", launch.VesselName);
        }

        [Fact]
        public void Apply_RenamesEveryCopy_BumpsTheVersionOnce_AndNothingWhenUnchanged()
        {
            Recording committed = Rec("v", "Ship", 0, 10, pid: 1, guid: G1);
            Recording leg2 = Rec("w", "Ship", 10, 20, pid: 1, guid: G1);
            RecordingTree t = Tree("T", new[] { committed, leg2 });
            Assert.True(MissionVesselNaming.ApplyRecordingRename(committed, "Named", new[] { t }));

            Recording liveCopy = Rec("v", "Ship", 0, 10, pid: 1, guid: G1);
            Recording liveLeg2 = Rec("w", "Ship", 10, 20, pid: 1, guid: G1);
            RecordingTree live = Tree("T", new[] { liveCopy, liveLeg2 });
            int before = DisplayNames.Version;
            VesselNameStore.ApplyResult r = VesselNameStore.ApplyTo(Resolve(t, live), "test");
            Assert.Equal(2, r.LegsRenamed);
            Assert.Equal("Named", liveCopy.VesselName);
            Assert.Equal(before + 1, DisplayNames.Version);

            VesselNameStore.ApplyTo(Resolve(t, live), "test");
            Assert.Equal(before + 1, DisplayNames.Version);
        }

        [Fact]
        public void ApplyAll_IsInertWithNoDecision()
        {
            RecordingTree t = Tree("T", new[] { Rec("v", "Ship", 0, 10, pid: 1, guid: G1) });
            UseUniverse(t);
            VesselNameStore.ApplyResult r = VesselNameStore.ApplyAll("test");
            Assert.False(r.Ran);
            Assert.DoesNotContain(logLines, l => l.Contains("Apply pass ("));
        }

        [Fact]
        public void SelfHeal_RunsWhenStateVersionMoves_NotOtherwise()
        {
            Recording v = Rec("v", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", new[] { v });
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(v, "Named", null));
            VesselNameStore.ApplyAll("prime");

            v.VesselName = "Ship";
            VesselNameStore.SelfHealTick(inFlight: false, realtime: 0f);
            Assert.Equal("Ship", v.VesselName);

            RecordingStore.BumpStateVersion();
            VesselNameStore.SelfHealTick(inFlight: false, realtime: 0f);
            Assert.Equal("Named", v.VesselName);
            Assert.Contains(logLines, l => l.Contains("Apply pass (self-heal:state-version)"));
        }

        // ---------------------------------------------------------------- codec

        [Fact]
        public void Codec_RoundTrips_UnderAGermanCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                VesselNameStore.NowUtProviderForTesting = () => 1234.5;
                Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1, chain: "c", chainIndex: 0);
                Recording a2 = Rec("a2", "Mission1", 100, 200, pid: 2, guid: G2, chain: "c", chainIndex: 1);
                RecordingTree t = Tree("T", new[] { a1, a2 });
                Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Relay-2", new[] { t }));
                t.Recordings["a3"] = Rec("a3", "Other", 200, 300, pid: 1, guid: G1);
                VesselNameStore.ApplyTo(Resolve(t), "test");

                var scenario = new ConfigNode("SCENARIO");
                VesselNameStore.Save(scenario);
                ConfigNode node = scenario.GetNode(VesselNameStore.NodeName);
                Assert.NotNull(node);
                Assert.Equal("1234.5", node.GetNode("DECISION").GetValue("ut"));
                Assert.Equal("2", node.GetValue("nextSeq"));

                VesselNameStore.ResetForTesting();
                VesselNameStore.Load(scenario);

                VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
                Assert.Equal(1, d.Seq);
                Assert.Equal(2, VesselNameStore.NextSeq);
                Assert.Equal("Relay-2", d.Name);
                Assert.Equal(1234.5, d.DecidedUT);
                Assert.Equal(VesselNameSource.ParsekRename, d.Source);
                Assert.Equal(new[] { G1, G2 }, d.Guids.ToArray());
                Assert.Equal(new[] { "Mission1" }, d.PriorNames.ToArray());
                Assert.True(d.CoveredLegIds.SetEquals(new[] { "a1", "a2" }));
                Assert.True(d.UncoveredLegIds.SetEquals(new[] { "a3" }));
                Assert.Same(d, VesselNameStore.FindByGuid(G2));
                Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                    && l.Contains("Loaded VESSEL_NAMES: decisions=1 skipped=0 nextSeq=2"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Codec_EmptyStoreWritesNoNode_AndAnAbsentNodeIsAnEmptyStore()
        {
            var scenario = new ConfigNode("SCENARIO");
            VesselNameStore.Save(scenario);
            Assert.Null(scenario.GetNode(VesselNameStore.NodeName));

            VesselNameStore.Load(new ConfigNode("SCENARIO"));
            Assert.Empty(VesselNameStore.Decisions);
            Assert.Equal(1, VesselNameStore.NextSeq);

            var empty = new ConfigNode("SCENARIO");
            empty.AddNode(VesselNameStore.NodeName);
            VesselNameStore.Load(empty);
            Assert.Empty(VesselNameStore.Decisions);
        }

        [Fact]
        public void Codec_SkipsMalformedDecisions_AndFoldsADoubleClaimedGuid()
        {
            var scenario = new ConfigNode("SCENARIO");
            ConfigNode node = scenario.AddNode(VesselNameStore.NodeName);
            node.AddValue("nextSeq", "3");
            ConfigNode bad = node.AddNode("DECISION");
            bad.AddValue("seq", "x");
            bad.AddValue("name", "Broken");
            ConfigNode older = node.AddNode("DECISION");
            older.AddValue("seq", "1");
            older.AddValue("guid", G1);
            older.AddValue("name", "Old");
            older.AddValue("covered", "a1");
            ConfigNode newer = node.AddNode("DECISION");
            newer.AddValue("seq", "5");
            newer.AddValue("guid", G1);
            newer.AddValue("guid", G2);
            newer.AddValue("name", "New");
            newer.AddValue("source", "StockDialog");

            VesselNameStore.Load(scenario);

            VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal(5, d.Seq);
            Assert.Equal(VesselNameSource.StockDialog, d.Source);
            Assert.Contains("a1", d.CoveredLegIds);
            Assert.Contains("Old", d.PriorNames);
            Assert.Equal(6, VesselNameStore.NextSeq);
            Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]") && l.Contains("malformed DECISION"));
            Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]") && l.Contains("keeping seq=5 'New', folding seq=1 'Old'"));
        }

        // ---------------------------------------------------------------- load policy

        [Fact]
        public void LoadPolicy_VesselNames_ColdReadsTheSave_EveryInSessionKindKeepsMemory()
        {
            Assert.False(LoadReconcilePolicy.IsConsumedEarly(LoadStateCategory.VesselNames));
            foreach (LoadKind kind in Enum.GetValues(typeof(LoadKind)).Cast<LoadKind>())
            {
                LoadReconcileDecision d = LoadReconcilePolicy.Decide(kind, LoadStateCategory.VesselNames);
                Assert.False(d.IsKnownGap);
                Assert.Equal(kind == LoadKind.Cold ? LoadReconcileAction.Save : LoadReconcileAction.Memory, d.Action);
            }
            // All three refined in-session kinds agree today (Phase 2a flips StockRevert).
            Assert.Equal(LoadReconcileAction.Memory,
                LoadReconcilePolicy.DecideEarly(EarlyLoadKind.InSession, LoadStateCategory.VesselNames).Action);
        }

        // ---------------------------------------------------------------- display version + caches

        [Fact]
        public void DisplayNames_RaisesTheEvent_AndAThrowingSubscriberDoesNotStopTheOthers()
        {
            int calls = 0;
            DisplayNames.DisplayNamesChanged += () => throw new InvalidOperationException("boom");
            DisplayNames.DisplayNamesChanged += () => calls++;
            int before = DisplayNames.Version;
            DisplayNames.NoteChanged("test", "x");
            Assert.Equal(before + 1, DisplayNames.Version);
            Assert.Equal(1, calls);
            Assert.Contains(logLines, l => l.Contains("[WARN][VesselNames]") && l.Contains("boom"));
        }

        [Fact]
        public void MissionRename_MovesTheDisplayVersion()
        {
            var m = new Mission("m1", "T", "Old");
            int before = DisplayNames.Version;
            MissionStore.RenameMission(m, "New");
            Assert.Equal(before + 1, DisplayNames.Version);
            MissionStore.RenameMission(m, "New");
            Assert.Equal(before + 1, DisplayNames.Version);
        }

        [Fact]
        public void MissionLogSignature_MovesWithTheDisplayVersion()
        {
            int a = StructureListWindowUI.ComputeChangeSignature(7, true, "M", null, 1);
            int b = StructureListWindowUI.ComputeChangeSignature(7, true, "M", null, 2);
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void MissionNamingCache_RebuildsWhenTheDisplayVersionMoves()
        {
            Recording v = Rec("v", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", new[] { v });
            var cache = new MissionVesselNaming.Cache();
            var trees = new List<RecordingTree> { t };
            Dictionary<string, string> first = cache.GetOrBuild(t, MissionStructureBuilder.Build, trees, 1, 1, null);
            Assert.Same(first, cache.GetOrBuild(t, MissionStructureBuilder.Build, trees, 1, 1, null));
            DisplayNames.NoteChanged("test", null);
            Assert.NotSame(first, cache.GetOrBuild(t, MissionStructureBuilder.Build, trees, 1, 1, null));
            Assert.Equal(2, cache.Rebuilds);
        }

        [Fact]
        public void DockEventGraphCache_RebuildsWhenTheDisplayVersionMoves()
        {
            EffectiveState.ResetCachesForTesting();
            DockEventGraphCache.ResetForTesting();
            DockEventGraph first = DockEventGraphCache.GetOrBuild();
            Assert.Same(first, DockEventGraphCache.GetOrBuild());
            DisplayNames.NoteChanged("test", null);
            Assert.NotSame(first, DockEventGraphCache.GetOrBuild());
            DockEventGraphCache.ResetForTesting();
        }

        [Fact]
        public void CommittedFutureIndexCache_RebuildsWhenTheDisplayVersionMoves()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            LedgerOrchestrator.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            try
            {
                CommittedFutureIndex first = CommittedFutureIndexCache.Current;
                Assert.Same(first, CommittedFutureIndexCache.Current);
                DisplayNames.NoteChanged("test", null);
                Assert.NotSame(first, CommittedFutureIndexCache.Current);
            }
            finally
            {
                GameStateStore.ResetForTesting();
                MilestoneStore.ResetForTesting();
                LedgerOrchestrator.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
                ParsekScenario.ResetInstanceForTesting();
            }
        }

        [Fact]
        public void GhostProtoRename_IsInPlaceAndOnlyWhenTheNameMoved()
        {
            Assert.Equal("Ghost: Relay", GhostMapPresence.BuildGhostProtoVesselName("Relay"));
            Assert.Null(GhostMapPresence.ResolveGhostProtoRename("Ghost: Relay", "Relay"));
            Assert.Equal("Ghost: Relay-2", GhostMapPresence.ResolveGhostProtoRename("Ghost: Relay", "Relay-2"));
            Assert.Equal("Ghost: Unknown", GhostMapPresence.ResolveGhostProtoRename("Ghost: Relay", null));
        }

        [Fact]
        public void GhostCommNetNodeName_ReadsTheRecordingNameFirst()
        {
            Assert.Equal("Relay-2", GhostCommNetMath.ResolveNodeDisplayName("Relay-2", "P2"));
            Assert.Equal("P2", GhostCommNetMath.ResolveNodeDisplayName("", "P2"));
            Assert.Equal("", GhostCommNetMath.ResolveNodeDisplayName(null, null));
        }

        [Fact]
        public void RecordingIdSetSignature_IsOrderFree_AndMovesOnANewLeg()
        {
            int a = VesselNameStore.ComputeRecordingIdSetSignature(new[] { "x", "y" });
            Assert.Equal(a, VesselNameStore.ComputeRecordingIdSetSignature(new[] { "y", "x" }));
            Assert.NotEqual(a, VesselNameStore.ComputeRecordingIdSetSignature(new[] { "x", "y", "z" }));
        }
    }
}
