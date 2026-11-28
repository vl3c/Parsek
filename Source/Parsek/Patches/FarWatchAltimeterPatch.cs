using System.Globalization;
using HarmonyLib;
using KSP.UI.Screens;

namespace Parsek
{
    /// <summary>
    /// The altimeter's Space Center (and Recover) button during a far watch (design-far-watch.md
    /// 9.3, 12.2): greyed through stock's own lock state, and blocked on the click by the same
    /// predicate (D1). Stock (decompiled 1.12.5) unlocks the buttons only through the private
    /// <c>AltimeterSliderButtons.setUnlock(int)</c> (0 locks both and lights the LED yellow, 1
    /// unlocks Space Center, 2 also Recover), called from its per-frame unlock coroutines while
    /// <c>state</c> differs; its click handlers <c>returnToSpaceCenter</c> and <c>recoverVessel</c>
    /// save and leave the flight when the active vessel (the drone) is clear to save. While a
    /// hand-back can still come every unlock is held at 0; once the watch ends, or waits in
    /// Aborted for a scene exit (7.6), stock's coroutine sees its state differ and unlocks the
    /// button again on the next frame.
    /// </summary>
    [HarmonyPatch(typeof(AltimeterSliderButtons), "setUnlock")]
    internal static class FarWatchAltimeterUnlockPatch
    {
        static void Prefix(ref int unlockState)
        {
            if (ParsekGameModeGate.CheckInert("FarWatchAltimeterUnlockPatch.Prefix")) return; // S9 game-mode gate
            int requested = unlockState;
            unlockState = FarWatchAltimeterBlock.GreyedUnlockState(requested, FarWatchAltimeterBlock.HoldsTheFlightExits());
            if (unlockState != requested)
                ParsekLog.VerboseRateLimited("FarWatch", "altimeter-unlock-held",
                    "altimeter buttons held locked during the far watch requested="
                    + requested.ToString(CultureInfo.InvariantCulture), 5.0);
        }
    }

    [HarmonyPatch(typeof(AltimeterSliderButtons), "returnToSpaceCenter")]
    internal static class FarWatchAltimeterSpaceCenterPatch
    {
        static bool Prefix()
        {
            if (ParsekGameModeGate.CheckInert("FarWatchAltimeterSpaceCenterPatch.Prefix")) return true; // S9 game-mode gate
            return FarWatchAltimeterBlock.RunStockClick("returnToSpaceCenter", FarWatchAltimeterBlock.HoldsTheFlightExits());
        }
    }

    [HarmonyPatch(typeof(AltimeterSliderButtons), "recoverVessel")]
    internal static class FarWatchAltimeterRecoverPatch
    {
        static bool Prefix()
        {
            if (ParsekGameModeGate.CheckInert("FarWatchAltimeterRecoverPatch.Prefix")) return true; // S9 game-mode gate
            return FarWatchAltimeterBlock.RunStockClick("recoverVessel", FarWatchAltimeterBlock.HoldsTheFlightExits());
        }
    }

    /// <summary>The one predicate the altimeter's greyed state and its click-block read.</summary>
    internal static class FarWatchAltimeterBlock
    {
        /// <summary>Pure: the unlock state stock may set; 0 (both locked) while the far watch holds the exits.</summary>
        internal static int GreyedUnlockState(int requested, bool holdsExits)
        {
            return holdsExits ? 0 : requested;
        }

        /// <summary>Whether stock's click handler runs; refused (and logged) while the far watch holds the exits.</summary>
        internal static bool RunStockClick(string handler, bool holdsExits)
        {
            if (!holdsExits)
                return true;
            ParsekLog.InfoRateLimited("FarWatch", "altimeter-click-refused-" + handler,
                "altimeter " + handler + " refused during the far watch (stop watching first)", 5.0);
            return false;
        }

        internal static bool HoldsTheFlightExits()
        {
            ParsekFlight f = ParsekFlight.Instance;
            return f != null && f.FarWatch != null && f.FarWatch.HoldsTheFlightExits;
        }
    }
}
