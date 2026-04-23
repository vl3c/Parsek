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
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder) || string.IsNullOrEmpty(relativePath))
            {
                ParsekLog.VerboseRateLimited("Paths", "resolve-save-scoped-missing-context",
                    $"ResolveSaveScopedPath missing context: rootSet={!string.IsNullOrEmpty(root)}, " +
                    $"saveSet={!string.IsNullOrEmpty(saveFolder)}, relativeSet={!string.IsNullOrEmpty(relativePath)}",
                    5.0);
                return null;
            }
            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, relativePath));
        }

        internal static string EnsureRecordingsDirectory()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.VerboseRateLimited("Paths", "ensure-recordings-missing-context",
                    $"EnsureRecordingsDirectory missing context: rootSet={!string.IsNullOrEmpty(root)}, " +
                    $"saveSet={!string.IsNullOrEmpty(saveFolder)}", 5.0);
                return null;
            }

            string dir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "Recordings"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                ParsekLog.Info("Paths", $"Created recordings directory '{dir}'");
            }
            return dir;
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
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.VerboseRateLimited("Paths", "ensure-rewindpoints-missing-context",
                    $"EnsureRewindPointsDirectory missing context: rootSet={!string.IsNullOrEmpty(root)}, " +
                    $"saveSet={!string.IsNullOrEmpty(saveFolder)}", 5.0);
                return null;
            }

            string dir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "RewindPoints"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                ParsekLog.Info("Paths", $"Created rewind points directory '{dir}'");
            }
            return dir;
        }

        internal static string EnsureRewindSavesDirectory()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.VerboseRateLimited("Paths", "ensure-rewindsaves-missing-context",
                    $"EnsureRewindSavesDirectory missing context: rootSet={!string.IsNullOrEmpty(root)}, " +
                    $"saveSet={!string.IsNullOrEmpty(saveFolder)}", 5.0);
                return null;
            }

            string dir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "Saves"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                ParsekLog.Info("Paths", $"Created rewind saves directory '{dir}'");
            }
            return dir;
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
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.VerboseRateLimited("Paths", "ensure-gamestate-missing-context",
                    $"EnsureGameStateDirectory missing context: rootSet={!string.IsNullOrEmpty(root)}, " +
                    $"saveSet={!string.IsNullOrEmpty(saveFolder)}", 5.0);
                return null;
            }

            string dir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "GameState"));
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                ParsekLog.Info("Paths", $"Created game state directory '{dir}'");
            }
            return dir;
        }

        internal static string ResolveGameStateDirectory()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
            {
                ParsekLog.VerboseRateLimited("Paths", "resolve-gamestate-missing-context",
                    $"ResolveGameStateDirectory missing context: rootSet={!string.IsNullOrEmpty(root)}, " +
                    $"saveSet={!string.IsNullOrEmpty(saveFolder)}", 5.0);
                return null;
            }

            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "GameState"));
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
    }
}
