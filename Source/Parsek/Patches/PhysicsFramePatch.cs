using System.Diagnostics;
using System.Globalization;
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

        /// <summary>
        /// Set by ParsekFlight when a recording tree is active.
        /// Null when no tree is active. Enables background physics recording
        /// for non-active vessels in the tree.
        /// </summary>
        internal static BackgroundRecorder BackgroundRecorderInstance;

        private static readonly Stopwatch recordingStopwatch = new Stopwatch();

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

            if (ActiveRecorder == null && BackgroundRecorderInstance == null)
                return;

            // VesselPrecalculate.vessel is protected; resolve the vessel
            // via the GameObject instead.
            Vessel v = FlightGlobals.ActiveVessel;

            // Active vessel recording path
            if (ActiveRecorder != null)
            {
                if (v == null)
                {
                    ParsekLog.VerboseRateLimited("PhysicsPatch", "active-vessel-null",
                        "Skipping physics callback: active vessel is null", 5.0);
                }
                else if (__instance.gameObject == v.gameObject)
                {
                    recordingStopwatch.Restart();
                    ActiveRecorder.OnPhysicsFrame(v);
                    recordingStopwatch.Stop();

                    long elapsedUs = recordingStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
                    DiagnosticsState.recordingBudget.totalMicroseconds = elapsedUs;

                    double totalMs = elapsedUs / 1000.0;
                    if (totalMs > 4.0)
                    {
                        ParsekLog.WarnRateLimited("Diagnostics", "recording-budget",
                            string.Format(CultureInfo.InvariantCulture,
                                "Recording frame exceeded budget: {0:F2}ms for vessel \"{1}\"",
                                totalMs, v.vesselName),
                            30.0);
                    }
                }
            }

            // Background physics recording for loaded vessels in tree
            if (BackgroundRecorderInstance != null && __instance.gameObject != null)
            {
                Vessel bgVessel = __instance.gameObject.GetComponent<Vessel>();
                if (bgVessel != null && bgVessel != v)
                {
                    BackgroundRecorderInstance.OnBackgroundPhysicsFrame(bgVessel);
                }
            }
        }
    }
}
