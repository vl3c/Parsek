using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 2a of design-vessel-and-mission-names.md: the stock rename capture core (7.1), the
    /// write-through naming-part tie rule (7.3), the live write rule (7.4 step 3), the Parsek
    /// rename writing through (7.2), revert epochs and the revert undo (7.6, ruling 5), the
    /// VesselNames policy cells, and the wiring of those hooks into the real code paths.
    /// </summary>
    [Collection("Sequential")]
    public class VesselNamesStockTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        private const string G1 = "11111111111111111111111111111111";
        private const string G2 = "22222222222222222222222222222222";
        private const string G9 = "99999999999999999999999999999999";

        public VesselNamesStockTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionStructureBuilder.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RevertInterceptor.ResetTestOverrides();
            ReFlyRevertDialog.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            VesselNameStore.ResetForTesting();
            VesselNameStore.NowUtProviderForTesting = () => 1000.0;
            DisplayNames.ResetSubscribersForTesting();
        }

        public void Dispose()
        {
            VesselNameStore.ResetForTesting();
            RevertInterceptor.ResetTestOverrides();
            ReFlyRevertDialog.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = priorStoreSuppress;
            DisplayNames.ResetSubscribersForTesting();
            MissionStructureBuilder.SuppressLogging = false;
            ParsekLog.ResetTestOverrides();
        }

        // ---------------------------------------------------------------- builders

        private static Recording Rec(string id, string vessel, double start, double end,
            uint pid = 0, string guid = null, string chain = null, int chainIndex = -1)
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

        private static RecordingTree Tree(string id, params Recording[] recs)
        {
            var tree = new RecordingTree { Id = id, TreeName = id, RootRecordingId = recs[0].RecordingId };
            foreach (Recording r in recs)
            {
                r.TreeId = id;
                tree.Recordings[r.RecordingId] = r;
            }
            return tree;
        }

        private static void UseUniverse(params RecordingTree[] trees)
        {
            VesselNameStore.TreesProviderForTesting = () => trees;
        }

        private static VesselIdentity.Resolution Resolve(params RecordingTree[] trees)
            => VesselIdentity.Resolve(trees, null);

        // The Prefix / body / Postfix of one stock accept: the Prefix keeps the live name,
        // the body renames (and fires onVesselRename, which Parsek does not listen to), the
        // Postfix records through the capture core.
        private static bool StockAccept(RecordingTree tree, string guid, uint pid, string before, string after,
            out VesselNameDecision decision, out string outcome,
            VesselNameSource source = VesselNameSource.StockDialog, string recordingLegId = null)
        {
            return VesselNameLiveSync.RecordStockRename(Resolve(tree), guid, pid, false, before, after, source,
                recordingLegId, null, 1000.0, out decision, out outcome);
        }

        private sealed class FakeVessel
        {
            internal readonly List<string> Writes = new List<string>();
            internal LiveVesselEntry Entry;
        }

        private static FakeVessel Live(string guid, uint pid, string name, string recordingLegId = null, bool ghost = false)
        {
            var f = new FakeVessel();
            f.Entry = new LiveVesselEntry
            {
                Guid = guid, Pid = pid, Name = name, RecordingLegId = recordingLegId, IsGhostMap = ghost,
                Write = n => { f.Writes.Add(n); f.Entry.Name = n; return true; },
            };
            return f;
        }

        // ---------------------------------------------------------------- capture (7.1)

        // catches: a Postfix that records twice (or records the from == to ping stock fires
        // inside the body), or a capture that rewrites the replaced decision's coverage.
        [Fact]
        public void Accept_RecordsOneDecision_AndLeavesTheReplacedDecisionIntact()
        {
            Recording h = Rec("h", "Ship", 0, 10, pid: 1, guid: G1, chain: "c", chainIndex: 0);
            Recording k = Rec("k", "Ship", 10, 20, pid: 2, guid: G2, chain: "c", chainIndex: 1);
            RecordingTree t = Tree("T", h, k);
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(h, "Twin", new[] { t }));
            VesselNameDecision first = VesselNameStore.FindByGuid(G2);
            var coveredBefore = new HashSet<string>(first.CoveredLegIds);
            var uncoveredBefore = new HashSet<string>(first.UncoveredLegIds);

            // The two vessels are apart now; the player renames G1 in the stock dialog.
            k.ChainId = null;
            k.ChainIndex = -1;
            Assert.True(StockAccept(t, G1, 1, "Twin", "Solo", out VesselNameDecision d, out string outcome));

            Assert.Equal("recorded", outcome);
            Assert.Equal(2, VesselNameStore.Decisions.Count);
            Assert.Same(d, VesselNameStore.FindByGuid(G1));
            Assert.Equal(VesselNameSource.StockDialog, d.Source);
            Assert.Contains("Twin", d.PriorNames);
            Assert.Equal("Solo", h.VesselName);
            Assert.Same(first, VesselNameStore.FindByGuid(G2));
            Assert.True(first.CoveredLegIds.SetEquals(coveredBefore));
            Assert.True(first.UncoveredLegIds.SetEquals(uncoveredBefore));
            Assert.Equal("Twin", k.VesselName);

            // The from == to ping (or a second Postfix for the same accept) records nothing.
            long next = VesselNameStore.NextSeq;
            Assert.False(StockAccept(t, G1, 1, "Solo", "Solo", out _, out string again));
            Assert.Equal("name-unchanged", again);
            Assert.Equal(next, VesselNameStore.NextSeq);
            Assert.Equal(2, VesselNameStore.Decisions.Count);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Vessel name decision recorded: identity=h 'Twin' -> 'Solo' source=StockDialog"));
        }

        [Fact]
        public void Accept_StockNameForAVesselThatAlreadyShowsIt_IsStillADecision()
        {
            // The live vessel carried an automatic name; renaming it back to the name its
            // legs show is a player choice (the unchanged-row refusal is for the Parsek editor).
            Recording a = Rec("a", "P1", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            Assert.True(StockAccept(t, G1, 1, "Auto Name", "P1", out VesselNameDecision d, out _));
            Assert.Equal("P1", d.Name);
            Assert.Contains("Auto Name", d.PriorNames);
        }

        [Fact]
        public void Accept_IgnoresGhostsUnknownVesselsAndBarePids()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 7, guid: G1);
            RecordingTree t = Tree("T", a);

            Assert.False(VesselNameLiveSync.RecordStockRename(Resolve(t), G1, 7, true, "Ship", "X",
                VesselNameSource.StockDialog, null, null, 0, out _, out string ghost));
            Assert.False(StockAccept(t, G9, 7, "Ship", "X", out _, out string unknown));
            Assert.False(StockAccept(t, null, 7, "Ship", "X", out _, out string barePid));

            Assert.Equal("ghost-map", ghost);
            Assert.Equal("no-identity", unknown);
            Assert.Equal("no-identity", barePid);
            Assert.Empty(VesselNameStore.Decisions);
            Assert.Equal("Ship", a.VesselName);
        }

        [Fact]
        public void Accept_PartNaming_RecordsAStockPartNamingDecision()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            Assert.True(StockAccept(t, G1, 1, "Ship", "Named By Part", out VesselNameDecision d, out _,
                VesselNameSource.StockPartNaming));
            Assert.Equal(VesselNameSource.StockPartNaming, d.Source);
            Assert.Equal("Named By Part", a.VesselName);
        }

        // ---------------------------------------------------------------- naming part (7.3)

        private static VesselNaming Naming(string name, int priority)
            => new VesselNaming { vesselName = name, namingPriority = priority, vesselType = VesselType.Probe };

        [Fact]
        public void TieRule_LoadedParts_FirstPartWithTheStrictlyHighestPriority()
        {
            var parts = new List<VesselNaming>
            {
                null, Naming("low", 1), Naming("first-best", 3), Naming("second-best", 3), Naming("mid", 2),
            };
            Assert.Equal(2, VesselRenameWriter.FindPriorityNamingIndex(parts));

            Assert.Equal(2, VesselRenameWriter.ApplyNameToPriorityNaming(parts, "Relay-2"));
            Assert.Equal("Relay-2", parts[2].vesselName);
            Assert.Equal("second-best", parts[3].vesselName);
            Assert.Equal(3, parts[2].namingPriority);
            Assert.Equal(VesselType.Probe, parts[2].vesselType);
        }

        [Fact]
        public void TieRule_ProtoSnapshots_PriorityZeroOrNoneNamesNothing()
        {
            var snapshots = new List<VesselNaming> { null, Naming("zero", 0), null };
            Assert.Equal(-1, VesselRenameWriter.FindPriorityNamingIndex(snapshots));
            Assert.Equal(-1, VesselRenameWriter.ApplyNameToPriorityNaming(snapshots, "X"));
            Assert.Equal("zero", snapshots[1].vesselName);
            Assert.Equal(-1, VesselRenameWriter.FindPriorityNamingIndex(null));

            var single = new List<VesselNaming> { null, Naming("core", 1) };
            Assert.Equal(1, VesselRenameWriter.ApplyNameToPriorityNaming(single, "X"));
            Assert.Equal("X", single[1].vesselName);
        }

        [Fact]
        public void WritableName_RejectsBlank()
        {
            Assert.False(VesselRenameWriter.IsWritableName(null));
            Assert.False(VesselRenameWriter.IsWritableName(""));
            Assert.False(VesselRenameWriter.IsWritableName("   "));
            Assert.True(VesselRenameWriter.IsWritableName("P1"));
        }

        // ---------------------------------------------------------------- live write rule (7.4)

        [Fact]
        public void LiveWrite_CoveredCurrentLeg_IsWritten_UncoveredIsLeftAlone()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "P1", new[] { t }));

            // Covered: the vessel's newest leg took the decision; the live name is stale.
            FakeVessel stale = Live(G1, 1, "Mission1");
            VesselNameLiveSync.ApplyLiveWriteRule(Resolve(t), new[] { stale.Entry }, null, "test", "test");
            Assert.Equal(new[] { "P1" }, stale.Writes.ToArray());

            // Stock named the vessel automatically after the decision (staging, control part
            // left): its newest leg captured that name and is uncovered, so it keeps it.
            t.Recordings["a2"] = Rec("a2", "Mission1 Probe", 100, 200, pid: 1, guid: G1);
            t.Recordings["a2"].TreeId = "T";
            VesselIdentity.Resolution res = Resolve(t);
            VesselNameStore.ApplyTo(res, "test");
            FakeVessel auto = Live(G1, 1, "Mission1 Probe");
            LiveWriteResult r = VesselNameLiveSync.ApplyLiveWriteRule(res, new[] { auto.Entry }, null, "test", "test");
            Assert.Empty(auto.Writes);
            Assert.Equal(1, r.Uncovered);

            // The leg recording the vessel right now decides over the newest one.
            FakeVessel recorded = Live(G1, 1, "Mission1", recordingLegId: "a1");
            VesselNameLiveSync.ApplyLiveWriteRule(res, new[] { recorded.Entry }, null, "test", "test");
            Assert.Equal(new[] { "P1" }, recorded.Writes.ToArray());
        }

        [Fact]
        public void DecideLiveWrite_Verdicts()
        {
            var d = new VesselNameDecision { Seq = 1, Name = "P1" };
            d.PriorNames.Add("Old");
            d.CoveredLegIds.Add("c");
            d.CapturedNames["c"] = "Captured";
            d.UncoveredLegIds.Add("u");
            Assert.Equal(LiveWriteVerdict.NoDecision, VesselNameLiveSync.DecideLiveWrite(null, "c", "x", null));
            Assert.Equal(LiveWriteVerdict.NoCurrentLeg, VesselNameLiveSync.DecideLiveWrite(d, null, "x", null));
            Assert.Equal(LiveWriteVerdict.Uncovered, VesselNameLiveSync.DecideLiveWrite(d, "u", "x", null));
            Assert.Equal(LiveWriteVerdict.Unclassified, VesselNameLiveSync.DecideLiveWrite(d, "new", "x", null));
            Assert.Equal(LiveWriteVerdict.AlreadyNamed, VesselNameLiveSync.DecideLiveWrite(d, "c", "P1", null));
            Assert.Equal(LiveWriteVerdict.Write, VesselNameLiveSync.DecideLiveWrite(d, "c", "Old", null));
            Assert.Equal(LiveWriteVerdict.Write, VesselNameLiveSync.DecideLiveWrite(d, "c", "Captured", null));
            Assert.Equal(LiveWriteVerdict.UnchosenName, VesselNameLiveSync.DecideLiveWrite(d, "c", "x", null));
        }

        // The staging race (design 8.17): the continuing vessel's new leg opened while it still
        // carried the decision name, then ModuleDecouple renamed it automatically one Update
        // later. A name nobody chose is never written over: the open leg becomes uncovered and
        // takes the live name, as if it had opened after the rename (ruling 4).
        [Fact]
        public void LiveWrite_StagingRace_AnAutomaticNameAfterTheLegOpened_IsKept_AndUncoversTheOpenLeg()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "P1", new[] { t }));
            Recording a2 = Rec("a2", "P1", 100, 200, pid: 1, guid: G1);
            a2.TreeId = "T";
            t.Recordings["a2"] = a2;
            VesselIdentity.Resolution res = Resolve(t);
            VesselNameStore.ApplyTo(res, "test");
            VesselNameDecision d = VesselNameStore.FindByGuid(G1);
            Assert.Contains("a2", d.CoveredLegIds);
            int display = DisplayNames.Version;

            FakeVessel live = Live(G1, 1, "P1 Debris", recordingLegId: "a2");
            LiveWriteResult r = VesselNameLiveSync.ApplyLiveWriteRule(res, new[] { live.Entry }, null, "test", "test");

            Assert.Empty(live.Writes);
            Assert.Equal(1, r.UnchosenName);
            Assert.Equal(1, r.LegsUncoveredByStockName);
            Assert.Contains("a2", d.UncoveredLegIds);
            Assert.DoesNotContain("a2", d.CoveredLegIds);
            Assert.Equal("P1 Debris", a2.VesselName);
            Assert.Equal("P1", a1.VesselName);
            Assert.True(DisplayNames.Version > display);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("stock automatic name kept on pid=1 leg=a2 'P1' -> 'P1 Debris'")
                && l.Contains("open leg marked uncovered, no write"));

            // The apply pass keeps it that way, and the next live pass leaves the vessel alone.
            VesselNameStore.ApplyTo(Resolve(t), "test");
            Assert.Equal("P1 Debris", a2.VesselName);
            LiveWriteResult again = VesselNameLiveSync.ApplyLiveWriteRule(Resolve(t), new[] { live.Entry }, null, "test", "test");
            Assert.Empty(live.Writes);
            Assert.Equal(1, again.Uncovered);
        }

        [Fact]
        public void LiveWrite_AnAutomaticNameOnAClosedLeg_IsKept_AndTheLegStaysCovered()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "P1", new[] { t }));
            FakeVessel live = Live(G1, 1, "Something Automatic");
            VesselNameLiveSync.ApplyLiveWriteRule(Resolve(t), new[] { live.Entry }, null, "test", "test");
            Assert.Empty(live.Writes);
            Assert.Contains("a1", VesselNameStore.FindByGuid(G1).CoveredLegIds);
            Assert.Equal("P1", a1.VesselName);
            Assert.Contains(logLines, l => l.Contains("leg closed, coverage kept, no write"));
        }

        // After a load the live vessel carries the quicksave's name, which is a prior name: stale.
        [Fact]
        public void LiveWrite_AfterALoad_APriorName_IsStillWritten()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Relay-1", new[] { t }));
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Relay-2", new[] { t }));
            FakeVessel quicksaveName = Live(G1, 1, "Relay-1", recordingLegId: "a1");
            FakeVessel launchName = Live(G1, 1, "Mission1");
            VesselNameLiveSync.ApplyLiveWriteRule(Resolve(t), new[] { quicksaveName.Entry, launchName.Entry },
                null, "post-load", "test");
            Assert.Equal(new[] { "Relay-2" }, quicksaveName.Writes.ToArray());
            Assert.Equal(new[] { "Relay-2" }, launchName.Writes.ToArray());
            Assert.Contains("a1", VesselNameStore.FindByGuid(G1).CoveredLegIds);
        }

        // A stock craft name is a raw #autoLOC tag; the leg captured it localized.
        [Fact]
        public void LiveWrite_ALocalizedTagEqualToAPriorName_IsStillWritten()
        {
            Recording a1 = Rec("a1", "Kerbal X", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "Named X", new[] { t }));
            VesselNameLiveSync.LocalizeForTesting = n => n == "#autoLOC_501224" ? "Kerbal X" : n;
            FakeVessel live = Live(G1, 1, "#autoLOC_501224", recordingLegId: "a1");
            LiveWriteResult r = VesselNameLiveSync.ApplyLiveWriteRule(Resolve(t), new[] { live.Entry }, null, "test", "test");
            Assert.Equal(new[] { "Named X" }, live.Writes.ToArray());
            Assert.Equal(0, r.UnchosenName);
        }

        [Fact]
        public void CurrentLeg_IsTheNewestLegCarryingTheLiveGuid_SkippingSupersededLegs()
        {
            Recording old = Rec("old", "S", 0, 10, pid: 1, guid: G1, chain: "c", chainIndex: 0);
            Recording newer = Rec("newer", "S", 10, 20, pid: 1, guid: G1, chain: "c", chainIndex: 1);
            Recording rekeyed = Rec("rekeyed", "S", 30, 40, pid: 2, guid: G2, chain: "c", chainIndex: 2);
            RecordingTree t = Tree("T", old, newer, rekeyed);
            VesselIdentity.Resolution res = Resolve(t);
            VesselIdentity.Identity id = res.ByRecordingId["old"];

            Assert.Equal("newer", VesselNameLiveSync.ResolveCurrentLegId(id, res, G1, null, null));
            Assert.Equal("rekeyed", VesselNameLiveSync.ResolveCurrentLegId(id, res, G2, null, null));
            Assert.Equal("old", VesselNameLiveSync.ResolveCurrentLegId(id, res, G1, null, leg => leg == "newer"));
            // Only superseded legs carry the guid: still the newest of them.
            Assert.Equal("newer", VesselNameLiveSync.ResolveCurrentLegId(id, res, G1, null, leg => true));
            Assert.Equal("old", VesselNameLiveSync.ResolveCurrentLegId(id, res, G1, "old", null));
            Assert.Null(VesselNameLiveSync.ResolveCurrentLegId(id, res, null, null, null));
        }

        [Fact]
        public void RecordingLeg_IsTheActiveLegForTheRecordedVessel_ElseItsBackgroundLeg()
        {
            RecordingTree t = Tree("T", Rec("act", "A", 0, 10, pid: 1, guid: G1), Rec("bg", "B", 0, 10, pid: 2, guid: G2));
            t.ActiveRecordingId = "act";
            t.BackgroundMap[2] = "bg";
            Assert.Equal("act", VesselNameLiveSync.ResolveRecordingLegId(t, true, 1, 1));
            Assert.Null(VesselNameLiveSync.ResolveRecordingLegId(t, false, 1, 1));
            Assert.Null(VesselNameLiveSync.ResolveRecordingLegId(t, true, 5, 1));
            Assert.Equal("bg", VesselNameLiveSync.ResolveRecordingLegId(t, true, 1, 2));
            Assert.Null(VesselNameLiveSync.ResolveRecordingLegId(null, true, 1, 1));
            Assert.Null(VesselNameLiveSync.ResolveRecordingLegId(t, true, 1, 0));
        }

        // ---------------------------------------------------------------- Parsek rename (7.2)

        [Fact]
        public void ParsekRename_WritesThroughToTheIdentitysLiveVesselOnly()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            Recording b = Rec("b", "Other", 0, 10, pid: 1, guid: G2);
            RecordingTree t = Tree("T", a);
            RecordingTree u = Tree("U", b);
            FakeVessel mine = Live(G1, 1, "Renamed In Stock Before This Build");
            FakeVessel other = Live(G2, 1, "Other");
            FakeVessel ghost = Live(G1, 1, "Ghost: Ship", ghost: true);
            FakeVessel pidOnly = Live(null, 1, "Ship");
            FakeVessel stranger = Live(G9, 9, "Ship");
            VesselNameLiveSync.LiveVesselsProviderForTesting = _ => new List<LiveVesselEntry>
            {
                mine.Entry, other.Entry, ghost.Entry, pidOnly.Entry, stranger.Entry,
            };

            Assert.True(VesselNameStore.RecordParsekRename(a, "Renamed", new[] { t, u }, out _));

            Assert.Equal(new[] { "Renamed" }, mine.Writes.ToArray());
            Assert.Empty(other.Writes);
            Assert.Empty(ghost.Writes);
            Assert.Empty(pidOnly.Writes);
            Assert.Empty(stranger.Writes);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][VesselNames]")
                && l.Contains("Live write pass (parsek-rename) scope=test:Auto: scanned=5 written=1")
                && l.Contains("noIdentity=2") && l.Contains("noDecision=1") && l.Contains("skippedGhost=1"));
        }

        [Fact]
        public void ParsekRename_WithoutALiveProvider_IsHeadlessInATestUniverse()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            Assert.True(VesselNameStore.RecordParsekRename(a, "Renamed", new[] { t }, out _));
            Assert.DoesNotContain(logLines, l => l.Contains("Live write pass (parsek-rename)"));
        }

        // ---------------------------------------------------------------- epochs and the undo (7.6)

        [Fact]
        public void Epoch_BumpsOnlyWhenTheRevertTargetChanges()
        {
            object a = new object(), b = new object();
            Assert.True(VesselNameStore.ShouldBumpFlightEpoch(null, a));
            Assert.False(VesselNameStore.ShouldBumpFlightEpoch(a, a));
            Assert.False(VesselNameStore.ShouldBumpFlightEpoch(a, null));

            int start = VesselNameStore.FlightEpoch;
            Assert.True(VesselNameStore.NoteFlightReady(a));
            Assert.False(VesselNameStore.NoteFlightReady(a));
            Assert.False(VesselNameStore.NoteFlightReady(null));
            Assert.True(VesselNameStore.NoteFlightReady(b));
            Assert.Equal(start + 2, VesselNameStore.FlightEpoch);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Flight epoch -> " + (start + 2)) && l.Contains("reason=flight-ready:new-revert-target"));
        }

        [Fact]
        public void Epoch_BumpDropsTheOlderEpochsUndoEntries()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            Assert.True(StockAccept(t, G1, 1, "Ship", "Old Flight", out _, out _));
            Assert.Single(VesselNameStore.UndoEntries);
            VesselNameStore.NoteFlightReady(new object()); // a stock flight entry, no Re-Fly attempt
            Assert.Empty(VesselNameStore.UndoEntries);

            // A revert now cancels only the new epoch: the earlier flight's rename stays.
            VesselNameStore.UndoResult r = VesselNameStore.UndoCurrentFlightEpoch("test");
            Assert.Equal(0, r.Undone);
            Assert.Equal("Old Flight", VesselNameStore.FindByGuid(G1).Name);
        }

        // The design's example: TS rename "Relay-2", in-flight stock rename "Relay-X", revert.
        [Fact]
        public void Revert_UndoesTheFlightsStockRename_RestoringTheReplacedDecisionAndItsCoverage()
        {
            Recording b1 = Rec("b1", "P2", 0, 100, pid: 2, guid: G2);
            RecordingTree t = Tree("T", b1);
            UseUniverse(t);
            Assert.True(StockAccept(t, G2, 2, "P2", "Relay-2", out VesselNameDecision relay2, out _));
            var covered = new HashSet<string>(relay2.CoveredLegIds);
            var priors = new List<string>(relay2.PriorNames);

            VesselNameStore.NoteFlightReady(new object()); // the launch
            Assert.True(StockAccept(t, G2, 2, "Relay-2", "Relay-X", out _, out _));
            Assert.Equal("Relay-X", b1.VesselName);

            VesselNameStore.UndoResult r = VesselNameStore.UndoCurrentFlightEpoch("onload:StockRevert");

            Assert.Equal(1, r.Undone);
            VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal("Relay-2", d.Name);
            Assert.Equal(relay2.Seq, d.Seq);
            Assert.True(d.CoveredLegIds.SetEquals(covered));
            Assert.Equal(priors, d.PriorNames);
            Assert.Same(d, VesselNameStore.FindByGuid(G2));
            Assert.Equal("Relay-2", b1.VesselName);
            Assert.Empty(VesselNameStore.UndoEntries);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Revert undo: dropped StockDialog decision") && l.Contains("'Relay-X', restored seq=1 'Relay-2'"));
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Revert undo (onload:StockRevert): epoch=") && l.Contains("undone=1"));
        }

        [Fact]
        public void Revert_StockRenameWithNoEarlierDecision_GivesTheLegsTheirCapturedNamesBack()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            Recording a2 = Rec("a2", "Mission1", 100, 200, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1, a2);
            UseUniverse(t);
            VesselNameStore.NoteFlightReady(new object());
            Assert.True(StockAccept(t, G1, 1, "Mission1", "P1", out _, out _));
            Assert.Equal("P1", a2.VesselName);

            VesselNameStore.UndoResult r = VesselNameStore.UndoCurrentFlightEpoch("onload:StockRevert");

            Assert.Equal(1, r.Undone);
            Assert.Equal(2, r.LegsRestored);
            Assert.Empty(VesselNameStore.Decisions);
            Assert.Null(VesselNameStore.FindByGuid(G1));
            Assert.Equal("Mission1", a1.VesselName);
            Assert.Equal("Mission1", a2.VesselName);
        }

        [Fact]
        public void Revert_AParsekRenameMadeInTheRevertedFlight_Survives()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            VesselNameStore.NoteFlightReady(new object());
            Assert.True(StockAccept(t, G1, 1, "Ship", "Stock-X", out _, out _));
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "Parsek-Y", new[] { t }));

            VesselNameStore.UndoResult r = VesselNameStore.UndoCurrentFlightEpoch("onload:StockRevert");

            Assert.Equal(0, r.Undone);
            Assert.Equal(1, r.KeptParsekRename);
            Assert.Equal(1, r.KeptReplacedLater);
            Assert.Equal("Parsek-Y", VesselNameStore.FindByGuid(G1).Name);
            Assert.Equal("Parsek-Y", a.VesselName);
        }

        [Fact]
        public void Revert_AStockRenameAfterAParsekRename_GoesBackToTheParsekName()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            VesselNameStore.NoteFlightReady(new object());
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "Parsek-Y", new[] { t }));
            Assert.True(StockAccept(t, G1, 1, "Parsek-Y", "Stock-X", out _, out _));

            VesselNameStore.UndoCurrentFlightEpoch("onload:StockRevert");

            VesselNameDecision d = Assert.Single(VesselNameStore.Decisions);
            Assert.Equal("Parsek-Y", d.Name);
            Assert.Equal(VesselNameSource.ParsekRename, d.Source);
            Assert.Equal("Parsek-Y", a.VesselName);
        }

        [Fact]
        public void Revert_TwoStockRenamesInOneFlight_UndoNewestFirst()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "Before", new[] { t }));
            VesselNameStore.NoteFlightReady(new object());
            Assert.True(StockAccept(t, G1, 1, "Before", "X1", out _, out _));
            Assert.True(StockAccept(t, G1, 1, "X1", "X2", out _, out _));

            VesselNameStore.UndoResult r = VesselNameStore.UndoCurrentFlightEpoch("onload:StockRevert");

            Assert.Equal(2, r.Undone);
            Assert.Equal("Before", Assert.Single(VesselNameStore.Decisions).Name);
            Assert.Equal("Before", a.VesselName);
        }

        [Fact]
        public void DiscardReFly_UndoesTheAttemptsStockRename()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "Kept", new[] { t }));
            int before = VesselNameStore.FlightEpoch;
            VesselNameLiveSync.OnReFlyStripComplete("sess_vn", 0f); // the attempt starts
            Assert.Equal(before + 1, VesselNameStore.FlightEpoch);
            Assert.True(StockAccept(t, G1, 1, "Kept", "Attempt Name", out _, out _));

            Assert.Equal(LoadReconcileAction.MemoryUndoRevertedFlight,
                LoadReconcilePolicy.Decide(LoadKind.DiscardReFly, LoadStateCategory.VesselNames).Action);
            VesselNameStore.UndoCurrentFlightEpoch("onload:DiscardReFly");

            Assert.Equal("Kept", VesselNameStore.FindByGuid(G1).Name);
            Assert.Equal("Kept", a.VesselName);
            Assert.Contains(logLines, l => l.Contains("Flight epoch -> " + (before + 1)) && l.Contains("refly-attempt-start sess=sess_vn"));
        }

        [Fact]
        public void Retry_UndoesTheAttemptsStockRename_BeforeReinvoking()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            VesselNameLiveSync.OnReFlyStripComplete("sess_retry", 0f);
            Assert.True(StockAccept(t, G1, 1, "Ship", "Attempt Name", out _, out _));

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_retry", TreeId = "tree_retry", ActiveReFlyRecordingId = "rec_prov",
                OriginChildRecordingId = "rec_origin", RewindPointId = "rp_retry", InvokedUT = 42.0,
                InvokedRealTime = "2026-10-11T00:00:00.000Z",
            };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_retry", BranchPointId = "bp_retry", UT = 0.0, QuicksaveFilename = "rp_retry.sfs",
                SessionProvisional = true, CreatingSessionId = "sess_retry",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_origin", Controllable = true },
                },
            };
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { rp },
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = marker,
            });
            bool invoked = false;
            string nameAtInvoke = "<not invoked>";
            RevertInterceptor.RewindInvokeStartForTesting = (r, s) =>
            {
                invoked = true;
                nameAtInvoke = VesselNameStore.FindByGuid(G1)?.Name;
            };

            RevertInterceptor.RetryHandler(marker);

            Assert.True(invoked);
            Assert.Null(nameAtInvoke);
            Assert.Empty(VesselNameStore.Decisions);
            Assert.Equal("Ship", a.VesselName);
            Assert.Contains(logLines, l => l.Contains("Revert undo (refly-retry sess=sess_retry)") && l.Contains("undone=1"));
            // Its load classifies ReFlyStart, which keeps memory (a fresh invoke cancels nothing).
            Assert.Equal(LoadReconcileAction.Memory,
                LoadReconcilePolicy.Decide(LoadKind.ReFlyStart, LoadStateCategory.VesselNames).Action);
        }

        [Fact]
        public void Undo_ARestoredDecisionLeavesAGuidAnotherDecisionNowHolds()
        {
            // h and k one vessel (chain) under "Twin"; apart, the stock rename takes h's guid
            // (Twin keeps k's). A later Parsek rename of k replaces Twin for k. Undoing the stock
            // rename restores Twin for h's guid but never takes k's guid back.
            Recording h = Rec("h", "Ship", 0, 10, pid: 1, guid: G1, chain: "c", chainIndex: 0);
            Recording k = Rec("k", "Ship", 10, 20, pid: 2, guid: G2, chain: "c", chainIndex: 1);
            RecordingTree t = Tree("T", h, k);
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(h, "Twin", new[] { t }));
            k.ChainId = null;
            k.ChainIndex = -1;
            VesselNameStore.NoteFlightReady(new object());
            Assert.True(StockAccept(t, G1, 1, "Twin", "Solo", out _, out _));
            Assert.True(MissionVesselNaming.ApplyRecordingRename(k, "Kay", new[] { t }));

            VesselNameStore.UndoCurrentFlightEpoch("onload:StockRevert");

            Assert.Equal("Twin", VesselNameStore.FindByGuid(G1).Name);
            Assert.Equal("Kay", VesselNameStore.FindByGuid(G2).Name);
            Assert.Equal(new[] { G1 }, VesselNameStore.FindByGuid(G1).Guids.ToArray());
            Assert.Equal("Twin", h.VesselName);
            Assert.Equal("Kay", k.VesselName);
        }

        // ---------------------------------------------------------------- review probes

        // catches: a Parsek rename that never reaches a live vessel whose name no leg shows (a
        // stock rename before this build): the live name read as a name nobody chose.
        [Fact]
        public void ParsekRename_ClosedLeg_LiveNameNoLegShows_IsWrittenThrough()
        {
            Recording a = Rec("a", "A", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            FakeVessel live = Live(G1, 1, "B");
            VesselNameLiveSync.LiveVesselsProviderForTesting = _ => new List<LiveVesselEntry> { live.Entry };

            Assert.True(VesselNameStore.RecordParsekRename(a, "C", new[] { t }, out _));

            Assert.Equal(new[] { "C" }, live.Writes.ToArray());
            Assert.Contains("B", VesselNameStore.FindByGuid(G1).PriorNames);
            Assert.Equal("C", a.VesselName);
        }

        // catches: the open-leg branch flipping the renamed leg back to the live name at once.
        [Fact]
        public void ParsekRename_OpenLeg_LiveNameNoLegShows_IsWrittenAndTheLegKeepsTheRename()
        {
            Recording a = Rec("a", "A", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            FakeVessel live = Live(G1, 1, "B", recordingLegId: "a");
            VesselNameLiveSync.LiveVesselsProviderForTesting = _ => new List<LiveVesselEntry> { live.Entry };

            Assert.True(VesselNameStore.RecordParsekRename(a, "C", new[] { t }, out _));

            Assert.Equal(new[] { "C" }, live.Writes.ToArray());
            Assert.Equal("C", a.VesselName);
            Assert.Contains("a", VesselNameStore.FindByGuid(G1).CoveredLegIds);
        }

        private static ReFlySessionMarker Marker(string sessionId)
            => new ReFlySessionMarker
            {
                SessionId = sessionId, TreeId = "tree_vn", ActiveReFlyRecordingId = "rec_prov",
                OriginChildRecordingId = "rec_origin", RewindPointId = "rp_vn", InvokedUT = 42.0,
                InvokedRealTime = "2026-10-11T00:00:00.000Z",
            };

        private static void InstallScenario(ReFlySessionMarker marker, RewindPoint rp = null)
        {
            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                RewindPoints = rp != null ? new List<RewindPoint> { rp } : new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = marker,
            });
        }

        // catches: F9 inside a Re-Fly attempt (stock rebuilds PostInitState on every flight
        // start) bumping the epoch and dropping the attempt's undo entries (ruling 8).
        [Fact]
        public void ReFlyAttempt_F9InsideTheAttempt_DiscardStillUndoesItsStockRename()
        {
            Recording a = Rec("a", "A", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            InstallScenario(Marker("sess"));
            VesselNameLiveSync.OnReFlyStripComplete("sess", 0f);
            int attemptEpoch = VesselNameStore.FlightEpoch;
            Assert.False(VesselNameStore.NoteFlightReady(new object())); // the attempt's own flight ready
            Assert.True(StockAccept(t, G1, 1, "A", "Z", out _, out _));
            Assert.False(VesselNameStore.NoteFlightReady(new object())); // F9 inside the attempt
            Assert.Equal(attemptEpoch, VesselNameStore.FlightEpoch);
            Assert.Single(VesselNameStore.UndoEntries);

            VesselNameStore.UndoResult r = VesselNameStore.UndoCurrentFlightEpoch("onload:DiscardReFly");

            Assert.Equal(1, r.Undone);
            Assert.Null(VesselNameStore.FindByGuid(G1));
            Assert.Equal("A", a.VesselName);
            Assert.Null(VesselNameStore.ReFlyEpochSessionId);
            Assert.Contains(logLines, l => l.Contains("on flight ready: inside Re-Fly attempt sess=sess"));
        }

        [Fact]
        public void ReFlyAttempt_TrackingStationRoundTrip_RetryUndoesBothStockRenames()
        {
            Recording a = Rec("a", "A", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            ReFlySessionMarker marker = Marker("sess_rt");
            var rp = new RewindPoint
            {
                RewindPointId = "rp_vn", BranchPointId = "bp_rt", UT = 0.0, QuicksaveFilename = "rp_vn.sfs",
                SessionProvisional = true, CreatingSessionId = "sess_rt",
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_origin", Controllable = true },
                },
            };
            InstallScenario(marker, rp);
            VesselNameLiveSync.OnReFlyStripComplete("sess_rt", 0f);
            VesselNameStore.NoteFlightReady(new object());
            Assert.True(StockAccept(t, G1, 1, "A", "In Flight", out _, out _));
            Assert.True(StockAccept(t, G1, 1, "In Flight", "In Tracking Station", out _, out _));
            VesselNameStore.NoteFlightReady(new object()); // back to flight from the Tracking Station
            bool invoked = false;
            RevertInterceptor.RewindInvokeStartForTesting = (r, s) => invoked = true;

            RevertInterceptor.RetryHandler(marker);

            Assert.True(invoked);
            Assert.Null(VesselNameStore.FindByGuid(G1));
            Assert.Equal("A", a.VesselName);
        }

        [Fact]
        public void ReFlyAttempt_EndedByAMerge_TheNextFlightEntryStartsANewEpoch()
        {
            InstallScenario(Marker("sess_m"));
            VesselNameLiveSync.OnReFlyStripComplete("sess_m", 0f);
            int attemptEpoch = VesselNameStore.FlightEpoch;
            Assert.False(VesselNameStore.NoteFlightReady(new object()));
            ParsekScenario.Instance.ActiveReFlySessionMarker = null; // merged
            Assert.True(VesselNameStore.NoteFlightReady(new object()));
            Assert.Equal(attemptEpoch + 1, VesselNameStore.FlightEpoch);
            Assert.Null(VesselNameStore.ReFlyEpochSessionId);
        }

        // The staging race with NO pass while the post-staging leg was open: the leg-close name
        // check uncovers it from the recorded vessel's live name at close.
        [Fact]
        public void LegClose_AnAutomaticNameTakenWhileOpen_UncoversTheLeg_WithNoPassInBetween()
        {
            Recording a1 = Rec("a1", "Mission1", 0, 100, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a1);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a1, "P1", new[] { t }));
            Recording a2 = Rec("a2", "P1", 100, 200, pid: 1, guid: G1);
            a2.TreeId = "T";
            t.Recordings["a2"] = a2;
            VesselIdentity.Resolution res = Resolve(t);
            VesselNameStore.ApplyTo(res, "test");
            VesselNameDecision d = VesselNameStore.FindByGuid(G1);
            Assert.Contains("a2", d.CoveredLegIds);

            // Stale names at close change nothing.
            Assert.False(VesselNameLiveSync.CheckClosingLegName(res, "a2", "P1", null, "test"));
            Assert.False(VesselNameLiveSync.CheckClosingLegName(res, "a2", "Mission1", null, "test"));
            Assert.Contains("a2", d.CoveredLegIds);

            Assert.True(VesselNameLiveSync.CheckClosingLegName(res, "a2", "P1 Debris", null, "leg-close:active"));

            Assert.Contains("a2", d.UncoveredLegIds);
            Assert.Equal("P1 Debris", a2.VesselName);
            VesselNameStore.ApplyTo(Resolve(t), "test");
            Assert.Equal("P1 Debris", a2.VesselName);
            Assert.Equal("P1", a1.VesselName);
            Assert.Contains(logLines, l => l.Contains("[INFO][VesselNames]")
                && l.Contains("Leg close: stock automatic name kept on leg=a2 'P1' -> 'P1 Debris'"));
            // An already uncovered leg is left alone.
            Assert.False(VesselNameLiveSync.CheckClosingLegName(res, "a2", "Other", null, "test"));
        }

        [Fact]
        public void Wiring_LegCloseNameCheckRunsWhereTreeLegsClose()
        {
            string append = MethodBody("ParsekFlight.cs",
                "internal static void AppendCapturedDataToRecording(Recording target, Recording source, double endUT)");
            IndexOrFail(append, "VesselNameLiveSync.NoteLegClosing(target");
            string close = MethodBody("BackgroundRecorder.cs", "private void CloseParentRecording(");
            IndexOrFail(close, "VesselNameLiveSync.NoteLegClosing(parentRec");
        }

        // ---------------------------------------------------------------- post-load pass plumbing

        [Fact]
        public void FlightReady_ArmsThePostLoadPass_UnlessTheStripPassJustRan()
        {
            VesselNameLiveSync.OnFlightReady(new object(), 100f);
            Assert.True(VesselNameLiveSync.FlightReadyPassArmedForTesting);

            VesselNameLiveSync.OnReFlyStripComplete("s", 200f);
            Assert.False(VesselNameLiveSync.FlightReadyPassArmedForTesting);
            VesselNameLiveSync.OnFlightReady(new object(), 201f);
            Assert.False(VesselNameLiveSync.FlightReadyPassArmedForTesting);
            Assert.Contains(logLines, l => l.Contains("Post-load live write pass not armed: the Re-Fly strip pass already ran"));

            VesselNameLiveSync.OnFlightReady(new object(), 400f);
            Assert.True(VesselNameLiveSync.FlightReadyPassArmedForTesting);
        }

        [Fact]
        public void FlightHostTick_WaitsForAPendingReFlyStrip_ThenRunsOnce()
        {
            Recording a = Rec("a", "Ship", 0, 10, pid: 1, guid: G1);
            RecordingTree t = Tree("T", a);
            UseUniverse(t);
            Assert.True(MissionVesselNaming.ApplyRecordingRename(a, "Named", new[] { t }));
            FakeVessel live = Live(G1, 1, "Ship");
            VesselNameLiveSync.LiveVesselsProviderForTesting = _ => new List<LiveVesselEntry> { live.Entry };
            VesselNameLiveSync.OnFlightReady(new object(), 10f);
            bool pending = RewindInvokeContext.Pending;
            try
            {
                RewindInvokeContext.Pending = true;
                VesselNameLiveSync.FlightHostTick(11f);
                Assert.Empty(live.Writes);
                RewindInvokeContext.Pending = false;
                VesselNameLiveSync.FlightHostTick(12f);
                VesselNameLiveSync.FlightHostTick(13f);
            }
            finally
            {
                RewindInvokeContext.Pending = pending;
            }
            Assert.Equal(new[] { "Named" }, live.Writes.ToArray());
            Assert.Contains(logLines, l => l.Contains("Post-load live write pass waiting: a Re-Fly strip is pending"));
            Assert.Contains(logLines, l => l.Contains("Live write pass (post-load:flight-ready) scope=test:FlightVessels: scanned=1 written=1"));
        }

        // ---------------------------------------------------------------- policy

        [Fact]
        public void Policy_VesselNames_Cells()
        {
            Assert.Equal(LoadReconcileAction.Save, LoadReconcilePolicy.Decide(LoadKind.Cold, LoadStateCategory.VesselNames).Action);
            Assert.Equal(LoadReconcileAction.MemoryUndoRevertedFlight,
                LoadReconcilePolicy.Decide(LoadKind.StockRevert, LoadStateCategory.VesselNames).Action);
            Assert.Equal(LoadReconcileAction.MemoryUndoRevertedFlight,
                LoadReconcilePolicy.Decide(LoadKind.DiscardReFly, LoadStateCategory.VesselNames).Action);
            foreach (LoadKind kind in new[] { LoadKind.PlainRewind, LoadKind.ReFlyStart, LoadKind.QuickloadFlight, LoadKind.InSessionOther })
                Assert.Equal(LoadReconcileAction.Memory, LoadReconcilePolicy.Decide(kind, LoadStateCategory.VesselNames).Action);
            Assert.Contains("RetryHandler", LoadReconcilePolicy.Decide(LoadKind.ReFlyStart, LoadStateCategory.VesselNames).Reason);
            // The undo action belongs to this category alone.
            foreach (LoadKind kind in Enum.GetValues(typeof(LoadKind)).Cast<LoadKind>())
                foreach (LoadStateCategory cat in Enum.GetValues(typeof(LoadStateCategory)).Cast<LoadStateCategory>())
                    if (cat != LoadStateCategory.VesselNames)
                        Assert.NotEqual(LoadReconcileAction.MemoryUndoRevertedFlight, LoadReconcilePolicy.Decide(kind, cat).Action);
        }

        // ---------------------------------------------------------------- Harmony targets

        [Fact]
        public void HarmonyTargets_ResolveToTheDecompiledPrivateSignatures()
        {
            var vesselAccept = (MethodInfo)VesselRenameAcceptCapturePatch.ResolveTargetMethodForTesting();
            Assert.NotNull(vesselAccept);
            Assert.True(vesselAccept.IsPrivate);
            Assert.Equal(new[] { typeof(string), typeof(VesselType) },
                vesselAccept.GetParameters().Select(p => p.ParameterType).ToArray());

            var partAccept = (MethodInfo)PartVesselNamingAcceptCapturePatch.ResolveTargetMethodForTesting();
            Assert.NotNull(partAccept);
            Assert.True(partAccept.IsPrivate);
            Assert.Equal(new[] { typeof(string), typeof(VesselType), typeof(int) },
                partAccept.GetParameters().Select(p => p.ParameterType).ToArray());
        }

        // ---------------------------------------------------------------- wiring (source scans)

        [Fact]
        public void Wiring_OnLoadUndoesAfterTheRevertDetectorAndBeforeTheTailReapply()
        {
            string body = MethodBody("ParsekScenario.cs", "public override void OnLoad(ConfigNode node)");
            int refined = IndexOrFail(body, "LoadReconcilePolicy.ClassifyRefined(");
            int undo = IndexOrFail(body, "VesselNameStore.UndoCurrentFlightEpoch(");
            Assert.Equal(1, Occurrences(body, "VesselNameStore.UndoCurrentFlightEpoch("));
            Assert.True(refined < undo, "the undo reads the refined kind, so it follows ClassifyRefined");
            Assert.True(body.IndexOf("VesselNameStore.ApplyAll(", undo, StringComparison.Ordinal) > undo,
                "the OnLoad tail re-applies after the undo");
            Assert.Contains("LoadReconcilePolicy.Decide(refinedLoadKind, LoadStateCategory.VesselNames).Action "
                + "== LoadReconcileAction.MemoryUndoRevertedFlight", Collapse(body));
            // Every OnLoad tail re-applies over the save's protos only (Vessel objects do not exist yet).
            Assert.Equal(Occurrences(body, "VesselNameStore.ApplyAll("),
                Occurrences(body, "VesselNameLiveScope.ProtosOnly"));
            Assert.True(Occurrences(body, "VesselNameStore.ApplyAll(") >= 3, "cold, rewind and in-session tails");
        }

        [Fact]
        public void Wiring_RetryUndoesBeforeReinvoking_StripAndFlightReadyRunThePass()
        {
            string retry = MethodBody("RevertInterceptor.cs", "internal static void RetryHandler(");
            int future = IndexOrFail(retry, "RewindInvoker.IsRewindPointInFutureNow(");
            int undo = IndexOrFail(retry, "VesselNameStore.UndoCurrentFlightEpoch(");
            int invoke = IndexOrFail(retry, "RewindInvoker.StartInvoke(");
            Assert.True(future < undo && undo < invoke,
                "Retry undoes after its last refusal and before it re-invokes the Re-Fly");

            string strip = MethodBody("RewindInvoker.cs", "private static void RunStripActivateMarker(");
            int marker = IndexOrFail(strip, "AtomicMarkerWrite(");
            int pass = IndexOrFail(strip, "VesselNameLiveSync.OnReFlyStripComplete(");
            Assert.True(marker < pass, "the post-strip pass follows the strip and the marker write");

            string ready = MethodBody("ParsekFlight.cs", "void OnFlightReady()");
            int hook = IndexOrFail(ready, "VesselNameLiveSync.OnFlightReady(FlightDriver.PostInitState");
            Assert.True(hook < ready.IndexOf("return;", StringComparison.Ordinal),
                "the epoch / pass hook runs before any early return");
            string update = MethodBody("ParsekFlight.cs", "void Update()");
            IndexOrFail(update, "VesselNameLiveSync.FlightHostTick(");
        }

        [Fact]
        public void Wiring_ParsekNeverListensToOnVesselRename()
        {
            // Stock fires onVesselRename inside the accept bodies, from part-naming automation and
            // as a from == to ping: it is not a player signal, and the write-through fires it.
            // In-game test code (InGameTests/) may watch the event to count fires.
            int handlers = 0;
            string inGameTests = Path.Combine(ParsekSourceRoot(), "InGameTests") + Path.DirectorySeparatorChar;
            foreach (string path in Directory.GetFiles(ParsekSourceRoot(), "*.cs", SearchOption.AllDirectories))
            {
                if (path.StartsWith(inGameTests, StringComparison.OrdinalIgnoreCase)) continue;
                string text = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));
                handlers += Occurrences(text, "onVesselRename.Add(");
            }
            Assert.Equal(0, handlers);
        }

        private static string MethodBody(string relPath, string signature)
        {
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(
                File.ReadAllText(Path.Combine(ParsekSourceRoot(), relPath)).Replace("\r\n", "\n"));
            int sig = prepared.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(sig >= 0, "signature not found in " + relPath + ": " + signature);
            Assert.True(prepared.IndexOf(signature, sig + 1, StringComparison.Ordinal) < 0,
                "signature ambiguous in " + relPath + ": " + signature);
            return SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sig));
        }

        private static int IndexOrFail(string body, string needle)
        {
            int idx = body.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(idx >= 0, "'" + needle + "' not found");
            return idx;
        }

        private static int Occurrences(string text, string needle)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ");

        private static string ParsekSourceRoot()
        {
            string repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string root = Path.Combine(repo, "Source", "Parsek");
            Assert.True(Directory.Exists(root), "Parsek source root not found at " + root);
            return root;
        }
    }
}
