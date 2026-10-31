using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>Recover</c> verb; the contract is on
    /// <see cref="TestCommandRecover"/>.
    ///
    /// <para><b>THE STOCK BUTTON'S OWN CLICK.</b> The verb invokes the live
    /// <c>AltimeterSliderButtons.vesselRecoveryButton.onClick</c>, whose only listener is
    /// stock's <c>recoverVessel</c>. Everything after that is stock and Parsek running as
    /// for a player: <c>OnVesselRecoveryRequested</c> (Parsek arms
    /// <c>InFlightRecoveryRequest</c>), stock's save + Space Center load (Parsek's scene-exit
    /// finalize applies the request), then <c>onVesselRecovered</c> at the Space Center
    /// (Parsek's terminal-state, funds and crew handlers). The seam fires no event; it only
    /// LISTENS to the two stock events, to read back that the click fired and that stock
    /// recovered the named vessel.</para>
    ///
    /// <para><b>A LOCKED BUTTON IS NOT PRESSED.</b> Stock unlocks the button only for a
    /// landed or splashed active vessel on the home world below 0.3 m/s horizontal speed,
    /// unpaused, in a game that allows recovery (<c>UIExtensions.Lock</c> is
    /// <c>interactable = false</c>). <c>Button.onClick.Invoke</c> ignores
    /// <c>interactable</c>, so the verb reads it first and refuses a locked button.</para>
    ///
    /// <para><b>A SLIDING CRAFT IS WAITED ON.</b> A locked or not-clear-to-save button is
    /// re-sampled every frame for up to <see cref="TestCommandRecover.UnlockWaitSeconds"/> before
    /// the press, as a player waits for a craft that touched down sliding; the verb presses the
    /// frame it unlocks, or refuses with the reason that still holds.</para>
    ///
    /// <para><b>THE WEDGE GUARD.</b> Stock's recovery drives <c>HighLogic.LoadScene(SPACECENTER)</c>,
    /// which Parsek's scene-exit prefix turns into a merge modal when a merge decision is
    /// outstanding. The verb evaluates ExitToSpaceCenter's gate on the same live predicates
    /// and refuses with its <c>dialog-required variant=</c> before clicking. With
    /// <c>mergeDialog=refly</c> the one exception is the Re-Fly merge modal under a live
    /// Re-Fly marker: the verb presses, and answers OK the frame that modal is up, so the
    /// spec's <c>AnswerMergeDialog</c> step can conclude it (stock's recovery then runs at
    /// the Space Center the dialog's post-choice loads).</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private uint recoverPid;
        private System.Guid recoverVesselGuid;
        private string recoverVesselName;
        private bool recoverRequestObserved;
        private bool recoverRecoveredObserved;
        private bool recoverListenersAdded;
        private bool recoverUnlockWait;
        private int recoverUnlockWaitStartFrame;
        // The spec's mergeDialog=refly opt-in, and whether this press went into that modal.
        private bool recoverAcceptReFlyDialog;
        private bool recoverIntoReFlyDialog;

        private void RecoverImpl(ParsedCommand cmd)
        {
            string pidArg = ArgOrNull(cmd, TestCommandRecover.PidKey);
            string argReason = TestCommandRecover.ParsePid(pidArg, out uint pid);
            if (argReason != null)
            {
                RejectRecover(argReason, $"pid={pidArg ?? string.Empty}");
                return;
            }
            string mergeDialogArg = ArgOrNull(cmd, TestCommandRecover.MergeDialogKey);
            string mergeDialogReason = TestCommandRecover.ParseMergeDialog(mergeDialogArg, out bool acceptReFlyDialog);
            if (mergeDialogReason != null)
            {
                RejectRecover(mergeDialogReason, $"mergeDialog={mergeDialogArg ?? string.Empty}");
                return;
            }
            recoverAcceptReFlyDialog = acceptReFlyDialog;
            recoverIntoReFlyDialog = false;

            string gate = SampleRecoverGate(pid, out Vessel active, out Button button, out string detail);
            RecoverUnlockWaitOutcome outcome = TestCommandRecover.DecideUnlockWait(
                gate, 0.0, TestCommandRecover.UnlockWaitSeconds);
            if (outcome == RecoverUnlockWaitOutcome.Refuse)
            {
                RejectRecover(gate, detail);
                return;
            }
            if (outcome == RecoverUnlockWaitOutcome.Wait)
            {
                // Go two-phase WITHOUT pressing: TryCompleteRecoverUnlockWait re-samples the
                // gate every frame and presses the frame it opens, or refuses at the window's end.
                recoverUnlockWait = true;
                recoverUnlockWaitStartFrame = Time.frameCount;
                recoverPid = pid;
                ParsekLog.Info(Tag, $"recover unlock-wait start reason={gate} {detail} - re-checking every frame "
                    + "for up to " + TestCommandRecover.UnlockWaitSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s");
                SetExecResult(PendingVerdict, null, null);
                return;
            }

            if (TryPressRecover(pid, active, button, out string verdict,
                    out List<KeyValuePair<string, string>> payload, out string msg))
                SetExecResult(PendingVerdict, null, null);
            else
                SetExecResult(verdict, payload, msg);
        }

        /// <summary>Samples the pre-press gate on the live active vessel and Recover button;
        /// <paramref name="detail"/> is the refusal line's context.</summary>
        private string SampleRecoverGate(uint pid, out Vessel active, out Button button, out string detail)
        {
            active = FlightGlobals.ActiveVessel;
            AltimeterSliderButtons altimeter = UnityEngine.Object.FindObjectOfType<AltimeterSliderButtons>();
            button = altimeter != null ? altimeter.vesselRecoveryButton : null;
            string clearToSave = active != null ? FlightGlobals.ClearToSave().ToString() : string.Empty;
            bool canLeave = HighLogic.CurrentGame != null
                && HighLogic.CurrentGame.Parameters.Flight.CanLeaveToSpaceCenter;

            string gate = TestCommandRecover.DecideGate(
                active != null, active != null ? active.persistentId : 0u, pid,
                button != null, button != null && button.interactable,
                clearToSave, canLeave);
            detail = null;
            if (gate != null)
            {
                CultureInfo ic = CultureInfo.InvariantCulture;
                detail = $"pid={pid.ToString(ic)}";
                if (active != null)
                    detail += $" activePid={active.persistentId.ToString(ic)} vessel={active.vesselName} "
                        + $"situation={active.situation} homeWorld={Bool(active.mainBody != null && active.mainBody.isHomeWorld)} "
                        + $"hSrfSpeed={active.horizontalSrfSpeed.ToString("F2", ic)} paused={Bool(FlightDriver.Pause)}";
                detail += $" clearToSave={clearToSave} canLeave={Bool(canLeave)}";
            }
            return gate;
        }

        /// <summary>
        /// The wedge guard, then the click. Returns true when the click fired stock's recovery
        /// request (the verb then waits for the Space Center, or for the Re-Fly merge modal
        /// under <c>mergeDialog=refly</c>); otherwise false with the terminal verdict, payload
        /// and msg (an OK hand-off when the accepted modal came up inside the click).
        /// </summary>
        private bool TryPressRecover(uint pid, Vessel active, Button button, out string verdict,
            out List<KeyValuePair<string, string>> payload, out string msg)
        {
            verdict = null;
            payload = null;
            msg = null;
            // The wedge guard, on the same live predicates ExitToSpaceCenter reads.
            ParsekFlight flight = ParsekFlight.Instance;
            ParsekScenario scenario = ParsekScenario.Instance;
            bool hasActiveTree = flight != null && flight.HasActiveTree;
            bool sessionArmed = scenario != null && scenario.ActiveSwitchSegmentSession != null;
            SceneExitInterceptor.DialogVariant activeTreeVariant =
                SceneExitInterceptor.ShouldShowDialogBeforeSceneChangeLive(GameScenes.SPACECENTER, flight);
            SceneExitInterceptor.DialogVariant pendingTreeVariant =
                SceneExitInterceptor.ShouldShowPendingTreeDialogBeforeSceneChangeLive(GameScenes.SPACECENTER);
            ExitGateDecision exitGate = TestCommandExitToSpaceCenter.DecideExitGate(
                hasActiveTree, sessionArmed, activeTreeVariant, pendingTreeVariant);
            bool markerLive = scenario != null && scenario.ActiveReFlySessionMarker != null;
            RecoverWedgeDecision wedge = TestCommandRecover.DecideWedge(
                exitGate, activeTreeVariant, markerLive, recoverAcceptReFlyDialog);
            if (wedge == RecoverWedgeDecision.Refuse)
            {
                string variant = TestCommandExitToSpaceCenter.VariantToken(
                    exitGate, activeTreeVariant, pendingTreeVariant);
                ParsekLog.Warn(Tag, $"recover rejected reason={TestCommandExitToSpaceCenter.DialogRequiredReason} "
                    + $"variant={variant} gate={exitGate} pid={pid.ToString(CultureInfo.InvariantCulture)} "
                    + $"markerLive={Bool(markerLive)} acceptReFlyDialog={Bool(recoverAcceptReFlyDialog)} "
                    + "- the recovery's Space Center load would raise a merge modal this step does not hand off");
                verdict = "REJECTED";
                msg = TestCommandExitToSpaceCenter.RefusalMsg(variant);
                return false;
            }
            recoverIntoReFlyDialog = wedge == RecoverWedgeDecision.PressIntoReFlyDialog;
            if (recoverIntoReFlyDialog)
                ParsekLog.Info(Tag, $"recover wedge accepted variant={activeTreeVariant} gate={exitGate} "
                    + $"pid={pid.ToString(CultureInfo.InvariantCulture)} - pressing into the re-fly merge dialog (mergeDialog=refly)");

            recoverPid = pid;
            recoverVesselGuid = active.id;
            recoverVesselName = active.vesselName;
            recoverRequestObserved = false;
            recoverRecoveredObserved = false;
            AddRecoverListeners();

            ParsekLog.Info(Tag, TestCommandRecover.FormatPressedLine(
                pid, active.vesselName, active.situation.ToString(), Planetarium.GetUniversalTime()));
            button.onClick.Invoke();

            // GameEvents fire synchronously, so the click either fired the request inside
            // the call or it did nothing.
            if (!recoverRequestObserved)
            {
                RemoveRecoverListeners();
                ParsekLog.Warn(Tag, $"recover error reason={TestCommandRecover.RequestNotFiredReason} "
                    + $"pid={pid.ToString(CultureInfo.InvariantCulture)} - the click fired no OnVesselRecoveryRequested");
                verdict = "ERROR";
                msg = TestCommandRecover.RequestNotFiredReason;
                return false;
            }
            if (TryHandOffReFlyMergeDialog(out payload))
            {
                verdict = "OK";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Under an accepted Re-Fly wedge, true once the Re-Fly merge modal is up: logs the
        /// hand-off, detaches the listeners (the recovery completes after the modal is answered,
        /// under the next step) and returns the OK payload. False otherwise.
        /// </summary>
        private bool TryHandOffReFlyMergeDialog(out List<KeyValuePair<string, string>> payload)
        {
            payload = null;
            if (!recoverIntoReFlyDialog || recoverRecoveredObserved || FindReFlyMergePopup() == null)
                return false;
            recoverIntoReFlyDialog = false;
            RemoveRecoverListeners();
            ParsekLog.Info(Tag, TestCommandRecover.FormatDialogHandoffLine(
                recoverPid, recoverVesselName, Planetarium.GetUniversalTime()));
            payload = TestCommandRecover.BuildDialogHandoffPayload(
                recoverPid, recoverVesselName, HighLogic.LoadedScene.ToString());
            return true;
        }

        // The unlock-wait poll: one gate sample per frame until it opens (press, then the
        // Space Center wait below runs on later frames), a final refusal appears, or the window
        // ends (REJECTED with the gate's reason - nothing was clicked).
        private void TryCompleteRecoverUnlockWait(double now)
        {
            double waited = now - completionStartedAt;
            uint pid = recoverPid;
            string gate = SampleRecoverGate(pid, out Vessel active, out Button button, out string detail);
            RecoverUnlockWaitOutcome outcome = TestCommandRecover.DecideUnlockWait(
                gate, waited, TestCommandRecover.UnlockWaitSeconds);
            if (outcome == RecoverUnlockWaitOutcome.Wait)
                return;

            int frames = Time.frameCount - recoverUnlockWaitStartFrame;
            recoverUnlockWait = false;
            string id = completionId; long seq = completionSeq; string verb = completionVerb;

            if (outcome == RecoverUnlockWaitOutcome.Proceed)
            {
                ParsekLog.Info(Tag, TestCommandRecover.FormatUnlockWaitClearedLine(pid, waited, frames));
                if (TryPressRecover(pid, active, button, out string verdict,
                        out List<KeyValuePair<string, string>> payload, out string msg))
                    return;
                ClearTwoPhase();
                EmitExecutedTerminal(id, seq, verb, verdict, payload, msg, dequeueHead: true);
                return;
            }

            ClearTwoPhase();
            ParsekLog.Warn(Tag, $"recover rejected reason={gate} after unlock-wait "
                + $"waited={waited.ToString("F1", CultureInfo.InvariantCulture)}s frames={Int(frames)} {detail}");
            EmitExecutedTerminal(id, seq, verb, "REJECTED", null, gate, dequeueHead: true);
        }

        private void TryCompleteRecover(double now)
        {
            if (recoverUnlockWait)
            {
                TryCompleteRecoverUnlockWait(now);
                return;
            }
            // An accepted Re-Fly modal that spawned after the click frame.
            if (TryHandOffReFlyMergeDialog(out List<KeyValuePair<string, string>> handoff))
            {
                string hid = completionId; long hseq = completionSeq; string hverb = completionVerb;
                ClearTwoPhase();
                EmitExecutedTerminal(hid, hseq, hverb, "OK", handoff, null, dequeueHead: true);
                return;
            }
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandRecover.Verb);
            RecoverCompletionDecision decision = TestCommandRecover.DecideCompletion(
                elapsed, MapScene(HighLogic.LoadedScene), HighLogic.CurrentGame != null,
                recoverRecoveredObserved, budget);
            if (decision == RecoverCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            uint pid = recoverPid;
            string vesselName = recoverVesselName;
            bool recovered = recoverRecoveredObserved;
            ClearTwoPhase();
            CultureInfo ic = CultureInfo.InvariantCulture;
            string sceneName = HighLogic.LoadedScene.ToString();

            switch (decision)
            {
                case RecoverCompletionDecision.Recovered:
                    ParsekLog.Info(Tag, TestCommandRecover.FormatCompleteLine(pid, vesselName, sceneName, elapsed));
                    EmitExecutedTerminal(id, seq, verb, "OK",
                        TestCommandRecover.BuildOkPayload(pid, vesselName, sceneName), null, dequeueHead: true);
                    break;
                case RecoverCompletionDecision.ReturnedToMenu:
                    ParsekLog.Error(Tag, $"recover error reason={TestCommandRecover.ReturnedToMenuReason} "
                        + $"pid={pid.ToString(ic)} elapsed={elapsed.ToString("F1", ic)}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRecover.ReturnedToMenuReason, dequeueHead: true);
                    break;
                case RecoverCompletionDecision.Timeout:
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, TestCommandRecover.TimeoutReason);
                    ParsekLog.Error(Tag, $"recover error reason={TestCommandRecover.TimeoutReason} "
                        + $"pid={pid.ToString(ic)} scene={sceneName} recovered={Bool(recovered)} "
                        + $"elapsed={elapsed.ToString("F1", ic)}s");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null,
                        TestCommandRecover.TimeoutReason, dequeueHead: true);
                    break;
            }
        }

        private void AddRecoverListeners()
        {
            if (recoverListenersAdded) return;
            GameEvents.OnVesselRecoveryRequested.Add(OnRecoverSeamRequestObserved);
            GameEvents.onVesselRecovered.Add(OnRecoverSeamRecoveredObserved);
            recoverListenersAdded = true;
        }

        // Idempotent; also called from ClearTwoPhase so a timeout or a completion throw
        // never leaves the listeners attached.
        private void RemoveRecoverListeners()
        {
            if (!recoverListenersAdded) return;
            GameEvents.OnVesselRecoveryRequested.Remove(OnRecoverSeamRequestObserved);
            GameEvents.onVesselRecovered.Remove(OnRecoverSeamRecoveredObserved);
            recoverListenersAdded = false;
        }

        private void OnRecoverSeamRequestObserved(Vessel v)
        {
            if (v != null && v.persistentId == recoverPid)
                recoverRequestObserved = true;
        }

        private void OnRecoverSeamRecoveredObserved(ProtoVessel pv, bool quick)
        {
            if (pv == null) return;
            // Stock's VesselRetrieval matches by Guid; the pid can be regenerated on a
            // collision at the Space Center load, so the Guid is the identity that holds.
            if (pv.vesselID == recoverVesselGuid || pv.persistentId == recoverPid)
            {
                recoverRecoveredObserved = true;
                ParsekLog.Info(Tag, $"recover observed onVesselRecovered pid={pv.persistentId.ToString(CultureInfo.InvariantCulture)} "
                    + $"vessel={pv.vesselName} quick={Bool(quick)}");
            }
        }

        private void RejectRecover(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"recover rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
