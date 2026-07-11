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
        /// </summary>
        internal static bool ShouldSave(bool gameLoaded, bool saveFolderPresent)
            => gameLoaded && saveFolderPresent;

        internal static List<KeyValuePair<string, string>> BuildPayload(bool saved)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("saved", saved ? "true" : "false"),
            };
    }
}
