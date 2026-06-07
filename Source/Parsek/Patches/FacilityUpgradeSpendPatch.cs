using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix on SpaceCenterBuilding.UpgradeFacility(bool), the player-facing
    /// facility-upgrade entry point. Stock UpgradeFacility deducts funds
    /// (Funding.Instance.AddFunds(-GetUpgradeCost(), StructureConstruction)) and ONLY THEN
    /// calls Facility.SetLevel(level + 1), so the old SetLevel-only block fired AFTER the
    /// deduction and silently consumed funds with no upgrade delivered (BUG-G, Bug 2,
    /// facility equivalent). Gating here — BEFORE the deduction — makes a committed-upgrade
    /// block non-destructive by construction: returning false skips the original method,
    /// so no funds are deducted and the facility level is unchanged.
    /// </summary>
    [HarmonyPatch(typeof(SpaceCenterBuilding), "UpgradeFacility", new[] { typeof(bool) })]
    internal static class FacilityUpgradeSpendPatch
    {
        static bool Prefix(SpaceCenterBuilding __instance)
        {
            if (__instance == null) return true;

            return !FacilityUpgradePatch.TryBlockFacilityUpgrade(__instance.Facility);
        }
    }
}
