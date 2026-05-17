using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug fix-refly-abandon-and-fork-persist §Bug2b: unit tests for
    /// <c>MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree</c>
    /// — the new TreeMerge-phase helper that copies the in-place-
    /// continuation fork's RECORDING node from the active tree into the
    /// committed tree before the splitter runs.
    /// </summary>
    [Collection("Sequential")]
    public class MergeJournalForkMigrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public MergeJournalForkMigrationTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording NewRec(string id, string treeId,
            MergeState state = MergeState.NotCommitted)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
            };
        }

        private static RecordingTree NewTree(string id, string activeId)
        {
            return new RecordingTree
            {
                Id = id,
                TreeName = id,
                ActiveRecordingId = activeId,
                RootRecordingId = activeId,
            };
        }

        private static ReFlySessionMarker NewMarker(string sessionId, string treeId,
            string forkId, bool inPlace)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = forkId,
                InPlaceContinuation = inPlace,
            };
        }

        [Fact]
        public void InPlace_AddsForkToCommittedTreeAndUpdatesActiveId()
        {
            // Committed tree starts with HEAD only; fork lives in the
            // store flat list but has not yet been added to the committed
            // tree's Recordings dict (the in-place AtomicMarkerWrite
            // attached it to PendingTree / active tree only).
            var head = NewRec("rec_head", "tree_1", MergeState.CommittedProvisional);
            var fork = NewRec("rec_fork", "tree_1", MergeState.NotCommitted);
            var committedTree = NewTree("tree_1", activeId: "rec_head");
            committedTree.AddOrReplaceRecording(head);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedInternal(fork);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());
            var marker = NewMarker("sess_1", "tree_1", "rec_fork", inPlace: true);

            MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree(
                marker, fork, ParsekScenario.Instance);

            // Fork is now in the committed tree.
            Assert.True(committedTree.Recordings.ContainsKey("rec_fork"));
            // ActiveRecordingId promoted from HEAD to fork.
            Assert.Equal("rec_fork", committedTree.ActiveRecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][MergeJournal]") &&
                l.Contains("MigrateActiveReFlyForkIntoCommittedTree: fork=rec_fork") &&
                l.Contains("activeRecordingIdBefore=rec_head") &&
                l.Contains("activeRecordingIdAfter=rec_fork") &&
                l.Contains("sess=sess_1"));
        }

        [Fact]
        public void NonInPlace_VerboseSkipsWithoutMutating()
        {
            var head = NewRec("rec_head", "tree_1", MergeState.CommittedProvisional);
            var fork = NewRec("rec_fork", "tree_1", MergeState.NotCommitted);
            var committedTree = NewTree("tree_1", activeId: "rec_head");
            committedTree.AddOrReplaceRecording(head);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());
            var marker = NewMarker("sess_1", "tree_1", "rec_fork", inPlace: false);

            MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree(
                marker, fork, ParsekScenario.Instance);

            // Committed tree unchanged.
            Assert.False(committedTree.Recordings.ContainsKey("rec_fork"));
            Assert.Equal("rec_head", committedTree.ActiveRecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][VERBOSE][MergeJournal]") &&
                l.Contains("MigrateActiveReFlyForkIntoCommittedTree: skipped") &&
                l.Contains("non-in-place"));
        }

        [Fact]
        public void NoCommittedTree_WarnsAndReturns()
        {
            var fork = NewRec("rec_fork", "tree_orphan", MergeState.NotCommitted);
            RecordingStore.AddCommittedInternal(fork);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());
            var marker = NewMarker("sess_1", "tree_orphan", "rec_fork", inPlace: true);

            MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree(
                marker, fork, ParsekScenario.Instance);

            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][WARN][MergeJournal]") &&
                l.Contains("MigrateActiveReFlyForkIntoCommittedTree: no committed tree") &&
                l.Contains("marker.TreeId=tree_orphan"));
        }

        [Fact]
        public void Idempotent_SecondCallIsNoOp()
        {
            // Crash-recovery contract: CompleteFromPostDurable's TreeMerge
            // block re-invokes the migrate helper after a crash. The
            // second call must produce no net change.
            var head = NewRec("rec_head", "tree_1", MergeState.CommittedProvisional);
            var fork = NewRec("rec_fork", "tree_1", MergeState.NotCommitted);
            var committedTree = NewTree("tree_1", activeId: "rec_head");
            committedTree.AddOrReplaceRecording(head);
            RecordingStore.AddCommittedInternal(head);
            RecordingStore.AddCommittedInternal(fork);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());
            var marker = NewMarker("sess_1", "tree_1", "rec_fork", inPlace: true);

            // First call: adds fork, promotes active id → INFO log
            // (real work happened).
            MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree(
                marker, fork, ParsekScenario.Instance);
            int recsAfterFirst = committedTree.Recordings.Count;
            string activeAfterFirst = committedTree.ActiveRecordingId;
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][MergeJournal]") &&
                l.Contains("MigrateActiveReFlyForkIntoCommittedTree") &&
                l.Contains("didWork=True"));

            int logCountBeforeSecond = logLines.Count;

            // Second call: no net change → VERBOSE log (idempotent re-run).
            MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree(
                marker, fork, ParsekScenario.Instance);

            Assert.Equal(recsAfterFirst, committedTree.Recordings.Count);
            Assert.Equal(activeAfterFirst, committedTree.ActiveRecordingId);
            Assert.Equal("rec_fork", committedTree.ActiveRecordingId);

            // The second call's migrate-helper log is VERBOSE not INFO —
            // so a crash-resumed CompleteFromPostDurable re-invocation
            // does not spam Info every time.
            var secondCallLines = logLines.GetRange(
                logCountBeforeSecond, logLines.Count - logCountBeforeSecond);
            Assert.Contains(secondCallLines, l =>
                l.Contains("[Parsek][VERBOSE][MergeJournal]") &&
                l.Contains("MigrateActiveReFlyForkIntoCommittedTree") &&
                l.Contains("didWork=False"));
            Assert.DoesNotContain(secondCallLines, l =>
                l.Contains("[Parsek][INFO][MergeJournal]") &&
                l.Contains("MigrateActiveReFlyForkIntoCommittedTree"));
        }

        [Fact]
        public void NullArgs_ReturnSilently()
        {
            // No exceptions; defensive null-guard.
            MergeJournalOrchestrator.MigrateActiveReFlyForkIntoCommittedTree(
                marker: null, provisional: null, scenario: null);
            // No assertions beyond "didn't throw".
        }
    }
}
