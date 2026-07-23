using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C2 dispatch-matrix coverage for the three EVA seam verbs (EvaExit, PlantFlag,
    /// EvaBoard). The safety gate: a verb must never execute in an unsafe state. EvaExit
    /// must defer split-pending / flighteva-not-ready and refuse load-in-flight; PlantFlag /
    /// EvaBoard must defer not-eva while the active vessel is not yet the EVA kerbal.
    /// </summary>
    public class TestCommandC2DispatchTests
    {
        private static ParsedCommand Cmd(string line)
            => TestCommandParser.ParseLine(line, 1);

        // A settled FLIGHT scene with the FlightEVA singleton present (the EvaExit ready state).
        private static DispatchState FlightEvaReady() => new DispatchState
        {
            Scene = TestCommandScene.Flight,
            GameLoaded = true,
            SettingsPresent = true,
            FlightEvaPresent = true,
        };

        // A settled FLIGHT scene whose active vessel is an EVA kerbal (the PlantFlag / EvaBoard
        // ready state).
        private static DispatchState FlightOnEva() => new DispatchState
        {
            Scene = TestCommandScene.Flight,
            GameLoaded = true,
            SettingsPresent = true,
            FlightEvaPresent = true,
            ActiveVesselIsEva = true,
        };

        private static DispatchState MainMenu() => new DispatchState { Scene = TestCommandScene.MainMenu };

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

        // ----- EvaExit -----

        [Fact]
        public void EvaExit_OutsideFlight_Defers_NotInFlight()
        {
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaExit"), MainMenu()), "not-in-flight");
        }

        [Fact]
        public void EvaExit_LoadInFlight_Rejects()
        {
            var st = FlightEvaReady();
            st.LoadInFlight = true;
            AssertReject(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaExit"), st), "load-in-flight");
        }

        [Fact]
        public void EvaExit_SplitPending_Defers()
        {
            var st = FlightEvaReady();
            st.StructuralSplitPending = true;
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaExit"), st), "split-pending");
        }

        [Fact]
        public void EvaExit_FlightEvaNotReady_Defers()
        {
            var st = FlightEvaReady();
            st.FlightEvaPresent = false;
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaExit"), st), "flighteva-not-ready");
        }

        [Fact]
        public void EvaExit_LoadInFlightBeatsSplitPending()
        {
            // load-in-flight is the fail-fast refuse and takes precedence over the deferrable
            // split-pending / flighteva-not-ready guards.
            var st = FlightEvaReady();
            st.LoadInFlight = true;
            st.StructuralSplitPending = true;
            st.FlightEvaPresent = false;
            AssertReject(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaExit"), st), "load-in-flight");
        }

        [Fact]
        public void EvaExit_Ready_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaExit release=true"), FlightEvaReady());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void EvaExit_ReadyWithRawKerbalArg_Executes()
        {
            // A raw (percent-decoded) kerbal name arg does not affect dispatch readiness.
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("id=1 cmd=EvaExit kerbal=Valentina%20Kerman"), FlightEvaReady());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        // ----- PlantFlag -----

        [Fact]
        public void PlantFlag_OutsideFlight_Defers_NotInFlight()
        {
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=PlantFlag"), MainMenu()), "not-in-flight");
        }

        [Fact]
        public void PlantFlag_ActiveNotEva_Defers_NotEva()
        {
            // In FLIGHT but the preceding EvaExit's auto-switch has not settled the EVA kerbal
            // as the active vessel yet.
            var st = FlightEvaReady(); // ActiveVesselIsEva = false
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=PlantFlag"), st), "not-eva");
        }

        [Fact]
        public void PlantFlag_OnEva_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=PlantFlag"), FlightOnEva());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        // ----- EvaBoard -----

        [Fact]
        public void EvaBoard_OutsideFlight_Defers_NotInFlight()
        {
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaBoard"), MainMenu()), "not-in-flight");
        }

        [Fact]
        public void EvaBoard_ActiveNotEva_Defers_NotEva()
        {
            var st = FlightEvaReady();
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaBoard"), st), "not-eva");
        }

        [Fact]
        public void EvaBoard_OnEva_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaBoard"), FlightOnEva());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void EvaBoard_OnEvaWithTargetPid_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaBoard targetPid=12345"), FlightOnEva());
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        // ----- Shared safe-point / batch gates apply to all three -----

        [Theory]
        [InlineData("EvaExit")]
        [InlineData("PlantFlag")]
        [InlineData("EvaBoard")]
        public void EvaVerbs_DuringSceneTransition_Defer_NotSafePoint(string verb)
        {
            var st = FlightOnEva();
            st.Transitioning = true;
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=" + verb), st), "not-safe-point");
        }

        [Theory]
        [InlineData("EvaExit")]
        [InlineData("PlantFlag")]
        [InlineData("EvaBoard")]
        public void EvaVerbs_BatchRunning_Defer_BatchRunning(string verb)
        {
            var st = FlightOnEva();
            st.BatchRunning = true;
            AssertDefer(TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=" + verb), st), "batch-running");
        }

        [Theory]
        [InlineData("EvaExit")]
        [InlineData("PlantFlag")]
        [InlineData("EvaBoard")]
        public void EvaVerbs_ClaimedJournal_Interrupted(string verb)
        {
            var st = FlightOnEva();
            st.JournalPhase = JournalPhase.Claimed;
            Assert.Equal(DispatchDecision.Interrupted,
                TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=" + verb), st).Decision);
        }
    }
}
