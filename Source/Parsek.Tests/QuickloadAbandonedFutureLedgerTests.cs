using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The abandoned-future reconcile at the quickload-resume trim, event and ledger halves
    /// (roadmap TA-2 / TA-3): the trimmed set's tagged game-state events after the resume UT are
    /// purged, and its recording-tagged ledger rows after the resume UT are retired (owner ruling
    /// OQ-2), driven through the production entry
    /// <c>FlightRecorder.PrepareQuickloadResumeStateIfNeeded</c>. Untagged rows, route rows,
    /// other trees and anything still committed stay. The end-state half is in
    /// <c>QuickloadResumeTests</c>.
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
        // TA-2: tagged game-state events
        // ============================================================

        [Fact]
        public void TrimAndReconcile_AbandonedContractCompletion_NotBookedAtCommit()
        {
            // F5 at 200; the flight completes a contract at 300 and crashes; F9. The resumed
            // flight flies on to 350 and commits: the abandoned completion must not be booked.
            var tree = MakeTree("ta2_contract");
            string rootId = tree.RootRecordingId;
            AddEvent(rootId, 300.0, GameStateEventType.ContractCompleted, "guid-abandoned", "fundsReward=5000");

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            var root = tree.Recordings[rootId];
            RecordingStore.AddCommittedInternal(root);
            LedgerOrchestrator.Initialize();
            bool science = false;
            LedgerOrchestrator.OnRecordingCommitted(rootId, 100.0, 350.0, null, ref science);

            Assert.DoesNotContain(Ledger.Actions, a =>
                a.Type == GameActionType.ContractComplete && a.ContractId == "guid-abandoned");
        }

        [Fact]
        public void TrimAndReconcile_PurgesOnlyTaggedAfterCutoff_KeepsPreCutoffUntaggedAndOtherTrees()
        {
            var tree = MakeTree("ta2_scope");
            string rootId = tree.RootRecordingId;
            string childId = ChildId(tree, 1);
            AddEvent(rootId, 150.0, GameStateEventType.ContractCompleted, "before", "fundsReward=1");
            AddEvent(rootId, 200.0, GameStateEventType.ContractCompleted, "at-cutoff", "fundsReward=1");
            AddEvent(rootId, 300.0, GameStateEventType.ContractCompleted, "after-root", "fundsReward=1");
            AddEvent(childId, 250.0, GameStateEventType.MilestoneAchieved, "after-child", "");
            AddEvent(null, 300.0, GameStateEventType.ContractCompleted, "untagged", "fundsReward=1");
            AddEvent("rec_other_tree", 300.0, GameStateEventType.ContractCompleted, "other-tree", "fundsReward=1");

            logLines.Clear();
            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            var keys = GameStateStore.Events.Select(e => e.key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "at-cutoff", "before", "other-tree", "untagged" }, keys);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains("eventsPurged=2")
                && l.Contains("ledgerRowsRetired=0"));
            // No row moved, so no recalculation.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("reason=quickload-abandoned-future") && l.Contains("[LedgerOrchestrator]"));
        }

        [Fact]
        public void CollectRecordingIdsWithTaggedEventsAfterUT_StrictLiveAndMilestones()
        {
            AddEvent("live_after", 300.0, GameStateEventType.ContractCompleted, "a", "");
            AddEvent("live_at", 200.0, GameStateEventType.ContractCompleted, "b", "");
            AddEvent("not_asked", 300.0, GameStateEventType.ContractCompleted, "c", "");
            AddEvent(null, 300.0, GameStateEventType.ContractCompleted, "d", "");
            var milestone = new Milestone { MilestoneId = "ms_collect", LastReplayedEventIndex = -1 };
            milestone.Events.Add(new GameStateEvent
            {
                ut = 250.0, eventType = GameStateEventType.ContractCompleted, key = "e", recordingId = "held_after",
            });
            MilestoneStore.AddMilestoneForTesting(milestone);

            var asked = new[] { "live_after", "live_at", "held_after", "absent" };
            Assert.Equal(new[] { "held_after", "live_after" },
                GameStateStore.CollectRecordingIdsWithTaggedEventsAfterUT(asked, 200.0)
                    .OrderBy(i => i, StringComparer.Ordinal));
            Assert.Equal(new[] { "held_after", "live_after", "live_at" },
                GameStateStore.CollectRecordingIdsWithTaggedEventsAfterUT(asked, double.NegativeInfinity)
                    .OrderBy(i => i, StringComparer.Ordinal));
            Assert.Empty(GameStateStore.CollectRecordingIdsWithTaggedEventsAfterUT(asked, double.NaN));
            Assert.Empty(GameStateStore.CollectRecordingIdsWithTaggedEventsAfterUT(null, 200.0));
            Assert.Equal(5, GameStateStore.Events.Count + milestone.Events.Count);
        }

        [Fact]
        public void TrimAndReconcile_PrunedRecording_AllEventsPurged()
        {
            var tree = MakeTree("ta2_pruned");
            string futureId = ChildId(tree, 2);
            SetPoints(tree.Recordings[futureId], 250.0, 400.0);
            AddEvent(futureId, 260.0, GameStateEventType.ContractCompleted, "pruned-a", "fundsReward=1");
            AddEvent(futureId, 390.0, GameStateEventType.MilestoneAchieved, "pruned-b", "");

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            Assert.False(tree.Recordings.ContainsKey(futureId));
            Assert.DoesNotContain(GameStateStore.Events, e => e.recordingId == futureId);
            Assert.Contains(logLines, l =>
                l.Contains("PurgeEventsForRecordings (quickload-abandoned-future)") && l.Contains("live=2"));
        }

        [Fact]
        public void TrimAndReconcile_MilestoneHeldEvent_Purged()
        {
            var tree = MakeTree("ta2_milestone");
            string rootId = tree.RootRecordingId;
            var milestone = new Milestone
            {
                MilestoneId = "ms_ta2",
                StartUT = 100.0,
                EndUT = 350.0,
                RecordingId = rootId,
                Committed = true,
                LastReplayedEventIndex = -1,
            };
            milestone.Events.Add(new GameStateEvent
            {
                ut = 150.0, eventType = GameStateEventType.ContractCompleted, key = "held-before", recordingId = rootId,
            });
            milestone.Events.Add(new GameStateEvent
            {
                ut = 300.0, eventType = GameStateEventType.ContractCompleted, key = "held-after", recordingId = rootId,
            });
            MilestoneStore.AddMilestoneForTesting(milestone);

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            Assert.Equal(new[] { "held-before" }, milestone.Events.Select(e => e.key).ToArray());
        }

        [Fact]
        public void TrimAndReconcile_AcceptAfterCutoff_ContractSnapshotPurgedWithIt()
        {
            var tree = MakeTree("ta2_snapshot");
            string rootId = tree.RootRecordingId;
            AddEvent(rootId, 120.0, GameStateEventType.ContractAccepted, "guid-kept", "");
            AddEvent(rootId, 250.0, GameStateEventType.ContractAccepted, "guid-abandoned", "");
            GameStateStore.AddContractSnapshot("guid-kept", new ConfigNode("CONTRACT"));
            GameStateStore.AddContractSnapshot("guid-abandoned", new ConfigNode("CONTRACT"));

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            Assert.Equal(new[] { "guid-kept" },
                GameStateStore.ContractSnapshots.Select(s => s.contractGuid).ToArray());
        }

        [Fact]
        public void TrimAndReconcile_CommittedIdNeverPurged()
        {
            var tree = MakeTree("ta2_committed");
            string childId = ChildId(tree, 1);
            AddEvent(childId, 300.0, GameStateEventType.ContractCompleted, "committed-after", "fundsReward=1");
            CommitStandaloneRecording(childId);

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            Assert.Contains(GameStateStore.Events, e => e.key == "committed-after");
        }

        [Theory]
        [InlineData("ReFlyStart")]
        [InlineData("Cold")]
        public void TrimAndReconcile_KindWithoutReconcileCell_KeepsEventsAndRows(string kindName)
        {
            var kind = (LoadKind)Enum.Parse(typeof(LoadKind), kindName);
            var tree = MakeTree("ta2_kind_" + kindName);
            string rootId = tree.RootRecordingId;
            AddEvent(rootId, 300.0, GameStateEventType.ContractCompleted, "kept", "fundsReward=1");
            Ledger.AddAction(new GameAction
            {
                UT = 300.0, Type = GameActionType.FundsEarning, RecordingId = rootId, FundsAwarded = 100f,
            });

            logLines.Clear();
            RunResumePrep(tree, 200.0, kind);

            Assert.Contains(GameStateStore.Events, e => e.key == "kept");
            Assert.Single(Ledger.Actions);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile skipped:")
                && l.Contains("events=" + LoadReconcilePolicy.Decide(kind, LoadStateCategory.AbandonedFutureEvents).Action)
                && l.Contains("ledgerRows=" + LoadReconcilePolicy.Decide(kind, LoadStateCategory.AbandonedFutureLedgerRows).Action));
        }

        // ============================================================
        // TA-3: ledger rows of a tree the F9 un-committed
        // ============================================================

        [Fact]
        public void TryRestoreActiveTreeNode_DetachedCommittedTree_ResumeRetiresRowsAfterCutoff()
        {
            var committed = CommitBoosterTree("ta3_detach");
            string rootId = committed.RootRecordingId;
            string boosterId = ChildId(committed, 1);
            AddRow(rootId, 150.0, GameActionType.ContractComplete, contractId: "kept-150");
            AddRow(rootId, 300.0, GameActionType.FundsEarning);
            AddRow(boosterId, 120.0, GameActionType.KerbalAssignment, kerbal: "Bob Kerman");
            AddRow(boosterId, 300.0, GameActionType.ReputationPenalty);

            var resumed = RestoreQuicksaveOf(committed);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);

            logLines.Clear();
            RunResumePrep(resumed, 200.0, LoadKind.QuickloadFlight);

            Assert.Equal(
                new[] { "ContractComplete:" + rootId },
                NonSeedRows().Select(a => a.Type + ":" + a.RecordingId).ToArray());
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]")
                && l.Contains("RetireAbandonedFutureActions: removed 3 action(s)")
                && l.Contains("reason=quickload-abandoned-future"));
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains("ledgerRowsRetired=3")
                && l.Contains("afterCutoff=2")
                && l.Contains("kerbalAssignment=1")
                && l.Contains("prunedRecording=0"));
            Assert.Contains(logLines, l => l.Contains("quickload-abandoned-future")
                && (l.Contains("Current-UT ledger recalculation") || l.Contains("Current-timeline recalc")));
        }

        [Fact]
        public void TrimAndReconcile_RetireRecalc_KeepsTheKspPatchDeferredWhileTheTreeIsLive()
        {
            // The recalculation after a retire runs while the resumed tree is live and
            // uncommitted; it must not write the committed-only ledger state into KSP.
            GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = () => true;
            try
            {
                var tree = MakeTree("ta3_deferral");
                AddRow(tree.RootRecordingId, 300.0, GameActionType.FundsEarning);
                Ledger.AddAction(new GameAction
                {
                    UT = 400.0, Type = GameActionType.FundsSpending, FundsSpent = 10f,
                    FundsSpendingSource = FundsSpendingSource.FacilityUpgrade,
                });

                logLines.Clear();
                RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

                Assert.Contains(logLines, l =>
                    l.Contains("Current-UT ledger recalculation: reason=quickload-abandoned-future cutoffUT=200"));
                Assert.Contains(logLines, l =>
                    l.Contains("RecalculateAndPatch: deferred KSP state patch")
                    && l.Contains("active uncommitted flight tree"));
            }
            finally
            {
                GameStateRecorder.HasActiveUncommittedTreeProviderForTesting = null;
            }
        }

        [Fact]
        public void TrimAndReconcile_OtherCommittedTreesUntouched()
        {
            var tree = MakeTree("ta3_other");
            var other = MakeTree("ta3_other_committed");
            foreach (var rec in other.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(other);
            AddRow(other.RootRecordingId, 300.0, GameActionType.FundsEarning);
            AddRow(ChildId(other, 1), 120.0, GameActionType.KerbalAssignment, kerbal: "Val Kerman");
            AddRow(tree.RootRecordingId, 300.0, GameActionType.FundsEarning);

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            Assert.Equal(
                new[] { ChildId(other, 1), other.RootRecordingId }.OrderBy(i => i, StringComparer.Ordinal),
                NonSeedRows().Select(a => a.RecordingId).OrderBy(i => i, StringComparer.Ordinal));
        }

        [Fact]
        public void TrimAndReconcile_UntaggedKscRowAfterCutoff_LeftToTheInSessionLoadRetire()
        {
            var tree = MakeTree("ta3_ksc");
            Ledger.AddAction(new GameAction
            {
                UT = 300.0, Type = GameActionType.FundsSpending, FundsSpent = 500f,
                FundsSpendingSource = FundsSpendingSource.FacilityUpgrade,
            });
            AddRow(tree.RootRecordingId, 300.0, GameActionType.FundsEarning);

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            var kept = Assert.Single(NonSeedRows());
            Assert.True(string.IsNullOrEmpty(kept.RecordingId));
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
            Ledger.AddAction(new GameAction
            {
                ActionId = "act_route", UT = 300.0, Type = GameActionType.RouteDispatched, RouteId = "route-1",
            });

            int retired = ParsekScenario.RetireUntaggedRowsAfterSaveAtInSessionLoad(kind, 200.0, 400.0);

            Assert.Equal(2, retired);
            Assert.Equal(
                new[] { "act_seed", "act_before_save", "act_at_save", "act_committed_future", "act_tagged", "act_route" },
                Ledger.Actions.Select(a => a.ActionId));
            Assert.Contains(logLines, l => l.Contains("[INFO][LoadPolicy]")
                && l.Contains("Untagged rows after the save retired: kind=" + kind + " saveUT=200 clockUT=400")
                && l.Contains("retired=2 seedsKept=0 routeRowsLeft=1"));
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
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(
                new GameAction { UT = 300.0, Type = GameActionType.RouteDispatched, RouteId = "r" }, 200.0, 400.0));
            Assert.False(ParsekScenario.ShouldRetireUntaggedRowAfterSave(null, 200.0, 400.0));
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

        [Fact]
        public void TrimAndReconcile_RouteRowsUntouched()
        {
            var tree = MakeTree("ta3_route");
            Ledger.AddAction(new GameAction
            {
                UT = 300.0, Type = GameActionType.RouteDispatched, RecordingId = tree.RootRecordingId, RouteId = "route-1",
            });

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            var kept = Assert.Single(NonSeedRows());
            Assert.Equal("route-1", kept.RouteId);
        }

        [Fact]
        public void TrimAndReconcile_ScienceCapturedBeforeCutoff_KeptEvenThoughStampedAtRecordingEnd()
        {
            // Commit-time science rows carry UT = recording end and the capture moment in StartUT;
            // a transmission before the quicksave is not part of the abandoned future.
            var tree = MakeTree("ta3_science");
            string rootId = tree.RootRecordingId;
            Ledger.AddAction(new GameAction
            {
                UT = 300.0, Type = GameActionType.ScienceEarning, RecordingId = rootId,
                SubjectId = "crewReport@KerbinFlyingLow", StartUT = 150f, EndUT = 300f, ScienceAwarded = 5f,
            });
            Ledger.AddAction(new GameAction
            {
                UT = 300.0, Type = GameActionType.ScienceEarning, RecordingId = rootId,
                SubjectId = "crewReport@KerbinSrfLanded", StartUT = 280f, EndUT = 300f, ScienceAwarded = 5f,
            });

            RunResumePrep(tree, 200.0, LoadKind.QuickloadFlight);

            var kept = Assert.Single(NonSeedRows());
            Assert.Equal("crewReport@KerbinFlyingLow", kept.SubjectId);
        }

        [Fact]
        public void TrimAndReconcile_RecommitAfterRetire_NoDuplicateFreshRows()
        {
            // Both commits go through the production tree commit: RecordingStore.CommitTree, then
            // LedgerOrchestrator.NotifyLedgerTreeCommitted (CommitTreeFlight / MergeCommit order).
            var committed = MakeBoosterFutureTree("ta3_recommit");
            string boosterId = ChildId(committed, 1);
            AddEvent(boosterId, 150.0, GameStateEventType.ContractCompleted, "guid-150", "fundsReward=100");
            AddEvent(boosterId, 290.0, GameStateEventType.ContractCompleted, "guid-290", "fundsReward=100");
            LedgerOrchestrator.Initialize();
            RecordingStore.CommitTree(committed);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(committed);
            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.KerbalAssignment && a.RecordingId == boosterId);
            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.ContractComplete && a.ContractId == "guid-290");

            var resumed = RestoreQuicksaveOf(committed);
            RunResumePrep(resumed, 200.0, LoadKind.QuickloadFlight);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.KerbalAssignment && a.RecordingId == boosterId);
            Assert.DoesNotContain(Ledger.Actions, a => a.ContractId == "guid-290");

            // The resumed flight: the booster survives this time and the tree commits again.
            var booster = resumed.Recordings[boosterId];
            SetPoints(booster, 120.0, 180.0, 200.0, 260.0);
            booster.TerminalStateValue = TerminalState.Landed;
            booster.GhostVisualSnapshot = CrewSnapshot("Bob Kerman");
            RecordingStore.CommitTree(resumed);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(resumed);

            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.ContractComplete && a.ContractId == "guid-150");
            Assert.DoesNotContain(Ledger.Actions, a => a.ContractId == "guid-290");
            var assignment = Assert.Single(Ledger.Actions,
                a => a.Type == GameActionType.KerbalAssignment && a.RecordingId == boosterId);
            Assert.NotEqual(KerbalEndState.Dead, assignment.KerbalEndStateField);
        }

        // ============================================================
        // Committed history at the quicksave: if the quicksave's active tree was already
        // committed (a copy-on-write restore clone; the save-side overlap guard writes that
        // shape only after an out-of-band sidecar write, see
        // QUICKLOAD-TRIM-CUTS-COMMITTED-HISTORY-OF-A-RESUMED-CLONE), the restore detaches it
        // too, so only the quicksave can tell its history members from the abandoned future.
        // Owner ruling D2: committed history is never retired, cut or pruned.
        // ============================================================

        [Fact]
        public void TryRestoreActiveTreeNode_CommittedBeforeTheQuicksave_TerminalMemberKeepsEndStateEventsAndRows()
        {
            // M lands at 400 in committed history and is still a ghost at the F5 (260); the
            // quicksave's own copy of M already runs to 400 with its Landed terminal.
            var committed = MakeHistoryTree("hist_terminal");
            CommitInMemory(committed);
            string rootId = committed.RootRecordingId;
            string mId = HistoryMemberId(committed);
            AddEvent(mId, 390.0, GameStateEventType.ContractCompleted, "hist-390", "fundsReward=1");
            AddRow(mId, 390.0, GameActionType.ContractComplete, contractId: "hist-390");
            AddRow(mId, 120.0, GameActionType.KerbalAssignment, kerbal: "Bob Kerman");
            AddRow(rootId, 300.0, GameActionType.FundsEarning);

            var resumed = RestoreQuicksaveNode(QuicksaveNodeOf(MakeHistoryTree("hist_terminal"), null));
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == committed.Id);

            logLines.Clear();
            RunResumePrep(resumed, 260.0, LoadKind.QuickloadFlight);

            var m = resumed.Recordings[mId];
            Assert.Equal(TerminalState.Landed, m.TerminalStateValue);
            Assert.Equal(KerbalEndState.Aboard, m.CrewEndStates["Bob Kerman"]);
            Assert.True(m.CrewEndStatesResolved);
            Assert.Contains(GameStateStore.Events, e => e.key == "hist-390");
            Assert.Contains(NonSeedRows(), a => a.Type == GameActionType.ContractComplete && a.ContractId == "hist-390");
            Assert.Contains(NonSeedRows(), a => a.Type == GameActionType.KerbalAssignment && a.RecordingId == mId);
            // The live root's own abandoned future still goes.
            Assert.DoesNotContain(NonSeedRows(), a => a.Type == GameActionType.FundsEarning && a.RecordingId == rootId);
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains("skippedQuicksaveHistory=1")
                && l.Contains(mId + ":terminal-in-quicksave"));
        }

        [Fact]
        public void TryRestoreActiveTreeNode_CommittedTreeInTheQuicksave_ChainSegmentWithoutTerminalKept()
        {
            // The quicksave carries the committed tree's own node (OnSave writes every committed
            // tree before the active one): a chain segment that ends past the cutoff with no
            // terminal, no explicit end and no branch point is still committed history.
            var committed = MakeHistoryTree("hist_chain");
            string segmentId = HistoryMemberId(committed);
            Recording segment = committed.Recordings[segmentId];
            segment.TerminalStateValue = null;
            segment.CrewEndStates = null;
            segment.CrewEndStatesResolved = false;
            segment.ExplicitEndUT = double.NaN;
            segment.ChainId = "chain_hist";
            segment.ChainIndex = 0;
            CommitInMemory(committed);
            AddEvent(segmentId, 390.0, GameStateEventType.ContractCompleted, "chain-390", "fundsReward=1");
            AddRow(segmentId, 390.0, GameActionType.ContractComplete, contractId: "chain-390");

            // The live clone adds a fresh segment the player is flying.
            RecordingTree clone = CloneWithLiveSegment(committed, "live_seg_hist_chain");
            AddRow("live_seg_hist_chain", 300.0, GameActionType.FundsEarning);

            var resumed = RestoreQuicksaveNode(QuicksaveNodeOf(clone, committed));

            logLines.Clear();
            RunResumePrep(resumed, 260.0, LoadKind.QuickloadFlight);

            Assert.Contains(GameStateStore.Events, e => e.key == "chain-390");
            Assert.Contains(NonSeedRows(), a => a.ContractId == "chain-390");
            Assert.DoesNotContain(NonSeedRows(), a => a.RecordingId == "live_seg_hist_chain");
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains(segmentId + ":committed-in-quicksave"));
        }

        [Fact]
        public void TryRestoreActiveTreeNode_CommittedBeforeTheQuicksave_BranchAfterCutoffMembersKept()
        {
            // P ends at a branch point at 350 (no terminal, no explicit end); its child P1 starts
            // there and lands at 420. Both are committed history at the F5 (260).
            var committed = MakeHistoryTree("hist_branch");
            string pId = HistoryMemberId(committed);
            Recording p = committed.Recordings[pId];
            p.TerminalStateValue = null;
            p.CrewEndStates = null;
            p.CrewEndStatesResolved = false;
            SetPoints(p, 120.0, 300.0, 350.0);
            p.ExplicitEndUT = double.NaN;
            p.ChildBranchPointId = "bp_hist_branch_350";
            var p1 = new Recording
            {
                RecordingId = "child_after_hist_branch", TreeId = committed.Id, VesselName = "P1",
                VesselPersistentId = 333u, ParentBranchPointId = "bp_hist_branch_350",
                TerminalStateValue = TerminalState.Landed,
            };
            SetPoints(p1, 350.0, 420.0);
            committed.AddOrReplaceRecording(p1);
            committed.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_hist_branch_350", UT = 350.0, Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { pId },
                ChildRecordingIds = new List<string> { p1.RecordingId },
            });
            CommitInMemory(committed);
            AddRow(pId, 340.0, GameActionType.ContractComplete, contractId: "p-340");
            AddRow(p1.RecordingId, 380.0, GameActionType.FundsEarning);
            AddEvent(p1.RecordingId, 380.0, GameStateEventType.ContractCompleted, "p1-380", "fundsReward=1");

            var quicksaved = RecordingTree.DeepClone(committed);
            var resumed = RestoreQuicksaveNode(QuicksaveNodeOf(quicksaved, null));

            logLines.Clear();
            RunResumePrep(resumed, 260.0, LoadKind.QuickloadFlight);

            Assert.Contains(NonSeedRows(), a => a.ContractId == "p-340");
            Assert.Contains(NonSeedRows(), a => a.RecordingId == "child_after_hist_branch");
            // P1 starts after the cutoff; as quicksave history the trim leaves it in the tree and
            // it stays out of the plan's pruned set, so neither its rows nor its events are dropped.
            Assert.Contains(GameStateStore.Events, e => e.key == "p1-380");
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains(pId + ":branch-after-cutoff-in-quicksave"));
        }

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
        public void TryRestoreActiveTreeNode_EndedMemberRestampedAfterTheQuicksave_EndingClearedAndRowRetired()
        {
            // QUICKLOAD-ENDED-MEMBER-RESTAMPED-AFTER-THE-SAVE-KEEPS-ITS-FUTURE. D landed at 150 and
            // its recording ended (the quicksave at 200 holds it Landed); a recovery at 300
            // restamped it Recovered with a recovery funds row before the tree committed. The F9
            // back to 200 restores the landed vessel, so the Recovered ending and the row are the
            // abandoned future. E ended Landed at 150 and was not touched: it stays history.
            string id = "restamp";
            var committed = MakeTree(id);
            string dId = ChildId(committed, 1);
            string eId = ChildId(committed, 2);
            Recording dCommitted = committed.Recordings[dId];
            SetPoints(dCommitted, 120.0, 150.0);
            dCommitted.TerminalStateValue = TerminalState.Recovered;
            dCommitted.ExplicitEndUT = 300.0;
            Recording eCommitted = committed.Recordings[eId];
            SetPoints(eCommitted, 130.0, 150.0);
            eCommitted.TerminalStateValue = TerminalState.Landed;
            CommitInMemory(committed);
            Ledger.AddAction(new GameAction
            {
                UT = 300.0, Type = GameActionType.FundsEarning, RecordingId = dId,
                FundsAwarded = 900f, FundsSource = FundsEarningSource.Recovery,
            });
            AddRow(eId, 140.0, GameActionType.ContractComplete, contractId: "e-140");

            var quicksaved = MakeTree(id);
            SetPoints(quicksaved.Recordings[dId], 120.0, 150.0);
            quicksaved.Recordings[dId].TerminalStateValue = TerminalState.Landed;
            SetPoints(quicksaved.Recordings[eId], 130.0, 150.0);
            quicksaved.Recordings[eId].TerminalStateValue = TerminalState.Landed;
            var resumed = RestoreQuicksaveNode(QuicksaveNodeOf(quicksaved, null));
            Assert.Equal(TerminalState.Recovered, resumed.Recordings[dId].TerminalStateValue);

            logLines.Clear();
            RunResumePrep(resumed, 200.0, LoadKind.QuickloadFlight);

            Recording d = resumed.Recordings[dId];
            Assert.Null(d.TerminalStateValue);
            Assert.True(d.EndUT <= 200.0);
            Assert.Contains(resumed.BackgroundMap, kvp => kvp.Value == dId);
            Assert.DoesNotContain(NonSeedRows(), a => a.RecordingId == dId);
            Assert.Equal(TerminalState.Landed, resumed.Recordings[eId].TerminalStateValue);
            Assert.Contains(NonSeedRows(), a => a.ContractId == "e-140");
            Assert.Contains(logLines, l =>
                l.Contains("Quickload abandoned-future reconcile:")
                && l.Contains(eId + ":terminal-in-quicksave")
                && !l.Contains(dId + ":"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("restamped after the quicksave")
                && l.Contains("rec=" + dId) && l.Contains("quicksaveTerminal=Landed")
                && l.Contains("terminal=Recovered"));
        }

        [Fact]
        public void TrimAndReconcile_ScienceAtLargeUT_SinglePrecisionCaptureBeforeCutoffKept()
        {
            // At UT ~1e7 a float step is a whole second: a transmission 0.3 s before the F5
            // rounds up past the double cutoff unless the cutoff is rounded the same way.
            var tree = MakeTree("ta3_float");
            string rootId = tree.RootRecordingId;
            const double cutoff = 10000000.9;
            Ledger.AddAction(new GameAction
            {
                UT = 10000050.0, Type = GameActionType.ScienceEarning, RecordingId = rootId,
                SubjectId = "crewReport@KerbinFlyingHigh", StartUT = (float)10000000.6,
                EndUT = (float)10000050.0, ScienceAwarded = 5f,
            });
            Ledger.AddAction(new GameAction
            {
                UT = 10000050.0, Type = GameActionType.ScienceEarning, RecordingId = rootId,
                SubjectId = "crewReport@KerbinInSpaceLow", StartUT = (float)10000002.6,
                EndUT = (float)10000050.0, ScienceAwarded = 5f,
            });
            Assert.True((double)(float)10000000.6 > cutoff, "fixture: the capture rounds past the double cutoff");

            RunResumePrep(tree, cutoff, LoadKind.QuickloadFlight);

            var kept = Assert.Single(NonSeedRows());
            Assert.Equal("crewReport@KerbinFlyingHigh", kept.SubjectId);
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

        [Fact]
        public void ShouldRetireAbandonedFutureRow_Table()
        {
            var plan = new ParsekScenario.AbandonedFuturePlan { CutoffUT = 200.0 };
            plan.TrimmedIds.Add("rec");
            plan.TrimmedIds.Add("pruned");
            plan.PrunedIds.Add("pruned");
            plan.TrimmedIds.Add("cleared");
            plan.EndStateClearedIds.Add("cleared");

            Assert.True(Retire(plan, "rec", 300.0, GameActionType.FundsEarning));
            Assert.False(Retire(plan, "rec", 200.0, GameActionType.FundsEarning));
            Assert.False(Retire(plan, "rec", 150.0, GameActionType.KerbalAssignment));
            Assert.True(Retire(plan, "cleared", 120.0, GameActionType.KerbalAssignment));
            Assert.False(Retire(plan, "cleared", 120.0, GameActionType.FundsSpending));
            Assert.True(Retire(plan, "pruned", 150.0, GameActionType.FundsSpending));
            Assert.False(Retire(plan, "other", 300.0, GameActionType.FundsEarning));
            Assert.False(Retire(plan, null, 300.0, GameActionType.FundsEarning));
            Assert.False(Retire(plan, "rec", 300.0, GameActionType.FundsInitial));
            Assert.False(ParsekScenario.ShouldRetireAbandonedFutureRow(new GameAction
            {
                UT = 300.0, Type = GameActionType.RouteDispatched, RecordingId = "rec", RouteId = "r",
            }, plan));
            Assert.False(ParsekScenario.ShouldRetireAbandonedFutureRow(null, plan));
            Assert.False(ParsekScenario.ShouldRetireAbandonedFutureRow(new GameAction { RecordingId = "rec", UT = 300.0 }, null));
        }

        // ============================================================
        // helpers
        // ============================================================

        // The recalculation that follows a retire may file the career seed rows.
        private static List<GameAction> NonSeedRows()
        {
            return Ledger.Actions.Where(a => !RecalculationEngine.IsSeedType(a.Type)).ToList();
        }

        private static bool Retire(ParsekScenario.AbandonedFuturePlan plan, string recordingId, double ut, GameActionType type)
        {
            return ParsekScenario.ShouldRetireAbandonedFutureRow(
                new GameAction { UT = ut, Type = type, RecordingId = recordingId }, plan);
        }

        private static RecordingTree MakeTree(string id)
        {
            var tree = new RecordingTree
            {
                Id = id,
                TreeName = id,
                RootRecordingId = "root_" + id,
                ActiveRecordingId = "root_" + id,
            };
            var root = new Recording { RecordingId = "root_" + id, TreeId = id, VesselName = id, VesselPersistentId = 111u };
            SetPoints(root, 100.0, 150.0, 200.0);
            tree.AddOrReplaceRecording(root);
            var booster = new Recording { RecordingId = "child_" + id + "_1", TreeId = id, VesselName = id + " booster", VesselPersistentId = 222u };
            SetPoints(booster, 120.0, 180.0, 200.0);
            tree.AddOrReplaceRecording(booster);
            var late = new Recording { RecordingId = "child_" + id + "_2", TreeId = id, VesselName = id + " late", VesselPersistentId = 333u };
            SetPoints(late, 130.0, 190.0);
            tree.AddOrReplaceRecording(late);
            tree.RebuildBackgroundMap();
            return tree;
        }

        private static string ChildId(RecordingTree tree, int index) => "child_" + tree.Id + "_" + index;

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

        // The committed future: the booster was destroyed at 300 with Bob aboard and the tree
        // ran on to 320.
        private static RecordingTree CommitBoosterTree(string id)
        {
            var tree = MakeBoosterFutureTree(id);
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static RecordingTree MakeBoosterFutureTree(string id)
        {
            var tree = MakeTree(id);
            SetPoints(tree.Recordings[tree.RootRecordingId], 100.0, 150.0, 320.0);
            var booster = tree.Recordings[ChildId(tree, 1)];
            SetPoints(booster, 120.0, 180.0, 300.0);
            booster.TerminalStateValue = TerminalState.Destroyed;
            booster.VesselDestroyed = true;
            booster.GhostVisualSnapshot = CrewSnapshot("Bob Kerman");
            booster.CrewEndStates = new Dictionary<string, KerbalEndState> { { "Bob Kerman", KerbalEndState.Dead } };
            booster.CrewEndStatesResolved = true;
            return tree;
        }

        // F9 back into the flight's quicksave taken at 200: the save carries the tree as it was
        // then (isActive); the restore splices the committed copy and detaches it.
        private static RecordingTree RestoreQuicksaveOf(RecordingTree committed)
        {
            var quicksaved = MakeTree(committed.Id);
            var node = new ConfigNode("PARSEK_SCENARIO");
            var treeNode = node.AddNode("RECORDING_TREE");
            quicksaved.Save(treeNode);
            treeNode.AddValue("isActive", "True");
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            return RecordingStore.PopPendingTree();
        }

        private static void CommitStandaloneRecording(string recordingId)
        {
            var otherTree = new RecordingTree { Id = "committed_" + recordingId, TreeName = "committed", RootRecordingId = recordingId };
            var rec = new Recording { RecordingId = recordingId, TreeId = otherTree.Id, MergeState = MergeState.Immutable };
            otherTree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(otherTree);
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

        private static void AddEvent(string recordingId, double ut, GameStateEventType type, string key, string detail)
        {
            var e = new GameStateEvent
            {
                ut = ut, eventType = type, key = key, detail = detail, recordingId = recordingId,
            };
            GameStateStore.AddEvent(ref e);
        }

        private static void AddRow(string recordingId, double ut, GameActionType type,
            string contractId = null, string kerbal = null)
        {
            Ledger.AddAction(new GameAction
            {
                UT = ut, Type = type, RecordingId = recordingId, ContractId = contractId, KerbalName = kerbal,
                StartUT = (float)ut,
            });
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
