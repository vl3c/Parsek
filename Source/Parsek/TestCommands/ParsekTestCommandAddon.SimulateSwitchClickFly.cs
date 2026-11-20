using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek.TestCommands
{
    /// <summary>
    /// The thin Unity applier for <c>SimulateStockSwitchClick site=ts|ksc</c>, two-phase; the
    /// contract and every decision are on the FlySites partial of
    /// <see cref="TestCommandSimulateSwitchClick"/>.
    ///
    /// <para><b>STOCK CONTROLS, IN A PLAYER'S ORDER.</b> Tracking Station:
    /// <c>SpaceTracking.SetVessel(v, true)</c> (what a list-row click calls), then the public
    /// <c>FlyButton</c>'s own <c>onClick</c> (<c>BtnOnClick_FlySelectedVessel</c> ->
    /// <c>FlyVessel</c>, patched). Space Center: the vessel's <c>KSCVesselMarker</c>, its
    /// <c>Marker</c> button's <c>onClick</c> (expand) and its <c>FlyButton</c>'s <c>onClick</c>
    /// (<c>Dismiss(Fly)</c> -> <c>KSCVesselMarkers.FlyVessel</c> one frame later, patched).
    /// The seam arms no marker and calls no <c>StartAndFocusVessel</c>; it LISTENS to
    /// <c>onGameSceneLoadRequested</c> and reads the intent the patch armed there, before
    /// stock's <c>ScenarioRunner</c> tears the scenario down (GameEvents fire the newest
    /// listener first, and this one is added after the scene's own).</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private SwitchClickSite flyClickSite;
        private uint flyClickPid;
        private string flyClickName;
        private SwitchClickFlyPhase flyClickPhase;
        private int flyClickPhaseStartFrame;
        private int flyClickSettledSinceFrame = -1;
        private bool flyClickSceneLoadRequested;
        private bool flyClickIntentArmed;
        private Guid flyClickIntentId;
        private bool flyClickListenerAdded;

        private static readonly FieldInfo KscMarkerFlyButtonField =
            typeof(KSCVesselMarker).GetField("FlyButton", KscMarkerFieldFlags);

        // Called by SimulateStockSwitchClickImpl once the site and the selector parsed.
        private void StartSwitchClickFly(SwitchClickSite site, SwitchClickTargetDecision selector)
        {
            Vessel target = ResolveSwitchClickTarget(selector, out int liveMatches, out int ghostMatches);
            SwitchClickTargetResolution resolution =
                TestCommandSimulateSwitchClick.ClassifyResolution(liveMatches, ghostMatches);
            if (target == null && resolution == SwitchClickTargetResolution.Resolved)
                resolution = SwitchClickTargetResolution.NotFound;
            if (resolution != SwitchClickTargetResolution.Resolved)
            {
                string resolveMsg = TestCommandSimulateSwitchClick.ResolutionMsg(resolution, selector, liveMatches);
                ParsekLog.Warn(Tag,
                    $"switchclick rejected reason={resolveMsg} site={TestCommandSimulateSwitchClick.SiteToken(site)} " +
                    $"resolution={resolution} liveMatches={Int(liveMatches)} ghostMatches={Int(ghostMatches)} " +
                    $"vesselsScanned={(FlightGlobals.Vessels != null ? Int(FlightGlobals.Vessels.Count) : "0")}");
                SetExecResult("REJECTED", null, resolveMsg);
                return;
            }

            // The Re-Fly switch block (design section 2.12 Rule 3), with the site's own path
            // token: both stock Fly patches refuse the same click with the same gate.
            string path = site == SwitchClickSite.Ksc
                ? ReFlySwitchGate.PathKscMarkerFly
                : ReFlySwitchGate.PathTrackingStationFly;
            if (ReFlySwitchGate.IsRefused(target.persistentId, path, true, out string reFlyRefusal))
            {
                string refusedMsg = "refly-switch-refused:" + reFlyRefusal;
                ParsekLog.Warn(Tag,
                    $"switchclick refused reason={refusedMsg} site={TestCommandSimulateSwitchClick.SiteToken(site)} " +
                    $"targetPid={target.persistentId}");
                SetExecResult("REJECTED", null, refusedMsg);
                return;
            }

            bool scenarioReady = ParsekScenario.Instance != null;
            bool canFlyVessel = ReadCanFlyVessel();
            bool owned = ReadDiscoveryOwned(target);
            bool onHome = target.mainBody != null && Planetarium.fetch != null
                && target.mainBody == Planetarium.fetch.Home;
            bool hasMarker = TestCommandKscMarkerRecover.HasStockMarker(
                target.LandedOrSplashed, onHome,
                target.vesselType == VesselType.DeployedSciencePart,
                target.vesselType == VesselType.DroppedPart);
            string gate = TestCommandSimulateSwitchClick.DecideFlyGate(
                site, scenarioReady, canFlyVessel, owned, hasMarker);
            ParsekLog.Info(Tag,
                $"switchclick fly gate site={TestCommandSimulateSwitchClick.SiteToken(site)} " +
                $"targetPid={target.persistentId} targetName={target.vesselName ?? string.Empty} " +
                $"situation={target.situation} scenarioReady={Bool(scenarioReady)} " +
                $"canFlyVessel={Bool(canFlyVessel)} owned={Bool(owned)} homeWorld={Bool(onHome)} " +
                $"kscMarker={Bool(hasMarker)} decision={gate ?? "proceed"}");
            if (gate != null)
            {
                ParsekLog.Warn(Tag,
                    $"switchclick refused reason={gate} site={TestCommandSimulateSwitchClick.SiteToken(site)} " +
                    $"targetPid={target.persistentId}");
                SetExecResult("REJECTED", null, gate);
                return;
            }

            flyClickSite = site;
            flyClickPid = target.persistentId;
            flyClickName = target.vesselName;
            flyClickSceneLoadRequested = false;
            flyClickIntentArmed = false;
            flyClickIntentId = Guid.Empty;
            flyClickSettledSinceFrame = -1;
            SetFlyClickPhase(SwitchClickFlyPhase.AwaitingControl);
            AddFlyClickListener();
            ParsekLog.Info(Tag,
                $"switchclick fly start site={TestCommandSimulateSwitchClick.SiteToken(site)} " +
                $"targetPid={flyClickPid} targetName={flyClickName ?? string.Empty} " +
                "- pressing the stock Fly control; the answer waits for the FLIGHT load and the intent consume");
            SetExecResult(PendingVerdict, null, null);
        }

        // What one completion poll sampled; the decision is the pure DecideFlyPoll.
        private struct FlyClickSample
        {
            public TestCommandScene Scene;
            public bool GameLoaded;
            public int FramesInPhase;
            public SpaceTracking Ts;
            public KSCVesselMarker Marker;
            public int MarkerCount;
            public bool ControlPresent;
            public bool Selected;
            public bool FlyInteractable;
            public bool Settled;
            public bool IntentStillArmed;
            public bool ActiveIsTarget;
            public Vessel Active;
        }

        private void TryCompleteSwitchClickFly(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("SimulateStockSwitchClick");
            bool expired = DeferralBudget.ShouldTimeout(completionStartedAt, now, budget);
            var sample = new FlyClickSample
            {
                Scene = MapScene(HighLogic.LoadedScene),
                GameLoaded = HighLogic.CurrentGame != null,
                FramesInPhase = Time.frameCount - flyClickPhaseStartFrame,
            };
            if (flyClickPhase == SwitchClickFlyPhase.AwaitingControl || flyClickPhase == SwitchClickFlyPhase.Selecting)
                SampleFlyControl(ref sample);
            if (flyClickPhase == SwitchClickFlyPhase.Loading && sample.Scene == TestCommandScene.Flight)
                SampleFlyLoading(ref sample);

            SwitchClickFlyPollAction action = TestCommandSimulateSwitchClick.DecideFlyPoll(
                flyClickPhase, flyClickSite, sample.Scene, sample.GameLoaded, sample.ControlPresent,
                sample.Selected, sample.FlyInteractable, flyClickSceneLoadRequested, flyClickIntentArmed,
                sample.Settled, sample.IntentStillArmed, sample.ActiveIsTarget, sample.FramesInPhase, expired);
            if (action != SwitchClickFlyPollAction.NotYet)
                ApplySwitchClickFlyAction(action, ref sample, elapsed);
        }

        // The site's stock control in its own scene: SpaceTracking and its Fly button, or the
        // target's KSC marker (wired once its panel setup completed) and its Fly button.
        private void SampleFlyControl(ref FlyClickSample sample)
        {
            if (flyClickSite == SwitchClickSite.TrackingStation && sample.Scene == TestCommandScene.TrackingStation)
            {
                SpaceTracking ts = SpaceTracking.Instance;
                sample.Ts = ts;
                sample.ControlPresent = ts != null && ts.FlyButton != null;
                Vessel sel = ts != null ? ts.SelectedVessel : null;
                sample.Selected = sel != null && sel.persistentId == flyClickPid;
                sample.FlyInteractable = sample.ControlPresent && ts.FlyButton.interactable;
                return;
            }
            if (flyClickSite != SwitchClickSite.Ksc || sample.Scene != TestCommandScene.SpaceCenter)
                return;
            KSCVesselMarker marker = FindKscVesselMarker(flyClickPid, out int markerCount);
            sample.Marker = marker;
            sample.MarkerCount = markerCount;
            if (marker == null)
                return;
            Button fly = KscMarkerFlyButtonField?.GetValue(marker) as Button;
            sample.ControlPresent = fly != null
                && KscMarkerButtonField?.GetValue(marker) is Button
                && KscMarkerExpandedField != null
                && KscMarkerPanelCtrlsField?.GetValue(marker) != null;
            bool locked = KscMarkerLockedField?.GetValue(marker) is bool l && l;
            sample.FlyInteractable = sample.ControlPresent && fly.interactable && !locked;
        }

        // The NEW flight: its ParsekFlight saw onFlightReady (there was no flight before the
        // press), the active vessel unpacked, and whether the intent the press armed is still
        // armed. FramesInPhase becomes frames since the flight settled.
        private void SampleFlyLoading(ref FlyClickSample sample)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            bool ready = TestCommandLoadGame.IsFlightReadyForThisLoad(
                flight != null ? flight.GetInstanceID() : 0, 0, flight != null && flight.FlightReadyObserved);
            Vessel active = ready ? FlightGlobals.ActiveVessel : null;
            sample.Active = active;
            sample.Settled = active != null && active.loaded && !active.packed;
            if (!sample.Settled) flyClickSettledSinceFrame = -1;
            else if (flyClickSettledSinceFrame < 0) flyClickSettledSinceFrame = Time.frameCount;
            StockActionIntentMarker armed = ParsekScenario.Instance != null
                ? ParsekScenario.Instance.CurrentStockActionIntent : null;
            sample.IntentStillArmed = armed != null && armed.IntentId == flyClickIntentId;
            sample.ActiveIsTarget = active != null && active.persistentId == flyClickPid;
            if (sample.Settled)
                sample.FramesInPhase = Time.frameCount - flyClickSettledSinceFrame;
        }

        private void ApplySwitchClickFlyAction(SwitchClickFlyPollAction action, ref FlyClickSample sample,
            double elapsed)
        {
            string siteToken = TestCommandSimulateSwitchClick.SiteToken(flyClickSite);
            uint activePid = sample.Active != null ? sample.Active.persistentId : 0u;
            switch (action)
            {
                case SwitchClickFlyPollAction.ControlMissing:
                    FinishSwitchClickFly("REJECTED", TestCommandSimulateSwitchClick.FlyControlMissingReason,
                        $"scene={HighLogic.LoadedScene} spaceTracking={Bool(SpaceTracking.Instance != null)} " +
                        $"kscMarkers={Bool(KSCVesselMarkers.fetch != null)} markers={Int(sample.MarkerCount)} " +
                        $"frames={Int(sample.FramesInPhase)}", elapsed, 0u);
                    return;

                case SwitchClickFlyPollAction.Select:
                    SelectSwitchClickFlyTarget(sample.Ts, sample.FramesInPhase);
                    return;

                case SwitchClickFlyPollAction.SelectFailed:
                    Vessel sel = sample.Ts != null ? sample.Ts.SelectedVessel : null;
                    FinishSwitchClickFly("ERROR", TestCommandSimulateSwitchClick.FlySelectFailedReason,
                        $"selected={(sel != null ? sel.persistentId.ToString(CultureInfo.InvariantCulture) : "none")}",
                        elapsed, 0u);
                    return;

                case SwitchClickFlyPollAction.ButtonLocked:
                    FinishSwitchClickFly("REJECTED", TestCommandSimulateSwitchClick.FlyButtonLockedReason,
                        $"control={(flyClickSite == SwitchClickSite.Ksc ? "ksc-marker-fly" : "trackingstation-fly")}",
                        elapsed, 0u);
                    return;

                case SwitchClickFlyPollAction.Press:
                    PressSwitchClickFly(sample.Ts, sample.Marker, sample.FramesInPhase, elapsed);
                    return;

                case SwitchClickFlyPollAction.BeginLoading:
                    ParsekLog.Info(Tag,
                        $"switchclick fly load requested site={siteToken} targetPid={flyClickPid} " +
                        $"intentId={flyClickIntentId:D} - waiting for the FLIGHT load and the consume");
                    SetFlyClickPhase(SwitchClickFlyPhase.Loading);
                    return;

                case SwitchClickFlyPollAction.NoSceneLoad:
                    FinishSwitchClickFly("ERROR", TestCommandSimulateSwitchClick.FlyNoSceneLoadReason,
                        $"frames={Int(sample.FramesInPhase)} scene={HighLogic.LoadedScene} - stock refused the Fly " +
                        "(a refused Re-Fly gate, a ghost block or a non-Owned vessel posts its own message)",
                        elapsed, 0u);
                    return;

                case SwitchClickFlyPollAction.IntentNotArmed:
                    FinishSwitchClickFly("ERROR", TestCommandSimulateSwitchClick.FlyIntentNotArmedReason,
                        $"expectedAction={TestCommandSimulateSwitchClick.ExpectedIntentAction(flyClickSite)} " +
                        "- stock requested the FLIGHT load but the site's arming patch armed no intent for the target",
                        elapsed, 0u);
                    return;

                case SwitchClickFlyPollAction.Ok:
                    FinishSwitchClickFly("OK", null, $"situation={sample.Active.situation}", elapsed, activePid);
                    return;

                case SwitchClickFlyPollAction.WrongVessel:
                    FinishSwitchClickFly("ERROR", TestCommandSimulateSwitchClick.FlyWrongVesselReason,
                        $"activeVesselPid={activePid} " +
                        $"activeName={(sample.Active != null ? sample.Active.vesselName : string.Empty)}",
                        elapsed, activePid);
                    return;

                case SwitchClickFlyPollAction.IntentNotConsumed:
                    FinishSwitchClickFly("ERROR", TestCommandSimulateSwitchClick.FlyIntentNotConsumedReason,
                        $"intentId={flyClickIntentId:D} framesSinceSettled={Int(sample.FramesInPhase)} " +
                        $"restorePending={ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady}",
                        elapsed, activePid);
                    return;

                case SwitchClickFlyPollAction.ReturnedToMenu:
                    FinishSwitchClickFly("ERROR", TestCommandSimulateSwitchClick.FlyReturnedToMenuReason,
                        $"phase={flyClickPhase}", elapsed, 0u);
                    return;

                case SwitchClickFlyPollAction.Timeout:
                    TestCommandDiagnostics.Timeout(completionId, completionVerb, elapsed,
                        TestCommandSimulateSwitchClick.FlyTimeoutReason);
                    FinishSwitchClickFly("ERROR", TestCommandSimulateSwitchClick.FlyTimeoutReason,
                        $"phase={flyClickPhase} scene={HighLogic.LoadedScene} loadRequested={Bool(flyClickSceneLoadRequested)} " +
                        $"intentArmed={Bool(flyClickIntentArmed)} settled={Bool(sample.Settled)} " +
                        $"intentStillArmed={Bool(sample.IntentStillArmed)}", elapsed, 0u);
                    return;
            }
        }

        // What a list-row click calls; stock drops a second SetVessel in one frame, so the
        // read-back is the next poll's.
        private void SelectSwitchClickFlyTarget(SpaceTracking ts, int framesInPhase)
        {
            Vessel v = FindRealVesselByPid(flyClickPid);
            if (v == null || ts == null)
            {
                ParsekLog.VerboseRateLimited(Tag, "switchclick-fly-select-missing",
                    $"switchclick fly select: no vessel with pid={flyClickPid} in the Tracking Station yet");
                return;
            }
            if (flyClickPhase != SwitchClickFlyPhase.Selecting)
                SetFlyClickPhase(SwitchClickFlyPhase.Selecting);
            ts.SetVessel(v, true);
            ParsekLog.Verbose(Tag, $"switchclick fly select pid={flyClickPid} vessel={v.vesselName} " +
                $"framesInPhase={Int(framesInPhase)}");
        }

        private void PressSwitchClickFly(SpaceTracking ts, KSCVesselMarker marker, int framesWaited, double elapsed)
        {
            ParsekLog.Info(Tag, TestCommandSimulateSwitchClick.FormatFlyPressedLine(
                flyClickSite, flyClickPid, flyClickName, framesWaited));
            SetFlyClickPhase(SwitchClickFlyPhase.Pressed);
            if (flyClickSite == SwitchClickSite.TrackingStation)
            {
                // BtnOnClick_FlySelectedVessel -> FlyVessel(selectedVessel): the patched
                // Prefixes arm the intent and strip the ghosts, then stock saves and requests
                // the FLIGHT load, all inside this call.
                ts.FlyButton.onClick.Invoke();
                return;
            }

            // A player's first click: the marker icon expands the panel that holds Fly. It
            // toggles, so it is clicked only while collapsed.
            bool alreadyExpanded = KscMarkerExpandedField.GetValue(marker) is bool open && open;
            if (!alreadyExpanded)
                ((Button)KscMarkerButtonField.GetValue(marker)).onClick.Invoke();
            if (!(KscMarkerExpandedField.GetValue(marker) is bool expanded) || !expanded)
            {
                FinishSwitchClickFly("REJECTED", TestCommandSimulateSwitchClick.FlyButtonLockedReason,
                    "expanded=false - the marker refused to open", elapsed, 0u);
                return;
            }
            // OnFlyButtonInput -> Dismiss(Fly); stock runs KSCVesselMarkers.FlyVessel (patched)
            // one frame later.
            ((Button)KscMarkerFlyButtonField.GetValue(marker)).onClick.Invoke();
        }

        private void FinishSwitchClickFly(string verdict, string reason, string detail, double elapsed,
            uint activePid)
        {
            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            SwitchClickSite site = flyClickSite;
            uint pid = flyClickPid;
            string name = flyClickName;
            Guid intentId = flyClickIntentId;
            ClearTwoPhase();
            string siteToken = TestCommandSimulateSwitchClick.SiteToken(site);
            string el = elapsed.ToString("F1", CultureInfo.InvariantCulture);
            List<KeyValuePair<string, string>> payload = null;
            if (verdict != "REJECTED")
                payload = TestCommandSimulateSwitchClick.BuildPayload(siteToken, pid, name, intentId,
                    TestCommandSimulateSwitchClick.FlyControlRoute, activePid,
                    activePid != 0u && activePid == pid);
            if (verdict == "OK")
            {
                ParsekLog.Info(Tag,
                    $"switchclick complete site={siteToken} targetPid={pid} targetName={name ?? string.Empty} " +
                    $"intentId={intentId:D} route={TestCommandSimulateSwitchClick.FlyControlRoute} " +
                    $"activeVesselPid={activePid} switched=true {detail} elapsed={el}s " +
                    "- consume outcome (segment started / refused) is in the TryConsumeStockActionIntent lines");
                EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                return;
            }
            string line = $"switchclick {(verdict == "REJECTED" ? "refused" : "failed")} reason={reason} " +
                $"site={siteToken} targetPid={pid} {detail} elapsed={el}s";
            if (verdict == "REJECTED") ParsekLog.Warn(Tag, line);
            else ParsekLog.Error(Tag, line);
            EmitExecutedTerminal(id, seq, verb, verdict, payload, reason, dequeueHead: true);
        }

        private void SetFlyClickPhase(SwitchClickFlyPhase phase)
        {
            if (flyClickPhase != phase)
                ParsekLog.Verbose(Tag, $"switchclick fly phase {flyClickPhase} -> {phase} pid={flyClickPid}");
            flyClickPhase = phase;
            flyClickPhaseStartFrame = Time.frameCount;
        }

        private void AddFlyClickListener()
        {
            if (flyClickListenerAdded) return;
            GameEvents.onGameSceneLoadRequested.Add(OnFlyClickSceneLoadRequested);
            flyClickListenerAdded = true;
        }

        // Idempotent; also called from ClearTwoPhase so a timeout or a completion throw never
        // leaves the listener attached.
        private void RemoveFlyClickListener()
        {
            if (!flyClickListenerAdded) return;
            GameEvents.onGameSceneLoadRequested.Remove(OnFlyClickSceneLoadRequested);
            flyClickListenerAdded = false;
        }

        // Fires inside the stock Fly's StartAndFocusVessel, after the patched Prefix armed the
        // intent and stock saved it into persistent, and before ScenarioRunner destroys the
        // scenario (newest listener first). Only a press this verb made counts.
        private void OnFlyClickSceneLoadRequested(GameScenes scene)
        {
            if (flyClickPhase != SwitchClickFlyPhase.Pressed || scene != GameScenes.FLIGHT)
                return;
            flyClickSceneLoadRequested = true;
            StockActionIntentMarker armed = ParsekScenario.Instance != null
                ? ParsekScenario.Instance.CurrentStockActionIntent : null;
            flyClickIntentArmed = TestCommandSimulateSwitchClick.IsSiteIntentFor(armed, flyClickSite, flyClickPid);
            if (flyClickIntentArmed)
                flyClickIntentId = armed.IntentId;
            ParsekLog.Info(Tag,
                $"switchclick fly observed FLIGHT load request site={TestCommandSimulateSwitchClick.SiteToken(flyClickSite)} " +
                $"targetPid={flyClickPid} intentArmed={Bool(flyClickIntentArmed)} " +
                $"intentId={(armed != null ? armed.IntentId.ToString("D") : "none")} " +
                $"action={(armed != null ? armed.Action.ToString() : "none")} " +
                $"markerTargetPid={(armed != null ? armed.TargetVesselPersistentId : 0u)}");
        }

        private static bool ReadCanFlyVessel()
        {
            try
            {
                return HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Parameters != null
                    && HighLogic.CurrentGame.Parameters.TrackingStation != null
                    && HighLogic.CurrentGame.Parameters.TrackingStation.CanFlyVessel;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"switchclick CanFlyVessel read failed: {ex.GetType().Name}: {ex.Message} - treating as false");
                return false;
            }
        }

        private static bool ReadDiscoveryOwned(Vessel v)
        {
            try
            {
                return v != null && v.DiscoveryInfo != null && v.DiscoveryInfo.Level == DiscoveryLevels.Owned;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag,
                    $"switchclick discovery read failed: {ex.GetType().Name}: {ex.Message} - treating as not owned");
                return false;
            }
        }
    }
}
