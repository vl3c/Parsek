using System;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// Shared file I/O utilities for safe-write operations.
    /// </summary>
    internal static class FileIOUtils
    {
        /// <summary>
        /// Writes a ConfigNode to disk using the safe-write pattern: write to .tmp, then
        /// atomic rename. Ensures parent directory exists. Logs and re-throws on failure.
        /// </summary>
        internal static void SafeWriteConfigNode(ConfigNode node, string path, string tag)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp";
            node.Save(tmpPath);

            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(tag, $"Failed to delete existing file '{path}': {ex.Message}");
                    throw;
                }
            }

            try
            {
                File.Move(tmpPath, path);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag, $"Failed to move temp file '{tmpPath}' to '{path}': {ex.Message}");
                throw;
            }
        }
    }
}
