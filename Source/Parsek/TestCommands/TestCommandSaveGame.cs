using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision + payload for the <c>SaveGame</c> verb (M-C1.1 follow-up,
    /// design-autotest-seam-verbs-c1.md; the M-B3 L2/R6 dependency). An in-process
    /// <c>GamePersistence.SaveGame</c> of the CURRENT live game state to the run save, so a
    /// scenario can persist-then-reload within ONE launch (the R6 facility-refund window:
    /// upgrade -&gt; SaveGame -&gt; LoadGame -&gt; assert). Sync verb (SaveGame is fast); the
    /// addon body samples live KSP and calls the real SaveGame, mirroring
    /// <c>FlushAndQuitImpl</c>'s save call shape minus the quit. Kept pure so the name-default
    /// + can-save gate + payload shape are xUnit-covered without Unity.
    /// </summary>
    internal static class TestCommandSaveGame
    {
        /// <summary>The default save slot when the <c>name</c> arg is absent.</summary>
        internal const string DefaultName = "persistent";

        /// <summary>Resolve the target save name: the percent-decoded <c>name</c> arg, or
        /// <see cref="DefaultName"/> ("persistent") when absent / empty.</summary>
        internal static string ResolveName(string nameArg)
            => string.IsNullOrEmpty(nameArg) ? DefaultName : nameArg;

        /// <summary>
        /// True when a save may be attempted: a game is loaded AND a save folder is resolved.
        /// With no game (e.g. MAINMENU, before any LoadGame) there is nothing to persist, so
        /// the verb refuses <c>ERROR msg=no-game</c> rather than write an empty save.
        /// </summary>
        internal static bool CanSave(bool gameLoaded, bool saveFolderPresent)
            => gameLoaded && saveFolderPresent;

        /// <summary>The <c>OK saved=&lt;name&gt;</c> payload naming the slot that was written.</summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(string name)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("saved", name ?? string.Empty),
            };
    }
}
