using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Localization;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>ReFlyRevert</c> verb; the contract and
    /// every decision are on <see cref="TestCommandReFlyRevert"/>.
    ///
    /// <para><b>STOCK CONTROLS, IN A PLAYER'S ORDER.</b> <c>PauseMenu.Display()</c> (what
    /// the Esc key calls), then the "GamePaused" popup's Revert Flight button, then the
    /// "RevertingFlight" popup's Revert to Launch / Revert to VAB / SPH button, then the
    /// chosen button of Parsek's "ParsekReFlyRevert" dialog. Every press is the button's own
    /// <c>DialogGUIButton.OptionSelected</c> (its callback plus, for a dismiss-on-select
    /// button, the popup's own dismiss), after reading the button's
    /// <c>OptionInteractableCondition</c> - the greyed state a player would see. The stock
    /// option's callback calls <c>FlightDriver.RevertToLaunch</c> /
    /// <c>RevertToPrelaunch</c>, so <c>RevertInterceptor</c>'s Harmony prefix runs exactly as
    /// for a click. The seam calls no Parsek handler directly.</para>
    ///
    /// <para><b>MENUS LEFT OPEN.</b> A REJECTED decided after the pause menu opened closes it
    /// again (<c>PauseMenu.Close</c>, the Esc key's second press) so the flight resumes
    /// unpaused; Cancel ("Continue Flying") does the same after its answer, because stock
    /// leaves the game paused behind a dismissed pause popup.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private ReFlyRevertChoice reFlyRevertChoice;
        private RevertTarget reFlyRevertTarget;
        private ReFlyRevertPhase reFlyRevertPhase;
        private int reFlyRevertPhaseStartFrame;
        private int reFlyRevertSettledPolls;
        private string reFlyRevertSessionId;
        private string reFlyRevertRpId;
        private string reFlyRevertOriginId;

        private void ReFlyRevertImpl(ParsedCommand cmd)
        {
            string choiceArg = ArgOrNull(cmd, TestCommandReFlyRevert.ChoiceKey);
            string targetArg = ArgOrNull(cmd, TestCommandReFlyRevert.TargetKey);
            string choiceReason = TestCommandReFlyRevert.ParseChoice(choiceArg, out ReFlyRevertChoice choice);
            string targetReason = TestCommandReFlyRevert.ParseTarget(targetArg, out RevertTarget target);
            string argReason = choiceReason ?? targetReason;
            if (argReason != null)
            {
                RejectReFlyRevert(argReason,
                    $"choice={choiceArg ?? string.Empty} target={targetArg ?? string.Empty}");
                return;
            }

            ParsekScenario scenario = ParsekScenario.Instance;
            ReFlySessionMarker marker = scenario != null ? scenario.ActiveReFlySessionMarker : null;
            string gate = TestCommandReFlyRevert.DecideGate(
                MapScene(HighLogic.LoadedScene), marker != null);
            if (gate != null)
            {
                RejectReFlyRevert(gate,
                    $"choice={choiceArg} target={targetArg} scene={HighLogic.LoadedScene} "
                    + $"scenario={Bool(scenario != null)} marker={Bool(marker != null)}");
                return;
            }

            reFlyRevertChoice = choice;
            reFlyRevertTarget = target;
            reFlyRevertSessionId = marker.SessionId;
            reFlyRevertRpId = marker.RewindPointId;
            reFlyRevertOriginId = marker.OriginChildRecordingId;
            reFlyRevertSettledPolls = 0;
            SetReFlyRevertPhase(ReFlyRevertPhase.AwaitingResume);
            ParsekLog.Info(Tag, TestCommandReFlyRevert.FormatStartLine(
                choice, target, reFlyRevertSessionId, reFlyRevertRpId, reFlyRevertOriginId));
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteReFlyRevert(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandReFlyRevert.Verb);
            var o = new ReFlyRevertObservation
            {
                Scene = MapScene(HighLogic.LoadedScene),
                GameLoaded = HighLogic.CurrentGame != null,
                FramesInPhase = Time.frameCount - reFlyRevertPhaseStartFrame,
                ElapsedSeconds = elapsed,
                Expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget),
            };

            PopupDialog menuPopup = null, optionPopup = null, dialogPopup = null;
            DialogGUIButton revertButton = null, optionButton = null, choiceButton = null;
            string optionDetail = null;
            switch (reFlyRevertPhase)
            {
                case ReFlyRevertPhase.AwaitingResume:
                {
                    ParsekFlight flight = ParsekFlight.Instance;
                    o.HasActiveTree = flight != null && flight.HasActiveTree;
                    break;
                }
                case ReFlyRevertPhase.OpeningMenu:
                    menuPopup = FindPopupByName(TestCommandReFlyRevert.PauseMenuPopupName);
                    revertButton = FindButtonByText(menuPopup, Localizer.Format("#autoLOC_360545"));
                    o.MenuPopupFound = menuPopup != null;
                    o.RevertButtonFound = revertButton != null;
                    o.RevertButtonInteractable = revertButton != null && IsInteractable(revertButton);
                    break;
                case ReFlyRevertPhase.ChoosingRevertOption:
                    optionPopup = FindPopupByName(TestCommandReFlyRevert.RevertOptionsPopupName);
                    o.OptionPopupFound = optionPopup != null;
                    if (optionPopup != null)
                    {
                        optionButton = ResolveRevertOptionButton(optionPopup, reFlyRevertTarget, out optionDetail);
                        o.OptionButtonFound = optionButton != null;
                        o.OptionButtonInteractable = optionButton != null && IsInteractable(optionButton);
                    }
                    break;
                case ReFlyRevertPhase.AwaitingDialog:
                    dialogPopup = FindPopupByName(ReFlyRevertDialog.DialogName);
                    choiceButton = FindButtonByText(dialogPopup, ChoiceButtonText(reFlyRevertChoice));
                    o.DialogFound = dialogPopup != null;
                    o.ChoiceButtonFound = choiceButton != null;
                    break;
                case ReFlyRevertPhase.Settling:
                {
                    ParsekScenario scenario = ParsekScenario.Instance;
                    ReFlySessionMarker marker = scenario != null ? scenario.ActiveReFlySessionMarker : null;
                    o.MarkerPresent = marker != null;
                    o.MarkerIsOriginalSession = marker != null
                        && string.Equals(marker.SessionId, reFlyRevertSessionId, StringComparison.Ordinal);
                    o.InvokePending = RewindInvokeContext.Pending;
                    o.DiscardIntentArmed = DiscardReFlyLoadIntent.IsArmed;
                    o.DialogVisible = ReFlyRevertDialog.DialogVisible;
                    if (reFlyRevertChoice == ReFlyRevertChoice.Discard
                        && o.Scene == TestCommandReFlyRevert.ExpectedDestination(reFlyRevertChoice, reFlyRevertTarget)
                        && o.GameLoaded && !o.DiscardIntentArmed && scenario != null)
                    {
                        reFlyRevertSettledPolls++;
                    }
                    o.SettledPolls = reFlyRevertSettledPolls;
                    break;
                }
            }

            ReFlyRevertPollAction action = TestCommandReFlyRevert.DecidePoll(
                reFlyRevertPhase, reFlyRevertChoice, reFlyRevertTarget, o,
                TestCommandMergeAnswer.ReFlyResumeSettleBudgetSeconds);

            CultureInfo ic = CultureInfo.InvariantCulture;
            switch (action)
            {
                case ReFlyRevertPollAction.NotYet:
                    return;

                case ReFlyRevertPollAction.OpenPauseMenu:
                {
                    if (!o.HasActiveTree)
                        ParsekLog.Warn(Tag, "reflyrevert re-fly resume did not settle within "
                            + TestCommandMergeAnswer.ReFlyResumeSettleBudgetSeconds.ToString("F0", ic)
                            + "s - opening the Esc menu anyway");
                    if (!PauseMenu.exists)
                    {
                        FinishReFlyRevert("REJECTED", null, TestCommandReFlyRevert.PauseMenuUnavailableReason,
                            "PauseMenu.exists=false", elapsed);
                        return;
                    }
                    bool wasOpen = PauseMenu.isOpen;
                    if (!wasOpen)
                        PauseMenu.Display();
                    ParsekLog.Info(Tag, $"reflyrevert opened the Esc menu (PauseMenu.Display) "
                        + $"alreadyOpen={Bool(wasOpen)} activeTree={Bool(o.HasActiveTree)} "
                        + $"canRevert={Bool(FlightDriver.CanRevert)} "
                        + $"canRevertToPostInit={Bool(FlightDriver.CanRevertToPostInit)} "
                        + $"canRevertToPrelaunch={Bool(FlightDriver.CanRevertToPrelaunch)}");
                    SetReFlyRevertPhase(ReFlyRevertPhase.OpeningMenu);
                    return;
                }

                case ReFlyRevertPollAction.PressRevertFlight:
                    ParsekLog.Info(Tag, TestCommandReFlyRevert.FormatPressLine(
                        "revert-flight", revertButton.OptionText, $"popup={TestCommandReFlyRevert.PauseMenuPopupName}"));
                    SetReFlyRevertPhase(ReFlyRevertPhase.ChoosingRevertOption);
                    DialogGuiButtonOptionSelectedMethod.Invoke(revertButton, null);
                    return;

                case ReFlyRevertPollAction.PauseMenuUnavailable:
                case ReFlyRevertPollAction.RevertUnavailable:
                {
                    string reason = action == ReFlyRevertPollAction.PauseMenuUnavailable
                        ? TestCommandReFlyRevert.PauseMenuUnavailableReason
                        : TestCommandReFlyRevert.RevertUnavailableReason;
                    string detail = $"phase={reFlyRevertPhase} menu={Bool(o.MenuPopupFound)} "
                        + $"button={Bool(o.RevertButtonFound)} interactable={Bool(o.RevertButtonInteractable)} "
                        + $"optionPopup={Bool(o.OptionPopupFound)} canRevert={Bool(FlightDriver.CanRevert)} "
                        + $"frames={Int(o.FramesInPhase)}";
                    CloseReFlyRevertMenus("rejected " + reason);
                    FinishReFlyRevert("REJECTED", null, reason, detail, elapsed);
                    return;
                }

                case ReFlyRevertPollAction.PressRevertOption:
                    ParsekLog.Info(Tag, TestCommandReFlyRevert.FormatPressLine(
                        "revert-option", optionButton.OptionText,
                        $"target={TestCommandReFlyRevert.TargetToken(reFlyRevertTarget)} {optionDetail}"));
                    SetReFlyRevertPhase(ReFlyRevertPhase.AwaitingDialog);
                    // The option's own callback: the pause popup's Dismiss, then
                    // FlightDriver.RevertToLaunch / RevertToPrelaunch, whose prefix spawns the
                    // Re-Fly revert dialog; then the RevertingFlight popup's own dismiss.
                    DialogGuiButtonOptionSelectedMethod.Invoke(optionButton, null);
                    return;

                case ReFlyRevertPollAction.OptionUnavailable:
                    CloseReFlyRevertMenus("rejected " + TestCommandReFlyRevert.OptionUnavailableReason);
                    FinishReFlyRevert("REJECTED", null, TestCommandReFlyRevert.OptionUnavailableReason,
                        $"target={TestCommandReFlyRevert.TargetToken(reFlyRevertTarget)} "
                        + $"found={Bool(o.OptionButtonFound)} interactable={Bool(o.OptionButtonInteractable)} "
                        + (optionDetail ?? string.Empty), elapsed);
                    return;

                case ReFlyRevertPollAction.DialogNotShown:
                    CloseReFlyRevertMenus("rejected " + TestCommandReFlyRevert.DialogNotShownReason);
                    FinishReFlyRevert("REJECTED", null, TestCommandReFlyRevert.DialogNotShownReason,
                        $"scene={HighLogic.LoadedScene} frames={Int(o.FramesInPhase)} "
                        + $"dialogVisible={Bool(ReFlyRevertDialog.DialogVisible)}", elapsed);
                    return;

                case ReFlyRevertPollAction.ChoiceUnavailable:
                {
                    // The dialog is up without the chosen button (Discard is hidden while a
                    // merge journal is live). Answer it the way a player backs out - Continue
                    // Flying - so the flight is not left behind a modal popup and its lock.
                    DialogGUIButton cont = FindButtonByText(dialogPopup, ReFlyRevertDialog.ContinueButtonText);
                    if (cont != null)
                    {
                        ParsekLog.Info(Tag, TestCommandReFlyRevert.FormatPressLine(
                            "dialog-backout", cont.OptionText, "the chosen button is not on the dialog"));
                        DialogGuiButtonOptionSelectedMethod.Invoke(cont, null);
                    }
                    CloseReFlyRevertMenus("rejected " + TestCommandReFlyRevert.ChoiceUnavailableReason);
                    FinishReFlyRevert("REJECTED", null, TestCommandReFlyRevert.ChoiceUnavailableReason,
                        $"choice={TestCommandReFlyRevert.ChoiceToken(reFlyRevertChoice)} "
                        + $"buttons={Int(GetDialogButtons(dialogPopup).Count)}", elapsed);
                    return;
                }

                case ReFlyRevertPollAction.AnswerDialog:
                {
                    ParsekLog.Info(Tag, TestCommandReFlyRevert.FormatPressLine(
                        "dialog", choiceButton.OptionText,
                        $"choice={TestCommandReFlyRevert.ChoiceToken(reFlyRevertChoice)} sess={reFlyRevertSessionId}"));
                    SetReFlyRevertPhase(ReFlyRevertPhase.Settling);
                    // The button's own callback runs the RevertInterceptor handler
                    // synchronously (Discard: prune, promote, LoadGame, arm the intent,
                    // LoadScene; Retry: StartInvoke; Cancel: one log line).
                    DialogGuiButtonOptionSelectedMethod.Invoke(choiceButton, null);
                    if (reFlyRevertChoice == ReFlyRevertChoice.Discard && !DiscardReFlyLoadIntent.IsArmed)
                    {
                        FinishReFlyRevert("ERROR", null, TestCommandReFlyRevert.DiscardNotDispatchedReason,
                            "the Discard Re-fly handler armed no load intent (it bailed before its "
                            + "scene dispatch; its own [ReFlySession] End line says why)", elapsed);
                        return;
                    }
                    if (reFlyRevertChoice == ReFlyRevertChoice.Cancel)
                        CloseReFlyRevertMenus("cancel answered");
                    return;
                }

                case ReFlyRevertPollAction.Ok:
                {
                    ParsekScenario scenario = ParsekScenario.Instance;
                    ReFlySessionMarker after = scenario != null ? scenario.ActiveReFlySessionMarker : null;
                    bool rpKept = FindRewindPoint(scenario, reFlyRevertRpId) != null;
                    List<KeyValuePair<string, string>> listed = CollectListedUnfinishedFlightSlots(out int members);
                    bool slotListed = TestCommandReFlyRevert.IsSlotListed(listed, reFlyRevertRpId, reFlyRevertOriginId);
                    FinishReFlyRevert("OK", TestCommandReFlyRevert.BuildOkPayload(
                            reFlyRevertChoice, reFlyRevertTarget, HighLogic.LoadedScene.ToString(),
                            reFlyRevertSessionId, after?.SessionId, reFlyRevertRpId, rpKept,
                            reFlyRevertOriginId, slotListed, members),
                        null, null, elapsed, rpKept, slotListed, members, after?.SessionId);
                    return;
                }

                case ReFlyRevertPollAction.RetryNotStarted:
                    FinishReFlyRevert("ERROR", null, TestCommandReFlyRevert.RetryNotStartedReason,
                        $"marker={Bool(o.MarkerPresent)} originalSession={Bool(o.MarkerIsOriginalSession)} "
                        + $"pending={Bool(o.InvokePending)} frames={Int(o.FramesInPhase)} (the Retry "
                        + "handler refused; its own [ReFlySession] line says why)", elapsed);
                    return;

                case ReFlyRevertPollAction.SessionChanged:
                    FinishReFlyRevert("ERROR", null, TestCommandReFlyRevert.SessionChangedReason,
                        $"marker={Bool(o.MarkerPresent)} originalSession={Bool(o.MarkerIsOriginalSession)}",
                        elapsed);
                    return;

                case ReFlyRevertPollAction.WrongDestination:
                    FinishReFlyRevert("ERROR", null, TestCommandReFlyRevert.WrongDestinationReason,
                        $"phase={reFlyRevertPhase} scene={HighLogic.LoadedScene} expected="
                        + TestCommandReFlyRevert.ExpectedDestination(reFlyRevertChoice, reFlyRevertTarget),
                        elapsed);
                    return;

                case ReFlyRevertPollAction.ReturnedToMenu:
                    FinishReFlyRevert("ERROR", null, TestCommandReFlyRevert.ReturnedToMenuReason,
                        $"phase={reFlyRevertPhase}", elapsed);
                    return;

                case ReFlyRevertPollAction.Timeout:
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, elapsed,
                        TestCommandReFlyRevert.TimeoutReason);
                    if (reFlyRevertPhase != ReFlyRevertPhase.Settling)
                        CloseReFlyRevertMenus("timeout");
                    FinishReFlyRevert("ERROR", null, TestCommandReFlyRevert.TimeoutReason,
                        $"phase={reFlyRevertPhase} scene={HighLogic.LoadedScene} "
                        + $"intentArmed={Bool(DiscardReFlyLoadIntent.IsArmed)} "
                        + $"pending={Bool(RewindInvokeContext.Pending)} settledPolls={Int(reFlyRevertSettledPolls)}",
                        elapsed);
                    return;
            }
        }

        private void FinishReFlyRevert(string verdict, List<KeyValuePair<string, string>> payload,
            string reason, string detail, double elapsed,
            bool rpKept = false, bool slotListed = false, int members = 0, string markerAfter = null)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            ReFlyRevertChoice choice = reFlyRevertChoice;
            RevertTarget target = reFlyRevertTarget;
            string session = reFlyRevertSessionId;
            string rpId = reFlyRevertRpId;
            string originId = reFlyRevertOriginId;
            ClearTwoPhase();
            if (verdict == "OK")
            {
                ParsekLog.Info(Tag, TestCommandReFlyRevert.FormatCompleteLine(
                    choice, target, HighLogic.LoadedScene.ToString(), session, rpId, rpKept,
                    originId, slotListed, members, markerAfter, elapsed));
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                return;
            }
            bool rejected = verdict == "REJECTED";
            string line = TestCommandReFlyRevert.FormatTerminalLine(rejected, reason, choice, target, detail, elapsed);
            if (rejected) ParsekLog.Warn(Tag, line);
            else ParsekLog.Error(Tag, line);
            EmitExecutedTerminal(id, seq, verb, verdict, null, reason, dequeueHead: true);
        }

        private void SetReFlyRevertPhase(ReFlyRevertPhase phase)
        {
            if (reFlyRevertPhase != phase)
                ParsekLog.Verbose(Tag, $"reflyrevert phase {reFlyRevertPhase} -> {phase}");
            reFlyRevertPhase = phase;
            reFlyRevertPhaseStartFrame = Time.frameCount;
        }

        private void RejectReFlyRevert(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"reflyrevert rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }

        // The Esc key's second press: dismisses the revert options and the pause popup and
        // unpauses. A no-op when the pause menu is not open (a scene already left it).
        private static void CloseReFlyRevertMenus(string why)
        {
            try
            {
                if (PauseMenu.exists && PauseMenu.isOpen)
                {
                    PauseMenu.Close();
                    ParsekLog.Info(Tag, $"reflyrevert closed the Esc menu (PauseMenu.Close) - {why}");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"reflyrevert PauseMenu.Close threw ({why}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string ChoiceButtonText(ReFlyRevertChoice choice)
        {
            switch (choice)
            {
                case ReFlyRevertChoice.Discard: return ReFlyRevertDialog.DiscardButtonText;
                case ReFlyRevertChoice.Retry: return ReFlyRevertDialog.RetryButtonText;
                case ReFlyRevertChoice.Cancel: return ReFlyRevertDialog.ContinueButtonText;
                default: return null;
            }
        }

        private static DialogGUIButton FindButtonByText(PopupDialog popup, string text)
        {
            if (popup == null || string.IsNullOrEmpty(text)) return null;
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            for (int i = 0; i < buttons.Count; i++)
            {
                if (buttons[i] != null && string.Equals(buttons[i].OptionText, text, StringComparison.Ordinal))
                    return buttons[i];
            }
            return null;
        }

        private static bool IsInteractable(DialogGUIButton button)
        {
            Func<bool> condition = button.OptionInteractableCondition;
            return condition == null || condition();
        }

        // The target's button in stock's RevertingFlight popup, by the order
        // PauseMenu.drawStockRevertOptions adds them, from stock's own predicates.
        private static DialogGUIButton ResolveRevertOptionButton(
            PopupDialog popup, RevertTarget target, out string detail)
        {
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            Game game = HighLogic.CurrentGame;
            bool canRestart = game != null && game.Parameters != null && game.Parameters.Flight.CanRestart;
            bool canLeaveToEditor = game != null && game.Parameters != null && game.Parameters.Flight.CanLeaveToEditor;
            EditorFacility shipType = ShipConstruction.ShipType;
            bool launchOffered = FlightDriver.CanRevertToPostInit && canRestart;
            bool prelaunchOffered = canLeaveToEditor && FlightDriver.CanRevertToPrelaunch
                && ShipConstruction.ShipConfig != null
                && (shipType == EditorFacility.VAB || shipType == EditorFacility.SPH);
            int index = TestCommandReFlyRevert.ResolveRevertOptionIndex(
                target, launchOffered, prelaunchOffered, buttons.Count);
            var texts = new List<string>();
            for (int i = 0; i < buttons.Count; i++)
                texts.Add(buttons[i] != null ? buttons[i].OptionText : "<null>");
            detail = $"launchOffered={Bool(launchOffered)} prelaunchOffered={Bool(prelaunchOffered)} "
                + $"facility={shipType} buttons={Int(buttons.Count)} index={Int(index)} "
                + $"texts=[{string.Join("|", texts.ToArray())}]";
            return index >= 0 && index < buttons.Count ? buttons[index] : null;
        }

        private static RewindPoint FindRewindPoint(ParsekScenario scenario, string rpId)
        {
            if (scenario == null || scenario.RewindPoints == null || string.IsNullOrEmpty(rpId))
                return null;
            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                RewindPoint rp = scenario.RewindPoints[i];
                if (rp != null && string.Equals(rp.RewindPointId, rpId, StringComparison.Ordinal))
                    return rp;
            }
            return null;
        }

        // Every STASH member (the Unfinished Flights group a player sees), resolved to its
        // (rewind point id, slot origin id) through the predicate the group itself uses.
        private static List<KeyValuePair<string, string>> CollectListedUnfinishedFlightSlots(out int members)
        {
            var listed = new List<KeyValuePair<string, string>>();
            IReadOnlyList<Recording> group = UnfinishedFlightsGroup.ComputeMembers();
            members = group != null ? group.Count : 0;
            for (int i = 0; i < members; i++)
            {
                if (!EffectiveState.TryResolveUnfinishedFlight(group[i], out RewindPoint rp, out int slotIndex))
                    continue;
                if (rp == null || rp.ChildSlots == null || slotIndex < 0 || slotIndex >= rp.ChildSlots.Count)
                    continue;
                ChildSlot slot = rp.ChildSlots[slotIndex];
                listed.Add(new KeyValuePair<string, string>(rp.RewindPointId, slot?.OriginChildRecordingId));
            }
            return listed;
        }
    }
}
