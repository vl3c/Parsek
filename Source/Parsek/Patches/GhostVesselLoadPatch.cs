using CommNet;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Prevents ghost vessel orbit lines from being clickable in map view (#172).
    /// Ghost orbit lines are visual-only — clicking them should not open the target
    /// popup, because a real vessel might share the same orbit and the click would
    /// be ambiguous. The ghost vessel ICON remains clickable via MapNode hover;
    /// OrbitTargeter.TargetCastNodes detects the icon hover and opens the normal
    /// "Set As Target / Switch To" popup. "Switch To" is redirected to watch mode
    /// by GhostVesselSwitchPatch.
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
    /// "Set As Target" and "Watch" options. Ghost orbit lines are non-clickable
    /// (GhostOrbitCastPatch), so the icon is the only interaction point.
    /// </summary>
    [HarmonyPatch(typeof(OrbitRendererBase), "objectNode_OnClick")]
    internal static class GhostIconClickPatch
    {
        static void Postfix(OrbitRendererBase __instance)
        {
            if (__instance.vessel == null) return;
            if (!GhostMapPresence.IsGhostMapVessel(__instance.vessel.persistentId)) return;
            if (!HighLogic.LoadedSceneIsFlight || !MapView.MapIsEnabled) return;

            Vessel v = __instance.vessel;
            int recIndex = GhostMapPresence.FindRecordingIndexByVesselPid(v.persistentId);
            string vesselName = v.vesselName ?? "Ghost";

            var options = new DialogGUIBase[]
            {
                new DialogGUIButton("Set As Target", () =>
                {
                    FlightGlobals.fetch.SetVesselTarget(v);
                    ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' set as target via icon click");
                }, dismissOnSelect: true),
                new DialogGUIButton("Watch", () =>
                {
                    var flight = ParsekFlight.Instance;
                    if (flight != null && recIndex >= 0)
                        flight.EnterWatchMode(recIndex);
                    else
                        ScreenMessages.PostScreenMessage(
                            $"<b>{vesselName}</b> is a ghost — it will materialize when its timeline reaches the spawn point.",
                            5f, ScreenMessageStyle.UPPER_CENTER);
                    ParsekLog.Info("GhostMap", $"Ghost '{vesselName}' watch requested via icon click (recIndex={recIndex})");
                }, dismissOnSelect: true)
            };

            // Position popup near mouse cursor (convert screen pos to normalized anchor)
            UnityEngine.Vector3 mousePos = UnityEngine.Input.mousePosition;
            float anchorX = mousePos.x / UnityEngine.Screen.width;
            float anchorY = mousePos.y / UnityEngine.Screen.height;
            var anchor = new UnityEngine.Vector2(anchorX, anchorY);

            PopupDialog.SpawnPopupDialog(
                anchor, anchor,
                new MultiOptionDialog("GhostIconMenu", "", vesselName,
                    HighLogic.UISkin, 160f, options),
                persistAcrossScenes: false, skin: HighLogic.UISkin);

            ParsekLog.Verbose("GhostMap",
                $"Ghost icon clicked: '{vesselName}' pid={v.persistentId} recIndex={recIndex}");
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
                UnityEngine.Object.Destroy(__instance);

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
    /// for the ghost's recording — following the ghost camera to its actual position.
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

            return false;
        }
    }
}
