using System;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix + finalizer on SpaceCenterBuilding.UpgradeFacility(bool), the
    /// player-facing facility-upgrade entry point. Stock UpgradeFacility deducts funds
    /// (Funding.Instance.AddFunds(-GetUpgradeCost(), StructureConstruction)) and ONLY THEN
    /// calls Facility.SetLevel(level + 1), so the old SetLevel-only block fired AFTER the
    /// deduction and silently consumed funds with no upgrade delivered (BUG-G, Bug 2,
    /// facility equivalent). Gating here - BEFORE the deduction - makes a committed-upgrade
    /// block non-destructive by construction: returning false skips the original method,
    /// so no funds are deducted and the facility level is unchanged.
    ///
    /// <para>An allowed upgrade opens the <see cref="FacilityUpgradeCapture"/> scope, which
    /// reads stock's StructureConstruction debit and hands it to the FacilityUpgraded event
    /// that SetLevel raises inside the call. The block and the scope live in one prefix so
    /// their order on this method is fixed, not Harmony's; a refused upgrade opens no scope.
    /// A finalizer (not a postfix) closes the scope, so an exception inside stock cannot
    /// leave it open, and it runs as a no-op after a refusal.</para>
    /// </summary>
    [HarmonyPatch(typeof(SpaceCenterBuilding), "UpgradeFacility", new[] { typeof(bool) })]
    internal static class FacilityUpgradeSpendPatch
    {
        static bool Prefix(SpaceCenterBuilding __instance, bool deduceFunds)
        {
            if (ParsekGameModeGate.CheckInert("FacilityUpgradeSpendPatch.Prefix")) return true; // S9 game-mode gate
            if (__instance == null) return true;

            bool blocked = FacilityUpgradePatch.TryBlockFacilityUpgrade(__instance.Facility);

            string facility = null;
            bool fundsCharged = false;
            double fundsAtOpen = double.NaN;
            if (!blocked)
            {
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
                        $"FacilityUpgradeSpendPatch prefix failed ({ex.GetType().Name}: {ex.Message}) - " +
                        "the upgrade row of this call carries cost 0");
                    return true;
                }
            }

            return FacilityUpgradeCapture.AllowAndOpenScope(blocked, facility, fundsCharged, fundsAtOpen);
        }

        static Exception Finalizer(Exception __exception)
        {
            FacilityUpgradeCapture.EndScope(__exception == null ? "upgrade-returned" : "upgrade-threw");
            return __exception;
        }
    }
}
