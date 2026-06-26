using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek
{
    public static partial class RecordingStore
    {
        internal static void DeleteRecordingFiles(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose("RecordingStore", "DeleteRecordingFiles called with null recording");
                return;
            }
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                ParsekLog.Warn("RecordingStore", $"DeleteRecordingFiles skipped: invalid recording id '{rec.RecordingId}'");
                return;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeleteRecordingFiles: id={rec.RecordingId} vessel='{rec.VesselName}' rewindSave={rec.RewindSaveFileName ?? "(none)"}");

            DeleteFileIfExists(RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId));
            // .pann annotation sidecar (design doc §17.3.1): regenerable cache,
            // but a stale file left behind after a recording is deleted could
            // be mis-cached against a future same-id recovery. Belongs in the
            // delete-path AND in RecordingFileSuffixes for orphan cleanup.
            DeleteFileIfExists(RecordingPaths.BuildAnnotationsRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(rec.RecordingId));
            DeleteFileIfExists(RecordingPaths.BuildReadableGhostSnapshotMirrorRelativePath(rec.RecordingId));

            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
                DeleteFileIfExists(RecordingPaths.BuildRewindSaveRelativePath(rec.RewindSaveFileName));
        }

        private static void DeleteFileIfExists(string relativePath)
        {
            try
            {
                string absolutePath = RecordingPaths.ResolveSaveScopedPath(relativePath);
                if (!string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                    ParsekLog.Verbose("RecordingStore", $"Deleted file: {relativePath}");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore", $"Failed to delete file '{relativePath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Known sidecar file suffixes for recording files. Used for orphan detection.
        /// </summary>
        private static readonly string[] RecordingFileSuffixes =
        {
            ".prec",
            "_vessel.craft",
            "_ghost.craft",
            ".prec.txt",
            "_vessel.craft.txt",
            "_ghost.craft.txt",
            ".pann",
        };

        /// <summary>
        /// Suffixes for recording files written by previous Parsek versions but no longer
        /// used. CleanOrphanFiles deletes any of these unconditionally — they are by
        /// definition stale (the format that wrote them no longer exists).
        /// </summary>
        private static readonly string[] LegacyRecordingFileSuffixes = { ".pcrf" };

        /// <summary>
        /// Extracts the recording ID from a sidecar filename by stripping known suffixes.
        /// Returns null if the filename doesn't match any known suffix.
        /// </summary>
        internal static string ExtractRecordingIdFromFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return null;

            for (int i = 0; i < RecordingFileSuffixes.Length; i++)
            {
                if (fileName.EndsWith(RecordingFileSuffixes[i], StringComparison.OrdinalIgnoreCase))
                    return fileName.Substring(0, fileName.Length - RecordingFileSuffixes[i].Length);
            }
            return null;
        }

        /// <summary>
        /// True if <paramref name="fileName"/> matches a legacy sidecar suffix that no
        /// longer corresponds to live Parsek code. Pure helper for orphan-cleanup
        /// (#260 follow-up — old saves can have .pcrf files left over from the dead
        /// ghost-geometry scaffolding; they have no current consumer and should be
        /// removed unconditionally rather than left as "unrecognized" forever).
        /// </summary>
        internal static bool IsLegacySidecarFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            for (int i = 0; i < LegacyRecordingFileSuffixes.Length; i++)
            {
                if (fileName.EndsWith(LegacyRecordingFileSuffixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        internal static bool IsTransientSidecarArtifactFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            for (int i = 0; i < RecordingFileSuffixes.Length; i++)
            {
                string suffix = RecordingFileSuffixes[i];
                if (fileName.EndsWith(suffix + ".tmp", StringComparison.OrdinalIgnoreCase)
                    || fileName.IndexOf(suffix + ".stage.", StringComparison.OrdinalIgnoreCase) >= 0
                    || fileName.IndexOf(suffix + ".bak.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Builds the recording view used by load/save cleanup passes that
        /// reason over raw committed rows, while also protecting the pending
        /// tree during deferred-merge windows. LoadTimeSweep uses it for
        /// supersede endpoint existence; GroupHierarchyStore uses it for live
        /// group collection. Intentionally excludes the committed-tree
        /// dictionary: zombie cleanup removes rows from the flat committed
        /// list, and the same sweep must not see those rows again through the
        /// parallel tree store. The same exclusion keeps group pruning aligned
        /// to the raw committed list plus the deferred pending tree, without
        /// letting stale tree-only copies protect old hierarchy entries.
        /// </summary>
        internal static List<Recording> BuildKnownRecordingsForCleanup()
        {
            var recordings = new List<Recording>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingForCleanup(recordings, seenIds, committedRecordings[i]);

            if (pendingTree != null && pendingTree.Recordings != null)
            {
                foreach (var kvp in pendingTree.Recordings)
                    AddKnownRecordingForCleanup(recordings, seenIds, kvp.Value);
            }

            return recordings;
        }

        private static void AddKnownRecordingForCleanup(
            List<Recording> recordings,
            HashSet<string> seenIds,
            Recording rec)
        {
            if (rec == null)
                return;

            if (string.IsNullOrEmpty(rec.RecordingId))
            {
                recordings.Add(rec);
                return;
            }

            if (seenIds.Add(rec.RecordingId))
                recordings.Add(rec);
        }

        internal static HashSet<string> BuildKnownRecordingIdsForCleanup()
        {
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingId(knownIds, committedRecordings[i]);

            AddKnownTreeRecordingIds(knownIds, pendingTree);
            AddKnownTreeRecordingIds(knownIds, savedPendingTreeDuringActiveRestore);
            return knownIds;
        }

        private static void AddKnownRecordingId(HashSet<string> knownIds, Recording rec)
        {
            if (!string.IsNullOrEmpty(rec?.RecordingId))
                knownIds.Add(rec.RecordingId);
        }

        private static int AddKnownTreeRecordingIds(HashSet<string> knownIds, RecordingTree tree)
        {
            int count = 0;
            if (tree == null || tree.Recordings == null)
                return 0;

            foreach (var kvp in tree.Recordings)
            {
                if (!string.IsNullOrEmpty(kvp.Value?.RecordingId))
                {
                    AddKnownRecordingId(knownIds, kvp.Value);
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Builds the set of all recording IDs that are currently known to the store
        /// (committed recordings, committed trees, and the pending tree). Used by
        /// <see cref="CleanOrphanFiles"/> to decide which sidecar files to keep.
        /// Extracted as <c>internal static</c> for direct testability (#290).
        /// </summary>
        internal static HashSet<string> BuildKnownRecordingIds()
        {
            return BuildKnownRecordingIds(out _);
        }

        /// <summary>
        /// Overload that also reports how many IDs came from the pending tree
        /// (for diagnostic logging in <see cref="CleanOrphanFiles"/>).
        /// </summary>
        internal static HashSet<string> BuildKnownRecordingIds(out int pendingTreeIdCount)
        {
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < committedRecordings.Count; i++)
                AddKnownRecordingId(knownIds, committedRecordings[i]);

            for (int t = 0; t < committedTrees.Count; t++)
            {
                var treeRecordings = committedTrees[t]?.Recordings;
                if (treeRecordings == null) continue;
                foreach (var kvp in treeRecordings)
                    AddKnownRecordingId(knownIds, kvp.Value);
            }

            pendingTreeIdCount = 0;
            pendingTreeIdCount += AddKnownTreeRecordingIds(knownIds, pendingTree);
            pendingTreeIdCount += AddKnownTreeRecordingIds(
                knownIds, savedPendingTreeDuringActiveRestore);
            return knownIds;
        }

        /// <summary>
        /// Resolves the Parsek/Recordings/ directory path for the currently-active save
        /// (or the test override). Returns null if no save context is available or the
        /// directory does not exist. Does not create the directory.
        /// </summary>
        internal static string ResolveRecordingsDirectoryForCurrentSave()
        {
            if (!string.IsNullOrEmpty(CleanOrphanFilesDirectoryOverrideForTesting))
                return Path.GetFullPath(CleanOrphanFilesDirectoryOverrideForTesting);
            string root;
            string saveFolder;
            try
            {
                root = KSPUtil.ApplicationRootPath ?? "";
                saveFolder = HighLogic.SaveFolder ?? "";
            }
            catch
            {
                // Unity bindings unavailable (e.g. unit-test host) — no save context.
                return null;
            }
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                return null;
            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "Recordings"));
        }

        /// <summary>
        /// Returns the set of distinct recording IDs whose sidecar files (.prec,
        /// _vessel.craft, _ghost.craft, .pann, plus the .txt readable variants) exist
        /// in the recordings directory. Excludes legacy / transient artifacts (.pcrf,
        /// .tmp / .stage / .bak suffixes) — only IDs corresponding to "live" sidecar
        /// files are returned. Used by both <see cref="CleanOrphanFiles"/>'s safety
        /// guard and <c>ParsekScenario.OnSave</c>'s stranded-sidecar warn.
        /// </summary>
        internal static HashSet<string> CollectSidecarIdsOnDisk()
        {
            var ids = new HashSet<string>();
            string dir = ResolveRecordingsDirectoryForCurrentSave();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return ids;
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return ids; }
            AddSidecarIdsFromFiles(files, ids);
            return ids;
        }

        private static void AddSidecarIdsFromFiles(string[] files, HashSet<string> ids)
        {
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (IsTransientSidecarArtifactFile(fileName) || IsLegacySidecarFile(fileName))
                    continue;
                string id = ExtractRecordingIdFromFileName(fileName);
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }

        /// <summary>
        /// Deletes orphaned sidecar files in the Parsek/Recordings/ directory that don't
        /// correspond to any known recording ID. Called after all recordings and trees are loaded.
        /// </summary>
        internal static void CleanOrphanFiles()
        {
            string recordingsDir = ResolveRecordingsDirectoryForCurrentSave();
            if (string.IsNullOrEmpty(recordingsDir))
            {
                ParsekLog.Verbose("RecordingStore", "CleanOrphanFiles: no save context — skipping");
                return;
            }
            if (!Directory.Exists(recordingsDir))
            {
                ParsekLog.Verbose("RecordingStore", "CleanOrphanFiles: no recordings directory — skipping");
                return;
            }

            // Build set of known recording IDs from committed + pending state.
            // On cold-start resume, TryRestoreActiveTreeNode stashes the active
            // tree into pendingTree BEFORE this method runs. Without including
            // pendingTree, branch recordings (debris, EVA) would be deleted as
            // orphans, silently degrading to 0 points on the next cold start (#290).
            // If the save also has a finalized isPending=True tree, it is held in
            // savedPendingTreeDuringActiveRestore and counted here too so active
            // quickload resume cannot orphan the unrelated pending sidecars.
            var knownIds = BuildKnownRecordingIds(out int pendingTreeIds);

            string[] files;
            try
            {
                files = Directory.GetFiles(recordingsDir);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore", $"CleanOrphanFiles: failed to list directory: {ex.Message}");
                return;
            }

            // Safety guard: refuse to delete when scenario reports zero known recording IDs
            // but the disk has live sidecar-shaped recording IDs. This is the "load lost its
            // tree state" pattern — deleting now turns a recoverable accident (the .sfs tree
            // metadata may still live in quicksave.sfs or a .bak) into permanent bulk-data
            // loss. The OnSave-side warn pairs with this guard to flag the originating fault.
            if (knownIds.Count == 0)
            {
                var diskIds = new HashSet<string>();
                AddSidecarIdsFromFiles(files, diskIds);
                if (diskIds.Count > 0)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"CleanOrphanFiles: REFUSING to delete — scenario reports 0 known recording IDs " +
                        $"but disk has {diskIds.Count} sidecar-shaped recording ID(s) ({files.Length} file(s) total). " +
                        $"This usually means the scenario load lost its tree state. Sidecars preserved so the " +
                        $"save can be restored from quicksave.sfs or a backup. Investigate before next save.");
                    return;
                }
            }

            ParsekLog.Verbose("RecordingStore",
                $"CleanOrphanFiles: scanning {files.Length} file(s) against {knownIds.Count} known recording ID(s)" +
                (pendingTreeIds > 0 ? $" (incl. {pendingTreeIds} from pending tree)" : ""));

            int orphanCount = 0;
            int legacyCount = 0;
            int transientCount = 0;
            int skippedUnrecognized = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                string extractedId = ExtractRecordingIdFromFileName(fileName);
                if (extractedId == null)
                {
                    // Legacy sidecars (e.g. .pcrf from the removed ghost-geometry scaffolding,
                    // #260) have no current consumer — delete unconditionally.
                    if (IsLegacySidecarFile(fileName))
                    {
                        try
                        {
                            File.Delete(files[i]);
                            legacyCount++;
                            ParsekLog.Verbose("RecordingStore", $"Deleted legacy sidecar file: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn("RecordingStore", $"Failed to delete legacy sidecar file '{fileName}': {ex.Message}");
                        }
                        continue;
                    }
                    if (IsTransientSidecarArtifactFile(fileName))
                    {
                        try
                        {
                            File.Delete(files[i]);
                            transientCount++;
                            ParsekLog.Verbose("RecordingStore", $"Deleted transient sidecar artifact: {fileName}");
                        }
                        catch (Exception ex)
                        {
                            ParsekLog.Warn("RecordingStore",
                                $"Failed to delete transient sidecar artifact '{fileName}': {ex.Message}");
                        }
                        continue;
                    }
                    skippedUnrecognized++;
                    continue; // Not a recognized sidecar file — leave it alone
                }

                if (!knownIds.Contains(extractedId))
                {
                    // Data-loss fix: orphaned RECORDING sidecars (.prec / craft / .pann with a
                    // real recording id) are MOVED to a quarantine subfolder, never hard-deleted.
                    // A sidecar can be "orphaned" not only by genuine garbage but by a transient
                    // state bug that drops a still-referenced tree (e.g. a Limbo quickload-resume
                    // tree that fell out of persistent.sfs — see the SavePendingTreeIfAny fix).
                    // Deleting then was a one-way destruction of immutable recorded data; quarantine
                    // de-clutters the active set while keeping the bulk data fully recoverable.
                    // Legacy (.pcrf) and transient (.tmp/.stage/.bak) artifacts above are still
                    // hard-deleted — they are by definition junk, not recorded data.
                    if (QuarantineOrphanRecordingFile(files[i], recordingsDir, fileName, extractedId))
                        orphanCount++;
                }
            }

            if (orphanCount > 0 || legacyCount > 0 || transientCount > 0)
                ParsekLog.Info("RecordingStore",
                    $"Cleaned orphan files: quarantined {orphanCount} orphaned recording file(s)" +
                    (legacyCount > 0 ? $", deleted {legacyCount} legacy sidecar file(s)" : "") +
                    (transientCount > 0 ? $", deleted {transientCount} transient sidecar artifact(s)" : "") +
                    (skippedUnrecognized > 0 ? $", skipped {skippedUnrecognized} unrecognized file(s)" : ""));
            else
                ParsekLog.Verbose("RecordingStore",
                    $"CleanOrphanFiles: no orphans found" +
                    (skippedUnrecognized > 0 ? $", skipped {skippedUnrecognized} unrecognized file(s)" : ""));
        }

        /// <summary>
        /// Subfolder under Parsek/Recordings/ where orphaned recording sidecars are parked by
        /// <see cref="CleanOrphanFiles"/> instead of being deleted. Top-level-only directory
        /// scans (Directory.GetFiles) never descend into it, so quarantined files are not
        /// re-scanned or double-counted.
        /// </summary>
        internal const string OrphanQuarantineDirName = "_quarantine";

        /// <summary>
        /// Moves an orphaned recording sidecar into the <see cref="OrphanQuarantineDirName"/>
        /// subfolder of <paramref name="recordingsDir"/>. Non-destructive: the immutable bulk
        /// data is preserved and recoverable. If a same-named file already sits in quarantine
        /// (e.g. a prior sweep of the same id), the existing copy is kept and the new one is
        /// suffixed so nothing is overwritten. Returns true if the file was moved.
        /// </summary>
        private static bool QuarantineOrphanRecordingFile(
            string filePath, string recordingsDir, string fileName, string extractedId)
        {
            try
            {
                string quarantineDir = Path.Combine(recordingsDir, OrphanQuarantineDirName);
                Directory.CreateDirectory(quarantineDir);
                string dest = Path.Combine(quarantineDir, fileName);
                if (File.Exists(dest))
                {
                    // Preserve the earlier quarantined copy; park this one alongside it.
                    string suffixed = Path.Combine(
                        quarantineDir,
                        fileName + ".dup" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    dest = suffixed;
                }
                File.Move(filePath, dest);
                ParsekLog.Verbose("RecordingStore",
                    $"Quarantined orphan recording file: {fileName} (id={extractedId}) -> {OrphanQuarantineDirName}/");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore",
                    $"Failed to quarantine orphan recording file '{fileName}': {ex.Message} (left in place, NOT deleted)");
                return false;
            }
        }
    }
}
