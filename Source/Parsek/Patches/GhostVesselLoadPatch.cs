using HarmonyLib;

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
}
