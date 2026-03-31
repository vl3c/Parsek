using CommNet;
using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
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
    /// </summary>
    [HarmonyPatch(typeof(FlightGlobals), nameof(FlightGlobals.SetActiveVessel))]
    internal static class GhostVesselSwitchPatch
    {
        static bool Prefix(Vessel v)
        {
            if (v == null || !GhostMapPresence.IsGhostMapVessel(v.persistentId))
                return true;

            int recIndex = GhostMapPresence.FindRecordingIndexByVesselPid(v.persistentId);
            if (recIndex < 0)
            {
                ParsekLog.Verbose("GhostMap",
                    $"Blocked SetActiveVessel for ghost '{v.vesselName}' pid={v.persistentId} — no recording index found");
                return false;
            }

            var flight = Object.FindObjectOfType<ParsekFlight>();
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
