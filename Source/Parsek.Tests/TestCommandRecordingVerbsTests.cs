using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.4 / P5.5 payload coverage for the four recorder/tree verbs. The idempotency
    /// flags (already / idle / nothing) are the signals the orchestrator uses to tell a
    /// real action from a no-op, so their presence rules are pinned here. Fails if a
    /// no-op flag leaks onto a real action (or vice versa) or a payload key drifts.
    /// </summary>
    public class TestCommandRecordingVerbsTests
    {
        private static bool Has(List<KeyValuePair<string, string>> p, string key)
            => p.Any(kv => kv.Key == key);

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        [Fact]
        public void Start_FreshRecorder_NoAlreadyFlag()
        {
            var p = TestCommandRecordingVerbs.BuildStartPayload(alreadyLive: false, recordingId: "rec1");
            Assert.Equal("rec1", Val(p, "recordingId"));
            Assert.False(Has(p, "already"));
        }

        [Fact]
        public void Start_AlreadyLive_CarriesAlreadyTrue()
        {
            var p = TestCommandRecordingVerbs.BuildStartPayload(alreadyLive: true, recordingId: "rec1");
            Assert.Equal("rec1", Val(p, "recordingId"));
            Assert.Equal("true", Val(p, "already"));
        }

        [Fact]
        public void Start_NullRecordingId_EmptyString()
        {
            var p = TestCommandRecordingVerbs.BuildStartPayload(false, null);
            Assert.Equal(string.Empty, Val(p, "recordingId"));
        }

        [Fact]
        public void Stop_LiveRecorder_StoppedTrue_NoIdle()
        {
            var p = TestCommandRecordingVerbs.BuildStopPayload(wasLive: true);
            Assert.Equal("true", Val(p, "stopped"));
            Assert.False(Has(p, "idle"));
        }

        [Fact]
        public void Stop_NoRecorder_StoppedFalse_IdleTrue()
        {
            var p = TestCommandRecordingVerbs.BuildStopPayload(wasLive: false);
            Assert.Equal("false", Val(p, "stopped"));
            Assert.Equal("true", Val(p, "idle"));
        }

        [Fact]
        public void Commit_PayloadIsCommittedTrue()
        {
            var p = TestCommandRecordingVerbs.BuildCommitPayload();
            Assert.Equal("true", Val(p, "committed"));
        }

        [Fact]
        public void Discard_WithTree_DiscardedTrue()
        {
            var p = TestCommandRecordingVerbs.BuildDiscardPayload(hadTree: true);
            Assert.Equal("true", Val(p, "discarded"));
            Assert.False(Has(p, "nothing"));
        }

        [Fact]
        public void Discard_NoTree_NothingTrue()
        {
            var p = TestCommandRecordingVerbs.BuildDiscardPayload(hadTree: false);
            Assert.Equal("true", Val(p, "nothing"));
            Assert.False(Has(p, "discarded"));
        }

        // ----- SelectDiscardReapRecordings (S0.5 discard-residue fix) -----
        // The reap guard is what keeps the discard-sidecar cleanup from ever touching
        // a committed original: only ids ABSENT from the post-discard known set reap.

        private static Recording Rec(string id) => new Recording { RecordingId = id };

        [Fact]
        public void Reap_UncommittedId_Selected()
        {
            var reap = TestCommandRecordingVerbs.SelectDiscardReapRecordings(
                new List<Recording> { Rec("aaaa1111") }, new HashSet<string>());
            Assert.Single(reap);
            Assert.Equal("aaaa1111", reap[0].RecordingId);
        }

        [Fact]
        public void Reap_KnownId_Skipped_CommittedRestoreCloneSafe()
        {
            // A committed-restore clone shares the committed original's id; that id
            // stays known after the discard, so its files must never be selected.
            var reap = TestCommandRecordingVerbs.SelectDiscardReapRecordings(
                new List<Recording> { Rec("aaaa1111"), Rec("bbbb2222") },
                new HashSet<string> { "aaaa1111" });
            Assert.Single(reap);
            Assert.Equal("bbbb2222", reap[0].RecordingId);
        }

        [Fact]
        public void Reap_NullAndEmptyIdEntries_Skipped()
        {
            var reap = TestCommandRecordingVerbs.SelectDiscardReapRecordings(
                new List<Recording> { null, Rec(null), Rec(""), Rec("cccc3333") },
                new HashSet<string>());
            Assert.Single(reap);
            Assert.Equal("cccc3333", reap[0].RecordingId);
        }

        [Fact]
        public void Reap_NullInputs_EmptyResult_FailClosed()
        {
            Assert.Empty(TestCommandRecordingVerbs.SelectDiscardReapRecordings(null, new HashSet<string>()));
            // Null known set fails CLOSED (deletion guard convention): nothing reaps.
            // The empty-store stranded case passes an EMPTY set, which still reaps.
            Assert.Empty(TestCommandRecordingVerbs.SelectDiscardReapRecordings(
                new List<Recording> { Rec("dddd4444") }, null));
        }

        // ----- DiscardReapSkipReason (Fable review of PR #1328, finding 1) -----
        // In the Re-Fly / merge-journal / restore-in-progress load shapes the active
        // tree holds the ONLY copy of committed recordings whose ids are absent from
        // the known set; the reap must stand down entirely there.

        [Fact]
        public void ReapGate_NormalDiscard_NoSkip()
        {
            Assert.Null(TestCommandRecordingVerbs.DiscardReapSkipReason(
                reFlyMarkerActive: false, mergeJournalActive: false, restoringActiveTree: false));
        }

        [Theory]
        [InlineData(true, false, false, "refly-marker-active")]
        [InlineData(false, true, false, "merge-journal-active")]
        [InlineData(false, false, true, "restoring-active-tree")]
        [InlineData(true, true, true, "refly-marker-active")]
        public void ReapGate_UnsafeShapes_Skip(
            bool reFly, bool journal, bool restoring, string expectedReason)
        {
            Assert.Equal(expectedReason, TestCommandRecordingVerbs.DiscardReapSkipReason(
                reFlyMarkerActive: reFly, mergeJournalActive: journal, restoringActiveTree: restoring));
        }
    }
}
