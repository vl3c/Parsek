using System.Collections.Generic;
using System.IO;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Test-only cleanup helper for <see cref="PersistenceSplitOptimizerTest"/>.
    ///
    /// <para><b>Why this exists.</b> The in-game persistence-split test injects a synthetic
    /// recording into <see cref="RecordingStore"/> and then calls
    /// <c>RunOptimizationPass()</c>. The optimizer mutates <c>FilesDirty</c> on the
    /// recordings it touches and immediately flushes the dirty sidecars to disk via
    /// <c>SaveRecordingFiles</c> (and the readable-mirror variants). It also invents
    /// fresh recording IDs for every split half it produces from the synthetic input.
    /// <see cref="RecordingStoreTestSnapshot.Restore"/> reverts the in-memory committed
    /// list, but is reference-shallow and disk-blind — the freshly-flushed sidecar files
    /// stay on disk forever. After Restore those IDs are no longer in any known-IDs set
    /// the next OnLoad's orphan sweep would consult, so <c>CleanOrphanFiles</c>
    /// conservatively keeps them around as "maybe a recovery candidate".</para>
    ///
    /// <para><b>What this does.</b> Given the set of recording IDs that the test created
    /// (the explicit synthetic IDs plus whatever IDs the optimizer's split pass invented),
    /// deletes every sidecar file the recording pipeline could have written for those
    /// IDs from the live save's <c>Parsek/Recordings/</c> directory. Each delete is
    /// per-file try/catch (file might be locked, already gone, etc.) — failures log a
    /// verbose line but never throw. A single INFO summary is emitted at the end.</para>
    ///
    /// <para><b>Path coverage</b> — every extension <see cref="RecordingPaths"/> exposes
    /// a builder for, plus the legacy <c>.pcrf</c> suffix in case some intermediate
    /// version writes one (defensive — the current pipeline never writes <c>.pcrf</c>):
    /// <list type="bullet">
    ///   <item><description><c>.prec</c> — binary trajectory sidecar.</description></item>
    ///   <item><description><c>.prec.txt</c> — readable trajectory mirror.</description></item>
    ///   <item><description><c>.pann</c> — pipeline annotations sidecar.</description></item>
    ///   <item><description><c>_vessel.craft</c> — vessel snapshot.</description></item>
    ///   <item><description><c>_vessel.craft.txt</c> — readable vessel mirror.</description></item>
    ///   <item><description><c>_ghost.craft</c> — ghost-only snapshot.</description></item>
    ///   <item><description><c>_ghost.craft.txt</c> — readable ghost mirror.</description></item>
    ///   <item><description><c>.pcrf</c> — legacy ghost-geometry sidecar (defensive).</description></item>
    /// </list>
    /// The list mirrors <c>RecordingStore.RecordingFileSuffixes</c> +
    /// <c>RecordingStore.LegacyRecordingFileSuffixes</c>; if a new sidecar suffix is
    /// added to the recording pipeline, <c>RecordingPaths</c> grows a new builder and
    /// this list must grow alongside it.</para>
    /// </summary>
    internal static class PersistenceSplitOptimizerTestCleanup
    {
        /// <summary>
        /// Production entry point: deletes sidecars under the active save's recordings
        /// directory for every id in <paramref name="recordingIds"/>. No-op if the save
        /// context is unavailable or the recordings directory does not exist.
        /// </summary>
        internal static int DeleteSidecarsForIds(IEnumerable<string> recordingIds)
        {
            string recordingsDir = RecordingStore.ResolveRecordingsDirectoryForCurrentSave();
            return DeleteSidecarsForIdsIn(recordingsDir, recordingIds);
        }

        /// <summary>
        /// Deletes sidecars for <paramref name="recordingIds"/> from
        /// <paramref name="recordingsDir"/>. Each path is rooted under the supplied
        /// directory before unlinking; per-file failures log verbose and are swallowed.
        /// Emits one INFO summary line. Returns the number of files actually deleted.
        /// Exposed for unit tests that supply a temp directory.
        /// </summary>
        internal static int DeleteSidecarsForIdsIn(string recordingsDir, IEnumerable<string> recordingIds)
        {
            if (recordingIds == null)
            {
                ParsekLog.Verbose("TestRunner",
                    "PersistenceSplitOptimizerTest cleanup: null id collection — skipping");
                return 0;
            }
            if (string.IsNullOrEmpty(recordingsDir))
            {
                ParsekLog.Verbose("TestRunner",
                    "PersistenceSplitOptimizerTest cleanup: no recordings directory resolved — skipping");
                return 0;
            }
            if (!Directory.Exists(recordingsDir))
            {
                ParsekLog.Verbose("TestRunner",
                    $"PersistenceSplitOptimizerTest cleanup: directory '{recordingsDir}' does not exist — skipping");
                return 0;
            }

            string fullDir;
            try { fullDir = Path.GetFullPath(recordingsDir); }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("TestRunner",
                    $"PersistenceSplitOptimizerTest cleanup: GetFullPath failed for '{recordingsDir}': {ex.Message}");
                return 0;
            }

            int deleted = 0;
            int idCount = 0;
            foreach (var id in recordingIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                if (!RecordingPaths.ValidateRecordingId(id, RecordingIdValidationLogContext.Test))
                    continue;
                idCount++;
                for (int s = 0; s < SidecarSuffixes.Length; s++)
                {
                    if (TryDeleteIfRooted(fullDir, id + SidecarSuffixes[s]))
                        deleted++;
                }
            }

            ParsekLog.Info("TestRunner",
                $"PersistenceSplitOptimizerTest cleanup: deleted {deleted} sidecar file(s) " +
                $"for {idCount} synthetic recording id(s)");
            return deleted;
        }

        /// <summary>
        /// Every sidecar suffix the recording pipeline writes (mirroring
        /// <c>RecordingStore.RecordingFileSuffixes</c>) plus the legacy <c>.pcrf</c>
        /// suffix. Any new suffix added to the production write/delete paths must be
        /// added here too. See <see cref="RecordingPaths"/> for the canonical builders.
        /// </summary>
        private static readonly string[] SidecarSuffixes =
        {
            ".prec",
            ".prec.txt",
            ".pann",
            "_vessel.craft",
            "_vessel.craft.txt",
            "_ghost.craft",
            "_ghost.craft.txt",
            ".pcrf",
        };

        /// <summary>
        /// Defensive delete: confirms the resolved absolute path is rooted under the
        /// supplied recordings directory before <c>File.Delete</c>. Per-file try/catch;
        /// failures log a verbose line and return false rather than throwing.
        /// </summary>
        private static bool TryDeleteIfRooted(string recordingsDirFull, string fileName)
        {
            try
            {
                string candidate = Path.Combine(recordingsDirFull, fileName);
                string resolved = Path.GetFullPath(candidate);
                // Path-traversal guard — refuse anything outside the recordings dir.
                string dirWithSep = recordingsDirFull;
                if (!dirWithSep.EndsWith(Path.DirectorySeparatorChar.ToString())
                    && !dirWithSep.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                {
                    dirWithSep = dirWithSep + Path.DirectorySeparatorChar;
                }
                if (!resolved.StartsWith(dirWithSep, System.StringComparison.OrdinalIgnoreCase))
                {
                    ParsekLog.Verbose("TestRunner",
                        $"PersistenceSplitOptimizerTest cleanup: refusing delete outside recordings dir " +
                        $"(resolved='{resolved}' dir='{recordingsDirFull}')");
                    return false;
                }
                if (!File.Exists(resolved))
                    return false;
                File.Delete(resolved);
                ParsekLog.Verbose("TestRunner",
                    $"PersistenceSplitOptimizerTest cleanup: deleted {fileName}");
                return true;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("TestRunner",
                    $"PersistenceSplitOptimizerTest cleanup: failed to delete '{fileName}': {ex.Message}");
                return false;
            }
        }
    }
}
