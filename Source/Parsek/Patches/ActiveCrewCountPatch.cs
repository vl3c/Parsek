using System;
using System.Reflection;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Postfix on stock <c>KerbalRoster.GetActiveCrewCount()</c>: a held owner and the
    /// active stand-in Parsek generated for his seat count as one active kerbal
    /// (<see cref="StandInSeatCount"/>). The postfix only gathers inputs; the decision is
    /// <see cref="StandInSeatCount.CollectSeatSharingPairs"/>.
    /// </summary>
    [HarmonyPatch]
    internal static class ActiveCrewCountPatch
    {
        static MethodBase TargetMethod()
        {
            var method = ResolveTargetMethodForTesting();
            if (method == null)
                ParsekLog.Warn(StandInSeatCount.Tag,
                    "KerbalRoster.GetActiveCrewCount() not found - a stand-in and the kerbal it stands in for " +
                    "will count as two active kerbals. Harmony will skip this patch (caught by ParsekHarmony try/catch).");
            return method;
        }

        internal static MethodInfo ResolveTargetMethodForTesting()
        {
            var method = AccessTools.Method(typeof(KerbalRoster), nameof(KerbalRoster.GetActiveCrewCount), Type.EmptyTypes);
            return method != null && method.ReturnType == typeof(int) ? method : null;
        }

        static void Postfix(KerbalRoster __instance, ref int __result)
        {
            if (ParsekGameModeGate.CheckInert("ActiveCrewCountPatch.Postfix")) return; // S9 game-mode gate
            try
            {
                __result = StandInSeatCount.AdjustLiveCount(__instance, __result);
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited(StandInSeatCount.Tag, "active-crew-count-failed",
                    "Active crew count adjustment failed, stock count kept (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}
