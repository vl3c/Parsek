using System;
using System.Collections.Generic;
using HarmonyLib;

namespace Parsek.Patches
{
    /// <summary>
    /// Harmony prefix + finalizer on SpaceCenterBuilding.UpgradeFacility(bool), the
    /// player-facing facility-upgrade entry point. Stock UpgradeFacility deducts funds
    /// (Funding.Instance.AddFunds(-GetUpgradeCost(), StructureConstruction)), runs the free
    /// <c>ResetStructures</c> repair of every destroyed building, and ONLY THEN calls
    /// Facility.SetLevel(level + 1), so the old SetLevel-only block fired AFTER the
    /// deduction and silently consumed funds with no upgrade delivered (BUG-G, Bug 2,
    /// facility equivalent). Gating here - BEFORE the deduction - makes a committed-upgrade
    /// block non-destructive by construction: returning false skips the original method,
    /// so no funds are deducted, nothing is repaired and the facility level is unchanged.
    ///
    /// <para>The block reads the facility's buildings as stock holds them now: besides a
    /// committed later level change, a committed later repair that covers the current
    /// destruction holds the upgrade, because its free <c>ResetStructures</c> would repair
    /// that destruction now and rewrite the recorded repair (owner principle, 2026-10-07,
    /// FACILITY-UPGRADE-FREE-REPAIR-REWRITES-COMMITTED-REPAIR). The <c>SetLevel</c> backstop
    /// (<see cref="FacilityUpgradePatch"/>) runs no structure reset, so it reads level
    /// changes only.</para>
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

            string facilityId = __instance.Facility != null ? __instance.Facility.id : null;
            List<FacilityRepairCapture.BuildingRepairInput> buildings;
            string facility;
            bool fundsCharged;
            double fundsAtOpen = double.NaN;
            try
            {
                buildings = FacilityRepairCapturePatchHelpers.ReadBuildings(__instance);
                facility = facilityId ?? __instance.facilityName;
                fundsCharged = deduceFunds && Funding.Instance != null;
                if (fundsCharged)
                    fundsAtOpen = Funding.Instance.Funds;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateRecorder",
                    $"FacilityUpgradeSpendPatch prefix failed ({ex.GetType().Name}: {ex.Message}) - " +
                    "the upgrade is checked against committed level changes only, and its row carries cost 0");
                return !FacilityUpgradePatch.TryBlockFacilityUpgradeFor(facilityId, null);
            }

            return BlockOrOpenScope(facilityId, buildings, facility, fundsCharged, fundsAtOpen);
        }

        /// <summary>
        /// The prefix's decision over values already read from the live building: the
        /// committed-upgrade block over the facility's level changes and its buildings
        /// (<see cref="FacilityUpgradePatch.TryBlockFacilityUpgradeFor"/>) first; a refused
        /// upgrade returns false (stock's method skipped) and opens no scope, an allowed one
        /// opens the upgrade scope for <paramref name="scopeFacility"/> and returns true.
        /// </summary>
        internal static bool BlockOrOpenScope(string facilityId,
            IList<FacilityRepairCapture.BuildingRepairInput> buildings,
            string scopeFacility, bool fundsCharged, double fundsAtOpen)
        {
            bool blocked = FacilityUpgradePatch.TryBlockFacilityUpgradeFor(facilityId, buildings);
            return FacilityUpgradeCapture.AllowAndOpenScope(blocked, scopeFacility, fundsCharged, fundsAtOpen);
        }

        static Exception Finalizer(Exception __exception)
        {
            FacilityUpgradeCapture.EndScope(__exception == null ? "upgrade-returned" : "upgrade-threw");
            return __exception;
        }
    }
}
