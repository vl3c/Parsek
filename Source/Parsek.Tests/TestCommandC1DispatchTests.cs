using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C1 dispatch-matrix coverage for the four batch-1 seam verbs (InvokeRewind,
    /// AnswerMergeDialog, TimeJump, KscAction). This is the safety gate: a verb must never
    /// execute in an unsafe state, InvokeRewind must refuse mid-merge-journal /
    /// load-in-flight / recording-active, AnswerMergeDialog must defer until a re-fly popup
    /// or marker exists, and KscAction must defer until the career (and, for
    /// upgrade-facility, the SPACECENTER scene) is ready.
    /// </summary>
    public class TestCommandC1DispatchTests
    {
        private static ParsedCommand Cmd(string line)
            => TestCommandParser.ParseLine(line, 1);

        private static DispatchState Flight() => new DispatchState
        {
            Scene = TestCommandScene.Flight,
            GameLoaded = true,
            SettingsPresent = true,
        };

        private static DispatchState MainMenu() => new DispatchState
        {
            Scene = TestCommandScene.MainMenu,
        };

        private static void AssertDefer(DispatchResult r, string reason)
        {
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal(reason, r.Reason);
        }

        private static void AssertReject(DispatchResult r, string reason)
        {
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal(reason, r.Reason);
        }

        // ----- InvokeRewind -----

        [Fact]
        public void InvokeRewind_OutsideFlight_Defers_NotInFlight()
        {
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=InvokeRewind rp=r slot=1"), MainMenu()),
                "not-in-flight");
        }

        [Fact]
        public void InvokeRewind_MergeJournalInFlight_Rejects()
        {
            var st = Flight();
            st.MergeJournalInFlight = true;
            AssertReject(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=InvokeRewind rp=r slot=1"), st),
                "merge-journal-in-flight");
        }

        [Fact]
        public void InvokeRewind_LoadInFlight_Rejects()
        {
            var st = Flight();
            st.LoadInFlight = true;
            AssertReject(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=InvokeRewind rp=r slot=1"), st),
                "load-in-flight");
        }

        [Fact]
        public void InvokeRewind_RecordingActive_Rejects()
        {
            var st = Flight();
            st.Recording = true;
            AssertReject(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=InvokeRewind rp=r slot=1"), st),
                "recording-active");
        }

        [Fact]
        public void InvokeRewind_MergeJournalTakesPrecedenceOverRecording()
        {
            // The design lists the guards merge-journal-in-flight -> load-in-flight ->
            // recording-active; the first satisfied one wins.
            var st = Flight();
            st.MergeJournalInFlight = true;
            st.Recording = true;
            AssertReject(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=InvokeRewind rp=r slot=1"), st),
                "merge-journal-in-flight");
        }

        [Fact]
        public void InvokeRewind_ReadyInFlight_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=InvokeRewind rp=r slot=1"), Flight());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        // ----- AnswerMergeDialog -----

        [Fact]
        public void AnswerMergeDialog_NoDialogNoMarker_Defers_NoReflyDialog()
        {
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=AnswerMergeDialog choice=merge"), MainMenu()),
                "no-refly-dialog");
        }

        [Fact]
        public void AnswerMergeDialog_LivePopup_Executes()
        {
            var st = MainMenu();
            st.ReFlyMergeDialogPresent = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=AnswerMergeDialog choice=merge"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void AnswerMergeDialog_MarkerOnly_Executes()
        {
            // A re-fly marker present but no dialog yet still executes: the verb DRIVES the
            // conclusion scene-exit that surfaces the pre-transition dialog.
            var st = Flight();
            st.ActiveReFlyMarker = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=AnswerMergeDialog choice=merge"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        // ----- TimeJump -----

        [Fact]
        public void TimeJump_OutsideFlight_Defers_NotInFlight()
        {
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=TimeJump deltaSeconds=600"), MainMenu()),
                "not-in-flight");
        }

        [Fact]
        public void TimeJump_InFlight_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=TimeJump deltaSeconds=600"), Flight());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        // ----- KscAction -----

        [Theory]
        [InlineData("research-node node=basicRocketry")]
        [InlineData("hire-kerbal kerbal=Jeb")]
        [InlineData("dismiss-kerbal kerbal=Bob")]
        public void KscAction_CareerNotReady_Defers(string args)
        {
            // AnyScene, but defers career-not-ready while the career singletons are absent.
            var st = MainMenu(); // CareerPresent = false
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=KscAction action=" + args), st),
                "career-not-ready");
        }

        [Fact]
        public void KscAction_ResearchNode_CareerReady_Executes()
        {
            var st = MainMenu();
            st.CareerPresent = true;
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=KscAction action=research-node node=basicRocketry"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void KscAction_UpgradeFacility_NotAtSpaceCenter_Defers()
        {
            var st = MainMenu();
            st.CareerPresent = true;
            st.AtSpaceCenter = false;
            AssertDefer(TestCommandDispatcher.DecideDispatch(
                    Cmd("id=1 cmd=KscAction action=upgrade-facility facility=VehicleAssemblyBuilding"), st),
                "not-at-space-center");
        }

        [Fact]
        public void KscAction_UpgradeFacility_CareerNotReadyTakesPrecedence()
        {
            var st = new DispatchState { Scene = TestCommandScene.SpaceCenter, AtSpaceCenter = true };
            // CareerPresent = false: the career gate is checked before the SPACECENTER sub-gate.
            AssertDefer(TestCommandDispatcher.DecideDispatch(
                    Cmd("id=1 cmd=KscAction action=upgrade-facility facility=VehicleAssemblyBuilding"), st),
                "career-not-ready");
        }

        [Fact]
        public void KscAction_UpgradeFacility_AtSpaceCenter_Executes()
        {
            var st = new DispatchState { Scene = TestCommandScene.SpaceCenter, CareerPresent = true, AtSpaceCenter = true };
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=KscAction action=upgrade-facility facility=VehicleAssemblyBuilding"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void KscAction_NonUpgrade_AtAnyScene_DoesNotRequireSpaceCenter()
        {
            // research-node is AnyScene: career-ready at the tracking station executes without
            // the SPACECENTER sub-gate.
            var st = new DispatchState { Scene = TestCommandScene.TrackingStation, CareerPresent = true };
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=KscAction action=research-node node=basicRocketry"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }
    }
}
