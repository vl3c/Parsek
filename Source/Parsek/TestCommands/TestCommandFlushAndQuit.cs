using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision + payload for the <c>FlushAndQuit</c> verb (P5.8). If a game is
    /// loaded the addon forces a "persistent"-slot save so committed data is durable,
    /// THEN quits deferred one frame (the quit is scheduled only after the response +
    /// journal DONE are flushed). It deliberately does NOT auto-commit an in-flight
    /// uncommitted recorder (a bare quit from flight never persisted one); the
    /// orchestrator sends <c>CommitTree</c> first to keep it. Kept pure so the
    /// should-save gate + payload shape are xUnit-covered without Unity.
    /// </summary>
    internal static class TestCommandFlushAndQuit
    {
        /// <summary>
        /// True when a game save should be forced: a game is loaded AND a save folder is
        /// resolved. With no game loaded (menu quit) there is nothing to save.
        ///
        /// <para><paramref name="suppressAfterBatchRestore"/> (from
        /// <see cref="ShouldSuppressSaveAfterBatchRestore"/>) suppresses the save outright.
        /// An in-game test batch reverts the campaign's persistent.sfs to its batch-start
        /// bytes at teardown; while that revert is still the intended FINAL state of the
        /// file, saving the LIVE game over it would write back whatever in-memory state the
        /// batch left behind. A NON-batch FlushAndQuit still saves normally.</para>
        /// </summary>
        internal static bool ShouldSave(
            bool gameLoaded, bool saveFolderPresent, bool suppressAfterBatchRestore = false)
            => gameLoaded && saveFolderPresent && !suppressAfterBatchRestore;

        /// <summary>
        /// Whether a batch-baseline restore still owns the file this flush would write.
        /// BOTH halves must hold:
        /// <list type="number">
        ///   <item><description>the latch is armed - a batch restore committed and nothing
        ///   state-mutating has run since (the caller clears it on a game load, a main-menu
        ///   transition, or any mutating seam verb);</description></item>
        ///   <item><description>the restore targeted the SAME save folder this flush would
        ///   write. A restore of campaign A must never suppress a flush into campaign B -
        ///   nothing restored B, so suppressing there would simply lose its save.</description></item>
        /// </list>
        /// A null / empty folder on either side fails the comparison, so an unresolvable
        /// context never suppresses (fail-safe: save as before). Ordinal comparison - KSP
        /// save folders are path segments, and a case-fold would be a different, wrong
        /// equality on a case-sensitive filesystem.
        /// </summary>
        internal static bool ShouldSuppressSaveAfterBatchRestore(
            bool batchBaselineRestoreLatched, string restoredSaveFolder, string currentSaveFolder)
        {
            if (!batchBaselineRestoreLatched)
                return false;
            if (string.IsNullOrEmpty(restoredSaveFolder) || string.IsNullOrEmpty(currentSaveFolder))
                return false;
            return string.Equals(
                restoredSaveFolder, currentSaveFolder, System.StringComparison.Ordinal);
        }

        internal static List<KeyValuePair<string, string>> BuildPayload(bool saved)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("saved", saved ? "true" : "false"),
            };
    }
}
