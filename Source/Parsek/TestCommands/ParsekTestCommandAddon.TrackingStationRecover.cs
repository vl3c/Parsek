using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>TrackingStationRecover</c> verb; the
    /// contract and every decision are on <see cref="TestCommandTrackingStationRecover"/>.
    ///
    /// <para><b>STOCK CONTROLS, IN A PLAYER'S ORDER.</b> The Tracking Station building's own
    /// click (<c>SpaceCenterBuilding.OnLeftClick</c>), <c>SpaceTracking.SetVessel(v, true)</c>
    /// (what a list-row click calls), the <c>RecoverButton</c>'s own <c>onClick</c>, the
    /// confirm button's own <c>DialogGUIButton.OptionSelected</c> (its callback is stock's
    /// <c>OnRecoverConfirm</c> plus the popup's dismiss, so Parsek's
    /// <c>GhostTrackingRecoverPatch</c> prefix runs exactly as for a click), the
    /// <c>MissionRecoveryDialog</c>'s own dismissal (<c>Object.Destroy</c> of its game
    /// object, the whole body of the private <c>dismissDialog</c> its Escape key runs), and
    /// the <c>LeaveBtn</c>'s own <c>onClick</c>. The seam fires no event; it LISTENS to
    /// <c>onVesselRecovered</c> to read back that stock recovered the named vessel.</para>
    ///
    /// <para><b>NO WEDGE GUARD.</b> Parsek's scene-exit prefix (<c>SceneExitInterceptor</c>'s
    /// <c>HighLogic.LoadScene</c> patch) acts only when the loaded scene is FLIGHT; neither
    /// the Space Center -> Tracking Station load nor the way back passes it.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private uint tsRecoverPid;
        private Guid tsRecoverVesselGuid;
        private string tsRecoverVesselName;
        private TsRecoverPhase tsRecoverPhase;
        private int tsRecoverPhaseStartFrame;
        private int tsRecoverTsUpFrame = -1;
        private bool tsRecoverRecoveredObserved;
        private bool tsRecoverQuick;
        private bool tsRecoverLeaveForced;
        private int tsRecoverLastLockLogFrame;
        private bool tsRecoverIntroPressed;

        private static readonly System.Reflection.FieldInfo TutorialDialogDisplayField =
            typeof(TutorialScenario).GetField("dialogDisplay",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
        private string tsRecoverLeaveLocks;
        private bool tsRecoverListenersAdded;

        private void TrackingStationRecoverImpl(ParsedCommand cmd)
        {
            string pidArg = ArgOrNull(cmd, TestCommandTrackingStationRecover.PidKey);
            string argReason = TestCommandTrackingStationRecover.ParsePid(pidArg, out uint pid);
            if (argReason != null)
            {
                RejectTsRecover(argReason, $"pid={pidArg ?? string.Empty}");
                return;
            }

            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool isGhost = GhostMapPresence.IsGhostMapVessel(pid);
            Vessel target = FindRealVesselByPid(pid);
            TrackingStationBuilding building = scene == TestCommandScene.SpaceCenter
                ? Object.FindObjectOfType<TrackingStationBuilding>() : null;
            GameParameters.SpaceCenterParams sc = HighLogic.CurrentGame != null
                ? HighLogic.CurrentGame.Parameters.SpaceCenter : null;
            bool canGoIn = sc == null || sc.CanGoInTrackingStation;
            bool open = building != null && building.IsOpen();
            float damage = building != null ? building.GetStructureDamage() : 0f;
            bool recoverable = target != null && target.IsRecoverable;

            string gate = TestCommandTrackingStationRecover.DecideGate(
                scene, target != null, isGhost, recoverable,
                building != null, open, canGoIn, damage);
            if (gate != null)
            {
                CultureInfo ic = CultureInfo.InvariantCulture;
                string detail = $"pid={pid.ToString(ic)} scene={HighLogic.LoadedScene} ghost={Bool(isGhost)} "
                    + $"found={Bool(target != null)}";
                if (target != null)
                    detail += $" vessel={target.vesselName} situation={target.situation} "
                        + $"homeWorld={Bool(target.mainBody != null && target.mainBody.isHomeWorld)}";
                detail += $" building={Bool(building != null)} open={Bool(open)} allowed={Bool(canGoIn)} "
                    + $"damage={damage.ToString("F0", ic)}";
                RejectTsRecover(gate, detail);
                return;
            }

            tsRecoverPid = pid;
            tsRecoverVesselGuid = target.id;
            tsRecoverVesselName = target.vesselName;
            tsRecoverRecoveredObserved = false;
            tsRecoverQuick = false;
            tsRecoverLeaveForced = false;
            tsRecoverLeaveLocks = null;
            tsRecoverIntroPressed = false;
            tsRecoverTsUpFrame = -1;
            SetTsRecoverPhase(TsRecoverPhase.EnteringTrackingStation);
            AddTsRecoverListeners();

            ParsekLog.Info(Tag, TestCommandTrackingStationRecover.FormatEnterLine(
                pid, target.vesselName, target.situation.ToString(), Planetarium.GetUniversalTime()));
            // The mouse click on the building: stock's damage check, then EnterBuilding ->
            // TrackingStationBuilding.OnClicked, which saves persistent and loads TRACKSTATION.
            building.OnLeftClick();
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteTrackingStationRecover(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandTrackingStationRecover.Verb);
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool gameLoaded = HighLogic.CurrentGame != null;
            SpaceTracking ts = scene == TestCommandScene.TrackingStation ? SpaceTracking.Instance : null;
            bool tsUp = ts != null;
            if (tsUp && tsRecoverTsUpFrame < 0)
                tsRecoverTsUpFrame = Time.frameCount;

            int framesInPhase = tsRecoverPhase == TsRecoverPhase.EnteringTrackingStation
                ? (tsRecoverTsUpFrame < 0 ? 0 : Time.frameCount - tsRecoverTsUpFrame)
                : Time.frameCount - tsRecoverPhaseStartFrame;
            Vessel selected = tsUp ? ts.SelectedVessel : null;
            bool vesselSelected = selected != null && selected.persistentId == tsRecoverPid;
            bool recoverInteractable = tsUp && ts.RecoverButton != null && ts.RecoverButton.interactable;
            PopupDialog confirm = tsRecoverPhase == TsRecoverPhase.Confirming
                ? FindPopupByName(TestCommandTrackingStationRecover.ConfirmDialogName) : null;
            MissionRecoveryDialog summary = tsRecoverPhase == TsRecoverPhase.Recovered
                ? Object.FindObjectOfType<MissionRecoveryDialog>() : null;
            bool leaveInteractable = tsUp && ts.LeaveBtn != null && ts.LeaveBtn.interactable;

            bool introLockHeld = tsUp
                && InputLockManager.lockStack.ContainsKey(TestCommandTrackingStationRecover.IntroLockId);

            TsRecoverPollAction action = TestCommandTrackingStationRecover.DecidePoll(
                tsRecoverPhase, scene, gameLoaded, tsUp, framesInPhase, vesselSelected,
                recoverInteractable, confirm != null, summary != null, leaveInteractable, expired,
                introLockHeld);

            CultureInfo ic = CultureInfo.InvariantCulture;
            string pidText = tsRecoverPid.ToString(ic);
            switch (action)
            {
                case TsRecoverPollAction.NotYet:
                    if (tsRecoverPhase == TsRecoverPhase.Recovered
                        && Time.frameCount - tsRecoverLastLockLogFrame >= 30)
                        LogTsRecoverLocks("waiting-for-leave", ts);
                    return;

                case TsRecoverPollAction.DismissIntro:
                {
                    if (tsRecoverIntroPressed) return;
                    tsRecoverIntroPressed = true;
                    LogTsRecoverLocks("intro", ts);
                    DialogGUIButton introButton = FindNewGameIntroButton(out string introDetail);
                    if (introButton == null || DialogGuiButtonOptionSelectedMethod == null)
                    {
                        ParsekLog.Warn(Tag, $"tsrecover dismiss intro pid={pidText}: no intro button "
                            + $"({introDetail}) - the lock will run out the wait");
                        return;
                    }
                    ParsekLog.Info(Tag, $"tsrecover dismiss intro pid={pidText} - stock new-game "
                        + $"Tracking Station intro holds lock {TestCommandTrackingStationRecover.IntroLockId}; "
                        + $"pressing its button '{introButton.OptionText}' ({introDetail})");
                    // The page button's own callback: tsComplete = true, CloseTutorialWindow,
                    // SaveGame - what the player's click does.
                    DialogGuiButtonOptionSelectedMethod.Invoke(introButton, null);
                    return;
                }

                case TsRecoverPollAction.IntroNotDismissed:
                    FinishTsRecover("ERROR", null, TestCommandTrackingStationRecover.IntroNotDismissedReason,
                        $"lock={TestCommandTrackingStationRecover.IntroLockId} pressed={Bool(tsRecoverIntroPressed)}",
                        elapsed);
                    return;

                case TsRecoverPollAction.SelectVessel:
                {
                    if (tsRecoverPhase != TsRecoverPhase.Selecting)
                    {
                        LogTsRecoverLocks("arrived", ts);
                        SetTsRecoverPhase(TsRecoverPhase.Selecting);
                    }
                    Vessel v = FindRealVesselByPid(tsRecoverPid);
                    if (v == null)
                    {
                        ParsekLog.VerboseRateLimited(Tag, "tsrecover-select-missing",
                            $"tsrecover select: no vessel with pid={pidText} in the Tracking Station yet");
                        return;
                    }
                    // What a list-row click calls; stock drops a second SetVessel in one frame,
                    // so the read-back is the next poll's.
                    ts.SetVessel(v, true);
                    ParsekLog.Verbose(Tag, $"tsrecover select pid={pidText} vessel={v.vesselName} "
                        + $"framesInPhase={Int(framesInPhase)}");
                    return;
                }

                case TsRecoverPollAction.SelectFailed:
                    FinishTsRecover("ERROR", null, TestCommandTrackingStationRecover.SelectFailedReason,
                        $"selected={(selected != null ? selected.persistentId.ToString(ic) : "none")}", elapsed);
                    return;

                case TsRecoverPollAction.ButtonLocked:
                    FinishTsRecover("REJECTED", null, TestCommandTrackingStationRecover.ButtonLockedReason,
                        $"situation={selected.situation} recoverable={Bool(selected.IsRecoverable)} "
                        + $"canAbort={Bool(HighLogic.CurrentGame.Parameters.TrackingStation.CanAbortVessel)}",
                        elapsed);
                    return;

                case TsRecoverPollAction.PressRecover:
                    ParsekLog.Info(Tag, TestCommandTrackingStationRecover.FormatPressedLine(
                        tsRecoverPid, tsRecoverVesselName, Time.frameCount - tsRecoverTsUpFrame));
                    // Stock's BtnOnclick_RecoverSelectedVessel: lockUI + the confirm popup.
                    ts.RecoverButton.onClick.Invoke();
                    LogTsRecoverLocks("after-recover-press", ts);
                    SetTsRecoverPhase(TsRecoverPhase.Confirming);
                    return;

                case TsRecoverPollAction.ConfirmNotFound:
                    FinishTsRecover("ERROR", null, TestCommandTrackingStationRecover.ConfirmNotFoundReason,
                        "popup=" + TestCommandTrackingStationRecover.ConfirmDialogName.Replace(' ', '_'), elapsed);
                    return;

                case TsRecoverPollAction.AnswerConfirm:
                {
                    DialogGUIButton button = FindTsRecoverConfirmButton(confirm);
                    if (button == null || DialogGuiButtonOptionSelectedMethod == null)
                    {
                        FinishTsRecover("ERROR", null, TestCommandTrackingStationRecover.ConfirmNotFoundReason,
                            "button=" + TestCommandTrackingStationRecover.ConfirmCallbackMethodName, elapsed);
                        return;
                    }
                    ParsekLog.Info(Tag, $"tsrecover confirm pid={pidText} vessel={tsRecoverVesselName} "
                        + $"button={button.OptionText}");
                    // The button's own callback: OnRecoverConfirm (onVesselRecovered fires
                    // synchronously inside it) and then the popup's dismiss.
                    DialogGuiButtonOptionSelectedMethod.Invoke(button, null);
                    LogTsRecoverLocks("after-confirm", ts);
                    if (!tsRecoverRecoveredObserved)
                    {
                        FinishTsRecover("ERROR", null, TestCommandTrackingStationRecover.NotRecoveredReason,
                            "the confirm fired no onVesselRecovered for the vessel", elapsed);
                        return;
                    }
                    SetTsRecoverPhase(TsRecoverPhase.Recovered);
                    return;
                }

                case TsRecoverPollAction.DismissSummary:
                    ParsekLog.Info(Tag, $"tsrecover dismiss MissionRecoveryDialog pid={pidText}");
                    // The whole body of the dialog's private dismissDialog (its Escape key path);
                    // OnDestroy fires onGUIRecoveryDialogDespawn, which unlocks the TS buttons.
                    Object.Destroy(summary.gameObject);
                    return;

                case TsRecoverPollAction.PressLeave:
                    ParsekLog.Info(Tag, $"tsrecover leave pid={pidText} - pressing the Tracking Station Leave button");
                    // Stock's BtnOnClick_LeaveTrackingStation: save, load SPACECENTER.
                    ts.LeaveBtn.onClick.Invoke();
                    SetTsRecoverPhase(TsRecoverPhase.Leaving);
                    return;

                case TsRecoverPollAction.PressLeaveForced:
                    tsRecoverLeaveForced = true;
                    tsRecoverLeaveLocks = TestCommandTrackingStationRecover
                        .DescribeTrackingStationLocks(InputLockManager.lockStack);
                    ParsekLog.Warn(Tag, $"tsrecover leave forced pid={pidText} - Leave stayed locked "
                        + $"{framesInPhase.ToString(CultureInfo.InvariantCulture)} frames after the recovery; "
                        + $"TRACKINGSTATION_UI locks={tsRecoverLeaveLocks} lockMask=0x"
                        + InputLockManager.lockMask.ToString("X", CultureInfo.InvariantCulture)
                        + " - invoking the Leave button's click handler");
                    ts.LeaveBtn.onClick.Invoke();
                    SetTsRecoverPhase(TsRecoverPhase.Leaving);
                    return;

                case TsRecoverPollAction.Ok:
                    FinishTsRecover("OK", TestCommandTrackingStationRecover.BuildOkPayload(
                        tsRecoverPid, tsRecoverVesselName, HighLogic.LoadedScene.ToString(), tsRecoverQuick,
                        tsRecoverLeaveForced, tsRecoverLeaveLocks),
                        null, null, elapsed);
                    return;

                case TsRecoverPollAction.ReturnedToMenu:
                    FinishTsRecover("ERROR", null, TestCommandTrackingStationRecover.ReturnedToMenuReason,
                        $"phase={tsRecoverPhase}", elapsed);
                    return;

                case TsRecoverPollAction.Timeout:
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, elapsed,
                        TestCommandTrackingStationRecover.TimeoutReason);
                    FinishTsRecover("ERROR", null, TestCommandTrackingStationRecover.TimeoutReason,
                        $"phase={tsRecoverPhase} scene={HighLogic.LoadedScene} tsUp={Bool(tsUp)} "
                        + $"selected={Bool(vesselSelected)} recovered={Bool(tsRecoverRecoveredObserved)} "
                        + $"leave={Bool(leaveInteractable)}", elapsed);
                    return;
            }
        }

        private void FinishTsRecover(string verdict, List<KeyValuePair<string, string>> payload,
            string reason, string detail, double elapsed)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            uint pid = tsRecoverPid;
            string vesselName = tsRecoverVesselName;
            bool quick = tsRecoverQuick;
            bool leaveForced = tsRecoverLeaveForced;
            ClearTwoPhase();
            CultureInfo ic = CultureInfo.InvariantCulture;
            if (verdict == "OK")
            {
                ParsekLog.Info(Tag, TestCommandTrackingStationRecover.FormatCompleteLine(
                    pid, vesselName, HighLogic.LoadedScene.ToString(), quick, elapsed, leaveForced));
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                return;
            }
            string line = $"tsrecover {(verdict == "REJECTED" ? "rejected" : "error")} reason={reason} "
                + $"pid={pid.ToString(ic)} {detail} elapsed={elapsed.ToString("F1", ic)}s";
            if (verdict == "REJECTED") ParsekLog.Warn(Tag, line);
            else ParsekLog.Error(Tag, line);
            EmitExecutedTerminal(id, seq, verb, verdict, null, reason, dequeueHead: true);
        }

        // Debug snapshot for the Leave lock: which control locks exist (id:mask), which of
        // them cover TRACKINGSTATION_UI, whether the confirm popup and the recovery summary
        // still exist, and Leave's own state. Bounded: four call sites, the waiting one at most
        // every 30 frames over LeaveWaitFrames.
        private void LogTsRecoverLocks(string at, SpaceTracking ts)
        {
            tsRecoverLastLockLogFrame = Time.frameCount;
            CultureInfo ic = CultureInfo.InvariantCulture;
            var all = new List<string>();
            foreach (var kv in InputLockManager.lockStack)
                all.Add(kv.Key + ":0x" + kv.Value.ToString("X", ic));
            all.Sort(System.StringComparer.Ordinal);
            bool popup = FindPopupByName(TestCommandTrackingStationRecover.ConfirmDialogName) != null;
            bool summary = Object.FindObjectOfType<MissionRecoveryDialog>() != null;
            bool leave = ts != null && ts.LeaveBtn != null && ts.LeaveBtn.interactable;
            ParsekLog.Info(Tag, $"tsrecover locks at={at} pid={tsRecoverPid.ToString(ic)} "
                + $"tsUiLocks={TestCommandTrackingStationRecover.DescribeTrackingStationLocks(InputLockManager.lockStack)} "
                + $"lockMask=0x{InputLockManager.lockMask.ToString("X", ic)} "
                + $"all=[{string.Join(",", all.ToArray())}] recoverPopup={Bool(popup)} "
                + $"summary={Bool(summary)} leave={Bool(leave)} frame={Time.frameCount.ToString(ic)}");
        }

        // The intro page's button: the one DialogGUIButton in ScenarioNewGameIntro's tutorial
        // window whose callback was built by ScenarioNewGameIntro (its welcome page closure).
        private static DialogGUIButton FindNewGameIntroButton(out string detail)
        {
            var intro = Object.FindObjectOfType<ScenarioNewGameIntro>();
            if (intro == null) { detail = "no ScenarioNewGameIntro"; return null; }
            if (TutorialDialogDisplayField == null) { detail = "no dialogDisplay field"; return null; }
            var popup = TutorialDialogDisplayField.GetValue(intro) as PopupDialog;
            if (popup == null) { detail = "no intro window"; return null; }
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            DialogGUIButton only = buttons.Count == 1 ? buttons[0] : null;
            for (int i = 0; i < buttons.Count; i++)
            {
                Callback cb = buttons[i].onOptionSelected;
                if (cb == null) continue;
                foreach (System.Delegate d in cb.GetInvocationList())
                {
                    System.Type owner = d.Method.DeclaringType;
                    while (owner != null && owner.DeclaringType != null) owner = owner.DeclaringType;
                    if (owner == typeof(ScenarioNewGameIntro))
                    {
                        detail = "buttons=" + buttons.Count.ToString(CultureInfo.InvariantCulture) + " by-owner";
                        return buttons[i];
                    }
                }
            }
            detail = "buttons=" + buttons.Count.ToString(CultureInfo.InvariantCulture)
                + (only != null ? " single" : " no-owner-match");
            return only;
        }

        private void SetTsRecoverPhase(TsRecoverPhase phase)
        {
            if (tsRecoverPhase != phase)
                ParsekLog.Verbose(Tag, $"tsrecover phase {tsRecoverPhase} -> {phase} "
                    + $"pid={tsRecoverPid.ToString(CultureInfo.InvariantCulture)}");
            tsRecoverPhase = phase;
            tsRecoverPhaseStartFrame = Time.frameCount;
        }

        // A real (non-ghost) vessel by persistentId. FlightGlobals.Vessels holds the unloaded
        // vessels of the flight state at the Space Center and in the Tracking Station.
        private static Vessel FindRealVesselByPid(uint pid)
        {
            if (FlightGlobals.fetch == null || FlightGlobals.Vessels == null) return null;
            List<Vessel> vessels = FlightGlobals.Vessels;
            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null || v.persistentId != pid) continue;
                if (GhostMapPresence.IsGhostMapVessel(v.persistentId)) continue;
                return v;
            }
            return null;
        }

        // The confirm button, identified by the stock method its callback runs first rather
        // than by position or by its localized label.
        private static DialogGUIButton FindTsRecoverConfirmButton(PopupDialog popup)
        {
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            for (int i = 0; i < buttons.Count; i++)
            {
                DialogGUIButton b = buttons[i];
                if (b == null || b.onOptionSelected == null) continue;
                Delegate[] list = b.onOptionSelected.GetInvocationList();
                for (int j = 0; j < list.Length; j++)
                {
                    if (list[j] != null && list[j].Method != null
                        && list[j].Method.Name == TestCommandTrackingStationRecover.ConfirmCallbackMethodName)
                        return b;
                }
            }
            return null;
        }

        private void AddTsRecoverListeners()
        {
            if (tsRecoverListenersAdded) return;
            GameEvents.onVesselRecovered.Add(OnTsRecoverRecoveredObserved);
            tsRecoverListenersAdded = true;
        }

        // Idempotent; also called from ClearTwoPhase so a timeout or a completion throw
        // never leaves the listener attached.
        private void RemoveTsRecoverListeners()
        {
            if (!tsRecoverListenersAdded) return;
            GameEvents.onVesselRecovered.Remove(OnTsRecoverRecoveredObserved);
            tsRecoverListenersAdded = false;
        }

        // The bool is stock's `quick` flag (VesselRecovery skips the summary on true); the
        // Tracking Station path fires it FALSE, so it is recorded, never waited on.
        private void OnTsRecoverRecoveredObserved(ProtoVessel pv, bool quick)
        {
            if (pv == null) return;
            if (pv.vesselID == tsRecoverVesselGuid || pv.persistentId == tsRecoverPid)
            {
                tsRecoverRecoveredObserved = true;
                tsRecoverQuick = quick;
                ParsekLog.Info(Tag, $"tsrecover observed onVesselRecovered pid={pv.persistentId.ToString(CultureInfo.InvariantCulture)} "
                    + $"vessel={pv.vesselName} quick={Bool(quick)}");
            }
        }

        private void RejectTsRecover(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"tsrecover rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
