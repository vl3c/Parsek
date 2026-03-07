using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    internal struct RestorePoint
    {
        public string Id;
        public double UT;
        public string SaveFileName;
        public string Label;
        public int RecordingCount;
        public double Funds;
        public double Science;
        public float Reputation;
        public double ReservedFundsAtSave;
        public double ReservedScienceAtSave;
        public float ReservedRepAtSave;
        public bool SaveFileExists;

        public RestorePoint(bool init)
        {
            Id = null;
            UT = 0;
            SaveFileName = null;
            Label = null;
            RecordingCount = 0;
            Funds = 0;
            Science = 0;
            Reputation = 0;
            ReservedFundsAtSave = 0;
            ReservedScienceAtSave = 0;
            ReservedRepAtSave = 0;
            SaveFileExists = true;
        }

        public void SerializeInto(ConfigNode parent)
        {
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode("RESTORE_POINT");
            node.AddValue("id", Id ?? "");
            node.AddValue("ut", UT.ToString("R", ic));
            node.AddValue("saveFile", SaveFileName ?? "");
            node.AddValue("label", Label ?? "");
            node.AddValue("recCount", RecordingCount.ToString(ic));
            node.AddValue("funds", Funds.ToString("R", ic));
            node.AddValue("science", Science.ToString("R", ic));
            node.AddValue("rep", Reputation.ToString("R", ic));
            node.AddValue("resFunds", ReservedFundsAtSave.ToString("R", ic));
            node.AddValue("resSci", ReservedScienceAtSave.ToString("R", ic));
            node.AddValue("resRep", ReservedRepAtSave.ToString("R", ic));
        }

        public static RestorePoint DeserializeFrom(ConfigNode node)
        {
            var ic = CultureInfo.InvariantCulture;
            var rp = new RestorePoint(true);

            rp.Id = node.GetValue("id") ?? "";
            double ut;
            if (double.TryParse(node.GetValue("ut"), NumberStyles.Float, ic, out ut))
                rp.UT = ut;
            rp.SaveFileName = node.GetValue("saveFile") ?? "";
            rp.Label = node.GetValue("label") ?? "";
            int recCount;
            if (int.TryParse(node.GetValue("recCount"), NumberStyles.Integer, ic, out recCount))
                rp.RecordingCount = recCount;
            double funds;
            if (double.TryParse(node.GetValue("funds"), NumberStyles.Float, ic, out funds))
                rp.Funds = funds;
            double science;
            if (double.TryParse(node.GetValue("science"), NumberStyles.Float, ic, out science))
                rp.Science = science;
            float rep;
            if (float.TryParse(node.GetValue("rep"), NumberStyles.Float, ic, out rep))
                rp.Reputation = rep;
            double resFunds;
            if (double.TryParse(node.GetValue("resFunds"), NumberStyles.Float, ic, out resFunds))
                rp.ReservedFundsAtSave = resFunds;
            double resSci;
            if (double.TryParse(node.GetValue("resSci"), NumberStyles.Float, ic, out resSci))
                rp.ReservedScienceAtSave = resSci;
            float resRep;
            if (float.TryParse(node.GetValue("resRep"), NumberStyles.Float, ic, out resRep))
                rp.ReservedRepAtSave = resRep;

            return rp;
        }
    }

    internal struct PendingLaunchSave
    {
        public string SaveFileName;
        public double UT;
        public double Funds;
        public double Science;
        public float Reputation;
        public double ReservedFundsAtSave;
        public double ReservedScienceAtSave;
        public float ReservedRepAtSave;
    }

    internal static class RestorePointStore
    {
        private static List<RestorePoint> restorePoints = new List<RestorePoint>();

        internal static PendingLaunchSave? pendingLaunchSave;
        internal static bool initialLoadDone;
        private static string lastSaveFolder;

        // Go-back flags (survive scene change via static fields)
        internal static bool IsGoingBack;
        internal static double GoBackUT;
        internal static ResourceBudget.BudgetSummary GoBackReserved;

        internal static bool SuppressLogging;

        internal static bool HasRestorePoints => restorePoints.Count > 0;
        internal static bool HasPendingLaunchSave => pendingLaunchSave.HasValue;
        internal static IReadOnlyList<RestorePoint> RestorePoints => restorePoints;

        internal static void ResetForTesting()
        {
            restorePoints.Clear();
            pendingLaunchSave = null;
            initialLoadDone = false;
            lastSaveFolder = null;
            IsGoingBack = false;
            GoBackUT = 0;
            GoBackReserved = default(ResourceBudget.BudgetSummary);
        }

        internal static string BuildLabel(string vesselName, int recordingCount, bool isTree)
        {
            string recWord = recordingCount == 1 ? "recording" : "recordings";
            string launchType = isTree ? "tree launch" : "launch";
            return $"\"{vesselName}\" {launchType} ({recordingCount} {recWord})";
        }

        internal static string RestorePointSaveName(string shortId)
        {
            return $"parsek_rp_{shortId}";
        }

        internal static void AddForTesting(RestorePoint rp)
        {
            restorePoints.Add(rp);
        }

        /// <summary>
        /// Removes a restore point from the in-memory list by id.
        /// Does NOT perform file I/O — used for unit testing the list removal logic.
        /// Returns true if the restore point was found and removed.
        /// </summary>
        internal static bool RemoveRestorePointFromList(string id)
        {
            for (int i = 0; i < restorePoints.Count; i++)
            {
                if (restorePoints[i].Id == id)
                {
                    restorePoints.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Clears all restore points and pending launch save from memory.
        /// Does NOT perform file I/O — used for unit testing the clear logic.
        /// </summary>
        internal static void ClearAllInMemory()
        {
            restorePoints.Clear();
            pendingLaunchSave = null;
        }

        /// <summary>
        /// Resets all playback state on committed recordings (standalone and tree).
        /// Called during go-back to prepare all recordings for fresh replay.
        /// </summary>
        internal static (int standaloneCount, int treeCount) ResetAllPlaybackState()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int standaloneCount = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].TreeId == null)
                    standaloneCount++;
                ResetRecordingPlaybackFields(recordings[i]);
            }

            var trees = RecordingStore.CommittedTrees;
            for (int i = 0; i < trees.Count; i++)
            {
                trees[i].ResourcesApplied = false;
                foreach (var rec in trees[i].Recordings.Values)
                    ResetRecordingPlaybackFields(rec);
            }

            ResourceBudget.Invalidate();

            if (!SuppressLogging)
                ParsekLog.Info("RestorePoint",
                    $"Playback state reset: {standaloneCount} standalone recording(s), {trees.Count} tree(s)");

            return (standaloneCount, trees.Count);
        }

        private static void ResetRecordingPlaybackFields(RecordingStore.Recording rec)
        {
            rec.VesselSpawned = false;
            rec.SpawnAttempts = 0;
            rec.SpawnedVesselPersistentId = 0;
            rec.LastAppliedResourceIndex = -1;
            rec.TakenControl = false;
            rec.SceneExitSituation = -1;
        }

        /// <summary>
        /// Marks all committed recordings, trees, and milestones as fully applied.
        /// Called after go-back resource adjustment to prevent double-application.
        /// </summary>
        internal static (int recCount, int treeCount) MarkAllFullyApplied()
        {
            var recordings = RecordingStore.CommittedRecordings;
            int recCount = 0;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].Points.Count > 0)
                {
                    recordings[i].LastAppliedResourceIndex = recordings[i].Points.Count - 1;
                    recCount++;
                }
            }

            var trees = RecordingStore.CommittedTrees;
            int treeCount = 0;
            for (int i = 0; i < trees.Count; i++)
            {
                if (!trees[i].ResourcesApplied)
                {
                    trees[i].ResourcesApplied = true;
                    treeCount++;
                }
            }

            var milestones = MilestoneStore.Milestones;
            int mileCount = 0;
            for (int i = 0; i < milestones.Count; i++)
            {
                if (milestones[i].Committed && milestones[i].Events.Count > 0)
                {
                    milestones[i].LastReplayedEventIndex = milestones[i].Events.Count - 1;
                    mileCount++;
                }
            }

            ResourceBudget.Invalidate();

            if (!SuppressLogging)
                ParsekLog.Info("RestorePoint",
                    $"Marked fully applied: {recCount} recording(s), {treeCount} tree(s), {mileCount} milestone(s)");

            return (recCount, treeCount);
        }

        /// <summary>
        /// Checks whether the player can go back to a restore point.
        /// Returns false with a reason string if any precondition fails.
        /// </summary>
        internal static bool CanGoBack(out string reason, bool isRecording = false, bool isInFlight = true)
        {
            if (restorePoints.Count == 0)
            {
                reason = "No restore points available";
                return false;
            }

            if (!isInFlight)
            {
                reason = "Go back is only available in flight";
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before going back";
                return false;
            }

            if (RecordingStore.HasPending)
            {
                reason = "Merge or discard pending recording first";
                return false;
            }

            if (RecordingStore.HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                return false;
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// Returns true if the given vessel situation (as int) represents a stable state
        /// suitable for Commit Flight or launch save capture.
        /// Stable: PRELAUNCH(4), LANDED(1), SPLASHED(2), ORBITING(32).
        /// Unstable: FLYING(8), SUB_ORBITAL(16), ESCAPING(64).
        /// </summary>
        internal static bool IsStableState(int situation)
        {
            // Vessel.Situations enum values:
            // LANDED=1, SPLASHED=2, PRELAUNCH=4, FLYING=8,
            // SUB_ORBITAL=16, ORBITING=32, ESCAPING=64, DOCKED=128
            switch (situation)
            {
                case 1:  // LANDED
                case 2:  // SPLASHED
                case 4:  // PRELAUNCH
                case 32: // ORBITING
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Counts committed recordings whose StartUT is after the given UT.
        /// Used to display how many future recordings will replay as ghosts after go-back.
        /// </summary>
        internal static int CountFutureRecordings(double ut)
        {
            int count = 0;
            var recordings = RecordingStore.CommittedRecordings;
            for (int i = 0; i < recordings.Count; i++)
            {
                if (recordings[i].StartUT > ut)
                    count++;
            }
            return count;
        }

        #region Launch Save Capture

        /// <summary>
        /// Captures a launch save (quicksave) at recording start if the vessel is in a stable state.
        /// Skipped for tree branch/chain promotions (which use the root's launch save).
        /// </summary>
        internal static void TryCaptureLaunchSave(Vessel v, bool isPromotion, double recordingStartUT)
        {
            if (isPromotion)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("RestorePoint", "Launch save skipped: tree branch/chain promotion");
                return;
            }

            if (!IsStableState((int)v.situation))
            {
                if (!SuppressLogging)
                    ParsekLog.Info("RestorePoint",
                        $"Launch save skipped: vessel in {v.situation} (not stable)");
                return;
            }

            // If a previous launch save exists, delete its quicksave file (orphaned)
            if (pendingLaunchSave.HasValue)
            {
                try
                {
                    string savesDir = Path.Combine(
                        KSPUtil.ApplicationRootPath ?? "",
                        "saves",
                        HighLogic.SaveFolder ?? "");
                    string oldSaveFile = pendingLaunchSave.Value.SaveFileName;
                    string oldPath = Path.Combine(savesDir, oldSaveFile + ".sfs");
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);
                    if (!SuppressLogging)
                        ParsekLog.Info("RestorePoint",
                            $"Replacing orphaned launch save: deleting {oldSaveFile}");
                }
                catch (Exception ex)
                {
                    if (!SuppressLogging)
                        ParsekLog.Warn("RestorePoint",
                            $"Failed to delete orphaned launch save: {ex.Message}");
                }
                pendingLaunchSave = null;
            }

            string shortId = Guid.NewGuid().ToString("N").Substring(0, 6);
            string saveFileName = RestorePointSaveName(shortId);

            // Capture quicksave
            try
            {
                string result = GamePersistence.SaveGame(saveFileName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    if (!SuppressLogging)
                        ParsekLog.Error("RestorePoint", "Failed to capture launch save: SaveGame returned null");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (!SuppressLogging)
                    ParsekLog.Error("RestorePoint", $"Failed to capture launch save: {ex.Message}");
                return;
            }

            // Capture resource snapshot
            double funds = 0;
            double science = 0;
            float reputation = 0;
            try
            {
                if (Funding.Instance != null)
                    funds = Funding.Instance.Funds;
                if (ResearchAndDevelopment.Instance != null)
                    science = ResearchAndDevelopment.Instance.Science;
                if (Reputation.Instance != null)
                    reputation = Reputation.Instance.reputation;
            }
            catch { }

            // Capture reserved amounts (full cost, ignoring application state)
            var reserved = ResourceBudget.ComputeTotalFullCost(
                RecordingStore.CommittedRecordings,
                MilestoneStore.Milestones,
                RecordingStore.CommittedTrees);

            pendingLaunchSave = new PendingLaunchSave
            {
                SaveFileName = saveFileName,
                UT = recordingStartUT,
                Funds = funds,
                Science = science,
                Reputation = reputation,
                ReservedFundsAtSave = reserved.reservedFunds,
                ReservedScienceAtSave = reserved.reservedScience,
                ReservedRepAtSave = (float)reserved.reservedReputation
            };

            if (!SuppressLogging)
                ParsekLog.Info("RestorePoint",
                    $"Captured launch save at UT {recordingStartUT}: vessel \"{v.vesselName}\" in {v.situation} (save: {saveFileName})");
        }

        /// <summary>
        /// Discards the pending launch save (deletes the quicksave file and clears the pending state).
        /// Called when a recording is discarded without committing.
        /// </summary>
        internal static void DiscardLaunchSave()
        {
            if (!pendingLaunchSave.HasValue)
                return;

            string saveFileName = pendingLaunchSave.Value.SaveFileName;

            try
            {
                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");
                string saveFilePath = Path.Combine(savesDir, saveFileName + ".sfs");
                if (File.Exists(saveFilePath))
                    File.Delete(saveFilePath);
            }
            catch (Exception ex)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("RestorePoint",
                        $"Failed to delete launch save file {saveFileName}: {ex.Message}");
            }

            pendingLaunchSave = null;

            if (!SuppressLogging)
                ParsekLog.Info("RestorePoint",
                    $"Launch save discarded: {saveFileName}");
        }

        #endregion

        #region File I/O

        internal static void SaveRestorePointFile()
        {
            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRestorePointsRelativePath());
            if (path == null)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("RestorePoint", "Cannot resolve restore points path — save skipped");
                return;
            }

            try
            {
                RecordingPaths.EnsureGameStateDirectory();

                var rootNode = SerializeAllToNode();

                SafeWriteConfigNode(rootNode, path);

                if (!SuppressLogging)
                    ParsekLog.Info("RestorePoint",
                        $"Metadata saved: {restorePoints.Count} restore points to {path}");
            }
            catch (Exception ex)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("RestorePoint", $"Failed to save restore point metadata: {ex.Message}");
            }
        }

        internal static bool LoadRestorePointFile()
        {
            string currentSave = HighLogic.SaveFolder;
            if (currentSave != lastSaveFolder)
            {
                initialLoadDone = false;
                lastSaveFolder = currentSave;
                if (!SuppressLogging)
                    ParsekLog.Verbose("RestorePoint",
                        $"Save folder changed to '{currentSave}' — resetting restore point load state");
            }

            if (initialLoadDone)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("RestorePoint", "LoadRestorePointFile: already loaded, skipping");
                return true;
            }

            initialLoadDone = true;
            restorePoints.Clear();

            string path = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRestorePointsRelativePath());
            if (path == null || !File.Exists(path))
            {
                if (!SuppressLogging)
                    ParsekLog.Info("RestorePoint",
                        $"No restore point file found at {path ?? "(null)"} (first run)");
                return true;
            }

            try
            {
                ConfigNode rootNode = ConfigNode.Load(path);
                if (rootNode == null)
                {
                    if (!SuppressLogging)
                        ParsekLog.Warn("RestorePoint", "Failed to parse restore points file");
                    return false;
                }

                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");

                DeserializeAllFromNode(rootNode, savesDir);

                if (!SuppressLogging)
                {
                    ParsekLog.Info("RestorePoint",
                        $"Loaded {restorePoints.Count} restore points from {path}");
                    if (restorePoints.Count > 20)
                        ParsekLog.Warn("RestorePoint",
                            $"Warning: {restorePoints.Count} restore points exceed recommended limit of 20");
                }

                return true;
            }
            catch (Exception ex)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("RestorePoint", $"Failed to load restore points: {ex.Message}");
                return false;
            }
        }

        internal static void DeleteRestorePoint(string id)
        {
            int idx = -1;
            for (int i = 0; i < restorePoints.Count; i++)
            {
                if (restorePoints[i].Id == id)
                {
                    idx = i;
                    break;
                }
            }

            if (idx < 0) return;

            string saveFileName = restorePoints[idx].SaveFileName;
            restorePoints.RemoveAt(idx);

            // Delete the .sfs file
            try
            {
                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "",
                    "saves",
                    HighLogic.SaveFolder ?? "");
                string saveFilePath = Path.Combine(savesDir, saveFileName + ".sfs");

                if (File.Exists(saveFilePath))
                {
                    File.Delete(saveFilePath);
                    if (!SuppressLogging)
                        ParsekLog.Info("RestorePoint",
                            $"Deleted restore point {id}: save file {saveFileName} removed");
                }
                else
                {
                    if (!SuppressLogging)
                        ParsekLog.Info("RestorePoint",
                            $"Deleted restore point {id}: save file already missing");
                }
            }
            catch (Exception ex)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("RestorePoint",
                        $"Failed to delete save file for restore point {id}: {ex.Message}");
            }

            SaveRestorePointFile();
        }

        internal static void ClearAll()
        {
            // Delete .sfs files for each restore point
            for (int i = 0; i < restorePoints.Count; i++)
            {
                try
                {
                    string savesDir = Path.Combine(
                        KSPUtil.ApplicationRootPath ?? "",
                        "saves",
                        HighLogic.SaveFolder ?? "");
                    string saveFilePath = Path.Combine(savesDir, restorePoints[i].SaveFileName + ".sfs");
                    if (File.Exists(saveFilePath))
                        File.Delete(saveFilePath);
                }
                catch (Exception ex)
                {
                    if (!SuppressLogging)
                        ParsekLog.Warn("RestorePoint", $"Failed to delete save file {restorePoints[i].SaveFileName}: {ex.Message}");
                }
            }

            // Delete pending launch save file if exists
            if (pendingLaunchSave.HasValue)
            {
                try
                {
                    string savesDir = Path.Combine(
                        KSPUtil.ApplicationRootPath ?? "",
                        "saves",
                        HighLogic.SaveFolder ?? "");
                    string pendingPath = Path.Combine(savesDir, pendingLaunchSave.Value.SaveFileName + ".sfs");
                    if (File.Exists(pendingPath))
                        File.Delete(pendingPath);
                }
                catch (Exception ex)
                {
                    if (!SuppressLogging)
                        ParsekLog.Warn("RestorePoint", $"Failed to delete save file {pendingLaunchSave.Value.SaveFileName}: {ex.Message}");
                }
                pendingLaunchSave = null;
            }

            int count = restorePoints.Count;
            restorePoints.Clear();

            // Delete metadata file
            try
            {
                string metadataPath = RecordingPaths.ResolveSaveScopedPath(
                    RecordingPaths.BuildRestorePointsRelativePath());
                if (metadataPath != null && File.Exists(metadataPath))
                    File.Delete(metadataPath);
            }
            catch (Exception ex)
            {
                if (!SuppressLogging)
                    ParsekLog.Warn("RestorePoint", $"Failed to delete restore point metadata file: {ex.Message}");
            }

            if (!SuppressLogging)
                ParsekLog.Info("RestorePoint", $"All restore points cleared (was {count})");
        }

        private static void SafeWriteConfigNode(ConfigNode node, string path)
        {
            string tmpPath = path + ".tmp";
            node.Save(tmpPath);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tmpPath, path);
        }

        #endregion

        #region Testable Serialization

        /// <summary>
        /// Builds a ConfigNode containing all restore points. Testable without file I/O.
        /// </summary>
        internal static ConfigNode SerializeAllToNode()
        {
            var rootNode = new ConfigNode("RESTORE_POINTS");
            for (int i = 0; i < restorePoints.Count; i++)
            {
                restorePoints[i].SerializeInto(rootNode);
            }
            return rootNode;
        }

        /// <summary>
        /// Loads restore points from a ConfigNode. Replaces current list.
        /// savesDir is optional — if provided, validates that .sfs files exist.
        /// </summary>
        internal static void DeserializeAllFromNode(ConfigNode rootNode, string savesDir = null)
        {
            restorePoints.Clear();

            ConfigNode[] rpNodes = rootNode.GetNodes("RESTORE_POINT");
            if (rpNodes == null) return;

            for (int i = 0; i < rpNodes.Length; i++)
            {
                RestorePoint rp = RestorePoint.DeserializeFrom(rpNodes[i]);

                // Validate save file name for invalid chars
                if (!string.IsNullOrEmpty(rp.SaveFileName) && HasInvalidFileNameChars(rp.SaveFileName))
                {
                    if (!SuppressLogging)
                        ParsekLog.Warn("RestorePoint",
                            $"Restore point {rp.Id} has invalid save file name: {rp.SaveFileName}");
                    continue;
                }

                restorePoints.Add(rp);
            }

            // Validate save file existence if savesDir provided
            if (!string.IsNullOrEmpty(savesDir))
                ValidateSaveFiles(savesDir);
        }

        /// <summary>
        /// Checks each restore point's .sfs file exists in the given saves directory.
        /// Sets SaveFileExists=false for missing files.
        /// </summary>
        internal static void ValidateSaveFiles(string savesDir)
        {
            if (string.IsNullOrEmpty(savesDir)) return;

            for (int i = 0; i < restorePoints.Count; i++)
            {
                var rp = restorePoints[i];
                if (string.IsNullOrEmpty(rp.SaveFileName))
                {
                    rp.SaveFileExists = false;
                    restorePoints[i] = rp;
                    continue;
                }

                string saveFilePath = Path.Combine(savesDir, rp.SaveFileName + ".sfs");
                if (!File.Exists(saveFilePath))
                {
                    rp.SaveFileExists = false;
                    restorePoints[i] = rp;
                    if (!SuppressLogging)
                        ParsekLog.Warn("RestorePoint",
                            $"Save file missing for restore point {rp.Id}: {saveFilePath}");
                }
            }
        }

        private static bool HasInvalidFileNameChars(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int i = 0; i < name.Length; i++)
            {
                if (Array.IndexOf(invalidChars, name[i]) >= 0)
                    return true;
            }
            // Also reject path traversal patterns
            return name.Contains("/") || name.Contains("\\") || name.Contains("..");
        }

        #endregion
    }
}
