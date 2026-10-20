using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek
{
    internal static class SidecarFileCommitBatch
    {
        internal static Action<string> DeleteTransientArtifactOverrideForTesting;

        internal sealed class StagedChange
        {
            public string FinalPath;
            public string StagedPath;
            public bool DeleteExisting;

            // Retention (design-load-restores-the-save.md 4.4): for a hashed sidecar kind the
            // replaced or deleted head is backed up under _versions and filed there by the
            // hash of its own bytes after the batch commits, instead of being deleted.
            public SidecarKind RetainKind;
            public string RetainRecordingId;

            // Hash of the staged bytes (the new head). Computed by Apply for a retained write
            // when the caller did not supply it; read back by the caller after Apply.
            public string StagedHash;

            // The staged file is not a scratch file (a version being promoted to head): a
            // failed batch must leave it in place.
            public bool PreserveStagedOnFailure;

            internal bool IsRetained =>
                RetainKind != SidecarKind.None && !string.IsNullOrEmpty(RetainRecordingId);
        }

        private const string Tag = "RecordingStore";

        // Process-lifetime retention counters behind the rate-limited summary line.
        private static int retainedFiled;
        private static int retainedIdenticalExists;
        private static int retainedSameAsHead;
        private static int retainedLeftIncoming;

        private sealed class CommittedChange
        {
            public StagedChange Change;
            public bool HadOriginalFile;
            public bool Committed;
            public string BackupPath;
        }

        internal static StagedChange StageWrite(Action<string> writer, string finalPath)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (string.IsNullOrEmpty(finalPath))
                throw new ArgumentException("Final path is required.", nameof(finalPath));

            string dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string stagedPath = finalPath + ".stage." + Guid.NewGuid().ToString("N");
            try
            {
                writer(stagedPath);
            }
            // The automation-only crash-after-temp hook stands in for a process death, which
            // runs no catch block: its residue must reach the next load's orphan sweep.
            catch (Exception ex) when (!(ex is FileIOUtils.SafeWriteInjectedCrashException))
            {
                DeleteTransientArtifact(stagedPath);
                DeleteTransientArtifact(stagedPath + FileIOUtils.SafeWriteTempSuffix);
                throw;
            }

            return new StagedChange
            {
                FinalPath = finalPath,
                StagedPath = stagedPath,
                DeleteExisting = false
            };
        }

        /// <summary>A staged write whose replaced head is retained as a version (design 4.4).</summary>
        internal static StagedChange StageRetainedWrite(
            Action<string> writer, string finalPath, string recordingId, SidecarKind kind)
        {
            StagedChange change = StageWrite(writer, finalPath);
            change.RetainKind = kind;
            change.RetainRecordingId = recordingId;
            return change;
        }

        /// <summary>A delete of a head whose bytes are retained as a version (design 4.4).</summary>
        internal static StagedChange RetainedDelete(string finalPath, string recordingId, SidecarKind kind)
        {
            return new StagedChange
            {
                FinalPath = finalPath,
                DeleteExisting = true,
                RetainKind = kind,
                RetainRecordingId = recordingId,
            };
        }

        /// <summary>
        /// Deletes one non-hashed file (a readable mirror, a <c>.pann</c>) through a one-change
        /// batch. Returns false when the delete threw; the file is then still there.
        /// </summary>
        internal static bool TryDeleteUnretained(string path, Func<bool> suppressLogging, out string failure)
        {
            failure = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return true;
            try
            {
                Apply(new List<StagedChange>
                {
                    new StagedChange { FinalPath = path, DeleteExisting = true }
                }, suppressLogging);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ":" + ex.Message;
                return false;
            }
        }

        internal static void Apply(List<StagedChange> changes, Func<bool> suppressLogging)
        {
            if (changes == null || changes.Count == 0)
                return;

            var committed = new List<CommittedChange>(changes.Count);
            try
            {
                for (int i = 0; i < changes.Count; i++)
                {
                    StagedChange change = changes[i];
                    var state = new CommittedChange
                    {
                        Change = change,
                        HadOriginalFile = !string.IsNullOrEmpty(change.FinalPath) && File.Exists(change.FinalPath),
                        BackupPath = BuildBackupPath(change)
                    };

                    if (change.IsRetained
                        && !change.DeleteExisting
                        && change.StagedHash == null
                        && !string.IsNullOrEmpty(change.StagedPath)
                        && File.Exists(change.StagedPath))
                    {
                        change.StagedHash = SidecarContentHash.ComputeFile(change.StagedPath);
                    }

                    if (change.DeleteExisting)
                    {
                        if (state.HadOriginalFile)
                        {
                            File.Move(change.FinalPath, state.BackupPath);
                            state.Committed = true;
                        }
                    }
                    else if (!string.IsNullOrEmpty(change.StagedPath))
                    {
                        if (state.HadOriginalFile)
                            File.Replace(change.StagedPath, change.FinalPath, state.BackupPath, true);
                        else
                            File.Move(change.StagedPath, change.FinalPath);

                        state.Committed = true;
                    }

                    committed.Add(state);
                }
            }
            catch
            {
                // #366: per-step try/catch so a rollback failure on one file
                // (e.g. backup deleted by external process or disk full mid-restore)
                // doesn't abort the remaining rollback. Atomicity is best-effort
                // across multiple files; the goal is to minimize remaining
                // inconsistency rather than achieve perfect rollback.
                for (int i = committed.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        RestoreCommittedChange(committed[i]);
                    }
                    catch (Exception rollbackEx)
                    {
                        if (suppressLogging == null || !suppressLogging())
                        {
                            string finalPath = committed[i]?.Change?.FinalPath ?? "?";
                            ParsekLog.Warn("RecordingStore",
                                $"ApplyStagedSidecarChanges: rollback step failed " +
                                $"path={finalPath} " +
                                $"ex={rollbackEx.GetType().Name}:{rollbackEx.Message}");
                        }
                    }
                }
                throw;
            }
            finally
            {
                CleanupArtifacts(changes, committed: null, suppressLogging: suppressLogging);
            }

            CleanupCommittedBackups(committed, suppressLogging);
        }

        internal static void CleanupStagedArtifacts(List<StagedChange> changes)
        {
            CleanupArtifacts(changes, committed: null, suppressLogging: null);
        }

        internal static void CleanupStagedArtifacts(List<StagedChange> changes, Func<bool> suppressLogging)
        {
            CleanupArtifacts(changes, committed: null, suppressLogging: suppressLogging);
        }

        private static string BuildBackupPath(StagedChange change)
        {
            if (change == null || string.IsNullOrEmpty(change.FinalPath))
                return null;

            if (!change.IsRetained)
                return change.FinalPath + ".bak." + Guid.NewGuid().ToString("N");

            // Same directory tree as the head, so the replace stays a rename and a rollback
            // restores from it exactly as from a .bak.
            string versionsDir = SidecarVersions.VersionsDirectoryForHead(change.FinalPath);
            if (!Directory.Exists(versionsDir))
                Directory.CreateDirectory(versionsDir);
            return Path.Combine(versionsDir,
                SidecarVersions.IncomingFileName(change.RetainRecordingId, change.RetainKind));
        }

        private static void RestoreCommittedChange(CommittedChange state)
        {
            if (state == null || !state.Committed || state.Change == null || string.IsNullOrEmpty(state.Change.FinalPath))
                return;

            if (state.Change.DeleteExisting)
            {
                if (!state.HadOriginalFile || string.IsNullOrEmpty(state.BackupPath) || !File.Exists(state.BackupPath))
                    return;

                if (File.Exists(state.Change.FinalPath))
                    File.Delete(state.Change.FinalPath);

                File.Move(state.BackupPath, state.Change.FinalPath);
                return;
            }

            if (state.HadOriginalFile)
            {
                if (string.IsNullOrEmpty(state.BackupPath) || !File.Exists(state.BackupPath))
                    return;

                if (File.Exists(state.Change.FinalPath))
                    File.Replace(state.BackupPath, state.Change.FinalPath, null, true);
                else
                    File.Move(state.BackupPath, state.Change.FinalPath);
                return;
            }

            if (File.Exists(state.Change.FinalPath))
                File.Delete(state.Change.FinalPath);
        }

        private static void CleanupArtifacts(
            List<StagedChange> changes,
            List<CommittedChange> committed,
            Func<bool> suppressLogging)
        {
            var failures = new List<string>();

            if (changes != null)
            {
                for (int i = 0; i < changes.Count; i++)
                {
                    if (changes[i] != null && changes[i].PreserveStagedOnFailure)
                        continue;
                    string stagedPath = changes[i]?.StagedPath;
                    RecordCleanupFailure(failures, DeleteTransientArtifact(stagedPath));
                    RecordCleanupFailure(
                        failures,
                        DeleteTransientArtifact(
                            string.IsNullOrEmpty(stagedPath) ? null : stagedPath + FileIOUtils.SafeWriteTempSuffix));
                }
            }

            if (committed != null)
            {
                for (int i = 0; i < committed.Count; i++)
                {
                    string backupPath = committed[i]?.BackupPath;
                    RecordCleanupFailure(failures, DeleteTransientArtifact(backupPath));
                }
            }

            if (failures.Count > 0 && (suppressLogging == null || !suppressLogging()))
            {
                ParsekLog.Warn("RecordingStore",
                    $"Sidecar transient cleanup failed: count={failures.Count} first={failures[0]}");
            }
        }

        private static void CleanupCommittedBackups(
            List<CommittedChange> committed,
            Func<bool> suppressLogging)
        {
            var unretained = new List<CommittedChange>(committed.Count);
            for (int i = 0; i < committed.Count; i++)
            {
                CommittedChange state = committed[i];
                if (state?.Change != null && state.Change.IsRetained)
                    FileRetainedBackup(state, suppressLogging);
                else
                    unretained.Add(state);
            }
            CleanupArtifacts(changes: null, committed: unretained, suppressLogging: suppressLogging);
        }

        /// <summary>
        /// Design 4.4 steps 2-4: hash the replaced head from its own bytes (never from
        /// memory's idea of it) and rename it to <c>_versions/&lt;id&gt;.&lt;hash&gt;&lt;suffix&gt;</c>.
        /// An existing target holds the same bytes and a hash equal to the new head's means a
        /// byte-identical rewrite: the incoming backup is dropped in both cases. A failure
        /// leaves the <c>.incoming.</c> file for the collector to file.
        /// </summary>
        private static void FileRetainedBackup(CommittedChange state, Func<bool> suppressLogging)
        {
            StagedChange change = state.Change;
            string backup = state.BackupPath;
            if (string.IsNullOrEmpty(backup) || !File.Exists(backup))
                return;

            string reason = change.DeleteExisting ? "delete" : "rewrite";
            string hash = null;
            string outcome;
            try
            {
                hash = SidecarContentHash.ComputeFile(backup);
                string target = Path.Combine(
                    Path.GetDirectoryName(backup),
                    SidecarVersions.VersionFileName(change.RetainRecordingId, hash, change.RetainKind));

                if (!change.DeleteExisting
                    && string.Equals(hash, change.StagedHash, StringComparison.Ordinal))
                {
                    outcome = DeleteTransientArtifact(backup) == null ? "same-as-head" : "left-incoming";
                }
                else if (File.Exists(target))
                {
                    outcome = DeleteTransientArtifact(backup) == null ? "identical-exists" : "left-incoming";
                }
                else
                {
                    File.Move(backup, target);
                    outcome = "filed";
                }
            }
            catch (Exception ex)
            {
                outcome = "left-incoming";
                if (suppressLogging == null || !suppressLogging())
                {
                    ParsekLog.Warn(Tag,
                        $"Sidecar retention filing failed: rec={change.RetainRecordingId} " +
                        $"kind={SidecarVersions.Label(change.RetainKind)} backup='{backup}' " +
                        $"ex={ex.GetType().Name}:{ex.Message} - left for the collector");
                }
            }

            switch (outcome)
            {
                case "filed": retainedFiled++; break;
                case "identical-exists": retainedIdenticalExists++; break;
                case "same-as-head": retainedSameAsHead++; break;
                default: retainedLeftIncoming++; break;
            }

            if (suppressLogging == null || !suppressLogging())
            {
                ParsekLog.Verbose(Tag,
                    $"Sidecar retained: rec={change.RetainRecordingId} " +
                    $"kind={SidecarVersions.Label(change.RetainKind)} hash={hash ?? "<unknown>"} " +
                    $"reason={reason} outcome={outcome}");
                ParsekLog.VerboseRateLimited(Tag, "sidecar-retention-summary",
                    $"Sidecar retention summary (process): filed={retainedFiled} " +
                    $"identicalExists={retainedIdenticalExists} sameAsHead={retainedSameAsHead} " +
                    $"leftIncoming={retainedLeftIncoming}", 30.0);
            }
        }

        private static void RecordCleanupFailure(List<string> failures, string failure)
        {
            if (failures == null || string.IsNullOrEmpty(failure))
                return;

            failures.Add(failure);
        }

        private static string DeleteTransientArtifact(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                if (DeleteTransientArtifactOverrideForTesting != null)
                    DeleteTransientArtifactOverrideForTesting(path);
                else
                    File.Delete(path);
                return null;
            }
            catch (Exception ex)
            {
                return $"path='{path}' ex={ex.GetType().Name}:{ex.Message}";
            }
        }
    }
}
