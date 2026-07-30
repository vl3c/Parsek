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
        [InlineData("InvokeRewind")]
        [InlineData("AnswerMergeDialog")]
        [InlineData("TimeJump")]
        [InlineData("KscAction")]
        [InlineData("SaveGame")]
        [InlineData("EvaExit")]
        [InlineData("EvaBoard")]
        [InlineData("PlantFlag")]
        [InlineData("EvaChuteDeploy")]
        [InlineData("ExitToSpaceCenter")]
        public void ImplementedVerbs_ClassifyImplemented(string verb)
        {
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(verb));
        }

        [Theory]
        [InlineData("StartLoopPlayback")]
        [InlineData("StopPlayback")]
        [InlineData("EnterWatchMode")]
        [InlineData("SealSlot")]
        [InlineData("StashSlot")]
        [InlineData("FlySlot")]
        [InlineData("RouteCommand")]
        [InlineData("MissionConfig")]
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
            // 20 = v1 (10) + M-C1 batch 1 (4) + M-C1.1 SaveGame (1) + M-C2 EVA (3)
            // + EVA-4 EvaChuteDeploy (1) + R12 ExitToSpaceCenter (1). Mirrored by
            // hlib.IMPLEMENTED_SEAM_VERBS.
            //
            // The reserved count is UNCHANGED at 11: R12's scene routing is one ADDITIVE
            // verb plus an additive `scene=` arg on LoadGame, neither of which was ever in
            // the reserved envelope. (R12's OTHER capability, SimulateStockSwitchClick, IS
            // a promotion out of that envelope and moves the reserved count to 10 when it
            // lands.)
            Assert.Equal(20, TestCommandVerbs.ImplementedVerbNames.Count);
            Assert.Equal(11, TestCommandVerbs.ReservedVerbNames.Count);
        }
    }
}
