using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using KSP.UI.Screens.Mapview.MapContextMenuOptions;

namespace Parsek.Patches
{
    /// <summary>
    /// Arms a <see cref="StockActionIntentMarker"/> when the player clicks the
    /// "Switch To" item in the map-view context menu on an owned vessel. The marker
    /// authorizes immediate switch-segment start when the same-frame in-FLIGHT
    /// <c>OnVesselSwitchComplete</c> consumes it in Phase C.
    ///
    /// Decompiled MapContextMenuOptions.FocusObject.OnSelect()
    /// (KSP 1.12.5 Assembly-CSharp.dll, load-bearing excerpts):
    /// <code>
    /// protected override void OnSelect() {
    ///   switch (GetMode()) {
    ///     case FocusMode.OwnedVessel:
    ///       if (HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsFar) {
    ///         FlightGlobals.SetActiveVessel(vessel);
    ///         MapView.ExitMapView();
    ///       }
    ///       break;
    ///     case FocusMode.UnownedVessel:
    ///       SpaceTracking.GoToAndFocusVessel(vessel);  // out-of-scope: routes to TS
    ///       break;
    ///     case FocusMode.CelestialBody:
    ///       PlanetariumCamera.fetch.SetTarget(...);     // out-of-scope: camera only
    ///       break;
    ///   }
    /// }
    /// </code>
    ///
    /// FlightGlobals.SetActiveVessel(Vessel) fires <c>onVesselSwitching</c> →
    /// <c>v.MakeActive()</c> → <c>onVesselChange</c> synchronously inside the method
    /// body before returning. Parsek's <c>OnVesselSwitchComplete</c> listener
    /// therefore runs *inside* SetActiveVessel — BEFORE this Postfix would run. Arming
    /// in the Postfix is too late; the consume site has already missed it. The
    /// correct shape is Prefix-arms / Postfix-cleans-up-on-refusal, with the Prefix's
    /// IntentId passed through <c>__state</c> so the Postfix only clears a marker it
    /// armed (not a subsequent click's marker).
    ///
    /// Refused early-return paths (decompile of FlightGlobals.setActiveVessel — all
    /// return <c>false</c> without firing onVesselSwitching / onVesselChange): vessel
    /// is null, vessel is already active, <c>ClearToSave()</c> fails for any of six
    /// reasons (not in atmosphere, under acceleration, moving over surface, about to
    /// crash, on a ladder, throttled up), or target's DiscoveryInfo.Level != Owned.
    /// The Postfix clears the marker with reason <c>refused-no-switch</c> in all of
    /// these.
    ///
    /// Unloaded-vessel branch outcome: when the target is unloaded, setActiveVessel
    /// fires <c>onVesselSwitchingToUnloaded</c>, saves, and calls
    /// <c>FlightDriver.StartAndFocusVessel</c> — a scene transition into a fresh
    /// FLIGHT scene load. The Postfix sees no <c>onVesselChange</c> consume happened
    /// and clears the marker with <c>refused-no-switch</c>; the new FLIGHT scene has
    /// no in-scene marker to consume. The deliberate outcome is that Map Switch-To
    /// to an unloaded vessel does NOT immediate-start a segment; the first-
    /// modification watcher catches the first meaningful change in the new scene.
    /// </summary>
    [HarmonyPatch]
    internal static class MapFocusObjectOnSelectPatch
    {
        // FocusObject lives in NAMESPACE KSP.UI.Screens.Mapview.MapContextMenuOptions
        // (a namespace, not a class) and FocusMode is a nested enum inside it.
        // Resolved at type-load via the FocusObject type's nested types. If a future
        // KSP drop renames anything, TargetMethod() returns null and Harmony skips
        // the patch instead of throwing.
        private static readonly Type FocusObjectType = typeof(FocusObject);
        private static readonly Type FocusModeType =
            FocusObjectType.GetNestedType("FocusMode", BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly object FocusModeOwnedVessel =
            FocusModeType != null && Enum.IsDefined(FocusModeType, "OwnedVessel")
                ? Enum.Parse(FocusModeType, "OwnedVessel")
                : null;

        // Harmony invokes TargetMethod() to resolve the target. The explicit
        // MethodInfo lookup (non-public override) sidesteps any attribute-time
        // resolution surprise that could throw if the override is materialized on
        // an unexpected derived class.
        static MethodBase TargetMethod()
        {
            if (FocusObjectType == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    "MapFocusObjectOnSelectPatch: FocusObject type not found; patch will not be applied");
                return null;
            }
            MethodInfo method = FocusObjectType.GetMethod(
                "OnSelect",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"MapFocusObjectOnSelectPatch: OnSelect method not found on {FocusObjectType.FullName}; patch will not be applied");
                return null;
            }
            return method;
        }

