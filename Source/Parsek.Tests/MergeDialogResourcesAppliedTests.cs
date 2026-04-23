using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase C of the ledger / lump-sum reconciliation fix
    /// (<c>docs/dev/plans/fix-ledger-lump-sum-reconciliation.md</c>).
    ///
    /// <para>The merge dialog used to commit a tree without advancing its child
    /// recordings' <c>LastAppliedResourceIndex</c> values to the end. That left
    /// already-live resources looking un-applied to budget reservation and, before
    /// Phase F deleted the lump-sum path, also re-armed the old tree applier on the
    /// next FLIGHT entry. The in-flight commit path
    /// (<see cref="ParsekFlight.CommitTreeFlight"/>) already did the equivalent
    /// inline; this phase brings the merge dialog into parity by routing both
    /// through <see cref="RecordingStore.MarkTreeAsApplied"/>.</para>
    ///
    /// <para>The test target is the extracted <see cref="MergeDialog.MergeCommit"/>
    /// (and its sibling <see cref="MergeDialog.MergeDiscard"/>): the dialog button
    /// lambda calls into them, so unit tests cover the same code path that runs
    /// in-game without needing a Unity dialog.</para>
    ///
    /// <para>The critical regression test is
    /// <see cref="MergeCommit_DoesNotTouchMilestoneReplayIndexes"/> — it pins the
    /// "tree-scoped, milestones untouched" contract that motivated the new
    /// <see cref="RecordingStore.MarkTreeAsApplied"/> primitive over the existing
    /// <see cref="RecordingStore.MarkAllFullyApplied"/> (which bumps every
    /// milestone's <c>LastReplayedEventIndex</c>).</para>
    /// </summary>
    [Collection("Sequential")]
    public class MergeDialogResourcesAppliedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MergeDialogResourcesAppliedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            // MergeCommit -> NotifyLedgerTreeCommitted -> RecalculateAndPatch -> KspStatePatcher
            // would otherwise call Unity's FindObjectsOfType<T>() and throw under xUnit.
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SaveGameForTesting = null;
            MergeJournalOrchestrator.ResetTestOverrides();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SaveGameForTesting = null;
            MergeJournalOrchestrator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static Recording MakeRecording(string id, string treeId, double startUT, double endUT,
            bool emptyPoints = false)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Vessel-" + id,
                TreeId = treeId
            };
            if (!emptyPoints)
            {
                // Three points so LastAppliedResourceIndex == Points.Count - 1 == 2.
                rec.Points.Add(new TrajectoryPoint { ut = startUT });
                rec.Points.Add(new TrajectoryPoint { ut = (startUT + endUT) * 0.5 });
                rec.Points.Add(new TrajectoryPoint { ut = endUT });
            }
            return rec;
        }

        private static RecordingTree MakeTree(string treeId, string activeRecordingId,
            params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Tree-" + treeId,
                RootRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : activeRecordingId,
                ActiveRecordingId = activeRecordingId
            };
            for (int i = 0; i < recordings.Length; i++)
                tree.Recordings[recordings[i].RecordingId] = recordings[i];
            return tree;
        }

        private static ReFlySessionMarker MakeMarker(
            string sessionId, string treeId, string provisionalId, string originId)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                RewindPointId = "rp_merge_dialog",
                InvokedUT = 0.0,
            };
        }

        private static Milestone MakeCommittedMilestone(string id, double startUT, double endUT,
            int lastReplayedIdx, int eventCount)
        {
            var m = new Milestone
            {
                MilestoneId = id,
                StartUT = startUT,
                EndUT = endUT,
                Epoch = 0,
                Committed = true,
                LastReplayedEventIndex = lastReplayedIdx
            };
            for (int i = 0; i < eventCount; i++)
            {
                m.Events.Add(new GameStateEvent
                {
                    ut = startUT + i,
                    eventType = GameStateEventType.FundsChanged,
                    key = id + ":" + i,
                    detail = "",
                    recordingId = ""
                });
            }
            return m;
        }

        // ================================================================
        // 1. Happy path: pending tree -> MergeCommit -> applied + committed
        // ================================================================

        [Fact]
        public void MergeCommit_AdvancesIndexesAndMovesToCommitted()
        {
            var rec1 = MakeRecording("rec-a", "tree-merge-1", 100.0, 200.0);
            var rec2 = MakeRecording("rec-b", "tree-merge-1", 150.0, 220.0);
            var rec3 = MakeRecording("rec-c", "tree-merge-1", 180.0, 250.0);
            var tree = MakeTree("tree-merge-1", "rec-a", rec1, rec2, rec3);

            // Precondition: stash as pending and indexes start at -1.
            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.HasPendingTree);
            foreach (var r in tree.Recordings.Values)
                Assert.Equal(-1, r.LastAppliedResourceIndex);

            // Decisions dict: all ghost-only (false) so we don't force VesselSnapshot work.
            var decisions = new Dictionary<string, bool>
            {
                { "rec-a", false },
                { "rec-b", false },
                { "rec-c", false },
            };

            MergeDialog.MergeCommit(tree, decisions, spawnCount: 0);

            // Every recording's index advanced to last point.
            Assert.Equal(2, rec1.LastAppliedResourceIndex);
            Assert.Equal(2, rec2.LastAppliedResourceIndex);
            Assert.Equal(2, rec3.LastAppliedResourceIndex);

            // Tree is now in committed storage and the pending slot is empty.
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "tree-merge-1");
        }

        // ================================================================
        // 2. Critical: milestone replay indexes must NOT change
        // ================================================================

        [Fact]
        public void MergeCommit_DoesNotTouchMilestoneReplayIndexes()
        {
            // Seed two committed milestones with non-trivial Events lists. If a future
            // reviewer "simplifies" MergeCommit to call MarkAllFullyApplied instead of
            // MarkTreeAsApplied, those milestones' LastReplayedEventIndex would jump to
            // (Events.Count - 1) and silently mark unrelated history as fully replayed.
            // This test pins the contract.
            var ms1 = MakeCommittedMilestone("mile-1", 50.0, 80.0, lastReplayedIdx: 1, eventCount: 5);
            var ms2 = MakeCommittedMilestone("mile-2", 90.0, 110.0, lastReplayedIdx: 0, eventCount: 3);
            MilestoneStore.AddMilestoneForTesting(ms1);
            MilestoneStore.AddMilestoneForTesting(ms2);

            var rec = MakeRecording("rec-mile", "tree-mile", 200.0, 300.0);
            var tree = MakeTree("tree-mile", "rec-mile", rec);
            RecordingStore.StashPendingTree(tree);

            // Snapshot before.
            var milestonesBefore = MilestoneStore.Milestones;
            var indexesBefore = new List<int>(milestonesBefore.Count);
            for (int i = 0; i < milestonesBefore.Count; i++)
                indexesBefore.Add(milestonesBefore[i].LastReplayedEventIndex);

            MergeDialog.MergeCommit(
                tree,
                new Dictionary<string, bool> { { "rec-mile", false } },
                spawnCount: 0);

            // After: recording indexes advanced, milestone indexes unchanged.
            var milestonesAfter = MilestoneStore.Milestones;
            Assert.Equal(indexesBefore.Count, milestonesAfter.Count);
            for (int i = 0; i < milestonesAfter.Count; i++)
                Assert.Equal(indexesBefore[i], milestonesAfter[i].LastReplayedEventIndex);
        }

        // ================================================================
        // 3. Recordings with empty Points are skipped (per primitive contract)
        // ================================================================

        [Fact]
        public void MergeCommit_LeavesEmptyPointRecordingsUnadvanced()
        {
            var fullRec = MakeRecording("rec-full", "tree-empty", 100.0, 200.0);
            var emptyRec = MakeRecording("rec-empty", "tree-empty", 0.0, 0.0, emptyPoints: true);
            var tree = MakeTree("tree-empty", "rec-full", fullRec, emptyRec);
            RecordingStore.StashPendingTree(tree);

            // Precondition.
            Assert.Equal(-1, fullRec.LastAppliedResourceIndex);
            Assert.Equal(-1, emptyRec.LastAppliedResourceIndex);
            Assert.Empty(emptyRec.Points);

            MergeDialog.MergeCommit(
                tree,
                new Dictionary<string, bool> { { "rec-full", false }, { "rec-empty", false } },
                spawnCount: 0);

            // Per-recording indexes only advance when Points is non-empty
            // (mirrors the MarkTreeAsApplied contract that LegacyTreeMigrationTests
            // pins separately).
            Assert.Equal(2, fullRec.LastAppliedResourceIndex);
            Assert.Equal(-1, emptyRec.LastAppliedResourceIndex);
        }

        // ================================================================
        // 4. Discard-branch parity: discard does NOT mark applied
        // ================================================================

        [Fact]
        public void MergeDiscard_DoesNotAdvanceRecordingIndexes()
        {
            var rec = MakeRecording("rec-discard", "tree-discard", 100.0, 200.0);
            var tree = MakeTree("tree-discard", "rec-discard", rec);

            // If a future change accidentally wires MarkTreeAsApplied into the wrong
            // branch (Discard instead of Merge), this assertion fails.
            Assert.Equal(-1, rec.LastAppliedResourceIndex);

            RecordingStore.StashPendingTree(tree);
            MergeDialog.MergeDiscard(tree);

            Assert.Equal(-1, rec.LastAppliedResourceIndex);

            // Tree should be wiped from the pending slot and never make it into
            // committed storage.
            Assert.False(RecordingStore.HasPendingTree);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == "tree-discard");
        }

        [Fact]
        public void MergeDiscard_RecalculatesAfterPendingTreeRemoval()
        {
            LedgerOrchestrator.Initialize();
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 25000f
            });

            var rec = MakeRecording("rec-discard-recalc", "tree-discard-recalc", 100.0, 200.0);
            var tree = MakeTree("tree-discard-recalc", "rec-discard-recalc", rec);
            RecordingStore.StashPendingTree(tree);

            MergeDialog.MergeDiscard(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]") && l.Contains("PatchAll complete"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("deferred KSP state patch"));
        }

        // ================================================================
        // 5. MergeCommit logs the user-choice INFO line (regression: lambda
        //    extraction must preserve the diagnostic the in-game log relies on)
        // ================================================================

        [Fact]
        public void MergeCommit_LogsUserChoiceInfoLine()
        {
            var rec = MakeRecording("rec-log", "tree-log", 100.0, 200.0);
            var tree = MakeTree("tree-log", "rec-log", rec);
            RecordingStore.StashPendingTree(tree);

            MergeDialog.MergeCommit(
                tree,
                new Dictionary<string, bool> { { "rec-log", false } },
                spawnCount: 0);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("User chose: Tree Merge")
                && l.Contains("tree-log"));
        }

        [Fact]
        public void MergeCommit_ReFlyPath_RefreshesQuicksaveAfterMarkerClears()
        {
            var origin = MakeRecording("rec-origin", "tree-refly", 100.0, 200.0);
            var provisional = MakeRecording("rec-provisional", "tree-refly", 100.0, 220.0);
            provisional.MergeState = MergeState.NotCommitted;
            provisional.SupersedeTargetId = origin.RecordingId;
            provisional.TerminalStateValue = TerminalState.Landed;

            var tree = MakeTree("tree-refly", provisional.RecordingId, origin, provisional);
            RecordingStore.StashPendingTree(tree);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = MakeMarker(
                    "sess-refly", "tree-refly", provisional.RecordingId, origin.RecordingId),
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            MergeJournalOrchestrator.DurableSaveForTesting = _ => { };
            bool quicksaveObserved = false;
            RecordingStore.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                if (saveName == "quicksave")
                {
                    Assert.Null(scenario.ActiveReFlySessionMarker);
                    Assert.Equal(MergeState.Immutable, provisional.MergeState);
                    quicksaveObserved = true;
                }
                return "ok";
            };

            MergeDialog.MergeCommit(
                tree,
                new Dictionary<string, bool>
                {
                    { origin.RecordingId, false },
                    { provisional.RecordingId, false },
                },
                spawnCount: 0);

            Assert.True(quicksaveObserved);
            Assert.Null(scenario.ActiveMergeJournal);
        }

        [Fact]
        public void MergeCommit_InterruptedReFlyPath_SkipsQuicksaveRefresh()
        {
            var origin = MakeRecording("rec-origin-int", "tree-refly-int", 100.0, 200.0);
            var provisional = MakeRecording("rec-provisional-int", "tree-refly-int", 100.0, 220.0);
            provisional.MergeState = MergeState.NotCommitted;
            provisional.SupersedeTargetId = origin.RecordingId;
            provisional.TerminalStateValue = TerminalState.Landed;

            var tree = MakeTree("tree-refly-int", provisional.RecordingId, origin, provisional);
            RecordingStore.StashPendingTree(tree);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = MakeMarker(
                    "sess-refly-int", "tree-refly-int", provisional.RecordingId, origin.RecordingId),
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            int quicksaveCalls = 0;
            RecordingStore.SaveGameForTesting = (saveName, saveFolder, mode) =>
            {
                if (saveName == "quicksave") quicksaveCalls++;
                return "ok";
            };
            MergeJournalOrchestrator.FaultInjectionPoint = MergeJournalOrchestrator.Phase.Finalize;
            MergeJournalOrchestrator.DurableSaveForTesting = _ => { };

            try
            {
                MergeDialog.MergeCommit(
                    tree,
                    new Dictionary<string, bool>
                    {
                        { origin.RecordingId, false },
                        { provisional.RecordingId, false },
                    },
                    spawnCount: 0);
            }
            finally
            {
                MergeJournalOrchestrator.FaultInjectionPoint = null;
            }

            Assert.Equal(0, quicksaveCalls);
            Assert.NotNull(scenario.ActiveMergeJournal);
            Assert.Equal(MergeJournal.Phases.Finalize, scenario.ActiveMergeJournal.Phase);
        }

        // ================================================================
        // 6. Null-tree safety on both branches
        // ================================================================

        [Fact]
        public void MergeCommit_NullTree_LogsWarnAndReturns()
        {
            MergeDialog.MergeCommit(null, new Dictionary<string, bool>(), spawnCount: 0);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("MergeCommit") && l.Contains("null"));
        }

        [Fact]
        public void MergeDiscard_NullTree_LogsWarnAndReturns()
        {
            MergeDialog.MergeDiscard(null);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("MergeDiscard") && l.Contains("null"));
        }
    }
}
