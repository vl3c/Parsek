using System.Reflection;
using CommNet;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents ghost vessel orbit lines from being clickable in map view (#172).
    /// Ghost orbit lines are visual-only — clicking them should not open the target
    /// popup, because a real vessel might share the same orbit and the click would
    /// be ambiguous. The ghost vessel ICON remains clickable via the objectNode_OnClick
    /// patch below.
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
    /// Handles left-click on ghost vessel icon in map view. Shows a popup with
    /// "Set As Target", "Watch", and "Focus" options. Changed from Postfix to Prefix
    /// so we can return false for ghost vessels, preventing KSP's default context menu
    /// from appearing alongside ours (#192).
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "objectNode_OnClick")]
    internal static class GhostIconClickPatch
    {
        // Track current popup so we can dismiss on re-click
        private static PopupDialog currentGhostMenu;

        static bool Prefix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null) return true;
            if (!GhostMapPresence.IsGhostMapVessel(__instance.vessel.persistentId)) return true;
            if (!HighLogic.LoadedSceneIsFlight || !MapView.MapIsEnabled) return true;

            Vessel v = __instance.vessel;
            int recIndex = GhostMapPresence.FindRecordingIndexByVesselPid(v.persistentId);
            string vesselName = v.vesselName ?? "Ghost";

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
                    if (FlightGlobals.fetch != null)
                    {
                        FlightGlobals.fetch.SetVesselTarget(v);
                        ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' set as target via icon click");
                    }
                }, dismissOnSelect: true),
                new DialogGUIButton("Watch", () =>
                {
                    currentGhostMenu = null;
                    var flight = ParsekFlight.Instance;
                    if (flight == null || recIndex < 0)
                    {
                        ScreenMessages.PostScreenMessage(
                            $"<b>{vesselName}</b> is a ghost — it will materialize when its timeline reaches the spawn point.",
                            5f, ScreenMessageStyle.UPPER_CENTER);
                        ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' watch unavailable (recIndex={recIndex})");
                    }
                    else if (!flight.HasActiveGhost(recIndex))
                    {
                        ScreenMessages.PostScreenMessage(
                            $"<b>{vesselName}</b> — ghost not active yet (recording may be in the future).",
                            5f, ScreenMessageStyle.UPPER_CENTER);
                        ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' watch refused — no active ghost (recIndex={recIndex})");
                    }
                    else if (!flight.IsGhostOnSameBody(recIndex))
                    {
                        ScreenMessages.PostScreenMessage(
                            $"<b>{vesselName}</b> — ghost is on a different celestial body.",
                            5f, ScreenMessageStyle.UPPER_CENTER);
                        ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' watch refused — different body (recIndex={recIndex})");
                    }
                    else if (!flight.IsGhostWithinVisualRange(recIndex))
                    {
                        ScreenMessages.PostScreenMessage(
                            $"<b>{vesselName}</b> — ghost is too far away to watch.",
                            5f, ScreenMessageStyle.UPPER_CENTER);
                        ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' watch refused — out of range (recIndex={recIndex})");
                    }
                    else
                    {
                        flight.EnterWatchMode(recIndex);
                        ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' watch started via icon click (recIndex={recIndex})");
                    }
                }, dismissOnSelect: true)
            };

            // Anchors at (0.5,0.5) = screen center. SpawnPopupDialog forces
            // localPosition=zero, placing the dialog at center. We then offset
            // via CanvasUtil.ScreenToUISpacePos which converts screen pixels to
            // canvas-center-relative coordinates (matching the 0.5/0.5 anchor).
            currentGhostMenu = PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new MultiOptionDialog("GhostIconMenu", "", vesselName,
                    HighLogic.UISkin, 160f, options),
                persistAcrossScenes: false, skin: HighLogic.UISkin);

            // Reposition to mouse cursor using KSP's canvas coordinate conversion
            if (currentGhostMenu != null)
            {
                var rt = currentGhostMenu.GetComponent<RectTransform>();
                if (rt != null && rt.parent != null)
                {
                    // ScreenToUISpacePos converts screen pixels to canvas-local coords
                    // where (0,0) = canvas center — matches our (0.5,0.5) anchor
                    bool zPositive;
                    var canvasRect = rt.parent.GetComponent<RectTransform>()
                                    ?? rt.root.GetComponent<RectTransform>();
                    if (canvasRect != null)
                    {
                        Vector3 uiPos = CanvasUtil.ScreenToUISpacePos(
                            Input.mousePosition, canvasRect, out zPositive);
                        rt.localPosition = uiPos;
                        ParsekLog.Verbose("GhostMap",
                            $"Popup repositioned: screen=({Input.mousePosition.x:F0},{Input.mousePosition.y:F0}) " +
                            $"canvas=({uiPos.x:F0},{uiPos.y:F0}) canvasSize=({canvasRect.sizeDelta.x:F0},{canvasRect.sizeDelta.y:F0})");
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
            if (flight != null)
            {
                flight.EnterWatchMode(recIndex);
                ParsekLog.Info("GhostMap",
                    $"Redirected SetActiveVessel to watch mode for ghost '{v.vesselName}' recording #{recIndex}");
            }

            // Focus the map camera on the ghost vessel (#193)
            if (PlanetariumCamera.fetch != null && MapView.MapIsEnabled && v.mapObject != null)
            {
                PlanetariumCamera.fetch.SetTarget(v.mapObject);
                ParsekLog.Info("GhostMap", $"Focused map camera on ghost '{v.vesselName}'");
            }

            return false;
        }
    }

    /// <summary>
    /// Prevents ghost vessels from appearing in OrbitTargeter's "Set as Target"
    /// popup (the second click path in map view). OrbitTargeter.TargetCastNodes
    /// checks MapNode hover state for all orbits and creates a MapContextMenu
    /// when it detects a hovered vessel. This postfix nulls the result for ghost
    /// vessels so OrbitTargeter never creates the default KSP target popup for them.
    /// Fixes: KSP's default popup appearing alongside Parsek's ghost menu when
    /// the ghost is in a different SOI from the camera's reference body (#192).
    /// </summary>
    /// <summary>
    /// Prevents ghost vessels from appearing in OrbitTargeter's target popup.
    /// OrbitTargeter.TargetCastNodes is a private method that returns OrbitDriver
    /// and has an out OrbitCastHit parameter. We null the return for ghost vessels
    /// so OrbitTargeter never creates the default KSP target popup for them.
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
