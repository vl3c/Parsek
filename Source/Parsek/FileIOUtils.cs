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
        /// Suffix of the transient copy the swap fallback parks the previous destination
        /// under while the replacement is moved into place. It only exists between the two
        /// moves of <see cref="ReplaceDestination"/> and is deleted on success.
        /// </summary>
        private const string SwapBackupExtension = ".bak";

        /// <summary>
        /// Writes a ConfigNode to disk using the safe-write pattern: serialize to a sibling
        /// <c>.tmp</c>, verify the temp file really landed, then swap it onto the destination.
        /// Ensures the parent directory exists. Logs and throws on failure.
        ///
        /// <para>
        /// <b>Ordering invariant - the original is recoverable at every step.</b>
        /// <c>ConfigNode.Save</c> reports failure through its <c>bool</c> return and swallows
        /// its own IO exception (disk full, permission denied, destination locked), so that
        /// return value is checked here: a failed serialize deletes the temp file and throws
        /// WITHOUT touching the destination, leaving the caller's previous data intact.
        /// Ignoring it used to be destructive - the old delete-then-move sequence deleted the
        /// destination and then failed to move a temp file that was never written, destroying
        /// the caller's data with nothing to show for it. The swap itself never passes through
        /// a "destination deleted, replacement not yet in place" state either; see
        /// <see cref="ReplaceDestination"/>.
        /// </para>
        /// </summary>
        internal static void SafeWriteConfigNode(ConfigNode node, string path, string tag)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", nameof(path));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp";

            bool saved;
            try
            {
                saved = node.Save(tmpPath);
            }
            catch (Exception ex)
            {
                TryDeleteScratch(tmpPath, tag);
                ParsekLog.Warn(tag,
                    $"SafeWrite: failed to write temp file '{tmpPath}' " +
                    $"(ConfigNode.Save threw {ex.GetType().Name}: {ex.Message}) - " +
                    $"destination '{path}' left untouched");
                throw;
            }

            // Belt and braces: a Save that reports success but wrote nothing is treated as a
            // failure too. An empty node legitimately serializes to a zero-byte file, so the
            // size check only applies when the node actually carries content.
            bool nodeHasContent = node.values.Count > 0 || node.nodes.Count > 0;
            var tmpInfo = new FileInfo(tmpPath);
            if (!saved || !tmpInfo.Exists || (nodeHasContent && tmpInfo.Length == 0))
            {
                string reason = !saved
                    ? "ConfigNode.Save returned false"
                    : (!tmpInfo.Exists ? "temp file missing after save" : "temp file empty after save");
                TryDeleteScratch(tmpPath, tag);
                ParsekLog.Warn(tag,
                    $"SafeWrite: failed to write temp file '{tmpPath}' ({reason}) - " +
                    $"destination '{path}' left untouched");
                throw new IOException(
                    $"Failed to write ConfigNode temp file '{tmpPath}' ({reason}); " +
                    $"'{path}' was left untouched");
            }

            ReplaceDestination(tmpPath, path, tag);
        }

        /// <summary>
        /// Writes raw bytes to disk using the same safe-write pattern as ConfigNode files:
        /// write to a sibling <c>.tmp</c>, then swap it onto the destination. Same ordering
        /// invariant as <see cref="SafeWriteConfigNode"/> - the original is recoverable at
        /// every step, and a failed temp write leaves the destination untouched.
        /// <see cref="File.WriteAllBytes"/> throws rather than returning a status, so its
        /// exception is re-thrown verbatim (callers that inspect the exception type keep the
        /// behaviour they had) after the partial temp file is cleaned up.
        /// </summary>
        internal static void SafeWriteBytes(byte[] data, string path, string tag)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", nameof(path));

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = path + ".tmp";
            try
            {
                File.WriteAllBytes(tmpPath, data ?? Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                TryDeleteScratch(tmpPath, tag);
                ParsekLog.Warn(tag,
                    $"SafeWrite: failed to write temp file '{tmpPath}' " +
                    $"({ex.GetType().Name}: {ex.Message}) - destination '{path}' left untouched");
                throw;
            }

            ReplaceDestination(tmpPath, path, tag);
        }

        /// <summary>
        /// Moves <paramref name="src"/> onto <paramref name="dst"/>, overwriting an existing
        /// destination. Ensures the destination directory exists, then swaps through
        /// <see cref="ReplaceDestination"/> so the destination is never deleted ahead of its
        /// replacement. Logs and re-throws on failure; the caller decides how to recover.
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
                ParsekLog.Verbose(tag,
                    $"SafeMove: overwriting existing destination '{dst}'");
            }

            try
            {
                ReplaceDestination(src, dst, tag);
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
        /// Puts <paramref name="sourcePath"/>'s content at <paramref name="destPath"/>
        /// (consuming the source, as <see cref="File.Move"/> does) without ever leaving the
        /// destination deleted-and-unreplaced.
        ///
        /// <para>
        /// <b>Ordering invariant - the original is recoverable at every step:</b>
        /// </para>
        /// <list type="number">
        /// <item>No destination yet: a bare <see cref="File.Move"/> is already atomic on the
        /// same volume, and there is nothing to lose if it fails.</item>
        /// <item>Destination exists: <see cref="File.Replace(string,string,string)"/> swaps it
        /// in one call - the destination holds either the old or the new content at every
        /// instant, never neither. Mono's implementation is not dependable on every
        /// filesystem, so a throw here is not fatal; it falls through to (3).</item>
        /// <item>Fallback: the original is MOVED ASIDE to a sibling <c>.bak</c> (never
        /// deleted), the replacement is moved into place, and only then is the aside copy
        /// removed. If the second move fails, the aside copy is moved back; if even that
        /// fails, the previous file is still on disk under <c>.bak</c> and the log says so.
        /// At no point is the only copy of the caller's data gone.</item>
        /// </list>
        /// </summary>
        private static void ReplaceDestination(string sourcePath, string destPath, string tag)
        {
            if (!File.Exists(destPath))
            {
                File.Move(sourcePath, destPath);
                return;
            }

            try
            {
                File.Replace(sourcePath, destPath, null);
                return;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(tag,
                    $"SafeReplace: File.Replace('{sourcePath}' -> '{destPath}') unavailable " +
                    $"({ex.GetType().Name}: {ex.Message}); using the move-aside fallback");
            }

            // A leftover .bak here can only be crash residue from an earlier swap, and the
            // destination it was protecting is present (checked above), so it is redundant.
            string bakPath = destPath + SwapBackupExtension;
            TryDeleteScratch(bakPath, tag);

            File.Move(destPath, bakPath);
            try
            {
                File.Move(sourcePath, destPath);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag,
                    $"SafeReplace: failed to move '{sourcePath}' onto '{destPath}' " +
                    $"({ex.GetType().Name}: {ex.Message}) - restoring the original from '{bakPath}'");
                try
                {
                    File.Move(bakPath, destPath);
                }
                catch (Exception restoreEx)
                {
                    ParsekLog.Warn(tag,
                        $"SafeReplace: failed to restore the original '{destPath}' from '{bakPath}' " +
                        $"({restoreEx.GetType().Name}: {restoreEx.Message}) - the previous file is " +
                        $"still on disk as '{bakPath}' and can be renamed back by hand");
                }
                throw;
            }

            TryDeleteScratch(bakPath, tag);
        }

        /// <summary>
        /// Best-effort delete of a scratch file (a <c>.tmp</c> that never made it, or the
        /// transient <c>.bak</c> after a successful swap). Never throws: the caller is either
        /// already unwinding a failure or has already committed the real write.
        /// </summary>
        private static void TryDeleteScratch(string path, string tag)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag,
                    $"Failed to delete scratch file '{path}': {ex.Message}");
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
