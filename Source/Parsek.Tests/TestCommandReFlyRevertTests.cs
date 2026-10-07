using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>ReFlyRevert choice= target=</c>: the Esc menu's
    /// Revert during a live Re-Fly session, answered on Parsek's Re-Fly revert dialog.
    /// </summary>
    public class TestCommandReFlyRevertTests
    {
        private const string Verb = "ReFlyRevert";
        private const double ResumeBudget = 30.0;

        private static DispatchState At(TestCommandScene scene) => new DispatchState
        {
            Scene = scene,
            GameLoaded = true,
            SettingsPresent = true,
        };

        private static ParsedCommand Cmd(string args)
            => TestCommandParser.ParseLine("id=1 cmd=" + Verb + " " + args, 1);

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.Single(kv => kv.Key == key).Value;

        private static ReFlyRevertObservation Flight(int frames = 5) => new ReFlyRevertObservation
        {
            Scene = TestCommandScene.Flight,
            GameLoaded = true,
            FramesInPhase = frames,
        };

        private static ReFlyRevertPollAction Poll(
            ReFlyRevertPhase phase, ReFlyRevertObservation o,
            ReFlyRevertChoice choice = ReFlyRevertChoice.Discard,
            RevertTarget target = RevertTarget.Launch)
            => TestCommandReFlyRevert.DecidePoll(phase, choice, target, o, ResumeBudget);

        // ----- dispatch -----

        [Fact]
        public void Dispatch_InFlight_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("choice=discard target=launch"), At(TestCommandScene.Flight));
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_AtTheSpaceCenter_ExecutesSoTheVerbRefusesTyped()
        {
            // RequiresGameLoaded: the wrong scene is the verb's own REJECTED, never a
            // 300 s not-in-flight defer.
            var r = TestCommandDispatcher.DecideDispatch(
                Cmd("choice=discard target=launch"), At(TestCommandScene.SpaceCenter));
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_GameNotLoaded_Defers()
        {
            var st = At(TestCommandScene.MainMenu);
            st.SettingsPresent = false;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("choice=discard target=launch"), st);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("game-not-loaded", r.Reason);
        }

        [Fact]
        public void Dispatch_LoadInFlight_RejectsAheadOfMergeJournal()
        {
            var st = At(TestCommandScene.Flight);
            st.LoadInFlight = true;
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("choice=discard target=launch"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_MergeJournalInFlight_Rejects()
        {
            var st = At(TestCommandScene.Flight);
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("choice=discard target=launch"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_LiveRecorder_StillExecutes()
        {
            // The re-fly's own recorder is live during every session the verb exists for.
            var st = At(TestCommandScene.Flight);
            st.Recording = true;
            st.HasTree = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("choice=discard target=launch"), st);
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Budget_Is300Seconds_AndTheVerbIsImplemented()
        {
            Assert.Equal(300.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.Equal(TestCommandReFlyRevert.Verb, Verb);
            Assert.Equal(VerbSceneRequirement.RequiresGameLoaded, TestCommandDispatcher.RequirementFor(Verb));
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(Verb));
        }

        // ----- args -----

        [Theory]
        [InlineData("discard", "Discard")]
        [InlineData("retry", "Retry")]
        [InlineData("cancel", "Cancel")]
        public void ParseChoice_Accepts(string raw, string expectedName)
        {
            var expected = (ReFlyRevertChoice)System.Enum.Parse(typeof(ReFlyRevertChoice), expectedName);
            Assert.Null(TestCommandReFlyRevert.ParseChoice(raw, out ReFlyRevertChoice choice));
            Assert.Equal(expected, choice);
            Assert.Equal(raw, TestCommandReFlyRevert.ChoiceToken(choice));
        }

        [Theory]
        [InlineData(null, "reflyrevert-choice-arg-missing")]
        [InlineData("", "reflyrevert-choice-arg-missing")]
        [InlineData("Discard", "reflyrevert-choice-arg-invalid")]
        [InlineData("merge", "reflyrevert-choice-arg-invalid")]
        [InlineData("continue", "reflyrevert-choice-arg-invalid")]
        public void ParseChoice_Refuses(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandReFlyRevert.ParseChoice(raw, out ReFlyRevertChoice choice));
            Assert.Equal(ReFlyRevertChoice.Unknown, choice);
        }

        [Theory]
        [InlineData("launch", "Launch")]
        [InlineData("prelaunch", "Prelaunch")]
        public void ParseTarget_Accepts(string raw, string expectedName)
        {
            var expected = (RevertTarget)System.Enum.Parse(typeof(RevertTarget), expectedName);
            Assert.Null(TestCommandReFlyRevert.ParseTarget(raw, out RevertTarget target));
            Assert.Equal(expected, target);
            Assert.Equal(raw, TestCommandReFlyRevert.TargetToken(target));
        }

        [Theory]
        [InlineData(null, "reflyrevert-target-arg-missing")]
        [InlineData("", "reflyrevert-target-arg-missing")]
        [InlineData("Launch", "reflyrevert-target-arg-invalid")]
        [InlineData("vab", "reflyrevert-target-arg-invalid")]
        public void ParseTarget_Refuses(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandReFlyRevert.ParseTarget(raw, out _));
        }

        [Fact]
        public void TheClosedVocabulariesAreTheParsers()
        {
            Assert.Equal(new[] { "discard", "retry", "cancel" }, TestCommandReFlyRevert.ChoiceValues);
            Assert.Equal(new[] { "launch", "prelaunch" }, TestCommandReFlyRevert.TargetValues);
            foreach (string v in TestCommandReFlyRevert.ChoiceValues)
                Assert.Null(TestCommandReFlyRevert.ParseChoice(v, out _));
            foreach (string v in TestCommandReFlyRevert.TargetValues)
                Assert.Null(TestCommandReFlyRevert.ParseTarget(v, out _));
        }

        // ----- gate -----

        [Theory]
        [InlineData("Flight", true, null)]
        [InlineData("Flight", false, "reflyrevert-no-session")]
        [InlineData("SpaceCenter", true, "reflyrevert-wrong-scene")]
        [InlineData("SpaceCenter", false, "reflyrevert-wrong-scene")]
        [InlineData("Editor", true, "reflyrevert-wrong-scene")]
        [InlineData("TrackingStation", true, "reflyrevert-wrong-scene")]
        public void Gate(string sceneName, bool marker, string expected)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            Assert.Equal(expected, TestCommandReFlyRevert.DecideGate(scene, marker));
        }

        [Fact]
        public void EveryReasonIsInTheTable()
        {
            string[] reasons =
            {
                TestCommandReFlyRevert.ChoiceArgMissingReason,
                TestCommandReFlyRevert.ChoiceArgInvalidReason,
                TestCommandReFlyRevert.TargetArgMissingReason,
                TestCommandReFlyRevert.TargetArgInvalidReason,
                TestCommandReFlyRevert.WrongSceneReason,
                TestCommandReFlyRevert.NoSessionReason,
                TestCommandReFlyRevert.PauseMenuUnavailableReason,
                TestCommandReFlyRevert.RevertUnavailableReason,
                TestCommandReFlyRevert.OptionUnavailableReason,
                TestCommandReFlyRevert.DialogNotShownReason,
                TestCommandReFlyRevert.ChoiceUnavailableReason,
            };
            Assert.Equal(reasons, TestCommandReFlyRevert.Reasons);
            string[] errors =
            {
                TestCommandReFlyRevert.DiscardNotDispatchedReason,
                TestCommandReFlyRevert.RetryNotStartedReason,
                TestCommandReFlyRevert.SessionChangedReason,
                TestCommandReFlyRevert.WrongDestinationReason,
                TestCommandReFlyRevert.ReturnedToMenuReason,
                TestCommandReFlyRevert.TimeoutReason,
            };
            Assert.Equal(errors, TestCommandReFlyRevert.ErrorReasons);
            var all = reasons.Concat(errors).ToList();
            Assert.All(all, r => Assert.StartsWith("reflyrevert-", r));
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        // ----- the stock revert option -----

        [Theory]
        // Launch only (the ordinary Re-Fly session: ReFlyRevertButtonGate forces
        // CanRevertToPostInit; CanRevertToPrelaunch is false for a resumed flight).
        [InlineData("Launch", true, false, 2, 0)]
        [InlineData("Prelaunch", true, false, 2, -1)]
        // Both offered: Launch first, then VAB / SPH, then back.
        [InlineData("Launch", true, true, 3, 0)]
        [InlineData("Prelaunch", true, true, 3, 1)]
        // Editor only.
        [InlineData("Prelaunch", false, true, 2, 0)]
        [InlineData("Launch", false, true, 2, -1)]
        // Neither: only the back button.
        [InlineData("Launch", false, false, 1, -1)]
        // A count that does not match the predicates is a moved layout, never a guess.
        [InlineData("Launch", true, false, 3, -1)]
        [InlineData("Prelaunch", true, true, 2, -1)]
        public void ResolveRevertOptionIndex_FollowsStockOrder(
            string targetName, bool launch, bool prelaunch, int buttons, int expected)
        {
            var target = (RevertTarget)System.Enum.Parse(typeof(RevertTarget), targetName);
            Assert.Equal(expected,
                TestCommandReFlyRevert.ResolveRevertOptionIndex(target, launch, prelaunch, buttons));
        }

        [Theory]
        [InlineData("Discard", "Launch", "SpaceCenter")]
        [InlineData("Discard", "Prelaunch", "Editor")]
        [InlineData("Retry", "Launch", "Flight")]
        [InlineData("Retry", "Prelaunch", "Flight")]
        [InlineData("Cancel", "Prelaunch", "Flight")]
        public void ExpectedDestination_FollowsTheHandlers(string choiceName, string targetName, string sceneName)
        {
            var choice = (ReFlyRevertChoice)System.Enum.Parse(typeof(ReFlyRevertChoice), choiceName);
            var target = (RevertTarget)System.Enum.Parse(typeof(RevertTarget), targetName);
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            Assert.Equal(scene, TestCommandReFlyRevert.ExpectedDestination(choice, target));
        }

        // ----- the click walk, before a dialog button -----

        [Fact]
        public void AwaitingResume_WaitsForTheTreeThenOpensTheMenu()
        {
            var o = Flight();
            o.ElapsedSeconds = 2.0;
            Assert.Equal(ReFlyRevertPollAction.NotYet, Poll(ReFlyRevertPhase.AwaitingResume, o));
            o.HasActiveTree = true;
            Assert.Equal(ReFlyRevertPollAction.OpenPauseMenu, Poll(ReFlyRevertPhase.AwaitingResume, o));
            // A resume that never settles still reaches the menu once the settle budget ran out.
            o.HasActiveTree = false;
            o.ElapsedSeconds = ResumeBudget;
            Assert.Equal(ReFlyRevertPollAction.OpenPauseMenu, Poll(ReFlyRevertPhase.AwaitingResume, o));
            o.Scene = TestCommandScene.SpaceCenter;
            Assert.Equal(ReFlyRevertPollAction.WrongDestination, Poll(ReFlyRevertPhase.AwaitingResume, o));
        }

        [Fact]
        public void OpeningMenu_PressesOnlyAnInteractableRevertFlight()
        {
            var o = Flight();
            Assert.Equal(ReFlyRevertPollAction.NotYet, Poll(ReFlyRevertPhase.OpeningMenu, o));
            o.FramesInPhase = TestCommandReFlyRevert.MenuWaitFrames;
            Assert.Equal(ReFlyRevertPollAction.PauseMenuUnavailable, Poll(ReFlyRevertPhase.OpeningMenu, o));
            o.MenuPopupFound = true;
            Assert.Equal(ReFlyRevertPollAction.RevertUnavailable, Poll(ReFlyRevertPhase.OpeningMenu, o));
            o.RevertButtonFound = true;
            // Greyed (FlightDriver.CanRevert false): a player could not press it.
            Assert.Equal(ReFlyRevertPollAction.RevertUnavailable, Poll(ReFlyRevertPhase.OpeningMenu, o));
            o.RevertButtonInteractable = true;
            o.FramesInPhase = 1;
            Assert.Equal(ReFlyRevertPollAction.PressRevertFlight, Poll(ReFlyRevertPhase.OpeningMenu, o));
        }

        [Fact]
        public void ChoosingRevertOption_RefusesAnAbsentOrGreyedOption()
        {
            var o = Flight();
            Assert.Equal(ReFlyRevertPollAction.NotYet, Poll(ReFlyRevertPhase.ChoosingRevertOption, o));
            o.FramesInPhase = TestCommandReFlyRevert.MenuWaitFrames;
            Assert.Equal(ReFlyRevertPollAction.RevertUnavailable, Poll(ReFlyRevertPhase.ChoosingRevertOption, o));
            o.OptionPopupFound = true;
            Assert.Equal(ReFlyRevertPollAction.OptionUnavailable, Poll(ReFlyRevertPhase.ChoosingRevertOption, o));
            o.OptionButtonFound = true;
            Assert.Equal(ReFlyRevertPollAction.OptionUnavailable, Poll(ReFlyRevertPhase.ChoosingRevertOption, o));
            o.OptionButtonInteractable = true;
            Assert.Equal(ReFlyRevertPollAction.PressRevertOption, Poll(ReFlyRevertPhase.ChoosingRevertOption, o));
        }

        [Fact]
        public void AwaitingDialog_RefusesAMissingDialogOrChoice()
        {
            var o = Flight();
            Assert.Equal(ReFlyRevertPollAction.NotYet, Poll(ReFlyRevertPhase.AwaitingDialog, o));
            o.FramesInPhase = TestCommandReFlyRevert.DialogWaitFrames;
            Assert.Equal(ReFlyRevertPollAction.DialogNotShown, Poll(ReFlyRevertPhase.AwaitingDialog, o));
            // A stock revert the prefix did not block reloads the flight; the dialog is
            // still missing when the pump resumes, in whatever scene.
            o.Scene = TestCommandScene.SpaceCenter;
            Assert.Equal(ReFlyRevertPollAction.DialogNotShown, Poll(ReFlyRevertPhase.AwaitingDialog, o));
            o.Scene = TestCommandScene.Flight;
            o.DialogFound = true;
            Assert.Equal(ReFlyRevertPollAction.ChoiceUnavailable, Poll(ReFlyRevertPhase.AwaitingDialog, o));
            o.ChoiceButtonFound = true;
            Assert.Equal(ReFlyRevertPollAction.AnswerDialog, Poll(ReFlyRevertPhase.AwaitingDialog, o));
        }

        [Theory]
        [InlineData("AwaitingResume")]
        [InlineData("OpeningMenu")]
        [InlineData("ChoosingRevertOption")]
        [InlineData("AwaitingDialog")]
        [InlineData("Settling")]
        public void MenuIsFastInEveryPhase(string phaseName)
        {
            var phase = (ReFlyRevertPhase)System.Enum.Parse(typeof(ReFlyRevertPhase), phaseName);
            var o = Flight();
            o.Scene = TestCommandScene.MainMenu;
            Assert.Equal(ReFlyRevertPollAction.ReturnedToMenu, Poll(phase, o));
        }

        [Theory]
        [InlineData("AwaitingResume")]
        [InlineData("OpeningMenu")]
        [InlineData("ChoosingRevertOption")]
        [InlineData("AwaitingDialog")]
        public void TheBudgetEndsEveryPrePressPhase(string phaseName)
        {
            var phase = (ReFlyRevertPhase)System.Enum.Parse(typeof(ReFlyRevertPhase), phaseName);
            var o = Flight();
            o.Expired = true;
            Assert.Equal(ReFlyRevertPollAction.Timeout, Poll(phase, o));
        }

        // ----- the outcome -----

        [Fact]
        public void Discard_WaitsForTheDestinationOnLoadThenSettles()
        {
            // Still in FLIGHT: the LoadScene was requested, the destination is not up yet.
            var o = Flight();
            o.DiscardIntentArmed = true;
            Assert.Equal(ReFlyRevertPollAction.NotYet, Poll(ReFlyRevertPhase.Settling, o));
            // In the Space Center, OnLoad not yet consumed the intent.
            o.Scene = TestCommandScene.SpaceCenter;
            Assert.Equal(ReFlyRevertPollAction.NotYet, Poll(ReFlyRevertPhase.Settling, o));
            // Consumed, but not yet settled for two polls.
            o.DiscardIntentArmed = false;
            o.SettledPolls = 1;
            Assert.Equal(ReFlyRevertPollAction.NotYet, Poll(ReFlyRevertPhase.Settling, o));
            o.SettledPolls = TestCommandReFlyRevert.SettlePolls;
            Assert.Equal(ReFlyRevertPollAction.Ok, Poll(ReFlyRevertPhase.Settling, o));
            // The wrong destination is an ERROR, never an OK.
            o.Scene = TestCommandScene.Editor;
            Assert.Equal(ReFlyRevertPollAction.WrongDestination, Poll(ReFlyRevertPhase.Settling, o));
            Assert.Equal(ReFlyRevertPollAction.Ok,
                Poll(ReFlyRevertPhase.Settling, o, target: RevertTarget.Prelaunch));
            o.Expired = true;
            Assert.Equal(ReFlyRevertPollAction.Timeout, Poll(ReFlyRevertPhase.Settling, o));
        }

        [Fact]
        public void Retry_IsOkOnlyOnAFreshSession()
        {
            var o = Flight();
            o.InvokePending = true;
            Assert.Equal(ReFlyRevertPollAction.NotYet,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Retry));
            o.InvokePending = false;
            o.MarkerPresent = true;
            o.MarkerIsOriginalSession = true;
            Assert.Equal(ReFlyRevertPollAction.NotYet,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Retry));
            o.FramesInPhase = TestCommandReFlyRevert.RetryStartWaitFrames;
            Assert.Equal(ReFlyRevertPollAction.RetryNotStarted,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Retry));
            o.MarkerIsOriginalSession = false;
            Assert.Equal(ReFlyRevertPollAction.Ok,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Retry));
            o.Scene = TestCommandScene.SpaceCenter;
            Assert.Equal(ReFlyRevertPollAction.WrongDestination,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Retry));
        }

        [Fact]
        public void Cancel_KeepsTheSession()
        {
            var o = Flight();
            o.MarkerPresent = true;
            o.MarkerIsOriginalSession = true;
            o.DialogVisible = true;
            Assert.Equal(ReFlyRevertPollAction.NotYet,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Cancel));
            o.DialogVisible = false;
            Assert.Equal(ReFlyRevertPollAction.Ok,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Cancel));
            o.MarkerIsOriginalSession = false;
            Assert.Equal(ReFlyRevertPollAction.SessionChanged,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Cancel));
            o.MarkerPresent = false;
            Assert.Equal(ReFlyRevertPollAction.SessionChanged,
                Poll(ReFlyRevertPhase.Settling, o, ReFlyRevertChoice.Cancel));
        }

        // ----- the exit cleanup -----

        [Theory]
        // Still in FLIGHT, the verb opened the menu and it is open: close it (a REJECTED,
        // discard-not-dispatched, retry-not-started or a Settling timeout before the load).
        [InlineData("Flight", true, true, true)]
        // A menu the verb did not open (a player or an earlier step opened it) is left alone.
        [InlineData("Flight", false, true, false)]
        // Already closed: nothing to do.
        [InlineData("Flight", true, false, false)]
        // A scene load took the menu with it (Discard landed, Retry reloaded the flight
        // into a new scene is still FLIGHT but its menu is a new, closed one).
        [InlineData("SpaceCenter", true, true, false)]
        [InlineData("Editor", true, true, false)]
        [InlineData("MainMenu", true, true, false)]
        public void ShouldCloseMenusOnExit_OnlyInFlightOnlyOurs(
            string sceneName, bool ours, bool open, bool expected)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            Assert.Equal(expected, TestCommandReFlyRevert.ShouldCloseMenusOnExit(scene, ours, open));
        }

        [Theory]
        [InlineData("Flight", true, true)]
        [InlineData("Flight", false, false)]
        [InlineData("SpaceCenter", true, false)]
        [InlineData("Editor", true, false)]
        public void ShouldBackOutOfDialogOnExit_OnlyInFlightWhileUp(string sceneName, bool open, bool expected)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            Assert.Equal(expected, TestCommandReFlyRevert.ShouldBackOutOfDialogOnExit(scene, open));
        }

        [Fact]
        public void ExitCleanupLine_IsStable()
        {
            Assert.Equal(
                "reflyrevert exit cleanup scene=FLIGHT menuOurs=true menuOpen=true dialogOpen=false"
                + " closedMenu=true backedOutOfDialog=false - terminal ERROR reflyrevert-discard-not-dispatched",
                TestCommandReFlyRevert.FormatExitCleanupLine(
                    "terminal ERROR reflyrevert-discard-not-dispatched", "FLIGHT", true, true, false,
                    true, false));
        }

        [Fact]
        public void IsSlotListed_MatchesTheRewindPointAndTheSlotOrigin()
        {
            var listed = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("rp_a", "rec_upper"),
                new KeyValuePair<string, string>("rp_b9_root", "rec_booster"),
            };
            Assert.True(TestCommandReFlyRevert.IsSlotListed(listed, "rp_b9_root", "rec_booster"));
            Assert.False(TestCommandReFlyRevert.IsSlotListed(listed, "rp_b9_root", "rec_upper"));
            Assert.False(TestCommandReFlyRevert.IsSlotListed(listed, "rp_a", "rec_booster"));
            Assert.False(TestCommandReFlyRevert.IsSlotListed(listed, null, "rec_booster"));
            Assert.False(TestCommandReFlyRevert.IsSlotListed(listed, "rp_b9_root", ""));
            Assert.False(TestCommandReFlyRevert.IsSlotListed(null, "rp_b9_root", "rec_booster"));
            Assert.False(TestCommandReFlyRevert.IsSlotListed(
                new List<KeyValuePair<string, string>>(), "rp_b9_root", "rec_booster"));
        }

        [Fact]
        public void TheDialogButtonsTheVerbFindsAreTheDialogsOwn()
        {
            Assert.Equal("Retry from Rewind Point", ReFlyRevertDialog.RetryButtonText);
            Assert.Equal("Discard Re-Fly", ReFlyRevertDialog.DiscardButtonText);
            Assert.Equal("Continue Flying", ReFlyRevertDialog.ContinueButtonText);
            Assert.Equal("ParsekReFlyRevert", ReFlyRevertDialog.DialogName);
        }

        [Fact]
        public void OkPayloadAndLines_AreInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandReFlyRevert.BuildOkPayload(
                    ReFlyRevertChoice.Discard, RevertTarget.Launch, "SPACECENTER", "sess1", null,
                    "rp_b9_root", true, "rec_booster", true, 1234);
                Assert.Equal(new[] { "choice", "target", "scene", "session", "rp", "rpKept", "slot",
                        "slotListed", "unfinishedFlights", "marker" },
                    p.Select(kv => kv.Key).ToArray());
                Assert.Equal("discard", Value(p, "choice"));
                Assert.Equal("launch", Value(p, "target"));
                Assert.Equal("sess1", Value(p, "session"));
                Assert.Equal("true", Value(p, "rpKept"));
                Assert.Equal("1234", Value(p, "unfinishedFlights"));
                Assert.Equal("none", Value(p, "marker"));
                Assert.Equal("sess2", Value(TestCommandReFlyRevert.BuildOkPayload(
                    ReFlyRevertChoice.Retry, RevertTarget.Launch, "FLIGHT", "sess1", "sess2",
                    "rp", false, "o", false, 0), "marker"));
                Assert.Equal(
                    "reflyrevert start choice=discard target=launch sess=sess1 rp=rp_b9_root slot=rec_booster"
                    + " - waiting for the Re-Fly resume, then the stock Esc menu",
                    TestCommandReFlyRevert.FormatStartLine(
                        ReFlyRevertChoice.Discard, RevertTarget.Launch, "sess1", "rp_b9_root", "rec_booster"));
                Assert.Equal("reflyrevert pressed dialog button='Discard Re-Fly' choice=discard",
                    TestCommandReFlyRevert.FormatPressLine("dialog", "Discard Re-Fly", "choice=discard"));
                Assert.Equal(
                    "reflyrevert complete choice=discard target=launch scene=SPACECENTER sess=sess1"
                    + " rp=rp_b9_root rpKept=true slot=rec_booster slotListed=true unfinishedFlights=1234"
                    + " marker=none elapsed=12.5s",
                    TestCommandReFlyRevert.FormatCompleteLine(
                        ReFlyRevertChoice.Discard, RevertTarget.Launch, "SPACECENTER", "sess1",
                        "rp_b9_root", true, "rec_booster", true, 1234, null, 12.5));
                Assert.Equal(
                    "reflyrevert rejected reason=reflyrevert-option-unavailable choice=discard"
                    + " target=prelaunch found=false elapsed=0.5s",
                    TestCommandReFlyRevert.FormatTerminalLine(true, "reflyrevert-option-unavailable",
                        ReFlyRevertChoice.Discard, RevertTarget.Prelaunch, "found=false", 0.5));
                Assert.Equal(
                    "reflyrevert error reason=reflyrevert-timeout choice=retry target=launch elapsed=300.0s",
                    TestCommandReFlyRevert.FormatTerminalLine(false, "reflyrevert-timeout",
                        ReFlyRevertChoice.Retry, RevertTarget.Launch, null, 300.0));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
