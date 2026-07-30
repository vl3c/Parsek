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
        [InlineData("SimulateStockSwitchClick")]
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
            // 21 = v1 (10) + M-C1 batch 1 (4) + M-C1.1 SaveGame (1) + M-C2 EVA (3)
            // + EVA-4 EvaChuteDeploy (1) + R12 ExitToSpaceCenter (1)
            // + R12 SimulateStockSwitchClick (1). Mirrored by hlib.IMPLEMENTED_SEAM_VERBS.
            //
            // The two R12 verbs move the counts DIFFERENTLY, and the difference is the
            // point: ExitToSpaceCenter is ADDITIVE (the reserved envelope never carried a
            // scene-transition verb) so it moved implemented 19 -> 20 and left reserved at
            // 11, while SimulateStockSwitchClick is a PROMOTION out of the reserved list
            // (20 -> 21 implemented, 11 -> 10 reserved) - its wire token is byte-identical
            // before and after and only the response changed. A future name that appears in
            // BOTH sets, or in neither, is what these two numbers catch.
            Assert.Equal(21, TestCommandVerbs.ImplementedVerbNames.Count);
            Assert.Equal(10, TestCommandVerbs.ReservedVerbNames.Count);
        }

        [Fact]
        public void Table_ImplementedAndReservedAreDisjoint()
        {
            // The failure mode a PROMOTION invites: adding the name to ImplementedVerbs and
            // forgetting to delete it from ReservedVerbs. Classify checks Implemented first,
            // so the verb would work and the leftover row would be invisible - until the
            // counts above were "fixed" by loosening them. R12 promoted the first name since
            // M-C1, so this is now a permanent guard rather than a one-off review note.
            foreach (string verb in TestCommandVerbs.ImplementedVerbNames)
                Assert.DoesNotContain(verb, TestCommandVerbs.ReservedVerbNames);
        }
    }
}
