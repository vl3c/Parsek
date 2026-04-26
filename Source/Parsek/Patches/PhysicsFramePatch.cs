using System.Diagnostics;
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
        /// Set by ParsekFlight.StartGloopsRecording(), cleared on stop.
        /// Null when not Gloops-recording. Runs in parallel with ActiveRecorder.
        /// </summary>
        internal static FlightRecorder GloopsRecorderInstance;
        private static FlightRecorder lastObservedGloopsRecorder;

        /// <summary>
        /// Set by ParsekFlight when a recording tree is active.
        /// Null when no tree is active. Enables background physics recording
        /// for non-active vessels in the tree.
        /// </summary>
        internal static BackgroundRecorder BackgroundRecorderInstance;
        private static BackgroundRecorder lastObservedBackgroundRecorder;

        private static readonly Stopwatch recordingStopwatch = new Stopwatch();

        static void Postfix(VesselPrecalculate __instance)
        {
            ParsekFlight flight = ParsekFlight.Instance;
            bool hasPostSwitchAutoRecordWatch =
                flight != null && flight.HasArmedPostSwitchAutoRecordWatch;

            if (ActiveRecorder != lastObservedRecorder)
            {
                if (ActiveRecorder == null)
                    ParsekLog.Info("PhysicsPatch", "Active recorder cleared");
                else
                    ParsekLog.Info("PhysicsPatch", "Active recorder attached");
                lastObservedRecorder = ActiveRecorder;
            }

            if (GloopsRecorderInstance != lastObservedGloopsRecorder)
            {
                if (GloopsRecorderInstance == null)
                    ParsekLog.Info("PhysicsPatch", "Gloops recorder cleared");
                else
                    ParsekLog.Info("PhysicsPatch", "Gloops recorder attached");
                lastObservedGloopsRecorder = GloopsRecorderInstance;
            }

            if (BackgroundRecorderInstance != lastObservedBackgroundRecorder)
            {
                if (BackgroundRecorderInstance == null)
                    ParsekLog.Info("PhysicsPatch", "Background recorder cleared");
                else
                    ParsekLog.Info("PhysicsPatch", "Background recorder attached");
                lastObservedBackgroundRecorder = BackgroundRecorderInstance;
            }

            if (ActiveRecorder == null
                && GloopsRecorderInstance == null
                && BackgroundRecorderInstance == null
                && !hasPostSwitchAutoRecordWatch)
                return;

            // VesselPrecalculate.vessel is protected; resolve the vessel
            // via the GameObject instead.
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
            {
                if (ActiveRecorder != null)
                {
                    ParsekLog.VerboseRateLimited("PhysicsPatch", "active-vessel-null-recorder",
                        "Skipping physics callback: active vessel is null", 5.0);
                }

                if (hasPostSwitchAutoRecordWatch)
                {
                    ParsekLog.VerboseRateLimited("PhysicsPatch", "active-vessel-null-watch",
                        "Skipping post-switch auto-record watch: active vessel is null", 5.0);
                }
            }

            if (v != null && __instance.gameObject == v.gameObject)
            {
                // Active vessel recording path
                if (ActiveRecorder != null)
                {
                    recordingStopwatch.Restart();
                    ActiveRecorder.OnPhysicsFrame(v);
                    recordingStopwatch.Stop();

                    long elapsedUs = recordingStopwatch.ElapsedTicks * 1000000L / Stopwatch.Frequency;
                    DiagnosticsState.recordingBudget.totalMicroseconds = elapsedUs;

                    DiagnosticsComputation.CheckRecordingBudgetThreshold(elapsedUs, v.vesselName);
                }

                if (hasPostSwitchAutoRecordWatch)
                {
                    flight.OnPostSwitchAutoRecordPhysicsFrame(v);
                }

                // Gloops recorder runs in parallel with the active recorder on the same vessel
                if (GloopsRecorderInstance != null)
                {
                    GloopsRecorderInstance.OnPhysicsFrame(v);
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
