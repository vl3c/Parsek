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
        /// Set by ParsekFlight when a recording tree is active.
        /// Null when no tree is active. Enables background physics recording
        /// for the tree members no active recorder records: the non-active ones, and
        /// the active one while the post-switch watch waits to promote it.
        /// </summary>
        internal static BackgroundRecorder BackgroundRecorderInstance;
        private static BackgroundRecorder lastObservedBackgroundRecorder;

        private static readonly Stopwatch recordingStopwatch = new Stopwatch();

        /// <summary>
        /// Whether the ACTIVE vessel's physics frame also goes to the background recorder: the
        /// player switched to it and the post-switch watch still waits for a modification. A
        /// member of the live tree stays a background recording until that trigger promotes it,
        /// and nothing else samples it meanwhile; an outsider is in no BackgroundMap, so
        /// <see cref="BackgroundRecorder.OnBackgroundPhysicsFrame"/> returns for it and it stays
        /// unrecorded. An attached active recorder owns the vessel.
        /// </summary>
        internal static bool ShouldSampleActiveVesselInBackground(
            bool activeRecorderAttached,
            bool postSwitchWatchArmedForVessel)
        {
            return !activeRecorderAttached && postSwitchWatchArmedForVessel;
        }

        static void Postfix(VesselPrecalculate __instance)
        {
            if (ParsekGameModeGate.CheckInert("PhysicsFramePatch.Postfix")) return; // S9 game-mode gate
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

            if (BackgroundRecorderInstance != lastObservedBackgroundRecorder)
            {
                if (BackgroundRecorderInstance == null)
                    ParsekLog.Info("PhysicsPatch", "Background recorder cleared");
                else
                    ParsekLog.Info("PhysicsPatch", "Background recorder attached");
                lastObservedBackgroundRecorder = BackgroundRecorderInstance;
            }

            if (ActiveRecorder == null
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
                    // Before the watch: the frame whose change promotes the vessel is still the
                    // background recorder's, and promotion seeds no events for it.
                    if (BackgroundRecorderInstance != null
                        && ShouldSampleActiveVesselInBackground(
                            ActiveRecorder != null,
                            flight.IsPostSwitchAutoRecordArmedForPid(v.persistentId)))
                    {
                        ParsekLog.VerboseRateLimited("PhysicsPatch", "watched-active-background-sample",
                            $"Background recorder keeps the watched active vessel until promotion: pid={v.persistentId}",
                            30.0);
                        BackgroundRecorderInstance.OnBackgroundPhysicsFrame(v);
                    }

                    flight.OnPostSwitchAutoRecordPhysicsFrame(v);
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
