using HarmonyLib;
using UnityEngine;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony postfix on VesselPrecalculate.CalculatePhysicsStats().
    /// Fires every physics frame for each vessel, giving us frame-precise
    /// recording instead of the old InvokeRepeating timer.
    /// </summary>
    [HarmonyPatch(typeof(VesselPrecalculate),
        nameof(VesselPrecalculate.CalculatePhysicsStats))]
    internal static class PhysicsFramePatch
    {
        /// <summary>
        /// Set by FlightRecorder.StartRecording(), cleared on stop.
        /// Null when not recording.
        /// </summary>
        internal static FlightRecorder ActiveRecorder;
        private static FlightRecorder lastObservedRecorder;

        static void Postfix(VesselPrecalculate __instance)
        {
            if (ActiveRecorder != lastObservedRecorder)
            {
                if (ActiveRecorder == null)
                    ParsekLog.Info("PhysicsPatch", "Active recorder cleared");
                else
                    ParsekLog.Info("PhysicsPatch", "Active recorder attached");
                lastObservedRecorder = ActiveRecorder;
            }

            if (ActiveRecorder == null)
                return;

            // VesselPrecalculate.vessel is protected; resolve the vessel
            // via the GameObject instead.
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                ParsekLog.VerboseRateLimited("PhysicsPatch", "active-vessel-null",
                    "Skipping physics callback: active vessel is null", 5.0);
                return;
            }

            if (__instance.gameObject != v.gameObject)
            {
                ParsekLog.VerboseRateLimited("PhysicsPatch", "non-active-vessel",
                    "Skipping physics callback: patch fired for non-active vessel", 5.0);
                return;
            }

            ActiveRecorder.OnPhysicsFrame(v);
        }
    }
}
