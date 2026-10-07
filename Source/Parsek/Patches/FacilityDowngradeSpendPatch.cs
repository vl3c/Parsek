using System;
using System.Collections.Generic;
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
    /// <para>The prefix first runs the Rebuild block (<see cref="FacilityRebuildBlock"/>, owner
    /// ruling 2026-10-07): a refused Rebuild skips the original (no debit, no free repair, no
    /// level change) and opens no scope. Only an allowed Rebuild opens the downgrade scope. The
    /// block and the scope live in this one prefix, as in <see cref="FacilityUpgradeSpendPatch"/>
    /// and <see cref="FacilityRepairScopePatch"/>, so their order on this method is fixed, not
    /// Harmony's. The prefix tells the finalizer through <c>__state</c> whether it opened the
    /// scope, so the finalizer never closes a scope this call did not open. A finalizer (not a
    /// postfix) closes it, so an exception inside stock cannot leave it open; at level 0 or when
    /// the player cannot afford it stock returns early with no debit and no level change, and
    /// the scope closes empty.</para>
    /// </summary>
    [HarmonyPatch(typeof(SpaceCenterBuilding), "DowngradeFacility", new[] { typeof(bool) })]
    internal static class FacilityDowngradeSpendPatch
    {
        static bool Prefix(SpaceCenterBuilding __instance, bool deduceFunds, out bool __state)
        {
            __state = false;
            if (ParsekGameModeGate.CheckInert("FacilityDowngradeSpendPatch.Prefix")) return true; // S9 game-mode gate
            if (__instance == null) return true;

            string facility;
            List<FacilityRepairCapture.BuildingRepairInput> buildings;
            bool fundsCharged;
            double fundsAtOpen = double.NaN;
            try
            {
                facility = __instance.Facility != null ? __instance.Facility.id : __instance.facilityName;
                buildings = FacilityRepairCapturePatchHelpers.ReadBuildings(__instance);
                fundsCharged = deduceFunds && Funding.Instance != null;
                if (fundsCharged)
                    fundsAtOpen = Funding.Instance.Funds;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("GameStateRecorder",
                    $"FacilityDowngradeSpendPatch prefix failed ({ex.GetType().Name}: {ex.Message}) - " +
                    "the rebuild of this call is not checked and stays informational, with no ledger row");
                return true;
            }

            return BlockOrOpenScope(facility, buildings, fundsCharged, fundsAtOpen, out __state);
        }

        /// <summary>
        /// The prefix's decision over values already read from the live building: the Rebuild
        /// block first (a refused Rebuild returns false, so stock's method is skipped, and opens
        /// no scope), else the downgrade scope is opened and the original runs.
        /// <paramref name="scopeOpened"/> is what the prefix hands its finalizer.
        /// </summary>
        internal static bool BlockOrOpenScope(string facility,
            IList<FacilityRepairCapture.BuildingRepairInput> buildings,
            bool fundsCharged, double fundsAtOpen, out bool scopeOpened)
        {
            scopeOpened = false;
            bool blocked = false;
            try
            {
                blocked = FacilityRebuildBlock.TryBlockFacilityRebuildFor(facility, buildings);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("FacilityRebuildPatch",
                    $"Facility rebuild block check failed ({ex.GetType().Name}: {ex.Message}) - rebuild allowed");
            }

            if (blocked)
            {
                ParsekLog.Verbose("GameStateRecorder",
                    $"FacilityDowngrade scope not opened: facility='{facility ?? "(null)"}' - " +
                    "the rebuild block refused the call (no debit, no repair, no level change)");
                return false;
            }

            FacilityUpgradeCapture.BeginScope(facility, fundsCharged, fundsAtOpen, downgrade: true);
            scopeOpened = true;
            return true;
        }

        /// <summary>The finalizer's body: closes the downgrade scope only when this call's
        /// prefix opened it.</summary>
        internal static void CloseScopeIfOpened(bool scopeOpened, Exception exception)
        {
            if (!scopeOpened) return;
            FacilityUpgradeCapture.EndScope(exception == null ? "downgrade-returned" : "downgrade-threw");
        }

        static Exception Finalizer(Exception __exception, bool __state)
        {
            CloseScopeIfOpened(__state, __exception);
            return __exception;
        }
    }
}
