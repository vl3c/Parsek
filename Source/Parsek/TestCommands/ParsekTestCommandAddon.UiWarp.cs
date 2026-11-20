namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-seam partial: the applier for <c>UiAction op=warp</c>, in its two forms.
    ///
    /// <para><c>window=spawncontrol</c> presses the Real Spawn Control table's FIRST row
    /// warp button. <b>THE BUTTON'S OWN BODY, NOT A SECOND SPELLING OF IT.</b> The row
    /// button's click handler is one method, <c>SpawnControlUI.ExecuteRowWarp</c>, and this
    /// op reaches it through <c>SpawnControlUI.TryPressFirstRowWarpForTesting</c>, which
    /// builds the row from the same sort and the same <c>SpawnControlPresentation</c> row
    /// model the draw pass uses. So the <c>Real Spawn Control: warp to ...</c> line and the
    /// flight-controller warp it triggers are exactly the ones a player's click produces.
    /// No synthesised input and no new player-facing surface.</para>
    ///
    /// <para><c>window=timeline year= day= hour= minute= confirm=warp|cancel</c> enters the
    /// Timeline's warp date and presses "Warp to time" through
    /// <c>TimelineWindowUI.TryPressWarpToTimeForTesting</c>, which resolves the button state
    /// with the draw's own resolver and runs the click body the drawn button runs
    /// (<c>WarpToTimeController.RequestWarp</c>). That click spawns the confirm dialog
    /// synchronously (<c>PopupDialog.SpawnPopupDialog</c> builds the buttons in the call),
    /// and the chosen button is answered through its own <c>OptionSelected</c>, which runs
    /// its callback and the dismiss a click runs. Pure half:
    /// <see cref="TestCommandUiWarpTime"/>.</para>
    ///
    /// <para><b>A GREYED BUTTON IS NOT PRESSED.</b> Either form answers REJECTED
    /// <c>warp-button-disabled</c> with the button's own disabled-hover text rather than
    /// warping anyway. REJECTED rather than ERROR because nothing was acted on.</para>
    ///
    /// <para><b>ONE-PHASE.</b> Every warp is decided synchronously inside the click (or the
    /// confirm callback); what follows - a spawn at recording end, the Space Center load a
    /// rewind requests and the pending forward warp it runs - belongs to the playback loop
    /// and the consumer, which a lane observes through its own log contracts.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void UiActionWarpOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiAction.WindowHasWarpButton(spec.Name))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.WarpUnsupportedWindowReason
                    + $" window={spec.Name}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.WarpUnsupportedWindowReason} window={spec.Name} "
                    + $"valid={TestCommandUiAction.WarpWindowNames}");
                return;
            }

            if (spec.Name == TestCommandUiAction.TimelineWindow)
            {
                UiActionTimelineWarpOp(cmd, ui, spec);
                return;
            }

            string stray = TestCommandUiWarpTime.FirstTimelineOnlyArg(cmd.Args);
            if (stray != null)
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiWarpTime.ArgUnsupportedReason
                    + $" window={spec.Name} arg={stray}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiWarpTime.ArgUnsupportedReason} window={spec.Name} arg={stray}");
                return;
            }

            ParsekFlight flight = ParsekFlight.Instance;
            SpawnControlUI window = ParsekWindowSet.Of(ui).GetSpawnControlUI();
            if (flight == null || window == null)
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.HostUnavailableReason
                    + $" op={TestCommandUiAction.WarpOpToken} window={spec.Name} "
                    + $"flight={Bool(flight != null)} window={Bool(window != null)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.HostUnavailableReason} window={spec.Name}");
                return;
            }

            if (!window.TryPressFirstRowWarpForTesting(flight,
                    out NearbySpawnCandidate cand, out SpawnCandidateRowPresentation row,
                    out string refusal, out string disabledReason))
            {
                string detail = refusal == SpawnControlUI.WarpRefusalButtonDisabled
                    ? $" vessel={cand.vesselName} recording={cand.recordingIndex} "
                      + $"disabledReason={disabledReason ?? string.Empty}"
                    : string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={refusal} "
                    + $"window={spec.Name}{detail}");
                SetExecResult("REJECTED", null, $"{refusal} window={spec.Name}{detail}");
                return;
            }

            ParsekLog.Info(Tag, $"uiaction warp window={spec.Name} vessel={cand.vesselName} "
                + $"recording={cand.recordingIndex} departure={Bool(row.UsesDepartureWarp)}");
            SetExecResult("OK",
                TestCommandUiAction.BuildWarpPayload(
                    spec.Name, cand.vesselName, cand.recordingIndex, row.UsesDepartureWarp),
                null);
        }

        // ----- op=warp window=timeline -----

        private void UiActionTimelineWarpOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            if (!TestCommandUiWarpTime.TryParseDate(
                    ArgOrNull(cmd, TestCommandUiWarpTime.YearArg),
                    ArgOrNull(cmd, TestCommandUiWarpTime.DayArg),
                    ArgOrNull(cmd, TestCommandUiWarpTime.HourArg),
                    ArgOrNull(cmd, TestCommandUiWarpTime.MinuteArg),
                    out UiWarpDate date, out string dateReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={dateReject} window={spec.Name}");
                SetExecResult("REJECTED", null, $"{dateReject} window={spec.Name}");
                return;
            }
            if (!TestCommandUiWarpTime.TryParseConfirm(
                    ArgOrNull(cmd, TestCommandUiWarpTime.ConfirmArg),
                    out bool confirmWarp, out string confirmReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={confirmReject} window={spec.Name}");
                SetExecResult("REJECTED", null, $"{confirmReject} window={spec.Name}");
                return;
            }

            TimelineWindowUI window = ParsekWindowSet.Of(ui).GetTimelineUI();
            if (window == null)
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiAction.HostUnavailableReason
                    + $" op={TestCommandUiAction.WarpOpToken} window={spec.Name} window=false");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiAction.HostUnavailableReason} window={spec.Name}");
                return;
            }

            string dateText = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Y{0} D{1} {2}:{3:00}", date.Year, date.Day, date.Hour, date.Minute);
            if (!window.TryPressWarpToTimeForTesting(date.Year, date.Day, date.Hour, date.Minute,
                    out double targetUT, out WarpToTimeMath.WarpPlanKind planKind,
                    out string disabledReason, out bool inFlight, out string refusal))
            {
                string detail = $" date={dateText} targetUT={WarpUtF1(targetUT)} "
                    + $"plan={TestCommandUiWarpTime.PlanToken(planKind)} "
                    + $"disabledReason={disabledReason ?? string.Empty}";
                ParsekLog.Warn(Tag, $"uiaction rejected reason={refusal} window={spec.Name}{detail}");
                SetExecResult("REJECTED", null, $"{refusal} window={spec.Name}{detail}");
                return;
            }

            string plan = TestCommandUiWarpTime.PlanToken(planKind);
            // Reported, not acted on: the click already resolved the same target through
            // this same call (WarpToTimeController.RequestWarp), so the payload names it.
            string rewindKind = TimelineWarpRewindKindToken(
                WarpToTimeController.ResolveRewindTarget(targetUT, out _).Kind);
            ParsekLog.Info(Tag, $"uiaction warp pressed window={spec.Name} date={dateText} "
                + $"targetUT={WarpUtF1(targetUT)} plan={plan} rewindKind={rewindKind} "
                + $"inFlight={Bool(inFlight)}");

            // The click body spawned the confirm synchronously; answer it the way a click on
            // the chosen button does (its own callback plus its dismiss-on-select).
            PopupDialog popup = FindPopupByName(WarpToTimeController.ConfirmDialogName);
            if (popup == null)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiWarpTime.ConfirmNotShownReason
                    + $" window={spec.Name} date={dateText} plan={plan}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiWarpTime.ConfirmNotShownReason} window={spec.Name} plan={plan}");
                return;
            }

            string label = TimelineWarpConfirmButtonLabel(confirmWarp);
            DialogGUIButton button = FindButtonByText(popup, label);
            if (button == null || DialogGuiButtonOptionSelectedMethod == null)
            {
                DialogGUIButton cancel = FindButtonByText(popup,
                    WarpToTimeController.ConfirmCancelButtonText);
                if (cancel != null && DialogGuiButtonOptionSelectedMethod != null)
                    DialogGuiButtonOptionSelectedMethod.Invoke(cancel, null);
                else
                    PopupDialog.DismissPopup(WarpToTimeController.ConfirmDialogName);
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiWarpTime.ConfirmButtonMissingReason
                    + $" window={spec.Name} button={label} buttons={Int(GetDialogButtons(popup).Count)}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiWarpTime.ConfirmButtonMissingReason} window={spec.Name} button={label}");
                return;
            }

            ParsekLog.Info(Tag, $"uiaction warp confirm press={label} window={spec.Name} "
                + $"date={dateText} plan={plan}");
            DialogGuiButtonOptionSelectedMethod.Invoke(button, null);

            bool pendingAfter = WarpToTimeRequest.HasPending;
            bool deferredAfter = WarpToTimeRequest.HasDeferredKscWarp;
            double utAfter = ReadUniversalTimeForWarpSeam();
            string outcome = TestCommandUiWarpTime.DecideConfirmOutcome(
                planKind, inFlight, confirmWarp, pendingAfter, deferredAfter,
                utAfter, targetUT);
            if (outcome != null)
            {
                ParsekLog.Error(Tag, $"uiaction error reason={outcome} window={spec.Name} "
                    + $"date={dateText} plan={plan} rewindKind={rewindKind} "
                    + $"pending={Bool(pendingAfter)} deferredKsc={Bool(deferredAfter)} "
                    + $"utAfter={WarpUtF1(utAfter)} targetUT={WarpUtF1(targetUT)}");
                SetExecResult("ERROR", null,
                    $"{outcome} window={spec.Name} plan={plan} utAfter={WarpUtF1(utAfter)} "
                    + $"targetUT={WarpUtF1(targetUT)}");
                return;
            }

            ParsekLog.Info(Tag, $"uiaction warp window={spec.Name} date={dateText} "
                + $"targetUT={WarpUtF1(targetUT)} plan={plan} rewindKind={rewindKind} "
                + $"confirm={(confirmWarp ? TestCommandUiWarpTime.ConfirmWarpToken : TestCommandUiWarpTime.ConfirmCancelToken)} "
                + $"pending={Bool(pendingAfter)} deferredKsc={Bool(deferredAfter)} "
                + $"utAfter={WarpUtF1(utAfter)}");
            SetExecResult("OK",
                TestCommandUiWarpTime.BuildPayload(date, targetUT, planKind,
                    rewindKind, confirmWarp, pendingAfter, deferredAfter),
                null);
        }

        /// <summary>The payload's <c>rewindKind=</c> token for a resolved rewind target.</summary>
        internal static string TimelineWarpRewindKindToken(WarpToTimeController.RewindTargetKind kind)
        {
            switch (kind)
            {
                case WarpToTimeController.RewindTargetKind.CareerStart:
                    return TestCommandUiWarpTime.RewindKindCareerStartToken;
                case WarpToTimeController.RewindTargetKind.Launch:
                    return TestCommandUiWarpTime.RewindKindLaunchToken;
                default:
                    return TestCommandUiWarpTime.RewindKindNoneToken;
            }
        }

        /// <summary>The confirm dialog's button label a <c>confirm=</c> token presses.</summary>
        internal static string TimelineWarpConfirmButtonLabel(bool confirmWarp)
            => confirmWarp
                ? WarpToTimeController.ConfirmWarpButtonText
                : WarpToTimeController.ConfirmCancelButtonText;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static double ReadUniversalTimeForWarpSeam()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0; }
        }

        private static string WarpUtF1(double v)
            => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }
}
