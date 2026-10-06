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
        public void TrimAndReconcile_UntaggedKscRowAfterCutoff_Kept()
        {
            // D2 / QL-R2: KSC rows carry no recording id and stay committed.
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
            var committed = CommitBoosterTree("ta3_recommit");
            string boosterId = ChildId(committed, 1);
            AddEvent(boosterId, 150.0, GameStateEventType.ContractCompleted, "guid-150", "fundsReward=100");
            AddEvent(boosterId, 290.0, GameStateEventType.ContractCompleted, "guid-290", "fundsReward=100");
            LedgerOrchestrator.Initialize();
            bool science = false;
            LedgerOrchestrator.OnRecordingCommitted(boosterId, 120.0, 300.0, null, ref science);
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
            RecordingStore.AddCommittedInternal(booster);
            science = false;
            LedgerOrchestrator.OnRecordingCommitted(boosterId, 120.0, 260.0, null, ref science);

            Assert.Single(Ledger.Actions, a => a.Type == GameActionType.ContractComplete && a.ContractId == "guid-150");
            Assert.DoesNotContain(Ledger.Actions, a => a.ContractId == "guid-290");
            var assignment = Assert.Single(Ledger.Actions,
                a => a.Type == GameActionType.KerbalAssignment && a.RecordingId == boosterId);
            Assert.NotEqual(KerbalEndState.Dead, assignment.KerbalEndStateField);
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

        // The committed future: the booster was destroyed at 300 with Bob aboard and the tree
        // ran on to 320.
        private static RecordingTree CommitBoosterTree(string id)
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
            foreach (var rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
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
