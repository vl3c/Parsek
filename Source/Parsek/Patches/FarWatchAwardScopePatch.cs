using System;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Stamps the host of a vessel milestone (docs/dev/design-far-watch.md 10.3): every vessel
    /// node calls <c>ProgressNode.CrewSensitiveComplete</c> with its host just before it
    /// completes and pays in the same call. Stamped only while a camera drone exists.
    /// </summary>
    [HarmonyPatch]
    internal static class FarWatchAwardScopeVesselPatch
    {
        static MethodBase TargetMethod()
        {
            MethodBase m = AccessTools.DeclaredMethod(typeof(ProgressNode), "CrewSensitiveComplete",
                new[] { typeof(Vessel) });
            if (m == null)
                ParsekLog.Warn(FarWatchAwardScope.Tag,
                    "ProgressNode.CrewSensitiveComplete(Vessel) not found - a drone-caused milestone cannot be attributed");
            return m;
        }

        static void Prefix(ProgressNode __instance, Vessel __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchAwardScopeVesselPatch.Prefix")) return; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists || __instance == null || __0 == null)
                return;
            try
            {
                FarWatchAwardScope.Stamp(__instance, __0.persistentId, __0.id,
                    FarWatchAwardScope.ReadFrameCountForStamp());
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(FarWatchAwardScope.Tag, "award stamp (Vessel) threw: " + ex.Message);
            }
        }
    }

    [HarmonyPatch]
    internal static class FarWatchAwardScopeProtoVesselPatch
    {
        static MethodBase TargetMethod()
        {
            MethodBase m = AccessTools.DeclaredMethod(typeof(ProgressNode), "CrewSensitiveComplete",
                new[] { typeof(ProtoVessel) });
            if (m == null)
                ParsekLog.Warn(FarWatchAwardScope.Tag,
                    "ProgressNode.CrewSensitiveComplete(ProtoVessel) not found - a drone-caused milestone cannot be attributed");
            return m;
        }

        static void Prefix(ProgressNode __instance, ProtoVessel __0)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchAwardScopeProtoVesselPatch.Prefix")) return; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists || __instance == null || __0 == null)
                return;
            try
            {
                FarWatchAwardScope.Stamp(__instance, __0.persistentId, __0.vesselID,
                    FarWatchAwardScope.ReadFrameCountForStamp());
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(FarWatchAwardScope.Tag, "award stamp (ProtoVessel) threw: " + ex.Message);
            }
        }
    }
}
