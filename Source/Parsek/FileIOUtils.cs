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
        /// Suffix PREFIX of the transient copy the swap fallback parks the previous destination
        /// under while the replacement is moved into place; a fresh GUID is appended so the name
        /// can never collide with a real file a user or another mod owns (a bare
        /// <c>persistent.sfs.bak</c> would). Same convention as
        /// <see cref="SidecarFileCommitBatch"/>, which also makes crash residue sweepable:
        /// <c>RecordingStore.OrphanCleanup.IsTransientSidecarArtifactFile</c> matches
        /// <c>&lt;suffix&gt;.bak.</c> with the trailing dot. The copy only exists between the two
        /// moves of <see cref="ReplaceDestination"/> and is deleted on success.
        /// </summary>
        private const string SwapBackupExtensionPrefix = ".bak.";

        /// <summary>
        /// TEST SEAM ONLY - always <c>false</c> in the game. When set, <see cref="ReplaceDestination"/>
        /// skips <see cref="File.Replace(string,string,string,bool)"/> and takes the move-aside
        /// fallback, which is otherwise unreachable from a unit test: on a healthy local filesystem
        /// <c>File.Replace</c> always succeeds, so the fallback's success / restore paths would
        /// carry no coverage at all. Tests must reset it in their Dispose. It is fenced only by
        /// the <c>Sequential</c> xUnit collection, not by any lock; a concurrent reader outside
        /// that collection would nonetheless be benign, since the fallback is result-equivalent
        /// to <c>File.Replace</c> - it just reaches the same end state by a different route.
        /// </summary>
        internal static bool ForceMoveAsideFallbackForTesting;

        /// <summary>
        /// Writes a ConfigNode to disk using the safe-write pattern: serialize to a sibling
        /// <c>.tmp</c>, verify the temp file really landed, then swap it onto the destination.
        /// Ensures the parent directory exists. Logs and throws on failure.
        ///
        /// <para>
        /// <b>Ordering invariant - the previous file is never the thing that gets lost.</b>
        /// <c>ConfigNode.Save</c> reports failure through its <c>bool</c> return and swallows
        /// its own IO exception (disk full, permission denied, destination locked), so that
        /// return value is checked here: a failed serialize deletes the temp file and throws
        /// WITHOUT touching the destination, leaving the caller's previous data intact.
        /// Ignoring it used to be destructive - the old delete-then-move sequence deleted the
        /// destination and then failed to move a temp file that was never written, destroying
        /// the caller's data with nothing to show for it. The swap itself is atomic on the
        /// <c>File.Replace</c> path and narrowed to a rename-pair (with the previous file kept
        /// under a named <c>.bak.&lt;guid&gt;</c>) on the fallback; see
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

            ReplaceOrDiscardTemp(tmpPath, path, tag);
        }

        /// <summary>
        /// Writes raw bytes to disk using the same safe-write pattern as ConfigNode files:
        /// write to a sibling <c>.tmp</c>, then swap it onto the destination. Same ordering
        /// invariant as <see cref="SafeWriteConfigNode"/> - a failed temp write leaves the
        /// destination untouched, and the swap never loses the previous bytes.
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

            ReplaceOrDiscardTemp(tmpPath, path, tag);
        }

        /// <summary>
        /// Swaps a written temp file onto its destination for the two safe-write entry points.
        /// A failed swap leaves the previous file recoverable (see
        /// <see cref="ReplaceDestination"/>'s ordering notes - in the double-failure corner that
        /// means the previous bytes sit at the logged <c>.bak.&lt;guid&gt;</c> rather than at the
        /// destination), so the temp file is discarded rather than orphaned next to the real
        /// file, and the failure is logged before it re-throws.
        /// </summary>
        private static void ReplaceOrDiscardTemp(string tmpPath, string path, string tag)
        {
            try
            {
                ReplaceDestination(tmpPath, path, tag);
            }
            catch (Exception ex)
            {
                TryDeleteScratch(tmpPath, tag);
                ParsekLog.Warn(tag,
                    $"SafeWrite: failed to replace '{path}' with temp file '{tmpPath}' " +
                    $"({ex.GetType().Name}: {ex.Message}); the temp file was discarded");
                throw;
            }
        }

        /// <summary>
        /// Moves <paramref name="src"/> onto <paramref name="dst"/>, overwriting an existing
        /// destination. Ensures the destination directory exists, then swaps through
        /// <see cref="ReplaceDestination"/> so an existing destination is replaced rather than
        /// deleted-then-rewritten. Logs and re-throws on failure; the caller decides how to
        /// recover.
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
                    $"SafeMove: replace('{src}' -> '{dst}') failed: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Puts <paramref name="sourcePath"/>'s content at <paramref name="destPath"/>
        /// (consuming the source, as <see cref="File.Move"/> does) without the destination ever
        /// becoming unrecoverable.
        ///
        /// <para>
        /// <b>Ordering invariant - the previous file is never the thing that gets lost:</b>
        /// </para>
        /// <list type="number">
        /// <item>Missing source: throws before anything at all is touched. A caller handing us
        /// a source that was never produced is an input error, not a reason to disturb the
        /// destination.</item>
        /// <item>No destination yet: a bare <see cref="File.Move"/> is atomic on the same
        /// volume, and there is nothing to lose if it fails. It never overwrites on .NET
        /// Framework, so if something else wins the race for that name in the TOCTOU window
        /// the move throws instead of clobbering it - loud, which is the safe outcome.</item>
        /// <item>Destination exists: <see cref="File.Replace(string,string,string,bool)"/> swaps
        /// it in one call - the destination holds either the old or the new content at every
        /// instant, never neither. <c>ignoreMetadataErrors: true</c> matches
        /// <see cref="SidecarFileCommitBatch"/>: the strict form fails on ACL / metadata
        /// mismatches (cloud-synced folders, non-NTFS volumes) and would push routine writes
        /// onto the weaker fallback. Mono's implementation is not dependable on every
        /// filesystem, so a throw here is not fatal; it falls through to (4).</item>
        /// <item>Fallback: the original is MOVED ASIDE to a uniquely-named sibling
        /// <c>.bak.&lt;guid&gt;</c> (never deleted, never a name anything else could own), the
        /// replacement is moved into place, and only then is the aside copy removed. If the
        /// second move fails, the aside copy is moved back; if even that fails, the previous
        /// file is still on disk under its <c>.bak.&lt;guid&gt;</c> name and the log says so.
        /// This path is NOT atomic - between the two renames the destination is briefly absent,
        /// and nothing restores the aside copy on the next load - but the window is a
        /// rename-pair rather than a write, and the previous bytes always survive somewhere
        /// named in the log.</item>
        /// </list>
        /// </summary>
        private static void ReplaceDestination(string sourcePath, string destPath, string tag)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Safe-write source '{sourcePath}' does not exist; " +
                    $"'{destPath}' was left untouched", sourcePath);
            }

            if (!File.Exists(destPath))
            {
                // Nothing to preserve. File.Move never overwrites on .NET Framework, so a
                // racing creation in this window throws rather than being clobbered.
                File.Move(sourcePath, destPath);
                return;
            }

            if (!ForceMoveAsideFallbackForTesting)
            {
                try
                {
                    File.Replace(sourcePath, destPath, null, true);
                    return;
                }
                catch (Exception ex)
                {
                    // ReplaceFile can fail AFTER the replacement has already landed
                    // (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2). If the source is gone and the
                    // destination is there, the new content IS in place and rolling back
                    // through the fallback would be the destructive act.
                    if (!File.Exists(sourcePath) && File.Exists(destPath))
                    {
                        ParsekLog.Warn(tag,
                            $"SafeReplace: File.Replace('{sourcePath}' -> '{destPath}') reported " +
                            $"{ex.GetType().Name}: {ex.Message}, but the replacement is already " +
                            "in place - treating as success");
                        return;
                    }

                    ParsekLog.Verbose(tag,
                        $"SafeReplace: File.Replace('{sourcePath}' -> '{destPath}') unavailable " +
                        $"({ex.GetType().Name}: {ex.Message}); using the move-aside fallback");
                }
            }

            // GUID-suffixed so this can never be a file a user or another mod owns (a bare
            // "<name>.bak" would be), and so no pre-delete is needed to claim the name.
            string bakPath = destPath + SwapBackupExtensionPrefix + Guid.NewGuid().ToString("N");

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
