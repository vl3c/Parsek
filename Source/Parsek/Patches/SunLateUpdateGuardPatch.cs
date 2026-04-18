using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Defends stock Sun.LateUpdate against a destroyed camera target after ghost cleanup.
    /// Without this, a stale watched-ghost transform can cascade through stock camera state
    /// into per-frame NullReferenceExceptions until the player force-closes KSP.
    /// </summary>
    [HarmonyPatch(typeof(Sun), "LateUpdate")]
    internal static class SunLateUpdateGuardPatch
    {
        private static bool warnedMissingTarget;

        internal static bool ShouldSkipLateUpdate(Object target)
        {
            return target == null;
        }

        internal static bool ShouldEmitMissingTargetWarning(bool alreadyWarned)
        {
            return !alreadyWarned;
        }

        internal static void ResetForTesting()
        {
            warnedMissingTarget = false;
        }

        static bool Prefix(Sun __instance)
        {
            Sun sun = Sun.Instance ?? __instance;
            if (sun == null)
                return true;

            Object target = sun.target;
            if (!ShouldSkipLateUpdate(target))
                return true;

            if (ShouldEmitMissingTargetWarning(warnedMissingTarget))
            {
                warnedMissingTarget = true;
                ParsekLog.Warn("CameraFollow",
                    "Suppressed Sun.LateUpdate because Sun.target was null/destroyed after ghost cleanup");
            }

            return false;
        }
    }
}
