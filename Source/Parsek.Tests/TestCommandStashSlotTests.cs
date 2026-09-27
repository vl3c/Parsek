using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>StashSlot</c> decision core (the seventh strict promotion
    /// out of the M-A2 reserved list): the slot-target parse, the post-handler verdict and
    /// the dispatch row. The production half (<c>UnfinishedFlightStashHandler.TryStash</c>
    /// over a real walked slot) is covered in <see cref="ReFlyThroughEvaTests"/>.
    /// </summary>
    public class TestCommandStashSlotTests
    {
        private static DispatchState Loaded(TestCommandScene scene) => new DispatchState
        {
            Scene = scene,
            GameLoaded = true,
            SettingsPresent = true,
        };

        private static ParsedCommand Cmd(string args)
            => TestCommandParser.ParseLine("id=1 cmd=StashSlot " + args, 1);

        // ---- target parse ----

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "0")]
        [InlineData(null, "0")]
        public void ResolveTarget_NoRp_RejectsTargetArgMissing(string rp, string slot)
        {
            StashTargetSelection sel = TestCommandStashSlot.ResolveTarget(rp, slot);
            Assert.False(sel.Ok);
            Assert.Equal("target-arg-missing", sel.RejectReason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("-1")]
        [InlineData("one")]
        [InlineData("1.5")]
        public void ResolveTarget_RpWithBadSlot_RejectsUnknownSlot(string slot)
        {
            StashTargetSelection sel = TestCommandStashSlot.ResolveTarget("rp-7", slot);
            Assert.False(sel.Ok);
            Assert.Equal("unknown-slot", sel.RejectReason);
        }

        [Fact]
        public void ResolveTarget_RpAndSlot_Parses()
        {
            StashTargetSelection sel = TestCommandStashSlot.ResolveTarget("rp-7", "2");
            Assert.True(sel.Ok);
            Assert.Equal("rp-7", sel.RewindPointId);
            Assert.Equal(2, sel.SlotIndex);
        }

        [Fact]
        public void RejectVocabulary_IsSealSlotsVerbatim()
        {
            // The harness maps these tokens once for SealSlot / InvokeRewind; sharing them
            // is why StashSlot needs no new _SEAM_REFUSAL_SUBKINDS rows.
            Assert.Equal(TestCommandSealSlot.TargetArgMissingReason, TestCommandStashSlot.TargetArgMissingReason);
            Assert.Equal(TestCommandSealSlot.UnknownSlotReason, TestCommandStashSlot.UnknownSlotReason);
            Assert.Equal(TestCommandSealSlot.UnknownRpReason, TestCommandStashSlot.UnknownRpReason);
        }

        // ---- verdict after the handler ----

        [Fact]
        public void ClassifyAfterStash_AllGood_IsOk()
        {
            Assert.Null(TestCommandStashSlot.ClassifyAfterStash(true, null, true, true, null));
        }

        [Theory]
        [InlineData("alreadyStashed", "stash-refused alreadyStashed")]
        [InlineData("alreadyUnfinishedFlight", "stash-refused alreadyUnfinishedFlight")]
        [InlineData("evaCrewJoinedForeignVessel", "stash-refused evaCrewJoinedForeignVessel")]
        [InlineData("unsafeTerminal:Recovered", "stash-refused unsafeTerminal:Recovered")]
        [InlineData(null, "stash-refused unknown")]
        [InlineData("", "stash-refused unknown")]
        public void ClassifyAfterStash_HandlerRefusal_CarriesTheHandlerReason(
            string reason, string expected)
        {
            // The refusal wins over every read-back flag: a refused stash wrote nothing.
            Assert.Equal(expected,
                TestCommandStashSlot.ClassifyAfterStash(false, reason, false, false, null));
            Assert.Equal(expected,
                TestCommandStashSlot.ClassifyAfterStash(false, reason, true, true, null));
        }

        [Fact]
        public void ClassifyAfterStash_OtherSlotStashed_IsItsOwnToken()
        {
            Assert.Equal("stash-resolved-other-slot",
                TestCommandStashSlot.ClassifyAfterStash(true, null, false, true, null));
        }

        [Fact]
        public void ClassifyAfterStash_NotUnfinishedAfterStash_CarriesThePredicateReason()
        {
            Assert.Equal("stash-not-unfinished no matching rewind point or slot",
                TestCommandStashSlot.ClassifyAfterStash(
                    true, null, true, false, "no matching rewind point or slot"));
            Assert.Equal("stash-not-unfinished unknown",
                TestCommandStashSlot.ClassifyAfterStash(true, null, true, false, null));
        }

        // ---- dispatch row ----

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Dispatch_ExecutesInFlightAndAtTheSpaceCenter(bool flight)
        {
            var scene = flight ? TestCommandScene.Flight : TestCommandScene.SpaceCenter;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("rp=rp-1 slot=0"), Loaded(scene));
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_LoadInFlight_Rejects()
        {
            var st = Loaded(TestCommandScene.Flight);
            st.LoadInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("rp=rp-1 slot=0"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_MergeJournalInFlight_Rejects()
        {
            var st = Loaded(TestCommandScene.Flight);
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("rp=rp-1 slot=0"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_LiveRecorder_DoesNotReject()
        {
            // Like SealSlot: the verb acts on COMMITTED state, and a player can press
            // Stash in the flight scene with a recorder running.
            var st = Loaded(TestCommandScene.Flight);
            st.Recording = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("rp=rp-1 slot=0"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void StashSlot_IsStateMutating()
        {
            Assert.True(TestCommandVerbs.IsStateMutatingVerb("StashSlot"));
        }
    }
}
