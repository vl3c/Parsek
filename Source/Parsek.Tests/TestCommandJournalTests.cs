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
            string line = TestCommandJournal.FormatExecuted("0002", 2, 17390512.951);
            Assert.True(TestCommandJournal.TryParseLine(line, out JournalLine jl));
            Assert.Equal(JournalPhase.Executed, jl.Phase);
            Assert.Equal("0002", jl.Id);
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
    }
}
