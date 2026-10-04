using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for the two-phase <c>KscMarkerRecover</c> verb; the contract
    /// and every decision are on <see cref="TestCommandKscMarkerRecover"/>.
    ///
    /// <para><b>STOCK CONTROLS, IN A PLAYER'S ORDER.</b> The vessel's own
    /// <c>KSCVesselMarker</c> from <c>KSCVesselMarkers.fetch</c>: its <c>Marker</c> button's
    /// <c>onClick</c> (expand the panel) and then its <c>RecoverButton</c>'s <c>onClick</c>
    /// (<c>OnRecoverButtonInput</c> -> <c>Dismiss(Recover)</c>), and the
    /// <c>MissionRecoveryDialog</c>'s own dismissal (<c>Object.Destroy</c> of its game
    /// object, the whole body of its private <c>dismissDialog</c>). Stock's
    /// <c>KSCVesselMarkers.RecoverVessel</c> runs the recovery one frame later. The seam
    /// fires no event; it LISTENS to <c>onVesselRecovered</c> to read back that stock
    /// recovered the named vessel. The marker's fields are private, so they are read by
    /// reflection; a field that no longer resolves reads as "marker not wired".</para>
    ///
    /// <para><b>NO WEDGE GUARD.</b> No scene load happens; stock saves <c>persistent</c>
    /// in place.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private uint kscRecoverPid;
        private Guid kscRecoverVesselGuid;
        private string kscRecoverVesselName;
        private string kscRecoverSituation;
        private KscRecoverPhase kscRecoverPhase;
        private int kscRecoverPhaseStartFrame;
        private bool kscRecoverRecoveredObserved;
        private int kscRecoverRecoveredFrame;
        private bool kscRecoverQuick;
        private bool kscRecoverListenersAdded;

        private const BindingFlags KscMarkerFieldFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo KscMarkersListField =
            typeof(KSCVesselMarkers).GetField("markers", KscMarkerFieldFlags);
        private static readonly FieldInfo KscMarkerVesselField =
            typeof(KSCVesselMarker).GetField("v", KscMarkerFieldFlags);
        private static readonly FieldInfo KscMarkerButtonField =
            typeof(KSCVesselMarker).GetField("Marker", KscMarkerFieldFlags);
        private static readonly FieldInfo KscMarkerRecoverButtonField =
            typeof(KSCVesselMarker).GetField("RecoverButton", KscMarkerFieldFlags);
        private static readonly FieldInfo KscMarkerExpandedField =
            typeof(KSCVesselMarker).GetField("expanded", KscMarkerFieldFlags);
        private static readonly FieldInfo KscMarkerLockedField =
            typeof(KSCVesselMarker).GetField("locked", KscMarkerFieldFlags);
        // Set by OnPanelSetupComplete, the last step of the AnchoredDialog.Start that runs
        // CreateWindowContent (where the button listeners are added).
        private static readonly FieldInfo KscMarkerPanelCtrlsField =
            typeof(KSCVesselMarker).GetField("panelCtrls", KscMarkerFieldFlags);

        private void KscMarkerRecoverImpl(ParsedCommand cmd)
        {
            string pidArg = ArgOrNull(cmd, TestCommandKscMarkerRecover.PidKey);
            string argReason = TestCommandKscMarkerRecover.ParsePid(pidArg, out uint pid);
            if (argReason != null)
            {
                RejectKscRecover(argReason, $"pid={pidArg ?? string.Empty}");
                return;
            }

            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool isGhost = GhostMapPresence.IsGhostMapVessel(pid);
            Vessel target = FindRealVesselByPid(pid);
            bool onHome = target != null && target.mainBody != null
                && Planetarium.fetch != null && target.mainBody == Planetarium.fetch.Home;
            bool hasMarker = target != null && TestCommandKscMarkerRecover.HasStockMarker(
                target.LandedOrSplashed, onHome,
                target.vesselType == VesselType.DeployedSciencePart,
                target.vesselType == VesselType.DroppedPart);

            string gate = TestCommandKscMarkerRecover.DecideGate(scene, target != null, isGhost, hasMarker);
            if (gate != null)
            {
                string detail = $"pid={pid.ToString(CultureInfo.InvariantCulture)} scene={HighLogic.LoadedScene} "
                    + $"ghost={Bool(isGhost)} found={Bool(target != null)}";
                if (target != null)
                    detail += $" vessel={target.vesselName} situation={target.situation} "
                        + $"homeWorld={Bool(onHome)} type={target.vesselType}";
                RejectKscRecover(gate, detail);
                return;
            }

            kscRecoverPid = pid;
            kscRecoverVesselGuid = target.id;
            kscRecoverVesselName = target.vesselName;
            kscRecoverSituation = target.situation.ToString();
            kscRecoverRecoveredObserved = false;
            kscRecoverRecoveredFrame = -1;
            kscRecoverQuick = false;
            SetKscRecoverPhase(KscRecoverPhase.FindingMarker);
            AddKscRecoverListeners();
            ParsekLog.Info(Tag, $"kscrecover start pid={pid.ToString(CultureInfo.InvariantCulture)} "
                + $"vessel={target.vesselName} situation={kscRecoverSituation} - finding the vessel's Space Center marker");
            SetExecResult(PendingVerdict, null, null);
        }

        private void TryCompleteKscMarkerRecover(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds(TestCommandKscMarkerRecover.Verb);
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            bool gameLoaded = HighLogic.CurrentGame != null;
            int framesInPhase = Time.frameCount - kscRecoverPhaseStartFrame;

            KSCVesselMarker marker = null;
            int markerCount = 0;
            bool wired = false, locked = false, interactable = false;
            if (kscRecoverPhase == KscRecoverPhase.FindingMarker && scene == TestCommandScene.SpaceCenter)
            {
                marker = FindKscVesselMarker(kscRecoverPid, out markerCount);
                if (marker != null)
                {
                    Button recoverButton = KscMarkerRecoverButtonField?.GetValue(marker) as Button;
                    wired = KscMarkerButtonField?.GetValue(marker) is Button
                        && recoverButton != null
                        && KscMarkerExpandedField != null
                        && KscMarkerPanelCtrlsField?.GetValue(marker) != null;
                    locked = KscMarkerLockedField?.GetValue(marker) is bool l && l;
                    interactable = recoverButton != null && recoverButton.interactable;
                }
            }
            MissionRecoveryDialog summary = kscRecoverPhase == KscRecoverPhase.Recovering
                ? Object.FindObjectOfType<MissionRecoveryDialog>() : null;
            int framesSinceRecovered = kscRecoverRecoveredFrame < 0 ? 0 : Time.frameCount - kscRecoverRecoveredFrame;

            KscRecoverPollAction action = TestCommandKscMarkerRecover.DecidePoll(
                kscRecoverPhase, scene, gameLoaded, framesInPhase, marker != null, wired, locked,
                interactable, kscRecoverRecoveredObserved, framesSinceRecovered, summary != null, expired);

            CultureInfo ic = CultureInfo.InvariantCulture;
            string pidText = kscRecoverPid.ToString(ic);
            switch (action)
            {
                case KscRecoverPollAction.NotYet:
                    return;

                case KscRecoverPollAction.MarkerNotFound:
                    FinishKscRecover("REJECTED", null, TestCommandKscMarkerRecover.MarkerNotFoundReason,
                        $"scene={HighLogic.LoadedScene} fetch={Bool(KSCVesselMarkers.fetch != null)} "
                        + $"markers={Int(markerCount)} found={Bool(marker != null)} wired={Bool(wired)} "
                        + $"reflection={Bool(KscMarkersListField != null && KscMarkerVesselField != null)} "
                        + $"frames={Int(framesInPhase)}", elapsed);
                    return;

                case KscRecoverPollAction.ButtonLocked:
                    FinishKscRecover("REJECTED", null, TestCommandKscMarkerRecover.ButtonLockedReason,
                        $"locked={Bool(locked)} interactable={Bool(interactable)}", elapsed);
                    return;

                case KscRecoverPollAction.PressRecover:
                {
                    // A player's first click: the marker icon expands the panel that holds
                    // Recover. Stock ignores it while the marker is locked, read back here.
                    Button markerButton = (Button)KscMarkerButtonField.GetValue(marker);
                    markerButton.onClick.Invoke();
                    if (!(KscMarkerExpandedField.GetValue(marker) is bool expanded) || !expanded)
                    {
                        FinishKscRecover("REJECTED", null, TestCommandKscMarkerRecover.ButtonLockedReason,
                            "expanded=false - the marker refused to open", elapsed);
                        return;
                    }
                    ParsekLog.Info(Tag, TestCommandKscMarkerRecover.FormatPressedLine(
                        kscRecoverPid, kscRecoverVesselName, kscRecoverSituation, markerCount,
                        framesInPhase, Planetarium.GetUniversalTime()));
                    SetKscRecoverPhase(KscRecoverPhase.Recovering);
                    // The second click: OnRecoverButtonInput -> Dismiss(Recover); stock runs
                    // KSCVesselMarkers.RecoverVessel one frame later.
                    ((Button)KscMarkerRecoverButtonField.GetValue(marker)).onClick.Invoke();
                    return;
                }

                case KscRecoverPollAction.DismissSummary:
                    ParsekLog.Info(Tag, $"kscrecover dismiss MissionRecoveryDialog pid={pidText}");
                    // The whole body of the dialog's private dismissDialog (its Escape key path).
                    Object.Destroy(summary.gameObject);
                    return;

                case KscRecoverPollAction.NotRecovered:
                    FinishKscRecover("ERROR", null, TestCommandKscMarkerRecover.NotRecoveredReason,
                        $"the press fired no onVesselRecovered for the vessel within {Int(framesInPhase)} frames",
                        elapsed);
                    return;

                case KscRecoverPollAction.Ok:
                    FinishKscRecover("OK", TestCommandKscMarkerRecover.BuildOkPayload(
                        kscRecoverPid, kscRecoverVesselName, HighLogic.LoadedScene.ToString(), kscRecoverQuick),
                        null, null, elapsed);
                    return;

                case KscRecoverPollAction.ReturnedToMenu:
                    FinishKscRecover("ERROR", null, TestCommandKscMarkerRecover.ReturnedToMenuReason,
                        $"phase={kscRecoverPhase}", elapsed);
                    return;

                case KscRecoverPollAction.Timeout:
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, elapsed,
                        TestCommandKscMarkerRecover.TimeoutReason);
                    FinishKscRecover("ERROR", null, TestCommandKscMarkerRecover.TimeoutReason,
                        $"phase={kscRecoverPhase} scene={HighLogic.LoadedScene} "
                        + $"recovered={Bool(kscRecoverRecoveredObserved)}", elapsed);
                    return;
            }
        }

        private void FinishKscRecover(string verdict, List<KeyValuePair<string, string>> payload,
            string reason, string detail, double elapsed)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            uint pid = kscRecoverPid;
            string vesselName = kscRecoverVesselName;
            bool quick = kscRecoverQuick;
            ClearTwoPhase();
            if (verdict == "OK")
            {
                ParsekLog.Info(Tag, TestCommandKscMarkerRecover.FormatCompleteLine(
                    pid, vesselName, HighLogic.LoadedScene.ToString(), quick, elapsed));
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                return;
            }
            bool rejected = verdict == "REJECTED";
            string line = TestCommandKscMarkerRecover.FormatTerminalLine(rejected, reason, pid, detail, elapsed);
            if (rejected) ParsekLog.Warn(Tag, line);
            else ParsekLog.Error(Tag, line);
            EmitExecutedTerminal(id, seq, verb, verdict, null, reason, dequeueHead: true);
        }

        // The vessel's marker in stock's private list, by the vessel's persistentId.
        private static KSCVesselMarker FindKscVesselMarker(uint pid, out int count)
        {
            count = 0;
            KSCVesselMarkers host = KSCVesselMarkers.fetch;
            if (host == null || KscMarkersListField == null || KscMarkerVesselField == null)
                return null;
            if (!(KscMarkersListField.GetValue(host) is IList markers))
                return null;
            count = markers.Count;
            for (int i = 0; i < markers.Count; i++)
            {
                var m = markers[i] as KSCVesselMarker;
                if (m == null) continue;
                var v = KscMarkerVesselField.GetValue(m) as Vessel;
                if (v != null && v.persistentId == pid)
                    return m;
            }
            return null;
        }

        private void SetKscRecoverPhase(KscRecoverPhase phase)
        {
            if (kscRecoverPhase != phase)
                ParsekLog.Verbose(Tag, $"kscrecover phase {kscRecoverPhase} -> {phase} "
                    + $"pid={kscRecoverPid.ToString(CultureInfo.InvariantCulture)}");
            kscRecoverPhase = phase;
            kscRecoverPhaseStartFrame = Time.frameCount;
        }

        private void AddKscRecoverListeners()
        {
            if (kscRecoverListenersAdded) return;
            GameEvents.onVesselRecovered.Add(OnKscRecoverRecoveredObserved);
            kscRecoverListenersAdded = true;
        }

        // Idempotent; also called from ClearTwoPhase so a timeout or a completion throw
        // never leaves the listener attached.
        private void RemoveKscRecoverListeners()
        {
            if (!kscRecoverListenersAdded) return;
            GameEvents.onVesselRecovered.Remove(OnKscRecoverRecoveredObserved);
            kscRecoverListenersAdded = false;
        }

        // The bool is stock's quick flag; the marker path fires it FALSE, so it is
        // recorded, never waited on.
        private void OnKscRecoverRecoveredObserved(ProtoVessel pv, bool quick)
        {
            if (pv == null) return;
            if (pv.vesselID != kscRecoverVesselGuid && pv.persistentId != kscRecoverPid) return;
            if (!kscRecoverRecoveredObserved)
                kscRecoverRecoveredFrame = Time.frameCount;
            kscRecoverRecoveredObserved = true;
            kscRecoverQuick = quick;
            ParsekLog.Info(Tag, TestCommandKscMarkerRecover.FormatObservedLine(pv.persistentId, pv.vesselName, quick));
        }

        private void RejectKscRecover(string reason, string detail)
        {
            ParsekLog.Warn(Tag, $"kscrecover rejected reason={reason} {detail}");
            SetExecResult("REJECTED", null, reason);
        }
    }
}
