using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Marks the flag a kerbal is picking up for the duration of stock <c>FlagSite.PickUp</c>
    /// (<c>TakeDown</c> calls it too), so the <c>onVesselWillDestroy</c> it fires from inside
    /// (<c>vessel.Die()</c>) is told apart from a crash or a recovery (<see cref="FlagPickUp"/>).
    /// Observation only: the stock body always runs.
    /// </summary>
    [HarmonyPatch(typeof(FlagSite), nameof(FlagSite.PickUp))]
    internal static class FlagPickUpCapturePatch
    {
        static void Prefix(FlagSite __instance)
        {
            if (ParsekGameModeGate.CheckInert("FlagPickUpCapturePatch.Prefix")) return;
            Vessel flag = __instance != null ? __instance.vessel : null;
            Vessel picker = FlightGlobals.ActiveVessel;
            if (flag == null || picker == null || !picker.isEVA)
                return;
            FlagPickUp.PendingFlagPid = flag.persistentId;
            FlagPickUp.PendingPickerPid = picker.persistentId;
            ParsekLog.Verbose("FlagReplay",
                $"Flag pick-up started: flag '{flag.vesselName}' pid={flag.persistentId} " +
                $"by '{picker.vesselName}' pid={picker.persistentId}");
        }

        static void Finalizer()
        {
            FlagPickUp.PendingFlagPid = 0u;
            FlagPickUp.PendingPickerPid = 0u;
        }
    }
}
