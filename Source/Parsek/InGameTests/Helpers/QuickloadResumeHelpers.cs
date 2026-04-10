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
            GamePersistence.SaveGame("quicksave", saveName, SaveMode.OVERWRITE);
            ParsekLog.Info("TestHelper",
                $"TriggerQuicksave: saved to '{saveName}/quicksave'");
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
            if (game == null)
            {
                ParsekLog.Warn("TestHelper",
                    $"TriggerQuickload: LoadGame returned null for '{saveName}/quicksave'");
                return;
            }

            HighLogic.CurrentGame = game;
            HighLogic.LoadScene(GameScenes.FLIGHT);
            ParsekLog.Info("TestHelper",
                $"TriggerQuickload: loading '{saveName}/quicksave' via LoadScene(FLIGHT)");
        }

        /// <summary>
        /// Waits until FlightGlobals.ready is true and HighLogic.LoadedScene matches
        /// the target scene, or until timeout.
        /// </summary>
        internal static IEnumerator WaitForFlightReady(float timeoutSeconds = 10f)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                if (HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready)
                    yield break;
                yield return null;
            }
            ParsekLog.Warn("TestHelper",
                $"WaitForFlightReady: timed out after {timeoutSeconds:F0}s");
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
            ParsekLog.Warn("TestHelper",
                $"WaitForActiveRecording: timed out after {timeoutSeconds:F0}s");
        }
    }
}
