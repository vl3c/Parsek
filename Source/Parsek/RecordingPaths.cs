using System;
using System.IO;

namespace Parsek
{
    internal enum RecordingIdValidationLogContext
    {
        Production,
        Test,
    }

    internal static class RecordingPaths
    {
        internal static string BuildTrajectoryRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}.prec");
        }

        internal static string BuildVesselSnapshotRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}_vessel.craft");
        }

        internal static string BuildGhostSnapshotRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}_ghost.craft");
        }

        internal static string BuildReadableTrajectoryMirrorRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}.prec.txt");
        }

        internal static string BuildReadableVesselSnapshotMirrorRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}_vessel.craft.txt");
        }

        internal static string BuildReadableGhostSnapshotMirrorRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}_ghost.craft.txt");
        }

        internal static string ResolveSaveScopedPath(string relativePath)
        {
            if (!TryGetSaveContext(
                    "ResolveSaveScopedPath",
                    "resolve-save-scoped-missing-context",
                    relativePath,
                    out string root,
                    out string saveFolder))
                return null;

            try
            {
                return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, relativePath));
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Paths",
                    $"ResolveSaveScopedPath failed: saveFolder='{saveFolder}' " +
                    $"relativePath='{FormatPathContext(relativePath)}' " +
                    $"ex={ex.GetType().Name}:{ex.Message}");
                return null;
            }
        }

        internal static string EnsureRecordingsDirectory()
        {
            if (!TryGetSaveContext(
                    "EnsureRecordingsDirectory",
                    "ensure-recordings-missing-context",
                    null,
                    out string root,
                    out string saveFolder))
                return null;

            return EnsureSaveScopedDirectory(
                root,
                saveFolder,
                "EnsureRecordingsDirectory",
                Path.Combine("Parsek", "Recordings"),
                "recordings");
        }

        internal static string BuildLedgerRelativePath()
        {
            return Path.Combine("Parsek", "GameState", "ledger.pgld");
        }

        internal static string BuildMilestonesRelativePath()
        {
            return Path.Combine("Parsek", "GameState", "milestones.pgsm");
        }

        internal static string BuildRewindSaveRelativePath(string saveFileName)
        {
            return Path.Combine("Parsek", "Saves", saveFileName + ".sfs");
        }

        /// <summary>
        /// Relative path to the <c>Parsek/RewindPoints/&lt;rpId&gt;.sfs</c>
        /// quicksave file for a given <see cref="RewindPoint"/> id. Input is
        /// validated through <see cref="ValidateRecordingId"/> to reject path
        /// traversal and invalid filename characters; returns <c>null</c> on
        /// validation failure (and the validator logs a Warn).
        /// </summary>
        internal static string BuildRewindPointRelativePath(string rewindPointId)
        {
            if (!ValidateRecordingId(rewindPointId))
                return null;
            return Path.Combine(RewindPointsSubdir, rewindPointId + ".sfs");
        }

        internal const string RewindPointsSubdir = "Parsek/RewindPoints";

        /// <summary>
        /// Ensures <c>saves/&lt;save&gt;/Parsek/RewindPoints/</c> exists; returns
        /// the absolute directory path or <c>null</c> if the KSP root or current
        /// save folder cannot be resolved.
        /// </summary>
        internal static string EnsureRewindPointsDirectory()
        {
            if (!TryGetSaveContext(
                    "EnsureRewindPointsDirectory",
                    "ensure-rewindpoints-missing-context",
                    null,
                    out string root,
                    out string saveFolder))
                return null;

            return EnsureSaveScopedDirectory(
                root,
                saveFolder,
                "EnsureRewindPointsDirectory",
                Path.Combine("Parsek", "RewindPoints"),
                "rewind points");
        }

        internal static string EnsureRewindSavesDirectory()
        {
            if (!TryGetSaveContext(
                    "EnsureRewindSavesDirectory",
                    "ensure-rewindsaves-missing-context",
                    null,
                    out string root,
                    out string saveFolder))
                return null;

            return EnsureSaveScopedDirectory(
                root,
                saveFolder,
                "EnsureRewindSavesDirectory",
                Path.Combine("Parsek", "Saves"),
                "rewind saves");
        }

        internal static string BuildGameStateEventsRelativePath()
        {
            return Path.Combine("Parsek", "GameState", "events.pgse");
        }

        internal static string BuildBaselineRelativePath(double ut)
        {
            return Path.Combine("Parsek", "GameState",
                $"baseline_{ut.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}.pgsb");
        }

        internal static string EnsureGameStateDirectory()
        {
            if (!TryGetSaveContext(
                    "EnsureGameStateDirectory",
                    "ensure-gamestate-missing-context",
                    null,
                    out string root,
                    out string saveFolder))
                return null;

            return EnsureSaveScopedDirectory(
                root,
                saveFolder,
                "EnsureGameStateDirectory",
                Path.Combine("Parsek", "GameState"),
                "game state");
        }

        internal static string ResolveGameStateDirectory()
        {
            if (!TryGetSaveContext(
                    "ResolveGameStateDirectory",
                    "resolve-gamestate-missing-context",
                    null,
                    out string root,
                    out string saveFolder))
                return null;

            try
            {
                return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "GameState"));
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Paths",
                    $"ResolveGameStateDirectory failed: saveFolder='{saveFolder}' " +
                    $"ex={ex.GetType().Name}:{ex.Message}");
                return null;
            }
        }

        internal static bool ValidateRecordingId(
            string id,
            RecordingIdValidationLogContext logContext = RecordingIdValidationLogContext.Production)
        {
            if (string.IsNullOrEmpty(id))
            {
                LogRecordingIdValidationFailure(
                    "Recording id validation failed: id is null or empty",
                    logContext);
                return false;
            }
            if (id.Contains("/") || id.Contains("\\") || id.Contains(".."))
            {
                LogRecordingIdValidationFailure(
                    $"Recording id validation failed for '{id}': contains invalid path sequence",
                    logContext);
                return false;
            }
            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int i = 0; i < id.Length; i++)
            {
                if (Array.IndexOf(invalidChars, id[i]) >= 0)
                {
                    LogRecordingIdValidationFailure(
                        $"Recording id validation failed for '{id}': contains invalid file-name char",
                        logContext);
                    return false;
                }
            }
            return true;
        }

        private static void LogRecordingIdValidationFailure(
            string message,
            RecordingIdValidationLogContext logContext)
        {
            if (logContext == RecordingIdValidationLogContext.Test)
            {
                ParsekLog.Verbose("Paths", message);
                return;
            }

            ParsekLog.Warn("Paths", message);
        }

        private static bool TryGetSaveContext(
            string operation,
            string rateLimitKey,
            string relativePath,
            out string root,
            out string saveFolder)
        {
            string rootError = null;
            string saveError = null;
            try { root = KSPUtil.ApplicationRootPath ?? ""; }
            catch (Exception ex)
            {
                root = "";
                rootError = ex.GetType().Name + ":" + ex.Message;
            }

            try { saveFolder = HighLogic.SaveFolder ?? ""; }
            catch (Exception ex)
            {
                saveFolder = "";
                saveError = ex.GetType().Name + ":" + ex.Message;
            }

            bool rootSet = !string.IsNullOrEmpty(root);
            bool saveSet = !string.IsNullOrEmpty(saveFolder);
            bool relativeSet = relativePath == null || !string.IsNullOrEmpty(relativePath);
            if (rootSet && saveSet && relativeSet)
                return true;

            ParsekLog.WarnRateLimited("Paths", rateLimitKey,
                $"{operation} missing context: rootSet={rootSet}, saveSet={saveSet}, " +
                $"relativeSet={relativeSet}, saveFolder='{FormatPathContext(saveFolder)}', " +
                $"relativePath='{FormatPathContext(relativePath)}'" +
                (string.IsNullOrEmpty(rootError) ? "" : $" rootError={rootError}") +
                (string.IsNullOrEmpty(saveError) ? "" : $" saveError={saveError}"),
                5.0);
            return false;
        }

        private static string EnsureSaveScopedDirectory(
            string root,
            string saveFolder,
            string operation,
            string relativeDirectory,
            string label)
        {
            try
            {
                string dir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, relativeDirectory));
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    ParsekLog.Info("Paths", $"Created {label} directory '{dir}'");
                }
                return dir;
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Paths",
                    $"{operation} failed: saveFolder='{FormatPathContext(saveFolder)}' " +
                    $"relativePath='{FormatPathContext(relativeDirectory)}' " +
                    $"ex={ex.GetType().Name}:{ex.Message}");
                return null;
            }
        }

        private static string FormatPathContext(string value)
        {
            return string.IsNullOrEmpty(value) ? "<null>" : value;
        }
    }
}
