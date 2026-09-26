using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// Suffix of the sibling temp file both safe-write entry points serialize into before the
        /// swap (<c>&lt;path&gt;.tmp</c>). One constant for the producer and both sweepers: the
        /// recordings sweep (<c>RecordingStore.IsTransientSidecarArtifactFile</c>) and
        /// <see cref="SweepStaleSafeWriteTemp"/> for the fixed-path stores.
        /// </summary>
        internal const string SafeWriteTempSuffix = ".tmp";

        /// <summary>
        /// Deletes the stale <c>&lt;path&gt;.tmp</c> that a crash between the temp write and the
        /// swap of <see cref="SafeWriteConfigNode"/> / <see cref="SafeWriteBytes"/> leaves next to
        /// <paramref name="path"/>, when <paramref name="path"/> itself exists. Called from a
        /// store's LOAD path before it reads <paramref name="path"/>; returns true when a file
        /// was deleted.
        ///
        /// <para>
        /// Safe because a safe-write is synchronous on the main thread and Parsek starts no
        /// threads (<c>grep-audit-background-threads.ps1</c>): when a load runs, no write of the
        /// same file can be in flight, so any <c>.tmp</c> on disk is residue from a process that
        /// died mid-write. When <paramref name="path"/> exists, the crash hit before the swap
        /// (or <c>File.Replace</c> swapped atomically), so the real file holds the previous or
        /// the new bytes, the load reads it, and the <c>.tmp</c> is junk.
        /// </para>
        /// <para>
        /// <b>Real file missing: the <c>.tmp</c> is KEPT.</b> The move-aside fallback in
        /// <see cref="ReplaceDestination"/> moves the destination to <c>.bak.&lt;guid&gt;</c>
        /// before moving the temp file into place; a crash between those two renames leaves no
        /// real file, the previous bytes in the <c>.bak.&lt;guid&gt;</c> and the newest complete
        /// bytes only in the <c>.tmp</c>. A first-ever save interrupted mid-write also leaves no
        /// real file, but a PARTIAL <c>.tmp</c>, and the two cannot be told apart, so the
        /// <c>.tmp</c> is neither deleted nor promoted: a Warn names it, its size and any
        /// <c>.bak.*</c> sibling so it can be recovered by hand. The <c>.bak.&lt;guid&gt;</c> is
        /// never touched either. Fail-open: an IO error is logged and swallowed.
        /// </para>
        /// </summary>
        internal static bool SweepStaleSafeWriteTemp(string path, string tag)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            return SweepOneStaleTemp(path + SafeWriteTempSuffix, path, tag) == StaleTempOutcome.Deleted;
        }

        internal enum StaleTempOutcome { Absent, Deleted, KeptRealMissing, Failed }

        private static StaleTempOutcome SweepOneStaleTemp(string tmpPath, string path, string tag)
        {
            try
            {
                if (!File.Exists(tmpPath))
                    return StaleTempOutcome.Absent;
                long bytes = new FileInfo(tmpPath).Length;
                if (!File.Exists(path))
                {
                    ParsekLog.Warn(tag,
                        $"SafeWrite: kept stale temp file '{tmpPath}' ({bytes.ToString(CultureInfo.InvariantCulture)} bytes) " +
                        $"because its real file '{path}' is missing; it may hold the newest save " +
                        "(interrupted swap) or a partial first save, recover by hand if needed; " +
                        $"bakSiblings={DescribeBakSiblings(path)}");
                    return StaleTempOutcome.KeptRealMissing;
                }
                File.Delete(tmpPath);
                ParsekLog.Info(tag,
                    $"SafeWrite: deleted stale temp file '{tmpPath}' ({bytes.ToString(CultureInfo.InvariantCulture)} bytes, " +
                    $"left by an interrupted save); '{Path.GetFileName(path)}' untouched");
                return StaleTempOutcome.Deleted;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag,
                    $"SafeWrite: failed to sweep stale temp file '{tmpPath}' " +
                    $"({ex.GetType().Name}: {ex.Message}); left in place");
                return StaleTempOutcome.Failed;
            }
        }

        /// <summary>Lists the swap fallback's <c>&lt;path&gt;.bak.*</c> siblings for a log line;
        /// never throws.</summary>
        private static string DescribeBakSiblings(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return "none";
                string[] baks = Directory.GetFiles(dir,
                    Path.GetFileName(path) + SwapBackupExtensionPrefix + "*", SearchOption.TopDirectoryOnly);
                if (baks.Length == 0)
                    return "none";
                var names = new string[baks.Length];
                for (int i = 0; i < baks.Length; i++)
                    names[i] = Path.GetFileName(baks[i]);
                Array.Sort(names, StringComparer.Ordinal);
                return string.Join(",", names);
            }
            catch (Exception ex)
            {
                return "unknown(" + ex.GetType().Name + ")";
            }
        }

        /// <summary>
        /// Directory form of <see cref="SweepStaleSafeWriteTemp"/> for stores whose file names
        /// vary (the per-UT <c>baseline_*.pgsb</c>): sweeps every <c>&lt;file&gt;.tmp</c> in
        /// <paramref name="dir"/> whose stem matches <paramref name="fileSearchPattern"/> (a
        /// pattern for the REAL file, e.g. <c>baseline_*.pgsb</c>), applying the same per-file
        /// rule (deleted when the real file exists, kept with a Warn when it is missing).
        /// Non-recursive. Unlike the fixed-path stores, these names never repeat, so without a
        /// sweep the residue would accumulate. Returns the count deleted; logs one summary line.
        /// </summary>
        internal static int SweepStaleSafeWriteTemps(string dir, string fileSearchPattern, string tag)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileSearchPattern))
                return 0;

            string[] candidates;
            try
            {
                if (!Directory.Exists(dir))
                    return 0;
                candidates = Directory.GetFiles(dir, fileSearchPattern + SafeWriteTempSuffix,
                    SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(tag,
                    $"SafeWrite: failed to list stale temp files in '{dir}' " +
                    $"({ex.GetType().Name}: {ex.Message}); left in place");
                return 0;
            }

            int deleted = 0;
            int kept = 0;
            int failed = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                // Windows GetFiles matches a 3-char extension as a prefix ("*.tmp" also
                // returns "x.tmpx"), so re-check the exact suffix before touching anything.
                string tmpPath = candidates[i];
                if (!tmpPath.EndsWith(SafeWriteTempSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string realPath = tmpPath.Substring(0, tmpPath.Length - SafeWriteTempSuffix.Length);
                switch (SweepOneStaleTemp(tmpPath, realPath, tag))
                {
                    case StaleTempOutcome.Deleted: deleted++; break;
                    case StaleTempOutcome.KeptRealMissing: kept++; break;
                    case StaleTempOutcome.Failed: failed++; break;
                }
            }

            if (deleted > 0 || kept > 0 || failed > 0)
                ParsekLog.Info(tag,
                    $"SafeWrite: swept stale temp files in '{dir}' pattern={fileSearchPattern}{SafeWriteTempSuffix} " +
                    $"deleted={deleted.ToString(CultureInfo.InvariantCulture)} " +
                    $"keptRealMissing={kept.ToString(CultureInfo.InvariantCulture)} " +
                    $"failed={failed.ToString(CultureInfo.InvariantCulture)}");
            return deleted;
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

            string tmpPath = path + SafeWriteTempSuffix;
            DestState destBefore = crashAfterTempPattern != null ? ProbeDest(path) : default(DestState);

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

            MaybeInjectCrashAfterTemp(tmpPath, path, destBefore);
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

            string tmpPath = path + SafeWriteTempSuffix;
            DestState destBefore = crashAfterTempPattern != null ? ProbeDest(path) : default(DestState);
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

            MaybeInjectCrashAfterTemp(tmpPath, path, destBefore);
            ReplaceOrDiscardTemp(tmpPath, path, tag);
        }

        // ----- Crash-after-temp fault hook (automation only; D16 `safe-write`) -----

        /// <summary>
        /// The exception the crash-after-temp hook throws. A dedicated type so the one
        /// in-process cleanup that would otherwise erase a real crash's residue
        /// (<c>SidecarFileCommitBatch.StageWrite</c>'s staged-file delete) can let it pass:
        /// a process that dies runs no catch block, so the orphan <c>.tmp</c> must survive
        /// for the next load to sweep. Never thrown unless the seam armed the hook.
        /// </summary>
        internal sealed class SafeWriteInjectedCrashException : IOException
        {
            internal SafeWriteInjectedCrashException(string message) : base(message) { }
        }

        internal struct DestState
        {
            internal bool Exists;
            internal long Length;
            internal long WriteTicks;
        }

        /// <summary>Path substring the armed hook fires on; null = disarmed (every player build).</summary>
        private static string crashAfterTempPattern;

        /// <summary>How many times the hook has fired this process (read by the seam probe).</summary>
        internal static int CrashAfterTempFiredCount { get; private set; }

        internal static bool IsCrashAfterTempArmed => crashAfterTempPattern != null;

        /// <summary>
        /// Arms the one-shot crash-after-temp hook for the next safe-write whose destination
        /// path contains <paramref name="pathPattern"/>. Refused unless
        /// <paramref name="seamArmed"/> is true: the only production caller is the
        /// <c>SafeWriteCrash</c> seam verb, which passes the command addon's own arm flag
        /// (<c>PARSEK_TEST_COMMANDS=1</c>, read once at Awake), so no player build can arm it.
        /// </summary>
        internal static bool ArmCrashAfterTemp(string pathPattern, bool seamArmed)
        {
            if (!seamArmed || string.IsNullOrEmpty(pathPattern))
            {
                ParsekLog.Warn("SafeWrite",
                    $"crash-after-temp arm refused seamArmed={seamArmed} " +
                    $"pattern={(string.IsNullOrEmpty(pathPattern) ? "<empty>" : pathPattern)}");
                return false;
            }
            crashAfterTempPattern = pathPattern;
            ParsekLog.Info("SafeWrite", $"crash-after-temp armed pattern={pathPattern} phase=after-temp");
            return true;
        }

        /// <summary>Disarms the hook and zeroes the fire count (test teardown).</summary>
        internal static void ResetCrashAfterTempForTesting()
        {
            crashAfterTempPattern = null;
            CrashAfterTempFiredCount = 0;
        }

        /// <summary>Pure fire predicate: armed, and the destination path contains the pattern.</summary>
        internal static bool ShouldCrashAfterTemp(string armedPattern, string path)
        {
            return !string.IsNullOrEmpty(armedPattern) && !string.IsNullOrEmpty(path)
                && path.IndexOf(armedPattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Pure: the destination is untouched when its existence, length and write
        /// time all equal what they were when the safe-write started.</summary>
        internal static bool IsDestUntouched(DestState before, DestState now)
        {
            return before.Exists == now.Exists && before.Length == now.Length
                && before.WriteTicks == now.WriteTicks;
        }

        internal static DestState ProbeDest(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new DestState { Exists = true, Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks }
                : default(DestState);
        }

        private static void MaybeInjectCrashAfterTemp(string tmpPath, string path, DestState destBefore)
        {
            string pattern = crashAfterTempPattern;
            if (!ShouldCrashAfterTemp(pattern, path))
                return;

            // One-shot: disarm BEFORE the throw, so the product's own retry / next save
            // writes normally.
            crashAfterTempPattern = null;
            CrashAfterTempFiredCount++;
            DestState destNow = ProbeDest(path);
            long tmpBytes = File.Exists(tmpPath) ? new FileInfo(tmpPath).Length : -1;
            ParsekLog.Info("SafeWrite",
                $"crash-after-temp fired phase=after-temp path='{path}' tmp='{tmpPath}' " +
                $"tmpBytes={tmpBytes} destExists={destNow.Exists} " +
                $"destUntouched={IsDestUntouched(destBefore, destNow)} pattern={pattern}");
            throw new SafeWriteInjectedCrashException(
                $"Injected crash after temp write '{tmpPath}' (pattern '{pattern}'); '{path}' not swapped");
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
