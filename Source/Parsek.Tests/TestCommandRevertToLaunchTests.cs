using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>RevertToLaunch</c>: stock's Revert to Launch outside
    /// a Re-Fly session (vessel names, design 15.3, ruling 5).
    /// </summary>
    public class TestCommandRevertToLaunchTests
    {
        private const string Verb = "RevertToLaunch";

        private static DispatchState At(TestCommandScene scene) => new DispatchState
        {
            Scene = scene,
            GameLoaded = true,
            SettingsPresent = true,
        };

        private static ParsedCommand Cmd()
            => TestCommandParser.ParseLine("id=1 cmd=" + Verb, 1);

        [Fact]
        public void TheVerbIsImplemented_FlightOnly_180s_AndMutating()
        {
            Assert.Equal(TestCommandRevertToLaunch.Verb, Verb);
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
            Assert.Equal(VerbSceneRequirement.RequiresFlight, TestCommandDispatcher.RequirementFor(Verb));
            Assert.Equal(180.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.Equal(DeferralBudget.RevertToLaunchSeconds, DeferralBudget.BudgetSeconds(Verb));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(Verb));
        }

        [Fact]
        public void Dispatch_ExecutesInFlight_WithALiveRecorder()
        {
            // Reverting a recorded flight is the case the verb exists for.
            var st = At(TestCommandScene.Flight);
            st.Recording = true;
            st.HasTree = true;
            Assert.Equal(DispatchDecision.Execute, TestCommandDispatcher.DecideDispatch(Cmd(), st).Decision);
        }

        [Fact]
        public void Dispatch_DefersOutsideFlight()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd(), At(TestCommandScene.SpaceCenter));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal(TestCommandDispatcher.NotInFlightReason, r.Reason);
        }

        [Fact]
        public void Dispatch_RefusesOverALoadOrAMergeJournal()
        {
            var st = At(TestCommandScene.Flight);
            st.LoadInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd(), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);

            st = At(TestCommandScene.Flight);
            st.MergeJournalInFlight = true;
            r = TestCommandDispatcher.DecideDispatch(Cmd(), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        [Fact]
        public void Gate_ReFlyFirst_ThenTheThreeStockConditions()
        {
            Assert.Equal("reverttolaunch-refly-session", TestCommandRevertToLaunch.DecideGate(true, true, true, true));
            Assert.Equal("reverttolaunch-refly-session", TestCommandRevertToLaunch.DecideGate(true, false, false, false));
            Assert.Equal("reverttolaunch-unavailable", TestCommandRevertToLaunch.DecideGate(false, false, true, true));
            Assert.Equal("reverttolaunch-unavailable", TestCommandRevertToLaunch.DecideGate(false, true, false, true));
            Assert.Equal("reverttolaunch-unavailable", TestCommandRevertToLaunch.DecideGate(false, true, true, false));
            Assert.Null(TestCommandRevertToLaunch.DecideGate(false, true, true, true));
        }

        [Fact]
        public void Completion_WaitsForANewReadyFlightWithAnUnpackedActiveVessel()
        {
            // The starting scene is a settled FLIGHT: without a new ParsekFlight it keeps waiting.
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandRevertToLaunch.DecideCompletion(
                1.0, TestCommandScene.Flight, true, false, true, 180.0));
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandRevertToLaunch.DecideCompletion(
                2.0, TestCommandScene.Flight, true, true, false, 180.0));
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandRevertToLaunch.DecideCompletion(
                2.0, TestCommandScene.Loading, true, false, false, 180.0));
            Assert.Equal(LoadCompletionDecision.CompleteOk, TestCommandRevertToLaunch.DecideCompletion(
                3.0, TestCommandScene.Flight, true, true, true, 180.0));
        }

        [Fact]
        public void Completion_MenuFailsFast_AndTheBudgetEndsIt()
        {
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu, TestCommandRevertToLaunch.DecideCompletion(
                3.0, TestCommandScene.MainMenu, true, false, false, 180.0));
            Assert.Equal(LoadCompletionDecision.LoadTimeout, TestCommandRevertToLaunch.DecideCompletion(
                181.0, TestCommandScene.Flight, true, false, true, 180.0));
            Assert.Equal(LoadCompletionDecision.LoadTimeout, TestCommandRevertToLaunch.DecideCompletion(
                181.0, TestCommandScene.Flight, true, true, false, 180.0));
        }

        [Fact]
        public void Lines_AndPayload_AreGrepStable()
        {
            Assert.Equal("reverttolaunch start pid=2200110033 name='VN5 Stock' situation=PRELAUNCH " +
                "recording=false hasActiveTree=true",
                TestCommandRevertToLaunch.FormatStartLine(2200110033, "VN5 Stock", "PRELAUNCH", false, true));
            Assert.Equal("reverttolaunch complete pid=2200110033 name='GS1 Auto-Chute Booster' " +
                "situation=PRELAUNCH elapsed=12.3s",
                TestCommandRevertToLaunch.FormatCompleteLine(2200110033, "GS1 Auto-Chute Booster", "PRELAUNCH", 12.34));
            var payload = TestCommandRevertToLaunch.BuildCompletePayload("FLIGHT", 2200110033, "X", "PRELAUNCH");
            Assert.Equal(new[] { "scene", "activePid", "activeName", "situation" }, payload.Select(kv => kv.Key));
            Assert.Equal("2200110033", payload.Single(kv => kv.Key == "activePid").Value);
        }

        [Fact]
        public void Target_DefaultsToLaunch_PrelaunchIsTheEditorRevert_OthersRefused()
        {
            Assert.Null(TestCommandRevertToLaunch.ParseTarget(null, out bool p));
            Assert.False(p);
            Assert.Null(TestCommandRevertToLaunch.ParseTarget("launch", out p));
            Assert.False(p);
            Assert.Null(TestCommandRevertToLaunch.ParseTarget("prelaunch", out p));
            Assert.True(p);
            Assert.Equal("reverttolaunch-target-arg-invalid", TestCommandRevertToLaunch.ParseTarget("vab", out p));
            Assert.Equal("reverttolaunch-target-arg-invalid", TestCommandRevertToLaunch.ParseTarget("", out p));
            Assert.Equal(new[] { "launch", "prelaunch" }, TestCommandRevertToLaunch.TargetValues);
        }

        [Fact]
        public void Dispatch_ExecutesWithTheTargetArg()
        {
            var cmd = TestCommandParser.ParseLine("id=1 cmd=" + Verb + " target=prelaunch", 1);
            Assert.Equal(DispatchDecision.Execute,
                TestCommandDispatcher.DecideDispatch(cmd, At(TestCommandScene.Flight)).Decision);
        }

        [Fact]
        public void PrelaunchGate_ReFlyFirst_ThenTheFourStockConditions()
        {
            Assert.Equal("reverttolaunch-refly-session",
                TestCommandRevertToLaunch.DecidePrelaunchGate(true, true, true, true, true));
            Assert.Equal("reverttolaunch-prelaunch-unavailable",
                TestCommandRevertToLaunch.DecidePrelaunchGate(false, false, true, true, true));
            Assert.Equal("reverttolaunch-prelaunch-unavailable",
                TestCommandRevertToLaunch.DecidePrelaunchGate(false, true, false, true, true));
            Assert.Equal("reverttolaunch-prelaunch-unavailable",
                TestCommandRevertToLaunch.DecidePrelaunchGate(false, true, true, false, true));
            Assert.Equal("reverttolaunch-prelaunch-unavailable",
                TestCommandRevertToLaunch.DecidePrelaunchGate(false, true, true, true, false));
            Assert.Null(TestCommandRevertToLaunch.DecidePrelaunchGate(false, true, true, true, true));
        }

        [Fact]
        public void PrelaunchCompletion_WaitsForASettledEditorWhoseOnLoadConsumedTheRevert()
        {
            int settle = TestCommandRevertToLaunch.EditorSettleFrames;
            // The starting FLIGHT scene, and the loading screen, keep waiting.
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandRevertToLaunch.DecidePrelaunchCompletion(
                1.0, TestCommandScene.Flight, true, false, 0, false, 180.0));
            // Editor up but the revert kind not consumed yet: the revert branch has not run.
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandRevertToLaunch.DecidePrelaunchCompletion(
                2.0, TestCommandScene.Editor, true, true, settle, false, 180.0));
            // Not settled long enough.
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandRevertToLaunch.DecidePrelaunchCompletion(
                2.0, TestCommandScene.Editor, true, true, settle - 1, true, 180.0));
            Assert.Equal(LoadCompletionDecision.CompleteOk, TestCommandRevertToLaunch.DecidePrelaunchCompletion(
                3.0, TestCommandScene.Editor, true, true, settle, true, 180.0));
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu, TestCommandRevertToLaunch.DecidePrelaunchCompletion(
                3.0, TestCommandScene.MainMenu, false, false, 0, true, 180.0));
            Assert.Equal(LoadCompletionDecision.LoadTimeout, TestCommandRevertToLaunch.DecidePrelaunchCompletion(
                181.0, TestCommandScene.Editor, true, true, settle, false, 180.0));
        }

        [Fact]
        public void PrelaunchLines_AndPayload_AreGrepStable()
        {
            Assert.Equal("reverttolaunch start target=prelaunch pid=7 name='X' situation=FLYING " +
                "recording=true hasActiveTree=true",
                TestCommandRevertToLaunch.FormatStartLine(7, "X", "FLYING", true, true, prelaunch: true));
            Assert.Equal("reverttolaunch complete target=prelaunch facility=VAB ship='Kerbal X Sweep' elapsed=4.5s",
                TestCommandRevertToLaunch.FormatPrelaunchCompleteLine("VAB", "Kerbal X Sweep", 4.5));
            var payload = TestCommandRevertToLaunch.BuildPrelaunchCompletePayload("EDITOR", "VAB", "S");
            Assert.Equal(new[] { "scene", "facility", "ship" }, payload.Select(kv => kv.Key));
        }

        [Fact]
        public void Reasons_AreDisjointFromTheErrors()
        {
            Assert.Equal(new[] { "reverttolaunch-target-arg-invalid", "reverttolaunch-refly-session",
                    "reverttolaunch-unavailable", "reverttolaunch-prelaunch-unavailable" },
                TestCommandRevertToLaunch.Reasons);
            Assert.Empty(TestCommandRevertToLaunch.Reasons.Intersect(TestCommandRevertToLaunch.ErrorReasons));
        }
    }
}
