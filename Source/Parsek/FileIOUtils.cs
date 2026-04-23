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

        /// <summary>
        /// Writes raw bytes to disk using the same safe-write pattern as ConfigNode files:
        /// write to .tmp, then replace the destination atomically.
        /// </summary>
        internal static void SafeWriteBytes(byte[] data, string path, string tag)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp";
            File.WriteAllBytes(tmpPath, data ?? Array.Empty<byte>());

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

        /// <summary>
        /// Atomically moves <paramref name="src"/> to <paramref name="dst"/>. Ensures
        /// the destination directory exists, deletes the destination if present, then
        /// invokes <see cref="File.Move"/> (an atomic rename on the same volume on
        /// Windows and POSIX). Logs and re-throws on failure; the caller decides how
        /// to recover.
        ///
        /// <para>
        /// Used by <see cref="RewindPointAuthor"/> to move the stock KSP save from the
        /// saves root (where <c>GamePersistence.SaveGame</c> writes) to the RP subdir
        /// <c>Parsek/RewindPoints/&lt;rpId&gt;.sfs</c> (design §5.10).
        /// </para>
        /// </summary>
        internal static void SafeMove(string src, string dst, string tag)
        {
            if (string.IsNullOrEmpty(src)) throw new ArgumentException("src is required", nameof(src));
            if (string.IsNullOrEmpty(dst)) throw new ArgumentException("dst is required", nameof(dst));

            string dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
            {
                Directory.CreateDirectory(dstDir);
            }

            if (File.Exists(dst))
            {
                try
                {
                    ParsekLog.Verbose(tag,
                        $"SafeMove: overwriting existing destination '{dst}'");
                    File.Delete(dst);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(tag,
                        $"SafeMove: failed to delete existing destination '{dst}': {ex.Message}");
                    throw;
                }
            }

            try
            {
                File.Move(src, dst);
                ParsekLog.Verbose(tag,
                    $"SafeMove: moved '{src}' -> '{dst}'");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag,
                    $"SafeMove: File.Move('{src}' -> '{dst}') failed: {ex.Message}");
                throw;
            }
        }
    }
}
