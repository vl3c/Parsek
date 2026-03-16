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

        #region Crew Replacements

        // Maps reserved kerbal name → replacement kerbal name
        private static Dictionary<string, string> crewReplacements = new Dictionary<string, string>();

        // Group hierarchy: child group name → parent group name
        internal static Dictionary<string, string> groupParents = new Dictionary<string, string>();

        /// <summary>
        /// Read-only access to current replacement mappings. For testing/diagnostics.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> CrewReplacements => crewReplacements;

        #endregion

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
            if (crewReplacements.Count > 0)
            {
                ConfigNode replacementsNode = node.AddNode("CREW_REPLACEMENTS");
                foreach (var kvp in crewReplacements)
                {
                    ConfigNode entry = replacementsNode.AddNode("ENTRY");
                    entry.AddValue("original", kvp.Key);
                    entry.AddValue("replacement", kvp.Value);
                }
                ScenarioLog($"[Parsek Scenario] Saved {crewReplacements.Count} crew replacement(s)");
            }

            // Persist group hierarchy
            if (groupParents.Count > 0)
            {
                ConfigNode hierarchyNode = node.AddNode("GROUP_HIERARCHY");
                foreach (var kvp in groupParents)
                {
                    ConfigNode entry = hierarchyNode.AddNode("ENTRY");
                    entry.AddValue("child", kvp.Key);
                    entry.AddValue("parent", kvp.Value);
                }
                ScenarioLog($"[Parsek Scenario] Saved group hierarchy: {groupParents.Count} entries");
            }

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
        }

        // Static flag: only load from save once per KSP session.
        // On revert, the launch quicksave has stale data — the in-memory
        // static list is the real source of truth within a session.
        private static bool initialLoadDone = false;
        private static string lastSaveFolder = null;
        private static uint budgetDeductionEpoch = 0;

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
                LoadCrewReplacements(node);
                LoadGroupHierarchy(node);
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

                    // Reset ALL playback state (recordings + trees)
                    var (standaloneCount, treeCount) = RecordingStore.ResetAllPlaybackState();
                    ParsekLog.Info("Rewind",
                        $"OnLoad: resetting playback state for {standaloneCount} recordings + {treeCount} trees");

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
                    ReserveSnapshotCrew();

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

                    return; // Skip ALL existing revert/scene-change logic
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

                bool isRevert = savedEpoch < MilestoneStore.CurrentEpoch
                                || totalSavedRecCount < recordings.Count;
                ParsekLog.Verbose("Scenario",
                    $"OnLoad: revert detection — savedEpoch={savedEpoch}, currentEpoch={MilestoneStore.CurrentEpoch}, " +
                    $"savedRecNodes={savedRecNodes.Length}, savedTreeRecs={savedTreeRecCount}, " +
                    $"memoryRecordings={recordings.Count}, isRevert={isRevert}");

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
                    recordings[i].VesselSpawned = false;
                    recordings[i].SpawnAttempts = 0;
                    recordings[i].SpawnedVesselPersistentId = 0;

                    recordings[i].LastAppliedResourceIndex = -1;
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

                ReserveSnapshotCrew();
                ParsekLog.Info("Scenario", $"{(isRevert ? "Revert" : "Scene change")} — preserving {recordings.Count} session recordings");
                return;
            }

            initialLoadDone = true;

            // Clear any pending state leaked from a previous save.
            // Static fields survive scene changes, so pending recordings/trees
            // from save A would otherwise be auto-committed into save B's timeline.
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
                RecordingStore.RewindReserved = default(ResourceBudget.BudgetSummary);
                RecordingStore.RewindBaselineFunds = 0;
                RecordingStore.RewindBaselineScience = 0;
                RecordingStore.RewindBaselineRep = 0;
            }

            recordings.Clear();

            ConfigNode[] recNodes = node.GetNodes("RECORDING");
            ScenarioLog($"[Parsek Scenario] Loading {recNodes.Length} committed recordings");

            LoadStandaloneRecordingsFromNodes(recNodes, recordings);

            // Validate chain integrity before any playback
            RecordingStore.ValidateChains();

            // Load committed recording trees.
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

            // Clean orphaned sidecar files (recordings deleted in previous sessions)
            RecordingStore.CleanOrphanFiles();

            // Restore milestone mutable state (LastReplayedEventIndex) from .sfs
            MilestoneStore.RestoreMutableState(node);

            ReserveSnapshotCrew();

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

            if (crewReplacements.Count > 0)
            {
                ParsekLog.Info("Scenario", $"Crew reservations active ({crewReplacements.Count}):");
                foreach (var kvp in crewReplacements)
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
                    UnreserveCrewInSnapshot(rec.VesselSnapshot);
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
                            UnreserveCrewInSnapshot(pending.VesselSnapshot);
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
                                UnreserveCrewInSnapshot(rec.VesselSnapshot);
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

            GameStateRecorder.SuppressResourceEvents = true;
            try
            {
                if (budget.reservedFunds > 0 && Funding.Instance != null)
                {
                    double fundsBefore = Funding.Instance.Funds;
                    Funding.Instance.AddFunds(-budget.reservedFunds, TransactionReasons.None);
                    ParsekLog.Info("Scenario",
                        $"Budget deduction: funds {fundsBefore:F0} → {Funding.Instance.Funds:F0} (reserved={budget.reservedFunds:F0})");
                }

                if (budget.reservedScience > 0 && ResearchAndDevelopment.Instance != null)
                {
                    double scienceBefore = ResearchAndDevelopment.Instance.Science;
                    ResearchAndDevelopment.Instance.AddScience(
                        -(float)budget.reservedScience, TransactionReasons.None);
                    ParsekLog.Info("Scenario",
                        $"Budget deduction: science {scienceBefore:F1} → {ResearchAndDevelopment.Instance.Science:F1} (reserved={budget.reservedScience:F1})");
                }

                if (budget.reservedReputation > 0 && Reputation.Instance != null)
                {
                    float repBefore = Reputation.Instance.reputation;
                    Reputation.Instance.AddReputation(
                        -(float)budget.reservedReputation, TransactionReasons.None);
                    ParsekLog.Info("Scenario",
                        $"Budget deduction: reputation {repBefore:F1} → {Reputation.Instance.reputation:F1} (reserved={budget.reservedReputation:F1})");
                }
            }
            finally
            {
                GameStateRecorder.SuppressResourceEvents = false;
            }

            // Replay committed actions (tech, parts, facilities, crew) before marking
            ActionReplay.ReplayCommittedActions(MilestoneStore.Milestones);

            // Mark all recordings as fully applied so that:
            // 1. ResourceBudget.ComputeTotal returns 0 reserved (deduction already covers it)
            // 2. Ghost replay doesn't re-apply resource deltas (avoiding double-subtraction)
            // 3. The UI correctly shows current funds as available (no second subtraction)
            var recordings = RecordingStore.CommittedRecordings;
            int recMarked = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].Points.Count > 0 && recordings[i].LastAppliedResourceIndex < recordings[i].Points.Count - 1)
                {
                    int oldIdx = recordings[i].LastAppliedResourceIndex;
                    recordings[i].LastAppliedResourceIndex = recordings[i].Points.Count - 1;
                    recMarked++;
                    ParsekLog.Verbose("Scenario",
                        $"  recording #{i} '{recordings[i].VesselName}': lastAppliedResourceIndex {oldIdx} → {recordings[i].LastAppliedResourceIndex}");
                }
            }

            // Mark all committed trees as ResourcesApplied (deduction already covers their costs)
            var committedTrees = RecordingStore.CommittedTrees;
            int treeMarked = 0;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                if (!committedTrees[i].ResourcesApplied)
                {
                    committedTrees[i].ResourcesApplied = true;
                    treeMarked++;
                }
            }
            ParsekLog.Verbose("Scenario", $"  Marked {treeMarked} tree(s) as ResourcesApplied");

            ParsekLog.Info("Scenario",
                $"Budget deduction applied for epoch {MilestoneStore.CurrentEpoch} — " +
                $"{recMarked} recording(s) and {treeMarked} tree(s) marked as fully applied");
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
            // Without this yield, AddFunds/AddScience/SetUniversalTime modify the
            // OLD instances which are then destroyed when the new scene finishes loading.
            yield return null;

            // Wait until ALL resource singletons are available
            int maxWait = 120; // ~2 seconds at 60fps
            while (maxWait-- > 0
                   && (Funding.Instance == null
                       || ResearchAndDevelopment.Instance == null
                       || Reputation.Instance == null))
                yield return null;

            if (Funding.Instance == null || ResearchAndDevelopment.Instance == null || Reputation.Instance == null)
            {
                ParsekLog.Warn("Rewind",
                    "Resource adjustment aborted: singletons not available after 120 frames");
                resourceTickingSuspended = false;
                yield break;
            }

            var ic = CultureInfo.InvariantCulture;

            // Apply adjusted UT — must happen after singletons are ready (new Planetarium).
            // Setting UT before LoadScene does NOT work — the scene transition overwrites it.
            if (adjustedUT > 0)
            {
                double prePlanetariumUT = Planetarium.GetUniversalTime();
                Planetarium.SetUniversalTime(adjustedUT);
                ParsekLog.Info("Rewind",
                    $"UT adjustment: {prePlanetariumUT.ToString("F1", ic)} → {adjustedUT.ToString("F1", ic)} " +
                    $"(post-set check: {Planetarium.GetUniversalTime().ToString("F1", ic)})");
            }

            // Log pre-adjustment state
            double preFunds = Funding.Instance.Funds;
            float preScience = ResearchAndDevelopment.Instance.Science;
            float preRep = Reputation.Instance.reputation;
            ParsekLog.Info("Rewind",
                $"Pre-adjustment state: funds={preFunds.ToString("F1", ic)}, " +
                $"science={preScience.ToString("F1", ic)}, " +
                $"rep={preRep.ToString("F1", ic)}, " +
                $"baseline: funds={baselineFunds.ToString("F1", ic)}, " +
                $"science={baselineScience.ToString("F1", ic)}, " +
                $"rep={baselineRep.ToString("F1", ic)}");

            // Reset resources to baseline (the pre-launch snapshot values).
            // Ghost playback will re-apply recording resource deltas at the correct UT.
            // ActionReplay handles game-state actions (tech, parts, facilities) without
            // resource deduction — those costs are part of the recording deltas.
            double fundsCorrection = baselineFunds - preFunds;
            double scienceCorrection = baselineScience - preScience;
            double repCorrection = (double)baselineRep - (double)preRep;

            ParsekLog.Info("Rewind",
                $"Resource reset to baseline: funds={baselineFunds.ToString("F1", ic)}, " +
                $"science={baselineScience.ToString("F1", ic)}, " +
                $"rep={baselineRep.ToString("F1", ic)}, " +
                $"correction: {fundsCorrection.ToString("F1", ic)}, " +
                $"{scienceCorrection.ToString("F1", ic)}, " +
                $"{repCorrection.ToString("F1", ic)}");

            // Apply with suppression (prevent synthetic game state events)
            GameStateRecorder.SuppressResourceEvents = true;
            try
            {
                if (fundsCorrection != 0)
                    Funding.Instance.AddFunds(fundsCorrection, TransactionReasons.None);
                if (scienceCorrection != 0)
                    ResearchAndDevelopment.Instance.AddScience((float)scienceCorrection, TransactionReasons.None);
                if (repCorrection != 0)
                    Reputation.Instance.AddReputation((float)repCorrection, TransactionReasons.None);
            }
            finally
            {
                GameStateRecorder.SuppressResourceEvents = false;
            }

            // Log post-adjustment state
            ParsekLog.Info("Rewind",
                $"Post-adjustment state: funds={Funding.Instance.Funds.ToString("F1", ic)}, " +
                $"science={ResearchAndDevelopment.Instance.Science.ToString("F1", ic)}, " +
                $"rep={Reputation.Instance.reputation.ToString("F1", ic)}");

            // Replay committed actions (tech, parts, facilities, crew).
            // Resources are NOT marked fully applied — ghost playback will re-apply
            // recording resource deltas at the correct UT as the timeline replays.
            // Pass rewindUT to skip events from after the rewind point (prevents
            // replaying tech unlocks / part purchases that haven't happened yet).
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
            TickStandaloneResourceDeltas(currentUT);
            TickTreeResourceDeltas(currentUT);
        }

        private void TickStandaloneResourceDeltas(double currentUT)
        {
            var recordings = RecordingStore.CommittedRecordings;
            bool anyApplied = false;

            for (int i = 0; i < recordings.Count; i++)
            {
                var rec = recordings[i];

                if (rec.TreeId != null) continue;
                if (rec.LoopPlayback) continue;
                if (rec.Points.Count < 2) continue;

                int targetIndex = ParsekFlight.ComputeTargetResourceIndex(
                    rec.Points, rec.LastAppliedResourceIndex, currentUT);

                if (targetIndex <= rec.LastAppliedResourceIndex)
                    continue;

                int startIdx = Math.Max(rec.LastAppliedResourceIndex, 0);
                TrajectoryPoint fromPoint = rec.Points[startIdx];
                TrajectoryPoint toPoint = rec.Points[targetIndex];

                double fundsDelta = toPoint.funds - fromPoint.funds;
                float scienceDelta = toPoint.science - fromPoint.science;
                float repDelta = toPoint.reputation - fromPoint.reputation;

                GameStateRecorder.SuppressResourceEvents = true;
                try
                {
                    if (fundsDelta != 0 && Funding.Instance != null)
                    {
                        if (fundsDelta < 0 && Funding.Instance.Funds + fundsDelta < 0)
                            fundsDelta = -Funding.Instance.Funds;
                        Funding.Instance.AddFunds(fundsDelta, TransactionReasons.None);
                    }

                    if (scienceDelta != 0 && ResearchAndDevelopment.Instance != null)
                    {
                        if (scienceDelta < 0 && ResearchAndDevelopment.Instance.Science + scienceDelta < 0)
                            scienceDelta = -ResearchAndDevelopment.Instance.Science;
                        ResearchAndDevelopment.Instance.AddScience(scienceDelta, TransactionReasons.None);
                    }

                    if (repDelta != 0 && Reputation.Instance != null)
                    {
                        if (repDelta < 0 && Reputation.CurrentRep + repDelta < 0)
                            repDelta = -Reputation.CurrentRep;
                        Reputation.Instance.AddReputation(repDelta, TransactionReasons.None);
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressResourceEvents = false;
                }

                rec.LastAppliedResourceIndex = targetIndex;
                anyApplied = true;

                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Info("Scenario",
                    $"Resource tick: \"{rec.VesselName}\" idx {startIdx}\u2192{targetIndex}" +
                    $" funds={fundsDelta.ToString("+0.0;-0.0", ic)} sci={scienceDelta.ToString("+0.0;-0.0", ic)} rep={repDelta.ToString("+0.0;-0.0", ic)}");

                if (targetIndex == rec.Points.Count - 1)
                {
                    ParsekLog.Info("Scenario",
                        $"Resource tick complete for \"{rec.VesselName}\"");
                }
            }

            if (anyApplied)
                ResourceBudget.Invalidate();
        }

        private void TickTreeResourceDeltas(double currentUT)
        {
            var trees = RecordingStore.CommittedTrees;
            bool anyApplied = false;

            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree.ResourcesApplied)
                    continue;

                double treeEndUT = 0;
                foreach (var rec in tree.Recordings.Values)
                {
                    double recEnd = rec.EndUT;
                    if (recEnd > treeEndUT) treeEndUT = recEnd;
                }

                if (currentUT <= treeEndUT)
                    continue;

                GameStateRecorder.SuppressResourceEvents = true;
                try
                {
                    if (tree.DeltaFunds != 0 && Funding.Instance != null)
                    {
                        double delta = tree.DeltaFunds;
                        if (delta < 0 && Funding.Instance.Funds + delta < 0)
                            delta = -Funding.Instance.Funds;
                        Funding.Instance.AddFunds(delta, TransactionReasons.None);
                        var ic = CultureInfo.InvariantCulture;
                        ParsekLog.Info("Scenario",
                            $"Tree resource tick: funds {delta.ToString("+0.0;-0.0", ic)} (tree '{tree.TreeName}')");
                    }

                    if (tree.DeltaScience != 0 && ResearchAndDevelopment.Instance != null)
                    {
                        double delta = tree.DeltaScience;
                        if (delta < 0 && ResearchAndDevelopment.Instance.Science + delta < 0)
                            delta = -ResearchAndDevelopment.Instance.Science;
                        ResearchAndDevelopment.Instance.AddScience((float)delta, TransactionReasons.None);
                        var ic = CultureInfo.InvariantCulture;
                        ParsekLog.Info("Scenario",
                            $"Tree resource tick: science {delta.ToString("+0.0;-0.0", ic)} (tree '{tree.TreeName}')");
                    }

                    if (tree.DeltaReputation != 0 && Reputation.Instance != null)
                    {
                        float delta = tree.DeltaReputation;
                        if (delta < 0 && Reputation.CurrentRep + delta < 0)
                            delta = -Reputation.CurrentRep;
                        Reputation.Instance.AddReputation(delta, TransactionReasons.None);
                        var ic = CultureInfo.InvariantCulture;
                        ParsekLog.Info("Scenario",
                            $"Tree resource tick: reputation {delta.ToString("+0.0;-0.0", ic)} (tree '{tree.TreeName}')");
                    }
                }
                finally
                {
                    GameStateRecorder.SuppressResourceEvents = false;
                }

                tree.ResourcesApplied = true;
                anyApplied = true;
                ParsekLog.Info("Scenario",
                    $"Tree resource lump sum applied for '{tree.TreeName}'");
            }

            if (anyApplied)
                ResourceBudget.Invalidate();
        }

        #endregion

        #region Crew Reservation

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
            ConfigNode[] recNodes, List<RecordingStore.Recording> recordings)
        {
            for (int r = 0; r < recNodes.Length; r++)
            {
                var recNode = recNodes[r];
                var rec = new RecordingStore.Recording
                {
                    VesselName = recNode.GetValue("vesselName") ?? "Unknown"
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

        private static void RestoreStandaloneMutableState(
            List<RecordingStore.Recording> recordings, ConfigNode[] savedRecNodes)
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

                recordings[i].VesselSpawned = false;
                recordings[i].SpawnAttempts = 0;

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

        private static void SaveStandaloneRecordings(ConfigNode node, List<RecordingStore.Recording> recordings)
        {
            for (int r = 0; r < recordings.Count; r++)
            {
                var rec = recordings[r];

                // Skip tree recordings — they are saved under RECORDING_TREE nodes below
                if (rec.TreeId != null)
                    continue;

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

                // Persist resource index so quickload doesn't re-apply deltas
                recNode.AddValue("lastResIdx", rec.LastAppliedResourceIndex);
            }
        }

        /// <summary>
        /// Saves versioned recording metadata and ghost-geometry metadata.
        /// Extracted for testability.
        /// </summary>
        internal static void SaveRecordingMetadata(ConfigNode recNode, RecordingStore.Recording rec)
        {
            recNode.AddValue("recordingId", rec.RecordingId ?? "");
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            recNode.AddValue("loopPlayback", rec.LoopPlayback);
            recNode.AddValue("loopIntervalSeconds", rec.LoopIntervalSeconds.ToString("R", CultureInfo.InvariantCulture));
            if (rec.LoopTimeUnit != RecordingStore.LoopTimeUnit.Sec)
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

            ParsekLog.Verbose("Scenario", $"Saved metadata: id={rec.RecordingId}, " +
                $"phase={rec.SegmentPhase ?? "(none)"}, body={rec.SegmentBodyName ?? "(none)"}, " +
                $"playback={rec.PlaybackEnabled}, chain={rec.ChainId ?? "(none)"}/{rec.ChainIndex}, " +
                $"preLaunch=[F={rec.PreLaunchFunds:F0}, S={rec.PreLaunchScience:F1}, R={rec.PreLaunchReputation:F1}]");
        }

        /// <summary>
        /// Loads versioned recording metadata and ghost-geometry metadata.
        /// Missing fields are treated as old-format recordings.
        /// Extracted for testability.
        /// </summary>
        internal static void LoadRecordingMetadata(ConfigNode recNode, RecordingStore.Recording rec)
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
                RecordingStore.LoopTimeUnit loopTimeUnit;
                if (System.Enum.TryParse(loopTimeUnitStr, out loopTimeUnit))
                    rec.LoopTimeUnit = loopTimeUnit;
            }

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
                rec.RecordingGroups = new List<string>(groups);

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

            ParsekLog.Verbose("Scenario", $"Loaded metadata: id={rec.RecordingId}, " +
                $"phase={rec.SegmentPhase ?? "(none)"}, body={rec.SegmentBodyName ?? "(none)"}, " +
                $"playback={rec.PlaybackEnabled}, chain={rec.ChainId ?? "(none)"}/{rec.ChainIndex}, " +
                $"preLaunch=[F={rec.PreLaunchFunds:F0}, S={rec.PreLaunchScience:F1}, R={rec.PreLaunchReputation:F1}]");
        }

        /// <summary>
        /// Mark crew from all unspawned vessel snapshots as Assigned so they
        /// can't be placed on new craft in the VAB/SPH.
        /// </summary>
        public static void ReserveSnapshotCrew()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                foreach (var rec in RecordingStore.CommittedRecordings)
                {
                    if (rec.LoopPlayback) continue;
                    if (RecordingStore.IsChainFullyDisabled(rec.ChainId)) continue;
                    ReserveCrewIn(rec.VesselSnapshot, rec.VesselSpawned, roster);
                }

                if (RecordingStore.HasPending && RecordingStore.Pending.VesselSnapshot != null)
                    ReserveCrewIn(RecordingStore.Pending.VesselSnapshot, false, roster);
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = false;
            }
        }

        /// <summary>
        /// Set crew in a specific snapshot back to Available.
        /// Call when discarding, recovering, or wiping recordings.
        /// </summary>
        public static void UnreserveCrewInSnapshot(ConfigNode snapshot)
        {
            if (snapshot == null) return;
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return;

            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
                {
                    foreach (string name in partNode.GetValues("crew"))
                    {
                        foreach (ProtoCrewMember pcm in roster.Crew)
                        {
                            if (pcm.name == name && pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned)
                            {
                                pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                                ScenarioLog($"[Parsek Scenario] Unreserved crew '{name}'");

                                // Clean up the replacement kerbal
                                CleanUpReplacement(name, roster);

                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = false;
            }
        }

        internal static void ReserveCrewIn(ConfigNode snapshot, bool alreadySpawned, KerbalRoster roster)
        {
            if (snapshot == null || alreadySpawned) return;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    bool found = false;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name != name) continue;
                        found = true;

                        // Skip dead crew — they're truly gone
                        if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Dead)
                            break;

                        // Rescue Missing crew — they're alive but orphaned from a
                        // removed vessel (e.g. --clean-start or manual save edits).
                        // The recording will respawn them, so restore them first.
                        if (pcm.rosterStatus == ProtoCrewMember.RosterStatus.Missing)
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Available;
                            ScenarioLog($"[Parsek Scenario] Rescued Missing crew '{name}' → Available for reservation");
                        }

                        // Hire a replacement kerbal so the available pool stays constant.
                        // This also handles crew who are already Assigned (e.g. on the pad
                        // vessel after a revert) — they still need a replacement so the
                        // swap can move them off the active vessel.
                        if (!crewReplacements.ContainsKey(name))
                        {
                            try
                            {
                                ProtoCrewMember replacement = roster.GetNewKerbal(ProtoCrewMember.KerbalType.Crew);
                                if (replacement != null)
                                {
                                    KerbalRoster.SetExperienceTrait(replacement, pcm.experienceTrait.TypeName);
                                    crewReplacements[name] = replacement.name;
                                    ScenarioLog($"[Parsek Scenario] Hired replacement '{replacement.name}' " +
                                        $"(trait: {pcm.experienceTrait.TypeName}) for reserved '{name}'");
                                }
                            }
                            catch (System.Exception ex)
                            {
                                ScenarioLog($"[Parsek Scenario] Failed to hire replacement for '{name}': {ex.Message}");
                            }
                        }

                        break;
                    }
                    if (!found)
                        ScenarioLog($"[Parsek Scenario] WARNING: Crew '{name}' not found in roster during reservation");
                }
            }
        }

        /// <summary>
        /// Remove a replacement kerbal from the roster if they're still Available.
        /// If the replacement is Assigned (on a mission), leave them as a "real" kerbal.
        /// </summary>
        private static void CleanUpReplacement(string originalName, KerbalRoster roster)
        {
            if (!crewReplacements.TryGetValue(originalName, out string replacementName))
                return;

            // Always remove the mapping
            crewReplacements.Remove(originalName);

            // Find the replacement in the roster
            ProtoCrewMember replacement = null;
            foreach (ProtoCrewMember pcm in roster.Crew)
            {
                if (pcm.name == replacementName)
                {
                    replacement = pcm;
                    break;
                }
            }

            if (replacement == null)
            {
                ScenarioLog($"[Parsek Scenario] Replacement '{replacementName}' not found in roster (already removed?)");
                return;
            }

            if (replacement.rosterStatus == ProtoCrewMember.RosterStatus.Available)
            {
                roster.Remove(replacement);
                ScenarioLog($"[Parsek Scenario] Removed replacement '{replacementName}' (was unused)");
            }
            else
            {
                ScenarioLog($"[Parsek Scenario] Kept replacement '{replacementName}' " +
                    $"(status: {replacement.rosterStatus} — now a real kerbal)");
            }
        }

        /// <summary>
        /// Remove all Available replacement kerbals and clear the mapping.
        /// Called when wiping all recordings.
        /// </summary>
        public static void ClearReplacements()
        {
            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null)
            {
                crewReplacements.Clear();
                return;
            }

            GameStateRecorder.SuppressCrewEvents = true;
            try
            {
                foreach (var kvp in new Dictionary<string, string>(crewReplacements))
                {
                    CleanUpReplacement(kvp.Key, roster);
                }

                crewReplacements.Clear();
                ScenarioLog("[Parsek Scenario] Cleared all crew replacements");
            }
            finally
            {
                GameStateRecorder.SuppressCrewEvents = false;
            }
        }

        /// <summary>
        /// Load crew replacement mappings from a ConfigNode.
        /// </summary>
        private static void LoadCrewReplacements(ConfigNode node)
        {
            crewReplacements.Clear();

            ConfigNode replacementsNode = node.GetNode("CREW_REPLACEMENTS");
            if (replacementsNode == null)
            {
                ScenarioLog("[Parsek Scenario] Loaded 0 crew replacements (no CREW_REPLACEMENTS node)");
                return;
            }

            ConfigNode[] entries = replacementsNode.GetNodes("ENTRY");
            for (int i = 0; i < entries.Length; i++)
            {
                string original = entries[i].GetValue("original");
                string replacement = entries[i].GetValue("replacement");
                if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(replacement))
                {
                    crewReplacements[original] = replacement;
                }
            }

            ScenarioLog($"[Parsek Scenario] Loaded {crewReplacements.Count} crew replacement(s)");
        }

        /// <summary>
        /// Load group hierarchy from a ConfigNode.
        /// </summary>
        private static void LoadGroupHierarchy(ConfigNode node)
        {
            groupParents.Clear();

            ConfigNode hierarchyNode = node.GetNode("GROUP_HIERARCHY");
            if (hierarchyNode == null)
            {
                ScenarioLog("[Parsek Scenario] Loaded 0 group hierarchy entries (no GROUP_HIERARCHY node)");
                return;
            }

            ConfigNode[] entries = hierarchyNode.GetNodes("ENTRY");
            for (int i = 0; i < entries.Length; i++)
            {
                string child = entries[i].GetValue("child");
                string parent = entries[i].GetValue("parent");
                if (!string.IsNullOrEmpty(child) && !string.IsNullOrEmpty(parent))
                {
                    groupParents[child] = parent;
                }
            }

            // Post-load validation: detect and break corrupted cycles
            var visited = new HashSet<string>();
            var toRemove = new List<string>();
            foreach (var kvp in groupParents)
            {
                visited.Clear();
                string current = kvp.Key;
                bool hasCycle = false;
                while (groupParents.TryGetValue(current, out string p))
                {
                    if (!visited.Add(current)) { hasCycle = true; break; }
                    current = p;
                }
                if (hasCycle && !toRemove.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                groupParents.Remove(toRemove[i]);
                ParsekLog.Warn("Scenario", $"LoadGroupHierarchy: broke cycle involving group '{toRemove[i]}'");
            }

            ScenarioLog($"[Parsek Scenario] Loaded {groupParents.Count} group hierarchy entries");
        }

        /// <summary>
        /// Swap reserved crew out of the active flight vessel, replacing them
        /// with their hired replacements. Prevents the player from recording
        /// with a reserved kerbal again after revert.
        /// </summary>
        public static int SwapReservedCrewInFlight()
        {
            if (FlightGlobals.ActiveVessel == null) return 0;
            if (crewReplacements.Count == 0) return 0;

            var roster = HighLogic.CurrentGame?.CrewRoster;
            if (roster == null) return 0;

            int swapCount = 0;
            int failCount = 0;

            foreach (Part part in FlightGlobals.ActiveVessel.parts)
            {
                // Iterate a copy because RemoveCrewmember modifies the list
                var crewList = new List<ProtoCrewMember>(part.protoModuleCrew);
                for (int i = 0; i < crewList.Count; i++)
                {
                    ProtoCrewMember original = crewList[i];
                    if (!crewReplacements.TryGetValue(original.name, out string replacementName))
                        continue;

                    // Find the replacement in the roster
                    ProtoCrewMember replacement = null;
                    foreach (ProtoCrewMember pcm in roster.Crew)
                    {
                        if (pcm.name == replacementName)
                        {
                            replacement = pcm;
                            break;
                        }
                    }

                    if (replacement == null)
                    {
                        ScenarioLog($"[Parsek Scenario] Cannot swap '{original.name}': replacement '{replacementName}' not in roster");
                        failCount++;
                        continue;
                    }

                    int seatIndex = part.protoModuleCrew.IndexOf(original);
                    if (seatIndex < 0)
                    {
                        ScenarioLog($"[Parsek Scenario] Cannot swap '{original.name}': not found in part crew list");
                        failCount++;
                        continue;
                    }
                    part.RemoveCrewmember(original);
                    part.AddCrewmemberAt(replacement, seatIndex);
                    swapCount++;
                    ScenarioLog($"[Parsek Scenario] Swapped '{original.name}' → '{replacement.name}' in part '{part.partInfo.title}'");
                }
            }

            if (swapCount > 0)
            {
                FlightGlobals.ActiveVessel.SpawnCrew();
                GameEvents.onVesselCrewWasModified.Fire(FlightGlobals.ActiveVessel);
                ScenarioLog($"[Parsek Scenario] Crew swap complete: {swapCount} succeeded" +
                    (failCount > 0 ? $", {failCount} failed" : "") +
                    " — refreshed vessel crew display");
            }
            else if (failCount > 0)
            {
                ScenarioLog($"[Parsek Scenario] Crew swap: 0 succeeded, {failCount} failed");
            }

            // --- EVA vessel cleanup pass ---
            // Reserved crew on EVA are separate vessels, not in ActiveVessel.parts.
            // Remove their EVA vessels to prevent duplicates at ghost EndUT spawn.
            int evaRemoved = 0;
            var allVessels = FlightGlobals.Vessels;
            for (int v = allVessels.Count - 1; v >= 0; v--)
            {
                Vessel vessel = allVessels[v];
                if (vessel == FlightGlobals.ActiveVessel) continue;
                if (!vessel.isEVA) continue;

                string evaCrewName = GetEvaCrewName(vessel);
                if (!ShouldRemoveEvaVessel(true, evaCrewName, crewReplacements)) continue;

                ScenarioLog($"[Parsek Scenario] Removing reserved EVA vessel '{evaCrewName}' (pid={vessel.persistentId})");

                // 1. Remove ProtoVessel to prevent re-spawn on save/load
                var flightState = HighLogic.CurrentGame?.flightState;
                if (flightState != null && vessel.protoVessel != null)
                    flightState.protoVessels.Remove(vessel.protoVessel);

                // 2. Remove from active vessel list
                allVessels.RemoveAt(v);

                // 3. Unload parts/modules/physics, then destroy GameObject
                vessel.Unload();
                if (vessel.gameObject != null)
                {
                    vessel.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(vessel.gameObject);
                }
                evaRemoved++;
            }

            if (evaRemoved > 0)
                ScenarioLog($"[Parsek Scenario] Removed {evaRemoved} reserved EVA vessel(s)");

            return swapCount;
        }

        /// <summary>
        /// Returns the single crew member's name from an EVA vessel, or null.
        /// Uses GetVesselCrew() for robustness with both packed and unpacked vessels.
        /// </summary>
        private static string GetEvaCrewName(Vessel evaVessel)
        {
            var crew = evaVessel.GetVesselCrew();
            return crew.Count > 0 ? crew[0].name : null;
        }

        /// <summary>
        /// Pure decision: should this EVA vessel be removed during crew swap?
        /// An EVA vessel is removed if its crew member is reserved (in the replacements dict).
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldRemoveEvaVessel(
            bool isEva, string crewName, IReadOnlyDictionary<string, string> replacements)
        {
            return isEva && !string.IsNullOrEmpty(crewName) && replacements.ContainsKey(crewName);
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

        /// <summary>
        /// Returns true if a crew member with the given roster status should be
        /// processed for reservation (i.e. not dead). Missing crew are processed
        /// because they may be alive but orphaned from a removed vessel.
        /// Extracted for testability.
        /// </summary>
        internal static bool ShouldProcessCrewForReservation(ProtoCrewMember.RosterStatus status)
        {
            return status != ProtoCrewMember.RosterStatus.Dead;
        }

        /// <summary>
        /// Extracts crew names from a vessel snapshot ConfigNode.
        /// </summary>
        internal static List<string> ExtractCrewFromSnapshot(ConfigNode snapshot)
        {
            var crew = new List<string>();
            if (snapshot == null) return crew;

            foreach (ConfigNode partNode in snapshot.GetNodes("PART"))
            {
                foreach (string name in partNode.GetValues("crew"))
                {
                    if (!string.IsNullOrEmpty(name))
                        crew.Add(name);
                }
            }
            return crew;
        }

        /// <summary>
        /// Clears replacement dictionary without roster access. For unit tests only.
        /// </summary>
        internal static void ResetReplacementsForTesting()
        {
            crewReplacements.Clear();
        }

        // ─── Group hierarchy management ──────────────────────────────────────

        /// <summary>
        /// Walks the parent chain from 'startGroup' upward, returns true if 'targetAncestor'
        /// is found (or equals 'startGroup' itself). Used for cycle detection.
        /// Max depth guard protects against corrupted cycles.
        /// </summary>
        internal static bool IsInAncestorChain(string startGroup, string targetAncestor)
        {
            if (string.IsNullOrEmpty(startGroup) || string.IsNullOrEmpty(targetAncestor))
                return false;
            string current = startGroup;
            for (int depth = 0; depth < 100; depth++)
            {
                if (current == targetAncestor) return true;
                if (!groupParents.TryGetValue(current, out string parent))
                    return false;
                current = parent;
            }
            ParsekLog.Warn("Scenario", $"IsInAncestorChain: max depth reached for group '{startGroup}' — possible cycle");
            return true; // Assume cycle, block the assignment
        }

        /// <summary>
        /// Sets the parent of a child group. Pass null parentGroup to make root-level.
        /// Returns false if assignment would create a cycle.
        /// </summary>
        public static bool SetGroupParent(string childGroup, string parentGroup)
        {
            if (string.IsNullOrEmpty(childGroup)) return false;

            if (parentGroup == null)
            {
                if (groupParents.Remove(childGroup))
                    ParsekLog.Info("Scenario", $"Group '{childGroup}' moved to root level");
                return true;
            }

            if (IsInAncestorChain(parentGroup, childGroup))
            {
                ParsekLog.Warn("Scenario", $"SetGroupParent: cannot assign '{childGroup}' to parent '{parentGroup}' — would create cycle");
                return false;
            }

            groupParents[childGroup] = parentGroup;
            ParsekLog.Info("Scenario", $"Group '{childGroup}' assigned to parent group '{parentGroup}'");
            return true;
        }

        /// <summary>
        /// Removes a group from the hierarchy. Promotes its children to root-level.
        /// </summary>
        public static void RemoveGroupFromHierarchy(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) return;

            // Find grandparent (null if this group was root-level)
            string grandparent;
            groupParents.TryGetValue(groupName, out grandparent);

            // Remove as child
            groupParents.Remove(groupName);

            // Reparent children to grandparent (or root if no grandparent)
            var toReparent = new List<string>();
            foreach (var kvp in groupParents)
            {
                if (kvp.Value == groupName)
                    toReparent.Add(kvp.Key);
            }
            for (int i = 0; i < toReparent.Count; i++)
            {
                if (grandparent != null)
                    groupParents[toReparent[i]] = grandparent;
                else
                    groupParents.Remove(toReparent[i]);
            }

            string destLabel = grandparent != null ? $"reparented under '{grandparent}'" : "promoted to root";
            ParsekLog.Info("Scenario", $"Group '{groupName}' removed from hierarchy ({toReparent.Count} sub-groups {destLabel})");
        }

        /// <summary>
        /// Returns all descendant group names (recursive).
        /// </summary>
        public static List<string> GetDescendantGroups(string groupName)
        {
            var descendants = new List<string>();
            if (string.IsNullOrEmpty(groupName)) return descendants;
            CollectDescendants(groupName, descendants, 0);
            return descendants;
        }

        private static void CollectDescendants(string groupName, List<string> result, int depth)
        {
            if (depth > 100)
            {
                ParsekLog.Warn("Scenario", $"CollectDescendants: max depth reached for group '{groupName}' — possible cycle, result truncated");
                return;
            }
            foreach (var kvp in groupParents)
            {
                if (kvp.Value == groupName)
                {
                    result.Add(kvp.Key);
                    CollectDescendants(kvp.Key, result, depth + 1);
                }
            }
        }

        /// <summary>
        /// Renames a group in the hierarchy (updates both keys and values).
        /// </summary>
        public static void RenameGroupInHierarchy(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName)
                return;

            int updated = 0;

            // Update as child (key)
            if (groupParents.TryGetValue(oldName, out string parent))
            {
                groupParents.Remove(oldName);
                groupParents[newName] = parent;
                updated++;
            }

            // Update as parent (values)
            var toUpdate = new List<string>();
            foreach (var kvp in groupParents)
            {
                if (kvp.Value == oldName)
                    toUpdate.Add(kvp.Key);
            }
            for (int i = 0; i < toUpdate.Count; i++)
            {
                groupParents[toUpdate[i]] = newName;
                updated++;
            }

            if (updated > 0)
                ParsekLog.Info("Scenario", $"RenameGroupInHierarchy: '{oldName}' → '{newName}' ({updated} hierarchy entries updated)");
        }

        /// <summary>
        /// Resets group hierarchy for testing.
        /// </summary>
        public static void ResetGroupsForTesting()
        {
            groupParents.Clear();
        }

        #region Vessel Lifecycle Events

        /// <summary>
        /// Prepares a standalone pending recording for ghost-only commit (no vessel spawn).
        /// Nulls vessel snapshot and unreserves crew. Call RecordingStore.CommitPending() after this.
        /// </summary>
        private static void AutoCommitGhostOnly(RecordingStore.Recording pending)
        {
            UnreserveCrewInSnapshot(pending.VesselSnapshot);
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
                UnreserveCrewInSnapshot(rec.VesselSnapshot);
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
                    UnreserveCrewInSnapshot(pending.VesselSnapshot);
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
                        UnreserveCrewInSnapshot(rec.VesselSnapshot);
                        rec.VesselSnapshot = null;
                        anyUpdated = true;
                        ParsekLog.Verbose("Scenario", $"Updated pending tree recording '{rec.VesselName}' with {state}");
                    }
                }
            }

            // Check committed recordings (most recent first — only update the first match to
            // avoid updating unrelated recordings that happen to share the same vessel name)
            var committed = RecordingStore.CommittedRecordings;
            for (int i = committed.Count - 1; i >= 0; i--)
            {
                var rec = committed[i];
                if (MatchesVessel(rec, vesselName) && CanOverwriteTerminalState(rec.TerminalStateValue, state))
                {
                    rec.TerminalStateValue = state;
                    rec.ExplicitEndUT = ut;
                    UnreserveCrewInSnapshot(rec.VesselSnapshot);
                    rec.VesselSnapshot = null;
                    anyUpdated = true;
                    ParsekLog.Verbose("Scenario", $"Updated committed recording '{rec.VesselName}' (#{i}) with {state}");
                    break; // only update the most recent matching recording
                }
            }

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
        private static bool MatchesVessel(RecordingStore.Recording rec, string vesselName)
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

        public void OnDestroy()
        {
            stateRecorder?.Unsubscribe();
            GameEvents.onVesselRecovered.Remove(OnVesselRecovered);
            GameEvents.onVesselTerminated.Remove(OnVesselTerminated);
        }
    }
}
