using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the crash-recovery decision
    /// (<see cref="TestCommandJournal.ReplayIntoPhaseMap"/> +
    /// <see cref="TestCommandJournal.DecideRecovery"/>). This is the core
    /// at-most-once guarantee: a non-idempotent command runs zero-or-one times,
    /// never twice. The matrix covers every journal-replay cell including the
    /// long-running LoadGame boot rows (CLAIMED never re-initiates, EXECUTED
    /// rewrites, DONE skips), duplicate ids, and a torn trailing line.
    /// </summary>
    public class TestCommandRecoveryTests
    {
        // ----- Phase -> action, direct -----

        [Fact]
        public void FreshId_NoJournal_Executes()
        {
            Assert.Equal(RecoveryAction.Execute,
                TestCommandJournal.DecideRecovery("x", JournalPhase.None));
        }

        [Fact]
        public void Claimed_Interrupted_NoReExecute()
        {
            Assert.Equal(RecoveryAction.Interrupted,
                TestCommandJournal.DecideRecovery("x", JournalPhase.Claimed));
        }

        [Fact]
        public void Executed_RewritesResponse()
        {
            Assert.Equal(RecoveryAction.RewriteResponse,
                TestCommandJournal.DecideRecovery("x", JournalPhase.Executed));
        }

        [Fact]
        public void Done_Skips()
        {
            Assert.Equal(RecoveryAction.Skip,
                TestCommandJournal.DecideRecovery("x", JournalPhase.Done));
        }

        // ----- Replay + decision, ordinary command -----

        [Fact]
        public void Replay_ClaimedOnly_DecidesInterrupted()
        {
            string content = TestCommandJournal.FormatClaimed("0002", 2, "StartRecording", "s", 1) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(JournalPhase.Claimed, map["0002"]);
            Assert.Equal(RecoveryAction.Interrupted, TestCommandJournal.DecideRecovery(map, "0002"));
        }

        [Fact]
        public void Replay_ClaimedThenExecuted_DecidesRewrite()
        {
            string content =
                TestCommandJournal.FormatClaimed("0002", 2, "StartRecording", "s", 1) + "\n"
                + TestCommandJournal.FormatExecuted("0002", 2, 2, "OK", null, null) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(JournalPhase.Executed, map["0002"]);
            Assert.Equal(RecoveryAction.RewriteResponse, TestCommandJournal.DecideRecovery(map, "0002"));
        }

        [Fact]
        public void Replay_FullLifecycle_DecidesSkip()
        {
            string content =
                TestCommandJournal.FormatClaimed("0002", 2, "StartRecording", "s", 1) + "\n"
                + TestCommandJournal.FormatExecuted("0002", 2, 2, "OK", null, null) + "\n"
                + TestCommandJournal.FormatDone("0002", 2, "OK", 3) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(JournalPhase.Done, map["0002"]);
            Assert.Equal(RecoveryAction.Skip, TestCommandJournal.DecideRecovery(map, "0002"));
        }

        [Fact]
        public void Replay_UnknownId_DecidesExecute()
        {
            var map = TestCommandJournal.ReplayIntoPhaseMap("");
            Assert.Equal(RecoveryAction.Execute, TestCommandJournal.DecideRecovery(map, "never-seen"));
        }

        // ----- LoadGame rows (long-running boot channel) -----

        [Fact]
        public void LoadGame_Claimed_Interrupted_NeverReInitiate()
        {
            // Crashed mid-scene-load: journal at CLAIMED. Must NOT re-initiate the load.
            string content = TestCommandJournal.FormatClaimed("0006", 6, "LoadGame", "s", 1) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(RecoveryAction.Interrupted, TestCommandJournal.DecideRecovery(map, "0006"));
        }

        [Fact]
        public void LoadGame_Executed_Rewrites()
        {
            string content =
                TestCommandJournal.FormatClaimed("0006", 6, "LoadGame", "s", 1) + "\n"
                + TestCommandJournal.FormatExecuted("0006", 6, 2, "OK", null, null) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(RecoveryAction.RewriteResponse, TestCommandJournal.DecideRecovery(map, "0006"));
        }

        [Fact]
        public void LoadGame_Done_Skips()
        {
            string content =
                TestCommandJournal.FormatClaimed("0006", 6, "LoadGame", "s", 1) + "\n"
                + TestCommandJournal.FormatExecuted("0006", 6, 2, "OK", null, null) + "\n"
                + TestCommandJournal.FormatDone("0006", 6, "OK", 3) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(RecoveryAction.Skip, TestCommandJournal.DecideRecovery(map, "0006"));
        }

        // ----- Duplicate id (crash-recovery rewrite): highest phase wins -----

        [Fact]
        public void DuplicateId_HighestPhaseWins()
        {
            // A DONE line followed by a stray earlier-phase line still resolves DONE.
            string content =
                TestCommandJournal.FormatExecuted("0002", 2, 2, "OK", null, null) + "\n"
                + TestCommandJournal.FormatDone("0002", 2, "OK", 3) + "\n"
                + TestCommandJournal.FormatClaimed("0002", 2, "StartRecording", "s", 4) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(JournalPhase.Done, map["0002"]);
        }

        [Fact]
        public void MultipleIds_TrackedIndependently()
        {
            string content =
                TestCommandJournal.FormatClaimed("a", 1, "SetSetting", "s", 1) + "\n"
                + TestCommandJournal.FormatExecuted("b", 2, 2, "OK", null, null) + "\n"
                + TestCommandJournal.FormatDone("c", 3, "OK", 3) + "\n";
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(RecoveryAction.Interrupted, TestCommandJournal.DecideRecovery(map, "a"));
            Assert.Equal(RecoveryAction.RewriteResponse, TestCommandJournal.DecideRecovery(map, "b"));
            Assert.Equal(RecoveryAction.Skip, TestCommandJournal.DecideRecovery(map, "c"));
        }

        // ----- True RewriteResponse (Adjudication A): re-emit the original verdict -----

        [Fact]
        public void Executed_RewriteResponse_ReEmitsOriginalVerdictPayloadMsg()
        {
            // The EXECUTED line stores the original terminal outcome; a crash before DONE
            // must rewrite THAT response, not a synthetic INTERRUPTED.
            var payload = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("recordingId", "abc123"),
            };
            string content =
                TestCommandJournal.FormatClaimed("0002", 2, "StartRecording", "s", 1) + "\n"
                + TestCommandJournal.FormatExecuted("0002", 2, 2, "OK", payload, null) + "\n";
            var lines = TestCommandJournal.SplitCompleteLines(content);

            Assert.Equal(RecoveryAction.RewriteResponse,
                TestCommandJournal.DecideRecovery(TestCommandJournal.ReplayIntoPhaseMap(lines), "0002"));

            Assert.True(TestCommandJournal.TryGetExecutedResponse(
                lines, "0002", out string verdict, out var recovered, out string msg));
            Assert.Equal("OK", verdict);
            Assert.Null(msg); // stored empty msg normalizes back to null
            Assert.NotNull(recovered);
            Assert.Equal("recordingId", recovered[0].Key);
            Assert.Equal("abc123", recovered[0].Value);
        }

        [Fact]
        public void Executed_RewriteResponse_IsByteEquivalentToOriginalResponse()
        {
            // The rewritten response differs from the pre-crash one ONLY in seq/ut; the
            // verdict + payload + msg reconstruct byte-for-byte. Holding seq + ut fixed here
            // isolates that reconstruction.
            var payload = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("recordingId", "abc123"),
            };
            string original = TestCommandResponse.FormatResponseLine(
                "0002", "StartRecording", "OK", 2, 1234.5, payload, null);

            string content = TestCommandJournal.FormatExecuted("0002", 2, 2, "OK", payload, null) + "\n";
            Assert.True(TestCommandJournal.TryGetExecutedResponse(
                TestCommandJournal.SplitCompleteLines(content), "0002",
                out string verdict, out var recovered, out string msg));
            string rewritten = TestCommandResponse.FormatResponseLine(
                "0002", "StartRecording", verdict, 2, 1234.5, recovered, msg);

            Assert.Equal(original, rewritten);
        }

        // ----- Torn trailing line: never durably committed -----

        [Fact]
        public void TornTrailingExecuted_Ignored_StaysAtClaimed()
        {
            // The EXECUTED line was being appended when the process died (no \n),
            // so replay must keep the id at CLAIMED -> Interrupted, not RewriteResponse.
            string content =
                TestCommandJournal.FormatClaimed("0002", 2, "StartRecording", "s", 1) + "\n"
                + TestCommandJournal.FormatExecuted("0002", 2, 2, "OK", null, null); // torn: no trailing \n
            var map = TestCommandJournal.ReplayIntoPhaseMap(content);
            Assert.Equal(JournalPhase.Claimed, map["0002"]);
            Assert.Equal(RecoveryAction.Interrupted, TestCommandJournal.DecideRecovery(map, "0002"));
        }
    }
}
