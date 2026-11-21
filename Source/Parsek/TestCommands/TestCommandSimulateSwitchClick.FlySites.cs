using System;

namespace Parsek.TestCommands
{
    /// <summary>The phase of a two-phase <c>site=ts</c> / <c>site=ksc</c> click.</summary>
    internal enum SwitchClickFlyPhase
    {
        /// <summary>Waiting for the scene's stock control: <c>SpaceTracking.Instance</c>, or
        /// the target's marker in <c>KSCVesselMarkers</c> (stock spawns the markers 15 frames
        /// after the Space Center wakes).</summary>
        AwaitingControl,

        /// <summary>Tracking Station only: <c>SpaceTracking.SetVessel(v, true)</c> was called
        /// (what a list-row click calls); waiting for the selection to read back.</summary>
        Selecting,

        /// <summary>The Fly button was pressed; waiting for stock to request the FLIGHT load
        /// (the KSC marker runs its <c>FlyVessel</c> one frame after the press).</summary>
        Pressed,

        /// <summary>The FLIGHT load was requested; waiting for the new flight to settle and
        /// for Parsek's consume site to spend the armed intent.</summary>
        Loading,
    }

    /// <summary>What one completion poll of a fly-site click does next.</summary>
    internal enum SwitchClickFlyPollAction
    {
        NotYet,
        /// <summary>The control never appeared (REJECTED <c>fly-control-missing</c>).</summary>
        ControlMissing,
        /// <summary>Tracking Station: call <c>SetVessel</c> (again).</summary>
        Select,
        /// <summary>Tracking Station: the selection never read back (ERROR).</summary>
        SelectFailed,
        /// <summary>The Fly button is not interactable (REJECTED <c>fly-button-locked</c>).</summary>
        ButtonLocked,
        /// <summary>Press the Fly button now.</summary>
        Press,
        /// <summary>Stock requested the FLIGHT load with the site's intent armed: wait for
        /// the new flight.</summary>
        BeginLoading,
        /// <summary>Pressed, but stock started no FLIGHT load (ERROR).</summary>
        NoSceneLoad,
        /// <summary>The FLIGHT load was requested with no matching intent armed (ERROR): the
        /// arming patch did not run on the stock path.</summary>
        IntentNotArmed,
        /// <summary>The flight settled and the intent was spent on the target (OK).</summary>
        Ok,
        /// <summary>The flight settled on a different vessel (ERROR).</summary>
        WrongVessel,
        /// <summary>The flight settled but the intent stayed armed past the wait (ERROR).</summary>
        IntentNotConsumed,
        /// <summary>The load fell back to the main menu (ERROR).</summary>
        ReturnedToMenu,
        /// <summary>The verb's budget ran out (ERROR).</summary>
        Timeout,
    }

    /// <summary>
    /// The two cross-scene sites of <c>SimulateStockSwitchClick</c>: <c>site=ts</c> (the
    /// Tracking Station's Fly) and <c>site=ksc</c> (a Space Center vessel marker's Fly).
    ///
    /// <para>WHY THEY PRESS STOCK CONTROLS. Both stock handlers save <c>persistent</c> and call
    /// <c>FlightDriver.StartAndFocusVessel("persistent", FlightGlobals.Vessels.IndexOf(v))</c>,
    /// and Parsek's patches on them (<c>GhostTrackingFlyPatch</c> +
    /// <c>SwitchIntentTrackingStationFlyPatch</c> on <c>SpaceTracking.FlyVessel</c>,
    /// <c>KscVesselMarkerFlyPatch</c> on <c>KSCVesselMarkers.FlyVessel</c>) arm the
    /// <c>StockActionIntentMarker</c> and strip the ghost map vessels from the live list
    /// BEFORE that save, so the index is the saved file's. A verb that armed the marker itself
    /// and called <c>StartAndFocusVessel</c> would certify none of that. So the verb only
    /// presses: <c>SpaceTracking.SetVessel(v, true)</c> then its <c>FlyButton</c>; or the
    /// marker's <c>Marker</c> button (expand) then its <c>FlyButton</c>
    /// (<c>Dismiss(Fly)</c> -> <c>FlyVessel</c> one frame later).</para>
    ///
    /// <para>TWO-PHASE. Grammar is the map site's (<c>site=ts|ksc vessel=|pid=</c>), issued in
    /// the site's own scene (the dispatcher defers <c>not-in-trackstation</c> /
    /// <c>not-at-space-center</c> elsewhere). The terminal waits for the NEW flight to settle
    /// (its <c>ParsekFlight</c> saw <c>onFlightReady</c>, the active vessel unpacked) and then
    /// for the armed intent to be SPENT (cleared, by the consume or a refusal), so OK means
    /// stock flew the player into the target and Parsek's consume ran on it. The payload is
    /// the map site's shape with <c>route=stock-fly-control</c>; as for the map site, the
    /// consume ROUTE (segment started / on-surface defer / ...) stays in the
    /// <c>[SwitchSegment]</c> / <c>[SwitchIntent]</c> lines, never re-derived here.</para>
    /// </summary>
    internal static partial class TestCommandSimulateSwitchClick
    {
        /// <summary>The payload <c>route</c> of a fly-site click.</summary>
        internal const string FlyControlRoute = "stock-fly-control";

