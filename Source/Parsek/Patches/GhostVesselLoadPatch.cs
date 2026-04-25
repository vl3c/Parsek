using System.Reflection;
using CommNet;
using HarmonyLib;
using KSP.UI.Screens.Mapview;
using UnityEngine;
using UnityEngine.UI;

namespace Parsek.Patches
{
    internal enum GhostMapWatchAction
    {
        Unavailable,
        NoActiveGhost,
        DifferentBody,
        OutOfRange,
        Start,
        Stop,
        Refresh,
    }

    internal static class GhostMapWatchHelper
    {
        internal static GhostMapWatchAction ResolveWatchAction(
            int recordingIndex,
            int watchedRecordingIndex,
            bool hasActiveGhost,
            bool sameBody,
            bool inRange,
            bool toggleIfAlreadyWatching)
        {
            if (recordingIndex < 0)
                return GhostMapWatchAction.Unavailable;
            if (watchedRecordingIndex == recordingIndex)
                return toggleIfAlreadyWatching
                    ? GhostMapWatchAction.Stop
                    : GhostMapWatchAction.Refresh;
            if (!hasActiveGhost)
                return GhostMapWatchAction.NoActiveGhost;
            if (!sameBody)
                return GhostMapWatchAction.DifferentBody;
            if (!inRange)
                return GhostMapWatchAction.OutOfRange;
            return GhostMapWatchAction.Start;
        }

        internal static string GetWatchButtonLabel(GhostMapWatchAction action)
        {
            switch (action)
            {
                case GhostMapWatchAction.Stop:
                    return "Stop Watching";
                case GhostMapWatchAction.Refresh:
                    return "Refresh Watch";
                case GhostMapWatchAction.OutOfRange:
                    return "Watch (Too Far)";
                case GhostMapWatchAction.NoActiveGhost:
                    return "Watch (Inactive)";
                case GhostMapWatchAction.DifferentBody:
                    return "Watch (Other SOI)";
                default:
                    return "Watch";
            }
        }

        internal static bool IsWatchActionEnabled(GhostMapWatchAction action)
        {
            return action == GhostMapWatchAction.Start
                || action == GhostMapWatchAction.Stop
                || action == GhostMapWatchAction.Refresh;
        }

        internal static bool TryFocusGhostInMapView(Vessel vessel, string source)
        {
            if (vessel == null
                || PlanetariumCamera.fetch == null
                || !MapView.MapIsEnabled
                || vessel.mapObject == null)
            {
                ParsekLog.Warn("GhostMap",
                    $"Focus failed via {source}: vessel={vessel != null} " +
                    $"camera={PlanetariumCamera.fetch != null} map={MapView.MapIsEnabled} " +
                    $"mapObj={vessel?.mapObject != null}");
                return false;
            }

            PlanetariumCamera.fetch.SetTarget(vessel.mapObject);
            ParsekLog.Info("GhostMap",
                $"Focused map camera on ghost '{vessel.vesselName}' via {source}");
            return true;
        }

