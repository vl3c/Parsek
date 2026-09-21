using System;
using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the appliers for <c>UiAction op=raise</c> and
    /// <c>op=dismiss</c> - the half of the census that puts a Parsek modal on screen and
    /// LEAVES IT STANDING for <c>op=dialog</c> plus <c>CaptureScreenshot</c>, then takes it
    /// down. Every decision (the closed dialog set, what each raise needs from the host,
    /// what a lane may press, and both payload shapes) lives in the pure sibling
    /// <see cref="TestCommandUiDialogRaise"/>.
    ///
    /// <para>
    /// WHY IT CALLS THE PRODUCTION SPAWN SITES. Each of these dialogs is spawned by ONE
    /// method whose whole body is the <c>PopupDialog.SpawnPopupDialog</c> call plus its
    /// button lambdas, so calling it is not a re-implementation - it is the same code a
    /// player's click reaches. Nothing here builds a <c>MultiOptionDialog</c> of its own:
    /// a seam-authored popup would photograph a screen the game cannot be in, and would
    /// additionally have to satisfy <c>ParsekDialogNamePrefixSourceGateTests</c> for a
    /// name no production path ever writes.
    /// </para>
    ///
    /// <para>
    /// BOTH OPS ARE TWO-PHASE, and for the <c>op=open</c> reason rather than by analogy. A
    /// <c>PopupDialog</c> is instantiated through uGUI, so the frame that follows the spawn
    /// is the first one in which it exists as a drawn surface - and the settle is what
    /// proves the popup that stands carries the EXPECTED name, which is the only thing
    /// separating "the spawn site ran" from "the spawn site's guard returned silently".
    /// Dismissal is the mirror: <c>PopupDialog.DismissPopup</c> destroys through Unity, so
    /// the read-back is only honest after a frame.
    /// </para>
    ///
    /// <para>
    /// THE INPUT LOCK, and it is released on EVERY exit rather than on the happy one.
    /// Exactly one spawn site in the table takes a <c>ControlTypes.All</c> lock that only
    /// its own button callbacks release (<c>UnfinishedFlightSealHandler</c>), so any path
    /// that leaves the dialog without pressing a button has to lift it or every later step
    /// in the lane runs behind a lock nothing will ever lift. There are FOUR such paths and
    /// the first cut covered one: the plain dismiss, the raise's own settle ERROR
    /// (<c>dialog-not-raised</c> - the lock is set and no dialog exists to clear it), and a
    /// dismiss REJECTED <c>dialog-not-open</c> (reachable whenever the popup went down some
    /// other way, Esc included, since that path runs no button callback). The fourth, the
    /// <c>dialog-not-dismissed</c> settle ERROR, needs nothing: the release already ran at
    /// execute time. Ordering the row last in a lane is spec discipline, not a guard, so
    /// <see cref="ReleaseRaisedDialogInputLock"/> is called from all three.
    /// <see cref="UiRaisableDialog.OwnsInputLock"/> carries the fact per row rather than a
    /// blanket "clear every Parsek lock", which would reach locks this op did not set.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // ----- op=raise -----

        private void UiActionRaiseOp(ParsedCommand cmd)
        {
            if (!TestCommandUiDialogRaise.TryResolveDialog(
                    ArgOrNull(cmd, TestCommandUiDialogRaise.PopupArg),
                    out UiRaisableDialog spec, out string reject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiDialogRaise.PopupArg)
                             ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={reject} popup={raw}");
                SetExecResult("REJECTED", null,
                    reject == TestCommandUiDialogRaise.PopupUnknownReason
                        ? $"{reject} popup={raw} "
                          + $"valid={TestCommandUiDialogRaise.ValidPopupNames}"
                        : reject);
                return;
            }

            // ONE AT A TIME. Parsek's own contract is that at most one modal stands, and a
            // second would make the following op=dialog report an arbitrary one of two.
            List<PopupDialog> standing = FindParsekPopups();
            if (standing.Count > 0)
            {
                string openName = ReadDialogName(standing[0]) ?? "-";
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiDialogRaise.AlreadyOpenReason
                    + $" popup={spec.Name} open={openName} count={Int(standing.Count)}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiDialogRaise.AlreadyOpenReason} popup={spec.Name} "
                    + $"open={openName}");
                return;
            }

            // ONE resolver per declared capability, each answering its own detail token.
            // Walked in table-field order; a row declares at most one, but the loop does
            // not rely on that - the first missing capability names itself.
            var host = new RaiseHost();
            if (!TryResolveRaiseHost(spec, ref host, out string unavailable))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiDialogRaise.TargetUnavailableReason
                    + $" popup={spec.Name} detail={unavailable}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiDialogRaise.TargetUnavailableReason} "
                    + $"popup={spec.Name} detail={unavailable}");
                return;
            }

            ParsekLog.Info(Tag, $"uiaction raise popup={spec.Name} name={spec.PopupName} "
                + $"title={spec.Title} "
                + $"rec={(host.Recording != null ? (host.Recording.RecordingId ?? "-") : "-")} "
                + $"route={(host.Route != null ? (host.Route.Id ?? "-") : "-")} "
                + $"candidate={(host.Candidate?.Tree != null ? (host.Candidate.Tree.Id ?? "-") : "-")} "
                + $"pressable={TestCommandUiDialogRaise.PressableButtonsOf(spec)}");

            SpawnRaisableDialog(spec, host);

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Raise,
                Window = null,
                StartFrame = Time.frameCount,
                RaiseDialogName = spec.Name,
            };
            SetExecResult(PendingVerdict, null, null);
        }

        /// <summary>
        /// Calls the ONE production spawn site for a table row.
        ///
        /// <para>A switch rather than a delegate on the table row, deliberately: the rows
        /// live in the PURE half, which must not hold a <c>ParsekUI</c> / <c>Recording</c>
        /// closure. The pairing between a row and its spawn site is therefore checked by
        /// the settle's name comparison rather than by the type system - which is the
        /// stronger check of the two, because it is made against the live popup.</para>
        /// </summary>
        private static void SpawnRaisableDialog(UiRaisableDialog spec, RaiseHost host)
        {
            Recording target = host.Recording;
            switch (spec.Name)
            {
                case TestCommandUiDialogRaise.ActionBlockedDialog:
                    // The three strings a blocked action would carry. Named as a census
                    // raise rather than dressed up as a real refusal: a reviewer reading
                    // the PNG must not take it for a live conflict.
                    CommittedActionDialog.ShowBlocked(
                        "GUI census: raise the Action Blocked dialog",
                        "This popup was raised by the automation seam so the census has a "
                        + "picture of it. Nothing is blocked.",
                        null);
                    return;

                case TestCommandUiDialogRaise.SaveFailedDialog:
                    SceneExitInterceptor.ShowSaveFailedPopup();
                    return;

                case TestCommandUiDialogRaise.WipeRecordingsDialog:
                    ParsekUI.ActiveInstance.ShowWipeRecordingsConfirmation(
                        CountEffectiveRecordings());
                    return;

                case TestCommandUiDialogRaise.WipeMilestonesDialog:
                    ParsekUI.ActiveInstance.ShowWipeMilestonesConfirmation(
                        MilestoneStore.Milestones != null
                            ? MilestoneStore.Milestones.Count : 0);
                    return;

                case TestCommandUiDialogRaise.RewindDialog:
                    ParsekUI.ActiveInstance.GetRecordingsTableUI()
                        .ShowRewindConfirmation(target);
                    return;

                case TestCommandUiDialogRaise.FastForwardDialog:
                    ParsekUI.ActiveInstance.GetRecordingsTableUI()
                        .ShowFastForwardConfirmation(target);
                    return;

                case TestCommandUiDialogRaise.SealDialog:
                    UnfinishedFlightSealHandler.ShowConfirmation(target);
                    return;

                case TestCommandUiDialogRaise.DeleteRouteDialog:
                    ParsekUI.ActiveInstance.GetLogisticsUI()
                        .SpawnDeleteRouteConfirmationForTesting(host.Route);
                    return;

                case TestCommandUiDialogRaise.DeleteDormantRouteDialog:
                    ParsekUI.ActiveInstance.GetLogisticsUI()
                        .SpawnDeleteDormantRouteConfirmationForTesting(host.Route);
                    return;

                case TestCommandUiDialogRaise.CreateRouteDialog:
                    ParsekUI.ActiveInstance.GetLogisticsUI()
                        .SpawnCreateRouteConfirmationForTesting(host.Candidate);
                    return;

                default:
                    // Unreachable: TryResolveDialog admitted the token from the same table
                    // this switch covers. Thrown rather than ignored so a row added to the
                    // table without a spawn arm fails loudly on its first raise instead of
                    // settling as dialog-not-raised.
                    throw new InvalidOperationException(
                        "no spawn arm for raisable dialog '" + (spec.Name ?? "<null>") + "'");
            }
        }

        /// <summary>
        /// Everything the live host supplied for one raise: at most one of these is
        /// non-null today, and the struct exists so the spawn switch takes ONE parameter
        /// however many capabilities the table grows.
        /// </summary>
        private struct RaiseHost
        {
            internal Recording Recording;
            internal Route Route;
            internal RouteCandidate Candidate;
        }

        /// <summary>
        /// Runs the resolver for each capability a row declares, filling
        /// <paramref name="host"/>. False names the FIRST missing one in
        /// <paramref name="detail"/>, which rides on the PRE-CALL
        /// <c>dialog-target-unavailable</c> reject - the alternative is calling a spawn
        /// site whose own guard returns silently and then reporting a modal that is not
        /// there.
        /// </summary>
        private static bool TryResolveRaiseHost(UiRaisableDialog spec, ref RaiseHost host,
                                                out string detail)
        {
            detail = null;
            if (spec.NeedsRecording
                && !TryResolveRaiseRecording(spec, out host.Recording, out detail))
            {
                return false;
            }
            if (spec.NeedsCommittedRoute
                && !TryResolveRaiseCommittedRoute(out host.Route, out detail))
            {
                return false;
            }
            if (spec.NeedsDormantRoute
                && !TryResolveRaiseDormantRoute(out host.Route, out detail))
            {
                return false;
            }
            if (spec.NeedsRouteCandidate
                && !TryResolveRaiseRouteCandidate(out host.Candidate, out detail))
            {
                return false;
            }
            return true;
        }

        /// <summary>The FIRST stored route, the <see cref="TryResolveRaiseRecording"/>
        /// rule: the dialog's SUBJECT does not matter to a census, only that it is a route
        /// that really exists.</summary>
        private static bool TryResolveRaiseCommittedRoute(out Route route, out string detail)
        {
            route = null;
            detail = null;
            IReadOnlyList<Route> routes = RouteStore.CommittedRoutes;
            int count = routes != null ? routes.Count : 0;
            for (int i = 0; i < count; i++)
            {
                if (routes[i] == null) continue;
                route = routes[i];
                return true;
            }
            detail = count == 0
                ? "no-committed-routes"
                : $"no-usable-committed-route-among={count}";
            return false;
        }

        /// <summary>The FIRST dormant route. A disjoint population from the committed one,
        /// so a host can carry routes and still answer unavailable here.</summary>
        private static bool TryResolveRaiseDormantRoute(out Route route, out string detail)
        {
            route = null;
            detail = null;
            IReadOnlyList<Route> routes = RouteStore.DormantRoutes;
            int count = routes != null ? routes.Count : 0;
            for (int i = 0; i < count; i++)
            {
                if (routes[i] == null) continue;
                route = routes[i];
                return true;
            }
            detail = count == 0
                ? "no-dormant-routes"
                : $"no-usable-dormant-route-among={count}";
            return false;
        }

        /// <summary>
        /// The FIRST candidate the Logistics window is DRAWING that carries both
        /// <c>Tree</c> and <c>Analysis</c> - the pair the spawn site's own guard requires.
        ///
        /// <para>Read off the window's throttled cache rather than from a fresh
        /// <c>RouteCandidateFinder.DeriveCandidates()</c> call. A live derivation would
        /// answer candidates the window is not drawing this second, so the census could
        /// photograph a Create Route confirm over a row that is not on screen; and
        /// deriving off the ~1 Hz throttle is the one thing that cache exists to prevent.
        /// The cost is that the window must have DRAWN once for the cache to be
        /// populated, which is the <c>run-runner-not-ready</c> shape and is named in the
        /// detail token.</para>
        /// </summary>
        private static bool TryResolveRaiseRouteCandidate(out RouteCandidate candidate,
                                                          out string detail)
        {
            candidate = null;
            detail = null;
            ParsekUI ui = ParsekUI.ActiveInstance;
            LogisticsWindowUI window = ui != null ? ui.GetLogisticsUI() : null;
            if (window == null)
            {
                detail = "no-logistics-window";
                return false;
            }
            IReadOnlyList<RouteCandidate> cached = window.CachedCandidatesForTesting;
            int count = cached != null ? cached.Count : 0;
            for (int i = 0; i < count; i++)
            {
                RouteCandidate c = cached[i];
                if (c == null || c.Tree == null || c.Analysis == null) continue;
                candidate = c;
                return true;
            }
            detail = count == 0
                ? "no-drawn-route-candidates"
                : $"no-usable-route-candidate-among={count}";
            return false;
        }

        /// <summary>
        /// Picks the recording a raise acts on: the FIRST of the effective set, and for the
        /// one row that needs it, the first whose rewind owner resolves.
        ///
        /// <para>Routed through <c>EffectiveState.ComputeERS()</c> like every other seam
        /// read of the committed set, so a superseded recording cannot be the subject of a
        /// photographed dialog. "First" is chosen rather than "newest" because the dialog's
        /// SUBJECT does not matter to a census - what matters is that the dialog is the one
        /// a player would see, over a recording that really exists.</para>
        /// </summary>
        private static bool TryResolveRaiseRecording(UiRaisableDialog spec,
                                                     out Recording target,
                                                     out string detail)
        {
            target = null;
            detail = null;
            IReadOnlyList<Recording> effective = EffectiveState.ComputeERS();
            int count = effective != null ? effective.Count : 0;
            if (count == 0)
            {
                detail = "no-effective-recordings";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                Recording rec = effective[i];
                if (rec == null) continue;
                if (spec.NeedsRewindOwner
                    && RecordingStore.GetRewindRecording(rec) == null)
                {
                    continue;
                }
                target = rec;
                return true;
            }
            detail = spec.NeedsRewindOwner
                ? $"no-rewind-owner-among={count}"
                : $"no-usable-recording-among={count}";
            return false;
        }

        /// <summary>How many recordings the wipe confirmation's count should say. The
        /// effective set, for the reason above - the production button reads the same
        /// number through the Settings window.</summary>
        private static int CountEffectiveRecordings()
        {
            IReadOnlyList<Recording> effective = EffectiveState.ComputeERS();
            return effective != null ? effective.Count : 0;
        }

        // ----- op=raise settle -----

        private void CompleteUiActionRaise(string id, long seq, string verb,
                                           UiActionPending pending)
        {
            if (!TestCommandUiDialogRaise.TryResolveDialog(
                    pending.RaiseDialogName, out UiRaisableDialog spec, out string _))
            {
                // Unreachable (the arm resolved the same token), and reported rather than
                // thrown so a settle can never leave the FIFO head held.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiDialogRaise.PopupUnknownReason
                    + $" popup={pending.RaiseDialogName ?? "-"} (settle)");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandUiDialogRaise.PopupUnknownReason, dequeueHead: true);
                return;
            }

            PopupDialog popup = FindPopupByName(spec.PopupName);
            string standingName = popup != null ? ReadDialogName(popup) : null;
            if (!TestCommandUiDialogRaise.RaiseConfirmed(spec, standingName))
            {
                // The spawn site took its input lock and then returned without a dialog,
                // so nothing will ever run the button callback that lifts it.
                ReleaseRaisedDialogInputLock(spec, "raise settled with no dialog standing");
                List<PopupDialog> any = FindParsekPopups();
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiDialogRaise.NotRaisedReason
                    + $" popup={spec.Name} want={spec.PopupName} "
                    + $"standing={standingName ?? "-"} parsekPopups={Int(any.Count)}; the "
                    + "production spawn site ran and no popup of that name is up - its own "
                    + "guard returned, or the dialog was dismissed before the settle");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiDialogRaise.NotRaisedReason} popup={spec.Name}",
                    dequeueHead: true);
                return;
            }

            var labels = new List<string>();
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            for (int i = 0; i < buttons.Count; i++)
                labels.Add(buttons[i] != null ? buttons[i].OptionText : null);
            string title = ReadDialogTitle(popup);

            ParsekLog.Info(Tag, $"uiaction raise ok popup={spec.Name} "
                + $"name={standingName} title={title ?? spec.Title ?? "-"} "
                + $"nbuttons={Int(labels.Count)} "
                + $"buttons={TestCommandUiDialog.FormatButtons(labels)}; it is STANDING and "
                + "nothing in the seam dismisses it before the next step");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandUiDialogRaise.BuildRaisePayload(spec, title, labels),
                null, dequeueHead: true);
        }

        // ----- op=dismiss -----

        private void UiActionDismissOp(ParsedCommand cmd)
        {
            if (!TestCommandUiDialogRaise.TryResolveDialog(
                    ArgOrNull(cmd, TestCommandUiDialogRaise.PopupArg),
                    out UiRaisableDialog spec, out string reject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiDialogRaise.PopupArg)
                             ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={reject} popup={raw}");
                SetExecResult("REJECTED", null,
                    reject == TestCommandUiDialogRaise.PopupUnknownReason
                        ? $"{reject} popup={raw} "
                          + $"valid={TestCommandUiDialogRaise.ValidPopupNames}"
                        : reject);
                return;
            }

            if (!TestCommandUiDialogRaise.TryResolvePress(
                    spec, ArgOrNull(cmd, TestCommandUiDialogRaise.PressArg),
                    out string pressButton, out string pressReject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiDialogRaise.PressArg)
                             ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={pressReject} "
                    + $"popup={spec.Name} press={raw} "
                    + $"pressable={TestCommandUiDialogRaise.PressableButtonsOf(spec)}");
                SetExecResult("REJECTED", null,
                    $"{pressReject} popup={spec.Name} press={raw} "
                    + $"pressable={TestCommandUiDialogRaise.PressableButtonsOf(spec)}");
                return;
            }

            PopupDialog popup = FindPopupByName(spec.PopupName);
            if (popup == null)
            {
                // REJECTED rather than a cheerful OK: a lane whose raise failed would
                // otherwise photograph an empty screen under a dialog label and read green.
                // The popup went down some other way (Esc dismisses a PopupDialog without
                // running any button callback), so the lock its spawn took is still set.
                ReleaseRaisedDialogInputLock(
                    spec, "dismiss found no standing dialog to take down");
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiDialogRaise.NotOpenReason
                    + $" popup={spec.Name} name={spec.PopupName}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiDialogRaise.NotOpenReason} popup={spec.Name}");
                return;
            }

            if (pressButton != null)
            {
                if (!TryPressRaisedDialogButton(popup, pressButton, out string pressError))
                {
                    ParsekLog.Error(Tag, "uiaction error reason="
                        + $"{pressError} popup={spec.Name} press={pressButton}");
                    SetExecResult("ERROR", null,
                        $"{pressError} popup={spec.Name} press={pressButton}");
                    return;
                }
                ParsekLog.Info(Tag, $"uiaction dismiss popup={spec.Name} "
                    + $"press={pressButton} via=button");
            }
            else
            {
                PopupDialog.DismissPopup(spec.PopupName);
                // The ONE row whose spawn site takes a ControlTypes.All lock that only its
                // own button callbacks release. Dismissing without pressing leaves it set,
                // and every later step in the lane would run behind it.
                ReleaseRaisedDialogInputLock(spec, "dismissed without pressing a button");
                ParsekLog.Info(Tag, $"uiaction dismiss popup={spec.Name} "
                    + "press=- via=dismisspopup");
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Dismiss,
                Window = null,
                StartFrame = Time.frameCount,
                RaiseDialogName = spec.Name,
                DismissPressButton = pressButton,
            };
            SetExecResult(PendingVerdict, null, null);
        }

        /// <summary>
        /// Presses one named button on a standing popup through its OWN callback - the same
        /// entry point <c>AnswerMergeDialog</c> uses (<c>OptionSelected</c> via
        /// <see cref="DialogGuiButtonOptionSelectedMethod"/>), so the handler runs
        /// synchronously in this frame.
        ///
        /// <para>Selected BY LABEL rather than by position, which is the whole difference
        /// from the merge answer's first/last selection: here the caller named a label, the
        /// pure half already checked it against the table AND against the press policy, and
        /// a label match is what makes "press Cancel" unable to become "press Wipe All"
        /// when a dialog's button order changes.</para>
        /// </summary>
        private bool TryPressRaisedDialogButton(PopupDialog popup, string label,
                                                out string error)
        {
            error = null;
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            if (buttons.Count == 0 || DialogGuiButtonOptionSelectedMethod == null)
            {
                error = TestCommandUiDialogRaise.NotOpenReason;
                return false;
            }
            for (int i = 0; i < buttons.Count; i++)
            {
                DialogGUIButton button = buttons[i];
                if (button == null) continue;
                if (!string.Equals(button.OptionText, label, StringComparison.Ordinal))
                    continue;
                DialogGuiButtonOptionSelectedMethod.Invoke(button, null);
                return true;
            }
            error = TestCommandUiDialogRaise.PressUnknownReason;
            return false;
        }

        /// <summary>
        /// Lifts the <c>ControlTypes.All</c> lock the ONE lock-owning row's spawn site takes,
        /// naming why. A no-op for every other row, so this is never a blanket "clear every
        /// Parsek lock" - it releases exactly the lock this op caused to be set.
        /// </summary>
        private static void ReleaseRaisedDialogInputLock(UiRaisableDialog spec, string why)
        {
            if (!spec.OwnsInputLock) return;
            UnfinishedFlightSealHandler.ClearLock();
            ParsekLog.Info(Tag, $"uiaction dismiss popup={spec.Name} "
                + "released the dialog's own ControlTypes.All input lock, which only its "
                + $"button callbacks would otherwise have cleared ({why})");
        }

        // ----- op=dismiss settle -----

        private void CompleteUiActionDismiss(string id, long seq, string verb,
                                             UiActionPending pending)
        {
            if (!TestCommandUiDialogRaise.TryResolveDialog(
                    pending.RaiseDialogName, out UiRaisableDialog spec, out string _))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiDialogRaise.PopupUnknownReason
                    + $" popup={pending.RaiseDialogName ?? "-"} (settle)");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    TestCommandUiDialogRaise.PopupUnknownReason, dequeueHead: true);
                return;
            }

            bool stillOpen = FindPopupByName(spec.PopupName) != null;
            if (stillOpen)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiDialogRaise.NotDismissedReason
                    + $" popup={spec.Name} name={spec.PopupName} "
                    + $"press={pending.DismissPressButton ?? "-"}");
                EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                    $"{TestCommandUiDialogRaise.NotDismissedReason} popup={spec.Name}",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction dismiss ok popup={spec.Name} "
                + $"press={pending.DismissPressButton ?? "-"} open=false");
            EmitExecutedTerminal(id, seq, verb, "OK",
                TestCommandUiDialogRaise.BuildDismissPayload(
                    spec, pending.DismissPressButton, false),
                null, dequeueHead: true);
        }
    }
}
