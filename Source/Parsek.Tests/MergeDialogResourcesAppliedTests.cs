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
    /// <para>The merge dialog used to commit a tree via <see cref="RecordingStore.CommitPendingTree"/>
    /// without setting <c>tree.ResourcesApplied=true</c>. The next FLIGHT scene
    /// then re-fired <c>ApplyTreeLumpSum</c> on the now-committed tree, producing
    /// the "suspicious drawdown" WARN that Phase A migrates away on load. The
    /// in-flight commit path (<see cref="ParsekFlight.CommitTreeFlight"/>) already
    /// did the equivalent inline; this phase brings the merge dialog into parity
    /// by routing both through the new <see cref="RecordingStore.MarkTreeAsApplied"/>
    /// primitive.</para>
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
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
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

        private static Milestone MakeCommittedMilestone(string id, double startUT, double endUT,
            int lastReplayedIdx, int eventCount)
        {
            var m = new Milestone
            {
                MilestoneId = id,
                StartUT = startUT,
                EndUT = endUT,
                Epoch = MilestoneStore.CurrentEpoch,
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
        public void MergeCommit_AdvancesResourcesAppliedAndIndexesAndMovesToCommitted()
        {
            var rec1 = MakeRecording("rec-a", "tree-merge-1", 100.0, 200.0);
            var rec2 = MakeRecording("rec-b", "tree-merge-1", 150.0, 220.0);
            var rec3 = MakeRecording("rec-c", "tree-merge-1", 180.0, 250.0);
            var tree = MakeTree("tree-merge-1", "rec-a", rec1, rec2, rec3);

            // Precondition: stash as pending. ResourcesApplied=false and indexes start at -1.
            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.False(tree.ResourcesApplied);
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

            // ResourcesApplied flipped, every recording's index advanced to last point.
            Assert.True(tree.ResourcesApplied);
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

            // After: tree marked applied, milestone indexes unchanged.
            Assert.True(tree.ResourcesApplied);
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

            // Tree-level flag is set unconditionally; per-recording indexes only advance
            // when Points is non-empty (mirrors the MarkTreeAsApplied contract that
            // LegacyTreeMigrationTests pins separately).
            Assert.True(tree.ResourcesApplied);
            Assert.Equal(2, fullRec.LastAppliedResourceIndex);
            Assert.Equal(-1, emptyRec.LastAppliedResourceIndex);
        }

        // ================================================================
        // 4. Discard-branch parity: discard does NOT mark applied
        // ================================================================

        [Fact]
        public void MergeDiscard_DoesNotMarkResourcesApplied()
        {
            var rec = MakeRecording("rec-discard", "tree-discard", 100.0, 200.0);
            var tree = MakeTree("tree-discard", "rec-discard", rec);

            // Sanity: ResourcesApplied is false before and stays false after a discard.
            // If a future change accidentally wires MarkTreeAsApplied into the wrong
            // branch (Discard instead of Merge), this assertion fails.
            Assert.False(tree.ResourcesApplied);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);

            RecordingStore.StashPendingTree(tree);
            MergeDialog.MergeDiscard(tree);

            Assert.False(tree.ResourcesApplied);
            Assert.Equal(-1, rec.LastAppliedResourceIndex);

            // Tree should be wiped from the pending slot and never make it into
            // committed storage.
            Assert.False(RecordingStore.HasPendingTree);
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == "tree-discard");
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