        internal static void HandleWatchRequest(
            Vessel vessel,
            int recIndex,
            string source,
            bool toggleIfAlreadyWatching)
        {
            string vesselName = vessel?.vesselName ?? "Ghost";
            var flight = ParsekFlight.Instance;
            if (flight == null)
            {
                ScreenMessages.PostScreenMessage(
                    $"<b>{vesselName}</b> is a ghost — it will materialize when its timeline reaches the spawn point.",
                    5f, ScreenMessageStyle.UPPER_CENTER);
                ParsekLog.Info("GhostMap",
                    $"Ghost '{vesselName}' watch unavailable via {source} (flight controller missing, recIndex={recIndex})");
                return;
            }

            bool hasActiveGhost = recIndex >= 0 && flight.HasActiveGhost(recIndex);
            bool sameBody = recIndex >= 0 && flight.IsGhostOnSameBody(recIndex);
            bool inRange = recIndex >= 0 && flight.IsGhostWithinVisualRange(recIndex);
            GhostMapWatchAction action = ResolveWatchAction(
                recIndex,
                flight.WatchedRecordingIndex,
                hasActiveGhost,
                sameBody,
                inRange,
                toggleIfAlreadyWatching);

            switch (action)
            {
                case GhostMapWatchAction.Unavailable:
                    ScreenMessages.PostScreenMessage(
                        $"<b>{vesselName}</b> is a ghost — it will materialize when its timeline reaches the spawn point.",
                        5f, ScreenMessageStyle.UPPER_CENTER);
                    ParsekLog.Info("GhostMap",
                        $"Ghost '{vesselName}' watch unavailable via {source} (recIndex={recIndex})");
                    return;

                case GhostMapWatchAction.NoActiveGhost:
                    ScreenMessages.PostScreenMessage(
                        $"<b>{vesselName}</b> — ghost not active yet (recording may be in the future).",
                        5f, ScreenMessageStyle.UPPER_CENTER);
                    ParsekLog.Info("GhostMap",
                        $"Ghost '{vesselName}' watch refused via {source} — no active ghost (recIndex={recIndex})");
                    return;

                case GhostMapWatchAction.DifferentBody:
                    ScreenMessages.PostScreenMessage(
                        $"<b>{vesselName}</b> — ghost is in a different SOI.",
                        5f, ScreenMessageStyle.UPPER_CENTER);
                    ParsekLog.Info("GhostMap",
                        $"Ghost '{vesselName}' watch refused via {source} — different SOI (recIndex={recIndex})");
                    return;

                case GhostMapWatchAction.OutOfRange:
                    ScreenMessages.PostScreenMessage(
                        $"<b>{vesselName}</b> — ghost is out of watch range.",
                        5f, ScreenMessageStyle.UPPER_CENTER);
                    ParsekLog.Info("GhostMap",
                        $"Ghost '{vesselName}' watch refused via {source} — out of range " +
                        $"(recIndex={recIndex}) {flight.DescribeWatchEligibilityForLogs(recIndex)}");
                    return;

                case GhostMapWatchAction.Stop:
                    flight.ExitWatchMode();
                    ParsekLog.Info("GhostMap",
                        $"Ghost '{vesselName}' watch stopped via {source} (recIndex={recIndex})");
                    return;

                case GhostMapWatchAction.Refresh:
                    TryFocusGhostInMapView(vessel, source + " refresh");
                    ParsekLog.Info("GhostMap",
                        $"Ghost '{vesselName}' watch refreshed via {source} (recIndex={recIndex})");
                    return;

                case GhostMapWatchAction.Start:
                    break;
            }

            string beforeFocus = flight.DescribeWatchFocusForLogs();
            flight.EnterWatchMode(recIndex);
            if (flight.WatchedRecordingIndex != recIndex)
            {
                ParsekLog.Warn("GhostMap",
                    $"Ghost '{vesselName}' watch request via {source} did not enter watch mode " +
                    $"(recIndex={recIndex}) beforeFocus={beforeFocus} afterFocus={flight.DescribeWatchFocusForLogs()}");
                return;
            }

            TryFocusGhostInMapView(vessel, source + " watch");
            ParsekLog.Info("GhostMap",
                $"Ghost '{vesselName}' watch started via {source} (recIndex={recIndex})");
        }
    }

    /// <summary>
    /// Prevents ghost vessel orbit lines from being clickable in map view (#172).
    /// Ghost orbit lines are visual-only — clicking them should not open the target
    /// popup, because a real vessel might share the same orbit and the click would
    /// be ambiguous. The ghost vessel ICON remains clickable via the
    /// objectNode_OnClick patch below, but only left-click opens the Parsek menu;
    /// right-click (and any other non-left button) is passed through to KSP's
    /// default handler so stock label-pinning still works.
    /// </summary>
    [HarmonyPatch(typeof(OrbitRenderer), nameof(OrbitRenderer.OrbitCast))]
    internal static class GhostOrbitCastPatch
    {
        static bool Prefix(OrbitRenderer __instance, ref bool __result)
        {
            if (__instance.vessel != null
                && GhostMapPresence.IsGhostMapVessel(__instance.vessel.persistentId))
            {
                __result = false;
                return false; // skip original — ghost orbit line not clickable
            }
            return true;
        }
    }


