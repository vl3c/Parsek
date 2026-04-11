using HarmonyLib;
using KSP.UI.Dialogs;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on FlightResultsDialog.Display to intercept the crash/mission
    /// report dialog. When autoMerge is off and a recording was just captured, shows
    /// the Parsek merge dialog first, then lets the KSP dialog through after the user
    /// makes a choice.
    /// </summary>
    [HarmonyPatch(typeof(FlightResultsDialog), nameof(FlightResultsDialog.Display))]
    internal static class FlightResultsPatch
    {
        /// <summary>
        /// When set, the next Display call is a replay after our merge dialog — let it through.
        /// </summary>
        internal static bool Bypass;

        /// <summary>
        /// Stored outcome message from the suppressed Display call, replayed after merge dialog.
        /// </summary>
        internal static string PendingOutcomeMsg;

        static bool Prefix(string outcomeMsg)
        {
            // Bypass flag: this is our replay call after the merge dialog — let KSP show its report
            if (Bypass)
            {
                Bypass = false;
                ParsekLog.Verbose("FlightResultsPatch", "Bypass active — letting FlightResultsDialog.Display through");
                return true;
            }

            // Only intercept when autoMerge is off
            if (ParsekScenario.IsAutoMerge)
                return true;

            // Only intercept when there's a pending tree or the recorder has pending data
            if (!RecordingStore.HasPendingTree && !HasActiveDestroyedRecording())
                return true;

            // Store the outcome message for later replay
            PendingOutcomeMsg = outcomeMsg;
            ParsekLog.Info("FlightResultsPatch",
                $"Intercepted FlightResultsDialog.Display — deferring until merge dialog completes (msg=\"{outcomeMsg}\")");

            // Suppress KSP's dialog — our merge dialog flow will handle showing it later
            return false;
        }

        /// <summary>
        /// Re-shows KSP's flight results dialog with the stored outcome message.
        /// Called from MergeDialog button callbacks after the user makes a choice.
        /// </summary>
        internal static void ReplayFlightResults()
        {
            if (string.IsNullOrEmpty(PendingOutcomeMsg))
            {
                ParsekLog.Verbose("FlightResultsPatch", "ReplayFlightResults: no pending message — skipping");
                return;
            }

            string msg = PendingOutcomeMsg;
            PendingOutcomeMsg = null;
            Bypass = true;
            ParsekLog.Info("FlightResultsPatch", $"Replaying FlightResultsDialog.Display (msg=\"{msg}\")");
            FlightResultsDialog.Display(msg);
        }

        /// <summary>
        /// Returns true if there is a pending outcome message that was suppressed
        /// and not yet replayed. Used as a safety net in OnFlightReady.
        /// </summary>
        internal static bool HasPendingResults()
        {
            return !string.IsNullOrEmpty(PendingOutcomeMsg);
        }

        /// <summary>
        /// Clears the pending outcome message without replaying it.
        /// Used on scene change to prevent stale crash reports from persisting.
        /// </summary>
        internal static void ClearPending()
        {
            if (!string.IsNullOrEmpty(PendingOutcomeMsg))
                ParsekLog.Info("FlightResultsPatch", $"Cleared pending results (msg=\"{PendingOutcomeMsg}\")");
            PendingOutcomeMsg = null;
        }

        /// <summary>
        /// Checks if the active recorder has a destroyed vessel recording in progress
        /// that hasn't been stashed yet (the split/coroutine path will stash it shortly).
        /// </summary>
        static bool HasActiveDestroyedRecording()
        {
            var recorder = PhysicsFramePatch.ActiveRecorder;
            if (recorder != null && recorder.VesselDestroyedDuringRecording)
                return true;
            return false;
        }
    }
}
