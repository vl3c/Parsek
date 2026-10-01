using System.Globalization;
using KSP.UI.Screens;
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
    /// <para><b>THE WEDGE GUARD.</b> Stock's recovery drives <c>HighLogic.LoadScene(SPACECENTER)</c>,
    /// which Parsek's scene-exit prefix turns into a merge modal when a merge decision is
    /// outstanding. The verb evaluates ExitToSpaceCenter's gate on the same live predicates
    /// and refuses with its <c>dialog-required variant=</c> before clicking.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private uint recoverPid;
        private System.Guid recoverVesselGuid;
        private string recoverVesselName;
        private bool recoverRequestObserved;
        private bool recoverRecoveredObserved;
        private bool recoverListenersAdded;

        private void RecoverImpl(ParsedCommand cmd)
        {
            string pidArg = ArgOrNull(cmd, TestCommandRecover.PidKey);
            string argReason = TestCommandRecover.ParsePid(pidArg, out uint pid);
            if (argReason != null)
            {
                RejectRecover(argReason, $"pid={pidArg ?? string.Empty}");
                return;
            }

            Vessel active = FlightGlobals.ActiveVessel;
            AltimeterSliderButtons altimeter = UnityEngine.Object.FindObjectOfType<AltimeterSliderButtons>();
            Button button = altimeter != null ? altimeter.vesselRecoveryButton : null;
            string clearToSave = active != null ? FlightGlobals.ClearToSave().ToString() : string.Empty;
            bool canLeave = HighLogic.CurrentGame != null
                && HighLogic.CurrentGame.Parameters.Flight.CanLeaveToSpaceCenter;

            string gate = TestCommandRecover.DecideGate(
                active != null, active != null ? active.persistentId : 0u, pid,
                button != null, button != null && button.interactable,
                clearToSave, canLeave);
            if (gate != null)
            {
                CultureInfo ic = CultureInfo.InvariantCulture;
                string detail = $"pid={pid.ToString(ic)}";
                if (active != null)
                    detail += $" activePid={active.persistentId.ToString(ic)} vessel={active.vesselName} "
                        + $"situation={active.situation} homeWorld={Bool(active.mainBody != null && active.mainBody.isHomeWorld)} "
                        + $"hSrfSpeed={active.horizontalSrfSpeed.ToString("F2", ic)} paused={Bool(FlightDriver.Pause)}";
                detail += $" clearToSave={clearToSave} canLeave={Bool(canLeave)}";
                RejectRecover(gate, detail);
                return;
            }

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
            if (exitGate != ExitGateDecision.Proceed)
            {
                string variant = TestCommandExitToSpaceCenter.VariantToken(
                    exitGate, activeTreeVariant, pendingTreeVariant);
                ParsekLog.Warn(Tag, $"recover rejected reason={TestCommandExitToSpaceCenter.DialogRequiredReason} "
                    + $"variant={variant} gate={exitGate} pid={pid.ToString(CultureInfo.InvariantCulture)} "
                    + "- the recovery's Space Center load would raise a merge modal no seam verb can answer");
                SetExecResult("REJECTED", null, TestCommandExitToSpaceCenter.RefusalMsg(variant));
                return;
            }

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
                SetExecResult("ERROR", null, TestCommandRecover.RequestNotFiredReason);
                return;
            }
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteRecover(double now)
        {
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

        private void OnRecoverSeamRecoveredObserved(ProtoVessel pv, bool fromTrackingStation)
        {
            if (pv == null) return;
            // Stock's VesselRetrieval matches by Guid; the pid can be regenerated on a
            // collision at the Space Center load, so the Guid is the identity that holds.
            if (pv.vesselID == recoverVesselGuid || pv.persistentId == recoverPid)
            {
                recoverRecoveredObserved = true;
                ParsekLog.Info(Tag, $"recover observed onVesselRecovered pid={pv.persistentId.ToString(CultureInfo.InvariantCulture)} "
                    + $"vessel={pv.vesselName} fromTrackingStation={Bool(fromTrackingStation)}");
            }
        }

        private void RejectRecover(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"recover rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
