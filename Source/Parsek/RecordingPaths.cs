using System;
using System.IO;

namespace Parsek
{
    internal static class RecordingPaths
    {
        internal static string BuildGhostGeometryRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}.pcrf");
        }

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

        internal static bool ValidateRecordingId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                ParsekLog.Warn("Paths", "Recording id validation failed: id is null or empty");
                return false;
            }
            if (id.Contains("/") || id.Contains("\\") || id.Contains(".."))
            {
                ParsekLog.Warn("Paths", $"Recording id validation failed for '{id}': contains invalid path sequence");
                return false;
            }
            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int i = 0; i < id.Length; i++)
            {
                if (Array.IndexOf(invalidChars, id[i]) >= 0)
                {
                    ParsekLog.Warn("Paths", $"Recording id validation failed for '{id}': contains invalid file-name char");
                    return false;
                }
            }
            return true;
        }
    }
}