        static void Prefix(object __instance, out Guid __state)
        {
            __state = default(Guid);

            if (__instance == null)
                return;

            // GetMode() — defensive Traverse so we don't crash if the method is
            // renamed in a future KSP drop.
            object modeValue;
            try
            {
                modeValue = Traverse.Create(__instance).Method("GetMode").GetValue();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: GetMode() failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (FocusModeOwnedVessel == null || modeValue == null || !modeValue.Equals(FocusModeOwnedVessel))
            {
                // UnownedVessel routes to TRACKSTATION (handled by the TS Fly
                // patch); CelestialBody is camera-only. Neither is in scope.
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: focusMode={(modeValue != null ? modeValue.ToString() : "<null>")} (not OwnedVessel)");
                return;
            }

            // Defensive Traverse on the private 'vessel' field. If KSP renames
            // the field we log a Warn and bail without arming (instead of arming
            // with PID 0).
            Vessel vessel;
            try
            {
                vessel = Traverse.Create(__instance).Field("vessel").GetValue<Vessel>();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: vessel field Traverse failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (vessel == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    "Map Switch-To intent not armed: FocusObject.vessel is null (Traverse may have failed)");
                return;
            }

            // Stock will refuse the switch if CanSwitchVesselsFar is off (the
            // OwnedVessel branch is gated on it). Do not arm.
            bool canSwitchVesselsFar = true;
            try
            {
                if (HighLogic.CurrentGame != null
                    && HighLogic.CurrentGame.Parameters != null
                    && HighLogic.CurrentGame.Parameters.Flight != null)
                {
                    canSwitchVesselsFar = HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsFar;
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: CanSwitchVesselsFar read failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (!canSwitchVesselsFar)
            {
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: CanSwitchVesselsFar=false targetPid={vessel.persistentId}");
                return;
            }

            var settings = ParsekSettings.Current;
            if (settings != null && !settings.autoRecordOnMapSwitchTo)
            {
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: setting-off targetPid={vessel.persistentId} vessel='{vessel.vesselName ?? "(unknown)"}'");
                return;
            }

            var scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"Map Switch-To intent not armed: ParsekScenario.Instance is null targetPid={vessel.persistentId}");
                return;
            }

            Guid intentId = Guid.NewGuid();
            var marker = new StockActionIntentMarker
            {
                IntentId = intentId,
                Action = StockActionType.MapSwitchTo,
                TargetVesselPersistentId = vessel.persistentId,
                SourceScene = StockActionSourceScene.Flight,
                CapturedRealtime = UnityEngine.Time.realtimeSinceStartup,
                CapturedUT = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                ProcessSessionId = ParsekProcess.ProcessSessionId,
            };

            // Prefix-on-Prefix race log: ParsekScenario.ArmStockActionIntent
            // already emits a `stale-intent-superseded` Info log when it overwrites
            // a still-armed marker, but we also tag the SwitchIntentPatch subsystem
            // so a grep of `[SwitchIntentPatch]` catches it.
            var existing = scenario.CurrentStockActionIntent;
            if (existing != null && existing.IntentId != intentId)
            {
                ParsekLog.Info("SwitchIntentPatch",
                    $"Map Switch-To intent stale-intent-superseded: prior intentId={existing.IntentId:D} " +
                    $"action={existing.Action} new intentId={intentId:D}");
            }

            scenario.ArmStockActionIntent(marker);
            __state = intentId;
            ParsekLog.Info("SwitchIntentPatch",
                $"Map Switch-To intent armed: intentId={marker.IntentId:D} action={marker.Action} " +
                $"targetPid={marker.TargetVesselPersistentId} sourceScene={marker.SourceScene} " +
                $"capturedUT={marker.CapturedUT.ToString("R", CultureInfo.InvariantCulture)}");
        }

        static void Postfix(Guid __state)
        {
            // Prefix didn't arm (gate failed) — nothing to clean up.
            if (__state == Guid.Empty)
                return;

            var scenario = ParsekScenario.Instance;
            if (scenario == null)
                return;

            var current = scenario.CurrentStockActionIntent;
            if (current == null)
            {
                // Either Phase C's OnVesselSwitchComplete consumed it (success
                // path) or another caller cleared it. Either way, nothing to do.
                return;
            }
            if (current.IntentId != __state)
            {
                // Subsequent click's marker (stale-intent-superseded path); leave
                // it armed for the new Prefix's lifecycle.
                ParsekLog.Verbose("SwitchIntentPatch",
                    $"Map Switch-To Postfix: marker IntentId mismatch (mine={__state:D} current={current.IntentId:D}) — leaving armed");
                return;
            }

            // Marker is still armed under our IntentId — consume site (Phase C
            // OnVesselSwitchComplete) didn't fire, meaning SetActiveVessel took an
            // early-return path (vessel null, already active, ClearToSave failed,
            // DiscoveryLevel != Owned) or the unloaded-vessel scene-transition
            // branch. Clear with refused-no-switch.
            scenario.ClearStockActionIntent("refused-no-switch");
            ParsekLog.Info("SwitchIntentPatch",
                $"Map Switch-To Postfix: cleared own marker intentId={__state:D} reason=refused-no-switch");
        }

        /// <summary>
        /// Pure gate predicate exposed for unit tests. Mirrors the four Prefix
        /// gates: setting-on, FocusMode == OwnedVessel, CanSwitchVesselsFar, and
        /// vessel non-null. Returns true only when all four gates pass.
        /// </summary>
        internal static bool ShouldArmMapSwitchTo(
            bool settingOn,
            bool isOwnedVesselMode,
            bool canSwitchVesselsFar,
            bool vesselNotNull)
        {
            if (!vesselNotNull) return false;
            if (!isOwnedVesselMode) return false;
            if (!canSwitchVesselsFar) return false;
            if (!settingOn) return false;
            return true;
        }
    }
}
