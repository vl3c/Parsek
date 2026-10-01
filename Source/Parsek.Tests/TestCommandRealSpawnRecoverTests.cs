using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure halves and dispatch rows of the D18 player-action seam pair:
    /// <c>RealSpawn rec=</c> (the Real Spawn Control row warp for one recording) and
    /// <c>Recover pid=</c> (the flight scene's stock Recover button).
    /// </summary>
    public class TestCommandRealSpawnRecoverTests
    {
        private static DispatchState Flight() => new DispatchState
        {
            Scene = TestCommandScene.Flight,
            GameLoaded = true,
            SettingsPresent = true,
        };

        private static ParsedCommand Cmd(string verb, string args)
            => TestCommandParser.ParseLine("id=1 cmd=" + verb + " " + args, 1);

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.Single(kv => kv.Key == key).Value;

        // ----- dispatch -----

        [Theory]
        [InlineData("RealSpawn", "rec=abc")]
        [InlineData("Recover", "pid=42")]
        public void Dispatch_InFlightWithLiveRecorder_Executes(string verb, string args)
        {
            var st = Flight();
            st.Recording = true;
            st.HasTree = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd(verb, args), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Theory]
        [InlineData("RealSpawn", "rec=abc")]
        [InlineData("Recover", "pid=42")]
        public void Dispatch_LoadInFlight_RejectsAheadOfMergeJournal(string verb, string args)
        {
            var st = Flight();
            st.LoadInFlight = true;
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd(verb, args), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Theory]
        [InlineData("RealSpawn", "rec=abc")]
        [InlineData("Recover", "pid=42")]
        public void Dispatch_MergeJournalInFlight_Rejects(string verb, string args)
        {
            var st = Flight();
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd(verb, args), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        [Theory]
        [InlineData("RealSpawn")]
        [InlineData("Recover")]
        public void Dispatch_AtTheSpaceCenter_DoesNotExecute(string verb)
        {
            var st = new DispatchState
            {
                Scene = TestCommandScene.SpaceCenter,
                GameLoaded = true,
                SettingsPresent = true,
            };
            var r = TestCommandDispatcher.DecideDispatch(Cmd(verb, "rec=a pid=1"), st);
            Assert.NotEqual(DispatchDecision.Execute, r.Decision);
        }

        [Theory]
        [InlineData("RealSpawn")]
        [InlineData("Recover")]
        public void Budget_Is120Seconds(string verb)
        {
            Assert.Equal(120.0, DeferralBudget.BudgetSeconds(verb));
        }

        // ----- RealSpawn -----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void RealSpawn_MissingRec_Refuses(string raw)
        {
            Assert.Equal("realspawn-rec-arg-missing", TestCommandRealSpawn.ValidateRecArg(raw));
        }

        [Fact]
        public void RealSpawn_PresentRec_Accepts()
        {
            Assert.Null(TestCommandRealSpawn.ValidateRecArg("6f1c0d"));
        }

        [Theory]
        // recordingFound, alreadySpawned, hasRow, buttonEnabled, usesDeparture -> decision
        [InlineData(false, false, true, true, false, "UnknownRecording")]
        [InlineData(true, true, true, true, false, "AlreadySpawned")]
        [InlineData(true, false, false, true, false, "NotACandidate")]
        [InlineData(true, false, true, false, false, "ButtonDisabled")]
        [InlineData(true, false, true, true, true, "RowWarpsToDeparture")]
        [InlineData(true, false, true, true, false, "Proceed")]
        // Order: an unknown recording outranks everything, a disabled button outranks
        // the departure-warp label (a greyed button is refused for being greyed).
        [InlineData(false, true, false, false, true, "UnknownRecording")]
        [InlineData(true, false, true, false, true, "ButtonDisabled")]
        public void RealSpawn_Gate(bool found, bool spawned, bool hasRow, bool enabled,
            bool departure, string expectedName)
        {
            var expected = (RealSpawnGateDecision)System.Enum.Parse(
                typeof(RealSpawnGateDecision), expectedName);
            Assert.Equal(expected,
                TestCommandRealSpawn.DecideGate(found, spawned, hasRow, enabled, departure));
        }

        [Fact]
        public void RealSpawn_EveryNonProceedGateHasAReasonInTheTable()
        {
            Assert.Null(TestCommandRealSpawn.GateReason(RealSpawnGateDecision.Proceed));
            foreach (RealSpawnGateDecision gate in System.Enum.GetValues(typeof(RealSpawnGateDecision)))
            {
                if (gate == RealSpawnGateDecision.Proceed) continue;
                Assert.Contains(TestCommandRealSpawn.GateReason(gate), TestCommandRealSpawn.Reasons);
            }
        }

        [Theory]
        [InlineData(1000.0, 1000.0, true)]
        [InlineData(999.995, 1000.0, true)]
        [InlineData(999.9, 1000.0, false)]
        [InlineData(500.0, 1000.0, false)]
        public void RealSpawn_WarpApplied_ReadBack(double utAfter, double endUT, bool expected)
        {
            Assert.Equal(expected, TestCommandRealSpawn.WarpApplied(utAfter, endUT));
        }

        [Fact]
        public void RealSpawn_Completion_SpawnedWinsOverAbandonedAndBudget()
        {
            Assert.Equal(RealSpawnCompletionDecision.Spawned,
                TestCommandRealSpawn.DecideCompletion(true, true, 7u, true, 500, 120));
        }

        [Fact]
        public void RealSpawn_Completion_SpawnedNeedsANonZeroPid()
        {
            Assert.Equal(RealSpawnCompletionDecision.StillWaiting,
                TestCommandRealSpawn.DecideCompletion(true, true, 0u, false, 5, 120));
        }

        [Fact]
        public void RealSpawn_Completion_AbandonedIsFast()
        {
            Assert.Equal(RealSpawnCompletionDecision.Abandoned,
                TestCommandRealSpawn.DecideCompletion(true, false, 0u, true, 1, 120));
        }

        [Fact]
        public void RealSpawn_Completion_TimesOutAtTheBudget()
        {
            Assert.Equal(RealSpawnCompletionDecision.StillWaiting,
                TestCommandRealSpawn.DecideCompletion(true, false, 0u, false, 119.9, 120));
            Assert.Equal(RealSpawnCompletionDecision.Timeout,
                TestCommandRealSpawn.DecideCompletion(true, false, 0u, false, 120, 120));
            // A recording that vanished mid-wait (a store swap) can only time out.
            Assert.Equal(RealSpawnCompletionDecision.Timeout,
                TestCommandRealSpawn.DecideCompletion(false, true, 7u, true, 120, 120));
        }

        [Fact]
        public void RealSpawn_OkPayload_IsInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandRealSpawn.BuildOkPayload("r1", 4000000001u, "Probe", 1234.5, true);
                Assert.Equal(new[] { "rec", "pid", "vessel", "endUT", "loaded" },
                    p.Select(kv => kv.Key).ToArray());
                Assert.Equal("4000000001", Value(p, "pid"));
                Assert.Equal("1234.5", Value(p, "endUT"));
                Assert.Equal("true", Value(p, "loaded"));
                Assert.Equal("realspawn pressed rec=r1 index=3 vessel=Probe endUT=1234.5 utBefore=10.0",
                    TestCommandRealSpawn.FormatPressedLine("r1", 3, "Probe", 1234.5, 10.0));
                Assert.Equal("realspawn complete rec=r1 pid=9 vessel=Probe loaded=false elapsed=0.5s",
                    TestCommandRealSpawn.FormatCompleteLine("r1", 9u, "Probe", false, 0.5));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void RealSpawn_ReasonsArePrefixedAndDistinct()
        {
            var all = TestCommandRealSpawn.Reasons.Concat(TestCommandRealSpawn.ErrorReasons).ToList();
            Assert.All(all, r => Assert.StartsWith("realspawn-", r));
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        // ----- Recover -----

        [Theory]
        [InlineData("1", 1u)]
        [InlineData("4294967295", 4294967295u)]
        public void Recover_ParsePid_Accepts(string raw, uint expected)
        {
            Assert.Null(TestCommandRecover.ParsePid(raw, out uint pid));
            Assert.Equal(expected, pid);
        }

        [Theory]
        [InlineData(null, "recover-pid-arg-missing")]
        [InlineData("", "recover-pid-arg-missing")]
        [InlineData("0", "recover-pid-arg-invalid")]
        [InlineData("-5", "recover-pid-arg-invalid")]
        [InlineData("+5", "recover-pid-arg-invalid")]
        [InlineData("1.0", "recover-pid-arg-invalid")]
        [InlineData("4294967296", "recover-pid-arg-invalid")]
        [InlineData("${spawn.pid}", "recover-pid-arg-invalid")]
        public void Recover_ParsePid_Refuses(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandRecover.ParsePid(raw, out uint pid));
            Assert.Equal(0u, pid);
        }

        [Theory]
        // hasActive, activePid, pid, buttonFound, interactable, clearToSave, canLeave -> reason
        [InlineData(false, 0u, 5u, true, true, "CLEAR", true, "recover-no-active-vessel")]
        [InlineData(true, 6u, 5u, true, true, "CLEAR", true, "recover-not-active-vessel")]
        [InlineData(true, 5u, 5u, false, false, "CLEAR", true, "recover-button-unavailable")]
        [InlineData(true, 5u, 5u, true, false, "CLEAR", true, "recover-button-locked")]
        [InlineData(true, 5u, 5u, true, true, "NOT_WHILE_ON_A_LADDER", true, "recover-not-clear-to-save")]
        [InlineData(true, 5u, 5u, true, true, "NOT_IN_ATMOSPHERE", true, "recover-not-clear-to-save")]
        [InlineData(true, 5u, 5u, true, true, "CLEAR", false, "recover-cannot-leave-to-space-center")]
        [InlineData(true, 5u, 5u, true, true, "CLEAR", true, null)]
        // A wrong pid is refused before the button is looked at: naming the vessel is
        // the spec's half, the button's state is the game's.
        [InlineData(true, 6u, 5u, false, false, "", false, "recover-not-active-vessel")]
        public void Recover_Gate(bool hasActive, uint activePid, uint pid, bool found,
            bool interactable, string clearToSave, bool canLeave, string expected)
        {
            Assert.Equal(expected, TestCommandRecover.DecideGate(
                hasActive, activePid, pid, found, interactable, clearToSave, canLeave));
        }

        [Fact]
        public void Recover_EveryGateReasonIsInTheTable()
        {
            string[] gateReasons =
            {
                TestCommandRecover.NoActiveVesselReason, TestCommandRecover.NotActiveVesselReason,
                TestCommandRecover.ButtonUnavailableReason, TestCommandRecover.ButtonLockedReason,
                TestCommandRecover.NotClearToSaveReason, TestCommandRecover.CannotLeaveReason,
                TestCommandRecover.PidArgMissingReason, TestCommandRecover.PidArgInvalidReason,
            };
            Assert.Equal(gateReasons.OrderBy(r => r), TestCommandRecover.Reasons.OrderBy(r => r));
        }

        [Fact]
        public void Recover_Completion_NeedsTheRecoveryNotJustTheScene()
        {
            // The Space Center settles 8 frames before stock recovers the vessel.
            Assert.Equal(RecoverCompletionDecision.StillWaiting,
                TestCommandRecover.DecideCompletion(5, TestCommandScene.SpaceCenter, true, false, 120));
            Assert.Equal(RecoverCompletionDecision.Recovered,
                TestCommandRecover.DecideCompletion(5, TestCommandScene.SpaceCenter, true, true, 120));
            // A recovery observed while the scene is still loading is not yet OK.
            Assert.Equal(RecoverCompletionDecision.StillWaiting,
                TestCommandRecover.DecideCompletion(5, TestCommandScene.Loading, true, true, 120));
            Assert.Equal(RecoverCompletionDecision.StillWaiting,
                TestCommandRecover.DecideCompletion(5, TestCommandScene.SpaceCenter, false, true, 120));
        }

        [Fact]
        public void Recover_Completion_MenuIsFastAndBudgetIsTheCatchAll()
        {
            Assert.Equal(RecoverCompletionDecision.ReturnedToMenu,
                TestCommandRecover.DecideCompletion(1, TestCommandScene.MainMenu, false, false, 120));
            Assert.Equal(RecoverCompletionDecision.Timeout,
                TestCommandRecover.DecideCompletion(120, TestCommandScene.SpaceCenter, true, false, 120));
            Assert.Equal(RecoverCompletionDecision.Timeout,
                TestCommandRecover.DecideCompletion(120, TestCommandScene.Flight, true, false, 120));
        }

        [Fact]
        public void Recover_OkPayloadAndLines_AreInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandRecover.BuildOkPayload(77u, "Pod", "SPACECENTER");
                Assert.Equal(new[] { "pid", "vessel", "scene", "recovered" },
                    p.Select(kv => kv.Key).ToArray());
                Assert.Equal("77", Value(p, "pid"));
                Assert.Equal("true", Value(p, "recovered"));
                Assert.Equal("recover pressed pid=77 vessel=Pod situation=LANDED ut=1234.5",
                    TestCommandRecover.FormatPressedLine(77u, "Pod", "LANDED", 1234.5));
                Assert.Equal("recover complete pid=77 vessel=Pod scene=SPACECENTER elapsed=3.2s",
                    TestCommandRecover.FormatCompleteLine(77u, "Pod", "SPACECENTER", 3.2));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Recover_ReasonsArePrefixedAndDistinct()
        {
            var all = TestCommandRecover.Reasons.Concat(TestCommandRecover.ErrorReasons).ToList();
            Assert.All(all, r => Assert.StartsWith("recover-", r));
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        [Fact]
        public void Recover_WedgeRefusal_ReusesExitToSpaceCentersToken()
        {
            Assert.DoesNotContain(TestCommandExitToSpaceCenter.DialogRequiredReason,
                TestCommandRecover.Reasons);
            Assert.StartsWith("dialog-required variant=",
                TestCommandExitToSpaceCenter.RefusalMsg("MergeDialog"));
        }
    }
}
