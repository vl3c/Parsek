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
