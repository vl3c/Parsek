using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// ScenarioModule that persists committed recordings to save games.
    /// Handles OnSave/OnLoad to serialize trajectory data into ConfigNodes.
    /// Also manages crew reservation for deferred vessel spawns.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION, GameScenes.EDITOR)]
    public class ParsekScenario : ScenarioModule
    {
        #region Game State Recording

        private GameStateRecorder stateRecorder;

        #endregion

        // Gates Update() resource ticking while coroutines reset resource baselines
        private bool resourceTickingSuspended = false;

        public override void OnSave(ConfigNode node)
        {
            // Safety net (defense-in-depth): if a pending recording still exists outside Flight
            // and the dialog is not actively pending, auto-commit ghost-only before serialization.
            // Under normal operation this is unreachable — Sites A/B handle all paths.
            if (HighLogic.LoadedScene != GameScenes.FLIGHT && !mergeDialogPending)
            {
                if (RecordingStore.HasPending)
                {
                    AutoCommitGhostOnly(RecordingStore.Pending);
                    RecordingStore.CommitPending();
                    ParsekLog.Warn("Scenario",
                        "Safety net: committed pending recording on save outside Flight");
                }
                if (RecordingStore.HasPendingTree)
                {
                    AutoCommitTreeGhostOnly(RecordingStore.PendingTree);
                    RecordingStore.CommitPendingTree();
                    ParsekLog.Warn("Scenario",
                        "Safety net: committed pending tree on save outside Flight");
                }
            }

            // Diagnostic: detect if HighLogic.SaveFolder changed since this scenario loaded.
            // Under normal KSP flow OnSave fires before the folder changes, but if it doesn't,
            // file writes (SaveRecordingFiles, GameStateStore, MilestoneStore) would target
            // the wrong save directory.
            string currentSaveFolder = HighLogic.SaveFolder;
            if (IsSaveFolderMismatch(scenarioSaveFolder, currentSaveFolder))
            {
                ParsekLog.Warn("Scenario",
                    $"OnSave: save folder mismatch — loaded for '{scenarioSaveFolder}' " +
                    $"but current is '{currentSaveFolder}'. Data may write to wrong save directory.");
            }

            // Clear any existing recording nodes
            node.RemoveNodes("RECORDING");

            var recordings = RecordingStore.CommittedRecordings;
            ParsekLog.Info("Scenario", $"OnSave: saving {recordings.Count} committed recordings, epoch={MilestoneStore.CurrentEpoch}");

            SaveStandaloneRecordings(node, recordings);

            // Save committed recording trees
            node.RemoveNodes("RECORDING_TREE");
            var committedTrees = RecordingStore.CommittedTrees;
            ParsekLog.Info("Scenario", $"OnSave: saving {committedTrees.Count} committed tree(s)");
            for (int t = 0; t < committedTrees.Count; t++)
            {
                var tree = committedTrees[t];

                // Write bulk data to external files for each recording in the tree
                foreach (var rec in tree.Recordings.Values)
                {
                    if (!RecordingStore.SaveRecordingFiles(rec))
                        ScenarioLog($"[Parsek Scenario] WARNING: File write failed for tree recording '{rec.VesselName}'");
                }

                ConfigNode treeNode = node.AddNode("RECORDING_TREE");
                tree.Save(treeNode);
            }

            // Persist crew replacement mappings
            CrewReservationManager.SaveCrewReplacements(node);

            // Persist group hierarchy and hidden groups
            GroupHierarchyStore.SaveInto(node);

            // Save game state events to external file
            GameStateStore.SaveEventFile();
            node.AddValue("gameStateEventCount", GameStateStore.EventCount);

            // Save any pending baselines
            foreach (var baseline in GameStateStore.Baselines)
                GameStateStore.SaveBaseline(baseline);

            // Flush any uncaptured game state events into a milestone before saving.
            // Handles events that happened without a recording commit (e.g. tech
            // research in R&D without launching a flight).
            ParsekLog.Verbose("Scenario", $"OnSave: flushing pending events at UT {Planetarium.GetUniversalTime():F0}");
            MilestoneStore.FlushPendingEvents(Planetarium.GetUniversalTime());

            // Save milestones to external file + mutable state to .sfs
            MilestoneStore.SaveMilestoneFile();
            MilestoneStore.SaveMutableState(node);
            node.AddValue("milestoneEpoch", MilestoneStore.CurrentEpoch);
            node.AddValue("budgetDeductionEpoch",
                budgetDeductionEpoch.ToString(CultureInfo.InvariantCulture));
            ParsekLog.Verbose("Scenario",
                $"OnSave: wrote milestoneEpoch={MilestoneStore.CurrentEpoch}, budgetDeductionEpoch={budgetDeductionEpoch}");

            lastOnSaveScene = HighLogic.LoadedScene;
        }

        // Static flag: only load from save once per KSP session.
        // On revert, the launch quicksave has stale data — the in-memory
        // static list is the real source of truth within a session.
        // Reset on main menu transition to prevent stale data leaking between saves.
        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;
        private static uint budgetDeductionEpoch = 0;
        private static bool mainMenuHookRegistered = false;

        // Tracks the scene from which the last OnSave fired.
        // Used to detect FLIGHT→FLIGHT transitions (Revert to Launch / quickload)
        // that aren't caught by epoch/recording-count comparison.
        private static GameScenes lastOnSaveScene = GameScenes.MAINMENU;

        // Cached autoMerge setting — ParsekSettings.Current can be null during early
        // scene loads (OnLoad fires before GameParameters are available). This is set
        // from ParsekSettings.Current whenever it's accessible, and used as fallback.
        private static bool cachedAutoMerge = false;

        // Deferred merge dialog: when autoMerge is off, the dialog follows the player
        // across scenes until they address it. Reset on each OnLoad so scene changes
        // get a fresh chance to show the dialog.
        private static bool mergeDialogPending = false;
        internal static bool MergeDialogPending
        {
            get => mergeDialogPending;
            set => mergeDialogPending = value;
        }

        /// <summary>
        /// Reads the autoMerge setting reliably. ParsekSettings.Current can be null
        /// during early scene loads — falls back to the cached value in that case.
        /// </summary>
        internal static bool IsAutoMerge
        {
            get
            {
                var settings = ParsekSettings.Current;
                if (settings != null)
                {
                    cachedAutoMerge = settings.autoMerge;
                    return settings.autoMerge;
                }
                return cachedAutoMerge;
            }
        }

        // Tracks which save folder this scenario instance was loaded for.
        // Used to detect OnSave firing after HighLogic.SaveFolder has changed.
        private string scenarioSaveFolder;

        public override void OnLoad(ConfigNode node)
        {
            // Reset deferred dialog flag and clear input lock (dialog may have been
            // destroyed by scene change without the user clicking a button)
            mergeDialogPending = false;
            InputLockManager.RemoveControlLock("ParsekMergeDialog");

            var recordings = RecordingStore.CommittedRecordings;

            // Register a one-time hook to reset session state when returning to main menu.
            // This prevents stale in-memory recordings from leaking into a new save
            // (e.g., deleting a career and creating a new one with the same name).
            // Wrapped in try-catch: KSP's EvtDelegate constructor can throw
            // NullReferenceException during early scene loads when GameEvents
            // internals aren't fully initialized.
            if (!mainMenuHookRegistered)
            {
                try
                {
                    GameEvents.onGameSceneLoadRequested.Add(OnMainMenuTransition);
                    mainMenuHookRegistered = true;
                }
                catch (System.NullReferenceException)
                {
                    ParsekLog.Verbose("Scenario",
                        "Failed to register main menu hook (GameEvents not ready) — will retry on next OnLoad");
                }
            }

            // Detect loading a different save game (not a revert)
            string currentSave = HighLogic.SaveFolder;
            scenarioSaveFolder = currentSave;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                lastSaveFolder = currentSave;
                ScenarioLog($"[Parsek Scenario] Save folder changed to '{currentSave}' — resetting session state");
            }

            // Load crew replacement mappings from the node (both initial and revert paths need this).
            // Skip during go-back: in-memory crewReplacements is the source of truth
            // (it has replacements for recordings committed after this quicksave).
            if (!RecordingStore.IsRewinding)
            {
                CrewReservationManager.LoadCrewReplacements(node);
                GroupHierarchyStore.LoadGroupHierarchy(node);
                GroupHierarchyStore.LoadHiddenGroups(node);
            }

            // Game state recorder lifecycle — re-subscribe on every OnLoad (handles reverts)
            stateRecorder?.Unsubscribe();
            if (!initialLoadDone)
            {
                ParsekLog.Verbose("Scenario", "OnLoad: initial load — loading external files");
                GameStateStore.LoadEventFile();
                GameStateStore.LoadBaselines();
                MilestoneStore.LoadMilestoneFile();

                // Clean up stale parsek_rw_*.sfs temp files left by a crash during rewind
                try
                {
                    string savesDir = System.IO.Path.Combine(
                        KSPUtil.ApplicationRootPath ?? "", "saves", HighLogic.SaveFolder ?? "");
                    if (System.IO.Directory.Exists(savesDir))
                    {
                        string[] staleFiles = System.IO.Directory.GetFiles(savesDir, "parsek_rw_*.sfs");
                        for (int s = 0; s < staleFiles.Length; s++)
                        {
                            try
                            {
                                System.IO.File.Delete(staleFiles[s]);
                                ScenarioLog($"[Parsek Scenario] Deleted stale rewind temp file: {System.IO.Path.GetFileName(staleFiles[s])}");
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ScenarioLog($"[Parsek Scenario] Failed to scan for stale rewind temp files: {ex.Message}");
                }

                // Restore epoch from save
                string epochStr = node.GetValue("milestoneEpoch");
                if (epochStr != null)
                {
                    uint epoch;
                    if (uint.TryParse(epochStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch))
                    {
                        MilestoneStore.CurrentEpoch = epoch;
                        ParsekLog.Verbose("Scenario", $"OnLoad: restored milestoneEpoch={epoch} from save");
                    }
                }

                // Restore budget deduction tracking
                string bdeStr = node.GetValue("budgetDeductionEpoch");
                if (bdeStr != null)
                {
                    uint bde;
                    if (uint.TryParse(bdeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out bde))
                    {
                        budgetDeductionEpoch = bde;
                        ParsekLog.Verbose("Scenario", $"OnLoad: restored budgetDeductionEpoch={bde} from save");
                    }
                }
            }
            stateRecorder = new GameStateRecorder();
            stateRecorder.SeedFacilityCacheFromCurrentState();
            stateRecorder.Subscribe();

            // Subscribe to vessel lifecycle events (Remove first to avoid duplicates on revert/scene change)
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselRecovered.Add(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
            GameEvents.onVesselTerminated.Add(OnVesselTerminated);

            // Capture initial baseline if none exist yet
            if (!initialLoadDone && GameStateStore.BaselineCount == 0)
            {
                try
                {
                    GameStateStore.CaptureBaselineIfNeeded();
                }
                catch (System.Exception ex)
                {
                    ScenarioLog($"[Parsek Scenario] Failed to capture initial baseline: {ex.Message}");
                }
            }

            if (initialLoadDone)
            {
                // Go-back detection: must be BEFORE revert detection and BEFORE any
                // .sfs data loading. In-memory state is the source of truth.
                if (RecordingStore.IsRewinding)
                {
                    HandleRewindOnLoad(node, recordings);
                    return;
                }

                // Detect revert vs scene change. On a revert, the quicksave is older:
                // its epoch is lower (after a prior revert bumped it) or it has fewer
                // recordings (new ones were committed since launch). On a scene change,
                // the most recent OnSave wrote the current epoch and recording count.
                ConfigNode[] savedRecNodes = node.GetNodes("RECORDING");
                uint savedEpoch = 0;
                string savedEpochStr = node.GetValue("milestoneEpoch");
                if (savedEpochStr != null)
                    uint.TryParse(savedEpochStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out savedEpoch);

                // Count tree recordings from saved tree nodes for accurate revert detection.
                // Tree recordings are in committedRecordings but NOT in standalone RECORDING nodes.
                ConfigNode[] savedTreeNodesForRevert = node.GetNodes("RECORDING_TREE");
                int savedTreeRecCount = 0;
                for (int t = 0; t < savedTreeNodesForRevert.Length; t++)
                    savedTreeRecCount += savedTreeNodesForRevert[t].GetNodes("RECORDING").Length;
                int totalSavedRecCount = savedRecNodes.Length + savedTreeRecCount;

                // FLIGHT→FLIGHT is always a revert (Revert to Launch or quickload).
                // KSP has no FLIGHT→FLIGHT path that isn't a revert.
                bool isFlightToFlight = lastOnSaveScene == GameScenes.FLIGHT
                                        && HighLogic.LoadedScene == GameScenes.FLIGHT;
                bool isRevert = savedEpoch < MilestoneStore.CurrentEpoch
                                || totalSavedRecCount < recordings.Count
                                || isFlightToFlight;
                ParsekLog.Verbose("Scenario",
                    $"OnLoad: revert detection — savedEpoch={savedEpoch}, currentEpoch={MilestoneStore.CurrentEpoch}, " +
                    $"savedRecNodes={savedRecNodes.Length}, savedTreeRecs={savedTreeRecCount}, " +
                    $"memoryRecordings={recordings.Count}, lastOnSaveScene={lastOnSaveScene}, " +
                    $"isFlightToFlight={isFlightToFlight}, isRevert={isRevert}");

                // Collect spawned vessel PIDs + names BEFORE restore resets them.
                // Only on revert — on normal scene changes the spawned vessels are
                // legitimate and must not be cleaned up.
                // Guard: if cleanup data was already set by a prior rewind path,
                // do NOT overwrite — the rewind path's data is authoritative.
                // (After rewind, ResetAllPlaybackState zeros spawn tracking, so
                // CollectSpawnedVesselInfo returns empty here and would clobber
                // the rewind data with null.)
                if (isRevert)
                {
                    bool alreadyHasCleanupData = RecordingStore.PendingCleanupPids != null
                                                  || RecordingStore.PendingCleanupNames != null;
                    if (alreadyHasCleanupData)
                    {
                        ParsekLog.Info("Scenario",
                            $"OnLoad: revert path skipping cleanup collection — " +
                            $"already set ({RecordingStore.PendingCleanupPids?.Count ?? 0} pid(s), " +
                            $"{RecordingStore.PendingCleanupNames?.Count ?? 0} name(s)) from prior rewind/revert");
                    }
                    else
                    {
                        var info = RecordingStore.CollectSpawnedVesselInfo();
                        var spawnedPids = info.pids.Count > 0 ? info.pids : null;
                        var spawnedNames = info.names.Count > 0 ? info.names : null;
                        RecordingStore.PendingCleanupPids = spawnedPids;
                        RecordingStore.PendingCleanupNames = spawnedNames;
                        ParsekLog.Verbose("Scenario",
                            $"OnLoad: revert cleanup collected — " +
                            $"{spawnedPids?.Count ?? 0} pid(s), {spawnedNames?.Count ?? 0} name(s)");
                    }
                }

                // Restore mutable state from save. On revert the launch quicksave has
                // no spawnedPid / lastResIdx, so they naturally reset.
                // On non-revert scene changes (e.g. tracking station → flight) the
                // save preserves these, preventing duplicate spawns and ghost replays.
                // NOTE: This loop only covers standalone recordings (not tree recordings).
                // Tree recordings get their mutable state from RECORDING_TREE nodes below.
                // Match by recordingId (not positional index) — the saved RECORDING nodes
                // may be from a stale quicksave with a different number/order of recordings
                // than the current in-memory list (e.g. after clear-all + re-record).
                RestoreStandaloneMutableState(recordings, savedRecNodes);

                // Restore tree recording mutable state from RECORDING_TREE nodes.
                // First, reset ALL tree recordings to defaults. On revert, the launch
                // quicksave has no tree nodes so this reset is the only thing that runs,
                // ensuring VesselSpawned/SpawnedPid/etc. don't carry over from the
                // committed flight (whose vessels were undone by the revert).
                // On scene change, the reset is overwritten by the saved values below.
                for (int i = 0; i < recordings.Count; i++)
                {
                    if (recordings[i].TreeId == null) continue;

                    ClearPostSpawnTerminalState(recordings[i], "tree recording");

                    recordings[i].VesselSpawned = false;
                    recordings[i].SpawnAttempts = 0;
                    recordings[i].SpawnDeathCount = 0;
                    recordings[i].SpawnedVesselPersistentId = 0;

                    recordings[i].LastAppliedResourceIndex = -1;
                }

                // Strip orphaned spawned vessels from flightState on revert.
                // These vessels were spawned by Parsek in a previous flight but their
                // tracking was lost when spawn flags were reset. Without stripping,
                // they contaminate the next launch quicksave and persist across reverts.
                // Use RecordingStore.PendingCleanupNames as the authoritative source —
                // the local collection may have been skipped by the guard above.
                var cleanupNames = RecordingStore.PendingCleanupNames;
                if (isRevert && cleanupNames != null && cleanupNames.Count > 0)
                {
                    var flightState = HighLogic.CurrentGame?.flightState;
                    if (flightState != null)
                        StripOrphanedSpawnedVessels(flightState.protoVessels, cleanupNames,
                            skipPrelaunch: true);
                }

                // Then restore from saved tree nodes (present on scene change, absent on revert)
                ConfigNode[] savedTreeNodes = node.GetNodes("RECORDING_TREE");
                if (savedTreeNodes.Length > 0)
                {
                    // Rebuild tree mutable state from saved tree nodes
                    foreach (var savedTreeNode in savedTreeNodes)
                    {
                        ConfigNode[] savedTreeRecNodes = savedTreeNode.GetNodes("RECORDING");
                        foreach (var savedTreeRecNode in savedTreeRecNodes)
                        {
                            string savedRecId = savedTreeRecNode.GetValue("recordingId");
                            if (string.IsNullOrEmpty(savedRecId)) continue;

                            // Find the in-memory recording by ID and restore mutable state
                            for (int i = 0; i < recordings.Count; i++)
                            {
                                if (recordings[i].RecordingId == savedRecId)
                                {
                                    string pidStr = savedTreeRecNode.GetValue("spawnedPid");
                                    uint savedPid = 0;
                                    if (pidStr != null)
                                        uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out savedPid);
                                    recordings[i].SpawnedVesselPersistentId = savedPid;

                                    string resIdxStr = savedTreeRecNode.GetValue("lastResIdx");
                                    int resIdx = -1;
                                    if (resIdxStr != null)
                                        int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx);
                                    recordings[i].LastAppliedResourceIndex = resIdx;

                                    break;
                                }
                            }
                        }
                    }
                }

                if (isRevert)
                {
                    // Restore milestone mutable state from .sfs and increment epoch.
                    // resetUnmatched: true — milestones created after the launch quicksave
                    // (not in the saved state) must be reset to unreplayed (-1).
                    MilestoneStore.RestoreMutableState(node, resetUnmatched: true);
                    MilestoneStore.CurrentEpoch++;
                    ParsekLog.Info("Scenario", $"Milestone epoch incremented to {MilestoneStore.CurrentEpoch} on revert");

                    // Schedule committed resource deduction (singletons may not be ready yet)
                    ParsekLog.Verbose("Scenario", "Scheduling budget deduction coroutine (singletons may not be ready yet)");
                    resourceTickingSuspended = true;
                    StartCoroutine(ApplyBudgetDeductionWhenReady());
                }
                else
                {
                    // Scene change — restore milestone state without resetting unmatched.
                    MilestoneStore.RestoreMutableState(node);
                    ParsekLog.Verbose("Scenario", "Scene change — milestone state restored (resetUnmatched=false)");
                }

                // Handle pending recordings on non-revert scene exits to non-flight scenes.
                // Always auto-commit on main menu (game is being unloaded, dialog would be meaningless).
                bool forceAutoMerge = HighLogic.LoadedScene == GameScenes.MAINMENU;
                if (!isRevert && HighLogic.LoadedScene != GameScenes.FLIGHT)
                {
                    if (IsAutoMerge || forceAutoMerge)
                    {
                        // autoMerge ON: auto-commit ghost-only (existing behavior)
                        if (RecordingStore.HasPending)
                        {
                            AutoCommitGhostOnly(RecordingStore.Pending);
                            RecordingStore.CommitPending();
                            ScreenMessages.PostScreenMessage("[Parsek] Recording committed to timeline", 5f);
                        }
                        if (RecordingStore.HasPendingTree)
                        {
                            AutoCommitTreeGhostOnly(RecordingStore.PendingTree);
                            RecordingStore.CommitPendingTree();
                            ScreenMessages.PostScreenMessage("[Parsek] Tree recording committed to timeline", 5f);
                        }
                    }
                    else if ((RecordingStore.HasPending || RecordingStore.HasPendingTree) && !mergeDialogPending)
                    {
                        // autoMerge OFF: defer to merge dialog in the new scene
                        mergeDialogPending = true;
                        StartCoroutine(ShowDeferredMergeDialog());
                    }
                }

                CrewReservationManager.ReserveSnapshotCrew();
                ParsekLog.Info("Scenario", $"{(isRevert ? "Revert" : "Scene change")} — preserving {recordings.Count} session recordings");
                return;
            }

            initialLoadDone = true;

            DiscardStalePendingState();

            recordings.Clear();

            ConfigNode[] recNodes = node.GetNodes("RECORDING");
            ScenarioLog($"[Parsek Scenario] Loading {recNodes.Length} committed recordings");

            LoadStandaloneRecordingsFromNodes(recNodes, recordings);

            // Validate chain integrity before any playback
            RecordingStore.ValidateChains();

            LoadRecordingTrees(node, recordings);

            // Clean orphaned sidecar files (recordings deleted in previous sessions)
            RecordingStore.CleanOrphanFiles();

            // Restore milestone mutable state (LastReplayedEventIndex) from .sfs
            MilestoneStore.RestoreMutableState(node);

            CrewReservationManager.ReserveSnapshotCrew();

            // Diagnostic summary of loaded recordings with UT context
            double loadUT = Planetarium.GetUniversalTime();
            ParsekLog.Info("Scenario", $"Scenario load summary — UT: {loadUT:F0}, {recordings.Count} recording(s)");
            for (int i = 0; i < recordings.Count; i++)
            {
                var loadedRec = recordings[i];
                double duration = loadedRec.EndUT - loadedRec.StartUT;
                string status;
                if (loadUT < loadedRec.StartUT)
                    status = $"future (starts in {loadedRec.StartUT - loadUT:F0}s)";
                else if (loadUT <= loadedRec.EndUT && duration > 0)
                    status = $"IN PROGRESS ({(loadUT - loadedRec.StartUT) / duration * 100:F0}%)";
                else if (loadUT <= loadedRec.EndUT)
                    status = "IN PROGRESS";
                else
                    status = "past";
                ParsekLog.Info("Scenario", $"  #{i}: \"{loadedRec.VesselName}\" — {status}");
            }

            if (CrewReservationManager.CrewReplacements.Count > 0)
            {
                ParsekLog.Info("Scenario", $"Crew reservations active ({CrewReservationManager.CrewReplacements.Count}):");
                foreach (var kvp in CrewReservationManager.CrewReplacements)
                    ParsekLog.Info("Scenario", $"  {kvp.Key} -> replacement: {kvp.Value}");
            }

            // Auto-unreserve crew for recordings whose EndUT has already passed
            // but vessel was never spawned (e.g. UT advanced while in Space Center).
            double currentUT = Planetarium.GetUniversalTime();
            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];
                if (rec.LoopPlayback) continue;
                if (rec.VesselSnapshot != null && !rec.VesselSpawned && currentUT > rec.EndUT)
                {
                    CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    rec.VesselSnapshot = null;
                    rec.VesselSpawned = true;
                    ScenarioLog($"[Parsek Scenario] Auto-unreserved crew for recording #{i} " +
                        $"({rec.VesselName}) — EndUT passed without spawn");
                }
            }

            // Handle pending recordings outside Flight (Esc > Abort Mission → Space Center path).
            // Always auto-commit on main menu (game is being unloaded).
            if (HighLogic.LoadedScene != GameScenes.FLIGHT &&
                (RecordingStore.HasPending || RecordingStore.HasPendingTree))
            {
                if (IsAutoMerge || HighLogic.LoadedScene == GameScenes.MAINMENU)
                {
                    // autoMerge ON: auto-commit ghost-only
                    if (RecordingStore.HasPending)
                    {
                        var pending = RecordingStore.Pending;
                        if (pending.VesselSnapshot != null)
                            CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                        pending.VesselSnapshot = null;
                        RecordingStore.CommitPending();
                        ScenarioLog($"[Parsek Scenario] Auto-committed pending recording outside Flight " +
                            $"(scene: {HighLogic.LoadedScene})");
                    }
                    if (RecordingStore.HasPendingTree)
                    {
                        var pt = RecordingStore.PendingTree;
                        foreach (var rec in pt.Recordings.Values)
                        {
                            if (rec.VesselSnapshot != null)
                                CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                            rec.VesselSnapshot = null;
                        }
                        RecordingStore.CommitPendingTree();
                        ScenarioLog($"[Parsek Scenario] Auto-committed pending tree outside Flight " +
                            $"(scene: {HighLogic.LoadedScene})");
                    }
                }
                else if (!mergeDialogPending)
                {
                    // autoMerge OFF: defer to merge dialog
                    mergeDialogPending = true;
                    StartCoroutine(ShowDeferredMergeDialog());
                }
            }
        }

        /// <summary>
        /// Handles the rewind (go-back) path during OnLoad. Increments epoch, restores
        /// milestone state, collects cleanup PIDs/names, resets playback state, strips
        /// orphaned vessels, schedules budget coroutine, re-reserves crew, clears rewind flags.
        /// </summary>
        private void HandleRewindOnLoad(ConfigNode node, List<Recording> recordings)
        {
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind detected, skipping .sfs recording/crew load " +
                $"(using {recordings.Count} in-memory recordings)");

            // Increment epoch (in-memory, NOT from .sfs — the quicksave has stale epoch)
            MilestoneStore.CurrentEpoch++;
            ParsekLog.Info("Rewind",
                $"OnLoad: epoch incremented to {MilestoneStore.CurrentEpoch}");

            // Restore milestone mutable state with resetUnmatched=true
            // (milestones created after rewind point get reset to unreplayed)
            MilestoneStore.RestoreMutableState(node, resetUnmatched: true);

            // Collect spawned vessel PIDs for belt-and-suspenders in OnFlightReady
            var (rewindSpawnedPids, _) = RecordingStore.CollectSpawnedVesselInfo();
            RecordingStore.PendingCleanupPids = rewindSpawnedPids.Count > 0 ? rewindSpawnedPids : null;

            // Collect ALL recording vessel names for flightState stripping.
            // On rewind, ANY vessel matching a recording name is from the future
            // and must be stripped — not just those with non-zero SpawnedVesselPersistentId.
            // (PIDs may already be zero from a previous rewind's ResetAllPlaybackState.)
            var allRecordingNames = RecordingStore.CollectAllRecordingVesselNames();
            RecordingStore.PendingCleanupNames = allRecordingNames.Count > 0 ? allRecordingNames : null;
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind cleanup data set — " +
                $"{rewindSpawnedPids.Count} pid(s), {allRecordingNames.Count} name(s)");

            // Reset ALL playback state (recordings + trees)
            var (standaloneCount, treeCount) = RecordingStore.ResetAllPlaybackState();
            ParsekLog.Info("Rewind",
                $"OnLoad: resetting playback state for {standaloneCount} recordings + {treeCount} trees");

            // Strip ALL vessels matching recording names from flightState.
            // The rewind save was preprocessed to strip the recorded vessel,
            // but KSP's scene transition may reintroduce vessels from the old
            // persistent.sfs. Strip unconditionally — on rewind, every matching
            // vessel is from the future.
            if (allRecordingNames.Count > 0)
            {
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null)
                    StripOrphanedSpawnedVessels(flightState.protoVessels, allRecordingNames,
                        skipPrelaunch: false);
            }

            // Rewind strip already handled protoVessel cleanup in flightState.
            // Clear pending data so OnFlightReady doesn't re-run with overbroad names
            // that would match freshly-spawned past vessels (bug #134). The revert path's
            // alreadyHasCleanupData guard (line ~352) will see null and collect
            // fresh data from CollectSpawnedVesselInfo() if needed.
            RecordingStore.PendingCleanupPids = null;
            RecordingStore.PendingCleanupNames = null;
            ParsekLog.Info("Rewind",
                "OnLoad: cleared PendingCleanupPids/Names after strip — " +
                "prevents OnFlightReady from destroying freshly-spawned past vessels");

            // Set budgetDeductionEpoch BEFORE scheduling coroutine
            // (prevents ApplyBudgetDeductionWhenReady from double-deducting)
            budgetDeductionEpoch = MilestoneStore.CurrentEpoch;

            // Schedule resource + UT adjustment (deferred — singletons from the OLD
            // scene may still be alive during OnLoad; we must yield at least one frame
            // so the new scene's Funding/R&D/Reputation/Planetarium are initialized).
            // Setting UT before LoadScene does NOT work — scene transition overwrites it.
            resourceTickingSuspended = true;
            StartCoroutine(ApplyRewindResourceAdjustment());
            ParsekLog.Info("Rewind",
                "OnLoad: resource + UT adjustment deferred (waiting for new scene singletons)");

            // Re-reserve crew from all recording snapshots
            CrewReservationManager.ReserveSnapshotCrew();

            // Clear rewind flags — rewind loads into SpaceCenter, not Flight
            RecordingStore.IsRewinding = false;
            ParsekLog.Info("Rewind",
                $"OnLoad: rewind complete at UT {RecordingStore.RewindUT}. " +
                $"Timeline: {recordings.Count} recordings");
            RecordingStore.RewindUT = 0;
            RecordingStore.RewindAdjustedUT = 0;
            RecordingStore.RewindBaselineFunds = 0;
            RecordingStore.RewindBaselineScience = 0;
            RecordingStore.RewindBaselineRep = 0;
        }

        /// <summary>
        /// Clears pending recordings, pending trees, and stale rewind flags that may have
        /// leaked from a previous save. Static fields survive scene changes, so without this
        /// cleanup, pending state from save A would be auto-committed into save B's timeline.
        /// </summary>
        private void DiscardStalePendingState()
        {
            if (RecordingStore.HasPending)
            {
                ParsekLog.Warn("Scenario",
                    $"OnLoad initial: discarding pending recording " +
                    $"'{RecordingStore.Pending.VesselName}' from previous save");
                RecordingStore.DiscardPending();
            }
            if (RecordingStore.HasPendingTree)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: discarding pending tree from previous save");
                RecordingStore.DiscardPendingTree();
            }
            if (RecordingStore.IsRewinding)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: clearing stale rewind flags from previous save");
                RecordingStore.IsRewinding = false;
                RecordingStore.RewindUT = 0;
                RecordingStore.RewindAdjustedUT = 0;
                RecordingStore.RewindReserved = default(BudgetSummary);
                RecordingStore.RewindBaselineFunds = 0;
                RecordingStore.RewindBaselineScience = 0;
                RecordingStore.RewindBaselineRep = 0;
            }
        }

        /// <summary>
        /// Loads committed recording trees from RECORDING_TREE ConfigNodes. Clears stale
        /// trees, loads each tree and its bulk data, and adds tree recordings to the
        /// committed recordings list for ghost playback.
        /// </summary>
        private static void LoadRecordingTrees(ConfigNode node, List<Recording> recordings)
        {
            // Always clear CommittedTrees — if the new save has no trees, stale trees
            // from the previous save would otherwise persist and contaminate this save.
            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            var committedTrees = RecordingStore.CommittedTrees;
            committedTrees.Clear();
            ParsekLog.Info("Scenario",
                $"OnLoad initial: cleared CommittedTrees, loading {treeNodes.Length} tree(s)");

            if (treeNodes.Length > 0)
            {
                for (int t = 0; t < treeNodes.Length; t++)
                {
                    var tree = RecordingTree.Load(treeNodes[t]);

                    // Load bulk data from external files for each recording in the tree
                    foreach (var rec in tree.Recordings.Values)
                    {
                        RecordingStore.LoadRecordingFiles(rec);
                    }

                    committedTrees.Add(tree);

                    // Add tree recordings to CommittedRecordings for ghost playback
                    foreach (var rec in tree.Recordings.Values)
                    {
                        recordings.Add(rec);
                    }

                    ScenarioLog($"[Parsek Scenario] Loaded tree '{tree.TreeName}': " +
                        $"{tree.Recordings.Count} recordings, {tree.BranchPoints.Count} branch points");
                }
            }
        }

        #region Deferred Merge Dialog

        /// <summary>
        /// Shows the merge dialog after a short delay, allowing the scene to fully load.
        /// Used when autoMerge is off and the player leaves Flight with a pending recording.
        /// </summary>
        private IEnumerator ShowDeferredMergeDialog()
        {
            // Wait ~60 frames for scene to fully load (UI skin, singletons, etc.)
            int waitFrames = 60;
            while (waitFrames-- > 0)
                yield return null;

            // Guard: pending may have been consumed during the wait
            if (!RecordingStore.HasPending && !RecordingStore.HasPendingTree)
            {
                mergeDialogPending = false;
                ParsekLog.Verbose("Scenario", "Deferred merge dialog: pending consumed during wait — aborting");
                yield break;
            }

            // EVA child recordings with a matching parent: auto-commit silently
            // (matches ParsekFlight.OnFlightReady behavior)
            if (RecordingStore.HasPending && !string.IsNullOrEmpty(RecordingStore.Pending.ParentRecordingId))
            {
                bool parentFound = false;
                foreach (var rec in RecordingStore.CommittedRecordings)
                {
                    if (rec.RecordingId == RecordingStore.Pending.ParentRecordingId)
                    {
                        parentFound = true;
                        break;
                    }
                }
                if (parentFound)
                {
                    ParsekLog.Info("Scenario",
                        $"Deferred merge: auto-committing EVA child recording (parent={RecordingStore.Pending.ParentRecordingId})");
                    RecordingStore.CommitPending();
                    mergeDialogPending = false;
                    yield break;
                }
            }

            // Show the appropriate dialog
            if (RecordingStore.HasPendingTree)
            {
                ParsekLog.Info("Scenario",
                    $"Showing deferred tree merge dialog in {HighLogic.LoadedScene}");
                MergeDialog.ShowTreeDialog(RecordingStore.PendingTree);
            }
            else if (RecordingStore.HasPending)
            {
                ParsekLog.Info("Scenario",
                    $"Showing deferred merge dialog in {HighLogic.LoadedScene}");
                MergeDialog.Show(RecordingStore.Pending);
            }
            // mergeDialogPending stays true until the user clicks a button
            // (ClearPendingFlag is called from the button callbacks)
        }

        #endregion

        #region Budget Deduction

        /// <summary>
        /// Waits for resource singletons to be available, then deducts committed
        /// budget from the game state. This ensures the KSP top bar and all
        /// purchase checks reflect available (non-committed) resources.
        /// </summary>
        private IEnumerator ApplyBudgetDeductionWhenReady()
        {
            // Wait until ALL resource singletons are available (may take a few frames
            // after scene load). Use || so we wait while ANY singleton is still null.
            int maxWait = 120; // ~2 seconds at 60fps
            while (maxWait-- > 0
                   && (Funding.Instance == null
                       || ResearchAndDevelopment.Instance == null
                       || Reputation.Instance == null))
                yield return null;

            ParsekLog.Verbose("Scenario",
                $"ApplyBudgetDeduction: singletons ready after {120 - maxWait} frames. " +
                $"Funding={Funding.Instance != null}, R&D={ResearchAndDevelopment.Instance != null}, Rep={Reputation.Instance != null}");

            if (budgetDeductionEpoch >= MilestoneStore.CurrentEpoch)
            {
                ParsekLog.Verbose("Scenario",
                    $"Budget deduction already applied for epoch {budgetDeductionEpoch} (current={MilestoneStore.CurrentEpoch})");
                resourceTickingSuspended = false;
                yield break;
            }
            budgetDeductionEpoch = MilestoneStore.CurrentEpoch;

            var budget = ResourceBudget.ComputeTotal(
                RecordingStore.CommittedRecordings,
                MilestoneStore.Milestones,
                RecordingStore.CommittedTrees);

            if (budget.reservedFunds <= 0 && budget.reservedScience <= 0
                && budget.reservedReputation <= 0)
            {
                ParsekLog.Verbose("Scenario", "No committed budget to deduct on revert — all zero");
                resourceTickingSuspended = false;
                yield break;
            }

            ParsekLog.Info("Scenario",
                $"Budget deduction starting for epoch {MilestoneStore.CurrentEpoch}: " +
                $"funds={budget.reservedFunds:F0}, science={budget.reservedScience:F1}, rep={budget.reservedReputation:F1}");

            ResourceApplicator.DeductBudget(budget, RecordingStore.CommittedRecordings, RecordingStore.CommittedTrees);

            // Replay committed actions (tech, parts, facilities, crew) before resuming ticking
            ActionReplay.ReplayCommittedActions(MilestoneStore.Milestones);
            resourceTickingSuspended = false;
        }

        /// <summary>
        /// Applies the differential resource adjustment after a rewind.
        /// Deferred via coroutine because Funding/R&amp;D/Reputation singletons
        /// are not available during OnLoad.
        /// </summary>
        private IEnumerator ApplyRewindResourceAdjustment()
        {
            // Capture rewind state before yielding — flags are cleared synchronously
            // in OnLoad after StartCoroutine returns.
            var saved = RecordingStore.RewindReserved;
            double rewindUT = RecordingStore.RewindUT;
            double adjustedUT = RecordingStore.RewindAdjustedUT;
            double baselineFunds = RecordingStore.RewindBaselineFunds;
            double baselineScience = RecordingStore.RewindBaselineScience;
            float baselineRep = RecordingStore.RewindBaselineRep;

            // CRITICAL: yield at least one frame before touching any singleton.
            // During OnLoad, singletons from the OLD scene may still be alive.
            // Without this yield, SetUniversalTime modifies the OLD Planetarium
            // which is then destroyed when the new scene finishes loading.
            yield return null;

            var ic = CultureInfo.InvariantCulture;

            // Apply adjusted UT unconditionally — Planetarium is always available
            // after the first yield. This must NOT be gated on resource singletons
            // (sandbox/science mode has no Funding/R&D/Reputation, but still needs UT).
            // UT=0 is valid (recording near game start with lead time clamped to 0).
            {
                double prePlanetariumUT = Planetarium.GetUniversalTime();
                Planetarium.SetUniversalTime(adjustedUT);
                ParsekLog.Info("Rewind",
                    $"UT adjustment: {prePlanetariumUT.ToString("F1", ic)} → {adjustedUT.ToString("F1", ic)} " +
                    $"(post-set check: {Planetarium.GetUniversalTime().ToString("F1", ic)})");
            }

            // Wait for resource singletons (career mode only).
            // In sandbox/science mode these are permanently null — skip gracefully.
            int maxWait = 120; // ~2 seconds at 60fps
            while (maxWait-- > 0
                   && (Funding.Instance == null
                       || ResearchAndDevelopment.Instance == null
                       || Reputation.Instance == null))
                yield return null;

            ResourceApplicator.CorrectToBaseline(baselineFunds, baselineScience, baselineRep);

            // Replay committed actions (tech, parts, facilities, crew).
            // Always runs regardless of game mode — tech unlocks and facility upgrades
            // exist in science mode too. Pass rewindUT to skip events after the rewind point.
            ActionReplay.ReplayCommittedActions(MilestoneStore.Milestones, rewindUT);

            // Belt-and-suspenders epoch guard
            budgetDeductionEpoch = MilestoneStore.CurrentEpoch;
            resourceTickingSuspended = false;
        }

        #endregion

        #region Resource Ticking

        /// <summary>
        /// Ticks resource deltas for committed recordings in non-Flight scenes.
        /// Flight scene is handled by ParsekFlight.UpdateTimelinePlayback.
        /// </summary>
        private void Update()
        {
            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                return;

            if (resourceTickingSuspended)
            {
                ParsekLog.VerboseRateLimited("Scenario", "UpdateSuspended",
                    "Update: resource ticking suspended (waiting for coroutine)");
                return;
            }

            if (Funding.Instance == null && ResearchAndDevelopment.Instance == null
                && Reputation.Instance == null)
                return;

            double currentUT = Planetarium.GetUniversalTime();
            ResourceApplicator.TickStandalone(RecordingStore.CommittedRecordings, currentUT);
            ResourceApplicator.TickTrees(RecordingStore.CommittedTrees, currentUT);
        }


        #endregion

        #region Recording Serialization

        /// <summary>
        /// Saves standalone (non-tree) recordings to the given ConfigNode.
        /// Writes bulk data to external files, then persists metadata and mutable state
        /// (EVA linkage, chain linkage, terminal state, resource index) into RECORDING nodes.
        /// </summary>
        /// <summary>
        /// Restores mutable playback state (spawnedPid, lastResIdx, playbackEnabled) for
        /// standalone recordings from saved RECORDING ConfigNodes. Matches by recordingId.
        /// Tree recordings are skipped (restored from RECORDING_TREE nodes separately).
        /// </summary>
        /// <summary>
        /// Deserializes standalone RECORDING nodes and adds the resulting recordings to the list.
        /// Loads metadata, external files (trajectory/snapshots), EVA/chain linkage, terminal state,
        /// and resource application index for each recording.
        /// </summary>
        private static void LoadStandaloneRecordingsFromNodes(
            ConfigNode[] recNodes, List<Recording> recordings)
        {
            for (int r = 0; r < recNodes.Length; r++)
            {
                var recNode = recNodes[r];
                var rec = new Recording
                {
                    VesselName = Recording.ResolveLocalizedName(recNode.GetValue("vesselName") ?? "Unknown")
                };
                LoadRecordingMetadata(recNode, rec);

                // Load bulk data from external files
                RecordingStore.LoadRecordingFiles(rec);

                // Restore EVA child recording linkage
                rec.ParentRecordingId = recNode.GetValue("parentRecordingId");
                rec.EvaCrewName = recNode.GetValue("evaCrewName");

                // Restore chain linkage
                rec.ChainId = recNode.GetValue("chainId");
                string chainIdxStr = recNode.GetValue("chainIndex");
                if (chainIdxStr != null)
                {
                    int ci;
                    if (int.TryParse(chainIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ci))
                        rec.ChainIndex = ci;
                }
                string chainBranchStr = recNode.GetValue("chainBranch");
                if (chainBranchStr != null)
                {
                    int cb;
                    if (int.TryParse(chainBranchStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out cb))
                        rec.ChainBranch = cb;
                }

                // Restore spawned vessel pid for duplicate spawn detection
                string pidStr = recNode.GetValue("spawnedPid");
                if (pidStr != null)
                {
                    uint pid;
                    if (uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid))
                        rec.SpawnedVesselPersistentId = pid;
                }

                // Restore vessel destroyed flag
                string destroyedStr = recNode.GetValue("vesselDestroyed");
                if (destroyedStr != null)
                {
                    bool destroyed;
                    if (bool.TryParse(destroyedStr, out destroyed))
                        rec.VesselDestroyed = destroyed;
                }

                // Restore terminal state + vessel PID for standalone recordings
                string terminalStateStr = recNode.GetValue("terminalState");
                if (terminalStateStr != null)
                {
                    int ts;
                    if (int.TryParse(terminalStateStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out ts)
                        && Enum.IsDefined(typeof(TerminalState), ts))
                        rec.TerminalStateValue = (TerminalState)ts;
                }
                string vpidStr = recNode.GetValue("vesselPersistentId");
                if (vpidStr != null)
                {
                    uint vpid;
                    if (uint.TryParse(vpidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out vpid))
                        rec.VesselPersistentId = vpid;
                }

                // Restore terrain height at recording end (v7+)
                string thtStr = recNode.GetValue("terrainHeightAtEnd");
                if (thtStr != null)
                {
                    double tht;
                    if (double.TryParse(thtStr, NumberStyles.Float, CultureInfo.InvariantCulture, out tht))
                        rec.TerrainHeightAtEnd = tht;
                }

                // Restore antenna specs for CommNet ghost relay (Phase 6f)
                ConfigNode[] antennaNodes = recNode.GetNodes("ANTENNA_SPEC");
                if (antennaNodes != null && antennaNodes.Length > 0)
                {
                    rec.AntennaSpecs = new List<AntennaSpec>();
                    for (int a = 0; a < antennaNodes.Length; a++)
                    {
                        var spec = new AntennaSpec();
                        spec.partName = antennaNodes[a].GetValue("part") ?? "";

                        string powerStr = antennaNodes[a].GetValue("power");
                        if (powerStr != null)
                        {
                            double power;
                            if (double.TryParse(powerStr, NumberStyles.Float, CultureInfo.InvariantCulture, out power))
                                spec.antennaPower = power;
                        }

                        string combinableStr = antennaNodes[a].GetValue("combinable");
                        if (combinableStr != null)
                        {
                            bool combinable;
                            if (bool.TryParse(combinableStr, out combinable))
                                spec.antennaCombinable = combinable;
                        }

                        string exponentStr = antennaNodes[a].GetValue("exponent");
                        if (exponentStr != null)
                        {
                            double exponent;
                            if (double.TryParse(exponentStr, NumberStyles.Float, CultureInfo.InvariantCulture, out exponent))
                                spec.antennaCombinableExponent = exponent;
                        }

                        spec.antennaType = antennaNodes[a].GetValue("type") ?? "";

                        rec.AntennaSpecs.Add(spec);
                    }
                    ScenarioLog($"[Parsek Scenario] Loaded {rec.AntennaSpecs.Count} antenna spec(s) for '{rec.VesselName}'");
                }

                // Restore resource application index
                string resIdxStr = recNode.GetValue("lastResIdx");
                if (resIdxStr != null)
                {
                    int resIdx;
                    if (int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx))
                        rec.LastAppliedResourceIndex = resIdx;
                }

                // Always add — even degraded recordings (missing .prec → 0 points)
                // must occupy their slot to preserve index-based revert mapping.
                recordings.Add(rec);
                string phaseInfo = !string.IsNullOrEmpty(rec.SegmentPhase)
                    ? $", phase={rec.SegmentBodyName} {rec.SegmentPhase}" : "";
                string chainInfo = !string.IsNullOrEmpty(rec.ChainId)
                    ? $", chain={rec.ChainId.Substring(0, System.Math.Min(8, rec.ChainId.Length))}../{rec.ChainIndex}" : "";
                string enabledInfo = !rec.PlaybackEnabled ? ", DISABLED" : "";
                ScenarioLog($"[Parsek Scenario] Loaded recording: {rec.VesselName}, " +
                    $"{rec.Points.Count} points, {rec.OrbitSegments.Count} orbit segments" +
                    (rec.Points.Count > 0 ? $", UT {rec.StartUT:F0}-{rec.EndUT:F0}" : ", degraded (0 points)") +
                    (rec.VesselSnapshot != null ? " (vessel spawn)" :
                     rec.GhostVisualSnapshot != null ? " (ghost-only)" : "") +
                    phaseInfo + chainInfo + enabledInfo);
            }
        }

        /// <summary>
        /// Clears terminal state (Recovered/Destroyed) that was set after a vessel was spawned.
        /// On revert, the spawn is undone so the terminal state from the previous flight is stale.
        /// </summary>
        internal static void ClearPostSpawnTerminalState(Recording rec, string context = "recording")
        {
            if (rec.VesselSpawned && rec.TerminalStateValue.HasValue)
            {
                var ts = rec.TerminalStateValue.Value;
                if (ts == TerminalState.Recovered || ts == TerminalState.Destroyed)
                {
                    ParsekLog.Verbose("Scenario",
                        $"Clearing post-spawn terminal state {ts} for {context} '{rec.VesselName}'");
                    rec.TerminalStateValue = null;
                }
            }
        }

        private static void RestoreStandaloneMutableState(
            List<Recording> recordings, ConfigNode[] savedRecNodes)
        {
            var savedRecById = new Dictionary<string, ConfigNode>(savedRecNodes.Length);
            for (int s = 0; s < savedRecNodes.Length; s++)
            {
                string sid = savedRecNodes[s].GetValue("recordingId");
                if (!string.IsNullOrEmpty(sid))
                    savedRecById[sid] = savedRecNodes[s];
            }
            for (int i = 0; i < recordings.Count; i++)
            {
                // Skip tree recordings — their mutable state is restored from tree nodes
                if (recordings[i].TreeId != null) continue;

                ClearPostSpawnTerminalState(recordings[i]);

                recordings[i].VesselSpawned = false;
                recordings[i].SpawnAttempts = 0;
                recordings[i].SpawnDeathCount = 0;

                uint savedPid = 0;
                int resIdx = -1;
                ConfigNode savedNode;
                if (recordings[i].RecordingId != null && savedRecById.TryGetValue(recordings[i].RecordingId, out savedNode))
                {
                    string pidStr = savedNode.GetValue("spawnedPid");
                    if (pidStr != null && !uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out savedPid))
                        ParsekLog.Warn("Scenario", $"Failed to parse spawnedPid '{pidStr}' for recording #{i}");

                    string resIdxStr = savedNode.GetValue("lastResIdx");
                    if (resIdxStr != null && !int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx))
                        ParsekLog.Warn("Scenario", $"Failed to parse lastResIdx '{resIdxStr}' for recording #{i}");

                    string playbackEnabledStr = savedNode.GetValue("playbackEnabled");
                    if (playbackEnabledStr != null)
                    {
                        bool savedPlaybackEnabled;
                        if (bool.TryParse(playbackEnabledStr, out savedPlaybackEnabled))
                            recordings[i].PlaybackEnabled = savedPlaybackEnabled;
                        else
                            ParsekLog.Warn("Scenario", $"Failed to parse playbackEnabled '{playbackEnabledStr}' for recording #{i}");
                    }
                }
                else
                {
                    ParsekLog.Verbose("Scenario",
                        $"No saved mutable state for recording #{i} \"{recordings[i].VesselName}\" " +
                        $"(id={recordings[i].RecordingId ?? "null"}) — using defaults");
                }
                recordings[i].SpawnedVesselPersistentId = savedPid;
                recordings[i].LastAppliedResourceIndex = resIdx;
            }
        }

        /// <summary>
        /// Strips protoVessels from flightState whose vesselName matches a spawned recording name.
        /// Called during revert/rewind to remove orphaned spawned vessels before they contaminate
        /// the next launch quicksave. Uses name-based matching because ProtoVessel doesn't expose
        /// vessel persistentId directly.
        /// <param name="skipPrelaunch">When true (KSP Revert), PRELAUNCH vessels are kept —
        /// they are the user's launch vessel, not spawned vessels. When false (Parsek Rewind),
        /// all matching vessels are stripped — a PRELAUNCH vessel from a later launch is
        /// incompatible with the earlier game state being restored.</param>
        /// </summary>
        internal static int StripOrphanedSpawnedVessels(
            List<ProtoVessel> protoVessels, HashSet<string> spawnedNames, bool skipPrelaunch)
        {
            if (protoVessels == null || spawnedNames == null || spawnedNames.Count == 0)
                return 0;

            int stripped = 0;
            for (int i = protoVessels.Count - 1; i >= 0; i--)
            {
                var pv = protoVessels[i];
                if (!spawnedNames.Contains(Recording.ResolveLocalizedName(pv.vesselName)))
                    continue;

                // On KSP Revert, skip PRELAUNCH vessels — these are the user's launch
                // vessel on the pad. On Parsek Rewind, strip them too — a PRELAUNCH
                // vessel from a future launch is incompatible with the rewound state.
                if (skipPrelaunch && pv.situation == Vessel.Situations.PRELAUNCH)
                {
                    ParsekLog.Verbose("Scenario",
                        $"Skipping PRELAUNCH vessel '{pv.vesselName}' (revert — protecting launch vessel)");
                    continue;
                }

                ParsekLog.Info("Scenario",
                    $"Stripping orphaned spawned vessel '{pv.vesselName}' " +
                    $"(situation={pv.situation}) from flightState");
                protoVessels.RemoveAt(i);
                stripped++;
            }

            if (stripped > 0)
                ParsekLog.Info("Scenario",
                    $"StripOrphanedSpawnedVessels: removed {stripped} vessel(s) from flightState");

            return stripped;
        }

        private static void SaveStandaloneRecordings(ConfigNode node, List<Recording> recordings)
        {
            int count = 0;
            for (int r = 0; r < recordings.Count; r++)
            {
                var rec = recordings[r];

                // Skip tree recordings — they are saved under RECORDING_TREE nodes below
                if (rec.TreeId != null)
                    continue;
                count++;

                ConfigNode recNode = node.AddNode("RECORDING");

                // Write bulk data to external files
                if (!RecordingStore.SaveRecordingFiles(rec))
                    ScenarioLog($"[Parsek Scenario] WARNING: File write failed for '{rec.VesselName}'");

                SaveRecordingMetadata(recNode, rec);
                recNode.AddValue("vesselName", rec.VesselName);
                recNode.AddValue("pointCount", rec.Points.Count);

                // Persist EVA child recording linkage
                if (!string.IsNullOrEmpty(rec.ParentRecordingId))
                    recNode.AddValue("parentRecordingId", rec.ParentRecordingId);
                if (!string.IsNullOrEmpty(rec.EvaCrewName))
                    recNode.AddValue("evaCrewName", rec.EvaCrewName);

                // Persist chain linkage
                if (!string.IsNullOrEmpty(rec.ChainId))
                    recNode.AddValue("chainId", rec.ChainId);
                if (rec.ChainIndex >= 0)
                    recNode.AddValue("chainIndex", rec.ChainIndex);
                if (rec.ChainBranch > 0)
                    recNode.AddValue("chainBranch", rec.ChainBranch);

                // Persist spawned vessel pid so we can detect duplicates after scene changes
                if (rec.SpawnedVesselPersistentId != 0)
                    recNode.AddValue("spawnedPid", rec.SpawnedVesselPersistentId);

                if (rec.VesselDestroyed)
                    recNode.AddValue("vesselDestroyed", rec.VesselDestroyed.ToString());

                // Persist terminal state + vessel PID for standalone recordings
                if (rec.TerminalStateValue.HasValue)
                    recNode.AddValue("terminalState", ((int)rec.TerminalStateValue.Value).ToString(CultureInfo.InvariantCulture));
                if (rec.VesselPersistentId != 0)
                    recNode.AddValue("vesselPersistentId", rec.VesselPersistentId.ToString(CultureInfo.InvariantCulture));

                // Persist terrain height at recording end (v7+)
                if (!double.IsNaN(rec.TerrainHeightAtEnd))
                    recNode.AddValue("terrainHeightAtEnd", rec.TerrainHeightAtEnd.ToString("R", CultureInfo.InvariantCulture));

                // Persist antenna specs for CommNet ghost relay (Phase 6f)
                if (rec.AntennaSpecs != null)
                {
                    for (int a = 0; a < rec.AntennaSpecs.Count; a++)
                    {
                        var spec = rec.AntennaSpecs[a];
                        var specNode = recNode.AddNode("ANTENNA_SPEC");
                        specNode.AddValue("part", spec.partName ?? "");
                        specNode.AddValue("power", spec.antennaPower.ToString("R", CultureInfo.InvariantCulture));
                        specNode.AddValue("combinable", spec.antennaCombinable.ToString());
                        specNode.AddValue("exponent", spec.antennaCombinableExponent.ToString("R", CultureInfo.InvariantCulture));
                        if (!string.IsNullOrEmpty(spec.antennaType))
                            specNode.AddValue("type", spec.antennaType);
                    }
                }

                // Persist resource index so quickload doesn't re-apply deltas
                recNode.AddValue("lastResIdx", rec.LastAppliedResourceIndex);
            }
            ParsekLog.Verbose("Scenario", $"Saved {count} standalone recordings");
        }

        /// <summary>
        /// Saves versioned recording metadata and ghost-geometry metadata.
        /// Extracted for testability.
        /// </summary>
        internal static void SaveRecordingMetadata(ConfigNode recNode, Recording rec)
        {
            recNode.AddValue("recordingId", rec.RecordingId ?? "");
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            recNode.AddValue("loopPlayback", rec.LoopPlayback);
            recNode.AddValue("loopIntervalSeconds", rec.LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture));
            if (rec.LoopAnchorVesselId != 0)
                recNode.AddValue("loopAnchorPid", rec.LoopAnchorVesselId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(rec.LoopAnchorBodyName))
                recNode.AddValue("loopAnchorBodyName", rec.LoopAnchorBodyName);
            if (rec.LoopTimeUnit != LoopTimeUnit.Sec)
                recNode.AddValue("loopTimeUnit", rec.LoopTimeUnit.ToString());
            if (rec.PreLaunchFunds != 0)
                recNode.AddValue("preLaunchFunds", rec.PreLaunchFunds.ToString("R", CultureInfo.InvariantCulture));
            if (rec.PreLaunchScience != 0)
                recNode.AddValue("preLaunchScience", rec.PreLaunchScience.ToString("R", CultureInfo.InvariantCulture));
            if (rec.PreLaunchReputation != 0)
                recNode.AddValue("preLaunchRep", rec.PreLaunchReputation.ToString("R", CultureInfo.InvariantCulture));

            // Rewind save metadata
            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
            {
                recNode.AddValue("rewindSave", rec.RewindSaveFileName);
                recNode.AddValue("rewindResFunds", rec.RewindReservedFunds.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("rewindResSci", rec.RewindReservedScience.ToString("R", CultureInfo.InvariantCulture));
                recNode.AddValue("rewindResRep", rec.RewindReservedRep.ToString("R", CultureInfo.InvariantCulture));
            }

            // UI grouping tags (multi-group membership)
            if (rec.RecordingGroups != null)
                for (int g = 0; g < rec.RecordingGroups.Count; g++)
                    recNode.AddValue("recordingGroup", rec.RecordingGroups[g]);

            // Atmosphere segment metadata (only if set, saves space)
            if (!string.IsNullOrEmpty(rec.SegmentPhase))
                recNode.AddValue("segmentPhase", rec.SegmentPhase);
            if (!string.IsNullOrEmpty(rec.SegmentBodyName))
                recNode.AddValue("segmentBodyName", rec.SegmentBodyName);
            if (!rec.PlaybackEnabled)
                recNode.AddValue("playbackEnabled", rec.PlaybackEnabled.ToString());
            if (rec.Hidden)
                recNode.AddValue("hidden", rec.Hidden.ToString());

            ParsekLog.Verbose("Scenario", $"Saved metadata: id={rec.RecordingId}, " +
                $"phase={rec.SegmentPhase ?? "(none)"}, body={rec.SegmentBodyName ?? "(none)"}, " +
                $"playback={rec.PlaybackEnabled}, hidden={rec.Hidden}, chain={rec.ChainId ?? "(none)"}/{rec.ChainIndex}, " +
                $"preLaunch=[F={rec.PreLaunchFunds:F0}, S={rec.PreLaunchScience:F1}, R={rec.PreLaunchReputation:F1}]");
        }

        /// <summary>
        /// Loads versioned recording metadata and ghost-geometry metadata.
        /// Missing fields are treated as old-format recordings.
        /// Extracted for testability.
        /// </summary>
        internal static void LoadRecordingMetadata(ConfigNode recNode, Recording rec)
        {
            string id = recNode.GetValue("recordingId");
            if (!string.IsNullOrEmpty(id))
                rec.RecordingId = id;

            string formatVersionStr = recNode.GetValue("recordingFormatVersion");
            if (formatVersionStr != null)
            {
                int formatVersion;
                if (int.TryParse(formatVersionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out formatVersion))
                    rec.RecordingFormatVersion = formatVersion;
            }

            string geomVersionStr = recNode.GetValue("ghostGeometryVersion");
            if (geomVersionStr != null)
            {
                int geomVersion;
                if (int.TryParse(geomVersionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out geomVersion))
                    rec.GhostGeometryVersion = geomVersion;
            }

            string loopPlaybackStr = recNode.GetValue("loopPlayback");
            if (loopPlaybackStr != null)
            {
                bool loopPlayback;
                if (bool.TryParse(loopPlaybackStr, out loopPlayback))
                    rec.LoopPlayback = loopPlayback;
            }

            string loopIntervalStr = recNode.GetValue("loopIntervalSeconds")
                                  ?? recNode.GetValue("loopPauseSeconds"); // migration fallback
            if (loopIntervalStr != null)
            {
                double loopIntervalSeconds;
                if (double.TryParse(loopIntervalStr, NumberStyles.Float, CultureInfo.InvariantCulture, out loopIntervalSeconds))
                    rec.LoopIntervalSeconds = loopIntervalSeconds;
            }

            string loopTimeUnitStr = recNode.GetValue("loopTimeUnit");
            if (loopTimeUnitStr != null)
            {
                LoopTimeUnit loopTimeUnit;
                if (System.Enum.TryParse(loopTimeUnitStr, out loopTimeUnit))
                    rec.LoopTimeUnit = loopTimeUnit;
            }

            string loopAnchorPidStr = recNode.GetValue("loopAnchorPid");
            if (loopAnchorPidStr != null)
            {
                uint loopAnchorPid;
                if (uint.TryParse(loopAnchorPidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out loopAnchorPid))
                    rec.LoopAnchorVesselId = loopAnchorPid;
            }

            string loopAnchorBodyNameStr = recNode.GetValue("loopAnchorBodyName");
            if (!string.IsNullOrEmpty(loopAnchorBodyNameStr))
                rec.LoopAnchorBodyName = loopAnchorBodyNameStr;

            rec.GhostGeometryRelativePath = recNode.GetValue("ghostGeometryPath");
            string strategy = recNode.GetValue("ghostGeometryStrategy");
            if (!string.IsNullOrEmpty(strategy))
                rec.GhostGeometryCaptureStrategy = strategy;
            string probeStatus = recNode.GetValue("ghostGeometryProbeStatus");
            if (!string.IsNullOrEmpty(probeStatus))
                rec.GhostGeometryProbeStatus = probeStatus;
            string geomAvailableStr = recNode.GetValue("ghostGeometryAvailable");
            if (geomAvailableStr != null)
            {
                bool geomAvailable;
                if (bool.TryParse(geomAvailableStr, out geomAvailable))
                    rec.GhostGeometryAvailable = geomAvailable;
            }
            rec.GhostGeometryCaptureError = recNode.GetValue("ghostGeometryError");

            string preLaunchFundsStr = recNode.GetValue("preLaunchFunds");
            if (preLaunchFundsStr != null)
            {
                double preLaunchFunds;
                if (double.TryParse(preLaunchFundsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out preLaunchFunds))
                    rec.PreLaunchFunds = preLaunchFunds;
            }
            string preLaunchScienceStr = recNode.GetValue("preLaunchScience");
            if (preLaunchScienceStr != null)
            {
                double preLaunchScience;
                if (double.TryParse(preLaunchScienceStr, NumberStyles.Float, CultureInfo.InvariantCulture, out preLaunchScience))
                    rec.PreLaunchScience = preLaunchScience;
            }
            string preLaunchRepStr = recNode.GetValue("preLaunchRep");
            if (preLaunchRepStr != null)
            {
                float preLaunchRep;
                if (float.TryParse(preLaunchRepStr, NumberStyles.Float, CultureInfo.InvariantCulture, out preLaunchRep))
                    rec.PreLaunchReputation = preLaunchRep;
            }

            // Rewind save metadata
            rec.RewindSaveFileName = recNode.GetValue("rewindSave");
            string rewindFundsStr = recNode.GetValue("rewindResFunds");
            if (rewindFundsStr != null)
            {
                double rewindFunds;
                if (double.TryParse(rewindFundsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rewindFunds))
                    rec.RewindReservedFunds = rewindFunds;
            }
            string rewindSciStr = recNode.GetValue("rewindResSci");
            if (rewindSciStr != null)
            {
                double rewindSci;
                if (double.TryParse(rewindSciStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rewindSci))
                    rec.RewindReservedScience = rewindSci;
            }
            string rewindRepStr = recNode.GetValue("rewindResRep");
            if (rewindRepStr != null)
            {
                float rewindRep;
                if (float.TryParse(rewindRepStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rewindRep))
                    rec.RewindReservedRep = rewindRep;
            }

            // UI grouping tags (multi-group membership, backward compat with single value)
            string[] groups = recNode.GetValues("recordingGroup");
            if (groups != null && groups.Length > 0)
            {
                for (int g = 0; g < groups.Length; g++)
                    groups[g] = Recording.ResolveLocalizedName(groups[g]);
                rec.RecordingGroups = new List<string>(groups);
            }

            // Atmosphere segment metadata
            rec.SegmentPhase = recNode.GetValue("segmentPhase");
            rec.SegmentBodyName = recNode.GetValue("segmentBodyName");
            string playbackEnabledStr = recNode.GetValue("playbackEnabled");
            if (playbackEnabledStr != null)
            {
                bool playbackEnabled;
                if (bool.TryParse(playbackEnabledStr, out playbackEnabled))
                    rec.PlaybackEnabled = playbackEnabled;
            }
            string hiddenStr = recNode.GetValue("hidden");
            if (hiddenStr != null)
            {
                bool hidden;
                if (bool.TryParse(hiddenStr, out hidden))
                    rec.Hidden = hidden;
            }

            ParsekLog.Verbose("Scenario", $"Loaded metadata: id={rec.RecordingId}, " +
                $"phase={rec.SegmentPhase ?? "(none)"}, body={rec.SegmentBodyName ?? "(none)"}, " +
                $"playback={rec.PlaybackEnabled}, hidden={rec.Hidden}, chain={rec.ChainId ?? "(none)"}/{rec.ChainIndex}, " +
                $"preLaunch=[F={rec.PreLaunchFunds:F0}, S={rec.PreLaunchScience:F1}, R={rec.PreLaunchReputation:F1}]");
        }

        /// <summary>
        /// Returns true if OnSave should warn about a save folder mismatch.
        /// This detects when HighLogic.SaveFolder has changed since the scenario
        /// was loaded, which could cause file writes to target the wrong save.
        /// </summary>
        internal static bool IsSaveFolderMismatch(string scenarioFolder, string currentFolder)
        {
            return !string.IsNullOrEmpty(scenarioFolder) && currentFolder != scenarioFolder;
        }

        #region Vessel Lifecycle Events

        /// <summary>
        /// Prepares a standalone pending recording for ghost-only commit (no vessel spawn).
        /// Nulls vessel snapshot and unreserves crew. Call RecordingStore.CommitPending() after this.
        /// </summary>
        private static void AutoCommitGhostOnly(Recording pending)
        {
            CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
            pending.VesselSnapshot = null;
            ParsekLog.Info("Scenario", $"Auto-commit ghost-only: '{pending.VesselName}'" +
                (pending.TerminalStateValue.HasValue ? $" (terminal={pending.TerminalStateValue.Value})" : ""));
        }

        /// <summary>
        /// Prepares all recordings in a pending tree for ghost-only commit (no vessel spawn).
        /// Nulls vessel snapshot and unreserves crew. Call RecordingStore.CommitPendingTree() after this.
        /// </summary>
        private static void AutoCommitTreeGhostOnly(RecordingTree tree)
        {
            foreach (var rec in tree.Recordings.Values)
            {
                CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                rec.VesselSnapshot = null;
            }
            ParsekLog.Info("Scenario", $"Auto-commit tree ghost-only: tree '{tree.Id}' " +
                $"({tree.Recordings.Count} recordings)");
        }

        private void OnVesselRecovered(ProtoVessel pv, bool fromTrackingStation)
        {
            if (pv == null) return;

            // During rewind, vessels are stripped from the save which fires onVesselRecovered.
            // Ignore these — the recordings must keep their snapshots for ghost playback and spawning.
            if (RecordingStore.IsRewinding)
            {
                ParsekLog.Info("Scenario",
                    $"Ignoring recovery of '{pv.vesselName}' during rewind");
                return;
            }

            string vesselName = pv.vesselName;
            if (string.IsNullOrEmpty(vesselName)) return;

            double now = Planetarium.GetUniversalTime();
            bool updated = UpdateRecordingsForTerminalEvent(vesselName, TerminalState.Recovered, now);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{vesselName}' recovered — recording(s) updated with Recovered terminal state");
        }

        private void OnVesselTerminated(ProtoVessel pv)
        {
            if (pv == null) return;
            if (RecordingStore.IsRewinding) return;
            string vesselName = pv.vesselName;
            if (string.IsNullOrEmpty(vesselName)) return;

            double now = Planetarium.GetUniversalTime();
            // onVesselTerminated also fires after onVesselRecovered for the same vessel.
            // The guard in UpdateRecordingsForTerminalEvent prevents overwriting Recovered with Destroyed.
            bool updated = UpdateRecordingsForTerminalEvent(vesselName, TerminalState.Destroyed, now);
            if (updated)
                ParsekLog.Info("Scenario", $"Vessel '{vesselName}' terminated — recording(s) updated with Destroyed terminal state");
        }

        /// <summary>
        /// Finds recordings matching the given vessel name and updates their terminal state.
        /// Checks pending recording, pending tree recordings, and committed recordings.
        /// Recovered/Destroyed can overwrite situation-based terminal states (Orbiting, Landed, etc.)
        /// that were set by OnSceneChangeRequested. Only prevents Destroyed from overwriting Recovered
        /// (onVesselTerminated fires after onVesselRecovered for the same vessel).
        /// </summary>
        private static bool UpdateRecordingsForTerminalEvent(string vesselName, TerminalState state, double ut)
        {
            bool anyUpdated = false;

            // Check pending standalone recording
            if (RecordingStore.HasPending)
            {
                var pending = RecordingStore.Pending;
                if (MatchesVessel(pending, vesselName) && CanOverwriteTerminalState(pending.TerminalStateValue, state))
                {
                    pending.TerminalStateValue = state;
                    pending.ExplicitEndUT = ut;
                    CrewReservationManager.UnreserveCrewInSnapshot(pending.VesselSnapshot);
                    pending.VesselSnapshot = null;
                    anyUpdated = true;
                    ParsekLog.Verbose("Scenario", $"Updated pending recording '{pending.VesselName}' with {state}");
                }
            }

            // Check pending tree recordings
            if (RecordingStore.HasPendingTree)
            {
                foreach (var rec in RecordingStore.PendingTree.Recordings.Values)
                {
                    if (MatchesVessel(rec, vesselName) && CanOverwriteTerminalState(rec.TerminalStateValue, state))
                    {
                        rec.TerminalStateValue = state;
                        rec.ExplicitEndUT = ut;
                        CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                        rec.VesselSnapshot = null;
                        anyUpdated = true;
                        ParsekLog.Verbose("Scenario", $"Updated pending tree recording '{rec.VesselName}' with {state}");
                    }
                }
            }

            // Committed recordings are never modified by terminal events. Recovery or
            // destruction of a real vessel (whether spawned by Parsek or pre-existing) must
            // not alter frozen recording data — snapshot, terminal state, crew, or EndUT.
            // Name-based matching is ambiguous (multiple recordings share vessel names) and
            // any mutation persists through reverts, permanently preventing re-spawn.

            return anyUpdated;
        }

        /// <summary>
        /// Determines whether a new terminal state can overwrite the existing one.
        /// Recovered/Destroyed can overwrite null or situation-based states (Orbiting, Landed, etc.).
        /// Destroyed cannot overwrite Recovered (onVesselTerminated fires after onVesselRecovered).
        /// </summary>
        private static bool CanOverwriteTerminalState(TerminalState? existing, TerminalState incoming)
        {
            if (!existing.HasValue) return true;

            // Recovered and Destroyed are "final" — only Recovered blocks further overwrite
            if (existing.Value == TerminalState.Recovered) return false;
            if (existing.Value == TerminalState.Destroyed) return false;

            // Situation-based states (Orbiting, Landed, Splashed, SubOrbital) can be overwritten
            // by Recovered or Destroyed (lifecycle events take precedence over scene-exit situation)
            return true;
        }

        /// <summary>
        /// Checks if a recording matches the given vessel name.
        /// Uses name-based matching (ProtoVessel doesn't expose vessel persistentId directly).
        /// </summary>
        private static bool MatchesVessel(Recording rec, string vesselName)
        {
            return !string.IsNullOrEmpty(rec.VesselName)
                && string.Equals(rec.VesselName, vesselName, StringComparison.Ordinal);
        }

        #endregion

        private static void ScenarioLog(string message)
        {
            const string legacyPrefix = "[Parsek Scenario] ";
            string clean = message ?? "(empty)";
            if (clean.StartsWith(legacyPrefix, StringComparison.Ordinal))
                clean = clean.Substring(legacyPrefix.Length);

            if (clean.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
            {
                int idx = clean.IndexOf(':');
                string trimmed = idx >= 0 ? clean.Substring(idx + 1).TrimStart() : clean;
                ParsekLog.Warn("Scenario", trimmed);
                return;
            }

            ParsekLog.Info("Scenario", clean);
        }

        #endregion

        /// <summary>
        /// Resets session state when transitioning to main menu, preventing stale
        /// in-memory recordings from leaking between saves with the same name.
        /// </summary>
        private static void OnMainMenuTransition(GameScenes newScene)
        {
            if (newScene == GameScenes.MAINMENU)
            {
                initialLoadDone = false;
                lastSaveFolder = null;
                lastOnSaveScene = GameScenes.MAINMENU;
                RecordingStore.PendingCleanupPids = null;
                RecordingStore.PendingCleanupNames = null;
                ParsekLog.Info("Scenario",
                    "Main menu transition — reset initialLoadDone to prevent stale data leak");
            }
        }

        public void OnDestroy()
        {
            stateRecorder?.Unsubscribe();
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
        }
    }
}
