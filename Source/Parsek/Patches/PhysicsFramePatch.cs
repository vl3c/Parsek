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

        static void Postfix(VesselPrecalculate __instance)
        {
            if (ActiveRecorder == null) return;

            // VesselPrecalculate.vessel is protected; resolve the vessel
            // via the GameObject instead.
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            if (__instance.gameObject != v.gameObject) return;

            ActiveRecorder.OnPhysicsFrame(v);
        }
    }
}
