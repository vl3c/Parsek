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

        // ----- KscAction: mode x sub-action decider matrix (M-B3 OQ1 follow-up) -----
        // The two readiness bits model the three career modes:
        //   CAREER          -> CareerPresent = true,  RnDPresent = true
        //   SCIENCE_SANDBOX -> CareerPresent = false, RnDPresent = true  (R&D live, Funding null)
        //   SANDBOX         -> CareerPresent = false, RnDPresent = false
        // research-node admits on (CareerPresent || RnDPresent); the other three stay
        // CAREER-only (CareerPresent). Scene is SPACECENTER + AtSpaceCenter so upgrade-facility
        // can reach Execute in the admit cells without the SPACECENTER sub-gate confounding.
        private static DispatchState Mode(bool careerPresent, bool rnDPresent) => new DispatchState
        {
            Scene = TestCommandScene.SpaceCenter,
            GameLoaded = true,
            SettingsPresent = true,
            CareerPresent = careerPresent,
            RnDPresent = rnDPresent,
            AtSpaceCenter = true,
        };

        [Theory]
        // CAREER: all four sub-actions admit (with their preconditions).
        [InlineData(true, true, "research-node node=basicRocketry", true, null)]
        [InlineData(true, true, "hire-kerbal kerbal=Jeb", true, null)]
        [InlineData(true, true, "dismiss-kerbal kerbal=Bob", true, null)]
        [InlineData(true, true, "upgrade-facility facility=VehicleAssemblyBuilding", true, null)]
        // SCIENCE_SANDBOX: research-node admits (R&D live); the other three defer career-not-ready.
        [InlineData(false, true, "research-node node=basicRocketry", true, null)]
        [InlineData(false, true, "hire-kerbal kerbal=Jeb", false, "career-not-ready")]
        [InlineData(false, true, "dismiss-kerbal kerbal=Bob", false, "career-not-ready")]
        [InlineData(false, true, "upgrade-facility facility=VehicleAssemblyBuilding", false, "career-not-ready")]
        // SANDBOX: all four defer career-not-ready.
        [InlineData(false, false, "research-node node=basicRocketry", false, "career-not-ready")]
        [InlineData(false, false, "hire-kerbal kerbal=Jeb", false, "career-not-ready")]
        [InlineData(false, false, "dismiss-kerbal kerbal=Bob", false, "career-not-ready")]
        [InlineData(false, false, "upgrade-facility facility=VehicleAssemblyBuilding", false, "career-not-ready")]
        public void KscAction_ModeSubActionMatrix(
            bool careerPresent, bool rnDPresent, string args, bool executes, string deferReason)
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=KscAction action=" + args), Mode(careerPresent, rnDPresent));
            if (executes)
                Assert.Equal(DispatchDecision.Execute, r.Decision);
            else
                AssertDefer(r, deferReason);
        }

        [Fact]
        public void KscAction_ResearchNode_ScienceMode_Executes_ButOthersStayCareerOnly()
        {
            // The single OQ1 finding that reaches back into a merged module: research-node
            // executes in SCIENCE_SANDBOX while hire / dismiss / upgrade-facility do not, and
            // the shared CareerPresent bit was NOT relaxed (it stays false in Science mode).
            var science = Mode(careerPresent: false, rnDPresent: true);
            Assert.Equal(DispatchDecision.Execute, TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=KscAction action=research-node node=basicRocketry"), science).Decision);
            AssertDefer(TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=KscAction action=hire-kerbal kerbal=Jeb"), science), "career-not-ready");
        }

        // ----- SaveGame (M-C1.1 follow-up) -----

        [Fact]
        public void SaveGame_AnyScene_Executes()
        {
            // AnyScene: the no-game refusal is an in-executor ERROR, not a dispatch gate, so a
            // settled scene with a game dispatches to Execute.
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=SaveGame"), Flight());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void SaveGame_DuringSceneTransition_Defers_NotSafePoint()
        {
            var st = Flight();
            st.Transitioning = true;
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=SaveGame"), st), "not-safe-point");
        }
    }
}
