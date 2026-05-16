using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug fix-refly-abandon-and-fork-persist §Bug1: directly tests
    /// <c>RewindInvoker.ReapPriorProvisionalsForRp</c>, the eager
    /// pre-marker-write reap that removes prior-session NotCommitted
    /// provisionals targeting the same RewindPoint.
    /// </summary>
    [Collection("Sequential")]
    public class RewindAbandonRetryProvisionalReapTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public RewindAbandonRetryProvisionalReapTests()
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

        private static Recording Provisional(
            string id, string sessionId, string rpId, string supersedeTarget,
            string treeId = "tree_1")
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = sessionId,
                SupersedeTargetId = supersedeTarget,
                ProvisionalForRpId = rpId,
            };
        }

        [Fact]
        public void RemovesPriorNotCommitted_FromFlatList_LogsCount()
        {
            // Two abandoned attempts for the same RP, different sessions.
            // The new (third) session calls Reap before adding its own
            // provisional and both prior orphans should be removed.
            var orphanA = Provisional(
                "rec_orphanA", sessionId: "sess_a", rpId: "rp_target",
                supersedeTarget: "rec_origin");
            var orphanB = Provisional(
                "rec_orphanB", sessionId: "sess_b", rpId: "rp_target",
                supersedeTarget: "rec_origin");
            RecordingStore.AddCommittedInternal(orphanA);
            RecordingStore.AddCommittedInternal(orphanB);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            int reaped = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: "rp_target", newSessionId: "sess_new");

            Assert.Equal(2, reaped);
            Assert.Null(FindCommitted("rec_orphanA"));
            Assert.Null(FindCommitted("rec_orphanB"));
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][ReFlySession]") &&
                l.Contains("ReapPriorProvisional: removed orphan rec=rec_orphanA") &&
                l.Contains("priorSess=sess_a") &&
                l.Contains("newSess=sess_new") &&
                l.Contains("rp=rp_target"));
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][ReFlySession]") &&
                l.Contains("ReapPriorProvisional: removed orphan rec=rec_orphanB") &&
                l.Contains("priorSess=sess_b"));
        }

        [Fact]
        public void DoesNotRemoveCommittedRecordingOnSameRp()
        {
            // A CommittedProvisional recording also tagged to the RP
            // (e.g. a previously committed Re-Fly continuation) must NOT
            // be reaped — the helper is for NotCommitted orphans only.
            var sealedRec = new Recording
            {
                RecordingId = "rec_sealed",
                VesselName = "rec_sealed",
                TreeId = "tree_1",
                MergeState = MergeState.CommittedProvisional,
                ProvisionalForRpId = "rp_target",
                CreatingSessionId = "sess_old",
            };
            var orphan = Provisional(
                "rec_orphan", sessionId: "sess_old", rpId: "rp_target",
                supersedeTarget: "rec_origin");
            RecordingStore.AddCommittedInternal(sealedRec);
            RecordingStore.AddCommittedInternal(orphan);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            int reaped = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: "rp_target", newSessionId: "sess_new");

            Assert.Equal(1, reaped);
            Assert.NotNull(FindCommitted("rec_sealed"));
            Assert.Null(FindCommitted("rec_orphan"));
        }

        [Fact]
        public void DoesNotRemoveSameSessionOrDifferentRp()
        {
            // Same RP but same session — that's the new session's own
            // provisional, not an orphan.
            var ownProvisional = Provisional(
                "rec_self", sessionId: "sess_new", rpId: "rp_target",
                supersedeTarget: "rec_origin");
            // Different RP, different session — unrelated orphan.
            var unrelated = Provisional(
                "rec_other", sessionId: "sess_x", rpId: "rp_OTHER",
                supersedeTarget: "rec_origin");
            RecordingStore.AddCommittedInternal(ownProvisional);
            RecordingStore.AddCommittedInternal(unrelated);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            int reaped = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: "rp_target", newSessionId: "sess_new");

            Assert.Equal(0, reaped);
            Assert.NotNull(FindCommitted("rec_self"));
            Assert.NotNull(FindCommitted("rec_other"));
        }

        [Fact]
        public void RemovesFromCommittedTreeRecordingsDict()
        {
            // The s11 evidence case: the orphan's Recording node lives
            // in a committed tree's Recordings dict and gets re-added
            // to committedRecordings by FinalizeTreeCommit on the next
            // commit pass. Reap must clean the dict too.
            var orphan = Provisional(
                "rec_orphan", sessionId: "sess_old", rpId: "rp_target",
                supersedeTarget: "rec_origin");
            var tree = new RecordingTree { Id = "tree_1", TreeName = "TestTree" };
            tree.AddOrReplaceRecording(orphan);
            RecordingStore.AddCommittedInternal(orphan);
            RecordingStore.CommittedTrees.Add(tree);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            int reaped = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: "rp_target", newSessionId: "sess_new");

            Assert.Equal(1, reaped);
            Assert.False(tree.Recordings.ContainsKey("rec_orphan"));
            Assert.Contains(logLines, l =>
                l.Contains("[Parsek][INFO][ReFlySession]") &&
                l.Contains("ReapPriorProvisional: removed orphan rec=rec_orphan") &&
                l.Contains("removedFromCommittedTrees=1"));
        }

        [Fact]
        public void EmptyRpId_ReturnsZero()
        {
            RecordingStore.AddCommittedInternal(Provisional(
                "rec_orphan", sessionId: "sess_old", rpId: "rp_target",
                supersedeTarget: "rec_origin"));

            int reaped = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: null, newSessionId: "sess_new");

            Assert.Equal(0, reaped);
            Assert.NotNull(FindCommitted("rec_orphan"));

            reaped = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: string.Empty, newSessionId: "sess_new");
            Assert.Equal(0, reaped);
        }

        [Fact]
        public void Idempotent_SecondCallNoOp()
        {
            // After a successful reap, a second call with the same args
            // must find nothing and return 0.
            var orphan = Provisional(
                "rec_orphan", sessionId: "sess_old", rpId: "rp_target",
                supersedeTarget: "rec_origin");
            RecordingStore.AddCommittedInternal(orphan);
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            int first = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: "rp_target", newSessionId: "sess_new");
            int second = RewindInvoker.ReapPriorProvisionalsForRp(
                rpId: "rp_target", newSessionId: "sess_new");

            Assert.Equal(1, first);
            Assert.Equal(0, second);
        }

        private static Recording FindCommitted(string id)
        {
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec != null && string.Equals(rec.RecordingId, id, StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }
    }
}
