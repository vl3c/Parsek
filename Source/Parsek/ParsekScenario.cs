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

        #region Crew Replacements

        // Maps reserved kerbal name → replacement kerbal name
        private static Dictionary<string, string> crewReplacements = new Dictionary<string, string>();

        /// <summary>
        /// Read-only access to current replacement mappings. For testing/diagnostics.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> CrewReplacements => crewReplacements;

        #endregion

        public override void OnSave(ConfigNode node)
        {
            // Clear any existing recording nodes
            node.RemoveNodes("RECORDING");

            var recordings = RecordingStore.CommittedRecordings;
            ParsekLog.Info("Scenario", $"OnSave: saving {recordings.Count} committed recordings, epoch={MilestoneStore.CurrentEpoch}");

            for (int r = 0; r < recordings.Count; r++)
            {
                var rec = recordings[r];

                // Skip tree recordings — they are saved under RECORDING_TREE nodes below
                if (rec.TreeId != null)
                    continue;

                ConfigNode recNode = node.AddNode("RECORDING");

                // Write bulk data to external files
                rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
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

                if (rec.TakenControl)
                    recNode.AddValue("takenControl", rec.TakenControl.ToString());

                // Persist resource index so quickload doesn't re-apply deltas
                recNode.AddValue("lastResIdx", rec.LastAppliedResourceIndex);
            }

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
                    rec.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
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

        public override void OnLoad(ConfigNode node)
        {
            var recordings = RecordingStore.CommittedRecordings;

            // Detect loading a different save game (not a revert)
            string currentSave = HighLogic.SaveFolder;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                lastSaveFolder = currentSave;
                ScenarioLog($"[Parsek Scenario] Save folder changed to '{currentSave}' — resetting session state");
            }

            // Load crew replacement mappings from the node (both initial and revert paths need this)
            LoadCrewReplacements(node);

            // Game state recorder lifecycle — re-subscribe on every OnLoad (handles reverts)
            stateRecorder?.Unsubscribe();
            if (!initialLoadDone)
            {
                ParsekLog.Verbose("Scenario", "OnLoad: initial load — loading external files");
                GameStateStore.LoadEventFile();
                GameStateStore.LoadBaselines();
                MilestoneStore.LoadMilestoneFile();

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
                // no spawnedPid / takenControl / lastResIdx, so they naturally reset.
                // On non-revert scene changes (e.g. tracking station → flight) the
                // save preserves these, preventing duplicate spawns and ghost replays.
                // NOTE: This loop only covers standalone recordings (not tree recordings).
                // Tree recordings get their mutable state from RECORDING_TREE nodes below.
                // Use a separate counter (standaloneIdx) for savedRecNodes because tree
                // recordings are interspersed in the in-memory list but absent from the
                // saved RECORDING nodes.
                int standaloneIdx = 0;
                for (int i = 0; i < recordings.Count; i++)
                {
                    // Skip tree recordings — their mutable state is restored from tree nodes
                    if (recordings[i].TreeId != null) continue;

                    recordings[i].VesselSpawned = false;
                    recordings[i].SpawnAttempts = 0;

                    uint savedPid = 0;
                    bool savedTaken = false;
                    int resIdx = -1;
                    if (standaloneIdx < savedRecNodes.Length)
                    {
                        string pidStr = savedRecNodes[standaloneIdx].GetValue("spawnedPid");
                        if (pidStr != null && !uint.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out savedPid))
                            ParsekLog.Warn("Scenario", $"Failed to parse spawnedPid '{pidStr}' for recording #{i}");

                        string takenStr = savedRecNodes[standaloneIdx].GetValue("takenControl");
                        if (takenStr != null && !bool.TryParse(takenStr, out savedTaken))
                            ParsekLog.Warn("Scenario", $"Failed to parse takenControl '{takenStr}' for recording #{i}");

                        string resIdxStr = savedRecNodes[standaloneIdx].GetValue("lastResIdx");
                        if (resIdxStr != null && !int.TryParse(resIdxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out resIdx))
                            ParsekLog.Warn("Scenario", $"Failed to parse lastResIdx '{resIdxStr}' for recording #{i}");

                        string playbackEnabledStr = savedRecNodes[standaloneIdx].GetValue("playbackEnabled");
                        if (playbackEnabledStr != null)
                        {
                            bool savedPlaybackEnabled;
                            if (bool.TryParse(playbackEnabledStr, out savedPlaybackEnabled))
                                recordings[i].PlaybackEnabled = savedPlaybackEnabled;
                            else
                                ParsekLog.Warn("Scenario", $"Failed to parse playbackEnabled '{playbackEnabledStr}' for recording #{i}");
                        }
                    }
                    recordings[i].SpawnedVesselPersistentId = savedPid;
                    recordings[i].TakenControl = savedTaken;
                    recordings[i].LastAppliedResourceIndex = resIdx;
                    standaloneIdx++;
                }

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
                    recordings[i].TakenControl = false;
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

                                    string takenStr = savedTreeRecNode.GetValue("takenControl");
                                    bool savedTaken = false;
                                    if (takenStr != null)
                                        bool.TryParse(takenStr, out savedTaken);
                                    recordings[i].TakenControl = savedTaken;

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
                    StartCoroutine(ApplyBudgetDeductionWhenReady());
                }
                else
                {
                    // Scene change — restore milestone state without resetting unmatched.
                    MilestoneStore.RestoreMutableState(node);
                    ParsekLog.Verbose("Scenario", "Scene change — milestone state restored (resetUnmatched=false)");
                }

                ReserveSnapshotCrew();
                ParsekLog.Info("Scenario", $"{(isRevert ? "Revert" : "Scene change")} — preserving {recordings.Count} session recordings");
                return;
            }

            initialLoadDone = true;
            recordings.Clear();

            ConfigNode[] recNodes = node.GetNodes("RECORDING");
            ScenarioLog($"[Parsek Scenario] Loading {recNodes.Length} committed recordings");

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

                // Restore taken control flag
                string takenStr = recNode.GetValue("takenControl");
                if (takenStr != null)
                {
                    bool taken;
                    if (bool.TryParse(takenStr, out taken))
                        rec.TakenControl = taken;
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
                    (!string.IsNullOrEmpty(rec.GhostGeometryRelativePath)
                        ? $" (ghost geometry: {(rec.GhostGeometryAvailable ? "ready" : "fallback")})"
                        : "") +
                    phaseInfo + chainInfo + enabledInfo);
            }

            // Validate chain integrity before any playback
            RecordingStore.ValidateChains();

            // Load committed recording trees
            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            if (treeNodes.Length > 0)
            {
                ScenarioLog($"[Parsek Scenario] Loading {treeNodes.Length} committed tree(s)");
                var committedTrees = RecordingStore.CommittedTrees;
                committedTrees.Clear();

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

            // If pending recording exists but we're not in Flight, auto-commit it.
            // Merge dialog can only show in Flight (ParsekFlight is Flight-only).
            // This handles Esc > Abort Mission → Space Center path.
            if (RecordingStore.HasPending && HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                var pending = RecordingStore.Pending;
                if (pending.VesselSnapshot != null)
                    UnreserveCrewInSnapshot(pending.VesselSnapshot);
                pending.VesselSnapshot = null;
                RecordingStore.CommitPending();
                ScenarioLog($"[Parsek Scenario] Auto-committed pending recording outside Flight " +
                    $"(scene: {HighLogic.LoadedScene})");
            }

            // If pending tree exists but we're not in Flight, auto-commit ghost-only.
            if (RecordingStore.HasPendingTree && HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                var pt = RecordingStore.PendingTree;
                // Null all vessel snapshots (ghost-only commit — no spawning outside Flight)
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

            // Mark all milestones as fully replayed (deduction already covers their costs)
            var milestones = MilestoneStore.Milestones;
            int mileMarked = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Events.Count > 0 && milestones[i].LastReplayedEventIndex < milestones[i].Events.Count - 1)
                {
                    int oldIdx = milestones[i].LastReplayedEventIndex;
                    milestones[i].LastReplayedEventIndex = milestones[i].Events.Count - 1;
                    mileMarked++;
                    ParsekLog.Verbose("Scenario",
                        $"  milestone {(milestones[i].MilestoneId != null && milestones[i].MilestoneId.Length >= 8 ? milestones[i].MilestoneId.Substring(0, 8) : milestones[i].MilestoneId ?? "?")}: lastReplayedEventIndex {oldIdx} → {milestones[i].LastReplayedEventIndex}");
                }
            }

            ParsekLog.Info("Scenario",
                $"Budget deduction applied for epoch {MilestoneStore.CurrentEpoch} — " +
                $"{recMarked} recording(s), {treeMarked} tree(s), and {mileMarked} milestone(s) marked as fully applied");
        }

        #endregion

        #region Crew Reservation

        /// <summary>
        /// Saves versioned recording metadata and ghost-geometry metadata.
        /// Extracted for testability.
        /// </summary>
        internal static void SaveRecordingMetadata(ConfigNode recNode, RecordingStore.Recording rec)
        {
            recNode.AddValue("recordingId", rec.RecordingId ?? "");
            recNode.AddValue("recordingFormatVersion", rec.RecordingFormatVersion);
            recNode.AddValue("ghostGeometryVersion", rec.GhostGeometryVersion);
            recNode.AddValue("loopPlayback", rec.LoopPlayback);
            recNode.AddValue("loopPauseSeconds", rec.LoopPauseSeconds.ToString("R", CultureInfo.InvariantCulture));
            recNode.AddValue("ghostGeometryStrategy", rec.GhostGeometryCaptureStrategy ?? "stub_v1");
            recNode.AddValue("ghostGeometryProbeStatus", rec.GhostGeometryProbeStatus ?? "unknown");
            if (!string.IsNullOrEmpty(rec.GhostGeometryRelativePath))
                recNode.AddValue("ghostGeometryPath", rec.GhostGeometryRelativePath);
            recNode.AddValue("ghostGeometryAvailable", rec.GhostGeometryAvailable);
            if (!string.IsNullOrEmpty(rec.GhostGeometryCaptureError))
                recNode.AddValue("ghostGeometryError", rec.GhostGeometryCaptureError);
            if (rec.PreLaunchFunds != 0)
                recNode.AddValue("preLaunchFunds", rec.PreLaunchFunds.ToString("R", CultureInfo.InvariantCulture));
            if (rec.PreLaunchScience != 0)
                recNode.AddValue("preLaunchScience", rec.PreLaunchScience.ToString("R", CultureInfo.InvariantCulture));
            if (rec.PreLaunchReputation != 0)
                recNode.AddValue("preLaunchRep", rec.PreLaunchReputation.ToString("R", CultureInfo.InvariantCulture));

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

            string loopPauseStr = recNode.GetValue("loopPauseSeconds");
            if (loopPauseStr != null)
            {
                double loopPauseSeconds;
                if (double.TryParse(loopPauseStr, NumberStyles.Float, CultureInfo.InvariantCulture, out loopPauseSeconds))
                    rec.LoopPauseSeconds = loopPauseSeconds;
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

                        // Mark as Assigned if Available
                        if (NeedsStatusChange(pcm.rosterStatus))
                        {
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Assigned;
                            ScenarioLog($"[Parsek Scenario] Reserved crew '{name}' for deferred vessel spawn");
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

            return swapCount;
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
        /// Returns true if a crew member with the given roster status needs to be
        /// marked as Assigned. Already-Assigned crew (e.g. on the pad vessel after
        /// revert) still need replacements but don't need a status change.
        /// </summary>
        internal static bool NeedsStatusChange(ProtoCrewMember.RosterStatus status)
        {
            return status == ProtoCrewMember.RosterStatus.Available;
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
        }
    }
}
