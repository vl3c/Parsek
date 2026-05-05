using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase C of the ledger / lump-sum reconciliation fix
    /// (<c>docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md</c>).
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
            RewindPointReaper.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
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
            RewindPointReaper.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
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

        [Fact]
        public void MergeDiscard_ReFlyMarkerTreeMismatch_FallsBackToRegularTreeDiscard()
        {
            var rec = MakeRecording("rec-mismatch-discard", "tree-dialog-discard", 100.0, 200.0);
            var tree = MakeTree("tree-dialog-discard", "rec-mismatch-discard", rec);
            RecordingStore.StashPendingTree(tree);

            var marker = MakeMarker(
                "sess-mismatch-discard",
                "tree-other-refly",
                "rec-other-attempt",
                "rec-other-origin");
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            TreeDiscardPurge.ResetCallCountForTesting();

            MergeDialog.MergeDiscard(tree);

            Assert.Equal(1, TreeDiscardPurge.PurgeTreeCountForTesting);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Same(marker, scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("does not match dialog tree")
                && l.Contains("falling back to regular tree discard"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("User chose: Tree Discard"));
        }

        [Fact]
        public void MergeDiscard_ReFlyPath_PreservesCommittedMissionTreeAndRp()
        {
            const string treeId = "tree-refly-discard";
            const string sessionId = "sess-refly-discard";
            const string rpId = "rp_merge_dialog";
            const string bpId = "bp-refly-discard";

            var origin = MakeRecording("rec-origin-discard", treeId, 100.0, 200.0);
            origin.MergeState = MergeState.Immutable;
            var provisional = MakeRecording("rec-refly-attempt-discard", treeId, 200.0, 260.0);
            provisional.MergeState = MergeState.NotCommitted;
            provisional.CreatingSessionId = sessionId;
            provisional.ProvisionalForRpId = rpId;
            provisional.SupersedeTargetId = origin.RecordingId;

            var committedTree = MakeTree(treeId, origin.RecordingId, origin);
            committedTree.BranchPoints.Add(new BranchPoint
            {
                Id = bpId,
                Type = BranchPointType.Terminal,
                RewindPointId = rpId,
                ChildRecordingIds = new List<string> { origin.RecordingId },
            });
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddProvisional(provisional);

            var pendingTree = MakeTree(treeId, provisional.RecordingId, origin, provisional);
            RecordingStore.StashPendingTree(pendingTree);

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                SessionProvisional = true,
                CreatingSessionId = sessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = origin.RecordingId },
                },
            };
            var relation = new RecordingSupersedeRelation
            {
                RelationId = "rsr-existing",
                OldRecordingId = origin.RecordingId,
                NewRecordingId = "rec-existing-successor",
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation> { relation },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
                ActiveReFlySessionMarker = MakeMarker(
                    sessionId, treeId, provisional.RecordingId, origin.RecordingId),
            };
            scenario.ActiveReFlySessionMarker.SupersedeTargetId = origin.RecordingId;
            ParsekScenario.SetInstanceForTesting(scenario);

            var evt = new GameStateEvent
            {
                ut = 230.0,
                eventType = GameStateEventType.FundsChanged,
                key = "attempt-funds",
                recordingId = provisional.RecordingId,
            };
            GameStateStore.AddEvent(ref evt);

            MergeDialog.MergeDiscard(pendingTree);

            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == treeId);
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == origin.RecordingId);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.RecordingId == provisional.RecordingId);
            Assert.DoesNotContain(GameStateStore.Events, e => e.recordingId == provisional.RecordingId);
            Assert.Single(scenario.RewindPoints);
            Assert.Same(rp, scenario.RewindPoints[0]);
            Assert.False(rp.SessionProvisional);
            Assert.Null(rp.CreatingSessionId);
            Assert.Contains(scenario.RecordingSupersedes, r => ReferenceEquals(r, relation));
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);
        }

        [Fact]
        public void MergeDiscard_ReFlyPath_RestoresSanitizedTreeWhenCommittedCopyDetached()
        {
            const string treeId = "tree-refly-detached-discard";
            const string sessionId = "sess-refly-detached-discard";
            const string rpId = "rp_merge_dialog";

            var origin = MakeRecording("rec-origin-detached-discard", treeId, 100.0, 200.0);
            origin.MergeState = MergeState.Immutable;
            var provisional = MakeRecording("rec-refly-attempt-detached-discard", treeId, 200.0, 260.0);
            provisional.MergeState = MergeState.NotCommitted;
            provisional.CreatingSessionId = sessionId;
            provisional.ProvisionalForRpId = rpId;
            provisional.SupersedeTargetId = origin.RecordingId;

            // Re-Fly load can detach the committed tree while the restored active
            // tree lives in the pending slot. Discard must restore the sanitized
            // original tree, not merely pop the only in-memory tree copy.
            RecordingStore.AddProvisional(provisional);
            var pendingTree = MakeTree(treeId, provisional.RecordingId, origin, provisional);
            pendingTree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-detached-discard",
                Type = BranchPointType.Terminal,
                ChildRecordingIds = new List<string>
                {
                    origin.RecordingId,
                    provisional.RecordingId,
                },
            });
            RecordingStore.StashPendingTree(pendingTree);

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp-detached-discard",
                UT = 125.0,
                SessionProvisional = true,
                CreatingSessionId = sessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = origin.RecordingId },
                },
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
                ActiveReFlySessionMarker = MakeMarker(
                    sessionId, treeId, provisional.RecordingId, origin.RecordingId),
            };
            scenario.ActiveReFlySessionMarker.SupersedeTargetId = origin.RecordingId;
            ParsekScenario.SetInstanceForTesting(scenario);

            var historicalEvent = new GameStateEvent
            {
                ut = 150.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "detached-restore-should-not-milestone",
                recordingId = "",
            };
            GameStateStore.AddEvent(ref historicalEvent);

            MergeDialog.MergeDiscard(pendingTree);

            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == treeId);
            Assert.Contains(RecordingStore.CommittedRecordings, r => r.RecordingId == origin.RecordingId);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings, r => r.RecordingId == provisional.RecordingId);
            Assert.Empty(MilestoneStore.Milestones);
            Assert.Contains(GameStateStore.Events, e => e.key == "detached-restore-should-not-milestone");
            Assert.DoesNotContain(pendingTree.Recordings.Keys, id => id == provisional.RecordingId);
            Assert.DoesNotContain(
                pendingTree.BranchPoints[0].ChildRecordingIds,
                id => id == provisional.RecordingId);
            Assert.False(rp.SessionProvisional);
            Assert.Null(scenario.ActiveReFlySessionMarker);
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

        // ================================================================
        // 7. Issue #734: in-place fork model -- discard removes the fork
        //    and leaves origin (and its sidecar trajectory) intact
        // ================================================================

        /// <summary>
        /// Issue #734 regression: a long-running in-place Re-Fly attempt
        /// records new trajectory into the FORK recording, not into origin.
        /// On Discard, the fork is removed from the committed list (and from
        /// the pending tree), and origin keeps its full pre-Re-Fly Points /
        /// TrackSections / terminal state untouched. Before #734 the recorder
        /// rebound to origin and Discard had to either roll the origin back
        /// from a snapshot or trim it -- both of which collapsed the recorded
        /// data when anything went wrong (issue #733).
        /// </summary>
        [Fact]
        public void MergeDiscard_ReFlyInPlaceFork_RemovesForkLeavesOriginIntact()
        {
            const string treeId = "tree-734-fork-discard";
            const string sessionId = "sess-734-fork-discard";
            const string rpId = "rp_734_fork";
            const string originId = "rec-734-origin";
            const string forkId = "rec-734-fork";

            // Origin: a finished landed flight with two real points and one
            // TrackSection. Captures the contract that origin's payload must
            // survive Discard byte-for-byte.
            var origin = MakeRecording(originId, treeId, 100.0, 200.0);
            origin.MergeState = MergeState.Immutable;
            origin.ExplicitStartUT = 100.0;
            origin.ExplicitEndUT = 200.0;
            origin.TerminalStateValue = TerminalState.Landed;
            origin.TerminalPosition = new SurfacePosition
            {
                body = "Kerbin",
                latitude = 1.0,
                longitude = 2.0,
                altitude = 3.0,
                situation = SurfaceSituation.Landed,
            };
            origin.EndpointPhase = RecordingEndpointPhase.SurfacePosition;
            origin.EndpointBodyName = "Kerbin";
            origin.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 200.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 200.0, bodyName = "Kerbin" },
                },
            });
            int originPointCountBefore = origin.Points.Count;
            int originTrackSectionCountBefore = origin.TrackSections.Count;
            TerminalState? originTerminalBefore = origin.TerminalStateValue;

            // Fork: the active provisional recording the recorder appended to
            // during the long-running in-place attempt. Inherits origin's
            // vessel identity but carries a NEW recording id and its own
            // (post-Re-Fly) trajectory data.
            var fork = MakeRecording(forkId, treeId, 200.0, 360.0);
            fork.MergeState = MergeState.NotCommitted;
            fork.CreatingSessionId = sessionId;
            fork.ProvisionalForRpId = rpId;
            fork.SupersedeTargetId = originId;
            fork.VesselPersistentId = origin.VesselPersistentId;
            fork.VesselName = origin.VesselName;
            fork.TerminalStateValue = TerminalState.Destroyed;
            // Mirror what AtomicMarkerWrite does: copy origin's frozen
            // trajectory under the fork's own pre-Re-Fly anchor snapshot so
            // the resolver / display alignment paths keyed by
            // ActiveReFlyRecordingId still see the original frozen trajectory.
            fork.CapturePreReFlyAnchorTrajectoryFrom(origin, sessionId);

            // Tree topology mirrors AtomicMarkerWrite: origin is in the
            // committed tree (immutable mission history), the FORK lives in
            // the pending tree only as the active provisional recording the
            // recorder flushes into. Both reference the same origin instance
            // so the assertions can pin origin's payload byte-for-byte.
            var committedTree = MakeTree(treeId, originId, origin);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddProvisional(fork);

            var pendingTree = MakeTree(treeId, forkId, origin, fork);
            RecordingStore.StashPendingTree(pendingTree);

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp-734-fork",
                UT = 200.0,
                SessionProvisional = true,
                CreatingSessionId = sessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = originId },
                },
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
                ActiveReFlySessionMarker = MakeMarker(sessionId, treeId, forkId, originId),
            };
            scenario.ActiveReFlySessionMarker.SupersedeTargetId = originId;
            scenario.ActiveReFlySessionMarker.InPlaceContinuation = true;
            ParsekScenario.SetInstanceForTesting(scenario);

            MergeDialog.MergeDiscard(pendingTree);

            // Origin survives the discard byte-for-byte: same instance, same
            // points, same TrackSections, same terminal state.
            Assert.Contains(RecordingStore.CommittedRecordings,
                r => ReferenceEquals(r, origin));
            Assert.Equal(originPointCountBefore, origin.Points.Count);
            Assert.Equal(originTrackSectionCountBefore, origin.TrackSections.Count);
            Assert.Equal(originTerminalBefore, origin.TerminalStateValue);
            Assert.Equal("Kerbin", origin.EndpointBodyName);
            // No legacy trim-back path runs in fork mode because the fork
            // owns the attempt's data.
            // Legacy in-place trim and snapshot-restore code paths are gone
            // entirely; assertions on their log lines would only catch the
            // empty list now and provide no value.

            // Fork is gone from both committed list and the pending tree.
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r.RecordingId == forkId);

            // Marker, pending tree slot, scenario journal are cleared.
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("User chose: Re-Fly Attempt Discard"));
        }

        /// <summary>
        /// Issue #734 reviewer P1: when AtomicMarkerWrite has to attach the
        /// in-place fork to a committed tree because no pending tree exists
        /// at marker-write time, Discard must still drop the fork. The
        /// pre-fix CommittedTreeContainsRecording guard treated the fork id
        /// as protected mission history and excluded it from attemptIds, so
        /// the fork survived in <see cref="RecordingStore.CommittedRecordings"/>
        /// and in the committed tree's <c>Recordings</c> dictionary - OnSave
        /// then serialised the abandoned attempt as committed history.
        /// Post-fix the guard recognises the marker-owned NotCommitted fork
        /// and the new <c>PruneAttemptRecordingsFromCommittedTrees</c> step
        /// strips the dictionary entry.
        /// </summary>
        [Fact]
        public void MergeDiscard_ReFlyInPlaceForkAttachedToCommittedTree_RemovesForkAndPrunesTreeEntry()
        {
            const string treeId = "tree-734-committed-attach";
            const string sessionId = "sess-734-committed-attach";
            const string rpId = "rp_734_committed_attach";
            const string originId = "rec-734-committed-origin";
            const string forkId = "rec-734-committed-fork";

            var origin = MakeRecording(originId, treeId, 100.0, 200.0);
            origin.MergeState = MergeState.Immutable;
            origin.ExplicitStartUT = 100.0;
            origin.ExplicitEndUT = 200.0;
            origin.TerminalStateValue = TerminalState.Landed;
            origin.EndpointPhase = RecordingEndpointPhase.SurfacePosition;
            origin.EndpointBodyName = "Kerbin";
            int originPointCountBefore = origin.Points.Count;
            TerminalState? originTerminalBefore = origin.TerminalStateValue;

            var fork = MakeRecording(forkId, treeId, 200.0, 360.0);
            fork.MergeState = MergeState.NotCommitted;
            fork.CreatingSessionId = sessionId;
            fork.ProvisionalForRpId = rpId;
            fork.SupersedeTargetId = originId;
            fork.VesselPersistentId = origin.VesselPersistentId;
            fork.VesselName = origin.VesselName;
            fork.TerminalStateValue = TerminalState.Destroyed;

            // Committed-tree-attach shape: NO pending tree. The fork lives
            // inside the SAME committed tree as origin because
            // FindTreeForReFlyFork(originChild.TreeId) found the committed
            // tree first when no pending tree was stashed.
            var committedTree = MakeTree(treeId, forkId, origin, fork);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddProvisional(fork);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.True(committedTree.Recordings.ContainsKey(forkId));

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp-734-committed-attach",
                UT = 200.0,
                SessionProvisional = true,
                CreatingSessionId = sessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = originId },
                },
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
                ActiveReFlySessionMarker = MakeMarker(sessionId, treeId, forkId, originId),
            };
            scenario.ActiveReFlySessionMarker.SupersedeTargetId = originId;
            scenario.ActiveReFlySessionMarker.InPlaceContinuation = true;
            ParsekScenario.SetInstanceForTesting(scenario);

            // Discard runs against the committed tree (the dialog tree in
            // this shape because no pending tree exists).
            MergeDialog.MergeDiscard(committedTree);

            // Origin survives: same instance, untouched payload, still in
            // both committed list and the committed tree's Recordings dict.
            Assert.Contains(RecordingStore.CommittedRecordings,
                r => ReferenceEquals(r, origin));
            Assert.Equal(originPointCountBefore, origin.Points.Count);
            Assert.Equal(originTerminalBefore, origin.TerminalStateValue);
            Assert.True(committedTree.Recordings.ContainsKey(originId));
            Assert.Same(origin, committedTree.Recordings[originId]);

            // Fork is gone from the committed list AND from the committed
            // tree's Recordings dictionary. The dictionary prune is the
            // critical assertion - pre-fix the guard kept the fork id out
            // of attemptIds, so neither RemoveCommittedAttemptRecordings
            // nor any tree pruning ran for it.
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r.RecordingId == forkId);
            Assert.False(committedTree.Recordings.ContainsKey(forkId));

            Assert.Null(scenario.ActiveReFlySessionMarker);

            // The two new log lines: the guard inclusion path (cites the
            // marker-owned fork id) + the per-tree prune summary (cites the
            // tree id and the recording-prune count).
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("marker-owned")
                && l.Contains(forkId));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("PruneAttemptRecordingsFromCommittedTrees")
                && l.Contains("tree=" + treeId)
                && l.Contains("prunedRecordings=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("User chose: Re-Fly Attempt Discard"));
        }

        /// <summary>
        /// Issue #734 reviewer P1 (round 4): when the in-place attempt is
        /// attached to a committed tree (no pending tree existed) AND mutates
        /// that committed tree's topology before Discard - i.e. the abandoned
        /// attempt creates a new branch point such as an in-flight stage
        /// separation - the prune step must also remove the session-authored
        /// branch point and scrub any references to attempt recordings from
        /// surviving branch points. Pre-fix, only the recording dictionary
        /// entry was removed; the abandoned branch point and dangling parent
        /// id refs to deleted attempt recordings would have been serialised
        /// by OnSave as committed mission topology.
        /// </summary>
        [Fact]
        public void MergeDiscard_ReFlyInPlaceForkInCommittedTreeWithSessionBranchPoint_PrunesBranchPointAndScrubsRefs()
        {
            const string treeId = "tree-734-bp-attach";
            const string sessionId = "sess-734-bp-attach";
            const string rpId = "rp_734_bp_attach";
            const string originId = "rec-734-bp-origin";
            const string preSessionDebrisId = "rec-734-bp-pre-debris";
            const string preSessionBpId = "bp-734-pre-session";
            const string forkId = "rec-734-bp-fork";
            const string attemptDebrisId = "rec-734-bp-attempt-debris";
            const string sessionAuthoredBpId = "bp-734-session-authored";

            // Origin: immutable, with a pre-session debris child branched off
            // a pre-session BranchPoint. Both must survive Discard intact.
            var origin = MakeRecording(originId, treeId, 100.0, 200.0);
            origin.MergeState = MergeState.Immutable;
            origin.ChildBranchPointId = preSessionBpId;

            var preSessionDebris = MakeRecording(preSessionDebrisId, treeId, 150.0, 200.0);
            preSessionDebris.MergeState = MergeState.Immutable;
            preSessionDebris.ParentBranchPointId = preSessionBpId;

            var preSessionBp = new BranchPoint
            {
                Id = preSessionBpId,
                UT = 150.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { originId },
                ChildRecordingIds = new List<string> { preSessionDebrisId },
            };

            // Fork: in-place attempt provisional, attached to the committed
            // tree because no pending tree existed at marker-write time.
            var fork = MakeRecording(forkId, treeId, 200.0, 360.0);
            fork.MergeState = MergeState.NotCommitted;
            fork.CreatingSessionId = sessionId;
            fork.ProvisionalForRpId = rpId;
            fork.SupersedeTargetId = originId;
            fork.VesselPersistentId = origin.VesselPersistentId;
            fork.VesselName = origin.VesselName;
            fork.TerminalStateValue = TerminalState.Destroyed;

            // Session-authored BranchPoint: stage separation booked DURING
            // the in-place attempt. Not present in PreSessionBranchPointIds,
            // so PruneSessionCreatedBranchPoints must drop it.
            var sessionAuthoredBp = new BranchPoint
            {
                Id = sessionAuthoredBpId,
                UT = 280.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { forkId },
                ChildRecordingIds = new List<string> { attemptDebrisId },
            };

            // Attempt-authored child recording: a debris fragment created
            // by the production helper ParsekFlight.CreateBreakupChildRecording
            // off the session-authored BranchPoint. The production shape sets
            // ONLY ParentBranchPointId + Generation - it does NOT propagate
            // marker.SessionId / RewindPointId / SupersedeTargetId, so the
            // marker-tag scan in CollectReFlyAttemptOwnedRecordingIds cannot
            // see this recording. The session-BP-descendant walk is the only
            // path that can reach it. The recording lives only in
            // tree.Recordings (production: tree.AddOrReplaceRecording, no
            // RecordingStore.AddProvisional), since the merge dialog now runs
            // pre-transition before any OnSave fires that would also push the
            // child into CommittedRecordings.
            var attemptDebris = MakeRecording(attemptDebrisId, treeId, 280.0, 360.0);
            attemptDebris.MergeState = MergeState.NotCommitted;
            attemptDebris.ParentBranchPointId = sessionAuthoredBpId;

            // Committed tree topology: origin + pre-session debris + fork +
            // attempt debris + both BranchPoints. This is the state OnSave
            // would write if the discard helper only removed Recordings dict
            // entries (the bug).
            var committedTree = MakeTree(treeId, forkId,
                origin, preSessionDebris, fork, attemptDebris);
            committedTree.BranchPoints.Add(preSessionBp);
            committedTree.BranchPoints.Add(sessionAuthoredBp);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedInternal(preSessionDebris);
            RecordingStore.AddProvisional(fork);
            // Intentionally do NOT AddProvisional(attemptDebris) - production
            // breakup children are not in the flat committed list during the
            // live session.
            Assert.False(RecordingStore.HasPendingTree);

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp-734-bp-attach",
                UT = 200.0,
                SessionProvisional = true,
                CreatingSessionId = sessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = originId },
                },
            };
            var marker = MakeMarker(sessionId, treeId, forkId, originId);
            marker.SupersedeTargetId = originId;
            marker.InPlaceContinuation = true;
            // PreSessionBranchPointIds baseline contains ONLY the pre-session
            // BranchPoint, mirroring SnapshotTreeBranchPointIds at marker
            // write time.
            marker.PreSessionBranchPointIds = new List<string> { preSessionBpId };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { rp },
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            MergeDialog.MergeDiscard(committedTree);

            // Origin + pre-session debris: untouched. Pre-session BranchPoint:
            // still present, still wired to its real parents/children.
            Assert.Contains(RecordingStore.CommittedRecordings,
                r => ReferenceEquals(r, origin));
            Assert.Contains(RecordingStore.CommittedRecordings,
                r => ReferenceEquals(r, preSessionDebris));
            Assert.True(committedTree.Recordings.ContainsKey(originId));
            Assert.True(committedTree.Recordings.ContainsKey(preSessionDebrisId));
            Assert.Contains(committedTree.BranchPoints,
                bp => bp.Id == preSessionBpId);
            var survivingPreSessionBp = committedTree.BranchPoints
                .First(bp => bp.Id == preSessionBpId);
            Assert.Contains(originId, survivingPreSessionBp.ParentRecordingIds);
            Assert.Contains(preSessionDebrisId, survivingPreSessionBp.ChildRecordingIds);

            // Fork: gone from CommittedRecordings AND committedTree.Recordings.
            // attemptDebris was never in CommittedRecordings (production
            // shape) but was in committedTree.Recordings - the dict-prune
            // is the load-bearing assertion.
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r.RecordingId == forkId);
            Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                r => r.RecordingId == attemptDebrisId);
            Assert.False(committedTree.Recordings.ContainsKey(forkId));
            Assert.False(committedTree.Recordings.ContainsKey(attemptDebrisId));

            // Session-authored BranchPoint: removed from the tree. No
            // surviving BranchPoint references either attempt id.
            Assert.DoesNotContain(committedTree.BranchPoints,
                bp => bp.Id == sessionAuthoredBpId);
            foreach (var bp in committedTree.BranchPoints)
            {
                Assert.DoesNotContain(forkId, bp.ParentRecordingIds);
                Assert.DoesNotContain(forkId, bp.ChildRecordingIds);
                Assert.DoesNotContain(attemptDebrisId, bp.ParentRecordingIds);
                Assert.DoesNotContain(attemptDebrisId, bp.ChildRecordingIds);
            }

            // Marker cleared.
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // The session-BP-descendant walk reports adopting attemptDebris
            // explicitly (the only path that can reach it given the
            // production-shape un-tagged child).
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("AddSessionBranchPointDescendantAttemptIds")
                && l.Contains("adopted 1"));

            // Per-tree prune summary records all counters - prunedRecordings=2
            // (fork + attempt debris), removedSessionBranchPoints=1
            // (sessionAuthoredBp).
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("PruneAttemptRecordingsFromCommittedTrees")
                && l.Contains("tree=" + treeId)
                && l.Contains("prunedRecordings=2")
                && l.Contains("removedSessionBranchPoints=1"));
        }

        // ================================================================
        // 8. Merge-journal-active discard guard (Opus pass-6 P1.C)
        // ================================================================

        [Fact]
        public void MergeDiscardWithResult_JournalActive_RefusesAndReturnsFalse()
        {
            var rec = MakeRecording("rec-journal-discard", "tree-journal-discard", 100.0, 200.0);
            var tree = MakeTree("tree-journal-discard", "rec-journal-discard", rec);
            RecordingStore.StashPendingTree(tree);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveMergeJournal = new MergeJournal
                {
                    JournalId = "journal_test",
                    SessionId = "sess_test",
                    Phase = MergeJournal.Phases.Supersede,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            bool result = MergeDialog.MergeDiscardWithResult(tree);

            Assert.False(result);
            // Pending tree must remain stashed - the refusal is a no-op
            // for state.
            Assert.True(RecordingStore.HasPendingTree);
            // Journal stays armed - we did NOT clobber it.
            Assert.NotNull(scenario.ActiveMergeJournal);
            Assert.Equal("journal_test", scenario.ActiveMergeJournal.JournalId);
            // Refusal logs and screen message.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("MergeDiscard")
                && l.Contains("merge journal active"));
        }

        [Fact]
        public void MergeDiscard_JournalActive_RefusesAndDoesNotClobberJournal()
        {
            // Same scenario via the void overload (post-load deferred path).
            var rec = MakeRecording("rec-journal-discard-void", "tree-journal-discard-void", 100.0, 200.0);
            var tree = MakeTree("tree-journal-discard-void", "rec-journal-discard-void", rec);
            RecordingStore.StashPendingTree(tree);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveMergeJournal = new MergeJournal
                {
                    JournalId = "journal_test_void",
                    SessionId = "sess_test_void",
                    Phase = MergeJournal.Phases.Supersede,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            MergeDialog.MergeDiscard(tree);

            Assert.True(RecordingStore.HasPendingTree);
            Assert.NotNull(scenario.ActiveMergeJournal);
        }

        [Fact]
        public void TryDiscardActiveReFlyAttempt_JournalActive_RefusesAndReturnsFalse()
        {
            // Direct call (test bypass / future caller). The MergeDiscard
            // gate above is the primary guard; this is belt-and-braces.
            var rec = MakeRecording("rec-journal-refly", "tree-journal-refly", 100.0, 200.0);
            var tree = MakeTree("tree-journal-refly", "rec-journal-refly", rec);

            var marker = MakeMarker(
                "sess-journal-refly",
                "tree-journal-refly",
                "rec-journal-refly",
                "rec-journal-origin");
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
                ActiveMergeJournal = new MergeJournal
                {
                    JournalId = "journal_refly",
                    SessionId = "sess-journal-refly",
                    Phase = MergeJournal.Phases.Supersede,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            bool result = MergeDialog.TryDiscardActiveReFlyAttempt(tree);

            Assert.False(result);
            Assert.NotNull(scenario.ActiveMergeJournal);
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("TryDiscardActiveReFlyAttempt")
                && l.Contains("merge journal active"));
        }

        [Fact]
        public void MergeDiscardWithResult_NormalDiscard_ReturnsTrue()
        {
            // Sanity: bool overload returns true on the normal path so
            // the pre-transition wrapper proceeds with postChoice.
            var rec = MakeRecording("rec-normal-discard", "tree-normal-discard", 100.0, 200.0);
            var tree = MakeTree("tree-normal-discard", "rec-normal-discard", rec);
            RecordingStore.StashPendingTree(tree);

            bool result = MergeDialog.MergeDiscardWithResult(tree);

            Assert.True(result);
            Assert.False(RecordingStore.HasPendingTree);
        }
    }
}
