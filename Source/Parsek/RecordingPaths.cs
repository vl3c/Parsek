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
                return null;
            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, relativePath));
        }

        internal static string EnsureRecordingsDirectory()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                return null;

            string dir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "Recordings"));
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        internal static string BuildMilestonesRelativePath()
        {
            return Path.Combine("Parsek", "GameState", "milestones.pgsm");
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
                return null;

            string dir = Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "GameState"));
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        internal static string ResolveGameStateDirectory()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                return null;

            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "GameState"));
        }

        internal static bool ValidateRecordingId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;
            if (id.Contains("/") || id.Contains("\\") || id.Contains(".."))
                return false;
            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int i = 0; i < id.Length; i++)
            {
                if (Array.IndexOf(invalidChars, id[i]) >= 0)
                    return false;
            }
            return true;
        }
    }
}
