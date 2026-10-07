using System;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix + finalizer on the private <c>SpaceCenterBuilding.DowngradeFacility(bool)</c>
    /// (FACILITY-DOWNGRADE-DEBIT-NOT-LEDGERED). Stock reaches it from the KSC facility menu's
    /// "Rebuild lvl N" button, which <c>KSCFacilityContextMenu</c> shows when the menu is opened
    /// with Left Ctrl held on an out-of-service facility above level 1 outside Mission mode; the
    /// building's dismiss handler passes <c>Funding.Instance != null</c>. The method debits
    /// <c>GetDowngradeCost()</c> (StructureConstruction), resets the structures for free and
    /// calls <c>SetLevel(level - 1)</c>, whose FacilityDowngraded event the recorder books as a
    /// level-change row carrying the debit (see <see cref="FacilityUpgradeCapture"/>).
    ///
    /// <para>The prefix only opens the downgrade scope; it never refuses the call (no committed
    /// downgrade reservation exists). A finalizer (not a postfix) closes the scope, so an
    /// exception inside stock cannot leave it open; at level 0 or when the player cannot afford
    /// it stock returns early with no debit and no level change, and the scope closes empty.</para>
    /// </summary>
    [HarmonyPatch(typeof(SpaceCenterBuilding), "DowngradeFacility", new[] { typeof(bool) })]
    internal static class FacilityDowngradeSpendPatch
    {
        static void Prefix(SpaceCenterBuilding __instance, bool deduceFunds)
        {
            if (ParsekGameModeGate.CheckInert("FacilityDowngradeSpendPatch.Prefix")) return; // S9 game-mode gate
            if (__instance == null) return;

            string facility;
            bool fundsCharged;
            double fundsAtOpen = double.NaN;
            try
            {
                facility = __instance.Facility != null ? __instance.Facility.id : __instance.facilityName;
                fundsCharged = deduceFunds && Funding.Instance != null;
                if (fundsCharged)
                    fundsAtOpen = Funding.Instance.Funds;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateRecorder",
                    $"FacilityDowngradeSpendPatch prefix failed ({ex.GetType().Name}: {ex.Message}) - " +
                    "the downgrade of this call stays informational, with no ledger row");
                return;
            }

            FacilityUpgradeCapture.BeginScope(facility, fundsCharged, fundsAtOpen, downgrade: true);
        }

        static Exception Finalizer(Exception __exception)
        {
            FacilityUpgradeCapture.EndScope(__exception == null ? "downgrade-returned" : "downgrade-threw");
            return __exception;
        }
    }
}
