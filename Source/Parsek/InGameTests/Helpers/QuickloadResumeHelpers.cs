using System.Collections;
using System.IO;
using UnityEngine;

namespace Parsek.InGameTests.Helpers
{
    /// <summary>
    /// Utility methods for in-game quickload-resume tests (#269).
    /// Wraps KSP's quicksave and stock programmatic quickload path, then
    /// provides polling coroutines for waiting on scene transitions and
    /// recording state.
    /// </summary>
    internal static class QuickloadResumeHelpers
    {
        private const string QuicksaveSlotName = "quicksave";

        /// <summary>
        /// Triggers a quicksave to the default "quicksave" slot.
        /// </summary>
        internal static void TriggerQuicksave()
        {
            string saveName = HighLogic.SaveFolder;
            string result = GamePersistence.SaveGame(QuicksaveSlotName, saveName, SaveMode.OVERWRITE);
            InGameAssert.IsTrue(!string.IsNullOrEmpty(result),
                $"TriggerQuicksave failed for '{saveName}/{QuicksaveSlotName}'");

            string quicksavePath = GetQuicksavePath(saveName);
            long quicksaveBytes = AssertQuicksaveFileReady(
                quicksavePath, saveName, caller: "TriggerQuicksave");
            ParsekLog.Info("TestHelper",
                $"TriggerQuicksave: saved to '{saveName}/{QuicksaveSlotName}' ({result}, {quicksaveBytes} bytes)");
        }

        /// <summary>
        /// Triggers a quickload from the default "quicksave" slot.
        /// Uses KSP's stock programmatic flight-resume path
        /// (FlightDriver.StartAndFocusVessel) instead of directly swapping the
        /// current game and calling LoadScene(FLIGHT).
        /// This destroys the current ParsekFlight instance and creates a new one
        /// after the scene reload. Callers MUST re-query ParsekFlight.Instance
        /// after yielding past this call.
        /// </summary>
        internal static void TriggerQuickload()
        {
            string saveName = HighLogic.SaveFolder;
            string quicksavePath = GetQuicksavePath(saveName);
            AssertQuicksaveFileReady(quicksavePath, saveName, caller: "TriggerQuickload");

            Game game = GamePersistence.LoadGame(QuicksaveSlotName, saveName, true, false);
            InGameAssert.IsNotNull(game,
                $"TriggerQuickload failed: LoadGame returned null for '{saveName}/{QuicksaveSlotName}'");
            InGameAssert.IsNotNull(game.flightState,
                $"TriggerQuickload failed: loaded game for '{saveName}/{QuicksaveSlotName}' had null flightState");

            int activeVesselIdx = game.flightState.activeVesselIdx;
            InGameAssert.IsTrue(activeVesselIdx >= 0,
                $"TriggerQuickload failed: loaded game for '{saveName}/{QuicksaveSlotName}' had invalid activeVesselIdx={activeVesselIdx}");

            FlightDriver.StartAndFocusVessel(game, activeVesselIdx);
            ParsekLog.Info("TestHelper",
                $"TriggerQuickload: loading '{saveName}/{QuicksaveSlotName}' via FlightDriver.StartAndFocusVessel(activeVesselIdx={activeVesselIdx})");
        }

        private static string GetQuicksavePath(string saveName)
        {
            return Path.Combine(
                KSPUtil.ApplicationRootPath ?? string.Empty,
                "saves",
                saveName ?? string.Empty,
                QuicksaveSlotName + ".sfs");
        }

        private static long AssertQuicksaveFileReady(string quicksavePath, string saveName, string caller)
        {
            InGameAssert.IsTrue(!string.IsNullOrEmpty(saveName),
                $"{caller} failed: HighLogic.SaveFolder was null/empty");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(KSPUtil.ApplicationRootPath),
                $"{caller} failed: KSPUtil.ApplicationRootPath was null/empty");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(quicksavePath),
                $"{caller} failed: quicksave path was null/empty for save '{saveName}'");
            InGameAssert.IsTrue(File.Exists(quicksavePath),
                $"{caller} failed: quicksave file missing at '{quicksavePath}'");

            long quicksaveBytes = new FileInfo(quicksavePath).Length;
            InGameAssert.IsTrue(quicksaveBytes > 0,
                $"{caller} failed: quicksave file '{quicksavePath}' was empty");
            return quicksaveBytes;
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
