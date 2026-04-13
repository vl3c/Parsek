using System.Collections;
using UnityEngine;

namespace Parsek.InGameTests.Helpers
{
    /// <summary>
    /// Utility methods for in-game quickload-resume tests (#269).
    /// Wraps KSP's quicksave/quickload APIs and provides polling coroutines
    /// for waiting on scene transitions and recording state.
    /// </summary>
    internal static class QuickloadResumeHelpers
    {
        /// <summary>
        /// Triggers a quicksave to the default "quicksave" slot.
        /// </summary>
        internal static void TriggerQuicksave()
        {
            string saveName = HighLogic.SaveFolder;
            string result = GamePersistence.SaveGame("quicksave", saveName, SaveMode.OVERWRITE);
            InGameAssert.IsTrue(!string.IsNullOrEmpty(result),
                $"TriggerQuicksave failed for '{saveName}/quicksave'");
            ParsekLog.Info("TestHelper",
                $"TriggerQuicksave: saved to '{saveName}/quicksave' ({result})");
        }

        /// <summary>
        /// Triggers a quickload from the default "quicksave" slot.
        /// This destroys the current ParsekFlight instance and creates a new one
        /// after the scene reload. Callers MUST re-query ParsekFlight.Instance
        /// after yielding past this call.
        /// </summary>
        internal static void TriggerQuickload()
        {
            string saveName = HighLogic.SaveFolder;
            Game game = GamePersistence.LoadGame("quicksave", saveName, true, false);
            InGameAssert.IsNotNull(game,
                $"TriggerQuickload failed: LoadGame returned null for '{saveName}/quicksave'");

            HighLogic.CurrentGame = game;
            HighLogic.LoadScene(GameScenes.FLIGHT);
            ParsekLog.Info("TestHelper",
                $"TriggerQuickload: loading '{saveName}/quicksave' via LoadScene(FLIGHT)");
        }

        /// <summary>
        /// Waits until FlightGlobals.ready is true and HighLogic.LoadedScene matches
        /// the target scene, or until timeout.
        /// </summary>
        internal static IEnumerator WaitForFlightReady(int previousFlightInstanceId, float timeoutSeconds = 10f)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                bool replacedFlight = flight != null
                    && (previousFlightInstanceId <= 0
                        || flight.GetInstanceID() != previousFlightInstanceId);
                if (HighLogic.LoadedScene == GameScenes.FLIGHT
                    && FlightGlobals.ready
                    && replacedFlight)
                    yield break;
                yield return null;
            }
            string activeVesselName = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.vesselName
                : "null";
            var timedOutFlight = ParsekFlight.Instance;
            InGameAssert.IsTrue(false,
                $"WaitForFlightReady timed out after {timeoutSeconds:F0}s " +
                $"(scene={HighLogic.LoadedScene}, flightReady={FlightGlobals.ready}, activeVessel={activeVesselName}, " +
                $"parsekFlight={(timedOutFlight != null ? timedOutFlight.GetInstanceID().ToString() : "null")}, " +
                $"expectedDifferentFrom={previousFlightInstanceId})");
        }

        /// <summary>
        /// Waits until ParsekFlight has an active recording (IsRecording == true),
        /// or until timeout. Must be called AFTER WaitForFlightReady.
        /// </summary>
        internal static IEnumerator WaitForActiveRecording(float timeoutSeconds = 10f)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (ParsekFlight.Instance?.IsRecording == true)
                    yield break;
                yield return null;
            }
            var flight = ParsekFlight.Instance;
            string activeRecId = flight?.ActiveTreeForSerialization?.ActiveRecordingId ?? "null";
            InGameAssert.IsTrue(false,
                $"WaitForActiveRecording timed out after {timeoutSeconds:F0}s " +
                $"(scene={HighLogic.LoadedScene}, flightReady={FlightGlobals.ready}, " +
                $"parsekFlight={(flight != null)}, isRecording={flight?.IsRecording == true}, " +
                $"hasActiveTree={flight?.HasActiveTree == true}, activeRecordingId={activeRecId})");
        }
    }
}
