using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the dispatch decision
    /// (<see cref="TestCommandDispatcher.DecideDispatch"/>). This is the safety gate:
    /// a command must never execute in an unsafe scene/state, LoadGame must never
    /// silently discard an in-flight recording, and a leftover CLAIMED must resolve
    /// to Interrupted (crash recovery) rather than re-execute. The matrix walks verb
    /// x state cells and asserts Execute / Defer(reason) / Reject(reason) / Interrupted.
    /// </summary>
    public class TestCommandDispatchTests
    {
        private static ParsedCommand Cmd(string verb)
            => TestCommandParser.ParseLine("id=1 cmd=" + verb, 1);

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

        // ----- Journal-phase crash recovery -----

        [Fact]
        public void JournalClaimed_Interrupted_EvenForExecutableVerb()
        {
            var st = Flight();
            st.JournalPhase = JournalPhase.Claimed;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("StartRecording"), st);
            Assert.Equal(DispatchDecision.Interrupted, r.Decision);
        }

        // ----- Parse / verb-class rejects -----

        [Fact]
        public void Malformed_Rejects_WithParseError()
        {
            var parsed = TestCommandParser.ParseLine("id=1 cmd=MissionMark garbage", 1);
            var r = TestCommandDispatcher.DecideDispatch(parsed, Flight());
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("malformed", r.Reason);
        }

        [Fact]
        public void MissingId_Rejects_MissingId()
        {
            var parsed = TestCommandParser.ParseLine("cmd=MissionMark", 1);
            var r = TestCommandDispatcher.DecideDispatch(parsed, Flight());
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("missing-id", r.Reason);
        }

        [Fact]
        public void ReservedVerb_Rejects_NotImplementedV1()
        {
            // SealSlot stays reserved after M-C1 (InvokeRewind was promoted to Implemented).
            var r = TestCommandDispatcher.DecideDispatch(Cmd("SealSlot"), Flight());
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("not-implemented-v1", r.Reason);
        }

        [Fact]
        public void UnknownVerb_Rejects_UnknownCommand()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("Frobnicate"), Flight());
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("unknown-command", r.Reason);
        }

        // ----- Safe-point gate -----

        [Fact]
        public void Transitioning_Defers_NotSafePoint()
        {
            var st = Flight();
            st.Transitioning = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("MissionMark"), st);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-safe-point", r.Reason);
        }

        [Fact]
        public void LoadingScene_Defers_NotSafePoint()
        {
            var st = new DispatchState { Scene = TestCommandScene.Loading };
            var r = TestCommandDispatcher.DecideDispatch(Cmd("MissionMark"), st);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-safe-point", r.Reason);
        }

        [Fact]
        public void SettleCounter_Defers_NotSafePoint()
        {
            var st = Flight();
            st.SettleCounter = 2;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("MissionMark"), st);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-safe-point", r.Reason);
        }

        [Fact]
        public void BatchRunning_Defers_BatchRunning()
        {
            var st = Flight();
            st.BatchRunning = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("RunTests"), st);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("batch-running", r.Reason);
        }

        // ----- Per-verb scene precondition -----

        [Theory]
        [InlineData("StartRecording")]
        [InlineData("StopRecording")]
        [InlineData("CommitTree")]
        [InlineData("DiscardTree")]
        public void FlightVerb_OutsideFlight_Defers_NotInFlight(string verb)
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(verb), MainMenu());
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-in-flight", r.Reason);
        }

        [Theory]
        [InlineData("StartRecording")]
        [InlineData("StopRecording")]
        [InlineData("DiscardTree")]
        public void FlightVerb_InFlight_Executes(string verb)
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(verb), Flight());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void CommitTree_InFlight_NoTree_StillExecutes_HandlerEmitsError()
        {
            // Dispatch only gates scene; the no-active-tree ERROR is the handler's
            // job (C1), so CommitTree in FLIGHT executes even without a tree.
            var st = Flight();
            st.HasTree = false;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("CommitTree"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void SetSetting_NoGame_Defers_GameNotLoaded()
        {
            var st = MainMenu(); // SettingsPresent = false
            var r = TestCommandDispatcher.DecideDispatch(Cmd("SetSetting"), st);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("game-not-loaded", r.Reason);
        }

        [Fact]
        public void SetSetting_GameLoaded_AnyScene_Executes()
        {
            var st = new DispatchState { Scene = TestCommandScene.SpaceCenter, GameLoaded = true, SettingsPresent = true };
            var r = TestCommandDispatcher.DecideDispatch(Cmd("SetSetting"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Theory]
        [InlineData("RecordingState")]
        [InlineData("MissionMark")]
        [InlineData("FlushAndQuit")]
        public void AnySceneVerb_ExecutesEvenAtTrackingStation(string verb)
        {
            var st = new DispatchState { Scene = TestCommandScene.TrackingStation };
            var r = TestCommandDispatcher.DecideDispatch(Cmd(verb), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void RunTests_NoBatch_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("RunTests"), Flight());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        // ----- LoadGame boot channel + guards -----

        [Fact]
        public void LoadGame_AtMainMenu_NoRecorder_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("LoadGame"), MainMenu());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void LoadGame_LiveRecorder_Rejects_RecordingActive()
        {
            var st = Flight();
            st.Recording = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("LoadGame"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("recording-active", r.Reason);
        }

        [Fact]
        public void LoadGame_LoadInFlight_Rejects_LoadInFlight()
        {
            var st = MainMenu();
            st.LoadInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("LoadGame"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }
    }
}
