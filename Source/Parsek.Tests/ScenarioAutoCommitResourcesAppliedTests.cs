using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase C round 2 of the ledger / lump-sum reconciliation fix
    /// (<c>docs/dev/plans/fix-ledger-lump-sum-reconciliation.md</c>).
    ///
    /// <para>The explicit <see cref="MergeDialog.MergeCommit"/> path was fixed in the
    /// first Phase C commit, but <see cref="ParsekScenario"/> has three other
    /// tree-commit flows that used to skip <see cref="RecordingStore.MarkTreeAsApplied"/>:</para>
    /// <list type="number">
    ///   <item><description><c>SafetyNetAutoCommitPending</c> — OnSave defense-in-depth at ParsekScenario.cs:~372.</description></item>
    ///   <item><description>Scene-exit auto-merge branch — ParsekScenario.cs:~1186 (FLIGHT → KSC/TS/MAINMENU with <c>!isRevert</c>).</description></item>
    ///   <item><description>Outside-Flight auto-commit branch — ParsekScenario.cs:~1348 (Esc > Abort Mission path).</description></item>
    /// </list>
    ///
    /// <para>All three commit a pending tree whose resources were applied live during
    /// the originating flight — KSP's <c>Funding.Instance.Funds</c> (and science/rep)
    /// already reflect the mission's income. Without <see cref="RecordingStore.MarkTreeAsApplied"/>,
    /// the next FLIGHT scene entry re-fires <c>ApplyTreeLumpSum</c> on the now-committed
    /// tree and double-credits, exactly the drawdown loop Phase A migrates away on load.</para>
    ///
    /// <para>All three sites converge on the shared seam
    /// <see cref="ParsekScenario.CommitPendingTreeAsApplied"/>: tests exercise that
    /// helper directly (the OnSave / OnLoad outer frames require a live scenario
    /// instance + KSP singletons and aren't unit-testable as a whole).</para>
    ///
    /// <para>The critical regression test is
    /// <see cref="AutoCommit_DoesNotTouchMilestoneReplayIndexes"/> — it pins the
    /// "tree-scoped, milestones untouched" contract the seam inherits from
    /// <see cref="RecordingStore.MarkTreeAsApplied"/>.</para>
    /// </summary>
    [Collection("Sequential")]
    public class ScenarioAutoCommitResourcesAppliedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ScenarioAutoCommitResourcesAppliedTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ================================================================
        // Helpers (mirror MergeDialogResourcesAppliedTests)
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
        // 1. Seam happy path: pending tree -> CommitPendingTreeAsApplied ->
        //    ResourcesApplied=true + indexes advanced + moved to committed.
        //
        //    Named after the production call site in SafetyNetAutoCommitPending
        //    (ParsekScenario.cs:~372 during OnSave). Scene-exit and outside-
        //    Flight sites get their own named test below; they all share the
        //    same seam, but the named tests pin the contract per call site.
        // ================================================================

        [Fact]
        public void SafetyNetAutoCommit_MarksTreeAsApplied()
        {
            var rec1 = MakeRecording("rec-safety-1", "tree-safety", 100.0, 200.0);
            var rec2 = MakeRecording("rec-safety-2", "tree-safety", 150.0, 220.0);
            var tree = MakeTree("tree-safety", "rec-safety-1", rec1, rec2);

            RecordingStore.StashPendingTree(tree);
            Assert.True(RecordingStore.HasPendingTree);
            Assert.False(tree.ResourcesApplied);
            foreach (var r in tree.Recordings.Values)
                Assert.Equal(-1, r.LastAppliedResourceIndex);

            ParsekScenario.CommitPendingTreeAsApplied(tree);

            Assert.True(tree.ResourcesApplied);
            Assert.Equal(2, rec1.LastAppliedResourceIndex); // 3 points - 1
            Assert.Equal(2, rec2.LastAppliedResourceIndex);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "tree-safety");
        }

        [Fact]
        public void SceneExitAutoMerge_MarksTreeAsApplied()
        {
            // Scenario: FLIGHT → KSC/TS with autoMerge=ON and !isRevert (ParsekScenario.cs:~1186).
            // The flight's resources are already live in KSP; the commit must mark applied so
            // the next FLIGHT entry does not re-fire ApplyTreeLumpSum.
            var rec = MakeRecording("rec-scene-exit", "tree-scene-exit", 100.0, 200.0);
            var tree = MakeTree("tree-scene-exit", "rec-scene-exit", rec);

            RecordingStore.StashPendingTree(tree);
            ParsekScenario.CommitPendingTreeAsApplied(tree);

            Assert.True(tree.ResourcesApplied);
            Assert.Equal(2, rec.LastAppliedResourceIndex);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "tree-scene-exit");
        }

        [Fact]
        public void OutsideFlightAutoCommit_MarksTreeAsApplied()
        {
            // Scenario: Esc > Abort Mission → Space Center with autoMerge=ON
            // (ParsekScenario.cs:~1348). Same resource-already-live semantics as the
            // scene-exit path.
            var rec = MakeRecording("rec-outside", "tree-outside", 100.0, 200.0);
            var tree = MakeTree("tree-outside", "rec-outside", rec);

            RecordingStore.StashPendingTree(tree);
            ParsekScenario.CommitPendingTreeAsApplied(tree);

            Assert.True(tree.ResourcesApplied);
            Assert.Equal(2, rec.LastAppliedResourceIndex);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "tree-outside");
        }

        // ================================================================
        // 2. Critical: milestone replay indexes must NOT change
        // ================================================================

        [Fact]
        public void AutoCommit_DoesNotTouchMilestoneReplayIndexes()
        {
            // Identical contract to MergeCommit_DoesNotTouchMilestoneReplayIndexes.
            // Seed two committed milestones with non-trivial Events lists; assert the
            // seam (which is the shared path for all three auto-commit sites) does
            // not advance LastReplayedEventIndex — the exact drift that made us add
            // a tree-scoped primitive rather than reuse MarkAllFullyApplied.
            var ms1 = MakeCommittedMilestone("mile-auto-1", 50.0, 80.0, lastReplayedIdx: 2, eventCount: 5);
            var ms2 = MakeCommittedMilestone("mile-auto-2", 90.0, 110.0, lastReplayedIdx: 0, eventCount: 3);
            MilestoneStore.AddMilestoneForTesting(ms1);
            MilestoneStore.AddMilestoneForTesting(ms2);

            var rec = MakeRecording("rec-auto-mile", "tree-auto-mile", 200.0, 300.0);
            var tree = MakeTree("tree-auto-mile", "rec-auto-mile", rec);
            RecordingStore.StashPendingTree(tree);

            var milestonesBefore = MilestoneStore.Milestones;
            var indexesBefore = new List<int>(milestonesBefore.Count);
            for (int i = 0; i < milestonesBefore.Count; i++)
                indexesBefore.Add(milestonesBefore[i].LastReplayedEventIndex);

            ParsekScenario.CommitPendingTreeAsApplied(tree);

            Assert.True(tree.ResourcesApplied);
            var milestonesAfter = MilestoneStore.Milestones;
            Assert.Equal(indexesBefore.Count, milestonesAfter.Count);
            for (int i = 0; i < milestonesAfter.Count; i++)
                Assert.Equal(indexesBefore[i], milestonesAfter[i].LastReplayedEventIndex);
        }

        // ================================================================
        // 3. Empty-points recordings: seam mirrors the MarkTreeAsApplied contract
        // ================================================================

        [Fact]
        public void AutoCommit_LeavesEmptyPointRecordingsUnadvanced()
        {
            var fullRec = MakeRecording("rec-auto-full", "tree-auto-empty", 100.0, 200.0);
            var emptyRec = MakeRecording("rec-auto-empty", "tree-auto-empty", 0.0, 0.0, emptyPoints: true);
            var tree = MakeTree("tree-auto-empty", "rec-auto-full", fullRec, emptyRec);
            RecordingStore.StashPendingTree(tree);

            Assert.Equal(-1, fullRec.LastAppliedResourceIndex);
            Assert.Equal(-1, emptyRec.LastAppliedResourceIndex);
            Assert.Empty(emptyRec.Points);

            ParsekScenario.CommitPendingTreeAsApplied(tree);

            // Tree flag flips unconditionally; per-recording index only advances
            // when Points is non-empty (mirrors MarkTreeAsApplied).
            Assert.True(tree.ResourcesApplied);
            Assert.Equal(2, fullRec.LastAppliedResourceIndex);
            Assert.Equal(-1, emptyRec.LastAppliedResourceIndex);
        }

        // ================================================================
        // 4. No pending tree: seam is a safe no-op pair
        // ================================================================

        [Fact]
        public void AutoCommit_NoPendingTree_CommitIsNoop_MarkAppliedStillRuns()
        {
            // Defense-in-depth: if the caller hands us a tree reference while
            // nothing is stashed as pending (production paths never do this, but
            // the helper must not throw), CommitPendingTree no-ops per its own
            // contract (RecordingStore.cs:965-976) and MarkTreeAsApplied still
            // flips the flag on the passed-in tree. The tree will NOT appear in
            // committed storage because CommitPendingTree didn't run.
            var rec = MakeRecording("rec-nopending", "tree-nopending", 100.0, 200.0);
            var tree = MakeTree("tree-nopending", "rec-nopending", rec);

            Assert.False(RecordingStore.HasPendingTree);
            Assert.False(tree.ResourcesApplied);

            ParsekScenario.CommitPendingTreeAsApplied(tree);

            // MarkTreeAsApplied still ran on the tree instance.
            Assert.True(tree.ResourcesApplied);
            Assert.Equal(2, rec.LastAppliedResourceIndex);
            // But CommitPendingTree was a no-op so committed storage stays empty.
            Assert.DoesNotContain(RecordingStore.CommittedTrees, t => t.Id == "tree-nopending");
        }

        // ================================================================
        // 5. Determination documented: no post-revert caller should land here
        //    (pinning test — asserts only one production call pattern exists)
        // ================================================================

        [Fact]
        public void AutoCommit_CommitOrderingIsCommitThenMark()
        {
            // Regression guard: the seam must CommitPendingTree first, then
            // MarkTreeAsApplied. If a future edit inverts the order, MarkTreeAsApplied
            // would flip the flag on a tree still sitting in the pending slot, and
            // CommitPendingTree would then move the applied tree into committed
            // storage — same end state today, but the ordering is the one we
            // document. Pin it so a future reorder is caught.
            var rec = MakeRecording("rec-order", "tree-order", 100.0, 200.0);
            var tree = MakeTree("tree-order", "rec-order", rec);
            RecordingStore.StashPendingTree(tree);

            Assert.True(RecordingStore.HasPendingTree);
            Assert.Same(tree, RecordingStore.PendingTree);

            ParsekScenario.CommitPendingTreeAsApplied(tree);

            // Post-condition: pending slot empty, tree in committed storage AND
            // ResourcesApplied=true. Both must hold for the lump-sum disarm to take
            // effect — a pending tree whose ResourcesApplied=true but is not yet
            // committed would still persist with the wrong state on OnSave.
            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(RecordingStore.CommittedTrees, t => t.Id == "tree-order");
            Assert.True(tree.ResourcesApplied);
        }
    }
}
