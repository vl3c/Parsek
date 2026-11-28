using System;
using System.Reflection;
using HarmonyLib;

namespace Parsek
{
    /// <summary>
    /// Stock's on-rails warp limit read at the watched ghost instead of the drone
    /// (design-far-watch.md 8.4): while a far watch owns the warp, the private
    /// <c>TimeWarp.getMaxOnRailsRateIdx</c> answers from <see cref="FarWatchWarpLimits.EvaluateStock"/>
    /// over the ghost's state (the ship's in a hold watch). Inert outside a far watch; any
    /// exception falls through to stock's own method.
    /// </summary>
    [HarmonyPatch]
    internal static class FarWatchWarpLimitPatch
    {
        private static AccessTools.FieldRef<TimeWarp, double> minPeAllowedRef;

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(TimeWarp), "getMaxOnRailsRateIdx",
                new[] { typeof(int), typeof(bool), typeof(ClearToSaveStatus).MakeByRefType() });
        }

        static bool Prefix(TimeWarp __instance, int tgtRateIdx, bool lookAhead, ref ClearToSaveStatus reason, ref int __result)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchWarpLimitPatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists)
                return true;
            try
            {
                FarWatchController fw = ParsekFlight.Instance != null ? ParsekFlight.Instance.FarWatch : null;
                FarWatchWarpResult r;
                if (fw == null || !fw.TryEvaluateStockWarpLimit(__instance, tgtRateIdx, lookAhead, out r))
                    return true;
                __result = r.RateIndex;
                reason = r.Reason;
                if (minPeAllowedRef == null)
                    minPeAllowedRef = AccessTools.FieldRefAccess<TimeWarp, double>("minPeAllowed");
                minPeAllowedRef(__instance) = r.MinPeAllowed;
                return false;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("FarWatchWarp", "warp-limit-patch-threw",
                    "warp-limit patch threw " + ex.GetType().Name + "; stock's limit applies", 5.0);
                return true;
            }
        }
    }

    /// <summary>
    /// Stock's HIGH / LOW warp-mode switch read at the watched ghost (8.4): stock's
    /// <c>TimeWarp.Update</c> switches the mode from the drone's state, so a switch that
    /// disagrees with the ghost's is refused while a ghost is followed. Parsek's own switch holds
    /// <see cref="FarWatchController.ModeSwitchTokenHeld"/>; a switch with the modifier key held
    /// (the player's own physics-warp request) is left alone.
    /// </summary>
    [HarmonyPatch]
    internal static class FarWatchWarpModePatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(TimeWarp), "setMode", new[] { typeof(TimeWarp.Modes) });
        }

        static bool Prefix(TimeWarp.Modes mode, ref bool __result)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchWarpModePatch.Prefix")) return true; // S9 game-mode gate
            if (!CameraDroneIdentity.Exists)
                return true;
            try
            {
                FarWatchController fw = ParsekFlight.Instance != null ? ParsekFlight.Instance.FarWatch : null;
                if (fw == null || !fw.ShouldRefuseStockModeSwitch(mode))
                    return true;
                if (GameSettings.MODIFIER_KEY != null && GameSettings.MODIFIER_KEY.GetKey(false))
                    return true;
                __result = false;
                ParsekLog.InfoRateLimited("FarWatchWarp", "warp-mode-refused-" + mode,
                    "stock warp mode switch to " + mode + " refused: the watched ghost decides the mode", 5.0);
                return false;
            }
            catch (Exception ex)
            {
                ParsekLog.WarnRateLimited("FarWatchWarp", "warp-mode-patch-threw",
                    "warp-mode patch threw " + ex.GetType().Name + "; stock's switch applies", 5.0);
                return true;
            }
        }
    }
}
