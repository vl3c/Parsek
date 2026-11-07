using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// The recording file janitor (RecordingStore services plan, service E): deletes a
    /// recording's on-disk footprint, names the recording sidecar files, and builds the
    /// known-recording sets that gate the orphan and file-collector deletions. Reads the
    /// store's committed list, committed trees and pending slots through its accessors; logs
    /// keep the <c>RecordingStore</c> tag.
    /// </summary>
    internal static class RecordingFileJanitor
    {
        internal static string CleanOrphanFilesDirectoryOverrideForTesting;

        // The test seam above; RecordingStore.ResetForTestingInternal calls this at the point
        // where it used to clear it itself.
        internal static void ResetForTesting()
        {
            CleanOrphanFilesDirectoryOverrideForTesting = null;
        }

        /// <summary>
        /// Removes a recording's on-disk footprint from the head positions
        /// (design-load-restores-the-save.md 4.4 / 4.5). The three hashed sidecars
        /// (<c>.prec</c>, <c>_vessel.craft</c>, <c>_ghost.craft</c>) are RETAINED: each head goes
        /// through a <see cref="SidecarFileCommitBatch"/> delete that files its bytes under
        /// <c>_versions/&lt;id&gt;.&lt;hash&gt;&lt;suffix&gt;</c>, because some save file may still name
        /// them; the collector removes a version once no save names it. The non-hashed kinds
        /// (<c>.pann</c> and the three readable mirrors) are deleted. The recording's
        /// quickload-resume save under <c>Parsek/Saves/</c> is left for the collector too.
        ///
        /// <para>Returns <c>true</c> when every limb it attempted completed - i.e.
        /// no head was left behind. Returns <c>false</c> when the recording was
        /// unusable (null / invalid id) or when at least one file could not be
        /// moved (locked by KSP or an antivirus); each such failure also logs its
        /// own Warn. Per-file failures are swallowed rather than thrown, so a partial
        /// delete never aborts a caller's teardown - the return value exists so callers
        /// that report a batch summary (<see cref="DiscardSidecarReap"/>) can count
        /// truthfully instead of assuming success. Callers that do not report may ignore it.</para>
        /// </summary>
        internal static bool DeleteRecordingFiles(Recording rec)
        {
            if (rec == null)
            {
                ParsekLog.Verbose("RecordingStore", "DeleteRecordingFiles called with null recording");
                return false;
            }
            if (!RecordingPaths.ValidateRecordingId(rec.RecordingId))
            {
                ParsekLog.Warn("RecordingStore", $"DeleteRecordingFiles skipped: invalid recording id '{rec.RecordingId}'");
                return false;
            }

            ParsekLog.Verbose("RecordingStore",
                $"DeleteRecordingFiles: id={rec.RecordingId} vessel='{rec.VesselName}' rewindSave={rec.RewindSaveFileName ?? "(none)"}");

            bool allDeleted = true;
            allDeleted &= RetainHeadIfExists(
                RecordingPaths.BuildTrajectoryRelativePath(rec.RecordingId), rec.RecordingId, SidecarKind.Prec);
            allDeleted &= RetainHeadIfExists(
                RecordingPaths.BuildVesselSnapshotRelativePath(rec.RecordingId), rec.RecordingId, SidecarKind.Vessel);
            allDeleted &= RetainHeadIfExists(
                RecordingPaths.BuildGhostSnapshotRelativePath(rec.RecordingId), rec.RecordingId, SidecarKind.Ghost);
            // .pann annotation sidecar (design doc §17.3.1): regenerable cache,
            // but a stale file left behind after a recording is deleted could
            // be mis-cached against a future same-id recovery. Belongs in the
            // delete-path AND in RecordingFileSuffixes for orphan cleanup.
            allDeleted &= DeleteFileIfExists(RecordingPaths.BuildAnnotationsRelativePath(rec.RecordingId));
            allDeleted &= DeleteFileIfExists(RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(rec.RecordingId));
            allDeleted &= DeleteFileIfExists(RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(rec.RecordingId));
            allDeleted &= DeleteFileIfExists(RecordingPaths.BuildReadableGhostSnapshotMirrorRelativePath(rec.RecordingId));

            if (!string.IsNullOrEmpty(rec.RewindSaveFileName))
            {
                ParsekLog.Verbose("RecordingStore",
                    $"DeleteRecordingFiles: rewind save '{rec.RewindSaveFileName}' left for collection " +
                    "(deleted once no save names it)");
            }

            return allDeleted;
        }

        /// <summary>
        /// Moves one hashed head into <c>_versions</c> through a retained delete. Returns false
        /// ONLY when the move threw (the head is still there); a missing file or an unresolved
        /// save context counts as nothing-left-behind.
        /// </summary>
        private static bool RetainHeadIfExists(string relativePath, string recordingId, SidecarKind kind)
        {
            try
            {
                string absolutePath = RecordingPaths.ResolveSaveScopedPath(relativePath);
                if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                    return true;
                SidecarFileCommitBatch.Apply(
                    new List<SidecarFileCommitBatch.StagedChange>
                    {
                        SidecarFileCommitBatch.RetainedDelete(absolutePath, recordingId, kind)
                    },
                    () => RecordingStore.SuppressLogging);
                ParsekLog.Verbose("RecordingStore", $"Retained head as version: {relativePath}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore", $"Failed to retain head '{relativePath}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes one non-hashed save-scoped file. Returns false ONLY when the delete threw
        /// (the file is still there); a file that does not exist, or a path that cannot be
        /// resolved because there is no save context, counts as nothing-left-behind.
        /// </summary>
        private static bool DeleteFileIfExists(string relativePath)
        {
            string absolutePath;
            try
            {
                absolutePath = RecordingPaths.ResolveSaveScopedPath(relativePath);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("RecordingStore", $"Failed to delete file '{relativePath}': {ex.Message}");
                return false;
            }
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return true;
            if (!SidecarFileCommitBatch.TryDeleteUnretained(absolutePath, () => RecordingStore.SuppressLogging, out string failure))
            {
                ParsekLog.Warn("RecordingStore", $"Failed to delete file '{relativePath}': {failure}");
                return false;
            }
            ParsekLog.Verbose("RecordingStore", $"Deleted file: {relativePath}");
            return true;
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

        internal static bool IsTransientSidecarArtifactFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            for (int i = 0; i < RecordingFileSuffixes.Length; i++)
            {
                string suffix = RecordingFileSuffixes[i];
                if (fileName.EndsWith(suffix + FileIOUtils.SafeWriteTempSuffix, StringComparison.OrdinalIgnoreCase)
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

            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
                AddKnownRecordingForCleanup(recordings, seenIds, RecordingStore.CommittedRecordings[i]);

            if (RecordingStore.PendingTree != null && RecordingStore.PendingTree.Recordings != null)
            {
                foreach (var kvp in RecordingStore.PendingTree.Recordings)
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
        /// in the recordings directory. Excludes transient artifacts
        /// (.tmp / .stage / .bak suffixes) — only IDs corresponding to "live" sidecar
        /// files are returned. Used by both <see cref="ParsekFileCollector"/>'s safety
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
                if (IsTransientSidecarArtifactFile(fileName))
                    continue;
                string id = ExtractRecordingIdFromFileName(fileName);
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }

        /// <summary>
        /// Subfolder under Parsek/Recordings/ where orphaned recording sidecars are parked by
        /// <see cref="ParsekFileCollector"/> instead of being deleted. Top-level-only directory
        /// scans (Directory.GetFiles) never descend into it, so quarantined files are not
        /// re-scanned or double-counted.
        /// </summary>
        internal const string OrphanQuarantineDirName = "_quarantine";

        /// <summary>
        /// Visits every recording the store holds in memory: the committed list, the committed
        /// trees, the pending tree and the pending tree saved during an active restore. A
        /// recording reachable from two of them is visited twice. Used by
        /// <see cref="ParsekFileCollector"/> to build memory's reference set.
        /// </summary>
        internal static void ForEachRecordingKnownToMemory(Action<Recording> visit)
        {
            if (visit == null)
                return;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
                visit(RecordingStore.CommittedRecordings[i]);
            for (int t = 0; t < RecordingStore.CommittedTrees.Count; t++)
                VisitTreeRecordings(RecordingStore.CommittedTrees[t], visit);
            VisitTreeRecordings(RecordingStore.PendingTree, visit);
            VisitTreeRecordings(PendingTreeSlot.SavedPendingTreeDuringActiveRestore, visit);
        }

        private static void VisitTreeRecordings(RecordingTree tree, Action<Recording> visit)
        {
            if (tree == null || tree.Recordings == null)
                return;
            foreach (var kvp in tree.Recordings)
                visit(kvp.Value);
        }
    }
}
