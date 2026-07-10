using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// Shared file I/O utilities for safe-write operations.
    /// </summary>
    internal static class FileIOUtils
    {
        /// <summary>
        /// Extension of the sidecar metadata file that <c>GamePersistence.SaveGame</c>
        /// writes next to every <c>.sfs</c> in the saves root (load-dialog metadata:
        /// UT, funds, science, reputation, thumbnail hash, etc.).
        /// </summary>
        internal const string LoadMetaExtension = ".loadmeta";

        /// <summary>
        /// Deletes the orphaned <c>.loadmeta</c> sidecar that <c>GamePersistence.SaveGame</c>
        /// leaves in the saves root after Parsek moves the matching <c>.sfs</c> into a Parsek
        /// subdirectory (<c>Parsek/Saves</c>, <c>Parsek/RewindPoints</c>). KSP's
        /// <c>SaveGame</c> always writes the <c>.sfs</c> + <c>.loadmeta</c> pair to the root;
        /// Parsek's quicksaves are loaded programmatically (the <c>.sfs</c> is copied back to
        /// the root first), so the root <c>.loadmeta</c> serves no purpose and only litters the
        /// save folder and the stock load dialog. Best-effort: a missing sidecar is a no-op and
        /// a delete failure is logged and swallowed (the orphan is harmless).
        /// </summary>
        /// <param name="savesDir">Absolute path to the save folder where SaveGame wrote.</param>
        /// <param name="saveBaseName">The save base name passed to SaveGame (no extension).</param>
        /// <param name="tag">Subsystem tag for log lines.</param>
        internal static void DeleteSaveSidecarLoadMeta(string savesDir, string saveBaseName, string tag)
        {
            if (string.IsNullOrEmpty(savesDir) || string.IsNullOrEmpty(saveBaseName))
                return;

            string loadMetaPath = Path.Combine(savesDir, saveBaseName + LoadMetaExtension);
            try
            {
                if (File.Exists(loadMetaPath))
                {
                    File.Delete(loadMetaPath);
                    ParsekLog.Verbose(tag,
                        $"Deleted orphaned save sidecar '{saveBaseName}{LoadMetaExtension}'");
                }
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag,
                    $"Failed to delete orphaned save sidecar '{saveBaseName}{LoadMetaExtension}': {ex.Message}");
            }
        }

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

        /// <summary>
        /// Recursively copies the directory tree at <paramref name="src"/> into
        /// <paramref name="dst"/>, returning the number of files and total bytes copied.
        /// Top-level subdirectories whose name is in <paramref name="excludeTopLevelDirs"/>
        /// (case-insensitive; pass a <see cref="StringComparer.OrdinalIgnoreCase"/> set) are
        /// skipped; the exclusion applies only at the first level. A non-existent source is a
        /// no-op success. Unlike <see cref="SafeMove"/>/<see cref="SafeWriteConfigNode"/> (which
        /// re-throw), this returns <c>false</c> on any failure so the caller can stage-and-abort;
        /// per-file logging is intentionally omitted (batch-counting convention) - the caller
        /// logs the aggregate <c>files=N bytes=M</c> summary.
        /// </summary>
        internal static bool CopyDirectory(
            string src, string dst, ISet<string> excludeTopLevelDirs, string tag,
            out int filesCopied, out long bytesCopied)
        {
            filesCopied = 0;
            bytesCopied = 0;
            try
            {
                var di = new DirectoryInfo(src);
                if (!di.Exists)
                {
                    ParsekLog.Verbose(tag, $"CopyDirectory: source '{src}' does not exist — skipped");
                    return true;
                }

                Directory.CreateDirectory(dst);

                foreach (FileInfo f in di.GetFiles())
                {
                    f.CopyTo(Path.Combine(dst, f.Name), false);
                    filesCopied++;
                    bytesCopied += f.Length;
                }

                foreach (DirectoryInfo sub in di.GetDirectories())
                {
                    if (excludeTopLevelDirs != null && excludeTopLevelDirs.Contains(sub.Name))
                        continue;
                    // Exclusion applies only at the top level: recurse with a null exclude set.
                    if (!CopyDirectory(sub.FullName, Path.Combine(dst, sub.Name), null, tag,
                            out int f2, out long b2))
                        return false;
                    filesCopied += f2;
                    bytesCopied += b2;
                }

                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag,
                    $"CopyDirectory('{src}' -> '{dst}') failed: {ex.GetType().Name}:{ex.Message}");
                return false;
            }
        }
    }
}
