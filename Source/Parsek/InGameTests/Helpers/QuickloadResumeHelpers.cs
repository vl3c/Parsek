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
            // Splitting validation from commit lets the batch-baseline-restore
            // flow run its prep wipe BETWEEN them: validate the .sfs is
            // loadable first (cheap, non-destructive parse), then wipe
            // in-memory state knowing the load is committed-or-imminent,
            // then commit the scene change. Other callers retain the
            // bundled "validate + commit" semantics through this wrapper.
            ValidatedGameLoad load = LoadAndValidateGameForQuickload(slotName);
            CommitValidatedGameLoad(load);
        }

        /// <summary>
        /// Result of the non-destructive validation half of a quickload:
        /// the .sfs has been parsed, the active-vessel index is in range,
        /// and the scene change is ready to commit via
        /// <see cref="CommitValidatedGameLoad"/>. The <see cref="Game"/>
        /// reference is the one that <c>FlightDriver.StartAndFocusVessel</c>
        /// will adopt; do not mutate it between validation and commit.
        /// </summary>
        internal struct ValidatedGameLoad
        {
            public Game Game;
            public int ActiveVesselIdx;
            public string SaveName;
            public string SlotName;
        }

        /// <summary>
        /// Non-destructive half of <see cref="TriggerQuickload"/>: parses
        /// the .sfs, validates flight-state shape and active-vessel index,
        /// returns the loaded <see cref="Game"/> for a subsequent
        /// <see cref="CommitValidatedGameLoad"/>. KSP's
        /// <c>GamePersistence.LoadGame</c> does not swap the active game
        /// or trigger any scene change, so failure throws (or skips) before
        /// the caller has done anything irreversible. Used by
        /// <c>InGameTestRunner.RestoreBatchFlightBaselineCore</c> to gate
        /// the prep wipe of save-scoped in-memory stores -- the wipe only
        /// runs after this call returns successfully, so a missing /
        /// corrupt baseline slot can no longer leave RecordingStore +
        /// six other Parsek stores cleared without the new scene having
        /// loaded.
        /// </summary>
        internal static ValidatedGameLoad LoadAndValidateGameForQuickload(string slotName)
        {
            string saveName = HighLogic.SaveFolder;
            InGameAssert.IsTrue(!string.IsNullOrEmpty(slotName),
                "LoadAndValidateGameForQuickload failed: slotName was null/empty");

            string quicksavePath = GetSavePath(saveName, slotName);
            EnsureQuicksaveFileReady(quicksavePath, saveName, caller: "LoadAndValidateGameForQuickload");

            Game game = GamePersistence.LoadGame(slotName, saveName, true, false);
            InGameAssert.IsNotNull(game,
                $"LoadAndValidateGameForQuickload failed: LoadGame returned null for '{saveName}/{slotName}'");
            InGameAssert.IsNotNull(game.flightState,
                $"LoadAndValidateGameForQuickload failed: loaded game for '{saveName}/{slotName}' had null flightState");
            InGameAssert.IsNotNull(game.flightState.protoVessels,
                $"LoadAndValidateGameForQuickload failed: loaded game for '{saveName}/{slotName}' had null protoVessels");

            int activeVesselIdx = game.flightState.activeVesselIdx;
            if (activeVesselIdx < 0 || activeVesselIdx >= game.flightState.protoVessels.Count)
            {
                InGameAssert.Skip(
                    $"LoadAndValidateGameForQuickload skipped: loaded quicksave '{saveName}/{slotName}' had invalid activeVesselIdx={activeVesselIdx} " +
                    $"for protoVessels.Count={game.flightState.protoVessels.Count}");
            }

            return new ValidatedGameLoad
            {
                Game = game,
                ActiveVesselIdx = activeVesselIdx,
                SaveName = saveName,
                SlotName = slotName,
            };
        }

        /// <summary>
        /// Commit half of <see cref="TriggerQuickload"/>: hands the
        /// validated game to <c>FlightDriver.StartAndFocusVessel</c>,
        /// which schedules the FLIGHT scene change. After this returns
        /// the caller should yield to <see cref="WaitForFlightReady"/>;
        /// <c>OnLoad</c> on every ScenarioModule (including
        /// ParsekScenario, which rebuilds RecordingStore + all the
        /// save-scoped Parsek stores from the loaded game) fires during
        /// the scene transition.
        /// </summary>
        internal static void CommitValidatedGameLoad(ValidatedGameLoad load)
        {
            FlightDriver.StartAndFocusVessel(load.Game, load.ActiveVesselIdx);
            ParsekLog.Info("TestHelper",
                $"TriggerQuickload: loading '{load.SaveName}/{load.SlotName}' via FlightDriver.StartAndFocusVessel(activeVesselIdx={load.ActiveVesselIdx})");
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
        /// Waits until the reloaded FLIGHT scene has a new ParsekFlight instance,
        /// FlightGlobals.ready is true, and FlightGlobals.ActiveVessel is
        /// non-null, or until timeout. Pass 0 when there is no previous
        /// ParsekFlight instance.
        /// </summary>
        internal static IEnumerator WaitForFlightReady(int previousFlightInstanceId, float timeoutSeconds = 10f)
        {
            float deadline = Time.time + timeoutSeconds;
            while (Time.time < deadline)
            {
                var flight = ParsekFlight.Instance;
                int currentFlightInstanceId = flight != null ? flight.GetInstanceID() : 0;
                if (IsReloadedFlightReady(
                    HighLogic.LoadedScene,
                    FlightGlobals.ready,
                    FlightGlobals.ActiveVessel != null,
                    currentFlightInstanceId,
                    previousFlightInstanceId))
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

        internal static bool IsReloadedFlightReady(
            GameScenes loadedScene,
            bool flightGlobalsReady,
            bool activeVesselPresent,
            int currentFlightInstanceId,
            int previousFlightInstanceId)
        {
            // Unity GetInstanceID() uses non-zero values for valid objects; 0 is
            // the local sentinel for "no ParsekFlight instance".
            bool replacedFlight = currentFlightInstanceId != 0
                && (previousFlightInstanceId == 0
                    || currentFlightInstanceId != previousFlightInstanceId);
            return loadedScene == GameScenes.FLIGHT
                && flightGlobalsReady
                && activeVesselPresent
                && replacedFlight;
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