        /// <summary>The dispatch DEFER token for <c>site=ts</c> outside the Tracking Station.</summary>
        internal const string NotInTrackingStationReason = "not-in-trackstation";

        /// <summary>The dispatch DEFER token for <c>site=ksc</c> outside the Space Center
        /// (the token KscAction already defers with).</summary>
        internal const string NotAtSpaceCenterReason = "not-at-space-center";

        // ----- Fly-site REJECTED tokens (gate) -----

        /// <summary>The scene's stock control (Tracking Station or the target's KSC marker)
        /// did not appear within <see cref="ControlWaitFrames"/>.</summary>
        internal const string FlyControlMissingReason = "fly-control-missing";

        /// <summary>The game's <c>TrackingStation.CanFlyVessel</c> parameter is off: stock
        /// locks the Fly button.</summary>
        internal const string CannotFlyVesselReason = "cannot-fly-vessel";

        /// <summary>The target is not an Owned discovery: stock's <c>SpaceTracking.FlyVessel</c>
        /// posts a screen message and does not fly.</summary>
        internal const string TargetNotOwnedReason = "target-not-owned";

        /// <summary>The target has no stock KSC marker (not landed or splashed on the home
        /// world, or a deployed science part / dropped part).</summary>
        internal const string TargetHasNoKscMarkerReason = "target-has-no-ksc-marker";

        /// <summary>The Fly button read non-interactable (or the marker refused to open).</summary>
        internal const string FlyButtonLockedReason = "fly-button-locked";

        // ----- Fly-site ERROR tokens (observed after the press) -----

        /// <summary>The Tracking Station selection never read back as the target.</summary>
        internal const string FlySelectFailedReason = "fly-select-failed";

        /// <summary>Fly was pressed and stock requested no FLIGHT load.</summary>
        internal const string FlyNoSceneLoadReason = "fly-no-scene-load";

        /// <summary>The FLIGHT load was requested but no intent for the target was armed.</summary>
        internal const string FlyIntentNotArmedReason = "fly-intent-not-armed";

        /// <summary>The flight settled on a vessel other than the target.</summary>
        internal const string FlyWrongVesselReason = "fly-wrong-vessel";

        /// <summary>The flight settled but the armed intent was never spent.</summary>
        internal const string FlyIntentNotConsumedReason = "fly-intent-not-consumed";

        /// <summary>The load settled back at the main menu.</summary>
        internal const string FlyReturnedToMenuReason = "fly-returned-to-menu";

        /// <summary>The verb's budget ran out.</summary>
        internal const string FlyTimeoutReason = "fly-timeout";

        /// <summary>The fly-site REJECTED vocabulary, in gate order.</summary>
        internal static readonly string[] FlyRejectReasons =
        {
            "fly-control-missing",
            "cannot-fly-vessel",
            "target-not-owned",
            "target-has-no-ksc-marker",
            "fly-button-locked",
        };

        /// <summary>The fly-site ERROR vocabulary.</summary>
        internal static readonly string[] FlyErrorReasons =
        {
            "fly-select-failed",
            "fly-no-scene-load",
            "fly-intent-not-armed",
            "fly-wrong-vessel",
            "fly-intent-not-consumed",
            "fly-returned-to-menu",
            "fly-timeout",
        };

