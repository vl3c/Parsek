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
        /// <para><paramref name="batchBaselineAlreadyRestored"/> suppresses the save
        /// outright. An in-game test batch reverts the campaign's persistent.sfs to its
        /// batch-start bytes at teardown; that revert is the intended FINAL state of the
        /// save. Saving the LIVE game over it afterwards writes back whatever in-memory
        /// state the batch left behind - which is how a batch run ended up persisting a
        /// scenario state the teardown had just undone. A NON-batch FlushAndQuit (nothing
        /// restored this process) still saves normally.</para>
        /// </summary>
        internal static bool ShouldSave(
            bool gameLoaded, bool saveFolderPresent, bool batchBaselineAlreadyRestored = false)
            => gameLoaded && saveFolderPresent && !batchBaselineAlreadyRestored;

        internal static List<KeyValuePair<string, string>> BuildPayload(bool saved)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("saved", saved ? "true" : "false"),
            };
    }
}
