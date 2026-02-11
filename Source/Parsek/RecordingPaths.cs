using System.IO;

namespace Parsek
{
    internal static class RecordingPaths
    {
        internal static string BuildGhostGeometryRelativePath(string recordingId)
        {
            return Path.Combine("Parsek", "Recordings", $"{recordingId}.pcrf");
        }

        internal static string ResolveSaveScopedPath(string relativePath)
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder) || string.IsNullOrEmpty(relativePath))
                return null;
            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, relativePath));
        }
    }
}
