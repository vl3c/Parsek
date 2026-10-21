using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The quickload-resume trim over committed history, driven through the production entry
    /// <c>FlightRecorder.PrepareQuickloadResumeStateIfNeeded</c>, and the untagged rows an
    /// in-session load retires after the loaded save. Phase 3 of the load design deleted the
    /// abandoned-future reconcile (owner ruling OQ-2) the resume trim used to run.
    /// </summary>
    [Collection("Sequential")]
    public class QuickloadAbandonedFutureLedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public QuickloadAbandonedFutureLedgerTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ResetAll();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            ResetAll();
            KspStatePatcher.ResetForTesting();
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.ClearPendingQuickloadResumeContext();
            ParsekScenario.ClearRestoredQuicksaveTreeFactsForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            FlightRecorder.QuickloadResumeUTProviderForTesting = null;
        }

        // ============================================================
        // Owner ruling 2026-10-19: a load back in time undoes the untagged rows made after the save
        // ============================================================

        private static void AddUntagged(string id, double ut, GameActionType type = GameActionType.FundsSpending)
        {
            Ledger.AddAction(new GameAction
            {
                ActionId = id, UT = ut, Type = type, FundsSpent = 500f,
                FundsSpendingSource = FundsSpendingSource.FacilityUpgrade,
            });
        }

        [Theory]
        [InlineData((int)LoadKind.QuickloadFlight)]
        [InlineData((int)LoadKind.InSessionOther)]
        public void UntaggedRowsAfterSave_InsideTheWindow_Retired(int kindValue)
        {
            var kind = (LoadKind)kindValue;
            Ledger.AddAction(new GameAction { ActionId = "act_seed", UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 25000f });
            AddUntagged("act_before_save", 150.0);
            AddUntagged("act_at_save", 200.0);
            AddUntagged("act_after_save", 300.0);
            AddUntagged("act_at_clock", 400.0);
            AddUntagged("act_committed_future", 500.0);
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_tagged", UT = 300.0, Type = GameActionType.FundsEarning, RecordingId = "rec_other",
            });

            int retired = ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(kind, 200.0, 400.0);

            Assert.Equal(2, retired);
            Assert.Equal(
                new[] { "act_seed", "act_before_save", "act_at_save", "act_committed_future", "act_tagged" },
                Ledger.Actions.Select(a => a.ActionId));
            Assert.Contains(logLines, l => l.Contains("[INFO][LoadPolicy]")
                && l.Contains("Untagged rows after the save retired: kind=" + kind + " saveUT=200 clockUT=400")
                && l.Contains("retired=2 seedsKept=0 keptKnown=0"));
            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("RetireAbandonedFutureActions: removed 2 action(s) reason=quickload-untagged-after-save"));
        }

        [Theory]
        [InlineData((int)LoadKind.Cold)]
        [InlineData((int)LoadKind.PlainRewind)]
        [InlineData((int)LoadKind.ReFlyStart)]
        [InlineData((int)LoadKind.DiscardReFly)]
        [InlineData((int)LoadKind.StockRevert)]
        public void UntaggedRowsAfterSave_OtherLoadKinds_Untouched(int kindValue)
        {
            var kind = (LoadKind)kindValue;
            AddUntagged("act_after_save", 300.0);

            Assert.Equal(0, ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(kind, 200.0, 400.0));

            Assert.Single(Ledger.Actions);
        }

        [Theory]
        [InlineData(200.0, 200.0)]
        [InlineData(400.0, 200.0)]
        [InlineData(double.NaN, 400.0)]
        [InlineData(200.0, double.NaN)]
        [InlineData(0.0, 400.0)]
        public void UntaggedRowsAfterSave_ForwardOrUnknownClock_Untouched(double saveUT, double clockUT)
        {
            AddUntagged("act_a", 300.0);
            AddUntagged("act_b", 200.0);

            Assert.Equal(0, ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(
                LoadKind.InSessionOther, saveUT, clockUT));

            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("Untagged rows after the save: nothing to retire kind=InSessionOther reason=no-window"));
        }

        [Fact]
        public void ShouldRetireUntaggedRowAfterSave_Pure()
        {
            Assert.True(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { UT = 300.0, Type = GameActionType.KerbalHire }, 200.0, 400.0));
            Assert.True(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { UT = 400.0, Type = GameActionType.ContractAccept }, 200.0, 400.0));
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { UT = 200.0, Type = GameActionType.KerbalHire }, 200.0, 400.0));
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { UT = 400.5, Type = GameActionType.KerbalHire }, 200.0, 400.0));
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { UT = 300.0, Type = GameActionType.KerbalHire, RecordingId = "rec" }, 200.0, 400.0));
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { UT = 300.0, Type = GameActionType.ScienceInitial }, 200.0, 400.0));
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(null, 200.0, 400.0));
            var known = new HashSet<string> { "act_known" };
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { ActionId = "act_known", UT = 300.0, Type = GameActionType.KerbalHire }, 200.0, 400.0, known));
            Assert.True(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { ActionId = "act_new", UT = 300.0, Type = GameActionType.KerbalHire }, 200.0, 400.0, known));
        }

        [Theory]
        [InlineData(500.0, double.NaN, 200.0, 500.0)]
        [InlineData(-1.0, 1000.1, double.NaN, 1000.1)]
        [InlineData(-1.0, double.NaN, 1000.1, 1000.1)]
        [InlineData(-1.0, 300.0, 409.9, 409.9)]
        [InlineData(-1.0, double.NaN, double.NaN, double.NaN)]
        [InlineData(0.0, 0.0, double.PositiveInfinity, double.NaN)]
        public void ResolvePreLoadClockUT_TakesTheLatestUsableReading(
            double preChangeUT, double sceneRequestUT, double planetariumUT, double expected)
        {
            double actual = ParsekScenario.ResolvePreLoadClockUT(preChangeUT, sceneRequestUT, planetariumUT);
            if (double.IsNaN(expected))
                Assert.True(double.IsNaN(actual));
            else
                Assert.Equal(expected, actual);
        }

        // ---- the exact list: rows that already existed when the save was written ----

        private static ConfigNode SaveNodeAt(double saveClockUT)
        {
            var node = new ConfigNode("SCENARIO");
            ParsekScenario.WriteUntaggedFutureRowsNode(node, saveClockUT, Ledger.Actions);
            return node;
        }

        [Fact]
        public void UntaggedRowsAfterSave_ExactList_RewindLeftRowKnownAtTheSaveSurvives()
        {
            // D2: a rewind left a Space Center row at 500 as committed future. The player
            // quicksaves at 300, plays on to 600 (the row is now in the past), then F9s.
            AddUntagged("act_rewind_left", 500.0);
            ConfigNode save = SaveNodeAt(300.0);

            var known = ParsekScenario.ReadUntaggedFutureRowIds(save, 300.0, out string mode);
            Assert.Equal("exact", mode);
            int retired = ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(
                LoadKind.QuickloadFlight, 300.0, 600.0, known, mode);

            Assert.Equal(0, retired);
            Assert.Equal("act_rewind_left", Assert.Single(Ledger.Actions).ActionId);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("Untagged rows after the save: nothing to retire kind=QuickloadFlight reason=empty")
                && l.Contains("keptKnown=1 mode=exact"));
        }

        [Fact]
        public void UntaggedRowsAfterSave_ExactList_PurchaseAfterTheSaveStillRetired()
        {
            AddUntagged("act_rewind_left", 500.0);
            AddUntagged("act_before_save", 250.0);
            ConfigNode save = SaveNodeAt(300.0);
            // After the save: a purchase at 400, a hire at 550 (inside the rewind-left row's span).
            AddUntagged("act_purchase", 400.0);
            AddUntagged("act_hire", 550.0, GameActionType.KerbalHire);

            var known = ParsekScenario.ReadUntaggedFutureRowIds(save, 300.0, out string mode);
            int retired = ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(
                LoadKind.InSessionOther, 300.0, 600.0, known, mode);

            Assert.Equal(2, retired);
            Assert.Equal(new[] { "act_rewind_left", "act_before_save" }, Ledger.Actions.Select(a => a.ActionId));
            Assert.Contains(logLines, l => l.Contains("[INFO][LoadPolicy]")
                && l.Contains("Untagged rows after the save retired: kind=InSessionOther saveUT=300 clockUT=600")
                && l.Contains("retired=2 seedsKept=0 keptKnown=1 mode=exact"));
        }

        [Fact]
        public void UntaggedRowsAfterSave_OldSaveWithoutTheList_FallsBackToTheWindowRule()
        {
            AddUntagged("act_rewind_left", 500.0);
            AddUntagged("act_purchase", 400.0);
            var oldSave = new ConfigNode("SCENARIO");

            var known = ParsekScenario.ReadUntaggedFutureRowIds(oldSave, 300.0, out string mode);
            Assert.Null(known);
            Assert.Equal("window-legacy", mode);
            int retired = ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(
                LoadKind.QuickloadFlight, 300.0, 600.0, known, mode);

            Assert.Equal(2, retired);
            Assert.Empty(Ledger.Actions);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("Untagged rows after the save: no list in the loaded save")
                && l.Contains("mode=window-legacy"));
            Assert.Contains(logLines, l => l.Contains("[INFO][LoadPolicy]")
                && l.Contains("retired=2 seedsKept=0 keptKnown=0 mode=window-legacy"));
        }

        [Fact]
        public void UntaggedRowsAfterSave_ListFromAnotherSave_FallsBackToTheWindowRule()
        {
            AddUntagged("act_rewind_left", 500.0);
            ConfigNode save = SaveNodeAt(300.0);

            var known = ParsekScenario.ReadUntaggedFutureRowIds(save, 120.0, out string mode);

            Assert.Null(known);
            Assert.Equal("window-save-mismatch", mode);
            Assert.Contains(logLines, l => l.Contains("[INFO][LoadPolicy]")
                && l.Contains("listSaveUT=300 loadedSaveUT=120")
                && l.Contains("mode=window-save-mismatch"));
        }

        [Fact]
        public void WriteUntaggedFutureRowsNode_ListsOnlyUntaggedNonSeedRowsAfterTheSave()
        {
            Ledger.AddAction(new GameAction { ActionId = "act_seed", UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 25000f });
            AddUntagged("act_old", 100.0);
            AddUntagged("act_at_save", 300.0);
            AddUntagged("act_future_a", 500.0);
            AddUntagged("act_future_b", 700.0, GameActionType.KerbalHire);
            Ledger.AddAction(new GameAction { ActionId = "act_tagged", UT = 500.0, Type = GameActionType.FundsEarning, RecordingId = "rec" });
            Ledger.AddAction(new GameAction { ActionId = "act_late_seed", UT = 500.0, Type = GameActionType.ScienceInitial });

            var node = new ConfigNode("SCENARIO");
            node.AddNode(ParsekScenario.UntaggedFutureRowsNodeName).AddValue("id", "stale");
            ParsekScenario.WriteUntaggedFutureRowsNode(node, 300.0, Ledger.Actions);

            ConfigNode list = Assert.Single(node.GetNodes(ParsekScenario.UntaggedFutureRowsNodeName));
            Assert.Equal("300", list.GetValue("saveUT"));
            // The save's own instant is listed too (a margin of a second): every listed row
            // existed when the save was written, so listing one more never retires anything.
            Assert.Equal(new[] { "act_at_save", "act_future_a", "act_future_b" }, list.GetValues("id"));

            // No clock: every candidate row is listed and the node carries no saveUT.
            ParsekScenario.WriteUntaggedFutureRowsNode(node, double.NaN, Ledger.Actions);
            list = Assert.Single(node.GetNodes(ParsekScenario.UntaggedFutureRowsNodeName));
            Assert.False(list.HasValue("saveUT"));
            Assert.Equal(new[] { "act_old", "act_at_save", "act_future_a", "act_future_b" }, list.GetValues("id"));
            var known = ParsekScenario.ReadUntaggedFutureRowIds(node, 300.0, out string mode);
            Assert.Equal("exact", mode);
            Assert.Equal(4, known.Count);
        }

        [Fact]
        public void WriteUntaggedFutureRowsNode_EmptyListStillWritesTheNode()
        {
            AddUntagged("act_old", 100.0);
            var node = new ConfigNode("SCENARIO");

            ParsekScenario.WriteUntaggedFutureRowsNode(node, 300.0, Ledger.Actions);

            Assert.True(node.HasNode(ParsekScenario.UntaggedFutureRowsNodeName));
            var known = ParsekScenario.ReadUntaggedFutureRowIds(node, 300.0, out string mode);
            Assert.Equal("exact", mode);
            Assert.Empty(known);
        }

        [Fact]
        public void UntaggedRowsAfterSave_ActionIdsSurviveTheLedgerFileRoundTrip()
        {
            // The ids the save lists must match the ids the ledger carries after a cold load
            // read it back from its external file (including a default-generated id).
            var generated = new GameAction
            {
                UT = 500.0, Type = GameActionType.FundsSpending, FundsSpent = 500f,
                FundsSpendingSource = FundsSpendingSource.FacilityUpgrade,
            };
            Assert.False(string.IsNullOrEmpty(generated.ActionId));
            Ledger.AddAction(generated);
            AddUntagged("act_explicit", 450.0, GameActionType.KerbalHire);
            ConfigNode save = SaveNodeAt(300.0);

            string dir = Path.Combine(Path.GetTempPath(), "parsek-untagged-ids-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "ledger.pgld");
                Assert.True(Ledger.SaveToFile(path));
                Ledger.ResetForTesting();
                Assert.True(Ledger.LoadFromFile(path));

                Assert.Equal(
                    new[] { generated.ActionId, "act_explicit" }.OrderBy(x => x, StringComparer.Ordinal),
                    Ledger.Actions.Select(a => a.ActionId).OrderBy(x => x, StringComparer.Ordinal));

                // A purchase after the save; the reloaded rows the save listed survive the F9.
                AddUntagged("act_purchase", 400.0);
                var known = ParsekScenario.ReadUntaggedFutureRowIds(save, 300.0, out string mode);
                Assert.Equal(1, ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(
                    LoadKind.QuickloadFlight, 300.0, 600.0, known, mode));
                Assert.DoesNotContain(Ledger.Actions, a => a.ActionId == "act_purchase");
                Assert.Equal(2, Ledger.Actions.Count);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        // ============================================================
        // Committed history at the quicksave: if the quicksave's active tree was already
        // committed (a copy-on-write restore clone; the save-side overlap guard writes that
        // shape only after an out-of-band sidecar write, see
        // QUICKLOAD-TRIM-CUTS-COMMITTED-HISTORY-OF-A-RESUMED-CLONE), the restore detaches it
        // too, so only the quicksave can tell its history members from the abandoned future.
        // Owner ruling D2: committed history is never retired, cut or pruned.
        // ============================================================

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TryRestoreActiveTreeNode_CommittedBeforeTheQuicksave_TrimLeavesHistoryMembersWhole(
            bool quicksaveHoldsCommittedNode)
        {
            // QUICKLOAD-TRIM-CUTS-COMMITTED-HISTORY-OF-A-RESUMED-CLONE. The quicksave (F5 at 260)
            // is a copy-on-write clone of a tree committed before it, with a fresh live segment
            // as the active recording. Committed history past the cutoff: M lands at 400, P ends
            // at a branch point at 350 and its child P1 flies 350..420. With the committed tree's
            // own node in the save (what OnSave writes) or without it (the reasons from the
            // member's own node), the trim must leave all three whole.
            var committed = MakeHistoryTree("hist_trim_" + (quicksaveHoldsCommittedNode ? "node" : "facts"));
            string mId = HistoryMemberId(committed);
            AddBranchHistory(committed, out string pId, out string p1Id, out string bpId);
            CommitInMemory(committed);
            string segmentId = "live_seg_" + committed.Id;
            RecordingTree clone = CloneWithLiveSegment(committed, segmentId);

            var resumed = RestoreQuicksaveNode(
                QuicksaveNodeOf(clone, quicksaveHoldsCommittedNode ? committed : null));
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);

            logLines.Clear();
            RunResumePrep(resumed, 260.0, LoadKind.QuickloadFlight);

            Recording m = resumed.Recordings[mId];
            Assert.Equal(400.0, m.EndUT);
            Assert.Equal(400.0, m.Points[m.Points.Count - 1].ut);
            Assert.Equal(TerminalState.Landed, m.TerminalStateValue);
            Assert.True(resumed.Recordings.ContainsKey(p1Id), "the future-only history child is pruned");
            Assert.Equal(420.0, resumed.Recordings[p1Id].EndUT);
            Assert.Equal(bpId, resumed.Recordings[pId].ChildBranchPointId);
            Assert.Equal(350.0, resumed.Recordings[pId].EndUT);
            Assert.Contains(resumed.BranchPoints, bp => bp.Id == bpId);
            // The live segment is still the abandoned future's.
            Assert.True(resumed.Recordings[segmentId].EndUT <= 260.0);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("Quickload tree trim:")
                // The node case also names the root (committed in the save; it ends before the cutoff).
                && l.Contains("keptWholeHistory=" + (quicksaveHoldsCommittedNode ? "4" : "3"))
                && l.Contains("prunedFutureRecordings=0"));
        }

        [Fact]
        public void RetireAbandonedFutureActions_RemovesWhatThePredicateSelectsAndLogs()
        {
            Ledger.AddAction(new GameAction { UT = 100.0, Type = GameActionType.FundsEarning, RecordingId = "a" });
            Ledger.AddAction(new GameAction { UT = 300.0, Type = GameActionType.FundsEarning, RecordingId = "a" });
            Ledger.AddAction(new GameAction { UT = 300.0, Type = GameActionType.FundsEarning, RecordingId = "b" });
            int version = Ledger.StateVersion;

            int removed = Ledger.RetireAbandonedFutureActions(a => a.RecordingId == "a" && a.UT > 200.0, "unit");

            Assert.Equal(1, removed);
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.NotEqual(version, Ledger.StateVersion);
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("RetireAbandonedFutureActions: removed 1 action(s)")
                && l.Contains("reason=unit") && l.Contains("total=2"));

            logLines.Clear();
            version = Ledger.StateVersion;
            Assert.Equal(0, Ledger.RetireAbandonedFutureActions(a => false, "unit"));
            Assert.Equal(version, Ledger.StateVersion);
            Assert.Contains(logLines, l => l.Contains("RetireAbandonedFutureActions: nothing to retire"));
        }

        // ============================================================
        // helpers
        // ============================================================

        // A tree committed before the quicksave at 260: the root flew 100..250, member M flew
        // 120..400 and landed with Bob aboard.
        private static RecordingTree MakeHistoryTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording { RecordingId = "root_" + id, TreeId = id, VesselName = id, VesselPersistentId = 111u };
            SetPoints(root, 100.0, 180.0, 250.0);
            tree.AddOrReplaceRecording(root);
            var m = new Recording
            {
                RecordingId = "hist_" + id, TreeId = id, VesselName = id + " lander", VesselPersistentId = 222u,
                TerminalStateValue = TerminalState.Landed,
                GhostVisualSnapshot = CrewSnapshot("Bob Kerman"),
                CrewEndStates = new Dictionary<string, KerbalEndState> { { "Bob Kerman", KerbalEndState.Aboard } },
                CrewEndStatesResolved = true,
            };
            SetPoints(m, 120.0, 300.0, 400.0);
            tree.AddOrReplaceRecording(m);
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static string HistoryMemberId(RecordingTree tree) => "hist_" + tree.Id;

        // Adds P (120..350, ends at an undock branch point at 350) and its child P1 (350..420,
        // Landed) to a history tree.
        private static void AddBranchHistory(RecordingTree tree, out string pId, out string p1Id, out string bpId)
        {
            pId = "p_" + tree.Id;
            p1Id = "p1_" + tree.Id;
            bpId = "bp_" + tree.Id + "_350";
            var p = new Recording
            {
                RecordingId = pId, TreeId = tree.Id, VesselName = "P", VesselPersistentId = 555u,
                ChildBranchPointId = bpId,
            };
            SetPoints(p, 120.0, 300.0, 350.0);
            p.ExplicitEndUT = double.NaN;
            tree.AddOrReplaceRecording(p);
            var p1 = new Recording
            {
                RecordingId = p1Id, TreeId = tree.Id, VesselName = "P1", VesselPersistentId = 666u,
                ParentBranchPointId = bpId, TerminalStateValue = TerminalState.Landed,
            };
            SetPoints(p1, 350.0, 420.0);
            tree.AddOrReplaceRecording(p1);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = bpId, UT = 350.0, Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { pId },
                ChildRecordingIds = new List<string> { p1Id },
            });
            tree.RebuildBackgroundMap();
        }

        private static void CommitInMemory(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // The copy-on-write clone the player resumed, plus a fresh live segment that is the
        // active recording.
        private static RecordingTree CloneWithLiveSegment(RecordingTree committed, string segmentId)
        {
            var clone = RecordingTree.DeepClone(committed);
            var segment = new Recording
            {
                RecordingId = segmentId, TreeId = committed.Id, VesselName = "live", VesselPersistentId = 444u,
            };
            SetPoints(segment, 250.0, 260.0);
            clone.AddOrReplaceRecording(segment);
            clone.ActiveRecordingId = segmentId;
            return clone;
        }

        // A save node holding the active tree (and, when given, a committed tree node written
        // before it, the way OnSave writes committed trees first).
        private static ConfigNode QuicksaveNodeOf(RecordingTree activeTree, RecordingTree committedTree)
        {
            var node = new ConfigNode("PARSEK_SCENARIO");
            if (committedTree != null)
                committedTree.Save(node.AddNode("RECORDING_TREE"));
            var treeNode = node.AddNode("RECORDING_TREE");
            activeTree.Save(treeNode);
            treeNode.AddValue("isActive", "True");
            return node;
        }

        private static RecordingTree RestoreQuicksaveNode(ConfigNode node)
        {
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            return RecordingStore.PopPendingTree();
        }

        private static ConfigNode CrewSnapshot(string kerbal)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            part.AddValue("crew", kerbal);
            return snapshot;
        }

        private static void SetPoints(Recording rec, params double[] uts)
        {
            rec.Points.Clear();
            rec.TrackSections.Clear();
            foreach (double ut in uts)
            {
                rec.Points.Add(new TrajectoryPoint
                {
                    ut = ut, altitude = 1000.0, bodyName = "Kerbin",
                    rotation = Quaternion.identity, velocity = Vector3.zero,
                });
            }
            rec.ExplicitStartUT = uts[0];
            rec.ExplicitEndUT = uts[uts.Length - 1];
        }

        private static void RunResumePrep(RecordingTree tree, double resumeUT, LoadKind kind)
        {
            var recorder = new FlightRecorder { ActiveTree = tree };
            FlightRecorder.QuickloadResumeUTProviderForTesting = () => resumeUT;
            ParsekScenario.ConfigurePendingQuickloadResumeContext(tree, kind);
            MethodInfo method = typeof(FlightRecorder).GetMethod(
                "PrepareQuickloadResumeStateIfNeeded", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method.Invoke(recorder, null);
        }
    }
}
