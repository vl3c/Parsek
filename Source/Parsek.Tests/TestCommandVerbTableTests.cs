using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the known-verb table (<see cref="TestCommandVerbs"/>).
    /// Guards that v1 verbs classify Implemented, phase-3 names classify Reserved
    /// (distinct so the orchestrator can probe capability), and anything else is
    /// Unknown. A regression would let v1 silently execute or mis-bucket a future
    /// command as a typo.
    /// </summary>
    public class TestCommandVerbTableTests
    {
        [Theory]
        [InlineData("SetSetting")]
        [InlineData("StartRecording")]
        [InlineData("StopRecording")]
        [InlineData("CommitTree")]
        [InlineData("DiscardTree")]
        [InlineData("RecordingState")]
        [InlineData("RunTests")]
        [InlineData("LoadGame")]
        [InlineData("MissionMark")]
        [InlineData("FlushAndQuit")]
        public void ImplementedVerbs_ClassifyImplemented(string verb)
        {
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(verb));
        }

        [Theory]
        [InlineData("StartLoopPlayback")]
        [InlineData("StopPlayback")]
        [InlineData("EnterWatchMode")]
        [InlineData("InvokeRewind")]
        [InlineData("AnswerMergeDialog")]
        [InlineData("KscAction")]
        [InlineData("SealSlot")]
        [InlineData("StashSlot")]
        [InlineData("FlySlot")]
        [InlineData("RouteCommand")]
        [InlineData("MissionConfig")]
        [InlineData("TimeJump")]
        [InlineData("SimulateStockSwitchClick")]
        [InlineData("CrashAfterJournalPhase")]
        [InlineData("RunInvariantReport")]
        public void ReservedVerbs_ClassifyReserved(string verb)
        {
            Assert.Equal(TestCommandVerbClass.Reserved, TestCommandVerbs.Classify(verb));
        }

        [Theory]
        [InlineData("Frobnicate")]
        [InlineData("setsetting")]  // case-sensitive: lowercase is not a match
        [InlineData("SETSETTING")]
        [InlineData("")]
        [InlineData(null)]
        public void UnknownVerbs_ClassifyUnknown(string verb)
        {
            Assert.Equal(TestCommandVerbClass.Unknown, TestCommandVerbs.Classify(verb));
        }

        [Fact]
        public void Table_HasExpectedCounts()
        {
            Assert.Equal(10, TestCommandVerbs.ImplementedVerbNames.Count);
            Assert.Equal(15, TestCommandVerbs.ReservedVerbNames.Count);
        }
    }
}
