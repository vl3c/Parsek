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
        [InlineData("MissionConfig")]
        [InlineData("StartLoopPlayback")]
        [InlineData("EnterWatchMode")]
        [InlineData("EnterMapView")]
        [InlineData("ExitMapView")]
        [InlineData("InvokeRewindToLaunch")]
        [InlineData("SealSlot")]
        [InlineData("RouteCommand")]
        public void ImplementedVerbs_ClassifyImplemented(string verb)
        {
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(verb));
        }

        [Theory]
        [InlineData("StopPlayback")]
        [InlineData("StashSlot")]
        [InlineData("FlySlot")]
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
            // 25 = v1 (10) + M-C1 batch 1 (4) + M-C1.1 SaveGame (1) + M-C2 EVA (3)
            // + EVA-4 EvaChuteDeploy (1) + R12 ExitToSpaceCenter (1)
            // + R12 SimulateStockSwitchClick (1) + the arrival-validation lane's
            // MissionConfig promotion (1) + the player-workflow lane's
            // StartLoopPlayback + EnterWatchMode promotions (2)
            // + M-A7 ExportRenderManifest (1) + the map-view pair EnterMapView +
            // ExitMapView (2) + InvokeRewindToLaunch (1), which takes the total to 28.
            // Mirrored by hlib.IMPLEMENTED_SEAM_VERBS.
            //
            // InvokeRewindToLaunch is ADDITIVE (27 -> 28 implemented, reserved unchanged at
            // 7): the reserved envelope never carried a rewind-to-launch verb. It is NOT a
            // second spelling of InvokeRewind - that verb drives the Re-Fly /
            // Rewind-to-Separation system (RewindPoint + slot -> RewindInvoker.StartInvoke
            // -> a ReFlySessionMarker), this one drives the Recordings-table "R" button
            // (RecordingStore.InitiateRewind: no RewindPoint, no marker, no merge journal).
            //
            // The map-view pair is ADDITIVE too (27 implemented, reserved unchanged at 7):
            // the reserved envelope never carried a camera / scene-presentation verb.
            //
            // ExportRenderManifest is ADDITIVE (24 -> 25 implemented, reserved unchanged
            // at 7): the reserved envelope never carried an export verb, so it is the
            // SaveGame / EVA shape rather than a promotion.
            //
            // The two R12 verbs move the counts DIFFERENTLY, and the difference is the
            // point: ExitToSpaceCenter is ADDITIVE (the reserved envelope never carried a
            // scene-transition verb) so it moved implemented 19 -> 20 and left reserved at
            // 11, while SimulateStockSwitchClick is a PROMOTION out of the reserved list
            // (20 -> 21 implemented, 11 -> 10 reserved) - its wire token is byte-identical
            // before and after and only the response changed. A future name that appears in
            // BOTH sets, or in neither, is what these two numbers catch.
            //
            // The logistics pair (SealSlot + RouteCommand) is a PROMOTION, so it moves
            // BOTH numbers in opposite directions: 28 -> 30 implemented, 7 -> 5 reserved.
            // That is the arithmetic signature this cell exists to hold - an additive
            // verb moves one number, a promotion moves two, and a half-done promotion
            // (added to Implemented, left in Reserved) moves the first without the
            // second and is caught here as well as by the disjointness cell below.
            // StashSlot and FlySlot deliberately stay behind: FlySlot's mechanism is
            // already driveable as InvokeRewind, and nothing needs StashSlot's slot-OPEN
            // direction.
            Assert.Equal(30, TestCommandVerbs.ImplementedVerbNames.Count);
            Assert.Equal(5, TestCommandVerbs.ReservedVerbNames.Count);
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
