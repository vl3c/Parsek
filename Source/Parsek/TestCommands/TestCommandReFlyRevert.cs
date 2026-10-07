using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Which button of the Re-Fly revert dialog a <c>ReFlyRevert</c> presses.</summary>
    internal enum ReFlyRevertChoice
    {
        Unknown = 0,
        /// <summary>"Discard Re-Fly": <c>RevertInterceptor.DiscardReFlyHandler</c>.</summary>
        Discard,
        /// <summary>"Retry from Rewind Point": <c>RevertInterceptor.RetryHandler</c>.</summary>
        Retry,
        /// <summary>"Continue Flying": <c>RevertInterceptor.CancelHandler</c>.</summary>
        Cancel,
    }

    /// <summary>Where a pending <c>ReFlyRevert</c> stands between polls.</summary>
    internal enum ReFlyRevertPhase
    {
        /// <summary>Waiting for the Re-Fly resume to make the restored tree active (a player
        /// cannot reach the Esc menu before the flight scene settles).</summary>
        AwaitingResume,
        /// <summary>The stock pause menu was opened; waiting for its popup and its Revert
        /// Flight button.</summary>
        OpeningMenu,
        /// <summary>Revert Flight was pressed; waiting for stock's "RevertingFlight" popup
        /// and the target's option.</summary>
        ChoosingRevertOption,
        /// <summary>The stock revert option was pressed; waiting for Parsek's Re-Fly revert
        /// dialog.</summary>
        AwaitingDialog,
        /// <summary>The chosen dialog button was pressed; waiting for its outcome.</summary>
        Settling,
    }

    /// <summary>What one completion poll of a pending <c>ReFlyRevert</c> does.</summary>
    internal enum ReFlyRevertPollAction
    {
        NotYet,
        OpenPauseMenu,
        PressRevertFlight,
        PauseMenuUnavailable,
        RevertUnavailable,
        PressRevertOption,
        OptionUnavailable,
        AnswerDialog,
        DialogNotShown,
        ChoiceUnavailable,
        Ok,
        RetryNotStarted,
        SessionChanged,
        WrongDestination,
        ReturnedToMenu,
        Timeout,
    }

    /// <summary>
    /// The live facts one <c>ReFlyRevert</c> poll reads. The applier fills only what its
    /// phase needs; everything else stays false / zero.
    /// </summary>
    internal struct ReFlyRevertObservation
    {
        internal TestCommandScene Scene;
        internal bool GameLoaded;
        internal int FramesInPhase;
        internal double ElapsedSeconds;
        internal bool Expired;

        // AwaitingResume
        internal bool HasActiveTree;

        // OpeningMenu
        internal bool MenuPopupFound;
        internal bool RevertButtonFound;
        internal bool RevertButtonInteractable;

        // ChoosingRevertOption
        internal bool OptionPopupFound;
        internal bool OptionButtonFound;
        internal bool OptionButtonInteractable;

        // AwaitingDialog
        internal bool DialogFound;
        internal bool ChoiceButtonFound;

        // Settling
        internal bool MarkerPresent;
        internal bool MarkerIsOriginalSession;
        internal bool InvokePending;
        internal bool DiscardIntentArmed;
        internal bool DialogVisible;
        internal int SettledPolls;
    }

    /// <summary>
    /// Pure half of the automation-only <c>ReFlyRevert</c> seam verb: during a live Re-Fly
    /// session, open the stock Esc menu, press Revert Flight, press the stock revert option
    /// for the target (Revert to Launch, or Revert to VAB / SPH), and answer Parsek's Re-Fly
    /// revert dialog (<c>ReFlyRevertDialog</c>) with the chosen button - the clicks a player
    /// makes, in a player's order.
    ///
    /// <para>Stock order (decompiled KSP 1.12.5): <c>PauseMenu.Display()</c> (the Esc key's
    /// action) pauses the game and spawns the "GamePaused" popup; its Revert Flight button
    /// (<c>#autoLOC_360545</c>, interactable on <c>FlightDriver.CanRevert</c>, which
    /// <c>ReFlyRevertButtonGate</c> forces during a session) spawns the "RevertingFlight"
    /// popup built by <c>PauseMenu.drawStockRevertOptions</c>: a Revert to Launch button when
    /// <c>FlightDriver.CanRevertToPostInit</c> and the game's <c>CanRestart</c>, then a
    /// Revert to VAB / SPH button when <c>CanLeaveToEditor</c>,
    /// <c>FlightDriver.CanRevertToPrelaunch</c>, a ship config and a VAB / SPH ship type,
    /// then a back button. The option's callback dismisses the pause popup and calls
    /// <c>FlightDriver.RevertToLaunch()</c> / <c>RevertToPrelaunch(facility)</c>, whose
    /// Harmony prefix (<c>RevertInterceptor.Prefix</c>) blocks the stock body while a Re-Fly
    /// marker is live and spawns the "ParsekReFlyRevert" dialog.</para>
    ///
    /// <para>A Re-Fly session is a resumed flight (the rewind point's quicksave), and
    /// <c>FlightDriver.Start</c> sets <c>CanRevertToPrelaunch</c> only for a PRELAUNCH vessel
    /// that is the post-init vessel with a ship config and a pre-launch state, so the Revert
    /// to VAB / SPH option is normally absent; <c>target=prelaunch</c> then answers REJECTED
    /// <see cref="OptionUnavailableReason"/> rather than calling the stock method a player
    /// cannot reach.</para>
    ///
    /// <para>Grammar: <c>cmd=ReFlyRevert choice=discard|retry|cancel
    /// target=launch|prelaunch</c>, issued in FLIGHT with a live Re-Fly session. TWO-PHASE,
    /// RequiresGameLoaded (the wrong scene is its own typed REJECTED), 300 s.</para>
    /// </summary>
    internal static class TestCommandReFlyRevert
    {
        internal const string Verb = "ReFlyRevert";
        internal const string ChoiceKey = "choice";
        internal const string TargetKey = "target";

        internal const string DiscardToken = "discard";
        internal const string RetryToken = "retry";
        internal const string CancelToken = "cancel";
        internal const string LaunchToken = "launch";
        internal const string PrelaunchToken = "prelaunch";

        /// <summary>The closed <c>choice=</c> vocabulary. hlib.REFLYREVERT_CHOICE_VALUES
        /// mirrors it (ReFlyRevertSourceSyncTests).</summary>
        internal static readonly string[] ChoiceValues = { "discard", "retry", "cancel" };

        /// <summary>The closed <c>target=</c> vocabulary. hlib.REFLYREVERT_TARGET_VALUES
        /// mirrors it.</summary>
        internal static readonly string[] TargetValues = { "launch", "prelaunch" };

        internal const string ChoiceArgMissingReason = "reflyrevert-choice-arg-missing";
        internal const string ChoiceArgInvalidReason = "reflyrevert-choice-arg-invalid";
        internal const string TargetArgMissingReason = "reflyrevert-target-arg-missing";
        internal const string TargetArgInvalidReason = "reflyrevert-target-arg-invalid";
        internal const string WrongSceneReason = "reflyrevert-wrong-scene";
        internal const string NoSessionReason = "reflyrevert-no-session";
        // Decided by the poll before the revert option is pressed: late REJECTEDs (the
        // KscMarkerRecover marker pair's precedent), because nothing that changes the game
        // was pressed - the seam closes the stock menus again before it answers.
        internal const string PauseMenuUnavailableReason = "reflyrevert-pause-menu-unavailable";
        internal const string RevertUnavailableReason = "reflyrevert-revert-unavailable";
        internal const string OptionUnavailableReason = "reflyrevert-option-unavailable";
        // Decided after the stock revert option was pressed and before a dialog button was.
        internal const string DialogNotShownReason = "reflyrevert-dialog-not-shown";
        internal const string ChoiceUnavailableReason = "reflyrevert-choice-unavailable";

        // Post-press ERROR terminals.
        internal const string DiscardNotDispatchedReason = "reflyrevert-discard-not-dispatched";
        internal const string RetryNotStartedReason = "reflyrevert-retry-not-started";
        internal const string SessionChangedReason = "reflyrevert-session-changed";
        internal const string WrongDestinationReason = "reflyrevert-wrong-destination";
        internal const string ReturnedToMenuReason = "reflyrevert-returned-to-menu";
        internal const string TimeoutReason = "reflyrevert-timeout";

        /// <summary>The REJECTED vocabulary, in gate order. hlib.REFLYREVERT_REASONS mirrors
        /// it (ReFlyRevertSourceSyncTests).</summary>
        internal static readonly string[] Reasons =
        {
            "reflyrevert-choice-arg-missing",
            "reflyrevert-choice-arg-invalid",
            "reflyrevert-target-arg-missing",
            "reflyrevert-target-arg-invalid",
            "reflyrevert-wrong-scene",
            "reflyrevert-no-session",
            "reflyrevert-pause-menu-unavailable",
            "reflyrevert-revert-unavailable",
            "reflyrevert-option-unavailable",
            "reflyrevert-dialog-not-shown",
            "reflyrevert-choice-unavailable",
        };

        /// <summary>The post-press ERROR vocabulary.</summary>
        internal static readonly string[] ErrorReasons =
        {
            "reflyrevert-discard-not-dispatched",
            "reflyrevert-retry-not-started",
            "reflyrevert-session-changed",
            "reflyrevert-wrong-destination",
            "reflyrevert-returned-to-menu",
            "reflyrevert-timeout",
        };

        /// <summary>Stock's popup names (the <c>MultiOptionDialog</c> name each spawn uses).</summary>
        internal const string PauseMenuPopupName = "GamePaused";
        internal const string RevertOptionsPopupName = "RevertingFlight";

        /// <summary>Frames the seam waits for a stock popup (the pause menu, the revert
        /// options) to exist after the press that spawns it. Both spawn synchronously.</summary>
        internal const int MenuWaitFrames = 120;

        /// <summary>Frames the seam waits for Parsek's dialog after the stock revert option
        /// was pressed. The interceptor spawns it synchronously inside the press.</summary>
        internal const int DialogWaitFrames = 120;

        /// <summary>Frames a pressed Retry may sit with no pending invocation and the old
        /// session still live before the seam calls it refused.</summary>
        internal const int RetryStartWaitFrames = 300;

        /// <summary>Polls in the settled destination (scene, game, intent consumed) before
        /// OK, so the destination's OnLoad and its first frames have run.</summary>
        internal const int SettlePolls = 2;

        /// <summary>Parses <c>choice=</c>; returns null or the refusal reason.</summary>
        internal static string ParseChoice(string raw, out ReFlyRevertChoice choice)
        {
            choice = ReFlyRevertChoice.Unknown;
            if (string.IsNullOrEmpty(raw)) return ChoiceArgMissingReason;
            switch (raw)
            {
                case DiscardToken: choice = ReFlyRevertChoice.Discard; return null;
                case RetryToken: choice = ReFlyRevertChoice.Retry; return null;
                case CancelToken: choice = ReFlyRevertChoice.Cancel; return null;
                default: return ChoiceArgInvalidReason;
            }
        }

        /// <summary>Parses <c>target=</c>; returns null or the refusal reason.</summary>
        internal static string ParseTarget(string raw, out RevertTarget target)
        {
            target = RevertTarget.Launch;
            if (string.IsNullOrEmpty(raw)) return TargetArgMissingReason;
            switch (raw)
            {
                case LaunchToken: target = RevertTarget.Launch; return null;
                case PrelaunchToken: target = RevertTarget.Prelaunch; return null;
                default: return TargetArgInvalidReason;
            }
        }

        internal static string ChoiceToken(ReFlyRevertChoice choice)
        {
            switch (choice)
            {
                case ReFlyRevertChoice.Discard: return DiscardToken;
                case ReFlyRevertChoice.Retry: return RetryToken;
                case ReFlyRevertChoice.Cancel: return CancelToken;
                default: return string.Empty;
            }
        }

        internal static string TargetToken(RevertTarget target)
        {
            return target == RevertTarget.Prelaunch ? PrelaunchToken : LaunchToken;
        }

        /// <summary>The scene a pressed choice should end in: Discard goes where its
        /// target's dispatch goes (the Space Center for Launch, the editor for Prelaunch);
        /// Retry reloads the flight; Cancel stays in it.</summary>
        internal static TestCommandScene ExpectedDestination(ReFlyRevertChoice choice, RevertTarget target)
        {
            if (choice == ReFlyRevertChoice.Discard)
                return target == RevertTarget.Prelaunch ? TestCommandScene.Editor : TestCommandScene.SpaceCenter;
            return TestCommandScene.Flight;
        }

        /// <summary>
        /// The pre-press gate, decided at dispatch before any side effect: the scene, then a
        /// live Re-Fly session. Returns null to proceed.
        /// </summary>
        internal static string DecideGate(TestCommandScene scene, bool markerPresent)
        {
            if (scene != TestCommandScene.Flight) return WrongSceneReason;
            if (!markerPresent) return NoSessionReason;
            return null;
        }

        /// <summary>
        /// Which button of stock's "RevertingFlight" popup is the target's option, by the
        /// order <c>PauseMenu.drawStockRevertOptions</c> adds them: Revert to Launch (when
        /// offered), Revert to VAB / SPH (when offered), then the back button. -1 when the
        /// target is not offered, or when the button count does not match the predicates
        /// (the layout moved, so a position would be a guess).
        /// </summary>
        internal static int ResolveRevertOptionIndex(
            RevertTarget target, bool launchOffered, bool prelaunchOffered, int buttonCount)
        {
            int expected = (launchOffered ? 1 : 0) + (prelaunchOffered ? 1 : 0) + 1;
            if (buttonCount != expected) return -1;
            if (target == RevertTarget.Launch)
                return launchOffered ? 0 : -1;
            if (!prelaunchOffered) return -1;
            return launchOffered ? 1 : 0;
        }

        /// <summary>
        /// One completion poll. MAINMENU is a fast failure in every phase. Before a dialog
        /// button is pressed nothing the game keeps was changed, so a missing menu, a greyed
        /// Revert, an absent option, a dialog that never showed or a hidden choice are
        /// REJECTED; after the press the outcome is OK or an ERROR.
        /// </summary>
        internal static ReFlyRevertPollAction DecidePoll(
            ReFlyRevertPhase phase, ReFlyRevertChoice choice, RevertTarget target,
            ReFlyRevertObservation o, double resumeSettleBudgetSeconds)
        {
            if (o.Scene == TestCommandScene.MainMenu)
                return ReFlyRevertPollAction.ReturnedToMenu;

            switch (phase)
            {
                case ReFlyRevertPhase.AwaitingResume:
                    if (o.Expired) return ReFlyRevertPollAction.Timeout;
                    if (o.Scene != TestCommandScene.Flight) return ReFlyRevertPollAction.WrongDestination;
                    // The AnswerMergeDialog settle rule: drive once the restored tree is
                    // active, or once the settle budget ran out (a resume that never
                    // settles is still what the player would face).
                    if (!o.HasActiveTree && o.ElapsedSeconds < resumeSettleBudgetSeconds)
                        return ReFlyRevertPollAction.NotYet;
                    return ReFlyRevertPollAction.OpenPauseMenu;

                case ReFlyRevertPhase.OpeningMenu:
                    if (o.Expired) return ReFlyRevertPollAction.Timeout;
                    if (o.Scene != TestCommandScene.Flight) return ReFlyRevertPollAction.WrongDestination;
                    if (!o.MenuPopupFound || !o.RevertButtonFound)
                    {
                        if (o.FramesInPhase < MenuWaitFrames) return ReFlyRevertPollAction.NotYet;
                        return o.MenuPopupFound
                            ? ReFlyRevertPollAction.RevertUnavailable
                            : ReFlyRevertPollAction.PauseMenuUnavailable;
                    }
                    if (!o.RevertButtonInteractable) return ReFlyRevertPollAction.RevertUnavailable;
                    return ReFlyRevertPollAction.PressRevertFlight;

                case ReFlyRevertPhase.ChoosingRevertOption:
                    if (o.Expired) return ReFlyRevertPollAction.Timeout;
                    if (o.Scene != TestCommandScene.Flight) return ReFlyRevertPollAction.WrongDestination;
                    if (!o.OptionPopupFound)
                        return o.FramesInPhase < MenuWaitFrames
                            ? ReFlyRevertPollAction.NotYet
                            : ReFlyRevertPollAction.RevertUnavailable;
                    if (!o.OptionButtonFound || !o.OptionButtonInteractable)
                        return ReFlyRevertPollAction.OptionUnavailable;
                    return ReFlyRevertPollAction.PressRevertOption;

                case ReFlyRevertPhase.AwaitingDialog:
                    if (o.Expired) return ReFlyRevertPollAction.Timeout;
                    if (!o.DialogFound)
                        return o.FramesInPhase < DialogWaitFrames
                            ? ReFlyRevertPollAction.NotYet
                            : ReFlyRevertPollAction.DialogNotShown;
                    if (!o.ChoiceButtonFound) return ReFlyRevertPollAction.ChoiceUnavailable;
                    return ReFlyRevertPollAction.AnswerDialog;

                case ReFlyRevertPhase.Settling:
                    return DecideSettle(choice, target, o);
            }
            return ReFlyRevertPollAction.NotYet;
        }

        private static ReFlyRevertPollAction DecideSettle(
            ReFlyRevertChoice choice, RevertTarget target, ReFlyRevertObservation o)
        {
            TestCommandScene expected = ExpectedDestination(choice, target);
            switch (choice)
            {
                case ReFlyRevertChoice.Discard:
                    if (o.Expired) return ReFlyRevertPollAction.Timeout;
                    // The load was requested from FLIGHT; until the destination loads the
                    // pump sees FLIGHT (or no safe point at all).
                    if (o.Scene == TestCommandScene.Flight || o.Scene == TestCommandScene.Loading)
                        return ReFlyRevertPollAction.NotYet;
                    if (o.Scene != expected) return ReFlyRevertPollAction.WrongDestination;
                    // The destination's OnLoad consumes the intent; until it has, the
                    // classification and the handoff have not run.
                    if (!o.GameLoaded || o.DiscardIntentArmed) return ReFlyRevertPollAction.NotYet;
                    return o.SettledPolls >= SettlePolls
                        ? ReFlyRevertPollAction.Ok
                        : ReFlyRevertPollAction.NotYet;

                case ReFlyRevertChoice.Retry:
                    if (o.Scene == TestCommandScene.Flight && o.GameLoaded && !o.InvokePending
                        && o.MarkerPresent && !o.MarkerIsOriginalSession)
                        return ReFlyRevertPollAction.Ok;
                    if (o.Expired) return ReFlyRevertPollAction.Timeout;
                    if (o.Scene != TestCommandScene.Flight && o.Scene != TestCommandScene.Loading)
                        return ReFlyRevertPollAction.WrongDestination;
                    if (o.Scene == TestCommandScene.Flight && !o.InvokePending
                        && o.FramesInPhase >= RetryStartWaitFrames)
                        return ReFlyRevertPollAction.RetryNotStarted;
                    return ReFlyRevertPollAction.NotYet;

                case ReFlyRevertChoice.Cancel:
                    if (o.Expired) return ReFlyRevertPollAction.Timeout;
                    if (o.Scene != TestCommandScene.Flight) return ReFlyRevertPollAction.WrongDestination;
                    if (o.DialogVisible) return ReFlyRevertPollAction.NotYet;
                    if (!o.MarkerPresent || !o.MarkerIsOriginalSession)
                        return ReFlyRevertPollAction.SessionChanged;
                    return ReFlyRevertPollAction.Ok;
            }
            return ReFlyRevertPollAction.NotYet;
        }

        /// <summary>
        /// Whether a terminal (any verdict) must close the stock Esc menu before it answers:
        /// only while the game is still in FLIGHT (a scene load already took the menu with
        /// it), only when the verb itself opened the menu (a menu a player or an earlier
        /// step opened is not the verb's to close), and only while it is still open.
        /// <c>PauseMenu.Display</c> paused the game, and stock leaves the pause on behind a
        /// dismissed pause popup after the revert option, so an exit that skips this leaves
        /// the next step with a paused flight.
        /// </summary>
        internal static bool ShouldCloseMenusOnExit(
            TestCommandScene scene, bool menuOpenedByVerb, bool pauseMenuOpen)
        {
            return scene == TestCommandScene.Flight && menuOpenedByVerb && pauseMenuOpen;
        }

        /// <summary>
        /// Whether a terminal must back out of Parsek's Re-Fly revert dialog (press its
        /// Continue Flying, which releases the dialog's <c>ControlTypes.All</c> input lock)
        /// before it answers: only in FLIGHT and only while the dialog is still up. Every
        /// dialog button clears the lock itself, so after a pressed choice this is false.
        /// </summary>
        internal static bool ShouldBackOutOfDialogOnExit(TestCommandScene scene, bool dialogOpen)
        {
            return scene == TestCommandScene.Flight && dialogOpen;
        }

        /// <summary>The grep-stable exit-cleanup line.</summary>
        internal static string FormatExitCleanupLine(
            string why, string sceneName, bool menuOpenedByVerb, bool pauseMenuOpen,
            bool dialogOpen, bool closedMenu, bool backedOut)
        {
            return "reflyrevert exit cleanup scene=" + (sceneName ?? string.Empty)
                + " menuOurs=" + (menuOpenedByVerb ? "true" : "false")
                + " menuOpen=" + (pauseMenuOpen ? "true" : "false")
                + " dialogOpen=" + (dialogOpen ? "true" : "false")
                + " closedMenu=" + (closedMenu ? "true" : "false")
                + " backedOutOfDialog=" + (backedOut ? "true" : "false")
                + " - " + (why ?? string.Empty);
        }

        /// <summary>
        /// Whether the session's slot is still an Unfinished Flight: some STASH member
        /// resolves to the origin rewind point and the slot whose origin child recording is
        /// <paramref name="originChildRecordingId"/>. <paramref name="listedSlots"/> holds
        /// one (rewind point id, slot origin id) pair per member, as
        /// <c>EffectiveState.TryResolveUnfinishedFlight</c> resolved it.
        /// </summary>
        internal static bool IsSlotListed(
            IReadOnlyList<KeyValuePair<string, string>> listedSlots,
            string rewindPointId, string originChildRecordingId)
        {
            if (listedSlots == null || string.IsNullOrEmpty(rewindPointId)
                || string.IsNullOrEmpty(originChildRecordingId))
                return false;
            for (int i = 0; i < listedSlots.Count; i++)
            {
                if (listedSlots[i].Key == rewindPointId && listedSlots[i].Value == originChildRecordingId)
                    return true;
            }
            return false;
        }

        /// <summary>The OK payload: <c>choice= target= scene= session= rp= rpKept= slot=
        /// slotListed= unfinishedFlights= marker=</c>. <c>session</c> is the session the verb
        /// found live; <c>marker</c> is the session live after the outcome (a fresh one
        /// after Retry, the same one after Cancel, <c>none</c> after Discard).</summary>
        internal static List<KeyValuePair<string, string>> BuildOkPayload(
            ReFlyRevertChoice choice, RevertTarget target, string sceneName,
            string sessionId, string markerSessionAfter, string rewindPointId, bool rpKept,
            string originChildRecordingId, bool slotListed, int unfinishedFlights)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("choice", ChoiceToken(choice)),
                new KeyValuePair<string, string>("target", TargetToken(target)),
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
                new KeyValuePair<string, string>("session", sessionId ?? string.Empty),
                new KeyValuePair<string, string>("rp", rewindPointId ?? string.Empty),
                new KeyValuePair<string, string>("rpKept", rpKept ? "true" : "false"),
                new KeyValuePair<string, string>("slot", originChildRecordingId ?? string.Empty),
                new KeyValuePair<string, string>("slotListed", slotListed ? "true" : "false"),
                new KeyValuePair<string, string>("unfinishedFlights", unfinishedFlights.ToString(ic)),
                new KeyValuePair<string, string>("marker", MarkerToken(markerSessionAfter)),
            };
        }

        /// <summary>The marker token the payload and the lines carry: the live session id,
        /// or <c>none</c>.</summary>
        internal static string MarkerToken(string markerSessionId)
        {
            return string.IsNullOrEmpty(markerSessionId) ? "none" : markerSessionId;
        }

        /// <summary>The grep-stable start line.</summary>
        internal static string FormatStartLine(
            ReFlyRevertChoice choice, RevertTarget target, string sessionId, string rewindPointId,
            string originChildRecordingId)
        {
            return "reflyrevert start choice=" + ChoiceToken(choice)
                + " target=" + TargetToken(target)
                + " sess=" + (sessionId ?? string.Empty)
                + " rp=" + (rewindPointId ?? string.Empty)
                + " slot=" + (originChildRecordingId ?? string.Empty)
                + " - waiting for the Re-Fly resume, then the stock Esc menu";
        }

        /// <summary>The grep-stable line for each stock or Parsek press.</summary>
        internal static string FormatPressLine(string step, string buttonText, string detail)
        {
            return "reflyrevert pressed " + (step ?? string.Empty)
                + " button='" + (buttonText ?? string.Empty) + "'"
                + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail);
        }

        /// <summary>The grep-stable completion line.</summary>
        internal static string FormatCompleteLine(
            ReFlyRevertChoice choice, RevertTarget target, string sceneName, string sessionId,
            string rewindPointId, bool rpKept, string originChildRecordingId, bool slotListed,
            int unfinishedFlights, string markerSessionAfter, double elapsed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "reflyrevert complete choice=" + ChoiceToken(choice)
                + " target=" + TargetToken(target)
                + " scene=" + (sceneName ?? string.Empty)
                + " sess=" + (sessionId ?? string.Empty)
                + " rp=" + (rewindPointId ?? string.Empty)
                + " rpKept=" + (rpKept ? "true" : "false")
                + " slot=" + (originChildRecordingId ?? string.Empty)
                + " slotListed=" + (slotListed ? "true" : "false")
                + " unfinishedFlights=" + unfinishedFlights.ToString(ic)
                + " marker=" + MarkerToken(markerSessionAfter)
                + " elapsed=" + elapsed.ToString("F1", ic) + "s";
        }

        /// <summary>The grep-stable refusal / failure line (<c>rejected</c> or <c>error</c>).</summary>
        internal static string FormatTerminalLine(
            bool rejected, string reason, ReFlyRevertChoice choice, RevertTarget target,
            string detail, double elapsed)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            return "reflyrevert " + (rejected ? "rejected" : "error")
                + " reason=" + (reason ?? string.Empty)
                + " choice=" + ChoiceToken(choice)
                + " target=" + TargetToken(target)
                + (string.IsNullOrEmpty(detail) ? string.Empty : " " + detail)
                + " elapsed=" + elapsed.ToString("F1", ic) + "s";
        }
    }
}
