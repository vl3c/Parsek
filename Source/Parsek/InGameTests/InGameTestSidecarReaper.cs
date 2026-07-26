using System.Collections.Generic;
using System.IO;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Shared sidecar reaper for in-game tests that put synthetic recordings through
    /// the REAL recording pipeline.
    ///
    /// <para><b>Why this exists.</b> Every in-store teardown an in-game test has is
    /// memory-only and disk-blind. <c>RecordingStore.RemoveCommittedTreeById</c> drops
    /// a tree from <c>CommittedTrees</c> and its recordings from
    /// <c>CommittedRecordings</c>; <c>RecordingStoreTestSnapshot.Restore</c>
    /// reinstates the pre-test committed list by reference. Neither touches the files.
    /// But the production write paths a test drives DO write files:
    /// <c>RecordingStore.CommitTree</c> flushes <c>SaveRecordingFiles</c> for every
    /// recording in the tree it commits, and <c>RunOptimizationPass</c> flushes the
    /// dirty sidecars for every recording it splits (inventing fresh ids as it goes).
    /// The result is orphan <c>.prec</c> / <c>.pann</c> / <c>.prec.txt</c> files left
    /// in <c>saves/&lt;save&gt;/Parsek/Recordings/</c> after a green test - invisible
    /// in the save's <c>persistent.sfs</c> (which carries no RECORDING node for them)
    /// and permanent, because once those ids are absent from the known-ids set
    /// <c>CleanOrphanFiles</c> keeps them as "maybe a recovery candidate".</para>
    ///
    /// <para><b>Found live 2026-07-26</b> by the M1-mission-loop-unit harness scenario,
    /// whose <c>recordings.count = {0,0}</c> pin red'd on five <c>mmis8-xdock-*</c>
    /// sidecars that <c>CrossTreeDockLoopUnitInGameTest</c>'s
    /// <c>RemoveCommittedTreeById</c> teardown could not reach. Same class as the S0.5
    /// discard-residue leak. Every in-game test that drives a real commit/flush must
    /// call this reaper in its finally.</para>
    ///
    /// <para><b>Known-ids guard.</b> The live entry point
    /// <see cref="DeleteSidecarsForIds"/> filters the candidate ids through
    /// <c>RecordingStore.BuildKnownRecordingIds()</c> and REFUSES to unlink files for
    /// any id the store still knows about (committed recording, committed tree, or
    /// pending tree). Same guard the S0.5 discard reap carries, and for the same
    /// reason: a synthetic id is only safe to reap once nothing in the store owns it,
    /// and an id that IS still owned belongs to real mission data. Skips are counted
    /// and logged, never silent.</para>
    ///
    /// <para><b>Path coverage</b> - every extension <see cref="RecordingPaths"/> exposes
    /// a builder for, plus the legacy <c>.pcrf</c> suffix in case some intermediate
    /// version writes one (defensive - the current pipeline never writes <c>.pcrf</c>):
    /// <list type="bullet">
    ///   <item><description><c>.prec</c> - binary trajectory sidecar.</description></item>
    ///   <item><description><c>.prec.txt</c> - readable trajectory mirror.</description></item>
    ///   <item><description><c>.pann</c> - pipeline annotations sidecar.</description></item>
    ///   <item><description><c>_vessel.craft</c> - vessel snapshot.</description></item>
    ///   <item><description><c>_vessel.craft.txt</c> - readable vessel mirror.</description></item>
    ///   <item><description><c>_ghost.craft</c> - ghost-only snapshot.</description></item>
    ///   <item><description><c>_ghost.craft.txt</c> - readable ghost mirror.</description></item>
    ///   <item><description><c>.pcrf</c> - legacy ghost-geometry sidecar (defensive).</description></item>
    /// </list>
    /// The list mirrors <c>RecordingStore.RecordingFileSuffixes</c> +
    /// <c>RecordingStore.LegacyRecordingFileSuffixes</c>; if a new sidecar suffix is
    /// added to the recording pipeline, <c>RecordingPaths</c> grows a new builder and
    /// this list must grow alongside it.</para>
    /// </summary>
    internal static class InGameTestSidecarReaper
    {
        /// <summary>Default log context when a caller does not name itself.</summary>
        private const string DefaultContext = "InGameTest";

        /// <summary>
        /// Production entry point: deletes sidecars under the active save's recordings
        /// directory for every id in <paramref name="recordingIds"/> that the store no
        /// longer knows about. No-op if the save context is unavailable or the
        /// recordings directory does not exist.
        /// </summary>
        /// <param name="recordingIds">The synthetic ids the test created.</param>
        /// <param name="context">
        /// Short caller name for the log lines (e.g. "CrossTreeDockLoopUnit"). Makes
        /// the reap greppable per test.
        /// </param>
        internal static int DeleteSidecarsForIds(
            IEnumerable<string> recordingIds, string context = DefaultContext)
        {
            string recordingsDir = RecordingStore.ResolveRecordingsDirectoryForCurrentSave();
            return DeleteSidecarsForIdsIn(
                recordingsDir, recordingIds, context, RecordingStore.BuildKnownRecordingIds());
        }

        /// <summary>
        /// Deletes sidecars for <paramref name="recordingIds"/> from
        /// <paramref name="recordingsDir"/>. Each path is rooted under the supplied
        /// directory before unlinking; per-file failures log verbose and are swallowed.
        /// Emits one INFO summary line. Returns the number of files actually deleted.
        /// Exposed for unit tests that supply a temp directory and an explicit
        /// known-ids set.
        /// </summary>
        /// <param name="knownRecordingIds">
        /// Ids the store still owns. Any candidate present here is PRESERVED (the file
        /// is real mission data, not test residue). Null means "the store knows
        /// nothing", which is the correct reading for a directory-scoped unit test.
        /// </param>
        internal static int DeleteSidecarsForIdsIn(
            string recordingsDir,
            IEnumerable<string> recordingIds,
            string context = DefaultContext,
            ICollection<string> knownRecordingIds = null)
        {
            string tag = string.IsNullOrEmpty(context) ? DefaultContext : context;
            if (recordingIds == null)
            {
                ParsekLog.Verbose("TestRunner",
                    $"{tag} sidecar reap: null id collection - skipping");
                return 0;
            }
            if (string.IsNullOrEmpty(recordingsDir))
            {
                ParsekLog.Verbose("TestRunner",
                    $"{tag} sidecar reap: no recordings directory resolved - skipping");
                return 0;
            }
            if (!Directory.Exists(recordingsDir))
            {
                ParsekLog.Verbose("TestRunner",
                    $"{tag} sidecar reap: directory '{recordingsDir}' does not exist - skipping");
                return 0;
            }

            string fullDir;
            try { fullDir = Path.GetFullPath(recordingsDir); }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("TestRunner",
                    $"{tag} sidecar reap: GetFullPath failed for '{recordingsDir}': {ex.Message}");
                return 0;
            }

            int deleted = 0;
            int idCount = 0;
            int preservedKnown = 0;
            int invalidId = 0;
            foreach (var id in recordingIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                if (!RecordingPaths.ValidateRecordingId(id, RecordingIdValidationLogContext.Test))
                {
                    invalidId++;
                    continue;
                }
                // Known-ids guard: the store still owns this recording, so its files are
                // live data, not test residue. Never unlink them.
                if (knownRecordingIds != null && knownRecordingIds.Contains(id))
                {
                    preservedKnown++;
                    ParsekLog.Verbose("TestRunner",
                        $"{tag} sidecar reap: preserving '{id}' - still known to RecordingStore");
                    continue;
                }
                idCount++;
                for (int s = 0; s < SidecarSuffixes.Length; s++)
                {
                    if (TryDeleteIfRooted(fullDir, id + SidecarSuffixes[s], tag))
                        deleted++;
                }
            }

            ParsekLog.Info("TestRunner",
                $"{tag} sidecar reap: deleted {deleted} sidecar file(s) " +
                $"for {idCount} synthetic recording id(s) " +
                $"(preservedKnown={preservedKnown} invalidId={invalidId})");
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
        private static bool TryDeleteIfRooted(string recordingsDirFull, string fileName, string tag)
        {
            try
            {
                string candidate = Path.Combine(recordingsDirFull, fileName);
                string resolved = Path.GetFullPath(candidate);
                // Path-traversal guard - refuse anything outside the recordings dir.
                string dirWithSep = recordingsDirFull;
                if (!dirWithSep.EndsWith(Path.DirectorySeparatorChar.ToString())
                    && !dirWithSep.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                {
                    dirWithSep = dirWithSep + Path.DirectorySeparatorChar;
                }
                if (!resolved.StartsWith(dirWithSep, System.StringComparison.OrdinalIgnoreCase))
                {
                    ParsekLog.Verbose("TestRunner",
                        $"{tag} sidecar reap: refusing delete outside recordings dir " +
                        $"(resolved='{resolved}' dir='{recordingsDirFull}')");
                    return false;
                }
                if (!File.Exists(resolved))
                    return false;
                File.Delete(resolved);
                ParsekLog.Verbose("TestRunner",
                    $"{tag} sidecar reap: deleted {fileName}");
                return true;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Verbose("TestRunner",
                    $"{tag} sidecar reap: failed to delete '{fileName}': {ex.Message}");
                return false;
            }
        }
    }
}
