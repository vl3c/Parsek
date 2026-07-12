using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision + payload helpers for the two-phase <c>LoadGame</c> boot verb
    /// (P5.7). The Unity side realises the <c>.sfs</c> into a KSP <c>Game</c> via
    /// <c>GamePersistence.LoadGame</c> and hands the primitive shape of that result here:
    /// a null / incompatible game or an out-of-range active-vessel index cannot be
    /// focused, so the verb fails with <c>load-failed</c> rather than sending
    /// <c>FlightDriver.StartAndFocusVessel</c> a bad index (design edge case 27). Kept
    /// pure so the focusability decision + completion payload are xUnit-covered without
    /// a live KSP <c>Game</c>.
    /// </summary>
    /// <summary>
    /// The two-phase LoadGame completion outcome (F2). Distinguishes the success
    /// (a settled FLIGHT scene with a game loaded), the two terminal FAILURES a
    /// bad load can produce, and the still-in-progress case.
    /// </summary>
    internal enum LoadCompletionDecision
    {
        /// <summary>The load has not settled yet: keep polling.</summary>
        StillWaiting,

        /// <summary>Settled FLIGHT scene with a game loaded: terminal OK.</summary>
        CompleteOk,

        /// <summary>The completion budget expired without a settled flight: terminal
        /// ERROR (msg=load-timeout). A never-settling load must not ride the harness
        /// run budget.</summary>
        LoadTimeout,

        /// <summary>The scene settled back at MAINMENU with no flight (a failed load,
        /// e.g. an NRE in FlightDriver.Start on an incompatible save): terminal ERROR
        /// (msg=load-failed-returned-to-menu).</summary>
        LoadFailedMenu,
    }

    internal static class TestCommandLoadGame
    {
        /// <summary>
        /// True when the loaded game can be focused: it exists, is version-COMPATIBLE
        /// (<c>Game.compatible</c>, the gate v0.5.4 <c>TestingTools.LoadSave</c> applied), has
        /// a flight state with a proto-vessel list, and its active-vessel index is in range.
        /// An incompatible game (a save from a mismatched KSP / mod version) must fail with
        /// <c>load-failed</c> rather than being flown into a broken scene.
        /// </summary>
        internal static bool IsLoadedGameFocusable(
            bool gamePresent, bool compatible, bool flightStatePresent, bool protoVesselsPresent,
            int activeVesselIdx, int protoVesselCount)
        {
            if (!gamePresent || !compatible || !flightStatePresent || !protoVesselsPresent)
                return false;
            return activeVesselIdx >= 0 && activeVesselIdx < protoVesselCount;
        }

        /// <summary>
        /// Decide the two-phase LoadGame completion (F2). The addon polls this only at
        /// SETTLED scenes (the pump gates off during LOADING / scene transition / settle),
        /// and the scene-transition flag is raised synchronously when the load is initiated,
        /// so the FIRST settled observation is already the destination scene. That makes a
        /// MAINMENU observation a reliable "the load bounced back to the menu" signal with
        /// no grace period needed.
        ///
        /// Order: a settled FLIGHT scene with a game loaded is the success; a settle-back to
        /// MAINMENU is the fast failure (checked before the budget so it does not have to wait
        /// out the whole load budget); the budget expiry is the catch-all for a load that
        /// never settles anywhere. Any other settled scene keeps waiting until one of those
        /// three fires. Kept pure so every cell is xUnit-covered without a live KSP scene.
        /// </summary>
        internal static LoadCompletionDecision DecideLoadCompletion(
            double elapsedSeconds, TestCommandScene currentScene, bool currentGameNonNull,
            double budgetSeconds)
        {
            if (currentScene == TestCommandScene.Flight && currentGameNonNull)
                return LoadCompletionDecision.CompleteOk;
            if (currentScene == TestCommandScene.MainMenu)
                return LoadCompletionDecision.LoadFailedMenu;
            if (elapsedSeconds >= budgetSeconds)
                return LoadCompletionDecision.LoadTimeout;
            return LoadCompletionDecision.StillWaiting;
        }

        /// <summary>Terminal completion payload once the new scene settles with a game loaded.</summary>
        internal static List<KeyValuePair<string, string>> BuildCompletePayload(string sceneName, string save)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scene", sceneName ?? string.Empty),
                new KeyValuePair<string, string>("save", save ?? string.Empty),
            };
    }
}