    /// <summary>
    /// Ghost vessel icon click handling in map view.
    /// Left-click opens the custom ghost menu (Focus / Set Target / Watch).
    /// Right-click (and any non-left click) is passed through to KSP's default
    /// handler so stock label-pinning still works. Changed from Postfix to Prefix
    /// so we can return false for ghost vessels, preventing KSP's default context
    /// menu from appearing alongside ours on the left-click path (#192).
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "objectNode_OnClick")]
    internal static class GhostIconClickPatch
    {
        // Track current popup so we can dismiss on re-click
        private static PopupDialog currentGhostMenu;

        /// <summary>
        /// Returns true when the click-up bitmask represents a left click (either
        /// exactly Left, or a combination that includes Left). Returns true for
        /// None as a defensive default — if the caller can't determine the button,
        /// we preserve the pre-filter behavior of opening our menu. Pure/static
        /// so it can be exercised without a live Unity EventSystem.
        /// </summary>
        internal static bool IsLeftClickFromButtons(Mouse.Buttons btns)
        {
            // Defensive default: no button reported -> preserve current UX.
            if (btns == Mouse.Buttons.None) return true;
            return (btns & Mouse.Buttons.Left) != 0;
        }

        internal static bool TryPassThroughNonLeftClick(Mouse.Buttons btns)
        {
            if (IsLeftClickFromButtons(btns))
                return false;

            ParsekLog.Verbose("GhostMap",
                $"Ghost icon non-left click (button={btns}) — passing through to stock handler for default pin-text");
            return true;
        }

        // Bind by original-argument index so the patch does not depend on KSP's
        // external parameter-name metadata staying exactly "btns".
        static bool Prefix(OrbitRendererBase __instance, [HarmonyArgument(1)] Mouse.Buttons btns)
        {
            if (__instance.vessel == null) return true;
            if (!GhostMapPresence.IsGhostMapVessel(__instance.vessel.persistentId)) return true;
            if (!HighLogic.LoadedSceneIsFlight || !MapView.MapIsEnabled) return true;

            // Button filter: only left-click opens the Parsek menu. Right/middle/etc.
            // fall through to KSP's own objectNode_OnClick so the stock pin-text
            // behavior (toggles MapNode.pinned in the MapNode.OnPointerUp path) fires.
            if (TryPassThroughNonLeftClick(btns))
                return true;

            Vessel v = __instance.vessel;
            int recIndex = GhostMapPresence.FindRecordingIndexByVesselPid(v.persistentId);
            string vesselName = v.vesselName ?? "Ghost";
            System.Func<GhostMapWatchAction> resolveCurrentWatchAction = () =>
            {
                var currentFlight = ParsekFlight.Instance;
                bool hasActiveGhost = currentFlight != null && recIndex >= 0 && currentFlight.HasActiveGhost(recIndex);
                bool sameBody = currentFlight != null && recIndex >= 0 && currentFlight.IsGhostOnSameBody(recIndex);
                bool inRange = currentFlight != null && recIndex >= 0 && currentFlight.IsGhostWithinVisualRange(recIndex);
                return GhostMapWatchHelper.ResolveWatchAction(
                    recIndex,
                    currentFlight != null ? currentFlight.WatchedRecordingIndex : -1,
                    hasActiveGhost,
                    sameBody,
                    inRange,
                    toggleIfAlreadyWatching: true);
            };

            // Dismiss any existing ghost menu before opening a new one
            DismissCurrentMenu();

            var options = new DialogGUIBase[]
            {
                new DialogGUIButton("Focus", () =>
                {
                    currentGhostMenu = null;
                    if (PlanetariumCamera.fetch != null && MapView.MapIsEnabled && v.mapObject != null)
                    {
                        PlanetariumCamera.fetch.SetTarget(v.mapObject);
                        ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' focused via menu (recIndex={recIndex})");
                    }
                    else
                    {
                        ParsekLog.Warn("GhostMap", $"Focus failed: camera={PlanetariumCamera.fetch != null} map={MapView.MapIsEnabled} mapObj={v.mapObject != null}");
                    }
                }, dismissOnSelect: true),
                new DialogGUIButton("Set As Target", () =>
                {
                    currentGhostMenu = null;
                    GhostMapPresence.SetGhostMapNavigationTarget(v, recIndex, "icon click");
                }, dismissOnSelect: true),
                new DialogGUIButton(() =>
                {
                    return GhostMapWatchHelper.GetWatchButtonLabel(resolveCurrentWatchAction());
                }, () =>
                {
                    currentGhostMenu = null;
                    GhostMapWatchHelper.HandleWatchRequest(
                        v,
                        recIndex,
                        source: "icon menu",
                        toggleIfAlreadyWatching: true);
                }, () => GhostMapWatchHelper.IsWatchActionEnabled(resolveCurrentWatchAction()),
                160f, 30f, true, (DialogGUIBase[])null)
            };

            // Anchors at (0,0) — matches KSP's MapContextMenu pattern (#196).
            // SpawnPopupDialog forces localPosition=zero after setting anchors;
            // we reposition immediately after using the same approach as stock.
            currentGhostMenu = PopupDialog.SpawnPopupDialog(
                Vector2.zero, Vector2.zero,
                new MultiOptionDialog("GhostIconMenu", "", vesselName,
                    HighLogic.UISkin, 160f, options),
                persistAcrossScenes: false, skin: HighLogic.UISkin);

            // Reposition to mouse cursor (#196) — mirrors MapContextMenu.SetupTransform:
            // force layout rebuild first so the popup has its final size, then convert
            // screen coords to canvas-local and offset downward so menu opens below cursor.
            if (currentGhostMenu != null)
            {
                currentGhostMenu.SetDraggable(false);
                var rt = currentGhostMenu.GetComponent<RectTransform>();
                if (rt != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                    RectTransform canvasRect = MapViewCanvasUtil.MapViewCanvasRect;
                    if (canvasRect != null)
                    {
                        Vector3 uiPos = CanvasUtil.ScreenToUISpacePos(
                            Input.mousePosition, canvasRect, out bool zPositive);
                        uiPos = CanvasUtil.AnchorOffset(uiPos, rt, Vector2.down);
                        rt.localPosition = uiPos;
                        ParsekLog.Verbose("GhostMap",
                            $"Popup positioned at cursor: screen=({Input.mousePosition.x:F0},{Input.mousePosition.y:F0}) " +
                            $"canvas=({uiPos.x:F0},{uiPos.y:F0})");
                    }
                }
            }

            // Track when the menu was opened so outside-click check can skip a few frames
            menuOpenFrame = Time.frameCount;

            ParsekLog.Verbose("GhostMap",
                $"Ghost icon clicked: '{vesselName}' pid={v.persistentId} recIndex={recIndex}");

            return false; // skip original — prevent KSP's default context menu
        }

        /// <summary>
        /// Dismisses the current ghost popup menu if one is open.
        /// Called before opening a new menu and on scene changes.
        /// </summary>
        internal static void DismissCurrentMenu()
        {
            if (currentGhostMenu != null)
            {
                currentGhostMenu.Dismiss();
                currentGhostMenu = null;
            }
        }

        /// <summary>
        /// Frame count when menu was last opened. Outside-click dismissal is delayed
        /// by a few frames to avoid eating the same click that opened the menu or
        /// a button click inside the menu.
        /// </summary>
        private static int menuOpenFrame;

        /// <summary>
        /// Called from ParsekFlight.LateUpdate() to check for outside clicks.
        /// Uses GetMouseButtonUp instead of GetMouseButtonDown: button callbacks fire
        /// on mouse-down (and dismiss the dialog via dismissOnSelect, clearing
        /// currentGhostMenu). By the time mouse-up arrives, the menu is already null
        /// if a button was clicked — so we only dismiss on genuinely outside clicks.
        /// </summary>
        internal static void CheckOutsideClick()
        {
            if (currentGhostMenu == null) return;

            // Grace period: skip the first 5 frames after opening
            if (Time.frameCount - menuOpenFrame < 5) return;

            if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1))
            {
                DismissCurrentMenu();
            }
        }
    }

    /// <summary>
    /// Prevents ghost map ProtoVessels from going off rails (becoming loaded physics vessels).
    /// Ghost vessels exist only for map presence (orbit lines, tracking station, targeting).
    /// They must remain unloaded — the ghost mesh provides the visual representation.
    /// </summary>
    [HarmonyPatch(typeof(Vessel), nameof(Vessel.GoOffRails))]
    internal static class GhostVesselLoadPatch
    {
        static bool Prefix(Vessel __instance)
        {
            if (GhostMapPresence.IsGhostMapVessel(__instance.persistentId))
            {
                ParsekLog.Verbose("GhostMap",
                    $"Blocked GoOffRails for ghost vessel '{__instance.vesselName}' pid={__instance.persistentId}");
                return false; // skip original — keep ghost on rails
            }
            return true;
        }
    }

    /// <summary>
    /// Prevents ghost map ProtoVessels from registering their own CommNet node.
    /// Ghost CommNet relay is handled separately by GhostCommNetRelay using the
    /// CommNet API directly with proper antenna specs from the recording.
    /// Without this patch, the ghost ProtoVessel's CommNetVessel would register
    /// a duplicate zero-power node in the CommNet network graph.
    /// </summary>
    [HarmonyPatch(typeof(CommNetVessel), "OnStart")]
    internal static class GhostCommNetVesselPatch
    {
        static bool Prefix(CommNetVessel __instance)
        {
            Vessel v = __instance.Vessel;
            if (v != null && GhostMapPresence.IsGhostMapVessel(v.persistentId))
            {
                // Remove self from vessel modules and destroy, same as KSP does
                // for Flag/Debris/Unknown vessels in the original OnStart
                v.vesselModules.Remove(__instance);
                v.connection = null;
                Object.Destroy(__instance);

                ParsekLog.Verbose("GhostMap",
                    $"Suppressed CommNetVessel for ghost '{v.vesselName}' " +
                    $"pid={v.persistentId} — GhostCommNetRelay handles CommNet");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Intercepts vessel switching to ghost map ProtoVessels (double-click in map view).
    /// Instead of switching to the ghost (a single barometer part), enters watch mode
    /// for the ghost's recording and focuses the map camera on the ghost vessel.
    /// Uses explicit method targeting because FlightGlobals.SetActiveVessel has two
    /// overloads (Vessel) and (Vessel, bool); the Type[] attribute constructor doesn't
    /// reliably disambiguate with Harmony's CreateClassProcessor.
    /// </summary>
    [HarmonyPatch]
    internal static class GhostVesselSwitchPatch
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return typeof(FlightGlobals).GetMethod(
                nameof(FlightGlobals.SetActiveVessel),
                new[] { typeof(Vessel) });
        }

        static bool Prefix(Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            int recIndex = GhostMapPresence.FindRecordingIndexByVesselPid(v.persistentId);
            if (recIndex < 0)
            {
                // Chain ghost — not in the recording-index dict. Show message instead of silently blocking.
                ScreenMessages.PostScreenMessage(
                    $"<b>{v.vesselName}</b> is a ghost vessel — it will materialize when its timeline reaches the spawn point.",
                    5f, ScreenMessageStyle.UPPER_CENTER);
                ParsekLog.Info("GhostMap",
                    $"Blocked SetActiveVessel for chain ghost '{v.vesselName}' pid={v.persistentId}");
                return false;
            }

            var flight = ParsekFlight.Instance;
            if (flight != null || recIndex >= 0)
            {
                GhostMapWatchHelper.HandleWatchRequest(
                    v,
                    recIndex,
                    source: "map double-click",
                    toggleIfAlreadyWatching: false);
            }

            return false;
        }
    }

    /// <summary>
    /// Prevents ghost vessels from appearing in OrbitTargeter's "Set as Target"
    /// popup (the second click path in map view). OrbitTargeter.TargetCastNodes
    /// is a private method (returns OrbitDriver, out OrbitCastHit parameter) that
    /// checks MapNode hover state for all orbits and creates a MapContextMenu
    /// when it detects a hovered vessel. This postfix nulls the result for ghost
    /// vessels so OrbitTargeter never creates the default KSP target popup for them.
    /// Fixes: KSP's default popup appearing alongside Parsek's ghost menu when
    /// the ghost is in a different SOI from the camera's reference body (#192).
    /// </summary>
    [HarmonyPatch]
    internal static class GhostTargetCastNodesPatch
    {
        static MethodBase TargetMethod()
        {
            // TargetCastNodes has signature: OrbitDriver TargetCastNodes(out OrbitCastHit)
            // Must search all methods since GetMethod with out params is fragile
            var methods = typeof(OrbitTargeter).GetMethods(
                BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "TargetCastNodes")
                {
                    ParsekLog.Info("GhostMap",
                        $"GhostTargetCastNodesPatch: found TargetCastNodes with {methods[i].GetParameters().Length} params");
                    return methods[i];
                }
            }
            ParsekLog.Warn("GhostMap", "GhostTargetCastNodesPatch: TargetCastNodes not found — patch will not apply");
            return null;
        }

        static void Postfix(ref OrbitDriver __result)
        {
            if (__result != null && __result.vessel != null
                && GhostMapPresence.IsGhostMapVessel(__result.vessel.persistentId))
            {
                ParsekLog.Verbose("GhostMap",
                    $"Suppressed OrbitTargeter.TargetCastNodes for ghost '{__result.vessel.vesselName}'");
                __result = null;
            }
        }
    }
}
