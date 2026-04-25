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
        }

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
            catch
            {
                DeleteTransientArtifact(stagedPath);
                DeleteTransientArtifact(stagedPath + ".tmp");
                throw;
            }

            return new StagedChange
            {
                FinalPath = finalPath,
                StagedPath = stagedPath,
                DeleteExisting = false
            };
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
                        BackupPath = string.IsNullOrEmpty(change.FinalPath)
                            ? null
                            : change.FinalPath + ".bak." + Guid.NewGuid().ToString("N")
                    };

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
                    string stagedPath = changes[i]?.StagedPath;
                    RecordCleanupFailure(failures, DeleteTransientArtifact(stagedPath));
                    RecordCleanupFailure(
                        failures,
                        DeleteTransientArtifact(
                            string.IsNullOrEmpty(stagedPath) ? null : stagedPath + ".tmp"));
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
            CleanupArtifacts(changes: null, committed: committed, suppressLogging: suppressLogging);
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
