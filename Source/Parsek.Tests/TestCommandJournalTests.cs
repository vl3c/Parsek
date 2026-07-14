using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the journal (WAL) line grammar
    /// (<see cref="TestCommandJournal"/>) formatters, parse, and the torn-trailing-
    /// line rule of the line splitter. A regression here corrupts the durable
    /// at-most-once record or lets a half-written (never durably committed) trailing
    /// line drive recovery.
    /// </summary>
    public class TestCommandJournalTests
    {
        [Fact]
        public void FormatClaimed_RoundTripsThroughParser()
        {
            string line = TestCommandJournal.FormatClaimed("0002", 2, "StartRecording", "7f3a", 17390512.884);
            Assert.True(TestCommandJournal.TryParseLine(line, out JournalLine jl));
            Assert.Equal("0002", jl.Id);
            Assert.Equal(JournalPhase.Claimed, jl.Phase);
            Assert.Equal(2, jl.Seq);
            Assert.Equal("StartRecording", jl.Verb);
            Assert.Equal("7f3a", jl.Session);
        }

        [Fact]
        public void FormatExecuted_RoundTrips()
        {
            string line = TestCommandJournal.FormatExecuted("0002", 2, 17390512.951, "OK", null, null);
            Assert.True(TestCommandJournal.TryParseLine(line, out JournalLine jl));
            Assert.Equal(JournalPhase.Executed, jl.Phase);
            Assert.Equal("0002", jl.Id);
        }

        [Fact]
        public void FormatExecuted_CarriesVerdictPayloadMsg_RoundTrips()
        {
            var payload = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
            {
                new System.Collections.Generic.KeyValuePair<string, string>("recordingId", "abc123"),
                new System.Collections.Generic.KeyValuePair<string, string>("scene", "FLIGHT"),
            };
            // A msg with a space and '=' must survive the double-encode (payload token) and
            // per-value encode round-trip.
            string line = TestCommandJournal.FormatExecuted("0006", 6, 17390512.951, "OK", payload, "note with=eq");
            Assert.True(TestCommandJournal.TryParseLine(line, out JournalLine jl));
            Assert.Equal("OK", jl.Verdict);
            Assert.Equal("note with=eq", jl.Msg);
            Assert.NotNull(jl.Payload);
            Assert.Equal(2, jl.Payload.Count);
            Assert.Equal("recordingId", jl.Payload[0].Key);
            Assert.Equal("abc123", jl.Payload[0].Value);
            Assert.Equal("FLIGHT", jl.Payload[1].Value);
            // The whole EXECUTED line stays a single-line, ASCII wire token (no raw newline).
            Assert.DoesNotContain("\n", line);
        }

        [Fact]
        public void FormatDone_CarriesVerdict()
        {
            string line = TestCommandJournal.FormatDone("0002", 2, "OK", 17390512.960);
            Assert.True(TestCommandJournal.TryParseLine(line, out JournalLine jl));
            Assert.Equal(JournalPhase.Done, jl.Phase);
            Assert.Equal("OK", jl.Verdict);
        }

        [Fact]
        public void Format_UsesInvariantCulture_NoCommaInT()
        {
            string line = TestCommandJournal.FormatClaimed("1", 1, "LoadGame", "abc", 17390512.884);
            Assert.DoesNotContain(",", line);
        }

        [Theory]
        [InlineData("id=1 seq=2 verb=X t=3")]      // no phase
        [InlineData("phase=CLAIMED seq=2")]         // no id
        [InlineData("id=1 phase=BOGUS seq=2")]      // unknown phase
        [InlineData("")]
        public void TryParseLine_Malformed_Fails(string line)
        {
            Assert.False(TestCommandJournal.TryParseLine(line, out _));
        }

        [Fact]
        public void SplitCompleteLines_DropsTornTrailingLine()
        {
            // Two whole lines + a torn third (no trailing \n).
            string content =
                "id=1 phase=CLAIMED seq=1 verb=X session=s t=1\n"
                + "id=1 phase=EXECUTED seq=1 t=2\n"
                + "id=1 phase=DONE seq=1 verd"; // torn, never committed
            var lines = TestCommandJournal.SplitCompleteLines(content);
            Assert.Equal(2, lines.Count);
            Assert.Contains("phase=EXECUTED", lines[1]);
        }

        [Fact]
        public void SplitCompleteLines_HandlesCrlf()
        {
            string content = "id=1 phase=CLAIMED seq=1 verb=X session=s t=1\r\n";
            var lines = TestCommandJournal.SplitCompleteLines(content);
            Assert.Single(lines);
            Assert.True(TestCommandJournal.TryParseLine(lines[0], out JournalLine jl));
            Assert.Equal(JournalPhase.Claimed, jl.Phase);
        }

        [Fact]
        public void SplitCompleteLines_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(TestCommandJournal.SplitCompleteLines(null));
            Assert.Empty(TestCommandJournal.SplitCompleteLines(""));
        }

        // ----- In-memory phase-map mirror (defense-in-depth at-most-once) -----

        [Fact]
        public void MirrorPhaseIntoMap_RaisesPhase_KeepsHighest()
        {
            var map = new System.Collections.Generic.Dictionary<string, JournalPhase>();
            TestCommandJournal.MirrorPhaseIntoMap(map, "0002", "CLAIMED");
            Assert.Equal(JournalPhase.Claimed, map["0002"]);
            TestCommandJournal.MirrorPhaseIntoMap(map, "0002", "EXECUTED");
            Assert.Equal(JournalPhase.Executed, map["0002"]);
            TestCommandJournal.MirrorPhaseIntoMap(map, "0002", "DONE");
            Assert.Equal(JournalPhase.Done, map["0002"]);
            // A lower phase after a higher one never regresses the map.
            TestCommandJournal.MirrorPhaseIntoMap(map, "0002", "CLAIMED");
            Assert.Equal(JournalPhase.Done, map["0002"]);
        }

        [Fact]
        public void MirrorPhaseIntoMap_UnknownToken_OrNullId_IsNoOp()
        {
            var map = new System.Collections.Generic.Dictionary<string, JournalPhase>();
            TestCommandJournal.MirrorPhaseIntoMap(map, "x", "BOGUS");
            Assert.False(map.ContainsKey("x"));
            TestCommandJournal.MirrorPhaseIntoMap(map, null, "CLAIMED");
            Assert.Empty(map);
            TestCommandJournal.MirrorPhaseIntoMap(null, "x", "CLAIMED"); // null map: no throw
        }

        // ----- M-C1 two-phase verbs: CLAIMED -> Interrupted at-most-once (design cells) -----

        // The three M-C1 verbs that hold the FIFO head as two-phase (InvokeRewind /
        // AnswerMergeDialog / TimeJump) ride the SAME phase-driven WAL as every other verb: an
        // id stuck at CLAIMED on the hypothetical addon restart the v1 harness never exercises
        // maps to Interrupted and the irreversible side effect is NEVER re-invoked. These are
        // thin wrappers over the generic phase logic, kept as design-named documentation cells
        // (design edge cases 5 / 11 / 16).
        [Theory]
        [InlineData("InvokeRewind")]
        [InlineData("AnswerMergeDialog")]
        [InlineData("TimeJump")]
        public void ClaimedTwoPhaseVerb_RecoversInterrupted(string verb)
        {
            string line = TestCommandJournal.FormatClaimed("0009", 9, verb, "sess1", 17390512.884);
            Assert.True(TestCommandJournal.TryParseLine(line, out JournalLine jl));
            Assert.Equal(JournalPhase.Claimed, jl.Phase);
            Assert.Equal(verb, jl.Verb);
            // A CLAIMED id recovers as Interrupted: the irreversible side effect never re-runs.
            Assert.Equal(RecoveryAction.Interrupted, TestCommandJournal.DecideRecovery(jl.Id, jl.Phase));

            // Same result through the replayed-map convenience overload.
            var map = new System.Collections.Generic.Dictionary<string, JournalPhase>();
            TestCommandJournal.MirrorPhaseIntoMap(map, "0009", "CLAIMED");
            Assert.Equal(RecoveryAction.Interrupted, TestCommandJournal.DecideRecovery(map, "0009"));
        }
    }
}
