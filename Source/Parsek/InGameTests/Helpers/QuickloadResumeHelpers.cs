using System;
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
        /// Triggers a stock save to the given slot name. Defaults to the
        /// standard "quicksave" slot used by the quickload-resume tests.
        /// </summary>
        internal static void TriggerQuicksave(string slotName = QuicksaveSlotName)
        {
            string saveName = HighLogic.SaveFolder;
            InGameAssert.IsTrue(!string.IsNullOrEmpty(slotName),
                "TriggerQuicksave failed: slotName was null/empty");

            string result = GamePersistence.SaveGame(slotName, saveName, SaveMode.OVERWRITE);
            InGameAssert.IsTrue(!string.IsNullOrEmpty(result),
                $"TriggerQuicksave failed for '{saveName}/{slotName}'");

            string quicksavePath = GetSavePath(saveName, slotName);
            long quicksaveBytes = EnsureQuicksaveFileReady(
                quicksavePath, saveName, caller: "TriggerQuicksave");
            ParsekLog.Info("TestHelper",
                $"TriggerQuicksave: saved to '{saveName}/{slotName}' ({result}, {quicksaveBytes} bytes)");
        }

        /// <summary>
        /// Triggers a stock load from the given slot name. Defaults to the
        /// standard "quicksave" slot used by the quickload-resume tests.
        /// Uses KSP's stock programmatic flight-resume backend
        /// (FlightDriver.StartAndFocusVessel) instead of directly swapping the
        /// current game and calling LoadScene(FLIGHT).
        /// This destroys the current ParsekFlight instance and creates a new one
        /// after the scene reload. Callers MUST re-query ParsekFlight.Instance
        /// after yielding past this call.
        /// </summary>
        internal static void TriggerQuickload(string slotName = QuicksaveSlotName)
        {
            string saveName = HighLogic.SaveFolder;
            InGameAssert.IsTrue(!string.IsNullOrEmpty(slotName),
                "TriggerQuickload failed: slotName was null/empty");

            string quicksavePath = GetSavePath(saveName, slotName);
            EnsureQuicksaveFileReady(quicksavePath, saveName, caller: "TriggerQuickload");

            Game game = GamePersistence.LoadGame(slotName, saveName, true, false);
            InGameAssert.IsNotNull(game,
                $"TriggerQuickload failed: LoadGame returned null for '{saveName}/{slotName}'");
            InGameAssert.IsNotNull(game.flightState,
                $"TriggerQuickload failed: loaded game for '{saveName}/{slotName}' had null flightState");
            InGameAssert.IsNotNull(game.flightState.protoVessels,
                $"TriggerQuickload failed: loaded game for '{saveName}/{slotName}' had null protoVessels");

            int activeVesselIdx = game.flightState.activeVesselIdx;
            if (activeVesselIdx < 0 || activeVesselIdx >= game.flightState.protoVessels.Count)
            {
                InGameAssert.Skip(
                    $"TriggerQuickload skipped: loaded quicksave '{saveName}/{slotName}' had invalid activeVesselIdx={activeVesselIdx} " +
                    $"for protoVessels.Count={game.flightState.protoVessels.Count}");
            }

            FlightDriver.StartAndFocusVessel(game, activeVesselIdx);
            ParsekLog.Info("TestHelper",
                $"TriggerQuickload: loading '{saveName}/{slotName}' via FlightDriver.StartAndFocusVessel(activeVesselIdx={activeVesselIdx})");
        }

        private static string GetSavePath(string saveName, string slotName)
        {
            return Path.Combine(
                KSPUtil.ApplicationRootPath ?? string.Empty,
                "saves",
                saveName ?? string.Empty,
                (slotName ?? string.Empty) + ".sfs");
        }

        internal static void TryDeleteSaveSlot(string slotName)
        {
            if (string.IsNullOrEmpty(slotName)
                || string.IsNullOrEmpty(HighLogic.SaveFolder)
                || string.IsNullOrEmpty(KSPUtil.ApplicationRootPath))
            {
                return;
            }

            string savePath = GetSavePath(HighLogic.SaveFolder, slotName);
            if (!File.Exists(savePath))
                return;

            try
            {
                File.Delete(savePath);
                ParsekLog.Verbose("TestHelper",
                    $"Deleted temporary save slot '{HighLogic.SaveFolder}/{slotName}'");
            }
            catch (IOException ioEx)
            {
                ParsekLog.Warn("TestHelper",
                    $"Failed to delete temporary save slot '{HighLogic.SaveFolder}/{slotName}': {ioEx.Message}");
            }
            catch (UnauthorizedAccessException accessEx)
            {
                ParsekLog.Warn("TestHelper",
                    $"Failed to delete temporary save slot '{HighLogic.SaveFolder}/{slotName}': {accessEx.Message}");
            }
        }

        private static long EnsureQuicksaveFileReady(string quicksavePath, string saveName, string caller)
        {
            InGameAssert.IsTrue(!string.IsNullOrEmpty(saveName),
                $"{caller} failed: HighLogic.SaveFolder was null/empty");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(KSPUtil.ApplicationRootPath),
                $"{caller} failed: KSPUtil.ApplicationRootPath was null/empty");
            InGameAssert.IsTrue(!string.IsNullOrEmpty(quicksavePath),
                $"{caller} failed: quicksave path was null/empty for save '{saveName}'");
            if (!File.Exists(quicksavePath))
            {
                InGameAssert.Skip($"{caller} skipped: quicksave file missing at '{quicksavePath}'");
            }

            long quicksaveBytes = new FileInfo(quicksavePath).Length;
            if (quicksaveBytes <= 0)
            {
                InGameAssert.Skip($"{caller} skipped: quicksave file '{quicksavePath}' was empty");
            }
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