        /// <summary>Frames to wait for the stock control to appear (stock spawns the KSC
        /// markers 15 frames after the Space Center wakes).</summary>
        internal const int ControlWaitFrames = 300;

        /// <summary>Frames to wait for <c>SetVessel</c> to read back (stock drops a second
        /// <c>SetVessel</c> in one frame, so it is retried).</summary>
        internal const int SelectWaitFrames = 120;

        /// <summary>Frames from the press to stock's FLIGHT load request (the KSC marker's
        /// <c>FlyVessel</c> runs one frame after the press).</summary>
        internal const int PressToLoadFrames = 120;

        /// <summary>Frames after the flight settled for the consume to spend the intent. The
        /// consume runs from <c>onFlightReady</c>, or from the active-tree restore when one is
        /// pending, so it is normally spent before the first poll that sees the flight.</summary>
        internal const int IntentConsumeWaitFrames = 600;

        /// <summary>True for the two cross-scene sites.</summary>
        internal static bool IsFlySite(SwitchClickSite site)
            => site == SwitchClickSite.TrackingStation || site == SwitchClickSite.Ksc;

        /// <summary>The scene the site's control lives in.</summary>
        internal static TestCommandScene RequiredScene(SwitchClickSite site)
        {
            switch (site)
            {
                case SwitchClickSite.TrackingStation: return TestCommandScene.TrackingStation;
                case SwitchClickSite.Ksc: return TestCommandScene.SpaceCenter;
                default: return TestCommandScene.Flight;
            }
        }

        /// <summary>The dispatch DEFER token for the site outside its scene.</summary>
        internal static string WrongSceneDeferReason(SwitchClickSite site)
        {
            switch (site)
            {
                case SwitchClickSite.TrackingStation: return NotInTrackingStationReason;
                case SwitchClickSite.Ksc: return NotAtSpaceCenterReason;
                default: return TestCommandDispatcher.NotInFlightReason;
            }
        }

        /// <summary>
        /// The dispatch scene sub-gate: null to execute, else the DEFER token. An unparseable
        /// <c>site=</c> executes in any scene so the executor answers its typed
        /// <c>site-arg-invalid</c> at once instead of deferring to a TIMEOUT.
        /// </summary>
        internal static string DecideDispatchScene(string rawSite, TestCommandScene scene)
        {
            if (!TryParseSite(rawSite, out SwitchClickSite site))
                return null;
            return scene == RequiredScene(site) ? null : WrongSceneDeferReason(site);
        }

        /// <summary>The intent action the site's arming patch stamps.</summary>
        internal static StockActionType ExpectedIntentAction(SwitchClickSite site)
            => site == SwitchClickSite.Ksc ? StockActionType.KscMarkerFly : StockActionType.TrackingStationFly;

        /// <summary>The intent source scene the site's arming patch stamps.</summary>
        internal static StockActionSourceScene ExpectedIntentSourceScene(SwitchClickSite site)
            => site == SwitchClickSite.Ksc ? StockActionSourceScene.SpaceCenter : StockActionSourceScene.TrackingStation;

        /// <summary>
        /// True when <paramref name="marker"/> is the intent the site's patch arms for
        /// <paramref name="targetPid"/>: the action, source scene and target all match.
        /// </summary>
        internal static bool IsSiteIntentFor(StockActionIntentMarker marker, SwitchClickSite site, uint targetPid)
            => marker != null
               && marker.Action == ExpectedIntentAction(site)
               && marker.SourceScene == ExpectedIntentSourceScene(site)
               && marker.TargetVesselPersistentId == targetPid;

