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
    internal static class TestCommandLoadGame
    {
        /// <summary>
        /// True when the loaded game can be focused: it exists, has a flight state with
        /// a proto-vessel list, and its active-vessel index is in range.
        /// </summary>
        internal static bool IsLoadedGameFocusable(
            bool gamePresent, bool flightStatePresent, bool protoVesselsPresent,
            int activeVesselIdx, int protoVesselCount)
        {
            if (!gamePresent || !flightStatePresent || !protoVesselsPresent)
                return false;
            return activeVesselIdx >= 0 && activeVesselIdx < protoVesselCount;
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
