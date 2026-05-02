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

        [Fact]
        public void MergeDiscard_ReFlyInPlacePath_DoesNotRemoveOriginRecording()
        {
            const string treeId = "tree-refly-inplace-discard";
            const string sessionId = "sess-refly-inplace-discard";
            const string rpId = "rp_merge_dialog";
            const string originId = "rec-origin-inplace-discard";

            var origin = MakeRecording(originId, treeId, 100.0, 260.0);
            origin.MergeState = MergeState.NotCommitted;
            origin.CreatingSessionId = sessionId;
            origin.ProvisionalForRpId = rpId;
            origin.SupersedeTargetId = originId;

            var committedTree = MakeTree(treeId, originId, origin);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            RecordingStore.AddCommittedInternal(origin);

            var pendingTree = MakeTree(treeId, originId, origin);
            RecordingStore.StashPendingTree(pendingTree);

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp-inplace-discard",
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
                ActiveReFlySessionMarker = MakeMarker(
                    sessionId, treeId, originId, originId),
            };
            scenario.ActiveReFlySessionMarker.SupersedeTargetId = originId;
            ParsekScenario.SetInstanceForTesting(scenario);

            var preRpEvent = new GameStateEvent
            {
                ut = 150.0,
                eventType = GameStateEventType.FundsChanged,
                key = "origin-pre-rp-funds",
                recordingId = originId,
            };
            var postRpAttemptEvent = new GameStateEvent
            {
                ut = 240.0,
                eventType = GameStateEventType.FundsChanged,
                key = "origin-post-rp-attempt-funds",
                recordingId = originId,
            };
            GameStateStore.AddEvent(ref preRpEvent);
            GameStateStore.AddEvent(ref postRpAttemptEvent);
            var milestone = new Milestone
            {
                MilestoneId = "ms-inplace-origin-events",
                StartUT = 100.0,
                EndUT = 260.0,
                RecordingId = originId,
                Committed = true,
                LastReplayedEventIndex = 1,
                Events = new List<GameStateEvent>
                {
                    new GameStateEvent
                    {
                        ut = 160.0,
                        eventType = GameStateEventType.ScienceChanged,
                        key = "origin-pre-rp-milestone",
                        recordingId = originId,
                    },
                    new GameStateEvent
                    {
                        ut = 245.0,
                        eventType = GameStateEventType.ScienceChanged,
                        key = "origin-post-rp-attempt-milestone",
                        recordingId = originId,
                    },
                },
            };
            MilestoneStore.AddMilestoneForTesting(milestone);

            MergeDialog.MergeDiscard(pendingTree);

            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == treeId);
            Assert.Contains(RecordingStore.CommittedRecordings, r => ReferenceEquals(r, origin));
            Assert.Contains(GameStateStore.Events, e => e.key == "origin-pre-rp-funds");
            Assert.DoesNotContain(GameStateStore.Events, e => e.key == "origin-post-rp-attempt-funds");
            Assert.Contains(milestone.Events, e => e.key == "origin-pre-rp-milestone");
            Assert.DoesNotContain(milestone.Events, e => e.key == "origin-post-rp-attempt-milestone");
            Assert.Equal(0, milestone.LastReplayedEventIndex);
            Assert.Null(origin.CreatingSessionId);
            Assert.Null(origin.ProvisionalForRpId);
            Assert.Null(origin.SupersedeTargetId);
            Assert.False(rp.SessionProvisional);
            Assert.Null(rp.CreatingSessionId);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);
        }

        [Fact]
        public void MergeDiscard_ReFlyInPlaceDetachedPath_TrimsOriginBackToRewindPoint()
        {
            const string treeId = "tree-refly-inplace-detached-discard";
            const string sessionId = "sess-refly-inplace-detached-discard";
            const string rpId = "rp_merge_dialog";
            const string originId = "rec-origin-inplace-detached-discard";

            var origin = MakeRecording(originId, treeId, 100.0, 260.0);
            origin.MergeState = MergeState.NotCommitted;
            origin.CreatingSessionId = sessionId;
            origin.ProvisionalForRpId = rpId;
            origin.SupersedeTargetId = originId;
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

            var pendingTree = MakeTree(treeId, originId, origin);
            RecordingStore.StashPendingTree(pendingTree);

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp-inplace-detached-discard",
                UT = 130.0,
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
                ActiveReFlySessionMarker = MakeMarker(
                    sessionId, treeId, originId, originId),
            };
            scenario.ActiveReFlySessionMarker.SupersedeTargetId = originId;
            ParsekScenario.SetInstanceForTesting(scenario);

            MergeDialog.MergeDiscard(pendingTree);

            Assert.Equal(0, TreeDiscardPurge.PurgeTreeCountForTesting);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == treeId);
            Assert.Contains(RecordingStore.CommittedRecordings, r => ReferenceEquals(r, origin));
            Assert.Equal(130.0, origin.EndUT);
            Assert.DoesNotContain(origin.Points, p => p.ut > 130.0);
            Assert.Null(origin.TerminalStateValue);
            Assert.False(origin.TerminalPosition.HasValue);
            Assert.Equal(RecordingEndpointPhase.Unknown, origin.EndpointPhase);
            Assert.Null(origin.CreatingSessionId);
            Assert.Null(origin.ProvisionalForRpId);
            Assert.Null(origin.SupersedeTargetId);
            Assert.False(rp.SessionProvisional);
            Assert.Null(scenario.ActiveReFlySessionMarker);
        }

        [Fact]
        public void MergeDiscard_ReFlyInPlaceDetachedPath_RestoresPreReFlyOriginalSnapshot()
        {
            const string treeId = "tree-refly-inplace-detached-restore";
            const string sessionId = "sess-refly-inplace-detached-restore";
            const string rpId = "rp_merge_dialog";
            const string originId = "rec-origin-inplace-detached-restore";

            var origin = MakeRecording(originId, treeId, 100.0, 260.0);
            origin.MergeState = MergeState.Immutable;
            origin.ExplicitStartUT = 100.0;
            origin.ExplicitEndUT = 260.0;
            origin.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 260.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 260.0, bodyName = "Kerbin" },
                },
            });
            origin.CapturePreReFlyOriginalRecording(sessionId);

            origin.Points.Clear();
            origin.Points.Add(new TrajectoryPoint { ut = 130.0, bodyName = "Kerbin" });
            origin.TrackSections.Clear();
            origin.MergeState = MergeState.NotCommitted;
            origin.CreatingSessionId = sessionId;
            origin.ProvisionalForRpId = rpId;
            origin.SupersedeTargetId = originId;
            origin.ChildBranchPointId = "attempt-child-bp";
            origin.VesselDestroyed = true;
            origin.ExplicitStartUT = 129.5;
            origin.ExplicitEndUT = 130.0;

            var pendingTree = MakeTree(treeId, originId, origin);
            RecordingStore.StashPendingTree(pendingTree);

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = "bp-inplace-detached-restore",
                UT = 130.0,
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
                ActiveReFlySessionMarker = MakeMarker(
                    sessionId, treeId, originId, originId),
            };
            scenario.ActiveReFlySessionMarker.SupersedeTargetId = originId;
            ParsekScenario.SetInstanceForTesting(scenario);

            MergeDialog.MergeDiscard(pendingTree);

            Recording restored = RecordingStore.CommittedRecordings
                .FirstOrDefault(r => r.RecordingId == originId);
            Assert.NotNull(restored);
            Assert.NotSame(origin, restored);
            Assert.Equal(3, restored.Points.Count);
            Assert.Equal(260.0, restored.EndUT);
            Assert.Single(restored.TrackSections);
            Assert.Null(restored.ChildBranchPointId);
            Assert.False(restored.VesselDestroyed);
            Assert.Null(restored.CreatingSessionId);
            Assert.Null(restored.ProvisionalForRpId);
            Assert.Null(restored.SupersedeTargetId);
            Assert.False(restored.HasPreReFlyOriginalRecording(sessionId));
            Assert.False(rp.SessionProvisional);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("TrimInPlaceAttemptBackToOriginRewindPoint"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("RestoreInPlaceOriginalRecordingFromSnapshot")
                && l.Contains(originId)
                && l.Contains("points=3"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("User chose: Re-Fly Attempt Discard")
                && l.Contains("inPlaceOriginalRestored=True"));
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