        /// <summary>
        /// The pre-press gate, in order: scenario, the Tracking Station's CanFlyVessel and
        /// Owned discovery (stock's own Fly refusals), then the KSC marker predicate. Returns
        /// null to proceed, else the REJECTED reason (<see cref="ScenarioNotReadyReason"/> or a
        /// <see cref="FlyRejectReasons"/> member).
        /// </summary>
        internal static string DecideFlyGate(
            SwitchClickSite site, bool scenarioReady, bool canFlyVessel, bool targetOwned,
            bool targetHasKscMarker)
        {
            if (!scenarioReady)
                return ScenarioNotReadyReason;
            if (site == SwitchClickSite.TrackingStation)
            {
                if (!canFlyVessel)
                    return CannotFlyVesselReason;
                if (!targetOwned)
                    return TargetNotOwnedReason;
            }
            if (site == SwitchClickSite.Ksc && !targetHasKscMarker)
                return TargetHasNoKscMarkerReason;
            return null;
        }

        /// <summary>
        /// One completion poll. Inputs are what the applier samples this frame;
        /// <paramref name="framesInPhase"/> counts frames since the phase began (for
        /// <see cref="SwitchClickFlyPhase.Loading"/>, since the new flight settled).
        /// <paramref name="flightSettled"/> is the NEW flight's <c>ParsekFlight</c> having seen
        /// <c>onFlightReady</c> with the active vessel unpacked; <paramref name="gameLoaded"/> is
        /// <c>HighLogic.CurrentGame != null</c>.
        /// </summary>
        internal static SwitchClickFlyPollAction DecideFlyPoll(
            SwitchClickFlyPhase phase,
            SwitchClickSite site,
            TestCommandScene scene,
            bool gameLoaded,
            bool controlPresent,
            bool targetSelected,
            bool flyButtonInteractable,
            bool sceneLoadRequested,
            bool intentArmedForTarget,
            bool flightSettled,
            bool intentStillArmed,
            bool activeIsTarget,
            int framesInPhase,
            bool expired)
        {
            if (phase == SwitchClickFlyPhase.Loading)
            {
                if (scene == TestCommandScene.MainMenu)
                    return SwitchClickFlyPollAction.ReturnedToMenu;
                if (scene == TestCommandScene.Flight && gameLoaded && flightSettled)
                {
                    if (!activeIsTarget)
                        return SwitchClickFlyPollAction.WrongVessel;
                    if (!intentStillArmed)
                        return SwitchClickFlyPollAction.Ok;
                    if (framesInPhase >= IntentConsumeWaitFrames)
                        return SwitchClickFlyPollAction.IntentNotConsumed;
                }
                return expired ? SwitchClickFlyPollAction.Timeout : SwitchClickFlyPollAction.NotYet;
            }

            if (expired)
                return SwitchClickFlyPollAction.Timeout;

            if (phase == SwitchClickFlyPhase.Pressed)
            {
                if (sceneLoadRequested)
                    return intentArmedForTarget
                        ? SwitchClickFlyPollAction.BeginLoading
                        : SwitchClickFlyPollAction.IntentNotArmed;
                return framesInPhase >= PressToLoadFrames
                    ? SwitchClickFlyPollAction.NoSceneLoad
                    : SwitchClickFlyPollAction.NotYet;
            }

            if (scene != RequiredScene(site) || !controlPresent)
                return framesInPhase >= ControlWaitFrames
                    ? SwitchClickFlyPollAction.ControlMissing
                    : SwitchClickFlyPollAction.NotYet;

            if (site == SwitchClickSite.TrackingStation)
            {
                if (phase == SwitchClickFlyPhase.AwaitingControl)
                    return SwitchClickFlyPollAction.Select;
                if (!targetSelected)
                    return framesInPhase >= SelectWaitFrames
                        ? SwitchClickFlyPollAction.SelectFailed
                        : SwitchClickFlyPollAction.Select;
            }

            return flyButtonInteractable ? SwitchClickFlyPollAction.Press : SwitchClickFlyPollAction.ButtonLocked;
        }

        /// <summary>The grep-stable line logged when the stock Fly control is pressed.</summary>
        internal static string FormatFlyPressedLine(SwitchClickSite site, uint targetPid, string targetName,
            int framesWaited)
            => "switchclick fly pressed site=" + SiteToken(site)
               + " targetPid=" + targetPid.ToString(System.Globalization.CultureInfo.InvariantCulture)
               + " targetName=" + (targetName ?? string.Empty)
               + " control=" + (site == SwitchClickSite.Ksc ? "ksc-marker-fly" : "trackingstation-fly")
               + " framesWaited=" + framesWaited.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
